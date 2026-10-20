using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Diagnostics
{
    /// <summary>The type the fixture assembly of <see cref="ReferenceCheckTests"/> was "compiled against".</summary>
    public static class ReferenceCheckFixtureTarget
    {
        static ReferenceCheckFixtureTarget()
        {
            ReferenceCheckFixtureFlags.TargetInitialised = true;
        }

        public static int Present() => 1;
    }

    /// <summary>Set by the static constructor of <see cref="ReferenceCheckFixtureTarget"/> (read without running it).</summary>
    public static class ReferenceCheckFixtureFlags
    {
        public static bool TargetInitialised;
    }

    /// <summary>
    /// <see cref="ReferenceCheck"/>: an assembly built against members its reference no longer has (emitted here with
    /// System.Reflection.Metadata, the "stub" being this test assembly's <see cref="ReferenceCheckFixtureTarget"/>, which
    /// lacks them) has every missing reference found and attributed to the type whose method uses it; present references
    /// pass without running any static constructor; the whole Core assembly resolves and sets up completely, every row
    /// of its reference tables visited; the time budget and cancellation hold. Types whose base type or interface is
    /// missing are reported by their own name, also when the loader exception names only the missing type (Mono), with
    /// the TypeDef table read from the file (<see cref="TypeDefTable"/>, checked against System.Reflection.Metadata).
    /// </summary>
    public sealed class ReferenceCheckTests
    {
        [Test]
        public void AMissingReferenceIsFoundAndAttributedToTheFeatureThatUsesIt()
        {
            var alc = new AssemblyLoadContext("reference-check-fixture", isCollectible: true);
            try
            {
                Assembly fixture = alc.LoadFromStream(new MemoryStream(BuildFixture()));
                ReferenceCheck.Result r = ReferenceCheck.Run(fixture.ManifestModule, ReferenceCheck.WallClock(), 30);
                string all = string.Join("\n", r.Failures.ConvertAll(f => f.ToString()));
                Assert.Equal(4, r.TypeRefs, "System.Object, the target, the missing type and the missing nested type");
                Assert.Equal(5, r.MemberRefs);
                Assert.True(r.MethodsScanned >= 4, "the uses were looked up in the method bodies");
                Assert.False(r.TimedOut);

                ReferenceCheck.Failure drive = r.Failures.Find(f => f.User == "Fixture.Movers.Truck");
                Assert.NotNull(drive, all);
                Assert.Equal("Drive()", drive.Method);
                Assert.Contains("MissingMethodException", drive.Error);
                Assert.Contains("Missing", drive.Error);
                Assert.True(drive.Binding);

                ReferenceCheck.Failure brush = r.Failures.Find(f => f.User == "Fixture.Terrain.Brush");
                Assert.NotNull(brush, all);
                Assert.Contains("TypeLoadException", brush.Error);
                Assert.True(brush.Binding);

                ReferenceCheck.Failure reader = r.Failures.Find(f => f.User == "Fixture.Water.Reader");
                Assert.NotNull(reader, all);
                Assert.Contains("MissingFieldException", reader.Error);

                Assert.Null(r.Failures.Find(f => f.User == "Fixture.Plain.Fine"), "a method that only uses present members is fine: " + all);
                // the nested type no code uses: reported, attributed to nothing
                ReferenceCheck.Failure unused = r.Failures.Find(f => f.User == null);
                Assert.NotNull(unused, all);
                Assert.Contains("TypeRef", unused.Reference);
                Assert.Equal(4, r.Failures.Count, all);
                Assert.Null(ModFeatureMap.Of(unused.User), "no user: no feature (a warning)");

                Assert.False(ReferenceCheckFixtureFlags.TargetInitialised, "resolving its members ran the target's static constructor");
                Assert.Contains("missing", ReferenceCheck.Summary(r));
            }
            finally
            {
                alc.Unload();
            }
        }

        [Test(TimeoutSeconds = 120)]
        public void TheWholeCoreAssemblyResolvesAndEveryRowIsVisited()
        {
            Assembly assembly = typeof(ReferenceCheck).Assembly;
            var loadFailures = new List<ReferenceCheck.Failure>();
            List<Type> types = ReferenceCheck.TypesOf(assembly, loadFailures);
            Assert.Equal(0, loadFailures.Count);
            ReferenceCheck.Result r = ReferenceCheck.Run(assembly.ManifestModule, ReferenceCheck.WallClock(), 90, null, types);
            Assert.Equal(0, r.Failures.Count, string.Join("\n", r.Failures.ConvertAll(f => f.ToString())));
            Assert.False(r.TimedOut, ReferenceCheck.Summary(r));
            Assert.Equal(types.Count, r.TypesSetUp, "every type was set up, without a false failure");
            // the enumeration ends exactly at each table's end
            using (var pe = new PEReader(File.OpenRead(assembly.Location)))
            {
                MetadataReader md = pe.GetMetadataReader();
                Assert.Equal(md.GetTableRowCount(TableIndex.TypeRef), r.TypeRefs);
                Assert.Equal(md.GetTableRowCount(TableIndex.MemberRef), r.MemberRefs);
                Assert.True(r.MemberRefs > 1000, "Core's references (" + r.MemberRefs + ")");
            }
        }

        [Test]
        public void TheTimeBudgetAndCancellationStopTheRun()
        {
            Module module = typeof(ReferenceCheck).Assembly.ManifestModule;
            double now = 0;
            ReferenceCheck.Result timed = ReferenceCheck.Run(module, () => now += 1, 10);
            Assert.True(timed.TimedOut);
            Assert.True(timed.TypeRefs < 64 * 12, "stopped early (" + timed.TypeRefs + " type references)");
            ReferenceCheck.Result cancelled = ReferenceCheck.Run(module, () => 0, 100, () => true);
            Assert.True(cancelled.Cancelled);
            Assert.Equal(0, cancelled.MemberRefs);
        }

        [Test]
        public void TheTokensOfAMethodBodyAreRead()
        {
            // ldc.i4.0; switch (2 targets); call 0x0A000007; ldsfld 0x0A000009; ldstr 0x70000001; ldc.i8 0; pop; newobj 0x0A00000B; ret
            var il = new List<byte> { 0x16, 0x45, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
            il.AddRange(new byte[] { 0x28, 7, 0, 0, 0x0A });
            il.AddRange(new byte[] { 0x7E, 9, 0, 0, 0x0A });
            il.AddRange(new byte[] { 0x72, 1, 0, 0, 0x70 });
            il.AddRange(new byte[] { 0x21, 0, 0, 0, 0, 0, 0, 0, 0, 0x26 });
            il.AddRange(new byte[] { 0x73, 0x0B, 0, 0, 0x0A, 0x2A });
            var tokens = new List<int>();
            ReferenceCheck.IlTokens(il.ToArray(), tokens);
            Assert.SequenceEqual(new[] { 0x0A000007, 0x0A000009, 0x0A00000B }, tokens);
            tokens.Clear();
            ReferenceCheck.IlTokens(new byte[] { 0x28, 1, 0 }, tokens); // truncated: nothing, no exception
            Assert.Equal(0, tokens.Count);
        }

        [Test]
        public void TheTypeDefTableMatchesSystemReflectionMetadata()
        {
            // this test assembly: generics, nested and compiler-generated types, interfaces of every kind
            foreach (string file in new[] { typeof(ReferenceCheckTests).Assembly.Location, typeof(object).Assembly.Location })
            {
                byte[] bytes = File.ReadAllBytes(file);
                TypeDefTable table = TypeDefTable.Read(bytes);
                using var pe = new PEReader(new MemoryStream(bytes));
                MetadataReader md = pe.GetMetadataReader();
                Assert.Equal(md.TypeDefinitions.Count, table.Types.Count, file);
                int i = 0, withBase = 0, withInterfaces = 0, nested = 0;
                foreach (TypeDefinitionHandle h in md.TypeDefinitions)
                {
                    TypeDefinition t = md.GetTypeDefinition(h);
                    TypeDefTable.Row row = table.Types[i++];
                    Assert.Equal(MetadataTokens.GetToken(h), row.Token);
                    Assert.Equal(DefName(md, h), row.FullName);
                    Assert.Equal(TypeName(md, t.BaseType), row.BaseName, row.FullName);
                    var interfaces = new List<string>();
                    foreach (InterfaceImplementationHandle ih in t.GetInterfaceImplementations())
                    {
                        string name = TypeName(md, md.GetInterfaceImplementation(ih).Interface);
                        if (name != null) interfaces.Add(name);
                    }
                    Assert.SequenceEqual(interfaces, row.InterfaceNames, row.FullName);
                    Assert.Equal(t.GetDeclaringType().IsNil ? null : DefName(md, t.GetDeclaringType()), row.Enclosing?.FullName);
                    if (row.BaseName != null) withBase++;
                    if (row.InterfaceNames.Count > 0) withInterfaces++;
                    if (row.Enclosing != null) nested++;
                }
                Assert.True(withBase > 100 && withInterfaces > 10 && nested > 10, file + ": " + withBase + " " + withInterfaces + " " + nested);
            }
            Assert.Throws<BadImageFormatException>(() => TypeDefTable.Read(new byte[200]));
        }

        [Test]
        public void ATypeThatDoesNotLoadIsReportedByItsOwnName()
        {
            byte[] image = BuildBrokenTypesFixture();
            var alc = new AssemblyLoadContext("reference-check-broken-types", isCollectible: true);
            try
            {
                Assembly fixture = alc.LoadFromStream(new MemoryStream(image));
                var failures = new List<ReferenceCheck.Failure>();
                List<Type> types = ReferenceCheck.TypesOf(fixture, failures, () => image);
                string all = string.Join("\n", failures.ConvertAll(f => f.ToString()));
                Assert.Contains("Fixture.Plain.Fine", string.Join(",", types.ConvertAll(t => t.FullName)));
                Assert.Equal(2, failures.Count, all);
                ReferenceCheck.Failure derived = failures.Find(f => f.User == "Fixture.Movers.Derived");
                Assert.NotNull(derived, "the type whose base type is missing, its nested type covered by it: " + all);
                Assert.True(derived.Binding);
                Assert.Contains("TypeLoadException", derived.Error);
                ReferenceCheck.Failure impl = failures.Find(f => f.User == "Fixture.Water.Impl");
                Assert.NotNull(impl, "the type whose interface is missing: " + all);
                Assert.True(impl.Binding);

                // Mono: the loader exception names the missing game type, not the mod's (simulated): still the mod's type,
                // and the missing type named as the cause
                var mono = new List<ReferenceCheck.Failure>();
                Exception[] loaders = { new TypeLoadException("Could not load type of field"), new TypeLoadException("Could not load interface") };
                string ns = typeof(ReferenceCheckFixtureTarget).Namespace;
                Assert.True(ReferenceCheck.AddUnloaded(fixture, types.ToArray(), loaders, () => image, mono,
                    e => e == loaders[0] ? ns + ".NoSuchBase" : ns + ".INoSuch"));
                Assert.SequenceEqual(new[] { "Fixture.Movers.Derived", "Fixture.Water.Impl" }, mono.ConvertAll(f => f.User));
                Assert.Contains(ns + ".NoSuchBase", mono[0].Reference);
                Assert.Contains("Could not load type of field", mono[0].Error);
                Assert.Contains(ns + ".INoSuch", mono[1].Reference);
                Assert.Contains("Could not load interface", mono[1].Error);

                // no readable image: the loader exceptions as they are (a game type's name maps to no feature: a warning)
                var plain = new List<ReferenceCheck.Failure>();
                ReferenceCheck.TypesOf(fixture, plain, () => null);
                Assert.Equal(2, plain.Count);
            }
            finally
            {
                alc.Unload();
            }
        }

        private static string DefName(MetadataReader md, TypeDefinitionHandle h)
        {
            TypeDefinition t = md.GetTypeDefinition(h);
            string name = md.GetString(t.Name);
            if (!t.GetDeclaringType().IsNil) return DefName(md, t.GetDeclaringType()) + "+" + name;
            string ns = md.GetString(t.Namespace);
            return ns.Length > 0 ? ns + "." + name : name;
        }

        private static string TypeName(MetadataReader md, EntityHandle h)
        {
            if (h.IsNil) return null;
            if (h.Kind == HandleKind.TypeDefinition) return DefName(md, (TypeDefinitionHandle)h);
            if (h.Kind != HandleKind.TypeReference) return null;
            TypeReference r = md.GetTypeReference((TypeReferenceHandle)h);
            string name = md.GetString(r.Name);
            if (r.ResolutionScope.Kind == HandleKind.TypeReference) return TypeName(md, (TypeReferenceHandle)r.ResolutionScope) + "+" + name;
            string ns = md.GetString(r.Namespace);
            return ns.Length > 0 ? ns + "." + name : name;
        }

        /// <summary>
        /// An assembly "compiled against" a version of the test assembly that had <c>NoSuchBase</c> and <c>INoSuch</c>:
        /// <c>Fixture.Movers.Derived</c> derives from the missing class (with a nested type <c>Inner</c>),
        /// <c>Fixture.Water.Impl</c> implements the missing interface, <c>Fixture.Plain.Fine</c> is a plain class.
        /// </summary>
        private static byte[] BuildBrokenTypesFixture()
        {
            var md = new MetadataBuilder();
            md.AddModule(0, md.GetOrAddString("BrokenTypes.dll"), md.GetOrAddGuid(Guid.NewGuid()), default, default);
            md.AddAssembly(md.GetOrAddString("BrokenTypes"), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
            AssemblyName corlibName = typeof(object).Assembly.GetName();
            AssemblyReferenceHandle corlib = md.AddAssemblyReference(md.GetOrAddString(corlibName.Name), corlibName.Version, default,
                md.GetOrAddBlob(corlibName.GetPublicKeyToken() ?? Array.Empty<byte>()), default, default);
            AssemblyName testsName = typeof(ReferenceCheckFixtureTarget).Assembly.GetName();
            AssemblyReferenceHandle tests = md.AddAssemblyReference(md.GetOrAddString(testsName.Name), testsName.Version, default, default, default, default);
            StringHandle ns = md.GetOrAddString(typeof(ReferenceCheckFixtureTarget).Namespace);
            TypeReferenceHandle obj = md.AddTypeReference(corlib, md.GetOrAddString("System"), md.GetOrAddString("Object"));
            TypeReferenceHandle noSuchBase = md.AddTypeReference(tests, ns, md.GetOrAddString("NoSuchBase"));
            TypeReferenceHandle noSuchInterface = md.AddTypeReference(tests, ns, md.GetOrAddString("INoSuch"));
            FieldDefinitionHandle fields = MetadataTokens.FieldDefinitionHandle(1);
            MethodDefinitionHandle methods = MetadataTokens.MethodDefinitionHandle(1);
            md.AddTypeDefinition(default, default, md.GetOrAddString("<Module>"), default, fields, methods);
            const TypeAttributes cls = TypeAttributes.Public | TypeAttributes.Class;
            TypeDefinitionHandle derived = md.AddTypeDefinition(cls, md.GetOrAddString("Fixture.Movers"), md.GetOrAddString("Derived"), noSuchBase, fields, methods);
            TypeDefinitionHandle inner = md.AddTypeDefinition(TypeAttributes.NestedPublic | TypeAttributes.Class, default, md.GetOrAddString("Inner"), obj, fields, methods);
            TypeDefinitionHandle impl = md.AddTypeDefinition(cls, md.GetOrAddString("Fixture.Water"), md.GetOrAddString("Impl"), obj, fields, methods);
            md.AddTypeDefinition(cls, md.GetOrAddString("Fixture.Plain"), md.GetOrAddString("Fine"), obj, fields, methods);
            md.AddNestedType(inner, derived);
            md.AddInterfaceImplementation(impl, noSuchInterface);
            var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll), new MetadataRootBuilder(md), new BlobBuilder());
            var blob = new BlobBuilder();
            pe.Serialize(blob);
            return blob.ToArray();
        }

        // ------------------------------------------------------------------------------------------------- fixture

        /// <summary>
        /// An assembly "compiled against" a version of <see cref="ReferenceCheckFixtureTarget"/> that had <c>Missing()</c>, a
        /// field <c>Gone</c>, and next to it the types <c>NoSuchType</c> and <c>NoSuchType/Inner</c>:
        /// <c>Fixture.Movers.Truck.Drive()</c> calls <c>Missing()</c>, <c>Fixture.Terrain.Brush.Paint()</c> casts to
        /// <c>NoSuchType</c>, <c>Fixture.Water.Reader.Read()</c> reads <c>Gone</c>, <c>Fixture.Plain.Fine.Ok()</c> calls
        /// <c>Present()</c>; only a custom-attribute-like metadata row would use <c>NoSuchType/Inner</c>, which no code uses.
        /// </summary>
        private static byte[] BuildFixture()
        {
            var md = new MetadataBuilder();
            md.AddModule(0, md.GetOrAddString("RefFixture.dll"), md.GetOrAddGuid(Guid.NewGuid()), default, default);
            md.AddAssembly(md.GetOrAddString("RefFixture"), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
            AssemblyName corlibName = typeof(object).Assembly.GetName();
            AssemblyReferenceHandle corlib = md.AddAssemblyReference(md.GetOrAddString(corlibName.Name), corlibName.Version, default,
                md.GetOrAddBlob(corlibName.GetPublicKeyToken() ?? Array.Empty<byte>()), default, default);
            AssemblyName testsName = typeof(ReferenceCheckFixtureTarget).Assembly.GetName();
            AssemblyReferenceHandle tests = md.AddAssemblyReference(md.GetOrAddString(testsName.Name), testsName.Version, default, default, default, default);

            StringHandle ns = md.GetOrAddString(typeof(ReferenceCheckFixtureTarget).Namespace);
            TypeReferenceHandle obj = md.AddTypeReference(corlib, md.GetOrAddString("System"), md.GetOrAddString("Object"));
            TypeReferenceHandle target = md.AddTypeReference(tests, ns, md.GetOrAddString(nameof(ReferenceCheckFixtureTarget)));
            TypeReferenceHandle missingType = md.AddTypeReference(tests, ns, md.GetOrAddString("NoSuchType"));
            md.AddTypeReference(missingType, default, md.GetOrAddString("Inner"));

            var intSig = new BlobBuilder();
            new BlobEncoder(intSig).MethodSignature().Parameters(0, r => r.Type().Int32(), p => { });
            BlobHandle intSigHandle = md.GetOrAddBlob(intSig);
            var voidSig = new BlobBuilder();
            new BlobEncoder(voidSig).MethodSignature().Parameters(0, r => r.Void(), p => { });
            BlobHandle voidSigHandle = md.GetOrAddBlob(voidSig);
            var ctorSig = new BlobBuilder();
            new BlobEncoder(ctorSig).MethodSignature(isInstanceMethod: true).Parameters(0, r => r.Void(), p => { });
            var instanceIntSig = new BlobBuilder();
            new BlobEncoder(instanceIntSig).MethodSignature(isInstanceMethod: true).Parameters(0, r => r.Type().Int32(), p => { });
            var fieldSig = new BlobBuilder();
            new BlobEncoder(fieldSig).FieldSignature().Int32();

            MemberReferenceHandle present = md.AddMemberReference(target, md.GetOrAddString("Present"), intSigHandle);
            MemberReferenceHandle missing = md.AddMemberReference(target, md.GetOrAddString("Missing"), intSigHandle);
            MemberReferenceHandle gone = md.AddMemberReference(target, md.GetOrAddString("Gone"), md.GetOrAddBlob(fieldSig));
            md.AddMemberReference(obj, md.GetOrAddString(".ctor"), md.GetOrAddBlob(ctorSig));
            md.AddMemberReference(obj, md.GetOrAddString("GetHashCode"), md.GetOrAddBlob(instanceIntSig));

            var il = new BlobBuilder();
            var bodies = new MethodBodyStreamEncoder(il);
            int Body(Action<InstructionEncoder> emit)
            {
                var code = new InstructionEncoder(new BlobBuilder());
                emit(code);
                return bodies.AddMethodBody(code);
            }

            md.AddTypeDefinition(default, default, md.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            int method = 1;
            void StaticType(string space, string name, string methodName, Action<InstructionEncoder> emit)
            {
                MethodDefinitionHandle m = md.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, MethodImplAttributes.IL,
                    md.GetOrAddString(methodName), voidSigHandle, Body(emit), MetadataTokens.ParameterHandle(1));
                md.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class, md.GetOrAddString(space), md.GetOrAddString(name),
                    obj, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(method));
                method++;
                _ = m;
            }

            StaticType("Fixture.Movers", "Truck", "Drive", c =>
            {
                c.Call(missing);
                c.OpCode(ILOpCode.Pop);
                c.OpCode(ILOpCode.Ret);
            });
            StaticType("Fixture.Terrain", "Brush", "Paint", c =>
            {
                c.OpCode(ILOpCode.Ldnull);
                c.OpCode(ILOpCode.Castclass);
                c.Token(missingType);
                c.OpCode(ILOpCode.Pop);
                c.OpCode(ILOpCode.Ret);
            });
            StaticType("Fixture.Water", "Reader", "Read", c =>
            {
                c.OpCode(ILOpCode.Ldsfld);
                c.Token(gone);
                c.OpCode(ILOpCode.Pop);
                c.OpCode(ILOpCode.Ret);
            });
            StaticType("Fixture.Plain", "Fine", "Ok", c =>
            {
                c.Call(present);
                c.OpCode(ILOpCode.Pop);
                c.OpCode(ILOpCode.Ret);
            });

            var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll), new MetadataRootBuilder(md), il);
            var blob = new BlobBuilder();
            pe.Serialize(blob);
            return blob.ToArray();
        }
    }
}
