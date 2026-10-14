using System;
using System.Collections.Generic;
using System.Diagnostics;
using SkylineTogether.Core;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// w8/robust (client): a budgeted rolling audit of the entity references the game's jobs follow without a check. The
    /// first real session crashed natively on a bulldoze and the log could not say which reference dangled (a native
    /// crash leaves no managed stack). This audit walks one reference type at a time, a few chunks per frame, and logs
    /// every reference to an entity that is destroyed (or Null where the game expects one) with the component, the
    /// holder's net id and family, at most a few lines per type and minute, plus totals for the diagnostics. A log that
    /// ends in a crash then names the reference that was dangling just before. It changes nothing.
    /// </summary>
    internal sealed class ReferenceAudit
    {
        private const double BudgetMs = 0.3;
        private const int LinesPerTypePerMinute = 3;

        private abstract class Check
        {
            public string Name;
            public EntityQuery Query;
            public long Dangling;
            public int LoggedThisMinute;
            public double MinuteStart = double.NegativeInfinity;

            /// <summary>Checks one chunk; adds (holder, target) of every dangling reference to <paramref name="found"/>.</summary>
            public abstract void Run(EntityManager em, ArchetypeChunk chunk, EntityTypeHandle entities, List<(Entity Holder, Entity Target)> found);
        }

        private sealed class DataCheck<T> : Check where T : unmanaged, IComponentData
        {
            private readonly Func<T, Entity>[] _refs;
            private readonly bool _nullIsDangling;

            public DataCheck(Func<T, Entity>[] refs, bool nullIsDangling)
            {
                _refs = refs;
                _nullIsDangling = nullIsDangling;
            }

            public override void Run(EntityManager em, ArchetypeChunk chunk, EntityTypeHandle entities, List<(Entity, Entity)> found)
            {
                ComponentTypeHandle<T> handle = em.GetComponentTypeHandle<T>(true);
                NativeArray<T> values = chunk.GetNativeArray(ref handle);
                NativeArray<Entity> holders = chunk.GetNativeArray(entities);
                for (int i = 0; i < values.Length; i++)
                {
                    foreach (Func<T, Entity> get in _refs)
                    {
                        Entity target = get(values[i]);
                        if (target == Entity.Null ? _nullIsDangling : !em.Exists(target)) found.Add((holders[i], target));
                    }
                }
            }
        }

        private sealed class BufferCheck<T> : Check where T : unmanaged, IBufferElementData
        {
            private readonly Func<T, Entity> _ref;

            public BufferCheck(Func<T, Entity> get)
            {
                _ref = get;
            }

            public override void Run(EntityManager em, ArchetypeChunk chunk, EntityTypeHandle entities, List<(Entity, Entity)> found)
            {
                BufferTypeHandle<T> handle = em.GetBufferTypeHandle<T>(true);
                BufferAccessor<T> buffers = chunk.GetBufferAccessor(ref handle);
                NativeArray<Entity> holders = chunk.GetNativeArray(entities);
                for (int i = 0; i < buffers.Length; i++)
                {
                    DynamicBuffer<T> b = buffers[i];
                    for (int k = 0; k < b.Length; k++)
                    {
                        Entity target = _ref(b[k]);
                        if (target == Entity.Null || !em.Exists(target)) found.Add((holders[i], target));
                    }
                }
            }
        }

        private readonly ILog _log;
        private readonly List<Check> _checks = new List<Check>();
        private readonly List<(Entity Holder, Entity Target)> _found = new List<(Entity, Entity)>();
        private NativeArray<ArchetypeChunk> _chunks;
        private int _check, _chunk = -1;
        private World _world;
        private NetIdSystem _ids;
        private bool _failed;

        public ReferenceAudit(ILog log)
        {
            _log = log;
        }

        /// <summary>Dangling references found this session (diagnostics).</summary>
        public long Total { get; private set; }

        /// <summary>Completed rounds over every reference type (diagnostics).</summary>
        public long Rounds { get; private set; }

        public string Describe()
        {
            if (_checks.Count == 0) return "reference audit: not running";
            var parts = new List<string>();
            foreach (Check c in _checks)
            {
                if (c.Dangling > 0) parts.Add(c.Name + " " + c.Dangling);
            }
            return "reference audit: " + Rounds + " rounds, " + Total + " dangling references" + (parts.Count > 0 ? " (" + string.Join(", ", parts) + ")" : "");
        }

        private void Build(World world)
        {
            _world = world;
            _ids = world.GetOrCreateSystemManaged<NetIdSystem>();
            EntityManager em = world.EntityManager;
            ComponentType[] none = { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() };
            void Data<T>(string name, bool nullIsDangling, params Func<T, Entity>[] refs) where T : unmanaged, IComponentData
            {
                _checks.Add(new DataCheck<T>(refs, nullIsDangling) { Name = name, Query = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<T>() }, None = none }) });
            }
            void Buffer<T>(string name, Func<T, Entity> get) where T : unmanaged, IBufferElementData
            {
                _checks.Add(new BufferCheck<T>(get) { Name = name, Query = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<T>() }, None = none }) });
            }
            // the references Net.ReferencesSystem, SubObjectReferencesSystem and the info views index unchecked
            Data<Game.Net.Edge>("Net.Edge.m_Start/m_End", true, x => x.m_Start, x => x.m_End);
            Buffer<Game.Net.ConnectedEdge>("Net.ConnectedEdge.m_Edge", x => x.m_Edge);
            Buffer<Game.Net.ConnectedNode>("Net.ConnectedNode.m_Node", x => x.m_Node);
            Data<Game.Common.Owner>("Common.Owner.m_Owner", false, x => x.m_Owner);
            Buffer<Game.Objects.SubObject>("Objects.SubObject.m_SubObject", x => x.m_SubObject);
            Data<Game.Objects.Attached>("Objects.Attached.m_Parent", false, x => x.m_Parent);
            Data<Game.Buildings.PropertyRenter>("Buildings.PropertyRenter.m_Property", false, x => x.m_Property);
            Buffer<Game.Buildings.Renter>("Buildings.Renter.m_Renter", x => x.m_Renter);
            Data<Game.Citizens.Worker>("Citizens.Worker.m_Workplace", false, x => x.m_Workplace);
            Buffer<Game.Companies.Employee>("Companies.Employee.m_Worker", x => x.m_Worker);
            Data<Game.Citizens.HouseholdMember>("Citizens.HouseholdMember.m_Household", false, x => x.m_Household);
            Buffer<Game.Citizens.HouseholdCitizen>("Citizens.HouseholdCitizen.m_Citizen", x => x.m_Citizen);
            Data<Game.Creatures.CurrentVehicle>("Creatures.CurrentVehicle.m_Vehicle", false, x => x.m_Vehicle);
            Buffer<Game.Vehicles.Passenger>("Vehicles.Passenger.m_Passenger", x => x.m_Passenger);
            Buffer<Game.Routes.RouteWaypoint>("Routes.RouteWaypoint.m_Waypoint", x => x.m_Waypoint);
            Buffer<Game.Routes.RouteSegment>("Routes.RouteSegment.m_Segment", x => x.m_Segment);
        }

        /// <summary>Client, once per frame after the apply: audits a slice within the budget.</summary>
        public void Update(double now)
        {
            if (_failed) return;
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return;
            try
            {
                if (_world != world)
                {
                    DisposeChunks();
                    _checks.Clear();
                    Build(world);
                    _check = 0;
                    _chunk = -1;
                }
                if (_checks.Count == 0) return;
                EntityManager em = world.EntityManager;
                long start = Stopwatch.GetTimestamp();
                long budget = (long)(BudgetMs * Stopwatch.Frequency / 1000.0);
                EntityTypeHandle entities = em.GetEntityTypeHandle();
                int fetchedFor = -1;
                try
                {
                    while (Stopwatch.GetTimestamp() - start < budget)
                    {
                        Check c = _checks[_check];
                        // the chunk list is fetched once per frame and type; the position carries over between frames (a
                        // structural change in between may skip or repeat a chunk, which an audit tolerates)
                        if (fetchedFor != _check)
                        {
                            DisposeChunks();
                            // the game's jobs may still write these components (buffers resize): finish them first
                            c.Query.CompleteDependency();
                            _chunks = c.Query.ToArchetypeChunkArray(Allocator.TempJob);
                            fetchedFor = _check;
                        }
                        if (_chunk < 0) _chunk = 0;
                        if (_chunk >= _chunks.Length)
                        {
                            _chunk = 0;
                            _check = (_check + 1) % _checks.Count;
                            if (_check == 0)
                            {
                                Rounds++;
                                break; // one round at most per frame
                            }
                            continue;
                        }
                        ArchetypeChunk chunk = _chunks[_chunk++];
                        _found.Clear();
                        c.Run(em, chunk, entities, _found);
                        if (_found.Count > 0) Report(em, c, now);
                    }
                }
                finally
                {
                    DisposeChunks(); // never kept across frames
                }
            }
            catch (Exception ex)
            {
                _failed = true; // diagnostics only: never let it cost more than one log line
                DisposeChunks();
                _log.Warn(LogCategory.Integrity, "reference audit stopped: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void Report(EntityManager em, Check c, double now)
        {
            c.Dangling += _found.Count;
            Total += _found.Count;
            if (now - c.MinuteStart >= 60)
            {
                c.MinuteStart = now;
                c.LoggedThisMinute = 0;
            }
            foreach ((Entity holder, Entity target) in _found)
            {
                if (c.LoggedThisMinute >= LinesPerTypePerMinute) break;
                c.LoggedThisMinute++;
                uint id = _ids != null ? _ids.GetNetId(holder) : 0;
                string who = id != 0 ? "#" + id + " (" + _ids.GetFamily(id) + ")" : holder + " (unbound)";
                _log.Warn(LogCategory.Integrity, "dangling reference: " + c.Name + " of " + who + " -> " + (target == Entity.Null ? "null" : target + " (destroyed)")
                                                 + "; session total for this reference " + c.Dangling);
            }
        }

        private void DisposeChunks()
        {
            if (_chunks.IsCreated) _chunks.Dispose();
            _chunks = default;
        }

        /// <summary>Session ended: forgets the world's queries.</summary>
        public void Reset()
        {
            DisposeChunks();
            _checks.Clear();
            _world = null;
            _check = 0;
            _chunk = -1;
            _failed = false;
            Total = 0;
            Rounds = 0;
        }
    }
}
