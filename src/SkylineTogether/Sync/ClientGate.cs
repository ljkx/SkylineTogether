using System;
using System.Collections.Generic;
using SkylineTogether.Core;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// Follower gating (ARCH §6.3, R11): systems that would overwrite, append to or randomise host-owned data on a
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
            // feat/integrity: writes nothing but warning icons, which come from the host (D11, IconSync)
            "Game.Net.ConnectionWarningSystem",
        };

        /// <summary>Line systems: off on clients except inside a route-tool preview window.</summary>
        private static readonly string[] RouteSystems =
        {
            "Game.Routes.RoutePathSystem",
            "Game.Routes.WaypointConnectionSystem",
        };

        /// <summary>The gated system names (probed at startup, <see cref="Diagnostics.Probes"/>).</summary>
        public static IReadOnlyList<string> GatedSystemNames => Gated;

        /// <summary>The line system names (probed at startup).</summary>
        public static IReadOnlyList<string> RouteSystemNames => RouteSystems;

        private readonly ILog _log;
        private readonly List<(ComponentSystemBase System, bool WasEnabled)> _disabled = new List<(ComponentSystemBase, bool)>();
        private readonly List<ComponentSystemBase> _routeSystems = new List<ComponentSystemBase>();
        private bool _closed;

        public ClientGate(ILog log)
        {
            _log = log;
        }

        /// <summary>Turns the gated systems off (client becomes a follower).</summary>
        public void Close()
        {
            if (_closed) return;
            _closed = true;
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return;
            int found = 0;
            foreach (string name in Gated) found += Disable(world, name, false) ? 1 : 0;
            foreach (string name in RouteSystems) found += Disable(world, name, true) ? 1 : 0;
            _log.Info(LogCategory.Gate, "follower gate closed: " + found + " of " + (Gated.Length + RouteSystems.Length) + " systems off");
        }

        /// <summary>Restores every system to its previous state (session ended).</summary>
        public void Open()
        {
            if (!_closed) return;
            _closed = false;
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
            _log.Info(LogCategory.Gate, "follower gate open");
        }

        public void SetRouteWindow(bool open)
        {
            if (!_closed) return;
            foreach (ComponentSystemBase s in _routeSystems) s.Enabled = open;
            _log.Debug(LogCategory.Gate, "route preview window " + (open ? "open" : "closed"));
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
