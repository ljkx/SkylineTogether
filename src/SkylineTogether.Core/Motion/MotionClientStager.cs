using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Motion
{
    /// <summary>A mover the host released from the client's interest: pin it at its last pose (ARCH §4.9.5).</summary>
    public struct MotionPin
    {
        public NetSlot Slot;
        /// <summary>False when no frame was ever received (pin at the entity's current pose).</summary>
        public bool HasFrame;
        public MotionFrame Frame;
    }

    /// <summary>
    /// The client side of the motion channel (ARCH §4.9.6): applies the host's <c>MotionControl</c> messages
    /// (assignments with their Q-A baselines, releases, release-all) in order, decodes the per-tick Q-B datagrams of
    /// <see cref="MotionHostEncoder"/> against the frames it holds (<see cref="IMotionReferences"/>), and keeps a small
    /// staging ring per interest slot. The game layer then asks for the pose of a mover at any tick
    /// (<see cref="TrySample"/>): an exact host frame, a Hermite interpolation between two host frames (so a client whose
    /// <c>UpdateFrame</c> differs from the host's still sees correct motion), an extrapolation for at most
    /// <see cref="ExtrapolationCapTicks"/> ticks past the newest frame, or, beyond that, the extrapolated pose pinned
    /// with zero velocity (never a runaway).
    /// <para>Bodies are message bodies without the envelope (<c>u32 seq | u16 ops | ops</c> and
    /// <c>u32 tick | u16 seq | records</c>, <see cref="MotionWire"/>). Main thread only.</para>
    /// </summary>
    public sealed class MotionClientStager : IMotionReferences
    {
        /// <summary>Frames kept per mover (8 × 16 ticks: two ring lifetimes).</summary>
        public const int RingSize = 8;

        /// <summary>Extrapolation past the newest frame stops (pins) after this many ticks (ARCH §4.9.5).</summary>
        public const int ExtrapolationCapTicks = 32;

        /// <summary>Marker byte of this channel's <c>Ack</c> body (<c>u8 'M' | u32 lastAppliedMotionControlSeq</c>).</summary>
        public const byte AckMarker = 0x4D;

        private sealed class Entry
        {
            public bool Assigned;
            public byte Generation;
            public NetSlot Slot;
            public byte UpdateFrame;
            public readonly uint[] Ticks = new uint[RingSize];
            public readonly MotionFrame[] Frames = new MotionFrame[RingSize];
            public int Count;
            public int Version;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly Dictionary<uint, ushort> _bySlot = new Dictionary<uint, ushort>();
        private readonly ByteReader _reader = new ByteReader();
        private readonly MotionFrame[] _qaFrames = new MotionFrame[4];
        private readonly uint[] _qaTicks = new uint[4];

        /// <summary>Sequence number of the last applied <c>MotionControl</c> message (sent back in the 10 Hz ack).</summary>
        public uint LastAppliedControlSeq { get; private set; }

        /// <summary>True once any <c>MotionControl</c> message was applied.</summary>
        public bool HasAppliedControl { get; private set; }

        /// <summary>Movers currently assigned.</summary>
        public int AssignedCount => _bySlot.Count;

        /// <summary>Upper bound of interest-slot numbers in use (for enumeration with <see cref="TryGetAssignment"/>).</summary>
        public int SlotCapacity => _entries.Count;

        public long FramesAccepted { get; private set; }

        public long FramesDropped { get; private set; }

        // ------------------------------------------------------------------------------------------------ control

        /// <summary>Applies one <c>MotionControl</c> body; released movers are appended to <paramref name="pins"/>.</summary>
        /// <exception cref="DecodeException">Malformed body (ops before the failure stay applied).</exception>
        public void ApplyControl(byte[] body, int offset, int count, List<MotionPin> pins)
        {
            _reader.Reset(body, offset, count);
            uint seq = _reader.ReadUInt32();
            int ops = _reader.ReadUInt16();
            for (int i = 0; i < ops; i++)
            {
                byte kind = _reader.ReadByte();
                switch (kind)
                {
                    case MotionWire.OpAssign:
                    {
                        MotionCodec.ReadQA(_reader, out ushort interestSlot, out byte generation, out NetSlot slot, out byte updateFrame, _qaFrames, _qaTicks);
                        Assign(interestSlot, generation, slot, updateFrame, pins);
                        break;
                    }
                    case MotionWire.OpRelease:
                    {
                        ushort interestSlot = _reader.ReadUInt16();
                        byte flags = _reader.ReadByte();
                        Release(interestSlot, (byte)(flags & MotionWire.GenerationMask), pins);
                        break;
                    }
                    case MotionWire.OpReleaseAll:
                        _reader.ReadByte();
                        ReleaseAll(pins);
                        break;
                    default:
                        throw new DecodeException("unknown MotionControl op " + kind);
                }
            }
            if (!_reader.IsAtEnd) throw new DecodeException("MotionControl has " + _reader.Remaining + " trailing bytes");
            LastAppliedControlSeq = seq;
            HasAppliedControl = true;
        }

        private void Assign(ushort interestSlot, byte generation, NetSlot slot, byte updateFrame, List<MotionPin> pins)
        {
            if (interestSlot >= MotionWire.MaxInterestSlots) throw new DecodeException("interest slot " + interestSlot + " beyond " + MotionWire.MaxInterestSlots);
            while (_entries.Count <= interestSlot) _entries.Add(new Entry());
            Entry e = _entries[interestSlot];
            if (e.Assigned && e.Slot != slot) Unassign(interestSlot, pins);
            if (_bySlot.TryGetValue(slot.Value, out ushort old) && old != interestSlot) Unassign(old, null);
            e.Assigned = true;
            e.Generation = generation;
            e.Slot = slot;
            e.UpdateFrame = updateFrame;
            e.Count = 0;
            e.Version++;
            _bySlot[slot.Value] = interestSlot;
            for (int k = 0; k < 4; k++) Insert(e, _qaTicks[k], _qaFrames[k]);
        }

        private void Release(ushort interestSlot, byte generation, List<MotionPin> pins)
        {
            if (interestSlot >= _entries.Count) return;
            Entry e = _entries[interestSlot];
            if (!e.Assigned || e.Generation != generation) return;
            Unassign(interestSlot, pins);
        }

        /// <summary>Releases every mover (session end, or the host's release-all).</summary>
        public void ReleaseAll(List<MotionPin> pins)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Assigned) Unassign((ushort)i, pins);
            }
        }

        private void Unassign(ushort interestSlot, List<MotionPin> pins)
        {
            Entry e = _entries[interestSlot];
            if (!e.Assigned) return;
            if (pins != null)
            {
                var pin = new MotionPin { Slot = e.Slot };
                if (e.Count > 0)
                {
                    pin.HasFrame = true;
                    pin.Frame = e.Frames[e.Count - 1];
                    pin.Frame.VX = pin.Frame.VY = pin.Frame.VZ = 0f;
                    pin.Frame.Synthetic = true;
                }
                pins.Add(pin);
            }
            if (_bySlot.TryGetValue(e.Slot.Value, out ushort mapped) && mapped == interestSlot) _bySlot.Remove(e.Slot.Value);
            e.Assigned = false;
            e.Count = 0;
            e.Version++;
        }

        // ------------------------------------------------------------------------------------------------ datagrams

        /// <summary>Decodes one <c>Motion</c> body; returns the number of frames stored. <paramref name="tick"/> is the host tick.</summary>
        /// <exception cref="DecodeException">Malformed body (records before the failure stay stored).</exception>
        public int ApplyDatagram(byte[] body, int offset, int count, out uint tick)
        {
            _reader.Reset(body, offset, count);
            tick = _reader.ReadUInt32();
            _reader.ReadUInt16();
            int stored = 0;
            while (!_reader.IsAtEnd)
            {
                bool decoded = MotionCodec.TryReadQB(_reader, tick, this, out QbHeader header, out MotionFrame frame);
                if (!decoded || header.InterestSlot >= _entries.Count)
                {
                    FramesDropped++;
                    continue;
                }
                Entry e = _entries[header.InterestSlot];
                if (!e.Assigned || e.Generation != header.AssignGeneration || (tick & 15u) != e.UpdateFrame)
                {
                    FramesDropped++;
                    continue;
                }
                frame.Synthetic = false;
                if (Insert(e, tick, frame))
                {
                    e.Version++;
                    stored++;
                    FramesAccepted++;
                }
            }
            return stored;
        }

        /// <summary>Sorted insert into the ring; false when the frame is older than everything kept or already known.</summary>
        private static bool Insert(Entry e, uint tick, in MotionFrame frame)
        {
            int pos = e.Count;
            while (pos > 0 && MotionMath.IsAfter(e.Ticks[pos - 1], tick)) pos--;
            if (pos > 0 && e.Ticks[pos - 1] == tick)
            {
                e.Frames[pos - 1] = frame;
                return true;
            }
            if (e.Count == RingSize)
            {
                if (pos == 0) return false;
                // drop the oldest
                Array.Copy(e.Ticks, 1, e.Ticks, 0, RingSize - 1);
                Array.Copy(e.Frames, 1, e.Frames, 0, RingSize - 1);
                e.Count--;
                pos--;
            }
            for (int i = e.Count; i > pos; i--)
            {
                e.Ticks[i] = e.Ticks[i - 1];
                e.Frames[i] = e.Frames[i - 1];
            }
            e.Ticks[pos] = tick;
            e.Frames[pos] = frame;
            e.Count++;
            return true;
        }

        /// <inheritdoc />
        public bool TryGetReference(ushort interestSlot, byte assignGeneration, uint referenceTick, out MotionFrame frame)
        {
            frame = default;
            if (interestSlot >= _entries.Count) return false;
            Entry e = _entries[interestSlot];
            if (!e.Assigned || e.Generation != assignGeneration) return false;
            for (int i = 0; i < e.Count; i++)
            {
                if (e.Ticks[i] != referenceTick || e.Frames[i].Synthetic) continue;
                frame = e.Frames[i];
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------------ queries

        /// <summary>The assignment of interest slot <paramref name="interestSlot"/>; <paramref name="version"/> changes whenever its frames change.</summary>
        public bool TryGetAssignment(int interestSlot, out NetSlot slot, out byte updateFrame, out int version)
        {
            slot = NetSlot.None;
            updateFrame = 0;
            version = 0;
            if (interestSlot < 0 || interestSlot >= _entries.Count) return false;
            Entry e = _entries[interestSlot];
            if (!e.Assigned) return false;
            slot = e.Slot;
            updateFrame = e.UpdateFrame;
            version = e.Version;
            return true;
        }

        /// <summary>True while the mover <paramref name="slot"/> is assigned (in the client's interest).</summary>
        public bool IsAssigned(NetSlot slot) => _bySlot.ContainsKey(slot.Value);

        /// <summary>The newest frame held for an interest slot.</summary>
        public bool TryGetNewest(int interestSlot, out uint tick, out MotionFrame frame)
        {
            tick = 0;
            frame = default;
            if (interestSlot < 0 || interestSlot >= _entries.Count) return false;
            Entry e = _entries[interestSlot];
            if (!e.Assigned || e.Count == 0) return false;
            tick = e.Ticks[e.Count - 1];
            frame = e.Frames[e.Count - 1];
            return true;
        }

        /// <summary>
        /// The mover's pose at host tick <paramref name="tick"/>: exact, interpolated, extrapolated (capped) or pinned;
        /// everything but an exact frame is marked <see cref="MotionFrame.Synthetic"/>. False when nothing is held.
        /// </summary>
        public bool TrySample(int interestSlot, uint tick, out MotionFrame frame)
        {
            frame = default;
            if (interestSlot < 0 || interestSlot >= _entries.Count) return false;
            Entry e = _entries[interestSlot];
            if (!e.Assigned || e.Count == 0) return false;
            return Sample(e.Ticks, e.Frames, e.Count, tick, out frame);
        }

        /// <summary>The sampling rule of <see cref="TrySample"/> over a sorted run of frames (public for tests and reuse).</summary>
        public static bool Sample(uint[] ticks, MotionFrame[] frames, int count, uint tick, out MotionFrame frame)
        {
            frame = default;
            if (count <= 0) return false;
            // newest frame at or before the tick
            int a = -1;
            for (int i = count - 1; i >= 0; i--)
            {
                if (!MotionMath.IsAfter(ticks[i], tick))
                {
                    a = i;
                    break;
                }
            }
            if (a < 0)
            {
                frame = frames[0];
                frame.Synthetic = true;
                return true;
            }
            if (ticks[a] == tick)
            {
                frame = frames[a];
                return true;
            }
            if (a + 1 < count)
            {
                frame = Interpolate(frames[a], frames[a + 1], MotionMath.TickDiff(ticks[a + 1], ticks[a]), MotionMath.TickDiff(tick, ticks[a]));
                return true;
            }
            frame = Extrapolate(frames[a], MotionMath.TickDiff(tick, ticks[a]));
            return true;
        }

        private static MotionFrame Interpolate(in MotionFrame a, in MotionFrame b, int span, int at)
        {
            double t = span > 0 ? (double)at / span : 0.0;
            double dt = span * MotionMath.SecondsPerTick;
            double t2 = t * t, t3 = t2 * t;
            double h00 = 2 * t3 - 3 * t2 + 1, h10 = t3 - 2 * t2 + t, h01 = -2 * t3 + 3 * t2, h11 = t3 - t2;
            MotionFrame f = t < 0.5 ? a : b;
            f.X = (float)(h00 * a.X + h10 * dt * a.VX + h01 * b.X + h11 * dt * b.VX);
            f.Y = (float)(h00 * a.Y + h10 * dt * a.VY + h01 * b.Y + h11 * dt * b.VY);
            f.Z = (float)(h00 * a.Z + h10 * dt * a.VZ + h01 * b.Z + h11 * dt * b.VZ);
            f.VX = (float)(a.VX + (b.VX - a.VX) * t);
            f.VY = (float)(a.VY + (b.VY - a.VY) * t);
            f.VZ = (float)(a.VZ + (b.VZ - a.VZ) * t);
            MotionMath.Slerp(a, b, t, out f.QX, out f.QY, out f.QZ, out f.QW);
            f.Synthetic = true;
            return f;
        }

        private static MotionFrame Extrapolate(in MotionFrame a, int ticks)
        {
            MotionFrame f = a;
            int used = Math.Min(ticks, ExtrapolationCapTicks);
            double dt = used * MotionMath.SecondsPerTick;
            f.X = (float)(a.X + a.VX * dt);
            f.Y = (float)(a.Y + a.VY * dt);
            f.Z = (float)(a.Z + a.VZ * dt);
            f.StateTimer = unchecked((ushort)(a.StateTimer + used / MotionMath.TicksPerStep));
            if (ticks > ExtrapolationCapTicks) f.VX = f.VY = f.VZ = 0f;
            f.Synthetic = true;
            return f;
        }

        // ------------------------------------------------------------------------------------------------ ack

        /// <summary>The client's ack body (sent as <c>MessageKind.Ack</c>, about 10 Hz).</summary>
        public static void WriteAck(ByteWriter w, uint lastAppliedControlSeq)
        {
            w.WriteByte(AckMarker);
            w.WriteUInt32(lastAppliedControlSeq);
        }

        /// <summary>Reads an ack body; false when the body belongs to another channel or is malformed.</summary>
        public static bool TryReadAck(byte[] body, int offset, int count, out uint lastAppliedControlSeq)
        {
            lastAppliedControlSeq = 0;
            if (body == null || count != 5 || body[offset] != AckMarker) return false;
            lastAppliedControlSeq = (uint)(body[offset + 1] | body[offset + 2] << 8 | body[offset + 3] << 16 | body[offset + 4] << 24);
            return true;
        }
    }
}
