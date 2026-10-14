using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// The payload of <see cref="ControlKind.PasswordResponse"/> (client → host, ARCHITECTURE.md#security-model): the client's nonce and
    /// <c>HMAC-SHA256(PBKDF2-SHA256(password, salt, 10 000), hostNonce ‖ clientNonce ‖ sessionId)</c>; the password never
    /// crosses the wire.
    /// <code>
    /// bytes clientNonce (≤ 64) | bytes mac (≤ 64)
    /// </code>
    /// The static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class PasswordResponseMessage
    {
        public const int MaxMacBytes = 64;

        public byte[] ClientNonce;
        public byte[] Mac;

        public static void Write(ByteWriter w, PasswordResponseMessage m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            MessageFields.WriteBytes(w, m.ClientNonce, HelloMessage.MaxNonceBytes, "ClientNonce");
            MessageFields.WriteBytes(w, m.Mac, MaxMacBytes, "Mac");
        }

        public static PasswordResponseMessage Read(ByteReader r)
        {
            MessageFields.CheckMessage(r, nameof(r));
            return new PasswordResponseMessage
            {
                ClientNonce = MessageFields.ReadBytes(r, HelloMessage.MaxNonceBytes, "PasswordResponse.ClientNonce"),
                Mac = MessageFields.ReadBytes(r, MaxMacBytes, "PasswordResponse.Mac")
            };
        }

        /// <summary>Reads a whole payload: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static PasswordResponseMessage Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            PasswordResponseMessage m = Read(r);
            MessageFields.EnsureEnd(r, "PasswordResponse");
            return m;
        }
    }
}
