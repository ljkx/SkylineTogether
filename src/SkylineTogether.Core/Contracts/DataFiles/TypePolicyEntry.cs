using System.Collections.Generic;

namespace SkylineTogether.Core
{
    /// <summary>
    /// One per-type override of <c>policies.json</c> (ARCH §4.2.7), optionally scoped to one family (for example
    /// <c>PathInformation</c> is OnDemand on movers but S-class line state on the <see cref="Core.Family.Segment"/> family,
    /// ARCH §4.11.3). Unset values fall back to the family default. See <see cref="PolicyTableModel"/> for the JSON schema.
    /// </summary>
    public sealed class TypePolicyEntry
    {
        /// <summary>Full type name of the component or buffer type.</summary>
        public string TypeName;
        /// <summary>The entry applies to this family only; null = every family (family-scoped entries win).</summary>
        public Family? Family;
        /// <summary>Class override; null = family default. (Exclusion is <see cref="TypeFlags.Excluded"/>.)</summary>
        public ReplicationClass? Class;
        /// <summary>Codec override; null = from the catalogued kind.</summary>
        public CodecKind? Codec;
        /// <summary>Policy flags (never the catalog facts Buffer, Enableable, SerializeAsEnabled).</summary>
        public TypeFlags Flags;
        /// <summary>Tag rule: tags a client adds when a value of the type changes (ARCH §4.7.7).</summary>
        public TouchFlags ApplyTags;
        /// <summary>Attention topics that promote the type (ARCH §4.8.3).</summary>
        public AttentionTopic Topics;
        /// <summary>Name of the hand-written merge policy (<see cref="TypeFlags.Merge"/>) or deferred-field policy
        /// (<see cref="TypeFlags.DeferredFields"/>) in the game layer (ARCH §4.2.5); null if none.</summary>
        public string MergePolicy;
        /// <summary>Name of the hand-written authorable-mask function (ARCH §3.8); null if none.</summary>
        public string AuthorableMask;
        /// <summary>Name of the hand-written presence-safe stand-in resolver (ARCH §4.11.3); null if none.</summary>
        public string StandInResolver;
        /// <summary>
        /// For a forward-reference type (<see cref="TypeFlags.ForwardReference"/>): the reverse-buffer types on the
        /// referenced owners that the host re-captures in the same transaction whenever this type changes (the
        /// co-capture table of ARCH §4.2.6, e.g. <c>HouseholdMember</c> → <c>HouseholdCitizen</c>). Empty otherwise.
        /// </summary>
        public IReadOnlyList<string> ReverseBuffers;

        public override string ToString() => (TypeName ?? "?") + (Family.HasValue ? " on " + Family.Value : string.Empty);
    }
}
