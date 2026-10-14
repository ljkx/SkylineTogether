using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Colossal.IO.AssetDatabase;
using Colossal.Serialization.Entities;
using Game.Prefabs;
using Game.Prefabs.Climate;
using Game.Prefabs.Terrain;
using Game.Prefabs.Water;
using Game.Rendering;
using Game.Simulation;
using Game.UI;
using SkylineTogether.Core;
using SkylineTogether.Core.Session;
using SkylineTogether.Diagnostics;
using Unity.Entities;
using Hash128 = Colossal.Hash128;
using ILog = SkylineTogether.Core.ILog;

namespace SkylineTogether.Session
{
    /// <summary>
    /// Package-bundled render prefabs in the join snapshot (R12 §4.3 "bundled render/climate prefabs (gap)").
    /// <para>
    /// <b>Host</b> (<see cref="Export"/>, on the "SaveLoadGame" queue right before the snapshot's <c>SaveGameSystem</c>
    /// run): calls the internal <c>PrefabSystem.SavePrefabAssets</c> exactly as <c>GameManager.Save</c> does
    /// (<c>GameManager.cs:961-963</c>), with the map's or save's previously saved prefabs
    /// (<c>MapMetadataSystem.prefabReferences</c>, what <c>MenuUISystem.GetSaveInfo</c> passes), into a transient
    /// database of its own. That stores the city's climate, water-render and terrain-render prefab (and the textures they
    /// reference outside the game's database) when they came from a package, and remaps their entities in the snapshot's
    /// serialization context to the new asset guids, so the snapshot names them by those guids. The assets' files go into
    /// a <see cref="JoinAssetBundle"/> in front of the save data. <c>SaveTextureDependencies</c> points the live prefab's
    /// texture fields at the copies; they are put back right after, so the host's own prefab never refers to the
    /// transient database (vanilla points them at its save package, which stays in the user's database).
    /// </para>
    /// <para>
    /// <b>Joiner</b> (<see cref="Import"/>, before the city load): adds every asset to a transient database of its own
    /// with the host's guid (<c>ILocalAssetDatabase.AddAsset(name, type, forceGuid)</c>), registers that database with
    /// <c>AssetDatabase.global</c> (texture references resolve through it, <c>AssetReference.cs:180-185</c>) and adds the
    /// prefabs to the prefab system, like the boot does for packaged prefabs (<c>GameManager.LoadPrefabs</c>). The
    /// databases stay registered for the game session (the city keeps using the prefabs); their folders are removed at
    /// the next start. <see cref="MissingRenderPrefabs"/> then checks the manifest's render prefab ids: a joiner that
    /// still cannot resolve one is refused with a clear text instead of loading <c>[Missing]</c> prefabs.
    /// </para>
    /// Every step is guarded: without the probe, or when the export fails, the snapshot carries an empty bundle and the
    /// joiner's check decides. Main thread only.
    /// </summary>
    public static class JoinAssetTransfer
    {
        private static readonly List<ILocalAssetDatabase> ClientDatabases = new List<ILocalAssetDatabase>();

        /// <summary>The live render prefab entities, in the order of <c>PrefabSystem.SavePrefabAssets</c>.</summary>
        private static List<(Entity Entity, PrefabBase Prefab)> LiveRenderPrefabs(World world, PrefabSystem prefabs)
        {
            var list = new List<(Entity, PrefabBase)>(3);
            Add(world.GetExistingSystemManaged<ClimateSystem>()?.currentClimate ?? Entity.Null, typeof(ClimatePrefab));
            Add(world.GetExistingSystemManaged<WaterRenderSystem>()?.currentWaterRenderSettings ?? Entity.Null, typeof(WaterRenderSettingsPrefab));
            Add(world.GetExistingSystemManaged<TerrainMaterialSystem>()?.currentTerrainRenderSettings ?? Entity.Null, typeof(TerrainRenderSettingsPrefab));
            return list;

            void Add(Entity e, Type type)
            {
                if (e == Entity.Null) return;
                if (prefabs.TryGetPrefab(e, out PrefabBase p) && p != null && type.IsInstanceOfType(p)) list.Add((e, p));
            }
        }

        // ------------------------------------------------------------------------------------------------ host

        /// <summary>
        /// Host: stores the package-bundled render prefabs into <paramref name="folder"/> (remapping them in
        /// <paramref name="context"/>), returns the bundle entries (empty when nothing is bundled) and the render prefab
        /// ids the snapshot will write. <paramref name="database"/> is the transient database (null when unused); the
        /// caller disposes it with the snapshot.
        /// </summary>
        public static List<JoinAssetEntry> Export(World world, string folder, ref Context context, ILog log, out string[] renderIds, out ILocalAssetDatabase database)
        {
            var entries = new List<JoinAssetEntry>();
            renderIds = Array.Empty<string>();
            database = null;
            PrefabSystem prefabs = world.GetExistingSystemManaged<PrefabSystem>();
            if (prefabs == null) return entries;
            List<(Entity Entity, PrefabBase Prefab)> live = LiveRenderPrefabs(world, prefabs);
            if (Probes.SavePrefabAssets != null && live.Count == 3)
            {
                var textures = SaveTextureFields(live);
                try
                {
                    database = AssetDatabase.GetTransient(0L, folder);
                    PrefabAsset[] references = world.GetExistingSystemManaged<MapMetadataSystem>()?.prefabReferences;
                    PrefabAsset[] saved = Probes.SavePrefabAssets(prefabs, database, references, ref context) ?? Array.Empty<PrefabAsset>();
                    if (saved.Length > 0)
                    {
                        foreach (IAssetData asset in database.AllAssets()) entries.Add(ToEntry(asset));
                        log.Info(LogCategory.Join, "join snapshot: " + saved.Length + " package-bundled render prefab(s) and " + (entries.Count - saved.Length) + " other asset(s) go with the city");
                    }
                }
                catch (Exception ex)
                {
                    entries.Clear();
                    log.Warn(LogCategory.Join, "join snapshot: storing the package-bundled render prefabs failed; joiners need the host's map or save package", ex);
                }
                finally
                {
                    RestoreTextureFields(textures);
                }
            }
            renderIds = RenderIds(live, context);
            return entries;
        }

        private static JoinAssetEntry ToEntry(IAssetData asset)
        {
            byte[] data;
            using (Stream s = ((AssetData)asset).GetReadStream())
            using (var m = new MemoryStream())
            {
                s.CopyTo(m);
                data = m.ToArray();
            }
            return new JoinAssetEntry
            {
                Kind = asset is PrefabAsset ? JoinAssetKind.Prefab : asset is TextureAsset ? JoinAssetKind.Texture : JoinAssetKind.Other,
                TypeName = asset.GetType().FullName,
                Name = asset.name,
                Guid = ((Hash128)asset.id).ToString(),
                Data = data,
            };
        }

        /// <summary>The ids the snapshot writes for the live render prefabs (with the context's remapped guids).</summary>
        private static string[] RenderIds(List<(Entity Entity, PrefabBase Prefab)> live, Context context)
        {
            if (Probes.PrefabIdHash == null) return Array.Empty<string>();
            var ids = new List<string>(live.Count);
            foreach ((Entity entity, PrefabBase prefab) in live)
            {
                context.TryGetRemapEntityGuid(entity, out Hash128 guid);
                object id = prefab.GetPrefabID(guid); // boxed: the field is read from the box
                var hash = (Hash128)Probes.PrefabIdHash.GetValue(id);
                ids.Add(RenderPrefabRef.Encode(prefab.GetType().Name, prefab.name, hash.isValid ? hash.ToString() : ""));
            }
            return ids.ToArray();
        }

        private static List<(object Owner, FieldInfo Field, object Value)> SaveTextureFields(List<(Entity Entity, PrefabBase Prefab)> live)
        {
            // the fields PrefabSystem.SaveTextureDependencies rewrites (PrefabSystem.cs:342-361)
            var saved = new List<(object, FieldInfo, object)>();
            foreach ((Entity _, PrefabBase prefab) in live)
            {
                foreach (FieldInfo f in prefab.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (f.FieldType == typeof(AssetReference<TextureAsset>)) saved.Add((prefab, f, f.GetValue(prefab)));
                }
            }
            return saved;
        }

        private static void RestoreTextureFields(List<(object Owner, FieldInfo Field, object Value)> saved)
        {
            foreach ((object owner, FieldInfo field, object value) in saved)
            {
                try
                {
                    field.SetValue(owner, value);
                }
                catch (Exception)
                {
                    // best effort: the field keeps the copy's reference
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ joiner

        /// <summary>Joiner: adds the host's bundled assets and prefabs (see the class summary). Returns the number of
        /// prefabs added.</summary>
        public static async Task<int> Import(IReadOnlyList<JoinAssetEntry> entries, string folder, ILog log)
        {
            if (entries == null || entries.Count == 0) return 0;
            World world = World.DefaultGameObjectInjectionWorld;
            PrefabSystem prefabs = world?.GetExistingSystemManaged<PrefabSystem>();
            if (prefabs == null) return 0;
            ILocalAssetDatabase db = AssetDatabase.GetTransient(0L, folder);
            ClientDatabases.Add(db);
            var prefabAssets = new List<PrefabAsset>();
            foreach (JoinAssetEntry e in entries)
            {
                try
                {
                    Type type = typeof(AssetData).Assembly.GetType(e.TypeName, false);
                    if (type == null || !typeof(AssetData).IsAssignableFrom(type) || !Hash128.TryParse(e.Guid, out Hash128 guid))
                    {
                        log.Warn(LogCategory.Join, "join assets: skipped " + e.Name + " (" + e.TypeName + ")");
                        continue;
                    }
                    IAssetData asset = db.AddAsset(e.Name, type, guid);
                    using (Stream s = ((AssetData)asset).GetWriteStream()) s.Write(e.Data, 0, e.Data.Length);
                    if (e.Kind == JoinAssetKind.Prefab && asset is PrefabAsset p) prefabAssets.Add(p);
                }
                catch (Exception ex)
                {
                    log.Warn(LogCategory.Join, "join assets: adding " + e.Name + " failed", ex);
                }
            }
            try
            {
                await AssetDatabase.global.RegisterDatabase(db); // texture references resolve through the global database
            }
            catch (Exception ex)
            {
                log.Warn(LogCategory.Join, "join assets: registering the database failed (textures of the host's render prefabs may be missing)", ex);
            }
            int added = 0;
            foreach (PrefabAsset asset in prefabAssets)
            {
                try
                {
                    if (!(asset.Load() is PrefabBase prefab)) continue;
                    if (prefabs.TryGetPrefab(prefab.GetPrefabID(), out _)) continue; // an earlier join of this game session
                    if (prefabs.AddPrefab(prefab)) added++;
                }
                catch (Exception ex)
                {
                    log.Warn(LogCategory.Join, "join assets: loading the prefab " + asset.name + " failed", ex);
                }
            }
            log.Info(LogCategory.Join, "join assets: " + added + " of " + prefabAssets.Count + " bundled render prefab(s) added");
            return added;
        }

        /// <summary>Joiner: the manifest's render prefabs this game cannot resolve (names for the refusal); empty when all
        /// resolve or the host sent none.</summary>
        public static List<string> MissingRenderPrefabs(string[] renderIds)
        {
            var missing = new List<string>();
            if (renderIds == null || renderIds.Length == 0) return missing;
            PrefabSystem prefabs = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<PrefabSystem>();
            if (prefabs == null) return missing;
            foreach (string value in renderIds)
            {
                if (!RenderPrefabRef.TryDecode(value, out string type, out string name, out string hashText)) continue;
                Hash128 hash = default;
                if (!string.IsNullOrEmpty(hashText) && !Hash128.TryParse(hashText, out hash)) continue;
                if (!prefabs.TryGetPrefab(new PrefabID(type, name, hash), out _)) missing.Add(name);
            }
            return missing;
        }
    }
}
