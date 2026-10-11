namespace SkylineTogether.Core
{
    /// <summary>
    /// A RESULT record (ARCH §4.6.3, §5.5): the host's verdict on proposal <see cref="ProposalSeq"/> of peer
    /// <see cref="Origin"/>; <see cref="RejectReason.None"/> means accepted. Only the origin peer acts on it.
    /// </summary>
    public struct ResultRecord
    {
        public PeerId Origin;
        public uint ProposalSeq;
        public RejectReason Reason;
        /// <summary>The conflicting slot of a rejection, when there is one.</summary>
        public NetSlot ConflictSlot;
    }
}
