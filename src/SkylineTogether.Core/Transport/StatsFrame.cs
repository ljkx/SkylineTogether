using System.Threading;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// The frame stamp under which <see cref="PeerOutbox.Stats"/> answers from one transport query: the game layer
    /// advances it once per frame, and every consumer of the same peer's statistics in that frame (the deferred values'
    /// link budget, motion, agents, attention values, cell maps, flow status, the network HUD) shares a single query of
    /// the transport (Steam's real-time status was queried about six times per peer and frame). 0, the start value,
    /// means "no frames": every read queries (tools and tests that never advance it). Thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class StatsFrame
    {
        private static long _current;

        /// <summary>The current stamp (0: not advanced, nothing is cached).</summary>
        public static long Current => Interlocked.Read(ref _current);

        /// <summary>Starts the next frame (main thread, once per frame).</summary>
        public static void Advance()
        {
            if (Interlocked.Increment(ref _current) == 0) Interlocked.Increment(ref _current); // 0 stays "no frames"
        }

        /// <summary>Back to "no frames" (tests).</summary>
        public static void Reset() => Interlocked.Exchange(ref _current, 0);
    }
}
