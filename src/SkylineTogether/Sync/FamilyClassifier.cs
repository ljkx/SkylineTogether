using System;
using System.Collections.Generic;
using SkylineTogether.Core;
using SkylineTogether.Core.Identity;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// Builds the family marker mask of an entity's archetype and applies Core's rule table (<see cref="FamilyRules"/>,
    /// ARCHITECTURE.md#families). Masks are cached per archetype; only the sub-object rule needs a per-entity look at the owner.
    /// </summary>
    public sealed class FamilyClassifier
    {
        private static readonly (string Type, FamilyMarker Marker)[] MarkerTypes =
        {
            ("Game.Tools.Temp", FamilyMarker.Temp),
            ("Game.Routes.LivePath", FamilyMarker.LivePath),
            ("Game.Prefabs.PrefabData", FamilyMarker.PrefabData),
            ("Game.Net.Lane", FamilyMarker.Lane),
            ("Game.Zones.Block", FamilyMarker.ZoneBlock),
            ("Game.Notifications.Icon", FamilyMarker.Icon),
            ("Game.Objects.Object", FamilyMarker.Object),
            ("Game.Objects.Secondary", FamilyMarker.Secondary),
            ("Game.Common.Owner", FamilyMarker.Owner),
            ("Game.Buildings.ServiceUpgrade", FamilyMarker.ServiceUpgrade),
            ("Game.Buildings.Building", FamilyMarker.Building),
            ("Game.Simulation.ElectricityFlowNode", FamilyMarker.FlowGraph),
            ("Game.Simulation.ElectricityFlowEdge", FamilyMarker.FlowGraph),
            ("Game.Simulation.WaterPipeNode", FamilyMarker.FlowGraph),
            ("Game.Simulation.WaterPipeEdge", FamilyMarker.FlowGraph),
            // ElectricityNodeConnection/WaterPipeNodeConnection are NOT markers: the graph systems add them to the
            // net nodes and edges themselves (ElectricityEdgeGraphSystem.GetOrCreateNetNodeConnection), so as markers they
            // made every road, power line and pipe a derived FlowGraph entity (rule 8 precedes the net rules). The
            // connection components are excluded from replication instead (TypeHandlers.Excluded).
            ("Game.Routes.Route", FamilyMarker.Route),
            ("Game.Net.Node", FamilyMarker.NetNode),
            ("Game.Net.Edge", FamilyMarker.NetEdge),
            ("Game.Net.Aggregate", FamilyMarker.Aggregate),
            ("Game.Vehicles.Vehicle", FamilyMarker.Vehicle),
            ("Game.Creatures.Creature", FamilyMarker.Creature),
            ("Game.Areas.Area", FamilyMarker.Area),
            ("Game.Routes.Waypoint", FamilyMarker.Waypoint),
            ("Game.Routes.Segment", FamilyMarker.Segment),
            ("Game.Citizens.Citizen", FamilyMarker.Citizen),
            ("Game.Citizens.Household", FamilyMarker.Household),
            ("Game.Citizens.HouseholdPet", FamilyMarker.HouseholdPet),
            ("Game.Companies.CompanyData", FamilyMarker.CompanyData),
            ("Game.Simulation.ServiceRequest", FamilyMarker.TransientAgent),
            ("Game.Agents.JobSeeker", FamilyMarker.TransientAgent),
            ("Game.Citizens.SchoolSeeker", FamilyMarker.TransientAgent),
            ("Game.Citizens.LookingForPartner", FamilyMarker.TransientAgent),
            ("Game.Citizens.CoordinatedMeeting", FamilyMarker.TransientAgent),
            ("Game.Events.Event", FamilyMarker.EventEntity),
            ("Game.Triggers.Chirp", FamilyMarker.JournalOrChirpOrLifePath),
            ("Game.Triggers.LifePathEvent", FamilyMarker.JournalOrChirpOrLifePath),
            ("Game.Events.EventJournalEntry", FamilyMarker.JournalOrChirpOrLifePath),
            ("Game.City.CityStatistic", FamilyMarker.CityStatistic),
            ("Game.City.City", FamilyMarker.Singleton),
            ("Game.Simulation.ServiceBudgetData", FamilyMarker.Singleton),
            ("Game.Common.TimeData", FamilyMarker.Singleton),
            ("Game.Simulation.WaterSourceData", FamilyMarker.WaterSource),
        };

        private readonly Dictionary<int, FamilyMarker> _markerByType = new Dictionary<int, FamilyMarker>();
        private readonly Dictionary<EntityArchetype, ArchetypeClass> _classes = new Dictionary<EntityArchetype, ArchetypeClass>();
        private readonly ComponentType _prefabRef;

        /// <summary>The marker component names (probed at startup, <see cref="Diagnostics.Probes"/>).</summary>
        public static IEnumerable<string> MarkerTypeNames
        {
            get
            {
                foreach ((string type, FamilyMarker _) in MarkerTypes) yield return type;
            }
        }

        public FamilyClassifier()
        {
            foreach (var (name, marker) in MarkerTypes)
            {
                Type t = FindType(name);
                if (t == null)
                {
                    Mod.Log?.Debug(LogCategory.Replication, "family marker type not found: " + name);
                    continue;
                }
                TypeIndex index = TypeManager.GetTypeIndex(t);
                if (index == TypeIndex.Null) continue;
                _markerByType[index.Value] = _markerByType.TryGetValue(index.Value, out FamilyMarker m) ? m | marker : marker;
            }
            _prefabRef = ComponentType.ReadOnly<Game.Prefabs.PrefabRef>();
        }

        private static readonly HashSet<string> Missing = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// A game type by its full name, or null. Every name the mod looks up goes through here (or through a list that
        /// <see cref="Diagnostics.Probes"/> checks): a missing one is logged once and listed in the probe report
        /// (<see cref="MissingTypeNames"/>), so a type renamed by a game update never disappears silently.
        /// </summary>
        public static Type FindType(string fullName)
        {
            Type t;
            try
            {
                t = typeof(Game.Common.Owner).Assembly.GetType(fullName, false);
            }
            catch (Exception)
            {
                t = null;
            }
            if (t == null && fullName != null)
            {
                bool first;
                lock (Missing) first = Missing.Add(fullName);
                if (first) Mod.Log?.Warn(LogCategory.Probe, "game type not found by name: " + fullName + " (renamed by a game update?); the feature that uses it runs without it");
            }
            return t;
        }

        /// <summary>The names <see cref="FindType"/> did not find so far.</summary>
        public static List<string> MissingTypeNames()
        {
            lock (Missing) return new List<string>(Missing);
        }

        /// <summary>Classifies an existing entity.</summary>
        public Family Classify(EntityManager em, Entity e)
        {
            EntityArchetype archetype = em.GetChunk(e).Archetype;
            ArchetypeClass c = ClassOf(archetype);
            if (!c.NeedsFacts) return c.Family;
            return FamilyRules.Classify(c.Markers, Facts(em, e, c.Markers), out _);
        }

        /// <summary>
        /// The family of every entity of an archetype when it does not depend on per-entity facts (one
        /// cached lookup per chunk); false when the entities must be classified one by one (<see cref="Classify"/>).
        /// </summary>
        public bool TryClassifyArchetype(EntityArchetype archetype, out Family family)
        {
            ArchetypeClass c = ClassOf(archetype);
            family = c.Family;
            return !c.NeedsFacts;
        }

        private struct ArchetypeClass
        {
            public FamilyMarker Markers;
            public bool NeedsFacts;
            public Family Family;
        }

        private ArchetypeClass ClassOf(EntityArchetype archetype)
        {
            if (_classes.TryGetValue(archetype, out ArchetypeClass c)) return c;
            FamilyMarker markers = Markers(archetype);
            if ((markers & FamilyMarker.Owner) != 0 && (markers & FamilyMarker.Route) != 0) markers |= FamilyMarker.GateBypassRoutePrefab;
            c = new ArchetypeClass { Markers = markers, NeedsFacts = FamilyRules.NeedsPerEntityFacts(markers) };
            if (!c.NeedsFacts) c.Family = FamilyRules.Classify(markers, default, out _);
            _classes[archetype] = c;
            return c;
        }

        private FamilyMarker Markers(EntityArchetype archetype)
        {
            FamilyMarker markers = FamilyMarker.None;
            bool hasPrefabRef = false;
            bool hasModSerializable = false;
            NativeArray<ComponentType> types = archetype.GetComponentTypes(Allocator.Temp);
            try
            {
                foreach (ComponentType t in types)
                {
                    if (_markerByType.TryGetValue(t.TypeIndex.Value, out FamilyMarker m)) markers |= m;
                    // mod compatibility: a type an adapter lists as tool-local (Traffic's connectors and lane handles,
                    // Move It's control points) makes the whole entity tool-local
                    if (Compat.ModCompat.IsLocalType(t.TypeIndex)) markers |= FamilyMarker.AdapterLocal;
                    if (t.TypeIndex == _prefabRef.TypeIndex) hasPrefabRef = true;
                    if (!hasModSerializable && TypeHandlers.Get(t) != null)
                    {
                        Type type = TypeManager.GetType(t.TypeIndex);
                        if (type != null && type.Assembly != typeof(Game.Common.Owner).Assembly) hasModSerializable = true;
                    }
                }
            }
            finally
            {
                types.Dispose();
            }
            // mod compatibility: an entity that only a mod's serializable data defines is a mod entity, with or without a
            // PrefabRef (Traffic's lane connection data carries PrefabRef(Traffic.FakePrefab), Better Bulldozer's removal
            // records the removed prefab); without this rule they fell to Generic and never replicated
            if (hasModSerializable && (markers & ~FamilyMarker.Owner) == 0) markers |= FamilyMarker.OnlyModSerializable;
            _ = hasPrefabRef;
            return markers;
        }

        private FamilyFacts Facts(EntityManager em, Entity e, FamilyMarker markers)
        {
            bool inSubObjects = false;
            bool ownerRouteTracked = false;
            if (em.HasComponent<Game.Common.Owner>(e))
            {
                Entity owner = em.GetComponentData<Game.Common.Owner>(e).m_Owner;
                if (owner != Entity.Null && em.Exists(owner))
                {
                    if ((markers & FamilyMarker.Object) != 0 && em.HasBuffer<Game.Objects.SubObject>(owner))
                    {
                        DynamicBuffer<Game.Objects.SubObject> subs = em.GetBuffer<Game.Objects.SubObject>(owner, true);
                        for (int i = 0; i < subs.Length; i++)
                        {
                            if (subs[i].m_SubObject == e)
                            {
                                inSubObjects = true;
                                break;
                            }
                        }
                    }
                    if ((markers & (FamilyMarker.Waypoint | FamilyMarker.Segment)) != 0)
                    {
                        ownerRouteTracked = em.HasComponent<Game.Routes.Route>(owner) && !em.HasComponent<Game.Common.Owner>(owner);
                    }
                }
            }
            return new FamilyFacts(inSubObjects, ownerRouteTracked);
        }

        /// <summary>Families whose entities are replicated as first-class entities in this version.</summary>
        public static bool IsReplicated(Family family)
        {
            switch (family)
            {
                case Family.NetNode:
                case Family.NetEdge:
                case Family.Aggregate:
                case Family.Object:
                case Family.Area:
                case Family.Route:
                case Family.Waypoint:
                case Family.Segment:
                case Family.WaterSource:
                case Family.ModEntity:
                    return true;
                // events and disasters, chirps, journal entries (host-only family, see IsHostOnly)
                case Family.Event:
                    return Diagnostics.Features.Events.IsAvailable;
                default:
                    return false;
            }
        }

        /// <summary>
        /// True while the host replicates agents (citizens, households, pets, companies) in this session; set by
        /// <see cref="AgentSync"/> at host session start when <c>Features.Agents</c> is available, false otherwise and on
        /// clients (a client applies whatever the host sends, it never decides).
        /// </summary>
        public static bool AgentsReplicated { get; set; }

        /// <summary>The agent family (ARCHITECTURE.md#families).</summary>
        public static bool IsAgent(Family family) => family == Family.Agent;

        /// <summary>
        /// Families the host binds, captures and streams: the replicated structure families plus agents while
        /// <see cref="AgentsReplicated"/>. Agents are host-owned: <see cref="IsReplicated"/> stays false for them, so a
        /// client can never propose, edit or delete one (proposal validation, client capture, rollbacks).
        /// </summary>
        public static bool IsHostReplicated(Family family) => IsReplicated(family) || (family == Family.Agent && AgentsReplicated);

        /// <summary>Families whose values the host scans periodically (they change without update tags).</summary>
        public static bool IsScanned(Family family) => family == Family.Route || family == Family.Waypoint || family == Family.Segment || family == Family.Singleton || family == Family.Event;

        /// <summary>
        /// Replicated families only the host's simulation creates, changes and deletes (events, disasters,
        /// chirps, journal entries). Clients never propose them, the host refuses proposals that touch them, and the
        /// integrity check leaves them out (their values change every simulation tick).
        /// </summary>
        public static bool IsHostOnly(Family family) => family == Family.Event;

        /// <summary>Families whose structure entities the presence watch covers (components the simulation
        /// adds and removes without an <c>Updated</c> tag).</summary>
        public static bool IsPresenceWatched(Family family) => family == Family.Object || family == Family.NetEdge || family == Family.NetNode || family == Family.Area;
    }
}
