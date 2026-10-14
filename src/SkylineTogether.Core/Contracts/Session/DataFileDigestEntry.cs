using System.Globalization;

namespace SkylineTogether.Core
{
    /// <summary>One data file of the handshake manifest (ARCHITECTURE.md#host-capture): its effective-content digest
    /// (of the canonical JSON; not populated by the shipped build) and whether an override file replaced the embedded copy.</summary>
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
