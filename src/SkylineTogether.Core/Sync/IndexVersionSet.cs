using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// A set of entity handles (index and version) with a membership test of one bit lookup for the common miss, for the
    /// client's orphan sweep (ARCHITECTURE.md#reference-safety): it reads the owner or parent of every owned object and asks
    /// whether that owner was deleted last frame, hundreds of thousands of tests against a handful of entities.
    /// Main thread only.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class IndexVersionSet
    {
        private ulong[] _bits = new ulong[64];
        private readonly Dictionary<int, int> _versions = new Dictionary<int, int>();

        public int Count => _versions.Count;

        /// <summary>Adds the handle; returns false for a negative index or one already held (with the same version).</summary>
        public bool Add(int index, int version)
        {
            if (index < 0) return false;
            int word = index >> 6;
            if (word >= _bits.Length) Array.Resize(ref _bits, Math.Max(word + 1, _bits.Length * 2));
            _bits[word] |= 1UL << (index & 63);
            if (_versions.TryGetValue(index, out int v) && v == version) return false;
            _versions[index] = version;
            return true;
        }

        public bool Contains(int index, int version)
        {
            if (index < 0) return false;
            int word = index >> 6;
            if (word >= _bits.Length || (_bits[word] & (1UL << (index & 63))) == 0) return false;
            return _versions.TryGetValue(index, out int v) && v == version;
        }

        public void Clear()
        {
            if (_versions.Count == 0) return;
            foreach (int index in _versions.Keys) _bits[index >> 6] = 0;
            _versions.Clear();
        }
    }
}
