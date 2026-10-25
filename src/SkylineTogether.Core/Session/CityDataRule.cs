using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// The join rules no host option can relax (ARCHITECTURE.md#handshake-and-admission, docs/design/MOD-COMPATIBILITY.md
    /// "Mods that store data in the city"):
    /// <list type="bullet">
    /// <item><b>City data.</b> A joiner that lacks one of the host's mods that store data in a saved city is refused,
    /// whatever "require the same mods", "let players join with different mods" or "join anyway" say: the game's loader
    /// cannot read saved types of a mod it does not have and crashes natively ("Not serializable type: ..." in
    /// Player.log, observed with Zoning Toolkit). A mod stores city data when the host's own scan finds saved component
    /// types or saved system state in it (<see cref="Modding.CityDataEvidence"/>, carried in
    /// <see cref="ModEntry.StoresCityData"/>), when the scan failed, or when the built-in list knows it does. Whether the
    /// current city holds the mod's data only changes the wording: a mod that can store data but has none in this city is
    /// still required, because nothing proves that the save carries no trace of it (system state, a type table). The one
    /// exception are saved types a mod is known never to write (<see cref="DormantSavedTypes"/>): they are city data only
    /// when the city has entities with them.</item>
    /// <item><b>The old multiplayer mod.</b> A peer with the "CS2 Multiplayer Mod" loaded (assembly name
    /// <see cref="OldMultiplayerModAssembly"/> or Paradox Mods id <see cref="OldMultiplayerModId"/>; detected by name and
    /// id only) cannot take part: two multiplayer mods at once conflict.</item>
    /// </list>
    /// The joiner repeats the check before it loads the snapshot (<see cref="CheckBeforeLoad"/>), from the host's join
    /// notice, so an older or misconfigured host cannot make it load a city it cannot read. Pure and thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class CityDataRule
    {
        /// <summary>The old multiplayer mod's assembly name.</summary>
        public const string OldMultiplayerModAssembly = "CS2MultiplayerMod";
        /// <summary>The old multiplayer mod's Paradox Mods id.</summary>
        public const string OldMultiplayerModId = "150432";
        /// <summary>The old multiplayer mod's display name in messages.</summary>
        public const string OldMultiplayerModTitle = "CS2 Multiplayer Mod";

        // mods whose decompiled installed versions save data with the city (MOD-COMPATIBILITY.md §2, class D or saved
        // per-city settings); they count as storing data even when a peer's scan says otherwise
        private static readonly HashSet<string> KnownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Traffic", "Anarchy", "BetterBulldozer", "Water_Features", "ZoningToolkit", "PlopTheGrowables", "Tree_Controller",
            "Placemaker",
            "RoadSpeedAdjuster",
        };

        private static readonly HashSet<string> KnownIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "80095", "74604", "75250", "75613", "75750", "75826", "75993",
            "163638",
            "125866",
        };

        // saved component types a mod declares but never puts on an entity (checked by hand in the installed version,
        // MOD-COMPATIBILITY.md §10). A save lists every saved type of every loaded mod in its type table, used or not, and
        // the game's loader reads an entry of a type it does not know without any data (one "Not serializable type"
        // warning); only entities that carry the type need the mod. Such a type is city data only where entities have it.
        private static readonly Dictionary<string, string[]> Dormant = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["EmploymentTracker"] = new[] { "EmploymentTracker.DeleteTimer" },
        };

        /// <summary>
        /// The saved component types of the mod (by assembly name) that it never puts on an entity: they count as city data
        /// only when the city has entities with them (<see cref="Modding.CityDataEvidence.Dormant"/>). Empty for most mods.
        /// </summary>
        public static IReadOnlyList<string> DormantSavedTypes(string assemblyName)
        {
            return !string.IsNullOrEmpty(assemblyName) && Dormant.TryGetValue(assemblyName, out string[] types) ? types : Array.Empty<string>();
        }

        /// <summary>True for a mod the built-in list knows to save data with the city.</summary>
        public static bool IsKnownCityData(string assemblyName, string platformId)
        {
            return (!string.IsNullOrEmpty(assemblyName) && KnownNames.Contains(assemblyName))
                   || (!string.IsNullOrEmpty(platformId) && KnownIds.Contains(platformId));
        }

        /// <summary>True when the entry is the old multiplayer mod (by assembly name or Paradox Mods id).</summary>
        public static bool IsOldMultiplayerMod(ModEntry m)
        {
            if (m == null) return false;
            return string.Equals(m.Name, OldMultiplayerModAssembly, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(m.PlatformId, OldMultiplayerModId, StringComparison.Ordinal);
        }

        /// <summary>The old multiplayer mod's entry in <paramref name="mods"/>, or null.</summary>
        public static ModEntry FindOldMultiplayerMod(IReadOnlyList<ModEntry> mods)
        {
            if (mods == null) return null;
            foreach (ModEntry m in mods)
            {
                if (IsOldMultiplayerMod(m)) return m;
            }
            return null;
        }

        /// <summary>True when a joiner must have this mod of the host's (its own scan, or the built-in list).</summary>
        public static bool StoresCityData(ModEntry m) => m != null && (m.StoresCityData || IsKnownCityData(m.Name, m.PlatformId));

        /// <summary>The host's mods that store city data, as report entries (name, version, id, in-city), sorted by name.</summary>
        public static List<ModEntry> CityDataMods(IReadOnlyList<ModEntry> host)
        {
            var list = new List<ModEntry>();
            if (host == null) return list;
            foreach (ModEntry m in host)
            {
                if (StoresCityData(m) && !IsOldMultiplayerMod(m)) list.Add(Entry(m));
            }
            list.Sort(ByName);
            return list;
        }

        /// <summary>The entries of <paramref name="required"/> that <paramref name="loaded"/> lacks (by Paradox Mods id or name).</summary>
        public static List<ModEntry> MissingFrom(IReadOnlyList<ModEntry> required, IReadOnlyList<ModEntry> loaded)
        {
            var list = new List<ModEntry>();
            if (required == null) return list;
            foreach (ModEntry m in required)
            {
                if (m != null && !Has(loaded, m)) list.Add(Entry(m));
            }
            list.Sort(ByName);
            return list;
        }

        /// <summary>True when <paramref name="loaded"/> has the mod: the same Paradox Mods id, or the same name (case-insensitive).</summary>
        public static bool Has(IReadOnlyList<ModEntry> loaded, ModEntry m)
        {
            if (loaded == null || m == null) return false;
            foreach (ModEntry l in loaded)
            {
                if (l == null) continue;
                if (!string.IsNullOrEmpty(m.PlatformId) && string.Equals(l.PlatformId, m.PlatformId, StringComparison.Ordinal)) return true;
                if (!string.IsNullOrEmpty(m.Name) && string.Equals(l.Name, m.Name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>The refusal for the old multiplayer mod (kind "conflict"; never overridable). Null when neither side has it.</summary>
        public static MismatchReport Conflict(ModEntry hostHas, ModEntry youHave)
        {
            if (hostHas == null && youHave == null) return null;
            return new MismatchReport
            {
                Kind = "conflict",
                HostConflict = hostHas == null ? null : new ModEntry { Name = hostHas.Name, Version = hostHas.Version, PlatformId = hostHas.PlatformId },
                ClientConflict = youHave == null ? null : new ModEntry { Name = youHave.Name, Version = youHave.Version, PlatformId = youHave.PlatformId },
            };
        }

        /// <summary>
        /// The refusal for mods with city data the joiner lacks (kind "mods", never overridable): <paramref name="missing"/>
        /// become <see cref="MismatchReport.CityData"/> and are also in <see cref="MismatchReport.Missing"/> (readers that
        /// do not know the city-data record still list them as missing). <paramref name="others"/> (may be null) carries the
        /// other mod differences.
        /// </summary>
        public static MismatchReport Refusal(List<ModEntry> missing, MismatchReport others)
        {
            var r = new MismatchReport { Kind = "mods" };
            if (others != null)
            {
                r.Missing.AddRange(others.Missing);
                r.Extra.AddRange(others.Extra);
                r.Versions.AddRange(others.Versions);
            }
            foreach (ModEntry m in missing)
            {
                r.CityData.Add(m);
                if (!Has(r.Missing, m)) r.Missing.Add(new ModEntry { Name = m.Name, Version = m.Version, PlatformId = m.PlatformId });
            }
            r.Missing.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.ToString(), b.ToString()));
            return r;
        }

        /// <summary>
        /// The joiner's last check before it loads the host's city (or as soon as the join notice arrives): null when the
        /// load is safe, otherwise the report to end the join with (the same screen as the host's refusal).
        /// <list type="bullet">
        /// <item>The joiner itself runs the old multiplayer mod: conflict.</item>
        /// <item>No notice: the host found no difference in the mods (identical lists), nothing to check.</item>
        /// <item>A notice from a host that checked city data (<see cref="MismatchReport.CityDataChecked"/>): every mod of
        /// <see cref="MismatchReport.HostCityData"/> must be loaded here.</item>
        /// <item>A notice from an older host (no such record): every mod the host has and the joiner lacks
        /// (<see cref="MismatchReport.Missing"/>) may store data, so each one stops the join (fail closed).</item>
        /// </list>
        /// </summary>
        public static MismatchReport CheckBeforeLoad(MismatchReport notice, IReadOnlyList<ModEntry> own)
        {
            MismatchReport conflict = Conflict(null, FindOldMultiplayerMod(own));
            if (conflict != null) return conflict;
            if (notice == null) return null;
            List<ModEntry> required;
            if (notice.CityDataChecked)
            {
                required = notice.HostCityData;
            }
            else
            {
                required = new List<ModEntry>();
                foreach (ModEntry m in notice.Missing)
                {
                    if (m != null) required.Add(new ModEntry { Name = m.Name, Version = m.Version, PlatformId = m.PlatformId, StoresCityData = true });
                }
            }
            List<ModEntry> missing = MissingFrom(required, own);
            if (missing.Count == 0) return null;
            if (!notice.CityDataChecked)
            {
                // an older host does not say what stores data; the city's data cannot be confirmed either way
                foreach (ModEntry m in missing) m.CityDataInCity = false;
            }
            return Refusal(missing, null);
        }

        private static ModEntry Entry(ModEntry m) => new ModEntry
        {
            Name = m.Name,
            Version = m.Version,
            PlatformId = m.PlatformId,
            StoresCityData = true,
            CityDataInCity = m.CityDataInCity,
            CityDataDetail = m.CityDataDetail,
        };

        private static int ByName(ModEntry a, ModEntry b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name ?? "", b.Name ?? "");
    }
}
