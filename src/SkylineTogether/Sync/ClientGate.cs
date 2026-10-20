using System;
using System.Collections.Generic;
using SkylineTogether.Core;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// Follower gating (ARCHITECTURE.md#gated-systems, R11): systems that would overwrite, append to or randomise host-owned data on a
    /// client. Clients never simulate (their simulation stays paused and their clock follows the host), so what is
    /// left are initializers that run on replicated entities when they appear, progression writers outside the
    /// simulation, and the line systems whose results are host data. Systems are found by name, so a game patch that
    /// renames one only costs a log line.
    /// </summary>
    public sealed class ClientGate
    {
        private static readonly string[] Gated =
        {
            "Game.Citizens.CitizenInitializeSystem",
            "Game.Citizens.HouseholdInitializeSystem",
            "Game.Citizens.CompanyInitializeSystem",
            "Game.Citizens.StorageInitializeSystem",
            "Game.Citizens.HouseholdPetInitializeSystem",
            "Game.Citizens.MeetingInitializeSystem",
            "Game.Policies.DefaultPoliciesSystem",
            "Game.Routes.InitializeSystem",
            "Game.Common.RandomLocalizationInitializeSystem",
            "Game.Objects.OutsideConnectionInitializeSystem",
            "Game.Buildings.ResourcesInitializeSystem",
            "Game.Buildings.ParkInitializeSystem",
            "Game.Events.EventJournalInitializeSystem",
            "Game.Tools.UpgradeDeletedSystem",
            "Game.Simulation.XPBuiltSystem",
            "Game.Simulation.NetXPSystem",
            "Game.Simulation.XPSystem",
            "Game.Simulation.MilestoneSystem",
            "Game.Buildings.ZoneCheckSystem",
            "Game.City.DevTreeSystem",
            "Game.AutoSaveSystem",
            // writes nothing but warning icons, which come from the host (IconSync)
            "Game.Net.ConnectionWarningSystem",
            // event entities come from the host; its initializer would ignite, damage and pick random targets
            "Game.Events.InitializeSystem",
            // CreateChirpSystem is NOT gated: TriggerSystem and LifePathEventSystem write into its queue and the game asserts
            // that it runs (in-game crash report). ChirpDrainSystem empties the queue right before it, so no local chirps.
            // cascades the host sends itself (every despawn, every changed reverse buffer, ARCHITECTURE.md#agents)
            "Game.Citizens.HouseholdAndCitizenRemoveSystem",
            "Game.Citizens.HouseholdPetRemoveSystem",
            "Game.Simulation.RemovedSystem",
            "Game.Buildings.SchoolUpdatedSystem",
            "Game.Events.AddHealthProblemSystem",
            // Modification2, not GameSimulation, so the follower clock leaves it on. On
            // terrain-change bounds (buildings, flatten/clip roads, terrain areas, TerrainSync tile copies) it re-samples
            // edge Curve y, node y, on-ground object Transform and area node y from this client's own height cascade,
            // which each peer renders itself from local lots and road clipping (16-bit heights: one step is 6 cm, the
            // write threshold 1 cm). It overwrote the host's values a few frames after every repair ("differs in:
            // Game.Net.Curve (value)", repaired again and again). The host's own results replicate with Updated tags.
            "Game.Simulation.GroundHeightSystem",
            // Modification2, not GameSimulation. The selected object's "traffic routes" (a building's arriving
            // vehicles and citizens, a vehicle's own route) are LivePath route entities whose segment curves
            // SegmentCurveSystem rebuilds every frame from each source's PathOwner/PathElement, *CurrentLane and
            // *NavigationLane data (TrafficRoutesSystem.FindPathSourcesJob, SegmentCurveSystem.UpdateSegmentCurvesJob).
            // A follower never advances that data: movers from the snapshot keep their path and target of the join
            // moment while motion moves them elsewhere, movers spawned later have none, so the routes started where a
            // vehicle used to be, led to places it no longer heads for, and lanes rebuilt since were skipped and joined
            // by straight connectors across the map. Off on clients (no route is better than a wrong one); the
            // LivePath entities that exist when the gate closes are removed (RemoveLivePaths).
            "Game.Tools.TrafficRoutesSystem",
        };

        /// <summary>Line systems: off on clients except inside a route-tool preview window.</summary>
        private static readonly string[] RouteSystems =
        {
            "Game.Routes.RoutePathSystem",
            "Game.Routes.WaypointConnectionSystem",
        };

        /// <summary>
        /// The rest of the route-tool preview pipeline. On a client the preview of a segment is computed as on the
        /// host (decompiled <c>Game.Common/SystemOrder.cs</c>): the route tool's temp waypoints get their lanes from
        /// <c>WaypointConnectionSystem</c> (Modification5, gated above), <c>RoutePathSystem</c> (ModificationEnd, gated
        /// above) enqueues a high-priority <c>PathfindAction</c> with the <c>uint.MaxValue</c> deadline
        /// (<c>RoutePathSystem.SetupPathfind</c>), <c>PathfindQueueSystem</c> (MainLoop, before) solves it on the client's
        /// own lane graph, which <c>LanesModifiedSystem</c>/<c>LaneDataSystem</c> (ModificationEnd) keep from the locally
        /// generated lanes, <c>PathfindResultSystem</c> (MainLoop) writes the path and a <c>PathUpdated</c> event, and
        /// <c>RoutePathReadySystem</c> (Modification1) and <c>SegmentCurveSystem</c> (Modification5) turn it into the
        /// curve the overlay draws (<c>Routes.SearchSystem</c> indexes it for snapping). None of these is a
        /// <c>GameSimulation</c> system, so the follower clock never turns them off, and no gate holds them; the client
        /// solves no other path queries (simulation queries are all GameSimulation), so the high-priority preview
        /// actions are not starved. Should anything else (another mod) have turned one off, the window turns it on for
        /// its duration and back off afterwards, so the preview follows the roads.
        /// </summary>
        private static readonly string[] PreviewPipeline =
        {
            "Game.Pathfind.PathfindQueueSystem",
            "Game.Pathfind.PathfindResultSystem",
            "Game.Pathfind.LanesModifiedSystem",
            "Game.Pathfind.LaneDataSystem",
            "Game.Routes.RoutePathReadySystem",
            "Game.Routes.SegmentCurveSystem",
            "Game.Routes.SearchSystem",
        };

        /// <summary>The route preview pipeline's system names (probed at startup).</summary>
        public static IReadOnlyList<string> PreviewPipelineNames => PreviewPipeline;

        /// <summary>The gated system names (probed at startup, <see cref="Diagnostics.Probes"/>).</summary>
        public static IReadOnlyList<string> GatedSystemNames => Gated;

        /// <summary>The line system names (probed at startup).</summary>
        public static IReadOnlyList<string> RouteSystemNames => RouteSystems;

        private readonly ILog _log;
        private readonly List<(ComponentSystemBase System, bool WasEnabled)> _disabled = new List<(ComponentSystemBase, bool)>();
        private readonly List<ComponentSystemBase> _routeSystems = new List<ComponentSystemBase>();
        private readonly List<ComponentSystemBase> _pipelineForced = new List<ComponentSystemBase>();
        private bool _closed;

        public ClientGate(ILog log)
        {
            _log = log;
        }

        /// <summary>Turns the gated systems off (client becomes a follower).</summary>
        public void Close()
        {
            if (_closed) return;
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return; // not closed: a later call (with a world) applies the gates
            _closed = true;
            int found = 0;
            foreach (string name in Gated) found += Disable(world, name, false) ? 1 : 0;
            foreach (string name in RouteSystems) found += Disable(world, name, true) ? 1 : 0;
            int livePaths = RemoveLivePaths(world);
            _log.Info(LogCategory.Gate, "follower gate closed: " + found + " of " + (Gated.Length + RouteSystems.Length) + " systems off"
                                        + (livePaths > 0 ? ", " + livePaths + " traffic route entities removed" : ""));
        }

        /// <summary>
        /// Marks every selected-object traffic route entity (route, waypoints, segments: all carry <c>LivePath</c>,
        /// <c>LivePathPrefab.GetArchetypeComponents</c>) <c>Deleted</c>, as <c>TrafficRoutesSystem</c> itself does when
        /// nothing is selected; with that system off on the client nothing else would ever remove them.
        /// </summary>
        private int RemoveLivePaths(World world)
        {
            try
            {
                EntityManager em = world.EntityManager;
                using (EntityQuery live = em.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<Game.Routes.LivePath>() },
                    None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>() },
                }))
                {
                    int count = live.CalculateEntityCount();
                    if (count > 0) em.AddComponent(live, ComponentType.ReadWrite<Game.Common.Deleted>());
                    return count;
                }
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Gate, "removing the traffic route entities failed: " + ex.Message);
                return 0;
            }
        }

        /// <summary>Restores every system to its previous state (session ended).</summary>
        public void Open()
        {
            if (!_closed) return;
            _closed = false;
            RouteWindowOpen = false;
            foreach ((ComponentSystemBase system, bool wasEnabled) in _disabled)
            {
                try
                {
                    SystemSwitch.Restore(system, wasEnabled);
                }
                catch (Exception)
                {
                    // the world may be gone
                }
            }
            _disabled.Clear();
            _routeSystems.Clear();
            ReleasePipeline();
            _log.Info(LogCategory.Gate, "follower gate open");
        }

        /// <summary>True while the route-tool preview window is open.</summary>
        public bool RouteWindowOpen { get; private set; }

        public void SetRouteWindow(bool open)
        {
            if (!_closed || open == RouteWindowOpen) return;
            RouteWindowOpen = open;
            foreach (ComponentSystemBase s in _routeSystems) s.Enabled = open;
            if (open) EnsurePipeline();
            else ReleasePipeline();
            _log.Debug(LogCategory.Gate, "route preview window " + (open ? "open" : "closed"));
        }

        /// <summary>Turns on, for the window, every preview pipeline system something else turned off.</summary>
        private void EnsurePipeline()
        {
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return;
            foreach (string name in PreviewPipeline)
            {
                try
                {
                    Type type = typeof(Game.Common.Owner).Assembly.GetType(name, false);
                    ComponentSystemBase system = type == null ? null : world.GetExistingSystemManaged(type);
                    if (system == null || system.Enabled) continue;
                    system.Enabled = true;
                    _pipelineForced.Add(system);
                    _log.Warn(LogCategory.Gate, name + " was off; on for the route preview window (the preview would not follow the roads)");
                }
                catch (Exception ex)
                {
                    _log.Debug(LogCategory.Gate, "route preview pipeline check of " + name + ": " + ex.Message);
                }
            }
        }

        private void ReleasePipeline()
        {
            foreach (ComponentSystemBase system in _pipelineForced)
            {
                try
                {
                    system.Enabled = false;
                }
                catch (Exception)
                {
                    // the world may be gone
                }
            }
            _pipelineForced.Clear();
        }

        private bool Disable(World world, string name, bool route)
        {
            Type type = typeof(Game.Common.Owner).Assembly.GetType(name, false);
            if (type == null)
            {
                _log.Warn(LogCategory.Gate, "gated system not found: " + name);
                return false;
            }
            ComponentSystemBase system = world.GetExistingSystemManaged(type);
            if (system == null)
            {
                _log.Debug(LogCategory.Gate, "gated system not created: " + name);
                return false;
            }
            _disabled.Add((system, SystemSwitch.TurnOff(system)));
            if (route) _routeSystems.Add(system);
            return true;
        }
    }
}
