using System;
using System.Globalization;
using System.IO;
using System.Text;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// Sends one blob (a snapshot, a domain rebaseline, a resume point, a tile batch) as indexed chunks on lane B
    /// (ARCHITECTURE.md#blob-transfer). Each chunk is a complete uncompressed <see cref="MessageKind.BlobChunk"/> envelope (see
    /// <see cref="BlobCodec"/>) of at most 256 KiB, read from the seekable source when it is sent, so the blob is never
    /// held in memory.
    /// <para>
    /// Flow: <see cref="Start(uint, Stream, string)"/> hashes the source (XXH3-128) and returns the manifest, which the
    /// caller delivers on lane R (a <see cref="MessageKind.BlobManifest"/> message or the snapshot manifest) so the
    /// receiver can create its <see cref="BlobReceiver"/>; the sender streams from the first <see cref="Pump"/> on, so
    /// start pumping once the receiver is ready. <see cref="Pump"/> keeps the bulk bytes queued in the outbox plus the
    /// transport's pending bytes within an adaptive window of <c>minWindowBytes</c>-<c>maxWindowBytes</c> (about one
    /// second at the reported send rate), which bounds memory and always leaves headroom for lane R.
    /// </para>
    /// <para>
    /// Resume: a have-bitmap from the receiver (<see cref="OnHave"/>) is authoritative: chunks it holds are never sent
    /// again, every other chunk is (re)sent in index order. After a reconnect, <see cref="Rebind"/> moves the sender to
    /// the new connection's outbox and waits for the receiver's have-bitmap, so only missing chunks cross the link
    /// again. A have-bitmap mid-transfer may cause chunks still in flight to be sent twice; the receiver ignores
    /// duplicates.
    /// </para>
    /// <para>
    /// Thread-safe (one lock); <see cref="Pump"/> runs on the network thread. Stream errors never escape
    /// <see cref="Pump"/>: they fail the transfer (<see cref="IsFailed"/>, logged). The source stream belongs to the
    /// caller, must not be shared with another sender (the sender seeks it) and must stay open until the transfer ends.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class BlobSender
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        private readonly object _lock = new object();
        private readonly ILog _log;
        private readonly int _minWindow;
        private readonly int _maxWindow;
        private readonly ByteWriter _message = new ByteWriter(2 + BlobCodec.ChunkHeaderBytes + Limits.MaxBulkChunkBytes);
        private PeerOutbox _output;
        private BlobManifestInfo _manifest;
        private Stream _source;
        private string _name = "blob sender";
        private int _chunkCount;
        private ChunkBitSet _toSend;      // set: the chunk must be (re)sent
        private ChunkBitSet _sentOnce;
        private int _cursor;              // lowest index that may be set in _toSend
        private long _toSendBytes;
        private long _totalSentBytes;
        private long _chunkMessagesSent;
        private int _resentChunks;
        private int _havesReceived;
        private int _window;
        private bool _started;
        private bool _cancelled;
        private bool _failed;
        private bool _awaitingHave;
        private bool _pausedLogged;
        private bool _completeLogged;
        private string _failureReason;
        private double _digestMilliseconds;

        /// <param name="output">The receiver's outbox; chunks go through its bulk lane window.</param>
        /// <param name="log">Logger (may be null).</param>
        /// <param name="minWindowBytes">Smallest in-flight window (bulk bytes queued in the outbox plus the transport's pending bytes).</param>
        /// <param name="maxWindowBytes">Largest in-flight window.</param>
        public BlobSender(PeerOutbox output, ILog log, int minWindowBytes = 2 << 20, int maxWindowBytes = 8 << 20)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
            if (minWindowBytes <= 0) throw new ArgumentOutOfRangeException(nameof(minWindowBytes), minWindowBytes, "must be positive");
            if (maxWindowBytes < minWindowBytes) throw new ArgumentOutOfRangeException(nameof(maxWindowBytes), maxWindowBytes, "must not be below minWindowBytes");
            _log = log;
            _minWindow = minWindowBytes;
            _maxWindow = maxWindowBytes;
            _window = minWindowBytes;
        }

        /// <summary>Starts a transfer in chunks of <see cref="BlobCodec.DefaultChunkBytes"/> (256 KiB, ARCHITECTURE.md#blob-transfer).</summary>
        public BlobManifestInfo Start(uint blobId, Stream source, string purpose)
        {
            return Start(blobId, source, purpose, BlobCodec.DefaultChunkBytes);
        }

        /// <summary>
        /// Hashes the whole source (it must be readable and seekable; it is read from position 0 to its length, which
        /// takes a moment for a large blob) and prepares every chunk for sending. Returns a copy of the manifest.
        /// Stream errors propagate. A sender transfers one blob: a second call throws <see cref="InvalidOperationException"/>.
        /// </summary>
        public BlobManifestInfo Start(uint blobId, Stream source, string purpose, int chunkSize)
        {
            return Start(blobId, source, purpose, chunkSize, null);
        }

        /// <summary>
        /// Like <see cref="Start(uint, Stream, string, int)"/>, but with the source's digest already known
        /// (computed while the file was written, or for an earlier receiver of the same file), so nothing is read
        /// before the first chunk. The caller vouches that <paramref name="knownDigest"/> is the XXH3-128 of the whole
        /// source; a wrong one makes the receiver reject the blob at the end. Null hashes the source as before.
        /// </summary>
        public BlobManifestInfo Start(uint blobId, Stream source, string purpose, int chunkSize, Digest128? knownDigest)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!source.CanRead || !source.CanSeek) throw new ArgumentException("the source must be readable and seekable", nameof(source));
            if (chunkSize < BlobCodec.MinChunkBytes || chunkSize > Limits.MaxBulkChunkBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkSize), chunkSize, BlobCodec.MinChunkBytes.ToString(CultureInfo.InvariantCulture) + " ... " + Limits.MaxBulkChunkBytes.ToString(CultureInfo.InvariantCulture));
            }
            purpose = purpose ?? string.Empty;
            if (Utf8.GetByteCount(purpose) > BlobCodec.MaxPurposeBytes) throw new ArgumentException("the purpose label exceeds " + BlobCodec.MaxPurposeBytes.ToString(CultureInfo.InvariantCulture) + " UTF-8 bytes", nameof(purpose));
            long size = source.Length;
            if (size > BlobCodec.MaxBlobBytes) throw new ArgumentException("the blob exceeds " + BlobCodec.MaxBlobBytes.ToString(CultureInfo.InvariantCulture) + " bytes", nameof(source));

            lock (_lock)
            {
                if (_started) throw new InvalidOperationException("a BlobSender transfers one blob; it was already started");
                Digest128 digest;
                if (knownDigest.HasValue)
                {
                    digest = knownDigest.Value;
                    _digestMilliseconds = 0;
                }
                else
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    digest = HashSource(source, size);
                    _digestMilliseconds = watch.Elapsed.TotalMilliseconds;
                }
                _manifest = new BlobManifestInfo { BlobId = blobId, Size = size, ChunkSize = chunkSize, Digest = digest, Purpose = purpose };
                _chunkCount = BlobCodec.ChunkCount(size, chunkSize);
                _toSend = new ChunkBitSet(_chunkCount);
                _toSend.SetAll();
                _sentOnce = new ChunkBitSet(_chunkCount);
                _toSendBytes = size;
                _cursor = 0;
                _source = source;
                _started = true;
                _name = "blob " + blobId.ToString(CultureInfo.InvariantCulture) + " \"" + purpose + "\" to peer " + _output.Peer.Value.ToString(CultureInfo.InvariantCulture);
                _log.Info(LogCategory.Net, _name + ": sending " + size.ToString(CultureInfo.InvariantCulture) + " bytes in "
                                           + _chunkCount.ToString(CultureInfo.InvariantCulture) + " chunk(s) of " + chunkSize.ToString(CultureInfo.InvariantCulture)
                                           + " bytes, digest " + digest
                                           + (knownDigest.HasValue ? " (known)" : " (hashed in " + _digestMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms)"));
                return _manifest.Clone();
            }
        }

        /// <summary>
        /// Hands chunks to the outbox's bulk lane while the bulk bytes queued there plus the transport's pending bytes
        /// stay below the window. Does nothing before <see cref="Start(uint, Stream, string)"/>, after
        /// <see cref="Cancel"/> or a failure, while the outbox is closed (until <see cref="Rebind"/>) and while waiting
        /// for a have-bitmap after <see cref="Rebind"/>.
        /// </summary>
        [ThreadAffinity(ThreadKind.Network)]
        public void Pump()
        {
            lock (_lock)
            {
                if (!_started || _cancelled || _failed || _awaitingHave || _toSend.Count == 0) return;
                PeerOutbox output = _output;
                if (output.IsClosed)
                {
                    if (!_pausedLogged)
                    {
                        _pausedLogged = true;
                        _log.Info(LogCategory.Net, _name + ": paused, the connection is closed (" + _toSend.Count.ToString(CultureInfo.InvariantCulture)
                                                   + " chunk(s) left); a reconnect resumes it");
                    }
                    return;
                }
                ConnectionStats stats = output.Stats;
                _window = BulkWindow.Compute(stats.SendRateBytesPerSecond, _minWindow, _maxWindow);
                long inFlight = output.QueuedBulkBytes + Math.Max(0, stats.PendingReliableBytes);
                while (inFlight < _window)
                {
                    int index = _toSend.NextSet(_cursor);
                    if (index < 0)
                    {
                        index = _toSend.NextSet(0);
                        if (index < 0) break;
                    }
                    if (!BuildChunk(index, out int payload)) return;
                    SendResult result = output.Send(Lane.Bulk, Delivery.ReliableOrdered, _message.Buffer, 0, _message.Length);
                    if (result != SendResult.Ok)
                    {
                        if (result != SendResult.Closed) Fail("the outbox refused a chunk (" + result + ")", null);
                        return;
                    }
                    _toSend.Clear(index);
                    _toSendBytes -= payload;
                    _cursor = index + 1;
                    if (!_sentOnce.Set(index)) _resentChunks++;
                    _chunkMessagesSent++;
                    _totalSentBytes += payload;
                    inFlight += _message.Length;
                }
                if (_toSend.Count == 0 && !_completeLogged)
                {
                    _completeLogged = true;
                    _log.Info(LogCategory.Net, _name + ": every chunk is handed to the transport (" + _chunkMessagesSent.ToString(CultureInfo.InvariantCulture)
                                               + " chunk message(s), " + _resentChunks.ToString(CultureInfo.InvariantCulture) + " resent)");
                }
            }
        }

        /// <summary>
        /// Applies the receiver's have-bitmap (layout of <see cref="BlobCodec"/>): held chunks are never sent again, all
        /// others are (re)sent in index order from the lowest. Ends the wait after <see cref="Rebind"/>. Throws
        /// <see cref="DecodeException"/> when the bitmap does not fit this blob (wrong length or bits beyond the last
        /// chunk); before <see cref="Start(uint, Stream, string)"/> and after <see cref="Cancel"/> the bitmap is ignored.
        /// </summary>
        public void OnHave(byte[] bitmap, int offset, int count)
        {
            if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));
            if (offset < 0 || count < 0 || offset > bitmap.Length - count) throw new ArgumentOutOfRangeException(nameof(count), "the range lies outside the bitmap");
            lock (_lock)
            {
                if (!_started || _cancelled)
                {
                    _log.Debug(LogCategory.Net, _name + ": ignored a have-bitmap (" + (_started ? "cancelled" : "not started") + ")");
                    return;
                }
                int expected = BlobCodec.HaveBitmapBytes(_chunkCount);
                if (count != expected)
                {
                    throw new DecodeException(_name + ": a have-bitmap of " + count.ToString(CultureInfo.InvariantCulture) + " bytes for "
                                              + _chunkCount.ToString(CultureInfo.InvariantCulture) + " chunks (expected " + expected.ToString(CultureInfo.InvariantCulture) + ")");
                }
                int tail = _chunkCount & 7;
                if (tail != 0 && (bitmap[offset + count - 1] >> tail) != 0)
                {
                    throw new DecodeException(_name + ": the have-bitmap sets bits beyond the last chunk");
                }
                int held = 0, requeued = 0, firstMissing = -1;
                for (int i = 0; i < _chunkCount; i++)
                {
                    if (ChunkBitSet.BitmapGet(bitmap, offset, i))
                    {
                        held++;
                        if (_toSend.Clear(i)) _toSendBytes -= ChunkLength(i);
                    }
                    else
                    {
                        if (firstMissing < 0) firstMissing = i;
                        if (_toSend.Set(i))
                        {
                            _toSendBytes += ChunkLength(i);
                            requeued++;
                        }
                    }
                }
                if (firstMissing >= 0 && firstMissing < _cursor) _cursor = firstMissing;
                bool wasWaiting = _awaitingHave;
                _awaitingHave = false;
                if (_toSend.Count > 0) _completeLogged = false;
                // rate-limited: a receiver may send a have-bitmap every half second
                _havesReceived++;
                bool logIt = wasWaiting || (requeued > 0 && (_havesReceived <= 5 || _havesReceived % 50 == 0));
                if (logIt) _log.Info(LogCategory.Net, _name + ": have-bitmap: the receiver holds " + held.ToString(CultureInfo.InvariantCulture) + " of "
                                           + _chunkCount.ToString(CultureInfo.InvariantCulture) + " chunk(s); " + _toSend.Count.ToString(CultureInfo.InvariantCulture)
                                           + " to send (" + requeued.ToString(CultureInfo.InvariantCulture) + " requeued)" + (wasWaiting ? "; resuming" : string.Empty));
            }
        }

        /// <summary>
        /// Moves the transfer to the outbox of a new connection to the same receiver (after a reconnect within the
        /// resume window, ARCHITECTURE.md#blob-transfer). Sending waits until the receiver's have-bitmap arrives (<see cref="OnHave"/>).
        /// </summary>
        public void Rebind(PeerOutbox output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            lock (_lock)
            {
                _output = output;
                _awaitingHave = _started;
                _pausedLogged = false;
                _log.Info(LogCategory.Net, _name + ": moved to connection " + output.Connection.Id.ToString(CultureInfo.InvariantCulture)
                                           + "; waiting for the receiver's have-bitmap");
            }
        }

        /// <summary>Stops sending; the transfer cannot be restarted (start a new sender).</summary>
        public void Cancel()
        {
            lock (_lock)
            {
                if (_cancelled) return;
                _cancelled = true;
                if (_started) _log.Info(LogCategory.Net, _name + ": cancelled with " + (_toSend?.Count ?? 0).ToString(CultureInfo.InvariantCulture) + " chunk(s) unsent");
            }
        }

        /// <summary>
        /// Every chunk has been handed to the outbox at least once since the last have-bitmap and none waits for a
        /// resend. The receiver's <see cref="BlobReceiver.IsComplete"/> and <see cref="BlobReceiver.VerifyDigest"/>
        /// confirm the end-to-end result.
        /// </summary>
        public bool IsComplete
        {
            get { lock (_lock) return _started && !_cancelled && !_failed && _toSend.Count == 0; }
        }

        /// <summary>Progress: blob bytes handed over and not waiting for a resend (resends after a have-bitmap move it back).</summary>
        public long BytesSent
        {
            get { lock (_lock) return _started ? _manifest.Size - _toSendBytes : 0; }
        }

        /// <summary>A copy of the manifest, or null before <see cref="Start(uint, Stream, string)"/>.</summary>
        public BlobManifestInfo Manifest
        {
            get { lock (_lock) return _manifest?.Clone(); }
        }

        public bool IsStarted
        {
            get { lock (_lock) return _started; }
        }

        public bool IsCancelled
        {
            get { lock (_lock) return _cancelled; }
        }

        /// <summary>Reading the source failed (or the outbox refused a chunk for good); see <see cref="FailureReason"/>.</summary>
        /// <summary>The failure is the sender's own: its source (the host's snapshot file) could not be read.</summary>
        public bool SourceFailed { get; private set; }

        public bool IsFailed
        {
            get { lock (_lock) return _failed; }
        }

        public string FailureReason
        {
            get { lock (_lock) return _failureReason; }
        }

        /// <summary>Waiting for the receiver's have-bitmap after <see cref="Rebind"/>.</summary>
        public bool IsWaitingForHave
        {
            get { lock (_lock) return _awaitingHave; }
        }

        public int ChunkCount
        {
            get { lock (_lock) return _chunkCount; }
        }

        /// <summary>Chunks waiting to be (re)sent.</summary>
        public int ChunksRemaining
        {
            get { lock (_lock) return _toSend?.Count ?? 0; }
        }

        /// <summary>Chunks sent more than once.</summary>
        public int ResentChunks
        {
            get { lock (_lock) return _resentChunks; }
        }

        /// <summary>Payload bytes handed over, resends included.</summary>
        public long TotalBytesSent
        {
            get { lock (_lock) return _totalSentBytes; }
        }

        /// <summary>Milliseconds <see cref="Start(uint, Stream, string, int, Digest128?)"/> spent hashing the source (0 with a known digest).</summary>
        public double DigestMilliseconds
        {
            get { lock (_lock) return _digestMilliseconds; }
        }

        /// <summary>The in-flight window of the last pump.</summary>
        public int CurrentWindowBytes
        {
            get { lock (_lock) return _window; }
        }

        // ---- internals ----

        private int ChunkLength(int index) => BlobCodec.ChunkLength(_manifest.Size, _manifest.ChunkSize, index);

        /// <summary>Reads chunk <paramref name="index"/> into the message writer and frames it; false (and failed) on a stream error.</summary>
        private bool BuildChunk(int index, out int length)
        {
            length = ChunkLength(index);
            _message.Reset();
            _message.WriteByte((byte)MessageKind.BlobChunk);
            _message.WriteByte((byte)EnvelopeFlags.None);
            int headerAt = _message.Reserve(BlobCodec.ChunkHeaderBytes);
            int payloadAt = _message.Reserve(length);
            byte[] buffer = _message.Buffer;
            try
            {
                _source.Position = (long)index * _manifest.ChunkSize;
                ReadExactly(_source, buffer, payloadAt, length);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                SourceFailed = true;
                Fail("cannot read chunk " + index.ToString(CultureInfo.InvariantCulture) + " from the source", e);
                return false;
            }
            ulong hash = XxHash64.Hash(buffer, payloadAt, length);
            BlobCodec.WriteChunkHeaderAt(buffer, headerAt, _manifest.BlobId, index, length, hash);
            return true;
        }

        private void Fail(string reason, Exception exception)
        {
            _failed = true;
            _failureReason = exception == null ? reason : reason + ": " + exception.Message;
            _log.Error(LogCategory.Net, _name + ": transfer failed: " + reason, exception);
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read <= 0) throw new EndOfStreamException("the source ended " + count.ToString(CultureInfo.InvariantCulture) + " byte(s) early (did it change after Start?)");
                offset += read;
                count -= read;
            }
        }

        private static Digest128 HashSource(Stream source, long size)
        {
            var hasher = new XxHash128Stream();
            byte[] chunk = BufferPool.Rent(64 * 1024);
            try
            {
                source.Position = 0;
                long remaining = size;
                while (remaining > 0)
                {
                    int read = source.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));
                    if (read <= 0) throw new EndOfStreamException("the source ended " + remaining.ToString(CultureInfo.InvariantCulture) + " byte(s) before its length");
                    hasher.Append(chunk, 0, read);
                    remaining -= read;
                }
            }
            finally
            {
                BufferPool.Return(chunk);
            }
            return hasher.Finish();
        }
    }
}
