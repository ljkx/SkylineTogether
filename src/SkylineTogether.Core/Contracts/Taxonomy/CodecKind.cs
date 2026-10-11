namespace SkylineTogether.Core
{
    /// <summary>Codec families of catalogued types (ARCH §4.3.5).</summary>
    public enum CodecKind : byte
    {
        /// <summary>The type's own <c>ISerializable</c> (component or buffer).</summary>
        Serializable,
        /// <summary>Presence only (signature).</summary>
        EmptyTag,
        /// <summary>The enabled bit of an enableable type.</summary>
        EnableBit,
        /// <summary>A list of <c>NetRef</c>s through the type's entity offsets (data-replicated reverse buffers).</summary>
        EntityRefList,
        /// <summary>The host <c>UpdateFrame</c> shared value of movers (1 byte).</summary>
        UpdateFrame,
        /// <summary>A shared serializable component, set at spawn.</summary>
        Shared,
        /// <summary>A hand-written codec.</summary>
        Custom
    }
}
