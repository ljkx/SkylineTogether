using System;
using System.Collections.Generic;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>Derived keys (docs/history/ARCHITECTURE-original.md §3.7) and NetRefs (docs/history/ARCHITECTURE-original.md §4.3.3).</summary>
    public sealed class DerivedKeyAndNetRefTests
    {
        private static DerivedKey SampleKey()
        {
            return new DerivedKey(DerivedKeyKind.Lane, new NetSlot(17), new PrefabRank(4), 2, 64, -128, 3, 0x1111, 0x2222, 0x3333);
        }

        [Test]
        public void QuantizeRoundsToSixtyFourthsOfAMetre()
        {
            Assert.Equal(0, DerivedKey.Quantize(0f));
            Assert.Equal(1, DerivedKey.Quantize(1f / 64f));
            Assert.Equal(64, DerivedKey.Quantize(1f));
            Assert.Equal(-64, DerivedKey.Quantize(-1f));
            Assert.Equal(6400, DerivedKey.Quantize(100f));
            Assert.Equal(917504, DerivedKey.Quantize(14336f));
            Assert.Equal(1, DerivedKey.Quantize(0.012f));      // 0.768 units -> 1
            Assert.Equal(0, DerivedKey.Quantize(0.007f));      // 0.448 units -> 0
            Assert.Equal(-1, DerivedKey.Quantize(-0.012f));
        }

        [Test]
        public void QuantizeUsesMathRoundBankersRounding()
        {
            // exactly half a unit rounds to the even neighbour, as (int)Math.Round(metres * 64) does
            Assert.Equal(0, DerivedKey.Quantize(0.5f / 64f));
            Assert.Equal(2, DerivedKey.Quantize(1.5f / 64f));
            Assert.Equal(2, DerivedKey.Quantize(2.5f / 64f));
            Assert.Equal(4, DerivedKey.Quantize(3.5f / 64f));
            Assert.Equal(-2, DerivedKey.Quantize(-1.5f / 64f));
            Assert.Equal(0, DerivedKey.Quantize(-0.5f / 64f));
        }

        [Test]
        public void QuantizeIsDefensiveForNonFiniteInput()
        {
            Assert.Equal(0, DerivedKey.Quantize(float.NaN));
            Assert.Equal(int.MaxValue, DerivedKey.Quantize(float.PositiveInfinity));
            Assert.Equal(int.MinValue, DerivedKey.Quantize(float.NegativeInfinity));
            Assert.Equal(int.MaxValue, DerivedKey.Quantize(1e30f));
            Assert.Equal(int.MinValue, DerivedKey.Quantize(-1e30f));
        }

        [Test]
        public void DerivedKeyKindsMatchTheWireSubkinds()
        {
            Assert.Equal(1, (int)DerivedKeyKind.Lane);
            Assert.Equal(2, (int)DerivedKeyKind.SubObject);
            Assert.Equal(3, (int)DerivedKeyKind.SecondaryObject);
            Assert.Equal(4, (int)DerivedKeyKind.Block);
            Assert.Equal(5, (int)DerivedKeyKind.OwnedRoute);
            Assert.Equal(6, (int)DerivedKeyKind.FlowEdge);
            Assert.Equal(7, (int)DerivedKeyKind.OwnedRouteElement);
            Assert.False(DerivedKey.IsValidKind(0));
            Assert.False(DerivedKey.IsValidKind(8));
            for (byte k = 1; k <= 7; k++) Assert.True(DerivedKey.IsValidKind(k));
        }

        [Test]
        public void DerivedKeyConstructorStoresEveryFieldAndRejectsUndefinedKinds()
        {
            DerivedKey key = SampleKey();
            Assert.Equal(DerivedKeyKind.Lane, key.Kind);
            Assert.Equal(new NetSlot(17), key.Owner);
            Assert.Equal(new PrefabRank(4), key.Prefab);
            Assert.Equal(2, key.Index);
            Assert.Equal(64, key.QX);
            Assert.Equal(-128, key.QY);
            Assert.Equal(3, key.QZ);
            Assert.Equal(0x1111UL, key.A);
            Assert.Equal(0x2222UL, key.B);
            Assert.Equal(0x3333UL, key.C);
            Assert.Throws<ArgumentOutOfRangeException>(() => new DerivedKey(0, NetSlot.None, PrefabRank.None, -1, 0, 0, 0, 0, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new DerivedKey((DerivedKeyKind)8, NetSlot.None, PrefabRank.None, -1, 0, 0, 0, 0, 0, 0));
        }

        [Test]
        public void DerivedKeyEqualityCoversEveryField()
        {
            DerivedKey key = SampleKey();
            Assert.Equal(key, SampleKey());
            Assert.True(key == SampleKey());
            Assert.Equal(key.GetHashCode(), SampleKey().GetHashCode());
            var variants = new[]
            {
                new DerivedKey(DerivedKeyKind.SubObject, new NetSlot(17), new PrefabRank(4), 2, 64, -128, 3, 0x1111, 0x2222, 0x3333),
                new DerivedKey(DerivedKeyKind.Lane, new NetSlot(18), new PrefabRank(4), 2, 64, -128, 3, 0x1111, 0x2222, 0x3333),
                new DerivedKey(DerivedKeyKind.Lane, new NetSlot(17), new PrefabRank(5), 2, 64, -128, 3, 0x1111, 0x2222, 0x3333),
                new DerivedKey(DerivedKeyKind.Lane, new NetSlot(17), new PrefabRank(4), 3, 64, -128, 3, 0x1111, 0x2222, 0x3333),
                new DerivedKey(DerivedKeyKind.Lane, new NetSlot(17), new PrefabRank(4), 2, 65, -128, 3, 0x1111, 0x2222, 0x3333),
                new DerivedKey(DerivedKeyKind.Lane, new NetSlot(17), new PrefabRank(4), 2, 64, -127, 3, 0x1111, 0x2222, 0x3333),
                new DerivedKey(DerivedKeyKind.Lane, new NetSlot(17), new PrefabRank(4), 2, 64, -128, 4, 0x1111, 0x2222, 0x3333),
                new DerivedKey(DerivedKeyKind.Lane, new NetSlot(17), new PrefabRank(4), 2, 64, -128, 3, 0x1112, 0x2222, 0x3333),
                new DerivedKey(DerivedKeyKind.Lane, new NetSlot(17), new PrefabRank(4), 2, 64, -128, 3, 0x1111, 0x2223, 0x3333),
                new DerivedKey(DerivedKeyKind.Lane, new NetSlot(17), new PrefabRank(4), 2, 64, -128, 3, 0x1111, 0x2222, 0x3334),
            };
            var hashes = new HashSet<int>();
            foreach (DerivedKey variant in variants)
            {
                Assert.NotEqual(key, variant);
                Assert.True(key != variant);
                hashes.Add(variant.GetHashCode());
            }
            Assert.True(hashes.Count >= 9, "hash codes of single-field variants should almost never collide");
        }

        [Test]
        public void NetRefNullAndLocalAreDistinct()
        {
            Assert.Equal(NetRefKind.Null, NetRef.Null.Kind);
            Assert.Equal(NetRefKind.Local, NetRef.Local.Kind);
            Assert.True(NetRef.Null.IsNull);
            Assert.False(NetRef.Local.IsNull);
            Assert.NotEqual(NetRef.Null, NetRef.Local);
            Assert.Equal(NetRef.Null, default(NetRef));
        }

        [Test]
        public void NetRefFactoriesSetKindAndValue()
        {
            NetRef slot = NetRef.ForSlot(new NetSlot(99));
            Assert.Equal(NetRefKind.Slot, slot.Kind);
            Assert.Equal(99u, slot.Value);
            Assert.Equal(new NetSlot(99), slot.Slot);
            Assert.True(slot.Prefab.IsNone);

            NetRef prefab = NetRef.ForPrefab(new PrefabRank(0));
            Assert.Equal(NetRefKind.Prefab, prefab.Kind);
            Assert.Equal(0u, prefab.Value);
            Assert.Equal(new PrefabRank(0), prefab.Prefab);
            Assert.True(prefab.Slot.IsNone);

            NetRef provisional = NetRef.ForProvisional(new LocalId(3));
            Assert.Equal(NetRefKind.Provisional, provisional.Kind);
            Assert.Equal(new LocalId(3), provisional.Provisional);

            DerivedKey key = SampleKey();
            NetRef derived = NetRef.ForDerived(key);
            Assert.Equal(NetRefKind.Derived, derived.Kind);
            Assert.Equal(key, derived.Key);
            Assert.Equal(0u, derived.Value);
        }

        [Test]
        public void NetRefFactoriesMapNoneToNull()
        {
            Assert.Equal(NetRef.Null, NetRef.ForSlot(NetSlot.None));
            Assert.Equal(NetRef.Null, NetRef.ForPrefab(PrefabRank.None));
            Assert.Equal(NetRef.Null, NetRef.ForProvisional(LocalId.None));
            Assert.Throws<ArgumentException>(() => NetRef.ForDerived(default(DerivedKey)));
        }

        [Test]
        public void NetRefEqualityDistinguishesKindsAndKeys()
        {
            Assert.Equal(NetRef.ForSlot(new NetSlot(5)), NetRef.ForSlot(new NetSlot(5)));
            Assert.NotEqual(NetRef.ForSlot(new NetSlot(5)), NetRef.ForPrefab(new PrefabRank(5)));
            Assert.NotEqual(NetRef.ForSlot(new NetSlot(5)), NetRef.ForProvisional(new LocalId(5)));
            DerivedKey a = SampleKey();
            var b = new DerivedKey(DerivedKeyKind.Lane, new NetSlot(17), new PrefabRank(4), 2, 64, -128, 3, 0x1111, 0x2222, 0x9999);
            Assert.Equal(NetRef.ForDerived(a), NetRef.ForDerived(SampleKey()));
            Assert.NotEqual(NetRef.ForDerived(a), NetRef.ForDerived(b));
            Assert.True(NetRef.ForDerived(a) == NetRef.ForDerived(SampleKey()));
            Assert.Equal(NetRef.ForDerived(a).GetHashCode(), NetRef.ForDerived(SampleKey()).GetHashCode());
        }

        [Test]
        public void NetRefToStringNamesTheTarget()
        {
            Assert.Equal("ref:null", NetRef.Null.ToString());
            Assert.Equal("ref:local", NetRef.Local.ToString());
            Assert.Equal("ref:slot:5", NetRef.ForSlot(new NetSlot(5)).ToString());
            Assert.Equal("ref:prefab:7", NetRef.ForPrefab(new PrefabRank(7)).ToString());
            Assert.Contains("Lane", NetRef.ForDerived(SampleKey()).ToString());
        }
    }
}
