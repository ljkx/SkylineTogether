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
    /// ARCH §3.2). Masks are cached per archetype; only the sub-object rule needs a per-entity look at the owner.
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
            ("Game.Simulation.ElectricityNodeConnection", FamilyMarker.FlowGraph),
            ("Game.Simulation.WaterPipeNodeConnection", FamilyMarker.FlowGraph),
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
        private readonly Dictionary<EntityArchetype, FamilyMarker> _cache = new Dictionary<EntityArchetype, FamilyMarker>();
        private readonly ComponentType _owner;
        private readonly ComponentType _prefabRef;
        private readonly ComponentType _subObject;

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
            _owner = ComponentType.ReadOnly<Game.Common.Owner>();
            _prefabRef = ComponentType.ReadOnly<Game.Prefabs.PrefabRef>();
            _subObject = ComponentType.ReadOnly<Game.Objects.SubObject>();
        }

        public static Type FindType(string fullName)
        {
            Type t = typeof(Game.Common.Owner).Assembly.GetType(fullName, false);
            return t;
        }

        /// <summary>Classifies an existing entity.</summary>
        public Family Classify(EntityManager em, Entity e)
        {
            EntityArchetype archetype = em.GetChunk(e).Archetype;
            if (!_cache.TryGetValue(archetype, out FamilyMarker markers))
            {
                markers = Markers(archetype);
                _cache[archetype] = markers;
            }
            if ((markers & FamilyMarker.Owner) != 0 && (markers & FamilyMarker.Route) != 0) markers |= FamilyMarker.GateBypassRoutePrefab;
            FamilyFacts facts = default;
            if (FamilyRules.NeedsPerEntityFacts(markers)) facts = Facts(em, e, markers);
            return FamilyRules.Classify(markers, facts, out _);
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
            if (!hasPrefabRef && hasModSerializable && (markers & ~FamilyMarker.Owner) == 0) markers |= FamilyMarker.OnlyModSerializable;
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
                default:
                    return false;
            }
        }

        /// <summary>Families whose values the host scans periodically (they change without update tags).</summary>
        public static bool IsScanned(Family family) => family == Family.Route || family == Family.Waypoint || family == Family.Segment || family == Family.Singleton;
    }
}
