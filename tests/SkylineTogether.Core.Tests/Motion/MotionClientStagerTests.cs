using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Motion;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Motion
{
    /// <summary>The client stager against the real host encoder (ARCHITECTURE.md#movers).</summary>
    public sealed class MotionClientStagerTests
    {
        private sealed class CaptureOutput : IPeerOutput
        {
            public readonly List<(MessageKind Kind, byte[] Body)> Messages = new List<(MessageKind, byte[])>();

            public PeerId Peer => new PeerId(1);

            public ConnectionStats Stats => default(ConnectionStats);

            public long QueuedBytes => 0;

            public double OldestQueuedSeconds => 0;

            public bool CanAcceptReliable(int bytes) => true;

            public SendResult Send(Lane lane, Delivery delivery, byte[] buffer, int offset, int count)
            {
                Assert.Equal((byte)0, buffer[offset + 1], "envelope flags");
                var body = new byte[count - 2];
                Buffer.BlockCopy(buffer, offset + 2, body, 0, body.Length);
                Messages.Add(((MessageKind)buffer[offset], body));
                return SendResult.Ok;
            }

            /// <summary>Feeds everything sent so far into the stager, in order.</summary>
            public void Deliver(MotionClientStager stager, List<MotionPin> pins, Predicate<int> dropDatagram = null)
            {
                int datagram = 0;
                foreach ((MessageKind kind, byte[] body) in Messages)
                {
                    if (kind == MessageKind.MotionControl) stager.ApplyControl(body, 0, body.Length, pins);
                    else if (kind == MessageKind.Motion)
                    {
                        if (dropDatagram != null && dropDatagram(datagram++)) continue;
                        stager.ApplyDatagram(body, 0, body.Length, out _);
                    }
                }
                Messages.Clear();
            }
        }

        private static readonly PeerId P = new PeerId(1);

        /// <summary>A car driving along +x at 10 m/s, frame of tick t (midpoint convention irrelevant here).</summary>
        private static MotionFrame Car(uint tick)
        {
            float x = 100f + tick * (10f / 60f);
            return new MotionFrame { X = x, Y = 5f, Z = -20f, VX = 10f, QX = 0f, QY = 0.7071068f, QZ = 0f, QW = 0.7071068f, Flags = 0x20 };
        }

        private static void Enter(MotionHostEncoder enc, uint slot, byte u, uint newest)
        {
            var frames = new MotionFrame[4];
            var ticks = new uint[4];
            for (int k = 0; k < 4; k++)
            {
                ticks[k] = newest - (uint)(16 * k);
                frames[k] = Car(ticks[k]);
            }
            enc.EnterInterest(P, new NetSlot(slot), u, frames, ticks);
        }

        [Test]
        public void ARefusedInterestEntrySaysSo()
        {
            var enc = new MotionHostEncoder(NullLog.Instance);
            var frames = new MotionFrame[4];
            var ticks = new uint[4];
            for (int k = 0; k < 4; k++)
            {
                ticks[k] = 800 - (uint)(16 * k);
                frames[k] = Car(ticks[k]);
            }
            Assert.False(enc.EnterInterest(P, new NetSlot(5), 0, frames, ticks), "an unknown peer");
            enc.AddPeer(P);
            Assert.True(enc.EnterInterest(P, new NetSlot(5), 0, frames, ticks));
            frames[2].X = float.NaN;
            Assert.False(enc.EnterInterest(P, new NetSlot(6), 0, frames, ticks), "a baseline that is not finite: the mover must not count as streamed");
        }

        [Test]
        public void BaselineAndFramesArriveAndSampleExactly()
        {
            var enc = new MotionHostEncoder(NullLog.Instance);
            enc.AddPeer(P);
            var output = new CaptureOutput();
            var stager = new MotionClientStager();
            var pins = new List<MotionPin>();
            const byte u = 5;
            Enter(enc, 42, u, 1000 * 16 + u);
            enc.Flush(P, output);
            output.Deliver(stager, pins);
            Assert.Equal(1, stager.AssignedCount);
            Assert.True(stager.HasAppliedControl);
            Assert.True(stager.TryGetAssignment(0, out NetSlot slot, out byte uf, out int v0));
            Assert.Equal(42u, slot.Value);
            Assert.Equal(u, uf);

            for (uint step = 1; step <= 6; step++)
            {
                uint tick = 1000 * 16 + u + step * 16;
                enc.AddFrame(P, tick, new NetSlot(42), Car(tick), true, false);
                enc.Flush(P, output);
                output.Deliver(stager, pins);
                Assert.True(stager.TrySample(0, tick, out MotionFrame f), "tick " + tick);
                Assert.False(f.Synthetic);
                Assert.Near(Car(tick).X, f.X, 1.0 / 64);
                Assert.Near(10.0, f.VX, 0.2);
                Assert.Equal(0x20u, f.Flags);
            }
            Assert.True(stager.TryGetAssignment(0, out _, out _, out int v1));
            Assert.True(v1 != v0, "the version changes with new frames");
            Assert.Equal(0, pins.Count);
            Assert.True(stager.FramesAccepted >= 6);
        }

        [Test]
        public void InterpolatesBetweenAndExtrapolatesPastFramesThenPins()
        {
            uint[] ticks = { 16, 32 };
            MotionFrame[] frames = { Car(16), Car(32) };
            Assert.True(MotionClientStager.Sample(ticks, frames, 2, 24, out MotionFrame mid));
            Assert.True(mid.Synthetic);
            Assert.Near(Car(24).X, mid.X, 1e-3);
            Assert.True(MotionClientStager.Sample(ticks, frames, 2, 48, out MotionFrame ahead));
            Assert.Near(Car(48).X, ahead.X, 1e-3);
            Assert.Near(10.0, ahead.VX, 1e-6);
            Assert.True(MotionClientStager.Sample(ticks, frames, 2, 32 + 200, out MotionFrame pinned));
            Assert.Near(Car(32 + MotionClientStager.ExtrapolationCapTicks).X, pinned.X, 1e-3);
            Assert.Equal(0f, pinned.VX);
            Assert.True(MotionClientStager.Sample(ticks, frames, 2, 3, out MotionFrame before));
            Assert.Near(Car(16).X, before.X, 1e-6);
            Assert.False(MotionClientStager.Sample(ticks, frames, 0, 3, out _));
        }

        [Test]
        public void ReleasePinsAndDropsLaterRecords()
        {
            var enc = new MotionHostEncoder(NullLog.Instance);
            enc.AddPeer(P);
            var output = new CaptureOutput();
            var stager = new MotionClientStager();
            var pins = new List<MotionPin>();
            Enter(enc, 7, 0, 640);
            enc.Flush(P, output);
            output.Deliver(stager, pins);
            enc.ExitInterest(P, new NetSlot(7));
            enc.Flush(P, output);
            output.Deliver(stager, pins);
            Assert.Equal(1, pins.Count);
            Assert.Equal(7u, pins[0].Slot.Value);
            Assert.True(pins[0].HasFrame);
            Assert.Near(Car(640).X, pins[0].Frame.X, 1.0 / 64);
            Assert.Equal(0f, pins[0].Frame.VX);
            Assert.Equal(0, stager.AssignedCount);

            // ack, then the interest slot is reused for another mover with a new generation
            enc.OnAck(P, stager.LastAppliedControlSeq);
            Enter(enc, 8, 0, 656);
            enc.Flush(P, output);
            output.Deliver(stager, pins);
            Assert.Equal(1, stager.AssignedCount);
            Assert.True(stager.TryGetNewest(0, out uint newest, out _));
            Assert.Equal(656u, newest);
        }

        [Test]
        public void LostDatagramsNeverCorruptLaterFrames()
        {
            var enc = new MotionHostEncoder(NullLog.Instance);
            enc.AddPeer(P);
            var output = new CaptureOutput();
            var stager = new MotionClientStager();
            var pins = new List<MotionPin>();
            const byte u = 3;
            uint start = 160 + u;
            Enter(enc, 9, u, start);
            enc.Flush(P, output);
            output.Deliver(stager, pins);
            int delivered = 0;
            for (uint step = 1; step <= 24; step++)
            {
                uint tick = start + step * 16;
                enc.AddFrame(P, tick, new NetSlot(9), Car(tick), true, false);
                enc.Flush(P, output);
                bool drop = step % 3 == 1;
                output.Deliver(stager, pins, i => drop);
                if (!drop) delivered++;
                if (stager.TryGetNewest(0, out uint newest, out MotionFrame f))
                {
                    Assert.Near(Car(newest).X, f.X, 1.0 / 64, "frame of tick " + newest);
                }
            }
            // every delivered frame is either applied or refused; a delta whose reference was lost is refused, never
            // decoded against another frame (the per-step check above verifies the applied ones)
            Assert.Equal((long)delivered, stager.FramesAccepted + stager.FramesDropped, "one frame per delivered datagram");
            Assert.True(stager.FramesDropped > 0, "deltas against a lost reference are refused");
            Assert.True(stager.FramesAccepted >= delivered / 2, "keyframes and deltas against delivered references apply: " + stager.FramesAccepted);
            Assert.True(stager.TryGetNewest(0, out uint last, out _));
            Assert.True(last >= start + 20 * 16, "keyframes resynchronise after losses");
        }

        [Test]
        public void ReleaseAllAndAckCodec()
        {
            var stager = new MotionClientStager();
            var w = new ByteWriter();
            MotionClientStager.WriteAck(w, 0xA1B2C3D4u);
            Assert.True(MotionClientStager.TryReadAck(w.Buffer, 0, w.Length, out uint seq));
            Assert.Equal(0xA1B2C3D4u, seq);
            Assert.False(MotionClientStager.TryReadAck(new byte[] { 1, 2, 3, 4, 5 }, 0, 5, out _));
            Assert.Throws<DecodeException>(() => stager.ApplyControl(new byte[] { 1, 0, 0, 0, 1, 0, 9 }, 0, 7, new List<MotionPin>()));
            Assert.Throws<DecodeException>(() => stager.ApplyDatagram(new byte[] { 1, 2, 3 }, 0, 3, out _));
        }
    }
}
