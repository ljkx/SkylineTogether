using System;
using System.Collections.Generic;
using System.Globalization;
using SkylineTogether.Core.Identity;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// The host's net id authority (ARCH §3.1): dense ids from 1 with safe reuse. A freed id goes through a quarantine
    /// before anything may reuse it:
    /// <list type="number">
    /// <item><see cref="Release"/> (the despawn was written into this frame's batches) parks it in the open release frame;</item>
    /// <item><see cref="Seal"/> (once per frame, after every batch of the frame was handed to the peers) stamps the
    /// parked ids with the release frame number and the time;</item>
    /// <item><see cref="Update"/> frees the oldest sealed ids whose release frame every live peer has acknowledged as applied
    /// (from <see cref="ApplyAckBook"/>), which lie below the join barrier (no join snapshot
    /// in progress could still contain the old binding) and which have waited at least
    /// <see cref="MinQuarantineSeconds"/> (channels that refer to ids at a slower cadence, such as icon sets or digests,
    /// have flushed their own removal by then).</item>
    /// </list>
    /// Free ids are reused lowest first; each reuse increments the id's 8-bit generation. Ids stay within 24 bits
    /// (<see cref="MaxId"/>, the motion encoder's slot width), and the tables indexed by id stay as large as the most
    /// ids ever alive at once plus the quarantine, instead of growing for the whole session.
    /// <para>Ranges granted to clients for their predicted edits (<see cref="Grant"/>) count as allocated; ids a client
    /// never used are returned through <see cref="Release"/> when it leaves.</para>
    /// <para>Host main thread only. Not thread-safe.</para>
    /// </summary>
    public sealed class NetIdAllocator
    {
        /// <summary>The largest id (24 bits, <see cref="NetSlot.MaxValue"/>).</summary>
        public const uint MaxId = NetSlot.MaxValue;

        /// <summary>Ids granted to a client per request.</summary>
        public const uint DefaultGrant = 4096;

        /// <summary>A grant is assembled from at most this many runs of free ids (the rest comes fresh and contiguous).</summary>
        public const int MaxGrantRuns = 16;

        /// <summary>Default minimum time a released id stays quarantined after its release frame was sealed.</summary>
        public const double DefaultMinQuarantineSeconds = 3.0;

        private const int Limit = (int)MaxId + 1;
        private const int GenerationPageShift = 13;
        private const int GenerationPageSize = 1 << GenerationPageShift;
        private const int GenerationPageMask = GenerationPageSize - 1;

        private struct Pending
        {
            public uint Id;
            public uint Frame;
            public double SealedAt;
        }

        private readonly ILog _log;
        private readonly SlotBitSet _allocated = new SlotBitSet(Limit);
        private readonly SlotBitSet _free = new SlotBitSet(Limit);
        private readonly SlotBitSet _quarantined = new SlotBitSet(Limit);
        private readonly List<uint> _open = new List<uint>();
        private readonly Queue<Pending> _sealed = new Queue<Pending>();
        private byte[][] _generationPages = new byte[16][];
        private int _highWater = 1;
        private uint _frame;
        private long _reused, _invalidReleases, _exhausted;

        public NetIdAllocator(ILog log = null)
        {
            _log = log;
        }

        /// <summary>Minimum time a released id stays quarantined after its release frame was sealed.</summary>
        public double MinQuarantineSeconds { get; set; } = DefaultMinQuarantineSeconds;

        /// <summary>The next fresh id (one above the highest id ever handed out; persisted with the city).</summary>
        public uint Next => (uint)_highWater;

        /// <summary>Ids currently allocated (bound, or granted to a client).</summary>
        public int AllocatedCount => _allocated.Count;

        /// <summary>Free ids below <see cref="Next"/> (reused before any fresh id).</summary>
        public int FreeCount => _free.Count;

        /// <summary>Released ids waiting in the quarantine (open and sealed).</summary>
        public int QuarantinedCount => _quarantined.Count;

        /// <summary>The last sealed release frame (0 before the first <see cref="Seal"/>).</summary>
        public uint SealedFrame => _frame;

        /// <summary>Ids handed out again after a quarantine (diagnostics).</summary>
        public long ReusedCount => _reused;

        /// <summary>Releases of ids that were not allocated (ignored; should stay 0).</summary>
        public long InvalidReleaseCount => _invalidReleases;

        /// <summary>Allocations refused because all 16 777 215 ids were in use or quarantined.</summary>
        public long ExhaustedCount => _exhausted;

        public bool IsAllocated(uint id) => id != 0 && id <= MaxId && _allocated.Get((int)id);

        public bool IsQuarantined(uint id) => id != 0 && id <= MaxId && _quarantined.Get((int)id);

        public bool IsFree(uint id) => id != 0 && id <= MaxId && (_free.Get((int)id) || id >= (uint)_highWater);

        /// <summary>The id's generation: incremented on every reuse, 0 for ids never reused.</summary>
        public byte GenerationOf(uint id) => id == 0 || id > MaxId ? (byte)0 : GetGeneration(id);

        /// <summary>The lowest free id (a reused one gets its generation incremented), else the next fresh one; throws when exhausted.</summary>
        public uint Allocate()
        {
            if (TryAllocate(out uint id)) return id;
            throw new InvalidOperationException("net ids exhausted (" + Describe() + ")");
        }

        /// <summary>As <see cref="Allocate"/>; false (and counted) when every id is in use or quarantined.</summary>
        public bool TryAllocate(out uint id)
        {
            int reused = _free.NextSetBit(1);
            if (reused > 0)
            {
                _free.Clear(reused);
                _allocated.Set(reused);
                SetGeneration((uint)reused, unchecked((byte)(GetGeneration((uint)reused) + 1)));
                _reused++;
                id = (uint)reused;
                return true;
            }
            if (_highWater >= Limit)
            {
                _exhausted++;
                id = 0;
                return false;
            }
            id = (uint)_highWater++;
            _allocated.Set((int)id);
            return true;
        }

        /// <summary>
        /// Grants up to <paramref name="count"/> ids to a client as runs of consecutive ids (appended to
        /// <paramref name="runs"/>): runs of free ids first (lowest first, at most <see cref="MaxGrantRuns"/>), then one
        /// fresh contiguous run. Every granted id counts as allocated. Returns the number granted (less than requested
        /// only when the id space is exhausted).
        /// </summary>
        public uint Grant(uint count, List<(uint Start, uint Count)> runs)
        {
            if (runs == null) throw new ArgumentNullException(nameof(runs));
            uint granted = 0;
            int pieces = 0;
            int from = 1;
            while (granted < count && pieces < MaxGrantRuns)
            {
                int start = _free.NextSetBit(from);
                if (start <= 0) break;
                int end = start;
                while (granted + (uint)(end - start) < count && _free.Get(end)) end++;
                for (int i = start; i < end; i++)
                {
                    _free.Clear(i);
                    _allocated.Set(i);
                    SetGeneration((uint)i, unchecked((byte)(GetGeneration((uint)i) + 1)));
                }
                _reused += end - start;
                runs.Add(((uint)start, (uint)(end - start)));
                granted += (uint)(end - start);
                pieces++;
                from = end;
            }
            if (granted < count)
            {
                long fresh = Math.Min((long)count - granted, (long)Limit - _highWater);
                if (fresh > 0)
                {
                    int start = _highWater;
                    _allocated.SetRange(start, start + (int)fresh);
                    _highWater += (int)fresh;
                    runs.Add(((uint)start, (uint)fresh));
                    granted += (uint)fresh;
                }
                else if (granted == 0)
                {
                    _exhausted++;
                }
            }
            return granted;
        }

        /// <summary>
        /// Marks a specific id allocated (a binding that did not come from <see cref="TryAllocate"/>, e.g. after an older
        /// save). A free or fresh id is taken (fresh ids skipped on the way become free); false when the id is already
        /// allocated or is quarantined (it is then left alone and the caller must not bind it).
        /// </summary>
        public bool Claim(uint id)
        {
            if (id == 0 || id > MaxId) return false;
            int i = (int)id;
            if (_allocated.Get(i)) return false;
            if (_quarantined.Get(i))
            {
                _log?.Warn(LogCategory.Replication, "net id #" + id.ToString(CultureInfo.InvariantCulture) + " is quarantined; refusing to bind it");
                return false;
            }
            if (i >= _highWater)
            {
                if (i > _highWater) _free.SetRange(_highWater, i);
                _highWater = i + 1;
            }
            else
            {
                _free.Clear(i);
            }
            _allocated.Set(i);
            return true;
        }

        /// <summary>
        /// Starts the quarantine of an allocated id (its despawn went out in the current frame). Releasing an id that is not
        /// allocated is ignored and counted: a double release would hand one id out twice.
        /// </summary>
        public bool Release(uint id)
        {
            if (id == 0 || id > MaxId || !_allocated.Get((int)id))
            {
                _invalidReleases++;
                return false;
            }
            _allocated.Clear((int)id);
            _quarantined.Set((int)id);
            _open.Add(id);
            return true;
        }

        /// <summary>
        /// Closes the open release frame: every id released since the previous seal is stamped with <paramref name="frame"/> (the
        /// release frame number of <see cref="ApplyAckBook.SealFrame"/>, which must not decrease) and <paramref name="now"/>.
        /// </summary>
        public void Seal(uint frame, double now)
        {
            if (frame < _frame) frame = _frame; // never earlier: it could only advance a reuse
            _frame = frame;
            foreach (uint id in _open) _sealed.Enqueue(new Pending { Id = id, Frame = frame, SealedAt = now });
            _open.Clear();
        }

        /// <summary>
        /// Frees the sealed ids whose release frame is at most <paramref name="minAcked"/> and below <paramref name="barrier"/>
        /// (0 = no barrier) and that were sealed at least <see cref="MinQuarantineSeconds"/> before <paramref name="now"/>.
        /// Returns the number freed.
        /// </summary>
        public int Update(uint minAcked, uint barrier, double now)
        {
            int freed = 0;
            while (_sealed.Count > 0)
            {
                Pending head = _sealed.Peek();
                if (head.Frame > minAcked) break;
                if (barrier != 0 && head.Frame >= barrier) break;
                if (now - head.SealedAt < MinQuarantineSeconds) break;
                _sealed.Dequeue();
                _quarantined.Clear((int)head.Id);
                _free.Set((int)head.Id);
                freed++;
            }
            return freed;
        }

        /// <summary>
        /// Frees every quarantined id at once (no peer exists that could still address them: a host session start or a
        /// load). Returns the number freed.
        /// </summary>
        public int FlushQuarantine()
        {
            int n = 0;
            foreach (uint id in _open)
            {
                _quarantined.Clear((int)id);
                _free.Set((int)id);
                n++;
            }
            _open.Clear();
            while (_sealed.Count > 0)
            {
                Pending p = _sealed.Dequeue();
                _quarantined.Clear((int)p.Id);
                _free.Set((int)p.Id);
                n++;
            }
            return n;
        }

        /// <summary>
        /// Rebuilds the allocator from saved bindings (ARCH §3.4): <paramref name="bound"/> ids are allocated with their
        /// generations, every other id below the high-water mark is free, nothing is quarantined. Invalid (0, above
        /// <see cref="MaxId"/>) and duplicate ids are skipped; returns the number skipped.
        /// </summary>
        public int Restore(uint highWater, IEnumerable<KeyValuePair<uint, byte>> bound)
        {
            if (bound == null) throw new ArgumentNullException(nameof(bound));
            Clear();
            int skipped = 0;
            int max = 0;
            foreach (KeyValuePair<uint, byte> pair in bound)
            {
                uint id = pair.Key;
                if (id == 0 || id > MaxId || !_allocated.Set((int)id))
                {
                    skipped++;
                    continue;
                }
                SetGeneration(id, pair.Value);
                if ((int)id > max) max = (int)id;
            }
            long mark = Math.Max(Math.Max((long)Math.Min(highWater, (uint)Limit), (long)max + 1), 1L);
            _highWater = (int)Math.Min(mark, Limit);
            if (_highWater > 1)
            {
                _free.SetRange(1, _highWater);
                for (int s = _allocated.NextSetBit(1); s >= 0; s = _allocated.NextSetBit(s + 1)) _free.Clear(s);
            }
            return skipped;
        }

        /// <summary>Forgets everything (ids start again at 1).</summary>
        public void Clear()
        {
            _allocated.Reset();
            _free.Reset();
            _quarantined.Reset();
            _open.Clear();
            _sealed.Clear();
            _generationPages = new byte[16][];
            _highWater = 1;
            _frame = 0;
        }

        /// <summary>One line for diagnostics.</summary>
        public string Describe()
        {
            var ci = CultureInfo.InvariantCulture;
            return "ids: " + _allocated.Count.ToString(ci) + " allocated, " + _free.Count.ToString(ci) + " free, "
                   + _quarantined.Count.ToString(ci) + " quarantined, next " + _highWater.ToString(ci) + ", reused " + _reused.ToString(ci)
                   + (_invalidReleases > 0 ? ", invalid releases " + _invalidReleases.ToString(ci) : "")
                   + (_exhausted > 0 ? ", EXHAUSTED " + _exhausted.ToString(ci) : "");
        }

        private byte GetGeneration(uint id)
        {
            int page = (int)(id >> GenerationPageShift);
            if (page >= _generationPages.Length) return 0;
            byte[] p = _generationPages[page];
            return p == null ? (byte)0 : p[id & GenerationPageMask];
        }

        private void SetGeneration(uint id, byte generation)
        {
            int page = (int)(id >> GenerationPageShift);
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
            }
            p[id & GenerationPageMask] = generation;
        }
    }

    /// <summary>
    /// Renumbering of net ids at host session start (no peer holds any id then): the bound ids keep their relative order
    /// and are packed into 1..n, so a city whose ids were spread by long sessions starts dense again. Pure.
    /// </summary>
    public static class NetIdCompaction
    {
        /// <summary>Compaction pays off when at least a quarter of the id range (and 4096 ids) is unused.</summary>
        public static bool ShouldCompact(int boundCount, uint next)
        {
            long range = (long)next - 1;
            long holes = range - boundCount;
            return holes >= 4096 && holes * 4 >= range;
        }

        /// <summary>
        /// The new id of each of <paramref name="sortedIds"/> (strictly ascending, no 0): position + 1. Returns false (and
        /// leaves <paramref name="newIds"/> null) when the input is not strictly ascending or contains 0.
        /// </summary>
        public static bool BuildMap(IReadOnlyList<uint> sortedIds, out uint[] newIds)
        {
            newIds = null;
            if (sortedIds == null) return false;
            var map = new uint[sortedIds.Count];
            uint previous = 0;
            for (int i = 0; i < sortedIds.Count; i++)
            {
                uint id = sortedIds[i];
                if (id <= previous) return false;
                previous = id;
                map[i] = (uint)(i + 1);
            }
            newIds = map;
            return true;
        }
    }

    /// <summary>
    /// Host: the id ranges granted to each client and which of their ids the client has used. A client may create an
    /// entity only with an id of its own grants that it has not used before, so a stale or replayed spawn can never
    /// bind an id the host has since released and reused.
    /// </summary>
    public sealed class IdGrantBook
    {
        private sealed class Range
        {
            public uint Start;
            public uint Count;
            public ulong[] Used;
        }

        private readonly Dictionary<byte, List<Range>> _byPeer = new Dictionary<byte, List<Range>>();

        /// <summary>Peers with grants.</summary>
        public int PeerCount => _byPeer.Count;

        public void Add(byte peer, uint start, uint count)
        {
            if (count == 0) return;
            if (!_byPeer.TryGetValue(peer, out List<Range> list)) _byPeer[peer] = list = new List<Range>();
            list.Add(new Range { Start = start, Count = count, Used = new ulong[(count + 63) / 64] });
        }

        /// <summary>True when <paramref name="id"/> lies in one of the peer's ranges (used or not).</summary>
        public bool IsGranted(byte peer, uint id) => Find(peer, id, out _, out _);

        /// <summary>True when the id is the peer's and not used yet (nothing is marked).</summary>
        public bool IsUnused(byte peer, uint id)
        {
            if (!Find(peer, id, out Range r, out uint offset)) return false;
            return (r.Used[offset >> 6] & (1UL << (int)(offset & 63))) == 0;
        }

        /// <summary>True when the id is the peer's and unused; it is then marked used.</summary>
        public bool TryConsume(byte peer, uint id)
        {
            if (!Find(peer, id, out Range r, out uint offset)) return false;
            ulong bit = 1UL << (int)(offset & 63);
            ref ulong word = ref r.Used[offset >> 6];
            if ((word & bit) != 0) return false;
            word |= bit;
            return true;
        }

        /// <summary>Forgets the peer; the ids it never used are appended to <paramref name="unused"/> (null: dropped).</summary>
        public void Remove(byte peer, List<uint> unused)
        {
            if (!_byPeer.TryGetValue(peer, out List<Range> list)) return;
            _byPeer.Remove(peer);
            if (unused == null) return;
            foreach (Range r in list)
            {
                for (uint i = 0; i < r.Count; i++)
                {
                    if ((r.Used[i >> 6] & (1UL << (int)(i & 63))) == 0) unused.Add(r.Start + i);
                }
            }
        }

        public void Clear() => _byPeer.Clear();

        private bool Find(byte peer, uint id, out Range range, out uint offset)
        {
            range = null;
            offset = 0;
            if (!_byPeer.TryGetValue(peer, out List<Range> list)) return false;
            for (int i = 0; i < list.Count; i++)
            {
                Range r = list[i];
                if (id >= r.Start && id - r.Start < r.Count)
                {
                    range = r;
                    offset = id - r.Start;
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>The ranges granted to one client; it asks for more before they run out.</summary>
    public sealed class ClientIdPool
    {
        public const uint LowWater = 1024;
        private readonly Queue<(uint Start, uint Count)> _ranges = new Queue<(uint, uint)>();
        private uint _current, _end;
        private bool _requested;

        public uint Remaining
        {
            get
            {
                uint n = _end - _current;
                foreach (var r in _ranges) n += r.Count;
                return n;
            }
        }

        /// <summary>True once when the pool runs low and no request is outstanding (the caller then asks the host).</summary>
        public bool ShouldRequestMore()
        {
            if (_requested || Remaining >= LowWater) return false;
            _requested = true;
            return true;
        }

        public void Add(uint start, uint count)
        {
            if (count == 0) return;
            _ranges.Enqueue((start, count));
            _requested = false;
        }

        /// <summary>Owner check for the host: is <paramref name="id"/> inside one of the granted ranges?</summary>
        public static bool InRanges(IReadOnlyList<(uint Start, uint Count)> ranges, uint id)
        {
            foreach (var r in ranges) if (id >= r.Start && id - r.Start < r.Count) return true;
            return false;
        }

        public bool TryTake(out uint id)
        {
            while (_current >= _end)
            {
                if (_ranges.Count == 0)
                {
                    id = 0;
                    return false;
                }
                var r = _ranges.Dequeue();
                _current = r.Start;
                _end = r.Start + r.Count;
            }
            id = _current++;
            return true;
        }

        public void Clear()
        {
            _ranges.Clear();
            _current = _end = 0;
            _requested = false;
        }
    }
}
