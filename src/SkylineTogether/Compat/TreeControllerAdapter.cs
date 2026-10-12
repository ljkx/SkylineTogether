using System.Collections.Generic;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// Tree Controller (Yenyang, Paradox Mods 75993): its brush changes tree state and age, swaps tree prefabs and
    /// toggles <c>Decoration</c> directly with update tags (captured like any direct-write tool); <c>DeciduousData</c>,
    /// <c>Evergreen</c> and the enableable <c>LumberResource</c> replicate with their enabled bits
    /// (<c>SyncRecordKind.EnabledBits</c>). The seasonal systems (<c>DeciduousSystem</c>, <c>FindTreesAndBushesSystem</c>)
    /// are simulation systems and run on the host only; the host's results replicate. The foliage colour system only
    /// changes prefab colour data and stays on for clients. Growth, winter model and free-vegetation settings are the
    /// host's (free vegetation changes what a placement costs).
    /// </summary>
    public sealed class TreeControllerAdapter : ModAdapter
    {
        public override string ModName => "Tree Controller";

        public override string AssemblyName => "Tree_Controller";

        public override string SettingsTypeName => "Tree_Controller.Settings.TreeControllerSettings";

        public override IEnumerable<string> SyncedSettings => new[]
        {
            "UseDeadModelDuringWinter",
            "DisableTreeGrowth",
            "FreeVegetation",
        };

        public override IEnumerable<string> ClientSimulationSystems => new[]
        {
            "Tree_Controller.Systems.ReloadFoliageColorDataSystem",
        };

        public override string Summary => "tree data and enabled bits replicate; seasonal systems host-only; growth settings from the host";
    }
}
