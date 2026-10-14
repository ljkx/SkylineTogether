using System.Collections.Generic;

namespace SkylineTogether.Core
{
    /// <summary>
    /// The lists behind the handshake digests (ARCHITECTURE.md#handshake-and-admission), sent by the client as
    /// <c>ManifestDetail</c> together with its hello and compared by the host after the password
    /// (<see cref="Session.CompatibilityCheck"/>). Serialised by <see cref="Protocol.ManifestDetailMessage"/>; a null list
    /// means "not provided" (too large to send).
    /// </summary>
    public sealed class HandshakeManifest
    {
        public HandshakeDigests Digests;
        public List<ModEntry> Mods = new List<ModEntry>();
        /// <summary>DLC and content packs whose prefabs are available (owned and installed).</summary>
        public List<ContentEntry> Content = new List<ContentEntry>();
        /// <summary>Asset packs: Paradox Mods packages that add prefabs (code mods shipping assets included).</summary>
        public List<ContentEntry> AssetPacks = new List<ContentEntry>();
        /// <summary>The current values of the simulation-relevant mod settings the host pushes.</summary>
        public List<SettingEntry> Settings = new List<SettingEntry>();
    }
}
