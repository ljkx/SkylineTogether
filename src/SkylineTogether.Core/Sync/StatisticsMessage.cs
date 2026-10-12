using System;
using System.Collections.Generic;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Sync
{
    /// <summary>One city statistic series: its key (<c>StatisticType</c> value and parameter) and its samples.</summary>
    public sealed class StatisticSeries
    {
        public int Type;
        public int Parameter;
        /// <summary><c>CityStatistic.m_Value</c> per sample, oldest first.</summary>
        public double[] Values = Array.Empty<double>();
        /// <summary><c>CityStatistic.m_TotalValue</c> per sample (same length as <see cref="Values"/>).</summary>
        public double[] Totals = Array.Empty<double>();

        /// <summary>A content hash of the series (change detection on the host).</summary>
        public ulong ContentHash()
        {
            ulong h = 14695981039346656037UL;
            h = Mix(h, (ulong)(uint)Type);
            h = Mix(h, (ulong)(uint)Parameter);
            int n = Math.Min(Values.Length, Totals.Length);
            h = Mix(h, (ulong)n);
            for (int i = 0; i < n; i++)
            {
                h = Mix(h, unchecked((ulong)BitConverter.DoubleToInt64Bits(Values[i])));
                h = Mix(h, unchecked((ulong)BitConverter.DoubleToInt64Bits(Totals[i])));
            }
            return h;
        }

        private static ulong Mix(ulong h, ulong v) => unchecked((h ^ v) * 1099511628211UL + (h >> 31));
    }

    /// <summary>
    /// Wire format of <see cref="MessageKind.Statistics"/> (host → client): the statistics panel's history, i.e. the
    /// <c>CityStatisticsSystem</c> sample counter and the <c>CityStatistic</c> buffers of the statistic entities, keyed by
    /// (type, parameter) rather than by entity, because every peer creates its own statistic entities at load
    /// (<c>CityStatisticsSystem.InitializeLookup</c>) and a client must never create or destroy them.
    /// <code>
    /// u8 version | u8 type (1) | i32 sampleCount | u32 lastSampleFrame | varuint n
    /// | n × (varint statType | varint parameter | varuint count | count × (f64 value | f64 total))
    /// </code>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class StatisticsMessage
    {
        public const byte Version = 1;
        public const byte TypeSeries = 1;

        /// <summary>Series per message at most.</summary>
        public const int MaxSeries = 4096;

        /// <summary>Samples per series at most (the game keeps about 32, a day's worth).</summary>
        public const int MaxSamples = 4096;

        private const string What = "statistics message";

        public static void Write(ByteWriter w, int sampleCount, uint lastSampleFrame, IReadOnlyList<StatisticSeries> series)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (series == null) throw new ArgumentNullException(nameof(series));
            if (series.Count > MaxSeries) throw new ArgumentException("too many series", nameof(series));
            w.WriteByte(Version);
            w.WriteByte(TypeSeries);
            w.WriteInt32(sampleCount);
            w.WriteUInt32(lastSampleFrame);
            w.WriteVarUInt32((uint)series.Count);
            foreach (StatisticSeries s in series)
            {
                int n = Math.Min(s.Values.Length, s.Totals.Length);
                if (n > MaxSamples) throw new ArgumentException("a series has too many samples", nameof(series));
                w.WriteVarInt32(s.Type);
                w.WriteVarInt32(s.Parameter);
                w.WriteVarUInt32((uint)n);
                for (int i = 0; i < n; i++)
                {
                    w.WriteDouble(s.Values[i]);
                    w.WriteDouble(s.Totals[i]);
                }
            }
        }

        public static List<StatisticSeries> Read(byte[] buffer, int offset, int count, out int sampleCount, out uint lastSampleFrame)
        {
            var r = new ByteReader(buffer, offset, count);
            byte version = r.ReadByte();
            if (version != Version) throw new DecodeException(What + " version " + version.ToString(CultureInfo.InvariantCulture) + " (expected " + Version + ")");
            byte type = r.ReadByte();
            if (type != TypeSeries) throw new DecodeException(What + ": unknown type " + type.ToString(CultureInfo.InvariantCulture));
            sampleCount = r.ReadInt32();
            lastSampleFrame = r.ReadUInt32();
            uint n = r.ReadVarUInt32();
            if (n > MaxSeries || n > (uint)(r.Remaining / 3)) throw new DecodeException(What + " with " + n + " series");
            var list = new List<StatisticSeries>((int)n);
            for (uint i = 0; i < n; i++)
            {
                var s = new StatisticSeries { Type = r.ReadVarInt32(), Parameter = r.ReadVarInt32() };
                uint samples = r.ReadVarUInt32();
                if (samples > MaxSamples || samples > (uint)(r.Remaining / 16)) throw new DecodeException(What + ": series with " + samples + " samples");
                s.Values = new double[samples];
                s.Totals = new double[samples];
                for (int k = 0; k < samples; k++)
                {
                    s.Values[k] = r.ReadDouble();
                    s.Totals[k] = r.ReadDouble();
                }
                list.Add(s);
            }
            if (!r.IsAtEnd) throw new DecodeException(What + " has " + r.Remaining + " trailing bytes");
            return list;
        }
    }
}
