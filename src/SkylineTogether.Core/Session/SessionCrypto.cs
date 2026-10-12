using System;
using System.Security.Cryptography;
using System.Text;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// Password proof: the password never crosses the network. The client answers the host's challenge with
    /// HMAC-SHA256(key = SHA256(salt ‖ UTF-8 password), hostNonce ‖ clientNonce ‖ sessionId).
    /// </summary>
    public static class SessionCrypto
    {
        public static byte[] PasswordMac(string password, byte[] hostNonce, byte[] clientNonce, byte[] salt, uint sessionId)
        {
            byte[] pw = Encoding.UTF8.GetBytes(password ?? "");
            byte[] keyInput = new byte[(salt?.Length ?? 0) + pw.Length];
            if (salt != null) Buffer.BlockCopy(salt, 0, keyInput, 0, salt.Length);
            Buffer.BlockCopy(pw, 0, keyInput, salt?.Length ?? 0, pw.Length);
            byte[] key;
            using (SHA256 sha = SHA256.Create()) key = sha.ComputeHash(keyInput);
            int hn = hostNonce?.Length ?? 0, cn = clientNonce?.Length ?? 0;
            byte[] msg = new byte[hn + cn + 4];
            if (hn > 0) Buffer.BlockCopy(hostNonce, 0, msg, 0, hn);
            if (cn > 0) Buffer.BlockCopy(clientNonce, 0, msg, hn, cn);
            msg[hn + cn] = (byte)sessionId;
            msg[hn + cn + 1] = (byte)(sessionId >> 8);
            msg[hn + cn + 2] = (byte)(sessionId >> 16);
            msg[hn + cn + 3] = (byte)(sessionId >> 24);
            using (var hmac = new HMACSHA256(key)) return hmac.ComputeHash(msg);
        }

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
