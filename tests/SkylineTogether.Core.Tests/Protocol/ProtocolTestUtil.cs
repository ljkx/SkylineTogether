using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>Helpers shared by the protocol tests: hex vectors, writers, a minimal type-set registry.</summary>
    internal static class ProtocolTestUtil
    {
        /// <summary>Parses "01 0a ff" (spaces, dashes and line breaks ignored) into bytes.</summary>
        public static byte[] Hex(string hex)
        {
            var digits = new StringBuilder();
            foreach (char c in hex)
            {
                if (Uri.IsHexDigit(c)) digits.Append(c);
                else if (c != ' ' && c != '-' && c != '\n' && c != '\r' && c != '|') throw new ArgumentException("not a hex digit: " + c);
            }
            if (digits.Length % 2 != 0) throw new ArgumentException("odd number of hex digits");
            var bytes = new byte[digits.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = byte.Parse(digits.ToString(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }

        public static string ToHex(byte[] data, int offset, int count)
        {
            var sb = new StringBuilder(count * 3);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[offset + i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static byte[] Bytes(ByteWriter w) => w.ToArray();

        public static byte[] Slice(byte[] data, int offset, int count)
        {
            var result = new byte[count];
            System.Buffer.BlockCopy(data, offset, result, 0, count);
            return result;
        }

        /// <summary>Asserts that <paramref name="actual"/> equals the hex vector.</summary>
        public static void AssertBytes(string expectedHex, byte[] actual, string message = null)
        {
            Assert.SequenceEqual(Hex(expectedHex), actual, message);
        }

        public static void AssertBytes(string expectedHex, ByteWriter actual, string message = null)
        {
            AssertBytes(expectedHex, actual.ToArray(), message);
        }

        public static ByteReader Reader(byte[] bytes) => new ByteReader(bytes, 0, bytes.Length);

        /// <summary>A unique, empty temp folder for spill tests (deleted by <see cref="DeleteFolder"/>).</summary>
        public static string NewTempFolder()
        {
            string path = Path.Combine(Path.GetTempPath(), "st-protocol-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        public static void DeleteFolder(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public static byte[] ReadAll(Stream stream)
        {
            using (var copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                return copy.ToArray();
            }
        }
    }

    /// <summary>A compressor that records calls and can be told to fail (envelope tests).</summary>
    internal sealed class SpyCompressor : ICompressor
    {
        private readonly ICompressor _inner;

        public SpyCompressor(ICompressor inner, CompressionKind kind)
        {
            _inner = inner;
            Kind = kind;
        }

        public CompressionKind Kind { get; }

        public int CompressCalls;
        public int DecompressCalls;
        public bool FailCompress;
        public bool FailDecompressWithIoError;

        public int MaxCompressedSize(int length) => _inner.MaxCompressedSize(length);

        public int Compress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset)
        {
            CompressCalls++;
            if (FailCompress) throw new InvalidOperationException("native compressor unavailable");
            return _inner.Compress(src, srcOffset, srcCount, dst, dstOffset);
        }

        public int Decompress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset, int expectedLength)
        {
            DecompressCalls++;
            if (FailDecompressWithIoError) throw new IOException("native decompressor crashed");
            return _inner.Decompress(src, srcOffset, srcCount, dst, dstOffset, expectedLength);
        }
    }
}
