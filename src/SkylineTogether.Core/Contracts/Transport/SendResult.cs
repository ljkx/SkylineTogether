namespace SkylineTogether.Core
{
    /// <summary>Outcome of <see cref="IConnection.Send"/> and <see cref="IPeerOutput.Send"/>.</summary>
    public enum SendResult : byte
    {
        /// <summary>Accepted (copied into the transport or the outbox queue).</summary>
        Ok,
        /// <summary>The send buffer is full (Steam <c>k_EResultLimitExceeded</c>); retry later (ARCHITECTURE.md#outbox-lanes-and-rate-control).</summary>
        Busy,
        /// <summary>The connection is closed.</summary>
        Closed,
        /// <summary>Larger than the transport's message cap.</summary>
        TooLarge,
        /// <summary>Any other transport error.</summary>
        Failed
    }
}
