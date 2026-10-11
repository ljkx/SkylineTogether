namespace SkylineTogether.Core
{
    /// <summary>
    /// Why the host rejected a proposal (ARCH §5.4.1, §5.5.2). The values travel in RESULT records
    /// (<c>varint reasonCode</c>) and must not change; each has a localised toast.
    /// </summary>
    public enum RejectReason : ushort
    {
        None,
        /// <summary>A stale base revision: another player edited the same entity first.</summary>
        Conflict,
        NoPermission,
        NotEnoughMoney,
        /// <summary>A created prefab is still locked.</summary>
        Locked,
        OutsideTiles,
        /// <summary>Structurally invalid (dead references, non-authorable types, non-finite values, limits).</summary>
        Invalid,
        RateLimited,
        /// <summary>The sender is not live yet (still joining).</summary>
        NotLive,
        /// <summary>The commit threw on the host.</summary>
        HostError,
        Timeout,
        /// <summary>The action is declared host-only (adapters' hostOnlyActions).</summary>
        HostOnly,
        /// <summary>A PrefabRef change across prefab classes or families (ARCH §5.4.1 rule 5).</summary>
        PrefabClass,
        /// <summary>Curve end points do not match their nodes (ARCH §5.4.1 rule 6).</summary>
        Geometry
    }
}
