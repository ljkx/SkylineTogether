using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Modding
{
    /// <summary>
    /// The bookkeeping of the game layer's <c>ModStateWatcher</c> (docs/design/MOD-COMPATIBILITY.md), without the ECS: the
    /// last known state (presence and value hashes) per net id, the once-a-second limit per ordinary entity, and the slow
    /// sweep's passes. Each reported change says since when it may have happened (<see cref="Change.Since"/>): a change
    /// found by the change versions happened after the previous collection, one the slow sweep finds after the start of
    /// the previous sweep pass (the entity was checked in that pass at the latest), and a change held back by the limit
    /// keeps the time it was first seen. A reported change is consumed: the ledger takes the new state as known whether
    /// or not the caller sends it, so the same difference is never reported twice (a client's derived mod state that it
    /// does not propose is not proposed at the player's next input either, R3 G2b). Pure; main thread.
    /// </summary>
    public sealed class ModStateLedger
    {
        /// <summary>Changes of an ordinary entity are reported at most this often (seconds; mod data entities and presence changes at once).</summary>
        public const double MinInterval = 1.0;

        /// <summary>One reported change.</summary>
        public struct Change
        {
            public uint NetId;
            /// <summary>The set of watched types changed (or the entity was not known): an upsert, not values.</summary>
            public bool PresenceChanged;
            /// <summary>The earliest time the change may have happened (it happened in [Since, now]).</summary>
            public double Since;
        }

        private readonly Dictionary<uint, (ulong Presence, ulong Value)> _shadow = new Dictionary<uint, (ulong, ulong)>();
        private readonly Dictionary<uint, double> _lastEmit = new Dictionary<uint, double>();
        // held back by the limit: since when the change may have happened
        private readonly Dictionary<uint, double> _retry = new Dictionary<uint, double>();
        private readonly List<uint> _retryScratch = new List<uint>();
        private readonly List<uint> _sweep = new List<uint>();
        private int _sweepCursor;
        private double _passStart, _previousPassStart, _lastCollect;

        /// <summary>Number of entities with a known state.</summary>
        public int Known => _shadow.Count;

        /// <summary>Forgets everything; the baseline is taken at <paramref name="now"/>.</summary>
        public void Reset(double now)
        {
            _shadow.Clear();
            _lastEmit.Clear();
            _retry.Clear();
            _sweep.Clear();
            _sweepCursor = 0;
            _passStart = _previousPassStart = _lastCollect = now;
        }

        /// <summary>Records an entity's current state as known (the baseline, the applier's write, the capture's send).</summary>
        public void Set(uint id, ulong presence, ulong value) => _shadow[id] = (presence, value);

        public bool IsKnown(uint id) => _shadow.ContainsKey(id);

        public void Forget(uint id)
        {
            _shadow.Remove(id);
            _lastEmit.Remove(id);
            _retry.Remove(id);
        }

        /// <summary>
        /// Starts a collection at <paramref name="now"/>; returns since when the changes the change versions show may
        /// have happened (the previous collection).
        /// </summary>
        public double BeginCollect(double now)
        {
            double since = _lastCollect;
            _lastCollect = now;
            return since;
        }

        /// <summary>
        /// The entity's state now: reports a change against the known state (and takes the new state as known), or holds
        /// it back when it is an ordinary entity's value change within <see cref="MinInterval"/> of its last report (it is
        /// checked again by <see cref="TakeRetries"/>). <paramref name="urgent"/>: a mod data entity (reported at once).
        /// </summary>
        public bool Observe(uint id, ulong presence, ulong value, bool urgent, double since, double now, out Change change)
        {
            change = default(Change);
            bool known = _shadow.TryGetValue(id, out (ulong Presence, ulong Value) old);
            if (known && old.Presence == presence && old.Value == value)
            {
                _retry.Remove(id); // back to the known state: nothing pending
                return false;
            }
            bool presenceChanged = !known || old.Presence != presence;
            if (_retry.TryGetValue(id, out double heldSince))
            {
                since = Math.Min(since, heldSince);
            }
            if (!urgent && !presenceChanged && _lastEmit.TryGetValue(id, out double last) && now - last < MinInterval)
            {
                _retry[id] = since;
                return false;
            }
            _retry.Remove(id);
            _shadow[id] = (presence, value);
            _lastEmit[id] = now;
            change = new Change { NetId = id, PresenceChanged = presenceChanged, Since = since };
            return true;
        }

        /// <summary>The held-back ids whose limit ended (removed from the hold; <see cref="Observe"/> them again), with since when.</summary>
        public void TakeRetries(double now, List<(uint Id, double Since)> output)
        {
            if (_retry.Count == 0) return;
            _retryScratch.Clear();
            _retryScratch.AddRange(_retry.Keys);
            foreach (uint id in _retryScratch)
            {
                if (_lastEmit.TryGetValue(id, out double last) && now - last < MinInterval) continue;
                output.Add((id, _retry[id]));
                _retry.Remove(id);
            }
        }

        /// <summary>
        /// The next ids of the slow sweep, at most <paramref name="max"/> (a pass goes over every id known when it starts);
        /// <paramref name="since"/>: since when a change the sweep finds may have happened (the previous pass's start).
        /// </summary>
        public void NextSweep(int max, double now, List<uint> output, out double since)
        {
            if (_sweepCursor >= _sweep.Count)
            {
                _sweep.Clear();
                _sweep.AddRange(_shadow.Keys);
                _sweepCursor = 0;
                _previousPassStart = _passStart;
                _passStart = now;
            }
            since = _previousPassStart;
            int end = Math.Min(_sweep.Count, _sweepCursor + max);
            for (; _sweepCursor < end; _sweepCursor++) output.Add(_sweep[_sweepCursor]);
        }
    }
}
