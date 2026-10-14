using System;
using Colossal.Mathematics;
using Game.Rendering;
using SkylineTogether.Core.Session;
using Unity.Mathematics;
using UnityEngine;

namespace SkylineTogether.Presence
{
    /// <summary>
    /// Draws a remote player's <see cref="ToolPreview"/> into the game's overlay buffer as translucent
    /// filled outlines in the player's colour, projected onto the ground like the game's own tool guides: net curves
    /// with their width, footprints, area outlines with their corner points, painted zone cells, line paths. Removals are
    /// filled red, invalid placements outlined red, sub-elements fainter. Outline widths grow with the camera distance
    /// so a preview stays readable from far away. Main thread, allocation-free.
    /// </summary>
    internal static class ToolPreviewRenderer
    {
        private const OverlayRenderSystem.StyleFlags Style = OverlayRenderSystem.StyleFlags.Projected;
        private static readonly Color DeleteFill = new Color(0.95f, 0.22f, 0.18f, 0.32f);
        private static readonly Color ErrorOutline = new Color(1f, 0.25f, 0.2f, 0.95f);
        private static readonly float2 Up = new float2(0f, 1f);

        /// <summary>Draws <paramref name="preview"/>; <paramref name="cameraDistance"/> (metres to the preview) scales the lines.</summary>
        public static void Draw(OverlayRenderSystem.Buffer buffer, ToolPreview preview, Color color, float cameraDistance)
        {
            if (preview == null || preview.Count == 0) return;
            float line = math.clamp(cameraDistance * 0.0022f, 0.3f, 8f);
            float[] d = preview.Data;
            for (int i = 0; i < preview.Count; i++)
            {
                ToolPreviewFlags flags = preview.FlagsAt(i);
                bool secondary = (flags & ToolPreviewFlags.Secondary) != 0;
                Color outline = (flags & ToolPreviewFlags.Error) != 0 ? ErrorOutline : WithAlpha(color, secondary ? 0.55f : 0.95f);
                Color fill = (flags & ToolPreviewFlags.Delete) != 0 ? DeleteFill : WithAlpha(color, secondary ? 0.1f : 0.24f);
                float w = secondary ? line * 0.6f : line;
                int o = preview.StartOf(i);
                switch (preview.ShapeAt(i))
                {
                    case ToolPreviewShape.Curve:
                    {
                        var curve = new Bezier4x3(V(d, o), V(d, o + 3), V(d, o + 6), V(d, o + 9));
                        buffer.DrawCurve(outline, fill, w, Style, curve, math.max(d[o + 12], w * 3f));
                        break;
                    }
                    case ToolPreviewShape.Segment:
                        buffer.DrawLine(outline, fill, w, Style, new Line3.Segment(V(d, o), V(d, o + 3)), math.max(d[o + 6], w * 3f), default(float2));
                        break;
                    case ToolPreviewShape.Circle:
                        buffer.DrawCircle(outline, fill, w, Style, Up, V(d, o), math.max(d[o + 3], w * 4f));
                        break;
                    case ToolPreviewShape.Rect:
                        DrawRect(buffer, outline, fill, w, V(d, o), d[o + 3], d[o + 4], d[o + 5]);
                        break;
                    case ToolPreviewShape.Polyline:
                        DrawPolyline(buffer, preview, i, outline, w, (flags & ToolPreviewFlags.Closed) != 0);
                        break;
                    case ToolPreviewShape.ZoneBlock:
                        DrawZoneBlock(buffer, outline, fill, w, V(d, o), d[o + 3], (int)d[o + 4], (int)d[o + 5], preview.BitsAt(i));
                        break;
                }
            }
        }

        /// <summary>An oriented rectangle as a straight band: the band runs along the local z axis, its width is the x size.</summary>
        private static void DrawRect(OverlayRenderSystem.Buffer buffer, Color outline, Color fill, float w, float3 center, float sizeX, float sizeZ, float yaw)
        {
            float length = math.max(sizeZ, w * 3f);
            var forward = new float3(math.sin(yaw), 0f, math.cos(yaw)) * (length * 0.5f);
            buffer.DrawLine(outline, fill, w, Style, new Line3.Segment(center - forward, center + forward), math.max(sizeX, w * 3f), default(float2));
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
                    buffer.DrawLine(outline, outline, 0f, Style, new Line3.Segment(a, new float3(q.X, q.Y, q.Z)), width, round);
                }
                buffer.DrawCircle(outline, corner, w * 0.5f, Style, Up, a, width * 3f); // the corner / control points
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
                    buffer.DrawLine(outline, fill, edge, Style, new Line3.Segment(a + r3, b - r3), 7.6f, default(float2));
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

        private static float3 V(float[] d, int o) => new float3(d[o], d[o + 1], d[o + 2]);

        private static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, a);
    }
}
