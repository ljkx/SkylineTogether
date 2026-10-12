using System;
using System.Collections.Generic;
using System.Diagnostics;
using Colossal.Mathematics;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.UI.InGame;
using SkylineTogether.Core;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Scheduling;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Session;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// Attention-driven simulation values (ARCH D5, §4.7.9, §4.8, playable subset). Clients run no gameplay
    /// simulation, so without this the values on structure entities (building condition, efficiency, consumers,
    /// stored resources, service facilities, road traffic and condition, land value, coverage, area amounts) stay at
    /// their join-snapshot state and info panels, infoviews and tooltips go stale.
    /// <para>
    /// <b>Host.</b> A time-sliced scan (about 1 ms per frame) hashes the simulation values of buildings, extensions,
    /// edges and areas (<see cref="EntityCodec.HashSimValues"/>); entities around a client's camera and the selected
    /// entities are rescanned first, everything else on a rolling cursor. A changed hash marks the entity dirty for
    /// every live peer (<see cref="AttentionPlanner"/>). Each frame every peer gets its dirty entities within its byte
    /// budget (<see cref="PeerByteBudget"/>, never while its link is backlogged): in-view first, then the rolling
    /// refresh, oldest change first. The camera comes from the existing view report (<c>MessageKind.View</c>), which
    /// this class extends with the selected entity and the active infoview's topics (<see cref="FillViewReport"/>).
    /// </para>
    /// <para>
    /// <b>Time-aligned inspection (K7).</b> Whenever the host's frame index crosses a multiple of 256, the selected
    /// entity of every peer (plus its installed upgrades) is sampled and sent stamped with that tick, and the host
    /// refreshes its own selected-info panel. A new selection gets one immediate sample. Between samples the
    /// selected entity is withheld from the attention stream. Clients hold a sample until their follower clock reaches
    /// the tick, write it and call <c>SelectedInfoUISystem.RequestUpdate()</c>.
    /// </para>
    /// Values travel on <see cref="MessageKind.SimValues"/> (never in the structure stream), are written without update
    /// tags (nothing re-derives, nothing is proposed back) and never touch structure types, so the integrity check of
    /// structure is unaffected. Main thread only; every entry point catches and logs.
    /// </summary>
    public sealed class AttentionValues
    {
        private const double ScanBudgetMs = 1.0;
        private const double HotShare = 0.6;
        private const double ViewInterval = 1.0;
        private const float MinRadius = 250f, MaxRadius = 2000f, RadiusPerZoom = 1.25f, TopicRadiusFactor = 1.5f;
        private const int BytesPerSecond = 24_000, BurstBytes = 48_000;
        private const long MaxBacklogBytes = 384 * 1024;
        private const int MaxMessageBytes = 32 * 1024;
        private const int MaxInboxPerFrame = 32;
        private const int MaxHeldValueMessages = 512; // final review: held behind the structure backlog at most this many
        private const float GridCell = 128f;

        private sealed class HostPeer
        {
            public PeerId Peer;
            public readonly ViewReport Report = new ViewReport();
            public bool HasReport;
            public uint Selected;
            public bool ImmediateSample;
            public AttentionTopic Topics;
            public double NextView;
            public float ViewX, ViewZ, ViewRadius;
            public readonly PeerByteBudget Budget = new PeerByteBudget(BytesPerSecond, BurstBytes, MaxBacklogBytes);
            public readonly HashSet<uint> View = new HashSet<uint>();
        }

        private readonly SessionManager _sessions;
        private readonly ILog _log;
        private TypeHandler _trafficLightsHandler; // w3/audit
        private EntityCodec _codec;
        private NetIdSystem _ids;
        private SimulationSystem _simulation;
        private SelectedInfoUISystem _selectedInfo;
        private ToolSystem _tools;

        // host
        private readonly AttentionPlanner _planner = new AttentionPlanner();
        private readonly SpatialHashGrid _grid = new SpatialHashGrid(GridCell);
        private readonly Dictionary<uint, ulong> _hashes = new Dictionary<uint, ulong>();
        private readonly Dictionary<byte, HostPeer> _peers = new Dictionary<byte, HostPeer>();
        private readonly Dictionary<byte, long> _joinStamps = new Dictionary<byte, long>();
        private readonly Dictionary<EntityArchetype, bool> _candidate = new Dictionary<EntityArchetype, bool>();
        private readonly List<uint> _scanIds = new List<uint>();
        private readonly List<uint> _hotIds = new List<uint>();
        private readonly Stopwatch _watch = new Stopwatch();
        private readonly ByteWriter _out = new ByteWriter(64 * 1024);
        private readonly List<PeerId> _peerScratch = new List<PeerId>();
        private int _scanCursor, _hotCursor;
        private double _nextRebuild;
        private bool _hotDirty;
        private uint _lastFrame;
        private bool _frameInit;
        private double _lastSend = -1;
        private long _changes, _samplesSent, _bytesSent;

        // client
        private readonly Queue<byte[]> _inbox = new Queue<byte[]>();
        private readonly InspectionSampleQueue<byte[]> _samples = new InspectionSampleQueue<byte[]>();
        private InfoviewPrefab _infoview;
        private uint _infomodeMask;
        private long _valuesApplied, _samplesApplied;
        private bool _warnedApply;

        public AttentionValues(SessionManager sessions, ILog log)
        {
            _sessions = sessions;
            _log = log;
            Instance = this;
            sessions.HostStarted += OnHostStarted;
            sessions.ClientStarted += OnClientStarted;
            sessions.SessionEnded += Reset;
        }

        /// <summary>The instance created by <see cref="SyncController"/> (the view report hook in <see cref="MoverClient"/>).</summary>
        public static AttentionValues Instance { get; private set; }

        private bool IsHost => _sessions.Host != null;

        private bool IsLiveClient => _sessions.Client != null && _sessions.Client.State == ClientState.Live;

        private static double Now => UnityEngine.Time.realtimeSinceStartupAsDouble;

        /// <summary>Short diagnostics for the session panel ("" when idle).</summary>
        public string Status
        {
            get
            {
                if (IsHost && _hashes.Count > 0) return "values: " + _hashes.Count + " scanned, " + _changes + " changes, " + (_bytesSent / 1024) + " KiB sent, " + _samplesSent + " samples";
                if (IsLiveClient && _valuesApplied + _samplesApplied > 0) return "values: " + _valuesApplied + " applied, " + _samplesApplied + " samples";
                return "";
            }
        }

        /// <summary>Host: the attention topics of a peer's active infoview and panels (none before its first view report).</summary>
        public AttentionTopic PeerTopics(PeerId peer) => _peers.TryGetValue(peer.Value, out HostPeer p) ? p.Topics : AttentionTopic.None;

        /// <summary>w4/flow: host, true when the entity is in the peer's current attention view (camera ring).</summary>
        public bool InView(PeerId peer, uint netId) => netId != 0 && _peers.TryGetValue(peer.Value, out HostPeer p) && p.View.Contains(netId);

        /// <summary>w4/flow: client, the attention topics of this player's active infoview (none without one).</summary>
        public AttentionTopic LocalTopics => _infoview != null ? (AttentionTopic)_infomodeMask & TopicMap.AllTopics : AttentionTopic.None;

        /// <summary>Called by <see cref="SyncController"/> once its codec exists.</summary>
        public void Attach(EntityCodec codec, NetIdSystem ids)
        {
            _codec = codec;
            _ids = ids;
            World world = codec.World;
            _simulation = world.GetOrCreateSystemManaged<SimulationSystem>();
            _selectedInfo = world.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            _tools = world.GetOrCreateSystemManaged<ToolSystem>();
        }

        // ================================================================== lifecycle

        private void OnHostStarted(SessionHost host)
        {
            Reset();
            host.AppMessage += OnHostMessage;
            host.PeerJoining += peer => _joinStamps[peer.Value] = _planner.CurrentStamp;
            host.PeerLive += OnPeerLive;
            host.PeerLeft += peer =>
            {
                _peers.Remove(peer.Value);
                _joinStamps.Remove(peer.Value);
                _planner.RemovePeer(peer);
                _hotDirty = true;
            };
        }

        private void OnClientStarted(SessionClient client)
        {
            Reset();
            client.AppMessage += e =>
            {
                if (e.Kind != MessageKind.SimValues) return;
                var copy = new byte[e.BodyCount];
                Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                _inbox.Enqueue(copy);
            };
        }

        private void Reset()
        {
            _planner.Clear();
            _grid.Clear();
            _hashes.Clear();
            _peers.Clear();
            _joinStamps.Clear();
            _candidate.Clear();
            _scanIds.Clear();
            _hotIds.Clear();
            _scanCursor = _hotCursor = 0;
            _nextRebuild = 0;
            _hotDirty = false;
            _frameInit = false;
            _lastSend = -1;
            _changes = _samplesSent = _bytesSent = 0;
            _inbox.Clear();
            _samples.Clear();
            _infoview = null;
            _infomodeMask = 0;
            _valuesApplied = _samplesApplied = 0;
        }

        private void OnPeerLive(PeerId peer)
        {
            if (!_joinStamps.TryGetValue(peer.Value, out long since)) since = _planner.CurrentStamp;
            _planner.AddPeer(peer, since);
            if (!_peers.TryGetValue(peer.Value, out HostPeer p)) _peers[peer.Value] = p = new HostPeer { Peer = peer };
            _planner.SetSelected(peer, p.Selected);
            _log.Info(LogCategory.Replication, peer + ": simulation values stream started (" + _planner.DirtyCount(peer) + " entities changed since its snapshot)");
        }

        /// <summary>Host: the view report the movers branch already sends (camera), plus selection and infoview topics.</summary>
        private void OnHostMessage(PeerId peer, Envelope e)
        {
            if (e.Kind != MessageKind.View) return;
            try
            {
                if (!_peers.TryGetValue(peer.Value, out HostPeer p)) _peers[peer.Value] = p = new HostPeer { Peer = peer };
                ViewReport.ReadInto(new ByteReader(e.Body, e.BodyOffset, e.BodyCount), p.Report);
                bool first = !p.HasReport;
                p.HasReport = true;
                p.Topics = TopicMap.FromView(p.Report);
                uint selected = p.Report.Selected.Value;
                if (selected != p.Selected)
                {
                    p.Selected = selected;
                    p.ImmediateSample = selected != 0;
                    _planner.SetSelected(peer, selected);
                    _hotDirty = true;
                }
                if (first) p.NextView = 0;
            }
            catch (DecodeException ex)
            {
                _log.Debug(LogCategory.Replication, peer + ": malformed view report ignored: " + ex.Message);
            }
        }

        // ================================================================== host: frame end

        /// <summary>Host frame end (after the structure capture): scan, views, samples, per-peer streams.</summary>
        public void CaptureOutgoing(double now)
        {
            if (_codec == null || !IsHost) return;
            try
            {
                Scan();
                UpdateViews(now);
                Samples();
                Stream(now);
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Replication, "simulation value capture failed", ex);
            }
        }

        private void Scan()
        {
            EntityManager em = _codec.EntityManager;
            if (_hotDirty) RebuildHot();
            _watch.Restart();
            // alone, the host only keeps baselines current (a joiner then needs only what changed after its snapshot)
            double budget = _planner.PeerCount > 0 ? ScanBudgetMs : ScanBudgetMs * 0.35;
            double hotBudget = _peers.Count > 0 ? budget * HotShare : 0;
            int hotLeft = _hotIds.Count;
            while (hotLeft-- > 0 && _watch.Elapsed.TotalMilliseconds < hotBudget)
            {
                if (_hotCursor >= _hotIds.Count) _hotCursor = 0;
                ScanOne(em, _hotIds[_hotCursor++]);
            }
            if (_scanCursor >= _scanIds.Count)
            {
                // a full pass is done: rebuild the id list (at most once a second, the table walk is not free)
                double now = Now;
                if (now < _nextRebuild) return;
                _nextRebuild = now + 1.0;
                RebuildScanList();
            }
            while (_scanCursor < _scanIds.Count && _watch.Elapsed.TotalMilliseconds < budget) ScanOne(em, _scanIds[_scanCursor++]);
        }

        private void RebuildScanList()
        {
            _scanCursor = 0;
            _scanIds.Clear();
            NativeArray<Entity> table = _ids.ReaderTable;
            for (int id = 1; id < table.Length; id++)
            {
                if (table[id] == Entity.Null) continue;
                Family f = _ids.GetFamily((uint)id);
                // w3/audit: nodes as well (traffic light phases, node wear)
                if (f == Family.Object || f == Family.NetEdge || f == Family.Area || f == Family.NetNode) _scanIds.Add((uint)id);
            }
            // entities unbound since the last pass
            if (_hashes.Count > _scanIds.Count + 1024)
            {
                var live = new HashSet<uint>(_scanIds);
                var gone = new List<uint>();
                foreach (uint id in _hashes.Keys)
                {
                    if (!live.Contains(id)) gone.Add(id);
                }
                foreach (uint id in gone) Forget(id);
            }
        }

        private void RebuildHot()
        {
            _hotDirty = false;
            var set = new HashSet<uint>();
            foreach (HostPeer p in _peers.Values)
            {
                set.UnionWith(p.View);
                if (p.Selected != 0) set.Add(p.Selected);
            }
            _hotIds.Clear();
            _hotIds.AddRange(set);
            _hotIds.Sort();
            _hotCursor = 0;
        }

        private void Forget(uint id)
        {
            if (_hashes.Remove(id)) _planner.Forget(id);
            _grid.Remove(id);
        }

        private void ScanOne(EntityManager em, uint id)
        {
            Entity e = _ids.GetEntity(id);
            if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e))
            {
                Forget(id);
                return;
            }
            Family family = _ids.GetFamily(id);
            if (!IsCandidate(em, e, family)) return;
            UpdatePosition(em, e, id, family);
            ulong hash = family == Family.NetNode ? NodeHash(em, e) : _codec.HashSimValues(e, family);
            if (_hashes.TryGetValue(id, out ulong old))
            {
                if (old == hash) return;
                _hashes[id] = hash;
                if (hash != 0)
                {
                    _planner.MarkChanged(id);
                    _changes++;
                }
                return;
            }
            _hashes[id] = hash; // first observation: the baseline every peer's snapshot already holds
        }

        /// <summary>Buildings, extensions and upgrades among objects (not trees, props, decals), every edge and area.</summary>
        private bool IsCandidate(EntityManager em, Entity e, Family family)
        {
            if (family == Family.NetEdge || family == Family.Area || family == Family.NetNode) return true;
            if (family != Family.Object) return false;
            EntityArchetype archetype = em.GetChunk(e).Archetype;
            if (_candidate.TryGetValue(archetype, out bool yes)) return yes;
            yes = false;
            NativeArray<ComponentType> types = archetype.GetComponentTypes(Allocator.Temp);
            try
            {
                foreach (ComponentType t in types)
                {
                    if (t.TypeIndex == ComponentType.ReadOnly<Game.Buildings.Building>().TypeIndex
                        || t.TypeIndex == ComponentType.ReadOnly<Game.Buildings.Extension>().TypeIndex)
                    {
                        yes = true;
                        break;
                    }
                }
            }
            finally
            {
                types.Dispose();
            }
            _candidate[archetype] = yes;
            return yes;
        }

        private void UpdatePosition(EntityManager em, Entity e, uint id, Family family)
        {
            if (family == Family.Object && em.HasComponent<Game.Objects.Transform>(e))
            {
                float3 p = em.GetComponentData<Game.Objects.Transform>(e).m_Position;
                _grid.Set(id, p.x, p.z);
            }
            else if (family == Family.NetEdge && em.HasComponent<Game.Net.Curve>(e))
            {
                float3 p = MathUtils.Position(em.GetComponentData<Game.Net.Curve>(e).m_Bezier, 0.5f);
                _grid.Set(id, p.x, p.z);
            }
            else if (family == Family.NetNode && em.HasComponent<Game.Net.Node>(e))
            {
                float3 p = em.GetComponentData<Game.Net.Node>(e).m_Position;
                _grid.Set(id, p.x, p.z);
            }
        }

        /// <summary>
        /// w3/audit: a node's value hash. Traffic lights count by their phase (state, flags, signal groups), not by the
        /// timer that ticks every update, so a junction is sent when its lights change; clients derive lane signals and
        /// signal heads from the phase (<see cref="SignalFollower"/>).
        /// </summary>
        private ulong NodeHash(EntityManager em, Entity e)
        {
            if (_trafficLightsHandler == null) _trafficLightsHandler = TypeHandlers.Get<Game.Net.TrafficLights>();
            if (_trafficLightsHandler == null || !em.HasComponent<Game.Net.TrafficLights>(e)) return _codec.HashSimValues(e, Family.NetNode);
            ulong hash = _codec.HashSimValues(e, Family.NetNode, _trafficLightsHandler);
            Game.Net.TrafficLights t = em.GetComponentData<Game.Net.TrafficLights>(e);
            ulong phase = (ulong)(byte)t.m_State | ((ulong)(byte)t.m_Flags << 8) | ((ulong)t.m_SignalGroupCount << 16) | ((ulong)t.m_CurrentSignalGroup << 24) | ((ulong)t.m_NextSignalGroup << 32);
            return (hash ^ (phase * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL)) | 1UL;
        }

        private void UpdateViews(double now)
        {
            foreach (HostPeer p in _peers.Values)
            {
                if (!p.HasReport || !_planner.HasPeer(p.Peer)) continue;
                ViewReport r = p.Report;
                float zoom = r.Zoom > 0f ? r.Zoom : MinRadius;
                float radius = Math.Max(MinRadius, Math.Min(MaxRadius, zoom * RadiusPerZoom));
                if (p.Topics != AttentionTopic.None) radius = Math.Min(MaxRadius * TopicRadiusFactor, radius * TopicRadiusFactor);
                float dx = r.PivotX - p.ViewX, dz = r.PivotZ - p.ViewZ;
                bool moved = dx * dx + dz * dz > p.ViewRadius * p.ViewRadius * 0.25f || Math.Abs(radius - p.ViewRadius) > p.ViewRadius * 0.25f;
                if (now < p.NextView && !moved) continue;
                p.NextView = now + ViewInterval;
                p.ViewX = r.PivotX;
                p.ViewZ = r.PivotZ;
                p.ViewRadius = radius;
                p.View.Clear();
                _grid.Query(r.PivotX, r.PivotZ, radius, p.View);
                _planner.SetView(p.Peer, p.View);
                _hotDirty = true;
            }
        }

        // ------------------------------------------------------------------ inspection samples (K7)

        private void Samples()
        {
            uint frame = _simulation.frameIndex;
            bool crossed = false;
            uint tick = 0;
            if (!_frameInit)
            {
                _frameInit = true;
            }
            else
            {
                crossed = InspectionCadence.Crossed(_lastFrame, frame, out tick);
            }
            _lastFrame = frame;
            if (crossed)
            {
                // the host's own panel refreshes on the same phase as every client's (ARCH §4.7.9)
                try
                {
                    if (_selectedInfo.selectedEntity != Entity.Null) _selectedInfo.RequestUpdate();
                }
                catch (Exception ex)
                {
                    _log.Debug(LogCategory.Replication, "host panel refresh failed: " + ex.Message);
                }
            }
            foreach (HostPeer p in _peers.Values)
            {
                if (p.Selected == 0 || !_planner.HasPeer(p.Peer)) continue;
                if (crossed) SendSample(p, tick);
                else if (p.ImmediateSample) SendSample(p, frame);
                p.ImmediateSample = false;
            }
        }

        private void SendSample(HostPeer p, uint tick)
        {
            EntityManager em = _codec.EntityManager;
            Entity e = _ids.GetEntity(p.Selected);
            if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) return;
            _out.Reset();
            int countPosition = SimValueMessage.BeginSample(_out, tick, p.Selected);
            int n = 0;
            if (_codec.WriteSimValues(_out, p.Selected, _ids.GetFamily(p.Selected), e) > 0) n++;
            // closure: installed upgrades (bound buildings of their own) belong to the same panel
            if (em.HasBuffer<Game.Buildings.InstalledUpgrade>(e))
            {
                DynamicBuffer<Game.Buildings.InstalledUpgrade> upgrades = em.GetBuffer<Game.Buildings.InstalledUpgrade>(e, true);
                var ids = new List<uint>(upgrades.Length);
                for (int i = 0; i < upgrades.Length; i++) ids.Add(_ids.GetNetId(upgrades[i].m_Upgrade));
                foreach (uint id in ids)
                {
                    Entity u = _ids.GetEntity(id);
                    if (id == 0 || u == Entity.Null || !em.Exists(u)) continue;
                    if (_codec.WriteSimValues(_out, id, _ids.GetFamily(id), u) > 0) n++;
                    if (n >= 64) break;
                }
            }
            SimValueMessage.End(_out, countPosition, n);
            if (n == 0) return;
            _sessions.Host?.SendTo(p.Peer, MessageKind.SimValues, _out.Buffer, 0, _out.Length);
            _samplesSent++;
            _bytesSent += _out.Length;
        }

        // ------------------------------------------------------------------ attention and rolling stream

        private void Stream(double now)
        {
            double dt = _lastSend < 0 ? 0 : Math.Min(1.0, now - _lastSend);
            _lastSend = now;
            SessionHost host = _sessions.Host;
            if (host == null) return;
            _peerScratch.Clear();
            foreach (HostPeer p in _peers.Values) _peerScratch.Add(p.Peer);
            foreach (PeerId peer in _peerScratch)
            {
                if (!_planner.HasPeer(peer) || !_peers.TryGetValue(peer.Value, out HostPeer p)) continue;
                IPeerOutput output = host.GetPeerOutput(peer);
                if (output == null) continue;
                long backlog = output.QueuedBytes + Math.Max(0, output.Stats.PendingReliableBytes);
                p.Budget.Boost = p.Topics != AttentionTopic.None ? 2f : 1f;
                int allowance = p.Budget.Allowance(dt, backlog);
                if (allowance <= 0 || _planner.DirtyCount(peer) == 0) continue;
                while (allowance > 0)
                {
                    _out.Reset();
                    int countPosition = SimValueMessage.BeginValues(_out);
                    int entities = 0;
                    int limit = Math.Min(allowance, MaxMessageBytes);
                    int used = _planner.Drain(peer, limit, id =>
                    {
                        if (_out.Length >= MaxMessageBytes) return AttentionPlanner.NoRoom;
                        int before = _out.Length;
                        if (!WriteEntity(id)) return AttentionPlanner.Gone;
                        entities++;
                        return Math.Max(1, _out.Length - before);
                    });
                    if (entities > 0)
                    {
                        SimValueMessage.End(_out, countPosition, entities);
                        host.SendTo(peer, MessageKind.SimValues, _out.Buffer, 0, _out.Length);
                        _bytesSent += _out.Length;
                    }
                    p.Budget.Spend(used);
                    allowance -= used;
                    if (used <= 0 || entities == 0) break;
                }
            }
        }

        /// <summary>Appends the entity's values to the message being built; false when it is gone or has none.</summary>
        private bool WriteEntity(uint id)
        {
            EntityManager em = _codec.EntityManager;
            Entity e = _ids.GetEntity(id);
            if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) return false;
            return _codec.WriteSimValues(_out, id, _ids.GetFamily(id), e) > 0;
        }

        // ================================================================== client

        /// <summary>
        /// Client view report hook (<see cref="MoverClient"/>, about 10 Hz): the selected entity and the topics of the
        /// active infoview (<see cref="InfomodeTopics"/>). The camera fields are the movers branch's.
        /// </summary>
        public void FillViewReport(ViewReport view, bool forRecorder = false)
        {
            // solo testing: the host fills the test recorder's view report with its own selection (forRecorder)
            if (view == null || _codec == null || (!IsLiveClient && !forRecorder)) return;
            try
            {
                Entity selected = _selectedInfo != null ? _selectedInfo.selectedEntity : Entity.Null;
                uint id = selected == Entity.Null ? 0 : _ids.GetNetId(selected);
                view.Selected = NetSlot.TryCreate(id, out NetSlot slot) ? slot : NetSlot.None;
                InfoviewPrefab infoview = _tools?.activeInfoview;
                if (!ReferenceEquals(infoview, _infoview))
                {
                    _infoview = infoview;
                    _infomodeMask = InfomodeTopics.ToInfomodeMask(TopicsOf(infoview));
                }
                view.Infoview = infoview != null ? (byte)1 : (byte)0;
                view.InfomodeMask = infoview != null ? _infomodeMask : 0;
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Replication, "view report attention fields failed: " + ex.Message);
            }
        }

        /// <summary>The attention topics of every infomode of an infoview (ARCH §4.8.3; over-approximates optional modes).</summary>
        private AttentionTopic TopicsOf(InfoviewPrefab infoview)
        {
            if (infoview == null || _tools == null) return AttentionTopic.None;
            AttentionTopic topics = AttentionTopic.None;
            List<InfomodeInfo> modes = _tools.GetInfomodes(infoview);
            if (modes == null) return topics;
            foreach (InfomodeInfo info in modes)
            {
                InfomodePrefab mode = info?.m_Mode;
                switch (mode)
                {
                    case BuildingStatusInfomodePrefab b:
                        topics |= InfomodeTopics.ForBuildingStatus((int)b.m_Type);
                        break;
                    case BuildingStateInfomodePrefab b:
                        topics |= InfomodeTopics.ForBuildingStatus((int)b.m_Type);
                        break;
                    case NetStatusInfomodePrefab n:
                        topics |= InfomodeTopics.ForNetStatus((int)n.m_Type);
                        break;
                    case HeatmapInfomodePrefab h:
                        topics |= InfomodeTopics.ForHeatmap((int)h.m_Type);
                        break;
                    case ObjectStatusInfomodePrefab o:
                        topics |= InfomodeTopics.ForObjectStatus((int)o.m_Type);
                        break;
                    case ObjectStateInfomodePrefab o:
                        topics |= InfomodeTopics.ForObjectStatus((int)o.m_Type);
                        break;
                    case ServiceCoverageInfomodePrefab _:
                        topics |= InfomodeTopics.ServiceCoverageInfomode;
                        break;
                    case RouteInfomodePrefab _:
                    case TransportStopInfomodePrefab _:
                    case VehicleInfomodePrefab _:
                        topics |= InfomodeTopics.TransportInfomode;
                        break;
                    case ZoneSuitabilityInfomodePrefab _:
                        topics |= InfomodeTopics.ZoneSuitabilityInfomode;
                        break;
                }
            }
            return topics;
        }

        /// <summary>
        /// Client frame start, right after the structure apply (so ids spawned in the same frame resolve): attention
        /// values apply on arrival, inspection samples when the follower clock reaches their tick.
        /// </summary>
        public void ApplyIncoming(int structureBacklog = 0)
        {
            if (_codec == null || !IsLiveClient)
            {
                if (_sessions.Client == null && _inbox.Count > 0) _inbox.Clear();
                return;
            }
            try
            {
                int n = 0;
                // final review: values sent after structure batches that are still queued (budgeted apply after
                // go-live or a burst) were applied first: values of entities not spawned yet were dropped for good and
                // an older Upsert applied afterwards overwrote newer values. They wait like the agent values do.
                bool hold = structureBacklog > 0 && _inbox.Count < MaxHeldValueMessages;
                while (!hold && _inbox.Count > 0 && n++ < MaxInboxPerFrame)
                {
                    byte[] data = _inbox.Dequeue();
                    try
                    {
                        if (data.Length > 1 && data[0] == SimValueMessage.Version && data[1] == SimValueMessage.TypeSample)
                        {
                            List<SyncRecord> header = SimValueMessage.Read(data, 0, data.Length, out _, out uint tick, out _);
                            if (header.Count > 0) _samples.Enqueue(tick, Now, data);
                            continue;
                        }
                        Apply(data, out _);
                        _valuesApplied++;
                    }
                    catch (DecodeException ex)
                    {
                        _log.Warn(LogCategory.Replication, "dropped a malformed simulation value message: " + ex.Message);
                    }
                }
                uint frame = _simulation.frameIndex;
                while (_samples.TryDequeueReady(frame, Now, out byte[] sample, out _))
                {
                    try
                    {
                        uint selected = Apply(sample, out bool isSample);
                        _samplesApplied++;
                        if (!isSample || selected == 0) continue;
                        Entity current = _selectedInfo.selectedEntity;
                        if (current != Entity.Null && _ids.GetNetId(current) == selected) _selectedInfo.RequestUpdate();
                    }
                    catch (DecodeException ex)
                    {
                        _log.Warn(LogCategory.Replication, "dropped a malformed inspection sample: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_warnedApply)
                {
                    _warnedApply = true;
                    _log.Error(LogCategory.Replication, "applying simulation values failed (further failures are logged at debug level)", ex);
                }
                else
                {
                    _log.Debug(LogCategory.Replication, "applying simulation values failed: " + ex.Message);
                }
            }
        }

        /// <summary>Writes one message; returns the sample's selected net id (0 for a values message).</summary>
        private uint Apply(byte[] data, out bool isSample)
        {
            List<SyncRecord> records = SimValueMessage.Read(data, 0, data.Length, out byte type, out _, out uint selected);
            isSample = type == SimValueMessage.TypeSample;
            var result = new ApplyResult();
            _codec.ApplySimValues(data, 0, data.Length, records, result);
            if (result.Problems.Count > 0) _log.Debug(LogCategory.Replication, "simulation values: " + string.Join("; ", result.Problems));
            return isSample ? selected : 0;
        }
    }
}
