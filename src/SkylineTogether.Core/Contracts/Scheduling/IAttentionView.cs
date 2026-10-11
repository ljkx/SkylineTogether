namespace SkylineTogether.Core
{
    /// <summary>What each peer currently observes (ARCH §4.8.3); implemented by core-scheduling's <c>AttentionSets</c>.</summary>
    public interface IAttentionView
    {
        /// <summary>The slot is in the peer's in-view static set (quad-tree query, 1 Hz).</summary>
        bool IsInView(PeerId peer, NetSlot slot);

        /// <summary>The slot is in the inspection closure of the peer's selected or followed entity.</summary>
        bool IsInClosure(PeerId peer, NetSlot slot);

        /// <summary>The mover is inside the peer's motion interest region.</summary>
        bool IsInMotionInterest(PeerId peer, NetSlot slot);

        /// <summary>The peer's active infoview and panel topics.</summary>
        AttentionTopic Topics(PeerId peer);

        bool TypeMatchesTopics(TypeRank type, AttentionTopic topics);
    }
}
