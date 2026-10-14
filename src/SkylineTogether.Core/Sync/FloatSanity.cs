using System;
using System.Collections.Generic;
using System.Reflection;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Where the float fields of an unmanaged value type sit, so a value decoded from the wire can be checked
    /// before it is written into the game. A NaN or infinite position, rotation or curve point is never valid game data,
    /// and the game's Burst jobs (culling, search trees, geometry) do not survive one: they index cells computed from the
    /// coordinate, so a NaN becomes an out-of-range index and a native crash. Built once per type by reflection; the
    /// field offsets come from the caller (the game layer passes Unity's own field offsets, tests the marshalling ones).
    /// </summary>
    public sealed class FloatLayout
    {
        private const int MaxDepth = 8;
        private const string QuaternionType = "Unity.Mathematics.quaternion";

        /// <summary>Byte offsets of every float field (nested structs flattened).</summary>
        public readonly int[] Floats;

        /// <summary>Byte offsets of every quaternion field (four floats that must form a unit quaternion).</summary>
        public readonly int[] Quaternions;

        /// <summary>The largest magnitude a float field may have (float.MaxValue: finite only).</summary>
        public readonly float Bound;

        /// <summary>The size of the value in bytes the offsets refer to (the last float ends inside it).</summary>
        public readonly int MinSize;

        private FloatLayout(int[] floats, int[] quaternions, float bound)
        {
            Floats = floats;
            Quaternions = quaternions;
            Bound = bound;
            int size = 0;
            foreach (int f in floats) size = Math.Max(size, f + 4);
            foreach (int q in quaternions) size = Math.Max(size, q + 16);
            MinSize = size;
        }

        /// <summary>True when the type has no float field to check.</summary>
        public bool IsEmpty => Floats.Length == 0 && Quaternions.Length == 0;

        /// <summary>
        /// The float layout of <paramref name="type"/>: float fields at any depth of nested value types, quaternions
        /// recognised by their Unity.Mathematics type. <paramref name="offsetOf"/> gives a field's byte offset in its
        /// declaring type.
        /// </summary>
        public static FloatLayout Build(Type type, float bound, Func<FieldInfo, int> offsetOf)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (offsetOf == null) throw new ArgumentNullException(nameof(offsetOf));
            var floats = new List<int>();
            var quaternions = new List<int>();
            Collect(type, 0, 0, offsetOf, floats, quaternions);
            return new FloatLayout(floats.ToArray(), quaternions.ToArray(), bound > 0 ? bound : float.MaxValue);
        }

        private static void Collect(Type type, int baseOffset, int depth, Func<FieldInfo, int> offsetOf, List<int> floats, List<int> quaternions)
        {
            if (depth > MaxDepth) return;
            foreach (FieldInfo f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Type ft = f.FieldType;
                if (ft == typeof(float))
                {
                    floats.Add(baseOffset + offsetOf(f));
                    continue;
                }
                if (!ft.IsValueType || ft.IsPrimitive || ft.IsEnum || ft.IsPointer) continue;
                // fixed buffers are compiler-generated structs with one element field: not values of their own
                if (f.GetCustomAttributes(typeof(System.Runtime.CompilerServices.FixedBufferAttribute), false).Length > 0) continue;
                int at = baseOffset + offsetOf(f);
                if (ft.FullName == QuaternionType) quaternions.Add(at);
                Collect(ft, at, depth + 1, offsetOf, floats, quaternions);
            }
        }

        /// <summary>
        /// True when every float is finite and within <see cref="Bound"/> (or exactly float.MinValue/MaxValue, the game's
        /// sentinels) and every quaternion has unit length (or is all zero, a default).
        /// </summary>
        public unsafe bool IsSane(byte* value)
        {
            for (int i = 0; i < Floats.Length; i++)
            {
                float v = *(float*)(value + Floats[i]);
                if (float.IsNaN(v)) return false;
                // the game's own sentinels (float.MinValue for "no elevation" in area nodes, ...) are finite and kept
                if (v == float.MaxValue || v == float.MinValue) continue;
                if (v < -Bound || v > Bound) return false; // infinities exceed any bound
            }
            for (int i = 0; i < Quaternions.Length; i++)
            {
                float* q = (float*)(value + Quaternions[i]);
                float len2 = q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3];
                // |q| within 10% of 1 (the host sends normalised values); an all-zero quaternion is a default the game
                // itself may hold (it runs with it on the host too), so only a non-unit, non-zero one is corrupt
                if (len2 == 0f) continue;
                if (!(len2 >= 0.81f && len2 <= 1.21f)) return false;
            }
            return true;
        }

        /// <summary>The managed form of <see cref="IsSane(byte*)"/> for a value's bytes at <paramref name="offset"/>.</summary>
        public unsafe bool IsSane(byte[] bytes, int offset)
        {
            if (bytes == null || offset < 0 || offset > bytes.Length - MinSize) return false;
            fixed (byte* p = bytes) return IsSane(p + offset);
        }

        /// <summary>True when the unmanaged value passes <see cref="IsSane(byte*)"/>.</summary>
        public unsafe bool IsSane<T>(T value) where T : unmanaged
        {
            return IsSane((byte*)&value);
        }
    }

    /// <summary>
    /// The component types whose decoded values are checked with a <see cref="FloatLayout"/> before they are
    /// written (geometry the game's native jobs index by coordinate). Positions are bounded (a city is at most tens of
    /// kilometres wide); other floats of these types must only be finite.
    /// </summary>
    public static class FloatSanity
    {
        /// <summary>The largest coordinate a position may have, in metres (vanilla maps are 14 km wide).</summary>
        public const float PositionBound = 1_000_000f;

        /// <summary>w9/perf: the largest radius, height, multiplier or pollution a water source may have (finite and sane).</summary>
        public const float WaterSourceBound = 1_000_000f;

        private static readonly Dictionary<string, float> Checked = new Dictionary<string, float>(StringComparer.Ordinal)
        {
            { "Game.Objects.Transform", PositionBound },
            { "Game.Objects.Relative", PositionBound },
            { "Game.Net.Node", PositionBound },
            { "Game.Net.Curve", PositionBound },
            { "Game.Areas.Node", PositionBound },
            { "Game.Routes.Position", PositionBound },
            { "Game.Routes.CurveElement", PositionBound },
            { "Game.Net.Elevation", float.MaxValue },
            { "Game.Objects.Elevation", float.MaxValue },
            { "Game.Objects.Attached", float.MaxValue },
            { "Game.Net.EdgeLane", float.MaxValue },
            { "Game.Events.WeatherPhenomenon", float.MaxValue },
            { "Game.Simulation.WaterSourceData", WaterSourceBound }, // w9/perf (trust): radius, height, flow of a proposed water source
        };

        /// <summary>The checked type names (probed at startup).</summary>
        public static IEnumerable<string> CheckedTypeNames => Checked.Keys;

        /// <summary>The layout to check values of <paramref name="type"/> with, or null when the type is not checked.</summary>
        public static FloatLayout LayoutOf(Type type, Func<FieldInfo, int> offsetOf)
        {
            if (type == null || !Checked.TryGetValue(type.FullName ?? "", out float bound)) return null;
            FloatLayout layout = FloatLayout.Build(type, bound, offsetOf);
            return layout.IsEmpty ? null : layout;
        }
    }
}
