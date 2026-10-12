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
    /// w3/audit: events, disasters and the chirper feed (docs/design/SYNC-COVERAGE.md). The entities themselves
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

        private readonly ILog _log;
        private readonly PresenceTracker _sent = new PresenceTracker();
        private readonly RollingIdPass _pass = new RollingIdPass();
        private readonly Dictionary<EntityArchetype, ulong> _signatures = new Dictionary<EntityArchetype, ulong>();
        private readonly Dictionary<EntityArchetype, bool> _trees = new Dictionary<EntityArchetype, bool>();
        private ComponentType _treeType;
        private ComponentType _buildingType;
        private readonly HashSet<uint> _watchedLast = new HashSet<uint>();
        private readonly HashSet<uint> _watchedNow = new HashSet<uint>();
        private readonly HashSet<uint> _liked = new HashSet<uint>();
        private readonly HashSet<string> _once = new HashSet<string>();
        private readonly List<uint> _scratchIds = new List<uint>();
        private World _world;
        private EntityCodec _codec;
        private NetIdSystem _ids;
        private SimulationSystem _simulation;
        private Game.Triggers.CreateChirpSystem _createChirps;
        private EntityQuery _watchQuery;
        private EntityQuery _batchesQuery;
        private EntityQuery _phenomena;
        private EntityQuery _chirps;
        private bool _hasWatchQuery;
        private double _lastWatch = double.NegativeInfinity;
        private double _lastRefill = double.NegativeInfinity;

        public EventSync(ILog log)
        {
            _log = log;
        }

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
                _createChirps = world.GetOrCreateSystemManaged<Game.Triggers.CreateChirpSystem>();
                var watched = new List<ComponentType>();
                foreach (string name in WatchedTypeNames)
                {
                    Type t = FamilyClassifier.FindType(name);
                    if (t == null)
                    {
                        _log.Debug(LogCategory.Replication, "presence watch: type not found: " + name);
                        continue;
                    }
                    watched.Add(ComponentType.ReadOnly(t));
                }
                ComponentType[] none = { ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>() };
                if (watched.Count > 0)
                {
                    _watchQuery = em.CreateEntityQuery(new EntityQueryDesc { Any = watched.ToArray(), None = none });
                    _hasWatchQuery = true;
                }
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
            _watchedLast.Clear();
            _watchedNow.Clear();
            _liked.Clear();
            _signatures.Clear();
            _trees.Clear();
            _lastWatch = double.NegativeInfinity;
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
            if (_hasWatchQuery && now - _lastWatch >= WatchInterval)
            {
                _lastWatch = now;
                _watchedNow.Clear();
                if (!_watchQuery.IsEmptyIgnoreFilter) found += Sweep(em, _watchQuery, into, _watchedNow, null);
                foreach (uint id in _watchedLast)
                {
                    if (_watchedNow.Contains(id)) continue;
                    if (Check(em, id, into)) found++;
                }
                _watchedLast.Clear();
                foreach (uint id in _watchedNow) _watchedLast.Add(id);
            }
            // 3: everything else on a budgeted rolling pass
            if (!_pass.InProgress && now - _lastRefill >= RollingRefillSeconds)
            {
                _lastRefill = now;
                RefillPass();
            }
            long start = Stopwatch.GetTimestamp();
            long budget = (long)(RollingBudgetMs * Stopwatch.Frequency / 1000.0);
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
                        if (id == 0 || !FamilyClassifier.IsPresenceWatched(_ids.GetFamily(id))) continue;
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
            _scratchIds.Clear();
            NativeArray<Entity> table = _ids.ReaderTable;
            for (int id = 1; id < table.Length; id++)
            {
                if (table[id] == Entity.Null) continue;
                if (FamilyClassifier.IsPresenceWatched(_ids.GetFamily((uint)id))) _scratchIds.Add((uint)id);
            }
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
            Guard("likes", ReadLikes);
            Guard("chirp queue", DrainChirpQueue);
        }

        /// <summary>Client, after the frame's host data was applied: per-player likes, target lists, hotspot frames.</summary>
        public void ClientAfterApply(HashSet<Entity> touched)
        {
            if (!Attached || !Features.Events.IsAvailable) return;
            EntityManager em = _world.EntityManager;
            if (touched != null && touched.Count > 0)
            {
                Guard("touched events", () =>
                {
                    foreach (Entity e in touched)
                    {
                        if (!em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                        if (em.HasBuffer<Game.Events.TargetElement>(e)) DropNullTargets(em.GetBuffer<Game.Events.TargetElement>(e));
                        if (em.HasComponent<Game.Triggers.Chirp>(e)) RestoreLike(em, e);
                        // a tree's growth stage arrived as values (no tag): its mesh is chosen again, as on the host
                        if (em.HasComponent<Game.Objects.Tree>(e) && !em.HasComponent<Game.Buildings.Building>(e) && !em.HasComponent<Game.Common.BatchesUpdated>(e)
                            && !em.HasComponent<Game.Common.Updated>(e) && !em.HasComponent<Game.Common.Created>(e))
                        {
                            em.AddComponent<Game.Common.BatchesUpdated>(e);
                        }
                    }
                });
            }
            Guard("hotspot frames", () => UpdateHotspots(em));
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

        private static void DropNullTargets(DynamicBuffer<Game.Events.TargetElement> targets)
        {
            for (int i = targets.Length - 1; i >= 0; i--)
            {
                if (targets[i].m_Entity == Entity.Null) targets.RemoveAt(i);
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

        /// <summary>
        /// The gated <c>CreateChirpSystem</c> keeps receiving local trigger results (weather, policies, tools); its queue
        /// is emptied so nothing piles up. While the system runs (gate open or failed) it consumes the queue itself.
        /// </summary>
        private void DrainChirpQueue()
        {
            if (_createChirps == null || _createChirps.Enabled) return;
            NativeQueue<Game.Triggers.ChirpCreationData> queue = _createChirps.GetQueue(out Unity.Jobs.JobHandle deps);
            deps.Complete();
            if (queue.IsCreated && queue.Count > 0) queue.Clear();
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
}
