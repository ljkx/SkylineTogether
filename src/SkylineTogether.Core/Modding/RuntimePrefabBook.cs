using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Modding
{
    /// <summary>What a mod adapter says about a runtime prefab it may create on demand (docs/design/MOD-COMPATIBILITY.md §7).</summary>
    public enum RuntimePrefabState : byte
    {
        /// <summary>No active adapter creates a prefab of this type and name: an ordinary missing asset.</summary>
        NotHandled = 0,
        /// <summary>The adapter asked its mod to create it; not usable yet.</summary>
        Pending = 1,
        /// <summary>It exists and is initialised: bind it.</summary>
        Ready = 2,
        /// <summary>The adapter would create it but may not (the session's limit was reached, or the mod refused).</summary>
        Refused = 3,
    }

    /// <summary>
    /// Bookkeeping of the runtime prefabs mods create on demand for one session (docs/design/MOD-COMPATIBILITY.md §7):
    /// <list type="bullet">
    /// <item><b>admission</b>: names come from another peer, so at most <see cref="MaxPrefabs"/> distinct prefabs are ever
    /// requested per session (a mod writes each to its own files);</item>
    /// <item><b>waiting records</b>: a client skips the host's records of objects whose prefab is still being created
    /// (no missing-asset report: it is expected for a few frames) and remembers their ids, at most
    /// <see cref="MaxWaitingPerPrefab"/> per prefab, to ask the host for them again once the prefab exists;</item>
    /// <item><b>give up</b>: a prefab still not created after <see cref="GiveUpSeconds"/> becomes an ordinary missing asset.</item>
    /// </list>
    /// Pure; not thread-safe (main thread).
    /// </summary>
    public sealed class RuntimePrefabBook
    {
        public const int MaxPrefabs = 512;
        public const int MaxWaitingPerPrefab = 4096;
        public const double GiveUpSeconds = 120.0;

        private sealed class Entry
        {
            public string Type;
            public string Name;
            public double Since;
            public readonly HashSet<uint> Waiting = new HashSet<uint>();
        }

        private readonly HashSet<string> _admitted = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<uint, Entry> _tracked = new Dictionary<uint, Entry>();
        private readonly List<uint> _scratch = new List<uint>();

        /// <summary>Distinct prefabs admitted this session.</summary>
        public int Admitted => _admitted.Count;

        /// <summary>Prefab ids whose creation is awaited.</summary>
        public int Tracked => _tracked.Count;

        /// <summary>True when a request for <paramref name="key"/> may go to the mod: admitted before, or within the limit.</summary>
        public bool Admit(string key)
        {
            if (key == null) return false;
            if (_admitted.Contains(key)) return true;
            if (_admitted.Count >= MaxPrefabs) return false;
            _admitted.Add(key);
            return true;
        }

        /// <summary>Awaits the creation of the prefab the host bound to <paramref name="prefabId"/> (kept when already tracked).</summary>
        public void Track(uint prefabId, string type, string name, double now)
        {
            if (prefabId == 0 || _tracked.ContainsKey(prefabId)) return;
            _tracked[prefabId] = new Entry { Type = type, Name = name, Since = now };
        }

        public bool IsTracked(uint prefabId) => _tracked.ContainsKey(prefabId);

        /// <summary>The type and name of a tracked prefab id.</summary>
        public bool TryGet(uint prefabId, out string type, out string name)
        {
            type = name = null;
            if (!_tracked.TryGetValue(prefabId, out Entry e)) return false;
            type = e.Type;
            name = e.Name;
            return true;
        }

        /// <summary>A record that needs the tracked prefab was skipped; false when the prefab is not tracked or the list is full.</summary>
        public bool Wait(uint prefabId, uint recordId)
        {
            if (recordId == 0 || !_tracked.TryGetValue(prefabId, out Entry e)) return false;
            if (e.Waiting.Count >= MaxWaitingPerPrefab && !e.Waiting.Contains(recordId)) return false;
            e.Waiting.Add(recordId);
            return true;
        }

        /// <summary>The prefab exists now: stops tracking it and adds the ids of the records that waited for it.</summary>
        public void Resolve(uint prefabId, List<uint> waiting)
        {
            if (!_tracked.TryGetValue(prefabId, out Entry e)) return;
            _tracked.Remove(prefabId);
            if (waiting == null) return;
            var ids = new List<uint>(e.Waiting);
            ids.Sort();
            waiting.AddRange(ids);
        }

        /// <summary>Stops tracking a prefab without asking for its records (the adapter refused or went away).</summary>
        public void Drop(uint prefabId) => _tracked.Remove(prefabId);

        /// <summary>The tracked prefab ids (a copy, in ascending order).</summary>
        public List<uint> TrackedIds()
        {
            var list = new List<uint>(_tracked.Keys);
            list.Sort();
            return list;
        }

        /// <summary>Stops tracking every prefab awaited for longer than <see cref="GiveUpSeconds"/> and returns their ids.</summary>
        public List<uint> Expire(double now)
        {
            _scratch.Clear();
            foreach (KeyValuePair<uint, Entry> pair in _tracked)
            {
                if (now - pair.Value.Since > GiveUpSeconds) _scratch.Add(pair.Key);
            }
            _scratch.Sort();
            foreach (uint id in _scratch) _tracked.Remove(id);
            return new List<uint>(_scratch);
        }

        /// <summary>Session end.</summary>
        public void Clear()
        {
            _admitted.Clear();
            _tracked.Clear();
        }
    }
}
