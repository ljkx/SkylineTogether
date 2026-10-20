using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// The host's load watchdog with realistic load traces (R2 N1): the game's own progress value stands still through a
    /// saved game's whole deserialization (it reads 1 before the load starts, 0 throughout, and jumps to 1 at the first
    /// simulation step), so only the load heartbeat (frames the joiner's game ran) shows that an honest slow load works.
    /// Also the cooldown by hold time (N4) and the rejoin that honours the host's wait (N7).
    /// </summary>
    public sealed class LoadWatchdogTests
    {
        /// <summary>Ticks host and client for <paramref name="seconds"/>, the client reporting <paramref name="progress"/>.</summary>
        private static void Load(SessionRig rig, SessionClient client, double seconds, float progress, bool clientRuns = true)
        {
            for (double t = 0; t < seconds; t += 0.5)
            {
                rig.Clock.Advance(0.5);
                rig.Host.Tick();
                if (!clientRuns) continue;
                if (client.State == ClientState.Loading) client.ReportLoadProgress(progress);
                client.Tick();
            }
        }

        private static (SessionRig Rig, SessionClient Client, FakeLoader Loader) Loading(HostOptions options = null)
        {
            var rig = new SessionRig(options ?? new HostOptions { HostName = "Hosty" });
            var (client, loader, _) = rig.AddClient("Slow", tweakLoader: l => l.Defer = true);
            rig.Run(300);
            Assert.Equal(ClientState.Loading, client.State, client.EndReason);
            return (rig, client, loader);
        }

        [Test]
        public void ASavedGameLoadWhoseProgressStaysAtZeroForTenMinutesIsNotDropped()
        {
            var (rig, client, loader) = Loading();
            Load(rig, client, 5, 1f);    // before the game's load starts, its progress value reads 1
            Load(rig, client, 660, 0f);  // deserialization: 0 throughout
            Assert.Equal(ClientState.Loading, client.State, client.EndReason);
            Assert.True(rig.Host.HoldSimulation, "still joining");
            Assert.DoesNotContain("made no progress", string.Join("\n", rig.Log.Lines));
            Load(rig, client, 2, 1f);    // the first simulation step
            loader.Finish();
            rig.Run(30);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
        }

        [Test]
        public void ALoadWithLongStillStagesAndAFrozenFrameIsNotDropped()
        {
            var (rig, client, loader) = Loading();
            Load(rig, client, 240, 0f);
            // the game's main thread is busy for 150 s (no frame, nothing sent): within the silence limit
            Load(rig, client, 150, 0f, clientRuns: false);
            Load(rig, client, 200, 0.3f);
            Load(rig, client, 280, 0.3f);
            Assert.Equal(ClientState.Loading, client.State, client.EndReason);
            loader.Finish();
            rig.Run(30);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
        }

        [Test]
        public void AnHonestLoadEndsAtTheLoadLimitWithoutAnAutomaticRetry()
        {
            var (rig, client, _) = Loading(new HostOptions { HostName = "Hosty", MaxLoadSeconds = 600 });
            Load(rig, client, 700, 0f);
            Assert.Equal(ClientState.Ended, client.State);
            Assert.Contains("Loading the city took more than 10 minutes", client.EndReason);
            Assert.Equal(ClientEndKind.LoadFailed, client.EndKind);
            Assert.False(SessionClient.IsRetryable(client.EndKind), "the next attempt would load just as long");
        }

        [Test]
        public void TheJoinersOwnLoadTimeoutIsNotRetriedAutomatically()
        {
            var (rig, client, _) = Loading(new HostOptions { HostName = "Hosty", MaxLoadSeconds = 0, MaxJoinSeconds = 0 });
            Load(rig, client, 1210, 0f);
            Assert.Equal(ClientState.Ended, client.State);
            Assert.Contains("didn't finish in 20 minutes", client.EndReason);
            Assert.Equal(ClientEndKind.LoadFailed, client.EndKind);
        }

        [Test]
        public void TheLoadHeartbeatTravelsInTheProgressReport()
        {
            var w = new ByteWriter();
            SessionPayloads.WriteLoadProgress(w, JoinPhase.Loading, 0.5f, 0x01020304);
            // f32 0.5 | u8 phase | u32 heartbeat, little-endian
            Assert.SequenceEqual(new byte[] { 0x00, 0x00, 0x00, 0x3F, (byte)JoinPhase.Loading, 0x04, 0x03, 0x02, 0x01 }, w.ToArray());
            byte[] bytes = w.ToArray();
            var r = new ByteReader(bytes, 0, bytes.Length);
            Assert.Equal(JoinPhase.Loading, SessionPayloads.ReadLoadProgress(r, out float f, out uint beat));
            Assert.Near(0.5, f, 1e-9);
            Assert.Equal(0x01020304u, beat);
            Assert.Equal(0, r.Remaining);
        }

        // ------------------------------------------------------------------------------------------ hold time (N4)

        private static SessionClient FailAfter(SessionRig rig, ulong id, double seconds)
        {
            var (client, _, _) = rig.AddClient("Joiner", steamId: id, tweakLoader: l => l.Defer = true);
            rig.Run(300);
            if (client.State != ClientState.Loading) return client;
            Load(rig, client, seconds, 0f);
            client.Leave();
            rig.Run(10);
            return client;
        }

        [Test]
        public void OneLongBrokenJoinCostsAnHonestPlayerNothing()
        {
            var rig = new SessionRig();
            FailAfter(rig, 7701, 900);
            var (again, _, _) = rig.AddClient("Joiner", steamId: 7701);
            rig.Run(300);
            Assert.Equal(ClientState.Live, again.State, again.EndReason);
        }

        [Test]
        public void FailedJoinsThatHeldTheGameLongMakeThePlayerWaitAsLong()
        {
            var rig = new SessionRig();
            FailAfter(rig, 7702, 1000);
            rig.RunHostOnly(31);
            SessionClient second = FailAfter(rig, 7702, 1000);
            Assert.Equal(ClientEndKind.Left, second.EndKind, "the second attempt was admitted and loaded");
            var (third, _, _) = rig.AddClient("Joiner", steamId: 7702);
            rig.Run(60);
            Assert.Equal(ClientEndKind.RefusedTemporarily, third.EndKind, third.EndReason);
            // about 2 × (1000 s + the join) held, minus the 1800 s allowance
            Assert.InRange(third.EndReport.RetryAfterSeconds, 150, 400, "the wait grows with the time held");
        }

        [Test]
        public void AWatchdogsDropIsNeverTheFreeFirstFailure()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", MaxLoadSeconds = 300 });
            var (client, _, _) = rig.AddClient("Joiner", steamId: 7703, tweakLoader: l => l.Defer = true);
            rig.Run(300);
            Load(rig, client, 320, 0f);
            Assert.Equal(ClientEndKind.LoadFailed, client.EndKind, client.EndReason);
            var (again, _, _) = rig.AddClient("Joiner", steamId: 7703);
            rig.Run(30);
            Assert.Equal(ClientEndKind.RefusedTemporarily, again.EndKind, again.EndReason);
        }

        // ------------------------------------------------------------------------------- rejoin and waits (N7)

        [Test]
        public void AnAutomaticRejoinWaitsForTheHostsCooldownWithoutSpendingAttempts()
        {
            var r = new RejoinController(new RejoinOptions { BackoffSeconds = new double[] { 2, 5 }, MaxAttempts = 2 });
            Assert.True(r.OnClientEnded(ClientEndKind.ConnectionLost, "lost", enteredCity: true, now: 0));
            Assert.True(r.Tick(2));
            Assert.Equal(1, r.Attempt);
            Assert.True(r.OnClientEnded(ClientEndKind.RefusedTemporarily, "wait", enteredCity: false, now: 3, retryAfterSeconds: 120));
            Assert.Equal(RejoinPhase.Waiting, r.Phase);
            Assert.False(r.Tick(100), "not before the host's wait is over");
            Assert.True(r.Tick(123));
            Assert.Equal(1, r.Attempt, "the refused attempt was not counted");
            Assert.True(r.OnClientEnded(ClientEndKind.ConnectionLost, "lost", enteredCity: true, now: 130));
            Assert.True(r.Tick(135));
            Assert.Equal(2, r.Attempt);
        }

        [Test]
        public void AHostWaitOfHoursAsksThePlayerInstead()
        {
            var r = new RejoinController();
            Assert.True(r.OnClientEnded(ClientEndKind.ConnectionLost, "lost", enteredCity: true, now: 0));
            Assert.True(r.Tick(2));
            Assert.True(r.OnClientEnded(ClientEndKind.RefusedTemporarily, "wait", enteredCity: false, now: 3, retryAfterSeconds: 7200));
            Assert.Equal(RejoinPhase.NeedsChoice, r.Phase);
            Assert.True(r.CanReconnect);
        }
    }
}
