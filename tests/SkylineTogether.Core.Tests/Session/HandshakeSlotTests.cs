using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// Handshake slots (R2 N3): a challenged connection that never answers is dropped quickly, and a remote that reconnects
    /// in a loop is closed before its hello; an honest joiner and its rejoins are not affected.
    /// </summary>
    public sealed class HandshakeSlotTests
    {
        private static void Pump(SessionRig rig, RawPeer peer, double seconds)
        {
            for (double t = 0; t < seconds; t += 0.25)
            {
                rig.Clock.Advance(0.25);
                rig.Host.Tick();
                peer.Poll();
            }
        }

        [Test]
        public void AHelloFollowedBySilenceLosesItsSlotAfterTheAnswerTimeout()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });
            var peer = new RawPeer(rig, "Silent", 8101);
            peer.Hello(rig.HostInfo);
            Pump(rig, peer, 1);
            Assert.NotNull(peer.Challenge());
            Pump(rig, peer, 17);
            Assert.True(peer.Disconnected, "dropped after 15 s without an answer, not 30");
        }

        [Test]
        public void AnHonestAnswerWithinSecondsIsAdmitted()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });
            var (client, _, _) = rig.AddClient("Anna", "pw");
            rig.Run(300);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
        }

        [Test]
        public void ARemoteThatReconnectsInALoopIsClosedBeforeItsHello()
        {
            var limiter = new ConnectionRateLimiter(20, 60);
            int allowed = 0;
            for (int i = 0; i < 40; i++)
            {
                if (limiter.Allow("ip:203.0.113.9", i * 0.5)) allowed++;
            }
            Assert.Equal(20, allowed, "twenty in the first minute");
            Assert.Equal(20, (int)limiter.Refused);
            Assert.True(limiter.Allow("ip:198.51.100.1", 20), "another remote is not affected");
            Assert.True(limiter.Allow(null, 20), "no identity: never limited");
            Assert.True(limiter.Allow("ip:203.0.113.9", 61), "the window slides");
        }

        [Test]
        public void SixAutomaticRejoinsStayWithinTheLimit()
        {
            var limiter = new ConnectionRateLimiter(20, 60);
            double t = 0;
            foreach (double wait in new double[] { 2, 5, 10, 20, 30, 30 })
            {
                t += wait;
                Assert.True(limiter.Allow("steam:4242", t));
            }
        }

        [Test]
        public void TheHostClosesALoopingRemote()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", MaxConnectionsPerRemotePerMinute = 3 });
            RawPeer last = null;
            for (int i = 0; i < 4; i++)
            {
                last = new RawPeer(rig, "Looper", 8102);
                Pump(rig, last, 0.5);
            }
            Assert.True(last.Disconnected, "the fourth connection within a minute is closed");
            Assert.Contains("connect more than 3 times a minute", string.Join("\n", rig.Log.Lines));
        }
    }
}
