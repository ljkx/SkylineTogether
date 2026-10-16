using System;
using Colossal.Mathematics;
using Game.Rendering;
using Game.Simulation;
using SkylineTogether.Core.Session;
using Unity.Mathematics;
using UnityEngine;

namespace SkylineTogether.Presence
{
    /// <summary>
    /// Draws a remote player's <see cref="ToolPreview"/> into the game's overlay buffer as translucent
    /// filled outlines in the player's colour: net curves with their width, footprints, area outlines with their corner
    /// points, painted zone cells, line paths. Removals are filled red, invalid placements outlined red, sub-elements
    /// fainter. Outline widths grow with the camera distance so a preview stays readable from far away. Main thread,
    /// allocation-free.
    /// <para>
    /// Heights: shapes on the ground are projected onto the terrain like the game's own tool guides. Shapes the sender
    /// marked <see cref="ToolPreviewFlags.Elevated"/> (elevated roads, bridges, raised nodes, objects placed at an
    /// elevation) are drawn in world space at their real height (the overlay's absolute curves, which follow a
    /// curve's slope, the way the game draws its raised guide lines), with a faint shadow projected onto the ground below
    /// and thin vertical drop lines (the overlay's cylinder mesh) from the shape down to the terrain, so the height reads
    /// at a glance. <see cref="ToolPreviewFlags.Underground"/> shapes (tunnels, lowered nodes) stay on the terrain
    /// surface, dashed and dimmer, with a small downward arrow on each tunnel piece. The terrain height comes from the
    /// local copy of the terrain (it is synced), so the wire carries only the real heights.
    /// </para>
    /// </summary>
    internal static class ToolPreviewRenderer
    {
        private const OverlayRenderSystem.StyleFlags Projected = OverlayRenderSystem.StyleFlags.Projected;

        /// <summary>World space at the real height; where terrain or buildings hide it, it fades instead of vanishing
        /// (the style of the game's raised water source guides).</summary>
        private const OverlayRenderSystem.StyleFlags Raised = OverlayRenderSystem.StyleFlags.DepthFadeBelow;

        /// <summary>A shape this high above the terrain (metres) gets drop lines.</summary>
        private const float MinDropHeight = 1f;

        /// <summary>Metres of curve per drop line, and the most drop lines one curve gets.</summary>
        private const float DropSpacing = 40f;
        private const int MaxDropsPerCurve = 8;

        private static readonly Color DeleteFill = new Color(0.95f, 0.22f, 0.18f, 0.32f);
        private static readonly Color ErrorOutline = new Color(1f, 0.25f, 0.2f, 0.95f);
        private static readonly float2 Up = new float2(0f, 1f);

        /// <summary>
        /// Draws <paramref name="preview"/>; <paramref name="cameraDistance"/> (metres to the preview) scales the lines,
        /// <paramref name="facing"/> (the camera's horizontal forward direction) turns the underground arrows towards
        /// the viewer, and <paramref name="terrain"/> (may be not created: then no drop lines) gives the ground height.
        /// </summary>
        public static void Draw(OverlayRenderSystem.Buffer buffer, ToolPreview preview, Color color, float cameraDistance, float2 facing, ref TerrainHeightData terrain)
        {
            if (preview == null || preview.Count == 0) return;
            float line = math.clamp(cameraDistance * 0.0022f, 0.3f, 8f);
            float[] d = preview.Data;
            bool ground = terrain.isCreated;
            for (int i = 0; i < preview.Count; i++)
            {
                ToolPreviewFlags flags = preview.FlagsAt(i);
                bool secondary = (flags & ToolPreviewFlags.Secondary) != 0;
                bool elevated = (flags & ToolPreviewFlags.Elevated) != 0;
                bool underground = !elevated && (flags & ToolPreviewFlags.Underground) != 0;
                Color outline = (flags & ToolPreviewFlags.Error) != 0 ? ErrorOutline : WithAlpha(color, secondary ? 0.55f : 0.95f);
                Color fill = (flags & ToolPreviewFlags.Delete) != 0 ? DeleteFill : WithAlpha(color, secondary ? 0.1f : 0.24f);
                if (underground)
                {
                    outline = WithAlpha(outline, outline.a * 0.6f);
                    fill = WithAlpha(fill, fill.a * 0.45f);
                }
                float w = secondary ? line * 0.6f : line;
                OverlayRenderSystem.StyleFlags style = elevated ? Raised : Projected;
                int o = preview.StartOf(i);
                switch (preview.ShapeAt(i))
                {
                    case ToolPreviewShape.Curve:
                    {
                        var curve = new Bezier4x3(V(d, o), V(d, o + 3), V(d, o + 6), V(d, o + 9));
                        float width = math.max(d[o + 12], w * 3f);
                        if (underground) DrawTunnel(buffer, outline, fill, w, curve, width, facing, secondary, ref terrain);
                        else buffer.DrawCurve(outline, fill, w, style, curve, width);
                        if (elevated)
                        {
                            buffer.DrawCurve(Shadow(outline), Shadow(fill), w * 0.6f, Projected, curve, width);
                            if (ground) DropsAlong(buffer, outline, w, curve, ref terrain);
                        }
                        break;
                    }
                    case ToolPreviewShape.Segment:
                    {
                        var segment = new Line3.Segment(V(d, o), V(d, o + 3));
                        float width = math.max(d[o + 6], w * 3f);
                        if (underground) buffer.DrawDashedLine(outline, fill, w, Projected, segment, width, Dash(width), Dash(width) * 0.6f);
                        else buffer.DrawLine(outline, fill, w, style, segment, width, default(float2));
                        if (elevated)
                        {
                            buffer.DrawLine(Shadow(outline), Shadow(fill), w * 0.6f, Projected, segment, width, default(float2));
                            if (ground) DropsAlong(buffer, outline, w, Straight(segment), ref terrain);
                        }
                        break;
                    }
                    case ToolPreviewShape.Circle:
                    {
                        float3 center = V(d, o);
                        float diameter = math.max(d[o + 3], w * 4f);
                        buffer.DrawCircle(outline, fill, w, style, Up, center, diameter);
                        if (elevated)
                        {
                            buffer.DrawCircle(Shadow(outline), Shadow(fill), w * 0.6f, Projected, Up, center, diameter);
                            if (ground) Drop(buffer, outline, w, center, ref terrain);
                        }
                        break;
                    }
                    case ToolPreviewShape.Rect:
                    {
                        float3 center = V(d, o);
                        DrawRect(buffer, outline, fill, w, style, center, d[o + 3], d[o + 4], d[o + 5]);
                        if (elevated)
                        {
                            DrawRect(buffer, Shadow(outline), Shadow(fill), w * 0.6f, Projected, center, d[o + 3], d[o + 4], d[o + 5]);
                            if (ground) Drop(buffer, outline, w, center, ref terrain);
                        }
                        break;
                    }
                    case ToolPreviewShape.Polyline:
                        DrawPolyline(buffer, preview, i, outline, w, (flags & ToolPreviewFlags.Closed) != 0);
                        break;
                    case ToolPreviewShape.ZoneBlock:
                        DrawZoneBlock(buffer, outline, fill, w, V(d, o), d[o + 3], (int)d[o + 4], (int)d[o + 5], preview.BitsAt(i));
                        break;
                }
            }
        }

        /// <summary>A tunnel piece: dashed on the terrain surface, with a downward arrow above its middle (primary pieces only).</summary>
        private static void DrawTunnel(OverlayRenderSystem.Buffer buffer, Color outline, Color fill, float w, Bezier4x3 curve, float width, float2 facing, bool secondary, ref TerrainHeightData terrain)
        {
            float dash = Dash(width);
            buffer.DrawDashedCurve(outline, fill, w, Projected, curve, width, dash, dash * 0.6f);
            if (secondary) return;
            float3 middle = MathUtils.Position(curve, 0.5f);
            float size = math.clamp(w * 2.5f, 1f, 16f); // the arrow mesh is 3 units tall and 1.5 wide
            if (!MathUtils.TryNormalize(ref facing)) facing = Up;
            Quaternion rotation = Quaternion.LookRotation(new Vector3(facing.x, 0f, facing.y), Vector3.up);
            // a negative height mirrors the arrow downwards from its position: it points at the surface above the tunnel
            float surface = terrain.isCreated ? TerrainUtils.SampleHeight(ref terrain, middle) : math.max(math.max(curve.a.y, curve.d.y), middle.y);
            float top = surface + size * 3.5f;
            buffer.DrawCustomMesh(WithAlpha(outline, 0.85f), new float3(middle.x, top, middle.z), -size, size, OverlayRenderSystem.CustomMeshType.Arrow, rotation);
        }

        /// <summary>Drop lines at the middle of evenly spaced stretches of an elevated curve (its ends are the nodes, which get their own).</summary>
        private static void DropsAlong(OverlayRenderSystem.Buffer buffer, Color outline, float w, Bezier4x3 curve, ref TerrainHeightData terrain)
        {
            int count = math.clamp((int)math.ceil(MathUtils.Length(curve.xz) / DropSpacing), 1, MaxDropsPerCurve);
            for (int k = 0; k < count; k++) Drop(buffer, outline, w, MathUtils.Position(curve, (k + 0.5f) / count), ref terrain);
        }

        /// <summary>A thin vertical line from <paramref name="top"/> down to the terrain, with a small ring where it meets the ground.</summary>
        private static void Drop(OverlayRenderSystem.Buffer buffer, Color outline, float w, float3 top, ref TerrainHeightData terrain)
        {
            float groundY = TerrainUtils.SampleHeight(ref terrain, top);
            float height = top.y - groundY;
            if (!(height > MinDropHeight) || height > 5000f) return;
            var center = new float3(top.x, groundY + height * 0.5f, top.z);
            float radius = math.max(w * 0.3f, 0.15f); // the cylinder mesh has a radius of 1 and a height of 1, centred
            buffer.DrawCustomMesh(WithAlpha(outline, outline.a * 0.7f), center, height, radius, OverlayRenderSystem.CustomMeshType.Cylinder);
            var foot = new float3(top.x, groundY, top.z);
            buffer.DrawCircle(outline, WithAlpha(outline, outline.a * 0.25f), w * 0.4f, Projected, Up, foot, w * 4f);
        }

        /// <summary>An oriented rectangle as a straight band: the band runs along the local z axis, its width is the x size.</summary>
        private static void DrawRect(OverlayRenderSystem.Buffer buffer, Color outline, Color fill, float w, OverlayRenderSystem.StyleFlags style, float3 center, float sizeX, float sizeZ, float yaw)
        {
            float length = math.max(sizeZ, w * 3f);
            var forward = new float3(math.sin(yaw), 0f, math.cos(yaw)) * (length * 0.5f);
            buffer.DrawLine(outline, fill, w, style, new Line3.Segment(center - forward, center + forward), math.max(sizeX, w * 3f), default(float2));
        }

        private static void DrawPolyline(OverlayRenderSystem.Buffer buffer, ToolPreview preview, int i, Color outline, float w, bool closed)
        {
            int n = preview.PolylinePointCount(i);
            float width = preview.Data[preview.StartOf(i)];
            width = width > 0f ? width : w * 2.5f;
            Color corner = WithAlpha(outline, outline.a * 0.35f);
            var round = new float2(1f, 1f);
            for (int k = 0; k < n; k++)
            {
                PreviewPoint p = preview.PointAt(i, k);
                var a = new float3(p.X, p.Y, p.Z);
                if (k + 1 < n || (closed && n > 2))
                {
                    PreviewPoint q = preview.PointAt(i, k + 1 < n ? k + 1 : 0);
                    buffer.DrawLine(outline, outline, 0f, Projected, new Line3.Segment(a, new float3(q.X, q.Y, q.Z)), width, round);
                }
                buffer.DrawCircle(outline, corner, w * 0.5f, Projected, Up, a, width * 3f); // the corner / control points
            }
        }

        /// <summary>
        /// The painted cells of a zone block as one band per run of cells in a row; the cell centres follow the game's
        /// <c>ZoneUtils.GetCellPosition</c> (8 m cells, local z along the block's direction, x along its right).
        /// </summary>
        private static void DrawZoneBlock(OverlayRenderSystem.Buffer buffer, Color outline, Color fill, float w, float3 center, float yaw, int cellsX, int cellsZ, ulong bits)
        {
            if (cellsX < 1 || cellsZ < 1 || cellsX * cellsZ > ToolPreview.MaxZoneCells) return;
            var direction = new float2(math.sin(yaw), math.cos(yaw));
            float2 right = MathUtils.Right(direction);
            float edge = math.min(w * 0.5f, 1f);
            for (int z = 0; z < cellsZ; z++)
            {
                int x = 0;
                while (x < cellsX)
                {
                    if ((bits & (1UL << (z * cellsX + x))) == 0)
                    {
                        x++;
                        continue;
                    }
                    int start = x;
                    while (x < cellsX && (bits & (1UL << (z * cellsX + x))) != 0) x++;
                    float3 a = Cell(center, direction, right, cellsX, cellsZ, start, z);
                    float3 b = Cell(center, direction, right, cellsX, cellsZ, x - 1, z);
                    var r3 = new float3(right.x, 0f, right.y) * 4f;
                    buffer.DrawLine(outline, fill, edge, Projected, new Line3.Segment(a + r3, b - r3), 7.6f, default(float2));
                }
            }
        }

        private static float3 Cell(float3 center, float2 direction, float2 right, int cellsX, int cellsZ, int x, int z)
        {
            float2 offset = new float2(cellsX - 2 * x - 1, cellsZ - 2 * z - 1) * 4f;
            float3 p = center;
            p.xz += direction * offset.y + right * offset.x;
            return p;
        }

        /// <summary>Dash length of an underground band of <paramref name="width"/> metres.</summary>
        private static float Dash(float width) => math.clamp(width, 6f, 40f);

        /// <summary>The faint copy projected onto the ground below an elevated shape.</summary>
        private static Color Shadow(Color c) => WithAlpha(c, c.a * 0.3f);

        private static Bezier4x3 Straight(Line3.Segment s) => new Bezier4x3(s.a, math.lerp(s.a, s.b, 1f / 3f), math.lerp(s.a, s.b, 2f / 3f), s.b);

        private static float3 V(float[] d, int o) => new float3(d[o], d[o + 1], d[o + 2]);

        private static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, a);
    }
}
