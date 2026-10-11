using System;
using SkylineTogether.Core.Follower;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Follower
{
    /// <summary>
    /// <see cref="LoadHandover"/> (ARCH §6.1, Appendix D): the client happy path, a delayed clear, the host variant, the
    /// reflection fallback, the probe-less sentinel path, retries and the retry limit, and loads that interrupt or
    /// follow a hand-over.
    /// </summary>
    public sealed class LoadHandoverTests
    {
        private const HandoverActions ProceedClient =
            HandoverActions.DisableSimulation | HandoverActions.RestorePausedAfterLoading | HandoverActions.StartFollowerClock;

        private const int N = 1000;   // the first WorldReady frame in most tests

        /// <summary>One LateUpdate sample. Defaults: WorldReady, not loading, no probe, no read-back.</summary>
        private static HandoverInputs At(int frame, bool? simLoading = null, bool readBack = false, bool loading = false, bool worldReady = true)
        {
            return new HandoverInputs
            {
                FrameNumber = frame,
                GameLoading = loading,
                WorldReady = worldReady && !loading,
                SimulationIsLoading = simLoading,
                SpeedReadBackOk = readBack
            };
        }

        /// <summary>A machine that saw the load and was armed; the next step at frame N is the first WorldReady frame.</summary>
        private static LoadHandover Armed(bool isClient, int fallback = 10, int maxRetry = 60)
        {
            var handover = new LoadHandover(isClient, fallback, maxRetry);
            Assert.Equal(HandoverActions.None, handover.Step(At(N - 50, simLoading: true, loading: true)));
            Assert.Equal(HandoverState.Loading, handover.State);
            handover.OnGameLoadingComplete();
            Assert.Equal(HandoverState.AwaitWorldReady, handover.State);
            Assert.True(handover.RestoreOwed);
            return handover;
        }

        [Test]
        public void ClientHappyPath()
        {
            LoadHandover h = Armed(true);
            // frame N: noted only; vanilla SimulationSystem runs its post-load update after this step
            Assert.Equal(HandoverActions.None, h.Step(At(N, simLoading: true)));
            Assert.Equal(HandoverState.AwaitFirstSimulationUpdate, h.State);
            Assert.False(h.HoldsSimulationDisabled, "SimulationSystem stays enabled until the flag is clear");

            // frame N+1: the flag is clear -> disable, restore and start the clock in one step
            Assert.Equal(ProceedClient, h.Step(At(N + 1, simLoading: false)));
            Assert.Equal(HandoverState.Verify, h.State);
            Assert.True(h.HoldsSimulationDisabled);
            Assert.False(h.RestoreOwed);

            // frame N+2: the clock's speed read back
            Assert.Equal(HandoverActions.None, h.Step(At(N + 2, simLoading: false, readBack: true)));
            Assert.Equal(HandoverState.Complete, h.State);
            Assert.Equal(2, h.FramesWaited);
            Assert.Equal(1, h.Attempts);
            Assert.False(h.UsedReflectionFallback);
            Assert.False(h.UsedSentinel);
            Assert.Null(h.FailureDetail);

            // nothing more to do while the session runs
            for (int f = N + 3; f < N + 200; f++) Assert.Equal(HandoverActions.None, h.Step(At(f, simLoading: false)));
            Assert.Equal(HandoverState.Complete, h.State);
            Assert.True(h.HoldsSimulationDisabled);
        }

        [Test]
        public void FrameNIsOnlyNotedEvenWhenTheFlagLooksClear()
        {
            LoadHandover h = Armed(true);
            Assert.Equal(HandoverActions.None, h.Step(At(N, simLoading: false)));
            Assert.Equal(HandoverState.AwaitFirstSimulationUpdate, h.State);
            Assert.Equal(ProceedClient, h.Step(At(N + 1, simLoading: false)));
        }

        [Test]
        public void ADelayedClearWaits()
        {
            LoadHandover h = Armed(true);
            h.Step(At(N, simLoading: true));
            Assert.Equal(HandoverActions.None, h.Step(At(N + 1, simLoading: true)), "first update only finished the progress bookkeeping");
            Assert.Equal(HandoverActions.None, h.Step(At(N + 2, simLoading: true)));
            Assert.Equal(HandoverState.AwaitFirstSimulationUpdate, h.State);
            Assert.False(h.HoldsSimulationDisabled);
            Assert.Equal(ProceedClient, h.Step(At(N + 3, simLoading: false)));
            Assert.Equal(HandoverActions.None, h.Step(At(N + 4, readBack: true)));
            Assert.Equal(HandoverState.Complete, h.State);
            Assert.Equal(4, h.FramesWaited);
        }

        [Test]
        public void WaitsForWorldReady()
        {
            LoadHandover h = Armed(true);
            // OnGameLoadingComplete handlers run before State.WorldReady (GameManager.cs:1136-1137)
            Assert.Equal(HandoverActions.None, h.Step(At(N - 1, simLoading: true, loading: true)));
            Assert.Equal(HandoverState.AwaitWorldReady, h.State);
            Assert.Equal(HandoverActions.None, h.Step(At(N - 1, simLoading: true, worldReady: false)));
            Assert.Equal(HandoverState.AwaitWorldReady, h.State);
            h.Step(At(N, simLoading: true));
            Assert.Equal(HandoverState.AwaitFirstSimulationUpdate, h.State);
        }

        [Test]
        public void HostVariantRestoresOnly()
        {
            LoadHandover h = Armed(false);
            Assert.False(h.IsClient);
            Assert.Equal(HandoverActions.None, h.Step(At(N, simLoading: true)));
            Assert.Equal(HandoverActions.None, h.Step(At(N + 1, simLoading: true)), "a delayed clear waits on the host too");
            Assert.Equal(HandoverActions.RestorePausedAfterLoading, h.Step(At(N + 2, simLoading: false)));
            Assert.Equal(HandoverState.Complete, h.State);
            Assert.False(h.HoldsSimulationDisabled);
            Assert.False(h.RestoreOwed);
            Assert.Equal(2, h.FramesWaited);
            // the host never releases what it never took
            Assert.Equal(HandoverActions.None, h.Step(At(N + 3, loading: true)));
            Assert.Equal(HandoverState.Loading, h.State);
        }

        [Test]
        public void ReflectionFallbackAfterTenFrames()
        {
            LoadHandover h = Armed(true);
            h.Step(At(N, simLoading: true));
            for (int f = N + 1; f < N + 10; f++)
            {
                Assert.Equal(HandoverActions.None, h.Step(At(f, simLoading: true)), "frame N+" + (f - N));
            }
            Assert.Equal(HandoverActions.ForceLoadedByProbe | ProceedClient, h.Step(At(N + 10, simLoading: true)));
            Assert.True(h.UsedReflectionFallback);
            Assert.Equal(HandoverState.Verify, h.State);
            Assert.Equal(HandoverActions.None, h.Step(At(N + 11, simLoading: false, readBack: true)));
            Assert.Equal(HandoverState.Complete, h.State);
            Assert.Equal(11, h.FramesWaited);
        }

        [Test]
        public void HostReflectionFallback()
        {
            LoadHandover h = Armed(false, fallback: 3, maxRetry: 10);
            h.Step(At(N, simLoading: true));
            Assert.Equal(HandoverActions.None, h.Step(At(N + 1, simLoading: true)));
            Assert.Equal(HandoverActions.None, h.Step(At(N + 2, simLoading: true)));
            Assert.Equal(HandoverActions.ForceLoadedByProbe | HandoverActions.RestorePausedAfterLoading, h.Step(At(N + 3, simLoading: true)));
            Assert.Equal(HandoverState.Complete, h.State);
        }

        [Test]
        public void ProbeLessSentinelPath()
        {
            LoadHandover h = Armed(true);
            Assert.Equal(HandoverActions.None, h.Step(At(N)));
            Assert.Equal(HandoverActions.DisableSimulation | HandoverActions.WriteSentinelSpeed, h.Step(At(N + 1)));
            Assert.Equal(HandoverState.Verify, h.State);
            Assert.True(h.HoldsSimulationDisabled);
            Assert.True(h.RestoreOwed, "nothing is restored before the read-back proved the flag clear");
            Assert.Equal(HandoverActions.RestorePausedAfterLoading | HandoverActions.StartFollowerClock, h.Step(At(N + 2, readBack: true)));
            Assert.Equal(HandoverState.Complete, h.State);
            Assert.True(h.UsedSentinel);
            Assert.False(h.RestoreOwed);
            Assert.Equal(2, h.FramesWaited);
            Assert.True(LoadHandover.SentinelSpeed > 0f && LoadHandover.SentinelSpeed < 1f, "neither paused nor a time-control speed (1, 2, 4)");
        }

        [Test]
        public void SentinelFailureRetriesWithSimulationEnabledForOneUpdate()
        {
            LoadHandover h = Armed(true);
            h.Step(At(N));
            Assert.Equal(HandoverActions.DisableSimulation | HandoverActions.WriteSentinelSpeed, h.Step(At(N + 1)));
            Assert.Equal(HandoverActions.EnableSimulation, h.Step(At(N + 2, readBack: false)));
            Assert.Equal(HandoverState.Retry, h.State);
            Assert.False(h.HoldsSimulationDisabled);
            Assert.Equal(HandoverActions.DisableSimulation | HandoverActions.WriteSentinelSpeed, h.Step(At(N + 3)));
            Assert.Equal(HandoverState.Verify, h.State);
            Assert.Equal(HandoverActions.RestorePausedAfterLoading | HandoverActions.StartFollowerClock, h.Step(At(N + 4, readBack: true)));
            Assert.Equal(HandoverState.Complete, h.State);
            Assert.Equal(2, h.Attempts);
            Assert.Equal(4, h.FramesWaited);
        }

        [Test]
        public void HostSentinelPathNeverDisablesSimulation()
        {
            LoadHandover h = Armed(false);
            h.Step(At(N));
            Assert.Equal(HandoverActions.WriteSentinelSpeed, h.Step(At(N + 1)));
            Assert.Equal(HandoverActions.None, h.Step(At(N + 2, readBack: false)));
            Assert.Equal(HandoverState.Retry, h.State);
            Assert.Equal(HandoverActions.WriteSentinelSpeed, h.Step(At(N + 3)));
            Assert.Equal(HandoverActions.RestorePausedAfterLoading, h.Step(At(N + 4, readBack: true)));
            Assert.Equal(HandoverState.Complete, h.State);
            Assert.False(h.HoldsSimulationDisabled);
        }

        [Test]
        public void ReadBackFailureAfterAClearProbeRetries()
        {
            LoadHandover h = Armed(true);
            h.Step(At(N, simLoading: true));
            Assert.Equal(ProceedClient, h.Step(At(N + 1, simLoading: false)));
            // the probe misread: the clock's speed did not stick; one more paused vanilla update, then try again
            Assert.Equal(HandoverActions.EnableSimulation, h.Step(At(N + 2, simLoading: false, readBack: false)));
            Assert.Equal(HandoverState.Retry, h.State);
            Assert.True(h.RestoreOwed, "EnableSimulation re-forces the pause");
            Assert.Equal(ProceedClient, h.Step(At(N + 3, simLoading: false)));
            Assert.Equal(HandoverActions.None, h.Step(At(N + 4, readBack: true)));
            Assert.Equal(HandoverState.Complete, h.State);
            Assert.Equal(2, h.Attempts);
        }

        [Test]
        public void RetryLimitFaultsOnTheSentinelPath()
        {
            var log = new ListLog();
            LoadHandover h = Armed(true);
            h.Log = log;
            h.Step(At(N));
            int faultFrame = -1;
            int disables = 0, enables = 0;
            for (int f = N + 1; f <= N + 100; f++)
            {
                HandoverActions a = h.Step(At(f, readBack: false));
                Assert.Equal(HandoverActions.None, a & (HandoverActions.RestorePausedAfterLoading | HandoverActions.StartFollowerClock),
                             "never restores or starts the clock without a successful read-back");
                if ((a & HandoverActions.DisableSimulation) != 0) disables++;
                if ((a & HandoverActions.EnableSimulation) != 0) enables++;
                if ((a & HandoverActions.Fault) != 0)
                {
                    faultFrame = f;
                    Assert.Equal(HandoverActions.Fault, a);
                    break;
                }
            }
            Assert.Equal(N + 60, faultFrame, "the read-back that fails at 60 frames after WorldReady faults");
            Assert.Equal(HandoverState.Failed, h.State);
            Assert.Equal(30, h.Attempts);
            Assert.Equal(30, disables);
            Assert.Equal(29, enables);
            Assert.True(h.UsedSentinel);
            Assert.Contains("within 60 frames", h.FailureDetail);
            Assert.Contains("probe P-SIMLOADING unavailable", h.FailureDetail);
            Assert.Contains("sentinel speed did not read back", h.FailureDetail);
            Assert.True(log.Contains("Error [join] load hand-over (client) Verify -> Failed"), "the failure is logged as an error");
            // Failed is terminal until the next load
            Assert.Equal(HandoverActions.None, h.Step(At(N + 61, readBack: true)));
            Assert.Equal(HandoverState.Failed, h.State);
        }

        [Test]
        public void RetryLimitFaultsWhenTheFlagNeverClears()
        {
            LoadHandover h = Armed(true);
            h.Step(At(N, simLoading: true));
            HandoverActions last = HandoverActions.None;
            int forces = 0;
            int f = N + 1;
            for (; f <= N + 100; f++)
            {
                last = h.Step(At(f, simLoading: true, readBack: false));
                if ((last & HandoverActions.ForceLoadedByProbe) != 0) forces++;
                if ((last & HandoverActions.Fault) != 0) break;
            }
            Assert.Equal(HandoverActions.Fault, last);
            Assert.Equal(N + 60, f, "the step 60 frames after WorldReady faults, as on the sentinel path");
            Assert.Equal(25, forces, "forced at N+10, N+12, ..., N+58");
            Assert.Equal(25, h.Attempts);
            Assert.Equal(HandoverState.Failed, h.State);
            Assert.Contains("reflection fallback tried", h.FailureDetail);
            Assert.Contains("did not read back", h.FailureDetail);
            Assert.False(h.HoldsSimulationDisabled, "the last retry re-enabled SimulationSystem (still paused: m_IsLoading keeps the speed at 0)");
            Assert.True(h.RestoreOwed, "the retry re-forced the pause; Reset or the next hand-over restores it");
        }

        [Test]
        public void ALateFirstStepStillGetsOneAttempt()
        {
            // frames counted while no step ran (the deadline has passed) must not fail a hand-over untried
            LoadHandover forced = Armed(true);
            forced.Step(At(N, simLoading: true));
            Assert.Equal(HandoverActions.ForceLoadedByProbe | ProceedClient, forced.Step(At(N + 100, simLoading: true)));
            Assert.Equal(HandoverActions.None, forced.Step(At(N + 101, readBack: true)));
            Assert.Equal(HandoverState.Complete, forced.State);
            Assert.Equal(101, forced.FramesWaited);

            LoadHandover clear = Armed(true);
            clear.Step(At(N, simLoading: true));
            Assert.Equal(ProceedClient, clear.Step(At(N + 100, simLoading: false)));
            Assert.Equal(HandoverActions.Fault, clear.Step(At(N + 101, readBack: false)), "its failed read-back is past the deadline");
            Assert.Equal(HandoverState.Failed, clear.State);

            LoadHandover sentinel = Armed(true);
            sentinel.Step(At(N));
            Assert.Equal(HandoverActions.DisableSimulation | HandoverActions.WriteSentinelSpeed, sentinel.Step(At(N + 100)));
            Assert.Equal(HandoverActions.RestorePausedAfterLoading | HandoverActions.StartFollowerClock, sentinel.Step(At(N + 101, readBack: true)));
            Assert.Equal(HandoverState.Complete, sentinel.State);
        }

        [Test]
        public void AClearFlagIsActedOnEvenAtTheDeadline()
        {
            LoadHandover h = Armed(true, fallback: 4, maxRetry: 8);
            h.Step(At(N, simLoading: true));
            for (int f = N + 1; f < N + 4; f++) Assert.Equal(HandoverActions.None, h.Step(At(f, simLoading: true)));
            Assert.Equal(HandoverActions.ForceLoadedByProbe | ProceedClient, h.Step(At(N + 4, simLoading: true)));
            Assert.Equal(HandoverActions.EnableSimulation, h.Step(At(N + 5, readBack: false)));
            Assert.Equal(HandoverActions.ForceLoadedByProbe | ProceedClient, h.Step(At(N + 6, simLoading: true)));
            Assert.Equal(HandoverActions.EnableSimulation, h.Step(At(N + 7, readBack: false)));
            // N+8 is the deadline, but the flag finally reads clear: act on it; the read-back decides
            Assert.Equal(ProceedClient, h.Step(At(N + 8, simLoading: false)));
            Assert.Equal(HandoverActions.None, h.Step(At(N + 9, readBack: true)));
            Assert.Equal(HandoverState.Complete, h.State);
            Assert.Equal(3, h.Attempts);

            LoadHandover stillSet = Armed(true, fallback: 4, maxRetry: 8);
            stillSet.Step(At(N, simLoading: true));
            stillSet.Step(At(N + 4, simLoading: true));
            stillSet.Step(At(N + 5, readBack: false));
            Assert.Equal(HandoverActions.Fault, stillSet.Step(At(N + 8, simLoading: true)), "still set at the deadline: fault without another force");
        }

        [Test]
        public void EqualBudgetsStillTryTheForcedAttempt()
        {
            LoadHandover h = Armed(true, fallback: 10, maxRetry: 10);
            h.Step(At(N, simLoading: true));
            for (int f = N + 1; f < N + 10; f++) h.Step(At(f, simLoading: true));
            Assert.Equal(HandoverActions.ForceLoadedByProbe | ProceedClient, h.Step(At(N + 10, simLoading: true)));
            Assert.Equal(HandoverActions.Fault, h.Step(At(N + 11, readBack: false)));
        }

        [Test]
        public void HostFaultStillRestoresTheUserSetting()
        {
            LoadHandover h = Armed(false, fallback: 2, maxRetry: 6);
            h.Step(At(N));
            HandoverActions last = HandoverActions.None;
            for (int f = N + 1; f <= N + 20; f++)
            {
                last = h.Step(At(f, readBack: false));
                if ((last & HandoverActions.Fault) != 0) break;
            }
            Assert.Equal(HandoverActions.RestorePausedAfterLoading | HandoverActions.Fault, last);
            Assert.Equal(HandoverState.Failed, h.State);
            Assert.False(h.RestoreOwed);
        }

        [Test]
        public void ANewLoadReleasesSimulationSystemAndTheNextHandoverWorks()
        {
            LoadHandover h = Armed(true);
            h.Step(At(N, simLoading: true));
            h.Step(At(N + 1, simLoading: false));
            h.Step(At(N + 2, readBack: true));
            Assert.Equal(HandoverState.Complete, h.State);
            Assert.True(h.HoldsSimulationDisabled);

            // resync or leaving: OnGamePreload set m_IsLoading again; only SimulationSystem's own update clears it
            Assert.Equal(HandoverActions.EnableSimulation, h.Step(At(N + 500, simLoading: true, loading: true)));
            Assert.Equal(HandoverState.Loading, h.State);
            Assert.False(h.HoldsSimulationDisabled);
            Assert.Equal(HandoverActions.None, h.Step(At(N + 501, simLoading: true, loading: true)));

            h.OnGameLoadingComplete();
            Assert.Equal(-1, h.FramesWaited, "per-hand-over diagnostics are reset");
            Assert.Equal(0, h.Attempts);
            Assert.Equal(HandoverActions.None, h.Step(At(N + 600, simLoading: true)));
            Assert.Equal(ProceedClient, h.Step(At(N + 601, simLoading: false)));
            Assert.Equal(HandoverActions.None, h.Step(At(N + 602, readBack: true)));
            Assert.Equal(HandoverState.Complete, h.State);
        }

        [Test]
        public void ALoadWithoutHandoverReturnsToIdle()
        {
            var h = new LoadHandover(true);
            Assert.Equal(HandoverState.Idle, h.State);
            Assert.Equal(HandoverActions.None, h.Step(At(1)));
            Assert.Equal(HandoverActions.None, h.Step(At(2, loading: true)));
            Assert.Equal(HandoverState.Loading, h.State);
            Assert.Equal(HandoverActions.None, h.Step(At(3, loading: false, worldReady: false)), "neither loading nor ready: keep waiting");
            Assert.Equal(HandoverState.Loading, h.State);
            Assert.Equal(HandoverActions.None, h.Step(At(4)));
            Assert.Equal(HandoverState.Idle, h.State);
            Assert.False(h.RestoreOwed);
        }

        [Test]
        public void ArmingWithoutASeenLoadAlsoWorks()
        {
            var h = new LoadHandover(true);
            h.OnGameLoadingComplete();
            Assert.Equal(HandoverState.AwaitWorldReady, h.State);
            h.OnGameLoadingComplete();
            Assert.Equal(HandoverState.AwaitWorldReady, h.State, "idempotent");
            Assert.Equal(HandoverActions.None, h.Step(At(N, simLoading: true)));
            Assert.Equal(ProceedClient, h.Step(At(N + 1, simLoading: false)));
        }

        [Test]
        public void ArmingWhileHoldingSimulationDisabledReEnablesItFirst()
        {
            LoadHandover h = Armed(true);
            h.Step(At(N, simLoading: true));
            h.Step(At(N + 1, simLoading: false));
            h.Step(At(N + 2, readBack: true));
            Assert.True(h.HoldsSimulationDisabled);

            // a second session load completed without any step seeing it
            h.OnGameLoadingComplete();
            Assert.Equal(HandoverActions.EnableSimulation, h.Step(At(N + 900, simLoading: true)), "the post-load update must run");
            Assert.Equal(HandoverState.AwaitFirstSimulationUpdate, h.State);
            Assert.False(h.HoldsSimulationDisabled);
            Assert.Equal(ProceedClient, h.Step(At(N + 901, simLoading: false)));
        }

        [Test]
        public void ANewLoadDuringTheHandoverAbortsIt()
        {
            LoadHandover h = Armed(true);
            h.Step(At(N, simLoading: true));
            h.Step(At(N + 1, simLoading: false));
            Assert.Equal(HandoverState.Verify, h.State);
            Assert.Equal(HandoverActions.EnableSimulation, h.Step(At(N + 2, simLoading: true, loading: true)));
            Assert.Equal(HandoverState.Loading, h.State);
            Assert.False(h.HoldsSimulationDisabled);

            LoadHandover waiting = Armed(true);
            waiting.Step(At(N, simLoading: true));
            Assert.Equal(HandoverActions.None, waiting.Step(At(N + 1, simLoading: true, loading: true)), "nothing held, nothing to release");
            Assert.Equal(HandoverState.Loading, waiting.State);
        }

        [Test]
        public void ResetReturnsWhatMustBeUndone()
        {
            LoadHandover verifying = Armed(true);
            verifying.Step(At(N, simLoading: true));
            verifying.Step(At(N + 1, simLoading: false));
            Assert.Equal(HandoverActions.EnableSimulation, verifying.Reset(), "restore was already emitted");
            Assert.Equal(HandoverState.Idle, verifying.State);
            Assert.False(verifying.HoldsSimulationDisabled);
            Assert.Equal(HandoverActions.None, verifying.Reset());

            LoadHandover waiting = Armed(true);
            waiting.Step(At(N, simLoading: true));
            Assert.Equal(HandoverActions.RestorePausedAfterLoading, waiting.Reset());

            LoadHandover complete = Armed(false);
            complete.Step(At(N, simLoading: true));
            complete.Step(At(N + 1, simLoading: false));
            Assert.Equal(HandoverActions.None, complete.Reset());

            LoadHandover failed = Armed(true, fallback: 1, maxRetry: 2);
            failed.Step(At(N));
            failed.Step(At(N + 1));                    // disable + sentinel
            failed.Step(At(N + 2, readBack: false));   // fault (2 >= 2)
            Assert.Equal(HandoverState.Failed, failed.State);
            Assert.Equal(HandoverActions.EnableSimulation | HandoverActions.RestorePausedAfterLoading, failed.Reset());
        }

        [Test]
        public void AStalledFrameCounterCannotStallTheFallback()
        {
            LoadHandover h = Armed(true);
            h.Step(At(5, simLoading: true));
            for (int i = 1; i < 10; i++) Assert.Equal(HandoverActions.None, h.Step(At(5, simLoading: true)), "step " + i);
            Assert.Equal(HandoverActions.ForceLoadedByProbe | ProceedClient, h.Step(At(5, simLoading: true)), "10 steps count as 10 frames");
        }

        [Test]
        public void SkippedFramesCountTowardsTheFallback()
        {
            LoadHandover h = Armed(true);
            h.Step(At(N, simLoading: true));
            Assert.Equal(HandoverActions.ForceLoadedByProbe | ProceedClient, h.Step(At(N + 15, simLoading: true)));
        }

        [Test]
        public void ConstructorValidatesTheBudgets()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LoadHandover(true, 0, 60));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LoadHandover(true, -1, 60));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LoadHandover(true, 10, 9));
            Assert.DoesNotThrow(() => new LoadHandover(true, 10, 10));
            Assert.Equal(10, LoadHandover.DefaultReflectionFallbackAfterFrames);
            Assert.Equal(60, LoadHandover.DefaultMaxRetryFrames);
            var defaults = new LoadHandover(false);
            Assert.Equal(HandoverState.Idle, defaults.State);
            Assert.Null(defaults.FailureDetail);
            Assert.Equal(-1, defaults.FramesWaited);
        }

        [Test]
        public void LogsTheCompletionWithTheFramesWaited()
        {
            var log = new ListLog();
            LoadHandover h = Armed(true);
            h.Log = log;
            h.Step(At(N, simLoading: true));
            h.Step(At(N + 1, simLoading: true));
            h.Step(At(N + 2, simLoading: false));
            h.Step(At(N + 3, readBack: true));
            Assert.True(log.Contains("Info [join] load hand-over (client) Verify -> Complete"));
            Assert.True(log.Contains("complete after 3 frame(s), 1 attempt(s)"));
            Assert.True(log.Contains("Debug [join] load hand-over (client) AwaitWorldReady -> AwaitFirstSimulationUpdate: WorldReady at frame 1000"));
            Assert.Contains("Complete after 3 frame(s)", h.ToString());
        }
    }
}
