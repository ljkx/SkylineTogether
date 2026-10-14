using System;
using System.Globalization;
using System.Security.Cryptography;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// w8/security: the one build id of Skyline Together: the first 12 hex digits (48 bits) of the SHA-256 of the
    /// shipped <c>SkylineTogether.dll</c>, exactly what the installer prints (tools/installer). The game computes it from
    /// the loaded assembly's file at start, compares it in the handshake (<see cref="HandshakeDigests.ModBuild"/> carries
    /// the 48 bits) and shows it in the panel, the refusal, the log and the diagnostics.
    /// </summary>
    public static class BuildId
    {
        /// <summary>Hex digits of a build id.</summary>
        public const int HexDigits = 12;

        /// <summary>The build id of a file's bytes (SHA-256, first 6 bytes).</summary>
        public static ulong OfBytes(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return FromHash(sha.ComputeHash(bytes ?? Array.Empty<byte>()));
        }

        /// <summary>The build id of a hash (its first 6 bytes, big-endian, as the hex string reads).</summary>
        public static ulong FromHash(byte[] hash)
        {
            if (hash == null || hash.Length < 6) throw new ArgumentException("at least 6 bytes", nameof(hash));
            ulong v = 0;
            for (int i = 0; i < 6; i++) v = (v << 8) | hash[i];
            return v;
        }

        /// <summary>12 lower-case hex digits (the installer's format).</summary>
        public static string Format(ulong build) => (build & 0xFFFFFFFFFFFFUL).ToString("x12", CultureInfo.InvariantCulture);

        /// <summary>Reads <see cref="Format"/>; false for anything but 12 hex digits.</summary>
        public static bool TryParse(string text, out ulong build)
        {
            build = 0;
            if (text == null || text.Length != HexDigits) return false;
            return ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out build);
        }
    }
}
