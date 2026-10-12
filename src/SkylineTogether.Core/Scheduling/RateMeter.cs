using System;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// Exponentially smoothed byte rate: <see cref="Add"/> accumulates, <see cref="Update"/> folds the accumulated bytes
    /// over the elapsed time into the average with time constant <c>tau</c> (frame-rate independent). Single owner.
    /// </summary>
    internal sealed class RateMeter
    {
        private readonly double _tau;
        private double _rate;
        private double _pending;
        private double _pendingSeconds;

        public RateMeter(double tauSeconds)
        {
            _tau = tauSeconds > 0 ? tauSeconds : 1.0;
        }

        /// <summary>The smoothed rate in bytes per second.</summary>
        public double Rate => _rate;

        public void Add(double bytes)
        {
            if (bytes > 0 && !double.IsInfinity(bytes)) _pending += bytes;
        }

        /// <summary>Folds what was added since the last update over <paramref name="deltaSeconds"/> into the rate.</summary>
        public void Update(double deltaSeconds)
        {
            if (!(deltaSeconds > 0) || double.IsInfinity(deltaSeconds)) return;
            _pendingSeconds += deltaSeconds;
            double instantaneous = _pending / _pendingSeconds;
            double alpha = 1.0 - Math.Exp(-_pendingSeconds / _tau);
            _rate += (instantaneous - _rate) * alpha;
            _pending = 0;
            _pendingSeconds = 0;
        }

        /// <summary>Sets the rate directly (used to seed estimates).</summary>
        public void Reset(double rate)
        {
            _rate = rate > 0 && !double.IsInfinity(rate) ? rate : 0;
            _pending = 0;
            _pendingSeconds = 0;
        }
    }
}
