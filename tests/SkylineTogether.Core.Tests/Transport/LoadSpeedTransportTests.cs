using System;
using System.Globalization;
using System.Diagnostics;
using System.Threading;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Tests.Session;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Transport
{
    /// <summary>Send rate control (pacing), the background pump, and snapshot throughput over NetSim.</summary>
    public sealed class LoadSpeedTransportTests
    {
        private static ConnectionStats Busy(int rate, float quality = 1f, int ping = 40) => new ConnectionStats
        {
            PingMs = ping,
            RemoteQuality = quality,
            SendRateBytesPerSecond = rate,
            PendingReliableBytes = rate, // a second of data waits: the rate limits the link
        };

        // ------------------------------------------------------------------ SendRateController

        [Test]
        public void TheRateRampsUpOnlyWhileTheLinkIsBusyAndClean()
        {
            var c = new SendRateController();
            Assert.Equal(1 << 20, c.Rate, "starts at 1 MiB/s, not Steam's fixed 128-256 KiB/s");
            double t = 0;
            for (int i = 0; i < 40; i++, t += 0.5) c.Update(t, new ConnectionStats { PingMs = 40, RemoteQuality = 1f });
            Assert.Equal(1 << 20, c.Rate, "an idle link never ramps up");
            int steps = 0;
            while (c.Rate < (48 << 20) && steps < 50)
            {
                c.Update(t, Busy(c.Rate));
                t += 0.5;
                steps++;
            }
            Assert.Equal(48 << 20, c.Rate, "reaches the cap");
            Assert.True(steps <= 12, "within a few seconds: " + steps + " decisions");
            Assert.Equal(48 << 20, c.PeakRate);
            c.Update(t, Busy(c.Rate));
            Assert.Equal(48 << 20, c.Rate, "never above the cap");
        }

        [Test]
        public void WithoutARemoteQualityTheRateStopsAtTheBlindCap()
        {
            var c = new SendRateController();
            double t = 0;
            for (int i = 0; i < 40; i++, t += 0.5) c.Update(t, Busy(c.Rate, quality: 0f));
            Assert.Equal(8 << 20, c.Rate);
        }

        [Test]
        public void LossBacksOffAndHoldsTheRate()
        {
            var c = new SendRateController();
            double t = 0;
            for (int i = 0; i < 6; i++, t += 0.5) c.Update(t, Busy(c.Rate));
            int before = c.Rate;
            Assert.True(c.Update(t, Busy(c.Rate, quality: 0.85f)), "loss changes the rate");
            Assert.Equal((int)(before * 0.7), c.Rate);
            Assert.Contains("loss", c.LastChange);
            int backedOff = c.Rate;
            t += 0.5;
            c.Update(t, Busy(c.Rate));
            t += 0.5;
            c.Update(t, Busy(c.Rate));
            Assert.Equal(backedOff, c.Rate, "no increase within the hold time");
            t += 2.0;
            c.Update(t, Busy(c.Rate));
            Assert.True(c.Rate > backedOff, "increases again once the hold is over");
            Assert.Equal(1, c.Decreases);
        }

        [Test]
        public void QueueingDelayBacksOffAndTheFloorHolds()
        {
            var c = new SendRateController();
            double t = 0;
            for (int i = 0; i < 4; i++, t += 0.5) c.Update(t, Busy(c.Rate, ping: 30));
            Assert.Equal(30, c.BaselinePingMs);
            int before = c.Rate;
            c.Update(t, Busy(c.Rate, ping: 400));
            Assert.True(c.Rate < before, "a full uplink queue (ping 400 ms over a 30 ms baseline) backs off");
            Assert.Contains("delay", c.LastChange);
            for (int i = 0; i < 40; i++)
            {
                t += 0.5;
                c.Update(t, Busy(c.Rate, ping: 400, quality: 0.5f));
            }
            Assert.Equal(256 << 10, c.Rate, "never below the floor");
            Assert.True(c.Update(t + 3, Busy(c.Rate, ping: 31)) || c.Rate == 256 << 10);
        }

        /// <summary>
        /// A bottleneck link (capacity, a router buffer of 0.25 s, 40 ms round trip): sending above the capacity queues
        /// (the ping rises) and then drops (the remote quality falls). The controller must find the capacity, stay near
        /// it, and follow it down when it shrinks.
        /// </summary>
        [Test]
        public void TheRateConvergesToALinksCapacityAndFollowsItDown()
        {
            var c = new SendRateController();
            double capacity = 6 << 20;
            double queue = 0, delivered = 0, offeredWindow = 0, lostWindow = 0, quality = 1;
            const double dt = 0.05;
            double measuredFrom = 15, measured = 0;
            for (double t = 0; t < 60; t += dt)
            {
                if (t >= 40 && capacity > (2 << 20)) capacity = 2 << 20; // the link gets slower
                double buffer = capacity * 0.25;
                double offered = c.Rate * dt;
                queue += offered;
                double sent = Math.Min(queue, capacity * dt);
                queue -= sent;
                double lost = Math.Max(0, queue - buffer);
                queue -= lost;
                delivered += sent;
                if (t >= measuredFrom && t < 40) measured += sent;
                offeredWindow += offered;
                lostWindow += lost;
                if (offeredWindow > c.Rate * 0.5)
                {
                    quality = 1 - lostWindow / offeredWindow;
                    offeredWindow = lostWindow = 0;
                }
                int ping = (int)(40 + queue / capacity * 1000);
                c.Update(t, new ConnectionStats { PingMs = ping, RemoteQuality = (float)quality, PendingReliableBytes = 4 << 20 });
                if (t > 50 && t < 50 + dt) Assert.True(c.Rate < 4 << 20, "followed the link down: " + c);
            }
            double rate = measured / (40 - measuredFrom);
            Assert.True(rate > 0.65 * (6 << 20), "uses most of the 6 MiB/s link: " + (rate / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture) + " MiB/s; " + c);
            Assert.True(c.Decreases > 0, "it probed above the capacity and backed off");
        }

        // ------------------------------------------------------------------ BackgroundPump

        [Test(TimeoutSeconds = 30)]
        public void TheBackgroundPumpCallsUntilTheRegistrationIsDisposed()
        {
            using (var pump = new BackgroundPump(null, "test pump", 1))
            {
                int calls = 0, failing = 0;
                IDisposable reg = pump.Add(() => Interlocked.Increment(ref calls));
                IDisposable bad = pump.Add(() =>
                {
                    Interlocked.Increment(ref failing);
                    throw new InvalidOperationException("boom");
                });
                var watch = Stopwatch.StartNew();
                while (Volatile.Read(ref calls) < 20 && watch.ElapsedMilliseconds < 5000) Thread.Sleep(5);
                Assert.True(Volatile.Read(ref calls) >= 20, "called repeatedly: " + calls);
                Assert.True(pump.Errors > 0 && Volatile.Read(ref calls) >= 20, "a throwing action does not stop the others");
                reg.Dispose();
                int after = Volatile.Read(ref calls);
                Thread.Sleep(50);
                Assert.Equal(after, Volatile.Read(ref calls), "never called after its registration was disposed");
                bad.Dispose();
                Assert.Equal(0, pump.Count);
                // the round in progress when the last registration went may still count itself (the counter is
                // incremented after the round lock is released): wait until the count settles, then it must stay
                long rounds = pump.Rounds;
                var settle = Stopwatch.StartNew();
                while (settle.ElapsedMilliseconds < 5000)
                {
                    Thread.Sleep(20);
                    long now = pump.Rounds;
                    if (now == rounds) break;
                    rounds = now;
                }
                Thread.Sleep(30);
                Assert.Equal(rounds, pump.Rounds, "the thread sleeps while nothing is registered");
            }
        }

        // ------------------------------------------------------------------ snapshot throughput over NetSim

        private static double DownloadRate(int bandwidth, double loss, double frameSeconds, int snapshotBytes, int seed)
        {
            var rig = new SoakRig(new NetSimOptions { LatencyMs = 40, JitterMs = 10, LossRate = loss, BandwidthBytesPerSecond = bandwidth, Seed = seed }, snapshotBytes: snapshotBytes)
            {
                ClientOptions = new NetSimOptions { LatencyMs = 40, JitterMs = 10, Seed = seed + 1 },
                BroadcastBytesPerFrame = 400,
                FrameSeconds = frameSeconds,
            };
            SoakRig.Player p = rig.AddPlayer("Anna", proposals: false, presence: false);
            Assert.True(rig.RunUntil(() => p.IsLive, 120), rig.Describe());
            Assert.SequenceEqual(rig.Snapshots.Data, p.Loader.Target.ToArray(), "the city arrived byte for byte");
            JoinTimeline t = p.Client.Timeline;
            Assert.Equal((long)snapshotBytes, t.SnapshotBytes);
            rig.AssertNoErrors();
            return t.TransferBytesPerSecond;
        }

        [Test(TimeoutSeconds = 120)]
        public void ASnapshotDownloadUsesTheLinksBandwidth()
        {
            const int bandwidth = 8 << 20;
            double rate = DownloadRate(bandwidth, 0, SoakRig.Frame, 24_000_000, 201);
            Assert.True(rate > 0.85 * bandwidth, "8 MiB/s link: " + (rate / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture) + " MiB/s");
        }

        [Test(TimeoutSeconds = 120)]
        public void ASnapshotDownloadKeepsItsRateAtALowFrameRateAndWithLoss()
        {
            const int bandwidth = 8 << 20;
            // the host renders at 12 fps (a held, big city): the one-second bulk window bridges the frames
            double slow = DownloadRate(bandwidth, 0, 1 / 12.0, 24_000_000, 211);
            Assert.True(slow > 0.8 * bandwidth, "at 12 fps: " + (slow / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture) + " MiB/s");
            double lossy = DownloadRate(bandwidth, 0.01, SoakRig.Frame, 16_000_000, 221);
            Assert.True(lossy > 0.65 * bandwidth, "with 1 % loss: " + (lossy / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture) + " MiB/s");
        }
    }
}
