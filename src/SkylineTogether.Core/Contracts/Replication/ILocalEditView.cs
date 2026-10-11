namespace SkylineTogether.Core
{
    /// <summary>
    /// The client's own edit in progress, as the route guard needs it (ARCH §4.11.4): entities of the stage-B original
    /// set (with their owned sub-elements) and predicted creations, and pairs covered by pending proposals.
    /// Implemented by game-client-actions' <c>LocalEditState</c>.
    /// </summary>
    public interface ILocalEditView
    {
        bool IsPartOfLocalEdit(NetSlot slot);

        bool IsCoveredByPendingProposal(NetSlot slot, TypeRank type);
    }
}
