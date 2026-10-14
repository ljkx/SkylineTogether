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
    // The client's capture of its own edits into proposals, and the route-tool preview window.
    public sealed partial class SyncController
    {
        private void CaptureClient(double now)
        {
            EntityManager em = _codec.EntityManager;
            UpdateRouteWindow(now);
            // entities the applier wrote this frame: their converged state becomes the baseline, so they are never proposed
            // back; it is hashed later (HashBaselines), at the latest right before a tool may edit them
            foreach (Entity e in _appliedThisFrame)
            {
                if (!em.Exists(e)) continue;
                uint id = _ids.GetNetId(e);
                if (id != 0)
                {
                    _shadow.Remove(id);
                    _baselinesPending.Mark(id);
                }
                _watcher?.Refresh(e); // mod compatibility: the applier's writes are not the player's edits
            }
            // vanilla tools always go through ApplyTool (captured via _toolApplied); only tools from other mods may
            // write the world directly (Move It), so only those make un-applied changes count as the player's edits
            // mod compatibility: mod tools that apply through the Temp pipeline (Traffic, Line Tool, Find It's picker) are not
            // direct-write tools; their applies arrive through ApplyTool like vanilla ones
            bool localTool = _tools.activeTool != null && !(_tools.activeTool is DefaultToolSystem)
                             && _tools.activeTool.GetType().Assembly != typeof(DefaultToolSystem).Assembly
                             && !Compat.ModCompat.IsPipelineTool(_tools.activeTool.GetType());
            bool toolPath = _toolApplied || localTool;
            // gameplay branch: deletions from the UI (district, service upgrade, line: R10 D1-D3) happen outside any
            // tool; outside a tool path only those are proposed, every other local deletion waits for the host
            ProposalFlags flags = _toolApplied ? ProposalFlags.ToolApply : localTool ? ProposalFlags.DirectTool : ProposalFlags.None;
            _batch.Reset();
            HashSet<Entity> written = _clientWritten;
            List<uint> proposed = _clientProposed;
            written.Clear();
            proposed.Clear();
            // mod compatibility: mod data entities the proposed values reference get ids from the pool and travel along
            _codec.DependencyBinder = ClientBindDependency;
            // perf pass: outside a tool path only deletions count, so whole chunks without Deleted are skipped instead of
            // copying and testing every tagged entity of the frame (the simulation and the applier tag many each frame)
            List<Entity> changed = _clientChanged;
            changed.Clear();
            if (!_changed.IsEmptyIgnoreFilter)
            {
                NativeArray<ArchetypeChunk> chunks = _changed.ToArchetypeChunkArray(Allocator.Temp);
                try
                {
                    EntityTypeHandle entityType = em.GetEntityTypeHandle();
                    foreach (ArchetypeChunk chunk in chunks)
                    {
                        if (!toolPath && !chunk.Has<Game.Common.Deleted>()) continue;
                        NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                        for (int i = 0; i < entities.Length; i++) changed.Add(entities[i]);
                    }
                }
                finally
                {
                    chunks.Dispose();
                }
            }
            try
            {
                foreach (Entity e in changed)
                {
                    bool isDeleted = em.HasComponent<Game.Common.Deleted>(e);
                    // an entity the applier touched and the local game then deleted is still a deletion to handle
                    if (_appliedThisFrame.Contains(e) && !isDeleted) continue;
                    if (!toolPath && !isDeleted) continue;
                    Family family = _codec.Families.Classify(em, e);
                    // mod compatibility: vehicles, cims and animals a mod tool deleted (Better Bulldozer)
                    if (family == Family.Mover && localTool && em.HasComponent<Game.Common.Deleted>(e))
                    {
                        uint mover = _ids.GetNetId(e);
                        if (mover != 0 && _ids.GetFamily(mover) == Family.Mover) _batch.Despawn(mover);
                        continue;
                    }
                    if (family == Family.ZoneBlock)
                    {
                        if (_toolApplied && !em.HasComponent<Game.Common.Deleted>(e)) WriteZoneBlock(_batch, e, false);
                        continue;
                    }
                    if (_subElementFrame && (family == Family.PrefabSubObject || family == Family.Lane) && em.HasComponent<Game.Common.Deleted>(e))
                    {
                        _actions.NoteSubElementDeleted(e); // gameplay branch: sent as derived keys (intent)
                        continue;
                    }
                    if (!FamilyClassifier.IsReplicated(family)) continue;
                    // events, chirps and journal entries are the host simulation's alone (a local like is per player)
                    if (FamilyClassifier.IsHostOnly(family)) continue;
                    uint id = _ids.GetNetId(e);
                    if (em.HasComponent<Game.Common.Deleted>(e))
                    {
                        if (id == 0) continue;
                        if (!toolPath)
                        {
                            if (!IsUiDeletion(em, e, family))
                            {
                                // a cascade of the local game (the owner was despawned: sub-nets, sub-areas,
                                // upgrades) destroys the entity at the end of this frame. Its binding must not outlive it:
                                // a later record naming the id would otherwise resolve to a destroyed entity (a dangling
                                // reference the game's jobs follow unchecked). Not proposed: the host deletes it itself.
                                _ids.Unbind(id);
                                ForgetShadow(id);
                                Integrity.OnLocalDelete(id);
                                _cascadeUnbinds++;
                                continue;
                            }
                            flags |= ProposalFlags.UiDelete;
                        }
                        _batch.Despawn(id);
                        _ids.Unbind(id);
                        ForgetShadow(id);
                        // until the host's despawn (or its rollback) arrives, records the host sent before it
                        // applied this deletion must not bring the entity back (in-flight repairs, upserts)
                        _localDeletes[id] = new LocalDelete { Seq = _seq + 1, Until = _sessions.Clock.Seconds + LocalDeleteSeconds };
                        Integrity.OnLocalDelete(id);
                        continue;
                    }
                    bool authored = em.HasComponent<Game.Common.Applied>(e) || _toolOriginals.Contains(e) || (localTool && !_toolApplied);
                    if (!authored) continue;
                    SyncRecordKind kind = SyncRecordKind.Upsert;
                    ulong hash = _codec.HashEntity(e, false);
                    if (id == 0)
                    {
                        if (!_pool.TryTake(out id))
                        {
                            _log.Warn(LogCategory.Replication, "out of net ids; an edit could not be sent (it will be corrected by the host)");
                            continue;
                        }
                        _ids.Bind(e, id, family);
                        kind = SyncRecordKind.Spawn;
                    }
                    else if (_shadow.TryGetValue(id, out ulong old) && old == hash)
                    {
                        continue;
                    }
                    SetShadow(id, hash);
                    _codec.WriteEntity(_batch, kind, id, family, e);
                    written.Add(e);
                    proposed.Add(id);
                }
            }
            finally
            {
                changed.Clear();
            }
            // mod compatibility: untagged mod state the player changed (components tool, info-panel toggles, Better
            // Bulldozer's removal records), then the dependencies of everything proposed
            WriteDependencies(written, proposed);
            int before = proposed.Count;
            _callNow = now;
            SafeCall("mod state capture", _clientModStateCall); // written/proposed are _clientWritten/_clientProposed
            if (proposed.Count > before) flags |= ProposalFlags.DirectTool;
            WriteDependencies(written, proposed);
            _codec.DependencyBinder = null;
            // a direct-write tool's edits are held against the host's echoes while the player keeps editing
            if ((localTool && !_toolApplied) || proposed.Count > before)
            {
                foreach (uint id in proposed)
                {
                    _holds.TryGetValue(id, out EditHold hold);
                    hold.Until = now + HoldSeconds;
                    _holds[id] = hold;
                }
            }
            if (_toolCost != 0) _batch.Cost(_toolCost);
            // gameplay branch: sequence, base ledger epoch and kind of the proposal (host validation, rollback)
            if (!_batch.IsEmpty) _batch.ProposalHeader(++_seq, _baseEpoch, flags);
            if (_pool.ShouldRequestMore(_sessions.Clock.Seconds)) _batch.Request(SyncRequest.MoreIds); // retried with backoff
            if (!_batch.IsEmpty)
            {
                ArraySegment<byte> bytes = _batch.Finish();
                _sessions.Client?.SendToHost(MessageKind.Proposal, bytes.Array, bytes.Offset, bytes.Count);
                Perf.Counters.AddBytes(Perf.ProposalBytes, bytes.Count, now);
            }
            HashBaselines(false); // a budgeted slice of the applier's entities (all of them before a tool can edit)
        }

        /// <summary>Client: entities the applier wrote whose converged state is not hashed yet (<see cref="PendingBaselines"/>).</summary>
        private readonly PendingBaselines _baselinesPending = new PendingBaselines();

        private const double BaselineBudgetMs = 0.5;

        /// <summary>
        /// Client: hashes waiting baselines into the shadow, every one of them (<paramref name="all"/>: a tool may edit the
        /// world next) or a budgeted slice. A baseline must be taken before the player can change the entity, or the edit
        /// would count as the converged state: vanilla tools edit only in their apply phase (<see cref="OnToolApply"/>
        /// hashes everything first) and other tools only while they are active (the frame start hashes everything first
        /// while any tool but the default one is selected). Outside a tool path nothing is compared with the shadow.
        /// </summary>
        private void HashBaselines(bool all)
        {
            if (_baselinesPending.Count == 0 || _codec == null) return;
            EntityManager em = _codec.EntityManager;
            long start = Perf.Start();
            long budget = all ? long.MaxValue : (long)(Perf.Allowance(Perf.BudgetBaselines, BaselineBudgetMs) * Stopwatch.Frequency / 1000.0);
            int n = 0;
            while (_baselinesPending.TryTake(out uint id))
            {
                Entity e = _ids.GetEntity(id);
                if (e != Entity.Null && em.Exists(e)) _shadow[id] = _codec.HashEntity(e, false);
                n++;
                if (!all && (n & 7) == 0 && Stopwatch.GetTimestamp() - start > budget) break;
            }
            Perf.Spent(Perf.BudgetBaselines, start);
        }

        /// <summary>Client: a baseline taken right now (a proposal's own state).</summary>
        private void SetShadow(uint id, ulong hash)
        {
            _shadow[id] = hash;
            _baselinesPending.Remove(id);
        }

        /// <summary>The id no longer has a baseline (deleted or unbound).</summary>
        private void ForgetShadow(uint id)
        {
            _shadow.Remove(id);
            _baselinesPending.Remove(id);
        }

        /// <summary>
        /// Route-tool preview window (ARCHITECTURE.md#client-proposals): while the player draws or edits a line, the local
        /// route path and waypoint systems run so the preview follows the roads; afterwards the host's line state is
        /// requested again, so no locally derived line state survives.
        /// </summary>
        /// <summary>
        /// Opens the route preview window in the frame the route tool is selected (<c>ToolSystem.activeTool</c>
        /// raises <c>EventToolChanged</c>), not at that frame's end capture, so the temp segments the tool creates in its
        /// first ToolUpdate already get their road-following path from <c>RoutePathSystem</c> (an <c>Updated</c> tag is
        /// seen once only). Closing stays with <see cref="UpdateRouteWindow"/> (linger, host line re-send).
        /// </summary>
        private void OnToolChanged(ToolBaseSystem tool)
        {
            try
            {
                if (!(tool is RouteToolSystem) || !IsLiveClient || _routeWindowOpen) return;
                _routeWindowUntil = Perf.Now + RouteWindowLinger; // the capture clock (SyncSystems: realtimeSinceStartup)
                _routeWindowOpen = true;
                _gate.SetRouteWindow(true);
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Gate, "route preview window on tool change: " + ex.Message);
            }
        }

        private void UpdateRouteWindow(double now)
        {
            bool routeTool = _tools.activeTool is RouteToolSystem;
            if (routeTool) _routeWindowUntil = now + RouteWindowLinger;
            bool open = now < _routeWindowUntil;
            if (open == _routeWindowOpen) return;
            _routeWindowOpen = open;
            _gate.SetRouteWindow(open);
            if (!open)
            {
                var w = new SyncBatch.Writer();
                w.Request(SyncRequest.ResendRoutes);
                ArraySegment<byte> bytes = w.Finish();
                _sessions.Client?.SendToHost(MessageKind.Proposal, bytes.Array, bytes.Offset, bytes.Count);
            }
        }
    }
}
