using System;
using System.Collections.Generic;
using Game.Net;
using MoveableBridgeData = Game.Prefabs.MoveableBridgeData;
using PrefabRef = Game.Prefabs.PrefabRef;
using SkylineTogether.Core;
using SkylineTogether.Diagnostics;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// w3/audit, client: traffic lights follow the host (ARCH §4.9.9). The host's <c>TrafficLightSystem</c> (a
    /// <c>GameSimulation</c> system, off on clients) advances each junction's phase and writes the lanes' signals and the
    /// signal heads in the same job. Clients receive the node's <c>TrafficLights</c> with the attention values whenever its
    /// phase changes, and this class repeats the derived half with the game's own public functions
    /// (<c>TrafficLightSystem.UpdateLaneSignal</c>, <c>UpdateTrafficLightState</c>, <c>UpdateMoveableBridge</c>): lane
    /// signals, the lights of the signal heads, and the lift of moveable bridges. Only junctions whose phase changed are
    /// touched. Main thread only; every failure is contained (the lights then simply stay as they are).
    /// </summary>
    public sealed class SignalFollower
    {
        private readonly ILog _log;
        private readonly Dictionary<Entity, ulong> _applied = new Dictionary<Entity, ulong>();
        private readonly List<Entity> _lanes = new List<Entity>();
        private readonly HashSet<Entity> _live = new HashSet<Entity>();
        private World _world;
        private EntityQuery _nodes;
        private bool _attached;
        private bool _warned;
        private int _frames;

        public SignalFollower(ILog log)
        {
            _log = log;
        }

        /// <summary>Junctions whose phase was applied (diagnostics).</summary>
        public long Applied { get; private set; }

        public void Attach(World world)
        {
            if (_attached) return;
            try
            {
                _world = world;
                _nodes = world.EntityManager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<TrafficLights>(), ComponentType.ReadOnly<SubLane>() },
                    None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>() },
                });
                _attached = true;
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Replication, "traffic light follower unavailable", ex);
            }
        }

        public void Reset()
        {
            _applied.Clear();
            _frames = 0;
            Applied = 0;
        }

        private static ulong Phase(TrafficLights t) =>
            (ulong)(byte)t.m_State | ((ulong)(byte)t.m_Flags << 8) | ((ulong)t.m_SignalGroupCount << 16) | ((ulong)t.m_CurrentSignalGroup << 24) | ((ulong)t.m_NextSignalGroup << 32) | (1UL << 40);

        /// <summary>Client, once per frame after the host's values were applied.</summary>
        public void Update()
        {
            if (!_attached || _nodes.IsEmptyIgnoreFilter) return;
            EntityManager em = _world.EntityManager;
            NativeArray<Entity> nodes = _nodes.ToEntityArray(Allocator.Temp);
            NativeArray<TrafficLights> lights = _nodes.ToComponentDataArray<TrafficLights>(Allocator.Temp);
            try
            {
                bool sweep = (++_frames & 255) == 0;
                if (sweep) _live.Clear();
                for (int i = 0; i < nodes.Length; i++)
                {
                    Entity node = nodes[i];
                    if (sweep) _live.Add(node);
                    TrafficLights t = lights[i];
                    if ((t.m_Flags & TrafficLightFlags.IsSubNode) != 0) continue;
                    ulong phase = Phase(t);
                    if (_applied.TryGetValue(node, out ulong last) && last == phase) continue;
                    _applied[node] = phase;
                    try
                    {
                        Apply(em, node, t);
                        Applied++;
                    }
                    catch (Exception ex)
                    {
                        if (!_warned)
                        {
                            _warned = true;
                            _log.Warn(LogCategory.Replication, "deriving the signals of a junction failed (further failures are not logged)", ex);
                        }
                    }
                }
                if (sweep && _applied.Count > _live.Count)
                {
                    var gone = new List<Entity>();
                    foreach (Entity e in _applied.Keys)
                    {
                        if (!_live.Contains(e)) gone.Add(e);
                    }
                    foreach (Entity e in gone) _applied.Remove(e);
                }
            }
            finally
            {
                nodes.Dispose();
                lights.Dispose();
            }
        }

        private void Apply(EntityManager em, Entity node, TrafficLights t)
        {
            _lanes.Clear();
            CollectLanes(em, em.GetBuffer<SubLane>(node, true));
            Entity bridge = Entity.Null;
            MoveableBridgeData bridgeData = default;
            if ((t.m_Flags & TrafficLightFlags.MoveableBridge) != 0)
            {
                FindMoveableBridge(em, node, out bridge, out bridgeData);
                CollectSubNetLanes(em, node);
            }
            foreach (Entity lane in _lanes)
            {
                LaneSignal signal = em.GetComponentData<LaneSignal>(lane);
                Game.Simulation.TrafficLightSystem.UpdateLaneSignal(t, ref signal);
                signal.m_Petitioner = Entity.Null;
                signal.m_Priority = signal.m_Default;
                em.SetComponentData(lane, signal);
            }
            if (em.HasBuffer<Game.Objects.SubObject>(node))
            {
                DynamicBuffer<Game.Objects.SubObject> subObjects = em.GetBuffer<Game.Objects.SubObject>(node, true);
                for (int i = 0; i < subObjects.Length; i++)
                {
                    Entity head = subObjects[i].m_SubObject;
                    if (!em.HasComponent<Game.Objects.TrafficLight>(head)) continue;
                    Game.Objects.TrafficLight light = em.GetComponentData<Game.Objects.TrafficLight>(head);
                    Game.Simulation.TrafficLightSystem.UpdateTrafficLightState(t, ref light);
                    em.SetComponentData(head, light);
                }
            }
            if (bridge != Entity.Null && em.HasComponent<Game.Objects.Transform>(bridge) && em.HasComponent<Game.Common.PointOfInterest>(bridge))
            {
                Game.Common.PointOfInterest poi = em.GetComponentData<Game.Common.PointOfInterest>(bridge);
                Game.Simulation.TrafficLightSystem.UpdateMoveableBridge(t, em.GetComponentData<Game.Objects.Transform>(bridge), bridgeData, ref poi);
                em.SetComponentData(bridge, poi);
                if (!em.HasComponent<Game.Common.EffectsUpdated>(node)) em.AddComponent<Game.Common.EffectsUpdated>(node);
            }
        }

        private void CollectLanes(EntityManager em, DynamicBuffer<SubLane> lanes)
        {
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].m_SubLane;
                if (em.HasComponent<LaneSignal>(lane)) _lanes.Add(lane);
            }
        }

        /// <summary>The lanes of the bridge's sub-nets on this node's side (as <c>TrafficLightSystem.FillLaneSignals</c>).</summary>
        private void CollectSubNetLanes(EntityManager em, Entity node)
        {
            if (!em.HasBuffer<ConnectedEdge>(node) || !em.HasComponent<Node>(node)) return;
            Node nodeData = em.GetComponentData<Node>(node);
            DynamicBuffer<ConnectedEdge> edges = em.GetBuffer<ConnectedEdge>(node, true);
            for (int i = 0; i < edges.Length; i++)
            {
                Entity edge = edges[i].m_Edge;
                if (!em.HasBuffer<SubNet>(edge) || !em.HasComponent<Curve>(edge)) continue;
                Curve curve = em.GetComponentData<Curve>(edge);
                bool nodeAtStart = Unity.Mathematics.math.distancesq(nodeData.m_Position, curve.m_Bezier.a) <= Unity.Mathematics.math.distancesq(nodeData.m_Position, curve.m_Bezier.d);
                DynamicBuffer<SubNet> subNets = em.GetBuffer<SubNet>(edge, true);
                for (int j = 0; j < subNets.Length; j++)
                {
                    Entity subNet = subNets[j].m_SubNet;
                    if (!em.HasComponent<Node>(subNet) || !em.HasBuffer<SubLane>(subNet)) continue;
                    Node sub = em.GetComponentData<Node>(subNet);
                    bool subAtStart = Unity.Mathematics.math.distancesq(sub.m_Position, curve.m_Bezier.a) <= Unity.Mathematics.math.distancesq(sub.m_Position, curve.m_Bezier.d);
                    if (nodeAtStart == subAtStart) CollectLanes(em, em.GetBuffer<SubLane>(subNet, true));
                }
            }
        }

        private static void FindMoveableBridge(EntityManager em, Entity node, out Entity bridge, out MoveableBridgeData data)
        {
            bridge = Entity.Null;
            data = default;
            if (!em.HasBuffer<Game.Objects.SubObject>(node)) return;
            DynamicBuffer<Game.Objects.SubObject> subObjects = em.GetBuffer<Game.Objects.SubObject>(node, true);
            for (int i = 0; i < subObjects.Length; i++)
            {
                Entity sub = subObjects[i].m_SubObject;
                if (!em.HasComponent<Game.Common.PointOfInterest>(sub) || !em.HasComponent<PrefabRef>(sub)) continue;
                Entity prefab = em.GetComponentData<PrefabRef>(sub).m_Prefab;
                if (!em.HasComponent<MoveableBridgeData>(prefab)) continue;
                bridge = sub;
                data = em.GetComponentData<MoveableBridgeData>(prefab);
                return;
            }
        }
    }
}
