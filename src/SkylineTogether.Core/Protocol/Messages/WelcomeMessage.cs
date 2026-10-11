using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// <c>Welcome</c> (host → client, ARCH §7.1 step 3): the admission. <see cref="SessionSettings"/> is the encoded
    /// <c>Session.SessionSettings</c> (core-session); <see cref="EnabledSysEntries"/> are the stable ids of the
    /// system-state entries the host enabled (ARCH §4.10.1); <see cref="Compression"/> is the session codec the host
    /// chose from the client's <see cref="HelloMessage.CompressionMask"/> (<see cref="CompressionNegotiation.Choose"/>).
    /// <code>
    /// u32 sessionId | u8 assignedPeer | u8 role | varint permissions | bytes sessionSettings (≤ 2 MiB) | bytes hostNonce (≤ 64)
    /// | varint count (≤ 4096) | count × u32 enabledSysEntry | u8 compression
    /// </code>
    /// The static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class WelcomeMessage
    {
        public const int MaxSessionSettingsBytes = 2 * 1024 * 1024;
        public const int MaxSysEntries = 4096;

        public uint SessionId;
        public PeerId AssignedPeer;
        public SessionRole Role;
        public Permission Permissions;
        public byte[] SessionSettings;
        public byte[] HostNonce;
        public uint[] EnabledSysEntries;
        /// <summary>The codec both sides compress with (<see cref="CompressionNegotiation.CompressorKindFor"/>);
        /// <see cref="CompressionKind.None"/> = bodies are sent uncompressed.</summary>
        public CompressionKind Compression;

        public static void Write(ByteWriter w, WelcomeMessage m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            if (m.Role > SessionRole.Viewer) throw new ArgumentException("undefined session role", nameof(m));
            if ((m.Permissions & ~Permission.All) != 0) throw new ArgumentException("undefined permission bits", nameof(m));
            if (!CompressionNegotiation.IsDefined(m.Compression)) throw new ArgumentException("undefined compression kind", nameof(m));
            int entries = m.EnabledSysEntries == null ? 0 : m.EnabledSysEntries.Length;
            if (entries > MaxSysEntries) throw new ArgumentException("more than 4096 enabled system-state entries", nameof(m));
            w.WriteUInt32(m.SessionId);
            w.WriteByte(m.AssignedPeer.Value);
            w.WriteByte((byte)m.Role);
            w.WriteVarUInt32((uint)m.Permissions);
            MessageFields.WriteBytes(w, m.SessionSettings, MaxSessionSettingsBytes, "SessionSettings");
            MessageFields.WriteBytes(w, m.HostNonce, HelloMessage.MaxNonceBytes, "HostNonce");
            w.WriteVarUInt32((uint)entries);
            for (int i = 0; i < entries; i++) w.WriteUInt32(m.EnabledSysEntries[i]);
            w.WriteByte((byte)m.Compression);
        }

        public static WelcomeMessage Read(ByteReader r)
        {
            MessageFields.CheckMessage(r, nameof(r));
            var m = new WelcomeMessage { SessionId = r.ReadUInt32(), AssignedPeer = new PeerId(r.ReadByte()) };
            byte role = r.ReadByte();
            if (role > (byte)SessionRole.Viewer) throw new DecodeException("Welcome: undefined role " + role.ToString(CultureInfo.InvariantCulture));
            m.Role = (SessionRole)role;
            uint permissions = r.ReadVarUInt32();
            if ((permissions & ~(uint)Permission.All) != 0)
            {
                throw new DecodeException("Welcome: undefined permission bits 0x" + permissions.ToString("X", CultureInfo.InvariantCulture));
            }
            m.Permissions = (Permission)permissions;
            m.SessionSettings = MessageFields.ReadBytes(r, MaxSessionSettingsBytes, "Welcome.SessionSettings");
            m.HostNonce = MessageFields.ReadBytes(r, HelloMessage.MaxNonceBytes, "Welcome.HostNonce");
            int count = MessageFields.ReadCount(r, MaxSysEntries, 4, "Welcome.EnabledSysEntries");
            m.EnabledSysEntries = count == 0 ? Array.Empty<uint>() : new uint[count];
            for (int i = 0; i < count; i++) m.EnabledSysEntries[i] = r.ReadUInt32();
            byte compression = r.ReadByte();
            if (!CompressionNegotiation.IsDefined((CompressionKind)compression))
            {
                throw new DecodeException("Welcome: undefined compression kind " + compression.ToString(CultureInfo.InvariantCulture));
            }
            m.Compression = (CompressionKind)compression;
            return m;
        }

        /// <summary>Reads a whole message body: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static WelcomeMessage Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            WelcomeMessage m = Read(r);
            MessageFields.EnsureEnd(r, "Welcome");
            return m;
        }
    }
}
