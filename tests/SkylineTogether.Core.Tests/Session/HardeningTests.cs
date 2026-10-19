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

        // ---------------------------------------------------------------------------------- roles (finding 3)

        private static SessionClient Join(SessionRig rig, string name, ulong steamId, string password = "")
        {
            var (c, _, _) = rig.AddClient(name, password, steamId: steamId);
            rig.Run(400);
            Assert.Equal(ClientState.Live, c.State, c.EndReason);
            return c;
        }

        [Test]
        public void ARejoiningPlayerKeepsTheRoleTheHostGaveThem()
        {
            var rig = new SessionRig();
            SessionClient griefer = Join(rig, "Griefer", 1101);
            SessionClient helper = Join(rig, "Helper", 1102);
            Assert.True(rig.Host.SetRole(griefer.LocalPeer, SessionRole.Viewer));
            Assert.True(rig.Host.SetRole(helper.LocalPeer, SessionRole.CoHost));
            rig.Run(20);
            griefer.Leave();
            helper.Leave();
            rig.Run(20);

            SessionClient again = Join(rig, "Griefer", 1101);
            Assert.Equal(SessionRole.Viewer, rig.Host.GetRole(again.LocalPeer), "a demoted player cannot become a builder again by rejoining");
            Assert.Equal(SessionRole.Viewer, again.LocalRole, "the welcome carries the remembered role");
            SessionClient helperAgain = Join(rig, "Helper", 1102);
            Assert.Equal(SessionRole.CoHost, rig.Host.GetRole(helperAgain.LocalPeer));
            SessionClient fresh = Join(rig, "Fresh", 1103);
            Assert.Equal(SessionRole.Builder, rig.Host.GetRole(fresh.LocalPeer), "the default is unchanged");
        }

        [Test]
        public void NewPlayersCanJoinAsViewersWhileKnownOnesKeepTheirRole()
        {
            var rig = new SessionRig();
            SessionClient anna = Join(rig, "Anna", 1201);
            Assert.True(rig.Host.SetRole(anna.LocalPeer, SessionRole.Builder));
            anna.Leave();
            rig.Run(20);
            rig.Host.Options.NewPlayersJoinAsViewer = true; // read live
            SessionClient stranger = Join(rig, "Stranger", 1202);
            Assert.Equal(SessionRole.Viewer, rig.Host.GetRole(stranger.LocalPeer));
            Assert.Equal(SessionRole.Viewer, stranger.LocalRole);
            SessionClient annaAgain = Join(rig, "Anna", 1201);
            Assert.Equal(SessionRole.Builder, rig.Host.GetRole(annaAgain.LocalPeer), "a player the host made a builder stays one");
            Assert.True(rig.Host.SetRole(stranger.LocalPeer, SessionRole.Builder));
            Assert.Equal(SessionRole.Builder, rig.Host.GetRole(stranger.LocalPeer));

            // the in-process test recorder always builds
            var local = Handshake(rig, new RawPeer(rig, new PeerIdentity(0, "recorder", "Recorder", false, local: true), 0));
            WelcomeMessage w = null;
            foreach ((MessageKind kind, byte[] body) in local.Received)
            {
                if (kind == MessageKind.Welcome) w = WelcomeMessage.Decode(body, 0, body.Length);
            }
            Assert.NotNull(w);
            Assert.Equal(SessionRole.Builder, w.Role);
        }

        [Test]
        public void ADirectPlayersRoleIsRememberedByAddress()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });
            var a = Handshake(rig, new RawPeer(rig, new PeerIdentity(0, "[2001:db8:1:2::10]:5000", "Ipv6", true), 0));
            a.AnswerChallenge("pw");
            Pump(rig, new[] { a });
            Assert.True(a.Got(MessageKind.Welcome));
            var list = new List<PeerId>();
            rig.Host.GetAdmittedPeers(list);
            Assert.True(rig.Host.SetRole(list[0], SessionRole.Viewer));
            rig.Host.Kick(list[0], "bye");
            // another address of the same /64 network
            var b = Handshake(rig, new RawPeer(rig, new PeerIdentity(0, "[2001:db8:1:2::99]:5001", "Ipv6", true), 0));
            b.AnswerChallenge("pw");
            Pump(rig, new[] { b });
            list.Clear();
            rig.Host.GetAdmittedPeers(list);
            Assert.Equal(SessionRole.Viewer, rig.Host.GetRole(list[0]));
            Assert.Equal("2001:db8:1:2::/64", BanList.AddressKey("[2001:DB8:1:2:abcd::1]:27015"));
            Assert.Equal("203.0.113.7", BanList.AddressKey("203.0.113.7:27015"));
            Assert.Equal("203.0.113.7", BanList.AddressKey("[::ffff:203.0.113.7]:1"));
            Assert.Null(BanList.AddressKey(" "));
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
