using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;

namespace SkylineTogether.Core.Diagnostics
{
    /// <summary>
    /// The run-time counterpart of <c>tools/ApiCheck</c> (ARCHITECTURE.md#probes-and-self-test): resolves every type and
    /// member reference of a module against the assemblies the game actually loaded, so that a game update which removed
    /// or changed a type or member the mod was compiled against is found at startup, not the first time the code that uses
    /// it runs (Mono throws <c>MissingMethodException</c>, <c>MissingFieldException</c> or <c>TypeLoadException</c> when
    /// it compiles that method, possibly mid-session).
    /// <list type="number">
    /// <item>Every row of the module's <c>TypeRef</c> table (tokens <c>0x01000001</c>, ...) is resolved with
    /// <see cref="Module.ResolveType(int)"/> and every row of its <c>MemberRef</c> table (<c>0x0A000001</c>, ...) with
    /// <see cref="Module.ResolveMember(int)"/>, until the runtime says the row is past the table's end
    /// (<see cref="ArgumentOutOfRangeException"/>; for a member reference only when its signature cannot be read either,
    /// because CoreCLR also reports a missing field as out of range).</item>
    /// <item>Only when something failed: the IL of every method of the module is read
    /// (<see cref="MethodBody.GetILAsByteArray"/>) to find which methods use the failed references; each use is resolved
    /// again in the method's generic context (a reference of a generic type's code may need its type arguments), and
    /// failures that remain are attributed to the method's declaring type, which the caller maps to a feature. Field
    /// types, parameter, return and local types are read too (a type used only there fails when the method is compiled).
    /// Types whose base type or interface is missing are reported by <see cref="TypesOf"/>, by their own name.</item>
    /// </list>
    /// Nothing of the module runs: resolving a reference loads types and members but runs no static constructor (neither
    /// Mono nor CoreCLR initialises a type to hand out its reflection object), so it is safe on a background thread.
    /// Pure reflection over the module.
    /// </summary>
    public static class ReferenceCheck
    {
        /// <summary>A reference that does not resolve, or a type of the module that does not load.</summary>
        public sealed class Failure
        {
            /// <summary>What failed: <c>TypeRef 0x01000012</c>, <c>MemberRef 0x0A000034</c>, a field or signature, a type.</summary>
            public string Reference;
            /// <summary>The full name of the module's type whose code uses it; null when no method body, field or signature uses it.</summary>
            public string User;
            /// <summary>The method of <see cref="User"/> that uses it (a signature), or null.</summary>
            public string Method;
            /// <summary>Exception type and message (it names the missing member).</summary>
            public string Error;
            /// <summary>A binding failure (missing or changed member, type or assembly): the code cannot run against this game.</summary>
            public bool Binding;

            public override string ToString() => Reference + " used by " + (User ?? "(no code)") + (Method != null ? "." + Method : "") + ": " + Error;
        }

        /// <summary>The outcome of <see cref="Run"/>.</summary>
        public sealed class Result
        {
            /// <summary>Rows of the TypeRef and MemberRef tables resolved.</summary>
            public int TypeRefs, MemberRefs;
            /// <summary>References that failed without a generic context (before the uses were checked).</summary>
            public int FailedWithoutContext;
            /// <summary>Methods whose IL was read (0 when every reference resolved at once).</summary>
            public int MethodsScanned;
            /// <summary>Failed references no method body uses (custom attributes, metadata only): logged, nothing switched off.</summary>
            public int Unused;
            public bool TimedOut;
            public bool Cancelled;
            public double Seconds;
            public readonly List<Failure> Failures = new List<Failure>();
        }

        /// <summary>The token type of the TypeRef table.</summary>
        public const int TypeRefTable = 0x01000000;
        /// <summary>The token type of the MemberRef table.</summary>
        public const int MemberRefTable = 0x0A000000;
        private const int TypeSpecTable = 0x1B000000;
        private const int MethodSpecTable = 0x2B000000;
        // rows per table at most (a token's row index has 24 bits)
        private const int MaxRows = 0xFFFFFF;

        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        /// <summary>
        /// Resolves every type and member reference of <paramref name="module"/> (see the class summary) until done, until
        /// <paramref name="budgetSeconds"/> of <paramref name="seconds"/> have passed, or until <paramref name="cancelled"/>
        /// says so. <paramref name="types"/>: the module's types (<see cref="TypesOf"/>; null: read here, load failures
        /// are then added to the result).
        /// </summary>
        public static Result Run(Module module, Func<double> seconds, double budgetSeconds, Func<bool> cancelled = null, IReadOnlyList<Type> types = null)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            if (seconds == null) throw new ArgumentNullException(nameof(seconds));
            var result = new Result();
            double start = seconds();
            bool Stop()
            {
                if (cancelled != null && cancelled())
                {
                    result.Cancelled = true;
                    return true;
                }
                if (seconds() - start > budgetSeconds)
                {
                    result.TimedOut = true;
                    return true;
                }
                return false;
            }

            var failed = new Dictionary<int, Exception>();
            result.TypeRefs = Enumerate(TypeRefTable, t => module.ResolveType(t), failed, Stop, module);
            if (!result.TimedOut && !result.Cancelled) result.MemberRefs = Enumerate(MemberRefTable, t => module.ResolveMember(t), failed, Stop, module);
            result.FailedWithoutContext = failed.Count;
            if (failed.Count > 0 && !result.TimedOut && !result.Cancelled)
            {
                if (types == null) types = TypesOf(module.Assembly, result.Failures);
                Attribute(module, types, failed, result, Stop);
            }
            result.Seconds = seconds() - start;
            return result;
        }

        private static int Enumerate(int table, Action<int> resolve, Dictionary<int, Exception> failed, Func<bool> stop, Module module)
        {
            int rows = 0;
            for (int row = 1; row <= MaxRows; row++)
            {
                if ((row & 63) == 0 && stop()) break;
                int token = table | row;
                try
                {
                    resolve(token);
                }
                catch (ArgumentOutOfRangeException e)
                {
                    // past the table's end, except for a member reference that names a missing field: CoreCLR and .NET
                    // Framework report it as out of range too (their literal-field fallback). Only a signature read from
                    // the row proves that it exists; anything else ends the table (never a false missing field)
                    if (table != MemberRefTable || !RowExists(module, token)) break;
                    failed[token] = new MissingFieldException(e.Message);
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    failed[token] = e;
                }
                rows++;
            }
            return rows;
        }

        private static bool RowExists(Module module, int token)
        {
            try
            {
                return module.ResolveSignature(token) != null;
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                return false;
            }
        }

        // the second phase: which of the module's methods use the failed references, resolved in their generic context
        private static void Attribute(Module module, IReadOnlyList<Type> types, Dictionary<int, Exception> failed, Result result, Func<bool> stop)
        {
            bool typeRefsFailed = false, memberRefsFailed = false;
            foreach (int token in failed.Keys)
            {
                if ((token & unchecked((int)0xFF000000)) == TypeRefTable) typeRefsFailed = true;
                else memberRefsFailed = true;
            }
            var resolvedSomewhere = new HashSet<int>();
            var uses = new Dictionary<int, List<Failure>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var tokens = new List<int>();
            foreach (Type type in types)
            {
                if (type == null || type.Module != module) continue;
                if (stop()) return;
                string user = type.FullName ?? type.Name;
                // field types (a method that reads the field fails to compile when its type is gone)
                FieldInfo[] fields;
                try
                {
                    fields = type.GetFields(Declared);
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    AddDirect(result, seen, "fields", user, null, e);
                    continue;
                }
                foreach (FieldInfo f in fields)
                {
                    try
                    {
                        _ = f.FieldType;
                    }
                    catch (Exception e) when (!(e is OutOfMemoryException))
                    {
                        AddDirect(result, seen, "field " + f.Name, user, null, e);
                    }
                }
                MethodBase[] methods;
                try
                {
                    var all = new List<MethodBase>(type.GetMethods(Declared));
                    all.AddRange(type.GetConstructors(Declared));
                    methods = all.ToArray();
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    AddDirect(result, seen, "methods", user, null, e);
                    continue;
                }
                Type[] typeArgs = null;
                try
                {
                    if (type.IsGenericTypeDefinition) typeArgs = type.GetGenericArguments();
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    AddDirect(result, seen, "generic parameters", user, null, e);
                }
                foreach (MethodBase m in methods)
                {
                    string method = Signature(m);
                    MethodBody body;
                    Type[] methodArgs = null;
                    try
                    {
                        // signatures: parameter and return types
                        foreach (ParameterInfo p in m.GetParameters()) _ = p.ParameterType;
                        if (m is MethodInfo mi) _ = mi.ReturnType;
                        if (m.IsGenericMethodDefinition) methodArgs = m.GetGenericArguments();
                        body = m.GetMethodBody();
                        if (body != null)
                        {
                            foreach (LocalVariableInfo local in body.LocalVariables) _ = local.LocalType;
                        }
                    }
                    catch (Exception e) when (!(e is OutOfMemoryException))
                    {
                        AddDirect(result, seen, "signature or locals", user, method, e);
                        continue;
                    }
                    if (body == null) continue;
                    byte[] il;
                    try
                    {
                        il = body.GetILAsByteArray();
                    }
                    catch (Exception e) when (!(e is OutOfMemoryException))
                    {
                        AddDirect(result, seen, "method body", user, method, e);
                        continue;
                    }
                    result.MethodsScanned++;
                    tokens.Clear();
                    IlTokens(il, tokens);
                    foreach (int token in tokens)
                    {
                        int table = token & unchecked((int)0xFF000000);
                        bool check = failed.ContainsKey(token)
                                     || table == TypeSpecTable && typeRefsFailed
                                     || table == MethodSpecTable && (memberRefsFailed || typeRefsFailed);
                        if (!check) continue;
                        try
                        {
                            if (table == TypeRefTable || table == TypeSpecTable) module.ResolveType(token, typeArgs, methodArgs);
                            else module.ResolveMember(token, typeArgs, methodArgs);
                            resolvedSomewhere.Add(token);
                        }
                        catch (ArgumentOutOfRangeException) when (!failed.ContainsKey(token))
                        {
                            // not a token of this module (an operand misread): ignored
                        }
                        catch (Exception e) when (!(e is OutOfMemoryException))
                        {
                            // a row that exists: out of range here is the missing field of the first phase
                            if (e is ArgumentOutOfRangeException) e = failed[token];
                            if (!uses.TryGetValue(token, out List<Failure> list)) uses[token] = list = new List<Failure>();
                            if (list.Count < 16 && !list.Exists(x => x.User == user && x.Method == method))
                            {
                                list.Add(new Failure { Reference = Describe(token), User = user, Method = method, Error = Describe(e), Binding = IsBindingFailure(e) });
                            }
                        }
                    }
                }
            }
            foreach (KeyValuePair<int, List<Failure>> pair in uses)
            {
                if (resolvedSomewhere.Contains(pair.Key)) continue; // it resolves in its generic context
                result.Failures.AddRange(pair.Value);
            }
            foreach (KeyValuePair<int, Exception> pair in failed)
            {
                if (resolvedSomewhere.Contains(pair.Key) || uses.ContainsKey(pair.Key)) continue;
                int table = pair.Key & unchecked((int)0xFF000000);
                if (table == TypeRefTable && IsBindingFailure(pair.Value) && !result.Failures.Exists(f => f.Error == Describe(pair.Value)))
                {
                    // a type reference needs no generic context: it is missing, but no method body, field or signature uses
                    // it (a custom attribute, a constraint); reported, with no feature to switch off
                    result.Failures.Add(new Failure { Reference = Describe(pair.Key), Error = Describe(pair.Value), Binding = true });
                }
                else
                {
                    // a member reference only metadata uses (a custom attribute's constructor, the method of a generic
                    // instantiation): it may need a generic context this check cannot give it; counted only
                    result.Unused++;
                }
            }
        }

        private static void AddDirect(Result result, HashSet<string> seen, string what, string user, string method, Exception e)
        {
            string error = Describe(e);
            if (!seen.Add(user + "|" + method + "|" + error)) return;
            result.Failures.Add(new Failure { Reference = what, User = user, Method = method, Error = error, Binding = IsBindingFailure(e) });
        }

        /// <summary>
        /// The types of <paramref name="assembly"/>; types that fail to load (a base type or interface the game removed)
        /// are added to <paramref name="failures"/> instead, each by its own name. Which types did not load is read from the
        /// assembly's file (<see cref="TypeDefTable"/>; <paramref name="image"/> returns its bytes, by default the file at
        /// <see cref="Assembly.Location"/>): Mono's loader exception names the missing game type, not the mod's, so the
        /// mod's types are found by their names and matched to the exception by their base type and interfaces. Without a
        /// readable image the loader exceptions are reported as they are (their type name is then usually the game's,
        /// which maps to no feature: a warning).
        /// </summary>
        public static List<Type> TypesOf(Assembly assembly, List<Failure> failures, Func<byte[]> image = null)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types ?? Array.Empty<Type>();
                Exception[] loaders = Array.FindAll(e.LoaderExceptions ?? Array.Empty<Exception>(), x => x != null);
                if (failures != null && !AddUnloaded(assembly, types, loaders, image, failures))
                {
                    foreach (Exception loader in loaders)
                    {
                        failures.Add(new Failure
                        {
                            Reference = "type", User = (loader as TypeLoadException)?.TypeName, Error = Describe(loader), Binding = IsBindingFailure(loader),
                        });
                    }
                }
            }
            var list = new List<Type>(types.Length);
            foreach (Type t in types)
            {
                if (t != null) list.Add(t);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
            return list;
        }

        // the types of the assembly's TypeDef table that are not among the loaded ones, one failure each (a nested type of
        // a type that did not load is covered by it); false when the table cannot be read or names no unloaded type
        // (typeNameOf: the type a loader exception names; a test stands in for Mono with it)
        internal static bool AddUnloaded(Assembly assembly, Type[] loaded, Exception[] loaders, Func<byte[]> image, List<Failure> failures, Func<Exception, string> typeNameOf = null)
        {
            if (typeNameOf == null) typeNameOf = e => (e as TypeLoadException)?.TypeName;
            TypeDefTable table;
            try
            {
                byte[] bytes = image != null ? image() : ReadLocation(assembly);
                if (bytes == null) return false;
                table = TypeDefTable.Read(bytes);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                return false;
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Type t in loaded)
            {
                if (t?.FullName != null) names.Add(t.FullName);
            }
            var byTypeName = new Dictionary<string, Exception>(StringComparer.Ordinal);
            foreach (Exception loader in loaders)
            {
                string name = typeNameOf(loader);
                if (!string.IsNullOrEmpty(name) && !byTypeName.ContainsKey(name.Replace('/', '+'))) byTypeName[name.Replace('/', '+')] = loader;
            }
            var unloaded = new Dictionary<string, TypeDefTable.Row>(StringComparer.Ordinal);
            foreach (TypeDefTable.Row row in table.Types)
            {
                if (row.Token != 0x02000001 && !names.Contains(row.FullName)) unloaded[row.FullName] = row;
            }
            int added = 0;
            foreach (TypeDefTable.Row row in table.Types)
            {
                if (!unloaded.ContainsKey(row.FullName ?? "") || row.Enclosing != null && unloaded.ContainsKey(row.Enclosing.FullName)) continue;
                Exception cause = CauseOf(row, byTypeName, unloaded, 0, out string causeName) ?? (loaders.Length > 0 ? loaders[0] : null);
                failures.Add(new Failure
                {
                    Reference = causeName != null && causeName != row.FullName ? "type (its base type or interface " + causeName + ")" : "type",
                    User = row.FullName,
                    Error = cause != null ? Describe(cause) : "TypeLoadException: the type does not load",
                    Binding = cause == null || IsBindingFailure(cause),
                });
                added++;
            }
            return added > 0;
        }

        // the loader exception that names this type, its base type or one of its interfaces (following the mod's own bases)
        private static Exception CauseOf(TypeDefTable.Row row, Dictionary<string, Exception> byTypeName, Dictionary<string, TypeDefTable.Row> unloaded, int depth, out string causeName)
        {
            causeName = null;
            var candidates = new List<string> { row.FullName };
            if (row.BaseName != null) candidates.Add(row.BaseName);
            candidates.AddRange(row.InterfaceNames);
            foreach (string name in candidates)
            {
                if (byTypeName.TryGetValue(name, out Exception e))
                {
                    causeName = name;
                    return e;
                }
            }
            if (depth < 16)
            {
                foreach (string name in candidates)
                {
                    if (name == row.FullName || !unloaded.TryGetValue(name, out TypeDefTable.Row inner)) continue;
                    Exception e = CauseOf(inner, byTypeName, unloaded, depth + 1, out string innerCause);
                    if (e != null)
                    {
                        causeName = innerCause;
                        return e;
                    }
                }
            }
            return null;
        }

        private static byte[] ReadLocation(Assembly assembly)
        {
            string location = assembly.Location;
            return string.IsNullOrEmpty(location) || !System.IO.File.Exists(location) ? null : System.IO.File.ReadAllBytes(location);
        }

        // ------------------------------------------------------------------------------------------------- IL

        // operand size per opcode (one-byte opcodes at [b], two-byte 0xFE xx at [256 + xx]); -1: unknown opcode, -2: switch
        private static int[] _operandSize;
        // whether the operand is a metadata token of a type, field, method or member
        private static bool[] _isToken;

        private static void BuildTables()
        {
            var size = new int[512];
            var token = new bool[512];
            for (int i = 0; i < size.Length; i++) size[i] = -1;
            foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!(f.GetValue(null) is OpCode op)) continue;
                int index = op.Size == 1 ? op.Value & 0xFF : 256 + (op.Value & 0xFF);
                int n;
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: n = 0; break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar: n = 1; break;
                    case OperandType.InlineVar: n = 2; break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR: n = 8; break;
                    case OperandType.InlineSwitch: n = -2; break;
                    default: n = 4; break; // tokens, InlineI, InlineBrTarget, ShortInlineR, InlineSig, InlineString
                }
                size[index] = n;
                token[index] = op.OperandType == OperandType.InlineField || op.OperandType == OperandType.InlineMethod
                               || op.OperandType == OperandType.InlineTok || op.OperandType == OperandType.InlineType;
            }
            _isToken = token;
            _operandSize = size;
        }

        /// <summary>
        /// The metadata tokens of a method body's IL that name a type, field, method or member (call, newobj, ldfld,
        /// ldtoken, castclass, ...), in order. Stops at an opcode it does not know (a malformed body).
        /// </summary>
        public static void IlTokens(byte[] il, List<int> output)
        {
            if (il == null) throw new ArgumentNullException(nameof(il));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (_operandSize == null) BuildTables();
            int[] size = _operandSize;
            bool[] isToken = _isToken;
            int at = 0;
            while (at < il.Length)
            {
                int index = il[at++];
                if (index == 0xFE)
                {
                    if (at >= il.Length) return;
                    index = 256 + il[at++];
                }
                int n = size[index];
                if (n == -1) return;
                if (n == -2)
                {
                    if (at + 4 > il.Length) return;
                    int count = BitConverter.ToInt32(il, at);
                    if (count < 0 || count > (il.Length - at - 4) / 4) return;
                    at += 4 + count * 4;
                    continue;
                }
                if (at + n > il.Length) return;
                if (isToken[index]) output.Add(BitConverter.ToInt32(il, at));
                at += n;
            }
        }

        // ------------------------------------------------------------------------------------------------- text

        private static string Describe(int token)
        {
            int table = token & unchecked((int)0xFF000000);
            string kind = table == TypeRefTable ? "TypeRef" : table == MemberRefTable ? "MemberRef" : table == TypeSpecTable ? "TypeSpec" : table == MethodSpecTable ? "MethodSpec" : "token";
            return kind + " 0x" + token.ToString("X8", CultureInfo.InvariantCulture);
        }

        private static string Signature(MethodBase m)
        {
            var sb = new System.Text.StringBuilder(m.Name).Append('(');
            try
            {
                ParameterInfo[] parameters = m.GetParameters();
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(parameters[i].ParameterType.Name);
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                sb.Append('?');
            }
            return sb.Append(')').ToString();
        }

        /// <summary>A missing or changed member, type or assembly (the exceptions a game update causes).</summary>
        public static bool IsBindingFailure(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e is MissingMemberException || e is TypeLoadException || e is System.IO.FileNotFoundException
                   || e is System.IO.FileLoadException || e is BadImageFormatException || e is MemberAccessException;
        }

        private static string Describe(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e.GetType().Name + ": " + e.Message;
        }

        /// <summary>A wall clock in seconds (for the game layer's background run).</summary>
        public static Func<double> WallClock()
        {
            Stopwatch watch = Stopwatch.StartNew();
            return () => watch.Elapsed.TotalSeconds;
        }

        /// <summary>A one-line summary for the log.</summary>
        public static string Summary(Result r)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            int binding = 0;
            foreach (Failure f in r.Failures)
            {
                if (f.Binding) binding++;
            }
            return r.TypeRefs.ToString(ci) + " type and " + r.MemberRefs.ToString(ci) + " member references resolved in " + r.Seconds.ToString("0.00", ci) + " s"
                   + (r.MethodsScanned > 0 ? " (" + r.FailedWithoutContext.ToString(ci) + " checked again in " + r.MethodsScanned.ToString(ci) + " method bodies)" : "")
                   + (r.TimedOut ? " (time budget reached)" : "") + (r.Cancelled ? " (cancelled)" : "")
                   + ", " + binding.ToString(ci) + " missing, " + (r.Failures.Count - binding).ToString(ci) + " other failure(s)"
                   + (r.Unused > 0 ? ", " + r.Unused.ToString(ci) + " used by metadata only not checked in context" : "");
        }
    }
}
