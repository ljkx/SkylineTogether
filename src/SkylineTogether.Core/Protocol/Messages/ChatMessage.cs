using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// A <c>Chat</c> line. The host relays chat and sets <see cref="From"/> itself (a client cannot speak for another).
    /// <code>
    /// u8 from | text text (≤ 500 chars) | bool isSystem
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

        public static void Write(ByteWriter w, ChatMessage m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            w.WriteByte(m.From.Value);
            MessageFields.WriteText(w, m.Text, Limits.MaxChatChars);
            w.WriteBool(m.IsSystem);
        }

        public static ChatMessage Read(ByteReader r)
        {
            MessageFields.CheckMessage(r, nameof(r));
            return new ChatMessage
            {
                From = new PeerId(r.ReadByte()),
                Text = MessageFields.ReadText(r, Limits.MaxChatChars, "Chat.Text"),
                IsSystem = r.ReadBool()
            };
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
