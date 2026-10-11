using System;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Protocol.ProtocolTestUtil;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>EnvelopeCodec (ARCH §4.6.1, §4.6.5): layout, compression, limits checked before decompression.</summary>
    public sealed class EnvelopeCodecTests
    {
        private static readonly ManagedLz4Compressor Lz4 = new ManagedLz4Compressor();

        private static byte[] Repeated(byte value, int count)
        {
            var bytes = new byte[count];
            for (int i = 0; i < count; i++) bytes[i] = value;
            return bytes;
        }

        [Test]
        public void SmallBodiesAreSentAsIs()
        {
            var codec = new EnvelopeCodec(Lz4);
            var output = new ByteWriter();
            codec.Encode(MessageKind.Ping, new byte[] { 1, 2, 3 }, 0, 3, output);
            AssertBytes("12 00 010203", output);       // kind Ping (18) | flags None | body

            Envelope e = codec.Decode(output.Buffer, 0, output.Length, new ByteWriter());
            Assert.Equal(MessageKind.Ping, e.Kind);
            Assert.Equal(EnvelopeFlags.None, e.Flags);
            Assert.SequenceEqual(new byte[] { 1, 2, 3 }, Slice(e.Body, e.BodyOffset, e.BodyCount));
        }

        [Test]
        public void EmptyBodiesAreValid()
        {
            var codec = new EnvelopeCodec(Lz4);
            var output = new ByteWriter();
            codec.Encode(MessageKind.Control, null, 0, 0, output);
            AssertBytes("04 00", output);
            Envelope e = codec.Decode(output.Buffer, 0, output.Length, new ByteWriter());
            Assert.Equal(0, e.BodyCount);
        }

        [Test]
        public void CompressedLayoutNamesTheCodec()
        {
            // 300 bytes of 'a': kind Epoch | flags Compressed|HasUncompressedSize | size 300 | codec ManagedLz4 | LZ4 block
            var codec = new EnvelopeCodec(Lz4);
            var output = new ByteWriter();
            codec.Encode(MessageKind.Epoch, Repeated((byte)'a', 300), 0, 300, output);
            AssertBytes("05 05 ac02 01 1f 61 0100 ff 14 50 6161616161", output);

            var scratch = new ByteWriter();
            Envelope e = codec.Decode(output.Buffer, 0, output.Length, scratch);
            Assert.Equal(MessageKind.Epoch, e.Kind);
            Assert.Equal(EnvelopeFlags.Compressed | EnvelopeFlags.HasUncompressedSize, e.Flags);
            Assert.Same(scratch.Buffer, e.Body);
            Assert.SequenceEqual(Repeated((byte)'a', 300), Slice(e.Body, e.BodyOffset, e.BodyCount));
        }

        [Test]
        public void CompressionIsSkippedBelowThresholdOrWithoutGain()
        {
            var codec = new EnvelopeCodec(Lz4);
            var output = new ByteWriter();
            byte[] small = Repeated(7, Limits.CompressThreshold - 1);
            codec.Encode(MessageKind.Epoch, small, 0, small.Length, output);
            Assert.Equal((byte)EnvelopeFlags.None, output.Buffer[1], "below the threshold");

            var random = new DeterministicRandom(5);
            byte[] noise = new byte[4096];
            random.NextBytes(noise, 0, noise.Length);
            output.Reset();
            codec.Encode(MessageKind.Epoch, noise, 0, noise.Length, output);
            Assert.Equal((byte)EnvelopeFlags.None, output.Buffer[1], "incompressible data is sent as is");
            Assert.Equal(2 + noise.Length, output.Length);

            output.Reset();
            new EnvelopeCodec(null).Encode(MessageKind.Epoch, Repeated(1, 1000), 0, 1000, output);
            Assert.Equal((byte)EnvelopeFlags.None, output.Buffer[1], "no compressor, no compression");
        }

        [Test]
        public void EncodeAppendsToTheOutput()
        {
            var codec = new EnvelopeCodec(Lz4);
            var output = new ByteWriter();
            output.WriteByte(0xEE);
            codec.Encode(MessageKind.Chat, new byte[] { 9 }, 0, 1, output);
            AssertBytes("ee 13 00 09", output);
        }

        [Test]
        public void RoundTripsEveryKindAndSize()
        {
            var codec = new EnvelopeCodec(Lz4);
            var random = new DeterministicRandom(11);
            var output = new ByteWriter();
            var scratch = new ByteWriter();
            for (int kind = 1; kind <= 19; kind++)
            {
                int max = Math.Min(Limits.MaxBodyBytes((MessageKind)kind), 70000);
                foreach (int size in new[] { 0, 1, 255, 256, 257, 1000, max })
                {
                    if (size > max) continue;
                    var body = new byte[size];
                    for (int i = 0; i < size; i++) body[i] = (byte)(random.Next(4) == 0 ? random.Next(256) : i % 7);
                    output.Reset();
                    codec.Encode((MessageKind)kind, body, 0, size, output);
                    Envelope e = codec.Decode(output.Buffer, 0, output.Length, scratch);
                    Assert.Equal((MessageKind)kind, e.Kind);
                    Assert.SequenceEqual(body, Slice(e.Body, e.BodyOffset, e.BodyCount), "kind " + kind + " size " + size);
                }
            }
        }

        [Test]
        public void BodiesAboveTheKindLimitAreRefusedByTheEncoder()
        {
            var codec = new EnvelopeCodec(Lz4);
            var output = new ByteWriter();
            Assert.Throws<ArgumentException>(() => codec.Encode(MessageKind.Ping, new byte[257], 0, 257, output));
            Assert.Throws<ArgumentException>(() => codec.Encode(MessageKind.Epoch, new byte[Limits.MaxUncompressedBytes + 1], 0, Limits.MaxUncompressedBytes + 1, output));
            Assert.Throws<ArgumentException>(() => codec.Encode((MessageKind)0, new byte[1], 0, 1, output));
            Assert.Equal(0, output.Length);
        }

        [Test]
        public void DeclaredSizeAboveTheLimitIsRejectedBeforeDecompression()
        {
            var spy = new SpyCompressor(Lz4, CompressionKind.ManagedLz4);
            var codec = new EnvelopeCodec(spy);
            // Epoch, compressed, declared 8 MiB + 1, codec ManagedLz4, a few bytes of "data"
            var message = new ByteWriter();
            message.WriteByte((byte)MessageKind.Epoch);
            message.WriteByte(5);
            message.WriteVarUInt32((uint)Limits.MaxUncompressedBytes + 1);
            message.WriteByte((byte)CompressionKind.ManagedLz4);
            message.WriteBytes(new byte[] { 0x00, 0x11, 0x22 }, 0, 3);
            var scratch = new ByteWriter(16);
            Assert.Throws<DecodeException>(() => codec.Decode(message.Buffer, 0, message.Length, scratch));
            Assert.Equal(0, spy.DecompressCalls, "the decompressor was never called");
            Assert.True(scratch.Capacity <= 16, "nothing was allocated for the bomb");

            // a bulk chunk may not declare more than 256 KiB (+ header)
            message.Reset();
            message.WriteByte((byte)MessageKind.BlobChunk);
            message.WriteByte(5);
            message.WriteVarUInt32((uint)(Limits.MaxBulkChunkBytes + Limits.BlobChunkHeaderSlack + 1));
            message.WriteByte((byte)CompressionKind.ManagedLz4);
            message.WriteByte(0);
            Assert.Throws<DecodeException>(() => codec.Decode(message.Buffer, 0, message.Length, scratch));
            Assert.Equal(0, spy.DecompressCalls);
        }

        [Test]
        public void MalformedEnvelopesAreRejected()
        {
            var codec = new EnvelopeCodec(Lz4);
            var scratch = new ByteWriter();
            Assert.Throws<DecodeException>(() => codec.Decode(Hex("05"), 0, 1, scratch), "shorter than the header");
            Assert.Throws<DecodeException>(() => codec.Decode(Hex("00 00"), 0, 2, scratch), "kind 0");
            Assert.Throws<DecodeException>(() => codec.Decode(Hex("05 08"), 0, 2, scratch), "unknown flag bit");
            Assert.Throws<DecodeException>(() => codec.Decode(Hex("05 04 01"), 0, 3, scratch), "size without compression");
            Assert.Throws<DecodeException>(() => codec.Decode(Hex("05 01 01 01 00"), 0, 5, scratch), "compression without size");
            Assert.Throws<DecodeException>(() => codec.Decode(Hex("05 03"), 0, 2, scratch), "fragment combined with compression");
            Assert.Throws<DecodeException>(() => codec.Decode(Hex("05 05 00 01 00"), 0, 5, scratch), "compressed empty body");
            Assert.Throws<DecodeException>(() => codec.Decode(Hex("05 05 05"), 0, 3, scratch), "no codec byte");
            Assert.Throws<DecodeException>(() => codec.Decode(Hex("05 05 05 01"), 0, 4, scratch), "no compressed data");
            Assert.Throws<DecodeException>(() => codec.Decode(Hex("05 05 05 09 00"), 0, 5, scratch), "unknown codec");
            Assert.Throws<DecodeException>(() => codec.Decode(Hex("05 05 05 02 00"), 0, 5, scratch), "Zstd without a Zstd compressor");
            Assert.Throws<DecodeException>(() => codec.Decode(Hex("05 05 05 01 50 61 61 61 61 61 61"), 0, 11, scratch), "block longer than declared");
            Assert.Throws<DecodeException>(() => codec.Decode(Hex("05 05 80 00 01 00"), 0, 6, scratch), "overlong size varint");
            var big = new byte[2 + Limits.MaxBodyBytes(MessageKind.Ping) + 1];
            big[0] = (byte)MessageKind.Ping;
            Assert.Throws<DecodeException>(() => codec.Decode(big, 0, big.Length, scratch), "uncompressed body above the kind limit");
        }

        [Test]
        public void UnknownKindsDecodeForTheRouterToDrop()
        {
            var codec = new EnvelopeCodec(Lz4);
            Envelope e = codec.Decode(Hex("c8 00 01 02"), 0, 4, new ByteWriter());
            Assert.Equal((MessageKind)200, e.Kind);
            Assert.False(Limits.IsKnownKind(e.Kind));
            Assert.True(Limits.IsKnownKind(MessageKind.Chat));
        }

        [Test]
        public void FragmentEnvelopesPassTheirPayloadThrough()
        {
            var codec = new EnvelopeCodec(Lz4);
            Envelope e = codec.Decode(Hex("05 02 aa bb"), 0, 4, new ByteWriter());
            Assert.Equal(EnvelopeFlags.Fragment, e.Flags);
            Assert.SequenceEqual(new byte[] { 0xAA, 0xBB }, Slice(e.Body, e.BodyOffset, e.BodyCount));
            var oversized = new byte[Limits.MaxMessageBytes + 1];
            oversized[0] = 5;
            oversized[1] = 2;
            Assert.Throws<DecodeException>(() => codec.Decode(oversized, 0, oversized.Length, new ByteWriter()), "a fragment above the Steam message cap");
        }

        [Test]
        public void Lz4BlocksDecodeWhicheverLz4CodecTheSenderNamed()
        {
            // a sender with native LZ4 (kind Lz4) and a receiver with ZSTD only: the built-in LZ4 decodes it
            var nativeLz4 = new SpyCompressor(Lz4, CompressionKind.Lz4);
            var sender = new EnvelopeCodec(nativeLz4);
            var output = new ByteWriter();
            byte[] body = Repeated(3, 5000);
            sender.Encode(MessageKind.Epoch, body, 0, body.Length, output);
            Assert.Equal((byte)CompressionKind.Lz4, output.Buffer[2 + WireFormatSize(body.Length)]);

            var zstdOnly = new SpyCompressor(Lz4, CompressionKind.Zstd);
            Envelope e = new EnvelopeCodec(zstdOnly).Decode(output.Buffer, 0, output.Length, new ByteWriter());
            Assert.SequenceEqual(body, Slice(e.Body, e.BodyOffset, e.BodyCount));
            Assert.Equal(0, zstdOnly.DecompressCalls, "the built-in LZ4 decoded it");

            // and a native-LZ4 receiver decodes ManagedLz4 blocks with its own compressor
            output.Reset();
            new EnvelopeCodec(Lz4).Encode(MessageKind.Epoch, body, 0, body.Length, output);
            e = new EnvelopeCodec(nativeLz4).Decode(output.Buffer, 0, output.Length, new ByteWriter());
            Assert.SequenceEqual(body, Slice(e.Body, e.BodyOffset, e.BodyCount));
            Assert.Equal(1, nativeLz4.DecompressCalls);
        }

        private static int WireFormatSize(int value)
        {
            var w = new ByteWriter();
            w.WriteVarUInt32((uint)value);
            return w.Length;
        }

        [Test]
        public void CompressorFailuresDegradeGracefully()
        {
            var failing = new SpyCompressor(Lz4, CompressionKind.Zstd) { FailCompress = true };
            var log = new ListLog();
            var codec = new EnvelopeCodec(failing, log);
            var output = new ByteWriter();
            byte[] body = Repeated(1, 1000);
            codec.Encode(MessageKind.Epoch, body, 0, body.Length, output);
            Assert.Equal((byte)EnvelopeFlags.None, output.Buffer[1], "sent uncompressed");
            Assert.Equal(1, log.Count(LogLevel.Warn));

            // a decompressor that throws something else than DecodeException still yields a DecodeException
            var crashing = new SpyCompressor(Lz4, CompressionKind.ManagedLz4) { FailDecompressWithIoError = true };
            output.Reset();
            new EnvelopeCodec(Lz4).Encode(MessageKind.Epoch, body, 0, body.Length, output);
            Assert.Throws<DecodeException>(() => new EnvelopeCodec(crashing).Decode(output.Buffer, 0, output.Length, new ByteWriter()));
        }

        [Test]
        public void ArgumentErrorsAreNotDecodeErrors()
        {
            var codec = new EnvelopeCodec(Lz4);
            Assert.Throws<ArgumentNullException>(() => codec.Decode(null, 0, 0, new ByteWriter()));
            Assert.Throws<ArgumentOutOfRangeException>(() => codec.Decode(new byte[4], 2, 3, new ByteWriter()));
            Assert.Throws<ArgumentNullException>(() => codec.Decode(new byte[4], 0, 4, null));
            Assert.Throws<ArgumentNullException>(() => codec.Encode(MessageKind.Ping, new byte[1], 0, 1, null));
        }
    }
}
