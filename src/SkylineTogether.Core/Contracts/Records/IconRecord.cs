namespace SkylineTogether.Core
{
    /// <summary>
    /// An ICON record (ARCH §4.6.3): <c>u8 op, NetRef owner, varint prefabRank, u8 priority, u8 layer, u16 flags,
    /// [NetRef target], [float3 location]</c>. Owners and targets may be derived keys (lanes, station stops).
    /// </summary>
    public struct IconRecord
    {
        public IconOp Op;
        public NetRef Owner;
        /// <summary>The notification prefab.</summary>
        public PrefabRank Prefab;
        public byte Priority;
        public byte Layer;
        public ushort Flags;
        public NetRef Target;
        public bool HasLocation;
        public float X, Y, Z;
    }
}
