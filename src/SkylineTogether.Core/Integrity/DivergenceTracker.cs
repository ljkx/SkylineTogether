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
    /// least <see cref="ConfirmSeconds"/> apart <i>and</i> the client's own hash did not change in between: a client that
    /// keeps receiving updates is following, a client whose value is stuck while the host's differs has diverged.
    /// </para>
    /// <para>
    /// <b>Repair and tolerance.</b> A confirmed entity is re-sent (<see cref="Outcome.Repair"/>); its next observation is
    /// at least <see cref="VerifySeconds"/> later and must be confirmed again. After <see cref="RepairsBeforeTolerance"/>
    /// repairs that did not help it is tolerated for <see cref="ToleranceSeconds"/> (logged once with an explanation
    /// request), and while tolerated <see cref="TryGetOverride"/> lets the client count the host's hash in its bucket sums,
    /// so one stubborn entity cannot keep its bucket mismatching. This is the repair-storm guard; the per-detail cap
    /// <see cref="MaxRepairsPerDetail"/> and the bound <see cref="MaxTracked"/> keep the cost bounded under mass divergence.
    /// </para>
    /// Single-threaded (main thread); time is passed in seconds.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class DivergenceTracker
    {
        /// <summary>Two observations of a stuck mismatch must be at least this far apart (seconds).</summary>
        public double ConfirmSeconds = 1.5;

        /// <summary>A repaired entity is judged again no earlier than this (seconds).</summary>
        public double VerifySeconds = 3.0;

        /// <summary>How long a tolerated entity stays masked (seconds).</summary>
        public double ToleranceSeconds = 600;

        /// <summary>Repairs of one entity before it is tolerated.</summary>
        public int RepairsBeforeTolerance = 2;

        /// <summary>Repairs one detail may ask for; further confirmed entities wait for the next detail.</summary>
        public int MaxRepairsPerDetail = 64;

        /// <summary>Entities tracked at most; further mismatches are ignored until entries resolve (counted in <see cref="Overflow"/>).</summary>
        public int MaxTracked = 50000;

        /// <summary>An observation not seen again for this long is forgotten by <see cref="Sweep"/> (seconds).</summary>
        public double ForgetSeconds = 300;

        private readonly Dictionary<uint, Entry> _entries = new Dictionary<uint, Entry>();
        private readonly List<uint> _scratch = new List<uint>();

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

        /// <summary>What one detail asks the caller to do.</summary>
        public sealed class Outcome
        {
            /// <summary>Entities to re-send (host: spawn, or despawn when it does not have them).</summary>
            public readonly List<uint> Repair = new List<uint>();

            /// <summary>Entities whose hash members should be requested and logged (newly tolerated).</summary>
            public readonly List<uint> Explain = new List<uint>();

            /// <summary>Entities tolerated by this detail.</summary>
            public readonly List<uint> NewlyTolerated = new List<uint>();

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

        /// <summary>Confirmed mismatches since creation.</summary>
        public long Confirmations { get; private set; }

        /// <summary>Repairs asked for since creation.</summary>
        public long RepairsRequested { get; private set; }

        /// <summary>Repaired entities that matched afterwards.</summary>
        public long RepairsSucceeded { get; private set; }

        /// <summary>Mismatches ignored because <see cref="MaxTracked"/> entities were tracked.</summary>
        public long Overflow { get; private set; }

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
            if (!stable)
            {
                e.Rounds = 1;
                e.FirstSeen = now;
                e.ClientPresent = clientPresent;
                e.ClientHash = clientHash;
                outcome.NeedRecheck(ConfirmSeconds);
                return;
            }
            if (now - e.FirstSeen < ConfirmSeconds)
            {
                outcome.NeedRecheck(ConfirmSeconds - (now - e.FirstSeen));
                return;
            }
            if (e.Attempts > 0 && now - e.LastRepair < VerifySeconds)
            {
                outcome.NeedRecheck(VerifySeconds - (now - e.LastRepair));
                return;
            }
            Confirmations++;
            if (e.Attempts >= RepairsBeforeTolerance)
            {
                e.ToleratedUntil = now + ToleranceSeconds;
                outcome.NewlyTolerated.Add(id);
                if (!e.Explained)
                {
                    e.Explained = true;
                    outcome.Explain.Add(id);
                }
                return;
            }
            if (outcome.Repair.Count >= MaxRepairsPerDetail)
            {
                outcome.NeedRecheck(ConfirmSeconds);
                return;
            }
            outcome.Repair.Add(id);
            RepairsRequested++;
            e.Attempts++;
            e.LastRepair = now;
            e.Rounds = 0;
            outcome.NeedRecheck(VerifySeconds);
        }

        private void Resolve(uint id)
        {
            if (!_entries.TryGetValue(id, out Entry e)) return;
            if (e.Attempts > 0) RepairsSucceeded++;
            _entries.Remove(id);
        }

        /// <summary>
        /// The bucket's digest matched: its observed and repaired entities agree now (tolerated ones stay masked, because
        /// the client counted the host's hash for them).
        /// </summary>
        public void OnBucketMatched(uint bucket, double now)
        {
            if (_entries.Count == 0) return;
            uint first = EntityDigest.FirstId(bucket);
            uint last = EntityDigest.LastId(bucket);
            _scratch.Clear();
            foreach (KeyValuePair<uint, Entry> pair in _entries)
            {
                if (pair.Key < first || pair.Key > last || pair.Value.ToleratedUntil > now) continue;
                _scratch.Add(pair.Key);
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

        /// <summary>True when any tracked entity lies in the bucket (cheap pre-check for overrides).</summary>
        public bool HasEntriesIn(uint bucket)
        {
            if (_entries.Count == 0) return false;
            uint first = EntityDigest.FirstId(bucket);
            uint last = EntityDigest.LastId(bucket);
            foreach (uint id in _entries.Keys)
            {
                if (id >= first && id <= last) return true;
            }
            return false;
        }

        /// <summary>Forgets stale observations and expired tolerances.</summary>
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
            foreach (uint id in _scratch) _entries.Remove(id);
        }

        /// <summary>Forgets everything (session end); keeps the statistics.</summary>
        public void Clear() => _entries.Clear();
    }
}
