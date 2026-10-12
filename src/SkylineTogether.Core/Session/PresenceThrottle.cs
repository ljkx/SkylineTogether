using System;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// Decides when the local player's presence is sent (ARCH §11.4): at most every <see cref="Interval"/> (10 Hz)
    /// while something changed, and every <see cref="IdleInterval"/> as a keepalive while nothing did, so an idle
    /// player costs almost nothing and a remote marker never expires while its player is still there.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class PresenceThrottle
    {
        private double _lastSent = double.NegativeInfinity;
        private float _x, _y, _z;
        private byte _tool;
        private int _prefab = int.MinValue;
        private float _brush;

        public double Interval { get; set; } = 0.1;

        public double IdleInterval { get; set; } = 1.0;

        /// <summary>Cursor movement (metres) below which the cursor counts as unchanged.</summary>
        public float MinMove { get; set; } = 0.25f;

        /// <summary>True when a presence with these values should be sent now; records it as sent.</summary>
        public bool ShouldSend(double now, float x, float y, float z, byte toolKind, int prefabRank, float brushRadius)
        {
            double since = now - _lastSent;
            if (since < Interval) return false;
            bool changed = toolKind != _tool || prefabRank != _prefab || Math.Abs(brushRadius - _brush) > 0.01f
                           || Math.Abs(x - _x) > MinMove || Math.Abs(y - _y) > MinMove || Math.Abs(z - _z) > MinMove;
            if (!changed && since < IdleInterval) return false;
            _lastSent = now;
            _x = x;
            _y = y;
            _z = z;
            _tool = toolKind;
            _prefab = prefabRank;
            _brush = brushRadius;
            return true;
        }

        /// <summary>Forgets what was sent (a new session starts).</summary>
        public void Reset()
        {
            _lastSent = double.NegativeInfinity;
            _prefab = int.MinValue;
        }
    }
}
