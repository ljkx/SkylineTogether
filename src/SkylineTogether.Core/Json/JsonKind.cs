namespace SkylineTogether.Core.Json
{
    /// <summary>The kind of a <see cref="JsonValue"/>.</summary>
    public enum JsonKind : byte
    {
        /// <summary>An explicit <c>null</c>, or a missing member or element (see <see cref="JsonValue"/>).</summary>
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }
}
