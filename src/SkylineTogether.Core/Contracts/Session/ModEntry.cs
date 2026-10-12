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
        /// <summary>The sending peer scanned its copy of the mod and found nothing that can touch shared state (no
        /// serializable component, saved system, prefab, tool or patch library): eligible for the client-local relaxation
        /// unless the built-in list knows it as world-affecting (Session.ModCompatibility).</summary>
        public bool ClientLocalHint;

        public override string ToString() => (Name ?? "?") + " " + (Version ?? "?");
    }
}
