using System;

namespace SkylineTogether.Core.Follower
{
    /// <summary>
    /// What the game layer executes after a <see cref="LoadHandover"/> step, in this order when several flags are set:
    /// <see cref="ForceLoadedByProbe"/>, <see cref="EnableSimulation"/> or <see cref="DisableSimulation"/>,
    /// <see cref="RestorePausedAfterLoading"/>, <see cref="WriteSentinelSpeed"/>, <see cref="StartFollowerClock"/>,
    /// <see cref="Fault"/>. Everything happens in the same LateUpdate, before <c>SimulationSystem</c> runs, so a system
    /// disabled here is already skipped in this frame's LateUpdate phase.
    /// </summary>
    [Flags]
    public enum HandoverActions : byte
    {
        None = 0,

        /// <summary><c>SimulationSystem.Enabled = false</c> (clients only; nothing in Game reads the flag, ARCH §6.1 step 3).</summary>
        DisableSimulation = 1,

        /// <summary>
        /// <c>SimulationSystem.Enabled = true</c>, <b>still paused</b>: stop driving the follower clock, keep
        /// <c>SharedSettings.instance.gameplay.pausedAfterLoading</c> forced to true, so the update that clears
        /// <c>m_IsLoading</c> sets <c>selectedSpeed = 0</c> and runs no tick (<c>SimulationSystem.cs:209-213</c>), and
        /// write <c>selectedSpeed = 0</c> through the public setter. The forced setting only acts at the moment the flag is
        /// cleared; if the flag is already clear (a read-back that failed for another reason, for example a third writer
        /// between write and read), the re-enabled update would otherwise tick GameSimulation at whatever speed is set
        /// (<c>SimulationSystem.cs:216-256</c>), while with the flag set the write is ignored (<c>:80</c>) and harmless.
        /// Emitted for a retry, and whenever a new load starts while the hand-over holds <c>SimulationSystem</c> disabled
        /// (its loading logic must run, ARCH §6.1 last bullet; <c>OnGamePreload</c> zeroes the speed anyway).
        /// </summary>
        EnableSimulation = 2,

        /// <summary>Restore <c>pausedAfterLoading</c> to the user's value (forced to true for the load, ARCH §7.4, §7.11).</summary>
        RestorePausedAfterLoading = 4,

        /// <summary>Start the follower clock at the snapshot frame; it writes <c>selectedSpeed</c> and reads it back, and
        /// the result is the next step's <see cref="HandoverInputs.SpeedReadBackOk"/>.</summary>
        StartFollowerClock = 8,

        /// <summary>Reflection fallback through probe <c>P-SIMLOADING</c>: write <c>m_IsLoading = false</c> and
        /// <c>m_SelectedSpeed = 0</c> (ARCH §6.1 step 4).</summary>
        ForceLoadedByProbe = 16,

        /// <summary>
        /// Probe-less test of <c>m_IsLoading</c>: write <see cref="LoadHandover.SentinelSpeed"/> through the public
        /// <c>selectedSpeed</c> setter, read it back, then write the previous value again (so no update ever runs at the
        /// sentinel speed, which matters on the host where <c>SimulationSystem</c> stays enabled); report the comparison
        /// as the next step's <see cref="HandoverInputs.SpeedReadBackOk"/>.
        /// </summary>
        WriteSentinelSpeed = 32,

        /// <summary>
        /// The hand-over failed (<see cref="LoadHandover.FailureDetail"/>): clients stop the follower clock, enter
        /// Faulted and request a resync (ARCH §12.2); the host logs it and shows "Degraded: clock".
        /// </summary>
        Fault = 64
    }
}
