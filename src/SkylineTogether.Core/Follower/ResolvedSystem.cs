namespace SkylineTogether.Core.Follower
{
    /// <summary>
    /// One system as the game layer found it (input of <see cref="GatePlanner.Plan"/>). game-follower lists every
    /// registration of <c>UpdateSystem.m_Systems</c> (probe <c>P-SYSTEMS</c>, <c>UpdateSystem.cs:78</c>), one row per
    /// (system, phase) pair, plus a row with <see cref="Resolved"/> = false for every table name it could not find.
    /// A system registered in several phases (for example <c>GameModeGovernmentSubsidiesSystem</c> in GameSimulation
    /// and ModificationEnd, <c>SystemOrder.cs</c>) appears once per phase; the planner merges the rows by type name.
    /// </summary>
    public sealed class ResolvedSystem
    {
        /// <summary>Full type name, e.g. <c>Game.Simulation.XPSystem</c>.</summary>
        public string TypeName;

        /// <summary>
        /// The <c>SystemUpdatePhase</c> name of this registration (<c>"GameSimulation"</c>, <c>"Modification5"</c>, ...);
        /// null or empty when unknown (declared-list fallback, or a system that is created but not registered).
        /// </summary>
        public string Phase;

        /// <summary>The type and its system instance exist in the world.</summary>
        public bool Resolved;

        /// <summary>The system's <c>Enabled</c> flag when the plan was made.</summary>
        public bool CurrentlyEnabled;

        public ResolvedSystem()
        {
        }

        public ResolvedSystem(string typeName, string phase, bool resolved = true, bool currentlyEnabled = true)
        {
            TypeName = typeName;
            Phase = phase;
            Resolved = resolved;
            CurrentlyEnabled = currentlyEnabled;
        }

        public override string ToString()
        {
            return (TypeName ?? "?") + " @" + (string.IsNullOrEmpty(Phase) ? "?" : Phase)
                   + (Resolved ? string.Empty : " (unresolved)") + (CurrentlyEnabled ? string.Empty : " (disabled)");
        }
    }
}
