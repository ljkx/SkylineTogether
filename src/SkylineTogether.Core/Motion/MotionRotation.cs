using System;

namespace SkylineTogether.Core.Motion
{
    /// <summary>Rotation encodings of the motion codec (2-bit mode on the wire).</summary>
    internal enum RotationMode : byte
    {
        /// <summary>Deltas only: the reference frame's rotation, bit for bit.</summary>
        SameAsReference = 0,
        /// <summary>12-bit yaw, zero pitch and roll (creatures, cars on flat ground).</summary>
        Yaw = 1,
        /// <summary>12-bit yaw and 7-bit pitch, zero roll: the shape of <c>quaternion.LookRotationSafe(direction, up)</c>
        /// that cars and trains use (<c>CarMoveSystem.cs:192-204</c>, <c>TrainMoveSystem.cs:87</c>).</summary>
        YawPitch = 2,
        /// <summary>Smallest-three with 12 bits per component: aircraft bank, boats tilt on waves and vehicles out of
        /// control tumble (<c>AircraftMoveSystem.cs:107-131</c>, <c>WatercraftMoveSystem.cs:129</c>).</summary>
        Full = 3
    }

    /// <summary>One quantised rotation.</summary>
    internal struct RotationCode
    {
        public RotationMode Mode;
        /// <summary>Yaw in 1/4096 turns (0..4095).</summary>
        public int Yaw;
        /// <summary>Pitch in units of π/256 (−64..63); 0 in <see cref="RotationMode.Yaw"/>.</summary>
        public int Pitch;
        /// <summary>Full: index of the dropped (largest) component and the other three, each in −2047..2047.</summary>
        public int Index, A, B, C;
    }

    /// <summary>
    /// Quantisation of mover rotations (ARCH §4.9.3: yaw 12 bits, pitch 7 bits; plus a full-rotation escape for rolled
    /// objects). The yaw/pitch form reconstructs <c>qY(yaw)·qX(−pitch)</c>, the zero-roll rotation whose forward axis is
    /// <c>(sin yaw·cos pitch, sin pitch, cos yaw·cos pitch)</c>; its half-angle sines and cosines come from tables built
    /// once, so a decode is a few multiplications. Thread-safe (the tables are read-only after type initialisation).
    /// </summary>
    internal static class MotionRotation
    {
        public const int YawBits = 12;
        public const int YawSteps = 1 << YawBits;
        public const int PitchBits = 7;
        public const int PitchMin = -64, PitchMax = 63;
        public const int FullComponentBits = 12;
        public const int FullComponentMax = 2047;

        /// <summary>Largest accepted angle between a rotation and its yaw/pitch reconstruction: yaw quantisation adds
        /// up to 0.044°, pitch up to 0.35°; anything beyond (roll) takes the full encoding.</summary>
        public const double YawPitchToleranceDegrees = 0.5;

        private const double PitchUnit = Math.PI / 256.0;
        private const double InverseSqrt2 = 0.70710678118654752440;

        private static readonly double CosHalfTolerance = Math.Cos(YawPitchToleranceDegrees * Math.PI / 180.0 / 2.0);
        private static readonly double[] SinHalfYaw = BuildYawTable(sine: true);
        private static readonly double[] CosHalfYaw = BuildYawTable(sine: false);
        private static readonly double[] SinHalfPitch = BuildPitchTable(sine: true);
        private static readonly double[] CosHalfPitch = BuildPitchTable(sine: false);

        private static double[] BuildYawTable(bool sine)
        {
            var table = new double[YawSteps];
            for (int code = 0; code < YawSteps; code++)
            {
                int signed = code < YawSteps / 2 ? code : code - YawSteps;   // yaw in [−π, π): cos of the half angle ≥ 0
                double half = signed * Math.PI / YawSteps;
                table[code] = sine ? Math.Sin(half) : Math.Cos(half);
            }
            return table;
        }

        private static double[] BuildPitchTable(bool sine)
        {
            var table = new double[PitchMax - PitchMin + 1];
            for (int pitch = PitchMin; pitch <= PitchMax; pitch++)
            {
                double half = -pitch * PitchUnit / 2.0;                       // rotation about X by −pitch
                table[pitch - PitchMin] = sine ? Math.Sin(half) : Math.Cos(half);
            }
            return table;
        }

        /// <summary>
        /// Chooses the cheapest encoding that reproduces the rotation within the tolerance: yaw only, yaw and pitch, or
        /// the full smallest-three form. The quaternion is normalised first (a degenerate one counts as the identity).
        /// </summary>
        public static RotationCode Encode(float qx, float qy, float qz, float qw)
        {
            double x = qx, y = qy, z = qz, w = qw;
            MotionMath.Normalize(ref x, ref y, ref z, ref w);
            MotionMath.Forward(x, y, z, w, out double fx, out double fy, out double fz);
            double horizontal = Math.Sqrt(fx * fx + fz * fz);
            if (horizontal > 1e-6)
            {
                double yaw = Math.Atan2(fx, fz);
                double pitch = Math.Atan2(fy, horizontal);
                int yawCode = (int)Math.Round(yaw * YawSteps / (2.0 * Math.PI)) & (YawSteps - 1);
                int pitchCode = (int)Math.Round(pitch / PitchUnit);
                if (pitchCode >= PitchMin && pitchCode <= PitchMax)
                {
                    var candidate = new RotationCode
                    {
                        Mode = pitchCode == 0 ? RotationMode.Yaw : RotationMode.YawPitch,
                        Yaw = yawCode,
                        Pitch = pitchCode
                    };
                    DecodeYawPitch(yawCode, pitchCode, out double rx, out double ry, out double rz, out double rw);
                    double dot = Math.Abs(x * rx + y * ry + z * rz + w * rw);
                    if (dot >= CosHalfTolerance) return candidate;
                }
            }
            return EncodeFull(x, y, z, w);
        }

        /// <summary>The quaternion of a code (<see cref="RotationMode.SameAsReference"/> is resolved by the caller).</summary>
        public static void Decode(in RotationCode code, out float qx, out float qy, out float qz, out float qw)
        {
            double x, y, z, w;
            if (code.Mode == RotationMode.Full)
            {
                DecodeFull(code.Index, code.A, code.B, code.C, out x, out y, out z, out w);
            }
            else
            {
                DecodeYawPitch(code.Yaw, code.Mode == RotationMode.Yaw ? 0 : code.Pitch, out x, out y, out z, out w);
            }
            qx = (float)x;
            qy = (float)y;
            qz = (float)z;
            qw = (float)w;
        }

        public static void Write(ref MotionBitWriter bits, in RotationCode code)
        {
            bits.Write((uint)code.Mode, 2);
            switch (code.Mode)
            {
                case RotationMode.Yaw:
                    bits.Write((uint)code.Yaw, YawBits);
                    break;
                case RotationMode.YawPitch:
                    bits.Write((uint)code.Yaw, YawBits);
                    bits.WriteSigned(code.Pitch, PitchBits);
                    break;
                case RotationMode.Full:
                    bits.Write((uint)code.Index, 2);
                    bits.WriteSigned(code.A, FullComponentBits);
                    bits.WriteSigned(code.B, FullComponentBits);
                    bits.WriteSigned(code.C, FullComponentBits);
                    break;
            }
        }

        /// <summary>Reads a rotation; <see cref="RotationMode.SameAsReference"/> is valid only where the caller allows it.</summary>
        public static RotationCode Read(ref MotionBitReader bits, bool allowSameAsReference)
        {
            var code = new RotationCode { Mode = (RotationMode)bits.Read(2) };
            switch (code.Mode)
            {
                case RotationMode.SameAsReference:
                    if (!allowSameAsReference) throw new DecodeException("motion keyframe uses the reference rotation mode");
                    break;
                case RotationMode.Yaw:
                    code.Yaw = (int)bits.Read(YawBits);
                    break;
                case RotationMode.YawPitch:
                    code.Yaw = (int)bits.Read(YawBits);
                    code.Pitch = bits.ReadSigned(PitchBits);
                    break;
                default:
                    code.Index = (int)bits.Read(2);
                    code.A = bits.ReadSigned(FullComponentBits);
                    code.B = bits.ReadSigned(FullComponentBits);
                    code.C = bits.ReadSigned(FullComponentBits);
                    if (code.A < -FullComponentMax || code.B < -FullComponentMax || code.C < -FullComponentMax)
                    {
                        throw new DecodeException("motion rotation component out of range");
                    }
                    break;
            }
            return code;
        }

        private static void DecodeYawPitch(int yawCode, int pitchCode, out double x, out double y, out double z, out double w)
        {
            double sy = SinHalfYaw[yawCode], cy = CosHalfYaw[yawCode];
            double sx = SinHalfPitch[pitchCode - PitchMin], cx = CosHalfPitch[pitchCode - PitchMin];
            // qY(yaw) · qX(−pitch) with qY = (0, sy, 0, cy) and qX = (sx, 0, 0, cx)
            x = cy * sx;
            y = sy * cx;
            z = -sy * sx;
            w = cy * cx;
        }

        private static RotationCode EncodeFull(double x, double y, double z, double w)
        {
            int index = 0;
            double largest = Math.Abs(x);
            if (Math.Abs(y) > largest) { index = 1; largest = Math.Abs(y); }
            if (Math.Abs(z) > largest) { index = 2; largest = Math.Abs(z); }
            if (Math.Abs(w) > largest) { index = 3; }
            double dropped = index == 0 ? x : index == 1 ? y : index == 2 ? z : w;
            if (dropped < 0)
            {
                // q and −q are the same rotation: keep the dropped component positive so it decodes as +sqrt(...)
                x = -x;
                y = -y;
                z = -z;
                w = -w;
            }
            double a, b, c;
            switch (index)
            {
                case 0: a = y; b = z; c = w; break;
                case 1: a = x; b = z; c = w; break;
                case 2: a = x; b = y; c = w; break;
                default: a = x; b = y; c = z; break;
            }
            return new RotationCode
            {
                Mode = RotationMode.Full,
                Index = index,
                A = QuantizeComponent(a),
                B = QuantizeComponent(b),
                C = QuantizeComponent(c)
            };
        }

        private static int QuantizeComponent(double v)
        {
            double scaled = Math.Round(v / InverseSqrt2 * FullComponentMax);
            if (scaled > FullComponentMax) return FullComponentMax;
            if (scaled < -FullComponentMax) return -FullComponentMax;
            return (int)scaled;
        }

        private static void DecodeFull(int index, int qa, int qb, int qc, out double x, out double y, out double z, out double w)
        {
            double a = qa * (InverseSqrt2 / FullComponentMax);
            double b = qb * (InverseSqrt2 / FullComponentMax);
            double c = qc * (InverseSqrt2 / FullComponentMax);
            double rest = 1.0 - (a * a + b * b + c * c);
            double d = rest > 0 ? Math.Sqrt(rest) : 0.0;
            switch (index)
            {
                case 0: x = d; y = a; z = b; w = c; break;
                case 1: x = a; y = d; z = b; w = c; break;
                case 2: x = a; y = b; z = d; w = c; break;
                default: x = a; y = b; z = c; w = d; break;
            }
            MotionMath.Normalize(ref x, ref y, ref z, ref w);
        }
    }
}
