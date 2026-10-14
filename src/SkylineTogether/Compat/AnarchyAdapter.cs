using System.Collections.Generic;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// Anarchy (Yenyang, Paradox Mods 74604): its serializable components (<c>PreventOverride</c>,
    /// <c>TransformRecord</c>, <c>DoNotForceUpdate</c>) replicate generically in the same record as the entity's
    /// <c>Transform</c>, so <c>ResetTransformSystem</c> on every peer re-applies the same record; components it adds
    /// without tags (components tool, info-panel toggles) are found by the <see cref="ModStateWatcher"/>. Its post-apply
    /// processing (<c>AnarchyPlopSystem</c>) runs where the tool applied (the client's prediction is proposed with the
    /// result). The rules that decide what placements are valid and how networks are built come from the host:
    /// unique-building limit and clearance below elevated networks. The per-player Anarchy toggle stays per player
    /// (it is a tool mode, like an elevation step). The "network upgrade prefabs" setting adds prefabs at load and must
    /// already match (a missing prefab is reported, never applied).
    /// </summary>
    public sealed class AnarchyAdapter : ModAdapter
    {
        public override string ModName => "Anarchy";

        public override string AssemblyName => "Anarchy";

        public override string SettingsTypeName => "Anarchy.Settings.AnarchyModSettings";

        public override IEnumerable<string> SyncedSettings => new[]
        {
            "AllowPlacingMultipleUniqueBuildings",
            "MinimumClearanceBelowElevatedNetworks",
        };

        /// <summary>
        /// The per-player Anarchy toggle switches off the game's placement errors (map tiles, shapes) for that player's
        /// tools; the host cannot see the toggle, so with Anarchy loaded the host does not apply those checks either.
        /// </summary>
        public override Core.Sync.PlacementChecks RelaxedPlacementChecks => Core.Sync.PlacementChecks.Tiles | Core.Sync.PlacementChecks.Geometry;

        public override string Summary => "components replicate with the entity; validation and clearance settings from the host";
    }
}
