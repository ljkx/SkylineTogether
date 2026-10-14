using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// The payload of <see cref="ControlKind.PasswordChallenge"/> (host → client, ARCH §14.2), sent right after the hello
    /// whenever the session has a password (always for a direct-IP or unauthenticated connection, which is refused when
    /// the host set none): the host's fresh 16-byte nonce, the session salt for PBKDF2 and the session id the response
    /// MAC covers. No compatibility detail is sent before the answer was right.
    /// <code>
    /// bytes hostNonce (≤ 64) | bytes salt (≤ 64) | u32 sessionId
    /// </code>
    /// The static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class PasswordChallengeMessage
    {
        public const int MaxSaltBytes = 64;

        public byte[] HostNonce;
        public byte[] Salt;
        public uint SessionId;

        public static void Write(ByteWriter w, PasswordChallengeMessage m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            MessageFields.WriteBytes(w, m.HostNonce, HelloMessage.MaxNonceBytes, "HostNonce");
            MessageFields.WriteBytes(w, m.Salt, MaxSaltBytes, "Salt");
            w.WriteUInt32(m.SessionId);
        }

        public static PasswordChallengeMessage Read(ByteReader r)
        {
            MessageFields.CheckMessage(r, nameof(r));
            return new PasswordChallengeMessage
            {
                HostNonce = MessageFields.ReadBytes(r, HelloMessage.MaxNonceBytes, "PasswordChallenge.HostNonce"),
                Salt = MessageFields.ReadBytes(r, MaxSaltBytes, "PasswordChallenge.Salt"),
                SessionId = r.ReadUInt32()
            };
        }

        /// <summary>Reads a whole payload: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static PasswordChallengeMessage Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            PasswordChallengeMessage m = Read(r);
            MessageFields.EnsureEnd(r, "PasswordChallenge");
            return m;
        }
    }
}
