using System;
using System.Collections.Generic;
using Game.Common;
using Game.Creatures;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Routes;
using Game.Simulation;
using Game.Vehicles;
using SkylineTogether.Core;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Motion;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Diagnostics;
using SkylineTogether.Session;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// Client side of mover replication (ARCHITECTURE.md#movers). Applies the host's mover records right after the structure
    /// apply (before every Modification phase, so <c>Applied</c> keeps the host's <c>UpdateFrame</c>,
    /// <c>UpdateGroupSystem.cs:408-416</c>): spawns from the prefab's own archetype, despawns with <c>Deleted</c> and
    /// mirrors the vanilla type swaps (moving ↔ stopped from the prefab archetypes, riding/inside a vehicle as
    /// <c>ResidentAISystem</c> does it, <c>ResidentAISystem.cs:200-215, 3910-3920</c>). Motion arrives on the motion
    /// channel and is staged by <see cref="MotionClientStager"/>; every frame, in PreCulling before
    /// <c>PreCullingSystem</c> (after <c>RenderingSystem.PrepareRendering</c> computed the render tick), the slots the
    /// interpolation will read are written "just in time" (R15 §2.2-2.6): <c>F1 = u + 16·((R − u − 32) >> 4)</c>, frames
    /// for <c>F1</c>, <c>F1 + 16</c> and <c>F1 + 32</c>. Movers outside interest are pinned: all four slots hold the
    /// same pose with zero velocity, so the ring never cycles back. Movers are never tagged <c>Updated</c> for motion
    /// (it would reset their frames, <c>UpdateGroupSystem.cs:561-615</c>). The host's roster records
    /// (<see cref="MoverRoster"/>, the last record of a movers batch) are compared with the movers of their window: movers
    /// the host does not have are removed at once, movers missing here twice in a row are asked for again
    /// (<see cref="MoverRosterAudit"/>, sent as <c>ResendEntities</c>). Main thread only.
    /// </summary>
    public sealed class MoverClient
    {
        private const double ReportInterval = 0.1;
        private const int MaxBatchesPerFrame = 64;
        private readonly SyncRecordPool _decodePool = new SyncRecordPool(); // decoded mover records, reused per batch
        private const TransformFlags LightMask = TransformFlags.MainLights | TransformFlags.ExtraLights | TransformFlags.WarningLights | TransformFlags.WorkLights;

        /// <summary>Modification systems that would create or move agents on a follower (R15 §4.6, ARCHITECTURE.md#gated-systems).</summary>
        private static readonly string[] Gated =
        {
            "Game.Objects.ObjectEmergeSystem",
            "Game.Creatures.GroupSystem",
            "Game.Vehicles.FixParkingLocationSystem",
            "Game.Vehicles.ParkedVehiclesSystem",
            "Game.Creatures.TripResetSystem",
            "Game.Pathfind.ParkingLaneDataSystem",
            "Game.Pathfind.PathOwnerTargetMovedSystem",
            "Game.Routes.BoardingVehicleSystem",
        };

        /// <summary>The mover gate's system names (probed at startup, <see cref="Diagnostics.Probes"/>).</summary>
        public static IReadOnlyList<string> GatedSystemNames => Gated;

        private sealed class SlotState
        {
            public NetSlot Slot;
            public Entity Entity;
            public uint UpdateFrame;
            public int Version = -1;
            public uint F1;
            public bool Written;
            public uint Lights;
        }

        private sealed class Archetypes
        {
            public HashSet<ComponentType> Moving;
            public HashSet<ComponentType> Stopped;
        }

        private readonly SessionManager _sessions;
        private readonly ILog _log;
        private readonly Queue<(byte[] Data, long Seq)> _batches = new Queue<(byte[], long)>();
        private readonly FrameBudget _applyBudget = new FrameBudget(4.0);
        private readonly Queue<(MessageKind Kind, byte[] Body)> _motion = new Queue<(MessageKind, byte[])>();
        private readonly List<SlotState> _slots = new List<SlotState>();
        private readonly List<MotionPin> _pins = new List<MotionPin>();
        private readonly HashSet<uint> _force = new HashSet<uint>();
        private readonly Dictionary<(Entity, bool), Archetypes> _archetypes = new Dictionary<(Entity, bool), Archetypes>();
        private readonly List<(ComponentSystemBase System, bool WasEnabled)> _gated = new List<(ComponentSystemBase, bool)>();
        private readonly List<Entity> _lights = new List<Entity>();
        // agent links whose citizen, pet, keeper or owner is not bound here yet (retried for a few seconds)
        private readonly Dictionary<uint, (MoverAgentLinks Links, double Until)> _pendingLinks = new Dictionary<uint, (MoverAgentLinks, double)>();
        private readonly List<uint> _linkScratch = new List<uint>();
        private const double LinkRetrySeconds = 5.0;
        private long _linksApplied;
        private readonly ByteWriter _report = new ByteWriter(64);
        private readonly ViewReport _view = new ViewReport();
        private readonly HashSet<ComponentType> _skipTypes = new HashSet<ComponentType>
        {
            ComponentType.ReadWrite<Created>(),
            ComponentType.ReadWrite<Updated>(),
        };
        private static readonly ComponentType[] RideRemoved =
        {
            ComponentType.ReadWrite<Moving>(),
            ComponentType.ReadWrite<TransformFrame>(),
            ComponentType.ReadWrite<HumanNavigation>(),
            ComponentType.ReadWrite<HumanCurrentLane>(),
            ComponentType.ReadWrite<AnimalNavigation>(),
            ComponentType.ReadWrite<AnimalCurrentLane>(),
            ComponentType.ReadWrite<Blocker>(),
        };
        private MotionClientStager _stager = new MotionClientStager();
        private World _world;
        private NetIdSystem _ids;
        private RenderingSystem _rendering;
        private CameraUpdateSystem _camera;
        private bool _active;
        private bool _pendingLive;
        private double _lastReport;
        private long _spawned, _despawned;

        /// <summary>Motion datagrams held at most (about 4 s of a busy view at 60 fps); newer ones are dropped.</summary>
        private const int MaxQueuedMotion = 256;
        private long _motionDropped, _recordFailures, _familyMismatches, _ridersReleased;
        private double _lastFailureLog = double.NegativeInfinity;
        // the host's roster (ARCHITECTURE.md#movers)
        private readonly MoverRosterAudit _audit = new MoverRosterAudit();
        private readonly List<uint> _rosterLocal = new List<uint>();
        private readonly List<uint> _rosterRemove = new List<uint>();
        private readonly List<uint> _rosterRequests = new List<uint>();
        private readonly SyncBatch.Writer _requestWriter = new SyncBatch.Writer();
        private long _rosterPasses, _passRemoved, _passAircraft, _requestedAtPassStart;
        private bool _sawRoster;

        public MoverClient(SessionManager sessions, ILog log)
        {
            _sessions = sessions;
            _log = log;
            // the deletion cascade removes a mover whose owner, citizen or pet it deletes the way this channel does
            DeletionCascade.DeleteMover = DeleteLocally;
        }

        public int InInterest => _stager.AssignedCount;

        public void OnClientStarted(SessionClient client)
        {
            _active = false;
            _pendingLive = false;
            _pendingLinks.Clear();
            _releasedVehicles.Clear();
            _linksApplied = 0;
            _batches.Clear();
            _motion.Clear();
            _stager = new MotionClientStager();
            _slots.Clear();
            ResetRoster();
            client.AppMessage += OnMessage;
            client.BecameLive += () => _pendingLive = true;
        }

        public void OnSessionEnded()
        {
            _active = false;
            _pendingLive = false;
            _batches.Clear();
            _motion.Clear();
            _slots.Clear();
            _force.Clear();
            _archetypes.Clear();
            ResetRoster();
            _stager = new MotionClientStager();
            foreach ((ComponentSystemBase system, bool wasEnabled) in _gated)
            {
                try
                {
                    SystemSwitch.Restore(system, wasEnabled);
                }
                catch (Exception)
                {
                    // the world may be gone
                }
            }
            _gated.Clear();
        }

        private void OnMessage(Envelope e)
        {
            switch (e.Kind)
            {
                case MessageKind.Epoch:
                    // only the movers' own batches (their first record is a mover record); SyncController skips them
                    // with the movers feature off nothing applies (or releases) these batches; holding
                    // them froze the apply acks, so the host never reused a released net id again
                    if (SyncBatch.IsMoverBatch(e.Body, e.BodyOffset, e.BodyCount) && Diagnostics.Features.Movers.IsAvailable)
                    {
                        // held until applied, so the host reuses a despawned mover's id only after this batch
                        long seq = 0;
                        SessionClient client = _sessions.Client;
                        if (client != null)
                        {
                            seq = client.ApplyAcks.Received;
                            client.ApplyAcks.Hold(seq);
                        }
                        _batches.Enqueue((Copy(e), seq));
                    }
                    break;
                case MessageKind.Motion:
                case MessageKind.MotionControl:
                    // nothing drains the queue once the movers feature is off, and a run of slow frames must
                    // not let it grow without end (motion is lossy by design: newer samples supersede old ones)
                    if (!Diagnostics.Features.Movers.IsAvailable) break;
                    if (_motion.Count >= MaxQueuedMotion && e.Kind == MessageKind.Motion)
                    {
                        _motionDropped++;
                        break;
                    }
                    _motion.Enqueue((e.Kind, Copy(e)));
                    break;
            }
        }

        /// <summary>The movers feature switched itself off: releases the held batches so the apply acks move on.</summary>
        public void DropHeld()
        {
            _motion.Clear(); // the feature is off, nothing will drain it
            if (_batches.Count == 0) return;
            SessionClient client = _sessions.Client;
            while (_batches.Count > 0)
            {
                (byte[] _, long seq) = _batches.Dequeue();
                if (seq != 0) client?.ApplyAcks.Release(seq);
            }
        }

        private static byte[] Copy(Envelope e)
        {
            var copy = new byte[e.BodyCount];
            Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
            return copy;
        }

        private bool EnsureWorld()
        {
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return false;
            if (_world == world) return true;
            _world = world;
            _ids = world.GetOrCreateSystemManaged<NetIdSystem>();
            _rendering = world.GetOrCreateSystemManaged<RenderingSystem>();
            _camera = world.GetOrCreateSystemManaged<CameraUpdateSystem>();
            _archetypes.Clear();
            return true;
        }

        // ================================================================== apply (frame start)

        /// <summary>Right after the structure apply (MainLoop, after <c>EndFrameBarrier</c>).</summary>
        public void Apply()
        {
            if (_sessions.Client == null || !EnsureWorld()) return;
            EntityManager em = _world.EntityManager;
            if (_pendingLive)
            {
                _pendingLive = false;
                GoLive(em);
            }
            if (!_active) return;
            long start = Perf.Start();
            double allowance = _applyBudget.BeginFrame();
            _createdThisFrame.Clear();
            int n = 0;
            while (_batches.Count > 0 && n < MaxBatchesPerFrame)
            {
                // budgeted in milliseconds with carry-over; at least one batch per frame, never split
                if (n > 0 && Perf.Elapsed(start) > allowance) break;
                n++;
                (byte[] data, long seq) = _batches.Dequeue();
                if (!ApplyOrRefuse(em, data, seq, false)) return;
            }
            if (n > 0)
            {
                double applied = Perf.Stop(Perf.MoverApply, start);
                _applyBudget.EndFrame(applied);
                Perf.Budget.End(Perf.BudgetMoverApply, applied); // shared budget telemetry (apply priority)
            }
            if (_pendingLinks.Count > 0) RetryLinks(em);
            if (_rosterRequests.Count > 0) SendRosterRequests();
            if (_releasedVehicles.Count > 0) ReleaseStrayRiders(em);
        }

        /// <summary>
        /// Applies one mover batch (only its despawns with <paramref name="despawnsOnly"/>) and releases its acknowledgement
        /// hold, the rule of the structure batches (ARCHITECTURE.md#client-apply): the host reuses a despawned mover's id once
        /// every peer acknowledged the batch, so a batch that cannot be read, or whose despawn left its mover, is never
        /// acknowledged; replication is switched off instead, which ends the session with a rejoin offer. False then.
        /// </summary>
        private bool ApplyOrRefuse(EntityManager em, byte[] data, long seq, bool despawnsOnly)
        {
            int notApplied;
            try
            {
                notApplied = ApplyBatch(em, SyncBatch.Read(data, 0, data.Length, _decodePool), despawnsOnly);
            }
            catch (Exception ex)
            {
                // SyncBatch.Read threw (every record and every later pass is guarded on its own): what the batch despawned is unknown
                _log.Warn(LogCategory.Replication, "a mover batch could not be read: " + ex.Message);
                Diagnostics.Guard.Disable(Diagnostics.Features.Replication, "a mover batch from the host could not be read (" + ex.GetType().Name + "), so what it deleted is unknown");
                return false;
            }
            if (notApplied > 0)
            {
                Diagnostics.Guard.Disable(Diagnostics.Features.Replication, notApplied + " mover deletion(s) could not be applied");
                return false;
            }
            if (seq != 0) _sessions.Client?.ApplyAcks.Release(seq);
            return true;
        }

        /// <summary>
        /// Client with the movers feature off (it tripped): the mover batches' despawns are still applied (the host reuses
        /// those ids once this client acknowledges them, and a stale mover binding is what a later record of another
        /// family would find), everything else in them is dropped.
        /// </summary>
        public void ApplyDespawnsOnly()
        {
            _motion.Clear(); // nothing will drain it
            if (_batches.Count == 0) return;
            if (_sessions.Client == null || !EnsureWorld())
            {
                DropHeld();
                return;
            }
            EntityManager em = _world.EntityManager;
            int n = 0;
            while (_batches.Count > 0 && n < MaxBatchesPerFrame)
            {
                n++;
                (byte[] data, long seq) = _batches.Dequeue();
                if (!ApplyOrRefuse(em, data, seq, true)) return;
            }
            if (_releasedVehicles.Count > 0) ReleaseStrayRiders(em);
        }

        /// <summary>Movers created by this frame's batches (several per frame while catching up).</summary>
        private readonly HashSet<Entity> _createdThisFrame = new HashSet<Entity>();

        /// <summary>Vehicles deleted here since the last stray-rider pass (<see cref="ReleaseStrayRiders"/>).</summary>
        private readonly HashSet<Entity> _releasedVehicles = new HashSet<Entity>();
        private readonly List<(Entity Rider, Entity Vehicle)> _strayRiders = new List<(Entity, Entity)>();
        private EntityQuery _ridingQuery;
        private World _ridingWorld;

        /// <summary>
        /// Riders attached to a vehicle deleted here (<see cref="Relative"/> plus <see cref="CurrentVehicle"/>) that its
        /// passenger list did not name (the list was overwritten by host values that dropped an unresolved element): they
        /// leave it like the listed ones. RelativeObjectSystem indexes the parent's bone history unchecked, a native crash
        /// for a rider near the camera once the vehicle is destroyed. Only bone-attached riders are scanned (few).
        /// </summary>
        private void ReleaseStrayRiders(EntityManager em)
        {
            try
            {
                if (_ridingWorld != _world)
                {
                    _ridingWorld = _world;
                    _ridingQuery = em.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[] { ComponentType.ReadOnly<CurrentVehicle>(), ComponentType.ReadOnly<Relative>() },
                        None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
                    });
                }
                if (_ridingQuery.IsEmptyIgnoreFilter) return;
                _strayRiders.Clear();
                NativeArray<Entity> riders = _ridingQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (Entity rider in riders)
                    {
                        Entity vehicle = em.GetComponentData<CurrentVehicle>(rider).m_Vehicle;
                        if (_releasedVehicles.Contains(vehicle)) _strayRiders.Add((rider, vehicle));
                    }
                }
                finally
                {
                    riders.Dispose();
                }
                foreach ((Entity rider, Entity vehicle) in _strayRiders) ReleaseRider(em, rider, vehicle);
                _strayRiders.Clear();
            }
            catch (Exception ex)
            {
                NoteFailure("releasing riders of deleted vehicles failed: " + ex.Message);
            }
            finally
            {
                _releasedVehicles.Clear();
            }
        }

        private void GoLive(EntityManager em)
        {
            _active = true;
            CloseGate();
            int pinned = 0, orphans = 0;
            try
            {
                // movers from the snapshot: pin them (the load rebuilt back-extrapolated frames that would cycle)
                using (EntityQuery framed = em.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<TransformFrame>(), ComponentType.ReadOnly<Transform>() },
                    None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>() },
                }))
                {
                    NativeArray<Entity> entities = framed.ToEntityArray(Allocator.Temp);
                    try
                    {
                        foreach (Entity e in entities)
                        {
                            Transform t = em.GetComponentData<Transform>(e);
                            Pin(em, e, new TransformFrame(t), t);
                            pinned++;
                        }
                    }
                    finally
                    {
                        entities.Dispose();
                    }
                }
                orphans = SweepOrphans(em);
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Replication, "preparing movers for the session failed", ex);
            }
            _log.Info(LogCategory.Replication, "movers: " + pinned + " pinned at their snapshot pose, " + orphans + " unbound movers removed"
                                               + " (movers the host no longer has follow with its first roster pass)");
        }

        /// <summary>Movers without a host id can never be updated or removed by the host: delete them (guarded).</summary>
        private int SweepOrphans(EntityManager em)
        {
            using (EntityQuery movers = em.CreateEntityQuery(new EntityQueryDesc
            {
                Any = new[] { ComponentType.ReadOnly<Vehicle>(), ComponentType.ReadOnly<Creature>() },
                None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Deleted>() },
            }))
            {
                NativeArray<Entity> entities = movers.ToEntityArray(Allocator.Temp);
                var orphans = new NativeList<Entity>(Allocator.Temp);
                try
                {
                    foreach (Entity e in entities)
                    {
                        if (_ids.GetNetId(e) == 0) orphans.Add(e);
                    }
                    int bound = entities.Length - orphans.Length;
                    if (orphans.Length == 0) return 0;
                    if (bound == 0 || orphans.Length > bound)
                    {
                        _log.Warn(LogCategory.Replication, "movers: " + orphans.Length + " of " + entities.Length + " have no host id; not removing them (the snapshot carried no mover ids?)");
                        return 0;
                    }
                    em.AddComponent(orphans.AsArray(), ComponentType.ReadWrite<Deleted>());
                    return orphans.Length;
                }
                finally
                {
                    orphans.Dispose();
                    entities.Dispose();
                }
            }
        }

        private void CloseGate()
        {
            foreach (string name in Gated)
            {
                try
                {
                    Type type = FamilyClassifier.FindType(name);
                    ComponentSystemBase system = type != null ? _world.GetExistingSystemManaged(type) : null;
                    if (system == null)
                    {
                        _log.Debug(LogCategory.Gate, "mover gate: system not found: " + name);
                        continue;
                    }
                    _gated.Add((system, SystemSwitch.TurnOff(system)));
                }
                catch (Exception ex)
                {
                    _log.Debug(LogCategory.Gate, "mover gate: " + name + ": " + ex.Message);
                }
            }
            _log.Info(LogCategory.Gate, "mover gate closed: " + _gated.Count + " of " + Gated.Length + " systems off");
        }

        /// <summary>Applies a mover batch; returns the number of its despawns that left their mover (never acknowledged).</summary>
        private int ApplyBatch(EntityManager em, List<SyncRecord> records, bool despawnsOnly = false)
        {
            var touched = new List<(Entity, MoverRecord, bool)>();
            int despawnsNotApplied = 0;
            foreach (SyncRecord r in records)
            {
                if (despawnsOnly && r.Kind != SyncRecordKind.MoverDespawn) continue;
                Entity despawning = Entity.Null;
                try
                {
                    switch (r.Kind)
                    {
                        case SyncRecordKind.MoverSpawn:
                        case SyncRecordKind.MoverState:
                        {
                            Entity e = _ids.GetEntity(r.NetId);
                            if (e != Entity.Null && em.Exists(e) && !em.HasComponent<Deleted>(e) && _ids.GetFamily(r.NetId) == Family.Mover && IsStale(em, e, r))
                            {
                                // the id is still bound here to a mover the host despawned (a lost despawn) and reused for
                                // another one: the old mover goes the way a despawn removes it (riders released, trailers
                                // along, motion and links forgotten), the record creates the new one
                                _staleReplaced++;
                                NoteFailure("mover #" + r.NetId + " was still bound here to an older mover (generation or prefab differs); replaced");
                                DeleteLocally(em, e);
                                if (_ids.GetEntity(r.NetId) == e) _ids.Unbind(r.NetId);
                                e = Entity.Null;
                            }
                            // an id bound to something that is not a mover here (ids out of step) is never
                            // turned into one: that would swap a building's or a road's components
                            if (e != Entity.Null && em.Exists(e) && !em.HasComponent<Deleted>(e) && _ids.GetFamily(r.NetId) != Family.Mover)
                            {
                                _familyMismatches++;
                                NoteFailure("mover record #" + r.NetId + " names a " + _ids.GetFamily(r.NetId) + " here; skipped");
                                break;
                            }
                            if (e != Entity.Null && (!em.Exists(e) || em.HasComponent<Deleted>(e)))
                            {
                                _ids.Unbind(r.NetId);
                                e = Entity.Null;
                            }
                            if (e == Entity.Null)
                            {
                                e = Create(em, r.NetId, r.Mover);
                                if (e == Entity.Null) break;
                                _ids.NoteGeneration(r.NetId, r.Generation);
                                ForgetBackoff(r.NetId);
                                _createdThisFrame.Add(e);
                                touched.Add((e, r.Mover, true));
                            }
                            else
                            {
                                // created by an earlier batch of this frame (catch-up applies several): still new, the
                                // game's reference systems add it to its vehicle's passenger list (adding it here too made
                                // a duplicate entry that stayed stale after the creature was deleted)
                                touched.Add((e, r.Mover, _createdThisFrame.Contains(e)));
                            }
                            break;
                        }
                        case SyncRecordKind.MoverDespawn:
                        {
                            Entity e = _ids.GetEntity(r.NetId);
                            // a mover despawn never deletes a building or a road whose id it names
                            if (e != Entity.Null && em.Exists(e) && _ids.GetFamily(r.NetId) != Family.Mover)
                            {
                                _familyMismatches++;
                                NoteFailure("mover despawn #" + r.NetId + " names a " + _ids.GetFamily(r.NetId) + " here; skipped");
                                break;
                            }
                            _ids.Unbind(r.NetId);
                            _force.Remove(r.NetId);
                            _pendingLinks.Remove(r.NetId);
                            ForgetBackoff(r.NetId);
                            if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Deleted>(e)) break;
                            despawning = e;
                            // riders of a vehicle (ObjectEmergeSystem's job on the host, gated here) must not
                            // keep a Relative/CurrentVehicle pointing at it once it is destroyed (RelativeObjectSystem
                            // indexes the parent's bone history unchecked)
                            ReleaseRiders(em, e);
                            em.AddComponent<Deleted>(e);
                            _despawned++;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    NoteFailure("mover record #" + r.NetId + " failed: " + ex.Message);
                    if (despawning != Entity.Null && !TagDeleted(em, despawning)) despawnsNotApplied++;
                }
            }
            if (despawnsOnly) return despawnsNotApplied;
            // states and references after every entity of the batch exists (layouts, riders)
            HashSet<Entity> created = null;
            foreach ((Entity e, MoverRecord rec, bool isNew) in touched)
            {
                try
                {
                    // a mover the same batch despawned stays deleted (no components, no passenger entry)
                    if (em.Exists(e) && !em.HasComponent<Deleted>(e)) ApplyState(em, e, rec, isNew);
                    if (isNew) (created ?? (created = new HashSet<Entity>())).Add(e); // (created this frame)
                }
                catch (Exception ex)
                {
                    NoteFailure("mover state failed: " + ex.Message);
                }
            }
            // the agents of the movers (vanilla reference systems pick them up for movers created this frame)
            foreach (SyncRecord r in records)
            {
                if (r.Kind != SyncRecordKind.MoverAgents || r.AgentLinks == null) continue;
                try
                {
                    Entity e = _ids.GetEntity(r.NetId);
                    if (e == Entity.Null || !em.Exists(e)) continue;
                    if (ApplyLinks(em, e, r.AgentLinks, created != null && created.Contains(e))) _pendingLinks.Remove(r.NetId);
                    else _pendingLinks[r.NetId] = (r.AgentLinks, UnityEngine.Time.realtimeSinceStartupAsDouble + LinkRetrySeconds);
                }
                catch (Exception ex)
                {
                    NoteFailure("mover agent links failed: " + ex.Message);
                }
            }
            // the roster comes last: by now this game holds every mover the host had when it wrote the batch
            foreach (SyncRecord r in records)
            {
                if (r.Kind != SyncRecordKind.MoverRoster) continue;
                try
                {
                    ApplyRoster(em, r);
                }
                catch (Exception ex)
                {
                    NoteFailure("mover roster failed: " + ex.Message);
                }
            }
            return despawnsNotApplied;
        }

        private void ResetRoster()
        {
            _audit.Clear();
            _rosterLocal.Clear();
            _rosterRemove.Clear();
            _rosterRequests.Clear();
            _rosterPasses = _passRemoved = _passAircraft = _requestedAtPassStart = 0;
            _sawRoster = false;
        }

        /// <summary>
        /// One roster window (ARCHITECTURE.md#movers): the movers bound here in <c>[first, first + span)</c> against the host's.
        /// Movers only this game has are deleted (their riders released, their ids unbound); movers it lacks are queued
        /// for a re-send request once they were missing twice. The result is logged after the host's fast pass for this
        /// player (the first <see cref="MoverRosterSchedule.Windows"/> windows cover every id), then per pass of the host
        /// (a window starting at id 1 begins the next) when it changed something.
        /// </summary>
        private void ApplyRoster(EntityManager em, SyncRecord r)
        {
            uint first = r.RangeStart, span = r.RangeCount;
            if (first == 1 && _sawRoster && _audit.Rosters > MoverRosterSchedule.Windows) EndRosterPass(false);
            _sawRoster = true;
            _rosterLocal.Clear();
            ulong end = (ulong)first + span;
            for (ulong i = first; i < end; i++)
            {
                uint id = (uint)i;
                if (_ids.GetFamily(id) != Family.Mover) continue;
                Entity e = _ids.GetEntity(id);
                if (e == Entity.Null) continue;
                if (!em.Exists(e))
                {
                    _ids.Unbind(id); // a binding of a destroyed entity: nothing to compare
                    continue;
                }
                if (!em.HasComponent<Deleted>(e)) _rosterLocal.Add(id);
            }
            _rosterRemove.Clear();
            _audit.OnRoster(first, span, r.Ids, _rosterLocal, _rosterRemove, _rosterRequests);
            foreach (uint id in _rosterRemove)
            {
                Entity e = _ids.GetEntity(id);
                _ids.Unbind(id);
                _force.Remove(id);
                _pendingLinks.Remove(id);
                if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Deleted>(e)) continue;
                if (em.HasComponent<Aircraft>(e)) _passAircraft++;
                ReleaseRiders(em, e);
                em.AddComponent<Deleted>(e);
                _passRemoved++;
                _despawned++;
            }
            if (_audit.Rosters == MoverRosterSchedule.Windows) EndRosterPass(true);
        }

        private void EndRosterPass(bool joinPass)
        {
            _rosterPasses++;
            long requested = _audit.Requested - _requestedAtPassStart;
            if (joinPass || _passRemoved > 0 || requested > 0)
            {
                _log.Info(LogCategory.Replication, "movers: " + (joinPass ? "first roster pass after joining" : "roster pass " + _rosterPasses) + ": removed " + _passRemoved
                                                   + " movers the host no longer has (" + _passAircraft + " aircraft), asked the host for " + requested + " missing here"
                                                   + (joinPass ? " (requests follow when a mover is missing in the next pass too)" : ""));
            }
            _passRemoved = _passAircraft = 0;
            _requestedAtPassStart = _audit.Requested;
        }

        /// <summary>Asks the host for the movers the roster found missing (an integrity re-send request; the host answers with their spawns).</summary>
        private void SendRosterRequests()
        {
            SessionClient client = _sessions.Client;
            if (client == null)
            {
                _rosterRequests.Clear();
                return;
            }
            int at = 0;
            while (at < _rosterRequests.Count)
            {
                int n = Math.Min(SyncBatch.MaxIdsPerRecord, _rosterRequests.Count - at);
                _requestWriter.Reset();
                _requestWriter.RequestIds(SyncRequest.ResendEntities, _rosterRequests.GetRange(at, n));
                ArraySegment<byte> bytes = _requestWriter.Finish();
                client.SendToHost(MessageKind.Proposal, bytes.Array, bytes.Offset, bytes.Count);
                at += n;
            }
            _rosterRequests.Clear();
        }

        /// <summary>
        /// Writes a mover's agent links (<c>Resident.m_Citizen</c>, <c>Pet.m_HouseholdPet</c>,
        /// <c>PersonalCar.m_Keeper</c>, a vehicle's <c>Owner</c>). Unresolved targets are left alone; returns false when one
        /// is still missing (retried). For a mover created in this frame the vanilla reference systems derive the reverse
        /// links (the citizen's <c>CurrentTransport</c>, the owner's <c>OwnedVehicle</c>); later owner changes keep the
        /// owner's vehicle list in step here.
        /// </summary>
        private bool ApplyLinks(EntityManager em, Entity e, MoverAgentLinks links, bool isNew)
        {
            bool complete = true;
            Entity agent = ResolveLink(em, links.Agent, ref complete);
            Entity keeper = ResolveLink(em, links.Keeper, ref complete);
            Entity owner = ResolveLink(em, links.Owner, ref complete);
            if (links.Agent == 0 || agent != Entity.Null)
            {
                if (em.HasComponent<Game.Creatures.Resident>(e))
                {
                    Game.Creatures.Resident resident = em.GetComponentData<Game.Creatures.Resident>(e);
                    if (resident.m_Citizen != agent)
                    {
                        resident.m_Citizen = agent;
                        em.SetComponentData(e, resident);
                    }
                }
                else if (em.HasComponent<Game.Creatures.Pet>(e))
                {
                    Game.Creatures.Pet pet = em.GetComponentData<Game.Creatures.Pet>(e);
                    if (pet.m_HouseholdPet != agent)
                    {
                        pet.m_HouseholdPet = agent;
                        em.SetComponentData(e, pet);
                    }
                }
            }
            if ((links.Keeper == 0 || keeper != Entity.Null) && em.HasComponent<Game.Vehicles.PersonalCar>(e))
            {
                Game.Vehicles.PersonalCar car = em.GetComponentData<Game.Vehicles.PersonalCar>(e);
                if (car.m_Keeper != keeper)
                {
                    car.m_Keeper = keeper;
                    em.SetComponentData(e, car);
                }
            }
            if ((links.Owner == 0 || owner != Entity.Null) && em.HasComponent<Vehicle>(e) && em.HasComponent<Owner>(e))
            {
                Owner current = em.GetComponentData<Owner>(e);
                if (current.m_Owner != owner)
                {
                    if (!isNew)
                    {
                        SetOwnedVehicle(em, current.m_Owner, e, false);
                        SetOwnedVehicle(em, owner, e, true);
                    }
                    em.SetComponentData(e, new Owner(owner));
                }
            }
            if (complete) _linksApplied++;
            DeletionCascade.IndexMover(em, e); // a later deletion of its owner, citizen or pet finds it
            return complete;
        }

        private Entity ResolveLink(EntityManager em, uint id, ref bool complete)
        {
            if (id == 0) return Entity.Null;
            Entity e = _ids.GetEntity(id);
            if (e != Entity.Null && em.Exists(e) && !em.HasComponent<Deleted>(e)) return e;
            complete = false;
            return Entity.Null;
        }

        private static void SetOwnedVehicle(EntityManager em, Entity owner, Entity vehicle, bool add)
        {
            if (owner == Entity.Null || !em.Exists(owner) || !em.HasBuffer<OwnedVehicle>(owner)) return;
            DynamicBuffer<OwnedVehicle> list = em.GetBuffer<OwnedVehicle>(owner);
            for (int i = list.Length - 1; i >= 0; i--)
            {
                if (list[i].m_Vehicle != vehicle) continue;
                if (add) return;
                list.RemoveAt(i);
            }
            if (add) list.Add(new OwnedVehicle(vehicle));
        }

        private void RetryLinks(EntityManager em)
        {
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            _linkScratch.Clear();
            _linkScratch.AddRange(_pendingLinks.Keys);
            foreach (uint id in _linkScratch)
            {
                (MoverAgentLinks links, double until) = _pendingLinks[id];
                Entity e = _ids.GetEntity(id);
                bool done;
                try
                {
                    done = e == Entity.Null || !em.Exists(e) || ApplyLinks(em, e, links, false);
                }
                catch (Exception ex)
                {
                    _log.Debug(LogCategory.Replication, "mover agent links retry failed: " + ex.Message);
                    done = true;
                }
                if (done || now > until) _pendingLinks.Remove(id);
            }
        }

        /// <summary>
        /// Whether a mover record names another mover than the one its id is bound to here (a despawn this client lost and
        /// the id reused): by the id's generation when known, else by the prefab (a mover never changes its prefab).
        /// </summary>
        private bool IsStale(EntityManager em, Entity e, SyncRecord r)
        {
            bool known = _ids.TryGetGeneration(r.NetId, out byte generation);
            bool prefabsKnown = false, same = true;
            if (!known && r.Mover != null && em.HasComponent<PrefabRef>(e))
            {
                Entity bound = em.GetComponentData<PrefabRef>(e).m_Prefab;
                Entity named = _ids.GetEntity(r.Mover.PrefabNetId);
                prefabsKnown = bound != Entity.Null && named != Entity.Null;
                same = bound == named;
            }
            return MoverBindingCheck.Check(r.Generation, known, generation, prefabsKnown, same) == BoundRecordVerdict.Replace;
        }

        /// <summary>Tags a mover whose despawn threw: deleted anyway (its id is unbound already). False when even that fails.</summary>
        private bool TagDeleted(EntityManager em, Entity e)
        {
            try
            {
                if (em.Exists(e) && !em.HasComponent<Deleted>(e)) em.AddComponent<Deleted>(e);
                return true;
            }
            catch (Exception)
            {
                return !em.Exists(e) || em.HasComponent<Deleted>(e);
            }
        }

        private long _staleReplaced;

        /// <summary>The roster back-off of an id ends with the mover it was for (a reused id starts over).</summary>
        private void ForgetBackoff(uint id) => _audit.Forget(id);

        /// <summary>Movers still bound under a reused id that a record replaced (diagnostics).</summary>
        public long StaleReplaced => _staleReplaced;

        private Entity Create(EntityManager em, uint netId, MoverRecord rec)
        {
            if (!_ids.MayBind(netId)) return Entity.Null; // beyond the host's announced id range
            Entity prefab = _ids.GetEntity(rec.PrefabNetId);
            if (prefab == Entity.Null || !em.Exists(prefab)) return Entity.Null;
            // only a vehicle or creature prefab makes a mover (with prefab ids out of step, the fallback archetype of a
            // building prefab created a building bound as a mover)
            if (!em.HasComponent<MovingObjectData>(prefab) && !em.HasComponent<VehicleData>(prefab) && !em.HasComponent<CreatureData>(prefab))
            {
                NoteFailure("mover #" + netId + " names a prefab that is no vehicle or creature here; not created");
                return Entity.Null;
            }
            Archetypes types = TypesOf(em, prefab, rec.TrainController);
            EntityArchetype archetype = ArchetypeOf(em, prefab, rec.TrainController, rec.Kind == MoverKind.Stopped);
            if (!archetype.Valid || types == null) return Entity.Null;
            Entity e = em.CreateEntity(archetype);
            em.AddComponent<Applied>(e);
            if (em.HasComponent<UpdateFrame>(e)) em.SetSharedComponent(e, new UpdateFrame(rec.UpdateFrame));
            em.SetComponentData(e, new PrefabRef(prefab));
            var t = new Transform(new float3(rec.X, rec.Y, rec.Z), new quaternion(rec.QX, rec.QY, rec.QZ, rec.QW));
            if (em.HasComponent<Transform>(e)) em.SetComponentData(e, t);
            if (em.HasComponent<PseudoRandomSeed>(e)) em.SetComponentData(e, new PseudoRandomSeed(rec.Seed));
            if (em.HasBuffer<TransformFrame>(e)) Pin(em, e, new TransformFrame(t), t);
            if (em.HasComponent<InterpolatedTransform>(e)) em.SetComponentData(e, new InterpolatedTransform(t));
            _ids.Bind(e, netId, Family.Mover);
            _spawned++;
            return e;
        }

        private EntityArchetype ArchetypeOf(EntityManager em, Entity prefab, bool trainController, bool stopped)
        {
            if (trainController && em.HasComponent<TrainObjectData>(prefab))
            {
                TrainObjectData train = em.GetComponentData<TrainObjectData>(prefab);
                return stopped ? train.m_StoppedControllerArchetype : train.m_ControllerArchetype;
            }
            if (stopped && em.HasComponent<MovingObjectData>(prefab)) return em.GetComponentData<MovingObjectData>(prefab).m_StoppedArchetype;
            return em.HasComponent<ObjectData>(prefab) ? em.GetComponentData<ObjectData>(prefab).m_Archetype : default;
        }

        private Archetypes TypesOf(EntityManager em, Entity prefab, bool trainController)
        {
            if (_archetypes.TryGetValue((prefab, trainController), out Archetypes cached)) return cached;
            EntityArchetype moving = ArchetypeOf(em, prefab, trainController, false);
            EntityArchetype stopped = ArchetypeOf(em, prefab, trainController, true);
            Archetypes result = null;
            if (moving.Valid && stopped.Valid) result = new Archetypes { Moving = TypeSet(moving), Stopped = TypeSet(stopped) };
            _archetypes[(prefab, trainController)] = result;
            return result;
        }

        private HashSet<ComponentType> TypeSet(EntityArchetype archetype)
        {
            var set = new HashSet<ComponentType>();
            NativeArray<ComponentType> types = archetype.GetComponentTypes(Allocator.Temp);
            try
            {
                foreach (ComponentType t in types)
                {
                    var rw = ComponentType.FromTypeIndex(t.TypeIndex);
                    if (!_skipTypes.Contains(rw)) set.Add(rw);
                }
            }
            finally
            {
                types.Dispose();
            }
            return set;
        }

        /// <summary>
        /// A failure of one mover record, state, link or motion message: counted (diagnostics), logged as a
        /// warning at most every 10 s. The integrity check asks the host again for movers that stay wrong.
        /// </summary>
        private void NoteFailure(string what)
        {
            _recordFailures++;
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (now - _lastFailureLog < 10)
            {
                _log.Debug(LogCategory.Replication, what);
                return;
            }
            _lastFailureLog = now;
            _log.Warn(LogCategory.Replication, what + " (" + _recordFailures + " mover failure(s) this session, " + _familyMismatches + " family mismatch(es))");
        }

        /// <summary>
        /// The riders of a vehicle about to be deleted (its own and its trailers' passenger lists) leave it: no
        /// Relative or CurrentVehicle pointing at a destroyed entity; they are hidden (Unspawned) until the host's next
        /// state for them arrives (usually in the same batch).
        /// </summary>
        private void ReleaseRiders(EntityManager em, Entity vehicle)
        {
            _releasedVehicles.Add(vehicle); // riders its list does not name are released at the end of the apply
            DeletionCascade.UnindexMover(em, vehicle);
            ReleasePassengers(em, vehicle);
            if (!em.HasBuffer<LayoutElement>(vehicle)) return;
            DynamicBuffer<LayoutElement> layout = em.GetBuffer<LayoutElement>(vehicle, true);
            var members = new Entity[layout.Length];
            for (int i = 0; i < layout.Length; i++) members[i] = layout[i].m_Vehicle;
            foreach (Entity member in members)
            {
                if (member != vehicle && member != Entity.Null && em.Exists(member)) ReleasePassengers(em, member);
            }
        }

        private void ReleasePassengers(EntityManager em, Entity vehicle)
        {
            if (!em.HasBuffer<Passenger>(vehicle)) return;
            DynamicBuffer<Passenger> list = em.GetBuffer<Passenger>(vehicle, true);
            var riders = new Entity[list.Length];
            for (int i = 0; i < list.Length; i++) riders[i] = list[i].m_Passenger;
            _releasedVehicles.Add(vehicle);
            foreach (Entity rider in riders) ReleaseRider(em, rider, vehicle);
        }

        private void ReleaseRider(EntityManager em, Entity rider, Entity vehicle)
        {
            if (rider == Entity.Null || !em.Exists(rider) || em.HasComponent<Deleted>(rider)) return;
            if (em.HasComponent<CurrentVehicle>(rider) && em.GetComponentData<CurrentVehicle>(rider).m_Vehicle != vehicle) return;
            Transform at = em.Exists(vehicle) && em.HasComponent<Transform>(vehicle) ? em.GetComponentData<Transform>(vehicle)
                           : em.HasComponent<Transform>(rider) ? em.GetComponentData<Transform>(rider) : default;
            if (em.HasComponent<Relative>(rider)) em.RemoveComponent<Relative>(rider);
            if (em.HasComponent<CurrentVehicle>(rider)) em.RemoveComponent<CurrentVehicle>(rider);
            ExitVehicle(em, rider, at);
            if (!em.HasComponent<Unspawned>(rider))
            {
                em.AddComponent<Unspawned>(rider);
                if (!em.HasComponent<BatchesUpdated>(rider)) em.AddComponent<BatchesUpdated>(rider);
            }
            _ridersReleased++;
        }

        /// <summary>
        /// A creature that left its vehicle gets the components of its moving state back, as the game's
        /// <c>ObjectEmergeSystem</c> does (off on clients): without <c>CurrentVehicle</c> and without <c>Moving</c> it had no
        /// update-group marker at all (ARCHITECTURE.md#client-apply), and its frames and navigation were missing. It stays
        /// hidden (<c>Unspawned</c>) at the vehicle's position until the host's next state for it arrives.
        /// </summary>
        private void ExitVehicle(EntityManager em, Entity rider, Transform at)
        {
            Entity prefab = em.HasComponent<PrefabRef>(rider) ? em.GetComponentData<PrefabRef>(rider).m_Prefab : Entity.Null;
            Archetypes types = prefab != Entity.Null && em.Exists(prefab) ? TypesOf(em, prefab, false) : null;
            if (types == null)
            {
                if (!em.HasComponent<Moving>(rider) && !em.HasComponent<Stopped>(rider)) em.AddComponent<Stopped>(rider);
                return;
            }
            if (em.HasComponent<Stopped>(rider) || em.HasComponent<Moving>(rider)) return;
            foreach (ComponentType t in types.Moving)
            {
                if (em.HasComponent(rider, t)) continue;
                em.AddComponent(rider, t);
                if (t == ComponentType.ReadWrite<TransformFrame>()) Pin(em, rider, new TransformFrame(at), at);
                else if (t == ComponentType.ReadWrite<InterpolatedTransform>()) em.SetComponentData(rider, new InterpolatedTransform(at));
            }
            if (em.HasComponent<Transform>(rider)) em.SetComponentData(rider, at);
            if (!em.HasComponent<Updated>(rider)) em.AddComponent<Updated>(rider);
        }

        /// <summary>
        /// Removes a mover on this client the way a host despawn does (<see cref="DeletionCascade.DeleteMover"/>): its id is
        /// released (the roster asks the host for it again if the host still has it), its riders leave it, a creature leaves
        /// its vehicle's passenger list, the trailers of a vehicle go with it (the game's <c>VehicleUtils.DeleteVehicle</c>).
        /// </summary>
        public bool DeleteLocally(EntityManager em, Entity e)
        {
            if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Deleted>(e)) return false;
            ForgetLocal(em, e);
            if (em.HasBuffer<LayoutElement>(e))
            {
                DynamicBuffer<LayoutElement> layout = em.GetBuffer<LayoutElement>(e, true);
                var members = new Entity[layout.Length];
                for (int i = 0; i < layout.Length; i++) members[i] = layout[i].m_Vehicle;
                foreach (Entity member in members)
                {
                    if (member == e || member == Entity.Null || !em.Exists(member) || em.HasComponent<Deleted>(member)) continue;
                    ForgetLocal(em, member);
                    ReleasePassengers(em, member);
                    em.AddComponent<Deleted>(member);
                    _despawned++;
                }
            }
            ReleaseRiders(em, e);
            em.AddComponent<Deleted>(e);
            _despawned++;
            return true;
        }

        /// <summary>
        /// The part of a mover deletion the game's systems that are off on clients would do (<see cref="RemovalCascadeSystem"/>:
        /// a vehicle the player's bulldozing deleted with its building, <c>SubElementDeleteSystem</c>): its riders leave it
        /// (<c>ObjectEmergeSystem</c>; a rider kept pointing at the destroyed vehicle, which <c>RelativeObjectSystem</c> indexes
        /// unchecked), and a creature leaves its vehicle's passenger list. The id stays bound until the capture proposed it.
        /// </summary>
        public void OnDeletedLocally(EntityManager em, Entity e)
        {
            if (e == Entity.Null || !em.Exists(e)) return;
            if (em.HasComponent<Vehicle>(e)) ReleaseRiders(em, e);
            if (em.HasComponent<CurrentVehicle>(e)) SetPassenger(em, em.GetComponentData<CurrentVehicle>(e).m_Vehicle, e, false);
        }

        private void ForgetLocal(EntityManager em, Entity e)
        {
            uint id = _ids != null ? _ids.GetNetId(e) : 0;
            if (id != 0 && _ids.GetFamily(id) == Family.Mover)
            {
                _ids.Unbind(id);
                _force.Remove(id);
                _pendingLinks.Remove(id);
            }
            if (em.HasComponent<CurrentVehicle>(e)) SetPassenger(em, em.GetComponentData<CurrentVehicle>(e).m_Vehicle, e, false);
        }

        /// <summary>
        /// The bone indices of a rider, checked against the vehicle (RelativeObjectSystem reads
        /// <c>boneHistory[parent]</c> when y &gt;= 0 and the prefab's <c>SubMesh[z]</c> unchecked); an index the vehicle does
        /// not have becomes -1 (the rider is placed relative to the vehicle itself).
        /// </summary>
        private static int3 SafeBones(EntityManager em, Entity vehicle, MoverRecord rec)
        {
            int y = rec.BoneY, z = rec.BoneZ;
            if (y >= 0 && (!em.HasBuffer<BoneHistory>(vehicle) || y >= em.GetBuffer<BoneHistory>(vehicle, true).Length)) y = -1;
            if (z >= 0)
            {
                Entity prefab = em.HasComponent<PrefabRef>(vehicle) ? em.GetComponentData<PrefabRef>(vehicle).m_Prefab : Entity.Null;
                if (y < 0 || prefab == Entity.Null || !em.HasBuffer<SubMesh>(prefab) || z >= em.GetBuffer<SubMesh>(prefab, true).Length) z = -1;
            }
            return new int3(rec.BoneX, y, z);
        }

        /// <summary>Mirrors the host's discrete state on an existing (or just created) mover.</summary>
        private void ApplyState(EntityManager em, Entity e, MoverRecord rec, bool isNew)
        {
            uint netId = _ids.GetNetId(e);
            Entity prefab = em.HasComponent<PrefabRef>(e) ? em.GetComponentData<PrefabRef>(e).m_Prefab : Entity.Null;
            Archetypes types = prefab != Entity.Null ? TypesOf(em, prefab, rec.TrainController) : null;
            bool structural = false;
            Entity vehicle = rec.Vehicle != 0 ? _ids.GetEntity(rec.Vehicle) : Entity.Null;
            if (vehicle != Entity.Null && (!em.Exists(vehicle) || em.HasComponent<Deleted>(vehicle))) vehicle = Entity.Null; // never a deleted vehicle
            // and never something that is not a vehicle (the id may name any family)
            if (vehicle != Entity.Null && (_ids.GetFamily(rec.Vehicle) != Family.Mover || !em.HasComponent<Vehicle>(vehicle))) vehicle = Entity.Null;
            bool riding = rec.Kind == MoverKind.Riding && vehicle != Entity.Null;
            bool inVehicle = rec.Kind == MoverKind.Inside || rec.Kind == MoverKind.Riding;
            bool wantStopped = rec.Kind == MoverKind.Stopped;
            var transform = new Transform(new float3(rec.X, rec.Y, rec.Z), new quaternion(rec.QX, rec.QY, rec.QZ, rec.QW));

            if (types != null)
            {
                bool isStopped = em.HasComponent<Stopped>(e);
                if (!inVehicle && wantStopped != isStopped)
                {
                    HashSet<ComponentType> from = isStopped ? types.Stopped : types.Moving;
                    HashSet<ComponentType> to = wantStopped ? types.Stopped : types.Moving;
                    foreach (ComponentType t in from)
                    {
                        if (!to.Contains(t) && em.HasComponent(e, t)) em.RemoveComponent(e, t);
                    }
                    structural = true;
                }
                if (inVehicle)
                {
                    foreach (ComponentType t in RideRemoved)
                    {
                        if (em.HasComponent(e, t))
                        {
                            em.RemoveComponent(e, t);
                            structural = true;
                        }
                    }
                    if (!riding && em.HasComponent<InterpolatedTransform>(e)) em.RemoveComponent<InterpolatedTransform>(e);
                    if (!riding && em.HasComponent<Relative>(e)) em.RemoveComponent<Relative>(e);
                    if (!em.HasComponent<CurrentVehicle>(e))
                    {
                        em.AddComponent<CurrentVehicle>(e);
                        structural = true;
                    }
                    else if (!isNew)
                    {
                        SetPassenger(em, em.GetComponentData<CurrentVehicle>(e).m_Vehicle, e, false);
                    }
                    em.SetComponentData(e, new CurrentVehicle(vehicle, (CreatureVehicleFlags)rec.VehicleFlags));
                    // Creatures.ReferencesSystem fills Passenger for created creatures; later boardings are ours
                    if (!isNew) SetPassenger(em, vehicle, e, true);
                    if (riding)
                    {
                        if (!em.HasComponent<InterpolatedTransform>(e)) em.AddComponentData(e, new InterpolatedTransform(transform));
                        if (!em.HasComponent<Relative>(e)) em.AddComponent<Relative>(e);
                        em.SetComponentData(e, new Relative
                        {
                            m_Position = new float3(rec.RX, rec.RY, rec.RZ),
                            m_Rotation = new quaternion(rec.RQX, rec.RQY, rec.RQZ, rec.RQW),
                            m_BoneIndex = SafeBones(em, vehicle, rec),
                        });
                    }
                }
                else
                {
                    if (em.HasComponent<Relative>(e))
                    {
                        em.RemoveComponent<Relative>(e);
                        structural = true;
                    }
                    if (em.HasComponent<CurrentVehicle>(e))
                    {
                        SetPassenger(em, em.GetComponentData<CurrentVehicle>(e).m_Vehicle, e, false);
                        em.RemoveComponent<CurrentVehicle>(e);
                        structural = true;
                    }
                    // the base archetype of the target state (re-adds frames and navigation after a ride or a swap)
                    foreach (ComponentType t in wantStopped ? types.Stopped : types.Moving)
                    {
                        if (em.HasComponent(e, t)) continue;
                        em.AddComponent(e, t);
                        structural = true;
                        if (t == ComponentType.ReadWrite<TransformFrame>()) Pin(em, e, new TransformFrame(transform), transform);
                        else if (t == ComponentType.ReadWrite<InterpolatedTransform>()) em.SetComponentData(e, new InterpolatedTransform(transform));
                    }
                }
            }

            bool unspawned = rec.Unspawned || (inVehicle && !riding);
            if (unspawned != em.HasComponent<Unspawned>(e))
            {
                if (unspawned) em.AddComponent<Unspawned>(e);
                else em.RemoveComponent<Unspawned>(e);
                if (!isNew && !em.HasComponent<BatchesUpdated>(e)) em.AddComponent<BatchesUpdated>(e);
            }
            if (rec.HasHumanFlags && em.HasComponent<Human>(e)) em.SetComponentData(e, new Human((HumanFlags)rec.HumanFlags));
            if (rec.Route != 0)
            {
                Entity route = _ids.GetEntity(rec.Route);
                if (route != Entity.Null && em.Exists(route) && !em.HasComponent<Deleted>(route))
                {
                    if (!em.HasComponent<CurrentRoute>(e)) em.AddComponent<CurrentRoute>(e);
                    em.SetComponentData(e, new CurrentRoute(route));
                }
            }
            else if (em.HasComponent<CurrentRoute>(e) && !isNew)
            {
                em.RemoveComponent<CurrentRoute>(e);
                structural = true;
            }
            ApplyLayout(em, e, rec);

            // the pose: motion owns movers in interest; everything else is (re)pinned at the host's pose
            bool inInterest = netId != 0 && netId <= NetSlot.MaxValue && _stager.IsAssigned(new NetSlot(netId));
            if (!inInterest && !riding && em.HasComponent<Transform>(e))
            {
                em.SetComponentData(e, transform);
                if (em.HasBuffer<TransformFrame>(e)) Pin(em, e, new TransformFrame(transform), transform);
                if (em.HasComponent<InterpolatedTransform>(e)) em.SetComponentData(e, new InterpolatedTransform(transform));
            }
            if (structural && !isNew)
            {
                // the vanilla swaps tag Updated (UpdateGroupSystem then resets the frames; the writer refills them)
                if (!em.HasComponent<Updated>(e)) em.AddComponent<Updated>(e);
                if (inInterest) _force.Add(netId);
            }
        }

        /// <summary>Keeps the vehicle's <c>Passenger</c> list in step with a boarding or leaving creature.</summary>
        private static void SetPassenger(EntityManager em, Entity vehicle, Entity creature, bool add)
        {
            if (vehicle == Entity.Null || !em.Exists(vehicle) || !em.HasBuffer<Passenger>(vehicle)) return;
            DynamicBuffer<Passenger> passengers = em.GetBuffer<Passenger>(vehicle);
            for (int i = passengers.Length - 1; i >= 0; i--)
            {
                if (passengers[i].m_Passenger != creature) continue;
                if (add) return;
                passengers.RemoveAt(i);
            }
            if (add) passengers.Add(new Passenger(creature));
        }

        private void ApplyLayout(EntityManager em, Entity e, MoverRecord rec)
        {
            if (rec.Controller != 0)
            {
                Entity controller = _ids.GetEntity(rec.Controller);
                if (controller != Entity.Null && em.Exists(controller) && !em.HasComponent<Deleted>(controller))
                {
                    if (!em.HasComponent<Controller>(e)) em.AddComponent<Controller>(e);
                    em.SetComponentData(e, new Controller(controller));
                }
            }
            if (rec.Layout != null)
            {
                DynamicBuffer<LayoutElement> layout = em.HasBuffer<LayoutElement>(e) ? em.GetBuffer<LayoutElement>(e) : em.AddBuffer<LayoutElement>(e);
                layout.Clear();
                foreach (uint id in rec.Layout)
                {
                    Entity member = id != 0 ? _ids.GetEntity(id) : Entity.Null;
                    if (member != Entity.Null && em.Exists(member) && !em.HasComponent<Deleted>(member)) layout.Add(new LayoutElement(member));
                }
                // members carry Controller (vanilla ControllerSystem derives it from the layout on load)
                for (int i = 0; i < layout.Length; i++)
                {
                    Entity member = layout[i].m_Vehicle;
                    if (!em.HasComponent<Controller>(member)) continue;
                    em.SetComponentData(member, new Controller(e));
                    layout = em.GetBuffer<LayoutElement>(e);
                }
            }
        }

        /// <summary>All four slots hold the same pose with zero velocity (ARCHITECTURE.md#movers).</summary>
        private static void Pin(EntityManager em, Entity e, TransformFrame frame, Transform transform)
        {
            frame.m_Velocity = default(float3);
            DynamicBuffer<TransformFrame> ring = em.GetBuffer<TransformFrame>(e);
            ring.ResizeUninitialized(4);
            for (int i = 0; i < 4; i++) ring[i] = frame;
            if (em.HasComponent<Moving>(e)) em.SetComponentData(e, default(Moving));
        }

        // ================================================================== motion (PreCulling)

        /// <summary>PreCulling, before <c>PreCullingSystem</c>: stage motion, write the slots the renderer reads, report the view.</summary>
        public void Write()
        {
            SessionClient client = _sessions.Client;
            if (client == null || !_active || !EnsureWorld()) return;
            long start = Perf.Start();
            try
            {
                WriteFrame(client);
            }
            finally
            {
                Perf.Stop(Perf.MotionWrite, start);
            }
        }

        private void WriteFrame(SessionClient client)
        {
            EntityManager em = _world.EntityManager;
            DrainMotion(em);
            uint render = _rendering.frameIndex;
            _lights.Clear();
            for (int i = 0; i < _stager.SlotCapacity; i++)
            {
                try
                {
                    WriteSlot(em, i, render);
                }
                catch (Exception ex)
                {
                    _log.Debug(LogCategory.Motion, "motion write failed: " + ex.Message);
                }
            }
            if (_lights.Count > 0)
            {
                var array = new NativeArray<Entity>(_lights.Count, Allocator.Temp);
                try
                {
                    for (int i = 0; i < _lights.Count; i++) array[i] = _lights[i];
                    em.AddComponent(array, ComponentType.ReadWrite<EffectsUpdated>());
                }
                finally
                {
                    array.Dispose();
                }
            }
            Report(client);
        }

        private void DrainMotion(EntityManager em)
        {
            while (_motion.Count > 0)
            {
                (MessageKind kind, byte[] body) = _motion.Dequeue();
                try
                {
                    if (kind == MessageKind.MotionControl)
                    {
                        _pins.Clear();
                        try
                        {
                            _stager.ApplyControl(body, 0, body.Length, _pins);
                        }
                        finally
                        {
                            // ops before a malformed one stay applied; their released movers are pinned anyway
                            foreach (MotionPin pin in _pins)
                            {
                                try
                                {
                                    ApplyPin(em, pin);
                                }
                                catch (Exception ex)
                                {
                                    NoteFailure("motion pin failed: " + ex.Message);
                                }
                            }
                        }
                    }
                    else
                    {
                        _stager.ApplyDatagram(body, 0, body.Length, out uint tick);
                        FollowerClock.Instance?.OnHostTick(tick);
                    }
                }
                catch (DecodeException ex)
                {
                    _log.Debug(LogCategory.Motion, "malformed " + kind + " dropped: " + ex.Message);
                }
                catch (Exception ex)
                {
                    NoteFailure(kind + " failed: " + ex.Message); // one bad message never loses the frame's slot writes
                }
            }
        }

        private void ApplyPin(EntityManager em, MotionPin pin)
        {
            Entity e = _ids.GetEntity(pin.Slot.Value);
            if (e == Entity.Null || !em.Exists(e) || !em.HasBuffer<TransformFrame>(e) || !em.HasComponent<Transform>(e)) return;
            Transform t = em.GetComponentData<Transform>(e);
            TransformFrame frame = new TransformFrame(t);
            if (pin.HasFrame)
            {
                frame = ToFrame(pin.Frame);
                t = new Transform(frame.m_Position, frame.m_Rotation);
                em.SetComponentData(e, t);
            }
            Pin(em, e, frame, t);
        }

        private void WriteSlot(EntityManager em, int index, uint render)
        {
            while (_slots.Count <= index) _slots.Add(new SlotState());
            SlotState s = _slots[index];
            if (!_stager.TryGetAssignment(index, out NetSlot slot, out byte hostFrame, out int version))
            {
                s.Slot = NetSlot.None;
                return;
            }
            Entity e = _ids.GetEntity(slot.Value);
            if (s.Slot != slot || s.Entity != e)
            {
                s.Slot = slot;
                s.Entity = e;
                s.Version = -1;
                s.Written = false;
                s.Lights = 0;
                s.UpdateFrame = hostFrame;
                if (e != Entity.Null && em.Exists(e) && em.HasComponent<UpdateFrame>(e)) s.UpdateFrame = em.GetSharedComponent<UpdateFrame>(e).m_Index & 15;
            }
            if (e == Entity.Null) return;
            uint u = s.UpdateFrame;
            uint baseTick = unchecked(render - 32);
            uint f1 = unchecked(baseTick - ((baseTick - u) & 15));
            // most slots were already written for this window (one in sixteen changes per frame); that is decided
            // before the entity lookups, which only the slots that write need
            if (s.Written && s.F1 == f1 && s.Version == version && !_force.Contains(slot.Value)) return;
            if (!em.Exists(e) || !em.HasBuffer<TransformFrame>(e)) return;
            _force.Remove(slot.Value);
            DynamicBuffer<TransformFrame> ring = em.GetBuffer<TransformFrame>(e);
            if (ring.Length != 4)
            {
                Transform current = em.GetComponentData<Transform>(e);
                ring.ResizeUninitialized(4);
                for (int k = 0; k < 4; k++) ring[k] = new TransformFrame(current);
            }
            uint lights = 0;
            MotionFrame shown = default;
            for (int k = 0; k < 3; k++)
            {
                uint tick = unchecked(f1 + (uint)(16 * k));
                if (!_stager.TrySample(index, tick, out MotionFrame f)) return;
                ring[(int)((tick >> 4) & 3)] = ToFrame(f);
                lights |= f.Flags;
                if (k == 1) shown = f;
            }
            // keep Transform and Moving coherent with what is shown (culling, audio, selection; R15 §2.4, §2.6 step 6)
            const float half = 2f / 15f;
            em.SetComponentData(e, new Transform(new float3(shown.X + shown.VX * half, shown.Y + shown.VY * half, shown.Z + shown.VZ * half),
                                                 new quaternion(shown.QX, shown.QY, shown.QZ, shown.QW)));
            if (em.HasComponent<Moving>(e))
            {
                Moving moving = em.GetComponentData<Moving>(e);
                moving.m_Velocity = new float3(shown.VX, shown.VY, shown.VZ);
                em.SetComponentData(e, moving);
            }
            lights &= (uint)LightMask;
            if (s.Written && lights != s.Lights) _lights.Add(e);
            s.Lights = lights;
            s.F1 = f1;
            s.Version = version;
            s.Written = true;
        }

        private static TransformFrame ToFrame(in MotionFrame f)
        {
            return new TransformFrame
            {
                m_Position = new float3(f.X, f.Y, f.Z),
                m_Velocity = new float3(f.VX, f.VY, f.VZ),
                m_Rotation = new quaternion(f.QX, f.QY, f.QZ, f.QW),
                m_Flags = (TransformFlags)f.Flags,
                m_State = (TransformState)f.State,
                m_Activity = f.Activity,
                m_StateTimer = f.StateTimer,
            };
        }

        /// <summary>View report and motion ack, about 10 Hz (ARCHITECTURE.md#simulation-values, #movers).</summary>
        private void Report(SessionClient client)
        {
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (now - _lastReport < ReportInterval) return;
            _lastReport = now;
            float3 position = _camera.position;
            float3 pivot = position;
            IGameCameraController controller = _camera.activeCameraController;
            if (controller != null)
            {
                UnityEngine.Vector3 p = controller.pivot;
                pivot = new float3(p.x, p.y, p.z);
            }
            float zoom = _camera.zoom;
            if (!(zoom > 0f) || float.IsInfinity(zoom)) zoom = math.distance(position, pivot);
            _view.PivotX = Finite(pivot.x);
            _view.PivotZ = Finite(pivot.z);
            _view.Zoom = Finite(zoom);
            _view.CamX = Finite(position.x);
            _view.CamY = Finite(position.y);
            _view.CamZ = Finite(position.z);
            AttentionValues.Instance?.FillViewReport(_view); // selected entity and infoview topics
            AgentSync.Instance?.FillViewReport(_view); // the followed entity (follow camera)
            _report.Reset();
            ViewReport.Write(_report, _view);
            client.SendToHost(MessageKind.View, _report.Buffer, 0, _report.Length);
            if (_stager.HasAppliedControl)
            {
                _report.Reset();
                MotionClientStager.WriteAck(_report, _stager.LastAppliedControlSeq);
                client.SendToHost(MessageKind.Ack, _report.Buffer, 0, _report.Length);
            }
        }

        private static float Finite(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;

        /// <summary>Diagnostics dump lines.</summary>
        public void Describe(List<string> lines)
        {
            if (!_active) return;
            lines.Add("movers: " + Status + "; motion frames accepted " + _stager.FramesAccepted + ", dropped " + _stager.FramesDropped);
            lines.Add("mover roster: " + _audit.Rosters + " windows checked, " + _rosterPasses + " passes, " + _audit.Removed + " movers removed (not on the host), "
                      + _audit.Requested + " asked for (missing here), " + _audit.Pending + " missing once");
        }

        /// <summary>One line for diagnostics.</summary>
        public string Status => _active ? "movers +" + _spawned + " -" + _despawned + ", " + _stager.AssignedCount + " in motion" + (_linksApplied > 0 ? ", " + _linksApplied + " agent links" : "")
                                          + (_recordFailures + _motionDropped + _ridersReleased > 0 ? ", " + _recordFailures + " failures, " + _familyMismatches + " family mismatches, "
                                             + _motionDropped + " motion dropped, " + _ridersReleased + " riders released" : "") : "";
    }
}
