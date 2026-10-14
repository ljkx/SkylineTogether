using System;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// Smooths a remote player's <see cref="ToolPreview"/> between the keyframes that arrive at the presence rate:
    /// while the shapes keep their layout (a curve being dragged, a building being rotated) the shown preview eases
    /// toward the received one every frame; a changed layout or a jump snaps. Allocation-free.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public static class ToolPreviewSmoother
    {
        /// <summary>A coordinate that moved farther than this (metres) between keyframes snaps instead of gliding.</summary>
        public const float SnapDistance = 250f;

        private const float TwoPi = (float)(2.0 * Math.PI);

        /// <summary>
        /// Moves <paramref name="shown"/> toward <paramref name="target"/> by <paramref name="blend"/> (0: stay, 1: reach).
        /// Returns false when it snapped (copied the target).
        /// </summary>
        public static bool Step(ToolPreview shown, ToolPreview target, float blend)
        {
            if (shown == null) throw new ArgumentNullException(nameof(shown));
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (blend >= 1f || !shown.SameLayout(target) || Jumped(shown, target))
            {
                shown.CopyFrom(target);
                return false;
            }
            if (blend <= 0f) return true;
            float[] s = shown.Data;
            float[] t = target.Data;
            for (int i = 0; i < shown.Count; i++)
            {
                int o = shown.StartOf(i);
                int n = shown.LengthOf(i);
                switch (shown.ShapeAt(i))
                {
                    case ToolPreviewShape.Rect:
                        Lerp(s, t, o, 5, blend);
                        s[o + 5] = LerpAngle(s[o + 5], t[o + 5], blend);
                        break;
                    case ToolPreviewShape.ZoneBlock:
                        Lerp(s, t, o, 3, blend);
                        s[o + 3] = LerpAngle(s[o + 3], t[o + 3], blend);
                        s[o + 4] = t[o + 4];
                        s[o + 5] = t[o + 5];
                        break;
                    default:
                        Lerp(s, t, o, n, blend);
                        break;
                }
            }
            shown.CopyFlagsAndBits(target);
            return true;
        }

        private static void Lerp(float[] s, float[] t, int o, int n, float blend)
        {
            for (int k = o; k < o + n; k++) s[k] += (t[k] - s[k]) * blend;
        }

        private static float LerpAngle(float from, float to, float blend)
        {
            float delta = to - from;
            delta -= TwoPi * (float)Math.Floor((delta + Math.PI) / TwoPi); // shortest way round, in [-pi, pi)
            return from + delta * blend;
        }

        private static bool Jumped(ToolPreview shown, ToolPreview target)
        {
            float[] s = shown.Data;
            float[] t = target.Data;
            for (int i = 0; i < shown.Count; i++)
            {
                int o = shown.StartOf(i);
                int points;
                switch (shown.ShapeAt(i))
                {
                    case ToolPreviewShape.Curve: points = 4; break;
                    case ToolPreviewShape.Segment: points = 2; break;
                    case ToolPreviewShape.Polyline: points = shown.PolylinePointCount(i); o += 1; break;
                    default: points = 1; break;
                }
                for (int k = o; k < o + 3 * points; k++)
                {
                    if (Math.Abs(s[k] - t[k]) > SnapDistance) return true;
                }
            }
            return false;
        }
    }
}
