namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// How the <see cref="Lane"/>s map onto a transport's own message lanes (ARCHITECTURE.md#outbox-lanes-and-rate-control):
    /// Steam Networking Sockets lanes when the game layer could configure them, the network simulator's lane
    /// priorities in tests. Each lane is its own Steam lane with its own reliable ordering, so a lost packet of
    /// snapshot chunks never holds back a control message, a chat line or a presence update behind it.
    /// <para>
    /// All lanes have the same priority and share the send rate by weight: when every lane has data, R gets 4/7, U 2/7
    /// and B 1/7; a lane without data leaves its share to the others. Equal priorities keep bulk transfers (snapshot
    /// chunks, water tiles) from starving while the reliable lane is busy; the weights keep a control message from
    /// waiting behind megabytes of chunks. <see cref="PeerOutbox"/> still holds bulk messages back while a reliable
    /// message is queued and keeps bulk inside its window, which is all the fairness there is when the transport has no
    /// lanes.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class LanePlan
    {
        /// <summary>Number of transport lanes (one per <see cref="Lane"/>).</summary>
        public const int Count = 3;

        /// <summary>Steam lane priorities by lane index (lower is more urgent; all equal: weighted sharing, no starvation).</summary>
        public static int[] Priorities() => new[] { 0, 0, 0 };

        /// <summary>Steam lane weights by lane index: R 4, U 2, B 1.</summary>
        public static ushort[] Weights() => new ushort[] { 4, 2, 1 };

        /// <summary>The transport lane index of <paramref name="lane"/> (the enum value).</summary>
        public static int IndexOf(Lane lane) => (int)lane;

        /// <summary>The lane of a received message's transport lane index; an index this build does not use counts as R.</summary>
        public static Lane LaneOf(int index) => index >= 0 && index < Count ? (Lane)index : Lane.Reliable;
    }
}
