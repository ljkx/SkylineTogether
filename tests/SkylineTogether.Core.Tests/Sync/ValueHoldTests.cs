using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// Value messages wait for the structure batches that arrived before them, and only for those (R3 V1): during a long
    /// catch-up no value is applied ahead of an older batch (a fixed 10 s limit did that, and the older upsert then
    /// overwrote the newer value), and batches arriving behind a value never hold it back (R2 S9: a backlog that never
    /// quite cleared stalled every acknowledgement).
    /// </summary>
    public sealed class ValueHoldTests
    {
        [Test]
        public void AValueWaitsForTheBatchesThatArrivedBeforeIt()
        {
            // structure batches 3..7 queued; the oldest value message arrived after batch 5 (it holds 6)
            Assert.True(ValueHoldRule.Wait(6, 3, 5, 512), "batch 3 arrived before it");
            Assert.True(ValueHoldRule.Wait(6, 5, 5, 512), "batch 5 too");
            Assert.False(ValueHoldRule.Wait(6, 6, 5, 512), "batch 6 arrived after it");
            Assert.False(ValueHoldRule.Wait(6, 0, 5, 512), "no backlog");
            Assert.False(ValueHoldRule.Wait(0, 3, 0, 512), "nothing held");
            Assert.False(ValueHoldRule.Wait(6, 3, 512, 512), "the queue is full: the oldest goes early");
            Assert.True(ValueHoldRule.Wait(0, 3, 5, 512), "no order known: waits for the backlog as before");
        }

        [Test]
        public void ALongCatchUpNeverAppliesAValueBeforeAnOlderBatch()
        {
            // a client goes live with 2000 structure batches queued and applies one per frame while the host keeps
            // sending a batch and a value message per frame, for ten minutes: the backlog never clears, yet every value
            // is applied after every batch that preceded it and waits only as long as those take
            var acks = new ApplyAckTracker();
            var values = new AckHeldQueue<long>();
            values.Attach(acks);
            var structure = new Queue<long>();
            long appliedThrough = 0;
            int maxWaitFrames = 0;
            var arrivedAtFrame = new Dictionary<long, int>();
            for (int k = 0; k < 2000; k++)
            {
                long seq = acks.OnReceived();
                acks.Hold(seq);
                structure.Enqueue(seq);
            }
            for (int frame = 0; frame < 39000; frame++)
            {
                if (frame < 36000)
                {
                    long seq = acks.OnReceived();
                    acks.Hold(seq);
                    structure.Enqueue(seq);
                    // a value message (it names what the batches before it spawned)
                    values.Enqueue(seq, frame);
                    arrivedAtFrame[seq] = frame;
                }
                // the structure apply: one batch a frame
                if (structure.Count > 0)
                {
                    long seq = structure.Dequeue();
                    Assert.True(seq == appliedThrough + 1, "in order");
                    appliedThrough = seq;
                    acks.Release(seq);
                }
                // the values
                long oldest = structure.Count > 0 ? structure.Peek() : 0;
                while (!ValueHoldRule.Wait(values.OldestHold, oldest, values.Count, 1 << 20) && values.TryDequeue(out long newestBefore))
                {
                    Assert.True(appliedThrough >= newestBefore, "value applied before batch " + newestBefore + " (applied through " + appliedThrough + ")");
                    maxWaitFrames = System.Math.Max(maxWaitFrames, frame - arrivedAtFrame[newestBefore]);
                }
            }
            Assert.Equal(0, values.Count, "every value applied once the backlog drained");
            Assert.Equal(0, structure.Count);
            Assert.True(maxWaitFrames <= 2001, "a value waits only for the batches before it (" + maxWaitFrames + " frames)");
            Assert.True(maxWaitFrames >= 1999, "and for all of them (" + maxWaitFrames + " frames)");
            Assert.Equal(acks.Received, acks.AppliedThrough, "everything acknowledged");
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
