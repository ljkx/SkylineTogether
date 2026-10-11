namespace SkylineTogether.Core
{
    /// <summary>
    /// The engine side the scheduler pulls records from (implemented by core-replication's
    /// <c>HostReplicationEngine</c>); both return the bytes emitted into the peer's message.
    /// </summary>
    public interface IRecordEmitter
    {
        /// <summary>Encodes the dirty values of <paramref name="typeMask"/> (type-set ordinals) of a slot from the value shadow.</summary>
        int EmitValues(PeerId peer, NetSlot slot, ulong typeMask, Priority priority);

        /// <summary>Encodes queued per-peer channel records up to <paramref name="maxBytes"/>.</summary>
        int EmitChannelBacklog(PeerId peer, Priority priority, int maxBytes);
    }
}
