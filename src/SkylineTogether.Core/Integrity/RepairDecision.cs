namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// The host's answer to a client repair request (docs/history/ARCHITECTURE-original.md §8.5 "Who decides"). The host never disconnects a peer for
    /// its repair-request rate (docs/history/ARCHITECTURE-original.md §5.6, §14.3); only a systematic repair loop ends the connection.
    /// </summary>
    public enum RepairDecision : byte
    {
        /// <summary>Levels 0-2 (or a bucket detail) were served: pairs marked dirty, entities re-sent, absent entities reported.</summary>
        Served,

        /// <summary>Dropped: above the per-peer rate, or a rebaseline / snapshot offer is already running. The client asks again later.</summary>
        Throttled,

        /// <summary>Level 3: a domain rebaseline of the class was started over the bulk lane.</summary>
        Rebaseline,

        /// <summary>Level 4: the peer is offered a single snapshot resync (rebaseline caps exceeded, or a snapshot hint).</summary>
        OfferSnapshot,

        /// <summary>More than the allowed snapshot resyncs within 30 minutes: disconnect with the reason "repair loop" and write a dump.</summary>
        DisconnectRepairLoop
    }
}
