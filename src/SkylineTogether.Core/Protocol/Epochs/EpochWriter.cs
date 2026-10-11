using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Writes one epoch message (ARCH §4.6.2):
    /// <code>
    /// u32 epoch | u32 hostTick | u8 flags | u8 sectionCount | [MultiPart: varint partIndex, varint partCount]
    /// repeat sectionCount: u8 sectionType | varint byteLength | payload
    /// </code>
    /// The message is appended to the output at its current position; the section count is patched by
    /// <see cref="End"/>. A message never exceeds <see cref="Limits.MaxUncompressedBytes"/>: a section that would
    /// overflow it is refused before anything is written (a larger transaction goes through
    /// <see cref="EpochPartSplitter"/>). Sections of one type may repeat; a reader treats their records as one sequence
    /// in order.
    /// <para>Not thread-safe; one instance per thread.</para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class EpochWriter
    {
        private ByteWriter _output;
        private int _start;
        private int _sections;
        private bool _open;

        /// <summary>True between <see cref="Begin"/> and <see cref="End"/>.</summary>
        public bool IsOpen => _open;

        /// <summary>Length of the message written so far (header included).</summary>
        public int Length => _open ? _output.Length - _start : 0;

        /// <summary>
        /// Starts a message in <paramref name="output"/>. <see cref="EpochFlags.MultiPart"/> is set when
        /// <paramref name="partCount"/> is above 1 and cleared otherwise, whatever <paramref name="flags"/> says.
        /// </summary>
        public void Begin(EpochId epoch, uint hostTick, EpochFlags flags, ByteWriter output, int partIndex = 0, int partCount = 1)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (_open) throw new InvalidOperationException("Begin while an epoch message is open");
            if ((flags & ~EpochFormat.KnownFlags) != 0) throw new ArgumentException("unknown epoch flags", nameof(flags));
            if (partCount < 1 || partCount > Limits.MaxEpochParts)
            {
                throw new ArgumentOutOfRangeException(nameof(partCount), partCount, "1 ... " + Limits.MaxEpochParts.ToString(CultureInfo.InvariantCulture));
            }
            if (partIndex < 0 || partIndex >= partCount) throw new ArgumentOutOfRangeException(nameof(partIndex), partIndex, "0 ... partCount - 1");

            flags = partCount > 1 ? flags | EpochFlags.MultiPart : flags & ~EpochFlags.MultiPart;
            _output = output;
            _start = output.Length;
            _sections = 0;
            output.WriteUInt32(epoch.Value);
            output.WriteUInt32(hostTick);
            output.WriteByte((byte)flags);
            output.WriteByte(0);                    // section count, patched by End
            if (partCount > 1)
            {
                output.WriteVarUInt32((uint)partIndex);
                output.WriteVarUInt32((uint)partCount);
            }
            _open = true;
        }

        /// <summary>
        /// Appends one section. Throws <see cref="ArgumentException"/> for an undefined section type,
        /// <see cref="InvalidOperationException"/> when the message already has 255 sections or would exceed
        /// <see cref="Limits.MaxUncompressedBytes"/> (nothing is written then).
        /// </summary>
        public void AppendSection(SectionType type, byte[] body, int offset, int count)
        {
            if (!_open) throw new InvalidOperationException("AppendSection without Begin");
            if (!EpochFormat.IsDefinedSection((byte)type)) throw new ArgumentException("undefined section type " + (byte)type, nameof(type));
            WireFormat.CheckOptionalRange(body, offset, count, nameof(body));
            if (_sections >= Limits.MaxSectionsPerEpoch) throw new InvalidOperationException("an epoch message holds at most 255 sections");
            long total = (long)Length + EpochFormat.SectionHeaderSize(count) + count;
            if (total > Limits.MaxUncompressedBytes)
            {
                throw new InvalidOperationException("epoch message would grow to " + total.ToString(CultureInfo.InvariantCulture)
                                                    + " bytes, above the part limit; split it with EpochPartSplitter");
            }
            _output.WriteByte((byte)type);
            _output.WriteVarUInt32((uint)count);
            if (count > 0) _output.WriteBytes(body, offset, count);
            _sections++;
        }

        /// <summary>Closes the message and returns its total length (header included).</summary>
        public int End()
        {
            if (!_open) throw new InvalidOperationException("End without Begin");
            _output.Buffer[_start + EpochFormat.SectionCountOffset] = (byte)_sections;
            int length = _output.Length - _start;
            _open = false;
            _output = null;
            return length;
        }
    }
}
