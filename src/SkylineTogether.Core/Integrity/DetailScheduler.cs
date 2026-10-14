using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// Which bucket details a client asks the host for, and when (rolling verification, ARCHITECTURE.md#divergence-tracking-and-repair):
    /// each bucket is wanted at most once (the earlier due time wins), a requested bucket is not asked for again until
    /// its detail arrived or <see cref="AwaitTimeoutSeconds"/> passed, and at most <see cref="MaxPending"/> buckets wait,
    /// so a mass divergence cannot grow the queue without bound (dropped buckets are found again in the next cycle).
    /// Single-threaded; time in seconds.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class DetailScheduler
    {
        /// <summary>Buckets waiting at most.</summary>
        public int MaxPending = 512;

        /// <summary>A requested detail that did not arrive within this time may be requested again (seconds).</summary>
        public double AwaitTimeoutSeconds = 10;

        private readonly Dictionary<uint, double> _due = new Dictionary<uint, double>();
        private readonly Dictionary<uint, double> _awaiting = new Dictionary<uint, double>();
        private readonly List<KeyValuePair<uint, double>> _sort = new List<KeyValuePair<uint, double>>();

        /// <summary>Buckets waiting to be requested.</summary>
        public int Pending => _due.Count;

        /// <summary>Buckets requested whose detail has not arrived.</summary>
        public int Awaiting => _awaiting.Count;

        /// <summary>Requests dropped because <see cref="MaxPending"/> buckets were waiting.</summary>
        public long Dropped { get; private set; }

        /// <summary>
        /// Wants the bucket's detail at <paramref name="dueAt"/> (or earlier if already wanted earlier). False when it is
        /// being awaited (and not timed out) or the queue is full.
        /// </summary>
        public bool Request(uint bucket, double dueAt, double now)
        {
            if (_awaiting.TryGetValue(bucket, out double since))
            {
                if (now - since < AwaitTimeoutSeconds) return false;
                _awaiting.Remove(bucket);
            }
            if (_due.TryGetValue(bucket, out double existing))
            {
                if (dueAt < existing) _due[bucket] = dueAt;
                return true;
            }
            if (_due.Count >= MaxPending)
            {
                Dropped++;
                return false;
            }
            _due.Add(bucket, dueAt);
            return true;
        }

        /// <summary>True while the bucket's detail was requested and has not arrived (nor timed out).</summary>
        public bool IsAwaiting(uint bucket, double now) => _awaiting.TryGetValue(bucket, out double since) && now - since < AwaitTimeoutSeconds;

        /// <summary>True when the bucket waits to be requested.</summary>
        public bool IsPending(uint bucket) => _due.ContainsKey(bucket);

        /// <summary>Moves up to <paramref name="max"/> due buckets (earliest first) to <paramref name="output"/> and marks them awaited.</summary>
        public int TakeDue(double now, int max, List<uint> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (max <= 0 || _due.Count == 0) return 0;
            _sort.Clear();
            foreach (KeyValuePair<uint, double> pair in _due)
            {
                if (pair.Value <= now) _sort.Add(pair);
            }
            if (_sort.Count == 0) return 0;
            _sort.Sort((a, b) => a.Value != b.Value ? a.Value.CompareTo(b.Value) : a.Key.CompareTo(b.Key));
            int n = Math.Min(max, _sort.Count);
            for (int i = 0; i < n; i++)
            {
                uint bucket = _sort[i].Key;
                _due.Remove(bucket);
                _awaiting[bucket] = now;
                output.Add(bucket);
            }
            return n;
        }

        /// <summary>The bucket's detail arrived.</summary>
        public void OnArrived(uint bucket) => _awaiting.Remove(bucket);

        /// <summary>The bucket matched: nothing to request for it.</summary>
        public void Cancel(uint bucket) => _due.Remove(bucket);

        public void Clear()
        {
            _due.Clear();
            _awaiting.Clear();
        }
    }

    /// <summary>A token bucket counting operations (repair requests and responses); time in seconds.</summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class RateGate
    {
        private readonly double _perSecond;
        private readonly double _burst;
        private double _tokens;
        private double _last = double.NaN;

        public RateGate(double perSecond, double burst)
        {
            if (perSecond <= 0) throw new ArgumentOutOfRangeException(nameof(perSecond));
            if (burst < 1) throw new ArgumentOutOfRangeException(nameof(burst));
            _perSecond = perSecond;
            _burst = burst;
            _tokens = burst;
        }

        /// <summary>Whole operations available now.</summary>
        public int Available(double now)
        {
            Refill(now);
            return (int)Math.Floor(_tokens);
        }

        /// <summary>Takes <paramref name="count"/> operations if available.</summary>
        public bool TryTake(double now, int count = 1)
        {
            Refill(now);
            if (count < 0 || _tokens < count) return false;
            _tokens -= count;
            return true;
        }

        private void Refill(double now)
        {
            if (double.IsNaN(_last) || now < _last)
            {
                _last = now;
                return;
            }
            _tokens = Math.Min(_burst, _tokens + (now - _last) * _perSecond);
            _last = now;
        }
    }
}
