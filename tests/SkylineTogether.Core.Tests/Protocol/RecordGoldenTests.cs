using System;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Protocol.ProtocolTestUtil;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>
    /// Golden byte vectors of every record kind (ARCH §4.6.3): they freeze ProtocolVersion 1. Each vector is built by
    /// hand from the documented layout (comments name the fields), then checked against the writer and decoded back.
    /// </summary>
    public sealed class RecordGoldenTests
    {
        private static byte[] Write(Action<RecordWriter> write)
        {
            var target = new ByteWriter();
            var writer = new RecordWriter(target);
            write(writer);
            Assert.Equal(1, writer.RecordCount);
            return target.ToArray();
        }

        private static RecordReader Read(string hex, RecordKind expected)
        {
            byte[] bytes = Hex(hex);
            var reader = new RecordReader();
            reader.Reset(bytes, 0, bytes.Length);
            Assert.True(reader.Next(out RecordKind kind));
            Assert.Equal(expected, kind);
            return reader;
        }

        private static void AssertEnd(RecordReader reader)
        {
            Assert.False(reader.Next(out _), "exactly one record");
        }

        [Test]
        public void TypeSetDefinition()
        {
            // 01 | typeSetId 3 | count 3 | ranks 2, 5, 300
            const string hex = "01 03 03 02 05 ac02";
            AssertBytes(hex, Write(w => w.TypeSetDef(new TypeSetId(3), new[] { new TypeRank(2), new TypeRank(5), new TypeRank(300) })));
            RecordReader r = Read(hex, RecordKind.TypeSetDef);
            TypeSetDefinition def = r.ReadTypeSetDef();
            Assert.Equal(new TypeSetId(3), def.Id);
            Assert.SequenceEqual(new[] { new TypeRank(2), new TypeRank(5), new TypeRank(300) }, def.Types);
            AssertEnd(r);
        }

        [Test]
        public void SpawnWithUpdateFrameAndComponents()
        {
            // 02 | slot 1000 | gen 2 | prefab 41+1 | typeSet 7 | flags Mover|HasUpdateFrame | updateFrame 9 | 2 components
            // | type 4, length 3, aa bb cc | type 130, length 0 | enabledBits 5
            const string hex = "02 e807 02 2a 07 03 09 02 04 03 aabbcc 8201 00 05";
            var header = new SpawnHeader
            {
                Slot = new NetSlot(1000), Generation = 2, Prefab = new PrefabRank(41), TypeSet = new TypeSetId(7),
                Flags = SpawnFlags.Mover | SpawnFlags.HasUpdateFrame, UpdateFrame = 9, ComponentCount = 2
            };
            AssertBytes(hex, Write(w =>
            {
                w.BeginSpawn(header);
                w.Component(new TypeRank(4), new byte[] { 0xAA, 0xBB, 0xCC }, 0, 3);
                w.Component(new TypeRank(130), null, 0, 0);
                w.EndSpawn(5);
            }));

            RecordReader r = Read(hex, RecordKind.Spawn);
            SpawnHeader read = r.ReadSpawnHeader();
            Assert.Equal(header.Slot, read.Slot);
            Assert.Equal(header.Generation, read.Generation);
            Assert.Equal(header.Prefab, read.Prefab);
            Assert.Equal(header.TypeSet, read.TypeSet);
            Assert.Equal(header.Flags, read.Flags);
            Assert.Equal(header.UpdateFrame, read.UpdateFrame);
            Assert.Equal(2, read.ComponentCount);
            Assert.True(r.NextComponent(out TypeRank t1, out int o1, out int c1));
            Assert.Equal(new TypeRank(4), t1);
            Assert.SequenceEqual(new byte[] { 0xAA, 0xBB, 0xCC }, Slice(r.Buffer, o1, c1));
            Assert.True(r.NextComponent(out TypeRank t2, out _, out int c2));
            Assert.Equal(new TypeRank(130), t2);
            Assert.Equal(0, c2);
            Assert.False(r.NextComponent(out _, out _, out _));
            Assert.Equal(5UL, r.ReadEnabledBits());
            AssertEnd(r);
        }

        [Test]
        public void SpawnWithoutPrefabOrUpdateFrame()
        {
            // 02 | slot 1 | gen 0 | prefab none | typeSet 1 | flags Repair | 0 components | enabledBits 0
            const string hex = "02 01 00 00 01 04 00 00";
            var header = new SpawnHeader { Slot = new NetSlot(1), Prefab = PrefabRank.None, TypeSet = new TypeSetId(1), Flags = SpawnFlags.Repair };
            AssertBytes(hex, Write(w =>
            {
                w.BeginSpawn(header);
                w.EndSpawn(0);
            }));
            RecordReader r = Read(hex, RecordKind.Spawn);
            SpawnHeader read = r.ReadSpawnHeader();
            Assert.Equal(PrefabRank.None, read.Prefab);
            Assert.Equal(SpawnFlags.Repair, read.Flags);
            Assert.Equal(0UL, r.ReadEnabledBits());
            AssertEnd(r);
        }

        [Test]
        public void Despawn()
        {
            // 03 | slot 1000 | gen 3 | flags DeleteChildrenAsHost
            const string hex = "03 e807 03 01";
            AssertBytes(hex, Write(w => w.Despawn(new DespawnRecord { Slot = new NetSlot(1000), Generation = 3, DeleteChildrenAsHost = true })));
            DespawnRecord d = Read(hex, RecordKind.Despawn).ReadDespawn();
            Assert.Equal(new NetSlot(1000), d.Slot);
            Assert.Equal((byte)3, d.Generation);
            Assert.True(d.DeleteChildrenAsHost);
        }

        [Test]
        public void Struct()
        {
            // 04 | slot 5 | new typeSet 9 | 1 added component | type 6, length 2, 01 02
            const string hex = "04 05 09 01 06 02 0102";
            AssertBytes(hex, Write(w =>
            {
                w.BeginStruct(new StructHeader { Slot = new NetSlot(5), NewTypeSet = new TypeSetId(9), AddedComponentCount = 1 });
                w.Component(new TypeRank(6), new byte[] { 1, 2 }, 0, 2);
                w.EndStruct();
            }));
            RecordReader r = Read(hex, RecordKind.Struct);
            StructHeader s = r.ReadStructHeader();
            Assert.Equal(new NetSlot(5), s.Slot);
            Assert.Equal(new TypeSetId(9), s.NewTypeSet);
            Assert.Equal(1, s.AddedComponentCount);
            Assert.True(r.NextComponent(out TypeRank type, out int o, out int c));
            Assert.Equal(new TypeRank(6), type);
            Assert.SequenceEqual(new byte[] { 1, 2 }, Slice(r.Buffer, o, c));
            Assert.False(r.NextComponent(out _, out _, out _));
            AssertEnd(r);
        }

        [Test]
        public void ValueWithEnabledBit()
        {
            // 05 | slot 70000 | type 12 | hdr HasEnabledBit|Enabled | length 4 | 01 02 03 04
            const string hex = "05 f0a204 0c 03 04 01020304";
            var header = new ValueHeader { Slot = new NetSlot(70000), Type = new TypeRank(12), Flags = ValueHeaderFlags.HasEnabledBit | ValueHeaderFlags.Enabled };
            AssertBytes(hex, Write(w => w.Value(header, new byte[] { 1, 2, 3, 4 }, 0, 4)));
            ValueHeader v = Read(hex, RecordKind.Value).ReadValue(out int o, out int c);
            Assert.Equal(header.Slot, v.Slot);
            Assert.Equal(header.Type, v.Type);
            Assert.Equal(header.Flags, v.Flags);
            Assert.Equal(4, c);
            Assert.Equal(7, o, "payload offset is absolute");
        }

        [Test]
        public void PresenceOnlyValue()
        {
            // 05 | slot 1 | type 2 | hdr PresenceOnly | length 0
            const string hex = "05 01 02 04 00";
            AssertBytes(hex, Write(w => w.Value(new ValueHeader { Slot = new NetSlot(1), Type = new TypeRank(2), Flags = ValueHeaderFlags.PresenceOnly }, null, 0, 0)));
            ValueHeader v = Read(hex, RecordKind.Value).ReadValue(out _, out int c);
            Assert.Equal(ValueHeaderFlags.PresenceOnly, v.Flags);
            Assert.Equal(0, c);
        }

        [Test]
        public void Touch()
        {
            // 06 | slot 129 | tags Updated|PathfindUpdated
            const string hex = "06 8101 05";
            AssertBytes(hex, Write(w => w.Touch(new TouchRecord { Slot = new NetSlot(129), Flags = TouchFlags.Updated | TouchFlags.PathfindUpdated })));
            TouchRecord t = Read(hex, RecordKind.Touch).ReadTouch();
            Assert.Equal(new NetSlot(129), t.Slot);
            Assert.Equal(TouchFlags.Updated | TouchFlags.PathfindUpdated, t.Flags);
        }

        [Test]
        public void BindCarriesTheOrigin()
        {
            // 07 | origin peer 3 | localId 17 | slot 1000 | gen 4
            const string hex = "07 03 11 e807 04";
            var bind = new BindRecord { Origin = new PeerId(3), Local = new LocalId(17), Slot = new NetSlot(1000), Generation = 4 };
            AssertBytes(hex, Write(w => w.Bind(bind)));
            BindRecord b = Read(hex, RecordKind.Bind).ReadBind();
            Assert.Equal(bind.Origin, b.Origin);
            Assert.Equal(bind.Local, b.Local);
            Assert.Equal(bind.Slot, b.Slot);
            Assert.Equal(bind.Generation, b.Generation);
        }

        [Test]
        public void Pose()
        {
            // 08 | slot 2 | length 2 | de ad
            const string hex = "08 02 02 dead";
            AssertBytes(hex, Write(w => w.Pose(new NetSlot(2), new byte[] { 0xDE, 0xAD }, 0, 2)));
            NetSlot slot = Read(hex, RecordKind.Pose).ReadPose(out int o, out int c);
            Assert.Equal(new NetSlot(2), slot);
            Assert.Equal(3, o);
            Assert.Equal(2, c);
        }

        [Test]
        public void KeyedAndKeyedDelete()
        {
            // 09 | sub-object key | type 33 | length 1 | ff
            string keyed = "09 " + NetRefCodecTests.SubObjectKeyHex + " 21 01 ff";
            AssertBytes(keyed, Write(w => w.Keyed(NetRefCodecTests.SubObjectKey(), new TypeRank(33), new byte[] { 0xFF }, 0, 1)));
            DerivedKey key = Read(keyed, RecordKind.Keyed).ReadKeyed(out TypeRank type, out int o, out int c, out bool isDelete);
            Assert.Equal(NetRefCodecTests.SubObjectKey(), key);
            Assert.Equal(new TypeRank(33), type);
            Assert.Equal(1, c);
            Assert.False(isDelete);

            // 0a | sub-object key
            string deleted = "0a " + NetRefCodecTests.SubObjectKeyHex;
            AssertBytes(deleted, Write(w => w.KeyedDelete(NetRefCodecTests.SubObjectKey())));
            Assert.Equal(NetRefCodecTests.SubObjectKey(), Read(deleted, RecordKind.KeyedDelete).ReadKeyed(out _, out _, out int dc, out bool del));
            Assert.True(del);
            Assert.Equal(0, dc);
        }

        [Test]
        public void Zone()
        {
            // 0b | block key (kind 4, owner 10, no prefab, side 1, q 128/0/-64, a 10, b 6, c 0) | revision 300 | length 3 | cells 4, run 4 of 5
            const string key = "04 0a 00 02 8002 00 7f 0a 06 00";
            string hex = "0b " + key + " ac02 03 040405";
            var block = new DerivedKey(DerivedKeyKind.Block, new NetSlot(10), PrefabRank.None, 1, 128, 0, -64, 10, 6, 0);
            var rle = new ByteWriter();
            ZoneOverlayCodec.Encode(new ushort[] { 5, 5, 5, 5 }, 4, rle);
            AssertBytes("04 04 05", rle);
            AssertBytes(hex, Write(w => w.Zone(block, 300, rle.Buffer, 0, rle.Length)));
            DerivedKey read = Read(hex, RecordKind.Zone).ReadZone(out ushort revision, out int o, out int c);
            Assert.Equal(block, read);
            Assert.Equal((ushort)300, revision);
            Assert.Equal(3, c);
            var cells = new ushort[60];
            Assert.Equal(4, ZoneOverlayCodec.Decode(new ByteReader(Hex(hex), o, c), cells));
            Assert.Equal((ushort)5, cells[3]);
        }

        [Test]
        public void IconWithDerivedOwnerAndTargetAndLocation()
        {
            // 0c | op Add + target + location (c0) | owner = lane key | prefab 20+1 | priority 2 | layer 1 | flags 0x0102
            // | target = sub-object key | x 1.0, y -2.5, z 1024.0
            string hex = "0c c0 03 " + NetRefCodecTests.LaneKeyHex + " 15 02 01 0201 03 " + NetRefCodecTests.SubObjectKeyHex
                         + " 0000803f 000020c0 00008044";
            var icon = new IconRecord
            {
                Op = IconOp.Add, Owner = NetRef.ForDerived(NetRefCodecTests.LaneKey()), Prefab = new PrefabRank(20), Priority = 2, Layer = 1,
                Flags = 0x0102, Target = NetRef.ForDerived(NetRefCodecTests.SubObjectKey()), HasLocation = true, X = 1f, Y = -2.5f, Z = 1024f
            };
            AssertBytes(hex, Write(w => w.Icon(icon)));
            IconRecord read = Read(hex, RecordKind.Icon).ReadIcon();
            Assert.Equal(icon.Op, read.Op);
            Assert.Equal(icon.Owner, read.Owner);
            Assert.Equal(icon.Prefab, read.Prefab);
            Assert.Equal(icon.Priority, read.Priority);
            Assert.Equal(icon.Layer, read.Layer);
            Assert.Equal(icon.Flags, read.Flags);
            Assert.Equal(icon.Target, read.Target);
            Assert.True(read.HasLocation);
            Assert.Equal(1f, read.X);
            Assert.Equal(-2.5f, read.Y);
            Assert.Equal(1024f, read.Z);
        }

        [Test]
        public void IconRemoveAllWithoutTargetOrLocation()
        {
            // 0c | op RemoveAll | owner slot 7 | prefab none | priority 0 | layer 0 | flags 0
            const string hex = "0c 02 1d 00 00 00 0000";
            var icon = new IconRecord { Op = IconOp.RemoveAll, Owner = NetRef.ForSlot(new NetSlot(7)), Prefab = PrefabRank.None };
            AssertBytes(hex, Write(w => w.Icon(icon)));
            IconRecord read = Read(hex, RecordKind.Icon).ReadIcon();
            Assert.Equal(IconOp.RemoveAll, read.Op);
            Assert.Equal(NetRef.Null, read.Target);
            Assert.False(read.HasLocation);
        }

        [Test]
        public void NameAndClear()
        {
            // 0d | target slot 7 | length 7 | "Main St"
            const string hex = "0d 1d 07 4d61696e205374";
            AssertBytes(hex, Write(w => w.Name(NetRef.ForSlot(new NetSlot(7)), "Main St")));
            Assert.Equal(NetRef.ForSlot(new NetSlot(7)), Read(hex, RecordKind.Name).ReadName(out string text));
            Assert.Equal("Main St", text);

            AssertBytes("0d 1d 00", Write(w => w.Name(NetRef.ForSlot(new NetSlot(7)), null)));
            Read("0d 1d 00", RecordKind.Name).ReadName(out string cleared);
            Assert.Equal(string.Empty, cleared);
        }

        [Test]
        public void PrefabStateSysStateEventTerrain()
        {
            AssertBytes("0e 03 010203", Write(w => w.PrefabState(new byte[] { 1, 2, 3 }, 0, 3)));
            Read("0e 03 010203", RecordKind.PrefabState).ReadPrefabState(out _, out int pc);
            Assert.Equal(3, pc);

            // 0f | entry id 0xdeadbeef (u32 LE) | length 1 | 07
            AssertBytes("0f efbeadde 01 07", Write(w => w.SysState(0xDEADBEEF, new byte[] { 7 }, 0, 1)));
            Assert.Equal(0xDEADBEEFu, Read("0f efbeadde 01 07", RecordKind.SysState).ReadSysState(out _, out int sc));
            Assert.Equal(1, sc);

            // 12 | kind MilestoneHint | length 1 | aa
            AssertBytes("12 01 01 aa", Write(w => w.Event(EventRecordKind.MilestoneHint, new byte[] { 0xAA }, 0, 1)));
            Assert.Equal(EventRecordKind.MilestoneHint, Read("12 01 01 aa", RecordKind.Event).ReadEvent(out _, out int ec));
            Assert.Equal(1, ec);

            AssertBytes("13 02 0001", Write(w => w.TerrainStamp(new byte[] { 0, 1 }, 0, 2)));
            Read("13 02 0001", RecordKind.TerrainStamp).ReadTerrainStamp(out _, out int tc);
            Assert.Equal(2, tc);
        }

        [Test]
        public void ResultsCarryTheOrigin()
        {
            // 10 | origin 2 | seq 5 | status accepted | reason 0
            AssertBytes("10 02 05 00 00", Write(w => w.Result(new ResultRecord { Origin = new PeerId(2), ProposalSeq = 5 })));
            ResultRecord accepted = Read("10 02 05 00 00", RecordKind.Result).ReadResult();
            Assert.Equal(new PeerId(2), accepted.Origin);
            Assert.Equal(5u, accepted.ProposalSeq);
            Assert.Equal(RejectReason.None, accepted.Reason);
            Assert.Equal(NetSlot.None, accepted.ConflictSlot);

            // 10 | origin 4 | seq 300 | status rejected + conflict | reason Conflict | conflict slot 1000
            var conflict = new ResultRecord { Origin = new PeerId(4), ProposalSeq = 300, Reason = RejectReason.Conflict, ConflictSlot = new NetSlot(1000) };
            AssertBytes("10 04 ac02 03 01 e807", Write(w => w.Result(conflict)));
            ResultRecord read = Read("10 04 ac02 03 01 e807", RecordKind.Result).ReadResult();
            Assert.Equal(RejectReason.Conflict, read.Reason);
            Assert.Equal(new NetSlot(1000), read.ConflictSlot);

            // 10 | origin 1 | seq 1 | status rejected | reason NotEnoughMoney
            AssertBytes("10 01 01 01 03", Write(w => w.Result(new ResultRecord { Origin = new PeerId(1), ProposalSeq = 1, Reason = RejectReason.NotEnoughMoney })));
        }

        [Test]
        public void HashBeacon()
        {
            // 11 | class S | bucket 300 | sum (u64 LE) | count 1000
            const string hex = "11 02 ac02 0807060504030201 e807";
            var beacon = new HashBeacon { Class = ReplicationClass.S, Bucket = 300, Sum = 0x0102030405060708UL, Count = 1000 };
            AssertBytes(hex, Write(w => w.Hash(beacon)));
            HashBeacon read = Read(hex, RecordKind.Hash).ReadHash();
            Assert.Equal(beacon.Class, read.Class);
            Assert.Equal(beacon.Bucket, read.Bucket);
            Assert.Equal(beacon.Sum, read.Sum);
            Assert.Equal(beacon.Count, read.Count);
        }

        [Test]
        public void TypeSetKeyframe()
        {
            // 14 | length 8 | keyframe: 2 sets | id 1, 2 types: 2, 5 | id 2, 1 type: 3
            const string hex = "14 08 02 01 02 02 05 02 01 03";
            var registry = new FakeTypeSetRegistry();
            registry.Define(new TypeSetId(1), new[] { new TypeRank(2), new TypeRank(5) });
            registry.Define(new TypeSetId(2), new[] { new TypeRank(3) });
            AssertBytes(hex, Write(w => w.TypeSetKeyframe(registry)));

            var into = new FakeTypeSetRegistry();
            RecordReader r = Read(hex, RecordKind.TypeSetKeyframe);
            r.ReadTypeSetKeyframe(into);
            Assert.Equal(2, into.Count);
            Assert.SequenceEqual(new[] { new TypeRank(2), new TypeRank(5) }, into.Get(new TypeSetId(1)));
            AssertEnd(r);
        }
    }
}
