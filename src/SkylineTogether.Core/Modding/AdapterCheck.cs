using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Modding
{
    /// <summary>
    /// The version check of one built-in mod adapter (docs/design/MOD-COMPATIBILITY.md §5): every type, system, tool and
    /// setting an adapter names was verified against one version of its mod. When any of them is missing at runtime the
    /// mod was updated, the adapter's knowledge no longer holds as a whole (half of its rules could contradict the other
    /// half), and the adapter turns itself off; the mod then gets the generic treatment like any unknown mod. Host and
    /// client run the same mod version (join check), so they decide the same. Pure.
    /// </summary>
    public sealed class AdapterCheck
    {
        private readonly List<string> _missing = new List<string>();
        private int _checked;

        /// <summary>The names that did not resolve, as <c>"kind name"</c>.</summary>
        public IReadOnlyList<string> Missing => _missing;

        /// <summary>Number of names checked.</summary>
        public int Checked => _checked;

        /// <summary>True when every name resolved (or nothing was named).</summary>
        public bool Healthy => _missing.Count == 0;

        /// <summary>Records one name; <paramref name="found"/> is whether it resolved in the loaded mod.</summary>
        public void Expect(string kind, string name, bool found)
        {
            _checked++;
            if (!found) _missing.Add(kind + " " + name);
        }

        /// <summary>The one log line (and diagnostics entry) for a disabled adapter; null when healthy.</summary>
        public string Describe(string modName, string version)
        {
            if (Healthy) return null;
            const int Shown = 3;
            var shown = new List<string>();
            for (int i = 0; i < _missing.Count && i < Shown; i++) shown.Add(_missing[i]);
            string more = _missing.Count > Shown ? " and " + (_missing.Count - Shown).ToString(CultureInfo.InvariantCulture) + " more" : "";
            return modName + (string.IsNullOrEmpty(version) ? "" : " " + version) + ": adapter disabled, " + _missing.Count.ToString(CultureInfo.InvariantCulture)
                   + " of " + _checked.ToString(CultureInfo.InvariantCulture) + " names not found in this version (" + string.Join(", ", shown) + more
                   + "); the generic analysis applies instead";
        }
    }
}
