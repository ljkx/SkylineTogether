using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Serialization.Entities;
using Unity.Entities;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// The handshake's evidence about one of this game's code mods (docs/design/MOD-COMPATIBILITY.md): whether it can
    /// touch shared state. A mod "registers" when its assembly declares a serializable component or buffer, a system
    /// whose state is saved, a prefab type or a tool, or ships a patch library (Harmony): any of those may change the
    /// world or the rules, so the mod must match on every peer. A mod with none of them only presents information to
    /// its own player and gets the client-local hint (the host then ignores it when only one side has it). The
    /// built-in list in <c>Core.Session.ModCompatibility</c> overrides both answers for known mods. Results are cached
    /// per assembly; a scan that fails reports "registers" (fail closed).
    /// </summary>
    public static class ModAssemblyScanner
    {
        private static readonly Dictionary<Assembly, bool> Cache = new Dictionary<Assembly, bool>();

        /// <summary>True when the mod may touch shared state (see the class summary).</summary>
        public static bool RegistersSharedState(Assembly assembly)
        {
            if (assembly == null) return true;
            lock (Cache)
            {
                if (Cache.TryGetValue(assembly, out bool cached)) return cached;
            }
            bool result = Scan(assembly);
            lock (Cache) Cache[assembly] = result;
            return result;
        }

        private static bool Scan(Assembly assembly)
        {
            try
            {
                foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
                {
                    if (string.Equals(reference.Name, "0Harmony", StringComparison.OrdinalIgnoreCase)) return true;
                }
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                foreach (Type t in types)
                {
                    if (t == null) continue;
                    bool serializable = typeof(ISerializable).IsAssignableFrom(t) || typeof(IEmptySerializable).IsAssignableFrom(t);
                    if (serializable && (typeof(IComponentData).IsAssignableFrom(t) || typeof(IBufferElementData).IsAssignableFrom(t))) return true;
                    if (typeof(ComponentSystemBase).IsAssignableFrom(t) && serializable) return true; // saved system state
                    if (typeof(Game.Prefabs.PrefabBase).IsAssignableFrom(t)) return true;
                    if (typeof(Game.Tools.ToolBaseSystem).IsAssignableFrom(t)) return true;
                }
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
