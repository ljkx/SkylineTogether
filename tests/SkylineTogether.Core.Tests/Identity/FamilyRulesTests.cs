using System;
using SkylineTogether.Core.Identity;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using M = SkylineTogether.Core.Identity.FamilyMarker;

namespace SkylineTogether.Core.Tests.Identity
{
    /// <summary>
    /// The family rule table of ARCH §3.2, one test per row (rule numbers as in the table), each with the markers of
    /// later rules added to prove first-match precedence.
    /// </summary>
    public sealed class FamilyRulesTests
    {
        private static readonly FamilyFacts NoFacts = default;
        private static readonly FamilyFacts InBuffer = new FamilyFacts(inOwnerSubObjectBuffer: true, ownerRouteTracked: false);
        private static readonly FamilyFacts RouteTracked = new FamilyFacts(inOwnerSubObjectBuffer: false, ownerRouteTracked: true);
        private static readonly FamilyFacts AllFacts = new FamilyFacts(true, true);

        /// <summary>Every marker of a later rule; added to a rule's own markers it must not change the result.</summary>
        private const M LaterEverything = M.Lane | M.ZoneBlock | M.Icon | M.Object | M.Secondary | M.Owner | M.ServiceUpgrade | M.Building
                                          | M.FlowGraph | M.Route | M.GateBypassRoutePrefab | M.NetNode | M.NetEdge | M.Aggregate | M.Vehicle
                                          | M.Creature | M.Area | M.Waypoint | M.Segment | M.Citizen | M.Household | M.HouseholdPet
                                          | M.CompanyData | M.TransientAgent | M.EventEntity | M.JournalOrChirpOrLifePath | M.CityStatistic
                                          | M.Singleton | M.WaterSource | M.OnlyModSerializable;

        private static void Expect(Family family, int rule, M markers, FamilyFacts facts, string because = null)
        {
            Family actual = FamilyRules.Classify(markers, facts, out int ruleNumber);
            string context = (because ?? string.Empty) + " [" + markers + "]";
            Assert.Equal(family, actual, context);
            Assert.Equal(rule, ruleNumber, context + " rule");
        }

        private static void ExpectForAllFacts(Family family, int rule, M markers, string because = null)
        {
            Expect(family, rule, markers, NoFacts, because);
            Expect(family, rule, markers, InBuffer, because);
            Expect(family, rule, markers, RouteTracked, because);
            Expect(family, rule, markers, AllFacts, because);
            Assert.False(FamilyRules.NeedsPerEntityFacts(markers), "no per-entity facts needed for [" + markers + "]");
        }

        [Test]
        public void Rule01_ToolLocal()
        {
            foreach (M local in new[] { M.Temp, M.LivePath, M.NullPrefab, M.AdapterLocal })
            {
                ExpectForAllFacts(Family.ToolLocal, 1, local);
                ExpectForAllFacts(Family.ToolLocal, 1, local | M.PrefabData, "before the prefab rule");
                ExpectForAllFacts(Family.ToolLocal, 1, local | M.Object | M.Building, "a tool preview of a building");
                ExpectForAllFacts(Family.ToolLocal, 1, local | M.Object | M.Owner, "no per-entity facts for a preview sub-object");
                ExpectForAllFacts(Family.ToolLocal, 1, local | M.NetEdge | M.Waypoint, "a preview edge or waypoint");
                ExpectForAllFacts(Family.ToolLocal, 1, local | LaterEverything);
            }
            ExpectForAllFacts(Family.ToolLocal, 1, M.Temp | M.LivePath | M.NullPrefab | M.AdapterLocal);
        }

        [Test]
        public void Rule02_Prefab()
        {
            ExpectForAllFacts(Family.Prefab, 2, M.PrefabData);
            ExpectForAllFacts(Family.Prefab, 2, M.PrefabData | M.Lane | M.Object | M.Owner);
            ExpectForAllFacts(Family.Prefab, 2, M.PrefabData | LaterEverything);
        }

        [Test]
        public void Rule03_Lane()
        {
            ExpectForAllFacts(Family.Lane, 3, M.Lane);
            ExpectForAllFacts(Family.Lane, 3, M.Lane | M.Owner, "lanes are owned by edges, nodes and buildings");
            ExpectForAllFacts(Family.Lane, 3, M.Lane | M.ZoneBlock | M.Icon | M.Object | M.Owner);
            ExpectForAllFacts(Family.Lane, 3, M.Lane | LaterEverything);
        }

        [Test]
        public void Rule04_ZoneBlock()
        {
            ExpectForAllFacts(Family.ZoneBlock, 4, M.ZoneBlock);
            ExpectForAllFacts(Family.ZoneBlock, 4, M.ZoneBlock | M.Owner);
            ExpectForAllFacts(Family.ZoneBlock, 4, M.ZoneBlock | (LaterEverything & ~M.Lane));
        }

        [Test]
        public void Rule05_Icon()
        {
            ExpectForAllFacts(Family.Icon, 5, M.Icon);
            ExpectForAllFacts(Family.Icon, 5, M.Icon | M.Owner);
            ExpectForAllFacts(Family.Icon, 5, M.Icon | (LaterEverything & ~(M.Lane | M.ZoneBlock)));
        }

        [Test]
        public void Rule06_SecondaryObject()
        {
            ExpectForAllFacts(Family.SecondaryObject, 6, M.Object | M.Secondary);
            ExpectForAllFacts(Family.SecondaryObject, 6, M.Object | M.Secondary | M.Owner, "secondary objects never consult the SubObject buffer");
            ExpectForAllFacts(Family.SecondaryObject, 6, M.Object | M.Secondary | M.Owner | M.Building | M.Vehicle | M.FlowGraph);
            ExpectForAllFacts(Family.SecondaryObject, 6, M.Object | M.Secondary | (LaterEverything & ~(M.Lane | M.ZoneBlock | M.Icon)));
            ExpectForAllFacts(Family.Generic, 23, M.Secondary, "Secondary without Object is not rule 6");
        }

        [Test]
        public void Rule07_PrefabSubObject()
        {
            Expect(Family.PrefabSubObject, 7, M.Object | M.Owner, InBuffer);
            Expect(Family.PrefabSubObject, 7, M.Object | M.Owner, AllFacts);
            Assert.True(FamilyRules.NeedsPerEntityFacts(M.Object | M.Owner));
            Expect(Family.Object, 13, M.Object | M.Owner, NoFacts, "an owned object outside the owner's SubObject buffer (area-spawned, placed by a tool)");
            Expect(Family.Object, 13, M.Object | M.Owner, RouteTracked);

            // the exclusions of SubObjectSystem's own criterion
            ExpectForAllFacts(Family.Object, 13, M.Object | M.Owner | M.ServiceUpgrade, "a service upgrade is an Object");
            ExpectForAllFacts(Family.Object, 13, M.Object | M.Owner | M.Building, "a sub-building is an Object");
            ExpectForAllFacts(Family.SecondaryObject, 6, M.Object | M.Owner | M.Secondary);
            // SubObjectReferencesSystem never lists vehicles and creatures (its query excludes them): no lookup needed
            ExpectForAllFacts(Family.Mover, 12, M.Object | M.Owner | M.Vehicle, "an owned vehicle (trailer, parked car)");
            ExpectForAllFacts(Family.Mover, 12, M.Object | M.Owner | M.Creature);

            // precedence over every later rule
            M later = M.FlowGraph | M.Route | M.GateBypassRoutePrefab | M.NetNode | M.NetEdge | M.Aggregate | M.Area | M.Waypoint | M.Segment
                      | M.Citizen | M.TransientAgent | M.EventEntity | M.CityStatistic | M.Singleton | M.WaterSource | M.OnlyModSerializable;
            Expect(Family.PrefabSubObject, 7, M.Object | M.Owner | later, InBuffer);
            Assert.True(FamilyRules.NeedsPerEntityFacts(M.Object | M.Owner | later));
        }

        [Test]
        public void Rule08_FlowGraph()
        {
            ExpectForAllFacts(Family.FlowGraph, 8, M.FlowGraph);
            ExpectForAllFacts(Family.FlowGraph, 8, M.FlowGraph | M.Owner);
            ExpectForAllFacts(Family.FlowGraph, 8, M.FlowGraph | M.NetNode, "flow nodes before the net node rule");
            ExpectForAllFacts(Family.FlowGraph, 8, M.FlowGraph | M.NetEdge);
            ExpectForAllFacts(Family.FlowGraph, 8, M.FlowGraph | M.Route | M.Owner | M.GateBypassRoutePrefab | M.Aggregate | M.Vehicle | M.Area
                                                   | M.Waypoint | M.Citizen | M.EventEntity | M.Singleton | M.OnlyModSerializable);
        }

        [Test]
        public void Rule09_OwnedRoute()
        {
            ExpectForAllFacts(Family.OwnedRoute, 9, M.Route | M.Owner | M.GateBypassRoutePrefab);
            ExpectForAllFacts(Family.Route, 15, M.Route | M.Owner, "an owned route whose prefab is not a gate bypass path is a Route");
            ExpectForAllFacts(Family.Route, 15, M.Route | M.GateBypassRoutePrefab, "rule 9 needs an owner");
            ExpectForAllFacts(Family.OwnedRoute, 9, M.Route | M.Owner | M.GateBypassRoutePrefab | M.NetNode | M.NetEdge | M.Aggregate | M.Vehicle
                                                    | M.Area | M.Waypoint | M.Segment | M.Citizen | M.Singleton | M.OnlyModSerializable);
        }

        [Test]
        public void Rule10_NetNodeAndNetEdge()
        {
            ExpectForAllFacts(Family.NetNode, 10, M.NetNode);
            ExpectForAllFacts(Family.NetEdge, 10, M.NetEdge);
            ExpectForAllFacts(Family.NetNode, 10, M.NetNode | M.Owner, "a building's sub-net node");
            ExpectForAllFacts(Family.NetEdge, 10, M.NetEdge | M.Owner);
            ExpectForAllFacts(Family.NetNode, 10, M.NetNode | M.NetEdge, "node before edge");
            ExpectForAllFacts(Family.NetEdge, 10, M.NetEdge | M.Aggregate | M.Vehicle | M.Area | M.Route | M.Waypoint | M.Citizen
                                                  | M.EventEntity | M.CityStatistic | M.Singleton | M.WaterSource | M.OnlyModSerializable);
        }

        [Test]
        public void Rule11_Aggregate()
        {
            ExpectForAllFacts(Family.Aggregate, 11, M.Aggregate);
            ExpectForAllFacts(Family.Aggregate, 11, M.Aggregate | M.Vehicle | M.Creature | M.Area | M.Route | M.Waypoint | M.Household
                                                     | M.TransientAgent | M.JournalOrChirpOrLifePath | M.Singleton | M.OnlyModSerializable);
        }

        [Test]
        public void Rule12_Mover()
        {
            ExpectForAllFacts(Family.Mover, 12, M.Vehicle);
            ExpectForAllFacts(Family.Mover, 12, M.Creature);
            ExpectForAllFacts(Family.Mover, 12, M.Vehicle | M.Object, "movers are objects too");
            ExpectForAllFacts(Family.Mover, 12, M.Creature | M.Object | M.Owner);
            ExpectForAllFacts(Family.Mover, 12, M.Vehicle | M.Object | M.Owner | M.Building | M.ServiceUpgrade);
            ExpectForAllFacts(Family.Mover, 12, M.Vehicle | M.Area | M.Route | M.Waypoint | M.Citizen | M.TransientAgent | M.EventEntity
                                                 | M.CityStatistic | M.Singleton | M.WaterSource | M.OnlyModSerializable);
        }

        [Test]
        public void Rule13_Object()
        {
            ExpectForAllFacts(Family.Object, 13, M.Object, "a top-level object");
            ExpectForAllFacts(Family.Object, 13, M.Object | M.Building);
            ExpectForAllFacts(Family.Object, 13, M.Object | M.Owner | M.Building, "a sub-building or extension");
            ExpectForAllFacts(Family.Object, 13, M.Object | M.Owner | M.ServiceUpgrade, "an upgrade");
            ExpectForAllFacts(Family.Object, 13, M.Object | M.Area | M.Route | M.Waypoint | M.Segment | M.Citizen | M.TransientAgent
                                                  | M.EventEntity | M.CityStatistic | M.Singleton | M.WaterSource | M.OnlyModSerializable);
        }

        [Test]
        public void Rule14_Area()
        {
            ExpectForAllFacts(Family.Area, 14, M.Area);
            ExpectForAllFacts(Family.Area, 14, M.Area | M.Owner, "a building's lot or sub-area");
            ExpectForAllFacts(Family.Area, 14, M.Area | M.Route | M.Waypoint | M.Segment | M.Citizen | M.TransientAgent | M.EventEntity
                                                | M.CityStatistic | M.Singleton | M.WaterSource | M.OnlyModSerializable);
        }

        [Test]
        public void Rule15_RouteWaypointSegment()
        {
            ExpectForAllFacts(Family.Route, 15, M.Route);
            ExpectForAllFacts(Family.Route, 15, M.Route | M.Waypoint | M.Citizen | M.EventEntity | M.Singleton | M.OnlyModSerializable);

            Expect(Family.Waypoint, 15, M.Waypoint | M.Owner, RouteTracked);
            Expect(Family.Segment, 15, M.Segment | M.Owner, RouteTracked);
            Expect(Family.Waypoint, 15, M.Waypoint | M.Owner, AllFacts);
            Assert.True(FamilyRules.NeedsPerEntityFacts(M.Waypoint | M.Owner));
            Assert.True(FamilyRules.NeedsPerEntityFacts(M.Segment | M.Owner));

            // an element whose owner route is not tracked belongs to a derived owned route (gate bypass, InitializeSystem)
            Expect(Family.OwnedRoute, 15, M.Waypoint | M.Owner, NoFacts);
            Expect(Family.OwnedRoute, 15, M.Segment | M.Owner, InBuffer);
            // the bypass segments carry the bypass prefab themselves: decided without the per-entity fact
            ExpectForAllFacts(Family.OwnedRoute, 15, M.Segment | M.Owner | M.GateBypassRoutePrefab);
            ExpectForAllFacts(Family.OwnedRoute, 15, M.Waypoint | M.Owner | M.GateBypassRoutePrefab);

            Expect(Family.Waypoint, 15, M.Waypoint | M.Segment | M.Owner, RouteTracked, "Waypoint wins over Segment");
            Expect(Family.Segment, 15, M.Segment | M.Owner | M.Citizen | M.TransientAgent | M.EventEntity | M.CityStatistic | M.Singleton
                                       | M.WaterSource | M.OnlyModSerializable, RouteTracked);
        }

        [Test]
        public void Rule16_Agent()
        {
            foreach (M agent in new[] { M.Citizen, M.Household, M.HouseholdPet, M.CompanyData })
            {
                ExpectForAllFacts(Family.Agent, 16, agent);
                ExpectForAllFacts(Family.Agent, 16, agent | M.TransientAgent | M.EventEntity | M.JournalOrChirpOrLifePath | M.CityStatistic
                                                    | M.Singleton | M.WaterSource | M.OnlyModSerializable);
            }
        }

        [Test]
        public void Rule17_TransientAgent()
        {
            ExpectForAllFacts(Family.TransientAgent, 17, M.TransientAgent);
            ExpectForAllFacts(Family.TransientAgent, 17, M.TransientAgent | M.EventEntity | M.JournalOrChirpOrLifePath | M.CityStatistic
                                                          | M.Singleton | M.WaterSource | M.OnlyModSerializable);
        }

        [Test]
        public void Rule18_Event()
        {
            ExpectForAllFacts(Family.Event, 18, M.EventEntity);
            ExpectForAllFacts(Family.Event, 18, M.JournalOrChirpOrLifePath);
            ExpectForAllFacts(Family.Event, 18, M.EventEntity | M.CityStatistic | M.Singleton | M.WaterSource | M.OnlyModSerializable);
            ExpectForAllFacts(Family.Event, 18, M.JournalOrChirpOrLifePath | M.CityStatistic | M.Singleton | M.WaterSource | M.OnlyModSerializable);
        }

        [Test]
        public void Rule19_Statistic()
        {
            ExpectForAllFacts(Family.Statistic, 19, M.CityStatistic);
            ExpectForAllFacts(Family.Statistic, 19, M.CityStatistic | M.Singleton | M.WaterSource | M.OnlyModSerializable);
        }

        [Test]
        public void Rule20_Singleton()
        {
            ExpectForAllFacts(Family.Singleton, 20, M.Singleton);
            ExpectForAllFacts(Family.Singleton, 20, M.Singleton | M.WaterSource | M.OnlyModSerializable);
        }

        [Test]
        public void Rule21_WaterSource()
        {
            ExpectForAllFacts(Family.WaterSource, 21, M.WaterSource, "water sources may lack PrefabRef");
            ExpectForAllFacts(Family.WaterSource, 21, M.WaterSource | M.OnlyModSerializable, "Water Features sources");
        }

        [Test]
        public void Rule22_ModEntity()
        {
            ExpectForAllFacts(Family.ModEntity, 22, M.OnlyModSerializable);
            ExpectForAllFacts(Family.ModEntity, 22, M.OnlyModSerializable | M.Owner);
        }

        [Test]
        public void Rule23_Generic()
        {
            ExpectForAllFacts(Family.Generic, 23, M.None);
            ExpectForAllFacts(Family.Generic, 23, M.Owner);
            ExpectForAllFacts(Family.Generic, 23, M.Owner | M.Building | M.ServiceUpgrade | M.GateBypassRoutePrefab, "markers that only qualify other rules");
            Expect(Family.Generic, 23, (M)(1UL << 40), NoFacts, "unknown marker bits are ignored");
        }

        [Test]
        public void FactsMatterExactlyWhereNeedsPerEntityFactsSaysSo()
        {
            var random = new DeterministicRandom(2026);
            var allFacts = new[] { NoFacts, InBuffer, RouteTracked, AllFacts };
            int consulted = 0;
            for (int i = 0; i < 20000; i++)
            {
                // a sparse random mask over the 35 defined markers, so later rules are reached often
                ulong mask = 0;
                int bits = random.Next(5);
                for (int b = 0; b < bits; b++) mask |= 1UL << random.Next(35);
                if (random.Next(3) == 0) mask |= (ulong)(M.Object | M.Owner);
                if (random.Next(4) == 0) mask |= (ulong)(random.Next(2) == 0 ? M.Waypoint : M.Segment);
                var markers = (M)mask;

                bool needs = FamilyRules.NeedsPerEntityFacts(markers);
                Family first = FamilyRules.Classify(markers, allFacts[0], out int firstRule);
                bool differs = false;
                foreach (FamilyFacts facts in allFacts)
                {
                    Family family = FamilyRules.Classify(markers, facts, out int rule);
                    Assert.InRange(rule, 1, FamilyRules.RuleCount);
                    if (family != first || rule != firstRule) differs = true;
                }
                Assert.Equal(needs, differs, "[" + markers + "]");
                if (needs) consulted++;
            }
            Assert.True(consulted > 1000, "the random masks exercised the fact-dependent rules (" + consulted + ")");
        }

        [Test]
        public void DefaultClassesFollowTheTreatmentColumn()
        {
            Assert.Equal(ReplicationClass.Excluded, FamilyRules.DefaultClass(Family.ToolLocal));
            Assert.Equal(ReplicationClass.Channel, FamilyRules.DefaultClass(Family.Prefab));
            Assert.Equal(ReplicationClass.Channel, FamilyRules.DefaultClass(Family.Icon));
            foreach (Family derived in new[] { Family.Lane, Family.ZoneBlock, Family.SecondaryObject, Family.PrefabSubObject, Family.FlowGraph, Family.OwnedRoute })
            {
                Assert.Equal(ReplicationClass.Derived, FamilyRules.DefaultClass(derived), derived.ToString());
                Assert.True(FamilyRules.IsDerived(derived), derived.ToString());
            }
            foreach (Family s in new[] { Family.NetNode, Family.NetEdge, Family.Aggregate, Family.Object, Family.Area, Family.Route, Family.Waypoint,
                                         Family.Segment, Family.Event, Family.WaterSource, Family.ModEntity, Family.Generic })
            {
                Assert.Equal(ReplicationClass.S, FamilyRules.DefaultClass(s), s.ToString());
                Assert.False(FamilyRules.IsDerived(s), s.ToString());
            }
            Assert.Equal(ReplicationClass.M, FamilyRules.DefaultClass(Family.Mover));
            Assert.Equal(ReplicationClass.A, FamilyRules.DefaultClass(Family.Agent));
            Assert.Equal(ReplicationClass.ALow, FamilyRules.DefaultClass(Family.TransientAgent));
            Assert.Equal(ReplicationClass.V, FamilyRules.DefaultClass(Family.Statistic));
            Assert.Equal(ReplicationClass.G, FamilyRules.DefaultClass(Family.Singleton));
            Assert.Equal(ReplicationClass.Excluded, FamilyRules.DefaultClass((Family)200), "undefined values fail closed");
            Assert.False(FamilyRules.IsDerived(Family.ToolLocal));
            Assert.False(FamilyRules.IsDerived(Family.Prefab));

            foreach (Family family in (Family[])Enum.GetValues(typeof(Family)))
            {
                // every defined family has a definite treatment
                ReplicationClass cls = FamilyRules.DefaultClass(family);
                Assert.True(family == Family.ToolLocal || cls != ReplicationClass.Excluded, family.ToString());
            }
        }

        [Test]
        public void EveryRuleIsReachableAndDescribed()
        {
            var reached = new bool[FamilyRules.RuleCount + 1];
            M[] samples =
            {
                M.Temp, M.PrefabData, M.Lane, M.ZoneBlock, M.Icon, M.Object | M.Secondary, M.Object | M.Owner, M.FlowGraph,
                M.Route | M.Owner | M.GateBypassRoutePrefab, M.NetNode, M.NetEdge, M.Aggregate, M.Vehicle, M.Object, M.Area, M.Route,
                M.Waypoint, M.Citizen, M.TransientAgent, M.EventEntity, M.CityStatistic, M.Singleton, M.WaterSource,
                M.OnlyModSerializable, M.None
            };
            foreach (M sample in samples)
            {
                FamilyRules.Classify(sample, AllFacts, out int rule);
                reached[rule] = true;
            }
            for (int rule = 1; rule <= FamilyRules.RuleCount; rule++)
            {
                Assert.True(reached[rule], "rule " + rule + " reached");
                Assert.NotEqual("unknown rule", FamilyRules.DescribeRule(rule));
            }
            Assert.Equal("unknown rule", FamilyRules.DescribeRule(0));
            Assert.Equal("unknown rule", FamilyRules.DescribeRule(24));
            Assert.Contains("Generic", FamilyRules.DescribeRule(23));
        }
    }
}
