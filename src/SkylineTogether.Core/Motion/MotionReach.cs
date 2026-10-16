using System;

namespace SkylineTogether.Core.Motion
{
    /// <summary>
    /// How far from a client's camera a mover's motion is streamed (ARCHITECTURE.md#movers). Movers outside every region
    /// stay pinned on the client with a rare pose refresh; that is invisible for cars and pedestrians far from the camera,
    /// but an aircraft is seen across the whole map, and ships and trains from kilometres away.
    /// </summary>
    public enum MoverReach : byte
    {
        /// <summary>Cars, trailers, trams, subways, pedestrians, animals: the client's interest region (200-1000 m).</summary>
        Normal = 0,
        /// <summary>Ships and boats, mainline trains: a wider region (<see cref="MotionReach.WideFactor"/> times, 1.5-4 km).</summary>
        Wide = 1,
        /// <summary>Airplanes, helicopters, rockets: everywhere on the map.</summary>
        Global = 2,
    }

    /// <summary>Where a mover in interest lies relative to the client's own region: decides priority and rate of its frames.</summary>
    public enum MotionField : byte
    {
        /// <summary>Within half the region's radius: near-field priority, never thinned by the far-field rate.</summary>
        Near = 0,
        /// <summary>Within the region: far-field priority (thinned only when the link degrades).</summary>
        Mid = 1,
        /// <summary>Outside the region, kept by a wider reach: far-field priority and every second sample only.</summary>
        Far = 2,
    }

    /// <summary>
    /// The interest rule of the reach classes (ARCHITECTURE.md#movers): the region a mover of each class is streamed in, and
    /// the field (priority and rate) of a mover in interest. Pure; the game layer decides the class from the mover's
    /// components and prefab and calls this per sampled mover.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class MotionReach
    {
        /// <summary>A wide mover's region is the client's region scaled by this factor ...</summary>
        public const float WideFactor = 3f;

        /// <summary>... but at least this radius (metres) ...</summary>
        public const float WideMinRadius = 1500f;

        /// <summary>... and at most this one.</summary>
        public const float WideMaxRadius = 4000f;

        /// <summary>A global mover's radius: more than the distance between any two points of a map (14.3 km square).</summary>
        public const float GlobalRadius = 24000f;

        /// <summary>The region a mover of <paramref name="reach"/> is streamed in, given the client's own region.</summary>
        public static MotionInterestRegion RegionFor(in MotionInterestRegion region, MoverReach reach)
        {
            if (reach == MoverReach.Normal) return region;
            float leaveFactor = region.Radius > 0f && region.LeaveRadius > region.Radius ? region.LeaveRadius / region.Radius : 1f;
            float radius = reach == MoverReach.Global
                ? GlobalRadius
                : Math.Max(WideMinRadius, Math.Min(WideMaxRadius, region.Radius * WideFactor));
            return new MotionInterestRegion
            {
                CenterX = region.CenterX,
                CenterZ = region.CenterZ,
                Radius = radius,
                LeaveRadius = reach == MoverReach.Global ? radius : radius * leaveFactor,
            };
        }

        /// <summary>The field of a mover in interest at (x, z), relative to the client's own region.</summary>
        public static MotionField FieldOf(in MotionInterestRegion region, float x, float z)
        {
            double dx = (double)x - region.CenterX, dz = (double)z - region.CenterZ;
            double d2 = dx * dx + dz * dz;
            double r = region.Radius;
            if (d2 <= r * r * 0.25) return MotionField.Near;
            return d2 <= r * r ? MotionField.Mid : MotionField.Far;
        }

        /// <summary>
        /// True when the sample of <paramref name="tick"/> of a far-field mover is sent (every second one of its 16-tick
        /// cycles; the phase is spread over the movers by id, so a crowd of aircraft does not burst on the same ticks).
        /// The client interpolates between the frames it gets.
        /// </summary>
        public static bool SendsFarSample(uint tick, uint netId) => (((tick >> 4) + netId) & 1u) == 0;
    }
}
