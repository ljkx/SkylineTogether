namespace SkylineTogether.Core
{
    /// <summary>
    /// The digests a client sends in <c>Hello</c> (ARCH §7.1 step 2, §7.9). Digests come first; full lists are
    /// exchanged only for the categories whose digests differ.
    /// </summary>
    public struct HandshakeDigests
    {
        public int ProtocolVersion;
        /// <summary>Hash of both mod assemblies.</summary>
        public ulong ModBuild;
        /// <summary><c>Game.Version.current</c> (<c>m_Version</c>).</summary>
        public string GameVersion;
        /// <summary><c>Game.Version.current.fullVersion</c>.</summary>
        public string GameFullVersion;
        /// <summary>Digest of the <c>FormatTags</c> names.</summary>
        public ulong FormatTags;
        /// <summary>Type catalog fingerprint (includes the policy-table version).</summary>
        public ulong Catalog;
        /// <summary>Digest of the sorted prefab list (duplicates included).</summary>
        public ulong Prefabs;
        /// <summary>Digest of the code-mod and UI-module manifest.</summary>
        public ulong Mods;
        /// <summary>DLC and content digest.</summary>
        public ulong Content;
        /// <summary>Digest of the simulation-relevant mod settings.</summary>
        public ulong ModSettings;
        /// <summary>Digest over the effective content of every data file (ARCH §4.2.7).</summary>
        public ulong DataFiles;
        /// <summary>The active locale (a difference is only a warning).</summary>
        public string Locale;
    }
}
