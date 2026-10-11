using System;
using System.Collections.Generic;
using System.IO;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Protocol.ProtocolTestUtil;
using Section = SkylineTogether.Core.Tests.Protocol.EpochFramingTests.Section;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>
    /// Epoch parts (ARCH §4.5.4, §4.6.2): the host's <see cref="EpochPartSplitter"/> never emits a message above the part
    /// size and splits the TX records at record boundaries; the client's <see cref="EpochPartAssembler"/> hands out the
    /// epoch only when every part arrived (in any order, duplicates ignored), spills large epochs to disk, reports an
    /// epoch above 256 MiB as <see cref="PartResult.Oversized"/> and never throws for bad input.
    /// </summary>
    public sealed class EpochPartTests
    {
        private static readonly EpochId Epoch = new EpochId(4711);
        private const uint Tick = 123456;

        /// <summary>One encoded section, the form <see cref="EpochPartSplitter.Split"/> takes for the non-TX sections.</summary>
        private static ArraySegment<byte> Encoded(SectionType type, byte[] body)
        {
            var w = new ByteWriter();
            w.WriteByte((byte)type);
            w.WriteVarUInt32((uint)body.Length);
            w.WriteBytes(body, 0, body.Length);
            return new ArraySegment<byte>(w.ToArray());
        }

        private static byte[] Filled(int length, byte seed)
        {
            var body = new byte[length];
            for (int i = 0; i < length; i++) body[i] = (byte)(seed + i * 7);
            return body;
        }

        /// <summary>Records of about <paramref name="totalBytes"/>: VALUE records with payloads up to <paramref name="maxPayload"/>, plus small records.</summary>
        private static byte[] BuildTransaction(long totalBytes, int maxPayload, ulong seed, out int recordCount)
        {
            var random = new DeterministicRandom(seed);
            var target = new ByteWriter((int)Math.Min(int.MaxValue / 2, totalBytes + maxPayload + 1024));
            var writer = new RecordWriter(target);
            var payload = new byte[maxPayload];
            random.NextBytes(payload, 0, payload.Length);
            uint slot = 1;
            while (target.Length < totalBytes)
            {
                if (random.Next(8) == 0)
                {
                    writer.Touch(new TouchRecord { Slot = new NetSlot(slot), Flags = TouchFlags.Updated });
                }
                else
                {
                    int length = random.Next(maxPayload + 1);
                    int start = random.Next(maxPayload - length + 1);
                    if (length > 0) payload[start] ^= (byte)slot;                   // keep the payloads distinct
                    writer.Value(new ValueHeader { Slot = new NetSlot(slot), Type = new TypeRank((ushort)(slot & 0x3FF)) }, payload, start, length);
                }
                slot = slot % NetSlot.MaxValue + 1;
            }
            recordCount = writer.RecordCount;
            return target.ToArray();
        }

        private sealed class Part
        {
            public byte[] Bytes;
            public EpochHeader Header;
        }

        private static List<Part> Split(EpochPartSplitter splitter, EpochFlags flags, byte[] tx, IReadOnlyList<ArraySegment<byte>> others)
        {
            var parts = new List<Part>();
            splitter.Split(Epoch, Tick, flags, tx, 0, tx == null ? 0 : tx.Length, others, (buffer, offset, count) =>
            {
                Assert.True(count <= splitter.MaxPartBytes, "part of " + count + " bytes exceeds " + splitter.MaxPartBytes);
                byte[] copy = Slice(buffer, offset, count);
                var reader = new EpochReader();
                parts.Add(new Part { Bytes = copy, Header = reader.Open(copy, 0, copy.Length) });
            });
            Assert.Equal(parts.Count, splitter.LastPartCount);
            return parts;
        }

        /// <summary>Concatenates the bodies of every TX section and lists the other sections of a complete epoch stream.</summary>
        private static byte[] ReadComplete(Stream complete, out EpochHeader header, List<Section> others, out int txSections)
        {
            var reader = new EpochStreamReader();
            header = reader.Open(complete);
            var tx = new MemoryStream();
            txSections = 0;
            var body = new byte[Limits.MaxUncompressedBytes];
            while (reader.NextSection(out SectionType type, out int length))
            {
                reader.ReadBody(body, 0);
                if (type == SectionType.Tx)
                {
                    // every TX section is a whole sequence of records on its own
                    RecordReaderValidationTests.ReadAll(body, 0, length, new FakeTypeSetRegistry());
                    tx.Write(body, 0, length);
                    txSections++;
                }
                else
                {
                    others.Add(new Section { Type = type, Body = Slice(body, 0, length) });
                }
            }
            return tx.ToArray();
        }

        private static PartResult Deliver(EpochPartAssembler assembler, Part part, out Stream complete)
        {
            EpochHeader header = part.Header;
            return assembler.Accept(in header, part.Bytes, 0, part.Bytes.Length, out complete);
        }

        [Test]
        public void SmallEpochIsOneOrdinaryMessageInCanonicalSectionOrder()
        {
            var splitter = new EpochPartSplitter();
            byte[] tx = BuildTransaction(2000, 100, 1, out _);
            // passed in mixed order: per-peer, shared and TSET sections
            var others = new List<ArraySegment<byte>>
            {
                Encoded(SectionType.Values, Filled(10, 1)),
                Encoded(SectionType.SysState, Filled(20, 2)),
                Encoded(SectionType.Hash, Filled(5, 3)),
                Encoded(SectionType.TypeSets, Filled(7, 4)),
                Encoded(SectionType.Clock, Filled(3, 5))
            };
            List<Part> parts = Split(splitter, EpochFlags.CatchUp, tx, others);
            Assert.Equal(1, parts.Count);
            EpochHeader h = parts[0].Header;
            Assert.Equal(Epoch, h.Epoch);
            Assert.Equal(Tick, h.HostTick);
            Assert.Equal(EpochFlags.CatchUp, h.Flags, "no MultiPart flag on a single message");
            Assert.Equal(1, h.PartCount);
            List<Section> sections = EpochFramingTests.ReadSections(parts[0].Bytes, out _);
            var order = new List<SectionType>();
            foreach (Section s in sections) order.Add(s.Type);
            Assert.SequenceEqual(new[] { SectionType.TypeSets, SectionType.SysState, SectionType.Clock, SectionType.Tx, SectionType.Values, SectionType.Hash }, order);
            Assert.SequenceEqual(tx, sections[3].Body);
            Assert.SequenceEqual(Filled(20, 2), sections[1].Body, "section bodies are copied unchanged");
        }

        [Test]
        public void EmptyTransactionWritesNoTxSection()
        {
            var splitter = new EpochPartSplitter();
            List<Part> parts = Split(splitter, EpochFlags.None, null, new[] { Encoded(SectionType.Clock, Filled(4, 9)) });
            Assert.Equal(1, parts.Count);
            List<Section> sections = EpochFramingTests.ReadSections(parts[0].Bytes, out EpochHeader header);
            Assert.Equal(1, header.SectionCount);
            Assert.Equal(SectionType.Clock, sections[0].Type);

            parts = Split(splitter, EpochFlags.None, new byte[0], null);
            Assert.Equal(1, parts.Count);
            Assert.Equal(0, parts[0].Header.SectionCount, "an epoch without content is still a message (the clock moves on)");
        }

        [Test(TimeoutSeconds = 600)]
        public void HundredMegabyteTransactionIsSplitAtRecordBoundariesAndReassembledByteIdentical()
        {
            byte[] tx = BuildTransaction(100L * 1024 * 1024, 64 * 1024, 2026, out int records);
            var others = new List<ArraySegment<byte>>
            {
                Encoded(SectionType.TypeSets, Filled(1000, 1)),
                Encoded(SectionType.SysState, Filled(300_000, 2)),
                Encoded(SectionType.Names, Filled(500, 3)),
                Encoded(SectionType.Values, Filled(200_000, 4)),
                Encoded(SectionType.Hash, Filled(64, 5))
            };
            var splitter = new EpochPartSplitter();
            List<Part> parts = Split(splitter, EpochFlags.None, tx, others);
            Assert.True(parts.Count >= 13 && parts.Count <= 14, "100 MiB in parts of at most 8 MiB: " + parts.Count + " parts");

            for (int i = 0; i < parts.Count; i++)
            {
                Assert.Equal(i, parts[i].Header.PartIndex);
                Assert.Equal(parts.Count, parts[i].Header.PartCount);
                Assert.Equal(EpochFlags.MultiPart, parts[i].Header.Flags);
                Assert.Equal(Epoch, parts[i].Header.Epoch);
                Assert.Equal(Tick, parts[i].Header.HostTick);
            }
            List<Section> first = EpochFramingTests.ReadSections(parts[0].Bytes, out _);
            Assert.Equal(SectionType.TypeSets, first[0].Type, "the TSET section leads part 0");
            Assert.Equal(SectionType.SysState, first[1].Type);
            Assert.Equal(SectionType.Names, first[2].Type);
            Assert.Equal(SectionType.Tx, first[3].Type);
            List<Section> last = EpochFramingTests.ReadSections(parts[parts.Count - 1].Bytes, out _);
            Assert.Equal(SectionType.Values, last[last.Count - 2].Type, "per-peer sections ride in the last part");
            Assert.Equal(SectionType.Hash, last[last.Count - 1].Type);

            // deliver in reverse order with duplicates; the epoch completes exactly when the last missing part arrives
            string spill = NewTempFolder();
            try
            {
                using (var assembler = new EpochPartAssembler(spill))
                {
                    Stream complete = null;
                    for (int i = parts.Count - 1; i >= 0; i--)
                    {
                        PartResult result = Deliver(assembler, parts[i], out Stream stream);
                        if (i > 0)
                        {
                            Assert.Equal(PartResult.Incomplete, result, "part " + i);
                            Assert.Null(stream);
                            Assert.Equal(PartResult.Incomplete, Deliver(assembler, parts[i], out _), "duplicate of part " + i);
                            Assert.Equal(1, assembler.PartialEpochs);
                        }
                        else
                        {
                            Assert.Equal(PartResult.Complete, result);
                            complete = stream;
                        }
                    }
                    Assert.Equal(0, assembler.PartialEpochs);
                    Assert.Equal(0L, assembler.PartialBytes);
                    Assert.Equal(1, Directory.GetFiles(spill).Length, "the epoch spilled to one temp file above 32 MiB");

                    var otherSections = new List<Section>();
                    byte[] reassembled;
                    EpochHeader header;
                    int txSections;
                    using (complete)
                    {
                        reassembled = ReadComplete(complete, out header, otherSections, out txSections);
                    }
                    Assert.Equal(0, Directory.GetFiles(spill).Length, "disposing the epoch deletes its spill file");
                    Assert.Equal(Epoch, header.Epoch);
                    Assert.Equal(Tick, header.HostTick);
                    Assert.Equal(EpochFlags.None, header.Flags, "the complete epoch is an ordinary message");
                    Assert.Equal(1, header.PartCount);
                    Assert.Equal(parts.Count, txSections);
                    Assert.Equal(tx.Length, reassembled.Length);
                    Assert.SequenceEqual(tx, reassembled, "TX records byte-identical");
                    Assert.Equal(5, otherSections.Count);
                    Assert.Equal(SectionType.TypeSets, otherSections[0].Type);
                    Assert.SequenceEqual(Filled(300_000, 2), otherSections[1].Body);
                    Assert.Equal(SectionType.Hash, otherSections[4].Type);

                    // a late duplicate of the delivered epoch is ignored
                    Assert.Equal(PartResult.Incomplete, Deliver(assembler, parts[3], out _));
                    Assert.Equal(0, assembler.PartialEpochs);

                    var count = new RecordReader();
                    count.Reset(tx, 0, tx.Length);
                    int seen = 0;
                    while (count.Next(out _)) seen++;
                    Assert.Equal(records, seen);
                }
            }
            finally
            {
                DeleteFolder(spill);
            }
        }

        [Test(TimeoutSeconds = 300)]
        public void RandomEpochsSurviveAnyDeliveryOrder()
        {
            var random = new DeterministicRandom(77);
            for (int round = 0; round < 40; round++)
            {
                int partSize = random.Next(4096, 40_000);
                var splitter = new EpochPartSplitter(partSize);
                byte[] tx = BuildTransaction(random.Next(0, 400_000), random.Next(1, partSize / 3), (ulong)round + 1, out _);
                var others = new List<ArraySegment<byte>>();
                int otherCount = random.Next(6);
                for (int i = 0; i < otherCount; i++)
                {
                    var type = (SectionType)random.Next(2, 14);
                    others.Add(Encoded(type, Filled(random.Next(0, partSize / 4), (byte)i)));
                }
                List<Part> parts = Split(splitter, (EpochFlags)random.Next(8), tx, others);

                // shuffle, duplicate some parts
                var order = new List<Part>(parts);
                for (int i = order.Count - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    (order[i], order[j]) = (order[j], order[i]);
                }
                int duplicates = random.Next(3);
                for (int d = 0; d < duplicates && parts.Count > 1; d++) order.Insert(random.Next(order.Count), parts[random.Next(parts.Count)]);

                var assembler = new EpochPartAssembler(null);
                Stream complete = null;
                int completions = 0;
                foreach (Part part in order)
                {
                    PartResult result = Deliver(assembler, part, out Stream stream);
                    Assert.True(result == PartResult.Complete || result == PartResult.Incomplete, "round " + round + ": " + result);
                    if (result == PartResult.Complete)
                    {
                        completions++;
                        complete = stream;
                    }
                }
                Assert.Equal(1, completions, "round " + round);
                var otherSections = new List<Section>();
                byte[] reassembled;
                using (complete) reassembled = ReadComplete(complete, out _, otherSections, out _);
                Assert.SequenceEqual(tx, reassembled, "round " + round);
                Assert.Equal(others.Count, otherSections.Count, "round " + round);
            }
        }

        [Test]
        public void SinglePartEpochIsReturnedAtOnceAsACopy()
        {
            byte[] message = EpochFramingTests.WriteEpoch(Epoch, Tick, EpochFlags.SnapshotBoundary, new[] { new Section { Type = SectionType.Tx, Body = new byte[] { 6, 1, 7 } } });
            var assembler = new EpochPartAssembler(null);
            var reader = new EpochReader();
            EpochHeader header = reader.Open(message, 0, message.Length);
            Assert.Equal(PartResult.Complete, assembler.Accept(in header, message, 0, message.Length, out Stream complete));
            byte[] copy = ReadAll(complete);
            message[0] ^= 0xFF;                                                     // the caller may reuse its buffer
            Assert.NotEqual(message[0], copy[0]);
            message[0] ^= 0xFF;
            Assert.SequenceEqual(message, copy);
            Assert.Equal(0, assembler.PartialEpochs);
        }

        [Test]
        public void EpochAboveTheTotalLimitIsOversized()
        {
            // 33 parts of about 8 MiB declare 264 MiB: part 32 crosses 256 MiB (the real limit) and drops the epoch
            const int partCount = 40;
            var body = new byte[Limits.MaxUncompressedBytes - 32];
            byte[] message = EpochFramingTests.WriteEpoch(Epoch, Tick, EpochFlags.None, new[] { new Section { Type = SectionType.Values, Body = body } }, 0, partCount);
            Assert.True(message.Length <= Limits.MaxUncompressedBytes);
            const int indexOffset = EpochFormat.FixedHeaderBytes;                   // partIndex varint (one byte below 128)
            var assembler = new EpochPartAssembler(null);
            var reader = new EpochReader();
            long accepted = 0;
            for (int index = 0; index < partCount; index++)
            {
                message[indexOffset] = (byte)index;
                EpochHeader header = reader.Open(message, 0, message.Length);
                PartResult result = assembler.Accept(in header, message, 0, message.Length, out Stream complete);
                Assert.Null(complete);
                long bodyBytes = message.Length - EpochFormat.HeaderSize(index, partCount);
                if (accepted + bodyBytes <= Limits.MaxEpochTotalBytes)
                {
                    Assert.Equal(PartResult.Incomplete, result, "part " + index);
                    accepted += bodyBytes;
                    Assert.Equal(accepted, assembler.PartialBytes);
                }
                else
                {
                    Assert.Equal(PartResult.Oversized, result, "part " + index + " crosses 256 MiB");
                    Assert.Equal(0L, assembler.PartialBytes, "the dropped epoch releases its buffers");
                    Assert.Equal(0, assembler.PartialEpochs);
                    Assert.True(index >= 31, "at least 31 full parts fit");
                    for (int later = index + 1; later < partCount; later++)
                    {
                        message[indexOffset] = (byte)later;
                        header = reader.Open(message, 0, message.Length);
                        Assert.Equal(PartResult.Oversized, assembler.Accept(in header, message, 0, message.Length, out _), "later parts of the dropped epoch");
                    }
                    Assert.Equal(0L, assembler.PartialBytes);

                    // the next epoch is assembled normally
                    byte[] next = EpochFramingTests.WriteEpoch(Epoch.Next(), Tick + 1, EpochFlags.None, new[] { new Section { Type = SectionType.Tx } }, 0, 2);
                    header = reader.Open(next, 0, next.Length);
                    Assert.Equal(PartResult.Incomplete, assembler.Accept(in header, next, 0, next.Length, out _));
                    next = EpochFramingTests.WriteEpoch(Epoch.Next(), Tick + 1, EpochFlags.None, new[] { new Section { Type = SectionType.Tx } }, 1, 2);
                    header = reader.Open(next, 0, next.Length);
                    Assert.Equal(PartResult.Complete, assembler.Accept(in header, next, 0, next.Length, out Stream done));
                    done.Dispose();
                    return;
                }
            }
            Assert.Fail("the epoch was never reported as oversized");
        }

        [Test]
        public void PartialEpochsTogetherAreBounded()
        {
            var assembler = new EpochPartAssembler(null, 0, null, 1000);
            var reader = new EpochReader();
            byte[] a = EpochFramingTests.WriteEpoch(new EpochId(1), 1, EpochFlags.None, new[] { new Section { Type = SectionType.Tx, Body = new byte[600] } }, 0, 2);
            byte[] b = EpochFramingTests.WriteEpoch(new EpochId(2), 1, EpochFlags.None, new[] { new Section { Type = SectionType.Tx, Body = new byte[600] } }, 0, 2);
            EpochHeader ha = reader.Open(a, 0, a.Length);
            Assert.Equal(PartResult.Incomplete, assembler.Accept(in ha, a, 0, a.Length, out _));
            EpochHeader hb = reader.Open(b, 0, b.Length);
            Assert.Equal(PartResult.Oversized, assembler.Accept(in hb, b, 0, b.Length, out _), "two partial epochs above the total");
            Assert.Equal(1, assembler.PartialEpochs, "the first epoch is kept");
            Assert.Equal(PartResult.Oversized, assembler.Accept(in hb, b, 0, b.Length, out _), "the dropped epoch stays dropped");
        }

        [Test]
        public void InconsistentOrMalformedPartsAreInvalid()
        {
            var log = new ListLog();
            var assembler = new EpochPartAssembler(null, 32L << 20, log);
            var reader = new EpochReader();
            Section[] content = { new Section { Type = SectionType.Tx, Body = new byte[] { 1 } } };

            byte[] p0 = EpochFramingTests.WriteEpoch(new EpochId(10), 5, EpochFlags.None, content, 0, 3);
            EpochHeader h0 = reader.Open(p0, 0, p0.Length);
            Assert.Equal(PartResult.Incomplete, assembler.Accept(in h0, p0, 0, p0.Length, out _));

            byte[] otherTick = EpochFramingTests.WriteEpoch(new EpochId(10), 6, EpochFlags.None, content, 1, 3);
            EpochHeader h1 = reader.Open(otherTick, 0, otherTick.Length);
            Assert.Equal(PartResult.Invalid, assembler.Accept(in h1, otherTick, 0, otherTick.Length, out _), "host tick differs between parts");
            Assert.Equal(0, assembler.PartialEpochs, "the inconsistent epoch is dropped");

            Assert.Equal(PartResult.Incomplete, assembler.Accept(in h0, p0, 0, p0.Length, out _));
            byte[] otherCount = EpochFramingTests.WriteEpoch(new EpochId(10), 5, EpochFlags.None, content, 1, 4);
            EpochHeader h2 = reader.Open(otherCount, 0, otherCount.Length);
            Assert.Equal(PartResult.Invalid, assembler.Accept(in h2, otherCount, 0, otherCount.Length, out _), "part count differs");

            Assert.Equal(PartResult.Incomplete, assembler.Accept(in h0, p0, 0, p0.Length, out _));
            byte[] otherFlags = EpochFramingTests.WriteEpoch(new EpochId(10), 5, EpochFlags.CatchUp, content, 1, 3);
            EpochHeader h3 = reader.Open(otherFlags, 0, otherFlags.Length);
            Assert.Equal(PartResult.Invalid, assembler.Accept(in h3, otherFlags, 0, otherFlags.Length, out _), "flags differ");

            // the header passed in must describe the bytes
            EpochHeader lying = h0;
            lying.PartIndex = 2;
            Assert.Equal(PartResult.Invalid, assembler.Accept(in lying, p0, 0, p0.Length, out _), "header does not match the bytes");

            // malformed framing never throws
            byte[] broken = (byte[])p0.Clone();
            broken[EpochFormat.SectionCountOffset] = 2;
            Assert.Equal(PartResult.Invalid, assembler.Accept(in h0, broken, 0, broken.Length, out _), "declared section missing");
            Assert.Equal(PartResult.Invalid, assembler.Accept(in h0, p0, 0, 5, out _), "truncated header");
            Assert.True(log.Count(LogLevel.Warn) >= 6, "every rejection is logged");
        }

        [Test]
        public void AtMostFourEpochsArePartialAtOnce()
        {
            var assembler = new EpochPartAssembler(null);
            var reader = new EpochReader();
            for (uint e = 1; e <= Limits.MaxPartialMessagesPerPeer; e++)
            {
                byte[] part = EpochFramingTests.WriteEpoch(new EpochId(e), 1, EpochFlags.None, new[] { new Section { Type = SectionType.Tx } }, 0, 2);
                EpochHeader header = reader.Open(part, 0, part.Length);
                Assert.Equal(PartResult.Incomplete, assembler.Accept(in header, part, 0, part.Length, out _));
            }
            Assert.Equal(4, assembler.PartialEpochs);
            byte[] fifth = EpochFramingTests.WriteEpoch(new EpochId(99), 1, EpochFlags.None, new[] { new Section { Type = SectionType.Tx } }, 0, 2);
            EpochHeader fifthHeader = reader.Open(fifth, 0, fifth.Length);
            Assert.Equal(PartResult.Invalid, assembler.Accept(in fifthHeader, fifth, 0, fifth.Length, out _));
            assembler.Reset();
            Assert.Equal(0, assembler.PartialEpochs);
            Assert.Equal(PartResult.Incomplete, assembler.Accept(in fifthHeader, fifth, 0, fifth.Length, out _), "after Reset");
        }

        [Test]
        public void MoreThan255SectionsInTotalAreInvalid()
        {
            var assembler = new EpochPartAssembler(null);
            var reader = new EpochReader();
            var sections = new Section[200];
            for (int i = 0; i < sections.Length; i++) sections[i] = new Section { Type = SectionType.Events };
            byte[] p0 = EpochFramingTests.WriteEpoch(Epoch, 1, EpochFlags.None, sections, 0, 2);
            byte[] p1 = EpochFramingTests.WriteEpoch(Epoch, 1, EpochFlags.None, sections, 1, 2);
            EpochHeader h0 = reader.Open(p0, 0, p0.Length);
            Assert.Equal(PartResult.Incomplete, assembler.Accept(in h0, p0, 0, p0.Length, out _));
            EpochHeader h1 = reader.Open(p1, 0, p1.Length);
            Assert.Equal(PartResult.Invalid, assembler.Accept(in h1, p1, 0, p1.Length, out _));
        }

        [Test]
        public void SpilledEpochEqualsTheInMemoryOneAndResetDeletesSpillFiles()
        {
            byte[] tx = BuildTransaction(300_000, 5000, 5, out _);
            var splitter = new EpochPartSplitter(64 * 1024);
            List<Part> parts = Split(splitter, EpochFlags.ChannelKeyframe, tx, new[] { Encoded(SectionType.Session, Filled(100, 1)) });
            Assert.True(parts.Count > 3);

            string spill = NewTempFolder();
            try
            {
                var memory = new EpochPartAssembler(null);
                var disk = new EpochPartAssembler(spill, 0);                       // spill at once
                byte[] fromMemory = null, fromDisk = null;
                foreach (Part part in parts)
                {
                    if (Deliver(memory, part, out Stream m) == PartResult.Complete) using (m) fromMemory = ReadAll(m);
                    if (Deliver(disk, part, out Stream d) == PartResult.Complete)
                    {
                        Assert.Equal(1, Directory.GetFiles(spill).Length);
                        Assert.True(d.CanSeek);
                        d.Position = 3;
                        Assert.Equal(3L, d.Position);
                        d.Seek(0, SeekOrigin.Begin);
                        using (d) fromDisk = ReadAll(d);
                        Assert.Throws<ObjectDisposedException>(() => d.ReadByte());
                    }
                }
                Assert.NotNull(fromMemory);
                Assert.SequenceEqual(fromMemory, fromDisk, "spilled and in-memory epochs are identical");
                Assert.Equal(0, Directory.GetFiles(spill).Length);

                // a partial spilled epoch: Reset (resync) and Dispose delete its file (Reset also forgets that the
                // epoch was delivered, so its parts are buffered again)
                disk.Reset();
                Assert.Equal(PartResult.Incomplete, Deliver(disk, parts[0], out _));
                Assert.Equal(1, Directory.GetFiles(spill).Length);
                Assert.True(disk.PartialBytes > 0);
                disk.Reset();
                Assert.Equal(0, Directory.GetFiles(spill).Length);
                Assert.Equal(0L, disk.PartialBytes);
                Deliver(disk, parts[1], out _);
                disk.Dispose();
                Assert.Equal(0, Directory.GetFiles(spill).Length);
                Assert.Throws<ObjectDisposedException>(() => Deliver(disk, parts[0], out _));
            }
            finally
            {
                DeleteFolder(spill);
            }
        }

        [Test]
        public void UnusableSpillDirectoryKeepsEpochsInMemory()
        {
            // a spill "directory" that is an existing file cannot be created: the assembler logs and keeps going in memory
            string folder = NewTempFolder();
            try
            {
                string file = Path.Combine(folder, "not-a-directory");
                File.WriteAllBytes(file, new byte[1]);
                var log = new ListLog();
                var assembler = new EpochPartAssembler(file, 0, log);
                byte[] tx = BuildTransaction(100_000, 2000, 8, out _);
                List<Part> parts = Split(new EpochPartSplitter(16 * 1024), EpochFlags.None, tx, null);
                byte[] reassembled = null;
                foreach (Part part in parts)
                {
                    if (Deliver(assembler, part, out Stream complete) == PartResult.Complete)
                    {
                        using (complete) reassembled = ReadComplete(complete, out _, new List<Section>(), out _);
                    }
                }
                Assert.SequenceEqual(tx, reassembled);
                Assert.True(log.Contains("cannot spill"), "the spill failure is logged");
            }
            finally
            {
                DeleteFolder(folder);
            }
        }

        [Test]
        public void SplitterRejectsWhatCannotBeSent()
        {
            var splitter = new EpochPartSplitter(4096);
            int calls = 0;
            Action<byte[], int, int> count = (b, o, c) => calls++;

            // a record larger than a part
            var big = new ByteWriter();
            new RecordWriter(big).Value(new ValueHeader { Slot = new NetSlot(1), Type = new TypeRank(1) }, new byte[5000], 0, 5000);
            byte[] bigTx = big.ToArray();
            Assert.Throws<InvalidOperationException>(() => splitter.Split(Epoch, Tick, EpochFlags.None, bigTx, 0, bigTx.Length, null, count));

            // a section larger than a part
            ArraySegment<byte> bigSection = Encoded(SectionType.SysState, new byte[5000]);
            Assert.Throws<InvalidOperationException>(() => splitter.Split(Epoch, Tick, EpochFlags.None, null, 0, 0, new[] { bigSection }, count));

            // records that are not a valid sequence (only checked when they must be split): an undefined record kind at the end
            byte[] valid = BuildTransaction(20_000, 500, 3, out _);
            var garbage = new byte[valid.Length + 1];
            Array.Copy(valid, garbage, valid.Length);
            Assert.Throws<ArgumentException>(() => splitter.Split(Epoch, Tick, EpochFlags.None, garbage, 0, garbage.Length, null, count));

            // more than 255 sections in the whole epoch
            byte[] manyRecords = BuildTransaction(256 * 4096, 3000, 4, out _);
            Assert.Throws<InvalidOperationException>(() => splitter.Split(Epoch, Tick, EpochFlags.None, manyRecords, 0, manyRecords.Length, null, count));

            // argument errors
            Assert.Throws<ArgumentException>(() => splitter.Split(Epoch, Tick, EpochFlags.None, null, 0, 0, new[] { Encoded(SectionType.Tx, new byte[1]) }, count), "TX as another section");
            Assert.Throws<ArgumentException>(() => splitter.Split(Epoch, Tick, EpochFlags.None, null, 0, 0, new[] { new ArraySegment<byte>(Hex("02 05 00")) }, count), "section length overruns");
            Assert.Throws<ArgumentException>(() => splitter.Split(Epoch, Tick, EpochFlags.None, null, 0, 0, new[] { new ArraySegment<byte>(Hex("02 01 00 00")) }, count), "more than one section");
            Assert.Throws<ArgumentException>(() => splitter.Split(Epoch, Tick, EpochFlags.None, null, 0, 0, new[] { new ArraySegment<byte>(Hex("0e 00")) }, count), "undefined section type");
            Assert.Throws<ArgumentException>(() => splitter.Split(Epoch, Tick, (EpochFlags)0x40, null, 0, 0, null, count));
            Assert.Throws<ArgumentNullException>(() => splitter.Split(Epoch, Tick, EpochFlags.None, null, 0, 0, null, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new EpochPartSplitter(4095));
            Assert.Throws<ArgumentOutOfRangeException>(() => new EpochPartSplitter(Limits.MaxUncompressedBytes + 1));
            Assert.Equal(0, calls, "nothing is emitted for an epoch that cannot be sent");
        }

        [Test]
        public void TooManyPartsCannotBeSent()
        {
            var splitter = new EpochPartSplitter(4096);
            byte[] tx = BuildTransaction(1025L * 4096, 1000, 6, out _);
            int calls = 0;
            // every part holds at least one section, so the 255-section limit refuses such an epoch before the
            // 1024-part limit; either way nothing is emitted
            Assert.Throws<InvalidOperationException>(() => splitter.Split(Epoch, Tick, EpochFlags.None, tx, 0, tx.Length, null, (b, o, c) => calls++));
            Assert.Equal(0, calls);
        }

        [Test]
        public void HeaderSectionsThatOverflowPartZeroContinueInTheNextPart()
        {
            var splitter = new EpochPartSplitter(8192);
            var others = new List<ArraySegment<byte>>();
            for (int i = 0; i < 5; i++) others.Add(Encoded(SectionType.Icons, Filled(3000, (byte)i)));
            byte[] tx = BuildTransaction(10_000, 400, 9, out _);
            List<Part> parts = Split(splitter, EpochFlags.None, tx, others);
            var types = new List<SectionType>();
            foreach (Part p in parts)
            {
                foreach (Section s in EpochFramingTests.ReadSections(p.Bytes, out _)) types.Add(s.Type);
            }
            int firstTx = types.IndexOf(SectionType.Tx);
            Assert.Equal(5, firstTx, "every header section precedes the TX records");
            for (int i = firstTx; i < types.Count; i++) Assert.Equal(SectionType.Tx, types[i]);
        }
    }
}
