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
            new AchievementFixerAdapter(), new CimCensusAdapter(), new EmploymentTrackerAdapter(),
            new PlacemakerAdapter(),
            new PrefabAssetFixesAdapter(), new TransportPolicyAdjusterAdapter(), new MagicHearseAdapter(), new RoadSpeedAdjusterAdapter(),
            new AdvancedSimulationSpeedAdapter(), new SimpleModCheckerPlusAdapter(),
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
        // off from the start of a join until the client is live (ModAdapter.LoadOffSystems)
        private static readonly List<(ComponentSystemBase System, bool Original)> LoadGated = new List<(ComponentSystemBase, bool)>();
        private static readonly HashSet<int> ClientCreatedIndices = new HashSet<int>();
        // runtime prefabs created on demand (MOD-COMPATIBILITY.md §7): the session's admissions, the prefabs the host names
        private static readonly RuntimePrefabBook RuntimePrefabs = new RuntimePrefabBook();
        private static readonly List<Entity> Announcements = new List<Entity>();
        private static readonly HashSet<string> RuntimeLogged = new HashSet<string>(StringComparer.Ordinal);
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
                ClientCreatedIndices.Clear();
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
                    foreach (string name in adapter.ClientCreatedTypes) AddTypeIndex(ClientCreatedIndices, adapter.FindType(name), adapter, name, log);
                    if (adapter.InternalChannel != null && adapter.ChannelSender == null)
                    {
                        ModAdapter target = adapter;
                        adapter.ChannelSender = ModIntegration.RegisterInternal(adapter.InternalChannel, "Skyline Together's " + adapter.ModName + " adapter",
                            (from, payload) => target.OnChannelMessage(from, payload));
                    }
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
                foreach (string name in adapter.LoadOffSystems) check.Expect("system", name, ResolveSystem(adapter, name) != null);
                foreach (string name in adapter.ClientCreatedTypes) check.Expect("type", name, adapter.FindType(name) != null);
                foreach (string name in adapter.Members) check.Expect("member", name, adapter.FindMember(name) != null);
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
            ModFacts facts;
            try
            {
                ReadContext(log);
                facts = ModAssemblyScanner.Scan(assembly, _phases, _patches);
                report = ModClassifier.Classify(facts, ModIntegration.DeclarationsFor(assembly));
            }
            catch (Exception ex)
            {
                facts = new ModFacts { AssemblyName = SafeName(assembly), ScanFailed = true, ScanError = ex.Message };
                report = ModClassifier.Classify(facts);
            }
            // a client-local classification checked by hand holds only for a copy like the checked version
            report.BeyondVerifiedLocal = Core.Session.ModCompatibility.BeyondVerifiedLocal(facts);
            if (report.BeyondVerifiedLocal != null && report.BeyondVerifiedLocal.Count > 0)
            {
                report.Hints.Add("the built-in list knows this mod as client-local for the version checked by hand, but this copy also has: " + string.Join(", ", report.BeyondVerifiedLocal) + "; the analysis decides the join check");
                log?.Warn(LogCategory.Compat, "mod analysis: " + report.AssemblyName + " " + VersionOf(assembly) + " differs from the version its built-in classification was checked against (" + string.Join(", ", report.BeyondVerifiedLocal) + "); classified by its analysis");
            }
            // the join check consults the built-in list first (verified by hand); say so where the scan disagrees
            else if (report.WorldAffecting && Core.Session.ModCompatibility.IsKnownClientLocal(report.AssemblyName, null))
            {
                report.Hints.Add("the built-in list knows this mod as client-local" + (report.BeyondVerifiedLocal != null ? " (checked against this version)" : "") + "; that decides the join check");
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
        /// SyncController hook (client): an unbound entity with one of these types was created by a mod as the player's
        /// edit (<see cref="ModAdapter.ClientCreatedTypes"/>), so it is proposed as a spawn.
        /// </summary>
        public static bool HasClientCreatedType(EntityManager em, Entity e)
        {
            if (ClientCreatedIndices.Count == 0) return false;
            foreach (int index in ClientCreatedIndices)
            {
                if (em.HasComponent(e, ComponentType.ReadOnly(new TypeIndex { Value = index }))) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------------------------------------- runtime prefabs

        /// <summary>
        /// SyncController hook: whether a prefab entity has a net id (set while a session runs). Adapters use it to find
        /// their own on-demand prefabs the host has not named yet.
        /// </summary>
        public static Func<Entity, bool> IsPrefabBound;

        /// <summary>
        /// A runtime prefab another peer uses and this game lacks (docs/design/MOD-COMPATIBILITY.md §7): the active adapter
        /// that creates it on demand asks its mod to (the first time) and says whether it is ready. At most
        /// <see cref="RuntimePrefabBook.MaxPrefabs"/> distinct prefabs per session; <paramref name="type"/> may be null
        /// (only the name is known). Main thread; never throws.
        /// </summary>
        public static RuntimePrefabState RuntimePrefab(string type, string name, ILog log)
        {
            if (string.IsNullOrEmpty(name)) return RuntimePrefabState.NotHandled;
            foreach (ModAdapter a in ActiveList)
            {
                if (a is GenericModAdapter || !a.HandlesRuntimePrefab(type, name)) continue;
                if (!RuntimePrefabs.Admit((type ?? "?") + ":" + name))
                {
                    if (RuntimeLogged.Add("limit")) log?.Warn(LogCategory.Compat, "mod compatibility: more than " + RuntimePrefabBook.MaxPrefabs + " runtime prefabs requested in this session; further ones count as missing");
                    return RuntimePrefabState.Refused;
                }
                RuntimePrefabState state;
                try
                {
                    state = a.ProvideRuntimePrefab(type, name);
                }
                catch (Exception ex)
                {
                    if (RuntimeLogged.Add("throw:" + a.ModName)) log?.Warn(LogCategory.Compat, "mod compatibility: " + a.ModName + " could not create the runtime prefab '" + name + "'", ex);
                    state = RuntimePrefabState.Refused;
                }
                if (state == RuntimePrefabState.NotHandled) continue;
                if (RuntimeLogged.Add(name)) log?.Info(LogCategory.Compat, "mod compatibility: " + a.ModName + " creates the runtime prefab '" + name + "' here (" + state + ")");
                return state;
            }
            return RuntimePrefabState.NotHandled;
        }

        /// <summary>Host: a runtime prefab an adapter created for a client; the next capture binds it and names it to everyone.</summary>
        public static void AnnouncePrefab(Entity prefab)
        {
            if (prefab != Entity.Null && !Announcements.Contains(prefab)) Announcements.Add(prefab);
        }

        /// <summary>Host capture: takes the prefabs to name (<see cref="AnnouncePrefab"/>).</summary>
        public static void TakeAnnouncements(List<Entity> into)
        {
            into.AddRange(Announcements);
            Announcements.Clear();
        }

        /// <summary>Every frame of a session (host, or a live client): the active built-in adapters' own work.</summary>
        public static void Tick(bool host, double now, ILog log)
        {
            foreach (ModAdapter a in ActiveList)
            {
                if (a is GenericModAdapter) continue;
                try
                {
                    a.Tick(host, now);
                }
                catch (Exception ex)
                {
                    if (RuntimeLogged.Add("tick:" + a.ModName)) log?.Warn(LogCategory.Compat, "mod compatibility: " + a.ModName + " adapter work failed", ex);
                }
            }
        }

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

        /// <summary>
        /// GlobalStateSync hook (host): how many slider spans beyond their range the values of route policies may be while
        /// an active adapter's mod computes route modifiers beyond the slider (<see cref="ModAdapter.RoutePolicyRangeSlack"/>).
        /// </summary>
        public static float RoutePolicyRangeSlack
        {
            get
            {
                float slack = 0f;
                foreach (ModAdapter a in ActiveList) slack = Math.Max(slack, a.RoutePolicyRangeSlack);
                return slack;
            }
        }

        /// <summary>
        /// SpeedAuthoritySystem hook (host): an active adapter's mod lets its player select exact game speeds
        /// (<see cref="ModAdapter.AllowsFineGameSpeed"/>), so a joined player's exact speed request is honoured.
        /// </summary>
        public static bool FineGameSpeedAllowed
        {
            get
            {
                foreach (ModAdapter a in ActiveList)
                {
                    if (a.AllowsFineGameSpeed) return true;
                }
                return false;
            }
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
            foreach ((ComponentSystemBase system, bool _) in LoadGated)
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
                foreach (string name in a.LoadOffSystems) TurnOff(a, name, LoadGated, log);
            }
        }

        /// <summary>The client follows the host: systems that would derive host-owned state are switched off.</summary>
        public static void OnClientLive(ILog log)
        {
            foreach (ModAdapter a in ActiveList)
            {
                foreach (string name in a.ClientOffSystems) TurnOff(a, name, LiveGated, log);
            }
            // the systems held off during the load come back once their adapter undid what the load queued for them
            foreach (ModAdapter a in ActiveList)
            {
                if (a is GenericModAdapter) continue;
                try
                {
                    a.OnClientLive();
                }
                catch (Exception ex)
                {
                    log?.Warn(LogCategory.Gate, "mod compatibility: " + a.ModName + " live step failed", ex);
                }
            }
            if (LoadGated.Count > 0) log?.Info(LogCategory.Gate, "mod compatibility: " + LoadGated.Count + " mod systems held off during the load are on again");
            Restore(LoadGated);
            if (JoinGated.Count + LiveGated.Count > 0) log?.Info(LogCategory.Gate, "mod compatibility: " + (JoinGated.Count + LiveGated.Count) + " mod systems off on this client");
        }

        /// <summary>Session end: every switched system goes back to its original state.</summary>
        public static void OnSessionEnded(ILog log)
        {
            Restore(LiveGated);
            Restore(LoadGated);
            Restore(JoinGated);
            RuntimePrefabs.Clear();
            RuntimeLogged.Clear();
            Announcements.Clear();
            foreach (ModAdapter a in All)
            {
                try
                {
                    if (a.Active) a.OnSessionEnded();
                }
                catch (Exception ex)
                {
                    log?.Warn(LogCategory.Compat, "mod compatibility: " + a.ModName + " session end failed", ex);
                }
            }
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
            sb.Append("  gated systems: ").Append(JoinGated.Count).Append(" join, ").Append(LiveGated.Count).Append(" live, ").Append(LoadGated.Count).Append(" until live\n");
            sb.Append("  runtime prefabs requested in this session: ").Append(RuntimePrefabs.Admitted).Append('\n');
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
