using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Modding
{
    /// <summary>
    /// What a mod can write into a saved city (docs/design/MOD-COMPATIBILITY.md §6, "Mods that store data in the city"):
    /// component types the game saves with entities (<c>ISerializable</c>, <c>IEmptySerializable</c>, of any storage kind)
    /// and systems that save their own state (<c>ISerializable</c> or <c>IDefaultSerializable</c>). The game's loader
    /// cannot read a city whose saved types belong to a mod it does not have (observed: "Not serializable type: ..."
    /// followed by a native crash), so a joiner that lacks such a mod is always refused. Declarations never change this:
    /// a type declared <c>never-replicate</c> is still saved with the city. A failed scan counts as storing data (fail
    /// closed). Pure data.
    /// </summary>
    public sealed class CityDataEvidence
    {
        /// <summary>Saved component types (full names).</summary>
        public readonly List<string> Components = new List<string>();
        /// <summary>Systems that save their own state (full names).</summary>
        public readonly List<string> Systems = new List<string>();
        /// <summary>The scan failed or nothing is known: the mod may store data (fail closed).</summary>
        public bool Unknown;

        /// <summary>True when the mod may store data in a city (the joiner must have it).</summary>
        public bool StoresData => Unknown || Components.Count > 0 || Systems.Count > 0;

        /// <summary>The evidence of one scan.</summary>
        public static CityDataEvidence Of(ModFacts facts)
        {
            var e = new CityDataEvidence();
            if (facts == null || facts.ScanFailed)
            {
                e.Unknown = true;
                return e;
            }
            foreach (ComponentFact c in facts.Components)
            {
                if (c?.Name != null && (c.Serializable || c.EmptySerializable)) e.Components.Add(c.Name);
            }
            foreach (SystemFact s in facts.Systems)
            {
                if (s?.Name != null && s.SavesState) e.Systems.Add(s.Name);
            }
            e.Components.Sort(System.StringComparer.Ordinal);
            e.Systems.Sort(System.StringComparer.Ordinal);
            return e;
        }

        /// <summary>One line for the log and the diagnostics: "saves 2 component types (A, B) and 1 system state (S)".</summary>
        public string Describe()
        {
            if (Unknown) return "may store data in the city (the scan failed)";
            if (!StoresData) return "stores no data in the city";
            var parts = new List<string>();
            if (Components.Count > 0) parts.Add(Count(Components.Count, "component type") + " (" + Sample(Components) + ")");
            if (Systems.Count > 0) parts.Add(Count(Systems.Count, "system state") + " (" + Sample(Systems) + ")");
            return "saves " + string.Join(" and ", parts) + " with the city";
        }

        private static string Count(int n, string what) => n.ToString(CultureInfo.InvariantCulture) + " " + what + (n == 1 ? "" : "s");

        private static string Sample(List<string> names)
        {
            int shown = names.Count < 3 ? names.Count : 3;
            string text = string.Join(", ", names.GetRange(0, shown));
            return names.Count > shown ? text + ", ..." : text;
        }
    }
}
