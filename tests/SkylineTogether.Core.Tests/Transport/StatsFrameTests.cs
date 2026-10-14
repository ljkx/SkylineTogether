using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Transport
{
    /// <summary>One transport statistics query per peer and frame (<see cref="StatsFrame"/>, <see cref="PeerOutbox.Stats"/>).</summary>
    public sealed class StatsFrameTests
    {
        private static PeerOutbox Outbox(FakeConnection connection) => new PeerOutbox(connection, new PeerId(2), new OutboxConfig(), new ManualClock(), new ListLog());

        [Test]
        public void TheConsumersOfOneFrameShareOneQuery()
        {
            StatsFrame.Reset();
            try
            {
                var connection = new FakeConnection { PendingReliable = 10 };
                PeerOutbox outbox = Outbox(connection);
                StatsFrame.Advance();
                // deferred values, motion, agents, attention values, cell maps and flow status read it in one frame
                for (int i = 0; i < 6; i++) Assert.Equal(10, outbox.Stats.PendingReliableBytes);
                Assert.Equal(1L, outbox.StatsQueries, "one query for six reads");
                Assert.Equal(5L, outbox.StatsReused);
                connection.PendingReliable = 20;
                Assert.Equal(10, outbox.Stats.PendingReliableBytes, "the frame's numbers stay the same within the frame");
                StatsFrame.Advance();
                Assert.Equal(20, outbox.Stats.PendingReliableBytes, "the next frame queries again");
                Assert.Equal(2L, outbox.StatsQueries);
            }
            finally
            {
                StatsFrame.Reset();
            }
        }

        [Test]
        public void WithoutFramesEveryReadQueries()
        {
            StatsFrame.Reset();
            var connection = new FakeConnection { PendingReliable = 1 };
            PeerOutbox outbox = Outbox(connection);
            Assert.Equal(1, outbox.Stats.PendingReliableBytes);
            connection.PendingReliable = 2;
            Assert.Equal(2, outbox.Stats.PendingReliableBytes);
            Assert.Equal(2L, outbox.StatsQueries);
        }

        [Test]
        public void TheWindowCheckAlwaysSeesFreshNumbers()
        {
            StatsFrame.Reset();
            try
            {
                var connection = new FakeConnection();
                PeerOutbox outbox = Outbox(connection);
                StatsFrame.Advance();
                Assert.Equal(0, outbox.Stats.PendingReliableBytes);
                int window = new OutboxConfig().ReliableWindowBytes;
                connection.PendingReliable = window;
                Assert.False(outbox.CanAcceptReliable(1), "the reliable window is judged on a fresh query");
            }
            finally
            {
                StatsFrame.Reset();
            }
        }
    }
}
