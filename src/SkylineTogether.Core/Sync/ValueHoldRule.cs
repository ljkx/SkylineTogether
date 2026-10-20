namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Client: whether value messages (agent and simulation values) still wait for the structure backlog
    /// (ARCHITECTURE.md#client-apply): they wait while structure batches are queued, so the entities they name exist, but
    /// not when too many are held and not longer than <see cref="MaxWaitSeconds"/>: their acknowledgement holds block every
    /// later acknowledgement, and a client whose backlog never quite cleared showed no acknowledgement progress for minutes
    /// and was dropped as not applying. A value whose entity is not there yet is asked for again like any unresolved one.
    /// Pure.
    /// </summary>
    public static class ValueHoldRule
    {
        /// <summary>The longest the oldest held value message waits for the structure backlog (seconds).</summary>
        public const double MaxWaitSeconds = 10;

        /// <param name="structureBacklog">Structure batches still queued.</param>
        /// <param name="held">Value messages queued.</param>
        /// <param name="maxHeld">Value messages held at most.</param>
        /// <param name="oldestQueuedAt">When the oldest of them arrived (NaN: unknown, it waits as before).</param>
        /// <param name="now">Now (seconds, the same clock).</param>
        public static bool Wait(int structureBacklog, int held, int maxHeld, double oldestQueuedAt, double now)
        {
            if (structureBacklog <= 0 || held == 0 || held >= maxHeld) return false;
            return double.IsNaN(oldestQueuedAt) || now - oldestQueuedAt < MaxWaitSeconds;
        }
    }
}
