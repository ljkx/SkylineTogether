using System;
using System.IO;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Transport
{
    /// <summary>
    /// The integrity checks of <see cref="BlobReceiver"/> (ARCHITECTURE.md#blob-transfer), which every join's city
    /// snapshot passes through: the per-chunk xxHash64, the blob id, the whole-blob XXH3-128 digest, the out-of-order and
    /// duplicate paths and the resend requests, each with exact counters.
    /// </summary>
    public sealed class BlobTransferTests
    {
        private const uint Id = 7;
        private const int ChunkSize = 512;

        private static byte[] Data(int size, int seed)
        {
            var data = new byte[size];
            new Random(seed).NextBytes(data);
            return data;
        }

        private static BlobManifestInfo ManifestOf(byte[] data, uint id = Id) => new BlobManifestInfo
        {
            BlobId = id,
            Size = data.Length,
            ChunkSize = ChunkSize,
            Digest = Digest128.Compute(data, 0, data.Length),
            Purpose = "test",
        };

        /// <summary>The body of a chunk message (header and payload, no envelope) with a correct hash.</summary>
        private static byte[] Chunk(byte[] data, int index, uint id = Id)
        {
            int start = index * ChunkSize;
            int length = Math.Min(ChunkSize, data.Length - start);
            var w = new ByteWriter();
            BlobCodec.WriteChunkHeader(w, id, index, length, XxHash64.Hash(data, start, length));
            w.WriteBytes(data, start, length);
            return w.ToArray();
        }

        /// <summary>A chunk whose payload has one flipped bit (the header still carries the hash of the original).</summary>
        private static byte[] Corrupted(byte[] chunk, int payloadByte = 100)
        {
            byte[] copy = (byte[])chunk.Clone();
            copy[BlobCodec.ChunkHeaderBytes + payloadByte] ^= 0x10;
            return copy;
        }

        private static bool Feed(BlobReceiver receiver, byte[] body) => receiver.OnChunk(body, 0, body.Length);

        [Test]
        public void AFlippedPayloadBitIsRejectedResentAndTheBlobStillVerifies()
        {
            byte[] data = Data(1300, 1); // chunks of 512, 512, 276
            var target = new MemoryStream();
            var receiver = new BlobReceiver(ManifestOf(data), target, new ListLog());
            Assert.Equal(3, receiver.ChunkCount);
            Assert.True(Feed(receiver, Chunk(data, 0)));
            Assert.False(Feed(receiver, Corrupted(Chunk(data, 1))), "a chunk that fails its xxHash64 is not accepted");
            Assert.Equal(1, receiver.CorruptChunks);
            Assert.Equal(0, receiver.RejectedChunks);
            Assert.Equal(0, receiver.DuplicateChunks);
            Assert.Equal(1, receiver.ChunksReceived, "and not stored");
            Assert.Equal(512L, receiver.BytesReceived);
            Assert.Equal(1, receiver.FirstMissingChunk);
            Assert.True(receiver.ResendRequested, "the corrupt chunk is asked for again");
            Assert.SequenceEqual(new byte[] { 0b001 }, receiver.BuildHaveBitmap());
            Assert.False(receiver.ResendRequested, "the have-bitmap answers the request");

            Assert.True(Feed(receiver, Chunk(data, 1)));
            Assert.True(Feed(receiver, Chunk(data, 2)));
            Assert.True(receiver.IsComplete);
            Assert.Equal(-1, receiver.FirstMissingChunk);
            Assert.True(receiver.VerifyDigest());
            Assert.SequenceEqual(data, target.ToArray());
            Assert.Equal(1, receiver.CorruptChunks);
            Assert.False(receiver.IsFailed);
        }

        [Test]
        public void ACorruptLastChunkIsRejected()
        {
            // the short last chunk: a corrupt copy must not complete the blob
            byte[] data = Data(1300, 2);
            var receiver = new BlobReceiver(ManifestOf(data), new MemoryStream(), new ListLog());
            Feed(receiver, Chunk(data, 0));
            Feed(receiver, Chunk(data, 1));
            Assert.False(Feed(receiver, Corrupted(Chunk(data, 2), 275)));
            Assert.False(receiver.IsComplete);
            Assert.Equal(1, receiver.CorruptChunks);
            Assert.Equal(2, receiver.FirstMissingChunk);
        }

        [Test]
        public void AChunkOfAnotherBlobIsRejectedAndNotStored()
        {
            byte[] data = Data(1024, 3);
            byte[] other = Data(1024, 4);
            var target = new MemoryStream();
            var log = new ListLog();
            var receiver = new BlobReceiver(ManifestOf(data), target, log);
            Assert.False(Feed(receiver, Chunk(other, 0, id: Id + 1)), "a well-formed chunk of blob 8 is not stored in blob 7");
            Assert.Equal(1, receiver.RejectedChunks);
            Assert.Equal(0, receiver.CorruptChunks);
            Assert.Equal(0, receiver.ChunksReceived);
            Assert.Equal(0L, receiver.BytesReceived);
            Assert.Equal(0L, target.Length, "nothing was written");
            Assert.False(receiver.ResendRequested, "another blob's chunk says nothing about this one");
            Assert.True(log.Contains("a chunk of blob 8 was delivered here"));

            Assert.True(Feed(receiver, Chunk(data, 0)));
            Assert.True(Feed(receiver, Chunk(data, 1)));
            Assert.True(receiver.VerifyDigest());
            Assert.SequenceEqual(data, target.ToArray());
        }

        [Test]
        public void AConsistentBlobWithOtherContentFailsTheDigest()
        {
            // every chunk is well-formed and well-hashed, but the content is not what the manifest announced
            byte[] announced = Data(1300, 5);
            byte[] sent = Data(1300, 6);
            var log = new ListLog();
            var receiver = new BlobReceiver(ManifestOf(announced), new MemoryStream(), log);
            for (int i = 0; i < 3; i++) Assert.True(Feed(receiver, Chunk(sent, i)));
            Assert.True(receiver.IsComplete);
            Assert.False(receiver.VerifyDigest(), "the XXH3-128 of the whole blob catches it");
            Assert.False(receiver.VerifyDigest(), "the result is cached");
            Assert.True(log.Contains("digest mismatch"));
            Assert.Equal(0, receiver.CorruptChunks);
        }

        [Test]
        public void ASingleWrongByteAnywhereFailsTheDigest()
        {
            // one changed byte in an otherwise identical blob, in the last (short) chunk, which the running digest hashes last
            byte[] announced = Data(1300, 7);
            byte[] sent = (byte[])announced.Clone();
            sent[1299] ^= 1;
            var receiver = new BlobReceiver(ManifestOf(announced), new MemoryStream(), new ListLog());
            for (int i = 0; i < 3; i++) Feed(receiver, Chunk(sent, i));
            Assert.False(receiver.VerifyDigest());
        }

        [Test]
        public void ADigestIsNotVerifiedBeforeTheBlobIsComplete()
        {
            byte[] data = Data(1300, 8);
            var receiver = new BlobReceiver(ManifestOf(data), new MemoryStream(), new ListLog());
            Feed(receiver, Chunk(data, 0));
            Assert.False(receiver.VerifyDigest());
            Feed(receiver, Chunk(data, 1));
            Feed(receiver, Chunk(data, 2));
            Assert.True(receiver.VerifyDigest(), "an early false is not cached");
        }

        [Test]
        public void ChunksAheadOfAGapAreStoredAndReadBackForTheDigest()
        {
            byte[] data = Data(2048, 9); // 4 chunks
            var target = new MemoryStream();
            var receiver = new BlobReceiver(ManifestOf(data), target, new ListLog());
            Assert.True(Feed(receiver, Chunk(data, 0)));
            Assert.True(Feed(receiver, Chunk(data, 2)), "stored ahead of the gap");
            Assert.True(Feed(receiver, Chunk(data, 3)));
            Assert.Equal(1, receiver.FirstMissingChunk);
            Assert.True(receiver.ResendRequested, "the last chunk arrived while chunk 1 is missing");
            Assert.True(Feed(receiver, Chunk(data, 1)));
            Assert.True(receiver.IsComplete);
            Assert.True(receiver.VerifyDigest(), "chunks 2 and 3 are read back from the target");
            Assert.SequenceEqual(data, target.ToArray());
            Assert.Equal(0, receiver.CorruptChunks);
            Assert.Equal(0, receiver.RejectedChunks);
            Assert.Equal(0, receiver.DuplicateChunks);
        }

        [Test]
        public void CorruptThenGoodDuplicatesOfTheChunkNextForTheDigestStillVerify()
        {
            // 0, 2 (ahead of the gap), 1: chunk 2 is now next for the running digest but already stored. A corrupt copy of
            // it must neither advance the digest nor overwrite the stored chunk; a good copy advances the digest.
            byte[] data = Data(2048, 10);
            var target = new MemoryStream();
            var receiver = new BlobReceiver(ManifestOf(data), target, new ListLog());
            Feed(receiver, Chunk(data, 0));
            Feed(receiver, Chunk(data, 2));
            Feed(receiver, Chunk(data, 1));
            Assert.True(Feed(receiver, Corrupted(Chunk(data, 2))), "a duplicate is accepted and ignored");
            Assert.Equal(1, receiver.DuplicateChunks);
            Assert.Equal(0, receiver.CorruptChunks, "a duplicate is not checked against anything stored");
            Assert.True(Feed(receiver, Chunk(data, 2)));
            Assert.Equal(2, receiver.DuplicateChunks);
            Assert.True(Feed(receiver, Chunk(data, 3)));
            Assert.True(receiver.IsComplete);
            Assert.Equal(3L * ChunkSize + 512, receiver.BytesReceived, "duplicates are not counted");
            Assert.True(receiver.VerifyDigest(), "the corrupt duplicate did not reach the digest");
            Assert.SequenceEqual(data, target.ToArray(), "nor the target");
        }

        [Test]
        public void ACorruptDuplicateAloneDoesNotAdvanceTheDigest()
        {
            // as above without the good copy: the digest must come from the stored chunk (read back), not the corrupt copy
            byte[] data = Data(2048, 11);
            var receiver = new BlobReceiver(ManifestOf(data), new MemoryStream(), new ListLog());
            Feed(receiver, Chunk(data, 0));
            Feed(receiver, Chunk(data, 2));
            Feed(receiver, Chunk(data, 1));
            Feed(receiver, Corrupted(Chunk(data, 2)));
            Feed(receiver, Chunk(data, 3));
            Assert.True(receiver.VerifyDigest());
        }

        [Test]
        public void TheLastChunkWhileOthersAreMissingRequestsAResendOnce()
        {
            byte[] data = Data(1300, 12);
            var receiver = new BlobReceiver(ManifestOf(data), new MemoryStream(), new ListLog());
            Feed(receiver, Chunk(data, 0));
            Assert.False(receiver.ResendRequested, "nothing is missing yet as far as the receiver knows");
            Assert.True(Feed(receiver, Chunk(data, 2)));
            Assert.True(receiver.ResendRequested, "the sender's pass reached the end with chunk 1 missing");
            Assert.SequenceEqual(new byte[] { 0b101 }, receiver.BuildHaveBitmap());
            Assert.False(receiver.ResendRequested);
            Assert.True(Feed(receiver, Chunk(data, 2)), "a duplicate of the last chunk");
            Assert.False(receiver.ResendRequested, "is not asked about again while chunk 1 is still the first missing one");
            Assert.False(Feed(receiver, Corrupted(Chunk(data, 1))));
            Assert.False(receiver.ResendRequested, "neither is a corrupt copy of the chunk already asked for");
            Assert.True(Feed(receiver, Chunk(data, 1)));
            Assert.True(receiver.IsComplete);
            Assert.False(receiver.ResendRequested);
            Assert.True(receiver.VerifyDigest());
        }

        [Test]
        public void MalformedChunksCountAsCorrupt()
        {
            byte[] data = Data(1300, 13);
            var receiver = new BlobReceiver(ManifestOf(data), new MemoryStream(), new ListLog());
            Assert.False(receiver.OnChunk(new byte[10], 0, 10), "shorter than the header");
            Assert.Equal(1, receiver.CorruptChunks);
            var w = new ByteWriter();
            BlobCodec.WriteChunkHeader(w, Id, 3, 0, 0);
            Assert.False(receiver.OnChunk(w.Buffer, 0, w.Length), "index 3 of 3 chunks");
            Assert.Equal(2, receiver.CorruptChunks);
            byte[] chunk = Chunk(data, 0);
            Assert.False(receiver.OnChunk(chunk, 0, chunk.Length - 1), "one payload byte short of the declared length");
            Assert.Equal(3, receiver.CorruptChunks);
            w.Reset();
            BlobCodec.WriteChunkHeader(w, Id, 2, 512, 0); // the last chunk holds 276 bytes
            w.WriteBytes(new byte[512], 0, 512);
            Assert.False(receiver.OnChunk(w.Buffer, 0, w.Length), "a last chunk of the wrong length");
            Assert.Equal(4, receiver.CorruptChunks);
            Assert.Equal(0, receiver.ChunksReceived);
            Assert.Equal(0, receiver.RejectedChunks);
            Assert.Throws<ArgumentOutOfRangeException>(() => receiver.OnChunk(chunk, 1, chunk.Length));
        }

        [Test]
        public void ANonSeekableTargetTakesChunksOnlyInOrder()
        {
            byte[] data = Data(1300, 14);
            var target = new ForwardOnlyStream();
            var receiver = new BlobReceiver(ManifestOf(data), target, new ListLog());
            Assert.True(Feed(receiver, Chunk(data, 0)));
            Assert.False(Feed(receiver, Chunk(data, 2)), "ahead of a gap: not stored, and a resend is requested");
            Assert.Equal(1, receiver.RejectedChunks);
            Assert.Equal(0, receiver.CorruptChunks);
            Assert.True(receiver.ResendRequested);
            Assert.True(Feed(receiver, Chunk(data, 2)), "the same again is not news: no second request");
            Assert.Equal(2, receiver.RejectedChunks);
            Assert.Equal(1, receiver.ChunksReceived);
            receiver.BuildHaveBitmap();
            Assert.True(Feed(receiver, Chunk(data, 1)));
            Assert.True(Feed(receiver, Chunk(data, 2)));
            Assert.True(receiver.IsComplete);
            Assert.True(receiver.VerifyDigest(), "every chunk was hashed in order; nothing is read back");
            Assert.SequenceEqual(data, target.Written.ToArray());
        }

        [Test]
        public void AWriteErrorFailsTheReceiverForGood()
        {
            byte[] data = Data(1300, 15);
            var target = new ForwardOnlyStream { FailWrites = true };
            var receiver = new BlobReceiver(ManifestOf(data), target, new ListLog());
            Assert.False(Feed(receiver, Chunk(data, 0)));
            Assert.True(receiver.IsFailed);
            Assert.Contains("cannot write chunk 0", receiver.FailureReason);
            Assert.Contains("disk full", receiver.FailureReason);
            target.FailWrites = false;
            Assert.False(Feed(receiver, Chunk(data, 0)), "a failed receiver accepts nothing more");
            Assert.False(receiver.IsComplete);
            Assert.False(receiver.VerifyDigest());
        }

        [Test]
        public void AnEmptyBlobIsCompleteAndVerifiesAtOnce()
        {
            var receiver = new BlobReceiver(ManifestOf(Array.Empty<byte>()), new MemoryStream(), new ListLog());
            Assert.Equal(0, receiver.ChunkCount);
            Assert.True(receiver.IsComplete);
            Assert.True(receiver.VerifyDigest());
            Assert.Equal(1f, receiver.Fraction);
            Assert.Equal(0.0, receiver.EtaSeconds(0));
        }

        [Test]
        public void ProgressAndEta()
        {
            byte[] data = Data(1024, 16);
            var receiver = new BlobReceiver(ManifestOf(data), new MemoryStream(), new ListLog());
            Assert.True(double.IsPositiveInfinity(receiver.EtaSeconds(0)));
            Feed(receiver, Chunk(data, 0));
            Assert.Equal(0.5f, receiver.Fraction);
            Assert.Equal(2.0, receiver.EtaSeconds(256));
        }

        /// <summary>A write-only stream that cannot seek (like a pipe), optionally failing every write.</summary>
        private sealed class ForwardOnlyStream : Stream
        {
            public readonly MemoryStream Written = new MemoryStream();
            public bool FailWrites;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (FailWrites) throw new IOException("disk full");
                Written.Write(buffer, offset, count);
            }
        }
    }
}
