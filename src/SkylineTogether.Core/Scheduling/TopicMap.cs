namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// Attention topics (ARCHITECTURE.md#simulation-values): decodes a client's view report into the topics it asks for. The design's per-type topic table (policy topics plus
    /// built-in defaults) belonged to the unbuilt worker-thread scheduler and was removed; the host collects topic data
    /// in <c>AttentionValues</c> (game layer).
    /// <para>
    /// View-report encoding (the client game layer builds it, this class decodes it on the host):
    /// <see cref="Protocol.ViewReport.Infoview"/> is 0 when no infoview is active and any other value (an opaque id of
    /// the client's choosing, used only in diagnostics) when one is; <see cref="Protocol.ViewReport.InfomodeMask"/> carries
    /// the <see cref="AttentionTopic"/> bits of the active infomodes, computed with <see cref="InfomodeTopics"/>;
    /// <see cref="Protocol.ViewReport.PanelFlags"/> uses the bits of <see cref="ViewPanels"/>.
    /// </para>
    /// Stateless; thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class TopicMap
    {
        /// <summary>Every defined topic bit (bits 0 ... 21).</summary>
        public const AttentionTopic AllTopics = (AttentionTopic)0x003FFFFFu;

        /// <summary>Topics whose data matters everywhere, not only in view: the line values of every line (transport
        /// infoview, transportation overview) and the city statistics (ARCHITECTURE.md#simulation-values). The topic data of every other
        /// topic belongs to in-view entities.</summary>
        public const AttentionTopic GlobalTopics = AttentionTopic.Transport | AttentionTopic.Statistics;

        /// <summary>
        /// The topics of the active infoview: none when <paramref name="infoviewId"/> is 0 (no infoview), otherwise the
        /// topic bits carried in <paramref name="infomodeMask"/> (see the class remarks; undefined bits are ignored).
        /// </summary>
        public static AttentionTopic FromInfoview(byte infoviewId, uint infomodeMask)
        {
            if (infoviewId == 0) return AttentionTopic.None;
            return (AttentionTopic)infomodeMask & AllTopics;
        }

        /// <summary>The topics raised by open panels: the transportation overview raises <see cref="AttentionTopic.Transport"/>
        /// (every line's values), the statistics panel <see cref="AttentionTopic.Statistics"/>; other panels raise none.</summary>
        public static AttentionTopic FromPanels(ushort panelFlags)
        {
            AttentionTopic topics = AttentionTopic.None;
            if ((panelFlags & ViewPanels.TransportationOverview) != 0) topics |= AttentionTopic.Transport;
            if ((panelFlags & ViewPanels.Statistics) != 0) topics |= AttentionTopic.Statistics;
            return topics;
        }

        /// <summary>The topics of a whole view report (infoview plus panels); none for null.</summary>
        public static AttentionTopic FromView(Protocol.ViewReport report)
        {
            if (report == null) return AttentionTopic.None;
            return FromInfoview(report.Infoview, report.InfomodeMask) | FromPanels(report.PanelFlags);
        }
    }
}
