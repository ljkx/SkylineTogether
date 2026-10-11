using System;
using System.Collections.Generic;
using System.IO;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Protocol.ProtocolTestUtil;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>
    /// EpochWriter, EpochReader and EpochStreamReader (ARCH §4.6.2): golden header and section layouts, the validation
    /// of the whole framing in <see cref="EpochReader.Open"/>, the writer's limits and parity of the stream reader.
    /// </summary>
    public sealed class EpochFramingTests
    {
        internal struct Section
        {
            public SectionType Type;
            public byte[] Body;
        }

        internal static byte[] WriteEpoch(EpochId epoch, uint hostTick, EpochFlags flags, IList<Section> sections, int partIndex = 0, int partCount = 1)
        {
            var output = new ByteWriter();
            var writer = new EpochWriter();
            writer.Begin(epoch, hostTick, flags, output, partIndex, partCount);
            foreach (Section s in sections) writer.AppendSection(s.Type, s.Body, 0, s.Body == null ? 0 : s.Body.Length);
            int length = writer.End();
            Assert.Equal(output.Length, length);
            return output.ToArray();
        }

        internal static List<Section> ReadSections(byte[] message, out EpochHeader header)
        {
            var reader = new EpochReader();
            header = reader.Open(message, 0, message.Length);
            var sections = new List<Section>();
            while (reader.NextSection(out SectionType type, out int offset, out int count))
            {
                sections.Add(new Section { Type = type, Body = Slice(message, offset, count) });
            }
            return sections;
        }

        private static void Rejected(string hex, string why)
        {
            byte[] bytes = Hex(hex);
            Assert.Throws<DecodeException>(() => new EpochReader().Open(bytes, 0, bytes.Length), why);
            Assert.Throws<DecodeException>(() =>
            {
                var stream = new EpochStreamReader();
                using (var input = new MemoryStream(bytes))
                {
                    stream.Open(input);
                    while (stream.NextSection(out _, out _)) { }
                }
            }, why + " (stream)");
        }

        [Test]
        public void SinglePartLayoutIsPinned()
        {
            // u32 epoch | u32 hostTick | u8 flags {SnapshotBoundary, ChannelKeyframe} | u8 sectionCount 2
            // | TX (01), length 2, aa bb | HASH (0a), length 0
            const string hex = "04030201 0d0c0b0a 05 02 | 01 02 aabb | 0a 00";
            byte[] message = WriteEpoch(new EpochId(0x01020304), 0x0A0B0C0D, EpochFlags.SnapshotBoundary | EpochFlags.ChannelKeyframe,
                                        new[] { new Section { Type = SectionType.Tx, Body = new byte[] { 0xAA, 0xBB } }, new Section { Type = SectionType.Hash } });
            AssertBytes(hex, message);

            List<Section> sections = ReadSections(message, out EpochHeader header);
            Assert.Equal(new EpochId(0x01020304), header.Epoch);
            Assert.Equal(0x0A0B0C0Du, header.HostTick);
            Assert.Equal(EpochFlags.SnapshotBoundary | EpochFlags.ChannelKeyframe, header.Flags);
            Assert.Equal(2, header.SectionCount);
            Assert.Equal(0, header.PartIndex);
            Assert.Equal(1, header.PartCount);
            Assert.Equal(2, sections.Count);
            Assert.Equal(SectionType.Tx, sections[0].Type);
            Assert.SequenceEqual(new byte[] { 0xAA, 0xBB }, sections[0].Body);
            Assert.Equal(SectionType.Hash, sections[1].Type);
            Assert.Equal(0, sections[1].Body.Length);
        }

        [Test]
        public void MultiPartLayoutIsPinned()
        {
            // epoch 7 | tick 100 | flags CatchUp | MultiPart (managed by the writer) | 1 section | partIndex 1, partCount 3
            // | TSET (0d), length 1, ff
            const string hex = "07000000 64000000 0a 01 01 03 | 0d 01 ff";
            byte[] message = WriteEpoch(new EpochId(7), 100, EpochFlags.CatchUp, new[] { new Section { Type = SectionType.TypeSets, Body = new byte[] { 0xFF } } }, 1, 3);
            AssertBytes(hex, message);
            ReadSections(message, out EpochHeader header);
            Assert.Equal(EpochFlags.CatchUp | EpochFlags.MultiPart, header.Flags);
            Assert.Equal(1, header.PartIndex);
            Assert.Equal(3, header.PartCount);

            // a single-part message never carries the MultiPart flag, whatever the caller passed
            byte[] single = WriteEpoch(new EpochId(7), 100, EpochFlags.CatchUp | EpochFlags.MultiPart, new Section[0]);
            AssertBytes("07000000 64000000 02 00", single);
        }

        [Test]
        public void EveryDefinedSectionTypeRoundTrips()
        {
            var sections = new List<Section>();
            for (byte t = (byte)SectionType.Tx; t <= (byte)SectionType.TypeSets; t++)
            {
                sections.Add(new Section { Type = (SectionType)t, Body = new byte[t * 3] });
            }
            byte[] message = WriteEpoch(new EpochId(1), 2, EpochFlags.None, sections);
            List<Section> read = ReadSections(message, out EpochHeader header);
            Assert.Equal(sections.Count, header.SectionCount);
            for (int i = 0; i < sections.Count; i++)
            {
                Assert.Equal(sections[i].Type, read[i].Type);
                Assert.Equal(sections[i].Body.Length, read[i].Body.Length);
            }
        }

        [Test]
        public void MalformedFramingIsRejectedByOpen()
        {
            Rejected("01000000 02000000 00", "shorter than the fixed header");
            Rejected("01000000 02000000 10 00", "unknown flag bit");
            Rejected("01000000 02000000 08 00", "MultiPart without part numbers");
            Rejected("01000000 02000000 08 00 00 01", "part count 1 with MultiPart");
            Rejected("01000000 02000000 08 00 03 03", "part index equal to the part count");
            Rejected("01000000 02000000 08 00 00 81", "truncated part count varint");
            Rejected("01000000 02000000 08 00 80 00 02", "overlong part index varint");
            Rejected("01000000 02000000 00 01", "declared section missing");
            Rejected("01000000 02000000 00 01 00 00", "section type 0");
            Rejected("01000000 02000000 00 01 0e 00", "section type 14");
            Rejected("01000000 02000000 00 01 01 05 aabb", "section body overruns the message");
            Rejected("01000000 02000000 00 01 01 01 aa bb", "trailing byte after the sections");
            Rejected("01000000 02000000 00 00 01", "trailing byte without sections");
            Rejected("01000000 02000000 00 01 01 80808080 10", "section length varint above 32 bits");
            Rejected("01000000 02000000 00 01 01 81808004", "section of 8 MiB + 1 bytes");
        }

        [Test]
        public void ReaderReportsAbsoluteOffsetsInsideLargerBuffers()
        {
            byte[] message = WriteEpoch(new EpochId(9), 1, EpochFlags.None, new[] { new Section { Type = SectionType.Values, Body = new byte[] { 1, 2, 3 } } });
            var buffer = new byte[message.Length + 20];
            Array.Copy(message, 0, buffer, 11, message.Length);
            var reader = new EpochReader();
            reader.Open(buffer, 11, message.Length);
            Assert.Equal(11 + EpochFormat.FixedHeaderBytes, reader.SectionsOffset);
            Assert.True(reader.NextSection(out SectionType type, out int offset, out int count));
            Assert.Equal(SectionType.Values, type);
            Assert.SequenceEqual(new byte[] { 1, 2, 3 }, Slice(buffer, offset, count));
            Assert.False(reader.NextSection(out _, out _, out _));
            Assert.False(reader.NextSection(out _, out _, out _), "stays at the end");
        }

        [Test]
        public void FailedOpenLeavesNoReadableSections()
        {
            var reader = new EpochReader();
            byte[] good = WriteEpoch(new EpochId(1), 1, EpochFlags.None, new[] { new Section { Type = SectionType.Tx, Body = new byte[] { 1 } } });
            reader.Open(good, 0, good.Length);
            byte[] bad = Hex("01000000 02000000 00 02 01 01 aa");
            Assert.Throws<DecodeException>(() => reader.Open(bad, 0, bad.Length));
            Assert.False(reader.NextSection(out _, out _, out _), "the previous message is forgotten");
        }

        [Test]
        public void WriterEnforcesSequencingAndArguments()
        {
            var output = new ByteWriter();
            var writer = new EpochWriter();
            Assert.Throws<InvalidOperationException>(() => writer.AppendSection(SectionType.Tx, null, 0, 0), "AppendSection before Begin");
            Assert.Throws<InvalidOperationException>(() => writer.End(), "End before Begin");
            Assert.Throws<ArgumentNullException>(() => writer.Begin(new EpochId(1), 0, EpochFlags.None, null));
            Assert.Throws<ArgumentException>(() => writer.Begin(new EpochId(1), 0, (EpochFlags)0x10, output));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.Begin(new EpochId(1), 0, EpochFlags.None, output, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.Begin(new EpochId(1), 0, EpochFlags.None, output, 2, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.Begin(new EpochId(1), 0, EpochFlags.None, output, 0, Limits.MaxEpochParts + 1));
            Assert.Equal(0, output.Length, "rejected Begin calls write nothing");

            writer.Begin(new EpochId(1), 0, EpochFlags.None, output);
            Assert.True(writer.IsOpen);
            Assert.Throws<InvalidOperationException>(() => writer.Begin(new EpochId(2), 0, EpochFlags.None, output), "nested Begin");
            Assert.Throws<ArgumentException>(() => writer.AppendSection((SectionType)0, null, 0, 0));
            Assert.Throws<ArgumentException>(() => writer.AppendSection((SectionType)14, null, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.AppendSection(SectionType.Tx, new byte[2], 1, 2));
            Assert.Equal(EpochFormat.FixedHeaderBytes, writer.Length);
            writer.End();
            Assert.False(writer.IsOpen);
            Assert.Equal(0, writer.Length);
        }

        [Test]
        public void WriterAppendsAtTheOutputPosition()
        {
            var output = new ByteWriter();
            output.WriteByte(0xEE);
            var writer = new EpochWriter();
            writer.Begin(new EpochId(3), 4, EpochFlags.None, output);
            writer.AppendSection(SectionType.Clock, new byte[] { 9 }, 0, 1);
            Assert.Equal(EpochFormat.FixedHeaderBytes + 3, writer.End());
            AssertBytes("ee 03000000 04000000 00 01 09 01 09", output);
        }

        [Test]
        public void WriterRefusesTheSectionThatWouldExceedThePartLimit()
        {
            var output = new ByteWriter(Limits.MaxUncompressedBytes + 64);
            var writer = new EpochWriter();
            writer.Begin(new EpochId(1), 0, EpochFlags.None, output);
            int room = Limits.MaxUncompressedBytes - EpochFormat.FixedHeaderBytes - EpochFormat.SectionHeaderSize(Limits.MaxUncompressedBytes);
            var body = new byte[room + 1];
            int before = output.Length;
            Assert.Throws<InvalidOperationException>(() => writer.AppendSection(SectionType.Tx, body, 0, body.Length));
            Assert.Equal(before, output.Length, "nothing written for a refused section");
            writer.AppendSection(SectionType.Tx, body, 0, room);
            Assert.Equal(Limits.MaxUncompressedBytes, writer.End(), "a message may fill the part limit exactly");
        }

        [Test]
        public void WriterHoldsAtMost255Sections()
        {
            var output = new ByteWriter();
            var writer = new EpochWriter();
            writer.Begin(new EpochId(1), 0, EpochFlags.None, output);
            for (int i = 0; i < Limits.MaxSectionsPerEpoch; i++) writer.AppendSection(SectionType.Events, null, 0, 0);
            Assert.Throws<InvalidOperationException>(() => writer.AppendSection(SectionType.Events, null, 0, 0));
            writer.End();
            byte[] message = output.ToArray();
            List<Section> sections = ReadSections(message, out EpochHeader header);
            Assert.Equal(255, header.SectionCount);
            Assert.Equal(255, sections.Count);
        }

        [Test]
        public void StreamReaderMatchesTheBufferReader()
        {
            var random = new DeterministicRandom(17);
            for (int round = 0; round < 200; round++)
            {
                var sections = new List<Section>();
                int count = random.Next(0, 20);
                for (int i = 0; i < count; i++)
                {
                    var body = new byte[random.Next(0, random.Next(4) == 0 ? 70000 : 300)];
                    random.NextBytes(body, 0, body.Length);
                    sections.Add(new Section { Type = (SectionType)random.Next(1, 14), Body = body });
                }
                bool multi = random.NextBool();
                int partCount = multi ? random.Next(2, 1000) : 1;
                int partIndex = multi ? random.Next(partCount) : 0;
                var flags = (EpochFlags)random.Next(8);
                byte[] message = WriteEpoch(new EpochId(random.NextUInt()), random.NextUInt(), flags, sections, partIndex, partCount);
                List<Section> expected = ReadSections(message, out EpochHeader header);

                bool seekable = random.NextBool();
                using (Stream input = seekable ? (Stream)new MemoryStream(message) : new NonSeekableStream(message))
                {
                    var reader = new EpochStreamReader();
                    EpochHeader streamHeader = reader.Open(input);
                    Assert.Equal(header.Epoch, streamHeader.Epoch);
                    Assert.Equal(header.HostTick, streamHeader.HostTick);
                    Assert.Equal(header.Flags, streamHeader.Flags);
                    Assert.Equal(header.SectionCount, streamHeader.SectionCount);
                    Assert.Equal(header.PartIndex, streamHeader.PartIndex);
                    Assert.Equal(header.PartCount, streamHeader.PartCount);
                    for (int i = 0; i < expected.Count; i++)
                    {
                        Assert.True(reader.NextSection(out SectionType type, out int length));
                        Assert.Equal(expected[i].Type, type);
                        Assert.Equal(expected[i].Body.Length, length);
                        if (random.Next(3) == 0) continue;                         // skipped by the next call
                        if (random.Next(3) == 0)
                        {
                            reader.SkipBody();
                            continue;
                        }
                        var body = new byte[length + 3];
                        reader.ReadBody(body, 3);
                        Assert.SequenceEqual(expected[i].Body, Slice(body, 3, length), "section " + i + " of round " + round);
                    }
                    Assert.False(reader.NextSection(out _, out _));
                }
            }
        }

        [Test]
        public void StreamReaderRejectsTruncationAndTrailingBytes()
        {
            byte[] message = WriteEpoch(new EpochId(5), 6, EpochFlags.None, new[] { new Section { Type = SectionType.Tx, Body = new byte[100] } });
            for (int cut = 0; cut < message.Length; cut++)
            {
                byte[] truncated = Slice(message, 0, cut);
                foreach (bool seekable in new[] { true, false })
                {
                    Assert.Throws<DecodeException>(() =>
                    {
                        using (Stream input = seekable ? (Stream)new MemoryStream(truncated) : new NonSeekableStream(truncated))
                        {
                            var reader = new EpochStreamReader();
                            reader.Open(input);
                            while (reader.NextSection(out _, out _)) { }
                        }
                    }, "cut at " + cut + (seekable ? "" : " (non-seekable)"));
                }
            }
            var longer = new byte[message.Length + 1];
            Array.Copy(message, longer, message.Length);
            Assert.Throws<DecodeException>(() =>
            {
                var reader = new EpochStreamReader();
                reader.Open(new MemoryStream(longer));
                while (reader.NextSection(out _, out _)) { }
            }, "trailing byte");

            // after a failure the epoch is abandoned: no section is read from a misaligned position
            byte[] badType = WriteEpoch(new EpochId(5), 6, EpochFlags.None, new[]
            {
                new Section { Type = SectionType.Tx, Body = new byte[3] },
                new Section { Type = SectionType.Values, Body = new byte[] { (byte)SectionType.Hash, 0 } }
            });
            badType[EpochFormat.FixedHeaderBytes + 5] = 0xEE;          // second section type → undefined
            var abandoned = new EpochStreamReader();
            abandoned.Open(new MemoryStream(badType));
            Assert.True(abandoned.NextSection(out _, out _));
            Assert.Throws<DecodeException>(() => abandoned.NextSection(out _, out _), "undefined section type");
            Assert.Throws<DecodeException>(() => abandoned.NextSection(out _, out _), "still abandoned");
            abandoned.Open(new MemoryStream(message));
            Assert.True(abandoned.NextSection(out SectionType type, out int length), "Open starts afresh");
            Assert.Equal(SectionType.Tx, type);
            Assert.Equal(100, length);
        }

        [Test]
        public void StreamReaderArgumentsAndSequencing()
        {
            var reader = new EpochStreamReader();
            Assert.Throws<ArgumentNullException>(() => reader.Open(null));
            Assert.Throws<InvalidOperationException>(() => reader.NextSection(out _, out _), "NextSection before Open");
            byte[] message = WriteEpoch(new EpochId(1), 1, EpochFlags.None, new[] { new Section { Type = SectionType.Tx, Body = new byte[10] } });
            reader.Open(new MemoryStream(message));
            Assert.True(reader.NextSection(out _, out int length));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadBody(new byte[length - 1], 0), "destination too small");
            Assert.Throws<ArgumentNullException>(() => reader.ReadBody(null, 0));
            reader.ReadBody(new byte[length], 0);
            Assert.False(reader.NextSection(out _, out _));
        }

        /// <summary>A read-only stream that cannot seek and returns at most 7 bytes per read (exercises partial reads).</summary>
        internal sealed class NonSeekableStream : Stream
        {
            private readonly byte[] _data;
            private int _position;

            public NonSeekableStream(byte[] data)
            {
                _data = data;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = Math.Min(Math.Min(count, 7), _data.Length - _position);
                Array.Copy(_data, _position, buffer, offset, n);
                _position += n;
                return n;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
