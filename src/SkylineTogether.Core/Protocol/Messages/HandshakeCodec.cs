using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// The encoding of <see cref="HandshakeDigests"/> shared by <see cref="HelloMessage"/> and
    /// <see cref="ManifestDetailMessage"/>. It starts with the protocol version so that a peer of any version can read
    /// it (<see cref="HelloMessage.PeekProtocolVersion"/>):
    /// <code>
    /// varint protocolVersion | u64 modBuild | string gameVersion (≤ 128 B) | string gameFullVersion (≤ 256 B)
    /// | u64 formatTags | u64 catalog | u64 prefabs | u64 mods | u64 content | u64 modSettings | u64 dataFiles | string locale (≤ 64 B)
    /// </code>
    /// </summary>
    internal static class HandshakeCodec
    {
        internal const int MaxGameVersionBytes = 128;
        internal const int MaxGameFullVersionBytes = 256;
        internal const int MaxLocaleBytes = 64;

        internal static void WriteDigests(ByteWriter w, in HandshakeDigests d)
        {
            if (d.ProtocolVersion < 0) throw new ArgumentOutOfRangeException(nameof(d), "negative protocol version");
            w.WriteVarUInt32((uint)d.ProtocolVersion);
            w.WriteUInt64(d.ModBuild);
            MessageFields.WriteString(w, d.GameVersion, MaxGameVersionBytes, "GameVersion");
            MessageFields.WriteString(w, d.GameFullVersion, MaxGameFullVersionBytes, "GameFullVersion");
            w.WriteUInt64(d.FormatTags);
            w.WriteUInt64(d.Catalog);
            w.WriteUInt64(d.Prefabs);
            w.WriteUInt64(d.Mods);
            w.WriteUInt64(d.Content);
            w.WriteUInt64(d.ModSettings);
            w.WriteUInt64(d.DataFiles);
            MessageFields.WriteString(w, d.Locale, MaxLocaleBytes, "Locale");
        }

        internal static HandshakeDigests ReadDigests(ByteReader r)
        {
            uint version = r.ReadVarUInt32();
            if (version > int.MaxValue) throw new DecodeException("handshake: protocol version " + version.ToString(CultureInfo.InvariantCulture) + " exceeds int.MaxValue");
            return new HandshakeDigests
            {
                ProtocolVersion = (int)version,
                ModBuild = r.ReadUInt64(),
                GameVersion = MessageFields.ReadString(r, MaxGameVersionBytes, "GameVersion"),
                GameFullVersion = MessageFields.ReadString(r, MaxGameFullVersionBytes, "GameFullVersion"),
                FormatTags = r.ReadUInt64(),
                Catalog = r.ReadUInt64(),
                Prefabs = r.ReadUInt64(),
                Mods = r.ReadUInt64(),
                Content = r.ReadUInt64(),
                ModSettings = r.ReadUInt64(),
                DataFiles = r.ReadUInt64(),
                Locale = MessageFields.ReadString(r, MaxLocaleBytes, "Locale")
            };
        }
    }
}
