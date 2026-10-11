using System;
using System.Collections.Generic;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Field encodings shared by the control-message codecs. Conventions: byte arrays are <c>varint length | bytes</c>
    /// (null is written as empty, read back as an empty array); identifier strings are <c>varint utf8Length | utf8</c>
    /// with a byte limit (null is written as empty, read back as ""); lists that may be null are
    /// <c>varint (count + 1) | items</c> with 0 = null; float fields are written as 0 when not finite and rejected when
    /// read; counts are checked against their limit and against the remaining bytes before anything is allocated.
    /// Writers throw <see cref="ArgumentException"/> for values beyond a limit (caller bugs; the writer may then hold
    /// part of the message, which the caller discards), readers throw <see cref="DecodeException"/>.
    /// </summary>
    internal static class MessageFields
    {
        internal static void WriteBytes(ByteWriter w, byte[] data, int max, string field)
        {
            int length = data == null ? 0 : data.Length;
            if (length > max)
            {
                throw new ArgumentException(field + " has " + length.ToString(CultureInfo.InvariantCulture) + " bytes, more than " + max.ToString(CultureInfo.InvariantCulture), field);
            }
            w.WriteVarUInt32((uint)length);
            if (length > 0) w.WriteBytes(data, 0, length);
        }

        internal static byte[] ReadBytes(ByteReader r, int max, string field)
        {
            uint length = r.ReadVarUInt32();
            if (length > (uint)max) throw new DecodeException(field + ": " + WireFormat.Format(length) + " bytes exceed the limit of " + max.ToString(CultureInfo.InvariantCulture));
            if (length > (uint)r.Remaining) throw new DecodeException(field + ": " + WireFormat.Format(length) + " bytes overrun the message");
            if (length == 0) return Array.Empty<byte>();
            var data = new byte[length];
            r.ReadBytes(data, 0, (int)length);
            return data;
        }

        internal static void WriteString(ByteWriter w, string value, int maxBytes, string field)
        {
            try
            {
                w.WriteString(value, maxBytes);
            }
            catch (ArgumentException e)
            {
                throw new ArgumentException(field + ": " + e.Message, field, e);
            }
        }

        internal static string ReadString(ByteReader r, int maxBytes, string field)
        {
            try
            {
                return r.ReadString(maxBytes);
            }
            catch (DecodeException e)
            {
                throw new DecodeException(field + ": " + e.Message, e);
            }
        }

        /// <summary>Free text: truncated to <paramref name="maxChars"/> when written.</summary>
        internal static void WriteText(ByteWriter w, string value, int maxChars)
        {
            w.WriteString(ProtocolText.Truncate(value, maxChars), maxChars * ProtocolText.MaxUtf8BytesPerChar);
        }

        /// <summary>Free text: rejected above <paramref name="maxChars"/>, control characters stripped.</summary>
        internal static string ReadText(ByteReader r, int maxChars, string field)
        {
            string raw = ReadString(r, maxChars * ProtocolText.MaxUtf8BytesPerChar, field);
            if (raw.Length > maxChars)
            {
                throw new DecodeException(field + ": " + raw.Length.ToString(CultureInfo.InvariantCulture) + " characters exceed the limit of " + maxChars.ToString(CultureInfo.InvariantCulture));
            }
            return ProtocolText.Sanitize(raw);
        }

        internal static void WriteStringList(ByteWriter w, IList<string> list, int maxCount, int maxBytesEach, string field)
        {
            if (list == null)
            {
                w.WriteVarUInt32(0);
                return;
            }
            if (list.Count > maxCount) throw new ArgumentException(field + " has " + list.Count.ToString(CultureInfo.InvariantCulture) + " entries, more than " + maxCount.ToString(CultureInfo.InvariantCulture), field);
            w.WriteVarUInt32((uint)list.Count + 1);
            for (int i = 0; i < list.Count; i++) WriteString(w, list[i], maxBytesEach, field);
        }

        internal static List<string> ReadStringList(ByteReader r, int maxCount, int maxBytesEach, string field)
        {
            int count = ReadNullableCount(r, maxCount, 1, field);
            if (count < 0) return null;
            var list = new List<string>(count);
            for (int i = 0; i < count; i++) list.Add(ReadString(r, maxBytesEach, field));
            return list;
        }

        internal static void WriteStringArray(ByteWriter w, string[] array, int maxCount, int maxBytesEach, string field)
        {
            WriteStringList(w, array, maxCount, maxBytesEach, field);
        }

        internal static string[] ReadStringArray(ByteReader r, int maxCount, int maxBytesEach, string field)
        {
            int count = ReadNullableCount(r, maxCount, 1, field);
            if (count < 0) return null;
            var array = new string[count];
            for (int i = 0; i < count; i++) array[i] = ReadString(r, maxBytesEach, field);
            return array;
        }

        /// <summary>Writes <c>count + 1</c>, or 0 for null; validates the limit.</summary>
        internal static void WriteNullableCount(ByteWriter w, int count, bool isNull, int maxCount, string field)
        {
            if (isNull)
            {
                w.WriteVarUInt32(0);
                return;
            }
            if (count > maxCount) throw new ArgumentException(field + " has " + count.ToString(CultureInfo.InvariantCulture) + " entries, more than " + maxCount.ToString(CultureInfo.InvariantCulture), field);
            w.WriteVarUInt32((uint)count + 1);
        }

        /// <summary>Reads <c>count + 1</c> (0 = null → returns −1); checks the limit and the remaining bytes.</summary>
        internal static int ReadNullableCount(ByteReader r, int maxCount, int minBytesEach, string field)
        {
            uint value = r.ReadVarUInt32();
            if (value == 0) return -1;
            return CheckCount(r, value - 1, maxCount, minBytesEach, field);
        }

        internal static void WriteCount(ByteWriter w, int count, int maxCount, string field)
        {
            if (count > maxCount) throw new ArgumentException(field + " has " + count.ToString(CultureInfo.InvariantCulture) + " entries, more than " + maxCount.ToString(CultureInfo.InvariantCulture), field);
            w.WriteVarUInt32((uint)count);
        }

        internal static int ReadCount(ByteReader r, int maxCount, int minBytesEach, string field)
        {
            return CheckCount(r, r.ReadVarUInt32(), maxCount, minBytesEach, field);
        }

        private static int CheckCount(ByteReader r, uint count, int maxCount, int minBytesEach, string field)
        {
            if (count > (uint)maxCount) throw new DecodeException(field + ": " + WireFormat.Format(count) + " entries exceed the limit of " + maxCount.ToString(CultureInfo.InvariantCulture));
            if ((long)count * minBytesEach > r.Remaining) throw new DecodeException(field + ": " + WireFormat.Format(count) + " entries overrun the message");
            return (int)count;
        }

        internal static void WriteFloat(ByteWriter w, float value) => w.WriteSingle(WireFormat.Finite(value));

        internal static float ReadFloat(ByteReader r, string field)
        {
            float value = r.ReadSingle();
            if (!WireFormat.IsFinite(value)) throw new DecodeException(field + " is not finite");
            return value;
        }

        internal static void WriteDouble(ByteWriter w, double value) => w.WriteDouble(WireFormat.Finite(value));

        internal static double ReadDouble(ByteReader r, string field)
        {
            double value = r.ReadDouble();
            if (!WireFormat.IsFinite(value)) throw new DecodeException(field + " is not finite");
            return value;
        }

        internal static void WriteFloats(ByteWriter w, float[] values, int maxCount, string field)
        {
            int count = values == null ? 0 : values.Length;
            WriteCount(w, count, maxCount, field);
            for (int i = 0; i < count; i++) WriteFloat(w, values[i]);
        }

        /// <summary>Reads a float array; <paramref name="reuse"/> is filled and returned when its length equals the count.</summary>
        internal static float[] ReadFloats(ByteReader r, int maxCount, string field, float[] reuse = null)
        {
            int count = ReadCount(r, maxCount, 4, field);
            if (count == 0) return Array.Empty<float>();
            float[] values = reuse != null && reuse.Length == count ? reuse : new float[count];
            for (int i = 0; i < count; i++) values[i] = ReadFloat(r, field);
            return values;
        }

        /// <summary>A slot as a varint; 0 (none) only where allowed.</summary>
        internal static NetSlot ReadSlot(ByteReader r, bool allowNone, string field)
        {
            uint value = r.ReadVarUInt32();
            if (value > NetSlot.MaxValue) throw new DecodeException(field + ": slot " + WireFormat.Format(value) + " exceeds 0xFFFFFF");
            if (value == 0 && !allowNone) throw new DecodeException(field + ": slot 0");
            return new NetSlot(value);
        }

        internal static void WritePrefab(ByteWriter w, PrefabRank prefab) => w.WriteVarUInt32(unchecked((uint)(prefab.Value + 1)));

        internal static PrefabRank ReadPrefab(ByteReader r, string field)
        {
            uint plusOne = r.ReadVarUInt32();
            if (plusOne > 0x80000000u) throw new DecodeException(field + ": prefab rank exceeds int.MaxValue");
            return plusOne == 0 ? PrefabRank.None : new PrefabRank((int)(plusOne - 1));
        }

        internal static ReplicationClass ReadClass(ByteReader r, string field)
        {
            byte value = r.ReadByte();
            if (value > (byte)ReplicationClass.OnDemand) throw new DecodeException(field + ": undefined replication class " + value.ToString(CultureInfo.InvariantCulture));
            return (ReplicationClass)value;
        }

        internal static void WriteClass(ByteWriter w, ReplicationClass cls, string field)
        {
            if ((byte)cls > (byte)ReplicationClass.OnDemand) throw new ArgumentException("undefined replication class", field);
            w.WriteByte((byte)cls);
        }

        internal static int ReadBucket(ByteReader r, string field)
        {
            uint value = r.ReadVarUInt32();
            if (value > Limits.MaxBucket) throw new DecodeException(field + ": bucket " + WireFormat.Format(value) + " exceeds " + Limits.MaxBucket.ToString(CultureInfo.InvariantCulture));
            return (int)value;
        }

        internal static void WriteBucket(ByteWriter w, int bucket, string field)
        {
            if (bucket < 0 || bucket > Limits.MaxBucket) throw new ArgumentOutOfRangeException(field, bucket, "0 ... " + Limits.MaxBucket.ToString(CultureInfo.InvariantCulture));
            w.WriteVarUInt32((uint)bucket);
        }

        /// <summary>A flags byte with only the bits of <paramref name="known"/>.</summary>
        internal static byte ReadFlags(ByteReader r, byte known, string field)
        {
            byte value = r.ReadByte();
            if ((value & ~known) != 0) throw new DecodeException(field + ": unknown flag bits 0x" + value.ToString("X2", CultureInfo.InvariantCulture));
            return value;
        }

        internal static void CheckMessage(object message, string name)
        {
            if (message == null) throw new ArgumentNullException(name);
        }

        /// <summary>Requires that <paramref name="r"/> consumed its whole window (one message per body).</summary>
        internal static void EnsureEnd(ByteReader r, string message)
        {
            if (!r.IsAtEnd) throw new DecodeException(message + ": " + r.Remaining.ToString(CultureInfo.InvariantCulture) + " trailing byte(s)");
        }
    }
}
