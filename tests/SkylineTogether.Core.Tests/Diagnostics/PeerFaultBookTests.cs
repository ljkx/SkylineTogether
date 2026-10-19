using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Diagnostics
{
    /// <summary>One peer's bad input is counted against that peer, never against the feature that serves everyone.</summary>
    public sealed class PeerFaultBookTests
    {
        [Test]
        public void APeerIsMutedAfterRepeatedFailuresAndOthersAreUnaffected()
        {
            var book = new PeerFaultBook();
            PeerFaultVerdict v = book.Report(3, 0);
            Assert.True(v.Log, "the first failure is logged");
            for (int i = 1; i < FeatureHealth.TripCount - 1; i++) Assert.False(book.Report(3, i).JustMuted);
            Assert.False(book.IsMuted(3));
            v = book.Report(3, 5);
            Assert.True(v.JustMuted, "the fifth failure within the window mutes the peer");
            Assert.True(book.IsMuted(3));
            Assert.False(book.Report(3, 6).JustMuted, "muted once");
            Assert.False(book.IsMuted(4), "another peer is not affected");
            Assert.Equal(1, book.MutedCount);
            book.Forget(3);
            Assert.False(book.IsMuted(3), "a peer that left starts clean");
        }

        [Test]
        public void SlowFailuresNeverMute()
        {
            var book = new PeerFaultBook();
            for (int i = 0; i < 20; i++) Assert.False(book.Report(1, i * FeatureHealth.TripWindowSeconds).JustMuted);
            Assert.False(book.IsMuted(1));
        }

        [Test]
        public void LoggingIsRateLimitedPerPeer()
        {
            var book = new PeerFaultBook();
            Assert.True(book.Report(1, 0).Log);
            Assert.False(book.Report(1, 1).Log);
            Assert.True(book.Report(2, 1).Log, "each peer has its own log limit");
            PeerFaultVerdict later = book.Report(1, 1 + FeatureHealth.LogIntervalSeconds);
            Assert.True(later.Log);
            Assert.Equal(1, later.Suppressed);
        }
    }
}
