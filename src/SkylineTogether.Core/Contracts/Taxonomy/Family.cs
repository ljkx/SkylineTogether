namespace SkylineTogether.Core
{
    /// <summary>
    /// Entity families of the classification rule table (ARCHITECTURE.md#families; implemented by core-identity's
    /// <c>FamilyRules</c>). The numeric values are stored in side tables and travel in records; never reorder.
    /// </summary>
    public enum Family : byte
    {
        /// <summary>Rule 1: Temp, LivePath, null-prefab or adapter-local entities; never replicated.</summary>
        ToolLocal,
        /// <summary>Rule 2: prefab entities; identity by rank only.</summary>
        Prefab,
        /// <summary>Rule 3: lanes; derived, portable key when referenced.</summary>
        Lane,
        /// <summary>Rule 4: zone blocks; derived, cells through the K channel.</summary>
        ZoneBlock,
        /// <summary>Rule 5: notification icons; icon channel.</summary>
        Icon,
        /// <summary>Rule 6: secondary objects; derived, keyed state.</summary>
        SecondaryObject,
        /// <summary>Rule 7: prefab sub-objects; derived, keyed state.</summary>
        PrefabSubObject,
        /// <summary>Rule 8: electricity and water flow graphs; derived.</summary>
        FlowGraph,
        /// <summary>Rule 9: routes owned by objects (gate-bypass paths); derived, K state.</summary>
        OwnedRoute,
        /// <summary>Rule 10: net nodes.</summary>
        NetNode,
        /// <summary>Rule 10: net edges.</summary>
        NetEdge,
        /// <summary>Rule 11: aggregates (street identity).</summary>
        Aggregate,
        /// <summary>Rule 12: vehicles and creatures.</summary>
        Mover,
        /// <summary>Rule 13: every other object (buildings, props, trees, upgrades, extensions, ...).</summary>
        Object,
        /// <summary>Rule 14: areas.</summary>
        Area,
        /// <summary>Rule 15: routes.</summary>
        Route,
        /// <summary>Rule 15: route waypoints.</summary>
        Waypoint,
        /// <summary>Rule 15: route segments.</summary>
        Segment,
        /// <summary>Rule 16: citizens, households, pets, companies.</summary>
        Agent,
        /// <summary>Rule 17: service requests, seekers, meetings.</summary>
        TransientAgent,
        /// <summary>Rule 18: persistent events, journal entries, chirps, life-path events.</summary>
        Event,
        /// <summary>Rule 19: city statistics.</summary>
        Statistic,
        /// <summary>Rule 20: City, budget entity, TimeData, palette.</summary>
        Singleton,
        /// <summary>Rule 21: water sources (may lack PrefabRef).</summary>
        WaterSource,
        /// <summary>Rule 22: entities matched only through a non-Game serializable type.</summary>
        ModEntity,
        /// <summary>Rule 23: anything else in the save query.</summary>
        Generic
    }
}
