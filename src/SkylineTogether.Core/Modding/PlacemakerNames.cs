using System;
using System.Globalization;
using System.Text;

namespace SkylineTogether.Core.Modding
{
    /// <summary>Which on-demand runtime prefab of Placemaker a prefab name stands for.</summary>
    public enum PlacemakerPrefabKind : byte
    {
        None = 0,
        /// <summary><c>Placemaker Park a.. l.. r.. c.. m.. u..[ b]</c>: a park companion building (<c>BuildingPrefab</c>).</summary>
        Park = 1,
        /// <summary><c>Placemaker Overlay (original)</c>: a surface drawn over paved ground (<c>SurfacePrefab</c>).</summary>
        Overlay = 2,
    }

    /// <summary>
    /// The prefabs Placemaker (Paradox Mods 163638) creates at runtime on demand, by name
    /// (docs/design/MOD-COMPATIBILITY.md §7). Two kinds exist per city and player, and both are fully described by their
    /// name, so a peer that lacks one can have Placemaker create the identical prefab (same type and name, no asset, so
    /// the same <c>PrefabID</c>):
    /// <list type="bullet">
    /// <item>a park companion building per set of park statistics: <c>"Placemaker Park a{attractiveness} l{leisure}
    /// r{range} c{capacity} m{magnitude} u{upkeep}"</c> plus <c>" b"</c> for a beach park, every number a non-negative
    /// decimal integer; Placemaker builds it from a vanilla park with exactly these values;</item>
    /// <item>an overlay copy of a surface: <c>"Placemaker Overlay (" + the original surface's name + ")"</c>.</item>
    /// </list>
    /// Only the canonical form is accepted (the one formatting produces, numbers without sign or leading zeros, fields in
    /// that order), with bounds, because the names arrive from another peer. Pure.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class PlacemakerNames
    {
        public const string ParkPrefix = "Placemaker Park ";
        public const string OverlayPrefix = "Placemaker Overlay (";

        /// <summary>The prefab type of a park companion.</summary>
        public const string ParkType = "BuildingPrefab";

        /// <summary>The prefab type of an overlay surface.</summary>
        public const string OverlayType = "SurfacePrefab";

        /// <summary>Characters of an accepted name at most (the longest canonical park name has well under 100).</summary>
        public const int MaxNameLength = 200;

        /// <summary>Largest accepted value of one park statistic (far above anything Placemaker computes).</summary>
        public const int MaxParkValue = 10000000;

        private static readonly char[] Fields = { 'a', 'l', 'r', 'c', 'm', 'u' };

        /// <summary>The statistics a park companion prefab is made of.</summary>
        public struct ParkKey : IEquatable<ParkKey>
        {
            public int Attractiveness;
            public int Leisure;
            public int Range;
            public int Capacity;
            public int Magnitude;
            public int Upkeep;
            public bool Beach;

            public bool Equals(ParkKey o) => Attractiveness == o.Attractiveness && Leisure == o.Leisure && Range == o.Range && Capacity == o.Capacity
                                             && Magnitude == o.Magnitude && Upkeep == o.Upkeep && Beach == o.Beach;

            public override bool Equals(object obj) => obj is ParkKey o && Equals(o);

            public override int GetHashCode() => ((((Attractiveness * 31 + Leisure) * 31 + Range) * 31 + Capacity) * 31 + Magnitude) * 31 + Upkeep * 2 + (Beach ? 1 : 0);

            public override string ToString() => ParkName(this);

            internal int Get(int field)
            {
                switch (field)
                {
                    case 0: return Attractiveness;
                    case 1: return Leisure;
                    case 2: return Range;
                    case 3: return Capacity;
                    case 4: return Magnitude;
                    default: return Upkeep;
                }
            }

            internal void Set(int field, int value)
            {
                switch (field)
                {
                    case 0: Attractiveness = value; break;
                    case 1: Leisure = value; break;
                    case 2: Range = value; break;
                    case 3: Capacity = value; break;
                    case 4: Magnitude = value; break;
                    default: Upkeep = value; break;
                }
            }
        }

        /// <summary>The name of the park companion prefab with these statistics (negative values count as 0).</summary>
        public static string ParkName(ParkKey key)
        {
            var sb = new StringBuilder(ParkPrefix, 64);
            for (int i = 0; i < Fields.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(Fields[i]).Append(Math.Max(0, key.Get(i)).ToString(CultureInfo.InvariantCulture));
            }
            if (key.Beach) sb.Append(" b");
            return sb.ToString();
        }

        /// <summary>Parses a park companion prefab name; false for anything but the canonical form within the bounds.</summary>
        public static bool TryParsePark(string name, out ParkKey key)
        {
            key = default(ParkKey);
            if (name == null || name.Length > MaxNameLength || !name.StartsWith(ParkPrefix, StringComparison.Ordinal)) return false;
            string[] parts = name.Substring(ParkPrefix.Length).Split(' ');
            if (parts.Length != Fields.Length && parts.Length != Fields.Length + 1) return false;
            for (int i = 0; i < Fields.Length; i++)
            {
                string p = parts[i];
                if (p.Length < 2 || p[0] != Fields[i] || !TryNumber(p, 1, out int value)) return false;
                key.Set(i, value);
            }
            if (parts.Length == Fields.Length + 1)
            {
                if (parts[Fields.Length] != "b") return false;
                key.Beach = true;
            }
            return true;
        }

        /// <summary>The name of the overlay copy of a surface.</summary>
        public static string OverlayName(string original) => OverlayPrefix + (original ?? "") + ")";

        /// <summary>
        /// Parses an overlay surface name into the original surface's name; false when it is not one, the original is empty,
        /// itself an overlay, contains control characters, or the name is too long.
        /// </summary>
        public static bool TryParseOverlay(string name, out string original)
        {
            original = null;
            if (name == null || name.Length > MaxNameLength || !name.StartsWith(OverlayPrefix, StringComparison.Ordinal) || !name.EndsWith(")", StringComparison.Ordinal)) return false;
            string inner = name.Substring(OverlayPrefix.Length, name.Length - OverlayPrefix.Length - 1);
            if (inner.Length == 0 || inner.StartsWith(OverlayPrefix, StringComparison.Ordinal)) return false;
            foreach (char c in inner)
            {
                if (char.IsControl(c)) return false;
            }
            original = inner;
            return true;
        }

        /// <summary>
        /// The kind of on-demand prefab a prefab type and name stand for; <paramref name="type"/> may be null when only the
        /// name is known (a prefab table read before the load), otherwise it must be the kind's prefab type.
        /// </summary>
        public static PlacemakerPrefabKind Classify(string type, string name)
        {
            if (TryParsePark(name, out _)) return type == null || type == ParkType ? PlacemakerPrefabKind.Park : PlacemakerPrefabKind.None;
            if (TryParseOverlay(name, out _)) return type == null || type == OverlayType ? PlacemakerPrefabKind.Overlay : PlacemakerPrefabKind.None;
            return PlacemakerPrefabKind.None;
        }

        /// <summary>A canonical non-negative decimal integer (no sign, no leading zero) of at most <see cref="MaxParkValue"/>.</summary>
        private static bool TryNumber(string s, int start, out int value)
        {
            value = 0;
            int length = s.Length - start;
            if (length < 1 || length > 8 || (length > 1 && s[start] == '0')) return false;
            long v = 0;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (c < '0' || c > '9') return false;
                v = v * 10 + (c - '0');
            }
            if (v > MaxParkValue) return false;
            value = (int)v;
            return true;
        }
    }
}
