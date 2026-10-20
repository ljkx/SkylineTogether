using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// The bounds of the join cooldown and of the automatic rejoin: a host that keeps answering "join again in N s" does
    /// not hold a client's city for ever, the hold-time cooldown has its cap, and a long cooldown does not reset the
    /// escalation of the failures behind it.
    /// </summary>
    public sealed class JoinRetryBoundsTests
    {
        // ------------------------------------------------------------------------------- rejoin and refused waits

        [Test]
        public void RefusedWaitsAreHonouredOnlySoOftenThenAttemptsCountAndThePlayerIsAsked()
        {
            var options = new RejoinOptions { BackoffSeconds = new double[] { 2 }, MaxAttempts = 3, MaxRefusedWaits = 4 };
            var r = new RejoinController(options);
            double now = 0;
            Assert.True(r.OnClientEnded(ClientEndKind.ConnectionLost, "lost", enteredCity: true, now: now));
            int refusals = 0;
            while (r.Phase == RejoinPhase.Waiting && refusals < 100)
            {
                now = r.NextAttemptAt;
                Assert.True(r.Tick(now));
                refusals++;
                r.OnClientEnded(ClientEndKind.RefusedTemporarily, "still joining", enteredCity: false, now: now + 1, retryAfterSeconds: 30);
                if (refusals <= options.MaxRefusedWaits)
                {
                    Assert.Equal(RejoinPhase.Waiting, r.Phase, "refusal " + refusals);
                    Assert.Equal(0, r.Attempt, "refusal " + refusals + " waited for the host without spending an attempt");
                }
                else if (r.Phase == RejoinPhase.Waiting)
                {
                    Assert.Equal(refusals - options.MaxRefusedWaits, r.Attempt, "refusal " + refusals + " spends an attempt");
                }
            }
            Assert.Equal(RejoinPhase.NeedsChoice, r.Phase, "the player is asked instead of waiting for ever");
            Assert.Equal(options.MaxRefusedWaits + options.MaxAttempts, refusals);
            Assert.True(r.CanReconnect, "a manual reconnect stays possible");
            r.ReconnectNow(now + 10);
            Assert.True(r.Tick(now + 10));
            Assert.True(r.OnClientEnded(ClientEndKind.RefusedTemporarily, "still joining", enteredCity: false, now: now + 11, retryAfterSeconds: 30));
            Assert.Equal(0, r.Attempt, "a manual reconnect honours the host's waits again");
        }

        [Test]
        public void TheDefaultRejoinAsksAfterSixRefusedWaitsAndSixAttempts()
        {
            var r = new RejoinController();
            double now = 0;
            r.OnClientEnded(ClientEndKind.ConnectionLost, "lost", enteredCity: true, now: now);
            int refusals = 0;
            while (r.Phase == RejoinPhase.Waiting && refusals < 1000)
            {
                now = r.NextAttemptAt;
                r.Tick(now);
                refusals++;
                r.OnClientEnded(ClientEndKind.RefusedTemporarily, "wait", enteredCity: false, now: now + 1, retryAfterSeconds: 60);
            }
            Assert.Equal(RejoinPhase.NeedsChoice, r.Phase);
            Assert.Equal(12, refusals);
        }

        // ------------------------------------------------------------------------------- the host's cooldown

        private static void Load(SessionRig rig, SessionClient client, double seconds)
        {
            for (double t = 0; t < seconds; t += 0.5)
            {
                rig.Clock.Advance(0.5);
                rig.Host.Tick();
                if (client.State == ClientState.Loading) client.ReportLoadProgress(0f);
                client.Tick();
            }
        }

        /// <summary>A join of <paramref name="id"/> that loads for <paramref name="seconds"/>, then leaves (a failed join).</summary>
        private static SessionClient FailAfter(SessionRig rig, ulong id, double seconds)
        {
            var (client, _, _) = rig.AddClient("Joiner", steamId: id, tweakLoader: l => l.Defer = true);
            rig.Run(300);
            Assert.Equal(ClientState.Loading, client.State, client.EndReason);
            Load(rig, client, seconds);
            client.Leave();
            rig.Run(10);
            return client;
        }

        private static SessionClient TryJoin(SessionRig rig, ulong id)
        {
            var (client, _, _) = rig.AddClient("Joiner", steamId: id);
            rig.Run(120);
            return client;
        }

        [Test]
        public void TheWaitForHoldingTheGameIsCapped()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", FailedJoinHoldAllowanceSeconds = 0, MaxJoinCooldownSeconds = 60 });
            FailAfter(rig, 7801, 400);
            SessionClient next = TryJoin(rig, 7801);
            Assert.Equal(ClientEndKind.RefusedTemporarily, next.EndKind, next.EndReason);
            // the join held the game for about 405 s beyond an allowance of 0: the wait is the cap, not the excess
            Assert.InRange(next.EndReport.RetryAfterSeconds, 50, 60, "capped at MaxJoinCooldownSeconds");
        }

        [Test]
        public void ACooldownLongerThanTheForgetWindowKeepsTheEscalation()
        {
            var rig = new SessionRig(new HostOptions
            {
                HostName = "Hosty", JoinCooldownSeconds = new double[] { 0, 300, 900 }, JoinFailureForgetSeconds = 100,
            });
            FailAfter(rig, 7802, 5);           // the first failure is free
            FailAfter(rig, 7802, 5);           // the second: 300 s
            rig.RunHostOnly(340);              // the wait is over (40 s ago), but 345 s have passed since the failure
            FailAfter(rig, 7802, 5);           // admitted; it fails again: the third in a row
            SessionClient next = TryJoin(rig, 7802);
            Assert.Equal(ClientEndKind.RefusedTemporarily, next.EndKind, next.EndReason);
            Assert.InRange(next.EndReport.RetryAfterSeconds, 800, 900, "the third step, not a fresh first failure");
        }
    }
}
