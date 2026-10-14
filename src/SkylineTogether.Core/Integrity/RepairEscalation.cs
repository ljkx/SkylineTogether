using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// The escalation above the client's object repairs (ARCHITECTURE.md#escalation-and-resync-offer,
    /// for the rolling verification of <see cref="DivergenceTracker"/> and <see cref="EntityDigest"/> buckets).
    /// <list type="bullet">
    /// <item><b>Bucket rebaseline</b> (level 3 for one bucket): when a bucket detail shows more differences than object
    /// repair is meant for (<see cref="RepairThresholds.EntityRepairLimit"/>), or a bucket still differs after its repairs
    /// for <see cref="StuckRoundsBeforeRebaseline"/> detail rounds, the client asks the host to re-send every object of
    /// the bucket (and to delete what the host does not have). At most once per bucket per
    /// <see cref="BucketCooldownSeconds"/> and at most <see cref="MaxRebaselinesPerMinute"/> buckets a minute, so it can
    /// never turn into a re-send loop.</item>
    /// <item><b>Resync offer</b> (level 4): when the divergence is massive (more than
    /// <see cref="RepairThresholds.ClassBucketFraction"/> of the compared buckets mismatch in
    /// <see cref="ResyncAfterCycles"/> consecutive verification cycles of at least <see cref="MinBucketsForResync"/>
    /// buckets, or <see cref="RebaselinesForResync"/> bucket rebaselines within 10 minutes, or the tracker gave up on many
    /// objects), the player is offered a rejoin with a fresh snapshot. Only offered, never forced; at most once per
    /// <see cref="ResyncCooldownSeconds"/>, and after <see cref="MaxResyncsPerHour"/> accepted resyncs within an hour no
    /// more offers (a deterministic fault would loop).</item>
    /// </list>
    /// <see cref="Reset"/> (a new session, also the rejoin of a resync) keeps the offer history, so the guards hold across
    /// rejoins. Pure logic: time is passed in. Main thread only.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class RepairEscalation
    {
        /// <summary>What the caller should do after a bucket detail.</summary>
        public enum Step : byte
        {
            None,
            /// <summary>Re-send every object of the bucket (host list and local list).</summary>
            RebaselineBucket
        }

        private const double RebaselineWindowSeconds = 600;

        private readonly RepairThresholds _thresholds;
        private readonly Dictionary<uint, BucketState> _buckets = new Dictionary<uint, BucketState>();
        private readonly TimeWindow _rebaselines = new TimeWindow(60);
        private readonly TimeWindow _recentRebaselines = new TimeWindow(RebaselineWindowSeconds);
        private readonly TimeWindow _acceptedResyncs = new TimeWindow(3600);
        private double _lastOfferAt = double.NegativeInfinity;
        private int _cyclesOver;
        private string _pendingOffer;

        public RepairEscalation(RepairThresholds thresholds = null)
        {
            _thresholds = thresholds ?? new RepairThresholds();
        }

        /// <summary>Detail rounds in which a bucket still differs after it was repaired before it is rebaselined.</summary>
        public int StuckRoundsBeforeRebaseline = 2;

        /// <summary>A bucket is rebaselined at most once per this many seconds.</summary>
        public double BucketCooldownSeconds = 600;

        /// <summary>Bucket rebaselines per minute at most (each is up to 128 objects).</summary>
        public int MaxRebaselinesPerMinute = 4;

        /// <summary>A verification cycle must have compared at least this many buckets to count for a resync.</summary>
        public int MinBucketsForResync = 20;

        /// <summary>Consecutive cycles above the mismatch fraction before a resync is offered.</summary>
        public int ResyncAfterCycles = 2;

        /// <summary>Bucket rebaselines within 10 minutes that make a resync offer.</summary>
        public int RebaselinesForResync = 8;

        /// <summary>Objects the tracker gave up on that make a resync offer (and at least 1 % of the verified objects).</summary>
        public int GivenUpForResync = 50;

        /// <summary>A declined or accepted offer is not repeated for this many seconds.</summary>
        public double ResyncCooldownSeconds = 1800;

        /// <summary>Accepted resyncs within an hour after which no more are offered.</summary>
        public int MaxResyncsPerHour = 2;

        public RepairThresholds Thresholds => _thresholds;

        /// <summary>Bucket rebaselines requested so far (kept by <see cref="Reset"/>).</summary>
        public long Rebaselines { get; private set; }

        /// <summary>Resync offers made so far (kept by <see cref="Reset"/>).</summary>
        public long ResyncOffers { get; private set; }

        /// <summary>The reason of an offer the player has not answered yet, or null.</summary>
        public string PendingOffer => _pendingOffer;

        private sealed class BucketState
        {
            public bool Repaired;
            public int StuckRounds;
            public double LastRebaseline = double.NegativeInfinity;
        }

        /// <summary>
        /// A bucket detail was compared: <paramref name="differences"/> objects differ (confirmed or not) and
        /// <paramref name="repairsRequested"/> of them were sent for repair now.
        /// </summary>
        public Step OnDetail(uint bucket, int differences, int repairsRequested, double now)
        {
            if (differences <= 0)
            {
                _buckets.Remove(bucket);
                return Step.None;
            }
            if (!_buckets.TryGetValue(bucket, out BucketState state))
            {
                state = new BucketState();
                _buckets[bucket] = state;
                if (_buckets.Count > 4096) Prune(now);
            }
            if (state.Repaired) state.StuckRounds++;
            if (repairsRequested > 0) state.Repaired = true;
            bool massive = differences > _thresholds.EntityRepairLimit;
            if (!massive && state.StuckRounds < StuckRoundsBeforeRebaseline) return Step.None;
            if (now - state.LastRebaseline < BucketCooldownSeconds) return Step.None;
            if (_rebaselines.Count(now) >= MaxRebaselinesPerMinute) return Step.None;
            state.LastRebaseline = now;
            state.StuckRounds = 0;
            state.Repaired = false;
            _rebaselines.Add(now);
            _recentRebaselines.Add(now);
            Rebaselines++;
            if (_recentRebaselines.Count(now) >= RebaselinesForResync)
            {
                Offer(now, _recentRebaselines.Count(now).ToString(CultureInfo.InvariantCulture) + " areas of the city had to be re-sent within 10 minutes");
            }
            return Step.RebaselineBucket;
        }

        /// <summary>A bucket matched the host's digest.</summary>
        public void OnBucketMatched(uint bucket) => _buckets.Remove(bucket);

        /// <summary>
        /// A verification cycle ended: <paramref name="compared"/> bucket digests were compared, <paramref name="mismatched"/>
        /// of them differed; <paramref name="givenUp"/> objects the tracker gave up on so far, of <paramref name="verified"/>.
        /// </summary>
        public void OnCycle(int compared, int mismatched, long givenUp, long verified, double now)
        {
            if (compared >= MinBucketsForResync && mismatched > _thresholds.ClassBucketFraction * compared) _cyclesOver++;
            else _cyclesOver = 0;
            if (_cyclesOver >= ResyncAfterCycles)
            {
                Offer(now, mismatched.ToString(CultureInfo.InvariantCulture) + " of " + compared.ToString(CultureInfo.InvariantCulture)
                           + " checked areas still differed from the host after repairs, " + _cyclesOver.ToString(CultureInfo.InvariantCulture) + " checks in a row");
            }
            if (givenUp >= GivenUpForResync && givenUp * 100 >= verified)
            {
                Offer(now, givenUp.ToString(CultureInfo.InvariantCulture) + " objects could not be repaired");
            }
        }

        /// <summary>The player declined the offer (it is not repeated for <see cref="ResyncCooldownSeconds"/>).</summary>
        public void Decline(double now)
        {
            if (_pendingOffer == null) return;
            _pendingOffer = null;
            _lastOfferAt = now;
        }

        /// <summary>The player accepted the offer (the caller rejoins).</summary>
        public void Accept(double now)
        {
            _pendingOffer = null;
            _lastOfferAt = now;
            _acceptedResyncs.Add(now);
        }

        /// <summary>A new session: forgets the bucket state, keeps the offer history and the counters.</summary>
        public void Reset()
        {
            _buckets.Clear();
            _cyclesOver = 0;
            _pendingOffer = null;
        }

        private void Offer(double now, string reason)
        {
            if (_pendingOffer != null) return;
            if (now - _lastOfferAt < ResyncCooldownSeconds) return;
            if (_acceptedResyncs.Count(now) >= MaxResyncsPerHour) return;
            _pendingOffer = reason;
            _lastOfferAt = now;
            _cyclesOver = 0;
            ResyncOffers++;
        }

        private void Prune(double now)
        {
            var stale = new List<uint>();
            foreach (KeyValuePair<uint, BucketState> pair in _buckets)
            {
                if (!pair.Value.Repaired && now - pair.Value.LastRebaseline > BucketCooldownSeconds) stale.Add(pair.Key);
            }
            foreach (uint b in stale) _buckets.Remove(b);
        }
    }
}
