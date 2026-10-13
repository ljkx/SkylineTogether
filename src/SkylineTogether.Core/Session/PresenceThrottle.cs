using System;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// Decides when the local player's presence is sent (ARCH §11.4): at most every <see cref="Interval"/> (10 Hz)
    /// while something changed, and every <see cref="IdleInterval"/> as a keepalive while nothing did, so an idle
    /// player costs almost nothing and a remote marker never expires while its player is still there.
    /// <para>
    /// blueprint branch: a discrete change (tool, prefab, brush, tool preview) is followed by <see cref="Repeats"/>
    /// unchanged copies <see cref="RepeatInterval"/> apart. Every message carries the whole state, so a copy heals a lost
    /// message (the last preview before the player stops dragging, or the empty preview after an apply or a cancel)
    /// within a few hundred milliseconds instead of the keepalive second.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class PresenceThrottle
    {
        private double _lastSent = double.NegativeInfinity;
        private float _x, _y, _z;
        private byte _tool;
        private int _prefab = int.MinValue;
        private float _brush;
        private ulong _preview;
        private int _repeatsLeft;

        public double Interval { get; set; } = 0.1;

        public double IdleInterval { get; set; } = 1.0;

        /// <summary>Unchanged copies sent after a discrete change.</summary>
        public int Repeats { get; set; } = 2;

        public double RepeatInterval { get; set; } = 0.2;

        /// <summary>Cursor movement (metres) below which the cursor counts as unchanged.</summary>
        public float MinMove { get; set; } = 0.25f;

        /// <summary>
        /// True when a message could go out now (the rate limit allows one); a cheap test before the caller gathers an
        /// expensive state such as the tool preview.
        /// </summary>
        public bool Due(double now) => now - _lastSent >= Interval;

        /// <summary>True when a presence with these values should be sent now; records it as sent.</summary>
        public bool ShouldSend(double now, float x, float y, float z, byte toolKind, int prefabRank, float brushRadius, ulong previewHash = 0)
        {
            double since = now - _lastSent;
            if (since < Interval) return false;
            bool discrete = toolKind != _tool || prefabRank != _prefab || Math.Abs(brushRadius - _brush) > 0.01f || previewHash != _preview;
            bool changed = discrete || Math.Abs(x - _x) > MinMove || Math.Abs(y - _y) > MinMove || Math.Abs(z - _z) > MinMove;
            bool repeat = !changed && _repeatsLeft > 0 && since >= RepeatInterval;
            if (!changed && !repeat && since < IdleInterval) return false;
            if (discrete) _repeatsLeft = Math.Max(0, Repeats);
            else if (_repeatsLeft > 0) _repeatsLeft--; // any send carries the whole state
            _lastSent = now;
            _x = x;
            _y = y;
            _z = z;
            _tool = toolKind;
            _prefab = prefabRank;
            _brush = brushRadius;
            _preview = previewHash;
            return true;
        }

        /// <summary>Forgets what was sent (a new session starts).</summary>
        public void Reset()
        {
            _lastSent = double.NegativeInfinity;
            _prefab = int.MinValue;
            _preview = 0;
            _repeatsLeft = 0;
        }
    }
}
