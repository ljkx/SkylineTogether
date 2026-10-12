using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Serialization.Entities;
using Game;
using Game.Notifications;
using Game.SceneFlow;
using Game.Simulation;
using Game.UI.InGame;
using SkylineTogether.Core;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Sync;
using Steamworks;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using ILog = SkylineTogether.Core.ILog;

namespace SkylineTogether.Diagnostics
{
    /// <summary>
    /// The startup self-test (ARCH D6, §12.3, §13.4, Appendix B): every non-public member and every type the mod finds
    /// by name, every Steam API it depends on and the UI module, resolved in one place. Runs at <c>OnLoad</c> (types and
    /// members), again when a city has loaded (world-dependent checks: system instances, field values, the system
    /// registration lists) and once the UI had time to observe the panel's bindings. The results go to the log, the
    /// diagnostics dump and a one-line summary in the panel; each failed probe switches its feature off
    /// (<see cref="Features"/>) so it degrades cleanly instead of throwing later. The resolved members are kept here and
    /// used by the features, so a feature never touches a member its probe did not verify. Main thread only.
    /// </summary>
    public static class Probes
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const double UiProbeSeconds = 20;

        private static readonly List<ProbeResult> Results = new List<ProbeResult>();
        private static bool _cityProbed;
        private static string _uiResult; // null: unknown yet
        private static double _uiWatchSince = -1;

        // ---------------------------------------------------------------- resolved members (null when the probe failed)

        /// <summary>P-CTX: internal <c>ContextFormat.EnableTag(int)</c> (full-tag serialization context).</summary>
        public static MethodInfo ContextEnableTag { get; private set; }

        /// <summary>P-CLOCK: the private setter of <c>SimulationSystem.frameIndex</c>.</summary>
        public static Action<SimulationSystem, uint> SetFrameIndex { get; private set; }

        /// <summary>P-SIMTIMER: <c>SimulationSystem.m_Timer</c> (float), for holding one tick; optional.</summary>
        public static FieldInfo SimulationTimer { get; private set; }

        /// <summary>P-SYSTEMS: <c>UpdateSystem.m_Systems</c>, <c>m_RefMap</c> and <c>SystemData.m_Phase/m_System</c>.</summary>
        public static FieldInfo UpdateSystems { get; private set; }

        public static FieldInfo UpdateRefMap { get; private set; }

        public static FieldInfo SystemDataPhase { get; private set; }

        public static FieldInfo SystemDataSystem { get; private set; }

        /// <summary>P-BARRIER: <c>TimeUISystem.m_SimulationPausedBarrierBinding</c>; optional.</summary>
        public static FieldInfo PausedBarrierBinding { get; private set; }

        /// <summary>P-ICONQUEUES: <c>IconCommandSystem.m_Queues</c> and <c>m_Dependencies</c>.</summary>
        public static FieldInfo IconQueues { get; private set; }

        public static FieldInfo IconDependencies { get; private set; }

        /// <summary>P-NAMES: <c>NameSystem.m_Names</c> (custom names); optional.</summary>
        public static FieldInfo NameSystemNames { get; private set; }

        /// <summary>w3/audit P-MAIL: <c>MailAccumulationSystem.m_LastAccumulatedMail/m_LastProcessedMail</c> (post panel); optional.</summary>
        public static FieldInfo MailAccumulated { get; private set; }

        public static FieldInfo MailProcessed { get; private set; }

        /// <summary>w3/audit P-CHIRPLINKS: <c>ChirpLinkSystem.m_CachedChirpData</c> (chirp sender and link names); optional.</summary>
        public static FieldInfo ChirpLinkCache { get; private set; }

        /// <summary>The latest report as text lines (log and diagnostics).</summary>
        public static IEnumerable<string> ReportLines() => Features.Registry.ReportLines();

        /// <summary>One line for the panel.</summary>
        public static string Summary => Features.Registry.Summary();

        // ---------------------------------------------------------------- stages

        /// <summary><c>OnLoad</c>: types, members and Steam.</summary>
        public static void RunStartup(ILog log)
        {
            Run("startup", log, world: false);
        }

        /// <summary>After a city finished loading: everything again, plus the world-dependent checks.</summary>
        public static void RunCityLoaded(ILog log)
        {
            bool first = !_cityProbed;
            _cityProbed = true;
            Run(first ? "first city loaded" : "city loaded", log, world: true);
        }

        /// <summary>
        /// Once per frame (driver): watches whether the UI module observes the panel's state binding once the menu or a
        /// city is shown; reports P-UI when it does, or after <see cref="UiProbeSeconds"/> when it does not.
        /// </summary>
        public static void Tick(ILog log, double now)
        {
            if (_uiResult != null) return;
            GameManager game = GameManager.instance;
            if (game == null || game.isGameLoading || (game.gameMode != GameMode.MainMenu && game.gameMode != GameMode.Game))
            {
                _uiWatchSince = -1;
                return;
            }
            if (_uiWatchSince < 0) _uiWatchSince = now;
            int observers = UI.SessionUISystem.StateObservers;
            if (observers > 0) _uiResult = "";
            else if (now - _uiWatchSince > UiProbeSeconds) _uiResult = "the panel's UI module did not load (no observer of skylineTogether.state); use the Options page";
            else return;
            Run(_cityProbed ? "city loaded, UI checked" : "startup, UI checked", log, world: _cityProbed);
            if (_uiResult.Length > 0)
            {
                Session.SessionManager.Notify(Settings.LocaleSource.Resolve("st.toast.uiMissing"), Colossal.PSI.Common.ProgressState.Warning);
            }
        }

        // ---------------------------------------------------------------- the probes

        private static void Run(string stage, ILog log, bool world)
        {
            Results.Clear();
            Safe("P-CTX", Features.Replication, "ContextFormat.EnableTag(int)", ProbeContext);
            Safe("P-CLOCK", Features.Clock, "SimulationSystem.frameIndex setter", ProbeFrameIndex);
            Safe("P-SIMTIMER", Features.Clock, "SimulationSystem.m_Timer", ProbeTimer, warningOnly: true);
            Safe("P-SYSTEMS", Features.Clock, "UpdateSystem.m_Systems/m_RefMap/SystemData", ProbeUpdateSystemLayout);
            Safe("P-BARRIER", Features.Clock, "TimeUISystem.m_SimulationPausedBarrierBinding", ProbeBarrierField, warningOnly: true);
            Safe("P-ICONQUEUES", Features.Icons, "IconCommandSystem.m_Queues/m_Dependencies", ProbeIconQueues);
            Safe("P-NAMES", Features.GlobalState, "NameSystem.m_Names", ProbeNameTable, warningOnly: true);
            // gameplay branch: ViewerGateSystem clears a viewer's tool apply through this protected setter
            Safe("P-APPLYMODE", Features.Gate, "ToolBaseSystem.applyMode setter", () => typeof(Game.Tools.ToolBaseSystem).GetProperty("applyMode", AnyInstance)?.GetSetMethod(true) != null
                ? null : "not found: viewers' tool applies are refused by the host instead", warningOnly: true);
            // w2/simvals: the chart sample counter is written on clients (StatisticsSync)
            Safe("P-STATS", Features.SimValues, "CityStatisticsSystem.m_SampleCount/m_LastSampleFrameIndex", () =>
                typeof(Game.Simulation.CityStatisticsSystem).GetField("m_SampleCount", AnyInstance)?.FieldType == typeof(int)
                && typeof(Game.Simulation.CityStatisticsSystem).GetField("m_LastSampleFrameIndex", AnyInstance)?.FieldType == typeof(uint)
                    ? null : "not found: client charts may be dated by their own counter", warningOnly: true);
            // gameplay branch: the host's pause menu is told apart from a session pause (SpeedAuthoritySystem)
            Safe("P-PAUSEMENU", Features.Speed, "TimeUISystem.m_UnpausedBeforeForcedPause/m_SpeedBeforePause", () =>
                typeof(TimeUISystem).GetField("m_UnpausedBeforeForcedPause", Private) != null && typeof(TimeUISystem).GetField("m_SpeedBeforePause", Private) != null
                    ? null : "not found: the host's pause menu pauses the session", warningOnly: true);
            // w3/audit: runtime panel values and chirp names written on clients (GlobalStateSync.Panels)
            Safe("P-MAIL", Features.GlobalState, "MailAccumulationSystem.m_LastAccumulatedMail/m_LastProcessedMail", ProbeMail, warningOnly: true);
            Safe("P-CHIRPLINKS", Features.Events, "ChirpLinkSystem.m_CachedChirpData", ProbeChirpLinks, warningOnly: true);
            ProbeDemandHandles();
            ProbeTypeNames("P-GATE", Features.Gate, ClientGate.GatedSystemNames, systems: true);
            ProbeTypeNames("P-GATE", Features.Gate, ClientGate.RouteSystemNames, systems: true);
            ProbeTypeNames("P-MOVERGATE", Features.Movers, MoverClient.GatedSystemNames, systems: true);
            ProbeTypeNames("P-COSMETIC", Features.Clock, FollowerClock.CosmeticSystemNames, systems: true);
            ProbeTypeNames("P-FAMILY", Features.Replication, FamilyClassifier.MarkerTypeNames, systems: false);
            ProbeTypeNames("P-TYPES", Features.Replication, TypeHandlers.ExcludedTypeNames, systems: false);
            ProbeTypeNames("P-HASH", Features.Integrity, IntegrityHasher.StructuralTypeNames, systems: false);
            ProbeSteam();
            if (world) ProbeWorld();
            if (_uiResult != null)
            {
                Results.Add(new ProbeResult { Id = "P-UI", Feature = Features.Ui.Id, Target = "UI module observes the panel", Ok = _uiResult.Length == 0, Detail = _uiResult.Length == 0 ? null : _uiResult });
            }
            Features.Registry.SetReport(stage, Results);
            if (log == null) return;
            bool clean = true;
            foreach (ProbeResult r in Results) clean &= r.Ok;
            foreach (string line in ReportLines())
            {
                if (clean) log.Info(LogCategory.Probe, line);
                else if (line.Contains("FAIL") || line.Contains("WARN")) log.Warn(LogCategory.Probe, line);
                else log.Info(LogCategory.Probe, line);
            }
        }

        private static void Safe(string id, FeatureHealth feature, string target, Func<string> probe, bool warningOnly = false)
        {
            string detail;
            try
            {
                detail = probe();
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
            }
            Results.Add(new ProbeResult { Id = id, Feature = feature.Id, Target = target, Ok = detail == null, WarningOnly = warningOnly, Detail = detail });
        }

        private static string ProbeContext()
        {
            MethodInfo m = typeof(ContextFormat).GetMethod("EnableTag", Private, null, new[] { typeof(int) }, null);
            ContextEnableTag = m;
            return m == null ? "not found (version-dependent fields of replicated components would decode wrongly)" : null;
        }

        private static string ProbeFrameIndex()
        {
            SetFrameIndex = null;
            PropertyInfo p = typeof(SimulationSystem).GetProperty("frameIndex", AnyInstance);
            MethodInfo setter = p?.GetSetMethod(true);
            if (setter == null || p.PropertyType != typeof(uint)) return "not found";
            SetFrameIndex = (Action<SimulationSystem, uint>)Delegate.CreateDelegate(typeof(Action<SimulationSystem, uint>), setter);
            return null;
        }

        private static string ProbeTimer()
        {
            FieldInfo f = typeof(SimulationSystem).GetField("m_Timer", Private);
            SimulationTimer = f != null && f.FieldType == typeof(float) ? f : null;
            return SimulationTimer == null ? "not found: the follower clock corrects a lead by jumping only" : null;
        }

        private static string ProbeUpdateSystemLayout()
        {
            UpdateSystems = UpdateRefMap = SystemDataPhase = SystemDataSystem = null;
            FieldInfo systems = typeof(UpdateSystem).GetField("m_Systems", Private);
            FieldInfo map = typeof(UpdateSystem).GetField("m_RefMap", Private);
            Type data = typeof(UpdateSystem).GetNestedType("SystemData", BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo phase = data?.GetField("m_Phase", AnyInstance);
            FieldInfo system = data?.GetField("m_System", AnyInstance);
            if (systems == null || map == null || phase == null || system == null) return "layout changed (m_Systems " + (systems != null) + ", m_RefMap " + (map != null) + ", SystemData " + (data != null) + ")";
            if (!typeof(IList).IsAssignableFrom(systems.FieldType) || !typeof(IDictionary).IsAssignableFrom(map.FieldType)) return "unexpected field types";
            if (phase.FieldType != typeof(SystemUpdatePhase) || !typeof(ComponentSystemBase).IsAssignableFrom(system.FieldType)) return "unexpected SystemData field types";
            UpdateSystems = systems;
            UpdateRefMap = map;
            SystemDataPhase = phase;
            SystemDataSystem = system;
            return null;
        }

        private static string ProbeBarrierField()
        {
            FieldInfo f = typeof(TimeUISystem).GetField("m_SimulationPausedBarrierBinding", Private);
            PausedBarrierBinding = f;
            if (f == null) return "not found: a local pause is not forwarded to the host (speed changes are)";
            return f.FieldType.GetProperty("observerCount", AnyInstance)?.PropertyType == typeof(int) ? null : "observerCount missing";
        }

        private static string ProbeIconQueues()
        {
            IconQueues = IconDependencies = null;
            FieldInfo queues = typeof(IconCommandSystem).GetField("m_Queues", Private);
            FieldInfo deps = typeof(IconCommandSystem).GetField("m_Dependencies", Private);
            if (queues == null || deps == null) return "not found: local warning icons are corrected only after they appear";
            if (queues.FieldType != typeof(List<NativeQueue<IconCommandBuffer.Command>>) || deps.FieldType != typeof(JobHandle)) return "unexpected field types";
            IconQueues = queues;
            IconDependencies = deps;
            return null;
        }

        private static string ProbeNameTable()
        {
            FieldInfo f = typeof(Game.UI.NameSystem).GetField("m_Names", Private);
            NameSystemNames = f != null && f.FieldType == typeof(Dictionary<Entity, string>) ? f : null;
            return NameSystemNames == null ? "not found: custom names will not sync" : null;
        }

        private static string ProbeMail()
        {
            FieldInfo acc = typeof(Game.Simulation.MailAccumulationSystem).GetField("m_LastAccumulatedMail", Private);
            FieldInfo proc = typeof(Game.Simulation.MailAccumulationSystem).GetField("m_LastProcessedMail", Private);
            bool ok = acc != null && proc != null && acc.FieldType == typeof(int) && proc.FieldType == typeof(int);
            MailAccumulated = ok ? acc : null;
            MailProcessed = ok ? proc : null;
            return ok ? null : "not found: the post office panel's mail rates stay at the join snapshot on clients";
        }

        private static string ProbeChirpLinks()
        {
            FieldInfo f = typeof(Game.UI.InGame.ChirpLinkSystem).GetField("m_CachedChirpData", Private);
            ChirpLinkCache = f != null && f.FieldType == typeof(Dictionary<Entity, Game.UI.InGame.ChirpLinkSystem.CachedChirpData>) ? f : null;
            return ChirpLinkCache == null ? "not found: chirps of citizens show no sender name on clients" : null;
        }

        private static void ProbeDemandHandles()
        {
            foreach ((Type type, string field) in GlobalStateSync.ClientJobHandleFields)
            {
                Safe("P-DEMAND", Features.GlobalState, type.Name + "." + field, () => JobHandleField(type, field) != null ? null : "not found (that channel is applied without completing the system's readers)", warningOnly: true);
            }
        }

        /// <summary>A private <c>JobHandle</c> field, or null.</summary>
        public static FieldInfo JobHandleField(Type type, string name)
        {
            FieldInfo f = type?.GetField(name, Private);
            return f != null && f.FieldType == typeof(JobHandle) ? f : null;
        }

        /// <summary>Types the mod finds by name: each missing one is a warning (the feature runs without it).</summary>
        private static void ProbeTypeNames(string id, FeatureHealth feature, IEnumerable<string> names, bool systems)
        {
            int total = 0;
            var missing = new List<string>();
            foreach (string name in names)
            {
                total++;
                Type t = null;
                try
                {
                    t = typeof(Game.Common.Owner).Assembly.GetType(name, false);
                }
                catch (Exception)
                {
                    t = null;
                }
                if (t == null || (systems && !typeof(ComponentSystemBase).IsAssignableFrom(t))) missing.Add(name);
            }
            foreach (string name in missing)
            {
                Results.Add(new ProbeResult { Id = id, Feature = feature.Id, Target = name, Ok = false, WarningOnly = true, Detail = systems ? "system not found (renamed by a game update?)" : "type not found" });
            }
            if (missing.Count < total || total == 0)
            {
                Results.Add(new ProbeResult { Id = id, Feature = feature.Id, Target = (total - missing.Count) + " of " + total + " names resolved", Ok = missing.Count == 0, WarningOnly = true });
            }
        }

        private static void ProbeSteam()
        {
            Safe("P-STEAM", Features.Steam, "Steam client running and logged in", () =>
            {
                if (!SteamAPI.IsSteamRunning()) return "Steam is not running (Skyline Together needs the Steam version of the game)";
                if (!SteamUser.GetSteamID().IsValid()) return "no Steam user is logged in";
                return null;
            });
            if (!SteamOk()) return;
            Safe("P-STEAMSOCKETS", Features.Steam, "Steam Networking Sockets (poll group, connection callback)", () =>
            {
                HSteamNetPollGroup group = SteamNetworkingSockets.CreatePollGroup();
                if (group == HSteamNetPollGroup.Invalid) return "CreatePollGroup failed";
                SteamNetworkingSockets.DestroyPollGroup(group);
                Callback<SteamNetConnectionStatusChangedCallback_t> callback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(_ => { });
                callback.Dispose();
                return null;
            });
            Safe("P-RELAY", Features.Steam, "Steam relay network", () =>
            {
                ESteamNetworkingAvailability a = SteamNetworkingUtils.GetRelayNetworkStatus(out SteamRelayNetworkStatus_t _);
                return a < 0 ? "relay network unavailable (" + a + "); joining by Steam ID may fail, direct IP still works" : null;
            }, warningOnly: true);
        }

        private static bool SteamOk()
        {
            foreach (ProbeResult r in Results)
            {
                if (r.Id == "P-STEAM") return r.Ok;
            }
            return false;
        }

        private static void ProbeWorld()
        {
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null)
            {
                Results.Add(new ProbeResult { Id = "P-WORLD", Feature = Features.Replication.Id, Target = "default world", Ok = false, Detail = "no world" });
                return;
            }
            if (UpdateSystems != null)
            {
                Safe("P-SYSTEMS", Features.Clock, "GameSimulation systems found through UpdateSystem", () =>
                {
                    List<ComponentSystemBase> found = GameSimulationSystems(world);
                    return found != null ? null : "fewer than 20 systems in the GameSimulation phase (registration layout changed?)";
                });
            }
            if (IconQueues != null)
            {
                Safe("P-ICONQUEUES", Features.Icons, "IconCommandSystem queues readable", () =>
                {
                    IconCommandSystem icons = world.GetExistingSystemManaged<IconCommandSystem>();
                    if (icons == null) return "IconCommandSystem not created";
                    return IconQueues.GetValue(icons) is List<NativeQueue<IconCommandBuffer.Command>> ? null : "m_Queues is null";
                });
            }
            if (PausedBarrierBinding != null)
            {
                Safe("P-BARRIER", Features.Clock, "pause barrier binding readable", () =>
                {
                    TimeUISystem time = world.GetExistingSystemManaged<TimeUISystem>();
                    return time != null && PausedBarrierBinding.GetValue(time) != null ? null : "binding not created";
                }, warningOnly: true);
            }
            if (NameSystemNames != null)
            {
                Safe("P-NAMES", Features.GlobalState, "NameSystem names readable", () =>
                {
                    Game.UI.NameSystem names = world.GetExistingSystemManaged<Game.UI.NameSystem>();
                    return names != null && NameSystemNames.GetValue(names) != null ? null : "m_Names is null";
                }, warningOnly: true);
            }
            Safe("P-GATE", Features.Gate, "gated systems created in the world", () =>
            {
                int total = 0, found = 0;
                foreach (string name in ClientGate.GatedSystemNames)
                {
                    total++;
                    Type t = typeof(Game.Common.Owner).Assembly.GetType(name, false);
                    if (t != null && world.GetExistingSystemManaged(t) != null) found++;
                }
                return found * 2 >= total ? null : "only " + found + " of " + total + " gated systems exist";
            }, warningOnly: true);
            Safe("P-SIMULATION", Features.Speed, "SimulationSystem present", () => world.GetExistingSystemManaged<SimulationSystem>() != null ? null : "missing");
        }

        /// <summary>
        /// Every system registered in <see cref="SystemUpdatePhase.GameSimulation"/>: the direct registrations
        /// (<c>m_Systems</c>) and the ordered ones (<c>m_RefMap</c>), each a private <c>SystemData</c> with
        /// <c>m_Phase</c> and <c>m_System</c> (<c>UpdateSystem.cs:15-37, 270-292</c>). Null when P-SYSTEMS failed or the
        /// result looks wrong (vanilla registers about 300 systems in this phase).
        /// </summary>
        public static List<ComponentSystemBase> GameSimulationSystems(World world)
        {
            if (UpdateSystems == null || world == null) return null;
            UpdateSystem update = world.GetExistingSystemManaged<UpdateSystem>();
            if (update == null) return null;
            if (!(UpdateSystems.GetValue(update) is IList list) || !(UpdateRefMap.GetValue(update) is IDictionary map)) return null;
            var result = new List<ComponentSystemBase>();
            var seen = new HashSet<ComponentSystemBase>();
            void Collect(IList items)
            {
                foreach (object item in items)
                {
                    if (item == null || !(SystemDataPhase.GetValue(item) is SystemUpdatePhase phase) || phase != SystemUpdatePhase.GameSimulation) continue;
                    if (SystemDataSystem.GetValue(item) is ComponentSystemBase system && seen.Add(system)) result.Add(system);
                }
            }
            Collect(list);
            foreach (DictionaryEntry entry in map)
            {
                if (entry.Value is IList children) Collect(children);
            }
            return result.Count >= 20 ? result : null;
        }
    }
}
