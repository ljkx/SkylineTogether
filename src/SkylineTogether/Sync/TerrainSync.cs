using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using SkylineTogether.Core;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Session;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// Terrain sync (ARCH §4.10.9, R07 §2-§5). The authoritative terrain is the host's base heightmap
    /// (<c>TerrainSystem.heightmap</c>, R16, 4096²); roads, lots and terrain areas are not baked into it but rasterised
    /// every time from ECS state into the cascade (<c>TerrainSystem.RenderCascades</c>, re-triggered by the
    /// Created/Updated/Deleted tags of lots, edges, nodes and areas), so building and road flattening follows from the
    /// replicated structure by itself and needs nothing here.
    /// <list type="bullet">
    /// <item><b>Capture</b> (host and client): at the start of the ApplyTool phase, the <c>Temp</c> + <c>Brush</c> stamps
    /// the vanilla <c>ApplyBrushesSystem</c> is about to apply, with the clamped frame time.</item>
    /// <item><b>Client stroke</b>: predicted by the vanilla tool, sent to the host as stamps. The host replays them through
    /// <c>TerrainSystem.ApplyBrush</c> with frame-time compensation (<see cref="BrushStrengthCompensation"/>) and
    /// forwards them; every other client replays them for immediate visuals.</item>
    /// <item><b>Exactness</b>: the host tracks the tiles every stamp touched; once a stroke settles (or every second during
    /// a long stroke) it reads them back without blocking, and broadcasts the changed 64×64 tiles. Clients copy them into
    /// their heightmap (<c>Graphics.CopyTexture</c>) and invalidate like a brush does (<c>OnAreaChanged</c>,
    /// <c>WaterSystem.TerrainWillChangeFromBrush</c>, <c>UpdateMinMax</c>). The originating client defers tiles while its
    /// own stroke is in progress, so its prediction is not snapped back mid-stroke.</item>
    /// <item><b>Audit</b>: the host reads back one row of tiles every few seconds and broadcasts their hashes; a client
    /// whose tiles differ asks for them (<see cref="TerrainMessageType.TileRequest"/>, rate-limited per peer).</item>
    /// </list>
    /// Fail-safe: without async readback or texture copy support only the stamp replay runs; every entry point catches
    /// and logs. Main thread only; driven by <see cref="SyncController"/>.
    /// </summary>
    public sealed class TerrainSync
    {
        private const int TileSize = 64;
        private const int StampMargin = 4;
        private const double SettleSeconds = 0.25;
        private const double MaxPendingSeconds = 1.0;
        private const int MaxTilesPerFlush = 512;
        private const int MaxTilesPerMessage = 512;
        private const int MaxBytesPerMessage = 4 * 1024 * 1024;
        private const int MaxInflight = 48;
        private const double AuditInterval = 4.0;
        private const double LocalStrokeLinger = 0.6;
        private const int MaxMessagesPerFrame = 16;
        private const float MaxStampSize = 20000f;
        private const double RequestWindow = 30.0;
        private const int MaxRequestedTilesPerWindow = 1024;
        private const int MaxTilesPerRequest = 256;
        private const double VerifyQuietSeconds = 3.0;

        private readonly SessionManager _sessions;
        private readonly ILog _log;
        private readonly Queue<(PeerId Peer, byte[] Data)> _inbox = new Queue<(PeerId, byte[])>();
        // w8/robust (host): what one peer may have waiting, and how many of its brush stamps are replayed per frame
        private readonly InboundQuota _hostQuota = new InboundQuota(128, 16L << 20);
        private const int MaxStampsPerPeerFrame = 512;
        private readonly Dictionary<byte, (int Frame, int Count)> _stampBudget = new Dictionary<byte, (int, int)>();
        private long _stampsRefused;
        private readonly List<GpuReadback<ushort>> _inflight = new List<GpuReadback<ushort>>();
        private readonly List<TerrainStamp> _outStamps = new List<TerrainStamp>();
        private readonly List<TerrainStamp> _stampScratch = new List<TerrainStamp>();
        private readonly List<TerrainStamp> _forward = new List<TerrainStamp>();
        private readonly HashSet<string> _warned = new HashSet<string>();
        private readonly ByteWriter _small = new ByteWriter(4096);

        // host
        private readonly HashSet<int> _dirty = new HashSet<int>();
        private readonly StrokeSettleDetector _settle = new StrokeSettleDetector(SettleSeconds) { MaxPendingSeconds = MaxPendingSeconds };
        private readonly Dictionary<int, ulong> _sentHash = new Dictionary<int, ulong>();
        private readonly Dictionary<byte, HashSet<int>> _requests = new Dictionary<byte, HashSet<int>>();
        private readonly Dictionary<byte, (double Start, int Count)> _requestBudget = new Dictionary<byte, (double, int)>();
        private bool _flushWanted;
        private int _auditRow;
        private double _nextAudit;

        // client
        private readonly Dictionary<int, ushort[]> _deferred = new Dictionary<int, ushort[]>();
        private readonly List<TileId> _mismatches = new List<TileId>();
        private double _localStrokeUntil;
        private double _remoteStampAt = double.NegativeInfinity;
        private int _verifyJobs;

        // game state
        private UploadTextures _uploads;
        private TerrainSystem _terrain;
        private WaterSystem _water;
        private PrefabSystem _prefabs;
        private TerrainMaterialSystem _material;
        private NetIdSystem _ids;
        private EntityQuery _brushes;
        private bool _worldReady;
        private TileGrid _grid;
        private ushort[] _tileBuffer = new ushort[TileSize * TileSize];
        private int _generation;
        private bool _gpuSupported;
        private long _tilesSent, _tilesApplied, _stampsReplayed, _skippedEarlyStamps;

        private enum JobKind
        {
            Broadcast,
            Reply,
            Audit,
            Verify,
        }

        private sealed class JobTag
        {
            public JobKind Kind;
            public PeerId Peer;
            public Dictionary<int, ulong> Expected;
        }

        /// <summary>Collects tiles into tile messages of bounded size and sends each when full.</summary>
        private sealed class TileOutbox
        {
            public readonly ByteWriter Writer = new ByteWriter(1 << 16);
            public int CountAt = -1;
            public int Count;
        }

        public TerrainSync(SessionManager sessions, ILog log)
        {
            _sessions = sessions;
            _log = log;
            sessions.HostStarted += OnHostStarted;
            sessions.ClientStarted += OnClientStarted;
            sessions.SessionEnded += Reset;
        }

        /// <summary>Host: raised with the world bounds of every flushed tile set (water sync marks those tiles hot).</summary>
        public event Action<Bounds2> HeightsChanged;

        private bool IsHost => _sessions.Host != null;

        private bool IsLiveClient => _sessions.Client != null && _sessions.Client.State == ClientState.Live;

        private double Now => _sessions.Clock.Seconds;

        /// <summary>Short diagnostics for the session panel ("" when idle).</summary>
        public string Status
        {
            get
            {
                if (!IsHost && !IsLiveClient) return "";
                if (_tilesSent == 0 && _tilesApplied == 0 && _stampsReplayed == 0) return "";
                return IsHost ? "terrain: " + _tilesSent + " tiles sent"
                    : "terrain: " + _tilesApplied + " tiles applied" + (_skippedEarlyStamps > 0 ? ", " + _skippedEarlyStamps + " stroke replays skipped while joining" : "");
            }
        }

        // ================================================================== lifecycle

        private void OnHostStarted(SessionHost host)
        {
            Reset();
            host.AppMessage += (peer, e) =>
            {
                if (e.Kind != MessageKind.Terrain) return;
                // w8/robust: a peer's waiting strokes and requests are bounded
                if (!_hostQuota.TryAdmit(peer, e.BodyCount))
                {
                    if (_hostQuota.TakeOverflow(peer)) _log.Warn(LogCategory.Fields, peer + " sends terrain messages faster than they are applied; further ones are dropped (the tile audit repairs its view)");
                    return;
                }
                var copy = new byte[e.BodyCount];
                Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                _inbox.Enqueue((peer, copy));
            };
            host.PeerLeft += peer =>
            {
                _requests.Remove(peer.Value);
                _requestBudget.Remove(peer.Value);
                _hostQuota.Forget(peer); // w8/robust
            };
        }

        private void OnClientStarted(SessionClient client)
        {
            Reset();
            client.AppMessage += e =>
            {
                if (e.Kind != MessageKind.Terrain) return;
                // w8/robust: with the feature off nothing drains the inbox (it grew for the rest of the session); the
                // host's next keyframe or a rejoin (which re-arms the feature) brings the state back
                if (!Diagnostics.Features.Terrain.IsAvailable) return;
                // polish: a joiner still waiting for the city skips stroke replays (the snapshot or the host's settled
                // tiles carry their result; a replay on top of the snapshot would apply a stroke twice)
                if (client.State != ClientState.Live && !TerrainMessage.KeepBeforeLive(e.Body, e.BodyOffset, e.BodyCount))
                {
                    _skippedEarlyStamps++;
                    return;
                }
                var copy = new byte[e.BodyCount];
                Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                _inbox.Enqueue((PeerId.Host, copy));
            };
        }

        private void Reset()
        {
            _generation++;
            _inbox.Clear();
            _inflight.Clear();
            _outStamps.Clear();
            _dirty.Clear();
            _settle.Reset();
            _sentHash.Clear();
            _requests.Clear();
            _requestBudget.Clear();
            _deferred.Clear();
            _mismatches.Clear();
            _flushWanted = false;
            _verifyJobs = 0;
            _localStrokeUntil = 0;
            _remoteStampAt = double.NegativeInfinity;
            _nextAudit = 0;
            _tilesSent = _tilesApplied = _stampsReplayed = _skippedEarlyStamps = 0;
            try
            {
                _uploads?.Dispose();
            }
            catch (Exception)
            {
                // textures may already be gone with the world
            }
            _uploads = null;
            if (_worldReady) _gpuSupported = GpuReadback<ushort>.Supported && UploadTextures.CopySupported;
        }

        private bool EnsureWorld()
        {
            if (_worldReady) return _terrain != null && _ids != null;
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return false;
            _worldReady = true;
            _terrain = world.GetOrCreateSystemManaged<TerrainSystem>();
            _water = world.GetOrCreateSystemManaged<WaterSystem>();
            _prefabs = world.GetOrCreateSystemManaged<PrefabSystem>();
            _material = world.GetOrCreateSystemManaged<TerrainMaterialSystem>();
            _ids = world.GetOrCreateSystemManaged<NetIdSystem>();
            _brushes = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Brush>());
            _gpuSupported = GpuReadback<ushort>.Supported && UploadTextures.CopySupported;
            if (!_gpuSupported)
            {
                _log.Warn(LogCategory.Fields, "terrain tiles disabled: this GPU path lacks async readback or texture copies; terrain follows brush replay only");
            }
            return true;
        }

        /// <summary>The tile grid of the current heightmap (rebuilt when its size changes); false before a map is loaded.</summary>
        private bool EnsureGrid()
        {
            Texture hm = _terrain?.heightmap;
            if (hm == null) return false;
            if (_grid == null || _grid.Width != hm.width || _grid.Height != hm.height)
            {
                _grid = new TileGrid(hm.width, hm.height, TileSize);
                _sentHash.Clear();
                _dirty.Clear();
                _deferred.Clear();
            }
            return true;
        }

        private TileGridInfo GridInfo => new TileGridInfo(_grid.Width, _grid.Height, _grid.TileSize);

        // ================================================================== entry points

        /// <summary>Frame start (after <c>EndFrameBarrier</c>): received stamps, tiles, manifests and requests.</summary>
        public void ApplyIncoming()
        {
            if (!IsHost && !IsLiveClient)
            {
                if (_sessions.Client == null && _inbox.Count > 0) _inbox.Clear();
                return;
            }
            try
            {
                if (!EnsureWorld()) return;
                double now = Now;
                int n = 0;
                while (_inbox.Count > 0 && n++ < MaxMessagesPerFrame)
                {
                    (PeerId peer, byte[] data) = _inbox.Dequeue();
                    if (_sessions.Host != null) _hostQuota.Release(peer, data.Length);
                    try
                    {
                        Handle(peer, data, now);
                    }
                    catch (DecodeException ex)
                    {
                        _log.Warn(LogCategory.Fields, "dropped a malformed terrain message from " + peer + ": " + ex.Message);
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Guard.Report(Diagnostics.Features.Terrain, "applying a terrain message from " + peer, ex);
                    }
                }
                if (!IsHost) FlushDeferred(now);
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Terrain, "terrain apply", ex);
            }
        }

        /// <summary>
        /// First in the ApplyTool phase (which runs only when the active tool applies): records the brush stamps the
        /// vanilla <c>ApplyBrushesSystem</c> applies right after, on the host and on live clients.
        /// </summary>
        public void OnToolApply()
        {
            if (!IsHost && !IsLiveClient) return;
            try
            {
                if (!EnsureWorld() || _brushes.IsEmptyIgnoreFilter) return;
                CaptureStamps();
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Terrain, "terrain stamp capture", ex);
            }
        }

        /// <summary>Frame end (before <c>PrepareCleanUpSystem</c>): sends stamps, finishes readbacks, flushes tiles, audits.</summary>
        public void CaptureOutgoing()
        {
            if (!IsHost && !IsLiveClient) return;
            try
            {
                if (!EnsureWorld()) return;
                double now = Now;
                SendStamps();
                ProcessReadbacks(now);
                if (IsHost)
                {
                    HostFlush(now);
                    HostReplies();
                    HostAudit(now);
                }
                else
                {
                    ClientRequestMismatches(now);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Terrain, "terrain capture", ex);
            }
        }

        // ================================================================== capture

        private void CaptureStamps()
        {
            EntityManager em = _terrain.EntityManager;
            float dt = BrushStrengthCompensation.ClampDelta(UnityEngine.Time.unscaledDeltaTime);
            double now = Now;
            NativeArray<Entity> entities = _brushes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in entities)
                {
                    Brush b = em.GetComponentData<Brush>(e);
                    if (b.m_Tool == Entity.Null || !em.Exists(b.m_Tool) || !em.HasComponent<TerraformingData>(b.m_Tool)) continue;
                    TerraformingData td = em.GetComponentData<TerraformingData>(b.m_Tool);
                    if (td.m_Target == TerraformingTarget.Height)
                    {
                        if (IsHost) MarkDirty(ToolUtils.GetBounds(b), now);
                        else _localStrokeUntil = now + LocalStrokeLinger;
                    }
                    Entity prefab = em.HasComponent<PrefabRef>(e) ? em.GetComponentData<PrefabRef>(e).m_Prefab : Entity.Null;
                    uint tool = _ids.GetNetId(b.m_Tool);
                    uint brush = prefab == Entity.Null ? 0 : _ids.GetNetId(prefab);
                    if (tool == 0 || brush == 0)
                    {
                        // tiles still converge; only the low-latency replay of this stamp is lost
                        WarnOnce("unbound", "a terrain brush prefab has no net id; its stamps are not replayed remotely");
                        continue;
                    }
                    _outStamps.Add(new TerrainStamp
                    {
                        ToolPrefab = tool,
                        BrushPrefab = brush,
                        PositionX = b.m_Position.x, PositionY = b.m_Position.y, PositionZ = b.m_Position.z,
                        TargetX = b.m_Target.x, TargetY = b.m_Target.y, TargetZ = b.m_Target.z,
                        StartX = b.m_Start.x, StartY = b.m_Start.y, StartZ = b.m_Start.z,
                        Angle = b.m_Angle,
                        Size = b.m_Size,
                        Strength = b.m_Strength,
                        Opacity = b.m_Opacity,
                        DeltaTime = dt,
                    });
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void SendStamps()
        {
            if (_outStamps.Count == 0) return;
            if (_outStamps.Count > TerrainMessage.MaxStamps) _outStamps.RemoveRange(TerrainMessage.MaxStamps, _outStamps.Count - TerrainMessage.MaxStamps);
            _small.Reset();
            if (IsHost)
            {
                TerrainMessage.WriteStamps(_small, PeerId.Host, _outStamps);
                _sessions.Host.Broadcast(MessageKind.Terrain, _small.Buffer, 0, _small.Length);
            }
            else
            {
                TerrainMessage.WriteStamps(_small, _sessions.Client.LocalPeer, _outStamps);
                _sessions.Client.SendToHost(MessageKind.Terrain, _small.Buffer, 0, _small.Length);
            }
            _outStamps.Clear();
        }

        /// <summary>Host: the tiles a height stamp with these world bounds writes become dirty.</summary>
        private void MarkDirty(Bounds2 area, double now)
        {
            if (!_gpuSupported || !EnsureGrid()) return;
            float2 offset = _terrain.playableOffset, size = _terrain.playableArea;
            if (!TileGrid.WorldToTexelRect(area.min.x, area.min.y, area.max.x, area.max.y, offset.x, offset.y, size.x, size.y,
                                           _grid.Width, _grid.Height, StampMargin, out int x0, out int y0, out int x1, out int y1))
            {
                return;
            }
            foreach (TileId tile in _grid.TilesIn(x0, y0, x1, y1)) _dirty.Add(_grid.IndexOf(tile));
            _settle.OnStamp(x0, y0, x1, y1, now);
        }

        // ================================================================== incoming

        private void Handle(PeerId peer, byte[] data, double now)
        {
            var r = new ByteReader(data, 0, data.Length);
            TerrainMessageType type = TerrainMessage.ReadHeader(r);
            if (IsHost)
            {
                if (type == TerrainMessageType.Stamps) HostApplyStamps(peer, r, now);
                else if (type == TerrainMessageType.TileRequest) HostQueueRequest(peer, r, now);
                else _log.Debug(LogCategory.Fields, peer + " sent a host-only terrain message (" + type + "); ignored");
                return;
            }
            switch (type)
            {
                case TerrainMessageType.Stamps:
                    ClientReplayStamps(r);
                    break;
                case TerrainMessageType.Tiles:
                    ClientApplyTiles(r, now);
                    break;
                case TerrainMessageType.Manifest:
                    ClientVerify(r, now);
                    break;
            }
        }

        private void HostApplyStamps(PeerId peer, ByteReader r, double now)
        {
            // final review: proposals, global channels and actions check the role; terrain strokes did not, so a viewer
            // could terraform the host's city (the client-side viewer gate is comfort only). Its replica is corrected by
            // the tile audit.
            SessionHost host = _sessions.Host;
            if (peer != PeerId.Host && host != null && !RolePermissions.Allows(host.GetRole(peer), Permission.Terraform))
            {
                WarnOnce("terraform." + peer.Value, peer + " may not terraform (role); its terrain strokes are ignored");
                return;
            }
            _stampScratch.Clear();
            TerrainMessage.ReadStamps(r, _stampScratch);
            _forward.Clear();
            int frame = UnityEngine.Time.frameCount;
            _stampBudget.TryGetValue(peer.Value, out (int Frame, int Count) budget);
            if (budget.Frame != frame) budget = (frame, 0);
            foreach (TerrainStamp s in _stampScratch)
            {
                // w8/robust: positions inside the map, opacity a fraction, bounded strength; a budget per peer and frame
                // (each stamp is a GPU brush pass, re-broadcast to everyone)
                if (!s.IsPlausible(MaxStampSize, TerrainStamp.MaxCoordinate, TerrainStamp.MaxStrength) || budget.Count >= MaxStampsPerPeerFrame)
                {
                    _stampsRefused++;
                    continue;
                }
                budget.Count++;
                if (!Replay(s, out Bounds2 area, out bool height)) continue;
                if (height) MarkDirty(area, now);
                _forward.Add(s);
            }
            _stampBudget[peer.Value] = budget;
            if (_forward.Count == 0) return;
            _small.Reset();
            TerrainMessage.WriteStamps(_small, peer, _forward);
            _sessions.Host.Broadcast(MessageKind.Terrain, _small.Buffer, 0, _small.Length);
        }

        private void ClientReplayStamps(ByteReader r)
        {
            _stampScratch.Clear();
            PeerId origin = TerrainMessage.ReadStamps(r, _stampScratch);
            if (origin == _sessions.Client.LocalPeer) return; // our own stroke: already predicted locally
            _remoteStampAt = Now;
            foreach (TerrainStamp s in _stampScratch)
            {
                if (s.IsPlausible(MaxStampSize, TerrainStamp.MaxCoordinate, TerrainStamp.MaxStrength)) Replay(s, out _, out _); // w8/robust: the same checks as the host
            }
        }

        /// <summary>
        /// Applies one stamp like <c>ApplyBrushesSystem</c> (<c>ApplyBrushesSystem.cs:267-314</c> [V]): heights through the
        /// public <c>TerrainSystem.ApplyBrush</c> with the strength compensated for this frame's time, material paint
        /// through <c>TerrainMaterialSystem.ApplyBrush</c>. Resource brushes (ore, oil, fertility, groundwater: CPU cell
        /// maps) are not replayed; they belong to cell-map sync.
        /// </summary>
        private bool Replay(TerrainStamp s, out Bounds2 area, out bool height)
        {
            area = default(Bounds2);
            height = false;
            EntityManager em = _terrain.EntityManager;
            Entity tool = _ids.GetEntity(s.ToolPrefab);
            Entity brushPrefab = _ids.GetEntity(s.BrushPrefab);
            if (tool == Entity.Null || brushPrefab == Entity.Null || !em.Exists(tool) || !em.HasComponent<TerraformingData>(tool))
            {
                WarnOnce("prefab", "a terrain stamp names an unknown tool or brush prefab; skipped (tiles still converge)");
                return false;
            }
            var brush = new Brush
            {
                m_Tool = tool,
                m_Position = new float3(s.PositionX, s.PositionY, s.PositionZ),
                m_Target = new float3(s.TargetX, s.TargetY, s.TargetZ),
                m_Start = new float3(s.StartX, s.StartY, s.StartZ),
                m_Angle = s.Angle,
                m_Size = s.Size,
                m_Strength = s.Strength,
                m_Opacity = s.Opacity,
            };
            TerraformingData td = em.GetComponentData<TerraformingData>(tool);
            switch (td.m_Target)
            {
                case TerraformingTarget.Height:
                {
                    if (!_prefabs.TryGetPrefab(brushPrefab, out BrushPrefab prefab) || prefab == null)
                    {
                        WarnOnce("brush", "a terrain stamp names a prefab that is not a brush; skipped");
                        return false;
                    }
                    TerraformingType type = td.m_Type;
                    if ((type == TerraformingType.Level || type == TerraformingType.Slope) && brush.m_Strength < 0f) return false;
                    if (type == TerraformingType.Soften && brush.m_Strength < 0f) brush.m_Strength = math.abs(brush.m_Strength) * 2f;
                    brush.m_Strength = BrushStrengthCompensation.Compensate(brush.m_Strength, s.DeltaTime, UnityEngine.Time.unscaledDeltaTime);
                    if (brush.m_Strength == 0f) return false;
                    area = ToolUtils.GetBounds(brush);
                    _terrain.ApplyBrush(type, area, brush, prefab.m_Texture);
                    height = true;
                    _stampsReplayed++;
                    return true;
                }
                case TerraformingTarget.Material:
                    _material.ApplyBrush(brush, brushPrefab);
                    _stampsReplayed++;
                    return true;
                case TerraformingTarget.None:
                    return false;
                default:
                    // w2/simvals: resource brushes (ore, oil, fertility, groundwater) write CPU cell maps; CellMapSync
                    // sends the painted cells as patches and the host streams the merged map tiles
                    return false;
            }
        }

        private void ClientApplyTiles(ByteReader r, double now)
        {
            int n = TerrainMessage.ReadTilesHeader(r, out TileGridInfo info);
            if (!_gpuSupported || !EnsureGrid()) return;
            if (!info.Equals(GridInfo))
            {
                WarnOnce("grid", "the host's heightmap grid " + info + " differs from ours " + GridInfo + "; terrain tiles ignored");
                return;
            }
            bool defer = now < _localStrokeUntil;
            bool any = false;
            Bounds2 union = default(Bounds2);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    int count = TerrainMessage.ReadTile(r, _grid, _tileBuffer, out TileId tile, out _);
                    int index = _grid.IndexOf(tile);
                    if (defer)
                    {
                        var copy = new ushort[count];
                        Array.Copy(_tileBuffer, copy, count);
                        _deferred[index] = copy;
                        continue;
                    }
                    _deferred.Remove(index); // superseded
                    if (!Upload(tile, _tileBuffer, count)) return;
                    Bounds2 b = TileBounds(tile);
                    union = any ? union | b : b;
                    any = true;
                }
                TerrainMessage.EnsureEnd(r);
            }
            finally
            {
                // w8/robust: tiles uploaded before a malformed one still get their water and min/max refresh
                if (any) AfterHeightWrite(union);
            }
        }

        private void FlushDeferred(double now)
        {
            if (_deferred.Count == 0 || now < _localStrokeUntil || _grid == null) return;
            bool any = false;
            Bounds2 union = default(Bounds2);
            foreach (KeyValuePair<int, ushort[]> e in _deferred)
            {
                TileId tile = _grid.TileAt(e.Key);
                if (!Upload(tile, e.Value, e.Value.Length)) break;
                Bounds2 b = TileBounds(tile);
                union = any ? union | b : b;
                any = true;
            }
            _deferred.Clear();
            if (any) AfterHeightWrite(union);
        }

        /// <summary>Writes one tile into the heightmap; false (logged, tiles disabled) when the GPU refuses.</summary>
        private bool Upload(TileId tile, ushort[] texels, int count)
        {
            Texture hm = _terrain.heightmap;
            if (hm == null) return false;
            _grid.GetRect(tile, out int x, out int y, out int w, out int h);
            if (count != w * h) return false;
            try
            {
                if (_uploads == null) _uploads = new UploadTextures(GraphicsFormat.R16_UNorm);
                Texture2D tex = _uploads.Get(w, h);
                if (texels.Length == count)
                {
                    tex.SetPixelData(texels, 0);
                }
                else
                {
                    var exact = new ushort[count];
                    Array.Copy(texels, exact, count);
                    tex.SetPixelData(exact, 0);
                }
                tex.Apply(false, false);
                Graphics.CopyTexture(tex, 0, 0, 0, 0, w, h, hm, 0, 0, x, y);
                _tilesApplied++;
                return true;
            }
            catch (Exception ex)
            {
                _gpuSupported = false;
                _log.Error(LogCategory.Fields, "writing a terrain tile failed; terrain tiles disabled for this session", ex);
                return false;
            }
        }

        /// <summary>The invalidation a brush does after writing the heightmap (<c>TerrainSystem.ApplyBrush</c>, R07 §5.2).</summary>
        private void AfterHeightWrite(Bounds2 area)
        {
            try
            {
                _water?.TerrainWillChangeFromBrush(area);
                _terrain.OnAreaChanged(in area);
                _terrain.UpdateMinMax(default(Brush), area);
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Terrain, "terrain invalidation after a tile write", ex);
            }
        }

        private Bounds2 TileBounds(TileId tile)
        {
            _grid.GetRect(tile, out int x, out int y, out int w, out int h);
            float2 offset = _terrain.playableOffset, size = _terrain.playableArea;
            var inv = new float2(1f / _grid.Width, 1f / _grid.Height);
            return new Bounds2(offset + new float2(x, y) * inv * size, offset + new float2(x + w, y + h) * inv * size);
        }

        private void ClientVerify(ByteReader r, double now)
        {
            var hashes = new List<KeyValuePair<TileId, ulong>>();
            TileGridInfo info = TerrainMessage.ReadManifest(r, hashes);
            if (!_gpuSupported || !EnsureGrid() || !info.Equals(GridInfo)) return;
            // replayed stamps are close but not bit-exact until the host's tiles arrive: no audit meanwhile
            if (_verifyJobs > 0 || now < _localStrokeUntil || now - _remoteStampAt < VerifyQuietSeconds || _deferred.Count > 0 || hashes.Count == 0) return;
            var expected = new Dictionary<int, ulong>(hashes.Count);
            var tiles = new List<int>(hashes.Count);
            foreach (KeyValuePair<TileId, ulong> e in hashes)
            {
                int index = _grid.IndexOf(e.Key);
                expected[index] = e.Value;
                tiles.Add(index);
            }
            _verifyJobs += StartReadbacks(tiles, new JobTag { Kind = JobKind.Verify, Expected = expected });
        }

        private void ClientRequestMismatches(double now)
        {
            if (_mismatches.Count == 0 || _verifyJobs > 0) return;
            if (now < _localStrokeUntil)
            {
                _mismatches.Clear(); // our own stroke explains them; the host's tiles follow
                return;
            }
            if (_mismatches.Count > MaxTilesPerRequest) _mismatches.RemoveRange(MaxTilesPerRequest, _mismatches.Count - MaxTilesPerRequest);
            _small.Reset();
            TerrainMessage.WriteTileRequest(_small, GridInfo, _mismatches);
            _sessions.Client.SendToHost(MessageKind.Terrain, _small.Buffer, 0, _small.Length);
            _log.Info(LogCategory.Fields, "terrain audit: " + _mismatches.Count + " tiles differ from the host; requested");
            _mismatches.Clear();
        }

        // ================================================================== host: flush, replies, audit

        private void HostFlush(double now)
        {
            if (_settle.TryTakeSettled(now, out _, out _, out _, out _)) _flushWanted = true;
            if (!_flushWanted) return;
            if (_dirty.Count == 0 || !_gpuSupported || !EnsureGrid())
            {
                _flushWanted = false;
                return;
            }
            if (_inflight.Count >= MaxInflight) return;
            var tiles = new List<int>(Math.Min(_dirty.Count, MaxTilesPerFlush));
            foreach (int index in _dirty)
            {
                tiles.Add(index);
                if (tiles.Count >= MaxTilesPerFlush) break;
            }
            foreach (int index in tiles) _dirty.Remove(index);
            if (_dirty.Count == 0) _flushWanted = false;
            StartReadbacks(tiles, new JobTag { Kind = JobKind.Broadcast });
            Bounds2 union = TileBounds(_grid.TileAt(tiles[0]));
            foreach (int index in tiles) union |= TileBounds(_grid.TileAt(index));
            try
            {
                HeightsChanged?.Invoke(union);
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Water, "a terrain change listener", ex);
            }
        }

        private void HostQueueRequest(PeerId peer, ByteReader r, double now)
        {
            var tiles = new List<TileId>();
            TileGridInfo info = TerrainMessage.ReadTileRequest(r, tiles);
            if (!_gpuSupported || !EnsureGrid() || !info.Equals(GridInfo)) return;
            if (!_requestBudget.TryGetValue(peer.Value, out (double Start, int Count) budget) || now - budget.Start > RequestWindow) budget = (now, 0);
            int allowed = Math.Max(0, MaxRequestedTilesPerWindow - budget.Count);
            if (allowed < tiles.Count) _log.Info(LogCategory.Fields, peer + " requested " + tiles.Count + " terrain tiles; " + allowed + " allowed in this window");
            if (!_requests.TryGetValue(peer.Value, out HashSet<int> set)) _requests[peer.Value] = set = new HashSet<int>();
            int taken = 0;
            foreach (TileId tile in tiles)
            {
                if (taken >= allowed) break;
                if (set.Add(_grid.IndexOf(tile))) taken++;
            }
            _requestBudget[peer.Value] = (budget.Start, budget.Count + taken);
        }

        private void HostReplies()
        {
            if (_requests.Count == 0 || _inflight.Count >= MaxInflight || _grid == null) return;
            foreach (KeyValuePair<byte, HashSet<int>> e in _requests)
            {
                if (e.Value.Count == 0) continue;
                var tiles = new List<int>(e.Value);
                e.Value.Clear();
                StartReadbacks(tiles, new JobTag { Kind = JobKind.Reply, Peer = new PeerId(e.Key) });
                return; // one peer per frame
            }
        }

        private void HostAudit(double now)
        {
            if (now < _nextAudit) return;
            _nextAudit = now + AuditInterval;
            if (!_gpuSupported || _sessions.Host.LivePeerCount == 0 || _inflight.Count >= MaxInflight || !EnsureGrid()) return;
            if (_auditRow >= _grid.TilesY) _auditRow = 0;
            var tiles = new List<int>(_grid.TilesX);
            for (int x = 0; x < _grid.TilesX; x++) tiles.Add(_auditRow * _grid.TilesX + x);
            _auditRow++;
            StartReadbacks(tiles, new JobTag { Kind = JobKind.Audit });
        }

        // ================================================================== readbacks

        /// <summary>Queues readbacks for the tiles, one per run of adjacent tiles in a tile row; returns the job count.</summary>
        private int StartReadbacks(List<int> tiles, JobTag tag)
        {
            Texture hm = _terrain.heightmap;
            if (hm == null || tiles.Count == 0) return 0;
            tiles.Sort();
            int jobs = 0;
            int i = 0;
            while (i < tiles.Count)
            {
                int start = tiles[i];
                int run = 1;
                while (i + run < tiles.Count && tiles[i + run] == start + run && (start + run) % _grid.TilesX != 0) run++;
                TileId first = _grid.TileAt(start), last = _grid.TileAt(start + run - 1);
                _grid.GetRect(first, out int x, out int y, out _, out int h);
                _grid.GetRect(last, out int lx, out _, out int lw, out _);
                GpuReadback<ushort> job = GpuReadback<ushort>.Start(hm, x, y, lx + lw - x, h, _generation, tag, _log);
                if (job != null)
                {
                    _inflight.Add(job);
                    jobs++;
                }
                i += run;
            }
            return jobs;
        }

        private void ProcessReadbacks(double now)
        {
            if (_inflight.Count == 0) return;
            TileOutbox broadcast = null;
            TileOutbox reply = null;
            PeerId replyPeer = PeerId.Host;
            for (int j = 0; j < _inflight.Count; j++)
            {
                GpuReadback<ushort> job = _inflight[j];
                if (!job.Done) continue;
                _inflight.RemoveAt(j--);
                var tag = (JobTag)job.Tag;
                if (tag.Kind == JobKind.Verify) _verifyJobs = Math.Max(0, _verifyJobs - 1);
                if (job.Generation != _generation || job.Failed || job.Data == null || _grid == null) continue;
                if (job.Data.Length < job.Width * job.Height) continue;
                List<KeyValuePair<TileId, ulong>> manifest = tag.Kind == JobKind.Audit ? new List<KeyValuePair<TileId, ulong>>() : null;
                if (tag.Kind == JobKind.Reply && reply != null && tag.Peer != replyPeer)
                {
                    SendOutbox(reply, replyPeer);
                    reply = null;
                }
                int tx0 = job.X / TileSize, tx1 = (job.X + job.Width - 1) / TileSize;
                int ty = job.Y / TileSize;
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    var tile = new TileId((ushort)tx, (ushort)ty);
                    if (!_grid.Contains(tile)) continue;
                    int count = ExtractTile(job, tile);
                    int index = _grid.IndexOf(tile);
                    switch (tag.Kind)
                    {
                        case JobKind.Broadcast:
                        {
                            ulong hash = HeightTileCodec.Hash(_tileBuffer, count);
                            if (_sentHash.TryGetValue(index, out ulong sent) && sent == hash) break;
                            if (broadcast == null) broadcast = new TileOutbox();
                            _sentHash[index] = AddToOutbox(broadcast, tile, count, PeerId.Host, true);
                            break;
                        }
                        case JobKind.Reply:
                            if (reply == null)
                            {
                                reply = new TileOutbox();
                                replyPeer = tag.Peer;
                            }
                            AddToOutbox(reply, tile, count, replyPeer, false);
                            break;
                        case JobKind.Audit:
                            if (_dirty.Contains(index)) break; // a flush of this tile follows anyway
                            manifest.Add(new KeyValuePair<TileId, ulong>(tile, HeightTileCodec.Hash(_tileBuffer, count)));
                            break;
                        case JobKind.Verify:
                            if (tag.Expected != null && tag.Expected.TryGetValue(index, out ulong expected)
                                && expected != HeightTileCodec.Hash(_tileBuffer, count) && !_deferred.ContainsKey(index))
                            {
                                _mismatches.Add(tile);
                            }
                            break;
                    }
                }
                if (manifest != null && manifest.Count > 0)
                {
                    // tiles completed earlier in this frame go out first, so the manifest never precedes them
                    if (broadcast != null)
                    {
                        SendOutbox(broadcast, PeerId.Host, true);
                        broadcast = null;
                    }
                    _small.Reset();
                    TerrainMessage.WriteManifest(_small, GridInfo, manifest);
                    _sessions.Host?.Broadcast(MessageKind.Terrain, _small.Buffer, 0, _small.Length);
                }
            }
            if (broadcast != null) SendOutbox(broadcast, PeerId.Host, true);
            if (reply != null) SendOutbox(reply, replyPeer);
        }

        /// <summary>Copies one tile out of a readback (row stride = the job's width) into <see cref="_tileBuffer"/>.</summary>
        private int ExtractTile(GpuReadback<ushort> job, TileId tile)
        {
            _grid.GetRect(tile, out int x, out int y, out int w, out int h);
            int count = w * h;
            if (_tileBuffer.Length < count) _tileBuffer = new ushort[count];
            for (int row = 0; row < h; row++)
            {
                Array.Copy(job.Data, (y - job.Y + row) * job.Width + (x - job.X), _tileBuffer, row * w, w);
            }
            return count;
        }

        private ulong AddToOutbox(TileOutbox box, TileId tile, int count, PeerId peer, bool broadcast)
        {
            if (box.CountAt < 0) box.CountAt = TerrainMessage.BeginTiles(box.Writer, GridInfo);
            ulong hash = TerrainMessage.AddTile(box.Writer, tile, _tileBuffer, count);
            box.Count++;
            if (box.Count >= MaxTilesPerMessage || box.Writer.Length >= MaxBytesPerMessage) SendOutbox(box, peer, broadcast);
            return hash;
        }

        private void SendOutbox(TileOutbox box, PeerId peer, bool broadcast = false)
        {
            if (box.CountAt < 0 || box.Count == 0) return;
            TerrainMessage.EndTiles(box.Writer, box.CountAt, box.Count);
            SessionHost host = _sessions.Host;
            if (host != null)
            {
                if (broadcast) host.Broadcast(MessageKind.Terrain, box.Writer.Buffer, 0, box.Writer.Length);
                else host.SendTo(peer, MessageKind.Terrain, box.Writer.Buffer, 0, box.Writer.Length);
                _tilesSent += box.Count;
            }
            box.Writer.Reset();
            box.CountAt = -1;
            box.Count = 0;
        }

        private void WarnOnce(string key, string message)
        {
            if (_warned.Add(key)) _log.Warn(LogCategory.Fields, message);
        }
    }
}
