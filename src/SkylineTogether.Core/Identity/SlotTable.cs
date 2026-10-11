using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Identity
{
    /// <summary>
    /// The slot side tables of ARCH §3.3 in pure arrays: entity index → slot (version-checked) and slot → entity,
    /// generation, family, flags (<c>spawnEmitted</c>), prefab rank at binding, current type set and authored revision.
    /// The game layer wraps it as <c>EntityMap</c>; core-replication's engines own one each.
    /// <para>
    /// Storage is paged in blocks of 8192 indices, matching the entity allocator's data blocks (R08 §10.1:
    /// <c>EntityComponentStore</c> allocates 8192-entity blocks and reuses indices at once), so memory follows the
    /// indices actually used and a single high slot or entity index never allocates the whole range. Entries take 8
    /// bytes per entity index and 24 per slot, so 1 M slots and 1.5 M entity indices hold about 36 MB (ARCH §3.3
    /// estimates 33 MB for unpadded columns); <see cref="MemoryBytes"/> reports the actual figure.
    /// </para>
    /// <para>
    /// Rules: every lookup is O(1) and version-checked, so a destroyed entity whose index was reused never resolves
    /// to the old slot. A slot keeps its revision across <see cref="Unbind"/> and the unbind bumps it (ARCH §3.8 rule
    /// c: despawn and slot reuse are authored changes), so a proposal based on the previous occupant of a reused slot
    /// is stale. One live entity is bound to at most one slot: binding it to a second slot unbinds the first (counted
    /// in <see cref="ConflictCount"/> and logged). A binding whose entity index was reused by a different entity
    /// keeps its slot until the caller's liveness check unbinds it (host: the liveness sweep emits its DESPAWN,
    /// ARCH §4.4.6); only the index mapping moves to the new entity. Entity versions grow per index, so the reverse case
    /// (binding an older, already destroyed version of an index another slot maps) never takes the index mapping from
    /// the newer occupant (<see cref="StaleBindCount"/>).
    /// </para>
    /// <para>
    /// Threading: only the main thread mutates the tables (on the host only inside the capture window, ARCH §4.5.5).
    /// Readers on other threads are safe while no thread mutates (the capture-window discipline); the readers are
    /// marked <see cref="ThreadKind.Any"/> for that reason.
    /// </para>
    /// </summary>
    public sealed class SlotTable : ISlotTable
    {
        private const int PageShift = 13;
        private const int PageSize = 1 << PageShift;
        private const int PageMask = PageSize - 1;
        private const int SlotLimit = (int)NetSlot.MaxValue + 1;
        private const int SlotPageLimit = SlotLimit >> PageShift;
        private const byte FlagBound = 1;
        private const byte FlagSpawnEmitted = 2;
        private const int EntityEntryBytes = 8;
        private const int SlotEntryBytes = 24;

        private struct EntityEntry
        {
            public uint Slot;       // 0 = no binding for this entity index
            public int Version;
        }

        private struct SlotEntry
        {
            public int EntityIndex;
            public int EntityVersion;
            public int Prefab;      // PrefabRank.Value at binding
            public int TypeSet;     // TypeSetId.Value
            public ushort Revision; // survives Unbind (ARCH §3.8)
            public byte Generation;
            public byte Family;
            public byte Flags;      // FlagBound | FlagSpawnEmitted
        }

        private readonly ILog _log;
        private readonly SlotBitSet _bound = new SlotBitSet(SlotLimit);
        private readonly SlotBitSet _unspawned = new SlotBitSet(SlotLimit);
        private EntityEntry[][] _entityPages;
        private SlotEntry[][] _slotPages;
        private int _entityPageCount;
        private int _slotPageCount;
        private int _highWaterMark = 1;
        private int _version;
        private long _conflicts;
        private long _rebinds;
        private long _staleBinds;

        /// <param name="initialEntityCapacity">Expected highest entity index; pre-sizes the page directory (pages
        /// themselves are allocated on first use).</param>
        public SlotTable(int initialEntityCapacity = 1 << 20) : this(initialEntityCapacity, null)
        {
        }

        /// <param name="initialEntityCapacity">Expected highest entity index (see the other constructor).</param>
        /// <param name="log">Receives binding conflicts (category <see cref="LogCategory.Replication"/>); may be null.</param>
        public SlotTable(int initialEntityCapacity, ILog log)
        {
            if (initialEntityCapacity < 0) throw new ArgumentOutOfRangeException(nameof(initialEntityCapacity));
            _log = log;
            _entityPages = new EntityEntry[Math.Max(1, (initialEntityCapacity + PageMask) >> PageShift)][];
            _slotPages = new SlotEntry[16][];
        }

        // ------------------------------------------------------------------ ISlotTable

        [ThreadAffinity(ThreadKind.Any)]
        public int BoundCount => _bound.Count;

        /// <summary>One above the highest slot ever bound since construction or <see cref="Clear"/>; 1 when none was.</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public int HighWaterMark => _highWaterMark;

        [ThreadAffinity(ThreadKind.Any)]
        public bool TryGetSlot(EntityKey entity, out NetSlot slot)
        {
            if (TryGetEntityEntry(entity.Index, out EntityEntry e) && e.Slot != 0 && e.Version == entity.Version)
            {
                slot = new NetSlot(e.Slot);
                return true;
            }
            slot = NetSlot.None;
            return false;
        }

        [ThreadAffinity(ThreadKind.Any)]
        public bool TryGetSlotByIndex(int entityIndex, out NetSlot slot)
        {
            if (TryGetEntityEntry(entityIndex, out EntityEntry e) && e.Slot != 0)
            {
                slot = new NetSlot(e.Slot);
                return true;
            }
            slot = NetSlot.None;
            return false;
        }

        [ThreadAffinity(ThreadKind.Any)]
        public bool TryGetEntity(NetSlot slot, out EntityKey entity)
        {
            if (TryGetBound(slot.Value, out SlotEntry s))
            {
                entity = new EntityKey(s.EntityIndex, s.EntityVersion);
                return true;
            }
            entity = EntityKey.Null;
            return false;
        }

        /// <summary>Generation of the current binding; 0 for an unbound slot.</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public byte GenerationOf(NetSlot slot) => TryGetBound(slot.Value, out SlotEntry s) ? s.Generation : (byte)0;

        /// <summary>Family of the current binding; <see cref="Family.ToolLocal"/> (never replicated) for an unbound slot.</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public Family FamilyOf(NetSlot slot) => TryGetBound(slot.Value, out SlotEntry s) ? (Family)s.Family : Family.ToolLocal;

        /// <summary>Prefab rank recorded at binding (the upsert check of ARCH §4.7.3); <see cref="PrefabRank.None"/> for an unbound slot.</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public PrefabRank PrefabOf(NetSlot slot) => TryGetBound(slot.Value, out SlotEntry s) ? new PrefabRank(s.Prefab) : PrefabRank.None;

        /// <summary>Current replicated type set; <see cref="TypeSetId.None"/> for an unbound slot.</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public TypeSetId TypeSetOf(NetSlot slot) => TryGetBound(slot.Value, out SlotEntry s) ? new TypeSetId(s.TypeSet) : TypeSetId.None;

        /// <summary>Authored revision (ARCH §3.8); kept across unbinding, so it is also defined for unbound slots.</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public ushort RevisionOf(NetSlot slot) => TryGetSlotEntry(slot.Value, out SlotEntry s) ? s.Revision : (ushort)0;

        [ThreadAffinity(ThreadKind.Any)]
        public bool IsSpawnEmitted(NetSlot slot) => TryGetBound(slot.Value, out SlotEntry s) && (s.Flags & FlagSpawnEmitted) != 0;

        // ------------------------------------------------------------------ mutation (main thread)

        /// <summary>
        /// Binds <paramref name="slot"/> to <paramref name="entity"/>. Binding a bound slot again updates it (a different
        /// entity replaces the old one, counted in <see cref="RebindCount"/>). If the entity is bound to another slot,
        /// that slot is unbound first (<see cref="ConflictCount"/>). Throws for <see cref="NetSlot.None"/>, the null
        /// entity, a negative entity index or an undefined family.
        /// </summary>
        public void Bind(NetSlot slot, byte generation, EntityKey entity, Family family, PrefabRank prefab, TypeSetId typeSet, bool spawnEmitted)
        {
            if (slot.IsNone) throw new ArgumentException("NetSlot.None cannot be bound", nameof(slot));
            if (entity.Index < 0) throw new ArgumentOutOfRangeException(nameof(entity), entity, "negative entity index");
            if (entity.IsNull) throw new ArgumentException("Entity.Null cannot be bound", nameof(entity));
            if ((byte)family > (byte)Family.Generic) throw new ArgumentOutOfRangeException(nameof(family), family, "undefined family");

            uint value = slot.Value;
            ref SlotEntry s = ref SlotRef(value);
            bool wasBound = (s.Flags & FlagBound) != 0;
            if (wasBound && (s.EntityIndex != entity.Index || s.EntityVersion != entity.Version))
            {
                ReleaseEntityMapping(value, s.EntityIndex);
                _rebinds++;
                if (_log != null && _log.IsEnabled(LogLevel.Debug))
                {
                    _log.Write(LogLevel.Debug, LogCategory.Replication, "slot table: " + slot + " rebound from "
                               + new EntityKey(s.EntityIndex, s.EntityVersion) + " to " + entity);
                }
            }

            ref EntityEntry e = ref EntityRef(entity.Index);
            bool moveIndexMapping = true;
            if (e.Slot != 0 && e.Slot != value)
            {
                if (e.Version == entity.Version)
                {
                    // one live entity, two slots: the newer binding wins, the older slot loses its binding
                    uint other = e.Slot;
                    UnbindCore(other);
                    _conflicts++;
                    if (_log != null && _log.IsEnabled(LogLevel.Warn))
                    {
                        _log.Write(LogLevel.Warn, LogCategory.Replication, "slot table: " + entity + " was bound to slot:"
                                   + other.ToString(CultureInfo.InvariantCulture) + " and is now bound to " + slot
                                   + "; the old slot was unbound");
                    }
                }
                else if (e.Version > entity.Version)
                {
                    // Entity versions grow monotonically per index (EntityComponentStore increments the version on every
                    // allocation and deallocation; live versions are odd, EntityComponentStore.cs:660-672, 766), so the
                    // other slot holds a newer occupant of this index and the entity bound here is already destroyed. The
                    // index mapping stays with the newer occupant; this binding only records slot → entity until the
                    // caller's liveness check (EntityManager.Exists) unbinds it.
                    moveIndexMapping = false;
                    _staleBinds++;
                    if (_log != null && _log.IsEnabled(LogLevel.Warn))
                    {
                        _log.Write(LogLevel.Warn, LogCategory.Replication, "slot table: " + slot + " bound to " + entity
                                   + ", but slot:" + e.Slot.ToString(CultureInfo.InvariantCulture) + " holds the newer version "
                                   + e.Version.ToString(CultureInfo.InvariantCulture) + " of that index; the index mapping was kept");
                    }
                }
                // otherwise the other slot's entity is gone (its index was reused): the index mapping moves to the new
                // entity, the stale binding stays until the caller's liveness check unbinds it (ARCH §3.3, §4.4.6)
            }

            if (moveIndexMapping)
            {
                e.Slot = value;
                e.Version = entity.Version;
            }
            s.EntityIndex = entity.Index;
            s.EntityVersion = entity.Version;
            s.Prefab = prefab.Value;
            s.TypeSet = typeSet.Value;
            s.Generation = generation;
            s.Family = (byte)family;
            s.Flags = (byte)(FlagBound | (spawnEmitted ? FlagSpawnEmitted : 0));

            if (!wasBound)
            {
                _bound.Set((int)value);
                _version++;
            }
            if (spawnEmitted) _unspawned.Clear((int)value);
            else _unspawned.Set((int)value);
            if ((int)value >= _highWaterMark) _highWaterMark = (int)value + 1;
        }

        /// <summary>
        /// Removes the binding of <paramref name="slot"/> (no-op when it is unbound) and bumps its revision, which the
        /// slot keeps (ARCH §3.8). The entity index mapping is cleared only if it still points at this slot.
        /// </summary>
        public void Unbind(NetSlot slot)
        {
            if (slot.IsNone) return;
            UnbindCore(slot.Value);
        }

        /// <summary>Sets the replicated type set of a bound slot (signature change); ignored for an unbound slot.</summary>
        public void SetTypeSet(NetSlot slot, TypeSetId typeSet)
        {
            if (!IsBoundValue(slot.Value)) return;
            SlotRef(slot.Value).TypeSet = typeSet.Value;
        }

        /// <summary>Sets the host's <c>spawnEmitted</c> bit of a bound slot (ARCH §3.1); ignored for an unbound slot.</summary>
        public void SetSpawnEmitted(NetSlot slot, bool value)
        {
            if (!IsBoundValue(slot.Value)) return;
            ref SlotEntry s = ref SlotRef(slot.Value);
            if (value)
            {
                s.Flags |= FlagSpawnEmitted;
                _unspawned.Clear((int)slot.Value);
            }
            else
            {
                s.Flags = (byte)(s.Flags & ~FlagSpawnEmitted);
                _unspawned.Set((int)slot.Value);
            }
        }

        /// <summary>Increments the authored revision (wrapping at 65536) and returns the new value; 0 for <see cref="NetSlot.None"/>.
        /// Allowed on unbound slots, whose revision persists.</summary>
        public ushort BumpRevision(NetSlot slot)
        {
            if (slot.IsNone) return 0;
            ref SlotEntry s = ref SlotRef(slot.Value);
            s.Revision = unchecked((ushort)(s.Revision + 1));
            return s.Revision;
        }

        /// <summary>Sets the authored revision (for example from a restore or a host value); ignored for <see cref="NetSlot.None"/>.</summary>
        public void SetRevision(NetSlot slot, ushort revision)
        {
            if (slot.IsNone) return;
            SlotRef(slot.Value).Revision = revision;
        }

        /// <summary>Drops every binding, revision and counter and releases the pages.</summary>
        public void Clear()
        {
            _entityPages = new EntityEntry[_entityPages.Length][];
            _slotPages = new SlotEntry[16][];
            _entityPageCount = 0;
            _slotPageCount = 0;
            _bound.Reset();
            _unspawned.Reset();
            _highWaterMark = 1;
            _conflicts = 0;
            _rebinds = 0;
            _staleBinds = 0;
            _version++;
        }

        /// <summary>
        /// The bound slots in ascending order. The enumeration throws <see cref="InvalidOperationException"/> when a slot
        /// is bound or unbound while it runs (field updates such as <see cref="SetTypeSet"/> are allowed).
        /// </summary>
        public IEnumerable<NetSlot> EnumerateBound()
        {
            int version = _version;
            for (int s = _bound.NextSetBit(1); s >= 0; s = _bound.NextSetBit(s + 1))
            {
                if (version != _version) throw new InvalidOperationException("the slot table was modified during the enumeration");
                yield return new NetSlot((uint)s);
                if (version != _version) throw new InvalidOperationException("the slot table was modified during the enumeration");
            }
        }

        /// <summary>Appends the bound slots to <paramref name="output"/> in ascending order; returns how many were appended.</summary>
        public int CopyBound(List<NetSlot> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            int count = 0;
            for (int s = _bound.NextSetBit(1); s >= 0; s = _bound.NextSetBit(s + 1))
            {
                output.Add(new NetSlot((uint)s));
                count++;
            }
            return count;
        }

        /// <summary>
        /// Appends every bound slot whose <c>spawnEmitted</c> bit is false (the host's pending spawns, ARCH §4.4.2 step 2)
        /// to <paramref name="output"/> in ascending order and returns how many were appended. Cost is proportional to the
        /// pending set, not to the table.
        /// </summary>
        public int CopyUnspawned(List<NetSlot> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            int count = 0;
            for (int s = _unspawned.NextSetBit(1); s >= 0; s = _unspawned.NextSetBit(s + 1))
            {
                output.Add(new NetSlot((uint)s));
                count++;
            }
            return count;
        }

        // ------------------------------------------------------------------ diagnostics

        /// <summary>Bound slots whose <c>spawnEmitted</c> bit is false.</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public int UnspawnedCount => _unspawned.Count;

        /// <summary>Bindings that took a live entity away from another slot (each unbound that slot; should stay 0).</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public long ConflictCount => _conflicts;

        /// <summary>Binds of an already bound slot to a different entity without an <see cref="Unbind"/> in between.</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public long RebindCount => _rebinds;

        /// <summary>Binds of an entity whose index already maps to a newer version on another slot (a destroyed entity;
        /// the index mapping stayed with the newer occupant; should stay 0).</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public long StaleBindCount => _staleBinds;

        /// <summary>Bytes held by the pages, directories and bit sets (diagnostics dump, ARCH §15.2).</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public long MemoryBytes
        {
            get
            {
                long bytes = (long)_entityPageCount * PageSize * EntityEntryBytes + (long)_slotPageCount * PageSize * SlotEntryBytes;
                bytes += ((long)_entityPages.Length + _slotPages.Length) * IntPtr.Size;
                return bytes + _bound.MemoryBytes + _unspawned.MemoryBytes;
            }
        }

        public override string ToString()
        {
            return "SlotTable(bound " + BoundCount.ToString(CultureInfo.InvariantCulture) + ", high-water "
                   + _highWaterMark.ToString(CultureInfo.InvariantCulture) + ", unspawned "
                   + _unspawned.Count.ToString(CultureInfo.InvariantCulture) + ")";
        }

        // ------------------------------------------------------------------ internals

        private void UnbindCore(uint slot)
        {
            int page = (int)(slot >> PageShift);
            if (page >= _slotPages.Length || _slotPages[page] == null) return;
            ref SlotEntry s = ref _slotPages[page][slot & PageMask];
            if ((s.Flags & FlagBound) == 0) return;
            ReleaseEntityMapping(slot, s.EntityIndex);
            ushort revision = unchecked((ushort)(s.Revision + 1));
            s = default;
            s.Revision = revision;
            _bound.Clear((int)slot);
            _unspawned.Clear((int)slot);
            _version++;
        }

        private void ReleaseEntityMapping(uint slot, int entityIndex)
        {
            if (entityIndex < 0) return;
            int page = entityIndex >> PageShift;
            if (page >= _entityPages.Length || _entityPages[page] == null) return;
            ref EntityEntry e = ref _entityPages[page][entityIndex & PageMask];
            if (e.Slot == slot)
            {
                e.Slot = 0;
                e.Version = 0;
            }
        }

        private bool IsBoundValue(uint slot) => slot != 0 && _bound.Get((int)slot);

        private bool TryGetEntityEntry(int entityIndex, out EntityEntry entry)
        {
            if (entityIndex >= 0)
            {
                EntityEntry[][] pages = _entityPages;
                int page = entityIndex >> PageShift;
                if (page < pages.Length)
                {
                    EntityEntry[] p = pages[page];
                    if (p != null)
                    {
                        entry = p[entityIndex & PageMask];
                        return true;
                    }
                }
            }
            entry = default;
            return false;
        }

        private bool TryGetSlotEntry(uint slot, out SlotEntry entry)
        {
            SlotEntry[][] pages = _slotPages;
            int page = (int)(slot >> PageShift);
            if (slot != 0 && page < pages.Length)
            {
                SlotEntry[] p = pages[page];
                if (p != null)
                {
                    entry = p[slot & PageMask];
                    return true;
                }
            }
            entry = default;
            return false;
        }

        private bool TryGetBound(uint slot, out SlotEntry entry)
        {
            return TryGetSlotEntry(slot, out entry) && (entry.Flags & FlagBound) != 0;
        }

        private ref EntityEntry EntityRef(int entityIndex)
        {
            int page = entityIndex >> PageShift;
            if (page >= _entityPages.Length)
            {
                int length = Math.Max(page + 1, _entityPages.Length * 2);
                Array.Resize(ref _entityPages, length);
            }
            EntityEntry[] p = _entityPages[page];
            if (p == null)
            {
                p = new EntityEntry[PageSize];
                _entityPages[page] = p;
                _entityPageCount++;
            }
            return ref p[entityIndex & PageMask];
        }

        private ref SlotEntry SlotRef(uint slot)
        {
            int page = (int)(slot >> PageShift);
            if (page >= _slotPages.Length)
            {
                int length = Math.Min(Math.Max(page + 1, _slotPages.Length * 2), SlotPageLimit);
                Array.Resize(ref _slotPages, length);
            }
            SlotEntry[] p = _slotPages[page];
            if (p == null)
            {
                p = new SlotEntry[PageSize];
                _slotPages[page] = p;
                _slotPageCount++;
            }
            return ref p[slot & PageMask];
        }
    }
}
