using System;

namespace SkylineTogether.Core.Motion
{
    /// <summary>
    /// Small numeric helpers shared by the motion codec, the staging rings and the extrapolator: serial (wrapping)
    /// tick comparison, position quantisation, quaternion maths in double precision, float bit casts. Pure functions,
    /// thread-safe.
    /// </summary>
    internal static class MotionMath
    {
        /// <summary>Ticks per 16-tick mover step: the movers write one frame per 16 ticks (<c>CarMoveSystem.cs:420-423</c>).</summary>
        public const int TicksPerStep = 16;

        /// <summary>Seconds per simulation tick at nominal speed (60 ticks per second, R03 §1.2).</summary>
        public const double SecondsPerTick = 1.0 / 60.0;

        /// <summary>Quantisation steps per metre (1/64 m, ARCH §4.9.3).</summary>
        public const double PositionScale = 64.0;

        /// <summary>
        /// Serial difference <c>a − b</c> of two ticks: correct across the 2^32 wrap as long as the ticks are less than
        /// 2^31 apart (game frame indices are persisted with the city and only grow).
        /// </summary>
        public static int TickDiff(uint a, uint b) => unchecked((int)(a - b));

        /// <summary><paramref name="a"/> is later than <paramref name="b"/> (serial order).</summary>
        public static bool IsAfter(uint a, uint b) => unchecked((int)(a - b)) > 0;

        /// <summary>The 1/64 m grid index of a coordinate; exact inverse of <c>q / 64</c> for every grid value.</summary>
        public static long QuantizePosition(float v) => (long)Math.Floor(v * PositionScale + 0.5);

        /// <summary>The coordinate of a grid index (exact in float for |q| &lt; 2^24).</summary>
        public static float DequantizePosition(long q) => (float)(q / PositionScale);

        public static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        public static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        /// <summary>Every float of the frame is finite.</summary>
        public static bool IsFinite(in MotionFrame f)
        {
            return IsFinite(f.X) && IsFinite(f.Y) && IsFinite(f.Z)
                   && IsFinite(f.VX) && IsFinite(f.VY) && IsFinite(f.VZ)
                   && IsFinite(f.QX) && IsFinite(f.QY) && IsFinite(f.QZ) && IsFinite(f.QW);
        }

        /// <summary>
        /// Normalises a quaternion in double precision. A (near) zero or non-finite quaternion becomes the identity, the
        /// rotation <c>TransformFrame(Transform)</c> would hold for an unrotated object.
        /// </summary>
        public static void Normalize(ref double x, ref double y, ref double z, ref double w)
        {
            double lengthSquared = x * x + y * y + z * z + w * w;
            if (!(lengthSquared > 1e-12) || double.IsInfinity(lengthSquared))
            {
                x = y = z = 0;
                w = 1;
                return;
            }
            double inverse = 1.0 / Math.Sqrt(lengthSquared);
            x *= inverse;
            y *= inverse;
            z *= inverse;
            w *= inverse;
        }

        /// <summary>
        /// The object's forward axis (+Z rotated by the quaternion), as <c>math.forward(q)</c> computes it:
        /// <c>(2(xz + wy), 2(yz − wx), 1 − 2(x² + y²))</c>.
        /// </summary>
        public static void Forward(double x, double y, double z, double w, out double fx, out double fy, out double fz)
        {
            fx = 2.0 * (x * z + w * y);
            fy = 2.0 * (y * z - w * x);
            fz = 1.0 - 2.0 * (x * x + y * y);
        }

        /// <summary>
        /// Spherical interpolation along the shorter arc, as <c>math.slerp</c> does (it flips the second quaternion
        /// when the dot product is negative and falls back to a normalised lerp for nearly equal rotations).
        /// </summary>
        public static void Slerp(in MotionFrame a, in MotionFrame b, double t, out float qx, out float qy, out float qz, out float qw)
        {
            double ax = a.QX, ay = a.QY, az = a.QZ, aw = a.QW;
            double bx = b.QX, by = b.QY, bz = b.QZ, bw = b.QW;
            Normalize(ref ax, ref ay, ref az, ref aw);
            Normalize(ref bx, ref by, ref bz, ref bw);
            double dot = ax * bx + ay * by + az * bz + aw * bw;
            if (dot < 0)
            {
                dot = -dot;
                bx = -bx;
                by = -by;
                bz = -bz;
                bw = -bw;
            }
            double wa, wb;
            if (dot < 0.9995)
            {
                double angle = Math.Acos(dot);
                double inverseSin = 1.0 / Math.Sin(angle);
                wa = Math.Sin(angle * (1.0 - t)) * inverseSin;
                wb = Math.Sin(angle * t) * inverseSin;
            }
            else
            {
                wa = 1.0 - t;
                wb = t;
            }
            double x = ax * wa + bx * wb, y = ay * wa + by * wb, z = az * wa + bz * wb, w = aw * wa + bw * wb;
            Normalize(ref x, ref y, ref z, ref w);
            qx = (float)x;
            qy = (float)y;
            qz = (float)z;
            qw = (float)w;
        }

        /// <summary>The raw IEEE-754 bits of a float.</summary>
        public static unsafe uint SingleToBits(float v) => *(uint*)&v;

        /// <summary>A float from its raw IEEE-754 bits.</summary>
        public static unsafe float BitsToSingle(uint bits) => *(float*)&bits;

        /// <summary>Bitwise equality of two floats (distinguishes −0 from +0, equal NaN payloads compare equal).</summary>
        public static bool SameBits(float a, float b) => SingleToBits(a) == SingleToBits(b);
    }
}
