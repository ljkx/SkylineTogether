using System;
using System.Globalization;
using System.IO;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// Receives one blob (ARCH §7.5): verifies each chunk's xxHash64, writes it at <c>index × chunkSize</c> into the
    /// target stream (a preallocated file or the transient asset's write stream) and never holds the blob in memory.
    /// The XXH3-128 digest is computed while chunks arrive in order (the normal case on the ordered bulk lane), so
    /// <see cref="VerifyDigest"/> usually reads nothing back.
    /// <para>
    /// <see cref="OnChunk"/> takes the body of a <see cref="MessageKind.BlobChunk"/> message (the envelope already
    /// removed). It returns false when the chunk was not accepted: corrupt (hash mismatch, inconsistent header), meant
    /// for another blob, or, on a target that cannot store chunks out of order (not seekable or not readable), arriving
    /// ahead of a missing chunk. A false return and the arrival of the last chunk while others are missing set
    /// <see cref="ResendRequested"/>: send <see cref="BuildHaveBitmap"/> to the sender (<see cref="BlobCodec.WriteHave"/>,
    /// lane R), which resends what is missing. A request is not repeated while the same chunk is still the first
    /// missing one. After a reconnect, always send a have-bitmap: the sender resumes with the missing chunks only.
    /// Duplicates are accepted and ignored.
    /// </para>
    /// <para>
    /// Thread-safe (one lock); <see cref="OnChunk"/> runs on the network thread. A write error of the target fails the
    /// receiver for good (<see cref="IsFailed"/>, logged); the target stream belongs to the caller.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class BlobReceiver
    {
        private const int VerifyBlockBytes = 64 * 1024;

        private readonly object _lock = new object();
        private readonly BlobManifestInfo _manifest;
        private readonly Stream _target;
        private readonly ILog _log;
        private readonly string _name;
        private readonly int _chunkCount;
        private readonly ChunkBitSet _received;
        private readonly XxHash128Stream _hash = new XxHash128Stream();
        private readonly bool _storesOutOfOrder;
        private int _hashedChunks;          // chunks [0, _hashedChunks) are in the running digest
        private long _bytesReceived;
        private long _sequentialPosition;   // where a non-seekable target stands
        private int _corruptChunks;
        private int _rejectedChunks;
        private int _duplicateChunks;
        private int _requestedFirstMissing = -1;
        private bool _resendRequested;
        private bool _failed;
        private string _failureReason;
        private bool _completeLogged;
        private bool _digestChecked;
        private bool _digestMatches;

        public BlobReceiver(BlobManifestInfo manifest, Stream target, ILog log)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            BlobCodec.Validate(manifest);
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (!target.CanWrite) throw new ArgumentException("the target must be writable", nameof(target));
            _manifest = manifest.Clone();
            _target = target;
            _log = log;
            _chunkCount = BlobCodec.ChunkCount(manifest.Size, manifest.ChunkSize);
            _received = new ChunkBitSet(_chunkCount);
            _storesOutOfOrder = target.CanSeek && target.CanRead;
            _name = "blob " + manifest.BlobId.ToString(CultureInfo.InvariantCulture) + " \"" + (manifest.Purpose ?? string.Empty) + "\"";
            _log.Info(LogCategory.Net, _name + ": receiving " + manifest.Size.ToString(CultureInfo.InvariantCulture) + " bytes in "
                                       + _chunkCount.ToString(CultureInfo.InvariantCulture) + " chunk(s)"
                                       + (_storesOutOfOrder ? string.Empty : " (the target only takes chunks in order)"));
            if (_chunkCount == 0) _completeLogged = true;
        }

        /// <summary>
        /// Takes one chunk message body. True: stored, or a harmless duplicate. False: not accepted (see the class
        /// summary); the data is not written.
        /// </summary>
        [ThreadAffinity(ThreadKind.Network)]
        public bool OnChunk(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count), "the range lies outside the buffer");
            lock (_lock)
            {
                if (_failed) return false;
                if (count < BlobCodec.ChunkHeaderBytes)
                {
                    return Corrupt(-1, "a chunk message of " + count.ToString(CultureInfo.InvariantCulture) + " bytes is shorter than its header");
                }
                BlobCodec.ReadChunkHeaderAt(buffer, offset, out uint blobId, out uint rawIndex, out uint rawLength, out ulong hash);
                if (blobId != _manifest.BlobId)
                {
                    return Reject("a chunk of blob " + blobId.ToString(CultureInfo.InvariantCulture) + " was delivered here");
                }
                if (rawIndex >= (uint)_chunkCount)
                {
                    return Corrupt(-1, "chunk index " + rawIndex.ToString(CultureInfo.InvariantCulture) + " of " + _chunkCount.ToString(CultureInfo.InvariantCulture));
                }
                int index = (int)rawIndex;
                int length = BlobCodec.ChunkLength(_manifest.Size, _manifest.ChunkSize, index);
                if (rawLength != (uint)length || count - BlobCodec.ChunkHeaderBytes != length)
                {
                    return Corrupt(index, "chunk " + index.ToString(CultureInfo.InvariantCulture) + " declares " + rawLength.ToString(CultureInfo.InvariantCulture)
                                          + " bytes and carries " + (count - BlobCodec.ChunkHeaderBytes).ToString(CultureInfo.InvariantCulture) + "; expected "
                                          + length.ToString(CultureInfo.InvariantCulture));
                }
                int payloadAt = offset + BlobCodec.ChunkHeaderBytes;
                if (_received.Get(index))
                {
                    _duplicateChunks++;
                    // a chunk stored ahead of a gap that is now next for the digest: its verified copy advances the
                    // running digest without a read-back (a corrupt copy is simply ignored, the chunk is stored)
                    if (index == _hashedChunks && XxHash64.Hash(buffer, payloadAt, length) == hash)
                    {
                        _hash.Append(buffer, payloadAt, length);
                        _hashedChunks++;
                    }
                    return true;
                }
                if (XxHash64.Hash(buffer, payloadAt, length) != hash)
                {
                    return Corrupt(index, "chunk " + index.ToString(CultureInfo.InvariantCulture) + " failed its xxHash64 check");
                }
                bool inOrder = index == _hashedChunks;
                if (!inOrder && !_storesOutOfOrder)
                {
                    _rejectedChunks++;
                    // not corrupt: it will be sent again behind the missing chunk; report it only when that is news
                    return !RequestResend();
                }
                try
                {
                    long position = (long)index * _manifest.ChunkSize;
                    if (_target.CanSeek)
                    {
                        if (_target.Position != position) _target.Position = position;
                    }
                    else if (position != _sequentialPosition)
                    {
                        throw new IOException("the target is not seekable and stands at " + _sequentialPosition.ToString(CultureInfo.InvariantCulture));
                    }
                    _target.Write(buffer, payloadAt, length);
                    _sequentialPosition = position + length;
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    _failed = true;
                    _failureReason = "cannot write chunk " + index.ToString(CultureInfo.InvariantCulture) + ": " + e.Message;
                    _log.Error(LogCategory.Net, _name + ": " + _failureReason, e);
                    return false;
                }
                _received.Set(index);
                _bytesReceived += length;
                if (inOrder)
                {
                    _hash.Append(buffer, payloadAt, length);
                    _hashedChunks++;
                }
                if (_received.IsFull)
                {
                    FinishLocked();
                }
                else if (index == _chunkCount - 1)
                {
                    // the sender's pass reached the end but something is missing
                    RequestResend();
                }
                return true;
            }
        }

        /// <summary>
        /// The have-bitmap (layout of <see cref="BlobCodec"/>): bit i set = chunk i is stored. Clears
        /// <see cref="ResendRequested"/>; the sender resends every chunk without its bit.
        /// </summary>
        public byte[] BuildHaveBitmap()
        {
            lock (_lock)
            {
                _resendRequested = false;
                _requestedFirstMissing = _received.NextClear(0);
                return _received.ToBitmap();
            }
        }

        /// <summary>Every chunk is stored.</summary>
        public bool IsComplete
        {
            get { lock (_lock) return _received.IsFull && !_failed; }
        }

        /// <summary>
        /// True when the stored blob's XXH3-128 equals the manifest's digest (false before completion). Chunks that
        /// arrived ahead of a gap are read back from the target for this (the target must then be readable). The result
        /// is computed once and cached.
        /// </summary>
        public bool VerifyDigest()
        {
            lock (_lock)
            {
                if (_digestChecked) return _digestMatches;
                if (_failed || !_received.IsFull) return false;
                Digest128 actual;
                try
                {
                    _target.Flush();
                    if (_hashedChunks < _chunkCount) HashTailFromTarget();
                    actual = _hash.Finish();
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    _log.Error(LogCategory.Net, _name + ": cannot verify the digest: " + e.Message, e);
                    _digestChecked = true;
                    _digestMatches = false;
                    return false;
                }
                _digestChecked = true;
                _digestMatches = actual == _manifest.Digest;
                if (_digestMatches)
                {
                    _log.Info(LogCategory.Net, _name + ": digest verified (" + actual + ")");
                }
                else
                {
                    _log.Error(LogCategory.Net, _name + ": digest mismatch: received " + actual + ", expected " + _manifest.Digest);
                }
                return _digestMatches;
            }
        }

        /// <summary>Payload bytes stored (duplicates not counted).</summary>
        public long BytesReceived
        {
            get { lock (_lock) return _bytesReceived; }
        }

        /// <summary>Seconds until completion at <paramref name="bytesPerSecond"/>: 0 when complete, infinity for a rate ≤ 0.</summary>
        public double EtaSeconds(double bytesPerSecond)
        {
            long remaining;
            lock (_lock) remaining = _manifest.Size - _bytesReceived;
            if (remaining <= 0) return 0;
            if (double.IsNaN(bytesPerSecond) || bytesPerSecond <= 0) return double.PositiveInfinity;
            return remaining / bytesPerSecond;
        }

        /// <summary>A copy of the manifest.</summary>
        public BlobManifestInfo Manifest => _manifest.Clone();

        /// <summary>Fraction of the blob stored, 0..1 (1 for an empty blob).</summary>
        public float Fraction
        {
            get
            {
                lock (_lock) return _manifest.Size == 0 ? 1f : (float)((double)_bytesReceived / _manifest.Size);
            }
        }

        public int ChunkCount => _chunkCount;

        public int ChunksReceived
        {
            get { lock (_lock) return _received.Count; }
        }

        /// <summary>The first chunk not stored yet, or −1 when complete.</summary>
        public int FirstMissingChunk
        {
            get { lock (_lock) return _received.NextClear(0); }
        }

        /// <summary>A have-bitmap should be sent (see the class summary); cleared by <see cref="BuildHaveBitmap"/>.</summary>
        public bool ResendRequested
        {
            get { lock (_lock) return _resendRequested; }
        }

        /// <summary>Chunks rejected as corrupt or inconsistent.</summary>
        public int CorruptChunks
        {
            get { lock (_lock) return _corruptChunks; }
        }

        /// <summary>Chunks not stored for another reason (another blob, out of order on a target that cannot take them).</summary>
        public int RejectedChunks
        {
            get { lock (_lock) return _rejectedChunks; }
        }

        public int DuplicateChunks
        {
            get { lock (_lock) return _duplicateChunks; }
        }

        /// <summary>Writing to the target failed; the receiver accepts nothing more.</summary>
        public bool IsFailed
        {
            get { lock (_lock) return _failed; }
        }

        public string FailureReason
        {
            get { lock (_lock) return _failureReason; }
        }

        // ---- internals (under _lock) ----

        private bool Corrupt(int index, string what)
        {
            _corruptChunks++;
            RequestResend();
            if (_corruptChunks <= 3 || _corruptChunks % 1000 == 0)
            {
                _log.Warn(LogCategory.Net, _name + ": rejected a chunk: " + what + " (" + _corruptChunks.ToString(CultureInfo.InvariantCulture) + " corrupt so far)");
            }
            return false;
        }

        private bool Reject(string what)
        {
            _rejectedChunks++;
            if (_rejectedChunks <= 3 || _rejectedChunks % 1000 == 0)
            {
                _log.Warn(LogCategory.Net, _name + ": rejected a chunk: " + what + " (" + _rejectedChunks.ToString(CultureInfo.InvariantCulture) + " rejected so far)");
            }
            return false;
        }

        /// <summary>Asks for a have-bitmap unless one was built while the same chunk was the first missing one; true when asked.</summary>
        private bool RequestResend()
        {
            int firstMissing = _received.NextClear(0);
            if (firstMissing < 0) return false;
            if (_requestedFirstMissing >= 0 && firstMissing <= _requestedFirstMissing) return false;
            if (_resendRequested) return false;
            _resendRequested = true;
            return true;
        }

        private void FinishLocked()
        {
            try
            {
                _target.Flush();
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                _failed = true;
                _failureReason = "cannot flush the target: " + e.Message;
                _log.Error(LogCategory.Net, _name + ": " + _failureReason, e);
                return;
            }
            _resendRequested = false;
            if (!_completeLogged)
            {
                _completeLogged = true;
                _log.Info(LogCategory.Net, _name + ": all " + _chunkCount.ToString(CultureInfo.InvariantCulture) + " chunk(s) received ("
                                           + _duplicateChunks.ToString(CultureInfo.InvariantCulture) + " duplicate, "
                                           + _corruptChunks.ToString(CultureInfo.InvariantCulture) + " corrupt)");
            }
        }

        /// <summary>Hashes the chunks that arrived ahead of a gap by reading them back from the target.</summary>
        private void HashTailFromTarget()
        {
            if (!_target.CanRead || !_target.CanSeek)
            {
                throw new IOException("chunks arrived out of order and the target cannot be read back");
            }
            long position = (long)_hashedChunks * _manifest.ChunkSize;
            long remaining = _manifest.Size - position;
            byte[] block = BufferPool.Rent(VerifyBlockBytes);
            try
            {
                _target.Position = position;
                while (remaining > 0)
                {
                    int read = _target.Read(block, 0, (int)Math.Min(block.Length, remaining));
                    if (read <= 0) throw new EndOfStreamException("the target ended " + remaining.ToString(CultureInfo.InvariantCulture) + " byte(s) before the blob size");
                    _hash.Append(block, 0, read);
                    remaining -= read;
                }
            }
            finally
            {
                BufferPool.Return(block);
            }
            _hashedChunks = _chunkCount;
        }
    }
}
