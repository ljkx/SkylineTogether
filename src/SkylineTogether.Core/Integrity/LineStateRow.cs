using System;
using System.Collections.Generic;
using System.Globalization;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// One row of the UI-level line digest (ARCHITECTURE.md#line-check-and-icon-digest): exactly what the vanilla line UI shows for one transport line,
    /// computed in game by game-integrity's <c>LineStateProbe</c> through the UI's own functions
    /// (<c>TransportUIUtils.BuildTransportLine</c>: stops, vehicles, cargo, capacity, length, schedule, active; the broken
    /// flag per segment from <c>PathInformation</c>; the non-Marker icon prefabs of the line and of every waypoint;
    /// <c>TransportLine.m_Flags</c> and <c>Route.m_Flags</c>), and compared here (<see cref="LineStateReport"/>).
    /// <para>
    /// Field semantics: <see cref="Stops"/> = waypoints whose <c>Connected</c> target is a transport or work stop (not a
    /// taxi stand); <see cref="Vehicles"/>, <see cref="Cargo"/>, <see cref="Capacity"/> = <c>RouteVehicle</c> count,
    /// passengers or resources, and capacity summed over layout members; <see cref="LengthDecimetres"/> = Σ segment
    /// <c>PathInformation.m_Distance</c> rounded to decimetres (<see cref="LengthToDecimetres"/>; failed segments
    /// contribute −1 m as in the UI); <see cref="Schedule"/> = the UI's <c>RouteSchedule</c>; <see cref="RouteFlags"/> =
    /// <c>Route.m_Flags</c> (<c>Complete</c> = 1); <see cref="TransportLineFlags"/> = <c>TransportLine.m_Flags</c>
    /// (<c>RequireVehicles</c> = 1, <c>NotEnoughVehicles</c> = 2) (<c>Game.UI.InGame/TransportUIUtils.cs:41-140</c>,
    /// <c>Game.Routes/RouteFlags.cs</c>, <c>TransportLineFlags.cs</c>). A segment is drawn broken when its
    /// <c>PathInformation.m_Origin</c> and <c>m_Destination</c> are both null (<c>LineVisualizerSection.cs:580-585</c>):
    /// bit i of <see cref="BrokenSegmentMask"/> for the first 64 segments, later segments folded into
    /// <see cref="BrokenOverflowHash"/> by <see cref="MarkSegmentBroken"/>.
    /// </para>
    /// The wire form (<see cref="Write"/>, control message <c>LineRows</c>) is deterministic and versioned.
    /// </summary>
    public sealed class LineStateRow
    {
        /// <summary>Version byte of the wire form written by <see cref="Write"/>.</summary>
        public const byte FormatVersion = 1;

        /// <summary>Smallest encoded row (every varint one byte).</summary>
        internal const int MinEncodedBytes = 51;

        private const byte FlagActive = 1;
        private const byte FlagIconsCompared = 2;
        private const ulong RowDomain = 0x1F83D9ABFB41BD6BUL;
        private const ulong IconSetDomain = 0x5BE0CD19137E2179UL;
        private const ulong BrokenOverflowDomain = 0xCBBB9D5DC1059ED8UL;

        public NetSlot Line;
        public uint SampleTick;
        public int Stops;
        public int Vehicles;
        public int Cargo;
        public int Capacity;
        public int LengthDecimetres;
        public byte Schedule;
        public bool Active;
        public uint RouteFlags;
        public ushort TransportLineFlags;

        /// <summary>Bit i = segment i drawn broken (segments 0 ... 63).</summary>
        public ulong BrokenSegmentMask;

        /// <summary>Segments 64 and above that are drawn broken (Σ of a mix of their indices); 0 when none.</summary>
        public ulong BrokenOverflowHash;

        /// <summary><see cref="IconSetHash"/> of the line's non-Marker icon prefab ranks.</summary>
        public ulong LineIconHash;

        /// <summary><see cref="IconSetHash"/> of the non-Marker icon prefab ranks of all the line's waypoints.</summary>
        public ulong WaypointIconHash;

        /// <summary>False before the icon channel exists (M3): the icon hashes are then neither hashed nor compared.</summary>
        public bool IconsCompared;

        /// <summary>
        /// Records that segment <paramref name="segmentIndex"/> is drawn broken: a bit for segments 0-63, a commutative
        /// contribution to <see cref="BrokenOverflowHash"/> for later segments. Call once per broken segment.
        /// </summary>
        public void MarkSegmentBroken(int segmentIndex)
        {
            if (segmentIndex < 0) throw new ArgumentOutOfRangeException(nameof(segmentIndex), segmentIndex, "segment indices start at 0");
            if (segmentIndex < 64)
            {
                BrokenSegmentMask |= 1UL << segmentIndex;
                return;
            }
            unchecked
            {
                BrokenOverflowHash += HashMix.Mix64(BrokenOverflowDomain ^ (ulong)segmentIndex);
            }
        }

        /// <summary>True when segment <paramref name="segmentIndex"/> (0-63) is marked broken.</summary>
        public bool IsSegmentBroken(int segmentIndex)
        {
            return segmentIndex >= 0 && segmentIndex < 64 && (BrokenSegmentMask & (1UL << segmentIndex)) != 0;
        }

        /// <summary>
        /// The order-independent hash of a set of icon prefab ranks (pass only non-Marker icons): the ranks are sorted
        /// (duplicates kept) and chained through <see cref="HashMix.Combine"/>, then combined with their count. 0 for no icons.
        /// </summary>
        public static ulong IconSetHash(IReadOnlyList<PrefabRank> prefabRanks)
        {
            if (prefabRanks == null) throw new ArgumentNullException(nameof(prefabRanks));
            if (prefabRanks.Count == 0) return 0;
            var values = new int[prefabRanks.Count];
            for (int i = 0; i < values.Length; i++) values[i] = prefabRanks[i].Value;
            Array.Sort(values);
            ulong h = IconSetDomain;
            foreach (int value in values) h = HashMix.Combine(h, unchecked((uint)value));
            return HashMix.Combine(h, (ulong)values.Length);
        }

        /// <summary>A route length in metres as the row stores it: rounded to decimetres (half away from zero), clamped to int, NaN → 0.</summary>
        public static int LengthToDecimetres(double metres)
        {
            if (double.IsNaN(metres)) return 0;
            double decimetres = Math.Round(metres * 10.0, MidpointRounding.AwayFromZero);
            if (decimetres >= int.MaxValue) return int.MaxValue;
            if (decimetres <= int.MinValue) return int.MinValue;
            return (int)decimetres;
        }

        /// <summary>
        /// The row's digest over every compared field (<see cref="SampleTick"/> included; the icon hashes only when
        /// <see cref="IconsCompared"/>), chained through <see cref="HashMix.Combine"/>. Equal rows hash equal.
        /// </summary>
        public ulong Hash()
        {
            ulong h = HashMix.Combine(RowDomain, Line.Value);
            h = HashMix.Combine(h, SampleTick);
            h = HashMix.Combine(h, unchecked((uint)Stops));
            h = HashMix.Combine(h, unchecked((uint)Vehicles));
            h = HashMix.Combine(h, unchecked((uint)Cargo));
            h = HashMix.Combine(h, unchecked((uint)Capacity));
            h = HashMix.Combine(h, unchecked((uint)LengthDecimetres));
            h = HashMix.Combine(h, Schedule);
            h = HashMix.Combine(h, Active ? 1UL : 0UL);
            h = HashMix.Combine(h, RouteFlags);
            h = HashMix.Combine(h, TransportLineFlags);
            h = HashMix.Combine(h, BrokenSegmentMask);
            h = HashMix.Combine(h, BrokenOverflowHash);
            h = HashMix.Combine(h, IconsCompared ? 1UL : 0UL);
            if (IconsCompared)
            {
                h = HashMix.Combine(h, LineIconHash);
                h = HashMix.Combine(h, WaypointIconHash);
            }
            return h;
        }

        /// <summary>
        /// Appends the row:
        /// <code>
        /// u8 version (1) | varint line | u32 sampleTick | zig-zag varint stops, vehicles, cargo, capacity, lengthDecimetres
        /// | u8 schedule | u8 flags {bit0 active, bit1 iconsCompared} | u32 routeFlags | u16 transportLineFlags
        /// | u64 brokenSegmentMask | u64 brokenOverflowHash | u64 lineIconHash | u64 waypointIconHash
        /// </code>
        /// </summary>
        public void Write(ByteWriter w)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (Line.IsNone) throw new InvalidOperationException("a line-state row needs its line slot");
            w.WriteByte(FormatVersion);
            w.WriteVarUInt32(Line.Value);
            w.WriteUInt32(SampleTick);
            w.WriteVarInt32(Stops);
            w.WriteVarInt32(Vehicles);
            w.WriteVarInt32(Cargo);
            w.WriteVarInt32(Capacity);
            w.WriteVarInt32(LengthDecimetres);
            w.WriteByte(Schedule);
            w.WriteByte((byte)((Active ? FlagActive : 0) | (IconsCompared ? FlagIconsCompared : 0)));
            w.WriteUInt32(RouteFlags);
            w.WriteUInt16(TransportLineFlags);
            w.WriteUInt64(BrokenSegmentMask);
            w.WriteUInt64(BrokenOverflowHash);
            w.WriteUInt64(LineIconHash);
            w.WriteUInt64(WaypointIconHash);
        }

        /// <summary>Reads a row written by <see cref="Write"/>. Throws <see cref="DecodeException"/> for truncated or invalid input.</summary>
        public static LineStateRow Read(ByteReader r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            byte version = r.ReadByte();
            if (version != FormatVersion) throw new DecodeException("line-state row: unknown format version " + version.ToString(CultureInfo.InvariantCulture));
            uint line = r.ReadVarUInt32();
            if (line == 0 || !NetSlot.TryCreate(line, out NetSlot slot)) throw new DecodeException("line-state row: invalid line slot " + line.ToString(CultureInfo.InvariantCulture));
            var row = new LineStateRow
            {
                Line = slot,
                SampleTick = r.ReadUInt32(),
                Stops = r.ReadVarInt32(),
                Vehicles = r.ReadVarInt32(),
                Cargo = r.ReadVarInt32(),
                Capacity = r.ReadVarInt32(),
                LengthDecimetres = r.ReadVarInt32(),
                Schedule = r.ReadByte()
            };
            byte flags = r.ReadByte();
            if ((flags & ~(FlagActive | FlagIconsCompared)) != 0) throw new DecodeException("line-state row: undefined flags 0x" + flags.ToString("X2", CultureInfo.InvariantCulture));
            row.Active = (flags & FlagActive) != 0;
            row.IconsCompared = (flags & FlagIconsCompared) != 0;
            row.RouteFlags = r.ReadUInt32();
            row.TransportLineFlags = r.ReadUInt16();
            row.BrokenSegmentMask = r.ReadUInt64();
            row.BrokenOverflowHash = r.ReadUInt64();
            row.LineIconHash = r.ReadUInt64();
            row.WaypointIconHash = r.ReadUInt64();
            return row;
        }

        /// <summary>A field-by-field copy.</summary>
        public LineStateRow Clone() => (LineStateRow)MemberwiseClone();

        public override string ToString()
        {
            return "line " + Line + " @" + SampleTick.ToString(CultureInfo.InvariantCulture) + ": stops " + Stops.ToString(CultureInfo.InvariantCulture)
                   + ", vehicles " + Vehicles.ToString(CultureInfo.InvariantCulture) + ", length " + LengthDecimetres.ToString(CultureInfo.InvariantCulture)
                   + " dm, broken 0x" + BrokenSegmentMask.ToString("X16", CultureInfo.InvariantCulture);
        }
    }
}
