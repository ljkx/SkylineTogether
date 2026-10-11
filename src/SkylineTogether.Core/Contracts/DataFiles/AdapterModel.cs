using System.Collections.Generic;
using SkylineTogether.Core.Json;

namespace SkylineTogether.Core
{
    /// <summary>
    /// A mod adapter of <c>adapters.json</c> (ARCH §10.3): data that tells the mod's gates, tools, types and settings
    /// apart, applied by game-compat through the hook interfaces. The file is <c>{ "version": 1, "adapters": [ ... ] }</c>
    /// (<c>version</c> optional) or simply the array. One adapter:
    /// <code>
    /// { "mod": "Anarchy", "assembly": "Anarchy",          required
    ///   "versions": "*",                                  optional, default "*" (interpreted by game-compat)
    ///   "class": "WorldAffecting",                        optional ModClass, default WorldAffecting
    ///   "clientOff": [ ... ], "clientKeep": [ ... ], "cosmetic": [ ... ], "joinDisable": [ ... ],   system type names
    ///   "localTypes": [ ... ], "playerMutableTypes": [ ... ], "resendAfterJoinTypes": [ ... ],      component type names
    ///   "directWriteTools": [ ... ],                      tool system type names
    ///   "hostOnlyActions": [ ... ], "simSettings": [ ... ], "localIconPrefabs": [ ... ],            names
    ///   "pushSettings": true,                             optional, default false
    ///   "systemStateSync": [ { "id": ..., "system": ... } ],   SystemStateEntryModel entries
    ///   "toolScoped": [ { "tool": ..., "systems": [ ... ] } ],  ToolScopedGateEntry entries
    ///   "notes": "..." }
    /// </code>
    /// Every list is optional (empty when absent) and free of duplicates; a system cannot be both <c>clientOff</c> and
    /// <c>clientKeep</c> or <c>cosmetic</c>. (mod, versions) pairs and system-state stable ids are unique in the file.
    /// </summary>
    public sealed class AdapterModel
    {
        /// <summary>File name used in error messages and digests.</summary>
        public const string FileName = "adapters.json";

        private static readonly string[] AdapterKeys =
        {
            "mod", "assembly", "versions", "class", "clientOff", "clientKeep", "cosmetic", "joinDisable", "localTypes",
            "playerMutableTypes", "directWriteTools", "hostOnlyActions", "simSettings", "resendAfterJoinTypes", "toolScoped",
            "localIconPrefabs", "pushSettings", "systemStateSync", "notes"
        };

        public string Mod;
        public string Assembly;
        public string Versions;
        public ModClass Class;
        /// <summary>System lists: disabled on clients / kept (informational) / added to the cosmetic runner / disabled during the join load.</summary>
        public IReadOnlyList<string> ClientOff, ClientKeep, Cosmetic, JoinDisable;
        /// <summary>Type lists: tool-local types / player-authorable types for proposals.</summary>
        public IReadOnlyList<string> LocalTypes, PlayerMutableTypes;
        /// <summary>Tool systems that open a stage-D window (ARCH §5.2).</summary>
        public IReadOnlyList<string> DirectWriteTools;
        /// <summary>Settings buttons reverted on clients.</summary>
        public IReadOnlyList<string> HostOnlyActions;
        /// <summary>Simulation-relevant settings (mismatch blocks the join unless pushed).</summary>
        public IReadOnlyList<string> SimSettings;
        /// <summary>Types the host re-sends to joiners because the mod changes them only for serialisation.</summary>
        public IReadOnlyList<string> ResendAfterJoinTypes;
        /// <summary>Tool-scoped gate entries for the mod's tools that need local derivation for their previews.</summary>
        public IReadOnlyList<ToolScopedGateEntry> ToolScoped;
        /// <summary>Notification prefabs a local-only mod uses for its own presentation; kept by the icon authority filter (ARCH §4.10.4).</summary>
        public IReadOnlyList<string> LocalIconPrefabs;
        /// <summary>The host may push its values of <see cref="SimSettings"/> to joiners.</summary>
        public bool PushSettings;
        /// <summary>Opt-in system-state codecs for the mod's serialised systems (each must pass the round-trip self-test).</summary>
        public IReadOnlyList<SystemStateEntryModel> SystemStateSync;
        public string Notes;

        public override string ToString() => (Mod ?? "?") + " " + (Versions ?? "*") + " (" + Class + ")";

        public static IReadOnlyList<AdapterModel> ParseList(JsonValue json)
        {
            var r = new ConfigReader(FileName);
            const string root = "$";
            JsonValue array;
            string arrayPath;
            if (json != null && json.Kind == JsonKind.Array)
            {
                array = json;
                arrayPath = root;
            }
            else
            {
                r.RequireObject(json, root);
                r.CheckKeys(json, root, "version", "adapters");
                r.GetInt(json, root, "version", false, 1, 1, int.MaxValue);
                array = r.GetArray(json, root, "adapters", true);
                arrayPath = ConfigReader.Child(root, "adapters");
            }

            var result = new List<AdapterModel>(array.Count);
            var identities = new HashSet<string>(System.StringComparer.Ordinal);
            var stableIds = new Dictionary<uint, string>();
            for (int i = 0; i < array.Count; i++)
            {
                string path = ConfigReader.Index(arrayPath, i);
                AdapterModel adapter = ParseAdapter(r, array[i], path, stableIds);
                if (!identities.Add(adapter.Mod + "|" + adapter.Versions))
                {
                    throw r.Error(array[i], path, "the adapter for \"" + adapter.Mod + "\" (versions \"" + adapter.Versions + "\") is listed twice");
                }
                result.Add(adapter);
            }
            return result.AsReadOnly();
        }

        private static AdapterModel ParseAdapter(ConfigReader r, JsonValue item, string path, Dictionary<uint, string> stableIds)
        {
            r.RequireObject(item, path);
            r.CheckKeys(item, path, AdapterKeys);
            var adapter = new AdapterModel
            {
                Mod = r.GetName(item, path, "mod", true),
                Assembly = r.GetTypeName(item, path, "assembly", true),
                Versions = r.GetName(item, path, "versions", false) ?? "*",
                Class = r.GetEnum(item, path, "class", false, ModClass.WorldAffecting),
                ClientOff = r.GetStringList(item, path, "clientOff", true),
                ClientKeep = r.GetStringList(item, path, "clientKeep", true),
                Cosmetic = r.GetStringList(item, path, "cosmetic", true),
                JoinDisable = r.GetStringList(item, path, "joinDisable", true),
                LocalTypes = r.GetStringList(item, path, "localTypes", true),
                PlayerMutableTypes = r.GetStringList(item, path, "playerMutableTypes", true),
                DirectWriteTools = r.GetStringList(item, path, "directWriteTools", true),
                HostOnlyActions = r.GetStringList(item, path, "hostOnlyActions", false),
                SimSettings = r.GetStringList(item, path, "simSettings", false),
                ResendAfterJoinTypes = r.GetStringList(item, path, "resendAfterJoinTypes", true),
                LocalIconPrefabs = r.GetStringList(item, path, "localIconPrefabs", false),
                PushSettings = r.GetBool(item, path, "pushSettings", false),
                Notes = r.GetString(item, path, "notes", false)
            };

            CheckDisjoint(r, item, path, "clientOff", adapter.ClientOff, "clientKeep", adapter.ClientKeep);
            CheckDisjoint(r, item, path, "clientOff", adapter.ClientOff, "cosmetic", adapter.Cosmetic);
            foreach (string system in adapter.Cosmetic)
            {
                if (System.Array.IndexOf(CosmeticList.NeverCosmetic, system) >= 0)
                {
                    throw r.Error(item["cosmetic"], ConfigReader.Child(path, "cosmetic"), "\"" + system + "\" writes replicated state and can never be cosmetic (ARCH §6.1)");
                }
            }

            var toolScoped = new List<ToolScopedGateEntry>();
            JsonValue toolArray = r.GetArray(item, path, "toolScoped", false);
            if (toolArray != null)
            {
                string toolPath = ConfigReader.Child(path, "toolScoped");
                var tools = new HashSet<string>(System.StringComparer.Ordinal);
                for (int i = 0; i < toolArray.Count; i++)
                {
                    ToolScopedGateEntry entry = ToolScopedGateEntry.Parse(r, toolArray[i], ConfigReader.Index(toolPath, i));
                    if (!tools.Add(entry.ToolTypeName))
                    {
                        throw r.Error(toolArray[i], ConfigReader.Index(toolPath, i), "tool \"" + entry.ToolTypeName + "\" is listed twice");
                    }
                    toolScoped.Add(entry);
                }
            }
            adapter.ToolScoped = toolScoped.AsReadOnly();

            JsonValue syncArray = r.GetArray(item, path, "systemStateSync", false);
            adapter.SystemStateSync = syncArray == null
                ? ConfigReader.Empty<SystemStateEntryModel>()
                : SystemStateEntryModel.ParseEntries(r, syncArray, ConfigReader.Child(path, "systemStateSync"), stableIds).AsReadOnly();
            return adapter;
        }

        private static void CheckDisjoint(ConfigReader r, JsonValue item, string path, string firstKey, IReadOnlyList<string> first,
                                          string secondKey, IReadOnlyList<string> second)
        {
            foreach (string name in second)
            {
                for (int i = 0; i < first.Count; i++)
                {
                    if (first[i] == name)
                    {
                        throw r.Error(item[secondKey], ConfigReader.Child(path, secondKey), "\"" + name + "\" is in both \"" + firstKey + "\" and \"" + secondKey + "\"");
                    }
                }
            }
        }
    }
}
