using System.Collections.Generic;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// Water Features (Yenyang, Paradox Mods 75613): its water tool creates water sources without a <c>PrefabRef</c>
    /// (water-source family, replicated with their mod components) and edits <c>WaterSourceData</c> in place without a
    /// tag, so <c>WaterSourceData</c> is watched (<see cref="ModStateWatcher"/>). Its lakes, basins, streams, tides and
    /// waves read the locally simulated water and run in the simulation phase: host only, the host's source values
    /// replicate. <c>ChangeWaterSystemValues</c> only copies the (host's) evaporation, fluidness and damping settings
    /// into the local water simulation and stays on for clients. The per-city settings travel in the join snapshot (the
    /// mod saves them with the city) and live changes through the settings channel.
    /// </summary>
    public sealed class WaterFeaturesAdapter : ModAdapter
    {
        public override string ModName => "Water Features";

        public override string AssemblyName => "Water_Features";

        public override string SettingsTypeName => "Water_Features.Settings.WaterFeaturesSettings";

        public override IEnumerable<string> WatchedVanillaTypes => new[] { "Game.Simulation.WaterSourceData" };

        public override IEnumerable<string> ClientSimulationSystems => new[] { "Water_Features.Systems.ChangeWaterSystemValues" };

        public override IEnumerable<string> SyncedSettings => new[]
        {
            "EvaporationRate", "Fluidness", "Damping", "ForceWaterSimulationSpeed", "WaterCausesDamage",
            "EnableSeasonalStreams", "SimulateSnowMelt", "ConstantFlowRate", "StreamSeasonality", "StreamStormwaterEffects",
            "MinimumMultiplier", "MaximumMultiplier",
            "EnableWavesAndTides", "WaveHeight", "WaveFrequency", "TideHeight", "TideClassification",
        };

        public override string Summary => "sources replicate (incl. in-place edits); simulation host-only; water settings from the host";
    }
}
