using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using SkylineTogether.Core;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Identity;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Scheduling;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Diagnostics;
using SkylineTogether.Session;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    // Host validation of client proposals (role, conflicts, rules), refusals with rollback, build-order rebasing and
    // charging the city.
    public sealed partial class SyncController
    {
        // ================================================================== gameplay branch: host validation and rollback

        private static ProposalFacts Facts(List<SyncRecord> records, out uint seq, out uint baseEpoch)
        {
            var f = new ProposalFacts();
            seq = 0;
            // a proposal without a header (older client) skips the conflict check
            baseEpoch = uint.MaxValue;
            foreach (SyncRecord r in records)
            {
                switch (r.Kind)
                {
                    case SyncRecordKind.Spawn: f.Spawns++; break;
                    case SyncRecordKind.Upsert:
                    case SyncRecordKind.Values: f.Upserts++; break;
                    case SyncRecordKind.Despawn: f.Despawns++; break;
                    case SyncRecordKind.ZoneCells: f.ZoneBlocks++; break;
                    case SyncRecordKind.Cost:
                        f.HasCost = true;
                        f.Cost += r.Amount;
                        break;
                    case SyncRecordKind.ProposalHeader:
                        seq = r.Seq;
                        baseEpoch = r.BaseEpoch;
                        f.Flags = r.ProposalFlags;
                        break;
                }
            }
            return f;
        }

        /// <summary>
        /// ARCHITECTURE.md#host-validation-and-commit over the host's world: permission of the sender's role, size, existence and
        /// conflicts of every entity the proposal edits or deletes, id grants of its creations, and money.
        /// </summary>
        private RejectReason Validate(PeerId peer, List<SyncRecord> records, in ProposalFacts facts, uint baseEpoch)
        {
            if (!Allows(peer, ProposalRules.Required(facts))) return RejectReason.NoPermission;
            RejectReason size = ProposalRules.CheckSize(facts);
            if (size != RejectReason.None) return size;
            EntityManager em = _codec.EntityManager;
            var spawned = new HashSet<uint>();
            foreach (SyncRecord r in records)
            {
                if (r.Kind == SyncRecordKind.Spawn) spawned.Add(r.NetId);
            }
            foreach (SyncRecord r in records)
            {
                // mod compatibility: an object of an asset or mod prefab the host does not have can never be created
                // here (the client is told and its prediction rolled back)
                if ((r.Kind == SyncRecordKind.Spawn || r.Kind == SyncRecordKind.Upsert) && _codec.NamesUnknownPrefab(r))
                {
                    _log.Info(LogCategory.Replication, peer + ": #" + r.NetId + " uses an asset or mod prefab the host does not have");
                    return RejectReason.HostError;
                }
                switch (r.Kind)
                {
                    case SyncRecordKind.Spawn:
                    {
                        // events, chirps and journal entries come from the host's simulation only
                        if (FamilyClassifier.IsHostOnly(r.Family)) return RejectReason.Invalid;
                        Entity existing = _ids.GetEntity(r.NetId);
                        bool bound = existing != Entity.Null && em.Exists(existing);
                        // a creation needs an unused id of the sender's own grants (released ids are never reusable by a client)
                        if (!bound && !_grants.IsUnused(peer.Value, r.NetId)) return RejectReason.Invalid;
                        if (bound && _ledger.IsConflict(r.NetId, baseEpoch, peer.Value)) return RejectReason.Conflict;
                        break;
                    }
                    case SyncRecordKind.Upsert:
                    case SyncRecordKind.Values:
                    {
                        if (spawned.Contains(r.NetId)) break;
                        Entity e = _ids.GetEntity(r.NetId);
                        // edited meanwhile deleted by somebody else (or never committed: a refused earlier proposal)
                        if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) return RejectReason.Conflict;
                        if (!FamilyClassifier.IsReplicated(_ids.GetFamily(r.NetId))) return RejectReason.Invalid;
                        if (FamilyClassifier.IsHostOnly(_ids.GetFamily(r.NetId))) return RejectReason.Invalid;
                        if (_ledger.IsConflict(r.NetId, baseEpoch, peer.Value)) return RejectReason.Conflict;
                        break;
                    }
                    case SyncRecordKind.Despawn:
                    {
                        Entity e = _ids.GetEntity(r.NetId);
                        if (e == Entity.Null || !em.Exists(e)) break; // already gone: nothing to delete
                        // mod compatibility: mod tools may delete vehicles, citizens and animals (Better Bulldozer)
                        Family despawned = _ids.GetFamily(r.NetId);
                        if (!FamilyClassifier.IsReplicated(despawned) && !(despawned == Family.Mover && (facts.Flags & ProposalFlags.DirectTool) != 0)) return RejectReason.Invalid;
                        if (FamilyClassifier.IsHostOnly(despawned)) return RejectReason.Invalid;
                        if (_ledger.IsConflict(r.NetId, baseEpoch, peer.Value)) return RejectReason.Conflict;
                        break;
                    }
                }
            }
            _proposalMaxRefund = RefundBound(em, records);
            Entity city = _city.City;
            if (facts.HasCost && city != Entity.Null && em.HasComponent<Game.City.PlayerMoney>(city))
            {
                Game.City.PlayerMoney money = em.GetComponentData<Game.City.PlayerMoney>(city);
                RejectReason cost = ProposalRules.CheckCost(facts, money.money, money.m_Unlimited);
                if (cost != RejectReason.None) return cost;
            }
            return RejectReason.None;
        }

        /// <summary>
        /// Host: refuses a proposal and rolls it back on its originator (ARCHITECTURE.md#host-validation-and-commit): a result record, then every entity
        /// the proposal touched in the host's state (alive: full record; unknown here: despawn, which removes the
        /// client's predicted creations) and the host's cells of every zone block it painted.
        /// </summary>
        private void RejectProposal(PeerId peer, uint seq, RejectReason reason, List<SyncRecord> records)
        {
            var ids = new List<uint>();
            var zones = new List<ZoneBlockKey>();
            foreach (SyncRecord r in records)
            {
                switch (r.Kind)
                {
                    case SyncRecordKind.Spawn:
                    case SyncRecordKind.Upsert:
                    case SyncRecordKind.Values:
                    case SyncRecordKind.Despawn:
                        ids.Add(r.NetId);
                        break;
                    case SyncRecordKind.ZoneCells:
                        zones.Add(r.Block);
                        break;
                }
            }
            _log.Info(LogCategory.Replication, peer + ": refused proposal " + seq + " (" + reason + "; " + ids.Count + " entities, " + zones.Count + " zone blocks)");
            SendRollback(peer, seq, reason, ids, zones);
            CountViolation(peer, reason);
        }

        /// <summary>Host: refuses an intent or global-state edit (rollback of its targets; seq 0).</summary>
        private void RejectIntent(PeerId peer, RejectReason reason, IReadOnlyList<uint> ids)
        {
            if (_codec == null || !IsHost) return;
            SendRollback(peer, 0, reason, ids ?? Array.Empty<uint>(), null);
            CountViolation(peer, reason);
        }

        private void CountViolation(PeerId peer, RejectReason reason)
        {
            if (!ViolationTracker.IsViolation(reason) || !_violations.Record(peer.Value, _sessions.Clock.Seconds)) return;
            _log.Warn(LogCategory.Replication, peer + ": too many invalid or flooding proposals; kicking");
            _violations.Forget(peer.Value);
            _sessions.Host?.Kick(peer, "Too many invalid changes were sent to the host.");
        }

        private void SendRollback(PeerId peer, uint seq, RejectReason reason, IReadOnlyList<uint> ids, List<ZoneBlockKey> zones)
        {
            EntityManager em = _codec.EntityManager;
            var w = new SyncBatch.Writer();
            w.ProposalResult(seq, reason);
            var seen = new HashSet<uint>();
            int full = 0;
            _out.TryGetValue(peer.Value, out PeerOut paced);
            foreach (uint id in ids)
            {
                if (id == 0 || !seen.Add(id)) continue;
                try
                {
                    Entity e = _ids.GetEntity(id);
                    Family family = _ids.GetFamily(id);
                    bool alive = e != Entity.Null && em.Exists(e) && !em.HasComponent<Game.Common.Deleted>(e);
                    if (!alive)
                    {
                        w.Despawn(id);
                    }
                    else if (FamilyClassifier.IsReplicated(family) && full >= MaxRollbackRecords && paced != null)
                    {
                        // host lag: a refused mass edit (a big bulldoze) re-encoded every entity in one frame;
                        // beyond the first records the rest goes through the peer's paced queue
                        paced.Queue.Mark(id, DeferredClass.Upsert, Perf.Now);
                    }
                    else if (FamilyClassifier.IsReplicated(family))
                    {
                        full++;
                        if (em.HasComponent<PrefabRef>(e)) _codec.EnsurePrefab(em.GetComponentData<PrefabRef>(e).m_Prefab, w);
                        _codec.WriteEntity(w, SyncRecordKind.Upsert, id, family, e);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn(LogCategory.Replication, peer + ": rolling back #" + id + " failed", ex);
                }
                if (w.RecordCount >= ResendChunkRecords)
                {
                    Send(peer, w, Perf.StructureBytes);
                    w = new SyncBatch.Writer();
                }
            }
            if (zones != null)
            {
                foreach (ZoneBlockKey key in zones)
                {
                    Entity block = FindBlock(key);
                    if (block != Entity.Null) WriteZoneBlock(w, block, true);
                }
            }
            if (!w.IsEmpty) Send(peer, w, Perf.StructureBytes);
        }

        /// <summary>Host: the build orders of the existing edges a proposal edits or deletes (read before the apply).</summary>
        private List<BuildOrderRange> TouchedEdgeBuildOrders(List<SyncRecord> records)
        {
            var list = new List<BuildOrderRange>();
            EntityManager em = _codec.EntityManager;
            foreach (SyncRecord r in records)
            {
                if (r.Kind != SyncRecordKind.Upsert && r.Kind != SyncRecordKind.Despawn) continue;
                Entity e = _ids.GetEntity(r.NetId);
                if (e == Entity.Null || !em.Exists(e) || !em.HasComponent<Game.Net.BuildOrder>(e)) continue;
                Game.Net.BuildOrder b = em.GetComponentData<Game.Net.BuildOrder>(e);
                list.Add(new BuildOrderRange(b.m_Start, b.m_End));
            }
            return list;
        }

        /// <summary>
        /// Host: re-bases the build orders of a client's fresh edges onto the host counter (ARCHITECTURE.md#host-validation-and-commit), so zone-block
        /// priority and node-lane order are identical on every peer; edges split from host edges keep their inherited
        /// sub-ranges. The committed edges then advance vanilla's counter themselves (<c>UpdateBuildOrderJob</c>).
        /// </summary>
        private void RebaseBuildOrders(List<Entity> spawned, List<BuildOrderRange> touchedEdges)
        {
            if (spawned.Count == 0) return;
            try
            {
                EntityManager em = _codec.EntityManager;
                var edges = new List<Entity>();
                var ranges = new List<BuildOrderRange>();
                foreach (Entity e in spawned)
                {
                    if (!em.Exists(e) || !em.HasComponent<Game.Net.Edge>(e) || !em.HasComponent<Game.Net.BuildOrder>(e)) continue;
                    Game.Net.BuildOrder b = em.GetComponentData<Game.Net.BuildOrder>(e);
                    edges.Add(e);
                    ranges.Add(new BuildOrderRange(b.m_Start, b.m_End));
                }
                if (edges.Count == 0) return;
                // the apply's structural changes completed every job, so the counter is readable on the main thread
                uint counter = 0;
                try
                {
                    if (_generateEdges != null) counter = _generateEdges.GetBuildOrder().value;
                }
                catch (Exception)
                {
                    counter = 0; // a job still owns it: the allocator's own maximum is used
                }
                int n = BuildOrderPlanner.RebaseFresh(_buildOrders, ranges, touchedEdges, counter);
                if (n == 0) return;
                for (int i = 0; i < edges.Count; i++)
                {
                    em.SetComponentData(edges[i], new Game.Net.BuildOrder { m_Start = ranges[i].Start, m_End = ranges[i].End });
                }
                _log.Debug(LogCategory.Replication, "re-based the build order of " + n + " committed edge(s); next " + _buildOrders.Next);
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Replication, "re-basing build orders failed (zoning priority may differ)", ex);
            }
        }

        private EntityQuery _temps;

        private long _proposalMaxRefund;
        private EntityQuery _economyParameters;

        /// <summary>
        /// The most a proposal may refund: the recent modification costs (<c>Recent</c>, what the game's
        /// refunds are computed from) of every entity it removes or changes and of their sub-elements, times the largest
        /// refund percentage of the economy parameters.
        /// </summary>
        private long RefundBound(EntityManager em, List<SyncRecord> records)
        {
            long recent = 0;
            foreach (SyncRecord r in records)
            {
                if (r.Kind != SyncRecordKind.Despawn && r.Kind != SyncRecordKind.Upsert && r.Kind != SyncRecordKind.Values) continue;
                Entity e = _ids.GetEntity(r.NetId);
                if (e != Entity.Null && em.Exists(e)) recent += RecentCosts(em, e, 0);
                if (recent >= int.MaxValue) break;
            }
            if (recent == 0) return 0;
            double pct = 1.0;
            try
            {
                if (_economyParameters == default) _economyParameters = em.CreateEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
                if (!_economyParameters.IsEmptyIgnoreFilter)
                {
                    EconomyParameterData p = _economyParameters.GetSingleton<EconomyParameterData>();
                    pct = Math.Max(Math.Max(Math.Max(p.m_BuildRefundPercentage.x, p.m_BuildRefundPercentage.y), Math.Max(p.m_BuildRefundPercentage.z, p.m_RoadRefundPercentage.x)),
                                   Math.Max(p.m_RoadRefundPercentage.y, p.m_RoadRefundPercentage.z));
                }
            }
            catch (Exception)
            {
                pct = 1.0;
            }
            return ProposalRules.RefundBound(recent, pct);
        }

        private static long RecentCosts(EntityManager em, Entity e, int depth)
        {
            if (e == Entity.Null || !em.Exists(e)) return 0;
            long sum = 0;
            if (em.HasComponent<Recent>(e)) sum += Math.Max(0, em.GetComponentData<Recent>(e).m_ModificationCost);
            if (depth >= 2) return sum;
            // sub-elements removed with their owner (derived, no net id) refund too
            if (em.HasBuffer<Game.Net.SubNet>(e))
            {
                DynamicBuffer<Game.Net.SubNet> b = em.GetBuffer<Game.Net.SubNet>(e, true);
                for (int i = 0; i < b.Length; i++) sum += RecentCosts(em, b[i].m_SubNet, depth + 1);
            }
            if (em.HasBuffer<Game.Objects.SubObject>(e))
            {
                DynamicBuffer<Game.Objects.SubObject> b = em.GetBuffer<Game.Objects.SubObject>(e, true);
                for (int i = 0; i < b.Length; i++) sum += RecentCosts(em, b[i].m_SubObject, depth + 1);
            }
            if (em.HasBuffer<Game.Buildings.InstalledUpgrade>(e))
            {
                DynamicBuffer<Game.Buildings.InstalledUpgrade> b = em.GetBuffer<Game.Buildings.InstalledUpgrade>(e, true);
                for (int i = 0; i < b.Length; i++) sum += RecentCosts(em, b[i].m_Upgrade, depth + 1);
            }
            return sum;
        }

        /// <summary>
        /// The least the created objects cost, as the game's object tool prices a new object (its
        /// prefab's construction cost, or the upgrade cost of a service upgrade); a free-standing tree costs its prefab's
        /// price times the multiplier of its growth stage. Nets are priced once the host composed them
        /// (<see cref="SettleNetCharges"/>).
        /// </summary>
        private long MinimumCharge(List<Entity> spawned)
        {
            if (spawned.Count == 0) return 0;
            EntityManager em = _codec.EntityManager;
            long sum = 0;
            foreach (Entity e in spawned)
            {
                sum += ObjectPrice(em, e);
                if (sum >= int.MaxValue) return int.MaxValue;
            }
            return sum;
        }

        /// <summary>
        /// What the game's object tool charges for creating <paramref name="e"/> (0 for anything it does not price).
        /// Trees owned by another object (a road's or a building's) are part of their owner's price.
        /// </summary>
        private long ObjectPrice(EntityManager em, Entity e)
        {
            if (!em.Exists(e) || !em.HasComponent<PrefabRef>(e)) return 0;
            Entity prefab = em.GetComponentData<PrefabRef>(e).m_Prefab;
            if (prefab == Entity.Null || !em.Exists(prefab)) return 0;
            if (em.HasComponent<TreeData>(prefab))
            {
                if (!em.HasComponent<PlaceableObjectData>(prefab) || HasOwner(em, e)) return 0;
                uint baseCost = em.GetComponentData<PlaceableObjectData>(prefab).m_ConstructionCost;
                TreeGrowth growth = em.HasComponent<Game.Objects.Tree>(e) ? GrowthOf(em.GetComponentData<Game.Objects.Tree>(e).m_State) : TreeGrowth.Base;
                Unity.Mathematics.int3 m = TreeCostMultipliers(em);
                return ConstructionCosts.TreeCost(baseCost, growth, m.x, m.y, m.z);
            }
            if (em.HasComponent<PlaceableObjectData>(prefab)) return em.GetComponentData<PlaceableObjectData>(prefab).m_ConstructionCost;
            if (em.HasComponent<ServiceUpgradeData>(prefab)) return em.GetComponentData<ServiceUpgradeData>(prefab).m_UpgradeCost;
            return 0;
        }

        /// <summary>The growth stage the game prices a tree by (its state masked to the age flags, as the object tool does).</summary>
        private static TreeGrowth GrowthOf(Game.Objects.TreeState state)
        {
            const Game.Objects.TreeState Ages = Game.Objects.TreeState.Teen | Game.Objects.TreeState.Adult | Game.Objects.TreeState.Elderly
                                                | Game.Objects.TreeState.Dead | Game.Objects.TreeState.Stump;
            switch (state & Ages)
            {
                case Game.Objects.TreeState.Teen: return TreeGrowth.Teen;
                case Game.Objects.TreeState.Adult: return TreeGrowth.Adult;
                case Game.Objects.TreeState.Elderly: return TreeGrowth.Elderly;
                default: return TreeGrowth.Base;
            }
        }

        /// <summary>The economy's tree cost multipliers (teen, adult, elderly); 1 when unknown, which only lowers the bound.</summary>
        private Unity.Mathematics.int3 TreeCostMultipliers(EntityManager em)
        {
            try
            {
                if (_economyParameters == default) _economyParameters = em.CreateEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
                if (!_economyParameters.IsEmptyIgnoreFilter) return _economyParameters.GetSingleton<EconomyParameterData>().m_TreeCostMultipliers;
            }
            catch (Exception)
            {
                // fall through
            }
            return new Unity.Mathematics.int3(1, 1, 1);
        }

        private static bool HasOwner(EntityManager em, Entity e) => em.HasComponent<Game.Common.Owner>(e) && em.GetComponentData<Game.Common.Owner>(e).m_Owner != Entity.Null;

        // ------------------------------------------------------------------ nets: priced after the host composed them

        /// <summary>A proposal's nets, priced at the end of the frame (the game composes new edges during the frame).</summary>
        private sealed class NetCharge
        {
            public PeerId Peer;
            public long ClientCost;
            public long ObjectFloor;
            public long MaxRefund;
            public long Charged;
            /// <summary>What the edges the proposal removed would cost (<see cref="ConstructionCosts.RemovedEdgeCeiling"/>).</summary>
            public long RemovedCeilings;
            public readonly List<Entity> Created = new List<Entity>();
            /// <summary>Existing edges it changed, with the price of their composition before the change.</summary>
            public readonly List<(Entity Edge, uint Unit, uint Elevation)> Upgraded = new List<(Entity, uint, uint)>();
            public int Frames;
        }

        private readonly List<NetCharge> _netCharges = new List<NetCharge>();

        /// <summary>Frames a proposal's new edges may stay uncomposed before they are priced with what is there.</summary>
        private const int NetChargeFrames = 4;

        /// <summary>
        /// Host, before a proposal is applied: the price of the free-standing edges it removes and the composition price
        /// of those it changes (vanilla tools only: a mod tool that moves an edge changes its composition for free), and
        /// for a mod tool's proposal the price of the objects it removes. Null when it touches no edge.
        /// </summary>
        private NetCharge CollectCostBasis(List<SyncRecord> records, bool direct, out long removedObjects)
        {
            removedObjects = 0;
            EntityManager em = _codec.EntityManager;
            var charge = new NetCharge();
            foreach (SyncRecord r in records)
            {
                if (r.Kind != SyncRecordKind.Despawn && r.Kind != SyncRecordKind.Upsert) continue;
                Entity e = _ids.GetEntity(r.NetId);
                if (e == Entity.Null || !em.Exists(e)) continue;
                if (r.Kind == SyncRecordKind.Despawn && direct) removedObjects += ObjectPrice(em, e);
                if (!em.HasComponent<Game.Net.Edge>(e) || HasOwner(em, e) || !TryEdgePrice(em, e, out float length, out uint unit, out uint elevation, out _)) continue;
                if (r.Kind == SyncRecordKind.Despawn) charge.RemovedCeilings += ConstructionCosts.RemovedEdgeCeiling(length, unit);
                else if (!direct) charge.Upgraded.Add((e, unit, elevation));
            }
            return charge;
        }

        /// <summary>Adds the proposal's new free-standing edges; true when there is anything to price later.</summary>
        private bool AddCreatedEdges(NetCharge charge, List<Entity> spawned)
        {
            EntityManager em = _codec.EntityManager;
            foreach (Entity e in spawned)
            {
                if (em.Exists(e) && em.HasComponent<Game.Net.Edge>(e) && !HasOwner(em, e)) charge.Created.Add(e);
            }
            return charge.Created.Count > 0 || charge.Upgraded.Count > 0;
        }

        /// <summary>
        /// The price data of a composed edge: its length, its composition's construction and elevation cost per unit, and
        /// its elevation steps (from the lower elevation of each end node, as the game computes them).
        /// </summary>
        private static bool TryEdgePrice(EntityManager em, Entity edge, out float length, out uint unit, out uint elevation, out int steps)
        {
            length = 0;
            unit = 0;
            elevation = 0;
            steps = 0;
            if (!em.Exists(edge) || !em.HasComponent<Game.Net.Curve>(edge) || !em.HasComponent<Game.Net.Composition>(edge) || !em.HasComponent<Game.Net.Edge>(edge)) return false;
            Entity composition = em.GetComponentData<Game.Net.Composition>(edge).m_Edge;
            if (composition == Entity.Null || !em.Exists(composition) || !em.HasComponent<PlaceableNetComposition>(composition)) return false;
            PlaceableNetComposition price = em.GetComponentData<PlaceableNetComposition>(composition);
            length = em.GetComponentData<Game.Net.Curve>(edge).m_Length;
            unit = price.m_ConstructionCost;
            elevation = price.m_ElevationCost;
            Game.Net.Edge ends = em.GetComponentData<Game.Net.Edge>(edge);
            steps = ConstructionCosts.ElevationSteps(LowestElevation(em, ends.m_Start), LowestElevation(em, ends.m_End));
            return true;
        }

        private static float LowestElevation(EntityManager em, Entity node)
        {
            if (node == Entity.Null || !em.Exists(node) || !em.HasComponent<Game.Net.Elevation>(node)) return 0f;
            Unity.Mathematics.float2 e = em.GetComponentData<Game.Net.Elevation>(node).m_Elevation;
            return Math.Min(e.x, e.y);
        }

        /// <summary>
        /// Host, end of frame: prices the nets of the proposals applied this frame (or a few frames ago, while their new edges
        /// were not composed yet) and charges what the client paid less than the bound (<see cref="ConstructionCosts"/>):
        /// new edges by length and composition, minus the edges they replaced, plus composition upgrades.
        /// </summary>
        private void SettleNetCharges()
        {
            if (_netCharges.Count == 0) return;
            EntityManager em = _codec.EntityManager;
            for (int i = _netCharges.Count - 1; i >= 0; i--)
            {
                NetCharge c = _netCharges[i];
                c.Frames++;
                if (c.Frames < NetChargeFrames && !Composed(em, c)) continue;
                _netCharges.RemoveAt(i);
                long created = 0, upgrades = 0;
                foreach (Entity edge in c.Created)
                {
                    if (TryEdgePrice(em, edge, out float length, out uint unit, out _, out _)) created += ConstructionCosts.CreatedEdgeFloor(length, unit);
                }
                foreach ((Entity edge, uint oldUnit, uint oldElevation) in c.Upgraded)
                {
                    if (TryEdgePrice(em, edge, out float length, out uint unit, out uint elevation, out int steps)) upgrades += ConstructionCosts.UpgradeFloor(length, steps, oldUnit, oldElevation, unit, elevation);
                }
                long netFloor = ConstructionCosts.NetFloor(created, c.RemovedCeilings, upgrades);
                if (netFloor <= 0) continue;
                long cost = ProposalRules.EffectiveCost(c.ClientCost, c.ObjectFloor + netFloor, c.MaxRefund);
                long extra = cost - c.Charged;
                if (extra <= 0) continue;
                ChargeCity(extra);
                Perf.Counters.Count("net costs corrected by the host");
                _log.Info(LogCategory.Replication, c.Peer + ": proposal cost " + c.ClientCost + " corrected to " + cost + " (nets at least " + netFloor + ", objects at least " + c.ObjectFloor
                                                   + ", refund at most " + c.MaxRefund + ")");
            }
        }

        private static bool Composed(EntityManager em, NetCharge c)
        {
            foreach (Entity edge in c.Created)
            {
                if (em.Exists(edge) && !em.HasComponent<Game.Net.Composition>(edge)) return false;
            }
            return true;
        }

        private void ChargeCity(long cost)
        {
            EntityManager em = _codec.EntityManager;
            Entity city = _city.City;
            if (city == Entity.Null || !em.HasComponent<Game.City.PlayerMoney>(city)) return;
            Game.City.PlayerMoney money = em.GetComponentData<Game.City.PlayerMoney>(city);
            int amount = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, cost));
            if (amount > 0) money.Subtract(amount);
            else money.Add(-amount);
            em.SetComponentData(city, money);
        }
    }
}
