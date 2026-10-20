namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Client: whether the oldest value message (agent and simulation values) still waits for the structure backlog
    /// (ARCHITECTURE.md#client-apply). A value message waits until every structure batch that arrived before it has been
    /// applied, so the entities it names exist and no older upsert overwrites its newer values afterwards; it never waits
    /// for batches that arrived after it. The order comes from the apply acknowledgement sequence: a value message holds
    /// sequence number <c>Received + 1</c> on arrival (<see cref="ApplyAckTracker.HoldNext"/>), so the structure batches
    /// before it are those with a smaller number. Because those are applied in order, the wait always ends while the
    /// structure apply makes progress, however long a catch-up takes and however many batches keep arriving behind it
    /// (a fixed time limit applied values ahead of older batches during long catch-ups, R3 V1; waiting for an empty
    /// backlog showed no acknowledgement progress for minutes when it never quite cleared, R2 S9). Only a full queue
    /// (the caller's limit) lets the oldest message go early, to bound memory. Pure.
    /// </summary>
    public static class ValueHoldRule
    {
        /// <param name="oldestValueHold">The oldest queued value message's sequence number (<see cref="AckHeldQueue{T}.OldestHold"/>; 0: unknown).</param>
        /// <param name="oldestStructureSeq">The oldest structure batch still queued (its acknowledgement sequence number; 0: none queued).</param>
        /// <param name="held">Value messages queued.</param>
        /// <param name="maxHeld">Value messages held at most.</param>
        public static bool Wait(long oldestValueHold, long oldestStructureSeq, int held, int maxHeld)
        {
            if (held == 0 || oldestStructureSeq <= 0 || held >= maxHeld) return false;
            if (oldestValueHold <= 0) return true; // no order known (no session tracker): wait for the backlog as before
            return oldestStructureSeq < oldestValueHold;
        }
    }
}
