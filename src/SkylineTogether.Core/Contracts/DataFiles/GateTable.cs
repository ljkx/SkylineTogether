using System.Collections.Generic;
using SkylineTogether.Core.Json;

namespace SkylineTogether.Core
{
    /// <summary>
    /// The client gate table, <c>gates.json</c> (ARCH §6.3, Appendix E). Schema:
    /// <code>
    /// {
    ///   "version": 1,                                     required integer &gt;= 1
    ///   "entries": [                                      systems gated for the whole session
    ///     { "type": "Game.Simulation.XPSystem",           required full type name
    ///       "phase": "ModificationEnd",                   optional, informational
    ///       "critical": true,                             optional, default false
    ///       "action": "Off",                              optional: Off (default), OffAndFeed, Keep
    ///       "reason": "rewards must not apply twice" }    optional
    ///   ],
    ///   "sessionLoad": [ { "type": "Game.Serialization.ResetBuildOrderSystem", "critical": true, "reason": "..." } ],
    ///                                                     disabled only during session loads (action OffDuringSessionLoad)
    ///   "toolScoped": [ { "tool": "Game.Tools.RouteToolSystem", "systems": [ ... ], "lingerSeconds": 2, "guard": "RouteGuard" } ]
    /// }
    /// </code>
    /// A system may appear only once across the three sections. Members starting with <c>_</c> are comments.
    /// </summary>
    public sealed class GateTable
    {
        /// <summary>File name used in error messages and digests.</summary>
        public const string FileName = "gates.json";

        public int Version;
        public IReadOnlyList<GateEntry> Entries;
        /// <summary>The session-load set (ARCH §6.3).</summary>
        public IReadOnlyList<GateEntry> SessionLoadEntries;
        public IReadOnlyList<ToolScopedGateEntry> ToolScopedEntries;

        /// <summary>Parses and validates the table; throws <see cref="ConfigValidationException"/> naming the offending value.</summary>
        public static GateTable Parse(JsonValue json)
        {
            var r = new ConfigReader(FileName);
            const string root = "$";
            r.RequireObject(json, root);
            r.CheckKeys(json, root, "version", "entries", "sessionLoad", "toolScoped");
            int version = r.GetInt(json, root, "version", true, 0, 1, int.MaxValue);

            var owners = new Dictionary<string, string>(System.StringComparer.Ordinal);   // system type -> section that lists it
            List<GateEntry> entries = ParseEntries(r, json, root, "entries", false, owners);
            List<GateEntry> sessionLoad = ParseEntries(r, json, root, "sessionLoad", true, owners);

            var toolScoped = new List<ToolScopedGateEntry>();
            JsonValue array = r.GetArray(json, root, "toolScoped", false);
            if (array != null)
            {
                string arrayPath = ConfigReader.Child(root, "toolScoped");
                var tools = new HashSet<string>(System.StringComparer.Ordinal);
                for (int i = 0; i < array.Count; i++)
                {
                    string path = ConfigReader.Index(arrayPath, i);
                    ToolScopedGateEntry entry = ToolScopedGateEntry.Parse(r, array[i], path);
                    if (!tools.Add(entry.ToolTypeName))
                    {
                        throw r.Error(array[i]["tool"], ConfigReader.Child(path, "tool"), "tool \"" + entry.ToolTypeName + "\" is listed twice");
                    }
                    foreach (string system in entry.Systems)
                    {
                        if (owners.TryGetValue(system, out string section))
                        {
                            throw r.Error(array[i]["systems"], ConfigReader.Child(path, "systems"),
                                          "\"" + system + "\" is tool-scoped and also listed in \"" + section + "\"");
                        }
                    }
                    toolScoped.Add(entry);
                }
            }

            return new GateTable
            {
                Version = version,
                Entries = entries.AsReadOnly(),
                SessionLoadEntries = sessionLoad.AsReadOnly(),
                ToolScopedEntries = toolScoped.AsReadOnly()
            };
        }

        private static List<GateEntry> ParseEntries(ConfigReader r, JsonValue json, string root, string key, bool sessionLoad, Dictionary<string, string> owners)
        {
            var result = new List<GateEntry>();
            JsonValue array = r.GetArray(json, root, key, false);
            if (array == null) return result;
            string arrayPath = ConfigReader.Child(root, key);
            for (int i = 0; i < array.Count; i++)
            {
                JsonValue item = array[i];
                string path = ConfigReader.Index(arrayPath, i);
                r.RequireObject(item, path);
                r.CheckKeys(item, path, "type", "phase", "critical", "action", "reason");
                string type = r.GetTypeName(item, path, "type", true);
                GateAction action;
                if (sessionLoad)
                {
                    GateAction? given = r.GetOptionalEnum<GateAction>(item, path, "action", false);
                    if (given.HasValue && given.Value != GateAction.OffDuringSessionLoad)
                    {
                        throw r.Error(item["action"], ConfigReader.Child(path, "action"), "entries of \"sessionLoad\" have the action OffDuringSessionLoad");
                    }
                    action = GateAction.OffDuringSessionLoad;
                }
                else
                {
                    action = r.GetEnum(item, path, "action", false, GateAction.Off);
                    if (action == GateAction.OffDuringSessionLoad)
                    {
                        throw r.Error(item["action"], ConfigReader.Child(path, "action"), "OffDuringSessionLoad entries belong in \"sessionLoad\"");
                    }
                    if (action == GateAction.ToolScoped)
                    {
                        throw r.Error(item["action"], ConfigReader.Child(path, "action"), "tool-scoped systems belong in \"toolScoped\"");
                    }
                }
                if (owners.TryGetValue(type, out string section))
                {
                    throw r.Error(item["type"], ConfigReader.Child(path, "type"), "\"" + type + "\" is already listed in \"" + section + "\"");
                }
                owners.Add(type, key);
                result.Add(new GateEntry
                {
                    TypeName = type,
                    Phase = r.GetString(item, path, "phase", false, string.Empty),
                    Critical = r.GetBool(item, path, "critical", false),
                    Action = action,
                    Reason = r.GetString(item, path, "reason", false)
                });
            }
            return result;
        }
    }
}
