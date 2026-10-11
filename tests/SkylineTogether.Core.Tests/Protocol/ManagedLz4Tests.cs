using System;
using System.Text;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Protocol.ProtocolTestUtil;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>
    /// ManagedLz4Compressor: round trips, compatibility with the reference LZ4 block format (hand-built spec vectors,
    /// an independent spec decoder in this file checking the compressor's output and its end-of-block rules) and strict
    /// rejection of malformed blocks.
    /// </summary>
    public sealed class ManagedLz4Tests
    {
        private static readonly ManagedLz4Compressor Lz4 = new ManagedLz4Compressor();

        private static byte[] Compress(byte[] input)
        {
            var dst = new byte[Lz4.MaxCompressedSize(input.Length)];
            int n = Lz4.Compress(input, 0, input.Length, dst, 0);
            return Slice(dst, 0, n);
        }

        private static byte[] Decompress(byte[] block, int expected)
        {
            var dst = new byte[expected];
            Assert.Equal(expected, Lz4.Decompress(block, 0, block.Length, dst, 0, expected));
            return dst;
        }

        private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

        // ---- an independent decoder written from the LZ4 block format description, with the compressor rules ----

        /// <summary>Decodes a block per the format description and checks the rules every LZ4 compressor keeps:
        /// the last sequence is literals only, the last 5 bytes are literals, the last match starts at least 12 bytes
        /// before the end.</summary>
        private static byte[] SpecDecode(byte[] block, int expected)
        {
            var output = new byte[expected];
            int ip = 0, op = 0, lastMatchStart = -1, lastMatchEnd = 0;
            while (true)
            {
                int token = block[ip++];
                int literals = token >> 4;
                if (literals == 15)
                {
                    int b;
                    do { b = block[ip++]; literals += b; } while (b == 255);
                }
                Array.Copy(block, ip, output, op, literals);
                ip += literals;
                op += literals;
                if (ip == block.Length) break;
                int offset = block[ip] | (block[ip + 1] << 8);
                ip += 2;
                Assert.True(offset > 0 && offset <= op, "offset inside the output");
                int match = (token & 15);
                if (match == 15)
                {
                    int b;
                    do { b = block[ip++]; match += b; } while (b == 255);
                }
                match += 4;
                lastMatchStart = op;
                for (int i = 0; i < match; i++) output[op + i] = output[op - offset + i];
                op += match;
                lastMatchEnd = op;
            }
            Assert.Equal(expected, op, "decoded length");
            if (expected >= 13 && lastMatchStart >= 0)
            {
                Assert.True(lastMatchEnd <= expected - 5, "the last 5 bytes are literals");
                Assert.True(lastMatchStart <= expected - 12, "the last match starts at least 12 bytes before the end");
            }
            return output;
        }

        [Test]
        public void ReferenceOutputsOfSimpleInputs()
        {
            // the canonical greedy result of the reference compressor
            AssertBytes("00", Compress(new byte[0]));
            AssertBytes("30 616263", Compress(Ascii("abc")));
            AssertBytes("16 61 0100 50 6161616161", Compress(Ascii(new string('a', 16))));
            AssertBytes("1f 61 0100 ff 14 50 6161616161", Compress(Ascii(new string('a', 300))));
        }

        [Test]
        public void HandBuiltSpecBlocksDecode()
        {
            Assert.SequenceEqual(Ascii("abc"), Decompress(Hex("30 616263"), 3));
            Assert.SequenceEqual(new byte[0], Decompress(Hex("00"), 0));
            // 3 literals, overlapping match (offset 3, length 12), last literals "abcab"
            Assert.SequenceEqual(Ascii("abcabcabcabcabcabcab"), Decompress(Hex("38 616263 0300 50 6162636162"), 20));
            // literal run of 20 with one length byte
            byte[] twenty = Ascii("ABCDEFGHIJKLMNOPQRST");
            Assert.SequenceEqual(twenty, Decompress(Hex("f0 05 " + ToHex(twenty, 0, 20)), 20));
            // match length 4 + 15 + 255 + 1 = 275 with offset 1, then 5 literals
            byte[] longMatch = Decompress(Hex("1f 78 0100 ff 01 50 7979797979"), 281);
            for (int i = 0; i < 276; i++) Assert.Equal((byte)'x', longMatch[i]);
            for (int i = 276; i < 281; i++) Assert.Equal((byte)'y', longMatch[i]);
        }

        [Test]
        public void MaximumOffsetIsSupported()
        {
            // 65536 literals (15 + 256 × 255 + 241), a match of 4 at offset 65535, then 8 literals: the last match
            // must start at least 12 bytes before the end of the block (4 match + 8 literal bytes)
            var random = new DeterministicRandom(3);
            var literals = new byte[65536];
            random.NextBytes(literals, 0, literals.Length);
            var block = new System.Collections.Generic.List<byte> { 0xF0 };
            for (int i = 0; i < 256; i++) block.Add(255);
            block.Add(241);
            block.AddRange(literals);
            block.Add(0xFF);
            block.Add(0xFF);
            block.Add(0x80);
            block.AddRange(Ascii("stuvwxyz"));
            byte[] output = Decompress(block.ToArray(), 65536 + 4 + 8);
            for (int i = 0; i < 4; i++) Assert.Equal(literals[1 + i], output[65536 + i]);
            Assert.SequenceEqual(literals, Slice(output, 0, 65536));
            Assert.SequenceEqual(Ascii("stuvwxyz"), Slice(output, 65540, 8));

            // the same block with only 5 trailing literals breaks the end-of-block rule (the reference decoder rejects it too)
            block.RemoveRange(block.Count - 9, 9);
            block.Add(0x50);
            block.AddRange(Ascii("vwxyz"));
            Assert.Throws<DecodeException>(() => Decompress(block.ToArray(), 65536 + 4 + 5), "last match within 12 bytes of the end");
        }

        [Test]
        public void CompressorOutputFollowsTheReferenceFormat()
        {
            var random = new DeterministicRandom(7);
            for (int round = 0; round < 300; round++)
            {
                byte[] input = SampleInput(random, round);
                byte[] block = Compress(input);
                Assert.True(block.Length <= Lz4.MaxCompressedSize(input.Length));
                Assert.SequenceEqual(input, SpecDecode(block, input.Length), "spec decoder, round " + round);
                Assert.SequenceEqual(input, Decompress(block, input.Length), "managed decoder, round " + round);
            }
        }

        internal static byte[] SampleInput(DeterministicRandom random, int round)
        {
            int size = round < 70 ? round : random.Next(1, 200_000);
            var input = new byte[size];
            switch (round % 4)
            {
                case 0:
                    random.NextBytes(input, 0, size);                                // incompressible
                    break;
                case 1:
                    for (int i = 0; i < size; i++) input[i] = (byte)(i % 13 == 0 ? random.Next(256) : i % 5);   // repetitive
                    break;
                case 2:
                    const string words = "road building zone district line water power ";
                    for (int i = 0; i < size; i++) input[i] = (byte)words[(i * 7 + random.Next(3)) % words.Length];
                    break;
                default:
                    for (int i = 0; i < size; i++) input[i] = (byte)(random.Next(8) == 0 ? random.Next(4) : 0);   // sparse
                    break;
            }
            return input;
        }

        [Test]
        public void LargeInputsRoundTrip()
        {
            var zeros = new byte[Limits.MaxUncompressedBytes];
            byte[] block = Compress(zeros);
            Assert.True(block.Length < zeros.Length / 200, "zeros compress well");
            Assert.SequenceEqual(zeros, Decompress(block, zeros.Length));

            var random = new DeterministicRandom(9);
            var noise = new byte[3 << 20];
            random.NextBytes(noise, 0, noise.Length);
            block = Compress(noise);
            Assert.True(block.Length <= Lz4.MaxCompressedSize(noise.Length));
            Assert.SequenceEqual(noise, Decompress(block, noise.Length));
        }

        [Test]
        public void OffsetsAndDeterminism()
        {
            byte[] input = Ascii("the quick brown fox jumps over the lazy dog, the quick brown fox jumps over the lazy dog");
            var dst = new byte[10 + Lz4.MaxCompressedSize(input.Length)];
            var source = new byte[input.Length + 5];
            Array.Copy(input, 0, source, 3, input.Length);
            int n = Lz4.Compress(source, 3, input.Length, dst, 10);
            Assert.SequenceEqual(Compress(input), Slice(dst, 10, n), "same block wherever the buffers lie");
            var output = new byte[input.Length + 4];
            Lz4.Decompress(dst, 10, n, output, 4, input.Length);
            Assert.SequenceEqual(input, Slice(output, 4, input.Length));
        }

        [Test]
        public void MalformedBlocksAreRejected()
        {
            Assert.Throws<DecodeException>(() => Decompress(Hex("40 6162"), 4), "literals beyond the block");
            Assert.Throws<DecodeException>(() => Decompress(Hex("38 616263 0000 50 6162636162"), 20), "zero offset");
            Assert.Throws<DecodeException>(() => Decompress(Hex("38 616263 0400 50 6162636162"), 20), "offset before the output");
            Assert.Throws<DecodeException>(() => Decompress(Hex("39 616263 0300 40 61626361"), 20), "match into the last 5 bytes");
            Assert.Throws<DecodeException>(() => Decompress(Hex("38 616263 03"), 20), "truncated offset");
            Assert.Throws<DecodeException>(() => Decompress(Hex("30 616263"), 4), "shorter than expected");
            Assert.Throws<DecodeException>(() => Decompress(Hex("30 616263"), 2), "longer than expected");
            Assert.Throws<DecodeException>(() => Decompress(Hex("30 616263 00"), 3), "trailing bytes");
            Assert.Throws<DecodeException>(() => Decompress(Hex("38 616263 0300 50 6162636162"), 10), "a match too close to the end");
            Assert.Throws<DecodeException>(() => Decompress(Hex("f0"), 20), "long literal run without length bytes");
            Assert.Throws<DecodeException>(() => Decompress(Hex("f0 ff ff ff"), 20), "length bytes beyond the output");
            Assert.Throws<DecodeException>(() => Decompress(Hex("1f 78 0100 ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff"), 30), "match length beyond the output");
            Assert.Throws<DecodeException>(() => Decompress(new byte[0], 0), "empty block");
            Assert.Throws<DecodeException>(() => Lz4.Decompress(Hex("30 616263"), 0, 4, new byte[2], 0, 3), "destination too small");
            Assert.Throws<DecodeException>(() => Lz4.Decompress(Hex("30 616263"), 0, 4, new byte[8], 0, -1), "negative length");
        }

        [Test]
        public void CompressArgumentsAreValidated()
        {
            Assert.Throws<ArgumentException>(() => Lz4.Compress(new byte[100], 0, 100, new byte[50], 0), "destination below the bound");
            Assert.Throws<ArgumentOutOfRangeException>(() => Lz4.Compress(new byte[10], 5, 10, new byte[64], 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Lz4.MaxCompressedSize(-1));
            Assert.Equal(CompressionKind.ManagedLz4, Lz4.Kind);
            Assert.Equal(16, Lz4.MaxCompressedSize(0));
            Assert.Equal(255 + 1 + 16, Lz4.MaxCompressedSize(255));
        }

        [Test(TimeoutSeconds = 120)]
        public void RandomBlocksNeverEscapeAsOtherExceptions()
        {
            var random = new DeterministicRandom(21);
            var dst = new byte[4096];
            for (int i = 0; i < 20000; i++)
            {
                byte[] input = SampleInput(random, 4 + random.Next(60));
                byte[] block = Compress(input);
                // mutate a few bytes, or truncate
                int mutations = 1 + random.Next(3);
                for (int m = 0; m < mutations && block.Length > 0; m++) block[random.Next(block.Length)] = (byte)random.Next(256);
                int length = random.Next(4) == 0 ? random.Next(block.Length + 1) : block.Length;
                int expected = random.Next(4) == 0 ? random.Next(200) : input.Length;
                try
                {
                    Lz4.Decompress(block, 0, length, dst, 0, Math.Min(expected, dst.Length));
                }
                catch (DecodeException)
                {
                }
            }
        }
    }
}
