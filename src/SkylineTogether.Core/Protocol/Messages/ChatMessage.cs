using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// A <c>Chat</c> line. The host relays chat and sets <see cref="From"/> itself (a client cannot speak for another).
    /// A system line of the host carries a locale key and its parameters (<c>Session.NetText</c>) so every player reads it in
    /// their own language; <see cref="Text"/> is the English fallback.
    /// <code>
    /// u8 from | text text (≤ 500 chars) | bool isSystem | string key (≤ 128 B, empty: none)
    /// | varint argCount (≤ 8) | argCount × (string name ≤ 32 B, text value ≤ 256 chars)
    /// </code>
    /// The text is truncated to <see cref="Limits.MaxChatChars"/> when written; a longer one is rejected and control
    /// characters are stripped when read. The static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class ChatMessage
    {
        public PeerId From;
        public string Text;
        public bool IsSystem;
        /// <summary>The locale key of a system line (null: none) and its parameters (name, value, ...).</summary>
        public string Key;
        public string[] Args;

        public static void Write(ByteWriter w, ChatMessage m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            w.WriteByte(m.From.Value);
            MessageFields.WriteText(w, m.Text, Limits.MaxChatChars);
            w.WriteBool(m.IsSystem);
            MessageFields.WriteString(w, m.Key ?? "", Session.LocalText.MaxKeyBytes, "Chat.Key");
            string[] args = m.Args ?? System.Array.Empty<string>();
            int pairs = System.Math.Min(Session.LocalText.MaxArgs, args.Length / 2);
            w.WriteVarUInt32((uint)pairs);
            for (int i = 0; i < pairs; i++)
            {
                MessageFields.WriteString(w, args[2 * i] ?? "", Session.LocalText.MaxArgNameBytes, "Chat.ArgName");
                MessageFields.WriteText(w, args[2 * i + 1] ?? "", Session.LocalText.MaxArgValueChars);
            }
        }

        public static ChatMessage Read(ByteReader r)
        {
            MessageFields.CheckMessage(r, nameof(r));
            var m = new ChatMessage
            {
                From = new PeerId(r.ReadByte()),
                Text = MessageFields.ReadText(r, Limits.MaxChatChars, "Chat.Text"),
                IsSystem = r.ReadBool()
            };
            string key = MessageFields.ReadString(r, Session.LocalText.MaxKeyBytes, "Chat.Key");
            m.Key = string.IsNullOrEmpty(key) ? null : key;
            uint pairs = r.ReadVarUInt32();
            if (pairs > Session.LocalText.MaxArgs) throw new DecodeException("Chat: " + pairs + " parameters");
            m.Args = new string[pairs * 2];
            for (int i = 0; i < pairs; i++)
            {
                m.Args[2 * i] = MessageFields.ReadString(r, Session.LocalText.MaxArgNameBytes, "Chat.ArgName");
                m.Args[2 * i + 1] = MessageFields.ReadText(r, Session.LocalText.MaxArgValueChars, "Chat.ArgValue");
            }
            return m;
        }

        /// <summary>Reads a whole message body: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static ChatMessage Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            ChatMessage m = Read(r);
            MessageFields.EnsureEnd(r, "Chat");
            return m;
        }
    }
}
