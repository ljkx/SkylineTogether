using System.Collections.Generic;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// Plop the Growables (algernon, Paradox Mods 75826): <c>PloppedBuilding</c>, <c>SpawnedBuilding</c> and
    /// <c>LevelLocked</c> replicate with the building (the info-panel level lock toggle adds/removes <c>LevelLocked</c>
    /// without a tag: found by the <see cref="ModStateWatcher"/>). The mod replaces vanilla zone checking
    /// (<c>SelectiveZoneCheckSystem</c>, condemns buildings) and tags plopped buildings (and makes them historical) in
    /// ModificationEnd on every machine; on a client the host's results replicate instead, so both are off there (vanilla
    /// <c>ZoneCheckSystem</c> is already off for clients). The load-time conversion of existing buildings must not run on
    /// a joiner (the host's city arrives converted). Lock and despawn settings are the host's.
    /// </summary>
    public sealed class PlopTheGrowablesAdapter : ModAdapter
    {
        public override string ModName => "Plop the Growables";

        public override string AssemblyName => "PlopTheGrowables";

        public override string SettingsTypeName => "PlopTheGrowables.ModSettings";

        public override IEnumerable<string> ClientOffSystems => new[]
        {
            "PlopTheGrowables.PloppedBuildingSystem",
            "PlopTheGrowables.SelectiveZoneCheckSystem",
        };

        public override IEnumerable<string> JoinOffSystems => new[] { "PlopTheGrowables.ExistingBuildingSystem" };

        public override IEnumerable<string> SyncedSettings => new[] { "SpawnedZoneDespawn", "LockPloppedBuildings" };

        /// <summary>Plopping places growable buildings, whose prefabs the host's progression may still list as locked.</summary>
        public override Core.Sync.PlacementChecks RelaxedPlacementChecks => Core.Sync.PlacementChecks.Unlocks;

        public override string Summary => "tags and level locks replicate; zone check and plop tagging host-only; settings from the host";
    }
}
