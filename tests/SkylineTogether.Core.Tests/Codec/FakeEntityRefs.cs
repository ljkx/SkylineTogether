using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Codec;

namespace SkylineTogether.Core.Tests.Codec
{
    /// <summary>
    /// A fake entity map for both codec seams: bound entities map to slots, prefab entities to ranks, derived entities
    /// to keys; everything else encodes as <see cref="NetRef.Local"/>. Decoding reports unbound slots and unknown
    /// derived keys as missing. Every call is recorded.
    /// </summary>
    internal sealed class FakeEntityRefs : IEntityRefWriter, IEntityRefReader
    {
        private readonly Dictionary<(int, int), NetRef> _byEntity = new Dictionary<(int, int), NetRef>();
        private readonly Dictionary<int, NetRef> _byIndex = new Dictionary<int, NetRef>();
        private readonly Dictionary<NetRef, (int, int)> _byRef = new Dictionary<NetRef, (int, int)>();

        public readonly List<(int Index, int Version, bool IgnoreVersion)> EncodeCalls = new List<(int, int, bool)>();
        public readonly List<NetRef> DecodeCalls = new List<NetRef>();

        /// <summary>The handle returned for a missing reference (the null entity unless a test sets a stand-in).</summary>
        public (int Index, int Version) MissingStandIn = (0, 0);

        public FakeEntityRefs Bind(int index, int version, NetRef reference)
        {
            _byEntity[(index, version)] = reference;
            _byIndex[index] = reference;
            _byRef[reference] = (index, version);
            return this;
        }

        public FakeEntityRefs BindSlot(int index, int version, uint slot) => Bind(index, version, NetRef.ForSlot(new NetSlot(slot)));

        public NetRef EncodeEntity(int index, int version, bool ignoreVersion)
        {
            EncodeCalls.Add((index, version, ignoreVersion));
            if (ignoreVersion)
            {
                if (index == 0) return NetRef.Null;   // a default PathNode (IEntityRefWriter contract)
                return _byIndex.TryGetValue(index, out NetRef byIndex) ? byIndex : NetRef.Local;
            }
            if (index == 0 && version == 0) return NetRef.Null;
            return _byEntity.TryGetValue((index, version), out NetRef reference) ? reference : NetRef.Local;
        }

        public (int index, int version) DecodeEntity(in NetRef value, out bool missing, out NetSlot missingSlot)
        {
            DecodeCalls.Add(value);
            missing = false;
            missingSlot = NetSlot.None;
            if (value.Kind == NetRefKind.Null || value.Kind == NetRefKind.Local) return (0, 0);
            if (_byRef.TryGetValue(value, out (int, int) entity)) return entity;
            missing = true;
            if (value.Kind == NetRefKind.Slot) missingSlot = value.Slot;
            else if (value.Kind == NetRefKind.Derived) missingSlot = value.Key.Owner;
            return MissingStandIn;
        }
    }

    /// <summary>Hex helpers and one-line writer/reader set-ups for the codec tests.</summary>
    internal static class CodecTestUtil
    {
        public static byte[] Write(Action<CanonicalWriter> write, IEntityRefWriter entities = null)
        {
            var target = new ByteWriter(16);
            var writer = new CanonicalWriter(target, entities ?? new FakeEntityRefs());
            write(writer);
            if (writer.OpenBlockCount != 0) throw new InvalidOperationException("the test left a block open");
            return target.ToArray();
        }

        public static string WriteHex(Action<CanonicalWriter> write, IEntityRefWriter entities = null) => Hex(Write(write, entities));

        public static CanonicalReader Reader(byte[] bytes, IEntityRefReader entities = null)
        {
            var reader = new CanonicalReader(entities ?? new FakeEntityRefs());
            reader.Open(bytes, 0, bytes.Length);
            return reader;
        }

        public static CanonicalReader Reader(string hex, IEntityRefReader entities = null) => Reader(Bytes(hex), entities);

        public static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 3);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static byte[] Bytes(string hex)
        {
            string compact = hex.Replace(" ", string.Empty);
            if (compact.Length % 2 != 0) throw new ArgumentException("odd hex length", nameof(hex));
            var bytes = new byte[compact.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = byte.Parse(compact.Substring(2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            return bytes;
        }

        public static uint Bits(float f) => unchecked((uint)BitConverter.SingleToInt32Bits(f));

        public static float FromBits(uint bits) => BitConverter.Int32BitsToSingle(unchecked((int)bits));

        public static ulong Bits(double d) => unchecked((ulong)BitConverter.DoubleToInt64Bits(d));

        public static double FromBits(ulong bits) => BitConverter.Int64BitsToDouble(unchecked((long)bits));
    }
}
