namespace SkylineTogether.Core
{
    /// <summary>One mod setting in the handshake manifest or the pushed session settings (ARCHITECTURE.md#mod-compatibility).</summary>
    public sealed class SettingEntry
    {
        public string Mod;
        public string Key;
        /// <summary>The value in invariant text form.</summary>
        public string Value;
        /// <summary>Declared simulation-relevant by the mod's adapter: a mismatch blocks the join unless pushable.</summary>
        public bool SimulationRelevant;
        /// <summary>The host may push its value to joiners before their load.</summary>
        public bool Pushable;

        public override string ToString() => (Mod ?? "?") + "." + (Key ?? "?") + " = " + (Value ?? "null");
    }
}
