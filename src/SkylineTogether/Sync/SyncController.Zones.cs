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
    // Zone cells: keyed state on derived zone blocks.
    public sealed partial class SyncController
    {
        // ================================================================== zone cells (keyed state on derived blocks)

        /// <summary>Zone cells of a block as a keyed record; <paramref name="force"/> also writes an empty fresh block (rollback).</summary>
        private void WriteZoneBlock(SyncBatch.Writer w, Entity block, bool force)
        {
            EntityManager em = _codec.EntityManager;
            if (!em.HasComponent<Game.Common.Owner>(block) || !em.HasComponent<Game.Zones.Block>(block) || !em.HasBuffer<Game.Zones.Cell>(block)) return;
            uint owner = _ids.GetNetId(em.GetComponentData<Game.Common.Owner>(block).m_Owner);
            if (owner == 0) return;
            DynamicBuffer<Game.Zones.Cell> cells = em.GetBuffer<Game.Zones.Cell>(block, true);
            var zones = new uint[cells.Length];
            bool any = false;
            for (int i = 0; i < cells.Length; i++)
            {
                Game.Zones.ZoneType z = cells[i].m_Zone;
                if (z.Equals(Game.Zones.ZoneType.None)) continue;
                Entity prefab = _zones.GetPrefab(z);
                // only the host allocates ids. A client's EnsurePrefab allocated from its local counter
                // (the host's high-water mark at snapshot time), which the host has long handed to new entities and
                // movers since; the host then rebound that id to the zone prefab and lost the entity's binding
                zones[i] = IsHost ? _codec.EnsurePrefab(prefab, w) : _ids.GetNetId(prefab);
                any |= zones[i] != 0;
            }
            if (!any && !force && em.HasComponent<Game.Common.Created>(block)) return; // a fresh empty block: nothing to tell
            w.ZoneCells(BlockKey(owner, em.GetComponentData<Game.Zones.Block>(block)), zones);
        }

        private static ZoneBlockKey BlockKey(uint owner, Game.Zones.Block b)
        {
            return new ZoneBlockKey
            {
                OwnerNetId = owner,
                X = ZoneBlockKey.Quantize(b.m_Position.x, 64f),
                Z = ZoneBlockKey.Quantize(b.m_Position.z, 64f),
                DirX = ZoneBlockKey.Quantize(b.m_Direction.x, 1024f),
                DirZ = ZoneBlockKey.Quantize(b.m_Direction.y, 1024f),
                SizeX = b.m_Size.x,
                SizeY = b.m_Size.y,
            };
        }

        private void ApplyZoneRecords(List<SyncRecord> records)
        {
            double now = _sessions.Clock.Seconds;
            bool fromHost = !IsHost;
            for (int i = 0; i < records.Count; i++)
            {
                SyncRecord r = records[i];
                // gameplay branch: the host's ledger epochs (conflict base) and proposal results (rollback toast)
                if (fromHost && r.Kind == SyncRecordKind.Epoch)
                {
                    _baseEpoch = r.EpochNumber;
                    continue;
                }
                if (fromHost && r.Kind == SyncRecordKind.ProposalResult)
                {
                    OnProposalResult(r);
                    continue;
                }
                if (fromHost && r.Kind == SyncRecordKind.ModSettings)
                {
                    SafeCall("mod settings", () => _modSettings.Apply(r)); // mod compatibility: the host's rules
                    continue;
                }
                if (r.Kind != SyncRecordKind.ZoneCells) continue;
                if (!TryApplyZone(r.Block, r.ZoneIds)) _pendingZones.Add(new PendingZone { Key = r.Block, Zones = r.ZoneIds, Until = now + 3.0 });
            }
        }

        /// <summary>Client: the host refused one of this player's proposals; the resend in the same batch rolled it back.</summary>
        private void OnProposalResult(SyncRecord r)
        {
            if (r.Reason == RejectReason.None) return;
            string text = "The host undid your last change: " + ProposalRules.Describe(r.Reason) + ".";
            _log.Info(LogCategory.Replication, "proposal " + r.Seq + " rejected (" + r.Reason + ")");
            double now = _sessions.Clock.Seconds;
            // one toast per reason and second, however many proposals were in flight
            if (r.Reason == _lastToastReason && now - _lastToast < 1.0) return;
            _lastToastReason = r.Reason;
            _lastToast = now;
            SessionManager.Notify(text);
        }

        private RejectReason _lastToastReason;
        private double _lastToast = double.NegativeInfinity;

        private void RetryZones()
        {
            if (_pendingZones.Count == 0) return;
            double now = _sessions.Clock.Seconds;
            for (int i = _pendingZones.Count - 1; i >= 0; i--)
            {
                PendingZone p = _pendingZones[i];
                if (TryApplyZone(p.Key, p.Zones)) _pendingZones.RemoveAt(i);
                else if (now > p.Until)
                {
                    _log.Debug(LogCategory.Replication, "zone block of #" + p.Key.OwnerNetId + " not found; dropped");
                    _pendingZones.RemoveAt(i);
                }
            }
        }

        private bool TryApplyZone(ZoneBlockKey key, uint[] zones)
        {
            EntityManager em = _codec.EntityManager;
            Entity best = FindBlock(key);
            if (best == Entity.Null || !em.HasBuffer<Game.Zones.Cell>(best)) return false;
            DynamicBuffer<Game.Zones.Cell> cells = em.GetBuffer<Game.Zones.Cell>(best);
            if (cells.Length != zones.Length) return false;
            for (int i = 0; i < cells.Length; i++)
            {
                Game.Zones.Cell c = cells[i];
                Game.Zones.ZoneType z = Game.Zones.ZoneType.None;
                if (zones[i] != 0)
                {
                    Entity prefab = _ids.GetEntity(zones[i]);
                    if (prefab != Entity.Null && em.HasComponent<ZoneData>(prefab)) z = em.GetComponentData<ZoneData>(prefab).m_ZoneType;
                }
                c.m_Zone = z;
                cells[i] = c;
            }
            if (!em.HasComponent<Game.Common.Updated>(best)) em.AddComponent<Game.Common.Updated>(best);
            _appliedThisFrame.Add(best);
            return true;
        }

        /// <summary>The live zone block of a key: same owner edge, size and direction, nearest position within 1 m.</summary>
        private Entity FindBlock(ZoneBlockKey key)
        {
            EntityManager em = _codec.EntityManager;
            Entity owner = _ids.GetEntity(key.OwnerNetId);
            if (owner == Entity.Null || !em.Exists(owner) || !em.HasBuffer<Game.Zones.SubBlock>(owner)) return Entity.Null;
            DynamicBuffer<Game.Zones.SubBlock> blocks = em.GetBuffer<Game.Zones.SubBlock>(owner, true);
            Entity best = Entity.Null;
            long bestDistance = 64L * 64L + 1;
            for (int i = 0; i < blocks.Length; i++)
            {
                Entity b = blocks[i].m_SubBlock;
                if (!em.HasComponent<Game.Zones.Block>(b) || em.HasComponent<Game.Common.Deleted>(b)) continue;
                ZoneBlockKey k = BlockKey(key.OwnerNetId, em.GetComponentData<Game.Zones.Block>(b));
                if (k.SizeX != key.SizeX || k.SizeY != key.SizeY || Math.Abs(k.DirX - key.DirX) > 8 || Math.Abs(k.DirZ - key.DirZ) > 8) continue;
                long d = k.DistanceSquared(key);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = b;
                }
            }
            return best;
        }
    }
}
