using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Diagnostics
{
    /// <summary>
    /// Addresses in free text (<see cref="AddressMask.InText"/>, Steam's connection descriptions and close texts) at the
    /// edges: at the end of a sentence, IPv6 without brackets, with a zone, next to versions and times.
    /// </summary>
    public sealed class AddressMaskTextTests
    {
        [Test]
        public void AnIpv4AddressAtTheEndOfASentenceIsMasked()
        {
            Assert.Equal("connecting to 203.0.113.x.", AddressMask.InText("connecting to 203.0.113.7."));
            Assert.Equal("from 203.0.113.x. Retrying", AddressMask.InText("from 203.0.113.7. Retrying"));
            Assert.Equal("(203.0.113.x), [203.0.113.x]", AddressMask.InText("(203.0.113.7), [203.0.113.8]"));
            Assert.Equal("203.0.113.x:27015.", AddressMask.InText("203.0.113.7:27015."));
        }

        [Test]
        public void VersionsWithMoreThanFourPartsStayAndFourPartVersionsAreTheOnlyCost()
        {
            Assert.Equal("game 1.6.2.1.3, mod 0.1.0", AddressMask.InText("game 1.6.2.1.3, mod 0.1.0"));
            Assert.Equal("v 1.6.2.1.3.", AddressMask.InText("v 1.6.2.1.3."), "a five-part version at the end of a sentence");
            Assert.Equal("999.1.1.1.", AddressMask.InText("999.1.1.1."), "not an address");
        }

        [Test]
        public void AnUnbracketedIpv6AddressIsMasked()
        {
            Assert.Equal("peer 2001:db8:x:x:x:x:x:x port 27015", AddressMask.InText("peer 2001:db8:85a3::8a2e:370:7334 port 27015"));
            Assert.Equal("peer 2001:db8:x:x:x:x:x:x.", AddressMask.InText("peer 2001:0db8:85a3:0000:0000:8a2e:0370:7334."), "eight groups, sentence end");
            Assert.Equal("link fe80:0:x:x:x:x:x:x", AddressMask.InText("link fe80::1ff:fe23:4567:890a%3"), "the zone goes with it");
            Assert.Equal("local ::ffff:198.51.100.x", AddressMask.InText("local ::ffff:198.51.100.20"));
            Assert.Equal("#3 UDP [2001:db8:x:x:x:x:x:x]:27015", AddressMask.InText("#3 UDP [2001:db8::5]:27015"), "bracketed as before");
        }

        [Test]
        public void TimesSteamIdsAndOtherColonTextStay()
        {
            Assert.Equal("at 12:30:45 and 01:02:03:04", AddressMask.InText("at 12:30:45 and 01:02:03:04"));
            Assert.Equal("#9 P2P steamid:76561190000000000", AddressMask.InText("#9 P2P steamid:76561190000000000"));
            Assert.Equal("a :: b", AddressMask.InText("a :: b"), "no digits");
            Assert.Equal("Game.Net::Edge", AddressMask.InText("Game.Net::Edge"));
        }
    }
}
