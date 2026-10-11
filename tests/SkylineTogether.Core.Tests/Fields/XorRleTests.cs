using System;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Fields
{
    /// <summary>XorRle: XOR delta round trips, sizes, keyframes against zero, atomic application and malformed input.</summary>
    public sealed class XorRleTests
    {
        private static byte[] Delta(byte[] previous, byte[] current, int count)
        {
            return FieldTestSupport.Write(w => XorRle.EncodeDelta(previous, current, count, w));
        }

        [Test]
        public void RoundTripsRandomVersions()
        {
            var random = new DeterministicRandom(8001);
            for (int round = 0; round < 200; round++)
            {
                int count = random.Next(70000);
                var previous = new byte[count];
                random.NextBytes(previous, 0, count);
                var current = (byte[])previous.Clone();
                int changes = count == 0 ? 0 : random.Next(1 + count / (1 + random.Next(1000)));
                for (int i = 0; i < changes; i++) current[random.Next(count)] = (byte)random.Next(256);
                byte[] delta = Delta(previous, current, count);
                var target = (byte[])previous.Clone();
                var reader = new ByteReader(delta, 0, delta.Length);
                XorRle.ApplyDelta(reader, target, count);
                Assert.True(reader.IsAtEnd, "round " + round + ": the delta is consumed exactly");
                Assert.SequenceEqual(current, target, "round " + round);
            }
        }

        [Test]
        public void UnchangedAndSparseMapsAreTiny()
        {
            var random = new DeterministicRandom(8002);
            var map = new byte[256 * 256 * 4];   // a 256² cell map of 4-byte cells
            random.NextBytes(map, 0, map.Length);
            byte[] unchanged = Delta(map, map, map.Length);
            Assert.True(unchanged.Length <= 8, "an unchanged 256 KiB map costs " + unchanged.Length + " bytes");
            var changed = (byte[])map.Clone();
            for (int cell = 0; cell < 100; cell++) changed[random.Next(map.Length / 4) * 4] ^= 0x5A;
            byte[] sparse = Delta(map, changed, map.Length);
            Assert.True(sparse.Length < 100 * 8, "100 changed cells cost " + sparse.Length + " bytes");
        }

        [Test]
        public void NullPreviousGivesAKeyframe()
        {
            var random = new DeterministicRandom(8003);
            var current = new byte[3000];
            for (int i = 0; i < current.Length; i += 1 + random.Next(20)) current[i] = (byte)(1 + random.Next(255));
            byte[] keyframe = Delta(null, current, current.Length);
            var target = new byte[current.Length];
            XorRle.ApplyDelta(new ByteReader(keyframe, 0, keyframe.Length), target, current.Length);
            Assert.SequenceEqual(current, target);
            Assert.SequenceEqual(Delta(new byte[current.Length], current, current.Length), keyframe, "null equals an all-zero previous version");
        }

        [Test]
        public void OnlyCountBytesAreCovered()
        {
            byte[] previous = { 1, 2, 3, 4, 5, 6 };
            byte[] current = { 1, 9, 3, 4, 7, 7 };
            byte[] delta = Delta(previous, current, 4);
            byte[] target = (byte[])previous.Clone();
            XorRle.ApplyDelta(new ByteReader(delta, 0, delta.Length), target, 4);
            Assert.SequenceEqual(new byte[] { 1, 9, 3, 4, 5, 6 }, target, "bytes beyond count are untouched");
            // count 4, then one pair: 1 zero (unchanged), 3 literal bytes 2^9 = 11, 0, 0 (two trailing zeros stay literal)
            Assert.SequenceEqual(new byte[] { 4, 1, 3, 11, 0, 0 }, delta);
        }

        [Test]
        public void TheReaderEndsBehindTheDelta()
        {
            byte[] previous = new byte[100];
            byte[] current = new byte[100];
            current[50] = 1;
            byte[] framed = FieldTestSupport.Write(w =>
            {
                w.WriteByte(0xAA);
                XorRle.EncodeDelta(previous, current, 100, w);
                w.WriteUInt32(0xCAFEBABE);
            });
            var reader = new ByteReader(framed, 0, framed.Length);
            Assert.Equal((byte)0xAA, reader.ReadByte());
            XorRle.ApplyDelta(reader, previous, 100);
            Assert.Equal(0xCAFEBABEu, reader.ReadUInt32(), "the data after the delta follows");
            Assert.True(reader.IsAtEnd);
            Assert.SequenceEqual(current, previous);
        }

        [Test]
        public void MalformedDeltasThrowAndLeaveTheTargetUnchanged()
        {
            var target = new byte[] { 10, 20, 30, 40 };
            AssertRejected(new byte[] { 5, 0, 5 }, target, "count mismatch");
            // first pair is valid (2 literal bytes), the second pair overruns: nothing may have been applied
            AssertRejected(new byte[] { 4, 0, 2, 0xFF, 0xFF, 0, 9 }, target, "a later pair overruns");
            AssertRejected(new byte[] { 4, 0, 2, 0xFF, 0xFF, 0, 0 }, target, "an empty pair");
            AssertRejected(new byte[] { 4, 1, 2, 0xFF }, target, "truncated literal");
            AssertRejected(new byte[0], target, "empty input");
            byte[] valid = Delta(new byte[4], new byte[] { 1, 2, 3, 4 }, 4);
            FieldTestSupport.AssertEveryTruncationFails(valid, r => XorRle.ApplyDelta(r, (byte[])target.Clone(), 4), "XOR delta");
            FieldTestSupport.AssertCorruptionOnlyThrowsDecodeException(Delta(new byte[300], NewRandom(300, 5), 300),
                r => XorRle.ApplyDelta(r, new byte[300], 300), 8004, 3000, "XOR delta");
        }

        [Test]
        public void ArgumentsAreValidated()
        {
            var writer = new ByteWriter();
            Assert.Throws<ArgumentNullException>(() => XorRle.EncodeDelta(new byte[1], null, 1, writer));
            Assert.Throws<ArgumentNullException>(() => XorRle.EncodeDelta(new byte[1], new byte[1], 1, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => XorRle.EncodeDelta(new byte[1], new byte[2], 2, writer));
            Assert.Throws<ArgumentOutOfRangeException>(() => XorRle.EncodeDelta(null, new byte[2], 3, writer));
            Assert.Throws<ArgumentOutOfRangeException>(() => XorRle.EncodeDelta(null, new byte[2], -1, writer));
            Assert.Throws<ArgumentNullException>(() => XorRle.ApplyDelta(null, new byte[1], 1));
            Assert.Throws<ArgumentNullException>(() => XorRle.ApplyDelta(new ByteReader(), null, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => XorRle.ApplyDelta(new ByteReader(), new byte[1], 2));
            Assert.Equal(0, writer.Length);
        }

        private static void AssertRejected(byte[] delta, byte[] target, string what)
        {
            byte[] before = (byte[])target.Clone();
            Assert.Throws<DecodeException>(() => XorRle.ApplyDelta(new ByteReader(delta, 0, delta.Length), target, target.Length), what);
            Assert.SequenceEqual(before, target, what + ": the target is unchanged");
        }

        private static byte[] NewRandom(int count, ulong seed)
        {
            var data = new byte[count];
            new DeterministicRandom(seed).NextBytes(data, 0, count);
            return data;
        }
    }
}
