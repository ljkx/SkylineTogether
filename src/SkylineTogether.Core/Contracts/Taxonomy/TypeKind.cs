namespace SkylineTogether.Core
{
    /// <summary>How a catalogued component type is serialised (ARCH §3.6).</summary>
    public enum TypeKind : byte
    {
        /// <summary>Data through the type's own <c>ISerializable</c>.</summary>
        Serializable,
        /// <summary>A zero-size <c>IEmptySerializable</c>: presence only.</summary>
        EmptyTag,
        /// <summary>A data-carrying <c>IEmptySerializable</c> (geometry, reverse buffers): presence only, unless the
        /// policy replicates it as data (ARCH §4.2.4).</summary>
        EmptyData,
        /// <summary>A shared serializable component (set at spawn).</summary>
        SharedSerializable
    }
}
