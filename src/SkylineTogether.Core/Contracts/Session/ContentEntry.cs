namespace SkylineTogether.Core
{
    /// <summary>
    /// One DLC or content pack, or one asset pack, in the handshake manifest (ARCHITECTURE.md#handshake-and-admission).
    /// Compared by <see cref="Id"/> when it is set, otherwise by <see cref="Name"/>.
    /// </summary>
    public sealed class ContentEntry
    {
        /// <summary>The DLC id (decimal) or the Paradox Mods id of an asset pack; may be empty.</summary>
        public string Id;
        /// <summary>The display name (DLC or package name).</summary>
        public string Name;
        /// <summary>The asset pack's Paradox Mods version; empty for DLC and when unknown.</summary>
        public string Version;
        /// <summary>Asset packs: the digest of the pack's sorted prefab ids (two copies with the same id but other contents differ); 0 for DLC.</summary>
        public ulong Digest;
        /// <summary>Asset packs: how many prefabs the pack adds; 0 for DLC.</summary>
        public int Prefabs;

        /// <summary>The comparison key: <c>id:</c> plus the id, or <c>name:</c> plus the name.</summary>
        public string Key => !string.IsNullOrEmpty(Id) ? "id:" + Id : "name:" + (Name ?? "");

        public override string ToString() => (string.IsNullOrEmpty(Name) ? "?" : Name) + (string.IsNullOrEmpty(Id) ? "" : " (" + Id + ")");
    }
}
