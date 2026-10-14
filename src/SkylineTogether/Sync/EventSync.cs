using System;
using System.Collections.Generic;
using System.Diagnostics;
using Game.Simulation;
using SkylineTogether.Core;
using SkylineTogether.Core.Sync;
using SkylineTogether.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// Events, disasters and the chirper feed (docs/design/SYNC-COVERAGE.md). The entities themselves
    /// (<c>Game.Events.Event</c> entities: fires, traffic accidents, weather phenomena, floods and tsunamis, health,
    /// crime, calendar and spectator events; event journal entries; chirps and life-path events) are host-only
    /// structure (<see cref="FamilyClassifier.IsHostOnly"/>): the host's capture streams them like any structure entity
    /// and its value scan re-sends what the event simulation changes (a tornado's path, a fire's progress, likes). This
    /// class adds what the tag-driven stream cannot see and what clients must do because their simulation is off:
    /// <list type="bullet">
    /// <item><b>Host presence watch.</b> The simulation adds and removes components of buildings, roads and areas
    /// without an <c>Updated</c> tag: <c>OnFire</c>, <c>Damaged</c>, <c>Flooded</c>, <c>InDanger</c>,
    /// <c>AccidentSite</c>, <c>FacingWeather</c> and more (<c>IgniteSystem</c> and <c>DamageSystem</c> only add
    /// <c>BatchesUpdated</c>, <c>WaterDamageSystem</c> and <c>AccidentSiteSystem</c> nothing). The watch compares each
    /// entity's replicated component set with the set last sent (<see cref="PresenceTracker"/>) for the watched types
    /// every 0.25 s, for every <c>BatchesUpdated</c> entity every frame, and for everything else on a budgeted rolling
    /// pass; a changed set is re-sent as a full structure record, so clients see the fire, the rubble and the accident
    /// site.</item>
    /// <item><b>Client fix-ups.</b> A weather phenomenon spawned from a host record has an empty <c>HotspotFrame</c>
    /// buffer (it is not saved and <c>Game.Events.InitializeSystem</c> is gated), which
    /// <c>EventInterpolateSystem</c> indexes without a check: the buffer is sized and its current slot written from
    /// the replicated hotspot every frame, as <c>WeatherPhenomenonSystem</c> does on the host. Event target lists lose
    /// the targets that do not replicate (citizens), which would be null entries. The player's own chirp likes stay
    /// per player. The chirp creation queue of the gated <c>CreateChirpSystem</c> is emptied, so local triggers
    /// neither create chirps nor grow the queue.</item>
    /// </list>
    /// Main thread only; the controller calls every entry point inside its own guards.
    /// </summary>
    public sealed class EventSync
    {
        private const double WatchInterval = 0.25;
        private const double RollingBudgetMs = 0.25;
        private const double RollingRefillSeconds = 2.0;
        private const int MaxPerFrame = 256;

        /// <summary>
        /// Components the simulation adds or removes on structure entities without an <c>Updated</c> tag (checked every
        /// 0.25 s; anything else is still found by the rolling pass).
        /// </summary>
        public static readonly string[] WatchedTypeNames =
        {
            "Game.Events.OnFire",
            "Game.Events.Flooded",
            "Game.Events.InDanger",
            "Game.Events.AccidentSite",
            "Game.Events.FacingWeather",
            "Game.Events.SpectatorSite",
            "Game.Objects.Damaged",
            "Game.Common.Destroyed",
            "Game.Buildings.Abandoned",
            "Game.Buildings.Condemned",
            "Game.Buildings.RescueTarget",
            "Game.Buildings.PropertyOnMarket",
            "Game.Buildings.PropertyToBeOnMarket",
            "Game.Objects.UnderConstruction",
        };

        /// <summary>
        /// Watched types that sit on thousands of buildings in a big city (properties on the market, abandoned and
        /// condemned buildings, construction sites) and change slowly: swept every 2 s instead of every 0.25 s (the sweep
        /// walks every entity that has one; at four times a second that was a few ms per second of host time).
        /// </summary>
        public static readonly string[] SlowWatchedTypeNames =
        {
            "Game.Buildings.PropertyOnMarket",
            "Game.Buildings.PropertyToBeOnMarket",
            "Game.Buildings.Abandoned",
            "Game.Buildings.Condemned",
            "Game.Objects.UnderConstruction",
        };

        private const double SlowWatchInterval = 2.0;

        /// <summary>One presence watch (a query, its interval and the ids it saw at its previous sweep).</summary>
        private sealed class Watch
        {
            public EntityQuery Query;
            public double Interval;
            public double Last = double.NegativeInfinity;
            public HashSet<uint> Seen = new HashSet<uint>();
            public HashSet<uint> Now = new HashSet<uint>();
        }

        private readonly List<Watch> _watches = new List<Watch>();

        private readonly ILog _log;
        private readonly PresenceTracker _sent = new PresenceTracker();
        private readonly RollingIdPass _pass = new RollingIdPass();
        private readonly Dictionary<EntityArchetype, ulong> _signatures = new Dictionary<EntityArchetype, ulong>();
        private readonly Dictionary<EntityArchetype, bool> _trees = new Dictionary<EntityArchetype, bool>();
        private ComponentType _treeType;
        private ComponentType _buildingType;
        private readonly HashSet<uint> _liked = new HashSet<uint>();
        private readonly HashSet<string> _once = new HashSet<string>();
        private readonly List<uint> _scratchIds = new List<uint>();
        private World _world;
        private EntityCodec _codec;
        private NetIdSystem _ids;
        private SimulationSystem _simulation;
        private EntityQuery _batchesQuery;
        private EntityQuery _phenomena;
        private EntityQuery _chirps;
        private double _lastRefill = double.NegativeInfinity;

        public EventSync(ILog log)
        {
            _log = log;
            // perf pass: the client's per-frame guarded calls use delegates made once
            _readLikesCall = ReadLikes;
            _touchedCall = FixTouched;
            _hotspotsCall = () => UpdateHotspots(_world.EntityManager);
        }

        private readonly Action _readLikesCall, _touchedCall, _hotspotsCall;
        private HashSet<Entity> _touched; // the entities of the current ClientAfterApply call

        /// <summary>Re-sends found by the presence watch (diagnostics).</summary>
        public long PresenceResends { get; private set; }

        private bool Attached => _codec != null;

        /// <summary>Binds to the world (once, from <c>SyncController.EnsureWorld</c>).</summary>
        public void Attach(World world, EntityCodec codec, NetIdSystem ids)
        {
            if (_codec != null) return;
            try
            {
                _world = world;
                EntityManager em = world.EntityManager;
                _simulation = world.GetOrCreateSystemManaged<SimulationSystem>();
                _treeType = ComponentType.ReadOnly<Game.Objects.Tree>();
                _buildingType = ComponentType.ReadOnly<Game.Buildings.Building>();
                var watched = new List<ComponentType>();
                var slow = new List<ComponentType>();
                var slowNames = new HashSet<string>(SlowWatchedTypeNames);
                foreach (string name in WatchedTypeNames)
                {
                    Type t = FamilyClassifier.FindType(name);
                    if (t == null)
                    {
                        _log.Debug(LogCategory.Replication, "presence watch: type not found: " + name);
                        continue;
                    }
                    (slowNames.Contains(name) ? slow : watched).Add(ComponentType.ReadOnly(t));
                }
                ComponentType[] none = { ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>() };
                _watches.Clear();
                if (watched.Count > 0) _watches.Add(new Watch { Query = em.CreateEntityQuery(new EntityQueryDesc { Any = watched.ToArray(), None = none }), Interval = WatchInterval });
                if (slow.Count > 0) _watches.Add(new Watch { Query = em.CreateEntityQuery(new EntityQueryDesc { Any = slow.ToArray(), None = none }), Interval = SlowWatchInterval });
                _batchesQuery = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Game.Common.BatchesUpdated>() }, None = none });
                _phenomena = em.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<Game.Events.WeatherPhenomenon>(), ComponentType.ReadWrite<Game.Events.HotspotFrame>() },
                    None = none,
                });
                _chirps = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Game.Triggers.Chirp>() }, None = none });
                _codec = codec;
                _ids = ids;
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Replication, "event sync unavailable", ex);
            }
        }

        /// <summary>Session started or ended: forget every baseline.</summary>
        public void Reset()
        {
            _sent.Clear();
            _pass.Clear();
            foreach (Watch w in _watches)
            {
                w.Seen.Clear();
                w.Now.Clear();
                w.Last = double.NegativeInfinity;
            }
            _liked.Clear();
            _signatures.Clear();
            _trees.Clear();
            _lastRefill = double.NegativeInfinity;
            PresenceResends = 0;
        }

        // ================================================================== host

        /// <summary>The order-independent signature of the replicated component set of an archetype (cached).</summary>
        private ulong SignatureOf(EntityArchetype archetype)
        {
            if (_signatures.TryGetValue(archetype, out ulong s)) return s;
            s = TypeSetSignature.Empty;
            foreach (TypeHandler h in _codec.HandlersOf(archetype)) s = TypeSetSignature.Add(s, h.Hash);
            _signatures[archetype] = s;
            return s;
        }

        /// <summary>Host: the entity was just sent as a full structure record (its set is now what clients hold).</summary>
        public void NoteWritten(EntityManager em, uint id, Entity e, Family family)
        {
            if (!Attached || id == 0 || !FamilyClassifier.IsPresenceWatched(family)) return;
            _sent.Record(id, SignatureOf(em.GetChunk(e).Archetype));
        }

        /// <summary>Host: the id was despawned or unbound.</summary>
        public void Forget(uint id) => _sent.Forget(id);

        /// <summary>
        /// Host, after the tagged capture of the frame: adds to <paramref name="into"/> every bound structure entity
        /// whose replicated component set differs from the one last sent.
        /// </summary>
        public void HostCollect(double now, HashSet<Entity> into, List<uint> values)
        {
            if (!Attached || !Features.Events.IsAvailable) return;
            EntityManager em = _world.EntityManager;
            int found = 0;
            // 1: entities whose batches changed this frame (fire, damage, destruction add BatchesUpdated); a tree whose
            // set is unchanged changed its growth stage (TreeGrowthSystem): its values are re-sent
            if (!_batchesQuery.IsEmptyIgnoreFilter) found += Sweep(em, _batchesQuery, into, null, values);
            // 2: the watched types every 0.25 s, plus the entities that had one of them at the previous sweep
            foreach (Watch w in _watches)
            {
                if (now - w.Last < w.Interval) continue;
                w.Last = now;
                w.Now.Clear();
                if (!w.Query.IsEmptyIgnoreFilter) found += Sweep(em, w.Query, into, w.Now, null);
                foreach (uint id in w.Seen)
                {
                    if (w.Now.Contains(id)) continue;
                    if (Check(em, id, into)) found++;
                }
                // perf pass: the sets swap roles (the copy cost an add per watched entity at every sweep)
                HashSet<uint> last = w.Seen;
                w.Seen = w.Now;
                w.Now = last;
                w.Now.Clear();
            }
            // 3: everything else on a budgeted rolling pass
            if (!_pass.InProgress && now - _lastRefill >= RollingRefillSeconds)
            {
                _lastRefill = now;
                RefillPass();
            }
            long start = Stopwatch.GetTimestamp();
            long budget = (long)(Perf.Allowance(Perf.BudgetPresenceWatch, RollingBudgetMs) * Stopwatch.Frequency / 1000.0); // w9/perf: shared budget
            int n = 0;
            while (_pass.Next(out uint id))
            {
                if (Check(em, id, into)) found++;
                if ((++n & 31) == 0 && Stopwatch.GetTimestamp() - start > budget) break;
            }
            if (found > 0)
            {
                PresenceResends += found;
                _log.Debug(LogCategory.Replication, "presence watch: " + found + " entities changed their component set without a tag");
            }
        }

        private int Sweep(EntityManager em, EntityQuery query, HashSet<Entity> into, HashSet<uint> seen, List<uint> treeValues)
        {
            int found = 0;
            NativeArray<ArchetypeChunk> chunks = query.ToArchetypeChunkArray(Allocator.Temp);
            IdIndexSet structure = _ids.StructureIds; // bound ids of the presence-watched families
            try
            {
                EntityTypeHandle entityType = em.GetEntityTypeHandle();
                foreach (ArchetypeChunk chunk in chunks)
                {
                    ulong signature = SignatureOf(chunk.Archetype);
                    bool trees = treeValues != null && IsTree(chunk.Archetype);
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        uint id = _ids.GetNetId(entities[i]);
                        if (id == 0 || !structure.Contains(id)) continue;
                        seen?.Add(id);
                        if (!_sent.Changed(id, signature))
                        {
                            if (trees) treeValues.Add(id);
                            continue;
                        }
                        if (into.Count >= MaxPerFrame) continue;
                        if (into.Add(entities[i])) found++;
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
            return found;
        }

        /// <summary>Trees (not buildings): their growth stage is not part of any value stream but the tree's own record.</summary>
        private bool IsTree(EntityArchetype archetype)
        {
            if (_trees.TryGetValue(archetype, out bool yes)) return yes;
            yes = false;
            NativeArray<ComponentType> types = archetype.GetComponentTypes(Allocator.Temp);
            try
            {
                bool tree = false, building = false;
                foreach (ComponentType t in types)
                {
                    if (t.TypeIndex == _treeType.TypeIndex) tree = true;
                    else if (t.TypeIndex == _buildingType.TypeIndex) building = true;
                }
                yes = tree && !building;
            }
            finally
            {
                types.Dispose();
            }
            _trees[archetype] = yes;
            return yes;
        }

        private bool Check(EntityManager em, uint id, HashSet<Entity> into)
        {
            if (into.Count >= MaxPerFrame) return false;
            Entity e = _ids.GetEntity(id);
            if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) return false;
            if (!FamilyClassifier.IsPresenceWatched(_ids.GetFamily(id))) return false;
            if (!_sent.Changed(id, SignatureOf(em.GetChunk(e).Archetype))) return false;
            return into.Add(e);
        }

        private void RefillPass()
        {
            // perf pass: the structure index set (bound presence-watched ids) instead of a walk over the whole id table
            _scratchIds.Clear();
            _ids.StructureIds.CopyTo(_scratchIds);
            _pass.Refill(_scratchIds);
        }

        // ================================================================== client

        /// <summary>
        /// Client, frame start before host data is applied: the player's own chirp likes (the vanilla chirper writes
        /// them through the end-frame barrier, so they are visible here), and the gated chirp queue is emptied.
        /// </summary>
        public void ClientBeforeApply()
        {
            if (!Attached || !Features.Events.IsAvailable) return;
            Guard("likes", _readLikesCall);
            // chirp queue: emptied by ChirpDrainSystem right before CreateChirpSystem (which stays enabled on clients)
        }

        /// <summary>Client, after the frame's host data was applied: per-player likes, target lists, hotspot frames.</summary>
        public void ClientAfterApply(HashSet<Entity> touched)
        {
            if (!Attached || !Features.Events.IsAvailable) return;
            if (touched != null && touched.Count > 0)
            {
                _touched = touched;
                Guard("touched events", _touchedCall);
                _touched = null;
            }
            Guard("hotspot frames", _hotspotsCall);
        }

        private void FixTouched()
        {
            EntityManager em = _world.EntityManager;
            foreach (Entity e in _touched)
            {
                if (!em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                if (em.HasBuffer<Game.Events.TargetElement>(e)) DropNullTargets(em, em.GetBuffer<Game.Events.TargetElement>(e));
                if (em.HasComponent<Game.Triggers.Chirp>(e)) RestoreLike(em, e);
                // a tree's growth stage arrived as values (no tag): its mesh is chosen again, as on the host
                if (em.HasComponent<Game.Objects.Tree>(e) && !em.HasComponent<Game.Buildings.Building>(e) && !em.HasComponent<Game.Common.BatchesUpdated>(e)
                    && !em.HasComponent<Game.Common.Updated>(e) && !em.HasComponent<Game.Common.Created>(e))
                {
                    em.AddComponent<Game.Common.BatchesUpdated>(e);
                }
            }
        }

        private void ReadLikes()
        {
            if (_chirps.IsEmptyIgnoreFilter)
            {
                _liked.Clear();
                return;
            }
            NativeArray<Entity> entities = _chirps.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Triggers.Chirp> chirps = _chirps.ToComponentDataArray<Game.Triggers.Chirp>(Allocator.Temp);
            try
            {
                _liked.Clear();
                for (int i = 0; i < entities.Length; i++)
                {
                    if ((chirps[i].m_Flags & Game.Triggers.ChirpFlags.Liked) == 0) continue;
                    uint id = _ids.GetNetId(entities[i]);
                    if (id != 0) _liked.Add(id);
                }
            }
            finally
            {
                entities.Dispose();
                chirps.Dispose();
            }
        }

        /// <summary>The like bit is the local player's (the host's own likes never arrive, the client's are kept).</summary>
        private void RestoreLike(EntityManager em, Entity e)
        {
            uint id = _ids.GetNetId(e);
            if (id == 0) return;
            Game.Triggers.Chirp chirp = em.GetComponentData<Game.Triggers.Chirp>(e);
            bool liked = _liked.Contains(id);
            bool has = (chirp.m_Flags & Game.Triggers.ChirpFlags.Liked) != 0;
            if (liked == has) return;
            if (liked) chirp.m_Flags |= Game.Triggers.ChirpFlags.Liked;
            else chirp.m_Flags &= ~Game.Triggers.ChirpFlags.Liked;
            em.SetComponentData(e, chirp);
        }

        /// <summary>
        /// Drops targets that resolve to nothing; also targets that are destroyed or being deleted here (the
        /// event systems index a target's components from the list).
        /// </summary>
        private static void DropNullTargets(EntityManager em, DynamicBuffer<Game.Events.TargetElement> targets)
        {
            for (int i = targets.Length - 1; i >= 0; i--)
            {
                Entity t = targets[i].m_Entity;
                if (t == Entity.Null || !em.Exists(t) || em.HasComponent<Game.Common.Deleted>(t)) targets.RemoveAt(i);
            }
        }

        /// <summary>
        /// Every weather phenomenon gets four hotspot frames (as <c>HotspotFrameSystem</c> builds them after a load) and
        /// the frame of the current 16-tick slot from the replicated hotspot (as <c>WeatherPhenomenonSystem</c> writes it).
        /// </summary>
        private void UpdateHotspots(EntityManager em)
        {
            if (_phenomena.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> entities = _phenomena.ToEntityArray(Allocator.Temp);
            try
            {
                int slot = (int)((_simulation.frameIndex / 16) & 3);
                const float step = 4f / 15f;
                foreach (Entity e in entities)
                {
                    Game.Events.WeatherPhenomenon w = em.GetComponentData<Game.Events.WeatherPhenomenon>(e);
                    // a non-finite hotspot would feed the interpolation and rendering of the phenomenon
                    if (!math.all(math.isfinite(w.m_HotspotPosition)) || !math.all(math.isfinite(w.m_HotspotVelocity))) continue;
                    DynamicBuffer<Game.Events.HotspotFrame> frames = em.GetBuffer<Game.Events.HotspotFrame>(e);
                    if (frames.Length != 4)
                    {
                        frames.ResizeUninitialized(4);
                        for (int i = 0; i < 4; i++) frames[i] = new Game.Events.HotspotFrame(w);
                        if (em.HasComponent<Game.Rendering.InterpolatedTransform>(e)) em.SetComponentData(e, new Game.Rendering.InterpolatedTransform(w));
                    }
                    frames[slot] = new Game.Events.HotspotFrame
                    {
                        m_Position = w.m_HotspotPosition - w.m_HotspotVelocity * step * 0.5f,
                        m_Velocity = w.m_HotspotVelocity,
                    };
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                if (_once.Add(what)) _log.Warn(LogCategory.Replication, "event sync: " + what + " failed", ex);
                Diagnostics.Guard.Report(Features.Events, "event sync (" + what + ")", ex);
            }
        }
    }

    /// <summary>
    /// Client, ModificationEnd right before <c>CreateChirpSystem</c>: chirps come from the host, so the chirps that local
    /// triggers (<c>TriggerSystem</c>, <c>LifePathEventSystem</c>) queued this frame are dropped. The system itself stays
    /// enabled, because its producers assert that it runs (Game.Triggers.CreateChirpSystem.GetQueue).
    /// </summary>
    public sealed class ChirpDrainSystem : Game.GameSystemBase
    {
        private Game.Triggers.CreateChirpSystem _createChirps;

        protected override void OnCreate()
        {
            base.OnCreate();
            _createChirps = World.GetOrCreateSystemManaged<Game.Triggers.CreateChirpSystem>();
        }

        protected override void OnUpdate()
        {
            SyncController sync = Mod.Sync;
            if (sync == null || !SyncController.SessionActive || !sync.IsLiveClient) return;
            if (_createChirps == null || !_createChirps.Enabled) return; // GetQueue asserts while the system is off
            try
            {
                NativeQueue<Game.Triggers.ChirpCreationData> queue = _createChirps.GetQueue(out Unity.Jobs.JobHandle deps);
                deps.Complete();
                if (queue.IsCreated && queue.Count > 0) queue.Clear();
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Features.Events, "chirp queue", ex);
            }
        }
    }
}
