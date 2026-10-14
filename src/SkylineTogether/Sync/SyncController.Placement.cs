using System;
using System.Collections.Generic;
using Game.Prefabs;
using SkylineTogether.Core;
using SkylineTogether.Core.Sync;
using SkylineTogether.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace SkylineTogether.Sync
{
    // Host placement checks of structure proposals: unlocks, owned map tiles, cheap geometry limits.
    public sealed partial class SyncController
    {
        // ================================================================== placement checks (host validation)

        private readonly PlacementContext _placement = new PlacementContext();
        private readonly PlacementCandidate _candidate = new PlacementCandidate();
        private readonly Dictionary<Entity, PrefabFacts> _prefabFacts = new Dictionary<Entity, PrefabFacts>();
        private readonly Dictionary<uint, PlacementPoint> _proposedNodes = new Dictionary<uint, PlacementPoint>();
        private readonly List<int> _rawRefs = new List<int>(4);
        private readonly List<Game.Areas.Node> _areaNodes = new List<Game.Areas.Node>();
        private readonly List<PlacementPoint> _tilePolygon = new List<PlacementPoint>();
        private EntityQuery _allTiles, _unownedTiles;
        private int _tilesCached = -1, _unownedCached = -1;

        /// <summary>The host runs at least one code mod, so a client's direct-write tool proposal is plausible.</summary>
        private bool _directToolsPossible;

        /// <summary>Placement checks the host's mods relax (<see cref="Compat.ModCompat.RelaxedPlacementChecks"/>).</summary>
        private PlacementChecks _relaxedChecks;

        /// <summary>Host session start: which mods relax the placement checks (logged once per session).</summary>
        private void ResetPlacement()
        {
            _tilesCached = -1;
            _unownedCached = -1;
            _placement.Tiles.Clear();
            var by = new List<string>();
            try
            {
                _directToolsPossible = Compat.ModCompat.LoadedMods().Count > 0;
                _relaxedChecks = Compat.ModCompat.RelaxedPlacementChecks(by);
            }
            catch (Exception ex)
            {
                // unknown mods: never refuse what a mod might legitimately allow
                _directToolsPossible = true;
                _relaxedChecks = PlacementChecks.Tiles | PlacementChecks.Geometry | PlacementChecks.Unlocks;
                _log.Warn(LogCategory.Replication, "placement checks: reading the loaded mods failed; unlock, tile and geometry checks are off", ex);
                return;
            }
            PlacementChecks active = PlacementChecks.All & ~_relaxedChecks;
            _log.Info(LogCategory.Replication, "placement checks of client proposals: " + active + (by.Count > 0 ? " (relaxed by " + string.Join(", ", by) + ")" : "")
                                               + (_directToolsPossible ? "; direct-write mod tools skip them" : "; no code mods, so no client tool may write directly"));
        }

        /// <summary>
        /// ARCHITECTURE.md#host-validation-and-commit, placement: every spawn and edit of an object, node, edge, area or line is
        /// read from the record without applying it and judged by Core's <see cref="PlacementValidator"/> against the host's
        /// world (prefab unlocks and classes, owned map tiles, map bounds, edge and area shapes); painted zone types must be
        /// unlocked. Returns the first refusal.
        /// </summary>
        private RejectReason CheckPlacement(PeerId peer, List<SyncRecord> records, in ProposalFacts facts, byte[] data, HashSet<uint> spawned)
        {
            if (facts.Spawns + facts.Upserts + facts.ZoneBlocks == 0) return RejectReason.None;
            PlacementChecks checks = PlacementPolicy.For(facts.Flags, _directToolsPossible, _relaxedChecks);
            _placement.Checks = checks;
            EntityManager em = _codec.EntityManager;
            _codec.BeginPeek(data, 0, data.Length);
            _prefabFacts.Clear();
            _proposedNodes.Clear();
            _placement.TouchedPrefabs.Clear();
            // a direct-write tool's proposal (Move It) only gets the class check: nothing else is read from its records
            bool shapes = (checks & (PlacementChecks.Tiles | PlacementChecks.Geometry)) != 0;
            bool unlocks = (checks & PlacementChecks.Unlocks) != 0;
            if (shapes) RefreshPlacementWorld(em);
            // the prefabs of existing entities the proposal edits or deletes (splits keep their net's prefab) and the
            // positions of the nodes it proposes (new edges end on them)
            foreach (SyncRecord r in records)
            {
                if (!shapes && !unlocks) break;
                if (r.Kind != SyncRecordKind.Spawn && r.Kind != SyncRecordKind.Upsert && r.Kind != SyncRecordKind.Despawn) continue;
                if (unlocks && r.Kind != SyncRecordKind.Spawn && !spawned.Contains(r.NetId))
                {
                    Entity existing = _ids.GetEntity(r.NetId);
                    if (existing != Entity.Null && em.Exists(existing) && em.HasComponent<PrefabRef>(existing))
                    {
                        Entity prefab = em.GetComponentData<PrefabRef>(existing).m_Prefab;
                        if (prefab != Entity.Null) _placement.TouchedPrefabs.Add(PrefabKey(prefab));
                    }
                }
                if (shapes && r.Kind != SyncRecordKind.Despawn && _codec.TryPeek(r, out Game.Net.Node node)) _proposedNodes[r.NetId] = Point(node.m_Position);
            }
            foreach (SyncRecord r in records)
            {
                if (r.Kind != SyncRecordKind.Spawn && r.Kind != SyncRecordKind.Upsert) continue;
                bool created = r.Kind == SyncRecordKind.Spawn;
                if (!created && spawned.Contains(r.NetId)) continue; // part of the spawn
                Entity existing = created ? Entity.Null : _ids.GetEntity(r.NetId);
                if (!created && (existing == Entity.Null || !em.Exists(existing))) continue; // refused as a conflict before
                Family family = created ? r.Family : _ids.GetFamily(r.NetId);
                CandidateShape shape = ShapeOf(family);
                if (shape == CandidateShape.Other) continue;
                FillCandidate(em, r, created, existing, shape, shapes, unlocks);
                RejectReason reason = PlacementValidator.Check(_candidate, _placement, out string detail);
                if (reason == RejectReason.None) continue;
                Perf.Counters.Count("proposals refused by placement checks");
                _log.Info(LogCategory.Replication, peer + ": placement check refused #" + r.NetId + " (" + reason + "): " + detail);
                return reason;
            }
            if ((checks & PlacementChecks.Unlocks) != 0)
            {
                RejectReason zones = CheckZoneUnlocks(peer, em, records);
                if (zones != RejectReason.None) return zones;
            }
            return RejectReason.None;
        }

        private static CandidateShape ShapeOf(Family family)
        {
            switch (family)
            {
                case Family.Object: return CandidateShape.Object;
                case Family.NetNode: return CandidateShape.Node;
                case Family.NetEdge: return CandidateShape.Edge;
                case Family.Area: return CandidateShape.Area;
                case Family.Route: return CandidateShape.Route;
                default: return CandidateShape.Other;
            }
        }

        /// <summary>
        /// Fills <see cref="_candidate"/> from the record and the host's current entity: the prefabs always, ownership when
        /// any placement check applies, positions, curves and outlines only for the tile and geometry checks.
        /// </summary>
        private void FillCandidate(EntityManager em, SyncRecord r, bool created, Entity existing, CandidateShape shape, bool shapes, bool unlocks)
        {
            PlacementCandidate c = _candidate;
            c.Reset();
            c.NetId = r.NetId;
            c.Shape = shape;
            c.Created = created;
            Entity prefab = _codec.PeekPrefab(r);
            if (prefab == Entity.Null && !created && em.HasComponent<PrefabRef>(existing)) prefab = em.GetComponentData<PrefabRef>(existing).m_Prefab; // the record keeps it
            c.Prefab = FactsOf(em, prefab);
            if (!created && em.HasComponent<PrefabRef>(existing)) c.OldPrefab = FactsOf(em, em.GetComponentData<PrefabRef>(existing).m_Prefab);
            if (!shapes && !unlocks) return;
            if (created)
            {
                // a spawn is owned when its record names an owner (a building's sub-net, a service upgrade, a lot)
                c.Owned = _codec.TryPeekRefs<Game.Common.Owner>(r, _rawRefs) && _rawRefs.Count > 0 && _rawRefs[0] != 0 && _rawRefs[0] != -1;
            }
            else
            {
                // an edit is judged by what the host has (a record cannot make an entity owned to escape the checks)
                c.Owned = em.HasComponent<Game.Common.Owner>(existing) && em.GetComponentData<Game.Common.Owner>(existing).m_Owner != Entity.Null;
            }
            if (!shapes) return;
            switch (shape)
            {
                case CandidateShape.Object:
                    if (_codec.TryPeek(r, out Game.Objects.Transform transform))
                    {
                        c.HasPosition = true;
                        c.Position = Point(transform.m_Position);
                    }
                    if (!created && em.HasComponent<Game.Objects.Transform>(existing))
                    {
                        c.HasOldPosition = true;
                        c.OldPosition = Point(em.GetComponentData<Game.Objects.Transform>(existing).m_Position);
                    }
                    break;
                case CandidateShape.Node:
                    if (_proposedNodes.TryGetValue(r.NetId, out PlacementPoint position))
                    {
                        c.HasPosition = true;
                        c.Position = position;
                    }
                    if (!created && em.HasComponent<Game.Net.Node>(existing))
                    {
                        c.HasOldPosition = true;
                        c.OldPosition = Point(em.GetComponentData<Game.Net.Node>(existing).m_Position);
                    }
                    break;
                case CandidateShape.Edge:
                    FillEdge(em, r, created, existing);
                    break;
                case CandidateShape.Area:
                    if (_codec.TryPeekBuffer(r, _areaNodes))
                    {
                        foreach (Game.Areas.Node n in _areaNodes) c.Polygon.Add(Point(n.m_Position));
                    }
                    if (_codec.TryPeek(r, out Game.Areas.Area area)) c.Complete = (area.m_Flags & Game.Areas.AreaFlags.Complete) != 0;
                    else if (!created && em.HasComponent<Game.Areas.Area>(existing)) c.Complete = (em.GetComponentData<Game.Areas.Area>(existing).m_Flags & Game.Areas.AreaFlags.Complete) != 0;
                    if (!created && em.HasBuffer<Game.Areas.Node>(existing))
                    {
                        DynamicBuffer<Game.Areas.Node> old = em.GetBuffer<Game.Areas.Node>(existing, true);
                        for (int i = 0; i < old.Length; i++) c.OldPolygon.Add(Point(old[i].m_Position));
                    }
                    break;
            }
        }

        /// <summary>An edge's nodes (by the ids its record names), its curve ends and where its nodes are.</summary>
        private void FillEdge(EntityManager em, SyncRecord r, bool created, Entity existing)
        {
            PlacementCandidate c = _candidate;
            if (_codec.TryPeekRefs<Game.Net.Edge>(r, _rawRefs) && _rawRefs.Count >= 2)
            {
                c.StartNode = _rawRefs[0] > 0 ? (uint)_rawRefs[0] : 0;
                c.EndNode = _rawRefs[1] > 0 ? (uint)_rawRefs[1] : 0;
            }
            if (!_codec.TryPeek(r, out Game.Net.Curve curve)) return;
            PlacementPoint a = Point(curve.m_Bezier.a), d = Point(curve.m_Bezier.d);
            if (!created && em.HasComponent<Game.Net.Curve>(existing))
            {
                // an edit is judged only when it moved the edge (an upgrade keeps its curve)
                Colossal.Mathematics.Bezier4x3 old = em.GetComponentData<Game.Net.Curve>(existing).m_Bezier;
                if (a.DistanceXZ(Point(old.a)) <= PlacementValidator.MoveThreshold && d.DistanceXZ(Point(old.d)) <= PlacementValidator.MoveThreshold) return;
            }
            c.HasCurve = true;
            c.CurveStart = a;
            c.CurveEnd = d;
            if (c.StartNode != 0 && c.EndNode != 0 && TryNodePosition(em, c.StartNode, out PlacementPoint start) && TryNodePosition(em, c.EndNode, out PlacementPoint end))
            {
                c.HasNodePositions = true;
                c.StartPosition = start;
                c.EndPosition = end;
            }
        }

        /// <summary>Where a node is: as the proposal puts it, otherwise on the host.</summary>
        private bool TryNodePosition(EntityManager em, uint id, out PlacementPoint position)
        {
            if (_proposedNodes.TryGetValue(id, out position)) return true;
            Entity node = _ids.GetEntity(id);
            if (node == Entity.Null || !em.Exists(node) || !em.HasComponent<Game.Net.Node>(node)) return false;
            position = Point(em.GetComponentData<Game.Net.Node>(node).m_Position);
            return true;
        }

        /// <summary>A prefab's facts on the host (cached per proposal).</summary>
        private PrefabFacts FactsOf(EntityManager em, Entity prefab)
        {
            if (prefab == Entity.Null || !em.Exists(prefab)) return default;
            if (_prefabFacts.TryGetValue(prefab, out PrefabFacts cached)) return cached;
            var f = new PrefabFacts { Known = true, Key = PrefabKey(prefab) };
            try
            {
                f.Locked = em.HasComponent<Locked>(prefab) && em.IsComponentEnabled<Locked>(prefab);
                if (em.HasComponent<NetData>(prefab)) f.Kind = PrefabKind.Net;
                else if (em.HasComponent<ObjectData>(prefab)) f.Kind = PrefabKind.Object;
                else if (em.HasComponent<AreaData>(prefab)) f.Kind = PrefabKind.Area;
                else if (em.HasComponent<RouteData>(prefab)) f.Kind = PrefabKind.Route;
                else if (em.HasComponent<ZoneData>(prefab)) f.Kind = PrefabKind.Zone;
                else f.Kind = PrefabKind.Other;
                // what the game never checks against map tiles: markers, outside connections, airspace paths, tiles
                if (em.HasComponent<ObjectGeometryData>(prefab) && (em.GetComponentData<ObjectGeometryData>(prefab).m_Flags & Game.Objects.GeometryFlags.Marker) != 0) f.TileExempt = true;
                if (em.HasComponent<OutsideConnectionData>(prefab)) f.TileExempt = true;
                if (em.HasComponent<TaxiwayData>(prefab) && (em.GetComponentData<TaxiwayData>(prefab).m_Flags & TaxiwayFlags.Airspace) != 0) f.TileExempt = true;
                if (em.HasComponent<AreaGeometryData>(prefab) && em.GetComponentData<AreaGeometryData>(prefab).m_Type == Game.Areas.AreaType.MapTile) f.TileExempt = true;
                if (em.HasComponent<NetGeometryData>(prefab)) f.MaxEdgeLength = Math.Max(0f, em.GetComponentData<NetGeometryData>(prefab).m_EdgeLengthRange.max);
            }
            catch (Exception ex)
            {
                // a prefab whose data cannot be read is judged by nothing
                _log.Debug(LogCategory.Replication, "placement facts of prefab " + prefab + ": " + ex.Message);
                f = default;
            }
            _prefabFacts[prefab] = f;
            return f;
        }

        private static long PrefabKey(Entity prefab) => ((long)prefab.Version << 32) | (uint)prefab.Index;

        private static PlacementPoint Point(float3 p) => new PlacementPoint(p.x, p.y, p.z);

        /// <summary>
        /// The map tiles (rebuilt when the number of tiles or of unowned tiles changed: a purchase removes the tile's
        /// <c>Native</c>), the playable area and the terrain's height range.
        /// </summary>
        private void RefreshPlacementWorld(EntityManager em)
        {
            try
            {
                if (_allTiles == default)
                {
                    _allTiles = em.CreateEntityQuery(ComponentType.ReadOnly<Game.Areas.MapTile>(), ComponentType.ReadOnly<Game.Areas.Node>(), ComponentType.Exclude<Game.Common.Deleted>(),
                                                     ComponentType.Exclude<Game.Tools.Temp>());
                    _unownedTiles = em.CreateEntityQuery(ComponentType.ReadOnly<Game.Areas.MapTile>(), ComponentType.ReadOnly<Game.Areas.Node>(), ComponentType.ReadOnly<Game.Common.Native>(),
                                                         ComponentType.Exclude<Game.Common.Deleted>(), ComponentType.Exclude<Game.Tools.Temp>());
                }
                int all = _allTiles.CalculateEntityCount(), unowned = _unownedTiles.CalculateEntityCount();
                if (all != _tilesCached || unowned != _unownedCached)
                {
                    _tilesCached = all;
                    _unownedCached = unowned;
                    _placement.Tiles.Clear();
                    NativeArray<Entity> tiles = _allTiles.ToEntityArray(Allocator.Temp);
                    try
                    {
                        foreach (Entity tile in tiles)
                        {
                            DynamicBuffer<Game.Areas.Node> nodes = em.GetBuffer<Game.Areas.Node>(tile, true);
                            _tilePolygon.Clear();
                            for (int i = 0; i < nodes.Length; i++) _tilePolygon.Add(Point(nodes[i].m_Position));
                            _placement.Tiles.Add(!em.HasComponent<Game.Common.Native>(tile), _tilePolygon);
                        }
                    }
                    finally
                    {
                        tiles.Dispose();
                    }
                    _log.Debug(LogCategory.Replication, "placement checks: " + _placement.Tiles.OwnedCount + " owned and " + _placement.Tiles.UnownedCount + " unowned map tiles");
                }
            }
            catch (Exception ex)
            {
                _placement.Tiles.Clear(); // no tiles: the tile check is skipped rather than guessed
                _tilesCached = -1;
                _log.Debug(LogCategory.Replication, "placement checks: reading the map tiles failed: " + ex.Message);
            }
            try
            {
                Game.Simulation.TerrainSystem terrain = _codec.World.GetExistingSystemManaged<Game.Simulation.TerrainSystem>();
                if (terrain == null)
                {
                    _placement.HasMapBounds = false;
                    _placement.HasHeightRange = false;
                    return;
                }
                float2 offset = terrain.playableOffset, size = terrain.playableArea;
                _placement.SetMapBounds(offset.x, offset.y, offset.x + size.x, offset.y + size.y);
                float2 height = terrain.heightScaleOffset; // x: height range, y: lowest height
                if (height.x > 0) _placement.SetHeightRange(height.y, height.y + height.x);
                else _placement.HasHeightRange = false;
            }
            catch (Exception)
            {
                _placement.HasMapBounds = false;
                _placement.HasHeightRange = false;
            }
        }

        /// <summary>
        /// Unlocks of painted zones: a zone type the host's progression has not unlocked may not appear in a block unless
        /// the host's block already has it (a block's record carries all of its cells).
        /// </summary>
        private RejectReason CheckZoneUnlocks(PeerId peer, EntityManager em, List<SyncRecord> records)
        {
            foreach (SyncRecord r in records)
            {
                if (r.Kind != SyncRecordKind.ZoneCells || r.ZoneIds == null) continue;
                Entity block = Entity.Null;
                bool blockRead = false;
                foreach (uint zoneId in r.ZoneIds)
                {
                    if (zoneId == 0) continue;
                    Entity prefab = _ids.GetEntity(zoneId);
                    if (prefab == Entity.Null || !em.Exists(prefab) || !em.HasComponent<Locked>(prefab) || !em.IsComponentEnabled<Locked>(prefab)) continue;
                    if (!blockRead)
                    {
                        block = FindBlock(r.Block);
                        blockRead = true;
                    }
                    if (BlockHasZone(em, block, prefab)) continue;
                    Perf.Counters.Count("proposals refused by placement checks");
                    _log.Info(LogCategory.Replication, peer + ": placement check refused the zone cells of block #" + r.Block.OwnerNetId + " (Locked): paints a locked zone type");
                    return RejectReason.Locked;
                }
            }
            return RejectReason.None;
        }

        private bool BlockHasZone(EntityManager em, Entity block, Entity zonePrefab)
        {
            if (block == Entity.Null || !em.Exists(block) || !em.HasBuffer<Game.Zones.Cell>(block)) return false;
            DynamicBuffer<Game.Zones.Cell> cells = em.GetBuffer<Game.Zones.Cell>(block, true);
            for (int i = 0; i < cells.Length; i++)
            {
                Game.Zones.ZoneType z = cells[i].m_Zone;
                if (!z.Equals(Game.Zones.ZoneType.None) && _zones.GetPrefab(z) == zonePrefab) return true;
            }
            return false;
        }
    }
}
