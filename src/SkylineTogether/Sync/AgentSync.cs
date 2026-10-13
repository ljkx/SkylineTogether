using System;
using System.Collections.Generic;
using System.Diagnostics;
using Game.Buildings;
using Game.Citizens;
using Game.Companies;
using Game.Creatures;
using Game.Rendering;
using Game.UI.InGame;
using Game.Vehicles;
using SkylineTogether.Core;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Scheduling;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Diagnostics;
using SkylineTogether.Session;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// w3/agents: citizens, households, household pets and companies (ARCH §2.2, §3.2 rule 16, class A; K5).
    /// <para>
    /// <b>Existence</b> is global and travels in the structure stream (<see cref="SyncController"/>): every agent is bound
    /// at host session start and in the join snapshot like every other family; births, arrivals, spawns of companies,
    /// deaths and departures are captured from the game's own tags as spawn and despawn records, and agents are created on
    /// clients from their prefab's <c>ArchetypeData</c> (the archetype the game's spawn systems use). Agents are host-owned:
    /// clients never propose them (<see cref="FamilyClassifier.IsHostReplicated"/>).
    /// </para>
    /// <para>
    /// <b>Key links</b> (household membership, property) are promoted to the structure stream: a change found by the scan
    /// becomes an upsert in the next broadcast, together with the reverse buffers of the old and the new owner (co-capture,
    /// ARCH §4.2.6): households as full records, buildings as their reverse buffers on this channel. The same happens for
    /// every owner of a spawned or despawned agent, so a client never keeps a renter, member, student or employee that is
    /// gone.
    /// </para>
    /// <para>
    /// <b>Values</b> change constantly without tags. A rolling, time-sliced scan (<see cref="RollingScan"/>, about 0.8 ms
    /// per frame while peers are connected) hashes each agent's component set and simulation values; a changed hash marks the
    /// agent in every peer's plan (<see cref="AgentPlanner"/>, latest value wins). Each frame a peer gets, within its byte
    /// budget and never behind a backlogged link: its focus agents first (the closure of its selected and followed entity:
    /// a building's renters, households, members, employees, students, occupants; a pedestrian's citizen and household; a
    /// vehicle's passengers, keeper and owner), then the agents around its camera (households and companies of the
    /// buildings in view and their members), then everything else oldest first. Value changes travel as
    /// <see cref="MessageKind.AgentValues"/> (no tags on clients); component-set changes as per-peer upserts.
    /// </para>
    /// <para>
    /// <b>Clients</b> apply values after the structure batches, ask again for agents whose values referenced something not
    /// known yet (<see cref="MessageKind.AgentRefresh"/>, throttled), refresh the open info panel, report the followed
    /// entity in the view report, remove snapshot agents the host never bound, and keep the load-time re-roll of citizen
    /// prefabs (<c>Game.Serialization.CitizenSystem</c>) off so the snapshot keeps the host's citizens.
    /// </para>
    /// Main thread only; every entry point catches and reports to <see cref="Features.Agents"/>.
    /// </summary>
    public sealed class AgentSync
    {
        private const double ScanBudgetMs = 0.8;
        private const double ScanBudgetAloneMs = 0.25;
        private const int MinScanPerFrame = 16;
        private const int BytesPerSecond = 16_000, BurstBytes = 32_000;
        private const long MaxBacklogBytes = 256 * 1024;
        private const int FocusFloorBytes = 4096;
        private const int MaxMessageBytes = 24 * 1024;
        private const int MaxRecordsPerBatch = 256;
        private const double FocusInterval = 0.5;
        private const double ViewInterval = 1.0;
        private const int MaxFocus = 1024;
        private const int MaxClosure = 2048;
        private const int MaxView = 1500;
        private const int MaxListExpand = 256;
        private const int MaxFocusTouches = 64;
        private const float MinRadius = 120f, MaxRadius = 600f, GridCell = 128f;
        private const int MaxInboxPerFrame = 32;
        private const int MaxHeldMessages = 512;
        private const int MaxOwnerBytes = 48 * 1024;
        private const int MaxOwnerRecordsPerFrame = 256; // crashfix: agent owners written in full per frame at most
        private const double PanelRefreshInterval = 0.5;

        /// <summary>Types the agent sync needs (probed at startup, <see cref="Probes"/>).</summary>
        public static readonly string[] TypeNames =
        {
            "Game.Prefabs.ArchetypeData",
            "Game.Citizens.HouseholdMember",
            "Game.Citizens.HouseholdPet",
            "Game.Citizens.HouseholdCitizen",
            "Game.Citizens.HouseholdAnimal",
            "Game.Citizens.Worker",
            "Game.Citizens.Student",
            "Game.Citizens.CurrentBuilding",
            "Game.Citizens.HomelessHousehold",
            "Game.Buildings.PropertyRenter",
            "Game.Buildings.Renter",
            "Game.Buildings.Student",
            "Game.Buildings.Occupant",
            "Game.Buildings.Patient",
            "Game.Companies.Employee",
            "Game.Creatures.Resident",
            "Game.Creatures.Pet",
            "Game.Vehicles.PersonalCar",
            "Game.Vehicles.Passenger",
        };

        /// <summary>The Deserialize-phase system that re-rolls citizen prefabs on every load (off during client sessions).</summary>
        public const string CitizenRerollSystemName = "Game.Serialization.CitizenSystem";

        /// <summary>Reverse buffers of owners written by the co-capture (ARCH §4.2.6).</summary>
        private static readonly string[] OwnerBufferNames =
        {
            "Game.Buildings.Renter", "Game.Buildings.Student", "Game.Companies.Employee", "Game.Buildings.Occupant",
            "Game.Buildings.Patient", "Game.Citizens.HouseholdCitizen", "Game.Citizens.HouseholdAnimal",
        };

        private struct AgentState
        {
            public Entity Entity;
            public ulong Presence;
            public ulong Value;
            public ulong Key;
            public AgentRefs Refs;
        }

        private sealed class HostPeer
        {
            public PeerId Peer;
            public readonly ViewReport Report = new ViewReport();
            public bool HasReport;
            public uint Selected, Followed;
            public double NextFocus, NextView;
            public float ViewX, ViewZ, ViewRadius;
            public readonly PeerByteBudget Budget = new PeerByteBudget(BytesPerSecond, BurstBytes, MaxBacklogBytes);
            public readonly HashSet<uint> Focus = new HashSet<uint>();
            public readonly HashSet<uint> View = new HashSet<uint>();
        }

        /// <summary>Writes an agent's full record into the frame's broadcast batch (host; the controller owns the batch).</summary>
        public delegate void RecordWriter(Entity e, uint id, SyncRecordKind kind);

        private readonly SessionManager _sessions;
        private readonly ILog _log;
        private EntityCodec _codec;
        private NetIdSystem _ids;
        private SelectedInfoUISystem _selectedInfo;
        private CameraUpdateSystem _camera;

        // host
        private readonly AgentPlanner _planner = new AgentPlanner();
        private readonly RollingScan _scan = new RollingScan();
        private readonly Dictionary<uint, AgentState> _states = new Dictionary<uint, AgentState>();
        private readonly Dictionary<EntityArchetype, ulong> _presenceByArchetype = new Dictionary<EntityArchetype, ulong>();
        private readonly SpatialHashGrid _grid = new SpatialHashGrid(GridCell);
        private readonly Dictionary<byte, HostPeer> _peers = new Dictionary<byte, HostPeer>();
        private readonly Dictionary<byte, long> _joinStamps = new Dictionary<byte, long>();
        private readonly HashSet<uint> _owners = new HashSet<uint>();
        private readonly List<uint> _ownerScratch = new List<uint>();
        private readonly List<uint> _keyChanged = new List<uint>();
        private readonly List<uint> _scanIds = new List<uint>();
        private readonly List<PeerId> _peerScratch = new List<PeerId>();
        private readonly ByteWriter _out = new ByteWriter(32 * 1024);
        private readonly ByteWriter _ownerOut = new ByteWriter(8 * 1024);
        private readonly SyncBatch.Writer _peerBatch = new SyncBatch.Writer();
        private readonly HashSet<uint> _closure = new HashSet<uint>();
        private readonly List<uint> _roots = new List<uint>(2);
        private readonly List<uint> _viewScratch = new List<uint>();
        private readonly Stopwatch _watch = new Stopwatch();
        private List<TypeHandler> _ownerHandlers;
        private double _lastStream = -1;
        private long _scanned, _changes, _structural, _keyUpserts, _ownerRecords, _bytesSent, _requests;

        // client
        private readonly Queue<byte[]> _inbox = new Queue<byte[]>();
        private readonly RefreshThrottle _throttle = new RefreshThrottle();
        private readonly List<uint> _unresolved = new List<uint>();
        private readonly List<uint> _due = new List<uint>();
        private readonly ByteWriter _request = new ByteWriter(256);
        private readonly ApplyResult _result = new ApplyResult();
        private ComponentSystemBase _reroll;
        private bool _rerollWasEnabled, _rerollGated;
        private long _valuesApplied, _messagesApplied, _orphans;
        private double _lastPanelRefresh, _lastSweep;
        private bool _warnedApply;

        public AgentSync(SessionManager sessions, ILog log)
        {
            _sessions = sessions;
            _log = log;
            Instance = this;
            sessions.HostStarted += OnHostStarted;
            sessions.ClientStarted += OnClientStarted;
            sessions.SessionEnded += OnSessionEnded;
        }

        /// <summary>The instance created by <see cref="SyncController"/> (view report hook).</summary>
        public static AgentSync Instance { get; private set; }

        private bool IsHost => _sessions.Host != null;

        private bool IsLiveClient => _sessions.Client != null && _sessions.Client.State == ClientState.Live;

        private static double Now => UnityEngine.Time.realtimeSinceStartupAsDouble;

        /// <summary>One line for the panel ("" when idle).</summary>
        public string Status
        {
            get
            {
                if (_ids == null) return "";
                int bound = _ids.CountOf(Family.Agent);
                if (IsHost && FamilyClassifier.AgentsReplicated)
                {
                    return "agents: " + bound + " (" + _changes + " changes, " + _keyUpserts + " moves, " + (_bytesSent / 1024) + " KiB sent)";
                }
                if (IsLiveClient && bound > 0) return "agents: " + bound + " (" + _valuesApplied + " values applied)";
                return "";
            }
        }

        /// <summary>Diagnostics dump lines.</summary>
        public void Describe(List<string> lines)
        {
            if (_ids == null) return;
            if (IsHost)
            {
                lines.Add("agents: replicated " + FamilyClassifier.AgentsReplicated + ", " + _ids.CountOf(Family.Agent) + " bound, " + _states.Count + " scanned, pass "
                          + _scan.Passes + " (" + _scan.Remaining + " left of " + _scan.PassLength + ", " + _scan.HotCount + " hot), " + _changes + " value changes, "
                          + _structural + " component-set changes, " + _keyUpserts + " key-link upserts, " + _ownerRecords + " owner records, " + _requests + " refresh requests");
                foreach (HostPeer p in _peers.Values)
                {
                    _planner.GetSent(p.Peer, out long agents, out long bytes);
                    lines.Add("agents to " + p.Peer + ": dirty " + _planner.DirtyCount(p.Peer) + " (focus " + _planner.FocusDirtyCount(p.Peer) + "/" + _planner.FocusCount(p.Peer)
                              + ", view " + _planner.ViewDirtyCount(p.Peer) + "/" + _planner.ViewCount(p.Peer) + ", full records " + _planner.StructuralCount(p.Peer)
                              + "), sent " + agents + " (" + (bytes / 1024) + " KiB)");
                }
            }
            else if (_sessions.Client != null)
            {
                lines.Add("agents: " + _ids.CountOf(Family.Agent) + " bound, " + _messagesApplied + " messages, " + _valuesApplied + " values applied, "
                          + _throttle.PendingCount + " refresh requests pending (" + _throttle.Refused + " refused), " + _orphans + " unbound snapshot agents removed"
                          + (_rerollGated ? ", citizen prefab re-roll off" : ""));
            }
        }

        /// <summary>Called by <see cref="SyncController"/> once its codec exists.</summary>
        public void Attach(EntityCodec codec, NetIdSystem ids)
        {
            _codec = codec;
            _ids = ids;
            World world = codec.World;
            _selectedInfo = world.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            _camera = world.GetOrCreateSystemManaged<CameraUpdateSystem>();
            _ownerHandlers = null;
        }

        // ================================================================== lifecycle

        private void OnHostStarted(SessionHost host)
        {
            ResetState();
            host.AppMessage += OnHostMessage;
            host.PeerJoining += peer => _joinStamps[peer.Value] = _planner.CurrentStamp;
            host.PeerLive += OnPeerLive;
            host.PeerLeft += peer =>
            {
                _peers.Remove(peer.Value);
                _joinStamps.Remove(peer.Value);
                _planner.RemovePeer(peer);
            };
        }

        private void OnClientStarted(SessionClient client)
        {
            ResetState();
            GateReroll(true);
            client.AppMessage += e =>
            {
                if (e.Kind != MessageKind.AgentValues) return;
                var copy = new byte[e.BodyCount];
                Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                _inbox.Enqueue(copy);
                Perf.Counters.AddBytes(Perf.ReceivedBytes, e.BodyCount, Perf.Now);
            };
            client.BecameLive += OnClientLive;
        }

        private void OnSessionEnded()
        {
            GateReroll(false);
            ResetState();
        }

        private void ResetState()
        {
            _planner.Clear();
            _scan.Clear();
            _states.Clear();
            _presenceByArchetype.Clear();
            _grid.Clear();
            _peers.Clear();
            _joinStamps.Clear();
            _owners.Clear();
            _keyChanged.Clear();
            _inbox.Clear();
            _throttle.Clear();
            _lastStream = -1;
            _scanned = _changes = _structural = _keyUpserts = _ownerRecords = _bytesSent = _requests = 0;
            _valuesApplied = _messagesApplied = _orphans = 0;
            _warnedApply = false;
        }

        /// <summary>Client: keeps the load-time citizen prefab re-roll off for the whole session (the join load included).</summary>
        private void GateReroll(bool off)
        {
            try
            {
                if (off)
                {
                    if (_rerollGated) return;
                    World world = World.DefaultGameObjectInjectionWorld;
                    Type type = typeof(Game.Common.Owner).Assembly.GetType(CitizenRerollSystemName, false);
                    _reroll = type != null && world != null ? world.GetExistingSystemManaged(type) : null;
                    if (_reroll == null)
                    {
                        _log.Warn(LogCategory.Gate, "agents: " + CitizenRerollSystemName + " not found; citizens may show other looks than on the host");
                        return;
                    }
                    _rerollWasEnabled = SystemSwitch.TurnOff(_reroll);
                    _rerollGated = true;
                }
                else if (_rerollGated)
                {
                    _rerollGated = false;
                    SystemSwitch.Restore(_reroll, _rerollWasEnabled);
                    _reroll = null;
                }
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Gate, "agents: citizen re-roll gate: " + ex.Message);
            }
        }

        private void OnPeerLive(PeerId peer)
        {
            if (!_joinStamps.TryGetValue(peer.Value, out long since)) since = _planner.CurrentStamp;
            _planner.AddPeer(peer, since);
            if (!_peers.TryGetValue(peer.Value, out HostPeer p)) _peers[peer.Value] = p = new HostPeer { Peer = peer };
            p.NextFocus = p.NextView = 0;
            if (FamilyClassifier.AgentsReplicated) _log.Info(LogCategory.Replication, peer + ": agent stream started (" + _planner.DirtyCount(peer) + " agents changed since its snapshot)");
        }

        private void OnHostMessage(PeerId peer, Envelope e)
        {
            if (e.Kind != MessageKind.View && e.Kind != MessageKind.AgentRefresh) return;
            try
            {
                if (!_peers.TryGetValue(peer.Value, out HostPeer p)) _peers[peer.Value] = p = new HostPeer { Peer = peer };
                if (e.Kind == MessageKind.View)
                {
                    ViewReport.ReadInto(new ByteReader(e.Body, e.BodyOffset, e.BodyCount), p.Report);
                    p.HasReport = true;
                    uint selected = p.Report.Selected.Value, followed = p.Report.Followed.Value;
                    if (selected != p.Selected || followed != p.Followed)
                    {
                        p.Selected = selected;
                        p.Followed = followed;
                        p.NextFocus = 0; // a new selection gets its closure at once
                    }
                    return;
                }
                if (!FamilyClassifier.AgentsReplicated) return;
                List<uint> ids = AgentRefreshMessage.Read(e.Body, e.BodyOffset, e.BodyCount);
                foreach (uint id in ids)
                {
                    if (_ids != null && _ids.GetFamily(id) == Family.Agent && _ids.GetEntity(id) != Entity.Null)
                    {
                        _planner.Request(peer, id);
                        _requests++;
                    }
                }
            }
            catch (DecodeException ex)
            {
                _log.Debug(LogCategory.Replication, peer + ": malformed " + e.Kind + " ignored: " + ex.Message);
            }
        }

        // ================================================================== host: existence hooks (structure capture)

        /// <summary>The forward references of an agent (net ids of what its owners' reverse buffers list it in).</summary>
        public AgentRefs RefsOf(EntityManager em, Entity e)
        {
            var r = new AgentRefs();
            if (em.HasComponent<HouseholdMember>(e)) r.Household = _ids.GetNetId(em.GetComponentData<HouseholdMember>(e).m_Household);
            else if (em.HasComponent<HouseholdPet>(e)) r.Household = _ids.GetNetId(em.GetComponentData<HouseholdPet>(e).m_Household);
            if (em.HasComponent<PropertyRenter>(e)) r.Property = _ids.GetNetId(em.GetComponentData<PropertyRenter>(e).m_Property);
            else if (em.HasComponent<HomelessHousehold>(e)) r.Property = _ids.GetNetId(em.GetComponentData<HomelessHousehold>(e).m_TempHome);
            if (em.HasComponent<Game.Citizens.Student>(e)) r.School = _ids.GetNetId(em.GetComponentData<Game.Citizens.Student>(e).m_School);
            if (em.HasComponent<Worker>(e)) r.Workplace = _ids.GetNetId(em.GetComponentData<Worker>(e).m_Workplace);
            if (em.HasComponent<CurrentBuilding>(e)) r.Building = _ids.GetNetId(em.GetComponentData<CurrentBuilding>(e).m_CurrentBuilding);
            return r;
        }

        /// <summary>Host: an agent's spawn record is in this frame's batch; its owners' reverse buffers go with it.</summary>
        public void OnSpawned(uint id, Entity e)
        {
            if (_codec == null || id == 0) return;
            try
            {
                EntityManager em = _codec.EntityManager;
                AgentState s = Observe(em, e);
                AgentRefs.CollectOwners(default, s.Refs, _owners);
                _states[id] = s;
                _planner.MarkSentToAll(id);
                UpdateGrid(em, id, e);
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Agents, "agent spawn bookkeeping", ex);
            }
        }

        /// <summary>Host: an agent is despawned (still readable: <c>Deleted</c> entities live until the end of the frame).</summary>
        public void OnDespawned(uint id, Entity e)
        {
            try
            {
                if (_states.TryGetValue(id, out AgentState old)) AgentRefs.CollectOwners(old.Refs, default, _owners);
                if (_codec != null && e != Entity.Null && _codec.EntityManager.Exists(e)) AgentRefs.CollectOwners(RefsOf(_codec.EntityManager, e), default, _owners);
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Agents, "agent despawn bookkeeping", ex);
            }
            Forget(id);
        }

        /// <summary>Host: the agent was tagged by the game (Updated): it is checked first in the next scan slice.</summary>
        public void Touch(uint id) => _scan.Touch(id);

        /// <summary>
        /// Host: a building is despawned. The game evicts its renters, dismisses its workers and unenrols its students in the
        /// same frame without tagging them, so they are checked first in the next slices (their property and job changes go
        /// out within a frame or two instead of a scan cycle).
        /// </summary>
        public void OnOwnerDespawned(EntityManager em, Entity owner)
        {
            if (!FamilyClassifier.AgentsReplicated || _ids == null || owner == Entity.Null || !em.Exists(owner)) return;
            try
            {
                TouchList<Renter>(em, owner, x => x.m_Renter);
                TouchList<Employee>(em, owner, x => x.m_Worker);
                TouchList<Game.Buildings.Student>(em, owner, x => x.m_Student);
                TouchList<Occupant>(em, owner, x => x.m_Occupant);
                TouchList<Patient>(em, owner, x => x.m_Patient);
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Agents, "agents of a removed building", ex);
            }
        }

        private void TouchList<T>(EntityManager em, Entity e, Func<T, Entity> get) where T : unmanaged, IBufferElementData
        {
            if (!em.HasBuffer<T>(e)) return;
            DynamicBuffer<T> buffer = em.GetBuffer<T>(e, true);
            for (int i = 0; i < buffer.Length; i++)
            {
                uint id = _ids.GetNetId(get(buffer[i]));
                if (id != 0 && _ids.GetFamily(id) == Family.Agent) _scan.Touch(id);
            }
        }

        /// <summary>Host: an agent's full record went to everyone (co-capture, rollback, repair): its baselines are current.</summary>
        public void OnRecordBroadcast(uint id, Entity e)
        {
            if (_codec == null) return;
            _states[id] = Observe(_codec.EntityManager, e);
            _planner.MarkSentToAll(id);
        }

        private void Forget(uint id)
        {
            _states.Remove(id);
            _planner.Forget(id);
            _grid.Remove(id);
        }

        // ================================================================== host: scan and key links (frame end)

        /// <summary>
        /// Host, inside the structure capture (before the batch is broadcast): the scan slice; key-link changes and the
        /// owners of every change of this frame are written into <paramref name="batch"/> through <paramref name="write"/>.
        /// </summary>
        public void HostCapture(SyncBatch.Writer batch, HashSet<Entity> written, RecordWriter write)
        {
            if (_codec == null || !IsHost || !FamilyClassifier.AgentsReplicated || !Features.Agents.IsAvailable) return;
            long start = Perf.Start();
            try
            {
                Scan();
                EntityManager em = _codec.EntityManager;
                foreach (uint id in _keyChanged)
                {
                    Entity e = _ids.GetEntity(id);
                    if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e) || written.Contains(e)) continue;
                    write(e, id, SyncRecordKind.Upsert);
                    OnRecordBroadcast(id, e);
                    _keyUpserts++;
                }
                _keyChanged.Clear();
                WriteOwners(em, written, write);
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Agents, "agent scan", ex);
                _keyChanged.Clear();
                _owners.Clear();
            }
            finally
            {
                Perf.Stop(Perf.AgentScan, start);
            }
        }

        private void Scan()
        {
            EntityManager em = _codec.EntityManager;
            double budget = _planner.PeerCount > 0 ? ScanBudgetMs : ScanBudgetAloneMs;
            _watch.Restart();
            int n = 0;
            while (true)
            {
                if (!_scan.TryNext(out uint id, out _))
                {
                    // a pass is complete: the next one starts with the agents bound now
                    _scanIds.Clear();
                    _ids.AgentIds.CopyTo(_scanIds);
                    if (_scanIds.Count == 0) return;
                    _scan.Restart(_scanIds);
                    if (!_scan.TryNext(out id, out _)) return;
                }
                ScanOne(em, id);
                n++;
                if (n >= MinScanPerFrame && (n & 7) == 0 && _watch.Elapsed.TotalMilliseconds > budget) break;
                if (n >= 200_000) break;
            }
        }

        private void ScanOne(EntityManager em, uint id)
        {
            Entity e = _ids.GetEntity(id);
            if (e == Entity.Null || _ids.GetFamily(id) != Family.Agent)
            {
                Forget(id);
                return;
            }
            if (!em.Exists(e))
            {
                // vanished without a Deleted tag: unbinding queues a despawn for the capture (NetIdSystem)
                _ids.Unbind(id);
                Forget(id);
                return;
            }
            if (em.HasComponent<Game.Common.Deleted>(e)) return; // the tag sweep despawns it
            _scanned++;
            AgentState now = Observe(em, e);
            if (!_states.TryGetValue(id, out AgentState old) || old.Entity != e)
            {
                _states[id] = now; // first observation: the baseline every peer's snapshot already holds
                UpdateGrid(em, id, e);
                return;
            }
            _states[id] = now;
            if (now.Key != old.Key)
            {
                // household membership or property changed: promoted to the broadcast stream with both owners
                _keyChanged.Add(id);
                AgentRefs.CollectOwners(old.Refs, now.Refs, _owners);
                UpdateGrid(em, id, e);
                return;
            }
            if (!old.Refs.Equals(now.Refs) && (old.Refs.Workplace != now.Refs.Workplace))
            {
                // a company's employee list travels with the company's values (buildings: attention values)
                MarkAgentOwner(old.Refs.Workplace);
                MarkAgentOwner(now.Refs.Workplace);
            }
            if (now.Presence != old.Presence)
            {
                _planner.MarkChanged(id, true);
                _structural++;
                _changes++;
            }
            else if (now.Value != old.Value)
            {
                _planner.MarkChanged(id);
                _changes++;
            }
        }

        private void MarkAgentOwner(uint owner)
        {
            if (owner != 0 && _ids.GetFamily(owner) == Family.Agent) _planner.MarkChanged(owner);
        }

        private AgentState Observe(EntityManager em, Entity e)
        {
            var s = new AgentState { Entity = e, Refs = RefsOf(em, e) };
            s.Key = s.Refs.KeyHash();
            s.Presence = PresenceHash(em, e);
            s.Value = _codec.HashSimValues(e, Family.Agent);
            return s;
        }

        /// <summary>The replicated component set and the enabled bits of enableable replicated types.</summary>
        private ulong PresenceHash(EntityManager em, Entity e)
        {
            EntityArchetype archetype = em.GetChunk(e).Archetype;
            if (!_presenceByArchetype.TryGetValue(archetype, out ulong set))
            {
                set = 0x9E3779B97F4A7C15UL;
                foreach (TypeHandler h in _codec.HandlersOf(archetype)) set = HashMix.Combine(set, h.Hash);
                _presenceByArchetype[archetype] = set;
            }
            ulong hash = set;
            TypeHandler[] handlers = _codec.HandlersOf(archetype);
            for (int i = 0; i < handlers.Length; i++)
            {
                TypeHandler h = handlers[i];
                if (h.SyncEnabledBit && !em.IsComponentEnabled(e, h.Type)) hash = HashMix.Combine(hash, h.Hash ^ 0xE0E0E0E0UL);
            }
            return hash;
        }

        /// <summary>Households and companies are placed at their property (camera attention); members follow their household.</summary>
        private void UpdateGrid(EntityManager em, uint id, Entity e)
        {
            Entity property = Entity.Null;
            if (em.HasComponent<PropertyRenter>(e)) property = em.GetComponentData<PropertyRenter>(e).m_Property;
            else if (em.HasComponent<HomelessHousehold>(e)) property = em.GetComponentData<HomelessHousehold>(e).m_TempHome;
            if (property != Entity.Null && em.Exists(property) && em.HasComponent<Game.Objects.Transform>(property))
            {
                float3 p = em.GetComponentData<Game.Objects.Transform>(property).m_Position;
                _grid.Set(id, p.x, p.z);
            }
            else
            {
                _grid.Remove(id);
            }
        }

        /// <summary>
        /// Co-capture (ARCH §4.2.6): the owners collected this frame. Agent owners (households, companies) as full records in
        /// the broadcast batch; building owners as their reverse buffers in one <see cref="MessageKind.AgentValues"/>
        /// broadcast right after the batch (sent by <see cref="HostStream"/>).
        /// </summary>
        private void WriteOwners(EntityManager em, HashSet<Entity> written, RecordWriter write)
        {
            _ownerOut.Reset();
            if (_owners.Count == 0) return;
            _ownerScratch.Clear();
            _ownerScratch.AddRange(_owners);
            _owners.Clear();
            _ownerScratch.Sort();
            int countPosition = -1, entities = 0, agentRecords = 0;
            foreach (uint owner in _ownerScratch)
            {
                Entity e = _ids.GetEntity(owner);
                if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e) || written.Contains(e)) continue;
                Family family = _ids.GetFamily(owner);
                if (family == Family.Agent)
                {
                    // crashfix: a big deletion (a block of buildings) co-captures thousands of households and companies;
                    // full records beyond the per-frame cap carry over to the next frames instead of one huge frame
                    if (agentRecords >= MaxOwnerRecordsPerFrame)
                    {
                        _owners.Add(owner);
                        continue;
                    }
                    write(e, owner, SyncRecordKind.Upsert);
                    OnRecordBroadcast(owner, e);
                    _ownerRecords++;
                    agentRecords++;
                    continue;
                }
                if (!FamilyClassifier.IsReplicated(family)) continue;
                if (_ownerOut.Length > MaxOwnerBytes)
                {
                    _owners.Add(owner); // crashfix: carried over (was dropped)
                    continue;
                }
                if (countPosition < 0) countPosition = SimValueMessage.BeginValues(_ownerOut);
                if (_codec.WriteValueSubset(_ownerOut, owner, family, e, OwnerHandlers()) > 0)
                {
                    entities++;
                    _ownerRecords++;
                }
            }
            if (entities > 0) SimValueMessage.End(_ownerOut, countPosition, entities);
            else _ownerOut.Reset();
        }

        private List<TypeHandler> OwnerHandlers()
        {
            if (_ownerHandlers != null) return _ownerHandlers;
            _ownerHandlers = new List<TypeHandler>();
            foreach (string name in OwnerBufferNames)
            {
                Type type = FamilyClassifier.FindType(name);
                if (type == null) continue;
                TypeIndex index = TypeManager.GetTypeIndex(type);
                TypeHandler h = index == TypeIndex.Null ? null : TypeHandlers.Get(index);
                if (h != null && h.Kind != SyncComponentKind.Presence) _ownerHandlers.Add(h);
            }
            return _ownerHandlers;
        }

        // ================================================================== host: attention and per-peer streams

        /// <summary>Host, after the frame's broadcast batch went out: owner buffers, focus and view sets, per-peer streams.</summary>
        public void HostStream(double now)
        {
            if (_codec == null || !IsHost || !FamilyClassifier.AgentsReplicated || !Features.Agents.IsAvailable) return;
            SessionHost host = _sessions.Host;
            if (host == null) return;
            try
            {
                if (_ownerOut.Length > 0)
                {
                    host.Broadcast(MessageKind.AgentValues, _ownerOut.Buffer, 0, _ownerOut.Length);
                    _bytesSent += _ownerOut.Length;
                    Perf.Counters.AddBytes(Perf.AgentBytes, (long)_ownerOut.Length * Math.Max(1, _peers.Count), Perf.Now);
                    _ownerOut.Reset();
                }
                double dt = _lastStream < 0 ? 0 : Math.Min(1.0, now - _lastStream);
                _lastStream = now;
                _peerScratch.Clear();
                foreach (HostPeer p in _peers.Values) _peerScratch.Add(p.Peer);
                foreach (PeerId peer in _peerScratch)
                {
                    if (!_planner.HasPeer(peer) || !_peers.TryGetValue(peer.Value, out HostPeer p)) continue;
                    if (now >= p.NextFocus) UpdateFocus(p, now);
                    if (now >= p.NextView) UpdateView(p, now);
                    StreamPeer(host, p, dt);
                }
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Agents, "agent stream", ex);
            }
        }

        private void UpdateFocus(HostPeer p, double now)
        {
            p.NextFocus = now + FocusInterval;
            _roots.Clear();
            if (p.Selected != 0) _roots.Add(p.Selected);
            if (p.Followed != 0) _roots.Add(p.Followed);
            _closure.Clear();
            if (_roots.Count > 0) ClosureBuilder.Build(_roots, Expand, 3, MaxClosure, _closure);
            p.Focus.Clear();
            foreach (uint id in _closure)
            {
                if (_ids.GetFamily(id) != Family.Agent) continue;
                p.Focus.Add(id);
                if (p.Focus.Count >= MaxFocus) break;
            }
            _planner.SetFocus(p.Peer, p.Focus);
            // the roots' agents are rechecked first, so a changed value of the panel's subject is found within a frame or two
            // (a large building's whole closure would eat the rolling pass; its members are found by the pass)
            int touched = 0;
            foreach (uint id in _roots)
            {
                if (_ids.GetFamily(id) == Family.Agent) _scan.Touch(id);
            }
            foreach (uint id in p.Focus)
            {
                if (touched++ >= MaxFocusTouches) break;
                _scan.Touch(id);
            }
        }

        private void UpdateView(HostPeer p, double now)
        {
            p.NextView = now + ViewInterval;
            if (!p.HasReport) return;
            ViewReport r = p.Report;
            float zoom = r.Zoom > 0f ? r.Zoom : MinRadius;
            float radius = Math.Max(MinRadius, Math.Min(MaxRadius, zoom * 0.75f));
            float dx = r.PivotX - p.ViewX, dz = r.PivotZ - p.ViewZ;
            bool moved = dx * dx + dz * dz > p.ViewRadius * p.ViewRadius * 0.0625f || Math.Abs(radius - p.ViewRadius) > p.ViewRadius * 0.25f;
            if (!moved && p.View.Count > 0) return;
            p.ViewX = r.PivotX;
            p.ViewZ = r.PivotZ;
            p.ViewRadius = radius;
            _viewScratch.Clear();
            _grid.Query(r.PivotX, r.PivotZ, radius, _viewScratch);
            _viewScratch.Sort();
            p.View.Clear();
            EntityManager em = _codec.EntityManager;
            foreach (uint id in _viewScratch)
            {
                if (p.View.Count >= MaxView) break;
                p.View.Add(id);
                Entity e = _ids.GetEntity(id);
                if (e == Entity.Null || !em.Exists(e)) continue;
                AddList<HouseholdCitizen>(em, e, p.View, MaxView, c => c.m_Citizen);
            }
            _planner.SetView(p.Peer, p.View);
        }

        private void AddList<T>(EntityManager em, Entity e, HashSet<uint> output, int max, Func<T, Entity> get) where T : unmanaged, IBufferElementData
        {
            if (!em.HasBuffer<T>(e)) return;
            DynamicBuffer<T> buffer = em.GetBuffer<T>(e, true);
            int n = Math.Min(buffer.Length, MaxListExpand);
            for (int i = 0; i < n && output.Count < max; i++)
            {
                uint id = _ids.GetNetId(get(buffer[i]));
                if (id != 0) output.Add(id);
            }
        }

        private void AddList<T>(EntityManager em, Entity e, List<uint> output, Func<T, Entity> get) where T : unmanaged, IBufferElementData
        {
            if (!em.HasBuffer<T>(e)) return;
            DynamicBuffer<T> buffer = em.GetBuffer<T>(e, true);
            int n = Math.Min(buffer.Length, MaxListExpand);
            for (int i = 0; i < n; i++)
            {
                uint id = _ids.GetNetId(get(buffer[i]));
                if (id != 0) output.Add(id);
            }
        }

        private void AddRef(List<uint> output, Entity target)
        {
            uint id = _ids.GetNetId(target);
            if (id != 0) output.Add(id);
        }

        /// <summary>The inspection closure's edges (ARCH §4.8.3): what a panel of this entity shows.</summary>
        private void Expand(uint id, List<uint> output)
        {
            EntityManager em = _codec.EntityManager;
            Entity e = _ids.GetEntity(id);
            if (e == Entity.Null || !em.Exists(e)) return;
            // pedestrians and animals: their citizen or household pet
            if (em.HasComponent<Resident>(e)) AddRef(output, em.GetComponentData<Resident>(e).m_Citizen);
            if (em.HasComponent<Game.Creatures.Pet>(e)) AddRef(output, em.GetComponentData<Game.Creatures.Pet>(e).m_HouseholdPet);
            // vehicles: passengers, keeper, owning household or company
            if (em.HasComponent<PersonalCar>(e)) AddRef(output, em.GetComponentData<PersonalCar>(e).m_Keeper);
            if (_ids.GetFamily(id) == Family.Mover && em.HasComponent<Game.Common.Owner>(e)) AddRef(output, em.GetComponentData<Game.Common.Owner>(e).m_Owner);
            AddList<Passenger>(em, e, output, x => x.m_Passenger);
            // agents: household, members, pets, workplace
            if (em.HasComponent<HouseholdMember>(e)) AddRef(output, em.GetComponentData<HouseholdMember>(e).m_Household);
            if (em.HasComponent<HouseholdPet>(e)) AddRef(output, em.GetComponentData<HouseholdPet>(e).m_Household);
            AddList<HouseholdCitizen>(em, e, output, x => x.m_Citizen);
            AddList<HouseholdAnimal>(em, e, output, x => x.m_HouseholdPet);
            if (em.HasComponent<Worker>(e)) AddRef(output, em.GetComponentData<Worker>(e).m_Workplace);
            if (em.HasComponent<TouristHousehold>(e)) AddRef(output, em.GetComponentData<TouristHousehold>(e).m_Hotel);
            // buildings and companies: renters, employees, students, occupants, patients
            AddList<Renter>(em, e, output, x => x.m_Renter);
            AddList<Employee>(em, e, output, x => x.m_Worker);
            AddList<Game.Buildings.Student>(em, e, output, x => x.m_Student);
            AddList<Occupant>(em, e, output, x => x.m_Occupant);
            AddList<Patient>(em, e, output, x => x.m_Patient);
        }

        private void StreamPeer(SessionHost host, HostPeer p, double dt)
        {
            if (_planner.DirtyCount(p.Peer) == 0) return;
            IPeerOutput output = host.GetPeerOutput(p.Peer);
            if (output == null) return;
            long backlog = output.QueuedBytes + Math.Max(0, output.Stats.PendingReliableBytes);
            int allowance = p.Budget.Allowance(dt, backlog);
            // the selected entity's panel never starves behind the rolling refresh (still bounded by the backlog guard)
            if (_planner.FocusDirtyCount(p.Peer) > 0 && backlog <= MaxBacklogBytes) allowance = Math.Max(allowance, FocusFloorBytes);
            if (allowance <= 0) return;
            EntityManager em = _codec.EntityManager;
            _out.Reset();
            _peerBatch.Reset();
            int countPosition = SimValueMessage.BeginValues(_out);
            int baseLength = _out.Length;
            int entities = 0;
            int used = _planner.Drain(p.Peer, allowance, (id, structural) =>
            {
                Entity e = _ids.GetEntity(id);
                if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e) || _ids.GetFamily(id) != Family.Agent) return AgentPlanner.Gone;
                int n;
                if (structural)
                {
                    int before = _peerBatch.Length;
                    _codec.WriteEntity(_peerBatch, SyncRecordKind.Upsert, id, Family.Agent, e);
                    n = _peerBatch.Length - before;
                    if (_peerBatch.RecordCount >= MaxRecordsPerBatch || _peerBatch.Length >= MaxMessageBytes) FlushBatch(host, p.Peer);
                }
                else
                {
                    int before = _out.Length;
                    if (_codec.WriteSimValues(_out, id, Family.Agent, e) == 0) return AgentPlanner.Gone;
                    entities++;
                    n = _out.Length - before;
                    if (_out.Length >= MaxMessageBytes)
                    {
                        FlushValues(host, p.Peer, countPosition, entities);
                        entities = 0;
                        _out.Reset();
                        countPosition = SimValueMessage.BeginValues(_out);
                    }
                }
                return Math.Max(1, n);
            });
            if (entities > 0 && _out.Length > baseLength) FlushValues(host, p.Peer, countPosition, entities);
            if (!_peerBatch.IsEmpty) FlushBatch(host, p.Peer);
            p.Budget.Spend(used);
        }

        private void FlushValues(SessionHost host, PeerId peer, int countPosition, int entities)
        {
            SimValueMessage.End(_out, countPosition, entities);
            host.SendTo(peer, MessageKind.AgentValues, _out.Buffer, 0, _out.Length);
            _bytesSent += _out.Length;
            Perf.Counters.AddBytes(Perf.AgentBytes, _out.Length, Perf.Now);
        }

        private void FlushBatch(SessionHost host, PeerId peer)
        {
            ArraySegment<byte> bytes = _peerBatch.Finish();
            // an Epoch message: applied by the structure applier, acknowledged and verified like every other record
            host.SendTo(peer, MessageKind.Epoch, bytes.Array, bytes.Offset, bytes.Count);
            _bytesSent += bytes.Count;
            Perf.Counters.AddBytes(Perf.AgentBytes, bytes.Count, Perf.Now);
            _peerBatch.Reset();
        }

        // ================================================================== client

        /// <summary>
        /// Client frame start, after the structure batches (so agents spawned in the same frame resolve). Values wait while
        /// structure batches are still queued, unless too many are held.
        /// </summary>
        public void ApplyIncoming(int structureBacklog)
        {
            if (_codec == null || !IsLiveClient)
            {
                if (_sessions.Client == null && _inbox.Count > 0) _inbox.Clear();
                return;
            }
            if (_inbox.Count == 0 || (structureBacklog > 0 && _inbox.Count < MaxHeldMessages)) return;
            long start = Perf.Start();
            try
            {
                double now = Now;
                int n = 0;
                bool any = false;
                while (_inbox.Count > 0 && n++ < MaxInboxPerFrame)
                {
                    byte[] data = _inbox.Dequeue();
                    try
                    {
                        List<SyncRecord> records = SimValueMessage.Read(data, 0, data.Length, out _, out _, out _);
                        _result.Reset();
                        _unresolved.Clear();
                        List<Entity> written = _codec.ApplyAgentValues(data, 0, data.Length, records, _result, _unresolved);
                        _valuesApplied += written.Count;
                        _messagesApplied++;
                        any |= written.Count > 0;
                        foreach (uint id in _unresolved)
                        {
                            // only agents can be asked for again (owner buffers of buildings follow their next change)
                            if (_ids.GetFamily(id) == Family.Agent) _throttle.Note(id, now);
                        }
                    }
                    catch (DecodeException ex)
                    {
                        _log.Warn(LogCategory.Replication, "dropped a malformed agent value message: " + ex.Message);
                    }
                }
                if (any) RefreshPanel(now);
            }
            catch (Exception ex)
            {
                if (!_warnedApply)
                {
                    _warnedApply = true;
                    _log.Error(LogCategory.Replication, "applying agent values failed (further failures are logged at debug level)", ex);
                }
                else
                {
                    _log.Debug(LogCategory.Replication, "applying agent values failed: " + ex.Message);
                }
                Guard.Report(Features.Agents, "agent values apply", ex);
            }
            finally
            {
                Perf.Stop(Perf.AgentApply, start);
            }
        }

        /// <summary>The open info panel shows agent data (household, residents, workers): refresh it at most twice a second.</summary>
        private void RefreshPanel(double now)
        {
            if (now - _lastPanelRefresh < PanelRefreshInterval || _selectedInfo == null) return;
            if (_selectedInfo.selectedEntity == Entity.Null) return;
            _lastPanelRefresh = now;
            try
            {
                _selectedInfo.RequestUpdate();
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Ui, "agents: panel refresh failed: " + ex.Message);
            }
        }

        /// <summary>Client frame end: refresh requests for agents whose values referenced something unknown here.</summary>
        public void ClientFrameEnd(double now)
        {
            SessionClient client = _sessions.Client;
            if (client == null || _codec == null || !IsLiveClient) return;
            try
            {
                _due.Clear();
                if (_throttle.TakeDue(now, AgentRefreshMessage.MaxIds, _due))
                {
                    _request.Reset();
                    AgentRefreshMessage.Write(_request, _due);
                    client.SendToHost(MessageKind.AgentRefresh, _request.Buffer, 0, _request.Length);
                }
                if (now - _lastSweep > 30)
                {
                    _lastSweep = now;
                    _throttle.Sweep(now);
                }
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Agents, "agent refresh requests", ex);
            }
        }

        /// <summary>
        /// Client, view report hook (about 10 Hz): the entity the camera follows ("follow citizen"), so the host streams its
        /// citizen, household and vehicle first.
        /// </summary>
        public void FillViewReport(ViewReport view, bool forRecorder = false)
        {
            // solo testing: the host fills the test recorder's view report with its own selection (forRecorder)
            if (view == null || _codec == null || (!IsLiveClient && !forRecorder)) return;
            try
            {
                Entity followed = _camera?.orbitCameraController != null ? _camera.orbitCameraController.followedEntity : Entity.Null;
                uint id = followed == Entity.Null ? 0 : _ids.GetNetId(followed);
                view.Followed = NetSlot.TryCreate(id, out NetSlot slot) ? slot : NetSlot.None;
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Replication, "agents: followed entity for the view report failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Client became live: snapshot agents the host never bound can never be updated or removed by it; they are removed
        /// (guarded: only when the host does bind agents, and only a minority).
        /// </summary>
        private void OnClientLive()
        {
            if (_codec == null || !Features.Agents.IsAvailable) return;
            try
            {
                EntityManager em = _codec.EntityManager;
                using (EntityQuery agents = em.CreateEntityQuery(new EntityQueryDesc
                {
                    Any = new[]
                    {
                        ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<HouseholdPet>(), ComponentType.ReadOnly<CompanyData>(),
                    },
                    None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Prefabs.PrefabData>() },
                }))
                {
                    NativeArray<Entity> entities = agents.ToEntityArray(Allocator.Temp);
                    var orphans = new NativeList<Entity>(Allocator.Temp);
                    try
                    {
                        foreach (Entity e in entities)
                        {
                            if (_ids.GetNetId(e) == 0) orphans.Add(e);
                        }
                        int bound = entities.Length - orphans.Length;
                        if (orphans.Length == 0) return;
                        if (bound == 0 || orphans.Length * 4 > bound)
                        {
                            _log.Info(LogCategory.Replication, "agents: " + orphans.Length + " of " + entities.Length + " have no host id; kept (the host does not replicate agents?)");
                            return;
                        }
                        em.AddComponent(orphans.AsArray(), ComponentType.ReadWrite<Game.Common.Deleted>());
                        _orphans = orphans.Length;
                        _log.Info(LogCategory.Replication, "agents: " + orphans.Length + " snapshot agents without a host id removed (" + bound + " bound)");
                    }
                    finally
                    {
                        orphans.Dispose();
                        entities.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Agents, "agent orphan sweep", ex);
            }
        }
    }
}
