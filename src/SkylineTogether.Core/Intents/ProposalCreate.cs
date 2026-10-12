using System.Collections.Generic;

namespace SkylineTogether.Core.Intents
{
    /// <summary>
    /// An entity the client created by prediction (ARCH §5.2 stage C, §5.4.2): identified by a provisional
    /// <see cref="LocalId"/> until the host binds it (BIND record, ARCH §5.5.1). <see cref="Components"/> carry the
    /// replicated values of the prediction; references to other predicted entities of the same proposal are
    /// provisional <see cref="NetRef"/>s. <see cref="TypeSet"/> names the replicated signature when the client's
    /// registry knows it (<see cref="TypeSetId.None"/> = derive it from the components); <see cref="EnabledBits"/> holds
    /// the enabled bit per ordinal of that type set, as in a SPAWN record.
    /// </summary>
    public sealed class ProposalCreate
    {
        public LocalId Local;

        public Family Family;

        /// <summary>The created prefab (must be unlocked, ARCH §5.4.1 rule 5); <see cref="PrefabRank.None"/> only for
        /// families without prefabs (water sources, mod entities, generic).</summary>
        public PrefabRank Prefab = PrefabRank.None;

        public TypeSetId TypeSet;

        public List<ComponentPayload> Components = new List<ComponentPayload>();

        public ulong EnabledBits;

        public override string ToString() => "create " + Local + " " + Family + " " + Prefab + " (" + (Components == null ? 0 : Components.Count) + " components)";
    }
}
