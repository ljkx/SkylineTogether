using System;

namespace SkylineTogether.Core.Fields
{
    /// <summary>
    /// Detects when a terraforming stroke has settled (ARCHITECTURE.md#zone-cells-cell-maps-terrain-and-water; original design, docs/history/ARCHITECTURE-original.md §4.10.9: "after a stroke settles (200 ms without stamps)
    /// the host reads back the union dirty rectangle"). Every applied stamp reports its texel rectangle (inclusive
    /// bounds, see <see cref="TileGrid.WorldToTexelRect"/>); once no stamp arrived for <see cref="SettleSeconds"/>,
    /// <see cref="TryTakeSettled"/> hands out the union of all rectangles since the last take and starts over. The
    /// dirty rectangle is tracked here because the game's own one (<c>TerrainSystem.m_LastWrite</c>) is consumed by
    /// its CPU readback (R07 §5.2).
    /// <para>
    /// <see cref="MaxPendingSeconds"/> optionally bounds how long a continuous stroke may stay unsettled (off by
    /// default): after that time the rectangle is handed out even while stamps keep arriving, and a new rectangle
    /// starts. Times are seconds of a monotonic clock (<see cref="IClock.Seconds"/>). Main thread only.
    /// </para>
    /// </summary>
    public sealed class StrokeSettleDetector
    {
        /// <summary>Slack for rounding in time differences (0.3 - 0.1 is slightly below 0.2 in binary floating point).</summary>
        private const double TimeEpsilon = 1e-6;

        private int _minX, _minY, _maxX, _maxY;
        private bool _pending;
        private double _firstStampSeconds;
        private double _lastStampSeconds;
        private double _maxPendingSeconds = double.PositiveInfinity;

        public StrokeSettleDetector(double settleSeconds = 0.2)
        {
            if (double.IsNaN(settleSeconds) || double.IsInfinity(settleSeconds) || settleSeconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(settleSeconds), settleSeconds, "expected a finite, non-negative number of seconds");
            }
            SettleSeconds = settleSeconds;
        }

        /// <summary>Quiet time after the last stamp before a stroke counts as settled.</summary>
        public double SettleSeconds { get; }

        /// <summary>
        /// Longest time a rectangle stays pending while stamps keep arriving (measured from its first stamp);
        /// <see cref="double.PositiveInfinity"/> (the default) waits for the stroke to settle.
        /// </summary>
        public double MaxPendingSeconds
        {
            get => _maxPendingSeconds;
            set
            {
                if (double.IsNaN(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value), value, "expected a positive number of seconds");
                _maxPendingSeconds = value;
            }
        }

        /// <summary>True while a dirty rectangle waits to be taken.</summary>
        public bool HasPending => _pending;

        /// <summary>Time of the last stamp of the pending rectangle (NaN when nothing is pending).</summary>
        public double LastStampSeconds => _pending ? _lastStampSeconds : double.NaN;

        /// <summary>
        /// Records a stamp that changed the inclusive texel rectangle at time <paramref name="now"/>. An empty
        /// rectangle (max below min) changed nothing and is ignored.
        /// </summary>
        public void OnStamp(int minX, int minY, int maxX, int maxY, double now)
        {
            CheckTime(now);
            if (maxX < minX || maxY < minY) return;
            if (!_pending)
            {
                _minX = minX;
                _minY = minY;
                _maxX = maxX;
                _maxY = maxY;
                _pending = true;
                _firstStampSeconds = now;
                _lastStampSeconds = now;
                return;
            }
            if (minX < _minX) _minX = minX;
            if (minY < _minY) _minY = minY;
            if (maxX > _maxX) _maxX = maxX;
            if (maxY > _maxY) _maxY = maxY;
            if (now > _lastStampSeconds) _lastStampSeconds = now;
        }

        /// <summary>
        /// True when a rectangle is pending and the stroke has settled at <paramref name="now"/> (no stamp for
        /// <see cref="SettleSeconds"/>, or pending for <see cref="MaxPendingSeconds"/>); hands out the union rectangle
        /// and clears it. Otherwise false with an empty rectangle (max below min).
        /// </summary>
        public bool TryTakeSettled(double now, out int minX, out int minY, out int maxX, out int maxY)
        {
            CheckTime(now);
            if (_pending && (now - _lastStampSeconds >= SettleSeconds - TimeEpsilon || now - _firstStampSeconds >= _maxPendingSeconds - TimeEpsilon))
            {
                minX = _minX;
                minY = _minY;
                maxX = _maxX;
                maxY = _maxY;
                _pending = false;
                return true;
            }
            minX = minY = 0;
            maxX = maxY = -1;
            return false;
        }

        /// <summary>Drops the pending rectangle (session end, or after a full resync made it irrelevant).</summary>
        public void Reset()
        {
            _pending = false;
        }

        private static void CheckTime(double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now))
            {
                throw new ArgumentOutOfRangeException(nameof(now), now, "expected a finite time in seconds");
            }
        }
    }
}
