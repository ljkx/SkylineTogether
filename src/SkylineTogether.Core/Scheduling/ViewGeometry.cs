using System;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// Interest regions from a view report (ARCH §4.8.3 item 1, R15 §7.1). The game camera orbits its pivot at distance
    /// <c>zoom</c>: <c>CameraController.UpdateCamera</c> places the camera at <c>pivot + zoom · (−sin yaw · cos tilt,
    /// sin tilt, −cos yaw · cos tilt)</c> and looks at the pivot (<c>Game/CameraController.cs:396-411, 475-480</c>), so the
    /// horizontal camera-to-pivot vector gives the view direction and its length over <c>zoom</c> the cosine of the
    /// tilt. The frustum footprint is the trapezoid between the ground hits of the lower and upper frustum edges, spread
    /// by the horizontal half angle; the view report carries no field of view, so both half angles are configured. The
    /// footprint is capped so that its far corners stay within the cap distance of the pivot ("the frustum footprint
    /// capped at r_far", R15 §7.1), and it is omitted when it lies inside the disc anyway (a steep view).
    /// </summary>
    internal static class ViewGeometry
    {
        private const double MinHorizontalDistance = 1.0;                 // metres: below this the camera looks straight down
        private const double HorizonMarginRadians = 2.0 * Math.PI / 180.0;

        /// <summary>clamp(k · zoom, min, max); a non-positive or non-finite zoom yields the minimum radius.</summary>
        public static float BaseRadius(SchedulerSettings s, float zoom)
        {
            if (!(zoom > 0) || float.IsInfinity(zoom)) return s.MinMotionRadius;
            double r = (double)s.MotionRadiusPerZoom * zoom;
            if (r < s.MinMotionRadius) r = s.MinMotionRadius;
            if (r > s.MaxMotionRadius) r = s.MaxMotionRadius;
            return (float)r;
        }

        /// <summary>
        /// The region for <paramref name="report"/> with the ladder's <paramref name="radiusScale"/> applied (1 = none):
        /// radius = max(min radius, base radius × scale), leave radius = radius × leave factor, footprint capped at
        /// max(radius, max radius × scale).
        /// </summary>
        public static MotionInterestRegion Build(SchedulerSettings s, Protocol.ViewReport report, float radiusScale)
        {
            float scale = radiusScale > 0f && radiusScale <= 1f ? radiusScale : 1f;
            float radius = Math.Max(s.MinMotionRadius, BaseRadius(s, report.Zoom) * scale);
            var region = new MotionInterestRegion
            {
                CenterX = report.PivotX,
                CenterZ = report.PivotZ,
                Radius = radius,
                LeaveRadius = radius * s.LeaveFactor,
                FrustumXZ = null
            };
            if (s.UseFrustumFootprint)
            {
                double cap = Math.Max(radius, (double)s.MaxMotionRadius * scale);
                region.FrustumXZ = Footprint(s, report, radius, cap);
            }
            return region;
        }

        /// <summary>The footprint polygon (x0, z0, ... x3, z3) or null when it adds nothing to the disc.</summary>
        public static float[] Footprint(SchedulerSettings s, Protocol.ViewReport r, double radius, double cap)
        {
            double cx = r.CamX, cz = r.CamZ, px = r.PivotX, pz = r.PivotZ;
            double dx = px - cx, dz = pz - cz;
            double h = Math.Sqrt(dx * dx + dz * dz);
            if (!(h >= MinHorizontalDistance) || double.IsInfinity(h)) return null;
            double zoom = r.Zoom > 0 && !float.IsInfinity(r.Zoom) ? r.Zoom : h;
            if (h > zoom * 1.5 + 100.0) return null;                         // camera and zoom disagree: no footprint
            if (zoom < h) zoom = h;

            double cosTilt = h / zoom;
            double sinTilt = Math.Sqrt(Math.Max(0.0, 1.0 - cosTilt * cosTilt));
            double tilt = Math.Atan2(sinTilt, cosTilt);
            double height = zoom * sinTilt;
            double vHalf = s.FrustumVerticalHalfAngleRadians;
            double hHalf = s.FrustumHalfAngleRadians;

            double lower = tilt + vHalf;                                      // depression of the lower frustum edge
            double near = lower >= Math.PI / 2 - 1e-3 ? 0.0 : height / Math.Tan(lower);
            double upper = tilt - vHalf;                                      // depression of the upper frustum edge
            double far = upper <= HorizonMarginRadians ? double.PositiveInfinity : height / Math.Tan(upper);

            double sinA = Math.Sin(hHalf), cosA = Math.Cos(hHalf);
            double disc = cap * cap - (h * sinA) * (h * sinA);
            if (disc <= 0) return null;
            double maxFar = h * cosA + Math.Sqrt(disc);                       // far corners within the cap of the pivot
            if (far > maxFar) far = maxFar;
            if (near < 0) near = 0;
            if (!(far > near + 1.0)) return null;

            double fx = dx / h, fz = dz / h;
            double lx = fx * cosA - fz * sinA, lz = fx * sinA + fz * cosA;    // forward rotated by +half angle
            double rx = fx * cosA + fz * sinA, rz = -fx * sinA + fz * cosA;   // forward rotated by -half angle

            double nlx = cx + near * lx, nlz = cz + near * lz;
            double flx = cx + far * lx, flz = cz + far * lz;
            double frx = cx + far * rx, frz = cz + far * rz;
            double nrx = cx + near * rx, nrz = cz + near * rz;

            double r2 = radius * radius;
            if (Within(nlx, nlz, px, pz, r2) && Within(flx, flz, px, pz, r2) && Within(frx, frz, px, pz, r2) && Within(nrx, nrz, px, pz, r2))
            {
                return null;                                                  // convex footprint entirely inside the disc
            }
            return new[] { (float)nlx, (float)nlz, (float)flx, (float)flz, (float)frx, (float)frz, (float)nrx, (float)nrz };
        }

        private static bool Within(double x, double z, double px, double pz, double r2)
        {
            double ex = x - px, ez = z - pz;
            return ex * ex + ez * ez <= r2;
        }
    }
}
