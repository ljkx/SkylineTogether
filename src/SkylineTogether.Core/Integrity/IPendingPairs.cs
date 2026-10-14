namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// Pairs whose live value legitimately differs from the ledger for a known reason (docs/history/ARCHITECTURE-original.md §8.3): parked for a missing
    /// reference (written with <c>Entity.Null</c> while the ledger holds the received hash, §4.7.5), held for a pending
    /// proposal (§5.5.4), awaiting this frame's end-of-frame re-application (§4.7.8) or a re-resolution (§3.7, §4.7.5),
    /// decoded with a presence-safe stand-in (§4.11.3), or covered by a pending proposal. The rolling verifier never
    /// compares them and <see cref="RepairCoordinator"/> never repairs them, so a parked record is not "repaired" again
    /// and again until its target binds. Implemented by game-integrity's <c>PendingPairs</c>.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public interface IPendingPairs
    {
        bool IsPending(NetSlot slot, TypeRank type);
    }
}
