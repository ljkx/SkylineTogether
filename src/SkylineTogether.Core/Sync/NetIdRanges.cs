using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Net id allocation. The host owns one counter; it assigns ids to the entities it creates and grants clients
    /// contiguous ranges, so an entity a client creates by its own (predicted) edit already carries its final id and
    /// never needs rebinding. Ids are dense from 1, so receivers can index tables by id.
    /// </summary>
    public sealed class NetIdAllocator
    {
        public const uint DefaultGrant = 4096;
        private uint _next;

        public NetIdAllocator(uint next = 1)
        {
            _next = Math.Max(1u, next);
        }

        /// <summary>The next id the host would assign (high-water mark + 1).</summary>
        public uint Next => _next;

        public uint Allocate()
        {
            if (_next == uint.MaxValue) throw new InvalidOperationException("net ids exhausted");
            return _next++;
        }

        /// <summary>Grants a contiguous range for a client.</summary>
        public (uint Start, uint Count) Grant(uint count = DefaultGrant)
        {
            if (count == 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (uint.MaxValue - _next < count) throw new InvalidOperationException("net ids exhausted");
            uint start = _next;
            _next += count;
            return (start, count);
        }

        /// <summary>Makes sure ids up to <paramref name="id"/> are never handed out again (after loading bindings).</summary>
        public void Reserve(uint id)
        {
            if (id >= _next) _next = id == uint.MaxValue ? uint.MaxValue : id + 1;
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
