using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using SkylineTogether.Core;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Scheduling;
using SkylineTogether.Core.Session;
using SkylineTogether.Session;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// Cell maps (ARCH §2.4, §4.10.8): pollution (air, ground, noise), groundwater, natural resources, land value,
    /// telecom coverage, terrain attractiveness, population and availability grids, traffic ambience and soil water,
    /// i.e. every <c>CellMapSystem&lt;T&gt;</c> whose system is off on clients (all run in GameSimulation). Wind and zone
    /// ambience stay client-simulated (cosmetic allow-list of <see cref="FollowerClock"/>).
    /// <para>
    /// <b>Host.</b> One map per frame at most is copied as raw cells (<c>GetMap(readOnly: true)</c>, its job dependencies
    /// completed first, R07 §8.1) about every 2 s and hashed per 16×16-cell tile (<see cref="CellMapLayout"/>). Each live
    /// peer holds the tile hashes it was sent (<see cref="CellMapPeerState"/>); only changed tiles travel, zero-run coded
    /// (the message envelope compresses with LZ4), within a per-peer byte budget that grows while the peer's infoview
    /// shows that map. A joiner's baseline is taken when its snapshot is prepared, so it receives only what changed after;
    /// a slow audit resends one whole map per peer every few minutes.
    /// </para>
    /// <para>
    /// <b>Client.</b> Tiles are written at frame start: <c>GetMap(readOnly: false)</c>, dependencies completed, copy,
    /// <c>AddWriter(default)</c>. Resource painting (the terrain tool's ore, oil, fertility and groundwater brushes, applied
    /// locally by <c>ApplyBrushesSystem</c> as prediction) is sent to the host as patches of the painted cells, at most
    /// 5 per second per map; the host merges only the painted fields (<c>m_Base</c> of ore, oil and fertility;
    /// groundwater amount and maximum) and its next tiles carry the result to everyone. Main thread only.
    /// </para>
    /// </summary>
    public sealed class CellMapSync
    {
        private const int TileCells = 16;
        private const double ScanInterval = 2.0;
        private const double AuditInterval = 300.0;
        private const double PatchInterval = 0.2;
        private const int BytesPerSecond = 8_000, BurstBytes = 64_000;
        private const long MaxBacklogBytes = 256 * 1024;
        private const int MaxMessageBytes = 32 * 1024;
        private const int MaxInboxPerFrame = 16;

        private abstract class Channel
        {
            public string Name;
            public uint Id;
            public AttentionTopic Topic;
            /// <summary>Bytes of a cell a resource-paint patch may change (null: not paintable).</summary>
            public byte[] PaintMask;
            public int ExpectedCellBytes;
            public CellMapLayout Layout;
            public byte[] Current = Array.Empty<byte>();
            public ulong[] Hashes = Array.Empty<ulong>();
            public bool Valid;
            public bool Hot;
            public double NextScan;
            public bool Failed;
            // client: received tiles not yet written
            public readonly List<KeyValuePair<int, byte[]>> PendingTiles = new List<KeyValuePair<int, byte[]>>();
            // paint capture: the union of this frame's (or this throttle window's) stamps, in cells
            public bool Painted;
            public int PaintX0, PaintY0, PaintX1, PaintY1;
            public double NextPatch;

            public abstract bool TryGetLayout(out int width, out int height, out int cellBytes);

            /// <summary>Copies the whole map into <paramref name="dst"/> after completing its writers.</summary>
            public abstract void CopyOut(byte[] dst);

            /// <summary>Overwrites the whole map from <paramref name="src"/> after completing its readers and writers.</summary>
            public abstract void CopyIn(byte[] src);
        }

        private sealed class Channel<TCell> : Channel where TCell : struct, ISerializable
        {
            public CellMapSystem<TCell> System;

            public override bool TryGetLayout(out int width, out int height, out int cellBytes)
            {
                CellMapData<TCell> data = System.GetData(true, out JobHandle _);
                width = data.m_TextureSize.x;
                height = data.m_TextureSize.y;
                cellBytes = UnsafeUtility.SizeOf<TCell>();
                return data.m_Buffer.IsCreated && width > 0 && height > 0 && data.m_Buffer.Length == width * height;
            }

            public override void CopyOut(byte[] dst)
            {
                NativeArray<TCell> map = System.GetMap(true, out JobHandle deps);
                deps.Complete();
                NativeArray<byte> bytes = map.Reinterpret<byte>(UnsafeUtility.SizeOf<TCell>());
                // w8/robust: the copy is not bounds-checked in a player build; the cached layout must still match the map
                if (dst == null || dst.Length != bytes.Length) throw new InvalidOperationException("map size changed (" + bytes.Length + " bytes, expected " + (dst?.Length ?? 0) + ")");
                NativeArray<byte>.Copy(bytes, 0, dst, 0, bytes.Length);
            }

            public override void CopyIn(byte[] src)
            {
                NativeArray<TCell> map = System.GetMap(false, out JobHandle deps);
                deps.Complete();
                NativeArray<byte> bytes = map.Reinterpret<byte>(UnsafeUtility.SizeOf<TCell>());
                if (src == null || src.Length != bytes.Length) throw new InvalidOperationException("map size changed (" + bytes.Length + " bytes, received " + (src?.Length ?? 0) + ")"); // w8/robust
                NativeArray<byte>.Copy(src, 0, bytes, 0, bytes.Length);
                System.AddWriter(default(JobHandle));
            }
        }

        private sealed class HostPeer
        {
            public PeerId Peer;
            public readonly Dictionary<uint, CellMapPeerState> States = new Dictionary<uint, CellMapPeerState>();
            public readonly PeerByteBudget Budget = new PeerByteBudget(BytesPerSecond, BurstBytes, MaxBacklogBytes);
            public double NextAudit;
            public int AuditIndex;
        }

        private readonly SessionManager _sessions;
        private readonly ILog _log;
        private readonly List<Channel> _channels = new List<Channel>();
        private readonly Dictionary<uint, Channel> _byId = new Dictionary<uint, Channel>();
        private readonly Dictionary<byte, HostPeer> _peers = new Dictionary<byte, HostPeer>();
        private readonly Dictionary<byte, Dictionary<uint, ulong[]>> _joinBaselines = new Dictionary<byte, Dictionary<uint, ulong[]>>();
        private readonly Queue<(PeerId Peer, byte[] Data)> _inbox = new Queue<(PeerId, byte[])>();
        // w8/robust (host): what one peer may have waiting in the inbox
        private readonly InboundQuota _hostQuota = new InboundQuota(64, 16L << 20);
        private readonly ByteWriter _out = new ByteWriter(64 * 1024);
        private readonly List<PeerId> _peerScratch = new List<PeerId>();
        private World _world;
        private EntityQuery _brushes;
        private byte[] _scratch = new byte[16 * 1024];
        private byte[] _patchCells;
        private int _scanIndex;
        private double _lastSend = -1;
        private long _tilesSent, _tilesApplied, _patches;

        public CellMapSync(SessionManager sessions, ILog log)
        {
            _sessions = sessions;
            _log = log;
            sessions.HostStarted += OnHostStarted;
            sessions.ClientStarted += OnClientStarted;
            sessions.SessionEnded += Reset;
        }

        /// <summary>The attention topics of a peer's active infoview (set by <see cref="SyncController"/> from <see cref="AttentionValues"/>).</summary>
        public Func<PeerId, AttentionTopic> PeerTopics { get; set; }

        private bool IsHost => _sessions.Host != null;

        private bool IsLiveClient => _sessions.Client != null && _sessions.Client.State == ClientState.Live;

        private static double Now => UnityEngine.Time.realtimeSinceStartupAsDouble;

        public string Status
        {
            get
            {
                if (IsHost && _tilesSent > 0) return "cell maps: " + _tilesSent + " tiles sent" + (_patches > 0 ? ", " + _patches + " paint patches" : "");
                if (IsLiveClient && _tilesApplied + _patches > 0) return "cell maps: " + _tilesApplied + " tiles applied" + (_patches > 0 ? ", " + _patches + " paint patches sent" : "");
                return "";
            }
        }

        // ================================================================== setup

        private bool EnsureWorld()
        {
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return false;
            if (_world == world) return true;
            _world = world;
            _channels.Clear();
            _byId.Clear();
            byte[] resources = Mask(16, 0, 1, 4, 5, 8, 9);       // NaturalResourceCell: m_Base of fertility, ore, oil
            byte[] groundwater = Mask(6, 0, 1, 4, 5);            // GroundWater: m_Amount, m_Max (not m_Polluted)
            Add<AirPollution>(world.GetOrCreateSystemManaged<AirPollutionSystem>(), "AirPollution", AttentionTopic.Pollution, null, 2);
            Add<GroundPollution>(world.GetOrCreateSystemManaged<GroundPollutionSystem>(), "GroundPollution", AttentionTopic.Pollution, null, 4);
            Add<NoisePollution>(world.GetOrCreateSystemManaged<NoisePollutionSystem>(), "NoisePollution", AttentionTopic.Noise, null, 4);
            Add<GroundWater>(world.GetOrCreateSystemManaged<GroundWaterSystem>(), "GroundWater", AttentionTopic.Groundwater, groundwater, 6);
            Add<NaturalResourceCell>(world.GetOrCreateSystemManaged<NaturalResourceSystem>(), "NaturalResources", AttentionTopic.NaturalResources, resources, 16);
            Add<LandValueCell>(world.GetOrCreateSystemManaged<LandValueSystem>(), "LandValue", AttentionTopic.LandValue, null, 4);
            Add<TelecomCoverage>(world.GetOrCreateSystemManaged<TelecomCoverageSystem>(), "TelecomCoverage", AttentionTopic.Telecom, null, 2);
            Add<TerrainAttractiveness>(world.GetOrCreateSystemManaged<TerrainAttractivenessSystem>(), "TerrainAttractiveness", AttentionTopic.None, null, 8);
            Add<PopulationCell>(world.GetOrCreateSystemManaged<PopulationToGridSystem>(), "Population", AttentionTopic.None, null, 4);
            Add<AvailabilityInfoCell>(world.GetOrCreateSystemManaged<AvailabilityInfoToGridSystem>(), "AvailabilityInfo", AttentionTopic.None, null, 16);
            Add<TrafficAmbienceCell>(world.GetOrCreateSystemManaged<TrafficAmbienceSystem>(), "TrafficAmbience", AttentionTopic.None, null, 8);
            Add<SoilWater>(world.GetOrCreateSystemManaged<SoilWaterSystem>(), "SoilWater", AttentionTopic.Groundwater, null, 8);
            _brushes = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Brush>());
            return true;
        }

        private static byte[] Mask(int cellBytes, params int[] painted)
        {
            var mask = new byte[cellBytes];
            foreach (int b in painted) mask[b] = 0xFF;
            return mask;
        }

        private void Add<TCell>(CellMapSystem<TCell> system, string name, AttentionTopic topic, byte[] paintMask, int expectedCellBytes) where TCell : struct, ISerializable
        {
            if (system == null) return;
            var c = new Channel<TCell> { System = system, Name = name, Id = CellMapMessage.MapId(name), Topic = topic, PaintMask = paintMask, ExpectedCellBytes = expectedCellBytes };
            _channels.Add(c);
            _byId[c.Id] = c;
        }

        /// <summary>The channel's layout from the live system (null until a map exists or when it is unusable).</summary>
        private CellMapLayout LayoutOf(Channel c)
        {
            if (c.Failed) return null;
            if (c.Layout != null) return c.Layout;
            try
            {
                if (!c.TryGetLayout(out int w, out int h, out int cellBytes)) return null;
                if (cellBytes != c.ExpectedCellBytes)
                {
                    // the painted-field masks assume the decompiled layout; a patched game disables painting only
                    if (c.PaintMask != null) _log.Warn(LogCategory.Fields, c.Name + ": cell size " + cellBytes + " B (expected " + c.ExpectedCellBytes + "); resource paint proposals are off");
                    c.PaintMask = null;
                }
                c.Layout = new CellMapLayout(w, h, cellBytes, TileCells);
                c.Current = new byte[c.Layout.MapBytes];
                c.Hashes = new ulong[c.Layout.TileCount];
                if (_scratch.Length < c.Layout.MaxTileBytes) _scratch = new byte[c.Layout.MaxTileBytes];
                return c.Layout;
            }
            catch (Exception ex)
            {
                c.Failed = true;
                _log.Warn(LogCategory.Fields, "cell map " + c.Name + " cannot be synchronised", ex);
                return null;
            }
        }

        // ================================================================== lifecycle

        private void OnHostStarted(SessionHost host)
        {
            Reset();
            host.AppMessage += (peer, e) =>
            {
                if (e.Kind != MessageKind.CellMaps) return;
                // w8/robust: a peer's waiting paint patches are bounded (clients send at most a few per second)
                if (!_hostQuota.TryAdmit(peer, e.BodyCount))
                {
                    if (_hostQuota.TakeOverflow(peer)) _log.Warn(LogCategory.Fields, peer + " sends map patches faster than they are applied; further ones are dropped");
                    return;
                }
                var copy = new byte[e.BodyCount];
                Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                _inbox.Enqueue((peer, copy));
            };
            host.PeerJoining += OnPeerJoining;
            host.PeerLive += OnPeerLive;
            host.PeerLeft += peer =>
            {
                _peers.Remove(peer.Value);
                _joinBaselines.Remove(peer.Value);
                _hostQuota.Forget(peer); // w8/robust
            };
        }

        private void OnClientStarted(SessionClient client)
        {
            Reset();
            client.AppMessage += e =>
            {
                if (e.Kind != MessageKind.CellMaps) return;
                var copy = new byte[e.BodyCount];
                Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                _inbox.Enqueue((PeerId.Host, copy));
            };
        }

        private void Reset()
        {
            _peers.Clear();
            _joinBaselines.Clear();
            _inbox.Clear();
            _lastSend = -1;
            _tilesSent = _tilesApplied = _patches = 0;
            foreach (Channel c in _channels)
            {
                c.Valid = false;
                c.Hot = false;
                c.NextScan = 0;
                c.PendingTiles.Clear();
                c.Painted = false;
                // the layout is re-read: a new map may have another size
                c.Layout = null;
                c.Failed = false;
            }
        }

        /// <summary>Host: the joiner's snapshot is being prepared; what the maps hold now is its baseline.</summary>
        private void OnPeerJoining(PeerId peer)
        {
            try
            {
                if (!EnsureWorld()) return;
                var baseline = new Dictionary<uint, ulong[]>();
                foreach (Channel c in _channels)
                {
                    if (!ScanChannel(c, Now)) continue;
                    baseline[c.Id] = (ulong[])c.Hashes.Clone();
                }
                _joinBaselines[peer.Value] = baseline;
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Fields, "cell map baseline for " + peer + " failed; it gets every map", ex);
            }
        }

        private void OnPeerLive(PeerId peer)
        {
            var p = new HostPeer { Peer = peer, NextAudit = Now + AuditInterval };
            _joinBaselines.TryGetValue(peer.Value, out Dictionary<uint, ulong[]> baseline);
            _joinBaselines.Remove(peer.Value);
            foreach (Channel c in _channels)
            {
                if (c.Layout == null) continue;
                var state = new CellMapPeerState(c.Layout.TileCount);
                if (baseline != null && baseline.TryGetValue(c.Id, out ulong[] hashes) && hashes.Length == c.Layout.TileCount)
                {
                    for (int t = 0; t < hashes.Length; t++) state.MarkSent(t, hashes[t]);
                }
                p.States[c.Id] = state;
            }
            _peers[peer.Value] = p;
        }

        // ================================================================== tool hook (host and client)

        /// <summary>First in ApplyTool: records the cells this frame's resource brushes paint (ore, oil, fertility, groundwater).</summary>
        public void OnToolApply()
        {
            if (!IsHost && !IsLiveClient) return;
            try
            {
                if (!EnsureWorld() || _brushes.IsEmptyIgnoreFilter) return;
                EntityManager em = _world.EntityManager;
                NativeArray<Entity> entities = _brushes.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (Entity e in entities)
                    {
                        Brush b = em.GetComponentData<Brush>(e);
                        if (b.m_Tool == Entity.Null || !em.Exists(b.m_Tool) || !em.HasComponent<TerraformingData>(b.m_Tool)) continue;
                        Channel c = PaintChannel(em.GetComponentData<TerraformingData>(b.m_Tool).m_Target);
                        if (c == null) continue;
                        CellMapLayout layout = LayoutOf(c);
                        if (layout == null) continue;
                        Bounds2 area = ToolUtils.GetBounds(b);
                        int x0 = CellOf(area.min.x, layout.Width) - 1, y0 = CellOf(area.min.y, layout.Height) - 1;
                        int x1 = CellOf(area.max.x, layout.Width) + 1, y1 = CellOf(area.max.y, layout.Height) + 1;
                        x0 = Math.Max(0, x0);
                        y0 = Math.Max(0, y0);
                        x1 = Math.Min(layout.Width - 1, x1);
                        y1 = Math.Min(layout.Height - 1, y1);
                        if (x1 < x0 || y1 < y0) continue;
                        if (IsHost)
                        {
                            c.Hot = true; // rescan at this frame's end: everyone gets the painted tiles soon
                            continue;
                        }
                        if (!c.Painted)
                        {
                            c.Painted = true;
                            c.PaintX0 = x0;
                            c.PaintY0 = y0;
                            c.PaintX1 = x1;
                            c.PaintY1 = y1;
                        }
                        else
                        {
                            c.PaintX0 = Math.Min(c.PaintX0, x0);
                            c.PaintY0 = Math.Min(c.PaintY0, y0);
                            c.PaintX1 = Math.Max(c.PaintX1, x1);
                            c.PaintY1 = Math.Max(c.PaintY1, y1);
                        }
                    }
                }
                finally
                {
                    entities.Dispose();
                }
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Fields, "resource paint capture failed", ex);
            }
        }

        private Channel PaintChannel(TerraformingTarget target)
        {
            string name;
            switch (target)
            {
                case TerraformingTarget.Ore:
                case TerraformingTarget.Oil:
                case TerraformingTarget.FertileLand:
                    name = "NaturalResources";
                    break;
                case TerraformingTarget.GroundWater:
                    name = "GroundWater";
                    break;
                default:
                    return null;
            }
            return _byId.TryGetValue(CellMapMessage.MapId(name), out Channel c) && c.PaintMask != null ? c : null;
        }

        /// <summary>The cell of a world coordinate (<c>CellMapSystem.GetCell</c>: (0.5 + p / kMapSize) × size).</summary>
        private static int CellOf(float world, int size)
        {
            double v = (0.5 + world / (double)CellMapSystem<AirPollution>.kMapSize) * size;
            if (double.IsNaN(v)) return 0;
            return (int)Math.Floor(Math.Max(-1e6, Math.Min(1e6, v)));
        }

        // ================================================================== frame start

        /// <summary>Frame start: the host merges paint patches, a client writes received tiles.</summary>
        public void ApplyIncoming()
        {
            if (!IsHost && !IsLiveClient)
            {
                if (_sessions.Client == null && _sessions.Host == null) _inbox.Clear();
                return;
            }
            try
            {
                if (!EnsureWorld()) return;
                int n = 0;
                while (_inbox.Count > 0 && n++ < MaxInboxPerFrame)
                {
                    (PeerId peer, byte[] data) = _inbox.Dequeue();
                    if (_sessions.Host != null) _hostQuota.Release(peer, data.Length);
                    try
                    {
                        Receive(peer, data);
                    }
                    catch (DecodeException ex)
                    {
                        _log.Warn(LogCategory.Fields, "dropped a malformed cell-map message from " + peer + ": " + ex.Message);
                    }
                }
                if (!IsHost) WritePendingTiles();
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Fields, "cell-map apply failed", ex);
            }
        }

        private void Receive(PeerId peer, byte[] data)
        {
            var r = new ByteReader(data, 0, data.Length);
            CellMapMessage.Header h = CellMapMessage.ReadHeader(r);
            if (!_byId.TryGetValue(h.MapId, out Channel c)) return;
            CellMapLayout layout = LayoutOf(c);
            if (layout == null || !h.Matches(layout))
            {
                _log.Debug(LogCategory.Fields, c.Name + ": message for another map layout skipped");
                return;
            }
            if (IsHost)
            {
                if (h.Type != CellMapMessage.TypePatch || c.PaintMask == null) return;
                // final review: resource painting is a terrain tool brush; a peer without the right is ignored (the
                // periodic per-peer audit gives its replica the host's tiles back)
                SessionHost host = _sessions.Host;
                if (peer != PeerId.Host && host != null && !RolePermissions.Allows(host.GetRole(peer), Permission.Terraform)) return;
                CellMapMessage.ReadPatch(r, layout, out int x0, out int y0, out int w, out int hgt, ref _patchCells);
                c.CopyOut(c.Current);
                int changed = CellMapMessage.MergeMasked(layout, c.Current, x0, y0, w, hgt, _patchCells, c.PaintMask);
                if (changed > 0) c.CopyIn(c.Current);
                // Current no longer matches the tile hashes: rescanned at this frame's end, then streamed to everyone
                c.Valid = false;
                c.Hot = true;
                _patches++;
                return;
            }
            if (h.Type != CellMapMessage.TypeTiles) return;
            int count = CellMapMessage.ReadTileCount(r, layout);
            for (int i = 0; i < count; i++)
            {
                var tile = new byte[layout.MaxTileBytes];
                int index = CellMapMessage.ReadTile(r, layout, tile);
                c.PendingTiles.Add(new KeyValuePair<int, byte[]>(index, tile));
            }
        }

        private void WritePendingTiles()
        {
            foreach (Channel c in _channels)
            {
                if (c.PendingTiles.Count == 0 || c.Layout == null) continue;
                try
                {
                    c.CopyOut(c.Current);
                    foreach (KeyValuePair<int, byte[]> t in c.PendingTiles) c.Layout.WriteTile(t.Value, t.Key, c.Current);
                    c.CopyIn(c.Current);
                    _tilesApplied += c.PendingTiles.Count;
                }
                catch (Exception ex)
                {
                    c.Failed = true;
                    _log.Warn(LogCategory.Fields, "writing cell map " + c.Name + " failed; it is no longer synchronised", ex);
                }
                c.PendingTiles.Clear();
            }
        }

        // ================================================================== frame end

        /// <summary>Frame end: the host scans and streams tiles, a client sends its paint patches.</summary>
        public void CaptureOutgoing()
        {
            if (!IsHost && !IsLiveClient) return;
            try
            {
                if (!EnsureWorld()) return;
                double now = Now;
                if (IsHost)
                {
                    ScanNext(now);
                    Stream(now);
                }
                else
                {
                    SendPatches(now);
                }
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Fields, "cell-map capture failed", ex);
            }
        }

        private void ScanNext(double now)
        {
            // painted maps first, then at most one due map per frame (each map about every ScanInterval)
            foreach (Channel c in _channels)
            {
                if (c.Hot) ScanChannel(c, now);
            }
            for (int i = 0; i < _channels.Count; i++)
            {
                Channel c = _channels[(_scanIndex + i) % _channels.Count];
                if (now < c.NextScan) continue;
                _scanIndex = (_scanIndex + i + 1) % _channels.Count;
                ScanChannel(c, now);
                break;
            }
        }

        /// <summary>Copies one map and hashes its tiles; false when the map is unusable.</summary>
        private bool ScanChannel(Channel c, double now)
        {
            c.Hot = false;
            c.NextScan = now + ScanInterval;
            CellMapLayout layout = LayoutOf(c);
            if (layout == null) return false;
            try
            {
                c.CopyOut(c.Current);
                layout.HashTiles(c.Current, c.Hashes, _scratch);
                c.Valid = true;
                return true;
            }
            catch (Exception ex)
            {
                c.Failed = true;
                c.Valid = false;
                _log.Warn(LogCategory.Fields, "reading cell map " + c.Name + " failed; it is no longer synchronised", ex);
                return false;
            }
        }

        private void Stream(double now)
        {
            double dt = _lastSend < 0 ? 0 : Math.Min(1.0, now - _lastSend);
            _lastSend = now;
            SessionHost host = _sessions.Host;
            if (host == null || _peers.Count == 0) return;
            _peerScratch.Clear();
            foreach (HostPeer p in _peers.Values) _peerScratch.Add(p.Peer);
            foreach (PeerId peer in _peerScratch)
            {
                if (!_peers.TryGetValue(peer.Value, out HostPeer p)) continue;
                IPeerOutput output = host.GetPeerOutput(peer);
                if (output == null) continue;
                if (now >= p.NextAudit && _channels.Count > 0)
                {
                    // slow safety net: one whole map per audit interval is sent again
                    p.NextAudit = now + AuditInterval;
                    Channel audit = _channels[p.AuditIndex++ % _channels.Count];
                    if (audit.Layout != null && p.States.TryGetValue(audit.Id, out CellMapPeerState s)) s.Reset(audit.Layout.TileCount);
                }
                AttentionTopic topics = PeerTopics != null ? PeerTopics(peer) : AttentionTopic.None;
                bool wanted = false;
                foreach (Channel c in _channels) wanted |= c.Topic != AttentionTopic.None && (c.Topic & topics) != 0;
                p.Budget.Boost = wanted ? 4f : 1f;
                long backlog = output.QueuedBytes + Math.Max(0, output.Stats.PendingReliableBytes);
                int allowance = p.Budget.Allowance(dt, backlog);
                if (allowance <= 0) continue;
                // maps the peer's infoview shows first
                for (int pass = 0; pass < 2 && allowance > 0; pass++)
                {
                    foreach (Channel c in _channels)
                    {
                        if (allowance <= 0) break;
                        bool topical = c.Topic != AttentionTopic.None && (c.Topic & topics) != 0;
                        if (topical != (pass == 0) || !c.Valid || c.Layout == null) continue;
                        int used = SendTiles(host, p, c, allowance);
                        p.Budget.Spend(used);
                        allowance -= used;
                    }
                }
            }
        }

        private int SendTiles(SessionHost host, HostPeer p, Channel c, int allowance)
        {
            if (!p.States.TryGetValue(c.Id, out CellMapPeerState state) || state.TileCount != c.Layout.TileCount)
            {
                state = new CellMapPeerState(c.Layout.TileCount);
                p.States[c.Id] = state;
            }
            _out.Reset();
            int countPosition = CellMapMessage.BeginTiles(_out, c.Id, c.Layout);
            int tiles = 0;
            int limit = Math.Min(allowance, MaxMessageBytes);
            while (_out.Length < limit && state.NextChanged(c.Hashes, out int tile))
            {
                CellMapMessage.AddTile(_out, c.Layout, tile, c.Current, _scratch);
                state.MarkSent(tile, c.Hashes[tile]);
                tiles++;
            }
            if (tiles == 0) return 0;
            CellMapMessage.EndTiles(_out, countPosition, tiles);
            host.SendTo(p.Peer, MessageKind.CellMaps, _out.Buffer, 0, _out.Length);
            _tilesSent += tiles;
            return _out.Length;
        }

        /// <summary>Client: the cells painted since the last patch go to the host (at most every <see cref="PatchInterval"/>).</summary>
        private void SendPatches(double now)
        {
            SessionClient client = _sessions.Client;
            if (client == null) return;
            foreach (Channel c in _channels)
            {
                if (!c.Painted || now < c.NextPatch || c.Layout == null || c.PaintMask == null) continue;
                c.Painted = false;
                c.NextPatch = now + PatchInterval;
                c.CopyOut(c.Current);
                int maxEdge = CellMapMessage.MaxPatchCells;
                for (int y = c.PaintY0; y <= c.PaintY1; y += maxEdge)
                {
                    for (int x = c.PaintX0; x <= c.PaintX1; x += maxEdge)
                    {
                        int w = Math.Min(maxEdge, c.PaintX1 - x + 1), h = Math.Min(maxEdge, c.PaintY1 - y + 1);
                        int need = w * h * c.Layout.CellBytes;
                        if (_patchCells == null || _patchCells.Length < need) _patchCells = new byte[need];
                        _out.Reset();
                        CellMapMessage.WritePatch(_out, c.Id, c.Layout, x, y, w, h, c.Current, _patchCells);
                        client.SendToHost(MessageKind.CellMaps, _out.Buffer, 0, _out.Length);
                        _patches++;
                    }
                }
            }
        }
    }
}
