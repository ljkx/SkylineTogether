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
    // The tool hook and the frame-end capture: global state, the host's entity capture, value scans and the per-peer
    // deferred queues.
    public sealed partial class SyncController
    {
        // ================================================================== tool hook

        /// <summary>Called at the start of the ApplyTool phase: the local tool is about to apply its preview.</summary>
        public void OnToolApply()
        {
            if (!SessionActive) return;
            if (Features.Terrain.IsAvailable)
            {
                try
                {
                    _terrain.OnToolApply(); // feat/terrain: brush stamps, on the host and on clients
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Terrain, "terrain stamp capture", ex);
                }
            }
            if (Features.SimValues.IsAvailable)
            {
                try
                {
                    _cellMaps.OnToolApply(); // resource brushes (ore, oil, fertility, groundwater)
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.SimValues, "resource brush capture", ex);
                }
            }
            // gameplay branch: the host records its own tool's originals too (authored changes, conflict ledger)
            if (_codec == null || (!IsLiveClient && !IsHost) || !Features.Replication.IsAvailable) return;
            EntityManager em = _codec.EntityManager;
            _toolApplied = true;
            if (_temps == default) _temps = em.CreateEntityQuery(ComponentType.ReadOnly<Game.Tools.Temp>()); // cached
            {
                NativeArray<Game.Tools.Temp> values = _temps.ToComponentDataArray<Game.Tools.Temp>(Allocator.Temp);
                try
                {
                    foreach (Game.Tools.Temp t in values)
                    {
                        // exactly what ToolApplySystem charges (ToolApplySystem.cs:59-66): cancelled temps are free
                        if ((t.m_Flags & Game.Tools.TempFlags.Cancel) == 0) _toolCost += t.m_Cost;
                        if (t.m_Original != Entity.Null) _toolOriginals.Add(t.m_Original);
                    }
                }
                finally
                {
                    values.Dispose();
                }
            }
        }

        // ================================================================== frame end: capture

        /// <summary>Just before <c>PrepareCleanUpSystem</c>, after every Modification phase of the frame.</summary>
        public void CaptureOutgoing(double now)
        {
            if (_codec != null) _codec.DependencyBinder = null; // mod compatibility: never left over from a failed capture
            if (!SessionActive) return; // single player: nothing to do, nothing allocated
            // feat/terrain: stamps out, readbacks in, settled heightmap tiles, audits, water samples
            if (Features.Terrain.IsAvailable)
            {
                long terrain = Perf.Start();
                try
                {
                    _terrain.CaptureOutgoing();
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Terrain, "terrain capture", ex);
                }
                Perf.Budget.End(Perf.BudgetTerrain, Perf.Stop(Perf.Terrain, terrain));
            }
            if (Features.Water.IsAvailable)
            {
                long water = Perf.Start();
                try
                {
                    _water.CaptureOutgoing();
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Water, "water capture", ex);
                }
                Perf.Budget.End(Perf.BudgetTerrain, Perf.Stop(Perf.Water, water));
            }
            if (Features.SimValues.IsAvailable)
            {
                long maps = Perf.Start();
                try
                {
                    _cellMaps.CaptureOutgoing(); // host map tiles, client paint patches
                    _statistics.CaptureOutgoing();
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.SimValues, "map tiles and statistics capture", ex);
                }
                Perf.Budget.End(Perf.BudgetMaps, Perf.Stop(Perf.Maps, maps));
            }
            double dt = _lastFrameTime > 0 ? Math.Min(0.25, Math.Max(0.0, now - _lastFrameTime)) : 1.0 / 60.0;
            _lastFrameTime = now;
            if (_codec != null)
            {
                // gameplay branch: the bulldozer's sub-element mode deletes derived props and fences (no net ids)
                _subElementFrame = _toolApplied && _tools.activeTool is BulldozeToolSystem bulldozer && bulldozer.actualMode != BulldozeToolSystem.Mode.MainElements;
                long start = Perf.Start();
                try
                {
                    if (Features.Replication.IsAvailable)
                    {
                        if (IsHost)
                        {
                            // the host player's own direct UI writes (historical, vehicles, districts, colours) become captures
                            SafeCall("host action watch", _hostWatchCall);
                            CaptureHost(now, dt);
                            if (Features.SimValues.IsAvailable)
                            {
                                long simValues = Perf.Start();
                                try
                                {
                                    _simValues.CaptureOutgoing(now); // attention values and inspection samples
                                }
                                catch (Exception ex)
                                {
                                    Guard.Report(Features.SimValues, "attention values and inspection samples", ex);
                                }
                                Perf.Budget.End(Perf.BudgetSimValues, Perf.Stop(Perf.SimValues, simValues));
                            }
                            if (Features.FlowStatus.IsAvailable)
                            {
                                try
                                {
                                    _flow.CaptureOutgoing(now); // flow status to peers with the Electricity/Water topic
                                }
                                catch (Exception ex)
                                {
                                    Guard.Report(Features.FlowStatus, "flow status capture", ex);
                                }
                            }
                        }
                        else if (IsLiveClient)
                        {
                            long client = Perf.Start();
                            CaptureClient(now);
                            SafeCall("action capture", _clientActionCall); // gameplay branch
                            Perf.Stop(Perf.ClientCapture, client);
                            if (Features.Agents.IsAvailable) _agents.ClientFrameEnd(now); // refresh requests
                        }
                    }
                    if (Features.Integrity.IsAvailable)
                    {
                        try
                        {
                            if (IsLiveClient) Integrity.ClientFrameEnd(now); // feat/integrity: integrity requests, icon digest
                        }
                        catch (Exception ex)
                        {
                            Guard.Report(Features.Integrity, "integrity (client frame end)", ex);
                        }
                    }
                }
                finally
                {
                    Perf.Stop(Perf.Capture, start);
                    _toolApplied = false;
                    _toolCost = 0;
                    _toolOriginals.Clear();
                }
                long global = Perf.Start();
                if (Features.GlobalState.IsAvailable) CaptureGlobal(now);
                Perf.Stop(Perf.Global, global);
                // the prefab table for the next joiner, a slice per frame
                if (IsHost && !_prefabTable.Complete)
                {
                    long table = Perf.Start();
                    try
                    {
                        _prefabTable.Advance(_codec, Perf.Allowance(Perf.BudgetPrefabTable, 1.0));
                    }
                    catch (Exception ex)
                    {
                        _log.Warn(LogCategory.Replication, "encoding the prefab table failed; joins encode it themselves", ex);
                        _prefabTable.Clear();
                    }
                    Perf.Spent(Perf.BudgetPrefabTable, table);
                }
                _authoredThisFrame.Clear();
                _forceCapture.Clear();
                if (IsLiveClient && Features.Replication.IsAvailable) SendApplyAck(now);
            }
            Perf.Counters.EndFrame(now);
            Perf.Budget.EndFrame(); // the shared budget's frame is apply (frame start) + capture (frame end)
        }

        /// <summary>
        /// Global state (<see cref="GlobalStateSync"/>), in a batch of its own after the entity batch (so names of new
        /// entities resolve): the host's changed channels to everyone, a client's UI edits to the host.
        /// </summary>
        private void CaptureGlobal(double now)
        {
            try
            {
                _globalBatch.Reset();
                if (IsHost)
                {
                    // gameplay branch: executors that need EndFrameBarrier run here (after AllowBarrier<EndFrameBarrier>)
                    SafeCall("late intents", _lateIntentsCall);
                    _global.HostCapture(_globalBatch);
                    _actions.HostCapture(_globalBatch, now);
                }
                else if (IsLiveClient)
                {
                    _global.ClientCapture(_globalBatch);
                    _actions.ClientFlush(_globalBatch);
                }
                if (_globalBatch.IsEmpty) return;
                if (IsHost)
                {
                    Broadcast(_globalBatch, Perf.GlobalBytes);
                    return;
                }
                ArraySegment<byte> bytes = _globalBatch.Finish();
                _sessions.Client?.SendToHost(MessageKind.Proposal, bytes.Array, bytes.Offset, bytes.Count);
                Perf.Counters.AddBytes(Perf.ProposalBytes, bytes.Count, now);
            }
            catch (Exception ex)
            {
                Guard.Report(Features.GlobalState, "global state capture", ex);
            }
        }

        /// <summary>Client: tells the host how many of its epoch messages were applied (in order with the proposals).</summary>
        private void SendApplyAck(double now)
        {
            SessionClient client = _sessions.Client;
            if (client == null) return;
            long applied = client.ApplyAcks.AppliedThrough;
            if (applied == _lastAckSent || now - _lastAckTime < AckInterval) return;
            _lastAckSent = applied;
            _lastAckTime = now;
            _ackWriter.Reset();
            ApplyAckMessage.Write(_ackWriter, applied);
            client.SendToHost(MessageKind.ApplyAck, _ackWriter.Buffer, 0, _ackWriter.Length);
        }

        private void CaptureHost(double now, double dt)
        {
            EntityManager em = _codec.EntityManager;
            _batch.Reset();
            _writtenThisFrame.Clear();
            // mod compatibility: mod data entities that replicated values reference are bound and sent with them
            _codec.DependencyBinder = HostBindDependency;
            // ids unbound without a despawn on the wire (their entity vanished without a Deleted tag): despawn them now,
            // then they may enter the quarantine
            _scratchIds.Clear();
            if (_ids.TakePendingDespawns(_scratchIds))
            {
                foreach (uint id in _scratchIds)
                {
                    _batch.Despawn(id);
                    _shadow.Remove(id);
                    _events.Forget(id);
                    _ledger.Forget(id);
                    DropDeferred(id, DeferredClass.Upsert);
                    _ids.Ids.Release(id);
                }
            }
            if (!_changed.IsEmptyIgnoreFilter)
            {
                NativeArray<ArchetypeChunk> chunks = _changed.ToArchetypeChunkArray(Allocator.Temp);
                try
                {
                    EntityTypeHandle entityType = em.GetEntityTypeHandle();
                    foreach (ArchetypeChunk chunk in chunks)
                    {
                        bool deleted = chunk.Has<Game.Common.Deleted>();
                        // one classification per chunk; whole chunks of derived or agent families
                        // (lanes, movers, citizens, icons, ...) are skipped without touching their entities
                        bool known = _codec.Families.TryClassifyArchetype(chunk.Archetype, out Family chunkFamily);
                        bool subElements = _subElementFrame && deleted && (chunkFamily == Family.Lane || chunkFamily == Family.PrefabSubObject);
                        if (known && chunkFamily != Family.ZoneBlock && !subElements && !FamilyClassifier.IsHostReplicated(chunkFamily)) continue;
                        bool created = chunk.Has<Game.Common.Created>();
                        bool hasPrefab = chunk.Has<PrefabRef>();
                        NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                        for (int i = 0; i < entities.Length; i++)
                        {
                            Entity e = entities[i];
                            Family family = known ? chunkFamily : _codec.Families.Classify(em, e);
                            CaptureHostEntity(em, e, family, deleted, created, hasPrefab);
                        }
                    }
                }
                finally
                {
                    chunks.Dispose();
                }
            }
            // components the simulation added or removed without a tag (fires, damage, floods, accident sites)
            if (Features.Events.IsAvailable)
            {
                long watch = Perf.Start();
                try
                {
                    _events.HostCollect(now, _forceCapture, _scratchValues);
                    foreach (uint id in _scratchValues) MarkDeferred(id, DeferredClass.Values, now); // tree growth stages
                    _scratchValues.Clear();
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Events, "presence watch", ex);
                }
                Perf.Budget.End(Perf.BudgetPresenceWatch, Perf.Stop(Perf.PresenceWatch, watch));
            }
            // gameplay branch: entities that intents or the host's own UI changed without any tag
            foreach (Entity e in _forceCapture)
            {
                if (_writtenThisFrame.Contains(e) || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                Family family = _codec.Families.Classify(em, e);
                if (FamilyClassifier.IsReplicated(family)) WriteHostEntity(em, e, family, _ids.GetNetId(e), em.HasComponent<Game.Common.Created>(e), em.HasComponent<PrefabRef>(e));
            }
            // the agent scan slice, key-link changes and the co-captured owners of this frame's agent changes
            _agents.HostCapture(_batch, _writtenThisFrame, _writeAgent ?? (_writeAgent = WriteAgentRecord));
            // mod compatibility: the dependencies all of the above needed, untagged mod state, and its dependencies
            WriteDependencies(_writtenThisFrame);
            _callNow = now;
            SafeCall("mod state capture", _hostModStateCall);
            WriteDependencies(_writtenThisFrame);
            _codec.DependencyBinder = null;
            SafeCall("mod settings capture", _hostModSettingsCall);
            long scan = Perf.Start();
            ScanValues(now);
            Perf.Budget.End(Perf.BudgetScan, Perf.Stop(Perf.Scan, scan));
            if (now - _lastClock >= ClockInterval)
            {
                _lastClock = now;
                _batch.Clock(_simulation.frameIndex);
                // the id frontier moved: clients accept ids up to it (plus slack), nothing far beyond
                if (_ids.Ids.Next != _announcedFrontier)
                {
                    _announcedFrontier = _ids.Ids.Next;
                    _batch.IdCeiling(_announcedFrontier);
                }
            }
            long integrity = Perf.Start();
            _digestBatch.Reset();
            if (Features.Integrity.IsAvailable)
            {
                try
                {
                    Integrity.HostCapture(now, _batch, _digestBatch, _sessions.Host?.LivePeerCount ?? 0); // feat/integrity: icons and line rows; digests per peer
                }
                catch (Exception ex)
                {
                    Guard.Report(Features.Integrity, "integrity (host capture)", ex);
                }
            }
            Perf.Budget.End(Perf.BudgetIntegrity, Perf.Stop(Perf.Integrity, integrity));
            if (!_batch.IsEmpty)
            {
                // gameplay branch: the ledger epoch goes last, so the first-record routing of batches is unchanged
                _batch.Epoch(_ledger.Advance());
                Broadcast(_batch, Perf.StructureBytes);
            }
            long deferred = Perf.Start();
            FlushDeferred(now, dt);
            Perf.Budget.End(Perf.BudgetDeferred, Perf.Stop(Perf.Deferred, deferred));
            _agents.HostStream(now); // owner buffers, then each peer's agents by attention (after the batch)
        }

        private AgentSync.RecordWriter _writeAgent;

        private void CaptureHostEntity(EntityManager em, Entity e, Family family, bool deleted, bool created, bool hasPrefab)
        {
            if (family == Family.ZoneBlock)
            {
                if (!deleted) WriteZoneBlock(_batch, e, false);
                return;
            }
            if (_subElementFrame && deleted && (family == Family.PrefabSubObject || family == Family.Lane))
            {
                _actions.NoteSubElementDeleted(e); // gameplay branch: replayed on clients by key
                return;
            }
            if (family == Family.Agent)
            {
                if (FamilyClassifier.AgentsReplicated) CaptureHostAgent(em, e, deleted, created, hasPrefab);
                return;
            }
            if (!FamilyClassifier.IsReplicated(family)) return;
            uint id = _ids.GetNetId(e);
            if (deleted)
            {
                if (id == 0) return;
                if (family == Family.Object) _agents.OnOwnerDespawned(em, e); // its renters, workers, students
                _batch.Despawn(id);
                _ids.Unbind(id, despawnSent: true);
                _shadow.Remove(id);
                _events.Forget(id);
                _ledger.Forget(id);
                DropDeferred(id, DeferredClass.Upsert);
                return;
            }
            WriteHostEntity(em, e, family, id, created, hasPrefab);
        }

        /// <summary>
        /// Births, arrivals and new companies are spawned (their owners' reverse buffers follow in the same batch,
        /// <see cref="AgentSync"/>), deaths and departures despawned; any other tag only moves the agent to the front of the
        /// value scan (agents change all the time, their values are budgeted per peer).
        /// </summary>
        private void CaptureHostAgent(EntityManager em, Entity e, bool deleted, bool created, bool hasPrefab)
        {
            uint id = _ids.GetNetId(e);
            if (deleted)
            {
                if (id == 0) return;
                _agents.OnDespawned(id, e);
                _batch.Despawn(id);
                _ids.Unbind(id, despawnSent: true);
                _ledger.Forget(id);
                DropDeferred(id, DeferredClass.Upsert);
                return;
            }
            if (created || id == 0)
            {
                if (_writtenThisFrame.Contains(e)) return;
                WriteHostEntity(em, e, Family.Agent, id, true, hasPrefab);
                uint bound = _ids.GetNetId(e);
                if (bound != 0) _agents.OnSpawned(bound, e);
                return;
            }
            _agents.Touch(id);
        }

        /// <summary>An agent's full record into the frame's broadcast batch (key-link changes, co-captured owners).</summary>
        private void WriteAgentRecord(Entity e, uint id, SyncRecordKind kind)
        {
            EntityManager em = _codec.EntityManager;
            if (em.HasComponent<PrefabRef>(e)) _codec.EnsurePrefab(em.GetComponentData<PrefabRef>(e).m_Prefab, _batch);
            _codec.WriteEntity(_batch, kind, id, Family.Agent, e);
            _writtenThisFrame.Add(e);
            DropDeferred(id, DeferredClass.Upsert);
        }

        private void WriteHostEntity(EntityManager em, Entity e, Family family, uint id, bool created, bool hasPrefab)
        {
            SyncRecordKind kind = created ? SyncRecordKind.Spawn : SyncRecordKind.Upsert;
            if (id == 0)
            {
                id = _ids.BindNew(e, family);
                if (id == 0) return;
                kind = SyncRecordKind.Spawn;
            }
            if (hasPrefab) _codec.EnsurePrefab(em.GetComponentData<PrefabRef>(e).m_Prefab, _batch);
            _codec.WriteEntity(_batch, kind, id, family, e);
            _writtenThisFrame.Add(e);
            DropDeferred(id, DeferredClass.Upsert); // the full record supersedes waiting values and re-sends
            if (FamilyClassifier.IsScanned(family)) _shadow[id] = _codec.HashEntity(e, true, family);
            _events.NoteWritten(em, id, e, family); // the component set clients now hold
            // gameplay branch: authored changes (committed proposals and intents, the host player's tool) for conflicts
            if (_authoredThisFrame.TryGetValue(e, out byte author)) _ledger.MarkAuthored(id, author);
            else if (em.HasComponent<Game.Common.Applied>(e) || _toolOriginals.Contains(e)) _ledger.MarkAuthored(id, PeerId.Host.Value);
        }

        /// <summary>
        /// Values that change without update tags (line paths and states, city money): a rolling hash comparison over the
        /// scanned families (their own index set, not the whole id table), budgeted in milliseconds per frame. A change
        /// marks the entity in every peer's deferred queue; the current value is serialised when it is sent.
        /// </summary>
        private void ScanValues(double now)
        {
            if (now - _lastScan < ScanInterval && _scanCursor == 0) return;
            EntityManager em = _codec.EntityManager;
            if (_scanCursor == 0)
            {
                _lastScan = now;
                _scanIds.Clear();
                _ids.ScannedIds.CopyTo(_scanIds);
                _scanCity = now - _lastCity >= CityInterval;
                if (_scanCity) _lastCity = now;
            }
            long start = Stopwatch.GetTimestamp();
            long budget = (long)(Perf.Allowance(Perf.BudgetScan, ScanBudgetMs) * Stopwatch.Frequency / 1000.0); // shared budget
            int n = 0;
            while (_scanCursor < _scanIds.Count)
            {
                uint id = _scanIds[_scanCursor++];
                Family family = _ids.GetFamily(id);
                if (family == Family.Singleton ? !_scanCity : !FamilyClassifier.IsScanned(family)) continue;
                Entity e = _ids.GetEntity(id);
                if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                n++;
                ulong hash = _codec.HashEntity(e, true, family);
                bool known = _shadow.TryGetValue(id, out ulong old);
                if (!known || old != hash)
                {
                    _shadow[id] = hash;
                    if (known || family == Family.Singleton) MarkDeferred(id, DeferredClass.Values, now); // first sight: baseline only
                }
                if (n >= MinScanPerFrame && (n & 7) == 0 && Stopwatch.GetTimestamp() - start > budget) break;
            }
            if (_scanCursor >= _scanIds.Count) _scanCursor = 0;
        }

        /// <summary>Marks an id in every peer's deferred queue (joiners included: they get it once live).</summary>
        private void MarkDeferred(uint id, DeferredClass cls, double now)
        {
            for (int i = 0; i < _outList.Count; i++) _outList[i].Queue.Mark(id, cls, now);
        }

        /// <summary>Drops waiting entries of an id (it was despawned, or a full record just went to everyone).</summary>
        private void DropDeferred(uint id, DeferredClass atMost)
        {
            for (int i = 0; i < _outList.Count; i++) _outList[i].Queue.RemoveIfAtMost(id, atMost);
        }

        /// <summary>
        /// Host: sends each live peer as many deferred records as its link takes this frame (ARCHITECTURE.md#deferred-values-and-link-pacing),
        /// oldest first, serialised from the current state; then the integrity digests to the peers that are up to date
        /// (a digest is only comparable once everything it covers was sent).
        /// </summary>
        private void FlushDeferred(double now, double dt)
        {
            SessionHost host = _sessions.Host;
            if (host == null || _outList.Count == 0) return;
            EntityManager em = _codec.EntityManager;
            long start = Stopwatch.GetTimestamp();
            long cpuBudget = (long)(Perf.Allowance(Perf.BudgetDeferred, DeferredBudgetMs) * Stopwatch.Frequency / 1000.0); // shared budget
            for (int p = 0; p < _outList.Count; p++)
            {
                PeerOut o = _outList[p];
                if (!host.TryGetLink(o.Peer, out PeerSendState link) || !link.Live) continue;
                // a re-send the request limit deferred is served once its bucket refilled
                if (_requests.HasDeferred(o.Peer.Value) && _requests.TakeDeferred(o.Peer.Value, _sessions.Clock.Seconds, out bool routesOnly)) Resend(o.Peer, routesOnly);
                if (o.ResendCursor > 0) AdvanceResend(o, now);
                if (o.Queue.Count > 0 && Stopwatch.GetTimestamp() - start <= cpuBudget)
                {
                    int bytes = LinkBudget.DeferredBytes(link.QueuedReliableBytes, link.PendingReliableBytes, link.SendRateBytesPerSecond, dt, _linkConfig);
                    if (bytes > 0) FlushPeer(em, o, bytes, start, cpuBudget);
                }
                if (!_digestBatch.IsEmpty && o.Queue.Count == 0) Send(o.Peer, _digestBatch, Perf.IntegrityBytes);
                else if (!_digestBatch.IsEmpty) Perf.Counters.Count("digests withheld (peer behind)");
            }
        }

        private void FlushPeer(EntityManager em, PeerOut o, int byteBudget, long start, long cpuBudget)
        {
            _peerBatch.Reset();
            int baseLength = _peerBatch.Length;
            while (_peerBatch.Length - baseLength < byteBudget && _peerBatch.RecordCount < ResendChunkRecords)
            {
                if ((_peerBatch.RecordCount & 7) == 7 && Stopwatch.GetTimestamp() - start > cpuBudget) break;
                if (!o.Queue.TryTake(out uint id, out DeferredClass cls, out _)) break;
                Entity e = _ids.GetEntity(id);
                if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                Family family = _ids.GetFamily(id);
                try
                {
                    if (cls == DeferredClass.Upsert && em.HasComponent<PrefabRef>(e)) _codec.EnsurePrefab(em.GetComponentData<PrefabRef>(e).m_Prefab, _peerBatch);
                    _codec.WriteEntity(_peerBatch, cls == DeferredClass.Upsert ? SyncRecordKind.Upsert : SyncRecordKind.Values, id, family, e);
                }
                catch (Exception ex)
                {
                    _log.Debug(LogCategory.Replication, "deferred record #" + id + " failed: " + ex.Message);
                }
            }
            if (_peerBatch.IsEmpty) return;
            o.SentRecords += _peerBatch.RecordCount;
            o.LastFlush = Perf.Now;
            Send(o.Peer, _peerBatch, Perf.DeferredBytes);
        }

        /// <summary>A client asked for the full state of every line or every entity: queued as deferred upserts (coalesced, link-paced).</summary>
        private void Resend(PeerId peer, bool routesOnly)
        {
            if (!_out.TryGetValue(peer.Value, out PeerOut o)) return;
            EntityManager em = _codec.EntityManager;
            double now = Perf.Now;
            int n = 0;
            if (routesOnly)
            {
                // host lag: lines are value-scanned families with their own index set; walking the whole id
                // table (every agent of the city) on each route-tool close cost milliseconds per peer
                IdIndexSet scanned = _ids.ScannedIds;
                for (int i = 0; i < scanned.Count; i++)
                {
                    uint id = scanned[i];
                    Family f = _ids.GetFamily(id);
                    if (!(f == Family.Route || f == Family.Waypoint || f == Family.Segment)) continue;
                    Entity e = _ids.GetEntity(id);
                    if (e == Entity.Null || !em.Exists(e)) continue;
                    o.Queue.Mark(id, DeferredClass.Upsert, now);
                    n++;
                }
                _log.Info(LogCategory.Replication, peer + ": re-sending " + n + " line entities (paced by its link)");
                return;
            }
            // the whole table is walked over several frames (AdvanceResend), not in this one
            if (o.ResendCursor == 0) _log.Info(LogCategory.Replication, peer + ": re-sending every entity (marked over the next frames, paced by its link)");
            o.ResendCursor = 1;
            o.ResendMarked = 0;
        }

        /// <summary>Marks the next slice of a full re-send for <paramref name="o"/>.</summary>
        private void AdvanceResend(PeerOut o, double now)
        {
            EntityManager em = _codec.EntityManager;
            NativeArray<Entity> table = _ids.ReaderTable;
            int end = Math.Min(table.Length, o.ResendCursor + ResendIdsPerFrame);
            for (int id = o.ResendCursor; id < end; id++)
            {
                Entity e = table[id];
                if (e == Entity.Null) continue;
                Family f = _ids.GetFamily((uint)id);
                if (f == Family.Prefab || !FamilyClassifier.IsHostReplicated(f) || !em.Exists(e)) continue;
                o.Queue.Mark((uint)id, DeferredClass.Upsert, now);
                o.ResendMarked++;
            }
            o.ResendCursor = end >= table.Length ? 0 : end;
            if (o.ResendCursor == 0) _log.Info(LogCategory.Replication, o.Peer + ": full re-send marked " + o.ResendMarked + " entities");
        }

        /// <summary>
        /// gameplay branch: a deletion the vanilla UI makes outside any tool (R10 D1-D3: <c>ActionsSection.OnDelete</c>
        /// for districts, service upgrades and lines, <c>UpgradesSection.OnDelete</c>, the transport overview's delete).
        /// Entities removed together with their owner are the owner's business.
        /// </summary>
        private static bool IsUiDeletion(EntityManager em, Entity e, Family family)
        {
            if (em.HasComponent<Game.Common.Owner>(e))
            {
                Entity owner = em.GetComponentData<Game.Common.Owner>(e).m_Owner;
                if (owner != Entity.Null && em.Exists(owner) && em.HasComponent<Game.Common.Deleted>(owner)) return false;
            }
            switch (family)
            {
                case Family.Route: return em.HasComponent<Game.Routes.TransportLine>(e) || em.HasComponent<Game.Routes.WorkRoute>(e);
                case Family.Area: return em.HasComponent<Game.Areas.District>(e);
                case Family.Object: return em.HasComponent<Game.Buildings.ServiceUpgrade>(e);
                default: return false;
            }
        }
    }
}
