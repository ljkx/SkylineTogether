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
    /// lacks them) has every missing reference found, attributed to the type whose method uses it and mapped to that
    /// type's feature; present references pass without running any static constructor; the whole Core assembly resolves
    /// completely, every row of its reference tables visited; the time budget and cancellation hold.
    /// </summary>
    public sealed class ReferenceCheckTests
    {
        private static readonly PrefixMap<string> FeatureOf = new PrefixMap<string>(new[]
        {
            ("Fixture.Movers.", "movers"),
            ("Fixture.Terrain.", "terrain"),
            ("Fixture.Water.", "water"),
        }, "replication");

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
                Assert.Equal("movers", FeatureOf.Of(drive.User));

                ReferenceCheck.Failure brush = r.Failures.Find(f => f.User == "Fixture.Terrain.Brush");
                Assert.NotNull(brush, all);
                Assert.Contains("TypeLoadException", brush.Error);
                Assert.True(brush.Binding);
                Assert.Equal("terrain", FeatureOf.Of(brush.User));

                ReferenceCheck.Failure reader = r.Failures.Find(f => f.User == "Fixture.Water.Reader");
                Assert.NotNull(reader, all);
                Assert.Contains("MissingFieldException", reader.Error);
                Assert.Equal("water", FeatureOf.Of(reader.User));

                Assert.Null(r.Failures.Find(f => f.User == "Fixture.Plain.Fine"), "a method that only uses present members is fine: " + all);
                // the nested type no code uses: reported, attributed to nothing
                ReferenceCheck.Failure unused = r.Failures.Find(f => f.User == null);
                Assert.NotNull(unused, all);
                Assert.Contains("TypeRef", unused.Reference);
                Assert.Equal(4, r.Failures.Count, all);
                Assert.Equal("replication", FeatureOf.Of(unused.User), "no user: the fallback");

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
        public void ThePrefixMapTakesTheFirstMatch()
        {
            var map = new PrefixMap<int>(new[] { ("A.B.Special", 1), ("A.B.", 2) }, 0);
            Assert.Equal(1, map.Of("A.B.SpecialThing+<>c"));
            Assert.Equal(2, map.Of("A.B.Other"));
            Assert.Equal(0, map.Of("C.D"));
            Assert.Equal(0, map.Of(null));
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
