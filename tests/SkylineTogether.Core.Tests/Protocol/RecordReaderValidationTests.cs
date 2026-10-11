using System;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Protocol.ProtocolTestUtil;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>RecordReader rejects every malformed or non-canonical record with DecodeException (ARCH §14.3).</summary>
    public sealed class RecordReaderValidationTests
    {
        /// <summary>Reads every record of the hex section fully, as a consumer would.</summary>
        internal static void ReadAll(byte[] bytes, int offset, int count, ITypeSetRegistry registry)
        {
            var r = new RecordReader();
            r.Reset(bytes, offset, count);
            while (r.Next(out RecordKind kind))
            {
                switch (kind)
                {
                    case RecordKind.TypeSetDef: r.ReadTypeSetDef(); break;
                    case RecordKind.Spawn:
                        r.ReadSpawnHeader();
                        while (r.NextComponent(out _, out _, out _)) { }
                        r.ReadEnabledBits();
                        break;
                    case RecordKind.Despawn: r.ReadDespawn(); break;
                    case RecordKind.Struct:
                        r.ReadStructHeader();
                        while (r.NextComponent(out _, out _, out _)) { }
                        break;
                    case RecordKind.Value: r.ReadValue(out _, out _); break;
                    case RecordKind.Touch: r.ReadTouch(); break;
                    case RecordKind.Bind: r.ReadBind(); break;
                    case RecordKind.Pose: r.ReadPose(out _, out _); break;
                    case RecordKind.Keyed:
                    case RecordKind.KeyedDelete: r.ReadKeyed(out _, out _, out _, out _); break;
                    case RecordKind.Zone: r.ReadZone(out _, out _, out _); break;
                    case RecordKind.Icon: r.ReadIcon(); break;
                    case RecordKind.Name: r.ReadName(out _); break;
                    case RecordKind.PrefabState: r.ReadPrefabState(out _, out _); break;
                    case RecordKind.SysState: r.ReadSysState(out _, out _); break;
                    case RecordKind.Result: r.ReadResult(); break;
                    case RecordKind.Hash: r.ReadHash(); break;
                    case RecordKind.Event: r.ReadEvent(out _, out _); break;
                    case RecordKind.TerrainStamp: r.ReadTerrainStamp(out _, out _); break;
                    case RecordKind.TypeSetKeyframe: r.ReadTypeSetKeyframe(registry); break;
                }
            }
        }

        private static void Rejected(string hex, string why)
        {
            byte[] bytes = Hex(hex);
            Assert.Throws<DecodeException>(() => ReadAll(bytes, 0, bytes.Length, new FakeTypeSetRegistry()), why);
            // the skip path validates the same way
            Assert.Throws<DecodeException>(() =>
            {
                var r = new RecordReader();
                r.Reset(bytes, 0, bytes.Length);
                while (r.Next(out _)) { }
            }, why + " (skipped)");
        }

        [Test]
        public void UnknownRecordKindsAreRejected()
        {
            Rejected("00", "kind 0");
            Rejected("15", "kind 21");
            Rejected("ff", "kind 255");
            Rejected("06 01 01 99", "an unknown kind after a valid record");
        }

        [Test]
        public void TruncatedRecordsAreRejected()
        {
            Rejected("02 e807 02 2a 07 03 09 02 04 03 aabb", "SPAWN payload cut short");
            Rejected("02 e807 02 2a 07 03 09 02 04 03 aabbcc 8201 00", "SPAWN without enabled bits");
            Rejected("05 01 02 00 05 0102", "VALUE payload beyond the section");
            Rejected("06 01", "TOUCH without tags");
            Rejected("10 02 05 02 00", "RESULT announcing a conflict slot that is missing");
        }

        [Test]
        public void SlotsAreValidated()
        {
            Rejected("06 00 01", "slot 0");
            Rejected("06 80808008 01", "slot above 24 bits");
            Rejected("03 00 00 00", "despawn of slot 0");
            Rejected("07 01 01 00 00", "bind to slot 0");
            Rejected("10 01 01 03 01 00", "conflict slot 0");
        }

        [Test]
        public void FlagsAndEnumsAreValidated()
        {
            Rejected("06 01 10", "touch tag bit 4");
            Rejected("03 01 00 02", "despawn flag bit 1");
            Rejected("02 01 00 00 01 08 00 00", "spawn flag bit 3");
            Rejected("05 01 01 08 00", "value header bit 3");
            Rejected("05 01 01 02 00", "Enabled without HasEnabledBit");
            Rejected("05 01 01 04 01 aa", "presence-only value with a payload");
            Rejected("11 0b 00 0000000000000000 00", "replication class 11");
            Rejected("11 02 8080 01 0000000000000000 00", "bucket 16384");
            Rejected("12 06 00", "event kind 6");
            Rejected("0c 10 00 00 00 00 0000", "icon op bit 4");
            Rejected("10 01 01 04 00", "result status bit 2");
            Rejected("10 01 01 01 00", "rejected status without a reason");
            Rejected("10 01 01 00 01", "accepted status with a reason");
            Rejected("10 01 01 01 808004", "reason above 65535");
        }

        [Test]
        public void CanonicalFormsAreEnforced()
        {
            Rejected("01 00 00", "type-set id 0");
            Rejected("01 01 02 05 05", "duplicate type rank");
            Rejected("01 01 02 05 04", "descending type ranks");
            Rejected("01 01 41", "65 types in a set");
            Rejected("01 01 01 808004", "type rank above 65535");
            Rejected("0c 40 00 00 00 00 0000 00", "an ICON target written as null");
            Rejected("0b 02 09 05 04 00 00 00 00 00 00 00 00", "ZONE keyed by a sub-object key");
            Rejected("0b 04 0a 00 02 8002 00 7f 0a 06 00 808004 00", "zone revision above 65535");
            Rejected("06 8000 01", "overlong slot varint");
        }

        [Test]
        public void CountsAreCheckedAgainstLimitsAndRemainingBytes()
        {
            Rejected("02 01 00 00 01 00 41", "65 components");
            Rejected("02 01 00 00 01 00 3f 00", "63 components declared, 1 byte left");
            Rejected("04 01 01 41", "STRUCT with 65 added components");
            Rejected("01 01 20 01", "32 types declared, 1 byte left");
        }

        [Test]
        public void IconCoordinatesMustBeFinite()
        {
            Rejected("0c 80 00 00 00 00 0000 0000c07f 00000000 00000000", "NaN x");
            Rejected("0c 80 00 00 00 00 0000 00000000 0000807f 00000000", "infinite y");
        }

        [Test]
        public void AFailedDecodeAbandonsTheSection()
        {
            // an ICON whose x is NaN, then a valid TOUCH: once ReadIcon failed half-way, the reader must not re-parse the
            // rest of the ICON as a new record, whatever the consumer does with the exception
            byte[] bytes = Hex("0c 80 00 00 00 00 0000 0000c07f 00000000 00000000  06 01 01");
            var r = new RecordReader();
            r.Reset(bytes, 0, bytes.Length);
            Assert.True(r.Next(out RecordKind kind));
            Assert.Equal(RecordKind.Icon, kind);
            Assert.Throws<DecodeException>(() => r.ReadIcon());
            Assert.Throws<DecodeException>(() => r.Next(out _), "no resynchronisation inside a malformed section");
            Assert.Throws<DecodeException>(() => r.Next(out _), "still abandoned");
            r.Reset(bytes, 20, 3);
            Assert.True(r.Next(out kind));
            Assert.Equal(RecordKind.Touch, kind);
            Assert.Equal(new NetSlot(1), r.ReadTouch().Slot);
            Assert.False(r.Next(out _));

            // a SPAWN component that overruns the section, read component by component
            bytes = Hex("02 e807 02 2a 07 03 09 02 04 05 aabb");
            r.Reset(bytes, 0, bytes.Length);
            Assert.True(r.Next(out _));
            r.ReadSpawnHeader();
            Assert.Throws<DecodeException>(() => r.NextComponent(out _, out _, out _));
            Assert.Throws<DecodeException>(() => r.Next(out _));

            // an unknown kind byte is not followed by an attempt to read the next byte as a record
            bytes = Hex("99 06 01 01");
            r.Reset(bytes, 0, bytes.Length);
            Assert.Throws<DecodeException>(() => r.Next(out _));
            Assert.Throws<DecodeException>(() => r.Next(out _));
        }

        [Test]
        public void NamesAreLimitedAndSanitised()
        {
            // 65 characters are rejected
            var w = new ByteWriter();
            w.WriteByte((byte)RecordKind.Name);
            NetRefCodec.Write(w, NetRef.Null);
            w.WriteString(new string('n', 65), 1000);
            byte[] tooLong = w.ToArray();
            Assert.Throws<DecodeException>(() => ReadAll(tooLong, 0, tooLong.Length, null));

            // control characters and bidi overrides are stripped
            w.Reset();
            w.WriteByte((byte)RecordKind.Name);
            NetRefCodec.Write(w, NetRef.Null);
            w.WriteString("Ma\nin" + (char)0x202E + "\tSt\x01", 1000);
            var r = new RecordReader();
            r.Reset(w.Buffer, 0, w.Length);
            Assert.True(r.Next(out _));
            r.ReadName(out string name);
            Assert.Equal("MainSt", name);
        }

        [Test]
        public void TypeSetKeyframesMustBeConsumedExactlyAndConsistent()
        {
            // the registry reads 1 set of 1 type but the record declares one byte more (a skipped keyframe is only
            // checked for its length: its content is defined by the registry)
            byte[] trailing = Hex("14 05 01 01 01 02 aa");
            Assert.Throws<DecodeException>(() => ReadAll(trailing, 0, trailing.Length, new FakeTypeSetRegistry()), "unread keyframe bytes");
            // contradiction with a known definition
            var registry = new FakeTypeSetRegistry();
            registry.Define(new TypeSetId(1), new[] { new TypeRank(9) });
            byte[] bytes = Hex("14 04 01 01 01 02");
            Assert.Throws<DecodeException>(() => ReadAll(bytes, 0, bytes.Length, registry));
            // a registry that fails with another exception still yields a DecodeException
            Assert.Throws<DecodeException>(() => ReadAll(bytes, 0, bytes.Length, new ThrowingRegistry()));
        }

        private sealed class ThrowingRegistry : ITypeSetRegistry
        {
            public int Count => 0;
            public TypeSetId GetOrAdd(System.Collections.Generic.IReadOnlyList<TypeRank> sortedTypes, out bool added) => throw new NotSupportedException();
            public bool TryGet(TypeSetId id, out System.Collections.Generic.IReadOnlyList<TypeRank> types) => throw new NotSupportedException();
            public System.Collections.Generic.IReadOnlyList<TypeRank> Get(TypeSetId id) => throw new NotSupportedException();
            public int OrdinalOf(TypeSetId set, TypeRank type) => throw new NotSupportedException();
            public bool Define(TypeSetId id, System.Collections.Generic.IReadOnlyList<TypeRank> sortedTypes) => throw new NotSupportedException();
            public void WriteKeyframe(ByteWriter writer) => throw new NotSupportedException();
            public void ReadKeyframe(ByteReader reader) => throw new InvalidOperationException("registry bug");
            public void Clear() { }
        }

        [Test]
        public void EmptySectionHasNoRecords()
        {
            var r = new RecordReader();
            r.Reset(new byte[4], 2, 0);
            Assert.False(r.Next(out _));
        }
    }
}
