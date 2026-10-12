using System;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Sync
{
    /// <summary>How a mover exists on the host (ARCH §4.9.2, §4.9.8): which vanilla type set the client mirrors.</summary>
    public enum MoverKind : byte
    {
        /// <summary>The prefab's moving archetype (<c>ObjectData.m_Archetype</c>): driving or walking.</summary>
        Moving = 0,
        /// <summary>The prefab's stopped archetype (<c>MovingObjectData.m_StoppedArchetype</c>): parked cars and trains.</summary>
        Stopped = 1,
        /// <summary>A creature riding visibly in or on a vehicle (<c>CurrentVehicle</c> + <c>Relative</c>: drivers, cyclists).</summary>
        Riding = 2,
        /// <summary>A creature inside a vehicle (<c>CurrentVehicle</c> without <c>Relative</c>, normally hidden).</summary>
        Inside = 3,
    }

    /// <summary>
    /// Existence and discrete state of one mover (vehicle, trailer, train car, creature): the body of the
    /// <see cref="SyncRecordKind.MoverSpawn"/> and <see cref="SyncRecordKind.MoverState"/> records. Motion itself travels
    /// on the motion channel (<c>MessageKind.Motion</c>); this record carries the pose only as the last known
    /// <c>Transform</c> (spawn pose, parking pose, and the pose a mover outside every interest region is pinned at).
    /// Net ids are those of <c>NetIdSystem</c>; 0 means "none".
    /// </summary>
    public sealed class MoverRecord
    {
        public MoverKind Kind;
        public bool Unspawned;
        /// <summary>Created from the train controller archetypes (<c>TrainObjectData</c>).</summary>
        public bool TrainController;
        public uint PrefabNetId;
        /// <summary><c>UpdateFrame.m_Index</c> (0..15).</summary>
        public byte UpdateFrame;
        /// <summary><c>PseudoRandomSeed.m_Seed</c>.</summary>
        public ushort Seed;
        public float X, Y, Z;
        /// <summary>Rotation quaternion (x, y, z, w); travels as four signed 16-bit components.</summary>
        public float QX, QY, QZ, QW = 1f;
        /// <summary>The <c>Controller</c> of a trailer or train car (0: none or itself).</summary>
        public uint Controller;
        /// <summary>The <c>LayoutElement</c> list of a controller (null: none).</summary>
        public uint[] Layout;
        /// <summary><c>CurrentRoute.m_Route</c> (public transport).</summary>
        public uint Route;
        /// <summary><c>Human.m_Flags</c> when <see cref="HasHumanFlags"/>.</summary>
        public bool HasHumanFlags;
        public uint HumanFlags;
        /// <summary>For <see cref="MoverKind.Riding"/> and <see cref="MoverKind.Inside"/>: <c>CurrentVehicle.m_Vehicle</c> and <c>m_Flags</c>.</summary>
        public uint Vehicle;
        public uint VehicleFlags;
        /// <summary>For <see cref="MoverKind.Riding"/>: <c>Relative</c> (local position, rotation and bone index).</summary>
        public float RX, RY, RZ, RQX, RQY, RQZ, RQW = 1f;
        public int BoneX, BoneY, BoneZ;
    }

    /// <summary>Wire form of <see cref="MoverRecord"/> (inside a <see cref="SyncBatch"/>).</summary>
    /// <remarks>
    /// <code>
    /// u8 flags = kind (bits 0-1) | unspawned 4 | trainController 8 | controller 16 | layout 32 | route 64 | humanFlags 128
    /// varint prefab | u8 updateFrame | u16 seed | 3 × f32 position | 4 × i16 rotation
    /// [varint controller] [varint count, count × varint layout] [varint route] [u32 humanFlags]
    /// kind Riding/Inside: varint vehicle | u32 vehicleFlags; Riding: 3 × f32 | 4 × i16 | 3 × varint bone
    /// </code>
    /// </remarks>
    public static class MoverRecordCodec
    {
        public const int MaxLayout = 256;

        public static void Write(ByteWriter w, MoverRecord m)
        {
            if (m == null) throw new ArgumentNullException(nameof(m));
            if (m.Layout != null && m.Layout.Length > MaxLayout) throw new ArgumentException("layout too long", nameof(m));
            int flags = (int)m.Kind & 3;
            if (m.Unspawned) flags |= 4;
            if (m.TrainController) flags |= 8;
            if (m.Controller != 0) flags |= 16;
            if (m.Layout != null) flags |= 32;
            if (m.Route != 0) flags |= 64;
            if (m.HasHumanFlags) flags |= 128;
            w.WriteByte((byte)flags);
            w.WriteVarUInt32(m.PrefabNetId);
            w.WriteByte((byte)(m.UpdateFrame & 15));
            w.WriteUInt16(m.Seed);
            w.WriteSingle(Finite(m.X));
            w.WriteSingle(Finite(m.Y));
            w.WriteSingle(Finite(m.Z));
            WriteRotation(w, m.QX, m.QY, m.QZ, m.QW);
            if (m.Controller != 0) w.WriteVarUInt32(m.Controller);
            if (m.Layout != null)
            {
                w.WriteVarUInt32((uint)m.Layout.Length);
                foreach (uint id in m.Layout) w.WriteVarUInt32(id);
            }
            if (m.Route != 0) w.WriteVarUInt32(m.Route);
            if (m.HasHumanFlags) w.WriteUInt32(m.HumanFlags);
            if (m.Kind == MoverKind.Riding || m.Kind == MoverKind.Inside)
            {
                w.WriteVarUInt32(m.Vehicle);
                w.WriteUInt32(m.VehicleFlags);
                if (m.Kind == MoverKind.Riding)
                {
                    w.WriteSingle(Finite(m.RX));
                    w.WriteSingle(Finite(m.RY));
                    w.WriteSingle(Finite(m.RZ));
                    WriteRotation(w, m.RQX, m.RQY, m.RQZ, m.RQW);
                    w.WriteVarInt32(m.BoneX);
                    w.WriteVarInt32(m.BoneY);
                    w.WriteVarInt32(m.BoneZ);
                }
            }
        }

        /// <summary>Reads a record written by <see cref="Write"/>; malformed input throws <see cref="DecodeException"/>.</summary>
        public static MoverRecord Read(ByteReader r)
        {
            var m = new MoverRecord();
            int flags = r.ReadByte();
            m.Kind = (MoverKind)(flags & 3);
            m.Unspawned = (flags & 4) != 0;
            m.TrainController = (flags & 8) != 0;
            m.PrefabNetId = r.ReadVarUInt32();
            m.UpdateFrame = r.ReadByte();
            if (m.UpdateFrame > 15) throw new DecodeException("mover update frame " + m.UpdateFrame);
            m.Seed = r.ReadUInt16();
            m.X = ReadFinite(r);
            m.Y = ReadFinite(r);
            m.Z = ReadFinite(r);
            ReadRotation(r, out m.QX, out m.QY, out m.QZ, out m.QW);
            if ((flags & 16) != 0) m.Controller = r.ReadVarUInt32();
            if ((flags & 32) != 0)
            {
                uint count = r.ReadVarUInt32();
                if (count > MaxLayout) throw new DecodeException("mover layout of " + count + " vehicles");
                m.Layout = new uint[count];
                for (int i = 0; i < count; i++) m.Layout[i] = r.ReadVarUInt32();
            }
            if ((flags & 64) != 0) m.Route = r.ReadVarUInt32();
            if ((flags & 128) != 0)
            {
                m.HasHumanFlags = true;
                m.HumanFlags = r.ReadUInt32();
            }
            if (m.Kind == MoverKind.Riding || m.Kind == MoverKind.Inside)
            {
                m.Vehicle = r.ReadVarUInt32();
                m.VehicleFlags = r.ReadUInt32();
                if (m.Kind == MoverKind.Riding)
                {
                    m.RX = ReadFinite(r);
                    m.RY = ReadFinite(r);
                    m.RZ = ReadFinite(r);
                    ReadRotation(r, out m.RQX, out m.RQY, out m.RQZ, out m.RQW);
                    m.BoneX = r.ReadVarInt32();
                    m.BoneY = r.ReadVarInt32();
                    m.BoneZ = r.ReadVarInt32();
                }
            }
            return m;
        }

        private static float Finite(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;

        private static float ReadFinite(ByteReader r)
        {
            float v = r.ReadSingle();
            if (float.IsNaN(v) || float.IsInfinity(v)) throw new DecodeException("non-finite mover coordinate");
            return v;
        }

        private static void WriteRotation(ByteWriter w, float x, float y, float z, float qw)
        {
            double len = Math.Sqrt((double)x * x + (double)y * y + (double)z * z + (double)qw * qw);
            if (!(len > 1e-6) || double.IsInfinity(len))
            {
                x = 0f;
                y = 0f;
                z = 0f;
                qw = 1f;
                len = 1.0;
            }
            w.WriteUInt16(unchecked((ushort)Snorm(x / len)));
            w.WriteUInt16(unchecked((ushort)Snorm(y / len)));
            w.WriteUInt16(unchecked((ushort)Snorm(z / len)));
            w.WriteUInt16(unchecked((ushort)Snorm(qw / len)));
        }

        private static short Snorm(double v) => (short)Math.Round(Math.Max(-1.0, Math.Min(1.0, v)) * 32767.0);

        private static void ReadRotation(ByteReader r, out float x, out float y, out float z, out float w)
        {
            double a = unchecked((short)r.ReadUInt16()) / 32767.0;
            double b = unchecked((short)r.ReadUInt16()) / 32767.0;
            double c = unchecked((short)r.ReadUInt16()) / 32767.0;
            double d = unchecked((short)r.ReadUInt16()) / 32767.0;
            double len = Math.Sqrt(a * a + b * b + c * c + d * d);
            if (len < 1e-6)
            {
                x = 0f;
                y = 0f;
                z = 0f;
                w = 1f;
                return;
            }
            x = (float)(a / len);
            y = (float)(b / len);
            z = (float)(c / len);
            w = (float)(d / len);
        }
    }
}
