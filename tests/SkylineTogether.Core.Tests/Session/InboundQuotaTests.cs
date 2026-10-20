using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// The host's per-peer inbox bound (<see cref="InboundQuota"/>) counts what is waiting, not what was ever sent: an
    /// honest builder whose proposals, strokes and patches are applied as they come is never refused, however much it
    /// sends over a session.
    /// </summary>
    public sealed class InboundQuotaTests
    {
        [Test]
        public void ReleasedBytesAreFreedSoASessionCanSendManyTimesTheLimit()
        {
            var q = new InboundQuota(4, 1000);
            var peer = new PeerId(3);
            long sent = 0;
            for (int i = 0; sent < 10 * q.MaxBytes; i++)
            {
                int bytes = 150 + i % 7 * 50; // 150..450 bytes
                Assert.True(q.TryAdmit(peer, bytes), "message " + i + " after " + sent + " bytes in total");
                Assert.True(q.TryAdmit(peer, 100), "a second one waits beside it (message " + i + ")");
                q.Release(peer, bytes);
                q.Release(peer, 100);
                sent += bytes + 100;
                Assert.Equal(0L, q.PendingBytes(peer), "nothing waits after message " + i);
                Assert.Equal(0, q.Pending(peer));
            }
            Assert.False(q.TakeOverflow(peer), "never over its limit");
            Assert.Equal(0L, q.Dropped);
        }

        [Test]
        public void WaitingBytesAreBoundedAndFreedExactly()
        {
            var q = new InboundQuota(100, 1000);
            var peer = new PeerId(4);
            Assert.True(q.TryAdmit(peer, 600));
            Assert.True(q.TryAdmit(peer, 400), "exactly the limit");
            Assert.Equal(1000L, q.PendingBytes(peer));
            Assert.False(q.TryAdmit(peer, 1), "one byte over the limit");
            q.Release(peer, 600);
            Assert.Equal(400L, q.PendingBytes(peer), "the released message's bytes, not more and not less");
            Assert.True(q.TryAdmit(peer, 600), "room for the released bytes again");
            Assert.False(q.TryAdmit(peer, 1));
        }
    }
}
