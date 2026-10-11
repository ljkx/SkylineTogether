using System;
using System.Collections.Generic;
using SkylineTogether.Core.Follower;
using SkylineTogether.Core.Json;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Follower
{
    /// <summary>
    /// <see cref="GatePlanner"/> (ARCH §6.3, Appendix E, R11 §4): critical misses, the producer/consumer pairs, the
    /// unclassified listing, adapter entries, the session-load and tool-scoped entries kept apart from the permanent ones,
    /// the simulation-phase rule and the precedence between overlapping claims.
    /// </summary>
    public sealed class GatePlannerTests
    {
        // producer/consumer systems and their registrations (Game.Common/SystemOrder.cs)
        private const string Trigger = "Game.Triggers.TriggerSystem";                         // ModificationEnd
        private const string CitizenInit = "Game.Citizens.CitizenInitializeSystem";           // Modification5
        private const string AddHealthProblem = "Game.Events.AddHealthProblemSystem";         // Modification4
        private const string EventsInit = "Game.Events.InitializeSystem";                     // Modification2
        private const string NotificationTrigger = "Game.Triggers.NotificationTriggerSystem"; // ModificationEnd
        private const string CityStatistics = "Game.Simulation.CityStatisticsSystem";         // GameSimulation
        private const string Xp = "Game.Simulation.XPSystem";                                 // ModificationEnd
        private const string XpBuilt = "Game.Simulation.XPBuiltSystem";                       // ModificationEnd
        private const string NetXp = "Game.Simulation.NetXPSystem";                           // ModificationEnd
        private const string XpAccumulation = "Game.Simulation.XPAccumulationSystem";         // GameSimulation
        private const string PropertyProcessing = "Game.Simulation.PropertyProcessingSystem"; // GameSimulation
        private const string CompanyInit = "Game.Citizens.CompanyInitializeSystem";           // Modification5
        private const string IconCommand = GatePlanner.IconCommandSystemTypeName;             // ModificationEnd
        private const string Subsidies = GatePlanner.GovernmentSubsidiesSystemTypeName;       // GameSimulation + ModificationEnd
        private const string Simulation = GatePlanner.SimulationSystemTypeName;               // LateUpdate
        private const string RouteTool = "Game.Tools.RouteToolSystem";                        // ToolUpdate
        private const string WaypointConnection = "Game.Routes.WaypointConnectionSystem";     // Modification5
        private const string RoutePath = "Game.Routes.RoutePathSystem";                       // ModificationEnd

        private static readonly string[] Cosmetic =
        {
            "Game.Simulation.TimeSystem", "Game.Simulation.ClimateSystem", "Game.Simulation.SnowSystem",
            "Game.Simulation.WindSimulationSystem", "Game.Simulation.WindSystem", "Game.Simulation.ZoneAmbienceSystem",
            "Game.Simulation.SpawnableAmbienceSystem", "Game.Simulation.WetnessSystem", "Game.Simulation.DirtynessSystem"
        };

        private static GateTable Table(string singleQuoted) => GateTable.Parse(MiniJson.Parse(singleQuoted.Replace('\'', '"')));

        private static GateTable EmptyTable() => Table("{ 'version': 1 }");

        private static ResolvedSystem Sys(string name, string phase, bool enabled = true) => new ResolvedSystem(name, phase, true, enabled);

        private static ResolvedSystem Missing(string name) => new ResolvedSystem(name, null, false, false);

        private static GateEntry Adapter(string name, GateAction action, bool critical = false) => new GateEntry { TypeName = name, Action = action, Critical = critical };

        /// <summary>The producer/consumer systems, the protected systems and the cosmetic systems as registered in vanilla.</summary>
        private static List<ResolvedSystem> VanillaWorld()
        {
            var world = new List<ResolvedSystem>
            {
                Sys(Trigger, "ModificationEnd"), Sys(CitizenInit, "Modification5"), Sys(AddHealthProblem, "Modification4"),
                Sys(EventsInit, "Modification2"), Sys(NotificationTrigger, "ModificationEnd"), Sys(CityStatistics, "GameSimulation"),
                Sys(Xp, "ModificationEnd"), Sys(XpBuilt, "ModificationEnd"), Sys(NetXp, "ModificationEnd"), Sys(XpAccumulation, "GameSimulation"),
                Sys(PropertyProcessing, "GameSimulation"), Sys(CompanyInit, "Modification5"), Sys(IconCommand, "ModificationEnd"),
                Sys(Subsidies, "GameSimulation"), Sys(Subsidies, "ModificationEnd"), Sys(Simulation, "LateUpdate"),
                Sys(RouteTool, "ToolUpdate"), Sys(WaypointConnection, "Modification5"), Sys(RoutePath, "ModificationEnd"),
            };
            foreach (string name in Cosmetic) world.Add(Sys(name, "GameSimulation"));
            world.Add(Sys("Game.Simulation.TimeSystem", "EditorSimulation"));
            world.Add(Sys("Game.Simulation.ClimateSystem", "EditorSimulation"));
            return world;
        }

        /// <summary>The producer/consumer rule systems, all gated as Appendix E does.</summary>
        private const string RuleSystemsOff = @"{ 'version': 1, 'entries': [
            { 'type': 'Game.Triggers.TriggerSystem', 'critical': true }, { 'type': 'Game.Citizens.CitizenInitializeSystem' },
            { 'type': 'Game.Events.AddHealthProblemSystem' }, { 'type': 'Game.Events.InitializeSystem' },
            { 'type': 'Game.Triggers.NotificationTriggerSystem' }, { 'type': 'Game.Simulation.XPSystem', 'critical': true },
            { 'type': 'Game.Simulation.XPBuiltSystem' }, { 'type': 'Game.Simulation.NetXPSystem' },
            { 'type': 'Game.Citizens.CompanyInitializeSystem' } ] }";

        private static GatePlan PlanVanilla(GateTable table, IEnumerable<GateEntry> adapters = null, List<ResolvedSystem> world = null, IReadOnlyList<string> keep = null)
        {
            return GatePlanner.Plan(table, adapters, world ?? VanillaWorld(), keep ?? Cosmetic);
        }

        /// <summary>
        /// The vanilla world without the two GameSimulation consumers (<c>CityStatisticsSystem</c>,
        /// <c>PropertyProcessingSystem</c>), which the phase rule always disables: tests about other rules can use small
        /// tables without gating those consumers' Modification-phase producers.
        /// </summary>
        private static List<ResolvedSystem> QuietWorld()
        {
            List<ResolvedSystem> world = VanillaWorld();
            world.RemoveAll(s => s.TypeName == CityStatistics || s.TypeName == PropertyProcessing);
            return world;
        }

        private static GatePlan PlanQuiet(GateTable table, IEnumerable<GateEntry> adapters = null, IReadOnlyList<string> keep = null)
        {
            return GatePlanner.Plan(table, adapters, QuietWorld(), keep ?? Cosmetic);
        }

        [Test]
        public void SmallTablesViolateTheRulesThroughThePhaseRule()
        {
            // CityStatisticsSystem and PropertyProcessingSystem are GameSimulation systems, always off on clients, so a
            // table must gate AddHealthProblemSystem and CompanyInitializeSystem (Appendix E does)
            GatePlan plan = PlanVanilla(EmptyTable());
            Assert.Equal(2, plan.Report.ProducerConsumerViolations.Count, string.Join(" | ", plan.Report.ProducerConsumerViolations));
            Assert.Contains("producer " + AddHealthProblem + " is on", plan.Report.ProducerConsumerViolations[0]);
            Assert.Contains("producer " + CompanyInit + " is on", plan.Report.ProducerConsumerViolations[1]);
            Assert.True(PlanQuiet(EmptyTable()).Report.Ok);
        }

        private static void NoViolations(GatePlan plan)
        {
            Assert.Equal(0, plan.Report.ProducerConsumerViolations.Count, "violations: " + string.Join(" | ", plan.Report.ProducerConsumerViolations));
        }

        private static string ViolationFor(GatePlan plan, string producer)
        {
            foreach (string message in plan.Report.ProducerConsumerViolations)
            {
                if (message.Contains("producer " + producer + " ")) return message;
            }
            Assert.Fail("no violation names the producer " + producer + "; got: " + string.Join(" | ", plan.Report.ProducerConsumerViolations));
            return null;
        }

        [Test]
        public void TheRuleSystemsGatedTogetherPassTheSelfCheck()
        {
            GatePlan plan = PlanVanilla(Table(RuleSystemsOff));
            NoViolations(plan);
            Assert.True(plan.Report.Ok);
            foreach (string name in new[] { Trigger, CitizenInit, AddHealthProblem, EventsInit, NotificationTrigger, Xp, XpBuilt, NetXp, CompanyInit })
            {
                Assert.Contains(name, plan.Disable);
            }
            // GameSimulation consumers and producers are covered by the simulation-phase rule
            Assert.Contains(CityStatistics, plan.Disable);
            Assert.Contains(PropertyProcessing, plan.Disable);
            Assert.Contains(XpAccumulation, plan.Disable);
        }

        [Test]
        public void TriggerSystemMayBeOffOnlyWithItsUnguardedProducers()
        {
            foreach (string producer in new[] { CitizenInit, AddHealthProblem, EventsInit, NotificationTrigger })
            {
                string json = RuleSystemsOff.Replace("{ 'type': '" + producer + "' }", "{ 'type': 'Some.Other.System' }");
                Assert.NotEqual(RuleSystemsOff, json, "fixture edit for " + producer);
                GatePlan plan = PlanVanilla(Table(json));
                Assert.False(plan.Report.Ok, producer);
                string message = ViolationFor(plan, producer);
                Assert.Contains(producer == AddHealthProblem ? "is off on clients" : Trigger + " is off on clients", message);
                Assert.Contains("R11 §4", message);
            }
        }

        [Test]
        public void CityStatisticsNeedsAddHealthProblemOff()
        {
            // CityStatisticsSystem is a GameSimulation system, off by the phase rule; its Mod4 producer must follow
            string json = RuleSystemsOff.Replace("{ 'type': 'Game.Events.AddHealthProblemSystem' }", "{ 'type': 'Some.Other.System' }");
            GatePlan plan = PlanVanilla(Table(json));
            Assert.Contains(CityStatistics, plan.Disable);
            int mentions = 0;
            foreach (string message in plan.Report.ProducerConsumerViolations)
            {
                if (message.Contains("producer " + AddHealthProblem + " ")) mentions++;
            }
            Assert.Equal(2, mentions, "TriggerSystem and CityStatisticsSystem both name it");
            bool cityStatistics = false;
            foreach (string message in plan.Report.ProducerConsumerViolations) cityStatistics |= message.StartsWith(CityStatistics + " is off on clients", StringComparison.Ordinal);
            Assert.True(cityStatistics);
        }

        [Test]
        public void XpSystemNeedsItsProducersOff()
        {
            string json = RuleSystemsOff.Replace("{ 'type': 'Game.Simulation.NetXPSystem' }", "{ 'type': 'Some.Other.System' }");
            GatePlan plan = PlanVanilla(Table(json));
            Assert.Contains(Xp + " is off on clients", ViolationFor(plan, NetXp));
            Assert.Equal(1, plan.Report.ProducerConsumerViolations.Count, "XPAccumulationSystem is covered by the phase rule");
        }

        [Test]
        public void PropertyProcessingNeedsCompanyInitializeOff()
        {
            string json = RuleSystemsOff.Replace("{ 'type': 'Game.Citizens.CompanyInitializeSystem' }", "{ 'type': 'Some.Other.System' }");
            GatePlan plan = PlanVanilla(Table(json));
            Assert.Contains(PropertyProcessing + " is off on clients", ViolationFor(plan, CompanyInit));
        }

        [Test]
        public void AConsumerThatStaysOnNeedsNothing()
        {
            // nothing gated except the phase rule: TriggerSystem and XPSystem stay on, so their producers may run
            NoViolations(PlanQuiet(EmptyTable()));
        }

        [Test]
        public void AnAbsentProducerCannotLeak()
        {
            string json = RuleSystemsOff.Replace("{ 'type': 'Game.Events.InitializeSystem' }", "{ 'type': 'Some.Other.System' }");
            List<ResolvedSystem> world = VanillaWorld();
            world.RemoveAll(s => s.TypeName == EventsInit);
            world.Add(Missing(EventsInit));
            GatePlan plan = PlanVanilla(Table(json), world: world);
            NoViolations(plan);
        }

        [Test]
        public void IconCommandSystemMustStayOn()
        {
            GatePlan plan = PlanQuiet(Table("{ 'version': 1, 'entries': [ { 'type': 'Game.Notifications.IconCommandSystem' } ] }"));
            Assert.False(plan.Report.Ok);
            Assert.False(new List<string>(plan.Disable).Contains(IconCommand), "the unsafe entry is ignored");
            Assert.Contains(IconCommand, plan.Keep);
            Assert.Equal(1, plan.Report.ProducerConsumerViolations.Count);
            Assert.Contains("must stay on", plan.Report.ProducerConsumerViolations[0]);
            Assert.Contains("Off (gates.json entries)", plan.Report.ProducerConsumerViolations[0]);

            GatePlan adapter = PlanQuiet(EmptyTable(), new[] { Adapter(IconCommand, GateAction.OffDuringSessionLoad) });
            Assert.False(adapter.Report.Ok);
            Assert.Equal(0, adapter.SessionLoadDisable.Count);

            List<ResolvedSystem> world = QuietWorld();
            world.RemoveAll(s => s.TypeName == IconCommand);
            world.Add(Sys(IconCommand, "ModificationEnd", enabled: false));
            GatePlan disabled = PlanVanilla(EmptyTable(), world: world);
            Assert.Equal(1, disabled.Report.ProducerConsumerViolations.Count);
            Assert.False(disabled.Report.Ok);
            Assert.Contains("currently disabled", disabled.Report.ProducerConsumerViolations[0]);

            GatePlan kept = PlanQuiet(Table("{ 'version': 1, 'entries': [ { 'type': 'Game.Notifications.IconCommandSystem', 'action': 'Keep' } ] }"));
            Assert.True(kept.Report.Ok);
            Assert.Contains(IconCommand, kept.Keep);
        }

        [Test]
        public void SimulationSystemIsNeverGatedByTheTable()
        {
            GatePlan plan = PlanQuiet(Table("{ 'version': 1, 'entries': [ { 'type': 'Game.Simulation.SimulationSystem', 'critical': true } ] }"));
            Assert.False(new List<string>(plan.Disable).Contains(Simulation));
            Assert.False(new List<string>(plan.Keep).Contains(Simulation));
            Assert.True(plan.Report.Ok, "ignored, not a failure");
            Assert.Contains("load hand-over", string.Join(" | ", plan.Report.Conflicts));
            Assert.False(new List<string>(plan.Report.Unclassified).Contains(Simulation));
        }

        [Test]
        public void CriticalMissesBlockTheJoin()
        {
            GateTable table = Table(@"{ 'version': 1, 'entries': [
                { 'type': 'Game.Simulation.XPSystem', 'critical': true },
                { 'type': 'Game.Gone.CriticalSystem', 'critical': true },
                { 'type': 'Game.Gone.OptionalSystem' } ],
              'sessionLoad': [ { 'type': 'Game.Serialization.ResetBuildOrderSystem', 'critical': true } ] }");
            List<ResolvedSystem> world = QuietWorld();
            world.Add(Missing("Game.Gone.OptionalSystem"));
            GatePlan plan = GatePlanner.Plan(table, null, world, Cosmetic);
            Assert.SequenceEqual(new[] { "Game.Gone.CriticalSystem", "Game.Serialization.ResetBuildOrderSystem" }, plan.Report.CriticalUnresolved);
            Assert.SequenceEqual(new[] { "Game.Gone.CriticalSystem", "Game.Gone.OptionalSystem", "Game.Serialization.ResetBuildOrderSystem" }, plan.Report.Unresolved);
            Assert.False(plan.Report.Ok);
            Assert.False(new List<string>(plan.Disable).Contains("Game.Gone.CriticalSystem"), "only resolved systems are in the plan");

            GatePlan optional = GatePlanner.Plan(Table("{ 'version': 1, 'entries': [ { 'type': 'Game.Gone.OptionalSystem' } ] }"), null, world, Cosmetic);
            Assert.True(optional.Report.Ok, "a missing optional entry only warns");
            Assert.SequenceEqual(new[] { "Game.Gone.OptionalSystem" }, optional.Report.Unresolved);
        }

        [Test]
        public void UnresolvedKeepNamesAndToolsAreWarnings()
        {
            GateTable table = Table(@"{ 'version': 1, 'toolScoped': [ { 'tool': 'Mod.Tools.GoneTool', 'systems': [ 'Game.Routes.WaypointConnectionSystem' ] } ] }");
            GatePlan plan = GatePlanner.Plan(table, null, QuietWorld(), new[] { "Game.Simulation.TimeSystem", "Mod.Gone.CosmeticSystem" });
            Assert.SequenceEqual(new[] { "Mod.Gone.CosmeticSystem", "Mod.Tools.GoneTool" }, plan.Report.Unresolved);
            Assert.Equal(0, plan.Report.CriticalUnresolved.Count);
            Assert.True(plan.Report.Ok);
            Assert.Contains(WaypointConnection, plan.ToolScoped);
        }

        [Test]
        public void SimulationPhaseSystemsAreDisabledUnlessKept()
        {
            var world = new List<ResolvedSystem>
            {
                Sys("Game.Simulation.ResidentAISystem", "GameSimulation"),
                Sys("Game.Simulation.EditorOnlySystem", "EditorSimulation"),
                Sys("Game.Simulation.LoadOnlySystem", "loadsimulation"),
                Sys("Game.Debug.DebugWatchSystem", "MainLoop"), Sys("Game.Debug.DebugWatchSystem", "GameSimulation"),
                Sys("Mod.Simulation.ModSystem", "GameSimulation"),
                Sys("Game.Simulation.TimeSystem", "GameSimulation"),
                Sys(Subsidies, "GameSimulation"), Sys(Subsidies, "ModificationEnd"),
                Sys("Game.Simulation.WaterSystem", "PostSimulation"),
            };
            GatePlan plan = GatePlanner.Plan(EmptyTable(), null, world, new[] { "Game.Simulation.TimeSystem" });
            Assert.SequenceEqual(new[]
            {
                "Game.Debug.DebugWatchSystem", "Game.Simulation.EditorOnlySystem", "Game.Simulation.LoadOnlySystem",
                "Game.Simulation.ResidentAISystem", "Mod.Simulation.ModSystem"
            }, plan.Disable);
            Assert.SequenceEqual(new[] { Subsidies, "Game.Simulation.TimeSystem" }, plan.Keep, "built-in keep and the cosmetic keep list");
            Assert.True(plan.Report.Ok);
            Assert.SequenceEqual(new[] { "Game.Simulation.WaterSystem" }, plan.Report.Unclassified, "PostSimulation runs on clients and is classifiable");
            // Enabled is per system: the rule also stops DebugWatchSystem's MainLoop registration (SystemOrder.cs), which is reported
            Assert.SequenceEqual(new[]
            {
                "Game.Debug.DebugWatchSystem: disabled by the simulation-phase rule, which also stops its registration in MainLoop; "
                + "list it as Keep if it must run on clients (ARCH §6.1)"
            }, plan.Report.Conflicts);
        }

        [Test]
        public void ThePhaseRuleNamesEveryOtherRegistration()
        {
            var world = new List<ResolvedSystem>
            {
                Sys("Mod.Odd.System", "UIUpdate"), Sys("Mod.Odd.System", "GameSimulation"), Sys("Mod.Odd.System", "Modification4"),
                Sys("Mod.Odd.System", "UIUpdate"), Sys("Mod.Sim.LoadOnly", "GameSimulation"), Sys("Mod.Sim.LoadOnly", "LoadSimulation"),
            };
            GatePlan plan = GatePlanner.Plan(EmptyTable(), null, world, null);
            Assert.SequenceEqual(new[] { "Mod.Odd.System", "Mod.Sim.LoadOnly" }, plan.Disable);
            Assert.Equal(1, plan.Report.Conflicts.Count, "two simulation phases are no other registration");
            Assert.Contains("stops its registration in Modification4, UIUpdate;", plan.Report.Conflicts[0]);
        }

        [Test]
        public void CosmeticSystemsStayEnabledForTheRunner()
        {
            GatePlan plan = PlanVanilla(Table(RuleSystemsOff));
            foreach (string name in Cosmetic)
            {
                Assert.Contains(name, plan.Keep);
                Assert.False(new List<string>(plan.Disable).Contains(name), name);
            }
            Assert.Contains(Subsidies, plan.Keep);
        }

        [Test]
        public void SessionLoadEntriesAreSeparateFromPermanentOnes()
        {
            GateTable table = Table(@"{ 'version': 1, 'sessionLoad': [
                { 'type': 'Game.Serialization.ResetBuildOrderSystem', 'critical': true },
                { 'type': 'Game.Serialization.CitizenSystem', 'critical': true },
                { 'type': 'Game.Serialization.RandomLocalizationSystem' } ] }");
            List<ResolvedSystem> world = QuietWorld();
            world.Add(Sys("Game.Serialization.ResetBuildOrderSystem", "Deserialize"));
            world.Add(Sys("Game.Serialization.CitizenSystem", "Deserialize"));
            world.Add(Sys("Game.Serialization.RandomLocalizationSystem", "Deserialize"));
            GatePlan plan = PlanVanilla(table, world: world);
            Assert.SequenceEqual(new[]
            {
                "Game.Serialization.CitizenSystem", "Game.Serialization.RandomLocalizationSystem", "Game.Serialization.ResetBuildOrderSystem"
            }, plan.SessionLoadDisable);
            foreach (string name in plan.SessionLoadDisable)
            {
                Assert.False(new List<string>(plan.Disable).Contains(name), name + " is not permanently off");
                Assert.False(new List<string>(plan.Keep).Contains(name));
            }
            Assert.True(plan.Report.Ok);
        }

        [Test]
        public void ToolScopedSystemsAreNeverPermanentlyKeptOrDisabled()
        {
            GateTable table = Table(@"{ 'version': 1, 'toolScoped': [ { 'tool': 'Game.Tools.RouteToolSystem',
                'systems': [ 'Game.Routes.WaypointConnectionSystem', 'Game.Routes.RoutePathSystem' ], 'guard': 'RouteGuard' } ] }");
            var keep = new List<string>(Cosmetic) { WaypointConnection };
            GatePlan plan = PlanQuiet(table, new[] { Adapter(RoutePath, GateAction.Off) }, keep);
            Assert.SequenceEqual(new[] { RoutePath, WaypointConnection }, plan.ToolScoped);
            Assert.False(new List<string>(plan.Keep).Contains(WaypointConnection), "never in the permanent keep list");
            Assert.False(new List<string>(plan.Disable).Contains(RoutePath), "never re-asserted off while a window is open");
            string conflicts = string.Join(" | ", plan.Report.Conflicts);
            Assert.Contains(WaypointConnection + ": tool-scoped overrides Keep (keep list)", conflicts);
            Assert.Contains(RoutePath + ": tool-scoped overrides Off (adapter)", conflicts);
            Assert.False(new List<string>(plan.Report.Unclassified).Contains(RouteTool), "the tool is classified by its entry");
            Assert.True(plan.Report.Ok);
        }

        [Test]
        public void AdapterEntries()
        {
            List<ResolvedSystem> world = VanillaWorld();
            world.Add(Sys("Mod.Traffic.LaneSystem", "Modification4"));
            world.Add(Sys("Mod.Traffic.SimSystem", "GameSimulation"));
            world.Add(Sys("Mod.Traffic.CosmeticSystem", "GameSimulation"));
            world.Add(Sys("Mod.Traffic.LoadFixSystem", "Deserialize"));
            world.Add(Sys("Mod.Tools.PreviewSystem", "Modification5"));
            var adapters = new[]
            {
                Adapter("Mod.Traffic.LaneSystem", GateAction.Off),
                Adapter("Mod.Traffic.SimSystem", GateAction.Keep),
                Adapter("Mod.Traffic.LoadFixSystem", GateAction.OffDuringSessionLoad),
                Adapter("Mod.Tools.PreviewSystem", GateAction.ToolScoped),
                Adapter("Mod.Traffic.MissingSystem", GateAction.Off, critical: true),
                null,
                new GateEntry { TypeName = null, Action = GateAction.Off }
            };
            var keep = new List<string>(Cosmetic) { "Mod.Traffic.CosmeticSystem" };
            GatePlan plan = GatePlanner.Plan(Table(RuleSystemsOff), adapters, world, keep);
            Assert.Contains("Mod.Traffic.LaneSystem", plan.Disable);
            Assert.Contains("Mod.Traffic.SimSystem", plan.Keep);
            Assert.Contains("Mod.Traffic.CosmeticSystem", plan.Keep);
            Assert.SequenceEqual(new[] { "Mod.Traffic.LoadFixSystem" }, plan.SessionLoadDisable);
            Assert.SequenceEqual(new[] { "Mod.Tools.PreviewSystem" }, plan.ToolScoped);
            Assert.SequenceEqual(new[] { "Mod.Traffic.MissingSystem" }, plan.Report.CriticalUnresolved);
            Assert.False(plan.Report.Ok);
            Assert.Contains("2 adapter entries without a type name ignored", string.Join(" | ", plan.Report.Conflicts));
        }

        [Test]
        public void OffWinsOverKeepAndSessionLoad()
        {
            GateTable table = Table(@"{ 'version': 1, 'entries': [
                { 'type': 'Game.Simulation.WindSystem', 'reason': 'mistake' },
                { 'type': 'Game.Prefabs.UnlockSystem', 'action': 'Keep' } ] }");
            List<ResolvedSystem> world = VanillaWorld();
            world.Add(Sys("Game.Prefabs.UnlockSystem", "MainLoop"));
            world.Add(Sys("Game.Serialization.CitizenSystem", "Deserialize"));
            var adapters = new[]
            {
                Adapter("Game.Prefabs.UnlockSystem", GateAction.Off),
                Adapter("Game.Serialization.CitizenSystem", GateAction.Off),
                Adapter("Game.Serialization.CitizenSystem", GateAction.OffDuringSessionLoad),
                Adapter(Subsidies, GateAction.OffAndFeed)
            };
            GatePlan plan = GatePlanner.Plan(table, adapters, world, Cosmetic);
            foreach (string name in new[] { "Game.Simulation.WindSystem", "Game.Prefabs.UnlockSystem", "Game.Serialization.CitizenSystem", Subsidies })
            {
                Assert.Contains(name, plan.Disable);
                Assert.False(new List<string>(plan.Keep).Contains(name), name);
                Assert.False(new List<string>(plan.SessionLoadDisable).Contains(name), name);
            }
            string conflicts = string.Join(" | ", plan.Report.Conflicts);
            Assert.Contains("Game.Simulation.WindSystem: Off (gates.json entries) overrides Keep (keep list) (fail closed)", conflicts);
            Assert.Contains("Game.Prefabs.UnlockSystem: Off (adapter) overrides Keep (gates.json entries) (fail closed)", conflicts);
            Assert.Contains("Game.Serialization.CitizenSystem: Off (adapter) overrides OffDuringSessionLoad (adapter) (fail closed)", conflicts);
            Assert.Contains(Subsidies + ": Off (adapter) overrides Keep (built-in) (fail closed)", conflicts);
        }

        [Test]
        public void SessionLoadWinsOverKeep()
        {
            GateTable table = Table("{ 'version': 1, 'sessionLoad': [ { 'type': 'Game.Serialization.CitizenSystem' } ] }");
            List<ResolvedSystem> world = VanillaWorld();
            world.Add(Sys("Game.Serialization.CitizenSystem", "Deserialize"));
            GatePlan plan = PlanVanilla(table, new[] { Adapter("Game.Serialization.CitizenSystem", GateAction.Keep) }, world);
            Assert.SequenceEqual(new[] { "Game.Serialization.CitizenSystem" }, plan.SessionLoadDisable);
            Assert.Contains("Game.Serialization.CitizenSystem: OffDuringSessionLoad (gates.json sessionLoad) overrides Keep (adapter)",
                            string.Join(" | ", plan.Report.Conflicts));
        }

        [Test]
        public void UnclassifiedListsUnnamedGameSystemsOfWorldWritingPhases()
        {
            List<ResolvedSystem> world = VanillaWorld();
            world.RemoveAll(s => s.TypeName == RouteTool || s.TypeName == WaypointConnection || s.TypeName == RoutePath);
            world.Add(Sys("Game.Buildings.NewPatchSystem", "Modification4"));        // listed
            world.Add(Sys("Game.Tools.NewToolHelperSystem", "ToolUpdate"));         // listed
            world.Add(Sys("Game.Common.NewMainLoopSystem", "MainLoop"));            // listed
            world.Add(Sys("Game.Common.UnknownPhaseSystem", null));                 // listed: phase unknown
            world.Add(Sys("Game.Rendering.NewRenderSystem", "Rendering"));          // presentation: not listed
            world.Add(Sys("Game.UI.NewPanelSystem", "UIUpdate"));                   // presentation: not listed
            world.Add(Sys("Game.Serialization.NewLoadSystem", "Deserialize"));      // load time: not listed
            world.Add(Sys("Game.Simulation.NewSimSystem", "GameSimulation"));       // disabled by the phase rule
            world.Add(Sys("Mod.Other.NewSystem", "Modification4"));                 // not a Game-assembly system
            world.Add(Missing("Game.Buildings.NotThereSystem"));                    // unresolved rows are no systems
            GatePlan plan = PlanVanilla(Table(RuleSystemsOff), world: world);
            Assert.SequenceEqual(new[]
            {
                "Game.Buildings.NewPatchSystem", "Game.Common.NewMainLoopSystem", "Game.Common.UnknownPhaseSystem",
                "Game.Tools.NewToolHelperSystem"
            }, plan.Report.Unclassified);
            Assert.True(plan.Report.Ok, "unclassified systems are maintenance information");
            Assert.Contains("Game.Simulation.NewSimSystem", plan.Disable);
        }

        [Test]
        public void ConsumersOffDuringLoadsOrOutsideWindowsNeedMatchingProducers()
        {
            // contrived tables that exercise the coverage rules for the other off modes
            var world = new List<ResolvedSystem>
            {
                Sys(Trigger, "ModificationEnd"), Sys(CitizenInit, "Modification5"), Sys(AddHealthProblem, "Modification4"),
                Sys(EventsInit, "Modification2"), Sys(NotificationTrigger, "ModificationEnd"),
                Sys(Xp, "ModificationEnd"), Sys(XpBuilt, "ModificationEnd"), Sys(NetXp, "ModificationEnd"), Sys(RouteTool, "ToolUpdate"),
                Sys("Mod.Tools.OtherTool", "ToolUpdate")
            };
            GatePlan sessionLoad = GatePlanner.Plan(Table(@"{ 'version': 1,
                'entries': [ { 'type': 'Game.Citizens.CitizenInitializeSystem' } ],
                'sessionLoad': [ { 'type': 'Game.Triggers.TriggerSystem' }, { 'type': 'Game.Events.AddHealthProblemSystem' } ],
                'toolScoped': [ { 'tool': 'Game.Tools.RouteToolSystem', 'systems': [ 'Game.Events.InitializeSystem' ] } ] }"), null, world, null);
            Assert.Equal(1, sessionLoad.Report.ProducerConsumerViolations.Count, string.Join(" | ", sessionLoad.Report.ProducerConsumerViolations));
            Assert.Contains(Trigger + " is off during session loads, but its producer " + NotificationTrigger + " is on",
                            sessionLoad.Report.ProducerConsumerViolations[0]);

            GatePlan windows = GatePlanner.Plan(Table(@"{ 'version': 1,
                'entries': [ { 'type': 'Game.Simulation.XPBuiltSystem' } ],
                'toolScoped': [ { 'tool': 'Game.Tools.RouteToolSystem', 'systems': [ 'Game.Simulation.XPSystem', 'Game.Simulation.NetXPSystem' ] } ] }"),
                null, world, null);
            NoViolations(windows);

            GatePlan otherTool = GatePlanner.Plan(Table(@"{ 'version': 1,
                'entries': [ { 'type': 'Game.Simulation.XPBuiltSystem' } ],
                'toolScoped': [ { 'tool': 'Game.Tools.RouteToolSystem', 'systems': [ 'Game.Simulation.XPSystem' ] },
                                { 'tool': 'Mod.Tools.OtherTool', 'systems': [ 'Game.Simulation.NetXPSystem' ] } ] }"), null, world, null);
            Assert.Equal(1, otherTool.Report.ProducerConsumerViolations.Count);
            Assert.Contains(Xp + " is off outside its tool's preview windows, but its producer " + NetXp + " is on at times when it is off",
                            otherTool.Report.ProducerConsumerViolations[0]);
        }

        [Test]
        public void ToolScopedInASimulationPhaseIsReported()
        {
            List<ResolvedSystem> world = VanillaWorld();
            world.Add(Sys("Mod.Sim.PreviewSystem", "GameSimulation"));
            GatePlan plan = PlanVanilla(Table(@"{ 'version': 1, 'toolScoped': [ { 'tool': 'Game.Tools.RouteToolSystem', 'systems': [ 'Mod.Sim.PreviewSystem' ] } ] }"),
                                        world: world);
            Assert.SequenceEqual(new[] { "Mod.Sim.PreviewSystem" }, plan.ToolScoped);
            Assert.False(new List<string>(plan.Disable).Contains("Mod.Sim.PreviewSystem"));
            Assert.Contains("never runs on clients", string.Join(" | ", plan.Report.Conflicts));
        }

        [Test]
        public void UnknownActionsFailClosed()
        {
            List<ResolvedSystem> world = VanillaWorld();
            world.Add(Sys("Mod.Odd.System", "Modification4"));
            GatePlan plan = PlanVanilla(EmptyTable(), new[] { Adapter("Mod.Odd.System", (GateAction)99) }, world);
            Assert.Contains("Mod.Odd.System", plan.Disable);
            Assert.Contains("unknown gate action 99 treated as Off", string.Join(" | ", plan.Report.Conflicts));
        }

        [Test]
        public void ThePlanIsDeterministic()
        {
            List<ResolvedSystem> world = VanillaWorld();
            world.Add(Sys("Game.Buildings.NewPatchSystem", "Modification4"));
            var adapters = new List<GateEntry> { Adapter("Mod.A.System", GateAction.Off), Adapter("Mod.B.System", GateAction.Keep) };
            world.Add(Sys("Mod.A.System", "Modification4"));
            world.Add(Sys("Mod.B.System", "GameSimulation"));
            GatePlan first = PlanVanilla(Table(RuleSystemsOff), adapters, world);

            world.Reverse();
            adapters.Reverse();
            var keep = new List<string>(Cosmetic);
            keep.Reverse();
            GatePlan second = PlanVanilla(Table(RuleSystemsOff), adapters, world, keep);

            Assert.SequenceEqual(first.Disable, second.Disable);
            Assert.SequenceEqual(first.SessionLoadDisable, second.SessionLoadDisable);
            Assert.SequenceEqual(first.Keep, second.Keep);
            Assert.SequenceEqual(first.ToolScoped, second.ToolScoped);
            Assert.SequenceEqual(first.Report.Unclassified, second.Report.Unclassified);
            Assert.SequenceEqual(first.Report.Conflicts, second.Report.Conflicts);
            var sorted = new List<string>(first.Disable);
            sorted.Sort(StringComparer.Ordinal);
            Assert.SequenceEqual(sorted, first.Disable, "ordinal order");
        }

        [Test]
        public void ToleratesMissingInputs()
        {
            Assert.Throws<ArgumentNullException>(() => GatePlanner.Plan(null, null, VanillaWorld(), Cosmetic));
            GatePlan plan = GatePlanner.Plan(Table(RuleSystemsOff), null, null, null);
            Assert.Equal(0, plan.Disable.Count, "nothing resolves without a system list");
            Assert.Equal(2, plan.Report.CriticalUnresolved.Count);
            Assert.False(plan.Report.Ok);

            var table = new GateTable { Version = 1 };   // hand-built table without lists
            var world = new List<ResolvedSystem> { null, new ResolvedSystem(null, "MainLoop"), Sys("Game.Simulation.ResidentAISystem", "GameSimulation") };
            GatePlan sparse = GatePlanner.Plan(table, null, world, new[] { (string)null, string.Empty });
            Assert.SequenceEqual(new[] { "Game.Simulation.ResidentAISystem" }, sparse.Disable);
            string conflicts = string.Join(" | ", sparse.Report.Conflicts);
            Assert.Contains("2 row(s) of the system list without a type name ignored", conflicts);
            Assert.Contains("2 empty name(s) in the keep list ignored", conflicts);
        }

        [Test]
        public void ReportTextIsComplete()
        {
            GatePlan plan = PlanVanilla(Table("{ 'version': 1, 'entries': [ { 'type': 'Game.Gone.CriticalSystem', 'critical': true } ] }"));
            string text = plan.Report.Describe();
            Assert.Contains("gate report: FAILED", text);
            Assert.Contains("critical unresolved (1):\n  Game.Gone.CriticalSystem", text);
            Assert.Contains("gate plan: disable ", plan.ToString());
            Assert.Contains("ok", new GateReport().ToString());
            Assert.True(new GateReport().Ok, "an empty report is ok");
        }

        /// <summary>
        /// The initial client gate table of ARCH Appendix E, written as gates.json (it doubles as an example for the
        /// shipped file), over the systems it names in their vanilla phases: the plan must pass the self-check.
        /// </summary>
        private const string AppendixE = @"{
  'version': 1,
  'entries': [
    { 'type': 'Game.Tools.UpgradeDeletedSystem', 'phase': 'ToolUpdate' },
    { 'type': 'Game.Events.AddMeetingSystem', 'phase': 'Modification1' },
    { 'type': 'Game.Prefabs.UnlockAllSystem', 'phase': 'Modification1' },
    { 'type': 'Game.Routes.RoutePathReadySystem', 'phase': 'Modification1', 'critical': true },
    { 'type': 'Game.Events.InitializeSystem', 'phase': 'Modification2', 'critical': true },
    { 'type': 'Game.Objects.OutsideConnectionInitializeSystem', 'phase': 'Modification2' },
    { 'type': 'Game.Objects.DamageSystem', 'phase': 'Modification2' },
    { 'type': 'Game.Objects.DestroySystem', 'phase': 'Modification2' },
    { 'type': 'Game.Simulation.GroundHeightSystem', 'phase': 'Modification2' },
    { 'type': 'Game.Citizens.HouseholdAndCitizenRemoveSystem', 'phase': 'Modification2' },
    { 'type': 'Game.Policies.DefaultPoliciesSystem', 'phase': 'Modification3', 'critical': true },
    { 'type': 'Game.Citizens.HouseholdInitializeSystem', 'phase': 'Modification4', 'critical': true },
    { 'type': 'Game.Routes.InitializeSystem', 'phase': 'Modification4' },
    { 'type': 'Game.Citizens.HouseholdPetRemoveSystem', 'phase': 'Modification4' },
    { 'type': 'Game.Events.ImpactSystem', 'phase': 'Modification4' },
    { 'type': 'Game.Events.IgniteSystem', 'phase': 'Modification4' },
    { 'type': 'Game.Events.FaceWeatherSystem', 'phase': 'Modification4' },
    { 'type': 'Game.Events.AddHealthProblemSystem', 'phase': 'Modification4', 'critical': true },
    { 'type': 'Game.Events.SpectateSystem', 'phase': 'Modification4' },
    { 'type': 'Game.Events.EndangerSystem', 'phase': 'Modification4' },
    { 'type': 'Game.Events.AddAccidentSiteSystem', 'phase': 'Modification4' },
    { 'type': 'Game.Events.AddCriminalSystem', 'phase': 'Modification4' },
    { 'type': 'Game.Events.SubmergeSystem', 'phase': 'Modification4' },
    { 'type': 'Game.Creatures.TripResetSystem', 'phase': 'Modification4' },
    { 'type': 'Game.Events.EventJournalInitializeSystem', 'phase': 'Modification4' },
    { 'type': 'Game.Objects.ObjectEmergeSystem', 'phase': 'Modification4B' },
    { 'type': 'Game.Vehicles.ParkedVehiclesSystem', 'phase': 'Modification4B' },
    { 'type': 'Game.Buildings.BuildingStateEfficiencySystem', 'phase': 'Modification4B' },
    { 'type': 'Game.Simulation.RemovedSystem', 'phase': 'Modification5' },
    { 'type': 'Game.Prefabs.ZoneBuiltRequirementSystem', 'phase': 'Modification5' },
    { 'type': 'Game.Buildings.ResourcesInitializeSystem', 'phase': 'Modification5' },
    { 'type': 'Game.Buildings.BatteryInitializeSystem', 'phase': 'Modification5' },
    { 'type': 'Game.Buildings.ParkInitializeSystem', 'phase': 'Modification5' },
    { 'type': 'Game.Citizens.CitizenInitializeSystem', 'phase': 'Modification5', 'critical': true },
    { 'type': 'Game.Citizens.HouseholdPetInitializeSystem', 'phase': 'Modification5' },
    { 'type': 'Game.Citizens.MeetingInitializeSystem', 'phase': 'Modification5' },
    { 'type': 'Game.Citizens.CompanyInitializeSystem', 'phase': 'Modification5', 'critical': true },
    { 'type': 'Game.Citizens.StorageInitializeSystem', 'phase': 'Modification5' },
    { 'type': 'Game.Buildings.CityServiceWorkplaceInitializeSystem', 'phase': 'Modification5' },
    { 'type': 'Game.Events.EventJournalSystem', 'phase': 'Modification5' },
    { 'type': 'Game.Creatures.GroupSystem', 'phase': 'Modification5' },
    { 'type': 'Game.Vehicles.FixParkingLocationSystem', 'phase': 'Modification5' },
    { 'type': 'Game.Net.FixLaneObjectsSystem', 'phase': 'Modification5' },
    { 'type': 'Game.Buildings.ZoneCheckSystem', 'phase': 'ModificationEnd' },
    { 'type': 'Game.Simulation.ServiceRequestSystem', 'phase': 'ModificationEnd' },
    { 'type': 'Game.Simulation.XPBuiltSystem', 'phase': 'ModificationEnd', 'critical': true },
    { 'type': 'Game.Simulation.NetXPSystem', 'phase': 'ModificationEnd', 'critical': true },
    { 'type': 'Game.Simulation.XPSystem', 'phase': 'ModificationEnd', 'critical': true },
    { 'type': 'Game.Simulation.XPAccumulationSystem', 'phase': 'GameSimulation' },
    { 'type': 'Game.Simulation.MilestoneSystem', 'phase': 'ModificationEnd', 'action': 'OffAndFeed', 'critical': true },
    { 'type': 'Game.Triggers.TriggerSystem', 'phase': 'ModificationEnd', 'critical': true },
    { 'type': 'Game.Triggers.LifePathEventSystem', 'phase': 'ModificationEnd' },
    { 'type': 'Game.Triggers.CreateChirpSystem', 'phase': 'ModificationEnd' },
    { 'type': 'Game.Triggers.RadioTagSystem', 'phase': 'ModificationEnd' },
    { 'type': 'Game.Prefabs.StrictObjectBuiltRequirementSystem', 'phase': 'ModificationEnd' },
    { 'type': 'Game.Prefabs.ObjectBuiltRequirementSystem', 'phase': 'ModificationEnd' },
    { 'type': 'Game.Prefabs.PrefabUnlockedRequirementSystem', 'phase': 'ModificationEnd' },
    { 'type': 'Game.City.DevTreeSystem', 'phase': 'ModificationEnd', 'critical': true },
    { 'type': 'Game.Common.RandomLocalizationInitializeSystem', 'phase': 'ModificationEnd' },
    { 'type': 'Game.Triggers.NotificationTriggerSystem', 'phase': 'ModificationEnd', 'critical': true },
    { 'type': 'Game.Pathfind.ParkingLaneDataSystem', 'phase': 'ModificationEnd' },
    { 'type': 'Game.Pathfind.PathOwnerTargetMovedSystem', 'phase': 'ModificationEnd' },
    { 'type': 'Game.Routes.BoardingVehicleSystem', 'phase': 'ModificationEnd' },
    { 'type': 'Game.Buildings.CityServiceEfficiencySystem', 'phase': 'ModificationEnd' },
    { 'type': 'Game.Simulation.CityServiceBudgetSystem', 'phase': 'ModificationEnd', 'action': 'OffAndFeed' },
    { 'type': 'Game.Net.ConnectionWarningSystem', 'phase': 'ModificationEnd', 'critical': true },
    { 'type': 'Game.Tutorials.TutorialSystem', 'phase': 'ModificationEnd' },
    { 'type': 'Game.AutoSaveSystem', 'phase': 'LateUpdate' },
    { 'type': 'Game.Notifications.IconCommandSystem', 'phase': 'ModificationEnd', 'action': 'Keep', 'reason': 'mandatory; client-local commands filtered' },
    { 'type': 'Game.Prefabs.UnlockSystem', 'phase': 'MainLoop', 'action': 'Keep', 'reason': 'processes synthesised Unlock events' }
  ],
  'sessionLoad': [
    { 'type': 'Game.Serialization.ResetBuildOrderSystem', 'phase': 'Deserialize', 'critical': true },
    { 'type': 'Game.Serialization.CitizenSystem', 'phase': 'Deserialize', 'critical': true },
    { 'type': 'Game.Serialization.RandomLocalizationSystem', 'phase': 'Deserialize' }
  ],
  'toolScoped': [
    { 'tool': 'Game.Tools.RouteToolSystem', 'systems': [ 'Game.Routes.WaypointConnectionSystem', 'Game.Routes.RoutePathSystem' ],
      'lingerSeconds': 2, 'guard': 'RouteGuard' }
  ]
}";

        [Test]
        public void AppendixEPassesTheSelfCheck()
        {
            GateTable table = Table(AppendixE);
            var world = new List<ResolvedSystem>();
            foreach (GateEntry entry in table.Entries) world.Add(Sys(entry.TypeName, entry.Phase));
            foreach (GateEntry entry in table.SessionLoadEntries) world.Add(Sys(entry.TypeName, entry.Phase));
            world.Add(Sys(RouteTool, "ToolUpdate"));
            world.Add(Sys(WaypointConnection, "Modification5"));
            world.Add(Sys(RoutePath, "ModificationEnd"));
            world.Add(Sys(CityStatistics, "GameSimulation"));
            world.Add(Sys(PropertyProcessing, "GameSimulation"));
            world.Add(Sys(Subsidies, "GameSimulation"));
            world.Add(Sys(Subsidies, "ModificationEnd"));
            world.Add(Sys(Simulation, "LateUpdate"));
            world.Add(Sys("Game.Simulation.StreetLightSystem", "GameSimulation"));
            world.Add(Sys("Game.Simulation.StreetLightSystem", "EditorSimulation"));
            world.Add(Sys("Game.Simulation.TrafficAmbienceSystem", "GameSimulation"));
            foreach (string name in Cosmetic) world.Add(Sys(name, "GameSimulation"));

            GatePlan plan = GatePlanner.Plan(table, null, world, Cosmetic);

            Assert.True(plan.Report.Ok, plan.Report.Describe());
            NoViolations(plan);
            Assert.Equal(0, plan.Report.Unresolved.Count, plan.Report.Describe());
            Assert.Equal(0, plan.Report.Conflicts.Count, plan.Report.Describe());
            Assert.Equal(0, plan.Report.Unclassified.Count, plan.Report.Describe());
            // 68 Off/OffAndFeed entries + CityStatistics, PropertyProcessing, StreetLight and TrafficAmbience by the phase rule
            Assert.Equal(68 + 4, plan.Disable.Count);
            Assert.Contains("Game.Simulation.StreetLightSystem", plan.Disable);   // never cosmetic (ARCH §6.1)
            Assert.Contains("Game.Simulation.MilestoneSystem", plan.Disable);
            Assert.SequenceEqual(new[] { RoutePath, WaypointConnection }, plan.ToolScoped);
            Assert.Equal(3, plan.SessionLoadDisable.Count);
            var expectedKeep = new List<string>(Cosmetic) { Subsidies, IconCommand, "Game.Prefabs.UnlockSystem" };
            expectedKeep.Sort(StringComparer.Ordinal);
            Assert.SequenceEqual(expectedKeep, plan.Keep);
            Assert.False(new List<string>(plan.Disable).Contains(Simulation), "the load hand-over owns SimulationSystem");
        }
    }
}
