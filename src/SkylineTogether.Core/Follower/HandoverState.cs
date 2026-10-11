namespace SkylineTogether.Core.Follower
{
    /// <summary>States of the <see cref="LoadHandover"/> state machine (ARCH §6.1).</summary>
    public enum HandoverState : byte
    {
        /// <summary>No session load is being handed over; the machine only watches for a game load to start.</summary>
        Idle,

        /// <summary>A game load is in progress (<c>GameManager.isGameLoading</c>); vanilla <c>SimulationSystem</c> runs its
        /// loading logic and must be enabled.</summary>
        Loading,

        /// <summary>The session load's <c>OnGameLoadingComplete</c> ran; waiting for the first LateUpdate with
        /// <c>GameManager.state == WorldReady</c>, which the game sets right after those handlers
        /// (<c>GameManager.cs:1136-1137</c>).</summary>
        AwaitWorldReady,

        /// <summary>Frame N (the first WorldReady frame) was noted and nothing else done; vanilla <c>SimulationSystem</c>
        /// runs its post-load update, which clears <c>m_IsLoading</c> (<c>SimulationSystem.cs:202-215</c>). From frame
        /// N+1 the flag is checked before every update.</summary>
        AwaitFirstSimulationUpdate,

        /// <summary>An attempt was made (on clients <c>SimulationSystem</c> is disabled); the next step checks the
        /// <c>selectedSpeed</c> read-back.</summary>
        Verify,

        /// <summary>The read-back failed: on clients <c>SimulationSystem</c> is enabled again, still paused, for one more
        /// update; the next step attempts again.</summary>
        Retry,

        /// <summary>Done. Clients: <c>SimulationSystem</c> stays disabled and the follower clock drives time. Host:
        /// <c>pausedAfterLoading</c> is restored and <c>SimulationSystem</c> keeps running.</summary>
        Complete,

        /// <summary>The hand-over did not complete within the retry budget (see <see cref="LoadHandover.FailureDetail"/>);
        /// clients enter Faulted and resync.</summary>
        Failed
    }
}
