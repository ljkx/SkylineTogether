using SkylineTogether.Core.Diagnostics;

namespace SkylineTogether.Diagnostics
{
    /// <summary>
    /// Every switchable feature of the mod with its health (ARCH §12.1-12.3, D6). <see cref="Probes"/> sets the startup
    /// results; <see cref="Guard"/> counts run-time failures and turns a feature off after repeated errors. Features
    /// check <see cref="FeatureHealth.IsAvailable"/> at their entry points. Critical features cannot be degraded: a
    /// failed probe blocks hosting and joining, a run-time trip ends the session (a client then rejoins).
    /// </summary>
    public static class Features
    {
        public static readonly FeatureRegistry Registry = new FeatureRegistry();

        /// <summary>The session itself (ticking, handshake, join, chat).</summary>
        public static readonly FeatureHealth Session = Registry.Add("session", "session", true);

        /// <summary>Steam API and Steam Networking Sockets.</summary>
        public static readonly FeatureHealth Steam = Registry.Add("steam", "Steam networking", true);

        /// <summary>Structure replication (capture, apply, proposals, net ids, the serialization context).</summary>
        public static readonly FeatureHealth Replication = Registry.Add("replication", "replication", true);

        /// <summary>One game speed for everyone; the host's hold while players join.</summary>
        public static readonly FeatureHealth Speed = Registry.Add("speed", "game speed authority", true);

        /// <summary>Follower gating of vanilla systems on clients (names are resolved; missing ones are warnings).</summary>
        public static readonly FeatureHealth Gate = Registry.Add("gate", "follower gate", false);

        /// <summary>The client's follower clock (time runs, gameplay simulation off). Fallback: a paused client.</summary>
        public static readonly FeatureHealth Clock = Registry.Add("clock", "follower clock", false);

        /// <summary>Taxes, budgets, fees, loans, policies, names, demand.</summary>
        public static readonly FeatureHealth GlobalState = Registry.Add("global", "city-wide settings sync", false);

        /// <summary>The client's icon authority filter.</summary>
        public static readonly FeatureHealth Icons = Registry.Add("icons", "warning icon filter", false);

        /// <summary>Vehicles and pedestrians.</summary>
        public static readonly FeatureHealth Movers = Registry.Add("movers", "vehicles and pedestrians", false);

        public static readonly FeatureHealth Terrain = Registry.Add("terrain", "terrain sync", false);

        public static readonly FeatureHealth Water = Registry.Add("water", "water sync", false);

        /// <summary>Divergence detection and repair, host icons, line check.</summary>
        public static readonly FeatureHealth Integrity = Registry.Add("integrity", "divergence checks", false);

        /// <summary>w2/simvals: attention values, inspection samples, resource/pollution cell maps, city statistics.</summary>
        public static readonly FeatureHealth SimValues = Registry.Add("simvals", "city statistics and maps", false);

        /// <summary>Gameplay branch: player actions outside the tools (dev tree, map tiles, milestones, UI writes).</summary>
        public static readonly FeatureHealth Actions = Registry.Add("actions", "player actions sync", false);

        /// <summary>
        /// w3/audit: events and disasters (fires, accidents, weather phenomena, floods), their marks on buildings and
        /// roads (presence watch), the chirper feed and the event journal.
        /// </summary>
        public static readonly FeatureHealth Events = Registry.Add("events", "events, disasters and chirper", false);

        /// <summary>Remote cursors and name tags.</summary>
        public static readonly FeatureHealth Presence = Registry.Add("presence", "player cursors", false);

        /// <summary>The in-game panel (UI module and its bindings). Fallback: the Options page.</summary>
        public static readonly FeatureHealth Ui = Registry.Add("ui", "in-game panel", false);
    }
}
