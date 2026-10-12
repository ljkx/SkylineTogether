using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// The UI-level line digest's comparison (ARCH §4.11.6): a hash of the whole line list for quick equality checks
    /// (record/replay verdicts, the post-join check with the S class, the 60 s diagnostics probe), and a field-by-field
    /// comparison of the host's and a client's rows that names every differing line and field, plus missing and extra
    /// lines (<c>/st lines compare</c>, acceptance test ST-LINES). Also the wire form of a row list (control message
    /// <c>LineRows</c>). Pure and thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class LineStateReport
    {
        /// <summary>The field of a difference for a line only one side has (values <see cref="PresentValue"/> / <see cref="MissingValue"/>).</summary>
        public const string LineField = "Line";

        /// <summary>The field of a difference for a line listed more than once on a side (values are the counts).</summary>
        public const string DuplicateField = "Duplicate";

        public const string PresentValue = "present";
        public const string MissingValue = "missing";

        /// <summary>Rows in one <see cref="WriteRows"/> list at most.</summary>
        public const int MaxRows = 16384;

        private const ulong ListDomain = 0x629A292A367CD507UL;

        /// <summary>
        /// The hash of a row list, independent of the row order: <c>Combine(Σ Mix64(row.Hash()) ^ domain, n)</c> over the
        /// non-null rows (a row listed twice counts twice). Equal hashes mean equal lists; different hashes call for
        /// <see cref="Compare"/>, which ignores icon fields when either side has <c>IconsCompared</c> false.
        /// </summary>
        public static ulong ListHash(IReadOnlyList<LineStateRow> rows)
        {
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            ulong sum = 0;
            ulong count = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                LineStateRow row = rows[i];
                if (row == null) continue;
                unchecked
                {
                    sum += HashMix.Mix64(row.Hash());
                }
                count++;
            }
            return HashMix.Combine(sum ^ ListDomain, count);
        }

        /// <summary>
        /// Every difference between the host's and the client's rows, ordered by line slot: a <see cref="LineField"/> entry
        /// for a line only one side lists, otherwise one entry per differing field (the icon hashes only when both rows
        /// have <c>IconsCompared</c>), and a <see cref="DuplicateField"/> entry for a line listed twice on a side (the first
        /// row is compared). Null rows are skipped. Empty when the lists match.
        /// </summary>
        public static IReadOnlyList<LineStateDifference> Compare(IReadOnlyList<LineStateRow> host, IReadOnlyList<LineStateRow> client)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (client == null) throw new ArgumentNullException(nameof(client));
            Dictionary<uint, LineStateRow> hostRows = Index(host, out Dictionary<uint, int> hostCounts);
            Dictionary<uint, LineStateRow> clientRows = Index(client, out Dictionary<uint, int> clientCounts);
            var lines = new List<uint>(hostRows.Count + clientRows.Count);
            foreach (uint line in hostRows.Keys) lines.Add(line);
            foreach (uint line in clientRows.Keys)
            {
                if (!hostRows.ContainsKey(line)) lines.Add(line);
            }
            lines.Sort();

            var differences = new List<LineStateDifference>();
            foreach (uint value in lines)
            {
                var line = new NetSlot(value);
                hostRows.TryGetValue(value, out LineStateRow h);
                clientRows.TryGetValue(value, out LineStateRow c);
                if (h == null)
                {
                    Add(differences, line, LineField, MissingValue, PresentValue);
                }
                else if (c == null)
                {
                    Add(differences, line, LineField, PresentValue, MissingValue);
                }
                else
                {
                    CompareRows(differences, line, h, c);
                }
                int hostCount = hostCounts.TryGetValue(value, out int hc) ? hc : 0;
                int clientCount = clientCounts.TryGetValue(value, out int cc) ? cc : 0;
                if (hostCount > 1 || clientCount > 1) Add(differences, line, DuplicateField, Format(hostCount), Format(clientCount));
            }
            return differences;
        }

        /// <summary>Appends a row list: <c>varint n</c> (≤ <see cref="MaxRows"/>) followed by the rows (<see cref="LineStateRow.Write"/>).</summary>
        public static void WriteRows(ByteWriter w, IReadOnlyList<LineStateRow> rows)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            int count = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null) count++;
            }
            if (count > MaxRows) throw new ArgumentException("more than " + MaxRows + " line-state rows in one list", nameof(rows));
            w.WriteVarUInt32((uint)count);
            for (int i = 0; i < rows.Count; i++) rows[i]?.Write(w);
        }

        /// <summary>Reads a row list written by <see cref="WriteRows"/>. Throws <see cref="DecodeException"/> for truncated or invalid input.</summary>
        public static List<LineStateRow> ReadRows(ByteReader r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            uint count = r.ReadVarUInt32();
            if (count > MaxRows) throw new DecodeException("line-state rows: " + count.ToString(CultureInfo.InvariantCulture) + " rows exceed " + MaxRows);
            if (count > (uint)(r.Remaining / LineStateRow.MinEncodedBytes))
            {
                throw new DecodeException("line-state rows: " + count.ToString(CultureInfo.InvariantCulture) + " rows cannot fit into " + r.Remaining.ToString(CultureInfo.InvariantCulture) + " bytes");
            }
            var rows = new List<LineStateRow>((int)count);
            for (uint i = 0; i < count; i++) rows.Add(LineStateRow.Read(r));
            return rows;
        }

        private static Dictionary<uint, LineStateRow> Index(IReadOnlyList<LineStateRow> rows, out Dictionary<uint, int> counts)
        {
            var index = new Dictionary<uint, LineStateRow>(rows.Count);
            counts = new Dictionary<uint, int>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                LineStateRow row = rows[i];
                if (row == null) continue;
                uint line = row.Line.Value;
                counts.TryGetValue(line, out int count);
                counts[line] = count + 1;
                if (count == 0) index.Add(line, row);
            }
            return index;
        }

        private static void CompareRows(List<LineStateDifference> output, NetSlot line, LineStateRow h, LineStateRow c)
        {
            if (h.SampleTick != c.SampleTick) Add(output, line, nameof(LineStateRow.SampleTick), Format(h.SampleTick), Format(c.SampleTick));
            if (h.Stops != c.Stops) Add(output, line, nameof(LineStateRow.Stops), Format(h.Stops), Format(c.Stops));
            if (h.Vehicles != c.Vehicles) Add(output, line, nameof(LineStateRow.Vehicles), Format(h.Vehicles), Format(c.Vehicles));
            if (h.Cargo != c.Cargo) Add(output, line, nameof(LineStateRow.Cargo), Format(h.Cargo), Format(c.Cargo));
            if (h.Capacity != c.Capacity) Add(output, line, nameof(LineStateRow.Capacity), Format(h.Capacity), Format(c.Capacity));
            if (h.LengthDecimetres != c.LengthDecimetres) Add(output, line, nameof(LineStateRow.LengthDecimetres), Format(h.LengthDecimetres), Format(c.LengthDecimetres));
            if (h.Schedule != c.Schedule) Add(output, line, nameof(LineStateRow.Schedule), Format(h.Schedule), Format(c.Schedule));
            if (h.Active != c.Active) Add(output, line, nameof(LineStateRow.Active), Format(h.Active), Format(c.Active));
            if (h.RouteFlags != c.RouteFlags) Add(output, line, nameof(LineStateRow.RouteFlags), Hex(h.RouteFlags), Hex(c.RouteFlags));
            if (h.TransportLineFlags != c.TransportLineFlags) Add(output, line, nameof(LineStateRow.TransportLineFlags), Hex(h.TransportLineFlags), Hex(c.TransportLineFlags));
            if (h.BrokenSegmentMask != c.BrokenSegmentMask)
            {
                Add(output, line, nameof(LineStateRow.BrokenSegmentMask), SegmentList(h.BrokenSegmentMask), SegmentList(c.BrokenSegmentMask));
            }
            if (h.BrokenOverflowHash != c.BrokenOverflowHash) Add(output, line, nameof(LineStateRow.BrokenOverflowHash), Hex(h.BrokenOverflowHash), Hex(c.BrokenOverflowHash));
            if (h.IconsCompared != c.IconsCompared) Add(output, line, nameof(LineStateRow.IconsCompared), Format(h.IconsCompared), Format(c.IconsCompared));
            if (h.IconsCompared && c.IconsCompared)
            {
                if (h.LineIconHash != c.LineIconHash) Add(output, line, nameof(LineStateRow.LineIconHash), Hex(h.LineIconHash), Hex(c.LineIconHash));
                if (h.WaypointIconHash != c.WaypointIconHash) Add(output, line, nameof(LineStateRow.WaypointIconHash), Hex(h.WaypointIconHash), Hex(c.WaypointIconHash));
            }
        }

        private static void Add(List<LineStateDifference> output, NetSlot line, string field, string hostValue, string clientValue)
        {
            output.Add(new LineStateDifference { Line = line, Field = field, HostValue = hostValue, ClientValue = clientValue });
        }

        /// <summary>"none" or the broken segment indices, e.g. "3,7".</summary>
        private static string SegmentList(ulong mask)
        {
            if (mask == 0) return "none";
            var text = new StringBuilder();
            for (int i = 0; i < 64; i++)
            {
                if ((mask & (1UL << i)) == 0) continue;
                if (text.Length > 0) text.Append(',');
                text.Append(i.ToString(CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Format(uint value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Format(bool value) => value ? "true" : "false";

        private static string Hex(uint value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

        private static string Hex(ulong value) => "0x" + value.ToString("X16", CultureInfo.InvariantCulture);
    }
}
