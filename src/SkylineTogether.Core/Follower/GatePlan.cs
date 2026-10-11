using System.Collections.Generic;

namespace SkylineTogether.Core.Follower
{
    /// <summary>
    /// What the client gate controller does (ARCH §6.3), computed by <see cref="GatePlanner.Plan"/>. The four name
    /// lists are disjoint, hold only systems that resolved, and are in ordinal order.
    /// </summary>
    public sealed class GatePlan
    {
        /// <summary>
        /// Systems set to <c>Enabled = false</c> for the whole session: the table's Off and OffAndFeed entries, the
        /// adapters' Off entries, and every other system registered in GameSimulation, EditorSimulation or
        /// LoadSimulation (belt and braces, ARCH §6.1). Applied in the session load's <c>OnGamePreload</c>, re-asserted
        /// in <c>OnGameLoaded</c>, <c>OnGameLoadingComplete</c> and every 60 frames, restored when the session ends.
        /// </summary>
        public IReadOnlyList<string> Disable;

        /// <summary>The session-load set: disabled only from a session load's <c>OnGamePreload</c> until its
        /// <c>OnGameLoadingComplete</c> (ARCH §6.3, §7.4).</summary>
        public IReadOnlyList<string> SessionLoadDisable;

        /// <summary>Systems that stay enabled (keep list, Keep entries, the cosmetic systems the follower clock runs
        /// directly, <c>GameModeGovernmentSubsidiesSystem</c>); informational and for the unclassified report.</summary>
        public IReadOnlyList<string> Keep;

        /// <summary>
        /// Systems owned by tool-scoped gates (ARCH §6.3, §4.11.4): off on clients except inside their tool's preview
        /// window. The gate controller never touches them; the tool-scoped gates disable them at start and flip them at
        /// the window edges. A tool-scoped system is never in <see cref="Disable"/> or <see cref="Keep"/>.
        /// </summary>
        public IReadOnlyList<string> ToolScoped;

        /// <summary>The checks of this plan.</summary>
        public GateReport Report;

        public override string ToString()
        {
            return "gate plan: disable " + Count(Disable) + ", session-load " + Count(SessionLoadDisable) + ", tool-scoped "
                   + Count(ToolScoped) + ", keep " + Count(Keep) + "; " + (Report == null ? "no report" : Report.ToString());
        }

        private static int Count(IReadOnlyList<string> list) => list == null ? 0 : list.Count;
    }
}
