namespace SkylineTogether.Core
{
    /// <summary>
    /// A peer's motion interest region (ARCH §4.8.3 item 1): a disc around the camera pivot plus the optional frustum
    /// footprint, entered at <see cref="Radius"/> and left at <see cref="LeaveRadius"/> (1.25 × Radius by convention).
    /// The hysteresis also applies to the footprint: while a mover is inside, the footprint counts as scaled around
    /// the centre by LeaveRadius / Radius. Coordinates are world metres on the XZ plane.
    /// </summary>
    public struct MotionInterestRegion
    {
        public float CenterX, CenterZ, Radius, LeaveRadius;

        /// <summary>Optional footprint polygon as (x0, z0, x1, z1, ...); ignored when null or with fewer than 3 vertices.</summary>
        public float[] FrustumXZ;

        /// <summary>
        /// True when (x, z) is inside the region. <paramref name="currentlyInside"/> selects the leave threshold (the
        /// mover is in interest now) or the enter threshold (it is not). Non-finite coordinates are never inside.
        /// </summary>
        public bool Contains(float x, float z, bool currentlyInside)
        {
            if (!IsFinite(x) || !IsFinite(z)) return false;
            float radius = Radius;
            double scale = 1.0;
            if (currentlyInside && LeaveRadius > Radius)
            {
                radius = LeaveRadius;
                if (Radius > 0f) scale = (double)LeaveRadius / Radius;
            }
            double dx = (double)x - CenterX;
            double dz = (double)z - CenterZ;
            if (radius > 0f && dx * dx + dz * dz <= (double)radius * radius) return true;

            float[] polygon = FrustumXZ;
            if (polygon == null || polygon.Length < 6) return false;
            // point in the scaled polygon  <=>  the point pulled towards the centre by 1/scale lies in the polygon
            return PointInPolygon(polygon, CenterX + dx / scale, CenterZ + dz / scale);
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        // even-odd rule (ray casting along +x); vertices are (polygon[2i], polygon[2i+1])
        private static bool PointInPolygon(float[] polygon, double px, double pz)
        {
            int n = polygon.Length / 2;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = polygon[2 * i], zi = polygon[2 * i + 1];
                double xj = polygon[2 * j], zj = polygon[2 * j + 1];
                if ((zi > pz) != (zj > pz))
                {
                    double xCross = xi + (pz - zi) * (xj - xi) / (zj - zi);
                    if (px < xCross) inside = !inside;
                }
            }
            return inside;
        }
    }
}
