using System;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Reads an epoch message written by <see cref="EpochWriter"/> (one part, or a complete epoch from
    /// <see cref="EpochPartAssembler"/>). <see cref="Open"/> validates the whole framing before anything is returned:
    /// the header (known flags, part numbering), every section header (defined type, body inside the message and at
    /// most <see cref="Limits.MaxUncompressedBytes"/>) and that the message ends exactly after the declared sections.
    /// A malformed message therefore throws <see cref="DecodeException"/> from <see cref="Open"/> and never part-way
    /// through. Record-level validation happens in <see cref="RecordReader"/>.
    /// <para>Not thread-safe; one instance per thread.</para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class EpochReader
    {
        private byte[] _buffer;
        private int _position;
        private int _end;
        private int _remaining;

        /// <summary>The header of the open message.</summary>
        public EpochHeader Header { get; private set; }

        /// <summary>Absolute offset of the first section of the open message.</summary>
        public int SectionsOffset { get; private set; }

        /// <summary>Validates <c>buffer[offset .. offset+count-1]</c> and positions the reader before its first section.</summary>
        public EpochHeader Open(byte[] buffer, int offset, int count)
        {
            WireFormat.CheckRange(buffer, offset, count, nameof(buffer));
            _buffer = null;
            _remaining = 0;
            int headerLength = EpochFormat.ParseHeader(buffer, offset, count, out EpochHeader header);
            EpochFormat.ValidateSections(buffer, offset + headerLength, offset + count, header.SectionCount);
            _buffer = buffer;
            _position = offset + headerLength;
            _end = offset + count;
            _remaining = header.SectionCount;
            Header = header;
            SectionsOffset = _position;
            return header;
        }

        /// <summary>
        /// The next section: its type and the absolute offset and length of its body in the opened buffer. False after
        /// the last section.
        /// </summary>
        public bool NextSection(out SectionType type, out int bodyOffset, out int bodyCount)
        {
            if (_buffer == null || _remaining == 0)
            {
                type = default;
                bodyOffset = 0;
                bodyCount = 0;
                return false;
            }
            // validated by Open, so this cannot throw
            EpochFormat.ReadSectionHeader(_buffer, ref _position, _end, out type, out bodyCount);
            bodyOffset = _position;
            _position += bodyCount;
            _remaining--;
            return true;
        }
    }
}
