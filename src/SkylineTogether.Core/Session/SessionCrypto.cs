using System;
using System.Security.Cryptography;
using System.Text;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// Password proof: the password never crosses the network. The client answers the host's challenge with
    /// <c>HMAC-SHA256(key = PBKDF2-HMAC-SHA256(UTF-8 password, salt, 10 000), hostNonce ‖ clientNonce ‖ sessionId)</c>.
    /// <para>
    /// Replay safety: the host draws a fresh 16-byte nonce for every challenge, accepts at most one answer per challenge
    /// (a wrong answer ends the connection, a right one admits it) and binds the MAC to its random session id, so an
    /// answer recorded from one connection proves nothing on another. The salt is random per session and the key
    /// derivation is slow on purpose: a captured challenge and answer cost 10 000 HMAC rounds per guessed password.
    /// </para>
    /// </summary>
    public static class SessionCrypto
    {
        /// <summary>PBKDF2 rounds of the password key (before, a single SHA-256 was used).</summary>
        public const int Pbkdf2Iterations = 10000;

        /// <summary>Shortest client nonce the host accepts in an answer.</summary>
        public const int MinNonceBytes = 8;

        /// <summary>The password key: PBKDF2-HMAC-SHA256 of the UTF-8 password with the salt, one 32-byte block.</summary>
        public static byte[] DeriveKey(string password, byte[] salt) => Pbkdf2Sha256(Encoding.UTF8.GetBytes(password ?? ""), salt ?? Array.Empty<byte>(), Pbkdf2Iterations);

        /// <summary>The answer to a challenge with an already derived key (<see cref="DeriveKey"/>).</summary>
        public static byte[] PasswordMac(byte[] key, byte[] hostNonce, byte[] clientNonce, uint sessionId)
        {
            int hn = hostNonce?.Length ?? 0, cn = clientNonce?.Length ?? 0;
            byte[] msg = new byte[hn + cn + 4];
            if (hn > 0) Buffer.BlockCopy(hostNonce, 0, msg, 0, hn);
            if (cn > 0) Buffer.BlockCopy(clientNonce, 0, msg, hn, cn);
            msg[hn + cn] = (byte)sessionId;
            msg[hn + cn + 1] = (byte)(sessionId >> 8);
            msg[hn + cn + 2] = (byte)(sessionId >> 16);
            msg[hn + cn + 3] = (byte)(sessionId >> 24);
            using (var hmac = new HMACSHA256(key ?? Array.Empty<byte>())) return hmac.ComputeHash(msg);
        }

        /// <summary>The answer to a challenge: derives the key (slow) and MACs the nonces and the session id.</summary>
        public static byte[] PasswordMac(string password, byte[] hostNonce, byte[] clientNonce, byte[] salt, uint sessionId)
        {
            return PasswordMac(DeriveKey(password, salt), hostNonce, clientNonce, sessionId);
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
