using System;
using System.IO;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// A read-only, seekable stream over an in-memory head followed by ranges of a spill file, in order: the complete
    /// epoch of <see cref="EpochPartAssembler"/> after it spilled to disk (the parts may lie in the file in arrival
    /// order). Owns the file: disposing the stream closes it, and the file is deleted (it was opened with
    /// <see cref="FileOptions.DeleteOnClose"/>; a plain delete is attempted as well).
    /// </summary>
    internal sealed class SegmentedReadStream : Stream
    {
        private readonly byte[] _head;
        private readonly FileStream _file;
        private readonly string _path;
        private readonly long[] _fileOffsets;
        private readonly int[] _lengths;
        private readonly long[] _starts;       // stream position of each file range
        private readonly long _length;
        private long _position;
        private bool _disposed;

        internal SegmentedReadStream(byte[] head, FileStream file, string path, long[] fileOffsets, int[] lengths)
        {
            _head = head;
            _file = file;
            _path = path;
            _fileOffsets = fileOffsets;
            _lengths = lengths;
            _starts = new long[lengths.Length];
            long position = head.Length;
            for (int i = 0; i < lengths.Length; i++)
            {
                _starts[i] = position;
                position += lengths[i];
            }
            _length = position;
        }

        public override bool CanRead => !_disposed;

        public override bool CanSeek => !_disposed;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                ThrowIfDisposed();
                return _length;
            }
        }

        public override long Position
        {
            get
            {
                ThrowIfDisposed();
                return _position;
            }
            set
            {
                ThrowIfDisposed();
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            WireFormat.CheckRange(buffer, offset, count, nameof(buffer));
            if (count == 0 || _position >= _length) return 0;
            if (_position < _head.Length)
            {
                int n = (int)Math.Min(count, _head.Length - _position);
                System.Buffer.BlockCopy(_head, (int)_position, buffer, offset, n);
                _position += n;
                return n;
            }
            int segment = FindSegment(_position);
            long within = _position - _starts[segment];
            int wanted = (int)Math.Min(count, _lengths[segment] - within);
            _file.Position = _fileOffsets[segment] + within;
            int read = _file.Read(buffer, offset, wanted);
            if (read <= 0) throw new IOException("the epoch spill file is shorter than expected");
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();
            long target;
            switch (origin)
            {
                case SeekOrigin.Begin: target = offset; break;
                case SeekOrigin.Current: target = _position + offset; break;
                case SeekOrigin.End: target = _length + offset; break;
                default: throw new ArgumentOutOfRangeException(nameof(origin));
            }
            if (target < 0) throw new IOException("seek before the start of the stream");
            _position = target;
            return target;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing) _file.Dispose();
                try
                {
                    if (File.Exists(_path)) File.Delete(_path);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            base.Dispose(disposing);
        }

        private int FindSegment(long position)
        {
            int low = 0, high = _starts.Length - 1;
            while (low < high)
            {
                int mid = (low + high + 1) >> 1;
                if (_starts[mid] <= position) low = mid;
                else high = mid - 1;
            }
            return low;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SegmentedReadStream));
        }
    }
}
