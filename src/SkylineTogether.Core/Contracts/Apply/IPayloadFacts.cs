namespace SkylineTogether.Core
{
    /// <summary>
    /// Facts about canonical payloads that only the game layer can decode (game-ecs-schema <c>PathStateFacts</c>);
    /// used by the apply planner's path-hold rule (ARCH §4.11.4).
    /// </summary>
    public interface IPayloadFacts
    {
        /// <summary>True for host path state that was not computed yet: an empty <c>PathElement</c> buffer or a
        /// <c>PathInformation</c> with zero distance.</summary>
        bool IsUncomputedPathState(TypeRank type, byte[] buffer, int offset, int count);
    }
}
