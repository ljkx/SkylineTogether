namespace SkylineTogether.Core.Follower
{
    /// <summary>
    /// The game state <see cref="LoadHandover"/> reads in each step, sampled by the game layer in LateUpdate before
    /// <c>SimulationSystem</c> runs (<c>ClientClockSystem</c> on clients, the host session adjustments on the host).
    /// </summary>
    public struct HandoverInputs
    {
        /// <summary><c>GameManager.instance.isGameLoading</c>: the state is Loading or booting
        /// (<c>GameManager.cs:264-274</c>).</summary>
        public bool GameLoading;

        /// <summary><c>GameManager.instance.state == GameManager.State.WorldReady</c>.</summary>
        public bool WorldReady;

        /// <summary>
        /// <c>SimulationSystem.m_IsLoading</c> read through probe <c>P-SIMLOADING</c> (<c>SimulationSystem.cs:62, 161,
        /// 211</c>); null when the probe is unavailable, in which case the sentinel read-back decides (ARCH §6.1 step 4).
        /// </summary>
        public bool? SimulationIsLoading;

        /// <summary>
        /// Whether the speed the game layer wrote in response to the previous step read back unchanged: the follower
        /// clock's first <c>selectedSpeed</c> after <see cref="HandoverActions.StartFollowerClock"/>, or
        /// <see cref="LoadHandover.SentinelSpeed"/> after <see cref="HandoverActions.WriteSentinelSpeed"/>. The public
        /// setter ignores every write while <c>m_IsLoading</c> is set (<c>SimulationSystem.cs:72-85</c>), so a
        /// matching read-back proves the flag is clear. Read only in <see cref="HandoverState.Verify"/>.
        /// <para>
        /// The game layer reads the value back right after writing it, in the same LateUpdate, so no other writer
        /// (<c>TimeUISystem</c>'s focus barrier, <c>ErrorDialogManager</c>, photo mode) can come in between, and keeps the
        /// result for the next step. A clock that starts paused writes 0, which is also the value a still-set flag
        /// leaves in place, so that read-back proves nothing: the game layer then reports true only if probe
        /// <c>P-SIMLOADING</c> reads <c>m_IsLoading</c> false, or (no probe) if a <see cref="LoadHandover.SentinelSpeed"/>
        /// write reads back (followed by writing 0 again).
        /// </para>
        /// </summary>
        public bool SpeedReadBackOk;

        /// <summary>The frame counter (<c>UnityEngine.Time.frameCount</c>), used to count frames since WorldReady.</summary>
        public int FrameNumber;

        public override string ToString()
        {
            return "frame " + FrameNumber + (GameLoading ? " loading" : string.Empty) + (WorldReady ? " worldReady" : string.Empty)
                   + " m_IsLoading=" + (SimulationIsLoading.HasValue ? (SimulationIsLoading.Value ? "true" : "false") : "n/a")
                   + " readBack=" + (SpeedReadBackOk ? "ok" : "failed");
        }
    }
}
