namespace SkylineTogether.Core
{
    /// <summary>Session states shown by the HUD sync pill and used by the session state machines (ARCH §11.5).</summary>
    public enum SessionPhase : byte
    {
        Idle,
        /// <summary>Host session start: binding and shadow priming (ARCH §4.4.8).</summary>
        PreparingSession,
        Hosting,
        Connecting,
        Handshake,
        /// <summary>The handshake found blocking differences; the mismatch report is shown.</summary>
        Mismatch,
        AwaitingSnapshot,
        Downloading,
        Loading,
        CatchingUp,
        /// <summary>Playable; value verification waits for a full host scan cycle (ARCH §7.6).</summary>
        LiveVerifying,
        Synced,
        Repairing,
        Resyncing,
        /// <summary>The host announced a stall (snapshot or save, ARCH §6.2).</summary>
        HostBusy,
        /// <summary>Backlog above 2 s.</summary>
        Lagging,
        /// <summary>A feature is disabled (probe failure, quarantined type, disabled channel).</summary>
        Degraded,
        Disconnected,
        /// <summary>A structural apply fault froze the follower; a resync follows (ARCH §12.2).</summary>
        Faulted,
        Ended
    }
}
