using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SkylineTogether.Core;
using SkylineTogether.Core.Integrity;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Session;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// Divergence detection and repair (ARCHITECTURE.md#integrity), plus the owner of the icon channel
    /// (<see cref="IconSync"/>) and the UI-level line check (<see cref="LineCheck"/>).
    /// <para>
    /// <b>Host.</b> A rolling pass over every bound id hashes one slice per frame (≤ 0.5 ms) with
    /// <see cref="IntegrityHasher"/> and appends a <see cref="SyncRecordKind.BucketDigest"/> per completed bucket of
    /// <see cref="EntityDigest.BucketSize"/> ids (empty buckets included, so client-only ids are noticed) to the frame's
    /// broadcast batch: the digest is ordered after every record it covers. It answers client requests: per-entity hashes
    /// of a bucket, re-sends (as spawns, which create or reconcile; ids it does not have are answered with a despawn),
    /// line re-sends with all waypoints and segments, and explanations; each peer has its own rate limits.
    /// </para>
    /// <para>
    /// <b>Client.</b> Once live and settled, each digest is compared with the same bucket's local hashes right after the
    /// batch that carried it was applied. A mismatch asks for the bucket's detail; <see cref="DivergenceTracker"/>
    /// confirms, repairs and tolerates (see there for the storm guards). A tolerated entity is logged once with the
    /// components that differ (from the host's explanation).
    /// </para>
    /// <para>
    /// <b>Escalation</b> (<see cref="RepairEscalation"/>): a bucket that keeps differing after its object
    /// repairs, or differs in more objects than repairs are meant for, is rebaselined (every object of it re-sent through
    /// the same re-send request; once per bucket per 10 minutes, a few a minute at most). Massive divergence (many buckets
    /// mismatching cycle after cycle, many rebaselines, many objects given up) offers the player a resync: a rejoin with
    /// a fresh snapshot (<see cref="ResyncOffer"/>, accepted through <see cref="SessionManager.Resync"/>), never forced and
    /// rate-limited across rejoins.
    /// </para>
    /// Entry points are called by <see cref="SyncController"/>; everything runs on the main thread.
    /// </summary>
    public sealed class IntegritySync
    {
        private const double HostBudgetSeconds = 0.0005;
        private const int HostMinIdsPerFrame = 32;
        private const double ClientSettleSeconds = 10;
        private const int ClientMaxBacklogForCompare = 8;
        private const int MaxDetailsPerFrame = 4;
        private const int MaxPendingRepairs = 4096;
        private const int RecordsPerResponse = 256;
        private const double SweepInterval = 30;
        private const double ToleranceLogInterval = 60;
        /// <summary>
        /// Agents mismatch for a while after every key-link change on the host (household moves, births, deaths
        /// reach clients only when the host's rolling agent scan gets to them, up to a minute in a large city); within this
        /// window an agent mismatch is not divergence. The host also moves the agents of a requested bucket to the front of
        /// its scan (<see cref="TouchAgent"/>), so most of them converge long before.
        /// </summary>
        private const double AgentConfirmSeconds = 45;
        private const double ResendLogInterval = 10;

        private readonly SessionManager _sessions;
        private readonly ILog _log;
        private EntityCodec _codec;
        private NetIdSystem _ids;
        private IntegrityHasher _hasher;

        // ---------------------------------------------------------------- host state
        private uint _cursor = 1;
        private uint _accBucket = uint.MaxValue;
        private EntityDigest.Accumulator _acc;
        private double _cycleStart = -1;
        private double _lastCycleSeconds;
        private long _cycleEntities;
        private long _lastCycleEntities;
        private long _servedRepairs;
        private long _servedDetails;
        private long _droppedRequests;
        private readonly Dictionary<byte, PeerGates> _gates = new Dictionary<byte, PeerGates>();
        private readonly List<EntityHash> _hostList = new List<EntityHash>(EntityDigest.BucketSize);
        // the host's re-send log is a rate-limited summary (it logged one line per request, every 0.5 s)
        private readonly Dictionary<string, int> _resentByFamily = new Dictionary<string, int>();
        private readonly List<string> _resentSamples = new List<string>();
        private int _resentSinceLog;
        private double _lastResendLog = double.NegativeInfinity;

        /// <summary>
        /// Host: moves an agent to the front of the host's agent scan (its key-link changes are streamed within
        /// a frame or two). Set by the controller; called for the agents of every bucket a client asks the detail of.
        /// </summary>
        public Action<uint> TouchAgent;

        private sealed class PeerGates
        {
            public readonly RateGate Details = new RateGate(16, 32);
            public readonly RateGate Resends = new RateGate(256, 512);
            public readonly RateGate Explains = new RateGate(8, 16);
            public readonly RateGate IconSyncs = new RateGate(0.1, 2);
            /// <summary>Bucket details of this peer waiting to be hashed.</summary>
            public int PendingDetails;
        }

        // ---------------------------------------------------------------- client state
        private readonly DivergenceTracker _tracker = new DivergenceTracker();
        // rebaselines and the resync offer above the object repairs
        private readonly RepairEscalation _escalation = new RepairEscalation();
        private int _cycleCompared;
        private string _announcedOffer;
        private readonly DetailScheduler _scheduler = new DetailScheduler();
        private readonly DivergenceTracker.Outcome _outcome = new DivergenceTracker.Outcome();
        private readonly RateGate _detailGate = new RateGate(8, 16);
        private readonly RateGate _repairGate = new RateGate(128, 256);
        // explanations of entities repaired more than once (what differs), at most one per 5 s
        private readonly RateGate _repeatExplainGate = new RateGate(0.2, 4);
        private readonly HashSet<uint> _repeatExplains = new HashSet<uint>();
        private readonly List<uint> _pendingRepairs = new List<uint>();
        private readonly HashSet<uint> _pendingRepairSet = new HashSet<uint>();
        private readonly List<uint> _pendingExplains = new List<uint>();
        private readonly List<uint> _due = new List<uint>();
        private readonly List<EntityHash> _clientList = new List<EntityHash>(EntityDigest.BucketSize);
        private readonly List<ComponentHash> _members = new List<ComponentHash>(64);
        private readonly SyncBatch.Writer _requests = new SyncBatch.Writer();
        private double _liveSince = -1;
        private uint _lastDigestBucket;
        private long _verifiedThisCycle;
        private long _verifiedLastCycle = -1;
        private long _bucketMismatches;
        private double _lastSweep;
        private double _lastToleranceLog = -1000;
        private int _suppressedToleranceLogs;

        public IntegritySync(SessionManager sessions, ILog log)
        {
            _sessions = sessions;
            _log = log;
            Icons = new IconSync(log, sessions.Clock);
            Lines = new LineCheck(log);
            _tracker.ConfirmSecondsFor = id => _ids != null && _ids.GetFamily(id) == Family.Agent ? AgentConfirmSeconds : 0;
        }

        /// <summary>The icon channel (host capture, client reconciliation and the local-producer filter).</summary>
        public IconSync Icons { get; }

        /// <summary>The UI-level line check.</summary>
        public LineCheck Lines { get; }

        private bool IsHost => _sessions.Host != null;

        /// <summary>Binds to the world's codec and id tables (called when the controller has them).</summary>
        public void Attach(EntityCodec codec, NetIdSystem ids)
        {
            if (_codec == codec) return;
            _codec = codec;
            _ids = ids;
            _hasher = new IntegrityHasher(codec, _log);
            Icons.Attach(codec, ids);
            Lines.Attach(codec, ids);
        }

        // ---------------------------------------------------------------- solo testing (record/replay verdict, ARCHITECTURE.md#solo-test-record-and-replay)

        /// <summary>
        /// Client, while replaying a recording: every checkpoint record (<see cref="SyncRecordKind.ReplayHashes"/>,
        /// <see cref="SyncRecordKind.ReplayMembers"/>, <see cref="SyncRecordKind.ReplayCheckpoint"/>) and icon record of a
        /// host batch, right after the batch was applied. Null otherwise.
        /// </summary>
        public Action<SyncRecord> ReplayRecord;

        /// <summary>True once bound to the world's codec and id tables.</summary>
        public bool IsAttached => _codec != null;

        /// <summary>One past the highest net id that can be bound (the checkpoints hash every bucket below it).</summary>
        public uint IdLimit => _ids == null ? 1u : Math.Max((uint)_ids.ReaderTable.Length, _ids.Ids.Next);

        /// <summary>The integrity hashes of one bucket, exactly as the rolling verification computes them (both roles).</summary>
        public void HashBucketForReplay(uint bucket, List<EntityHash> output)
        {
            if (_codec == null)
            {
                output.Clear();
                return;
            }
            HashBucket(bucket, output);
        }

        /// <summary>The members of a bound, verified entity's integrity hash; false when it has none here.</summary>
        public bool CollectForReplay(uint id, List<ComponentHash> output, out Family family)
        {
            output.Clear();
            family = default;
            if (_codec == null) return false;
            Entity e = _ids.GetEntity(id);
            if (e == Entity.Null) return false;
            family = _ids.GetFamily(id);
            EntityManager em = _codec.EntityManager;
            if (!IntegrityHasher.IsVerified(family) || !em.Exists(e)) return false;
            _hasher.Collect(e, id, family, output);
            return true;
        }

        /// <summary>The family of a bound id ("" when it is not bound).</summary>
        public string FamilyNameOf(uint id)
        {
            if (_ids == null || _ids.GetEntity(id) == Entity.Null) return "";
            return _ids.GetFamily(id).ToString();
        }

        /// <summary>A component type's name from its hash (as the explanations name it).</summary>
        public static string TypeNameOf(ulong typeHash) => TypeName(typeHash);

        /// <summary>Forgets all session state (session ended or started).</summary>
        public void Reset()
        {
            _cursor = 1;
            _accBucket = uint.MaxValue;
            _acc.Reset();
            _cycleStart = -1;
            _cycleEntities = 0;
            _gates.Clear();
            _pendingDetails.Clear();
            _detailWriters.Clear();
            _tracker.Clear();
            _scheduler.Clear();
            _pendingRepairs.Clear();
            _pendingRepairSet.Clear();
            _pendingExplains.Clear();
            _repeatExplains.Clear();
            _resentByFamily.Clear();
            _resentSamples.Clear();
            _resentSinceLog = 0;
            _liveSince = -1;
            _verifiedThisCycle = 0;
            _verifiedLastCycle = -1;
            _lastDigestBucket = 0;
            _cycleCompared = 0;
            _bucketMismatches = 0;
            _escalation.Reset(); // keeps the resync offer history (guards hold across the rejoin of a resync)
            _announcedOffer = null;
            _hasher?.Clear();
            Icons.Reset();
            Lines.Reset();
        }

        /// <summary>One line for the panel, e.g. "verified 12,345 objects, 0 repairs".</summary>
        public string Status
        {
            get
            {
                if (_codec == null) return "";
                var sb = new StringBuilder();
                if (IsHost)
                {
                    if (_lastCycleSeconds > 0) sb.Append("verifying ").Append(N(_lastCycleEntities)).Append(" objects every ").Append(((int)Math.Round(_lastCycleSeconds)).ToString(CultureInfo.InvariantCulture)).Append(" s");
                    else sb.Append("verifying ").Append(N(_cycleEntities)).Append(" objects");
                    sb.Append(", ").Append(N(_servedRepairs)).Append(" repairs sent");
                    if (_servedDetails > 0) sb.Append(" (").Append(N(_servedDetails)).Append(" checks)");
                    if (_droppedRequests > 0) sb.Append(", ").Append(N(_droppedRequests)).Append(" requests throttled");
                }
                else if (_liveSince >= 0)
                {
                    if (_verifiedLastCycle >= 0) sb.Append("verified ").Append(N(_verifiedLastCycle)).Append(" objects");
                    else sb.Append("verifying (").Append(N(_verifiedThisCycle)).Append(" objects so far)");
                    sb.Append(", ").Append(N(_tracker.RepairsRequested)).Append(_tracker.RepairsRequested == 1 ? " repair" : " repairs");
                    int tolerated = _tracker.ToleratedCount(_sessions.Clock.Seconds);
                    if (tolerated > 0) sb.Append(", ").Append(N(tolerated)).Append(" unresolved");
                    if (_tracker.GivenUpCount > 0) sb.Append(" (").Append(N(_tracker.GivenUpCount)).Append(" given up)");
                    if (_escalation.Rebaselines > 0) sb.Append(", ").Append(N(_escalation.Rebaselines)).Append(_escalation.Rebaselines == 1 ? " area re-sent" : " areas re-sent");
                }
                else
                {
                    return "";
                }
                string icons = Icons.Status;
                if (icons.Length > 0) sb.Append("; ").Append(icons);
                string lines = Lines.Status;
                if (lines.Length > 0) sb.Append("; ").Append(lines);
                return sb.ToString();
            }
        }

        private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

        // ================================================================== host

        /// <summary>
        /// Host, at the end of the frame's capture: icon sets and line rows into the broadcast batch, the rolling digests into
        /// <paramref name="digests"/> (sent per peer after its deferred values, only to peers that are up to date).
        /// </summary>
        public void HostCapture(double now, SyncBatch.Writer batch, SyncBatch.Writer digests, int livePeers)
        {
            if (_codec == null) return;
            try
            {
                ServePendingDetails();
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Integrity, "bucket details", ex);
            }
            long icons = Diagnostics.Perf.Start();
            try
            {
                Icons.HostCapture(now, batch);
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Integrity, "icon capture", ex);
            }
            Diagnostics.Perf.Stop(Diagnostics.Perf.Icons, icons);
            try
            {
                Lines.HostCapture(now, batch);
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Integrity, "line rows", ex);
            }
            if (livePeers <= 0) return; // nobody to verify against
            try
            {
                HostDigests(now, digests);
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Integrity, "rolling digest (restarting the cycle)", ex);
                _cursor = 1;
                _accBucket = uint.MaxValue;
            }
        }

        private void HostDigests(double now, SyncBatch.Writer batch)
        {
            if (_cycleStart < 0) _cycleStart = now;
            uint end = Math.Max((uint)_ids.ReaderTable.Length, _ids.Ids.Next);
            long budget = (long)(Diagnostics.Perf.Allowance(Diagnostics.Perf.BudgetIntegrity, HostBudgetSeconds * 1000.0) / 1000.0 * Stopwatch.Frequency); // shared budget
            long start = Stopwatch.GetTimestamp();
            int n = 0;
            while (true)
            {
                if (_cursor >= end)
                {
                    if (_accBucket != uint.MaxValue) batch.BucketDigest(_accBucket, _acc.Count, _acc.Sum);
                    _accBucket = uint.MaxValue;
                    _acc.Reset();
                    _cursor = 1;
                    _lastCycleSeconds = now - _cycleStart;
                    _lastCycleEntities = _cycleEntities;
                    _cycleEntities = 0;
                    _cycleStart = now;
                    return;
                }
                uint bucket = EntityDigest.BucketOf(_cursor);
                if (bucket != _accBucket)
                {
                    if (_accBucket != uint.MaxValue) batch.BucketDigest(_accBucket, _acc.Count, _acc.Sum);
                    _acc.Reset();
                    _accBucket = bucket;
                }
                if (TryHash(_cursor, out ulong hash))
                {
                    _acc.Add(_cursor, hash);
                    _cycleEntities++;
                }
                _cursor++;
                n++;
                if (n >= HostMinIdsPerFrame && (n & 15) == 0 && Stopwatch.GetTimestamp() - start > budget) return;
            }
        }

        /// <summary>The integrity hash of a bound, live, verified entity; false when the id has none (either side).</summary>
        private bool TryHash(uint id, out ulong hash)
        {
            hash = 0;
            Entity e = _ids.GetEntity(id);
            if (e == Entity.Null) return false;
            Family family = _ids.GetFamily(id);
            if (!IntegrityHasher.IsVerified(family)) return false;
            EntityManager em = _codec.EntityManager;
            if (!em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) return false;
            try
            {
                hash = _hasher.Hash(e, id, family);
                return true;
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Integrity, "hashing #" + id + " failed: " + ex.Message);
                hash = 0xBAD;
                return true;
            }
        }

        private void HashBucket(uint bucket, List<EntityHash> output)
        {
            output.Clear();
            uint last = EntityDigest.LastId(bucket);
            for (uint id = EntityDigest.FirstId(bucket); id <= last && id != 0; id++)
            {
                if (TryHash(id, out ulong hash)) output.Add(new EntityHash(id, hash));
                if (id == uint.MaxValue) break;
            }
        }

        /// <summary>Host: a peer left.</summary>
        public void OnPeerLeft(PeerId peer) => _gates.Remove(peer.Value);

        /// <summary>Host: a <see cref="SyncRecordKind.RequestIds"/> record from a live peer (applied at frame start).</summary>
        public void OnPeerRequest(PeerId peer, SyncRecord rec)
        {
            if (_codec == null || !IsHost || rec.Ids == null) return;
            double now = _sessions.Clock.Seconds;
            if (!_gates.TryGetValue(peer.Value, out PeerGates gates)) _gates[peer.Value] = gates = new PeerGates();
            try
            {
                switch (rec.Request)
                {
                    case SyncRequest.BucketDetail:
                        ServeDetails(peer, rec.Ids, gates, now);
                        break;
                    case SyncRequest.ResendEntities:
                        ServeResends(peer, rec.Ids, gates, now, false);
                        break;
                    case SyncRequest.ResendLines:
                        ServeResends(peer, rec.Ids, gates, now, true);
                        break;
                    case SyncRequest.ExplainEntities:
                        ServeExplains(peer, rec.Ids, gates, now);
                        break;
                    case SyncRequest.IconFullSync:
                        if (gates.IconSyncs.TryTake(now)) Icons.SendFullSync(w => Send(peer, w));
                        else _droppedRequests++;
                        break;
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Integrity, peer + ": integrity request " + rec.Request, ex);
            }
        }

        /// <summary>
        /// Bucket details are queued and hashed under a frame budget (<see cref="ServePendingDetails"/>): each
        /// bucket serializes up to 128 entities, and a burst of requests hashed in one frame cost the host tens of ms.
        /// </summary>
        private void ServeDetails(PeerId peer, uint[] buckets, PeerGates gates, double now)
        {
            foreach (uint bucket in buckets)
            {
                if (!gates.Details.TryTake(now) || gates.PendingDetails >= MaxPendingDetailsPerPeer)
                {
                    _droppedRequests++;
                    continue;
                }
                gates.PendingDetails++;
                _pendingDetails.Enqueue((peer, bucket));
            }
        }

        private const int MaxPendingDetailsPerPeer = 64;
        private const double DetailBudgetMs = 1.0;
        private readonly Queue<(PeerId Peer, uint Bucket)> _pendingDetails = new Queue<(PeerId, uint)>();
        private readonly Dictionary<byte, SyncBatch.Writer> _detailWriters = new Dictionary<byte, SyncBatch.Writer>();

        /// <summary>Host, every frame: hashes queued bucket details within the budget (at least one per frame).</summary>
        private void ServePendingDetails()
        {
            if (_pendingDetails.Count == 0) return;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            int served = 0;
            while (_pendingDetails.Count > 0)
            {
                if (served > 0 && (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency > DetailBudgetMs) break;
                (PeerId peer, uint bucket) = _pendingDetails.Dequeue();
                if (!_gates.TryGetValue(peer.Value, out PeerGates gates)) continue; // the peer left
                gates.PendingDetails = Math.Max(0, gates.PendingDetails - 1);
                HashBucket(bucket, _hostList);
                if (!_detailWriters.TryGetValue(peer.Value, out SyncBatch.Writer w)) _detailWriters[peer.Value] = w = new SyncBatch.Writer();
                w.EntityHashes(bucket, _hostList);
                _servedDetails++;
                served++;
                TouchAgents(_hostList);
            }
            foreach (KeyValuePair<byte, SyncBatch.Writer> pair in _detailWriters)
            {
                if (pair.Value.IsEmpty) continue;
                Send(new PeerId(pair.Key), pair.Value);
                pair.Value.Reset();
            }
        }

        /// <summary>
        /// Host: the agents of a bucket a client found mismatching (and the members of its households) go to the
        /// front of the agent scan, so a key-link change the scan had not reached yet is streamed before the client's
        /// confirmation window ends instead of being "repaired".
        /// </summary>
        private void TouchAgents(List<EntityHash> hashes)
        {
            if (TouchAgent == null) return;
            EntityManager em = _codec.EntityManager;
            foreach (EntityHash h in hashes)
            {
                if (_ids.GetFamily(h.NetId) != Family.Agent) continue;
                TouchAgent(h.NetId);
                Entity e = _ids.GetEntity(h.NetId);
                if (e == Entity.Null || !em.Exists(e) || !em.HasBuffer<Game.Citizens.HouseholdCitizen>(e)) continue;
                DynamicBuffer<Game.Citizens.HouseholdCitizen> members = em.GetBuffer<Game.Citizens.HouseholdCitizen>(e, true);
                for (int i = 0; i < members.Length && i < 32; i++)
                {
                    uint member = _ids.GetNetId(members[i].m_Citizen);
                    if (member != 0) TouchAgent(member);
                }
            }
        }

        /// <summary>"Family 'prefab name'" of a bound entity, for the logs ("?" when it is not bound here).</summary>
        public string Describe(uint id)
        {
            if (_ids == null || _codec == null) return "?";
            Entity e = _ids.GetEntity(id);
            if (e == Entity.Null) return "not here";
            string family = _ids.GetFamily(id).ToString();
            try
            {
                EntityManager em = _codec.EntityManager;
                if (!em.Exists(e)) return family + ", destroyed";
                if (!em.HasComponent<Game.Prefabs.PrefabRef>(e)) return family;
                Entity prefab = em.GetComponentData<Game.Prefabs.PrefabRef>(e).m_Prefab;
                if (_prefabs == null) _prefabs = _codec.World.GetExistingSystemManaged<Game.Prefabs.PrefabSystem>();
                string name = _prefabs != null && prefab != Entity.Null && em.Exists(prefab) ? _prefabs.GetPrefabName(prefab) : null;
                string extra = family == "Area" && IntegrityHasher.IsSlaveArea(em, e) ? ", slave area" : "";
                return string.IsNullOrEmpty(name) ? family + extra : family + " '" + name + "'" + extra;
            }
            catch (Exception)
            {
                return family;
            }
        }

        private Game.Prefabs.PrefabSystem _prefabs;

        private void NoteResent(uint id, Family family)
        {
            string key = family.ToString();
            _resentByFamily.TryGetValue(key, out int n);
            _resentByFamily[key] = n + 1;
            _resentSinceLog++;
            if (_resentSamples.Count < 6) _resentSamples.Add("#" + id + " " + Describe(id));
        }

        private void FlushResendLog(PeerId peer, double now, bool force)
        {
            if (_resentSinceLog == 0) return;
            if (!force && now - _lastResendLog < ResendLogInterval) return;
            var parts = new List<string>();
            foreach (KeyValuePair<string, int> pair in _resentByFamily) parts.Add(pair.Key + " " + pair.Value);
            double span = double.IsNegativeInfinity(_lastResendLog) ? 0 : now - _lastResendLog;
            _log.Info(LogCategory.Integrity, peer + ": re-sent " + _resentSinceLog + " entities for repair" + (span > 0 ? " in the last " + (int)Math.Round(span) + " s" : "")
                                             + " (" + string.Join(", ", parts) + "), e.g. " + string.Join("; ", _resentSamples) + " (session: " + _servedRepairs + ")");
            _lastResendLog = now;
            _resentSinceLog = 0;
            _resentByFamily.Clear();
            _resentSamples.Clear();
        }

        private void ServeResends(PeerId peer, uint[] ids, PeerGates gates, double now, bool lines)
        {
            EntityManager em = _codec.EntityManager;
            SyncBatch.Writer w = _resendWriter; // reused: the host copies what it sends
            w.Reset();
            int sent = 0;
            foreach (uint id in ids)
            {
                if (!gates.Resends.TryTake(now))
                {
                    _droppedRequests++;
                    continue;
                }
                try
                {
                    Entity e = _ids.GetEntity(id);
                    Family family = _ids.GetFamily(id);
                    bool alive = e != Entity.Null && em.Exists(e) && !em.HasComponent<Game.Common.Deleted>(e);
                    if (!alive)
                    {
                        // the client holds an id the host does not have: it goes
                        if (family != Family.Prefab && family != Family.Singleton) w.Despawn(id);
                        continue;
                    }
                    if (!IntegrityHasher.IsVerified(family)) continue;
                    WriteResend(w, id, family, e);
                    sent++;
                    NoteResent(id, family);
                    if (lines && family == Family.Route)
                    {
                        // the whole line, so missing waypoints and segments are created on the client too
                        if (em.HasBuffer<Game.Routes.RouteWaypoint>(e))
                        {
                            DynamicBuffer<Game.Routes.RouteWaypoint> waypoints = em.GetBuffer<Game.Routes.RouteWaypoint>(e, true);
                            var list = new List<Entity>(waypoints.Length);
                            for (int i = 0; i < waypoints.Length; i++) list.Add(waypoints[i].m_Waypoint);
                            int children = WriteChildren(w, list);
                            sent += children;
                            gates.Resends.TryTake(now, Math.Min(children, 64)); // children cost tokens too (bounded per line)
                        }
                        if (em.HasBuffer<Game.Routes.RouteSegment>(e))
                        {
                            DynamicBuffer<Game.Routes.RouteSegment> segments = em.GetBuffer<Game.Routes.RouteSegment>(e, true);
                            var list = new List<Entity>(segments.Length);
                            for (int i = 0; i < segments.Length; i++) list.Add(segments[i].m_Segment);
                            int children = WriteChildren(w, list);
                            sent += children;
                            gates.Resends.TryTake(now, Math.Min(children, 64));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Diagnostics.Guard.Report(Diagnostics.Features.Integrity, peer + ": re-sending #" + id, ex);
                }
                if (w.RecordCount >= RecordsPerResponse)
                {
                    Send(peer, w);
                    w.Reset();
                }
            }
            if (!w.IsEmpty) Send(peer, w);
            _servedRepairs += sent;
            if (sent > 0)
            {
                if (lines) _log.Info(LogCategory.Integrity, peer + ": re-sent " + sent + " line entities for repair");
                else FlushResendLog(peer, now, false);
            }
        }

        private int WriteChildren(SyncBatch.Writer w, List<Entity> children)
        {
            EntityManager em = _codec.EntityManager;
            int n = 0;
            foreach (Entity child in children)
            {
                uint id = _ids.GetNetId(child);
                if (id == 0 || !em.Exists(child)) continue;
                WriteResend(w, id, _ids.GetFamily(id), child);
                n++;
            }
            return n;
        }

        private readonly SyncBatch.Writer _resendWriter = new SyncBatch.Writer();
        private readonly EncodedRecordCache _resendCache = new EncodedRecordCache();

        /// <summary>
        /// A repair record, encoded once per frame start: several peers asking for the same bucket (a rebaseline after a
        /// burst) get the same bytes instead of a serialization each.
        /// </summary>
        private void WriteResend(SyncBatch.Writer w, uint id, Family family, Entity e)
        {
            ulong key = EncodedRecordCache.Key(id, SyncRecordKind.Spawn);
            if (_resendCache.TryAppend(key, w)) return;
            int length = w.Length, records = w.RecordCount;
            _codec.WriteEntity(w, SyncRecordKind.Spawn, id, family, e);
            _resendCache.Store(key, w, length, records);
        }

        /// <summary>
        /// Host: the world may change (a new frame, or a proposal about to be applied); repair records encoded so far
        /// are stale.
        /// </summary>
        public void InvalidateResends() => _resendCache.Clear();

        private void ServeExplains(PeerId peer, uint[] ids, PeerGates gates, double now)
        {
            EntityManager em = _codec.EntityManager;
            var w = new SyncBatch.Writer();
            foreach (uint id in ids)
            {
                if (!gates.Explains.TryTake(now))
                {
                    _droppedRequests++;
                    continue;
                }
                Entity e = _ids.GetEntity(id);
                _members.Clear();
                if (e != Entity.Null && em.Exists(e) && IntegrityHasher.IsVerified(_ids.GetFamily(id))) _hasher.Collect(e, id, _ids.GetFamily(id), _members);
                w.EntityExplain(id, _members);
            }
            if (!w.IsEmpty) Send(peer, w);
        }

        private void Send(PeerId peer, SyncBatch.Writer w)
        {
            ArraySegment<byte> bytes = w.Finish();
            _sessions.Host?.SendTo(peer, MessageKind.Epoch, bytes.Array, bytes.Offset, bytes.Count);
        }

        // ================================================================== client

        /// <summary>Client: the session became live (the join snapshot is loaded).</summary>
        public void OnClientLive()
        {
            _liveSince = _sessions.Clock.Seconds;
            Icons.OnClientLive();
        }

        /// <summary>
        /// Client, right after a batch from the host was applied: presence masks, digests, details, explanations, icon
        /// sets and line rows, in batch order. <paramref name="backlog"/> = batches still queued (digests are not compared
        /// while catching up, the comparison would be stale).
        /// </summary>
        public void OnHostBatch(byte[] data, int backlog)
        {
            if (_codec == null || _liveSince < 0) return;
            OnHostBatch(SyncBatch.Read(data, 0, data.Length), backlog);
        }

        /// <summary>As <see cref="OnHostBatch(byte[], int)"/> with the records the caller already decoded (decoded once).</summary>
        public void OnHostBatch(List<SyncRecord> records, int backlog)
        {
            if (_codec == null || _liveSince < 0) return;
            double now = _sessions.Clock.Seconds;
            bool compare = now - _liveSince >= ClientSettleSeconds && backlog <= ClientMaxBacklogForCompare;
            foreach (SyncRecord r in records)
            {
                try
                {
                    switch (r.Kind)
                    {
                        case SyncRecordKind.Spawn:
                        case SyncRecordKind.Upsert:
                        case SyncRecordKind.Values:
                            _hasher.OnRecord(r, _ids.GetEntity(r.NetId));
                            break;
                        case SyncRecordKind.Despawn:
                            _hasher.Forget(r.NetId);
                            ForgetRepair(r.NetId); // a pending repair of a despawned id would ask for a ghost
                            Icons.ForgetNetId(r.NetId); // the id may be reused; the old owner's icons must not stick to it
                            break;
                        case SyncRecordKind.BucketDigest:
                            if (compare) OnDigest(r.Bucket, r.Count, r.Hash, now);
                            break;
                        case SyncRecordKind.EntityHashes:
                            OnDetail(r.Bucket, r.Hashes, now);
                            break;
                        case SyncRecordKind.EntityExplain:
                            OnExplain(r.NetId, r.ComponentHashes);
                            break;
                        case SyncRecordKind.IconSet:
                        case SyncRecordKind.IconControl:
                            Icons.OnRecord(r, now);
                            ReplayRecord?.Invoke(r); // solo testing: newer icons make a pending icon comparison stale
                            break;
                        case SyncRecordKind.ReplayHashes:
                        case SyncRecordKind.ReplayMembers:
                        case SyncRecordKind.ReplayCheckpoint:
                            ReplayRecord?.Invoke(r); // solo testing: the checkpoints of a recording (replay only)
                            break;
                        case SyncRecordKind.LineRows:
                            Lines.OnHostRows(r.Payload, now, compare);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Diagnostics.Guard.Report(Diagnostics.Features.Integrity, "integrity record " + r.Kind, ex);
                }
            }
        }

        /// <summary>Client: this player deleted the entity (or its owner's cascade did): nothing to verify or repair.</summary>
        public void OnLocalDelete(uint id)
        {
            _hasher?.Forget(id);
            ForgetRepair(id);
        }

        private void ForgetRepair(uint id)
        {
            _tracker.Forget(id);
            if (_pendingRepairSet.Remove(id)) _pendingRepairs.Remove(id);
        }

        private void OnDigest(uint bucket, uint count, ulong sum, double now)
        {
            if (bucket < _lastDigestBucket)
            {
                _verifiedLastCycle = _verifiedThisCycle;
                _verifiedThisCycle = 0;
                if (_bucketMismatches > 0) _log.Debug(LogCategory.Integrity, "verification cycle: " + _bucketMismatches + " of " + _cycleCompared + " buckets mismatched");
                // massive divergence across a whole cycle may offer a resync
                _escalation.OnCycle(_cycleCompared, (int)_bucketMismatches, _tracker.GivenUpCount, _verifiedLastCycle, now);
                AnnounceOffer();
                _bucketMismatches = 0;
                _cycleCompared = 0;
            }
            _lastDigestBucket = bucket;
            _cycleCompared++;
            EntityDigest.Accumulator local = ClientBucket(bucket, now, true);
            if (local.Matches(count, sum))
            {
                _verifiedThisCycle += count;
                _tracker.OnBucketMatched(bucket, now);
                _escalation.OnBucketMatched(bucket);
                _scheduler.Cancel(bucket);
                return;
            }
            _bucketMismatches++;
            _scheduler.Request(bucket, now, now);
        }

        /// <summary>A resync the integrity check offers the player (the reason), or null.</summary>
        public string ResyncOffer => _sessions.Client != null ? _escalation.PendingOffer : null;

        /// <summary>The player declined the resync offer (not repeated for 30 minutes).</summary>
        public void DeclineResync()
        {
            if (_escalation.PendingOffer == null) return;
            _log.Info(LogCategory.Integrity, "the player declined the resync");
            _escalation.Decline(_sessions.Clock.Seconds);
        }

        /// <summary>The player accepted the resync; the caller rejoins (<see cref="SessionManager.Resync"/>).</summary>
        public void AcceptResync()
        {
            _log.Info(LogCategory.Integrity, "the player accepted the resync");
            _escalation.Accept(_sessions.Clock.Seconds);
        }

        private void AnnounceOffer()
        {
            string offer = _escalation.PendingOffer;
            if (offer == null || ReferenceEquals(offer, _announcedOffer)) return;
            _announcedOffer = offer;
            _log.Warn(LogCategory.Integrity, "the city differs from the host's in too many places for object repairs (" + offer + "); offering a resync");
            SessionManager.Notify(Settings.LocaleSource.Resolve("st.toast.resyncOffered"), Colossal.PSI.Common.ProgressState.Warning, "SkylineTogether.resync", 15f);
        }

        /// <summary>The client's digest of a bucket; with overrides, tolerated entities count with the host's last hash.</summary>
        private EntityDigest.Accumulator ClientBucket(uint bucket, double now, bool overrides)
        {
            var acc = new EntityDigest.Accumulator();
            bool any = overrides && _tracker.HasEntriesIn(bucket);
            uint last = EntityDigest.LastId(bucket);
            for (uint id = EntityDigest.FirstId(bucket); id <= last && id != 0; id++)
            {
                if (any && _tracker.TryGetOverride(id, now, out bool hostPresent, out ulong hostHash))
                {
                    if (hostPresent) acc.Add(id, hostHash);
                }
                else if (TryHash(id, out ulong hash))
                {
                    acc.Add(id, hash);
                }
                if (id == uint.MaxValue) break;
            }
            return acc;
        }

        private void OnDetail(uint bucket, List<EntityHash> host, double now)
        {
            _scheduler.OnArrived(bucket);
            if (host == null) return;
            HashBucket(bucket, _clientList);
            _tracker.OnDetail(host, _clientList, now, _outcome);
            if (_outcome.Repair.Count > 0)
            {
                var text = new StringBuilder();
                int shown = 0;
                foreach (uint id in _outcome.Repair)
                {
                    if (_pendingRepairs.Count < MaxPendingRepairs && _pendingRepairSet.Add(id)) _pendingRepairs.Add(id);
                    if (shown++ < 8)
                    {
                        text.Append(shown > 1 ? ", " : "").Append('#').Append(id).Append(' ').Append(Describe(id, host));
                        int before = _tracker.RepairsOf(id) - 1;
                        if (before > 0) text.Append(" [repair ").Append(before + 1).Append(']');
                    }
                }
                if (_outcome.Repair.Count > 8) text.Append(", ...");
                _log.Info(LogCategory.Integrity, "differs from the host; requesting " + _outcome.Repair.Count + " object(s): " + text);
            }
            foreach (uint id in _outcome.Explain) _pendingExplains.Add(id);
            // an entity repaired again after a repair that matched: ask what differs (rate-limited), so the log
            // tells which component keeps coming back
            foreach (uint id in _outcome.Repeated)
            {
                if (_pendingExplains.Contains(id) || !_repeatExplainGate.TryTake(now)) continue;
                _repeatExplains.Add(id);
                _pendingExplains.Add(id);
            }
            if (_outcome.GivenUp.Count > 0)
            {
                var given = new List<string>();
                foreach (uint id in _outcome.GivenUp)
                {
                    if (given.Count >= 8) break;
                    given.Add("#" + id + " " + Describe(id));
                }
                _log.Warn(LogCategory.Integrity, _outcome.GivenUp.Count + " object(s) still differ from the host after repeated repairs and tolerance; given up for this session: " + string.Join(", ", given));
            }
            if (_outcome.NewlyTolerated.Count > 0) LogTolerated(_outcome.NewlyTolerated, now);
            if (_outcome.Recheck) _scheduler.Request(bucket, now + _outcome.RecheckAfter, now);
            // a bucket whose repairs do not converge (or too different for them) is re-sent as a whole
            if (_escalation.OnDetail(bucket, _outcome.Mismatching, _outcome.Repair.Count, now) == RepairEscalation.Step.RebaselineBucket) RebaselineBucket(bucket, host);
            AnnounceOffer();
        }

        /// <summary>
        /// Asks the host to re-send every object of a bucket it has (as spawns, which create or reconcile)
        /// and to answer every id only this game has (the host sends a despawn): the same request as object repairs,
        /// for the whole bucket at once (at most 128 ids, through the same rate gate).
        /// </summary>
        private void RebaselineBucket(uint bucket, List<EntityHash> host)
        {
            int queued = 0;
            foreach (EntityHash h in host)
            {
                if (_pendingRepairs.Count < MaxPendingRepairs && _pendingRepairSet.Add(h.NetId))
                {
                    _pendingRepairs.Add(h.NetId);
                    queued++;
                }
            }
            foreach (EntityHash c in _clientList)
            {
                if (_pendingRepairs.Count < MaxPendingRepairs && _pendingRepairSet.Add(c.NetId))
                {
                    _pendingRepairs.Add(c.NetId);
                    queued++;
                }
            }
            _log.Info(LogCategory.Integrity, "bucket " + bucket + " still differs from the host after object repairs; re-sending all of it (" + queued + " objects; "
                                             + _escalation.Rebaselines + " bucket(s) re-sent so far)");
        }

        private string Describe(uint id, List<EntityHash> host)
        {
            bool onHost = host.Exists(h => h.NetId == id);
            bool here = _ids.GetEntity(id) != Entity.Null;
            string family = here ? Describe(id) : "?";
            if (onHost && !here) return "(missing here)";
            if (!onHost && here) return "(" + family + ", not on the host)";
            return "(" + family + ")";
        }

        private void LogTolerated(List<uint> ids, double now)
        {
            if (now - _lastToleranceLog < ToleranceLogInterval)
            {
                _suppressedToleranceLogs += ids.Count;
                return;
            }
            _lastToleranceLog = now;
            string more = _suppressedToleranceLogs > 0 ? " (+" + _suppressedToleranceLogs + " earlier, not logged)" : "";
            _suppressedToleranceLogs = 0;
            var parts = new List<string>();
            foreach (uint id in ids)
            {
                if (parts.Count >= 16) break;
                parts.Add("#" + id + " " + Describe(id));
            }
            _log.Warn(LogCategory.Integrity, ids.Count + " object(s) still differ from the host after " + _tracker.RepairsBeforeTolerance
                                             + " repairs; not repaired again for " + (int)(_tracker.ToleranceSeconds / 60) + " min: " + string.Join(", ", parts) + more
                                             + ". Explanations follow.");
        }

        private void OnExplain(uint id, List<ComponentHash> host)
        {
            if (host == null) return;
            _members.Clear();
            Entity e = _ids.GetEntity(id);
            Family family = _ids.GetFamily(id);
            if (e != Entity.Null && _codec.EntityManager.Exists(e)) _hasher.Collect(e, id, family, _members);
            var mine = new Dictionary<ulong, ComponentHash>();
            foreach (ComponentHash m in _members) mine[m.TypeHash] = m;
            var parts = new List<string>();
            foreach (ComponentHash h in host)
            {
                string name = TypeName(h.TypeHash);
                if (!mine.TryGetValue(h.TypeHash, out ComponentHash c)) parts.Add(name + " (only on the host)");
                else
                {
                    mine.Remove(h.TypeHash);
                    if (c.State != h.State) parts.Add(name + " (" + (h.State == ComponentHashState.Value ? "value on the host, presence here" : "presence on the host, value here") + ")");
                    else if (c.ValueHash != h.ValueHash) parts.Add(name + " (value)");
                }
            }
            foreach (ComponentHash c in mine.Values) parts.Add(TypeName(c.TypeHash) + " (only here)");
            if (host.Count == 0 && e != Entity.Null) parts.Add("the host does not have it");
            if (e == Entity.Null) parts.Add("missing here");
            string what = "#" + id + " (" + Describe(id) + ") differs in: " + (parts.Count == 0 ? "nothing now (it changed meanwhile)" : string.Join(", ", parts));
            // explanations of repeated repairs are diagnostics (Info); those of tolerated entities stay warnings
            if (_repeatExplains.Remove(id)) _log.Info(LogCategory.Integrity, "repaired again: " + what + " (repairs so far: " + _tracker.RepairsOf(id) + ")");
            else _log.Warn(LogCategory.Integrity, what);
        }

        private static string TypeName(ulong typeHash)
        {
            TypeHandler h = TypeHandlers.GetByHash(typeHash);
            return h != null ? h.Name : "type 0x" + typeHash.ToString("X16", CultureInfo.InvariantCulture);
        }

        /// <summary>Client, at the end of the frame: sends due requests (details, repairs, explanations, line re-sends, icon sync).</summary>
        public void ClientFrameEnd(double now)
        {
            if (_codec == null || _liveSince < 0 || _sessions.Client == null) return;
            try
            {
                Icons.ClientFrameEnd(now);
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Integrity, "icon check", ex);
            }
            _requests.Reset();
            _due.Clear();
            int details = Math.Min(MaxDetailsPerFrame, _detailGate.Available(now));
            if (details > 0 && _scheduler.TakeDue(now, details, _due) > 0)
            {
                _detailGate.TryTake(now, _due.Count);
                _requests.RequestIds(SyncRequest.BucketDetail, _due);
            }
            int repairs = Math.Min(Math.Min(_pendingRepairs.Count, _repairGate.Available(now)), SyncBatch.MaxIdsPerRecord);
            if (repairs > 0)
            {
                _repairGate.TryTake(now, repairs);
                List<uint> take = _pendingRepairs.GetRange(0, repairs);
                _pendingRepairs.RemoveRange(0, repairs);
                foreach (uint id in take) _pendingRepairSet.Remove(id);
                _requests.RequestIds(SyncRequest.ResendEntities, take);
            }
            if (_pendingExplains.Count > 0)
            {
                _requests.RequestIds(SyncRequest.ExplainEntities, _pendingExplains.GetRange(0, Math.Min(_pendingExplains.Count, 16)));
                _pendingExplains.RemoveRange(0, Math.Min(_pendingExplains.Count, 16));
            }
            List<uint> lines = Lines.TakeResends();
            if (lines.Count > 0) _requests.RequestIds(SyncRequest.ResendLines, lines);
            if (Icons.TakeFullSyncRequest()) _requests.RequestIds(SyncRequest.IconFullSync, Array.Empty<uint>());
            if (!_requests.IsEmpty)
            {
                ArraySegment<byte> bytes = _requests.Finish();
                _sessions.Client.SendToHost(MessageKind.Proposal, bytes.Array, bytes.Offset, bytes.Count);
            }
            if (now - _lastSweep > SweepInterval)
            {
                _lastSweep = now;
                _tracker.Sweep(now);
            }
        }
    }
}
