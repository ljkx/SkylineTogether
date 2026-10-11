namespace SkylineTogether.Core.Identity
{
    /// <summary>
    /// The per-entity facts two rules of the family table need (ARCH §3.2); game-ecs-schema computes them only where
    /// <see cref="FamilyRules.NeedsPerEntityFacts"/> says so.
    /// </summary>
    public struct FamilyFacts
    {
        /// <summary>
        /// Rule 7: the entity is listed in its owner's <c>SubObject</c> buffer and its <c>Owner.m_Owner</c> is that owner,
        /// the criterion of <c>SubObjectSystem.FillOldSubObjectsBuffer</c> (<c>Game.Objects/SubObjectSystem.cs:1172-1195</c>).
        /// </summary>
        public bool InOwnerSubObjectBuffer;

        /// <summary>Rule 15: the owner of a waypoint or segment is a tracked route (one classified as <see cref="Family.Route"/>).</summary>
        public bool OwnerRouteTracked;

        public FamilyFacts(bool inOwnerSubObjectBuffer, bool ownerRouteTracked)
        {
            InOwnerSubObjectBuffer = inOwnerSubObjectBuffer;
            OwnerRouteTracked = ownerRouteTracked;
        }
    }
}
