using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Protocol.ProtocolTestUtil;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>NetRefCodec: golden vectors for every kind and derived-key subkind (docs/history/ARCHITECTURE-original.md §4.3.3), strict decoding.</summary>
    public sealed class NetRefCodecTests
    {
        private static byte[] Encode(NetRef value)
        {
            var w = new ByteWriter();
            NetRefCodec.Write(w, value);
            return w.ToArray();
        }

        private static NetRef Decode(string hex)
        {
            ByteReader r = Reader(Hex(hex));
            NetRef value = NetRefCodec.Read(r);
            Assert.True(r.IsAtEnd, "the whole reference is consumed");
            return value;
        }

        internal static DerivedKey LaneKey()
        {
            // owner 300, lane prefab 12, flags 3, quantised (64, -128, 3), path nodes with easy LEB128 forms
            return new DerivedKey(DerivedKeyKind.Lane, new NetSlot(300), new PrefabRank(12), 3, 64, -128, 3, 0x80, 1, 0x4000);
        }

        internal const string LaneKeyHex = "01 ac02 0d 06 8001 ff01 06 8001 01 808001";

        internal static DerivedKey SubObjectKey() => new DerivedKey(DerivedKeyKind.SubObject, new NetSlot(9), new PrefabRank(4), 2, 0, 0, 0, 0, 0, 0);

        internal const string SubObjectKeyHex = "02 09 05 04 00 00 00 00 00 00";

        [Test]
        public void NullAndLocalAreOneByte()
        {
            AssertBytes("00", Encode(NetRef.Null));
            AssertBytes("04", Encode(NetRef.Local));
            Assert.Equal(NetRef.Null, Decode("00"));
            Assert.Equal(NetRef.Local, Decode("04"));
        }

        [Test]
        public void SlotsUseTagOne()
        {
            AssertBytes("05", Encode(NetRef.ForSlot(new NetSlot(1))));
            AssertBytes("7d", Encode(NetRef.ForSlot(new NetSlot(31))));
            AssertBytes("81 01", Encode(NetRef.ForSlot(new NetSlot(32))));
            AssertBytes("fd ff ff 1f", Encode(NetRef.ForSlot(new NetSlot(NetSlot.MaxValue))));
            Assert.Equal(NetRef.ForSlot(new NetSlot(32)), Decode("81 01"));
            Assert.Equal(NetRef.ForSlot(new NetSlot(NetSlot.MaxValue)), Decode("fd ff ff 1f"));
        }

        [Test]
        public void PrefabsUseTagTwo()
        {
            AssertBytes("02", Encode(NetRef.ForPrefab(new PrefabRank(0))));
            AssertBytes("a2 1f", Encode(NetRef.ForPrefab(new PrefabRank(1000))));
            AssertBytes("fe ff ff ff 1f", Encode(NetRef.ForPrefab(new PrefabRank(int.MaxValue))));
            Assert.Equal(NetRef.ForPrefab(new PrefabRank(1000)), Decode("a2 1f"));
            Assert.Equal(NetRef.ForPrefab(new PrefabRank(int.MaxValue)), Decode("fe ff ff ff 1f"));
        }

        [Test]
        public void ProvisionalIdsUseSubkindZero()
        {
            AssertBytes("03 00 01", Encode(NetRef.ForProvisional(new LocalId(1))));
            AssertBytes("03 00 ac 02", Encode(NetRef.ForProvisional(new LocalId(300))));
            Assert.Equal(NetRef.ForProvisional(new LocalId(300)), Decode("03 00 ac 02"));
        }

        [Test]
        public void DerivedKeysWriteEveryFieldInDeclarationOrder()
        {
            AssertBytes("03 " + LaneKeyHex, Encode(NetRef.ForDerived(LaneKey())));
            Assert.Equal(NetRef.ForDerived(LaneKey()), Decode("03 " + LaneKeyHex));
            AssertBytes("03 " + SubObjectKeyHex, Encode(NetRef.ForDerived(SubObjectKey())));

            var ownedRoute = new DerivedKey(DerivedKeyKind.OwnedRoute, new NetSlot(5), new PrefabRank(7), -1, 0, 0, 0, 0, 0, 0);
            AssertBytes("03 05 05 08 01 00 00 00 00 00 00", Encode(NetRef.ForDerived(ownedRoute)));

            var noPrefab = new DerivedKey(DerivedKeyKind.SecondaryObject, new NetSlot(1), PrefabRank.None, -1, -1, 2, 0, 0, 0, 0);
            AssertBytes("03 03 01 00 01 01 04 00 00 00 00", Encode(NetRef.ForDerived(noPrefab)));
            Assert.Equal(NetRef.ForDerived(noPrefab), Decode("03 03 01 00 01 01 04 00 00 00 00"));
        }

        [Test]
        public void EverySubkindRoundTrips()
        {
            var keys = new[]
            {
                LaneKey(),
                SubObjectKey(),
                new DerivedKey(DerivedKeyKind.SecondaryObject, new NetSlot(77), new PrefabRank(3), -1, 6400, -64, 917504, 0, 0, 0),
                new DerivedKey(DerivedKeyKind.Block, new NetSlot(10), new PrefabRank(1), 1, 128, 0, -64, 10, 6, 0),
                new DerivedKey(DerivedKeyKind.OwnedRoute, new NetSlot(12), new PrefabRank(99), -1, 0, 0, 0, 0, 0, 0),
                new DerivedKey(DerivedKeyKind.FlowEdge, new NetSlot(1), PrefabRank.None, -1, 0, 0, 0, 2, 7, 0),
                // owned-route element: a gate-bypass segment (A = 1) with index 2 (docs/history/ARCHITECTURE-original.md §3.7)
                new DerivedKey(DerivedKeyKind.OwnedRouteElement, new NetSlot(12), new PrefabRank(99), 2, 0, 0, 0, 1, 0, 0),
                new DerivedKey(DerivedKeyKind.Lane, new NetSlot(NetSlot.MaxValue), new PrefabRank(int.MaxValue), int.MinValue,
                               int.MaxValue, int.MinValue, -1, ulong.MaxValue, ulong.MaxValue - 1, 0x8000000000000000UL),
            };
            foreach (DerivedKey key in keys)
            {
                var w = new ByteWriter();
                NetRefCodec.Write(w, NetRef.ForDerived(key));
                ByteReader r = Reader(w.ToArray());
                Assert.Equal(NetRef.ForDerived(key), NetRefCodec.Read(r), key.Kind.ToString());
                Assert.True(r.IsAtEnd);

                var kw = new ByteWriter();
                NetRefCodec.WriteKey(kw, key);
                Assert.Equal((byte)key.Kind, kw.Buffer[0], "a key starts with its kind byte");
                Assert.Equal(key, NetRefCodec.ReadKey(Reader(kw.ToArray())));
            }
        }

        [Test]
        public void RandomReferencesRoundTrip()
        {
            var random = new DeterministicRandom(17);
            for (int i = 0; i < 5000; i++)
            {
                NetRef value = RandomRef(random);
                var w = new ByteWriter();
                NetRefCodec.Write(w, value);
                ByteReader r = Reader(w.ToArray());
                Assert.Equal(value, NetRefCodec.Read(r));
                Assert.True(r.IsAtEnd);
            }
        }

        internal static NetRef RandomRef(DeterministicRandom random)
        {
            switch (random.Next(6))
            {
                case 0: return NetRef.Null;
                case 1: return NetRef.Local;
                case 2: return NetRef.ForSlot(new NetSlot((uint)random.Next(1, (int)NetSlot.MaxValue + 1)));
                case 3: return NetRef.ForPrefab(new PrefabRank(random.Next(0, int.MaxValue)));
                case 4: return NetRef.ForProvisional(new LocalId(random.NextUInt() | 1));
                default:
                    var key = new DerivedKey((DerivedKeyKind)random.Next(1, 8), new NetSlot((uint)random.Next((int)NetSlot.MaxValue + 1)),
                                             new PrefabRank(random.Next(-1, 100000)), (int)random.NextUInt(), (int)random.NextUInt(),
                                             (int)random.NextUInt(), random.Next(-1000, 1000), random.NextULong(), random.NextULong() >> random.Next(64), 0);
                    return NetRef.ForDerived(key);
            }
        }

        [Test]
        public void NonCanonicalOrOutOfRangeReferencesAreRejected()
        {
            Assert.Throws<DecodeException>(() => Decode("08"), "tag 0 with payload 2");
            Assert.Throws<DecodeException>(() => Decode("01"), "slot 0 (null has its own encoding)");
            Assert.Throws<DecodeException>(() => Decode("81 80 80 20"), "slot above 24 bits");
            Assert.Throws<DecodeException>(() => Decode("82 80 80 80 20"), "prefab rank above int.MaxValue");
            Assert.Throws<DecodeException>(() => Decode("07"), "extended tag with a payload");
            Assert.Throws<DecodeException>(() => Decode("03"), "extended tag without a subkind");
            Assert.Throws<DecodeException>(() => Decode("03 08 00 00 00 00 00 00 00 00 00"), "subkind 8");
            Assert.Throws<DecodeException>(() => Decode("03 00 00"), "provisional LocalId 0");
            Assert.Throws<DecodeException>(() => Decode("80 00"), "overlong varint");
            Assert.Throws<DecodeException>(() => Decode("03 05 80 80 80 08 00 00 00 00 00 00 00 00"), "owner above 24 bits");
            Assert.Throws<DecodeException>(() => Decode("03 05 05 81 80 80 80 08 00 00 00 00 00 00 00"), "prefab rank + 1 above 2^31");
            Assert.Throws<DecodeException>(() => Decode("03 01 05 08 01 00 00 00 00 00"), "truncated key");
        }
    }
}
