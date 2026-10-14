namespace SkylineTogether.Core
{
    /// <summary>Delivery guarantee of one message (ARCHITECTURE.md#transport).</summary>
    public enum Delivery : byte
    {
        /// <summary>Reliable and ordered within its lane; never lost (the outbox re-queues on Busy, ARCHITECTURE.md#outbox-lanes-and-rate-control).</summary>
        ReliableOrdered = 0,
        /// <summary>Unreliable; may be dropped or reordered; each message must decode on its own.</summary>
        Unreliable = 1
    }
}
