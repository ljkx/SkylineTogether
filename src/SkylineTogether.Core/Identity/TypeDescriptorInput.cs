namespace SkylineTogether.Core.Identity
{
    /// <summary>
    /// The facts game-ecs-schema collects for one serializable type while walking the serializer library (R01 §4.1)
    /// and <c>TypeManager.GetTypeInfo</c> (R08 §5): <c>StableTypeHash</c>, names, kind, buffer, enableable and
    /// <c>ISerializeAsEnabled</c> flags, <c>SizeInChunk</c>, <c>ElementSize</c> and the number of entity offsets.
    /// <see cref="TypeCatalogBuilder"/> turns these into ranked <see cref="TypeDescriptor"/>s.
    /// </summary>
    public struct TypeDescriptorInput
    {
        public ulong StableTypeHash;
        public string FullName;
        /// <summary>
        /// The simple assembly name (<c>Assembly.GetName().Name</c>). It is part of the catalog fingerprint, so a
        /// display name such as <c>"Game, Version=..."</c> is cut at the first comma by the builder: versions are
        /// compared separately (<c>StableTypeHash</c> covers the assembly name but not its version, R12 §7; ARCH §7.9).
        /// </summary>
        public string AssemblyName;
        public TypeKind Kind;
        public bool IsBuffer;
        public bool IsEnableable;
        public bool SerializeAsEnabled;
        public int SizeInChunk;
        public int ElementSize;
        public int EntityOffsetCount;

        public override string ToString() => (FullName ?? "?") + " (" + StableTypeHash.ToString("X16", System.Globalization.CultureInfo.InvariantCulture) + ")";
    }
}
