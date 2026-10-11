using System;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Fields
{
    /// <summary>The shared zero-run coding: exact format, round trips in every mode, size bound and malformed input.</summary>
    public sealed class ZeroRunCodecTests
    {
        private static byte[] Encode(byte[] data, byte[] xorWith = null)
        {
            var buffer = new byte[ZeroRunCodec.MaxEncodedLength(data.Length)];
            int length = ZeroRunCodec.Encode(data, 0, xorWith, 0, data.Length, buffer, 0);
            var result = new byte[length];
            Buffer.BlockCopy(buffer, 0, result, 0, length);
            return result;
        }

        private static byte[] Decode(byte[] encoded, int count)
        {
            var target = new byte[count];
            int consumed = ZeroRunCodec.Decode(encoded, 0, encoded.Length, target, 0, count, ZeroRunCodec.Mode.Overwrite);
            Assert.Equal(encoded.Length, consumed, "the whole stream is consumed");
            return target;
        }

        [Test]
        public void FormatIsPinned()
        {
            // pair (4 zeros, literal 5 0 6): the single zero stays inside the literal; pair (3 zeros, literal 7)
            byte[] encoded = Encode(new byte[] { 0, 0, 0, 0, 5, 0, 6, 0, 0, 0, 7 });
            Assert.SequenceEqual(new byte[] { 4, 3, 5, 0, 6, 3, 1, 7 }, encoded);
            Assert.SequenceEqual(new byte[] { 0, 2, 1, 2 }, Encode(new byte[] { 1, 2 }), "literal only");
            Assert.SequenceEqual(new byte[] { 0x80, 0x01, 0 }, Encode(new byte[128]), "zeros only (varint 128)");
            Assert.SequenceEqual(new byte[] { 0, 3, 9, 0, 0 }, Encode(new byte[] { 9, 0, 0 }), "two trailing zeros stay literal");
            Assert.SequenceEqual(new byte[] { 0, 1, 9, 3, 0 }, Encode(new byte[] { 9, 0, 0, 0 }), "three trailing zeros become a run");
            Assert.SequenceEqual(new byte[0], Encode(new byte[0]), "nothing for an empty input");
        }

        [Test]
        public void RoundTripsAtEveryZeroDensity()
        {
            var random = new DeterministicRandom(7001);
            foreach (int density in new[] { 0, 1, 10, 50, 90, 99, 100 })
            {
                for (int round = 0; round < 40; round++)
                {
                    var data = new byte[random.Next(2000)];
                    for (int i = 0; i < data.Length; i++) data[i] = random.Next(100) < density ? (byte)0 : (byte)(1 + random.Next(255));
                    byte[] encoded = Encode(data);
                    Assert.True(encoded.Length <= ZeroRunCodec.MaxEncodedLength(data.Length), "size bound");
                    Assert.SequenceEqual(data, Decode(encoded, data.Length), "density " + density + " round " + round);
                }
            }
        }

        [Test]
        public void XorModesApplyDeltas()
        {
            var random = new DeterministicRandom(7002);
            var previous = new byte[5000];
            random.NextBytes(previous, 0, previous.Length);
            var current = (byte[])previous.Clone();
            for (int i = 0; i < 40; i++) current[random.Next(current.Length)] ^= (byte)(1 + random.Next(255));
            byte[] encoded = Encode(current, previous);
            Assert.True(encoded.Length < 300, "40 changed bytes cost " + encoded.Length + " bytes");
            var target = (byte[])previous.Clone();
            ZeroRunCodec.Decode(encoded, 0, encoded.Length, target, 0, target.Length, ZeroRunCodec.Mode.Xor);
            Assert.SequenceEqual(current, target);
            // validation touches nothing (and accepts a null target)
            Assert.Equal(encoded.Length, ZeroRunCodec.Decode(encoded, 0, encoded.Length, null, 0, target.Length, ZeroRunCodec.Mode.Validate));
        }

        [Test]
        public void EncodedSizeStaysWithinTheBoundForAdversarialPatterns()
        {
            foreach (int period in new[] { 1, 2, 3, 4, 5, 7, 128, 129 })
            {
                foreach (int length in new[] { 0, 1, 2, 3, 100, 4096, 70000 })
                {
                    var data = new byte[length];
                    for (int i = 0; i < length; i += period) data[i] = 0xAB;
                    byte[] encoded = Encode(data);
                    Assert.True(encoded.Length <= ZeroRunCodec.MaxEncodedLength(length), "period " + period + " length " + length + ": " + encoded.Length);
                    Assert.SequenceEqual(data, Decode(encoded, length), "period " + period + " length " + length);
                }
            }
            var allLiteral = new byte[3 << 20];
            for (int i = 0; i < allLiteral.Length; i++) allLiteral[i] = (byte)(1 + i % 255);
            Assert.True(Encode(allLiteral).Length <= ZeroRunCodec.MaxEncodedLength(allLiteral.Length), "a 3 MiB literal run");
        }

        [Test]
        public void DecodeStopsAtTheEndOfTheStreamAndHonoursOffsets()
        {
            byte[] encoded = Encode(new byte[] { 0, 0, 0, 0, 5, 0, 6, 0, 0, 0, 7 });
            var framed = new byte[encoded.Length + 13];
            Buffer.BlockCopy(encoded, 0, framed, 6, encoded.Length);
            framed[6 + encoded.Length] = 0xEE;   // trailing data that is not part of the stream
            var target = new byte[20];
            for (int i = 0; i < target.Length; i++) target[i] = 0xCC;
            int consumed = ZeroRunCodec.Decode(framed, 6, framed.Length, target, 4, 11, ZeroRunCodec.Mode.Overwrite);
            Assert.Equal(encoded.Length, consumed);
            Assert.SequenceEqual(new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0, 0, 0, 0, 5, 0, 6, 0, 0, 0, 7, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, target);
        }

        [Test]
        public void MalformedStreamsThrowDecodeException()
        {
            AssertInvalid(new byte[] { 0, 0 }, 4, "a pair that describes nothing");
            AssertInvalid(new byte[] { 5, 0 }, 4, "a zero run beyond the count");
            AssertInvalid(new byte[] { 0, 5, 1, 2, 3, 4, 5 }, 4, "a literal run beyond the count");
            AssertInvalid(new byte[] { 0, 4, 1, 2 }, 4, "a literal run beyond the input");
            AssertInvalid(new byte[] { 0x80 }, 4, "a truncated varint");
            AssertInvalid(new byte[] { 0x82, 0x00, 0 }, 4, "an overlong varint");
            AssertInvalid(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x1F, 0 }, 4, "a varint above 32 bits");
            AssertInvalid(new byte[] { 2, 0 }, 4, "a stream that ends early");
            AssertInvalid(new byte[0], 1, "an empty stream for one byte");
        }

        [Test]
        public void RandomInputOnlyEverThrowsDecodeException()
        {
            var random = new DeterministicRandom(7003);
            var target = new byte[64];
            for (int round = 0; round < 5000; round++)
            {
                var garbage = new byte[random.Next(40)];
                random.NextBytes(garbage, 0, garbage.Length);
                try
                {
                    ZeroRunCodec.Decode(garbage, 0, garbage.Length, target, 0, random.Next(65), (ZeroRunCodec.Mode)random.Next(3));
                }
                catch (DecodeException)
                {
                }
                catch (Exception e)
                {
                    Assert.Fail("round " + round + " threw " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        private static void AssertInvalid(byte[] stream, int count, string what)
        {
            foreach (ZeroRunCodec.Mode mode in new[] { ZeroRunCodec.Mode.Validate, ZeroRunCodec.Mode.Overwrite, ZeroRunCodec.Mode.Xor })
            {
                var target = new byte[count];
                Assert.Throws<DecodeException>(() => ZeroRunCodec.Decode(stream, 0, stream.Length, target, 0, count, mode), what + " (" + mode + ")");
            }
        }
    }
}
