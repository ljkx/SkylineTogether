using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Motion
{
    /// <summary>
    /// The host's per-peer map between mover slots and the peer's dense 16-bit interest slots (ARCHITECTURE.md#movers).
    /// <para>
    /// Q-B records name interest slots on the unreliable lane while assignments travel on the reliable lane, so an
    /// interest slot is reused only after the client acknowledged the <c>MotionControl</c> message that released it
    /// (the 10 Hz <c>Ack</c> carries the last applied sequence), and every reuse increments its 4-bit generation. A
    /// datagram for the old mover can therefore never land in the new mover's staging ring.
    /// </para>
    /// <para>
    /// Free interest slots are handed out first-in first-out (the slot released longest ago first) and fresh slots are
    /// taken only when no released slot is free, which keeps the slot range, and the receivers' tables, as small as the
    /// largest number of simultaneous entries while spacing out the reuse of any one slot. Sequence numbers compare
    /// with serial arithmetic. Not thread-safe (the encoder holds the peer's lock).
    /// </para>
    /// </summary>
    public sealed class InterestSlotTable
    {
        private enum EntryState : byte { Free, Live, Releasing }

        private struct Entry
        {
            public EntryState State;
            public byte Generation;
            public bool EverUsed;
            public NetSlot Slot;
            public uint Seq;
        }

        private struct PendingRelease
        {
            public ushort InterestSlot;
            public uint Seq;
        }

        private readonly int _capacity;
        private readonly Dictionary<uint, ushort> _bySlot = new Dictionary<uint, ushort>();
        private Entry[] _entries = new Entry[64];
        private int _highWater;                        // interest slots 0 .. _highWater-1 have been handed out at least once
        private ushort[] _free = new ushort[64];       // FIFO ring of acknowledged free interest slots
        private int _freeHead, _freeCount;
        private PendingRelease[] _pending = new PendingRelease[64];   // FIFO by sequence (releases are issued in order)
        private int _pendingHead, _pendingCount;
        private uint _lastAck;
        private bool _hasAck;

        /// <param name="capacity">Interest slots 0 .. capacity−1 (at most 65535; 0xFFFF is reserved).</param>
        public InterestSlotTable(int capacity = 65535)
        {
            if (capacity < 1 || capacity > MotionWire.ReservedInterestSlot)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "an interest-slot table holds 1 .. 65535 slots");
            }
            _capacity = capacity;
        }

        /// <summary>The number of interest slots this table may hand out.</summary>
        public int Capacity => _capacity;

        /// <summary>Live assignments.</summary>
        public int Count => _bySlot.Count;

        /// <summary>Released assignments the client has not acknowledged yet (their interest slots stay blocked).</summary>
        public int PendingReleases => _pendingCount;

        /// <summary>The newest acknowledged sequence (0 before the first acknowledgement).</summary>
        public uint LastAcknowledged => _lastAck;

        /// <summary>
        /// Assigns an interest slot to <paramref name="slot"/> in the message with sequence
        /// <paramref name="motionControlSeq"/>. A slot that is already live keeps its assignment (returned again).
        /// Returns false when every interest slot is live or waiting for an acknowledged release.
        /// </summary>
        public bool TryAssign(NetSlot slot, uint motionControlSeq, out ushort interestSlot, out byte generation)
        {
            if (slot.IsNone) throw new ArgumentException("cannot assign an interest slot to slot none", nameof(slot));
            if (_bySlot.TryGetValue(slot.Value, out interestSlot))
            {
                generation = _entries[interestSlot].Generation;
                return true;
            }
            int chosen;
            if (_freeCount > 0)
            {
                chosen = _free[_freeHead];
                _freeHead = (_freeHead + 1) % _free.Length;
                _freeCount--;
            }
            else if (_highWater < _capacity)
            {
                chosen = _highWater++;
                EnsureEntryCapacity(_highWater);
            }
            else
            {
                interestSlot = 0;
                generation = 0;
                return false;
            }
            ref Entry entry = ref _entries[chosen];
            if (entry.EverUsed) entry.Generation = (byte)((entry.Generation + 1) & MotionWire.GenerationMask);
            entry.EverUsed = true;
            entry.State = EntryState.Live;
            entry.Slot = slot;
            entry.Seq = motionControlSeq;
            interestSlot = (ushort)chosen;
            generation = entry.Generation;
            _bySlot.Add(slot.Value, interestSlot);
            return true;
        }

        /// <summary>
        /// Releases the live assignment of <paramref name="slot"/> in the message with sequence
        /// <paramref name="motionControlSeq"/>; its interest slot stays blocked until the client acknowledges that
        /// sequence. Unknown slots are ignored.
        /// </summary>
        public void Release(NetSlot slot, uint motionControlSeq)
        {
            if (!_bySlot.TryGetValue(slot.Value, out ushort interestSlot)) return;
            _bySlot.Remove(slot.Value);
            BeginRelease(interestSlot, motionControlSeq);
        }

        /// <summary>Releases every live assignment in the message <paramref name="motionControlSeq"/> (short-drop resume, ARCHITECTURE.md#leaving-kicks-and-recovery).</summary>
        public void ReleaseAll(uint motionControlSeq)
        {
            if (_bySlot.Count == 0) return;
            // release in interest-slot order so the result does not depend on dictionary enumeration order
            for (int i = 0; i < _highWater; i++)
            {
                if (_entries[i].State == EntryState.Live) BeginRelease((ushort)i, motionControlSeq);
            }
            _bySlot.Clear();
        }

        /// <summary>
        /// The client applied every <c>MotionControl</c> message up to <paramref name="lastAppliedMotionControlSeq"/>:
        /// releases issued in those messages free their interest slots. Older (reordered) acknowledgements are ignored.
        /// </summary>
        public void OnClientAck(uint lastAppliedMotionControlSeq)
        {
            if (_hasAck && !MotionMath.IsAfter(lastAppliedMotionControlSeq, _lastAck)) return;
            _lastAck = lastAppliedMotionControlSeq;
            _hasAck = true;
            while (_pendingCount > 0)
            {
                PendingRelease head = _pending[_pendingHead];
                if (MotionMath.IsAfter(head.Seq, lastAppliedMotionControlSeq)) break;
                _pendingHead = (_pendingHead + 1) % _pending.Length;
                _pendingCount--;
                ref Entry entry = ref _entries[head.InterestSlot];
                if (entry.State != EntryState.Releasing) continue;
                entry.State = EntryState.Free;
                entry.Slot = NetSlot.None;
                PushFree(head.InterestSlot);
            }
        }

        /// <summary>The live assignment of an interest slot.</summary>
        public bool TryGetSlot(ushort interestSlot, out NetSlot slot, out byte generation)
        {
            if (interestSlot < _highWater && _entries[interestSlot].State == EntryState.Live)
            {
                slot = _entries[interestSlot].Slot;
                generation = _entries[interestSlot].Generation;
                return true;
            }
            slot = NetSlot.None;
            generation = 0;
            return false;
        }

        /// <summary>The live interest slot of a mover.</summary>
        public bool TryGetInterestSlot(NetSlot slot, out ushort interestSlot, out byte generation)
        {
            if (_bySlot.TryGetValue(slot.Value, out interestSlot))
            {
                generation = _entries[interestSlot].Generation;
                return true;
            }
            generation = 0;
            return false;
        }

        public override string ToString()
        {
            return "InterestSlotTable(live " + Count.ToString(CultureInfo.InvariantCulture) + ", releasing "
                   + _pendingCount.ToString(CultureInfo.InvariantCulture) + ", free " + _freeCount.ToString(CultureInfo.InvariantCulture)
                   + ", high water " + _highWater.ToString(CultureInfo.InvariantCulture) + ")";
        }

        private void BeginRelease(ushort interestSlot, uint seq)
        {
            ref Entry entry = ref _entries[interestSlot];
            entry.State = EntryState.Releasing;
            entry.Seq = seq;
            if (_pendingCount == _pending.Length)
            {
                var grown = new PendingRelease[_pending.Length * 2];
                for (int i = 0; i < _pendingCount; i++) grown[i] = _pending[(_pendingHead + i) % _pending.Length];
                _pending = grown;
                _pendingHead = 0;
            }
            _pending[(_pendingHead + _pendingCount) % _pending.Length] = new PendingRelease { InterestSlot = interestSlot, Seq = seq };
            _pendingCount++;
        }

        private void PushFree(ushort interestSlot)
        {
            if (_freeCount == _free.Length)
            {
                var grown = new ushort[_free.Length * 2];
                for (int i = 0; i < _freeCount; i++) grown[i] = _free[(_freeHead + i) % _free.Length];
                _free = grown;
                _freeHead = 0;
            }
            _free[(_freeHead + _freeCount) % _free.Length] = interestSlot;
            _freeCount++;
        }

        private void EnsureEntryCapacity(int count)
        {
            if (count <= _entries.Length) return;
            int size = _entries.Length;
            while (size < count) size *= 2;
            Array.Resize(ref _entries, Math.Min(size, _capacity));
        }
    }
}
