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
    // Frame start: applying what arrived (host batches on clients, proposals and requests on the host), the host clock
    // and binding of created entities.
    public sealed partial class SyncController
    {
        // ================================================================== frame start: apply

        /// <summary>Right after <c>EndFrameBarrier</c> playback, before tools and Modification (R11 §6.2).</summary>
        public void ApplyIncoming()
        {
            // single player (no session): nothing to do, nothing allocated
            if (!SessionActive)
            {
                if (_inbox.Count > 0) _inbox.Clear();
                return;
            }
            _appliedThisFrame.Clear();
            // received stamps and heightmap/water tiles (independent of the entity stream)
            if (Features.Terrain.IsAvailable)
            {
                long terrain = Perf.Start();
                try
                {
                    _terrain.ApplyIncoming();
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Terrain, "terrain apply", ex);
                }
                Perf.Budget.End(Perf.BudgetApply, Perf.Stop(Perf.Terrain, terrain)); // applying what arrived
            }
            if (Features.Water.IsAvailable)
            {
                long water = Perf.Start();
                try
                {
                    _water.ApplyIncoming();
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Water, "water apply", ex);
                }
                Perf.Budget.End(Perf.BudgetApply, Perf.Stop(Perf.Water, water));
            }
            if (Features.SimValues.IsAvailable)
            {
                long maps = Perf.Start();
                try
                {
                    _cellMaps.ApplyIncoming(); // host merges paint patches, clients write map tiles
                    _statistics.ApplyIncoming();
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.SimValues, "map tiles and statistics apply", ex);
                }
                Perf.Budget.End(Perf.BudgetMaps, Perf.Stop(Perf.Maps, maps));
            }
            // client: mod systems gated here that their mod switched back on stay off, from the start of the join on (a
            // mod that re-enables them when the joined city finished loading ran them through the snapshot apply and the
            // catch-up before)
            if (!IsHost && _sessions.Client != null) SafeCall("mod gates", _reassertCall ?? (_reassertCall = ReassertGates), Features.ModCompat);
            if (_codec == null || !Features.Replication.IsAvailable) return;
            if (!IsHost && !IsLiveClient)
            {
                if (_sessions.Client == null && _inbox.Count > 0) _inbox.Clear();
                return;
            }
            long start = Perf.Start();
            double now = Perf.Now;
            // everything the previous frame sent is out: close its release frame
            if (IsHost) _ids.Ids.Seal(_acks.SealFrame(_epochSent), now);
            if (IsHost) KickQueued();
            if (IsHost) Integrity.InvalidateResends(); // repair records are encoded once per unchanged world state
            // client: local global-state edits are detected before host data can overwrite them
            if (!IsHost)
            {
                // the children of what was deleted last frame (bulldozing, despawns) whose owner or parent is gone now go
                // first, before any game system of this frame follows their Owner or Attached (ARCHITECTURE.md#reference-safety)
                if (Orphans.HasWork) SafeCall("orphan sweep", _orphanCall);
                // a selected tool (other than the default one) may edit the world this frame: edit baselines first
                if (_tools.activeTool != null && !(_tools.activeTool is DefaultToolSystem)) HashBaselines(true);
                SafeCall("action detection", _actionDetectCall); // dev tree, map tiles
                if (Features.GlobalState.IsAvailable)
                {
                    try
                    {
                        _global.BeforeApply();
                    }
                    catch (Exception ex)
                    {
                        Guard.Report(Features.GlobalState, "global state (before apply)", ex);
                    }
                }
                if (_holds.Count > 0 && Features.GlobalState.IsAvailable) GlobalBeforeHostWrite(); // a released hold applies host records
                SafeCall("edit holds", _editHoldsCall, Features.ModCompat);
                if (Features.Events.IsAvailable)
                {
                    try
                    {
                        _events.ClientBeforeApply(); // the player's chirp likes, the gated chirp queue
                    }
                    catch (Exception ex)
                    {
                        Guard.Report(Features.Events, "events (before apply)", ex);
                    }
                }
            }
            // a client that just went live catches up with bigger slices
            SessionClient joining = IsHost ? null : _sessions.Client;
            bool catchingUp = joining != null && joining.Timeline.Has(JoinStage.Live) && !joining.Timeline.Has(JoinStage.CaughtUp);
            FrameBudget budget = catchingUp ? _catchUpBudget : _applyBudget;
            int maxBatches = catchingUp ? CatchUpMaxBatchesPerFrame : MaxBatchesPerFrame;
            // apply is the shared budget's first priority (never clipped; measured for the others' reservations)
            double allowance = Perf.Allowance(Perf.BudgetApply, budget.BeginFrame());
            int n = 0;
            while (_inbox.Count > 0 && n < maxBatches)
            {
                // budgeted with carry-over; a batch (a transaction) is never split, and at least one is applied per frame
                if (n > 0 && Perf.Elapsed(start) > allowance) break;
                n++;
                Inbound item = _inbox.Dequeue();
                if (IsHost) _hostInboxQuota.Release(item.Peer, item.Data.Length);
                bool acknowledge = true;
                try
                {
                    if (IsHost)
                    {
                        if (item.IsAck) _acks.OnAck(item.Peer.Value, ApplyAckMessage.Read(item.Data, 0, item.Data.Length));
                        else ApplyProposal(item.Peer, item.Data);
                    }
                    else
                    {
                        ApplyFromHost(item.Data);
                    }
                }
                catch (Exception ex)
                {
                    // client: a failed batch is acknowledged only once its despawns are applied (ARCHITECTURE.md#client-apply)
                    if (!IsHost) acknowledge = RecoverFailedBatch(ex);
                    ApplyFailed(item.Peer, ex);
                    if (!Features.Replication.IsAvailable) return; // the session is ending (client: a rejoin repairs it)
                }
                finally
                {
                    _applyingRecords = null;
                    if (item.Seq != 0 && acknowledge) _sessions.Client?.ApplyAcks.Release(item.Seq);
                }
            }
            // the backlog left after this frame (zero ends the catch-up and logs the join's timings)
            joining?.ReportBacklog(_inbox.Count);
            RetryZones();
            // client: simulation values after the structure batches, so ids spawned this frame resolve
            // (each value message waits for the structure batches that arrived before it: the oldest one still queued)
            long oldestStructure = !IsHost && _inbox.Count > 0 ? _inbox.Peek().Seq : 0;
            if (!IsHost) _simValues.ApplyIncoming(oldestStructure);
            // client: flow status into the derived flow edges (after the structure apply, so new owners resolve)
            if (!IsHost && Features.FlowStatus.IsAvailable)
            {
                try
                {
                    _flow.ApplyIncoming(now);
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.FlowStatus, "flow status apply", ex);
                }
            }
            // client: agent values once the structure batches that spawn them are applied
            if (!IsHost && Features.Agents.IsAvailable) _agents.ApplyIncoming(oldestStructure);
            else if (!IsHost) _agents.DropHeld(); // nothing applies them while off; their acknowledgement holds must not stay
            // client: per-player chirp likes, event target lists, weather phenomenon hotspot frames, tree growth
            if (!IsHost && Features.Events.IsAvailable)
            {
                try
                {
                    _events.ClientAfterApply(_appliedThisFrame);
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Events, "events (after apply)", ex);
                }
            }
            // client: lane signals and signal heads from the host's junction phases
            if (!IsHost && Features.SimValues.IsAvailable)
            {
                long signals = Perf.Start();
                try
                {
                    _signals.Update();
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.SimValues, "traffic lights", ex);
                }
                Perf.Stop(Perf.Signals, signals);
            }
            // client: what the appliers wrote becomes the baseline, so it is never proposed back
            if (!IsHost)
            {
                if (Features.GlobalState.IsAvailable)
                {
                    try
                    {
                        _global.AfterApply();
                    }
                    catch (Exception ex)
                    {
                        Guard.Report(Features.GlobalState, "global state (after apply)", ex);
                    }
                }
                SafeCall("milestones", _milestonesCall);
            }
            if (IsHost) UpdateQuarantine(now);
            // client: the reference sweep (repairs dangling agent links, reports the others) and its requests to the host
            if (!IsHost) _audit.Update(now, _inbox.Count);
            double applied = Perf.Stop(Perf.Apply, start);
            budget.EndFrame(applied);
            Perf.Budget.End(Perf.BudgetApply, applied);
        }

        /// <summary>
        /// A batch could not be applied. On a client the replica may now differ from the host's city: failures count
        /// against replication (critical), so repeated ones end the session and the player is offered a rejoin with a
        /// fresh snapshot (ARCHITECTURE.md#feature-health-and-guards "fail into resync"). On the host, one peer's broken proposals never end the
        /// session: they are logged with a rate limit and the peer is removed once they keep failing.
        /// </summary>
        private void ApplyFailed(PeerId peer, Exception ex)
        {
            string what = ex is DecodeException ? "decoding a batch from " + peer : "applying a batch from " + peer;
            if (!IsHost)
            {
                Guard.Report(Features.Replication, what, ex);
                return;
            }
            if (!_proposalHealth.TryGetValue(peer.Value, out FeatureHealth health))
            {
                _proposalHealth[peer.Value] = health = new FeatureHealth("proposals." + peer.Value, "proposals of " + peer, false);
            }
            FailureVerdict v = health.ReportFailure(ex, _sessions.Clock.Seconds);
            if (v.Log) _log.Warn(LogCategory.Replication, what + " failed" + (v.Suppressed > 0 ? " (" + v.Suppressed + " more not logged)" : "") + ": " + ex.GetType().Name + ": " + ex.Message);
            if (v.JustTripped)
            {
                _log.Error(LogCategory.Replication, peer + ": its changes keep failing on the host; removing it from the session", ex);
                _sessions.Host?.Drop(peer, Core.Session.NetText.Of(Core.Session.NetText.DropApplyFailed)); // a rejoin repairs it: not a kick
            }
        }

        private Action _reassertCall;
        private readonly HashSet<string> _reassertLogged = new HashSet<string>();

        private void ReassertGates()
        {
            Compat.ModCompat.ReassertGates(system =>
            {
                string name = system.GetType().FullName;
                if (_reassertLogged.Add(name)) _log.Warn(LogCategory.Gate, name + " was switched on again while gated on this client (a mod's own setting?); held off for the session");
            });
        }

        /// <summary>Runs a step in its own guard; a failure counts against <paramref name="feature"/> (player actions by default).</summary>
        private void SafeCall(string what, Action action, FeatureHealth feature = null)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Guard.Report(feature ?? Features.Actions, what, ex);
            }
        }

        /// <summary>
        /// Host: frees quarantined ids every peer has applied the despawn of. While a join is in progress, a snapshot may
        /// still be written: ids released from that moment on wait until the join is over (the join barrier).
        /// </summary>
        private void UpdateQuarantine(double now)
        {
            SessionHost host = _sessions.Host;
            if (host == null) return;
            if (host.JoinInProgress)
            {
                if (_barrier == 0) _barrier = Math.Max(1u, _acks.CurrentFrame);
            }
            else
            {
                _barrier = 0;
            }
            _ids.Ids.Update(_acks.MinAckedFrame, _barrier, now);
            // a live peer that stopped applying (its replication feature switched off, its apply is stuck)
            // pins the quarantine and only falls further behind: a rejoin with a fresh snapshot is its catch-up
            // (live peers only: a joiner acknowledges nothing while it downloads and loads the city; checked once a second)
            if (now - _lastAckStallCheck < 1.0) return;
            _lastAckStallCheck = now;
            _livePeerScratch.Clear();
            foreach (PlayerInfo player in host.GetPlayers())
            {
                if (player.Id != PeerId.Host && player.State == PlayerState.Live) _livePeerScratch.Add(player.Id.Value);
            }
            foreach (byte stalled in _ackStall.Update(_livePeerScratch, _acks.AckedFrame, _acks.CurrentFrame, now))
            {
                var peer = new PeerId(stalled);
                _kicks.Add((peer, peer + " has applied nothing for " + (int)AckStallSeconds + " s (" + (_acks.CurrentFrame - _acks.AckedFrame(stalled)) + " release frames behind)",
                            Core.Session.NetText.DropNotApplying));
            }
        }

        /// <summary>Host: drops the peers queued for removal (outside the transport callbacks).</summary>
        private void KickQueued()
        {
            if (_kicks.Count == 0) return;
            SessionHost host = _sessions.Host;
            foreach ((PeerId peer, string log, string reason) in _kicks)
            {
                _log.Warn(LogCategory.Replication, log + "; removing it from the session");
                host?.Drop(peer, Core.Session.NetText.Of(reason)); // a technical removal: the player is offered a rejoin
            }
            _kicks.Clear();
        }

        /// <summary>Client: the records of the host batch being applied (null before it was decoded).</summary>
        private List<SyncRecord> _applyingRecords;

        private long _failedBatches;
        private readonly List<uint> _recoveryIds = new List<uint>();
        private readonly HashSet<Entity> _salvageTouched = new HashSet<Entity>();

        /// <summary>
        /// Client: a host batch threw while being applied. Acknowledging it tells the host it may reuse the ids the batch
        /// despawned, so it is acknowledged only when every one of those despawns is applied now (their ids unbound, their
        /// entities deleted cleanly; the codec already destroyed whatever the batch half-created). Everything the batch
        /// carried is asked for again. A batch that could not even be decoded, or whose despawns cannot be applied, is
        /// never acknowledged: replication is switched off, which ends the session with a rejoin offer (a fresh snapshot).
        /// Returns whether the batch may be acknowledged.
        /// </summary>
        private bool RecoverFailedBatch(Exception ex)
        {
            List<SyncRecord> records = _applyingRecords;
            _applyingRecords = null;
            _failedBatches++;
            if (_codec == null || !FailedBatchRecovery.MayAcknowledge(records != null, 0))
            {
                Guard.Disable(Features.Replication, "a batch from the host could not be read (" + ex.GetType().Name + "), so what it deleted is unknown");
                return false;
            }
            _recoveryIds.Clear();
            FailedBatchRecovery.DespawnsToApply(records, _recoveryIds);
            int despawns = _recoveryIds.Count, stillBound;
            _salvageTouched.Clear();
            try
            {
                stillBound = _codec.SalvageDespawns(_recoveryIds, _salvageTouched);
            }
            catch (Exception)
            {
                stillBound = Math.Max(1, despawns);
            }
            // what the salvage tagged is the applier's write like any apply's: not captured back, and its agent links
            // checked by the reference audit
            if (_salvageTouched.Count > 0)
            {
                foreach (Entity e in _salvageTouched) _appliedThisFrame.Add(e);
                try
                {
                    _audit.AfterApply(_salvageTouched, Perf.Now);
                }
                catch (Exception audit)
                {
                    Guard.Report(Features.Integrity, "reference audit after a failed batch", audit);
                }
                _salvageTouched.Clear();
            }
            _recoveryIds.Clear();
            FailedBatchRecovery.IdsToRequest(records, _recoveryIds);
            int asked = 0;
            if (Features.Integrity.IsAvailable)
            {
                try
                {
                    foreach (uint id in _recoveryIds)
                    {
                        Integrity.RequestRepair(id);
                        asked++;
                    }
                }
                catch (Exception repair)
                {
                    Guard.Report(Features.Integrity, "repair requests after a failed batch", repair);
                }
            }
            _recoveryIds.Clear();
            if (!FailedBatchRecovery.MayAcknowledge(true, stillBound))
            {
                // an id still bound, or an entity left without its deletion tag: the host would reuse the id while this
                // client keeps the old entity
                Guard.Disable(Features.Replication, stillBound + " deletion(s) of a batch that failed to apply could not be applied");
                return false;
            }
            _log.Warn(LogCategory.Replication, "a batch from the host failed to apply (" + ex.GetType().Name + ": " + ex.Message + "); its " + despawns + " deletion(s) were applied and "
                                               + asked + " entities are asked for again");
            return true;
        }

        private void ApplyFromHost(byte[] data)
        {
            List<SyncRecord> records = SyncBatch.Read(data, 0, data.Length, _decodePool); // decoded once for every consumer (pooled records)
            _applyingRecords = records;
            if (Features.GlobalState.IsAvailable && MayWriteGlobalState(records)) GlobalBeforeHostWrite();
            // ids this player deleted locally wait for the host's despawn; a rejected proposal releases its ids
            // (the rollback in the same message recreates them)
            if (_localDeletes.Count > 0) UpdateLocalDeletes(records);
            // mod compatibility: records of entities the player is dragging with a direct-write tool wait (edit hold)
            Func<SyncRecord, bool> skip = null;
            if (_holds.Count > 0 || _localDeletes.Count > 0)
            {
                double now = _sessions.Clock.Seconds;
                skip = rec =>
                {
                    if (rec.Kind == SyncRecordKind.Despawn)
                    {
                        _holds.Remove(rec.NetId); // a deleted entity is never held (its id may be reused later)
                        _localDeletes.Remove(rec.NetId);
                        return false;
                    }
                    if (_localDeletes.ContainsKey(rec.NetId))
                    {
                        _localDeleteSkips++;
                        return true;
                    }
                    if (!_holds.TryGetValue(rec.NetId, out EditHold hold) || now >= hold.Until) return false;
                    hold.Batch = data;
                    _holds[rec.NetId] = hold;
                    return true;
                };
            }
            // the host's batch judged by the same referential-integrity rule as a proposal on this world; what would
            // dangle here is not kept dangling by the apply (not created, or deleted cleanly) and is asked for again
            CheckHostBatch(records, data);
            // references to ids not bound here are collected, so the reference audit can ask the host for them
            _codec.MissingIds = _audit.MissingIds;
            ApplyResult r;
            try
            {
                r = _codec.Apply(records, data, 0, data.Length, null, skip);
            }
            finally
            {
                _codec.MissingIds = null;
            }
            // the street lists and road edges the batch wrote take their derived direction with them (ARCHITECTURE.md#derived-net-links)
            AfterApplyLinks(r.Touched);
            if (_structureRepairs.Count > 0 || r.DanglingIds.Count > 0) RequestStructureRepairs(r);
            // no agent link the batch wrote, tagged or cascaded is left dangling for this frame's game systems
            // (ARCHITECTURE.md#reference-safety)
            double applied = Perf.Now;
            _audit.NoteMissing(applied);
            _audit.AfterApply(r.Touched, applied);
            // mod compatibility: tell the player once that objects of missing assets are left out (details in the log)
            if (!_missingNotified && _codec.MissingAssets.Count > 0)
            {
                _missingNotified = true;
                // name the packages and mods (and what to do) in the player's language instead of only pointing at the diagnostics
                SessionManager.Notify(Core.Modding.MissingAssets.Summary(_codec.MissingBySource(), Settings.LocaleSource.Lookup), seconds: 15f);
            }
            foreach (Entity e in r.Touched) _appliedThisFrame.Add(e);
            foreach ((uint start, uint count) in r.IdRanges) _pool.Add(start, count);
            if (r.Clock.HasValue) FollowClock(r.Clock.Value);
            if (r.Spawned + r.Upserted + r.Despawned > 0) LastApply = r.ToString();
            if (r.Problems.Count > 0 && _log.IsEnabled(LogLevel.Debug)) _log.Debug(LogCategory.Replication, "apply problems: " + string.Join("; ", r.Problems)); // joined only when logged
            ApplyZoneRecords(records);
            ApplyGlobal(data, PeerId.Host, false);
            if (Features.Integrity.IsAvailable)
            {
                try
                {
                    Integrity.OnHostBatch(records, _inbox.Count); // presence masks, digests, icons, line rows
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Integrity, "integrity (host batch)", ex);
                }
            }
            // a despawn whose entity survived (its deletion threw): the batch is handled like a failed one, so it is
            // acknowledged only once the salvage deleted that entity (RecoverFailedBatch)
            if (r.DespawnsNotApplied > 0) throw new InvalidOperationException(r.DespawnsNotApplied + " deletion(s) of the batch left their entity");
        }

        private readonly List<StructureChange> _batchChanges = new List<StructureChange>();
        private readonly List<StructureProblem> _batchProblems = new List<StructureProblem>();
        private readonly StructureIntegrity _batchIntegrity = new StructureIntegrity();
        private readonly List<uint> _structureRepairs = new List<uint>();
        private StructureFacts _batchFacts;
        private double _lastBatchProblemLog = double.NegativeInfinity;
        private int _batchProblemsSuppressed;

        /// <summary>
        /// Client (ARCHITECTURE.md#client-apply): a host batch that deletes or writes nodes and edges is judged by
        /// <see cref="StructureIntegrity"/> on this client's world before it is applied, like a proposal on the host. The
        /// host's world is consistent, so a problem means this client differs (an edge of its own prediction at a node the
        /// host deleted, a node it lacks): the apply then creates nothing dangling and deletes what would dangle
        /// (<see cref="EntityCodec"/>), and the ids involved are asked for again.
        /// </summary>
        private void CheckHostBatch(List<SyncRecord> records, byte[] data)
        {
            _structureRepairs.Clear();
            // only batches that touch nets: written nodes or edges, or a despawn of a node or edge bound here (agent and
            // event despawns arrive every frame)
            bool nets = false;
            foreach (SyncRecord rec in records)
            {
                Family family = rec.Kind == SyncRecordKind.Despawn ? _ids.GetFamily(rec.NetId)
                                : rec.Kind == SyncRecordKind.Spawn || rec.Kind == SyncRecordKind.Upsert ? rec.Family : Family.Generic;
                if (family == Family.NetEdge || family == Family.NetNode)
                {
                    nets = true;
                    break;
                }
            }
            if (!nets) return;
            try
            {
                _codec.BeginPeek(data, 0, data.Length);
                _codec.PeekStructure(records, _batchChanges);
                _batchProblems.Clear();
                if (_batchFacts == null) _batchFacts = new StructureFacts(_ids);
                RejectReason reason = _batchIntegrity.Check(_batchChanges, _batchFacts.Use(_codec.EntityManager, false), _batchProblems, 4);
                if (reason == RejectReason.None) return;
                Perf.Counters.Count("host batches with dangling references here");
                foreach (StructureProblem p in _batchProblems)
                {
                    if (p.Subject != 0) _structureRepairs.Add(p.Subject);
                    if (p.Other != 0) _structureRepairs.Add(p.Other);
                }
                double now = _sessions.Clock.Seconds;
                if (now - _lastBatchProblemLog < 10.0)
                {
                    _batchProblemsSuppressed++;
                    return;
                }
                _lastBatchProblemLog = now;
                _log.Info(LogCategory.Replication, "host batch would leave dangling references here (" + _batchIntegrity.ProblemCount + "): "
                                                   + StructureIntegrity.Summarize(_batchProblems, _batchIntegrity.ProblemCount)
                                                   + "; not kept dangling (not created, or deleted) and asked for again"
                                                   + (_batchProblemsSuppressed > 0 ? " (" + _batchProblemsSuppressed + " earlier such batch(es) not logged)" : ""));
                _batchProblemsSuppressed = 0;
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Replication, "checking a host batch failed: " + ex.Message);
            }
        }

        /// <summary>Client: asks the host again for what a batch would have left dangling here (integrity repair).</summary>
        private void RequestStructureRepairs(ApplyResult r)
        {
            if (!Features.Integrity.IsAvailable)
            {
                _structureRepairs.Clear();
                return;
            }
            try
            {
                foreach (uint id in _structureRepairs) Integrity.RequestRepair(id);
                foreach (uint id in r.DanglingIds) Integrity.RequestRepair(id);
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Integrity, "repair requests after a host batch", ex);
            }
            _structureRepairs.Clear();
        }

        /// <summary>
        /// Client: records that may change state the global channels compare with their baseline (fees and loan on the City,
        /// budgets on their singleton, followed citizens, names and the ids they are keyed by).
        /// </summary>
        private static bool MayWriteGlobalState(List<SyncRecord> records)
        {
            for (int i = 0; i < records.Count; i++)
            {
                SyncRecord r = records[i];
                switch (r.Kind)
                {
                    case SyncRecordKind.Spawn:
                    case SyncRecordKind.Despawn:
                        return true;
                    case SyncRecordKind.Upsert:
                    case SyncRecordKind.Values:
                        if (r.Family == Family.Singleton || r.Family == Family.Agent) return true;
                        break;
                }
            }
            return false;
        }

        private void GlobalBeforeHostWrite()
        {
            try
            {
                _global.BeforeHostWrite();
            }
            catch (Exception ex)
            {
                Guard.Report(Features.GlobalState, "global state (before a host write)", ex);
            }
        }

        /// <summary>Client: an entity this player deleted, waiting for the host's despawn.</summary>
        private struct LocalDelete
        {
            public uint Seq;
            public double Until;
        }

        private const double LocalDeleteSeconds = 20;
        private readonly Dictionary<uint, LocalDelete> _localDeletes = new Dictionary<uint, LocalDelete>();
        private readonly List<uint> _localDeleteScratch = new List<uint>();
        private long _localDeleteSkips, _cascadeUnbinds;

        /// <summary>
        /// Client: expires local deletions the host never answered (after <see cref="LocalDeleteSeconds"/> the
        /// integrity check takes over) and releases those of proposals this batch reports as rejected.
        /// </summary>
        private void UpdateLocalDeletes(List<SyncRecord> records)
        {
            double now = _sessions.Clock.Seconds;
            _localDeleteScratch.Clear();
            foreach (KeyValuePair<uint, LocalDelete> pair in _localDeletes)
            {
                if (now >= pair.Value.Until) _localDeleteScratch.Add(pair.Key);
            }
            foreach (SyncRecord r in records)
            {
                if (r.Kind != SyncRecordKind.ProposalResult || r.Reason == RejectReason.None) continue;
                foreach (KeyValuePair<uint, LocalDelete> pair in _localDeletes)
                {
                    if (pair.Value.Seq == r.Seq) _localDeleteScratch.Add(pair.Key);
                }
            }
            foreach (uint id in _localDeleteScratch) _localDeletes.Remove(id);
        }

        private void ApplyGlobal(byte[] data, PeerId peer, bool proposal)
        {
            if (!Features.GlobalState.IsAvailable) return;
            try
            {
                _global.Apply(data, peer, proposal);
            }
            catch (Exception ex)
            {
                Guard.Report(Features.GlobalState, "global state apply", ex);
            }
        }

        private void ApplyProposal(PeerId peer, byte[] data)
        {
            List<SyncRecord> records = SyncBatch.Read(data, 0, data.Length, _decodePool);
            // validation (ARCHITECTURE.md#host-validation-and-commit); a refused proposal is rolled back on its originator by a resend
            ProposalFacts facts = Facts(records);
            uint seq = facts.Seq;
            // a proposal that waited while its sender left (or was replaced by a reconnect) changes nothing; there is
            // nobody to roll back (a joiner's snapshot carries the host's state)
            if (ProposalRules.CheckSender(_sessions.Host?.GetPeerOutput(peer) != null) != RejectReason.None)
            {
                Perf.Counters.Count("proposals of departed peers dropped");
                _log.Debug(LogCategory.Replication, peer + ": dropped proposal " + seq + " (" + RejectReason.NotLive + ": the player is no longer live)");
                return;
            }
            bool isGlobal = SyncBatch.FirstRecordKind(data, 0, data.Length) == (byte)SyncRecordKind.Global;
            if ((facts.EntityCount > 0 || facts.HasCost || isGlobal) && !_rate.TryTake(peer.Value, _sessions.Clock.Seconds))
            {
                RejectProposal(peer, seq, RejectReason.RateLimited, records);
                ServeRequests(peer, records);
                return;
            }
            if (facts.EntityCount > 0 || facts.HasCost)
            {
                RejectReason reason = Validate(peer, records, facts, data);
                if (reason != RejectReason.None)
                {
                    RejectProposal(peer, seq, reason, records);
                    ServeRequests(peer, records);
                    return;
                }
            }
            if (facts.EntityCount == 0) _proposalMaxRefund = 0; // set by Validate otherwise
            if (facts.EntityCount > 0 || facts.HasCost || isGlobal) Integrity.InvalidateResends(); // the world changes now
            List<BuildOrderRange> touchedEdges = TouchedEdgeBuildOrders(records);
            bool direct = (facts.Flags & ProposalFlags.DirectTool) != 0;
            // what the removed and upgraded edges (and a mod tool's removed objects) are worth, read before the apply
            long removedObjects = 0;
            NetCharge netCharge = facts.EntityCount > 0 ? CollectCostBasis(records, direct, out removedObjects) : null;
            _proposalPeer = peer;
            ApplyResult r;
            try
            {
                r = _codec.Apply(records, data, 0, data.Length, _mayCreate);
            }
            catch (Exception)
            {
                // the codec destroyed what it half-created; the client's prediction is rolled back to whatever the host
                // holds now (failures are counted against the peer by the caller)
                RollBackFailedProposal(peer, seq, records);
                throw;
            }
            RebaseBuildOrders(r.SpawnedEntities, touchedEdges);
            AfterApplyLinks(r.Touched);
            for (int i = 0; i < records.Count; i++)
            {
                SyncRecord rec = records[i];
                if (rec.Kind != SyncRecordKind.Spawn && rec.Kind != SyncRecordKind.Upsert) continue;
                Entity written = _ids.GetEntity(rec.NetId);
                if (written == Entity.Null) continue;
                _authoredThisFrame[written] = peer.Value;
                // the ids the client created with are used now (no-op for ids that are not its grants)
                _grants.TryConsume(peer.Value, rec.NetId);
            }
            string summary = peer + ": " + r;
            // the client's cost within the host's bounds (construction costs of what was created, refunds
            // of what was removed); a vanilla client's sum is never changed. A mod tool's proposal (Move It) pays for
            // the priced objects it adds beyond those it removed: moving stays free, cloning does not
            long minCharge = MinimumCharge(r.SpawnedEntities);
            if (direct) minCharge = Math.Max(0, minCharge - removedObjects);
            long clientCost = r.HasCost ? r.Cost : 0;
            long cost = ProposalRules.EffectiveCost(clientCost, minCharge, _proposalMaxRefund);
            if (cost != clientCost)
            {
                Perf.Counters.Count("proposal costs corrected by the host");
                _log.Info(LogCategory.Replication, peer + ": proposal cost " + clientCost + " corrected to " + cost + " (created at least " + minCharge + ", refund at most " + _proposalMaxRefund + ")");
            }
            if (cost != 0) ChargeCity(cost);
            // nets are priced by the composition the host's game gives them later this frame (SettleNetCharges)
            if (netCharge != null && AddCreatedEdges(netCharge, r.SpawnedEntities))
            {
                netCharge.Peer = peer;
                netCharge.ClientCost = clientCost;
                netCharge.ObjectFloor = minCharge;
                netCharge.MaxRefund = _proposalMaxRefund;
                netCharge.Charged = cost;
                _netCharges.Add(netCharge);
            }
            if (r.Problems.Count > 0) _log.Info(LogCategory.Replication, peer + " proposal problems: " + string.Join("; ", r.Problems));
            ApplyZoneRecords(records);
            ApplyGlobal(data, peer, true);
            ServeRequests(peer, records);
            LastApply = summary;
        }

        private void ServeRequests(PeerId peer, List<SyncRecord> records)
        {
            foreach (SyncRecord rec in records)
            {
                if (rec.Kind == SyncRecordKind.RequestIds && Features.Integrity.IsAvailable)
                {
                    try
                    {
                        Integrity.OnPeerRequest(peer, rec);
                    }
                    catch (Exception ex)
                    {
                        Guard.Report(Features.Integrity, "integrity request of " + peer, ex);
                    }
                }
                if (rec.Kind != SyncRecordKind.Request) continue;
                double now = _sessions.Clock.Seconds;
                if (rec.Request == SyncRequest.MoreIds)
                {
                    // request-only batches skip the proposal rate limit, so grants have their own (and a cap on
                    // the ids a peer holds unused); a refused request is dropped and the client asks again with backoff
                    if (!_requests.TryGrantIds(peer.Value, now, _grants.UnusedCount(peer.Value)))
                    {
                        _log.Debug(LogCategory.Replication, peer + ": id request over the limit; dropped");
                        CountViolation(peer, RejectReason.RateLimited);
                        continue;
                    }
                    var w = new SyncBatch.Writer();
                    Grant(peer, w);
                    if (!w.IsEmpty) Send(peer, w, Perf.StructureBytes);
                }
                else if (rec.Request == SyncRequest.ResendRoutes || rec.Request == SyncRequest.ResendAll)
                {
                    bool routesOnly = rec.Request == SyncRequest.ResendRoutes;
                    // a re-send scans the whole id table; over the limit it is deferred (FlushDeferred serves it)
                    if (_requests.TryResend(peer.Value, routesOnly, now)) Resend(peer, routesOnly);
                    else _log.Debug(LogCategory.Replication, peer + ": re-send request over the limit; deferred");
                }
            }
        }

        private void FollowClock(uint frame)
        {
            // a running follower clock owns frameIndex; the record is one of its samples
            if (FollowerClock.Instance != null && FollowerClock.Instance.OnHostClock(frame)) return;
            Action<SimulationSystem, uint> set = Probes.SetFrameIndex; // P-CLOCK
            if (set == null || !Features.Clock.IsAvailable) return;
            try
            {
                set(_simulation, frame);
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Clock, "following the host's frame", ex);
            }
        }

        /// <summary>
        /// Host, right before <c>SaveGameSystem</c>: binds the entities created this frame, so a join snapshot taken in
        /// this frame already carries their ids (otherwise the joiner would create them a second time).
        /// </summary>
        public void BindCreated()
        {
            if (_codec == null || !IsHost || !Features.Replication.IsAvailable) return;
            if (_created.IsEmptyIgnoreFilter) return;
            EntityManager em = _codec.EntityManager;
            NativeArray<ArchetypeChunk> chunks = _created.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                EntityTypeHandle entityType = em.GetEntityTypeHandle();
                foreach (ArchetypeChunk chunk in chunks)
                {
                    bool known = _codec.Families.TryClassifyArchetype(chunk.Archetype, out Family chunkFamily);
                    if (known && !FamilyClassifier.IsHostReplicated(chunkFamily)) continue; // lanes, blocks, movers... (agents are bound)
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        Entity e = entities[i];
                        if (_ids.GetNetId(e) != 0) continue;
                        Family family = known ? chunkFamily : _codec.Families.Classify(em, e);
                        if (FamilyClassifier.IsHostReplicated(family)) _ids.BindNew(e, family);
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }
    }
}
