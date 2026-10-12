using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// A set of net ids with O(1) add, remove and membership and dense iteration (a packed list plus a position per id):
    /// the per-family index sets that replace scans over the whole id table. Removal swaps the last member into the gap,
    /// so iteration order is not insertion order. Not thread-safe.
    /// </summary>
    public sealed class IdIndexSet
    {
        private uint[] _items = new uint[64];
        private int[] _positionPlusOne = new int[64];
        private int _count;

        public int Count => _count;

        /// <summary>The member at <paramref name="index"/> (0 ≤ index &lt; <see cref="Count"/>).</summary>
        public uint this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
                return _items[index];
            }
        }

        public bool Contains(uint id) => id < (uint)_positionPlusOne.Length && _positionPlusOne[id] != 0;

        /// <summary>Adds the id; false when it was already a member. Ids above 2^28 are refused.</summary>
        public bool Add(uint id)
        {
            if (id >= (1u << 28)) throw new ArgumentOutOfRangeException(nameof(id));
            if (Contains(id)) return false;
            if (id >= (uint)_positionPlusOne.Length) Array.Resize(ref _positionPlusOne, (int)Math.Max(id + 1, (uint)_positionPlusOne.Length * 2));
            if (_count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
            _items[_count] = id;
            _positionPlusOne[id] = ++_count;
            return true;
        }

        /// <summary>Removes the id; false when it was not a member.</summary>
        public bool Remove(uint id)
        {
            if (!Contains(id)) return false;
            int index = _positionPlusOne[id] - 1;
            int last = _count - 1;
            if (index != last)
            {
                uint moved = _items[last];
                _items[index] = moved;
                _positionPlusOne[moved] = index + 1;
            }
            _positionPlusOne[id] = 0;
            _count = last;
            return true;
        }

        /// <summary>Appends every member to <paramref name="output"/> (a snapshot for a rolling pass).</summary>
        public void CopyTo(List<uint> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            for (int i = 0; i < _count; i++) output.Add(_items[i]);
        }

        public void Clear()
        {
            for (int i = 0; i < _count; i++) _positionPlusOne[_items[i]] = 0;
            _count = 0;
        }
    }
}
