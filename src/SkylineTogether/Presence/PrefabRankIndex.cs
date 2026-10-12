using System;
using System.Collections.Generic;
using Game.Prefabs;
using Game.SceneFlow;
using Game.UI.InGame;
using SkylineTogether.Core;
using Unity.Collections;
using Unity.Entities;
using ILog = SkylineTogether.Core.ILog;

namespace SkylineTogether.Presence
{
    /// <summary>
    /// Prefab ranks for presence (ARCH §3.5): every prefab entity (<see cref="PrefabData"/>, disabled ones included)
    /// resolved through <see cref="PrefabSystem.TryGetPrefab{T}(Entity, out T)"/>, ordered ordinally by
    /// <see cref="PrefabBase.GetPrefabID"/>. Peers with the same prefab set get the same ranks, so a rank names the
    /// same prefab everywhere without a table on the wire. Built lazily (a few thousand prefabs, once per session) and
    /// reset when a session starts or ends; a differing prefab set only mislabels the cosmetic tool caption.
    /// </summary>
    internal sealed class PrefabRankIndex
    {
        private readonly ILog _log;
        private readonly List<PrefabBase> _byRank = new List<PrefabBase>();
        private readonly Dictionary<PrefabBase, int> _rankOf = new Dictionary<PrefabBase, int>();
        private readonly Dictionary<int, string> _titles = new Dictionary<int, string>();
        private bool _built;

        public PrefabRankIndex(ILog log)
        {
            _log = log;
        }

        public void Invalidate()
        {
            _built = false;
            _byRank.Clear();
            _rankOf.Clear();
            _titles.Clear();
        }

        /// <summary>The rank of <paramref name="prefab"/>, -1 when it is null or unknown.</summary>
        public int RankOf(World world, PrefabBase prefab)
        {
            if (prefab == null) return -1;
            EnsureBuilt(world);
            return _rankOf.TryGetValue(prefab, out int rank) ? rank : -1;
        }

        /// <summary>The player-facing (localised) name of the prefab with this rank, or null.</summary>
        public string TitleOf(World world, int rank)
        {
            if (rank < 0) return null;
            if (_titles.TryGetValue(rank, out string cached)) return cached;
            EnsureBuilt(world);
            string title = null;
            if (rank < _byRank.Count)
            {
                PrefabBase prefab = _byRank[rank];
                title = prefab.name;
                try
                {
                    PrefabSystem prefabs = world.GetExistingSystemManaged<PrefabSystem>();
                    PrefabUISystem ui = world.GetExistingSystemManaged<PrefabUISystem>();
                    if (prefabs != null && ui != null)
                    {
                        ui.GetTitleAndDescription(prefabs.GetEntity(prefab), out string titleId, out string _);
                        var dictionary = GameManager.instance?.localizationManager?.activeDictionary;
                        if (dictionary != null && !string.IsNullOrEmpty(titleId) && dictionary.TryGetValue(titleId, out string localized) && !string.IsNullOrEmpty(localized))
                        {
                            title = localized;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Debug(LogCategory.Ui, "no title for prefab " + prefab.name + ": " + ex.Message);
                }
            }
            _titles[rank] = title;
            return title;
        }

        private void EnsureBuilt(World world)
        {
            if (_built || world == null) return;
            _built = true;
            PrefabSystem prefabs = world.GetExistingSystemManaged<PrefabSystem>();
            if (prefabs == null) return;
            var entries = new List<(string Id, PrefabBase Prefab)>();
            EntityManager em = world.EntityManager;
            using (EntityQuery q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabData>() },
                Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IgnoreComponentEnabledState,
            }))
            {
                NativeArray<Entity> all = q.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (Entity e in all)
                    {
                        if (prefabs.TryGetPrefab(e, out PrefabBase prefab) && prefab != null) entries.Add((prefab.GetPrefabID().ToString(), prefab));
                    }
                }
                finally
                {
                    all.Dispose();
                }
            }
            entries.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            foreach ((string _, PrefabBase prefab) in entries)
            {
                if (_rankOf.ContainsKey(prefab)) continue;
                _rankOf[prefab] = _byRank.Count;
                _byRank.Add(prefab);
            }
            _log.Debug(LogCategory.Ui, "presence prefab index: " + _byRank.Count + " prefabs");
        }
    }
}
