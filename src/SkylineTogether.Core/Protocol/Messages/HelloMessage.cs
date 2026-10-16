using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// <c>Hello</c> (client → host, the first message, ARCHITECTURE.md#handshake-and-admission): the handshake digests, the client's Steam
    /// identity and display name, its nonce for the direct-IP password challenge (ARCHITECTURE.md#security-model) and the codecs it can
    /// decompress (ARCHITECTURE.md#fragmentation-and-compression, <see cref="CompressionNegotiation"/>) and its join flags
    /// (<see cref="HelloJoinFlags"/>: "join anyway" with different mods).
    /// <code>
    /// digests (see below; starts with varint protocolVersion) | u64 steamId | text displayName (≤ 64 chars) | bytes clientNonce (≤ 64)
    /// | u8 compressionMask (bits of CompressionNegotiation.DefinedBits) | u8 joinFlags (bits of HelloJoinFlags.DefinedBits)
    /// digests = varint protocolVersion | u64 modBuild | string gameVersion | string gameFullVersion | u64 mods | u64 content
    ///         | u64 assetPacks | u64 modSettings | string locale
    /// </code>
    /// A host first reads the version with <see cref="PeekProtocolVersion"/> and refuses a different one with
    /// <see cref="RefuseReason.ProtocolVersion"/> before parsing the rest, whose layout may differ between versions.
    /// The static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class HelloMessage
    {
        /// <summary>Longest nonce accepted (the challenge uses 16 bytes).</summary>
        public const int MaxNonceBytes = 64;

        public HandshakeDigests Digests;
        public ulong SteamId;
        /// <summary>Free text: truncated to <see cref="Limits.MaxNameChars"/> when written, control characters stripped when read.</summary>
        public string DisplayName;
        public byte[] ClientNonce;
        /// <summary>
        /// Codecs the client decompresses (<see cref="CompressionNegotiation.LocalMask"/>); 0 announces nothing beyond
        /// what every peer decodes (uncompressed and LZ4). Readers reject undefined bits.
        /// </summary>
        public byte CompressionMask;
        /// <summary>
        /// <see cref="HelloJoinFlags"/> bits (0: nothing asked). Readers reject undefined bits. The host trusts
        /// <see cref="HelloJoinFlags.AcceptModMismatch"/> only for the mod and asset pack verdict, and only when its options allow it.
        /// </summary>
        public byte JoinFlags;

        public static void Write(ByteWriter w, HelloMessage m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            if ((m.CompressionMask & ~CompressionNegotiation.DefinedBits) != 0)
            {
                throw new System.ArgumentException("undefined compression mask bits", nameof(m));
            }
            if ((m.JoinFlags & ~HelloJoinFlags.DefinedBits) != 0)
            {
                throw new System.ArgumentException("undefined join flag bits", nameof(m));
            }
            HandshakeCodec.WriteDigests(w, m.Digests);
            w.WriteUInt64(m.SteamId);
            MessageFields.WriteText(w, m.DisplayName, Limits.MaxNameChars);
            MessageFields.WriteBytes(w, m.ClientNonce, MaxNonceBytes, "ClientNonce");
            w.WriteByte(m.CompressionMask);
            w.WriteByte(m.JoinFlags);
        }

        public static HelloMessage Read(ByteReader r)
        {
            MessageFields.CheckMessage(r, nameof(r));
            return new HelloMessage
            {
                Digests = HandshakeCodec.ReadDigests(r),
                SteamId = r.ReadUInt64(),
                DisplayName = MessageFields.ReadText(r, Limits.MaxNameChars, "Hello.DisplayName"),
                ClientNonce = MessageFields.ReadBytes(r, MaxNonceBytes, "Hello.ClientNonce"),
                CompressionMask = MessageFields.ReadFlags(r, CompressionNegotiation.DefinedBits, "Hello.CompressionMask"),
                JoinFlags = MessageFields.ReadFlags(r, HelloJoinFlags.DefinedBits, "Hello.JoinFlags")
            };
        }

        /// <summary>Reads a whole message body: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static HelloMessage Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            HelloMessage m = Read(r);
            MessageFields.EnsureEnd(r, "Hello");
            return m;
        }

        /// <summary>
        /// The protocol version at the start of a Hello body, readable whatever the sender's version (the layout of this
        /// first varint never changes). Throws <see cref="DecodeException"/> when the body does not start with one.
        /// </summary>
        public static int PeekProtocolVersion(byte[] buffer, int offset, int count)
        {
            WireFormat.CheckRange(buffer, offset, count, nameof(buffer));
            int position = offset;
            uint version = WireFormat.ReadVarUInt32(buffer, ref position, offset + count, "Hello protocol version");
            if (version > int.MaxValue)
            {
                throw new DecodeException("Hello: protocol version " + version.ToString(CultureInfo.InvariantCulture) + " exceeds int.MaxValue");
            }
            return (int)version;
        }
    }
}
