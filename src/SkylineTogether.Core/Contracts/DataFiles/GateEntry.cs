namespace SkylineTogether.Core
{
    /// <summary>One system of the gate table (ARCH §6.3), resolved by full type name in game and mod assemblies.</summary>
    public sealed class GateEntry
    {
        /// <summary>Full type name of the system, e.g. <c>Game.Simulation.XPSystem</c>.</summary>
        public string TypeName;
        /// <summary>Update phase (informational; the gate works whatever the phase).</summary>
        public string Phase;
        /// <summary>A critical entry must resolve, or the client refuses to join with a clear message.</summary>
        public bool Critical;
        public GateAction Action;
        /// <summary>Why the system is gated (documentation shown in diagnostics).</summary>
        public string Reason;

        public override string ToString() => Action + " " + (TypeName ?? "?") + (Critical ? " (critical)" : string.Empty);
    }
}
