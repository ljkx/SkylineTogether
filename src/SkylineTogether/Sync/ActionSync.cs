using System;
using System.Collections.Generic;
using Game.Areas;
using Game.City;
using Game.Common;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Game.UI.InGame;
using SkylineTogether.Core;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Sync;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// gameplay branch: the player actions whose vanilla handlers write the world without any tag the entity capture
    /// could scope, or through an API only the host may run (R10 §2-§5, ARCH §5.2 stage A, §5.3). See
    /// docs/design/ACTION-COVERAGE.md for the full action table.
    /// <list type="bullet">
    /// <item><b>Client → host</b> (<see cref="GlobalChannel.Action"/> intents): dev-tree purchases and map-tile purchases
    /// (detected at frame start, before host data, from the local <c>Unlock</c> events / un-<c>Native</c>-ed tiles and
    /// the points or money the vanilla handler took), the historical toggle, vehicle models and service districts of
    /// the selected entity (M4 direct writes, watched against a baseline), custom mesh colours (every entity tagged
    /// <c>BatchesUpdated</c>, so "set to all" is covered), line colours (<c>ColorUpdated</c> events) and the bulldozer's
    /// "sub-elements" deletions of derived props and fences (derived keys).</item>
    /// <item><b>Host</b>: validates and executes each intent through the vanilla data paths (frame start; dev-tree
    /// purchases in the late phase because <c>DevTreeSystem.Purchase</c> uses <c>EndFrameBarrier</c>), and marks the
    /// target authored so the capture streams it; the host player's own direct writes are found by the same watchers.
    /// The host's unlocked-prefab set (<see cref="GlobalChannel.Unlocks"/>) reaches clients at ≤ 1 Hz on change; they
    /// replay it as vanilla <c>Unlock</c> events (milestones, dev tree, mods), and get milestone pop-ups.</item>
    /// </list>
    /// Every entry point is isolated by try/catch in its caller; a failing watcher only costs its own action.
    /// </summary>
    public sealed class ActionSync
    {
        private const double UnlockInterval = 1.0;
        private const double UnlockKeyframe = 30.0;
        private const int MaxMilestonePopups = 5;

        private readonly ILog _log;
        private readonly HashSet<string> _once = new HashSet<string>();
        private readonly ChannelSendGate _unlockGate = new ChannelSendGate(UnlockKeyframe);
        private World _world;
        private EntityCodec _codec;
        private NetIdSystem _ids;
        private SelectedInfoUISystem _selected;
        private ToolSystem _tools;
        private CitySystem _city;
        private DevTreeSystem _devTree;
        private MapTilePurchaseSystem _mapTiles;
        private EntityQuery _meshDirty;
        private EntityQuery _meshEnabled;
        private EntityQuery _colorEvents;
        private EntityQuery _unlockEvents;
        private EntityQuery _tilesUpdated;
        private EntityQuery _ownedTiles; // w9/perf (trust): the host prices purchases itself
        private readonly List<float> _tileValues = new List<float>();
        private readonly List<float> _featureAmounts = new List<float>();
        private readonly List<float> _featureCosts = new List<float>();
        private EntityQuery _lockable;
        private EntityQuery _milestones;
        private EntityArchetype _unlockArchetype;
        private EntityArchetype _colorArchetype;
        private EntityArchetype _milestoneArchetype;
        private double _lastUnlock = double.NegativeInfinity;

        // client
        private readonly EditWatch _historical = new EditWatch();
        private readonly EditWatch _vehicles = new EditWatch();
        private readonly EditWatch _districts = new EditWatch();
        private readonly Dictionary<uint, ulong> _meshState = new Dictionary<uint, ulong>();
        private readonly List<ActionIntent> _pending = new List<ActionIntent>();
        private readonly HashSet<Entity> _injectedUnlocks = new HashSet<Entity>();
        private readonly List<SubElementKey> _subElements = new List<SubElementKey>();
        private Entity _watchedSelection;
        private Entity _watchedOwner;
        private bool _meshPrimed;
        private int _lastMoney;
        private bool _hasMoney;
        private int _lastPoints;
        private bool _hasPoints;
        private int _lastMilestone = -1;
        private double _relockHoldUntil = double.NegativeInfinity;

        // host
        private readonly List<ActionIntent> _broadcast = new List<ActionIntent>();
        private readonly List<(PeerId Peer, uint Node)> _lateDevTree = new List<(PeerId, uint)>();

        public ActionSync(ILog log)
        {
            _log = log;
            // perf pass: the watchers run every frame on both roles; their delegates are made once
            _foundSelection = (e, kind, intent) => OnWatchFound(e, intent);
            _foundWrite = OnWatchFound;
            _watchSelectionCall = () => WatchSelection(_watchWrote, _foundSelection);
            _watchMeshCall = () => WatchMeshColors(_watchWrote, _foundWrite);
            _watchRoutesCall = () => WatchRouteColors(_watchWrote, _foundWrite);
        }

        private readonly Found _foundSelection;
        private readonly Action<Entity, ActionIntent> _foundWrite;
        private readonly Action _watchSelectionCall, _watchMeshCall, _watchRoutesCall;
        private Action _detectDevTree, _detectMapTiles, _milestonesCall; // w9/perf
        private HashSet<Entity> _watchWrote; // the entities not to report this pass (host: proposals; client: host data)
        private bool _watchForHost;

        /// <summary>A watcher found a direct write: on the host the capture streams the entity, on a client it becomes an intent.</summary>
        private void OnWatchFound(Entity e, ActionIntent intent)
        {
            if (_watchForHost) Touch?.Invoke(e, PeerId.Host);
            else _pending.Add(intent);
        }

        /// <summary>Host: an intent made an entity change; the capture streams it, authored by the given peer.</summary>
        public Action<Entity, PeerId> Touch;

        /// <summary>Host: an intent was refused; the controller tells the peer and re-sends <c>ids</c> (rollback).</summary>
        public Action<PeerId, RejectReason, IReadOnlyList<uint>> Reject;

        /// <summary>Host: whether a peer may do what a permission covers.</summary>
        public Func<PeerId, Permission, bool> Allows;

        /// <summary>Client: the local peer id (sub-element deletions of this peer are not replayed on it again).</summary>
        public Func<PeerId> LocalPeer;

        private bool Attached => _codec != null;

        public void Attach(World world, EntityCodec codec, NetIdSystem ids)
        {
            if (_codec != null) return;
            try
            {
                _world = world;
                _selected = world.GetOrCreateSystemManaged<SelectedInfoUISystem>();
                _tools = world.GetOrCreateSystemManaged<ToolSystem>();
                _city = world.GetOrCreateSystemManaged<CitySystem>();
                _devTree = world.GetOrCreateSystemManaged<DevTreeSystem>();
                _mapTiles = world.GetOrCreateSystemManaged<MapTilePurchaseSystem>();
                EntityManager em = world.EntityManager;
                _meshDirty = em.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<BatchesUpdated>(), ComponentType.ReadOnly<CustomMeshColor>() },
                    None = new[] { ComponentType.ReadOnly<Temp>() },
                    Options = EntityQueryOptions.IgnoreComponentEnabledState,
                });
                _meshEnabled = em.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<CustomMeshColor>() },
                    None = new[] { ComponentType.ReadOnly<Temp>() },
                });
                _colorEvents = em.CreateEntityQuery(ComponentType.ReadOnly<Game.Common.Event>(), ComponentType.ReadOnly<Game.Routes.ColorUpdated>());
                _unlockEvents = em.CreateEntityQuery(ComponentType.ReadOnly<Game.Common.Event>(), ComponentType.ReadOnly<Unlock>());
                _tilesUpdated = em.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<MapTile>(), ComponentType.ReadOnly<Updated>() },
                    None = new[] { ComponentType.ReadOnly<Native>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
                });
                _ownedTiles = em.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<MapTile>() },
                    None = new[] { ComponentType.ReadOnly<Native>() },
                });
                _lockable = em.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<Locked>(), ComponentType.ReadOnly<PrefabData>() },
                    Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IgnoreComponentEnabledState,
                });
                _milestones = em.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<MilestoneData>(), ComponentType.ReadOnly<PrefabData>() },
                    Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IgnoreComponentEnabledState,
                });
                // the archetypes the vanilla producers use (DevTreeSystem.cs:122, ColorSection.cs:33, MilestoneSystem.cs:61)
                _unlockArchetype = em.CreateArchetype(ComponentType.ReadWrite<Unlock>(), ComponentType.ReadWrite<Game.Common.Event>());
                _colorArchetype = em.CreateArchetype(ComponentType.ReadWrite<Game.Common.Event>(), ComponentType.ReadWrite<Game.Routes.ColorUpdated>());
                _milestoneArchetype = em.CreateArchetype(ComponentType.ReadWrite<Game.Common.Event>(), ComponentType.ReadWrite<MilestoneReachedEvent>());
                _codec = codec;
                _ids = ids;
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Replication, "action sync unavailable", ex);
            }
        }

        /// <summary>Session started or ended: forget every baseline and queue.</summary>
        public void Reset()
        {
            _historical.Clear();
            _vehicles.Clear();
            _districts.Clear();
            _meshState.Clear();
            _pending.Clear();
            _injectedUnlocks.Clear();
            _subElements.Clear();
            _broadcast.Clear();
            _lateDevTree.Clear();
            _unlockGate.Clear();
            _watchedSelection = Entity.Null;
            _watchedOwner = Entity.Null;
            _meshPrimed = false;
            _hasMoney = false;
            _hasPoints = false;
            _lastMilestone = -1;
            _lastUnlock = double.NegativeInfinity;
        }

        /// <summary>Host: send the unlock set with the next capture (a peer joined, or a purchase was refused).</summary>
        public void ResendUnlocks() => _unlockGate.MarkDirty(GlobalChannel.Unlocks);

        // ================================================================== client: detection

        /// <summary>
        /// Client, frame start before host data: the vanilla dev-tree and map-tile handlers ran in last frame's UI pass;
        /// their <c>Unlock</c> events (EndFrameBarrier) and un-<c>Native</c>-ed tiles are visible now, and the points or
        /// money they took are still the local prediction (host values have not been applied yet this frame).
        /// </summary>
        public void BeforeApply()
        {
            if (!Attached) return;
            _injectedUnlocks.Clear();
            Guard("dev tree", _detectDevTree ?? (_detectDevTree = DetectDevTree)); // w9/perf: cached delegates
            Guard("map tiles", _detectMapTiles ?? (_detectMapTiles = DetectMapTiles));
        }

        private void DetectDevTree()
        {
            if (_unlockEvents.IsEmptyIgnoreFilter || !_hasPoints) return;
            EntityManager em = _world.EntityManager;
            int points = ReadPoints();
            int spent = _lastPoints - points;
            if (spent <= 0) return;
            NativeArray<Unlock> events = _unlockEvents.ToComponentDataArray<Unlock>(Allocator.Temp);
            try
            {
                foreach (Unlock u in events)
                {
                    if (u.m_Prefab == Entity.Null || !em.Exists(u.m_Prefab) || !em.HasComponent<DevTreeNodeData>(u.m_Prefab)) continue;
                    int cost = em.GetComponentData<DevTreeNodeData>(u.m_Prefab).m_Cost;
                    if (cost > spent) continue;
                    uint id = _ids.GetNetId(u.m_Prefab);
                    if (id == 0) continue;
                    spent -= cost;
                    _pending.Add(new ActionIntent { Kind = ActionKind.DevTreePurchase, Target = id });
                    _relockHoldUntil = UnityEngine.Time.realtimeSinceStartupAsDouble + 3.0;
                    _log.Info(LogCategory.Replication, "proposing dev-tree purchase #" + id);
                }
            }
            finally
            {
                events.Dispose();
            }
        }

        private void DetectMapTiles()
        {
            if (_tilesUpdated.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> tiles = _tilesUpdated.ToEntityArray(Allocator.Temp);
            try
            {
                var ids = new List<uint>();
                foreach (Entity t in tiles)
                {
                    uint id = _ids.GetNetId(t);
                    if (id != 0) ids.Add(id);
                }
                if (ids.Count == 0) return;
                long cost = 0;
                if (_hasMoney && ReadMoney(out int money, out bool unlimited) && !unlimited) cost = Math.Max(0, (long)_lastMoney - money);
                // tiles are bought from the map-tile view (the selection tool in MapTiles mode, MapTilePurchaseSystem
                // .TryGetSelections) and normally cost money; an un-Native-ed tile with neither is not a purchase
                bool view = _tools.activeTool is SelectionToolSystem sel && sel.selectionType == SelectionType.MapTiles;
                if (!view && cost <= 0) return;
                _pending.Add(new ActionIntent { Kind = ActionKind.PurchaseMapTiles, Ids = ids.ToArray(), Amount = cost });
                _log.Info(LogCategory.Replication, "proposing the purchase of " + ids.Count + " map tile(s) for " + cost);
            }
            finally
            {
                tiles.Dispose();
            }
        }

        /// <summary>Client, after the frame's host data: milestone pop-ups for levels the host reached.</summary>
        public void AfterApply()
        {
            if (!Attached) return;
            Guard("milestones", _milestonesCall ?? (_milestonesCall = Milestones));
        }

        private void Milestones()
        {
            EntityManager em = _world.EntityManager;
            Entity city = _city.City;
            if (city == Entity.Null || !em.Exists(city) || !em.HasComponent<MilestoneLevel>(city)) return;
            int level = em.GetComponentData<MilestoneLevel>(city).m_AchievedMilestone;
            if (_lastMilestone >= 0 && level > _lastMilestone)
            {
                // MilestoneSystem is off on clients; its event feeds the vanilla pop-up (MilestoneUISystem.cs:316)
                NativeArray<Entity> prefabs = _milestones.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = Math.Max(_lastMilestone + 1, level - MaxMilestonePopups + 1); i <= level; i++)
                    {
                        Entity milestone = Entity.Null;
                        foreach (Entity p in prefabs)
                        {
                            if (em.GetComponentData<MilestoneData>(p).m_Index != i) continue;
                            milestone = p;
                            break;
                        }
                        Entity ev = em.CreateEntity(_milestoneArchetype);
                        em.SetComponentData(ev, new MilestoneReachedEvent(milestone, i));
                    }
                }
                finally
                {
                    prefabs.Dispose();
                }
            }
            _lastMilestone = level;
        }

        /// <summary>
        /// Client, end of frame (after the entity capture): watched direct writes and events of this frame.
        /// <paramref name="applied"/> holds the entities the host's data wrote this frame (never local edits).
        /// </summary>
        public void ClientCapture(HashSet<Entity> applied)
        {
            if (!Attached) return;
            _watchWrote = applied;
            _watchForHost = false;
            Guard("selection", _watchSelectionCall);
            Guard("mesh colours", _watchMeshCall);
            Guard("line colours", _watchRoutesCall);
            _watchWrote = null;
            if (_subElements.Count > 0)
            {
                _pending.Add(new ActionIntent { Kind = ActionKind.DeleteSubElements, Keys = _subElements.ToArray() });
                _subElements.Clear();
            }
            // baselines for the next frame-start detection: after this frame's tool apply and host data
            _hasMoney = ReadMoney(out _lastMoney, out _);
            _lastPoints = ReadPoints();
            _hasPoints = true;
        }

        /// <summary>Client: appends the pending intents to the global proposal batch.</summary>
        public void ClientFlush(SyncBatch.Writer w)
        {
            if (_pending.Count == 0) return;
            try
            {
                for (int start = 0; start < _pending.Count; start += ActionPayload.MaxIntents)
                {
                    var part = _pending.GetRange(start, Math.Min(ActionPayload.MaxIntents, _pending.Count - start));
                    byte[] payload = ActionPayload.Write(part);
                    w.Global(GlobalChannel.Action, payload, 0, payload.Length);
                }
            }
            catch (Exception ex)
            {
                Once("flush", "action sync: writing intents failed", ex);
            }
            _pending.Clear();
        }

        /// <summary>
        /// Both roles, on frames where the bulldozer applied in "sub-elements" or "everything" mode: a derived prop or
        /// fence that was removed on its own (its owner stays) becomes a key the other peers resolve and delete.
        /// </summary>
        public void NoteSubElementDeleted(Entity e)
        {
            if (!Attached) return;
            EntityManager em = _world.EntityManager;
            if (!em.HasComponent<Owner>(e)) return;
            Entity owner = em.GetComponentData<Owner>(e).m_Owner;
            if (owner == Entity.Null || !em.Exists(owner)) return;
            // an owner that is being removed, rebuilt or regenerated deletes and recreates its children by itself
            if (em.HasComponent<Deleted>(owner) || em.HasComponent<Updated>(owner) || em.HasComponent<Created>(owner)) return;
            if (!_codec.TryGetPortableRef(e, out uint id, out DerivedKey key) || id != 0) return;
            if (_subElements.Count < ActionPayload.MaxKeys) _subElements.Add(ToWire(key));
        }

        private delegate void Found(Entity e, ActionKind kind, ActionIntent intent);

        /// <summary>The selected entity (and the service-district selection tool's owner): historical, vehicles, districts.</summary>
        private void WatchSelection(HashSet<Entity> hostWrote, Found found)
        {
            EntityManager em = _world.EntityManager;
            Entity selected = _selected.selectedEntity;
            Entity owner = _tools.activeTool is SelectionToolSystem sel && sel.selectionType == SelectionType.ServiceDistrict ? sel.selectionOwner : Entity.Null;
            if (selected != _watchedSelection || owner != _watchedOwner)
            {
                // a new target: its current state is the baseline (state seen earlier may be stale)
                _historical.Clear();
                _vehicles.Clear();
                _districts.Clear();
                _watchedSelection = selected;
                _watchedOwner = owner;
            }
            WatchOne(em, selected, hostWrote, found);
            if (owner != selected) WatchOne(em, owner, hostWrote, found);
        }

        private void WatchOne(EntityManager em, Entity e, HashSet<Entity> hostWrote, Found found)
        {
            if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Deleted>(e) || em.HasComponent<Temp>(e)) return;
            uint id = _ids.GetNetId(e);
            if (id == 0) return;
            bool wrote = hostWrote != null && hostWrote.Contains(e);
            if (em.HasComponent<Game.Buildings.Building>(e))
            {
                bool historical = (em.GetComponentData<Game.Buildings.Building>(e).m_Flags & Game.Buildings.BuildingFlags.Historical) != 0;
                if (_historical.Check(id, historical ? 1UL : 0UL, wrote))
                {
                    found(e, ActionKind.SetHistorical, new ActionIntent { Kind = ActionKind.SetHistorical, Target = id, Amount = historical ? 1 : 0 });
                }
            }
            if (em.HasBuffer<Game.Routes.VehicleModel>(e))
            {
                uint[] pairs = ReadVehicles(em, e, out bool complete);
                if (complete && _vehicles.Check(id, Hash(pairs), wrote))
                {
                    found(e, ActionKind.SetVehicleModels, new ActionIntent { Kind = ActionKind.SetVehicleModels, Target = id, Ids = pairs });
                }
            }
            if (em.HasBuffer<ServiceDistrict>(e))
            {
                uint[] districts = ReadDistricts(em, e, out bool complete);
                if (complete && _districts.Check(id, Hash(districts), wrote))
                {
                    found(e, ActionKind.SetServiceDistricts, new ActionIntent { Kind = ActionKind.SetServiceDistricts, Target = id, Ids = districts });
                }
            }
        }

        /// <summary>Custom mesh colours: every vanilla writer adds <c>BatchesUpdated</c> (VisualCustomizeSection.cs:101-107, 255-267).</summary>
        private void WatchMeshColors(HashSet<Entity> hostWrote, Action<Entity, ActionIntent> found)
        {
            EntityManager em = _world.EntityManager;
            if (!_meshPrimed)
            {
                // custom colours that existed before this session part are the baseline, not edits
                _meshPrimed = true;
                NativeArray<Entity> colored = _meshEnabled.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (Entity e in colored)
                    {
                        uint id = _ids.GetNetId(e);
                        if (id != 0) _meshState[id] = MeshState(em, e, out _);
                    }
                }
                finally
                {
                    colored.Dispose();
                }
            }
            if (_meshDirty.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> dirty = _meshDirty.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in dirty)
                {
                    uint id = _ids.GetNetId(e);
                    if (id == 0 || em.HasComponent<Deleted>(e)) continue;
                    ulong state = MeshState(em, e, out float[] values);
                    ulong old = _meshState.TryGetValue(id, out ulong o) ? o : 0UL;
                    if (state == 0) _meshState.Remove(id);
                    else _meshState[id] = state;
                    if (state == old || (hostWrote != null && hostWrote.Contains(e))) continue;
                    found(e, new ActionIntent { Kind = ActionKind.SetMeshColor, Target = id, Values = values });
                }
            }
            finally
            {
                dirty.Dispose();
            }
        }

        /// <summary>Line colours: the vanilla handlers create an <c>Event+ColorUpdated</c> (ColorSection.cs:51-52, TransportationOverviewUISystem).</summary>
        private void WatchRouteColors(HashSet<Entity> hostWrote, Action<Entity, ActionIntent> found)
        {
            if (_colorEvents.IsEmptyIgnoreFilter) return;
            EntityManager em = _world.EntityManager;
            NativeArray<Game.Routes.ColorUpdated> events = _colorEvents.ToComponentDataArray<Game.Routes.ColorUpdated>(Allocator.Temp);
            try
            {
                var seen = new HashSet<Entity>();
                foreach (Game.Routes.ColorUpdated u in events)
                {
                    Entity route = u.m_Route;
                    if (!seen.Add(route) || route == Entity.Null || !em.Exists(route) || !em.HasComponent<Game.Routes.Color>(route)) continue;
                    if (hostWrote != null && hostWrote.Contains(route)) continue;
                    uint id = _ids.GetNetId(route);
                    if (id == 0) continue;
                    Color32 c = em.GetComponentData<Game.Routes.Color>(route).m_Color;
                    found(route, new ActionIntent { Kind = ActionKind.SetRouteColor, Target = id, Values = new float[] { c.r, c.g, c.b, c.a } });
                }
            }
            finally
            {
                events.Dispose();
            }
        }

        // ================================================================== host

        /// <summary>
        /// Host, end of frame: the host player's own direct writes (same watchers; the entities are marked authored so
        /// the capture streams them), the unlocked-prefab set, and the sub-element deletions to replay on clients.
        /// <paramref name="proposalWrote"/>: entities a proposal or intent wrote this frame (not the host player's edits).
        /// </summary>
        public void HostWatch(HashSet<Entity> proposalWrote)
        {
            if (!Attached) return;
            _watchWrote = proposalWrote;
            _watchForHost = true;
            Guard("host selection", _watchSelectionCall);
            Guard("host mesh colours", _watchMeshCall);
            Guard("host line colours", _watchRoutesCall);
            _watchWrote = null;
        }

        /// <summary>Host, global batch of the frame: sub-element deletions to replay on clients and the unlock set.</summary>
        public void HostCapture(SyncBatch.Writer w, double now)
        {
            if (!Attached) return;
            if (_subElements.Count > 0)
            {
                _broadcast.Add(new ActionIntent { Kind = ActionKind.DeleteSubElements, Target = PeerId.Host.Value, Keys = _subElements.ToArray() });
                _subElements.Clear();
            }
            if (_broadcast.Count > 0)
            {
                try
                {
                    byte[] payload = ActionPayload.Write(_broadcast);
                    w.Global(GlobalChannel.Action, payload, 0, payload.Length);
                }
                catch (Exception ex)
                {
                    Once("broadcast", "action sync: writing host actions failed", ex);
                }
                _broadcast.Clear();
            }
            bool due = now - _lastUnlock >= UnlockInterval;
            if (!due && !_unlockGate.IsDirty(GlobalChannel.Unlocks)) return;
            if (due) _lastUnlock = now;
            Guard("unlocks", () =>
            {
                byte[] payload = UnlockSet.Write(ReadUnlocked());
                if (_unlockGate.ShouldSend(GlobalChannel.Unlocks, XxHash64.Hash(payload, 0, payload.Length), now)) w.Global(GlobalChannel.Unlocks, payload, 0, payload.Length);
            });
        }

        /// <summary>Host, late phase (after <c>AllowBarrier&lt;EndFrameBarrier&gt;</c>): executors that use <c>EndFrameBarrier</c>.</summary>
        public void RunLate()
        {
            if (!Attached || _lateDevTree.Count == 0) return;
            EntityManager em = _world.EntityManager;
            foreach ((PeerId peer, uint nodeId) in _lateDevTree)
            {
                try
                {
                    Entity node = _ids.GetEntity(nodeId);
                    if (node == Entity.Null || !em.Exists(node) || !em.HasComponent<DevTreeNodeData>(node) || !em.HasComponent<Locked>(node))
                    {
                        Reject?.Invoke(peer, RejectReason.Invalid, Array.Empty<uint>());
                        continue;
                    }
                    if (!em.IsComponentEnabled<Locked>(node)) continue; // already bought (by someone else): nothing to do
                    if (em.GetComponentData<DevTreeNodeData>(node).m_Cost > ReadPoints())
                    {
                        Reject?.Invoke(peer, RejectReason.NotEnoughMoney, Array.Empty<uint>());
                        continue;
                    }
                    // validates points, lock state, service and requirements itself (DevTreeSystem.cs:157-175)
                    _devTree.Purchase(node);
                    _log.Info(LogCategory.Replication, peer + " bought dev-tree node #" + nodeId);
                }
                catch (Exception ex)
                {
                    Once("dev tree", "action sync: a dev-tree purchase failed", ex);
                    Reject?.Invoke(peer, RejectReason.HostError, Array.Empty<uint>());
                }
            }
            _lateDevTree.Clear();
            // the unlock set goes out again either way, so a refused prediction is corrected quickly
            ResendUnlocks();
        }

        /// <summary>Host: a client's <see cref="GlobalChannel.Action"/> payload (frame start).</summary>
        public void ApplyProposal(PeerId peer, byte[] payload)
        {
            if (!Attached) return;
            List<ActionIntent> intents = ActionPayload.Read(payload, 0, payload.Length);
            EntityManager em = _world.EntityManager;
            foreach (ActionIntent a in intents)
            {
                RejectReason reason = ActionPayload.Validate(a);
                if (reason == RejectReason.None && Allows != null && !Allows(peer, ActionPayload.Required(a.Kind))) reason = RejectReason.NoPermission;
                if (reason == RejectReason.None)
                {
                    try
                    {
                        reason = Execute(peer, em, a);
                    }
                    catch (Exception ex)
                    {
                        Once("execute " + a.Kind, "action sync: executing " + a.Kind + " failed", ex);
                        reason = RejectReason.HostError;
                    }
                }
                if (reason == RejectReason.None) continue;
                _log.Info(LogCategory.Replication, peer + ": refused " + a + " (" + reason + ")");
                var ids = new List<uint>();
                if (a.Target != 0 && a.Kind != ActionKind.DevTreePurchase) ids.Add(a.Target);
                ids.AddRange(a.Ids.Length > 0 && a.Kind == ActionKind.PurchaseMapTiles ? a.Ids : Array.Empty<uint>());
                foreach (SubElementKey k in a.Keys) ids.Add(k.OwnerNetId);
                Reject?.Invoke(peer, reason, ids);
                if (a.Kind == ActionKind.DevTreePurchase) ResendUnlocks();
            }
        }

        private RejectReason Execute(PeerId peer, EntityManager em, ActionIntent a)
        {
            Entity target = a.Target != 0 ? _ids.GetEntity(a.Target) : Entity.Null;
            bool live = target != Entity.Null && em.Exists(target) && !em.HasComponent<Deleted>(target);
            switch (a.Kind)
            {
                case ActionKind.DevTreePurchase:
                    _lateDevTree.Add((peer, a.Target));
                    return RejectReason.None;
                case ActionKind.PurchaseMapTiles:
                    return PurchaseTiles(peer, em, a);
                case ActionKind.SetHistorical:
                {
                    if (!live || !em.HasComponent<Game.Buildings.Building>(target)) return RejectReason.Conflict;
                    Game.Buildings.Building b = em.GetComponentData<Game.Buildings.Building>(target);
                    if (a.Amount != 0) b.m_Flags |= Game.Buildings.BuildingFlags.Historical;
                    else b.m_Flags &= ~Game.Buildings.BuildingFlags.Historical;
                    em.SetComponentData(target, b); // VisualCustomizeSection.cs:75-83 writes it the same way, untagged
                    Touch?.Invoke(target, peer);
                    return RejectReason.None;
                }
                case ActionKind.SetVehicleModels:
                {
                    if (!live || !em.HasBuffer<Game.Routes.VehicleModel>(target)) return RejectReason.Conflict;
                    var models = new List<Game.Routes.VehicleModel>();
                    for (int i = 0; i + 1 < a.Ids.Length; i += 2)
                    {
                        Entity primary = Prefab(em, a.Ids[i]);
                        Entity secondary = Prefab(em, a.Ids[i + 1]);
                        if ((a.Ids[i] != 0 && primary == Entity.Null) || (a.Ids[i + 1] != 0 && secondary == Entity.Null)) return RejectReason.Invalid;
                        models.Add(new Game.Routes.VehicleModel { m_PrimaryPrefab = primary, m_SecondaryPrefab = secondary });
                    }
                    DynamicBuffer<Game.Routes.VehicleModel> buffer = em.GetBuffer<Game.Routes.VehicleModel>(target);
                    buffer.Clear();
                    foreach (Game.Routes.VehicleModel m in models) buffer.Add(m);
                    Touch?.Invoke(target, peer);
                    return RejectReason.None;
                }
                case ActionKind.SetServiceDistricts:
                {
                    if (!live || !em.HasBuffer<ServiceDistrict>(target)) return RejectReason.Conflict;
                    var districts = new List<Entity>();
                    foreach (uint id in a.Ids)
                    {
                        Entity d = _ids.GetEntity(id);
                        if (d == Entity.Null || !em.Exists(d) || em.HasComponent<Deleted>(d) || !em.HasComponent<District>(d)) return RejectReason.Conflict;
                        if (!districts.Contains(d)) districts.Add(d);
                    }
                    DynamicBuffer<ServiceDistrict> buffer = em.GetBuffer<ServiceDistrict>(target);
                    buffer.Clear();
                    foreach (Entity d in districts) buffer.Add(new ServiceDistrict(d));
                    // the selection tool tags the owner Updated (SelectionToolSystem.cs:421)
                    if (!em.HasComponent<Updated>(target)) em.AddComponent<Updated>(target);
                    Touch?.Invoke(target, peer);
                    return RejectReason.None;
                }
                case ActionKind.SetMeshColor:
                {
                    if (!live || !em.HasBuffer<CustomMeshColor>(target)) return RejectReason.Conflict;
                    DynamicBuffer<CustomMeshColor> buffer = em.GetBuffer<CustomMeshColor>(target);
                    buffer.Clear();
                    if (a.Values.Length == 12)
                    {
                        float[] v = a.Values;
                        buffer.Add(new CustomMeshColor
                        {
                            m_ColorSet = new ColorSet
                            {
                                m_Channel0 = new UnityEngine.Color(v[0], v[1], v[2], v[3]),
                                m_Channel1 = new UnityEngine.Color(v[4], v[5], v[6], v[7]),
                                m_Channel2 = new UnityEngine.Color(v[8], v[9], v[10], v[11]),
                            },
                        });
                    }
                    MeshColorWritten(em, target);
                    Touch?.Invoke(target, peer);
                    return RejectReason.None;
                }
                case ActionKind.SetRouteColor:
                {
                    if (!live || !em.HasComponent<Game.Routes.Color>(target)) return RejectReason.Conflict;
                    var color = new Color32(ToByte(a.Values[0]), ToByte(a.Values[1]), ToByte(a.Values[2]), ToByte(a.Values[3]));
                    em.SetComponentData(target, new Game.Routes.Color(color));
                    RouteColorWritten(em, target, _colorArchetype);
                    Touch?.Invoke(target, peer);
                    return RejectReason.None;
                }
                case ActionKind.DeleteSubElements:
                {
                    int deleted = 0;
                    foreach (SubElementKey k in a.Keys)
                    {
                        Entity e = ResolveStrict(k);
                        if (e == Entity.Null || em.HasComponent<Deleted>(e)) continue;
                        em.AddComponent<Deleted>(e);
                        deleted++;
                    }
                    if (deleted > 0) _broadcast.Add(new ActionIntent { Kind = ActionKind.DeleteSubElements, Target = peer.Value, Keys = a.Keys });
                    return RejectReason.None;
                }
                default:
                    return RejectReason.Invalid;
            }
        }

        private RejectReason PurchaseTiles(PeerId peer, EntityManager em, ActionIntent a)
        {
            var tiles = new List<Entity>();
            var seen = new HashSet<Entity>();
            foreach (uint id in a.Ids)
            {
                Entity t = _ids.GetEntity(id);
                if (t == Entity.Null || !em.Exists(t) || !em.HasComponent<MapTile>(t) || em.HasComponent<Deleted>(t)) return RejectReason.Invalid;
                if (em.HasComponent<Native>(t) && seen.Add(t)) tiles.Add(t);
            }
            if (tiles.Count == 0) return RejectReason.None; // somebody bought them already: nothing to charge
            Entity city = _city.City;
            if (city == Entity.Null || !em.HasComponent<PlayerMoney>(city)) return RejectReason.HostError;
            PlayerMoney money = em.GetComponentData<PlayerMoney>(city);
            // w9/perf (trust): the host prices the tiles from its own world; the client's observed cost is only logged
            int cost = PriceTiles(em, tiles);
            // permits as the host's own purchase computes them (milestones, mods patching it)
            RejectReason verdict = MapTilePricing.Validate(tiles.Count, _mapTiles.GetAvailableTiles(), cost, money.money, money.m_Unlimited);
            if (verdict != RejectReason.None) return verdict;
            if (MapTilePricing.Differs(a.Amount, cost)) _log.Info(LogCategory.Replication, peer + ": map tiles priced at " + cost + " by the host (the client saw " + a.Amount + ")");
            money.Subtract(cost); // MapTilePurchaseSystem.PurchaseSelection (:311-344) without the host's selection tool
            em.SetComponentData(city, money);
            foreach (Entity t in tiles)
            {
                MapTilePurchaseSystem.UnlockTile(em, t);
                Touch?.Invoke(t, peer);
            }
            _log.Info(LogCategory.Replication, peer + " bought " + tiles.Count + " map tile(s) for " + cost);
            return RejectReason.None;
        }

        /// <summary>
        /// w9/perf (trust): the price of buying <paramref name="tiles"/> (all still unowned) with the host's current owned
        /// count, feature amounts and prefab costs (<see cref="MapTilePricing"/>).
        /// </summary>
        private int PriceTiles(EntityManager em, List<Entity> tiles)
        {
            _tileValues.Clear();
            foreach (Entity t in tiles)
            {
                if (!em.HasBuffer<MapFeatureElement>(t) || !em.HasComponent<PrefabRef>(t)) continue; // the game prices such a tile at nothing, too
                Entity prefab = em.GetComponentData<PrefabRef>(t).m_Prefab;
                if (prefab == Entity.Null || !em.HasBuffer<MapFeatureData>(prefab) || !em.HasComponent<TilePurchaseCostFactor>(prefab)) continue;
                DynamicBuffer<MapFeatureElement> amounts = em.GetBuffer<MapFeatureElement>(t, true);
                DynamicBuffer<MapFeatureData> costs = em.GetBuffer<MapFeatureData>(prefab, true);
                _featureAmounts.Clear();
                _featureCosts.Clear();
                for (int j = 0; j < amounts.Length; j++) _featureAmounts.Add(amounts[j].m_Amount);
                for (int j = 0; j < costs.Length; j++) _featureCosts.Add(costs[j].m_Cost);
                _tileValues.Add(MapTilePricing.TileValue(_featureAmounts, _featureCosts, em.GetComponentData<TilePurchaseCostFactor>(prefab).m_Amount));
            }
            return MapTilePricing.Cost(_tileValues, _ownedTiles.CalculateEntityCount());
        }

        // ================================================================== client: host channels

        /// <summary>Client: a host channel this class owns; false when <paramref name="channel"/> is not one of them.</summary>
        public bool ApplyFromHost(uint channel, byte[] payload)
        {
            if (channel == GlobalChannel.Unlocks)
            {
                if (Attached) ApplyUnlocks(UnlockSet.Read(payload, 0, payload.Length));
                return true;
            }
            if (channel == GlobalChannel.Action)
            {
                if (Attached) ApplyHostActions(ActionPayload.Read(payload, 0, payload.Length));
                return true;
            }
            return false;
        }

        private void ApplyUnlocks(List<uint> hostUnlocked)
        {
            EntityManager em = _world.EntityManager;
            var local = new HashSet<uint>();
            var known = new HashSet<uint>();
            NativeArray<Entity> prefabs = _lockable.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity p in prefabs)
                {
                    uint id = _ids.GetNetId(p);
                    if (id == 0 || IsTutorial(em, p)) continue;
                    known.Add(id);
                    if (!em.IsComponentEnabled<Locked>(p)) local.Add(id);
                }
            }
            finally
            {
                prefabs.Dispose();
            }
            var unlock = new List<uint>();
            var relock = new List<uint>();
            UnlockSet.Diff(hostUnlocked, local, known, unlock, relock);
            foreach (uint id in unlock)
            {
                // vanilla UnlockSystem (kept on) flips the bit and every UI consumer refreshes (ARCH §4.10.3)
                Entity prefab = _ids.GetEntity(id);
                Entity ev = em.CreateEntity(_unlockArchetype);
                em.SetComponentData(ev, new Unlock(prefab));
                _injectedUnlocks.Add(prefab);
            }
            // a purchase this player predicted stays unlocked until the host had time to commit it (no flicker)
            if (UnityEngine.Time.realtimeSinceStartupAsDouble < _relockHoldUntil) relock.Clear();
            foreach (uint id in relock) em.SetComponentEnabled<Locked>(_ids.GetEntity(id), true);
            if (unlock.Count + relock.Count > 0) _log.Info(LogCategory.Replication, "unlocks from the host: +" + unlock.Count + " -" + relock.Count);
        }

        private void ApplyHostActions(List<ActionIntent> intents)
        {
            EntityManager em = _world.EntityManager;
            byte me = LocalPeer != null ? LocalPeer().Value : (byte)0;
            foreach (ActionIntent a in intents)
            {
                if (a.Kind != ActionKind.DeleteSubElements || a.Target == me) continue;
                foreach (SubElementKey k in a.Keys)
                {
                    Entity e = ResolveStrict(k);
                    if (e != Entity.Null && !em.HasComponent<Deleted>(e)) em.AddComponent<Deleted>(e);
                }
            }
        }

        // ================================================================== apply fix-ups (EntityCodec hook)

        /// <summary>
        /// Called by the applier after it wrote a component: state that vanilla keeps beside the component value
        /// (the enable bit and render refresh of custom mesh colours; the line colour on the line's vehicles and the
        /// <c>ColorUpdated</c> event the renderers wait for).
        /// </summary>
        public static void AfterWrite(EntityManager em, Entity e, Type type, bool valueChanged, EntityArchetype colorArchetype)
        {
            if (type == typeof(CustomMeshColor))
            {
                MeshColorWritten(em, e);
            }
            else if (type == typeof(Game.Routes.Color) && valueChanged && em.HasComponent<Game.Routes.Route>(e))
            {
                RouteColorWritten(em, e, colorArchetype);
            }
        }

        private static void MeshColorWritten(EntityManager em, Entity e)
        {
            if (!em.HasBuffer<CustomMeshColor>(e)) return;
            int length = em.GetBuffer<CustomMeshColor>(e, true).Length;
            em.SetComponentEnabled<CustomMeshColor>(e, length > 0);
            if (!em.HasComponent<BatchesUpdated>(e)) em.AddComponent<BatchesUpdated>(e);
        }

        private static void RouteColorWritten(EntityManager em, Entity route, EntityArchetype colorArchetype)
        {
            Game.Routes.Color color = em.GetComponentData<Game.Routes.Color>(route);
            if (em.HasBuffer<Game.Routes.RouteVehicle>(route))
            {
                DynamicBuffer<Game.Routes.RouteVehicle> vehicles = em.GetBuffer<Game.Routes.RouteVehicle>(route, true);
                var list = new List<Entity>(vehicles.Length);
                for (int i = 0; i < vehicles.Length; i++) list.Add(vehicles[i].m_Vehicle);
                foreach (Entity v in list)
                {
                    if (v == Entity.Null || !em.Exists(v)) continue;
                    if (em.HasComponent<Game.Routes.Color>(v)) em.SetComponentData(v, color);
                    else em.AddComponentData(v, color);
                }
            }
            if (colorArchetype.Valid)
            {
                Entity ev = em.CreateEntity(colorArchetype);
                em.SetComponentData(ev, new Game.Routes.ColorUpdated(route));
            }
        }

        /// <summary>The event archetype the applier uses for <see cref="AfterWrite"/>.</summary>
        public EntityArchetype ColorEventArchetype => _colorArchetype;

        // ================================================================== helpers

        private List<uint> ReadUnlocked()
        {
            EntityManager em = _world.EntityManager;
            var list = new List<uint>();
            NativeArray<Entity> prefabs = _lockable.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity p in prefabs)
                {
                    if (em.IsComponentEnabled<Locked>(p) || IsTutorial(em, p)) continue;
                    uint id = _ids.GetNetId(p);
                    if (id != 0) list.Add(id);
                }
            }
            finally
            {
                prefabs.Dispose();
            }
            list.Sort();
            for (int i = list.Count - 1; i > 0; i--)
            {
                if (list[i] == list[i - 1]) list.RemoveAt(i);
            }
            return list;
        }

        private readonly Dictionary<EntityArchetype, bool> _tutorialArchetypes = new Dictionary<EntityArchetype, bool>();

        /// <summary>
        /// Tutorial prefabs are unlocked by each player's own tutorial progress (TutorialSystem.ManualUnlock), so they
        /// stay out of the shared unlock set (ARCH §4.10.3, R10 F3).
        /// </summary>
        private bool IsTutorial(EntityManager em, Entity prefab)
        {
            EntityArchetype archetype = em.GetChunk(prefab).Archetype;
            if (_tutorialArchetypes.TryGetValue(archetype, out bool tutorial)) return tutorial;
            NativeArray<ComponentType> types = archetype.GetComponentTypes(Allocator.Temp);
            try
            {
                foreach (ComponentType t in types)
                {
                    Type type = TypeManager.GetType(t.TypeIndex);
                    if (type != null && type.Namespace == "Game.Tutorials")
                    {
                        tutorial = true;
                        break;
                    }
                }
            }
            finally
            {
                types.Dispose();
            }
            _tutorialArchetypes[archetype] = tutorial;
            return tutorial;
        }

        private Entity ResolveStrict(SubElementKey k)
        {
            DerivedKey key = new DerivedKey { Kind = k.Kind, OwnerNetId = k.OwnerNetId, PrefabNetId = k.PrefabNetId, Index = k.Index, X = k.X, Y = k.Y, Z = k.Z };
            Entity e = _codec.ResolveDerivedKey(key);
            if (e == Entity.Null) return Entity.Null;
            // the resolver falls back to the nearest candidate within 4 m; a deletion needs the same element (≤ 1 m)
            if (!_codec.TryGetPortableRef(e, out uint id, out DerivedKey found) || id != 0) return Entity.Null;
            if (found.Kind != k.Kind || found.PrefabNetId != k.PrefabNetId || found.OwnerNetId != k.OwnerNetId) return Entity.Null;
            if (Math.Abs((long)found.X - k.X) > 64 || Math.Abs((long)found.Y - k.Y) > 64 || Math.Abs((long)found.Z - k.Z) > 64) return Entity.Null;
            return e;
        }

        private static SubElementKey ToWire(DerivedKey k) => new SubElementKey { Kind = k.Kind, OwnerNetId = k.OwnerNetId, PrefabNetId = k.PrefabNetId, Index = k.Index, X = k.X, Y = k.Y, Z = k.Z };

        private Entity Prefab(EntityManager em, uint id)
        {
            if (id == 0) return Entity.Null;
            Entity e = _ids.GetEntity(id);
            return e != Entity.Null && em.Exists(e) && em.HasComponent<PrefabData>(e) ? e : Entity.Null;
        }

        private uint[] ReadVehicles(EntityManager em, Entity e, out bool complete)
        {
            complete = true;
            DynamicBuffer<Game.Routes.VehicleModel> buffer = em.GetBuffer<Game.Routes.VehicleModel>(e, true);
            var ids = new uint[buffer.Length * 2];
            for (int i = 0; i < buffer.Length; i++)
            {
                ids[i * 2] = Ref(buffer[i].m_PrimaryPrefab, ref complete);
                ids[i * 2 + 1] = Ref(buffer[i].m_SecondaryPrefab, ref complete);
            }
            return ids;
        }

        private uint[] ReadDistricts(EntityManager em, Entity e, out bool complete)
        {
            complete = true;
            DynamicBuffer<ServiceDistrict> buffer = em.GetBuffer<ServiceDistrict>(e, true);
            var ids = new uint[buffer.Length];
            for (int i = 0; i < buffer.Length; i++) ids[i] = Ref(buffer[i].m_District, ref complete);
            return ids;
        }

        private uint Ref(Entity e, ref bool complete)
        {
            if (e == Entity.Null) return 0;
            uint id = _ids.GetNetId(e);
            if (id == 0) complete = false;
            return id;
        }

        private static ulong Hash(uint[] values)
        {
            ulong h = 1469598103934665603UL;
            foreach (uint v in values) h = (h ^ v) * 1099511628211UL;
            return h ^ (ulong)values.Length;
        }

        /// <summary>0 for "no custom colour"; otherwise a hash of the colour set (values: the 12 floats).</summary>
        private static ulong MeshState(EntityManager em, Entity e, out float[] values)
        {
            values = Array.Empty<float>();
            if (!em.IsComponentEnabled<CustomMeshColor>(e)) return 0;
            DynamicBuffer<CustomMeshColor> buffer = em.GetBuffer<CustomMeshColor>(e, true);
            if (buffer.Length == 0) return 0;
            ColorSet set = buffer[0].m_ColorSet;
            values = new[]
            {
                set.m_Channel0.r, set.m_Channel0.g, set.m_Channel0.b, set.m_Channel0.a,
                set.m_Channel1.r, set.m_Channel1.g, set.m_Channel1.b, set.m_Channel1.a,
                set.m_Channel2.r, set.m_Channel2.g, set.m_Channel2.b, set.m_Channel2.a,
            };
            ulong h = 1469598103934665603UL;
            foreach (float f in values) h = (h ^ (uint)BitConverter.ToInt32(BitConverter.GetBytes(f), 0)) * 1099511628211UL;
            return h == 0 ? 1 : h;
        }

        private bool ReadMoney(out int money, out bool unlimited)
        {
            money = 0;
            unlimited = false;
            EntityManager em = _world.EntityManager;
            Entity city = _city.City;
            if (city == Entity.Null || !em.Exists(city) || !em.HasComponent<PlayerMoney>(city)) return false;
            PlayerMoney m = em.GetComponentData<PlayerMoney>(city);
            money = m.money;
            unlimited = m.m_Unlimited;
            return true;
        }

        private int ReadPoints()
        {
            EntityManager em = _world.EntityManager;
            Entity city = _city.City;
            if (city == Entity.Null || !em.Exists(city) || !em.HasComponent<DevTreePoints>(city)) return 0;
            return em.GetComponentData<DevTreePoints>(city).m_Points;
        }

        private static byte ToByte(float v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v)));

        private void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Once("guard " + what, "action sync: " + what + " failed", ex);
            }
        }

        private void Once(string key, string message, Exception ex)
        {
            if (_once.Add(key)) _log.Warn(LogCategory.Replication, message, ex);
        }
    }
}
