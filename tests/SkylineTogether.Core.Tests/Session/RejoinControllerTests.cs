using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Follower;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>The recovery decisions after a client session ended, the feature health accounting and the clock's stall handling.</summary>
    public sealed class RejoinControllerTests
    {
        private static RejoinController Make() => new RejoinController(new RejoinOptions { BackoffSeconds = new double[] { 2, 5, 10 }, MaxAttempts = 3 });

        [Test]
        public void AFirstJoinThatFailsBeforeTheCityLoadsIsNoRecoveryCase()
        {
            RejoinController r = Make();
            Assert.False(r.OnClientEnded(ClientEndKind.Unreachable, "no answer", enteredCity: false, now: 10));
            Assert.Equal(RejoinPhase.Idle, r.Phase);
            Assert.False(r.Recovering);
        }

        [Test]
        public void ALostConnectionRetriesWithBackoffThenAsks()
        {
            RejoinController r = Make();
            Assert.True(r.OnClientEnded(ClientEndKind.ConnectionLost, "lost", enteredCity: true, now: 100));
            Assert.Equal(RejoinPhase.Waiting, r.Phase);
            Assert.True(r.CanReconnect);
            Assert.False(r.Tick(101.9));
            Assert.Near(0.1, r.SecondsUntilAttempt(101.9), 1e-9);
            Assert.True(r.Tick(102));
            Assert.Equal(RejoinPhase.Attempting, r.Phase);
            Assert.Equal(1, r.Attempt);
            // attempt 1 cannot reach the host: next one after 5 s
            Assert.True(r.OnClientEnded(ClientEndKind.Unreachable, "no answer", enteredCity: false, now: 103));
            Assert.Equal(RejoinPhase.Waiting, r.Phase);
            Assert.False(r.Tick(107.9));
            Assert.True(r.Tick(108));
            Assert.True(r.OnClientEnded(ClientEndKind.JoinFailed, "stalled", enteredCity: false, now: 110));
            Assert.True(r.Tick(120));
            Assert.Equal(3, r.Attempt);
            // the last attempt fails: ask, but allow a manual reconnect
            Assert.True(r.OnClientEnded(ClientEndKind.ConnectionLost, "lost again", enteredCity: true, now: 121));
            Assert.Equal(RejoinPhase.NeedsChoice, r.Phase);
            Assert.True(r.CanReconnect);
            Assert.Equal("lost again", r.Reason);
            Assert.False(r.Tick(1000), "no automatic attempt while asking");
            r.ReconnectNow(1000);
            Assert.True(r.Tick(1000));
            Assert.Equal(1, r.Attempt, "a manual reconnect starts a new series");
            r.OnLive();
            Assert.Equal(RejoinPhase.Idle, r.Phase);
            Assert.Equal(1, r.Rejoins);
        }

        [Test]
        public void TheHostEndingTheSessionOrAKickAsksAtOnce()
        {
            RejoinController r = Make();
            Assert.True(r.OnClientEnded(ClientEndKind.SessionEnded, "The host left the city.", enteredCity: true, now: 5));
            Assert.Equal(RejoinPhase.NeedsChoice, r.Phase);
            Assert.False(r.CanReconnect);
            r.Reset();
            Assert.True(r.OnClientEnded(ClientEndKind.Kicked, "kicked", enteredCity: true, now: 5));
            Assert.Equal(RejoinPhase.NeedsChoice, r.Phase);
            Assert.False(r.CanReconnect);
            // a rejoin attempt that ends because the host stopped hosting meanwhile: asked, no more retries
            r.ReconnectNow(6);
            Assert.True(r.Tick(6));
            Assert.True(r.OnClientEnded(ClientEndKind.SessionEnded, "ended", enteredCity: false, now: 7));
            Assert.Equal(RejoinPhase.NeedsChoice, r.Phase);
            Assert.False(r.CanReconnect);
        }

        [Test]
        public void ARefusalStopsRetryingButATemporaryOneDoesNot()
        {
            RejoinController r = Make();
            r.OnClientEnded(ClientEndKind.ConnectionLost, "lost", enteredCity: true, now: 0);
            Assert.True(r.Tick(2));
            Assert.True(r.OnClientEnded(ClientEndKind.RefusedTemporarily, "still joining", enteredCity: false, now: 2.5));
            Assert.Equal(RejoinPhase.Waiting, r.Phase);
            Assert.True(r.Tick(10));
            Assert.True(r.OnClientEnded(ClientEndKind.Refused, "Wrong password.", enteredCity: false, now: 11));
            Assert.Equal(RejoinPhase.NeedsChoice, r.Phase);
            Assert.False(r.CanReconnect, "a refusal is not fixed by retrying");
        }

        [Test]
        public void LeavingDuringRecoveryEndsIt()
        {
            RejoinController r = Make();
            r.OnClientEnded(ClientEndKind.ConnectionLost, "lost", enteredCity: true, now: 0);
            Assert.True(r.Tick(2));
            Assert.False(r.OnClientEnded(ClientEndKind.Left, "You left the session.", enteredCity: false, now: 3));
            Assert.Equal(RejoinPhase.Idle, r.Phase);
        }

        [Test]
        public void ADueAttemptWaitsWhileItCannotStart()
        {
            RejoinController r = Make();
            r.OnClientEnded(ClientEndKind.ConnectionLost, "lost", enteredCity: true, now: 0);
            Assert.False(r.Tick(3, canStart: false));
            Assert.Equal(RejoinPhase.Waiting, r.Phase);
            Assert.Equal(0, r.Attempt, "a postponed attempt is not spent");
            Assert.False(r.Tick(3.5));
            Assert.True(r.Tick(4));
        }

        [Test]
        public void EndKindsThatARetryCanFix()
        {
            Assert.True(SessionClient.IsRetryable(ClientEndKind.ConnectionLost));
            Assert.True(SessionClient.IsRetryable(ClientEndKind.Unreachable));
            Assert.True(SessionClient.IsRetryable(ClientEndKind.Timeout));
            Assert.False(SessionClient.IsRetryable(ClientEndKind.PasswordRequired));
            Assert.True(SessionClient.IsRetryable(ClientEndKind.JoinFailed));
            Assert.True(SessionClient.IsRetryable(ClientEndKind.RefusedTemporarily));
            Assert.False(SessionClient.IsRetryable(ClientEndKind.Refused));
            Assert.False(SessionClient.IsRetryable(ClientEndKind.SessionEnded));
            Assert.False(SessionClient.IsRetryable(ClientEndKind.Kicked));
            Assert.False(SessionClient.IsRetryable(ClientEndKind.Left));
            Assert.False(SessionClient.IsRetryable(ClientEndKind.LoadFailed));
            Assert.False(SessionClient.IsRetryable(ClientEndKind.ProtocolError));
        }

        [Test]
        public void AnAbortTellsTheHostAndEndsWithALocalFaultThatOffersAReconnect()
        {
            var rig = new SessionRig();
            var (client, _, _) = rig.AddClient("Anna");
            rig.Run(200);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.True(client.EnteredCity && client.WasLive);
            client.Abort("replication failed");
            Assert.Equal(ClientState.Ended, client.State);
            Assert.Equal(ClientEndKind.LocalFault, client.EndKind);
            rig.Run(10);
            Assert.Equal(0, rig.Host.LivePeerCount, "the host saw the player leave at once");
            RejoinController r = Make();
            Assert.True(r.OnClientEnded(client.EndKind, client.EndReason, client.EnteredCity, 0));
            Assert.Equal(RejoinPhase.NeedsChoice, r.Phase, "a local fault is not retried automatically (it could loop)");
            Assert.True(r.CanReconnect);
        }

        [Test]
        public void EndKindsOfTheJoinPath()
        {
            var rig = new SessionRig(new HostOptions { Password = "secret" });
            var (wrong, _, _) = rig.AddClient("Anna", password: "nope");
            var (none, _, _) = rig.AddClient("Ben");
            rig.Run(100);
            Assert.Equal(ClientEndKind.Refused, wrong.EndKind, wrong.EndReason);
            Assert.Equal(ClientEndKind.PasswordRequired, none.EndKind, none.EndReason);
            Assert.False(wrong.EnteredCity);
            var (right, _, _) = rig.AddClient("Cleo", password: "secret");
            rig.Run(200);
            Assert.Equal(ClientState.Live, right.State, right.EndReason);
            rig.Host.Kick(right.LocalPeer, "bye");
            rig.Run(10);
            Assert.Equal(ClientEndKind.Kicked, right.EndKind);
            var (left, _, _) = rig.AddClient("Dora", password: "secret");
            rig.Run(200);
            left.Leave();
            Assert.Equal(ClientEndKind.Left, left.EndKind);
            Assert.Equal(ClientEndKind.None, new SessionClient(LoopbackTransport.CreatePair().client, new FakeInfo(), new FakeLoader(), new ClientOptions(), rig.Clock, rig.Log).EndKind);
        }

        // ------------------------------------------------------------------ feature health

        [Test]
        public void AFeatureLogsWithARateLimitAndTripsOnRepeatedFailures()
        {
            var f = new FeatureHealth("icons", "icon filter", critical: false);
            Assert.True(f.IsAvailable);
            FailureVerdict v = f.ReportFailure(new System.InvalidOperationException("boom"), 0);
            Assert.True(v.Log);
            Assert.False(v.JustTripped);
            v = f.ReportFailure(null, 1);
            Assert.False(v.Log, "rate limited");
            f.ReportFailure(null, 2);
            f.ReportFailure(null, 3);
            v = f.ReportFailure(null, 4);
            Assert.True(v.JustTripped, "five failures within 30 s trip the feature");
            Assert.True(v.Log);
            Assert.False(f.IsAvailable);
            Assert.Contains("repeated errors", f.UnavailableReason);
            v = f.ReportFailure(null, 5);
            Assert.False(v.JustTripped, "tripped once");
            f.Rearm();
            Assert.True(f.IsAvailable);
            Assert.Equal(6, f.TotalFailures);
        }

        [Test]
        public void SparseFailuresDoNotTrip()
        {
            var f = new FeatureHealth("terrain", "terrain sync", critical: false);
            for (int i = 0; i < 20; i++) Assert.False(f.ReportFailure(null, i * 10.0).JustTripped);
            Assert.True(f.IsAvailable);
            FailureVerdict v = f.ReportFailure(null, 195);
            Assert.False(v.Log, "within 10 s of the last logged failure");
            v = f.ReportFailure(null, 212);
            Assert.True(v.Log);
            Assert.Equal(1, v.Suppressed);
        }

        [Test]
        public void TheRegistryAppliesProbeResultsAndNamesWhatBlocksSessions()
        {
            var reg = new FeatureRegistry();
            FeatureHealth replication = reg.Add("replication", "replication", critical: true);
            FeatureHealth icons = reg.Add("icons", "icon filter", critical: false);
            reg.SetReport("startup", new[]
            {
                new ProbeResult { Id = "P-CTX", Feature = "replication", Target = "ContextFormat.EnableTag", Ok = true },
                new ProbeResult { Id = "P-ICONQUEUES", Feature = "icons", Target = "IconCommandSystem.m_Queues", Ok = false, Detail = "missing" },
                new ProbeResult { Id = "P-GATE", Feature = "replication", Target = "Game.Foo", Ok = false, WarningOnly = true },
            });
            Assert.True(replication.IsAvailable, "a warning does not switch a feature off");
            Assert.False(icons.IsAvailable);
            Assert.Null(reg.BlockingReason());
            Assert.Contains("1/3 checks passed", reg.Summary());
            Assert.Contains("off: icon filter", reg.Summary());
            reg.SetReport("city", new[] { new ProbeResult { Id = "P-CTX", Feature = "replication", Target = "ContextFormat.EnableTag", Ok = false } });
            Assert.False(replication.IsAvailable);
            Assert.True(icons.IsAvailable, "a later report replaces the earlier one");
            Assert.Contains("replication", reg.BlockingReason());
            int lines = 0;
            foreach (string l in reg.ReportLines()) lines++;
            Assert.Equal(1 + 1 + 2, lines);
        }

        // ------------------------------------------------------------------ follower clock during host stalls

        [Test]
        public void TheClockEstimateStopsAtTheExtrapolationCapAndSnapsBackAfterAStall()
        {
            var m = new FollowerClockModel { MaxExtrapolationSeconds = 1.0 };
            m.SetRate(60, 0);
            m.AddSample(1000, 0);
            Assert.Near(1030, m.Estimate(0.5), 1e-9);
            Assert.Near(1060, m.Estimate(1.0), 1e-9);
            Assert.Near(1060, m.Estimate(5.0), 1e-9, "the host stalled (autosave): the estimate holds instead of running ahead");
            // the host resumes where it stopped (its simulation did not run during the stall)
            m.AddSample(1003, 5.0);
            Assert.Near(1003, m.Estimate(5.0), 1e-9, "after a stall a late sample re-anchors at once");
            Assert.Near(1033, m.Estimate(5.5), 1e-9);
            // normal operation: a late sample still only pulls slowly
            m.AddSample(1030, 5.5);
            Assert.InRange(m.Estimate(5.5), 1032, 1033);
        }
    }
}
