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
            // deletions from the UI (district, service upgrade, line: R10 D1-D3) happen outside any
            // tool; outside a tool path only those are proposed, every other local deletion waits for the host
            ProposalFlags flags = _toolApplied ? ProposalFlags.ToolApply : localTool ? ProposalFlags.DirectTool : ProposalFlags.None;
            _batch.Reset();
            HashSet<Entity> written = _clientWritten;
            List<uint> proposed = _clientProposed;
            written.Clear();
            proposed.Clear();
            // mod compatibility: mod data entities the proposed values reference get ids from the pool and travel along
            _codec.DependencyBinder = ClientBindDependency;
            // outside a tool path only deletions count, so whole chunks without Deleted are skipped instead of
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
            // the proposal's entities in two passes: every new entity of the frame gets its id first, then the records are
            // written. Written in chunk order, a new edge whose new node came in a later chunk could not name it, and its
            // Edge value travelled as presence only (the host then created an edge with null nodes: a native crash).
            List<PendingWrite> writes = _clientWrites;
            List<uint> deletions = _clientDeletions;
            writes.Clear();
            deletions.Clear();
            uint nextSeq = _seq + 1;
            try
            {
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
                            _actions.NoteSubElementDeleted(e); // sent as derived keys (intent)
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
                            // the id stays bound until the proposal is written and checked (FinishDeletions), so the
                            // check sees what a kept record still names
                            _batch.Despawn(id);
                            deletions.Add(id);
                            continue;
                        }
                        bool authored = em.HasComponent<Game.Common.Applied>(e) || _toolOriginals.Contains(e) || (localTool && !_toolApplied);
                        if (!authored) continue;
                        bool created = false;
                        if (id == 0)
                        {
                            if (!_pool.TryTake(out id))
                            {
                                _log.Warn(LogCategory.Replication, "out of net ids; an edit could not be sent (it will be corrected by the host)");
                                continue;
                            }
                            _ids.Bind(e, id, family);
                            created = true;
                        }
                        writes.Add(new PendingWrite { Entity = e, Id = id, Family = family, Created = created });
                    }
                    foreach (PendingWrite w in writes)
                    {
                        if (!em.Exists(w.Entity)) continue;
                        ulong hash = _codec.HashEntity(w.Entity, false);
                        if (!w.Created && _shadow.TryGetValue(w.Id, out ulong old) && old == hash) continue;
                        SetShadow(w.Id, hash);
                        _codec.WriteEntity(_batch, w.Created ? SyncRecordKind.Spawn : SyncRecordKind.Upsert, w.Id, w.Family, w.Entity);
                        written.Add(w.Entity);
                        proposed.Add(w.Id);
                    }
                }
                finally
                {
                    changed.Clear();
                    writes.Clear();
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
                // sequence, base ledger epoch and kind of the proposal (host validation, rollback)
                if (!_batch.IsEmpty) _batch.ProposalHeader(++_seq, _baseEpoch, flags);
                if (_pool.ShouldRequestMore(_sessions.Clock.Seconds)) _batch.Request(SyncRequest.MoreIds); // retried with backoff
                if (!_batch.IsEmpty)
                {
                    ArraySegment<byte> bytes = _batch.Finish();
                    if (proposed.Count > 0 || deletions.Count > 0) CheckOwnProposal(bytes, nextSeq, now);
                    _sessions.Client?.SendToHost(MessageKind.Proposal, bytes.Array, bytes.Offset, bytes.Count);
                    Perf.Counters.AddBytes(Perf.ProposalBytes, bytes.Count, now);
                }
            }
            finally
            {
                FinishDeletions(deletions, nextSeq);
            }
            HashBaselines(false); // a budgeted slice of the applier's entities (all of them before a tool can edit)
        }

        /// <summary>An entity the client's capture proposes (written in the second pass, after every new id is bound).</summary>
        private struct PendingWrite
        {
            public Entity Entity;
            public uint Id;
            public Family Family;
            public bool Created;
        }

        private readonly List<PendingWrite> _clientWrites = new List<PendingWrite>();
        private readonly List<uint> _clientDeletions = new List<uint>();

        /// <summary>
        /// Client: the ids this proposal deletes are unbound once it is written and checked: until the host's despawn (or
        /// its rollback) arrives, records the host sent before it applied the deletion must not bring them back.
        /// </summary>
        private void FinishDeletions(List<uint> deletions, uint seq)
        {
            double until = _sessions.Clock.Seconds + LocalDeleteSeconds;
            foreach (uint id in deletions)
            {
                _ids.Unbind(id);
                ForgetShadow(id);
                _localDeletes[id] = new LocalDelete { Seq = seq, Until = until };
                Integrity.OnLocalDelete(id);
            }
            deletions.Clear();
        }

        private readonly List<StructureChange> _ownChanges = new List<StructureChange>();
        private readonly List<StructureProblem> _ownProblems = new List<StructureProblem>();
        private readonly StructureIntegrity _ownIntegrity = new StructureIntegrity();
        private StructureFacts _ownFacts;
        private double _lastOwnProblemLog = double.NegativeInfinity;
        private int _ownProblemsSuppressed;

        /// <summary>
        /// Client: its own proposal judged by the host's rule (<see cref="StructureIntegrity"/>, ARCHITECTURE.md#client-proposals)
        /// on its own world, where the change is already applied (deleted entities still carry <c>Deleted</c>). A proposal
        /// that would leave a dangling reference on the host is still sent: the host refuses it and its rollback restores
        /// the host's state here; the log line names what was wrong (the capture's own diagnostics).
        /// </summary>
        private void CheckOwnProposal(ArraySegment<byte> bytes, uint seq, double now)
        {
            try
            {
                List<SyncRecord> records = SyncBatch.Read(bytes.Array, bytes.Offset, bytes.Count);
                _codec.BeginPeek(bytes.Array, bytes.Offset, bytes.Count);
                _codec.PeekStructure(records, _ownChanges);
                _ownProblems.Clear();
                if (_ownFacts == null) _ownFacts = new StructureFacts(_ids);
                RejectReason reason = _ownIntegrity.Check(_ownChanges, _ownFacts.Use(_codec.EntityManager, true), _ownProblems, 4, worldIncludesChanges: true);
                if (reason == RejectReason.None) return;
                Perf.Counters.Count("own proposals with dangling references");
                if (now - _lastOwnProblemLog < 2.0)
                {
                    _ownProblemsSuppressed++;
                    return;
                }
                _lastOwnProblemLog = now;
                _log.Warn(LogCategory.Replication, "own change " + seq + " would leave dangling references on the host (" + reason + "; the host will refuse it and roll it back): "
                                                   + StructureIntegrity.Summarize(_ownProblems, _ownIntegrity.ProblemCount)
                                                   + (_ownProblemsSuppressed > 0 ? " (" + _ownProblemsSuppressed + " earlier such change(s) not logged)" : ""));
                _ownProblemsSuppressed = 0;
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Replication, "checking the own proposal failed: " + ex.Message);
            }
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
                // one line per close: the host logs the re-send it causes, and this names the reason on our side
                _log.Info(LogCategory.Replication, "route tool closed: asking the host to re-send its lines");
                var w = new SyncBatch.Writer();
                w.Request(SyncRequest.ResendRoutes);
                ArraySegment<byte> bytes = w.Finish();
                _sessions.Client?.SendToHost(MessageKind.Proposal, bytes.Array, bytes.Offset, bytes.Count);
            }
        }
    }
}
