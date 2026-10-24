using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Diagnostics
{
    /// <summary>
    /// Which feature each type of the game layer (<c>SkylineTogether.dll</c>) belongs to, for P-REFS
    /// (ARCHITECTURE.md#probes-and-self-test): a type or member a game update removed is attributed to the mod's type (and
    /// method) whose code uses it, and this map names the feature(s) to switch off. Every type is listed on its own (no
    /// namespace-wide default), so a new type must be placed deliberately: <c>ModFeatureMapTests</c> checks every type of
    /// the built DLL (<c>src/SkylineTogether/Data/mod-types.generated.txt</c>, kept current by <c>tools/build.sh</c>).
    /// <list type="bullet">
    /// <item>A type entry (<c>SkylineTogether.Sync.LineCheck</c>) matches that type and its nested types
    /// (<c>...+Inner</c>, compiler-generated closures and state machines).</item>
    /// <item>A method entry (<c>SkylineTogether.Sync.EntityCodec::ApplySimValues</c>) matches that method of the type,
    /// including its lambdas, local functions and iterator or async state machine (their names carry the method's
    /// name); it is for partial classes whose parts belong to different features. Method entries come before the
    /// type's own entry (the first match wins).</item>
    /// <item>A namespace entry (ending in <c>.</c>) matches every type below it; used only for compiler-generated
    /// attributes and for Core (<c>SkylineTogether.Core.</c>, the second DLL, which every feature uses).</item>
    /// </list>
    /// The value is the ids of the features (<c>Features.cs</c> of the game layer) to switch off; an empty list means
    /// "no feature": diagnostics and solo-testing code whose failure is reported as a warning and switches nothing off. A
    /// type that no entry matches gets null: also a warning (never the critical city sync), and the test fails. Pure.
    /// </summary>
    public static class ModFeatureMap
    {
        private static readonly string[] None = new string[0];

        private const string Session = "session", Steam = "steam", Replication = "replication", Speed = "speed", Gate = "gate",
            Clock = "clock", Global = "global", Icons = "icons", Movers = "movers", Terrain = "terrain", Water = "water",
            Integrity = "integrity", SimValues = "simvals", Agents = "agents", Flow = "flow", Actions = "actions",
            Events = "events", Presence = "presence", Previews = "previews", Invites = "invites", Mods = "mods",
            ModCompat = "modcompat", Ui = "ui";

        private const string Sync = "SkylineTogether.Sync.";

        /// <summary>The entries in match order (see the class summary).</summary>
        public static readonly IReadOnlyList<(string Key, string[] Features)> Entries = new (string, string[])[]
        {
            // compiler-generated types and attributes: no code that uses the game
            ("<Module>", None),
            ("<PrivateImplementationDetails>", None),
            ("Microsoft.CodeAnalysis.", None),
            ("System.Runtime.CompilerServices.", None),

            // Core (the second DLL): shared by every feature and bound only to the base class library
            ("SkylineTogether.Core.", F(Session)),

            // the mod's entry point, settings, locale and its own infrastructure (logging, guards, probes, timing): every
            // feature runs through them, so a failure there is the session's
            ("SkylineTogether.Mod", F(Session)),
            ("SkylineTogether.Settings.LocaleSource", F(Session)),
            ("SkylineTogether.Settings.SkylineTogetherSettings", F(Session)),
            ("SkylineTogether.Diagnostics.Features", F(Session)),
            ("SkylineTogether.Diagnostics.GameLog", F(Session)),
            ("SkylineTogether.Diagnostics.Guard", F(Session)),
            ("SkylineTogether.Diagnostics.Perf", F(Session)),
            ("SkylineTogether.Diagnostics.Probes", F(Session)),
            ("SkylineTogether.Diagnostics.ReferenceProbe", F(Session)),
            ("SkylineTogether.Diagnostics.ShortPaths", F(Session)),
            // diagnostics only (the build id falls back to the assembly's MVID when InstallInfo fails): warnings
            ("SkylineTogether.Diagnostics.DiagnosticsDump", None),
            // crash breadcrumbs: every call is contained, a failure only loses a line
            ("SkylineTogether.Diagnostics.Forensics", None),
            ("SkylineTogether.Diagnostics.InstallInfo", None),
            ("SkylineTogether.UI.PerfOverlay", None),
            // solo testing (replay of a recording): no feature of a session
            ("SkylineTogether.Replay.RecordingInfo", None),
            ("SkylineTogether.Replay.ReplayController", None),
            ("SkylineTogether.UI.ReplayUISystem", None),

            // session, Steam, speed
            ("SkylineTogether.Session.GameInfoProvider", F(Session)),
            ("SkylineTogether.Session.JoinAssetTransfer", F(Session)),
            ("SkylineTogether.Session.JoinNoticeText", F(Session)),
            ("SkylineTogether.Session.MainThreadQueue", F(Session)),
            ("SkylineTogether.Session.SessionDriver", F(Session)),
            ("SkylineTogether.Session.SessionEndInfo", F(Session)),
            ("SkylineTogether.Session.SessionManager", F(Session)),
            ("SkylineTogether.Session.SessionMode", F(Session)),
            ("SkylineTogether.Session.SnapshotLoader", F(Session)),
            ("SkylineTogether.Session.SnapshotService", F(Session)),
            ("SkylineTogether.Session.SessionToasts", F(Ui)),
            ("SkylineTogether.Session.SteamFriendList", F(Invites)),
            ("SkylineTogether.Session.SteamInvites", F(Invites)),
            ("SkylineTogether.Net.SteamConnection", F(Steam)),
            ("SkylineTogether.Net.SteamLanes", F(Steam)),
            ("SkylineTogether.Net.SteamSocketsTransport", F(Steam)),
            ("SkylineTogether.Systems.SpeedAuthoritySystem", F(Speed)),

            // UI
            ("SkylineTogether.UI.InviteUISystem", F(Ui)),
            ("SkylineTogether.UI.SessionUISystem", F(Ui)),

            // players' cursors and previews
            ("SkylineTogether.Presence.PrefabRankIndex", F(Presence)),
            ("SkylineTogether.Presence.PresenceController", F(Presence)),
            ("SkylineTogether.Presence.PresenceSystem", F(Presence)),
            ("SkylineTogether.Presence.ToolPreviewCapture", F(Previews)),
            ("SkylineTogether.Presence.ToolPreviewRenderer", F(Previews)),

            // mod support and the integration API
            ("SkylineTogether.Compat.ModIntegration", F(Mods)),
            ("SkylineTogether.Compat.AchievementFixerAdapter", F(ModCompat)),
            ("SkylineTogether.Compat.AnarchyAdapter", F(ModCompat)),
            ("SkylineTogether.Compat.AssetSources", F(ModCompat)),
            ("SkylineTogether.Compat.BetterBulldozerAdapter", F(ModCompat)),
            ("SkylineTogether.Compat.CimCensusAdapter", F(ModCompat)),
            ("SkylineTogether.Compat.CityDataProbe", F(ModCompat)),
            ("SkylineTogether.Compat.EmploymentTrackerAdapter", F(ModCompat)),
            ("SkylineTogether.Compat.FindItAdapter", F(ModCompat)),
            ("SkylineTogether.Compat.FiveTwentyNineTilesAdapter", F(ModCompat)),
            ("SkylineTogether.Compat.GenericModAdapter", F(ModCompat)),
            ("SkylineTogether.Compat.HarmonyPatches", F(ModCompat)),
            ("SkylineTogether.Compat.HistoricalStartAdapter", F(ModCompat)),
            ("SkylineTogether.Compat.LineToolAdapter", F(ModCompat)),
            ("SkylineTogether.Compat.ModAdapter", F(ModCompat)),
            ("SkylineTogether.Compat.ModAssemblyScanner", F(ModCompat)),
            ("SkylineTogether.Compat.ModCompat", F(ModCompat)),
            ("SkylineTogether.Compat.ModSettingsSync", F(ModCompat)),
            ("SkylineTogether.Compat.ModStateWatcher", F(ModCompat)),
            ("SkylineTogether.Compat.MoveItAdapter", F(ModCompat)),
            ("SkylineTogether.Compat.PlayerInputWatch", F(ModCompat)),
            ("SkylineTogether.Compat.PlopTheGrowablesAdapter", F(ModCompat)),
            ("SkylineTogether.Compat.TrafficAdapter", F(ModCompat)),
            ("SkylineTogether.Compat.TreeControllerAdapter", F(ModCompat)),
            ("SkylineTogether.Compat.WaterFeaturesAdapter", F(ModCompat)),
            ("SkylineTogether.Compat.ZoningToolkitAdapter", F(ModCompat)),
            ("SkylineTogether.Compat.PlacemakerAdapter", F(ModCompat)),

            // the codec's partial for simulation values: its own methods belong to the value paths that call them
            // (attention values and agents); the lenient write it shares with structure records stays replication
            (Sync + "EntityCodec::ApplySimValues", F(SimValues)),
            (Sync + "EntityCodec::ApplyAgentValues", F(Agents)),
            (Sync + "EntityCodec::WriteValueSubset", F(Agents)),
            (Sync + "EntityCodec::WriteSimValues", F(SimValues, Agents)),
            (Sync + "EntityCodec::HashSimValues", F(SimValues, Agents)),
            (Sync + "EntityCodec::SimHandlers", F(SimValues, Agents)),
            (Sync + "EntityCodec::IsSimValueType", F(SimValues, Agents)),
            (Sync + "EntityCodec::Classify", F(SimValues, Agents)),
            // the controller's partial for mod state: capture and edit holds run guarded by mod support
            (Sync + "SyncController::CaptureModChanges", F(ModCompat)),
            (Sync + "SyncController::ReleaseHolds", F(ModCompat)),

            // structure replication (critical): the controller, the codec and its handlers, net ids, the wire, cascades
            (Sync + "AggregateLinkGuardSystem", F(Replication)),
            (Sync + "AggregateMeshGuardSystem", F(Replication)),
            (Sync + "ApplyResult", F(Replication)),
            (Sync + "BufferHandler`1", F(Replication)),
            (Sync + "DataHandler`1", F(Replication)),
            (Sync + "DeletionCascade", F(Replication)),
            (Sync + "DerivedKey", F(Replication)),
            (Sync + "DerivedLinkCleanupSystem", F(Replication)),
            (Sync + "DerivedLinkGuard", F(Replication)),
            (Sync + "DerivedLinkRun", F(Replication)),
            (Sync + "EntityCodec", F(Replication)),
            (Sync + "EntityRefCollector", F(Replication)),
            (Sync + "EntityRefListHandler`1", F(Replication)),
            (Sync + "FamilyClassifier", F(Replication)),
            (Sync + "HouseholdAnimalListHandler", F(Replication)),
            (Sync + "HouseholdCitizenListHandler", F(Replication)),
            (Sync + "NetIdSystem", F(Replication)),
            (Sync + "NetReader", F(Replication)),
            (Sync + "NetReaderState", F(Replication)),
            (Sync + "NetReferenceGuardSystem", F(Replication)),
            (Sync + "NetWriter", F(Replication)),
            (Sync + "NetWriterState", F(Replication)),
            (Sync + "OrphanSweep", F(Replication)),
            (Sync + "PrefabTableCache", F(Replication)),
            (Sync + "PresenceHandler", F(Replication)),
            (Sync + "ReferenceAudit", F(Replication)),
            (Sync + "RemovalCascadeSystem", F(Replication)),
            (Sync + "RenterListHandler", F(Replication)),
            (Sync + "RoadConnectionGuardSystem", F(Replication)),
            (Sync + "RouteSegmentsHandler", F(Replication)),
            (Sync + "RouteWaypointsHandler", F(Replication)),
            (Sync + "StructureFacts", F(Replication)),
            (Sync + "StudentListHandler", F(Replication)),
            (Sync + "SyncApplySystem", F(Replication)),
            (Sync + "SyncBindSystem", F(Replication)),
            (Sync + "SyncCaptureSystem", F(Replication)),
            (Sync + "SyncController", F(Replication)),
            (Sync + "SyncToolApplySystem", F(Replication)),
            (Sync + "SystemSwitch", F(Replication)),
            (Sync + "TypeHandler", F(Replication)),
            (Sync + "TypeHandlers", F(Replication)),
            (Sync + "UpdateFrameGuardSystem", F(Replication)),

            // the switchable sync features
            (Sync + "ActionSync", F(Actions)),
            (Sync + "AgentLinkAccess", F(Agents)),
            (Sync + "AgentSync", F(Agents)),
            (Sync + "AttentionValues", F(SimValues)),
            (Sync + "CellMapSync", F(SimValues)),
            (Sync + "SignalFollower", F(SimValues)), // traffic lights follow the attention values (guarded by simvals)
            (Sync + "StatisticsSync", F(SimValues)),
            (Sync + "ChirpDrainSystem", F(Events)),
            (Sync + "EventSync", F(Events)),
            (Sync + "ClientGate", F(Gate)),
            (Sync + "ViewerGateSystem", F(Gate)),
            (Sync + "FlowStatusSync", F(Flow)),
            (Sync + "FollowerClock", F(Clock)),
            (Sync + "GlobalStateSync", F(Global)),
            (Sync + "IconAuthorityFilterSystem", F(Icons)),
            (Sync + "IconSync", F(Icons)),
            (Sync + "IntegrityHasher", F(Integrity)),
            (Sync + "IntegritySync", F(Integrity)),
            (Sync + "LineCheck", F(Integrity)),
            (Sync + "MoverApplySystem", F(Movers)),
            (Sync + "MoverCaptureSystem", F(Movers)),
            (Sync + "MoverClient", F(Movers)),
            (Sync + "MoverHost", F(Movers)),
            (Sync + "MoverSync", F(Movers)),
            (Sync + "MoverWriteSystem", F(Movers)),
            (Sync + "TerrainSync", F(Terrain)),
            (Sync + "UploadTextures", F(Terrain)), // the terrain's GPU upload path
            (Sync + "GpuReadback`1", F(Terrain, Water)), // the GPU read path of terrain and water
            (Sync + "WaterSync", F(Water)),
        };

        private static string[] F(params string[] features) => features;

        /// <summary>
        /// The feature ids for code of <paramref name="typeName"/> (a full name in reflection form) and, when known,
        /// <paramref name="method"/> (a name or signature, <c>Name(Type, ...)</c>): an empty array for "no feature", null
        /// when no entry matches.
        /// </summary>
        public static string[] Of(string typeName, string method = null)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            string type = Normalize(typeName, method, out string name);
            foreach ((string key, string[] features) in Entries)
            {
                int sep = key.IndexOf("::", StringComparison.Ordinal);
                if (sep >= 0)
                {
                    // Type::Method: that type (nested and generated types normalised) and exactly that method name
                    if (name != null && type.Length == sep && string.CompareOrdinal(key, 0, type, 0, sep) == 0
                        && key.Length - sep - 2 == name.Length && string.CompareOrdinal(key, sep + 2, name, 0, name.Length) == 0)
                    {
                        return features;
                    }
                    continue;
                }
                if (key.EndsWith(".", StringComparison.Ordinal))
                {
                    if (type.StartsWith(key, StringComparison.Ordinal)) return features;
                    continue;
                }
                if (type == key || type.StartsWith(key, StringComparison.Ordinal) && type[key.Length] == '+') return features;
            }
            return null;
        }

        /// <summary>
        /// The user type and method name an entry is matched with: compiler-generated nested types (<c>Outer+&lt;Run&gt;d__3</c>,
        /// <c>Outer+&lt;&gt;c__DisplayClass5_0</c>) count as their outer type; lambdas and local functions
        /// (<c>&lt;Run&gt;b__0</c>, <c>&lt;Run&gt;g__Local|2_0</c>) and state machines count as the method they are in.
        /// </summary>
        internal static string Normalize(string typeName, string method, out string name)
        {
            name = method;
            if (name != null)
            {
                int paren = name.IndexOf('(');
                if (paren >= 0) name = name.Substring(0, paren);
                if (name.StartsWith("<", StringComparison.Ordinal))
                {
                    int close = name.IndexOf('>');
                    if (close > 1) name = name.Substring(1, close - 1);
                }
            }
            string type = typeName;
            int generated = type.IndexOf("+<", StringComparison.Ordinal);
            if (generated >= 0)
            {
                string nested = type.Substring(generated + 2);
                type = type.Substring(0, generated);
                int close = nested.IndexOf('>');
                if (close > 0) name = nested.Substring(0, close); // a state machine: its method
            }
            return type;
        }

        /// <summary>Every feature id the entries name (the test checks each against the game layer's registry).</summary>
        public static IEnumerable<string> FeatureIds()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string _, string[] features) in Entries)
            {
                foreach (string f in features)
                {
                    if (seen.Add(f)) yield return f;
                }
            }
        }
    }
}
