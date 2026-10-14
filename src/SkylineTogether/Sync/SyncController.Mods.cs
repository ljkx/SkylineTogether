using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using SkylineTogether.Core;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Identity;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Scheduling;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Diagnostics;
using SkylineTogether.Session;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    // Mod compatibility (docs/design/MOD-COMPATIBILITY.md): dependency binding, mod component changes, edit holds.
    public sealed partial class SyncController
    {
        // ================================================================== mod compatibility (docs/design/MOD-COMPATIBILITY.md)

        /// <summary>
        /// Host: binds an unbound entity a replicated value references when it is itself replicated (Traffic's lane
        /// connection data, Better Bulldozer's removal records) and queues it for this batch.
        /// </summary>
        private bool HostBindDependency(Entity e)
        {
            EntityManager em = _codec.EntityManager;
            if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e) || _ids.GetNetId(e) != 0) return false;
            Family family = _codec.Families.Classify(em, e);
            if (family != Family.ModEntity && family != Family.WaterSource) return false;
            uint id = _ids.BindNew(e, family);
            if (id == 0) return false;
            _dependencies.Enqueue((e, id, family));
            return true;
        }

        /// <summary>Client: the same with an id from the host-granted pool (the host creates the entity from the proposal).</summary>
        private bool ClientBindDependency(Entity e)
        {
            EntityManager em = _codec.EntityManager;
            if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e) || _ids.GetNetId(e) != 0) return false;
            Family family = _codec.Families.Classify(em, e);
            if (family != Family.ModEntity) return false;
            if (!_pool.TryTake(out uint id)) return false;
            _ids.Bind(e, id, family);
            _dependencies.Enqueue((e, id, family));
            return true;
        }

        /// <summary>Writes the queued dependencies as spawns (their own references may queue more).</summary>
        private void WriteDependencies(HashSet<Entity> written, List<uint> proposed = null)
        {
            EntityManager em = _codec.EntityManager;
            int guard = 0;
            while (_dependencies.Count > 0 && guard++ < 100000)
            {
                (Entity e, uint id, Family family) = _dependencies.Dequeue();
                if (!em.Exists(e) || written.Contains(e)) continue;
                try
                {
                    if (IsHost && em.HasComponent<PrefabRef>(e)) _codec.EnsurePrefab(em.GetComponentData<PrefabRef>(e).m_Prefab, _batch);
                    // client: the shadow of what it proposed comes from the record's own serialization
                    if (IsHost) _codec.WriteEntity(_batch, SyncRecordKind.Spawn, id, family, e);
                    else SetShadow(id, _codec.WriteEntityHashed(_batch, SyncRecordKind.Spawn, id, family, e, Family.Generic));
                    written.Add(e);
                    proposed?.Add(id);
                    _watcher?.Refresh(e);
                }
                catch (Exception ex)
                {
                    _log.Warn(LogCategory.Replication, "sending the mod data entity #" + id + " failed", ex);
                }
            }
            _dependencies.Clear();
        }

        /// <summary>
        /// Untagged mod state (<see cref="Compat.ModStateWatcher"/>): changed entities are written as upserts when their set
        /// of watched types changed, else as values. Host: unbound replicated entities are bound and spawned. Client:
        /// only bound entities the applier did not write are proposed.
        /// </summary>
        private void CaptureModChanges(double now, HashSet<Entity> written, bool host, List<uint> proposed = null)
        {
            if (_watcher == null || !_watcher.Active) return;
            EntityManager em = _codec.EntityManager;
            _modChanges.Clear();
            _watcher.Collect(now, _modChanges);
            foreach (Compat.ModStateWatcher.Change c in _modChanges)
            {
                Entity e = c.Entity;
                if (written.Contains(e) || _appliedThisFrame.Contains(e) || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                uint id = c.NetId;
                Family family;
                if (id == 0)
                {
                    if (!host) continue;
                    family = _codec.Families.Classify(em, e);
                    if (!FamilyClassifier.IsReplicated(family)) continue;
                    id = _ids.BindNew(e, family);
                    if (id == 0) continue;
                    if (em.HasComponent<PrefabRef>(e)) _codec.EnsurePrefab(em.GetComponentData<PrefabRef>(e).m_Prefab, _batch);
                    _codec.WriteEntity(_batch, SyncRecordKind.Spawn, id, family, e);
                    written.Add(e);
                    _watcher.Refresh(e);
                    continue;
                }
                family = _ids.GetFamily(id);
                if (!FamilyClassifier.IsReplicated(family)) continue;
                SyncRecordKind kind = c.PresenceChanged ? SyncRecordKind.Upsert : SyncRecordKind.Values;
                if (host && kind == SyncRecordKind.Upsert && em.HasComponent<PrefabRef>(e)) _codec.EnsurePrefab(em.GetComponentData<PrefabRef>(e).m_Prefab, _batch);
                if (host) _codec.WriteEntity(_batch, kind, id, family, e);
                else SetShadow(id, _codec.WriteEntityHashed(_batch, kind, id, family, e, Family.Generic));
                written.Add(e);
                proposed?.Add(id);
            }
        }

        /// <summary>
        /// Client, frame start: ends expired edit holds and applies the host's latest record of each released entity
        /// (only that entity's records of the stored batch).
        /// </summary>
        private void ReleaseHolds()
        {
            if (_holds.Count == 0 || _codec == null) return;
            double now = _sessions.Clock.Seconds;
            List<uint> expired = null;
            foreach (KeyValuePair<uint, EditHold> pair in _holds)
            {
                if (now >= pair.Value.Until) (expired ?? (expired = new List<uint>())).Add(pair.Key);
            }
            if (expired == null) return;
            foreach (uint id in expired)
            {
                byte[] batch = _holds[id].Batch;
                _holds.Remove(id);
                if (batch == null) continue;
                try
                {
                    uint target = id;
                    ApplyResult r = _codec.Apply(batch, 0, batch.Length, null, rec => rec.NetId != target
                        || !(rec.Kind == SyncRecordKind.Spawn || rec.Kind == SyncRecordKind.Upsert || rec.Kind == SyncRecordKind.Values || rec.Kind == SyncRecordKind.EnabledBits));
                    foreach (Entity e in r.Touched) _appliedThisFrame.Add(e);
                }
                catch (Exception ex)
                {
                    _log.Debug(LogCategory.Replication, "applying the held record of #" + id + " failed: " + ex.Message);
                }
            }
        }
    }
}
