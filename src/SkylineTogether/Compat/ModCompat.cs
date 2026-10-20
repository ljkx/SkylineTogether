using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Game.Modding;
using Game.SceneFlow;
using SkylineTogether.Core;
using SkylineTogether.Core.Modding;
using SkylineTogether.Sync;
using Unity.Entities;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// The registry of third-party mod adapters (docs/design/MOD-COMPATIBILITY.md). Detects the loaded mods by assembly
    /// name, checks every name a built-in adapter relies on (an adapter whose mod changed turns itself off, Core
    /// <see cref="AdapterCheck"/>), analyses every loaded code mod (<see cref="ReportFor"/>: scan, declarations, Core
    /// <see cref="ModClassifier"/>) into a <see cref="GenericModAdapter"/>, and applies the rules of all active adapters at
    /// the session events: tool-local and never-replicated types for the codec, watched types for the
    /// <see cref="ModStateWatcher"/>, client gates (systems off on a joining or live client, restored exactly when the
    /// session ends) and the simulation systems a following client keeps. Main thread only; every step is guarded, a
    /// broken adapter only loses itself.
    /// </summary>
    public static class ModCompat
    {
        private static readonly ModAdapter[] All =
        {
            new TrafficAdapter(), new MoveItAdapter(), new AnarchyAdapter(), new BetterBulldozerAdapter(), new TreeControllerAdapter(),
            new WaterFeaturesAdapter(), new PlopTheGrowablesAdapter(), new FiveTwentyNineTilesAdapter(), new HistoricalStartAdapter(),
            new ZoningToolkitAdapter(), new LineToolAdapter(), new FindItAdapter(),
        };

        private static readonly List<ModAdapter> ActiveList = new List<ModAdapter>();
        private static readonly Dictionary<Assembly, ModReport> Reports = new Dictionary<Assembly, ModReport>();
        private static readonly Dictionary<Assembly, GenericModAdapter> Generic = new Dictionary<Assembly, GenericModAdapter>();
        private static readonly HashSet<int> LocalTypeIndices = new HashSet<int>();
        private static readonly HashSet<int> WatchedVanillaIndices = new HashSet<int>();
        private static readonly HashSet<string> ExcludedTypes = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> ClientSimulation = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Pipeline = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<(ComponentSystemBase System, bool Original)> JoinGated = new List<(ComponentSystemBase, bool)>();
        private static readonly List<(ComponentSystemBase System, bool Original)> LiveGated = new List<(ComponentSystemBase, bool)>();
        private static Dictionary<Type, List<Game.SystemUpdatePhase>> _phases;
        private static Dictionary<Assembly, List<string>> _patches;
        private static bool _contextRead;
        private static bool _detected;

        /// <summary>The built-in adapters (loaded or not).</summary>
        public static IReadOnlyList<ModAdapter> Adapters => All;

        /// <summary>The adapters whose rules apply: healthy built-in ones and the generic adapter of every loaded mod.</summary>
        public static IReadOnlyList<ModAdapter> ActiveAdapters => ActiveList;

        /// <summary>
        /// The code mods the game loaded (other than Skyline Together) with their versions, from the game's mod manager.
        /// </summary>
        public static List<(Assembly Assembly, string Version)> LoadedMods()
        {
            var list = new List<(Assembly, string)>();
            Assembly own = typeof(ModCompat).Assembly;
            ModManager manager = GameManager.instance?.modManager;
            if (manager == null) return list;
            foreach (ModManager.ModInfo info in manager)
            {
                try
                {
                    if (!info.isValid || !info.isLoaded || info.asset == null) continue;
                    Assembly assembly = info.asset.assembly;
                    if (assembly == null || assembly == own) continue;
                    bool known = false;
                    foreach ((Assembly a, string _) in list) known |= a == assembly;
                    if (!known) list.Add((assembly, info.asset.version?.ToString() ?? ""));
                }
                catch (Exception)
                {
                    // a broken entry is not analysed
                }
            }
            return list;
        }

        /// <summary>
        /// Detects which adapted mods are loaded (by assembly name among the loaded assemblies), checks their names,
        /// analyses every loaded mod and resolves the rules. Called before replication starts and again at each session
        /// start; the analysis of a mod is computed once per game.
        /// </summary>
        public static void Refresh(ILog log)
        {
            try
            {
                var byName = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (a.IsDynamic) continue;
                        string name = a.GetName().Name;
                        if (!byName.ContainsKey(name)) byName[name] = a;
                    }
                    catch (Exception)
                    {
                        // an assembly that cannot describe itself is not one of ours
                    }
                }
                ActiveList.Clear();
                LocalTypeIndices.Clear();
                WatchedVanillaIndices.Clear();
                ExcludedTypes.Clear();
                ClientSimulation.Clear();
                Pipeline.Clear();
                var loaded = new List<string>();
                foreach (ModAdapter adapter in All)
                {
                    byName.TryGetValue(adapter.AssemblyName, out Assembly assembly);
                    bool first = adapter.Assembly != assembly || !_detected;
                    adapter.Assembly = assembly;
                    if (assembly == null) continue;
                    // mod compatibility: an adapter is knowledge about one version of its mod; any name gone means the mod
                    // changed, and half of the rules could contradict the other half, so the whole adapter goes off
                    AdapterCheck check = Check(adapter);
                    adapter.DisabledReason = check.Describe(adapter.ModName, VersionOf(assembly));
                    if (adapter.DisabledReason != null)
                    {
                        if (first) log?.Warn(LogCategory.Compat, "mod compatibility: " + adapter.DisabledReason);
                        continue;
                    }
                    loaded.Add(adapter.ModName);
                    ActiveList.Add(adapter);
                }
                foreach ((Assembly assembly, string _) in LoadedMods())
                {
                    if (!Generic.TryGetValue(assembly, out GenericModAdapter generic))
                    {
                        ModReport report = ReportFor(assembly, log);
                        if (report == null) continue;
                        generic = new GenericModAdapter(assembly, NameOf(assembly), report);
                        Generic[assembly] = generic;
                    }
                    if (generic.HasRules) ActiveList.Add(generic);
                }
                foreach (ModAdapter adapter in ActiveList)
                {
                    foreach (string name in adapter.LocalTypes) AddTypeIndex(LocalTypeIndices, adapter.FindType(name), adapter, name, log);
                    foreach (string name in adapter.WatchedVanillaTypes) AddTypeIndex(WatchedVanillaIndices, typeof(Game.Common.Owner).Assembly.GetType(name, false), adapter, name, log);
                    foreach (string name in adapter.NeverReplicatedTypes) ExcludedTypes.Add(name);
                    foreach (string name in adapter.ClientSimulationSystems) ClientSimulation.Add(name);
                    foreach (string name in adapter.PipelineTools) Pipeline.Add(name);
                }
                if (!_detected || loaded.Count > 0) log?.Info(LogCategory.Compat, "mod compatibility: adapters active for " + (loaded.Count == 0 ? "no installed mod" : string.Join(", ", loaded)));
                _detected = true;
            }
            catch (Exception ex)
            {
                log?.Warn(LogCategory.Compat, "mod compatibility: detecting mods failed", ex);
            }
        }

        /// <summary>Every name the adapter relies on, resolved against the loaded mod (and the game for vanilla types).</summary>
        private static AdapterCheck Check(ModAdapter adapter)
        {
            var check = new AdapterCheck();
            try
            {
                foreach (string name in adapter.LocalTypes) check.Expect("type", name, adapter.FindType(name) != null);
                foreach (string name in adapter.NeverReplicatedTypes) check.Expect("type", name, adapter.FindType(name) != null);
                foreach (string name in adapter.WatchedVanillaTypes) check.Expect("game type", name, typeof(Game.Common.Owner).Assembly.GetType(name, false) != null);
                foreach (string name in adapter.ClientOffSystems) check.Expect("system", name, ResolveSystem(adapter, name) != null);
                foreach (string name in adapter.JoinOffSystems) check.Expect("system", name, ResolveSystem(adapter, name) != null);
                foreach (string name in adapter.ClientSimulationSystems) check.Expect("system", name, ResolveSystem(adapter, name) != null);
                foreach (string name in adapter.PipelineTools) check.Expect("tool", name, adapter.FindType(name) != null);
                foreach (string key in adapter.SyncedSettings)
                {
                    string typeName = adapter.SettingsTypeFor(key);
                    Type settings = typeName != null ? adapter.FindType(typeName) : FirstSettingsType(adapter.Assembly);
                    PropertyInfo p = settings?.GetProperty(key, BindingFlags.Instance | BindingFlags.Public);
                    check.Expect("setting", (typeName ?? "?") + "." + key, p != null && p.CanRead && p.CanWrite && SkylineTogether.Core.Session.SettingValueText.IsSupported(p.PropertyType));
                }
            }
            catch (Exception ex)
            {
                check.Expect("check", ex.GetType().Name, false);
            }
            return check;
        }

        private static Type FirstSettingsType(Assembly assembly)
        {
            foreach (Type t in ModAssemblyScanner.LoadableTypes(assembly))
            {
                if (t != null && !t.IsAbstract && typeof(ModSetting).IsAssignableFrom(t)) return t;
            }
            return null;
        }

        private static void AddTypeIndex(HashSet<int> set, Type type, ModAdapter adapter, string name, ILog log)
        {
            if (type == null)
            {
                log?.Debug(LogCategory.Compat, "mod compatibility: " + adapter.ModName + " has no type " + name + " (mod updated?)");
                return;
            }
            try
            {
                TypeIndex index = TypeManager.GetTypeIndex(type);
                if (index != TypeIndex.Null) set.Add(index.Value);
            }
            catch (Exception)
            {
                log?.Debug(LogCategory.Compat, "mod compatibility: " + name + " is not a component type");
            }
        }

        // ---------------------------------------------------------------------------------------------- analysis

        /// <summary>
        /// The analysis of one loaded mod (computed once, logged once): its scan (<see cref="ModAssemblyScanner"/>), its
        /// declarations (<see cref="ModIntegration"/>) and the decisions of Core's <see cref="ModClassifier"/>. Null only
        /// when <paramref name="assembly"/> is null.
        /// </summary>
        public static ModReport ReportFor(Assembly assembly, ILog log)
        {
            if (assembly == null) return null;
            if (Reports.TryGetValue(assembly, out ModReport cached)) return cached;
            log = log ?? Mod.Log;
            ModReport report;
            try
            {
                ReadContext(log);
                ModFacts facts = ModAssemblyScanner.Scan(assembly, _phases, _patches);
                report = ModClassifier.Classify(facts, ModIntegration.DeclarationsFor(assembly));
            }
            catch (Exception ex)
            {
                report = ModClassifier.Classify(new ModFacts { AssemblyName = SafeName(assembly), ScanFailed = true, ScanError = ex.Message });
            }
            // the join check consults the built-in list first (verified by hand); say so where the scan disagrees
            if (report.WorldAffecting && Core.Session.ModCompatibility.IsKnownClientLocal(report.AssemblyName, null))
            {
                report.Hints.Add("the built-in list knows this mod as client-local; that decides the join check");
            }
            else if (!report.WorldAffecting && Core.Session.ModCompatibility.IsKnownWorldAffecting(report.AssemblyName, null))
            {
                report.Hints.Add("the built-in list knows this mod as world-affecting; that decides the join check");
            }
            Reports[assembly] = report;
            log?.Info(LogCategory.Compat, "mod analysis: " + report.AssemblyName + " " + VersionOf(assembly) + " is " + report.Summary() + " (" + string.Join("; ", report.ScopeReasons) + ")");
            if (report.Problems.Count > 0) log?.Warn(LogCategory.Compat, "mod analysis: " + report.AssemblyName + " declaration problems (ignored): " + string.Join(" | ", report.Problems));
            return report;
        }

        /// <summary>The system phases and Harmony's patch list, read once for all mods (both may stay unknown).</summary>
        private static void ReadContext(ILog log)
        {
            if (_contextRead) return;
            _contextRead = true;
            try
            {
                _phases = Diagnostics.Probes.SystemPhases(World.DefaultGameObjectInjectionWorld);
            }
            catch (Exception ex)
            {
                log?.Debug(LogCategory.Compat, "mod analysis: system phases unreadable (" + ex.Message + ")");
            }
            _patches = HarmonyPatches.ByPatchingAssembly(log);
            if (_phases == null) log?.Warn(LogCategory.Compat, "mod analysis: the system update phases could not be read; every mod system counts as world-affecting");
        }

        /// <summary>The readable name of a loaded mod (its assembly name).</summary>
        public static string NameOf(Assembly assembly)
        {
            foreach (ModAdapter a in All)
            {
                if (a.Assembly == assembly) return a.ModName;
            }
            return SafeName(assembly);
        }

        private static string SafeName(Assembly assembly)
        {
            try
            {
                return assembly.GetName().Name;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string VersionOf(Assembly assembly)
        {
            foreach ((Assembly a, string version) in LoadedMods())
            {
                if (a == assembly) return version;
            }
            return "";
        }

        // ---------------------------------------------------------------------------------------------- hooks

        /// <summary>FamilyClassifier hook: true for a type an adapter lists as tool-local (its entities never replicate).</summary>
        public static bool IsLocalType(TypeIndex index) => LocalTypeIndices.Count > 0 && LocalTypeIndices.Contains(index.Value);

        /// <summary>TypeHandlers hook: a saved mod type that is declared never to be sent (its entity still replicates).</summary>
        public static bool IsExcludedType(Type type) => ExcludedTypes.Count > 0 && type != null && ExcludedTypes.Contains(type.FullName ?? "");

        /// <summary>ModStateWatcher hook: vanilla types an adapter asks to watch (written by a mod without tags).</summary>
        public static IEnumerable<int> WatchedVanillaTypes => WatchedVanillaIndices;

        /// <summary>FollowerClock hook: a mod's simulation system that keeps running on a following client.</summary>
        public static bool IsClientSimulationAllowed(Type system) => system != null && ClientSimulation.Contains(system.FullName ?? "");

        /// <summary>SyncController hook: a mod tool that applies through the Temp pipeline (not a direct-write tool).</summary>
        public static bool IsPipelineTool(Type tool) => tool != null && Pipeline.Contains(tool.FullName ?? "");

        /// <summary>
        /// The host placement checks the active adapters relax (ARCHITECTURE.md#host-validation-and-commit,
        /// <see cref="ModAdapter.RelaxedPlacementChecks"/>); <paramref name="by"/> (optional) receives "mod (checks)" lines.
        /// </summary>
        public static SkylineTogether.Core.Sync.PlacementChecks RelaxedPlacementChecks(List<string> by)
        {
            var relaxed = SkylineTogether.Core.Sync.PlacementChecks.None;
            foreach (ModAdapter adapter in ActiveList)
            {
                SkylineTogether.Core.Sync.PlacementChecks r = adapter.RelaxedPlacementChecks;
                if (r == SkylineTogether.Core.Sync.PlacementChecks.None) continue;
                relaxed |= r;
                by?.Add(adapter.ModName + " (" + r + ")");
            }
            return relaxed;
        }

        /// <summary>The active adapter of a loaded mod that synchronises <paramref name="key"/>, or null.</summary>
        public static ModAdapter FindWithSetting(string assemblyName, string key)
        {
            foreach (ModAdapter a in ActiveList)
            {
                if (!string.Equals(a.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string s in a.SyncedSettings)
                {
                    if (string.Equals(s, key, StringComparison.Ordinal)) return a;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------------------------------------- client gates

        /// <summary>
        /// Client, every frame: the mod systems gated here stay off (a mod that switched one back on, for example when a
        /// city finished loading, ran it with the client's own settings). <paramref name="flipped"/> gets each one switched
        /// off again. Only the mod gates: the game's own gated systems are toggled on purpose (the route preview window).
        /// </summary>
        public static int ReassertGates(Action<ComponentSystemBase> flipped)
        {
            int n = 0;
            foreach ((ComponentSystemBase system, bool _) in JoinGated)
            {
                if (!SkylineTogether.Sync.SystemSwitch.Reassert(system)) continue;
                n++;
                flipped?.Invoke(system);
            }
            foreach ((ComponentSystemBase system, bool _) in LiveGated)
            {
                if (!SkylineTogether.Sync.SystemSwitch.Reassert(system)) continue;
                n++;
                flipped?.Invoke(system);
            }
            return n;
        }

        /// <summary>A client starts joining: load-time mutators of the player's own settings are switched off.</summary>
        public static void OnClientStarted(ILog log)
        {
            Refresh(log);
            foreach (ModAdapter a in ActiveList)
            {
                foreach (string name in a.JoinOffSystems) TurnOff(a, name, JoinGated, log);
            }
        }

        /// <summary>The client follows the host: systems that would derive host-owned state are switched off.</summary>
        public static void OnClientLive(ILog log)
        {
            foreach (ModAdapter a in ActiveList)
            {
                foreach (string name in a.ClientOffSystems) TurnOff(a, name, LiveGated, log);
            }
            if (JoinGated.Count + LiveGated.Count > 0) log?.Info(LogCategory.Gate, "mod compatibility: " + (JoinGated.Count + LiveGated.Count) + " mod systems off on this client");
        }

        /// <summary>Session end: every switched system goes back to its original state.</summary>
        public static void OnSessionEnded(ILog log)
        {
            Restore(LiveGated);
            Restore(JoinGated);
        }

        private static void TurnOff(ModAdapter adapter, string name, List<(ComponentSystemBase, bool)> into, ILog log)
        {
            try
            {
                World world = World.DefaultGameObjectInjectionWorld;
                if (world == null) return;
                Type type = ResolveSystem(adapter, name);
                if (type == null)
                {
                    log?.Warn(LogCategory.Gate, "mod compatibility: " + adapter.ModName + " system not found: " + name + " (mod updated?)");
                    return;
                }
                ComponentSystemBase system = world.GetExistingSystemManaged(type);
                if (system == null) return;
                foreach ((ComponentSystemBase s, bool _) in into)
                {
                    if (s == system) return;
                }
                into.Add((system, SystemSwitch.TurnOff(system)));
            }
            catch (Exception ex)
            {
                log?.Warn(LogCategory.Gate, "mod compatibility: switching off " + name + " failed", ex);
            }
        }

        /// <summary>A system type by full name; <c>PostDeserialize:T</c> names the game's post-deserialize wrapper of T.</summary>
        private static Type ResolveSystem(ModAdapter adapter, string name)
        {
            const string Post = "PostDeserialize:";
            if (!name.StartsWith(Post, StringComparison.Ordinal)) return adapter.FindType(name);
            Type inner = adapter.FindType(name.Substring(Post.Length));
            return inner == null ? null : typeof(Game.Serialization.PostDeserialize<>).MakeGenericType(inner);
        }

        private static void Restore(List<(ComponentSystemBase System, bool Original)> list)
        {
            foreach ((ComponentSystemBase system, bool original) in list)
            {
                try
                {
                    SystemSwitch.Restore(system, original);
                }
                catch (Exception)
                {
                    // the world may be gone
                }
            }
            list.Clear();
        }

        /// <summary>The diagnostics section: adapters, the analysis of every loaded mod and the integration API.</summary>
        public static string Describe()
        {
            var sb = new StringBuilder();
            foreach (ModAdapter a in All)
            {
                sb.Append("  ").Append(a.ModName).Append(a.Active ? ": " + a.Summary : a.Loaded ? ": " + a.DisabledReason : ": not installed").Append('\n');
            }
            sb.Append("  gated systems: ").Append(JoinGated.Count).Append(" join, ").Append(LiveGated.Count).Append(" live\n");
            var lines = new List<string>();
            ModIntegration.Describe(lines);
            foreach (string l in lines) sb.Append(l).Append('\n');
            sb.Append("  analysis of every loaded mod:\n");
            foreach ((Assembly assembly, string _) in LoadedMods())
            {
                ModReport report = ReportFor(assembly, null);
                if (report == null) continue;
                foreach (string l in report.Describe()) sb.Append("    ").Append(l).Append('\n');
            }
            return sb.ToString();
        }
    }
}
