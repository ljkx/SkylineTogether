using System;
using System.Collections.Generic;
using Game.Prefabs;
using SkylineTogether.Core;
using SkylineTogether.Diagnostics;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    // The update-group guard (UpdateFrameGuardSystem, ARCHITECTURE.md#client-apply).
    public sealed partial class SyncController
    {
        private readonly List<Entity> _guardCreated = new List<Entity>();

        /// <summary>
        /// Handles the chunks of <paramref name="query"/>: entities with <c>UpdateFrame</c>, <c>Created</c> or <c>Deleted</c>
        /// and no update-group marker. Each archetype is logged once per session with its component type names (the game's
        /// own line prints only "Unity.Entities.ComponentType"), its family, net id and prefab; created entities are deleted
        /// (a client releases the id and asks the host again), deleted ones counted.
        /// </summary>
        internal void GuardUnsupported(EntityManager em, EntityQuery query, HashSet<EntityArchetype> logged)
        {
            _guardCreated.Clear();
            query.CompleteDependency();
            NativeArray<ArchetypeChunk> chunks = query.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                EntityTypeHandle entityType = em.GetEntityTypeHandle();
                foreach (ArchetypeChunk chunk in chunks)
                {
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                    if (entities.Length == 0) continue;
                    Entity first = entities[0];
                    // a tool preview (Temp) belongs to the local tool, which regenerates it every frame: reported only
                    bool created = em.HasComponent<Game.Common.Created>(first) && !em.HasComponent<Game.Common.Deleted>(first) && !em.HasComponent<Game.Tools.Temp>(first);
                    if (created)
                    {
                        UpdateFrameGuardSystem.Deleted += entities.Length;
                        for (int i = 0; i < entities.Length; i++) _guardCreated.Add(entities[i]);
                    }
                    else
                    {
                        UpdateFrameGuardSystem.SeenDeleted += entities.Length;
                    }
                    if (logged.Add(chunk.Archetype)) LogUnsupported(em, chunk.Archetype, first, entities.Length, created);
                }
            }
            finally
            {
                chunks.Dispose();
            }
            foreach (Entity e in _guardCreated)
            {
                if (!em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                uint id = _ids != null ? _ids.GetNetId(e) : 0;
                EntityCodec.SanitizeForDelete(em, e);
                if (!IsHost && id != 0)
                {
                    _ids.Unbind(id);
                    ForgetShadow(id);
                    Integrity?.OnLocalDelete(id);
                    _audit.RequestLater(id, Perf.Now);
                }
                em.AddComponent<Game.Common.Deleted>(e); // Created and Deleted: the update-group system skips it
            }
            _guardCreated.Clear();
        }

        private void LogUnsupported(EntityManager em, EntityArchetype archetype, Entity sample, int count, bool created)
        {
            var names = new List<string>();
            NativeArray<ComponentType> types = archetype.GetComponentTypes(Allocator.Temp);
            try
            {
                foreach (ComponentType t in types) names.Add(TypeManager.GetType(t.TypeIndex)?.FullName ?? t.TypeIndex.ToString());
            }
            finally
            {
                types.Dispose();
            }
            names.Sort(StringComparer.Ordinal);
            uint id = _ids != null ? _ids.GetNetId(sample) : 0;
            string who = id != 0 ? "#" + id + " (" + _ids.GetFamily(id) + ")" : sample + " (unbound, " + _codec?.Families.Classify(em, sample) + ")";
            string prefab = "?";
            if (em.HasComponent<PrefabRef>(sample))
            {
                Entity p = em.GetComponentData<PrefabRef>(sample).m_Prefab;
                try
                {
                    PrefabSystem prefabs = em.World.GetExistingSystemManaged<PrefabSystem>();
                    if (prefabs != null && p != Entity.Null && em.Exists(p)) prefab = prefabs.GetPrefabName(p);
                }
                catch (Exception)
                {
                    prefab = p.ToString();
                }
            }
            _log.Warn(LogCategory.Replication, "update-group guard: " + count + " " + (created ? "created" : "deleted") + " entit" + (count == 1 ? "y" : "ies")
                                               + " with UpdateFrame but no update-group marker, e.g. " + who + " prefab '" + prefab + "'; components: " + string.Join(", ", names)
                                               + (created ? "; deleted before the game's update-group system and asked from the host again" : "; harmless to the game, a link was removed from it earlier"));
        }
    }
}
