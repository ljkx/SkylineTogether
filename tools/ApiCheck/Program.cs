using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace SkylineTogether.ApiCheck
{
    /// <summary>
    /// Checks offline that the mod's DLLs still bind to the game (docs/dev/building.md#api-check). For every checked DLL:
    /// <list type="bullet">
    /// <item>every type reference resolves to a type definition in the referenced assembly (following type forwarders);</item>
    /// <item>every member reference (method, constructor, field, property accessor) resolves to a member of that type or a
    /// base type with the same name, generic arity, static-ness and signature;</item>
    /// <item>every string literal of the form <c>Game.Namespace.Type</c> (type names the game layer looks up at run time)
    /// names a type or namespace of the game's assemblies.</item>
    /// </list>
    /// A game update that removes or changes a member the mod binds to at compile time makes Mono throw
    /// <c>MissingMethodException</c> or <c>TypeLoadException</c> when it first compiles the calling method, in the middle
    /// of a session; this finds it before anyone plays.
    /// <para>
    /// Usage: <c>ApiCheck [--managed DIR] [--dll PATH]... [--ref DIR]...</c>. <c>--managed</c> defaults to
    /// <c>$GameManagedPath</c>, else <c>$GameDir\Cities2_Data\Managed</c>, else the Steam default of Directory.Build.props;
    /// <c>--dll</c> defaults to the built SkylineTogether.dll and SkylineTogether.Core.dll; every checked DLL is also a
    /// reference (the game layer references Core); <c>--ref</c> adds folders of further references. Exit code 0: all
    /// resolved; 1: something did not (listed); 2: usage error or unreadable input. <c>--self-test</c> checks the checker
    /// against the running .NET runtime (CI, where the game is missing).
    /// </para>
    /// <para>
    /// <c>ApiCheck --list-types DLL --out FILE</c> writes the type list of a DLL (<see cref="ListTypes"/>): what
    /// <c>tools/build.sh</c> keeps <c>src/SkylineTogether/Data/mod-types.generated.txt</c> current with, so the Core tests can
    /// check without the game that every type of the game layer maps to a feature (ARCHITECTURE.md#probes-and-self-test).
    /// </para>
    /// </summary>
    public static class Program
    {
        private const string DefaultGameDir = @"C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II";

        public static int Main(string[] args)
        {
            string managed = null;
            var dlls = new List<string>();
            var refs = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if ((a == "--managed" || a == "--dll" || a == "--ref") && i + 1 < args.Length)
                {
                    string v = args[++i];
                    if (a == "--managed") managed = v;
                    else if (a == "--dll") dlls.Add(v);
                    else refs.Add(v);
                }
                else if (a == "--self-test")
                {
                    return SelfTest();
                }
                else if (a == "--list-types" && i + 1 < args.Length)
                {
                    string dll = args[++i];
                    string output = i + 2 < args.Length && args[i + 1] == "--out" ? args[i + 2] : null;
                    return ListTypes(dll, output);
                }
                else if (a == "-h" || a == "--help")
                {
                    Console.WriteLine("ApiCheck [--managed DIR] [--dll PATH]... [--ref DIR]... | --self-test | --list-types DLL [--out FILE]");
                    return 0;
                }
                else
                {
                    Console.Error.WriteLine("ApiCheck: unknown or incomplete option " + a);
                    return 2;
                }
            }
            managed ??= DefaultManagedPath();
            if (dlls.Count == 0)
            {
                string root = FindRepoRoot();
                string bin = Path.Combine(root ?? ".", "src", "SkylineTogether", "bin", "Release", "net472");
                dlls.Add(Path.Combine(bin, "SkylineTogether.dll"));
                dlls.Add(Path.Combine(bin, "SkylineTogether.Core.dll"));
            }
            if (!Directory.Exists(managed))
            {
                Console.Error.WriteLine("ApiCheck: no game assemblies in " + managed + " (pass --managed or set GameManagedPath)");
                return 2;
            }
            foreach (string dll in dlls)
            {
                if (!File.Exists(dll))
                {
                    Console.Error.WriteLine("ApiCheck: " + dll + " does not exist (build first)");
                    return 2;
                }
            }

            int problems = Check(dlls, refs.Append(managed), _ => true, Console.Out);
            if (problems > 0)
            {
                Console.WriteLine("ApiCheck: " + problems + " reference(s) do not resolve against " + managed
                                  + ". The game changed: the mod would fail where it uses them (adapt the code, or switch the feature off by a probe).");
                return 1;
            }
            return 0;
        }

        /// <summary>Checks <paramref name="dlls"/> against the DLLs of <paramref name="folders"/> that <paramref name="include"/> accepts; the number of problems.</summary>
        internal static int Check(IEnumerable<string> dlls, IEnumerable<string> folders, Func<string, bool> include, TextWriter output)
        {
            using var index = new AssemblyIndex();
            foreach (string dll in dlls) index.Add(dll, preferred: true);
            foreach (string dir in folders)
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*.dll").OrderBy(f => f, StringComparer.Ordinal))
                {
                    if (include(Path.GetFileName(file))) index.Add(file, preferred: false);
                }
            }
            int problems = 0;
            foreach (string dll in dlls)
            {
                Module module = index.ModuleOf(dll);
                var checker = new Checker(index, module);
                List<string> found = checker.Run();
                output.WriteLine("ApiCheck " + Path.GetFileName(dll) + ": " + checker.TypeRefs + " type references, " + checker.MemberRefs
                                 + " member references, " + checker.NameLiterals + " game type names checked against " + index.GameAssemblies + " assemblies: "
                                 + (found.Count == 0 ? "all resolve" : found.Count + " problem(s)"));
                foreach (string p in found) output.WriteLine("  MISSING " + p);
                problems += found.Count;
            }
            return problems;
        }

        /// <summary>
        /// Writes the types of <paramref name="dll"/> to <paramref name="output"/> (stdout when null), one full name per line
        /// in reflection form (<c>Namespace.Type</c>, nested <c>Outer+Inner</c>), sorted ordinally, after a comment line.
        /// Compiler-generated nested types (closures, state machines: names starting with <c>&lt;</c>) and everything inside
        /// them are left out: they belong to their outer type (<c>ModFeatureMap</c> maps them by it). The file is rewritten only
        /// when its content changes. Exit code 0, or 2 when the DLL cannot be read.
        /// </summary>
        private static int ListTypes(string dll, string output)
        {
            var names = new List<string>();
            try
            {
                using var pe = new PEReader(File.ReadAllBytes(dll).ToImmutableArray());
                MetadataReader md = pe.GetMetadataReader();
                foreach (TypeDefinitionHandle h in md.TypeDefinitions)
                {
                    string name = ReflectionName(md, h);
                    if (name != null) names.Add(name);
                }
            }
            catch (Exception e) when (e is IOException || e is BadImageFormatException || e is InvalidOperationException || e is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("ApiCheck: cannot read " + dll + ": " + e.Message);
                return 2;
            }
            names.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            sb.Append("# The types of ").Append(Path.GetFileName(dll))
              .Append(" (generated by tools/build.sh with ApiCheck --list-types; ModFeatureMapTests checks that each maps to a feature)\n");
            foreach (string n in names) sb.Append(n).Append('\n');
            string text = sb.ToString();
            if (output == null)
            {
                Console.Write(text);
                return 0;
            }
            string old = File.Exists(output) ? File.ReadAllText(output).Replace("\r\n", "\n") : null;
            if (old != text) File.WriteAllText(output, text, new UTF8Encoding(false));
            Console.WriteLine("ApiCheck: " + names.Count + " types of " + Path.GetFileName(dll) + (old == text ? " (unchanged)" : old == null ? " written" : " (the list changed; commit it)"));
            return 0;
        }

        // the reflection name of a type definition, or null for a compiler-generated nested type (or one inside it)
        private static string ReflectionName(MetadataReader md, TypeDefinitionHandle h)
        {
            TypeDefinition t = md.GetTypeDefinition(h);
            string name = md.GetString(t.Name);
            TypeDefinitionHandle outer = t.GetDeclaringType();
            if (outer.IsNil)
            {
                string ns = md.GetString(t.Namespace);
                return ns.Length > 0 ? ns + "." + name : name;
            }
            if (name.StartsWith("<", StringComparison.Ordinal)) return null;
            string outerName = ReflectionName(md, outer);
            return outerName == null ? null : outerName + "+" + name;
        }

        /// <summary>
        /// CI's check of the checker (no game there): this tool's own DLL resolves completely against the running .NET
        /// runtime, does not once System.Reflection.Metadata is taken away, and a copy of it with one referenced member
        /// renamed, or with one member reference's parameter type changed, has exactly that one problem (the member-level
        /// checks: name, then signature).
        /// </summary>
        private static int SelfTest()
        {
            string self = typeof(Program).Assembly.Location;
            string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);
            using var quiet = new StringWriter(CultureInfo.InvariantCulture);
            int complete = Check(new[] { self }, new[] { runtime }, _ => true, quiet);
            using var withoutSrm = new StringWriter(CultureInfo.InvariantCulture);
            int missing = Check(new[] { self }, new[] { runtime }, f => !string.Equals(f, "System.Reflection.Metadata.dll", StringComparison.OrdinalIgnoreCase), withoutSrm);
            bool assemblyLevel = complete == 0 && missing > 0 && withoutSrm.ToString().Contains("assembly System.Reflection.Metadata", StringComparison.Ordinal);
            string renamed = MutatedSelfCheck(self, runtime, SelfMutation.RenameMember, out string renamedReport);
            string retyped = MutatedSelfCheck(self, runtime, SelfMutation.ChangeParameter, out string retypedReport);
            bool ok = assemblyLevel && renamed != null && retyped != null;
            Console.WriteLine("ApiCheck self-test: " + (ok ? "ok" : "FAILED") + " (" + complete + " problem(s) against the runtime, " + missing
                              + " without System.Reflection.Metadata; a renamed member: " + (renamed ?? "not found") + "; a changed parameter: " + (retyped ?? "not found") + ")");
            if (!ok) Console.WriteLine(quiet.ToString() + withoutSrm + renamedReport + retypedReport);
            return ok ? 0 : 1;
        }

        private enum SelfMutation { RenameMember, ChangeParameter }

        /// <summary>
        /// Patches one member reference of a copy of <paramref name="self"/> (its name in the #Strings heap, or the first
        /// parameter type in its signature blob) and checks the copy: the first candidate whose check reports exactly one
        /// problem of the expected kind is the result (that problem line), null when no candidate does (the member-level
        /// check resolves what it must not). Candidates whose patched bytes are shared with other metadata are tried in
        /// turn; a working checker finds one among the first few.
        /// </summary>
        private static string MutatedSelfCheck(string self, string runtime, SelfMutation mutation, out string report)
        {
            byte[] original = File.ReadAllBytes(self);
            var sb = new StringBuilder();
            string dir = Path.Combine(Path.GetTempPath(), "ApiCheck-self-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var candidates = new List<(int Offset, byte Value, string Member)>();
                using (var pe = new PEReader(original.ToImmutableArray()))
                {
                    MetadataReader md = pe.GetMetadataReader();
                    int metadataStart = pe.PEHeaders.MetadataStartOffset;
                    var nameUses = new Dictionary<int, int>();
                    foreach (MemberReferenceHandle h in md.MemberReferences)
                    {
                        int offset = MetadataTokens.GetHeapOffset(md.GetMemberReference(h).Name);
                        nameUses[offset] = nameUses.TryGetValue(offset, out int n) ? n + 1 : 1;
                    }
                    foreach (MemberReferenceHandle h in md.MemberReferences)
                    {
                        MemberReference m = md.GetMemberReference(h);
                        string name = md.GetString(m.Name);
                        if (m.GetKind() != MemberReferenceKind.Method || name.StartsWith(".", StringComparison.Ordinal)) continue;
                        if (mutation == SelfMutation.RenameMember)
                        {
                            int offset = MetadataTokens.GetHeapOffset(m.Name);
                            if (nameUses[offset] != 1 || name.Length < 4) continue;
                            // the last letter of the name in the #Strings heap: WriteLine -> WriteLinf
                            int at = metadataStart + md.GetHeapMetadataOffset(HeapIndex.String) + offset + name.Length - 1;
                            candidates.Add((at, (byte)(original[at] == (byte)'f' ? 'g' : 'f'), name));
                        }
                        else
                        {
                            // HASTHIS? | param count | void or a primitive return type | first parameter: a primitive -> u8
                            BlobReader blob = md.GetBlobReader(m.Signature);
                            int start = metadataStart + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(m.Signature) + CompressedLengthSize(blob.Length);
                            if (blob.Length < 4) continue;
                            byte header = original[start], count = original[start + 1], ret = original[start + 2], first = original[start + 3];
                            if ((header & 0x1F) != 0 || count == 0 || count > 0x7F) continue;
                            if (ret < 0x01 || ret > 0x0E || first < 0x02 || first > 0x0E || first == 0x0B) continue;
                            candidates.Add((start + 3, 0x0B, name));
                        }
                    }
                }
                foreach (var (offset, value, member) in candidates.Take(40))
                {
                    byte[] copy = (byte[])original.Clone();
                    copy[offset] = value;
                    string path = Path.Combine(dir, Path.GetFileName(self));
                    File.WriteAllBytes(path, copy);
                    string output = CheckQuietly(path, runtime, out int problems);
                    if (output == null) continue; // a patch that broke the metadata itself
                    string[] lines = output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("  MISSING ", StringComparison.Ordinal)).ToArray();
                    sb.AppendLine(mutation + " " + member + ": " + problems + " problem(s)");
                    bool expected = mutation == SelfMutation.RenameMember
                        ? lines.Length == 1 && lines[0].Contains("::" + member.Substring(0, member.Length - 1), StringComparison.Ordinal) && lines[0].EndsWith("(no member of that name)", StringComparison.Ordinal)
                        : lines.Length == 1 && lines[0].Contains("::" + member + " ", StringComparison.Ordinal) && lines[0].Contains("UInt64", StringComparison.Ordinal) && lines[0].Contains("(the game has: ", StringComparison.Ordinal);
                    if (problems == 1 && expected) return lines[0].Trim().Length > 160 ? lines[0].Trim().Substring(0, 160) + "..." : lines[0].Trim();
                }
                return null;
            }
            finally
            {
                report = sb.ToString();
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                    // a scanner still holds the copy: the temp folder is cleaned up eventually
                }
            }
        }

        // the report of checking one DLL against the runtime, or null when its metadata cannot be read
        private static string CheckQuietly(string dll, string runtime, out int problems)
        {
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            try
            {
                problems = Check(new[] { dll }, new[] { runtime }, _ => true, output);
                return output.ToString();
            }
            catch (Exception e) when (e is BadImageFormatException || e is InvalidOperationException || e is ArgumentException)
            {
                problems = 0;
                return null;
            }
        }

        private static int CompressedLengthSize(int length) => length < 0x80 ? 1 : length < 0x4000 ? 2 : 4;

        private static string DefaultManagedPath()
        {
            string managed = Environment.GetEnvironmentVariable("GameManagedPath");
            if (!string.IsNullOrEmpty(managed)) return managed;
            string game = Environment.GetEnvironmentVariable("GameDir");
            if (string.IsNullOrEmpty(game)) game = DefaultGameDir;
            return Path.Combine(game, "Cities2_Data", "Managed");
        }

        private static string FindRepoRoot()
        {
            for (string dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
            {
                if (File.Exists(Path.Combine(dir, "Directory.Build.props")) && Directory.Exists(Path.Combine(dir, "src"))) return dir;
            }
            return null;
        }
    }

    /// <summary>One assembly's metadata.</summary>
    internal sealed class Module
    {
        public string Name;
        public string Path;
        public PEReader Pe;
        public MetadataReader Md;
        public bool Preferred;
        public readonly Dictionary<string, TypeDefinitionHandle> Types = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);
        public readonly Dictionary<string, ExportedTypeHandle> Exported = new Dictionary<string, ExportedTypeHandle>(StringComparer.Ordinal);
        public readonly HashSet<string> Namespaces = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>The assemblies by name, their types by full name (nested types as <c>Outer/Inner</c>).</summary>
    internal sealed class AssemblyIndex : IDisposable
    {
        private readonly Dictionary<string, Module> _byName = new Dictionary<string, Module>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Module> _byPath = new Dictionary<string, Module>(StringComparer.OrdinalIgnoreCase);

        public int GameAssemblies { get; private set; }

        public void Add(string path, bool preferred)
        {
            string full = System.IO.Path.GetFullPath(path);
            if (_byPath.ContainsKey(full)) return;
            PEReader pe = null;
            try
            {
                pe = new PEReader(File.ReadAllBytes(full).ToImmutableArray());
                if (!pe.HasMetadata) return;
                MetadataReader md = pe.GetMetadataReader();
                if (!md.IsAssembly) return;
                var m = new Module { Name = md.GetString(md.GetAssemblyDefinition().Name), Path = full, Pe = pe, Md = md, Preferred = preferred };
                if (_byName.ContainsKey(m.Name) && !preferred) return; // our own build wins over a stale copy in the game folder
                Index(m);
                pe = null; // the module owns it now
                _byName[m.Name] = m;
                _byPath[full] = m;
                if (!preferred) GameAssemblies++;
            }
            catch (BadImageFormatException)
            {
                // a native DLL
            }
            finally
            {
                pe?.Dispose();
            }
        }

        private static void Index(Module m)
        {
            MetadataReader md = m.Md;
            foreach (TypeDefinitionHandle h in md.TypeDefinitions)
            {
                m.Types[FullName(md, h)] = h;
                TypeDefinition t = md.GetTypeDefinition(h);
                if (!t.GetDeclaringType().IsNil) continue;
                string ns = md.GetString(t.Namespace);
                for (int dot = ns.Length; dot > 0; dot = ns.LastIndexOf('.', dot - 1))
                {
                    if (!m.Namespaces.Add(ns.Substring(0, dot))) break;
                }
            }
            foreach (ExportedTypeHandle h in md.ExportedTypes)
            {
                ExportedType e = md.GetExportedType(h);
                if (e.Implementation.Kind != HandleKind.AssemblyReference) continue;
                string ns = md.GetString(e.Namespace);
                string name = md.GetString(e.Name);
                m.Exported[ns.Length > 0 ? ns + "." + name : name] = h;
            }
        }

        public Module ModuleOf(string path) => _byPath[System.IO.Path.GetFullPath(path)];

        public Module Find(string assemblyName) => _byName.TryGetValue(assemblyName, out Module m) ? m : null;

        public IEnumerable<Module> All => _byName.Values;

        public static string FullName(MetadataReader md, TypeDefinitionHandle h)
        {
            TypeDefinition t = md.GetTypeDefinition(h);
            string name = md.GetString(t.Name);
            TypeDefinitionHandle outer = t.GetDeclaringType();
            if (!outer.IsNil) return FullName(md, outer) + "/" + name;
            string ns = md.GetString(t.Namespace);
            return ns.Length > 0 ? ns + "." + name : name;
        }

        public static string FullName(MetadataReader md, TypeReferenceHandle h)
        {
            TypeReference t = md.GetTypeReference(h);
            string name = md.GetString(t.Name);
            if (t.ResolutionScope.Kind == HandleKind.TypeReference) return FullName(md, (TypeReferenceHandle)t.ResolutionScope) + "/" + name;
            string ns = md.GetString(t.Namespace);
            return ns.Length > 0 ? ns + "." + name : name;
        }

        public void Dispose()
        {
            foreach (Module m in _byPath.Values) m.Pe.Dispose();
        }
    }

    /// <summary>A type definition in a module.</summary>
    internal readonly struct TypeDef
    {
        public readonly Module Module;
        public readonly TypeDefinitionHandle Handle;

        public TypeDef(Module module, TypeDefinitionHandle handle)
        {
            Module = module;
            Handle = handle;
        }

        public bool IsNil => Module == null;

        public string Name => AssemblyIndex.FullName(Module.Md, Handle) + " (" + Module.Name + ")";
    }

    /// <summary>
    /// Signatures as canonical strings: type names without their assembly (forwarded types keep their names), generic
    /// parameters as <c>!n</c>/<c>!!n</c>, required modifiers kept and optional ones dropped (as the runtime binds).
    /// </summary>
    internal sealed class SignatureText : ISignatureTypeProvider<string, object>
    {
        public static readonly SignatureText Instance = new SignatureText();

        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetFunctionPointerType(MethodSignature<string> signature) => "method " + Method(signature);

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";

        public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index.ToString(CultureInfo.InvariantCulture);

        public string GetGenericTypeParameter(object genericContext, int index) => "!" + index.ToString(CultureInfo.InvariantCulture);

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => isRequired ? unmodifiedType + " modreq(" + modifier + ")" : unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => AssemblyIndex.FullName(reader, handle);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => AssemblyIndex.FullName(reader, handle);

        public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public static string Method(MethodSignature<string> s)
            => (s.Header.IsInstance ? "instance " : "static ") + s.ReturnType + " <" + s.GenericParameterCount.ToString(CultureInfo.InvariantCulture) + ">("
               + string.Join(",", s.ParameterTypes) + ")";
    }

    /// <summary>Checks one module's references.</summary>
    internal sealed class Checker
    {
        // Game.Namespace.Type (also nested with '+'), the shape of the names the game layer passes to Assembly.GetType
        private static readonly Regex GameTypeName = new Regex(@"^Game(\.[A-Z][A-Za-z0-9_]*)+([+`][A-Za-z0-9_]+)*$", RegexOptions.CultureInvariant);

        private readonly AssemblyIndex _index;
        private readonly Module _module;
        private readonly Dictionary<TypeReferenceHandle, (TypeDef Def, string Problem)> _resolved = new Dictionary<TypeReferenceHandle, (TypeDef, string)>();
        private readonly List<string> _problems = new List<string>();

        public Checker(AssemblyIndex index, Module module)
        {
            _index = index;
            _module = module;
        }

        public int TypeRefs { get; private set; }

        public int MemberRefs { get; private set; }

        public int NameLiterals { get; private set; }

        public List<string> Run()
        {
            MetadataReader md = _module.Md;
            foreach (TypeReferenceHandle h in md.TypeReferences)
            {
                TypeRefs++;
                (TypeDef _, string problem) = Resolve(_module, h);
                if (problem != null) _problems.Add(problem);
            }
            foreach (MemberReferenceHandle h in md.MemberReferences)
            {
                MemberRefs++;
                string problem = CheckMember(h);
                if (problem != null) _problems.Add(problem);
            }
            CheckGameTypeNames();
            return _problems.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
        }

        // ------------------------------------------------------------------ types

        private (TypeDef Def, string Problem) Resolve(Module module, TypeReferenceHandle h)
        {
            if (module == _module && _resolved.TryGetValue(h, out var cached)) return cached;
            var result = ResolveUncached(module, h);
            if (module == _module) _resolved[h] = result;
            return result;
        }

        private (TypeDef, string) ResolveUncached(Module module, TypeReferenceHandle h)
        {
            MetadataReader md = module.Md;
            TypeReference r = md.GetTypeReference(h);
            string name = AssemblyIndex.FullName(md, h);
            EntityHandle scope = r.ResolutionScope;
            switch (scope.Kind)
            {
                case HandleKind.TypeReference:
                {
                    (TypeDef outer, string problem) = Resolve(module, (TypeReferenceHandle)scope);
                    if (outer.IsNil) return (default, problem);
                    string nested = md.GetString(r.Name);
                    foreach (TypeDefinitionHandle n in outer.Module.Md.GetTypeDefinition(outer.Handle).GetNestedTypes())
                    {
                        if (outer.Module.Md.GetString(outer.Module.Md.GetTypeDefinition(n).Name) == nested) return (new TypeDef(outer.Module, n), null);
                    }
                    return (default, "type " + name + " (nested type not found in " + outer.Name + ")");
                }
                case HandleKind.AssemblyReference:
                {
                    string assembly = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
                    return FindInAssembly(assembly, name, 0);
                }
                default:
                    // this module (ModuleDefinition, ModuleReference or nil scope)
                    return module.Types.TryGetValue(name, out TypeDefinitionHandle own) ? (new TypeDef(module, own), null) : (default, "type " + name + " (not in " + module.Name + ")");
            }
        }

        private (TypeDef, string) FindInAssembly(string assembly, string name, int depth)
        {
            Module target = _index.Find(assembly);
            if (target == null) return (default, "assembly " + assembly + " (needed for " + name + ") is not in the game's Managed folder");
            if (target.Types.TryGetValue(name, out TypeDefinitionHandle def)) return (new TypeDef(target, def), null);
            if (depth < 8 && target.Exported.TryGetValue(name, out ExportedTypeHandle exported))
            {
                ExportedType e = target.Md.GetExportedType(exported);
                string next = target.Md.GetString(target.Md.GetAssemblyReference((AssemblyReferenceHandle)e.Implementation).Name);
                return FindInAssembly(next, name, depth + 1);
            }
            return (default, "type " + name + " (not in " + assembly + Elsewhere(_index.All.Where(a => !a.Preferred), name) + ")");
        }

        /// <summary>Where a type of the same simple name lives now (a moved type), as text for a problem line.</summary>
        private static string Elsewhere(IEnumerable<Module> modules, string fullName)
        {
            int cut = Math.Max(fullName.LastIndexOf('.'), fullName.LastIndexOf('/'));
            string simple = fullName.Substring(cut + 1);
            var hits = new List<string>();
            foreach (Module m in modules)
            {
                foreach (string candidate in m.Types.Keys)
                {
                    int c = Math.Max(candidate.LastIndexOf('.'), candidate.LastIndexOf('/'));
                    if (string.Equals(candidate.Substring(c + 1), simple, StringComparison.Ordinal)) hits.Add(candidate + " in " + m.Name);
                }
            }
            return hits.Count == 0 ? string.Empty : "; found as " + string.Join(", ", hits.Take(3));
        }

        /// <summary>The type definition an entity handle (definition, reference or generic instantiation) of a module names.</summary>
        private TypeDef DefinitionOf(Module module, EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    return new TypeDef(module, (TypeDefinitionHandle)handle);
                case HandleKind.TypeReference:
                    return Resolve(module, (TypeReferenceHandle)handle).Def;
                case HandleKind.TypeSpecification:
                {
                    // a generic instantiation: GENERICINST (CLASS | VALUETYPE) TypeDefOrRef ...; arrays and the like have none
                    BlobReader blob = module.Md.GetBlobReader(module.Md.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                    if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return default;
                    blob.ReadSignatureTypeCode();
                    return DefinitionOf(module, blob.ReadTypeHandle());
                }
                default:
                    return default;
            }
        }

        // ------------------------------------------------------------------ members

        private string CheckMember(MemberReferenceHandle h)
        {
            MetadataReader md = _module.Md;
            MemberReference m = md.GetMemberReference(h);
            EntityHandle parent = m.Parent;
            if (parent.Kind == HandleKind.MethodDefinition || parent.Kind == HandleKind.ModuleReference) return null; // vararg call sites, global members
            if (parent.Kind == HandleKind.TypeSpecification)
            {
                BlobReader blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
                if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null; // array accessors are made by the runtime
            }
            TypeDef owner = DefinitionOf(_module, parent);
            if (owner.IsNil) return null; // the type itself is reported as a type reference
            string name = md.GetString(m.Name);
            bool isField = m.GetKind() == MemberReferenceKind.Field;
            string wanted = isField ? m.DecodeFieldSignature(SignatureText.Instance, null) : SignatureText.Method(m.DecodeMethodSignature(SignatureText.Instance, null));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var near = new List<string>();
            for (TypeDef t = owner; !t.IsNil && seen.Add(t.Name);)
            {
                MetadataReader tmd = t.Module.Md;
                TypeDefinition def = tmd.GetTypeDefinition(t.Handle);
                if (isField)
                {
                    foreach (FieldDefinitionHandle fh in def.GetFields())
                    {
                        FieldDefinition f = tmd.GetFieldDefinition(fh);
                        if (tmd.GetString(f.Name) != name) continue;
                        string actual = f.DecodeSignature(SignatureText.Instance, null);
                        if (actual == wanted) return null;
                        near.Add(actual);
                    }
                }
                else
                {
                    foreach (MethodDefinitionHandle mh in def.GetMethods())
                    {
                        MethodDefinition d = tmd.GetMethodDefinition(mh);
                        if (tmd.GetString(d.Name) != name) continue;
                        string actual = SignatureText.Method(d.DecodeSignature(SignatureText.Instance, null));
                        if (actual == wanted) return null;
                        near.Add(actual);
                    }
                }
                EntityHandle baseType = def.BaseType;
                if (baseType.IsNil) break;
                t = DefinitionOf(t.Module, baseType);
            }
            return (isField ? "field " : "method ") + owner.Name + "::" + name + " " + wanted
                   + (near.Count > 0 ? " (the game has: " + string.Join("; ", near.Distinct()) + ")" : " (no member of that name)");
        }

        // ------------------------------------------------------------------ type names in string literals

        private void CheckGameTypeNames()
        {
            MetadataReader md = _module.Md;
            Module game = _index.Find("Game");
            if (game == null) return;
            var games = _index.All.Where(a => !a.Preferred).ToList();
            UserStringHandle h = MetadataTokens.UserStringHandle(1);
            int heap = md.GetHeapSize(HeapIndex.UserString);
            while (!h.IsNil && MetadataTokens.GetHeapOffset(h) < heap)
            {
                string s = md.GetUserString(h);
                h = md.GetNextHandle(h);
                if (!GameTypeName.IsMatch(s)) continue;
                NameLiterals++;
                string full = s.Replace('+', '/');
                bool found = games.Any(a => a.Types.ContainsKey(full) || a.Exported.ContainsKey(full) || a.Namespaces.Contains(s));
                if (!found) _problems.Add("type name \"" + s + "\" (a string the game layer looks up; no such type or namespace in the game" + Elsewhere(games, full) + ")");
            }
        }
    }
}
