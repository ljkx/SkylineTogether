using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>Who a <see cref="ModChannelMessage"/> is for.</summary>
    public enum ModChannelTarget : byte
    {
        /// <summary>Every other player (the host relays a client's message to everyone else).</summary>
        Everyone = 0,
        /// <summary>The host only.</summary>
        Host = 1,
        /// <summary>One player (<see cref="ModChannelMessage.TargetPeer"/>).</summary>
        Player = 2,
    }

    /// <summary>
    /// A <c>ModChannel</c> message: opaque bytes of another mod (docs/MOD-INTEGRATION.md), routed by the mod's channel
    /// name (its assembly name). Clients only talk to the host; the host relays (<see cref="Modding.ModChannelRouter"/>)
    /// and sets <see cref="From"/> itself, so a client cannot speak for another.
    /// <code>
    /// u8 version (1) | string channel (1 ... 128 UTF-8 bytes) | u8 from | u8 target | u8 targetPeer | bytes payload (≤ 16 KiB)
    /// </code>
    /// The static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class ModChannelMessage
    {
        public const byte Version = 1;

        /// <summary>UTF-8 bytes of a channel name at most.</summary>
        public const int MaxChannelBytes = 128;

        /// <summary>Payload bytes of one message at most.</summary>
        public const int MaxPayloadBytes = 16 * 1024;

        /// <summary>Largest encoded message (the envelope's body limit).</summary>
        public const int MaxEncodedBytes = MaxPayloadBytes + MaxChannelBytes + 16;

        public string Channel;
        public PeerId From;
        public ModChannelTarget Target;
        /// <summary>The player for <see cref="ModChannelTarget.Player"/> (ignored otherwise).</summary>
        public PeerId TargetPeer;
        public byte[] Payload;

        public static void Write(ByteWriter w, ModChannelMessage m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            if (string.IsNullOrEmpty(m.Channel)) throw new System.ArgumentException("the channel name is empty", nameof(m));
            if (m.Target > ModChannelTarget.Player) throw new System.ArgumentException("unknown target " + (byte)m.Target, nameof(m));
            w.WriteByte(Version);
            MessageFields.WriteString(w, m.Channel, MaxChannelBytes, "ModChannel.Channel");
            w.WriteByte(m.From.Value);
            w.WriteByte((byte)m.Target);
            w.WriteByte(m.TargetPeer.Value);
            MessageFields.WriteBytes(w, m.Payload, MaxPayloadBytes, "ModChannel.Payload");
        }

        public static ModChannelMessage Read(ByteReader r)
        {
            MessageFields.CheckMessage(r, nameof(r));
            byte version = r.ReadByte();
            if (version != Version) throw new DecodeException("ModChannel: version " + version);
            var m = new ModChannelMessage { Channel = MessageFields.ReadString(r, MaxChannelBytes, "ModChannel.Channel") };
            if (m.Channel.Length == 0) throw new DecodeException("ModChannel: empty channel name");
            m.From = new PeerId(r.ReadByte());
            byte target = r.ReadByte();
            if (target > (byte)ModChannelTarget.Player) throw new DecodeException("ModChannel: unknown target " + target);
            m.Target = (ModChannelTarget)target;
            m.TargetPeer = new PeerId(r.ReadByte());
            m.Payload = MessageFields.ReadBytes(r, MaxPayloadBytes, "ModChannel.Payload");
            return m;
        }

        /// <summary>Reads a whole message body: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static ModChannelMessage Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            ModChannelMessage m = Read(r);
            MessageFields.EnsureEnd(r, "ModChannel");
            return m;
        }
    }
}
