using System;
using Colossal.Mathematics;
using Game.Areas;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Routes;

using Game.Tools;
using Game.Zones;
using SkylineTogether.Core.Session;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;
using NetElevation = Game.Net.Elevation;
using NetNode = Game.Net.Node;
using ObjectElevation = Game.Objects.Elevation;
using ObjectTransform = Game.Objects.Transform;
using RoutePosition = Game.Routes.Position;
using WaterSourceData = Game.Simulation.WaterSourceData;

namespace SkylineTogether.Presence
{
    /// <summary>
    /// Reads the local player's live tool preview from the game's own tool output: the vanilla tools
    /// (and mod tools built the same way) turn the player's input into <see cref="Temp"/> entities — the road being
    /// drawn, the building at the cursor, the district outline, the zone cells being painted, the line being edited,
    /// the things the bulldozer would remove — so no tool logic is repeated here. Each <see cref="Capture"/> reduces them
    /// to <see cref="ToolPreview"/> shapes, most important first (the codec drops the tail when a message is full):
    /// net edges, objects, areas, zone blocks, lines and water sources, then net nodes, then sub-elements of the preview
    /// itself (a building's own roads, lots and sub-buildings). Main thread; reads through the entity manager (completing the
    /// writers of the few types read), at the presence rate only and only while a tool is active. Never writes.
    /// <para>
    /// Every point keeps its real height (curve control points, node and object positions). Pieces above or below the
    /// ground are flagged so the receivers can draw them in 3D: an edge by its composition (<c>Elevated</c> for elevated
    /// roads and bridges, <c>Tunnel</c>) and otherwise, like a node, by its net <c>Elevation</c> (above
    /// <see cref="ElevationThreshold"/>: elevated, below its negative: underground); an object by its object
    /// <c>Elevation</c> (placed at an elevation, stacked).
    /// </para>
    /// </summary>
    internal sealed class ToolPreviewCapture : IDisposable
    {
        /// <summary>Game types the capture reads (probed at startup, P-PREVIEW).</summary>
        public static readonly string[] TypeNames =
        {
            "Game.Tools.Temp", "Game.Tools.Error", "Game.Net.Edge", "Game.Net.Curve", "Game.Net.Node", "Game.Net.Composition",
            "Game.Prefabs.NetGeometryData", "Game.Prefabs.NetCompositionData", "Game.Objects.Object", "Game.Objects.Transform",
            "Game.Prefabs.ObjectGeometryData", "Game.Prefabs.BuildingData", "Game.Areas.Area", "Game.Areas.Node", "Game.Zones.Block",
            "Game.Zones.Cell", "Game.Routes.Route", "Game.Routes.RouteWaypoint", "Game.Routes.RouteSegment", "Game.Routes.Position",
            "Game.Routes.CurveElement", "Game.Simulation.WaterSourceData", "Game.Common.Owner", "Game.Net.Elevation", "Game.Objects.Elevation",
            "Game.Simulation.TerrainSystem",
        };

        /// <summary>Metres above (or below) the ground from which a piece counts as elevated (or underground).</summary>
        public const float ElevationThreshold = 1f;

        private const TempFlags Shown = TempFlags.Create | TempFlags.Delete | TempFlags.Modify | TempFlags.Replace | TempFlags.Upgrade
                                        | TempFlags.Dragging | TempFlags.Duplicate | TempFlags.Select;

        private const float DefaultNetWidth = 2f;
        private const int RoutePointsPerSegment = 32;

        private readonly EntityManager _em;
        private readonly EntityQuery _edges, _nodes, _objects, _areas, _blocks, _routes, _water;

        public ToolPreviewCapture(EntityManager em)
        {
            _em = em;
            ComponentType temp = ComponentType.ReadOnly<Temp>();
            ComponentType deleted = ComponentType.ReadOnly<Deleted>();
            _edges = Query(em, deleted, temp, ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<PrefabRef>());
            _nodes = Query(em, deleted, temp, ComponentType.ReadOnly<NetNode>(), ComponentType.ReadOnly<PrefabRef>());
            _objects = Query(em, deleted, temp, ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<ObjectTransform>(), ComponentType.ReadOnly<PrefabRef>());
            _areas = Query(em, deleted, temp, ComponentType.ReadOnly<Area>(), ComponentType.ReadOnly<AreaNode>());
            _blocks = Query(em, deleted, temp, ComponentType.ReadOnly<Block>(), ComponentType.ReadOnly<Cell>());
            _routes = Query(em, deleted, temp, ComponentType.ReadOnly<Route>(), ComponentType.ReadOnly<RouteWaypoint>());
            _water = Query(em, deleted, temp, ComponentType.ReadOnly<WaterSourceData>(), ComponentType.ReadOnly<ObjectTransform>());
        }

        public void Dispose()
        {
            foreach (EntityQuery q in new[] { _edges, _nodes, _objects, _areas, _blocks, _routes, _water })
            {
                try
                {
                    q.Dispose();
                }
                catch (Exception)
                {
                    // the world is going away
                }
            }
        }

        /// <summary>Replaces <paramref name="into"/> with the local tool's current preview (empty when there is none).</summary>
        public void Capture(ToolPreview into)
        {
            into.Clear();
            var secondary = new NativeList<Entity>(16, Allocator.Temp);
            try
            {
                int edges = CaptureEdges(into, secondary);
                CaptureObjects(into, secondary);
                CaptureAreas(into, secondary);
                CaptureBlocks(into);
                CaptureRoutes(into);
                CaptureWater(into);
                CaptureNodes(into, edges == 0);
                for (int i = 0; i < secondary.Length; i++) AddSecondary(into, secondary[i]);
            }
            finally
            {
                secondary.Dispose();
            }
        }

        // ---------------------------------------------------------------- nets

        private int CaptureEdges(ToolPreview into, NativeList<Entity> secondary)
        {
            if (_edges.IsEmptyIgnoreFilter) return 0;
            int added = 0;
            NativeArray<Entity> entities = _edges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (into.Count >= ToolPreview.MaxShapes)
                    {
                        into.Truncated = true; // full: the rest would be dropped anyway
                        break;
                    }
                    Entity e = entities[i];
                    if (!Visible(e, out ToolPreviewFlags flags)) continue;
                    if (IsSubElement(e))
                    {
                        secondary.Add(in e);
                        continue;
                    }
                    if (AddEdge(into, e, flags)) added++;
                }
            }
            finally
            {
                entities.Dispose();
            }
            return added;
        }

        private bool AddEdge(ToolPreview into, Entity e, ToolPreviewFlags flags)
        {
            Bezier4x3 b = _em.GetComponentData<Curve>(e).m_Bezier;
            if (!Finite(b.a) || !Finite(b.b) || !Finite(b.c) || !Finite(b.d)) return false;
            return into.AddCurve(flags | EdgeHeight(e), P(b.a), P(b.b), P(b.c), P(b.d), EdgeWidth(e));
        }

        /// <summary>Elevated or underground by the edge's composition (bridges and elevated roads, tunnels), else by its elevation.</summary>
        private ToolPreviewFlags EdgeHeight(Entity e)
        {
            if (_em.HasComponent<Composition>(e))
            {
                Entity composition = _em.GetComponentData<Composition>(e).m_Edge;
                if (composition != Entity.Null && _em.HasComponent<NetCompositionData>(composition))
                {
                    CompositionFlags.General general = _em.GetComponentData<NetCompositionData>(composition).m_Flags.m_General;
                    if ((general & CompositionFlags.General.Tunnel) != 0) return ToolPreviewFlags.Underground;
                    if ((general & CompositionFlags.General.Elevated) != 0) return ToolPreviewFlags.Elevated;
                }
            }
            return NetHeight(e);
        }

        /// <summary>Elevated or underground by the net elevation of an edge or node (both sides; ramps count by their higher side).</summary>
        private ToolPreviewFlags NetHeight(Entity e)
        {
            if (!_em.HasComponent<NetElevation>(e)) return ToolPreviewFlags.None; // on the ground
            float2 elevation = _em.GetComponentData<NetElevation>(e).m_Elevation;
            if (!math.all(math.isfinite(elevation))) return ToolPreviewFlags.None;
            float high = math.cmax(elevation);
            if (high > ElevationThreshold) return ToolPreviewFlags.Elevated;
            return high < -ElevationThreshold ? ToolPreviewFlags.Underground : ToolPreviewFlags.None;
        }

        private float EdgeWidth(Entity e)
        {
            if (_em.HasComponent<Composition>(e))
            {
                Entity composition = _em.GetComponentData<Composition>(e).m_Edge;
                if (composition != Entity.Null && _em.HasComponent<NetCompositionData>(composition))
                {
                    float w = _em.GetComponentData<NetCompositionData>(composition).m_Width;
                    if (w > 0.1f && w < 1000f) return w;
                }
            }
            return NetWidth(_em.GetComponentData<PrefabRef>(e).m_Prefab);
        }

        private float NetWidth(Entity prefab)
        {
            if (prefab != Entity.Null && _em.HasComponent<NetGeometryData>(prefab))
            {
                float w = _em.GetComponentData<NetGeometryData>(prefab).m_DefaultWidth;
                if (w > 0.1f && w < 1000f) return w;
            }
            return DefaultNetWidth; // power lines, pipes without geometry
        }

        private void CaptureNodes(ToolPreview into, bool primary)
        {
            if (_nodes.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> entities = _nodes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (into.Count >= ToolPreview.MaxShapes)
                    {
                        into.Truncated = true; // full: the rest would be dropped anyway
                        break;
                    }
                    Entity e = entities[i];
                    if (!Visible(e, out ToolPreviewFlags flags) || IsSubElement(e)) continue;
                    float3 p = _em.GetComponentData<NetNode>(e).m_Position;
                    if (!Finite(p)) continue;
                    if (!primary) flags |= ToolPreviewFlags.Secondary;
                    if (!into.AddCircle(flags | NetHeight(e), P(p), NetWidth(_em.GetComponentData<PrefabRef>(e).m_Prefab))) return;
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        // ---------------------------------------------------------------- objects

        private void CaptureObjects(ToolPreview into, NativeList<Entity> secondary)
        {
            if (_objects.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> entities = _objects.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (into.Count >= ToolPreview.MaxShapes)
                    {
                        into.Truncated = true; // full: the rest would be dropped anyway
                        break;
                    }
                    Entity e = entities[i];
                    if (!Visible(e, out ToolPreviewFlags flags)) continue;
                    if (IsSubElement(e))
                    {
                        secondary.Add(in e);
                        continue;
                    }
                    AddObject(into, e, flags);
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private bool AddObject(ToolPreview into, Entity e, ToolPreviewFlags flags)
        {
            ObjectTransform t = _em.GetComponentData<ObjectTransform>(e);
            if (!Finite(t.m_Position) || !math.all(math.isfinite(t.m_Rotation.value))) return false;
            Entity prefab = _em.GetComponentData<PrefabRef>(e).m_Prefab;
            flags |= ObjectHeight(e);
            float3 forward = math.mul(t.m_Rotation, new float3(0f, 0f, 1f));
            float yaw = math.atan2(forward.x, forward.z);
            if (prefab != Entity.Null && _em.HasComponent<BuildingData>(prefab))
            {
                int2 lot = _em.GetComponentData<BuildingData>(prefab).m_LotSize;
                if (lot.x > 0 && lot.y > 0) return into.AddRect(flags, P(t.m_Position), lot.x * 8f, lot.y * 8f, yaw);
            }
            if (prefab != Entity.Null && _em.HasComponent<ObjectGeometryData>(prefab))
            {
                ObjectGeometryData g = _em.GetComponentData<ObjectGeometryData>(prefab);
                float3 size = g.m_Bounds.max - g.m_Bounds.min;
                if (Finite(size) && size.x > 0f && size.z > 0f)
                {
                    float3 center = t.m_Position + math.mul(t.m_Rotation, MathUtils.Center(g.m_Bounds));
                    center.y = t.m_Position.y;
                    if ((g.m_Flags & Game.Objects.GeometryFlags.Circular) != 0) return into.AddCircle(flags, P(center), math.max(size.x, size.z));
                    return into.AddRect(flags, P(center), size.x, size.z, yaw);
                }
            }
            return into.AddCircle(flags, P(t.m_Position), 4f);
        }

        /// <summary>Elevated or underground by the object's elevation above the ground (placed at an elevation, stacked).</summary>
        private ToolPreviewFlags ObjectHeight(Entity e)
        {
            if (!_em.HasComponent<ObjectElevation>(e)) return ToolPreviewFlags.None;
            float elevation = _em.GetComponentData<ObjectElevation>(e).m_Elevation;
            if (elevation > ElevationThreshold) return ToolPreviewFlags.Elevated;
            return elevation < -ElevationThreshold ? ToolPreviewFlags.Underground : ToolPreviewFlags.None; // NaN: neither
        }

        // ---------------------------------------------------------------- areas, zones, lines, water

        private void CaptureAreas(ToolPreview into, NativeList<Entity> secondary)
        {
            if (_areas.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> entities = _areas.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (into.Count >= ToolPreview.MaxShapes)
                    {
                        into.Truncated = true; // full: the rest would be dropped anyway
                        break;
                    }
                    Entity e = entities[i];
                    if (!Visible(e, out ToolPreviewFlags flags)) continue;
                    if (IsSubElement(e))
                    {
                        secondary.Add(in e);
                        continue;
                    }
                    AddArea(into, e, flags);
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private bool AddArea(ToolPreview into, Entity e, ToolPreviewFlags flags)
        {
            DynamicBuffer<AreaNode> nodes = _em.GetBuffer<AreaNode>(e, true);
            int n = nodes.Length;
            if (n < 2) return false;
            if ((_em.GetComponentData<Area>(e).m_Flags & AreaFlags.Complete) != 0 && n >= 3) flags |= ToolPreviewFlags.Closed;
            if (!into.BeginPolyline(flags, 0f)) return false;
            int max = ToolPreview.MaxPolylinePoints;
            for (int k = 0; k < Math.Min(n, max); k++)
            {
                int index = n <= max ? k : (int)((long)k * (n - 1) / (max - 1)); // evenly thinned, first and last kept
                float3 p = nodes[index].m_Position;
                if (Finite(p)) into.AddPolylinePoint(P(p));
            }
            return into.EndPolyline();
        }

        private void CaptureBlocks(ToolPreview into)
        {
            if (_blocks.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> entities = _blocks.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (into.Count >= ToolPreview.MaxShapes)
                    {
                        into.Truncated = true; // full: the rest would be dropped anyway
                        break;
                    }
                    Entity e = entities[i];
                    if (!_em.HasComponent<Temp>(e) || (_em.GetComponentData<Temp>(e).m_Flags & TempFlags.Hidden) != 0) continue;
                    Block block = _em.GetComponentData<Block>(e);
                    int cells = block.m_Size.x * block.m_Size.y;
                    if (block.m_Size.x < 1 || block.m_Size.y < 1 || block.m_Size.x > 16 || block.m_Size.y > 16 || cells > ToolPreview.MaxZoneCells) continue;
                    if (!Finite(block.m_Position) || !math.all(math.isfinite(block.m_Direction))) continue;
                    DynamicBuffer<Cell> buffer = _em.GetBuffer<Cell>(e, true);
                    ulong bits = 0;
                    for (int c = 0; c < cells && c < buffer.Length; c++)
                    {
                        if ((buffer[c].m_State & CellFlags.Selected) != 0) bits |= 1UL << c; // cells the zone tool paints (GenerateZonesSystem)
                    }
                    if (bits == 0) continue;
                    float yaw = math.atan2(block.m_Direction.x, block.m_Direction.y);
                    if (!into.AddZoneBlock(ToolPreviewFlags.None, P(block.m_Position), yaw, block.m_Size.x, block.m_Size.y, bits)) return;
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void CaptureRoutes(ToolPreview into)
        {
            if (_routes.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> entities = _routes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (into.Count >= ToolPreview.MaxShapes)
                    {
                        into.Truncated = true; // full: the rest would be dropped anyway
                        break;
                    }
                    Entity e = entities[i];
                    if (!Visible(e, out ToolPreviewFlags flags)) continue;
                    AddRoute(into, e, flags);
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void AddRoute(ToolPreview into, Entity route, ToolPreviewFlags flags)
        {
            DynamicBuffer<RouteWaypoint> waypoints = _em.GetBuffer<RouteWaypoint>(route, true);
            int count = waypoints.Length;
            bool hasSegments = _em.HasBuffer<RouteSegment>(route);
            for (int i = 0; i < count; i++)
            {
                if (!TryWaypoint(waypoints[i].m_Waypoint, out float3 from)) continue;
                into.AddCircle(flags, P(from), 6f);
                int next = i + 1 < count ? i + 1 : ((_em.GetComponentData<Route>(route).m_Flags & RouteFlags.Complete) != 0 && count > 2 ? 0 : -1);
                if (next < 0 || !TryWaypoint(waypoints[next].m_Waypoint, out float3 to)) continue;
                Entity segment = Entity.Null;
                if (hasSegments)
                {
                    DynamicBuffer<RouteSegment> segments = _em.GetBuffer<RouteSegment>(route, true);
                    if (i < segments.Length) segment = segments[i].m_Segment;
                }
                if (!AddRouteSegment(into, segment, flags, from, to)) return;
            }
        }

        /// <summary>The path of one line segment: its curve elements (the way the vehicles drive) thinned to a polyline, or the straight leg.</summary>
        private bool AddRouteSegment(ToolPreview into, Entity segment, ToolPreviewFlags flags, float3 from, float3 to)
        {
            if (!into.BeginPolyline(flags, 0f)) return false;
            int curves = segment != Entity.Null && _em.Exists(segment) && _em.HasBuffer<CurveElement>(segment) ? _em.GetBuffer<CurveElement>(segment, true).Length : 0;
            if (curves == 0)
            {
                into.AddPolylinePoint(P(from));
                into.AddPolylinePoint(P(to));
                return into.EndPolyline() || !into.Truncated;
            }
            DynamicBuffer<CurveElement> elements = _em.GetBuffer<CurveElement>(segment, true);
            int points = Math.Min(curves + 1, RoutePointsPerSegment);
            for (int k = 0; k < points; k++)
            {
                int index = (int)((long)k * curves / (points - 1)); // 0 ... curves, evenly thinned
                float3 p = index < curves ? elements[index].m_Curve.a : elements[curves - 1].m_Curve.d;
                if (Finite(p)) into.AddPolylinePoint(P(p));
            }
            return into.EndPolyline() || !into.Truncated;
        }

        private bool TryWaypoint(Entity waypoint, out float3 position)
        {
            position = default;
            if (waypoint == Entity.Null || !_em.Exists(waypoint) || !_em.HasComponent<RoutePosition>(waypoint)) return false;
            position = _em.GetComponentData<RoutePosition>(waypoint).m_Position;
            return Finite(position);
        }

        private void CaptureWater(ToolPreview into)
        {
            if (_water.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> entities = _water.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (into.Count >= ToolPreview.MaxShapes)
                    {
                        into.Truncated = true; // full: the rest would be dropped anyway
                        break;
                    }
                    Entity e = entities[i];
                    if (!Visible(e, out ToolPreviewFlags flags)) continue;
                    float3 p = _em.GetComponentData<ObjectTransform>(e).m_Position;
                    float radius = _em.GetComponentData<WaterSourceData>(e).m_Radius;
                    if (!Finite(p) || !(radius > 0f) || radius > 5000f) continue;
                    if (!into.AddCircle(flags, P(p), radius * 2f)) return;
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        // ---------------------------------------------------------------- sub-elements

        private void AddSecondary(ToolPreview into, Entity e)
        {
            if (!_em.Exists(e) || !Visible(e, out ToolPreviewFlags flags)) return;
            flags |= ToolPreviewFlags.Secondary;
            if (_em.HasComponent<Edge>(e) && _em.HasComponent<Curve>(e)) AddEdge(into, e, flags);
            else if (_em.HasComponent<ObjectTransform>(e) && _em.HasComponent<PrefabRef>(e) && IsBuilding(e)) AddObject(into, e, flags); // a building's props and trees would only add noise
            else if (_em.HasBuffer<AreaNode>(e) && _em.HasComponent<Area>(e)) AddArea(into, e, flags);
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// True for a preview element the player should see: new things and things the tool changes or removes; not the
        /// hidden originals and not the untouched neighbours the game regenerates around an edit.
        /// </summary>
        private bool Visible(Entity e, out ToolPreviewFlags flags)
        {
            flags = ToolPreviewFlags.None;
            Temp temp = _em.GetComponentData<Temp>(e);
            if ((temp.m_Flags & TempFlags.Hidden) != 0) return false;
            if (temp.m_Original != Entity.Null && (temp.m_Flags & Shown) == 0) return false;
            if ((temp.m_Flags & TempFlags.Delete) != 0) flags |= ToolPreviewFlags.Delete;
            if (_em.HasComponent<Error>(e)) flags |= ToolPreviewFlags.Error;
            return true;
        }

        /// <summary>True when <paramref name="e"/> belongs to another preview element (a building's own roads, props, lots).</summary>
        private bool IsSubElement(Entity e)
        {
            if (!_em.HasComponent<Owner>(e)) return false;
            Entity owner = _em.GetComponentData<Owner>(e).m_Owner;
            return owner != Entity.Null && _em.Exists(owner) && _em.HasComponent<Temp>(owner);
        }

        private bool IsBuilding(Entity e)
        {
            Entity prefab = _em.GetComponentData<PrefabRef>(e).m_Prefab;
            return prefab != Entity.Null && _em.HasComponent<BuildingData>(prefab);
        }

        private static EntityQuery Query(EntityManager em, ComponentType deleted, params ComponentType[] all)
        {
            return em.CreateEntityQuery(new EntityQueryDesc { All = all, None = new[] { deleted } });
        }

        private static bool Finite(float3 v) => math.all(math.isfinite(v));

        private static PreviewPoint P(float3 v) => new PreviewPoint(v.x, v.y, v.z);
    }
}
