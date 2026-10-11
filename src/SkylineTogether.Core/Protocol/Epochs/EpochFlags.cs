using System;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>Flags of an epoch message (ARCH §4.6.2); the values travel on the wire, other bits are rejected.</summary>
    [Flags]
    public enum EpochFlags : byte
    {
        None = 0,

        /// <summary>The epoch E of a join snapshot (ARCH §7.1 step 5).</summary>
        SnapshotBoundary = 1,

        /// <summary>Sent from the epoch log during a joiner's catch-up (ARCH §7.7).</summary>
        CatchUp = 2,

        /// <summary>Carries channel keyframes (system state, prefab state, names, cell maps) instead of deltas.</summary>
        ChannelKeyframe = 4,

        /// <summary>One part of a multi-part epoch; <c>varint partIndex, varint partCount</c> follow the fixed header.
        /// <see cref="EpochWriter"/> sets and clears it from the part count.</summary>
        MultiPart = 8
    }
}
