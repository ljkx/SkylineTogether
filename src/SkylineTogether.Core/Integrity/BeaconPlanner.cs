using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// Chooses the HASH beacons of each peer's epoch message (docs/history/ARCHITECTURE-original.md §8.4). A beacon carries the host's bucket sum and
    /// member count of one class and bucket; it must describe what that peer has <b>been sent</b>:
    /// <list type="bullet">
    /// <item>S and G (shared P0): the hash shadow is updated when a record is emitted (§4.4.5), so a beacon placed after
    /// the transaction is always consistent; these buckets are never skipped.</item>
    /// <item>V, A, A-low, M, K (per peer): a bucket is beaconed only when the peer has no dirty bits in it
    /// (<see cref="IPeerDirtyView.BucketHasDirty"/>); otherwise it is skipped this round. The engine calls
    /// <see cref="AppendBeacons"/> after the peer's value sections of the message were built and their bits cleared.</item>
    /// </list>
    /// Classes are enabled per peer by game-integrity after the class's first full scan cycle past the peer's join
    /// (§7.1 step 14, §7.6). The peer's beacon budget is a token bucket: <c>defaultPerSecond</c> (32 ≈ 0.5 KB/s), or
    /// <c>highRatePerSecond</c> (1024) while any class runs a high-rate sweep (post-join verification, hash resync). A
    /// high-rate class is served first and drops back to the default rate after one complete sweep over its buckets
    /// (every bucket visited once, beaconed or skipped). Cursors rotate through each class's buckets, so every bucket is
    /// eventually verified. Thread-safe: <see cref="AppendBeacons"/> runs on worker threads (possibly several peers in
    /// parallel), the other members on the main thread; bucket sums are read outside the capture window only.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class BeaconPlanner : IBeaconSource
    {
        /// <summary>Credits a peer can accumulate, in seconds of its current rate (at least one beacon).</summary>
        private const double BurstSeconds = 0.25;

        private static readonly ReplicationClass[] AuditedClasses =
        {
            ReplicationClass.S, ReplicationClass.G, ReplicationClass.V, ReplicationClass.A,
            ReplicationClass.ALow, ReplicationClass.M, ReplicationClass.K
        };

        private readonly object _gate = new object();
        private readonly BucketSums _sums;
        private readonly IClock _clock;
        private readonly int _defaultPerSecond;
        private readonly int _highRatePerSecond;
        private readonly Dictionary<byte, PeerState> _peers = new Dictionary<byte, PeerState>();
        private long _emitted;
        private long _skippedDirty;

        public BeaconPlanner(BucketSums sums, IClock clock, int defaultPerSecond = 32, int highRatePerSecond = 1024)
        {
            _sums = sums ?? throw new ArgumentNullException(nameof(sums));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (defaultPerSecond < 1) throw new ArgumentOutOfRangeException(nameof(defaultPerSecond), defaultPerSecond, "at least one beacon per second");
            if (highRatePerSecond < defaultPerSecond) throw new ArgumentOutOfRangeException(nameof(highRatePerSecond), highRatePerSecond, "the high rate must not be below the default rate");
            _defaultPerSecond = defaultPerSecond;
            _highRatePerSecond = highRatePerSecond;
        }

        /// <summary>Optional log (enable and disable events, ignored calls).</summary>
        public ILog Log { get; set; }

        /// <summary>Beacons appended since creation.</summary>
        public long BeaconsEmitted
        {
            get
            {
                lock (_gate) return _emitted;
            }
        }

        /// <summary>Buckets of per-peer classes skipped because the peer had dirty bits in them.</summary>
        public long BucketsSkippedDirty
        {
            get
            {
                lock (_gate) return _skippedDirty;
            }
        }

        /// <summary>True when beacons of <paramref name="cls"/> are enabled for the peer.</summary>
        public bool IsEnabled(PeerId peer, ReplicationClass cls)
        {
            if (!IntegrityRules.IsAudited(cls)) return false;
            lock (_gate)
            {
                return _peers.TryGetValue(peer.Value, out PeerState state) && state.Classes[(int)cls].Enabled;
            }
        }

        /// <summary>True while the class runs a high-rate sweep for the peer.</summary>
        public bool IsHighRate(PeerId peer, ReplicationClass cls)
        {
            if (!IntegrityRules.IsAudited(cls)) return false;
            lock (_gate)
            {
                return _peers.TryGetValue(peer.Value, out PeerState state) && state.Classes[(int)cls].Enabled && state.Classes[(int)cls].HighRate;
            }
        }

        /// <summary>Complete sweeps over the class's buckets for the peer since the class was enabled (high-rate and default).</summary>
        public int CompletedSweeps(PeerId peer, ReplicationClass cls)
        {
            if (!IntegrityRules.IsAudited(cls)) return 0;
            lock (_gate)
            {
                return _peers.TryGetValue(peer.Value, out PeerState state) ? state.Classes[(int)cls].CompletedSweeps : 0;
            }
        }

        /// <summary>Forgets the peer (disconnect, or before it rejoins from a snapshot).</summary>
        public void RemovePeer(PeerId peer)
        {
            lock (_gate)
            {
                _peers.Remove(peer.Value);
            }
        }

        /// <summary>
        /// Enables beacons of <paramref name="cls"/> for the peer. With <paramref name="highRate"/> a new complete sweep
        /// at the high rate starts (also for a class that is already enabled, e.g. a hash resync after a short drop).
        /// Classes without bucket audits (excluded, derived, channel, on-demand) are ignored.
        /// </summary>
        public void EnableClass(PeerId peer, ReplicationClass cls, bool highRate)
        {
            if (!IntegrityRules.IsAudited(cls))
            {
                Log.Warn(LogCategory.Integrity, "beacons: class " + cls + " has no bucket audit; EnableClass ignored for " + peer);
                return;
            }
            lock (_gate)
            {
                if (!_peers.TryGetValue(peer.Value, out PeerState state))
                {
                    state = new PeerState(_clock.Seconds);
                    _peers.Add(peer.Value, state);
                }
                ClassState cs = state.Classes[(int)cls];
                bool wasEnabled = cs.Enabled;
                cs.Enabled = true;
                if (highRate)
                {
                    cs.HighRate = true;
                    cs.SweepRemaining = Math.Max(1, _sums.BucketCount(cls));
                }
                else if (!wasEnabled)
                {
                    cs.HighRate = false;
                }
                if (!wasEnabled) cs.Cursor = 0;
            }
            Log.Info(LogCategory.Integrity, "beacons: " + cls + " enabled for " + peer + (highRate ? " (high-rate sweep)" : string.Empty));
        }

        /// <summary>Stops beacons of the class for the peer (e.g. while a rebaseline of that class streams).</summary>
        public void DisableClass(PeerId peer, ReplicationClass cls)
        {
            if (!IntegrityRules.IsAudited(cls)) return;
            lock (_gate)
            {
                if (_peers.TryGetValue(peer.Value, out PeerState state))
                {
                    ClassState cs = state.Classes[(int)cls];
                    cs.Enabled = false;
                    cs.HighRate = false;
                }
            }
        }

        /// <summary>
        /// Appends at most <paramref name="maxBeacons"/> beacons for the peer's next message, within the peer's rate;
        /// returns how many were appended. <paramref name="dirty"/> is the peer's dirty view (null = nothing dirty).
        /// </summary>
        [ThreadAffinity(ThreadKind.Worker)]
        public int AppendBeacons(PeerId peer, IPeerDirtyView dirty, List<HashBeacon> output, int maxBeacons)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (maxBeacons <= 0) return 0;
            lock (_gate)
            {
                if (!_peers.TryGetValue(peer.Value, out PeerState state)) return 0;
                double now = _clock.Seconds;
                bool high = state.AnyHighRate();
                if (!state.AnyEnabled())
                {
                    state.LastSeconds = now;
                    state.Credits = 0;
                    return 0;
                }
                int rate = high ? _highRatePerSecond : _defaultPerSecond;
                double elapsed = now - state.LastSeconds;
                if (elapsed > 0) state.Credits += rate * elapsed;
                state.LastSeconds = now;
                double burst = Math.Max(1.0, rate * BurstSeconds);
                if (state.Credits > burst) state.Credits = burst;
                int budget = (int)Math.Min(state.Credits, maxBeacons);
                if (budget <= 0) return 0;

                int emitted = 0;
                int examined = 0;
                int idle = 0;
                int examineLimit = budget * 8 + 64;                     // bounds the work when most buckets are dirty
                while (emitted < budget && examined < examineLimit)
                {
                    ClassState cs = state.NextClass(high);
                    if (cs == null) break;
                    examined++;
                    ReplicationClass cls = cs.Class;
                    int bucketCount = _sums.BucketCount(cls);
                    if (bucketCount <= 0)
                    {
                        CompleteSweepIfHighRate(cs);                    // nothing to verify in an empty class
                        if (high && !state.AnyHighRate()) break;
                        if (++idle >= AuditedClasses.Length) break;     // a whole rotation without any bucket
                        continue;
                    }
                    idle = 0;
                    if (cs.Cursor >= bucketCount) cs.Cursor = 0;
                    int bucket = cs.Cursor;
                    if (IsDirty(dirty, cls, bucket, peer))
                    {
                        _skippedDirty++;
                    }
                    else
                    {
                        output.Add(new HashBeacon { Class = cls, Bucket = bucket, Sum = _sums.Sum(cls, bucket), Count = _sums.Count(cls, bucket) });
                        emitted++;
                        state.Credits -= 1;
                    }
                    Advance(cs, bucketCount);
                    // The last high-rate sweep finished: default classes resume with the default rate from the next message.
                    if (high && !state.AnyHighRate()) break;
                }
                _emitted += emitted;
                return emitted;
            }
        }

        /// <summary>Per-peer classes skip buckets with dirty bits; a failing dirty view counts as dirty (never a wrong beacon).</summary>
        private bool IsDirty(IPeerDirtyView dirty, ReplicationClass cls, int bucket, PeerId peer)
        {
            if (dirty == null || !IntegrityRules.IsPerPeer(cls)) return false;
            try
            {
                return dirty.BucketHasDirty(bucket);
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.Integrity, "beacons: the dirty view of " + peer + " failed for " + IntegrityRules.Describe(cls, bucket) + "; bucket skipped", ex);
                return true;
            }
        }

        private static void Advance(ClassState cs, int bucketCount)
        {
            cs.Cursor++;
            if (cs.Cursor >= bucketCount)
            {
                cs.Cursor = 0;
                if (!cs.HighRate) cs.CompletedSweeps++;
            }
            if (cs.HighRate && --cs.SweepRemaining <= 0) CompleteSweepIfHighRate(cs);
        }

        private static void CompleteSweepIfHighRate(ClassState cs)
        {
            if (!cs.HighRate) return;
            cs.HighRate = false;
            cs.SweepRemaining = 0;
            cs.CompletedSweeps++;
        }

        private sealed class ClassState
        {
            internal readonly ReplicationClass Class;
            internal bool Enabled;
            internal bool HighRate;
            internal int Cursor;
            internal int SweepRemaining;
            internal int CompletedSweeps;

            internal ClassState(ReplicationClass cls)
            {
                Class = cls;
            }
        }

        private sealed class PeerState
        {
            internal readonly ClassState[] Classes = new ClassState[IntegrityRules.ClassCount];
            internal double Credits;
            internal double LastSeconds;
            private int _rotation;

            internal PeerState(double now)
            {
                for (int i = 0; i < Classes.Length; i++) Classes[i] = new ClassState((ReplicationClass)i);
                LastSeconds = now;
            }

            internal bool AnyEnabled()
            {
                foreach (ReplicationClass cls in AuditedClasses)
                {
                    if (Classes[(int)cls].Enabled) return true;
                }
                return false;
            }

            internal bool AnyHighRate()
            {
                foreach (ReplicationClass cls in AuditedClasses)
                {
                    ClassState cs = Classes[(int)cls];
                    if (cs.Enabled && cs.HighRate) return true;
                }
                return false;
            }

            /// <summary>The next enabled class in rotation; only high-rate classes while <paramref name="highOnly"/>.</summary>
            internal ClassState NextClass(bool highOnly)
            {
                for (int n = 0; n < AuditedClasses.Length; n++)
                {
                    _rotation = (_rotation + 1) % AuditedClasses.Length;
                    ClassState cs = Classes[(int)AuditedClasses[_rotation]];
                    if (!cs.Enabled) continue;
                    if (highOnly && !cs.HighRate) continue;
                    return cs;
                }
                return null;
            }
        }
    }
}
