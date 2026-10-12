using System;

namespace SkylineTogether.Core.Follower
{
    /// <summary>What the follower clock does to the local <c>SimulationSystem</c> this frame.</summary>
    public struct ClockCorrection
    {
        /// <summary>Set <c>frameIndex</c> to <see cref="SetFrame"/> (hard jump).</summary>
        public bool Jump;
        public uint SetFrame;
        /// <summary>Advance <c>frameIndex</c> by this many ticks before the simulation runs (catch-up; the skipped ticks
        /// only skip cosmetic updates).</summary>
        public int SkipTicks;
        /// <summary>Let the simulation produce no tick this frame (the client is ahead).</summary>
        public bool Hold;
    }

    /// <summary>
    /// The follower clock's estimate of the host's simulation frame (ARCH §6.1, simplified): host frames arrive as
    /// samples (the <c>Clock</c> record and the tick of every motion datagram), each one a lower bound of the host frame
    /// at its arrival because it was taken before it travelled. The estimate advances at the session speed between
    /// samples, follows a sample that is ahead of it at once (the least delayed sample wins) and drifts slowly towards
    /// samples that are behind it (our local rate is a little fast). <see cref="Decide"/> turns the difference between
    /// the target (estimate minus the motion lag) and the local frame into a correction.
    /// Times are seconds of any monotonic clock. Main thread only.
    /// </summary>
    public sealed class FollowerClockModel
    {
        /// <summary>A sample this far from the estimate (ticks) resets it.</summary>
        public const double ResetTicks = 600;

        /// <summary>Share of a late sample's difference that is applied (slow downward drift).</summary>
        public const double DownwardGain = 0.05;

        /// <summary>Above this error (ticks) the local frame jumps instead of catching up smoothly.</summary>
        public const int JumpTicks = 120;

        /// <summary>Errors up to this many ticks are tolerated (no correction).</summary>
        public const int DeadBand = 2;

        /// <summary>Ticks skipped per frame at most while catching up.</summary>
        public const int MaxSkipPerFrame = 4;

        private double _anchorFrame;
        private double _anchorTime;
        private double _rate;

        /// <summary>True once a sample arrived.</summary>
        public bool HasEstimate { get; private set; }

        /// <summary>Ticks per second the estimate advances (60 × speed, 0 while paused).</summary>
        public double Rate => _rate;

        /// <summary>
        /// Forgets every sample (a new activation, e.g. the next session in the same game run): an estimate carried over
        /// from an earlier session within <see cref="ResetTicks"/> of the new host's frame would only drift down at
        /// <see cref="DownwardGain"/> and keep the client ahead of the motion stream for minutes.
        /// </summary>
        public void Reset()
        {
            HasEstimate = false;
            _anchorFrame = 0;
            _anchorTime = 0;
            _rate = 0;
        }

        /// <summary>Changes the advance rate; the estimate is re-anchored so it stays continuous.</summary>
        public void SetRate(double ticksPerSecond, double now)
        {
            if (double.IsNaN(ticksPerSecond) || ticksPerSecond < 0) ticksPerSecond = 0;
            if (HasEstimate)
            {
                _anchorFrame = Estimate(now);
                _anchorTime = now;
            }
            _rate = ticksPerSecond;
        }

        /// <summary>Adds a host frame observed at <paramref name="now"/>.</summary>
        public void AddSample(uint hostFrame, double now)
        {
            if (!HasEstimate)
            {
                _anchorFrame = hostFrame;
                _anchorTime = now;
                HasEstimate = true;
                return;
            }
            double predicted = Estimate(now);
            // the difference in wrapping uint arithmetic (frames wrap after ~2.3 years of play at speed 1)
            uint predictedFrame = unchecked((uint)(long)Math.Floor(predicted));
            int diff = unchecked((int)(hostFrame - predictedFrame));
            if (Math.Abs(diff) > ResetTicks)
            {
                _anchorFrame = hostFrame;
                _anchorTime = now;
                return;
            }
            double anchor = diff > 0 ? Math.Max(predicted, Math.Floor(predicted) + diff) : predicted + diff * DownwardGain;
            if (anchor >= 4294967296.0) anchor -= 4294967296.0;
            _anchorFrame = anchor;
            _anchorTime = now;
        }

        /// <summary>The estimated host frame at <paramref name="now"/> (fractional).</summary>
        public double Estimate(double now)
        {
            if (!HasEstimate) return 0;
            double elapsed = Math.Max(0, now - _anchorTime);
            return _anchorFrame + elapsed * _rate;
        }

        /// <summary>
        /// The correction for local frame <paramref name="local"/> towards <paramref name="target"/>. While
        /// <paramref name="paused"/> any difference is a jump (nothing moves, so nothing can stutter).
        /// </summary>
        public static ClockCorrection Decide(uint local, uint target, bool paused)
        {
            int err = unchecked((int)(target - local));
            var c = new ClockCorrection();
            if (err == 0) return c;
            if (paused || err > JumpTicks || err < -JumpTicks)
            {
                c.Jump = true;
                c.SetFrame = target;
                return c;
            }
            if (err > DeadBand) c.SkipTicks = Math.Min(err - DeadBand, MaxSkipPerFrame);
            else if (err < -DeadBand) c.Hold = true;
            return c;
        }
    }
}
