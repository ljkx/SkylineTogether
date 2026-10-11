using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Codec;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Codec.CodecTestUtil;

namespace SkylineTogether.Core.Tests.Codec
{
    /// <summary>The NetRef layout inside canonical bytes (ARCH §4.3.3) and the entity seams around it.</summary>
    public sealed class NetRefEncodingTests
    {
        private static string Encode(NetRef value) => WriteHex(w => w.WriteNetRef(value));

        private static NetRef Decode(string hex)
        {
            CanonicalReader reader = Reader(hex);
            NetRef value = reader.ReadNetRef();
            Assert.True(reader.IsRecordConsumed, "the reference consumed exactly its bytes");
            return value;
        }

        private static void Golden(string hex, NetRef value)
        {
            Assert.Equal(hex, Encode(value), value.ToString());
            Assert.Equal(value, Decode(hex), hex);
        }

        [Test]
        public void NullAndLocal()
        {
            Golden("00", NetRef.Null);
            Golden("04", NetRef.Local);
            Golden("00", default(NetRef));
        }

        [Test]
        public void Slots()
        {
            Golden("05", NetRef.ForSlot(new NetSlot(1)));
            Golden("7d", NetRef.ForSlot(new NetSlot(31)));
            Golden("81 01", NetRef.ForSlot(new NetSlot(32)));
            Golden("fd ff ff 1f", NetRef.ForSlot(new NetSlot(NetSlot.MaxValue)));
        }

        [Test]
        public void PrefabRanks()
        {
            Golden("02", NetRef.ForPrefab(new PrefabRank(0)));
            Golden("06", NetRef.ForPrefab(new PrefabRank(1)));
            Golden("a2 1f", NetRef.ForPrefab(new PrefabRank(1000)));
            Golden("fe ff ff ff 1f", NetRef.ForPrefab(new PrefabRank(int.MaxValue)));
        }

        [Test]
        public void ProvisionalIds()
        {
            Golden("03 00 01", NetRef.ForProvisional(new LocalId(1)));
            Golden("03 00 ac 02", NetRef.ForProvisional(new LocalId(300)));
            Golden("03 00 ff ff ff ff 0f", NetRef.ForProvisional(new LocalId(uint.MaxValue)));
        }

        [Test]
        public void DerivedKeysCarryEveryField()
        {
            // lane: owner slot 5, prefab rank 7, flags -1 (unused), quantised position, path nodes in A/B/C
            Golden("03 01 05 08 01 80 01 7f 00 81 80 80 80 50 00 01",
                   NetRef.ForDerived(new DerivedKey(DerivedKeyKind.Lane, new NetSlot(5), new PrefabRank(7), -1, 64, -64, 0, 0x500000001UL, 0, 1)));
            // sub-object without prefab: prefab encoded as 0 (rank + 1)
            Golden("03 02 a3 02 00 06 02 04 06 00 00 00",
                   NetRef.ForDerived(new DerivedKey(DerivedKeyKind.SubObject, new NetSlot(0x123), PrefabRank.None, 3, 1, 2, 3, 0, 0, 0)));
            // owned-route element (subkind 7): A = 1 (segment)
            Golden("03 07 01 01 04 00 00 00 01 00 00",
                   NetRef.ForDerived(new DerivedKey(DerivedKeyKind.OwnedRouteElement, new NetSlot(1), new PrefabRank(0), 2, 0, 0, 0, 1, 0, 0)));
            // extremes of every field
            Golden("03 06 ff ff ff 07 80 80 80 80 08 ff ff ff ff 0f fe ff ff ff 0f 01 02 ff ff ff ff ff ff ff ff ff 01 80 01 7f",
                   NetRef.ForDerived(new DerivedKey(DerivedKeyKind.FlowEdge, new NetSlot(NetSlot.MaxValue), new PrefabRank(int.MaxValue),
                                                    int.MinValue, int.MaxValue, -1, 1, ulong.MaxValue, 0x80, 0x7F)));
        }

        [Test]
        public void EveryDerivedSubkindRoundTrips()
        {
            for (byte kind = 1; kind <= 7; kind++)
            {
                var key = new DerivedKey((DerivedKeyKind)kind, new NetSlot(1000u + kind), new PrefabRank(kind * 3), kind - 2, kind * 64, -kind, kind, kind, (ulong)kind << 40, 0);
                NetRef value = NetRef.ForDerived(key);
                string hex = Encode(value);
                Assert.True(hex.StartsWith("03 0" + kind, StringComparison.Ordinal), "tag 3, subkind " + kind + ": " + hex);
                Assert.Equal(value, Decode(hex));
            }
        }

        [Test]
        public void NonCanonicalOrInvalidEncodingsAreRejected()
        {
            string[] invalid =
            {
                "08",                          // tag 0 with payload 2
                "01",                          // slot 0
                "81 80 80 20",                 // slot 0x1000000
                "82 80 80 80 20",              // prefab rank 2^31
                "07 00",                       // extended tag with payload 1
                "03 08", "03 ff",              // unknown subkinds
                "03 00 00",                    // provisional LocalId 0
                "80 00",                       // overlong head
                "84 00",                       // overlong head with a payload bit
                "ff ff ff ff ff ff ff ff ff 02", // head above 64 bits
                "03 01 80 80 80 08 00 00 00 00 00 00 00 00",      // owner slot 0x1000000
                "03 01 00 81 80 80 80 08 00 00 00 00 00 00 00",   // prefab rank + 1 = 2^31 + 1
                "03 01 ff ff ff ff 1f 00 00 00 00 00 00 00 00",   // owner varint above 32 bits
                "03 01 05 08 01 80 81 00 7f 00 00 00 00",         // overlong qx
            };
            foreach (string hex in invalid)
            {
                Assert.Throws<DecodeException>(() => Decode(hex), hex);
            }
        }

        [Test]
        public void TruncatedReferencesOverrun()
        {
            string full = Encode(NetRef.ForDerived(new DerivedKey(DerivedKeyKind.Lane, new NetSlot(5), new PrefabRank(7), -1, 64, -64, 0, 0x500000001UL, 0, 1)));
            byte[] bytes = Bytes(full);
            for (int length = 0; length < bytes.Length; length++)
            {
                byte[] cut = new byte[length];
                Array.Copy(bytes, cut, length);
                Assert.Throws<DecodeException>(() => Reader(cut).ReadNetRef(), "truncated to " + length + " bytes");
            }
            Assert.Throws<DecodeException>(() => Decode("81"));
            Assert.Throws<DecodeException>(() => Decode("03 00"));
        }

        [Test]
        public void RandomReferencesRoundTripCanonically()
        {
            var random = new DeterministicRandom(4303);
            for (int i = 0; i < 5000; i++)
            {
                NetRef value = RandomNetRef(random);
                byte[] bytes = Write(w => w.WriteNetRef(value));
                CanonicalReader reader = Reader(bytes);
                NetRef back = reader.ReadNetRef();
                Assert.Equal(value, back, "case " + i);
                Assert.True(reader.IsRecordConsumed, "case " + i);
                Assert.Equal(Hex(bytes), Hex(Write(w => w.WriteNetRef(back))), "re-encoding is identical, case " + i);
            }
        }

        [Test]
        public void ReferencesRespectTheBlockBound()
        {
            // a slot reference whose varint continues past the end of the enclosing block
            byte[] bytes = Bytes("01 00 00 00 81 01");
            CanonicalReader reader = Reader(bytes);
            reader.Begin();
            Assert.Throws<DecodeException>(() => reader.ReadNetRef());
        }

        // ------------------------------------------------------------------ seams

        [Test]
        public void TheWriterConsultsTheSeamForEveryEntityIncludingNull()
        {
            var entities = new FakeEntityRefs().BindSlot(3, 1, 9);
            Write(w =>
            {
                w.WriteEntity(0, 0);
                w.WriteEntity(3, 1);
                w.WriteEntityIgnoreVersion(3);
                w.WriteEntityArray(new[] { 3, 0 }, new[] { 1, 0 }, 2);
            }, entities);
            Assert.SequenceEqual(new[] { (0, 0, false), (3, 1, false), (3, 0, true), (3, 1, false), (0, 0, false) }, entities.EncodeCalls);
        }

        [Test]
        public void IndexOnlyReferencesRoundTripIncludingTheDefaultPathNode()
        {
            // PathNode.Serialize writes Write(new Entity { Index = m_SearchKey >> 32 }, ignoreVersion: true) and
            // Deserialize keeps only the index of Read(out Entity) (Game.Pathfind/PathNode.cs:133-149)
            var writerSide = new FakeEntityRefs().BindSlot(3, 1, 9);
            byte[] bytes = Write(w =>
            {
                w.WriteEntityIgnoreVersion(0);   // default PathNode: owner index 0
                w.WriteEntityIgnoreVersion(3);   // index-only lookup of a bound entity
            }, writerSide);
            Assert.Equal("00 25", Hex(bytes), "null, then slot 9");

            var readerSide = new FakeEntityRefs().BindSlot(70, 4, 9);
            CanonicalReader reader = Reader(bytes, readerSide);
            Assert.True(reader.ReadEntityIgnoreVersion(out int first));
            Assert.Equal(0, first, "a default PathNode stays default");
            Assert.True(reader.ReadEntityIgnoreVersion(out int second));
            Assert.Equal(70, second, "the reader's own entity index for slot 9");
            Assert.True(reader.IsRecordConsumed);
            Assert.Equal(2, reader.ReferenceCount);
        }

        [Test]
        public void TheReaderConsultsTheSeamForEveryReferenceInOrder()
        {
            var entities = new FakeEntityRefs().BindSlot(3, 1, 9);
            byte[] bytes = Write(w => { w.WriteEntity(0, 0); w.WriteEntity(3, 1); w.WriteEntity(77, 2); }, entities);
            CanonicalReader reader = Reader(bytes, entities);
            Assert.True(reader.ReadEntity(out _, out _));
            Assert.True(reader.ReadEntity(out int index, out int version));
            Assert.Equal(3, index);
            Assert.Equal(1, version);
            Assert.True(reader.ReadEntity(out _, out _), "a Local reference is not missing");
            Assert.SequenceEqual(new[] { NetRef.Null, NetRef.ForSlot(new NetSlot(9)), NetRef.Local }, entities.DecodeCalls);
            Assert.Equal(3, reader.ReferenceCount);
            Assert.False(reader.HadMissingReference);
            Assert.Equal(NetSlot.None, reader.FirstMissing);
        }

        [Test]
        public void MissingReferencesAreFlaggedAndTheStandInPassesThrough()
        {
            var writerSide = new FakeEntityRefs()
                .BindSlot(5, 1, 12)
                .BindSlot(6, 1, 13)
                .Bind(7, 1, NetRef.ForDerived(new DerivedKey(DerivedKeyKind.Lane, new NetSlot(40), new PrefabRank(2), 0, 0, 0, 0, 0, 0, 0)))
                .Bind(8, 1, NetRef.ForProvisional(new LocalId(4)));
            byte[] bytes = Write(w =>
            {
                w.WriteEntity(8, 1);   // provisional, unknown to the reader: missing without a slot
                w.WriteEntity(5, 1);   // slot 12, unbound on the reader: missing
                w.WriteEntity(7, 1);   // derived key, unresolved: missing, owner 40
                w.WriteEntity(6, 1);   // slot 13, bound on the reader
            }, writerSide);

            var readerSide = new FakeEntityRefs().BindSlot(60, 2, 13);
            readerSide.MissingStandIn = (99, 4);
            CanonicalReader reader = Reader(bytes, readerSide);
            Assert.False(reader.ReadEntity(out int i0, out int v0));
            Assert.Equal(99, i0);
            Assert.Equal(4, v0);
            Assert.True(reader.HadMissingReference);
            Assert.Equal(NetSlot.None, reader.FirstMissing, "the provisional reference names no slot");
            Assert.False(reader.ReadEntity(out _, out _));
            Assert.Equal(new NetSlot(12), reader.FirstMissing, "the first missing reference that names a slot");
            Assert.False(reader.ReadEntity(out _, out _));
            Assert.Equal(new NetSlot(12), reader.FirstMissing, "later missing references do not replace it");
            Assert.True(reader.ReadEntity(out int i3, out int v3));
            Assert.Equal(60, i3);
            Assert.Equal(2, v3);
            Assert.Equal(3, reader.MissingReferenceCount);
            Assert.Equal(4, reader.ReferenceCount);
            Assert.Equal(1, reader.DerivedReferenceCount);
            Assert.True(reader.IsRecordConsumed);

            // Open starts a new record: the flags and counters are reset
            reader.Open(bytes, 0, bytes.Length);
            Assert.False(reader.HadMissingReference);
            Assert.Equal(NetSlot.None, reader.FirstMissing);
            Assert.Equal(0, reader.MissingReferenceCount);
            Assert.Equal(0, reader.ReferenceCount);
            Assert.Equal(0, reader.DerivedReferenceCount);
        }

        [Test]
        public void EntityArraysReportWhetherEveryReferenceResolved()
        {
            var writerSide = new FakeEntityRefs().BindSlot(5, 1, 12).BindSlot(6, 1, 13);
            byte[] bytes = Write(w => w.WriteEntityArray(new[] { 5, 6 }, new[] { 1, 1 }, 2), writerSide);
            var readerSide = new FakeEntityRefs().BindSlot(50, 1, 13);
            CanonicalReader reader = Reader(bytes, readerSide);
            var indices = new int[2];
            var versions = new int[2];
            Assert.False(reader.ReadEntityArray(indices, versions, 2));
            Assert.Equal(50, indices[1], "elements after a missing one are still decoded");
            Assert.Equal(new NetSlot(12), reader.FirstMissing);
            Assert.Equal(1, reader.MissingReferenceCount);
        }

        // ------------------------------------------------------------------ parity with core-protocol

        /// <summary>
        /// core-protocol's <c>NetRefCodec</c> encodes record-level references (ICON, NAME) with the same table
        /// (ARCH §4.6.3 "NetRef = §4.3.3"); both encodings must be byte-identical. The codec is looked up by reflection so
        /// that this module does not depend on core-protocol; the test is skipped until it exists.
        /// </summary>
        [Test]
        public void MatchesTheProtocolNetRefCodecWhenPresent()
        {
            Type codec = typeof(CanonicalWriter).Assembly.GetType("SkylineTogether.Core.Protocol.NetRefCodec");
            if (codec == null)
            {
                Assert.Skip("core-protocol's NetRefCodec is not in this build yet");
                return;
            }
            var write = codec.GetMethod("Write", new[] { typeof(ByteWriter), typeof(NetRef).MakeByRefType() })
                        ?? codec.GetMethod("Write", new[] { typeof(ByteWriter), typeof(NetRef) });
            var read = codec.GetMethod("Read", new[] { typeof(ByteReader) });
            Assert.NotNull(write, "NetRefCodec.Write(ByteWriter, in NetRef)");
            Assert.NotNull(read, "NetRefCodec.Read(ByteReader)");

            var random = new DeterministicRandom(17);
            var corpus = new List<NetRef>
            {
                NetRef.Null, NetRef.Local, NetRef.ForSlot(new NetSlot(1)), NetRef.ForSlot(new NetSlot(NetSlot.MaxValue)),
                NetRef.ForPrefab(new PrefabRank(0)), NetRef.ForPrefab(new PrefabRank(int.MaxValue)),
                NetRef.ForProvisional(new LocalId(1)), NetRef.ForProvisional(new LocalId(uint.MaxValue)),
            };
            for (int i = 0; i < 500; i++) corpus.Add(RandomNetRef(random));

            var differences = new List<string>();
            foreach (NetRef value in corpus)
            {
                string canonical = Encode(value);
                var protocolBytes = new ByteWriter(64);
                write.Invoke(null, new object[] { protocolBytes, value });
                string protocol = Hex(protocolBytes.ToArray());
                if (canonical != protocol)
                {
                    differences.Add(value + ": canonical [" + canonical + "] protocol [" + protocol + "]");
                    continue;
                }
                byte[] bytes = Bytes(canonical);
                var decoded = (NetRef)read.Invoke(null, new object[] { new ByteReader(bytes, 0, bytes.Length) });
                if (!decoded.Equals(value)) differences.Add(value + ": the protocol codec decodes the canonical bytes to " + decoded);
                if (differences.Count >= 10) break;
            }
            if (differences.Count > 0)
            {
                Assert.Fail("core-codec and core-protocol encode NetRefs differently (ARCH §4.3.3 requires identical bytes):\n  "
                            + string.Join("\n  ", differences));
            }
        }

        // ------------------------------------------------------------------ helpers

        internal static NetRef RandomNetRef(DeterministicRandom random)
        {
            switch (random.Next(6))
            {
                case 0: return NetRef.Null;
                case 1: return NetRef.Local;
                case 2: return NetRef.ForSlot(new NetSlot(1 + (uint)random.Next((int)NetSlot.MaxValue)));
                case 3: return NetRef.ForPrefab(new PrefabRank(random.NextBool() ? random.Next(5000) : random.Next(int.MaxValue)));
                case 4: return NetRef.ForProvisional(new LocalId(random.NextBool() ? 1u + (uint)random.Next(1000) : Math.Max(1u, random.NextUInt())));
                default:
                {
                    var kind = (DerivedKeyKind)(1 + random.Next(7));
                    var owner = new NetSlot((uint)random.Next((int)NetSlot.MaxValue + 1));
                    var prefab = random.Next(4) == 0 ? PrefabRank.None : new PrefabRank(random.Next(int.MaxValue));
                    return NetRef.ForDerived(new DerivedKey(kind, owner, prefab, RandomInt(random), RandomInt(random), RandomInt(random), RandomInt(random),
                                                            RandomULong(random), RandomULong(random), RandomULong(random)));
                }
            }
        }

        private static int RandomInt(DeterministicRandom random)
        {
            switch (random.Next(5))
            {
                case 0: return -1;
                case 1: return 0;
                case 2: return random.Next(-100, 100);
                case 3: return random.NextBool() ? int.MinValue : int.MaxValue;
                default: return unchecked((int)random.NextUInt());
            }
        }

        private static ulong RandomULong(DeterministicRandom random)
        {
            switch (random.Next(4))
            {
                case 0: return 0;
                case 1: return (ulong)random.Next(300);
                case 2: return ulong.MaxValue;
                default: return random.NextULong();
            }
        }
    }
}
