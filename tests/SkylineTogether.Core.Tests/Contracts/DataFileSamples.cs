using SkylineTogether.Core.Json;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>
    /// Sample data files in the documented schemas (written with single quotes; <see cref="J"/> turns them into JSON).
    /// They double as schema examples for the authors of the shipped files in <c>src/SkylineTogether/Data</c>.
    /// </summary>
    internal static class DataFileSamples
    {
        public static JsonValue J(string singleQuoted) => MiniJson.Parse(singleQuoted.Replace('\'', '"'));

        public const string Gates = @"{
  'version': 1,
  '_comment': 'initial client gate table (ARCH Appendix E)',
  'entries': [
    { 'type': 'Game.Simulation.XPSystem', 'phase': 'ModificationEnd', 'critical': true, 'reason': 'rewards must not apply twice' },
    { 'type': 'Game.Simulation.CityServiceBudgetSystem', 'phase': 'ModificationEnd', 'action': 'OffAndFeed' },
    { 'type': 'Game.Tools.UpgradeDeletedSystem', 'phase': 'ToolUpdate', '_note': 'would duplicate host-rebuilt sub-nets' },
    { 'type': 'Game.Prefabs.UnlockSystem', 'action': 'keep', 'reason': 'processes synthesised Unlock events' }
  ],
  'sessionLoad': [
    { 'type': 'Game.Serialization.ResetBuildOrderSystem', 'phase': 'Deserialize', 'critical': true },
    { 'type': 'Game.Serialization.CitizenSystem', 'phase': 'Deserialize', 'critical': true },
    { 'type': 'Game.Serialization.RandomLocalizationSystem', 'phase': 'Deserialize', 'action': 'OffDuringSessionLoad' }
  ],
  'toolScoped': [
    { 'tool': 'Game.Tools.RouteToolSystem', 'systems': [ 'Game.Routes.WaypointConnectionSystem', 'Game.Routes.RoutePathSystem' ], 'lingerSeconds': 2, 'guard': 'RouteGuard' },
    { 'tool': 'MoveIt.Tools.MoveItTool', 'systems': [ 'Game.Routes.WaypointConnectionSystem' ] }
  ]
}";

        public const string Cosmetic = @"{
  'version': 1,
  'systems': [
    'Game.Simulation.TimeSystem', 'Game.Simulation.ClimateSystem', 'Game.Simulation.SnowSystem', 'Game.Simulation.WindSimulationSystem',
    'Game.Simulation.WindSystem', 'Game.Simulation.ZoneAmbienceSystem', 'Game.Simulation.SpawnableAmbienceSystem',
    'Game.Simulation.WetnessSystem', 'Game.Simulation.DirtynessSystem'
  ]
}";

        public const string Policies = @"{
  'version': 3,
  'familyDefaults': { 'NetEdge': 'S', 'Agent': 'A', 'Mover': 'M', 'transientAgent': 'ALow' },
  'types': [
    { 'type': 'Game.Objects.Surface', 'excluded': true, '_why': 'cosmetic wetness, simulated locally' },
    { 'type': 'Game.Net.Road', 'playerAuthorable': true, 'authorableMask': 'Road', 'topics': [ 'Traffic' ] },
    { 'type': 'Game.Buildings.Building', 'deferredFields': 'Building', 'authorableMask': 'Building' },
    { 'type': 'Game.Triggers.Chirp', 'mergePolicy': 'Chirp' },
    { 'type': 'Game.Pathfind.PathInformation', 'onDemand': true },
    { 'type': 'Game.Pathfind.PathInformation', 'family': 'Segment', 'class': 'S', 'source': true, 'cacheReceived': true, 'guarded': true,
      'standInResolver': 'SegmentPathEndpoints' },
    { 'type': 'Game.Buildings.Renter', 'dataReplicated': true, 'hashAsSet': true, 'codec': 'EntityRefList' },
    { 'type': 'Game.Citizens.HouseholdMember', 'reverseBuffers': [ 'Game.Citizens.HouseholdCitizen' ] },
    { 'type': 'Game.Net.Upgraded', 'playerAuthorable': true, 'applyTags': [ 'PathfindUpdated', 'Updated' ] },
    { 'type': 'Game.Net.TrafficLights', 'presenceAuthorable': true, 'noReapply': false },
    { 'type': 'Game.Vehicles.PathOwner', 'class': 'OnDemand' }
  ]
}";

        public const string Adapters = @"{
  'version': 1,
  'adapters': [
    { 'mod': 'Anarchy', 'assembly': 'Anarchy', 'versions': '*', 'class': 'WorldAffecting',
      'clientOff': [ 'Anarchy.Systems.OverridePrevention.PreventOverrideSystem', 'Anarchy.Systems.ResetTransformSystem' ],
      'clientKeep': [ 'Anarchy.Systems.OverridePrevention.AnarchyPlopSystem' ],
      'cosmetic': [],
      'joinDisable': [],
      'localTypes': [],
      'playerMutableTypes': [ 'Anarchy.Components.PreventOverride', 'Anarchy.Components.TransformRecord' ],
      'directWriteTools': [ 'Anarchy.Tools.AnarchyComponentsToolSystem' ],
      'hostOnlyActions': [],
      'simSettings': [ 'AllowPlacingMultipleUniqueBuildings', 'MinimumClearanceBelowElevatedNetworks' ],
      'pushSettings': true,
      'resendAfterJoinTypes': [],
      'systemStateSync': [],
      'toolScoped': [],
      'localIconPrefabs': [],
      'notes': 'components generic; Transform and TransformRecord in one record' },
    { 'mod': 'Water Features', 'assembly': 'Water_Features', 'versions': '1.*',
      'directWriteTools': [ 'Water_Features.Tools.CustomWaterToolSystem' ],
      'resendAfterJoinTypes': [ 'Game.Simulation.WaterSourceData' ],
      'hostOnlyActions': [ 'Cleanup cycle' ],
      'systemStateSync': [ { 'id': 'WaterFeatures.Parameters', 'system': 'Water_Features.Systems.WaterParametersSystem', 'codec': 'Custom', 'maxHz': 0.5 } ],
      'toolScoped': [ { 'tool': 'Water_Features.Tools.CustomWaterToolSystem', 'systems': [ 'Game.Simulation.WaterSystem' ], 'lingerSeconds': 1 } ] },
    { 'mod': 'Find It', 'assembly': 'FindIt', 'class': 'LocalOnly', 'localIconPrefabs': [ 'FindIt Marker' ] }
  ]
}";

        public const string SystemState = @"{
  'version': 1,
  'entries': [
    { 'id': 'TaxSystem', 'system': 'Game.Simulation.TaxSystem', 'maxHz': 1, 'readerHandleMember': 'Readers' },
    { 'id': 'ResidentialDemand', 'system': 'Game.Simulation.ResidentialDemandSystem' },
    { 'id': 'CityStatistics', 'system': 'Game.City.CityStatisticsSystem', 'codec': 'DeserializeOnly', 'maxHz': 0.25 },
    { 'id': 'Climate', 'system': 'Game.Simulation.ClimateSystem', 'codec': 'Custom', 'maxHz': 0.2, 'requiresRoundTrip': false }
  ]
}";

        public const string Locale = @"{
  '_comment': 'English strings; the fallback for every other locale',
  'st.menu.coop': 'Co-op',
  'st.hud.sync.synced': 'Synced',
  'st.toast.rejected.conflict': 'Conflicts with the edit of {0}',
  'st.empty': ''
}";
    }
}
