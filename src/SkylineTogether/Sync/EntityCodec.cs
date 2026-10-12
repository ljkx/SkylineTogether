using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Game.Prefabs;
using SkylineTogether.Core;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Sync;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Hash128 = Colossal.Hash128;

namespace SkylineTogether.Sync
{
    /// <summary>What an apply did, for the caller's bookkeeping.</summary>
    public sealed class ApplyResult
    {
        public int Spawned, Upserted, Values, Despawned, Skipped, ZoneBlocks;
        public long Cost;
        public bool HasCost;
        public uint? Clock;
        public readonly List<(uint Start, uint Count)> IdRanges = new List<(uint, uint)>();
        /// <summary>Entities written or tagged by this apply (the client excludes them from its own capture).</summary>
        public readonly HashSet<Entity> Touched = new HashSet<Entity>();
        /// <summary>gameplay branch: the entities this apply created (host: build-order re-base of committed edges).</summary>
        public readonly List<Entity> SpawnedEntities = new List<Entity>();
        public readonly List<string> Problems = new List<string>();

        /// <summary>Clears the result for the next apply (the codec reuses one instance).</summary>
        public void Reset()
        {
            Spawned = Upserted = Values = Despawned = Skipped = ZoneBlocks = 0;
            Cost = 0;
            HasCost = false;
            Clock = null;
            IdRanges.Clear();
            Touched.Clear();
            Problems.Clear();
            SpawnedEntities.Clear();
        }

        public override string ToString() => "+" + Spawned + " ~" + Upserted + " v" + Values + " -" + Despawned + (Skipped > 0 ? " skipped " + Skipped : "") + (ZoneBlocks > 0 ? " zones " + ZoneBlocks : "");
    }

    /// <summary>
    /// Turns entities into <see cref="SyncBatch"/> records and records back into entities (ARCH §4.3, §4.7). The wire
    /// form of each component is its own save encoding; entity references become net ids, references to derived
    /// entities (lanes, station stops) become derived keys, and values that reference anything else are sent as
    /// presence only, so no peer ever receives a dangling reference. The applier creates entities from the prefab's
    /// own archetype, reconciles the replicated component set, writes the values, then tags the result so the
    /// game's own Modification pipeline rebuilds everything derived (geometry, lanes, zone blocks, sub-objects).
    /// </summary>
    public sealed partial class EntityCodec : IDisposable
    {
        private readonly ILog _log;
        private readonly NetIdSystem _ids;
        private readonly PrefabSystem _prefabs;
        private readonly FamilyClassifier _families = new FamilyClassifier();
        private readonly NetWriterState _ws = new NetWriterState();
        private readonly NetReaderState _rs = new NetReaderState();
        private readonly ByteWriter _payload = new ByteWriter(4096);
        private readonly HashSet<ulong> _failedTypes = new HashSet<ulong>();
        private readonly HashSet<string> _missingPrefabs = new HashSet<string>();
        // mod compatibility: prefabs the host named that this game lacks (asset packs, runtime prefabs of mods); the
        // binding is retried when a record needs it, because some mods create prefabs after the join's prefab table
        private readonly Dictionary<uint, byte[]> _unresolvedPrefabs = new Dictionary<uint, byte[]>();
        private readonly Dictionary<uint, string> _unresolvedPrefabNames = new Dictionary<uint, string>();
        private readonly SortedDictionary<string, int> _missingAssets = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<SyncEnabledBit> _bits = new List<SyncEnabledBit>();
        private Context _writeContext;
        private Context _readContext;
        private NativeList<byte> _scratch;
        private NativeArray<byte> _batch;
        private NativeReference<int> _position;
        private byte[] _managedScratch = new byte[4096];
        private EntityArchetype _pathEventArchetype;
        private EntityArchetype _colorEventArchetype;
        private bool _disposed;
        // w2/ids perf pass: per-archetype handler lists and pooled apply collections (no allocation per record)
        private readonly Dictionary<EntityArchetype, TypeHandler[]> _handlersByArchetype = new Dictionary<EntityArchetype, TypeHandler[]>();
        private readonly List<TypeHandler> _handlerScratch = new List<TypeHandler>(64);
        private readonly ApplyResult _result = new ApplyResult();
        private readonly List<(SyncRecord Record, Entity Entity)> _targets = new List<(SyncRecord, Entity)>();
        private readonly HashSet<Entity> _spawned = new HashSet<Entity>();
        private readonly HashSet<Entity> _touch = new HashSet<Entity>();
        private readonly HashSet<int> _recordTypes = new HashSet<int>();
        private readonly List<Entity> _pathUpdated = new List<Entity>();
        private readonly ByteReader _keyReader = new ByteReader();
        private TypeHandler _pathElement, _pathInformation, _connected, _prefabRef;
        private TypeHandler _journalEntry, _lifePathEvent, _chirp; // w3/audit: archetypes of host-only families
        private bool _handlersResolved;

        public EntityCodec(World world, NetIdSystem ids, ILog log)
        {
            World = world;
            _ids = ids;
            _log = log;
            _prefabs = world.GetOrCreateSystemManaged<PrefabSystem>();
            _writeContext = CreateContext(Purpose.SaveGame);
            _readContext = CreateContext(Purpose.LoadGame);
            _scratch = new NativeList<byte>(4096, Allocator.Persistent);
            _batch = new NativeArray<byte>(1 << 16, Allocator.Persistent);
            _position = new NativeReference<int>(Allocator.Persistent);
            _ws.Keys = BuildKey;
            _pathEventArchetype = EntityManager.CreateArchetype(ComponentType.ReadWrite<Game.Common.Event>(), ComponentType.ReadWrite<Game.Pathfind.PathUpdated>());
            _colorEventArchetype = EntityManager.CreateArchetype(ComponentType.ReadWrite<Game.Common.Event>(), ComponentType.ReadWrite<Game.Routes.ColorUpdated>());
        }

        public World World { get; }

        public EntityManager EntityManager => World.EntityManager;

        public NetIdSystem Ids => _ids;

        public FamilyClassifier Families => _families;

        /// <summary>
        /// Mod compatibility (dependency binding): set by the capture while it writes. Called for an entity reference
        /// that has neither a net id nor a derived key; returns true when it bound the entity (and queued it to be sent
        /// in the same batch), so the referencing value is serialized again instead of travelling as presence only.
        /// Traffic's lane connection data and Better Bulldozer's removal records are such entities.
        /// </summary>
        public Func<Entity, bool> DependencyBinder { get; set; }

        /// <summary>
        /// Mod compatibility: assets (prefabs) the host uses that this game lacks, with the number of records skipped
        /// because of them. A record whose prefab is missing is never applied (an entity without its prefab breaks the
        /// game's systems).
        /// </summary>
        public IReadOnlyDictionary<string, int> MissingAssets => _missingAssets;

        /// <summary>
        /// final review: session end. The missing-asset report and the unresolved prefab ids belong to one host's
        /// session; kept, they showed (and toasted) the previous session's assets, and an unresolved id could bind a
        /// later session's record to the old host's prefab name.
        /// </summary>
        public void ResetSession()
        {
            _missingPrefabs.Clear();
            _missingAssets.Clear();
            _unresolvedPrefabs.Clear();
            _unresolvedPrefabNames.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writeContext.Dispose();
            _readContext.Dispose();
            if (_scratch.IsCreated) _scratch.Dispose();
            if (_batch.IsCreated) _batch.Dispose();
            if (_position.IsCreated) _position.Dispose();
        }

        /// <summary>A serialization context with every format tag enabled, as for a save of this game version (R01 §0).</summary>
        private static Context CreateContext(Purpose purpose)
        {
            int tags = Enum.GetValues(typeof(Game.FormatTags)).Length + 64;
            var context = new Context(purpose, Game.Version.current, Hash128.Empty, tags, Allocator.Persistent);
            // P-CTX (Diagnostics.Probes); when it failed, replication is off and hosting/joining is refused
            MethodInfo enable = Diagnostics.Probes.ContextEnableTag;
            if (enable != null)
            {
                object format = context.format; // boxed copy; the bits live in shared unmanaged memory
                for (int i = 0; i < tags; i++) enable.Invoke(format, new object[] { i });
            }
            else
            {
                Mod.Log?.Error(LogCategory.Replication, "ContextFormat.EnableTag not found: version-dependent fields may decode wrongly");
            }
            return context;
        }

        // ================================================================== writing

        /// <summary>
        /// The replicating type handlers of an archetype, in the archetype's type order (cached: archetypes live as long as
        /// the world). Replaces a <c>GetComponentTypes</c> allocation and a handler lookup per type on every write or hash.
        /// </summary>
        public TypeHandler[] HandlersOf(EntityArchetype archetype)
        {
            if (_handlersByArchetype.TryGetValue(archetype, out TypeHandler[] cached)) return cached;
            NativeArray<ComponentType> types = archetype.GetComponentTypes(Allocator.Temp);
            try
            {
                _handlerScratch.Clear();
                foreach (ComponentType t in types)
                {
                    TypeHandler h = TypeHandlers.Get(t);
                    if (h != null) _handlerScratch.Add(h);
                }
                cached = _handlerScratch.ToArray();
            }
            finally
            {
                types.Dispose();
            }
            _handlersByArchetype[archetype] = cached;
            return cached;
        }

        /// <summary>The replicating type handlers of an existing entity (see <see cref="HandlersOf(EntityArchetype)"/>).</summary>
        public TypeHandler[] HandlersOf(Entity e) => HandlersOf(EntityManager.GetChunk(e).Archetype);

        /// <summary>Appends an entity record. Values records carry data only; others carry the full replicated type set.</summary>
        public void WriteEntity(SyncBatch.Writer w, SyncRecordKind kind, uint netId, Family family, Entity e)
        {
            TypeHandler[] handlers = HandlersOf(e);
            w.BeginEntity(kind, netId, family, kind != SyncRecordKind.Values);
            // w3/audit: events and chirps keep their values when they name an entity that does not replicate (a citizen
            // sender, a household target): that reference travels as null instead of dropping the whole component
            _ws.NullRefs = LenientRefs(family);
            // w3/agents: references of agents to transient agents (job and school seekers, meetings), service requests and
            // icons never replicate; they travel as null so the values beside them are not lost (as simulation values do)
            bool lenient = family == Family.Agent;
            try
            {
                for (int i = 0; i < handlers.Length; i++)
                {
                    TypeHandler h = handlers[i];
                    if (h.Kind == SyncComponentKind.Presence)
                    {
                        if (kind != SyncRecordKind.Values) w.AddComponent(h.Hash, SyncComponentKind.Presence, null, 0, 0);
                        continue;
                    }
                    int length;
                    bool ok = lenient && h.Kind == SyncComponentKind.Data ? TrySerializeSim(h, e, out length) : TrySerialize(h, e, out length);
                    if (ok) w.AddComponent(h.Hash, h.Kind, _managedScratch, 0, length);
                    else if (kind != SyncRecordKind.Values) w.AddComponent(h.Hash, SyncComponentKind.Presence, null, 0, 0);
                }
            }
            finally
            {
                _ws.NullRefs = null;
            }
            w.EndEntity();
            // mod compatibility: enabled bits travel right after the record (the vanilla Decoration of trees placed outside
            // decoration mode, Tree Controller's LumberResource)
            _bits.Clear();
            EntityManager em = EntityManager;
            for (int i = 0; i < handlers.Length; i++)
            {
                TypeHandler h = handlers[i];
                if (h.SyncEnabledBit) _bits.Add(new SyncEnabledBit(h.Hash, em.IsComponentEnabled(e, h.Type)));
            }
            if (_bits.Count > 0) w.EnabledBits(netId, _bits);
        }

        /// <summary>
        /// Mod compatibility (<see cref="Compat.ModStateWatcher"/>): hashes of an entity's replicated state restricted to
        /// the types <paramref name="include"/> accepts (every replicated type when it is null): the presence of those
        /// types, and their values and (with <paramref name="bits"/>) enabled bits. Never binds dependencies.
        /// </summary>
        public void HashSubset(Entity e, Func<TypeIndex, bool> include, bool bits, out ulong presence, out ulong value)
        {
            presence = 1469598103934665603UL;
            value = 1469598103934665603UL;
            EntityManager em = EntityManager;
            NativeArray<ComponentType> types = em.GetComponentTypes(e, Allocator.Temp);
            Func<Entity, bool> binder = DependencyBinder;
            DependencyBinder = null;
            try
            {
                foreach (ComponentType t in types)
                {
                    TypeHandler h = TypeHandlers.Get(t);
                    if (h == null || (include != null && !include(t.TypeIndex))) continue;
                    presence = Mix(presence, h.Hash);
                    if (bits && h.SyncEnabledBit && !em.IsComponentEnabled(e, t)) value = Mix(value, h.Hash ^ 0xE0E0E0E0UL);
                    if (h.Kind == SyncComponentKind.Presence) continue;
                    if (!TrySerialize(h, e, out int length)) continue;
                    value = Mix(value, Core.Hashing.XxHash64.Hash(_managedScratch, 0, length));
                }
            }
            finally
            {
                DependencyBinder = binder;
                types.Dispose();
            }
        }

        /// <summary>
        /// Serializes one component into <see cref="_managedScratch"/> as <c>[key count][keys][game bytes]</c>. False when the
        /// value references something that cannot be named on another peer (it is then sent as presence only).
        /// </summary>
        private bool TrySerialize(TypeHandler h, Entity e, out int length)
        {
            length = 0;
            for (int attempt = 0; ; attempt++)
            {
                _scratch.Clear();
                _ws.BeginComponent();
                _ws.Table = _ids.WriterTable;
                var inner = default(BinaryWriter);
                inner.Initialize(_writeContext, _scratch, _ws.Table);
                try
                {
                    h.Write(EntityManager, e, new NetWriter(inner, _ws));
                }
                catch (Exception ex)
                {
                    if (_failedTypes.Add(h.Hash)) _log.Warn(LogCategory.Replication, "cannot serialize " + h.Name + "; it will not replicate", ex);
                    return false;
                }
                if (!_ws.Unresolved) break;
                // mod compatibility: bind unbound mod data entities the value references, then write it again (the
                // writer table may have grown, so the whole value is serialized anew)
                if (attempt > 0 || DependencyBinder == null || _ws.UnresolvedEntities.Count == 0) return false;
                bool bound = false;
                var pending = new List<Entity>(_ws.UnresolvedEntities);
                foreach (Entity dependency in pending)
                {
                    try
                    {
                        bound |= DependencyBinder(dependency);
                    }
                    catch (Exception ex)
                    {
                        _log.Debug(LogCategory.Replication, "binding a dependency of " + h.Name + " failed: " + ex.Message);
                    }
                }
                if (!bound) return false;
            }
            _payload.Reset();
            _payload.WriteVarUInt32((uint)_ws.UsedKeys.Count);
            foreach (DerivedKey k in _ws.UsedKeys)
            {
                _payload.WriteByte(k.Kind);
                _payload.WriteVarUInt32(k.OwnerNetId);
                _payload.WriteVarUInt32(k.PrefabNetId);
                _payload.WriteVarInt32(k.Index);
                _payload.WriteVarInt32(k.X);
                _payload.WriteVarInt32(k.Y);
                _payload.WriteVarInt32(k.Z);
            }
            int head = _payload.Length;
            length = head + _scratch.Length;
            if (_managedScratch.Length < length) _managedScratch = new byte[Math.Max(length, _managedScratch.Length * 2)];
            Buffer.BlockCopy(_payload.Buffer, 0, _managedScratch, 0, head);
            if (_scratch.Length > 0) NativeArray<byte>.Copy(_scratch.AsArray(), 0, _managedScratch, head, _scratch.Length);
            return true;
        }

        /// <summary>A 64-bit hash of an entity's replicated values (shadow comparison).</summary>
        public ulong HashEntity(Entity e, bool valuesOnly) => HashEntity(e, valuesOnly, Family.Generic);

        /// <summary>
        /// A 64-bit hash of an entity's replicated values as <see cref="WriteEntity"/> writes them for
        /// <paramref name="family"/> (w3/audit: host-only families hash with their non-replicated references nulled).
        /// </summary>
        public ulong HashEntity(Entity e, bool valuesOnly, Family family)
        {
            ulong hash = 1469598103934665603UL;
            EntityManager em = EntityManager;
            Func<Entity, bool> binder = DependencyBinder;
            DependencyBinder = null; // mod compatibility: hashing never binds
            _ws.NullRefs = LenientRefs(family);
            try
            {
                TypeHandler[] handlers = HandlersOf(e);
                for (int i = 0; i < handlers.Length; i++)
                {
                    TypeHandler h = handlers[i];
                    hash = Mix(hash, h.Hash);
                    // mod compatibility: a disabled enableable component differs from an enabled one
                    if (h.SyncEnabledBit && !em.IsComponentEnabled(e, h.Type)) hash = Mix(hash, h.Hash ^ 0xE0E0E0E0UL);
                    if (h.Kind == SyncComponentKind.Presence) continue;
                    if (!TrySerialize(h, e, out int length)) continue;
                    hash = Mix(hash, Core.Hashing.XxHash64.Hash(_managedScratch, 0, length));
                }
            }
            finally
            {
                DependencyBinder = binder;
                _ws.NullRefs = null;
            }
            return hash;
        }

        private static readonly Func<Entity, bool> AnyRef = _ => true;

        /// <summary>
        /// w3/audit: the references a family's structure records may null. Host-only families (events, chirps, journal
        /// entries) point at citizens, households and service requests, which do not replicate as entities; their
        /// other values must still arrive. Every other family keeps the strict rule (an unresolvable reference sends
        /// the component as presence only).
        /// </summary>
        private static Func<Entity, bool> LenientRefs(Family family) => FamilyClassifier.IsHostOnly(family) ? AnyRef : null;

        private static ulong Mix(ulong h, ulong v) => (h ^ v) * 1099511628211UL + (h >> 29);

        // ------------------------------------------------------------------ integrity and icon channels (feat/integrity)

        /// <summary>
        /// Hash of one component's replicated value as both peers can compute it (IntegritySync): the game bytes with net
        /// ids, and each derived key reduced to its kind, owner and prefab (index and position depend on each peer's own
        /// regeneration). False when the value is not replicated (it references something that cannot be named) or fails
        /// to serialize; the caller then hashes presence only, exactly as the record carried it.
        /// </summary>
        public bool TryHashValue(TypeHandler h, Entity e, out ulong hash)
        {
            hash = 0;
            if (h == null || h.Kind == SyncComponentKind.Presence) return false;
            // w3/agents: reverse buffers are compared as multisets (ARCH 4.2.4)
            if (h.TryHashAsSet(EntityManager, e, _ids, out hash)) return true;
            if (!TrySerialize(h, e, out int length)) return false;
            int head = _payload.Length;
            hash = Core.Hashing.XxHash64.Hash(_managedScratch, head, length - head);
            foreach (DerivedKey k in _ws.UsedKeys)
            {
                hash = Mix(hash, ((ulong)k.Kind << 32) | k.OwnerNetId);
                hash = Mix(hash, k.PrefabNetId);
            }
            return true;
        }

        /// <summary>A portable name of an entity: its net id, else a derived key (lanes, prefab sub-objects); false when neither exists.</summary>
        public bool TryGetPortableRef(Entity e, out uint netId, out DerivedKey key)
        {
            key = default;
            netId = _ids.GetNetId(e);
            if (netId != 0) return true;
            return e != Entity.Null && BuildKey(e, out key);
        }

        /// <summary>The local entity a derived key names, or <c>Entity.Null</c>.</summary>
        public Entity ResolveDerivedKey(in DerivedKey key) => ResolveKey(key);

        /// <summary>Derived key of an unbound lane or prefab sub-object whose owner is bound.</summary>
        private bool BuildKey(Entity e, out DerivedKey key)
        {
            key = default;
            EntityManager em = EntityManager;
            if (!em.Exists(e) || !em.HasComponent<Game.Common.Owner>(e)) return false;
            Entity owner = em.GetComponentData<Game.Common.Owner>(e).m_Owner;
            uint ownerId = _ids.GetNetId(owner);
            if (ownerId == 0) return false;
            uint prefabId = em.HasComponent<PrefabRef>(e) ? _ids.GetNetId(em.GetComponentData<PrefabRef>(e).m_Prefab) : 0;
            float3 position;
            int index = -1;
            if (em.HasComponent<Game.Net.Lane>(e) && em.HasComponent<Game.Net.Curve>(e))
            {
                key.Kind = DerivedKey.LaneKind;
                position = MathUtils.Position(em.GetComponentData<Game.Net.Curve>(e).m_Bezier, 0.5f);
                if (em.HasBuffer<Game.Net.SubLane>(owner))
                {
                    DynamicBuffer<Game.Net.SubLane> lanes = em.GetBuffer<Game.Net.SubLane>(owner, true);
                    for (int i = 0; i < lanes.Length; i++)
                    {
                        if (lanes[i].m_SubLane == e)
                        {
                            index = i;
                            break;
                        }
                    }
                }
            }
            else if (em.HasComponent<Game.Objects.Object>(e) && em.HasComponent<Game.Objects.Transform>(e))
            {
                key.Kind = DerivedKey.SubObjectKind;
                position = em.GetComponentData<Game.Objects.Transform>(e).m_Position;
                if (em.HasBuffer<Game.Objects.SubObject>(owner))
                {
                    DynamicBuffer<Game.Objects.SubObject> subs = em.GetBuffer<Game.Objects.SubObject>(owner, true);
                    for (int i = 0; i < subs.Length; i++)
                    {
                        if (subs[i].m_SubObject == e)
                        {
                            index = i;
                            break;
                        }
                    }
                }
            }
            else
            {
                return false;
            }
            key.OwnerNetId = ownerId;
            key.PrefabNetId = prefabId;
            key.Index = index;
            key.X = ZoneBlockKey.Quantize(position.x, 64f);
            key.Y = ZoneBlockKey.Quantize(position.y, 64f);
            key.Z = ZoneBlockKey.Quantize(position.z, 64f);
            return true;
        }

        private Entity ResolveKey(in DerivedKey key)
        {
            EntityManager em = EntityManager;
            Entity owner = _ids.GetEntity(key.OwnerNetId);
            if (owner == Entity.Null || !em.Exists(owner)) return Entity.Null;
            Entity prefab = _ids.GetEntity(key.PrefabNetId);
            const long Near = 64L * 64L;          // 1 m
            const long Far = 256L * 256L;         // 4 m
            if (key.Kind == DerivedKey.LaneKind)
            {
                if (!em.HasBuffer<Game.Net.SubLane>(owner)) return Entity.Null;
                DynamicBuffer<Game.Net.SubLane> lanes = em.GetBuffer<Game.Net.SubLane>(owner, true);
                Entity best = Entity.Null;
                long bestDistance = Far;
                for (int i = 0; i < lanes.Length; i++)
                {
                    Entity lane = lanes[i].m_SubLane;
                    if (!em.HasComponent<Game.Net.Curve>(lane)) continue;
                    if (prefab != Entity.Null && em.HasComponent<PrefabRef>(lane) && em.GetComponentData<PrefabRef>(lane).m_Prefab != prefab) continue;
                    long d = Distance(key, MathUtils.Position(em.GetComponentData<Game.Net.Curve>(lane).m_Bezier, 0.5f));
                    if (i == key.Index && d <= Near) return lane;
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = lane;
                    }
                }
                return best;
            }
            if (key.Kind == DerivedKey.SubObjectKind)
            {
                if (!em.HasBuffer<Game.Objects.SubObject>(owner)) return Entity.Null;
                DynamicBuffer<Game.Objects.SubObject> subs = em.GetBuffer<Game.Objects.SubObject>(owner, true);
                Entity best = Entity.Null;
                long bestDistance = Far;
                for (int i = 0; i < subs.Length; i++)
                {
                    Entity sub = subs[i].m_SubObject;
                    if (!em.HasComponent<Game.Objects.Transform>(sub)) continue;
                    if (prefab != Entity.Null && em.HasComponent<PrefabRef>(sub) && em.GetComponentData<PrefabRef>(sub).m_Prefab != prefab) continue;
                    long d = Distance(key, em.GetComponentData<Game.Objects.Transform>(sub).m_Position);
                    if (i == key.Index && d <= Near) return sub;
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = sub;
                    }
                }
                return best;
            }
            return Entity.Null;
        }

        private static long Distance(in DerivedKey key, float3 p)
        {
            long dx = key.X - ZoneBlockKey.Quantize(p.x, 64f);
            long dy = key.Y - ZoneBlockKey.Quantize(p.y, 64f);
            long dz = key.Z - ZoneBlockKey.Quantize(p.z, 64f);
            return dx * dx + dy * dy + dz * dz;
        }

        // ================================================================== prefabs

        /// <summary>Binds every prefab entity to a net id (host, at session start) and returns them.</summary>
        public List<(uint Id, Entity Prefab)> BindAllPrefabs()
        {
            var list = new List<(uint, Entity)>();
            EntityManager em = EntityManager;
            using (EntityQuery q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabData>() },
                Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IgnoreComponentEnabledState,
            }))
            {
                NativeArray<Entity> prefabs = q.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (Entity p in prefabs)
                    {
                        uint id = _ids.GetNetId(p);
                        if (id == 0)
                        {
                            id = _ids.BindNew(p, Family.Prefab);
                            if (id == 0) continue;
                        }
                        list.Add((id, p));
                    }
                }
                finally
                {
                    prefabs.Dispose();
                }
            }
            return list;
        }

        /// <summary>Host: makes sure a prefab referenced by a record is bound; writes its record when newly bound.</summary>
        public uint EnsurePrefab(Entity prefab, SyncBatch.Writer w)
        {
            if (prefab == Entity.Null) return 0;
            uint id = _ids.GetNetId(prefab);
            if (id != 0) return id;
            id = _ids.BindNew(prefab, Family.Prefab);
            if (id == 0) return 0;
            WritePrefab(w, id, prefab);
            return id;
        }

        public void WritePrefab(SyncBatch.Writer w, uint id, Entity prefab)
        {
            PrefabBase p;
            try
            {
                p = _prefabs.GetPrefab<PrefabBase>(prefab);
            }
            catch (Exception)
            {
                return;
            }
            if (p == null) return;
            PrefabID pid = p.GetPrefabID();
            _scratch.Clear();
            var inner = default(BinaryWriter);
            inner.Initialize(_writeContext, _scratch, _ids.WriterTable);
            pid.Serialize(inner);
            if (_managedScratch.Length < _scratch.Length) _managedScratch = new byte[_scratch.Length * 2];
            NativeArray<byte>.Copy(_scratch.AsArray(), 0, _managedScratch, 0, _scratch.Length);
            w.Prefab(id, p.name, _managedScratch, 0, _scratch.Length);
        }

        private void BindPrefab(SyncRecord rec, ApplyResult result)
        {
            byte[] data = rec.PrefabData ?? Array.Empty<byte>();
            if (TryBindPrefab(rec.NetId, data, out string pid))
            {
                _unresolvedPrefabs.Remove(rec.NetId);
                _unresolvedPrefabNames.Remove(rec.NetId);
                return;
            }
            // mod compatibility: remembered, so the binding is retried when a record needs this prefab (mods such as Find
            // It create prefabs at runtime, possibly after the join's prefab table arrived)
            _unresolvedPrefabs[rec.NetId] = data;
            _unresolvedPrefabNames[rec.NetId] = string.IsNullOrEmpty(pid) ? (rec.PrefabName ?? "#" + rec.NetId) : pid;
            if (_missingPrefabs.Add(rec.PrefabName ?? pid))
            {
                result.Problems.Add("missing prefab " + pid);
                _log.Warn(LogCategory.Replication, "the host uses a prefab this game does not have: " + pid + " (an asset or mod is missing)");
            }
        }

        /// <summary>Resolves an encoded <c>PrefabID</c> in this game's prefab system and binds it to <paramref name="netId"/>.</summary>
        private bool TryBindPrefab(uint netId, byte[] data, out string pidText)
        {
            pidText = "";
            var native = new NativeArray<byte>(Math.Max(1, data.Length), Allocator.Temp);
            try
            {
                NativeArray<byte>.Copy(data, native, data.Length);
                _position.Value = 0;
                var reader = default(BinaryReader);
                reader.Initialize(_readContext, native, _position, _ids.ReaderTable);
                PrefabID pid = default;
                pid.Deserialize(reader);
                pidText = pid.ToString();
                if (!_prefabs.TryGetPrefab(pid, out PrefabBase prefab)) return false;
                _ids.Bind(_prefabs.GetEntity(prefab), netId, Family.Prefab);
                return true;
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Replication, "prefab #" + netId + " could not be read: " + ex.Message);
                return false;
            }
            finally
            {
                native.Dispose();
            }
        }

        /// <summary>
        /// Mod compatibility: the asset a record's <c>PrefabRef</c> needs when this game cannot resolve it (null when the
        /// record has no prefab or its prefab is here). Such a record is never applied: an entity whose prefab is missing
        /// (or null) breaks every game system that reads its prefab data.
        /// </summary>
        private string MissingPrefab(SyncRecord r, int batchOffset)
        {
            TypeHandler prefabRef = TypeHandlers.Get<PrefabRef>();
            if (prefabRef == null) return null;
            foreach (SyncComponent c in r.Components)
            {
                if (c.TypeHash != prefabRef.Hash) continue;
                if (c.Kind != SyncComponentKind.Data) return "an asset the other player does not have";
                try
                {
                    var keys = new ByteReader(c.Data, c.Offset, c.Count);
                    if (keys.ReadVarUInt32() != 0) return null;
                    _position.Value = c.Offset - batchOffset + keys.Position;
                    var inner = default(BinaryReader);
                    inner.Initialize(_readContext, _batch, _position, _ids.ReaderTable);
                    inner.Read(out int index);
                    if (index <= 0) return index == 0 ? null : "an asset the other player does not have";
                    uint id = (uint)index;
                    Entity prefab = _ids.GetEntity(id);
                    if (prefab != Entity.Null && EntityManager.Exists(prefab)) return null;
                    if (_unresolvedPrefabs.TryGetValue(id, out byte[] encoded) && TryBindPrefab(id, encoded, out _))
                    {
                        _log.Info(LogCategory.Replication, "prefab " + _unresolvedPrefabNames[id] + " is available now");
                        _unresolvedPrefabs.Remove(id);
                        _unresolvedPrefabNames.Remove(id);
                        return null;
                    }
                    return _unresolvedPrefabNames.TryGetValue(id, out string name) ? name : "asset #" + id;
                }
                catch (Exception)
                {
                    return null; // malformed: the normal read reports it
                }
            }
            return null;
        }

        /// <summary>
        /// Mod compatibility (host validation of a proposal): true when an entity record's <c>PrefabRef</c> names no
        /// prefab the host has (the client used an asset or a mod's runtime prefab the host lacks, so it could not even
        /// name it). Reads the decoded record's own bytes, so it works before <see cref="Apply"/>.
        /// </summary>
        public bool NamesUnknownPrefab(SyncRecord r)
        {
            TypeHandler prefabRef = TypeHandlers.Get<PrefabRef>();
            if (prefabRef == null || r == null) return false;
            foreach (SyncComponent c in r.Components)
            {
                if (c.TypeHash != prefabRef.Hash) continue;
                if (c.Kind != SyncComponentKind.Data) return true;
                try
                {
                    var keys = new ByteReader(c.Data, c.Offset, c.Count);
                    if (keys.ReadVarUInt32() != 0) return false;
                    int at = c.Offset + keys.Position;
                    if (at + 4 > c.Offset + c.Count) return false;
                    int index = BitConverter.ToInt32(c.Data, at);
                    if (index == 0) return false;
                    if (index < 0) return true;
                    Entity prefab = _ids.GetEntity((uint)index);
                    return prefab == Entity.Null || !EntityManager.Exists(prefab);
                }
                catch (Exception)
                {
                    return false;
                }
            }
            return false;
        }

        private void NoteMissingAsset(string asset, uint netId, ApplyResult result)
        {
            _missingAssets.TryGetValue(asset, out int n);
            _missingAssets[asset] = n + 1;
            if (n == 0) _log.Warn(LogCategory.Replication, "skipped #" + netId + " and every other object that uses " + asset + ": this asset is missing here (subscribe to the same asset packs and mods as the other player)");
            result.Problems.Add("#" + netId + " needs missing " + asset);
        }

        // ================================================================== applying

        /// <summary>
        /// Applies a batch. <paramref name="mayCreate"/> (host, for proposals) decides whether a spawn of an unknown id
        /// is allowed; null allows everything (client). The result is reused by the next apply.
        /// </summary>
        /// <param name="skip">Mod compatibility (client): entity records to leave alone this time (the local player is
        /// still dragging them with a direct-write tool; see <c>SyncController</c>'s edit hold). Despawns are never skipped.</param>
        public ApplyResult Apply(byte[] data, int offset, int count, Func<uint, Family, bool> mayCreate, Func<SyncRecord, bool> skip = null)
        {
            return Apply(SyncBatch.Read(data, offset, count), data, offset, count, mayCreate, skip);
        }

        /// <summary>
        /// Applies an already decoded batch (<paramref name="records"/> must come from <paramref name="data"/>, decoded once
        /// by the caller and shared with the other consumers of the batch). The returned result is owned by the codec and
        /// valid until the next apply.
        /// </summary>
        public ApplyResult Apply(List<SyncRecord> records, byte[] data, int offset, int count, Func<uint, Family, bool> mayCreate, Func<SyncRecord, bool> skip = null)
        {
            ResolveHandlers();
            ApplyResult result = _result;
            result.Reset();
            if (_batch.Length < count)
            {
                _batch.Dispose();
                _batch = new NativeArray<byte>(Math.Max(count, _batch.Length * 2), Allocator.Persistent);
            }
            NativeArray<byte>.Copy(data, offset, _batch, 0, count);
            EntityManager em = EntityManager;
            // review fix (direction check): mayCreate is given exactly when the host applies a client's proposal. Host →
            // client records (prefab bindings above all) and writes to host-owned bindings (movers, prefabs, the city
            // singleton) are refused: one Prefab record rebinds a host prefab to the client's id and evicts whatever
            // entity the host had bound to it.
            bool proposal = mayCreate != null;
            _targets.Clear();
            _spawned.Clear();
            _touch.Clear();
            _pathUpdated.Clear();

            // 0: prefabs and channel records
            for (int i = 0; i < records.Count; i++)
            {
                SyncRecord r = records[i];
                if (proposal && !SyncBatch.AcceptedFromClient(r.Kind))
                {
                    result.Problems.Add("ignored " + r.Kind + " from a client");
                    continue;
                }
                switch (r.Kind)
                {
                    case SyncRecordKind.Prefab:
                        BindPrefab(r, result);
                        break;
                    case SyncRecordKind.IdRange:
                        result.IdRanges.Add((r.RangeStart, r.RangeCount));
                        break;
                    case SyncRecordKind.Cost:
                        result.Cost += r.Amount;
                        result.HasCost = true;
                        break;
                    case SyncRecordKind.Clock:
                        result.Clock = r.Frame;
                        break;
                }
            }

            // 1: create the entities of spawns (all first, so references inside the batch resolve)
            for (int i = 0; i < records.Count; i++)
            {
                SyncRecord r = records[i];
                if (r.Kind != SyncRecordKind.Spawn && r.Kind != SyncRecordKind.Upsert && r.Kind != SyncRecordKind.Values) continue;
                if (skip != null && skip(r))
                {
                    result.Skipped++;
                    continue;
                }
                // mod compatibility: never apply a record whose asset is missing here (or was unnamed on the sender)
                string missing = MissingPrefab(r, offset);
                if (missing != null)
                {
                    result.Skipped++;
                    NoteMissingAsset(missing, r.NetId, result);
                    continue;
                }
                Entity e = _ids.GetEntity(r.NetId);
                if (e != Entity.Null && !em.Exists(e))
                {
                    // host: the id waits for a despawn on the wire before it can be reused (NetIdSystem)
                    _ids.Unbind(r.NetId);
                    e = Entity.Null;
                }
                if (e == Entity.Null)
                {
                    if (r.Kind != SyncRecordKind.Spawn && r.Kind != SyncRecordKind.Upsert)
                    {
                        result.Skipped++;
                        continue;
                    }
                    if (mayCreate != null && !mayCreate(r.NetId, r.Family))
                    {
                        result.Skipped++;
                        result.Problems.Add("refused to create #" + r.NetId);
                        continue;
                    }
                    e = Create(r, offset, result);
                    if (e == Entity.Null)
                    {
                        result.Skipped++;
                        continue;
                    }
                    _ids.Bind(e, r.NetId, r.Family);
                    if (_ids.GetEntity(r.NetId) != e)
                    {
                        // the id could not be bound (host: quarantined); never leave an unbound replica behind
                        em.DestroyEntity(e);
                        result.Skipped++;
                        result.Problems.Add("#" + r.NetId + " could not be bound");
                        continue;
                    }
                    _spawned.Add(e);
                    result.SpawnedEntities.Add(e);
                    result.Spawned++;
                }
                else if (proposal && !FamilyClassifier.IsReplicated(_ids.GetFamily(r.NetId)))
                {
                    result.Skipped++;
                    result.Problems.Add("refused to write #" + r.NetId + " (" + _ids.GetFamily(r.NetId) + ")");
                    continue;
                }
                else if (r.Kind == SyncRecordKind.Values)
                {
                    result.Values++;
                }
                else
                {
                    result.Upserted++;
                }
                _targets.Add((r, e));
            }

            // 2: reconcile the replicated component set (structural changes)
            for (int t = 0; t < _targets.Count; t++)
            {
                (SyncRecord r, Entity e) = _targets[t];
                if (r.Kind == SyncRecordKind.Values) continue;
                // gameplay branch: a client's edit of an existing entity never touches host-owned state (ARCH §5.2)
                bool keepHostOwned = proposal && !_spawned.Contains(e);
                _recordTypes.Clear();
                for (int c = 0; c < r.Components.Count; c++)
                {
                    TypeHandler h = TypeHandlers.GetByHash(r.Components[c].TypeHash);
                    if (h == null) continue;
                    _recordTypes.Add(h.Index.Value);
                    if (keepHostOwned && HostOwnedTypes.Contains(h.Name)) continue;
                    if (!em.HasComponent(e, h.Type)) em.AddComponent(e, h.Type);
                }
                if (r.CompleteSet)
                {
                    // the cached list is a snapshot of the archetype before the removals below
                    TypeHandler[] present = HandlersOf(e);
                    for (int i = 0; i < present.Length; i++)
                    {
                        TypeHandler h = present[i];
                        if (_recordTypes.Contains(h.Index.Value) || !em.HasComponent(e, h.Type)) continue;
                        if (keepHostOwned && HostOwnedTypes.Contains(h.Name)) continue;
                        em.RemoveComponent(e, h.Type);
                    }
                }
            }

            // 3: values
            for (int t = 0; t < _targets.Count; t++)
            {
                (SyncRecord r, Entity e) = _targets[t];
                bool path = false;
                bool keepHostOwned = proposal && !_spawned.Contains(e);
                for (int c = 0; c < r.Components.Count; c++)
                {
                    SyncComponent comp = r.Components[c];
                    if (comp.Kind == SyncComponentKind.Presence) continue;
                    TypeHandler h = TypeHandlers.GetByHash(comp.TypeHash);
                    if (h == null || h.Kind == SyncComponentKind.Presence || !em.HasComponent(e, h.Type)) continue;
                    if (keepHostOwned && HostOwnedTypes.Contains(h.Name)) continue;
                    // gameplay branch: state vanilla keeps beside some values (mesh colour enable bit, line colour events)
                    Type fixup = FixupType(h);
                    Game.Routes.Color oldColor = fixup == typeof(Game.Routes.Color) ? em.GetComponentData<Game.Routes.Color>(e) : default;
                    ReadComponent(h, e, comp, offset, data, result);
                    if (fixup != null)
                    {
                        bool changed = fixup != typeof(Game.Routes.Color) || !SameColor(oldColor, em.GetComponentData<Game.Routes.Color>(e));
                        try
                        {
                            ActionSync.AfterWrite(em, e, fixup, changed, _colorEventArchetype);
                        }
                        catch (Exception ex)
                        {
                            result.Problems.Add("fix-up of " + h.Name + ": " + ex.Message);
                        }
                    }
                    if (r.Family == Family.Segment && (h == _pathElement || h == _pathInformation)) path = true;
                }
                result.Touched.Add(e);
                if (path) _pathUpdated.Add(e);
            }

            // 3b (mod compatibility): enabled bits of enableable components, for the entities written above
            Dictionary<uint, Entity> written = null;
            for (int i = 0; i < records.Count; i++)
            {
                SyncRecord r = records[i];
                if (r.Kind != SyncRecordKind.EnabledBits || r.EnabledBits == null || (skip != null && skip(r))) continue;
                if (written == null)
                {
                    written = new Dictionary<uint, Entity>();
                    for (int t = 0; t < _targets.Count; t++) written[_targets[t].Item1.NetId] = _targets[t].Item2;
                }
                if (!written.TryGetValue(r.NetId, out Entity e) || !em.Exists(e)) continue;
                bool keepHostOwned = proposal && !_spawned.Contains(e);
                foreach (SyncEnabledBit bit in r.EnabledBits)
                {
                    TypeHandler h = TypeHandlers.GetByHash(bit.TypeHash);
                    if (h == null || !h.SyncEnabledBit || !em.HasComponent(e, h.Type)) continue;
                    if (keepHostOwned && HostOwnedTypes.Contains(h.Name)) continue;
                    try
                    {
                        if (em.IsComponentEnabled(e, h.Type) != bit.Enabled) em.SetComponentEnabled(e, h.Type, bit.Enabled);
                    }
                    catch (Exception ex)
                    {
                        result.Problems.Add("enabled bit of " + h.Name + ": " + ex.Message);
                    }
                }
            }

            // 4: tags: created entities already carry Created/Updated from their archetype; edits re-derive with closure
            for (int t = 0; t < _targets.Count; t++)
            {
                (SyncRecord r, Entity e) = _targets[t];
                if (r.Kind == SyncRecordKind.Values) continue;
                // w3/agents: nothing derives from agents; Updated on them would only wake systems for nothing
                if (r.Family == Family.Agent) continue;
                _touch.Add(e);
                if (r.Family == Family.NetEdge || r.Family == Family.NetNode) Closure(e, _touch);
            }
            foreach (Entity e in _spawned)
            {
                if (!em.HasComponent<Game.Common.Created>(e)) em.AddComponent<Game.Common.Created>(e);
            }

            // 5: despawns
            for (int i = 0; i < records.Count; i++)
            {
                SyncRecord r = records[i];
                if (r.Kind != SyncRecordKind.Despawn || (skip != null && skip(r))) continue;
                // mod compatibility: a client may delete vehicles, citizens and animals with a mod tool (Better Bulldozer)
                if (proposal && _ids.GetEntity(r.NetId) != Entity.Null && !FamilyClassifier.IsReplicated(_ids.GetFamily(r.NetId)) && _ids.GetFamily(r.NetId) != Family.Mover)
                {
                    result.Problems.Add("refused to delete #" + r.NetId + " (" + _ids.GetFamily(r.NetId) + ")");
                    continue;
                }
                Entity e = _ids.GetEntity(r.NetId);
                // w2/ids: the host keeps the binding of a proposed deletion; its capture sees the Deleted tag, broadcasts
                // the despawn to every peer and only then releases the id (unbinding here hid the despawn from the
                // other clients)
                if (!proposal) _ids.Unbind(r.NetId);
                if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                Family family = _families.Classify(em, e);
                if (family == Family.NetEdge || family == Family.NetNode) Closure(e, _touch);
                em.AddComponent<Game.Common.Deleted>(e);
                _touch.Remove(e);
                result.Touched.Add(e);
                result.Despawned++;
            }

            foreach (Entity e in _touch)
            {
                if (!em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                if (!em.HasComponent<Game.Common.Updated>(e)) em.AddComponent<Game.Common.Updated>(e);
                result.Touched.Add(e);
            }
            for (int i = 0; i < _pathUpdated.Count; i++) SynthesizePathUpdated(_pathUpdated[i]);
            return result;
        }

        private void ResolveHandlers()
        {
            if (_handlersResolved) return;
            _handlersResolved = true;
            _pathElement = HandlerByName("Game.Pathfind.PathElement");
            _pathInformation = HandlerByName("Game.Pathfind.PathInformation");
            _connected = TypeHandlers.Get<Game.Routes.Connected>();
            _prefabRef = TypeHandlers.Get<PrefabRef>();
            _journalEntry = HandlerByName("Game.Events.EventJournalEntry");
            _lifePathEvent = HandlerByName("Game.Triggers.LifePathEvent");
            _chirp = HandlerByName("Game.Triggers.Chirp");
        }

        private static TypeHandler HandlerByName(string name)
        {
            Type type = FamilyClassifier.FindType(name);
            if (type == null) return null;
            TypeIndex index = TypeManager.GetTypeIndex(type);
            return index == TypeIndex.Null ? null : TypeHandlers.Get(index);
        }

        /// <summary>
        /// gameplay branch: types a client's proposal never writes on an existing entity (ARCH §5.2: host-only types).
        /// Line state and initializer outputs are the host's (§4.11.3, §5.4.3: RouteNumber, VehicleModel); values the
        /// player edits through the UI travel as intents (<see cref="ActionSync"/>: line colour, vehicle models, service
        /// districts, mesh colour), so a tool edit of the same entity in the same round trip cannot revert them; the
        /// simulation buffers of buildings belong to the host's simulation. New entities keep everything (the host's
        /// initializers overwrite RouteNumber and VehicleModel of created lines themselves).
        /// </summary>
        private static readonly HashSet<string> HostOwnedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Game.Routes.RouteNumber",
            "Game.Routes.VehicleModel",
            "Game.Routes.Color",
            "Game.Routes.RouteLane",
            "Game.Routes.AccessLane",
            "Game.Routes.PathTargets",
            "Game.Routes.ConnectedRoute",
            "Game.Routes.RouteVehicle",
            "Game.Pathfind.PathInformation",
            "Game.Pathfind.PathElement",
            "Game.Areas.ServiceDistrict",
            "Game.Rendering.CustomMeshColor",
            "Game.Buildings.Renter",
            "Game.Buildings.Efficiency",
            "Game.Economy.Resources",
            "Game.Buildings.Patient",
            "Game.Buildings.Student",
            "Game.Buildings.Occupant",
            "Game.Companies.Employee",
            "Game.Vehicles.OwnedVehicle",
            "Game.Simulation.ServiceDispatch",
            "Game.Buildings.BuildingCondition",
            "Game.Buildings.CrimeProducer",
            "Game.Buildings.MailProducer",
            "Game.Buildings.GarbageProducer",
            "Game.Buildings.WaterConsumer",
            "Game.Buildings.ElectricityConsumer",
        };

        /// <summary>True when a client proposal may not write <paramref name="typeName"/> on an existing entity.</summary>
        public static bool IsHostOwned(string typeName) => typeName != null && HostOwnedTypes.Contains(typeName);

        private static Type FixupType(TypeHandler h)
        {
            if (h.Name == "Game.Rendering.CustomMeshColor") return typeof(Game.Rendering.CustomMeshColor);
            if (h.Name == "Game.Routes.Color") return typeof(Game.Routes.Color);
            return null;
        }

        private static bool SameColor(Game.Routes.Color a, Game.Routes.Color b) => a.m_Color.r == b.m_Color.r && a.m_Color.g == b.m_Color.g && a.m_Color.b == b.m_Color.b && a.m_Color.a == b.m_Color.a;

        private void ReadComponent(TypeHandler h, Entity e, in SyncComponent c, int batchOffset, byte[] data, ApplyResult result)
        {
            ByteReader keys = _keyReader;
            keys.Reset(data, c.Offset, c.Count);
            _rs.Keys.Clear();
            _rs.Unresolved = false;
            try
            {
                uint keyCount = keys.ReadVarUInt32();
                if (keyCount > 4096) throw new DecodeException("component with " + keyCount + " derived keys");
                for (uint k = 0; k < keyCount; k++)
                {
                    var key = new DerivedKey
                    {
                        Kind = keys.ReadByte(),
                        OwnerNetId = keys.ReadVarUInt32(),
                        PrefabNetId = keys.ReadVarUInt32(),
                        Index = keys.ReadVarInt32(),
                        X = keys.ReadVarInt32(),
                        Y = keys.ReadVarInt32(),
                        Z = keys.ReadVarInt32(),
                    };
                    _rs.Keys.Add(ResolveKey(key));
                }
                int start = c.Offset - batchOffset + keys.Position;
                int end = c.Offset - batchOffset + c.Count;
                _position.Value = start;
                _rs.Table = _ids.ReaderTable;
                var inner = default(BinaryReader);
                inner.Initialize(_readContext, _batch, _position, _rs.Table);
                h.Read(EntityManager, e, new NetReader(inner, _rs));
                if (_position.Value != end && _failedTypes.Add(h.Hash ^ 0x5A5A5A5AUL))
                {
                    _log.Warn(LogCategory.Replication, h.Name + ": read " + (_position.Value - start) + " of " + (end - start) + " bytes (format differs?)");
                }
            }
            catch (Exception ex)
            {
                result.Problems.Add(h.Name + ": " + ex.Message);
                if (_failedTypes.Add(h.Hash ^ 0xA5A5A5A5UL)) _log.Warn(LogCategory.Replication, "applying " + h.Name + " failed", ex);
            }
        }

        private Entity Create(SyncRecord r, int batchOffset, ApplyResult result)
        {
            EntityManager em = EntityManager;
            Entity prefab = ReadPrefab(r, batchOffset);
            EntityArchetype archetype = default;
            try
            {
                archetype = PrefabArchetype(r, prefab);
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Replication, "no prefab archetype for #" + r.NetId + ": " + ex.Message);
            }
            if (!archetype.Valid)
            {
                var types = new List<ComponentType> { ComponentType.ReadWrite<Game.Common.Created>(), ComponentType.ReadWrite<Game.Common.Updated>() };
                foreach (SyncComponent c in r.Components)
                {
                    TypeHandler h = TypeHandlers.GetByHash(c.TypeHash);
                    if (h != null) types.Add(h.Type);
                }
                if (types.Count <= 2)
                {
                    result.Problems.Add("#" + r.NetId + " has no known components");
                    return Entity.Null;
                }
                archetype = em.CreateArchetype(types.ToArray());
            }
            return em.CreateEntity(archetype);
        }

        private Entity ReadPrefab(SyncRecord r, int batchOffset)
        {
            TypeHandler prefabRef = _prefabRef;
            for (int i = 0; i < r.Components.Count; i++)
            {
                SyncComponent c = r.Components[i];
                if (prefabRef == null || c.TypeHash != prefabRef.Hash || c.Kind != SyncComponentKind.Data) continue;
                ByteReader keys = _keyReader;
                keys.Reset(c.Data, c.Offset, c.Count);
                if (keys.ReadVarUInt32() != 0) return Entity.Null;
                _position.Value = c.Offset - batchOffset + keys.Position;
                _rs.Keys.Clear();
                _rs.Table = _ids.ReaderTable;
                var inner = default(BinaryReader);
                inner.Initialize(_readContext, _batch, _position, _rs.Table);
                PrefabRef value = default;
                value.Deserialize(new NetReader(inner, _rs));
                return value.m_Prefab;
            }
            return Entity.Null;
        }

        private static bool HasComponent(SyncRecord r, TypeHandler h)
        {
            if (h == null) return false;
            for (int i = 0; i < r.Components.Count; i++)
            {
                if (r.Components[i].TypeHash == h.Hash) return true;
            }
            return false;
        }

        private EntityArchetype PrefabArchetype(SyncRecord r, Entity prefab)
        {
            EntityManager em = EntityManager;
            if (prefab == Entity.Null || !em.Exists(prefab)) return default;
            switch (r.Family)
            {
                case Family.NetNode:
                    return em.HasComponent<NetData>(prefab) ? em.GetComponentData<NetData>(prefab).m_NodeArchetype : default;
                case Family.NetEdge:
                    return em.HasComponent<NetData>(prefab) ? em.GetComponentData<NetData>(prefab).m_EdgeArchetype : default;
                case Family.Object:
                    return em.HasComponent<ObjectData>(prefab) ? em.GetComponentData<ObjectData>(prefab).m_Archetype : default;
                case Family.Area:
                    return em.HasComponent<AreaData>(prefab) ? em.GetComponentData<AreaData>(prefab).m_Archetype : default;
                case Family.Aggregate:
                    return em.HasComponent<AggregateNetData>(prefab) ? em.GetComponentData<AggregateNetData>(prefab).m_Archetype : default;
                case Family.Route:
                    return em.HasComponent<RouteData>(prefab) ? em.GetComponentData<RouteData>(prefab).m_RouteArchetype : default;
                case Family.Waypoint:
                    if (!em.HasComponent<RouteData>(prefab)) return default;
                    bool isConnected = HasComponent(r, _connected);
                    return isConnected ? em.GetComponentData<RouteData>(prefab).m_ConnectedArchetype : em.GetComponentData<RouteData>(prefab).m_WaypointArchetype;
                case Family.Segment:
                    return em.HasComponent<RouteData>(prefab) ? em.GetComponentData<RouteData>(prefab).m_SegmentArchetype : default;
                case Family.Event:
                    // w3/audit: journal entries and life-path events carry their event's prefab; vanilla creates them
                    // from ad-hoc archetypes, which the record's own type set reproduces (generic fallback)
                    if (HasComponent(r, _journalEntry) || HasComponent(r, _lifePathEvent)) return default;
                    if (HasComponent(r, _chirp))
                    {
                        if (em.HasComponent<ChirpData>(prefab)) return em.GetComponentData<ChirpData>(prefab).m_Archetype;
                        return em.HasComponent<LifePathEventData>(prefab) ? em.GetComponentData<LifePathEventData>(prefab).m_ChirpArchetype : default;
                    }
                    return em.HasComponent<EventData>(prefab) ? em.GetComponentData<EventData>(prefab).m_Archetype : default;
                case Family.Agent:
                    // w3/agents: citizens, households, pets and companies are created from the ArchetypeData of their
                    // prefab, exactly as BirthSystem, HouseholdSpawnSystem and the company spawn systems create them
                    return em.HasComponent<ArchetypeData>(prefab) ? em.GetComponentData<ArchetypeData>(prefab).m_Archetype : default;
                default:
                    return default;
            }
        }

        /// <summary>Tags the net closure (MoveIt's rule): an edge's nodes and their edges, a node's edges and their nodes.</summary>
        private void Closure(Entity e, HashSet<Entity> touch)
        {
            EntityManager em = EntityManager;
            if (em.HasComponent<Game.Net.Edge>(e))
            {
                Game.Net.Edge edge = em.GetComponentData<Game.Net.Edge>(e);
                NodeClosure(edge.m_Start, touch);
                NodeClosure(edge.m_End, touch);
            }
            else if (em.HasComponent<Game.Net.Node>(e))
            {
                NodeClosure(e, touch);
            }
            if (em.HasComponent<Game.Net.Aggregated>(e))
            {
                Entity aggregate = em.GetComponentData<Game.Net.Aggregated>(e).m_Aggregate;
                if (aggregate != Entity.Null && em.Exists(aggregate)) touch.Add(aggregate);
            }
        }

        private void NodeClosure(Entity node, HashSet<Entity> touch)
        {
            EntityManager em = EntityManager;
            if (node == Entity.Null || !em.Exists(node)) return;
            touch.Add(node);
            if (!em.HasBuffer<Game.Net.ConnectedEdge>(node)) return;
            DynamicBuffer<Game.Net.ConnectedEdge> edges = em.GetBuffer<Game.Net.ConnectedEdge>(node, true);
            for (int i = 0; i < edges.Length; i++)
            {
                Entity edge = edges[i].m_Edge;
                if (edge == Entity.Null || !em.Exists(edge)) continue;
                touch.Add(edge);
                if (!em.HasComponent<Game.Net.Edge>(edge)) continue;
                Game.Net.Edge data = em.GetComponentData<Game.Net.Edge>(edge);
                if (data.m_Start != node && em.Exists(data.m_Start)) touch.Add(data.m_Start);
                if (data.m_End != node && em.Exists(data.m_End)) touch.Add(data.m_End);
            }
        }

        // ================================================================== opaque blobs (GlobalStateSync)

        /// <summary>
        /// Serializes arbitrary game state (for example a system's own <c>ISerializable</c>) with the codec's full-tag
        /// save context; entity references become net ids. Returns null when the state references an entity that has
        /// no net id (derived keys are not available in blobs).
        /// </summary>
        public byte[] WriteBlob(Action<NetWriter> write)
        {
            NetWriterState.KeyBuilder keys = _ws.Keys;
            _scratch.Clear();
            _ws.BeginComponent();
            _ws.Table = _ids.WriterTable;
            _ws.Keys = null;
            try
            {
                var inner = default(BinaryWriter);
                inner.Initialize(_writeContext, _scratch, _ws.Table);
                write(new NetWriter(inner, _ws));
                if (_ws.Unresolved) return null;
                var bytes = new byte[_scratch.Length];
                if (_scratch.Length > 0) NativeArray<byte>.Copy(_scratch.AsArray(), 0, bytes, 0, _scratch.Length);
                return bytes;
            }
            finally
            {
                _ws.Keys = keys;
            }
        }

        /// <summary>
        /// Deserializes a blob written by <see cref="WriteBlob"/> with the codec's full-tag load context (R11 §5.3: the
        /// systems' runtime <c>Deserialize</c> needs every format tag). False when the reader did not consume exactly
        /// the blob or a reference did not resolve; exceptions of <paramref name="read"/> propagate.
        /// </summary>
        public bool ReadBlob(byte[] data, int offset, int count, Action<NetReader> read)
        {
            var native = new NativeArray<byte>(Math.Max(1, count), Allocator.Temp);
            try
            {
                if (count > 0) NativeArray<byte>.Copy(data, offset, native, 0, count);
                _position.Value = 0;
                _rs.Keys.Clear();
                _rs.Unresolved = false;
                _rs.Table = _ids.ReaderTable;
                var inner = default(BinaryReader);
                inner.Initialize(_readContext, native, _position, _rs.Table);
                read(new NetReader(inner, _rs));
                return _position.Value == count && !_rs.Unresolved;
            }
            finally
            {
                native.Dispose();
            }
        }

        /// <summary>
        /// The event the host's pathfinder produced for a segment (ARCH §4.11.3): its consumers rebuild the line's
        /// curves, search tree and render buffers from the host's path.
        /// </summary>
        private void SynthesizePathUpdated(Entity segment)
        {
            EntityManager em = EntityManager;
            if (!em.Exists(segment) || !em.HasComponent<Game.Routes.Segment>(segment) || !em.HasComponent<Game.Common.Owner>(segment)) return;
            Entity route = em.GetComponentData<Game.Common.Owner>(segment).m_Owner;
            if (!em.Exists(route) || !em.HasBuffer<Game.Routes.RouteWaypoint>(route)) return;
            DynamicBuffer<Game.Routes.RouteWaypoint> waypoints = em.GetBuffer<Game.Routes.RouteWaypoint>(route, true);
            if (waypoints.Length == 0) return;
            int index = em.GetComponentData<Game.Routes.Segment>(segment).m_Index;
            if (index < 0 || index >= waypoints.Length) return;
            Entity a = waypoints[index].m_Waypoint;
            Entity b = waypoints[(index + 1) % waypoints.Length].m_Waypoint;
            if (!em.HasComponent<Game.Routes.Position>(a) || !em.HasComponent<Game.Routes.Position>(b)) return;
            var data = new Game.Pathfind.PathEventData
            {
                m_Position1 = em.GetComponentData<Game.Routes.Position>(a).m_Position,
                m_Position2 = em.GetComponentData<Game.Routes.Position>(b).m_Position,
            };
            Entity ev = em.CreateEntity(_pathEventArchetype);
            em.SetComponentData(ev, new Game.Pathfind.PathUpdated(segment, data));
        }
    }
}
