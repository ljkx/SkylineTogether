using System;
using System.Collections.Generic;
using System.Diagnostics;
using Game.Simulation;
using SkylineTogether.Core;
using SkylineTogether.Core.Buffers;
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
    /// w4/flow: the flow-status channel of ARCH §4.11.5. The electricity and water flow graphs are derived per peer
    /// (<c>FamilyMarker.FlowGraph</c>, rebuilt by the graph systems in the Modification phases), but their flow values
    /// (<c>ElectricityFlowEdge</c> flow, capacity and Bottleneck/BeyondBottleneck/Disconnected flags, <c>WaterPipeEdge</c>
    /// fresh and sewage flow, capacities, pollution and flags) are written only by the GameSimulation flow systems
    /// (<c>ElectricityFlowSystem</c>, <c>WaterPipeFlowSystem</c>, the building AI systems), which are off on clients. Without
    /// this channel a client's Electricity and Water infoviews coloured power lines, pipes and transformers from the join
    /// snapshot forever.
    /// <para>
    /// <b>Keys.</b> A flow edge is named by its start and end flow node, and a flow node by its owner's net id and role
    /// (<see cref="FlowNodeKey"/>): the net node or edge holding it in <c>*NodeConnection</c>, the marker node holding it in
    /// <c>*ValveConnection</c>, the building holding it in <c>*BuildingConnection</c> (transformer, producer, consumer,
    /// battery charge and discharge), or the flow system's source and sink super node. Edges touching an unbound owner
    /// are not keyed; duplicate keys are dropped on both sides (<see cref="FlowStatusTable"/>).
    /// </para>
    /// <para>
    /// <b>Host.</b> While at least one live peer has the Electricity (or Water) topic in its view report, a time-sliced
    /// pass (<see cref="ScanBudgetMs"/> per frame, at most one start per <see cref="PassInterval"/>) walks the graph's
    /// connections and edges into a table. When a pass finishes, every peer whose topic is active gets the edges it has
    /// not been sent or whose value changed significantly (<see cref="FlowStatusChannel"/>): in-view edges first (the
    /// owners in its attention view), the rest after, drained over the following frames within the peer's byte budget.
    /// Activating the topic is a keyframe. Messages: <see cref="MessageKind.FlowStatus"/>.
    /// </para>
    /// <para>
    /// <b>Client.</b> Received values go into a cache by key and are written at once into the derived edge the last
    /// local pass indexed under that key (after completing the jobs that read or write the edge type). While the
    /// player's own infoview has the topic, local passes re-index the graph and re-apply cached values to edges the
    /// graph systems rebuilt (a new edge starts with zero flow). The kept graph systems never overwrite flow or flags:
    /// they create edges, and on building and road-connection updates rewrite only direction and capacity of the
    /// struct they read (<c>ElectricityBuildingGraphSystem.CreateOrUpdateFlowEdge</c>,
    /// <c>ElectricityGraphUtils.TrySetFlowEdge</c>); the capacity then follows with the next delta. Building-level
    /// consumer state (<c>ElectricityConsumer</c>, <c>WaterConsumer</c> fulfilment and warning flags) comes from the
    /// attention values under the same topics, so buildings and lines agree within a pass.
    /// </para>
    /// Main thread only; every entry point is guarded (<see cref="Features.FlowStatus"/>).
    /// </summary>
    public sealed class FlowStatusSync
    {
        private const double PassInterval = 1.0;
        private const double ScanBudgetMs = 0.75;
        private const int BytesPerSecond = 16_000, BurstBytes = 48_000;
        private const long MaxBacklogBytes = 256 * 1024;
        private const int MaxMessageBytes = 32 * 1024;
        private const int MaxInboxPerFrame = 16;

        /// <summary>Component and system names this channel depends on (probed at startup, <see cref="Probes"/>).</summary>
        public static readonly string[] TypeNames =
        {
            "Game.Simulation.ElectricityFlowEdge",
            "Game.Simulation.ElectricityNodeConnection",
            "Game.Simulation.ElectricityValveConnection",
            "Game.Simulation.ElectricityBuildingConnection",
            "Game.Simulation.WaterPipeEdge",
            "Game.Simulation.WaterPipeNodeConnection",
            "Game.Simulation.WaterPipeValveConnection",
            "Game.Simulation.WaterPipeBuildingConnection",
        };

        /// <summary>The flow systems that must stay off on clients (GameSimulation; probed at startup).</summary>
        public static readonly string[] FlowSystemNames =
        {
            "Game.Simulation.ElectricityFlowSystem",
            "Game.Simulation.WaterPipeFlowSystem",
        };

        private enum Stage
        {
            Idle,
            NodeConnections,
            ValveConnections,
            BuildingConnections,
            Edges,
        }

        /// <summary>One network's graph: queries, the pass in progress and the last finished pass.</summary>
        private sealed class Graph
        {
            public FlowNetwork Network;
            public AttentionTopic Topic;
            public EntityQuery Edges, NodeConnections, ValveConnections, BuildingConnections;
            /// <summary>Client: read-write query of the edge type; completing it waits for the readers too (NetColorSystem, tooltips).</summary>
            public EntityQuery Writes;
            public readonly Dictionary<Entity, FlowNodeKey> Owners = new Dictionary<Entity, FlowNodeKey>();
            public FlowStatusTable Scan = new FlowStatusTable(), Current = new FlowStatusTable();
            public Dictionary<FlowKey, Entity> ScanIndex = new Dictionary<FlowKey, Entity>(), Index = new Dictionary<FlowKey, Entity>();
            public Stage Stage;
            public int Cursor;
            public double NextPass;
            public Entity Source, Sink;
            public int Passes;
            // client
            public readonly Dictionary<FlowKey, FlowValue> Cache = new Dictionary<FlowKey, FlowValue>();
            public bool Completed;
        }

        private sealed class PeerNetwork
        {
            public readonly FlowStatusChannel Channel = new FlowStatusChannel();
            public readonly List<FlowKey> Pending = new List<FlowKey>();
            public int Cursor;
        }

        private sealed class HostPeer
        {
            public PeerId Peer;
            public readonly PeerByteBudget Budget = new PeerByteBudget(BytesPerSecond, BurstBytes, MaxBacklogBytes);
            public readonly PeerNetwork Electricity = new PeerNetwork(), Water = new PeerNetwork();
            public double LastStream = -1;

            public PeerNetwork Of(FlowNetwork network) => network == FlowNetwork.Electricity ? Electricity : Water;
        }

        private readonly SessionManager _sessions;
        private readonly ILog _log;
        private readonly Graph _electricity = new Graph { Network = FlowNetwork.Electricity, Topic = AttentionTopic.Electricity };
        private readonly Graph _water = new Graph { Network = FlowNetwork.Water, Topic = AttentionTopic.Water };
        private readonly Dictionary<byte, HostPeer> _peers = new Dictionary<byte, HostPeer>();
        private readonly List<HostPeer> _peerScratch = new List<HostPeer>();
        private readonly Queue<byte[]> _inbox = new Queue<byte[]>();
        private readonly List<KeyValuePair<FlowKey, FlowValue>> _entries = new List<KeyValuePair<FlowKey, FlowValue>>();
        private readonly ByteWriter _out = new ByteWriter(MaxMessageBytes + 1024);
        private readonly Stopwatch _watch = new Stopwatch();
        private World _world;
        private NetIdSystem _ids;
        private ElectricityFlowSystem _electricityFlow;
        private WaterPipeFlowSystem _waterFlow;
        private bool _attached;
        private bool _warnedRunning;
        private long _entriesSent, _bytesSent, _messagesApplied, _valuesWritten;

        public FlowStatusSync(SessionManager sessions, ILog log)
        {
            _sessions = sessions;
            _log = log;
            sessions.HostStarted += OnHostStarted;
            sessions.ClientStarted += OnClientStarted;
            sessions.SessionEnded += Reset;
        }

        /// <summary>Host: the attention topics of a peer (<see cref="AttentionValues.PeerTopics"/>).</summary>
        public Func<PeerId, AttentionTopic> PeerTopics { get; set; }

        /// <summary>Host: whether an entity is in a peer's attention view (<see cref="AttentionValues.InView"/>).</summary>
        public Func<PeerId, uint, bool> InView { get; set; }

        /// <summary>Client: the topics of this player's active infoview (<see cref="AttentionValues.LocalTopics"/>).</summary>
        public Func<AttentionTopic> LocalTopics { get; set; }

        private bool IsHost => _sessions.Host != null;

        private bool IsLiveClient => _sessions.Client != null && _sessions.Client.State == ClientState.Live;

        /// <summary>Short diagnostics for the session panel ("" when idle).</summary>
        public string Status
        {
            get
            {
                if (IsHost && _entriesSent > 0) return "flow status: " + _entriesSent + " edges sent (" + (_bytesSent / 1024) + " KiB)";
                if (IsLiveClient && _messagesApplied > 0) return "flow status: " + _valuesWritten + " edges written";
                return "";
            }
        }

        /// <summary>Called by <see cref="SyncController"/> once the world and the net id system exist.</summary>
        public void Attach(World world, NetIdSystem ids)
        {
            if (_attached && ReferenceEquals(world, _world)) return;
            _world = world;
            _ids = ids;
            EntityManager em = world.EntityManager;
            _electricityFlow = world.GetOrCreateSystemManaged<ElectricityFlowSystem>();
            _waterFlow = world.GetOrCreateSystemManaged<WaterPipeFlowSystem>();
            _electricity.Edges = Query(em, ComponentType.ReadOnly<ElectricityFlowEdge>());
            _electricity.Writes = Query(em, ComponentType.ReadWrite<ElectricityFlowEdge>());
            _electricity.NodeConnections = Query(em, ComponentType.ReadOnly<ElectricityNodeConnection>());
            _electricity.ValveConnections = Query(em, ComponentType.ReadOnly<ElectricityValveConnection>());
            _electricity.BuildingConnections = Query(em, ComponentType.ReadOnly<ElectricityBuildingConnection>());
            _water.Edges = Query(em, ComponentType.ReadOnly<WaterPipeEdge>());
            _water.Writes = Query(em, ComponentType.ReadWrite<WaterPipeEdge>());
            _water.NodeConnections = Query(em, ComponentType.ReadOnly<WaterPipeNodeConnection>());
            _water.ValveConnections = Query(em, ComponentType.ReadOnly<WaterPipeValveConnection>());
            _water.BuildingConnections = Query(em, ComponentType.ReadOnly<WaterPipeBuildingConnection>());
            _attached = true;
        }

        private static EntityQuery Query(EntityManager em, ComponentType type)
        {
            return em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { type },
                None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
        }

        // ================================================================== lifecycle

        private void OnHostStarted(SessionHost host)
        {
            Reset();
            host.PeerLive += peer => Guard.Run(Features.FlowStatus, "flow status peer live", () =>
            {
                if (!_peers.ContainsKey(peer.Value)) _peers[peer.Value] = new HostPeer { Peer = peer };
            });
            host.PeerLeft += peer => _peers.Remove(peer.Value);
        }

        private void OnClientStarted(SessionClient client)
        {
            Reset();
            client.AppMessage += e =>
            {
                if (e.Kind != MessageKind.FlowStatus) return;
                Guard.Run(Features.FlowStatus, "flow status receive", () =>
                {
                    var copy = new byte[e.BodyCount];
                    Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                    _inbox.Enqueue(copy);
                });
            };
        }

        private void Reset()
        {
            _peers.Clear();
            _inbox.Clear();
            foreach (Graph g in new[] { _electricity, _water })
            {
                EndPass(g);
                g.Current.Clear();
                g.Index.Clear();
                g.Cache.Clear();
                g.NextPass = 0;
                g.Passes = 0;
                g.Completed = false;
            }
            _warnedRunning = false;
            _entriesSent = _bytesSent = _messagesApplied = _valuesWritten = 0;
        }

        private static void EndPass(Graph g)
        {
            g.Stage = Stage.Idle;
            g.Cursor = 0;
            g.Owners.Clear();
            g.Scan.Clear();
            g.ScanIndex.Clear();
        }

        // ================================================================== host

        /// <summary>Host frame end (after the structure capture): topic states, graph passes, per-peer streams.</summary>
        public void CaptureOutgoing(double now)
        {
            if (!_attached || !IsHost) return;
            bool needElectricity = false, needWater = false;
            foreach (HostPeer p in _peers.Values)
            {
                AttentionTopic topics = PeerTopics != null ? PeerTopics(p.Peer) : AttentionTopic.None;
                if (p.Electricity.Channel.SetActive((topics & AttentionTopic.Electricity) != 0)) Keyframe(p, FlowNetwork.Electricity);
                if (p.Water.Channel.SetActive((topics & AttentionTopic.Water) != 0)) Keyframe(p, FlowNetwork.Water);
                if (!p.Electricity.Channel.Active) Clear(p.Electricity);
                if (!p.Water.Channel.Active) Clear(p.Water);
                needElectricity |= p.Electricity.Channel.Active;
                needWater |= p.Water.Channel.Active;
            }
            if (!needElectricity && _electricity.Stage != Stage.Idle) EndPass(_electricity);
            if (!needWater && _water.Stage != Stage.Idle) EndPass(_water);
            if (!needElectricity && !needWater) return;
            long start = Perf.Start();
            _watch.Restart();
            if (needElectricity) Advance(_electricity, now, ScanBudgetMs, true);
            if (needWater) Advance(_water, now, ScanBudgetMs, true);
            Perf.Stop(Perf.FlowScan, start);
            Stream(now);
        }

        private void Keyframe(HostPeer p, FlowNetwork network)
        {
            // served by the next finished pass (the channel forgot what the peer was sent: everything goes)
            Clear(p.Of(network));
            _log.Debug(LogCategory.Replication, p.Peer + ": " + network + " flow status active (keyframe)");
        }

        private static void Clear(PeerNetwork n)
        {
            n.Pending.Clear();
            n.Cursor = 0;
        }

        private void Plan(Graph g, HostPeer p)
        {
            PeerNetwork n = p.Of(g.Network);
            if (!n.Channel.Active) return;
            Func<FlowKey, bool> inView = null;
            if (InView != null)
            {
                PeerId peer = p.Peer;
                Func<PeerId, uint, bool> view = InView;
                inView = k => view(peer, k.StartOwner) || view(peer, k.EndOwner);
            }
            n.Channel.Plan(g.Current, inView, n.Pending);
            n.Cursor = 0;
        }

        private void Stream(double now)
        {
            SessionHost host = _sessions.Host;
            if (host == null) return;
            _peerScratch.Clear();
            _peerScratch.AddRange(_peers.Values);
            foreach (HostPeer p in _peerScratch)
            {
                bool any = Remaining(p.Electricity) > 0 || Remaining(p.Water) > 0;
                double dt = p.LastStream < 0 ? 0.0 : Math.Min(2.0, Math.Max(0.0, now - p.LastStream));
                p.LastStream = now;
                IPeerOutput output = host.GetPeerOutput(p.Peer);
                if (output == null) continue;
                long backlog = output.QueuedBytes + Math.Max(0, output.Stats.PendingReliableBytes);
                int allowance = p.Budget.Allowance(dt, backlog);
                if (!any || allowance <= 0) continue;
                allowance -= Send(host, p, _electricity, allowance);
                if (allowance > 0) Send(host, p, _water, allowance);
            }
        }

        private static int Remaining(PeerNetwork n) => n.Channel.Active ? n.Pending.Count - n.Cursor : 0;

        /// <summary>Sends pending entries of one network within <paramref name="allowance"/>; returns the bytes spent.</summary>
        private int Send(SessionHost host, HostPeer p, Graph g, int allowance)
        {
            PeerNetwork n = p.Of(g.Network);
            int spent = 0;
            while (Remaining(n) > 0 && spent < allowance)
            {
                _out.Reset();
                int countPosition = FlowStatusMessage.Begin(_out, g.Network, n.Channel.KeyframePending ? FlowStatusMessage.FlagKeyframe : (byte)0);
                int count = 0;
                int limit = Math.Min(MaxMessageBytes, allowance - spent);
                while (n.Cursor < n.Pending.Count && count < FlowStatusMessage.MaxEntries && _out.Length + FlowStatusMessage.MaxEntryBytes <= Math.Max(limit, 64))
                {
                    FlowKey key = n.Pending[n.Cursor++];
                    if (!g.Current.TryGet(key, out FlowValue value)) continue;
                    FlowStatusMessage.WriteEntry(_out, g.Network, key, value);
                    n.Channel.MarkSent(key, value);
                    count++;
                }
                if (count == 0) break;
                FlowStatusMessage.End(_out, countPosition, count);
                host.SendTo(p.Peer, MessageKind.FlowStatus, _out.Buffer, 0, _out.Length);
                p.Budget.Spend(_out.Length);
                spent += _out.Length;
                _entriesSent += count;
                _bytesSent += _out.Length;
                Perf.Counters.AddBytes(Perf.FlowBytes, _out.Length, Perf.Now);
            }
            return spent;
        }

        // ================================================================== passes (host and client)

        /// <summary>Runs the network's pass within the budget; true when a pass finished this frame.</summary>
        private bool Advance(Graph g, double now, double budgetMs, bool host)
        {
            if (g.Stage == Stage.Idle)
            {
                if (now < g.NextPass) return false;
                g.NextPass = now + PassInterval;
                EndPass(g);
                g.Source = g.Network == FlowNetwork.Electricity ? _electricityFlow.sourceNode : _waterFlow.sourceNode;
                g.Sink = g.Network == FlowNetwork.Electricity ? _electricityFlow.sinkNode : _waterFlow.sinkNode;
                g.Stage = Stage.NodeConnections;
            }
            EntityManager em = _world.EntityManager;
            while (g.Stage != Stage.Idle && _watch.Elapsed.TotalMilliseconds < budgetMs)
            {
                EntityQuery query = QueryOf(g);
                query.CompleteDependency();
                NativeArray<ArchetypeChunk> chunks = query.ToArchetypeChunkArray(Allocator.Temp);
                try
                {
                    EntityTypeHandle entityType = em.GetEntityTypeHandle();
                    while (g.Cursor < chunks.Length && _watch.Elapsed.TotalMilliseconds < budgetMs)
                    {
                        Process(em, g, chunks[g.Cursor++], entityType);
                    }
                    if (g.Cursor < chunks.Length) return false;
                }
                finally
                {
                    chunks.Dispose();
                }
                g.Cursor = 0;
                g.Stage = g.Stage == Stage.Edges ? Stage.Idle : g.Stage + 1;
            }
            if (g.Stage != Stage.Idle) return false;
            Finish(g, host);
            return true;
        }

        private static EntityQuery QueryOf(Graph g)
        {
            switch (g.Stage)
            {
                case Stage.NodeConnections: return g.NodeConnections;
                case Stage.ValveConnections: return g.ValveConnections;
                case Stage.BuildingConnections: return g.BuildingConnections;
                default: return g.Edges;
            }
        }

        private void Finish(Graph g, bool host)
        {
            FlowStatusTable table = g.Current;
            g.Current = g.Scan;
            g.Scan = table;
            Dictionary<FlowKey, Entity> index = g.Index;
            g.Index = g.ScanIndex;
            g.ScanIndex = index;
            EndPass(g);
            g.Passes++;
            g.Completed = true;
            if (g.Passes == 1)
            {
                _log.Info(LogCategory.Replication, g.Network + " flow status: " + g.Current.Count + " keyed edges" + (g.Current.AmbiguousCount > 0 ? ", " + g.Current.AmbiguousCount + " ambiguous" : ""));
            }
            if (host)
            {
                foreach (HostPeer p in _peers.Values) Plan(g, p);
            }
            else
            {
                Reapply(g);
            }
        }

        private void Process(EntityManager em, Graph g, ArchetypeChunk chunk, EntityTypeHandle entityType)
        {
            NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
            if (g.Network == FlowNetwork.Electricity)
            {
                switch (g.Stage)
                {
                    case Stage.NodeConnections:
                    {
                        NativeArray<ElectricityNodeConnection> c = chunk.GetNativeArray(ref Handle<ElectricityNodeConnection>(em, ref _eNode));
                        for (int i = 0; i < c.Length; i++) Own(g, entities[i], c[i].m_ElectricityNode, FlowNodeRole.Net);
                        break;
                    }
                    case Stage.ValveConnections:
                    {
                        NativeArray<ElectricityValveConnection> c = chunk.GetNativeArray(ref Handle<ElectricityValveConnection>(em, ref _eValve));
                        for (int i = 0; i < c.Length; i++) Own(g, entities[i], c[i].m_ValveNode, FlowNodeRole.Valve);
                        break;
                    }
                    case Stage.BuildingConnections:
                    {
                        NativeArray<ElectricityBuildingConnection> c = chunk.GetNativeArray(ref Handle<ElectricityBuildingConnection>(em, ref _eBuilding));
                        for (int i = 0; i < c.Length; i++)
                        {
                            ElectricityBuildingConnection b = c[i];
                            Own(g, entities[i], b.m_TransformerNode, FlowNodeRole.Transformer);
                            Own(g, entities[i], EdgeNode(em, b.m_ProducerEdge, false), FlowNodeRole.Producer);
                            Own(g, entities[i], EdgeNode(em, b.m_ConsumerEdge, true), FlowNodeRole.Consumer);
                            Own(g, entities[i], EdgeNode(em, b.m_ChargeEdge, true), FlowNodeRole.Charge);
                            Own(g, entities[i], EdgeNode(em, b.m_DischargeEdge, false), FlowNodeRole.Discharge);
                        }
                        break;
                    }
                    default:
                    {
                        NativeArray<ElectricityFlowEdge> c = chunk.GetNativeArray(ref Handle<ElectricityFlowEdge>(em, ref _eEdge));
                        for (int i = 0; i < c.Length; i++)
                        {
                            ElectricityFlowEdge edge = c[i];
                            Edge(g, entities[i], edge.m_Start, edge.m_End, new FlowValue { Flow = edge.m_Flow, Capacity = edge.m_Capacity, Flags = (byte)edge.m_Flags });
                        }
                        break;
                    }
                }
                return;
            }
            switch (g.Stage)
            {
                case Stage.NodeConnections:
                {
                    NativeArray<WaterPipeNodeConnection> c = chunk.GetNativeArray(ref Handle<WaterPipeNodeConnection>(em, ref _wNode));
                    for (int i = 0; i < c.Length; i++) Own(g, entities[i], c[i].m_WaterPipeNode, FlowNodeRole.Net);
                    break;
                }
                case Stage.ValveConnections:
                {
                    NativeArray<WaterPipeValveConnection> c = chunk.GetNativeArray(ref Handle<WaterPipeValveConnection>(em, ref _wValve));
                    for (int i = 0; i < c.Length; i++) Own(g, entities[i], c[i].m_ValveNode, FlowNodeRole.Valve);
                    break;
                }
                case Stage.BuildingConnections:
                {
                    NativeArray<WaterPipeBuildingConnection> c = chunk.GetNativeArray(ref Handle<WaterPipeBuildingConnection>(em, ref _wBuilding));
                    for (int i = 0; i < c.Length; i++)
                    {
                        WaterPipeBuildingConnection b = c[i];
                        Own(g, entities[i], WaterEdgeNode(em, b.m_ProducerEdge, false), FlowNodeRole.Producer);
                        Own(g, entities[i], WaterEdgeNode(em, b.m_ConsumerEdge, true), FlowNodeRole.Consumer);
                    }
                    break;
                }
                default:
                {
                    NativeArray<WaterPipeEdge> c = chunk.GetNativeArray(ref Handle<WaterPipeEdge>(em, ref _wEdge));
                    for (int i = 0; i < c.Length; i++)
                    {
                        WaterPipeEdge edge = c[i];
                        Edge(g, entities[i], edge.m_Start, edge.m_End, Value(edge));
                    }
                    break;
                }
            }
        }

        // type handles, refreshed before every use (a handle from the EntityManager is valid until the next structural change)
        private ComponentTypeHandle<ElectricityNodeConnection> _eNode;
        private ComponentTypeHandle<ElectricityValveConnection> _eValve;
        private ComponentTypeHandle<ElectricityBuildingConnection> _eBuilding;
        private ComponentTypeHandle<ElectricityFlowEdge> _eEdge;
        private ComponentTypeHandle<WaterPipeNodeConnection> _wNode;
        private ComponentTypeHandle<WaterPipeValveConnection> _wValve;
        private ComponentTypeHandle<WaterPipeBuildingConnection> _wBuilding;
        private ComponentTypeHandle<WaterPipeEdge> _wEdge;

        private static ref ComponentTypeHandle<T> Handle<T>(EntityManager em, ref ComponentTypeHandle<T> slot) where T : unmanaged, IComponentData
        {
            slot = em.GetComponentTypeHandle<T>(true);
            return ref slot;
        }

        private static FlowValue Value(in WaterPipeEdge edge) => new FlowValue
        {
            Flow = edge.m_FreshFlow,
            Capacity = edge.m_FreshCapacity,
            SewageFlow = edge.m_SewageFlow,
            SewageCapacity = edge.m_SewageCapacity,
            Pollution = edge.m_FreshPollution,
            Flags = (byte)edge.m_Flags,
        };

        private static Entity EdgeNode(EntityManager em, Entity edge, bool start)
        {
            if (edge == Entity.Null || !em.Exists(edge) || !em.HasComponent<ElectricityFlowEdge>(edge)) return Entity.Null;
            ElectricityFlowEdge e = em.GetComponentData<ElectricityFlowEdge>(edge);
            return start ? e.m_Start : e.m_End;
        }

        private static Entity WaterEdgeNode(EntityManager em, Entity edge, bool start)
        {
            if (edge == Entity.Null || !em.Exists(edge) || !em.HasComponent<WaterPipeEdge>(edge)) return Entity.Null;
            WaterPipeEdge e = em.GetComponentData<WaterPipeEdge>(edge);
            return start ? e.m_Start : e.m_End;
        }

        private void Own(Graph g, Entity owner, Entity node, FlowNodeRole role)
        {
            if (node == Entity.Null) return;
            uint id = _ids.GetNetId(owner);
            if (id == 0) return;
            g.Owners[node] = new FlowNodeKey(id, role);
        }

        private static bool TryKey(Graph g, Entity node, out FlowNodeKey key)
        {
            if (node == Entity.Null)
            {
                key = default;
                return false;
            }
            if (node == g.Source)
            {
                key = new FlowNodeKey(0, FlowNodeRole.Source);
                return true;
            }
            if (node == g.Sink)
            {
                key = new FlowNodeKey(0, FlowNodeRole.Sink);
                return true;
            }
            return g.Owners.TryGetValue(node, out key);
        }

        private static void Edge(Graph g, Entity entity, Entity start, Entity end, FlowValue value)
        {
            if (!TryKey(g, start, out FlowNodeKey s) || !TryKey(g, end, out FlowNodeKey t)) return;
            var key = new FlowKey(s, t);
            if (g.Scan.Add(key, value)) g.ScanIndex[key] = entity;
            else g.ScanIndex.Remove(key);
        }

        // ================================================================== client

        /// <summary>
        /// Client frame start, after the structure and value apply: received flow values into the cache and the indexed
        /// edges; while the player's infoview has the topic, local passes re-index and re-apply.
        /// </summary>
        public void ApplyIncoming(double now)
        {
            if (!_attached || !IsLiveClient)
            {
                if (_sessions.Client == null && _inbox.Count > 0) _inbox.Clear();
                return;
            }
            int n = 0;
            while (_inbox.Count > 0 && n++ < MaxInboxPerFrame)
            {
                byte[] data = _inbox.Dequeue();
                try
                {
                    FlowNetwork network = FlowStatusMessage.Read(data, 0, data.Length, _entries, out _);
                    Graph g = network == FlowNetwork.Electricity ? _electricity : _water;
                    CompleteEdges(g);
                    EntityManager em = _world.EntityManager;
                    foreach (KeyValuePair<FlowKey, FlowValue> kv in _entries)
                    {
                        g.Cache[kv.Key] = kv.Value;
                        if (g.Index.TryGetValue(kv.Key, out Entity e)) Write(em, g, e, kv.Value);
                    }
                    _messagesApplied++;
                }
                catch (DecodeException ex)
                {
                    _log.Warn(LogCategory.Replication, "dropped a malformed flow status message: " + ex.Message);
                }
            }
            AttentionTopic topics = LocalTopics != null ? LocalTopics() : AttentionTopic.None;
            _watch.Restart();
            foreach (Graph g in new[] { _electricity, _water })
            {
                if ((topics & g.Topic) == 0)
                {
                    if (g.Stage != Stage.Idle) EndPass(g);
                    continue;
                }
                CheckFlowSystemOff(g);
                Advance(g, now, ScanBudgetMs * 0.5, false);
            }
        }

        /// <summary>Completes every job that reads or writes the edge type before the main thread writes it.</summary>
        private static void CompleteEdges(Graph g) => g.Writes.CompleteDependency();

        /// <summary>After a local pass: cached host values onto the edges the graph systems built or rebuilt meanwhile.</summary>
        private void Reapply(Graph g)
        {
            if (g.Cache.Count == 0) return;
            CompleteEdges(g);
            EntityManager em = _world.EntityManager;
            foreach (KeyValuePair<FlowKey, Entity> kv in g.Index)
            {
                if (g.Cache.TryGetValue(kv.Key, out FlowValue value) && g.Current.TryGet(kv.Key, out FlowValue local) && !local.Equals(value)) Write(em, g, kv.Value, value);
            }
            // memory guard: keys of edges that no longer exist (the table is complete only after a whole pass)
            if (g.Cache.Count > g.Current.Count + 4096)
            {
                var gone = new List<FlowKey>();
                foreach (FlowKey k in g.Cache.Keys)
                {
                    if (!g.Current.TryGet(k, out _) && !g.Current.IsAmbiguous(k)) gone.Add(k);
                }
                foreach (FlowKey k in gone) g.Cache.Remove(k);
            }
        }

        /// <summary>Writes the host's value into a local derived edge (flow, capacity, flags; never start, end or index).</summary>
        private void Write(EntityManager em, Graph g, Entity e, in FlowValue value)
        {
            if (!em.Exists(e)) return;
            if (g.Network == FlowNetwork.Electricity)
            {
                if (!em.HasComponent<ElectricityFlowEdge>(e)) return;
                ElectricityFlowEdge edge = em.GetComponentData<ElectricityFlowEdge>(e);
                if (edge.m_Flow == value.Flow && edge.m_Capacity == value.Capacity && (byte)edge.m_Flags == value.Flags) return;
                edge.m_Flow = value.Flow;
                edge.m_Capacity = value.Capacity;
                edge.m_Flags = (ElectricityFlowEdgeFlags)value.Flags;
                em.SetComponentData(e, edge);
            }
            else
            {
                if (!em.HasComponent<WaterPipeEdge>(e)) return;
                WaterPipeEdge edge = em.GetComponentData<WaterPipeEdge>(e);
                if (Value(edge).Equals(value)) return;
                edge.m_FreshFlow = value.Flow;
                edge.m_FreshCapacity = value.Capacity;
                edge.m_SewageFlow = value.SewageFlow;
                edge.m_SewageCapacity = value.SewageCapacity;
                edge.m_FreshPollution = value.Pollution;
                edge.m_Flags = (WaterPipeEdgeFlags)value.Flags;
                em.SetComponentData(e, edge);
            }
            _valuesWritten++;
        }

        /// <summary>The flow systems are GameSimulation systems, off on clients (follower clock or paused simulation).</summary>
        private void CheckFlowSystemOff(Graph g)
        {
            if (_warnedRunning) return;
            ComponentSystemBase system = g.Network == FlowNetwork.Electricity ? (ComponentSystemBase)_electricityFlow : _waterFlow;
            FollowerClock clock = FollowerClock.Instance;
            if (system == null || clock == null || !clock.Active || !system.Enabled) return;
            _warnedRunning = true;
            _log.Warn(LogCategory.Replication, system.GetType().Name + " runs on this client; it may overwrite the host's flow status in the infoview");
        }
    }
}
