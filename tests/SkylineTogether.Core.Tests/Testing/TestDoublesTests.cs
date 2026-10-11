using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Testing
{
    /// <summary>The test doubles of SkylineTogether.Core.Testing.</summary>
    public sealed class TestDoublesTests
    {
        [Test]
        public void NullLogDiscardsEverything()
        {
            Assert.Same(NullLog.Instance, NullLog.Instance);
            foreach (LogLevel level in Enum.GetValues(typeof(LogLevel))) Assert.False(NullLog.Instance.IsEnabled(level));
            NullLog.Instance.Write(LogLevel.Critical, "x", "y", new Exception());
        }

        [Test]
        public void ListLogRecordsLinesAndCounts()
        {
            var log = new ListLog();
            Assert.True(log.IsEnabled(LogLevel.Trace));
            log.Write(LogLevel.Debug, "net", "connected");
            log.Write(LogLevel.Error, "apply", "failed", new InvalidOperationException("boom"));
            log.Write(LogLevel.Critical, null, null);
            Assert.SequenceEqual(new[] { "Debug [net] connected", "Error [apply] failed | InvalidOperationException: boom", "Critical [] " }, log.Lines);
            Assert.Equal(3, log.Count(LogLevel.Trace));
            Assert.Equal(2, log.Count(LogLevel.Error));
            Assert.Equal(1, log.Count(LogLevel.Critical));
            Assert.True(log.Contains("boom"));
            Assert.False(log.Contains("nothing"));
            IReadOnlyList<string> snapshot = log.Lines;
            log.Clear();
            Assert.Equal(0, log.Lines.Count);
            Assert.Equal(3, snapshot.Count, "Lines returns a snapshot");
        }

        [Test]
        public void ListLogHonoursItsMinimumAndIsThreadSafe()
        {
            var log = new ListLog(LogLevel.Warn);
            Assert.False(log.IsEnabled(LogLevel.Info));
            log.Write(LogLevel.Info, "x", "ignored");
            Assert.Equal(0, log.Lines.Count);
            Parallel.For(0, 8, i =>
            {
                for (int n = 0; n < 1000; n++) log.Write(LogLevel.Warn, "t", "message");
            });
            Assert.Equal(8000, log.Lines.Count);
            Assert.Equal(8000, log.Count(LogLevel.Warn));
        }

        [Test]
        public void ManualClockMovesOnlyForward()
        {
            var clock = new ManualClock(1.5);
            Assert.Equal(15000000L, clock.Ticks);
            Assert.Equal(1.5, clock.Seconds);
            clock.Advance(0.25);
            Assert.Equal(1.75, clock.Seconds);
            Assert.Equal(17500000L, clock.Ticks);
            clock.Advance(0);
            clock.Set(10);
            Assert.Equal(10.0, clock.Seconds);
            clock.Set(10);
            Assert.Throws<ArgumentOutOfRangeException>(() => clock.Set(9.999));
            Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(-0.1));
            Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ManualClock(double.PositiveInfinity));
            Assert.Equal(0L, new ManualClock().Ticks);
        }

        [Test]
        public void ManualClockIsThreadSafe()
        {
            var clock = new ManualClock();
            Parallel.For(0, 8, i =>
            {
                for (int n = 0; n < 10000; n++) clock.Advance(0.0001);
            });
            Assert.Equal(80000L * 1000, clock.Ticks);
        }

        [Test]
        public void StopwatchClockIsMonotonicAndConsistent()
        {
            var clock = new StopwatchClock();
            long previous = clock.Ticks;
            Assert.True(previous >= 0);
            for (int i = 0; i < 1000; i++)
            {
                long now = clock.Ticks;
                Assert.True(now >= previous, "ticks never go backwards");
                previous = now;
            }
            Thread.Sleep(20);
            double seconds = clock.Seconds;
            Assert.True(seconds >= 0.015, "the clock advances with real time");
            Assert.Near(seconds, clock.Ticks / 1e7, 0.05);
        }

        [Test]
        public void DeterministicRandomIsReproducible()
        {
            var a = new DeterministicRandom(42);
            var b = new DeterministicRandom(42);
            for (int i = 0; i < 1000; i++) Assert.Equal(a.NextULong(), b.NextULong());
            Assert.NotEqual(new DeterministicRandom(1).NextULong(), new DeterministicRandom(2).NextULong());
        }

        [Test]
        public void DeterministicRandomGoldenSequence()
        {
            // pinned so that seeded tests and fuzzers produce the same cases on every runtime and in every build
            var random = new DeterministicRandom(42);
            Assert.Equal(0x15780B2E0C2EC716UL, random.NextULong());
            Assert.Equal(0x6104D9866D113A7EUL, random.NextULong());
            Assert.Equal(0xAE17533239E499A1UL, random.NextULong());
            var zero = new DeterministicRandom(0);
            Assert.Equal(0x99EC5F36CB75F2B4UL, zero.NextULong());
            Assert.Equal(0xBF6E1F78u, zero.NextUInt());
            Assert.Equal(10, zero.Next(100));
            Assert.Equal(0.4165890778296456, zero.NextDouble());
        }

        [Test]
        public void BoundedValuesStayInRangeAndCoverIt()
        {
            var random = new DeterministicRandom(9);
            var counts = new int[10];
            for (int i = 0; i < 100000; i++)
            {
                int v = random.Next(10);
                Assert.True(v >= 0 && v < 10);
                counts[v]++;
            }
            foreach (int count in counts) Assert.InRange(count, 9000, 11000);
            for (int i = 0; i < 1000; i++)
            {
                int v = random.Next(-5, 5);
                Assert.True(v >= -5 && v < 5);
                double d = random.NextDouble();
                Assert.True(d >= 0 && d < 1);
            }
            Assert.Equal(7, random.Next(7, 7));
            int extreme = random.Next(int.MinValue, int.MaxValue);
            Assert.True(extreme < int.MaxValue);
            Assert.Equal(0, random.Next(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => random.Next(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => random.Next(5, 4));
        }

        [Test]
        public void NextBytesFillsTheRange()
        {
            var random = new DeterministicRandom(3);
            var buffer = new byte[37];
            random.NextBytes(buffer, 2, 33);
            Assert.Equal((byte)0, buffer[0]);
            Assert.Equal((byte)0, buffer[1]);
            Assert.Equal((byte)0, buffer[35]);
            Assert.Equal((byte)0, buffer[36]);
            int nonZero = 0;
            for (int i = 2; i < 35; i++) if (buffer[i] != 0) nonZero++;
            Assert.True(nonZero > 25);
            Assert.Throws<ArgumentOutOfRangeException>(() => random.NextBytes(buffer, 30, 10));
            Assert.Throws<ArgumentNullException>(() => random.NextBytes(null, 0, 0));
            bool sawTrue = false, sawFalse = false;
            for (int i = 0; i < 100; i++)
            {
                if (random.NextBool()) sawTrue = true;
                else sawFalse = true;
            }
            Assert.True(sawTrue && sawFalse);
        }
    }
}
