using System;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Codec;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Codec.CodecTestUtil;

namespace SkylineTogether.Core.Tests.Codec
{
    /// <summary>Block bookkeeping, argument validation and target handling of <see cref="CanonicalWriter"/>.</summary>
    public sealed class CanonicalWriterTests
    {
        [Test]
        public void ConstructorRejectsMissingDependencies()
        {
            Assert.Throws<ArgumentNullException>(() => new CanonicalWriter(null, new FakeEntityRefs()));
            Assert.Throws<ArgumentNullException>(() => new CanonicalWriter(new ByteWriter(), null));
        }

        [Test]
        public void AppendsToTheTargetAfterExistingBytes()
        {
            var target = new ByteWriter();
            target.WriteByte(0xEE);
            var writer = new CanonicalWriter(target, new FakeEntityRefs());
            Assert.Same(target, writer.Target);
            BlockToken block = writer.Begin();
            Assert.Equal(1, block.Position, "the placeholder follows the existing byte");
            writer.WriteShort(5);
            Assert.True(writer.End(block, out int size));
            Assert.Equal(2, size);
            Assert.Equal("ee 02 00 00 00 05 00", Hex(target.ToArray()));
        }

        [Test]
        public void RetargetSwitchesTheBufferWithoutCarryingOpenBlocks()
        {
            var first = new ByteWriter();
            var second = new ByteWriter();
            second.WriteByte(0xEE);
            var entities = new FakeEntityRefs().BindSlot(3, 1, 9);
            var writer = new CanonicalWriter(first, entities);
            writer.WriteShort(1);
            writer.Begin();
            Assert.Equal(1, writer.OpenBlockCount);

            writer.Retarget(second);
            Assert.Same(second, writer.Target);
            Assert.Equal(0, writer.OpenBlockCount, "the open block belonged to the previous target");
            Assert.Same(entities, writer.Entities, "the entity seam is kept");
            BlockToken block = writer.Begin();
            writer.WriteEntity(3, 1);
            Assert.True(writer.End(block, out int size));
            Assert.Equal(1, size);
            Assert.Equal("ee 01 00 00 00 25", Hex(second.ToArray()), "appended after the existing byte");
            Assert.Equal("01 00 00 00 00 00", Hex(first.ToArray()), "the previous target is left as it was");

            Assert.Throws<ArgumentNullException>(() => writer.Retarget(null));
            Assert.Same(second, writer.Target, "a refused retarget changes nothing");
        }

        [Test]
        public void BlocksMustBeClosedInnermostFirst()
        {
            var writer = new CanonicalWriter(new ByteWriter(), new FakeEntityRefs());
            Assert.Throws<InvalidOperationException>(() => writer.End(new BlockToken { Position = 0 }), "no open block");
            BlockToken outer = writer.Begin();
            BlockToken inner = writer.Begin();
            Assert.Equal(2, writer.OpenBlockCount);
            Assert.Throws<InvalidOperationException>(() => writer.End(outer), "the inner block is still open");
            Assert.Throws<InvalidOperationException>(() => writer.End(new BlockToken { Position = 99 }));
            Assert.False(writer.End(inner));
            Assert.True(writer.End(outer));
            Assert.Equal(0, writer.OpenBlockCount);
            Assert.Throws<InvalidOperationException>(() => writer.End(outer), "a block cannot be closed twice");
        }

        [Test]
        public void DeepNestingWorks()
        {
            const int depth = 40;
            byte[] bytes = Write(w =>
            {
                var tokens = new BlockToken[depth];
                for (int i = 0; i < depth; i++)
                {
                    tokens[i] = w.Begin();
                    w.WriteByte((byte)i);
                }
                for (int i = depth - 1; i >= 0; i--) w.End(tokens[i]);
            });
            Assert.Equal(depth * 5, bytes.Length);
            CanonicalReader reader = Reader(bytes);
            var read = new BlockToken[depth];
            for (int i = 0; i < depth; i++)
            {
                read[i] = reader.Begin(out int size);
                Assert.Equal((depth - i) * 5 - 4, size, "level " + i);
                Assert.Equal((byte)i, reader.ReadByte());
            }
            for (int i = depth - 1; i >= 0; i--) Assert.True(reader.End(read[i]));
            Assert.True(reader.IsRecordConsumed);
        }

        [Test]
        public void AbortedSerializationCanBeCleanedUp()
        {
            var target = new ByteWriter();
            var writer = new CanonicalWriter(target, new FakeEntityRefs());
            writer.Begin();
            writer.WriteInt(1);
            Assert.Equal(1, writer.OpenBlockCount, "a Serialize call that threw left a block open");
            writer.ClearOpenBlocks();
            Assert.Equal(0, writer.OpenBlockCount);

            // a reset of the target also invalidates open blocks: Begin drops those beyond the written bytes
            writer.Begin();
            target.Reset();
            BlockToken fresh = writer.Begin();
            Assert.Equal(0, fresh.Position);
            Assert.Equal(1, writer.OpenBlockCount);
            Assert.False(writer.End(fresh));

            // End of a block whose bytes were discarded is refused instead of patching foreign bytes
            BlockToken stale = writer.Begin();
            target.Reset();
            Assert.Throws<InvalidOperationException>(() => writer.End(stale));
            Assert.Equal(0, writer.OpenBlockCount);
        }

        [Test]
        public void SourceArraysAreValidated()
        {
            var writer = new CanonicalWriter(new ByteWriter(), new FakeEntityRefs());
            Assert.Throws<ArgumentNullException>(() => writer.WriteIntArray(null, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteIntArray(new int[1], 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteIntArray(new int[1], -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteUShortArray(new ushort[1], 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteByteArray(new byte[1], 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteByteArray(new byte[3], 4, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteFloat2Array(new float[3], 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteFloat4Array(new float[7], 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteInt2Array(new int[3], 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteEntityArray(new int[2], new int[1], 2));
            Assert.Throws<ArgumentNullException>(() => writer.WriteEntityArray(null, new int[1], 1));
            Assert.Throws<ArgumentNullException>(() => writer.WriteBezier4x3(null));
            Assert.Throws<ArgumentException>(() => writer.WriteBezier4x3(new float[11]));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteCount(-1));
            Assert.Equal(0, writer.Target.Length, "rejected calls write nothing");

            // empty arrays may be null
            writer.WriteIntArray(null, 0);
            writer.WriteUShortArray(null, 0);
            writer.WriteByteArray(null, 0, 4);
            writer.WriteFloat2Array(null, 0);
            writer.WriteFloat4Array(null, 0);
            writer.WriteInt2Array(null, 0);
            writer.WriteEntityArray(null, null, 0);
            Assert.Equal(0, writer.Target.Length);
        }

        [Test]
        public void LargeArraysRoundTrip()
        {
            const int n = 20000;
            var ints = new int[n];
            var shorts = new ushort[n];
            var bytes = new byte[n];
            var floats = new float[4 * n];
            var pairs = new int[2 * n];
            for (int i = 0; i < n; i++)
            {
                ints[i] = unchecked((int)((uint)i * 2654435761u));
                shorts[i] = (ushort)(i * 7);
                bytes[i] = (byte)(i * 31);
                pairs[2 * i] = -i;
                pairs[2 * i + 1] = i << 8;
            }
            for (int i = 0; i < floats.Length; i++) floats[i] = FromBits(unchecked((uint)i * 0x9E3779B9u));

            byte[] data = Write(w =>
            {
                w.WriteCount(n);
                w.WriteIntArray(ints, n);
                w.WriteUShortArray(shorts, n);
                w.WriteByteArray(bytes, n, 3);
                w.WriteFloat4Array(floats, n);
                w.WriteFloat2Array(floats, n);
                w.WriteInt2Array(pairs, n);
            });
            CanonicalReader reader = Reader(data);
            int count = reader.ReadCount(4);
            Assert.Equal(n, count);
            var ints2 = new int[n];
            var shorts2 = new ushort[n];
            var bytes2 = new byte[n];
            var floats4 = new float[4 * n];
            var floats2 = new float[2 * n];
            var pairs2 = new int[2 * n];
            reader.ReadIntArray(ints2, n);
            reader.ReadUShortArray(shorts2, n);
            reader.ReadByteArray(bytes2, n, 3);
            reader.ReadFloat4Array(floats4, n);
            reader.ReadFloat2Array(floats2, n);
            reader.ReadInt2Array(pairs2, n);
            Assert.True(reader.IsRecordConsumed);
            Assert.SequenceEqual(ints, ints2);
            Assert.SequenceEqual(shorts, shorts2);
            Assert.SequenceEqual(bytes, bytes2);
            Assert.SequenceEqual(pairs, pairs2);
            for (int i = 0; i < floats4.Length; i++) Assert.Equal(Bits(floats[i]), Bits(floats4[i]), "float4 element " + i);
            for (int i = 0; i < floats2.Length; i++) Assert.Equal(Bits(floats[i]), Bits(floats2[i]), "float2 element " + i);
        }

        [Test]
        public void ArrayEncodingsMatchTheScalarEncodings()
        {
            // a NativeArray member is the same bytes as writing each element on its own (BinaryWriter.cs:68-102 is a
            // raw little-endian memcpy)
            var ints = new[] { 0, -1, int.MaxValue, int.MinValue, 0x01020304 };
            var shorts = new ushort[] { 0, 1, 0xFFFF, 0x8000 };
            var floats = new[] { 0f, -0f, 1.5f, float.NaN, float.NegativeInfinity, FromBits(0x7FC00001u), 3e-45f, 1e30f };
            Assert.Equal(WriteHex(w => { foreach (int v in ints) w.WriteInt(v); }), WriteHex(w => w.WriteIntArray(ints, ints.Length)));
            Assert.Equal(WriteHex(w => { foreach (ushort v in shorts) w.WriteUShort(v); }), WriteHex(w => w.WriteUShortArray(shorts, shorts.Length)));
            Assert.Equal(WriteHex(w => { foreach (float v in floats) w.WriteFloat(v); }), WriteHex(w => w.WriteFloat2Array(floats, floats.Length / 2)));
            Assert.Equal(WriteHex(w => { foreach (float v in floats) w.WriteFloat(v); }), WriteHex(w => w.WriteFloat4Array(floats, floats.Length / 4)));
            Assert.Equal(WriteHex(w => { for (int i = 0; i < 4; i++) w.WriteInt(ints[i]); }), WriteHex(w => w.WriteInt2Array(ints, 2)));
            Assert.Equal(WriteHex(w => { w.WriteFloat3(1, 2, 3); w.WriteFloat3(4, 5, 6); w.WriteFloat3(7, 8, 9); w.WriteFloat3(10, 11, 12); }),
                         WriteHex(w => w.WriteBezier4x3(new float[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 })));
        }
    }
}
