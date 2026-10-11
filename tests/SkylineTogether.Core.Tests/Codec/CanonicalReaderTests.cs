using System;
using SkylineTogether.Core.Codec;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Codec.CodecTestUtil;

namespace SkylineTogether.Core.Tests.Codec
{
    /// <summary>Bounds, strictness and block semantics of <see cref="CanonicalReader"/>.</summary>
    public sealed class CanonicalReaderTests
    {
        [Test]
        public void AReaderOpenedOnARecordNeverReadsPastIt()
        {
            // the record is the middle two bytes; the sentinels around it must stay unreachable
            byte[] buffer = { 0xAA, 0xAA, 0x01, 0x02, 0xBB, 0xBB, 0xBB, 0xBB };
            var reader = new CanonicalReader(new FakeEntityRefs());
            reader.Open(buffer, 2, 2);
            Assert.Equal(2, reader.Length);
            Assert.Throws<DecodeException>(() => reader.ReadInt(), "a 4-byte value does not fit the 2-byte record");
            Assert.Equal(0, reader.Position, "a failed read does not move the cursor");
            Assert.Equal((byte)1, reader.ReadByte());
            Assert.Equal((byte)2, reader.ReadByte());
            Assert.True(reader.IsAtEnd);
            Assert.True(reader.IsRecordConsumed);
            Assert.Throws<DecodeException>(() => reader.ReadByte());
            Assert.Throws<DecodeException>(() => reader.ReadBool());
            Assert.Throws<DecodeException>(() => reader.ReadChar());
            Assert.Throws<DecodeException>(() => reader.ReadNetRef());
            Assert.Throws<DecodeException>(() => reader.ReadEntity(out _, out _));
            Assert.Throws<DecodeException>(() => reader.Skip(1));
            Assert.Throws<DecodeException>(() => reader.Begin());
        }

        [Test]
        public void EveryMemberOverrunsWithDecodeException()
        {
            var probes = new (string name, Action<CanonicalReader> read, int bytes)[]
            {
                ("ReadShort", r => r.ReadShort(), 2), ("ReadUShort", r => r.ReadUShort(), 2),
                ("ReadInt", r => r.ReadInt(), 4), ("ReadUInt", r => r.ReadUInt(), 4), ("ReadFloat", r => r.ReadFloat(), 4),
                ("ReadLong", r => r.ReadLong(), 8), ("ReadULong", r => r.ReadULong(), 8), ("ReadDouble", r => r.ReadDouble(), 8),
                ("ReadFloat2", r => r.ReadFloat2(out _, out _), 8), ("ReadFloat3", r => r.ReadFloat3(out _, out _, out _), 12),
                ("ReadFloat4", r => r.ReadFloat4(out _, out _, out _, out _), 16), ("ReadInt2", r => r.ReadInt2(out _, out _), 8),
                ("ReadInt3", r => r.ReadInt3(out _, out _, out _), 12), ("ReadInt4", r => r.ReadInt4(out _, out _, out _, out _), 16),
                ("ReadUInt4", r => r.ReadUInt4(out _, out _, out _, out _), 16), ("ReadQuaternion", r => r.ReadQuaternion(out _, out _, out _, out _), 16),
                ("ReadColor", r => r.ReadColor(out _, out _, out _, out _), 16), ("ReadColor32", r => r.ReadColor32(out _, out _, out _, out _), 4),
                ("ReadHash128", r => r.ReadHash128(out _, out _, out _, out _), 16), ("ReadBezier4x3", r => r.ReadBezier4x3(new float[12]), 48),
                ("ReadIntArray", r => r.ReadIntArray(new int[3], 3), 12), ("ReadUShortArray", r => r.ReadUShortArray(new ushort[3], 3), 6),
                ("ReadByteArray", r => r.ReadByteArray(new byte[3], 3), 3), ("ReadByteArray/2", r => r.ReadByteArray(new byte[6], 6, 2), 6),
                ("ReadFloat2Array", r => r.ReadFloat2Array(new float[4], 2), 16), ("ReadFloat4Array", r => r.ReadFloat4Array(new float[4], 1), 16),
                ("ReadInt2Array", r => r.ReadInt2Array(new int[4], 2), 16), ("ReadCount", r => r.ReadCount(), 4),
                ("ReadString", r => r.ReadString(), 4), ("Begin", r => r.Begin(), 4),
            };
            foreach ((string name, Action<CanonicalReader> read, int bytes) in probes)
            {
                byte[] data = new byte[bytes - 1];
                CanonicalReader reader = Reader(data);
                Assert.Throws<DecodeException>(() => read(reader), name + " on " + data.Length + " bytes");
                Assert.Equal(0, reader.Position, name + ": nothing consumed by the failed read");
            }
        }

        [Test]
        public void BooleansAndBitVectorsMustBeCanonical()
        {
            Assert.Throws<DecodeException>(() => Reader("02").ReadBool());
            Assert.Throws<DecodeException>(() => Reader("ff").ReadBool());
            Assert.Throws<DecodeException>(() => Reader("04").ReadBool2(out _, out _));
            Assert.Throws<DecodeException>(() => Reader("08").ReadBool3(out _, out _, out _));
            Assert.Throws<DecodeException>(() => Reader("10").ReadBool4(out _, out _, out _, out _));
            Assert.Throws<DecodeException>(() => Reader("80").ReadBool4(out _, out _, out _, out _));
        }

        [Test]
        public void CharsMustBeInTheirShortestForm()
        {
            string[] invalid =
            {
                "80",              // a continuation byte as lead
                "bf",
                "c0 80",           // overlong NUL
                "c1 bf",           // overlong U+007F
                "e0 80 80",        // overlong NUL in three bytes
                "e0 9f bf",        // overlong U+07FF
                "c3 28",           // invalid continuation
                "e2 28 a1", "e2 82 28",
                "f0 9f 98 80",     // a 4-byte sequence is never written for a char
                "f8 88 80 80 80", "ff",
                "c3",              // truncated
                "e2 82",
            };
            foreach (string hex in invalid)
            {
                Assert.Throws<DecodeException>(() => Reader(hex).ReadChar(), hex);
            }
        }

        [Test]
        public void StringLengthsAreValidatedBeforeAllocation()
        {
            Assert.Throws<DecodeException>(() => Reader("ff ff ff ff").ReadString(), "negative length");
            Assert.Throws<DecodeException>(() => Reader("ff ff ff 7f 41").ReadString(), "int.MaxValue chars in a 5-byte record");
            Assert.Throws<DecodeException>(() => Reader("03 00 00 00 41 42").ReadString(), "three chars, two bytes");
            Assert.Throws<DecodeException>(() => Reader("02 00 00 00 41 c3").ReadString(), "truncated second char");
            // two 3-byte chars declared, the second cut off inside a block bound
            CanonicalReader reader = Reader("08 00 00 00 02 00 00 00 e2 82 ac e2 82 ac");
            reader.Begin();
            Assert.Throws<DecodeException>(() => reader.ReadString());
        }

        [Test]
        public void LongStringsRoundTrip()
        {
            var chars = new char[5000];
            for (int i = 0; i < chars.Length; i++) chars[i] = (char)(i * 13 % 0x10000);
            string text = new string(chars);
            CanonicalReader reader = Reader(Write(w => { w.WriteString(text); w.WriteString("short"); w.WriteString(text.Substring(0, 300)); }));
            Assert.Equal(text, reader.ReadString());
            Assert.Equal("short", reader.ReadString());
            Assert.Equal(text.Substring(0, 300), reader.ReadString());
            Assert.True(reader.IsRecordConsumed);
        }

        [Test]
        public void BlockSizesAreValidated()
        {
            Assert.Throws<DecodeException>(() => Reader("ff ff ff ff").Begin(), "negative size");
            Assert.Throws<DecodeException>(() => Reader("05 00 00 00 01 02 03 04").Begin(), "size beyond the record");
            // an inner block may not extend past its outer block
            CanonicalReader reader = Reader("06 00 00 00 04 00 00 00 01 02 03 04");
            reader.Begin();
            Assert.Throws<DecodeException>(() => reader.Begin());
        }

        [Test]
        public void ReadsInsideABlockAreBoundedByIt()
        {
            CanonicalReader reader = Reader("02 00 00 00 01 02 03 04 05 06");
            BlockToken block = reader.Begin(out int size);
            Assert.Equal(2, size);
            Assert.Equal(2, reader.Remaining);
            Assert.Equal(6, reader.RecordRemaining);
            Assert.Throws<DecodeException>(() => reader.ReadInt(), "an int would cross the block end");
            Assert.Equal((short)0x0201, reader.ReadShort());
            Assert.True(reader.IsAtEnd, "at the block end");
            Assert.False(reader.IsRecordConsumed);
            Assert.True(reader.End(block));
            Assert.Equal(4, reader.Remaining);
            Assert.Equal(0x06050403, reader.ReadInt());
            Assert.True(reader.IsRecordConsumed);
        }

        [Test]
        public void EndSkipsUnreadBytesLikeTheStockReader()
        {
            // BinaryReader.End seeks to the block end and returns false when the payload was not consumed
            CanonicalReader reader = Reader("09 00 00 00 aa 03 00 00 00 01 02 03 bb 7f");
            BlockToken outer = reader.Begin(out int outerSize);
            Assert.Equal(9, outerSize);
            Assert.Equal((byte)0xAA, reader.ReadByte());
            BlockToken inner = reader.Begin(out int innerSize);
            Assert.Equal(3, innerSize);
            Assert.Equal((byte)1, reader.ReadByte());
            Assert.False(reader.End(inner), "two bytes of the inner block were skipped");
            Assert.Equal((byte)0xBB, reader.ReadByte());
            Assert.True(reader.End(outer));
            Assert.Equal((byte)0x7F, reader.ReadByte());
            Assert.True(reader.IsRecordConsumed);

            // skipping a whole nested structure without reading anything
            reader = Reader("09 00 00 00 aa 03 00 00 00 01 02 03 bb 7f");
            Assert.False(reader.End(reader.Begin()));
            Assert.Equal((byte)0x7F, reader.ReadByte());
        }

        [Test]
        public void SkipIsBoundedAndRejectsNegativeSizes()
        {
            CanonicalReader reader = Reader("01 02 03 04");
            reader.Skip(0);
            reader.Skip(3);
            Assert.Equal(3, reader.Position);
            Assert.Throws<DecodeException>(() => reader.Skip(2));
            Assert.Throws<DecodeException>(() => reader.Skip(-1));
            Assert.Equal((byte)4, reader.ReadByte());
        }

        [Test]
        public void CountsAreValidatedAgainstTheRemainingBytes()
        {
            Assert.Throws<DecodeException>(() => Reader("ff ff ff ff").ReadCount(), "negative");
            Assert.Throws<DecodeException>(() => Reader("02 00 00 00 01").ReadCount(), "two elements of at least one byte, one byte left");
            Assert.Throws<DecodeException>(() => Reader("02 00 00 00 01 02 03 04 05 06 07").ReadCount(4), "two ints need 8 bytes");
            Assert.Throws<DecodeException>(() => Reader("ff ff ff 7f 00").ReadCount(), "int.MaxValue");
            Assert.Equal(2, Reader("02 00 00 00 01 02 03 04 05 06 07 08").ReadCount(4));
            Assert.Throws<ArgumentOutOfRangeException>(() => Reader("00 00 00 00").ReadCount(0));
        }

        [Test]
        public void EndMustCloseTheInnermostBlock()
        {
            // outer { inner { 01 02 } 03 04 }: the outer block ends at 12, the inner one at 10
            CanonicalReader reader = Reader("08 00 00 00 02 00 00 00 01 02 03 04");
            Assert.Throws<InvalidOperationException>(() => reader.End(new BlockToken { Position = 4 }), "no block is open");
            BlockToken outer = reader.Begin();
            BlockToken inner = reader.Begin();
            Assert.Equal(12, outer.Position);
            Assert.Equal(10, inner.Position);
            Assert.Equal(2, reader.OpenBlockCount);
            Assert.Throws<InvalidOperationException>(() => reader.End(outer), "the inner block is still open");
            Assert.Equal(2, reader.OpenBlockCount, "the refused End changed nothing");
            Assert.False(reader.End(inner), "the inner payload was skipped");
            Assert.Throws<InvalidOperationException>(() => reader.End(inner), "the inner block is closed already");
            Assert.False(reader.End(outer), "03 04 were skipped");
            Assert.Equal(0, reader.OpenBlockCount);
            Assert.True(reader.IsRecordConsumed);
        }

        [Test]
        public void BlocksEndingAtTheSameByteHaveEqualTokens()
        {
            // a reader token is the block's end (like the stock ReaderBlock), so nested blocks that end together are
            // interchangeable: closing them in either order leaves the cursor at the same place
            CanonicalReader reader = Reader("06 00 00 00 02 00 00 00 01 02");
            BlockToken outer = reader.Begin();
            BlockToken inner = reader.Begin();
            Assert.Equal(outer.Position, inner.Position);
            Assert.False(reader.End(outer), "closes the innermost block, whose payload was skipped");
            Assert.True(reader.End(inner));
            Assert.True(reader.IsRecordConsumed);
        }

        [Test]
        public void OpenValidatesItsArgumentsAndResetsState()
        {
            var reader = new CanonicalReader(new FakeEntityRefs());
            Assert.Throws<ArgumentNullException>(() => reader.Open(null, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.Open(new byte[4], -1, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.Open(new byte[4], 3, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.Open(new byte[4], 0, -1));
            Assert.Throws<ArgumentNullException>(() => new CanonicalReader(null));
            Assert.Throws<DecodeException>(() => reader.ReadByte(), "an unopened reader is empty");

            byte[] data = Bytes("04 00 00 00 01 02 03 04");
            reader.Open(data, 0, data.Length);
            reader.Begin();
            reader.ReadByte();
            reader.Open(data, 0, data.Length);
            Assert.Equal(0, reader.OpenBlockCount);
            Assert.Equal(0, reader.Position);
            Assert.Equal(8, reader.Remaining);
            reader.Open(data, 8, 0);
            Assert.True(reader.IsRecordConsumed, "an empty record");
        }

        [Test]
        public void DestinationArraysAreValidated()
        {
            CanonicalReader reader = Reader("01 00 00 00 02 00 00 00");
            Assert.Throws<ArgumentNullException>(() => reader.ReadIntArray(null, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadIntArray(new int[1], 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadIntArray(new int[1], -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadFloat2Array(new float[3], 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadFloat4Array(new float[7], 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadInt2Array(new int[3], 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadEntityArray(new int[2], new int[1], 2));
            Assert.Throws<ArgumentException>(() => reader.ReadBezier4x3(new float[11]));
            Assert.Throws<ArgumentNullException>(() => reader.ReadBezier4x3(null));
            Assert.Equal(0, reader.Position, "argument errors consume nothing");
        }

        [Test]
        public void FailedReadsLeaveTheCursorUnchanged()
        {
            // every payload follows one valid byte (aa), so a failed read must leave the cursor at 1
            var cases = new (string hex, string name, Action<CanonicalReader> read)[]
            {
                ("aa ff ff ff ff", "string with a negative length", r => r.ReadString()),
                ("aa 05 00 00 00 41 42", "string longer than the record", r => r.ReadString()),
                ("aa 03 00 00 00 41 c3 28", "string with an invalid second char", r => r.ReadString()),
                ("aa 02 00 00 00 41 e2 82", "string whose last char is cut off", r => r.ReadString()),
                ("aa 02 00 00 00 41 c0 80", "string with an overlong char", r => r.ReadString()),
                ("aa ff ff ff ff", "negative count", r => r.ReadCount()),
                ("aa 02 00 00 00 01", "count beyond the record", r => r.ReadCount()),
                ("aa 02 00 00 00 01 02 03 04", "count of ints beyond the record", r => r.ReadCount(4)),
                ("aa ff ff ff ff", "block with a negative size", r => r.Begin()),
                ("aa 05 00 00 00 01", "block beyond the record", r => r.Begin(out _)),
                ("aa 03 01 05", "truncated derived key", r => r.ReadNetRef()),
                ("aa 01", "slot 0", r => r.ReadNetRef()),
                ("aa 80 00", "overlong reference head", r => r.ReadNetRef()),
                ("aa 81", "truncated slot reference", r => r.ReadEntity(out _, out _)),
                ("aa c3 28", "invalid char", r => r.ReadChar()),
                ("aa e2 82", "truncated char", r => r.ReadChar()),
                ("aa 02", "bool 2", r => r.ReadBool()),
                ("aa 04", "bool2 with bit 2", r => r.ReadBool2(out _, out _)),
                ("aa 01 02", "skip beyond the record", r => r.Skip(3)),
                ("aa 01 02 03", "int beyond the record", r => r.ReadInt()),
            };
            foreach ((string hex, string name, Action<CanonicalReader> read) in cases)
            {
                var entities = new FakeEntityRefs();
                CanonicalReader reader = Reader(hex, entities);
                Assert.Equal((byte)0xAA, reader.ReadByte());
                Assert.Throws<DecodeException>(() => read(reader), name);
                Assert.Equal(1, reader.Position, name + ": the cursor did not move");
                Assert.Equal(0, reader.OpenBlockCount, name + ": no block was opened");
                Assert.Equal(0, reader.ReferenceCount, name + ": no reference reached the seam");
                Assert.Equal(0, entities.DecodeCalls.Count, name + ": the seam was not called");
                Assert.Equal(Bytes(hex)[1], reader.ReadByte(), name + ": the next byte is still readable");
            }
        }

        private sealed class ThrowingEntityRefs : IEntityRefReader
        {
            public int Calls;

            public (int index, int version) DecodeEntity(in NetRef value, out bool missing, out NetSlot missingSlot)
            {
                Calls++;
                throw new InvalidOperationException("seam failure for " + value);
            }
        }

        [Test]
        public void ASeamExceptionLeavesTheCursorAndCountersUnchanged()
        {
            // aa, then the slot reference 5 (one byte, 0x15 = (5 << 2) | 1)
            var seam = new ThrowingEntityRefs();
            var reader = new CanonicalReader(seam);
            reader.Open(Bytes("aa 15"), 0, 2);
            Assert.Equal((byte)0xAA, reader.ReadByte());
            Assert.Throws<InvalidOperationException>(() => reader.ReadEntity(out _, out _), "the seam's exception reaches the caller unchanged");
            Assert.Equal(1, seam.Calls);
            Assert.Equal(1, reader.Position, "the cursor did not move");
            Assert.Equal(0, reader.ReferenceCount, "the failed reference was not counted");
            Assert.False(reader.HadMissingReference);
            Assert.Equal(NetRef.ForSlot(new NetSlot(5)), reader.ReadNetRef(), "the reference is still readable");
            Assert.True(reader.IsRecordConsumed);
        }

        [Test]
        public void DecodeErrorsNameTheFailingPosition()
        {
            // a 7-byte block holds the length and "AB" but only the first byte of the third char (c3 a9 at position 10)
            CanonicalReader reader = Reader("07 00 00 00 03 00 00 00 41 42 c3 a9");
            reader.Begin();
            DecodeException error = Assert.Throws<DecodeException>(() => reader.ReadString());
            Assert.Contains("position 10", error.Message);
            Assert.Contains("enclosing block", error.Message);
            Assert.Equal(4, reader.Position);

            error = Assert.Throws<DecodeException>(() => Reader("00 00 03 01 05").Skip(6));
            Assert.Contains("position 0", error.Message);
            error = Assert.Throws<DecodeException>(() => { CanonicalReader r = Reader("00 00 01"); r.ReadShort(); r.ReadNetRef(); });
            Assert.Contains("position 2", error.Message);
        }

        [Test]
        public void CloseDropsTheBufferAndLeavesAnEmptyRecord()
        {
            byte[] data = Bytes("02 00 00 00 01 02 7f");
            var entities = new FakeEntityRefs();
            var reader = new CanonicalReader(entities);
            reader.Open(data, 0, data.Length);
            reader.Begin();
            reader.ReadByte();
            reader.Close();
            Assert.Equal(0, reader.Length);
            Assert.Equal(0, reader.Position);
            Assert.Equal(0, reader.Remaining);
            Assert.Equal(0, reader.OpenBlockCount);
            Assert.True(reader.IsAtEnd);
            Assert.True(reader.IsRecordConsumed, "an empty record is consumed");
            Assert.Throws<DecodeException>(() => reader.ReadByte(), "a closed reader has nothing to read");
            Assert.Throws<DecodeException>(() => reader.ReadEntity(out _, out _));
            Assert.Equal(0, entities.DecodeCalls.Count);
            reader.Open(data, 6, 1);
            Assert.Equal((byte)0x7F, reader.ReadByte(), "a closed reader can be opened again");
        }

        [Test]
        public void TheWholeRecordCheckSeesUnconsumedBytesAndOpenBlocks()
        {
            CanonicalReader reader = Reader("01 00 00 00 07 08");
            reader.Begin();
            reader.ReadByte();
            Assert.True(reader.IsAtEnd);
            Assert.False(reader.IsRecordConsumed, "a block is still open");
            reader.Open(Bytes("01 00 00 00 07 08"), 0, 6);
            reader.End(reader.Begin());
            Assert.False(reader.IsRecordConsumed, "one byte left");
            Assert.Equal(5, reader.Position);
        }
    }
}
