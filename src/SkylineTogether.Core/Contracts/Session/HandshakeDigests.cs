namespace SkylineTogether.Core
{
    /// <summary>
    /// The digests a client sends in <c>Hello</c> (ARCHITECTURE.md#handshake-and-admission): versions, then one digest per
    /// compared category (<see cref="Session.CompatibilityDigests"/>). The host compares them after the password; only a
    /// category whose digest differs has its lists (from the client's <c>ManifestDetail</c>) compared item by item.
    /// </summary>
    public struct HandshakeDigests
    {
        public int ProtocolVersion;
        /// <summary>The build id of Skyline Together (<see cref="Session.BuildId"/>).</summary>
        public ulong ModBuild;
        /// <summary><c>Game.Version.current</c> (short version).</summary>
        public string GameVersion;
        /// <summary><c>Game.Version.current.fullVersion</c>.</summary>
        public string GameFullVersion;
        /// <summary>Digest of the loaded code mods (name, version, Paradox Mods id).</summary>
        public ulong Mods;
        /// <summary>Digest of the DLC and content packs whose prefabs are available.</summary>
        public ulong Content;
        /// <summary>Digest of the asset packs (Paradox Mods packages that add prefabs), each with the digest of its prefab ids.</summary>
        public ulong AssetPacks;
        /// <summary>Digest of the simulation-relevant mod settings the host pushes (<c>Compat.ModSettingsSync</c>).</summary>
        public ulong ModSettings;
        /// <summary>The active locale (informational).</summary>
        public string Locale;
    }
}
