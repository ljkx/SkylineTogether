namespace SkylineTogether.Core
{
    /// <summary>
    /// The per-peer scheduler (ARCH §4.8.4), implemented by core-scheduling's <c>PeerScheduler</c> and run by the host
    /// engine's frame work on a worker thread after the capture window closed.
    /// </summary>
    [ThreadAffinity(ThreadKind.Worker)]
    public interface IPeerScheduler
    {
        void BeginFrame(double deltaSeconds, int peerCount);

        /// <summary>Schedules one peer's P1/P2/P3 work for this frame (P0 is accounted, never throttled); returns the bytes scheduled.</summary>
        int RunPeer(PeerId peer, in PeerLinkState link, int p0BytesThisFrame, IPeerDirtyView dirty, IAttentionView attention,
                    IRecordEmitter emitter, IMotionBudgetSink motion);

        /// <summary>The peer's P0 backlog stayed above the threshold too long (automatic speed cap input, ARCH §4.8.5).</summary>
        bool IsP0Overloaded(PeerId peer, out double backlogSeconds);
    }
}
