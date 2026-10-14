using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Identity;
using SkylineTogether.Core.Motion;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

#pragma warning disable CS8981 // the stand-ins keep the game's lower-case type names
namespace Unity.Mathematics
{
    // stand-ins with the game's type names (FloatLayout recognises quaternions by their Unity.Mathematics name)
    [StructLayout(LayoutKind.Sequential)]
    internal struct float3
    {
        public float x, y, z;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct float4
    {
        public float x, y, z, w;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct quaternion
    {
        public float4 value;
    }
}

#pragma warning restore CS8981

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>A stand-in for Game.Objects.Transform: a position and a rotation.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FakeTransform
    {
        public Unity.Mathematics.float3 m_Position;
        public Unity.Mathematics.quaternion m_Rotation;
    }

    /// <summary>A stand-in for Game.Areas.Node: a position and an elevation that may be the float.MinValue sentinel.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FakeAreaNode
    {
        public Unity.Mathematics.float3 m_Position;
        public float m_Elevation;
        public int m_Flags;
        public byte m_Kind;
    }

    /// <summary>w8/robust: validation at the decode boundary (Core parts).</summary>
    public sealed class InputValidationTests
    {
        private static int OffsetOf(FieldInfo f) => (int)Marshal.OffsetOf(f.DeclaringType, f.Name);

        private static FakeTransform Transform(float x, float y, float z, float qx, float qy, float qz, float qw)
        {
            return new FakeTransform
            {
                m_Position = new Unity.Mathematics.float3 { x = x, y = y, z = z },
                m_Rotation = new Unity.Mathematics.quaternion { value = new Unity.Mathematics.float4 { x = qx, y = qy, z = qz, w = qw } },
            };
        }

        // ------------------------------------------------------------------ float layouts

        [Test]
        public void FloatLayoutFindsNestedFloatsAndQuaternions()
        {
            FloatLayout layout = FloatLayout.Build(typeof(FakeTransform), 1000f, OffsetOf);
            Assert.Equal(7, layout.Floats.Length, "three position floats and the quaternion's four");
            Assert.Equal(1, layout.Quaternions.Length);
            Assert.Equal(12, layout.Quaternions[0], "the quaternion follows the float3");
            Assert.Equal(28, layout.MinSize);
        }

        [Test]
        public void SaneTransformsPassAndBrokenOnesAreRefused()
        {
            FloatLayout layout = FloatLayout.Build(typeof(FakeTransform), 1000f, OffsetOf);
            Assert.True(layout.IsSane(Transform(10, 20, -30, 0, 0, 0, 1)));
            Assert.True(layout.IsSane(Transform(999, 0, 0, 0, 0.7071068f, 0, 0.7071068f)));
            Assert.False(layout.IsSane(Transform(float.NaN, 0, 0, 0, 0, 0, 1)), "NaN position");
            Assert.False(layout.IsSane(Transform(0, float.PositiveInfinity, 0, 0, 0, 0, 1)), "infinite position");
            Assert.False(layout.IsSane(Transform(5000, 0, 0, 0, 0, 0, 1)), "beyond the bound");
            Assert.True(layout.IsSane(Transform(0, 0, 0, 0, 0, 0, 0)), "an all-zero quaternion is a default the game itself may hold");
            Assert.False(layout.IsSane(Transform(0, 0, 0, 0, 0, 0, 3)), "a quaternion far from unit length");
            Assert.False(layout.IsSane(Transform(0, 0, 0, float.NaN, 0, 0, 1)), "NaN rotation");
        }

        [Test]
        public void TheGamesFloatSentinelsAreKept()
        {
            FloatLayout layout = FloatLayout.Build(typeof(FakeAreaNode), FloatSanity.PositionBound, OffsetOf);
            Assert.Equal(4, layout.Floats.Length, "int and byte fields are not floats");
            var node = new FakeAreaNode { m_Position = new Unity.Mathematics.float3 { x = 1, y = 2, z = 3 }, m_Elevation = float.MinValue, m_Flags = -1, m_Kind = 255 };
            Assert.True(layout.IsSane(node), "float.MinValue means 'no elevation' in area nodes");
            node.m_Elevation = float.NegativeInfinity;
            Assert.False(layout.IsSane(node));
            node.m_Elevation = 0;
            node.m_Position.x = float.NaN;
            Assert.False(layout.IsSane(node));
        }

        [Test]
        public void FloatLayoutChecksTheBytesItIsGiven()
        {
            FloatLayout layout = FloatLayout.Build(typeof(FakeTransform), 1000f, OffsetOf);
            var bytes = new byte[28];
            BitConverter.GetBytes(1f).CopyTo(bytes, 24); // w = 1
            Assert.True(layout.IsSane(bytes, 0));
            BitConverter.GetBytes(float.NaN).CopyTo(bytes, 4);
            Assert.False(layout.IsSane(bytes, 0));
            Assert.False(layout.IsSane(new byte[10], 0), "too short");
            Assert.False(layout.IsSane(bytes, 4), "offset leaves too few bytes");
        }

        [Test]
        public void OnlyGeometryTypesAreChecked()
        {
            Assert.Null(FloatSanity.LayoutOf(typeof(FakeTransform), OffsetOf), "not a checked game type name");
            Assert.Contains("Game.Objects.Transform", FloatSanity.CheckedTypeNames);
            Assert.Contains("Game.Net.Curve", FloatSanity.CheckedTypeNames);
            Assert.Contains("Game.Areas.Node", FloatSanity.CheckedTypeNames);
        }

        // ------------------------------------------------------------------ inbound quotas

        [Test]
        public void InboundQuotaRefusesAPeerOverItsLimitAndReportsItOnce()
        {
            var q = new InboundQuota(3, 1000);
            var a = new PeerId(1);
            var b = new PeerId(2);
            Assert.True(q.TryAdmit(a, 100));
            Assert.True(q.TryAdmit(a, 100));
            Assert.True(q.TryAdmit(a, 100));
            Assert.False(q.TryAdmit(a, 100), "the fourth waiting message");
            Assert.True(q.TryAdmit(b, 900), "other peers are not affected");
            Assert.False(q.TryAdmit(b, 200), "the byte limit");
            Assert.True(q.TakeOverflow(a));
            Assert.False(q.TakeOverflow(a), "reported once");
            Assert.Equal(2L, q.Dropped);
            q.Release(a, 100);
            Assert.True(q.TryAdmit(a, 100), "room again after a release");
            Assert.Equal(3, q.Pending(a));
            q.Forget(a);
            Assert.Equal(0, q.Pending(a));
            Assert.False(q.TryAdmit(new PeerId(3), 5000), "one message above the byte limit is refused outright");
        }

        // ------------------------------------------------------------------ ack stalls

        [Test]
        public void AckStallWatchReportsOnlyAPeerThatIsBehindWithoutProgress()
        {
            var watch = new AckStallWatch(10);
            var acked = new Dictionary<byte, uint> { { 1, 5 }, { 2, 5 } };
            byte[] peers = { 1, 2 };
            uint current = 5;
            Assert.Equal(0, watch.Update(peers, p => acked[p], current, 0).Count);
            current = 50; // the host keeps sending; peer 2 keeps applying, peer 1 does not
            for (double t = 1; t <= 10; t += 1)
            {
                acked[2] = current - 1;
                current++;
                Assert.Equal(0, watch.Update(peers, p => acked[p], current, t).Count, "within the limit at " + t);
            }
            acked[2] = current;
            IReadOnlyList<byte> stalled = watch.Update(peers, p => acked[p], current, 11);
            Assert.Equal(1, stalled.Count);
            Assert.Equal((byte)1, stalled[0]);
            Assert.Equal(0, watch.Update(peers, p => acked[p], current, 12).Count, "reported once");
            acked[1] = current;
            Assert.Equal(0, watch.Update(peers, p => acked[p], current, 30).Count, "caught up: no stall");
            Assert.Equal(0, watch.Update(peers, p => acked[p], current, 300).Count, "an idle, caught-up peer is never stalled");
        }

        // ------------------------------------------------------------------ id grants

        [Test]
        public void ClientIdPoolRefusesRangesNoTableCanHold()
        {
            var pool = new ClientIdPool();
            Assert.False(pool.Add(0, 10), "id 0 is 'no id'");
            Assert.False(pool.Add(NetIdAllocator.MaxId, 2), "runs past the largest id");
            Assert.False(pool.Add(uint.MaxValue - 1, 5), "wraps around");
            Assert.False(pool.Add(NetIdAllocator.MaxId + 1, 1), "starts beyond the largest id");
            Assert.Equal(4L, pool.Refused);
            Assert.True(pool.Add(NetIdAllocator.MaxId, 1), "the very last id is fine");
            Assert.True(pool.Add(100, 3));
            Assert.True(pool.TryTake(out uint first));
            Assert.Equal(NetIdAllocator.MaxId, first);
            Assert.True(pool.TryTake(out uint second));
            Assert.Equal(100u, second);
            Assert.True(ClientIdPool.IsValidRange(1, NetIdAllocator.MaxId));
            Assert.False(ClientIdPool.IsValidRange(2, NetIdAllocator.MaxId));
        }

        // ------------------------------------------------------------------ terrain stamps

        private static TerrainStamp Stamp()
        {
            return new TerrainStamp
            {
                ToolPrefab = 1, BrushPrefab = 2, PositionX = 100, PositionY = 50, PositionZ = -200, TargetX = 100, TargetZ = -200,
                StartX = 90, StartZ = -190, Size = 50, Strength = 0.5f, Opacity = 0.25f, DeltaTime = 0.05f,
            };
        }

        [Test]
        public void TerrainStampsOutsideTheMapOrWithWildValuesAreRefused()
        {
            Assert.True(Stamp().IsPlausible(2000, TerrainStamp.MaxCoordinate, TerrainStamp.MaxStrength));
            TerrainStamp s = Stamp();
            s.PositionX = 3e38f;
            Assert.True(s.IsPlausible(2000), "finite, so the old check passed it");
            Assert.False(s.IsPlausible(2000, TerrainStamp.MaxCoordinate, TerrainStamp.MaxStrength), "a coordinate far outside any map");
            s = Stamp();
            s.Opacity = 7;
            Assert.False(s.IsPlausible(2000, TerrainStamp.MaxCoordinate, TerrainStamp.MaxStrength), "opacity is a fraction");
            s = Stamp();
            s.Strength = -1e9f;
            Assert.False(s.IsPlausible(2000, TerrainStamp.MaxCoordinate, TerrainStamp.MaxStrength), "strength bounded");
            s = Stamp();
            s.DeltaTime = 50;
            Assert.False(s.IsPlausible(2000, TerrainStamp.MaxCoordinate, TerrainStamp.MaxStrength), "frame time bounded");
            s = Stamp();
            s.TargetY = float.NaN;
            Assert.False(s.IsPlausible(2000, TerrainStamp.MaxCoordinate, TerrainStamp.MaxStrength));
        }

        // ------------------------------------------------------------------ flow status

        [Test]
        public void FlowStatusValuesAreSanitisedOnDecode()
        {
            var w = new ByteWriter(256);
            int count = FlowStatusMessage.Begin(w, FlowNetwork.Water, 0);
            var key = new FlowKey(new FlowNodeKey(5, FlowNodeRole.Net), new FlowNodeKey(6, FlowNodeRole.Net));
            FlowStatusMessage.WriteEntry(w, FlowNetwork.Water, key, new FlowValue { Flow = -40, Capacity = -100, SewageFlow = 3, SewageCapacity = -7, Pollution = float.NaN, Flags = 1 });
            FlowStatusMessage.End(w, count, 1);
            var output = new List<KeyValuePair<FlowKey, FlowValue>>();
            FlowStatusMessage.Read(w.Buffer, 0, w.Length, output, out _);
            Assert.Equal(1, output.Count);
            FlowValue v = output[0].Value;
            Assert.Equal(-40, v.Flow, "flow keeps its direction");
            Assert.Equal(0, v.Capacity, "capacities are never negative");
            Assert.Equal(0, v.SewageCapacity);
            Assert.Equal(0f, v.Pollution, "NaN pollution becomes 0");
        }

        // ------------------------------------------------------------------ motion interest slots

        [Test]
        public void MotionControlRefusesInterestSlotsBeyondTheLimit()
        {
            var stager = new MotionClientStager();
            var w = new ByteWriter(256);
            w.WriteUInt32(1);
            w.WriteUInt16(1);
            w.WriteByte(MotionWire.OpAssign);
            var frames = new MotionFrame[4];
            for (int i = 0; i < 4; i++) frames[i] = new MotionFrame { X = i, QW = 1 };
            MotionCodec.WriteQA(w, 60000, 1, new NetSlot(42), 0, frames, new uint[] { 16, 32, 48, 64 });
            var pins = new List<MotionPin>();
            Assert.Throws<DecodeException>(() => stager.ApplyControl(w.Buffer, 0, w.Length, pins));
            Assert.True(stager.SlotCapacity < 100, "no ring was allocated for the bogus slot (" + stager.SlotCapacity + ")");
        }

        // ------------------------------------------------------------------ envelopes and fragments

        [Test]
        public void EnvelopeDecodeHonoursALowerBodyLimit()
        {
            var codec = new EnvelopeCodec(new ManagedLz4Compressor(), null);
            var body = new byte[200_000]; // compressible
            var encoded = new ByteWriter(4096);
            codec.Encode(MessageKind.Control, body, 0, body.Length, encoded);
            var scratch = new ByteWriter(16);
            Assert.Equal(200_000, codec.Decode(encoded.Buffer, 0, encoded.Length, scratch).BodyCount);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<DecodeException>(() => codec.Decode(encoded.Buffer, 0, encoded.Length, new ByteWriter(16), 64 * 1024));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 100_000, "refused before decompressing");
        }

        [Test]
        public void ReassemblerBuffersWhatArrivesNotWhatIsDeclared()
        {
            var r = new Reassembler();
            var w = new ByteWriter(64);
            w.WriteByte((byte)MessageKind.Epoch);
            w.WriteByte((byte)EnvelopeFlags.Fragment);
            w.WriteVarUInt32(9);
            w.WriteVarUInt32(0);
            w.WriteVarUInt32(30);
            w.WriteVarUInt32(8_000_000);
            w.WriteBytes(new byte[1000], 0, 1000);
            var e = new Envelope { Kind = MessageKind.Epoch, Flags = EnvelopeFlags.Fragment, Body = w.Buffer, BodyOffset = 2, BodyCount = w.Length - 2 };
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.False(r.Accept(e, out _, out _));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 200_000, "the first fragment of an 8 MB message costs little");
            Assert.Equal(1, r.PartialMessages);
            var r2 = new Reassembler();
            Assert.Throws<DecodeException>(() => r2.Accept(e, out _, out _, 1024 * 1024), "a lower limit refuses the declared size");
        }

        [Test]
        public void FragmentedMessagesStillReassembleExactlyWithGrowingBuffers()
        {
            var fragmenter = new Fragmenter();
            var codec = new EnvelopeCodec(null, null);
            var body = new byte[1_500_000];
            new Random(3).NextBytes(body);
            var encoded = new ByteWriter(4096);
            codec.Encode(MessageKind.Epoch, body, 0, body.Length, encoded);
            var fragments = new List<byte[]>();
            var scratch = new ByteWriter(4096);
            fragmenter.Split(MessageKind.Epoch, encoded.Buffer, 0, encoded.Length, scratch, (b, o, c) =>
            {
                var copy = new byte[c];
                Buffer.BlockCopy(b, o, copy, 0, c);
                fragments.Add(copy);
            });
            Assert.True(fragments.Count > 2);
            var r = new Reassembler();
            byte[] message = null;
            int length = 0;
            foreach (byte[] f in fragments)
            {
                Envelope env = codec.Decode(f, 0, f.Length, new ByteWriter(16));
                if (r.Accept(env, out message, out length)) break;
            }
            Assert.NotNull(message);
            Envelope whole = codec.Decode(message, 0, length, new ByteWriter(16));
            Assert.Equal(body.Length, whole.BodyCount);
            for (int i = 0; i < body.Length; i += 9973) Assert.Equal(body[i], whole.Body[whole.BodyOffset + i]);
        }
    }
}
