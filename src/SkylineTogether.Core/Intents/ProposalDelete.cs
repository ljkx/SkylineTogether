namespace SkylineTogether.Core.Intents
{
    /// <summary>A replicated entity the client deleted (docs/history/ARCHITECTURE-original.md §5.4.2: the host adds <c>Deleted</c> plus closure touches).</summary>
    public sealed class ProposalDelete
    {
        public NetSlot Slot;

        public ushort BaseRevision;

        public override string ToString() => "delete " + Slot + " rev " + BaseRevision;
    }
}
