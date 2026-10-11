namespace SkylineTogether.Core
{
    /// <summary>Delivery guarantee of one message (ARCH §9.1).</summary>
    public enum Delivery : byte
    {
        /// <summary>Reliable and ordered within its lane; never lost (the outbox re-queues on Busy, ARCH §9.3).</summary>
        ReliableOrdered = 0,
        /// <summary>Unreliable; may be dropped or reordered; each message must decode on its own.</summary>
        Unreliable = 1
    }
}
