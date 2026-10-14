using System;
using SkylineTogether.Core.Scheduling;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Scheduling
{
    /// <summary>
    /// Seeded properties of the rate controllers: the token bucket and the send-rate AIMD. Each is driven with random
    /// (but reproducible) inputs and must keep its bounds, its hysteresis and its timing rules, and converge where it
    /// promises to.
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
    }
}
