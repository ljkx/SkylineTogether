using System.Collections.Generic;
using System.Reflection;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// Prefab Asset Fixes (StarQ, Paradox Mods 113708): edits prefab data in place, from its settings, every time a game
    /// loads and live when a setting changes (<c>AssetFixSystem.Fix*</c>, called by the setting's setter): the prison van
    /// capacity, industrial workers per cell, storage limits, zone properties and building properties of Dutch low
    /// housing, upgrade lists, unlock requirements of the French city hall, the sub-objects, sub-nets and lane directions
    /// of a few buildings. Prefab data is not replicated, so every peer must hold the same values: the settings are the
    /// host's (<see cref="ModSettingsSync"/> writes them through the setters, which redo the edit; a client's own settings
    /// come back when the session ends). The mod keeps its settings object in a static field, which is why
    /// <see cref="SettingsInstance"/> names it. <c>AssetFixSystem.OnUpdate</c> (Deserialize phase, once per load) also
    /// removes the power line of two parking lots from the loaded city (<c>Deleted</c> on the building's road edge): on a
    /// joined player that system is off from the start of the join, the host's city arrives with the fix done.
    /// </summary>
    public sealed class PrefabAssetFixesAdapter : ModAdapter
    {
        public override string ModName => "Prefab Asset Fixes";

        public override string AssemblyName => "PrefabAssetFixes";

        public override string SettingsTypeName => "PrefabAssetFixes.Setting";

        public override IEnumerable<string> JoinOffSystems => new[] { "PrefabAssetFixes.Systems.AssetFixSystem" };

        // the toggles that change prefab data (SolarParking is a no-op in this version, the About and log entries are read-only)
        public override IEnumerable<string> SyncedSettings => new[]
        {
            "IndustrialCompanyWorker", "Prison", "Hospital", "USSWHospital", "HoveringPoles", "RSClinic", "LHTBusStation02",
            "LHTTaxiDepot01", "LHTTramDepot01", "LHTCargoHarbor01", "RHTBusStation01", "NLLowHousehold", "AdditionalTransformers",
            "FRCiltyHall",
        };

        public override IEnumerable<string> Members => new[] { "PrefabAssetFixes.Mod::m_Setting" };

        public override object SettingsInstance()
        {
            return FindMember("PrefabAssetFixes.Mod::m_Setting") is FieldInfo f ? f.GetValue(null) : null;
        }

        public override string Summary => "prefab edits identical on every peer: settings are the host's, load-time city fix off on clients";
    }

    /// <summary>
    /// Transport Policy Adjuster (Paradox Mods 78622, Harmony): replaces the game's <c>Policies.ModifiedSystem</c> and
    /// <c>RouteModifierInitializeSystem</c> update by its own copies (<c>ModifiedSystem</c> in Modification4,
    /// <c>RouteModifierInitializeSystem.OnUpdate</c> prefix) in which a route policy's slider value is no longer clamped to
    /// the slider, and replaces the vehicle count info section so the player can ask for more vehicles than the slider
    /// allows (the six per-vehicle-type maximums of its settings only size that section: UI, per player). The values it
    /// writes are the vanilla <c>Policy</c> buffers and <c>RouteModifier</c> buffers, which replicate; every peer derives
    /// the route modifiers with the mod's copy, so the mod is world-affecting (same version on all peers). The host
    /// refuses a joined player's route policy value outside the slider range, which would cap the mod's larger vehicle
    /// counts: while the mod is on the host that range is widened by <see cref="RoutePolicyRangeSlack"/> spans.
    /// </summary>
    public sealed class TransportPolicyAdjusterAdapter : ModAdapter
    {
        public override string ModName => "Transport Policy Adjuster";

        public override string AssemblyName => "TransportPolicyAdjuster";

        // the two replaced game systems and the unclamped slider mapping this adapter's rule relies on
        public override IEnumerable<string> Members => new[]
        {
            "TransportPolicyAdjuster.ModifiedSystem::OnUpdate",
            "TransportPolicyAdjuster.DisableBaseModifiedSystem::OnUpdate",
            "TransportPolicyAdjuster.RouteModifierInitializeSystem::OnUpdate",
            "TransportPolicyAdjuster.RouteModifierInitializeSystem+RouteModifierRefreshData::GetModifierDelta",
        };

        public override float RoutePolicyRangeSlack => Core.Sync.GlobalProposalRules.RoutePolicyRangeSlack;

        public override string Summary => "world-affecting (replaces policy and route modifier systems); route policy values beyond the slider accepted";
    }

    /// <summary>
    /// Magic Hearse v Funeral Director (River-mochi, Paradox Mods 123497): <c>MagicHearseSystem</c>,
    /// <c>CemeteryResetSystem</c> and <c>HearseWarningDelaySystem</c> are GameSimulation systems (host only, switched off
    /// on a following client like every simulation system); their results (<c>Deleted</c> citizens, cemetery storage,
    /// health problem timers) replicate. <c>FuneralDirectorSystem</c> (PrefabUpdate, redone at every load and on a setting
    /// change) scales deathcare prefab data and the hearse's speed, and with "control workers" rewrites
    /// <c>WorkProvider.m_MaxWorkers</c> of placed deathcare buildings: it is host work, so it is off on a client from the
    /// start of the join (the host's city arrives with the result). No saved component (its three components are plain
    /// runtime markers), no patch, no settings the simulation of another peer reads: a joined player does not need the mod.
    /// </summary>
    public sealed class MagicHearseAdapter : ModAdapter
    {
        public override string ModName => "Magic Hearse";

        public override string AssemblyName => "MagicHearse";

        public override IEnumerable<string> JoinOffSystems => new[] { "MagicHearse.FuneralDirectorSystem" };

        public override string Summary => "host-side simulation; the rewrite of prefab and workplace data is off on clients";
    }

    /// <summary>
    /// Road Speed Adjuster (DanielVNZ, Paradox Mods 125866): its tool (a direct-write tool) puts the saved
    /// <c>CustomSpeed</c> on road edges and writes the lanes' speed limits; <c>RoadSpeedApplySystem</c> (ModificationEnd,
    /// every peer) derives the lane limits from <c>CustomSpeed</c> whenever an edge updates. The speed limits therefore
    /// travel with the city and the edge record on their own, and every peer derives the same lanes. What lives in the
    /// local JSON file (<c>ModsData/RoadSpeedAdjuster/&lt;city&gt;.json</c>) is only each road's original ("default") speed,
    /// keyed by the process-local entity index, for "reset to original" and "clear all": a joined player does not read or
    /// write that file in the host's city, because <c>RoadSpeedSaveDataSystem</c> (which opens it by city name, and saves
    /// it when it is destroyed) is off from the start of the join. Without the data, the mod's own fall-backs apply
    /// (clear all removes <c>CustomSpeed</c> without a stored default, the road's lanes then derive again from its
    /// prefab; a single reset does nothing).
    /// </summary>
    public sealed class RoadSpeedAdjusterAdapter : ModAdapter
    {
        public override string ModName => "Road Speed Adjuster";

        public override string AssemblyName => "RoadSpeedAdjuster";

        public override IEnumerable<string> JoinOffSystems => new[] { "RoadSpeedAdjuster.Systems.RoadSpeedSaveDataSystem" };

        public override string Summary => "CustomSpeed replicates with edges, lanes derived on every peer; local default-speed file untouched on clients";
    }

    /// <summary>
    /// Advanced Simulation Speed (fork by Bruceyboy24804, Paradox Mods 124304; the original by zakuro9715 is 79794): a UI
    /// system (UIUpdate) that replaces the time controls with buttons which write <c>SimulationSystem.selectedSpeed</c>
    /// (any value from 0 to 8: doubled, or by a fixed step) and shows <c>smoothSpeed</c>. It patches nothing and saves
    /// nothing (its settings are mode, step and display options; the speed is not stored, the game starts every load at 1
    /// or paused). Skyline Together carries the exact speed (<c>SpeedState.Exact</c>): the host's speed is everyone's, the
    /// follower clock of a joined player mirrors it, and the player's own button presses become speed requests that the
    /// host runs exactly while it has this mod (<see cref="AllowsFineGameSpeed"/>), else rounds to 1, 2 or 4.
    /// </summary>
    public sealed class AdvancedSimulationSpeedAdapter : ModAdapter
    {
        public override string ModName => "Advanced Simulation Speed";

        public override string AssemblyName => "AdvancedSimulationSpeed";

        // the fork's UI system (the original's is named UISystem: that version gets the generic treatment, nothing changes for it)
        public override IEnumerable<string> Members => new[] { "AdvancedSimulationSpeed.Systems.AdvancedSimSpeedUISystem::OnCreate" };

        public override bool AllowsFineGameSpeed => true;

        public override string Summary => "client-local UI; exact speeds travel with the speed sync, the host runs a player's exact request";
    }

    /// <summary>
    /// Simple Mod Checker Plus (StarQ, Paradox Mods 79186): checks the player's mods and settings at the start of the game
    /// (a scan of the user folder's <c>.coc</c> files and a notification that deletes the corrupt ones and quits the game
    /// only when the player clicks it; a migration of its own data folder that quits once while the mod loads; backups of
    /// game and mod settings; deleting <c>continue_game.json</c> and, with its option, the corrupt files when the game shuts down). Nothing
    /// of that runs by itself in a session. Its only system in the game loop, <c>AutosaveOffCheck</c> (MainLoop), posts a
    /// joke chirp through the Custom Chirps mod when autosave is off: a joined player's would be a chirp the host does not
    /// have, so it is off on a client from the start of the join. The mod is never part of a join decision.
    /// </summary>
    public sealed class SimpleModCheckerPlusAdapter : ModAdapter
    {
        public override string ModName => "Simple Mod Checker Plus";

        public override string AssemblyName => "SimpleModCheckerPlus";

        public override IEnumerable<string> JoinOffSystems => new[] { "SimpleModCheckerPlus.Systems.AutosaveOffCheck" };

        public override string Summary => "client-local; its autosave chirp system is off on clients";
    }
}
