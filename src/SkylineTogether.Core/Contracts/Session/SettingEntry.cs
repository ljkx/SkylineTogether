namespace SkylineTogether.Core
{
    /// <summary>One mod setting in the handshake manifest or the pushed session settings (ARCHITECTURE.md#mod-compatibility).</summary>
    public sealed class SettingEntry
    {
        public string Mod;
        public string Key;
        /// <summary>The value in invariant text form.</summary>
        public string Value;
        /// <summary>Declared simulation-relevant by the mod's adapter (a difference is told to the joiner; the host's value applies).</summary>
        public bool SimulationRelevant;
        /// <summary>The host pushes its value to clients for the session (<c>Compat.ModSettingsSync</c>).</summary>
        public bool Pushable;

        public override string ToString() => (Mod ?? "?") + "." + (Key ?? "?") + " = " + (Value ?? "null");
    }
}
