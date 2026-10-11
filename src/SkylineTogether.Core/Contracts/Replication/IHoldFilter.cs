namespace SkylineTogether.Core
{
    /// <summary>Pairs covered by a pending proposal of this client, whose incoming host values are held (ARCH §5.5.4);
    /// implemented by core-intents' <c>ProposalTracker</c>.</summary>
    public interface IHoldFilter
    {
        bool IsHeld(NetSlot slot, TypeRank type);
    }
}
