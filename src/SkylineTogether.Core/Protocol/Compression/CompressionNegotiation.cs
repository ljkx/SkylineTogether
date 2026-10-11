using System;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Handshake negotiation of the message-body codec (ARCH §4.6.5: "the codec is negotiated in the handshake"). The
    /// client announces the codecs it can decompress in <see cref="HelloMessage.CompressionMask"/>; the host picks one
    /// with <see cref="Choose"/> and names it in <see cref="WelcomeMessage.Compression"/>; both sides then compress with
    /// it (<see cref="EnvelopeCodec"/>).
    /// <para>
    /// Mask bit <c>1 &lt;&lt; (int)kind</c> stands for a <see cref="CompressionKind"/>. <see cref="CompressionKind.None"/>
    /// and <see cref="CompressionKind.ManagedLz4"/> are always supported: every peer runs this Core, whose
    /// <see cref="EnvelopeCodec"/> decodes LZ4 blocks with the built-in <see cref="ManagedLz4Compressor"/> whatever the
    /// sender's LZ4 implementation (native <see cref="CompressionKind.Lz4"/> and the managed codec share the block
    /// format). A mask of 0 (a peer that announced nothing) therefore still means "uncompressed and LZ4".
    /// </para>
    /// <para>
    /// <b>Before the codec is known.</b> Each received envelope names its codec, so <i>decoding</i> depends only on what
    /// the receiver has: an <see cref="EnvelopeCodec"/> built on the best local compressor (<see cref="CompressorKindFor"/>
    /// of the local mask) decodes everything that peer announced, from the first message on. <i>Encoding</i> must follow
    /// the negotiation: Hello, Welcome, Refuse and every message sent before the session codec is known are encoded with a
    /// <see cref="Handshake"/> codec (a <see cref="ManagedLz4Compressor"/>), which every peer of every protocol version
    /// decodes; this keeps a Refuse readable across versions (<see cref="ProtocolVersion"/>). A peer therefore keeps its
    /// decoding codec for the whole connection and switches only its encoding codec once Welcome is sent or processed.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class CompressionNegotiation
    {
        /// <summary>
        /// The codec for encoding the messages sent before the session codec is negotiated (Hello, Welcome, Refuse,
        /// handshake control messages): LZ4 blocks, which every peer decodes with its built-in decoder.
        /// </summary>
        public const CompressionKind Handshake = CompressionKind.ManagedLz4;

        /// <summary>Bits of the codecs every peer decodes.</summary>
        public const byte AlwaysSupported = (1 << (int)CompressionKind.None) | (1 << (int)CompressionKind.ManagedLz4);

        /// <summary>Bits of every defined <see cref="CompressionKind"/>; readers reject other bits.</summary>
        public const byte DefinedBits = AlwaysSupported | (1 << (int)CompressionKind.Zstd) | (1 << (int)CompressionKind.Lz4);

        /// <summary>The bit of one codec.</summary>
        public static byte Bit(CompressionKind kind)
        {
            if (!IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind), kind, "undefined compression kind");
            return (byte)(1 << (int)kind);
        }

        /// <summary>
        /// The mask of a peer whose native probes found ZSTD (<paramref name="nativeZstd"/>) and LZ4
        /// (<paramref name="nativeLz4"/>) working (probes <c>P-ZSTD</c>, <c>P-LZ4</c>, ARCH Appendix B).
        /// </summary>
        public static byte LocalMask(bool nativeZstd, bool nativeLz4)
        {
            byte mask = AlwaysSupported;
            if (nativeZstd) mask |= 1 << (int)CompressionKind.Zstd;
            if (nativeLz4) mask |= 1 << (int)CompressionKind.Lz4;
            return mask;
        }

        /// <summary>True when a peer with <paramref name="mask"/> decompresses bodies compressed with <paramref name="kind"/>.</summary>
        public static bool Supports(byte mask, CompressionKind kind)
        {
            switch (kind)
            {
                case CompressionKind.None:
                case CompressionKind.ManagedLz4:
                case CompressionKind.Lz4:            // same block format: the built-in decoder takes it
                    return true;
                case CompressionKind.Zstd:
                    return (mask & (1 << (int)CompressionKind.Zstd)) != 0;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The session codec for a host with <paramref name="hostMask"/> and a client with <paramref name="clientMask"/>:
        /// ZSTD (the baseline, ARCH §4.6.5) when both have it natively, otherwise LZ4 (native where available: a peer
        /// without native LZ4 compresses with <see cref="ManagedLz4Compressor"/>, see <see cref="CompressorKindFor"/>).
        /// </summary>
        public static CompressionKind Choose(byte hostMask, byte clientMask)
        {
            if ((hostMask & clientMask & (1 << (int)CompressionKind.Zstd)) != 0) return CompressionKind.Zstd;
            if ((hostMask & (1 << (int)CompressionKind.Lz4)) != 0) return CompressionKind.Lz4;
            return CompressionKind.ManagedLz4;
        }

        /// <summary>
        /// The compressor a peer with <paramref name="localMask"/> uses for the negotiated <paramref name="session"/>
        /// codec: the session codec when it has it natively, the managed LZ4 codec for an LZ4 session without native LZ4,
        /// and <see cref="CompressionKind.None"/> (send uncompressed) when it cannot produce the session codec at all.
        /// </summary>
        public static CompressionKind CompressorKindFor(CompressionKind session, byte localMask)
        {
            switch (session)
            {
                case CompressionKind.Zstd:
                    return (localMask & (1 << (int)CompressionKind.Zstd)) != 0 ? CompressionKind.Zstd : CompressionKind.ManagedLz4;
                case CompressionKind.Lz4:
                    return (localMask & (1 << (int)CompressionKind.Lz4)) != 0 ? CompressionKind.Lz4 : CompressionKind.ManagedLz4;
                case CompressionKind.ManagedLz4:
                    return CompressionKind.ManagedLz4;
                default:
                    return CompressionKind.None;
            }
        }

        internal static bool IsDefined(CompressionKind kind) => (byte)kind <= (byte)CompressionKind.Lz4;
    }
}
