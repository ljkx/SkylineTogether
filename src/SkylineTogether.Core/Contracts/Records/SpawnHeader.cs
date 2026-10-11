namespace SkylineTogether.Core
{
    /// <summary>
    /// Header of a SPAWN record (ARCH §4.6.3): <c>varint slot, u8 gen, varint prefabRank+1, varint typeSetId,
    /// u8 flags, [u8 updateFrame], varint payloadCount</c>, followed by the components and the enabled bits.
    /// Encoded by core-protocol's <c>RecordWriter</c>.
    /// </summary>
    public struct SpawnHeader
    {
        public NetSlot Slot;
        public byte Generation;
        /// <summary>Prefab rank; <see cref="PrefabRank.None"/> for entities without <c>PrefabRef</c> (water sources, mod entities).</summary>
        public PrefabRank Prefab;
        public TypeSetId TypeSet;
        public SpawnFlags Flags;
        /// <summary>The host's <c>UpdateFrame</c> (movers; only with <see cref="SpawnFlags.HasUpdateFrame"/>).</summary>
        public byte UpdateFrame;
        /// <summary>Number of component payloads that follow.</summary>
        public int ComponentCount;
    }
}
