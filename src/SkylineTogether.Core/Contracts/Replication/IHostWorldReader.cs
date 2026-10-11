namespace SkylineTogether.Core
{
    /// <summary>
    /// Live ECS reads the host engine needs outside the normal capture paths (repairs, coalesced S/G flushes);
    /// implemented by game-host-capture, called only inside the capture window.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public interface IHostWorldReader
    {
        /// <summary>Appends the canonical bytes of (slot, type); false when the slot is unbound or dead or lacks the type.</summary>
        bool TrySerialize(NetSlot slot, TypeRank type, Buffers.ByteWriter writer, out uint rawHash);

        /// <summary>Describes a bound, live entity for a full re-spawn; false when it no longer exists.</summary>
        bool TryDescribe(NetSlot slot, out SpawnObservation spawn, out IComponentPayloadSource payloads);
    }
}
