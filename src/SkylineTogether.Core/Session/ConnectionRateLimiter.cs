using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// New connections per remote (Steam ID or address) in a sliding window (R2 N3): a remote that reconnects in a loop to
    /// keep the host's handshake slots full is closed at once, before its hello is read, so the slots stay free for others.
    /// An honest player's reconnects (an automatic rejoin tries six times in about a minute and a half) stay well below the
    /// limit. Memory is bounded: the oldest remotes are forgotten first. Pure (the clock is passed in); not thread-safe.
    /// </summary>
    public sealed class ConnectionRateLimiter
    {
        /// <summary>Remotes remembered at most.</summary>
        public const int MaxRemotes = 4096;

        private readonly int _max;
        private readonly double _window;
        private readonly Dictionary<string, Queue<double>> _recent = new Dictionary<string, Queue<double>>(StringComparer.Ordinal);
        private readonly Queue<string> _order = new Queue<string>();

        public ConnectionRateLimiter(int maxPerWindow, double windowSeconds)
        {
            _max = Math.Max(1, maxPerWindow);
            _window = Math.Max(1, windowSeconds);
        }

        /// <summary>Connections refused so far (diagnostics).</summary>
        public long Refused { get; private set; }

        /// <summary>
        /// Counts a new connection of <paramref name="remote"/> (null: no identity, never limited) and returns whether it
        /// may go on. A refused connection does not count against the remote's window.
        /// </summary>
        public bool Allow(string remote, double now)
        {
            if (remote == null) return true;
            if (!_recent.TryGetValue(remote, out Queue<double> times))
            {
                while (_recent.Count >= MaxRemotes && _order.Count > 0) _recent.Remove(_order.Dequeue());
                times = new Queue<double>();
                _recent[remote] = times;
                _order.Enqueue(remote);
            }
            while (times.Count > 0 && now - times.Peek() > _window) times.Dequeue();
            if (times.Count >= _max)
            {
                Refused++;
                return false;
            }
            times.Enqueue(now);
            return true;
        }
    }
}
