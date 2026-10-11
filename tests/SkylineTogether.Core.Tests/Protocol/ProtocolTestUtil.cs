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

    /// <summary>
    /// A minimal session-global type-set registry for the TSET tests (the real one is core-identity's
    /// <c>TypeSetRegistry</c>). Keyframe layout of ARCH §4.6.3: <c>varint count, repeat { varint typeSetId, varint n,
    /// typeRank... }</c>.
    /// </summary>
    internal sealed class FakeTypeSetRegistry : ITypeSetRegistry
    {
        private readonly SortedDictionary<int, TypeRank[]> _sets = new SortedDictionary<int, TypeRank[]>();

        public int Count => _sets.Count;

        public TypeSetId GetOrAdd(IReadOnlyList<TypeRank> sortedTypes, out bool added)
        {
            foreach (KeyValuePair<int, TypeRank[]> pair in _sets)
            {
                if (Same(pair.Value, sortedTypes))
                {
                    added = false;
                    return new TypeSetId(pair.Key);
                }
            }
            int id = _sets.Count + 1;
            _sets[id] = Copy(sortedTypes);
            added = true;
            return new TypeSetId(id);
        }

        public bool TryGet(TypeSetId id, out IReadOnlyList<TypeRank> types)
        {
            bool found = _sets.TryGetValue(id.Value, out TypeRank[] set);
            types = set;
            return found;
        }

        public IReadOnlyList<TypeRank> Get(TypeSetId id)
        {
            if (!_sets.TryGetValue(id.Value, out TypeRank[] set)) throw new UnknownTypeSetException(id);
            return set;
        }

        public int OrdinalOf(TypeSetId set, TypeRank type)
        {
            if (!_sets.TryGetValue(set.Value, out TypeRank[] types)) return -1;
            return Array.IndexOf(types, type);
        }

        public bool Define(TypeSetId id, IReadOnlyList<TypeRank> sortedTypes)
        {
            if (_sets.TryGetValue(id.Value, out TypeRank[] known)) return Same(known, sortedTypes);
            _sets[id.Value] = Copy(sortedTypes);
            return true;
        }

        public void WriteKeyframe(ByteWriter writer)
        {
            writer.WriteVarUInt32((uint)_sets.Count);
            foreach (KeyValuePair<int, TypeRank[]> pair in _sets)
            {
                writer.WriteVarUInt32((uint)pair.Key);
                writer.WriteVarUInt32((uint)pair.Value.Length);
                foreach (TypeRank rank in pair.Value) writer.WriteVarUInt32(rank.Value);
            }
        }

        public void ReadKeyframe(ByteReader reader)
        {
            uint count = reader.ReadVarUInt32();
            if (count > 100000) throw new DecodeException("keyframe: too many sets");
            for (uint i = 0; i < count; i++)
            {
                uint id = reader.ReadVarUInt32();
                uint n = reader.ReadVarUInt32();
                if (id == 0 || id > int.MaxValue || n > 64) throw new DecodeException("keyframe: bad set");
                var types = new TypeRank[n];
                for (int t = 0; t < n; t++)
                {
                    uint rank = reader.ReadVarUInt32();
                    if (rank > ushort.MaxValue) throw new DecodeException("keyframe: bad rank");
                    types[t] = new TypeRank((ushort)rank);
                }
                if (!Define(new TypeSetId((int)id), types)) throw new DecodeException("keyframe contradicts a known definition of " + id);
            }
        }

        public void Clear() => _sets.Clear();

        private static TypeRank[] Copy(IReadOnlyList<TypeRank> types)
        {
            var copy = new TypeRank[types.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = types[i];
            return copy;
        }

        private static bool Same(IReadOnlyList<TypeRank> a, IReadOnlyList<TypeRank> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
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
