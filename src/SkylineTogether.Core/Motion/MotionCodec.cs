using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Motion
{
    /// <summary>
    /// Wire encodings of mover frames (ARCH §4.9.3, R15 §9.1): Q-B keyframes and keyframe-referenced deltas for the
    /// per-tick datagrams, and the Q-A interest-entry record with all four <c>TransformFrame</c> slots.
    /// <para>
    /// <b>Q-B record</b>: <c>u16 interestSlot | u8 hdr</c> (see <see cref="QbHeader"/>), then a bit-packed body that
    /// ends on a byte boundary with zero padding, so every record decodes on its own and a receiver can skip a record
    /// whose reference it lacks.
    /// </para>
    /// <code>
    /// keyframe body:
    ///   position  1 bit: 0 = x 22, y 20, z 22 bits signed in 1/64 m (±32 km, ±8 km)   1 = three raw f32
    ///   rotation  2 bits: 1 = yaw 12 | 2 = yaw 12 + pitch 7 (π/256) | 3 = smallest-three 2 + 3 × 12   (0 invalid)
    ///   velocity  2 bits: 0 = zero | 1 = speed along the decoded forward axis, 11 bits signed in 1/8 m/s
    ///                     | 2 = three f16 (exact)   (3 invalid)
    ///   flags     18 bits, then 1 bit "high bits follow" + 14 bits
    ///   state     1 bit: 0 = state, activity and timer zero | 1 = state 3 bits (7: + 8 bits), activity 8, timer 16
    /// delta body (against the reference frame at tick T − 16·refAge):
    ///   position  2 bits: 0/1/2 = (dx, dy, dz) in 1/64 m with (8, 6, 8) / (12, 10, 12) / (17, 14, 17) bits,
    ///                     3 = absolute escape (1 bit + the keyframe position)
    ///   rotation  2 bits: 0 = the reference rotation, otherwise as in keyframes
    ///   velocity  2 bits: 0, 1, 2 as in keyframes, 3 = the reference velocity
    ///   flags     1 bit "unchanged since the reference", else as in keyframes
    ///   state     2 bits: 0 = unchanged | 1 = state and activity unchanged, timer = reference timer + refAge
    ///                     | 2 = state and activity unchanged, timer 16 bits | 3 = absolute
    /// </code>
    /// <para>
    /// Deltas reference positions by their 1/64 m grid index, so a decoded position is always the host position
    /// rounded to the grid (error ≤ 1/128 m per axis) and errors never accumulate. The "same as reference" modes
    /// compare the decoded values bit for bit; the reference passed to <see cref="WriteQBDelta"/> must therefore be the
    /// frame exactly as the receiver decoded it (the output of <see cref="TryReadQB"/> or <see cref="ReadQA"/> for the
    /// reference record).
    /// </para>
    /// <para>
    /// Writers throw <see cref="ArgumentException"/> for invalid arguments (non-finite frames, generations above 15,
    /// the reserved interest slot 0xFFFF); readers throw only <see cref="DecodeException"/>. Stateless and thread-safe.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class MotionCodec
    {
        /// <summary>Position resolution of keyframes and deltas.</summary>
        public const float PositionQuantum = 1f / 64f;

        /// <summary>A keyframe on every fourth sample of a mover (64 ticks, the life of the <c>TransformFrame</c> ring).</summary>
        public const int KeyframeEvery = 4;

        internal const int AbsoluteXZBits = 22;
        internal const int AbsoluteYBits = 20;
        internal const int SpeedBits = 11;
        internal const double SpeedScale = 8.0;
        internal const int FlagsLowBits = 18;
        internal const int FlagsHighBits = 14;

        private const int SpeedMin = -1024, SpeedMax = 1023;
        private const int StateBits = 3, StateEscape = 7, ActivityBits = 8, TimerBits = 16;
        private const int MaxRefAge = 7;

        // velocity: speed mode is used while |v − forward·s| ≤ 0.05 m/s + 14 % of |v| (about 8°); the Hermite tangent
        // is v·4/45 s (ObjectInterpolateSystem.cs:4354), so such a direction error moves the curve by a few centimetres
        private const double SpeedModeAbsoluteTolerance = 0.05;
        private const double SpeedModeRelativeTolerance = 0.14;
        private const double ZeroVelocitySquared = 1.0 / 256.0 / 256.0;

        private const byte VelocityZero = 0, VelocitySpeed = 1, VelocityVector = 2, VelocitySameAsReference = 3;
        private const byte StateUnchanged = 0, StateTimerAdvanced = 1, StateTimerExplicit = 2, StateAbsolute = 3;
        private const int PositionAbsoluteMode = 3;

        private static readonly int[] DeltaXZBits = { 8, 12, 17 };
        private static readonly int[] DeltaYBits = { 6, 10, 14 };

        private struct VelocityCode
        {
            public byte Mode;
            public int Speed;
            public ushort HX, HY, HZ;
        }

        private struct DeltaFields
        {
            public int PositionMode;
            public int DX, DY, DZ;
            public float RX, RY, RZ;
            public RotationCode Rotation;
            public VelocityCode Velocity;
            public bool FlagsUnchanged;
            public uint Flags;
            public byte StateMode;
            public byte State, Activity;
            public ushort Timer;
        }

        // ------------------------------------------------------------------------------------------------ Q-B writers

        /// <summary>Appends a self-contained Q-B keyframe. <paramref name="exactVelocity"/> sends the f16 velocity vector.</summary>
        public static void WriteQBKeyframe(ByteWriter w, ushort interestSlot, byte assignGeneration, in MotionFrame frame, bool exactVelocity)
        {
            CheckWriter(w);
            CheckInterestSlot(interestSlot);
            CheckGeneration(assignGeneration);
            CheckFrame(frame, nameof(frame));
            w.WriteUInt16(interestSlot);
            w.WriteByte((byte)(1 | (assignGeneration << 4)));
            var bits = new MotionBitWriter(w);
            WriteKeyframeBody(ref bits, frame, exactVelocity);
            bits.Flush();
        }

        /// <summary>
        /// Appends a Q-B delta against <paramref name="reference"/>, the receiver's decoded frame of tick
        /// <c>T − 16·<paramref name="refAge"/></c> (1..7). A position too far from the reference uses the absolute
        /// escape; the host encoder sends a keyframe instead in that case.
        /// </summary>
        public static void WriteQBDelta(ByteWriter w, ushort interestSlot, byte assignGeneration, byte refAge, in MotionFrame frame, in MotionFrame reference, bool exactVelocity)
        {
            WriteDeltaRecord(w, interestSlot, assignGeneration, refAge, frame, reference, exactVelocity);
        }

        /// <summary>As <see cref="WriteQBDelta"/>; returns true when the position needed the absolute escape.</summary>
        internal static bool WriteDeltaRecord(ByteWriter w, ushort interestSlot, byte assignGeneration, byte refAge, in MotionFrame frame, in MotionFrame reference, bool exactVelocity)
        {
            CheckWriter(w);
            CheckInterestSlot(interestSlot);
            CheckGeneration(assignGeneration);
            if (refAge < 1 || refAge > MaxRefAge) throw new ArgumentOutOfRangeException(nameof(refAge), refAge, "refAge is 1..7 steps of 16 ticks");
            CheckFrame(frame, nameof(frame));
            CheckFrame(reference, nameof(reference));
            w.WriteUInt16(interestSlot);
            w.WriteByte((byte)((refAge << 1) | (assignGeneration << 4)));
            var bits = new MotionBitWriter(w);
            bool escaped = WriteDeltaBody(ref bits, frame, reference, refAge, exactVelocity);
            bits.Flush();
            return escaped;
        }

        // ------------------------------------------------------------------------------------------------ Q-B reader

        /// <summary>
        /// Reads one Q-B record of a datagram for tick <paramref name="tick"/>. Keyframes always decode. A delta is
        /// decoded against <paramref name="references"/>; when the reference is missing (or <paramref name="references"/>
        /// is null) the record is still consumed and the method returns false: the caller drops it (ARCH §4.9.3).
        /// <paramref name="header"/> is filled in either case. Malformed input throws <see cref="DecodeException"/>.
        /// </summary>
        public static bool TryReadQB(ByteReader r, uint tick, IMotionReferences references, out QbHeader header, out MotionFrame frame)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            ushort interestSlot = r.ReadUInt16();
            byte hdr = r.ReadByte();
            header = new QbHeader
            {
                InterestSlot = interestSlot,
                Keyframe = (hdr & 1) != 0,
                RefAge = (byte)((hdr >> 1) & 7),
                AssignGeneration = (byte)(hdr >> 4)
            };
            if (interestSlot == MotionWire.ReservedInterestSlot) throw new DecodeException("motion record uses the reserved interest slot 0xFFFF");
            var bits = new MotionBitReader(r);
            if (header.Keyframe)
            {
                if (header.RefAge != 0) throw new DecodeException("motion keyframe with a reference age");
                ReadKeyframeBody(ref bits, out frame);
                bits.End();
                return true;
            }
            if (header.RefAge == 0) throw new DecodeException("motion delta without a reference age");
            ReadDeltaFields(ref bits, out DeltaFields fields);
            bits.End();
            uint referenceTick = unchecked(tick - (uint)(MotionMath.TicksPerStep * header.RefAge));
            if (references == null || !references.TryGetReference(interestSlot, header.AssignGeneration, referenceTick, out MotionFrame reference))
            {
                frame = default;
                return false;
            }
            return ApplyDelta(fields, reference, header.RefAge, out frame);
        }

        // ------------------------------------------------------------------------------------------------ Q-A record

        /// <summary>
        /// Appends the interest-entry record: <c>u16 interestSlot | u8 generation | varint slot | u8 updateFrame |
        /// u32 tick₀ | 3 × varint (tickᵢ − tick₀) | 4 × keyframe body</c>. The four frames are the mover's
        /// <c>TransformFrame</c> slots with their host ticks; every tick must be ≡ <paramref name="updateFrame"/> (mod 16)
        /// and the ticks must differ. The assignment is valid from the tick after the newest baseline frame.
        /// </summary>
        public static void WriteQA(ByteWriter w, ushort interestSlot, byte assignGeneration, NetSlot slot, byte updateFrame, MotionFrame[] fourSlots, uint[] fourTicks)
        {
            CheckWriter(w);
            CheckInterestSlot(interestSlot);
            CheckGeneration(assignGeneration);
            if (slot.IsNone) throw new ArgumentException("the mover slot must not be none", nameof(slot));
            if (updateFrame > 15) throw new ArgumentOutOfRangeException(nameof(updateFrame), updateFrame, "UpdateFrame is 0..15");
            CheckBaselineArrays(fourSlots, fourTicks);
            string problem = DescribeBaselineProblem(updateFrame, fourTicks);
            if (problem != null) throw new ArgumentException(problem, nameof(fourTicks));
            for (int i = 0; i < 4; i++) CheckFrame(fourSlots[i], nameof(fourSlots));

            w.WriteUInt16(interestSlot);
            w.WriteByte(assignGeneration);
            w.WriteVarUInt32(slot.Value);
            w.WriteByte(updateFrame);
            w.WriteUInt32(fourTicks[0]);
            for (int i = 1; i < 4; i++) w.WriteVarInt32(MotionMath.TickDiff(fourTicks[i], fourTicks[0]));
            for (int i = 0; i < 4; i++)
            {
                var bits = new MotionBitWriter(w);
                WriteKeyframeBody(ref bits, fourSlots[i], exactVelocity: false);
                bits.Flush();
            }
        }

        /// <summary>Reads a record written by <see cref="WriteQA"/> into the caller's arrays (at least four entries each).</summary>
        public static void ReadQA(ByteReader r, out ushort interestSlot, out byte assignGeneration, out NetSlot slot, out byte updateFrame, MotionFrame[] fourSlots, uint[] fourTicks)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            CheckBaselineArrays(fourSlots, fourTicks);
            interestSlot = r.ReadUInt16();
            if (interestSlot == MotionWire.ReservedInterestSlot) throw new DecodeException("motion assignment uses the reserved interest slot 0xFFFF");
            assignGeneration = r.ReadByte();
            if (assignGeneration > MotionWire.GenerationMask)
            {
                throw new DecodeException("motion assignment generation " + assignGeneration.ToString(CultureInfo.InvariantCulture) + " has more than 4 bits");
            }
            uint slotValue = r.ReadVarUInt32();
            if (slotValue == 0 || !NetSlot.TryCreate(slotValue, out slot))
            {
                throw new DecodeException("motion assignment for invalid slot " + slotValue.ToString(CultureInfo.InvariantCulture));
            }
            updateFrame = r.ReadByte();
            if (updateFrame > 15) throw new DecodeException("motion assignment UpdateFrame " + updateFrame.ToString(CultureInfo.InvariantCulture) + " is above 15");
            uint tick0 = r.ReadUInt32();
            fourTicks[0] = tick0;
            for (int i = 1; i < 4; i++) fourTicks[i] = unchecked(tick0 + (uint)r.ReadVarInt32());
            string problem = DescribeBaselineProblem(updateFrame, fourTicks);
            if (problem != null) throw new DecodeException("motion assignment: " + problem);
            for (int i = 0; i < 4; i++)
            {
                var bits = new MotionBitReader(r);
                ReadKeyframeBody(ref bits, out fourSlots[i]);
                bits.End();
            }
        }

        /// <summary>Null when the four ticks are distinct and congruent to the update frame (mod 16), else the reason.</summary>
        internal static string DescribeBaselineProblem(byte updateFrame, uint[] fourTicks)
        {
            for (int i = 0; i < 4; i++)
            {
                if ((fourTicks[i] & 15u) != updateFrame)
                {
                    return "baseline tick " + fourTicks[i].ToString(CultureInfo.InvariantCulture) + " is not congruent to UpdateFrame "
                           + updateFrame.ToString(CultureInfo.InvariantCulture) + " (mod 16)";
                }
                for (int j = 0; j < i; j++)
                {
                    if (fourTicks[i] == fourTicks[j]) return "baseline tick " + fourTicks[i].ToString(CultureInfo.InvariantCulture) + " appears twice";
                }
            }
            return null;
        }

        // ------------------------------------------------------------------------------------------------ bodies

        private static void WriteKeyframeBody(ref MotionBitWriter bits, in MotionFrame frame, bool exactVelocity)
        {
            WriteAbsolutePosition(ref bits, frame);
            RotationCode rotation = MotionRotation.Encode(frame.QX, frame.QY, frame.QZ, frame.QW);
            MotionRotation.Write(ref bits, rotation);
            MotionRotation.Decode(rotation, out float qx, out float qy, out float qz, out float qw);
            VelocityCode velocity = EncodeVelocity(frame, qx, qy, qz, qw, exactVelocity);
            WriteVelocity(ref bits, velocity);
            WriteFlags(ref bits, frame.Flags);
            bool hasState = frame.State != 0 || frame.Activity != 0 || frame.StateTimer != 0;
            bits.WriteBool(hasState);
            if (hasState) WriteStateAbsolute(ref bits, frame.State, frame.Activity, frame.StateTimer);
        }

        private static void ReadKeyframeBody(ref MotionBitReader bits, out MotionFrame frame)
        {
            frame = default;
            ReadAbsolutePosition(ref bits, ref frame);
            RotationCode rotation = MotionRotation.Read(ref bits, allowSameAsReference: false);
            MotionRotation.Decode(rotation, out frame.QX, out frame.QY, out frame.QZ, out frame.QW);
            byte mode = (byte)bits.Read(2);
            if (mode == VelocitySameAsReference) throw new DecodeException("motion keyframe uses the reference velocity mode");
            VelocityCode velocity = ReadVelocityPayload(ref bits, mode);
            DecodeVelocity(velocity, frame.QX, frame.QY, frame.QZ, frame.QW, out frame.VX, out frame.VY, out frame.VZ);
            frame.Flags = ReadFlags(ref bits);
            if (bits.ReadBool())
            {
                ReadStateAbsolute(ref bits, out frame.State, out frame.Activity, out frame.StateTimer);
            }
        }

        private static bool WriteDeltaBody(ref MotionBitWriter bits, in MotionFrame frame, in MotionFrame reference, int refAge, bool exactVelocity)
        {
            // position: grid index differences against the reference's grid index
            bool escaped = true;
            long qx = MotionMath.QuantizePosition(frame.X), qy = MotionMath.QuantizePosition(frame.Y), qz = MotionMath.QuantizePosition(frame.Z);
            long rx = MotionMath.QuantizePosition(reference.X), ry = MotionMath.QuantizePosition(reference.Y), rz = MotionMath.QuantizePosition(reference.Z);
            if (InAbsoluteRange(qx, qy, qz) && InAbsoluteRange(rx, ry, rz))
            {
                long dx = qx - rx, dy = qy - ry, dz = qz - rz;
                for (int mode = 0; mode < DeltaXZBits.Length; mode++)
                {
                    int xzBits = DeltaXZBits[mode], yBits = DeltaYBits[mode];
                    if (FitsSigned(dx, xzBits) && FitsSigned(dy, yBits) && FitsSigned(dz, xzBits))
                    {
                        bits.Write((uint)mode, 2);
                        bits.WriteSigned((int)dx, xzBits);
                        bits.WriteSigned((int)dy, yBits);
                        bits.WriteSigned((int)dz, xzBits);
                        escaped = false;
                        break;
                    }
                }
            }
            if (escaped)
            {
                bits.Write(PositionAbsoluteMode, 2);
                WriteAbsolutePosition(ref bits, frame);
            }

            // rotation: "same as the reference" when the decoded rotation is bit-identical
            RotationCode rotation = MotionRotation.Encode(frame.QX, frame.QY, frame.QZ, frame.QW);
            MotionRotation.Decode(rotation, out float qxr, out float qyr, out float qzr, out float qwr);
            if (MotionMath.SameBits(qxr, reference.QX) && MotionMath.SameBits(qyr, reference.QY)
                && MotionMath.SameBits(qzr, reference.QZ) && MotionMath.SameBits(qwr, reference.QW))
            {
                bits.Write((uint)RotationMode.SameAsReference, 2);
            }
            else
            {
                MotionRotation.Write(ref bits, rotation);
            }

            // velocity: "same as the reference" when the decoded velocity is bit-identical
            VelocityCode velocity = EncodeVelocity(frame, qxr, qyr, qzr, qwr, exactVelocity);
            if (velocity.Mode != VelocityZero)
            {
                DecodeVelocityUnchecked(velocity, qxr, qyr, qzr, qwr, out float vx, out float vy, out float vz);
                if (MotionMath.SameBits(vx, reference.VX) && MotionMath.SameBits(vy, reference.VY) && MotionMath.SameBits(vz, reference.VZ))
                {
                    velocity = new VelocityCode { Mode = VelocitySameAsReference };
                }
            }
            WriteVelocity(ref bits, velocity);

            // flags
            if (frame.Flags == reference.Flags)
            {
                bits.WriteBool(true);
            }
            else
            {
                bits.WriteBool(false);
                WriteFlags(ref bits, frame.Flags);
            }

            // state
            if (frame.State == reference.State && frame.Activity == reference.Activity)
            {
                if (frame.StateTimer == reference.StateTimer)
                {
                    bits.Write(StateUnchanged, 2);
                }
                else if (frame.StateTimer == unchecked((ushort)(reference.StateTimer + refAge)))
                {
                    bits.Write(StateTimerAdvanced, 2);
                }
                else
                {
                    bits.Write(StateTimerExplicit, 2);
                    bits.Write(frame.StateTimer, TimerBits);
                }
            }
            else
            {
                bits.Write(StateAbsolute, 2);
                WriteStateAbsolute(ref bits, frame.State, frame.Activity, frame.StateTimer);
            }
            return escaped;
        }

        private static void ReadDeltaFields(ref MotionBitReader bits, out DeltaFields fields)
        {
            fields = default;
            fields.PositionMode = (int)bits.Read(2);
            if (fields.PositionMode < PositionAbsoluteMode)
            {
                int xzBits = DeltaXZBits[fields.PositionMode], yBits = DeltaYBits[fields.PositionMode];
                fields.DX = bits.ReadSigned(xzBits);
                fields.DY = bits.ReadSigned(yBits);
                fields.DZ = bits.ReadSigned(xzBits);
            }
            else
            {
                var position = default(MotionFrame);
                ReadAbsolutePosition(ref bits, ref position);
                fields.RX = position.X;
                fields.RY = position.Y;
                fields.RZ = position.Z;
            }
            fields.Rotation = MotionRotation.Read(ref bits, allowSameAsReference: true);
            byte velocityMode = (byte)bits.Read(2);
            fields.Velocity = velocityMode == VelocitySameAsReference
                ? new VelocityCode { Mode = VelocitySameAsReference }
                : ReadVelocityPayload(ref bits, velocityMode);
            fields.FlagsUnchanged = bits.ReadBool();
            if (!fields.FlagsUnchanged) fields.Flags = ReadFlags(ref bits);
            fields.StateMode = (byte)bits.Read(2);
            switch (fields.StateMode)
            {
                case StateTimerExplicit:
                    fields.Timer = (ushort)bits.Read(TimerBits);
                    break;
                case StateAbsolute:
                    ReadStateAbsolute(ref bits, out fields.State, out fields.Activity, out fields.Timer);
                    break;
            }
        }

        private static bool ApplyDelta(in DeltaFields fields, in MotionFrame reference, int refAge, out MotionFrame frame)
        {
            frame = default;
            if (fields.PositionMode < PositionAbsoluteMode)
            {
                frame.X = MotionMath.DequantizePosition(MotionMath.QuantizePosition(reference.X) + fields.DX);
                frame.Y = MotionMath.DequantizePosition(MotionMath.QuantizePosition(reference.Y) + fields.DY);
                frame.Z = MotionMath.DequantizePosition(MotionMath.QuantizePosition(reference.Z) + fields.DZ);
            }
            else
            {
                frame.X = fields.RX;
                frame.Y = fields.RY;
                frame.Z = fields.RZ;
            }

            if (fields.Rotation.Mode == RotationMode.SameAsReference)
            {
                frame.QX = reference.QX;
                frame.QY = reference.QY;
                frame.QZ = reference.QZ;
                frame.QW = reference.QW;
            }
            else
            {
                MotionRotation.Decode(fields.Rotation, out frame.QX, out frame.QY, out frame.QZ, out frame.QW);
            }

            if (fields.Velocity.Mode == VelocitySameAsReference)
            {
                frame.VX = reference.VX;
                frame.VY = reference.VY;
                frame.VZ = reference.VZ;
            }
            else
            {
                DecodeVelocity(fields.Velocity, frame.QX, frame.QY, frame.QZ, frame.QW, out frame.VX, out frame.VY, out frame.VZ);
            }

            frame.Flags = fields.FlagsUnchanged ? reference.Flags : fields.Flags;
            switch (fields.StateMode)
            {
                case StateUnchanged:
                    frame.State = reference.State;
                    frame.Activity = reference.Activity;
                    frame.StateTimer = reference.StateTimer;
                    break;
                case StateTimerAdvanced:
                    frame.State = reference.State;
                    frame.Activity = reference.Activity;
                    frame.StateTimer = unchecked((ushort)(reference.StateTimer + refAge));
                    break;
                case StateTimerExplicit:
                    frame.State = reference.State;
                    frame.Activity = reference.Activity;
                    frame.StateTimer = fields.Timer;
                    break;
                default:
                    frame.State = fields.State;
                    frame.Activity = fields.Activity;
                    frame.StateTimer = fields.Timer;
                    break;
            }
            // a reference supplied by a foreign IMotionReferences could hold garbage: never hand out a non-finite frame
            return MotionMath.IsFinite(frame);
        }

        // ------------------------------------------------------------------------------------------------ fields

        private static bool InAbsoluteRange(long qx, long qy, long qz)
        {
            return FitsSigned(qx, AbsoluteXZBits) && FitsSigned(qy, AbsoluteYBits) && FitsSigned(qz, AbsoluteXZBits);
        }

        private static bool FitsSigned(long value, int bitCount)
        {
            long limit = 1L << (bitCount - 1);
            return value >= -limit && value < limit;
        }

        private static void WriteAbsolutePosition(ref MotionBitWriter bits, in MotionFrame frame)
        {
            long qx = MotionMath.QuantizePosition(frame.X), qy = MotionMath.QuantizePosition(frame.Y), qz = MotionMath.QuantizePosition(frame.Z);
            if (InAbsoluteRange(qx, qy, qz))
            {
                bits.WriteBool(false);
                bits.WriteSigned((int)qx, AbsoluteXZBits);
                bits.WriteSigned((int)qy, AbsoluteYBits);
                bits.WriteSigned((int)qz, AbsoluteXZBits);
            }
            else
            {
                // far outside the world (more than 32 km out): raw floats keep the value instead of wrapping it
                bits.WriteBool(true);
                bits.Write(MotionMath.SingleToBits(frame.X), 32);
                bits.Write(MotionMath.SingleToBits(frame.Y), 32);
                bits.Write(MotionMath.SingleToBits(frame.Z), 32);
            }
        }

        /// <summary>Reads an absolute position into the frame; returns true for the raw form.</summary>
        private static bool ReadAbsolutePosition(ref MotionBitReader bits, ref MotionFrame frame)
        {
            if (!bits.ReadBool())
            {
                frame.X = MotionMath.DequantizePosition(bits.ReadSigned(AbsoluteXZBits));
                frame.Y = MotionMath.DequantizePosition(bits.ReadSigned(AbsoluteYBits));
                frame.Z = MotionMath.DequantizePosition(bits.ReadSigned(AbsoluteXZBits));
                return false;
            }
            frame.X = MotionMath.BitsToSingle(bits.Read(32));
            frame.Y = MotionMath.BitsToSingle(bits.Read(32));
            frame.Z = MotionMath.BitsToSingle(bits.Read(32));
            if (!MotionMath.IsFinite(frame.X) || !MotionMath.IsFinite(frame.Y) || !MotionMath.IsFinite(frame.Z))
            {
                throw new DecodeException("motion record with a non-finite position");
            }
            return true;
        }

        private static VelocityCode EncodeVelocity(in MotionFrame frame, float qx, float qy, float qz, float qw, bool exact)
        {
            double vx = frame.VX, vy = frame.VY, vz = frame.VZ;
            if (vx == 0 && vy == 0 && vz == 0) return new VelocityCode { Mode = VelocityZero };
            double lengthSquared = vx * vx + vy * vy + vz * vz;
            if (!exact)
            {
                if (lengthSquared <= ZeroVelocitySquared) return new VelocityCode { Mode = VelocityZero };
                MotionMath.Forward(qx, qy, qz, qw, out double fx, out double fy, out double fz);
                double along = vx * fx + vy * fy + vz * fz;
                double code = Math.Round(along * SpeedScale);
                if (code >= SpeedMin && code <= SpeedMax)
                {
                    double speed = code / SpeedScale;
                    double ex = vx - fx * speed, ey = vy - fy * speed, ez = vz - fz * speed;
                    double tolerance = SpeedModeAbsoluteTolerance + SpeedModeRelativeTolerance * Math.Sqrt(lengthSquared);
                    if (ex * ex + ey * ey + ez * ez <= tolerance * tolerance)
                    {
                        return new VelocityCode { Mode = VelocitySpeed, Speed = (int)code };
                    }
                }
            }
            return new VelocityCode
            {
                Mode = VelocityVector,
                HX = MotionHalf.FromSingle(frame.VX),
                HY = MotionHalf.FromSingle(frame.VY),
                HZ = MotionHalf.FromSingle(frame.VZ)
            };
        }

        private static void WriteVelocity(ref MotionBitWriter bits, in VelocityCode velocity)
        {
            bits.Write(velocity.Mode, 2);
            if (velocity.Mode == VelocitySpeed)
            {
                bits.WriteSigned(velocity.Speed, SpeedBits);
            }
            else if (velocity.Mode == VelocityVector)
            {
                bits.Write(velocity.HX, 16);
                bits.Write(velocity.HY, 16);
                bits.Write(velocity.HZ, 16);
            }
        }

        private static VelocityCode ReadVelocityPayload(ref MotionBitReader bits, byte mode)
        {
            var velocity = new VelocityCode { Mode = mode };
            if (mode == VelocitySpeed)
            {
                velocity.Speed = bits.ReadSigned(SpeedBits);
            }
            else if (mode == VelocityVector)
            {
                velocity.HX = (ushort)bits.Read(16);
                velocity.HY = (ushort)bits.Read(16);
                velocity.HZ = (ushort)bits.Read(16);
            }
            return velocity;
        }

        private static void DecodeVelocity(in VelocityCode velocity, float qx, float qy, float qz, float qw, out float vx, out float vy, out float vz)
        {
            if (velocity.Mode == VelocityVector)
            {
                if (!MotionHalf.TryToSingle(velocity.HX, out vx) || !MotionHalf.TryToSingle(velocity.HY, out vy) || !MotionHalf.TryToSingle(velocity.HZ, out vz))
                {
                    throw new DecodeException("motion record with a non-finite velocity");
                }
                return;
            }
            DecodeVelocityUnchecked(velocity, qx, qy, qz, qw, out vx, out vy, out vz);
        }

        private static void DecodeVelocityUnchecked(in VelocityCode velocity, float qx, float qy, float qz, float qw, out float vx, out float vy, out float vz)
        {
            switch (velocity.Mode)
            {
                case VelocitySpeed:
                {
                    MotionMath.Forward(qx, qy, qz, qw, out double fx, out double fy, out double fz);
                    double speed = velocity.Speed / SpeedScale;
                    vx = (float)(fx * speed);
                    vy = (float)(fy * speed);
                    vz = (float)(fz * speed);
                    return;
                }
                case VelocityVector:
                    MotionHalf.TryToSingle(velocity.HX, out vx);
                    MotionHalf.TryToSingle(velocity.HY, out vy);
                    MotionHalf.TryToSingle(velocity.HZ, out vz);
                    return;
                default:
                    vx = vy = vz = 0f;
                    return;
            }
        }

        private static void WriteFlags(ref MotionBitWriter bits, uint flags)
        {
            bits.Write(flags, FlagsLowBits);
            uint high = flags >> FlagsLowBits;
            bits.WriteBool(high != 0);
            if (high != 0) bits.Write(high, FlagsHighBits);
        }

        private static uint ReadFlags(ref MotionBitReader bits)
        {
            uint flags = bits.Read(FlagsLowBits);
            if (bits.ReadBool()) flags |= bits.Read(FlagsHighBits) << FlagsLowBits;
            return flags;
        }

        private static void WriteStateAbsolute(ref MotionBitWriter bits, byte state, byte activity, ushort timer)
        {
            if (state < StateEscape)
            {
                bits.Write(state, StateBits);
            }
            else
            {
                bits.Write(StateEscape, StateBits);
                bits.Write(state, 8);
            }
            bits.Write(activity, ActivityBits);
            bits.Write(timer, TimerBits);
        }

        private static void ReadStateAbsolute(ref MotionBitReader bits, out byte state, out byte activity, out ushort timer)
        {
            uint s = bits.Read(StateBits);
            if (s == StateEscape) s = bits.Read(8);
            state = (byte)s;
            activity = (byte)bits.Read(ActivityBits);
            timer = (ushort)bits.Read(TimerBits);
        }

        // ------------------------------------------------------------------------------------------------ checks

        private static void CheckWriter(ByteWriter w)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
        }

        private static void CheckInterestSlot(ushort interestSlot)
        {
            if (interestSlot == MotionWire.ReservedInterestSlot)
            {
                throw new ArgumentOutOfRangeException(nameof(interestSlot), interestSlot, "interest slot 0xFFFF is reserved");
            }
        }

        private static void CheckGeneration(byte generation)
        {
            if (generation > MotionWire.GenerationMask)
            {
                throw new ArgumentOutOfRangeException(nameof(generation), generation, "assignment generations have 4 bits");
            }
        }

        private static void CheckFrame(in MotionFrame frame, string name)
        {
            if (!MotionMath.IsFinite(frame)) throw new ArgumentException("motion frames must be finite: " + frame, name);
        }

        private static void CheckBaselineArrays(MotionFrame[] fourSlots, uint[] fourTicks)
        {
            if (fourSlots == null) throw new ArgumentNullException(nameof(fourSlots));
            if (fourTicks == null) throw new ArgumentNullException(nameof(fourTicks));
            if (fourSlots.Length < 4) throw new ArgumentException("four baseline frames are required", nameof(fourSlots));
            if (fourTicks.Length < 4) throw new ArgumentException("four baseline ticks are required", nameof(fourTicks));
        }
    }
}
