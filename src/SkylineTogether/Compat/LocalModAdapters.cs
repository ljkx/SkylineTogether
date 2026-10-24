using System.Collections.Generic;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// Achievement Fixer (Paradox Mods 121256): after every load in game mode its <c>AchievementFixerSystem</c> (MainLoop)
    /// switches <c>PlatformManager.achievementsEnabled</c> back on for 1800 frames, which the game turns off for a modded
    /// city. It writes no entity and nothing shared (client-local, Core <c>ModCompatibility</c>). A joiner's achievements
    /// are off in the host's city (<c>Session.SnapshotLoader</c>), so the system is off on a client from the start of the
    /// join and the session keeps the flag off (<c>SnapshotLoader.HoldAchievementsOff</c>: the mod also sets it once when
    /// the load completes, before any gate can act); the player's own value comes back at the session end. On the host it
    /// works as in single player.
    /// </summary>
    public sealed class AchievementFixerAdapter : ModAdapter
    {
        public override string ModName => "Achievement Fixer";

        public override string AssemblyName => "AchievementFixer";

        public override IEnumerable<string> JoinOffSystems => new[] { "AchievementFixer.AchievementFixerSystem" };

        public override string Summary => "client-local; off on clients (a joiner's achievements stay off in the host's city)";
    }

    /// <summary>
    /// Cim Census (Paradox Mods 85825): <c>StatisticsCalculationSystem</c> is a UI system registered in GameSimulation that
    /// counts citizens, workers, pets, waiting passengers and vehicles with read-only jobs for its panel, every 60 updates
    /// while the panel's auto refresh is on. It writes nothing, so it keeps running on a following client (the follower
    /// clock would switch it off with every simulation system) and counts the replicated city there.
    /// </summary>
    public sealed class CimCensusAdapter : ModAdapter
    {
        public override string ModName => "Cim Census";

        public override string AssemblyName => "CimCensus";

        public override IEnumerable<string> ClientSimulationSystems => new[] { "CimCensus.StatisticsCalculationSystem" };

        public override string Summary => "client-local; its read-only statistics system keeps running on clients";
    }

    /// <summary>
    /// Cim Route Highlighter (assembly EmploymentTracker, Paradox Mods 76662): highlights the selected building's
    /// employees, residents and destinations (<c>Highlighted</c> and <c>BatchesUpdated</c>, never replicated) and draws
    /// routes in the rendering phase; everything is the player's own view. Its saved <c>DeleteTimer</c> component is never
    /// put on an entity: it is excluded from replication, and it is city data only where a city has entities with it
    /// (Core <c>CityDataRule.DormantSavedTypes</c>).
    /// </summary>
    public sealed class EmploymentTrackerAdapter : ModAdapter
    {
        public override string ModName => "Cim Route Highlighter";

        public override string AssemblyName => "EmploymentTracker";

        public override IEnumerable<string> NeverReplicatedTypes => new[] { "EmploymentTracker.DeleteTimer" };

        public override string Summary => "client-local; highlights and routes are per player; DeleteTimer (unused) never sent";
    }
}
