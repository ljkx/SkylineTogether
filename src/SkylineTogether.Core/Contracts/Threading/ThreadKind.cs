namespace SkylineTogether.Core
{
    /// <summary>
    /// The thread a member may be called on (ARCHITECTURE.md#processes-and-threads). ECS is touched only on the main
    /// thread. As built, the session polls the transport and pumps the outboxes on the main thread too; only the host's
    /// snapshot pump (<c>Transport.BackgroundPump</c>) runs on a thread of its own.
    /// </summary>
    public enum ThreadKind : byte
    {
        /// <summary>The Unity main thread (inside game systems).</summary>
        Main,

        /// <summary>The network thread that polls the transports and pumps the outboxes (ARCHITECTURE.md#processes-and-threads).</summary>
        Network,

        /// <summary>A codec worker thread (epoch encoding and merging, motion encoding; ARCHITECTURE.md#processes-and-threads).</summary>
        Worker,

        /// <summary>Any thread; the member is thread-safe.</summary>
        Any
    }
}
