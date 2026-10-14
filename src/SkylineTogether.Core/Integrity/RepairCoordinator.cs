using System;
using System.Collections.Generic;
using System.Text;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// The client side of the repair ladder (docs/history/ARCHITECTURE-original.md §8.3-8.5). It compares host beacons with the ledger, confirms bucket
    /// mismatches, asks for bucket details, turns confirmed per-slot differences into entity re-sends, confirms leaks,
    /// keeps the tolerance list, and produces coalesced, rate-limited <see cref="RepairRequestMessage"/>s. Every request
    /// carries only a level <b>hint</b>; the host keeps the ladder state per peer and decides
    /// (<see cref="HostRepairResponder"/>).
    /// <list type="bullet">
    /// <item><b>Confirmation</b> (§8.4): level 1 only after two beacons of the same bucket, at least the confirmation
    /// interval (250 ms, the longest coalescing interval) apart, both mismatch the ledger. A matching beacon in between
    /// resets the bucket. A bucket detail is requested with a <see cref="RepairLevel.Bucket"/> request whose slots name
    /// buckets (the bucket's first slot; slot 1 for bucket 0).</item>
    /// <item><b>Bucket details</b> (level 1): the host's per-slot digests are compared with the ledger's
    /// (<see cref="IHashLedgerDetail"/>); differing, missing and extra slots are confirmed differences. Only differences
    /// of a confirmed bucket count toward the thresholds of §8.5 (<see cref="RepairThresholds"/>): ≤ 64 → entity re-send
    /// (level 2, extras included: the host answers "not present" and the client adds <c>Deleted</c>); &gt; 64 in a bucket,
    /// &gt; 10 % of a class's buckets confirmed or &gt; 3 confirmed bucket repairs per minute → rebaseline hint (level 3);
    /// a confirmed structural mismatch in &gt; 5 % of the S buckets → snapshot hint (level 4). A second rebaseline of a
    /// class within 10 minutes becomes a snapshot hint. Slots changed locally after the detail's epoch are skipped (they
    /// cannot be compared with it).</item>
    /// <item><b>Leaks</b> (level 0, §5.2): repaired only when reported in two different frames (or immediately after an
    /// end-of-frame re-application, §4.7.8), never for pending pairs (<see cref="IPendingPairs"/>) or tolerated ones.</item>
    /// <item><b>Tolerance</b>: a pair (or, from details, a slot) that still mismatches after two repairs is masked for 10
    /// minutes and logged; only if the pairs tolerated since the class's last rebaseline exceed 1 % of the class does it
    /// escalate to level 3.</item>
    /// <item><b>Requests</b> (§8.3): one per class per 100 ms, at most 5 per second with at most 256 slots each, below the
    /// host's acceptance limit (10/s × 512 slots, §14.3); a host throttle notice backs off (1 s doubling to 8 s).</item>
    /// </list>
    /// Time: every <c>now</c> argument must be on the coordinator's clock (<see cref="IClock.Seconds"/>); operations without
    /// a <c>now</c> argument read the clock. Threading: main thread; every member takes an internal lock, so statistics may
    /// be read from elsewhere.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class RepairCoordinator
    {
        private const double BucketRepairWindowSeconds = 60;          // "> 3 confirmed bucket repairs per minute"
        private const double RepeatRebaselineWindowSeconds = 600;     // "≥ 2 rebaselines of the same class within 10 minutes"
        private const double CodecFaultWindowSeconds = 600;           // "a second fault of the same type within 10 minutes"
        private const double MaxBackoffSeconds = 8;
        private const double BackoffResetSeconds = 10;
        private const int WarningsBeforeSampling = 20;

        private static readonly ReplicationClass[] AuditedClasses =
        {
            ReplicationClass.S, ReplicationClass.G, ReplicationClass.V, ReplicationClass.A,
            ReplicationClass.ALow, ReplicationClass.M, ReplicationClass.K
        };

        private readonly object _gate = new object();
        private readonly IHashLedger _ledger;
        private readonly IHashLedgerDetail _detail;
        private readonly IPendingPairs _pending;
        private readonly IClock _clock;
        private readonly ILog _log;
        private readonly double _confirmSeconds;
        private readonly int _maxRequestsPerSecond;
        private readonly int _maxSlotsPerRequest;
        private readonly RepairThresholds _thresholds = new RepairThresholds();
        private readonly ClassState[] _classes = new ClassState[IntegrityRules.ClassCount];
        private readonly Dictionary<long, BucketState> _buckets = new Dictionary<long, BucketState>();
        private readonly Dictionary<ulong, double> _leakCandidates = new Dictionary<ulong, double>();
        private readonly Dictionary<ulong, int> _pairFailures = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, Tolerance> _toleratedPairs = new Dictionary<ulong, Tolerance>();
        private readonly Dictionary<uint, Tolerance> _toleratedSlots = new Dictionary<uint, Tolerance>();
        private readonly Dictionary<ushort, double> _lastCodecFault = new Dictionary<ushort, double>();
        private readonly List<HashDetailMessage> _deferredDetails = new List<HashDetailMessage>();
        private readonly TimeWindow _requestWindow = new TimeWindow(1.0);
        private readonly List<NetSlot> _scratchSlots = new List<NetSlot>();
        private readonly List<ulong> _scratchKeys = new List<ulong>();
        private RepairStats _stats;
        private EpochId _lastApplied;
        private bool _epochTracking;
        private double _throttledUntil = double.NaN;
        private double _backoff;
        private bool _resyncWanted;
        private string _resyncReason;
        private double _resyncRaisedAt = double.NaN;
        private bool _pendingSnapshot;
        private int _classRotation;
        private int _warnings;
        private bool _degradedWarned;

        public RepairCoordinator(IHashLedger ledger, IPendingPairs pending, IClock clock, ILog log, double confirmSeconds = 0.25,
                                 int maxRequestsPerSecond = 5, int maxSlotsPerRequest = 256)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (double.IsNaN(confirmSeconds) || confirmSeconds < 0) throw new ArgumentOutOfRangeException(nameof(confirmSeconds), confirmSeconds, "must be zero or positive");
            if (maxRequestsPerSecond < 1) throw new ArgumentOutOfRangeException(nameof(maxRequestsPerSecond), maxRequestsPerSecond, "at least one request per second");
            if (maxSlotsPerRequest < 1 || maxSlotsPerRequest > Limits.MaxRepairSlots)
            {
                throw new ArgumentOutOfRangeException(nameof(maxSlotsPerRequest), maxSlotsPerRequest, "1 ... " + Limits.MaxRepairSlots);
            }
            _detail = ledger as IHashLedgerDetail;
            _pending = pending;
            _log = log;
            _confirmSeconds = confirmSeconds;
            _maxRequestsPerSecond = maxRequestsPerSecond;
            _maxSlotsPerRequest = maxSlotsPerRequest;
            foreach (ReplicationClass cls in AuditedClasses) _classes[(int)cls] = new ClassState(cls);
        }

        /// <summary>The ladder thresholds (mutable; read at use time).</summary>
        public RepairThresholds Thresholds => _thresholds;

        /// <summary>True while a bucket is confirmed mismatching, a detail is awaited, or a request is queued.</summary>
        public bool IsRepairing
        {
            get
            {
                lock (_gate)
                {
                    if (_pendingSnapshot) return true;
                    foreach (ReplicationClass cls in AuditedClasses)
                    {
                        if (_classes[(int)cls].HasPending) return true;
                    }
                    foreach (BucketState state in _buckets.Values)
                    {
                        if (state.Confirmed || state.DetailQueued || !double.IsNaN(state.DetailRequestedAt)) return true;
                    }
                    return _deferredDetails.Count > 0;
                }
            }
        }

        // ================================================================== beacons (docs/history/ARCHITECTURE-original.md §8.4)

        /// <summary>Compares a beacon with the ledger after everything before it was applied; level 1 only after two mismatches ≥ the confirmation interval apart.</summary>
        public RepairAction OnBeacon(in HashBeacon beacon) => OnBeacon(beacon, EpochId.None);

        /// <summary>
        /// As <see cref="OnBeacon(in HashBeacon)"/>, with the epoch whose message carried the beacon: when the ledger
        /// changed that bucket in a later epoch (a merged apply plan), the beacon cannot describe the ledger and is ignored.
        /// </summary>
        public RepairAction OnBeacon(in HashBeacon beacon, EpochId beaconEpoch)
        {
            lock (_gate)
            {
                ReplicationClass cls = beacon.Class;
                int bucket = beacon.Bucket;
                if (!IntegrityRules.IsAudited(cls) || !IntegrityRules.IsValidBucket(bucket) || beacon.Count < 0)
                {
                    Warn("beacon ignored: class " + (int)cls + ", bucket " + IntegrityRules.Format(bucket) + ", count " + IntegrityRules.Format(beacon.Count));
                    return RepairAction.None;
                }
                double now = _clock.Seconds;
                ClassState cs = _classes[(int)cls];
                if (bucket > cs.MaxBucketSeen) cs.MaxBucketSeen = bucket;
                if (!beaconEpoch.IsNone && _detail != null && _detail.BucketModifiedEpoch(cls, bucket) > beaconEpoch)
                {
                    return RepairAction.None;
                }

                long key = IntegrityRules.BucketKey(cls, bucket);
                _buckets.TryGetValue(key, out BucketState state);
                if (_ledger.BucketSum(cls, bucket) == beacon.Sum && _ledger.BucketCount(cls, bucket) == beacon.Count)
                {
                    if (state != null)
                    {
                        if (state.Confirmed) _log.Info(LogCategory.Integrity, "repair: bucket " + IntegrityRules.Describe(cls, bucket) + " matches the host again");
                        RemoveBucket(key, state);
                    }
                    return RepairAction.None;
                }

                _stats.BucketMismatches++;
                if (state == null)
                {
                    state = new BucketState(cls, bucket) { FirstMismatchAt = now };
                    _buckets.Add(key, state);
                    if (_log != null && _log.IsEnabled(LogLevel.Debug))
                    {
                        _log.Debug(LogCategory.Integrity, "repair: beacon " + IntegrityRules.Describe(cls, bucket) + " differs (host count "
                                                         + IntegrityRules.Format(beacon.Count) + ", ledger " + IntegrityRules.Format(_ledger.BucketCount(cls, bucket)) + "); awaiting confirmation");
                    }
                    return RepairAction.AwaitConfirmation;
                }
                if (state.DetailQueued) return RepairAction.None;
                if (!double.IsNaN(state.DetailRequestedAt))
                {
                    if (now - state.DetailRequestedAt < _thresholds.DetailTimeoutSeconds) return RepairAction.None;
                    // The detail never arrived (dropped by a host throttle, or lost to a reset): the bucket is still
                    // confirmed, so ask again at once.
                    state.DetailRequestedAt = double.NaN;
                    QueueDetail(cs, state);
                    _log.Info(LogCategory.Integrity, "repair: no detail for " + IntegrityRules.Describe(cls, bucket) + " within "
                                                    + IntegrityRules.Format(_thresholds.DetailTimeoutSeconds) + " s; requested again");
                    return RepairAction.RequestDetail;
                }
                if (double.IsNaN(state.FirstMismatchAt))
                {
                    state.FirstMismatchAt = now;
                    return RepairAction.AwaitConfirmation;
                }
                if (now - state.FirstMismatchAt < _confirmSeconds) return RepairAction.AwaitConfirmation;

                state.Confirmed = true;
                state.ConfirmedAt = now;
                state.FirstMismatchAt = double.NaN;
                QueueDetail(cs, state);
                _log.Info(LogCategory.Integrity, "repair: bucket " + IntegrityRules.Describe(cls, bucket) + " confirmed different (two beacons "
                                                + IntegrityRules.Format(_confirmSeconds) + " s or more apart); detail requested");
                return RepairAction.RequestDetail;
            }
        }

        // ================================================================== bucket details (level 1 → 2/3/4)

        /// <summary>
        /// Compares a bucket detail with the ledger. Returns what was queued; <paramref name="extras"/> lists the slots the
        /// client holds in the bucket that the host does not list (they are also queued for an entity request, which the
        /// host answers with "not present"). A detail whose epoch the client has not applied yet is kept until
        /// <see cref="OnEpochApplied"/> reaches it (then <see cref="RepairAction.AwaitConfirmation"/> and no extras).
        /// </summary>
        public RepairAction OnDetail(HashDetailMessage detail, out NetSlot[] extras)
        {
            extras = Array.Empty<NetSlot>();
            lock (_gate)
            {
                if (!ValidateDetail(detail, out string problem))
                {
                    Warn("bucket detail ignored: " + problem);
                    return RepairAction.None;
                }
                if (_epochTracking && !detail.AtEpoch.IsNone && detail.AtEpoch > _lastApplied)
                {
                    if (_deferredDetails.Count >= Math.Max(1, _thresholds.MaxDeferredDetails))
                    {
                        HashDetailMessage dropped = _deferredDetails[0];
                        _deferredDetails.RemoveAt(0);
                        Warn("too many bucket details wait for their epoch; dropped " + IntegrityRules.Describe(dropped.Class, dropped.Bucket));
                    }
                    _deferredDetails.Add(detail);
                    return RepairAction.AwaitConfirmation;
                }
                return ProcessDetail(detail, out extras);
            }
        }

        /// <summary>
        /// The client applied every epoch up to <paramref name="lastApplied"/> (call after each applied plan): bucket details
        /// waiting for that epoch are processed now. Calling it once switches epoch alignment on; without it details are
        /// processed on arrival.
        /// </summary>
        public void OnEpochApplied(EpochId lastApplied)
        {
            lock (_gate)
            {
                _epochTracking = true;
                if (lastApplied > _lastApplied) _lastApplied = lastApplied;
                if (_deferredDetails.Count == 0) return;
                for (int i = 0; i < _deferredDetails.Count;)
                {
                    HashDetailMessage detail = _deferredDetails[i];
                    if (detail.AtEpoch > _lastApplied)
                    {
                        i++;
                        continue;
                    }
                    _deferredDetails.RemoveAt(i);
                    RepairAction action = ProcessDetail(detail, out NetSlot[] extras);
                    if (_log != null && _log.IsEnabled(LogLevel.Debug))
                    {
                        _log.Debug(LogCategory.Integrity, "repair: deferred detail " + IntegrityRules.Describe(detail.Class, detail.Bucket) + " processed at "
                                                         + _lastApplied + ": " + action + ", " + IntegrityRules.Format(extras.Length) + " extra slot(s)");
                    }
                }
            }
        }

        // ================================================================== leaks (level 0)

        /// <summary>A live value differs from the ledger outside every capture set; repaired once reported in two different frames.</summary>
        public void OnLeak(NetSlot slot, TypeRank type, double now) => OnLeak(slot, type, now, false);

        /// <summary>
        /// As <see cref="OnLeak(NetSlot, TypeRank, double)"/>; <paramref name="immediate"/> skips the two-frame wait (a
        /// difference found right after the end-of-frame re-application, docs/history/ARCHITECTURE-original.md §4.7.8). Pending and tolerated pairs are ignored.
        /// </summary>
        public void OnLeak(NetSlot slot, TypeRank type, double now, bool immediate)
        {
            if (slot.IsNone) return;
            lock (_gate)
            {
                ulong key = IntegrityRules.PairKey(slot, type);
                if (IsPendingSafe(slot, type))
                {
                    _leakCandidates.Remove(key);
                    return;
                }
                if (IsToleratedLocked(slot, type, now)) return;
                if (!immediate)
                {
                    if (!_leakCandidates.TryGetValue(key, out double firstSeen) || now - firstSeen > _thresholds.LeakCandidateSeconds)
                    {
                        _leakCandidates[key] = now;                         // first frame (or a stale candidate): wait for the next frame
                        return;
                    }
                    if (now <= firstSeen) return;                         // the same frame: not two frames yet
                }
                _leakCandidates.Remove(key);
                ReplicationClass cls = ClassOfPair(slot, type);
                _stats.Leaks++;
                QueueEntity(_classes[(int)cls], slot, true);
                if (_log != null && _log.IsEnabled(LogLevel.Debug))
                {
                    _log.Debug(LogCategory.Integrity, "repair: leak " + slot + " " + type + " (" + cls + ")" + (immediate ? " after re-application" : " persisted two frames") + "; repair queued");
                }
            }
        }

        // ================================================================== repair outcomes and tolerance

        /// <summary>
        /// A repaired value was applied and re-checked: a pair that still mismatches after two repairs is masked for 10
        /// minutes (tolerance), which replaces escalation. Pending pairs never count as failed.
        /// </summary>
        public void OnRepairApplied(NetSlot slot, TypeRank type, bool stillMismatching, double now)
        {
            if (slot.IsNone) return;
            lock (_gate)
            {
                ulong key = IntegrityRules.PairKey(slot, type);
                if (!stillMismatching)
                {
                    _pairFailures.Remove(key);
                    return;
                }
                if (IsPendingSafe(slot, type) || IsToleratedLocked(slot, type, now)) return;
                _pairFailures.TryGetValue(key, out int failures);
                failures++;
                if (failures < Math.Max(1, _thresholds.FailedRepairsBeforeTolerance))
                {
                    _pairFailures[key] = failures;
                    return;
                }
                _pairFailures.Remove(key);
                ReplicationClass cls = ClassOfPair(slot, type);
                TolerateLocked(key, true, slot, type, cls, now, "still mismatching after " + IntegrityRules.Format(failures) + " repairs");
            }
        }

        /// <summary>True while the pair, or its whole slot, is on the tolerance list.</summary>
        public bool IsTolerated(NetSlot slot, TypeRank type, double now)
        {
            lock (_gate)
            {
                return IsToleratedLocked(slot, type, now);
            }
        }

        // ================================================================== requests

        /// <summary>
        /// Appends the requests due now: per class at most one per coalescing interval, within the request rate; slots of a
        /// class are taken in arrival order, at most the per-request slot limit each. Returns how many were appended.
        /// </summary>
        public int TakeRequests(double now, List<RepairRequestMessage> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            lock (_gate)
            {
                Housekeeping(now);
                if (!double.IsNaN(_throttledUntil))
                {
                    if (now < _throttledUntil)
                    {
                        if (AnythingPending()) _stats.RequestsThrottled++;
                        return 0;
                    }
                    if (now >= _throttledUntil + BackoffResetSeconds)
                    {
                        _throttledUntil = double.NaN;
                        _backoff = 0;
                    }
                }
                int emitted = 0;
                if (_pendingSnapshot)
                {
                    if (!_requestWindow.TryAdd(now, _maxRequestsPerSecond))
                    {
                        _stats.RequestsThrottled++;
                        return 0;
                    }
                    output.Add(new RepairRequestMessage { Class = ReplicationClass.S, Slots = Array.Empty<NetSlot>(), LevelHint = RepairLevel.Snapshot });
                    _pendingSnapshot = false;
                    emitted++;
                }
                int count = AuditedClasses.Length;
                for (int n = 0; n < count; n++)
                {
                    ClassState cs = _classes[(int)AuditedClasses[(_classRotation + n) % count]];
                    if (!cs.HasPending) continue;
                    if (now - cs.LastRequestAt < _thresholds.CoalesceSeconds) continue;
                    if (!_requestWindow.TryAdd(now, _maxRequestsPerSecond))
                    {
                        _stats.RequestsThrottled++;
                        break;
                    }
                    output.Add(BuildRequest(cs));
                    cs.LastRequestAt = now;
                    emitted++;
                }
                _classRotation = (_classRotation + 1) % count;
                return emitted;
            }
        }

        /// <summary>
        /// The host dropped a request (control message <c>RepairThrottled</c>): requests pause for 1 s, doubling up to 8 s
        /// while notices keep coming. Nothing is lost: buckets that still mismatch are confirmed again and leaks are
        /// reported again.
        /// </summary>
        public void OnHostThrottled(double now)
        {
            lock (_gate)
            {
                _backoff = _backoff <= 0 ? 1 : Math.Min(_backoff * 2, MaxBackoffSeconds);
                _throttledUntil = now + _backoff;
                _stats.RequestsThrottled++;
                _log.Info(LogCategory.Integrity, "repair: the host throttled a request; pausing requests for " + IntegrityRules.Format(_backoff) + " s");
            }
        }

        // ================================================================== level 3 / 4 hints

        /// <summary>True while a rebaseline of the class is wanted (a hint; the host decides).</summary>
        public bool WantsRebaseline(ReplicationClass cls, out string reason)
        {
            reason = null;
            if (!IntegrityRules.IsAudited(cls)) return false;
            lock (_gate)
            {
                ClassState cs = _classes[(int)cls];
                if (!cs.RebaselineWanted) return false;
                if (_clock.Seconds - cs.RebaselineRaisedAt >= _thresholds.HintCooldownSeconds)
                {
                    cs.RebaselineWanted = false;
                    return false;
                }
                reason = cs.RebaselineReason;
                return true;
            }
        }

        /// <summary>True while a snapshot resync is wanted (a hint; the host decides).</summary>
        public bool WantsResync(out string reason)
        {
            reason = null;
            lock (_gate)
            {
                if (!_resyncWanted) return false;
                if (_clock.Seconds - _resyncRaisedAt >= _thresholds.HintCooldownSeconds)
                {
                    _resyncWanted = false;
                    return false;
                }
                reason = _resyncReason;
                return true;
            }
        }

        /// <summary>
        /// A domain rebaseline of the class finished (every record re-sent, extras deleted): the class's bucket state,
        /// queued requests and tolerance escalation start over. A second rebaseline of the class within 10 minutes is a
        /// snapshot hint.
        /// </summary>
        public void OnRebaselineCompleted(ReplicationClass cls)
        {
            if (!IntegrityRules.IsAudited(cls)) return;
            lock (_gate)
            {
                double now = _clock.Seconds;
                ClassState cs = _classes[(int)cls];
                cs.RebaselineWanted = false;
                cs.PendingRebaseline = false;
                cs.CompletedRebaselines.Add(now);
                cs.LastRebaselineCompletedAt = now;
                cs.ToleratedSinceRebaseline = 0;
                cs.BucketRepairs.Clear();
                cs.ClearPendingSlots();
                cs.ClearPendingDetails();
                RemoveBucketsOf(cls);
                _log.Info(LogCategory.Integrity, "repair: rebaseline of class " + cls + " completed");
            }
        }

        /// <summary>
        /// A value-codec exception quarantined <paramref name="type"/> (docs/history/ARCHITECTURE-original.md §12.2): its class is rebaselined (level 3); a
        /// second fault of the same type within 10 minutes is a snapshot hint.
        /// </summary>
        public RepairAction ReportCodecFault(ReplicationClass cls, TypeRank type, double now)
        {
            if (!IntegrityRules.IsAudited(cls)) return RepairAction.None;
            lock (_gate)
            {
                bool repeated = _lastCodecFault.TryGetValue(type.Value, out double last) && now - last < CodecFaultWindowSeconds;
                _lastCodecFault[type.Value] = now;
                return repeated
                    ? RaiseResyncLocked("second value-codec fault of " + type + " within 10 minutes")
                    : RaiseRebaselineLocked(_classes[(int)cls], "value-codec fault of " + type + " (type quarantined)");
            }
        }

        /// <summary>Forgets every bucket, request, leak, tolerance and deferred detail (after a snapshot resync or a session reset); keeps the statistics.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _buckets.Clear();
                _leakCandidates.Clear();
                _pairFailures.Clear();
                _toleratedPairs.Clear();
                _toleratedSlots.Clear();
                _lastCodecFault.Clear();
                _deferredDetails.Clear();
                _requestWindow.Clear();
                foreach (ReplicationClass cls in AuditedClasses) _classes[(int)cls] = new ClassState(cls);
                _lastApplied = EpochId.None;
                _epochTracking = false;
                _throttledUntil = double.NaN;
                _backoff = 0;
                _resyncWanted = false;
                _resyncReason = null;
                _resyncRaisedAt = double.NaN;
                _pendingSnapshot = false;
            }
        }

        public RepairStats GetStats()
        {
            lock (_gate)
            {
                return _stats;
            }
        }

        // ================================================================== internals: details

        private RepairAction ProcessDetail(HashDetailMessage detail, out NetSlot[] extras)
        {
            double now = _clock.Seconds;
            ReplicationClass cls = detail.Class;
            int bucket = detail.Bucket;
            ClassState cs = _classes[(int)cls];
            long key = IntegrityRules.BucketKey(cls, bucket);
            if (!_buckets.TryGetValue(key, out BucketState state))
            {
                state = new BucketState(cls, bucket);                     // unsolicited: track repair attempts, count nothing
                _buckets.Add(key, state);
            }
            bool confirmed = state.Confirmed;
            if (state.DetailQueued) cs.RemovePendingDetail(bucket);
            state.DetailQueued = false;
            state.DetailRequestedAt = double.NaN;
            state.FirstMismatchAt = double.NaN;

            var differing = new List<NetSlot>();
            var extraList = new List<NetSlot>();
            int missing = 0;
            int masked = 0;
            int skippedNewer = 0;
            if (_detail == null)
            {
                if (!_degradedWarned)
                {
                    _degradedWarned = true;
                    _log.Warn(LogCategory.Integrity, "repair: the ledger offers no per-slot view (IHashLedgerDetail); bucket details re-send every listed slot");
                }
                int n = detail.Slots == null ? 0 : detail.Slots.Length;
                for (int i = 0; i < n; i++)
                {
                    if (IsSlotToleratedLocked(detail.Slots[i].Value, now)) masked++;
                    else differing.Add(detail.Slots[i]);
                }
            }
            else
            {
                CompareWithLedger(detail, now, differing, extraList, ref missing, ref masked, ref skippedNewer);
            }

            int found = differing.Count + extraList.Count;
            extras = extraList.Count == 0 ? Array.Empty<NetSlot>() : extraList.ToArray();
            if (found == 0)
            {
                if (masked == 0 && skippedNewer == 0)
                {
                    RemoveBucket(key, state);                             // matches slot by slot: the beacon mismatches were transient
                    return RepairAction.None;
                }
                state.SlotAttempts?.Clear();
                return masked > 0 ? RepairAction.Tolerate : RepairAction.None;
            }

            if (confirmed)
            {
                _stats.ConfirmedMismatches += found;
                if (cls == ReplicationClass.S && (missing > 0 || extraList.Count > 0)) state.Structural = true;
            }

            RepairAction action;
            if (confirmed && found > _thresholds.EntityRepairLimit)
            {
                action = RaiseRebaselineLocked(cs, IntegrityRules.Format(found) + " confirmed mismatches in bucket " + IntegrityRules.Describe(cls, bucket));
            }
            else
            {
                int queued = QueueDetailRepairs(cs, state, differing, extraList, now);
                action = queued > 0 ? RepairAction.RequestEntities : RepairAction.Tolerate;
                if (confirmed && queued > 0)
                {
                    cs.BucketRepairs.Add(now);
                    if (cs.BucketRepairs.Count(now) > _thresholds.BucketRepairsPerMinuteLimit)
                    {
                        action = Escalate(action, RaiseRebaselineLocked(cs, "more than " + IntegrityRules.Format(_thresholds.BucketRepairsPerMinuteLimit)
                                                                            + " confirmed bucket repairs within a minute in class " + cls));
                    }
                }
            }

            if (confirmed)
            {
                int span = SpanOf(cs);
                int confirmedBuckets = CountBuckets(cls, now, false);
                if (confirmedBuckets >= _thresholds.MinConfirmedBucketsForFraction && confirmedBuckets > _thresholds.ClassBucketFraction * span)
                {
                    action = Escalate(action, RaiseRebaselineLocked(cs, IntegrityRules.Format(confirmedBuckets) + " of " + IntegrityRules.Format(span)
                                                                        + " buckets of class " + cls + " confirmed different"));
                }
                if (cls == ReplicationClass.S)
                {
                    int structural = CountBuckets(cls, now, true);
                    if (structural >= _thresholds.MinStructuralBucketsForResync && structural > _thresholds.StructuralFraction * span)
                    {
                        action = Escalate(action, RaiseResyncLocked("structural mismatch in " + IntegrityRules.Format(structural) + " of "
                                                                    + IntegrityRules.Format(span) + " S buckets"));
                    }
                }
            }

            _log.Info(LogCategory.Integrity, "repair: detail " + IntegrityRules.Describe(cls, bucket) + " at " + detail.AtEpoch + ": "
                                            + IntegrityRules.Format(differing.Count) + " differing (" + IntegrityRules.Format(missing) + " missing), "
                                            + IntegrityRules.Format(extraList.Count) + " extra, " + IntegrityRules.Format(masked) + " tolerated, "
                                            + IntegrityRules.Format(skippedNewer) + " newer locally" + (confirmed ? string.Empty : " (unsolicited)") + " → " + action);
            return action;
        }

        /// <summary>Merge walk over the host's slots (sorted here) and the ledger's slots of the bucket (ascending).</summary>
        private void CompareWithLedger(HashDetailMessage detail, double now, List<NetSlot> differing, List<NetSlot> extras,
                                       ref int missing, ref int masked, ref int skippedNewer)
        {
            int n = detail.Slots == null ? 0 : detail.Slots.Length;
            var hostSlots = new uint[n];
            var hostHashes = new uint[n];
            for (int i = 0; i < n; i++)
            {
                hostSlots[i] = detail.Slots[i].Value;
                hostHashes[i] = detail.Hashes[i];
            }
            Array.Sort(hostSlots, hostHashes);
            _scratchSlots.Clear();
            _detail.CollectBucketSlots(detail.Class, detail.Bucket, _scratchSlots);
            int h = 0;
            int c = 0;
            uint previousHost = 0;
            while (h < n || c < _scratchSlots.Count)
            {
                if (h < n && hostSlots[h] == previousHost && previousHost != 0)
                {
                    h++;                                                    // duplicate host entry: the first one counts
                    continue;
                }
                uint hs = h < n ? hostSlots[h] : uint.MaxValue;
                uint cs = c < _scratchSlots.Count ? _scratchSlots[c].Value : uint.MaxValue;
                if (hs < cs)
                {
                    previousHost = hs;
                    if (Classify(new NetSlot(hs), detail.AtEpoch, now, ref masked, ref skippedNewer))
                    {
                        differing.Add(new NetSlot(hs));                     // the host has it, the ledger has nothing of this class
                        missing++;
                    }
                    h++;
                }
                else if (cs < hs)
                {
                    if (Classify(_scratchSlots[c], detail.AtEpoch, now, ref masked, ref skippedNewer)) extras.Add(_scratchSlots[c]);
                    c++;
                }
                else
                {
                    previousHost = hs;
                    var slot = new NetSlot(hs);
                    bool equal = _detail.TryGetSlotDigest(detail.Class, slot, out uint digest) && digest == hostHashes[h];
                    if (!equal && Classify(slot, detail.AtEpoch, now, ref masked, ref skippedNewer)) differing.Add(slot);
                    h++;
                    c++;
                }
            }
        }

        /// <summary>True when a differing slot counts: not tolerated and not changed locally after the detail's epoch.</summary>
        private bool Classify(NetSlot slot, EpochId atEpoch, double now, ref int masked, ref int skippedNewer)
        {
            if (!atEpoch.IsNone && _detail.SlotModifiedEpoch(slot) > atEpoch)
            {
                skippedNewer++;
                return false;
            }
            if (IsSlotToleratedLocked(slot.Value, now))
            {
                masked++;
                return false;
            }
            return true;
        }

        /// <summary>Queues entity re-sends; a slot that differs in a third consecutive detail is tolerated instead.</summary>
        private int QueueDetailRepairs(ClassState cs, BucketState state, List<NetSlot> differing, List<NetSlot> extras, double now)
        {
            Dictionary<uint, int> previous = state.SlotAttempts;
            var attempts = new Dictionary<uint, int>(differing.Count + extras.Count);
            int queued = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                List<NetSlot> slots = pass == 0 ? differing : extras;
                foreach (NetSlot slot in slots)
                {
                    int made = 0;
                    if (previous != null) previous.TryGetValue(slot.Value, out made);
                    if (made >= Math.Max(1, _thresholds.FailedRepairsBeforeTolerance))
                    {
                        TolerateLocked(slot.Value, false, slot, default, cs.Class, now,
                                       "still differs from the host after " + IntegrityRules.Format(made) + " entity re-sends (bucket detail)");
                        continue;
                    }
                    attempts[slot.Value] = made + 1;
                    if (QueueEntity(cs, slot, false)) queued++;
                }
            }
            state.SlotAttempts = attempts;                                 // only consecutive details count as repeated failures
            _stats.EntityRepairs += queued;
            return queued;
        }

        private static bool ValidateDetail(HashDetailMessage detail, out string problem)
        {
            problem = null;
            if (detail == null)
            {
                problem = "null";
                return false;
            }
            if (!IntegrityRules.IsAudited(detail.Class))
            {
                problem = "class " + (int)detail.Class + " has no bucket audit";
                return false;
            }
            if (!IntegrityRules.IsValidBucket(detail.Bucket))
            {
                problem = "bucket " + IntegrityRules.Format(detail.Bucket) + " out of range";
                return false;
            }
            int slots = detail.Slots == null ? 0 : detail.Slots.Length;
            int hashes = detail.Hashes == null ? 0 : detail.Hashes.Length;
            if (slots != hashes)
            {
                problem = IntegrityRules.Format(slots) + " slots but " + IntegrityRules.Format(hashes) + " hashes";
                return false;
            }
            if (slots > Limits.MaxHashDetailSlots)
            {
                problem = IntegrityRules.Format(slots) + " slots in one bucket";
                return false;
            }
            for (int i = 0; i < slots; i++)
            {
                NetSlot slot = detail.Slots[i];
                if (slot.IsNone || slot.Bucket != detail.Bucket)
                {
                    problem = slot + " is not a slot of bucket " + IntegrityRules.Format(detail.Bucket);
                    return false;
                }
            }
            return true;
        }

        // ================================================================== internals: queues and requests

        private void QueueDetail(ClassState cs, BucketState state)
        {
            if (cs.AddPendingDetail(state.Bucket))
            {
                if (cs.PendingDetailCount > 1) _stats.RequestsCoalesced++;
            }
            else
            {
                _stats.RequestsCoalesced++;
            }
            state.DetailQueued = true;
        }

        /// <summary>Queues a slot for an entity request; false when it was already queued or the class queue is full.</summary>
        private bool QueueEntity(ClassState cs, NetSlot slot, bool leak)
        {
            if (!leak) cs.PendingSlotsAreLeaks = false;
            if (cs.ContainsPendingSlot(slot.Value))
            {
                _stats.RequestsCoalesced++;
                return false;
            }
            if (cs.PendingSlotCount >= Math.Max(1, _thresholds.MaxPendingSlotsPerClass))
            {
                Warn("repair queue of class " + cs.Class + " is full; " + slot + " waits for the next detail or leak report");
                return false;
            }
            if (cs.PendingSlotCount > 0) _stats.RequestsCoalesced++;
            if (cs.PendingSlotCount == 0) cs.PendingSlotsAreLeaks = leak;
            cs.AddPendingSlot(slot.Value);
            return true;
        }

        private RepairRequestMessage BuildRequest(ClassState cs)
        {
            if (cs.PendingRebaseline)
            {
                cs.PendingRebaseline = false;
                return new RepairRequestMessage { Class = cs.Class, Slots = Array.Empty<NetSlot>(), LevelHint = RepairLevel.Rebaseline };
            }
            if (cs.PendingSlotCount > 0)
            {
                bool leaks = cs.PendingSlotsAreLeaks;
                NetSlot[] slots = cs.TakePendingSlots(_maxSlotsPerRequest);
                if (cs.PendingSlotCount == 0) cs.PendingSlotsAreLeaks = true;
                return new RepairRequestMessage { Class = cs.Class, Slots = slots, LevelHint = leaks ? RepairLevel.Leak : RepairLevel.Entity };
            }
            int[] buckets = cs.TakePendingDetails(_maxSlotsPerRequest);
            var designators = new NetSlot[buckets.Length];
            double now = _clock.Seconds;
            for (int i = 0; i < buckets.Length; i++)
            {
                designators[i] = IntegrityRules.BucketDesignator(buckets[i]);
                if (_buckets.TryGetValue(IntegrityRules.BucketKey(cs.Class, buckets[i]), out BucketState state))
                {
                    state.DetailQueued = false;
                    state.DetailRequestedAt = now;
                }
            }
            return new RepairRequestMessage { Class = cs.Class, Slots = designators, LevelHint = RepairLevel.Bucket };
        }

        private bool AnythingPending()
        {
            if (_pendingSnapshot) return true;
            foreach (ReplicationClass cls in AuditedClasses)
            {
                if (_classes[(int)cls].HasPending) return true;
            }
            return false;
        }

        private void Housekeeping(double now)
        {
            if (_leakCandidates.Count > 0)
            {
                _scratchKeys.Clear();
                foreach (KeyValuePair<ulong, double> candidate in _leakCandidates)
                {
                    if (now - candidate.Value > _thresholds.LeakCandidateSeconds) _scratchKeys.Add(candidate.Key);
                }
                foreach (ulong key in _scratchKeys) _leakCandidates.Remove(key);
            }
            if (_toleratedPairs.Count > 0)
            {
                _scratchKeys.Clear();
                foreach (KeyValuePair<ulong, Tolerance> entry in _toleratedPairs)
                {
                    if (entry.Value.Until <= now) _scratchKeys.Add(entry.Key);
                }
                foreach (ulong key in _scratchKeys)
                {
                    Expire(_toleratedPairs[key]);
                    _toleratedPairs.Remove(key);
                }
            }
            if (_toleratedSlots.Count > 0)
            {
                _scratchKeys.Clear();
                foreach (KeyValuePair<uint, Tolerance> entry in _toleratedSlots)
                {
                    if (entry.Value.Until <= now) _scratchKeys.Add(entry.Key);
                }
                foreach (ulong key in _scratchKeys)
                {
                    Expire(_toleratedSlots[(uint)key]);
                    _toleratedSlots.Remove((uint)key);
                }
            }
        }

        private void Expire(Tolerance tolerance)
        {
            ClassState cs = _classes[(int)tolerance.Class];
            if (cs == null) return;
            if (double.IsNaN(cs.LastRebaselineCompletedAt) || tolerance.AddedAt > cs.LastRebaselineCompletedAt)
            {
                if (cs.ToleratedSinceRebaseline > 0) cs.ToleratedSinceRebaseline--;
            }
        }

        // ================================================================== internals: tolerance and hints

        private void TolerateLocked(ulong key, bool pair, NetSlot slot, TypeRank type, ReplicationClass cls, double now, string reason)
        {
            if (!IntegrityRules.IsAudited(cls)) cls = ReplicationClass.S;
            var tolerance = new Tolerance(now + Math.Max(0, _thresholds.ToleranceSeconds), cls, now);
            bool added;
            if (pair)
            {
                added = !_toleratedPairs.ContainsKey(key);
                _toleratedPairs[key] = tolerance;
            }
            else
            {
                added = !_toleratedSlots.ContainsKey(slot.Value);
                _toleratedSlots[slot.Value] = tolerance;
            }
            if (!added) return;
            _stats.Tolerated++;
            var text = new StringBuilder(160);
            text.Append("repair: tolerating ").Append(slot);
            if (pair)
            {
                text.Append(' ').Append(type);
                if (_ledger.TryGet(slot, type, out uint hash)) text.Append(" (ledger hash 0x").Append(hash.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)).Append(')');
            }
            else
            {
                text.Append(" (whole slot)");
            }
            text.Append(", class ").Append(cls).Append(", for ").Append(IntegrityRules.Format(_thresholds.ToleranceSeconds)).Append(" s: ").Append(reason);
            _log.Warn(LogCategory.Integrity, text.ToString());

            ClassState cs = _classes[(int)cls];
            cs.ToleratedSinceRebaseline++;
            long total = _detail != null ? _detail.TotalCount(cls) : ApproximateTotal(cls);
            if (cs.ToleratedSinceRebaseline >= _thresholds.MinToleratedForRebaseline && cs.ToleratedSinceRebaseline > _thresholds.ToleranceClassFraction * total)
            {
                RaiseRebaselineLocked(cs, IntegrityRules.Format(cs.ToleratedSinceRebaseline) + " tolerated pairs exceed "
                                          + IntegrityRules.Format(_thresholds.ToleranceClassFraction * 100) + " % of class " + cls);
            }
        }

        private bool IsToleratedLocked(NetSlot slot, TypeRank type, double now)
        {
            if (_toleratedPairs.TryGetValue(IntegrityRules.PairKey(slot, type), out Tolerance pair) && pair.Until > now) return true;
            return IsSlotToleratedLocked(slot.Value, now);
        }

        private bool IsSlotToleratedLocked(uint slot, double now)
        {
            return _toleratedSlots.TryGetValue(slot, out Tolerance whole) && whole.Until > now;
        }

        private RepairAction RaiseRebaselineLocked(ClassState cs, string reason)
        {
            double now = _clock.Seconds;
            if (cs.RebaselineWanted && now - cs.RebaselineRaisedAt < _thresholds.HintCooldownSeconds) return RepairAction.RequestRebaseline;
            if (cs.CompletedRebaselines.Any(now))
            {
                return RaiseResyncLocked("a second rebaseline of class " + cs.Class + " within 10 minutes would be needed (" + reason + ")");
            }
            cs.RebaselineWanted = true;
            cs.RebaselineReason = reason;
            cs.RebaselineRaisedAt = now;
            cs.PendingRebaseline = true;
            _stats.Rebaselines++;
            _log.Warn(LogCategory.Integrity, "repair: asking the host for a rebaseline of class " + cs.Class + ": " + reason);
            return RepairAction.RequestRebaseline;
        }

        private RepairAction RaiseResyncLocked(string reason)
        {
            double now = _clock.Seconds;
            if (_resyncWanted && now - _resyncRaisedAt < _thresholds.HintCooldownSeconds) return RepairAction.RequestSnapshot;
            _resyncWanted = true;
            _resyncReason = reason;
            _resyncRaisedAt = now;
            _pendingSnapshot = true;
            _stats.Resyncs++;
            _log.Warn(LogCategory.Integrity, "repair: asking the host for a snapshot resync: " + reason);
            return RepairAction.RequestSnapshot;
        }

        private static RepairAction Escalate(RepairAction current, RepairAction candidate) => Level(candidate) > Level(current) ? candidate : current;

        private static int Level(RepairAction action)
        {
            switch (action)
            {
                case RepairAction.AwaitConfirmation: return 1;
                case RepairAction.Tolerate: return 2;
                case RepairAction.RequestDetail: return 3;
                case RepairAction.RequestEntities: return 4;
                case RepairAction.RequestRebaseline: return 5;
                case RepairAction.RequestSnapshot: return 6;
                default: return 0;
            }
        }

        // ================================================================== internals: helpers

        private bool IsPendingSafe(NetSlot slot, TypeRank type)
        {
            if (_pending == null) return false;
            try
            {
                return _pending.IsPending(slot, type);
            }
            catch (Exception ex)
            {
                // Unknown state: treat as pending, so a failing view can never cause repair requests.
                Warn("repair: the pending-pairs view failed for " + slot + " " + type + " (" + ex.GetType().Name + ": " + ex.Message + ")");
                return true;
            }
        }

        /// <summary>The class a pair is counted in (from the ledger), else the class of another member of the slot, else S.</summary>
        private ReplicationClass ClassOfPair(NetSlot slot, TypeRank type)
        {
            if (_detail != null)
            {
                if (_detail.TryGetClass(slot, type, out ReplicationClass cls) && IntegrityRules.IsAudited(cls)) return cls;
                foreach (ReplicationClass candidate in AuditedClasses)
                {
                    if (_detail.TryGetSlotDigest(candidate, slot, out _)) return candidate;
                }
            }
            return ReplicationClass.S;
        }

        private int SpanOf(ClassState cs)
        {
            int span = cs.MaxBucketSeen + 1;
            if (_detail != null) span = Math.Max(span, _detail.KnownBucketCount(cs.Class));
            return Math.Max(span, 1);
        }

        private int CountBuckets(ReplicationClass cls, double now, bool structuralOnly)
        {
            int count = 0;
            foreach (BucketState state in _buckets.Values)
            {
                if (state.Class != cls || !state.Confirmed) continue;
                if (now - state.ConfirmedAt > _thresholds.ConfirmedBucketLifetimeSeconds) continue;
                if (structuralOnly && !state.Structural) continue;
                count++;
            }
            return count;
        }

        private long ApproximateTotal(ReplicationClass cls)
        {
            long total = 0;
            int span = SpanOf(_classes[(int)cls]);
            for (int b = 0; b < span; b++) total += _ledger.BucketCount(cls, b);
            return total;
        }

        private void RemoveBucket(long key, BucketState state)
        {
            if (state.DetailQueued) _classes[(int)state.Class].RemovePendingDetail(state.Bucket);
            _buckets.Remove(key);
        }

        private void RemoveBucketsOf(ReplicationClass cls)
        {
            var keys = new List<long>();
            foreach (KeyValuePair<long, BucketState> entry in _buckets)
            {
                if (entry.Value.Class == cls) keys.Add(entry.Key);
            }
            foreach (long key in keys) _buckets.Remove(key);
        }

        private void Warn(string message)
        {
            _warnings++;
            if (_warnings <= WarningsBeforeSampling || _warnings % 1000 == 0)
            {
                _log.Warn(LogCategory.Integrity, message + (_warnings == WarningsBeforeSampling ? " (further repair warnings are sampled)" : string.Empty));
            }
        }

        // ================================================================== state types

        private readonly struct Tolerance
        {
            internal readonly double Until;
            internal readonly ReplicationClass Class;
            internal readonly double AddedAt;

            internal Tolerance(double until, ReplicationClass cls, double addedAt)
            {
                Until = until;
                Class = cls;
                AddedAt = addedAt;
            }
        }

        private sealed class BucketState
        {
            internal readonly ReplicationClass Class;
            internal readonly int Bucket;
            internal double FirstMismatchAt = double.NaN;
            internal bool Confirmed;
            internal double ConfirmedAt = double.NaN;
            internal bool DetailQueued;
            internal double DetailRequestedAt = double.NaN;
            internal bool Structural;
            internal Dictionary<uint, int> SlotAttempts;

            internal BucketState(ReplicationClass cls, int bucket)
            {
                Class = cls;
                Bucket = bucket;
            }
        }

        private sealed class ClassState
        {
            internal readonly ReplicationClass Class;
            internal readonly TimeWindow BucketRepairs = new TimeWindow(BucketRepairWindowSeconds);
            internal readonly TimeWindow CompletedRebaselines = new TimeWindow(RepeatRebaselineWindowSeconds);
            internal bool PendingSlotsAreLeaks = true;
            internal bool PendingRebaseline;
            internal bool RebaselineWanted;
            internal string RebaselineReason;
            internal double RebaselineRaisedAt = double.NaN;
            internal double LastRequestAt = double.NegativeInfinity;
            internal double LastRebaselineCompletedAt = double.NaN;
            internal int ToleratedSinceRebaseline;
            internal int MaxBucketSeen = -1;
            private readonly List<uint> _slotOrder = new List<uint>();
            private readonly HashSet<uint> _slotSet = new HashSet<uint>();
            private readonly List<int> _detailOrder = new List<int>();
            private readonly HashSet<int> _detailSet = new HashSet<int>();

            internal ClassState(ReplicationClass cls)
            {
                Class = cls;
            }

            internal bool HasPending => PendingRebaseline || _slotOrder.Count > 0 || _detailOrder.Count > 0;

            internal int PendingSlotCount => _slotOrder.Count;

            internal int PendingDetailCount => _detailOrder.Count;

            internal bool ContainsPendingSlot(uint slot) => _slotSet.Contains(slot);

            internal void AddPendingSlot(uint slot)
            {
                _slotSet.Add(slot);
                _slotOrder.Add(slot);
            }

            internal NetSlot[] TakePendingSlots(int max)
            {
                int n = Math.Min(max, _slotOrder.Count);
                var slots = new NetSlot[n];
                for (int i = 0; i < n; i++)
                {
                    slots[i] = new NetSlot(_slotOrder[i]);
                    _slotSet.Remove(_slotOrder[i]);
                }
                _slotOrder.RemoveRange(0, n);
                return slots;
            }

            internal void ClearPendingSlots()
            {
                _slotOrder.Clear();
                _slotSet.Clear();
                PendingSlotsAreLeaks = true;
            }

            internal bool AddPendingDetail(int bucket)
            {
                if (!_detailSet.Add(bucket)) return false;
                _detailOrder.Add(bucket);
                return true;
            }

            internal void RemovePendingDetail(int bucket)
            {
                if (_detailSet.Remove(bucket)) _detailOrder.Remove(bucket);
            }

            internal int[] TakePendingDetails(int max)
            {
                int n = Math.Min(max, _detailOrder.Count);
                var buckets = new int[n];
                for (int i = 0; i < n; i++)
                {
                    buckets[i] = _detailOrder[i];
                    _detailSet.Remove(_detailOrder[i]);
                }
                _detailOrder.RemoveRange(0, n);
                return buckets;
            }

            internal void ClearPendingDetails()
            {
                _detailOrder.Clear();
                _detailSet.Clear();
            }
        }
    }
}
