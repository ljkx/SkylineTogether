using System.Globalization;

namespace SkylineTogether.Core
{
    /// <summary>One data file of the handshake manifest (ARCH §4.2.7): its effective-content digest
    /// (<see cref="DataFileDigest.Compute"/>) and whether an override file replaced the embedded copy.</summary>
    public sealed class DataFileDigestEntry
    {
        /// <summary>File name, e.g. <c>policies.json</c>.</summary>
        public string File;
        public ulong Digest;
        public bool Overridden;

        public override string ToString()
        {
            return (File ?? "?") + " " + Digest.ToString("x16", CultureInfo.InvariantCulture) + (Overridden ? " (override)" : string.Empty);
        }
    }
}
