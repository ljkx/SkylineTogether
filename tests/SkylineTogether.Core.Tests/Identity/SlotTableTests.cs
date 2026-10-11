using System;
using System.Collections.Generic;
using SkylineTogether.Core.Identity;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Identity
{
    /// <summary>The slot side tables (ARCH §3.3).</summary>
    public sealed class SlotTableTests
    {
        private static NetSlot S(uint value) => new NetSlot(value);

        private static EntityKey E(int index, int version) => new EntityKey(index, version);

        [Test]
        public void BindExposesEveryField()
        {
            var table = new SlotTable();
            table.Bind(S(7), 3, E(100, 1), Family.NetEdge, new PrefabRank(42), new TypeSetId(9), spawnEmitted: true);

            Assert.True(table.TryGetSlot(E(100, 1), out NetSlot slot));
            Assert.Equal(S(7), slot);
            Assert.True(table.TryGetSlotByIndex(100, out slot));
            Assert.Equal(S(7), slot);
            Assert.True(table.TryGetEntity(S(7), out EntityKey entity));
            Assert.Equal(E(100, 1), entity);
            Assert.Equal((byte)3, table.GenerationOf(S(7)));
            Assert.Equal(Family.NetEdge, table.FamilyOf(S(7)));
            Assert.Equal(new PrefabRank(42), table.PrefabOf(S(7)));
            Assert.Equal(new TypeSetId(9), table.TypeSetOf(S(7)));
            Assert.True(table.IsSpawnEmitted(S(7)));
            Assert.Equal((ushort)0, table.RevisionOf(S(7)));
            Assert.Equal(1, table.BoundCount);
            Assert.Equal(8, table.HighWaterMark);
        }

        [Test]
        public void UnboundSlotsReturnDefaults()
        {
            var table = new SlotTable();
            Assert.Equal(1, table.HighWaterMark, "nothing bound yet");
            Assert.False(table.TryGetEntity(S(5), out EntityKey entity));
            Assert.True(entity.IsNull);
            Assert.Equal((byte)0, table.GenerationOf(S(5)));
            Assert.Equal(Family.ToolLocal, table.FamilyOf(S(5)));
            Assert.Equal(PrefabRank.None, table.PrefabOf(S(5)));
            Assert.Equal(TypeSetId.None, table.TypeSetOf(S(5)));
            Assert.False(table.IsSpawnEmitted(S(5)));
            Assert.False(table.TryGetEntity(NetSlot.None, out _));
            Assert.False(table.TryGetSlot(EntityKey.Null, out NetSlot slot));
            Assert.True(slot.IsNone);
            Assert.False(table.TryGetSlotByIndex(-3, out _));
            Assert.False(table.TryGetSlot(E(-1, 1), out _));
        }

        [Test]
        public void VersionChecksRejectStaleEntities()
        {
            var table = new SlotTable();
            table.Bind(S(5), 0, E(10, 1), Family.Object, new PrefabRank(1), TypeSetId.None, true);
            Assert.False(table.TryGetSlot(E(10, 3), out _), "same index, other version: a different entity");
            Assert.True(table.TryGetSlotByIndex(10, out NetSlot byIndex), "the index-only lookup ignores the version");
            Assert.Equal(S(5), byIndex);
        }

        [Test]
        public void EntityIndexZeroIsAValidEntity()
        {
            // the entity allocator hands out index 0 with version 1 (Unity.Entities EntityComponentStore.AllocateEntities)
            var table = new SlotTable();
            table.Bind(S(1), 0, E(0, 1), Family.Object, PrefabRank.None, TypeSetId.None, true);
            Assert.True(table.TryGetSlot(E(0, 1), out NetSlot slot));
            Assert.Equal(S(1), slot);
            Assert.False(table.TryGetSlot(EntityKey.Null, out _), "Entity.Null never resolves");
        }

        [Test]
        public void UnbindClearsBothDirectionsAndBumpsTheKeptRevision()
        {
            var table = new SlotTable();
            table.Bind(S(5), 2, E(10, 1), Family.Object, new PrefabRank(1), new TypeSetId(3), true);
            table.SetRevision(S(5), 41);
            table.Unbind(S(5));
            Assert.False(table.TryGetSlot(E(10, 1), out _));
            Assert.False(table.TryGetSlotByIndex(10, out _));
            Assert.False(table.TryGetEntity(S(5), out _));
            Assert.Equal(0, table.BoundCount);
            Assert.Equal((ushort)42, table.RevisionOf(S(5)), "despawn bumps the revision, and the slot keeps it (ARCH §3.8)");
            table.Unbind(S(5));
            Assert.Equal((ushort)42, table.RevisionOf(S(5)), "unbinding an unbound slot is a no-op");
            table.Unbind(NetSlot.None);

            // a reused slot starts from the bumped revision, so a proposal based on the old occupant is stale
            table.Bind(S(5), 3, E(11, 1), Family.Object, new PrefabRank(1), new TypeSetId(3), false);
            Assert.Equal((ushort)42, table.RevisionOf(S(5)));
            Assert.Equal(6, table.HighWaterMark, "the high-water mark never drops");
        }

        [Test]
        public void AReusedEntityIndexMovesOnlyTheIndexMapping()
        {
            var table = new SlotTable();
            table.Bind(S(5), 0, E(10, 1), Family.Object, PrefabRank.None, TypeSetId.None, true);
            // entity (10,1) was destroyed locally and index 10 now belongs to (10,3), bound to slot 7
            table.Bind(S(7), 0, E(10, 3), Family.Object, PrefabRank.None, TypeSetId.None, true);
            Assert.True(table.TryGetSlot(E(10, 3), out NetSlot slot));
            Assert.Equal(S(7), slot);
            Assert.False(table.TryGetSlot(E(10, 1), out _));
            Assert.True(table.TryGetEntity(S(5), out EntityKey stale), "the stale binding stays until the liveness check unbinds it");
            Assert.Equal(E(10, 1), stale);
            Assert.Equal(0L, table.ConflictCount);

            table.Unbind(S(5));
            Assert.True(table.TryGetSlot(E(10, 3), out slot), "unbinding the stale slot keeps the new mapping");
            Assert.Equal(S(7), slot);
        }

        [Test]
        public void BindingAnOlderVersionNeverTakesTheIndexFromTheNewerOccupant()
        {
            var log = new ListLog();
            var table = new SlotTable(1024, log);
            // index 10 now belongs to (10,5) on slot 7; a late bind of the destroyed (10,3) must not shadow it
            table.Bind(S(7), 0, E(10, 5), Family.Object, PrefabRank.None, TypeSetId.None, true);
            table.Bind(S(5), 0, E(10, 3), Family.Object, PrefabRank.None, TypeSetId.None, true);
            Assert.True(table.TryGetSlot(E(10, 5), out NetSlot slot), "the newer occupant keeps the index mapping");
            Assert.Equal(S(7), slot);
            Assert.True(table.TryGetSlotByIndex(10, out slot));
            Assert.Equal(S(7), slot);
            Assert.False(table.TryGetSlot(E(10, 3), out _));
            Assert.True(table.TryGetEntity(S(5), out EntityKey stale), "slot -> entity is recorded for the liveness check");
            Assert.Equal(E(10, 3), stale);
            Assert.Equal(1L, table.StaleBindCount);
            Assert.Equal(0L, table.ConflictCount);
            Assert.Equal(1, log.Count(LogLevel.Warn));

            // re-binding the stale slot (same entity) and unbinding it leave the newer mapping alone
            table.Bind(S(5), 0, E(10, 3), Family.Object, PrefabRank.None, TypeSetId.None, true);
            table.Unbind(S(5));
            Assert.True(table.TryGetSlot(E(10, 5), out slot));
            Assert.Equal(S(7), slot);
            Assert.Equal(1, table.BoundCount);

            table.Clear();
            Assert.Equal(0L, table.StaleBindCount);
        }

        [Test]
        public void ALiveEntityBoundToASecondSlotLeavesTheFirst()
        {
            var log = new ListLog();
            var table = new SlotTable(1024, log);
            table.Bind(S(5), 0, E(10, 1), Family.Object, PrefabRank.None, TypeSetId.None, true);
            table.Bind(S(7), 0, E(10, 1), Family.Object, PrefabRank.None, TypeSetId.None, true);
            Assert.True(table.TryGetSlot(E(10, 1), out NetSlot slot));
            Assert.Equal(S(7), slot);
            Assert.False(table.TryGetEntity(S(5), out _), "the older slot lost its binding");
            Assert.Equal(1, table.BoundCount);
            Assert.Equal(1L, table.ConflictCount);
            Assert.Equal(1, log.Count(LogLevel.Warn));
            Assert.True(log.Contains("slot:5"), "the warning names the displaced slot");
        }

        [Test]
        public void RebindingASlotReleasesItsOldEntity()
        {
            var table = new SlotTable();
            table.Bind(S(5), 0, E(10, 1), Family.Object, new PrefabRank(1), TypeSetId.None, true);
            table.Bind(S(5), 1, E(20, 1), Family.Mover, new PrefabRank(2), TypeSetId.None, true);
            Assert.False(table.TryGetSlot(E(10, 1), out _));
            Assert.True(table.TryGetSlot(E(20, 1), out NetSlot slot));
            Assert.Equal(S(5), slot);
            Assert.Equal(Family.Mover, table.FamilyOf(S(5)));
            Assert.Equal((byte)1, table.GenerationOf(S(5)));
            Assert.Equal(1, table.BoundCount);
            Assert.Equal(1L, table.RebindCount);

            // binding the same slot to the same entity again only updates its fields
            table.Bind(S(5), 1, E(20, 1), Family.Mover, new PrefabRank(3), new TypeSetId(4), true);
            Assert.Equal(new PrefabRank(3), table.PrefabOf(S(5)));
            Assert.Equal(1L, table.RebindCount);
        }

        [Test]
        public void SpawnEmittedAndPendingSpawns()
        {
            var table = new SlotTable();
            table.Bind(S(9), 0, E(1, 1), Family.Object, PrefabRank.None, TypeSetId.None, spawnEmitted: false);
            table.Bind(S(3), 0, E(2, 1), Family.Object, PrefabRank.None, TypeSetId.None, spawnEmitted: false);
            table.Bind(S(4), 0, E(3, 1), Family.Object, PrefabRank.None, TypeSetId.None, spawnEmitted: true);
            var pending = new List<NetSlot> { S(99) };
            Assert.Equal(2, table.CopyUnspawned(pending));
            Assert.SequenceEqual(new[] { S(99), S(3), S(9) }, pending, "appended in ascending order");
            Assert.Equal(2, table.UnspawnedCount);

            table.SetSpawnEmitted(S(3), true);
            Assert.True(table.IsSpawnEmitted(S(3)));
            table.Unbind(S(9));
            pending.Clear();
            Assert.Equal(0, table.CopyUnspawned(pending));

            table.SetSpawnEmitted(S(4), false);
            Assert.False(table.IsSpawnEmitted(S(4)));
            pending.Clear();
            table.CopyUnspawned(pending);
            Assert.SequenceEqual(new[] { S(4) }, pending);

            table.SetSpawnEmitted(S(50), false);
            table.SetTypeSet(S(50), new TypeSetId(1));
            Assert.Equal(1, table.CopyUnspawned(new List<NetSlot>()), "writes to unbound slots are ignored (only slot 4 is pending)");
            Assert.Equal(TypeSetId.None, table.TypeSetOf(S(50)));
            Assert.Throws<ArgumentNullException>(() => table.CopyUnspawned(null));
        }

        [Test]
        public void TypeSetAndRevisionUpdates()
        {
            var table = new SlotTable();
            table.Bind(S(2), 0, E(5, 1), Family.Area, PrefabRank.None, new TypeSetId(1), true);
            table.SetTypeSet(S(2), new TypeSetId(8));
            Assert.Equal(new TypeSetId(8), table.TypeSetOf(S(2)));
            Assert.Equal((ushort)1, table.BumpRevision(S(2)));
            Assert.Equal((ushort)2, table.BumpRevision(S(2)));
            table.SetRevision(S(2), ushort.MaxValue);
            Assert.Equal((ushort)0, table.BumpRevision(S(2)), "revisions wrap at 65536");
            Assert.Equal((ushort)0, table.BumpRevision(NetSlot.None));
            table.SetRevision(NetSlot.None, 5);
            Assert.Equal((ushort)0, table.RevisionOf(NetSlot.None));
            Assert.Equal((ushort)1, table.BumpRevision(S(3000)), "unbound slots keep a revision too");
        }

        [Test]
        public void HighSlotsAndEntityIndicesAllocateOnlyTheirPages()
        {
            var table = new SlotTable(0);
            const int maxEntityIndex = 16384 * 8192 - 1;   // the entity allocator's last index (R08 §10.1)
            table.Bind(new NetSlot(NetSlot.MaxValue), 255, E(maxEntityIndex, 7), Family.Generic, PrefabRank.None, TypeSetId.None, false);
            Assert.True(table.TryGetSlot(E(maxEntityIndex, 7), out NetSlot slot));
            Assert.Equal(new NetSlot(NetSlot.MaxValue), slot);
            Assert.Equal((int)NetSlot.MaxValue + 1, table.HighWaterMark);
            Assert.Equal(Family.Generic, table.FamilyOf(slot));
            Assert.True(table.MemoryBytes < 8L << 20, "one entity page and one slot page, got " + table.MemoryBytes + " bytes");
        }

        [Test]
        public void EnumerationIsAscendingAndDetectsModification()
        {
            var table = new SlotTable();
            uint[] slots = { 9000, 3, 77, 8192, 8191 };
            int index = 1;
            foreach (uint s in slots) table.Bind(S(s), 0, E(index++, 1), Family.Object, PrefabRank.None, TypeSetId.None, true);
            Assert.SequenceEqual(new[] { S(3), S(77), S(8191), S(8192), S(9000) }, table.EnumerateBound());
            var copy = new List<NetSlot>();
            Assert.Equal(5, table.CopyBound(copy));
            Assert.SequenceEqual(table.EnumerateBound(), copy);

            Assert.Throws<InvalidOperationException>(() =>
            {
                foreach (NetSlot s in table.EnumerateBound()) table.Unbind(s);
            });
            foreach (NetSlot s in table.EnumerateBound()) table.SetTypeSet(s, new TypeSetId(2));   // field updates are allowed
        }

        [Test]
        public void ClearForgetsEverything()
        {
            var table = new SlotTable();
            table.Bind(S(5), 1, E(10, 1), Family.Object, PrefabRank.None, TypeSetId.None, false);
            table.BumpRevision(S(5));
            table.Clear();
            Assert.Equal(0, table.BoundCount);
            Assert.Equal(1, table.HighWaterMark);
            Assert.False(table.TryGetSlot(E(10, 1), out _));
            Assert.Equal((ushort)0, table.RevisionOf(S(5)));
            Assert.Equal(0, table.CopyUnspawned(new List<NetSlot>()));
            table.Bind(S(5), 1, E(10, 1), Family.Object, PrefabRank.None, TypeSetId.None, true);
            Assert.Equal(1, table.BoundCount);
        }

        [Test]
        public void InvalidBindsThrow()
        {
            var table = new SlotTable();
            Assert.Throws<ArgumentException>(() => table.Bind(NetSlot.None, 0, E(1, 1), Family.Object, PrefabRank.None, TypeSetId.None, true));
            Assert.Throws<ArgumentException>(() => table.Bind(S(1), 0, EntityKey.Null, Family.Object, PrefabRank.None, TypeSetId.None, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.Bind(S(1), 0, E(-1, 1), Family.Object, PrefabRank.None, TypeSetId.None, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.Bind(S(1), 0, E(1, 1), (Family)200, PrefabRank.None, TypeSetId.None, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SlotTable(-1));
            Assert.Equal(0, table.BoundCount);
        }

        [Test]
        public void RandomOperationsKeepBothDirectionsConsistent()
        {
            var random = new DeterministicRandom(1234);
            var table = new SlotTable(64);
            var model = new Dictionary<uint, EntityKey>();          // slot -> entity
            var slotOf = new Dictionary<EntityKey, uint>();         // entity -> slot (one slot per live entity)
            var modelVersion = new Dictionary<int, int>();          // entity index -> current version
            for (int step = 0; step < 30000; step++)
            {
                uint slot = (uint)(1 + random.Next(random.Next(4) == 0 ? 100000 : 300));
                int op = random.Next(10);
                if (op < 6)
                {
                    int index = random.Next(random.Next(4) == 0 ? 2000000 : 500);
                    // the current entity of the index; sometimes the index is reused by a new entity (odd versions, as the ECS gives)
                    modelVersion.TryGetValue(index, out int version);
                    if (version == 0) version = 1;
                    else if (random.Next(5) == 0) version += 2;
                    modelVersion[index] = version;
                    var entity = E(index, version);
                    table.Bind(new NetSlot(slot), (byte)random.Next(256), entity, Family.Object, PrefabRank.None, TypeSetId.None, random.NextBool());
                    // model: a live entity bound elsewhere loses that binding; the slot's old entity loses its mapping
                    if (slotOf.TryGetValue(entity, out uint displaced) && displaced != slot) model.Remove(displaced);
                    if (model.TryGetValue(slot, out EntityKey old) && slotOf.TryGetValue(old, out uint owner) && owner == slot) slotOf.Remove(old);
                    model[slot] = entity;
                    slotOf[entity] = slot;
                }
                else
                {
                    table.Unbind(new NetSlot(slot));
                    if (model.TryGetValue(slot, out EntityKey old))
                    {
                        if (slotOf.TryGetValue(old, out uint owner) && owner == slot) slotOf.Remove(old);
                        model.Remove(slot);
                    }
                }
            }

            Assert.Equal(model.Count, table.BoundCount);
            foreach (KeyValuePair<uint, EntityKey> pair in model)
            {
                Assert.True(table.TryGetEntity(new NetSlot(pair.Key), out EntityKey entity));
                Assert.Equal(pair.Value, entity);
                // the newest binding of an entity index resolves; older (stale) ones may be shadowed
                if (table.TryGetSlot(entity, out NetSlot back)) Assert.Equal(pair.Key, back.Value);
            }
            var enumerated = new List<NetSlot>(table.EnumerateBound());
            Assert.Equal(model.Count, enumerated.Count);
            for (int i = 1; i < enumerated.Count; i++) Assert.True(enumerated[i - 1] < enumerated[i]);
        }
    }
}
