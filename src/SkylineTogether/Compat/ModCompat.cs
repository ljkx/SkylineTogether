using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using SkylineTogether.Core;
using SkylineTogether.Sync;
using Unity.Entities;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// The registry of third-party mod adapters (docs/design/MOD-COMPATIBILITY.md). Detects the loaded mods by assembly
    /// name, resolves their adapters' type and system names by reflection, and applies the per-mod rules at the session
    /// events: tool-local types for the family classifier, watched types for the <see cref="ModStateWatcher"/>, client
    /// gates (systems off on a joining or live client, restored exactly when the session ends) and the simulation
    /// systems a following client keeps. Main thread only; every step is guarded, a broken adapter only loses itself.
    /// </summary>
    public static class ModCompat
    {
        private static readonly ModAdapter[] All =
        {
            new TrafficAdapter(), new MoveItAdapter(), new AnarchyAdapter(), new BetterBulldozerAdapter(), new TreeControllerAdapter(),
            new WaterFeaturesAdapter(), new PlopTheGrowablesAdapter(), new FiveTwentyNineTilesAdapter(), new HistoricalStartAdapter(),
            new ZoningToolkitAdapter(), new LineToolAdapter(), new FindItAdapter(),
        };

        private static readonly HashSet<int> LocalTypeIndices = new HashSet<int>();
        private static readonly HashSet<int> WatchedVanillaIndices = new HashSet<int>();
        private static readonly HashSet<string> ClientSimulation = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Pipeline = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<(ComponentSystemBase System, bool Original)> JoinGated = new List<(ComponentSystemBase, bool)>();
        private static readonly List<(ComponentSystemBase System, bool Original)> LiveGated = new List<(ComponentSystemBase, bool)>();
        private static bool _detected;

        /// <summary>The known adapters (loaded or not).</summary>
        public static IReadOnlyList<ModAdapter> Adapters => All;

        /// <summary>
        /// Detects which adapted mods are loaded (by assembly name among the loaded assemblies) and resolves their types.
        /// Cheap; called before replication starts and again at each session start.
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
                LocalTypeIndices.Clear();
                WatchedVanillaIndices.Clear();
                ClientSimulation.Clear();
                Pipeline.Clear();
                var loaded = new List<string>();
                foreach (ModAdapter adapter in All)
                {
                    byName.TryGetValue(adapter.AssemblyName, out Assembly assembly);
                    adapter.Assembly = assembly;
                    if (assembly == null) continue;
                    loaded.Add(adapter.ModName);
                    foreach (string name in adapter.LocalTypes) AddTypeIndex(LocalTypeIndices, adapter.FindType(name), adapter, name, log);
                    foreach (string name in adapter.WatchedVanillaTypes) AddTypeIndex(WatchedVanillaIndices, typeof(Game.Common.Owner).Assembly.GetType(name, false), adapter, name, log);
                    foreach (string name in adapter.ClientSimulationSystems) ClientSimulation.Add(name);
                    foreach (string name in adapter.PipelineTools) Pipeline.Add(name);
                }
                if (!_detected || loaded.Count > 0) log?.Info(LogCategory.Replication, "mod compatibility: adapters active for " + (loaded.Count == 0 ? "no installed mod" : string.Join(", ", loaded)));
                _detected = true;
            }
            catch (Exception ex)
            {
                log?.Warn(LogCategory.Replication, "mod compatibility: detecting mods failed", ex);
            }
        }

        private static void AddTypeIndex(HashSet<int> set, Type type, ModAdapter adapter, string name, ILog log)
        {
            if (type == null)
            {
                log?.Debug(LogCategory.Replication, "mod compatibility: " + adapter.ModName + " has no type " + name + " (mod updated?)");
                return;
            }
            try
            {
                TypeIndex index = TypeManager.GetTypeIndex(type);
                if (index != TypeIndex.Null) set.Add(index.Value);
            }
            catch (Exception)
            {
                log?.Debug(LogCategory.Replication, "mod compatibility: " + name + " is not a component type");
            }
        }

        /// <summary>FamilyClassifier hook: true for a type an adapter lists as tool-local (its entities never replicate).</summary>
        public static bool IsLocalType(TypeIndex index) => LocalTypeIndices.Count > 0 && LocalTypeIndices.Contains(index.Value);

        /// <summary>ModStateWatcher hook: vanilla types an adapter asks to watch (written by a mod without tags).</summary>
        public static IEnumerable<int> WatchedVanillaTypes => WatchedVanillaIndices;

        /// <summary>FollowerClock hook: a mod's simulation system that keeps running on a following client.</summary>
        public static bool IsClientSimulationAllowed(Type system) => system != null && ClientSimulation.Contains(system.FullName ?? "");

        /// <summary>SyncController hook: a mod tool that applies through the Temp pipeline (not a direct-write tool).</summary>
        public static bool IsPipelineTool(Type tool) => tool != null && Pipeline.Contains(tool.FullName ?? "");

        /// <summary>The adapter of a loaded mod by its assembly name, or null.</summary>
        public static ModAdapter Find(string assemblyName)
        {
            foreach (ModAdapter a in All)
            {
                if (a.Loaded && string.Equals(a.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase)) return a;
            }
            return null;
        }

        // ---------------------------------------------------------------------------------------------- client gates

        /// <summary>A client starts joining: load-time mutators of the player's own settings are switched off.</summary>
        public static void OnClientStarted(ILog log)
        {
            Refresh(log);
            foreach (ModAdapter a in All)
            {
                if (!a.Loaded) continue;
                foreach (string name in a.JoinOffSystems) TurnOff(a, name, JoinGated, log);
            }
        }

        /// <summary>The client follows the host: systems that would derive host-owned state are switched off.</summary>
        public static void OnClientLive(ILog log)
        {
            foreach (ModAdapter a in All)
            {
                if (!a.Loaded) continue;
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

        /// <summary>The diagnostics section: which adapters are active and what they do.</summary>
        public static string Describe()
        {
            var sb = new StringBuilder();
            foreach (ModAdapter a in All)
            {
                sb.Append("  ").Append(a.ModName).Append(a.Loaded ? ": " + a.Summary : ": not installed").Append('\n');
            }
            sb.Append("  gated systems: ").Append(JoinGated.Count).Append(" join, ").Append(LiveGated.Count).Append(" live\n");
            return sb.ToString();
        }
    }
}
