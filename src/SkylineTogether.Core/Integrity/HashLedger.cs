using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// The client's hash ledger (docs/history/ARCHITECTURE-original.md §8.3): the member hash of the last <b>received</b> value per (slot, type), whether it
    /// was applied, parked for a missing reference or held for a pending proposal, plus the same bucket sums the host
    /// keeps (<see cref="BucketSums"/>). It proves "received = sent"; the rolling verifier proves "live = received".
    /// Because parked and held values are recorded too, pending pairs never cause a beacon mismatch (§8.4).
    /// <list type="bullet">
    /// <item><see cref="Record(NetSlot, TypeRank, uint)"/> stores a pair hash (computed with <see cref="MemberHasher"/>)
    /// and moves the pair's member (<see cref="MemberHasher.Member"/>) in the sums. A hash of 0 removes the pair, which is
    /// how a caller holding only <see cref="IHashLedger"/> applies a signature change that removed a type
    /// (member hashes are never 0).</item>
    /// <item><see cref="OnMemberChanged"/> tracks members that are not pair values (the existence member of a slot,
    /// <see cref="MemberHasher.ExistenceMember"/>; K-class keyed members bucketed by owner slot). They are stored per
    /// slot, so <see cref="RemoveSlot"/> removes them and slot digests include them. Removing a member that is not stored
    /// changes nothing and is counted in <see cref="Inconsistencies"/>, so the sums always equal the stored entries.</item>
    /// <item>The class a pair is counted in comes from the pair-class resolver when one is given (the policy table's
    /// class of the type on the slot's family: V types on S entities belong to V buckets, docs/history/ARCHITECTURE-original.md §4.11.6), else from the
    /// slot's class; <see cref="Record(NetSlot, TypeRank, ReplicationClass, uint)"/> takes it explicitly. The host's hash
    /// shadow must use the same assignment.</item>
    /// <item>When <see cref="Epoch"/> is set before a plan's values are recorded, every change stamps its slot and bucket
    /// with it (<see cref="SlotModifiedEpoch"/>, <see cref="BucketModifiedEpoch"/>), so the repair coordinator can ignore
    /// beacons and detail entries that are older than a local change.</item>
    /// </list>
    /// Storage: about 8 bytes per pair and 16 per extra member (plus power-of-two rounding) in a segmented arena, 4 bytes
    /// per slot of the occupied 8192-slot pages, and the bucket arrays (≈ 12 MB for 1 M slots and 3 M pairs [E]).
    /// Threading: main thread only (client apply, rolling verification, repair).
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class HashLedger : IHashLedgerDetail
    {
        private const int PageShift = 13;
        private const int PageSize = 1 << PageShift;
        private const int PageMask = PageSize - 1;
        private const int PageCount = (int)(NetSlot.MaxValue >> PageShift) + 1;
        private const ulong ExtraFlag = 1UL << 63;
        private const int WarningsBeforeSampling = 20;

        private readonly Func<NetSlot, ReplicationClass> _classOf;
        private readonly Func<NetSlot, TypeRank, ReplicationClass> _pairClassOf;
        private readonly BucketSums _sums = new BucketSums();
        private readonly LedgerArena _arena = new LedgerArena();
        private readonly uint[][] _bucketEpochs = new uint[IntegrityRules.ClassCount][];
        private int[][] _heads = new int[PageCount][];            // per slot: block handle + 1 (0 = nothing recorded)
        private uint[][] _slotEpochs = new uint[PageCount][];
        private int _headPages;
        private int _epochPages;
        private int _slots;
        private int _pairs;
        private int _extras;
        private long _rejected;
        private long _inconsistencies;
        private int _warnings;
        private EpochId _epoch;

        /// <summary>A ledger that counts every pair in the class of its slot.</summary>
        public HashLedger(Func<NetSlot, ReplicationClass> classOf)
        {
            _classOf = classOf ?? throw new ArgumentNullException(nameof(classOf));
        }

        /// <summary>
        /// A ledger that counts each pair in the class <paramref name="pairClassOf"/> returns for it (the policy table's
        /// class of the type on the slot's family); <paramref name="classOf"/> remains the slot's own class.
        /// </summary>
        public HashLedger(Func<NetSlot, ReplicationClass> classOf, Func<NetSlot, TypeRank, ReplicationClass> pairClassOf)
            : this(classOf)
        {
            _pairClassOf = pairClassOf ?? throw new ArgumentNullException(nameof(pairClassOf));
        }

        /// <summary>Optional log for rejected updates and caller inconsistencies (sampled after the first 20).</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public ILog Log { get; set; }

        /// <summary>
        /// The epoch whose records are being recorded (set it to the plan's last epoch before recording an applied plan).
        /// <see cref="EpochId.None"/> disables the modification stamps.
        /// </summary>
        public EpochId Epoch
        {
            get => _epoch;
            set => _epoch = value;
        }

        /// <summary>Slots with at least one recorded pair or extra member.</summary>
        public int SlotCount => _slots;

        /// <summary>Recorded (slot, type) pairs.</summary>
        public int PairCount => _pairs;

        /// <summary>Stored members that are not pair values (existence, keyed state).</summary>
        public int ExtraMemberCount => _extras;

        /// <summary>Updates ignored: slot 0, undefined class, an unresolvable class, or a slot with more than 65536 entries.</summary>
        public long RejectedUpdates => _rejected + _sums.RejectedUpdates;

        /// <summary>Removals of extra members that were not stored (a caller's bookkeeping differs from the ledger's).</summary>
        public long Inconsistencies => _inconsistencies + _sums.Inconsistencies;

        /// <summary>Bytes held by the ledger (arena, slot pages, epoch stamps, bucket sums).</summary>
        public long MemoryBytes
        {
            get
            {
                long bytes = _arena.MemoryBytes + _sums.MemoryBytes;
                bytes += (long)_headPages * PageSize * sizeof(int) + (long)_epochPages * PageSize * sizeof(uint);
                bytes += 2L * PageCount * IntPtr.Size;
                for (int c = 0; c < IntegrityRules.ClassCount; c++)
                {
                    if (_bucketEpochs[c] != null) bytes += (long)IntegrityRules.MaxBuckets * sizeof(uint);
                }
                return bytes;
            }
        }

        // ------------------------------------------------------------------ IHashLedger

        /// <summary>
        /// Records the member hash of the last received value of (slot, type) in the pair's resolved class. A hash of 0
        /// removes the pair (a type the slot no longer has).
        /// </summary>
        public void Record(NetSlot slot, TypeRank type, uint hash)
        {
            if (hash == 0)
            {
                RemovePair(slot, type);
                return;
            }
            if (slot.IsNone)
            {
                Reject("Record: slot 0 (type " + type + ")");
                return;
            }
            if (!TryResolveClass(slot, type, out ReplicationClass cls)) return;
            RecordCore(slot, type, cls, hash);
        }

        public bool TryGet(NetSlot slot, TypeRank type, out uint hash)
        {
            hash = 0;
            int handle = GetHandle(slot);
            if (handle < 0) return false;
            int index = FindPair(handle, _arena.Used(handle), type.Value);
            if (index < 0) return false;
            hash = (uint)_arena.Get(handle, index);
            return true;
        }

        /// <summary>Removes every pair and extra member of the slot (a despawn or an unbind) from the ledger and the sums.</summary>
        public void RemoveSlot(NetSlot slot)
        {
            int handle = GetHandle(slot);
            if (handle < 0) return;
            int used = _arena.Used(handle);
            int i = 0;
            while (i < used)
            {
                ulong entry = _arena.Get(handle, i);
                ReplicationClass cls = ClassOfEntry(entry);
                if ((entry & ExtraFlag) != 0)
                {
                    _sums.OnMemberChanged(cls, slot, _arena.Get(handle, i + 1), 0);
                    _extras--;
                    i += 2;
                }
                else
                {
                    _sums.OnMemberChanged(cls, slot, MemberHasher.Member(slot, TypeOfEntry(entry), (uint)entry), 0);
                    _pairs--;
                    i++;
                }
                Stamp(slot, cls);
            }
            _arena.Free(handle);
            SetHandle(slot, -1);
            _slots--;
        }

        /// <summary>Σ <c>Mix64(member)</c> of the bucket, as the host's beacons carry it.</summary>
        public ulong BucketSum(ReplicationClass cls, int bucket) => _sums.Sum(cls, bucket);

        /// <summary>Present members in the bucket, as the host's beacons carry it.</summary>
        public int BucketCount(ReplicationClass cls, int bucket) => _sums.Count(cls, bucket);

        /// <summary>
        /// Moves an extra member (not a pair value) of the slot: removes the stored member <paramref name="oldMember"/> of
        /// the class (if present) and stores <paramref name="newMember"/>; 0 means "absent" on either side.
        /// </summary>
        public void OnMemberChanged(ReplicationClass cls, NetSlot slot, ulong oldMember, ulong newMember)
        {
            if (oldMember == newMember) return;
            if (slot.IsNone || !IntegrityRules.IsValidClass(cls))
            {
                Reject("OnMemberChanged: slot " + slot + ", class " + (int)cls);
                return;
            }
            int handle = GetHandle(slot);
            if (oldMember != 0)
            {
                int index = handle < 0 ? -1 : FindExtra(handle, cls, oldMember);
                if (index >= 0)
                {
                    _arena.RemoveRange(handle, index, 2);
                    _extras--;
                    _sums.OnMemberChanged(cls, slot, oldMember, 0);
                    Stamp(slot, cls);
                    if (newMember == 0 && _arena.Used(handle) == 0)
                    {
                        _arena.Free(handle);
                        SetHandle(slot, -1);
                        _slots--;
                        return;
                    }
                }
                else
                {
                    _inconsistencies++;
                    Warn("OnMemberChanged: " + slot + " has no stored " + cls + " member to replace; only the new member is applied");
                }
            }
            if (newMember == 0) return;
            if (handle < 0)
            {
                handle = _arena.Allocate(2);
                SetHandle(slot, handle);
                _slots++;
            }
            else
            {
                handle = EnsureRoom(slot, handle, 2);
                if (handle < 0) return;
            }
            int used = _arena.Used(handle);
            _arena.Set(handle, used, ExtraFlag | ((ulong)(byte)cls << 48));
            _arena.Set(handle, used + 1, newMember);
            _arena.SetUsed(handle, used + 2);
            _extras++;
            _sums.OnMemberChanged(cls, slot, 0, newMember);
            Stamp(slot, cls);
        }

        // ------------------------------------------------------------------ explicit-class and removal extras

        /// <summary>Records a pair hash in an explicit class (a hash of 0 removes the pair).</summary>
        public void Record(NetSlot slot, TypeRank type, ReplicationClass cls, uint hash)
        {
            if (hash == 0)
            {
                RemovePair(slot, type);
                return;
            }
            if (slot.IsNone || !IntegrityRules.IsValidClass(cls))
            {
                Reject("Record: slot " + slot + ", type " + type + ", class " + (int)cls);
                return;
            }
            RecordCore(slot, type, cls, hash);
        }

        /// <summary>Removes one pair (a type the slot no longer has); false when it was not recorded.</summary>
        public bool RemovePair(NetSlot slot, TypeRank type)
        {
            int handle = GetHandle(slot);
            if (handle < 0) return false;
            int index = FindPair(handle, _arena.Used(handle), type.Value);
            if (index < 0) return false;
            ulong entry = _arena.Get(handle, index);
            ReplicationClass cls = ClassOfEntry(entry);
            _sums.OnMemberChanged(cls, slot, MemberHasher.Member(slot, type, (uint)entry), 0);
            _arena.RemoveRange(handle, index, 1);
            _pairs--;
            Stamp(slot, cls);
            if (_arena.Used(handle) == 0)
            {
                _arena.Free(handle);
                SetHandle(slot, -1);
                _slots--;
            }
            return true;
        }

        /// <summary>Forgets everything (snapshot resync, session end) and releases the storage; <see cref="Epoch"/> is kept.</summary>
        public void Clear()
        {
            _arena.Clear();
            _sums.Clear();
            _heads = new int[PageCount][];
            _slotEpochs = new uint[PageCount][];
            for (int c = 0; c < IntegrityRules.ClassCount; c++) _bucketEpochs[c] = null;
            _headPages = 0;
            _epochPages = 0;
            _slots = 0;
            _pairs = 0;
            _extras = 0;
            _rejected = 0;
            _inconsistencies = 0;
            _warnings = 0;
        }

        // ------------------------------------------------------------------ IHashLedgerDetail

        public int KnownBucketCount(ReplicationClass cls) => _sums.BucketCount(cls);

        public long TotalCount(ReplicationClass cls) => _sums.TotalCount(cls);

        public int CollectBucketSlots(ReplicationClass cls, int bucket, List<NetSlot> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (!IntegrityRules.IsValidClass(cls) || !IntegrityRules.IsValidBucket(bucket)) return 0;
            uint first = IntegrityRules.FirstSlotOf(bucket);
            uint last = IntegrityRules.LastSlotOf(bucket);
            int[] page = _heads[first >> PageShift];                 // a bucket (1024 slots) lies inside one page (8192)
            if (page == null) return 0;
            int added = 0;
            for (uint s = first; s <= last; s++)
            {
                int handle = page[s & PageMask] - 1;
                if (handle < 0 || !HasClass(handle, cls)) continue;
                output.Add(new NetSlot(s));
                added++;
            }
            return added;
        }

        public bool TryGetSlotDigest(ReplicationClass cls, NetSlot slot, out uint digest)
        {
            digest = 0;
            int handle = GetHandle(slot);
            if (handle < 0) return false;
            int used = _arena.Used(handle);
            ulong sum = 0;
            bool found = false;
            int i = 0;
            while (i < used)
            {
                ulong entry = _arena.Get(handle, i);
                bool extra = (entry & ExtraFlag) != 0;
                if (ClassOfEntry(entry) == cls)
                {
                    ulong member = extra ? _arena.Get(handle, i + 1) : MemberHasher.Member(slot, TypeOfEntry(entry), (uint)entry);
                    unchecked
                    {
                        sum += MemberHasher.Contribution(member);
                    }
                    found = true;
                }
                i += extra ? 2 : 1;
            }
            if (found) digest = MemberHasher.SlotDigest(sum);
            return found;
        }

        public bool TryGetClass(NetSlot slot, TypeRank type, out ReplicationClass cls)
        {
            cls = ReplicationClass.Excluded;
            int handle = GetHandle(slot);
            if (handle < 0) return false;
            int index = FindPair(handle, _arena.Used(handle), type.Value);
            if (index < 0) return false;
            cls = ClassOfEntry(_arena.Get(handle, index));
            return true;
        }

        public EpochId SlotModifiedEpoch(NetSlot slot)
        {
            if (slot.IsNone) return EpochId.None;
            uint[] page = _slotEpochs[slot.Value >> PageShift];
            return page == null ? EpochId.None : new EpochId(page[slot.Value & PageMask]);
        }

        public EpochId BucketModifiedEpoch(ReplicationClass cls, int bucket)
        {
            if (!IntegrityRules.IsValidClass(cls) || !IntegrityRules.IsValidBucket(bucket)) return EpochId.None;
            uint[] buckets = _bucketEpochs[(int)cls];
            return buckets == null ? EpochId.None : new EpochId(buckets[bucket]);
        }

        // ------------------------------------------------------------------ diagnostics

        /// <summary>Appends the recorded pairs of the slot (replica-diff dumps, docs/history/ARCHITECTURE-original.md §13.5); returns how many.</summary>
        public int CollectPairs(NetSlot slot, List<(TypeRank type, uint hash, ReplicationClass cls)> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            int handle = GetHandle(slot);
            if (handle < 0) return 0;
            int used = _arena.Used(handle);
            int added = 0;
            int i = 0;
            while (i < used)
            {
                ulong entry = _arena.Get(handle, i);
                if ((entry & ExtraFlag) != 0)
                {
                    i += 2;
                    continue;
                }
                output.Add((TypeOfEntry(entry), (uint)entry, ClassOfEntry(entry)));
                added++;
                i++;
            }
            return added;
        }

        /// <summary>Appends the extra members of the slot (existence, keyed state); returns how many.</summary>
        public int CollectExtraMembers(NetSlot slot, List<(ReplicationClass cls, ulong member)> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            int handle = GetHandle(slot);
            if (handle < 0) return 0;
            int used = _arena.Used(handle);
            int added = 0;
            int i = 0;
            while (i < used)
            {
                ulong entry = _arena.Get(handle, i);
                if ((entry & ExtraFlag) != 0)
                {
                    output.Add((ClassOfEntry(entry), _arena.Get(handle, i + 1)));
                    added++;
                    i += 2;
                }
                else
                {
                    i++;
                }
            }
            return added;
        }

        // ------------------------------------------------------------------ internals

        private void RecordCore(NetSlot slot, TypeRank type, ReplicationClass cls, uint hash)
        {
            int handle = GetHandle(slot);
            if (handle >= 0)
            {
                int used = _arena.Used(handle);
                int index = FindPair(handle, used, type.Value);
                if (index >= 0)
                {
                    ulong entry = _arena.Get(handle, index);
                    uint oldHash = (uint)entry;
                    ReplicationClass oldCls = ClassOfEntry(entry);
                    if (oldHash == hash && oldCls == cls) return;
                    ulong oldMember = MemberHasher.Member(slot, type, oldHash);
                    ulong newMember = MemberHasher.Member(slot, type, hash);
                    if (oldCls == cls)
                    {
                        _sums.OnMemberChanged(cls, slot, oldMember, newMember);
                    }
                    else
                    {
                        _sums.OnMemberChanged(oldCls, slot, oldMember, 0);
                        _sums.OnMemberChanged(cls, slot, 0, newMember);
                        Stamp(slot, oldCls);
                    }
                    _arena.Set(handle, index, PairEntry(type, cls, hash));
                    Stamp(slot, cls);
                    return;
                }
                handle = EnsureRoom(slot, handle, 1);
                if (handle < 0) return;
            }
            else
            {
                handle = _arena.Allocate(2);
                SetHandle(slot, handle);
                _slots++;
            }
            int at = _arena.Used(handle);
            _arena.Set(handle, at, PairEntry(type, cls, hash));
            _arena.SetUsed(handle, at + 1);
            _pairs++;
            _sums.OnMemberChanged(cls, slot, 0, MemberHasher.Member(slot, type, hash));
            Stamp(slot, cls);
        }

        private bool TryResolveClass(NetSlot slot, TypeRank type, out ReplicationClass cls)
        {
            try
            {
                cls = _pairClassOf != null ? _pairClassOf(slot, type) : _classOf(slot);
            }
            catch (Exception ex)
            {
                cls = ReplicationClass.Excluded;
                _rejected++;
                _warnings++;
                if (_warnings <= WarningsBeforeSampling) Log.Warn(LogCategory.Integrity, "ledger: the class resolver failed for " + slot + " " + type + "; the value is not recorded", ex);
                return false;
            }
            if (!IntegrityRules.IsValidClass(cls))
            {
                Reject("the class resolver returned the undefined class " + (int)cls + " for " + slot + " " + type);
                return false;
            }
            return true;
        }

        /// <summary>Makes room for <paramref name="needed"/> more entries; returns the (possibly moved) handle, or -1 when the block is full.</summary>
        private int EnsureRoom(NetSlot slot, int handle, int needed)
        {
            int used = _arena.Used(handle);
            if (used + needed <= _arena.Capacity(handle)) return handle;
            if (used + needed > LedgerArena.MaxEntriesPerBlock)
            {
                Reject("more than " + LedgerArena.MaxEntriesPerBlock + " ledger entries for " + slot);
                return -1;
            }
            int grown = _arena.Grow(handle, used + needed);
            SetHandle(slot, grown);
            return grown;
        }

        private int FindPair(int handle, int used, ushort type)
        {
            int i = 0;
            while (i < used)
            {
                ulong entry = _arena.Get(handle, i);
                if ((entry & ExtraFlag) != 0)
                {
                    i += 2;
                    continue;
                }
                if (TypeOfEntry(entry).Value == type) return i;
                i++;
            }
            return -1;
        }

        private int FindExtra(int handle, ReplicationClass cls, ulong member)
        {
            int used = _arena.Used(handle);
            int i = 0;
            while (i < used)
            {
                ulong entry = _arena.Get(handle, i);
                if ((entry & ExtraFlag) != 0)
                {
                    if (ClassOfEntry(entry) == cls && _arena.Get(handle, i + 1) == member) return i;
                    i += 2;
                }
                else
                {
                    i++;
                }
            }
            return -1;
        }

        private bool HasClass(int handle, ReplicationClass cls)
        {
            int used = _arena.Used(handle);
            int i = 0;
            while (i < used)
            {
                ulong entry = _arena.Get(handle, i);
                if (ClassOfEntry(entry) == cls) return true;
                i += (entry & ExtraFlag) != 0 ? 2 : 1;
            }
            return false;
        }

        private int GetHandle(NetSlot slot)
        {
            if (slot.IsNone) return -1;
            int[] page = _heads[slot.Value >> PageShift];
            return page == null ? -1 : page[slot.Value & PageMask] - 1;
        }

        private void SetHandle(NetSlot slot, int handle)
        {
            int p = (int)(slot.Value >> PageShift);
            int[] page = _heads[p];
            if (page == null)
            {
                if (handle < 0) return;
                page = new int[PageSize];
                _heads[p] = page;
                _headPages++;
            }
            page[slot.Value & PageMask] = handle + 1;
        }

        private void Stamp(NetSlot slot, ReplicationClass cls)
        {
            uint epoch = _epoch.Value;
            if (epoch == 0) return;
            int p = (int)(slot.Value >> PageShift);
            uint[] page = _slotEpochs[p];
            if (page == null)
            {
                page = new uint[PageSize];
                _slotEpochs[p] = page;
                _epochPages++;
            }
            page[slot.Value & PageMask] = epoch;
            uint[] buckets = _bucketEpochs[(int)cls];
            if (buckets == null)
            {
                buckets = new uint[IntegrityRules.MaxBuckets];
                _bucketEpochs[(int)cls] = buckets;
            }
            buckets[slot.Bucket] = epoch;
        }

        private void Reject(string what)
        {
            _rejected++;
            Warn("ledger update rejected: " + what);
        }

        private void Warn(string message)
        {
            _warnings++;
            if (_warnings <= WarningsBeforeSampling || _warnings % 1000 == 0)
            {
                Log.Warn(LogCategory.Integrity, message + (_warnings == WarningsBeforeSampling ? " (further ledger warnings are sampled)" : string.Empty));
            }
        }

        private static ulong PairEntry(TypeRank type, ReplicationClass cls, uint hash) => ((ulong)(byte)cls << 48) | ((ulong)type.Value << 32) | hash;

        private static ReplicationClass ClassOfEntry(ulong entry) => (ReplicationClass)(byte)(entry >> 48);

        private static TypeRank TypeOfEntry(ulong entry) => new TypeRank((ushort)(entry >> 32));
    }
}
