using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Replication
{
    /// <summary>
    /// The host's hash shadow (ARCH §4.5.1): per (slot, type) the 32-bit raw hash (change-detection prefilter over the
    /// chunk bytes) and the 32-bit canonical value hash of the last captured value (for S and G of the last
    /// <i>emitted</i> value, ARCH §4.4.5), a presence bit, the replicated enabled bit and, for types with an authorable
    /// mask, the hash of the authorable projection (ARCH §3.8); per slot its existence member. Every change of a member
    /// updates the bucket sums in O(1) through <see cref="IBucketSink"/> (ARCH §8.2), with members from
    /// <see cref="MemberHash"/>.
    /// <para>
    /// <b>Classes.</b> A pair is summed into the bucket of its own class (S for source types, V for simulation types on
    /// structure entities, ARCH §4.2.2): the per-pair resolver given to the constructor decides, defaulting to the
    /// catalog's class override or else the slot's class. The existence member uses the slot's class. Pairs of classes
    /// that are not hashed (OnDemand, excluded, keyed, channels) are stored for change detection but are no members. The
    /// class of a slot must not change while it holds pairs: callers remove the slot (<see cref="RemoveSlot"/>) before
    /// re-binding it to another family.
    /// </para>
    /// <para>
    /// <b>Storage</b>: per type, pages of 4096 slots in unmanaged memory, allocated on first use (8 bytes per pair plus
    /// two bits); per slot an existence page. About 8 bytes per replicated pair, as ARCH §4.5.1 estimates.
    /// </para>
    /// <para>
    /// <b>Threading</b>: mutated only on the main thread inside the capture window; read by workers outside it
    /// (ARCH §4.5.5). No locks.
    /// </para>
    /// </summary>
    public sealed unsafe class HashShadow : IHashShadowView, IDisposable
    {
        private const int PageShift = 12;
        private const int PageSize = 1 << PageShift;
        private const int PageMask = PageSize - 1;
        private const int BitWords = PageSize / 64;
        private const int CanonicalOffset = PageSize * 4;
        private const int PresenceOffset = PageSize * 8;
        private const int DisabledOffset = PresenceOffset + BitWords * 8;
        private const int PageBytes = DisabledOffset + BitWords * 8;
        private const int ProjectionPresenceOffset = PageSize * 4;
        private const int ProjectionPageBytes = ProjectionPresenceOffset + BitWords * 8;
        private const int ExistenceClassOffset = PageSize * 8;
        private const int ExistencePageBytes = ExistenceClassOffset + PageSize;
        private const int SlotPageLimit = (int)((NetSlot.MaxValue + 1) >> PageShift);

        private readonly ITypeCatalog _catalog;
        private readonly IBucketSink _buckets;
        private readonly Func<NetSlot, ReplicationClass> _classOf;
        private readonly Func<NetSlot, TypeRank, ReplicationClass> _pairClassOf;
        private readonly int _typeCount;
        private readonly bool[] _enableBit;
        private readonly IntPtr[][] _pages;
        private readonly IntPtr[][] _projectionPages;
        private readonly bool[] _typeActive;
        private readonly List<int> _activeTypes = new List<int>();
        private IntPtr[] _existencePages = new IntPtr[16];
        private long _pageCount;
        private long _projectionPageCount;
        private long _existencePageCount;
        private int _pairCount;
        private int _existenceCount;
        private bool _disposed;

        /// <param name="catalog">The session's type catalog (type count, enableable facts, class overrides).</param>
        /// <param name="buckets">Receives member changes for the bucket sums; may be null (no sums).</param>
        /// <param name="classOf">The class of a slot (its existence member, and of its pairs when the catalog has no override).</param>
        public HashShadow(ITypeCatalog catalog, IBucketSink buckets, Func<NetSlot, ReplicationClass> classOf)
            : this(catalog, buckets, classOf, null)
        {
        }

        /// <param name="catalog">The session's type catalog.</param>
        /// <param name="buckets">Receives member changes for the bucket sums; may be null (no sums).</param>
        /// <param name="classOf">The class of a slot (its existence member).</param>
        /// <param name="pairClassOf">The class of a (slot, type) pair (the host engine passes <c>PolicyTable.ClassOf</c> of the
        /// slot's family); null uses the catalog's class override, else <paramref name="classOf"/>.</param>
        public HashShadow(ITypeCatalog catalog, IBucketSink buckets, Func<NetSlot, ReplicationClass> classOf,
                          Func<NetSlot, TypeRank, ReplicationClass> pairClassOf)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _classOf = classOf ?? throw new ArgumentNullException(nameof(classOf));
            _buckets = buckets;
            _pairClassOf = pairClassOf ?? DefaultPairClass;
            _typeCount = catalog.Count;
            _enableBit = new bool[_typeCount];
            for (int i = 0; i < _typeCount; i++)
            {
                TypeFlags flags = catalog[new TypeRank((ushort)i)].Flags;
                _enableBit[i] = (flags & TypeFlags.Enableable) != 0 && (flags & TypeFlags.SerializeAsEnabled) == 0;
            }
            _pages = new IntPtr[_typeCount][];
            _projectionPages = new IntPtr[_typeCount][];
            _typeActive = new bool[_typeCount];
        }

        ~HashShadow()
        {
            FreeAll();
        }

        /// <summary>Pairs present in the shadow.</summary>
        public int PairCount => _pairCount;

        /// <summary>Slots with an existence member.</summary>
        public int ExistenceCount => _existenceCount;

        /// <summary>Unmanaged and directory bytes held (diagnostics, ARCH §15.2).</summary>
        public long MemoryBytes
        {
            get
            {
                long bytes = _pageCount * PageBytes + _projectionPageCount * ProjectionPageBytes + _existencePageCount * ExistencePageBytes;
                for (int i = 0; i < _activeTypes.Count; i++)
                {
                    IntPtr[] directory = _pages[_activeTypes[i]];
                    if (directory != null) bytes += directory.Length * IntPtr.Size;
                }
                return bytes + _existencePages.Length * IntPtr.Size;
            }
        }

        // ------------------------------------------------------------------ queries

        /// <summary>The stored raw and canonical hashes of a pair; false when absent.</summary>
        public bool TryGet(NetSlot slot, TypeRank type, out uint raw, out uint canonical)
        {
            byte* page = PageOf(type.Value, slot.Value);
            int i = (int)(slot.Value & PageMask);
            if (page != null && GetBit(page + PresenceOffset, i))
            {
                raw = ((uint*)page)[i];
                canonical = ((uint*)(page + CanonicalOffset))[i];
                return true;
            }
            raw = 0;
            canonical = 0;
            return false;
        }

        /// <summary>True when the pair is present.</summary>
        public bool Contains(NetSlot slot, TypeRank type)
        {
            byte* page = PageOf(type.Value, slot.Value);
            return page != null && GetBit(page + PresenceOffset, (int)(slot.Value & PageMask));
        }

        /// <summary>The stored enabled bit of a present pair (true for types without a replicated enabled bit).</summary>
        public bool TryGetEnabled(NetSlot slot, TypeRank type, out bool enabled)
        {
            byte* page = PageOf(type.Value, slot.Value);
            int i = (int)(slot.Value & PageMask);
            if (page != null && GetBit(page + PresenceOffset, i))
            {
                enabled = !GetBit(page + DisabledOffset, i);
                return true;
            }
            enabled = true;
            return false;
        }

        public bool TryGetCanonical(NetSlot slot, TypeRank type, out uint hash) => TryGet(slot, type, out _, out hash);

        /// <summary>The stored authorable-projection hash of a pair (ARCH §3.8); false when none was recorded.</summary>
        public bool TryGetProjection(NetSlot slot, TypeRank type, out uint hash)
        {
            hash = 0;
            if (type.Value >= _typeCount || slot.IsNone) return false;
            IntPtr[] directory = _projectionPages[type.Value];
            int pageIndex = (int)(slot.Value >> PageShift);
            if (directory == null || pageIndex >= directory.Length || directory[pageIndex] == IntPtr.Zero) return false;
            byte* page = (byte*)directory[pageIndex];
            int i = (int)(slot.Value & PageMask);
            if (!GetBit(page + ProjectionPresenceOffset, i)) return false;
            hash = ((uint*)page)[i];
            return true;
        }

        /// <summary>The class of a slot (its existence member).</summary>
        public ReplicationClass ClassOf(NetSlot slot) => _classOf(slot);

        /// <summary>The class a pair is summed into.</summary>
        public ReplicationClass PairClassOf(NetSlot slot, TypeRank type) => _pairClassOf(slot, type);

        // ------------------------------------------------------------------ mutation (main thread, capture window)

        /// <summary>
        /// Stores the hashes of a pair (enabled bit unchanged; a new pair counts as enabled). Returns true when the canonical
        /// hash changed or the pair is new; the bucket sums follow.
        /// </summary>
        public bool SetCanonical(NetSlot slot, TypeRank type, uint raw, uint canonical)
        {
            return SetCore(slot, type, raw, canonical, false, true);
        }

        /// <summary>Stores the hashes and the enabled bit of a pair in one step; true when the member changed or the pair is new.</summary>
        public bool SetCanonical(NetSlot slot, TypeRank type, uint raw, uint canonical, bool enabled)
        {
            return SetCore(slot, type, raw, canonical, true, enabled);
        }

        /// <summary>Updates the raw hash of a present pair (an unchanged canonical value with new chunk bytes).</summary>
        public void SetRaw(NetSlot slot, TypeRank type, uint raw)
        {
            byte* page = PageOf(type.Value, slot.Value);
            int i = (int)(slot.Value & PageMask);
            if (page != null && GetBit(page + PresenceOffset, i)) ((uint*)page)[i] = raw;
        }

        /// <summary>Sets the enabled bit of a present pair; true when it changed (the member follows for types with a replicated bit).</summary>
        public bool SetEnabled(NetSlot slot, TypeRank type, bool enabled)
        {
            byte* page = PageOf(type.Value, slot.Value);
            int i = (int)(slot.Value & PageMask);
            if (page == null || !GetBit(page + PresenceOffset, i)) return false;
            bool wasEnabled = !GetBit(page + DisabledOffset, i);
            if (wasEnabled == enabled) return false;
            uint canonical = ((uint*)(page + CanonicalOffset))[i];
            SetBit(page + DisabledOffset, i, !enabled);
            if (_enableBit[type.Value]) Report(slot, type, canonical, wasEnabled, true, canonical, enabled, true);
            return true;
        }

        /// <summary>Records the authorable-projection hash of a pair (ARCH §3.8).</summary>
        public void SetProjection(NetSlot slot, TypeRank type, uint hash)
        {
            CheckPair(slot, type);
            int pageIndex = (int)(slot.Value >> PageShift);
            IntPtr[] directory = _projectionPages[type.Value];
            if (directory == null || pageIndex >= directory.Length)
            {
                int length = Math.Min(SlotPageLimit, Math.Max(pageIndex + 1, directory == null ? 4 : directory.Length * 2));
                Array.Resize(ref directory, length);
                _projectionPages[type.Value] = directory;
            }
            if (directory[pageIndex] == IntPtr.Zero)
            {
                directory[pageIndex] = Allocate(ProjectionPageBytes);
                _projectionPageCount++;
                MarkActive(type.Value);
            }
            byte* page = (byte*)directory[pageIndex];
            int i = (int)(slot.Value & PageMask);
            ((uint*)page)[i] = hash;
            SetBit(page + ProjectionPresenceOffset, i, true);
        }

        /// <summary>Removes one pair; true when it was present.</summary>
        public bool Remove(NetSlot slot, TypeRank type)
        {
            bool removed = false;
            byte* page = PageOf(type.Value, slot.Value);
            int i = (int)(slot.Value & PageMask);
            if (page != null && GetBit(page + PresenceOffset, i))
            {
                uint canonical = ((uint*)(page + CanonicalOffset))[i];
                bool enabled = !GetBit(page + DisabledOffset, i);
                SetBit(page + PresenceOffset, i, false);
                SetBit(page + DisabledOffset, i, false);
                ((uint*)page)[i] = 0;
                ((uint*)(page + CanonicalOffset))[i] = 0;
                _pairCount--;
                Report(slot, type, canonical, enabled, true, 0, true, false);
                removed = true;
            }
            ClearProjection(slot, type.Value);
            return removed;
        }

        /// <summary>Sets the existence member of a slot (0 removes it); the bucket of the slot's class follows.</summary>
        public void SetExistence(NetSlot slot, ulong member)
        {
            if (slot.IsNone) throw new ArgumentException("NetSlot.None has no existence", nameof(slot));
            byte* page = ExistencePage(slot.Value, member != 0);
            if (page == null) return;
            int i = (int)(slot.Value & PageMask);
            ulong old = ((ulong*)page)[i];
            var oldClass = (ReplicationClass)page[ExistenceClassOffset + i];
            ReplicationClass cls = member != 0 ? _classOf(slot) : oldClass;
            if (old == member && oldClass == cls) return;
            ((ulong*)page)[i] = member;
            page[ExistenceClassOffset + i] = (byte)cls;
            if (old == 0 && member != 0) _existenceCount++;
            else if (old != 0 && member == 0) _existenceCount--;
            if (_buckets == null) return;
            if (oldClass == cls)
            {
                if (PolicyTable.IsHashedClass(cls)) _buckets.OnMemberChanged(cls, slot, old, member);
            }
            else
            {
                if (old != 0 && PolicyTable.IsHashedClass(oldClass)) _buckets.OnMemberChanged(oldClass, slot, old, 0);
                if (member != 0 && PolicyTable.IsHashedClass(cls)) _buckets.OnMemberChanged(cls, slot, 0, member);
            }
        }

        /// <summary>The existence member of a slot (0 when none).</summary>
        public ulong ExistenceOf(NetSlot slot)
        {
            byte* page = ExistencePage(slot.Value, false);
            return page == null ? 0 : ((ulong*)page)[slot.Value & PageMask];
        }

        /// <summary>Removes every pair and the existence member of a slot (despawn).</summary>
        public void RemoveSlot(NetSlot slot)
        {
            if (slot.IsNone) return;
            for (int t = 0; t < _activeTypes.Count; t++) Remove(slot, new TypeRank((ushort)_activeTypes[t]));
            if (ExistenceOf(slot) != 0) SetExistence(slot, 0);
        }

        // ------------------------------------------------------------------ bucket details (ARCH §8.5 level 1)

        /// <summary>
        /// Appends, for every slot of <paramref name="bucket"/> with members of <paramref name="cls"/>, the slot and its
        /// folded contribution <see cref="MemberHash.FoldContribution"/>, in ascending slot order.
        /// </summary>
        public void CollectBucket(ReplicationClass cls, int bucket, List<NetSlot> slots, List<uint> combinedHashes)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (combinedHashes == null) throw new ArgumentNullException(nameof(combinedHashes));
            var sums = new ulong[1 << NetSlot.BucketShift];
            var present = new bool[sums.Length];
            if (!AccumulateBucket(cls, bucket, sums, present, out _)) return;
            uint first = (uint)bucket << NetSlot.BucketShift;
            for (int i = 0; i < sums.Length; i++)
            {
                if (!present[i]) continue;
                slots.Add(new NetSlot(first + (uint)i));
                combinedHashes.Add(MemberHash.FoldContribution(sums[i]));
            }
        }

        /// <summary>The exact bucket sum (Σ Mix64(member)) and member count of a class and bucket, recomputed from the shadow.</summary>
        public ulong BucketSum(ReplicationClass cls, int bucket, out int count)
        {
            var sums = new ulong[1 << NetSlot.BucketShift];
            var present = new bool[sums.Length];
            AccumulateBucket(cls, bucket, sums, present, out count);
            ulong total = 0;
            for (int i = 0; i < sums.Length; i++) total += sums[i];
            return total;
        }

        public void Dispose()
        {
            FreeAll();
            GC.SuppressFinalize(this);
        }

        public override string ToString()
        {
            return "HashShadow(" + _pairCount.ToString(CultureInfo.InvariantCulture) + " pairs, " + _existenceCount.ToString(CultureInfo.InvariantCulture)
                   + " slots, " + (MemoryBytes >> 10).ToString(CultureInfo.InvariantCulture) + " KiB)";
        }

        // ------------------------------------------------------------------ internals

        private ReplicationClass DefaultPairClass(NetSlot slot, TypeRank type)
        {
            TypeDescriptor d = _catalog[type];
            if ((d.Flags & TypeFlags.Excluded) != 0) return ReplicationClass.Excluded;
            if ((d.Flags & TypeFlags.OnDemand) != 0) return ReplicationClass.OnDemand;
            return d.ClassOverride != ReplicationClass.Excluded ? d.ClassOverride : _classOf(slot);
        }

        private bool SetCore(NetSlot slot, TypeRank type, uint raw, uint canonical, bool setEnabled, bool enabled)
        {
            CheckPair(slot, type);
            byte* page = PageOrCreate(type.Value, slot.Value);
            int i = (int)(slot.Value & PageMask);
            bool present = GetBit(page + PresenceOffset, i);
            uint oldCanonical = ((uint*)(page + CanonicalOffset))[i];
            bool oldEnabled = !GetBit(page + DisabledOffset, i);
            bool newEnabled = setEnabled ? enabled : (present ? oldEnabled : true);
            ((uint*)page)[i] = raw;
            ((uint*)(page + CanonicalOffset))[i] = canonical;
            SetBit(page + DisabledOffset, i, !newEnabled);
            if (!present)
            {
                SetBit(page + PresenceOffset, i, true);
                _pairCount++;
            }
            bool bit = _enableBit[type.Value];
            bool changed = !present || oldCanonical != canonical || (bit && oldEnabled != newEnabled);
            if (changed) Report(slot, type, oldCanonical, oldEnabled, present, canonical, newEnabled, true);
            return changed;
        }

        private void Report(NetSlot slot, TypeRank type, uint oldCanonical, bool oldEnabled, bool oldPresent, uint newCanonical, bool newEnabled, bool newPresent)
        {
            if (_buckets == null) return;
            ReplicationClass cls = _pairClassOf(slot, type);
            if (!PolicyTable.IsHashedClass(cls)) return;
            bool bit = _enableBit[type.Value];
            ulong oldMember = oldPresent ? MemberHash.Member(slot, type, MemberHash.PairHash(oldCanonical, bit, oldEnabled)) : 0;
            ulong newMember = newPresent ? MemberHash.Member(slot, type, MemberHash.PairHash(newCanonical, bit, newEnabled)) : 0;
            if (oldMember != newMember) _buckets.OnMemberChanged(cls, slot, oldMember, newMember);
        }

        private bool AccumulateBucket(ReplicationClass cls, int bucket, ulong[] sums, bool[] present, out int count)
        {
            count = 0;
            if (bucket < 0 || bucket > (int)(NetSlot.MaxValue >> NetSlot.BucketShift)) return false;
            uint first = (uint)bucket << NetSlot.BucketShift;
            int n = sums.Length;
            int pageIndex = (int)(first >> PageShift);
            int start = (int)(first & PageMask);
            for (int t = 0; t < _activeTypes.Count; t++)
            {
                int rank = _activeTypes[t];
                IntPtr[] directory = _pages[rank];
                if (directory == null || pageIndex >= directory.Length || directory[pageIndex] == IntPtr.Zero) continue;
                byte* page = (byte*)directory[pageIndex];
                var type = new TypeRank((ushort)rank);
                bool bit = _enableBit[rank];
                for (int i = 0; i < n; i++)
                {
                    int index = start + i;
                    if (!GetBit(page + PresenceOffset, index)) continue;
                    var slot = new NetSlot(first + (uint)i);
                    if (slot.IsNone || _pairClassOf(slot, type) != cls) continue;
                    uint canonical = ((uint*)(page + CanonicalOffset))[index];
                    bool enabled = !GetBit(page + DisabledOffset, index);
                    sums[i] += HashMix.Mix64(MemberHash.Member(slot, type, MemberHash.PairHash(canonical, bit, enabled)));
                    present[i] = true;
                    count++;
                }
            }
            byte* existence = pageIndex < _existencePages.Length ? (byte*)_existencePages[pageIndex] : null;
            if (existence != null)
            {
                for (int i = 0; i < n; i++)
                {
                    int index = start + i;
                    ulong member = ((ulong*)existence)[index];
                    if (member == 0 || (ReplicationClass)existence[ExistenceClassOffset + index] != cls) continue;
                    sums[i] += HashMix.Mix64(member);
                    present[i] = true;
                    count++;
                }
            }
            return true;
        }

        private void CheckPair(NetSlot slot, TypeRank type)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HashShadow));
            if (slot.IsNone) throw new ArgumentException("NetSlot.None has no pairs", nameof(slot));
            if (type.Value >= _typeCount) throw new ArgumentOutOfRangeException(nameof(type), type, "outside the catalog");
        }

        private byte* PageOf(int rank, uint slot)
        {
            if (rank >= _typeCount || slot == 0) return null;
            IntPtr[] directory = _pages[rank];
            int pageIndex = (int)(slot >> PageShift);
            if (directory == null || pageIndex >= directory.Length) return null;
            return (byte*)directory[pageIndex];
        }

        private byte* PageOrCreate(int rank, uint slot)
        {
            IntPtr[] directory = _pages[rank];
            int pageIndex = (int)(slot >> PageShift);
            if (directory == null || pageIndex >= directory.Length)
            {
                int length = Math.Min(SlotPageLimit, Math.Max(pageIndex + 1, directory == null ? 4 : directory.Length * 2));
                Array.Resize(ref directory, length);
                _pages[rank] = directory;
            }
            if (directory[pageIndex] == IntPtr.Zero)
            {
                directory[pageIndex] = Allocate(PageBytes);
                _pageCount++;
                MarkActive(rank);
            }
            return (byte*)directory[pageIndex];
        }

        private byte* ExistencePage(uint slot, bool create)
        {
            if (slot == 0) return null;
            int pageIndex = (int)(slot >> PageShift);
            if (pageIndex >= _existencePages.Length)
            {
                if (!create) return null;
                Array.Resize(ref _existencePages, Math.Min(SlotPageLimit, Math.Max(pageIndex + 1, _existencePages.Length * 2)));
            }
            if (_existencePages[pageIndex] == IntPtr.Zero)
            {
                if (!create) return null;
                if (_disposed) throw new ObjectDisposedException(nameof(HashShadow));
                _existencePages[pageIndex] = Allocate(ExistencePageBytes);
                _existencePageCount++;
            }
            return (byte*)_existencePages[pageIndex];
        }

        private void ClearProjection(NetSlot slot, int rank)
        {
            IntPtr[] directory = _projectionPages[rank];
            int pageIndex = (int)(slot.Value >> PageShift);
            if (directory == null || pageIndex >= directory.Length || directory[pageIndex] == IntPtr.Zero) return;
            byte* page = (byte*)directory[pageIndex];
            SetBit(page + ProjectionPresenceOffset, (int)(slot.Value & PageMask), false);
        }

        private void MarkActive(int rank)
        {
            if (_typeActive[rank]) return;
            _typeActive[rank] = true;
            _activeTypes.Add(rank);
        }

        private static IntPtr Allocate(int bytes)
        {
            IntPtr pointer = Marshal.AllocHGlobal(bytes);
            NativeBuffer.Zero((byte*)pointer, bytes);
            return pointer;
        }

        private static bool GetBit(byte* bits, int index) => (((ulong*)bits)[index >> 6] & (1UL << (index & 63))) != 0;

        private static void SetBit(byte* bits, int index, bool value)
        {
            ulong* word = (ulong*)bits + (index >> 6);
            if (value) *word |= 1UL << (index & 63);
            else *word &= ~(1UL << (index & 63));
        }

        private void FreeAll()
        {
            if (_disposed) return;
            _disposed = true;
            FreeDirectories(_pages);
            FreeDirectories(_projectionPages);
            for (int i = 0; i < _existencePages.Length; i++)
            {
                if (_existencePages[i] != IntPtr.Zero) Marshal.FreeHGlobal(_existencePages[i]);
                _existencePages[i] = IntPtr.Zero;
            }
            _pageCount = 0;
            _projectionPageCount = 0;
            _existencePageCount = 0;
            _pairCount = 0;
            _existenceCount = 0;
        }

        private static void FreeDirectories(IntPtr[][] directories)
        {
            if (directories == null) return;
            for (int t = 0; t < directories.Length; t++)
            {
                IntPtr[] directory = directories[t];
                if (directory == null) continue;
                for (int i = 0; i < directory.Length; i++)
                {
                    if (directory[i] != IntPtr.Zero) Marshal.FreeHGlobal(directory[i]);
                    directory[i] = IntPtr.Zero;
                }
            }
        }
    }
}
