using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using SkylineTogether.Core;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Identity;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Scheduling;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Diagnostics;
using SkylineTogether.Session;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// Host-authoritative structure sync (ARCH §4-§6, playable subset). The host captures every created, edited or
    /// deleted persistent entity at the end of each frame and streams it; clients apply at the start of the frame,
    /// before any tool or Modification system runs, and the game re-derives everything else. A client's own edits are
    /// predicted by the vanilla tools, captured the same way and proposed to the host, which applies them with the
    /// same applier and streams the authoritative result to everyone. Line state (paths, connections) is host data.
    /// Clients never simulate: their clock follows the host and the systems that would overwrite host data are off.
    /// <para>
    /// Two different counters run here; they never mix:
    /// <list type="bullet">
    /// <item><b>Ledger epochs</b> (gameplay branch, <see cref="AuthoredLedger"/>): the number of each host broadcast
    /// batch, sent as <see cref="SyncRecordKind.Epoch"/>; a client names the newest one it applied as the
    /// <c>BaseEpoch</c> of its proposals, and the host refuses edits of entities authored by somebody else after it
    /// (conflicts, ARCH §5.4.1).</item>
    /// <item><b>Release frames</b> (w2/ids, <see cref="ApplyAckBook"/>): one per host frame, sealed at frame start, with
    /// a per-peer watermark of the <c>MessageKind.Epoch</c> messages queued so far. Clients acknowledge how many
    /// <c>Epoch</c> messages they <i>applied</i> (<see cref="MessageKind.ApplyAck"/>, in order with their proposals); a
    /// released net id is reused (<see cref="NetIdAllocator"/>) only once every peer applied the frame of its despawn
    /// and no join is in progress.</item>
    /// </list>
    /// </para>
    /// <para>
    /// w2/ids: structure is always sent; scanned values and re-sends wait per peer in a coalescing queue and are sent
    /// only while the peer's link has room (ARCH D4: congestion becomes staleness, not backlog); motion yields to both.
    /// </para>
    /// </summary>
    public sealed class SyncController
    {
        private const double ApplyBudgetMs = 6.0;
        private const int MaxBatchesPerFrame = 64;
        private const double ScanBudgetMs = 1.0;
        private const int MinScanPerFrame = 32;
        private const double DeferredBudgetMs = 1.5;
        private const double ClockInterval = 0.25;
        private const double ScanInterval = 0.25;
        private const double CityInterval = 1.0;
        private const double RouteWindowLinger = 2.0;
        private const double AckInterval = 0.1;
        private const int ResendChunkRecords = 512;

        private struct Inbound
        {
            public PeerId Peer;
            public byte[] Data;
            /// <summary>Client: the apply-ack sequence number held for this batch (0: none).</summary>
            public long Seq;
            /// <summary>Host: a <see cref="MessageKind.ApplyAck"/> (Data holds its body).</summary>
            public bool IsAck;
        }

        /// <summary>Host: one peer's deferred (coalesced) updates.</summary>
        private sealed class PeerOut
        {
            public PeerId Peer;
            public readonly CoalescingQueue Queue = new CoalescingQueue();
            public long SentRecords;
            public double LastFlush;
        }

        private readonly SessionManager _sessions;
        private readonly ILog _log;
        private readonly Queue<Inbound> _inbox = new Queue<Inbound>();
        private readonly ClientIdPool _pool = new ClientIdPool();
        // host: failures applying one peer's proposals (rate-limited log; the peer is removed when they keep failing)
        private readonly Dictionary<byte, FeatureHealth> _proposalHealth = new Dictionary<byte, FeatureHealth>();
        private readonly Dictionary<uint, ulong> _shadow = new Dictionary<uint, ulong>();
        private readonly HashSet<Entity> _appliedThisFrame = new HashSet<Entity>();
        private readonly HashSet<Entity> _toolOriginals = new HashSet<Entity>();
        private readonly List<PendingZone> _pendingZones = new List<PendingZone>();
        private readonly SyncBatch.Writer _batch = new SyncBatch.Writer();
        private readonly SyncBatch.Writer _peerBatch = new SyncBatch.Writer();
        private readonly SyncBatch.Writer _digestBatch = new SyncBatch.Writer();
        // global state (taxes, budgets, fees, loan, policies, names, demand): own batch, so a peer that cannot decode a
        // global record never loses entity data
        private readonly GlobalStateSync _global;
        // w3/audit: events, disasters, chirps: presence watch (host) and client fix-ups
        private readonly EventSync _events;
        private readonly SignalFollower _signals; // w3/audit: clients derive lane signals and signal heads
        private readonly SyncBatch.Writer _globalBatch = new SyncBatch.Writer();
        private readonly ClientGate _gate;
        // feat/terrain: terrain and water sync (self-contained; they follow the session events themselves)
        private readonly TerrainSync _terrain;
        private readonly WaterSync _water;
        // w2/simvals: simulation values by attention (+ time-aligned panels), cell maps, statistics history (self-contained)
        private readonly AttentionValues _simValues;
        private readonly CellMapSync _cellMaps;
        private readonly FlowStatusSync _flow; // w4/flow: electricity and water flow status for the infoviews
        private readonly StatisticsSync _statistics;
        // w3/agents: citizens, households, pets and companies (existence here, values and attention there)
        private readonly AgentSync _agents;
        // gameplay branch: UI intents, host validation (ARCH §5.4-5.6), conflicts, build order
        private readonly ActionSync _actions;
        private readonly AuthoredLedger _ledger = new AuthoredLedger();
        // direct-write tools may propose every frame; the limit only stops floods (violations kick, ARCH §5.6)
        private readonly ProposalRateLimiter _rate = new ProposalRateLimiter(120, 240);

        /// <summary>polish: limits of request records (more ids, full re-sends), which request-only batches carry past
        /// <see cref="_rate"/>.</summary>
        private readonly RequestThrottle _requests = new RequestThrottle();
        private readonly ViolationTracker _violations = new ViolationTracker();
        private BuildOrderAllocator _buildOrders = new BuildOrderAllocator();
        /// <summary>Host: entities a proposal or intent wrote this frame, with their author (ledger, capture).</summary>
        private readonly Dictionary<Entity, byte> _authoredThisFrame = new Dictionary<Entity, byte>();
        /// <summary>Host: entities changed without a tag (intents, host UI direct writes) that the capture must stream.</summary>
        private readonly HashSet<Entity> _forceCapture = new HashSet<Entity>();
        private readonly HashSet<Entity> _writtenThisFrame = new HashSet<Entity>();
        // perf pass: the client capture's per-frame collections, reused
        private readonly HashSet<Entity> _clientWritten = new HashSet<Entity>();
        private readonly List<uint> _clientProposed = new List<uint>();
        private readonly List<Entity> _clientChanged = new List<Entity>();
        private Game.Tools.GenerateEdgesSystem _generateEdges;
        /// <summary>Client: the newest ledger epoch applied (the base of its proposals; not a release frame).</summary>
        private uint _baseEpoch;
        private uint _seq;
        private bool _subElementFrame;
        // w2/ids: id quarantine, grants, deferred values
        private readonly ApplyAckBook _acks = new ApplyAckBook();
        private readonly IdGrantBook _grants = new IdGrantBook();
        private readonly Dictionary<byte, PeerOut> _out = new Dictionary<byte, PeerOut>();
        private readonly List<PeerOut> _outList = new List<PeerOut>();
        private readonly List<(uint Start, uint Count)> _grantRuns = new List<(uint, uint)>();
        private readonly List<uint> _scratchValues = new List<uint>(); // w3/audit
        private readonly List<uint> _scratchIds = new List<uint>();
        private readonly LinkBudgetConfig _linkConfig = new LinkBudgetConfig();
        private readonly FrameBudget _applyBudget = new FrameBudget(ApplyBudgetMs);
        private readonly ByteWriter _ackWriter = new ByteWriter(16);
        private readonly Func<byte, long> _epochSent;
        private readonly Func<uint, Family, bool> _mayCreate;
        private PeerId _proposalPeer;
        private uint _barrier;
        private long _lastAckSent = -1;
        private double _lastAckTime;
        private double _lastFrameTime;
        private EntityCodec _codec;
        private NetIdSystem _ids;
        private SimulationSystem _simulation;
        private ToolSystem _tools;
        private ZoneSystem _zones;
        private CitySystem _city;
        private EntityQuery _changed;
        private EntityQuery _tracked;
        private EntityQuery _created;
        private double _lastClock, _lastScan, _lastCity;
        private int _scanCursor;
        private bool _scanCity;
        private readonly List<uint> _scanIds = new List<uint>();
        private bool _toolApplied;
        private long _toolCost;
        private double _routeWindowUntil;
        private bool _routeWindowOpen;
        private bool _clientLive;
        /// <summary>This controller served a client session (mod settings to restore at its end).</summary>
        private bool _wasClient;
        private bool _disposed;
        // mod compatibility (docs/design/MOD-COMPATIBILITY.md): untagged mod state, mod data entities referenced by
        // replicated values, host mod settings, and the client's edit hold for direct-write tools
        private Compat.ModStateWatcher _watcher;
        private readonly Compat.ModSettingsSync _modSettings;
        private readonly List<Compat.ModStateWatcher.Change> _modChanges = new List<Compat.ModStateWatcher.Change>();
        private readonly Queue<(Entity Entity, uint Id, Family Family)> _dependencies = new Queue<(Entity, uint, Family)>();
        private readonly Dictionary<uint, EditHold> _holds = new Dictionary<uint, EditHold>();
        private const double HoldSeconds = 0.6;
        private bool _missingNotified;

        /// <summary>
        /// Client: an entity the local player is editing with a direct-write tool (Move It drag). The host's echo of an
        /// older proposal is not applied while the hold lasts (it would snap the object back mid-drag); the latest host
        /// record seen during the hold is applied when it ends, so the result always converges to the host's.
        /// </summary>
        private struct EditHold
        {
            public double Until;
            public byte[] Batch;
        }

        private struct PendingZone
        {
            public ZoneBlockKey Key;
            public uint[] Zones;
            public double Until;
        }

        public SyncController(SessionManager sessions, ILog log)
        {
            _sessions = sessions;
            _log = log;
            _gate = new ClientGate(log);
            _global = new GlobalStateSync(log);
            _events = new EventSync(log); // w3/audit
            _signals = new SignalFollower(log);
            // gameplay branch: semantic intents beside the global channels; both report refusals here (rollback)
            _actions = new ActionSync(log)
            {
                Touch = (e, peer) =>
                {
                    _authoredThisFrame[e] = peer.Value;
                    _forceCapture.Add(e);
                },
                Reject = (peer, reason, ids) => RejectIntent(peer, reason, ids),
                Allows = Allows,
                LocalPeer = () => _sessions.Client?.LocalPeer ?? PeerId.Host,
            };
            _global.Actions = _actions;
            _global.Allows = Allows;
            // polish: a viewer's client undoes its own edits of shared state no tool gate covers (following citizens)
            _global.LocalAllows = needed =>
            {
                SessionClient client = _sessions.Client;
                return client == null || client.State != ClientState.Live || RolePermissions.Allows(client.LocalRole, needed);
            };
            _global.Reject = (peer, reason, ids) => RejectIntent(peer, reason, ids);
            // feat/terrain
            _terrain = new TerrainSync(sessions, log);
            _water = new WaterSync(sessions, log);
            _terrain.HeightsChanged += _water.MarkHot;
            Integrity = new IntegritySync(sessions, log); // feat/integrity
            _simValues = new AttentionValues(sessions, log); // w2/simvals
            _cellMaps = new CellMapSync(sessions, log) { PeerTopics = _simValues.PeerTopics };
            _flow = new FlowStatusSync(sessions, log) { PeerTopics = _simValues.PeerTopics, InView = _simValues.InView, LocalTopics = () => _simValues.LocalTopics };
            _statistics = new StatisticsSync(sessions, log);
            _agents = new AgentSync(sessions, log); // w3/agents
            _modSettings = new Compat.ModSettingsSync(log); // mod compatibility
            _epochSent = peer => _sessions.Host?.GetEpochSent(new PeerId(peer)) ?? 0;
            _mayCreate = MayCreate;
            sessions.HostStarted += OnHostStarted;
            sessions.ClientStarted += OnClientStarted;
            sessions.SessionEnded += OnSessionEnded;
        }

        /// <summary>True while hosting or in a client session (any state): the frame-loop systems return at once otherwise.</summary>
        public static bool SessionActive
        {
            get
            {
                Session.SessionManager s = Mod.Sessions;
                return s != null && (s.Host != null || s.Client != null);
            }
        }

        public bool IsHost => _sessions.Host != null;

        public bool IsLiveClient => _sessions.Client != null && _sessions.Client.State == ClientState.Live && _clientLive;

        public int BoundCount => _ids?.BoundCount ?? 0;

        public string LastApply { get; private set; } = "";

        /// <summary>Mod compatibility: assets the host uses that this game lacks, with the number of records skipped.</summary>
        public IReadOnlyDictionary<string, int> MissingAssets => _codec?.MissingAssets;

        /// <summary>feat/integrity: divergence detection and repair, host icons, line check (see <see cref="IntegritySync"/>).</summary>
        public IntegritySync Integrity { get; }

        // ---- solo testing (record/replay verdict, ARCH §13.5): read-only views for the recorder and the replay ----

        /// <summary>Host: deferred values waiting for <paramref name="peer"/> (-1: unknown peer); checkpoints wait for 0.</summary>
        public int DeferredCount(PeerId peer) => _out.TryGetValue(peer.Value, out PeerOut o) ? o.Queue.Count : -1;

        /// <summary>Client: host batches received and not applied yet.</summary>
        public int PendingBatches => _inbox.Count;

        /// <summary>The global-state channels (checkpoint hashes).</summary>
        public GlobalStateSync Global => _global;

        /// <summary>One line for the panel: what replication is doing.</summary>
        public string Status
        {
            get
            {
                if (_codec == null || (!IsHost && !IsLiveClient)) return "";
                string s = _ids.BoundCount + " objects in sync";
                if (_inbox.Count > 0) s += ", " + _inbox.Count + " updates queued";
                if (_pendingZones.Count > 0) s += ", " + _pendingZones.Count + " zone blocks pending";
                if (IsHost)
                {
                    int deferred = 0;
                    foreach (PeerOut o in _outList) deferred = Math.Max(deferred, o.Queue.Count);
                    if (deferred > 0) s += ", " + deferred + " values waiting (slow link)";
                    if (_ids.Ids.QuarantinedCount > 0) s += ", " + _ids.Ids.QuarantinedCount + " ids in quarantine";
                }
                foreach (string env in new[] { _terrain.Status, _water.Status, _simValues.Status, _cellMaps.Status, _statistics.Status, _agents.Status, _flow.Status }) // feat/terrain, w2/simvals, w3/agents, w4/flow
                {
                    if (env.Length > 0) s += ", " + env;
                }
                string integrity = Integrity.Status; // feat/integrity
                if (integrity.Length > 0) s += "; " + integrity;
                // review: the movers branch had no line in the panel or the diagnostics
                MoverSync movers = MoverSync.Instance;
                if (movers != null)
                {
                    if (IsHost && movers.Host.MoverCount > 0) s += "; " + movers.Host.MoverCount + " movers";
                    else if (!IsHost && movers.Client.Status.Length > 0) s += "; " + movers.Client.Status;
                }
                FollowerClock clock = FollowerClock.Instance;
                if (!IsHost && clock != null) s += clock.Active ? "; clock follows the host" : "; paused follower";
                // mod compatibility: assets the host uses that this game lacks are never applied, so say so
                int missing = _codec.MissingAssets.Count;
                if (missing > 0)
                {
                    string first = "";
                    foreach (string name in _codec.MissingAssets.Keys)
                    {
                        first = name;
                        break;
                    }
                    s += "; " + missing + (missing == 1 ? " asset missing: " : " assets missing, e.g. ") + first;
                }
                if (!IsHost && _modSettings.OverriddenCount > 0) s += "; " + _modSettings.OverriddenCount + " mod settings from the host";
                string perf = Perf.Counters.CompactSummary(Perf.Now, Perf.ReceivedBytes); // details: diagnostics dump
                if (perf.Length > 0) s += "; perf: " + perf;
                return s;
            }
        }

        /// <summary>w2/ids: the id tables, the quarantine, the deferred queues and the perf counters (diagnostics dump).</summary>
        public void Describe(List<string> lines)
        {
            if (_ids != null) lines.Add("net ids: " + _ids.Describe());
            if (IsHost) lines.Add("presence watch (w3/audit): " + _events.PresenceResends + " re-sends of component sets changed without a tag");
            if (IsHost)
            {
                lines.Add("release frames: sealed " + _acks.CurrentFrame + ", slowest peer " + _acks.MaxLag + " behind" + (_barrier != 0 ? ", join barrier at " + _barrier : "")
                          + "; ledger epoch " + _ledger.LastBroadcast);
                foreach (PeerOut o in _outList)
                {
                    lines.Add("peer " + o.Peer + ": applied frame " + _acks.AckedFrame(o.Peer.Value) + ", deferred " + o.Queue.Count + " (coalesced " + o.Queue.CoalescedCount
                              + " of " + o.Queue.MarkedCount + "), sent " + o.SentRecords + " deferred records");
                }
            }
            else if (_sessions.Client != null)
            {
                ApplyAckTracker t = _sessions.Client.ApplyAcks;
                lines.Add("apply acks: received " + t.Received + ", applied through " + t.AppliedThrough + ", held " + t.HeldCount + ", last ack " + _lastAckSent
                          + "; ledger base epoch " + _baseEpoch);
            }
            _agents.Describe(lines); // w3/agents
            Perf.Counters.Describe(Perf.Now, lines);
        }

        // ================================================================== lifecycle

        private bool EnsureWorld()
        {
            if (_disposed) return false;
            if (_codec != null) return true;
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return false;
            Compat.ModCompat.Refresh(_log); // mod compatibility: before the classifier caches any archetype
            _ids = world.GetOrCreateSystemManaged<NetIdSystem>();
            _codec = new EntityCodec(world, _ids, _log);
            _watcher = new Compat.ModStateWatcher(_codec, _ids, _log);
            Integrity.Attach(_codec, _ids); // feat/integrity
            _simValues.Attach(_codec, _ids); // w2/simvals
            _flow.Attach(world, _ids); // w4/flow
            _agents.Attach(_codec, _ids); // w3/agents
            _simulation = world.GetOrCreateSystemManaged<SimulationSystem>();
            _tools = world.GetOrCreateSystemManaged<ToolSystem>();
            _tools.EventToolChanged += OnToolChanged; // polish: the route preview window opens with the tool
            _zones = world.GetOrCreateSystemManaged<ZoneSystem>();
            _city = world.GetOrCreateSystemManaged<CitySystem>();
            EntityManager em = world.EntityManager;
            _changed = em.CreateEntityQuery(new EntityQueryDesc
            {
                Any = new[] { ComponentType.ReadOnly<Game.Common.Created>(), ComponentType.ReadOnly<Game.Common.Updated>(), ComponentType.ReadOnly<Game.Common.Deleted>() },
                None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
            _tracked = em.CreateEntityQuery(new EntityQueryDesc
            {
                Any = new[] { ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.City.City>(), ComponentType.ReadOnly<Game.Simulation.WaterSourceData>() },
                None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<PrefabData>() },
            });
            _created = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Common.Created>() },
                None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>() },
            });
            _global.Attach(world, _codec, _ids);
            _events.Attach(world, _codec, _ids); // w3/audit
            _signals.Attach(world);
            _actions.Attach(world, _codec, _ids); // gameplay branch
            _generateEdges = world.GetOrCreateSystemManaged<Game.Tools.GenerateEdgesSystem>();
            return true;
        }

        private void OnHostStarted(SessionHost host)
        {
            if (!EnsureWorld()) return;
            host.PeerJoining += OnPeerJoining;
            host.PeerLeft += OnPeerLeft;
            host.PeerLeft += Integrity.OnPeerLeft; // feat/integrity
            Integrity.Reset();
            ResetGameplay();
            ResetHostState();
            host.AppMessage += (peer, e) =>
            {
                if (e.Kind != MessageKind.Proposal && e.Kind != MessageKind.ApplyAck) return;
                var copy = new byte[e.BodyCount];
                Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                // acknowledgements share the proposals' queue: an ack is counted only after every proposal the client
                // sent before it was applied (those may still name ids the client knew before the despawn)
                _inbox.Enqueue(new Inbound { Peer = peer, Data = copy, IsAck = e.Kind == MessageKind.ApplyAck });
                Perf.Counters.AddBytes(Perf.ReceivedBytes, e.BodyCount, Perf.Now);
            };
            // w3/agents: decided once per session, before the city is bound (agents are bound with everything else)
            FamilyClassifier.AgentsReplicated = Features.Agents.IsAvailable;
            if (!FamilyClassifier.AgentsReplicated) _log.Warn(LogCategory.Replication, "agents are not replicated in this session (feature unavailable); clients keep their snapshot's citizens");
            BindCity();
            // mod compatibility: untagged mod state is compared from here on; every mod setting goes out once
            _modSettings.Reset();
            SafeCall("mod state watcher", () => _watcher.Start(true));
        }

        private void OnClientStarted(SessionClient client)
        {
            if (!EnsureWorld()) return;
            _clientLive = false;
            _pool.Clear();
            Integrity.Reset(); // feat/integrity
            _shadow.Clear();
            ResetGameplay();
            SafeCall("mod join gates", () => Compat.ModCompat.OnClientStarted(_log)); // mod compatibility: before the load
            _wasClient = true;
            _lastAckSent = -1;
            _ids.Authority = false;
            FamilyClassifier.AgentsReplicated = false; // w3/agents: the host decides; a client applies what arrives
            client.AppMessage += e =>
            {
                if (e.Kind != MessageKind.Epoch) return;
                // review fix: mover batches belong to MoverClient; here they were decoded three more times for nothing
                // and counted as backlog (which suspends the integrity comparison)
                if (SyncBatch.IsMoverBatch(e.Body, e.BodyOffset, e.BodyCount)) return;
                var copy = new byte[e.BodyCount];
                Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                long seq = client.ApplyAcks.Received;
                client.ApplyAcks.Hold(seq); // w2/ids: acknowledged once applied
                _inbox.Enqueue(new Inbound { Peer = PeerId.Host, Data = copy, Seq = seq });
                Perf.Counters.AddBytes(Perf.ReceivedBytes, e.BodyCount, Perf.Now);
            };
            client.BecameLive += () =>
            {
                _clientLive = true;
                _gate.Close();
                // mod compatibility: mod systems that would derive host-owned state go off; untagged mod state is watched
                SafeCall("mod client gates", () => Compat.ModCompat.OnClientLive(_log));
                SafeCall("mod state watcher", () => _watcher?.Start(false));
                Integrity.OnClientLive(); // feat/integrity
                _log.Info(LogCategory.Replication, "client live with " + _ids.BoundCount + " bound entities; following the host");
            };
        }

        private void OnSessionEnded()
        {
            // mod compatibility: restore the player's own mod settings and every switched mod system
            if (_wasClient) SafeCall("restoring mod settings", _modSettings.RestoreClient);
            SafeCall("mod gates", () => Compat.ModCompat.OnSessionEnded(_log));
            _watcher?.Stop();
            _modSettings.Reset();
            _holds.Clear();
            _dependencies.Clear();
            _missingNotified = false;
            _codec?.ResetSession(); // final review: the missing-asset list is per session
            _wasClient = false; // (the id tables are cleared below for host and client alike)
            FamilyClassifier.AgentsReplicated = false; // w3/agents
            if (_ids != null)
            {
                // nobody can address a quarantined id any more
                _ids.Authority = false;
                _ids.Ids.FlushQuarantine();
            }
            // a client's bindings describe the host's city of that session only; a host's are rebuilt and compacted at
            // the next session start, so keeping them would only grow the tables (single-player safety)
            _ids?.Clear();
            _proposalHealth.Clear();
            _gate.Open();
            Integrity.Reset(); // feat/integrity
            _inbox.Clear();
            _pool.Clear();
            _shadow.Clear();
            _pendingZones.Clear();
            _clientLive = false;
            _routeWindowOpen = false;
            _routeWindowUntil = 0;
            // review fix: a value scan interrupted by the session end resumed in the next session with the old id list
            _scanCursor = 0;
            _scanIds.Clear();
            _appliedThisFrame.Clear();
            _toolOriginals.Clear();
            _toolApplied = false;
            _toolCost = 0;
            _global.Reset();
            ResetGameplay();
            ResetHostState();
            _lastAckSent = -1;
        }

        /// <summary>gameplay branch: forgets ledger, limits, sequence numbers and intent baselines (session start or end).</summary>
        private void ResetGameplay()
        {
            _actions.Reset();
            _events.Reset(); // w3/audit
            _signals.Reset();
            _ledger.Clear();
            _rate.Clear();
            _requests.Clear();
            _violations.Clear();
            _authoredThisFrame.Clear();
            _forceCapture.Clear();
            _baseEpoch = 0;
            _seq = 0;
            _subElementFrame = false;
            _buildOrders = new BuildOrderAllocator();
        }

        /// <summary>w2/ids: forgets release frames, grants and deferred queues (session start or end).</summary>
        private void ResetHostState()
        {
            _acks.Clear();
            _grants.Clear();
            _out.Clear();
            _outList.Clear();
            _barrier = 0;
            _applyBudget.Reset();
        }

        /// <summary>Host: whether <paramref name="peer"/>'s role allows <paramref name="needed"/> (ARCH §5.6).</summary>
        private bool Allows(PeerId peer, Permission needed)
        {
            SessionHost host = _sessions.Host;
            if (host == null || peer == PeerId.Host) return true;
            return RolePermissions.Allows(host.GetRole(peer), needed);
        }

        /// <summary>Mod unload: frees the codec's native buffers and serialization contexts (they are persistent).</summary>
        public void Dispose()
        {
            _disposed = true;
            try
            {
                _codec?.Dispose();
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Replication, "disposing the codec: " + ex.Message);
            }
            _codec = null; // every entry point checks it (and EnsureWorld refuses after disposal)
            if (_tools != null) _tools.EventToolChanged -= OnToolChanged; // polish
        }

        /// <summary>
        /// Host session start (ARCH K17): compacts the ids (w2/ids; no peer holds any yet), then binds every persistent
        /// entity and every prefab before anyone joins.
        /// </summary>
        private void BindCity()
        {
            EntityManager em = _codec.EntityManager;
            _ids.Authority = false;
            int renumbered = _ids.Compact(); // also rebuilds the allocator (nothing quarantined)
            _ids.Authority = true;
            int prefabs = _codec.BindAllPrefabs().Count;
            int bound = 0;
            NativeArray<Entity> entities = _tracked.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in entities)
                {
                    Family family = _codec.Families.Classify(em, e);
                    if (!FamilyClassifier.IsHostReplicated(family) && family != Family.Singleton) continue; // w3/agents: agents too
                    uint known = _ids.GetNetId(e);
                    if (known == 0 && (known = _ids.BindNew(e, family)) != 0) bound++;
                    // w3/audit: the snapshot gives joiners this component set; the presence watch compares against it
                    if (known != 0) _events.NoteWritten(em, known, e, family);
                }
            }
            finally
            {
                entities.Dispose();
            }
            _log.Info(LogCategory.Replication, "host session start: " + prefabs + " prefabs, " + _ids.BoundCount + " bindings (" + _ids.CountOf(Family.Agent) + " agents, " + bound + " new"
                                               + (renumbered > 0 ? ", " + renumbered + " renumbered" : "") + "); " + _ids.Ids.Describe());
            PrimeBuildOrders(em);
        }

        /// <summary>gameplay branch: the build-order allocator starts above every live edge (ARCH §5.4.3).</summary>
        private void PrimeBuildOrders(EntityManager em)
        {
            try
            {
                using (EntityQuery q = em.CreateEntityQuery(ComponentType.ReadOnly<Game.Net.BuildOrder>()))
                {
                    NativeArray<Game.Net.BuildOrder> orders = q.ToComponentDataArray<Game.Net.BuildOrder>(Allocator.Temp);
                    try
                    {
                        foreach (Game.Net.BuildOrder b in orders) _buildOrders.Observe(b.m_Start, b.m_End);
                    }
                    finally
                    {
                        orders.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Replication, "reading the city's build orders failed", ex);
            }
        }

        private void OnPeerJoining(PeerId peer)
        {
            // w2/ids: the peer's stream starts now; every release frame sealed so far counts as applied by it
            _acks.AddPeer(peer.Value);
            var o = new PeerOut { Peer = peer };
            _out[peer.Value] = o;
            _outList.Add(o);
            // first messages of the joiner's stream (buffered until its load finished): the prefab table and an id range
            var w = new SyncBatch.Writer();
            foreach ((uint id, Entity prefab) in _codec.BindAllPrefabs()) _codec.WritePrefab(w, id, prefab);
            Grant(peer, w);
            // gameplay branch: the joiner's snapshot contains every ledger epoch up to now, so its proposals start from here
            w.Epoch(_ledger.LastBroadcast);
            Send(peer, w, Perf.StructureBytes);
            _log.Info(LogCategory.Replication, peer + ": sent " + w.RecordCount + " prefab/range records");
            _global.ResendAll(); // the joiner gets every global channel with the next capture
            _modSettings.ResendAll(); // mod compatibility: and every synchronised mod setting
            _actions.ResendUnlocks();
        }

        private void OnPeerLeft(PeerId peer)
        {
            _proposalHealth.Remove(peer.Value);
            // gameplay branch
            _rate.Forget(peer.Value);
            _requests.Forget(peer.Value);
            _violations.Forget(peer.Value);
            // w2/ids
            _acks.RemovePeer(peer.Value);
            if (_out.TryGetValue(peer.Value, out PeerOut o))
            {
                _out.Remove(peer.Value);
                _outList.Remove(o);
            }
            // ids the peer never used return through the quarantine (nobody else ever saw them)
            _scratchIds.Clear();
            _grants.Remove(peer.Value, _scratchIds);
            int returned = 0;
            if (_ids != null)
            {
                foreach (uint id in _scratchIds)
                {
                    if (_ids.GetEntity(id) == Entity.Null && _ids.Ids.IsAllocated(id) && _ids.Ids.Release(id)) returned++;
                }
            }
            if (returned > 0) _log.Debug(LogCategory.Replication, peer + " left; " + returned + " unused granted ids returned");
        }

        /// <summary>Host: grants a client its next id range (runs of free ids, then fresh ones) and writes the records.</summary>
        private void Grant(PeerId peer, SyncBatch.Writer w)
        {
            _grantRuns.Clear();
            _ids.Ids.Grant(NetIdAllocator.DefaultGrant, _grantRuns);
            foreach ((uint start, uint count) in _grantRuns)
            {
                _grants.Add(peer.Value, start, count);
                w.IdRange(start, count);
            }
        }

        /// <summary>
        /// Host, proposals: a client may create only with an id of its own grants that it never used (marked used once the
        /// entity is bound, <see cref="ApplyProposal"/>), so a stale or replayed spawn can never bind a released id.
        /// </summary>
        private bool MayCreate(uint id, Family family)
        {
            return FamilyClassifier.IsReplicated(family) && _grants.IsUnused(_proposalPeer.Value, id);
        }

        // ================================================================== frame start: apply

        /// <summary>Right after <c>EndFrameBarrier</c> playback, before tools and Modification (R11 §6.2).</summary>
        public void ApplyIncoming()
        {
            // single player (no session): nothing to do, nothing allocated
            if (!SessionActive)
            {
                if (_inbox.Count > 0) _inbox.Clear();
                return;
            }
            _appliedThisFrame.Clear();
            // feat/terrain: received stamps and heightmap/water tiles (independent of the entity stream)
            if (Features.Terrain.IsAvailable)
            {
                long terrain = Perf.Start();
                try
                {
                    _terrain.ApplyIncoming();
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Terrain, "terrain apply", ex);
                }
                Perf.Stop(Perf.Terrain, terrain);
            }
            if (Features.Water.IsAvailable)
            {
                long water = Perf.Start();
                try
                {
                    _water.ApplyIncoming();
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Water, "water apply", ex);
                }
                Perf.Stop(Perf.Water, water);
            }
            if (Features.SimValues.IsAvailable)
            {
                long maps = Perf.Start();
                try
                {
                    _cellMaps.ApplyIncoming(); // w2/simvals: host merges paint patches, clients write map tiles
                    _statistics.ApplyIncoming();
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.SimValues, "map tiles and statistics apply", ex);
                }
                Perf.Stop(Perf.Maps, maps);
            }
            if (_codec == null || !Features.Replication.IsAvailable) return;
            if (!IsHost && !IsLiveClient)
            {
                if (_sessions.Client == null && _inbox.Count > 0) _inbox.Clear();
                return;
            }
            long start = Perf.Start();
            double now = Perf.Now;
            // w2/ids: everything the previous frame sent is out: close its release frame
            if (IsHost) _ids.Ids.Seal(_acks.SealFrame(_epochSent), now);
            // client: local global-state edits are detected before host data can overwrite them
            if (!IsHost)
            {
                SafeCall("action detection", _actions.BeforeApply); // gameplay branch: dev tree, map tiles
                if (Features.GlobalState.IsAvailable)
                {
                    try
                    {
                        _global.BeforeApply();
                    }
                    catch (Exception ex)
                    {
                        Guard.Report(Features.GlobalState, "global state (before apply)", ex);
                    }
                }
                SafeCall("edit holds", ReleaseHolds); // mod compatibility
                if (Features.Events.IsAvailable)
                {
                    try
                    {
                        _events.ClientBeforeApply(); // w3/audit: the player's chirp likes, the gated chirp queue
                    }
                    catch (Exception ex)
                    {
                        Guard.Report(Features.Events, "events (before apply)", ex);
                    }
                }
            }
            double allowance = _applyBudget.BeginFrame();
            int n = 0;
            while (_inbox.Count > 0 && n < MaxBatchesPerFrame)
            {
                // budgeted with carry-over; a batch (a transaction) is never split, and at least one is applied per frame
                if (n > 0 && Perf.Elapsed(start) > allowance) break;
                n++;
                Inbound item = _inbox.Dequeue();
                try
                {
                    if (IsHost)
                    {
                        if (item.IsAck) _acks.OnAck(item.Peer.Value, ApplyAckMessage.Read(item.Data, 0, item.Data.Length));
                        else ApplyProposal(item.Peer, item.Data);
                    }
                    else
                    {
                        ApplyFromHost(item.Data);
                    }
                }
                catch (Exception ex)
                {
                    ApplyFailed(item.Peer, ex);
                    if (!Features.Replication.IsAvailable) return; // the session is ending (client: a rejoin repairs it)
                }
                finally
                {
                    if (item.Seq != 0) _sessions.Client?.ApplyAcks.Release(item.Seq);
                }
            }
            RetryZones();
            // w2/simvals (client): simulation values after the structure batches, so ids spawned this frame resolve
            if (!IsHost) _simValues.ApplyIncoming(_inbox.Count);
            // w4/flow (client): flow status into the derived flow edges (after the structure apply, so new owners resolve)
            if (!IsHost && Features.FlowStatus.IsAvailable)
            {
                try
                {
                    _flow.ApplyIncoming(now);
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.FlowStatus, "flow status apply", ex);
                }
            }
            // w3/agents (client): agent values once the structure batches that spawn them are applied
            if (!IsHost && Features.Agents.IsAvailable) _agents.ApplyIncoming(_inbox.Count);
            // w3/audit (client): per-player chirp likes, event target lists, weather phenomenon hotspot frames, tree growth
            if (!IsHost && Features.Events.IsAvailable)
            {
                try
                {
                    _events.ClientAfterApply(_appliedThisFrame);
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Events, "events (after apply)", ex);
                }
            }
            // w3/audit (client): lane signals and signal heads from the host's junction phases
            if (!IsHost && Features.SimValues.IsAvailable)
            {
                long signals = Perf.Start();
                try
                {
                    _signals.Update();
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.SimValues, "traffic lights", ex);
                }
                Perf.Stop(Perf.Signals, signals);
            }
            // client: what the appliers wrote becomes the baseline, so it is never proposed back
            if (!IsHost)
            {
                if (Features.GlobalState.IsAvailable)
                {
                    try
                    {
                        _global.AfterApply();
                    }
                    catch (Exception ex)
                    {
                        Guard.Report(Features.GlobalState, "global state (after apply)", ex);
                    }
                }
                SafeCall("milestones", _actions.AfterApply); // gameplay branch
            }
            if (IsHost) UpdateQuarantine(now);
            _applyBudget.EndFrame(Perf.Stop(Perf.Apply, start));
        }

        /// <summary>
        /// A batch could not be applied. On a client the replica may now differ from the host's city: failures count
        /// against replication (critical), so repeated ones end the session and the player is offered a rejoin with a
        /// fresh snapshot (ARCH D6 "fail into resync"). On the host, one peer's broken proposals never end the
        /// session: they are logged with a rate limit and the peer is removed once they keep failing.
        /// </summary>
        private void ApplyFailed(PeerId peer, Exception ex)
        {
            string what = ex is DecodeException ? "decoding a batch from " + peer : "applying a batch from " + peer;
            if (!IsHost)
            {
                Guard.Report(Features.Replication, what, ex);
                return;
            }
            if (!_proposalHealth.TryGetValue(peer.Value, out FeatureHealth health))
            {
                _proposalHealth[peer.Value] = health = new FeatureHealth("proposals." + peer.Value, "proposals of " + peer, false);
            }
            FailureVerdict v = health.ReportFailure(ex, _sessions.Clock.Seconds);
            if (v.Log) _log.Warn(LogCategory.Replication, what + " failed" + (v.Suppressed > 0 ? " (" + v.Suppressed + " more not logged)" : "") + ": " + ex.GetType().Name + ": " + ex.Message);
            if (v.JustTripped)
            {
                _log.Error(LogCategory.Replication, peer + ": its changes keep failing on the host; removing it from the session", ex);
                _sessions.Host?.Kick(peer, "The host could not apply your changes. Join again to get a fresh copy of the city.");
            }
        }

        private void SafeCall(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Actions, what, ex);
            }
        }

        /// <summary>
        /// Host: frees quarantined ids every peer has applied the despawn of. While a join is in progress, a snapshot may
        /// still be written: ids released from that moment on wait until the join is over (the join barrier).
        /// </summary>
        private void UpdateQuarantine(double now)
        {
            SessionHost host = _sessions.Host;
            if (host == null) return;
            if (host.JoinInProgress)
            {
                if (_barrier == 0) _barrier = Math.Max(1u, _acks.CurrentFrame);
            }
            else
            {
                _barrier = 0;
            }
            _ids.Ids.Update(_acks.MinAckedFrame, _barrier, now);
        }

        private void ApplyFromHost(byte[] data)
        {
            // mod compatibility: records of entities the player is dragging with a direct-write tool wait (edit hold)
            Func<SyncRecord, bool> skip = null;
            if (_holds.Count > 0)
            {
                double now = _sessions.Clock.Seconds;
                skip = rec =>
                {
                    if (rec.Kind == SyncRecordKind.Despawn)
                    {
                        _holds.Remove(rec.NetId); // a deleted entity is never held (its id may be reused later)
                        return false;
                    }
                    if (!_holds.TryGetValue(rec.NetId, out EditHold hold) || now >= hold.Until) return false;
                    hold.Batch = data;
                    _holds[rec.NetId] = hold;
                    return true;
                };
            }
            List<SyncRecord> records = SyncBatch.Read(data, 0, data.Length); // decoded once for every consumer
            ApplyResult r = _codec.Apply(records, data, 0, data.Length, null, skip);
            // mod compatibility: tell the player once that objects of missing assets are left out (details in the log)
            if (!_missingNotified && _codec.MissingAssets.Count > 0)
            {
                _missingNotified = true;
                SessionManager.Notify("Some objects of the host's city use assets or mods you do not have; they are left out. See the diagnostics for the list.");
            }
            foreach (Entity e in r.Touched) _appliedThisFrame.Add(e);
            foreach ((uint start, uint count) in r.IdRanges) _pool.Add(start, count);
            if (r.Clock.HasValue) FollowClock(r.Clock.Value);
            if (r.Spawned + r.Upserted + r.Despawned > 0) LastApply = r.ToString();
            if (r.Problems.Count > 0) _log.Debug(LogCategory.Replication, "apply problems: " + string.Join("; ", r.Problems));
            ApplyZoneRecords(records);
            ApplyGlobal(data, PeerId.Host, false);
            if (Features.Integrity.IsAvailable)
            {
                try
                {
                    Integrity.OnHostBatch(records, _inbox.Count); // feat/integrity: presence masks, digests, icons, line rows
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Integrity, "integrity (host batch)", ex);
                }
            }
        }

        private void ApplyGlobal(byte[] data, PeerId peer, bool proposal)
        {
            if (!Features.GlobalState.IsAvailable) return;
            try
            {
                _global.Apply(data, peer, proposal);
            }
            catch (Exception ex)
            {
                Guard.Report(Features.GlobalState, "global state apply", ex);
            }
        }

        private void ApplyProposal(PeerId peer, byte[] data)
        {
            List<SyncRecord> records = SyncBatch.Read(data, 0, data.Length);
            // gameplay branch: validation (ARCH §5.4.1); a refused proposal is rolled back on its originator by a resend
            ProposalFacts facts = Facts(records, out uint seq, out uint baseEpoch);
            bool isGlobal = SyncBatch.FirstRecordKind(data, 0, data.Length) == (byte)SyncRecordKind.Global;
            if ((facts.EntityCount > 0 || facts.HasCost || isGlobal) && !_rate.TryTake(peer.Value, _sessions.Clock.Seconds))
            {
                RejectProposal(peer, seq, RejectReason.RateLimited, records);
                ServeRequests(peer, records);
                return;
            }
            if (facts.EntityCount > 0 || facts.HasCost)
            {
                RejectReason reason = Validate(peer, records, facts, baseEpoch);
                if (reason != RejectReason.None)
                {
                    RejectProposal(peer, seq, reason, records);
                    ServeRequests(peer, records);
                    return;
                }
            }
            List<BuildOrderRange> touchedEdges = TouchedEdgeBuildOrders(records);
            _proposalPeer = peer;
            ApplyResult r = _codec.Apply(records, data, 0, data.Length, _mayCreate);
            RebaseBuildOrders(r.SpawnedEntities, touchedEdges);
            for (int i = 0; i < records.Count; i++)
            {
                SyncRecord rec = records[i];
                if (rec.Kind != SyncRecordKind.Spawn && rec.Kind != SyncRecordKind.Upsert) continue;
                Entity written = _ids.GetEntity(rec.NetId);
                if (written == Entity.Null) continue;
                _authoredThisFrame[written] = peer.Value;
                // w2/ids: the ids the client created with are used now (no-op for ids that are not its grants)
                _grants.TryConsume(peer.Value, rec.NetId);
            }
            string summary = peer + ": " + r;
            if (r.HasCost && r.Cost != 0) ChargeCity(r.Cost);
            if (r.Problems.Count > 0) _log.Info(LogCategory.Replication, peer + " proposal problems: " + string.Join("; ", r.Problems));
            ApplyZoneRecords(records);
            ApplyGlobal(data, peer, true);
            ServeRequests(peer, records);
            LastApply = summary;
        }

        private void ServeRequests(PeerId peer, List<SyncRecord> records)
        {
            foreach (SyncRecord rec in records)
            {
                if (rec.Kind == SyncRecordKind.RequestIds && Features.Integrity.IsAvailable)
                {
                    try
                    {
                        Integrity.OnPeerRequest(peer, rec); // feat/integrity
                    }
                    catch (Exception ex)
                    {
                        Guard.Report(Features.Integrity, "integrity request of " + peer, ex);
                    }
                }
                if (rec.Kind != SyncRecordKind.Request) continue;
                double now = _sessions.Clock.Seconds;
                if (rec.Request == SyncRequest.MoreIds)
                {
                    // polish: request-only batches skip the proposal rate limit, so grants have their own (and a cap on
                    // the ids a peer holds unused); a refused request is dropped and the client asks again with backoff
                    if (!_requests.TryGrantIds(peer.Value, now, _grants.UnusedCount(peer.Value)))
                    {
                        _log.Debug(LogCategory.Replication, peer + ": id request over the limit; dropped");
                        CountViolation(peer, RejectReason.RateLimited);
                        continue;
                    }
                    var w = new SyncBatch.Writer();
                    Grant(peer, w);
                    if (!w.IsEmpty) Send(peer, w, Perf.StructureBytes);
                }
                else if (rec.Request == SyncRequest.ResendRoutes || rec.Request == SyncRequest.ResendAll)
                {
                    bool routesOnly = rec.Request == SyncRequest.ResendRoutes;
                    // polish: a re-send scans the whole id table; over the limit it is deferred (FlushDeferred serves it)
                    if (_requests.TryResend(peer.Value, routesOnly, now)) Resend(peer, routesOnly);
                    else _log.Debug(LogCategory.Replication, peer + ": re-send request over the limit; deferred");
                }
            }
        }

        // ================================================================== gameplay branch: host validation and rollback

        private static ProposalFacts Facts(List<SyncRecord> records, out uint seq, out uint baseEpoch)
        {
            var f = new ProposalFacts();
            seq = 0;
            // a proposal without a header (older client) skips the conflict check
            baseEpoch = uint.MaxValue;
            foreach (SyncRecord r in records)
            {
                switch (r.Kind)
                {
                    case SyncRecordKind.Spawn: f.Spawns++; break;
                    case SyncRecordKind.Upsert:
                    case SyncRecordKind.Values: f.Upserts++; break;
                    case SyncRecordKind.Despawn: f.Despawns++; break;
                    case SyncRecordKind.ZoneCells: f.ZoneBlocks++; break;
                    case SyncRecordKind.Cost:
                        f.HasCost = true;
                        f.Cost += r.Amount;
                        break;
                    case SyncRecordKind.ProposalHeader:
                        seq = r.Seq;
                        baseEpoch = r.BaseEpoch;
                        f.Flags = r.ProposalFlags;
                        break;
                }
            }
            return f;
        }

        /// <summary>
        /// ARCH §5.4.1 rules 1-4 and 7 over the host's world: permission of the sender's role, size, existence and
        /// conflicts of every entity the proposal edits or deletes, id grants of its creations, and money.
        /// </summary>
        private RejectReason Validate(PeerId peer, List<SyncRecord> records, in ProposalFacts facts, uint baseEpoch)
        {
            if (!Allows(peer, ProposalRules.Required(facts))) return RejectReason.NoPermission;
            RejectReason size = ProposalRules.CheckSize(facts);
            if (size != RejectReason.None) return size;
            EntityManager em = _codec.EntityManager;
            var spawned = new HashSet<uint>();
            foreach (SyncRecord r in records)
            {
                if (r.Kind == SyncRecordKind.Spawn) spawned.Add(r.NetId);
            }
            foreach (SyncRecord r in records)
            {
                // mod compatibility: an object of an asset or mod prefab the host does not have can never be created
                // here (the client is told and its prediction rolled back)
                if ((r.Kind == SyncRecordKind.Spawn || r.Kind == SyncRecordKind.Upsert) && _codec.NamesUnknownPrefab(r))
                {
                    _log.Info(LogCategory.Replication, peer + ": #" + r.NetId + " uses an asset or mod prefab the host does not have");
                    return RejectReason.HostError;
                }
                switch (r.Kind)
                {
                    case SyncRecordKind.Spawn:
                    {
                        // w3/audit: events, chirps and journal entries come from the host's simulation only
                        if (FamilyClassifier.IsHostOnly(r.Family)) return RejectReason.Invalid;
                        Entity existing = _ids.GetEntity(r.NetId);
                        bool bound = existing != Entity.Null && em.Exists(existing);
                        // w2/ids: a creation needs an unused id of the sender's own grants (released ids are never reusable by a client)
                        if (!bound && !_grants.IsUnused(peer.Value, r.NetId)) return RejectReason.Invalid;
                        if (bound && _ledger.IsConflict(r.NetId, baseEpoch, peer.Value)) return RejectReason.Conflict;
                        break;
                    }
                    case SyncRecordKind.Upsert:
                    case SyncRecordKind.Values:
                    {
                        if (spawned.Contains(r.NetId)) break;
                        Entity e = _ids.GetEntity(r.NetId);
                        // edited meanwhile deleted by somebody else (or never committed: a refused earlier proposal)
                        if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) return RejectReason.Conflict;
                        if (!FamilyClassifier.IsReplicated(_ids.GetFamily(r.NetId))) return RejectReason.Invalid;
                        if (FamilyClassifier.IsHostOnly(_ids.GetFamily(r.NetId))) return RejectReason.Invalid; // w3/audit
                        if (_ledger.IsConflict(r.NetId, baseEpoch, peer.Value)) return RejectReason.Conflict;
                        break;
                    }
                    case SyncRecordKind.Despawn:
                    {
                        Entity e = _ids.GetEntity(r.NetId);
                        if (e == Entity.Null || !em.Exists(e)) break; // already gone: nothing to delete
                        // mod compatibility: mod tools may delete vehicles, citizens and animals (Better Bulldozer)
                        Family despawned = _ids.GetFamily(r.NetId);
                        if (!FamilyClassifier.IsReplicated(despawned) && !(despawned == Family.Mover && (facts.Flags & ProposalFlags.DirectTool) != 0)) return RejectReason.Invalid;
                        if (FamilyClassifier.IsHostOnly(despawned)) return RejectReason.Invalid; // w3/audit
                        if (_ledger.IsConflict(r.NetId, baseEpoch, peer.Value)) return RejectReason.Conflict;
                        break;
                    }
                }
            }
            Entity city = _city.City;
            if (facts.HasCost && city != Entity.Null && em.HasComponent<Game.City.PlayerMoney>(city))
            {
                Game.City.PlayerMoney money = em.GetComponentData<Game.City.PlayerMoney>(city);
                RejectReason cost = ProposalRules.CheckCost(facts, money.money, money.m_Unlimited);
                if (cost != RejectReason.None) return cost;
            }
            return RejectReason.None;
        }

        /// <summary>
        /// Host: refuses a proposal and rolls it back on its originator (ARCH §5.5.2): a result record, then every entity
        /// the proposal touched in the host's state (alive: full record; unknown here: despawn, which removes the
        /// client's predicted creations) and the host's cells of every zone block it painted.
        /// </summary>
        private void RejectProposal(PeerId peer, uint seq, RejectReason reason, List<SyncRecord> records)
        {
            var ids = new List<uint>();
            var zones = new List<ZoneBlockKey>();
            foreach (SyncRecord r in records)
            {
                switch (r.Kind)
                {
                    case SyncRecordKind.Spawn:
                    case SyncRecordKind.Upsert:
                    case SyncRecordKind.Values:
                    case SyncRecordKind.Despawn:
                        ids.Add(r.NetId);
                        break;
                    case SyncRecordKind.ZoneCells:
                        zones.Add(r.Block);
                        break;
                }
            }
            _log.Info(LogCategory.Replication, peer + ": refused proposal " + seq + " (" + reason + "; " + ids.Count + " entities, " + zones.Count + " zone blocks)");
            SendRollback(peer, seq, reason, ids, zones);
            CountViolation(peer, reason);
        }

        /// <summary>Host: refuses an intent or global-state edit (rollback of its targets; seq 0).</summary>
        private void RejectIntent(PeerId peer, RejectReason reason, IReadOnlyList<uint> ids)
        {
            if (_codec == null || !IsHost) return;
            SendRollback(peer, 0, reason, ids ?? Array.Empty<uint>(), null);
            CountViolation(peer, reason);
        }

        private void CountViolation(PeerId peer, RejectReason reason)
        {
            if (!ViolationTracker.IsViolation(reason) || !_violations.Record(peer.Value, _sessions.Clock.Seconds)) return;
            _log.Warn(LogCategory.Replication, peer + ": too many invalid or flooding proposals; kicking");
            _violations.Forget(peer.Value);
            _sessions.Host?.Kick(peer, "Too many invalid changes were sent to the host.");
        }

        private void SendRollback(PeerId peer, uint seq, RejectReason reason, IReadOnlyList<uint> ids, List<ZoneBlockKey> zones)
        {
            EntityManager em = _codec.EntityManager;
            var w = new SyncBatch.Writer();
            w.ProposalResult(seq, reason);
            var seen = new HashSet<uint>();
            foreach (uint id in ids)
            {
                if (id == 0 || !seen.Add(id)) continue;
                try
                {
                    Entity e = _ids.GetEntity(id);
                    Family family = _ids.GetFamily(id);
                    bool alive = e != Entity.Null && em.Exists(e) && !em.HasComponent<Game.Common.Deleted>(e);
                    if (!alive)
                    {
                        w.Despawn(id);
                    }
                    else if (FamilyClassifier.IsReplicated(family))
                    {
                        if (em.HasComponent<PrefabRef>(e)) _codec.EnsurePrefab(em.GetComponentData<PrefabRef>(e).m_Prefab, w);
                        _codec.WriteEntity(w, SyncRecordKind.Upsert, id, family, e);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn(LogCategory.Replication, peer + ": rolling back #" + id + " failed", ex);
                }
                if (w.RecordCount >= ResendChunkRecords)
                {
                    Send(peer, w, Perf.StructureBytes);
                    w = new SyncBatch.Writer();
                }
            }
            if (zones != null)
            {
                foreach (ZoneBlockKey key in zones)
                {
                    Entity block = FindBlock(key);
                    if (block != Entity.Null) WriteZoneBlock(w, block, true);
                }
            }
            if (!w.IsEmpty) Send(peer, w, Perf.StructureBytes);
        }

        /// <summary>Host: the build orders of the existing edges a proposal edits or deletes (read before the apply).</summary>
        private List<BuildOrderRange> TouchedEdgeBuildOrders(List<SyncRecord> records)
        {
            var list = new List<BuildOrderRange>();
            EntityManager em = _codec.EntityManager;
            foreach (SyncRecord r in records)
            {
                if (r.Kind != SyncRecordKind.Upsert && r.Kind != SyncRecordKind.Despawn) continue;
                Entity e = _ids.GetEntity(r.NetId);
                if (e == Entity.Null || !em.Exists(e) || !em.HasComponent<Game.Net.BuildOrder>(e)) continue;
                Game.Net.BuildOrder b = em.GetComponentData<Game.Net.BuildOrder>(e);
                list.Add(new BuildOrderRange(b.m_Start, b.m_End));
            }
            return list;
        }

        /// <summary>
        /// Host: re-bases the build orders of a client's fresh edges onto the host counter (ARCH §5.4.3), so zone-block
        /// priority and node-lane order are identical on every peer; edges split from host edges keep their inherited
        /// sub-ranges. The committed edges then advance vanilla's counter themselves (<c>UpdateBuildOrderJob</c>).
        /// </summary>
        private void RebaseBuildOrders(List<Entity> spawned, List<BuildOrderRange> touchedEdges)
        {
            if (spawned.Count == 0) return;
            try
            {
                EntityManager em = _codec.EntityManager;
                var edges = new List<Entity>();
                var ranges = new List<BuildOrderRange>();
                foreach (Entity e in spawned)
                {
                    if (!em.Exists(e) || !em.HasComponent<Game.Net.Edge>(e) || !em.HasComponent<Game.Net.BuildOrder>(e)) continue;
                    Game.Net.BuildOrder b = em.GetComponentData<Game.Net.BuildOrder>(e);
                    edges.Add(e);
                    ranges.Add(new BuildOrderRange(b.m_Start, b.m_End));
                }
                if (edges.Count == 0) return;
                // the apply's structural changes completed every job, so the counter is readable on the main thread
                uint counter = 0;
                try
                {
                    if (_generateEdges != null) counter = _generateEdges.GetBuildOrder().value;
                }
                catch (Exception)
                {
                    counter = 0; // a job still owns it: the allocator's own maximum is used
                }
                int n = BuildOrderPlanner.RebaseFresh(_buildOrders, ranges, touchedEdges, counter);
                if (n == 0) return;
                for (int i = 0; i < edges.Count; i++)
                {
                    em.SetComponentData(edges[i], new Game.Net.BuildOrder { m_Start = ranges[i].Start, m_End = ranges[i].End });
                }
                _log.Debug(LogCategory.Replication, "re-based the build order of " + n + " committed edge(s); next " + _buildOrders.Next);
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Replication, "re-basing build orders failed (zoning priority may differ)", ex);
            }
        }

        private void ChargeCity(long cost)
        {
            EntityManager em = _codec.EntityManager;
            Entity city = _city.City;
            if (city == Entity.Null || !em.HasComponent<Game.City.PlayerMoney>(city)) return;
            Game.City.PlayerMoney money = em.GetComponentData<Game.City.PlayerMoney>(city);
            int amount = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, cost));
            if (amount > 0) money.Subtract(amount);
            else money.Add(-amount);
            em.SetComponentData(city, money);
        }

        private void FollowClock(uint frame)
        {
            // movers branch: a running follower clock owns frameIndex; the record is one of its samples
            if (FollowerClock.Instance != null && FollowerClock.Instance.OnHostClock(frame)) return;
            Action<SimulationSystem, uint> set = Probes.SetFrameIndex; // P-CLOCK
            if (set == null || !Features.Clock.IsAvailable) return;
            try
            {
                set(_simulation, frame);
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Clock, "following the host's frame", ex);
            }
        }

        /// <summary>
        /// Host, right before <c>SaveGameSystem</c>: binds the entities created this frame, so a join snapshot taken in
        /// this frame already carries their ids (otherwise the joiner would create them a second time).
        /// </summary>
        public void BindCreated()
        {
            if (_codec == null || !IsHost || !Features.Replication.IsAvailable) return;
            if (_created.IsEmptyIgnoreFilter) return;
            EntityManager em = _codec.EntityManager;
            NativeArray<ArchetypeChunk> chunks = _created.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                EntityTypeHandle entityType = em.GetEntityTypeHandle();
                foreach (ArchetypeChunk chunk in chunks)
                {
                    bool known = _codec.Families.TryClassifyArchetype(chunk.Archetype, out Family chunkFamily);
                    if (known && !FamilyClassifier.IsHostReplicated(chunkFamily)) continue; // lanes, blocks, movers... (w3/agents: agents are bound)
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        Entity e = entities[i];
                        if (_ids.GetNetId(e) != 0) continue;
                        Family family = known ? chunkFamily : _codec.Families.Classify(em, e);
                        if (FamilyClassifier.IsHostReplicated(family)) _ids.BindNew(e, family);
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }

        // ================================================================== tool hook

        /// <summary>Called at the start of the ApplyTool phase: the local tool is about to apply its preview.</summary>
        public void OnToolApply()
        {
            if (!SessionActive) return;
            if (Features.Terrain.IsAvailable)
            {
                try
                {
                    _terrain.OnToolApply(); // feat/terrain: brush stamps, on the host and on clients
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Terrain, "terrain stamp capture", ex);
                }
            }
            if (Features.SimValues.IsAvailable)
            {
                try
                {
                    _cellMaps.OnToolApply(); // w2/simvals: resource brushes (ore, oil, fertility, groundwater)
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.SimValues, "resource brush capture", ex);
                }
            }
            // gameplay branch: the host records its own tool's originals too (authored changes, conflict ledger)
            if (_codec == null || (!IsLiveClient && !IsHost) || !Features.Replication.IsAvailable) return;
            EntityManager em = _codec.EntityManager;
            _toolApplied = true;
            using (EntityQuery temps = em.CreateEntityQuery(ComponentType.ReadOnly<Game.Tools.Temp>()))
            {
                NativeArray<Game.Tools.Temp> values = temps.ToComponentDataArray<Game.Tools.Temp>(Allocator.Temp);
                try
                {
                    foreach (Game.Tools.Temp t in values)
                    {
                        // exactly what ToolApplySystem charges (ToolApplySystem.cs:59-66): cancelled temps are free
                        if ((t.m_Flags & Game.Tools.TempFlags.Cancel) == 0) _toolCost += t.m_Cost;
                        if (t.m_Original != Entity.Null) _toolOriginals.Add(t.m_Original);
                    }
                }
                finally
                {
                    values.Dispose();
                }
            }
        }

        // ================================================================== frame end: capture

        /// <summary>Just before <c>PrepareCleanUpSystem</c>, after every Modification phase of the frame.</summary>
        public void CaptureOutgoing(double now)
        {
            if (_codec != null) _codec.DependencyBinder = null; // mod compatibility: never left over from a failed capture
            if (!SessionActive) return; // single player: nothing to do, nothing allocated
            // feat/terrain: stamps out, readbacks in, settled heightmap tiles, audits, water samples
            if (Features.Terrain.IsAvailable)
            {
                long terrain = Perf.Start();
                try
                {
                    _terrain.CaptureOutgoing();
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Terrain, "terrain capture", ex);
                }
                Perf.Stop(Perf.Terrain, terrain);
            }
            if (Features.Water.IsAvailable)
            {
                long water = Perf.Start();
                try
                {
                    _water.CaptureOutgoing();
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Water, "water capture", ex);
                }
                Perf.Stop(Perf.Water, water);
            }
            if (Features.SimValues.IsAvailable)
            {
                long maps = Perf.Start();
                try
                {
                    _cellMaps.CaptureOutgoing(); // w2/simvals: host map tiles, client paint patches
                    _statistics.CaptureOutgoing();
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.SimValues, "map tiles and statistics capture", ex);
                }
                Perf.Stop(Perf.Maps, maps);
            }
            double dt = _lastFrameTime > 0 ? Math.Min(0.25, Math.Max(0.0, now - _lastFrameTime)) : 1.0 / 60.0;
            _lastFrameTime = now;
            if (_codec != null)
            {
                // gameplay branch: the bulldozer's sub-element mode deletes derived props and fences (no net ids)
                _subElementFrame = _toolApplied && _tools.activeTool is BulldozeToolSystem bulldozer && bulldozer.actualMode != BulldozeToolSystem.Mode.MainElements;
                long start = Perf.Start();
                try
                {
                    if (Features.Replication.IsAvailable)
                    {
                        if (IsHost)
                        {
                            // the host player's own direct UI writes (historical, vehicles, districts, colours) become captures
                            SafeCall("host action watch", () => _actions.HostWatch(new HashSet<Entity>(_authoredThisFrame.Keys)));
                            CaptureHost(now, dt);
                            if (Features.SimValues.IsAvailable)
                            {
                                long simValues = Perf.Start();
                                try
                                {
                                    _simValues.CaptureOutgoing(now); // w2/simvals: attention values and inspection samples
                                }
                                catch (Exception ex)
                                {
                                    Guard.Report(Features.SimValues, "attention values and inspection samples", ex);
                                }
                                Perf.Stop(Perf.SimValues, simValues);
                            }
                            if (Features.FlowStatus.IsAvailable)
                            {
                                try
                                {
                                    _flow.CaptureOutgoing(now); // w4/flow: flow status to peers with the Electricity/Water topic
                                }
                                catch (Exception ex)
                                {
                                    Guard.Report(Features.FlowStatus, "flow status capture", ex);
                                }
                            }
                        }
                        else if (IsLiveClient)
                        {
                            long client = Perf.Start();
                            CaptureClient(now);
                            SafeCall("action capture", () => _actions.ClientCapture(_appliedThisFrame)); // gameplay branch
                            Perf.Stop(Perf.ClientCapture, client);
                            if (Features.Agents.IsAvailable) _agents.ClientFrameEnd(now); // w3/agents: refresh requests
                        }
                    }
                    if (Features.Integrity.IsAvailable)
                    {
                        try
                        {
                            if (IsLiveClient) Integrity.ClientFrameEnd(now); // feat/integrity: integrity requests, icon digest
                        }
                        catch (Exception ex)
                        {
                            Guard.Report(Features.Integrity, "integrity (client frame end)", ex);
                        }
                    }
                }
                finally
                {
                    Perf.Stop(Perf.Capture, start);
                    _toolApplied = false;
                    _toolCost = 0;
                    _toolOriginals.Clear();
                }
                long global = Perf.Start();
                if (Features.GlobalState.IsAvailable) CaptureGlobal(now);
                Perf.Stop(Perf.Global, global);
                _authoredThisFrame.Clear();
                _forceCapture.Clear();
                if (IsLiveClient && Features.Replication.IsAvailable) SendApplyAck(now);
            }
            Perf.Counters.EndFrame(now);
        }

        /// <summary>
        /// Global state (<see cref="GlobalStateSync"/>), in a batch of its own after the entity batch (so names of new
        /// entities resolve): the host's changed channels to everyone, a client's UI edits to the host.
        /// </summary>
        private void CaptureGlobal(double now)
        {
            try
            {
                _globalBatch.Reset();
                if (IsHost)
                {
                    // gameplay branch: executors that need EndFrameBarrier run here (after AllowBarrier<EndFrameBarrier>)
                    SafeCall("late intents", _actions.RunLate);
                    _global.HostCapture(_globalBatch);
                    _actions.HostCapture(_globalBatch, now);
                }
                else if (IsLiveClient)
                {
                    _global.ClientCapture(_globalBatch);
                    _actions.ClientFlush(_globalBatch);
                }
                if (_globalBatch.IsEmpty) return;
                if (IsHost)
                {
                    Broadcast(_globalBatch, Perf.GlobalBytes);
                    return;
                }
                ArraySegment<byte> bytes = _globalBatch.Finish();
                _sessions.Client?.SendToHost(MessageKind.Proposal, bytes.Array, bytes.Offset, bytes.Count);
                Perf.Counters.AddBytes(Perf.ProposalBytes, bytes.Count, now);
            }
            catch (Exception ex)
            {
                Guard.Report(Features.GlobalState, "global state capture", ex);
            }
        }

        /// <summary>Client: tells the host how many of its epoch messages were applied (in order with the proposals).</summary>
        private void SendApplyAck(double now)
        {
            SessionClient client = _sessions.Client;
            if (client == null) return;
            long applied = client.ApplyAcks.AppliedThrough;
            if (applied == _lastAckSent || now - _lastAckTime < AckInterval) return;
            _lastAckSent = applied;
            _lastAckTime = now;
            _ackWriter.Reset();
            ApplyAckMessage.Write(_ackWriter, applied);
            client.SendToHost(MessageKind.ApplyAck, _ackWriter.Buffer, 0, _ackWriter.Length);
        }

        private void CaptureHost(double now, double dt)
        {
            EntityManager em = _codec.EntityManager;
            _batch.Reset();
            _writtenThisFrame.Clear();
            // mod compatibility: mod data entities that replicated values reference are bound and sent with them
            _codec.DependencyBinder = HostBindDependency;
            // ids unbound without a despawn on the wire (their entity vanished without a Deleted tag): despawn them now,
            // then they may enter the quarantine
            _scratchIds.Clear();
            if (_ids.TakePendingDespawns(_scratchIds))
            {
                foreach (uint id in _scratchIds)
                {
                    _batch.Despawn(id);
                    _shadow.Remove(id);
                    _events.Forget(id); // w3/audit
                    _ledger.Forget(id);
                    DropDeferred(id, DeferredClass.Upsert);
                    _ids.Ids.Release(id);
                }
            }
            if (!_changed.IsEmptyIgnoreFilter)
            {
                NativeArray<ArchetypeChunk> chunks = _changed.ToArchetypeChunkArray(Allocator.Temp);
                try
                {
                    EntityTypeHandle entityType = em.GetEntityTypeHandle();
                    foreach (ArchetypeChunk chunk in chunks)
                    {
                        bool deleted = chunk.Has<Game.Common.Deleted>();
                        // w2/ids perf pass: one classification per chunk; whole chunks of derived or agent families
                        // (lanes, movers, citizens, icons, ...) are skipped without touching their entities
                        bool known = _codec.Families.TryClassifyArchetype(chunk.Archetype, out Family chunkFamily);
                        bool subElements = _subElementFrame && deleted && (chunkFamily == Family.Lane || chunkFamily == Family.PrefabSubObject);
                        if (known && chunkFamily != Family.ZoneBlock && !subElements && !FamilyClassifier.IsHostReplicated(chunkFamily)) continue;
                        bool created = chunk.Has<Game.Common.Created>();
                        bool hasPrefab = chunk.Has<PrefabRef>();
                        NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                        for (int i = 0; i < entities.Length; i++)
                        {
                            Entity e = entities[i];
                            Family family = known ? chunkFamily : _codec.Families.Classify(em, e);
                            CaptureHostEntity(em, e, family, deleted, created, hasPrefab);
                        }
                    }
                }
                finally
                {
                    chunks.Dispose();
                }
            }
            // w3/audit: components the simulation added or removed without a tag (fires, damage, floods, accident sites)
            if (Features.Events.IsAvailable)
            {
                long watch = Perf.Start();
                try
                {
                    _events.HostCollect(now, _forceCapture, _scratchValues);
                    foreach (uint id in _scratchValues) MarkDeferred(id, DeferredClass.Values, now); // tree growth stages
                    _scratchValues.Clear();
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Events, "presence watch", ex);
                }
                Perf.Stop(Perf.PresenceWatch, watch);
            }
            // gameplay branch: entities that intents or the host's own UI changed without any tag
            foreach (Entity e in _forceCapture)
            {
                if (_writtenThisFrame.Contains(e) || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                Family family = _codec.Families.Classify(em, e);
                if (FamilyClassifier.IsReplicated(family)) WriteHostEntity(em, e, family, _ids.GetNetId(e), em.HasComponent<Game.Common.Created>(e), em.HasComponent<PrefabRef>(e));
            }
            // w3/agents: the agent scan slice, key-link changes and the co-captured owners of this frame's agent changes
            _agents.HostCapture(_batch, _writtenThisFrame, _writeAgent ?? (_writeAgent = WriteAgentRecord));
            // mod compatibility: the dependencies all of the above needed, untagged mod state, and its dependencies
            WriteDependencies(_writtenThisFrame);
            SafeCall("mod state capture", () => CaptureModChanges(now, _writtenThisFrame, true));
            WriteDependencies(_writtenThisFrame);
            _codec.DependencyBinder = null;
            SafeCall("mod settings capture", () => _modSettings.HostCapture(_batch, now));
            long scan = Perf.Start();
            ScanValues(now);
            Perf.Stop(Perf.Scan, scan);
            if (now - _lastClock >= ClockInterval)
            {
                _lastClock = now;
                _batch.Clock(_simulation.frameIndex);
            }
            long integrity = Perf.Start();
            _digestBatch.Reset();
            if (Features.Integrity.IsAvailable)
            {
                try
                {
                    Integrity.HostCapture(now, _batch, _digestBatch, _sessions.Host?.LivePeerCount ?? 0); // feat/integrity: icons and line rows; digests per peer
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Integrity, "integrity (host capture)", ex);
                }
            }
            Perf.Stop(Perf.Integrity, integrity);
            if (!_batch.IsEmpty)
            {
                // gameplay branch: the ledger epoch goes last, so the first-record routing of batches is unchanged
                _batch.Epoch(_ledger.Advance());
                Broadcast(_batch, Perf.StructureBytes);
            }
            long deferred = Perf.Start();
            FlushDeferred(now, dt);
            Perf.Stop(Perf.Deferred, deferred);
            _agents.HostStream(now); // w3/agents: owner buffers, then each peer's agents by attention (after the batch)
        }

        private AgentSync.RecordWriter _writeAgent;

        private void CaptureHostEntity(EntityManager em, Entity e, Family family, bool deleted, bool created, bool hasPrefab)
        {
            if (family == Family.ZoneBlock)
            {
                if (!deleted) WriteZoneBlock(_batch, e, false);
                return;
            }
            if (_subElementFrame && deleted && (family == Family.PrefabSubObject || family == Family.Lane))
            {
                _actions.NoteSubElementDeleted(e); // gameplay branch: replayed on clients by key
                return;
            }
            if (family == Family.Agent)
            {
                if (FamilyClassifier.AgentsReplicated) CaptureHostAgent(em, e, deleted, created, hasPrefab); // w3/agents
                return;
            }
            if (!FamilyClassifier.IsReplicated(family)) return;
            uint id = _ids.GetNetId(e);
            if (deleted)
            {
                if (id == 0) return;
                if (family == Family.Object) _agents.OnOwnerDespawned(em, e); // w3/agents: its renters, workers, students
                _batch.Despawn(id);
                _ids.Unbind(id, despawnSent: true);
                _shadow.Remove(id);
                _events.Forget(id); // w3/audit
                _ledger.Forget(id);
                DropDeferred(id, DeferredClass.Upsert);
                return;
            }
            WriteHostEntity(em, e, family, id, created, hasPrefab);
        }

        /// <summary>
        /// w3/agents: births, arrivals and new companies are spawned (their owners' reverse buffers follow in the same batch,
        /// <see cref="AgentSync"/>), deaths and departures despawned; any other tag only moves the agent to the front of the
        /// value scan (agents change all the time, their values are budgeted per peer).
        /// </summary>
        private void CaptureHostAgent(EntityManager em, Entity e, bool deleted, bool created, bool hasPrefab)
        {
            uint id = _ids.GetNetId(e);
            if (deleted)
            {
                if (id == 0) return;
                _agents.OnDespawned(id, e);
                _batch.Despawn(id);
                _ids.Unbind(id, despawnSent: true);
                _ledger.Forget(id);
                DropDeferred(id, DeferredClass.Upsert);
                return;
            }
            if (created || id == 0)
            {
                if (_writtenThisFrame.Contains(e)) return;
                WriteHostEntity(em, e, Family.Agent, id, true, hasPrefab);
                uint bound = _ids.GetNetId(e);
                if (bound != 0) _agents.OnSpawned(bound, e);
                return;
            }
            _agents.Touch(id);
        }

        /// <summary>w3/agents: an agent's full record into the frame's broadcast batch (key-link changes, co-captured owners).</summary>
        private void WriteAgentRecord(Entity e, uint id, SyncRecordKind kind)
        {
            EntityManager em = _codec.EntityManager;
            if (em.HasComponent<PrefabRef>(e)) _codec.EnsurePrefab(em.GetComponentData<PrefabRef>(e).m_Prefab, _batch);
            _codec.WriteEntity(_batch, kind, id, Family.Agent, e);
            _writtenThisFrame.Add(e);
            DropDeferred(id, DeferredClass.Upsert);
        }

        private void WriteHostEntity(EntityManager em, Entity e, Family family, uint id, bool created, bool hasPrefab)
        {
            SyncRecordKind kind = created ? SyncRecordKind.Spawn : SyncRecordKind.Upsert;
            if (id == 0)
            {
                id = _ids.BindNew(e, family);
                if (id == 0) return;
                kind = SyncRecordKind.Spawn;
            }
            if (hasPrefab) _codec.EnsurePrefab(em.GetComponentData<PrefabRef>(e).m_Prefab, _batch);
            _codec.WriteEntity(_batch, kind, id, family, e);
            _writtenThisFrame.Add(e);
            DropDeferred(id, DeferredClass.Upsert); // the full record supersedes waiting values and re-sends
            if (FamilyClassifier.IsScanned(family)) _shadow[id] = _codec.HashEntity(e, true, family);
            _events.NoteWritten(em, id, e, family); // w3/audit: the component set clients now hold
            // gameplay branch: authored changes (committed proposals and intents, the host player's tool) for conflicts
            if (_authoredThisFrame.TryGetValue(e, out byte author)) _ledger.MarkAuthored(id, author);
            else if (em.HasComponent<Game.Common.Applied>(e) || _toolOriginals.Contains(e)) _ledger.MarkAuthored(id, PeerId.Host.Value);
        }

        /// <summary>
        /// Values that change without update tags (line paths and states, city money): a rolling hash comparison over the
        /// scanned families (their own index set, not the whole id table), budgeted in milliseconds per frame. A change
        /// marks the entity in every peer's deferred queue; the current value is serialised when it is sent.
        /// </summary>
        private void ScanValues(double now)
        {
            if (now - _lastScan < ScanInterval && _scanCursor == 0) return;
            EntityManager em = _codec.EntityManager;
            if (_scanCursor == 0)
            {
                _lastScan = now;
                _scanIds.Clear();
                _ids.ScannedIds.CopyTo(_scanIds);
                _scanCity = now - _lastCity >= CityInterval;
                if (_scanCity) _lastCity = now;
            }
            long start = Stopwatch.GetTimestamp();
            long budget = (long)(ScanBudgetMs * Stopwatch.Frequency / 1000.0);
            int n = 0;
            while (_scanCursor < _scanIds.Count)
            {
                uint id = _scanIds[_scanCursor++];
                Family family = _ids.GetFamily(id);
                if (family == Family.Singleton ? !_scanCity : !FamilyClassifier.IsScanned(family)) continue;
                Entity e = _ids.GetEntity(id);
                if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                n++;
                ulong hash = _codec.HashEntity(e, true, family);
                bool known = _shadow.TryGetValue(id, out ulong old);
                if (!known || old != hash)
                {
                    _shadow[id] = hash;
                    if (known || family == Family.Singleton) MarkDeferred(id, DeferredClass.Values, now); // first sight: baseline only
                }
                if (n >= MinScanPerFrame && (n & 7) == 0 && Stopwatch.GetTimestamp() - start > budget) break;
            }
            if (_scanCursor >= _scanIds.Count) _scanCursor = 0;
        }

        /// <summary>Marks an id in every peer's deferred queue (joiners included: they get it once live).</summary>
        private void MarkDeferred(uint id, DeferredClass cls, double now)
        {
            for (int i = 0; i < _outList.Count; i++) _outList[i].Queue.Mark(id, cls, now);
        }

        /// <summary>Drops waiting entries of an id (it was despawned, or a full record just went to everyone).</summary>
        private void DropDeferred(uint id, DeferredClass atMost)
        {
            for (int i = 0; i < _outList.Count; i++) _outList[i].Queue.RemoveIfAtMost(id, atMost);
        }

        /// <summary>
        /// Host: sends each live peer as many deferred records as its link takes this frame (ARCH §4.8.4, simplified),
        /// oldest first, serialised from the current state; then the integrity digests to the peers that are up to date
        /// (a digest is only comparable once everything it covers was sent).
        /// </summary>
        private void FlushDeferred(double now, double dt)
        {
            SessionHost host = _sessions.Host;
            if (host == null || _outList.Count == 0) return;
            EntityManager em = _codec.EntityManager;
            long start = Stopwatch.GetTimestamp();
            long cpuBudget = (long)(DeferredBudgetMs * Stopwatch.Frequency / 1000.0);
            for (int p = 0; p < _outList.Count; p++)
            {
                PeerOut o = _outList[p];
                if (!host.TryGetLink(o.Peer, out PeerSendState link) || !link.Live) continue;
                // polish: a re-send the request limit deferred is served once its bucket refilled
                if (_requests.HasDeferred(o.Peer.Value) && _requests.TakeDeferred(o.Peer.Value, _sessions.Clock.Seconds, out bool routesOnly)) Resend(o.Peer, routesOnly);
                if (o.Queue.Count > 0 && Stopwatch.GetTimestamp() - start <= cpuBudget)
                {
                    int bytes = LinkBudget.DeferredBytes(link.QueuedReliableBytes, link.PendingReliableBytes, link.SendRateBytesPerSecond, dt, _linkConfig);
                    if (bytes > 0) FlushPeer(em, o, bytes, start, cpuBudget);
                }
                if (!_digestBatch.IsEmpty && o.Queue.Count == 0) Send(o.Peer, _digestBatch, Perf.IntegrityBytes);
                else if (!_digestBatch.IsEmpty) Perf.Counters.Count("digests withheld (peer behind)");
            }
        }

        private void FlushPeer(EntityManager em, PeerOut o, int byteBudget, long start, long cpuBudget)
        {
            _peerBatch.Reset();
            int baseLength = _peerBatch.Length;
            while (_peerBatch.Length - baseLength < byteBudget && _peerBatch.RecordCount < ResendChunkRecords)
            {
                if ((_peerBatch.RecordCount & 7) == 7 && Stopwatch.GetTimestamp() - start > cpuBudget) break;
                if (!o.Queue.TryTake(out uint id, out DeferredClass cls, out _)) break;
                Entity e = _ids.GetEntity(id);
                if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                Family family = _ids.GetFamily(id);
                try
                {
                    if (cls == DeferredClass.Upsert && em.HasComponent<PrefabRef>(e)) _codec.EnsurePrefab(em.GetComponentData<PrefabRef>(e).m_Prefab, _peerBatch);
                    _codec.WriteEntity(_peerBatch, cls == DeferredClass.Upsert ? SyncRecordKind.Upsert : SyncRecordKind.Values, id, family, e);
                }
                catch (Exception ex)
                {
                    _log.Debug(LogCategory.Replication, "deferred record #" + id + " failed: " + ex.Message);
                }
            }
            if (_peerBatch.IsEmpty) return;
            o.SentRecords += _peerBatch.RecordCount;
            o.LastFlush = Perf.Now;
            Send(o.Peer, _peerBatch, Perf.DeferredBytes);
        }

        /// <summary>A client asked for the full state of every line or every entity: queued as deferred upserts (coalesced, link-paced).</summary>
        private void Resend(PeerId peer, bool routesOnly)
        {
            if (!_out.TryGetValue(peer.Value, out PeerOut o)) return;
            EntityManager em = _codec.EntityManager;
            NativeArray<Entity> table = _ids.ReaderTable;
            double now = Perf.Now;
            int n = 0;
            for (int id = 1; id < table.Length; id++)
            {
                Entity e = table[id];
                if (e == Entity.Null) continue;
                Family f = _ids.GetFamily((uint)id);
                if (f == Family.Prefab) continue;
                if (routesOnly ? !(f == Family.Route || f == Family.Waypoint || f == Family.Segment) : !FamilyClassifier.IsHostReplicated(f)) continue;
                if (!em.Exists(e)) continue;
                o.Queue.Mark((uint)id, DeferredClass.Upsert, now);
                n++;
            }
            _log.Info(LogCategory.Replication, peer + ": re-sending " + n + (routesOnly ? " line entities" : " entities") + " (paced by its link)");
        }

        /// <summary>
        /// gameplay branch: a deletion the vanilla UI makes outside any tool (R10 D1-D3: <c>ActionsSection.OnDelete</c>
        /// for districts, service upgrades and lines, <c>UpgradesSection.OnDelete</c>, the transport overview's delete).
        /// Entities removed together with their owner are the owner's business.
        /// </summary>
        private static bool IsUiDeletion(EntityManager em, Entity e, Family family)
        {
            if (em.HasComponent<Game.Common.Owner>(e))
            {
                Entity owner = em.GetComponentData<Game.Common.Owner>(e).m_Owner;
                if (owner != Entity.Null && em.Exists(owner) && em.HasComponent<Game.Common.Deleted>(owner)) return false;
            }
            switch (family)
            {
                case Family.Route: return em.HasComponent<Game.Routes.TransportLine>(e) || em.HasComponent<Game.Routes.WorkRoute>(e);
                case Family.Area: return em.HasComponent<Game.Areas.District>(e);
                case Family.Object: return em.HasComponent<Game.Buildings.ServiceUpgrade>(e);
                default: return false;
            }
        }

        private void CaptureClient(double now)
        {
            EntityManager em = _codec.EntityManager;
            UpdateRouteWindow(now);
            // entities the applier wrote this frame: remember their converged state, never propose them back
            foreach (Entity e in _appliedThisFrame)
            {
                if (!em.Exists(e)) continue;
                uint id = _ids.GetNetId(e);
                if (id != 0) _shadow[id] = _codec.HashEntity(e, false);
                _watcher?.Refresh(e); // mod compatibility: the applier's writes are not the player's edits
            }
            // vanilla tools always go through ApplyTool (captured via _toolApplied); only tools from other mods may
            // write the world directly (Move It), so only those make un-applied changes count as the player's edits
            // mod compatibility: mod tools that apply through the Temp pipeline (Traffic, Line Tool, Find It's picker) are not
            // direct-write tools; their applies arrive through ApplyTool like vanilla ones
            bool localTool = _tools.activeTool != null && !(_tools.activeTool is DefaultToolSystem)
                             && _tools.activeTool.GetType().Assembly != typeof(DefaultToolSystem).Assembly
                             && !Compat.ModCompat.IsPipelineTool(_tools.activeTool.GetType());
            bool toolPath = _toolApplied || localTool;
            // gameplay branch: deletions from the UI (district, service upgrade, line: R10 D1-D3) happen outside any
            // tool; outside a tool path only those are proposed, every other local deletion waits for the host
            ProposalFlags flags = _toolApplied ? ProposalFlags.ToolApply : localTool ? ProposalFlags.DirectTool : ProposalFlags.None;
            _batch.Reset();
            HashSet<Entity> written = _clientWritten;
            List<uint> proposed = _clientProposed;
            written.Clear();
            proposed.Clear();
            // mod compatibility: mod data entities the proposed values reference get ids from the pool and travel along
            _codec.DependencyBinder = ClientBindDependency;
            // perf pass: outside a tool path only deletions count, so whole chunks without Deleted are skipped instead of
            // copying and testing every tagged entity of the frame (the simulation and the applier tag many each frame)
            List<Entity> changed = _clientChanged;
            changed.Clear();
            if (!_changed.IsEmptyIgnoreFilter)
            {
                NativeArray<ArchetypeChunk> chunks = _changed.ToArchetypeChunkArray(Allocator.Temp);
                try
                {
                    EntityTypeHandle entityType = em.GetEntityTypeHandle();
                    foreach (ArchetypeChunk chunk in chunks)
                    {
                        if (!toolPath && !chunk.Has<Game.Common.Deleted>()) continue;
                        NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                        for (int i = 0; i < entities.Length; i++) changed.Add(entities[i]);
                    }
                }
                finally
                {
                    chunks.Dispose();
                }
            }
            try
            {
                foreach (Entity e in changed)
                {
                    if (_appliedThisFrame.Contains(e)) continue;
                    if (!toolPath && !em.HasComponent<Game.Common.Deleted>(e)) continue;
                    Family family = _codec.Families.Classify(em, e);
                    // mod compatibility: vehicles, cims and animals a mod tool deleted (Better Bulldozer)
                    if (family == Family.Mover && localTool && em.HasComponent<Game.Common.Deleted>(e))
                    {
                        uint mover = _ids.GetNetId(e);
                        if (mover != 0 && _ids.GetFamily(mover) == Family.Mover) _batch.Despawn(mover);
                        continue;
                    }
                    if (family == Family.ZoneBlock)
                    {
                        if (_toolApplied && !em.HasComponent<Game.Common.Deleted>(e)) WriteZoneBlock(_batch, e, false);
                        continue;
                    }
                    if (_subElementFrame && (family == Family.PrefabSubObject || family == Family.Lane) && em.HasComponent<Game.Common.Deleted>(e))
                    {
                        _actions.NoteSubElementDeleted(e); // gameplay branch: sent as derived keys (intent)
                        continue;
                    }
                    if (!FamilyClassifier.IsReplicated(family)) continue;
                    // w3/audit: events, chirps and journal entries are the host simulation's alone (a local like is per player)
                    if (FamilyClassifier.IsHostOnly(family)) continue;
                    uint id = _ids.GetNetId(e);
                    if (em.HasComponent<Game.Common.Deleted>(e))
                    {
                        if (id == 0) continue;
                        if (!toolPath)
                        {
                            if (!IsUiDeletion(em, e, family)) continue;
                            flags |= ProposalFlags.UiDelete;
                        }
                        _batch.Despawn(id);
                        _ids.Unbind(id);
                        _shadow.Remove(id);
                        continue;
                    }
                    bool authored = em.HasComponent<Game.Common.Applied>(e) || _toolOriginals.Contains(e) || (localTool && !_toolApplied);
                    if (!authored) continue;
                    SyncRecordKind kind = SyncRecordKind.Upsert;
                    ulong hash = _codec.HashEntity(e, false);
                    if (id == 0)
                    {
                        if (!_pool.TryTake(out id))
                        {
                            _log.Warn(LogCategory.Replication, "out of net ids; an edit could not be sent (it will be corrected by the host)");
                            continue;
                        }
                        _ids.Bind(e, id, family);
                        kind = SyncRecordKind.Spawn;
                    }
                    else if (_shadow.TryGetValue(id, out ulong old) && old == hash)
                    {
                        continue;
                    }
                    _shadow[id] = hash;
                    _codec.WriteEntity(_batch, kind, id, family, e);
                    written.Add(e);
                    proposed.Add(id);
                }
            }
            finally
            {
                changed.Clear();
            }
            // mod compatibility: untagged mod state the player changed (components tool, info-panel toggles, Better
            // Bulldozer's removal records), then the dependencies of everything proposed
            WriteDependencies(written, proposed);
            int before = proposed.Count;
            SafeCall("mod state capture", () => CaptureModChanges(now, written, false, proposed));
            if (proposed.Count > before) flags |= ProposalFlags.DirectTool;
            WriteDependencies(written, proposed);
            _codec.DependencyBinder = null;
            // a direct-write tool's edits are held against the host's echoes while the player keeps editing
            if ((localTool && !_toolApplied) || proposed.Count > before)
            {
                foreach (uint id in proposed)
                {
                    _holds.TryGetValue(id, out EditHold hold);
                    hold.Until = now + HoldSeconds;
                    _holds[id] = hold;
                }
            }
            if (_toolCost != 0) _batch.Cost(_toolCost);
            // gameplay branch: sequence, base ledger epoch and kind of the proposal (host validation, rollback)
            if (!_batch.IsEmpty) _batch.ProposalHeader(++_seq, _baseEpoch, flags);
            if (_pool.ShouldRequestMore(_sessions.Clock.Seconds)) _batch.Request(SyncRequest.MoreIds); // polish: retried with backoff
            if (!_batch.IsEmpty)
            {
                ArraySegment<byte> bytes = _batch.Finish();
                _sessions.Client?.SendToHost(MessageKind.Proposal, bytes.Array, bytes.Offset, bytes.Count);
                Perf.Counters.AddBytes(Perf.ProposalBytes, bytes.Count, now);
            }
        }

        /// <summary>
        /// Route-tool preview window (ARCH §4.11.4, simplified): while the player draws or edits a line, the local
        /// route path and waypoint systems run so the preview follows the roads; afterwards the host's line state is
        /// requested again, so no locally derived line state survives.
        /// </summary>
        /// <summary>
        /// polish: opens the route preview window in the frame the route tool is selected (<c>ToolSystem.activeTool</c>
        /// raises <c>EventToolChanged</c>), not at that frame's end capture, so the temp segments the tool creates in its
        /// first ToolUpdate already get their road-following path from <c>RoutePathSystem</c> (an <c>Updated</c> tag is
        /// seen once only). Closing stays with <see cref="UpdateRouteWindow"/> (linger, host line re-send).
        /// </summary>
        private void OnToolChanged(ToolBaseSystem tool)
        {
            try
            {
                if (!(tool is RouteToolSystem) || !IsLiveClient || _routeWindowOpen) return;
                _routeWindowUntil = Perf.Now + RouteWindowLinger; // the capture clock (SyncSystems: realtimeSinceStartup)
                _routeWindowOpen = true;
                _gate.SetRouteWindow(true);
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Gate, "route preview window on tool change: " + ex.Message);
            }
        }

        private void UpdateRouteWindow(double now)
        {
            bool routeTool = _tools.activeTool is RouteToolSystem;
            if (routeTool) _routeWindowUntil = now + RouteWindowLinger;
            bool open = now < _routeWindowUntil;
            if (open == _routeWindowOpen) return;
            _routeWindowOpen = open;
            _gate.SetRouteWindow(open);
            if (!open)
            {
                var w = new SyncBatch.Writer();
                w.Request(SyncRequest.ResendRoutes);
                ArraySegment<byte> bytes = w.Finish();
                _sessions.Client?.SendToHost(MessageKind.Proposal, bytes.Array, bytes.Offset, bytes.Count);
            }
        }

        // ================================================================== zone cells (keyed state on derived blocks)

        /// <summary>Zone cells of a block as a keyed record; <paramref name="force"/> also writes an empty fresh block (rollback).</summary>
        private void WriteZoneBlock(SyncBatch.Writer w, Entity block, bool force)
        {
            EntityManager em = _codec.EntityManager;
            if (!em.HasComponent<Game.Common.Owner>(block) || !em.HasComponent<Game.Zones.Block>(block) || !em.HasBuffer<Game.Zones.Cell>(block)) return;
            uint owner = _ids.GetNetId(em.GetComponentData<Game.Common.Owner>(block).m_Owner);
            if (owner == 0) return;
            DynamicBuffer<Game.Zones.Cell> cells = em.GetBuffer<Game.Zones.Cell>(block, true);
            var zones = new uint[cells.Length];
            bool any = false;
            for (int i = 0; i < cells.Length; i++)
            {
                Game.Zones.ZoneType z = cells[i].m_Zone;
                if (z.Equals(Game.Zones.ZoneType.None)) continue;
                Entity prefab = _zones.GetPrefab(z);
                // review fix: only the host allocates ids. A client's EnsurePrefab allocated from its local counter
                // (the host's high-water mark at snapshot time), which the host has long handed to new entities and
                // movers since; the host then rebound that id to the zone prefab and lost the entity's binding
                zones[i] = IsHost ? _codec.EnsurePrefab(prefab, w) : _ids.GetNetId(prefab);
                any |= zones[i] != 0;
            }
            if (!any && !force && em.HasComponent<Game.Common.Created>(block)) return; // a fresh empty block: nothing to tell
            w.ZoneCells(BlockKey(owner, em.GetComponentData<Game.Zones.Block>(block)), zones);
        }

        private static ZoneBlockKey BlockKey(uint owner, Game.Zones.Block b)
        {
            return new ZoneBlockKey
            {
                OwnerNetId = owner,
                X = ZoneBlockKey.Quantize(b.m_Position.x, 64f),
                Z = ZoneBlockKey.Quantize(b.m_Position.z, 64f),
                DirX = ZoneBlockKey.Quantize(b.m_Direction.x, 1024f),
                DirZ = ZoneBlockKey.Quantize(b.m_Direction.y, 1024f),
                SizeX = b.m_Size.x,
                SizeY = b.m_Size.y,
            };
        }

        private void ApplyZoneRecords(List<SyncRecord> records)
        {
            double now = _sessions.Clock.Seconds;
            bool fromHost = !IsHost;
            for (int i = 0; i < records.Count; i++)
            {
                SyncRecord r = records[i];
                // gameplay branch: the host's ledger epochs (conflict base) and proposal results (rollback toast)
                if (fromHost && r.Kind == SyncRecordKind.Epoch)
                {
                    _baseEpoch = r.EpochNumber;
                    continue;
                }
                if (fromHost && r.Kind == SyncRecordKind.ProposalResult)
                {
                    OnProposalResult(r);
                    continue;
                }
                if (fromHost && r.Kind == SyncRecordKind.ModSettings)
                {
                    SafeCall("mod settings", () => _modSettings.Apply(r)); // mod compatibility: the host's rules
                    continue;
                }
                if (r.Kind != SyncRecordKind.ZoneCells) continue;
                if (!TryApplyZone(r.Block, r.ZoneIds)) _pendingZones.Add(new PendingZone { Key = r.Block, Zones = r.ZoneIds, Until = now + 3.0 });
            }
        }

        /// <summary>Client: the host refused one of this player's proposals; the resend in the same batch rolled it back.</summary>
        private void OnProposalResult(SyncRecord r)
        {
            if (r.Reason == RejectReason.None) return;
            string text = "The host undid your last change: " + ProposalRules.Describe(r.Reason) + ".";
            _log.Info(LogCategory.Replication, "proposal " + r.Seq + " rejected (" + r.Reason + ")");
            double now = _sessions.Clock.Seconds;
            // one toast per reason and second, however many proposals were in flight
            if (r.Reason == _lastToastReason && now - _lastToast < 1.0) return;
            _lastToastReason = r.Reason;
            _lastToast = now;
            SessionManager.Notify(text);
        }

        private RejectReason _lastToastReason;
        private double _lastToast = double.NegativeInfinity;

        private void RetryZones()
        {
            if (_pendingZones.Count == 0) return;
            double now = _sessions.Clock.Seconds;
            for (int i = _pendingZones.Count - 1; i >= 0; i--)
            {
                PendingZone p = _pendingZones[i];
                if (TryApplyZone(p.Key, p.Zones)) _pendingZones.RemoveAt(i);
                else if (now > p.Until)
                {
                    _log.Debug(LogCategory.Replication, "zone block of #" + p.Key.OwnerNetId + " not found; dropped");
                    _pendingZones.RemoveAt(i);
                }
            }
        }

        private bool TryApplyZone(ZoneBlockKey key, uint[] zones)
        {
            EntityManager em = _codec.EntityManager;
            Entity best = FindBlock(key);
            if (best == Entity.Null || !em.HasBuffer<Game.Zones.Cell>(best)) return false;
            DynamicBuffer<Game.Zones.Cell> cells = em.GetBuffer<Game.Zones.Cell>(best);
            if (cells.Length != zones.Length) return false;
            for (int i = 0; i < cells.Length; i++)
            {
                Game.Zones.Cell c = cells[i];
                Game.Zones.ZoneType z = Game.Zones.ZoneType.None;
                if (zones[i] != 0)
                {
                    Entity prefab = _ids.GetEntity(zones[i]);
                    if (prefab != Entity.Null && em.HasComponent<ZoneData>(prefab)) z = em.GetComponentData<ZoneData>(prefab).m_ZoneType;
                }
                c.m_Zone = z;
                cells[i] = c;
            }
            if (!em.HasComponent<Game.Common.Updated>(best)) em.AddComponent<Game.Common.Updated>(best);
            _appliedThisFrame.Add(best);
            return true;
        }

        /// <summary>The live zone block of a key: same owner edge, size and direction, nearest position within 1 m.</summary>
        private Entity FindBlock(ZoneBlockKey key)
        {
            EntityManager em = _codec.EntityManager;
            Entity owner = _ids.GetEntity(key.OwnerNetId);
            if (owner == Entity.Null || !em.Exists(owner) || !em.HasBuffer<Game.Zones.SubBlock>(owner)) return Entity.Null;
            DynamicBuffer<Game.Zones.SubBlock> blocks = em.GetBuffer<Game.Zones.SubBlock>(owner, true);
            Entity best = Entity.Null;
            long bestDistance = 64L * 64L + 1;
            for (int i = 0; i < blocks.Length; i++)
            {
                Entity b = blocks[i].m_SubBlock;
                if (!em.HasComponent<Game.Zones.Block>(b) || em.HasComponent<Game.Common.Deleted>(b)) continue;
                ZoneBlockKey k = BlockKey(key.OwnerNetId, em.GetComponentData<Game.Zones.Block>(b));
                if (k.SizeX != key.SizeX || k.SizeY != key.SizeY || Math.Abs(k.DirX - key.DirX) > 8 || Math.Abs(k.DirZ - key.DirZ) > 8) continue;
                long d = k.DistanceSquared(key);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = b;
                }
            }
            return best;
        }

        // ================================================================== mod compatibility (docs/design/MOD-COMPATIBILITY.md)

        /// <summary>
        /// Host: binds an unbound entity a replicated value references when it is itself replicated (Traffic's lane
        /// connection data, Better Bulldozer's removal records) and queues it for this batch.
        /// </summary>
        private bool HostBindDependency(Entity e)
        {
            EntityManager em = _codec.EntityManager;
            if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e) || _ids.GetNetId(e) != 0) return false;
            Family family = _codec.Families.Classify(em, e);
            if (family != Family.ModEntity && family != Family.WaterSource) return false;
            uint id = _ids.BindNew(e, family);
            if (id == 0) return false;
            _dependencies.Enqueue((e, id, family));
            return true;
        }

        /// <summary>Client: the same with an id from the host-granted pool (the host creates the entity from the proposal).</summary>
        private bool ClientBindDependency(Entity e)
        {
            EntityManager em = _codec.EntityManager;
            if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e) || _ids.GetNetId(e) != 0) return false;
            Family family = _codec.Families.Classify(em, e);
            if (family != Family.ModEntity) return false;
            if (!_pool.TryTake(out uint id)) return false;
            _ids.Bind(e, id, family);
            _dependencies.Enqueue((e, id, family));
            return true;
        }

        /// <summary>Writes the queued dependencies as spawns (their own references may queue more).</summary>
        private void WriteDependencies(HashSet<Entity> written, List<uint> proposed = null)
        {
            EntityManager em = _codec.EntityManager;
            int guard = 0;
            while (_dependencies.Count > 0 && guard++ < 100000)
            {
                (Entity e, uint id, Family family) = _dependencies.Dequeue();
                if (!em.Exists(e) || written.Contains(e)) continue;
                try
                {
                    if (IsHost && em.HasComponent<PrefabRef>(e)) _codec.EnsurePrefab(em.GetComponentData<PrefabRef>(e).m_Prefab, _batch);
                    _codec.WriteEntity(_batch, SyncRecordKind.Spawn, id, family, e);
                    written.Add(e);
                    proposed?.Add(id);
                    if (!IsHost) _shadow[id] = _codec.HashEntity(e, false);
                    _watcher?.Refresh(e);
                }
                catch (Exception ex)
                {
                    _log.Warn(LogCategory.Replication, "sending the mod data entity #" + id + " failed", ex);
                }
            }
            _dependencies.Clear();
        }

        /// <summary>
        /// Untagged mod state (<see cref="Compat.ModStateWatcher"/>): changed entities are written as upserts when their set
        /// of watched types changed, else as values. Host: unbound replicated entities are bound and spawned. Client:
        /// only bound entities the applier did not write are proposed.
        /// </summary>
        private void CaptureModChanges(double now, HashSet<Entity> written, bool host, List<uint> proposed = null)
        {
            if (_watcher == null || !_watcher.Active) return;
            EntityManager em = _codec.EntityManager;
            _modChanges.Clear();
            _watcher.Collect(now, _modChanges);
            foreach (Compat.ModStateWatcher.Change c in _modChanges)
            {
                Entity e = c.Entity;
                if (written.Contains(e) || _appliedThisFrame.Contains(e) || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                uint id = c.NetId;
                Family family;
                if (id == 0)
                {
                    if (!host) continue;
                    family = _codec.Families.Classify(em, e);
                    if (!FamilyClassifier.IsReplicated(family)) continue;
                    id = _ids.BindNew(e, family);
                    if (id == 0) continue;
                    if (em.HasComponent<PrefabRef>(e)) _codec.EnsurePrefab(em.GetComponentData<PrefabRef>(e).m_Prefab, _batch);
                    _codec.WriteEntity(_batch, SyncRecordKind.Spawn, id, family, e);
                    written.Add(e);
                    _watcher.Refresh(e);
                    continue;
                }
                family = _ids.GetFamily(id);
                if (!FamilyClassifier.IsReplicated(family)) continue;
                SyncRecordKind kind = c.PresenceChanged ? SyncRecordKind.Upsert : SyncRecordKind.Values;
                if (host && kind == SyncRecordKind.Upsert && em.HasComponent<PrefabRef>(e)) _codec.EnsurePrefab(em.GetComponentData<PrefabRef>(e).m_Prefab, _batch);
                _codec.WriteEntity(_batch, kind, id, family, e);
                written.Add(e);
                if (!host) _shadow[id] = _codec.HashEntity(e, false);
                proposed?.Add(id);
            }
        }

        /// <summary>
        /// Client, frame start: ends expired edit holds and applies the host's latest record of each released entity
        /// (only that entity's records of the stored batch).
        /// </summary>
        private void ReleaseHolds()
        {
            if (_holds.Count == 0 || _codec == null) return;
            double now = _sessions.Clock.Seconds;
            List<uint> expired = null;
            foreach (KeyValuePair<uint, EditHold> pair in _holds)
            {
                if (now >= pair.Value.Until) (expired ?? (expired = new List<uint>())).Add(pair.Key);
            }
            if (expired == null) return;
            foreach (uint id in expired)
            {
                byte[] batch = _holds[id].Batch;
                _holds.Remove(id);
                if (batch == null) continue;
                try
                {
                    uint target = id;
                    ApplyResult r = _codec.Apply(batch, 0, batch.Length, null, rec => rec.NetId != target
                        || !(rec.Kind == SyncRecordKind.Spawn || rec.Kind == SyncRecordKind.Upsert || rec.Kind == SyncRecordKind.Values || rec.Kind == SyncRecordKind.EnabledBits));
                    foreach (Entity e in r.Touched) _appliedThisFrame.Add(e);
                }
                catch (Exception ex)
                {
                    _log.Debug(LogCategory.Replication, "applying the held record of #" + id + " failed: " + ex.Message);
                }
            }
        }

        // ================================================================== sending

        private void Broadcast(SyncBatch.Writer w, string channel)
        {
            SessionHost host = _sessions.Host;
            if (host == null) return;
            ArraySegment<byte> bytes = w.Finish();
            host.Broadcast(MessageKind.Epoch, bytes.Array, bytes.Offset, bytes.Count);
            Perf.Counters.AddBytes(channel, (long)bytes.Count * Math.Max(1, _outList.Count), Perf.Now);
        }

        private void Send(PeerId peer, SyncBatch.Writer w, string channel)
        {
            SessionHost host = _sessions.Host;
            if (host == null) return;
            ArraySegment<byte> bytes = w.Finish();
            host.SendTo(peer, MessageKind.Epoch, bytes.Array, bytes.Offset, bytes.Count);
            Perf.Counters.AddBytes(channel, bytes.Count, Perf.Now);
        }
    }
}
