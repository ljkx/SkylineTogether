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
    /// Cell maps (ARCHITECTURE.md#zone-cells-cell-maps-terrain-and-water): pollution (air, ground, noise), groundwater, natural resources, land value,
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
        private const int MaxScanStalls = 8;

        private abstract class Channel
        {
            public string Name;
            public uint Id;
            public AttentionTopic Topic;
            /// <summary>Bytes of a cell a resource-paint patch may change (null: not paintable).</summary>
            public byte[] PaintMask;
            /// <summary>The valid range of each painted field (what the game's brushes can produce).</summary>
            public CellFieldClamp[] PaintClamps;
            /// <summary>A digest of the host's sent tiles waiting to be compared after this frame's tiles.</summary>
            public bool DigestPending;
            public ulong Digest;
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
            // client: the cells local stamps touched since their last patch, and their values before the first such stamp
            // (only cells whose painted fields changed are merged by the host; the rest of the rectangle is the client's
            // possibly older copy of the host's map)
            public bool[] PaintTouched;
            public byte[] PaintBaseline;
            public byte[] PaintedBits;
            /// <summary>Field offsets kept in order after a merge (low &lt;= mid &lt;= high; groundwater), or null.</summary>
            public int[] OrderedFields;

            public abstract bool TryGetLayout(out int width, out int height, out int cellBytes);

            /// <summary>A job still writes the map (reading it now would stall the main thread).</summary>
            public abstract bool WritersRunning();

            /// <summary>Frames a due scan waited for the map's writers.</summary>
            public int ScanStalls;

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

            public override bool WritersRunning()
            {
                System.GetMap(true, out JobHandle deps);
                return !deps.IsCompleted;
            }

            public override void CopyOut(byte[] dst)
            {
                NativeArray<TCell> map = System.GetMap(true, out JobHandle deps);
                deps.Complete();
                NativeArray<byte> bytes = map.Reinterpret<byte>(UnsafeUtility.SizeOf<TCell>());
                // the copy is not bounds-checked in a player build; the cached layout must still match the map
                if (dst == null || dst.Length != bytes.Length) throw new InvalidOperationException("map size changed (" + bytes.Length + " bytes, expected " + (dst?.Length ?? 0) + ")");
                NativeArray<byte>.Copy(bytes, 0, dst, 0, bytes.Length);
            }

            public override void CopyIn(byte[] src)
            {
                NativeArray<TCell> map = System.GetMap(false, out JobHandle deps);
                deps.Complete();
                NativeArray<byte> bytes = map.Reinterpret<byte>(UnsafeUtility.SizeOf<TCell>());
                if (src == null || src.Length != bytes.Length) throw new InvalidOperationException("map size changed (" + bytes.Length + " bytes, received " + (src?.Length ?? 0) + ")");
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
            public double NextDigest;
            public int DigestIndex;
        }

        private readonly SessionManager _sessions;
        private readonly ILog _log;
        private readonly List<Channel> _channels = new List<Channel>();
        private readonly Dictionary<uint, Channel> _byId = new Dictionary<uint, Channel>();
        private readonly Dictionary<byte, HostPeer> _peers = new Dictionary<byte, HostPeer>();
        private readonly Dictionary<byte, Dictionary<uint, ulong[]>> _joinBaselines = new Dictionary<byte, Dictionary<uint, ulong[]>>();
        private readonly Queue<(PeerId Peer, byte[] Data)> _inbox = new Queue<(PeerId, byte[])>();
        // host: what one peer may have waiting in the inbox
        private readonly InboundQuota _hostQuota = new InboundQuota(64, 16L << 20);
        private readonly ByteWriter _out = new ByteWriter(64 * 1024);
        private readonly List<PeerId> _peerScratch = new List<PeerId>();
        private World _world;
        private EntityQuery _brushes;
        private byte[] _scratch = new byte[16 * 1024];
        private byte[] _patchCells;
        private byte[] _patchPainted;
        private int _scanIndex;
        private double _lastSend = -1;
        private long _tilesSent, _tilesApplied, _patches;
        private const double DigestInterval = 10.0;
        private const double PaintQuietSeconds = 5.0;
        private readonly CellMapResyncGate _resync = new CellMapResyncGate();
        private readonly Stack<byte[]> _tilePool = new Stack<byte[]>();
        private int _tilePoolSize;
        private long _clamped, _resyncRequests, _resyncsServed, _digestsSent, _digestMismatches;
        private double _lastPaint = double.NegativeInfinity;

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
                if (IsHost && _tilesSent > 0)
                {
                    return "cell maps: " + _tilesSent + " tiles sent" + (_patches > 0 ? ", " + _patches + " paint patches" : "") + (_clamped > 0 ? ", " + _clamped + " painted values clamped" : "")
                           + (_resyncsServed > 0 ? ", " + _resyncsServed + " map resyncs" : "") + (_digestsSent > 0 ? ", " + _digestsSent + " map digests" : "");
                }
                if (IsLiveClient && _tilesApplied + _patches > 0)
                {
                    return "cell maps: " + _tilesApplied + " tiles applied" + (_patches > 0 ? ", " + _patches + " paint patches sent" : "") + (_digestMismatches > 0 ? ", " + _digestMismatches + " map digests differed" : "") + (_resyncRequests > 0 ? ", " + _resyncRequests + " map resyncs asked" : "");
                }
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
            // the brushes clamp what they paint to 0..10000 (ApplyBrushesSystem: resource m_Base as
            // ushort, groundwater amount as short, m_Max = amount; GroundWaterSystem.kMaxGroundWater)
            var resourceClamps = new[] { new CellFieldClamp(0, false, 0, 10000), new CellFieldClamp(4, false, 0, 10000), new CellFieldClamp(8, false, 0, 10000) };
            var groundwaterClamps = new[] { new CellFieldClamp(0, true, 0, 10000), new CellFieldClamp(4, true, 0, 10000) };
            Add<AirPollution>(world.GetOrCreateSystemManaged<AirPollutionSystem>(), "AirPollution", AttentionTopic.Pollution, null, 2);
            Add<GroundPollution>(world.GetOrCreateSystemManaged<GroundPollutionSystem>(), "GroundPollution", AttentionTopic.Pollution, null, 4);
            Add<NoisePollution>(world.GetOrCreateSystemManaged<NoisePollutionSystem>(), "NoisePollution", AttentionTopic.Noise, null, 4);
            Add<GroundWater>(world.GetOrCreateSystemManaged<GroundWaterSystem>(), "GroundWater", AttentionTopic.Groundwater, groundwater, 6, groundwaterClamps);
            // GroundWaterSystem relies on m_Polluted <= m_Amount <= m_Max
            if (_byId.TryGetValue(CellMapMessage.MapId("GroundWater"), out Channel water)) water.OrderedFields = new[] { 2, 0, 4 };
            Add<NaturalResourceCell>(world.GetOrCreateSystemManaged<NaturalResourceSystem>(), "NaturalResources", AttentionTopic.NaturalResources, resources, 16, resourceClamps);
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

        private void Add<TCell>(CellMapSystem<TCell> system, string name, AttentionTopic topic, byte[] paintMask, int expectedCellBytes, CellFieldClamp[] clamps = null) where TCell : struct, ISerializable
        {
            if (system == null) return;
            var c = new Channel<TCell> { System = system, Name = name, Id = CellMapMessage.MapId(name), Topic = topic, PaintMask = paintMask, PaintClamps = clamps, ExpectedCellBytes = expectedCellBytes };
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
                    c.PaintClamps = null;
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
                // a peer's waiting paint patches are bounded (clients send at most a few per second)
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
                _hostQuota.Forget(peer);
                _resync.ForgetPeer(peer.Value);
            };
        }

        private void OnClientStarted(SessionClient client)
        {
            Reset();
            client.AppMessage += e =>
            {
                if (e.Kind != MessageKind.CellMaps) return;
                // with the feature off nothing drains the inbox (it grew for the rest of the session); the
                // host's next keyframe or a rejoin (which re-arms the feature) brings the state back
                if (!Diagnostics.Features.SimValues.IsAvailable) return;
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
            _clamped = _resyncRequests = _resyncsServed = _digestsSent = _digestMismatches = 0;
            _lastPaint = double.NegativeInfinity;
            _resync.Clear();
            foreach (Channel c in _channels)
            {
                c.Valid = false;
                c.Hot = false;
                c.NextScan = 0;
                ReleaseTiles(c);
                c.Painted = false;
                c.DigestPending = false;
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
                        _lastPaint = Now; // the client's own prediction differs from the host's tiles for a while
                        RecordPaintBaseline(c, layout, x0, y0, x1, y1);
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

        /// <summary>
        /// Client, before the game's brush writes this frame's stamps: the cells of the stamp's rectangle not touched
        /// since the last patch keep their current values as the baseline the patch compares against.
        /// </summary>
        private static void RecordPaintBaseline(Channel c, CellMapLayout layout, int x0, int y0, int x1, int y1)
        {
            int cells = layout.Width * layout.Height;
            if (c.PaintTouched == null || c.PaintTouched.Length != cells || c.PaintBaseline == null || c.PaintBaseline.Length != layout.MapBytes)
            {
                c.PaintTouched = new bool[cells];
                c.PaintBaseline = new byte[layout.MapBytes];
            }
            if (c.Current.Length != layout.MapBytes) c.Current = new byte[layout.MapBytes];
            c.CopyOut(c.Current);
            int cellBytes = layout.CellBytes;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int cell = y * layout.Width + x;
                    if (c.PaintTouched[cell]) continue;
                    c.PaintTouched[cell] = true;
                    Buffer.BlockCopy(c.Current, cell * cellBytes, c.PaintBaseline, cell * cellBytes, cellBytes);
                }
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
                if (!IsHost)
                {
                    WritePendingTiles();
                    CheckDigests(Now); // after this frame's tiles, which the digest covers
                }
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
                if (h.Type == CellMapMessage.TypeResync)
                {
                    // the client's copy of this map differs from what it was sent: everything again (paced)
                    if (!_peers.TryGetValue(peer.Value, out HostPeer hp) || !hp.States.TryGetValue(c.Id, out CellMapPeerState rs)) return;
                    if (!_resync.TryAcceptHost(peer.Value, c.Id, Now)) return;
                    rs.Reset(layout.TileCount);
                    _resyncsServed++;
                    _log.Info(LogCategory.Fields, peer + " asked for cell map " + c.Name + " again (its copy differed); resending it");
                    return;
                }
                if (h.Type != CellMapMessage.TypePatch || c.PaintMask == null) return;
                // resource painting is a terrain tool brush; a peer without the right is ignored (the
                // periodic per-peer audit gives its replica the host's tiles back)
                SessionHost host = _sessions.Host;
                if (peer != PeerId.Host && host != null && !RolePermissions.Allows(host.GetRole(peer), Permission.Terraform)) return;
                CellMapMessage.ReadPatch(r, layout, out int x0, out int y0, out int w, out int hgt, ref _patchCells, ref _patchPainted);
                c.CopyOut(c.Current);
                // painted values beyond what the game's brushes produce are clamped before the merge
                int clamped = CellMapMessage.ClampPatch(layout, c.Current, x0, y0, w, hgt, _patchCells, c.PaintClamps);
                if (clamped > 0)
                {
                    if (_clamped == 0) _log.Warn(LogCategory.Fields, peer + " painted " + c.Name + " values outside the brush range; they were clamped");
                    _clamped += clamped;
                }
                // only the cells the player painted: the rest of the rectangle is the client's older copy of this map
                int changed = CellMapMessage.MergeMasked(layout, c.Current, x0, y0, w, hgt, _patchCells, c.PaintMask, _patchPainted);
                if (changed > 0 && c.OrderedFields != null) CellMapMessage.EnforceOrder(layout, c.Current, x0, y0, w, hgt, c.OrderedFields[0], c.OrderedFields[1], c.OrderedFields[2]);
                if (changed > 0) c.CopyIn(c.Current);
                // Current no longer matches the tile hashes: rescanned at this frame's end, then streamed to everyone
                c.Valid = false;
                c.Hot = true;
                _patches++;
                return;
            }
            if (h.Type == CellMapMessage.TypeDigest)
            {
                c.Digest = CellMapMessage.ReadDigest(r);
                c.DigestPending = true;
                return;
            }
            if (h.Type != CellMapMessage.TypeTiles) return;
            int count = CellMapMessage.ReadTileCount(r, layout);
            for (int i = 0; i < count; i++)
            {
                byte[] tile = RentTile(layout.MaxTileBytes); // pooled (was one allocation per tile)
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
                ReleaseTiles(c);
            }
        }

        private byte[] RentTile(int size)
        {
            if (size != _tilePoolSize)
            {
                _tilePool.Clear();
                _tilePoolSize = size;
            }
            return _tilePool.Count > 0 ? _tilePool.Pop() : new byte[size];
        }

        private void ReleaseTiles(Channel c)
        {
            foreach (KeyValuePair<int, byte[]> t in c.PendingTiles)
            {
                if (t.Value.Length == _tilePoolSize && _tilePool.Count < 256) _tilePool.Push(t.Value);
            }
            c.PendingTiles.Clear();
        }

        /// <summary>
        /// Compares each map whose digest arrived with this replica's own tiles; a map that differs is
        /// asked for again (<see cref="CellMapMessage.TypeResync"/>, backing off while it keeps differing). Skipped while
        /// the player paints (the local prediction differs until the host's merged tiles arrive).
        /// </summary>
        private void CheckDigests(double now)
        {
            SessionClient client = _sessions.Client;
            foreach (Channel c in _channels)
            {
                if (!c.DigestPending) continue;
                c.DigestPending = false;
                if (client == null || c.Layout == null || c.Failed || c.PendingTiles.Count > 0) continue;
                if (c.Painted || now - _lastPaint < PaintQuietSeconds) continue;
                try
                {
                    c.CopyOut(c.Current);
                    c.Layout.HashTiles(c.Current, c.Hashes, _scratch);
                }
                catch (Exception ex)
                {
                    _log.Debug(LogCategory.Fields, "checking cell map " + c.Name + " failed: " + ex.Message);
                    continue;
                }
                if (CellMapMessage.Digest(c.Hashes, c.Layout.TileCount) == c.Digest)
                {
                    _resync.ClientMatched(c.Id);
                    continue;
                }
                _digestMismatches++;
                if (!_resync.TryRequestClient(c.Id, now)) continue;
                _out.Reset();
                CellMapMessage.WriteResync(_out, c.Id, c.Layout);
                client.SendToHost(MessageKind.CellMaps, _out.Buffer, 0, _out.Length);
                _resyncRequests++;
                _log.Info(LogCategory.Fields, "cell map " + c.Name + " differs from the host's; asked for it again");
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
                if (c.Hot && !Stalled(c)) ScanChannel(c, now);
            }
            for (int i = 0; i < _channels.Count; i++)
            {
                Channel c = _channels[(_scanIndex + i) % _channels.Count];
                if (now < c.NextScan) continue;
                if (Stalled(c)) break; // next frame, once the writers finished
                _scanIndex = (_scanIndex + i + 1) % _channels.Count;
                ScanChannel(c, now);
                break;
            }
        }

        /// <summary>
        /// True while a job still writes the map and the scan has waited fewer than <see cref="MaxScanStalls"/>
        /// frames (the scan then copies the map without completing the simulation's jobs on the main thread).
        /// </summary>
        private static bool Stalled(Channel c)
        {
            bool running;
            try
            {
                running = c.Layout != null && c.WritersRunning();
            }
            catch (Exception)
            {
                running = false;
            }
            if (running && c.ScanStalls < MaxScanStalls)
            {
                c.ScanStalls++;
                return true;
            }
            c.ScanStalls = 0;
            return false;
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
                if (now >= p.NextDigest && _channels.Count > 0) SendDigest(host, p, now);
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

        /// <summary>
        /// Every <see cref="DigestInterval"/>, the digest of one map (round-robin) as this peer was sent it,
        /// once the peer is up to date with that map; the client compares it with its own tiles.
        /// </summary>
        private void SendDigest(SessionHost host, HostPeer p, double now)
        {
            p.NextDigest = now + DigestInterval;
            for (int i = 0; i < _channels.Count; i++)
            {
                Channel c = _channels[(p.DigestIndex + i) % _channels.Count];
                if (!c.Valid || c.Layout == null || !p.States.TryGetValue(c.Id, out CellMapPeerState state) || state.TileCount != c.Layout.TileCount) continue;
                if (!state.TryDigest(c.Hashes, out ulong digest)) continue;
                p.DigestIndex = (p.DigestIndex + i + 1) % _channels.Count;
                _out.Reset();
                CellMapMessage.WriteDigest(_out, c.Id, c.Layout, digest);
                host.SendTo(p.Peer, MessageKind.CellMaps, _out.Buffer, 0, _out.Length);
                _digestsSent++;
                return;
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
                if (c.PaintTouched == null || c.PaintBaseline == null || c.PaintBaseline.Length != c.Current.Length) continue; // nothing recorded
                int maxEdge = CellMapMessage.MaxPatchCells;
                for (int y = c.PaintY0; y <= c.PaintY1; y += maxEdge)
                {
                    for (int x = c.PaintX0; x <= c.PaintX1; x += maxEdge)
                    {
                        int w = Math.Min(maxEdge, c.PaintX1 - x + 1), h = Math.Min(maxEdge, c.PaintY1 - y + 1);
                        int need = w * h * c.Layout.CellBytes;
                        if (_patchCells == null || _patchCells.Length < need) _patchCells = new byte[need];
                        int maskBytes = CellMapMessage.PaintedMaskBytes(w, h);
                        if (c.PaintedBits == null || c.PaintedBits.Length < maskBytes) c.PaintedBits = new byte[maskBytes];
                        // only the cells whose painted fields changed since before the first stamp touched them
                        if (CellMapMessage.MarkPainted(c.Layout, c.Current, c.PaintBaseline, c.PaintTouched, c.PaintMask, x, y, w, h, c.PaintedBits) == 0) continue;
                        _out.Reset();
                        CellMapMessage.WritePatch(_out, c.Id, c.Layout, x, y, w, h, c.Current, _patchCells, c.PaintedBits);
                        client.SendToHost(MessageKind.CellMaps, _out.Buffer, 0, _out.Length);
                        _patches++;
                    }
                }
                for (int y = c.PaintY0; y <= c.PaintY1; y++)
                {
                    for (int x = c.PaintX0; x <= c.PaintX1; x++) c.PaintTouched[y * c.Layout.Width + x] = false;
                }
            }
        }
    }
}
