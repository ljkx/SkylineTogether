using System;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Modding
{
    /// <summary>The requests a client's Placemaker adapter sends to the host.</summary>
    public enum PlacemakerMessageKind : byte
    {
        /// <summary>Create (if needed) and name an on-demand runtime prefab this client created (<see cref="PlacemakerNames"/>).</summary>
        EnsurePrefab = 1,
        /// <summary>Create the park companion building of a group this client built.</summary>
        ParkCompanion = 2,
        /// <summary>Create the parking companion building of a group this client built (Placemaker's experimental option).</summary>
        ParkingCompanion = 3,
    }

    /// <summary>
    /// The payload of Skyline Together's own channel for Placemaker (docs/design/MOD-COMPATIBILITY.md §2), carried by the
    /// mod channel (<c>MessageKind.ModChannel</c>, channel <see cref="Channel"/>; no wire change). The first byte is the
    /// kind, so viewers (who may not send on it) are refused by the usual channel permissions:
    /// <code>
    /// 1 EnsurePrefab:  u8 kind | string type (≤ 64 UTF-8 bytes) | string name (≤ 400 UTF-8 bytes, a valid on-demand name)
    /// 2 ParkCompanion: u8 kind | i32 group (> 0) | string name (a park name) | f32 x | f32 z | f32 yaw
    /// 3 ParkingCompanion: u8 kind | i32 group (> 0)
    /// </code>
    /// Decoding validates everything (names by <see cref="PlacemakerNames"/>, finite coordinates within
    /// <see cref="MaxCoordinate"/>, the yaw within ±2π); <see cref="TryRead"/> never throws. Pure.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class PlacemakerMessage
    {
        /// <summary>The mod channel name (not an assembly name, so it cannot collide with a mod's own channel).</summary>
        public const string Channel = "SkylineTogether.Placemaker";

        public const int MaxTypeBytes = 64;
        public const int MaxNameBytes = 2 * PlacemakerNames.MaxNameLength;

        /// <summary>Largest accepted world coordinate (the map is about 14.3 km wide, centred on 0).</summary>
        public const float MaxCoordinate = 20000f;

        public PlacemakerMessageKind Kind;
        public string PrefabType;
        public string PrefabName;
        public int Group;
        public float X;
        public float Z;
        public float Yaw;

        public static PlacemakerMessage EnsurePrefab(string type, string name) => new PlacemakerMessage { Kind = PlacemakerMessageKind.EnsurePrefab, PrefabType = type, PrefabName = name };

        public static PlacemakerMessage ParkCompanion(int group, string name, float x, float z, float yaw) => new PlacemakerMessage
        {
            Kind = PlacemakerMessageKind.ParkCompanion, Group = group, PrefabName = name, X = x, Z = z, Yaw = yaw,
        };

        public static PlacemakerMessage ParkingCompanion(int group) => new PlacemakerMessage { Kind = PlacemakerMessageKind.ParkingCompanion, Group = group };

        /// <summary>The payload; throws <see cref="ArgumentException"/> for a message <see cref="IsValid"/> refuses.</summary>
        public byte[] ToBytes()
        {
            if (!IsValid()) throw new ArgumentException("invalid Placemaker message (" + Kind + ", " + (PrefabName ?? "null") + ")");
            var w = new ByteWriter(64);
            w.WriteByte((byte)Kind);
            if (Kind == PlacemakerMessageKind.EnsurePrefab)
            {
                w.WriteString(PrefabType, MaxTypeBytes);
                w.WriteString(PrefabName, MaxNameBytes);
            }
            else if (Kind == PlacemakerMessageKind.ParkingCompanion)
            {
                w.WriteInt32(Group);
            }
            else
            {
                w.WriteInt32(Group);
                w.WriteString(PrefabName, MaxNameBytes);
                w.WriteSingle(X);
                w.WriteSingle(Z);
                w.WriteSingle(Yaw);
            }
            return w.ToArray();
        }

        /// <summary>Decodes and validates a payload; false (and null) for anything malformed, unknown or out of range.</summary>
        public static bool TryRead(byte[] payload, out PlacemakerMessage message)
        {
            message = null;
            if (payload == null || payload.Length == 0) return false;
            try
            {
                var r = new ByteReader(payload, 0, payload.Length);
                var m = new PlacemakerMessage { Kind = (PlacemakerMessageKind)r.ReadByte() };
                switch (m.Kind)
                {
                    case PlacemakerMessageKind.EnsurePrefab:
                        m.PrefabType = r.ReadString(MaxTypeBytes);
                        m.PrefabName = r.ReadString(MaxNameBytes);
                        break;
                    case PlacemakerMessageKind.ParkCompanion:
                        m.Group = r.ReadInt32();
                        m.PrefabName = r.ReadString(MaxNameBytes);
                        m.X = r.ReadSingle();
                        m.Z = r.ReadSingle();
                        m.Yaw = r.ReadSingle();
                        break;
                    case PlacemakerMessageKind.ParkingCompanion:
                        m.Group = r.ReadInt32();
                        break;
                    default:
                        return false;
                }
                if (!r.IsAtEnd || !m.IsValid()) return false;
                message = m;
                return true;
            }
            catch (DecodeException)
            {
                return false;
            }
        }

        /// <summary>The checks both ends apply.</summary>
        public bool IsValid()
        {
            switch (Kind)
            {
                case PlacemakerMessageKind.EnsurePrefab:
                    if (string.IsNullOrEmpty(PrefabType) || PrefabType.Length > MaxTypeBytes) return false;
                    return PlacemakerNames.Classify(PrefabType, PrefabName) != PlacemakerPrefabKind.None;
                case PlacemakerMessageKind.ParkCompanion:
                    return Group > 0 && PlacemakerNames.TryParsePark(PrefabName, out _) && Coordinate(X) && Coordinate(Z)
                           && !float.IsNaN(Yaw) && Math.Abs(Yaw) <= 2 * Math.PI + 0.001;
                case PlacemakerMessageKind.ParkingCompanion:
                    return Group > 0;
                default:
                    return false;
            }
        }

        private static bool Coordinate(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) <= MaxCoordinate;
    }
}
