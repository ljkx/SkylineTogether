using System;
using System.Collections.Generic;
using SkylineTogether.Core.Scheduling;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Scheduling
{
    /// <summary>
    /// Seeded properties of the rate controllers: the token bucket, the send-rate AIMD, the per-peer link estimator,
    /// the automatic speed cap and the motion degradation ladder. Each is driven with random (but reproducible) inputs
    /// and must keep its bounds, its hysteresis and its timing rules, and converge where it promises to.
    /// </summary>
    public sealed class RateControlPropertyTests
    {
        // ------------------------------------------------------------------ token bucket

        [Test]
        public void TokenBucketNeverLetsAPoliteSpenderExceedRateTimesTimePlusBurst()
        {
            Prop.ForAll("token bucket", 1100, 200, g =>
            {
                int rate = g.Pick(0, 1, 1000, 48_000, 1 << 20, int.MaxValue / 4);
                int burst = g.Pick(0, 1, 512, 16 * 1024, 1 << 20);
                var bucket = new TokenBucket(rate, burst);
                double elapsed = 0, spent = 0;
                for (int i = 0; i < 300; i++)
                {
                    double dt = g.OneIn(20) ? g.Pick(double.NaN, -1.0, double.PositiveInfinity, 0.0) : g.Double() / 30;
                    if (dt > 0 && !double.IsInfinity(dt)) elapsed += dt;
                    int may = bucket.Take(dt);
                    Assert.True(may >= 0 && may <= burst, "Take returns 0 .. burst (" + may + ")");
                    Assert.True(bucket.Balance <= burst, "the balance never exceeds the burst");
                    int spend = g.OneIn(3) ? may : g.Int(may + 1);
                    bucket.Consume(spend);
                    spent += spend;
                    Assert.True(spent <= burst + (double)rate * elapsed + 1e-6, "spent " + spent + " > " + burst + " + " + rate + " × " + elapsed);
                }
            });
        }

        [Test]
        public void TokenBucketKeepsFractionsAndBoundsItsDebt()
        {
            // 48 000 B/s at 144 fps: no byte lost to rounding over ten seconds of greedy spending
            var bucket = new TokenBucket(48_000, 400);
            bucket.Consume(400);
            long spent = 0;
            for (int i = 0; i < 1440; i++)
            {
                int may = bucket.Take(1.0 / 144);
                bucket.Consume(may);
                spent += may;
            }
            Assert.InRange(spent, 480_000 - 1, 480_000);

            var debt = new TokenBucket(1000, 100);
            debt.Consume(10_000);                      // P0 is accounted, never refused
            Assert.Equal(-9_900, debt.Available);
            Assert.Equal(0, debt.Take(1.0), "nothing to spend while in debt");
            debt.LimitDebt(500);
            Assert.Equal(-500, debt.Available, "debt beyond what is really queued is forgiven");
            debt.LimitDebt(-5);
            Assert.Equal(0, debt.Available, "a negative limit means no debt");
            debt.Consume(double.NaN);
            debt.Consume(-3.0);
            debt.Consume(double.PositiveInfinity);
            debt.Consume(0);
            Assert.Equal(0.0, debt.Balance, "non-positive and non-finite amounts are ignored");
            debt.Consume(long.MaxValue / 2.0);
            Assert.Equal(int.MinValue, debt.Available, "huge debts saturate instead of overflowing");
            debt.RateBytesPerSecond = -7;
            debt.BurstBytes = -1;
            Assert.Equal(0, debt.RateBytesPerSecond);
            Assert.Equal(0, debt.BurstBytes);
            Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucket(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucket(0, -1));

            var shrink = new TokenBucket(0, 1000);
            shrink.BurstBytes = 10;
            Assert.Equal(10, shrink.Take(0), "a lower depth caps the balance at the next take");
        }

        // ------------------------------------------------------------------ send rate (AIMD)

        private static ConnectionStats Stats(int ping, float remoteQuality, int pending) =>
            new ConnectionStats { PingMs = ping, RemoteQuality = remoteQuality, PendingReliableBytes = pending };

        [Test]
        public void SendRateStaysInItsBoundsAndHonoursTheHoldAfterABackOff()
        {
            var options = new SendRateOptions();
            Prop.ForAll("send rate bounds", 1200, 100, g =>
            {
                var c = new SendRateController(options);
                double now = 0, lastDecrease = double.NegativeInfinity;
                for (int i = 0; i < 400; i++)
                {
                    now += g.Double() * 0.6;
                    float quality = g.Pick(0f, 0f, 1f, 0.99f, 0.95f, 0.9f, 0.5f);
                    int ping = g.Pick(0, 30, 40, 50, 200, 400);
                    int pending = g.Pick(0, 1000, c.Rate, c.Rate * 2);
                    int before = c.Rate;
                    bool changed = c.Update(now, Stats(ping, quality, pending));
                    Assert.Equal(changed, c.Rate != before, "Update reports exactly the changes");
                    Assert.True(c.Rate >= options.MinBytesPerSecond && c.Rate <= options.MaxBytesPerSecond, "rate " + c.Rate);
                    if (c.Rate > before)
                    {
                        Assert.True(now - lastDecrease >= options.HoldSeconds, "no increase within the hold after a back-off");
                        Assert.True(pending >= before * options.BusySeconds, "increases only while the link is busy");
                        Assert.True(quality == 0f ? c.Rate <= options.BlindMaxBytesPerSecond : quality >= options.GoodQuality, "and clean (or blind up to its cap)");
                    }
                    if (c.Rate < before) lastDecrease = now;
                }
                Assert.True(c.PeakRate >= c.Rate);
            });
        }

        [Test]
        public void SendRateSettlesNearTheCapacityOfASimulatedLink()
        {
            foreach (int capacity in new[] { 300_000, 2_000_000, 12_000_000 })
            {
                var c = new SendRateController();
                double sum = 0;
                int samples = 0;
                for (double now = 0; now < 240; now += 0.1)
                {
                    // a link that delivers `capacity`: faster sending is lost (remote quality falls), the sender always has data
                    float quality = c.Rate <= capacity ? 1f : (float)capacity / c.Rate;
                    c.Update(now, Stats(40, quality, c.Rate));
                    if (now > 120)
                    {
                        sum += c.Rate;
                        samples++;
                    }
                }
                double mean = sum / samples;
                Assert.InRange(mean, capacity * 0.45, capacity * 1.1);
            }
        }

        [Test]
        public void SendRateBacksOffOnQueueingDelayAgainstItsPingBaseline()
        {
            var c = new SendRateController();
            int start = c.Rate;
            c.Update(0, Stats(40, 1f, 0));
            Assert.Equal(40, c.BaselinePingMs);
            Assert.False(c.Update(1, Stats(90, 1f, 0)), "90 ms is within baseline + 60 ms");
            Assert.True(c.Update(2, Stats(150, 1f, 0)), "150 ms is queueing delay");
            Assert.True(c.Rate < start);
            Assert.Contains("queueing delay", c.LastChange);
            // the baseline follows a route change after its window
            for (double t = 3; t < 70; t += 0.5) c.Update(t, Stats(200, 0f, 0));
            Assert.Equal(200, c.BaselinePingMs, "a lasting higher round trip becomes the new baseline");
        }

        // ------------------------------------------------------------------ link estimator

        [Test]
        public void LinkBudgetStaysBetweenFloorAndCeilingAndDecreasesOncePerHold()
        {
            var config = new SchedulerConfig();
            Prop.ForAll("link estimator", 1300, 100, g =>
            {
                var link = new LinkEstimator(config);
                int lastDecreases = 0;
                double sinceDecrease = double.PositiveInfinity;
                for (int i = 0; i < 300; i++)
                {
                    double dt = g.Pick(1 / 60.0, 1 / 30.0, 0.1, 0.0, -1.0, double.NaN, 2.0);
                    var stats = new ConnectionStats
                    {
                        SendRateBytesPerSecond = g.Pick(0, -5, 100_000, 3_000_000, int.MaxValue),
                        QueueTimeMicroseconds = g.Pick(0L, 10_000L, 100_000L, 400_000L),
                        PingMs = g.Pick(0, 50, 900),
                    };
                    int cap = g.Pick(0, -1, 10_000, 64_000, 1_000_000);
                    bool backlogged = g.Bool();
                    int before = link.CurrentBudget;
                    int budget = link.Update(stats, cap, backlogged, dt);
                    double step = dt > 0 && !double.IsInfinity(dt) ? Math.Min(dt, 1.0) : 0;
                    sinceDecrease += step;
                    Assert.Equal(budget, link.CurrentBudget);
                    Assert.True(link.Floor <= link.Ceiling, "floor " + link.Floor + " <= ceiling " + link.Ceiling);
                    Assert.True(budget >= link.Floor && budget <= link.Ceiling, "budget " + budget + " in [" + link.Floor + ", " + link.Ceiling + "]");
                    if (cap > 0) Assert.True(budget <= Math.Max(cap, Math.Min(config.FloorBytesPerSecond, cap)), "never above the user's cap");
                    if (link.Decreases > lastDecreases)
                    {
                        Assert.True(sinceDecrease >= config.DecreaseHoldSeconds - 1e-9 || lastDecreases == 0, "one decrease per hold (" + sinceDecrease + " s)");
                        sinceDecrease = 0;
                        lastDecreases = link.Decreases;
                    }
                    if (i > 0 && budget > before && budget > link.Floor)
                    {
                        // (a budget lifted to a higher floor is not growth)
                        Assert.True(backlogged && step > 0 && stats.QueueTimeMicroseconds / 1000.0 < config.QueueLowMs, "growth only while backlogged on a short queue");
                    }
                }
                link.Reset();
                Assert.Equal(0, link.CurrentBudget);
                Assert.Equal(0, link.Decreases);
            });
        }

        // ------------------------------------------------------------------ automatic speed cap

        [Test]
        public void SpeedCapNeverRaisesSpeedNorDropsBelowTheMinimumNorPauses()
        {
            Prop.ForAll("speed cap", 1400, 150, g =>
            {
                var cap = new AutoSpeedCap(overloadSeconds: g.Pick(0.0, 5.0, 30.0), recoverySeconds: g.Pick(0.0, 10.0, 60.0)) { MinimumSpeed = g.Pick(0, 1, 2) };
                int minimum = Math.Max(1, cap.MinimumSpeed);
                double now = g.Double() * 100;
                bool anyOverloadedEver = false;
                for (int i = 0; i < 400; i++)
                {
                    now += g.OneIn(50) ? -g.Double() * 10 : g.Double() * 3;      // clocks may jump back
                    int requested = g.Pick(0, 1, 2, 3, 3, 3, 5);
                    var flags = g.List(4, x => x.OneIn(3));
                    if (flags.Contains(true)) anyOverloadedEver = true;
                    int capBefore = cap.CapSpeed;
                    int effective = cap.Evaluate(requested, flags, g.OneIn(100) ? double.NaN : now, out string note);
                    if (requested <= 0) Assert.Equal(requested, effective, "paused passes through");
                    else
                    {
                        Assert.True(effective <= requested, "never faster than requested");
                        Assert.True(effective >= Math.Min(requested, minimum), "never below the minimum (" + effective + ")");
                    }
                    if (flags.Contains(true) && capBefore > 0 && requested > 0) Assert.True(cap.CapSpeed > 0 && cap.CapSpeed <= capBefore, "no step up while a peer is overloaded (" + capBefore + " -> " + cap.CapSpeed + ")");
                    if (!anyOverloadedEver) Assert.False(cap.IsCapping, "no cap without an overloaded peer");
                    Assert.Equal(cap.OverloadedIndices.Count, CountTrue(flags));
                    if (cap.IsCapping || cap.OverloadedIndices.Count > 0) Assert.NotNull(note);
                }
            });
        }

        private static int CountTrue(List<bool> flags)
        {
            int n = 0;
            foreach (bool f in flags) if (f) n++;
            return n;
        }

        [Test]
        public void SpeedCapStepsDownOncePerOverloadPeriodAndRecoversStepwise()
        {
            var cap = new AutoSpeedCap(overloadSeconds: 30, recoverySeconds: 60);
            var overloaded = new[] { false, true };
            var calm = new[] { false, false };
            var speeds = new List<int>();
            for (double t = 0; t <= 100; t += 1) speeds.Add(cap.Evaluate(5, overloaded, t, out _));
            Assert.Equal(4, speeds[0], "the first step happens at once");
            Assert.Equal(4, speeds[29]);
            Assert.Equal(3, speeds[30], "the next after 30 s");
            Assert.Equal(2, speeds[60]);
            Assert.Equal(1, speeds[90]);
            Assert.Equal(1, speeds[100], "never below the minimum");
            Assert.SequenceEqual(new[] { 1 }, cap.OverloadedIndices);

            int last = 1;
            int raises = 0;
            for (double t = 101; t < 101 + 60 * 5; t += 1)
            {
                int s = cap.Evaluate(5, calm, t, out _);
                Assert.True(s >= last && s <= last + 1, "recovery rises one step at a time");
                if (s > last) raises++;
                last = s;
            }
            Assert.Equal(5, last, "60 s of headroom per step lift the cap completely");
            Assert.False(cap.IsCapping);
            Assert.Equal(4, raises);

            cap.Evaluate(5, overloaded, 1000, out _);
            Assert.True(cap.IsCapping);
            Assert.Equal(0, cap.Evaluate(0, overloaded, 1001, out _), "paused stays paused");
            cap.Reset();
            Assert.False(cap.IsCapping);
            Assert.Equal(5, cap.Evaluate(5, calm, 1002, out string note));
            Assert.Null(note);
        }

        // ------------------------------------------------------------------ degradation ladder

        [Test]
        public void DegradationLadderKeepsItsBoundsAndHysteresis()
        {
            var config = new SchedulerConfig();
            float minScale = Math.Max(0.01f, Math.Min(1f, config.MinMotionRadius / config.MaxMotionRadius));
            Prop.ForAll("degradation", 1500, 60, g =>
            {
                var clock = new ManualClock(10);
                var d = new DegradationController(config, clock);
                var peer = new PeerId((byte)g.Int(1, 255));
                int lastStep = 0;
                double lastStepChange = double.NegativeInfinity;
                for (int i = 0; i < 600; i++)
                {
                    clock.Advance(g.Pick(1 / 60.0, 0.1, 0.5));
                    int budget = g.Pick(0, 10_000, 100_000);
                    double ratio = g.Pick(0.0, 0.3, 0.8, 1.0, 1.5, 3.0);
                    int demand = (int)(budget * ratio) + (budget == 0 && ratio > 0 ? 1000 : 0);
                    MotionQuality q = d.Evaluate(peer, budget, demand, out float scale);
                    int step = (int)d.StepOf(peer);
                    Assert.True(step >= 0 && step <= (int)DegradationStep.CoalesceCellMaps);
                    Assert.Equal(DegradationController.QualityOf((DegradationStep)step), q);
                    Assert.True(scale >= minScale - 1e-6f && scale <= 1f, "radius scale " + scale);
                    if (step < (int)DegradationStep.ShrinkRadius) Assert.Equal(1f, scale, "the radius only shrinks from the shrink step on");
                    Assert.True(Math.Abs(step - lastStep) <= 1, "one step at a time");
                    if (step != lastStep)
                    {
                        double gap = clock.Seconds - lastStepChange;
                        Assert.True(gap >= Math.Min(config.DegradeAfterSeconds, config.RecoverAfterSeconds) - 1e-9, "steps are spaced (" + gap + " s)");
                        bool over = demand > 0 && demand > budget * config.OverBudgetRatio;
                        bool under = demand <= budget * config.RecoverBudgetRatio;
                        Assert.True(step > lastStep ? over : under, "a step follows its own condition");
                        lastStepChange = clock.Seconds;
                        lastStep = step;
                    }
                }
                d.RemovePeer(peer);
                Assert.Equal(DegradationStep.None, d.StepOf(peer));
                Assert.Equal(1f, d.RadiusScaleOf(peer));
            });
        }

        [Test]
        public void DegradationReachesTheBottomUnderOverloadAndClimbsBackWhenCalm()
        {
            var clock = new ManualClock(0);
            var d = new DegradationController(new SchedulerConfig(), clock);
            var peer = new PeerId(3);
            for (int i = 0; i < 60 * 120; i++)
            {
                clock.Advance(1 / 60.0);
                d.Evaluate(peer, 10_000, 50_000, out _);
            }
            Assert.Equal(DegradationStep.CoalesceCellMaps, d.StepOf(peer));
            Assert.Equal(MotionQuality.Reduced, DegradationController.QualityOf(d.StepOf(peer)));
            // in the dead band nothing moves
            for (int i = 0; i < 60 * 60; i++)
            {
                clock.Advance(1 / 60.0);
                d.Evaluate(peer, 10_000, 9_000, out _);
            }
            Assert.Equal(DegradationStep.CoalesceCellMaps, d.StepOf(peer), "between the thresholds the ladder holds");
            for (int i = 0; i < 60 * 600; i++)
            {
                clock.Advance(1 / 60.0);
                d.Evaluate(peer, 10_000, 1_000, out _);
            }
            Assert.Equal(DegradationStep.None, d.StepOf(peer));
            Assert.Equal(1f, d.RadiusScaleOf(peer));
        }
    }
}
