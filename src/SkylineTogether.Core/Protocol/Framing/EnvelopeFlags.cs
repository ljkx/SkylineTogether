using System;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// The flags byte of an envelope (ARCH §4.6.1); the values travel on the wire. Valid combinations:
    /// <see cref="None"/>, <see cref="Compressed"/> | <see cref="HasUncompressedSize"/>, and <see cref="Fragment"/>
    /// alone. Every other combination and every other bit is rejected by <see cref="EnvelopeCodec.Decode"/>.
    /// </summary>
    [Flags]
    public enum EnvelopeFlags : byte
    {
        None = 0,

        /// <summary>The body is compressed: <c>varint uncompressedSize, u8 compressionKind, compressed bytes</c>.</summary>
        Compressed = 1,

        /// <summary>A fragment of a larger encoded message (<see cref="Fragmenter"/>, <see cref="Reassembler"/>).</summary>
        Fragment = 2,

        /// <summary>The varint uncompressed size follows the header; always set together with <see cref="Compressed"/>.</summary>
        HasUncompressedSize = 4
    }
}
