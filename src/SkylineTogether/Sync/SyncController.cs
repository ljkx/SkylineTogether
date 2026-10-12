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
    /// <summary>
    /// Host-authoritative structure sync (ARCH §4-§6, playable subset). The host captures every created, edited or
    /// deleted persistent entity at the end of each frame and streams it; clients apply at the start of the frame,
    /// before any tool or Modification system runs, and the game re-derives everything else. A client's own edits are
    /// predicted by the vanilla tools, captured the same way and proposed to the host, which applies them with the
    /// same applier and streams the authoritative result to everyone. Line state (paths, connections) is host data.
    /// Clients never simulate: their clock follows the host and the systems that would overwrite host data are off.
    /// <para>
    /// w2/ids: net ids are reused through a quarantine (<see cref="NetIdAllocator"/>): each host frame is an epoch
    /// (<see cref="EpochAckBook"/>), clients acknowledge what they applied (<see cref="MessageKind.EpochAck"/>, in order
    /// with their proposals), and a released id is reused only once every peer applied its despawn and no join is in
    /// progress. Structure is always sent; scanned values and re-sends wait per peer in a coalescing queue and are sent
    /// only while the peer's link has room (ARCH D4: congestion becomes staleness, not backlog); motion yields to both.
    /// </para>
    /// </summary>
    public sealed class SyncController
    {
        private const double ApplyBudgetMs = 6.0;
        private const int MaxBatchesPerFrame = 64;
        private const double ScanBudgetMs = 1.0;
        private const int MinScanPerFrame = 32;
        private const double DeferredBudgetMs = 1.5;
        private const double ClockInterval = 0.25;
        private const double ScanInterval = 0.25;
        private const double CityInterval = 1.0;
        private const double RouteWindowLinger = 2.0;
        private const double AckInterval = 0.1;
        private const int ResendChunkRecords = 512;

        private struct Inbound
        {
            public PeerId Peer;
            public byte[] Data;
            /// <summary>Client: the epoch sequence number held for this batch (0: none).</summary>
            public long Seq;
            /// <summary>Host: an <see cref="MessageKind.EpochAck"/> (Data holds its body).</summary>
            public bool IsAck;
        }

        /// <summary>Host: one peer's deferred (coalesced) updates.</summary>
        private sealed class PeerOut
        {
            public PeerId Peer;
            public readonly CoalescingQueue Queue = new CoalescingQueue();
            public long SentRecords;
            public double LastFlush;
        }

        private readonly SessionManager _sessions;
        private readonly ILog _log;
        private readonly Queue<Inbound> _inbox = new Queue<Inbound>();
        private readonly ClientIdPool _pool = new ClientIdPool();
        private readonly Dictionary<uint, ulong> _shadow = new Dictionary<uint, ulong>();
        private readonly HashSet<Entity> _appliedThisFrame = new HashSet<Entity>();
        private readonly HashSet<Entity> _toolOriginals = new HashSet<Entity>();
        private readonly List<PendingZone> _pendingZones = new List<PendingZone>();
        private readonly SyncBatch.Writer _batch = new SyncBatch.Writer();
        private readonly SyncBatch.Writer _peerBatch = new SyncBatch.Writer();
        private readonly SyncBatch.Writer _digestBatch = new SyncBatch.Writer();
        // global state (taxes, budgets, fees, loan, policies, names, demand): own batch, so a peer that cannot decode a
        // global record never loses entity data
        private readonly GlobalStateSync _global;
        private readonly SyncBatch.Writer _globalBatch = new SyncBatch.Writer();
        private readonly ClientGate _gate;
        // feat/terrain: terrain and water sync (self-contained; they follow the session events themselves)
        private readonly TerrainSync _terrain;
        private readonly WaterSync _water;
        // w2/ids: id quarantine, grants, deferred values
        private readonly EpochAckBook _acks = new EpochAckBook();
        private readonly IdGrantBook _grants = new IdGrantBook();
        private readonly Dictionary<byte, PeerOut> _out = new Dictionary<byte, PeerOut>();
        private readonly List<PeerOut> _outList = new List<PeerOut>();
        private readonly List<(uint Start, uint Count)> _grantRuns = new List<(uint, uint)>();
        private readonly List<uint> _scratchIds = new List<uint>();
        private readonly LinkBudgetConfig _linkConfig = new LinkBudgetConfig();
        private readonly FrameBudget _applyBudget = new FrameBudget(ApplyBudgetMs);
        private readonly ByteWriter _ackWriter = new ByteWriter(16);
        private readonly Func<byte, long> _epochSent;
        private readonly Func<uint, Family, bool> _mayCreate;
        private PeerId _proposalPeer;
        private uint _barrier;
        private long _lastAckSent = -1;
        private double _lastAckTime;
        private double _lastFrameTime;
        private EntityCodec _codec;
        private NetIdSystem _ids;
        private SimulationSystem _simulation;
        private ToolSystem _tools;
        private ZoneSystem _zones;
        private CitySystem _city;
        private EntityQuery _changed;
        private EntityQuery _tracked;
        private EntityQuery _created;
        private PropertyInfo _frameIndex;
        private double _lastClock, _lastScan, _lastCity;
        private int _scanCursor;
        private bool _scanCity;
        private readonly List<uint> _scanIds = new List<uint>();
        private bool _toolApplied;
        private long _toolCost;
        private double _routeWindowUntil;
        private bool _routeWindowOpen;
        private bool _clientLive;
        private bool _wasClient;
        private bool _disposed;

        private struct PendingZone
        {
            public ZoneBlockKey Key;
            public uint[] Zones;
            public double Until;
        }

        public SyncController(SessionManager sessions, ILog log)
        {
            _sessions = sessions;
            _log = log;
            _gate = new ClientGate(log);
            _global = new GlobalStateSync(log);
            // feat/terrain
            _terrain = new TerrainSync(sessions, log);
            _water = new WaterSync(sessions, log);
            _terrain.HeightsChanged += _water.MarkHot;
            Integrity = new IntegritySync(sessions, log); // feat/integrity
            _epochSent = peer => _sessions.Host?.GetEpochSent(new PeerId(peer)) ?? 0;
            _mayCreate = MayCreate;
            sessions.HostStarted += OnHostStarted;
            sessions.ClientStarted += OnClientStarted;
            sessions.SessionEnded += OnSessionEnded;
        }

        public bool IsHost => _sessions.Host != null;

        public bool IsLiveClient => _sessions.Client != null && _sessions.Client.State == ClientState.Live && _clientLive;

        public int BoundCount => _ids?.BoundCount ?? 0;

        public string LastApply { get; private set; } = "";

        /// <summary>feat/integrity: divergence detection and repair, host icons, line check (see <see cref="IntegritySync"/>).</summary>
        public IntegritySync Integrity { get; }

        /// <summary>One line for the panel: what replication is doing.</summary>
        public string Status
        {
            get
            {
                if (_codec == null || (!IsHost && !IsLiveClient)) return "";
                string s = _ids.BoundCount + " objects in sync";
                if (_inbox.Count > 0) s += ", " + _inbox.Count + " updates queued";
                if (_pendingZones.Count > 0) s += ", " + _pendingZones.Count + " zone blocks pending";
                if (IsHost)
                {
                    int deferred = 0;
                    foreach (PeerOut o in _outList) deferred = Math.Max(deferred, o.Queue.Count);
                    if (deferred > 0) s += ", " + deferred + " values waiting (slow link)";
                    if (_ids.Ids.QuarantinedCount > 0) s += ", " + _ids.Ids.QuarantinedCount + " ids in quarantine";
                }
                foreach (string env in new[] { _terrain.Status, _water.Status }) // feat/terrain
                {
                    if (env.Length > 0) s += ", " + env;
                }
                string integrity = Integrity.Status; // feat/integrity
                if (integrity.Length > 0) s += "; " + integrity;
                // review: the movers branch had no line in the panel or the diagnostics
                MoverSync movers = MoverSync.Instance;
                if (movers != null)
                {
                    if (IsHost && movers.Host.MoverCount > 0) s += "; " + movers.Host.MoverCount + " movers";
                    else if (!IsHost && movers.Client.Status.Length > 0) s += "; " + movers.Client.Status;
                }
                FollowerClock clock = FollowerClock.Instance;
                if (!IsHost && clock != null) s += clock.Active ? "; clock follows the host" : "; paused follower";
                string perf = Perf.Counters.CompactSummary(Perf.Now, Perf.ReceivedBytes); // details: diagnostics dump
                if (perf.Length > 0) s += "; perf: " + perf;
                return s;
            }
        }

        /// <summary>w2/ids: the id tables, the quarantine, the deferred queues and the perf counters (diagnostics dump).</summary>
        public void Describe(List<string> lines)
        {
            if (_ids != null) lines.Add("net ids: " + _ids.Describe());
            if (IsHost)
            {
                lines.Add("epochs: sealed " + _acks.CurrentEpoch + ", slowest peer " + _acks.MaxLag + " behind" + (_barrier != 0 ? ", join barrier at " + _barrier : ""));
                foreach (PeerOut o in _outList)
                {
                    lines.Add("peer " + o.Peer + ": acked epoch " + _acks.AckedEpoch(o.Peer.Value) + ", deferred " + o.Queue.Count + " (coalesced " + o.Queue.CoalescedCount
                              + " of " + o.Queue.MarkedCount + "), sent " + o.SentRecords + " deferred records");
                }
            }
            else if (_sessions.Client != null)
            {
                EpochAckTracker t = _sessions.Client.EpochAcks;
                lines.Add("epochs: received " + t.Received + ", applied through " + t.AppliedThrough + ", held " + t.HeldCount + ", last ack " + _lastAckSent);
            }
            Perf.Counters.Describe(Perf.Now, lines);
        }

        // ================================================================== lifecycle

        private bool EnsureWorld()
        {
            if (_disposed) return false;
            if (_codec != null) return true;
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return false;
            _ids = world.GetOrCreateSystemManaged<NetIdSystem>();
            _codec = new EntityCodec(world, _ids, _log);
            Integrity.Attach(_codec, _ids); // feat/integrity
            _simulation = world.GetOrCreateSystemManaged<SimulationSystem>();
            _tools = world.GetOrCreateSystemManaged<ToolSystem>();
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
            _frameIndex = typeof(SimulationSystem).GetProperty("frameIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _global.Attach(world, _codec, _ids);
            return true;
        }

        private void OnHostStarted(SessionHost host)
        {
            if (!EnsureWorld()) return;
            host.PeerJoining += OnPeerJoining;
            host.PeerLeft += OnPeerLeft;
            host.PeerLeft += Integrity.OnPeerLeft; // feat/integrity
            Integrity.Reset();
            ResetHostState();
            host.AppMessage += (peer, e) =>
            {
                if (e.Kind != MessageKind.Proposal && e.Kind != MessageKind.EpochAck) return;
                var copy = new byte[e.BodyCount];
                Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                // acknowledgements share the proposals' queue: an ack is counted only after every proposal the client
                // sent before it was applied (those may still name ids the client knew before the despawn)
                _inbox.Enqueue(new Inbound { Peer = peer, Data = copy, IsAck = e.Kind == MessageKind.EpochAck });
                Perf.Counters.AddBytes(Perf.ReceivedBytes, e.BodyCount, Perf.Now);
            };
            BindCity();
        }

        private void OnClientStarted(SessionClient client)
        {
            if (!EnsureWorld()) return;
            _clientLive = false;
            _wasClient = true;
            _pool.Clear();
            Integrity.Reset(); // feat/integrity
            _shadow.Clear();
            _lastAckSent = -1;
            _ids.Authority = false;
            client.AppMessage += e =>
            {
                if (e.Kind != MessageKind.Epoch) return;
                // review fix: mover batches belong to MoverClient; here they were decoded three more times for nothing
                // and counted as backlog (which suspends the integrity comparison)
                if (SyncBatch.IsMoverBatch(e.Body, e.BodyOffset, e.BodyCount)) return;
                var copy = new byte[e.BodyCount];
                Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                long seq = client.EpochAcks.Received;
                client.EpochAcks.Hold(seq); // w2/ids: acknowledged once applied
                _inbox.Enqueue(new Inbound { Peer = PeerId.Host, Data = copy, Seq = seq });
                Perf.Counters.AddBytes(Perf.ReceivedBytes, e.BodyCount, Perf.Now);
            };
            client.BecameLive += () =>
            {
                _clientLive = true;
                _gate.Close();
                Integrity.OnClientLive(); // feat/integrity
                _log.Info(LogCategory.Replication, "client live with " + _ids.BoundCount + " bound entities; following the host");
            };
        }

        private void OnSessionEnded()
        {
            if (_wasClient)
            {
                // a client's bindings describe the host's city of that session only
                _ids?.Clear();
                _wasClient = false;
            }
            if (_ids != null)
            {
                // nobody can address a quarantined id any more
                _ids.Authority = false;
                _ids.Ids.FlushQuarantine();
            }
            _gate.Open();
            Integrity.Reset(); // feat/integrity
            _inbox.Clear();
            _pool.Clear();
            _shadow.Clear();
            _pendingZones.Clear();
            _clientLive = false;
            _routeWindowOpen = false;
            _routeWindowUntil = 0;
            // review fix: a value scan interrupted by the session end resumed in the next session with the old id list
            _scanCursor = 0;
            _scanIds.Clear();
            _appliedThisFrame.Clear();
            _toolOriginals.Clear();
            _toolApplied = false;
            _toolCost = 0;
            _global.Reset();
            ResetHostState();
            _lastAckSent = -1;
        }

        private void ResetHostState()
        {
            _acks.Clear();
            _grants.Clear();
            _out.Clear();
            _outList.Clear();
            _barrier = 0;
            _applyBudget.Reset();
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
        }

        /// <summary>
        /// Host session start (ARCH K17): compacts the ids (w2/ids; no peer holds any yet), then binds every persistent
        /// entity and every prefab before anyone joins.
        /// </summary>
        private void BindCity()
        {
            EntityManager em = _codec.EntityManager;
            _ids.Authority = false;
            int renumbered = _ids.Compact(); // also rebuilds the allocator (nothing quarantined)
            _ids.Authority = true;
            int prefabs = _codec.BindAllPrefabs().Count;
            int bound = 0;
            NativeArray<Entity> entities = _tracked.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in entities)
                {
                    Family family = _codec.Families.Classify(em, e);
                    if (!FamilyClassifier.IsReplicated(family) && family != Family.Singleton) continue;
                    if (_ids.GetNetId(e) != 0) continue;
                    if (_ids.BindNew(e, family) != 0) bound++;
                }
            }
            finally
            {
                entities.Dispose();
            }
            _log.Info(LogCategory.Replication, "host session start: " + prefabs + " prefabs, " + _ids.BoundCount + " bindings (" + bound + " new"
                                               + (renumbered > 0 ? ", " + renumbered + " renumbered" : "") + "); " + _ids.Ids.Describe());
        }

        private void OnPeerJoining(PeerId peer)
        {
            // w2/ids: the peer's stream starts now; every epoch sealed so far counts as applied by it
            _acks.AddPeer(peer.Value);
            var o = new PeerOut { Peer = peer };
            _out[peer.Value] = o;
            _outList.Add(o);
            // first messages of the joiner's stream (buffered until its load finished): the prefab table and an id range
            var w = new SyncBatch.Writer();
            foreach ((uint id, Entity prefab) in _codec.BindAllPrefabs()) _codec.WritePrefab(w, id, prefab);
            Grant(peer, w);
            Send(peer, w, Perf.StructureBytes);
            _log.Info(LogCategory.Replication, peer + ": sent " + w.RecordCount + " prefab/range records");
            _global.ResendAll(); // the joiner gets every global channel with the next capture
        }

        private void OnPeerLeft(PeerId peer)
        {
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

        // ================================================================== frame start: apply

        /// <summary>Right after <c>EndFrameBarrier</c> playback, before tools and Modification (R11 §6.2).</summary>
        public void ApplyIncoming()
        {
            _appliedThisFrame.Clear();
            // feat/terrain: received stamps and heightmap/water tiles (independent of the entity stream)
            _terrain.ApplyIncoming();
            _water.ApplyIncoming();
            if (_codec == null) return;
            if (!IsHost && !IsLiveClient)
            {
                if (_sessions.Client == null && _inbox.Count > 0) _inbox.Clear();
                return;
            }
            long start = Perf.Start();
            double now = Perf.Now;
            // w2/ids: everything the previous frame sent is out: close its epoch
            if (IsHost) _ids.Ids.Seal(_acks.Seal(_epochSent), now);
            // client: local global-state edits are detected before host data can overwrite them
            if (!IsHost) _global.BeforeApply();
            double allowance = _applyBudget.BeginFrame();
            int n = 0;
            while (_inbox.Count > 0 && n < MaxBatchesPerFrame)
            {
                // budgeted with carry-over; a batch (a transaction) is never split, and at least one is applied per frame
                if (n > 0 && Perf.Elapsed(start) > allowance) break;
                n++;
                Inbound item = _inbox.Dequeue();
                try
                {
                    if (IsHost)
                    {
                        if (item.IsAck) _acks.OnAck(item.Peer.Value, EpochAckMessage.Read(item.Data, 0, item.Data.Length));
                        else ApplyProposal(item.Peer, item.Data);
                    }
                    else
                    {
                        ApplyFromHost(item.Data);
                    }
                }
                catch (DecodeException ex)
                {
                    _log.Warn(LogCategory.Replication, "dropped a malformed batch from " + item.Peer + ": " + ex.Message);
                }
                catch (Exception ex)
                {
                    _log.Error(LogCategory.Replication, "applying a batch from " + item.Peer + " failed", ex);
                }
                finally
                {
                    if (item.Seq != 0) _sessions.Client?.EpochAcks.Release(item.Seq);
                }
            }
            RetryZones();
            // client: what the appliers wrote becomes the baseline, so it is never proposed back
            if (!IsHost) _global.AfterApply();
            if (IsHost) UpdateQuarantine(now);
            _applyBudget.EndFrame(Perf.Stop(Perf.Apply, start));
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
                if (_barrier == 0) _barrier = Math.Max(1u, _acks.CurrentEpoch);
            }
            else
            {
                _barrier = 0;
            }
            _ids.Ids.Update(_acks.MinAckedEpoch, _barrier, now);
        }

        private void ApplyFromHost(byte[] data)
        {
            List<SyncRecord> records = SyncBatch.Read(data, 0, data.Length); // decoded once for every consumer
            ApplyResult r = _codec.Apply(records, data, 0, data.Length, null);
            foreach (Entity e in r.Touched) _appliedThisFrame.Add(e);
            foreach ((uint start, uint count) in r.IdRanges) _pool.Add(start, count);
            if (r.Clock.HasValue) FollowClock(r.Clock.Value);
            ApplyZoneRecords(records);
            _global.Apply(data, PeerId.Host, false);
            if (r.Spawned + r.Upserted + r.Despawned > 0) LastApply = r.ToString();
            if (r.Problems.Count > 0) _log.Debug(LogCategory.Replication, "apply problems: " + string.Join("; ", r.Problems));
            Integrity.OnHostBatch(records, _inbox.Count); // feat/integrity: presence masks, digests, icons, line rows
        }

        private void ApplyProposal(PeerId peer, byte[] data)
        {
            List<SyncRecord> records = SyncBatch.Read(data, 0, data.Length);
            _proposalPeer = peer;
            ApplyResult r = _codec.Apply(records, data, 0, data.Length, _mayCreate);
            for (int i = 0; i < records.Count; i++)
            {
                // the ids the client created with are used now (no-op for ids that are not its grants)
                SyncRecord rec = records[i];
                if ((rec.Kind == SyncRecordKind.Spawn || rec.Kind == SyncRecordKind.Upsert) && _ids.GetEntity(rec.NetId) != Entity.Null) _grants.TryConsume(peer.Value, rec.NetId);
            }
            ApplyZoneRecords(records);
            _global.Apply(data, peer, true);
            if (r.HasCost && r.Cost != 0) ChargeCity(r.Cost);
            string summary = peer + ": " + r;
            if (r.Problems.Count > 0) _log.Info(LogCategory.Replication, peer + " proposal problems: " + string.Join("; ", r.Problems));
            foreach (SyncRecord rec in records)
            {
                if (rec.Kind == SyncRecordKind.RequestIds) Integrity.OnPeerRequest(peer, rec); // feat/integrity
                if (rec.Kind != SyncRecordKind.Request) continue;
                if (rec.Request == SyncRequest.MoreIds)
                {
                    var w = new SyncBatch.Writer();
                    Grant(peer, w);
                    if (!w.IsEmpty) Send(peer, w, Perf.StructureBytes);
                }
                else if (rec.Request == SyncRequest.ResendRoutes || rec.Request == SyncRequest.ResendAll)
                {
                    Resend(peer, rec.Request == SyncRequest.ResendRoutes);
                }
            }
            LastApply = summary;
        }

        private void ChargeCity(long cost)
        {
            EntityManager em = _codec.EntityManager;
            Entity city = _city.City;
            if (city == Entity.Null || !em.HasComponent<Game.City.PlayerMoney>(city)) return;
            Game.City.PlayerMoney money = em.GetComponentData<Game.City.PlayerMoney>(city);
            int amount = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, cost));
            if (amount > 0) money.Subtract(amount);
            else money.Add(-amount);
            em.SetComponentData(city, money);
        }

        private void FollowClock(uint frame)
        {
            // movers branch: a running follower clock owns frameIndex; the record is one of its samples
            if (FollowerClock.Instance != null && FollowerClock.Instance.OnHostClock(frame)) return;
            try
            {
                if (_frameIndex != null && _frameIndex.CanWrite) _frameIndex.SetValue(_simulation, frame);
                else _frameIndex?.GetSetMethod(true)?.Invoke(_simulation, new object[] { frame });
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Replication, "clock follow failed: " + ex.Message);
                _frameIndex = null;
            }
        }

        /// <summary>
        /// Host, right before <c>SaveGameSystem</c>: binds the entities created this frame, so a join snapshot taken in
        /// this frame already carries their ids (otherwise the joiner would create them a second time).
        /// </summary>
        public void BindCreated()
        {
            if (_codec == null || !IsHost) return;
            if (_created.IsEmptyIgnoreFilter) return;
            EntityManager em = _codec.EntityManager;
            NativeArray<ArchetypeChunk> chunks = _created.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                EntityTypeHandle entityType = em.GetEntityTypeHandle();
                foreach (ArchetypeChunk chunk in chunks)
                {
                    bool known = _codec.Families.TryClassifyArchetype(chunk.Archetype, out Family chunkFamily);
                    if (known && !FamilyClassifier.IsReplicated(chunkFamily)) continue; // lanes, blocks, movers, agents...
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        Entity e = entities[i];
                        if (_ids.GetNetId(e) != 0) continue;
                        Family family = known ? chunkFamily : _codec.Families.Classify(em, e);
                        if (FamilyClassifier.IsReplicated(family)) _ids.BindNew(e, family);
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }

        // ================================================================== tool hook (client)

        /// <summary>Called at the start of the ApplyTool phase: the local tool is about to apply its preview.</summary>
        public void OnToolApply()
        {
            _terrain.OnToolApply(); // feat/terrain: brush stamps, on the host and on clients
            if (!IsLiveClient) return;
            EntityManager em = _codec.EntityManager;
            _toolApplied = true;
            using (EntityQuery temps = em.CreateEntityQuery(ComponentType.ReadOnly<Game.Tools.Temp>()))
            {
                NativeArray<Game.Tools.Temp> values = temps.ToComponentDataArray<Game.Tools.Temp>(Allocator.Temp);
                try
                {
                    foreach (Game.Tools.Temp t in values)
                    {
                        _toolCost += t.m_Cost;
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
            // feat/terrain: stamps out, readbacks in, settled heightmap tiles, audits, water samples
            _terrain.CaptureOutgoing();
            _water.CaptureOutgoing();
            double dt = _lastFrameTime > 0 ? Math.Min(0.25, Math.Max(0.0, now - _lastFrameTime)) : 1.0 / 60.0;
            _lastFrameTime = now;
            if (_codec != null)
            {
                long start = Perf.Start();
                if (IsHost) CaptureHost(now, dt);
                else if (IsLiveClient) CaptureClient(now);
                if (IsLiveClient) Integrity.ClientFrameEnd(now); // feat/integrity: integrity requests, icon digest
                Perf.Stop(Perf.Capture, start);
                _toolApplied = false;
                _toolCost = 0;
                _toolOriginals.Clear();
                long global = Perf.Start();
                CaptureGlobal(now);
                Perf.Stop(Perf.Global, global);
                if (IsLiveClient) SendEpochAck(now);
            }
            Perf.Counters.EndFrame(now);
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
                if (IsHost) _global.HostCapture(_globalBatch);
                else if (IsLiveClient) _global.ClientCapture(_globalBatch);
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
                _log.Error(LogCategory.Replication, "global state capture failed", ex);
            }
        }

        /// <summary>Client: tells the host how many of its epoch messages were applied (in order with the proposals).</summary>
        private void SendEpochAck(double now)
        {
            SessionClient client = _sessions.Client;
            if (client == null) return;
            long applied = client.EpochAcks.AppliedThrough;
            if (applied == _lastAckSent || now - _lastAckTime < AckInterval) return;
            _lastAckSent = applied;
            _lastAckTime = now;
            _ackWriter.Reset();
            EpochAckMessage.Write(_ackWriter, applied);
            client.SendToHost(MessageKind.EpochAck, _ackWriter.Buffer, 0, _ackWriter.Length);
        }

        private void CaptureHost(double now, double dt)
        {
            EntityManager em = _codec.EntityManager;
            _batch.Reset();
            // ids unbound without a despawn on the wire (their entity vanished without a Deleted tag): despawn them now,
            // then they may enter the quarantine
            _scratchIds.Clear();
            if (_ids.TakePendingDespawns(_scratchIds))
            {
                foreach (uint id in _scratchIds)
                {
                    _batch.Despawn(id);
                    _shadow.Remove(id);
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
                        // w2/ids perf pass: one classification per chunk; whole chunks of derived or agent families
                        // (lanes, movers, citizens, icons, ...) are skipped without touching their entities
                        bool known = _codec.Families.TryClassifyArchetype(chunk.Archetype, out Family chunkFamily);
                        if (known && chunkFamily != Family.ZoneBlock && !FamilyClassifier.IsReplicated(chunkFamily)) continue;
                        bool deleted = chunk.Has<Game.Common.Deleted>();
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
            long scan = Perf.Start();
            ScanValues(now);
            Perf.Stop(Perf.Scan, scan);
            if (now - _lastClock >= ClockInterval)
            {
                _lastClock = now;
                _batch.Clock(_simulation.frameIndex);
            }
            long integrity = Perf.Start();
            _digestBatch.Reset();
            Integrity.HostCapture(now, _batch, _digestBatch, _sessions.Host?.LivePeerCount ?? 0); // feat/integrity: icons and line rows; digests per peer
            Perf.Stop(Perf.Integrity, integrity);
            if (!_batch.IsEmpty) Broadcast(_batch, Perf.StructureBytes);
            long deferred = Perf.Start();
            FlushDeferred(now, dt);
            Perf.Stop(Perf.Deferred, deferred);
        }

        private void CaptureHostEntity(EntityManager em, Entity e, Family family, bool deleted, bool created, bool hasPrefab)
        {
            if (family == Family.ZoneBlock)
            {
                if (!deleted) WriteZoneBlock(e);
                return;
            }
            if (!FamilyClassifier.IsReplicated(family)) return;
            uint id = _ids.GetNetId(e);
            if (deleted)
            {
                if (id == 0) return;
                _batch.Despawn(id);
                _ids.Unbind(id, despawnSent: true);
                _shadow.Remove(id);
                DropDeferred(id, DeferredClass.Upsert);
                return;
            }
            SyncRecordKind kind = created ? SyncRecordKind.Spawn : SyncRecordKind.Upsert;
            if (id == 0)
            {
                id = _ids.BindNew(e, family);
                if (id == 0) return;
                kind = SyncRecordKind.Spawn;
            }
            if (hasPrefab) _codec.EnsurePrefab(em.GetComponentData<PrefabRef>(e).m_Prefab, _batch);
            _codec.WriteEntity(_batch, kind, id, family, e);
            DropDeferred(id, DeferredClass.Upsert); // the full record supersedes waiting values and re-sends
            if (FamilyClassifier.IsScanned(family)) _shadow[id] = _codec.HashEntity(e, true);
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
            long budget = (long)(ScanBudgetMs * Stopwatch.Frequency / 1000.0);
            int n = 0;
            while (_scanCursor < _scanIds.Count)
            {
                uint id = _scanIds[_scanCursor++];
                Family family = _ids.GetFamily(id);
                if (family == Family.Singleton ? !_scanCity : !FamilyClassifier.IsScanned(family)) continue;
                Entity e = _ids.GetEntity(id);
                if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                n++;
                ulong hash = _codec.HashEntity(e, true);
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
        /// Host: sends each live peer as many deferred records as its link takes this frame (ARCH §4.8.4, simplified),
        /// oldest first, serialised from the current state; then the integrity digests to the peers that are up to date
        /// (a digest is only comparable once everything it covers was sent).
        /// </summary>
        private void FlushDeferred(double now, double dt)
        {
            SessionHost host = _sessions.Host;
            if (host == null || _outList.Count == 0) return;
            EntityManager em = _codec.EntityManager;
            long start = Stopwatch.GetTimestamp();
            long cpuBudget = (long)(DeferredBudgetMs * Stopwatch.Frequency / 1000.0);
            for (int p = 0; p < _outList.Count; p++)
            {
                PeerOut o = _outList[p];
                if (!host.TryGetLink(o.Peer, out PeerSendState link) || !link.Live) continue;
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
            NativeArray<Entity> table = _ids.ReaderTable;
            double now = Perf.Now;
            int n = 0;
            for (int id = 1; id < table.Length; id++)
            {
                Entity e = table[id];
                if (e == Entity.Null) continue;
                Family f = _ids.GetFamily((uint)id);
                if (f == Family.Prefab) continue;
                if (routesOnly ? !(f == Family.Route || f == Family.Waypoint || f == Family.Segment) : !FamilyClassifier.IsReplicated(f)) continue;
                if (!em.Exists(e)) continue;
                o.Queue.Mark((uint)id, DeferredClass.Upsert, now);
                n++;
            }
            _log.Info(LogCategory.Replication, peer + ": re-sending " + n + (routesOnly ? " line entities" : " entities") + " (paced by its link)");
        }

        private void CaptureClient(double now)
        {
            EntityManager em = _codec.EntityManager;
            UpdateRouteWindow(now);
            // entities the applier wrote this frame: remember their converged state, never propose them back
            foreach (Entity e in _appliedThisFrame)
            {
                if (!em.Exists(e)) continue;
                uint id = _ids.GetNetId(e);
                if (id != 0) _shadow[id] = _codec.HashEntity(e, false);
            }
            // vanilla tools always go through ApplyTool (captured via _toolApplied); only tools from other mods may
            // write the world directly (Move It), so only those make un-applied changes count as the player's edits
            bool localTool = _tools.activeTool != null && !(_tools.activeTool is DefaultToolSystem)
                             && _tools.activeTool.GetType().Assembly != typeof(DefaultToolSystem).Assembly;
            if (!_toolApplied && !localTool) return;
            _batch.Reset();
            NativeArray<Entity> changed = _changed.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in changed)
                {
                    if (_appliedThisFrame.Contains(e)) continue;
                    Family family = _codec.Families.Classify(em, e);
                    if (family == Family.ZoneBlock)
                    {
                        if (_toolApplied && !em.HasComponent<Game.Common.Deleted>(e)) WriteZoneBlock(e);
                        continue;
                    }
                    if (!FamilyClassifier.IsReplicated(family)) continue;
                    uint id = _ids.GetNetId(e);
                    if (em.HasComponent<Game.Common.Deleted>(e))
                    {
                        if (id == 0) continue;
                        _batch.Despawn(id);
                        _ids.Unbind(id);
                        _shadow.Remove(id);
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
                    _shadow[id] = hash;
                    _codec.WriteEntity(_batch, kind, id, family, e);
                }
            }
            finally
            {
                changed.Dispose();
            }
            if (_toolCost != 0) _batch.Cost(_toolCost);
            if (_pool.ShouldRequestMore()) _batch.Request(SyncRequest.MoreIds);
            if (!_batch.IsEmpty)
            {
                ArraySegment<byte> bytes = _batch.Finish();
                _sessions.Client?.SendToHost(MessageKind.Proposal, bytes.Array, bytes.Offset, bytes.Count);
                Perf.Counters.AddBytes(Perf.ProposalBytes, bytes.Count, now);
            }
        }

        /// <summary>
        /// Route-tool preview window (ARCH §4.11.4, simplified): while the player draws or edits a line, the local
        /// route path and waypoint systems run so the preview follows the roads; afterwards the host's line state is
        /// requested again, so no locally derived line state survives.
        /// </summary>
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

        // ================================================================== zone cells (keyed state on derived blocks)

        private void WriteZoneBlock(Entity block)
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
                // review fix: only the host allocates ids. A client's EnsurePrefab allocated from its local counter
                // (the host's high-water mark at snapshot time), which the host has long handed to new entities and
                // movers since; the host then rebound that id to the zone prefab and lost the entity's binding
                zones[i] = IsHost ? _codec.EnsurePrefab(prefab, _batch) : _ids.GetNetId(prefab);
                any |= zones[i] != 0;
            }
            if (!any && em.HasComponent<Game.Common.Created>(block)) return; // a fresh empty block: nothing to tell
            _batch.ZoneCells(BlockKey(owner, em.GetComponentData<Game.Zones.Block>(block)), zones);
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
            for (int i = 0; i < records.Count; i++)
            {
                SyncRecord r = records[i];
                if (r.Kind != SyncRecordKind.ZoneCells) continue;
                if (!TryApplyZone(r.Block, r.ZoneIds)) _pendingZones.Add(new PendingZone { Key = r.Block, Zones = r.ZoneIds, Until = now + 3.0 });
            }
        }

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
            Entity owner = _ids.GetEntity(key.OwnerNetId);
            if (owner == Entity.Null || !em.Exists(owner) || !em.HasBuffer<Game.Zones.SubBlock>(owner)) return false;
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

        // ================================================================== sending

        private void Broadcast(SyncBatch.Writer w, string channel)
        {
            SessionHost host = _sessions.Host;
            if (host == null) return;
            ArraySegment<byte> bytes = w.Finish();
            host.Broadcast(MessageKind.Epoch, bytes.Array, bytes.Offset, bytes.Count);
            Perf.Counters.AddBytes(channel, (long)bytes.Count * Math.Max(1, _outList.Count), Perf.Now);
        }

        private void Send(PeerId peer, SyncBatch.Writer w, string channel)
        {
            SessionHost host = _sessions.Host;
            if (host == null) return;
            ArraySegment<byte> bytes = w.Finish();
            host.SendTo(peer, MessageKind.Epoch, bytes.Array, bytes.Offset, bytes.Count);
            Perf.Counters.AddBytes(channel, bytes.Count, Perf.Now);
        }
    }
}
