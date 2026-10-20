using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Colossal.IO.AssetDatabase;
using Game.Prefabs;
using SkylineTogether.Core;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Protocol;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// Where prefabs come from, for missing-asset reports (docs/design/MOD-COMPATIBILITY.md §7): base game prefabs have no
    /// source; a prefab with an asset comes from a Paradox Mods package (display name and id) or is the host's local asset;
    /// a prefab without an asset was created at runtime by a mod (named when its type or one of its prefab components
    /// belongs to a mod, or when exactly one loaded mod creates prefabs at runtime). Never a file path. Also the name
    /// fallback of prefab binding (<see cref="FindByName"/>): a runtime prefab whose name is deterministic binds on
    /// every peer even when its <c>PrefabID</c> hash differs. Main thread only; every read is guarded.
    /// </summary>
    public static class AssetSources
    {
        private static readonly Dictionary<string, List<PrefabBase>> ByName = new Dictionary<string, List<PrefabBase>>(StringComparer.Ordinal);
        private static int _indexedCount = -1;

        /// <summary>The origin of a prefab; null for base game prefabs (and when it cannot be read).</summary>
        public static AssetSource Of(PrefabBase prefab)
        {
            if (prefab == null) return null;
            try
            {
                if (prefab.isBuiltin) return null;
                PrefabAsset asset = prefab.asset;
                if (asset == null) return new AssetSource { Kind = AssetSourceKind.RuntimePrefab, Name = CreatorOf(prefab) ?? "" };
                SourceMeta meta = asset.GetMeta();
                if (!string.IsNullOrEmpty(meta.platformID) || prefab.isSubscribedMod)
                {
                    return new AssetSource { Kind = AssetSourceKind.Package, Name = Clip(meta.packageName, AssetSource.MaxNameBytes), PlatformId = Clip(meta.platformID, AssetSource.MaxPlatformIdBytes) };
                }
                return new AssetSource { Kind = AssetSourceKind.LocalAsset, Name = Clip(prefab.name, AssetSource.MaxNameBytes) };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The mod that made a runtime prefab, or null when it cannot be told: its type or one of its prefab components
        /// comes from a mod. Not guessed otherwise (the game creates a few prefabs in code as well, and a wrong name would
        /// send a joiner after the wrong mod).
        /// </summary>
        private static string CreatorOf(PrefabBase prefab)
        {
            Assembly game = typeof(PrefabBase).Assembly;
            if (prefab.GetType().Assembly != game) return ModCompat.NameOf(prefab.GetType().Assembly);
            if (prefab.components != null)
            {
                foreach (ComponentBase c in prefab.components)
                {
                    if (c != null && c.GetType().Assembly != game) return ModCompat.NameOf(c.GetType().Assembly);
                }
            }
            return null;
        }

        private static string Clip(string s, int maxBytes) => ProtocolText.Truncate(ProtocolText.Sanitize(s), maxBytes / ProtocolText.MaxUtf8BytesPerChar);

        // ------------------------------------------------------------------------------------------------ the join

        /// <summary>
        /// Host, join snapshot: the origins of the prefabs the city's entities use, most prefabs first, at most
        /// <see cref="SnapshotManifestCodec.MaxAssetSources"/>. Null when the city uses base game prefabs only.
        /// </summary>
        public static List<AssetSourceUse> CityUse(World world, PrefabSystem prefabs)
        {
            EntityManager em = world.EntityManager;
            var used = new HashSet<Entity>();
            EntityQuery query = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<PrefabData>() },
            });
            try
            {
                NativeArray<PrefabRef> refs = query.ToComponentDataArray<PrefabRef>(Allocator.Temp);
                try
                {
                    foreach (PrefabRef r in refs) used.Add(r.m_Prefab);
                }
                finally
                {
                    refs.Dispose();
                }
            }
            finally
            {
                query.Dispose();
            }
            var bySource = new Dictionary<AssetSource, AssetSourceUse>();
            foreach (Entity e in used)
            {
                if (e == Entity.Null || !prefabs.TryGetPrefab(e, out PrefabBase prefab) || prefab == null) continue;
                AssetSource source = Of(prefab);
                if (source == null) continue;
                if (!bySource.TryGetValue(source, out AssetSourceUse use)) bySource[source] = use = new AssetSourceUse { Source = source, Example = Clip(prefab.name, AssetSourceUse.MaxExampleBytes) };
                use.Prefabs++;
            }
            if (bySource.Count == 0) return null;
            var list = new List<AssetSourceUse>(bySource.Values);
            list.Sort((a, b) => b.Prefabs != a.Prefabs ? b.Prefabs.CompareTo(a.Prefabs) : string.CompareOrdinal(a.Source.Describe(), b.Source.Describe()));
            if (list.Count > SnapshotManifestCodec.MaxAssetSources) list.RemoveRange(SnapshotManifestCodec.MaxAssetSources, list.Count - SnapshotManifestCodec.MaxAssetSources);
            return list;
        }

        /// <summary>
        /// Joiner, before the load: the host's sources this game lacks with their prefab counts: packages whose Paradox
        /// Mods id no local prefab carries, runtime prefabs of mods that are not loaded here, and local assets of the host
        /// whose name no local prefab has (DLC content without a package id is found that way). Runtime prefabs of an
        /// unnamed mod cannot be checked and are left to the per-record report.
        /// </summary>
        public static List<KeyValuePair<AssetSource, int>> MissingHere(List<AssetSourceUse> sources, PrefabSystem prefabs)
        {
            var missing = new List<KeyValuePair<AssetSource, int>>();
            if (sources == null || sources.Count == 0) return missing;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (PrefabBase p in AllPrefabs(prefabs))
            {
                try
                {
                    if (p == null) continue;
                    names.Add(Clip(p.name, AssetSource.MaxNameBytes)); // compared like the host clipped it
                    string id = p.asset?.GetMeta().platformID;
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
                }
                catch (Exception)
                {
                    // an asset without readable metadata proves nothing
                }
            }
            var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((Assembly assembly, string _) in ModCompat.LoadedMods()) mods.Add(ModCompat.NameOf(assembly));
            foreach (AssetSourceUse use in sources)
            {
                AssetSource s = use?.Source;
                if (s == null) continue;
                bool have;
                switch (s.Kind)
                {
                    case AssetSourceKind.Package:
                        have = string.IsNullOrEmpty(s.PlatformId) || ids.Contains(s.PlatformId);
                        break;
                    case AssetSourceKind.RuntimePrefab:
                        have = string.IsNullOrEmpty(s.Name) || mods.Contains(s.Name);
                        break;
                    case AssetSourceKind.LocalAsset:
                        have = names.Contains(s.Name ?? "");
                        break;
                    default:
                        have = true;
                        break;
                }
                if (!have) missing.Add(new KeyValuePair<AssetSource, int>(s, use.Prefabs));
            }
            return missing;
        }

        // ------------------------------------------------------------------------------------------------ handshake

        /// <summary>
        /// The asset packs of the handshake manifest (ARCHITECTURE.md#handshake-and-admission): one entry per Paradox Mods id
        /// among the loaded prefabs (asset packs, and the assets of code mods), with the package's name and version, the
        /// number of prefabs and the digest of their sorted <c>PrefabID</c>s. Local assets are not listed (a joiner cannot
        /// get them; the join snapshot's per-city check names the ones the city uses). Sorted by id.
        /// </summary>
        public static List<ContentEntry> Packages(PrefabSystem prefabs)
        {
            var byId = new Dictionary<string, (string Name, string Version, List<string> Ids)>(StringComparer.Ordinal);
            foreach (PrefabBase p in AllPrefabs(prefabs))
            {
                try
                {
                    if (p == null || p.isBuiltin) continue;
                    PrefabAsset asset = p.asset;
                    if (asset == null) continue;
                    SourceMeta meta = asset.GetMeta();
                    string id = meta.platformID;
                    if (string.IsNullOrEmpty(id)) continue;
                    if (!byId.TryGetValue(id, out var pack))
                    {
                        pack = (meta.packageName, meta.platformVersion, new List<string>());
                        byId[id] = pack;
                    }
                    pack.Ids.Add(p.GetPrefabID().ToString());
                }
                catch (Exception)
                {
                    // a prefab whose asset cannot describe itself is not listed
                }
            }
            var list = new List<ContentEntry>(byId.Count);
            foreach (KeyValuePair<string, (string Name, string Version, List<string> Ids)> pack in byId)
            {
                list.Add(new ContentEntry
                {
                    Id = Clip(pack.Key, ManifestDetailMessage.MaxEntryIdBytes),
                    Name = Clip(pack.Value.Name, ManifestDetailMessage.MaxEntryNameBytes),
                    Version = Clip(pack.Value.Version, ManifestDetailMessage.MaxEntryVersionBytes),
                    Digest = Core.Session.CompatibilityDigests.OfPrefabIds(pack.Value.Ids),
                    Prefabs = pack.Value.Ids.Count,
                });
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return list;
        }

        // ------------------------------------------------------------------------------------------------ name fallback

        /// <summary>
        /// The local prefab with the host prefab's type and name when its exact <c>PrefabID</c> does not resolve and the
        /// match is unambiguous (Core <see cref="MissingAssets.ChooseNameFallback"/>); null otherwise.
        /// </summary>
        public static PrefabBase FindByName(PrefabSystem prefabs, string type, string name, bool hostHasHash)
        {
            if (string.IsNullOrEmpty(type) || name == null) return null;
            Reindex(prefabs);
            if (!ByName.TryGetValue(type + ":" + name, out List<PrefabBase> list)) return null;
            var candidates = new List<MissingAssets.Candidate>(list.Count);
            foreach (PrefabBase p in list)
            {
                bool hasAsset = p.asset != null;
                bool hasHash = hasAsset && p.GetPrefabID().ToString() != type + ":" + name;
                candidates.Add(new MissingAssets.Candidate { HasAsset = hasAsset, HasHash = hasHash });
            }
            int chosen = MissingAssets.ChooseNameFallback(hostHasHash, candidates);
            return chosen >= 0 ? list[chosen] : null;
        }

        private static void Reindex(PrefabSystem prefabs)
        {
            int count = PrefabCount(prefabs);
            if (count == _indexedCount) return;
            _indexedCount = count;
            ByName.Clear();
            foreach (PrefabBase p in AllPrefabs(prefabs))
            {
                if (p == null) continue;
                string key = p.GetType().Name + ":" + p.name;
                if (!ByName.TryGetValue(key, out List<PrefabBase> list)) ByName[key] = list = new List<PrefabBase>(1);
                list.Add(p);
            }
        }

        /// <summary>Number of prefabs in the prefab system (cheap; -1 when unreadable). Changes when a mod adds prefabs.</summary>
        public static int PrefabCount(PrefabSystem prefabs)
        {
            try
            {
                return prefabs != null && Diagnostics.Probes.PrefabList?.GetValue(prefabs) is ICollection all ? all.Count : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static List<PrefabBase> AllPrefabs(PrefabSystem prefabs)
        {
            var list = new List<PrefabBase>();
            try
            {
                if (prefabs != null && Diagnostics.Probes.PrefabList?.GetValue(prefabs) is IEnumerable all)
                {
                    foreach (object o in all)
                    {
                        if (o is PrefabBase p) list.Add(p);
                    }
                }
            }
            catch (Exception)
            {
                // without the list there is no fallback and no package check
            }
            return list;
        }
    }
}
