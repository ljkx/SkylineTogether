using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using SkylineTogether.Core;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
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
    /// </summary>
    public sealed class SyncController
    {
        private const int MaxBatchesPerFrame = 64;
        private const int ScanBatch = 256;
        private const double ClockInterval = 0.25;
        private const double ScanInterval = 0.25;
        private const double CityInterval = 1.0;
        private const double RouteWindowLinger = 2.0;

        private readonly SessionManager _sessions;
        private readonly ILog _log;
        private readonly Queue<(PeerId Peer, byte[] Data)> _inbox = new Queue<(PeerId, byte[])>();
        private readonly Dictionary<byte, List<(uint Start, uint Count)>> _peerRanges = new Dictionary<byte, List<(uint, uint)>>();
        private readonly ClientIdPool _pool = new ClientIdPool();
        private readonly Dictionary<uint, ulong> _shadow = new Dictionary<uint, ulong>();
        private readonly HashSet<Entity> _appliedThisFrame = new HashSet<Entity>();
        private readonly HashSet<Entity> _toolOriginals = new HashSet<Entity>();
        private readonly List<PendingZone> _pendingZones = new List<PendingZone>();
        private readonly SyncBatch.Writer _batch = new SyncBatch.Writer();
        private readonly ClientGate _gate;
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
        private List<uint> _scanIds = new List<uint>();
        private bool _toolApplied;
        private long _toolCost;
        private double _routeWindowUntil;
        private bool _routeWindowOpen;
        private bool _clientLive;
        private bool _wasClient;

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
            sessions.HostStarted += OnHostStarted;
            sessions.ClientStarted += OnClientStarted;
            sessions.SessionEnded += OnSessionEnded;
        }

        public bool IsHost => _sessions.Host != null;

        public bool IsLiveClient => _sessions.Client != null && _sessions.Client.State == ClientState.Live && _clientLive;

        public int BoundCount => _ids?.BoundCount ?? 0;

        public string LastApply { get; private set; } = "";

        /// <summary>One line for the panel: what replication is doing.</summary>
        public string Status
        {
            get
            {
                if (_codec == null || (!IsHost && !IsLiveClient)) return "";
                string s = _ids.BoundCount + " objects in sync";
                if (_inbox.Count > 0) s += ", " + _inbox.Count + " updates queued";
                if (_pendingZones.Count > 0) s += ", " + _pendingZones.Count + " zone blocks pending";
                return s;
            }
        }

        // ================================================================== lifecycle

        private bool EnsureWorld()
        {
            if (_codec != null) return true;
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return false;
            _ids = world.GetOrCreateSystemManaged<NetIdSystem>();
            _codec = new EntityCodec(world, _ids, _log);
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
            return true;
        }

        private void OnHostStarted(SessionHost host)
        {
            if (!EnsureWorld()) return;
            host.PeerJoining += OnPeerJoining;
            host.PeerLeft += peer => _peerRanges.Remove(peer.Value);
            host.AppMessage += (peer, e) =>
            {
                if (e.Kind != MessageKind.Proposal) return;
                var copy = new byte[e.BodyCount];
                Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                _inbox.Enqueue((peer, copy));
            };
            BindCity();
        }

        private void OnClientStarted(SessionClient client)
        {
            if (!EnsureWorld()) return;
            _clientLive = false;
            _wasClient = true;
            _pool.Clear();
            _shadow.Clear();
            client.AppMessage += e =>
            {
                if (e.Kind != MessageKind.Epoch) return;
                var copy = new byte[e.BodyCount];
                Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                _inbox.Enqueue((PeerId.Host, copy));
            };
            client.BecameLive += () =>
            {
                _clientLive = true;
                _gate.Close();
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
            _gate.Open();
            _inbox.Clear();
            _peerRanges.Clear();
            _pool.Clear();
            _shadow.Clear();
            _pendingZones.Clear();
            _clientLive = false;
            _routeWindowOpen = false;
        }

        /// <summary>Host session start (ARCH K17): binds every persistent entity and every prefab before anyone joins.</summary>
        private void BindCity()
        {
            EntityManager em = _codec.EntityManager;
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
                    _ids.Bind(e, _ids.Ids.Allocate(), family);
                    bound++;
                }
            }
            finally
            {
                entities.Dispose();
            }
            _log.Info(LogCategory.Replication, "host session start: " + prefabs + " prefabs, " + _ids.BoundCount + " bindings (" + bound + " new)");
        }

        private void OnPeerJoining(PeerId peer)
        {
            // first messages of the joiner's stream (buffered until its load finished): the prefab table and an id range
            var w = new SyncBatch.Writer();
            foreach ((uint id, Entity prefab) in _codec.BindAllPrefabs()) _codec.WritePrefab(w, id, prefab);
            (uint start, uint count) = _ids.Ids.Grant();
            AddRange(peer, start, count);
            w.IdRange(start, count);
            Send(peer, w);
            _log.Info(LogCategory.Replication, peer + ": sent " + w.RecordCount + " prefab/range records");
        }

        private void AddRange(PeerId peer, uint start, uint count)
        {
            if (!_peerRanges.TryGetValue(peer.Value, out var list)) _peerRanges[peer.Value] = list = new List<(uint, uint)>();
            list.Add((start, count));
        }

        // ================================================================== frame start: apply

        /// <summary>Right after <c>EndFrameBarrier</c> playback, before tools and Modification (R11 §6.2).</summary>
        public void ApplyIncoming()
        {
            _appliedThisFrame.Clear();
            if (_codec == null) return;
            if (!IsHost && !IsLiveClient)
            {
                if (_sessions.Client == null && _inbox.Count > 0) _inbox.Clear();
                return;
            }
            int n = 0;
            while (_inbox.Count > 0 && n++ < MaxBatchesPerFrame)
            {
                (PeerId peer, byte[] data) = _inbox.Dequeue();
                try
                {
                    if (IsHost) ApplyProposal(peer, data);
                    else ApplyFromHost(data);
                }
                catch (DecodeException ex)
                {
                    _log.Warn(LogCategory.Replication, "dropped a malformed batch from " + peer + ": " + ex.Message);
                }
                catch (Exception ex)
                {
                    _log.Error(LogCategory.Replication, "applying a batch from " + peer + " failed", ex);
                }
            }
            RetryZones();
        }

        private void ApplyFromHost(byte[] data)
        {
            ApplyResult r = _codec.Apply(data, 0, data.Length, null);
            foreach (Entity e in r.Touched) _appliedThisFrame.Add(e);
            foreach ((uint start, uint count) in r.IdRanges) _pool.Add(start, count);
            if (r.Clock.HasValue) FollowClock(r.Clock.Value);
            ApplyZoneRecords(data);
            if (r.Spawned + r.Upserted + r.Despawned > 0) LastApply = r.ToString();
            if (r.Problems.Count > 0) _log.Debug(LogCategory.Replication, "apply problems: " + string.Join("; ", r.Problems));
        }

        private void ApplyProposal(PeerId peer, byte[] data)
        {
            _peerRanges.TryGetValue(peer.Value, out var ranges);
            ApplyResult r = _codec.Apply(data, 0, data.Length, (id, family) => ranges != null && ClientIdPool.InRanges(ranges, id) && FamilyClassifier.IsReplicated(family));
            ApplyZoneRecords(data);
            if (r.HasCost && r.Cost != 0) ChargeCity(r.Cost);
            foreach (SyncRecord rec in SyncBatch.Read(data, 0, data.Length))
            {
                if (rec.Kind != SyncRecordKind.Request) continue;
                if (rec.Request == SyncRequest.MoreIds)
                {
                    (uint start, uint count) = _ids.Ids.Grant();
                    AddRange(peer, start, count);
                    var w = new SyncBatch.Writer();
                    w.IdRange(start, count);
                    Send(peer, w);
                }
                else if (rec.Request == SyncRequest.ResendRoutes || rec.Request == SyncRequest.ResendAll)
                {
                    Resend(peer, rec.Request == SyncRequest.ResendRoutes);
                }
            }
            LastApply = peer + ": " + r;
            if (r.Problems.Count > 0) _log.Info(LogCategory.Replication, peer + " proposal problems: " + string.Join("; ", r.Problems));
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
            EntityManager em = _codec.EntityManager;
            NativeArray<Entity> created = _created.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in created)
                {
                    if (_ids.GetNetId(e) != 0) continue;
                    Family family = _codec.Families.Classify(em, e);
                    if (FamilyClassifier.IsReplicated(family)) _ids.Bind(e, _ids.Ids.Allocate(), family);
                }
            }
            finally
            {
                created.Dispose();
            }
        }

        // ================================================================== tool hook (client)

        /// <summary>Called at the start of the ApplyTool phase: the local tool is about to apply its preview.</summary>
        public void OnToolApply()
        {
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
            if (_codec == null) return;
            if (IsHost) CaptureHost(now);
            else if (IsLiveClient) CaptureClient(now);
            _toolApplied = false;
            _toolCost = 0;
            _toolOriginals.Clear();
        }

        private void CaptureHost(double now)
        {
            EntityManager em = _codec.EntityManager;
            _batch.Reset();
            NativeArray<Entity> changed = _changed.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in changed)
                {
                    Family family = _codec.Families.Classify(em, e);
                    if (family == Family.ZoneBlock)
                    {
                        if (!em.HasComponent<Game.Common.Deleted>(e)) WriteZoneBlock(e);
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
                    SyncRecordKind kind = em.HasComponent<Game.Common.Created>(e) ? SyncRecordKind.Spawn : SyncRecordKind.Upsert;
                    if (id == 0)
                    {
                        id = _ids.Ids.Allocate();
                        _ids.Bind(e, id, family);
                        kind = SyncRecordKind.Spawn;
                    }
                    if (em.HasComponent<PrefabRef>(e)) _codec.EnsurePrefab(em.GetComponentData<PrefabRef>(e).m_Prefab, _batch);
                    _codec.WriteEntity(_batch, kind, id, family, e);
                    if (FamilyClassifier.IsScanned(family)) _shadow[id] = _codec.HashEntity(e, true);
                }
            }
            finally
            {
                changed.Dispose();
            }
            ScanValues(now);
            if (now - _lastClock >= ClockInterval)
            {
                _lastClock = now;
                _batch.Clock(_simulation.frameIndex);
            }
            if (!_batch.IsEmpty) Broadcast(_batch);
        }

        /// <summary>
        /// Values that change without update tags (line paths and states, city money): a rolling hash comparison over the
        /// scanned families, a slice per frame.
        /// </summary>
        private void ScanValues(double now)
        {
            if (now - _lastScan < ScanInterval && _scanCursor == 0) return;
            EntityManager em = _codec.EntityManager;
            if (_scanCursor == 0)
            {
                _lastScan = now;
                _scanIds.Clear();
                NativeArray<Entity> table = _ids.ReaderTable;
                bool includeCity = now - _lastCity >= CityInterval;
                if (includeCity) _lastCity = now;
                for (int id = 1; id < table.Length; id++)
                {
                    if (table[id] == Entity.Null) continue;
                    Family f = _ids.GetFamily((uint)id);
                    if (f == Family.Singleton ? includeCity : FamilyClassifier.IsScanned(f)) _scanIds.Add((uint)id);
                }
            }
            int end = Math.Min(_scanIds.Count, _scanCursor + ScanBatch);
            for (; _scanCursor < end; _scanCursor++)
            {
                uint id = _scanIds[_scanCursor];
                Entity e = _ids.GetEntity(id);
                if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                ulong hash = _codec.HashEntity(e, true);
                if (_shadow.TryGetValue(id, out ulong old) && old == hash) continue;
                bool first = !_shadow.ContainsKey(id);
                _shadow[id] = hash;
                if (first && _ids.GetFamily(id) != Family.Singleton) continue; // baseline only
                _codec.WriteEntity(_batch, SyncRecordKind.Values, id, _ids.GetFamily(id), e);
            }
            if (_scanCursor >= _scanIds.Count) _scanCursor = 0;
        }

        private void Resend(PeerId peer, bool routesOnly)
        {
            EntityManager em = _codec.EntityManager;
            var w = new SyncBatch.Writer();
            NativeArray<Entity> table = _ids.ReaderTable;
            int n = 0;
            for (int id = 1; id < table.Length; id++)
            {
                Entity e = table[id];
                if (e == Entity.Null || !em.Exists(e)) continue;
                Family f = _ids.GetFamily((uint)id);
                if (f == Family.Prefab) continue;
                if (routesOnly ? !(f == Family.Route || f == Family.Waypoint || f == Family.Segment) : !FamilyClassifier.IsReplicated(f)) continue;
                _codec.WriteEntity(w, SyncRecordKind.Upsert, (uint)id, f, e);
                if (++n % 512 == 0)
                {
                    Send(peer, w);
                    w = new SyncBatch.Writer();
                }
            }
            if (!w.IsEmpty) Send(peer, w);
            _log.Info(LogCategory.Replication, peer + ": resent " + n + (routesOnly ? " line entities" : " entities"));
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
            bool localTool = _tools.activeTool != null && !(_tools.activeTool is DefaultToolSystem);
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
                zones[i] = _codec.EnsurePrefab(prefab, _batch);
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

        private void ApplyZoneRecords(byte[] data)
        {
            double now = _sessions.Clock.Seconds;
            foreach (SyncRecord r in SyncBatch.Read(data, 0, data.Length))
            {
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

        private void Broadcast(SyncBatch.Writer w)
        {
            ArraySegment<byte> bytes = w.Finish();
            _sessions.Host?.Broadcast(MessageKind.Epoch, bytes.Array, bytes.Offset, bytes.Count);
        }

        private void Send(PeerId peer, SyncBatch.Writer w)
        {
            ArraySegment<byte> bytes = w.Finish();
            _sessions.Host?.SendTo(peer, MessageKind.Epoch, bytes.Array, bytes.Offset, bytes.Count);
        }
    }
}
