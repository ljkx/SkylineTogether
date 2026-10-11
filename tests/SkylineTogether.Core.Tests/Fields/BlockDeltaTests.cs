using System;
using System.Collections.Generic;
using System.IO;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Fields
{
    /// <summary>BlockDelta (warm join): content-defined blocks, reconstruction with insertions and modifications, validation.</summary>
    public sealed class BlockDeltaTests
    {
        private const int SmallBlock = 4096;

        private static byte[] RandomBytes(int length, ulong seed)
        {
            var data = new byte[length];
            new DeterministicRandom(seed).NextBytes(data, 0, length);
            return data;
        }

        /// <summary>Data that looks like a save: compressed (random) buffers separated by small headers and zero padding.</summary>
        private static byte[] SaveLike(int length, ulong seed)
        {
            var random = new DeterministicRandom(seed);
            var data = new byte[length];
            int p = 0;
            while (p < length)
            {
                int run = Math.Min(length - p, 500 + random.Next(20000));
                random.NextBytes(data, p, run);
                p += run;
                int zeros = Math.Min(length - p, random.Next(300));
                p += zeros;
            }
            return data;
        }

        private static byte[] Splice(byte[] data, int at, int removeCount, byte[] insert)
        {
            var result = new byte[data.Length - removeCount + insert.Length];
            Buffer.BlockCopy(data, 0, result, 0, at);
            Buffer.BlockCopy(insert, 0, result, at, insert.Length);
            Buffer.BlockCopy(data, at + removeCount, result, at + insert.Length, data.Length - at - removeCount);
            return result;
        }

        private static byte[] BuildDelta(byte[] source, byte[] basis, int blockSize, out BlockDeltaStats stats)
        {
            IReadOnlyList<Digest128> receiverBlocks = BlockDelta.HashBlocks(new MemoryStream(basis), blockSize);
            var delta = new MemoryStream();
            BlockDelta.BuildDelta(new MemoryStream(source), receiverBlocks, blockSize, delta, out stats);
            Assert.Equal(delta.Length, stats.DeltaBytes, "DeltaBytes counts the delta stream");
            Assert.Equal((long)source.Length, stats.SourceBytes);
            Assert.Equal(stats.SourceBytes, stats.CopiedBytes + stats.LiteralBytes, "every source byte is copied or literal");
            return delta.ToArray();
        }

        private static byte[] Apply(byte[] basis, byte[] delta)
        {
            var output = new MemoryStream();
            BlockDelta.ApplyDelta(new MemoryStream(basis), new MemoryStream(delta), output);
            return output.ToArray();
        }

        private static byte[] RoundTrip(byte[] source, byte[] basis, int blockSize, out BlockDeltaStats stats)
        {
            byte[] delta = BuildDelta(source, basis, blockSize, out stats);
            byte[] rebuilt = Apply(basis, delta);
            Assert.SequenceEqual(source, rebuilt, "the rebuilt file equals the source");
            return delta;
        }

        [Test]
        public void IdenticalFilesNeedOnlyACopy()
        {
            byte[] file = SaveLike(400000, 1);
            byte[] delta = RoundTrip(file, file, SmallBlock, out BlockDeltaStats stats);
            Assert.Equal(stats.SourceBlocks, stats.MatchedBlocks);
            Assert.Equal(0L, stats.LiteralBytes);
            Assert.True(delta.Length < 64, "one copy operation: " + delta.Length + " bytes");
        }

        [Test]
        public void InsertionsAreFoundAtAnyOffset()
        {
            byte[] basis = SaveLike(600000, 2);
            byte[] source = Splice(basis, 150000, 0, RandomBytes(1234, 3));      // insertion in the middle
            source = Splice(source, 0, 0, RandomBytes(17, 4));                   // insertion at the start
            source = Splice(source, source.Length, 0, RandomBytes(5000, 5));     // append
            RoundTrip(source, basis, SmallBlock, out BlockDeltaStats stats);
            Assert.True(stats.LiteralBytes < 1234 + 17 + 5000 + 8 * 4 * SmallBlock, "literal bytes " + stats.LiteralBytes + " of " + source.Length);
            Assert.True(stats.MatchedBlocks > stats.SourceBlocks * 8 / 10, "matched " + stats.MatchedBlocks + " of " + stats.SourceBlocks);
        }

        [Test]
        public void DeletionsAndModificationsAreHandled()
        {
            byte[] basis = SaveLike(500000, 6);
            byte[] source = Splice(basis, 300000, 25000, new byte[0]);           // a deleted range
            var random = new DeterministicRandom(7);
            for (int i = 0; i < 6; i++) source[random.Next(source.Length)] ^= 0xFF;   // scattered modifications
            source = Splice(source, 100000, 1000, RandomBytes(3000, 8));         // a replaced range that grew
            RoundTrip(source, basis, SmallBlock, out BlockDeltaStats stats);
            Assert.True(stats.LiteralBytes < source.Length / 4, "literal bytes " + stats.LiteralBytes + " of " + source.Length);
        }

        [Test]
        public void UnrelatedEmptyAndRepetitiveFiles()
        {
            byte[] a = RandomBytes(100000, 9);
            byte[] b = RandomBytes(120000, 10);
            RoundTrip(b, a, SmallBlock, out BlockDeltaStats unrelated);
            Assert.Equal(0, unrelated.MatchedBlocks);
            Assert.Equal((long)b.Length, unrelated.LiteralBytes);
            RoundTrip(new byte[0], a, SmallBlock, out BlockDeltaStats empty);
            Assert.Equal(0, empty.SourceBlocks);
            RoundTrip(b, new byte[0], SmallBlock, out BlockDeltaStats noBasis);
            Assert.Equal((long)b.Length, noBasis.LiteralBytes);
            RoundTrip(new byte[0], new byte[0], SmallBlock, out _);
            var zeros = new byte[300000];
            RoundTrip(zeros, zeros, SmallBlock, out BlockDeltaStats allZero);
            Assert.Equal(0L, allZero.LiteralBytes, "equal zero runs match");
            RoundTrip(Splice(zeros, 777, 0, new byte[5]), zeros, SmallBlock, out BlockDeltaStats shiftedZeros);
            Assert.True(shiftedZeros.LiteralBytes <= 4 * SmallBlock, "a zero run shifted by an insertion still matches (" + shiftedZeros.LiteralBytes + " literal bytes)");
            // a short period without any content-defined boundary degrades to maximum-size blocks (correct, not compact);
            // real snapshots are compressed buffers with high entropy (R01 §4)
            var pattern = new byte[300000];
            for (int i = 0; i < pattern.Length; i++) pattern[i] = (byte)(i % 251);
            RoundTrip(pattern, zeros, SmallBlock, out _);
            RoundTrip(Splice(pattern, 1000, 0, new byte[] { 1, 2, 3 }), pattern, SmallBlock, out _);
            // a repeated random block: equal blocks resolve to their first occurrence, and an insertion is still found
            byte[] unit = RandomBytes(40000, 24);
            var repeated = new byte[unit.Length * 6];
            for (int k = 0; k < 6; k++) Buffer.BlockCopy(unit, 0, repeated, k * unit.Length, unit.Length);
            RoundTrip(Splice(repeated, 1000, 0, new byte[] { 1, 2, 3 }), repeated, SmallBlock, out BlockDeltaStats duplicates);
            Assert.True(duplicates.LiteralBytes < 3 * 4 * SmallBlock, "repeated content is matched (" + duplicates.LiteralBytes + " literal bytes)");
        }

        [Test]
        public void DefaultBlockSizeOnASnapshotSizedFile()
        {
            byte[] basis = SaveLike(9 << 20, 11);
            byte[] source = Splice(basis, 4 << 20, 64, RandomBytes(100000, 12));
            Assert.Equal(1 << 20, BlockDelta.DefaultBlockSize);
            RoundTrip(source, basis, BlockDelta.DefaultBlockSize, out BlockDeltaStats stats);
            Assert.True(stats.CopiedBytes > source.Length / 2, "copied " + stats.CopiedBytes + " of " + source.Length);
        }

        [Test]
        public void HashBlocksIsContentDefined()
        {
            byte[] basis = SaveLike(800000, 13);
            byte[] source = Splice(basis, 400000, 0, RandomBytes(333, 14));
            var before = new HashSet<Digest128>(BlockDelta.HashBlocks(new MemoryStream(basis), SmallBlock));
            IReadOnlyList<Digest128> after = BlockDelta.HashBlocks(new MemoryStream(source), SmallBlock);
            int shared = 0;
            foreach (Digest128 digest in after) if (before.Contains(digest)) shared++;
            Assert.True(shared >= after.Count - 3, "an insertion changes at most a few blocks (" + shared + " of " + after.Count + " shared)");
            Assert.SequenceEqual(BlockDelta.HashBlocks(new MemoryStream(basis), SmallBlock), BlockDelta.HashBlocks(new TrickleStream(new MemoryStream(basis), 777, false), SmallBlock),
                                 "block boundaries do not depend on how the stream delivers its bytes");
        }

        [Test]
        public void ChunkSizesStayWithinBounds()
        {
            byte[] data = SaveLike(2000000, 15);
            var chunker = new ContentDefinedChunker(SmallBlock);
            chunker.Start(new MemoryStream(data));
            var lengths = new List<int>();
            long total = 0;
            while (chunker.Next(out byte[] buffer, out int offset, out int length))
            {
                lengths.Add(length);
                total += length;
            }
            chunker.Finish();
            Assert.Equal((long)data.Length, total);
            for (int i = 0; i < lengths.Count - 1; i++)
            {
                Assert.InRange(lengths[i], chunker.MinSize + 1, chunker.MaxSize, "chunk " + i);
            }
            double average = (double)total / lengths.Count;
            Assert.InRange(average, SmallBlock * 0.5, SmallBlock * 2.0, "average chunk size");
        }

        [Test]
        public void GearTableAndBoundariesArePinned()
        {
            // host and client must cut identical blocks: the gear table and the cut rule are part of the warm-join format
            Assert.Equal(PinnedGear0, ContentDefinedChunker.Gear[0]);
            Assert.Equal(PinnedGear255, ContentDefinedChunker.Gear[255]);
            var data = new byte[200000];
            for (int i = 0; i < data.Length; i++) data[i] = unchecked((byte)(i * 2654435761u >> 13));
            IReadOnlyList<Digest128> blocks = BlockDelta.HashBlocks(new MemoryStream(data), 1024);
            var all = new byte[blocks.Count * 16];
            for (int i = 0; i < blocks.Count; i++)
            {
                BitConverter.GetBytes(blocks[i].Lo).CopyTo(all, 16 * i);
                BitConverter.GetBytes(blocks[i].Hi).CopyTo(all, 16 * i + 8);
            }
            Assert.Equal(PinnedBlockCount, blocks.Count);
            Assert.Equal(PinnedBlockListHash, XxHash64.Hash(all, 0, all.Length));
        }

        [Test]
        public void StreamsAreReadFromTheirCurrentPosition()
        {
            byte[] basisFile = SaveLike(200000, 16);
            byte[] sourceFile = Splice(basisFile, 50000, 0, RandomBytes(100, 17));
            var prefixedBasis = new byte[basisFile.Length + 1000];
            Buffer.BlockCopy(basisFile, 0, prefixedBasis, 1000, basisFile.Length);
            var basisStream = new MemoryStream(prefixedBasis) { Position = 1000 };
            IReadOnlyList<Digest128> blocks = BlockDelta.HashBlocks(basisStream, SmallBlock);
            var delta = new MemoryStream();
            BlockDelta.BuildDelta(new TrickleStream(new MemoryStream(sourceFile), 999, false), blocks, SmallBlock, delta);
            var output = new MemoryStream();
            var applyBasis = new TrickleStream(new MemoryStream(prefixedBasis) { Position = 1000 }, 1500, true);
            BlockDelta.ApplyDelta(applyBasis, new TrickleStream(new MemoryStream(delta.ToArray()), 333, false), output);
            Assert.SequenceEqual(sourceFile, output.ToArray());
        }

        [Test]
        public void AWrongBasisIsRejected()
        {
            byte[] basis = SaveLike(300000, 18);
            byte[] source = Splice(basis, 1000, 0, RandomBytes(10, 19));
            byte[] delta = BuildDelta(source, basis, SmallBlock, out _);
            byte[] otherBasis = (byte[])basis.Clone();
            for (int i = 0; i < otherBasis.Length; i += 50000) otherBasis[i] ^= 0x55;   // same length, other content
            AssertRejected(otherBasis, delta, "a modified basis");
            AssertRejected(SaveLike(300000, 20), delta, "an unrelated basis");
            AssertRejected(new byte[0], delta, "an empty basis");
        }

        [Test]
        public void CorruptAndTruncatedDeltasOnlyThrowDecodeException()
        {
            byte[] basis = SaveLike(120000, 21);
            byte[] source = Splice(basis, 60000, 500, RandomBytes(2000, 22));
            byte[] delta = BuildDelta(source, basis, 1024, out _);
            for (int length = 0; length < delta.Length; length += Math.Max(1, delta.Length / 300))
            {
                var prefix = new byte[length];
                Buffer.BlockCopy(delta, 0, prefix, 0, length);
                AssertRejected(basis, prefix, "a prefix of " + length + " bytes");
            }
            var random = new DeterministicRandom(23);
            int rejected = 0;
            for (int round = 0; round < 400; round++)
            {
                byte[] corrupt = (byte[])delta.Clone();
                // corrupt the header and operations more often than literal data, where only the digest can notice
                int position = random.Next(2) == 0 ? random.Next(Math.Min(64, corrupt.Length)) : random.Next(corrupt.Length);
                corrupt[position] ^= (byte)(1 + random.Next(255));
                try
                {
                    byte[] rebuilt = Apply(basis, corrupt);
                    Assert.SequenceEqual(source, rebuilt, "a corrupt delta either fails or still rebuilds the exact source");
                }
                catch (DecodeException)
                {
                    rejected++;
                }
            }
            Assert.True(rejected > 300, "corruptions detected: " + rejected);
            AssertRejected(basis, new byte[] { (byte)'S', (byte)'T', (byte)'B', (byte)'X', 1 }, "bad magic");
            AssertRejected(basis, new byte[] { (byte)'S', (byte)'T', (byte)'B', (byte)'D', 9 }, "unknown version");
            AssertRejected(basis, new byte[] { (byte)'S', (byte)'T', (byte)'B', (byte)'D', 1, 0x10, 0 }, "block size below the minimum");
        }

        [Test]
        public void BlockListsRoundTripAndDriveTheDelta()
        {
            byte[] basis = SaveLike(700000, 30);
            byte[] source = Splice(basis, 350000, 100, RandomBytes(4000, 31));
            IReadOnlyList<Digest128> blocks = BlockDelta.HashBlocks(new MemoryStream(basis), SmallBlock);
            byte[] wire = FieldTestSupport.Write(w => BlockDelta.WriteBlockList(blocks, SmallBlock, w));
            Assert.Equal(1 + 2 + (blocks.Count < 128 ? 1 : 2) + 16 * blocks.Count, wire.Length, "version, varint block size, varint count, 16 bytes per block");
            var reader = new ByteReader(wire, 0, wire.Length);
            IReadOnlyList<Digest128> received = BlockDelta.ReadBlockList(reader, BlockDelta.MaxBlocksFor(basis.Length, SmallBlock), out int blockSize);
            Assert.True(reader.IsAtEnd);
            Assert.Equal(SmallBlock, blockSize);
            Assert.SequenceEqual(blocks, received);
            var delta = new MemoryStream();
            BlockDelta.BuildDelta(new MemoryStream(source), received, blockSize, delta);
            Assert.SequenceEqual(source, Apply(basis, delta.ToArray()), "the host builds the delta from the received list");
        }

        [Test]
        public void BlockListFormatIsPinned()
        {
            var blocks = new[] { new Digest128(0x0807060504030201UL, 0x100F0E0D0C0B0A09UL) };
            byte[] wire = FieldTestSupport.Write(w => BlockDelta.WriteBlockList(blocks, 1 << 20, w));
            Assert.SequenceEqual(new byte[] { 1, 0x80, 0x80, 0x40, 1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }, wire);
            byte[] empty = FieldTestSupport.Write(w => BlockDelta.WriteBlockList(new Digest128[0], 256, w));
            Assert.SequenceEqual(new byte[] { 1, 0x80, 0x02, 0 }, empty);
            Assert.Equal(0, BlockDelta.ReadBlockList(new ByteReader(empty, 0, empty.Length), 0, out int size).Count);
            Assert.Equal(256, size);
        }

        [Test]
        public void HostileBlockListsAreRejectedBeforeAllocating()
        {
            var blocks = new List<Digest128>();
            for (int i = 0; i < 10; i++) blocks.Add(new Digest128((ulong)i, ~(ulong)i));
            byte[] wire = FieldTestSupport.Write(w => BlockDelta.WriteBlockList(blocks, SmallBlock, w));
            Assert.Throws<DecodeException>(() => BlockDelta.ReadBlockList(new ByteReader(wire, 0, wire.Length), 9, out _), "more blocks than allowed");
            Assert.Equal(10, BlockDelta.ReadBlockList(new ByteReader(wire, 0, wire.Length), 10, out _).Count, "exactly the limit");
            // a header that claims two billion blocks fails on the limit or the input size, never with an allocation
            byte[] claim = { 1, 0x80, 0x20, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 };
            Assert.Throws<DecodeException>(() => BlockDelta.ReadBlockList(new ByteReader(claim, 0, claim.Length), 1000, out _), "beyond the limit");
            Assert.Throws<DecodeException>(() => BlockDelta.ReadBlockList(new ByteReader(claim, 0, claim.Length), int.MaxValue, out _), "beyond the input");
            byte[] badVersion = (byte[])wire.Clone();
            badVersion[0] = 2;
            Assert.Throws<DecodeException>(() => BlockDelta.ReadBlockList(new ByteReader(badVersion, 0, badVersion.Length), 100, out _), "version");
            byte[] tinyBlocks = FieldTestSupport.Write(w =>
            {
                w.WriteByte(BlockDelta.BlockListVersion);
                w.WriteVarUInt32(BlockDelta.MinBlockSize - 1);
                w.WriteVarUInt32(0);
            });
            Assert.Throws<DecodeException>(() => BlockDelta.ReadBlockList(new ByteReader(tinyBlocks, 0, tinyBlocks.Length), 100, out _), "block size below the minimum");
            byte[] hugeBlocks = FieldTestSupport.Write(w =>
            {
                w.WriteByte(BlockDelta.BlockListVersion);
                w.WriteVarUInt32(BlockDelta.MaxBlockSize + 1);
                w.WriteVarUInt32(0);
            });
            Assert.Throws<DecodeException>(() => BlockDelta.ReadBlockList(new ByteReader(hugeBlocks, 0, hugeBlocks.Length), 100, out _), "block size above the maximum");
            FieldTestSupport.AssertEveryTruncationFails(wire, r => BlockDelta.ReadBlockList(r, 100, out _), "block list");
            FieldTestSupport.AssertCorruptionOnlyThrowsDecodeException(wire, r => BlockDelta.ReadBlockList(r, 100, out _), 32, 2000, "block list");
        }

        [Test]
        public void TheReceiverRejectsADeltaForAnotherBlockListBeforeReadingItsBasis()
        {
            byte[] basis = SaveLike(300000, 40);
            byte[] source = Splice(basis, 150000, 64, RandomBytes(3000, 41));
            IReadOnlyList<Digest128> blocks = BlockDelta.HashBlocks(new MemoryStream(basis), SmallBlock);
            byte[] delta = BuildDelta(source, basis, SmallBlock, out _);

            var output = new MemoryStream();
            BlockDelta.ApplyDelta(new MemoryStream(basis), new MemoryStream(delta), output, SmallBlock, blocks.Count);
            Assert.SequenceEqual(source, output.ToArray(), "the header matches the list the receiver sent");
            output = new MemoryStream();
            BlockDelta.ApplyDelta(new MemoryStream(basis), new MemoryStream(delta), output, -1, -1);
            Assert.SequenceEqual(source, output.ToArray(), "-1 skips both checks");

            // the basis throws on every read, so a rejection proves the header was checked first
            Assert.Throws<DecodeException>(() => BlockDelta.ApplyDelta(new UnreadableStream(), new MemoryStream(delta), new MemoryStream(), SmallBlock * 2, blocks.Count), "another block size");
            Assert.Throws<DecodeException>(() => BlockDelta.ApplyDelta(new UnreadableStream(), new MemoryStream(delta), new MemoryStream(), SmallBlock, blocks.Count + 1), "another block count");
            Assert.Throws<DecodeException>(() => BlockDelta.ApplyDelta(new UnreadableStream(), new MemoryStream(delta), new MemoryStream(), -1, blocks.Count - 1), "another block count, any size");
            byte[] tinyBlocks = FieldTestSupport.Write(w =>
            {
                w.WriteUInt32(0x44425453);   // "STBD"
                w.WriteByte(BlockDelta.FormatVersion);
                w.WriteVarUInt32(BlockDelta.MinBlockSize);
                w.WriteVarUInt32(int.MaxValue - 1);
            });
            Assert.Throws<DecodeException>(() => BlockDelta.ApplyDelta(new UnreadableStream(), new MemoryStream(tinyBlocks), new MemoryStream(), BlockDelta.DefaultBlockSize, 8192), "a hostile header");

            Assert.Throws<ArgumentOutOfRangeException>(() => BlockDelta.ApplyDelta(new MemoryStream(), new MemoryStream(), new MemoryStream(), BlockDelta.MinBlockSize - 1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => BlockDelta.ApplyDelta(new MemoryStream(), new MemoryStream(), new MemoryStream(), SmallBlock, -2));
            Assert.Equal(2L << 30, BlockDelta.MaxOutputBytes, "the output cap is the 2 GB blob cap of ARCH §14.5");
        }

        /// <summary>A seekable stream whose reads fail: proves that a code path never touches it.</summary>
        private sealed class UnreadableStream : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => true;

            public override bool CanWrite => false;

            public override long Length => 0;

            public override long Position { get; set; }

            public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("the basis must not be read");

            public override long Seek(long offset, SeekOrigin origin) => throw new InvalidOperationException("the basis must not be read");

            public override void Flush()
            {
            }

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        [Test]
        public void MaxBlocksForBoundsTheRealBlockCount()
        {
            Assert.Equal(0, BlockDelta.MaxBlocksFor(0, 256));
            Assert.Equal(1, BlockDelta.MaxBlocksFor(1, 256));
            Assert.Equal(1, BlockDelta.MaxBlocksFor(65, 256), "a block longer than the 64-byte minimum holds the whole file");
            Assert.Equal(2, BlockDelta.MaxBlocksFor(66, 256), "65 + 1 bytes can be two blocks");
            Assert.Equal(8192, BlockDelta.MaxBlocksFor(2L << 30, BlockDelta.DefaultBlockSize), "a 2 GiB snapshot: at most 8192 blocks (128 KiB of digests)");
            Assert.Equal(int.MaxValue, BlockDelta.MaxBlocksFor(long.MaxValue, BlockDelta.MinBlockSize), "clamped to int");
            foreach (int blockSize in new[] { 256, 1024, SmallBlock })
            {
                foreach (int length in new[] { 1, 63, 64, 65, 1000, 50000, 333333 })
                {
                    foreach (byte[] data in new[] { RandomBytes(length, (ulong)length), new byte[length], SaveLike(length, (ulong)length + 1) })
                    {
                        int count = BlockDelta.HashBlocks(new MemoryStream(data), blockSize).Count;
                        Assert.True(count <= BlockDelta.MaxBlocksFor(length, blockSize), "length " + length + " block size " + blockSize + ": " + count + " blocks");
                    }
                }
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => BlockDelta.MaxBlocksFor(-1, 256));
            Assert.Throws<ArgumentOutOfRangeException>(() => BlockDelta.MaxBlocksFor(100, 255));
        }

        [Test]
        public void ArgumentsAreValidated()
        {
            var empty = new MemoryStream();
            var blocks = new List<Digest128>();
            Assert.Throws<ArgumentNullException>(() => BlockDelta.HashBlocks(null));
            Assert.Throws<ArgumentOutOfRangeException>(() => BlockDelta.HashBlocks(empty, BlockDelta.MinBlockSize - 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => BlockDelta.HashBlocks(empty, BlockDelta.MaxBlockSize + 1));
            Assert.Throws<ArgumentNullException>(() => BlockDelta.BuildDelta(null, blocks, SmallBlock, new MemoryStream()));
            Assert.Throws<ArgumentNullException>(() => BlockDelta.BuildDelta(empty, null, SmallBlock, new MemoryStream()));
            Assert.Throws<ArgumentNullException>(() => BlockDelta.BuildDelta(empty, blocks, SmallBlock, null));
            Assert.Throws<ArgumentException>(() => BlockDelta.BuildDelta(empty, blocks, SmallBlock, new MemoryStream(new byte[10], false)));
            Assert.Throws<ArgumentException>(() => BlockDelta.ApplyDelta(new TrickleStream(new MemoryStream(), 10, false), new MemoryStream(), new MemoryStream()), "the basis must be seekable");
            Assert.Throws<ArgumentNullException>(() => BlockDelta.ApplyDelta(new MemoryStream(), null, new MemoryStream()));
            Assert.Throws<ArgumentException>(() => BlockDelta.ApplyDelta(new MemoryStream(), new MemoryStream(), new MemoryStream(new byte[1], false)));
            var writer = new ByteWriter();
            Assert.Throws<ArgumentNullException>(() => BlockDelta.WriteBlockList(null, SmallBlock, writer));
            Assert.Throws<ArgumentNullException>(() => BlockDelta.WriteBlockList(blocks, SmallBlock, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => BlockDelta.WriteBlockList(blocks, BlockDelta.MaxBlockSize + 1, writer));
            Assert.Equal(0, writer.Length, "nothing was written by the failed calls");
            Assert.Throws<ArgumentNullException>(() => BlockDelta.ReadBlockList(null, 10, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => BlockDelta.ReadBlockList(new ByteReader(), -1, out _));
        }

        internal const ulong PinnedGear0 = 0xC579BB6EE8EE4AE8UL;
        internal const ulong PinnedGear255 = 0x991E583CFF69A5D9UL;
        internal const int PinnedBlockCount = 178;
        internal const ulong PinnedBlockListHash = 0x8738B805E06F2791UL;

        private static void AssertRejected(byte[] basis, byte[] delta, string what)
        {
            try
            {
                Apply(basis, delta);
                Assert.Fail(what + ": the delta was applied");
            }
            catch (DecodeException)
            {
            }
            catch (AssertionException)
            {
                throw;
            }
            catch (Exception e)
            {
                Assert.Fail(what + ": threw " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
