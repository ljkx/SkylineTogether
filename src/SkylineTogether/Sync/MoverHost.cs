using System;
using System.Collections.Generic;
using Game.Common;
using Game.Creatures;
using Game.Objects;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Vehicles;
using SkylineTogether.Core;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Motion;
using SkylineTogether.Core.Scheduling;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Diagnostics;
using SkylineTogether.Session;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// Host side of mover replication (ARCHITECTURE.md#movers). Existence is global: every vehicle, trailer, train car and
    /// creature gets a net id (<see cref="Family.Mover"/>) and every spawn, despawn and discrete state change
    /// (moving ↔ parked, entering or leaving a vehicle, <c>Unspawned</c>) is broadcast as a <see cref="MoverRecord"/>.
    /// Motion is interest-managed: each client reports its camera (<c>MessageKind.View</c>); for every simulated tick
    /// the movers whose <c>UpdateFrame</c> matches the tick are sampled (exactly how the movers slice their work,
    /// <c>CarMoveSystem.cs:420-423</c>) and those inside a client's region stream their <c>TransformFrame</c> slot through
    /// <see cref="MotionHostEncoder"/> within a byte budget. Movers leaving the region are released with a pin.
    /// Out-of-interest movers get a rare pose refresh, so they stay roughly in place on every client. Movers seen from far
    /// away have a wider reach (<see cref="MoverReach"/>): aircraft stream everywhere on the map, ships and mainline trains in
    /// a region three times as wide; outside the client's own region their frames are far-field, every second sample.
    /// Every movers batch ends with a <see cref="MoverRoster"/> window when one is due (<see cref="MoverRosterSchedule"/>: a
    /// pass over all ids about every half minute, a fast pass when a player starts its motion stream), so clients remove
    /// movers the host no longer has and ask for the ones they lack (<see cref="QueueResend"/>); a mover whose entity
    /// vanished here without a <c>Deleted</c> tag is despawned when its window comes up.
    /// Runs right before <c>SaveGameSystem</c>, so a join snapshot taken this frame already carries the new ids.
    /// Main thread only.
    /// </summary>
    public sealed class MoverHost
    {
        /// <summary>Motion bytes per second and client at speed 1 (doubled at speed 2 and above).</summary>
        public const int MotionBytesPerSecond = 72_000;
        private const int MaxInterest = 2500;
        private const float MinRadius = 200f, MaxRadius = 1000f, RadiusPerZoom = 1.0f, LeaveFactor = 1.25f;
        private const int RefreshScanPerFrame = 96;
        private const int RefreshSendPerFrame = 4;
        private const float RefreshDistance = 32f;
        private const int MaxTicksPerFrame = 16;
        private const uint StaleInterestTicks = 48;

        private sealed class HostMover
        {
            public MoverKind Kind;
            public bool Unspawned;
            public uint Vehicle;
            public uint Route;
            public float3 Sent;
            /// <summary>The agent links last sent (citizen or pet, keeper, owner).</summary>
            public MoverAgentLinks Links;
        }

        private sealed class PeerView
        {
            public PeerId Peer;
            public readonly ViewReport Report = new ViewReport();
            public bool HasReport;
            public MotionInterestRegion Region;
            /// <summary>The regions of the wider reach classes (<see cref="MotionReach.RegionFor"/>), kept with <see cref="Region"/>.</summary>
            public MotionInterestRegion WideRegion, GlobalRegion;
            public float Scale = 1f;
            public readonly Dictionary<uint, uint> Interest = new Dictionary<uint, uint>();
            /// <summary>The keys of <see cref="Interest"/> as an array-backed set (the sampling loop tests it per mover).</summary>
            public readonly IdIndexSet Members = new IdIndexSet();
            public bool Congested;
            public long MotionBytes;

            public void Enter(uint id, uint tick)
            {
                Interest[id] = tick;
                Members.Add(id);
            }

            public bool Leave(uint id)
            {
                Members.Remove(id);
                return Interest.Remove(id);
            }
        }

        private readonly SessionManager _sessions;
        private readonly ILog _log;
        private readonly Dictionary<uint, HostMover> _movers = new Dictionary<uint, HostMover>();
        private readonly Dictionary<byte, PeerView> _peers = new Dictionary<byte, PeerView>();
        // the views sampled this frame, and the sampling time budget per frame (ms)
        private readonly List<PeerView> _views = new List<PeerView>(8);
        private const double SampleBudgetMs = 2.0;
        private long _sampleCarryOvers;
        private readonly SyncBatch.Writer _batch = new SyncBatch.Writer();
        private readonly StagedReleases _staged = new StagedReleases(); // despawned ids, released after the broadcast
        private Action<uint> _writeDespawn;
        private readonly List<uint> _refresh = new List<uint>();
        private readonly MotionFrame[] _four = new MotionFrame[4];
        private readonly uint[] _fourTicks = new uint[4];
        private readonly List<uint> _scratchIds = new List<uint>();
        private readonly MoverAgentLinks _linksScratch = new MoverAgentLinks();
        private readonly LinkBudgetConfig _linkConfig = new LinkBudgetConfig();
        private MotionHostEncoder _encoder;
        private World _world;
        private NetIdSystem _ids;
        private SimulationSystem _simulation;
        private EntityQuery _all, _created, _deleted, _changed, _framed;
        private bool _bindAll;
        private bool _active;
        private bool _sampleInit;
        private uint _lastSampled;
        private int _refreshCursor;
        private bool _warnedSlots;
        // reach classes (ARCHITECTURE.md#movers): per chunk by component, trains per prefab (track type)
        private readonly Dictionary<Entity, MoverReach> _prefabReach = new Dictionary<Entity, MoverReach>();
        private ComponentTypeHandle<Aircraft> _aircraftType;
        private ComponentTypeHandle<Watercraft> _watercraftType;
        private ComponentTypeHandle<Train> _trainType;
        private ComponentTypeHandle<Unspawned> _unspawnedType;
        private ComponentTypeHandle<PrefabRef> _prefabRefType;
        private long _wideFrames, _farSkipped;
        // the roster (ARCHITECTURE.md#movers): windows of live movers, and movers asked for again by a client
        private readonly MoverRosterSchedule _roster = new MoverRosterSchedule();
        private readonly List<uint> _rosterIds = new List<uint>();
        private readonly List<uint> _rosterRuns = new List<uint>();
        private readonly List<uint> _vanishedIds = new List<uint>();
        private readonly List<(byte Peer, uint Id)> _resendQueue = new List<(byte, uint)>();
        private readonly SyncBatch.Writer _resendBatch = new SyncBatch.Writer();
        private const int MaxQueuedResends = 4096;
        private long _rosterWindows, _vanished, _resent, _lastPasses;
        private long _passMovers, _passVanished;
        private bool _burstPass;

        public MoverHost(SessionManager sessions, ILog log)
        {
            _sessions = sessions;
            _log = log;
        }

        public int MoverCount => _movers.Count;

        /// <summary>Frames whose view sampling ran out of its time budget and continued next frame (diagnostics).</summary>
        public long SampleCarryOvers => _sampleCarryOvers;

        public void OnHostStarted(SessionHost host)
        {
            _encoder = new MotionHostEncoder(_log);
            _staged.Clear();
            _movers.Clear();
            _peers.Clear();
            _prefabReach.Clear();
            _wideFrames = _farSkipped = 0;
            _roster.Reset();
            _resendQueue.Clear();
            _rosterWindows = _vanished = _resent = _lastPasses = _passMovers = _passVanished = 0;
            _burstPass = false;
            _bindAll = true;
            _sampleInit = false;
            _active = true;
            host.AppMessage += OnMessage;
            host.PeerLeft += peer =>
            {
                _peers.Remove(peer.Value);
                _encoder?.RemovePeer(peer);
            };
        }

        public void OnSessionEnded()
        {
            _active = false;
            _staged.Clear();
            _movers.Clear();
            _peers.Clear();
            _refresh.Clear();
            _resendQueue.Clear();
            _encoder = null;
        }

        private void OnMessage(PeerId peer, Envelope e)
        {
            try
            {
                if (e.Kind == MessageKind.View)
                {
                    if (!_peers.TryGetValue(peer.Value, out PeerView view))
                    {
                        // a view report that arrives after its peer left must not bring its stream back
                        if (_sessions.Host?.GetPeerOutput(peer) == null) return;
                        view = new PeerView { Peer = peer };
                        _peers[peer.Value] = view;
                        _encoder?.AddPeer(peer);
                        // a fast roster pass: movers its snapshot holds but the host no longer has go at once
                        _roster.Burst();
                        _burstPass = true;
                        _log.Info(LogCategory.Motion, peer + ": motion stream started; a full mover roster pass follows");
                    }
                    ViewReport.ReadInto(new ByteReader(e.Body, e.BodyOffset, e.BodyCount), view.Report);
                    view.HasReport = true;
                    UpdateRegion(view);
                }
                else if (e.Kind == MessageKind.Ack && MotionClientStager.TryReadAck(e.Body, e.BodyOffset, e.BodyCount, out uint seq))
                {
                    _encoder?.OnAck(peer, seq);
                }
            }
            catch (DecodeException ex)
            {
                _log.Debug(LogCategory.Motion, peer + ": malformed " + e.Kind + " ignored: " + ex.Message);
            }
        }

        private static void UpdateRegion(PeerView v)
        {
            float zoom = v.Report.Zoom > 0f ? v.Report.Zoom : MinRadius;
            float radius = Math.Max(MinRadius, Math.Min(MaxRadius, zoom * RadiusPerZoom) * v.Scale);
            v.Region = new MotionInterestRegion { CenterX = v.Report.PivotX, CenterZ = v.Report.PivotZ, Radius = radius, LeaveRadius = radius * LeaveFactor };
            v.WideRegion = MotionReach.RegionFor(v.Region, MoverReach.Wide);
            v.GlobalRegion = MotionReach.RegionFor(v.Region, MoverReach.Global);
        }

        private bool EnsureWorld()
        {
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return false;
            if (_world == world) return true;
            _world = world;
            _prefabReach.Clear();
            _ids = world.GetOrCreateSystemManaged<NetIdSystem>();
            _simulation = world.GetOrCreateSystemManaged<SimulationSystem>();
            EntityManager em = world.EntityManager;
            ComponentType temp = ComponentType.ReadOnly<Game.Tools.Temp>();
            ComponentType deleted = ComponentType.ReadOnly<Deleted>();
            ComponentType created = ComponentType.ReadOnly<Created>();
            ComponentType vehicle = ComponentType.ReadOnly<Vehicle>();
            ComponentType creature = ComponentType.ReadOnly<Creature>();
            _all = em.CreateEntityQuery(new EntityQueryDesc { Any = new[] { vehicle, creature }, None = new[] { temp, deleted } });
            _created = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { created }, Any = new[] { vehicle, creature }, None = new[] { temp, deleted } });
            _deleted = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { deleted }, Any = new[] { vehicle, creature }, None = new[] { temp } });
            ComponentType[] tags = { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<BatchesUpdated>() };
            _changed = em.CreateEntityQuery(
                new EntityQueryDesc { All = new[] { vehicle }, Any = tags, None = new[] { temp, deleted, created } },
                new EntityQueryDesc { All = new[] { creature }, Any = tags, None = new[] { temp, deleted, created } });
            _framed = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<TransformFrame>(), ComponentType.ReadOnly<UpdateFrame>(), ComponentType.ReadOnly<Transform>() },
                None = new[] { temp, deleted },
            });
            return true;
        }

        // ================================================================== frame

        /// <summary>Right before <c>SaveGameSystem</c>: existence records, then motion.</summary>
        public void Update()
        {
            if (!_active || _sessions.Host == null || !EnsureWorld()) return;
            EntityManager em = _world.EntityManager;
            if (_bindAll)
            {
                _bindAll = false;
                BindAll(em);
            }
            long start = Perf.Start();
            _batch.Reset();
            // despawns of a batch that was never sent (the capture threw): their ids are still allocated (staged), and
            // their despawns go out again first (ARCHITECTURE.md#net-ids)
            if (_staged.Count > 0) _staged.Rewrite(_writeDespawn ?? (_writeDespawn = id => _batch.MoverDespawn(id)));
            CaptureDeleted(em);
            CaptureCreated(em);
            CaptureChanged(em);
            Refresh(em);
            if (_peers.Count > 0) WriteRoster(em); // last: it describes the movers after every record of this batch
            if (!_batch.IsEmpty)
            {
                ArraySegment<byte> bytes = _batch.Finish();
                _sessions.Host.Broadcast(MessageKind.Epoch, bytes.Array, bytes.Offset, bytes.Count);
                _ids.ReleaseStaged(_staged); // quarantined only once their despawns are on the way to every peer
                Perf.Counters.AddBytes(Perf.MoverBytes, (long)bytes.Count * Math.Max(1, _peers.Count), Perf.Now);
            }
            ServeResends(em);
            Perf.Stop(Perf.MoverCapture, start);
            if (_peers.Count == 0) return;
            long motion = Perf.Start();
            Motion(em);
            Perf.Stop(Perf.MotionSample, motion);
        }

        private void BindAll(EntityManager em)
        {
            NativeArray<Entity> entities = _all.ToEntityArray(Allocator.Temp);
            int bound = 0;
            try
            {
                foreach (Entity e in entities)
                {
                    try
                    {
                        uint id = _ids.GetNetId(e);
                        if (id == 0)
                        {
                            id = _ids.BindNew(e, Family.Mover);
                            if (id == 0) continue;
                            bound++;
                        }
                        MoverRecord rec = Describe(em, e);
                        if (rec == null) continue;
                        HostMover known = Remember(rec);
                        known.Links = Links(em, e); // the snapshot carries them
                        _movers[id] = known;
                    }
                    catch (Exception ex)
                    {
                        _log.Debug(LogCategory.Replication, "mover bind failed: " + ex.Message);
                    }
                }
            }
            finally
            {
                entities.Dispose();
            }
            _log.Info(LogCategory.Replication, "host session start: " + _movers.Count + " movers tracked (" + bound + " newly bound)");
        }

        private static HostMover Remember(MoverRecord rec)
        {
            return new HostMover { Kind = rec.Kind, Unspawned = rec.Unspawned, Vehicle = rec.Vehicle, Route = rec.Route, Sent = new float3(rec.X, rec.Y, rec.Z) };
        }

        private void CaptureDeleted(EntityManager em)
        {
            if (_deleted.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> entities = _deleted.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in entities)
                {
                    uint id = _ids.GetNetId(e);
                    if (id == 0 || _ids.GetFamily(id) != Family.Mover) continue;
                    _batch.MoverDespawn(id);
                    _ids.Unbind(id, _staged); // quarantined after the broadcast, until every peer applied this batch
                    _movers.Remove(id);
                    if (id > NetSlot.MaxValue) continue;
                    foreach (PeerView v in _peers.Values)
                    {
                        if (v.Leave(id)) _encoder.ExitInterest(v.Peer, new NetSlot(id));
                    }
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void CaptureCreated(EntityManager em)
        {
            if (_created.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> entities = _created.ToEntityArray(Allocator.Temp);
            try
            {
                // bind first, so layouts and riders inside the same batch reference each other by id
                foreach (Entity e in entities)
                {
                    if (_ids.GetNetId(e) == 0) _ids.BindNew(e, Family.Mover);
                }
                foreach (Entity e in entities)
                {
                    try
                    {
                        uint id = _ids.GetNetId(e);
                        MoverRecord rec = Describe(em, e);
                        if (id == 0 || rec == null) continue;
                        ForgetInterest(id); // a reused id never inherits an old mover's interest
                        _batch.Mover(SyncRecordKind.MoverSpawn, id, rec, _ids.GetGeneration(id));
                        HostMover created = Remember(rec);
                        _movers[id] = created;
                        WriteLinks(em, e, id, created, true); // right behind the spawn, same batch
                    }
                    catch (Exception ex)
                    {
                        _log.Debug(LogCategory.Replication, "mover spawn capture failed: " + ex.Message);
                    }
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void CaptureChanged(EntityManager em)
        {
            if (_changed.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> entities = _changed.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in entities)
                {
                    try
                    {
                        uint id = _ids.GetNetId(e);
                        if (id == 0)
                        {
                            // a mover that escaped binding (should not happen): treat it as new
                            id = _ids.BindNew(e, Family.Mover);
                            if (id == 0) continue;
                        }
                        bool knownMover = _movers.TryGetValue(id, out HostMover known);
                        if (knownMover && SameState(em, e, known))
                        {
                            WriteLinks(em, e, id, known, false);
                            continue;
                        }
                        MoverRecord rec = Describe(em, e);
                        if (rec == null) continue;
                        _batch.Mover(known == null ? SyncRecordKind.MoverSpawn : SyncRecordKind.MoverState, id, rec, _ids.GetGeneration(id));
                        HostMover now = Remember(rec);
                        now.Links = known?.Links;
                        _movers[id] = now;
                        WriteLinks(em, e, id, now, known == null);
                    }
                    catch (Exception ex)
                    {
                        _log.Debug(LogCategory.Replication, "mover state capture failed: " + ex.Message);
                    }
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void ForgetInterest(uint id)
        {
            if (_encoder == null || id > NetSlot.MaxValue) return;
            foreach (PeerView v in _peers.Values)
            {
                if (v.Members.Contains(id) && v.Leave(id)) _encoder.ExitInterest(v.Peer, new NetSlot(id));
            }
        }

        /// <summary>Cheap check of the replicated discrete state against the last record (most tags change nothing of it).</summary>
        private bool SameState(EntityManager em, Entity e, HostMover known)
        {
            MoverKind kind;
            uint vehicle = 0;
            if (em.HasComponent<CurrentVehicle>(e))
            {
                kind = em.HasComponent<Relative>(e) ? MoverKind.Riding : MoverKind.Inside;
                vehicle = _ids.GetNetId(em.GetComponentData<CurrentVehicle>(e).m_Vehicle);
            }
            else
            {
                kind = em.HasComponent<Stopped>(e) || !em.HasComponent<Moving>(e) ? MoverKind.Stopped : MoverKind.Moving;
            }
            uint route = em.HasComponent<CurrentRoute>(e) ? _ids.GetNetId(em.GetComponentData<CurrentRoute>(e).m_Route) : 0;
            return kind == known.Kind && vehicle == known.Vehicle && route == known.Route && em.HasComponent<Unspawned>(e) == known.Unspawned;
        }

        /// <summary>Rolling pose refresh (ARCHITECTURE.md#movers): a few movers per frame that moved far since their last record.</summary>
        private void Refresh(EntityManager em)
        {
            if (_refreshCursor >= _refresh.Count)
            {
                _refreshCursor = 0;
                _refresh.Clear();
                _refresh.AddRange(_movers.Keys);
            }
            int sent = 0;
            int end = Math.Min(_refresh.Count, _refreshCursor + RefreshScanPerFrame);
            for (; _refreshCursor < end && sent < RefreshSendPerFrame; _refreshCursor++)
            {
                uint id = _refresh[_refreshCursor];
                if (!_movers.TryGetValue(id, out HostMover known)) continue;
                Entity e = _ids.GetEntity(id);
                if (e == Entity.Null || !em.Exists(e) || !em.HasComponent<Transform>(e)) continue;
                WriteLinks(em, e, id, known, false); // a keeper or owner changed without a tag
                float3 p = em.GetComponentData<Transform>(e).m_Position;
                if (math.distancesq(p, known.Sent) < RefreshDistance * RefreshDistance) continue;
                try
                {
                    MoverRecord rec = Describe(em, e);
                    if (rec == null) continue;
                    _batch.Mover(SyncRecordKind.MoverState, id, rec, _ids.GetGeneration(id));
                    HostMover now = Remember(rec);
                    now.Links = known.Links;
                    _movers[id] = now;
                    sent++;
                }
                catch (Exception ex)
                {
                    _log.Debug(LogCategory.Replication, "mover refresh failed: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// The next roster window when one is due (ARCHITECTURE.md#movers): every mover of the window this host tracks
        /// whose entity is alive. A tracked mover whose entity is gone (destroyed without a <c>Deleted</c> tag, so no
        /// despawn was ever captured) is despawned here first.
        /// </summary>
        private void WriteRoster(EntityManager em)
        {
            bool burst = _roster.BurstRemaining > 0;
            if (!_roster.TryNext(_sessions.Clock.Seconds, _ids.Ids.Next, out uint first, out uint span)) return;
            _rosterIds.Clear();
            _vanishedIds.Clear();
            ulong end = (ulong)first + span;
            for (ulong i = first; i < end; i++)
            {
                uint id = (uint)i;
                if (_ids.GetFamily(id) != Family.Mover || !_movers.ContainsKey(id)) continue;
                Entity e = _ids.GetEntity(id);
                if (e != Entity.Null && em.Exists(e) && !em.HasComponent<Deleted>(e)) _rosterIds.Add(id);
                else _vanishedIds.Add(id);
            }
            foreach (uint id in _vanishedIds)
            {
                _batch.MoverDespawn(id);
                _ids.Unbind(id, _staged); // quarantined after the broadcast, until every peer applied this batch
                _movers.Remove(id);
                ForgetInterest(id);
            }
            MoverRoster.BuildRuns(_rosterIds, _rosterRuns);
            _batch.MoverRoster(first, span, _rosterRuns);
            _rosterWindows++;
            _vanished += _vanishedIds.Count;
            _passMovers += _rosterIds.Count;
            _passVanished += _vanishedIds.Count;
            _burstPass |= burst;
            if (_roster.Passes == _lastPasses) return;
            _lastPasses = _roster.Passes;
            if (_burstPass || _passVanished > 0)
            {
                _log.Info(LogCategory.Replication, "mover roster: pass " + _lastPasses + " sent (" + _passMovers + " movers" + (_burstPass ? ", fast pass for a joining player" : "")
                                                   + (_passVanished > 0 ? "; " + _passVanished + " movers that vanished here without a despawn were despawned" : "") + ")");
            }
            _passMovers = _passVanished = 0;
            _burstPass = _roster.BurstRemaining > 0;
        }

        /// <summary>
        /// A client lacks this mover (its roster check found it missing twice): its spawn and agent links are sent to that
        /// client at the end of the next capture. Called from the integrity request path (<c>ResendEntities</c>).
        /// </summary>
        public void QueueResend(PeerId peer, uint id)
        {
            if (!_active || _resendQueue.Count >= MaxQueuedResends) return;
            _resendQueue.Add((peer.Value, id));
        }

        private void ServeResends(EntityManager em)
        {
            if (_resendQueue.Count == 0) return;
            SessionHost host = _sessions.Host;
            _resendQueue.Sort((a, b) => a.Peer != b.Peer ? a.Peer.CompareTo(b.Peer) : a.Id.CompareTo(b.Id));
            int i = 0;
            while (i < _resendQueue.Count)
            {
                byte peer = _resendQueue[i].Peer;
                _resendBatch.Reset();
                uint previous = 0;
                for (; i < _resendQueue.Count && _resendQueue[i].Peer == peer; i++)
                {
                    uint id = _resendQueue[i].Id;
                    if (id == previous) continue;
                    previous = id;
                    try
                    {
                        if (!_movers.ContainsKey(id)) continue; // gone meanwhile: its despawn or the next roster tells the client
                        Entity e = _ids.GetEntity(id);
                        if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Deleted>(e)) continue;
                        MoverRecord rec = Describe(em, e);
                        if (rec == null) continue;
                        _resendBatch.Mover(SyncRecordKind.MoverSpawn, id, rec, _ids.GetGeneration(id));
                        MoverAgentLinks links = Links(em, e);
                        if (!links.IsEmpty) _resendBatch.MoverAgents(id, links);
                        _resent++;
                    }
                    catch (Exception ex)
                    {
                        _log.Debug(LogCategory.Replication, "mover re-send failed: " + ex.Message);
                    }
                }
                if (_resendBatch.IsEmpty || host == null) continue;
                ArraySegment<byte> bytes = _resendBatch.Finish();
                host.SendTo(new PeerId(peer), MessageKind.Epoch, bytes.Array, bytes.Offset, bytes.Count);
            }
            _resendQueue.Clear();
        }

        /// <summary>
        /// The agents a mover refers to (the citizen of a pedestrian, the household pet of an animal, the keeper
        /// and the owner of a vehicle) as net ids; unbound targets are 0 and are sent once they are bound.
        /// </summary>
        private MoverAgentLinks Links(EntityManager em, Entity e) => Links(em, e, new MoverAgentLinks());

        /// <summary>Fills <paramref name="links"/> (cleared first) with the mover's agent links; returns it.</summary>
        private MoverAgentLinks Links(EntityManager em, Entity e, MoverAgentLinks links)
        {
            links.Agent = links.Keeper = links.Owner = 0;
            if (em.HasComponent<Game.Creatures.Resident>(e)) links.Agent = _ids.GetNetId(em.GetComponentData<Game.Creatures.Resident>(e).m_Citizen);
            else if (em.HasComponent<Game.Creatures.Pet>(e)) links.Agent = _ids.GetNetId(em.GetComponentData<Game.Creatures.Pet>(e).m_HouseholdPet);
            if (em.HasComponent<Game.Vehicles.PersonalCar>(e)) links.Keeper = _ids.GetNetId(em.GetComponentData<Game.Vehicles.PersonalCar>(e).m_Keeper);
            if (em.HasComponent<Vehicle>(e) && em.HasComponent<Owner>(e)) links.Owner = _ids.GetNetId(em.GetComponentData<Owner>(e).m_Owner);
            return links;
        }

        /// <summary>Writes the mover's agent links when they differ from the last ones sent (always after a spawn).</summary>
        private void WriteLinks(EntityManager em, Entity e, uint id, HostMover known, bool spawn)
        {
            if (known == null) return;
            try
            {
                // read into a scratch instance; a new one is kept only when the links changed (this runs for
                // every tagged mover and the refresh scan's movers each frame)
                MoverAgentLinks links = Links(em, e, _linksScratch);
                if (spawn ? links.IsEmpty : links.SameAs(known.Links)) return;
                if (!spawn && known.Links == null && links.IsEmpty) return;
                links = links.Clone();
                _batch.MoverAgents(id, links);
                known.Links = links;
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Replication, "mover agent links failed: " + ex.Message);
            }
        }

        /// <summary>The replicated description of a mover; null when its prefab has no net id.</summary>
        private MoverRecord Describe(EntityManager em, Entity e)
        {
            if (!em.HasComponent<PrefabRef>(e)) return null;
            Entity prefab = em.GetComponentData<PrefabRef>(e).m_Prefab;
            uint prefabId = _ids.GetNetId(prefab);
            if (prefabId == 0) return null;
            var rec = new MoverRecord { PrefabNetId = prefabId };
            if (em.HasComponent<Transform>(e))
            {
                Transform t = em.GetComponentData<Transform>(e);
                rec.X = t.m_Position.x;
                rec.Y = t.m_Position.y;
                rec.Z = t.m_Position.z;
                rec.QX = t.m_Rotation.value.x;
                rec.QY = t.m_Rotation.value.y;
                rec.QZ = t.m_Rotation.value.z;
                rec.QW = t.m_Rotation.value.w;
            }
            if (em.HasComponent<UpdateFrame>(e)) rec.UpdateFrame = (byte)(em.GetSharedComponent<UpdateFrame>(e).m_Index & 15);
            if (em.HasComponent<PseudoRandomSeed>(e)) rec.Seed = em.GetComponentData<PseudoRandomSeed>(e).m_Seed;
            rec.Unspawned = em.HasComponent<Unspawned>(e);
            if (em.HasComponent<CurrentVehicle>(e))
            {
                CurrentVehicle cv = em.GetComponentData<CurrentVehicle>(e);
                rec.Vehicle = _ids.GetNetId(cv.m_Vehicle);
                rec.VehicleFlags = (uint)cv.m_Flags;
                rec.Kind = em.HasComponent<Relative>(e) ? MoverKind.Riding : MoverKind.Inside;
                if (rec.Kind == MoverKind.Riding)
                {
                    Relative r = em.GetComponentData<Relative>(e);
                    rec.RX = r.m_Position.x;
                    rec.RY = r.m_Position.y;
                    rec.RZ = r.m_Position.z;
                    rec.RQX = r.m_Rotation.value.x;
                    rec.RQY = r.m_Rotation.value.y;
                    rec.RQZ = r.m_Rotation.value.z;
                    rec.RQW = r.m_Rotation.value.w;
                    rec.BoneX = r.m_BoneIndex.x;
                    rec.BoneY = r.m_BoneIndex.y;
                    rec.BoneZ = r.m_BoneIndex.z;
                }
            }
            else
            {
                rec.Kind = em.HasComponent<Stopped>(e) || !em.HasComponent<Moving>(e) ? MoverKind.Stopped : MoverKind.Moving;
            }
            if (em.HasComponent<Controller>(e))
            {
                Entity controller = em.GetComponentData<Controller>(e).m_Controller;
                if (controller != e && controller != Entity.Null) rec.Controller = _ids.GetNetId(controller);
            }
            if (em.HasBuffer<LayoutElement>(e))
            {
                DynamicBuffer<LayoutElement> layout = em.GetBuffer<LayoutElement>(e, true);
                if (layout.Length > 0 && layout.Length <= MoverRecordCodec.MaxLayout)
                {
                    rec.Layout = new uint[layout.Length];
                    for (int i = 0; i < layout.Length; i++) rec.Layout[i] = _ids.GetNetId(layout[i].m_Vehicle);
                }
                rec.TrainController = em.HasComponent<TrainObjectData>(prefab);
            }
            if (em.HasComponent<CurrentRoute>(e)) rec.Route = _ids.GetNetId(em.GetComponentData<CurrentRoute>(e).m_Route);
            if (em.HasComponent<Human>(e))
            {
                rec.HasHumanFlags = true;
                rec.HumanFlags = (uint)em.GetComponentData<Human>(e).m_Flags;
            }
            return rec;
        }

        // ================================================================== motion

        private void Motion(EntityManager em)
        {
            uint frame = _simulation.frameIndex;
            if (!_sampleInit)
            {
                _sampleInit = true;
                _lastSampled = frame;
            }
            int ticks = TickDiff(frame, _lastSampled);
            if (ticks < 0 || ticks > 1024)
            {
                _lastSampled = frame;
                ticks = 0;
            }
            if (ticks > MaxTicksPerFrame) _lastSampled = unchecked(frame - MaxTicksPerFrame);
            if (ticks > 0)
            {
                _framed.CompleteDependency();
                EntityTypeHandle entityType = em.GetEntityTypeHandle();
                ComponentTypeHandle<Transform> transformType = em.GetComponentTypeHandle<Transform>(true);
                BufferTypeHandle<TransformFrame> frameType = em.GetBufferTypeHandle<TransformFrame>(true);
                _aircraftType = em.GetComponentTypeHandle<Aircraft>(true);
                _watercraftType = em.GetComponentTypeHandle<Watercraft>(true);
                _trainType = em.GetComponentTypeHandle<Train>(true);
                _unspawnedType = em.GetComponentTypeHandle<Unspawned>(true);
                _prefabRefType = em.GetComponentTypeHandle<PrefabRef>(true);
                // views with a report, once per frame (not a dictionary walk per mover), and a time budget with
                // carry-over: a tick not sampled this frame is sampled next frame (the frame ring keeps 4 x 16 ticks)
                _views.Clear();
                foreach (PeerView v in _peers.Values)
                {
                    if (v.HasReport) _views.Add(v);
                }
                long sampleStart = System.Diagnostics.Stopwatch.GetTimestamp();
                double sampleBudgetMs = Perf.Allowance(Perf.BudgetMotion, SampleBudgetMs); // shared budget
                while (_lastSampled != frame)
                {
                    if (_views.Count == 0)
                    {
                        _lastSampled = frame; // nobody watches: nothing to sample
                        break;
                    }
                    if ((System.Diagnostics.Stopwatch.GetTimestamp() - sampleStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency > sampleBudgetMs)
                    {
                        _sampleCarryOvers++;
                        break;
                    }
                    _lastSampled = unchecked(_lastSampled + 1);
                    try
                    {
                        SampleTick(em, _lastSampled, entityType, ref transformType, ref frameType);
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Guard.Report(Diagnostics.Features.Movers, "motion sampling of tick " + _lastSampled, ex);
                    }
                }
                Perf.Spent(Perf.BudgetMotion, sampleStart);
                _framed.ResetFilter();
                PruneInterest(frame);
            }
            Flush();
        }

        private void SampleTick(EntityManager em, uint tick, EntityTypeHandle entityType, ref ComponentTypeHandle<Transform> transformType, ref BufferTypeHandle<TransformFrame> frameType)
        {
            _framed.SetSharedComponentFilter(new UpdateFrame(tick & 15));
            NativeArray<ArchetypeChunk> chunks = _framed.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                int slot = (int)((tick >> 4) & 3);
                foreach (ArchetypeChunk chunk in chunks)
                {
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                    NativeArray<Transform> transforms = chunk.GetNativeArray(ref transformType);
                    BufferAccessor<TransformFrame> frames = chunk.GetBufferAccessor(ref frameType);
                    // hidden movers (off the map on an outside connection) never need more than the normal region
                    bool hidden = chunk.Has(ref _unspawnedType);
                    MoverReach chunkReach = hidden ? MoverReach.Normal
                        : chunk.Has(ref _aircraftType) ? MoverReach.Global
                        : chunk.Has(ref _watercraftType) ? MoverReach.Wide : MoverReach.Normal;
                    bool trains = !hidden && chunkReach == MoverReach.Normal && chunk.Has(ref _trainType) && chunk.Has(ref _prefabRefType);
                    NativeArray<PrefabRef> prefabs = trains ? chunk.GetNativeArray(ref _prefabRefType) : default;
                    for (int i = 0; i < entities.Length; i++)
                    {
                        uint id = _ids.GetNetId(entities[i]);
                        if (id == 0) continue;
                        if (id > NetSlot.MaxValue)
                        {
                            if (!_warnedSlots) _log.Warn(LogCategory.Motion, "net ids above 24 bits: newer movers no longer stream motion");
                            _warnedSlots = true;
                            continue;
                        }
                        float3 p = transforms[i].m_Position;
                        MoverReach reach = trains ? TrainReach(em, prefabs[i].m_Prefab) : chunkReach;
                        for (int vi = 0; vi < _views.Count; vi++)
                        {
                            PeerView v = _views[vi];
                            bool inside = v.Members.Contains(id);
                            MotionInterestRegion region = reach == MoverReach.Global ? v.GlobalRegion : reach == MoverReach.Wide ? v.WideRegion : v.Region;
                            if (!region.Contains(p.x, p.z, inside))
                            {
                                if (inside)
                                {
                                    v.Leave(id);
                                    _encoder.ExitInterest(v.Peer, new NetSlot(id));
                                }
                                continue;
                            }
                            DynamicBuffer<TransformFrame> ring = frames[i];
                            if (ring.Length < 4) continue;
                            if (!inside)
                            {
                                for (int k = 0; k < 4; k++)
                                {
                                    uint t = unchecked(tick - (uint)(16 * k));
                                    _fourTicks[k] = t;
                                    _four[k] = ToMotion(ring[(int)((t >> 4) & 3)]);
                                }
                                // a refused entry (a baseline frame that is not finite) is not streamed; it is tried again
                                if (!_encoder.EnterInterest(v.Peer, new NetSlot(id), (byte)(tick & 15), _four, _fourTicks)) continue;
                            }
                            else
                            {
                                MotionField field = MotionReach.FieldOf(v.Region, p.x, p.z);
                                if (field == MotionField.Far)
                                {
                                    // kept by its wider reach: far-field priority and every second sample (the client
                                    // interpolates between them)
                                    if (!MotionReach.SendsFarSample(tick, id))
                                    {
                                        _farSkipped++;
                                        v.Enter(id, tick);
                                        continue;
                                    }
                                    _wideFrames++;
                                }
                                _encoder.AddFrame(v.Peer, tick, new NetSlot(id), ToMotion(ring[slot]), field == MotionField.Near, false);
                            }
                            v.Enter(id, tick);
                        }
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }

        /// <summary>A train's reach by its prefab's track type: mainline trains are wide, trams and subways normal.</summary>
        private MoverReach TrainReach(EntityManager em, Entity prefab)
        {
            if (_prefabReach.TryGetValue(prefab, out MoverReach reach)) return reach;
            reach = MoverReach.Normal;
            if (prefab != Entity.Null && em.Exists(prefab) && em.HasComponent<TrainData>(prefab)
                && (em.GetComponentData<TrainData>(prefab).m_TrackType & Game.Net.TrackTypes.Train) != 0)
            {
                reach = MoverReach.Wide;
            }
            _prefabReach[prefab] = reach;
            return reach;
        }

        /// <summary>Diagnostics dump lines.</summary>
        public void Describe(List<string> lines)
        {
            if (!_active) return;
            lines.Add("movers: " + _movers.Count + " tracked, " + _peers.Count + " motion streams; " + _wideFrames + " far-field frames of aircraft, ships and trains ("
                      + _farSkipped + " samples thinned), " + _sampleCarryOvers + " sampling carry-overs");
            lines.Add("mover roster: " + _rosterWindows + " windows in " + _roster.Passes + " passes, " + _vanished + " vanished movers despawned, "
                      + _resent + " movers re-sent to clients that lacked them");
        }

        private static int TickDiff(uint a, uint b) => unchecked((int)(a - b));

        private static MotionFrame ToMotion(TransformFrame f)
        {
            return new MotionFrame
            {
                X = f.m_Position.x, Y = f.m_Position.y, Z = f.m_Position.z,
                VX = f.m_Velocity.x, VY = f.m_Velocity.y, VZ = f.m_Velocity.z,
                QX = f.m_Rotation.value.x, QY = f.m_Rotation.value.y, QZ = f.m_Rotation.value.z, QW = f.m_Rotation.value.w,
                Flags = (uint)f.m_Flags,
                State = (byte)f.m_State,
                Activity = f.m_Activity,
                StateTimer = f.m_StateTimer,
            };
        }

        /// <summary>Movers not sampled for three cycles (despawned, parked, boarded) leave every interest; regions adapt to load.</summary>
        private void PruneInterest(uint frame)
        {
            foreach (PeerView v in _peers.Values)
            {
                _scratchIds.Clear();
                foreach (KeyValuePair<uint, uint> pair in v.Interest)
                {
                    if (TickDiff(frame, pair.Value) > StaleInterestTicks) _scratchIds.Add(pair.Key);
                }
                foreach (uint id in _scratchIds)
                {
                    v.Leave(id);
                    _encoder.ExitInterest(v.Peer, new NetSlot(id));
                }
                float scale = v.Scale;
                if (v.Interest.Count > MaxInterest) scale = Math.Max(0.25f, scale * 0.97f);
                else if (v.Interest.Count < MaxInterest * 3 / 5) scale = Math.Min(1f, scale * 1.01f);
                if (scale != v.Scale && v.HasReport)
                {
                    v.Scale = scale;
                    UpdateRegion(v);
                }
            }
        }

        private void Flush()
        {
            SessionHost host = _sessions.Host;
            float speed = Math.Max(1f, _simulation.selectedSpeed);
            int perSecond = (int)(MotionBytesPerSecond * Math.Min(2f, speed));
            int budget = Math.Max(256, (int)(perSecond * Math.Min(0.25f, UnityEngine.Time.unscaledDeltaTime)));
            _scratchIds.Clear();
            foreach (PeerView v in _peers.Values)
            {
                IPeerOutput output = host.GetPeerOutput(v.Peer);
                if (output == null)
                {
                    _scratchIds.Add(v.Peer.Value);
                    continue;
                }
                // frames per second at full quality: one per 16 ticks per mover in interest
                double demand = v.Interest.Count * (60.0 * speed / 16.0) * 12.0;
                MotionQuality quality = demand <= perSecond ? MotionQuality.Full
                    : demand <= 2.0 * perSecond ? MotionQuality.FarHalf
                    : demand <= 4.0 * perSecond ? MotionQuality.FarQuarter : MotionQuality.Reduced;
                // structure and values come first (ARCHITECTURE.md#deferred-values-and-link-pacing); while the peer's reliable backlog is high, motion
                // keeps only a trickle (control messages, a few frames) and the client extrapolates, then pins
                int peerBudget = budget;
                v.Congested = host.TryGetLink(v.Peer, out PeerSendState link)
                              && LinkBudget.IsCongested(link.QueuedReliableBytes, link.PendingReliableBytes, link.SendRateBytesPerSecond, _linkConfig);
                if (v.Congested)
                {
                    peerBudget = 256;
                    quality = MotionQuality.Reduced;
                    Perf.Counters.Count("motion frames yielded to a congested link");
                }
                _encoder.SetMotionBudget(v.Peer, peerBudget, quality);
                MotionEncoderStats before = _encoder.GetStats(v.Peer);
                _encoder.Flush(v.Peer, output);
                MotionEncoderStats after = _encoder.GetStats(v.Peer);
                long sent = after.DatagramBytes - before.DatagramBytes + after.ControlBytes - before.ControlBytes;
                if (sent > 0)
                {
                    v.MotionBytes += sent;
                    Perf.Counters.AddBytes(Perf.MotionBytes, sent, Perf.Now);
                }
            }
            foreach (uint gone in _scratchIds)
            {
                _peers.Remove((byte)gone);
                _encoder.RemovePeer(new PeerId((byte)gone));
            }
        }
    }
}
