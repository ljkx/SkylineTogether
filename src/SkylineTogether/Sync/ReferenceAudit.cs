using System;
using System.Collections.Generic;
using System.Diagnostics;
using SkylineTogether.Core;
using SkylineTogether.Core.Sync;
using SkylineTogether.Diagnostics;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// Client: finds and repairs the entity references the game follows without a check (ARCHITECTURE.md#reference-audit,
    /// #reference-safety). A dangling link of the agent link graph (<see cref="AgentLinkGraph"/>: renters, employees,
    /// pupils, patients, occupants, household members both ways, property, workplace, school, hotel, current building) is
    /// repaired the way the game's removal systems would have done it: a list element naming nobody, a destroyed or a
    /// deleted entity is dropped, a component link to one is cleared or the component removed. The repaired holder is asked
    /// from the host again, so the host's own state replaces the clean-up. Other references the game's jobs index unchecked
    /// (net topology, owners, sub-objects, routes, passengers) are only reported.
    /// <para>
    /// Two passes: right after every applied batch, the entities it wrote or tagged (<see cref="AfterApply"/>: cheap and
    /// targeted, so a batch never leaves a dangling link behind for the game's systems of the same frame), and a background
    /// sweep that walks every link type chunk by chunk and finishes a lap over the whole graph within a bounded time
    /// (<see cref="SweepPacer"/>: snapshot leftovers, references to entities the client destroyed itself). Repairs are
    /// counted per link and logged with a rate limit (<see cref="LinkRepairStats"/>).
    /// </para>
    /// Main thread only. The first native crash of a real session had no managed stack; a log that still ends in one names the
    /// reference that was dangling last.
    /// </summary>
    internal sealed class ReferenceAudit
    {
        /// <summary>A lap over every link type within this time (the first lap after going live is faster).</summary>
        private const double LapSeconds = 20, FirstLapSeconds = 8;
        private const double MinBudgetMs = 0.3, MaxBudgetMs = 2.0;
        private const int LinesPerTypePerMinute = 3;
        /// <summary>Ask the host for repaired holders and for missing referenced ids after this delay (their record is often already queued).</summary>
        private const double RequestDelaySeconds = 3;
        private const int RequestCapacity = 4096;
        private const int RequestsPerFrame = 64;

        private abstract class Check
        {
            public string Name;
            public EntityQuery Query;
            /// <summary>The agent link this check repairs; null: report only.</summary>
            public AgentLink? Link;
            public long Dangling;
            public int LoggedThisMinute;
            public double MinuteStart = double.NegativeInfinity;

            /// <summary>Checks one chunk; adds (holder, target) of every dangling reference to <paramref name="found"/>.</summary>
            public abstract void Run(EntityManager em, ArchetypeChunk chunk, EntityTypeHandle entities, List<(Entity Holder, Entity Target)> found);

            /// <summary>Notes every reference of the chunk's holders in the referrer index (<see cref="DeletionCascade.Index"/>).</summary>
            public abstract void Index(EntityManager em, ArchetypeChunk chunk, EntityTypeHandle entities, AgentReferrerIndex<Entity> index, Func<Entity, bool> alive);
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
                        if (target == Entity.Null ? _nullIsDangling : !IsLive(em, target)) found.Add((holders[i], target));
                    }
                }
            }

            public override void Index(EntityManager em, ArchetypeChunk chunk, EntityTypeHandle entities, AgentReferrerIndex<Entity> index, Func<Entity, bool> alive)
            {
                ComponentTypeHandle<T> handle = em.GetComponentTypeHandle<T>(true);
                NativeArray<T> values = chunk.GetNativeArray(ref handle);
                NativeArray<Entity> holders = chunk.GetNativeArray(entities);
                for (int i = 0; i < values.Length; i++)
                {
                    foreach (Func<T, Entity> get in _refs)
                    {
                        Entity target = get(values[i]);
                        if (target != Entity.Null) index.Add(target, holders[i], alive);
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
                        if (target == Entity.Null || !IsLive(em, target)) found.Add((holders[i], target));
                    }
                }
            }

            public override void Index(EntityManager em, ArchetypeChunk chunk, EntityTypeHandle entities, AgentReferrerIndex<Entity> index, Func<Entity, bool> alive)
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
                        if (target != Entity.Null) index.Add(target, holders[i], alive);
                    }
                }
            }
        }

        /// <summary>Exists and is not being deleted (a deleted target is gone at the end of this frame).</summary>
        private static bool IsLive(EntityManager em, Entity e) => em.Exists(e) && !em.HasComponent<Game.Common.Deleted>(e);

        private readonly ILog _log;
        private readonly List<Check> _checks = new List<Check>();
        private readonly List<(Entity Holder, Entity Target)> _found = new List<(Entity, Entity)>();
        private readonly HashSet<Entity> _holders = new HashSet<Entity>();
        private readonly List<Entity> _members = new List<Entity>();
        private readonly Dictionary<EntityArchetype, int> _linkMasks = new Dictionary<EntityArchetype, int>();
        private readonly LinkRepairStats _stats = new LinkRepairStats(AgentLinkGraph.Count);
        private readonly SweepPacer _pacer = new SweepPacer(FirstLapSeconds, MinBudgetMs, MaxBudgetMs);
        private readonly RepairRequestQueue _holderRequests = new RepairRequestQueue(RequestDelaySeconds, RequestCapacity);
        private readonly RepairRequestQueue _missingRequests = new RepairRequestQueue(RequestDelaySeconds, RequestCapacity);
        private readonly List<uint> _due = new List<uint>();
        private readonly Func<uint, bool> _isBound, _isUnbound;
        private readonly Func<int, string> _linkName = i => AgentLinkGraph.Rule((AgentLink)i).Name;
        private AgentLinkAccess _access;
        private AgentReferrerIndex<Entity> _index;
        private bool _indexBuilt;
        private Func<Entity, bool> _alive;

        /// <summary>Targets of the referrer index whose gone referrers are pruned per frame (a background lap).</summary>
        private const int IndexSweepTargetsPerFrame = 256;
        private NativeArray<ArchetypeChunk> _chunks;
        private int _check, _chunk = -1;
        private int _lapChunks, _lapVisited;
        private World _world;
        private NetIdSystem _ids;
        private bool _failed, _warnedRepair;

        public ReferenceAudit(ILog log)
        {
            _log = log;
            _isBound = id => _ids != null && _ids.GetEntity(id) != Entity.Null;
            _isUnbound = id => _ids != null && _ids.GetEntity(id) == Entity.Null;
        }

        /// <summary>Asks the host for an entity again (the integrity check's repair request); set by the controller.</summary>
        public Action<uint> RequestRepair;

        /// <summary>
        /// Net ids the applier met in references but has no entity for (<see cref="NetReaderState.MissingIds"/>); the
        /// controller hands this list to the codec while it applies host data and calls <see cref="NoteMissing"/> after.
        /// </summary>
        public readonly List<uint> MissingIds = new List<uint>();

        /// <summary>Dangling references found this session (diagnostics).</summary>
        public long Total { get; private set; }

        /// <summary>Completed sweeps over every reference type (diagnostics).</summary>
        public long Rounds => _pacer.Laps;

        public string Describe()
        {
            if (_checks.Count == 0) return "reference audit: not running";
            var parts = new List<string>();
            foreach (Check c in _checks)
            {
                if (c.Dangling > 0) parts.Add(c.Name + " " + c.Dangling);
            }
            return "reference audit: " + Rounds + " sweeps (last " + _pacer.LastLapSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s, longest "
                   + _pacer.LongestLapSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s), " + Total + " dangling references"
                   + (parts.Count > 0 ? " (" + string.Join(", ", parts) + ")" : "") + "; repaired " + _stats.Repaired + " (" + _stats.RepairedAfterApply + " right after an apply), "
                   + _stats.Requested + " objects asked from the host, " + _missingRequests.Refused + " missing ids not asked again; removal clean-up of local deletions "
                   + DeletionCascade.LocalDeletions + ", " + DeletionCascade.HoldersDeleted + " citizens and pets deleted with their household, " + DeletionCascade.MoversDeleted
                   + " movers deleted with their owner; link elements naming nobody dropped on read " + AgentLinkAccess.WireNullsDropped
                   + (_index != null ? "; referrer index " + _index.Count + " links to " + _index.Targets + " entities (" + _index.LargeTargets + " large, " + _index.Pruned + " pruned)" : "");
        }

        private void Build(World world)
        {
            _world = world;
            _ids = world.GetOrCreateSystemManaged<NetIdSystem>();
            EntityManager em = world.EntityManager;
            _access = new AgentLinkAccess(em);
            _index = new AgentReferrerIndex<Entity>(1 << 16);
            _indexBuilt = false;
            DeletionCascade.Index = _index;
            _alive = e => _world != null && _world.EntityManager.Exists(e);
            _linkMasks.Clear();
            ComponentType[] none = { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() };
            EntityQuery QueryOf<T>() => em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<T>() }, None = none });
            void Data<T>(string name, bool nullIsDangling, AgentLink? link, params Func<T, Entity>[] refs) where T : unmanaged, IComponentData
            {
                _checks.Add(new DataCheck<T>(refs, nullIsDangling) { Name = name, Link = link, Query = QueryOf<T>() });
            }
            void Buffer<T>(string name, AgentLink? link, Func<T, Entity> get) where T : unmanaged, IBufferElementData
            {
                _checks.Add(new BufferCheck<T>(get) { Name = name, Link = link, Query = QueryOf<T>() });
            }
            void Agent<T>(AgentLink link, Func<T, Entity> get) where T : unmanaged, IComponentData
            {
                AgentLinkRule rule = AgentLinkGraph.Rule(link);
                Data(rule.Name, !rule.NullAllowed, link, get);
            }
            void AgentList<T>(AgentLink link, Func<T, Entity> get) where T : unmanaged, IBufferElementData => Buffer(AgentLinkGraph.Rule(link).Name, link, get);
            // the agent link graph: repaired
            AgentList<Game.Buildings.Renter>(AgentLink.Renter, x => x.m_Renter);
            AgentList<Game.Companies.Employee>(AgentLink.Employee, x => x.m_Worker);
            AgentList<Game.Buildings.Student>(AgentLink.SchoolStudent, x => x.m_Student);
            AgentList<Game.Buildings.Patient>(AgentLink.Patient, x => x.m_Patient);
            AgentList<Game.Buildings.Occupant>(AgentLink.Occupant, x => x.m_Occupant);
            AgentList<Game.Citizens.HouseholdCitizen>(AgentLink.HouseholdCitizen, x => x.m_Citizen);
            AgentList<Game.Citizens.HouseholdAnimal>(AgentLink.HouseholdAnimal, x => x.m_HouseholdPet);
            Agent<Game.Buildings.PropertyRenter>(AgentLink.PropertyRenter, x => x.m_Property);
            Agent<Game.Citizens.HomelessHousehold>(AgentLink.HomelessHome, x => x.m_TempHome);
            Agent<Game.Citizens.TouristHousehold>(AgentLink.TouristHotel, x => x.m_Hotel);
            Agent<Game.Citizens.Worker>(AgentLink.Worker, x => x.m_Workplace);
            Agent<Game.Citizens.Student>(AgentLink.CitizenStudent, x => x.m_School);
            Agent<Game.Citizens.CurrentBuilding>(AgentLink.CurrentBuilding, x => x.m_CurrentBuilding);
            Agent<Game.Citizens.HouseholdMember>(AgentLink.HouseholdMember, x => x.m_Household);
            Agent<Game.Citizens.HouseholdPet>(AgentLink.HouseholdPet, x => x.m_Household);
            // the references Net.ReferencesSystem, SubObjectReferencesSystem, the movers and the route systems index
            // unchecked: reported (their owners repair them: structure records, the mover channel, the line check)
            Data<Game.Net.Edge>("Net.Edge.m_Start/m_End", true, null, x => x.m_Start, x => x.m_End);
            Buffer<Game.Net.ConnectedEdge>("Net.ConnectedEdge.m_Edge", null, x => x.m_Edge);
            Buffer<Game.Net.ConnectedNode>("Net.ConnectedNode.m_Node", null, x => x.m_Node);
            Data<Game.Common.Owner>("Common.Owner.m_Owner", false, null, x => x.m_Owner);
            Buffer<Game.Objects.SubObject>("Objects.SubObject.m_SubObject", null, x => x.m_SubObject);
            Data<Game.Objects.Attached>("Objects.Attached.m_Parent", false, null, x => x.m_Parent);
            Data<Game.Creatures.CurrentVehicle>("Creatures.CurrentVehicle.m_Vehicle", false, null, x => x.m_Vehicle);
            Buffer<Game.Vehicles.Passenger>("Vehicles.Passenger.m_Passenger", null, x => x.m_Passenger);
            Buffer<Game.Routes.RouteWaypoint>("Routes.RouteWaypoint.m_Waypoint", null, x => x.m_Waypoint);
            Buffer<Game.Routes.RouteSegment>("Routes.RouteSegment.m_Segment", null, x => x.m_Segment);
        }

        private bool EnsureWorld()
        {
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return false;
            if (_world != world)
            {
                DisposeChunks();
                _checks.Clear();
                Build(world);
                _check = 0;
                _chunk = -1;
                _pacer.Reset();
            }
            return _checks.Count > 0;
        }

        // ================================================================== targeted pass

        /// <summary>
        /// Client, right after an applied batch or agent value message: repairs the agent links of the entities it wrote
        /// or tagged, so the frame's game systems never see a link the apply left dangling (an existing entity keeps its
        /// old reference when the new one does not resolve; a new entity gets a null; a despawn may miss a list the client
        /// had out of date).
        /// </summary>
        public void AfterApply(IEnumerable<Entity> entities, double now)
        {
            if (entities == null || !EnsureWorld()) return;
            long start = Perf.Start();
            try
            {
                EntityManager em = _world.EntityManager;
                _access.EntityManager = em;
                foreach (Entity e in entities)
                {
                    RepairLinksOf(em, e, now);
                    // every holder whose links the apply wrote is noted again, so a later deletion finds it
                    if (_index != null && em.Exists(e) && !em.HasComponent<Game.Common.Deleted>(e)) AgentLinkGraph.IndexHolder(_access, e, _index, _alive);
                }
            }
            catch (Exception ex)
            {
                ReportRepairFailure(ex);
            }
            finally
            {
                Perf.Stop(Perf.ReferenceRepair, start);
            }
        }

        private void RepairLinksOf(EntityManager em, Entity e, double now)
        {
            if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e) || em.HasComponent<Game.Tools.Temp>(e)) return;
            int mask = LinkMask(em, e);
            if (mask == 0) return;
            for (int link = 0; link < AgentLinkGraph.Count; link++)
            {
                if ((mask & (1 << link)) != 0) Repair(e, (AgentLink)link, true, now);
            }
        }

        /// <summary>The agent links an entity's archetype has (bit per <see cref="AgentLink"/>), cached per archetype.</summary>
        private int LinkMask(EntityManager em, Entity e)
        {
            EntityArchetype archetype = em.GetChunk(e).Archetype;
            if (_linkMasks.TryGetValue(archetype, out int mask)) return mask;
            mask = 0;
            NativeArray<ComponentType> types = archetype.GetComponentTypes(Allocator.Temp);
            try
            {
                for (int link = 0; link < AgentLinkGraph.Count; link++)
                {
                    TypeIndex index = AgentLinkAccess.TypeOf((AgentLink)link).TypeIndex;
                    foreach (ComponentType t in types)
                    {
                        if (t.TypeIndex != index) continue;
                        mask |= 1 << link;
                        break;
                    }
                }
            }
            finally
            {
                types.Dispose();
            }
            _linkMasks[archetype] = mask;
            return mask;
        }

        /// <summary>
        /// Repairs one link of one holder (<see cref="AgentLinkRule.Decide"/>); counts, logs (rate-limited) and queues the
        /// holder for a repair request. Returns the number of changes.
        /// </summary>
        private int Repair(Entity holder, AgentLink link, bool afterApply, double now)
        {
            AgentLinkRule rule = AgentLinkGraph.Rule(link);
            Entity target;
            LinkTarget state;
            int dead = 0, gone = 0;
            int n;
            // read first: deleting a holder unbinds its id
            uint id = _ids != null ? _ids.GetNetId(holder) : 0;
            if (rule.IsBuffer)
            {
                _members.Clear();
                if (!_access.GetMembers(holder, link, _members)) return 0;
                target = Entity.Null;
                state = LinkTarget.Live;
                foreach (Entity m in _members)
                {
                    LinkTarget s = _access.StateOf(m);
                    if (s == LinkTarget.Live) continue;
                    if (s != LinkTarget.Null) gone++;
                    if (dead++ == 0)
                    {
                        target = m;
                        state = s;
                    }
                }
                if (dead == 0) return 0;
                n = _access.DropElements(holder, link, null);
            }
            else
            {
                if (!_access.TryGetForward(holder, link, out target)) return 0;
                state = _access.StateOf(target);
                LinkCleanup action = rule.Decide(state);
                if (action == LinkCleanup.Keep) return 0;
                dead = 1;
                n = action == LinkCleanup.DeleteHolder
                    ? DeletionCascade.DeleteWithCascade(_world.EntityManager, holder, null) // the game deletes a citizen or pet whose household is gone
                    : _access.CleanForward(holder, link, action);
            }
            if (n == 0) return 0;
            Total += dead;
            if (_stats.Note((int)link, dead, true, afterApply, now))
            {
                string who = id != 0 ? "#" + id + " (" + _ids.GetFamily(id) + ")" : holder + " (unbound)";
                string what = state == LinkTarget.Null ? "null" : target + (state == LinkTarget.Deleted ? " (deleted)" : " (destroyed)");
                string fix = rule.IsBuffer ? (dead == 1 ? "element dropped" : dead + " elements dropped") : Fix(rule.Decide(state));
                _log.Warn(LogCategory.Integrity, "repaired dangling reference" + (afterApply ? " after an apply" : "") + ": " + rule.Name + " of " + who + " -> " + what + " (" + fix
                                                 + "); session total for this reference " + _stats.RepairedOf((int)link));
            }
            // the host's own state replaces the clean-up: a cleared or removed link (the holder may have moved on there),
            // or an agent's list (a full agent record re-derives nothing); a building's list only lost members that are gone
            // (a deleted holder is unbound and asked for by the cascade; a list that only named nobody is what the host's own
            // normalised record carries as well, so asking brings nothing: RepairRequestPolicy)
            if (id != 0 && !rule.IsBuffer && rule.Decide(state) == LinkCleanup.DeleteHolder) return n;
            if (id != 0 && RepairRequestPolicy.AskHolder(rule.IsBuffer, _ids.GetFamily(id) == Family.Agent, gone)) _holderRequests.Add(id, now);
            return n;
        }

        private void ReportRepairFailure(Exception ex)
        {
            if (_warnedRepair) return;
            _warnedRepair = true; // the repair never stops the apply; one line per session is enough
            _log.Warn(LogCategory.Integrity, "reference repair failed (further failures are not logged): " + ex.GetType().Name + ": " + ex.Message);
        }

        /// <summary>Queues the ids <see cref="MissingIds"/> collected during the last apply for a request, and clears the list.</summary>
        /// <summary>Asks the host for <paramref name="id"/> after the request delay (an entity this client deleted on its own).</summary>
        public void RequestLater(uint id, double now)
        {
            if (id != 0) _missingRequests.Add(id, now);
        }

        private static string Fix(LinkCleanup action)
        {
            switch (action)
            {
                case LinkCleanup.ClearReference: return "reference cleared";
                case LinkCleanup.DeleteHolder: return "deleted with its household, as the game does";
                default: return "component removed";
            }
        }

        public void NoteMissing(double now)
        {
            if (MissingIds.Count == 0) return;
            foreach (uint id in MissingIds) _missingRequests.Add(id, now);
            MissingIds.Clear();
        }

        // ================================================================== background sweep

        /// <summary>
        /// Client, once per frame after the apply: sweeps a slice within the pacer's budget, and asks the host for repaired
        /// holders and missing ids once nothing is queued for apply (<paramref name="backlog"/> batches).
        /// </summary>
        public void Update(double now, int backlog)
        {
            if (!EnsureWorld()) return;
            if (backlog == 0) SendRequests(now);
            string summary = _stats.TakeSummary(now, _linkName);
            if (summary != null)
            {
                _log.Info(LogCategory.Integrity, "reference repair: " + summary + "; session total " + _stats.Repaired + ", " + _stats.Requested + " objects asked from the host");
            }
            if (_failed) return; // the sweep stopped; the targeted pass and the requests go on
            EntityManager em = _world.EntityManager;
            _access.EntityManager = em;
            long start = Stopwatch.GetTimestamp();
            long perf = Perf.Start();
            try
            {
                if (!_indexBuilt) BuildIndex(em);
                // the referrer index only grows otherwise (entries are added, never removed when a link changes)
                else if (_index != null) _index.Sweep(_alive, IndexSweepTargetsPerFrame);
                if (!_pacer.InLap) StartLap(now);
                double progress = _lapChunks > 0 ? (double)_lapVisited / _lapChunks : 1.0;
                long budget = (long)(_pacer.BudgetMs(now, progress) * Stopwatch.Frequency / 1000.0);
                int fetchedFor = -1;
                try
                {
                    while (Stopwatch.GetTimestamp() - start < budget)
                    {
                        Check c = _checks[_check];
                        // the chunk list is fetched once per frame and type, and again after a repair changed the structure
                        // (a stale chunk pointer may point at a freed chunk); the position carries over between frames (a
                        // structural change in between may skip or repeat a chunk, which the sweep tolerates)
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
                                _pacer.LapDone(now);
                                break; // one lap at most per frame; the next starts next frame
                            }
                            continue;
                        }
                        ArchetypeChunk chunk = _chunks[_chunk++];
                        _lapVisited++;
                        _found.Clear();
                        EntityTypeHandle entityType = em.GetEntityTypeHandle();
                        c.Run(em, chunk, entityType, _found);
                        if (c.Link.HasValue && _index != null) c.Index(em, chunk, entityType, _index, _alive);
                        if (_found.Count == 0) continue;
                        if (c.Link.HasValue)
                        {
                            long structural = _access.StructuralChanges, deletions = DeletionCascade.HoldersDeleted + DeletionCascade.MoversDeleted;
                            RepairFound(c, now);
                            // refetch before the next chunk (a removed component or a deleted holder moved entities between chunks)
                            if (_access.StructuralChanges != structural || DeletionCascade.HoldersDeleted + DeletionCascade.MoversDeleted != deletions) fetchedFor = -1;
                        }
                        else
                        {
                            Report(c, now);
                        }
                    }
                }
                finally
                {
                    DisposeChunks(); // never kept across frames
                }
            }
            catch (Exception ex)
            {
                _failed = true; // never let it cost more than one log line
                DisposeChunks();
                _log.Warn(LogCategory.Integrity, "reference audit stopped: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Perf.Stop(Perf.ReferenceRepair, perf);
            }
        }

        /// <summary>
        /// Client, once when it goes live: notes every agent link and every mover's owner, citizen and pet in the referrer
        /// index (<see cref="DeletionCascade.Index"/>), so the first deletion already finds every holder. The sweep and every
        /// apply keep it up to date afterwards.
        /// </summary>
        private void BuildIndex(EntityManager em)
        {
            _indexBuilt = true;
            if (_index == null) return;
            long start = Stopwatch.GetTimestamp();
            EntityTypeHandle entityType = em.GetEntityTypeHandle();
            foreach (Check c in _checks)
            {
                if (!c.Link.HasValue) continue;
                c.Query.CompleteDependency();
                NativeArray<ArchetypeChunk> chunks = c.Query.ToArchetypeChunkArray(Allocator.Temp);
                try
                {
                    foreach (ArchetypeChunk chunk in chunks) c.Index(em, chunk, entityType, _index, null);
                }
                finally
                {
                    chunks.Dispose();
                }
            }
            int movers = 0;
            using (EntityQuery q = em.CreateEntityQuery(new EntityQueryDesc
            {
                Any = new[] { ComponentType.ReadOnly<Game.Vehicles.Vehicle>(), ComponentType.ReadOnly<Game.Creatures.Creature>() },
                None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
            }))
            {
                NativeArray<Entity> entities = q.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (Entity e in entities) movers += DeletionCascade.IndexMover(em, e);
                }
                finally
                {
                    entities.Dispose();
                }
            }
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            _log.Info(LogCategory.Integrity, "referrer index built: " + _index.Count + " links to " + _index.Targets + " entities (" + movers + " of movers) in "
                                             + ms.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms; every deletion on this client now cleans its referrers in the same frame");
        }

        private void StartLap(double now)
        {
            int total = 0;
            foreach (Check c in _checks) total += c.Query.CalculateChunkCountWithoutFiltering();
            _lapChunks = Math.Max(1, total);
            _lapVisited = 0;
            _pacer.StartLap(now, _pacer.Laps == 0 ? FirstLapSeconds : LapSeconds);
        }

        private void RepairFound(Check c, double now)
        {
            _holders.Clear();
            foreach ((Entity Holder, Entity Target) f in _found) _holders.Add(f.Holder);
            AgentLink link = c.Link.Value;
            foreach (Entity holder in _holders)
            {
                if (!_access.IsUsable(holder)) continue;
                int before = (int)_stats.RepairedOf((int)link);
                Repair(holder, link, false, now);
                c.Dangling += _stats.RepairedOf((int)link) - before;
            }
        }

        private void Report(Check c, double now)
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

        private void SendRequests(double now)
        {
            if (RequestRepair == null) return;
            _due.Clear();
            // a repaired holder that still exists: the host's record replaces the clean-up; a referenced id that is
            // still unknown here: the host sends it (or a despawn when it is gone there too)
            _holderRequests.TakeDue(now, RequestsPerFrame, _isBound, _due);
            _missingRequests.TakeDue(now, RequestsPerFrame - _due.Count, _isUnbound, _due);
            foreach (uint id in _due) RequestRepair(id);
            _stats.NoteRequested(_due.Count);
        }

        private void DisposeChunks()
        {
            if (_chunks.IsCreated) _chunks.Dispose();
            _chunks = default;
        }

        /// <summary>Session ended: forgets the world's queries and the counters.</summary>
        public void Reset()
        {
            DisposeChunks();
            _checks.Clear();
            _linkMasks.Clear();
            _world = null;
            _access = null;
            _check = 0;
            _chunk = -1;
            _failed = false;
            _warnedRepair = false;
            Total = 0;
            _stats.Reset();
            _pacer.Reset();
            _holderRequests.Clear();
            _missingRequests.Clear();
            MissingIds.Clear();
            DeletionCascade.ResetSession();
            _index = null;
            _indexBuilt = false;
        }
    }
}
