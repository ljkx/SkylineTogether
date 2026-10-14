using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// The placement checks of a structure proposal (ARCHITECTURE.md#host-validation-and-commit): what a vanilla tool
    /// could not have produced on the host's city. Each check can be switched off by a mod whose rules legitimately
    /// allow more (<see cref="PlacementPolicy"/>).
    /// </summary>
    [Flags]
    public enum PlacementChecks : byte
    {
        None = 0,
        /// <summary>New structures use only prefabs the host's progression has unlocked.</summary>
        Unlocks = 1,
        /// <summary>New and moved structures stay on map tiles the city owns.</summary>
        Tiles = 2,
        /// <summary>Cheap shape limits: inside the map, plausible height, edge length and end points, area polygons.</summary>
        Geometry = 4,
        All = Unlocks | Tiles | Geometry,
    }

    /// <summary>Which placement checks apply to one proposal.</summary>
    public static class PlacementPolicy
    {
        /// <summary>
        /// The checks for a proposal with <paramref name="flags"/>. <paramref name="relaxedByMods"/> are the checks a mod
        /// loaded on the host legitimately bypasses (Anarchy: tiles and geometry). A direct-write mod tool's proposal
        /// (Move It) gets none of them, but only when the host runs a code mod at all
        /// (<paramref name="directToolsPossible"/>): on a host without mods no client tool can write directly, so the flag
        /// is ignored instead of becoming a way around every check.
        /// </summary>
        public static PlacementChecks For(ProposalFlags flags, bool directToolsPossible, PlacementChecks relaxedByMods)
        {
            if ((flags & ProposalFlags.DirectTool) != 0 && directToolsPossible) return PlacementChecks.None;
            return PlacementChecks.All & ~relaxedByMods;
        }
    }

    /// <summary>What a prefab is, as far as the placement rules care (from the prefab's data components).</summary>
    public enum PrefabKind : byte
    {
        Unknown,
        Net,
        Object,
        Area,
        Route,
        Zone,
        Other,
    }

    /// <summary>A world position (metres; y is up).</summary>
    public readonly struct PlacementPoint
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        public PlacementPoint(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>The horizontal distance to <paramref name="other"/>.</summary>
        public float DistanceXZ(PlacementPoint other)
        {
            float dx = X - other.X, dz = Z - other.Z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        public override string ToString() => "(" + X.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ", " + Y.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                                             + ", " + Z.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>The facts of one prefab on the host.</summary>
    public struct PrefabFacts
    {
        /// <summary>The prefab exists on the host (false: the other facts are meaningless).</summary>
        public bool Known;
        /// <summary>Identifies the prefab within one proposal (the game layer's entity key).</summary>
        public long Key;
        /// <summary>The prefab's <c>Locked</c> component is enabled on the host (progression has not unlocked it).</summary>
        public bool Locked;
        public PrefabKind Kind;
        /// <summary>
        /// The game never checks this prefab against map tiles: marker objects, outside connections, airspace paths and
        /// map tiles themselves.
        /// </summary>
        public bool TileExempt;
        /// <summary>The longest edge the game builds of this net (<c>NetGeometryData.m_EdgeLengthRange.max</c>); 0: none.</summary>
        public float MaxEdgeLength;
    }

    /// <summary>The shape of a proposed entity, from its family.</summary>
    public enum CandidateShape : byte
    {
        Other,
        Object,
        Node,
        Edge,
        Area,
        Route,
    }

    /// <summary>
    /// One spawn or edit of a proposal, reduced to what the placement rules need. The game layer fills it from the
    /// record (decoded without applying it) and the host's current entity. Reused: call <see cref="Reset"/>.
    /// </summary>
    public sealed class PlacementCandidate
    {
        public uint NetId;
        public CandidateShape Shape;
        /// <summary>A spawn (false: an edit of an existing entity).</summary>
        public bool Created;
        /// <summary>The entity has an owner (a building's sub-net, a service upgrade, a lot): its owner was checked.</summary>
        public bool Owned;
        /// <summary>The prefab the record names.</summary>
        public PrefabFacts Prefab;
        /// <summary>Edits: the prefab the entity has on the host now.</summary>
        public PrefabFacts OldPrefab;

        /// <summary>Objects and nodes: the proposed position.</summary>
        public bool HasPosition;
        public PlacementPoint Position;
        /// <summary>Edits of objects and nodes: the position on the host now.</summary>
        public bool HasOldPosition;
        public PlacementPoint OldPosition;

        /// <summary>Edges: the net ids of the start and end node (0 when unknown).</summary>
        public uint StartNode, EndNode;
        /// <summary>Edges: the proposed curve's end points.</summary>
        public bool HasCurve;
        public PlacementPoint CurveStart, CurveEnd;
        /// <summary>Edges: where the start and end node are (proposed or on the host).</summary>
        public bool HasNodePositions;
        public PlacementPoint StartPosition, EndPosition;

        /// <summary>Areas: the proposed polygon (empty when the record carries none).</summary>
        public readonly List<PlacementPoint> Polygon = new List<PlacementPoint>();
        /// <summary>Edits of areas: the polygon on the host now.</summary>
        public readonly List<PlacementPoint> OldPolygon = new List<PlacementPoint>();
        /// <summary>Areas: the polygon is closed (<c>AreaFlags.Complete</c>).</summary>
        public bool Complete;

        public void Reset()
        {
            NetId = 0;
            Shape = CandidateShape.Other;
            Created = false;
            Owned = false;
            Prefab = default;
            OldPrefab = default;
            HasPosition = false;
            Position = default;
            HasOldPosition = false;
            OldPosition = default;
            StartNode = 0;
            EndNode = 0;
            HasCurve = false;
            CurveStart = default;
            CurveEnd = default;
            HasNodePositions = false;
            StartPosition = default;
            EndPosition = default;
            Polygon.Clear();
            OldPolygon.Clear();
            Complete = false;
        }
    }

    /// <summary>The host's side of the placement rules for one proposal.</summary>
    public sealed class PlacementContext
    {
        public PlacementChecks Checks = PlacementChecks.All;
        /// <summary>The city's map tiles (empty: tiles are not checked, as in the editor or a map without tiles).</summary>
        public readonly TileMap Tiles = new TileMap();
        /// <summary>The playable area (horizontal), when known.</summary>
        public bool HasMapBounds;
        public float MinX, MinZ, MaxX, MaxZ;
        /// <summary>The terrain's height range, when known.</summary>
        public bool HasHeightRange;
        public float MinY, MaxY;
        /// <summary>
        /// Prefabs of existing entities the proposal edits or deletes. A vanilla tool that splits or adjusts an existing
        /// net (a pre-placed highway the player connects to) creates new edges of that net's prefab, locked or not.
        /// </summary>
        public readonly HashSet<long> TouchedPrefabs = new HashSet<long>();

        public void SetMapBounds(float minX, float minZ, float maxX, float maxZ)
        {
            HasMapBounds = maxX > minX && maxZ > minZ;
            MinX = minX;
            MinZ = minZ;
            MaxX = maxX;
            MaxZ = maxZ;
        }

        public void SetHeightRange(float minY, float maxY)
        {
            HasHeightRange = maxY >= minY && !float.IsNaN(minY) && !float.IsNaN(maxY);
            MinY = minY;
            MaxY = maxY;
        }
    }

    /// <summary>
    /// The decision logic of the placement rules (ARCHITECTURE.md#host-validation-and-commit). Every limit is chosen so
    /// that a vanilla tool's result always passes: the game's own checks (<c>Game.Tools.ValidationSystem</c>) are stricter
    /// (they test whole footprints, compositions and collisions), and these only test points, chords and polygons with a
    /// margin. Pure: no world access, so every rule is unit tested.
    /// </summary>
    public static class PlacementValidator
    {
        /// <summary>A point counts as outside the owned tiles only when it is this far from every owned tile.</summary>
        public const float TileMargin = 2f;
        /// <summary>An edit moved an object, node or area node when it moved farther than this.</summary>
        public const float MoveThreshold = 0.5f;
        /// <summary>How far beyond the playable area a point may lie (the game allows 0.1 m around the terrain).</summary>
        public const float MapMargin = 1f;
        /// <summary>Below the lowest and above the highest terrain height a structure may reach (tunnels, elevated nets).</summary>
        public const float HeightSlack = 1024f;
        /// <summary>How far an edge's curve end may lie from its node (the game puts it exactly on the node).</summary>
        public const float EndpointTolerance = 1f;
        /// <summary>The game refuses edges longer than the net's longest edge times this (<c>LongDistance</c>).</summary>
        public const float EdgeLengthFactor = 1.1f;
        /// <summary>The game measures edges between the node footprints; the chord between the node centres may be longer.</summary>
        public const float EdgeNodeSlack = 64f;
        /// <summary>A closed area polygon with less area than this is degenerate.</summary>
        public const float MinPolygonArea = 0.01f;
        /// <summary>Polygons with more nodes are not tested for self-intersection (quadratic).</summary>
        public const int MaxPolygonNodesTested = 4096;

        /// <summary>
        /// The verdict on one candidate: <see cref="RejectReason.None"/>, <see cref="RejectReason.PrefabClass"/>,
        /// <see cref="RejectReason.Locked"/>, <see cref="RejectReason.Geometry"/> or <see cref="RejectReason.OutsideTiles"/>
        /// (in this order); <paramref name="detail"/> says why for the host's log.
        /// </summary>
        public static RejectReason Check(PlacementCandidate c, PlacementContext ctx, out string detail)
        {
            detail = null;
            if (c == null || ctx == null) return RejectReason.None;
            RejectReason r = CheckClass(c, out detail);
            if (r != RejectReason.None) return r;
            if ((ctx.Checks & PlacementChecks.Unlocks) != 0)
            {
                r = CheckUnlock(c, ctx, out detail);
                if (r != RejectReason.None) return r;
            }
            if ((ctx.Checks & PlacementChecks.Geometry) != 0)
            {
                r = CheckGeometry(c, ctx, out detail);
                if (r != RejectReason.None) return r;
            }
            if ((ctx.Checks & PlacementChecks.Tiles) != 0)
            {
                r = CheckTiles(c, ctx, out detail);
                if (r != RejectReason.None) return r;
            }
            return RejectReason.None;
        }

        /// <summary>The prefab kind a shape's entity must have (Unknown: not checked).</summary>
        public static PrefabKind ExpectedKind(CandidateShape shape)
        {
            switch (shape)
            {
                case CandidateShape.Object: return PrefabKind.Object;
                case CandidateShape.Node:
                case CandidateShape.Edge: return PrefabKind.Net;
                case CandidateShape.Area: return PrefabKind.Area;
                case CandidateShape.Route: return PrefabKind.Route;
                default: return PrefabKind.Unknown;
            }
        }

        /// <summary>
        /// A prefab of another class than the entity (a road prefab on a building, a building prefab on a node), or an
        /// edit that swaps the prefab for one of another class. Vanilla tools only replace a prefab within its class
        /// (road upgrades). Not relaxed by any mod: the host creates the entity from the prefab's own archetype.
        /// </summary>
        public static RejectReason CheckClass(PlacementCandidate c, out string detail)
        {
            detail = null;
            PrefabKind expected = ExpectedKind(c.Shape);
            if (expected == PrefabKind.Unknown || !c.Prefab.Known || c.Prefab.Kind == PrefabKind.Unknown) return RejectReason.None;
            if (c.Prefab.Kind != expected)
            {
                detail = "a " + c.Shape + " with a " + c.Prefab.Kind + " prefab";
                return RejectReason.PrefabClass;
            }
            if (!c.Created && c.OldPrefab.Known && c.OldPrefab.Kind != PrefabKind.Unknown && c.OldPrefab.Kind != c.Prefab.Kind)
            {
                detail = "replaces a " + c.OldPrefab.Kind + " prefab with a " + c.Prefab.Kind + " prefab";
                return RejectReason.PrefabClass;
            }
            return RejectReason.None;
        }

        /// <summary>
        /// A free-standing object, edge, area or line of a prefab the host's progression has not unlocked, created or
        /// swapped in by an edit. Owned entities (a building's own sub-nets, which may use any net) and nodes (they take
        /// their edges' prefab) are not checked, nor new edges of a prefab the proposal also edits or deletes (a split).
        /// </summary>
        public static RejectReason CheckUnlock(PlacementCandidate c, PlacementContext ctx, out string detail)
        {
            detail = null;
            if (c.Owned || !c.Prefab.Known || !c.Prefab.Locked) return RejectReason.None;
            switch (c.Shape)
            {
                case CandidateShape.Object:
                case CandidateShape.Edge:
                case CandidateShape.Area:
                case CandidateShape.Route:
                    break;
                default:
                    return RejectReason.None;
            }
            if (!c.Created && (!c.OldPrefab.Known || c.OldPrefab.Key == c.Prefab.Key)) return RejectReason.None; // an edit that keeps its prefab
            if (ctx.TouchedPrefabs.Contains(c.Prefab.Key)) return RejectReason.None;
            detail = (c.Created ? "creates a " : "changes a " + c.Shape + " to a ") + "locked " + c.Prefab.Kind + " prefab";
            return RejectReason.Locked;
        }

        /// <summary>
        /// Cheap shape limits on what the proposal creates or moves: positions inside the playable area and within a
        /// generous height range, edges between two different nodes with their curve ends on those nodes and no longer
        /// than the game builds them, closed area polygons that do not cross themselves and enclose some area.
        /// </summary>
        public static RejectReason CheckGeometry(PlacementCandidate c, PlacementContext ctx, out string detail)
        {
            detail = null;
            if (c.Owned) return RejectReason.None;
            switch (c.Shape)
            {
                case CandidateShape.Object:
                case CandidateShape.Node:
                    if (c.HasPosition && IsPlacedOrMoved(c) && !InsideMap(c.Position, ctx, out detail)) return RejectReason.Geometry;
                    return RejectReason.None;
                case CandidateShape.Area:
                    return CheckArea(c, ctx, out detail);
                case CandidateShape.Edge:
                    return CheckEdge(c, out detail);
                default:
                    return RejectReason.None;
            }
        }

        /// <summary>
        /// New or moved free-standing objects, nodes and area nodes that lie inside a tile the city does not own (and not
        /// within <see cref="TileMargin"/> of an owned one), as the game's <c>ExceedsCityLimits</c> check refuses them.
        /// Edges are covered by their nodes.
        /// </summary>
        public static RejectReason CheckTiles(PlacementCandidate c, PlacementContext ctx, out string detail)
        {
            detail = null;
            if (c.Owned || c.Prefab.TileExempt || ctx.Tiles.IsEmpty) return RejectReason.None;
            switch (c.Shape)
            {
                case CandidateShape.Object:
                case CandidateShape.Node:
                    if (c.HasPosition && IsPlacedOrMoved(c) && ctx.Tiles.Classify(c.Position.X, c.Position.Z, TileMargin) == TileVerdict.Unowned)
                    {
                        detail = "places a " + c.Shape + " at " + c.Position + " on a tile the city does not own";
                        return RejectReason.OutsideTiles;
                    }
                    return RejectReason.None;
                case CandidateShape.Area:
                    for (int i = 0; i < c.Polygon.Count; i++)
                    {
                        PlacementPoint p = c.Polygon[i];
                        if (!c.Created && IsNear(p, c.OldPolygon, MoveThreshold)) continue;
                        if (ctx.Tiles.Classify(p.X, p.Z, TileMargin) != TileVerdict.Unowned) continue;
                        detail = "puts an area node at " + p + " on a tile the city does not own";
                        return RejectReason.OutsideTiles;
                    }
                    return RejectReason.None;
                default:
                    return RejectReason.None;
            }
        }

        /// <summary>A spawn, or an edit that moved the object or node by more than <see cref="MoveThreshold"/>.</summary>
        public static bool IsPlacedOrMoved(PlacementCandidate c)
        {
            if (c.Created) return true;
            return c.HasOldPosition && c.Position.DistanceXZ(c.OldPosition) > MoveThreshold;
        }

        private static bool InsideMap(PlacementPoint p, PlacementContext ctx, out string detail)
        {
            detail = null;
            if (ctx.HasMapBounds && (p.X < ctx.MinX - MapMargin || p.X > ctx.MaxX + MapMargin || p.Z < ctx.MinZ - MapMargin || p.Z > ctx.MaxZ + MapMargin))
            {
                detail = "places something at " + p + ", outside the map";
                return false;
            }
            if (ctx.HasHeightRange && (p.Y < ctx.MinY - HeightSlack || p.Y > ctx.MaxY + HeightSlack))
            {
                detail = "places something at " + p + ", far above or below the terrain";
                return false;
            }
            return true;
        }

        private static RejectReason CheckEdge(PlacementCandidate c, out string detail)
        {
            detail = null;
            if (c.StartNode != 0 && c.StartNode == c.EndNode)
            {
                detail = "an edge from node #" + c.StartNode + " to itself";
                return RejectReason.Geometry;
            }
            if (!c.HasCurve) return RejectReason.None;
            float chord = c.CurveStart.DistanceXZ(c.CurveEnd);
            if (c.Prefab.Known && c.Prefab.MaxEdgeLength > 0 && chord > c.Prefab.MaxEdgeLength * EdgeLengthFactor + EdgeNodeSlack)
            {
                detail = "an edge of " + chord.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " m (the net's edges are at most "
                         + c.Prefab.MaxEdgeLength.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " m)";
                return RejectReason.Geometry;
            }
            if (c.HasNodePositions && (c.CurveStart.DistanceXZ(c.StartPosition) > EndpointTolerance || c.CurveEnd.DistanceXZ(c.EndPosition) > EndpointTolerance))
            {
                detail = "an edge whose curve " + c.CurveStart + " - " + c.CurveEnd + " does not end at its nodes " + c.StartPosition + " - " + c.EndPosition;
                return RejectReason.Geometry;
            }
            return RejectReason.None;
        }

        private static RejectReason CheckArea(PlacementCandidate c, PlacementContext ctx, out string detail)
        {
            detail = null;
            if (c.Polygon.Count == 0) return RejectReason.None;
            bool changed = c.Created;
            for (int i = 0; i < c.Polygon.Count; i++)
            {
                PlacementPoint p = c.Polygon[i];
                if (!c.Created && IsNear(p, c.OldPolygon, MoveThreshold)) continue;
                changed = true;
                if (!InsideMap(p, ctx, out detail)) return RejectReason.Geometry;
            }
            if (!changed || !c.Complete) return RejectReason.None;
            if (c.Polygon.Count < 3)
            {
                detail = "a closed area of " + c.Polygon.Count + " node(s)";
                return RejectReason.Geometry;
            }
            if (Math.Abs(PolygonGeometry.SignedArea(c.Polygon)) < MinPolygonArea)
            {
                detail = "a closed area without area";
                return RejectReason.Geometry;
            }
            if (c.Polygon.Count <= MaxPolygonNodesTested && PolygonGeometry.SelfIntersects(c.Polygon, true))
            {
                detail = "an area whose outline crosses itself";
                return RejectReason.Geometry;
            }
            return RejectReason.None;
        }

        private static bool IsNear(PlacementPoint p, List<PlacementPoint> points, float distance)
        {
            for (int i = 0; i < points.Count; i++)
            {
                if (p.DistanceXZ(points[i]) <= distance) return true;
            }
            return false;
        }
    }

    /// <summary>Where a point lies relative to the city's map tiles.</summary>
    public enum TileVerdict : byte
    {
        /// <summary>On no tile at all (outside the tile grid; the map bounds check decides).</summary>
        NoTile,
        /// <summary>On an owned tile or within the margin of one.</summary>
        Owned,
        /// <summary>On a tile the city does not own, farther than the margin from every owned tile.</summary>
        Unowned,
    }

    /// <summary>
    /// The city's map tiles as horizontal polygons, owned or not (the game marks a tile the city has not bought with
    /// <c>Native</c>). Built by the game layer from the tiles' area nodes; main thread only.
    /// </summary>
    public sealed class TileMap
    {
        private readonly List<Tile> _owned = new List<Tile>();
        private readonly List<Tile> _unowned = new List<Tile>();

        public int OwnedCount => _owned.Count;

        public int UnownedCount => _unowned.Count;

        public bool IsEmpty => _owned.Count == 0 && _unowned.Count == 0;

        public void Clear()
        {
            _owned.Clear();
            _unowned.Clear();
        }

        /// <summary>Adds a tile; polygons of fewer than three points are ignored.</summary>
        public void Add(bool owned, IReadOnlyList<PlacementPoint> polygon)
        {
            if (polygon == null || polygon.Count < 3) return;
            var tile = new Tile(polygon);
            (owned ? _owned : _unowned).Add(tile);
        }

        /// <summary>
        /// <see cref="TileVerdict.Owned"/> when the point is on an owned tile or within <paramref name="margin"/> of one,
        /// otherwise <see cref="TileVerdict.Unowned"/> when it is on another tile, otherwise <see cref="TileVerdict.NoTile"/>.
        /// </summary>
        public TileVerdict Classify(float x, float z, float margin)
        {
            margin = Math.Max(0, margin);
            foreach (Tile t in _owned)
            {
                if (!t.Near(x, z, margin)) continue;
                if (PolygonGeometry.Contains(t.Xs, t.Zs, x, z) || PolygonGeometry.DistanceToOutline(t.Xs, t.Zs, x, z) <= margin) return TileVerdict.Owned;
            }
            foreach (Tile t in _unowned)
            {
                if (t.Near(x, z, 0) && PolygonGeometry.Contains(t.Xs, t.Zs, x, z)) return TileVerdict.Unowned;
            }
            return TileVerdict.NoTile;
        }

        private sealed class Tile
        {
            public readonly float[] Xs;
            public readonly float[] Zs;
            private readonly float _minX, _minZ, _maxX, _maxZ;

            public Tile(IReadOnlyList<PlacementPoint> polygon)
            {
                Xs = new float[polygon.Count];
                Zs = new float[polygon.Count];
                _minX = _minZ = float.MaxValue;
                _maxX = _maxZ = float.MinValue;
                for (int i = 0; i < polygon.Count; i++)
                {
                    Xs[i] = polygon[i].X;
                    Zs[i] = polygon[i].Z;
                    _minX = Math.Min(_minX, Xs[i]);
                    _maxX = Math.Max(_maxX, Xs[i]);
                    _minZ = Math.Min(_minZ, Zs[i]);
                    _maxZ = Math.Max(_maxZ, Zs[i]);
                }
            }

            public bool Near(float x, float z, float margin) => x >= _minX - margin && x <= _maxX + margin && z >= _minZ - margin && z <= _maxZ + margin;
        }
    }

    /// <summary>Horizontal (x, z) polygon helpers of the placement rules.</summary>
    public static class PolygonGeometry
    {
        /// <summary>Even-odd point-in-polygon test.</summary>
        public static bool Contains(float[] xs, float[] zs, float x, float z)
        {
            bool inside = false;
            int n = xs.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if ((zs[i] > z) != (zs[j] > z) && x < (xs[j] - xs[i]) * (z - zs[i]) / (zs[j] - zs[i]) + xs[i]) inside = !inside;
            }
            return inside;
        }

        /// <summary>The distance from the point to the nearest edge of the closed outline.</summary>
        public static float DistanceToOutline(float[] xs, float[] zs, float x, float z)
        {
            double best = double.MaxValue;
            int n = xs.Length;
            for (int i = 0, j = n - 1; i < n; j = i++) best = Math.Min(best, SegmentDistance(xs[j], zs[j], xs[i], zs[i], x, z));
            return (float)best;
        }

        private static double SegmentDistance(double ax, double az, double bx, double bz, double px, double pz)
        {
            double dx = bx - ax, dz = bz - az;
            double len = dx * dx + dz * dz;
            double t = len > 0 ? Math.Max(0, Math.Min(1, ((px - ax) * dx + (pz - az) * dz) / len)) : 0;
            double cx = ax + t * dx - px, cz = az + t * dz - pz;
            return Math.Sqrt(cx * cx + cz * cz);
        }

        /// <summary>The signed area of the polygon (shoelace formula over x and z).</summary>
        public static double SignedArea(IReadOnlyList<PlacementPoint> polygon)
        {
            double sum = 0;
            int n = polygon.Count;
            for (int i = 0, j = n - 1; i < n; j = i++) sum += (double)polygon[j].X * polygon[i].Z - (double)polygon[i].X * polygon[j].Z;
            return sum * 0.5;
        }

        /// <summary>
        /// True when two non-adjacent edges of the outline cross properly (touching end points and collinear overlaps do
        /// not count, so only an outline that certainly crosses itself is reported).
        /// </summary>
        public static bool SelfIntersects(IReadOnlyList<PlacementPoint> polygon, bool closed)
        {
            int n = polygon.Count;
            int edges = closed ? n : n - 1;
            for (int i = 0; i < edges; i++)
            {
                PlacementPoint a = polygon[i], b = polygon[(i + 1) % n];
                for (int j = i + 2; j < edges; j++)
                {
                    if (closed && i == 0 && j == n - 1) continue; // the closing edge is adjacent to the first
                    PlacementPoint c = polygon[j], d = polygon[(j + 1) % n];
                    if (ProperlyCross(a, b, c, d)) return true;
                }
            }
            return false;
        }

        private static bool ProperlyCross(PlacementPoint a, PlacementPoint b, PlacementPoint c, PlacementPoint d)
        {
            double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static double Cross(PlacementPoint o, PlacementPoint p, PlacementPoint q) => ((double)p.X - o.X) * ((double)q.Z - o.Z) - ((double)p.Z - o.Z) * ((double)q.X - o.X);
    }
}
