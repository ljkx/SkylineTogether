using System;
using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Diagnostics;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// The join's prefab table, encoded once per session and in slices across frames instead of in the
    /// frame a peer joins (encoding every prefab's <c>PrefabID</c> took about 160 ms in one frame per joiner). The host's
    /// session start hands over the bound prefabs (<see cref="Start"/>); <see cref="Advance"/> encodes them within the
    /// shared frame budget; a join (<see cref="WriteTo"/>) only encodes what was bound since (mods create prefabs at
    /// runtime) and appends the cached records. A prefab whose binding changed meanwhile (rare: a mod destroyed one)
    /// makes the join rebuild the table, exactly as before. Main thread only.
    /// </summary>
    internal sealed class PrefabTableCache
    {
        private readonly SyncBatch.Writer _table = new SyncBatch.Writer();
        private readonly Dictionary<uint, Entity> _encoded = new Dictionary<uint, Entity>();
        private readonly List<(uint Id, Entity Prefab)> _pending = new List<(uint, Entity)>();
        private int _cursor;
        private long _joins, _rebuilds, _diffed;

        public bool Complete => _cursor >= _pending.Count;

        public int Count => _encoded.Count;

        /// <summary>Host session start: the prefabs bound now are encoded over the next frames.</summary>
        public void Start(List<(uint Id, Entity Prefab)> bound)
        {
            Clear();
            if (bound != null) _pending.AddRange(bound);
        }

        public void Clear()
        {
            _table.Reset();
            _encoded.Clear();
            _pending.Clear();
            _cursor = 0;
        }

        /// <summary>Encodes pending prefabs for at most <paramref name="budgetMs"/> (at least one when any is pending).</summary>
        public void Advance(EntityCodec codec, double budgetMs)
        {
            if (Complete || codec == null) return;
            long start = Perf.Start();
            int n = 0;
            while (_cursor < _pending.Count)
            {
                if (n > 0 && (n & 15) == 0 && Perf.Elapsed(start) > budgetMs) break;
                (uint id, Entity prefab) = _pending[_cursor++];
                Encode(codec, id, prefab);
                n++;
            }
            if (Complete) _pending.Clear();
        }

        /// <summary>
        /// A peer joins: appends the whole prefab table to <paramref name="w"/>. <paramref name="bound"/> is every prefab
        /// bound now (<see cref="EntityCodec.BindAllPrefabs"/>): what the cache lacks is encoded now, and a binding that
        /// no longer matches the cache rebuilds it.
        /// </summary>
        public void WriteTo(EntityCodec codec, List<(uint Id, Entity Prefab)> bound, SyncBatch.Writer w)
        {
            _joins++;
            // whatever the background slices did not reach yet
            while (_cursor < _pending.Count)
            {
                (uint id, Entity prefab) = _pending[_cursor++];
                Encode(codec, id, prefab);
            }
            _pending.Clear();
            _cursor = 0;
            bool stale = false;
            int known = 0;
            foreach ((uint id, Entity prefab) in bound)
            {
                if (_encoded.TryGetValue(id, out Entity cached))
                {
                    if (cached != prefab)
                    {
                        stale = true;
                        break;
                    }
                    known++;
                }
            }
            if (stale || known != _encoded.Count)
            {
                // a cached binding went away or changed: rebuild from the current bindings
                _rebuilds++;
                _table.Reset();
                _encoded.Clear();
            }
            foreach ((uint id, Entity prefab) in bound)
            {
                if (_encoded.ContainsKey(id)) continue;
                Encode(codec, id, prefab);
                _diffed++;
            }
            w.Append(_table);
        }

        private void Encode(EntityCodec codec, uint id, Entity prefab)
        {
            if (_encoded.ContainsKey(id)) return;
            _encoded[id] = prefab;
            codec.WritePrefab(_table, id, prefab);
        }

        public string Describe() => "prefab table: " + _encoded.Count + " prefabs cached" + (Complete ? "" : " (" + (_pending.Count - _cursor) + " still encoding)") + ", " + _table.Length / 1024
                                    + " KiB; " + _joins + " joins, " + _diffed + " encoded at a join, " + _rebuilds + " rebuilds";
    }
}
