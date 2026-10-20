using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// <see cref="HostProofExpectation"/> (R3 A1): over Steam P2P the host must prove the password only when the session is
    /// known to have one: its lobby says so, or it challenged this game before (a retry with the typed password, a
    /// rejoin). A password saved in the join settings never requires it; a host that removed its password is not refused
    /// again after one "Couldn't confirm the host".
    /// </summary>
    public sealed class HostProofExpectationTests
    {
        private const ulong Host = 1001UL;
        private const ulong Other = 1002UL;

        [Test]
        public void NothingIsExpectedOfAnUnknownHost()
        {
            var e = new HostProofExpectation();
            Assert.False(e.Expects(Host), "a saved password alone does not count: nothing records it");
            Assert.False(e.Expects(0), "a direct join has no Steam ID (it always requires the proof anyway)");
        }

        [Test]
        public void TheLobbysLatestWordCounts()
        {
            var e = new HostProofExpectation();
            e.OnLobby(Host, true);
            Assert.True(e.Expects(Host));
            Assert.False(e.Expects(Other));
            e.OnLobby(Host, false);
            Assert.False(e.Expects(Host), "the host removed its password");
        }

        [Test]
        public void AHostThatAskedForThePasswordMustProveItOnTheRetryAndTheRejoin()
        {
            var e = new HostProofExpectation();
            e.OnEnded(Host, ClientEndKind.PasswordRequired, default(RefuseReason), false);
            Assert.True(e.Expects(Host), "the player types the password and tries again");
            var f = new HostProofExpectation();
            f.OnEnded(Host, ClientEndKind.Refused, RefuseReason.BadPassword, false);
            Assert.True(f.Expects(Host), "a wrong password: it challenged");
            var g = new HostProofExpectation();
            g.OnEnded(Host, ClientEndKind.ConnectionLost, default(RefuseReason), true);
            Assert.True(g.Expects(Host), "a challenged session that dropped: the rejoin expects the challenge again");
            var h = new HostProofExpectation();
            h.OnEnded(Host, ClientEndKind.ConnectionLost, default(RefuseReason), false);
            h.OnEnded(Host, ClientEndKind.Refused, RefuseReason.Full, false);
            Assert.False(h.Expects(Host), "never challenged");
        }

        [Test]
        public void AHostThatDidNotProveItIsForgottenAfterThatJoin()
        {
            var e = new HostProofExpectation();
            e.OnLobby(Host, true);
            e.OnEnded(Host, ClientEndKind.HostNotProven, default(RefuseReason), false);
            Assert.False(e.Expects(Host), "shown once; the player's next try decides");
        }

        [Test]
        public void TheMemoryIsBounded()
        {
            var e = new HostProofExpectation();
            for (ulong i = 1; i <= HostProofExpectation.MaxHosts + 5; i++) e.OnLobby(i, true);
            Assert.False(e.Expects(1), "the oldest went first");
            Assert.True(e.Expects(HostProofExpectation.MaxHosts + 5));
        }
    }
}
