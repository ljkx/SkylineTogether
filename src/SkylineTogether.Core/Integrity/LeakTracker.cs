using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// Counts client leaks per component type and family (docs/history/ARCHITECTURE-original.md §13.3, P-C §7.3): a leak is a live value that differs
    /// from the ledger outside every capture set (§5.2). The diagnostics dump and the network panel list the top types;
    /// after a game patch they point at the gameplay system missing from the gate table. Counts saturate at
    /// <see cref="int.MaxValue"/>. Thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class LeakTracker
    {
        private readonly object _gate = new object();
        private readonly Dictionary<ushort, int> _byType = new Dictionary<ushort, int>();
        private readonly Dictionary<uint, int> _byTypeAndFamily = new Dictionary<uint, int>();
        private long _total;

        /// <summary>Counts one leak of <paramref name="type"/> on an entity of <paramref name="family"/>.</summary>
        public void Count(TypeRank type, Family family)
        {
            lock (_gate)
            {
                _byType.TryGetValue(type.Value, out int count);
                if (count < int.MaxValue) _byType[type.Value] = count + 1;
                uint key = Key(type, family);
                _byTypeAndFamily.TryGetValue(key, out int perFamily);
                if (perFamily < int.MaxValue) _byTypeAndFamily[key] = perFamily + 1;
                _total++;
            }
        }

        /// <summary>The <paramref name="n"/> types with the most leaks, most first (ties by type rank); empty for n ≤ 0.</summary>
        public IReadOnlyList<KeyValuePair<TypeRank, int>> Top(int n)
        {
            if (n <= 0) return Array.Empty<KeyValuePair<TypeRank, int>>();
            lock (_gate)
            {
                var all = new List<KeyValuePair<TypeRank, int>>(_byType.Count);
                foreach (KeyValuePair<ushort, int> entry in _byType) all.Add(new KeyValuePair<TypeRank, int>(new TypeRank(entry.Key), entry.Value));
                all.Sort(CompareByCountThenType);
                if (all.Count > n) all.RemoveRange(n, all.Count - n);
                return all;
            }
        }

        /// <summary>The families a type leaked on, most first (ties by family value).</summary>
        public IReadOnlyList<KeyValuePair<Family, int>> FamiliesOf(TypeRank type)
        {
            lock (_gate)
            {
                var families = new List<KeyValuePair<Family, int>>();
                foreach (KeyValuePair<uint, int> entry in _byTypeAndFamily)
                {
                    if ((ushort)(entry.Key >> 8) == type.Value) families.Add(new KeyValuePair<Family, int>((Family)(byte)entry.Key, entry.Value));
                }
                families.Sort((a, b) => a.Value != b.Value ? b.Value.CompareTo(a.Value) : ((byte)a.Key).CompareTo((byte)b.Key));
                return families;
            }
        }

        /// <summary>Leaks counted for the type over all families.</summary>
        public int CountOf(TypeRank type)
        {
            lock (_gate)
            {
                return _byType.TryGetValue(type.Value, out int count) ? count : 0;
            }
        }

        /// <summary>Leaks counted for the type on one family.</summary>
        public int CountOf(TypeRank type, Family family)
        {
            lock (_gate)
            {
                return _byTypeAndFamily.TryGetValue(Key(type, family), out int count) ? count : 0;
            }
        }

        /// <summary>Every leak counted since creation or the last <see cref="Reset"/>.</summary>
        public long Total
        {
            get
            {
                lock (_gate) return _total;
            }
        }

        /// <summary>Distinct types with leaks.</summary>
        public int DistinctTypes
        {
            get
            {
                lock (_gate) return _byType.Count;
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _byType.Clear();
                _byTypeAndFamily.Clear();
                _total = 0;
            }
        }

        private static uint Key(TypeRank type, Family family) => ((uint)type.Value << 8) | (byte)family;

        private static int CompareByCountThenType(KeyValuePair<TypeRank, int> a, KeyValuePair<TypeRank, int> b)
        {
            return a.Value != b.Value ? b.Value.CompareTo(a.Value) : a.Key.CompareTo(b.Key);
        }
    }
}
