using System.Collections.Generic;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// Direct-IP addresses are masked in every log line of a session (ARCHITECTURE.md#logs): connection lines, wrong
    /// passwords, bans and lifted bans name a direct player's address only as <c>203.0.113.x</c>, while the ban list
    /// itself keeps the full address it needs.
    /// </summary>
    public sealed class LogAddressTests
    {
        private static void Pump(SessionRig rig, RawPeer peer)
        {
            for (int i = 0; i < 10; i++)
            {
                rig.Clock.Advance(1 / 60.0);
                rig.Host.Tick();
                peer.Poll();
            }
        }

        private static RawPeer Handshake(SessionRig rig, RawPeer peer)
        {
            peer.Hello(rig.HostInfo);
            peer.Manifest(rig.HostInfo);
            Pump(rig, peer);
            return peer;
        }

        [Test]
        public void AnIdentityIsWrittenWithItsAddressMasked()
        {
            Assert.Equal("Anna (0, 203.0.113.x:27015)", new PeerIdentity(0, "203.0.113.7:27015", "Anna", true).ToString());
            Assert.Equal("? (0, [2001:db8:x:x:x:x:x:x]:27015)", new PeerIdentity(0, "[2001:db8::7]:27015", null, true).ToString());
            Assert.Equal("Ben (76561190000000000)", new PeerIdentity(76561190000000000, null, "Ben").ToString(), "a Steam peer has no address");
        }

        [Test]
        public void ATransportAddressKeepsItsPort()
        {
            Assert.Equal("203.0.113.x:27015", AddressMask.Address("203.0.113.7:27015"));
            Assert.Equal("[2001:db8:x:x:x:x:x:x]:27015", AddressMask.Address("[2001:db8::7]:27015"));
            Assert.Equal("2001:db8:x:x:x:x:x:x", AddressMask.Address("2001:db8::7"), "a bare IPv6 address has no port");
            Assert.Equal("2001:db8:x:x:x:x:x:x/64", AddressMask.Address("2001:db8:1:2::/64"), "a ban key of an IPv6 network");
            Assert.Equal("m***.ddns.example:27015", AddressMask.Address("myhost.ddns.example:27015"));
            Assert.Equal("203.0.113.x", AddressMask.Address("203.0.113.7"));
            Assert.Equal("", AddressMask.Address(null));
        }

        [Test]
        public void AnAddressKeyIsMaskedAndASteamKeyIsNot()
        {
            Assert.Equal("ip:203.0.113.x", AddressMask.Key("ip:203.0.113.7"));
            Assert.Equal("ip:2001:db8:x:x:x:x:x:x/64", AddressMask.Key("ip:2001:db8:1:2::/64"));
            Assert.Equal("steam:76561190000000000", AddressMask.Key("steam:76561190000000000"));
            Assert.Equal("unknown", AddressMask.Key("unknown"));
        }

        [Test]
        public void TheHostsLogNeverHoldsADirectPlayersFullAddress()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });

            // a wrong password, then a join and a ban, then the ban lifted
            var wrong = Handshake(rig, new RawPeer(rig, new PeerIdentity(0, "203.0.113.7:5000", null, true), 0));
            wrong.AnswerChallenge("guess");
            Pump(rig, wrong);
            Assert.Equal(RefuseReason.BadPassword, wrong.Refusal().Reason);

            var anna = Handshake(rig, new RawPeer(rig, new PeerIdentity(8101, "198.51.100.20:4000", "Anna", true), 8101));
            anna.AnswerChallenge("pw");
            Pump(rig, anna);
            var list = new List<PeerId>();
            rig.Host.GetAdmittedPeers(list);
            Assert.True(rig.Host.Ban(list[0], "bye"));
            Assert.True(rig.Host.Options.Bans.IsBanned(0, "198.51.100.20:4001"), "the ban list keeps the full address");
            foreach (BanEntry e in new List<BanEntry>(rig.Host.Options.Bans.Entries))
            {
                if (e.SteamId == 0) Assert.True(rig.Host.Unban(e.Key));
            }

            Assert.True(rig.Log.Contains("203.0.113.x"), "the refused connection is logged, masked");
            Assert.True(rig.Log.Contains("198.51.100.x"), "the ban is logged, masked");
            foreach (string line in rig.Log.Lines)
            {
                Assert.False(line.Contains("203.0.113.7"), "full address in: " + line);
                Assert.False(line.Contains("198.51.100.20"), "full address in: " + line);
            }
        }
    }
}
