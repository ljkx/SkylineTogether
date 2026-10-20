using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// A key derivation scheduler the test drives: <see cref="Schedule"/> only queues the work, <see cref="RunPending"/>
    /// runs each queued derivation on a thread of its own and completes its task. No wall-clock waits.
    /// </summary>
    internal sealed class ManualDeriveScheduler
    {
        private readonly List<(Func<byte[]> Work, TaskCompletionSource<byte[]> Done)> _pending = new List<(Func<byte[]>, TaskCompletionSource<byte[]>)>();

        /// <summary>Derivations started so far.</summary>
        public int Started { get; private set; }

        /// <summary>Managed thread ids the derivations ran on.</summary>
        public readonly List<int> WorkerThreads = new List<int>();

        public int Pending => _pending.Count;

        public Task<byte[]> Schedule(Func<byte[]> work)
        {
            Started++;
            var done = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add((work, done));
            return done.Task;
        }

        public void RunPending()
        {
            var batch = _pending.ToArray();
            _pending.Clear();
            foreach (var (work, done) in batch)
            {
                byte[] result = null;
                Exception error = null;
                int threadId = 0;
                var thread = new Thread(() =>
                {
                    threadId = Environment.CurrentManagedThreadId;
                    try
                    {
                        result = work();
                    }
                    catch (Exception e)
                    {
                        error = e;
                    }
                });
                thread.Start();
                thread.Join();
                WorkerThreads.Add(threadId);
                if (error != null) done.SetException(error);
                else done.SetResult(result);
            }
        }
    }

    /// <summary>
    /// <see cref="PasswordKeyCache"/>: a new password gives a new key in both modes (a host that changes its password to
    /// lock someone out must not keep accepting the old one), and the background mode really derives off the caller's
    /// thread.
    /// </summary>
    public sealed class PasswordKeyCacheTests
    {
        private static readonly byte[] Salt = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

        [Test]
        public void ANewPasswordGivesANewKeyOnTheCallersThread()
        {
            var cache = new PasswordKeyCache(Salt, background: false);
            byte[] a = cache.TryGet("alpha");
            Assert.SequenceEqual(SessionCrypto.DeriveKey("alpha", Salt), a);
            Assert.Same(a, cache.TryGet("alpha"), "the same password is derived once");
            byte[] b = cache.TryGet("bravo");
            Assert.SequenceEqual(SessionCrypto.DeriveKey("bravo", Salt), b, "the second password gives the second key");
            Assert.False(Equal(a, b));
            Assert.SequenceEqual(SessionCrypto.DeriveKey("alpha", Salt), cache.TryGet("alpha"), "and back");
            Assert.SequenceEqual(SessionCrypto.DeriveKey("", Salt), cache.TryGet(null), "null is the empty password");
            Assert.False(cache.Deriving);
        }

        [Test]
        public void ANewPasswordGivesANewKeyInTheBackground()
        {
            var scheduler = new ManualDeriveScheduler();
            var cache = new PasswordKeyCache(Salt, true, scheduler.Schedule);
            Assert.Null(cache.TryGet("alpha"), "nothing before the derivation ran");
            Assert.True(cache.Deriving);
            Assert.Null(cache.TryGet("alpha"), "asking again does not start a second derivation");
            Assert.Equal(1, scheduler.Started);
            scheduler.RunPending();
            byte[] a = cache.TryGet("alpha");
            Assert.SequenceEqual(SessionCrypto.DeriveKey("alpha", Salt), a);
            Assert.False(cache.Deriving);

            Assert.Null(cache.TryGet("bravo"), "a new password is derived, not answered with the old key");
            Assert.Equal(2, scheduler.Started);
            Assert.Same(a, cache.TryGet("alpha"), "the old key stays valid for the old password meanwhile");
            scheduler.RunPending();
            Assert.SequenceEqual(SessionCrypto.DeriveKey("bravo", Salt), cache.TryGet("bravo"));
            Assert.Equal(2, scheduler.Started);
        }

        [Test]
        public void APasswordChangedDuringTheDerivationRestartsIt()
        {
            var scheduler = new ManualDeriveScheduler();
            var cache = new PasswordKeyCache(Salt, true, scheduler.Schedule);
            Assert.Null(cache.TryGet("alpha"));
            Assert.Null(cache.TryGet("bravo"));
            Assert.Equal(2, scheduler.Started);
            scheduler.RunPending();
            Assert.SequenceEqual(SessionCrypto.DeriveKey("bravo", Salt), cache.TryGet("bravo"), "the key of the current password, not of the abandoned one");
        }

        [Test]
        public void AFailedBackgroundDerivationFallsBackToTheCallersThread()
        {
            var cache = new PasswordKeyCache(Salt, true, work => Task.FromException<byte[]>(new InvalidOperationException("worker died")));
            Assert.SequenceEqual(SessionCrypto.DeriveKey("alpha", Salt), cache.TryGet("alpha"));
        }

        [Test(TimeoutSeconds = 30)]
        public void TheDefaultSchedulerDerivesOnTheThreadPool()
        {
            var cache = new PasswordKeyCache(Salt, true);
            byte[] key = cache.TryGet("alpha");
            if (key == null)
            {
                Assert.True(SpinWait.SpinUntil(() => !cache.Deriving, TimeSpan.FromSeconds(20)), "the derivation finished");
                key = cache.TryGet("alpha");
            }
            Assert.SequenceEqual(SessionCrypto.DeriveKey("alpha", Salt), key);
        }

        [Test(TimeoutSeconds = 30)]
        public void ABackgroundDerivationRunsOffTheCallersThread()
        {
            // a thread-pool task held at a gate, so the first call cannot see a finished derivation
            int callerThread = Environment.CurrentManagedThreadId;
            int workerThread = 0;
            using (var gate = new ManualResetEventSlim(false))
            {
                Task<byte[]> started = null;
                var cache = new PasswordKeyCache(Salt, true, work => started = Task.Run(() =>
                {
                    gate.Wait();
                    workerThread = Environment.CurrentManagedThreadId;
                    return work();
                }));
                Assert.Null(cache.TryGet("alpha"), "the caller does not wait for the derivation");
                Assert.True(cache.Deriving);
                gate.Set();
                Assert.True(started.Wait(TimeSpan.FromSeconds(20)), "the derivation finished");
                Assert.SequenceEqual(SessionCrypto.DeriveKey("alpha", Salt), cache.TryGet("alpha"));
            }
            Assert.NotEqual(0, workerThread);
            Assert.NotEqual(callerThread, workerThread, "derived on a worker thread");
        }

        [Test]
        public void ANewHostPasswordAppliesToTheNextJoinerOnTheCallersThread() => HostPasswordChange(background: false);

        [Test]
        public void ANewHostPasswordAppliesToTheNextJoinerInTheBackground() => HostPasswordChange(background: true);

        /// <summary>
        /// The host reads its options live (the game's settings panel sets <c>Options.Password</c>): after a change the old
        /// password is refused and the new one admitted; players already in stay.
        /// </summary>
        private static void HostPasswordChange(bool background)
        {
            var hostScheduler = new ManualDeriveScheduler();
            var options = new HostOptions { HostName = "Hosty", Password = "alpha", DeriveKeysInBackground = background };
            if (background) options.KeyDerivationScheduler = hostScheduler.Schedule;
            var rig = new SessionRig(options);
            var (first, _, _) = rig.AddClient("First", "alpha");
            RunUntilSettled(rig, hostScheduler);
            Assert.Equal(ClientState.Live, first.State, first.EndReason);

            rig.Host.Options.Password = "bravo";
            var (old, _, _) = rig.AddClient("Old", "alpha");
            var (fresh, _, _) = rig.AddClient("Fresh", "bravo");
            RunUntilSettled(rig, hostScheduler);
            Assert.Equal(ClientState.Ended, old.State, "the old password no longer works");
            Assert.Equal(RefuseReason.BadPassword, old.RefusedFor, old.EndReason);
            Assert.Equal(ClientState.Live, fresh.State, fresh.EndReason);
            Assert.Equal(ClientState.Live, first.State, "a player admitted with the old password stays");
            if (background)
            {
                Assert.Equal(2, hostScheduler.Started, "one derivation per password");
                foreach (int id in hostScheduler.WorkerThreads) Assert.NotEqual(Environment.CurrentManagedThreadId, id);
            }
        }

        private static void RunUntilSettled(SessionRig rig, ManualDeriveScheduler scheduler)
        {
            for (int i = 0; i < 600; i++)
            {
                rig.Run(1);
                scheduler.RunPending();
            }
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
