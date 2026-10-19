using System;
using System.Net;
using System.Threading;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Transport
{
    /// <summary>A typed host name is resolved on a worker thread; the main thread only polls.</summary>
    public sealed class HostLookupTests
    {
        private static void WaitFor(HostLookup l)
        {
            for (int i = 0; i < 500 && !l.IsDone; i++) Thread.Sleep(10);
            Assert.True(l.IsDone);
        }

        [Test]
        public void AnIpLiteralNeedsNoLookup()
        {
            var l = new HostLookup(" 203.0.113.7 ", _ => throw new InvalidOperationException("no DNS for a literal"));
            Assert.True(l.IsDone);
            Assert.Equal("203.0.113.7", l.Result(out string error));
            Assert.Null(error);
            Assert.True(HostLookup.IsLiteral("[2001:db8::1]", out _));
            Assert.False(HostLookup.IsLiteral("my.host.example", out _));
            Assert.Throws<ArgumentException>(() => new HostLookup("  "));
        }

        [Test]
        public void ANameIsResolvedOffTheCallersThreadPreferringIPv4()
        {
            var release = new ManualResetEventSlim(false);
            int caller = Thread.CurrentThread.ManagedThreadId, resolver = caller;
            var l = new HostLookup("host.example", name =>
            {
                resolver = Thread.CurrentThread.ManagedThreadId;
                release.Wait(5000);
                return new[] { IPAddress.Parse("2001:db8::5"), IPAddress.Parse("198.51.100.9") };
            });
            Assert.False(l.IsDone, "the caller does not wait for the lookup");
            Assert.Null(l.Result(out string pending));
            Assert.Equal("still resolving", pending);
            release.Set();
            WaitFor(l);
            Assert.NotEqual(caller, resolver, "resolved on another thread");
            Assert.Equal("198.51.100.9", l.Result(out _));
        }

        [Test]
        public void AFailedLookupSaysWhy()
        {
            var l = new HostLookup("nowhere.example", _ => throw new System.Net.Sockets.SocketException(11001));
            WaitFor(l);
            Assert.Null(l.Result(out string error));
            Assert.Contains("could not resolve \"nowhere.example\"", error);
            var empty = new HostLookup("empty.example", _ => new IPAddress[0]);
            WaitFor(empty);
            Assert.Null(empty.Result(out error));
            Assert.NotNull(error);
        }
    }
}
