using System;
using System.Collections.Generic;
using SkylineTogether.Core.Sync;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// Client-side decisions of the rolling live verification (playable subset of ARCH §8.3-8.5): which differing,
    /// missing or extra entities of a bucket detail are real divergence, when to ask the host to re-send them, and when
    /// to stop trying.
    /// <para>
    /// <b>Confirmation.</b> The host hashes its live state, which may be ahead of what it has streamed (scanned values go
    /// out a scan pass later), so one mismatch proves nothing. An entity counts only when it mismatches in two details at
    /// least <see cref="ConfirmSeconds"/> apart (or the per-entity window of <see cref="ConfirmSecondsFor"/>: agents' key
    /// links reach clients only when the host's rolling agent scan gets to them) <i>and</i> the client's own hash did not
    /// change in between: a client that keeps receiving updates is following, a client whose value is stuck while the
    /// host's differs has diverged.
    /// </para>
    /// <para>
    /// <b>Repair and tolerance.</b> A confirmed entity is re-sent (<see cref="Outcome.Repair"/>); its next observation is
    /// at least <see cref="VerifySeconds"/> later and must be confirmed again. After <see cref="RepairsBeforeTolerance"/>
    /// repairs that did not help it is tolerated for <see cref="ToleranceSeconds"/> (logged once with an explanation
    /// request), and while tolerated <see cref="TryGetOverride"/> lets the client count the host's hash in its bucket sums,
    /// so one stubborn entity cannot keep its bucket mismatching.
    /// </para>
    /// <para>
    /// <b>Storm guards</b> (a real session repaired the same derived areas again and again, because every repair
    /// matched for a moment, resolved the entry and with it its attempt count, before the client's own geometry system
    /// regenerated the value): a per-entity repair <i>history</i> outlives the entry for <see cref="HistorySeconds"/>, so
    /// an entity that keeps coming back waits an exponential backoff (<see cref="BackoffSeconds"/> doubled per earlier
    /// repair) and is tolerated after <see cref="MaxRepairsInHistory"/> repairs even when each one matched briefly; an
    /// entity tolerated <see cref="MaxTolerances"/> times is given up for the session (<see cref="Outcome.GivenUp"/>); and
    /// a global token bucket (<see cref="MaxRepairsPerMinute"/>) bounds the repair rate whatever the cause. The per-detail
    /// cap <see cref="MaxRepairsPerDetail"/> and the bound <see cref="MaxTracked"/> keep the cost bounded under mass
    /// divergence.
    /// </para>
    /// Single-threaded (main thread); time is passed in seconds.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class DivergenceTracker
    {
        /// <summary>Two observations of a stuck mismatch must be at least this far apart (seconds).</summary>
        public double ConfirmSeconds = 1.5;

        /// <summary>
        /// An optional per-entity confirmation window (seconds) replacing <see cref="ConfirmSeconds"/>; a value
        /// not above zero falls back to it. The client gives agents a window longer than the host's agent scan cycle.
        /// </summary>
        public Func<uint, double> ConfirmSecondsFor;

        /// <summary>A repaired entity is judged again no earlier than this (seconds).</summary>
        public double VerifySeconds = 3.0;

        /// <summary>How long a tolerated entity stays masked (seconds).</summary>
        public double ToleranceSeconds = 600;

        /// <summary>Repairs of one entity (without a match in between) before it is tolerated.</summary>
        public int RepairsBeforeTolerance = 2;

        /// <summary>Repairs one detail may ask for; further confirmed entities wait for the next detail.</summary>
        public int MaxRepairsPerDetail = 64;

        /// <summary>Entities tracked at most; further mismatches are ignored until entries resolve (counted in <see cref="Overflow"/>).</summary>
        public int MaxTracked = 50000;

        /// <summary>An observation not seen again for this long is forgotten by <see cref="Sweep"/> (seconds).</summary>
        public double ForgetSeconds = 300;

        /// <summary>How long an entity's repairs are remembered after its last one (seconds).</summary>
        public double HistorySeconds = 1800;

        /// <summary>Repairs within the history after which an entity is tolerated even if each repair matched briefly.</summary>
        public int MaxRepairsInHistory = 3;

        /// <summary>An entity repaired before waits this long times 2^(earlier repairs - 1) since its last repair (seconds).</summary>
        public double BackoffSeconds = 10;

        /// <summary>Tolerance periods after which an entity is given up for the session.</summary>
        public int MaxTolerances = 2;

        /// <summary>Repairs per minute at most, all entities together (a token bucket with a burst of the same size).</summary>
        public double MaxRepairsPerMinute = 120;

        /// <summary>Histories kept at most (further first repairs are not remembered).</summary>
        public int MaxHistory = 50000;

        private readonly Dictionary<uint, Entry> _entries = new Dictionary<uint, Entry>();
        private readonly Dictionary<uint, History> _history = new Dictionary<uint, History>();
        private readonly Dictionary<uint, int> _perBucket = new Dictionary<uint, int>();
        private readonly List<uint> _scratch = new List<uint>();
        private double _budget = double.NaN;
        private double _budgetAt;

        private sealed class Entry
        {
            public bool ClientPresent;
            public ulong ClientHash;
            public bool HostPresent;
            public ulong HostHash;
            public int Rounds;
            public double FirstSeen;
            public double LastSeen;
            public int Attempts;
            public double LastRepair;
            public double ToleratedUntil;
            public bool Explained;
        }

        private sealed class History
        {
            public int Repairs;
            public double LastRepair;
            public int Tolerances;
        }

        /// <summary>What one detail asks the caller to do.</summary>
        public sealed class Outcome
        {
            /// <summary>Entities to re-send (host: spawn, or despawn when it does not have them).</summary>
            public readonly List<uint> Repair = new List<uint>();

            /// <summary>Entities whose hash members should be requested and logged (newly tolerated).</summary>
            public readonly List<uint> Explain = new List<uint>();

            /// <summary>Entities tolerated by this detail.</summary>
            public readonly List<uint> NewlyTolerated = new List<uint>();

            /// <summary>Entities given up for the session by this detail (also in <see cref="NewlyTolerated"/>).</summary>
            public readonly List<uint> GivenUp = new List<uint>();

            /// <summary>Repaired entities that were repaired before (candidates for a rate-limited explanation).</summary>
            public readonly List<uint> Repeated = new List<uint>();

            /// <summary>True when the bucket should be detailed again (an observation awaits confirmation or a repair awaits verification).</summary>
            public bool Recheck;

            /// <summary>With <see cref="Recheck"/>: the earliest useful delay in seconds.</summary>
            public double RecheckAfter;

            public int Matching;
            public int Mismatching;

            public void Clear()
            {
                Repair.Clear();
                Explain.Clear();
                NewlyTolerated.Clear();
                GivenUp.Clear();
                Repeated.Clear();
                Recheck = false;
                RecheckAfter = 0;
                Matching = 0;
                Mismatching = 0;
            }

            internal void NeedRecheck(double delay)
            {
                delay = Math.Max(0.25, delay);
                RecheckAfter = Recheck ? Math.Min(RecheckAfter, delay) : delay;
                Recheck = true;
            }
        }

        /// <summary>Entities being observed, repaired or tolerated.</summary>
        public int Tracked => _entries.Count;

        /// <summary>Entities whose repair history is remembered.</summary>
        public int Histories => _history.Count;

        /// <summary>Confirmed mismatches since creation.</summary>
        public long Confirmations { get; private set; }

        /// <summary>Repairs asked for since creation.</summary>
        public long RepairsRequested { get; private set; }

        /// <summary>Repaired entities that matched afterwards.</summary>
        public long RepairsSucceeded { get; private set; }

        /// <summary>Mismatches ignored because <see cref="MaxTracked"/> entities were tracked.</summary>
        public long Overflow { get; private set; }

        /// <summary>Confirmed repairs postponed because the global budget was spent.</summary>
        public long BudgetDeferred { get; private set; }

        /// <summary>Confirmed repairs postponed by the per-entity backoff.</summary>
        public long BackedOff { get; private set; }

        /// <summary>Entities given up for the session.</summary>
        public long GivenUpCount { get; private set; }

        /// <summary>Entities currently tolerated.</summary>
        public int ToleratedCount(double now)
        {
            int n = 0;
            foreach (Entry e in _entries.Values)
            {
                if (e.ToleratedUntil > now) n++;
            }
            return n;
        }

        /// <summary>Repairs of the entity remembered in its history (0 when none).</summary>
        public int RepairsOf(uint netId) => _history.TryGetValue(netId, out History h) ? h.Repairs : 0;

        /// <summary>Evaluates one bucket detail: the host's and the client's hashes of the same bucket.</summary>
        public void OnDetail(IReadOnlyList<EntityHash> host, IReadOnlyList<EntityHash> client, double now, Outcome outcome)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (outcome == null) throw new ArgumentNullException(nameof(outcome));
            outcome.Clear();
            List<(uint NetId, EntityDifference Kind)> diffs = EntityDigest.Compare(host, client);
            var hostById = new Dictionary<uint, ulong>(host.Count);
            foreach (EntityHash h in host) hostById[h.NetId] = h.Hash;
            var clientById = new Dictionary<uint, ulong>(client.Count);
            foreach (EntityHash h in client) clientById[h.NetId] = h.Hash;
            var mismatched = new HashSet<uint>();
            foreach ((uint id, EntityDifference kind) in diffs)
            {
                mismatched.Add(id);
                bool hostPresent = kind != EntityDifference.ExtraOnClient;
                bool clientPresent = kind != EntityDifference.MissingOnClient;
                Observe(id, hostPresent, hostPresent ? hostById[id] : 0, clientPresent, clientPresent ? clientById[id] : 0, now, outcome);
            }
            outcome.Mismatching = diffs.Count;

            // everything else matches: resolve its entries
            int matching = 0;
            foreach (uint id in hostById.Keys)
            {
                if (mismatched.Contains(id)) continue;
                matching++;
                Resolve(id);
            }
            foreach (uint id in clientById.Keys)
            {
                if (mismatched.Contains(id) || hostById.ContainsKey(id)) continue;
                matching++;
                Resolve(id);
            }
            outcome.Matching = matching;
        }

        private double ConfirmFor(uint id)
        {
            if (ConfirmSecondsFor == null) return ConfirmSeconds;
            double s = ConfirmSecondsFor(id);
            return s > 0 ? s : ConfirmSeconds;
        }

        private void Observe(uint id, bool hostPresent, ulong hostHash, bool clientPresent, ulong clientHash, double now, Outcome outcome)
        {
            if (!_entries.TryGetValue(id, out Entry e))
            {
                if (_entries.Count >= MaxTracked)
                {
                    Overflow++;
                    return;
                }
                e = new Entry();
                _entries.Add(id, e);
                AddToBucket(id);
            }
            e.HostPresent = hostPresent;
            e.HostHash = hostHash;
            if (e.ToleratedUntil > now) return; // masked; the override follows the host's hash
            if (e.ToleratedUntil != 0)
            {
                // tolerance expired: start over
                e.ToleratedUntil = 0;
                e.Attempts = 0;
                e.Rounds = 0;
                e.Explained = false;
            }
            bool stable = e.Rounds > 0 && e.ClientPresent == clientPresent && e.ClientHash == clientHash;
            e.LastSeen = now;
            double confirm = ConfirmFor(id);
            if (!stable)
            {
                e.Rounds = 1;
                e.FirstSeen = now;
                e.ClientPresent = clientPresent;
                e.ClientHash = clientHash;
                outcome.NeedRecheck(confirm);
                return;
            }
            if (now - e.FirstSeen < confirm)
            {
                outcome.NeedRecheck(confirm - (now - e.FirstSeen));
                return;
            }
            if (e.Attempts > 0 && now - e.LastRepair < VerifySeconds)
            {
                outcome.NeedRecheck(VerifySeconds - (now - e.LastRepair));
                return;
            }
            Confirmations++;
            _history.TryGetValue(id, out History h);
            if (h != null && h.Tolerances == 0 && now - h.LastRepair > HistorySeconds)
            {
                _history.Remove(id);
                h = null;
            }
            int earlier = h?.Repairs ?? 0;
            if (e.Attempts >= RepairsBeforeTolerance || earlier >= MaxRepairsInHistory)
            {
                if (h == null)
                {
                    h = new History { LastRepair = now };
                    _history[id] = h;
                }
                h.Tolerances++;
                bool giveUp = h.Tolerances >= MaxTolerances;
                e.ToleratedUntil = giveUp ? double.MaxValue : now + ToleranceSeconds;
                outcome.NewlyTolerated.Add(id);
                if (giveUp)
                {
                    GivenUpCount++;
                    outcome.GivenUp.Add(id);
                }
                if (!e.Explained)
                {
                    e.Explained = true;
                    outcome.Explain.Add(id);
                }
                return;
            }
            if (earlier > 0 && e.Attempts == 0)
            {
                // it matched after an earlier repair and diverged again: exponential backoff
                double wait = BackoffSeconds * Math.Pow(2, Math.Min(earlier - 1, 10));
                double since = now - h.LastRepair;
                if (since < wait)
                {
                    BackedOff++;
                    outcome.NeedRecheck(wait - since);
                    return;
                }
            }
            if (outcome.Repair.Count >= MaxRepairsPerDetail)
            {
                outcome.NeedRecheck(ConfirmSeconds);
                return;
            }
            if (!TakeBudget(now))
            {
                BudgetDeferred++;
                outcome.NeedRecheck(Math.Max(ConfirmSeconds, 60.0 / Math.Max(1, MaxRepairsPerMinute)));
                return;
            }
            outcome.Repair.Add(id);
            if (earlier > 0) outcome.Repeated.Add(id);
            RepairsRequested++;
            e.Attempts++;
            e.LastRepair = now;
            e.Rounds = 0;
            if (h == null)
            {
                if (_history.Count < MaxHistory) _history[id] = new History { Repairs = 1, LastRepair = now };
            }
            else
            {
                h.Repairs++;
                h.LastRepair = now;
            }
            outcome.NeedRecheck(VerifySeconds);
        }

        private bool TakeBudget(double now)
        {
            double cap = Math.Max(1, MaxRepairsPerMinute);
            if (double.IsNaN(_budget) || now < _budgetAt)
            {
                _budget = cap;
                _budgetAt = now;
            }
            _budget = Math.Min(cap, _budget + (now - _budgetAt) * cap / 60.0);
            _budgetAt = now;
            if (_budget < 1) return false;
            _budget -= 1;
            return true;
        }

        private void AddToBucket(uint id)
        {
            uint bucket = EntityDigest.BucketOf(id);
            _perBucket.TryGetValue(bucket, out int n);
            _perBucket[bucket] = n + 1;
        }

        private void RemoveEntry(uint id)
        {
            if (!_entries.Remove(id)) return;
            uint bucket = EntityDigest.BucketOf(id);
            if (_perBucket.TryGetValue(bucket, out int n))
            {
                if (n <= 1) _perBucket.Remove(bucket);
                else _perBucket[bucket] = n - 1;
            }
        }

        private void Resolve(uint id)
        {
            if (!_entries.TryGetValue(id, out Entry e)) return;
            if (e.ToleratedUntil == double.MaxValue) return; // given up for the session
            if (e.Attempts > 0) RepairsSucceeded++;
            RemoveEntry(id); // the history stays, so a resolved entry no longer resets the repair count
        }

        /// <summary>
        /// The bucket's digest matched: its observed and repaired entities agree now (tolerated ones stay masked, because
        /// the client counted the host's hash for them).
        /// </summary>
        public void OnBucketMatched(uint bucket, double now)
        {
            if (!_perBucket.ContainsKey(bucket)) return;
            uint first = EntityDigest.FirstId(bucket);
            uint last = EntityDigest.LastId(bucket);
            _scratch.Clear();
            for (uint id = first; id <= last && id != 0; id++)
            {
                if (_entries.TryGetValue(id, out Entry e) && e.ToleratedUntil <= now) _scratch.Add(id);
                if (id == uint.MaxValue) break;
            }
            foreach (uint id in _scratch) Resolve(id);
        }

        /// <summary>True while the entity is tolerated.</summary>
        public bool IsTolerated(uint netId, double now) => _entries.TryGetValue(netId, out Entry e) && e.ToleratedUntil > now;

        /// <summary>For a tolerated entity: whether the host has it and its last known host hash (to count in bucket sums).</summary>
        public bool TryGetOverride(uint netId, double now, out bool hostPresent, out ulong hostHash)
        {
            if (_entries.TryGetValue(netId, out Entry e) && e.ToleratedUntil > now)
            {
                hostPresent = e.HostPresent;
                hostHash = e.HostHash;
                return true;
            }
            hostPresent = false;
            hostHash = 0;
            return false;
        }

        /// <summary>True when any tracked entity lies in the bucket (O(1); was a scan over every entry per digest).</summary>
        public bool HasEntriesIn(uint bucket) => _perBucket.ContainsKey(bucket);

        /// <summary>
        /// The id was despawned or deleted locally: its observation, tolerance and history no longer describe
        /// anything (the id may be reused for another entity).
        /// </summary>
        public void Forget(uint netId)
        {
            RemoveEntry(netId);
            _history.Remove(netId);
        }

        /// <summary>Forgets stale observations, expired tolerances and old histories.</summary>
        public void Sweep(double now)
        {
            _scratch.Clear();
            foreach (KeyValuePair<uint, Entry> pair in _entries)
            {
                Entry e = pair.Value;
                bool expired = e.ToleratedUntil != 0 && e.ToleratedUntil <= now;
                bool stale = e.ToleratedUntil == 0 && now - e.LastSeen > ForgetSeconds;
                if (expired || stale) _scratch.Add(pair.Key);
            }
            foreach (uint id in _scratch) RemoveEntry(id);
            _scratch.Clear();
            foreach (KeyValuePair<uint, History> pair in _history)
            {
                // a history with a tolerance counts toward giving up; it is kept as long as the session (bounded by MaxHistory)
                if (pair.Value.Tolerances == 0 && now - pair.Value.LastRepair > HistorySeconds && !_entries.ContainsKey(pair.Key)) _scratch.Add(pair.Key);
            }
            foreach (uint id in _scratch) _history.Remove(id);
        }

        /// <summary>Forgets everything (session end); keeps the statistics.</summary>
        public void Clear()
        {
            _entries.Clear();
            _history.Clear();
            _perBucket.Clear();
            _budget = double.NaN;
        }
    }
}
