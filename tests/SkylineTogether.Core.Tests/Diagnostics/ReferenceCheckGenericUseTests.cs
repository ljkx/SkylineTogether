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
    /// <summary>
    /// The startup reference check attributes uses of a missing type through a generic method instantiation (MethodSpec), a
    /// type token (TypeSpec, ldtoken) and a field type, and of a missing method through ldftn (a two-byte opcode), to the
    /// type whose code uses them, so the right feature is switched off.
    /// </summary>
    public sealed class ReferenceCheckGenericUseTests
    {
        [Test]
        public void MissingReferencesUsedGenericallyAreAttributedToTheirUser()
        {
            var alc = new AssemblyLoadContext("probe", isCollectible: true);
            try
            {
                Assembly fixture = alc.LoadFromStream(new MemoryStream(Build()));
                ReferenceCheck.Result r = ReferenceCheck.Run(fixture.ManifestModule, ReferenceCheck.WallClock(), 30);
                string all = string.Join("\n", r.Failures.ConvertAll(f => f.ToString()));
                Assert.NotNull(r.Failures.Find(f => f.User == "Fixture.Agents.Gen"), "MethodSpec use: " + all);
                Assert.NotNull(r.Failures.Find(f => f.User == "Fixture.Icons.Tok"), "TypeSpec use: " + all);
                Assert.NotNull(r.Failures.Find(f => f.User == "Fixture.Water.Holder"), "field type use: " + all);
                Assert.NotNull(r.Failures.Find(f => f.User == "Fixture.Events.Ftn"), "ldftn use: " + all);
            }
            finally
            {
                alc.Unload();
            }
        }

        private static byte[] Build()
        {
            var md = new MetadataBuilder();
            md.AddModule(0, md.GetOrAddString("Probe.dll"), md.GetOrAddGuid(Guid.NewGuid()), default, default);
            md.AddAssembly(md.GetOrAddString("Probe"), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
            AssemblyName corlibName = typeof(object).Assembly.GetName();
            AssemblyReferenceHandle corlib = md.AddAssemblyReference(md.GetOrAddString(corlibName.Name), corlibName.Version, default,
                md.GetOrAddBlob(corlibName.GetPublicKeyToken() ?? Array.Empty<byte>()), default, default);
            AssemblyName testsName = typeof(ReferenceCheckFixtureTarget).Assembly.GetName();
            AssemblyReferenceHandle tests = md.AddAssemblyReference(md.GetOrAddString(testsName.Name), testsName.Version, default, default, default, default);
            AssemblyName runtimeName = typeof(Activator).Assembly.GetName();

            StringHandle ns = md.GetOrAddString(typeof(ReferenceCheckFixtureTarget).Namespace);
            TypeReferenceHandle obj = md.AddTypeReference(corlib, md.GetOrAddString("System"), md.GetOrAddString("Object"));
            TypeReferenceHandle activator = md.AddTypeReference(corlib, md.GetOrAddString("System"), md.GetOrAddString("Activator"));
            TypeReferenceHandle list = md.AddTypeReference(corlib, md.GetOrAddString("System.Collections.Generic"), md.GetOrAddString("List`1"));
            TypeReferenceHandle target = md.AddTypeReference(tests, ns, md.GetOrAddString(nameof(ReferenceCheckFixtureTarget)));
            TypeReferenceHandle missingType = md.AddTypeReference(tests, ns, md.GetOrAddString("NoSuchType"));

            var intSig = new BlobBuilder();
            new BlobEncoder(intSig).MethodSignature().Parameters(0, r => r.Type().Int32(), p => { });
            var voidSig = new BlobBuilder();
            new BlobEncoder(voidSig).MethodSignature().Parameters(0, r => r.Void(), p => { });
            BlobHandle voidSigHandle = md.GetOrAddBlob(voidSig);
            // !!0 CreateInstance<T>()
            var genSig = new BlobBuilder();
            new BlobEncoder(genSig).MethodSignature(genericParameterCount: 1).Parameters(0, r => r.Type().GenericMethodTypeParameter(0), p => { });
            MemberReferenceHandle createInstance = md.AddMemberReference(activator, md.GetOrAddString("CreateInstance"), md.GetOrAddBlob(genSig));
            var instBlob = new BlobBuilder();
            new BlobEncoder(instBlob).MethodSpecificationSignature(1).AddArgument().Type(missingType, false);
            MethodSpecificationHandle createMissing = md.AddMethodSpecification(createInstance, md.GetOrAddBlob(instBlob));
            // List<NoSuchType>
            var listBlob = new BlobBuilder();
            var gi = new BlobEncoder(listBlob).TypeSpecificationSignature().GenericInstantiation(list, 1, false);
            gi.AddArgument().Type(missingType, false);
            TypeSpecificationHandle listOfMissing = md.AddTypeSpecification(md.GetOrAddBlob(listBlob));
            MemberReferenceHandle missing = md.AddMemberReference(target, md.GetOrAddString("Missing"), md.GetOrAddBlob(intSig));

            var il = new BlobBuilder();
            var bodies = new MethodBodyStreamEncoder(il);
            int Body(Action<InstructionEncoder> emit)
            {
                var code = new InstructionEncoder(new BlobBuilder());
                emit(code);
                return bodies.AddMethodBody(code);
            }

            md.AddTypeDefinition(default, default, md.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            int method = 1, field = 1;
            void StaticType(string space, string name, string methodName, Action<InstructionEncoder> emit, bool withField = false)
            {
                if (withField)
                {
                    var fs = new BlobBuilder();
                    new BlobEncoder(fs).FieldSignature().Type(missingType, false);
                    md.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static, md.GetOrAddString("F"), md.GetOrAddBlob(fs));
                }
                md.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, MethodImplAttributes.IL,
                    md.GetOrAddString(methodName), voidSigHandle, Body(emit), MetadataTokens.ParameterHandle(1));
                md.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class, md.GetOrAddString(space), md.GetOrAddString(name),
                    obj, MetadataTokens.FieldDefinitionHandle(field), MetadataTokens.MethodDefinitionHandle(method));
                method++;
                if (withField) field++;
            }

            StaticType("Fixture.Agents", "Gen", "Use", c =>
            {
                c.Call(createMissing);
                c.OpCode(ILOpCode.Pop);
                c.OpCode(ILOpCode.Ret);
            });
            StaticType("Fixture.Icons", "Tok", "Use", c =>
            {
                c.OpCode(ILOpCode.Ldtoken);
                c.Token(listOfMissing);
                c.OpCode(ILOpCode.Pop);
                c.OpCode(ILOpCode.Ret);
            });
            StaticType("Fixture.Water", "Holder", "Read", c =>
            {
                c.OpCode(ILOpCode.Ldsfld);
                c.Token(MetadataTokens.FieldDefinitionHandle(1));
                c.OpCode(ILOpCode.Pop);
                c.OpCode(ILOpCode.Ret);
            }, withField: true);
            StaticType("Fixture.Events", "Ftn", "Use", c =>
            {
                c.OpCode(ILOpCode.Ldftn);
                c.Token(missing);
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
