using System;
using System.Security.Cryptography;
using System.Text;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// Password proof: the password never crosses the network. Both sides derive
    /// <c>key = PBKDF2-HMAC-SHA256(UTF-8 password, salt, 200 000)</c> and prove it with HMAC-SHA256 over the handshake's
    /// transcript: a label for the direction, the host's random session id, the host's fresh nonce, the client's nonce,
    /// the salt and the host's Steam ID as the client dialled it (0 for a direct connection). The client answers the
    /// host's challenge with <see cref="ClientProof"/>; the host checks it and answers with <see cref="HostProof"/>, which
    /// the client checks before it sends anything else (mutual: an impostor host learns nothing and is detected).
    /// <para>
    /// Replay and relay safety: the host draws a fresh 16-byte nonce for every challenge, accepts at most one answer per
    /// challenge and binds the proofs to its session id and to its Steam ID, so an answer recorded from one connection
    /// proves nothing on another, and an answer relayed through someone else's Steam ID fails. Between two direct
    /// connections without Steam authentication nothing binds the proof to the connection (Steam exposes no channel
    /// binding), which SECURITY.md documents. The salt is random per session and the key derivation is slow on purpose:
    /// a captured challenge and answer cost 200 000 HMAC rounds per guessed password. Derive it off the main thread
    /// (<see cref="PasswordKeyCache"/>).
    /// </para>
    /// </summary>
    public static class SessionCrypto
    {
        /// <summary>PBKDF2 rounds of the password key (10 000 until protocol 6).</summary>
        public const int Pbkdf2Iterations = 200000;

        /// <summary>Shortest client nonce the host accepts in an answer.</summary>
        public const int MinNonceBytes = 8;

        /// <summary>The password key: PBKDF2-HMAC-SHA256 of the UTF-8 password with the salt, one 32-byte block (slow).</summary>
        public static byte[] DeriveKey(string password, byte[] salt) => Pbkdf2Sha256(Encoding.UTF8.GetBytes(password ?? ""), salt ?? Array.Empty<byte>(), Pbkdf2Iterations);

        /// <summary>The client's answer to a challenge, with the derived key (<see cref="DeriveKey"/>).</summary>
        public static byte[] ClientProof(byte[] key, uint sessionId, byte[] hostNonce, byte[] clientNonce, byte[] salt, ulong hostSteamId)
            => Proof("SkylineTogether client proof", key, sessionId, hostNonce, clientNonce, salt, hostSteamId);

        /// <summary>The host's answer to a right client proof: it knows the password too.</summary>
        public static byte[] HostProof(byte[] key, uint sessionId, byte[] hostNonce, byte[] clientNonce, byte[] salt, ulong hostSteamId)
            => Proof("SkylineTogether host proof", key, sessionId, hostNonce, clientNonce, salt, hostSteamId);

        private static byte[] Proof(string label, byte[] key, uint sessionId, byte[] hostNonce, byte[] clientNonce, byte[] salt, ulong hostSteamId)
        {
            using (var m = new System.IO.MemoryStream(128))
            {
                byte[] l = Encoding.ASCII.GetBytes(label);
                m.Write(l, 0, l.Length);
                m.WriteByte(0);
                for (int i = 0; i < 4; i++) m.WriteByte((byte)(sessionId >> (8 * i)));
                Field(m, hostNonce);
                Field(m, clientNonce);
                Field(m, salt);
                for (int i = 0; i < 8; i++) m.WriteByte((byte)(hostSteamId >> (8 * i)));
                using (var hmac = new HMACSHA256(key ?? Array.Empty<byte>())) return hmac.ComputeHash(m.ToArray());
            }
        }

        // length-prefixed, so no two transcripts encode alike
        private static void Field(System.IO.Stream s, byte[] data)
        {
            data = data ?? Array.Empty<byte>();
            int n = Math.Min(255, data.Length);
            s.WriteByte((byte)n);
            s.Write(data, 0, n);
        }

        /// <summary>
        /// PBKDF2 (RFC 8018) with HMAC-SHA256, the first 32-byte block only (enough for an HMAC-SHA256 key). Written out
        /// because the SHA-256 overload of <c>Rfc2898DeriveBytes</c> is not in netstandard2.0.
        /// </summary>
        public static byte[] Pbkdf2Sha256(byte[] password, byte[] salt, int iterations)
        {
            if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations), iterations, "at least one round");
            using (var hmac = new HMACSHA256(password ?? Array.Empty<byte>()))
            {
                salt = salt ?? Array.Empty<byte>();
                byte[] first = new byte[salt.Length + 4];
                Buffer.BlockCopy(salt, 0, first, 0, salt.Length);
                first[salt.Length + 3] = 1; // block index 1, big-endian
                byte[] u = hmac.ComputeHash(first);
                byte[] t = (byte[])u.Clone();
                for (int i = 1; i < iterations; i++)
                {
                    u = hmac.ComputeHash(u);
                    for (int j = 0; j < t.Length; j++) t[j] ^= u[j];
                }
                return t;
            }
        }

        /// <summary>Compares two byte arrays in time that depends only on their length (the MAC length is public).</summary>
        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return bytes;
        }
    }
}
