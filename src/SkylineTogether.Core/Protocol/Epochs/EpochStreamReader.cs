using System;
using System.Globalization;
using System.IO;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Reads an epoch message from a stream section by section, so a large complete epoch from
    /// <see cref="EpochPartAssembler"/> (up to <see cref="Limits.MaxEpochTotalBytes"/>, possibly spilled to a temp file)
    /// is processed with buffers no larger than one section: every section body is at most
    /// <see cref="Limits.MaxUncompressedBytes"/>, and the TX records of a multi-part epoch arrive as several TX sections,
    /// each split at record boundaries, whose records form one sequence in order.
    /// <para>
    /// The stream must contain exactly one epoch message from its current position to its end; truncation, an
    /// undefined section type, an oversized section or trailing bytes throw <see cref="DecodeException"/>. I/O errors of
    /// the stream propagate unchanged. After a <see cref="DecodeException"/> or an I/O error the epoch is abandoned:
    /// every further call throws <see cref="DecodeException"/> until the next <see cref="Open"/>, so a section is never
    /// read from a misaligned position. Not thread-safe.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class EpochStreamReader
    {
        private readonly byte[] _small = new byte[EpochFormat.MaxHeaderBytes];
        private Stream _stream;
        private int _remainingSections;
        private int _pendingBody;
        private bool _failed;

        /// <summary>The header read by <see cref="Open"/>.</summary>
        public EpochHeader Header { get; private set; }

        /// <summary>Reads and validates the header at the stream's current position.</summary>
        public EpochHeader Open(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            _stream = null;
            _pendingBody = 0;
            _remainingSections = 0;
            _failed = false;
            ReadExactly(stream, _small, 0, EpochFormat.FixedHeaderBytes, "epoch header");
            uint epoch = EpochFormat.ReadUInt32(_small, 0);
            uint hostTick = EpochFormat.ReadUInt32(_small, 4);
            var flags = (EpochFlags)_small[8];
            int sectionCount = _small[EpochFormat.SectionCountOffset];
            EpochFormat.ValidateFlags(flags);
            int partIndex = 0, partCount = 1;
            if ((flags & EpochFlags.MultiPart) != 0)
            {
                uint index = ReadVarUInt32(stream, "epoch part index");
                uint count = ReadVarUInt32(stream, "epoch part count");
                EpochFormat.ValidateParts(index, count);
                partIndex = (int)index;
                partCount = (int)count;
            }
            var header = new EpochHeader
            {
                Epoch = new EpochId(epoch),
                HostTick = hostTick,
                Flags = flags,
                SectionCount = sectionCount,
                PartIndex = partIndex,
                PartCount = partCount
            };
            _stream = stream;
            _remainingSections = header.SectionCount;
            Header = header;
            return header;
        }

        /// <summary>
        /// Reads the next section header; the body follows and must be read with <see cref="ReadBody"/> or skipped with
        /// <see cref="SkipBody"/> (it is skipped automatically by the next call). Returns false after the last
        /// section, once the end of the stream has been verified.
        /// </summary>
        public bool NextSection(out SectionType type, out int length)
        {
            if (_stream == null) throw new InvalidOperationException("NextSection without Open");
            ThrowIfFailed();
            type = default;
            length = 0;
            try
            {
                return ReadSectionHeader(out type, out length);
            }
            catch
            {
                _failed = true;
                throw;
            }
        }

        private bool ReadSectionHeader(out SectionType type, out int length)
        {
            if (_pendingBody > 0) SkipBody();
            type = default;
            length = 0;
            if (_remainingSections == 0)
            {
                if (_stream.ReadByte() >= 0) throw new DecodeException("epoch: trailing bytes after the declared sections");
                return false;
            }
            int typeByte = _stream.ReadByte();
            if (typeByte < 0) throw new DecodeException("epoch: stream ends before the declared sections");
            if (!EpochFormat.IsDefinedSection((byte)typeByte)) throw new DecodeException("epoch: unknown section type " + typeByte.ToString(CultureInfo.InvariantCulture));
            uint size = ReadVarUInt32(_stream, "epoch section length");
            if (size > (uint)Limits.MaxUncompressedBytes)
            {
                throw new DecodeException("epoch: section of " + WireFormat.Format(size) + " bytes exceeds the part limit");
            }
            _remainingSections--;
            _pendingBody = (int)size;
            type = (SectionType)typeByte;
            length = (int)size;
            return true;
        }

        /// <summary>Reads the pending section body into <paramref name="destination"/> at <paramref name="offset"/>.</summary>
        public void ReadBody(byte[] destination, int offset)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (offset < 0 || offset > destination.Length - _pendingBody) throw new ArgumentOutOfRangeException(nameof(offset), "the destination has no room for the section");
            if (_pendingBody == 0) return;
            ThrowIfFailed();
            try
            {
                ReadExactly(_stream, destination, offset, _pendingBody, "epoch section");
            }
            catch
            {
                _failed = true;
                throw;
            }
            _pendingBody = 0;
        }

        /// <summary>Skips the pending section body.</summary>
        public void SkipBody()
        {
            if (_pendingBody == 0) return;
            ThrowIfFailed();
            try
            {
                SkipPendingBody();
            }
            catch
            {
                _failed = true;
                throw;
            }
        }

        private void SkipPendingBody()
        {
            if (_stream.CanSeek)
            {
                long target = _stream.Position + _pendingBody;
                if (target > _stream.Length) throw new DecodeException("epoch: stream ends inside a section");
                _stream.Position = target;
            }
            else
            {
                var scratch = new byte[Math.Min(_pendingBody, 81920)];
                int left = _pendingBody;
                while (left > 0)
                {
                    int n = Math.Min(left, scratch.Length);
                    ReadExactly(_stream, scratch, 0, n, "epoch section");
                    left -= n;
                }
            }
            _pendingBody = 0;
        }

        private void ThrowIfFailed()
        {
            if (_failed) throw new DecodeException("epoch: an earlier read of this epoch failed; open it again");
        }

        private uint ReadVarUInt32(Stream stream, string what)
        {
            int count = 0;
            for (;;)
            {
                int b = stream.ReadByte();
                if (b < 0) throw new DecodeException(what + ": stream ends inside a varint");
                _small[count++] = (byte)b;
                if ((b & 0x80) == 0 || count == 5) break;
            }
            int position = 0;
            return WireFormat.ReadVarUInt32(_small, ref position, count, what);
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count, string what)
        {
            while (count > 0)
            {
                int n = stream.Read(buffer, offset, count);
                if (n <= 0) throw new DecodeException(what + ": stream ends " + count.ToString(CultureInfo.InvariantCulture) + " byte(s) early");
                offset += n;
                count -= n;
            }
        }
    }
}
