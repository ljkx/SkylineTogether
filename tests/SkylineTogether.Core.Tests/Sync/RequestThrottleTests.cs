using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>polish: limits of request-only batches (id grants, full re-sends) and the client's id re-request backoff.</summary>
    public sealed class RequestThrottleTests
    {
        [Test]
        public void IdGrantsAreRateLimitedPerPeer()
        {
            var t = new RequestThrottle(idsPerSecond: 0.5, idBurst: 2);
            Assert.True(t.TryGrantIds(1, 0, 0));
            Assert.True(t.TryGrantIds(1, 0, 0));
            Assert.False(t.TryGrantIds(1, 0, 0), "burst used up");
            Assert.True(t.TryGrantIds(2, 0, 0), "other peers have their own bucket");
            Assert.False(t.TryGrantIds(1, 1.9, 0));
            Assert.True(t.TryGrantIds(1, 2.0, 0), "one token every two seconds");
        }

        [Test]
        public void NoGrantWhileThePeerHoldsManyUnusedIds()
        {
            var t = new RequestThrottle();
            Assert.False(t.TryGrantIds(1, 0, RequestThrottle.MaxUnusedIds));
            Assert.True(t.TryGrantIds(1, 0, RequestThrottle.MaxUnusedIds - 1));
        }

        [Test]
        public void RefusedResendsAreDeferredAndCoalesced()
        {
            var t = new RequestThrottle(resendsPerSecond: 0.2, resendBurst: 1);
            Assert.True(t.TryResend(1, true, 0));
            Assert.False(t.TryResend(1, true, 1));
            Assert.True(t.HasDeferred(1));
            Assert.False(t.TryResend(1, false, 2), "a full re-send joins the deferred one");
            Assert.False(t.TakeDeferred(1, 4.9, out _));
            Assert.True(t.TakeDeferred(1, 5.0, out bool routesOnly));
            Assert.False(routesOnly, "the full re-send covers the lines-only one");
            Assert.False(t.HasDeferred(1));
            Assert.False(t.TakeDeferred(1, 100, out _), "nothing left");
            t.TryResend(1, true, 100);
            t.TryResend(1, true, 100);
            t.Forget(1);
            Assert.False(t.HasDeferred(1));
        }

        [Test]
        public void GrantBookCountsUnusedIds()
        {
            var book = new IdGrantBook();
            Assert.Equal(0L, book.UnusedCount(3));
            book.Add(3, 100, 70);
            book.Add(3, 500, 10);
            Assert.Equal(80L, book.UnusedCount(3));
            Assert.True(book.TryConsume(3, 100));
            Assert.True(book.TryConsume(3, 169));
            Assert.True(book.TryConsume(3, 505));
            Assert.False(book.TryConsume(3, 505));
            Assert.Equal(77L, book.UnusedCount(3));
        }

        [Test]
        public void ClientRepeatsAnUnansweredIdRequestWithBackoff()
        {
            var pool = new ClientIdPool();
            Assert.True(pool.ShouldRequestMore(10));
            Assert.False(pool.ShouldRequestMore(12.9));
            Assert.True(pool.ShouldRequestMore(13), "no answer after 3 s: ask again");
            Assert.False(pool.ShouldRequestMore(18.9));
            Assert.True(pool.ShouldRequestMore(19), "then after 6 s");
            Assert.False(pool.ShouldRequestMore(30.9));
            Assert.True(pool.ShouldRequestMore(31), "then after 12 s");
            Assert.True(pool.ShouldRequestMore(55));
            Assert.False(pool.ShouldRequestMore(84.9));
            Assert.True(pool.ShouldRequestMore(85), "capped at 30 s");
            pool.Add(1, ClientIdPool.LowWater + 10);
            Assert.False(pool.ShouldRequestMore(1000), "answered: enough ids");
            for (int i = 0; i < 20; i++) pool.TryTake(out _);
            Assert.True(pool.ShouldRequestMore(1000));
            Assert.False(pool.ShouldRequestMore(1002.9), "the backoff starts again at 3 s");
            Assert.True(pool.ShouldRequestMore(1003));
        }
    }
}
