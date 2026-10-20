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

        /// <summary>
        /// The samples are the newest ones appended to the series (<see cref="StatisticsMessage.TypeMixed"/>); false: they
        /// are the whole series (its newest <see cref="StatisticsMessage.MaxSamples"/>).
        /// </summary>
        public bool Append;

        /// <summary>The length of the host's whole buffer the samples end at (in <see cref="StatisticsMessage.TypeMixed"/>).</summary>
        public long TotalLength;

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

        /// <summary>
        /// Whole series and appends: after the counters, per series
        /// <c>u8 append | varint statType | varint parameter | varuint64 totalLength | varuint count | count × samples</c>.
        /// The host sends the samples appended since its last message instead of each series' whole history at every
        /// sample (<see cref="StatisticsDelta"/>).
        /// </summary>
        public const byte TypeMixed = 2;

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

        /// <summary>Writes whole series and appends (<see cref="TypeMixed"/>).</summary>
        public static void WriteMixed(ByteWriter w, int sampleCount, uint lastSampleFrame, IReadOnlyList<StatisticSeries> series)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (series == null) throw new ArgumentNullException(nameof(series));
            if (series.Count > MaxSeries) throw new ArgumentException("too many series", nameof(series));
            w.WriteByte(Version);
            w.WriteByte(TypeMixed);
            w.WriteInt32(sampleCount);
            w.WriteUInt32(lastSampleFrame);
            w.WriteVarUInt32((uint)series.Count);
            foreach (StatisticSeries s in series)
            {
                int n = Math.Min(s.Values.Length, s.Totals.Length);
                if (n > MaxSamples || s.TotalLength < n) throw new ArgumentException("a series is out of range", nameof(series));
                w.WriteBool(s.Append);
                w.WriteVarInt32(s.Type);
                w.WriteVarInt32(s.Parameter);
                w.WriteVarUInt64((ulong)s.TotalLength);
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
            if (type != TypeSeries && type != TypeMixed) throw new DecodeException(What + ": unknown type " + type.ToString(CultureInfo.InvariantCulture));
            bool mixed = type == TypeMixed;
            sampleCount = r.ReadInt32();
            lastSampleFrame = r.ReadUInt32();
            uint n = r.ReadVarUInt32();
            if (n > MaxSeries || n > (uint)(r.Remaining / 3)) throw new DecodeException(What + " with " + n + " series");
            var list = new List<StatisticSeries>((int)n);
            for (uint i = 0; i < n; i++)
            {
                bool append = mixed && r.ReadBool();
                var s = new StatisticSeries { Append = append, Type = r.ReadVarInt32(), Parameter = r.ReadVarInt32() };
                ulong total = mixed ? r.ReadVarUInt64() : 0;
                if (total > long.MaxValue / 2) throw new DecodeException(What + ": series of " + total + " samples");
                s.TotalLength = (long)total;
                uint samples = r.ReadVarUInt32();
                if (samples > MaxSamples || samples > (uint)(r.Remaining / 16)) throw new DecodeException(What + ": series with " + samples + " samples");
                s.Values = new double[samples];
                s.Totals = new double[samples];
                for (int k = 0; k < samples; k++)
                {
                    s.Values[k] = r.ReadDouble();
                    s.Totals[k] = r.ReadDouble();
                }
                if (mixed && s.TotalLength < samples) throw new DecodeException(What + ": " + samples + " samples ending at " + s.TotalLength);
                if (!mixed) s.TotalLength = samples;
                list.Add(s);
            }
            if (!r.IsAtEnd) throw new DecodeException(What + " has " + r.Remaining + " trailing bytes");
            return list;
        }
    }

    /// <summary>Host: what the peers' copies of one series end at after the last message (<see cref="StatisticsDelta"/>).</summary>
    public struct StatisticSentState
    {
        public bool Valid;
        /// <summary>The length of the host's whole buffer when it was last sent.</summary>
        public long Total;
        /// <summary>The last sample sent (its bits), the seam the next append continues.</summary>
        public long LastValueBits, LastTotalBits;
    }

    /// <summary>
    /// Statistics are appended to, never rewritten (<c>CityStatisticsSystem</c> adds a sample per interval), so after the
    /// whole series only the new samples travel (the whole history went to every peer at every sample: megabytes per
    /// sampling in an older city). Every message names the host buffer length its samples end at, so a receiver appends
    /// exactly what it lacks, whatever it held before (a joiner's snapshot, a message that arrived while it joined). A
    /// series that does not continue the last one sent (shorter, or the last sent sample changed) is sent whole. Pure.
    /// </summary>
    public static class StatisticsDelta
    {
        /// <summary>
        /// Host: what to send for the series' current window (<paramref name="window"/>, its newest samples, with
        /// <see cref="StatisticSeries.TotalLength"/>): an append, the whole window, or null when nothing changed; and the state after it.
        /// </summary>
        public static StatisticSeries Plan(StatisticSentState sent, StatisticSeries window, out StatisticSentState next)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            int n = Math.Min(window.Values.Length, window.Totals.Length);
            long total = window.TotalLength;
            next = new StatisticSentState { Valid = true, Total = total };
            if (n > 0)
            {
                next.LastValueBits = BitConverter.DoubleToInt64Bits(window.Values[n - 1]);
                next.LastTotalBits = BitConverter.DoubleToInt64Bits(window.Totals[n - 1]);
            }
            if (!sent.Valid || total < n) return Whole(window, n);
            long appended = total - sent.Total;
            if (appended == 0 && next.LastValueBits == sent.LastValueBits && next.LastTotalBits == sent.LastTotalBits) return null;
            if (appended <= 0 || appended >= n) return Whole(window, n);
            // the seam: the last sample sent is still there, unchanged
            int seam = n - 1 - (int)appended;
            if (BitConverter.DoubleToInt64Bits(window.Values[seam]) != sent.LastValueBits || BitConverter.DoubleToInt64Bits(window.Totals[seam]) != sent.LastTotalBits) return Whole(window, n);
            var delta = new StatisticSeries
            {
                Type = window.Type, Parameter = window.Parameter, Append = true, TotalLength = total,
                Values = new double[appended], Totals = new double[appended],
            };
            Array.Copy(window.Values, n - (int)appended, delta.Values, 0, (int)appended);
            Array.Copy(window.Totals, n - (int)appended, delta.Totals, 0, (int)appended);
            return delta;
        }

        private static StatisticSeries Whole(StatisticSeries window, int n)
        {
            return new StatisticSeries { Type = window.Type, Parameter = window.Parameter, Values = window.Values, Totals = window.Totals, TotalLength = Math.Max(window.TotalLength, n) };
        }

        /// <summary>
        /// Client: applies an append to its copy, whose newest sample is the host's sample <paramref name="copyTotal"/> - 1:
        /// the samples it lacks are appended (none when it is already that far), and the copy keeps its newest
        /// <see cref="StatisticsMessage.MaxSamples"/>. False when the append starts after the copy ends (a gap: the copy is
        /// left alone until a whole series arrives).
        /// </summary>
        public static bool TryAppend(double[] values, double[] totals, long copyTotal, StatisticSeries append, out double[] newValues, out double[] newTotals)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (totals == null) throw new ArgumentNullException(nameof(totals));
            if (append == null) throw new ArgumentNullException(nameof(append));
            int have = Math.Min(values.Length, totals.Length);
            int k = Math.Min(append.Values.Length, append.Totals.Length);
            long start = append.TotalLength - k;
            newValues = values;
            newTotals = totals;
            if (copyTotal >= append.TotalLength) return true; // nothing new
            if (copyTotal < start) return false;
            int skip = (int)(copyTotal - start), add = k - skip;
            int length = Math.Min(StatisticsMessage.MaxSamples, have + add);
            int keep = length - add;
            newValues = new double[length];
            newTotals = new double[length];
            Array.Copy(values, have - keep, newValues, 0, keep);
            Array.Copy(totals, have - keep, newTotals, 0, keep);
            Array.Copy(append.Values, skip, newValues, keep, add);
            Array.Copy(append.Totals, skip, newTotals, keep, add);
            return true;
        }
    }
}
