using System;
using System.Globalization;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Portable identity of a derived entity (lane, sub-object, secondary object, zone block, owned route and its
    /// elements, flow edge) that every peer regenerates locally and that therefore never gets a slot (docs/history/ARCHITECTURE-original.md §3.7).
    /// Floats are quantised to 1/64 m with <see cref="Quantize"/>, because float results may differ between CPUs.
    /// <para>Field use per kind (the constructors in core-identity's <c>DerivedKeys</c> are the authority):</para>
    /// <list type="bullet">
    /// <item><see cref="Owner"/>: the owner slot (for a flow edge: the owner of the start node).</item>
    /// <item><see cref="Prefab"/>: lane, sub-object, secondary-object or route prefab rank; <c>PrefabRank.None</c> if unused.</item>
    /// <item><see cref="Index"/>: aligned sub-object index, block side, lane flags or owned-route element index; -1 if unused.</item>
    /// <item><see cref="QX"/>, <see cref="QY"/>, <see cref="QZ"/>: quantised positions.</item>
    /// <item><see cref="A"/>, <see cref="B"/>, <see cref="C"/>: lane: start, middle and end <c>PathNode</c> with the owner
    /// index re-encoded as a slot; flow edge: end owner and role; owned-route element: A = 0 waypoint, 1 segment.</item>
    /// </list>
    /// </summary>
    public readonly struct DerivedKey : IEquatable<DerivedKey>
    {
        /// <summary>Quantisation step: 64 units per metre (1/64 m).</summary>
        public const int UnitsPerMetre = 64;

        public readonly DerivedKeyKind Kind;
        public readonly NetSlot Owner;
        public readonly PrefabRank Prefab;
        /// <summary>Aligned sub-object index, block side, lane flags, owned-route element index; -1 if unused.</summary>
        public readonly int Index;
        /// <summary>Positions quantised to 1/64 m.</summary>
        public readonly int QX, QY, QZ;
        /// <summary>Kind-specific values (lane path nodes, flow-edge end owner and role, owned-route element kind).</summary>
        public readonly ulong A, B, C;

        /// <summary>Creates a key; <paramref name="kind"/> must be one of the defined kinds 1-7.</summary>
        public DerivedKey(DerivedKeyKind kind, NetSlot owner, PrefabRank prefab, int index, int qx, int qy, int qz, ulong a, ulong b, ulong c)
        {
            if (!IsValidKind((byte)kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "a derived key kind is 1-7");
            }
            Kind = kind;
            Owner = owner;
            Prefab = prefab;
            Index = index;
            QX = qx;
            QY = qy;
            QZ = qz;
            A = a;
            B = b;
            C = c;
        }

        /// <summary>True when <paramref name="kind"/> is a defined <see cref="DerivedKeyKind"/> (decoders check wire subkinds with it).</summary>
        public static bool IsValidKind(byte kind) => kind >= (byte)DerivedKeyKind.Lane && kind <= (byte)DerivedKeyKind.OwnedRouteElement;

        /// <summary>
        /// Quantises a length in metres to 1/64 m: <c>(int)Math.Round(metres * 64)</c> (banker's rounding, as
        /// <c>Math.Round</c> does). Defensive for non-finite input: NaN gives 0, values beyond the <c>int</c> range clamp.
        /// </summary>
        public static int Quantize(float metres)
        {
            if (float.IsNaN(metres)) return 0;
            double scaled = Math.Round((double)metres * UnitsPerMetre);
            if (scaled >= int.MaxValue) return int.MaxValue;
            if (scaled <= int.MinValue) return int.MinValue;
            return (int)scaled;
        }

        public bool Equals(DerivedKey other)
        {
            return Kind == other.Kind && Owner == other.Owner && Prefab == other.Prefab && Index == other.Index
                   && QX == other.QX && QY == other.QY && QZ == other.QZ && A == other.A && B == other.B && C == other.C;
        }

        public override bool Equals(object obj) => obj is DerivedKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                ulong h = (ulong)Kind;
                h = h * 0x9E3779B185EBCA87UL + Owner.Value;
                h = h * 0x9E3779B185EBCA87UL + (uint)Prefab.Value;
                h = h * 0x9E3779B185EBCA87UL + (uint)Index;
                h = h * 0x9E3779B185EBCA87UL + (uint)QX;
                h = h * 0x9E3779B185EBCA87UL + (uint)QY;
                h = h * 0x9E3779B185EBCA87UL + (uint)QZ;
                h = h * 0x9E3779B185EBCA87UL + A;
                h = h * 0x9E3779B185EBCA87UL + B;
                h = h * 0x9E3779B185EBCA87UL + C;
                h ^= h >> 31;
                return (int)h ^ (int)(h >> 32);
            }
        }

        public override string ToString()
        {
            var ci = CultureInfo.InvariantCulture;
            return "key:" + Kind + "(" + Owner + ", " + Prefab + ", i=" + Index.ToString(ci)
                   + ", q=" + QX.ToString(ci) + "/" + QY.ToString(ci) + "/" + QZ.ToString(ci)
                   + ", a=" + A.ToString("X", ci) + ", b=" + B.ToString("X", ci) + ", c=" + C.ToString("X", ci) + ")";
        }

        public static bool operator ==(DerivedKey a, DerivedKey b) => a.Equals(b);

        public static bool operator !=(DerivedKey a, DerivedKey b) => !a.Equals(b);
    }
}
