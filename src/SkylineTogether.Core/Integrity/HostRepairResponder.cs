using System;
using System.Collections.Generic;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// The host side of the repair ladder (ARCH §8.5 "Who decides", §14.3). Clients send only hints; the host keeps the
    /// ladder state per peer and decides:
    /// <list type="bullet">
    /// <item><b>Levels 0-2</b> (<see cref="RepairLevel.Leak"/>, <see cref="RepairLevel.Entity"/>): a slot the host does
    /// not have is answered "not present" (<see cref="IHostReplicationEngine.ReportNotPresent"/>; the client adds
    /// <c>Deleted</c>); a V-class slot gets its pairs marked dirty for the peer at P1
    /// (<see cref="IHostReplicationEngine.MarkDirty"/>; signature problems of an S entity show in its S bucket); every
    /// other slot is re-serialised from live ECS into the next epoch flagged <c>Repair</c>
    /// (<see cref="IHostReplicationEngine.ResendStructural"/>), which also creates missing entities.</item>
    /// <item><b>Level 1</b> (<see cref="RepairLevel.Bucket"/>): each slot of the request names a bucket (its first slot,
    /// slot 1 for bucket 0); the buckets are queued per peer and <see cref="TakeDetails"/> builds the details from the
    /// hash shadow (<see cref="BuildDetail"/>) once a per-peer class's bucket has no dirty bits for that peer.</item>
    /// <item><b>Level 3</b>: at most one rebaseline per peer at a time and at most three per peer within 10 minutes; a
    /// second rebaseline of the same class within 10 minutes, or one beyond the cap, becomes a single snapshot offer.</item>
    /// <item><b>Level 4</b>: a peer that needs more than three snapshot resyncs within 30 minutes is disconnected with the
    /// reason "repair loop" (<see cref="RepairDecision.DisconnectRepairLoop"/>; both sides write a diagnostics dump).</item>
    /// <item>Requests above the per-peer rate (10/s) are dropped (<see cref="RepairDecision.Throttled"/>); the host never
    /// kicks for repair-request rate. At most 512 slots of a request are served.</item>
    /// </list>
    /// Threading: main thread (requests arrive through <c>SessionController.Update</c>); engine calls made outside the
    /// capture window are queued by the engine (ARCH §4.5.5). Every member takes an internal lock.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class HostRepairResponder
    {
        private const double RebaselineWindowSeconds = 600;
        private const double SnapshotWindowSeconds = 1800;
        private const int WarningsBeforeSampling = 20;

        private readonly object _gate = new object();
        private readonly IHostReplicationEngine _engine;
        private readonly IHashShadowView _shadow;
        private readonly HostRepairPolicy _policy;
        private readonly IClock _clock;
        private readonly ILog _log;
        private readonly Dictionary<byte, PeerState> _peers = new Dictionary<byte, PeerState>();
        private readonly List<NetSlot> _scratchSlots = new List<NetSlot>();
        private readonly List<uint> _scratchHashes = new List<uint>();
        private readonly HashSet<uint> _scratchSeen = new HashSet<uint>();
        private HostRepairStats _stats;
        private int _warnings;

        public HostRepairResponder(IHostReplicationEngine engine, IHashShadowView shadow, HostRepairPolicy policy, IClock clock, ILog log)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _shadow = shadow ?? throw new ArgumentNullException(nameof(shadow));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _policy = policy ?? new HostRepairPolicy();
            _log = log;
        }

        /// <summary>The caps in use (mutable; read at use time).</summary>
        public HostRepairPolicy Policy => _policy;

        /// <summary>
        /// The per-slot digests of one bucket of one class from the hash shadow (ARCH §8.5 level 1), sorted by slot, for the
        /// state as of <paramref name="atEpoch"/> (the last epoch whose records the shadow reflects). Returns null when the
        /// detail cannot be built (undefined class or bucket, or a failing shadow; logged).
        /// </summary>
        public HashDetailMessage BuildDetail(ReplicationClass cls, int bucket, EpochId atEpoch)
        {
            lock (_gate)
            {
                return BuildDetailLocked(cls, bucket, atEpoch);
            }
        }

        /// <summary>Decides one repair request of <paramref name="peer"/>; never disconnects for the request rate.</summary>
        public RepairDecision OnRepairRequest(PeerId peer, RepairRequestMessage request)
        {
            lock (_gate)
            {
                if (request == null)
                {
                    _stats.Rejected++;
                    Warn("repair request of " + peer + " ignored: null");
                    return RepairDecision.Throttled;
                }
                if (!IntegrityRules.IsAudited(request.Class))
                {
                    _stats.Rejected++;
                    Warn("repair request of " + peer + " ignored: class " + (int)request.Class + " has no bucket audit");
                    return RepairDecision.Throttled;
                }
                double now = _clock.Seconds;
                PeerState state = GetOrCreate(peer);
                if (!state.Requests.TryAdd(now, Math.Max(1, _policy.MaxRequestsPerSecond)))
                {
                    _stats.Throttled++;
                    if (++state.ThrottledInARow == 1 || state.ThrottledInARow % 100 == 0)
                    {
                        _log.Info(LogCategory.Integrity, "repair: request of " + peer + " throttled (more than "
                                                        + IntegrityRules.Format(_policy.MaxRequestsPerSecond) + " per second); the client repeats it later");
                    }
                    return RepairDecision.Throttled;
                }
                state.ThrottledInARow = 0;
                switch (request.LevelHint)
                {
                    case RepairLevel.Leak:
                    case RepairLevel.Entity:
                        return ServeEntities(peer, request);
                    case RepairLevel.Bucket:
                        return QueueDetails(peer, state, request, now);
                    case RepairLevel.Rebaseline:
                        return DecideRebaseline(peer, state, request.Class, now, "requested by the client");
                    case RepairLevel.Snapshot:
                        return DecideSnapshot(peer, state, now, "requested by the client");
                    default:
                        _stats.Rejected++;
                        Warn("repair request of " + peer + " ignored: undefined level " + (int)request.LevelHint);
                        return RepairDecision.Throttled;
                }
            }
        }

        /// <summary>
        /// A resync the client asked for outside the repair ladder (control message <c>ResyncRequest</c>, e.g. after a
        /// structural apply fault): an offer, a throttle while an offer is outstanding, or the repair-loop disconnect.
        /// </summary>
        public RepairDecision OnResyncRequest(PeerId peer, string reason)
        {
            lock (_gate)
            {
                return DecideSnapshot(peer, GetOrCreate(peer), _clock.Seconds, string.IsNullOrEmpty(reason) ? "resync requested" : reason);
            }
        }

        /// <summary>The peer performed a snapshot resync (for any reason): counts toward the repair-loop limit and clears its pending repairs.</summary>
        public void OnSnapshotResync(PeerId peer)
        {
            lock (_gate)
            {
                double now = _clock.Seconds;
                PeerState state = GetOrCreate(peer);
                state.SnapshotResyncs.Add(now);
                state.SnapshotOfferedAt = double.NaN;
                state.Running.Clear();
                state.ClearDetails();
                _log.Info(LogCategory.Integrity, "repair: " + peer + " resynced from a snapshot (" + IntegrityRules.Format(state.SnapshotResyncs.Count(now))
                                                + " within 30 minutes)");
            }
        }

        /// <summary>The bulk transfer of a rebaseline finished (or failed): the peer may start another one.</summary>
        public void OnRebaselineCompleted(PeerId peer, ReplicationClass cls)
        {
            lock (_gate)
            {
                if (!_peers.TryGetValue(peer.Value, out PeerState state)) return;
                for (int i = state.Running.Count - 1; i >= 0; i--)
                {
                    if (state.Running[i].Class == cls) state.Running.RemoveAt(i);
                }
            }
        }

        /// <summary>True while a rebaseline of the class streams to the peer.</summary>
        public bool IsRebaselineRunning(PeerId peer, ReplicationClass cls)
        {
            lock (_gate)
            {
                if (!_peers.TryGetValue(peer.Value, out PeerState state)) return false;
                PruneRunning(peer, state, _clock.Seconds);
                foreach (RunningRebaseline running in state.Running)
                {
                    if (running.Class == cls) return true;
                }
                return false;
            }
        }

        /// <summary>Bucket details queued for the peer.</summary>
        public int PendingDetails(PeerId peer)
        {
            lock (_gate)
            {
                return _peers.TryGetValue(peer.Value, out PeerState state) ? state.DetailQueue.Count : 0;
            }
        }

        /// <summary>
        /// Builds up to <paramref name="maxDetails"/> queued bucket details for the peer, as of <paramref name="atEpoch"/>.
        /// A detail of a per-peer class (V, A, A-low, M, K) waits while the peer has dirty bits in its bucket, because the
        /// shadow then holds values the peer has not received (ARCH §8.4); after <see cref="HostRepairPolicy.DetailMaxWaitSeconds"/>
        /// it is dropped (the client asks again when the bucket still mismatches). Returns how many were appended.
        /// </summary>
        public int TakeDetails(PeerId peer, IPeerDirtyView dirtyOrNull, EpochId atEpoch, List<HashDetailMessage> output, int maxDetails = 4)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            lock (_gate)
            {
                if (maxDetails <= 0 || !_peers.TryGetValue(peer.Value, out PeerState state)) return 0;
                double now = _clock.Seconds;
                int built = 0;
                int attempts = state.DetailQueue.Count;
                while (built < maxDetails && attempts-- > 0)
                {
                    PendingDetail item = state.DetailQueue.Dequeue();
                    if (IntegrityRules.IsPerPeer(item.Class) && IsDirty(dirtyOrNull, item, peer))
                    {
                        if (now - item.QueuedAt >= _policy.DetailMaxWaitSeconds)
                        {
                            state.DetailKeys.Remove(IntegrityRules.BucketKey(item.Class, item.Bucket));
                            _stats.DetailsDropped++;
                            _log.Debug(LogCategory.Integrity, "repair: detail " + IntegrityRules.Describe(item.Class, item.Bucket) + " for " + peer
                                                             + " dropped: the bucket stayed dirty for " + IntegrityRules.Format(_policy.DetailMaxWaitSeconds) + " s");
                        }
                        else
                        {
                            state.DetailQueue.Enqueue(item);
                        }
                        continue;
                    }
                    state.DetailKeys.Remove(IntegrityRules.BucketKey(item.Class, item.Bucket));
                    HashDetailMessage detail = BuildDetailLocked(item.Class, item.Bucket, atEpoch);
                    if (detail == null) continue;
                    output.Add(detail);
                    built++;
                }
                return built;
            }
        }

        /// <summary>Forgets the peer's ladder state (disconnect).</summary>
        public void RemovePeer(PeerId peer)
        {
            lock (_gate)
            {
                _peers.Remove(peer.Value);
            }
        }

        public HostRepairStats GetStats()
        {
            lock (_gate)
            {
                return _stats;
            }
        }

        // ------------------------------------------------------------------ levels

        private RepairDecision ServeEntities(PeerId peer, RepairRequestMessage request)
        {
            NetSlot[] slots = request.Slots ?? Array.Empty<NetSlot>();
            int limit = Math.Min(Math.Max(1, _policy.MaxSlotsPerRequest), Limits.MaxRepairSlots);
            int served = 0;
            int ignored = 0;
            _scratchSeen.Clear();
            for (int i = 0; i < slots.Length; i++)
            {
                if (served >= limit)
                {
                    ignored = slots.Length - i;
                    break;
                }
                NetSlot slot = slots[i];
                if (slot.IsNone || !_scratchSeen.Add(slot.Value)) continue;
                try
                {
                    if (!IsBound(slot))
                    {
                        _engine.ReportNotPresent(peer, slot);
                        _stats.NotPresent++;
                    }
                    else if (request.Class == ReplicationClass.V)
                    {
                        _engine.MarkDirty(peer, slot, ulong.MaxValue, Priority.P1);
                        _stats.SlotsMarkedDirty++;
                    }
                    else
                    {
                        _engine.ResendStructural(peer, slot);
                        _stats.SlotsResent++;
                    }
                    served++;
                }
                catch (Exception ex)
                {
                    _log.Error(LogCategory.Integrity, "repair: serving " + slot + " (" + request.Class + ") for " + peer + " failed", ex);
                }
            }
            if (ignored > 0)
            {
                _log.Debug(LogCategory.Integrity, "repair: " + IntegrityRules.Format(ignored) + " slot(s) of a request of " + peer + " beyond the per-request limit ignored");
            }
            _stats.Served++;
            return RepairDecision.Served;
        }

        private RepairDecision QueueDetails(PeerId peer, PeerState state, RepairRequestMessage request, double now)
        {
            NetSlot[] designators = request.Slots ?? Array.Empty<NetSlot>();
            int maxPerRequest = Math.Max(1, _policy.MaxDetailsPerRequest);
            int maxQueued = Math.Max(1, _policy.MaxQueuedDetailsPerPeer);
            int added = 0;
            foreach (NetSlot designator in designators)
            {
                if (added >= maxPerRequest) break;
                if (designator.IsNone) continue;
                long key = IntegrityRules.BucketKey(request.Class, designator.Bucket);
                if (state.DetailKeys.Contains(key)) continue;
                while (state.DetailQueue.Count >= maxQueued)
                {
                    PendingDetail oldest = state.DetailQueue.Dequeue();
                    state.DetailKeys.Remove(IntegrityRules.BucketKey(oldest.Class, oldest.Bucket));
                    _stats.DetailsDropped++;
                }
                state.DetailQueue.Enqueue(new PendingDetail(request.Class, designator.Bucket, now));
                state.DetailKeys.Add(key);
                _stats.DetailsQueued++;
                added++;
            }
            _stats.Served++;
            if (_log != null && _log.IsEnabled(LogLevel.Debug))
            {
                _log.Debug(LogCategory.Integrity, "repair: " + IntegrityRules.Format(added) + " bucket detail(s) of class " + request.Class + " queued for " + peer);
            }
            return RepairDecision.Served;
        }

        private RepairDecision DecideRebaseline(PeerId peer, PeerState state, ReplicationClass cls, double now, string reason)
        {
            PruneRunning(peer, state, now);
            foreach (RunningRebaseline running in state.Running)
            {
                if (running.Class == cls) return RepairDecision.Served;   // already streaming: nothing new to do
            }
            if (state.Running.Count >= Math.Max(1, _policy.MaxConcurrentRebaselines))
            {
                _stats.Throttled++;
                return RepairDecision.Throttled;                          // one at a time; the client asks again later
            }
            state.PruneRebaselines(now);
            if (state.RebaselinedRecently(cls))
            {
                return DecideSnapshot(peer, state, now, "a second rebaseline of class " + cls + " within 10 minutes (" + reason + ")");
            }
            if (state.Rebaselines.Count >= Math.Max(0, _policy.MaxRebaselinesPer10Min))
            {
                return DecideSnapshot(peer, state, now, IntegrityRules.Format(state.Rebaselines.Count) + " rebaselines within 10 minutes already (" + reason + ")");
            }
            try
            {
                _engine.StartRebaseline(peer, cls);
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Integrity, "repair: starting a rebaseline of class " + cls + " for " + peer + " failed", ex);
                _stats.Throttled++;
                return RepairDecision.Throttled;
            }
            state.Rebaselines.Add(new RunningRebaseline(cls, now));
            state.Running.Add(new RunningRebaseline(cls, now));
            _stats.Rebaselines++;
            _log.Info(LogCategory.Integrity, "repair: rebaseline of class " + cls + " started for " + peer + " (" + reason + ")");
            return RepairDecision.Rebaseline;
        }

        private RepairDecision DecideSnapshot(PeerId peer, PeerState state, double now, string reason)
        {
            int recent = state.SnapshotResyncs.Count(now);
            if (recent >= Math.Max(0, _policy.MaxSnapshotResyncsPer30Min))
            {
                _stats.RepairLoops++;
                _log.Error(LogCategory.Integrity, "repair: " + peer + " needs another snapshot resync after " + IntegrityRules.Format(recent)
                                                 + " within 30 minutes; disconnecting (repair loop): " + reason);
                return RepairDecision.DisconnectRepairLoop;
            }
            if (!double.IsNaN(state.SnapshotOfferedAt) && now - state.SnapshotOfferedAt < _policy.SnapshotOfferTimeoutSeconds)
            {
                _stats.Throttled++;
                return RepairDecision.Throttled;                          // a single offer is outstanding
            }
            state.SnapshotOfferedAt = now;
            _stats.SnapshotOffers++;
            _log.Warn(LogCategory.Integrity, "repair: offering " + peer + " a snapshot resync: " + reason);
            return RepairDecision.OfferSnapshot;
        }

        // ------------------------------------------------------------------ details

        private HashDetailMessage BuildDetailLocked(ReplicationClass cls, int bucket, EpochId atEpoch)
        {
            if (!IntegrityRules.IsAudited(cls) || !IntegrityRules.IsValidBucket(bucket))
            {
                Warn("bucket detail not built: class " + (int)cls + ", bucket " + IntegrityRules.Format(bucket));
                return null;
            }
            _scratchSlots.Clear();
            _scratchHashes.Clear();
            try
            {
                _shadow.CollectBucket(cls, bucket, _scratchSlots, _scratchHashes);
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Integrity, "repair: the hash shadow failed to collect bucket " + IntegrityRules.Describe(cls, bucket), ex);
                return null;
            }
            if (_scratchSlots.Count != _scratchHashes.Count)
            {
                _log.Error(LogCategory.Integrity, "repair: the hash shadow returned " + IntegrityRules.Format(_scratchSlots.Count) + " slots but "
                                                 + IntegrityRules.Format(_scratchHashes.Count) + " hashes for " + IntegrityRules.Describe(cls, bucket));
                return null;
            }
            int n = _scratchSlots.Count;
            var slots = new uint[n];
            var hashes = new uint[n];
            int kept = 0;
            for (int i = 0; i < n; i++)
            {
                NetSlot slot = _scratchSlots[i];
                if (slot.IsNone || slot.Bucket != bucket) continue;
                slots[kept] = slot.Value;
                hashes[kept] = _scratchHashes[i];
                kept++;
            }
            if (kept != n) Warn("the hash shadow returned " + IntegrityRules.Format(n - kept) + " slot(s) outside bucket " + IntegrityRules.Describe(cls, bucket));
            Array.Sort(slots, hashes, 0, kept);
            int unique = 0;
            for (int i = 0; i < kept; i++)
            {
                if (unique > 0 && slots[unique - 1] == slots[i]) continue;   // a bucket holds each slot once
                slots[unique] = slots[i];
                hashes[unique] = hashes[i];
                unique++;
            }
            var detailSlots = new NetSlot[unique];
            var detailHashes = new uint[unique];
            for (int i = 0; i < unique; i++)
            {
                detailSlots[i] = new NetSlot(slots[i]);
                detailHashes[i] = hashes[i];
            }
            _stats.DetailsBuilt++;
            return new HashDetailMessage { Class = cls, Bucket = bucket, AtEpoch = atEpoch, Slots = detailSlots, Hashes = detailHashes };
        }

        private bool IsDirty(IPeerDirtyView dirty, PendingDetail item, PeerId peer)
        {
            if (dirty == null) return false;
            try
            {
                return dirty.BucketHasDirty(item.Bucket);
            }
            catch (Exception ex)
            {
                Warn("repair: the dirty view of " + peer + " failed for " + IntegrityRules.Describe(item.Class, item.Bucket) + " (" + ex.GetType().Name + "); detail deferred");
                return true;
            }
        }

        // ------------------------------------------------------------------ helpers

        private bool IsBound(NetSlot slot)
        {
            ISlotTable slots = _engine.Slots;
            return slots == null || slots.TryGetEntity(slot, out _);
        }

        private void PruneRunning(PeerId peer, PeerState state, double now)
        {
            for (int i = state.Running.Count - 1; i >= 0; i--)
            {
                if (now - state.Running[i].StartedAt < _policy.RebaselineTimeoutSeconds) continue;
                _log.Warn(LogCategory.Integrity, "repair: the rebaseline of class " + state.Running[i].Class + " for " + peer + " was never reported complete; counting it as finished");
                state.Running.RemoveAt(i);
            }
        }

        private PeerState GetOrCreate(PeerId peer)
        {
            if (!_peers.TryGetValue(peer.Value, out PeerState state))
            {
                state = new PeerState();
                _peers.Add(peer.Value, state);
            }
            return state;
        }

        private void Warn(string message)
        {
            _warnings++;
            if (_warnings <= WarningsBeforeSampling || _warnings % 1000 == 0)
            {
                _log.Warn(LogCategory.Integrity, message + (_warnings == WarningsBeforeSampling ? " (further warnings are sampled)" : string.Empty));
            }
        }

        private readonly struct PendingDetail
        {
            internal readonly ReplicationClass Class;
            internal readonly int Bucket;
            internal readonly double QueuedAt;

            internal PendingDetail(ReplicationClass cls, int bucket, double queuedAt)
            {
                Class = cls;
                Bucket = bucket;
                QueuedAt = queuedAt;
            }
        }

        private readonly struct RunningRebaseline
        {
            internal readonly ReplicationClass Class;
            internal readonly double StartedAt;

            internal RunningRebaseline(ReplicationClass cls, double startedAt)
            {
                Class = cls;
                StartedAt = startedAt;
            }
        }

        private sealed class PeerState
        {
            internal readonly TimeWindow Requests = new TimeWindow(1.0);
            internal readonly TimeWindow SnapshotResyncs = new TimeWindow(SnapshotWindowSeconds);
            internal readonly List<RunningRebaseline> Rebaselines = new List<RunningRebaseline>();   // started within 10 minutes
            internal readonly List<RunningRebaseline> Running = new List<RunningRebaseline>();
            internal readonly Queue<PendingDetail> DetailQueue = new Queue<PendingDetail>();
            internal readonly HashSet<long> DetailKeys = new HashSet<long>();
            internal double SnapshotOfferedAt = double.NaN;
            internal int ThrottledInARow;

            internal void PruneRebaselines(double now)
            {
                for (int i = Rebaselines.Count - 1; i >= 0; i--)
                {
                    if (now - Rebaselines[i].StartedAt >= RebaselineWindowSeconds) Rebaselines.RemoveAt(i);
                }
            }

            internal bool RebaselinedRecently(ReplicationClass cls)
            {
                foreach (RunningRebaseline rebaseline in Rebaselines)
                {
                    if (rebaseline.Class == cls) return true;
                }
                return false;
            }

            internal void ClearDetails()
            {
                DetailQueue.Clear();
                DetailKeys.Clear();
            }
        }
    }
}
