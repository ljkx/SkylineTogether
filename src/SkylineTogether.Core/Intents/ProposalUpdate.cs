using System.Collections.Generic;

namespace SkylineTogether.Core.Intents
{
    /// <summary>
    /// Changed player-authorable components of an existing replicated entity (docs/history/ARCHITECTURE-original.md §5.2 rules, §5.4.1 rule 4).
    /// <see cref="BaseRevision"/> is the host revision the client's ledger held for the slot (docs/history/ARCHITECTURE-original.md §3.8); a stale
    /// revision rejects the proposal as a conflict (rule 2).
    /// </summary>
    public sealed class ProposalUpdate
    {
        public NetSlot Slot;

        public ushort BaseRevision;

        public List<ComponentPayload> Components = new List<ComponentPayload>();

        public override string ToString() => "update " + Slot + " rev " + BaseRevision + " (" + (Components == null ? 0 : Components.Count) + " components)";
    }
}
