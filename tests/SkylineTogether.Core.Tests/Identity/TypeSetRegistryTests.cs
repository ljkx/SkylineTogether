using System;
using System.Collections.Generic;
using System.Threading;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Identity;
using SkylineTogether.Core.Json;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Identity
{
    /// <summary>The session-global type-set registry and its keyframe (ARCH §3.6, §4.6.3 TYPESET/TSETKEY).</summary>
    public sealed class TypeSetRegistryTests
    {
        private static ITypeCatalog Catalog(int count)
        {
            var builder = new TypeCatalogBuilder(PolicyTableModel.Parse(MiniJson.Parse("{\"version\": 1}")));
            for (int i = 0; i < count; i++)
            {
                builder.Add(new TypeDescriptorInput { StableTypeHash = (ulong)(i + 1) * 0x1000, FullName = "T" + i, AssemblyName = "Game", SizeInChunk = 4 });
            }
            return builder.Build();
        }

        private static TypeRank[] R(params int[] ranks)
        {
            var result = new TypeRank[ranks.Length];
            for (int i = 0; i < ranks.Length; i++) result[i] = new TypeRank((ushort)ranks[i]);
            return result;
        }

        private static TypeRank[] Range(int from, int count)
        {
            var result = new TypeRank[count];
            for (int i = 0; i < count; i++) result[i] = new TypeRank((ushort)(from + i));
            return result;
        }

        private static ByteReader ReaderOf(ByteWriter writer) => new ByteReader(writer.ToArray(), 0, writer.Length);

        private static ByteReader ReaderOf(params uint[] varints)
        {
            var writer = new ByteWriter();
            foreach (uint v in varints) writer.WriteVarUInt32(v);
            return ReaderOf(writer);
        }

        [Test]
        public void SignaturesAreDeduplicatedAndIdsStartAtOne()
        {
            var registry = new TypeSetRegistry(Catalog(100));
            TypeSetId a = registry.GetOrAdd(R(1, 5, 9), out bool addedA);
            TypeSetId b = registry.GetOrAdd(R(1, 5), out bool addedB);
            TypeSetId again = registry.GetOrAdd(new List<TypeRank>(R(1, 5, 9)), out bool addedAgain);
            TypeSetId empty = registry.GetOrAdd(R(), out bool addedEmpty);
            Assert.Equal(new TypeSetId(1), a);
            Assert.True(addedA);
            Assert.Equal(new TypeSetId(2), b);
            Assert.True(addedB);
            Assert.Equal(a, again, "the same content gives the same id");
            Assert.False(addedAgain, "a known signature is not added again (no TYPESET record)");
            Assert.Equal(new TypeSetId(3), empty, "an entity without replicated types still has a (empty) signature");
            Assert.True(addedEmpty);
            Assert.Equal(3, registry.Count);
            Assert.Equal(3, registry.HighestId);
            Assert.False(registry.IsReplica);
            Assert.SequenceEqual(R(1, 5, 9), registry.Get(a));
            Assert.True(registry.TryGet(b, out IReadOnlyList<TypeRank> typesB));
            Assert.SequenceEqual(R(1, 5), typesB);
            Assert.Equal(0, registry.Get(empty).Count);
            Assert.True(registry.TryFind(R(1, 5), out TypeSetId found));
            Assert.Equal(b, found);
            Assert.False(registry.TryFind(R(1, 6), out TypeSetId notFound));
            Assert.Equal(TypeSetId.None, notFound);
        }

        [Test]
        public void OrdinalsArePositionsInTheSortedSet()
        {
            var registry = new TypeSetRegistry(Catalog(200));
            TypeSetId set = registry.GetOrAdd(R(3, 17, 18, 120, 199), out _);
            Assert.Equal(0, registry.OrdinalOf(set, new TypeRank(3)));
            Assert.Equal(1, registry.OrdinalOf(set, new TypeRank(17)));
            Assert.Equal(2, registry.OrdinalOf(set, new TypeRank(18)));
            Assert.Equal(3, registry.OrdinalOf(set, new TypeRank(120)));
            Assert.Equal(4, registry.OrdinalOf(set, new TypeRank(199)));
            Assert.Equal(-1, registry.OrdinalOf(set, new TypeRank(4)), "absent type");
            Assert.Equal(-1, registry.OrdinalOf(set, new TypeRank(0)));
            Assert.Equal(-1, registry.OrdinalOf(new TypeSetId(99), new TypeRank(3)), "unknown set");
            Assert.Equal(-1, registry.OrdinalOf(TypeSetId.None, new TypeRank(3)));

            TypeSetId full = registry.GetOrAdd(Range(100, 64), out _);
            for (int i = 0; i < 64; i++) Assert.Equal(i, registry.OrdinalOf(full, new TypeRank((ushort)(100 + i))), "ordinals are below 64");
        }

        [Test]
        public void InvalidSignaturesAreRefused()
        {
            var registry = new TypeSetRegistry(Catalog(70));
            Assert.Throws<ArgumentException>(() => registry.GetOrAdd(null, out _));
            Assert.Throws<ArgumentException>(() => registry.GetOrAdd(R(5, 3), out _), "unsorted");
            Assert.Throws<ArgumentException>(() => registry.GetOrAdd(R(3, 3), out _), "duplicate rank");
            Assert.Throws<ArgumentException>(() => registry.GetOrAdd(R(1, 70), out _), "rank outside the catalog");
            Assert.Throws<ArgumentException>(() => registry.GetOrAdd(Range(0, 65), out _), "more than 64 types");
            Assert.False(registry.TryGetOrAdd(R(9, 2), out TypeSetId id, out bool added, out string error));
            Assert.Equal(TypeSetId.None, id);
            Assert.False(added);
            Assert.Contains("strictly ascending", error);
            Assert.False(registry.TryFind(R(9, 2), out _));
            Assert.Equal(0, registry.Count, "nothing was added");
            Assert.Throws<ArgumentNullException>(() => new TypeSetRegistry(null));
        }

        [Test]
        public void UnknownIdsThrowUnknownTypeSetException()
        {
            var registry = new TypeSetRegistry(Catalog(10));
            registry.GetOrAdd(R(1), out _);
            UnknownTypeSetException e = Assert.Throws<UnknownTypeSetException>(() => registry.Get(new TypeSetId(7)));
            Assert.Equal(new TypeSetId(7), e.Id);
            Assert.Throws<UnknownTypeSetException>(() => registry.Get(TypeSetId.None));
            Assert.Throws<UnknownTypeSetException>(() => registry.Get(new TypeSetId(-3)));
            Assert.False(registry.TryGet(new TypeSetId(2), out IReadOnlyList<TypeRank> types));
            Assert.NotNull(types);
            Assert.Equal(0, types.Count, "an unknown id gives an empty list, never null");
            Assert.Contains("unknown", registry.Describe(new TypeSetId(2)));
            Assert.Contains("{1}", registry.Describe(new TypeSetId(1)));
        }

        [Test]
        public void KeyframeRoundTripsTheWholeRegistry()
        {
            ITypeCatalog catalog = Catalog(300);
            var host = new TypeSetRegistry(catalog);
            var random = new DeterministicRandom(17);
            var expected = new List<TypeRank[]>();
            while (expected.Count < 200)
            {
                var ranks = new SortedSet<int>();
                int n = random.Next(65);
                while (ranks.Count < n) ranks.Add(random.Next(300));
                TypeRank[] set = R(new List<int>(ranks).ToArray());
                host.GetOrAdd(set, out bool added);
                if (added) expected.Add(set);
            }

            var writer = new ByteWriter();
            host.WriteKeyframe(writer);
            var client = new TypeSetRegistry(catalog);
            ByteReader reader = ReaderOf(writer);
            client.ReadKeyframe(reader);
            Assert.True(reader.IsAtEnd, "the keyframe is consumed exactly");
            Assert.Equal(expected.Count, client.Count);
            Assert.True(client.IsReplica);
            for (int i = 0; i < expected.Count; i++)
            {
                var id = new TypeSetId(i + 1);
                Assert.SequenceEqual(expected[i], client.Get(id), id.ToString());
                Assert.True(client.TryFind(expected[i], out TypeSetId found));
                Assert.Equal(id, found);
            }

            var again = new ByteWriter();
            client.WriteKeyframe(again);
            Assert.SequenceEqual(writer.ToArray(), again.ToArray(), "the client's keyframe is byte-identical");
        }

        [Test]
        public void KeyframeFollowsTheDocumentedEncoding()
        {
            var registry = new TypeSetRegistry(Catalog(300));
            registry.GetOrAdd(R(2, 200), out _);
            registry.GetOrAdd(R(), out _);
            registry.GetOrAdd(R(7), out _);
            var writer = new ByteWriter();
            registry.WriteKeyframe(writer);
            // varint count, then per set: varint id, varint n, n x varint rank (200 = 0xC8 0x01 as a varint)
            Assert.SequenceEqual(new byte[] { 3, 1, 2, 2, 0xC8, 0x01, 2, 0, 3, 1, 7 }, writer.ToArray());

            var empty = new ByteWriter();
            new TypeSetRegistry(Catalog(1)).WriteKeyframe(empty);
            Assert.SequenceEqual(new byte[] { 0 }, empty.ToArray(), "an empty registry is one zero byte");
        }

        [Test]
        public void KeyframesWithGapsInTheIdsRoundTrip()
        {
            // a client learns ids from TYPESET records too, so its ids need not be dense
            var registry = new TypeSetRegistry(Catalog(20));
            Assert.True(registry.Define(new TypeSetId(4), R(1, 2)));
            Assert.True(registry.Define(new TypeSetId(9), R(3)));
            var writer = new ByteWriter();
            registry.WriteKeyframe(writer);
            Assert.SequenceEqual(new byte[] { 2, 4, 2, 1, 2, 9, 1, 3 }, writer.ToArray());
            var copy = new TypeSetRegistry(Catalog(20));
            copy.ReadKeyframe(ReaderOf(writer));
            Assert.Equal(2, copy.Count);
            Assert.Equal(9, copy.HighestId);
            Assert.False(copy.TryGet(new TypeSetId(5), out _));
            Assert.SequenceEqual(R(3), copy.Get(new TypeSetId(9)));
        }

        [Test]
        public void DefineOfAnIdenticalKnownIdSucceedsAndOfAContradictingOneFails()
        {
            var log = new ListLog();
            var client = new TypeSetRegistry(Catalog(50), log);
            Assert.True(client.Define(new TypeSetId(1), R(1, 2, 3)));
            Assert.True(client.Define(new TypeSetId(1), new List<TypeRank>(R(1, 2, 3))), "identical redefinition (log replay after a keyframe)");
            Assert.Equal(1, client.Count);
            Assert.Equal(0L, client.RejectedCount);

            Assert.False(client.Define(new TypeSetId(1), R(1, 2, 4)), "contradiction");
            Assert.False(client.TryDefine(new TypeSetId(1), R(1, 2), out string error));
            Assert.Contains("contradicts", error);
            Assert.SequenceEqual(R(1, 2, 3), client.Get(new TypeSetId(1)), "the known definition stays");
            Assert.Equal(2L, client.RejectedCount);
            Assert.Contains("typeset:1", client.LastRejection);
            Assert.Equal(2, log.Count(LogLevel.Warn));

            Assert.True(client.Define(new TypeSetId(2), R(1, 2, 3)), "two ids may share content (only the first is found by content)");
            Assert.True(client.TryFind(R(1, 2, 3), out TypeSetId first));
            Assert.Equal(new TypeSetId(1), first);
        }

        [Test]
        public void DefineRejectsInvalidIdsAndSignatures()
        {
            var client = new TypeSetRegistry(Catalog(10));
            Assert.False(client.Define(TypeSetId.None, R(1)));
            Assert.False(client.Define(new TypeSetId(-1), R(1)));
            Assert.False(client.Define(new TypeSetId(TypeSetRegistry.MaxTypeSetId + 1), R(1)));
            Assert.False(client.Define(new TypeSetId(1), R(2, 1)));
            Assert.False(client.Define(new TypeSetId(1), R(10)), "rank outside the catalog");
            Assert.False(client.Define(new TypeSetId(1), null));
            Assert.False(client.Define(new TypeSetId(1), Range(0, 65)));
            Assert.Equal(0, client.Count);
            Assert.Equal(7L, client.RejectedCount);
            Assert.True(client.Define(new TypeSetId(TypeSetRegistry.MaxTypeSetId), R(9)), "the highest id is accepted");
        }

        [Test]
        public void AContradictingKeyframeThrowsAndChangesNothing()
        {
            var client = new TypeSetRegistry(Catalog(50));
            Assert.True(client.Define(new TypeSetId(2), R(4, 5)));
            var host = new TypeSetRegistry(Catalog(50));
            host.GetOrAdd(R(1), out _);           // id 1
            host.GetOrAdd(R(4, 6), out _);        // id 2: contradicts the client's id 2
            host.GetOrAdd(R(7), out _);           // id 3
            var writer = new ByteWriter();
            host.WriteKeyframe(writer);
            DecodeException e = Assert.Throws<DecodeException>(() => client.ReadKeyframe(ReaderOf(writer)));
            Assert.Contains("contradicts", e.Message);
            Assert.Equal(1, client.Count, "nothing of the rejected keyframe was published");
            Assert.False(client.TryGet(new TypeSetId(1), out _));
            Assert.False(client.TryGet(new TypeSetId(3), out _));
            Assert.Equal(1L, client.RejectedCount);
        }

        [Test]
        public void AKeyframeMergesWithIdenticalKnownDefinitions()
        {
            var host = new TypeSetRegistry(Catalog(50));
            host.GetOrAdd(R(1), out _);
            host.GetOrAdd(R(2, 3), out _);
            host.GetOrAdd(R(4), out _);
            var writer = new ByteWriter();
            host.WriteKeyframe(writer);
            var client = new TypeSetRegistry(Catalog(50));
            Assert.True(client.Define(new TypeSetId(2), R(2, 3)));
            client.ReadKeyframe(ReaderOf(writer));
            Assert.Equal(3, client.Count);
            client.ReadKeyframe(ReaderOf(writer));
            Assert.Equal(3, client.Count, "reading the same keyframe twice is idempotent");
            Assert.SequenceEqual(R(4), client.Get(new TypeSetId(3)));
        }

        [Test]
        public void MalformedKeyframesThrowDecodeExceptionAndChangeNothing()
        {
            var cases = new List<KeyValuePair<string, Func<ByteReader>>>
            {
                new KeyValuePair<string, Func<ByteReader>>("empty input", () => ReaderOf()),
                new KeyValuePair<string, Func<ByteReader>>("truncated set", () => ReaderOf(1, 1, 3, 1)),
                new KeyValuePair<string, Func<ByteReader>>("truncated after the count", () => ReaderOf(2, 1, 0)),
                new KeyValuePair<string, Func<ByteReader>>("id 0", () => ReaderOf(1, 0, 0)),
                new KeyValuePair<string, Func<ByteReader>>("ids not ascending", () => ReaderOf(2, 3, 0, 3, 0)),
                new KeyValuePair<string, Func<ByteReader>>("ids descending", () => ReaderOf(2, 3, 0, 2, 0)),
                new KeyValuePair<string, Func<ByteReader>>("id above the maximum", () => ReaderOf(1, (uint)TypeSetRegistry.MaxTypeSetId + 1, 0)),
                new KeyValuePair<string, Func<ByteReader>>("rank outside the catalog", () => ReaderOf(1, 1, 1, 40)),
                new KeyValuePair<string, Func<ByteReader>>("ranks not ascending", () => ReaderOf(1, 1, 2, 5, 5)),
                new KeyValuePair<string, Func<ByteReader>>("more than 64 types", () => ReaderOf(1, 1, 65)),
                new KeyValuePair<string, Func<ByteReader>>("absurd count", () => ReaderOf(uint.MaxValue, 1, 0)),
                new KeyValuePair<string, Func<ByteReader>>("count beyond the remaining bytes", () => ReaderOf(3, 1, 0)),
                new KeyValuePair<string, Func<ByteReader>>("unterminated varint", () => new ByteReader(new byte[] { 1, 0x80 }, 0, 2))
            };
            foreach (KeyValuePair<string, Func<ByteReader>> c in cases)
            {
                var registry = new TypeSetRegistry(Catalog(40));
                Assert.True(registry.Define(new TypeSetId(7), R(1)));
                Assert.Throws<DecodeException>(() => registry.ReadKeyframe(c.Value()), c.Key);
                Assert.Equal(1L, registry.RejectedCount, c.Key + ": every rejection (overruns included) is counted once");
                Assert.NotNull(registry.LastRejection, c.Key);
                Assert.Equal(1, registry.Count, c.Key + ": unchanged");
                Assert.SequenceEqual(R(1), registry.Get(new TypeSetId(7)), c.Key);
            }
            Assert.Throws<ArgumentNullException>(() => new TypeSetRegistry(Catalog(1)).ReadKeyframe(null));
            Assert.Throws<ArgumentNullException>(() => new TypeSetRegistry(Catalog(1)).WriteKeyframe(null));
        }

        [Test]
        public void RandomKeyframeBytesNeverEscapeAsAnythingButDecodeException()
        {
            var random = new DeterministicRandom(99);
            var host = new TypeSetRegistry(Catalog(64));
            for (int i = 0; i < 20; i++) host.GetOrAdd(R(i, i + 1, i + 30), out _);
            var writer = new ByteWriter();
            host.WriteKeyframe(writer);
            byte[] valid = writer.ToArray();
            for (int round = 0; round < 3000; round++)
            {
                byte[] bytes = (byte[])valid.Clone();
                int flips = 1 + random.Next(4);
                for (int f = 0; f < flips; f++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                int length = random.Next(4) == 0 ? random.Next(bytes.Length + 1) : bytes.Length;
                var registry = new TypeSetRegistry(Catalog(64));
                try
                {
                    registry.ReadKeyframe(new ByteReader(bytes, 0, length));
                }
                catch (DecodeException)
                {
                    Assert.Equal(0, registry.Count, "a rejected keyframe publishes nothing");
                }
            }
        }

        [Test]
        public void AReplicaRefusesToInventIds()
        {
            var client = new TypeSetRegistry(Catalog(20));
            Assert.True(client.Define(new TypeSetId(1), R(1, 2)));
            Assert.True(client.IsReplica);
            Assert.Equal(new TypeSetId(1), client.GetOrAdd(R(1, 2), out bool added), "known signatures are found");
            Assert.False(added);
            Assert.Throws<InvalidOperationException>(() => client.GetOrAdd(R(3), out _));
            Assert.False(client.TryGetOrAdd(R(3), out _, out _, out string error));
            Assert.Contains("replica", error);

            var joiner = new TypeSetRegistry(Catalog(20));
            var keyframe = new ByteWriter();
            client.WriteKeyframe(keyframe);
            joiner.ReadKeyframe(ReaderOf(keyframe));
            Assert.True(joiner.IsReplica, "a keyframe makes a replica too");
            Assert.Throws<InvalidOperationException>(() => joiner.GetOrAdd(R(4), out _));

            var emptyKeyframe = new TypeSetRegistry(Catalog(20));
            emptyKeyframe.ReadKeyframe(ReaderOf(0));
            Assert.False(emptyKeyframe.IsReplica, "an empty keyframe defines nothing");
        }

        [Test]
        public void TheHostKeepsAssigningAboveDefinitionsItWasGiven()
        {
            var host = new TypeSetRegistry(Catalog(20));
            host.GetOrAdd(R(1), out _);
            Assert.True(host.Define(new TypeSetId(5), R(2)), "an authority accepts an external definition of a free id");
            Assert.False(host.IsReplica);
            Assert.Equal(new TypeSetId(6), host.GetOrAdd(R(3), out bool added));
            Assert.True(added);
        }

        [Test]
        public void ClearForgetsDefinitionsRoleAndCounters()
        {
            var client = new TypeSetRegistry(Catalog(20));
            Assert.True(client.Define(new TypeSetId(3), R(1)));
            Assert.False(client.Define(new TypeSetId(3), R(2)));
            client.Clear();
            Assert.Equal(0, client.Count);
            Assert.Equal(0, client.HighestId);
            Assert.False(client.IsReplica);
            Assert.Equal(0L, client.RejectedCount);
            Assert.Null(client.LastRejection);
            Assert.False(client.TryGet(new TypeSetId(3), out _));
            Assert.Equal(new TypeSetId(1), client.GetOrAdd(R(2), out bool added), "after a clear the registry may act as host again");
            Assert.True(added);
        }

        [Test]
        public void ManySetsGrowTheTable()
        {
            var registry = new TypeSetRegistry(Catalog(16));
            for (int k = 1; k < 1 << 16; k++)
            {
                Assert.Equal(new TypeSetId(k), registry.GetOrAdd(BitsOf(k), out bool added));
                Assert.True(added);
            }
            Assert.Equal((1 << 16) - 1, registry.Count);
            Assert.SequenceEqual(BitsOf(40000), registry.Get(new TypeSetId(40000)));
            Assert.Equal(new TypeSetId(12345), registry.GetOrAdd(BitsOf(12345), out bool again));
            Assert.False(again);
        }

        [Test(TimeoutSeconds = 120)]
        public void ReadersOnOtherThreadsSeeEveryPublishedDefinition()
        {
            const int sets = 6000;
            var registry = new TypeSetRegistry(Catalog(16));
            var failures = new List<string>();
            int writerDone = 0;
            var readers = new Thread[3];
            for (int t = 0; t < readers.Length; t++)
            {
                int seed = t + 1;
                readers[t] = new Thread(() =>
                {
                    var random = new DeterministicRandom((ulong)seed);
                    try
                    {
                        while (Volatile.Read(ref writerDone) == 0 || random.Next(4) != 0)
                        {
                            int count = registry.Count;
                            if (count == 0) continue;
                            int id = 1 + random.Next(count);
                            if (!registry.TryGet(new TypeSetId(id), out IReadOnlyList<TypeRank> types))
                            {
                                throw new InvalidOperationException("id " + id + " not visible although Count = " + count);
                            }
                            TypeRank[] expected = BitsOf(id);
                            if (types.Count != expected.Length) throw new InvalidOperationException("id " + id + " has " + types.Count + " types");
                            for (int i = 0; i < expected.Length; i++)
                            {
                                if (types[i] != expected[i]) throw new InvalidOperationException("id " + id + " differs at " + i);
                            }
                            if (registry.OrdinalOf(new TypeSetId(id), expected[expected.Length - 1]) != expected.Length - 1)
                            {
                                throw new InvalidOperationException("ordinal of id " + id);
                            }
                            if (Volatile.Read(ref writerDone) != 0 && count == sets) break;
                        }
                    }
                    catch (Exception e)
                    {
                        lock (failures) failures.Add(e.Message);
                    }
                });
                readers[t].IsBackground = true;
                readers[t].Start();
            }
            for (int k = 1; k <= sets; k++) registry.GetOrAdd(BitsOf(k), out _);
            Volatile.Write(ref writerDone, 1);
            foreach (Thread reader in readers) Assert.True(reader.Join(TimeSpan.FromSeconds(60)), "reader finished");
            Assert.Equal(0, failures.Count, string.Join("; ", failures));
            Assert.Equal(sets, registry.Count);
        }

        /// <summary>The set bits of <paramref name="k"/> as ascending ranks: a distinct, non-empty set per k ≥ 1.</summary>
        private static TypeRank[] BitsOf(int k)
        {
            var ranks = new List<TypeRank>();
            for (int bit = 0; bit < 16; bit++)
            {
                if ((k & (1 << bit)) != 0) ranks.Add(new TypeRank((ushort)bit));
            }
            return ranks.ToArray();
        }
    }
}
