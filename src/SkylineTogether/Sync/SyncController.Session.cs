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
    // Session lifecycle: world setup, host and client start, session end and resets, peers joining and leaving.
    public sealed partial class SyncController
    {
        // ================================================================== lifecycle

        private bool EnsureWorld()
        {
            if (_disposed) return false;
            if (_codec != null) return true;
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return false;
            Compat.ModCompat.Refresh(_log); // mod compatibility: before the classifier caches any archetype
            _ids = world.GetOrCreateSystemManaged<NetIdSystem>();
            _codec = new EntityCodec(world, _ids, _log);
            _watcher = new Compat.ModStateWatcher(_codec, _ids, _log);
            Integrity.Attach(_codec, _ids); // feat/integrity
            _simValues.Attach(_codec, _ids);
            _flow.Attach(world, _ids);
            _agents.Attach(_codec, _ids);
            _simulation = world.GetOrCreateSystemManaged<SimulationSystem>();
            _tools = world.GetOrCreateSystemManaged<ToolSystem>();
            _tools.EventToolChanged += OnToolChanged; // the route preview window opens with the tool
            _zones = world.GetOrCreateSystemManaged<ZoneSystem>();
            _city = world.GetOrCreateSystemManaged<CitySystem>();
            EntityManager em = world.EntityManager;
            _changed = em.CreateEntityQuery(new EntityQueryDesc
            {
                Any = new[] { ComponentType.ReadOnly<Game.Common.Created>(), ComponentType.ReadOnly<Game.Common.Updated>(), ComponentType.ReadOnly<Game.Common.Deleted>() },
                None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
            _tracked = em.CreateEntityQuery(new EntityQueryDesc
            {
                Any = new[] { ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.City.City>(), ComponentType.ReadOnly<Game.Simulation.WaterSourceData>() },
                None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<PrefabData>() },
            });
            _created = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Common.Created>() },
                None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>() },
            });
            _global.Attach(world, _codec, _ids);
            _events.Attach(world, _codec, _ids);
            _signals.Attach(world);
            _actions.Attach(world, _codec, _ids); // gameplay branch
            _generateEdges = world.GetOrCreateSystemManaged<Game.Tools.GenerateEdgesSystem>();
            return true;
        }

        private void OnHostStarted(SessionHost host)
        {
            Perf.Budget.Reset(); // w9/perf: per-session budget statistics
            if (!EnsureWorld()) return;
            host.PeerJoining += OnPeerJoining;
            host.PeerLeft += OnPeerLeft;
            host.PeerLeft += Integrity.OnPeerLeft; // feat/integrity
            Integrity.Reset();
            ResetGameplay();
            ResetHostState();
            host.AppMessage += (peer, e) =>
            {
                if (e.Kind != MessageKind.Proposal && e.Kind != MessageKind.ApplyAck) return;
                // bounded per peer (the kick happens in the apply loop, not inside the transport callback)
                if (!_hostInboxQuota.TryAdmit(peer, e.BodyCount))
                {
                    if (_hostInboxQuota.TakeOverflow(peer))
                    {
                        _kicks.Add((peer, peer + " sends changes faster than the host applies them (" + _hostInboxQuota.Pending(peer) + " waiting, "
                                          + (_hostInboxQuota.PendingBytes(peer) >> 10) + " KiB)",
                                    "Your game sent changes faster than the host could apply them. Join again to continue."));
                    }
                    return;
                }
                var copy = new byte[e.BodyCount];
                Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                // acknowledgements share the proposals' queue: an ack is counted only after every proposal the client
                // sent before it was applied (those may still name ids the client knew before the despawn)
                _inbox.Enqueue(new Inbound { Peer = peer, Data = copy, IsAck = e.Kind == MessageKind.ApplyAck });
                Perf.Counters.AddBytes(Perf.ReceivedBytes, e.BodyCount, Perf.Now);
            };
            // decided once per session, before the city is bound (agents are bound with everything else)
            FamilyClassifier.AgentsReplicated = Features.Agents.IsAvailable;
            if (!FamilyClassifier.AgentsReplicated) _log.Warn(LogCategory.Replication, "agents are not replicated in this session (feature unavailable); clients keep their snapshot's citizens");
            BindCity();
            // mod compatibility: untagged mod state is compared from here on; every mod setting goes out once
            _modSettings.Reset();
            SafeCall("mod state watcher", () => _watcher.Start(true));
        }

        private void OnClientStarted(SessionClient client)
        {
            Perf.Budget.Reset(); // w9/perf: per-session budget statistics
            if (!EnsureWorld()) return;
            _clientLive = false;
            _pool.Clear();
            Integrity.Reset(); // feat/integrity
            _shadow.Clear();
            ResetGameplay();
            SafeCall("mod join gates", () => Compat.ModCompat.OnClientStarted(_log)); // mod compatibility: before the load
            _wasClient = true;
            _lastAckSent = -1;
            _ids.Authority = false;
            FamilyClassifier.AgentsReplicated = false; // the host decides; a client applies what arrives
            client.AppMessage += e =>
            {
                if (e.Kind != MessageKind.Epoch) return;
                // mover batches belong to MoverClient; here they were decoded three more times for nothing
                // and counted as backlog (which suspends the integrity comparison)
                if (SyncBatch.IsMoverBatch(e.Body, e.BodyOffset, e.BodyCount)) return;
                var copy = new byte[e.BodyCount];
                Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                long seq = client.ApplyAcks.Received;
                client.ApplyAcks.Hold(seq); // acknowledged once applied
                _inbox.Enqueue(new Inbound { Peer = PeerId.Host, Data = copy, Seq = seq });
                Perf.Counters.AddBytes(Perf.ReceivedBytes, e.BodyCount, Perf.Now);
            };
            client.BecameLive += () =>
            {
                _clientLive = true;
                _gate.Close();
                // mod compatibility: mod systems that would derive host-owned state go off; untagged mod state is watched
                SafeCall("mod client gates", () => Compat.ModCompat.OnClientLive(_log));
                SafeCall("mod state watcher", () => _watcher?.Start(false));
                Integrity.OnClientLive(); // feat/integrity
                _log.Info(LogCategory.Replication, "client live with " + _ids.BoundCount + " bound entities; following the host");
            };
        }

        private void OnSessionEnded()
        {
            // mod compatibility: restore the player's own mod settings and every switched mod system
            if (_wasClient) SafeCall("restoring mod settings", _modSettings.RestoreClient);
            SafeCall("mod gates", () => Compat.ModCompat.OnSessionEnded(_log));
            _watcher?.Stop();
            _audit.Reset();
            _modSettings.Reset();
            _holds.Clear();
            _localDeletes.Clear();
            _dependencies.Clear();
            _missingNotified = false;
            _codec?.ResetSession(); // the missing-asset list is per session
            _wasClient = false; // (the id tables are cleared below for host and client alike)
            FamilyClassifier.AgentsReplicated = false;
            if (_ids != null)
            {
                // nobody can address a quarantined id any more
                _ids.Authority = false;
                _ids.Ids.FlushQuarantine();
            }
            // a client's bindings describe the host's city of that session only; a host's are rebuilt and compacted at
            // the next session start, so keeping them would only grow the tables (single-player safety)
            _ids?.Clear();
            _proposalHealth.Clear();
            _gate.Open();
            Integrity.Reset(); // feat/integrity
            _inbox.Clear();
            _pool.Clear();
            _shadow.Clear();
            _pendingZones.Clear();
            _clientLive = false;
            _routeWindowOpen = false;
            _routeWindowUntil = 0;
            // a value scan interrupted by the session end resumed in the next session with the old id list
            _scanCursor = 0;
            _scanIds.Clear();
            _appliedThisFrame.Clear();
            _toolOriginals.Clear();
            _toolApplied = false;
            _toolCost = 0;
            _global.Reset();
            ResetGameplay();
            ResetHostState();
            _lastAckSent = -1;
        }

        /// <summary>gameplay branch: forgets ledger, limits, sequence numbers and intent baselines (session start or end).</summary>
        private void ResetGameplay()
        {
            _actions.Reset();
            _events.Reset();
            _signals.Reset();
            _ledger.Clear();
            _rate.Clear();
            _requests.Clear();
            _violations.Clear();
            _authoredThisFrame.Clear();
            _forceCapture.Clear();
            _baseEpoch = 0;
            _seq = 0;
            _subElementFrame = false;
            _buildOrders = new BuildOrderAllocator();
        }

        /// <summary>Forgets release frames, grants and deferred queues (session start or end).</summary>
        private void ResetHostState()
        {
            _hostInboxQuota.Clear();
            _ackStall.Clear();
            _kicks.Clear();
            _acks.Clear();
            _grants.Clear();
            _out.Clear();
            _outList.Clear();
            _barrier = 0;
            _announcedFrontier = 0;
            _prefabTable.Clear();
            _applyBudget.Reset();
            _catchUpBudget.Reset();
        }

        /// <summary>Host: whether <paramref name="peer"/>'s role allows <paramref name="needed"/> (ARCH §5.6).</summary>
        private bool Allows(PeerId peer, Permission needed)
        {
            SessionHost host = _sessions.Host;
            if (host == null || peer == PeerId.Host) return true;
            return RolePermissions.Allows(host.GetRole(peer), needed);
        }

        /// <summary>Mod unload: frees the codec's native buffers and serialization contexts (they are persistent).</summary>
        public void Dispose()
        {
            _disposed = true;
            try
            {
                _codec?.Dispose();
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Replication, "disposing the codec: " + ex.Message);
            }
            _codec = null; // every entry point checks it (and EnsureWorld refuses after disposal)
            if (_tools != null) _tools.EventToolChanged -= OnToolChanged;
        }

        /// <summary>
        /// Host session start (ARCH K17): compacts the ids (no peer holds any yet), then binds every persistent
        /// entity and every prefab before anyone joins.
        /// </summary>
        private void BindCity()
        {
            EntityManager em = _codec.EntityManager;
            _ids.Authority = false;
            int renumbered = _ids.Compact(); // also rebuilds the allocator (nothing quarantined)
            _ids.Authority = true;
            List<(uint Id, Entity Prefab)> allPrefabs = _codec.BindAllPrefabs();
            _prefabTable.Start(allPrefabs); // w9/perf: encoded over the next frames, not in a joiner's frame
            int prefabs = allPrefabs.Count;
            int bound = 0;
            NativeArray<Entity> entities = _tracked.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in entities)
                {
                    Family family = _codec.Families.Classify(em, e);
                    if (!FamilyClassifier.IsHostReplicated(family) && family != Family.Singleton) continue; // agents too
                    uint known = _ids.GetNetId(e);
                    if (known == 0 && (known = _ids.BindNew(e, family)) != 0) bound++;
                    // the snapshot gives joiners this component set; the presence watch compares against it
                    if (known != 0) _events.NoteWritten(em, known, e, family);
                }
            }
            finally
            {
                entities.Dispose();
            }
            _log.Info(LogCategory.Replication, "host session start: " + prefabs + " prefabs, " + _ids.BoundCount + " bindings (" + _ids.CountOf(Family.Agent) + " agents, " + bound + " new"
                                               + (renumbered > 0 ? ", " + renumbered + " renumbered" : "") + "); " + _ids.Ids.Describe());
            PrimeBuildOrders(em);
        }

        /// <summary>gameplay branch: the build-order allocator starts above every live edge (ARCH §5.4.3).</summary>
        private void PrimeBuildOrders(EntityManager em)
        {
            try
            {
                using (EntityQuery q = em.CreateEntityQuery(ComponentType.ReadOnly<Game.Net.BuildOrder>()))
                {
                    NativeArray<Game.Net.BuildOrder> orders = q.ToComponentDataArray<Game.Net.BuildOrder>(Allocator.Temp);
                    try
                    {
                        foreach (Game.Net.BuildOrder b in orders) _buildOrders.Observe(b.m_Start, b.m_End);
                    }
                    finally
                    {
                        orders.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Replication, "reading the city's build orders failed", ex);
            }
        }

        private void OnPeerJoining(PeerId peer)
        {
            // the peer's stream starts now; every release frame sealed so far counts as applied by it
            _acks.AddPeer(peer.Value);
            var o = new PeerOut { Peer = peer };
            _out[peer.Value] = o;
            _outList.Add(o);
            // first messages of the joiner's stream (buffered until its load finished): the prefab table and an id range
            var w = new SyncBatch.Writer();
            long tableStart = Perf.Start();
            _prefabTable.WriteTo(_codec, _codec.BindAllPrefabs(), w); // w9/perf: cached, only new bindings are encoded now
            double tableMs = Perf.Elapsed(tableStart);
            w.IdCeiling(_ids.Ids.Next); // w9/perf
            Grant(peer, w);
            // gameplay branch: the joiner's snapshot contains every ledger epoch up to now, so its proposals start from here
            w.Epoch(_ledger.LastBroadcast);
            Send(peer, w, Perf.StructureBytes);
            _log.Info(LogCategory.Replication, peer + ": sent " + w.RecordCount + " prefab/range records (prefab table in " + tableMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms)");
            _global.ResendAll(); // the joiner gets every global channel with the next capture
            _modSettings.ResendAll(); // mod compatibility: and every synchronised mod setting
            _actions.ResendUnlocks();
        }

        private void OnPeerLeft(PeerId peer)
        {
            _proposalHealth.Remove(peer.Value);
            _hostInboxQuota.Forget(peer);
            _ackStall.Forget(peer.Value);
            // gameplay branch
            _rate.Forget(peer.Value);
            _requests.Forget(peer.Value);
            _violations.Forget(peer.Value);
            _acks.RemovePeer(peer.Value);
            if (_out.TryGetValue(peer.Value, out PeerOut o))
            {
                _out.Remove(peer.Value);
                _outList.Remove(o);
            }
            // ids the peer never used return through the quarantine (nobody else ever saw them)
            _scratchIds.Clear();
            _grants.Remove(peer.Value, _scratchIds);
            int returned = 0;
            if (_ids != null)
            {
                foreach (uint id in _scratchIds)
                {
                    if (_ids.GetEntity(id) == Entity.Null && _ids.Ids.IsAllocated(id) && _ids.Ids.Release(id)) returned++;
                }
            }
            if (returned > 0) _log.Debug(LogCategory.Replication, peer + " left; " + returned + " unused granted ids returned");
        }

        /// <summary>Host: grants a client its next id range (runs of free ids, then fresh ones) and writes the records.</summary>
        private void Grant(PeerId peer, SyncBatch.Writer w)
        {
            _grantRuns.Clear();
            _ids.Ids.Grant(NetIdAllocator.DefaultGrant, _grantRuns);
            foreach ((uint start, uint count) in _grantRuns)
            {
                _grants.Add(peer.Value, start, count);
                w.IdRange(start, count);
            }
        }

        /// <summary>
        /// Host, proposals: a client may create only with an id of its own grants that it never used (marked used once the
        /// entity is bound, <see cref="ApplyProposal"/>), so a stale or replayed spawn can never bind a released id.
        /// </summary>
        private bool MayCreate(uint id, Family family)
        {
            return FamilyClassifier.IsReplicated(family) && _grants.IsUnused(_proposalPeer.Value, id);
        }
    }
}
