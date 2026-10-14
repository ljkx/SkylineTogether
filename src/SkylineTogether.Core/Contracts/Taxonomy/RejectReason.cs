namespace SkylineTogether.Core
{
    /// <summary>
    /// Why the host rejected a proposal (ARCHITECTURE.md#host-validation-and-commit). The values travel in RESULT records
    /// (<c>varint reasonCode</c>) and must not change; each has a localised toast (<c>ProposalRules.LocaleKey</c>).
    /// </summary>
    public enum RejectReason : ushort
    {
        None,
        /// <summary>A stale base revision: another player edited the same entity first.</summary>
        Conflict,
        NoPermission,
        NotEnoughMoney,
        /// <summary>A created prefab or painted zone type is still locked by the host's progression (or map tile permits are used up).</summary>
        Locked,
        /// <summary>A new or moved structure lies on a map tile the city does not own.</summary>
        OutsideTiles,
        /// <summary>Structurally invalid (dead references, non-authorable types, non-finite values, limits, no or several headers).</summary>
        Invalid,
        RateLimited,
        /// <summary>The sender is no longer live (left or replaced while its proposal waited); logged on the host, never delivered.</summary>
        NotLive,
        /// <summary>The commit threw on the host, or the proposal names an asset the host lacks.</summary>
        HostError,
        /// <summary>The proposal's base epoch is older than the conflict ledger reaches back.</summary>
        Timeout,
        /// <summary>The proposal touches state only the host's simulation creates or changes (events, chirps, journal entries).</summary>
        HostOnly,
        /// <summary>A prefab of another class than the entity, or a <c>PrefabRef</c> change across classes.</summary>
        PrefabClass,
        /// <summary>A shape the game does not build: outside the map, curve ends off their nodes, edges too long, broken area outlines.</summary>
        Geometry
    }
}
