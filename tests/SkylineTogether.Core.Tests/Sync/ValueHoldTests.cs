using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// Value messages wait for the structure backlog, but not without end (R2 S9): their acknowledgement holds block every
    /// later acknowledgement, and a CPU-starved client whose backlog never cleared was dropped as not applying.
    /// </summary>
    public sealed class ValueHoldTests
    {
        [Test]
        public void ValuesWaitForTheBacklogForAWhile()
        {
            Assert.True(ValueHoldRule.Wait(3, 5, 512, 100, 105));
            Assert.False(ValueHoldRule.Wait(0, 5, 512, 100, 105), "no backlog");
            Assert.False(ValueHoldRule.Wait(3, 0, 512, double.NaN, 105), "nothing held");
            Assert.False(ValueHoldRule.Wait(3, 512, 512, 100, 101), "too many held");
        }

        [Test]
        public void TheOldestValueStopsWaitingAfterTenSeconds()
        {
            Assert.True(ValueHoldRule.Wait(1, 5, 512, 100, 109.9));
            Assert.False(ValueHoldRule.Wait(1, 5, 512, 100, 110), "a backlog that never quite clears no longer stalls the acknowledgements");
            Assert.True(ValueHoldRule.Wait(1, 5, 512, double.NaN, 1000), "no arrival time: as before");
        }

        [Test]
        public void TheQueueKnowsWhenItsOldestMessageArrived()
        {
            var acks = new ApplyAckTracker();
            var q = new AckHeldQueue<int>();
            q.Attach(acks);
            Assert.True(double.IsNaN(q.OldestQueuedAt));
            q.Enqueue(1, 50);
            q.Enqueue(2, 60);
            Assert.Equal(50.0, q.OldestQueuedAt);
            Assert.True(q.TryDequeue(out int first));
            Assert.Equal(1, first);
            Assert.Equal(60.0, q.OldestQueuedAt);
            q.Clear();
            Assert.True(double.IsNaN(q.OldestQueuedAt));
        }
    }
}
