namespace SkylineTogether.Core
{
    /// <summary>
    /// The replicated components of one entity, serialised on demand by the game layer (spawns, priming, signature
    /// changes). Index order is the order of the entity's type set.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public interface IComponentPayloadSource
    {
        int Count { get; }

        TypeRank TypeAt(int index);

        /// <summary>Appends the canonical bytes of component <paramref name="index"/> (ARCH §4.3.4) and returns the
        /// 32-bit raw hash of its chunk bytes (the change-detection prefilter).</summary>
        void Write(int index, Buffers.ByteWriter writer, out uint rawHash);

        /// <summary>False when the type is not enableable (or is <c>ISerializeAsEnabled</c>).</summary>
        bool TryGetEnabled(int index, out bool enabled);
    }
}
