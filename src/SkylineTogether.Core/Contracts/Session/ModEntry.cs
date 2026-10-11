namespace SkylineTogether.Core
{
    /// <summary>One loaded code mod or UI module in the handshake manifest (ARCH §7.9, §10.2).</summary>
    public sealed class ModEntry
    {
        public string Name;
        public string Version;
        /// <summary>The Paradox Mods id (for "subscribe to missing mods" and Skyve).</summary>
        public string PlatformId;
        /// <summary>Hash of the mod's DLL.</summary>
        public string DllHash;
        public bool IsUiModule;
        /// <summary>The mod registers serializable types, simulation systems or prefabs (never eligible for the
        /// local-only relaxation).</summary>
        public bool RegistersTypesOrSystemsOrPrefabs;

        public override string ToString() => (Name ?? "?") + " " + (Version ?? "?");
    }
}
