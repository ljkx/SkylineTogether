namespace SkylineTogether.Core
{
    /// <summary>One loaded code mod or UI module in the handshake manifest (ARCHITECTURE.md#handshake-and-admission, #mod-compatibility).</summary>
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
        /// local-only relaxation). For a client-local mod of the built-in list that was checked against one version
        /// (Session.ModCompatibility.HasVerifiedProfile): the sending peer's copy has more than that version.</summary>
        public bool RegistersTypesOrSystemsOrPrefabs;
        /// <summary>The sending peer scanned its copy of the mod and found nothing that can touch shared state (no
        /// serializable component, saved system, prefab, tool or patch library): eligible for the client-local relaxation
        /// unless the built-in list knows it as world-affecting (Session.ModCompatibility).</summary>
        public bool ClientLocalHint;

        /// <summary>
        /// Local to the peer that built the entry, never on the wire (the handshake manifest does not carry it): the
        /// peer's scan of its own copy found that the mod may store data in a saved city (saved component types or system
        /// state, or the scan failed; Modding.CityDataEvidence). The host refuses every joiner that lacks such a mod,
        /// whatever its options say (Session.CityDataRule). In a join notice or a refusal (Session.MismatchReport) the flag
        /// comes from the host's records.
        /// </summary>
        public bool StoresCityData;
        /// <summary>
        /// Local, like <see cref="StoresCityData"/>: the current city actually holds the mod's data (entities with one of
        /// its saved component types, or a system of it that saves state exists), counted when the manifest was built.
        /// </summary>
        public bool CityDataInCity;
        /// <summary>Local, like <see cref="StoresCityData"/>: what the data is, for the log ("saves 1 component type (...)").</summary>
        public string CityDataDetail;

        public override string ToString() => (Name ?? "?") + " " + (Version ?? "?");
    }
}
