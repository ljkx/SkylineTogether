using System;
using System.Collections.Generic;
using SkylineTogether.Core.Motion;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Motion
{
    /// <summary>Reach classes of the motion interest (ARCHITECTURE.md#movers): aircraft everywhere, ships and trains wider.</summary>
    public sealed class MotionReachTests
    {
        private static MotionInterestRegion Region(float radius) =>
            new MotionInterestRegion { CenterX = 1000f, CenterZ = -500f, Radius = radius, LeaveRadius = radius * 1.25f };

        [Test]
        public void NormalMoversKeepTheClientsRegion()
        {
            MotionInterestRegion own = Region(600f);
            MotionInterestRegion normal = MotionReach.RegionFor(own, MoverReach.Normal);
            Assert.Equal(own.Radius, normal.Radius);
            Assert.Equal(own.LeaveRadius, normal.LeaveRadius);
            Assert.False(normal.Contains(1000f + 700f, -500f, false));
        }

        [Test]
        public void AircraftAreInInterestAnywhereOnTheMap()
        {
            // a plane seen in the sky from the far side of a 14.3 km map is still streamed (the reported bug: planes far
            // from the client's camera were pinned in mid-air)
            MotionInterestRegion own = Region(200f);
            MotionInterestRegion global = MotionReach.RegionFor(own, MoverReach.Global);
            Assert.True(global.Contains(1000f + 14336f, -500f + 14336f, false), "opposite corner of the map");
            Assert.True(global.Contains(-7168f, 7168f, false));
            Assert.Equal(global.Radius, global.LeaveRadius);
            Assert.False(global.Contains(float.NaN, 0f, false), "non-finite positions are never inside");
        }

        [Test]
        public void WideMoversUseAScaledRegionWithinBounds()
        {
            MotionInterestRegion small = MotionReach.RegionFor(Region(200f), MoverReach.Wide);
            Assert.Equal(MotionReach.WideMinRadius, small.Radius);
            MotionInterestRegion mid = MotionReach.RegionFor(Region(800f), MoverReach.Wide);
            Assert.Equal(2400f, mid.Radius);
            Assert.Near(2400.0 * 1.25, mid.LeaveRadius, 1e-3);
            MotionInterestRegion big = MotionReach.RegionFor(Region(1000f * 2f), MoverReach.Wide);
            Assert.Equal(MotionReach.WideMaxRadius, big.Radius);
            Assert.True(mid.Contains(1000f + 2300f, -500f, false));
            Assert.False(mid.Contains(1000f + 2500f, -500f, false));
            Assert.True(mid.Contains(1000f + 2900f, -500f, true), "hysteresis while inside");
        }

        [Test]
        public void FieldFollowsTheClientsOwnRegion()
        {
            MotionInterestRegion own = Region(400f);
            Assert.Equal(MotionField.Near, MotionReach.FieldOf(own, 1000f + 150f, -500f));
            Assert.Equal(MotionField.Mid, MotionReach.FieldOf(own, 1000f, -500f + 350f));
            Assert.Equal(MotionField.Far, MotionReach.FieldOf(own, 1000f + 5000f, -500f));
        }

        [Test]
        public void FarSamplesAreEverySecondCycleSpreadOverMovers()
        {
            const byte u = 10; // aircraft update frame
            int sentA = 0, sentB = 0, together = 0;
            for (uint cycle = 0; cycle < 64; cycle++)
            {
                uint tick = cycle * 16 + u;
                bool a = MotionReach.SendsFarSample(tick, 1000);
                bool b = MotionReach.SendsFarSample(tick, 1001);
                if (a) sentA++;
                if (b) sentB++;
                if (a && b) together++;
                // never two skipped cycles in a row
                Assert.True(a || MotionReach.SendsFarSample(tick + 16, 1000), "cycle " + cycle);
            }
            Assert.Equal(32, sentA);
            Assert.Equal(32, sentB);
            Assert.Equal(0, together);
        }

        private sealed class CaptureOutput : IPeerOutput
        {
            public readonly List<(MessageKind Kind, byte[] Body)> Messages = new List<(MessageKind, byte[])>();

            public PeerId Peer => new PeerId(1);

            public ConnectionStats Stats => default(ConnectionStats);

            public long QueuedBytes => 0;

            public double OldestQueuedSeconds => 0;

            public bool CanAcceptReliable(int bytes) => true;

            public SendResult Send(Lane lane, Delivery delivery, byte[] buffer, int offset, int count)
            {
                var body = new byte[count - 2];
                Buffer.BlockCopy(buffer, offset + 2, body, 0, body.Length);
                Messages.Add(((MessageKind)buffer[offset], body));
                return SendResult.Ok;
            }

            public void Deliver(MotionClientStager stager, List<MotionPin> pins)
            {
                foreach ((MessageKind kind, byte[] body) in Messages)
                {
                    if (kind == MessageKind.MotionControl) stager.ApplyControl(body, 0, body.Length, pins);
                    else if (kind == MessageKind.Motion) stager.ApplyDatagram(body, 0, body.Length, out _);
                }
                Messages.Clear();
            }
        }

        /// <summary>An airliner at cruise: 70 m/s along +z at 600 m, frame of tick t.</summary>
        private static MotionFrame Plane(uint tick) =>
            new MotionFrame { X = -3000f, Y = 600f, Z = 2000f + tick * (70f / 60f), VZ = 70f, QW = 1f, Flags = 0x4000 };

        [Test]
        public void AFarAircraftAtHalfRateStillFliesSmoothlyOnTheClient()
        {
            var peer = new PeerId(1);
            var enc = new MotionHostEncoder(NullLog.Instance);
            enc.AddPeer(peer);
            var output = new CaptureOutput();
            var stager = new MotionClientStager();
            var pins = new List<MotionPin>();
            const byte u = 10;
            const uint id = 70001;
            uint start = 4000 * 16 + u;
            var frames = new MotionFrame[4];
            var ticks = new uint[4];
            for (int k = 0; k < 4; k++)
            {
                ticks[k] = start - (uint)(16 * k);
                frames[k] = Plane(ticks[k]);
            }
            enc.EnterInterest(peer, new NetSlot(id), u, frames, ticks);
            enc.Flush(peer, output);
            output.Deliver(stager, pins);
            int sent = 0;
            for (uint cycle = 1; cycle <= 24; cycle++)
            {
                uint tick = start + cycle * 16;
                if (MotionReach.SendsFarSample(tick, id))
                {
                    enc.AddFrame(peer, tick, new NetSlot(id), Plane(tick), false, false);
                    sent++;
                }
                enc.Flush(peer, output);
                output.Deliver(stager, pins);
                // the client renders about two cycles behind the host: every cycle there has a pose on the true path
                uint shown = tick - 32;
                Assert.True(stager.TrySample(0, shown, out MotionFrame f), "cycle " + cycle);
                Assert.Near(Plane(shown).Z, f.Z, 0.5, "cycle " + cycle);
                Assert.Near(600.0, f.Y, 0.1);
            }
            Assert.Equal(12, sent);
            Assert.Equal(0, pins.Count);
        }
    }
}
