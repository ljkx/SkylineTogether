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
    /// Client side of mover replication (ARCH K4, §4.9). Applies the host's mover records right after the structure
    /// apply (before every Modification phase, so <c>Applied</c> keeps the host's <c>UpdateFrame</c>,
    /// <c>UpdateGroupSystem.cs:408-416</c>): spawns from the prefab's own archetype, despawns with <c>Deleted</c> and
    /// mirrors the vanilla type swaps (moving ↔ stopped from the prefab archetypes, riding/inside a vehicle as
    /// <c>ResidentAISystem</c> does it, <c>ResidentAISystem.cs:200-215, 3910-3920</c>). Motion arrives on the motion
    /// channel and is staged by <see cref="MotionClientStager"/>; every frame, in PreCulling before
    /// <c>PreCullingSystem</c> (after <c>RenderingSystem.PrepareRendering</c> computed the render tick), the slots the
    /// interpolation will read are written "just in time" (R15 §2.2-2.6): <c>F1 = u + 16·((R − u − 32) >> 4)</c>, frames
    /// for <c>F1</c>, <c>F1 + 16</c> and <c>F1 + 32</c>. Movers outside interest are pinned: all four slots hold the
    /// same pose with zero velocity, so the ring never cycles back. Movers are never tagged <c>Updated</c> for motion
    /// (it would reset their frames, <c>UpdateGroupSystem.cs:561-615</c>). Main thread only.
    /// </summary>
    public sealed class MoverClient
    {
        private const double ReportInterval = 0.1;
        private const int MaxBatchesPerFrame = 64;
        private const TransformFlags LightMask = TransformFlags.MainLights | TransformFlags.ExtraLights | TransformFlags.WarningLights | TransformFlags.WorkLights;

        /// <summary>Modification systems that would create or move agents on a follower (R15 §4.6, ARCH §6.4).</summary>
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
        // w3/agents: agent links whose citizen, pet, keeper or owner is not bound here yet (retried for a few seconds)
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

        public MoverClient(SessionManager sessions, ILog log)
        {
            _sessions = sessions;
            _log = log;
        }

        public int InInterest => _stager.AssignedCount;

        public void OnClientStarted(SessionClient client)
        {
            _active = false;
            _pendingLive = false;
            _pendingLinks.Clear(); // w3/agents
            _linksApplied = 0;
            _batches.Clear();
            _motion.Clear();
            _stager = new MotionClientStager();
            _slots.Clear();
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
                    // final review: with the movers feature off nothing applies (or releases) these batches; holding
                    // them froze the apply acks, so the host never reused a released net id again
                    if (SyncBatch.IsMoverBatch(e.Body, e.BodyOffset, e.BodyCount) && Diagnostics.Features.Movers.IsAvailable)
                    {
                        // w2/ids: held until applied, so the host reuses a despawned mover's id only after this batch
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
                    _motion.Enqueue((e.Kind, Copy(e)));
                    break;
            }
        }

        /// <summary>The movers feature switched itself off: releases the held batches so the apply acks move on.</summary>
        public void DropHeld()
        {
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
            int n = 0;
            while (_batches.Count > 0 && n < MaxBatchesPerFrame)
            {
                // w2/ids: budgeted in milliseconds with carry-over; at least one batch per frame, never split
                if (n > 0 && Perf.Elapsed(start) > allowance) break;
                n++;
                (byte[] data, long seq) = _batches.Dequeue();
                try
                {
                    ApplyBatch(em, SyncBatch.Read(data, 0, data.Length));
                }
                catch (DecodeException ex)
                {
                    _log.Warn(LogCategory.Replication, "dropped a malformed mover batch: " + ex.Message);
                }
                catch (Exception ex)
                {
                    Diagnostics.Guard.Report(Diagnostics.Features.Movers, "applying a mover batch", ex);
                }
                finally
                {
                    if (seq != 0) _sessions.Client?.ApplyAcks.Release(seq);
                }
            }
            if (n > 0) _applyBudget.EndFrame(Perf.Stop(Perf.MoverApply, start));
            if (_pendingLinks.Count > 0) RetryLinks(em);
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
            _log.Info(LogCategory.Replication, "movers: " + pinned + " pinned at their snapshot pose, " + orphans + " unbound movers removed");
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
                    Type type = typeof(Owner).Assembly.GetType(name, false);
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

        private void ApplyBatch(EntityManager em, List<SyncRecord> records)
        {
            var touched = new List<(Entity, MoverRecord, bool)>();
            foreach (SyncRecord r in records)
            {
                try
                {
                    switch (r.Kind)
                    {
                        case SyncRecordKind.MoverSpawn:
                        case SyncRecordKind.MoverState:
                        {
                            Entity e = _ids.GetEntity(r.NetId);
                            if (e != Entity.Null && (!em.Exists(e) || em.HasComponent<Deleted>(e)))
                            {
                                _ids.Unbind(r.NetId);
                                e = Entity.Null;
                            }
                            if (e == Entity.Null)
                            {
                                e = Create(em, r.NetId, r.Mover);
                                if (e == Entity.Null) break;
                                touched.Add((e, r.Mover, true));
                            }
                            else
                            {
                                touched.Add((e, r.Mover, false));
                            }
                            break;
                        }
                        case SyncRecordKind.MoverDespawn:
                        {
                            Entity e = _ids.GetEntity(r.NetId);
                            _ids.Unbind(r.NetId);
                            _force.Remove(r.NetId);
                            _pendingLinks.Remove(r.NetId); // w3/agents
                            if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Deleted>(e)) break;
                            em.AddComponent<Deleted>(e);
                            _despawned++;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Debug(LogCategory.Replication, "mover record #" + r.NetId + " failed: " + ex.Message);
                }
            }
            // states and references after every entity of the batch exists (layouts, riders)
            HashSet<Entity> created = null;
            foreach ((Entity e, MoverRecord rec, bool isNew) in touched)
            {
                try
                {
                    if (em.Exists(e)) ApplyState(em, e, rec, isNew);
                    if (isNew) (created ?? (created = new HashSet<Entity>())).Add(e);
                }
                catch (Exception ex)
                {
                    _log.Debug(LogCategory.Replication, "mover state failed: " + ex.Message);
                }
            }
            // w3/agents: the agents of the movers (vanilla reference systems pick them up for movers created this frame)
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
                    _log.Debug(LogCategory.Replication, "mover agent links failed: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// w3/agents: writes a mover's agent links (<c>Resident.m_Citizen</c>, <c>Pet.m_HouseholdPet</c>,
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

        private Entity Create(EntityManager em, uint netId, MoverRecord rec)
        {
            Entity prefab = _ids.GetEntity(rec.PrefabNetId);
            if (prefab == Entity.Null || !em.Exists(prefab)) return Entity.Null;
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

        /// <summary>Mirrors the host's discrete state on an existing (or just created) mover.</summary>
        private void ApplyState(EntityManager em, Entity e, MoverRecord rec, bool isNew)
        {
            uint netId = _ids.GetNetId(e);
            Entity prefab = em.HasComponent<PrefabRef>(e) ? em.GetComponentData<PrefabRef>(e).m_Prefab : Entity.Null;
            Archetypes types = prefab != Entity.Null ? TypesOf(em, prefab, rec.TrainController) : null;
            bool structural = false;
            Entity vehicle = rec.Vehicle != 0 ? _ids.GetEntity(rec.Vehicle) : Entity.Null;
            if (vehicle != Entity.Null && (!em.Exists(vehicle) || em.HasComponent<Deleted>(vehicle))) vehicle = Entity.Null; // crashfix: never a deleted vehicle
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
                            m_BoneIndex = new int3(rec.BoneX, rec.BoneY, rec.BoneZ),
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
                if (route != Entity.Null && em.Exists(route))
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

        /// <summary>All four slots hold the same pose with zero velocity (ARCH §4.9.5).</summary>
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
                        _stager.ApplyControl(body, 0, body.Length, _pins);
                        foreach (MotionPin pin in _pins) ApplyPin(em, pin);
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
            // perf pass: most slots were already written for this window (one in sixteen changes per frame); that is decided
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

        /// <summary>View report and motion ack, about 10 Hz (ARCH §4.8.3, §4.9.3).</summary>
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
            AttentionValues.Instance?.FillViewReport(_view); // w2/simvals: selected entity and infoview topics
            AgentSync.Instance?.FillViewReport(_view); // w3/agents: the followed entity (follow camera)
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

        /// <summary>One line for diagnostics.</summary>
        public string Status => _active ? "movers +" + _spawned + " -" + _despawned + ", " + _stager.AssignedCount + " in motion" + (_linksApplied > 0 ? ", " + _linksApplied + " agent links" : "") : "";
    }
}
