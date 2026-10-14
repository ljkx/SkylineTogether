namespace SkylineTogether.Core.Identity
{
    /// <summary>
    /// The family rule table of ARCHITECTURE.md#families, implemented once and pure: the game layer only builds the
    /// <see cref="FamilyMarker"/> mask (once per archetype) and, where <see cref="NeedsPerEntityFacts"/> says so, the
    /// <see cref="FamilyFacts"/> of an entity. First match wins; rule numbers are those of docs/history/ARCHITECTURE-original.md §3.2:
    /// <list type="number">
    /// <item>Temp, LivePath, null prefab or adapter-local type → ToolLocal</item>
    /// <item>PrefabData → Prefab</item>
    /// <item>Lane → Lane</item>
    /// <item>Zone block → ZoneBlock</item>
    /// <item>Icon → Icon</item>
    /// <item>Object + Secondary → SecondaryObject</item>
    /// <item>Object + Owner, in the owner's SubObject buffer, none of ServiceUpgrade/Building/Secondary → PrefabSubObject</item>
    /// <item>flow-graph node, edge or connection → FlowGraph</item>
    /// <item>Route + Owner whose prefab is a gate bypass path → OwnedRoute</item>
    /// <item>Net node / net edge → NetNode / NetEdge</item>
    /// <item>Aggregate → Aggregate</item>
    /// <item>Vehicle or Creature → Mover</item>
    /// <item>any other Object → Object</item>
    /// <item>Area → Area</item>
    /// <item>Route → Route; Waypoint/Segment whose owner route is tracked → Waypoint/Segment</item>
    /// <item>Citizen, Household, HouseholdPet, CompanyData → Agent</item>
    /// <item>service requests, seekers, meetings → TransientAgent</item>
    /// <item>persistent event, journal entry, chirp, life-path event → Event</item>
    /// <item>CityStatistic → Statistic</item>
    /// <item>City, ServiceBudgetData, TimeData, MeshColorPalette → Singleton</item>
    /// <item>WaterSourceData → WaterSource</item>
    /// <item>only a non-Game serializable type → ModEntity</item>
    /// <item>anything else in the save query → Generic</item>
    /// </list>
    /// Two refinements keep the table exact and cheap: rule 7 never applies to vehicles and creatures, because
    /// <c>SubObjectReferencesSystem</c> never puts them into a <c>SubObject</c> buffer (its query excludes
    /// <c>Vehicle</c>, <c>Creature</c> and <c>Secondary</c>, <c>Game.Objects/SubObjectReferencesSystem.cs</c>), so owned
    /// vehicles need no per-entity lookup; and a waypoint or segment whose owner route is not tracked belongs to a
    /// derived owned route (a gate bypass path, whose route and two segments every peer creates in
    /// <c>Game.Buildings/InitializeSystem.cs:154-168</c>), so rule 15 classifies it as <see cref="Family.OwnedRoute"/>
    /// (derived, addressed by owned-route element keys, ARCHITECTURE.md#derived-keys) instead of letting it fall through to Generic, which
    /// would replicate a duplicate of what clients derive. An element whose own prefab is a gate bypass path is decided
    /// without the per-entity fact.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class FamilyRules
    {
        /// <summary>Number of rules (ARCHITECTURE.md#families).</summary>
        public const int RuleCount = 23;

        private static readonly string[] RuleTexts =
        {
            "Temp, LivePath, null prefab or adapter-local type -> ToolLocal",
            "PrefabData -> Prefab",
            "Lane -> Lane",
            "Zone block -> ZoneBlock",
            "Icon -> Icon",
            "Object + Secondary -> SecondaryObject",
            "Object + Owner in the owner's SubObject buffer -> PrefabSubObject",
            "flow graph -> FlowGraph",
            "gate bypass route -> OwnedRoute",
            "net node / net edge -> NetNode / NetEdge",
            "Aggregate -> Aggregate",
            "Vehicle or Creature -> Mover",
            "other Object -> Object",
            "Area -> Area",
            "Route / waypoint / segment -> Route / Waypoint / Segment (OwnedRoute when the owner route is not tracked)",
            "Citizen, Household, HouseholdPet, CompanyData -> Agent",
            "service request, seeker, meeting -> TransientAgent",
            "event, journal entry, chirp, life path -> Event",
            "CityStatistic -> Statistic",
            "City, budget, TimeData, palette -> Singleton",
            "WaterSourceData -> WaterSource",
            "only mod serializable types -> ModEntity",
            "anything else -> Generic"
        };

        /// <summary>Classifies an entity; <paramref name="ruleNumber"/> receives the deciding rule (1 ... 23).</summary>
        public static Family Classify(FamilyMarker markers, in FamilyFacts facts, out int ruleNumber)
        {
            return Evaluate(markers, facts, out ruleNumber, out _);
        }

        /// <summary>
        /// True when the family of an entity with these markers depends on <see cref="FamilyFacts"/> (rule 7 candidates
        /// and rule-15 waypoints and segments); otherwise one classification per archetype is exact.
        /// </summary>
        public static bool NeedsPerEntityFacts(FamilyMarker markers)
        {
            Evaluate(markers, default, out _, out bool consulted);
            return consulted;
        }

        /// <summary>
        /// The built-in default replication class of a family (ARCHITECTURE.md#families, #host-capture); <c>policies.json</c>
        /// <c>familyDefaults</c> may override it. Derived families are <see cref="ReplicationClass.Derived"/> (their keyed
        /// state is K per type, by policy); prefab state and icons travel on channels; Event is S (existence and target
        /// presence; chirps and journal entries get A per type by policy). Undefined values → Excluded (fail closed).
        /// </summary>
        public static ReplicationClass DefaultClass(Family family)
        {
            switch (family)
            {
                case Family.ToolLocal:
                    return ReplicationClass.Excluded;
                case Family.Prefab:
                case Family.Icon:
                    return ReplicationClass.Channel;
                case Family.Lane:
                case Family.ZoneBlock:
                case Family.SecondaryObject:
                case Family.PrefabSubObject:
                case Family.FlowGraph:
                case Family.OwnedRoute:
                    return ReplicationClass.Derived;
                case Family.NetNode:
                case Family.NetEdge:
                case Family.Aggregate:
                case Family.Object:
                case Family.Area:
                case Family.Route:
                case Family.Waypoint:
                case Family.Segment:
                case Family.Event:
                case Family.WaterSource:
                case Family.ModEntity:
                case Family.Generic:
                    return ReplicationClass.S;
                case Family.Mover:
                    return ReplicationClass.M;
                case Family.Agent:
                    return ReplicationClass.A;
                case Family.TransientAgent:
                    return ReplicationClass.ALow;
                case Family.Statistic:
                    return ReplicationClass.V;
                case Family.Singleton:
                    return ReplicationClass.G;
                default:
                    return ReplicationClass.Excluded;
            }
        }

        /// <summary>True for families that every peer derives locally and that never get slots (ARCHITECTURE.md#derived-keys).</summary>
        public static bool IsDerived(Family family) => DefaultClass(family) == ReplicationClass.Derived;

        /// <summary>A short text of a rule for the per-rule counts of the diagnostics dump.</summary>
        public static string DescribeRule(int ruleNumber)
        {
            return ruleNumber >= 1 && ruleNumber <= RuleCount ? RuleTexts[ruleNumber - 1] : "unknown rule";
        }

        private static bool Has(FamilyMarker markers, FamilyMarker all) => (markers & all) == all;

        private static bool Any(FamilyMarker markers, FamilyMarker any) => (markers & any) != 0;

        private static Family Evaluate(FamilyMarker m, in FamilyFacts facts, out int rule, out bool factConsulted)
        {
            factConsulted = false;
            if (Any(m, FamilyMarker.Temp | FamilyMarker.LivePath | FamilyMarker.NullPrefab | FamilyMarker.AdapterLocal))
            {
                rule = 1;
                return Family.ToolLocal;
            }
            if (Has(m, FamilyMarker.PrefabData))
            {
                rule = 2;
                return Family.Prefab;
            }
            if (Has(m, FamilyMarker.Lane))
            {
                rule = 3;
                return Family.Lane;
            }
            if (Has(m, FamilyMarker.ZoneBlock))
            {
                rule = 4;
                return Family.ZoneBlock;
            }
            if (Has(m, FamilyMarker.Icon))
            {
                rule = 5;
                return Family.Icon;
            }
            if (Has(m, FamilyMarker.Object | FamilyMarker.Secondary))
            {
                rule = 6;
                return Family.SecondaryObject;
            }
            if (Has(m, FamilyMarker.Object | FamilyMarker.Owner)
                && !Any(m, FamilyMarker.ServiceUpgrade | FamilyMarker.Building | FamilyMarker.Secondary | FamilyMarker.Vehicle | FamilyMarker.Creature))
            {
                factConsulted = true;
                if (facts.InOwnerSubObjectBuffer)
                {
                    rule = 7;
                    return Family.PrefabSubObject;
                }
            }
            if (Has(m, FamilyMarker.FlowGraph))
            {
                rule = 8;
                return Family.FlowGraph;
            }
            if (Has(m, FamilyMarker.Route | FamilyMarker.Owner | FamilyMarker.GateBypassRoutePrefab))
            {
                rule = 9;
                return Family.OwnedRoute;
            }
            if (Has(m, FamilyMarker.NetNode))
            {
                rule = 10;
                return Family.NetNode;
            }
            if (Has(m, FamilyMarker.NetEdge))
            {
                rule = 10;
                return Family.NetEdge;
            }
            if (Has(m, FamilyMarker.Aggregate))
            {
                rule = 11;
                return Family.Aggregate;
            }
            if (Any(m, FamilyMarker.Vehicle | FamilyMarker.Creature))
            {
                rule = 12;
                return Family.Mover;
            }
            if (Has(m, FamilyMarker.Object))
            {
                rule = 13;
                return Family.Object;
            }
            if (Has(m, FamilyMarker.Area))
            {
                rule = 14;
                return Family.Area;
            }
            if (Has(m, FamilyMarker.Route))
            {
                rule = 15;
                return Family.Route;
            }
            if (Any(m, FamilyMarker.Waypoint | FamilyMarker.Segment))
            {
                rule = 15;
                bool ownerTracked;
                if (Has(m, FamilyMarker.GateBypassRoutePrefab))
                {
                    ownerTracked = false;
                }
                else
                {
                    factConsulted = true;
                    ownerTracked = facts.OwnerRouteTracked;
                }
                if (!ownerTracked) return Family.OwnedRoute;
                return Has(m, FamilyMarker.Waypoint) ? Family.Waypoint : Family.Segment;
            }
            if (Any(m, FamilyMarker.Citizen | FamilyMarker.Household | FamilyMarker.HouseholdPet | FamilyMarker.CompanyData))
            {
                rule = 16;
                return Family.Agent;
            }
            if (Has(m, FamilyMarker.TransientAgent))
            {
                rule = 17;
                return Family.TransientAgent;
            }
            if (Any(m, FamilyMarker.EventEntity | FamilyMarker.JournalOrChirpOrLifePath))
            {
                rule = 18;
                return Family.Event;
            }
            if (Has(m, FamilyMarker.CityStatistic))
            {
                rule = 19;
                return Family.Statistic;
            }
            if (Has(m, FamilyMarker.Singleton))
            {
                rule = 20;
                return Family.Singleton;
            }
            if (Has(m, FamilyMarker.WaterSource))
            {
                rule = 21;
                return Family.WaterSource;
            }
            if (Has(m, FamilyMarker.OnlyModSerializable))
            {
                rule = 22;
                return Family.ModEntity;
            }
            rule = 23;
            return Family.Generic;
        }
    }
}
