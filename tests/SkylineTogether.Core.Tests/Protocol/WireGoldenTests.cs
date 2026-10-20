using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Motion;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;
using static SkylineTogether.Core.Tests.Protocol.ProtocolTestUtil;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>
    /// Golden byte vectors of the payload codecs outside <see cref="MessageGoldenTests"/>: flow status, statistics,
    /// apply acks, agent records, local texts, blob framing, tile bodies and mover rotations. Every expected byte is
    /// written by hand from the documented layout (the comments name the fields), never computed from the
    /// implementation's own constants, so a changed constant, field order or width fails here and forces a protocol
    /// version bump (docs/dev/releasing.md#protocol-version-policy). Each vector is decoded back where a reader exists.
    /// </summary>
    public sealed class WireGoldenTests
    {
        private static byte[] Write(Action<ByteWriter> write)
        {
            var w = new ByteWriter();
            write(w);
            return w.ToArray();
        }

        // ------------------------------------------------------------------ flow status

        [Test]
        public void FlowStatusElectricity()
        {
            // u8 version 1 | u8 network Electricity (1) | u8 flags keyframe (1) | u32 n = 2
            // entry: varint 300 | varint 70000 | roles Net(1) << 4 | Consumer(5) | zigzag -3 | zigzag 1000 | flags 5
            // entry: varint 0 | varint 1 | roles Source(8) << 4 | Net(1) | zigzag 64 | zigzag 0 | flags 0
            const string hex = "01 01 01 | 02000000 | ac02 f0a204 15 05 d00f 05 | 00 01 81 8001 00 00";
            var a = new FlowKey(new FlowNodeKey(300, FlowNodeRole.Net), new FlowNodeKey(70000, FlowNodeRole.Consumer));
            var b = new FlowKey(new FlowNodeKey(0, FlowNodeRole.Source), new FlowNodeKey(1, FlowNodeRole.Net));
            byte[] bytes = Write(w =>
            {
                int at = FlowStatusMessage.Begin(w, FlowNetwork.Electricity, 1);
                FlowStatusMessage.WriteEntry(w, FlowNetwork.Electricity, a, new FlowValue { Flow = -3, Capacity = 1000, Flags = 5 });
                FlowStatusMessage.WriteEntry(w, FlowNetwork.Electricity, b, new FlowValue { Flow = 64 });
                FlowStatusMessage.End(w, at, 2);
            });
            AssertBytes(hex, bytes);
            byte[] golden = Hex(hex);
            var read = new List<KeyValuePair<FlowKey, FlowValue>>();
            Assert.Equal(FlowNetwork.Electricity, FlowStatusMessage.Read(golden, 0, golden.Length, read, out byte flags));
            Assert.Equal((byte)1, flags);
            Assert.Equal(2, read.Count);
            Assert.Equal(a, read[0].Key);
            Assert.Equal(-3, read[0].Value.Flow, "flow and capacity are not swapped");
            Assert.Equal(1000, read[0].Value.Capacity);
            Assert.Equal((byte)5, read[0].Value.Flags);
            Assert.Equal(b, read[1].Key);
            Assert.Equal(64, read[1].Value.Flow);
            Assert.Equal(0, read[1].Value.Capacity);
        }

        [Test]
        public void FlowStatusWater()
        {
            // u8 version 1 | u8 network Water (2) | u8 flags 0 | u32 n = 1
            // entry: varint 5 | varint 0 | roles Valve(2) << 4 | Sink(9) | zigzag 10 | zigzag 20 | flags 2
            //        | zigzag sewage -1 | zigzag sewage capacity 7 | f32 pollution 0.5
            const string hex = "01 02 00 | 01000000 | 05 00 29 14 28 02 | 01 0e | 0000003f";
            var key = new FlowKey(new FlowNodeKey(5, FlowNodeRole.Valve), new FlowNodeKey(0, FlowNodeRole.Sink));
            var value = new FlowValue { Flow = 10, Capacity = 20, Flags = 2, SewageFlow = -1, SewageCapacity = 7, Pollution = 0.5f };
            byte[] bytes = Write(w =>
            {
                int at = FlowStatusMessage.Begin(w, FlowNetwork.Water, 0);
                FlowStatusMessage.WriteEntry(w, FlowNetwork.Water, key, value);
                FlowStatusMessage.End(w, at, 1);
            });
            AssertBytes(hex, bytes);
            byte[] golden = Hex(hex);
            var read = new List<KeyValuePair<FlowKey, FlowValue>>();
            Assert.Equal(FlowNetwork.Water, FlowStatusMessage.Read(golden, 0, golden.Length, read, out byte flags));
            Assert.Equal((byte)0, flags);
            Assert.Equal(key, read[0].Key);
            Assert.True(value.Equals(read[0].Value), "decoded " + read[0].Value);
        }

        // ------------------------------------------------------------------ statistics

        [Test]
        public void StatisticsSeries()
        {
            // u8 version 1 | u8 type series (1) | i32 sampleCount 7 | u32 lastSampleFrame 0x01020304 | varuint n = 1
            // | zigzag type 3 | zigzag parameter 0 | varuint count 1 | f64 1.5 | f64 -2.0
            const string hex = "01 01 | 07000000 | 04030201 | 01 | 06 00 01 | 000000000000f83f 00000000000000c0";
            var series = new StatisticSeries { Type = 3, Parameter = 0, Values = new[] { 1.5 }, Totals = new[] { -2.0 } };
            AssertBytes(hex, Write(w => StatisticsMessage.Write(w, 7, 0x01020304u, new[] { series })));
            byte[] golden = Hex(hex);
            List<StatisticSeries> read = StatisticsMessage.Read(golden, 0, golden.Length, out int samples, out uint frame);
            Assert.Equal(7, samples);
            Assert.Equal(0x01020304u, frame);
            Assert.Equal(1, read.Count);
            Assert.Equal(3, read[0].Type);
            Assert.SequenceEqual(new[] { 1.5 }, read[0].Values);
            Assert.SequenceEqual(new[] { -2.0 }, read[0].Totals);
        }

        // ------------------------------------------------------------------ acks and agents

        [Test]
        public void ApplyAck()
        {
            // varuint64 applied count
            AssertBytes("00", Write(w => ApplyAckMessage.Write(w, 0)));
            AssertBytes("ac02", Write(w => ApplyAckMessage.Write(w, 300)));
            AssertBytes("ffffffffffffffff7f", Write(w => ApplyAckMessage.Write(w, long.MaxValue)));
            byte[] golden = Hex("ac02");
            Assert.Equal(300L, ApplyAckMessage.Read(golden, 0, golden.Length));
            byte[] tooLarge = Hex("ffffffffffffffffff01"); // 2^64 - 1
            Assert.Throws<DecodeException>(() => ApplyAckMessage.Read(tooLarge, 0, tooLarge.Length));
        }

        [Test]
        public void AgentRefresh()
        {
            // u8 version 1 | varint n = 3 | varint 1 | varint 0 | varint 300
            const string hex = "01 03 01 00 ac02";
            AssertBytes(hex, Write(w => AgentRefreshMessage.Write(w, new uint[] { 1, 0, 300 })));
            byte[] golden = Hex(hex);
            Assert.SequenceEqual(new uint[] { 1, 300 }, AgentRefreshMessage.Read(golden, 0, golden.Length), "zero ids are dropped");
        }

        [Test]
        public void MoverAgentLinks()
        {
            // u8 flags (agent 1 | owner 4) | varint agent 5 | varint owner 300
            const string hex = "05 05 ac02";
            AssertBytes(hex, Write(w => MoverAgentLinksCodec.Write(w, new MoverAgentLinks { Agent = 5, Owner = 300 })));
            MoverAgentLinks read = MoverAgentLinksCodec.Read(Reader(Hex(hex)));
            Assert.Equal(5u, read.Agent);
            Assert.Equal(0u, read.Keeper);
            Assert.Equal(300u, read.Owner);
            AssertBytes("00", Write(w => MoverAgentLinksCodec.Write(w, new MoverAgentLinks())), "no links: the flags byte only");
            AssertBytes("02 07", Write(w => MoverAgentLinksCodec.Write(w, new MoverAgentLinks { Keeper = 7 })));
        }

        // ------------------------------------------------------------------ texts

        [Test]
        public void LocalTextWithAKeyAndAnArgument()
        {
            // text "Hi Bo" | key "st.net.x" | varint 1 pair | name "NAME" | value "Bo" (strings: varint UTF-8 length | bytes)
            const string hex = "05 486920426f | 08 73742e6e65742e78 | 01 | 04 4e414d45 | 02 426f";
            var text = new LocalText { Text = "Hi Bo", Key = "st.net.x", Args = new[] { "NAME", "Bo" } };
            AssertBytes(hex, Write(w => LocalText.Write(w, text)));
            LocalText read = LocalText.Read(Reader(Hex(hex)));
            Assert.Equal("Hi Bo", read.Text);
            Assert.Equal("st.net.x", read.Key);
            Assert.SequenceEqual(new[] { "NAME", "Bo" }, read.Args);
            AssertBytes("00 00 00", Write(w => LocalText.Write(w, null)), "no text: empty text, no key, no arguments");
        }

        [Test]
        public void MessageFieldPrimitives()
        {
            // bytes: varint length | data; string list: varint count + 1 (0 = null) | strings; nullable count likewise
            AssertBytes("03 010203", Write(w => MessageFields.WriteBytes(w, new byte[] { 1, 2, 3 }, 8, "f")));
            AssertBytes("00", Write(w => MessageFields.WriteBytes(w, null, 8, "f")));
            AssertBytes("00", Write(w => MessageFields.WriteStringList(w, null, 4, 8, "f")), "null list");
            AssertBytes("01", Write(w => MessageFields.WriteStringList(w, new List<string>(), 4, 8, "f")), "empty list");
            AssertBytes("03 01 61 00", Write(w => MessageFields.WriteStringList(w, new List<string> { "a", "" }, 4, 8, "f")));
            AssertBytes("0000c03f", Write(w => MessageFields.WriteFloat(w, 1.5f)));
            AssertBytes("00000000", Write(w => MessageFields.WriteFloat(w, float.NaN)), "non-finite floats are sent as 0");
            AssertBytes("00", Write(w => MessageFields.WritePrefab(w, PrefabRank.None)), "prefab rank + 1: none (-1) is 0");
            AssertBytes("06", Write(w => MessageFields.WritePrefab(w, new PrefabRank(5))));
        }

        // ------------------------------------------------------------------ blob transfer

        [Test]
        public void BlobManifest()
        {
            // u32 blobId | varint64 size 1300 | varint chunkSize 512 | u64 digest lo | u64 digest hi | purpose "city"
            const string hex = "04030201 | 940a | 8004 | 8877665544332211 | 00ffeeddccbbaa99 | 04 63697479";
            var m = new BlobManifestInfo { BlobId = 0x01020304, Size = 1300, ChunkSize = 512, Digest = new Digest128(0x1122334455667788UL, 0x99AABBCCDDEEFF00UL), Purpose = "city" };
            AssertBytes(hex, Write(w => BlobCodec.WriteManifest(w, m)));
            byte[] golden = Hex(hex);
            BlobManifestInfo read = BlobCodec.DecodeManifest(golden, 0, golden.Length);
            Assert.Equal(m.BlobId, read.BlobId);
            Assert.Equal(1300L, read.Size);
            Assert.Equal(512, read.ChunkSize);
            Assert.Equal(m.Digest, read.Digest);
            Assert.Equal("city", read.Purpose);
        }

        [Test]
        public void BlobChunkAndHave()
        {
            // envelope kind BlobChunk (16) | flags 0 | u32 blobId 7 | u32 index 3 | u32 length 0 | u64 XXH64 of nothing (seed 0)
            AssertBytes("10 00 | 07000000 | 03000000 | 00000000 | 99e9d85137db46ef",
                        Write(w => BlobCodec.WriteChunkMessage(w, 7, 3, Array.Empty<byte>(), 0, 0)));
            // u32 blobId | varint bitmap length | bitmap
            const string have = "07000000 | 01 | 05";
            AssertBytes(have, Write(w => BlobCodec.WriteHave(w, 7, new byte[] { 5 }, 0, 1)));
            byte[] golden = Hex(have);
            ArraySegment<byte> bitmap = BlobCodec.DecodeHave(golden, 0, golden.Length, out uint blobId);
            Assert.Equal(7u, blobId);
            Assert.Equal(1, bitmap.Count);
            Assert.Equal((byte)5, bitmap.Array[bitmap.Offset]);
        }

        // ------------------------------------------------------------------ tile bodies

        [Test]
        public void TileBodyUncompressed()
        {
            // u8 flags 0 | varuint zero-run length 8 | zero-run stream (4 zeros, literal 5 0 6; 3 zeros, literal 7)
            byte[] raw = { 0, 0, 0, 0, 5, 0, 6, 0, 0, 0, 7 };
            const string hex = "00 | 08 | 04 03 05 00 06 03 01 07";
            AssertBytes(hex, Write(w => TileBodyCodec.Write(raw, raw.Length, null, w, null, "test")));
            AssertBytes(hex, Write(w => TileBodyCodec.Write(raw, raw.Length, new ManagedLz4Compressor(), w, null, "test")), "short streams are never compressed");
            var back = new byte[raw.Length];
            TileBodyCodec.Read(Reader(Hex(hex)), raw.Length, null, back, "test");
            Assert.SequenceEqual(raw, back);
        }

        [Test]
        public void TileBodyCompressedHeader()
        {
            // u8 flags 1 (compressed) | u8 CompressionKind.ManagedLz4 (1) | varuint zero-run length 203 | varuint packed 3 | packed
            var raw = new byte[200];
            for (int i = 0; i < raw.Length; i++) raw[i] = 1; // one literal run: 00 | varuint 200 | 200 × 01 = 203 bytes
            AssertBytes("01 01 | cb01 | 03 | aabbcc", Write(w => TileBodyCodec.Write(raw, raw.Length, new FixedCompressor(), w, null, "test")));
        }

        /// <summary>A "compressor" whose output is always aa bb cc (only the framing around it is under test).</summary>
        private sealed class FixedCompressor : ICompressor
        {
            public CompressionKind Kind => CompressionKind.ManagedLz4;

            public int MaxCompressedSize(int length) => length + 16;

            public int Compress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset)
            {
                dst[dstOffset] = 0xAA;
                dst[dstOffset + 1] = 0xBB;
                dst[dstOffset + 2] = 0xCC;
                return 3;
            }

            public int Decompress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset, int expectedLength) => throw new NotSupportedException();
        }

        // ------------------------------------------------------------------ mover rotations

        private static byte[] Rotation(RotationCode code)
        {
            var w = new ByteWriter();
            var bits = new MotionBitWriter(w);
            MotionRotation.Write(ref bits, code);
            bits.Flush();
            return w.ToArray();
        }

        private static RotationCode ReadRotation(string hex)
        {
            var bits = new MotionBitReader(Reader(Hex(hex)));
            return MotionRotation.Read(ref bits, allowSameAsReference: true);
        }

        [Test]
        public void RotationCodes()
        {
            // bits are packed LSB first. Yaw: mode 1 (2 bits) | yaw 0x123 (12 bits) = 0x048d
            AssertBytes("8d 04", Rotation(new RotationCode { Mode = RotationMode.Yaw, Yaw = 0x123 }));
            // YawPitch: mode 2 | yaw 0xabc | pitch -5 (7-bit two's complement 0x7b) = 0x1eeaf2
            AssertBytes("f2 ea 1e", Rotation(new RotationCode { Mode = RotationMode.YawPitch, Yaw = 0xABC, Pitch = -5 }));
            // Full: mode 3 | index 2 | a -1 (0xfff) | b 2047 (0x7ff) | c -2047 (0x801), 12 bits each = 0x8017fffffb
            AssertBytes("fb ff ff 17 80", Rotation(new RotationCode { Mode = RotationMode.Full, Index = 2, A = -1, B = 2047, C = -2047 }));
            // SameAsReference: mode 0 only
            AssertBytes("00", Rotation(new RotationCode { Mode = RotationMode.SameAsReference }));

            RotationCode yp = ReadRotation("f2 ea 1e");
            Assert.Equal(RotationMode.YawPitch, yp.Mode);
            Assert.Equal(0xABC, yp.Yaw);
            Assert.Equal(-5, yp.Pitch);
            RotationCode full = ReadRotation("fb ff ff 17 80");
            Assert.Equal(RotationMode.Full, full.Mode);
            Assert.Equal(2, full.Index);
            Assert.Equal(-1, full.A);
            Assert.Equal(2047, full.B);
            Assert.Equal(-2047, full.C);
        }

        [Test]
        public void RotationEncodingConventions()
        {
            // the identity faces +Z: yaw 0, no pitch
            AssertBytes("01 00", Rotation(MotionRotation.Encode(0f, 0f, 0f, 1f)));
            // a quarter turn about +Y faces +X: yaw a quarter of 4096 steps (1024): mode 1 | 1024 << 2 = 0x1001
            float h = (float)Math.Sqrt(0.5);
            AssertBytes("01 10", Rotation(MotionRotation.Encode(0f, h, 0f, h)));
            // a half turn: yaw 2048 = 0x2001
            AssertBytes("01 20", Rotation(MotionRotation.Encode(0f, 1f, 0f, 0f)));
        }

        [Test]
        public void HalfFloats()
        {
            // IEEE binary16 bit patterns (sign | 5-bit exponent | 10-bit mantissa)
            Assert.Equal((ushort)0x3C00, MotionHalf.FromSingle(1f));
            Assert.Equal((ushort)0xC000, MotionHalf.FromSingle(-2f));
            Assert.Equal((ushort)0x3555, MotionHalf.FromSingle(1f / 3f));
            Assert.Equal((ushort)0x7BFF, MotionHalf.FromSingle(65504f));
            Assert.Equal((ushort)0x7BFF, MotionHalf.FromSingle(1e9f), "clamped, never infinite");
            Assert.Equal((ushort)0x0001, MotionHalf.FromSingle(5.9604645e-8f), "the smallest subnormal");
            Assert.Equal((ushort)0x3C00, MotionHalf.FromSingle(1.00048828125f), "a tie rounds to the even mantissa (down)");
            Assert.Equal((ushort)0x3C02, MotionHalf.FromSingle(1.00146484375f), "a tie rounds to the even mantissa (up)");
        }
    }
}
