using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>w2/ids: per-peer epoch acknowledgements on the host and applied-message tracking on the client.</summary>
    public sealed class EpochAckTests
    {
        [Test]
        public void WithoutPeersEveryEpochCountsAsApplied()
        {
            var book = new EpochAckBook();
            Assert.Equal(0u, book.MinAckedEpoch);
            Assert.Equal(1u, book.Seal(_ => 0));
            Assert.Equal(2u, book.Seal(_ => 0));
            Assert.Equal(2u, book.MinAckedEpoch);
        }

        [Test]
        public void APeerHasAppliedAnEpochOnceItsCountReachesTheWatermark()
        {
            var book = new EpochAckBook();
            var sent = new Dictionary<byte, long> { [1] = 0 };
            book.AddPeer(1);
            sent[1] = 3;
            uint e1 = book.Seal(p => sent[p]); // watermark 3
            sent[1] = 5;
            uint e2 = book.Seal(p => sent[p]); // watermark 5
            Assert.Equal(0u, book.MinAckedEpoch);
            book.OnAck(1, 2);
            Assert.Equal(0u, book.AckedEpoch(1));
            book.OnAck(1, 3);
            Assert.Equal(e1, book.AckedEpoch(1));
            book.OnAck(1, 4);
            Assert.Equal(e1, book.MinAckedEpoch);
            book.OnAck(1, 5);
            Assert.Equal(e2, book.MinAckedEpoch);
            book.OnAck(1, 1); // stale, ignored
            Assert.Equal(e2, book.MinAckedEpoch);
        }

        [Test]
        public void EpochsWithNothingNewForAPeerNeedNoAck()
        {
            var book = new EpochAckBook();
            book.AddPeer(1);
            long sent = 2;
            book.Seal(_ => sent);
            book.OnAck(1, 2);
            Assert.Equal(1u, book.MinAckedEpoch);
            uint e2 = book.Seal(_ => sent); // nothing sent since the ack
            Assert.Equal(e2, book.AckedEpoch(1), "the peer has everything of epoch 2 already");
        }

        [Test]
        public void ANewPeerStartsCaughtUpAndTheSlowestPeerHoldsTheMinimum()
        {
            var book = new EpochAckBook();
            var sent = new Dictionary<byte, long> { [1] = 0, [2] = 0 };
            book.AddPeer(1);
            sent[1] = 1;
            book.Seal(p => sent[p]);
            sent[1] = 2;
            book.Seal(p => sent[p]);
            book.AddPeer(2); // joins after epoch 2: earlier epochs never went to it
            Assert.Equal(2u, book.AckedEpoch(2));
            sent[1] = 3;
            sent[2] = 1;
            uint e3 = book.Seal(p => sent[p]);
            book.OnAck(1, 3);
            Assert.Equal(e3, book.AckedEpoch(1));
            Assert.Equal(2u, book.MinAckedEpoch, "peer 2 has not applied epoch 3");
            book.OnAck(2, 1);
            Assert.Equal(e3, book.MinAckedEpoch);
            Assert.Equal(0u, book.MaxLag);
        }

        [Test]
        public void ALeavingPeerNoLongerHoldsAnythingBack()
        {
            var book = new EpochAckBook();
            book.AddPeer(1);
            book.AddPeer(2);
            book.Seal(_ => 4);
            book.OnAck(1, 4);
            Assert.Equal(0u, book.MinAckedEpoch);
            book.RemovePeer(2);
            Assert.Equal(1u, book.MinAckedEpoch);
            book.OnAck(2, 100); // unknown peer: ignored
            Assert.False(book.HasPeer(2));
        }

        [Test]
        public void TrackerAcknowledgesOnlyWhatWasAppliedInOrder()
        {
            var t = new EpochAckTracker();
            Assert.Equal(0L, t.AppliedThrough);
            long a = t.OnReceived();
            long b = t.OnReceived();
            long c = t.OnReceived();
            t.Hold(a);
            t.Hold(c);
            Assert.Equal(0L, t.AppliedThrough, "message 1 is still held");
            t.Release(c);
            Assert.Equal(0L, t.AppliedThrough, "applied out of order: 1 still held");
            t.Release(a);
            Assert.Equal(c, t.AppliedThrough, "message 2 was never held: applied at once");
            Assert.Equal(0, t.HeldCount);
            Assert.Equal(b + 1, t.AppliedThrough);
        }

        [Test]
        public void TwoConsumersCanHoldTheSameMessage()
        {
            var t = new EpochAckTracker();
            long a = t.OnReceived();
            t.Hold(a);
            t.Hold(a);
            t.Release(a);
            Assert.Equal(0L, t.AppliedThrough);
            t.Release(a);
            Assert.Equal(1L, t.AppliedThrough);
            t.Release(a); // unknown now: ignored
            Assert.Equal(1L, t.AppliedThrough);
        }

        [Test]
        public void TrackerRejectsHoldsOutOfOrderOrInTheFuture()
        {
            var t = new EpochAckTracker();
            long a = t.OnReceived();
            long b = t.OnReceived();
            t.Hold(b);
            Assert.Throws<ArgumentException>(() => t.Hold(a));
            Assert.Throws<ArgumentOutOfRangeException>(() => t.Hold(b + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => t.Hold(0));
        }

        [Test]
        public void TrackerStaysSmallUnderALongRandomSession()
        {
            var t = new EpochAckTracker();
            var random = new DeterministicRandom(7);
            var held = new List<long>();
            long expected = 0;
            for (int i = 0; i < 50_000; i++)
            {
                long seq = t.OnReceived();
                if (random.Next(4) != 0)
                {
                    t.Hold(seq);
                    held.Add(seq);
                }
                while (held.Count > 0 && random.Next(3) == 0)
                {
                    int k = random.Next(Math.Min(4, held.Count)); // mostly in order, sometimes not
                    t.Release(held[k]);
                    held.RemoveAt(k);
                }
                expected = held.Count == 0 ? seq : MinOf(held) - 1;
                Assert.Equal(expected, t.AppliedThrough);
            }
            foreach (long s in held) t.Release(s);
            Assert.Equal(t.Received, t.AppliedThrough);
        }

        private static long MinOf(List<long> list)
        {
            long min = long.MaxValue;
            foreach (long v in list) min = Math.Min(min, v);
            return min;
        }

        [Test]
        public void AckMessageRoundTripsAndRejectsGarbage()
        {
            var w = new ByteWriter(16);
            EpochAckMessage.Write(w, 123_456_789_012);
            Assert.Equal(123_456_789_012L, EpochAckMessage.Read(w.Buffer, 0, w.Length));
            Assert.Throws<ArgumentOutOfRangeException>(() => EpochAckMessage.Write(new ByteWriter(4), -1));
            Assert.Throws<DecodeException>(() => EpochAckMessage.Read(new byte[] { 0x80 }, 0, 1));
            Assert.Throws<DecodeException>(() => EpochAckMessage.Read(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x01 }, 0, 10));
        }
    }
}
