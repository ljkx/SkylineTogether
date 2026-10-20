using System;
using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Game.Serialization;
using SkylineTogether.Core;
using SkylineTogether.Core.Sync;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// Network identity of replicated entities (ARCHITECTURE.md#net-ids): dense side tables, never a component, so binding costs
    /// no structural change. <see cref="WriterTable"/> (index = <c>Entity.Index</c>, value = <c>Entity(netId,
    /// version)</c>) and <see cref="ReaderTable"/> (index = net id, value = the local entity) plug straight into the
    /// game's <c>BinaryWriter</c>/<c>BinaryReader</c>. The bindings of tracked entities are saved into every save
    /// (the join snapshot included) by the game's own system serializer, so a joiner's freshly loaded entities carry
    /// the host's ids without any table transfer. Without the mod, the block is skipped by the game
    /// (<c>ObsoleteSystemSerializer</c> skips it by its size); a save without the block resets the tables
    /// (<c>SetDefaults</c>).
    /// <para>
    /// Single-player safety: the bindings are written only into a join snapshot (<see cref="WritingSnapshot"/>, set by
    /// <c>SnapshotService</c> around its save); every other save, including the host's own saves and autosaves during a
    /// session, carries an empty block of 12 bytes. Loaded bindings are kept only by a joining client; anyone else
    /// reads and drops them. The tables never grow without a session.
    /// </para>
    /// <para>
    /// Ids are reused. On the host (<see cref="Authority"/>) an id unbound with its despawn written into a batch is
    /// staged (<see cref="StagedReleases"/>) and enters the quarantine of <see cref="Ids"/> (<see cref="NetIdAllocator"/>)
    /// only once that batch was handed to the peers (<see cref="ReleaseStaged"/>); the quarantine frees it only after every
    /// peer applied the despawn. An id unbound without a despawn on the wire waits in <see cref="TakePendingDespawns"/>
    /// until the capture writes one. Clients keep the host's generation of each id (<see cref="TryGetGeneration"/>), so
    /// a record for an id this client still binds to an older entity is recognised (<see cref="BoundRecordCheck"/>).
    /// Per-family index sets (<see cref="ScannedIds"/>) replace scans over the whole table. Session start compacts the
    /// ids (<see cref="Compact"/>). Saves of format 1 (no generations) still load.
    /// </para>
    /// </summary>
    [FormerlySerializedAs("SkylineTogether.Sync.NetIdSystem, SkylineTogether")]
    public sealed class NetIdSystem : GameSystemBase, IDefaultSerializable, ISerializable, IPostDeserialize
    {
        /// <summary>1: bindings (entity, id, family). 2: + the id's generation.</summary>
        private const int FormatVersion = 2;

        private const int FamilyCount = 32;


        /// <summary>True while the host's join snapshot is being written (the only save that carries the bindings).</summary>
        public static volatile bool WritingSnapshot;

        private NativeList<Entity> _writer;
        private NativeList<Entity> _reader;
        private NativeList<byte> _family;
        private int _bound;
        private readonly IdIndexSet _scanned = new IdIndexSet();
        private readonly IdIndexSet _agents = new IdIndexSet();
        private readonly IdIndexSet _structure = new IdIndexSet();
        private readonly int[] _familyCounts = new int[FamilyCount];
        private readonly List<uint> _pendingDespawns = new List<uint>();
        private readonly HashSet<uint> _pendingDespawnSet = new HashSet<uint>(); // O(1) membership (was List.Contains per unbind)
        private bool _warnedExhausted;
        private readonly IdGenerationTable _clientGenerations = new IdGenerationTable();

        public NetIdAllocator Ids { get; } = new NetIdAllocator(Mod.Log);

        /// <summary>The largest id the host's announced range allows (<see cref="NetIdCeiling"/>).</summary>
        public NetIdCeiling Ceiling { get; } = new NetIdCeiling();

        /// <summary>
        /// True on the host during a session: unbinding releases ids into the quarantine. False on clients and outside
        /// sessions (ids are then only mirrored).
        /// </summary>
        public bool Authority { get; set; }

        /// <summary>Index = <c>Entity.Index</c>; value = <c>Entity(netId, version)</c>, default when unbound.</summary>
        public NativeArray<Entity> WriterTable => _writer.AsArray();

        /// <summary>Index = net id; value = the local entity (<c>Entity.Null</c> when unbound). Index 0 is always null.</summary>
        public NativeArray<Entity> ReaderTable => _reader.AsArray();

        public int BoundCount => _bound;

        /// <summary>Bound ids of the value-scanned families (<see cref="FamilyClassifier.IsScanned"/>).</summary>
        public IdIndexSet ScannedIds => _scanned;

        /// <summary>Bound ids of the agent family (the host's rolling agent scan).</summary>
        public IdIndexSet AgentIds => _agents;

        /// <summary>
        /// Bound ids of the structure families the host's rolling passes cover (objects, net edges and nodes,
        /// areas: <see cref="FamilyClassifier.IsPresenceWatched"/>; the presence watch and the attention values scan), so
        /// refilling those passes copies this set instead of walking the whole id table (agents and movers included).
        /// </summary>
        public IdIndexSet StructureIds => _structure;

        /// <summary>Bound ids per family (diagnostics).</summary>
        public int CountOf(Family family) => (int)family < FamilyCount ? _familyCounts[(int)family] : 0;

        protected override void OnCreate()
        {
            base.OnCreate();
            _writer = new NativeList<Entity>(1024, Allocator.Persistent);
            _reader = new NativeList<Entity>(1024, Allocator.Persistent);
            _family = new NativeList<byte>(1024, Allocator.Persistent);
            Clear();
        }

        protected override void OnDestroy()
        {
            if (_writer.IsCreated) _writer.Dispose();
            if (_reader.IsCreated) _reader.Dispose();
            if (_family.IsCreated) _family.Dispose();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
        }

        public void Clear()
        {
            // a client session can end on exit after the world destroyed this system (lists disposed)
            if (!_writer.IsCreated || !_reader.IsCreated || !_family.IsCreated) return;
            _writer.Clear();
            _reader.Clear();
            _family.Clear();
            // a session's tables (one entry per entity of the city) are released when it ends
            if (_writer.Capacity > 4096) _writer.Capacity = 1024;
            if (_reader.Capacity > 4096) _reader.Capacity = 1024;
            if (_family.Capacity > 4096) _family.Capacity = 1024;
            _reader.Add(Entity.Null);
            _family.Add(0);
            _writer.Add(default(Entity));
            _bound = 0;
            ClearIndexes();
            _pendingDespawns.Clear();
            _pendingDespawnSet.Clear();
            Ids.Clear();
            _clientGenerations.Clear();
            Ceiling.Reset();
            _warnedExhausted = false;
        }

        /// <summary>The entity's net id, 0 when unbound (or when the index now belongs to another entity).</summary>
        public uint GetNetId(Entity entity)
        {
            if (entity.Index <= 0 || entity.Index >= _writer.Length) return 0;
            Entity e = _writer[entity.Index];
            return e.Version == entity.Version && e.Index > 0 ? (uint)e.Index : 0;
        }

        /// <summary>The local entity bound to <paramref name="netId"/>, or <c>Entity.Null</c>.</summary>
        public Entity GetEntity(uint netId)
        {
            return netId > 0 && netId < (uint)_reader.Length ? _reader[(int)netId] : Entity.Null;
        }

        public Family GetFamily(uint netId)
        {
            return netId > 0 && netId < (uint)_family.Length ? (Family)_family[(int)netId] : Family.Generic;
        }

        /// <summary>The id's generation (incremented on each reuse; host and the clients that loaded its save).</summary>
        public byte GetGeneration(uint netId) => Ids.GenerationOf(netId);

        /// <summary>
        /// The host's generation of the id: on the host always known; on a client known for ids bound from the host's
        /// records or its snapshot, unknown for ids this client bound itself (its predicted edits).
        /// </summary>
        public bool TryGetGeneration(uint netId, out byte generation)
        {
            if (Authority)
            {
                generation = Ids.GenerationOf(netId);
                return netId != 0 && netId <= NetIdAllocator.MaxId;
            }
            return _clientGenerations.TryGet(netId, out generation);
        }

        /// <summary>Client: records the host's generation of a bound id (from the record that bound it).</summary>
        public void NoteGeneration(uint netId, byte generation)
        {
            if (!Authority) _clientGenerations.Set(netId, generation);
        }

        /// <summary>
        /// Host: a new id (lowest free after quarantine, else fresh), 0 when all 16 777 215 ids are in use (logged once;
        /// the caller then leaves the entity unbound).
        /// </summary>
        public uint AllocateId()
        {
            if (Ids.TryAllocate(out uint id)) return id;
            if (!_warnedExhausted)
            {
                _warnedExhausted = true;
                Mod.Log?.Error(LogCategory.Replication, "net ids exhausted (" + Ids.Describe() + "); new entities are not replicated");
            }
            return 0;
        }

        /// <summary>Host: allocates an id and binds the entity to it; 0 when no id is left.</summary>
        public uint BindNew(Entity entity, Family family)
        {
            uint id = AllocateId();
            if (id != 0) Bind(entity, id, family);
            return id;
        }

        /// <summary>
        /// Whether <paramref name="netId"/> may be bound here: always on the host, within the host's announced
        /// range on a client (a refusal is counted; the first one is logged).
        /// </summary>
        public bool MayBind(uint netId)
        {
            if (netId == 0 || netId > NetIdAllocator.MaxId) return false;
            if (Authority) return true;
            if (Ceiling.Allows(netId, out bool first)) return true;
            if (first) Mod.Log?.Warn(LogCategory.Replication, "the host sent net id " + netId + ", beyond its announced range (" + Ceiling.Describe() + "); records with such ids are refused");
            return false;
        }

        /// <summary>Binds the entity to the id; false when the id is refused (quarantined on the host, beyond the ceiling on a client).</summary>
        public bool Bind(Entity entity, uint netId, Family family)
        {
            if (entity == Entity.Null || !MayBind(netId)) return false;
            if (Authority && !Ids.IsAllocated(netId) && !Ids.Claim(netId)) return false; // quarantined ids are never rebound
            Entity old = GetEntity(netId);
            if (old != Entity.Null && old != entity) UnbindEntityIndex(old);
            uint previous = GetNetId(entity);
            if (previous != 0 && previous != netId) UnbindId(previous, null);
            EnsureReader((int)netId);
            EnsureWriter(entity.Index);
            if (_reader[(int)netId] == Entity.Null) _bound++;
            else RemoveFromIndexes(netId, (Family)_family[(int)netId]);
            if (!Authority && _reader[(int)netId] != entity) _clientGenerations.Forget(netId); // until the binder notes the host's
            _reader[(int)netId] = entity;
            _family[(int)netId] = (byte)family;
            _writer[entity.Index] = new Entity { Index = (int)netId, Version = entity.Version };
            AddToIndexes(netId, family);
            return true;
        }

        /// <summary>
        /// Unbinds an id. The host keeps the id allocated until the capture wrote a despawn for it
        /// (<see cref="TakePendingDespawns"/>), so no peer can keep a binding of an id that is reused.
        /// </summary>
        public void Unbind(uint netId) => Unbind(netId, null);

        /// <summary>
        /// Unbinds an id whose despawn the caller just wrote into a batch that is not sent yet: on the host the id is
        /// staged in <paramref name="staged"/> and enters the quarantine only through <see cref="ReleaseStaged"/>, once that
        /// batch was handed to the peers (null: as <see cref="Unbind(uint)"/>).
        /// </summary>
        public void Unbind(uint netId, StagedReleases staged)
        {
            Entity e = GetEntity(netId);
            if (e != Entity.Null) UnbindEntityIndex(e);
            UnbindId(netId, staged);
        }

        /// <summary>Host: the batch carrying the staged despawns was handed to the peers; their ids enter the quarantine.</summary>
        public int ReleaseStaged(StagedReleases staged) => staged == null || staged.Count == 0 ? 0 : staged.Commit(Ids);

        /// <summary>Host: whether <paramref name="netId"/> was unbound and waits for its despawn (<see cref="TakePendingDespawns"/>).</summary>
        public bool IsDespawnPending(uint netId) => _pendingDespawnSet.Contains(netId);

        /// <summary>Host: ids unbound without a despawn on the wire; the caller writes despawns and releases them.</summary>
        public bool TakePendingDespawns(List<uint> output)
        {
            if (_pendingDespawns.Count == 0) return false;
            output.AddRange(_pendingDespawns);
            _pendingDespawns.Clear();
            _pendingDespawnSet.Clear();
            return true;
        }

        private void UnbindId(uint netId, StagedReleases staged)
        {
            if (netId == 0 || netId >= (uint)_reader.Length) return;
            bool wasBound = _reader[(int)netId] != Entity.Null;
            if (wasBound)
            {
                _bound--;
                RemoveFromIndexes(netId, (Family)_family[(int)netId]);
            }
            _reader[(int)netId] = Entity.Null;
            _family[(int)netId] = 0;
            // only a binding is released: an allocated but unbound id is a client's unused grant (returned when it
            // leaves) or already waits for its despawn
            if (!wasBound || !Authority || !Ids.IsAllocated(netId)) return;
            if (staged != null)
            {
                staged.Stage(netId);
                return;
            }
            if (_pendingDespawnSet.Add(netId)) _pendingDespawns.Add(netId);
        }

        private void AddToIndexes(uint id, Family family)
        {
            if ((int)family < FamilyCount) _familyCounts[(int)family]++;
            if (FamilyClassifier.IsScanned(family)) _scanned.Add(id);
            if (family == Family.Agent && id < (1u << 28)) _agents.Add(id);
            if (FamilyClassifier.IsPresenceWatched(family) && id < (1u << 28)) _structure.Add(id);
        }

        private void RemoveFromIndexes(uint id, Family family)
        {
            if ((int)family < FamilyCount && _familyCounts[(int)family] > 0) _familyCounts[(int)family]--;
            _scanned.Remove(id);
            _agents.Remove(id);
            _structure.Remove(id);
        }

        private void ClearIndexes()
        {
            _scanned.Clear();
            _agents.Clear();
            _structure.Clear();
            Array.Clear(_familyCounts, 0, _familyCounts.Length);
        }

        private void UnbindEntityIndex(Entity e)
        {
            if (e.Index > 0 && e.Index < _writer.Length && _writer[e.Index].Version == e.Version) _writer[e.Index] = default(Entity);
        }

        private void EnsureReader(int index)
        {
            if (index < _reader.Length) return;
            int size = NetIdCeiling.GrowTo(_reader.Length, index, (int)NetIdAllocator.MaxId + 1); // geometric; ids are bounded by MayBind
            int old = _reader.Length;
            _reader.Resize(size, NativeArrayOptions.ClearMemory);
            _family.Resize(size, NativeArrayOptions.ClearMemory);
            for (int i = old; i < size; i++) _reader[i] = Entity.Null;
        }

        private void EnsureWriter(int index)
        {
            if (index < _writer.Length) return;
            int size = Math.Max(index + 1, _writer.Length * 2);
            _writer.Resize(size, NativeArrayOptions.ClearMemory);
        }

        /// <summary>Drops bindings whose entity no longer exists (no despawn is sent: only for load time).</summary>
        public int Prune()
        {
            int dropped = 0;
            bool authority = Authority;
            Authority = false;
            try
            {
                for (int id = 1; id < _reader.Length; id++)
                {
                    Entity e = _reader[id];
                    if (e == Entity.Null) continue;
                    if (!EntityManager.Exists(e))
                    {
                        Unbind((uint)id);
                        dropped++;
                    }
                }
            }
            finally
            {
                Authority = authority;
            }
            return dropped;
        }

        /// <summary>
        /// Rebuilds the allocator from the current bindings (every unbound id below <paramref name="next"/> becomes free,
        /// nothing stays quarantined). Only when no peer can hold an id: after a load, at host session start.
        /// </summary>
        public void RebuildAllocator(uint next)
        {
            var bound = new List<KeyValuePair<uint, byte>>(_bound);
            for (int id = 1; id < _reader.Length; id++)
            {
                if (_reader[id] != Entity.Null) bound.Add(new KeyValuePair<uint, byte>((uint)id, Ids.GenerationOf((uint)id)));
            }
            _pendingDespawns.Clear();
            _pendingDespawnSet.Clear();
            int skipped = Ids.Restore(next, bound);
            if (skipped > 0) Mod.Log?.Warn(LogCategory.Replication, "net ids: " + skipped + " invalid bindings skipped while rebuilding the allocator");
        }

        /// <summary>
        /// Host session start, before any peer is admitted (ARCHITECTURE.md#hosting): packs the bound ids into 1..n when long sessions
        /// spread them (<see cref="NetIdCompaction.ShouldCompact"/>), keeping their order, and rebuilds the allocator.
        /// Returns the number of ids that changed. Nothing may hold an id across this call (every session cache is reset
        /// at session end, and the movers bind lazily).
        /// </summary>
        public int Compact()
        {
            uint next = Math.Max(Ids.Next, (uint)_reader.Length);
            if (!NetIdCompaction.ShouldCompact(_bound, next))
            {
                RebuildAllocator(next);
                return 0;
            }
            var ids = new List<uint>(_bound);
            var entities = new List<Entity>(_bound);
            var families = new List<byte>(_bound);
            for (int id = 1; id < _reader.Length; id++)
            {
                Entity e = _reader[id];
                if (e == Entity.Null) continue;
                ids.Add((uint)id);
                entities.Add(e);
                families.Add(_family[id]);
            }
            if (!NetIdCompaction.BuildMap(ids, out uint[] map))
            {
                RebuildAllocator(next);
                return 0;
            }
            bool authority = Authority;
            Authority = false;
            int moved = 0;
            try
            {
                for (int i = 0; i < _writer.Length; i++) _writer[i] = default(Entity);
                for (int i = 0; i < _reader.Length; i++)
                {
                    _reader[i] = Entity.Null;
                    _family[i] = 0;
                }
                _bound = 0;
                ClearIndexes();
                Ids.Clear();
                for (int i = 0; i < ids.Count; i++)
                {
                    if (map[i] != ids[i]) moved++;
                    Bind(entities[i], map[i], (Family)families[i]);
                }
                int length = Math.Max(1 << 16, ids.Count + 1);
                if (_reader.Length > length)
                {
                    _reader.Resize(length, NativeArrayOptions.ClearMemory);
                    _family.Resize(length, NativeArrayOptions.ClearMemory);
                }
            }
            finally
            {
                Authority = authority;
            }
            RebuildAllocator((uint)ids.Count + 1);
            Mod.Log?.Info(LogCategory.Replication, "net ids compacted: " + ids.Count + " bindings now 1.." + ids.Count + " (was up to " + (next - 1) + "), " + moved + " renumbered");
            return moved;
        }

        /// <summary>One line for the diagnostics.</summary>
        public string Describe()
        {
            return _bound + " bound, table " + _reader.Length + "/" + _writer.Length + " (ids/entities), " + Ids.Describe()
                   + (_pendingDespawns.Count > 0 ? ", " + _pendingDespawns.Count + " despawns pending" : "");
        }

        // ------------------------------------------------------------------ persistence (every save)

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            bool include = WritingSnapshot && Mod.Sessions?.Host != null && _reader.IsCreated;
            writer.Write(FormatVersion);
            writer.Write(include ? Math.Max(Ids.Next, 1u) : 1u);
            int count = 0;
            if (include)
            {
                for (int id = 1; id < _reader.Length; id++)
                {
                    if (_reader[id] != Entity.Null && (Family)_family[id] != Family.Prefab) count++;
                }
            }
            writer.Write(count);
            if (count == 0) return;
            for (int id = 1; id < _reader.Length; id++)
            {
                Entity e = _reader[id];
                if (e == Entity.Null || (Family)_family[id] == Family.Prefab) continue;
                writer.Write(e);
                writer.Write((uint)id);
                writer.Write(_family[id]);
                writer.Write(Ids.GenerationOf((uint)id));
            }
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            // runs inside the deserializer's exclusive entity transaction: containers only
            Clear();
            Authority = false;
            reader.Read(out int format);
            if (format != 1 && format != FormatVersion) return; // a newer build's block: the reader skips the rest (logged by the game)
            reader.Read(out uint next);
            reader.Read(out int count);
            if (count < 0) return;
            Ceiling.Announce(next); // the host's frontier at the snapshot bounds the loaded ids
            // only a client joining the host's session uses the host's ids; any other load reads them and drops them
            bool keep = Mod.Sessions?.Client != null;
            var generations = new List<KeyValuePair<uint, byte>>(keep ? Math.Max(0, Math.Min(count, 1 << 20)) : 0);
            for (int i = 0; i < count; i++)
            {
                reader.Read(out Entity e);
                reader.Read(out uint id);
                reader.Read(out byte family);
                byte generation = 0;
                if (format >= 2) reader.Read(out generation);
                if (!keep || e == Entity.Null || id == 0 || id > NetIdAllocator.MaxId) continue;
                Bind(e, id, (Family)family);
                if (GetEntity(id) != e) continue;
                generations.Add(new KeyValuePair<uint, byte>(id, generation));
                if (format >= 2) _clientGenerations.Set(id, generation);
            }
            if (keep) Ids.Restore(Math.Min(Math.Max(next, 1u), NetIdAllocator.MaxId + 1), generations);
        }

        public void SetDefaults(Context context)
        {
            Clear();
        }

        public void PostDeserialize(Context context)
        {
            try
            {
                if (_bound == 0) return;
                int dropped = Prune();
                RebuildAllocator(Ids.Next);
                Mod.Log?.Info(LogCategory.Replication, "net ids after load: " + _bound + " bound" + (dropped > 0 ? ", " + dropped + " dropped" : "") + "; " + Ids.Describe());
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Replication, "net ids after load", ex);
            }
        }
    }
}
