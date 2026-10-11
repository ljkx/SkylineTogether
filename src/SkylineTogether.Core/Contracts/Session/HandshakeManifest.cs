using System.Collections.Generic;

namespace SkylineTogether.Core
{
    /// <summary>
    /// The detailed lists behind the handshake digests (ARCH §7.9), exchanged for mismatching categories and compared by
    /// core-session's <c>CompatibilityChecker</c>. Serialised by core-protocol (<c>ManifestDetailMessage</c>).
    /// </summary>
    public sealed class HandshakeManifest
    {
        public HandshakeDigests Digests;
        public List<ModEntry> Mods = new List<ModEntry>();
        /// <summary>Referenced content (DLC and asset packs).</summary>
        public List<string> Content = new List<string>();
        public List<string> FormatTagNames = new List<string>();
        /// <summary>Filled only on a prefab digest mismatch.</summary>
        public List<string> PrefabIds;
        public List<SettingEntry> Settings = new List<SettingEntry>();
        /// <summary>Filled only on a data-file digest mismatch (ARCH §4.2.7).</summary>
        public List<DataFileDigestEntry> DataFiles;
    }
}
