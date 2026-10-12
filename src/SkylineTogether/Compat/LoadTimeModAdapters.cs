using System.Collections.Generic;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// 529 Tiles (algernon, Paradox Mods 74328): after every load, <c>FiveTwentyNineSystem.PostDeserialize</c> unlocks or
    /// relocks map tiles and rewrites milestone tile counts according to the local settings. A joiner must not do that
    /// with its own settings: the host's city arrives with the host's tiles (the <c>Native</c> state of map tiles is save
    /// data and replicates), so the post-deserialize step is off on clients while they join and play. Tile costs and the
    /// upkeep multiplier are computed by the host's simulation (clients do not simulate), so they need no sync.
    /// </summary>
    public sealed class FiveTwentyNineTilesAdapter : ModAdapter
    {
        public override string ModName => "529 Tiles";

        public override string AssemblyName => "FiveTwentyNineTiles";

        public override IEnumerable<string> JoinOffSystems => new[] { "PostDeserialize:FiveTwentyNineTiles.FiveTwentyNineSystem" };

        public override string Summary => "map tiles replicate; load-time unlocks host-only";
    }

    /// <summary>
    /// Historical Start (algernon, Paradox Mods 74535): unlocks transport, industry and highway prefabs at every load
    /// from the local settings. Prefab unlocks are the host's (they travel in the join snapshot and through the
    /// gameplay branch's unlock channel), so the system is off on a client while it joins and plays.
    /// </summary>
    public sealed class HistoricalStartAdapter : ModAdapter
    {
        public override string ModName => "Historical Start";

        public override string AssemblyName => "HistoricalStart";

        public override IEnumerable<string> JoinOffSystems => new[] { "HistoricalStart.HistoricalStartSystem" };

        public override string Summary => "load-time unlocks host-only";
    }
}
