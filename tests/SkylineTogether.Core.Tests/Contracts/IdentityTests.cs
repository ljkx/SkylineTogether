using System;
using System.Collections.Generic;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>Value semantics and bounds of the identity primitives (ARCH §3).</summary>
    public sealed class IdentityTests
    {
        [Test]
        public void NetSlotAcceptsTheFull24BitRange()
        {
            Assert.Equal(0x00FFFFFFu, NetSlot.MaxValue);
            Assert.Equal(0x00FFFFFFu, new NetSlot(NetSlot.MaxValue).Value);
            Assert.Equal(1u, new NetSlot(1).Value);
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetSlot(0x01000000));
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetSlot(uint.MaxValue));
        }

        [Test]
        public void NetSlotNoneIsDefaultAndZero()
        {
            Assert.True(NetSlot.None.IsNone);
            Assert.True(default(NetSlot).IsNone);
            Assert.Equal(NetSlot.None, new NetSlot(0));
            Assert.False(new NetSlot(1).IsNone);
        }

        [Test]
        public void NetSlotBucketIsSlotShiftedByTen()
        {
            Assert.Equal(0, new NetSlot(0).Bucket);
            Assert.Equal(0, new NetSlot(1023).Bucket);
            Assert.Equal(1, new NetSlot(1024).Bucket);
            Assert.Equal(1, new NetSlot(2047).Bucket);
            Assert.Equal(2, new NetSlot(2048).Bucket);
            Assert.Equal(16383, new NetSlot(NetSlot.MaxValue).Bucket);
        }

        [Test]
        public void NetSlotEqualityComparisonAndHashing()
        {
            var a = new NetSlot(42);
            var b = new NetSlot(42);
            var c = new NetSlot(43);
            Assert.True(a == b);
            Assert.False(a != b);
            Assert.True(a != c);
            Assert.True(a.Equals(b));
            Assert.True(a.Equals((object)b));
            Assert.False(a.Equals((object)42u));
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.True(a.CompareTo(c) < 0);
            Assert.True(c.CompareTo(a) > 0);
            Assert.Equal(0, a.CompareTo(b));
            Assert.True(a < c && c > a && a <= b && a >= b);
            var set = new HashSet<NetSlot> { a, b, c };
            Assert.Equal(2, set.Count);
            var list = new List<NetSlot> { new NetSlot(9), new NetSlot(3), new NetSlot(7) };
            list.Sort();
            Assert.SequenceEqual(new[] { new NetSlot(3), new NetSlot(7), new NetSlot(9) }, list);
        }

        [Test]
        public void NetSlotTryCreateAndValidation()
        {
            Assert.True(NetSlot.IsValidValue(0));
            Assert.True(NetSlot.IsValidValue(NetSlot.MaxValue));
            Assert.False(NetSlot.IsValidValue(NetSlot.MaxValue + 1));
            Assert.True(NetSlot.TryCreate(77, out NetSlot ok));
            Assert.Equal(77u, ok.Value);
            Assert.False(NetSlot.TryCreate(NetSlot.MaxValue + 1, out NetSlot bad));
            Assert.True(bad.IsNone);
        }

        [Test]
        public void NetSlotToStringIsReadable()
        {
            Assert.Equal("slot:none", NetSlot.None.ToString());
            Assert.Equal("slot:1234", new NetSlot(1234).ToString());
        }

        [Test]
        public void NetIdPacksGenerationAboveTheSlot()
        {
            var id = new NetId(new NetSlot(0x123456), 0xAB);
            Assert.Equal(0xAB123456u, id.Value);
            Assert.Equal(new NetSlot(0x123456), id.Slot);
            Assert.Equal((byte)0xAB, id.Generation);
            var raw = new NetId(0x01000001);
            Assert.Equal(new NetSlot(1), raw.Slot);
            Assert.Equal((byte)1, raw.Generation);
            var max = new NetId(uint.MaxValue);
            Assert.Equal(new NetSlot(NetSlot.MaxValue), max.Slot);
            Assert.Equal((byte)255, max.Generation);
            Assert.Equal(new NetId(new NetSlot(5), 2), new NetId(0x02000005));
            Assert.NotEqual(new NetId(new NetSlot(5), 2), new NetId(new NetSlot(5), 3));
            Assert.Equal("netid:5/2", new NetId(new NetSlot(5), 2).ToString());
        }

        [Test]
        public void LocalIdNoneIsZero()
        {
            Assert.True(LocalId.None.IsNone);
            Assert.True(default(LocalId).IsNone);
            Assert.False(new LocalId(1).IsNone);
            Assert.Equal(new LocalId(9), new LocalId(9));
            Assert.True(new LocalId(9) != new LocalId(10));
        }

        [Test]
        public void PrefabRankNoneIsMinusOneAndDefaultIsRankZero()
        {
            Assert.Equal(-1, PrefabRank.None.Value);
            Assert.True(PrefabRank.None.IsNone);
            Assert.False(default(PrefabRank).IsNone, "default(PrefabRank) is rank 0, a real prefab");
            Assert.Equal(0, default(PrefabRank).Value);
            Assert.Equal(PrefabRank.None, new PrefabRank(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PrefabRank(-2));
            Assert.Equal(new PrefabRank(5), new PrefabRank(5));
            Assert.Equal("prefab:none", PrefabRank.None.ToString());
            Assert.Equal("prefab:5", new PrefabRank(5).ToString());
        }

        [Test]
        public void TypeRankComparesByValue()
        {
            Assert.True(new TypeRank(1) < new TypeRank(2));
            Assert.True(new TypeRank(2) >= new TypeRank(2));
            Assert.Equal(new TypeRank(65535), new TypeRank(ushort.MaxValue));
            Assert.True(new TypeRank(3).CompareTo(new TypeRank(1)) > 0);
            Assert.Equal(new TypeRank(7).GetHashCode(), new TypeRank(7).GetHashCode());
        }

        [Test]
        public void TypeSetIdNoneIsZero()
        {
            Assert.True(TypeSetId.None.IsNone);
            Assert.True(default(TypeSetId).IsNone);
            Assert.False(new TypeSetId(1).IsNone);
            Assert.Equal(new TypeSetId(3), new TypeSetId(3));
            Assert.True(new TypeSetId(3) != new TypeSetId(4));
        }

        [Test]
        public void EpochIdNextAndOrdering()
        {
            var e = new EpochId(10);
            Assert.Equal(new EpochId(11), e.Next());
            Assert.True(e < e.Next());
            Assert.True(e.Next() > e);
            Assert.True(e <= new EpochId(10) && e >= new EpochId(10));
            Assert.False(e < new EpochId(10));
            Assert.True(EpochId.None.IsNone);
            Assert.Equal(new EpochId(1), EpochId.None.Next());
            Assert.True(new EpochId(5).CompareTo(new EpochId(6)) < 0);
            Assert.Equal(new EpochId(0), new EpochId(uint.MaxValue).Next());   // documented unchecked increment
        }

        [Test]
        public void PeerIdHostIsZero()
        {
            Assert.True(PeerId.Host.IsHost);
            Assert.True(default(PeerId).IsHost);
            Assert.False(new PeerId(1).IsHost);
            Assert.Equal(new PeerId(4), new PeerId(4));
            Assert.Equal("peer:host", PeerId.Host.ToString());
            Assert.Equal("peer:4", new PeerId(4).ToString());
        }

        [Test]
        public void EntityKeyMirrorsEntityNull()
        {
            Assert.True(EntityKey.Null.IsNull);
            Assert.True(default(EntityKey).IsNull);
            Assert.False(new EntityKey(0, 1).IsNull);
            Assert.False(new EntityKey(1, 0).IsNull);
            Assert.Equal(new EntityKey(5, 2), new EntityKey(5, 2));
            Assert.True(new EntityKey(5, 2) != new EntityKey(5, 3), "a different version is a different entity");
            Assert.Equal("Entity.Null", EntityKey.Null.ToString());
            Assert.Equal("Entity(5:2)", new EntityKey(5, 2).ToString());
        }
    }
}
