using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Motion;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Motion
{
    /// <summary>
    /// The motion codec beyond straight constant-velocity motion: curves (rotation and velocity change every frame),
    /// climbs, delta-width escalation up to the absolute escape, reference ages above 1, flags, and the animation
    /// state and timer modes. Every decoded frame is compared with what was encoded.
    /// </summary>
    public sealed class MotionCodecCurveTests
    {
        private sealed class References : IMotionReferences
        {
            public readonly Dictionary<uint, MotionFrame> Frames = new Dictionary<uint, MotionFrame>();

            public bool TryGetReference(ushort interestSlot, byte assignGeneration, uint referenceTick, out MotionFrame frame) => Frames.TryGetValue(referenceTick, out frame);
        }

        private static MotionFrame OnCircle(double t, double radius, double climb)
        {
            double angle = t * 0.02, speed = radius * 0.02 * 60;
            double heading = angle + Math.PI / 2;
            return new MotionFrame
            {
                X = (float)(radius * Math.Cos(angle)), Y = (float)(30 + climb * t), Z = (float)(radius * Math.Sin(angle)),
                VX = (float)(speed * Math.Cos(heading)), VY = (float)(climb * 60), VZ = (float)(speed * Math.Sin(heading)),
                QX = 0, QY = (float)Math.Sin(-heading / 2), QZ = 0, QW = (float)Math.Cos(-heading / 2),
                Flags = 0x5,
            };
        }

        private static void AssertClose(in MotionFrame want, in MotionFrame got, string what)
        {
            double tolerance = MotionCodec.PositionQuantum;
            Assert.Near(want.X, got.X, tolerance, what + " x");
            Assert.Near(want.Y, got.Y, tolerance, what + " y");
            Assert.Near(want.Z, got.Z, tolerance, what + " z");
            double dot = Math.Abs(want.QX * got.QX + want.QY * got.QY + want.QZ * got.QZ + want.QW * got.QW);
            Assert.True(dot > 0.9995, what + ": rotation off (dot " + dot + ")");
            double speed = Math.Sqrt(want.VX * want.VX + want.VY * want.VY + want.VZ * want.VZ);
            double dv = Math.Sqrt(Sq(want.VX - got.VX) + Sq(want.VY - got.VY) + Sq(want.VZ - got.VZ));
            Assert.True(dv <= 0.05 + 0.15 * speed + 1.0 / 8, what + ": velocity off by " + dv + " at " + speed + " m/s");
            Assert.Equal(want.Flags, got.Flags, what + " flags");
            Assert.Equal(want.State, got.State, what + " state");
            Assert.Equal(want.Activity, got.Activity, what + " activity");
            Assert.Equal(want.StateTimer, got.StateTimer, what + " timer");
        }

        private static double Sq(double v) => v * v;

        private static MotionFrame Decode(ByteWriter w, uint tick, References refs, out QbHeader header)
        {
            var r = new ByteReader(w.Buffer, 0, w.Length);
            Assert.True(MotionCodec.TryReadQB(r, tick, refs, out header, out MotionFrame frame), "decoded");
            Assert.True(r.IsAtEnd, "the record is consumed exactly");
            return frame;
        }

        [Test]
        public void ACurvingClimbingMoverRoundTripsWithEveryReferenceAge()
        {
            for (byte age = 1; age <= 7; age++)
            {
                var refs = new References();
                uint tick = 1000;
                var w = new ByteWriter();
                MotionCodec.WriteQBKeyframe(w, 4, 2, OnCircle(0, 80, 0.05), false);
                refs.Frames[tick] = Decode(w, tick, refs, out QbHeader h);
                Assert.True(h.Keyframe);
                for (int step = 1; step <= 21; step++)
                {
                    uint t = tick + (uint)(16 * step);
                    if (step % age != 0) continue;
                    MotionFrame want = OnCircle(16 * step, 80, 0.05);
                    MotionFrame reference = refs.Frames[t - (uint)(16 * age)];
                    w.Reset();
                    MotionCodec.WriteQBDelta(w, 4, 2, age, want, reference, false);
                    MotionFrame got = Decode(w, t, refs, out h);
                    Assert.False(h.Keyframe);
                    Assert.Equal(age, h.RefAge);
                    Assert.Equal((byte)2, h.AssignGeneration);
                    AssertClose(want, got, "age " + age + " step " + step);
                    refs.Frames[t] = got; // the next delta refers to what the receiver decoded
                }
            }
        }

        [Test]
        public void JumpsWidenTheDeltaUpToTheAbsoluteEscape()
        {
            var refs = new References();
            var start = new MotionFrame { X = 100, Y = 20, Z = -50, QW = 1 };
            var w = new ByteWriter();
            MotionCodec.WriteQBKeyframe(w, 1, 0, start, true);
            refs.Frames[0] = Decode(w, 0, refs, out _);
            int lastLength = 0;
            uint tick = 0;
            // each jump needs a wider field: 1 m, 30 m, 600 m, 3000 m (beyond the widest delta, about 1 km: the absolute escape)
            foreach (float jump in new[] { 1f, 30f, 600f, 3000f })
            {
                MotionFrame reference = refs.Frames[tick];
                MotionFrame want = reference;
                want.X += jump;
                want.Z -= jump / 2;
                want.Y += jump / 10;
                tick += 16;
                w.Reset();
                bool escaped = MotionCodec.WriteDeltaRecord(w, 1, 0, 1, want, reference, true);
                Assert.Equal(jump > 2000, escaped, "absolute escape for " + jump + " m");
                Assert.True(w.Length >= lastLength, "a wider jump never takes fewer bytes");
                lastLength = w.Length;
                MotionFrame got = Decode(w, tick, refs, out _);
                AssertClose(want, got, "jump " + jump);
                refs.Frames[tick] = got;
            }
        }

        [Test]
        public void StateAndTimerModesRoundTrip()
        {
            var refs = new References();
            var w = new ByteWriter();
            var pedestrian = new MotionFrame { X = 5, Y = 1, Z = 5, QW = 1, State = 2, Activity = 9, StateTimer = 100, Flags = 1u << 17 };
            MotionCodec.WriteQBKeyframe(w, 7, 15, pedestrian, false);
            refs.Frames[16] = Decode(w, 16, refs, out _);
            AssertClose(pedestrian, refs.Frames[16], "keyframe");
            var cases = new List<(string What, Func<MotionFrame, MotionFrame> Change)>
            {
                ("unchanged", f => f),
                ("timer advanced by one", f =>
                {
                    f.StateTimer++;
                    return f;
                }),
                ("timer jumped", f =>
                {
                    f.StateTimer = (ushort)(f.StateTimer + 500);
                    return f;
                }),
                ("new state and activity", f =>
                {
                    f.State = 5;
                    f.Activity = 200;
                    f.StateTimer = 0;
                    return f;
                }),
                ("escaped state", f =>
                {
                    f.State = 7;
                    return f;
                }),
                ("flags changed", f =>
                {
                    f.Flags ^= 0x80000003u;
                    return f;
                }),
            };
            uint tick = 16;
            foreach ((string what, Func<MotionFrame, MotionFrame> change) in cases)
            {
                MotionFrame reference = refs.Frames[tick];
                MotionFrame want = change(reference);
                want.X += 0.5f;
                tick += 16;
                w.Reset();
                MotionCodec.WriteQBDelta(w, 7, 15, 1, want, reference, false);
                MotionFrame got = Decode(w, tick, refs, out _);
                AssertClose(want, got, what);
                refs.Frames[tick] = got;
            }
        }

        [Test]
        public void ADeltaWithoutItsReferenceIsConsumedAndRefused()
        {
            var refs = new References();
            var w = new ByteWriter();
            MotionFrame a = OnCircle(0, 40, 0), b = OnCircle(16, 40, 0);
            MotionCodec.WriteQBDelta(w, 3, 1, 2, b, a, false);
            MotionCodec.WriteQBKeyframe(w, 3, 1, b, false);
            var r = new ByteReader(w.Buffer, 0, w.Length);
            Assert.False(MotionCodec.TryReadQB(r, 32, refs, out QbHeader h, out _), "its reference (tick 0) is not held");
            Assert.Equal((byte)2, h.RefAge);
            Assert.True(MotionCodec.TryReadQB(r, 32, refs, out h, out MotionFrame key), "the next record still decodes");
            Assert.True(h.Keyframe);
            AssertClose(b, key, "keyframe after a refused delta");
            Assert.True(r.IsAtEnd);
        }
    }
}
