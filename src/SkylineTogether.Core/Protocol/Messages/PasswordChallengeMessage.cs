using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// The payload of <see cref="ControlKind.PasswordChallenge"/> (host → client on a direct-IP connection, ARCH §14.2):
    /// the host's 16-byte nonce, the session salt for PBKDF2 and the session id the response MAC covers.
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
