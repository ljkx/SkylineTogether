namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// The repair ladder (ARCH §8.5); in a <see cref="RepairRequestMessage"/> a hint only, the host decides. The values
    /// travel on the wire.
    /// </summary>
    public enum RepairLevel : byte
    {
        /// <summary>A pair the client wrote locally outside the applier (leak repair).</summary>
        Leak,
        /// <summary>Re-send the listed entities.</summary>
        Entity,
        /// <summary>Re-send the bucket.</summary>
        Bucket,
        /// <summary>Domain rebaseline of the class over the bulk lane.</summary>
        Rebaseline,
        /// <summary>Snapshot resync.</summary>
        Snapshot
    }
}
