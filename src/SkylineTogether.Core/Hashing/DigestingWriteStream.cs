using System;
using System.IO;

namespace SkylineTogether.Core.Hashing
{
    /// <summary>
    /// A write-only, forward-only stream in front of another stream that computes the XXH3-128 digest
    /// (<see cref="Digest128"/>) and the length of everything written through it, so the host knows a snapshot's digest
    /// the moment the game's save finishes instead of reading the whole file again (once per joiner, before).
    /// <para>
    /// The game's save writes with <c>StreamBinaryWriter.WriteBytes</c> (sequential <c>Write</c> calls from a job
    /// thread, then <c>Dispose</c>); it never seeks, which is required here: <see cref="CanSeek"/> is false and seeking
    /// throws. Writes are serialized by a lock, so <see cref="Finish"/> and <see cref="BytesWritten"/> may be read from
    /// another thread once the writer is done. Disposing disposes the inner stream unless <c>leaveOpen</c>.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class DigestingWriteStream : Stream
    {
        private readonly object _lock = new object();
        private readonly Stream _inner;
        private readonly bool _leaveOpen;
        private readonly XxHash128Stream _hash = new XxHash128Stream();
        private long _written;
        private bool _disposed;

        public DigestingWriteStream(Stream inner, bool leaveOpen = false)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (!inner.CanWrite) throw new ArgumentException("the inner stream must be writable", nameof(inner));
            _leaveOpen = leaveOpen;
        }

        /// <summary>Bytes written so far.</summary>
        public long BytesWritten
        {
            get { lock (_lock) return _written; }
        }

        /// <summary>The digest of everything written so far (the state is not changed; more writes may follow).</summary>
        public Digest128 Finish()
        {
            lock (_lock) return _hash.Finish();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            lock (_lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(DigestingWriteStream));
                _inner.Write(buffer, offset, count);
                _hash.Append(buffer, offset, count);
                _written += count;
            }
        }

        public override void WriteByte(byte value)
        {
            Write(new[] { value }, 0, 1);
        }

        public override void Flush()
        {
            lock (_lock)
            {
                if (!_disposed) _inner.Flush();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_lock)
                {
                    if (!_disposed)
                    {
                        _disposed = true;
                        if (!_leaveOpen) _inner.Dispose();
                    }
                }
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => BytesWritten;

        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException("a digesting stream is forward-only");
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("a digesting stream is write-only");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("a digesting stream is forward-only");

        public override void SetLength(long value) => throw new NotSupportedException("a digesting stream is forward-only");
    }
}
