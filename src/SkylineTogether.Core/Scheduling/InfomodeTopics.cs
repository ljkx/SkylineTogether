namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// The attention topics of the game's infomodes (ARCH §4.8.3: topics are "mapped to what <c>ObjectColorSystem</c>
    /// actually reads"). The client game layer that builds the view report ORs the topics of every active infomode
    /// (<c>InfomodeActive</c>) of the active infoview into <see cref="Protocol.ViewReport.InfomodeMask"/>; the host reads
    /// them back with <see cref="TopicMap.FromInfoview"/>. Core cannot reference the game's enums, so the methods take
    /// their numeric values; the tables mirror the decompiled enums (order verified):
    /// <c>Game.Prefabs.BuildingStatusType</c> (31 values), <c>Game.Prefabs.NetStatusType</c> (13),
    /// <c>Game.Rendering.HeatmapData</c> (20) and <c>Game.Prefabs.ObjectStatusType</c> (6). Unknown values map to
    /// <see cref="AttentionTopic.None"/>: a wrong or newer infomode only costs freshness, never correctness.
    /// </summary>
    public static class InfomodeTopics
    {
        private const AttentionTopic Residents = AttentionTopic.Happiness | AttentionTopic.Health | AttentionTopic.Wellbeing
                                                 | AttentionTopic.Education | AttentionTopic.Age;

        // Game.Prefabs.BuildingStatusType, ObjectColorSystem.GetBuildingStatusColors (ObjectColorSystem.cs:846-1300)
        private static readonly AttentionTopic[] BuildingStatus =
        {
            AttentionTopic.Crime,            // CrimeProbability: CrimeProducer.m_Crime
            AttentionTopic.Mail,             // MailAccumulation: MailProducer
            AttentionTopic.Wealth,           // Wealth: Household + Resources of renters
            AttentionTopic.Education,        // Education: Citizen of residents
            AttentionTopic.None,             // Level: prefab data (structure)
            AttentionTopic.Health,           // Health: Citizen of residents
            AttentionTopic.Garbage,          // GarbageAccumulation: GarbageProducer
            AttentionTopic.Profitability,    // Profitability: CompanyStatisticData of renters
            AttentionTopic.Age,              // Age: Citizen of residents
            AttentionTopic.None,             // LeisureProvider: renter presence
            AttentionTopic.Happiness,        // Happiness: Citizen of residents
            AttentionTopic.Electricity,      // ElectricityConsumption: ElectricityConsumer
            AttentionTopic.Telecom,          // NetworkQuality: telecom coverage map
            AttentionTopic.Pollution,        // AirPollutionSource
            AttentionTopic.Pollution,        // GroundPollutionSource
            AttentionTopic.Noise,            // NoisePollutionSource
            AttentionTopic.Wellbeing,        // Wellbeing: Citizen of residents
            AttentionTopic.None,             // LodgingProvider: renter prefab data
            AttentionTopic.Pollution,        // WaterPollutionSource
            AttentionTopic.LandValue,        // LandValue
            AttentionTopic.Water,            // WaterConsumption: WaterConsumer
            AttentionTopic.None,             // ResidentialBuilding: renter count
            AttentionTopic.None,             // CommercialBuilding
            AttentionTopic.None,             // IndustrialBuilding
            AttentionTopic.None,             // OfficeBuilding
            AttentionTopic.None,             // SignatureResidential
            AttentionTopic.None,             // SignatureCommercial
            AttentionTopic.None,             // SignatureIndustrial
            AttentionTopic.None,             // SignatureOffice
            AttentionTopic.None,             // HomelessCount
            AttentionTopic.Profitability     // OutsideTrading: CurrentTrading of renter companies
        };

        // Game.Prefabs.NetStatusType
        private static readonly AttentionTopic[] NetStatus =
        {
            AttentionTopic.None,             // Wear
            AttentionTopic.Traffic,          // TrafficFlow: Road flow
            AttentionTopic.Noise,            // NoisePollutionSource
            AttentionTopic.Pollution,        // AirPollutionSource
            AttentionTopic.Traffic,          // TrafficVolume
            AttentionTopic.Electricity,      // LowVoltageFlow: flow-status channel
            AttentionTopic.Electricity,      // HighVoltageFlow
            AttentionTopic.Water,            // PipeWaterFlow
            AttentionTopic.Water,            // PipeSewageFlow
            AttentionTopic.None,             // OilFlow
            AttentionTopic.None,             // LeisureProvider
            AttentionTopic.None,             // BicycleLanes
            AttentionTopic.Traffic           // BicycleTrafficVolume
        };

        // Game.Rendering.HeatmapData (cell maps, ARCH §4.10.8)
        private static readonly AttentionTopic[] Heatmap =
        {
            AttentionTopic.None,             // None
            AttentionTopic.Groundwater,      // GroundWater
            AttentionTopic.Pollution,        // GroundPollution
            AttentionTopic.Pollution,        // AirPollution
            AttentionTopic.None,             // Wind (client-local)
            AttentionTopic.None,             // WaterFlow (client-local water simulation)
            AttentionTopic.Telecom,          // TelecomCoverage
            AttentionTopic.NaturalResources, // Fertility
            AttentionTopic.NaturalResources, // Ore
            AttentionTopic.NaturalResources, // Oil
            AttentionTopic.LandValue,        // LandValue
            AttentionTopic.None,             // Attraction
            AttentionTopic.None,             // Customers
            AttentionTopic.None,             // Workplaces
            AttentionTopic.None,             // Services
            AttentionTopic.Noise,            // Noise
            AttentionTopic.Pollution,        // WaterPollution
            AttentionTopic.None,             // Population
            AttentionTopic.Groundwater | AttentionTopic.Pollution,   // GroundWaterPollution
            AttentionTopic.NaturalResources  // Fish
        };

        // Game.Prefabs.ObjectStatusType
        private static readonly AttentionTopic[] ObjectStatus =
        {
            AttentionTopic.NaturalResources, // WoodResource
            AttentionTopic.None,             // FireHazard
            AttentionTopic.None,             // Damage
            AttentionTopic.None,             // Destroyed
            AttentionTopic.NaturalResources, // ExtractorPlaceholder
            AttentionTopic.None              // Tourist
        };

        /// <summary><c>ServiceCoverageInfomodePrefab</c> (any <c>CoverageService</c>): edge <c>ServiceCoverage</c>.</summary>
        public const AttentionTopic ServiceCoverageInfomode = AttentionTopic.Coverage;

        /// <summary><c>RouteInfomodePrefab</c>, <c>TransportStopInfomodePrefab</c>, <c>VehicleInfomodePrefab</c>: the transport infoview.</summary>
        public const AttentionTopic TransportInfomode = AttentionTopic.Transport;

        /// <summary><c>ZoneSuitabilityInfomodePrefab</c>: zone suitability follows land value.</summary>
        public const AttentionTopic ZoneSuitabilityInfomode = AttentionTopic.LandValue;

        /// <summary>The topics shared by every resident-based building status (happiness, health, wellbeing, education, age).</summary>
        public const AttentionTopic ResidentTopics = Residents;

        /// <summary><c>BuildingStatusInfomodePrefab</c> / <c>BuildingStateInfomodePrefab</c> with this
        /// <c>Game.Prefabs.BuildingStatusType</c> value.</summary>
        public static AttentionTopic ForBuildingStatus(int buildingStatusType) => Lookup(BuildingStatus, buildingStatusType);

        /// <summary><c>NetStatusInfomodePrefab</c> with this <c>Game.Prefabs.NetStatusType</c> value.</summary>
        public static AttentionTopic ForNetStatus(int netStatusType) => Lookup(NetStatus, netStatusType);

        /// <summary><c>HeatmapInfomodePrefab</c> with this <c>Game.Rendering.HeatmapData</c> value.</summary>
        public static AttentionTopic ForHeatmap(int heatmapData) => Lookup(Heatmap, heatmapData);

        /// <summary><c>ObjectStatusInfomodePrefab</c> / <c>ObjectStateInfomodePrefab</c> with this
        /// <c>Game.Prefabs.ObjectStatusType</c> value.</summary>
        public static AttentionTopic ForObjectStatus(int objectStatusType) => Lookup(ObjectStatus, objectStatusType);

        /// <summary>The value to put into <see cref="Protocol.ViewReport.InfomodeMask"/> for these topics.</summary>
        public static uint ToInfomodeMask(AttentionTopic topics) => (uint)(topics & TopicMap.AllTopics);

        /// <summary>Number of values each table knows (tests compare them with the decompiled enums).</summary>
        internal static int BuildingStatusCount => BuildingStatus.Length;

        internal static int NetStatusCount => NetStatus.Length;

        internal static int HeatmapCount => Heatmap.Length;

        internal static int ObjectStatusCount => ObjectStatus.Length;

        private static AttentionTopic Lookup(AttentionTopic[] table, int value) => value >= 0 && value < table.Length ? table[value] : AttentionTopic.None;
    }
}
