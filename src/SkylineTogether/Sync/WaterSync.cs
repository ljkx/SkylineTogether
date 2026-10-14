using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Simulation;
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
    /// Bounded water correction (ARCH §4.10.10, R07 §6). The water simulation is a per-machine GPU simulation stepped
    /// per rendered frame, so it can never be lockstepped; clients are paused followers, whose water would otherwise
    /// stay frozen at the join snapshot. The host therefore samples <c>WaterSystem.WaterTexture</c> (2048² RGBA32F:
    /// depth, velocity, pollution) one 256×256 tile per second with a non-blocking readback, round-robin, with "hot"
    /// tiles near terrain edits and water source changes first (sampled a few times as the water settles). A tile is
    /// sent when its 16×16-texel block averages moved since it was last sent, or as a slow refresh of wet tiles; it is
    /// quantised by <see cref="WaterTileCodec"/> (1 mm depth, half-float velocity, u8 pollution), LZ4-compressed by the
    /// envelope and broadcast on the bulk lane. Clients copy it into their water texture, refresh the flow maps
    /// (<c>ForceUpdateFlow</c>) and briefly disable active-tile culling so newly wet tiles are processed. The host's
    /// sea level travels with every message. Water gameplay (pumping, floods, damage) is host-only and replicates
    /// through ECS, so this affects visuals only. The backdrop (outside the playable area) is not corrected.
    /// Main thread only; fail-safe (every entry point catches and logs; unsupported GPU paths disable it).
    /// </summary>
    public sealed class WaterSync
    {
        private const int TileSize = 256;
        private const int BlockSize = 16;
        private const double SampleInterval = 1.0;
        private const double RefreshSeconds = 120.0;
        private const float BlockDepthThreshold = 0.05f;
        private const int BlockWetThreshold = 3;
        private const float WetDepth = 0.01f;
        private const int MaxMessagesPerFrame = 2;
        private const int MaxHotEntries = 4096;
        private const double CullingOffSeconds = 3.0;
        private const int RoundRobinEvery = 3;
        private static readonly double[] HotDelays = { 3.0, 12.0, 40.0 };

        private readonly SessionManager _sessions;
        private readonly ILog _log;
        private readonly Queue<byte[]> _inbox = new Queue<byte[]>();
        private readonly List<(double Due, int Tile)> _hot = new List<(double, int)>();
        private readonly HashSet<string> _warned = new HashSet<string>();
        private readonly ByteWriter _writer = new ByteWriter(1 << 16);

        private WaterSystem _water;
        private EntityQuery _sourceChanges;
        private bool _worldReady;
        private bool _gpuSupported;
        private TileGrid _grid;
        private int _generation;

        // host
        private GpuReadback<float> _job;
        private bool _jobForced;
        private int _cursor;
        private int _picks;
        private double _nextSample;
        private float[][] _sentStats;
        private double[] _lastSent;

        // client
        private UploadTextures _uploads;
        private float[] _rgba = new float[TileSize * TileSize * WaterTileCodec.ChannelsPerTexel];
        private double _restoreCullingAt;
        private long _tilesSent, _tilesApplied;

        public WaterSync(SessionManager sessions, ILog log)
        {
            _sessions = sessions;
            _log = log;
            sessions.HostStarted += OnHostStarted;
            sessions.ClientStarted += OnClientStarted;
            sessions.SessionEnded += Reset;
        }

        private bool IsHost => _sessions.Host != null;

        private bool IsLiveClient => _sessions.Client != null && _sessions.Client.State == ClientState.Live;

        private double Now => _sessions.Clock.Seconds;

        /// <summary>Short diagnostics for the session panel ("" when idle).</summary>
        public string Status
        {
            get
            {
                if (IsHost && _tilesSent > 0) return "water: " + _tilesSent + " tiles sent";
                if (IsLiveClient && _tilesApplied > 0) return "water: " + _tilesApplied + " tiles applied";
                return "";
            }
        }

        // ================================================================== lifecycle

        private void OnHostStarted(SessionHost host)
        {
            Reset();
        }

        private void OnClientStarted(SessionClient client)
        {
            Reset();
            client.AppMessage += e =>
            {
                if (e.Kind != MessageKind.Water) return;
                // with the feature off nothing drains the inbox (it grew for the rest of the session); the
                // host's next keyframe or a rejoin (which re-arms the feature) brings the state back
                if (!Diagnostics.Features.Water.IsAvailable) return;
                var copy = new byte[e.BodyCount];
                Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                _inbox.Enqueue(copy);
            };
        }

        private void Reset()
        {
            _generation++;
            _inbox.Clear();
            _hot.Clear();
            _job = null;
            _sentStats = null;
            _lastSent = null;
            _cursor = 0;
            _nextSample = 0;
            _tilesSent = _tilesApplied = 0;
            try
            {
                if (_restoreCullingAt > 0 && _water != null) _water.UseActiveCellsCulling = true;
                _uploads?.Dispose();
            }
            catch (Exception)
            {
                // the world may be gone
            }
            _restoreCullingAt = 0;
            _uploads = null;
            if (_worldReady) _gpuSupported = GpuReadback<float>.Supported && UploadTextures.CopySupported;
        }

        private bool EnsureWorld()
        {
            if (_worldReady) return _water != null;
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return false;
            _worldReady = true;
            _water = world.GetOrCreateSystemManaged<WaterSystem>();
            _sourceChanges = world.EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Simulation.WaterSourceData>(), ComponentType.ReadOnly<Game.Objects.Transform>() },
                Any = new[] { ComponentType.ReadOnly<Game.Common.Created>(), ComponentType.ReadOnly<Game.Common.Updated>(), ComponentType.ReadOnly<Game.Common.Deleted>() },
                None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
            _gpuSupported = GpuReadback<float>.Supported && UploadTextures.CopySupported;
            if (!_gpuSupported) _log.Warn(LogCategory.Fields, "water correction disabled: this GPU path lacks async readback or texture copies");
            return true;
        }

        /// <summary>The tile grid of the current water texture; false while the water system is not loaded.</summary>
        private bool EnsureGrid()
        {
            if (!_water.Loaded) return false;
            RenderTexture tex = _water.WaterTexture;
            if (tex == null) return false;
            if (_grid == null || _grid.Width != tex.width || _grid.Height != tex.height)
            {
                _grid = new TileGrid(tex.width, tex.height, TileSize);
                _sentStats = null;
                _lastSent = null;
                _hot.Clear();
                _cursor = 0;
            }
            if (_sentStats == null)
            {
                _sentStats = new float[_grid.TileCount][];
                _lastSent = new double[_grid.TileCount];
                // the join snapshot carried the water: the first refresh of a wet tile is due one period from now
                double start = Now;
                for (int i = 0; i < _lastSent.Length; i++) _lastSent[i] = start;
            }
            return true;
        }

        private TileGridInfo GridInfo => new TileGridInfo(_grid.Width, _grid.Height, _grid.TileSize);

        // ================================================================== host

        /// <summary>
        /// Host: the water over this world area is about to change (terrain edit, source change); its tiles are
        /// sampled and sent unconditionally a few times as the water settles.
        /// </summary>
        public void MarkHot(Bounds2 area)
        {
            if (!IsHost) return;
            try
            {
                if (!EnsureWorld() || !_gpuSupported || !EnsureGrid()) return;
                float cell = WaterSystem.kMapSize / (float)_grid.Width;
                float half = WaterSystem.kMapSize / 2;
                int x0 = (int)math.floor((half + area.min.x) / cell), x1 = (int)math.floor((half + area.max.x) / cell);
                int y0 = (int)math.floor((half + area.min.y) / cell), y1 = (int)math.floor((half + area.max.y) / cell);
                double now = Now;
                foreach (TileId tile in _grid.TilesIn(x0, y0, x1, y1))
                {
                    int index = _grid.IndexOf(tile);
                    foreach (double delay in HotDelays)
                    {
                        double due = now + delay;
                        // a source that is tagged every frame must not flood the schedule: one entry per tile and delay
                        if (_hot.Exists(h => h.Tile == index && Math.Abs(h.Due - due) < 2.0)) continue;
                        _hot.Add((due, index));
                    }
                }
                if (_hot.Count > MaxHotEntries) _hot.RemoveRange(0, _hot.Count - MaxHotEntries);
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Water, "marking water tiles", ex);
            }
        }

        /// <summary>Frame end: host sampling, change detection and sending.</summary>
        public void CaptureOutgoing()
        {
            if (!IsHost) return;
            try
            {
                if (!EnsureWorld() || !_gpuSupported || !EnsureGrid()) return;
                double now = Now;
                DetectSourceChanges();
                if (_job != null && _job.Done) FinishJob(now);
                if (_job != null || now < _nextSample) return;
                _nextSample = now + SampleInterval;
                if (_sessions.Host.LivePeerCount == 0) return;
                int tile = PickTile(now, out _jobForced);
                _grid.GetRect(_grid.TileAt(tile), out int x, out int y, out int w, out int h);
                _job = GpuReadback<float>.Start(_water.WaterTexture, x, y, w, h, _generation, tile, _log);
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Water, "water capture", ex);
            }
        }

        private void DetectSourceChanges()
        {
            if (_sourceChanges.IsEmptyIgnoreFilter) return;
            EntityManager em = _water.EntityManager;
            NativeArray<Entity> sources = _sourceChanges.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in sources)
                {
                    float3 p = em.GetComponentData<Game.Objects.Transform>(e).m_Position;
                    float r = math.clamp(em.GetComponentData<Game.Simulation.WaterSourceData>(e).m_Radius, 50f, WaterSystem.kMaxSourceRadius);
                    MarkHot(new Bounds2(p.xz - r, p.xz + r));
                }
            }
            finally
            {
                sources.Dispose();
            }
        }

        /// <summary>The next tile: the earliest due hot tile, else round-robin (which gets every third sample anyway).</summary>
        private int PickTile(double now, out bool forced)
        {
            int best = -1;
            bool roundRobinTurn = ++_picks % RoundRobinEvery == 0;
            for (int i = 0; i < _hot.Count && !roundRobinTurn; i++)
            {
                if (_hot[i].Due <= now && (best < 0 || _hot[i].Due < _hot[best].Due)) best = i;
            }
            if (best >= 0)
            {
                int tile = _hot[best].Tile;
                _hot.RemoveAll(h => h.Tile == tile && h.Due <= now);
                forced = true;
                return tile;
            }
            forced = false;
            int next = _cursor;
            _cursor = (_cursor + 1) % _grid.TileCount;
            return next;
        }

        private void FinishJob(double now)
        {
            GpuReadback<float> job = _job;
            _job = null;
            if (job.Generation != _generation || job.Failed || job.Data == null) return;
            int tile = (int)job.Tag;
            int texels = job.Width * job.Height;
            if (tile < 0 || tile >= _grid.TileCount || job.Data.Length < texels * WaterTileCodec.ChannelsPerTexel) return;
            float[] stats = BlockStats(job.Data, job.Width, job.Height);
            float[] sent = _sentStats[tile];
            bool send = _jobForced;
            if (!send && sent != null) send = Differs(sent, stats);
            if (!send && (IsWet(stats) || (sent != null && IsWet(sent))) && now - _lastSent[tile] >= RefreshSeconds) send = true;
            if (!send)
            {
                if (sent == null) _sentStats[tile] = stats; // baseline: what the clients loaded with the join snapshot
                return;
            }
            _writer.Reset();
            int countAt = WaterMessage.BeginTiles(_writer, _water.SeaLevel, GridInfo);
            WaterMessage.AddTile(_writer, _grid.TileAt(tile), job.Data, texels, WaterTileLevel.DepthPollutionVelocity);
            WaterMessage.EndTiles(_writer, countAt, 1);
            _sessions.Host.Broadcast(MessageKind.Water, _writer.Buffer, 0, _writer.Length, Lane.Bulk);
            _sentStats[tile] = stats;
            _lastSent[tile] = now;
            _tilesSent++;
        }

        /// <summary>Per 16×16 block: mean depth and wet texel count (interleaved).</summary>
        private static float[] BlockStats(float[] rgba, int width, int height)
        {
            int bx = (width + BlockSize - 1) / BlockSize, by = (height + BlockSize - 1) / BlockSize;
            var stats = new float[bx * by * 2];
            var counts = new int[bx * by];
            for (int y = 0; y < height; y++)
            {
                int row = (y / BlockSize) * bx;
                for (int x = 0; x < width; x++)
                {
                    float depth = rgba[(y * width + x) * WaterTileCodec.ChannelsPerTexel];
                    if (!(depth > 0f)) depth = 0f; // also NaN
                    int b = row + x / BlockSize;
                    stats[b * 2] += depth;
                    if (depth > WetDepth) stats[b * 2 + 1] += 1f;
                    counts[b]++;
                }
            }
            for (int b = 0; b < counts.Length; b++)
            {
                if (counts[b] > 0) stats[b * 2] /= counts[b];
            }
            return stats;
        }

        private static bool Differs(float[] a, float[] b)
        {
            if (a.Length != b.Length) return true;
            for (int i = 0; i < a.Length; i += 2)
            {
                if (math.abs(a[i] - b[i]) > BlockDepthThreshold || math.abs(a[i + 1] - b[i + 1]) >= BlockWetThreshold) return true;
            }
            return false;
        }

        private static bool IsWet(float[] stats)
        {
            for (int i = 1; i < stats.Length; i += 2)
            {
                if (stats[i] > 0f) return true;
            }
            return false;
        }

        // ================================================================== client

        /// <summary>Frame start: client applies received water tiles.</summary>
        public void ApplyIncoming()
        {
            if (!IsLiveClient)
            {
                if (_sessions.Client == null && _inbox.Count > 0) _inbox.Clear();
                return;
            }
            try
            {
                if (!EnsureWorld()) return;
                double now = Now;
                if (_restoreCullingAt > 0 && now >= _restoreCullingAt)
                {
                    _water.UseActiveCellsCulling = true;
                    _restoreCullingAt = 0;
                }
                int n = 0;
                while (_inbox.Count > 0 && n++ < MaxMessagesPerFrame)
                {
                    byte[] data = _inbox.Dequeue();
                    try
                    {
                        ApplyMessage(data, now);
                    }
                    catch (DecodeException ex)
                    {
                        _log.Warn(LogCategory.Fields, "dropped a malformed water message: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Water, "water apply", ex);
            }
        }

        /// <summary>The largest sea level magnitude accepted from the host (heights span 4096 m).</summary>
        private const float MaxSeaLevel = 10000f;

        private void ApplyMessage(byte[] data, double now)
        {
            var r = new ByteReader(data, 0, data.Length);
            int n = WaterMessage.ReadHeader(r, out float seaLevel, out TileGridInfo info);
            if (!_gpuSupported || !EnsureGrid()) return;
            if (!info.Equals(GridInfo))
            {
                if (_warned.Add("grid")) _log.Warn(LogCategory.Fields, "the host's water grid " + info + " differs from ours " + GridInfo + "; water tiles ignored");
                return;
            }
            // only a sea level within the terrain's height range (the GPU simulation takes it as is)
            if (math.abs(_water.SeaLevel - seaLevel) > 0.01f && seaLevel >= -MaxSeaLevel && seaLevel <= MaxSeaLevel)
            {
                _log.Info(LogCategory.Fields, "sea level follows the host: " + _water.SeaLevel + " → " + seaLevel);
                _water.SeaLevel = seaLevel;
            }
            int applied = 0;
            for (int i = 0; i < n; i++)
            {
                int count = WaterMessage.ReadTile(r, _grid, _rgba, out TileId tile);
                if (!Upload(tile, count)) return;
                applied++;
            }
            TerrainMessage.EnsureEnd(r);
            if (applied == 0) return;
            _water.ForceUpdateFlow = true;
            if (_water.UseActiveCellsCulling)
            {
                _water.UseActiveCellsCulling = false;
                _restoreCullingAt = now + CullingOffSeconds;
            }
            else if (_restoreCullingAt > 0)
            {
                _restoreCullingAt = now + CullingOffSeconds;
            }
        }

        private bool Upload(TileId tile, int count)
        {
            RenderTexture target = _water.WaterTexture;
            if (target == null) return false;
            _grid.GetRect(tile, out int x, out int y, out int w, out int h);
            if (count != w * h) return false;
            try
            {
                if (_uploads == null) _uploads = new UploadTextures(GraphicsFormat.R32G32B32A32_SFloat);
                Texture2D tex = _uploads.Get(w, h);
                int floats = count * WaterTileCodec.ChannelsPerTexel;
                if (_rgba.Length == floats)
                {
                    tex.SetPixelData(_rgba, 0);
                }
                else
                {
                    var exact = new float[floats];
                    Array.Copy(_rgba, exact, floats);
                    tex.SetPixelData(exact, 0);
                }
                tex.Apply(false, false);
                Graphics.CopyTexture(tex, 0, 0, 0, 0, w, h, target, 0, 0, x, y);
                _tilesApplied++;
                return true;
            }
            catch (Exception ex)
            {
                _gpuSupported = false;
                _log.Error(LogCategory.Fields, "writing a water tile failed; water correction disabled for this session", ex);
                return false;
            }
        }
    }
}
