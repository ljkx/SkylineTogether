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
            // feat/terrain: received stamps and heightmap/water tiles (independent of the entity stream)
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
            // client: local global-state edits are detected before host data can overwrite them
            if (!IsHost)
            {
                SafeCall("action detection", _actionDetectCall); // gameplay branch: dev tree, map tiles
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
                SafeCall("edit holds", _editHoldsCall); // mod compatibility
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
                    ApplyFailed(item.Peer, ex);
                    if (!Features.Replication.IsAvailable) return; // the session is ending (client: a rejoin repairs it)
                }
                finally
                {
                    if (item.Seq != 0) _sessions.Client?.ApplyAcks.Release(item.Seq);
                }
            }
            // the backlog left after this frame (zero ends the catch-up and logs the join's timings)
            joining?.ReportBacklog(_inbox.Count);
            RetryZones();
            // client: simulation values after the structure batches, so ids spawned this frame resolve
            if (!IsHost) _simValues.ApplyIncoming(_inbox.Count);
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
            if (!IsHost && Features.Agents.IsAvailable) _agents.ApplyIncoming(_inbox.Count);
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
                SafeCall("milestones", _milestonesCall); // gameplay branch
            }
            if (IsHost) UpdateQuarantine(now);
            // client: the rolling audit of references the game follows unchecked (diagnostics only)
            if (!IsHost && Features.Integrity.IsAvailable) _audit.Update(now);
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
                _sessions.Host?.Kick(peer, "The host could not apply your changes. Join again to get a fresh copy of the city.");
            }
        }

        private void SafeCall(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Actions, what, ex);
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
                            "Your game stopped applying the host's updates. Join again to get a fresh copy of the city."));
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
                host?.Kick(peer, reason);
            }
            _kicks.Clear();
        }

        private void ApplyFromHost(byte[] data)
        {
            List<SyncRecord> records = SyncBatch.Read(data, 0, data.Length); // decoded once for every consumer
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
            ApplyResult r = _codec.Apply(records, data, 0, data.Length, null, skip);
            // mod compatibility: tell the player once that objects of missing assets are left out (details in the log)
            if (!_missingNotified && _codec.MissingAssets.Count > 0)
            {
                _missingNotified = true;
                // name the packages and mods (and what to do) instead of only pointing at the diagnostics
                SessionManager.Notify(Core.Modding.MissingAssets.Summary(_codec.MissingBySource()) ?? "Some objects of the host's city use assets or mods you do not have; they are left out. See the diagnostics for the list.", seconds: 15f);
            }
            foreach (Entity e in r.Touched) _appliedThisFrame.Add(e);
            foreach ((uint start, uint count) in r.IdRanges) _pool.Add(start, count);
            if (r.Clock.HasValue) FollowClock(r.Clock.Value);
            if (r.Spawned + r.Upserted + r.Despawned > 0) LastApply = r.ToString();
            if (r.Problems.Count > 0) _log.Debug(LogCategory.Replication, "apply problems: " + string.Join("; ", r.Problems));
            ApplyZoneRecords(records);
            ApplyGlobal(data, PeerId.Host, false);
            if (Features.Integrity.IsAvailable)
            {
                try
                {
                    Integrity.OnHostBatch(records, _inbox.Count); // feat/integrity: presence masks, digests, icons, line rows
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Integrity, "integrity (host batch)", ex);
                }
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
            List<SyncRecord> records = SyncBatch.Read(data, 0, data.Length);
            // gameplay branch: validation (ARCHITECTURE.md#host-validation-and-commit); a refused proposal is rolled back on its originator by a resend
            ProposalFacts facts = Facts(records, out uint seq, out uint baseEpoch);
            bool isGlobal = SyncBatch.FirstRecordKind(data, 0, data.Length) == (byte)SyncRecordKind.Global;
            if ((facts.EntityCount > 0 || facts.HasCost || isGlobal) && !_rate.TryTake(peer.Value, _sessions.Clock.Seconds))
            {
                RejectProposal(peer, seq, RejectReason.RateLimited, records);
                ServeRequests(peer, records);
                return;
            }
            if (facts.EntityCount > 0 || facts.HasCost)
            {
                RejectReason reason = Validate(peer, records, facts, baseEpoch);
                if (reason != RejectReason.None)
                {
                    RejectProposal(peer, seq, reason, records);
                    ServeRequests(peer, records);
                    return;
                }
            }
            if (facts.EntityCount == 0) _proposalMaxRefund = 0; // set by Validate otherwise
            List<BuildOrderRange> touchedEdges = TouchedEdgeBuildOrders(records);
            _proposalPeer = peer;
            ApplyResult r = _codec.Apply(records, data, 0, data.Length, _mayCreate);
            RebaseBuildOrders(r.SpawnedEntities, touchedEdges);
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
            // of what was removed); a vanilla client's sum is never changed
            long minCharge = (facts.Flags & ProposalFlags.DirectTool) != 0 ? 0 : MinimumCharge(r.SpawnedEntities);
            long cost = ProposalRules.EffectiveCost(r.HasCost ? r.Cost : 0, minCharge, _proposalMaxRefund);
            if (cost != (r.HasCost ? r.Cost : 0))
            {
                Perf.Counters.Count("proposal costs corrected by the host");
                _log.Info(LogCategory.Replication, peer + ": proposal cost " + (r.HasCost ? r.Cost : 0) + " corrected to " + cost + " (created at least " + minCharge + ", refund at most " + _proposalMaxRefund + ")");
            }
            if (cost != 0) ChargeCity(cost);
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
                        Integrity.OnPeerRequest(peer, rec); // feat/integrity
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
            // movers branch: a running follower clock owns frameIndex; the record is one of its samples
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
