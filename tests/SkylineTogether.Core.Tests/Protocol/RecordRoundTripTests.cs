using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Protocol.ProtocolTestUtil;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>RecordWriter/RecordReader: random sequences of every record kind round-trip; skipping; writer rules.</summary>
    public sealed class RecordRoundTripTests
    {
        /// <summary>Writes one random record and returns a checker that reads and verifies it.</summary>
        internal static Action<RecordReader> WriteRandomRecord(RecordWriter w, DeterministicRandom random)
        {
            switch (random.Next(20))
            {
                case 0:
                {
                    var types = new List<TypeRank>();
                    int next = random.Next(10);
                    int n = random.Next(65);
                    for (int i = 0; i < n && next <= ushort.MaxValue; i++)
                    {
                        types.Add(new TypeRank((ushort)next));
                        next += 1 + random.Next(1000);
                    }
                    var id = new TypeSetId(random.Next(1, int.MaxValue));
                    w.TypeSetDef(id, types);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.TypeSetDef);
                        TypeSetDefinition def = r.ReadTypeSetDef();
                        Assert.Equal(id, def.Id);
                        Assert.SequenceEqual(types, def.Types);
                    };
                }
                case 1:
                {
                    bool hasFrame = random.NextBool();
                    var header = new SpawnHeader
                    {
                        Slot = RandomSlot(random), Generation = (byte)random.Next(256), Prefab = new PrefabRank(random.Next(-1, 200000)),
                        TypeSet = new TypeSetId(random.Next(0, 100000)),
                        Flags = (random.NextBool() ? SpawnFlags.Mover : 0) | (hasFrame ? SpawnFlags.HasUpdateFrame : 0) | (random.NextBool() ? SpawnFlags.Repair : 0),
                        UpdateFrame = hasFrame ? (byte)random.Next(16) : (byte)0, ComponentCount = random.Next(6)
                    };
                    var payloads = new List<(TypeRank, byte[])>();
                    w.BeginSpawn(header);
                    for (int i = 0; i < header.ComponentCount; i++)
                    {
                        byte[] payload = RandomBytes(random, random.Next(40));
                        var type = new TypeRank((ushort)random.Next(65536));
                        payloads.Add((type, payload));
                        w.Component(type, payload, 0, payload.Length);
                    }
                    ulong bits = random.NextULong() >> random.Next(64);
                    w.EndSpawn(bits);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.Spawn);
                        SpawnHeader h = r.ReadSpawnHeader();
                        Assert.Equal(header.Slot, h.Slot);
                        Assert.Equal(header.Generation, h.Generation);
                        Assert.Equal(header.Prefab, h.Prefab);
                        Assert.Equal(header.TypeSet, h.TypeSet);
                        Assert.Equal(header.Flags, h.Flags);
                        Assert.Equal(header.UpdateFrame, h.UpdateFrame);
                        Assert.Equal(header.ComponentCount, h.ComponentCount);
                        foreach ((TypeRank type, byte[] payload) in payloads)
                        {
                            Assert.True(r.NextComponent(out TypeRank t, out int o, out int c));
                            Assert.Equal(type, t);
                            Assert.SequenceEqual(payload, Slice(r.Buffer, o, c));
                        }
                        Assert.Equal(bits, r.ReadEnabledBits());
                    };
                }
                case 2:
                {
                    var record = new DespawnRecord { Slot = RandomSlot(random), Generation = (byte)random.Next(256), DeleteChildrenAsHost = random.NextBool() };
                    w.Despawn(record);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.Despawn);
                        DespawnRecord d = r.ReadDespawn();
                        Assert.Equal(record.Slot, d.Slot);
                        Assert.Equal(record.Generation, d.Generation);
                        Assert.Equal(record.DeleteChildrenAsHost, d.DeleteChildrenAsHost);
                    };
                }
                case 3:
                {
                    var header = new StructHeader { Slot = RandomSlot(random), NewTypeSet = new TypeSetId(random.Next(1, 5000)), AddedComponentCount = random.Next(4) };
                    var payloads = new List<byte[]>();
                    w.BeginStruct(header);
                    for (int i = 0; i < header.AddedComponentCount; i++)
                    {
                        byte[] payload = RandomBytes(random, random.Next(20));
                        payloads.Add(payload);
                        w.Component(new TypeRank((ushort)i), payload, 0, payload.Length);
                    }
                    w.EndStruct();
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.Struct);
                        StructHeader s = r.ReadStructHeader();
                        Assert.Equal(header.Slot, s.Slot);
                        Assert.Equal(header.NewTypeSet, s.NewTypeSet);
                        for (int i = 0; i < payloads.Count; i++)
                        {
                            Assert.True(r.NextComponent(out TypeRank t, out int o, out int c));
                            Assert.Equal(new TypeRank((ushort)i), t);
                            Assert.SequenceEqual(payloads[i], Slice(r.Buffer, o, c));
                        }
                        Assert.False(r.NextComponent(out _, out _, out _));
                    };
                }
                case 4:
                {
                    bool presence = random.Next(4) == 0;
                    ValueHeaderFlags flags = presence ? ValueHeaderFlags.PresenceOnly : 0;
                    if (random.NextBool()) flags |= ValueHeaderFlags.HasEnabledBit | (random.NextBool() ? ValueHeaderFlags.Enabled : 0);
                    var header = new ValueHeader { Slot = RandomSlot(random), Type = new TypeRank((ushort)random.Next(65536)), Flags = flags };
                    byte[] payload = presence ? Array.Empty<byte>() : RandomBytes(random, random.Next(300));
                    w.Value(header, payload, 0, payload.Length);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.Value);
                        ValueHeader v = r.ReadValue(out int o, out int c);
                        Assert.Equal(header.Slot, v.Slot);
                        Assert.Equal(header.Type, v.Type);
                        Assert.Equal(header.Flags, v.Flags);
                        Assert.SequenceEqual(payload, Slice(r.Buffer, o, c));
                    };
                }
                case 5:
                {
                    var record = new TouchRecord { Slot = RandomSlot(random), Flags = (TouchFlags)random.Next(16) };
                    w.Touch(record);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.Touch);
                        TouchRecord t = r.ReadTouch();
                        Assert.Equal(record.Slot, t.Slot);
                        Assert.Equal(record.Flags, t.Flags);
                    };
                }
                case 6:
                {
                    var record = new BindRecord { Origin = new PeerId((byte)random.Next(256)), Local = new LocalId(random.NextUInt() | 1), Slot = RandomSlot(random), Generation = (byte)random.Next(256) };
                    w.Bind(record);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.Bind);
                        BindRecord b = r.ReadBind();
                        Assert.Equal(record.Origin, b.Origin);
                        Assert.Equal(record.Local, b.Local);
                        Assert.Equal(record.Slot, b.Slot);
                        Assert.Equal(record.Generation, b.Generation);
                    };
                }
                case 7:
                {
                    NetSlot slot = RandomSlot(random);
                    byte[] payload = RandomBytes(random, random.Next(30));
                    w.Pose(slot, payload, 0, payload.Length);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.Pose);
                        Assert.Equal(slot, r.ReadPose(out int o, out int c));
                        Assert.SequenceEqual(payload, Slice(r.Buffer, o, c));
                    };
                }
                case 8:
                {
                    DerivedKey key = RandomKey(random);
                    var type = new TypeRank((ushort)random.Next(65536));
                    byte[] payload = RandomBytes(random, random.Next(50));
                    w.Keyed(key, type, payload, 0, payload.Length);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.Keyed);
                        Assert.Equal(key, r.ReadKeyed(out TypeRank t, out int o, out int c, out bool del));
                        Assert.Equal(type, t);
                        Assert.False(del);
                        Assert.SequenceEqual(payload, Slice(r.Buffer, o, c));
                    };
                }
                case 9:
                {
                    DerivedKey key = RandomKey(random);
                    w.KeyedDelete(key);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.KeyedDelete);
                        Assert.Equal(key, r.ReadKeyed(out _, out _, out _, out bool del));
                        Assert.True(del);
                    };
                }
                case 10:
                {
                    var key = new DerivedKey(DerivedKeyKind.Block, RandomSlot(random), new PrefabRank(random.Next(-1, 50)), random.Next(2), random.Next(-100000, 100000), 0,
                                             random.Next(-100000, 100000), (ulong)random.Next(1, 11), 6, 0);
                    var cells = new ushort[random.Next(61)];
                    for (int i = 0; i < cells.Length; i++) cells[i] = (ushort)random.Next(3);
                    var rle = new ByteWriter();
                    ZoneOverlayCodec.Encode(cells, cells.Length, rle);
                    var revision = (ushort)random.Next(65536);
                    w.Zone(key, revision, rle.Buffer, 0, rle.Length);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.Zone);
                        Assert.Equal(key, r.ReadZone(out ushort rev, out int o, out int c));
                        Assert.Equal(revision, rev);
                        var decoded = new ushort[64];
                        Assert.Equal(cells.Length, ZoneOverlayCodec.Decode(new ByteReader(r.Buffer, o, c), decoded));
                        for (int i = 0; i < cells.Length; i++) Assert.Equal(cells[i], decoded[i]);
                    };
                }
                case 11:
                {
                    NetRef target = NetRefCodecTests.RandomRef(random);
                    var icon = new IconRecord
                    {
                        Op = (IconOp)random.Next(4), Owner = NetRefCodecTests.RandomRef(random), Prefab = new PrefabRank(random.Next(-1, 9000)),
                        Priority = (byte)random.Next(256), Layer = (byte)random.Next(256), Flags = (ushort)random.Next(65536), Target = target,
                        HasLocation = random.NextBool()
                    };
                    if (icon.HasLocation)
                    {
                        icon.X = (float)(random.NextDouble() * 1e4);
                        icon.Y = (float)(random.NextDouble() * -1e3);
                        icon.Z = (float)random.NextDouble();
                    }
                    w.Icon(icon);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.Icon);
                        IconRecord read = r.ReadIcon();
                        Assert.Equal(icon.Op, read.Op);
                        Assert.Equal(icon.Owner, read.Owner);
                        Assert.Equal(icon.Prefab, read.Prefab);
                        Assert.Equal(icon.Priority, read.Priority);
                        Assert.Equal(icon.Layer, read.Layer);
                        Assert.Equal(icon.Flags, read.Flags);
                        Assert.Equal(icon.Target, read.Target);
                        Assert.Equal(icon.HasLocation, read.HasLocation);
                        Assert.Equal(icon.X, read.X);
                        Assert.Equal(icon.Y, read.Y);
                        Assert.Equal(icon.Z, read.Z);
                    };
                }
                case 12:
                {
                    NetRef target = NetRefCodecTests.RandomRef(random);
                    string text = RandomText(random, random.Next(65));
                    w.Name(target, text);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.Name);
                        Assert.Equal(target, r.ReadName(out string read));
                        Assert.Equal(text, read);
                    };
                }
                case 13:
                {
                    byte[] payload = RandomBytes(random, random.Next(100));
                    w.PrefabState(payload, 0, payload.Length);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.PrefabState);
                        r.ReadPrefabState(out int o, out int c);
                        Assert.SequenceEqual(payload, Slice(r.Buffer, o, c));
                    };
                }
                case 14:
                {
                    uint entry = random.NextUInt();
                    byte[] blob = RandomBytes(random, random.Next(500));
                    w.SysState(entry, blob, 0, blob.Length);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.SysState);
                        Assert.Equal(entry, r.ReadSysState(out int o, out int c));
                        Assert.SequenceEqual(blob, Slice(r.Buffer, o, c));
                    };
                }
                case 15:
                {
                    var reason = (RejectReason)random.Next(14);
                    var record = new ResultRecord
                    {
                        Origin = new PeerId((byte)random.Next(256)), ProposalSeq = random.NextUInt(), Reason = reason,
                        ConflictSlot = random.NextBool() ? RandomSlot(random) : NetSlot.None
                    };
                    w.Result(record);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.Result);
                        ResultRecord read = r.ReadResult();
                        Assert.Equal(record.Origin, read.Origin);
                        Assert.Equal(record.ProposalSeq, read.ProposalSeq);
                        Assert.Equal(record.Reason, read.Reason);
                        Assert.Equal(record.ConflictSlot, read.ConflictSlot);
                    };
                }
                case 16:
                {
                    var beacon = new HashBeacon { Class = (ReplicationClass)random.Next(11), Bucket = random.Next(Limits.MaxBucket + 1), Sum = random.NextULong(), Count = random.Next(int.MaxValue) };
                    w.Hash(beacon);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.Hash);
                        HashBeacon read = r.ReadHash();
                        Assert.Equal(beacon.Class, read.Class);
                        Assert.Equal(beacon.Bucket, read.Bucket);
                        Assert.Equal(beacon.Sum, read.Sum);
                        Assert.Equal(beacon.Count, read.Count);
                    };
                }
                case 17:
                {
                    var kind = (EventRecordKind)random.Next(6);
                    byte[] payload = RandomBytes(random, random.Next(60));
                    w.Event(kind, payload, 0, payload.Length);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.Event);
                        Assert.Equal(kind, r.ReadEvent(out int o, out int c));
                        Assert.SequenceEqual(payload, Slice(r.Buffer, o, c));
                    };
                }
                case 18:
                {
                    byte[] payload = RandomBytes(random, random.Next(120));
                    w.TerrainStamp(payload, 0, payload.Length);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.TerrainStamp);
                        r.ReadTerrainStamp(out int o, out int c);
                        Assert.SequenceEqual(payload, Slice(r.Buffer, o, c));
                    };
                }
                default:
                {
                    var registry = new FakeTypeSetRegistry();
                    int sets = random.Next(4);
                    for (int i = 0; i < sets; i++) registry.Define(new TypeSetId(i + 1), new[] { new TypeRank((ushort)i), new TypeRank((ushort)(i + 100)) });
                    w.TypeSetKeyframe(registry);
                    return r =>
                    {
                        Assert.True(r.Next(out RecordKind k) && k == RecordKind.TypeSetKeyframe);
                        var into = new FakeTypeSetRegistry();
                        r.ReadTypeSetKeyframe(into);
                        Assert.Equal(sets, into.Count);
                    };
                }
            }
        }

        internal static NetSlot RandomSlot(DeterministicRandom random)
        {
            int bits = random.Next(1, 25);
            return new NetSlot((uint)random.Next(1, 1 << bits));
        }

        internal static DerivedKey RandomKey(DeterministicRandom random)
        {
            NetRef value;
            do value = NetRefCodecTests.RandomRef(random);
            while (value.Kind != NetRefKind.Derived);
            return value.Key;
        }

        internal static byte[] RandomBytes(DeterministicRandom random, int count)
        {
            var bytes = new byte[count];
            random.NextBytes(bytes, 0, count);
            return bytes;
        }

        private static string RandomText(DeterministicRandom random, int length)
        {
            const string alphabet = "abcXYZ 019-éßŝ中文";
            var chars = new char[length];
            for (int i = 0; i < length; i++) chars[i] = alphabet[random.Next(alphabet.Length)];
            return new string(chars);
        }

        [Test(TimeoutSeconds = 120)]
        public void RandomRecordSequencesRoundTrip()
        {
            var random = new DeterministicRandom(4242);
            for (int round = 0; round < 200; round++)
            {
                var target = new ByteWriter();
                var writer = new RecordWriter(target);
                var checks = new List<Action<RecordReader>>();
                int count = random.Next(1, 60);
                for (int i = 0; i < count; i++) checks.Add(WriteRandomRecord(writer, random));
                Assert.Equal(count, writer.RecordCount);

                // read with an offset into a larger buffer to check absolute payload offsets
                var buffer = new byte[target.Length + 10];
                System.Buffer.BlockCopy(target.Buffer, 0, buffer, 7, target.Length);
                var reader = new RecordReader();
                reader.Reset(buffer, 7, target.Length);
                foreach (Action<RecordReader> check in checks) check(reader);
                Assert.False(reader.Next(out _));
                Assert.Equal(count, reader.RecordsRead);
            }
        }

        [Test]
        public void UnreadRecordsAreSkippedWithValidation()
        {
            var random = new DeterministicRandom(99);
            for (int round = 0; round < 100; round++)
            {
                var target = new ByteWriter();
                var writer = new RecordWriter(target);
                var kinds = new List<RecordKind>();
                int count = random.Next(1, 40);
                for (int i = 0; i < count; i++) WriteRandomRecord(writer, random);
                var listing = new RecordReader();
                listing.Reset(target.Buffer, 0, target.Length);
                while (listing.Next(out RecordKind kind)) kinds.Add(kind);
                Assert.Equal(count, kinds.Count, "Next alone walks every record");

                // read only some records, and only parts of SPAWN and STRUCT records
                var partial = new RecordReader();
                partial.Reset(target.Buffer, 0, target.Length);
                int seen = 0;
                while (partial.Next(out RecordKind kind))
                {
                    Assert.Equal(kinds[seen++], kind);
                    if (random.NextBool()) continue;
                    if (kind == RecordKind.Spawn)
                    {
                        partial.ReadSpawnHeader();
                        if (random.NextBool()) partial.NextComponent(out _, out _, out _);
                    }
                    else if (kind == RecordKind.Struct)
                    {
                        partial.ReadStructHeader();
                    }
                }
                Assert.Equal(count, seen);
            }
        }

        [Test]
        public void ReadEnabledBitsSkipsUnvisitedComponents()
        {
            var target = new ByteWriter();
            var w = new RecordWriter(target);
            w.BeginSpawn(new SpawnHeader { Slot = new NetSlot(3), TypeSet = new TypeSetId(1), ComponentCount = 3 });
            for (int i = 0; i < 3; i++) w.Component(new TypeRank((ushort)i), new byte[] { (byte)i }, 0, 1);
            w.EndSpawn(0b101);
            w.Touch(new TouchRecord { Slot = new NetSlot(3), Flags = TouchFlags.Updated });
            var r = new RecordReader();
            r.Reset(target.Buffer, 0, target.Length);
            Assert.True(r.Next(out _));
            r.ReadSpawnHeader();
            Assert.Equal(0b101UL, r.ReadEnabledBits());
            Assert.True(r.Next(out RecordKind next));
            Assert.Equal(RecordKind.Touch, next);
        }

        [Test]
        public void MismatchedReadsAreProgrammingErrors()
        {
            var target = new ByteWriter();
            var w = new RecordWriter(target);
            w.Touch(new TouchRecord { Slot = new NetSlot(1), Flags = TouchFlags.Updated });
            var r = new RecordReader();
            r.Reset(target.Buffer, 0, target.Length);
            Assert.Throws<InvalidOperationException>(() => r.ReadTouch(), "no current record before Next");
            Assert.True(r.Next(out _));
            Assert.Throws<InvalidOperationException>(() => r.ReadDespawn());
            Assert.Throws<InvalidOperationException>(() => r.NextComponent(out _, out _, out _));
            Assert.Throws<InvalidOperationException>(() => r.ReadEnabledBits());
            r.ReadTouch();
            Assert.Throws<InvalidOperationException>(() => r.ReadTouch(), "a record is read once");
        }

        [Test]
        public void WriterValidatesBeforeWriting()
        {
            var target = new ByteWriter();
            var w = new RecordWriter(target);
            Assert.Throws<ArgumentException>(() => w.Touch(new TouchRecord { Slot = NetSlot.None }));
            Assert.Throws<ArgumentException>(() => w.Touch(new TouchRecord { Slot = new NetSlot(1), Flags = (TouchFlags)16 }));
            Assert.Throws<ArgumentException>(() => w.Despawn(new DespawnRecord()));
            Assert.Throws<ArgumentException>(() => w.Bind(new BindRecord { Slot = new NetSlot(1), Local = LocalId.None }));
            Assert.Throws<ArgumentException>(() => w.Value(new ValueHeader { Slot = new NetSlot(1), Flags = ValueHeaderFlags.PresenceOnly }, new byte[1], 0, 1));
            Assert.Throws<ArgumentException>(() => w.Value(new ValueHeader { Slot = new NetSlot(1), Flags = ValueHeaderFlags.Enabled }, null, 0, 0));
            Assert.Throws<ArgumentException>(() => w.Value(new ValueHeader { Slot = new NetSlot(1), Flags = (ValueHeaderFlags)8 }, null, 0, 0));
            Assert.Throws<ArgumentException>(() => w.TypeSetDef(new TypeSetId(1), new[] { new TypeRank(5), new TypeRank(5) }));
            Assert.Throws<ArgumentOutOfRangeException>(() => w.TypeSetDef(TypeSetId.None, new TypeRank[0]));
            Assert.Throws<ArgumentException>(() => w.Zone(NetRefCodecTests.SubObjectKey(), 0, null, 0, 0));
            Assert.Throws<ArgumentException>(() => w.Keyed(default(DerivedKey), new TypeRank(1), null, 0, 0));
            Assert.Throws<ArgumentException>(() => w.Hash(new HashBeacon { Class = (ReplicationClass)11 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => w.Hash(new HashBeacon { Class = ReplicationClass.S, Bucket = Limits.MaxBucket + 1 }));
            Assert.Throws<ArgumentException>(() => w.Event((EventRecordKind)6, null, 0, 0));
            Assert.Throws<ArgumentException>(() => w.Icon(new IconRecord { Op = (IconOp)4 }));
            Assert.Throws<ArgumentException>(() => w.BeginSpawn(new SpawnHeader { Slot = new NetSlot(1), UpdateFrame = 3 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => w.BeginSpawn(new SpawnHeader { Slot = new NetSlot(1), ComponentCount = 65 }));
            Assert.Equal(0, target.Length, "nothing was written by the rejected calls");
            Assert.Equal(0, w.RecordCount);
        }

        [Test]
        public void SpawnAndStructSequencingIsEnforced()
        {
            var target = new ByteWriter();
            var w = new RecordWriter(target);
            Assert.Throws<InvalidOperationException>(() => w.Component(new TypeRank(1), null, 0, 0));
            Assert.Throws<InvalidOperationException>(() => w.EndSpawn(0));
            w.BeginSpawn(new SpawnHeader { Slot = new NetSlot(1), ComponentCount = 1 });
            Assert.Throws<InvalidOperationException>(() => w.Touch(new TouchRecord { Slot = new NetSlot(1) }), "another record while SPAWN is open");
            Assert.Throws<InvalidOperationException>(() => w.EndSpawn(0), "missing component");
            Assert.Throws<InvalidOperationException>(() => w.EndStruct(), "wrong end");
            w.Component(new TypeRank(1), null, 0, 0);
            Assert.Throws<InvalidOperationException>(() => w.Component(new TypeRank(2), null, 0, 0), "one component too many");
            w.EndSpawn(0);
            Assert.Equal(1, w.RecordCount);
        }

        [Test]
        public void CancelOpenRecordRollsBack()
        {
            var target = new ByteWriter();
            var w = new RecordWriter(target);
            w.Touch(new TouchRecord { Slot = new NetSlot(1), Flags = TouchFlags.Updated });
            int before = target.Length;
            w.BeginStruct(new StructHeader { Slot = new NetSlot(2), NewTypeSet = new TypeSetId(3), AddedComponentCount = 2 });
            w.Component(new TypeRank(1), new byte[] { 1, 2, 3 }, 0, 3);
            Assert.True(w.HasOpenRecord);
            w.CancelOpenRecord();
            Assert.False(w.HasOpenRecord);
            Assert.Equal(before, target.Length);
            Assert.Equal(1, w.RecordCount);
            w.Touch(new TouchRecord { Slot = new NetSlot(1), Flags = TouchFlags.Updated });
            Assert.Equal(2, w.RecordCount);
        }

        [Test]
        public void NamesAreTruncatedAndNonFiniteIconLocationsWrittenAsZero()
        {
            var target = new ByteWriter();
            var w = new RecordWriter(target);
            w.Name(NetRef.ForSlot(new NetSlot(1)), new string('x', 100));
            w.Icon(new IconRecord { Owner = NetRef.ForSlot(new NetSlot(1)), HasLocation = true, X = float.NaN, Y = float.PositiveInfinity, Z = 3f });
            var r = new RecordReader();
            r.Reset(target.Buffer, 0, target.Length);
            Assert.True(r.Next(out _));
            r.ReadName(out string name);
            Assert.Equal(Limits.MaxNameChars, name.Length);
            Assert.True(r.Next(out _));
            IconRecord icon = r.ReadIcon();
            Assert.Equal(0f, icon.X);
            Assert.Equal(0f, icon.Y);
            Assert.Equal(3f, icon.Z);
        }

        [Test]
        public void NameTruncationKeepsSurrogatePairsWhole()
        {
            var target = new ByteWriter();
            var w = new RecordWriter(target);
            string text = new string('a', 63) + "\U0001F600";       // 63 + 2 UTF-16 units: the pair would straddle the limit
            w.Name(NetRef.Null, text);
            var r = new RecordReader();
            r.Reset(target.Buffer, 0, target.Length);
            Assert.True(r.Next(out _));
            r.ReadName(out string name);
            Assert.Equal(new string('a', 63), name);
        }

        [Test]
        public void ResetClearsTargetAndCount()
        {
            var target = new ByteWriter();
            var w = new RecordWriter(target);
            w.Touch(new TouchRecord { Slot = new NetSlot(1) });
            w.BeginSpawn(new SpawnHeader { Slot = new NetSlot(1), ComponentCount = 1 });
            w.Reset();
            Assert.Equal(0, target.Length);
            Assert.Equal(0, w.RecordCount);
            Assert.False(w.HasOpenRecord);
        }
    }
}
