using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// The host's defences from the adversarial network review: identity and duplicate resolution after the password,
    /// the order of the handshake's checks, remembered roles, join watchdogs and cooldowns, bans of direct connections.
    /// </summary>
    public sealed class HardeningTests
    {
        private static void Pump(SessionRig rig, IEnumerable<RawPeer> peers, int frames = 10)
        {
            for (int i = 0; i < frames; i++)
            {
                rig.Clock.Advance(1 / 60.0);
                rig.Host.Tick();
                foreach (var c in rig.Clients) c.Client.Tick();
                foreach (RawPeer p in peers) p.Poll();
            }
        }

        private static RawPeer Handshake(SessionRig rig, RawPeer peer, FakeInfo info = null)
        {
            peer.Hello(info ?? rig.HostInfo);
            peer.Manifest(info ?? rig.HostInfo);
            Pump(rig, new[] { peer });
            return peer;
        }

        // ------------------------------------------------------------------------------------- duplicates (finding 2)

        [Test]
        public void AConnectionWithALivePlayersIdentityButNoPasswordLeavesThemAlone()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "secret" });
            var (victim, _, _) = rig.AddClient("Victim", "secret", steamId: 1001);
            rig.Run(400);
            Assert.Equal(ClientState.Live, victim.State, victim.EndReason);

            // the transport reports the victim's Steam ID for the attacker (a direct connection whose identity Steam did
            // not verify, before the transport fix): it is challenged, refused, and the victim keeps its connection
            var attackerTransport = rig.HostTransport.CreateClient(new PeerIdentity(1001, "203.0.113.9:5555", "Victim", true));
            var attacker = new SessionClient(attackerTransport, new FakeInfo(), new FakeLoader(), new ClientOptions { PlayerName = "x", SteamId = 1001, Password = "" }, rig.Clock, rig.Log);
            attacker.Connect(new Endpoint());
            rig.Clients.Add((attacker, new FakeLoader(), new FakeInfo()));
            rig.Run(200);
            Assert.Equal(ClientState.Ended, attacker.State);
            Assert.Equal(ClientState.Live, victim.State, victim.EndReason);
            Assert.Equal(1, rig.Host.LivePeerCount);

            // a wrong password does not touch the victim either
            var wrong = Handshake(rig, new RawPeer(rig, new PeerIdentity(1001, "203.0.113.9:5556", "Victim", true), 1001));
            wrong.AnswerChallenge("guess");
            Pump(rig, new[] { wrong });
            Assert.Equal(RefuseReason.BadPassword, wrong.Refusal().Reason);
            Assert.Equal(ClientState.Live, victim.State, victim.EndReason);
        }

        [Test]
        public void TheSamePlayerWithThePasswordStillReplacesItsStaleConnection()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "secret" });
            var (first, _, _) = rig.AddClient("Anna", "secret", steamId: 1002);
            rig.Run(400);
            Assert.Equal(ClientState.Live, first.State, first.EndReason);
            var again = Handshake(rig, new RawPeer(rig, "Anna", 1002));
            Assert.Equal(1, rig.Host.LivePeerCount, "nothing happens before the password");
            again.AnswerChallenge("secret");
            Pump(rig, new[] { again });
            Assert.True(again.Got(MessageKind.Welcome), "admitted");
            rig.Run(30);
            Assert.Equal(ClientState.Ended, first.State, "the stale connection was replaced");
        }

        // ------------------------------------------------------------------------------------ check order (finding 6)

        [Test]
        public void ABannedPlayerOfAnotherVersionLearnsOnlyThatItIsBanned()
        {
            var rig = new SessionRig();
            rig.Host.Options.Bans.Add(7401, null, "Banned", 0);
            var peer = new RawPeer(rig, "Banned", 7401);
            peer.Hello(rig.HostInfo, protocol: (ushort)(ProtocolVersion.Current + 1));
            Pump(rig, new[] { peer });
            RefuseMessage r = peer.Refusal();
            Assert.Equal(RefuseReason.Banned, r.Reason);
            Assert.DoesNotContain(ProtocolVersion.Current.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Detail ?? "");
            Assert.Null(MismatchReport.Decode(r.MismatchReport)?.HostProtocol);

            var friends = new HashSet<ulong>();
            var policy = new SessionRig(new HostOptions { HostName = "Hosty", JoinPolicy = JoinPolicy.FriendsAndInvited, IsFriendOrInvited = friends.Contains });
            var stranger = new RawPeer(policy, "Stranger", 7402);
            stranger.Hello(policy.HostInfo, protocol: (ushort)(ProtocolVersion.Current + 1));
            Pump(policy, new[] { stranger });
            Assert.Equal(RefuseReason.NotInvited, stranger.Refusal().Reason);
        }

        [Test]
        public void PingsAreNotAnsweredBeforeAdmission()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });
            var peer = new RawPeer(rig, "Pinger", 7403);
            var w = new ByteWriter();
            PingMessage.Write(w, new PingMessage { Sequence = 1, SentSeconds = 1 });
            peer.Send(MessageKind.Ping, w);
            Pump(rig, new[] { peer });
            Assert.False(peer.Got(MessageKind.Ping), "no reply before the hello");
            peer.Hello(rig.HostInfo);
            peer.Send(MessageKind.Ping, w);
            Pump(rig, new[] { peer });
            Assert.NotNull(peer.Challenge());
            Assert.False(peer.Got(MessageKind.Ping), "no reply before admission");
        }
    }
}
