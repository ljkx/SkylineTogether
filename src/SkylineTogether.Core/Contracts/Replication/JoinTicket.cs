namespace SkylineTogether.Core
{
    /// <summary>The host's record of a joiner at its snapshot frame (ARCH §7.1 step 5, §7.4).</summary>
    public struct JoinTicket
    {
        public PeerId Peer;
        /// <summary>Epoch E produced by the snapshot frame's sweep.</summary>
        public EpochId SnapshotEpoch;
        /// <summary>The host frame index F0 at the snapshot.</summary>
        public uint SnapshotTick;
        /// <summary>The host's build-order counter at the snapshot (the joiner restores <c>GenerateEdgesSystem</c>'s counter from it).</summary>
        public uint HostBuildOrderNext;
    }
}
