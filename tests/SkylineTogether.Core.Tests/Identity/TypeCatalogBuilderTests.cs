using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Identity;
using SkylineTogether.Core.Json;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Identity
{
    /// <summary>Type ranks, effective policies and the catalog fingerprint (ARCH §3.6, §4.2.7).</summary>
    public sealed class TypeCatalogBuilderTests
    {
        private const string Policies = @"{
  'version': 4,
  'types': [
    { 'type': 'Game.Objects.Surface', 'excluded': true },
    { 'type': 'Game.Net.Road', 'playerAuthorable': true, 'authorableMask': 'Road', 'topics': [ 'Traffic' ], 'applyTags': [ 'BatchesUpdated' ] },
    { 'type': 'Game.Buildings.Building', 'deferredFields': 'Building' },
    { 'type': 'Game.Triggers.Chirp', 'mergePolicy': 'Chirp', 'class': 'A' },
    { 'type': 'Game.Pathfind.PathInformation', 'onDemand': true },
    { 'type': 'Game.Pathfind.PathInformation', 'family': 'Segment', 'class': 'S', 'source': true, 'cacheReceived': true, 'guarded': true,
      'standInResolver': 'SegmentPathEndpoints' },
    { 'type': 'Game.Buildings.Renter', 'dataReplicated': true, 'hashAsSet': true },
    { 'type': 'Game.Simulation.UpdateFrame', 'codec': 'UpdateFrame' },
    { 'type': 'Game.Missing.TypeFromAnOldPatch', 'excluded': true },
    { 'type': 'Game.Missing.ScopedOnly', 'family': 'Mover', 'class': 'M' }
  ]
}";

        private static PolicyTableModel Model(string json = Policies) => PolicyTableModel.Parse(MiniJson.Parse(json.Replace('\'', '"')));

        private static TypeDescriptorInput T(ulong hash, string name, TypeKind kind = TypeKind.Serializable, bool buffer = false,
                                             bool enableable = false, bool asEnabled = false, int size = 8, int element = 0, int entities = 0)
        {
            return new TypeDescriptorInput
            {
                StableTypeHash = hash, FullName = name, AssemblyName = "Game", Kind = kind, IsBuffer = buffer, IsEnableable = enableable,
                SerializeAsEnabled = asEnabled, SizeInChunk = size, ElementSize = element, EntityOffsetCount = entities
            };
        }

        private static List<TypeDescriptorInput> Inputs()
        {
            return new List<TypeDescriptorInput>
            {
                T(0x9000, "Game.Net.Road", size: 36),
                T(0x1000, "Game.Objects.Surface", size: 5),
                T(0x5000, "Game.Buildings.Building", size: 20, entities: 1),
                T(0x3000, "Game.Triggers.Chirp", size: 24, entities: 1),
                T(0x7000, "Game.Pathfind.PathInformation", size: 32, entities: 2),
                T(0x2000, "Game.Buildings.Renter", TypeKind.EmptyData, buffer: true, size: 16, element: 8, entities: 1),
                T(0x8000, "Game.Simulation.UpdateFrame", TypeKind.SharedSerializable, size: 4),
                T(0x4000, "Game.Common.Native", TypeKind.EmptyTag, size: 0),
                T(0x6000, "Game.Prefabs.Locked", TypeKind.EmptyTag, enableable: true, size: 0),
                T(0xA000, "Game.Citizens.CarKeeper", enableable: true, size: 12, entities: 1),
                T(0xB000, "Game.Objects.Tree", TypeKind.EmptyTag, enableable: true, asEnabled: true, size: 0),
                T(0xC000, "Game.Net.SubLane", TypeKind.EmptyData, buffer: true, size: 16, element: 8, entities: 1)
            };
        }

        private static TypeCatalog Build(IEnumerable<TypeDescriptorInput> inputs, PolicyTableModel model = null, ILog log = null)
        {
            var builder = new TypeCatalogBuilder(model ?? Model(), log);
            foreach (TypeDescriptorInput input in inputs) builder.Add(input);
            return (TypeCatalog)builder.Build();
        }

        [Test]
        public void RanksFollowTheStableTypeHash()
        {
            TypeCatalog catalog = Build(Inputs());
            Assert.Equal(12, catalog.Count);
            ulong previous = 0;
            for (int rank = 0; rank < catalog.Count; rank++)
            {
                TypeDescriptor d = catalog[new TypeRank((ushort)rank)];
                Assert.Equal(new TypeRank((ushort)rank), d.Rank);
                Assert.True(d.StableTypeHash > previous);
                previous = d.StableTypeHash;
            }
            Assert.Equal("Game.Objects.Surface", catalog[new TypeRank(0)].FullName);
            Assert.True(catalog.TryGetByName("Game.Net.Road", out TypeDescriptor road));
            Assert.Equal(new TypeRank(8), road.Rank);
            Assert.True(catalog.TryGetByStableHash(0x5000, out TypeDescriptor building));
            Assert.Equal("Game.Buildings.Building", building.FullName);
            Assert.False(catalog.TryGetByStableHash(0x5001, out TypeDescriptor none));
            Assert.Null(none);
            Assert.False(catalog.TryGetByName("Game.Nope", out _));
            Assert.False(catalog.TryGetByName(null, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => { TypeDescriptor _ = catalog[new TypeRank(12)]; });
            Assert.True(catalog.IsValid(new TypeRank(11)));
            Assert.False(catalog.IsValid(new TypeRank(12)));
            Assert.Equal(4, catalog.PolicyVersion);
            Assert.Equal(12, catalog.Descriptors.Count);
        }

        [Test]
        public void PoliciesAndFactsMakeTheEffectiveDescriptor()
        {
            TypeCatalog catalog = Build(Inputs());
            TypeDescriptor Get(string name)
            {
                Assert.True(catalog.TryGetByName(name, out TypeDescriptor d), name);
                return d;
            }

            TypeDescriptor surface = Get("Game.Objects.Surface");
            Assert.True(surface.Has(TypeFlags.Excluded));
            Assert.Equal(ReplicationClass.Excluded, surface.ClassOverride, "no class override (exclusion is the flag)");

            TypeDescriptor road = Get("Game.Net.Road");
            Assert.Equal(TypeFlags.PlayerAuthorable | TypeFlags.AuthorableMask, road.Flags);
            Assert.Equal(AttentionTopic.Traffic, road.Topics);
            Assert.Equal(TouchFlags.BatchesUpdated, road.ApplyTags);
            Assert.Equal(CodecKind.Serializable, road.Codec);
            Assert.Null(road.MergePolicy);

            TypeDescriptor building = Get("Game.Buildings.Building");
            Assert.True(building.Has(TypeFlags.DeferredFields));
            Assert.Equal("Building", building.MergePolicy);

            TypeDescriptor chirp = Get("Game.Triggers.Chirp");
            Assert.True(chirp.Has(TypeFlags.Merge));
            Assert.Equal("Chirp", chirp.MergePolicy);
            Assert.Equal(ReplicationClass.A, chirp.ClassOverride);

            TypeDescriptor path = Get("Game.Pathfind.PathInformation");
            Assert.Equal(ReplicationClass.OnDemand, path.ClassOverride, "the family-independent entry");
            Assert.True(path.Has(TypeFlags.OnDemand));
            Assert.False(path.Has(TypeFlags.Source), "family-scoped entries are resolved per family by core-replication");
            Assert.False(path.Has(TypeFlags.CacheReceived));

            TypeDescriptor renter = Get("Game.Buildings.Renter");
            Assert.Equal(TypeFlags.Buffer | TypeFlags.DataReplicatedPresence | TypeFlags.HashAsSet, renter.Flags);
            Assert.Equal(CodecKind.EntityRefList, renter.Codec, "data-replicated presence types use the EntityRefList codec");

            Assert.Equal(CodecKind.UpdateFrame, Get("Game.Simulation.UpdateFrame").Codec, "codec override");
            Assert.Equal(CodecKind.EmptyTag, Get("Game.Common.Native").Codec);
            Assert.Equal(CodecKind.EnableBit, Get("Game.Prefabs.Locked").Codec, "an enableable tag replicates its enabled bit");
            Assert.True(Get("Game.Prefabs.Locked").Has(TypeFlags.Enableable));
            Assert.Equal(CodecKind.Serializable, Get("Game.Citizens.CarKeeper").Codec, "an enableable data type keeps its codec");
            Assert.Equal(CodecKind.EmptyTag, Get("Game.Objects.Tree").Codec, "ISerializeAsEnabled ignores the enabled bit");
            Assert.Equal(TypeFlags.Enableable | TypeFlags.SerializeAsEnabled, Get("Game.Objects.Tree").Flags);
            Assert.Equal(CodecKind.EmptyTag, Get("Game.Net.SubLane").Codec, "presence only");
            Assert.Equal(TypeFlags.Buffer, Get("Game.Net.SubLane").Flags);
            Assert.Equal(1, catalog.CountOfKind(TypeKind.SharedSerializable));
            Assert.Equal(3, catalog.CountOfKind(TypeKind.EmptyTag));
        }

        [Test]
        public void DefaultCodecsPerKind()
        {
            Assert.Equal(CodecKind.Serializable, TypeCatalogBuilder.DefaultCodec(TypeKind.Serializable, TypeFlags.Enableable));
            Assert.Equal(CodecKind.EmptyTag, TypeCatalogBuilder.DefaultCodec(TypeKind.EmptyTag, TypeFlags.None));
            Assert.Equal(CodecKind.EnableBit, TypeCatalogBuilder.DefaultCodec(TypeKind.EmptyTag, TypeFlags.Enableable));
            Assert.Equal(CodecKind.EmptyTag, TypeCatalogBuilder.DefaultCodec(TypeKind.EmptyTag, TypeFlags.Enableable | TypeFlags.SerializeAsEnabled));
            Assert.Equal(CodecKind.EmptyTag, TypeCatalogBuilder.DefaultCodec(TypeKind.EmptyData, TypeFlags.Buffer));
            Assert.Equal(CodecKind.EnableBit, TypeCatalogBuilder.DefaultCodec(TypeKind.EmptyData, TypeFlags.Enableable));
            Assert.Equal(CodecKind.EntityRefList, TypeCatalogBuilder.DefaultCodec(TypeKind.EmptyData, TypeFlags.DataReplicatedPresence | TypeFlags.Enableable));
            Assert.Equal(CodecKind.Shared, TypeCatalogBuilder.DefaultCodec(TypeKind.SharedSerializable, TypeFlags.None));
        }

        [Test]
        public void TheFingerprintIsIndependentOfInsertionOrder()
        {
            List<TypeDescriptorInput> inputs = Inputs();
            ulong fingerprint = Build(inputs).Fingerprint;
            var reversed = new List<TypeDescriptorInput>(inputs);
            reversed.Reverse();
            Assert.Equal(fingerprint, Build(reversed).Fingerprint);
            Assert.NotEqual(0UL, fingerprint);
        }

        [Test]
        public void TheFingerprintChangesWithAnyInputAndThePolicyVersion()
        {
            List<TypeDescriptorInput> inputs = Inputs();
            ulong fingerprint = Build(inputs).Fingerprint;
            var mutations = new List<Func<TypeDescriptorInput, TypeDescriptorInput>>
            {
                t => { t.StableTypeHash ^= 0x10000; return t; },
                t => { t.FullName += "2"; return t; },
                t => { t.AssemblyName += "2"; return t; },
                t => { t.Kind = t.Kind == TypeKind.Serializable ? TypeKind.EmptyData : TypeKind.Serializable; return t; },
                t => { t.IsBuffer = !t.IsBuffer; return t; },
                t => { t.IsEnableable = !t.IsEnableable; return t; },
                t => { t.SerializeAsEnabled = !t.SerializeAsEnabled; return t; },
                t => { t.SizeInChunk += 1; return t; },
                t => { t.ElementSize += 1; return t; },
                t => { t.EntityOffsetCount += 1; return t; }
            };
            for (int m = 0; m < mutations.Count; m++)
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    var changed = new List<TypeDescriptorInput>(inputs);
                    changed[i] = mutations[m](changed[i]);
                    Assert.NotEqual(fingerprint, Build(changed).Fingerprint, "mutation " + m + " of input " + i);
                }
            }
            Assert.NotEqual(fingerprint, Build(inputs, Model(Policies.Replace("'version': 4", "'version': 5"))).Fingerprint, "policy version");
            Assert.NotEqual(fingerprint, Build(inputs, Model(Policies.Replace("'codec': 'UpdateFrame'", "'codec': 'Shared'"))).Fingerprint, "codec");
            Assert.NotEqual(fingerprint, Build(inputs.GetRange(0, 11)).Fingerprint, "type set");
        }

        [Test]
        public void TheFingerprintFollowsTheDocumentedEncoding()
        {
            TypeCatalog catalog = Build(Inputs());
            var writer = new ByteWriter();
            writer.WriteInt32(4);
            writer.WriteVarUInt32((uint)catalog.Count);
            foreach (TypeDescriptor d in catalog.Descriptors)
            {
                writer.WriteUInt64(d.StableTypeHash);
                writer.WriteString(d.FullName, 1 << 16);
                writer.WriteString(d.AssemblyName, 1 << 16);
                writer.WriteByte((byte)d.Kind);
                byte facts = (byte)((d.Has(TypeFlags.Buffer) ? 1 : 0) | (d.Has(TypeFlags.Enableable) ? 2 : 0) | (d.Has(TypeFlags.SerializeAsEnabled) ? 4 : 0));
                writer.WriteByte(facts);
                writer.WriteVarUInt32((uint)d.SizeInChunk);
                writer.WriteVarUInt32((uint)d.ElementSize);
                writer.WriteVarUInt32((uint)d.EntityOffsetCount);
                writer.WriteByte((byte)d.Codec);
            }
            Assert.Equal(XxHash64.Hash(writer.Buffer, 0, writer.Length, 0x535443415431UL), catalog.Fingerprint);
        }

        [Test]
        public void AssemblyDisplayNamesAreReducedToTheSimpleName()
        {
            // StableTypeHash covers the assembly name but not its version (R12 §7); versions are compared separately
            List<TypeDescriptorInput> inputs = Inputs();
            ulong fingerprint = Build(inputs).Fingerprint;
            var versioned = new List<TypeDescriptorInput>();
            foreach (TypeDescriptorInput input in inputs)
            {
                TypeDescriptorInput v = input;
                v.AssemblyName = "Game, Version=1.2.3.4, Culture=neutral, PublicKeyToken=null";
                versioned.Add(v);
            }
            TypeCatalog catalog = Build(versioned);
            Assert.Equal(fingerprint, catalog.Fingerprint);
            Assert.Equal("Game", catalog[new TypeRank(0)].AssemblyName);
            Assert.Equal(string.Empty, TypeCatalogBuilder.SimpleAssemblyName(null));
            Assert.Equal(string.Empty, TypeCatalogBuilder.SimpleAssemblyName(""));
            Assert.Equal("Mod", TypeCatalogBuilder.SimpleAssemblyName("  Mod , Version=2.0"));
            Assert.Equal("Mod.Core", TypeCatalogBuilder.SimpleAssemblyName("Mod.Core"));

            var builder = new TypeCatalogBuilder(Model());
            builder.Add(inputs[0]);
            builder.Add(versioned[0]);
            Assert.Equal(1, builder.Count, "the same type with a display name is the same input");
        }

        [Test]
        public void PolicyOnlyChangesOutsideTheCodecDoNotTouchTheFingerprint()
        {
            // the policy file's full content is compared separately (data-file digests, ARCH §4.2.7)
            List<TypeDescriptorInput> inputs = Inputs();
            ulong fingerprint = Build(inputs).Fingerprint;
            Assert.Equal(fingerprint, Build(inputs, Model(Policies.Replace("'topics': [ 'Traffic' ]", "'topics': [ 'Noise' ]"))).Fingerprint);
        }

        [Test]
        public void WarningsNameUnknownPoliciesAndUselessFlags()
        {
            var log = new ListLog();
            List<TypeDescriptorInput> inputs = Inputs();
            inputs.Add(T(0xD000, "Game.Net.Road", size: 36));                    // a second type of the same name
            inputs.Add(T(0xE000, "Mod.Weird", entities: 0));
            PolicyTableModel model = Model(Policies.Replace("{ 'type': 'Game.Missing.ScopedOnly'",
                "{ 'type': 'Mod.Weird', 'dataReplicated': true, 'hashAsSet': true, 'codec': 'EntityRefList' },\n    { 'type': 'Game.Missing.ScopedOnly'"));
            TypeCatalog catalog = Build(inputs, model, log);
            string all = string.Join("\n", catalog.Warnings);
            Assert.Contains("Game.Missing.TypeFromAnOldPatch", all);
            Assert.Contains("Game.Missing.ScopedOnly", all);
            Assert.Contains("two catalogued types are named Game.Net.Road", all);
            Assert.Contains("Mod.Weird: \"dataReplicated\"", all);
            Assert.Contains("Mod.Weird: \"hashAsSet\"", all);
            Assert.Contains("Mod.Weird: the EntityRefList codec", all);
            Assert.True(catalog.TryGetByName("Game.Net.Road", out TypeDescriptor road));
            Assert.Equal(new TypeRank(8), road.Rank, "the lower rank wins the name");
            Assert.True(log.Contains("type catalog: 14 types"));
            Assert.Equal(catalog.Warnings.Count, log.Count(LogLevel.Warn));
        }

        [Test]
        public void RepeatedAndCollidingInputs()
        {
            var builder = new TypeCatalogBuilder(Model());
            TypeDescriptorInput road = T(0x9000, "Game.Net.Road");
            builder.Add(road);
            builder.Add(road);
            Assert.Equal(1, builder.Count, "an identical input is ignored");
            Assert.Throws<ArgumentException>(() => builder.Add(T(0x9000, "Game.Net.Other")));
            Assert.Throws<ArgumentException>(() => builder.Add(T(0x9001, null)));
            Assert.Throws<ArgumentException>(() => builder.Add(T(0x9002, "  ")));
            Assert.Throws<ArgumentException>(() => builder.Add(T(0x9003, new string('x', TypeCatalogBuilder.MaxNameLength + 1))));
            Assert.Throws<ArgumentOutOfRangeException>(() => builder.Add(T(0x9004, "A", size: -1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => builder.Add(T(0x9005, "B", element: -1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => builder.Add(T(0x9006, "C", entities: -1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => builder.Add(T(0x9007, "D", kind: (TypeKind)9)));
            Assert.Throws<ArgumentNullException>(() => new TypeCatalogBuilder(null));
            Assert.Equal(1, builder.Build().Count);
        }

        [Test]
        public void MoreThan65536TypesAreRefused()
        {
            var builder = new TypeCatalogBuilder(Model());
            for (int i = 0; i <= TypeCatalogBuilder.MaxTypes; i++) builder.Add(T((ulong)i + 1, "T" + i));
            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Test]
        public void AnEmptyCatalogIsValid()
        {
            TypeCatalog catalog = Build(new List<TypeDescriptorInput>());
            Assert.Equal(0, catalog.Count);
            Assert.False(catalog.IsValid(new TypeRank(0)));
            Assert.False(catalog.TryGetByStableHash(1, out _));
        }
    }
}
