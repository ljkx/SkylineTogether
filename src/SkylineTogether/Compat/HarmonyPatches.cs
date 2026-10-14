using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using SkylineTogether.Core;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// Reads Harmony's list of patched methods by reflection (docs/design/MOD-COMPATIBILITY.md §6): which methods each
    /// mod patches, found through the declaring assembly of every prefix, postfix, transpiler and finalizer. Skyline
    /// Together has no reference to Harmony; every loaded <c>0Harmony</c> (mods may ship different copies, which share
    /// their patch state) is asked through its public static <c>Harmony.GetAllPatchedMethods</c> and
    /// <c>Harmony.GetPatchInfo</c>. Nothing of another mod runs. Null when no Harmony is loaded or none could be read.
    /// </summary>
    public static class HarmonyPatches
    {
        private static readonly string[] PatchLists = { "Prefixes", "Postfixes", "Transpilers", "Finalizers", "ILManipulators" };

        /// <summary>
        /// The patched methods (<c>Namespace.Type.Method</c>) per assembly that declares the patch methods; null when the
        /// patch list is unknown (no Harmony loaded, or reading it failed).
        /// </summary>
        public static Dictionary<Assembly, List<string>> ByPatchingAssembly(ILog log)
        {
            Dictionary<Assembly, List<string>> result = null;
            foreach (Assembly harmony in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (harmony.IsDynamic || !string.Equals(harmony.GetName().Name, "0Harmony", StringComparison.OrdinalIgnoreCase)) continue;
                    Type type = harmony.GetType("HarmonyLib.Harmony", false);
                    MethodInfo all = type?.GetMethod("GetAllPatchedMethods", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    MethodInfo info = type?.GetMethod("GetPatchInfo", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(MethodBase) }, null);
                    if (all == null || info == null) continue;
                    var map = result ?? new Dictionary<Assembly, List<string>>();
                    if (!(all.Invoke(null, null) is IEnumerable originals)) continue;
                    foreach (object o in originals)
                    {
                        if (!(o is MethodBase original)) continue;
                        object patches = info.Invoke(null, new object[] { original });
                        if (patches == null) continue;
                        string target = (original.DeclaringType?.FullName ?? "?") + "." + original.Name;
                        foreach (string list in PatchLists)
                        {
                            if (!(Member(patches, list) is IEnumerable entries)) continue;
                            foreach (object patch in entries)
                            {
                                MethodInfo method = (Member(patch, "PatchMethod") ?? Member(patch, "patch")) as MethodInfo;
                                Assembly owner = method?.DeclaringType?.Assembly;
                                if (owner == null) continue;
                                if (!map.TryGetValue(owner, out List<string> targets)) map[owner] = targets = new List<string>();
                                if (!targets.Contains(target)) targets.Add(target);
                            }
                        }
                    }
                    result = map;
                }
                catch (Exception ex)
                {
                    log?.Debug(LogCategory.Compat, "mod analysis: reading Harmony's patch list failed (" + ex.GetType().Name + ": " + ex.Message + ")");
                }
            }
            return result;
        }

        private static object Member(object o, string name)
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type t = o.GetType();
            PropertyInfo p = t.GetProperty(name, Flags);
            if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o);
            return t.GetField(name, Flags)?.GetValue(o);
        }
    }
}
