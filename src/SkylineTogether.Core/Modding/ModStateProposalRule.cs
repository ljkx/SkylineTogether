namespace SkylineTogether.Core.Modding
{
    /// <summary>
    /// Client (docs/design/MOD-COMPATIBILITY.md): whether untagged mod state that changed on this client is the player's
    /// edit, to be proposed to the host, or the work of the client's own copy of a mod system (one that runs on every
    /// peer and recomputes a saved component from local state), which must never overwrite the host's value: the host
    /// derives its own, and proposing it made one client's local derivation the city's value (with several clients,
    /// a ping-pong). A change is an edit when a tool is applying (vanilla or a mod's direct-write tool) or the player
    /// pressed or released a button within <see cref="InputWindowSeconds"/>: an edit through a mod's panel or hotkey
    /// always follows input. A mod system's derivation during the player's input is still proposed, as before. Pure.
    /// </summary>
    public static class ModStateProposalRule
    {
        /// <summary>How long after the player's last button press or release a mod state change still counts as an edit.</summary>
        public const double InputWindowSeconds = 3.0;

        /// <param name="toolApplying">A tool applies this frame (the vanilla apply, or a mod's direct-write tool is active).</param>
        /// <param name="now">Now (seconds).</param>
        /// <param name="lastInput">The last button edge (seconds; positive infinity when input cannot be read: everything counts).</param>
        public static bool MayPropose(bool toolApplying, double now, double lastInput)
        {
            if (toolApplying) return true;
            if (double.IsPositiveInfinity(lastInput) || double.IsNaN(lastInput)) return true;
            return now - lastInput <= InputWindowSeconds;
        }
    }
}
