using System;
using System.Text;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Json;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Digest of the effective content of a data file after overrides (ARCH §4.2.7): xxHash64 (seed 0) of the UTF-8
    /// bytes of its canonical JSON. The canonical form sorts object members by ordinal name, drops comment members
    /// (names starting with <c>_</c>, which no parser reads), removes whitespace and normalises number literals
    /// exactly (1, 1.0 and 1e0 are equal), so the digest is independent of member order, formatting and comments and
    /// changes with any effective value. Equal on every runtime: no floating-point formatting is involved.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class DataFileDigest
    {
        public static ulong Compute(JsonValue effectiveContent)
        {
            if (effectiveContent == null) throw new ArgumentNullException(nameof(effectiveContent));
            byte[] utf8 = Encoding.UTF8.GetBytes(CanonicalJson.ToCanonicalString(effectiveContent));
            return XxHash64.Hash(utf8, 0, utf8.Length);
        }

        /// <summary>The canonical JSON text the digest is computed from (diagnostics: shows why two digests differ).</summary>
        public static string ToCanonicalJson(JsonValue effectiveContent)
        {
            if (effectiveContent == null) throw new ArgumentNullException(nameof(effectiveContent));
            return CanonicalJson.ToCanonicalString(effectiveContent);
        }
    }
}
