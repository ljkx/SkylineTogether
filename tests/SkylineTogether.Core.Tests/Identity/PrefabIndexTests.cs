using System;
using System.Collections.Generic;
using System.Text;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Identity;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Identity
{
    /// <summary>Prefab ranks, digest, duplicates and class signatures (ARCH §3.5, §5.4.1 rule 5).</summary>
    public sealed class PrefabIndexTests
    {
        private static PrefabIdentity P(string type, string name, string hash, int entityIndex, string hint = null)
        {
            return new PrefabIdentity(type, name, hash, entityIndex, hint);
        }

        private static List<PrefabIdentity> Sample()
        {
            return new List<PrefabIdentity>
            {
                P("RoadPrefab", "Small Road", "aa01", 40, "Game"),
                P("BuildingPrefab", "Hospital01", "bb02", 12, "Game"),
                P("BuildingPrefab", "Clinic01", "bb03", 13, "Game"),
                P("StaticObjectPrefab", "Bench", "", 77, "Mod:Benches"),
                P("ZonePrefab", "Residential Low", "cc04", 5, "Game")
            };
        }

        [Test]
        public void RanksFollowTheOrdinalKeyOrder()
        {
            List<PrefabIdentity> prefabs = Sample();
            PrefabIndex index = PrefabIndex.Build(prefabs, new ulong[] { 10, 20, 30, 40, 50 });
            Assert.Equal(5, index.Count);
            string[] expected =
            {
                "BuildingPrefab|Clinic01|bb03", "BuildingPrefab|Hospital01|bb02", "RoadPrefab|Small Road|aa01",
                "StaticObjectPrefab|Bench|", "ZonePrefab|Residential Low|cc04"
            };
            for (int rank = 0; rank < expected.Length; rank++)
            {
                Assert.Equal(expected[rank], index.Describe(new PrefabRank(rank)));
                Assert.Equal(expected[rank], index.Sorted[rank].Key);
                Assert.True(index.TryGetRank(expected[rank], out PrefabRank found));
                Assert.Equal(new PrefabRank(rank), found);
            }
            // class signatures and entity indices travel with their prefab through the sort
            Assert.Equal(30UL, index.ClassSignature(new PrefabRank(0)));
            Assert.Equal(20UL, index.ClassSignature(new PrefabRank(1)));
            Assert.Equal(10UL, index.ClassSignature(new PrefabRank(2)));
            Assert.Equal(40UL, index.ClassSignature(new PrefabRank(3)));
            Assert.Equal(50UL, index.ClassSignature(new PrefabRank(4)));
            Assert.Equal(new PrefabRank(2), index.RankOfEntityIndex(40));
            Assert.Equal(new PrefabRank(4), index.RankOfEntityIndex(5));
            Assert.Equal(13, index.EntityIndexOf(new PrefabRank(0)));
            Assert.Equal(PrefabRank.None, index.RankOfEntityIndex(41));
            Assert.Equal(PrefabRank.None, index.RankOfEntityIndex(-1));
            Assert.Equal(0, index.Duplicates.Count);
            Assert.Equal(0, index.Problems.Count);
        }

        [Test]
        public void SortingIsOrdinalNotCultural()
        {
            PrefabIndex index = PrefabIndex.Build(new List<PrefabIdentity>
            {
                P("T", "b", "", 1), P("T", "B", "", 2), P("T", "ä", "", 3), P("T", "a", "", 4), P("T", "Z", "", 5)
            }, null);
            var order = new List<string>();
            foreach (PrefabIdentity p in index.Sorted) order.Add(p.Name);
            Assert.SequenceEqual(new[] { "B", "Z", "a", "b", "ä" }, order);
        }

        [Test]
        public void DigestIsIndependentOfInsertionOrder()
        {
            List<PrefabIdentity> prefabs = Sample();
            ulong digest = PrefabIndex.Build(prefabs, null).Digest;
            var random = new DeterministicRandom(5);
            for (int round = 0; round < 20; round++)
            {
                var shuffled = new List<PrefabIdentity>(prefabs);
                for (int i = shuffled.Count - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    PrefabIdentity t = shuffled[i];
                    shuffled[i] = shuffled[j];
                    shuffled[j] = t;
                }
                PrefabIndex index = PrefabIndex.Build(shuffled, null);
                Assert.Equal(digest, index.Digest, "round " + round);
                for (int rank = 0; rank < index.Count; rank++)
                {
                    Assert.Equal(PrefabIndex.Build(prefabs, null).Describe(new PrefabRank(rank)), index.Describe(new PrefabRank(rank)));
                }
            }
        }

        [Test]
        public void DigestFollowsTheDocumentedEncoding()
        {
            List<PrefabIdentity> prefabs = Sample();
            PrefabIndex index = PrefabIndex.Build(prefabs, null);
            var writer = new ByteWriter();
            writer.WriteVarUInt32((uint)index.Count);
            foreach (PrefabIdentity p in index.Sorted)
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(p.Key);
                writer.WriteVarUInt32((uint)utf8.Length);
                writer.WriteBytes(utf8, 0, utf8.Length);
            }
            Assert.Equal(XxHash64.Hash(writer.Buffer, 0, writer.Length), index.Digest);

            var empty = PrefabIndex.Build(new List<PrefabIdentity>(), null);
            Assert.Equal(XxHash64.Hash(new byte[] { 0 }, 0, 1), empty.Digest, "the empty list hashes its zero count");
        }

        [Test]
        public void DigestChangesWithAnyKeyAndWithDuplicates()
        {
            List<PrefabIdentity> prefabs = Sample();
            ulong digest = PrefabIndex.Build(prefabs, null).Digest;
            for (int i = 0; i < prefabs.Count; i++)
            {
                foreach (int part in new[] { 0, 1, 2 })
                {
                    var changed = new List<PrefabIdentity>(prefabs);
                    PrefabIdentity p = changed[i];
                    if (part == 0) p.TypeName += "X";
                    else if (part == 1) p.Name += "X";
                    else p.Hash += "0";
                    changed[i] = p;
                    Assert.NotEqual(digest, PrefabIndex.Build(changed, null).Digest, "prefab " + i + " part " + part);
                }
            }
            var withDuplicate = new List<PrefabIdentity>(prefabs) { P("RoadPrefab", "Small Road", "aa01", 900, "Mod:Copy") };
            Assert.NotEqual(digest, PrefabIndex.Build(withDuplicate, null).Digest, "a duplicate is part of the digest");

            // the source hint and the entity index are not identity
            var other = new List<PrefabIdentity>();
            foreach (PrefabIdentity p in prefabs) other.Add(P(p.TypeName, p.Name, p.Hash, p.EntityIndex + 1000, "elsewhere"));
            Assert.Equal(digest, PrefabIndex.Build(other, null).Digest);
        }

        [Test]
        public void DuplicatesAreRankedReportedAndResolvedToTheFirst()
        {
            var prefabs = new List<PrefabIdentity>
            {
                P("BuildingPrefab", "Shop", "h1", 30, "ModB"),
                P("BuildingPrefab", "Park", "h2", 31, "Game"),
                P("BuildingPrefab", "Shop", "h1", 29, "ModA")
            };
            PrefabIndex index = PrefabIndex.Build(prefabs, new ulong[] { 1, 2, 3 });
            Assert.Equal(3, index.Count);
            Assert.SequenceEqual(new[] { "BuildingPrefab|Shop|h1" }, index.Duplicates);
            Assert.SequenceEqual(new[] { "BuildingPrefab|Shop|h1 (2x: ModA, ModB)" }, index.DuplicateDetails);
            Assert.True(index.TryGetRank("BuildingPrefab|Shop|h1", out PrefabRank first));
            Assert.Equal(new PrefabRank(1), first, "Park sorts first; the duplicates share ranks 1 and 2");
            Assert.Equal(new PrefabRank(1), index.RankOfEntityIndex(29), "ties are ordered by source hint");
            Assert.Equal(new PrefabRank(2), index.RankOfEntityIndex(30));
            Assert.Equal(3UL, index.ClassSignature(new PrefabRank(1)));
            Assert.Equal(1UL, index.ClassSignature(new PrefabRank(2)));
        }

        [Test]
        public void KeysEscapeSeparatorsSoIdentitiesNeverCollide()
        {
            Assert.Equal("T|a|b", PrefabIdentity.MakeKey("T", "a", "b"));
            Assert.Equal("T||", PrefabIdentity.MakeKey("T", null, null));
            Assert.Equal("||", new PrefabIdentity().Key);
            string k1 = PrefabIdentity.MakeKey("T", "a|b", "c");
            string k2 = PrefabIdentity.MakeKey("T", "a", "b|c");
            Assert.NotEqual(k1, k2);
            Assert.Equal("T|a\\|b|c", k1);
            Assert.Equal("T|back\\\\slash|", PrefabIdentity.MakeKey("T", "back\\slash", ""));
            Assert.Equal("T|n|h (ModX)", P("T", "n", "h", 1, "ModX").ToString());
        }

        [Test]
        public void SparseEntityIndicesUseTheDictionaryPath()
        {
            var prefabs = new List<PrefabIdentity> { P("A", "x", "", 5_000_000), P("B", "y", "", 3), P("C", "z", "", -1) };
            PrefabIndex index = PrefabIndex.Build(prefabs, null);
            Assert.Equal(new PrefabRank(0), index.RankOfEntityIndex(5_000_000));
            Assert.Equal(new PrefabRank(1), index.RankOfEntityIndex(3));
            Assert.Equal(PrefabRank.None, index.RankOfEntityIndex(4));
            Assert.Equal(-1, index.EntityIndexOf(new PrefabRank(2)), "an unknown entity index stays unmapped");
            Assert.Equal(0UL, index.ClassSignature(new PrefabRank(2)), "null signatures are zeros");
        }

        [Test]
        public void ARepeatedEntityIndexKeepsItsFirstRank()
        {
            var prefabs = new List<PrefabIdentity> { P("A", "x", "", 8), P("B", "y", "", 8) };
            PrefabIndex index = PrefabIndex.Build(prefabs, null);
            Assert.Equal(new PrefabRank(0), index.RankOfEntityIndex(8));
            Assert.Equal(1, index.Problems.Count);
            Assert.Contains("B|y|", index.Problems[0]);
        }

        [Test]
        public void InvalidInputAndRanks()
        {
            Assert.Throws<ArgumentNullException>(() => PrefabIndex.Build(null, null));
            Assert.Throws<ArgumentException>(() => PrefabIndex.Build(Sample(), new ulong[] { 1 }));
            PrefabIndex index = PrefabIndex.Build(Sample(), null);
            Assert.Equal("<no prefab>", index.Describe(PrefabRank.None));
            Assert.Equal("<unknown prefab rank 99>", index.Describe(new PrefabRank(99)));
            Assert.Equal(-1, index.EntityIndexOf(PrefabRank.None));
            Assert.Equal(0UL, index.ClassSignature(new PrefabRank(99)));
            Assert.False(index.TryGetRank(null, out PrefabRank rank));
            Assert.True(rank.IsNone);
            Assert.False(index.TryGetRank("nope", out _));
            Assert.Null(index.KeyOf(new PrefabRank(-1)));
            Assert.Equal(5, index.CopyKeys().Count);
        }

        [Test]
        public void ClassSignatureDigestFollowsTheSignaturesInRankOrder()
        {
            List<PrefabIdentity> prefabs = Sample();
            PrefabIndex a = PrefabIndex.Build(prefabs, new ulong[] { 1, 2, 3, 4, 5 });
            PrefabIndex b = PrefabIndex.Build(prefabs, new ulong[] { 1, 2, 3, 4, 6 });
            Assert.Equal(a.Digest, b.Digest, "signatures are not part of the prefab digest");
            Assert.NotEqual(a.ClassSignatureDigest, b.ClassSignatureDigest);
            var reversed = new List<PrefabIdentity>(prefabs);
            reversed.Reverse();
            PrefabIndex c = PrefabIndex.Build(reversed, new ulong[] { 5, 4, 3, 2, 1 });
            Assert.Equal(a.ClassSignatureDigest, c.ClassSignatureDigest, "the same signature per prefab in any input order");
        }

        [Test]
        public void TheLoggingBuildReportsSummaryDuplicatesAndProblems()
        {
            var prefabs = new List<PrefabIdentity>
            {
                P("BuildingPrefab", "Shop", "h1", 30, "ModB"),
                P("BuildingPrefab", "Shop", "h1", 29, "ModA"),
                P("RoadPrefab", "Road", "h2", 29, "Game")
            };
            var log = new ListLog();
            PrefabIndex index = PrefabIndex.Build(prefabs, null, log);
            Assert.Equal(PrefabIndex.Build(prefabs, null).Digest, index.Digest, "logging does not change the result");
            Assert.True(log.Contains("[join] prefab index: 3 prefabs, 1 duplicate key(s), digest " + index.Digest.ToString("X16")));
            Assert.True(log.Contains("duplicate prefab id BuildingPrefab|Shop|h1 (2x: ModA, ModB)"));
            Assert.True(log.Contains("prefab entity index 29"));
            Assert.Equal(2, log.Count(LogLevel.Warn));

            var many = new List<PrefabIdentity>();
            for (int i = 0; i < 30; i++)
            {
                many.Add(P("T", "dup" + i, "", 2 * i));
                many.Add(P("T", "dup" + i, "", 2 * i + 1));
            }
            var capped = new ListLog();
            PrefabIndex.Build(many, null, capped);
            Assert.Equal(PrefabIndex.MaxLoggedFindings + 1, capped.Count(LogLevel.Warn), "findings are capped with one summary line");
            Assert.True(capped.Contains("10 more line(s)"));
            Assert.DoesNotThrow(() => PrefabIndex.Build(Sample(), null, null), "a null log is allowed");
        }

        [Test]
        public void DiffIsAMultisetDifference()
        {
            var host = new List<string> { "T|a|", "T|b|", "T|b|", "T|c|", null };
            var client = new List<string> { "T|b|", "T|c|", "T|d|", "T|d|", null };
            PrefabIndex.Diff(host, client, out IReadOnlyList<string> missing, out IReadOnlyList<string> extra);
            Assert.SequenceEqual(new[] { "T|a|", "T|b|" }, missing);
            Assert.SequenceEqual(new[] { "T|d|", "T|d|" }, extra);

            PrefabIndex.Diff(null, client, out missing, out extra);
            Assert.Equal(0, missing.Count);
            Assert.Equal(4, extra.Count);
            PrefabIndex.Diff(host, host, out missing, out extra);
            Assert.Equal(0, missing.Count + extra.Count);
        }
    }
}
