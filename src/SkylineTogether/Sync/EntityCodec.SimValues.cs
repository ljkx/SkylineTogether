using System;
using System.Collections.Generic;
using SkylineTogether.Core;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Sync;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// w2/simvals: the simulation-value path of the codec (<see cref="AttentionValues"/>, ARCH D5, §4.7.9). A simulation
    /// value is a data component or buffer of a structure entity that the host's simulation changes without update tags
    /// (building condition, efficiency, consumers and producers, stored resources, service facility state, road traffic
    /// flow and condition, land value, coverage, area amounts, tree growth, ...). Structure and identity types (prefab,
    /// owner, transform, net geometry and topology, area nodes, the deferred <c>Building</c> fields, policies) never travel
    /// on this path: they are structure records' business and the integrity check verifies them. Types of other
    /// assemblies (mods) are structure as well. Movers (inspected vehicles and creatures) send only an allow-list, because
    /// <see cref="MoverClient"/> owns their pose, lane and flag state.
    /// <para>
    /// Values are written leniently: a data component whose references point only at service requests or notification
    /// icons (entities that never replicate; <c>CrimeProducer.m_PatrolRequest</c>, <c>WorkProvider</c> notification
    /// entities, ...) travels with those references nulled, so the values next to them are not lost. Every other
    /// unresolvable reference, and any in a buffer, still drops the component (presence-only rule of the codec).
    /// </para>
    /// </summary>
    public sealed partial class EntityCodec
    {
        /// <summary>Structure and identity types: never written by the simulation-value path.</summary>
        private static readonly HashSet<string> SimExcluded = new HashSet<string>(StringComparer.Ordinal)
        {
            "Game.Prefabs.PrefabRef",
            "Game.Common.Owner",
            "Game.Common.PseudoRandomSeed",
            "Game.Common.RandomLocalizationIndex",
            "Game.Objects.Transform",
            "Game.Objects.Attached",
            "Game.Objects.Attachment",
            "Game.Objects.Elevation",
            "Game.Objects.Relative",
            "Game.Net.Node",
            "Game.Net.Edge",
            "Game.Net.Curve",
            "Game.Net.Elevation",
            "Game.Net.Upgraded",
            "Game.Net.Fixed",
            "Game.Net.AggregateElement",
            "Game.Net.BuildOrder",
            "Game.Net.NetCustomColorVariation",
            "Game.Areas.Node",
            "Game.Areas.Area",
            "Game.Areas.District",
            // deferred road-connection fields (ARCH §4.2.5); written only by structure records
            "Game.Buildings.Building",
            // policies travel on the global-state channel, which detects client edits by comparison
            "Game.Policies.Policy",
            "Game.Objects.CustomMeshColor",
            // cosmetic or derived locally on every peer (ARCH §2.4, §4.2.5, §4.9.9, §6.4)
            "Game.Objects.Surface",
            "Game.Objects.StreetLight",
            "Game.Objects.TrafficLight",
            "Game.Areas.CurrentDistrict",
            "Game.Areas.BorderDistrict",
            "Game.Areas.DistrictModifier",
            // structure and authored placement data
            "Game.Objects.Aligned",
            "Game.Objects.NetObject",
            "Game.Objects.SpawnLocation",
            "Game.Objects.Stack",
            "Game.Objects.BlockedLane",
            "Game.Areas.ServiceDistrict",
            "Game.Net.ConnectedNode",
            "Game.Net.CutRange",
            "Game.Net.SubReplacement",
            "Game.Net.OutsideConnection",
            // mover state owned by the motion channel and MoverClient
            "Game.Objects.TransformFrame",
            "Game.Objects.Moving",
            "Game.Objects.TripSource",
            "Game.Routes.RouteWaypoint",
            "Game.Routes.RouteSegment",
            "Game.Simulation.WaterSourceData",
        };

        /// <summary>Namespaces whose types are never simulation values of structure entities.</summary>
        private static readonly string[] SimExcludedNamespaces =
        {
            "Game.Tools.", "Game.Rendering.", "Game.Pathfind.", "Game.Notifications.", "Game.Zones.", "Game.Prefabs.",
        };

        /// <summary>Simulation values of movers (vehicles, creatures): cargo, passengers and service state only.</summary>
        private static readonly HashSet<string> MoverAllowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Game.Vehicles.Passenger",
            "Game.Economy.Resources",
            "Game.Vehicles.Odometer",
            "Game.Vehicles.DeliveryTruck",
            "Game.Vehicles.CargoTransport",
            "Game.Vehicles.PublicTransport",
            "Game.Vehicles.GarbageTruck",
            "Game.Vehicles.PostVan",
            "Game.Vehicles.Ambulance",
            "Game.Vehicles.Hearse",
            "Game.Vehicles.FireEngine",
            "Game.Vehicles.PoliceCar",
            "Game.Vehicles.MaintenanceVehicle",
            "Game.Vehicles.WorkVehicle",
            "Game.Vehicles.Taxi",
        };

        private readonly Dictionary<ulong, byte> _simTypes = new Dictionary<ulong, byte>();
        private readonly Dictionary<EntityArchetype, TypeHandler[]> _simStructure = new Dictionary<EntityArchetype, TypeHandler[]>();
        private readonly Dictionary<EntityArchetype, TypeHandler[]> _simMover = new Dictionary<EntityArchetype, TypeHandler[]>();
        private readonly List<TypeHandler> _simScratch = new List<TypeHandler>();
        private readonly ByteWriter _simHashWriter = new ByteWriter(1024);
        private Func<Entity, bool> _simNullRefs;

        private const byte SimNo = 0, SimStructure = 1, SimMover = 2, SimBoth = 3;

        /// <summary>True when the type travels as a simulation value of a structure entity (or of a mover).</summary>
        public bool IsSimValueType(TypeHandler h, bool mover)
        {
            if (h == null || h.Kind == SyncComponentKind.Presence) return false;
            if (!_simTypes.TryGetValue(h.Hash, out byte kind))
            {
                kind = Classify(h);
                _simTypes[h.Hash] = kind;
            }
            return mover ? (kind & SimMover) != 0 : (kind & SimStructure) != 0;
        }

        private static byte Classify(TypeHandler h)
        {
            Type type = TypeManager.GetType(h.Index);
            string name = h.Name ?? "";
            if (type == null || type.Assembly != typeof(Game.Common.Owner).Assembly) return SimNo;
            byte kind = SimNo;
            if (MoverAllowed.Contains(name)) kind |= SimMover;
            if (SimExcluded.Contains(name)) return kind;
            foreach (string ns in SimExcludedNamespaces)
            {
                if (name.StartsWith(ns, StringComparison.Ordinal)) return kind;
            }
            return (byte)(kind | SimStructure);
        }

        /// <summary>The simulation-value handlers of the entity's archetype (cached per archetype).</summary>
        public TypeHandler[] SimHandlers(Entity e, bool mover)
        {
            EntityManager em = EntityManager;
            EntityArchetype archetype = em.GetChunk(e).Archetype;
            Dictionary<EntityArchetype, TypeHandler[]> cache = mover ? _simMover : _simStructure;
            if (cache.TryGetValue(archetype, out TypeHandler[] handlers)) return handlers;
            _simScratch.Clear();
            NativeArray<ComponentType> types = archetype.GetComponentTypes(Allocator.Temp);
            try
            {
                foreach (ComponentType t in types)
                {
                    TypeHandler h = TypeHandlers.Get(t);
                    if (IsSimValueType(h, mover)) _simScratch.Add(h);
                }
            }
            finally
            {
                types.Dispose();
            }
            // a fixed order (stable type hash), so the host's value hash does not depend on archetype order
            _simScratch.Sort((a, b) => a.Hash.CompareTo(b.Hash));
            handlers = _simScratch.ToArray();
            cache[archetype] = handlers;
            return handlers;
        }

        /// <summary>
        /// Appends the entity's simulation values to a <see cref="SimValueMessage"/>. Returns the number of components
        /// written; with 0 nothing was appended (the entity has no simulation value that can travel).
        /// </summary>
        public int WriteSimValues(ByteWriter w, uint netId, Family family, Entity e) => WriteSimValues(w, netId, family, e, null);

        /// <summary>
        /// <see cref="WriteSimValues(ByteWriter, uint, Family, Entity)"/> without the component of <paramref name="skip"/>
        /// (w3/audit: a node's traffic lights are hashed by phase, not by their tick timer).
        /// </summary>
        public int WriteSimValues(ByteWriter w, uint netId, Family family, Entity e, TypeHandler skip)
        {
            TypeHandler[] handlers = SimHandlers(e, family == Family.Mover);
            if (handlers.Length == 0) return 0;
            int start = w.Length;
            int countPosition = SimValueMessage.BeginEntity(w, netId, family);
            int n = 0;
            foreach (TypeHandler h in handlers)
            {
                if (n >= SimValueMessage.MaxComponents) break;
                if (h == skip) continue;
                if (!TrySerializeSim(h, e, out int length)) continue;
                SimValueMessage.AddComponent(w, h.Hash, h.Kind, _managedScratch, 0, length);
                n++;
            }
            if (n == 0)
            {
                SimValueMessage.DropEntity(w, start);
                return 0;
            }
            SimValueMessage.EndEntity(w, countPosition, n);
            return n;
        }

        /// <summary>A 64-bit hash of what <see cref="WriteSimValues"/> would send (0: nothing travels).</summary>
        public ulong HashSimValues(Entity e, Family family) => HashSimValues(e, family, null);

        /// <summary>The hash of <see cref="WriteSimValues(ByteWriter, uint, Family, Entity, TypeHandler)"/> without <paramref name="skip"/>.</summary>
        public ulong HashSimValues(Entity e, Family family, TypeHandler skip)
        {
            _simHashWriter.Reset();
            if (WriteSimValues(_simHashWriter, 0, family, e, skip) == 0) return 0;
            return Core.Hashing.XxHash64.Hash(_simHashWriter.Buffer, 0, _simHashWriter.Length) | 1UL;
        }

        private bool TrySerializeSim(TypeHandler h, Entity e, out int length)
        {
            if (_simNullRefs == null) _simNullRefs = IsNullableRef;
            _ws.NullRefs = h.Kind == SyncComponentKind.Data ? _simNullRefs : null;
            try
            {
                return TrySerialize(h, e, out length);
            }
            finally
            {
                _ws.NullRefs = null;
            }
        }

        /// <summary>References a simulation value may null: service requests, notification icons, gone entities.</summary>
        private bool IsNullableRef(Entity e)
        {
            EntityManager em = EntityManager;
            if (!em.Exists(e)) return true;
            if (em.HasComponent<Game.Simulation.ServiceRequest>(e) || em.HasComponent<Game.Notifications.Icon>(e)) return true;
            // w3/agents: transient agents (seekers, meetings) never replicate (ARCH 2.2, A-low: references tolerate staleness)
            return em.HasComponent<Game.Agents.JobSeeker>(e) || em.HasComponent<Game.Citizens.SchoolSeeker>(e)
                   || em.HasComponent<Game.Citizens.LookingForPartner>(e) || em.HasComponent<Game.Citizens.CoordinatedMeeting>(e);
        }

        /// <summary>
        /// w3/agents: appends the given components of an entity to a <see cref="SimValueMessage"/> (the reverse buffers of
        /// an owner after a membership change). Returns the number of components written (0: nothing was appended).
        /// </summary>
        public int WriteValueSubset(ByteWriter w, uint netId, Family family, Entity e, IReadOnlyList<TypeHandler> handlers)
        {
            EntityManager em = EntityManager;
            int start = w.Length;
            int countPosition = SimValueMessage.BeginEntity(w, netId, family);
            int n = 0;
            for (int i = 0; i < handlers.Count; i++)
            {
                TypeHandler h = handlers[i];
                if (h == null || h.Kind == SyncComponentKind.Presence || !em.HasComponent(e, h.Type)) continue;
                if (!TrySerializeSim(h, e, out int length)) continue;
                SimValueMessage.AddComponent(w, h.Hash, h.Kind, _managedScratch, 0, length);
                n++;
            }
            if (n == 0)
            {
                SimValueMessage.DropEntity(w, start);
                return 0;
            }
            SimValueMessage.EndEntity(w, countPosition, n);
            return n;
        }

        /// <summary>
        /// w3/agents, client: writes decoded agent value records (<c>MessageKind.AgentValues</c>) into the bound entities,
        /// without tags. Only simulation-value types are written (never prefab, owner or seed; never movers). Ids whose
        /// values held a reference that did not resolve yet are added to <paramref name="unresolved"/>. Returns the
        /// entities written.
        /// </summary>
        public List<Entity> ApplyAgentValues(byte[] data, int offset, int count, List<SyncRecord> records, ApplyResult result, List<uint> unresolved)
        {
            var written = new List<Entity>();
            if (records == null || records.Count == 0) return written;
            if (_batch.Length < count)
            {
                _batch.Dispose();
                _batch = new NativeArray<byte>(Math.Max(count, _batch.Length * 2), Allocator.Persistent);
            }
            NativeArray<byte>.Copy(data, offset, _batch, 0, count);
            EntityManager em = EntityManager;
            foreach (SyncRecord r in records)
            {
                Entity e = _ids.GetEntity(r.NetId);
                if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e) || _ids.GetFamily(r.NetId) == Family.Mover)
                {
                    result.Skipped++;
                    continue;
                }
                bool any = false, missing = false;
                foreach (SyncComponent c in r.Components)
                {
                    TypeHandler h = TypeHandlers.GetByHash(c.TypeHash);
                    if (h == null || h.Kind != c.Kind || !IsSimValueType(h, false) || !em.HasComponent(e, h.Type)) continue;
                    ReadComponent(h, e, c, offset, data, result);
                    missing |= _rs.Unresolved;
                    any = true;
                }
                if (missing) unresolved?.Add(r.NetId);
                if (!any) continue;
                result.Values++;
                written.Add(e);
            }
            return written;
        }

        /// <summary>
        /// Client: writes the simulation values of decoded <see cref="SimValueMessage"/> records into the bound entities,
        /// without tags (nothing is re-derived and nothing is proposed back). Unknown ids, missing components and types
        /// that are not simulation values here are skipped. Returns the entities written.
        /// </summary>
        public List<Entity> ApplySimValues(byte[] data, int offset, int count, List<SyncRecord> records, ApplyResult result)
        {
            var written = new List<Entity>();
            if (records == null || records.Count == 0) return written;
            if (_batch.Length < count)
            {
                _batch.Dispose();
                _batch = new NativeArray<byte>(Math.Max(count, _batch.Length * 2), Allocator.Persistent);
            }
            NativeArray<byte>.Copy(data, offset, _batch, 0, count);
            EntityManager em = EntityManager;
            foreach (SyncRecord r in records)
            {
                Entity e = _ids.GetEntity(r.NetId);
                if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e))
                {
                    result.Skipped++;
                    continue;
                }
                bool mover = _ids.GetFamily(r.NetId) == Family.Mover;
                bool any = false;
                foreach (SyncComponent c in r.Components)
                {
                    TypeHandler h = TypeHandlers.GetByHash(c.TypeHash);
                    if (h == null || h.Kind != c.Kind || !IsSimValueType(h, mover) || !em.HasComponent(e, h.Type)) continue;
                    ReadComponent(h, e, c, offset, data, result);
                    any = true;
                }
                if (!any) continue;
                result.Values++;
                written.Add(e);
            }
            return written;
        }
    }
}
