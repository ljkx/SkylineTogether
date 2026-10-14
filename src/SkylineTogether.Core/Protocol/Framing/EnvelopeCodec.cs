using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// The envelope of every message (ARCH §4.6.1, §4.6.5):
    /// <code>
    /// u8 kind | u8 flags | body                                                        flags = None
    /// u8 kind | u8 flags | varint uncompressedSize | u8 compressionKind | compressed    flags = Compressed | HasUncompressedSize
    /// u8 kind | u8 flags | fragment payload (see Fragmenter)                           flags = Fragment
    /// </code>
    /// Bodies of <see cref="Limits.CompressThreshold"/> bytes or more are compressed with the configured compressor
    /// when that makes them smaller. The compression kind byte names the codec, so a receiver always knows how a body
    /// was compressed: it decodes with its configured compressor when the kinds match, and LZ4 blocks (native
    /// <see cref="CompressionKind.Lz4"/> or <see cref="CompressionKind.ManagedLz4"/>, the same format) always, with the
    /// built-in <see cref="ManagedLz4Compressor"/>; any other unavailable codec is a <see cref="DecodeException"/>
    /// with a readable message.
    /// <para>
    /// The decoder enforces the per-kind body limit of <see cref="Limits.MaxBodyBytes"/> on the declared size before
    /// decompressing (decompression-bomb guard, ARCH §14.3), the fragment size limit, and the canonical flag
    /// combinations. Kind 0 is malformed; other unknown kinds decode normally and are dropped by the router.
    /// </para>
    /// <para>Stateless and thread-safe; outputs go to caller-owned writers.</para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class EnvelopeCodec
    {
        private const EnvelopeFlags KnownFlags = EnvelopeFlags.Compressed | EnvelopeFlags.Fragment | EnvelopeFlags.HasUncompressedSize;
        private const EnvelopeFlags CompressedFlags = EnvelopeFlags.Compressed | EnvelopeFlags.HasUncompressedSize;

        /// <summary>Output bytes an LZ4 block yields per input byte at most (255, plus a small constant per block).</summary>
        private const ulong Lz4MaxExpansion = 255;

        private static readonly ManagedLz4Compressor BuiltInLz4 = new ManagedLz4Compressor();

        private readonly ICompressor _compressor;
        private readonly ILog _log;

        /// <param name="compressor">The negotiated compressor; null (or kind <see cref="CompressionKind.None"/>) never compresses.</param>
        public EnvelopeCodec(ICompressor compressor) : this(compressor, null)
        {
        }

        /// <param name="compressor">The negotiated compressor; null (or kind <see cref="CompressionKind.None"/>) never compresses.</param>
        /// <param name="log">Receives a warning when the compressor fails (the body is then sent uncompressed); may be null.</param>
        public EnvelopeCodec(ICompressor compressor, ILog log)
        {
            _compressor = compressor != null && compressor.Kind != CompressionKind.None ? compressor : null;
            _log = log;
        }

        /// <summary>The compressor used for encoding, or null when bodies are never compressed.</summary>
        public ICompressor Compressor => _compressor;

        /// <summary>
        /// Appends the envelope of <c>body[offset .. offset+count-1]</c> to <paramref name="output"/> (the writer is not
        /// reset). Throws <see cref="ArgumentException"/> when <paramref name="kind"/> is 0 or the body exceeds
        /// <see cref="Limits.MaxBodyBytes"/> for the kind: such a message must be split by the caller. A compressor
        /// failure is logged and the body is sent uncompressed. The result may exceed
        /// <see cref="Limits.FragmentThreshold"/>; pass it to <see cref="Fragmenter.Split"/> before sending.
        /// </summary>
        public void Encode(MessageKind kind, byte[] body, int offset, int count, ByteWriter output)
        {
            if ((byte)kind == 0) throw new ArgumentException("message kind 0 is invalid", nameof(kind));
            WireFormat.CheckOptionalRange(body, offset, count, nameof(body));
            if (output == null) throw new ArgumentNullException(nameof(output));
            int max = Limits.MaxBodyBytes(kind);
            if (count > max)
            {
                throw new ArgumentException(kind + " body of " + count.ToString(CultureInfo.InvariantCulture)
                                            + " bytes exceeds the limit of " + max.ToString(CultureInfo.InvariantCulture), nameof(count));
            }
            if (_compressor != null && count >= Limits.CompressThreshold && TryEncodeCompressed(kind, body, offset, count, output))
            {
                return;
            }
            output.WriteByte((byte)kind);
            output.WriteByte((byte)EnvelopeFlags.None);
            if (count > 0) output.WriteBytes(body, offset, count);
        }

        /// <summary>
        /// Decodes one envelope from <c>buffer[offset .. offset+count-1]</c> (exactly one message). Decompressed bodies
        /// are written into <paramref name="scratch"/>, which is reset first and must not hold the input. Throws
        /// <see cref="DecodeException"/> for every malformed envelope and for bodies above the limits.
        /// </summary>
        public Envelope Decode(byte[] buffer, int offset, int count, ByteWriter scratch)
        {
            WireFormat.CheckRange(buffer, offset, count, nameof(buffer));
            if (scratch == null) throw new ArgumentNullException(nameof(scratch));
            if (count < 2)
            {
                throw new DecodeException("message of " + count.ToString(CultureInfo.InvariantCulture) + " byte(s) is shorter than the 2-byte envelope header");
            }
            byte kindByte = buffer[offset];
            if (kindByte == 0) throw new DecodeException("message kind 0 is invalid");
            var kind = (MessageKind)kindByte;
            var flags = (EnvelopeFlags)buffer[offset + 1];
            if ((flags & ~KnownFlags) != 0)
            {
                throw new DecodeException(kind + ": unknown envelope flags 0x" + ((byte)flags).ToString("X2", CultureInfo.InvariantCulture));
            }
            int max = Limits.MaxBodyBytes(kind);
            int end = offset + count;

            if ((flags & EnvelopeFlags.Fragment) != 0)
            {
                if (flags != EnvelopeFlags.Fragment) throw new DecodeException(kind + ": a fragment carries no other envelope flag");
                if (count > Limits.MaxMessageBytes)
                {
                    throw new DecodeException(kind + ": fragment of " + count.ToString(CultureInfo.InvariantCulture) + " bytes exceeds the message cap");
                }
                return new Envelope { Kind = kind, Flags = flags, Body = buffer, BodyOffset = offset + 2, BodyCount = count - 2 };
            }

            if ((flags & EnvelopeFlags.Compressed) == 0)
            {
                if (flags != EnvelopeFlags.None) throw new DecodeException(kind + ": uncompressed size declared without compression");
                if (count - 2 > max)
                {
                    throw new DecodeException(kind + ": body of " + (count - 2).ToString(CultureInfo.InvariantCulture)
                                              + " bytes exceeds the limit of " + max.ToString(CultureInfo.InvariantCulture));
                }
                return new Envelope { Kind = kind, Flags = flags, Body = buffer, BodyOffset = offset + 2, BodyCount = count - 2 };
            }

            if (flags != CompressedFlags) throw new DecodeException(kind + ": compressed body without a declared size");
            int position = offset + 2;
            uint declared = WireFormat.ReadVarUInt32(buffer, ref position, end, kind + " uncompressed size");
            if (declared == 0) throw new DecodeException(kind + ": compressed empty body");
            if (declared > (uint)max)
            {
                // checked before anything is allocated or decompressed
                throw new DecodeException(kind + ": declared size " + WireFormat.Format(declared) + " exceeds the limit of "
                                          + max.ToString(CultureInfo.InvariantCulture));
            }
            if (position >= end) throw new DecodeException(kind + ": compressed body without a compression kind");
            var codec = (CompressionKind)buffer[position++];
            int sourceCount = end - position;
            if (sourceCount <= 0) throw new DecodeException(kind + ": compressed body without data");
            ICompressor decompressor = SelectDecompressor(kind, codec);
            if ((codec == CompressionKind.Lz4 || codec == CompressionKind.ManagedLz4) && declared > (ulong)sourceCount * Lz4MaxExpansion + 1024)
            {
                // w8/tests: an LZ4 block expands at most ~255:1 (a match grows by 255 bytes per extra length byte), so a
                // larger declared size is corrupt; rejecting it here keeps a 20-byte message from making the receiver
                // allocate (and keep, in its reused scratch buffer) up to 8 MiB before decompression fails
                throw new DecodeException(kind + ": declared size " + WireFormat.Format(declared) + " is more than LZ4 can produce from "
                                          + sourceCount.ToString(CultureInfo.InvariantCulture) + " bytes");
            }

            int size = (int)declared;
            scratch.Reset();
            int target = scratch.Reserve(size);
            try
            {
                int produced = decompressor.Decompress(buffer, position, sourceCount, scratch.Buffer, target, size);
                if (produced != size)
                {
                    throw new DecodeException(kind + ": decompressed " + produced.ToString(CultureInfo.InvariantCulture)
                                              + " bytes, declared " + size.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (DecodeException)
            {
                throw;
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // a compressor must report corrupt input as DecodeException; anything else still must not escape a decoder
                throw new DecodeException(kind + ": " + codec + " decompression failed: " + e.Message, e);
            }
            return new Envelope { Kind = kind, Flags = flags, Body = scratch.Buffer, BodyOffset = target, BodyCount = size };
        }

        private ICompressor SelectDecompressor(MessageKind kind, CompressionKind codec)
        {
            if (_compressor != null && _compressor.Kind == codec) return _compressor;
            if (codec == CompressionKind.Lz4 || codec == CompressionKind.ManagedLz4)
            {
                // both are the LZ4 block format
                if (_compressor != null && (_compressor.Kind == CompressionKind.Lz4 || _compressor.Kind == CompressionKind.ManagedLz4)) return _compressor;
                return BuiltInLz4;
            }
            if (codec == CompressionKind.Zstd)
            {
                throw new DecodeException(kind + ": the peer compressed with Zstd, which is not available here (native compression probe failed?)");
            }
            throw new DecodeException(kind + ": unknown compression kind " + ((byte)codec).ToString(CultureInfo.InvariantCulture));
        }

        private bool TryEncodeCompressed(MessageKind kind, byte[] body, int offset, int count, ByteWriter output)
        {
            int bound;
            try
            {
                bound = _compressor.MaxCompressedSize(count);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                _log.Warn(LogCategory.Protocol, _compressor.Kind + " compressor rejected a " + count.ToString(CultureInfo.InvariantCulture) + "-byte body; sending it uncompressed", e);
                return false;
            }
            if (bound <= 0) return false;
            byte[] temp = BufferPool.Rent(bound);
            try
            {
                int compressed;
                try
                {
                    compressed = _compressor.Compress(body, offset, count, temp, 0);
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    _log.Warn(LogCategory.Protocol, _compressor.Kind + " compression of a " + kind + " body failed; sending it uncompressed", e);
                    return false;
                }
                if (compressed <= 0 || compressed > bound) return false;
                int header = 2 + WireFormat.VarUInt32Size((uint)count) + 1;
                if (header + compressed >= 2 + count) return false;   // no gain: send the body as is
                output.WriteByte((byte)kind);
                output.WriteByte((byte)CompressedFlags);
                output.WriteVarUInt32((uint)count);
                output.WriteByte((byte)_compressor.Kind);
                output.WriteBytes(temp, 0, compressed);
                return true;
            }
            finally
            {
                BufferPool.Return(temp);
            }
        }
    }
}
