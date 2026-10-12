using System.Collections.Generic;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// Better Bulldozer (Yenyang, Paradox Mods 75250): the sub-element tool deletes props, fences, sub-nets and
    /// extensions directly and records the removal: a <c>PermanentlyRemovedSubElementPrefab</c> buffer on the owner whose
    /// elements reference <b>record entities</b> (<c>OwnerRecord</c> + <c>PrefabRef</c> of the removed prefab). The owner's
    /// buffer is added without an update tag, so the <see cref="ModStateWatcher"/> finds it; the record entities classify
    /// as mod entities and travel with it (dependency binding). Every peer then removes regenerated sub-elements itself
    /// (<c>RemoveRegeneratedSubelementPrefabsSystem</c> on the owner's update), exactly like after a load. Vehicles, cims
    /// and animals deleted by its tool become despawn proposals of those movers. The automatic removal settings act on
    /// every peer, so they are the host's.
    /// </summary>
    public sealed class BetterBulldozerAdapter : ModAdapter
    {
        public override string ModName => "Better Bulldozer";

        public override string AssemblyName => "BetterBulldozer";

        public override string SettingsTypeName => "Better_Bulldozer.Settings.BetterBulldozerModSettings";

        public override IEnumerable<string> SyncedSettings => new[]
        {
            "AllowRemovingSubElementNetworks",
            "AllowRemovingExtensions",
            "AutomaticRemovalManicuredGrass",
            "AutomaticRemovalFencesAndHedges",
            "AutomaticRemovalBrandingObjects",
        };

        public override string Summary => "removal records replicate with their owner; automatic removals use the host's settings";
    }
}
