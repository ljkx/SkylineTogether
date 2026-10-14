using System;
using System.IO;
using System.IO.Compression;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Fields
{
    /// <summary>
    /// A real compressor for the codec tests (raw deflate from the .NET runtime), reporting itself as
    /// <see cref="CompressionKind.Zstd"/> by default so the body framing sees a negotiated codec.
    /// </summary>
    internal sealed class DeflateTestCompressor : ICompressor
    {
        private readonly CompressionKind _kind;

        public DeflateTestCompressor(CompressionKind kind = CompressionKind.Zstd)
        {
            _kind = kind;
        }

        public int CompressCalls { get; private set; }

        public int DecompressCalls { get; private set; }

        public CompressionKind Kind => _kind;

        public int MaxCompressedSize(int length) => length + (length >> 1) + 256;

        public int Compress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset)
        {
            CompressCalls++;
            using (var memory = new MemoryStream())
            {
                using (var deflate = new DeflateStream(memory, CompressionLevel.Optimal, true))
                {
                    deflate.Write(src, srcOffset, srcCount);
                }
                int length = (int)memory.Length;
                if (dstOffset < 0 || dstOffset > dst.Length - length) throw new ArgumentException("destination too small");
                Buffer.BlockCopy(memory.GetBuffer(), 0, dst, dstOffset, length);
                return length;
            }
        }

        public int Decompress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset, int expectedLength)
        {
            DecompressCalls++;
            if (dstOffset < 0 || expectedLength < 0 || dstOffset > dst.Length - expectedLength) throw new DecodeException("destination too small");
            try
            {
                using (var memory = new MemoryStream(src, srcOffset, srcCount, false))
                using (var deflate = new DeflateStream(memory, CompressionMode.Decompress))
                {
                    int total = 0;
                    while (total < expectedLength)
                    {
                        int read = deflate.Read(dst, dstOffset + total, expectedLength - total);
                        if (read <= 0) break;
                        total += read;
                    }
                    if (total != expectedLength || deflate.ReadByte() >= 0) throw new DecodeException("deflate length mismatch");
                    return expectedLength;
                }
            }
            catch (InvalidDataException e)
            {
                throw new DecodeException("corrupt deflate data: " + e.Message);
            }
        }
    }

    /// <summary>A compressor whose output is never smaller than its input (forces the uncompressed body path).</summary>
    internal sealed class ExpandingTestCompressor : ICompressor
    {
        public int CompressCalls { get; private set; }

        public CompressionKind Kind => CompressionKind.Zstd;

        public int MaxCompressedSize(int length) => length + 16;

        public int Compress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset)
        {
            CompressCalls++;
            Buffer.BlockCopy(src, srcOffset, dst, dstOffset + 8, srcCount);
            return srcCount + 8;
        }

        public int Decompress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset, int expectedLength)
        {
            throw new InvalidOperationException("never called: the codec must not use a larger compressed form");
        }
    }

    /// <summary>Compresses with deflate but fails decompression with an exception that is not a DecodeException.</summary>
    internal sealed class ThrowingDecompressor : ICompressor
    {
        private readonly DeflateTestCompressor _inner = new DeflateTestCompressor();

        public CompressionKind Kind => _inner.Kind;

        public int MaxCompressedSize(int length) => _inner.MaxCompressedSize(length);

        public int Compress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset) => _inner.Compress(src, srcOffset, srcCount, dst, dstOffset);

        public int Decompress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset, int expectedLength)
        {
            throw new InvalidOperationException("simulated native failure");
        }
    }

    /// <summary>The failure a <see cref="FaultyCompressor"/> simulates.</summary>
    internal enum CompressorFault
    {
        ThrowInBound,
        ZeroBound,
        HugeBound,
        ThrowInCompress,
        NegativeLength,
        LengthAboveBound,
        OutOfMemoryInCompress
    }

    /// <summary>A compressor that fails in one configurable way (a broken native codec); decompression is never needed.</summary>
    internal sealed class FaultyCompressor : ICompressor
    {
        private readonly CompressorFault _fault;

        public FaultyCompressor(CompressorFault fault)
        {
            _fault = fault;
        }

        public CompressionKind Kind => CompressionKind.Zstd;

        public int MaxCompressedSize(int length)
        {
            switch (_fault)
            {
                case CompressorFault.ThrowInBound: throw new InvalidOperationException("simulated bound failure");
                case CompressorFault.ZeroBound: return 0;
                case CompressorFault.HugeBound: return int.MaxValue;
                default: return length + 64;
            }
        }

        public int Compress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset)
        {
            switch (_fault)
            {
                case CompressorFault.ThrowInCompress: throw new InvalidOperationException("simulated native failure");
                case CompressorFault.NegativeLength: return -5;
                case CompressorFault.LengthAboveBound: return srcCount + 1000;
                case CompressorFault.OutOfMemoryInCompress: throw new OutOfMemoryException("simulated");
                default: return 1;
            }
        }

        public int Decompress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset, int expectedLength)
        {
            throw new InvalidOperationException("never called: a failed compression must leave the body uncompressed");
        }
    }

    /// <summary>A read-only stream view that hands out at most a few bytes per Read and optionally hides seeking.</summary>
    internal sealed class TrickleStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _maxRead;
        private readonly bool _seekable;

        public TrickleStream(Stream inner, int maxRead, bool seekable)
        {
            _inner = inner;
            _maxRead = maxRead;
            _seekable = seekable;
        }

        public override bool CanRead => true;

        public override bool CanSeek => _seekable;

        public override bool CanWrite => false;

        public override long Length => _seekable ? _inner.Length : throw new NotSupportedException();

        public override long Position
        {
            get => _seekable ? _inner.Position : throw new NotSupportedException();
            set
            {
                if (!_seekable) throw new NotSupportedException();
                _inner.Position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, _maxRead));

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!_seekable) throw new NotSupportedException();
            return _inner.Seek(offset, origin);
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal static class FieldTestSupport
    {
        /// <summary>The bytes a writer action produces.</summary>
        public static byte[] Write(Action<ByteWriter> action)
        {
            var writer = new ByteWriter();
            action(writer);
            return writer.ToArray();
        }

        /// <summary>
        /// Decodes every proper prefix of <paramref name="encoded"/> and asserts that each one fails with a
        /// <see cref="DecodeException"/> and nothing else (the decoder rule of ARCHITECTURE.md#code-rules).
        /// </summary>
        public static void AssertEveryTruncationFails(byte[] encoded, Action<ByteReader> decode, string what)
        {
            for (int length = 0; length < encoded.Length; length++)
            {
                var reader = new ByteReader(encoded, 0, length);
                try
                {
                    decode(reader);
                    Assert.Fail(what + ": a prefix of " + length + " of " + encoded.Length + " bytes decoded without error");
                }
                catch (DecodeException)
                {
                }
                catch (AssertionException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Assert.Fail(what + ": a prefix of " + length + " bytes threw " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        /// <summary>
        /// Decodes <paramref name="rounds"/> randomly corrupted copies; each must either decode or throw a
        /// <see cref="DecodeException"/>, never anything else.
        /// </summary>
        public static int AssertCorruptionOnlyThrowsDecodeException(byte[] encoded, Action<ByteReader> decode, ulong seed, int rounds, string what)
        {
            var random = new DeterministicRandom(seed);
            int failures = 0;
            var copy = new byte[encoded.Length];
            for (int round = 0; round < rounds; round++)
            {
                Buffer.BlockCopy(encoded, 0, copy, 0, encoded.Length);
                int flips = 1 + random.Next(4);
                for (int f = 0; f < flips; f++)
                {
                    int position = random.Next(copy.Length);
                    copy[position] ^= (byte)(1 + random.Next(255));
                }
                try
                {
                    decode(new ByteReader(copy, 0, copy.Length));
                }
                catch (DecodeException)
                {
                    failures++;
                }
                catch (Exception e)
                {
                    Assert.Fail(what + ": corruption round " + round + " threw " + e.GetType().Name + ": " + e.Message);
                }
            }
            return failures;
        }
    }
}
