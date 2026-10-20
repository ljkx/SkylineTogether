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
        /// <summary>Stale bindings replaced (an id still bound here to an entity the host despawned and reused the id of).</summary>
        public int StaleReplaced;
        /// <summary>Spawns not created because a structural reference (owner, edge node, parent) is not here.</summary>
        public int UnresolvedSpawns;
        /// <summary>Components that kept (existing entity) or nulled (new entity) a reference that did not resolve.</summary>
        public int UnresolvedComponents;
        /// <summary>
        /// References made safe on entities that stay or are about to be deleted: a dead owner or edge node of a
        /// despawned entity, a middle connection of a kept edge to a node that is gone.
        /// </summary>
        public int UnsafeSkipped;
        /// <summary>
        /// Entities deleted because they would dangle (an edge whose node is gone or deleted, an entity whose owner is
        /// gone): never kept, never tagged for re-derivation (ARCHITECTURE.md#client-apply).
        /// </summary>
        public int DanglingDeleted;
        /// <summary>Client: references to despawned entities removed by <see cref="DeletionCascade"/>.</summary>
        public int Cascaded;
        public long Cost;
        public bool HasCost;
        public uint? Clock;
        /// <summary>The host's id frontier announced by this batch (0: none).</summary>
        public uint IdCeiling;
        public readonly List<(uint Start, uint Count)> IdRanges = new List<(uint, uint)>();
        /// <summary>Entities written or tagged by this apply (the client excludes them from its own capture).</summary>
        public readonly HashSet<Entity> Touched = new HashSet<Entity>();
        /// <summary>The entities this apply created (host: build-order re-base of committed edges).</summary>
        public readonly List<Entity> SpawnedEntities = new List<Entity>();
        public readonly List<string> Problems = new List<string>();
        /// <summary>Net ids of the entities deleted because they would dangle (a client asks the host for them again).</summary>
        public readonly List<uint> DanglingIds = new List<uint>();

        /// <summary>Clears the result for the next apply (the codec reuses one instance).</summary>
        public void Reset()
        {
            Spawned = Upserted = Values = Despawned = Skipped = ZoneBlocks = StaleReplaced = 0;
            UnresolvedSpawns = UnresolvedComponents = UnsafeSkipped = DanglingDeleted = Cascaded = 0;
            Cost = 0;
            HasCost = false;
            Clock = null;
            IdCeiling = 0;
            IdRanges.Clear();
            Touched.Clear();
            Problems.Clear();
            DanglingIds.Clear();
            SpawnedEntities.Clear();
        }

        public override string ToString() => "+" + Spawned + " ~" + Upserted + " v" + Values + " -" + Despawned + (Skipped > 0 ? " skipped " + Skipped : "") + (ZoneBlocks > 0 ? " zones " + ZoneBlocks : "")
                                             + (UnresolvedSpawns > 0 ? " unresolved spawns " + UnresolvedSpawns : "") + (DanglingDeleted > 0 ? " dangling deleted " + DanglingDeleted : "") + (UnsafeSkipped > 0 ? " sanitized " + UnsafeSkipped : "") + (Cascaded > 0 ? " cascaded " + Cascaded : "");
    }

    /// <summary>
    /// Turns entities into <see cref="SyncBatch"/> records and records back into entities (ARCHITECTURE.md#batch-encoding, #client-apply). The wire
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
        // where the host's non-base-game prefabs come from (client: by prefab id, and by missing asset name for
        // the reports; host: by prefab entity), the explanation of each missing asset, and the prefab count at the
        // last name fallback attempt of an id (retried only when a mod added prefabs since)
        private readonly Dictionary<uint, Core.Modding.AssetSource> _prefabSources = new Dictionary<uint, Core.Modding.AssetSource>();
        private readonly Dictionary<string, Core.Modding.AssetSource> _missingSources = new Dictionary<string, Core.Modding.AssetSource>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _missingTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, int> _fallbackTried = new Dictionary<uint, int>();
        private readonly Dictionary<Entity, Core.Modding.AssetSource> _sourceOf = new Dictionary<Entity, Core.Modding.AssetSource>();
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
        // per-archetype handler lists and pooled apply collections (no allocation per record)
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
        private TypeHandler _journalEntry, _lifePathEvent, _chirp; // archetypes of host-only families
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
        /// Mod compatibility: the missing assets grouped by where the host said they come from
        /// (<see cref="SyncRecordKind.PrefabSource"/>), with the number of distinct missing prefabs of each; prefabs
        /// without a source record count under an unknown source.
        /// </summary>
        public List<KeyValuePair<Core.Modding.AssetSource, int>> MissingBySource()
        {
            var counts = new Dictionary<Core.Modding.AssetSource, int>();
            var unknown = new Core.Modding.AssetSource();
            foreach (string asset in _missingAssets.Keys)
            {
                Core.Modding.AssetSource source = _missingSources.TryGetValue(asset, out Core.Modding.AssetSource s) && s != null ? s : unknown;
                counts.TryGetValue(source, out int n);
                counts[source] = n + 1;
            }
            return new List<KeyValuePair<Core.Modding.AssetSource, int>>(counts);
        }

        /// <summary>Mod compatibility: the one-line explanation of a missing asset (source and what to do).</summary>
        public string ExplainMissing(string asset)
        {
            if (_missingTexts.TryGetValue(asset ?? "", out string text)) return text;
            return asset + ": this asset is missing here (subscribe to the same asset packs and mods as the other player)";
        }

        /// <summary>
        /// Session end. The missing-asset report and the unresolved prefab ids belong to one host's
        /// session; kept, they showed (and toasted) the previous session's assets, and an unresolved id could bind a
        /// later session's record to the old host's prefab name.
        /// </summary>
        public void ResetSession()
        {
            _missingPrefabs.Clear();
            _missingAssets.Clear();
            _unresolvedPrefabs.Clear();
            _unresolvedPrefabNames.Clear();
            _prefabSources.Clear();
            _missingSources.Clear();
            _missingTexts.Clear();
            _fallbackTried.Clear();
            _sourceOf.Clear();
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

        /// <summary>
        /// Copies a received batch into the native read buffer, followed by <see cref="NetReaderState.Padding"/>
        /// zero bytes (the game's reader may read a few bytes past a payload before the bounds check of
        /// <see cref="NetReader"/> stops it; they must stay inside the allocation).
        /// </summary>
        private void LoadBatch(byte[] data, int offset, int count)
        {
            if (count < 0 || offset < 0 || data == null || offset > data.Length - count) throw new DecodeException("batch slice out of range");
            int needed = count + NetReaderState.Padding;
            if (_batch.Length < needed)
            {
                _batch.Dispose();
                _batch = new NativeArray<byte>(Math.Max(needed, _batch.Length * 2), Allocator.Persistent);
            }
            NativeArray<byte>.Copy(data, offset, _batch, 0, count);
            for (int i = count; i < needed; i++) _batch[i] = 0;
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
        public void WriteEntity(SyncBatch.Writer w, SyncRecordKind kind, uint netId, Family family, Entity e) => WriteEntityCore(w, kind, netId, family, e, false);

        /// <summary>
        /// Appends an entity record (<see cref="WriteEntity"/>) and returns the hash <see cref="HashEntity(Entity, bool, Family)"/>
        /// with <paramref name="hashFamily"/> would compute right after it, from the same serialization: the host's shadow of
        /// a value-scanned entity and a client's shadow of what it proposed were a second full serialization of the entity.
        /// Agents (simulation-lenient values) and a <paramref name="hashFamily"/> whose reference rule differs from the record's
        /// fall back to writing and hashing separately, so the result is always exactly the separate hash.
        /// </summary>
        public ulong WriteEntityHashed(SyncBatch.Writer w, SyncRecordKind kind, uint netId, Family family, Entity e, Family hashFamily)
        {
            if (family == Family.Agent || !ReferenceEquals(LenientRefs(family), LenientRefs(hashFamily)))
            {
                WriteEntityCore(w, kind, netId, family, e, false);
                return HashEntity(e, true, hashFamily);
            }
            return WriteEntityCore(w, kind, netId, family, e, true);
        }

        /// <summary>Writes the record; with <paramref name="hash"/> also mixes the value hash exactly as <see cref="HashEntity(Entity, bool, Family)"/> does.</summary>
        private ulong WriteEntityCore(SyncBatch.Writer w, SyncRecordKind kind, uint netId, Family family, Entity e, bool hash)
        {
            TypeHandler[] handlers = HandlersOf(e);
            EntityManager em = EntityManager;
            ulong valueHash = HashSeed;
            _ids.TryGetGeneration(netId, out byte generation); // 0 when a client does not know it (the host ignores it)
            w.BeginEntity(kind, netId, family, kind != SyncRecordKind.Values, generation);
            // events and chirps keep their values when they name an entity that does not replicate (a citizen
            // sender, a household target): that reference travels as null instead of dropping the whole component
            _ws.NullRefs = LenientRefs(family);
            // references of agents to transient agents (job and school seekers, meetings), service requests and
            // icons never replicate; they travel as null so the values beside them are not lost (as simulation values do)
            bool lenient = family == Family.Agent;
            try
            {
                _bits.Clear();
                for (int i = 0; i < handlers.Length; i++)
                {
                    TypeHandler h = handlers[i];
                    if (h.SyncEnabledBit)
                    {
                        bool enabled = em.IsComponentEnabled(e, h.Type);
                        _bits.Add(new SyncEnabledBit(h.Hash, enabled));
                        if (hash)
                        {
                            valueHash = Mix(valueHash, h.Hash);
                            if (!enabled) valueHash = Mix(valueHash, h.Hash ^ DisabledMarker);
                        }
                    }
                    else if (hash)
                    {
                        valueHash = Mix(valueHash, h.Hash);
                    }
                    if (h.Kind == SyncComponentKind.Presence)
                    {
                        if (kind != SyncRecordKind.Values) w.AddComponent(h.Hash, SyncComponentKind.Presence, null, 0, 0);
                        continue;
                    }
                    // an agent link to something gone travels the way the reader's repair leaves it (left out: the reader
                    // removes the component), so a repaired client and the host agree (AgentLinkNormalization)
                    if (lenient && OmitsAgentLink(h, e)) continue;
                    int length;
                    bool ok = lenient && h.Kind == SyncComponentKind.Data ? TrySerializeSim(h, e, out length) : TrySerialize(h, e, out length);
                    if (ok)
                    {
                        w.AddComponent(h.Hash, h.Kind, _managedScratch, 0, length);
                        if (hash) valueHash = Mix(valueHash, Core.Hashing.XxHash64.Hash(_managedScratch, 0, length));
                    }
                    else if (kind != SyncRecordKind.Values)
                    {
                        w.AddComponent(h.Hash, SyncComponentKind.Presence, null, 0, 0);
                    }
                }
            }
            finally
            {
                _ws.NullRefs = null;
            }
            w.EndEntity();
            // mod compatibility: enabled bits travel right after the record (the vanilla Decoration of trees placed outside
            // decoration mode, Tree Controller's LumberResource)
            if (_bits.Count > 0) w.EnabledBits(netId, _bits);
            return valueHash;
        }

        private Dictionary<int, AgentLink> _forwardLinks;
        private AgentLinkAccess _linkView;

        /// <summary>The forward agent link a component type is (<see cref="AgentLinkGraph.Forward"/>), if any.</summary>
        internal bool TryGetForwardLink(TypeHandler h, out AgentLink link)
        {
            if (_forwardLinks == null)
            {
                _forwardLinks = new Dictionary<int, AgentLink>();
                foreach (AgentLink f in AgentLinkGraph.Forward) _forwardLinks[AgentLinkAccess.TypeOf(f).TypeIndex.Value] = f;
            }
            return _forwardLinks.TryGetValue(h.Index.Value, out link);
        }

        /// <summary>
        /// True when an agent's forward link component is left out of its record (<see cref="AgentLinkNormalization.ForwardLink"/>):
        /// its target is gone or cannot be named, and the reader's repair would remove the component (a workplace, a school,
        /// a property that is gone).
        /// </summary>
        internal bool OmitsAgentLink(TypeHandler h, Entity e)
        {
            if (!TryGetForwardLink(h, out AgentLink link)) return false;
            AgentLinkAccess view = _linkView ?? (_linkView = new AgentLinkAccess(EntityManager));
            view.EntityManager = EntityManager;
            if (!view.TryGetForward(e, link, out Entity target)) return false;
            LinkTarget state = view.StateOf(target);
            bool nameable = target != Entity.Null && _ids.GetNetId(target) != 0;
            return AgentLinkNormalization.ForwardLink(link, state, nameable) == ForwardInRecord.Omit;
        }

        /// <summary>The state of an agent link's target as the integrity hash sees it.</summary>
        internal LinkTarget LinkStateOf(TypeHandler h, Entity e)
        {
            if (!TryGetForwardLink(h, out AgentLink link)) return LinkTarget.Live;
            AgentLinkAccess view = _linkView ?? (_linkView = new AgentLinkAccess(EntityManager));
            view.EntityManager = EntityManager;
            return view.TryGetForward(e, link, out Entity target) ? view.StateOf(target) : LinkTarget.Live;
        }

        private const ulong HashSeed = 1469598103934665603UL;
        private const ulong DisabledMarker = 0xE0E0E0E0UL;

        /// <summary>
        /// Mod compatibility (<see cref="Compat.ModStateWatcher"/>): hashes of an entity's replicated state restricted to
        /// the types <paramref name="include"/> accepts (every replicated type when it is null): the presence of those
        /// types, and their values and (with <paramref name="bits"/>) enabled bits. Never binds dependencies.
        /// </summary>
        public void HashSubset(Entity e, Func<TypeIndex, bool> include, bool bits, out ulong presence, out ulong value)
        {
            presence = HashSeed;
            value = HashSeed;
            EntityManager em = EntityManager;
            // the archetype's cached handlers: the same types in the same order as GetComponentTypes, without its allocation
            TypeHandler[] handlers = HandlersOf(e);
            Func<Entity, bool> binder = DependencyBinder;
            DependencyBinder = null;
            try
            {
                for (int i = 0; i < handlers.Length; i++)
                {
                    TypeHandler h = handlers[i];
                    if (include != null && !include(h.Type.TypeIndex)) continue;
                    presence = Mix(presence, h.Hash);
                    if (bits && h.SyncEnabledBit && !em.IsComponentEnabled(e, h.Type)) value = Mix(value, h.Hash ^ DisabledMarker);
                    if (h.Kind == SyncComponentKind.Presence) continue;
                    if (!TrySerialize(h, e, out int length)) continue;
                    value = Mix(value, Core.Hashing.XxHash64.Hash(_managedScratch, 0, length));
                }
            }
            finally
            {
                DependencyBinder = binder;
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
        /// <paramref name="family"/> (host-only families hash with their non-replicated references nulled).
        /// </summary>
        public ulong HashEntity(Entity e, bool valuesOnly, Family family)
        {
            ulong hash = HashSeed;
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
                    if (h.SyncEnabledBit && !em.IsComponentEnabled(e, h.Type)) hash = Mix(hash, h.Hash ^ DisabledMarker);
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
        /// The references a family's structure records may null. Host-only families (events, chirps, journal
        /// entries) point at citizens, households and service requests, which do not replicate as entities; their
        /// other values must still arrive. Every other family keeps the strict rule (an unresolvable reference sends
        /// the component as presence only).
        /// </summary>
        private static Func<Entity, bool> LenientRefs(Family family) => FamilyClassifier.IsHostOnly(family) ? AnyRef : null;

        private static ulong Mix(ulong h, ulong v) => (h ^ v) * 1099511628211UL + (h >> 29);

        // ------------------------------------------------------------------ integrity and icon channels

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
            // reverse buffers are compared as multisets (ARCHITECTURE.md#agents)
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
            // where a prefab that is not base game comes from, so a client without it can say what is missing
            if (!_sourceOf.TryGetValue(prefab, out Core.Modding.AssetSource source)) _sourceOf[prefab] = source = Compat.AssetSources.Of(p);
            if (source != null) w.PrefabSource(id, source);
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
            string key = string.IsNullOrEmpty(pid) ? (rec.PrefabName ?? "#" + rec.NetId) : pid;
            _unresolvedPrefabNames[rec.NetId] = key;
            // the host's source record says where it comes from and what the player can do about it
            _prefabSources.TryGetValue(rec.NetId, out Core.Modding.AssetSource source);
            _missingSources[key] = source;
            _missingTexts[key] = Core.Modding.MissingAssets.Describe(key, rec.PrefabName, source);
            if (_missingPrefabs.Add(rec.PrefabName ?? pid))
            {
                result.Problems.Add("missing prefab " + pid);
                _log.Warn(LogCategory.Replication, "the host uses a prefab this game does not have: " + _missingTexts[key]);
            }
        }

        /// <summary>Resolves an encoded <c>PrefabID</c> in this game's prefab system and binds it to <paramref name="netId"/>.</summary>
        private bool TryBindPrefab(uint netId, byte[] data, out string pidText)
        {
            pidText = "";
            var native = new NativeArray<byte>(data.Length + NetReaderState.Padding, Allocator.Temp, NativeArrayOptions.ClearMemory);
            try
            {
                NativeArray<byte>.Copy(data, native, data.Length);
                _position.Value = 0;
                var reader = default(BinaryReader);
                reader.Initialize(_readContext, native, _position, _ids.ReaderTable);
                // a prefab id holds strings whose lengths come from the wire; the bounded reader refuses them
                // before the game's reader allocates or reads past the record
                _rs.Bound(_position, data.Length);
                _rs.Keys.Clear();
                _rs.Table = _ids.ReaderTable;
                PrefabID pid = default;
                pid.Deserialize(new NetReader(reader, _rs));
                pidText = pid.ToString();
                if (!_prefabs.TryGetPrefab(pid, out PrefabBase prefab))
                {
                    prefab = NameFallback(netId, pid, pidText);
                    if (prefab == null) return false;
                }
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
        /// The name fallback of prefab binding. A prefab a mod creates at runtime is the same prefab on every peer
        /// when its type and name are, even if its <c>PrefabID</c> hash differs (the mod reused a local file as its asset);
        /// bound only when unambiguous (Core <c>MissingAssets.ChooseNameFallback</c>). Tried again for an id only after a
        /// mod added prefabs (the prefab count changed), so records of a missing asset cost a dictionary lookup.
        /// </summary>
        private PrefabBase NameFallback(uint netId, PrefabID pid, string pidText)
        {
            int count = Compat.AssetSources.PrefabCount(_prefabs);
            if (count < 0 || (_fallbackTried.TryGetValue(netId, out int tried) && tried == count)) return null;
            _fallbackTried[netId] = count;
            string name = pid.GetName();
            if (!Core.Modding.MissingAssets.TryParsePrefabId(pidText, name, out string type, out bool hostHasHash)) return null;
            PrefabBase match = Compat.AssetSources.FindByName(_prefabs, type, name, hostHasHash);
            if (match != null) _log.Info(LogCategory.Replication, "prefab " + pidText + " bound by type and name to " + match.GetPrefabID() + " (a runtime prefab with a different hash)");
            return match;
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
                    ByteReader keys = _keyReader; // reused (was one reader per record)
                    keys.Reset(c.Data, c.Offset, c.Count);
                    if (keys.ReadVarUInt32() != 0) return null;
                    _position.Value = c.Offset - batchOffset + keys.Position;
                    if (_position.Value < 0 || _position.Value + 4 > c.Offset - batchOffset + c.Count) return null; // malformed, the normal read reports it
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
        /// name it). Reads the decoded record's own bytes, so it works before it is applied.
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
            if (n == 0) _log.Warn(LogCategory.Replication, "skipped #" + netId + " and every other object that uses a missing asset: " + ExplainMissing(asset));
            if (n < 3) result.Problems.Add("#" + netId + " needs missing " + asset); // counted per asset; a string only for the first few
        }

        // ================================================================== applying

        /// <summary>
        /// Applies a batch. <paramref name="mayCreate"/> (host, for proposals) decides whether a spawn of an unknown id
        /// is allowed; null allows everything (client). The result is reused by the next apply.
        /// </summary>
        /// <param name="data">The batch buffer.</param>
        /// <param name="offset">Start of the batch in <paramref name="data"/>.</param>
        /// <param name="count">Length of the batch.</param>
        /// <param name="mayCreate">Whether an unknown id may be spawned; null allows everything.</param>
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
            LoadBatch(data, offset, count);
            EntityManager em = EntityManager;
            // direction check: mayCreate is given exactly when the host applies a client's proposal. Host →
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
                    case SyncRecordKind.PrefabSource:
                        if (r.Source != null) _prefabSources[r.NetId] = r.Source; // written right before its Prefab record
                        break;
                    case SyncRecordKind.Prefab:
                        BindPrefab(r, result);
                        break;
                    case SyncRecordKind.IdRange:
                        result.IdRanges.Add((r.RangeStart, r.RangeCount));
                        if (!proposal) _ids.Ceiling.AnnounceRange(r.RangeStart, r.RangeCount); // a grant lies at the host's frontier
                        break;
                    case SyncRecordKind.Cost:
                        result.Cost += r.Amount;
                        result.HasCost = true;
                        break;
                    case SyncRecordKind.Clock:
                        result.Clock = r.Frame;
                        break;
                    case SyncRecordKind.IdCeiling:
                        // before the entity steps, so this batch's new ids are within it
                        if (!proposal) _ids.Ceiling.Announce(r.RangeStart);
                        result.IdCeiling = r.RangeStart;
                        break;
                }
            }

            // 0b: despawns of this batch. Their entities are dead to every reference read below (an upsert or a
            // new entity that names one would otherwise keep a reference to an entity deleted a moment later); a record of
            // an id the same batch despawns afterwards is not applied (it goes away anyway); a despawn that comes before
            // a spawn of the same id (an id reused within one batch) is applied first.
            _despawnAt.Clear();
            _rs.Dying.Clear();
            for (int i = 0; i < records.Count; i++)
            {
                SyncRecord r = records[i];
                if (r.Kind != SyncRecordKind.Despawn || _despawnAt.ContainsKey(r.NetId)) continue;
                _despawnAt[r.NetId] = i;
                Entity dying = _ids.GetEntity(r.NetId);
                if (dying != Entity.Null) _rs.Dying.Add(dying);
            }
            if (_despawnAt.Count > 0)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    SyncRecord r = records[i];
                    if (r.Kind != SyncRecordKind.Spawn || !_despawnAt.TryGetValue(r.NetId, out int at) || at > i) continue;
                    skip?.Invoke(records[at]); // despawns are never skipped; the callback forgets holds of the id
                    Despawn(records[at], proposal, result);
                    _despawnAt.Remove(r.NetId);
                    _earlyDespawns.Add(at);
                }
            }

            _spawnState.Clear();
            try
            {
                ApplySteps(records, offset, data, proposal, mayCreate, skip, result);
            }
            catch (Exception)
            {
                // a batch that throws outside the per-record guards never leaves a half-made entity behind
                // (no values, a null PrefabRef, zero edge nodes) for this frame's Modification systems
                RollBackUnwrittenSpawns(result);
                throw;
            }
            finally
            {
                _rs.Dying.Clear();
                _earlyDespawns.Clear();
                _spawnState.Clear();
            }
            return result;
        }

        /// <summary>The new entities of this apply: written completely, or failed (<see cref="SpawnTransaction{T}"/>).</summary>
        private readonly SpawnTransaction<Entity> _spawnState = new SpawnTransaction<Entity>();

        private readonly List<Entity> _rollBackScratch = new List<Entity>();
        private readonly HashSet<Entity> _dropScratch = new HashSet<Entity>();
        private long _rolledBackSpawnsTotal;

        /// <summary>New entities destroyed again because the apply failed before their values were written (diagnostics).</summary>
        public long RolledBackSpawnsTotal => _rolledBackSpawnsTotal;

        /// <summary>
        /// The apply threw: every entity it created whose values were not written is destroyed again and its id unbound
        /// (the integrity check asks for it again). Entities whose values were written stay (they are complete). Never throws.
        /// </summary>
        private void RollBackUnwrittenSpawns(ApplyResult result)
        {
            EntityManager em = EntityManager;
            _rollBackScratch.Clear();
            _spawnState.Incomplete(_spawned, _rollBackScratch);
            foreach (Entity e in _rollBackScratch)
            {
                try
                {
                    _spawned.Remove(e);
                    result.SpawnedEntities.Remove(e);
                    uint id = _ids.GetNetId(e);
                    if (id != 0)
                    {
                        _ids.Unbind(id);
                        _rs.MissingIds?.Add(id);
                        result.DanglingIds.Add(id);
                    }
                    if (em.Exists(e)) em.DestroyEntity(e); // created by this apply; nothing outside it refers to it
                    _rolledBackSpawnsTotal++;
                }
                catch (Exception ex)
                {
                    _log.Warn(LogCategory.Replication, "rolling back a new entity of a failed apply failed", ex);
                }
            }
            if (_rollBackScratch.Count > 0) result.Problems.Add(_rollBackScratch.Count + " new entities rolled back after the apply failed");
            _rollBackScratch.Clear();
        }

        /// <summary>
        /// Client: the apply of a host batch threw. The batch's despawns are what the host may reuse ids after (once this
        /// client acknowledges it), so each of <paramref name="ids"/> (<see cref="FailedBatchRecovery.DespawnsToApply"/>) is
        /// applied now: the id is unbound and its entity deleted cleanly, exactly as step 5 would have. Returns the
        /// number of those ids still bound afterwards (the batch must then never be acknowledged).
        /// </summary>
        public int SalvageDespawns(List<uint> ids)
        {
            ApplyResult result = _result;
            EntityManager em = EntityManager;
            int stillBound = 0;
            _touch.Clear();
            try
            {
                foreach (uint id in ids)
                {
                    try
                    {
                        Despawn(id, false, result);
                    }
                    catch (Exception)
                    {
                        // checked below
                    }
                    if (_ids.GetEntity(id) != Entity.Null) stillBound++;
                }
                DeleteDangling(false, result);
                foreach (Entity e in _touch)
                {
                    if (!em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                    if (!em.HasComponent<Game.Common.Updated>(e)) em.AddComponent<Game.Common.Updated>(e);
                    result.Touched.Add(e);
                }
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Replication, "applying the despawns of a failed batch failed", ex);
                stillBound = 0;
                foreach (uint id in ids)
                {
                    if (_ids.GetEntity(id) != Entity.Null) stillBound++;
                }
            }
            finally
            {
                _touch.Clear();
            }
            return stillBound;
        }

        /// <summary>Ids despawned by the batch being applied, with the index of their first despawn record.</summary>
        private readonly Dictionary<uint, int> _despawnAt = new Dictionary<uint, int>();

        /// <summary>True when the batch being applied (or the last one applied) despawns <paramref name="id"/>.</summary>
        public bool IsDespawning(uint id) => _despawnAt.ContainsKey(id);

        /// <summary>Despawn records already applied before the entity steps (id reuse within one batch).</summary>
        private readonly HashSet<int> _earlyDespawns = new HashSet<int>();

        private void ApplySteps(List<SyncRecord> records, int offset, byte[] data, bool proposal, Func<uint, Family, bool> mayCreate, Func<SyncRecord, bool> skip, ApplyResult result)
        {
            EntityManager em = EntityManager;
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
                if (_despawnAt.TryGetValue(r.NetId, out int despawnAt) && despawnAt > i)
                {
                    result.Skipped++; // despawned later in this batch
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
                else if (e != Entity.Null && em.HasComponent<Game.Common.Deleted>(e))
                {
                    // the entity is being deleted this frame; writing it (or creating a twin) would resurrect it
                    // with references the game's cleanup is about to invalidate
                    result.Skipped++;
                    continue;
                }
                else if (e != Entity.Null && !proposal && CheckBinding(r, e, offset) == BoundRecordVerdict.Replace)
                {
                    // a stale binding (the host reused the id for another entity): never written across entities
                    ReplaceStaleBinding(r, e, result);
                    e = Entity.Null;
                    if (r.Kind == SyncRecordKind.Values)
                    {
                        result.Skipped++;
                        result.DanglingIds.Add(r.NetId); // a values record cannot create it: asked for again
                        continue;
                    }
                }
                if (e == Entity.Null)
                {
                    if (r.Kind != SyncRecordKind.Spawn && r.Kind != SyncRecordKind.Upsert)
                    {
                        result.Skipped++;
                        continue;
                    }
                    if (!_ids.MayBind(r.NetId))
                    {
                        result.Skipped++; // beyond the host's announced id range (logged by NetIdSystem)
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
                    try
                    {
                        _ids.Bind(e, r.NetId, r.Family);
                    }
                    catch (Exception)
                    {
                        em.DestroyEntity(e); // never an unbound replica with default values
                        throw;
                    }
                    if (_ids.GetEntity(r.NetId) != e)
                    {
                        // the id could not be bound (host: quarantined); never leave an unbound replica behind
                        em.DestroyEntity(e);
                        result.Skipped++;
                        result.Problems.Add("#" + r.NetId + " could not be bound");
                        continue;
                    }
                    if (!proposal) _ids.NoteGeneration(r.NetId, r.Generation);
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
                // client: an id bound by its own prediction learns the host's generation from the host's record
                if (!proposal && !_spawned.Contains(e)) _ids.NoteGeneration(r.NetId, r.Generation);
                _targets.Add((r, e));
            }

            // 1b: new entities whose structural references (owner, edge nodes, parent) do not resolve here are
            // not created: the game's reference systems index them unchecked (SubObjectReferencesSystem, Net.ReferencesSystem)
            if (_spawned.Count > 0) DropUnresolvableSpawns(offset, data, result);

            // 2: reconcile the replicated component set (structural changes)
            for (int t = 0; t < _targets.Count; t++)
            {
                (SyncRecord r, Entity e) = _targets[t];
                if (r.Kind == SyncRecordKind.Values) continue;
                try
                {
                    Reconcile(r, e, proposal);
                }
                catch (Exception ex)
                {
                    result.Problems.Add("#" + r.NetId + " component set: " + ex.Message); // one record never stops the batch
                    if (_spawned.Contains(e)) _spawnState.Fail(e); // a new entity with a broken component set is not kept
                }
            }
            if (_spawnState.FailedCount > 0) DropFailedSpawns(result, "its component set could not be built");

            // 2b: no new entity keeps UpdateFrame without an update-group marker (the game's UpdateGroupSystem indexes
            // its Burst job with a table it does not have for it)
            if (_spawned.Count > 0) DropIncompleteSpawns(result);

            // 3: values
            for (int t = 0; t < _targets.Count; t++)
            {
                (SyncRecord r, Entity e) = _targets[t];
                try
                {
                    WriteValues(r, e, proposal, offset, data, result);
                    if (_spawned.Contains(e)) _spawnState.Written(e);
                }
                catch (Exception ex)
                {
                    result.Problems.Add("#" + r.NetId + " values: " + ex.Message);
                    if (_spawned.Contains(e)) _spawnState.Fail(e); // half-written values: never kept on a new entity
                }
            }
            // what referred to a dropped entity is cleaned up below (dangling owners and nodes) or by the reference audit
            if (_spawnState.FailedCount > 0) DropFailedSpawns(result, "its values could not be written");

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
                // nothing derives from agents; Updated on them would only wake systems for nothing
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
                if (r.Kind != SyncRecordKind.Despawn || (skip != null && skip(r)) || _earlyDespawns.Contains(i)) continue;
                Despawn(r, proposal, result);
            }

            // an entity that would dangle once this batch is applied (an edge whose node is gone or being deleted, an
            // entity whose owner is gone) is deleted cleanly instead of kept: the game's Burst jobs follow Edge,
            // ConnectedEdge and Owner unchecked whether the entity is tagged or not (a kept, untagged edge still crashed
            // the host). Deleting one adds its surviving node's closure, which is checked again.
            DeleteDangling(proposal, result);
            foreach (Entity e in _touch)
            {
                if (!em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                // Net.ReferencesSystem indexes an Updated edge's middle connections unchecked; one to a node that is gone
                // is dropped (the edge itself stays valid)
                if (DropDeadMiddleConnections(em, e))
                {
                    result.UnsafeSkipped++;
                    result.Problems.Add("dropped a middle connection to a node missing here: " + Describe(e));
                }
                if (!em.HasComponent<Game.Common.Updated>(e)) em.AddComponent<Game.Common.Updated>(e);
                result.Touched.Add(e);
            }
            if (result.UnresolvedSpawns > 0 || result.UnsafeSkipped > 0 || result.DanglingDeleted > 0) NoteUnsafe(result);
            for (int i = 0; i < _pathUpdated.Count; i++) SynthesizePathUpdated(_pathUpdated[i]);
        }

        /// <summary>Applies one despawn record (step 5, or before the entity steps when the batch reuses the id).</summary>
        private void Despawn(SyncRecord r, bool proposal, ApplyResult result) => Despawn(r.NetId, proposal, result);

        private void Despawn(uint netId, bool proposal, ApplyResult result)
        {
            EntityManager em = EntityManager;
            // mod compatibility: a client may delete vehicles, citizens and animals with a mod tool (Better Bulldozer)
            if (proposal && _ids.GetEntity(netId) != Entity.Null && !FamilyClassifier.IsReplicated(_ids.GetFamily(netId)) && _ids.GetFamily(netId) != Family.Mover)
            {
                result.Problems.Add("refused to delete #" + netId + " (" + _ids.GetFamily(netId) + ")");
                return;
            }
            Entity e = _ids.GetEntity(netId);
            // the host keeps the binding of a proposed deletion; its capture sees the Deleted tag, broadcasts
            // the despawn to every peer and only then releases the id (unbinding here hid the despawn from the
            // other clients)
            if (!proposal) _ids.Unbind(netId);
            if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) return;
            try
            {
                Family family = _families.Classify(em, e);
                if (family == Family.NetEdge || family == Family.NetNode) Closure(e, _touch);
                // a node goes with every edge that still ends at it: the records normally delete them too (the host
                // refuses a proposal that does not, StructureIntegrity), but an edge this peer has and the sender did not
                // know of (a client's own prediction in flight) would end at a destroyed node after this frame
                if (em.HasComponent<Game.Net.Node>(e)) CollectEdgesLeftAt(em, e);
                // the game's reference systems process a Deleted entity's owner and edge nodes unchecked
                if (SanitizeForDelete(em, e)) result.UnsafeSkipped++;
                // client: what the gated removal systems would do to the entity's renters, workers, pupils and
                // reverse lists, so nothing on this client keeps pointing at it while the host's own records travel
                if (!proposal)
                {
                    try
                    {
                        result.Cascaded += DeletionCascade.Apply(em, e, result.Touched);
                    }
                    catch (Exception ex)
                    {
                        result.Problems.Add("removal clean-up of #" + netId + ": " + ex.Message);
                    }
                }
                em.AddComponent<Game.Common.Deleted>(e);
                _touch.Remove(e);
                result.Touched.Add(e);
                result.Despawned++;
                for (int i = 0; i < _leftAtNode.Count; i++) DeleteDangling(_leftAtNode[i], proposal, result, "it ends at node #" + netId + ", which was deleted");
            }
            catch (Exception ex)
            {
                result.Problems.Add("despawn of #" + netId + ": " + ex.Message);
            }
            finally
            {
                _leftAtNode.Clear();
            }
        }

        private long _staleBindingsTotal;
        private double _lastStaleLog = double.NegativeInfinity;
        private int _staleNotLogged;

        /// <summary>Records written to an id this client still bound to an older entity, which was replaced (diagnostics).</summary>
        public long StaleBindingsTotal => _staleBindingsTotal;

        /// <summary>
        /// Client: whether an entity record describes the entity its id is bound to here (<see cref="BoundRecordCheck"/>):
        /// by the id's generation when known, else by the family and the prefab class.
        /// </summary>
        private BoundRecordVerdict CheckBinding(SyncRecord r, Entity e, int offset)
        {
            bool known = _ids.TryGetGeneration(r.NetId, out byte generation);
            Family bound = _ids.GetFamily(r.NetId);
            bool classes = false, same = true;
            if (!known && r.Family == bound && r.Kind != SyncRecordKind.Values) classes = TryComparePrefabClasses(r, e, offset, out same);
            return BoundRecordCheck.Check(r.Generation, known, generation, r.Family, bound, classes, same);
        }

        /// <summary>Whether the record's prefab and the entity's are of the same class; false when either is unknown.</summary>
        private bool TryComparePrefabClasses(SyncRecord r, Entity e, int offset, out bool same)
        {
            same = true;
            try
            {
                EntityManager em = EntityManager;
                if (!em.HasComponent<PrefabRef>(e)) return false;
                Entity bound = em.GetComponentData<PrefabRef>(e).m_Prefab;
                Entity named = ReadPrefab(r, offset);
                if (bound == Entity.Null || named == Entity.Null) return false;
                if (bound == named) return true;
                if (!em.Exists(bound) || !em.Exists(named)) return false;
                if (!_prefabs.TryGetPrefab(bound, out PrefabBase a) || !_prefabs.TryGetPrefab(named, out PrefabBase b) || a == null || b == null) return false;
                same = a.GetType() == b.GetType();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Client: the id of the record is still bound to an entity the host has despawned (a despawn this client lost): the
        /// old entity is despawned the way a despawn record does it (made safe, its removal clean-up, its closure tagged),
        /// so the record can create the new entity under the id. Components are never moved across entities.
        /// </summary>
        private void ReplaceStaleBinding(SyncRecord r, Entity old, ApplyResult result)
        {
            Family bound = _ids.GetFamily(r.NetId);
            bool known = _ids.TryGetGeneration(r.NetId, out byte generation);
            string what = Describe(old);
            _staleBindingsTotal++;
            result.StaleReplaced++;
            Despawn(r.NetId, false, result);
            if (_ids.GetEntity(r.NetId) == old) _ids.Unbind(r.NetId); // a refused or failed despawn still frees the id for the new entity
            double now = Diagnostics.Perf.Now;
            if (now - _lastStaleLog < 10.0)
            {
                _staleNotLogged++;
                return;
            }
            _lastStaleLog = now;
            _log.Warn(LogCategory.Replication, "the host's " + r.Kind + " of #" + r.NetId + " (" + r.Family + ", generation " + r.Generation + ") names an id still bound here to "
                                               + what + " (" + bound + (known ? ", generation " + generation : "") + "): a despawn was lost; the old entity is deleted"
                                               + (_staleNotLogged > 0 ? " (" + _staleNotLogged + " more since the last report)" : ""));
            _staleNotLogged = 0;
        }

        /// <summary>Edges that end at a node being despawned and that this batch does not despawn itself.</summary>
        private readonly List<Entity> _leftAtNode = new List<Entity>();

        private void CollectEdgesLeftAt(EntityManager em, Entity node)
        {
            _leftAtNode.Clear();
            if (!em.HasBuffer<Game.Net.ConnectedEdge>(node)) return;
            DynamicBuffer<Game.Net.ConnectedEdge> edges = em.GetBuffer<Game.Net.ConnectedEdge>(node, true);
            for (int i = 0; i < edges.Length; i++)
            {
                Entity edge = edges[i].m_Edge;
                if (edge == Entity.Null || !em.Exists(edge) || !em.HasComponent<Game.Net.Edge>(edge)) continue;
                if (em.HasComponent<Game.Common.Deleted>(edge) || em.HasComponent<Game.Tools.Temp>(edge)) continue;
                Game.Net.Edge ends = em.GetComponentData<Game.Net.Edge>(edge);
                if (ends.m_Start != node && ends.m_End != node) continue; // a middle connection: dropped by the game
                uint id = _ids.GetNetId(edge);
                if (id != 0 && _despawnAt.ContainsKey(id)) continue; // despawned by its own record of this batch
                if (!_leftAtNode.Contains(edge)) _leftAtNode.Add(edge);
            }
        }

        private readonly List<(Entity Entity, string Why)> _dangling = new List<(Entity, string)>();

        /// <summary>Deletes every entity of the touch set that would dangle (see <see cref="DanglingReason"/>), repeatedly.</summary>
        private void DeleteDangling(bool proposal, ApplyResult result)
        {
            EntityManager em = EntityManager;
            for (int round = 0; round < 8; round++)
            {
                _dangling.Clear();
                foreach (Entity e in _touch)
                {
                    // tool previews belong to the local tool, which regenerates them every frame
                    if (!em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e) || em.HasComponent<Game.Tools.Temp>(e)) continue;
                    string why = DanglingReason(em, e);
                    if (why != null) _dangling.Add((e, why));
                }
                if (_dangling.Count == 0) return;
                foreach ((Entity e, string why) in _dangling) DeleteDangling(e, proposal, result, why);
            }
            // never tagged: whatever still dangles after eight rounds leaves the touch set and is reported
            _dangling.Clear();
            foreach (Entity e in _touch)
            {
                if (em.Exists(e) && !em.HasComponent<Game.Common.Deleted>(e) && !em.HasComponent<Game.Tools.Temp>(e) && DanglingReason(em, e) != null) _dangling.Add((e, null));
            }
            foreach ((Entity e, string _) in _dangling)
            {
                _touch.Remove(e);
                result.Problems.Add("still dangling after repeated clean-up, not re-derived: " + Describe(e));
            }
            _dangling.Clear();
        }

        /// <summary>
        /// Why an entity that stays would dangle once this batch is applied, or null: an edge whose start or end node does
        /// not exist here, is no node, or is being deleted (it is destroyed at the end of the frame); an entity whose owner
        /// no longer exists.
        /// </summary>
        internal static string DanglingReason(EntityManager em, Entity e)
        {
            if (em.HasComponent<Game.Net.Edge>(e))
            {
                Game.Net.Edge edge = em.GetComponentData<Game.Net.Edge>(e);
                string start = NodeProblem(em, edge.m_Start), end = NodeProblem(em, edge.m_End);
                if (start != null) return "its start node " + start;
                if (end != null) return "its end node " + end;
            }
            if (em.HasComponent<Game.Common.Owner>(e))
            {
                Entity owner = em.GetComponentData<Game.Common.Owner>(e).m_Owner;
                if (owner != Entity.Null && !em.Exists(owner)) return "its owner is missing here";
            }
            // a line that kept its waypoint or segment list (a new list did not resolve here) while an element of the old
            // one is gone or deleted: the route systems index the elements unchecked
            if (em.HasBuffer<Game.Routes.RouteWaypoint>(e))
            {
                DynamicBuffer<Game.Routes.RouteWaypoint> waypoints = em.GetBuffer<Game.Routes.RouteWaypoint>(e, true);
                for (int i = 0; i < waypoints.Length; i++)
                {
                    if (!IsLiveElement(em, waypoints[i].m_Waypoint)) return "a waypoint of its line is missing or deleted here";
                }
            }
            if (em.HasBuffer<Game.Routes.RouteSegment>(e))
            {
                DynamicBuffer<Game.Routes.RouteSegment> segments = em.GetBuffer<Game.Routes.RouteSegment>(e, true);
                for (int i = 0; i < segments.Length; i++)
                {
                    if (!IsLiveElement(em, segments[i].m_Segment)) return "a segment of its line is missing or deleted here";
                }
            }
            return null;
        }

        private static bool IsLiveElement(EntityManager em, Entity e) => e == Entity.Null || (em.Exists(e) && !em.HasComponent<Game.Common.Deleted>(e));

        private static string NodeProblem(EntityManager em, Entity node)
        {
            if (node == Entity.Null) return "is null";
            if (!em.Exists(node)) return "is missing here";
            if (!em.HasBuffer<Game.Net.ConnectedEdge>(node)) return "is not a node";
            if (em.HasComponent<Game.Common.Deleted>(node)) return "is being deleted";
            return null;
        }

        /// <summary>
        /// Deletes an entity that would dangle, the way a despawn does: its references are made safe for the game's
        /// reference systems, a client removes what points at it (<see cref="DeletionCascade"/>) and unbinds its id (the
        /// integrity check asks the host for it again); the host keeps the binding, so its capture broadcasts the despawn.
        /// </summary>
        private void DeleteDangling(Entity e, bool proposal, ApplyResult result, string why)
        {
            EntityManager em = EntityManager;
            if (!em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) return;
            uint id = _ids.GetNetId(e);
            try
            {
                if (em.HasComponent<Game.Net.Edge>(e) || em.HasComponent<Game.Net.Node>(e)) Closure(e, _touch);
                SanitizeForDelete(em, e);
                if (!proposal)
                {
                    try
                    {
                        result.Cascaded += DeletionCascade.Apply(em, e, result.Touched);
                    }
                    catch (Exception ex)
                    {
                        result.Problems.Add("removal clean-up of " + Describe(e) + ": " + ex.Message);
                    }
                    if (id != 0) _ids.Unbind(id);
                }
                em.AddComponent<Game.Common.Deleted>(e);
                _touch.Remove(e);
                result.Touched.Add(e);
                result.DanglingDeleted++;
                _danglingTotal++;
                if (id != 0) result.DanglingIds.Add(id);
                result.Problems.Add("deleted " + (id != 0 ? "#" + id + " " : "") + e + " instead of keeping it dangling: " + why);
            }
            catch (Exception ex)
            {
                result.Problems.Add("deleting the dangling " + e + " failed: " + ex.Message);
            }
        }

        private string Describe(Entity e)
        {
            uint id = _ids.GetNetId(e);
            return id != 0 ? "#" + id + " " + e : e.ToString();
        }

        private void Reconcile(SyncRecord r, Entity e, bool proposal)
        {
            EntityManager em = EntityManager;
            {
                // a client's edit of an existing entity never touches host-owned state (ARCHITECTURE.md#client-proposals)
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
        }

        private void WriteValues(SyncRecord r, Entity e, bool proposal, int offset, byte[] data, ApplyResult result)
        {
            EntityManager em = EntityManager;
            {
                bool path = false;
                bool spawned = _spawned.Contains(e);
                bool keepHostOwned = proposal && !spawned;
                for (int c = 0; c < r.Components.Count; c++)
                {
                    SyncComponent comp = r.Components[c];
                    if (comp.Kind == SyncComponentKind.Presence) continue;
                    TypeHandler h = TypeHandlers.GetByHash(comp.TypeHash);
                    // a payload of another kind than this game's type (data vs buffer) would be misread
                    if (h == null || h.Kind == SyncComponentKind.Presence || h.Kind != comp.Kind || !em.HasComponent(e, h.Type)) continue;
                    if (keepHostOwned && HostOwnedTypes.Contains(h.Name)) continue;
                    // state vanilla keeps beside some values (mesh colour enable bit, line colour events)
                    Type fixup = FixupType(h);
                    Game.Routes.Color oldColor = fixup == typeof(Game.Routes.Color) ? em.GetComponentData<Game.Routes.Color>(e) : default;
                    ReadComponent(h, e, comp, offset, data, result, !spawned);
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
        }

        // ================================================================== reference safety

        private TypeHandler[] _hardTypes;
        private readonly Dictionary<Entity, List<Entity>> _spawnRefs = new Dictionary<Entity, List<Entity>>();
        private readonly Stack<List<Entity>> _refLists = new Stack<List<Entity>>();
        private readonly HashSet<Entity> _badSpawns = new HashSet<Entity>();
        private long _unresolvedSpawnsTotal, _unsafeTotal, _danglingTotal;
        private double _lastUnsafeLog = double.NegativeInfinity;

        private string _probeFailure;
        private long _malformedTotal;

        /// <summary>Component values refused as malformed (overruns, NaN or out-of-range geometry), session total.</summary>
        public long MalformedTotal => _malformedTotal;

        /// <summary>Reads the bounded reader refused because they ran past their payload (session total).</summary>
        public long OverrunsTotal => _rs.Overruns;

        /// <summary>Spawns skipped because a structural reference did not resolve (session total).</summary>
        public long UnresolvedSpawnsTotal => _unresolvedSpawnsTotal;

        /// <summary>References made safe (dead owners or nodes of despawned entities, dead middle connections), session total.</summary>
        public long UnsafeTotal => _unsafeTotal;

        /// <summary>Entities deleted because they would have dangled (session total).</summary>
        public long DanglingTotal => _danglingTotal;

        /// <summary>References the reader dropped because their entity was destroyed or deleted (session total).</summary>
        public long DeadReferencesTotal => _rs.DeadReferences;

        /// <summary>
        /// Client: while not null, the net ids of references that did not resolve because nothing is bound to them here
        /// are added to this list (<see cref="NetReaderState.MissingIds"/>; the reference audit asks the host for them).
        /// </summary>
        public List<uint> MissingIds
        {
            get => _rs.MissingIds;
            set => _rs.MissingIds = value;
        }

        /// <summary>
        /// The structural references of a new entity: without them the game's reference systems index nothing
        /// (<c>SubObjectReferencesSystem</c>: <c>m_SubObjects[owner]</c>; <c>Net.ReferencesSystem</c>: <c>m_Edges[edge.m_Start]</c>;
        /// attached objects follow their parent).
        /// </summary>
        private TypeHandler[] HardTypes()
        {
            if (_hardTypes != null) return _hardTypes;
            var list = new List<TypeHandler>();
            foreach (TypeHandler h in new[] { TypeHandlers.Get<Game.Common.Owner>(), TypeHandlers.Get<Game.Net.Edge>(), TypeHandlers.Get<Game.Objects.Attached>() })
            {
                if (h != null && h.CanProbe) list.Add(h);
            }
            _hardTypes = list.ToArray();
            return _hardTypes;
        }

        /// <summary>
        /// Step 1b: new entities of this apply whose owner, edge nodes or parent do not resolve here (unknown id, or bound to
        /// a destroyed or deleted entity), and the new entities that reference those, are destroyed again before any
        /// system saw them and their ids unbound; the integrity check asks for them again once their references exist.
        /// </summary>
        private void DropUnresolvableSpawns(int offset, byte[] data, ApplyResult result)
        {
            TypeHandler[] hard = HardTypes();
            if (hard.Length == 0) return;
            _badSpawns.Clear();
            _spawnRefs.Clear();
            try
            {
                EntityManager em0 = EntityManager;
                TypeHandler edgeType = TypeHandlers.Get<Game.Net.Edge>(), ownerType = TypeHandlers.Get<Game.Common.Owner>();
                for (int t = 0; t < _targets.Count; t++)
                {
                    (SyncRecord r, Entity e) = _targets[t];
                    if (!_spawned.Contains(e)) continue;
                    List<Entity> refs = _refLists.Count > 0 ? _refLists.Pop() : new List<Entity>();
                    refs.Clear();
                    _spawnRefs[e] = refs;
                    // a structural value the sender could not name travels as presence only, and the new entity would
                    // get a null owner or null edge nodes: a new edge needs its Edge value, an owner must be named
                    bool isEdge = em0.HasComponent<Game.Net.Edge>(e);
                    if (isEdge && !CarriesData(r, edgeType))
                    {
                        _badSpawns.Add(e);
                        result.Problems.Add("#" + r.NetId + " (" + r.Family + ") not created: its Game.Net.Edge value was not sent (the sender could not name a node)");
                    }
                    if (CarriesPresenceOnly(r, ownerType))
                    {
                        _badSpawns.Add(e);
                        result.Problems.Add("#" + r.NetId + " (" + r.Family + ") not created: its owner was not sent (the sender could not name it)");
                    }
                    // an agent needs its marker and its household here (a pet's household link is its update-group marker)
                    if (r.Family == Family.Agent && !AgentSpawnAllowed(r, offset, data, result))
                    {
                        _badSpawns.Add(e);
                        _rs.MissingIds?.Add(r.NetId); // asked for again once its household is here
                    }
                    for (int c = 0; c < r.Components.Count; c++)
                    {
                        SyncComponent comp = r.Components[c];
                        if (comp.Kind == SyncComponentKind.Presence) continue;
                        TypeHandler h = TypeHandlers.GetByHash(comp.TypeHash);
                        if (h == null || h.Kind != comp.Kind || !h.CanProbe) continue;
                        // geometry is checked too: a new entity at a NaN position is never created
                        bool structural = comp.Kind == SyncComponentKind.Data && Array.IndexOf(hard, h) >= 0;
                        if (!structural && !h.ChecksFloats) continue;
                        _probeFailure = null;
                        _rs.Collect = refs;
                        int before = refs.Count;
                        bool unresolved;
                        try
                        {
                            unresolved = ProbeComponent(h, comp, offset, data, result);
                        }
                        finally
                        {
                            _rs.Collect = null;
                        }
                        if (unresolved && (structural || _probeFailure != null))
                        {
                            _badSpawns.Add(e);
                            result.Problems.Add("#" + r.NetId + " (" + r.Family + ") not created: its " + h.Name
                                                + (_probeFailure != null ? " is malformed (" + _probeFailure + ")" : " references something missing here"));
                            if (_probeFailure != null) _malformedTotal++;
                        }
                        else if (h == edgeType && !unresolved)
                        {
                            // both ends must be nodes that stay: a null end resolves without being unresolved, and an id may
                            // name something that is no node here
                            int named = refs.Count - before;
                            bool nodes = named >= 2;
                            for (int k = before; nodes && k < refs.Count; k++) nodes = NodeProblem(em0, refs[k]) == null;
                            if (!nodes)
                            {
                                _badSpawns.Add(e);
                                result.Problems.Add("#" + r.NetId + " (" + r.Family + ") not created: its Game.Net.Edge does not name two nodes that exist here");
                            }
                        }
                    }
                }
                // a new entity whose owner or node is one of the dropped new entities goes too
                bool changed = _badSpawns.Count > 0;
                while (changed)
                {
                    changed = false;
                    foreach (KeyValuePair<Entity, List<Entity>> pair in _spawnRefs)
                    {
                        if (_badSpawns.Contains(pair.Key)) continue;
                        foreach (Entity referenced in pair.Value)
                        {
                            if (!_badSpawns.Contains(referenced)) continue;
                            _badSpawns.Add(pair.Key);
                            changed = true;
                            break;
                        }
                    }
                }
                if (_badSpawns.Count == 0) return;
                _unresolvedSpawnsTotal += RemoveSpawns(_badSpawns, result, false); // created by this apply; nothing references them yet
            }
            finally
            {
                foreach (List<Entity> refs in _spawnRefs.Values) _refLists.Push(refs);
                _spawnRefs.Clear();
                _badSpawns.Clear();
            }
        }

        private readonly List<SpawnComponent> _spawnComponents = new List<SpawnComponent>();
        private readonly Dictionary<EntityArchetype, bool> _completeArchetypes = new Dictionary<EntityArchetype, bool>();
        private ComponentType[] _markerTypes;
        private long _incompleteSpawnsTotal;

        /// <summary>New entities not kept because they had UpdateFrame and no update-group marker (diagnostics).</summary>
        public long IncompleteSpawnsTotal => _incompleteSpawnsTotal;

        /// <summary>
        /// The agent spawn rule (<see cref="AgentSpawnRules"/>): the record carries an agent marker as data, and a citizen's
        /// or pet's household resolves here. False (with the reason in the problems) when the agent is not created.
        /// </summary>
        private bool AgentSpawnAllowed(SyncRecord r, int offset, byte[] data, ApplyResult result)
        {
            _spawnComponents.Clear();
            for (int c = 0; c < r.Components.Count; c++)
            {
                SyncComponent comp = r.Components[c];
                TypeHandler h = TypeHandlers.GetByHash(comp.TypeHash);
                if (h == null) continue;
                bool isData = comp.Kind != SyncComponentKind.Presence && h.Kind == comp.Kind;
                bool resolved = true;
                if (isData && h.CanProbe && IsDefiningLink(h.Name))
                {
                    _probeFailure = null;
                    resolved = !ProbeComponent(h, comp, offset, data, result);
                }
                _spawnComponents.Add(new SpawnComponent(h.Name, isData, resolved));
            }
            AgentSpawnVerdict verdict = AgentSpawnRules.Check(_spawnComponents, out string type);
            _spawnComponents.Clear();
            if (verdict == AgentSpawnVerdict.Create) return true;
            string why = verdict == AgentSpawnVerdict.DefiningLinkMissing ? "its " + type + " names a household missing here"
                       : verdict == AgentSpawnVerdict.MarkerWithoutValue ? "its " + type + " was sent as presence only"
                       : "the record names no citizen, household, pet or company";
            result.Problems.Add("#" + r.NetId + " (Agent) not created: " + why);
            return false;
        }

        private static bool IsDefiningLink(string name)
        {
            foreach (string link in AgentSpawnRules.DefiningLinks)
            {
                if (link == name) return true;
            }
            return false;
        }

        /// <summary>True when the archetype has no UpdateFrame or one of the update-group markers (cached per archetype).</summary>
        internal bool IsUpdateGroupComplete(EntityArchetype archetype)
        {
            if (_completeArchetypes.TryGetValue(archetype, out bool complete)) return complete;
            if (_markerTypes == null)
            {
                var markers = new List<ComponentType>();
                foreach (string name in UpdateGroupMarkers.TypeNames)
                {
                    Type t = FamilyClassifier.FindType(name);
                    if (t != null) markers.Add(ComponentType.ReadOnly(t));
                }
                _markerTypes = markers.ToArray();
            }
            NativeArray<ComponentType> types = archetype.GetComponentTypes(Allocator.Temp);
            try
            {
                TypeIndex updateFrame = ComponentType.ReadOnly<Game.Simulation.UpdateFrame>().TypeIndex;
                bool hasFrame = false, hasMarker = false;
                foreach (ComponentType t in types)
                {
                    if (t.TypeIndex == updateFrame) hasFrame = true;
                    foreach (ComponentType m in _markerTypes)
                    {
                        if (m.TypeIndex == t.TypeIndex) hasMarker = true;
                    }
                }
                complete = !hasFrame || hasMarker;
            }
            finally
            {
                types.Dispose();
            }
            _completeArchetypes[archetype] = complete;
            return complete;
        }

        /// <summary>
        /// Step 2b: a new entity whose component set, after the reconcile, has UpdateFrame but no update-group marker is not
        /// kept (destroyed before any value refers to it; its id is released and asked for again), and named in the log
        /// with its record family, prefab and component types.
        /// </summary>
        private void DropIncompleteSpawns(ApplyResult result)
        {
            EntityManager em = EntityManager;
            _badSpawns.Clear();
            for (int t = 0; t < _targets.Count; t++)
            {
                (SyncRecord r, Entity e) = _targets[t];
                if (!_spawned.Contains(e) || !em.Exists(e) || _badSpawns.Contains(e)) continue;
                EntityArchetype archetype = em.GetChunk(e).Archetype;
                if (IsUpdateGroupComplete(archetype)) continue;
                var names = new List<string>();
                NativeArray<ComponentType> types = archetype.GetComponentTypes(Allocator.Temp);
                try
                {
                    foreach (ComponentType c in types) names.Add(TypeManager.GetType(c.TypeIndex)?.Name ?? c.TypeIndex.ToString());
                }
                finally
                {
                    types.Dispose();
                }
                names.Sort(StringComparer.Ordinal);
                Entity prefab = em.HasComponent<PrefabRef>(e) ? em.GetComponentData<PrefabRef>(e).m_Prefab : Entity.Null;
                string prefabName = prefab != Entity.Null && em.Exists(prefab) ? _prefabs.GetPrefabName(prefab) : "none";
                _log.Warn(LogCategory.Replication, "spawn of #" + r.NetId + " (" + r.Family + ", prefab " + prefabName + ") not kept: UpdateFrame without an update-group marker; components "
                                                   + string.Join(", ", names) + "; asked for again");
                _badSpawns.Add(e);
            }
            if (_badSpawns.Count == 0) return;
            try
            {
                _incompleteSpawnsTotal += RemoveSpawns(_badSpawns, result, true); // created by this apply; no value refers to them yet
            }
            finally
            {
                _badSpawns.Clear();
            }
        }

        /// <summary>
        /// Drops the failed new entities (<see cref="SpawnTransaction{T}.Failed"/>): a new entity whose component set or values could not be
        /// written is never kept half-made (default values, a null PrefabRef, zero edge nodes are what the game's jobs then
        /// index unchecked); it is destroyed before any system saw it and asked for again.
        /// </summary>
        private void DropFailedSpawns(ApplyResult result, string why)
        {
            result.Problems.Add(_spawnState.FailedCount + " new entities not kept: " + why);
            _dropScratch.Clear();
            foreach (Entity e in _spawnState.Failed) _dropScratch.Add(e);
            _rolledBackSpawnsTotal += RemoveSpawns(_dropScratch, result, true);
            _spawnState.ClearFailed();
            _dropScratch.Clear();
        }

        /// <summary>
        /// Removes the new entities of <paramref name="drop"/> from this apply in one pass (was one list removal per entity,
        /// quadratic in big catch-up batches): their targets go, their ids are unbound (and asked for again with
        /// <paramref name="askAgain"/>) and the entities, created by this apply, are destroyed. Returns the targets dropped.
        /// </summary>
        private int RemoveSpawns(HashSet<Entity> drop, ApplyResult result, bool askAgain)
        {
            EntityManager em = EntityManager;
            int kept = 0, dropped = 0;
            for (int t = 0; t < _targets.Count; t++)
            {
                (SyncRecord r, Entity e) = _targets[t];
                if (!drop.Contains(e))
                {
                    _targets[kept++] = _targets[t];
                    continue;
                }
                dropped++;
                result.Spawned--;
                result.Skipped++;
                result.UnresolvedSpawns++;
                if (_ids.GetEntity(r.NetId) == e) _ids.Unbind(r.NetId);
                if (askAgain) _rs.MissingIds?.Add(r.NetId);
            }
            _targets.RemoveRange(kept, _targets.Count - kept);
            foreach (Entity e in drop)
            {
                _spawned.Remove(e);
                _spawnState.Forget(e);
                if (em.Exists(e)) em.DestroyEntity(e);
            }
            result.SpawnedEntities.RemoveAll(e => drop.Contains(e));
            return dropped;
        }

        /// <summary>True when the record carries the component's value (not only its presence).</summary>
        private static bool CarriesData(SyncRecord r, TypeHandler h)
        {
            if (h == null) return true; // the type does not replicate: nothing to require
            for (int i = 0; i < r.Components.Count; i++)
            {
                if (r.Components[i].TypeHash == h.Hash && r.Components[i].Kind == h.Kind) return true;
            }
            return false;
        }

        /// <summary>True when the record carries the component as presence only although this game has its value.</summary>
        private static bool CarriesPresenceOnly(SyncRecord r, TypeHandler h)
        {
            if (h == null || h.Kind == SyncComponentKind.Presence) return false;
            for (int i = 0; i < r.Components.Count; i++)
            {
                if (r.Components[i].TypeHash == h.Hash) return r.Components[i].Kind == SyncComponentKind.Presence;
            }
            return false;
        }

        /// <summary>A node an edge may reference: it exists, is not being deleted and has its connected-edge list.</summary>
        private static bool IsUsableNode(EntityManager em, Entity node)
        {
            return node != Entity.Null && em.Exists(node) && em.HasBuffer<Game.Net.ConnectedEdge>(node);
        }

        /// <summary>
        /// Drops the middle connections (<c>ConnectedNode</c>) of a kept edge to nodes that are gone here: tagging the edge
        /// makes <c>Net.ReferencesSystem</c> index each connected node's <c>ConnectedEdge</c> buffer (a native crash for a
        /// missing one). True when something was dropped.
        /// </summary>
        internal static bool DropDeadMiddleConnections(EntityManager em, Entity e)
        {
            if (!em.HasComponent<Game.Net.Edge>(e) || !em.HasBuffer<Game.Net.ConnectedNode>(e)) return false;
            DynamicBuffer<Game.Net.ConnectedNode> nodes = em.GetBuffer<Game.Net.ConnectedNode>(e, true);
            int dead = 0;
            for (int i = 0; i < nodes.Length; i++)
            {
                if (!IsUsableNode(em, nodes[i].m_Node)) dead++;
            }
            if (dead == 0) return false;
            nodes = em.GetBuffer<Game.Net.ConnectedNode>(e);
            for (int i = nodes.Length - 1; i >= 0; i--)
            {
                if (!IsUsableNode(em, nodes[i].m_Node)) nodes.RemoveAt(i);
            }
            return true;
        }

        /// <summary>
        /// Before an entity is tagged <c>Deleted</c>, references the game's reference systems would follow
        /// unchecked are made safe: an owner that no longer exists is removed (<c>SubObjectReferencesSystem</c>,
        /// <c>ServiceUpgradeReferencesSystem</c>, <c>SubNetReferencesSystem</c> index the owner's buffer), connected nodes
        /// that are gone are dropped, and an edge node that is gone is replaced by the edge's other node (the reference
        /// system then removes the edge from that node's list twice, which is harmless); an edge with both nodes gone loses
        /// its <c>Edge</c> component. True when something had to be changed.
        /// </summary>
        internal static bool SanitizeForDelete(EntityManager em, Entity e)
        {
            bool changed = false;
            if (em.HasComponent<Game.Common.Owner>(e))
            {
                Entity owner = em.GetComponentData<Game.Common.Owner>(e).m_Owner;
                if (owner != Entity.Null && !em.Exists(owner))
                {
                    em.RemoveComponent<Game.Common.Owner>(e);
                    changed = true;
                }
            }
            if (em.HasBuffer<Game.Net.ConnectedNode>(e))
            {
                DynamicBuffer<Game.Net.ConnectedNode> nodes = em.GetBuffer<Game.Net.ConnectedNode>(e);
                for (int i = nodes.Length - 1; i >= 0; i--)
                {
                    if (IsUsableNode(em, nodes[i].m_Node)) continue;
                    nodes.RemoveAt(i);
                    changed = true;
                }
            }
            if (em.HasComponent<Game.Net.Edge>(e))
            {
                Game.Net.Edge edge = em.GetComponentData<Game.Net.Edge>(e);
                bool start = IsUsableNode(em, edge.m_Start), end = IsUsableNode(em, edge.m_End);
                if (!start || !end)
                {
                    changed = true;
                    if (start) edge.m_End = edge.m_Start;
                    else if (end) edge.m_Start = edge.m_End;
                    if (start || end) em.SetComponentData(e, edge);
                    else em.RemoveComponent<Game.Net.Edge>(e);
                }
            }
            return changed;
        }

        private void NoteUnsafe(ApplyResult result)
        {
            _unsafeTotal += result.UnsafeSkipped;
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (now - _lastUnsafeLog < 10) return;
            _lastUnsafeLog = now;
            var sample = new List<string>();
            foreach (string p in result.Problems)
            {
                if (sample.Count >= 4) break;
                if (p.Contains("not created") || p.Contains("instead of keeping it dangling") || p.Contains("middle connection") || p.Contains("still dangling")) sample.Add(p);
            }
            _log.Info(LogCategory.Replication, "reference safety: " + result.UnresolvedSpawns + " new object(s) not created (owner/node/parent missing here), "
                                               + result.DanglingDeleted + " deleted instead of left dangling, " + result.UnsafeSkipped + " reference(s) made safe (session: "
                                               + _unresolvedSpawnsTotal + " not created, " + _danglingTotal + " deleted, " + _unsafeTotal + " made safe, "
                                               + _rs.DeadReferences + " dead references dropped)"
                                               + (sample.Count > 0 ? ": " + string.Join("; ", sample) : ""));
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
        /// Types a client's proposal never writes on an existing entity (ARCHITECTURE.md#host-validation-and-commit).
        /// Line state and initializer outputs are the host's (RouteNumber, VehicleModel); values the
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

        private void ReadComponent(TypeHandler h, Entity e, in SyncComponent c, int batchOffset, byte[] data, ApplyResult result, bool keepOnUnresolved = true)
        {
            ReadOrProbe(h, e, c, batchOffset, data, result, keepOnUnresolved, false);
        }

        /// <summary>
        /// Deserializes a data component without writing it; true when one of its references does not resolve
        /// here (unknown, destroyed or deleted). <see cref="NetReaderState.Collect"/> receives the resolved references.
        /// </summary>
        private bool ProbeComponent(TypeHandler h, in SyncComponent c, int batchOffset, byte[] data, ApplyResult result)
        {
            return ReadOrProbe(h, Entity.Null, c, batchOffset, data, result, true, true);
        }

        private bool ReadOrProbe(TypeHandler h, Entity e, in SyncComponent c, int batchOffset, byte[] data, ApplyResult result, bool keepOnUnresolved, bool probe)
        {
            ByteReader keys = _keyReader;
            keys.Reset(data, c.Offset, c.Count);
            _rs.Keys.Clear();
            _rs.Unresolved = false;
            // references to destroyed or deleted entities resolve to nothing; an existing entity keeps its value
            _rs.CheckLiveness = true;
            _rs.EntityManager = EntityManager;
            _rs.KeepOnUnresolved = keepOnUnresolved;
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
                if (start < 0 || end < start || end > _batch.Length - NetReaderState.Padding) throw new DecodeException(h.Name + ": payload outside the batch");
                _position.Value = start;
                _rs.Bound(_position, end); // no read past the component's payload
                _rs.Table = _ids.ReaderTable;
                var inner = default(BinaryReader);
                inner.Initialize(_readContext, _batch, _position, _rs.Table);
                if (probe)
                {
                    h.Probe(new NetReader(inner, _rs));
                    return _rs.Unresolved;
                }
                h.Read(EntityManager, e, new NetReader(inner, _rs));
                if (_rs.Unresolved) result.UnresolvedComponents++;
                if (_position.Value != end && _failedTypes.Add(h.Hash ^ 0x5A5A5A5AUL))
                {
                    _log.Warn(LogCategory.Replication, h.Name + ": read " + (_position.Value - start) + " of " + (end - start) + " bytes (format differs?)");
                }
                return _rs.Unresolved;
            }
            catch (Exception ex)
            {
                result.Problems.Add(h.Name + ": " + ex.Message);
                if (probe) _probeFailure = ex.Message;
                // malformed or refused values (NaN geometry, overruns) are counted; the integrity check asks
                // the host for the entity again, and the session-wide count shows in the diagnostics
                if (ex is DecodeException) _malformedTotal++;
                if (_failedTypes.Add(h.Hash ^ 0xA5A5A5A5UL)) _log.Warn(LogCategory.Replication, (probe ? "checking " : "applying ") + h.Name + " failed", ex);
                return true;
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
                var seen = new HashSet<int> { types[0].TypeIndex.Value, types[1].TypeIndex.Value };
                foreach (SyncComponent c in r.Components)
                {
                    TypeHandler h = TypeHandlers.GetByHash(c.TypeHash);
                    // a type named twice (a malformed record) made CreateArchetype throw half-way through the apply
                    if (h != null && seen.Add(h.Index.Value)) types.Add(h.Type);
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
                _rs.Bound(_position, c.Offset - batchOffset + c.Count);
                _rs.Keys.Clear();
                _rs.Table = _ids.ReaderTable;
                var inner = default(BinaryReader);
                inner.Initialize(_readContext, _batch, _position, _rs.Table);
                PrefabRef value = default;
                try
                {
                    value.Deserialize(new NetReader(inner, _rs));
                }
                catch (DecodeException)
                {
                    return Entity.Null;
                }
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
                    // journal entries and life-path events carry their event's prefab; vanilla creates them
                    // from ad-hoc archetypes, which the record's own type set reproduces (generic fallback)
                    if (HasComponent(r, _journalEntry) || HasComponent(r, _lifePathEvent)) return default;
                    if (HasComponent(r, _chirp))
                    {
                        if (em.HasComponent<ChirpData>(prefab)) return em.GetComponentData<ChirpData>(prefab).m_Archetype;
                        return em.HasComponent<LifePathEventData>(prefab) ? em.GetComponentData<LifePathEventData>(prefab).m_ChirpArchetype : default;
                    }
                    return em.HasComponent<EventData>(prefab) ? em.GetComponentData<EventData>(prefab).m_Archetype : default;
                case Family.Agent:
                    // citizens, households, pets and companies are created from the ArchetypeData of their
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
            if (data == null || count < 0 || offset < 0 || offset > data.Length - count) throw new DecodeException("blob slice out of range");
            var native = new NativeArray<byte>(count + NetReaderState.Padding, Allocator.Temp, NativeArrayOptions.ClearMemory);
            try
            {
                if (count > 0) NativeArray<byte>.Copy(data, offset, native, 0, count);
                _position.Value = 0;
                _rs.Bound(_position, count); // no read past the blob
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
        /// The event the host's pathfinder produced for a segment (ARCHITECTURE.md#structure-and-transport-lines): its consumers rebuild the line's
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
