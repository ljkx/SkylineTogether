using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Serialization.Entities;
using Game;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Session;
using Unity.Entities;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// Scans one code mod's assembly into <see cref="ModFacts"/> (docs/design/MOD-COMPATIBILITY.md §6): its ECS component
    /// types (saved or not, enableable, buffers, entity references), its systems with the update phases the game
    /// registered them in, its tools, prefab types, runtime prefab creation, settings properties and Harmony patches.
    /// Only metadata is read: type and member information, method bodies as IL bytes (<see cref="IlScanner"/>, whose
    /// tokens are resolved to see what the mod calls) and the game's own registration lists; no code of the mod runs. The
    /// decisions are made by Core's <see cref="ModClassifier"/>. A scan that throws marks the facts as failed (the mod is
    /// then world-affecting, fail closed). Main thread (the phase lists are read from the world).
    /// </summary>
    public static class ModAssemblyScanner
    {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        /// <summary>Scans <paramref name="assembly"/>. <paramref name="phases"/> and <paramref name="patches"/> may be null (unknown).</summary>
        public static ModFacts Scan(Assembly assembly, Dictionary<Type, List<SystemUpdatePhase>> phases, Dictionary<Assembly, List<string>> patches)
        {
            var facts = new ModFacts { PhasesKnown = phases != null };
            try
            {
                facts.AssemblyName = assembly.GetName().Name;
                foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
                {
                    if (string.Equals(reference.Name, "0Harmony", StringComparison.OrdinalIgnoreCase)) facts.ReferencesHarmony = true;
                }
                if (patches != null) facts.PatchedMethods = patches.TryGetValue(assembly, out List<string> targets) ? new List<string>(targets) : new List<string>();
                var calls = new CallIndex(assembly);
                foreach (Type t in LoadableTypes(assembly))
                {
                    if (t == null) continue;
                    try
                    {
                        Classify(t, facts, phases, calls);
                    }
                    catch (Exception ex) when (ex is TypeLoadException || ex is System.IO.FileNotFoundException || ex is NotSupportedException || ex is BadImageFormatException)
                    {
                        // a type that references a missing assembly cannot be inspected; it cannot run either
                    }
                }
                facts.RuntimePrefabCreators.AddRange(calls.PrefabCreators);
                // after every type was visited (a system's jobs may come after it)
                foreach (SystemFact s in facts.Systems) s.WritesEntities = calls.WritesEntities(s.Name);
            }
            catch (Exception ex)
            {
                facts.ScanFailed = true;
                facts.ScanError = ex.GetType().Name + ": " + ex.Message;
            }
            return facts;
        }

        private static void Classify(Type t, ModFacts facts, Dictionary<Type, List<SystemUpdatePhase>> phases, CallIndex calls)
        {
            calls.Visit(t);
            if (t.IsAbstract && !t.IsSealed && !t.IsInterface) // abstract classes are no systems, prefabs or settings of their own
            {
                return;
            }
            if (t.IsInterface || t.IsGenericTypeDefinition) return;
            bool serializable = typeof(ISerializable).IsAssignableFrom(t);
            bool empty = typeof(IEmptySerializable).IsAssignableFrom(t);
            if (t.IsValueType && (typeof(IComponentData).IsAssignableFrom(t) || typeof(IBufferElementData).IsAssignableFrom(t) || typeof(ISharedComponentData).IsAssignableFrom(t)))
            {
                ComponentStorage storage = typeof(ISharedComponentData).IsAssignableFrom(t) ? ComponentStorage.Shared
                    : typeof(IBufferElementData).IsAssignableFrom(t) ? ComponentStorage.Buffer
                    : HasInstanceFields(t) ? ComponentStorage.Data : ComponentStorage.Tag;
                facts.Components.Add(new ComponentFact
                {
                    Name = t.FullName,
                    Storage = storage,
                    Serializable = serializable,
                    EmptySerializable = empty,
                    Enableable = typeof(IEnableableComponent).IsAssignableFrom(t),
                    SerializeAsEnabled = typeof(ISerializeAsEnabled).IsAssignableFrom(t),
                    HasEntityReferences = HasEntityFields(t, 0),
                });
                return;
            }
            if (!t.IsValueType && typeof(IComponentData).IsAssignableFrom(t))
            {
                facts.Components.Add(new ComponentFact { Name = t.FullName, Storage = ComponentStorage.Managed, Serializable = serializable, EmptySerializable = empty });
                return;
            }
            if (typeof(ComponentSystemBase).IsAssignableFrom(t))
            {
                var system = new SystemFact
                {
                    Name = t.FullName,
                    // system state the game saves with the city (IDefaultSerializable systems are ISerializable too; checked anyway)
                    SavesState = serializable || typeof(IDefaultSerializable).IsAssignableFrom(t),
                    IsTool = typeof(Game.Tools.ToolBaseSystem).IsAssignableFrom(t),
                    PostDeserialize = typeof(Game.Serialization.IPostDeserialize).IsAssignableFrom(t),
                };
                if (phases != null && phases.TryGetValue(t, out List<SystemUpdatePhase> registered))
                {
                    foreach (SystemUpdatePhase p in registered) system.Phases.Add(p.ToString());
                }
                if (system.IsTool) system.UsesCreationDefinitions = calls.MentionsCreationDefinition(t);
                facts.Systems.Add(system);
                return;
            }
            if (typeof(Game.Prefabs.PrefabBase).IsAssignableFrom(t) || typeof(Game.Prefabs.ComponentBase).IsAssignableFrom(t))
            {
                facts.PrefabTypes.Add(t.FullName);
                return;
            }
            if (typeof(Game.Modding.ModSetting).IsAssignableFrom(t))
            {
                foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.DeclaringType == null || p.DeclaringType.Assembly != t.Assembly || p.GetIndexParameters().Length != 0) continue;
                    bool settable = p.CanRead && p.CanWrite && p.GetSetMethod(false) != null && p.GetGetMethod(false) != null;
                    facts.Settings.Add(new SettingFact { SettingsType = t.FullName, Name = p.Name, Supported = settable && SettingValueText.IsSupported(p.PropertyType) });
                }
            }
        }

        /// <summary>The types of an assembly, also when some of them cannot load (those are skipped).</summary>
        public static Type[] LoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types ?? Type.EmptyTypes;
            }
        }

        private static bool HasInstanceFields(Type t) => t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length > 0;

        private static bool HasEntityFields(Type t, int depth)
        {
            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Type ft = f.FieldType;
                if (ft == typeof(Entity)) return true;
                if (depth < 2 && ft.IsValueType && !ft.IsPrimitive && !ft.IsEnum && ft.Assembly != typeof(int).Assembly && HasEntityFields(ft, depth + 1)) return true;
            }
            return false;
        }

        /// <summary>
        /// The IL view of one assembly: which methods create prefabs at runtime (calls of <c>PrefabSystem.AddPrefab</c> or
        /// <c>AddOrUpdatePrefab</c>) and which types mention <c>CreationDefinition</c> (tools that feed the Temp pipeline).
        /// Tokens are resolved in the declaring method's generic context; unresolvable ones are skipped. Outside tools only
        /// member references to other assemblies are resolved (the prefab system is one), which keeps the scan cheap.
        /// </summary>
        private sealed class CallIndex
        {
            private const int MemberRefTable = 0x0A;
            private const int MethodSpecTable = 0x2B; // generic method instantiations (EntityManager.SetComponentData<T>)
            private static readonly Type CreationDefinition = typeof(Game.Tools.CreationDefinition);
            private readonly Assembly _assembly;
            private readonly List<IlToken> _tokens = new List<IlToken>();
            private readonly HashSet<Type> _mentions = new HashSet<Type>();
            private readonly Dictionary<int, MemberInfo> _resolved = new Dictionary<int, MemberInfo>();
            private readonly HashSet<string> _entityWriters = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<string> PrefabCreators = new List<string>();

            /// <summary>True when the system type (or a type nested in it) calls an entity write (<see cref="IsEntityWrite"/>).</summary>
            public bool WritesEntities(string systemName) => systemName != null && _entityWriters.Contains(systemName);

            /// <summary>The outermost system type around <paramref name="t"/> (itself included), or null.</summary>
            private static Type EnclosingSystem(Type t)
            {
                Type found = null;
                for (Type o = t; o != null; o = o.DeclaringType)
                {
                    if (typeof(ComponentSystemBase).IsAssignableFrom(o)) found = o;
                }
                return found;
            }

            private static readonly HashSet<string> EntityWriteNames = new HashSet<string>(StringComparer.Ordinal)
            {
                "AddComponent", "AddComponentData", "SetComponentData", "RemoveComponent", "AddBuffer", "SetBuffer", "AppendToBuffer",
                "CreateEntity", "DestroyEntity", "Instantiate", "SetComponentEnabled", "AddSharedComponent", "SetSharedComponent",
                "AddSharedComponentManaged", "SetSharedComponentManaged",
            };

            /// <summary>An entity write of <c>EntityManager</c> or <c>EntityCommandBuffer</c> (and its parallel writer).</summary>
            private static bool IsEntityWrite(MethodBase m)
            {
                Type d = m.DeclaringType;
                if (d == null || !EntityWriteNames.Contains(m.Name)) return false;
                return d == typeof(EntityManager) || d == typeof(EntityCommandBuffer) || d == typeof(EntityCommandBuffer.ParallelWriter);
            }

            public CallIndex(Assembly assembly)
            {
                _assembly = assembly;
            }

            private static bool InTool(Type t)
            {
                for (Type o = t; o != null; o = o.DeclaringType)
                {
                    if (typeof(Game.Tools.ToolBaseSystem).IsAssignableFrom(o)) return true;
                }
                return false;
            }

            public void Visit(Type t)
            {
                bool tool = InTool(t);
                Type system = EnclosingSystem(t);
                if (tool)
                {
                    foreach (FieldInfo f in SafeFields(t))
                    {
                        if (Mentions(f.FieldType)) _mentions.Add(t);
                    }
                }
                foreach (MethodBase m in SafeMethods(t))
                {
                    byte[] il;
                    try
                    {
                        il = m.GetMethodBody()?.GetILAsByteArray();
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (il == null || il.Length == 0) continue;
                    _tokens.Clear();
                    IlScanner.Scan(il, _tokens);
                    Type[] typeArgs = null, methodArgs = null;
                    try
                    {
                        typeArgs = t.IsGenericType ? t.GetGenericArguments() : null;
                        methodArgs = m.IsGenericMethod ? m.GetGenericArguments() : null;
                    }
                    catch (Exception)
                    {
                        // resolved without generic context below
                    }
                    foreach (IlToken token in _tokens)
                    {
                        bool call = token.Kind == IlOperand.Method && token.Token >> 24 == MemberRefTable;
                        bool spec = system != null && token.Kind == IlOperand.Method && token.Token >> 24 == MethodSpecTable;
                        if (!call && !spec && !tool) continue;
                        MemberInfo member = Resolve(m.Module, token.Token, typeArgs, methodArgs);
                        if (member == null) continue;
                        if (call && member is MethodBase called && IsPrefabCreation(called))
                        {
                            string where = (t.FullName ?? t.Name) + "." + m.Name;
                            if (!PrefabCreators.Contains(where)) PrefabCreators.Add(where);
                        }
                        if ((call || spec) && system != null && member is MethodBase write && IsEntityWrite(write)) _entityWriters.Add(system.FullName ?? system.Name);
                        if (tool && MentionsMember(member)) _mentions.Add(t);
                    }
                }
            }

            /// <summary>True when the tool or one of its nested types (its jobs) mentions <c>CreationDefinition</c>.</summary>
            public bool MentionsCreationDefinition(Type tool)
            {
                foreach (Type t in _mentions)
                {
                    for (Type o = t; o != null; o = o.DeclaringType)
                    {
                        if (o == tool) return true;
                    }
                }
                return false;
            }

            private static bool IsPrefabCreation(MethodBase m)
            {
                return m.DeclaringType == typeof(Game.Prefabs.PrefabSystem) && (m.Name == "AddPrefab" || m.Name == "AddOrUpdatePrefab");
            }

            private static bool MentionsMember(MemberInfo member)
            {
                switch (member)
                {
                    case Type type:
                        return Mentions(type);
                    case MethodInfo method:
                        if (Mentions(method.DeclaringType)) return true;
                        if (method.IsGenericMethod)
                        {
                            foreach (Type a in method.GetGenericArguments())
                            {
                                if (Mentions(a)) return true;
                            }
                        }
                        return false;
                    case MemberInfo other:
                        return Mentions(other.DeclaringType);
                }
                return false;
            }

            private static bool Mentions(Type t)
            {
                if (t == null) return false;
                if (t == CreationDefinition) return true;
                if (t.HasElementType) return Mentions(t.GetElementType());
                if (!t.IsGenericType) return false;
                foreach (Type a in t.GetGenericArguments())
                {
                    if (Mentions(a)) return true;
                }
                return false;
            }

            private MemberInfo Resolve(Module module, int token, Type[] typeArgs, Type[] methodArgs)
            {
                // tokens resolve alike outside a generic context; those are looked up once per assembly
                bool plain = typeArgs == null && methodArgs == null && module == _assembly.ManifestModule;
                if (plain && _resolved.TryGetValue(token, out MemberInfo known)) return known;
                MemberInfo member;
                try
                {
                    member = module.ResolveMember(token, typeArgs, methodArgs);
                }
                catch (Exception)
                {
                    member = null;
                }
                if (plain) _resolved[token] = member;
                return member;
            }

            private IEnumerable<FieldInfo> SafeFields(Type t)
            {
                try
                {
                    return t.Assembly == _assembly ? t.GetFields(Declared) : Array.Empty<FieldInfo>();
                }
                catch (Exception)
                {
                    return Array.Empty<FieldInfo>();
                }
            }

            private static IEnumerable<MethodBase> SafeMethods(Type t)
            {
                var list = new List<MethodBase>();
                try
                {
                    list.AddRange(t.GetMethods(Declared));
                    list.AddRange(t.GetConstructors(Declared));
                }
                catch (Exception)
                {
                    // partial list
                }
                return list;
            }
        }

        // ------------------------------------------------------------------------------------------------- the join check

        /// <summary>
        /// True when the mod may touch shared state (the handshake's evidence, <see cref="ModCompatibility"/>): the
        /// classification of <see cref="ModCompat.ReportFor"/>; null assemblies and failures count as true (fail closed).
        /// </summary>
        public static bool RegistersSharedState(Assembly assembly)
        {
            if (assembly == null) return true;
            ModReport report = ModCompat.ReportFor(assembly, null);
            return report == null || report.WorldAffecting;
        }
    }
}
