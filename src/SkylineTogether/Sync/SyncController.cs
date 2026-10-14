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
    /// Host-authoritative structure sync (ARCHITECTURE.md#sync-pipeline). The host captures every created, edited or
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
    /// (conflicts, ARCHITECTURE.md#host-validation-and-commit).</item>
    /// <item><b>Release frames</b> (<see cref="ApplyAckBook"/>): one per host frame, sealed at frame start, with
    /// a per-peer watermark of the <c>MessageKind.Epoch</c> messages queued so far. Clients acknowledge how many
    /// <c>Epoch</c> messages they <i>applied</i> (<see cref="MessageKind.ApplyAck"/>, in order with their proposals); a
    /// released net id is reused (<see cref="NetIdAllocator"/>) only once every peer applied the frame of its despawn
    /// and no join is in progress.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Structure is always sent; scanned values and re-sends wait per peer in a coalescing queue and are sent
    /// only while the peer's link has room (ARCHITECTURE.md#deferred-values-and-link-pacing: congestion becomes staleness, not backlog); motion yields to both.
    /// </para>
    /// </summary>
    public sealed partial class SyncController
    {
        private const double ApplyBudgetMs = 6.0;
        private const int MaxBatchesPerFrame = 64;
        /// <summary>
        /// Right after a client went live it applies the backlog that queued up during its join in big bounded
        /// slices (its own simulation is gated anyway): this budget per frame and this many batches, until the backlog is
        /// empty (the "catching up" state, <see cref="Core.Session.JoinTimeline.CatchingUp"/>).
        /// </summary>
        private const double CatchUpBudgetMs = 24.0;
        private const int CatchUpMaxBatchesPerFrame = 512;
        private const double ScanBudgetMs = 1.0;
        private const int MinScanPerFrame = 32;
        private const double DeferredBudgetMs = 1.5;
        private const double ClockInterval = 0.25;
        private const double ScanInterval = 0.25;
        private const double CityInterval = 1.0;
        private const double RouteWindowLinger = 2.0;
        private const double AckInterval = 0.1;
        private const int ResendChunkRecords = 512;
        private const int MaxRollbackRecords = 512; // full records a rollback writes in its frame

        // host: a peer's proposals and acks waiting to be applied are bounded; a peer that keeps sending
        // faster than the host applies, or whose applied frame stops advancing, is dropped with a rejoin offer
        private readonly InboundQuota _hostInboxQuota = new InboundQuota(4096, 64L << 20);
        private ReferenceAudit _audit;
        private readonly AckStallWatch _ackStall = new AckStallWatch(AckStallSeconds);
        private readonly List<(PeerId Peer, string Log, string Reason)> _kicks = new List<(PeerId, string, string)>();
        private readonly List<byte> _livePeerScratch = new List<byte>();
        private const double AckStallSeconds = 120;
        private double _lastAckStallCheck = double.NegativeInfinity;

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
            /// <summary>A full re-send in progress: the next id to mark (0: none) and the ids marked so far.</summary>
            public int ResendCursor;
            public int ResendMarked;
        }

        /// <summary>Ids a full re-send walks per frame (the whole table in one frame cost 30-60 ms in a big city).</summary>
        private const int ResendIdsPerFrame = 20000;

        private readonly SessionManager _sessions;
        private readonly ILog _log;
        private readonly Queue<Inbound> _inbox = new Queue<Inbound>();
        private readonly ClientIdPool _pool = new ClientIdPool();
        // host: failures applying one peer's proposals (rate-limited log; the peer is removed when they keep failing)
        private readonly Dictionary<byte, FeatureHealth> _proposalHealth = new Dictionary<byte, FeatureHealth>();
        private readonly Dictionary<uint, ulong> _shadow = new Dictionary<uint, ulong>();
        /// <summary>The decoded records of the batch being applied (reused; valid until the next batch is decoded).</summary>
        private readonly SyncRecordPool _decodePool = new SyncRecordPool();
        private readonly HashSet<Entity> _appliedThisFrame = new HashSet<Entity>();
        private readonly HashSet<Entity> _toolOriginals = new HashSet<Entity>();
        private readonly List<PendingZone> _pendingZones = new List<PendingZone>();
        private readonly SyncBatch.Writer _batch = new SyncBatch.Writer();
        private readonly SyncBatch.Writer _peerBatch = new SyncBatch.Writer();
        private readonly SyncBatch.Writer _digestBatch = new SyncBatch.Writer();
        // global state (taxes, budgets, fees, loan, policies, names, demand): own batch, so a peer that cannot decode a
        // global record never loses entity data
        private readonly GlobalStateSync _global;
        // events, disasters, chirps: presence watch (host) and client fix-ups
        private readonly EventSync _events;
        private readonly SignalFollower _signals; // clients derive lane signals and signal heads
        private readonly SyncBatch.Writer _globalBatch = new SyncBatch.Writer();
        private readonly ClientGate _gate;
        // feat/terrain: terrain and water sync (self-contained; they follow the session events themselves)
        private readonly TerrainSync _terrain;
        private readonly WaterSync _water;
        // simulation values by attention (+ time-aligned panels), cell maps, statistics history (self-contained)
        private readonly AttentionValues _simValues;
        private readonly CellMapSync _cellMaps;
        private readonly FlowStatusSync _flow; // electricity and water flow status for the infoviews
        private readonly StatisticsSync _statistics;
        // citizens, households, pets and companies (existence here, values and attention there)
        private readonly AgentSync _agents;
        // gameplay branch: UI intents, host validation (ARCHITECTURE.md#host-validation-and-commit), conflicts, build order
        private readonly ActionSync _actions;
        private readonly AuthoredLedger _ledger = new AuthoredLedger();
        // direct-write tools may propose every frame; the limit only stops floods (violations kick, ARCHITECTURE.md#host-validation-and-commit)
        private readonly ProposalRateLimiter _rate = new ProposalRateLimiter(120, 240);

        /// <summary>Limits of request records (more ids, full re-sends), which request-only batches carry past
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
        private readonly HashSet<Entity> _authoredKeys = new HashSet<Entity>();
        private readonly Action _hostWatchCall, _clientActionCall, _lateIntentsCall, _actionDetectCall, _editHoldsCall, _milestonesCall;
        private readonly Action _hostModStateCall, _hostModSettingsCall, _clientModStateCall;
        private double _callNow; // the capture time the cached mod-state and mod-settings calls use
        private Game.Tools.GenerateEdgesSystem _generateEdges;
        /// <summary>Client: the newest ledger epoch applied (the base of its proposals; not a release frame).</summary>
        private uint _baseEpoch;
        private uint _seq;
        private bool _subElementFrame;
        // id quarantine, grants, deferred values
        private readonly ApplyAckBook _acks = new ApplyAckBook();
        private readonly IdGrantBook _grants = new IdGrantBook();
        private readonly Dictionary<byte, PeerOut> _out = new Dictionary<byte, PeerOut>();
        private readonly List<PeerOut> _outList = new List<PeerOut>();
        private readonly List<(uint Start, uint Count)> _grantRuns = new List<(uint, uint)>();
        private readonly List<uint> _scratchValues = new List<uint>();
        private readonly List<uint> _scratchIds = new List<uint>();
        private readonly LinkBudgetConfig _linkConfig = new LinkBudgetConfig();
        private readonly FrameBudget _applyBudget = new FrameBudget(ApplyBudgetMs);
        private readonly FrameBudget _catchUpBudget = new FrameBudget(CatchUpBudgetMs);
        private readonly ByteWriter _ackWriter = new ByteWriter(16);
        private readonly Func<byte, long> _epochSent;
        private readonly Func<uint, Family, bool> _mayCreate;
        private PeerId _proposalPeer;
        private uint _barrier;
        private uint _announcedFrontier; // the id frontier last sent to the clients
        private readonly PrefabTableCache _prefabTable = new PrefabTableCache(); // the joins' prefab table, built across frames
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
            _audit = new ReferenceAudit(log);
            _global = new GlobalStateSync(log);
            _events = new EventSync(log);
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
            // a viewer's client undoes its own edits of shared state no tool gate covers (following citizens)
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
            _simValues = new AttentionValues(sessions, log);
            _cellMaps = new CellMapSync(sessions, log) { PeerTopics = _simValues.PeerTopics };
            _flow = new FlowStatusSync(sessions, log) { PeerTopics = _simValues.PeerTopics, InView = _simValues.InView, LocalTopics = () => _simValues.LocalTopics };
            _statistics = new StatisticsSync(sessions, log);
            _agents = new AgentSync(sessions, log);
            Integrity.TouchAgent = _agents.Touch; // agents of a requested bucket are scanned first
            _modSettings = new Compat.ModSettingsSync(log); // mod compatibility
            _epochSent = peer => _sessions.Host?.GetEpochSent(new PeerId(peer)) ?? 0;
            _mayCreate = MayCreate;
            // perf pass: the guarded calls of every frame use delegates made once (a lambda or method group passed to
            // SafeCall allocated a delegate, and a closure, on every call)
            _hostWatchCall = () =>
            {
                // a copy: the watch's Touch handler adds to _authoredThisFrame while it runs
                _authoredKeys.Clear();
                foreach (Entity e in _authoredThisFrame.Keys) _authoredKeys.Add(e);
                _actions.HostWatch(_authoredKeys);
            };
            _clientActionCall = () => _actions.ClientCapture(_appliedThisFrame);
            _lateIntentsCall = _actions.RunLate;
            _actionDetectCall = _actions.BeforeApply;
            _editHoldsCall = ReleaseHolds;
            _milestonesCall = _actions.AfterApply;
            _hostModStateCall = () => CaptureModChanges(_callNow, _writtenThisFrame, true);
            _hostModSettingsCall = () => _modSettings.HostCapture(_batch, _callNow);
            _clientModStateCall = () => CaptureModChanges(_callNow, _clientWritten, false, _clientProposed);
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

        /// <summary>Where a missing asset comes from and what to do about it (diagnostics).</summary>
        public string ExplainMissing(string asset) => _codec?.ExplainMissing(asset) ?? asset;

        /// <summary>feat/integrity: divergence detection and repair, host icons, line check (see <see cref="IntegritySync"/>).</summary>
        public IntegritySync Integrity { get; }

        // ---- solo testing (record/replay verdict, ARCHITECTURE.md#solo-test-record-and-replay): read-only views for the recorder and the replay ----

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
                foreach (string env in new[] { _terrain.Status, _water.Status, _simValues.Status, _cellMaps.Status, _statistics.Status, _agents.Status, _flow.Status }) // feat/terrain
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

        /// <summary>The id tables, the quarantine, the deferred queues and the perf counters (diagnostics dump).</summary>
        public void Describe(List<string> lines)
        {
            if (_ids != null) lines.Add("net ids: " + _ids.Describe());
            if (_ids != null && !IsHost) lines.Add("net ids from the host: " + _ids.Ceiling.Describe());
            if (IsHost) lines.Add(_prefabTable.Describe());
            if (IsHost) lines.Add("presence watch: " + _events.PresenceResends + " re-sends of component sets changed without a tag");
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
            if (_codec != null)
            {
                // reference safety of the applier and the client's deletion bookkeeping
                lines.Add("reference safety: " + _codec.UnresolvedSpawnsTotal + " new objects not created (owner/node/parent missing), " + _codec.UnsafeTotal
                          + " untagged or sanitized for broken net references, " + _codec.DeadReferencesTotal + " references to destroyed/deleted entities dropped; "
                          + _localDeletes.Count + " local deletions awaiting the host, " + _localDeleteSkips + " stale host records skipped, " + _cascadeUnbinds + " cascade unbinds");
                // decode-boundary validation and the client's removal clean-up
                lines.Add("input validation: " + _codec.MalformedTotal + " malformed values refused (NaN/out-of-range geometry, overruns), " + _codec.OverrunsTotal
                          + " reads past a payload stopped; removal clean-up: " + DeletionCascade.Removed + " references to despawned entities removed");
                if (!IsHost) lines.Add(_audit.Describe());
            }
            _agents.Describe(lines);
            Perf.Budget.Describe(lines); // the shared frame budget, per feature avg/p95/max
            Perf.Counters.Describe(Perf.Now, lines);
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
