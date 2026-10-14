using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// Time-aligned inspection (ARCHITECTURE.md#simulation-values): the info panel refreshes every 256 simulation ticks relative to its
    /// last refresh (<c>UIUpdateState.Advance</c>), so the host samples the selected entities when its frame index crosses
    /// a multiple of 256 and every client applies the sample when its own clock reaches the stamped tick, then forces a
    /// panel refresh. Pure arithmetic, wrap-safe over the 32-bit frame counter.
    /// </summary>
    public static class InspectionCadence
    {
        /// <summary>Ticks between samples (the panel's update interval, <c>SelectedInfoUISystem</c>: 256).</summary>
        public const uint Interval = 256;

        /// <summary>The sample tick at or before <paramref name="frame"/> (a multiple of <see cref="Interval"/>).</summary>
        public static uint SampleTick(uint frame) => frame & ~(Interval - 1);

        /// <summary>
        /// True when a multiple of <see cref="Interval"/> lies in (<paramref name="last"/>, <paramref name="now"/>];
        /// <paramref name="tick"/> is the latest such multiple. A clock that went backwards or jumped more than 2^31
        /// ticks counts as a crossing at <c>SampleTick(now)</c> (resynchronises after a load or a clock reset).
        /// </summary>
        public static bool Crossed(uint last, uint now, out uint tick)
        {
            tick = SampleTick(now);
            if (now == last) return false;
            if ((int)(now - last) < 0) return true;
            return SampleTick(last) != tick;
        }

        /// <summary>Signed distance from <paramref name="from"/> to <paramref name="to"/> in ticks (wrap-safe).</summary>
        public static int Delta(uint from, uint to) => unchecked((int)(to - from));
    }

    /// <summary>
    /// Client side of time-aligned inspection: samples wait until the local clock reaches their tick (FIFO, so samples
    /// apply in host order). A sample also becomes ready when it has waited longer than <see cref="MaxHoldSeconds"/> of
    /// wall time (a paused or lagging follower must not freeze the panel) or when its tick lies implausibly far ahead
    /// of the local clock (<see cref="MaxAheadTicks"/>, a clock discontinuity). Not thread-safe.
    /// </summary>
    public sealed class InspectionSampleQueue<T>
    {
        /// <summary>Longest wall-clock wait of a sample.</summary>
        public const double MaxHoldSeconds = 3.0;

        /// <summary>Samples further ahead of the local clock than this apply at once.</summary>
        public const int MaxAheadTicks = 4096;

        /// <summary>Samples kept at most; older ones are dropped (a newer sample supersedes them anyway).</summary>
        public const int MaxPending = 16;

        private struct Pending
        {
            public uint Tick;
            public double Arrived;
            public T Payload;
        }

        private readonly Queue<Pending> _queue = new Queue<Pending>();

        public int Count => _queue.Count;

        /// <summary>Dropped because the queue was full (diagnostics).</summary>
        public long Dropped { get; private set; }

        public void Enqueue(uint tick, double now, T payload)
        {
            while (_queue.Count >= MaxPending)
            {
                _queue.Dequeue();
                Dropped++;
            }
            _queue.Enqueue(new Pending { Tick = tick, Arrived = now, Payload = payload });
        }

        /// <summary>The next sample that is ready at local frame <paramref name="localFrame"/>, in arrival order.</summary>
        public bool TryDequeueReady(uint localFrame, double now, out T payload, out uint tick)
        {
            payload = default(T);
            tick = 0;
            if (_queue.Count == 0) return false;
            Pending p = _queue.Peek();
            int ahead = InspectionCadence.Delta(localFrame, p.Tick);
            bool ready = ahead <= 0 || ahead > MaxAheadTicks || now - p.Arrived >= MaxHoldSeconds || double.IsNaN(now);
            if (!ready) return false;
            _queue.Dequeue();
            payload = p.Payload;
            tick = p.Tick;
            return true;
        }

        public void Clear() => _queue.Clear();
    }
}
