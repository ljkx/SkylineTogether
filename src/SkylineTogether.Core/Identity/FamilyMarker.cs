using System;

namespace SkylineTogether.Core.Identity
{
    /// <summary>
    /// Presence of the marker types the family rule table tests (ARCHITECTURE.md#families). game-ecs-schema's <c>FamilyClassifier</c>
    /// builds the mask once per archetype (plus the per-entity <see cref="NullPrefab"/> test for Move It control
    /// points); <see cref="FamilyRules"/> decides the family. The values are stable (diagnostics dumps print them).
    /// </summary>
    [Flags]
    public enum FamilyMarker : ulong
    {
        None = 0,
        /// <summary><c>Game.Tools.Temp</c> (tool preview).</summary>
        Temp = 1UL << 0,
        /// <summary><c>Game.Net.LivePath</c> (excluded from saves).</summary>
        LivePath = 1UL << 1,
        /// <summary><c>PrefabRef.m_Prefab == Entity.Null</c> (Move It control points, R09 §2.3).</summary>
        NullPrefab = 1UL << 2,
        /// <summary>A type an adapter lists as local (<c>localTypes</c>, e.g. <c>MIT_*</c>).</summary>
        AdapterLocal = 1UL << 3,
        /// <summary><c>Game.Prefabs.PrefabData</c> (a prefab entity).</summary>
        PrefabData = 1UL << 4,
        /// <summary><c>Game.Net.Lane</c>.</summary>
        Lane = 1UL << 5,
        /// <summary><c>Game.Zones.Block</c>.</summary>
        ZoneBlock = 1UL << 6,
        /// <summary><c>Game.Notifications.Icon</c>.</summary>
        Icon = 1UL << 7,
        /// <summary><c>Game.Objects.Object</c>.</summary>
        Object = 1UL << 8,
        /// <summary><c>Game.Objects.Secondary</c>.</summary>
        Secondary = 1UL << 9,
        /// <summary><c>Game.Common.Owner</c>.</summary>
        Owner = 1UL << 10,
        /// <summary><c>Game.Buildings.ServiceUpgrade</c>.</summary>
        ServiceUpgrade = 1UL << 11,
        /// <summary><c>Game.Buildings.Building</c>.</summary>
        Building = 1UL << 12,
        /// <summary><c>ElectricityFlowNode/Edge</c>, <c>WaterPipeNode/Edge</c> or their connection entities.</summary>
        FlowGraph = 1UL << 13,
        /// <summary><c>Game.Routes.Route</c>.</summary>
        Route = 1UL << 14,
        /// <summary>The entity's prefab is a gate bypass path (<c>GateData.m_BypassPathPrefab</c>,
        /// <c>Game.Buildings/InitializeSystem.cs:154-168</c>).</summary>
        GateBypassRoutePrefab = 1UL << 15,
        /// <summary><c>Game.Net.Node</c>.</summary>
        NetNode = 1UL << 16,
        /// <summary><c>Game.Net.Edge</c>.</summary>
        NetEdge = 1UL << 17,
        /// <summary><c>Game.Net.Aggregate</c>.</summary>
        Aggregate = 1UL << 18,
        /// <summary><c>Game.Vehicles.Vehicle</c>.</summary>
        Vehicle = 1UL << 19,
        /// <summary><c>Game.Creatures.Creature</c>.</summary>
        Creature = 1UL << 20,
        /// <summary><c>Game.Areas.Area</c>.</summary>
        Area = 1UL << 21,
        /// <summary><c>Game.Routes.Waypoint</c>.</summary>
        Waypoint = 1UL << 22,
        /// <summary><c>Game.Routes.Segment</c>.</summary>
        Segment = 1UL << 23,
        /// <summary><c>Game.Citizens.Citizen</c>.</summary>
        Citizen = 1UL << 24,
        /// <summary><c>Game.Citizens.Household</c>.</summary>
        Household = 1UL << 25,
        /// <summary><c>Game.Citizens.HouseholdPet</c>.</summary>
        HouseholdPet = 1UL << 26,
        /// <summary><c>Game.Companies.CompanyData</c>.</summary>
        CompanyData = 1UL << 27,
        /// <summary><c>ServiceRequest</c> (and typed requests), <c>JobSeeker</c>, <c>SchoolSeeker</c>, <c>LookingForPartner</c>,
        /// <c>CoordinatedMeeting</c>.</summary>
        TransientAgent = 1UL << 28,
        /// <summary><c>Game.Events.Event</c> (persistent event entities).</summary>
        EventEntity = 1UL << 29,
        /// <summary><c>EventJournalEntry</c>, <c>Chirp</c> or <c>LifePathEvent</c>.</summary>
        JournalOrChirpOrLifePath = 1UL << 30,
        /// <summary><c>Game.City.CityStatistic</c>.</summary>
        CityStatistic = 1UL << 31,
        /// <summary><c>City</c>, <c>ServiceBudgetData</c>, <c>TimeData</c> or <c>MeshColorPalette</c>.</summary>
        Singleton = 1UL << 32,
        /// <summary><c>Game.Simulation.WaterSourceData</c> (may lack <c>PrefabRef</c>).</summary>
        WaterSource = 1UL << 33,
        /// <summary>The entity is in the save query only through a non-Game serializable type (e.g. Better Bulldozer records).</summary>
        OnlyModSerializable = 1UL << 34
    }
}
