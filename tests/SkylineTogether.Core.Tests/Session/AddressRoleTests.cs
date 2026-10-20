using System.Collections.Generic;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// Roles remembered by address (R2 N8): an address is shared (router, carrier NAT, VPN, IPv6 /64), so only a demotion
    /// to viewer is remembered for it; a co-host or builder role is remembered only under an authenticated Steam ID.
    /// </summary>
    public sealed class AddressRoleTests
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

        private static PeerId JoinDirect(SessionRig rig, string address)
        {
            var peer = new RawPeer(rig, new PeerIdentity(0, address, "Direct", true), 0);
            peer.Hello(rig.HostInfo);
            peer.Manifest(rig.HostInfo);
            Pump(rig, peer);
            peer.AnswerChallenge("pw");
            Pump(rig, peer);
            Assert.True(peer.Got(MessageKind.Welcome), address + " admitted");
            var list = new List<PeerId>();
            rig.Host.GetAdmittedPeers(list);
            return list[list.Count - 1];
        }

        [Test]
        public void ACoHostRoleIsNotInheritedByTheNextPlayerOfTheSameAddress()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });
            PeerId first = JoinDirect(rig, "203.0.113.7:5000");
            Assert.True(rig.Host.SetRole(first, SessionRole.CoHost));
            rig.Host.Kick(first, "bye");
            Pump(rig, new RawPeer(rig, "noop", 1));
            // a housemate behind the same router, with the password
            PeerId second = JoinDirect(rig, "203.0.113.7:5001");
            Assert.Equal(SessionRole.Builder, rig.Host.GetRole(second), "the default role, not the co-host's");
        }

        [Test]
        public void AViewerStaysAViewerByAddressUntilPromotedAgain()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });
            PeerId a = JoinDirect(rig, "198.51.100.4:5000");
            Assert.True(rig.Host.SetRole(a, SessionRole.Viewer));
            rig.Host.Kick(a, "bye");
            PeerId b = JoinDirect(rig, "198.51.100.4:5002");
            Assert.Equal(SessionRole.Viewer, rig.Host.GetRole(b), "a demotion survives a rejoin from another port");
            Assert.True(rig.Host.SetRole(b, SessionRole.Builder));
            rig.Host.Kick(b, "bye");
            rig.RunHostOnly(40); // a kick while joining counts towards the join cooldown
            PeerId c = JoinDirect(rig, "198.51.100.4:5003");
            Assert.Equal(SessionRole.Builder, rig.Host.GetRole(c), "the promotion forgot the demotion");
        }

        [Test]
        public void OnlySteamIdentitiesKeepElevatedRoles()
        {
            Assert.True(SessionHost.MayRememberRole("steam:4242", SessionRole.CoHost));
            Assert.True(SessionHost.MayRememberRole("steam:4242", SessionRole.Builder));
            Assert.True(SessionHost.MayRememberRole("ip:203.0.113.7", SessionRole.Viewer));
            Assert.False(SessionHost.MayRememberRole("ip:203.0.113.7", SessionRole.CoHost));
            Assert.False(SessionHost.MayRememberRole("ip:2001:db8:1:2::/64", SessionRole.Builder));
            Assert.False(SessionHost.MayRememberRole(null, SessionRole.Viewer));
        }
    }
}
