using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Identity
{
    /// <summary>
    /// The host's slot allocator (ARCH §3.1): lowest free slot first, with deferred reuse. A released slot becomes free
    /// only when (a) every connected peer has acknowledged the epoch that carried its DESPAWN
    /// (<see cref="OnMinAckedEpoch"/> ≥ despawn epoch) and (b) the epoch-log tail has passed that epoch
    /// (<see cref="OnLogTail"/> &gt; despawn epoch), so no in-flight record, repair response or motion datagram can
    /// address a reused slot. Each reuse increments the slot's 8-bit generation (wrapping); a fresh slot starts at
    /// generation 0.
    /// <para>
    /// Release epochs are kept non-decreasing (an out-of-order release is delayed to the latest release epoch seen),
    /// which can only postpone a reuse, never advance it. With no connected peer the engine reports its current epoch
    /// as the minimum acknowledged epoch.
    /// </para>
    /// <para>Host main thread, inside the capture window (ARCH §4.5.5). Not thread-safe.</para>
    /// </summary>
    public sealed class SlotAllocator
    {
        private const int SlotLimit = (int)NetSlot.MaxValue + 1;
        private const int GenerationPageShift = 13;
        private const int GenerationPageSize = 1 << GenerationPageShift;
        private const int GenerationPageMask = GenerationPageSize - 1;

        private struct PendingRelease
        {
            public uint Slot;
            public uint Epoch;
        }

        private readonly ILog _log;
        private readonly SlotBitSet _allocated = new SlotBitSet(SlotLimit);
        private readonly SlotBitSet _pending = new SlotBitSet(SlotLimit);
        private readonly SlotBitSet _free = new SlotBitSet(SlotLimit);
        private readonly Queue<PendingRelease> _queue;
        private byte[][] _generationPages = new byte[16][];
        private int _generationPageCount;
        private int _highWaterMark = 1;
        private EpochId _minAcked;
        private EpochId _logTail;
        private uint _lastReleaseEpoch;
        private long _invalidReleases;

        /// <param name="initialCapacity">Expected number of slots; pre-sizes the internal sets.</param>
        public SlotAllocator(int initialCapacity = 1 << 16) : this(initialCapacity, null)
        {
        }

        /// <param name="initialCapacity">Expected number of slots.</param>
        /// <param name="log">Receives invalid releases and restore anomalies (category <see cref="LogCategory.Replication"/>); may be null.</param>
        public SlotAllocator(int initialCapacity, ILog log)
        {
            if (initialCapacity < 0) throw new ArgumentOutOfRangeException(nameof(initialCapacity));
            _log = log;
            _allocated.EnsureCapacity(initialCapacity);
            _queue = new Queue<PendingRelease>(Math.Min(Math.Max(initialCapacity / 16, 16), 1 << 16));
        }

        /// <summary>One above the highest slot ever allocated (persisted by <c>NetIdSystem</c>); 1 when none was.</summary>
        public int HighWaterMark => _highWaterMark;

        /// <summary>Released slots waiting for the acknowledgement and log-tail conditions.</summary>
        public int PendingReleaseCount => _queue.Count;

        /// <summary>Slots currently allocated (bound on the host).</summary>
        public int AllocatedCount => _allocated.Count;

        /// <summary>Free slots below the high-water mark (reused before any fresh slot).</summary>
        public int FreeCount => _free.Count;

        /// <summary>The last minimum acknowledged epoch reported through <see cref="OnMinAckedEpoch"/>.</summary>
        public EpochId MinAckedEpoch => _minAcked;

        /// <summary>The last epoch-log tail reported through <see cref="OnLogTail"/>.</summary>
        public EpochId LogTail => _logTail;

        /// <summary>Releases of slots that were not allocated (ignored; should stay 0).</summary>
        public long InvalidReleaseCount => _invalidReleases;

        /// <summary>True while the slot is allocated (bound), false when free, pending release or never used.</summary>
        public bool IsAllocated(NetSlot slot) => !slot.IsNone && _allocated.Get((int)slot.Value);

        /// <summary>True while the slot waits for its deferred release.</summary>
        public bool IsPendingRelease(NetSlot slot) => !slot.IsNone && _pending.Get((int)slot.Value);

        /// <summary>The slot's current generation (of its present or last allocation; 0 for never-used slots).</summary>
        public byte GenerationOf(NetSlot slot) => slot.IsNone ? (byte)0 : GetGeneration(slot.Value);

        /// <summary>
        /// Allocates the lowest free slot (a reused slot gets its generation incremented), else the next fresh slot.
        /// Throws <see cref="InvalidOperationException"/> when all 16 777 215 slots are in use (see <see cref="TryAllocate"/>).
        /// </summary>
        public NetSlot Allocate(out byte generation)
        {
            if (TryAllocate(out NetSlot slot, out generation)) return slot;
            throw new InvalidOperationException("the slot space is exhausted (" + _allocated.Count.ToString(CultureInfo.InvariantCulture)
                                                + " allocated, " + _queue.Count.ToString(CultureInfo.InvariantCulture) + " pending release)");
        }

        /// <summary>As <see cref="Allocate"/>, but returns false instead of throwing when the slot space is exhausted.</summary>
        public bool TryAllocate(out NetSlot slot, out byte generation)
        {
            int reused = _free.NextSetBit(1);
            if (reused > 0)
            {
                _free.Clear(reused);
                _allocated.Set(reused);
                generation = unchecked((byte)(GetGeneration((uint)reused) + 1));
                SetGeneration((uint)reused, generation);
                slot = new NetSlot((uint)reused);
                return true;
            }
            if (_highWaterMark >= SlotLimit)
            {
                slot = NetSlot.None;
                generation = 0;
                return false;
            }
            int fresh = _highWaterMark++;
            _allocated.Set(fresh);
            generation = GetGeneration((uint)fresh);
            slot = new NetSlot((uint)fresh);
            return true;
        }

        /// <summary>
        /// Starts the deferred release of an allocated slot whose DESPAWN went out in <paramref name="despawnEpoch"/>.
        /// Releasing a slot that is not allocated (free, pending or never used) is ignored, counted and logged, because a
        /// double release would hand one slot out twice.
        /// </summary>
        public void Release(NetSlot slot, EpochId despawnEpoch)
        {
            if (slot.IsNone || !_allocated.Get((int)slot.Value))
            {
                _invalidReleases++;
                if (_log != null && _log.IsEnabled(LogLevel.Warn))
                {
                    string state = slot.IsNone ? "none" : _pending.Get((int)slot.Value) ? "already pending release" : "not allocated";
                    _log.Write(LogLevel.Warn, LogCategory.Replication, "slot allocator: ignored release of " + slot + " (" + state + ")");
                }
                return;
            }
            uint epoch = Math.Max(despawnEpoch.Value, _lastReleaseEpoch);
            _lastReleaseEpoch = epoch;
            _allocated.Clear((int)slot.Value);
            _pending.Set((int)slot.Value);
            _queue.Enqueue(new PendingRelease { Slot = slot.Value, Epoch = epoch });
            FreeEligible();
        }

        /// <summary>The minimum over all connected peers of their acknowledged epoch (condition a); frees eligible slots.</summary>
        public void OnMinAckedEpoch(EpochId minAckedOverPeers)
        {
            _minAcked = minAckedOverPeers;
            FreeEligible();
        }

        /// <summary>The oldest epoch the epoch log still holds (condition b: the tail must have passed the despawn
        /// epoch); frees eligible slots.</summary>
        public void OnLogTail(EpochId tail)
        {
            _logTail = tail;
            FreeEligible();
        }

        /// <summary>
        /// Rebuilds the allocator after a <c>NetIdSystem</c> load (ARCH §3.4): <paramref name="bound"/> are allocated with
        /// their generations, every other slot below the high-water mark is free, nothing is pending. A high-water mark
        /// below the highest bound slot is raised to it; invalid (none) and duplicate slots are skipped and logged.
        /// </summary>
        public void Restore(int highWaterMark, IEnumerable<KeyValuePair<NetSlot, byte>> bound)
        {
            if (bound == null) throw new ArgumentNullException(nameof(bound));
            ResetState();
            int maxBound = 0;
            int restored = 0;
            int skipped = 0;
            foreach (KeyValuePair<NetSlot, byte> pair in bound)
            {
                NetSlot slot = pair.Key;
                if (slot.IsNone || !_allocated.Set((int)slot.Value))
                {
                    skipped++;
                    continue;
                }
                SetGeneration(slot.Value, pair.Value);
                if ((int)slot.Value > maxBound) maxBound = (int)slot.Value;
                restored++;
            }

            long effective = Math.Max(Math.Max((long)highWaterMark, (long)maxBound + 1), 1);
            if (effective > SlotLimit) effective = SlotLimit;
            _highWaterMark = (int)effective;
            if (_highWaterMark > 1)
            {
                _free.SetRange(1, _highWaterMark);
                for (int s = _allocated.NextSetBit(1); s >= 0; s = _allocated.NextSetBit(s + 1))
                {
                    _free.Clear(s);
                }
            }

            if (_log != null)
            {
                bool markInconsistent = (restored > 0 && highWaterMark < maxBound + 1) || highWaterMark > SlotLimit;
                if (skipped > 0 || markInconsistent)
                {
                    _log.Warn(LogCategory.Replication, "slot allocator: restore skipped " + skipped.ToString(CultureInfo.InvariantCulture)
                              + " invalid or duplicate binding(s); saved high-water mark " + highWaterMark.ToString(CultureInfo.InvariantCulture)
                              + ", effective " + _highWaterMark.ToString(CultureInfo.InvariantCulture));
                }
                _log.Info(LogCategory.Replication, "slot allocator: restored " + restored.ToString(CultureInfo.InvariantCulture)
                          + " binding(s), high-water mark " + _highWaterMark.ToString(CultureInfo.InvariantCulture)
                          + ", " + _free.Count.ToString(CultureInfo.InvariantCulture) + " free slot(s)");
            }
        }

        /// <summary>Forgets every allocation (a new session without a saved block).</summary>
        public void Clear() => ResetState();

        /// <summary>Bytes held by the internal sets and generation pages.</summary>
        public long MemoryBytes => _allocated.MemoryBytes + _pending.MemoryBytes + _free.MemoryBytes
                                   + (long)_generationPageCount * GenerationPageSize + (long)_queue.Count * 8;

        public override string ToString()
        {
            return "SlotAllocator(allocated " + _allocated.Count.ToString(CultureInfo.InvariantCulture) + ", free "
                   + _free.Count.ToString(CultureInfo.InvariantCulture) + ", pending " + _queue.Count.ToString(CultureInfo.InvariantCulture)
                   + ", high-water " + _highWaterMark.ToString(CultureInfo.InvariantCulture) + ")";
        }

        private void FreeEligible()
        {
            while (_queue.Count > 0)
            {
                PendingRelease head = _queue.Peek();
                if (head.Epoch > _minAcked.Value || head.Epoch >= _logTail.Value) return;
                _queue.Dequeue();
                _pending.Clear((int)head.Slot);
                _free.Set((int)head.Slot);
            }
        }

        private void ResetState()
        {
            _allocated.Reset();
            _pending.Reset();
            _free.Reset();
            _queue.Clear();
            _generationPages = new byte[16][];
            _generationPageCount = 0;
            _highWaterMark = 1;
            _minAcked = EpochId.None;
            _logTail = EpochId.None;
            _lastReleaseEpoch = 0;
            _invalidReleases = 0;
        }

        private byte GetGeneration(uint slot)
        {
            int page = (int)(slot >> GenerationPageShift);
            if (page >= _generationPages.Length) return 0;
            byte[] p = _generationPages[page];
            return p == null ? (byte)0 : p[slot & GenerationPageMask];
        }

        private void SetGeneration(uint slot, byte generation)
        {
            int page = (int)(slot >> GenerationPageShift);
            if (page >= _generationPages.Length)
            {
                if (generation == 0) return;
                Array.Resize(ref _generationPages, Math.Max(page + 1, _generationPages.Length * 2));
            }
            byte[] p = _generationPages[page];
            if (p == null)
            {
                if (generation == 0) return;
                p = new byte[GenerationPageSize];
                _generationPages[page] = p;
                _generationPageCount++;
            }
            p[slot & GenerationPageMask] = generation;
        }
    }
}
