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
            if (_watcher == null || !_watcher.Active || !Features.ModCompat.IsAvailable) return;
            EntityManager em = _codec.EntityManager;
            _modChanges.Clear();
            _clientCreated.Clear();
            _watcher.Collect(now, _modChanges);
            foreach (Compat.ModStateWatcher.Change c in _modChanges)
            {
                Entity e = c.Entity;
                if (written.Contains(e) || _appliedThisFrame.Contains(e) || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                if (!host && !Core.Modding.ModStateProposalRule.MayPropose(_modStateToolPath, c.Since, now, _input.LastInput))
                {
                    // the client's own copy of a mod system recomputed it (no tool, no edit input around the time it changed):
                    // the host derives its own value, which arrives with the host's records. Consumed (the watcher took it
                    // as known): a later input never proposes it (ModStateProposalRule)
                    if (_derivedModChanges++ == 0) _log.Info(LogCategory.Compat, "mod state changed here without the player's input (a mod system deriving it locally); such changes are not sent to the host");
                    continue;
                }
                uint id = c.NetId;
                Family family;
                if (id == 0)
                {
                    if (!host)
                    {
                        // an entity a mod created on this client as the player's edit (Placemaker's group record and its
                        // carrier): bound from the pool and proposed, all of the frame's first so they can name each other
                        if (Compat.ModCompat.HasClientCreatedType(em, e)) _clientCreated.Add(e);
                        continue;
                    }
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
            if (!host && _clientCreated.Count > 0) ProposeClientCreated(em, written, proposed);
        }

        private readonly List<Entity> _clientCreated = new List<Entity>();
        private readonly List<(Entity Entity, uint Id, Family Family)> _clientCreatedBound = new List<(Entity, uint, Family)>();

        /// <summary>
        /// Client: new entities of <see cref="Compat.ModAdapter.ClientCreatedTypes"/> become spawn proposals. Every one is
        /// bound first, then written, so a record naming another new one (a group record and its carrier) sends a net id.
        /// </summary>
        private void ProposeClientCreated(EntityManager em, HashSet<Entity> written, List<uint> proposed)
        {
            _clientCreatedBound.Clear();
            foreach (Entity e in _clientCreated)
            {
                if (_ids.GetNetId(e) != 0 || written.Contains(e)) continue;
                Family family = _codec.Families.Classify(em, e);
                if (!FamilyClassifier.IsReplicated(family) || FamilyClassifier.IsHostOnly(family)) continue;
                if (!_pool.TryTake(out uint id)) break;
                _ids.Bind(e, id, family);
                if (_ids.GetEntity(id) != e) continue; // refused (beyond the announced range): not sent
                _clientCreatedBound.Add((e, id, family));
            }
            foreach ((Entity e, uint id, Family family) in _clientCreatedBound)
            {
                SetShadow(id, _codec.WriteEntityHashed(_batch, SyncRecordKind.Spawn, id, family, e, Family.Generic));
                written.Add(e);
                proposed?.Add(id);
                _watcher?.Refresh(e);
            }
            if (_clientCreatedBound.Count > 0 && _clientCreatedLogged++ == 0) _log.Info(LogCategory.Compat, "mod compatibility: proposing " + _clientCreatedBound.Count + " entities a mod created as this player's edit");
            _clientCreatedBound.Clear();
        }

        private long _clientCreatedLogged;

        /// <summary>
        /// Host capture: binds the runtime prefabs adapters created for clients (<see cref="Compat.ModCompat.AnnouncePrefab"/>)
        /// and writes their prefab records, so a client can name them in its proposals.
        /// </summary>
        private void AnnounceRuntimePrefabs()
        {
            _announced.Clear();
            Compat.ModCompat.TakeAnnouncements(_announced);
            EntityManager em = _codec.EntityManager;
            foreach (Entity prefab in _announced)
            {
                if (!em.Exists(prefab) || _ids.GetNetId(prefab) != 0) continue;
                uint id = _codec.EnsurePrefab(prefab, _batch);
                if (id != 0) _log.Info(LogCategory.Compat, "mod compatibility: runtime prefab #" + id + " created for a client is named to everyone");
            }
            _announced.Clear();
        }

        private readonly List<Entity> _announced = new List<Entity>();

        /// <summary>
        /// Client, before the load: the runtime prefabs the host's prefab table names that a mod here creates on demand
        /// (Placemaker's park companions) are requested now, so the game's loader finds them in the city
        /// (docs/design/MOD-COMPATIBILITY.md §7). Only names are read; the codec binds the table when the client is live.
        /// </summary>
        private void RequestRuntimePrefabs(byte[] body)
        {
            if (body == null || SyncBatch.IsMoverBatch(body, 0, body.Length)) return;
            int requested = 0;
            foreach (SyncRecord r in SyncBatch.Read(body, 0, body.Length))
            {
                if (r.Kind != SyncRecordKind.Prefab || string.IsNullOrEmpty(r.PrefabName)) continue;
                Core.Modding.RuntimePrefabState state = Compat.ModCompat.RuntimePrefab(null, r.PrefabName, _log);
                if (state == Core.Modding.RuntimePrefabState.Pending || state == Core.Modding.RuntimePrefabState.Ready) requested++;
            }
            if (requested > 0) _log.Info(LogCategory.Compat, "mod compatibility: " + requested + " runtime prefab(s) of the host's city requested before the load");
        }

        /// <summary>Client: a tool applies this frame, so this frame's untagged mod state changes are the player's edits (<see cref="Core.Modding.ModStateProposalRule"/>).</summary>
        private bool _modStateToolPath = true;

        /// <summary>Client: mod state changes not proposed because no tool or input preceded them (diagnostics).</summary>
        private long _derivedModChanges;

        private readonly Compat.PlayerInputWatch _input = new Compat.PlayerInputWatch();

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
