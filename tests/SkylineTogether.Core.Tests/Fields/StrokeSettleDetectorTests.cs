using System;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Fields
{
    /// <summary>StrokeSettleDetector: 200 ms settle timing, union rectangles, forced flushes and argument checks.</summary>
    public sealed class StrokeSettleDetectorTests
    {
        private static int[] Take(StrokeSettleDetector detector, double now)
        {
            if (!detector.TryTakeSettled(now, out int minX, out int minY, out int maxX, out int maxY))
            {
                Assert.True(maxX < minX && maxY < minY, "an empty rectangle when nothing settled");
                return null;
            }
            return new[] { minX, minY, maxX, maxY };
        }

        [Test]
        public void SettlesTwoHundredMillisecondsAfterTheLastStamp()
        {
            var detector = new StrokeSettleDetector();
            Assert.Equal(0.2, detector.SettleSeconds);
            Assert.Null(Take(detector, 0), "nothing pending");
            detector.OnStamp(10, 10, 20, 20, 1.0);
            detector.OnStamp(15, 5, 25, 18, 1.1);
            detector.OnStamp(12, 12, 13, 13, 1.25);
            Assert.True(detector.HasPending);
            Assert.Equal(1.25, detector.LastStampSeconds);
            Assert.Null(Take(detector, 1.3), "50 ms after the last stamp");
            Assert.Null(Take(detector, 1.44), "190 ms after the last stamp");
            Assert.SequenceEqual(new[] { 10, 5, 25, 20 }, Take(detector, 1.45), "the union of all stamps after exactly 200 ms");
            Assert.False(detector.HasPending);
            Assert.True(double.IsNaN(detector.LastStampSeconds));
            Assert.Null(Take(detector, 2.0), "taken only once");
        }

        [Test]
        public void ExactDecimalTimesSettleDespiteBinaryRounding()
        {
            // 0.3 - 0.1 is 0.19999999999999998 in doubles; a stroke checked exactly 200 ms later has settled
            var detector = new StrokeSettleDetector(0.2);
            detector.OnStamp(0, 0, 1, 1, 0.1);
            Assert.NotNull(Take(detector, 0.3));
        }

        [Test]
        public void EachSettledStrokeStartsANewRectangle()
        {
            var detector = new StrokeSettleDetector(0.2);
            detector.OnStamp(0, 0, 63, 63, 0);
            Assert.SequenceEqual(new[] { 0, 0, 63, 63 }, Take(detector, 0.5));
            detector.OnStamp(1000, 2000, 1010, 2010, 0.6);
            Assert.Null(Take(detector, 0.7));
            Assert.SequenceEqual(new[] { 1000, 2000, 1010, 2010 }, Take(detector, 0.8), "the old rectangle is not part of the new one");
        }

        [Test]
        public void EmptyRectanglesAreIgnored()
        {
            var detector = new StrokeSettleDetector(0.2);
            detector.OnStamp(10, 10, 9, 20, 0);
            detector.OnStamp(10, 10, 20, 9, 0);
            Assert.False(detector.HasPending, "an empty rectangle starts no stroke");
            detector.OnStamp(5, 5, 6, 6, 1.0);
            detector.OnStamp(50, 50, 40, 60, 1.15);   // empty: neither grows the rectangle nor delays the settle
            Assert.SequenceEqual(new[] { 5, 5, 6, 6 }, Take(detector, 1.2));
        }

        [Test]
        public void StampsWithEarlierTimesDoNotMoveTheSettleBack()
        {
            var detector = new StrokeSettleDetector(0.2);
            detector.OnStamp(0, 0, 1, 1, 5.0);
            detector.OnStamp(2, 2, 3, 3, 4.0);
            Assert.Equal(5.0, detector.LastStampSeconds);
            Assert.Null(Take(detector, 5.1));
            Assert.SequenceEqual(new[] { 0, 0, 3, 3 }, Take(detector, 5.2));
        }

        [Test]
        public void MaxPendingFlushesALongContinuousStroke()
        {
            var detector = new StrokeSettleDetector(0.2) { MaxPendingSeconds = 2.0 };
            for (int frame = 0; frame <= 120; frame++)
            {
                double now = frame / 60.0;
                detector.OnStamp(frame, 0, frame + 4, 4, now);
                int[] taken = Take(detector, now);
                if (frame < 120)
                {
                    Assert.Null(taken, "frame " + frame);
                }
                else
                {
                    Assert.SequenceEqual(new[] { 0, 0, 124, 4 }, taken, "two seconds into the stroke");
                }
            }
            detector.OnStamp(500, 0, 504, 4, 2.02);
            Assert.SequenceEqual(new[] { 500, 0, 504, 4 }, Take(detector, 2.3), "the rest of the stroke settles normally");
            Assert.Throws<ArgumentOutOfRangeException>(() => detector.MaxPendingSeconds = 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => detector.MaxPendingSeconds = double.NaN);
            Assert.True(double.IsPositiveInfinity(new StrokeSettleDetector().MaxPendingSeconds), "off by default");
        }

        [Test]
        public void ZeroSettleTimeAndReset()
        {
            var immediate = new StrokeSettleDetector(0);
            immediate.OnStamp(1, 2, 3, 4, 10);
            Assert.SequenceEqual(new[] { 1, 2, 3, 4 }, Take(immediate, 10));
            var detector = new StrokeSettleDetector();
            detector.OnStamp(1, 2, 3, 4, 10);
            detector.Reset();
            Assert.False(detector.HasPending);
            Assert.Null(Take(detector, 100));
        }

        [Test]
        public void ArgumentsAreValidated()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new StrokeSettleDetector(-0.1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new StrokeSettleDetector(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new StrokeSettleDetector(double.PositiveInfinity));
            var detector = new StrokeSettleDetector();
            Assert.Throws<ArgumentOutOfRangeException>(() => detector.OnStamp(0, 0, 1, 1, double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => detector.TryTakeSettled(double.PositiveInfinity, out _, out _, out _, out _));
        }
    }
}
