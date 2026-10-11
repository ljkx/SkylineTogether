namespace SkylineTogether.Core
{
    /// <summary>
    /// One catalogued component type (ARCH §3.6, §4.2.1): identity, layout facts from the game's <c>TypeManager</c>
    /// and the effective policy (built by core-identity's <c>TypeCatalogBuilder</c> from the game layer's inputs and
    /// <c>policies.json</c>).
    /// </summary>
    public sealed class TypeDescriptor
    {
        public TypeRank Rank;
        /// <summary><c>TypeInfo.StableTypeHash</c>; the sort key of the ranks.</summary>
        public ulong StableTypeHash;
        public string FullName;
        public string AssemblyName;
        public TypeKind Kind;
        public TypeFlags Flags;
        public CodecKind Codec;
        /// <summary>Policy class override; <see cref="ReplicationClass.Excluded"/> means "use the family default"
        /// (exclusion itself is <see cref="TypeFlags.Excluded"/>).</summary>
        public ReplicationClass ClassOverride;
        public int SizeInChunk;
        /// <summary>Element size of buffer types (0 for components).</summary>
        public int ElementSize;
        /// <summary>Number of <c>Entity</c> fields (entity offsets) per value or element.</summary>
        public int EntityOffsetCount;
        /// <summary>Tags the client adds when a value of this type changes (tag rule, ARCH §4.7.7).</summary>
        public TouchFlags ApplyTags;
        /// <summary>Attention topics whose peers get this type at P1 (ARCH §4.8.3).</summary>
        public AttentionTopic Topics;
        /// <summary>Name of the hand-written merge or deferred-field policy, or null.</summary>
        public string MergePolicy;

        public bool Has(TypeFlags flag) => (Flags & flag) == flag;

        public override string ToString() => Rank + " " + (FullName ?? "?");
    }
}
