using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Intents
{
    /// <summary>
    /// One applied terrain brush stamp (ARCH §4.10.9, §5.3 <c>TerrainStroke</c>): the arguments of
    /// <c>TerrainSystem.ApplyBrush(TerraformingType type, Bounds2 area, Brush brush, Texture texture)</c>
    /// (<c>Game.Simulation/TerrainSystem.cs:3968</c>) with the <c>Brush</c> fields (<c>Game.Tools/Brush.cs</c>), the
    /// tool prefab of <c>Brush.m_Tool</c> (it selects the tool strength through <c>TerraformingBrushData</c> and the
    /// terraforming target: height, ore, oil, fertility, groundwater or material), the brush texture prefab, and the
    /// originator's clamped frame time. <c>ApplyBrush</c> scales every stamp by the caller's
    /// <c>Time.unscaledDeltaTime</c> clamped to [0.05, 1] (<c>TerrainSystem.cs:3766-3775</c>), so a replaying machine
    /// pre-scales the strength from <see cref="ClampedDeltaTime"/> (core-fields <c>BrushStrengthCompensation</c>).
    /// <para>
    /// Body (also the payload of a TERRAIN record when the host forwards stamps, ARCH §4.10.9):
    /// <code>
    /// u8 terraformingType | f32 posX posY posZ targetX targetY targetZ startX startY startZ angle size strength opacity
    /// | varint toolPrefab+1 | varint brushPrefab+1 | f32 areaMinX areaMinZ areaMaxX areaMaxZ | f32 clampedDeltaTime
    /// </code>
    /// Floats are raw IEEE-754 bits, so non-finite values survive the codec and are rejected by
    /// <c>ProposalValidator</c> (a protocol decoder rejects only malformed bytes).
    /// </para>
    /// </summary>
    public sealed class TerrainStamp
    {
        /// <summary>The largest defined <c>Game.Prefabs.TerraformingType</c> (Shift, Level, Soften, Slope).</summary>
        public const byte MaxTerraformingType = 3;

        /// <summary>Encoded size without the two prefab varints (1 type byte and 18 floats).</summary>
        public const int FixedBytes = 1 + 18 * 4;

        /// <summary><c>Game.Prefabs.TerraformingType</c>: 0 Shift, 1 Level, 2 Soften, 3 Slope.</summary>
        public byte TerraformingType;

        public float PosX, PosY, PosZ, TargetX, TargetY, TargetZ, StartX, StartY, StartZ, Angle, Size, Strength, Opacity;

        /// <summary><c>Brush.m_Tool</c> (tool strength lookup and terraforming target).</summary>
        public PrefabRank ToolPrefab = PrefabRank.None;

        /// <summary>The brush texture prefab.</summary>
        public PrefabRank BrushPrefab = PrefabRank.None;

        /// <summary>The <c>Bounds2 area</c> argument of <c>ApplyBrush</c> (x/z metres).</summary>
        public float AreaMinX, AreaMinZ, AreaMaxX, AreaMaxZ;

        /// <summary>The originator's <c>clamp(Time.unscaledDeltaTime, 0.05, 1)</c> (ARCH §4.10.9).</summary>
        public float ClampedDeltaTime;

        /// <summary>Appends the stamp body.</summary>
        public void Write(ByteWriter w)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            w.WriteByte(TerraformingType);
            w.WriteSingle(PosX);
            w.WriteSingle(PosY);
            w.WriteSingle(PosZ);
            w.WriteSingle(TargetX);
            w.WriteSingle(TargetY);
            w.WriteSingle(TargetZ);
            w.WriteSingle(StartX);
            w.WriteSingle(StartY);
            w.WriteSingle(StartZ);
            w.WriteSingle(Angle);
            w.WriteSingle(Size);
            w.WriteSingle(Strength);
            w.WriteSingle(Opacity);
            ProposalCodec.WritePrefab(w, ToolPrefab);
            ProposalCodec.WritePrefab(w, BrushPrefab);
            w.WriteSingle(AreaMinX);
            w.WriteSingle(AreaMinZ);
            w.WriteSingle(AreaMaxX);
            w.WriteSingle(AreaMaxZ);
            w.WriteSingle(ClampedDeltaTime);
        }

        /// <summary>Reads a stamp body written by <see cref="Write"/>. Throws <see cref="DecodeException"/>.</summary>
        public static TerrainStamp Read(ByteReader r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            if (r.Remaining < FixedBytes + 2) throw new DecodeException("terrain stamp: " + r.Remaining.ToString(CultureInfo.InvariantCulture) + " bytes, at least " + (FixedBytes + 2).ToString(CultureInfo.InvariantCulture) + " expected");
            var s = new TerrainStamp
            {
                TerraformingType = r.ReadByte(),
                PosX = r.ReadSingle(),
                PosY = r.ReadSingle(),
                PosZ = r.ReadSingle(),
                TargetX = r.ReadSingle(),
                TargetY = r.ReadSingle(),
                TargetZ = r.ReadSingle(),
                StartX = r.ReadSingle(),
                StartY = r.ReadSingle(),
                StartZ = r.ReadSingle(),
                Angle = r.ReadSingle(),
                Size = r.ReadSingle(),
                Strength = r.ReadSingle(),
                Opacity = r.ReadSingle()
            };
            s.ToolPrefab = ProposalCodec.ReadPrefab(r, "terrain stamp tool prefab");
            s.BrushPrefab = ProposalCodec.ReadPrefab(r, "terrain stamp brush prefab");
            s.AreaMinX = r.ReadSingle();
            s.AreaMinZ = r.ReadSingle();
            s.AreaMaxX = r.ReadSingle();
            s.AreaMaxZ = r.ReadSingle();
            s.ClampedDeltaTime = r.ReadSingle();
            return s;
        }

        /// <summary>Reads exactly one stamp body (a TERRAIN record payload): <see cref="Read"/> plus a check that nothing follows.</summary>
        public static TerrainStamp Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            TerrainStamp stamp = Read(r);
            if (!r.IsAtEnd) throw new DecodeException("terrain stamp: " + r.Remaining.ToString(CultureInfo.InvariantCulture) + " trailing byte(s)");
            return stamp;
        }

        /// <summary>True when every float field is finite (NaN and infinities are rejected by the validator).</summary>
        public bool AllFinite()
        {
            return ProposalCodec.IsFinite(PosX) && ProposalCodec.IsFinite(PosY) && ProposalCodec.IsFinite(PosZ)
                   && ProposalCodec.IsFinite(TargetX) && ProposalCodec.IsFinite(TargetY) && ProposalCodec.IsFinite(TargetZ)
                   && ProposalCodec.IsFinite(StartX) && ProposalCodec.IsFinite(StartY) && ProposalCodec.IsFinite(StartZ)
                   && ProposalCodec.IsFinite(Angle) && ProposalCodec.IsFinite(Size) && ProposalCodec.IsFinite(Strength)
                   && ProposalCodec.IsFinite(Opacity) && ProposalCodec.IsFinite(AreaMinX) && ProposalCodec.IsFinite(AreaMinZ)
                   && ProposalCodec.IsFinite(AreaMaxX) && ProposalCodec.IsFinite(AreaMaxZ) && ProposalCodec.IsFinite(ClampedDeltaTime);
        }

        public override string ToString()
        {
            var ci = CultureInfo.InvariantCulture;
            return "stamp type " + TerraformingType.ToString(ci) + " at (" + PosX.ToString("R", ci) + ", " + PosZ.ToString("R", ci) + ") size "
                   + Size.ToString("R", ci) + " strength " + Strength.ToString("R", ci) + " dt " + ClampedDeltaTime.ToString("R", ci);
        }
    }
}
