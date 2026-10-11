using System;
using System.Globalization;

namespace SkylineTogether.Core.Follower
{
    /// <summary>
    /// The load hand-over of ARCH §6.1: when, after a session load, vanilla <c>SimulationSystem</c> may be disabled
    /// on a client (and the follower clock started), and when the user's <c>pausedAfterLoading</c> may be restored on
    /// either side. Pure state machine; the game layer samples <see cref="HandoverInputs"/> and executes the returned
    /// <see cref="HandoverActions"/>.
    /// <para>
    /// Why it waits. <c>OnGamePreload</c> sets <c>selectedSpeed = 0</c> and the private <c>m_IsLoading = true</c>; while
    /// the flag is set the public <c>selectedSpeed</c> setter ignores every write (<c>SimulationSystem.cs:72-85,
    /// 155-162</c>). Only <c>SimulationSystem.OnUpdate</c> clears the flag, once <c>GameManager.isGameLoading</c> is false
    /// and the loading progress reads 1 (<c>:202-215</c>), and <c>GameManager</c> invokes <c>onGameLoadingComplete</c>
    /// before it sets <c>State.WorldReady</c> (<c>GameManager.cs:1136-1137</c>). Disabling the system inside
    /// <c>OnGameLoadingComplete</c> would therefore leave the flag set for the whole session (speed stuck at 0, frozen
    /// render interpolation, paused water). The world keeps updating while the game loads (<c>State.Loading</c> sorts
    /// after <c>WorldReady</c> and <c>shouldUpdateWorld</c> is <c>state &gt;= WorldReady</c>, <c>GameManager.cs:124-134,
    /// 296</c>), so the loading-progress bookkeeping of a loaded save normally completes during the load and the flag
    /// clears in the update of the WorldReady frame itself.
    /// </para>
    /// <para>
    /// Client sequence: <see cref="OnGameLoadingComplete"/> → <see cref="HandoverState.AwaitWorldReady"/>; frame N (first
    /// step with WorldReady) is only noted → <see cref="HandoverState.AwaitFirstSimulationUpdate"/>, and vanilla runs
    /// its post-load update (paused, because <c>pausedAfterLoading</c> is forced). From frame N+1: as soon as the probe
    /// reads the flag clear, one step returns <see cref="HandoverActions.DisableSimulation"/> |
    /// <see cref="HandoverActions.RestorePausedAfterLoading"/> | <see cref="HandoverActions.StartFollowerClock"/> →
    /// <see cref="HandoverState.Verify"/>, and the next step completes when the clock's speed read back. Fallbacks: the
    /// flag still set <c>reflectionFallbackAfterFrames</c> frames after WorldReady → the same step plus
    /// <see cref="HandoverActions.ForceLoadedByProbe"/>; no probe → disable and <see cref="HandoverActions.WriteSentinelSpeed"/>,
    /// whose read-back decides; a failed read-back → <see cref="HandoverActions.EnableSimulation"/> (still paused) for one
    /// update and <see cref="HandoverState.Retry"/>. Deadline: the first step at or after <c>maxRetryFrames</c> frames after
    /// WorldReady that finds the hand-over incomplete returns <see cref="HandoverActions.Fault"/>, except that every
    /// hand-over gets at least one attempt (a late first step after skipped frames is never failed untried), and a probe
    /// reading the flag clear is always acted on (its read-back then decides).
    /// </para>
    /// <para>
    /// Host variant (<c>isClient</c> false): <c>SimulationSystem</c> stays enabled; the machine only decides when
    /// <c>pausedAfterLoading</c> may be restored, which is after the post-load update that cleared the flag. Restoring it
    /// inside <c>OnGameLoadingComplete</c> would let that update read the user's setting and run up to two real ticks at
    /// speed 1 (<c>round(selectedSpeed · 2)</c>, <c>SimulationSystem.cs:255</c>) before the session is ready.
    /// </para>
    /// <para>
    /// A game load seen while the machine holds <c>SimulationSystem</c> disabled (resync, leaving) returns
    /// <see cref="HandoverActions.EnableSimulation"/>: only the system's own update clears the flag that every
    /// <c>OnGamePreload</c> sets. Frames are counted as the larger of the <see cref="HandoverInputs.FrameNumber"/>
    /// difference and the number of steps since WorldReady, so a frame counter that does not advance cannot stall the
    /// fallbacks or the deadline. Main thread only.
    /// </para>
    /// </summary>
    public sealed class LoadHandover
    {
        /// <summary>
        /// The probe-less test speed (<see cref="HandoverActions.WriteSentinelSpeed"/>): exactly representable, and
        /// different from every speed <c>selectedSpeed</c> holds during a hand-over (0, because <c>pausedAfterLoading</c>
        /// is forced; the time controls only set 1, 2 or 4, <c>TimeUISystem.cs:228-231</c>), so a read-back can only match
        /// if the write took effect. (Photo mode's slider can reach any value from 0 to 8, but not during a load.)
        /// </summary>
        public const float SentinelSpeed = 0.375f;

        /// <summary>Default of <c>reflectionFallbackAfterFrames</c> (ARCH Appendix D).</summary>
        public const int DefaultReflectionFallbackAfterFrames = 10;

        /// <summary>Default of <c>maxRetryFrames</c> (ARCH Appendix D).</summary>
        public const int DefaultMaxRetryFrames = 60;

        private readonly bool _isClient;
        private readonly int _fallbackAfterFrames;
        private readonly int _maxRetryFrames;

        private HandoverState _state;
        private bool _holdsSimulationDisabled;
        private bool _pendingEnable;
        private bool _restoreOwed;
        private bool _verifyingSentinel;
        private int _worldReadyFrame;
        private int _stepsSinceWorldReady;
        private int _lastElapsed;
        private int _attempts;
        private int _framesWaited = -1;
        private bool _usedFallback;
        private bool _usedSentinel;
        private string _lastFailure;
        private string _failureDetail;

        /// <param name="isClient">True on a client (disables <c>SimulationSystem</c>, starts the follower clock); false on
        /// the host (restores <c>pausedAfterLoading</c> only).</param>
        /// <param name="reflectionFallbackAfterFrames">Frames after WorldReady with <c>m_IsLoading</c> still set before the
        /// flag is forced through the probe (at least 1).</param>
        /// <param name="maxRetryFrames">Frames after WorldReady by which the hand-over must be complete, else
        /// <see cref="HandoverActions.Fault"/> (at least <paramref name="reflectionFallbackAfterFrames"/>; the forced attempt
        /// is always made and verified, even when the two are equal).</param>
        public LoadHandover(bool isClient, int reflectionFallbackAfterFrames = DefaultReflectionFallbackAfterFrames, int maxRetryFrames = DefaultMaxRetryFrames)
        {
            if (reflectionFallbackAfterFrames < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(reflectionFallbackAfterFrames), reflectionFallbackAfterFrames, "must be at least 1 frame");
            }
            if (maxRetryFrames < reflectionFallbackAfterFrames)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRetryFrames), maxRetryFrames,
                                                      "must be at least reflectionFallbackAfterFrames (" + Num(reflectionFallbackAfterFrames) + ")");
            }
            _isClient = isClient;
            _fallbackAfterFrames = reflectionFallbackAfterFrames;
            _maxRetryFrames = maxRetryFrames;
            _state = HandoverState.Idle;
        }

        public HandoverState State => _state;

        /// <summary>Why the hand-over failed; null unless <see cref="State"/> is <see cref="HandoverState.Failed"/>.</summary>
        public string FailureDetail => _failureDetail;

        public bool IsClient => _isClient;

        /// <summary>True while a step of this machine disabled <c>SimulationSystem</c> and no later step re-enabled it.</summary>
        public bool HoldsSimulationDisabled => _holdsSimulationDisabled;

        /// <summary>True from <see cref="OnGameLoadingComplete"/> until a step returned
        /// <see cref="HandoverActions.RestorePausedAfterLoading"/> (and again while a retry keeps the session paused).</summary>
        public bool RestoreOwed => _restoreOwed;

        /// <summary>Frames between WorldReady and completion (logged by the self-test, ARCH §16.2 item 4); -1 until complete.</summary>
        public int FramesWaited => _framesWaited;

        /// <summary>Attempts of the current hand-over (steps that acted on a clear, forced or tested flag).</summary>
        public int Attempts => _attempts;

        /// <summary>The current hand-over forced <c>m_IsLoading</c> through the probe at least once.</summary>
        public bool UsedReflectionFallback => _usedFallback;

        /// <summary>The current hand-over had no probe and used the sentinel read-back.</summary>
        public bool UsedSentinel => _usedSentinel;

        /// <summary>Optional log for state transitions (category <see cref="LogCategory.Join"/>); null keeps the machine silent.</summary>
        public ILog Log { get; set; }

        /// <summary>
        /// Arms the hand-over from the session load's <c>OnGameLoadingComplete</c>; <c>SimulationSystem</c> stays enabled
        /// and <c>pausedAfterLoading</c> stays forced. Idempotent while waiting for WorldReady. If the machine still held
        /// <c>SimulationSystem</c> disabled (no step saw the load), the next step re-enables it first, because its
        /// post-load update must run to clear <c>m_IsLoading</c>.
        /// </summary>
        public void OnGameLoadingComplete()
        {
            if (_state == HandoverState.AwaitWorldReady) return;
            if (_holdsSimulationDisabled) _pendingEnable = true;
            ResetAttemptState();
            _restoreOwed = true;
            Transition(HandoverState.AwaitWorldReady, "session load complete; SimulationSystem stays enabled until its first post-load update");
        }

        /// <summary>One step per LateUpdate, before <c>SimulationSystem</c> runs.</summary>
        public HandoverActions Step(in HandoverInputs inputs)
        {
            HandoverActions actions = HandoverActions.None;
            if (_pendingEnable)
            {
                _pendingEnable = false;
                _holdsSimulationDisabled = false;
                actions |= HandoverActions.EnableSimulation;
            }

            switch (_state)
            {
                case HandoverState.Idle:
                    if (inputs.GameLoading) actions |= EnterLoading("a game load started");
                    return actions;

                case HandoverState.Loading:
                    if (!inputs.GameLoading && inputs.WorldReady) Transition(HandoverState.Idle, "the load finished without a session hand-over");
                    return actions;

                case HandoverState.Complete:
                case HandoverState.Failed:
                    if (inputs.GameLoading) actions |= EnterLoading("a new game load started");
                    return actions;

                case HandoverState.AwaitWorldReady:
                    if (!inputs.WorldReady) return actions;
                    // frame N: note it and do nothing else; vanilla SimulationSystem runs its post-load update after us
                    _worldReadyFrame = inputs.FrameNumber;
                    _stepsSinceWorldReady = 0;
                    Transition(HandoverState.AwaitFirstSimulationUpdate,
                               "WorldReady at frame " + Num(inputs.FrameNumber) + "; SimulationSystem runs its post-load update");
                    return actions;
            }

            // AwaitFirstSimulationUpdate, Verify, Retry
            if (inputs.GameLoading) return actions | EnterLoading("a new game load started during the hand-over");
            if (_stepsSinceWorldReady < int.MaxValue) _stepsSinceWorldReady++;
            int elapsed = Elapsed(inputs.FrameNumber);
            return actions | (_state == HandoverState.Verify ? StepVerify(in inputs, elapsed) : Attempt(in inputs, elapsed));
        }

        /// <summary>
        /// Abandons any hand-over (leaving the session, <c>FollowerMode.Exit</c>) and returns to
        /// <see cref="HandoverState.Idle"/>. Returns what the game layer must undo:
        /// <see cref="HandoverActions.EnableSimulation"/> if this machine holds <c>SimulationSystem</c> disabled, and
        /// <see cref="HandoverActions.RestorePausedAfterLoading"/> if <see cref="RestoreOwed"/>.
        /// </summary>
        public HandoverActions Reset()
        {
            HandoverActions undo = HandoverActions.None;
            if (_holdsSimulationDisabled || _pendingEnable) undo |= HandoverActions.EnableSimulation;
            if (_restoreOwed) undo |= HandoverActions.RestorePausedAfterLoading;
            _holdsSimulationDisabled = false;
            _pendingEnable = false;
            _restoreOwed = false;
            ResetAttemptState();
            if (_state != HandoverState.Idle)
            {
                Transition(HandoverState.Idle, "reset" + (undo == HandoverActions.None ? string.Empty : " (undo: " + undo + ")"));
            }
            return undo;
        }

        public override string ToString()
        {
            return "load hand-over (" + Role + "): " + _state
                   + (_state == HandoverState.Complete ? " after " + Num(_framesWaited) + " frame(s)" : string.Empty)
                   + (_attempts > 0 ? ", attempts " + Num(_attempts) : string.Empty)
                   + (_usedFallback ? ", reflection fallback" : string.Empty)
                   + (_usedSentinel ? ", sentinel" : string.Empty)
                   + (_failureDetail != null ? ": " + _failureDetail : string.Empty);
        }

        private string Role => _isClient ? "client" : "host";

        private HandoverActions Attempt(in HandoverInputs inputs, int elapsed)
        {
            // the deadline fails a hand-over only after at least one attempt, so a late first step is never failed untried
            bool pastDeadline = elapsed >= _maxRetryFrames && _attempts > 0;

            if (inputs.SimulationIsLoading.HasValue)
            {
                if (!inputs.SimulationIsLoading.Value)
                {
                    // always act on a clear flag; the read-back in Verify enforces the deadline
                    _attempts++;
                    return Proceed(HandoverActions.None, "m_IsLoading is clear " + Num(elapsed) + " frame(s) after WorldReady");
                }
                if (pastDeadline) return Fail(elapsed);
                if (elapsed < _fallbackAfterFrames)
                {
                    // a delayed clear (the first update may only finish the loading-progress bookkeeping,
                    // SimulationSystem.cs:202-208): wait for the next update
                    return HandoverActions.None;
                }
                _attempts++;
                _usedFallback = true;
                return Proceed(HandoverActions.ForceLoadedByProbe,
                               "m_IsLoading still set " + Num(elapsed) + " frame(s) after WorldReady; forced through probe P-SIMLOADING");
            }

            if (pastDeadline) return Fail(elapsed);
            // no probe: a sentinel written through the public setter reads back only if the flag is clear
            _attempts++;
            _usedSentinel = true;
            _verifyingSentinel = true;
            HandoverActions result = HandoverActions.WriteSentinelSpeed;
            if (_isClient)
            {
                _holdsSimulationDisabled = true;
                result |= HandoverActions.DisableSimulation;
            }
            Transition(HandoverState.Verify, "probe P-SIMLOADING unavailable; testing m_IsLoading with a sentinel speed (attempt " + Num(_attempts) + ")");
            return result;
        }

        private HandoverActions Proceed(HandoverActions extra, string reason)
        {
            _restoreOwed = false;
            if (!_isClient)
            {
                Complete(reason);
                return extra | HandoverActions.RestorePausedAfterLoading;
            }
            _holdsSimulationDisabled = true;
            _verifyingSentinel = false;
            Transition(HandoverState.Verify, reason + "; SimulationSystem disabled, follower clock started (attempt " + Num(_attempts) + ")");
            return extra | HandoverActions.DisableSimulation | HandoverActions.RestorePausedAfterLoading | HandoverActions.StartFollowerClock;
        }

        private HandoverActions StepVerify(in HandoverInputs inputs, int elapsed)
        {
            if (inputs.SpeedReadBackOk)
            {
                if (_verifyingSentinel)
                {
                    _restoreOwed = false;
                    Complete("the sentinel speed read back, so m_IsLoading is clear");
                    return _isClient
                        ? HandoverActions.RestorePausedAfterLoading | HandoverActions.StartFollowerClock
                        : HandoverActions.RestorePausedAfterLoading;
                }
                Complete("selectedSpeed reads back what the follower clock wrote");
                return HandoverActions.None;
            }

            _lastFailure = _verifyingSentinel
                ? "the sentinel speed did not read back (m_IsLoading still set)"
                : "selectedSpeed did not read back what the follower clock wrote (m_IsLoading still set)";
            if (elapsed >= _maxRetryFrames) return Fail(elapsed);

            HandoverActions result = HandoverActions.None;
            if (_isClient)
            {
                // one more vanilla update, still paused (EnableSimulation re-forces pausedAfterLoading), so that
                // SimulationSystem can clear the flag itself
                _holdsSimulationDisabled = false;
                _restoreOwed = true;
                result |= HandoverActions.EnableSimulation;
            }
            Transition(HandoverState.Retry, _lastFailure + "; retrying, " + Num(elapsed) + " frame(s) after WorldReady");
            return result;
        }

        private HandoverActions Fail(int elapsed)
        {
            _failureDetail = "the load hand-over did not complete within " + Num(_maxRetryFrames) + " frames after WorldReady ("
                             + Num(elapsed) + " frames, " + Num(_attempts) + " attempt(s)"
                             + (_usedFallback ? ", reflection fallback tried" : string.Empty)
                             + (_usedSentinel ? ", probe P-SIMLOADING unavailable" : string.Empty) + "): "
                             + (_lastFailure ?? "m_IsLoading never cleared");
            Transition(HandoverState.Failed, _failureDetail);
            if (_isClient) return HandoverActions.Fault;
            // the host keeps playing; never leave the user's setting forced
            _restoreOwed = false;
            return HandoverActions.RestorePausedAfterLoading | HandoverActions.Fault;
        }

        private void Complete(string reason)
        {
            _framesWaited = _lastElapsed;
            Transition(HandoverState.Complete, reason + "; complete after " + Num(_framesWaited) + " frame(s), " + Num(_attempts) + " attempt(s)"
                                               + (_usedFallback ? ", reflection fallback" : string.Empty) + (_usedSentinel ? ", sentinel" : string.Empty));
        }

        private HandoverActions EnterLoading(string reason)
        {
            HandoverActions result = HandoverActions.None;
            if (_holdsSimulationDisabled)
            {
                // vanilla loading logic must run: only SimulationSystem.OnUpdate clears m_IsLoading (ARCH §6.1)
                _holdsSimulationDisabled = false;
                result |= HandoverActions.EnableSimulation;
            }
            ResetAttemptState();
            Transition(HandoverState.Loading, reason + (result == HandoverActions.None ? string.Empty : "; SimulationSystem re-enabled"));
            return result;
        }

        private void ResetAttemptState()
        {
            _verifyingSentinel = false;
            _worldReadyFrame = 0;
            _stepsSinceWorldReady = 0;
            _lastElapsed = 0;
            _attempts = 0;
            _framesWaited = -1;
            _usedFallback = false;
            _usedSentinel = false;
            _lastFailure = null;
            _failureDetail = null;
        }

        private int Elapsed(int frameNumber)
        {
            long delta = (long)frameNumber - _worldReadyFrame;
            if (delta < _stepsSinceWorldReady) delta = _stepsSinceWorldReady;
            _lastElapsed = delta > int.MaxValue ? int.MaxValue : (int)delta;
            return _lastElapsed;
        }

        private void Transition(HandoverState next, string reason)
        {
            HandoverState previous = _state;
            _state = next;
            ILog log = Log;
            if (log == null) return;
            LogLevel level = next == HandoverState.Failed ? LogLevel.Error
                           : next == HandoverState.Complete || next == HandoverState.Retry ? LogLevel.Info
                           : LogLevel.Debug;
            if (!log.IsEnabled(level)) return;
            log.Write(level, LogCategory.Join, "load hand-over (" + Role + ") " + previous + " -> " + next + ": " + reason);
        }

        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
