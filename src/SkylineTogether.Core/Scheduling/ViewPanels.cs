namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// Bits of <see cref="Protocol.ViewReport.PanelFlags"/>: the panels a player has open (ARCHITECTURE.md#simulation-values). The client game
    /// layer that builds the view report sets them; <see cref="TopicMap.FromPanels"/> turns them into attention topics on
    /// the host. Bits not defined here are ignored by the host (a newer client may send more).
    /// </summary>
    public static class ViewPanels
    {
        public const ushort None = 0;

        /// <summary>The transportation overview (line list) is open: raises <see cref="AttentionTopic.Transport"/>, i.e.
        /// the line values of every line (ARCHITECTURE.md#simulation-values).</summary>
        public const ushort TransportationOverview = 1 << 0;

        /// <summary>The statistics panel is open: raises <see cref="AttentionTopic.Statistics"/> (city statistic buffers).</summary>
        public const ushort Statistics = 1 << 1;

        /// <summary>A line's info panel is open (<see cref="Protocol.ViewReport.PanelTarget"/> = the line). Its values reach
        /// the player through the inspection closure (time-aligned samples, ARCHITECTURE.md#simulation-values), so it raises no topic.</summary>
        public const ushort LinePanel = 1 << 2;

        /// <summary>A stop's info panel is open (target = the stop); served by the closure like <see cref="LinePanel"/>.</summary>
        public const ushort StopPanel = 1 << 3;

        /// <summary>A district panel is open (target = the district); informational.</summary>
        public const ushort DistrictPanel = 1 << 4;

        /// <summary>The budget panel is open; budget arrays travel on their channel for every peer, so no topic.</summary>
        public const ushort BudgetPanel = 1 << 5;

        /// <summary>Every bit defined above.</summary>
        public const ushort DefinedMask = TransportationOverview | Statistics | LinePanel | StopPanel | DistrictPanel | BudgetPanel;
    }
}
