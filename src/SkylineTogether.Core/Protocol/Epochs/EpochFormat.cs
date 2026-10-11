using System.Globalization;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// The framing rules of epoch messages shared by <see cref="EpochWriter"/>, <see cref="EpochReader"/>,
    /// <see cref="EpochStreamReader"/>, <see cref="EpochPartSplitter"/> and <see cref="EpochPartAssembler"/>.
    /// </summary>
    internal static class EpochFormat
    {
        /// <summary>u32 epoch, u32 hostTick, u8 flags, u8 sectionCount.</summary>
        internal const int FixedHeaderBytes = 10;

        /// <summary>Byte offset of the section count inside the header.</summary>
        internal const int SectionCountOffset = 9;

        /// <summary>A multi-part header with both varints at their largest (5 bytes each).</summary>
        internal const int MaxHeaderBytes = FixedHeaderBytes + 10;

        internal const EpochFlags KnownFlags = EpochFlags.SnapshotBoundary | EpochFlags.CatchUp | EpochFlags.ChannelKeyframe | EpochFlags.MultiPart;

        internal static bool IsDefinedSection(byte type) => type >= (byte)SectionType.Tx && type <= (byte)SectionType.TypeSets;

        /// <summary>Encoded size of a section header for a body of <paramref name="bodyLength"/> bytes.</summary>
        internal static int SectionHeaderSize(int bodyLength) => 1 + WireFormat.VarUInt32Size((uint)bodyLength);

        /// <summary>Encoded size of an epoch header with the given part numbering.</summary>
        internal static int HeaderSize(int partIndex, int partCount)
        {
            if (partCount <= 1) return FixedHeaderBytes;
            return FixedHeaderBytes + WireFormat.VarUInt32Size((uint)partIndex) + WireFormat.VarUInt32Size((uint)partCount);
        }

        /// <summary>
        /// Parses and validates the header at <paramref name="offset"/>; returns its length. Throws
        /// <see cref="DecodeException"/>.
        /// </summary>
        internal static int ParseHeader(byte[] buffer, int offset, int count, out EpochHeader header)
        {
            if (count < FixedHeaderBytes)
            {
                throw new DecodeException("epoch message of " + count.ToString(CultureInfo.InvariantCulture)
                                          + " bytes is shorter than its " + FixedHeaderBytes.ToString(CultureInfo.InvariantCulture) + "-byte header");
            }
            uint epoch = ReadUInt32(buffer, offset);
            uint hostTick = ReadUInt32(buffer, offset + 4);
            var flags = (EpochFlags)buffer[offset + 8];
            int sectionCount = buffer[offset + SectionCountOffset];
            ValidateFlags(flags);
            int position = offset + FixedHeaderBytes;
            int partIndex = 0, partCount = 1;
            if ((flags & EpochFlags.MultiPart) != 0)
            {
                int end = offset + count;
                uint index = WireFormat.ReadVarUInt32(buffer, ref position, end, "epoch part index");
                uint parts = WireFormat.ReadVarUInt32(buffer, ref position, end, "epoch part count");
                ValidateParts(index, parts);
                partIndex = (int)index;
                partCount = (int)parts;
            }
            header = new EpochHeader
            {
                Epoch = new EpochId(epoch),
                HostTick = hostTick,
                Flags = flags,
                SectionCount = sectionCount,
                PartIndex = partIndex,
                PartCount = partCount
            };
            return position - offset;
        }

        internal static void ValidateFlags(EpochFlags flags)
        {
            if ((flags & ~KnownFlags) != 0)
            {
                throw new DecodeException("epoch: unknown flags 0x" + ((byte)flags).ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        internal static void ValidateParts(uint index, uint count)
        {
            if (count < 2 || count > Limits.MaxEpochParts)
            {
                throw new DecodeException("epoch: part count " + WireFormat.Format(count) + " outside 2 ... "
                                          + Limits.MaxEpochParts.ToString(CultureInfo.InvariantCulture));
            }
            if (index >= count) throw new DecodeException("epoch: part index " + WireFormat.Format(index) + " of " + WireFormat.Format(count));
        }

        /// <summary>
        /// Walks <paramref name="sectionCount"/> section headers from <paramref name="position"/> and requires them to
        /// end exactly at <paramref name="end"/>; each section type must be defined and each body at most
        /// <see cref="Limits.MaxUncompressedBytes"/>. Throws <see cref="DecodeException"/>.
        /// </summary>
        internal static void ValidateSections(byte[] buffer, int position, int end, int sectionCount)
        {
            for (int i = 0; i < sectionCount; i++)
            {
                ReadSectionHeader(buffer, ref position, end, out _, out int length);
                position += length;
            }
            if (position != end)
            {
                throw new DecodeException("epoch: " + (end - position).ToString(CultureInfo.InvariantCulture) + " trailing byte(s) after "
                                          + sectionCount.ToString(CultureInfo.InvariantCulture) + " section(s)");
            }
        }

        /// <summary>Reads one section header and checks that its body lies inside the message.</summary>
        internal static void ReadSectionHeader(byte[] buffer, ref int position, int end, out SectionType type, out int length)
        {
            if (position >= end) throw new DecodeException("epoch: message ends before the declared sections");
            byte typeByte = buffer[position++];
            if (!IsDefinedSection(typeByte)) throw new DecodeException("epoch: unknown section type " + typeByte.ToString(CultureInfo.InvariantCulture));
            uint size = WireFormat.ReadVarUInt32(buffer, ref position, end, "epoch section length");
            if (size > (uint)Limits.MaxUncompressedBytes)
            {
                throw new DecodeException("epoch: section of " + WireFormat.Format(size) + " bytes exceeds the part limit");
            }
            if (size > (uint)(end - position))
            {
                throw new DecodeException("epoch: " + (SectionType)typeByte + " section of " + WireFormat.Format(size)
                                          + " bytes overruns the message (" + (end - position).ToString(CultureInfo.InvariantCulture) + " left)");
            }
            type = (SectionType)typeByte;
            length = (int)size;
        }

        internal static uint ReadUInt32(byte[] b, int p) => b[p] | ((uint)b[p + 1] << 8) | ((uint)b[p + 2] << 16) | ((uint)b[p + 3] << 24);

        internal static void WriteUInt32(byte[] b, int p, uint v)
        {
            b[p] = (byte)v;
            b[p + 1] = (byte)(v >> 8);
            b[p + 2] = (byte)(v >> 16);
            b[p + 3] = (byte)(v >> 24);
        }
    }
}
