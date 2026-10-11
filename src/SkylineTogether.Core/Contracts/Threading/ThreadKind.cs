namespace SkylineTogether.Core
{
    /// <summary>
    /// The thread a member may be called on (IMPLEMENTATION-PLAN §3 rule 3). ECS is touched only on the main thread
    /// (ARCH D8); network I/O runs on the network thread; encoding and decoding run on worker threads.
    /// </summary>
    public enum ThreadKind : byte
    {
        /// <summary>The Unity main thread (inside game systems).</summary>
        Main,

        /// <summary>The network thread that polls the transports and pumps the outboxes (ARCH §9.6).</summary>
        Network,

        /// <summary>A codec worker thread (epoch encoding and merging, motion encoding; ARCH §4.5.5).</summary>
        Worker,

        /// <summary>Any thread; the member is thread-safe.</summary>
        Any
    }
}
