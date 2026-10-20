using System.Collections.Generic;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// What the host actually writes about direct-IP players (SECURITY.md, ARCHITECTURE.md#logs: no full network address in
    /// the log), at debug level and through a whole session: a direct player without a name who joins, plays and is
    /// kicked, a wrong password, a refused version and a stranger who only connects. Every address appears masked.
    /// </summary>
    public sealed class DirectPeerLogTests
    {
        private static readonly string[] FullAddresses = { "203.0.113.7", "203.0.113.8", "203.0.113.9", "198.51.100.20" };

        private static void Pump(SessionRig rig, RawPeer peer)
        {
            for (int i = 0; i < 10; i++)
            {
                rig.Clock.Advance(1 / 60.0);
                rig.Host.Tick();
                peer.Poll();
            }
        }

        [Test]
        public void TheHostsLogHoldsNoFullAddressOfADirectPlayer()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });

            // a direct player without a display name joins with the password, plays, and is kicked
            LoopbackTransport t = rig.HostTransport.CreateClient(new PeerIdentity(0, "203.0.113.7:5000", null, true));
            var info = new FakeInfo();
            var loader = new FakeLoader();
            var client = new SessionClient(t, info, loader, new ClientOptions { PlayerName = "Direct", Password = "pw" }, rig.Clock, rig.Log);
            client.Connect(new Endpoint());
            rig.Clients.Add((client, loader, info));
            rig.Run(400);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            var admitted = new List<PeerId>();
            rig.Host.GetAdmittedPeers(admitted);
            rig.Host.Kick(admitted[0], "bye");
            rig.Run(60);
            Assert.Equal(ClientEndKind.Kicked, client.EndKind, client.EndReason);

            // a wrong password
            var wrong = new RawPeer(rig, new PeerIdentity(0, "203.0.113.8:5001", null, true), 0);
            wrong.Hello(rig.HostInfo);
            wrong.Manifest(rig.HostInfo);
            Pump(rig, wrong);
            wrong.AnswerChallenge("guess");
            Pump(rig, wrong);
            Assert.Equal(RefuseReason.BadPassword, wrong.Refusal().Reason);

            // another version of the mod
            var old = new RawPeer(rig, new PeerIdentity(0, "203.0.113.9:5002", null, true), 0);
            old.Hello(rig.HostInfo, protocol: 1);
            Pump(rig, old);
            Assert.Equal(RefuseReason.ProtocolVersion, old.Refusal().Reason);

            // a stranger that connects and goes without a word
            var stranger = new RawPeer(rig, new PeerIdentity(0, "198.51.100.20:6000", null, true), 0);
            Pump(rig, stranger);
            stranger.Channel.Connection.Close(0, "bye", false);
            rig.RunHostOnly(30);

            Assert.True(rig.Log.Contains("203.0.113.x"), "the direct player is in the log, masked");
            Assert.True(rig.Log.Contains("198.51.100.x"), "the stranger's connection is logged, masked");
            foreach (string line in rig.Log.Lines)
            {
                foreach (string address in FullAddresses)
                {
                    Assert.False(line.Contains(address), "the full address " + address + " in: " + line);
                }
            }
        }
    }
}
