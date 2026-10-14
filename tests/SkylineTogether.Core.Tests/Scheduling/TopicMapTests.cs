using System;
using System.Collections.Generic;
using SkylineTogether.Core.Identity;
using SkylineTogether.Core.Json;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Scheduling;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Scheduling
{
    /// <summary>
    /// Attention topics (ARCH §4.8.3), which the game layer uses to decide which values a player with an infoview or a
    /// panel open gets first: the infomode tables, the view-report encoding (infoview + infomode mask + panel flags),
    /// topic scopes, and the per-type topic map built from the policy table and the built-in defaults.
    /// </summary>
    public sealed class TopicMapTests
    {
        [Test]
        public void AllTopicsIsExactlyTheDefinedTopics()
        {
            AttentionTopic all = AttentionTopic.None;
            foreach (AttentionTopic t in Enum.GetValues(typeof(AttentionTopic))) all |= t;
            Assert.Equal(all, TopicMap.AllTopics);
            Assert.Equal(AttentionTopic.Transport | AttentionTopic.Statistics, TopicMap.GlobalTopics);
        }

        [Test]
        public void InfomodeTablesMatchTheGameEnumsAndIgnoreUnknownValues()
        {
            // the tables mirror game enums value by value; a lost or extra row shifts every later topic
            Assert.Equal(31, InfomodeTopics.BuildingStatusCount, "BuildingStatusType values");
            Assert.Equal(13, InfomodeTopics.NetStatusCount, "NetStatusType values");
            Assert.Equal(20, InfomodeTopics.HeatmapCount, "HeatmapData values");
            Assert.Equal(6, InfomodeTopics.ObjectStatusCount, "ObjectStatusType values");
            Assert.Equal(AttentionTopic.Crime, InfomodeTopics.ForBuildingStatus(0));
            Assert.Equal(AttentionTopic.Profitability, InfomodeTopics.ForBuildingStatus(30), "OutsideTrading is the last row");
            Assert.Equal(AttentionTopic.Traffic, InfomodeTopics.ForNetStatus(12));
            Assert.Equal(AttentionTopic.NaturalResources, InfomodeTopics.ForHeatmap(19));
            Assert.Equal(AttentionTopic.NaturalResources, InfomodeTopics.ForObjectStatus(4));
            foreach (int offset in new[] { 0, 1, 1000 })
            {
                Assert.Equal(AttentionTopic.None, InfomodeTopics.ForBuildingStatus(InfomodeTopics.BuildingStatusCount + offset));
                Assert.Equal(AttentionTopic.None, InfomodeTopics.ForNetStatus(InfomodeTopics.NetStatusCount + offset));
                Assert.Equal(AttentionTopic.None, InfomodeTopics.ForHeatmap(InfomodeTopics.HeatmapCount + offset));
                Assert.Equal(AttentionTopic.None, InfomodeTopics.ForObjectStatus(InfomodeTopics.ObjectStatusCount + offset));
            }
            foreach (int bad in new[] { -1, int.MinValue, int.MaxValue })
            {
                Assert.Equal(AttentionTopic.None, InfomodeTopics.ForBuildingStatus(bad) | InfomodeTopics.ForNetStatus(bad) | InfomodeTopics.ForHeatmap(bad) | InfomodeTopics.ForObjectStatus(bad));
            }
            for (int i = 0; i < InfomodeTopics.BuildingStatusCount; i++)
            {
                Assert.Equal(InfomodeTopics.ForBuildingStatus(i), InfomodeTopics.ForBuildingStatus(i) & TopicMap.AllTopics, "defined bits only");
            }
            Assert.Equal(AttentionTopic.None, TopicMap.CreateDefault(EmptyCatalog()).TopicsOf(new TypeRank(0)), "an empty catalog maps nothing");
        }

        [Test]
        public void ViewReportsEncodeTopicsLosslesslyAndPanelsRaiseOnlyTheirTopics()
        {
            Prop.ForAll("view topics", 2200, 300, g =>
            {
                var topics = (AttentionTopic)g.UInt();
                uint mask = InfomodeTopics.ToInfomodeMask(topics);
                Assert.Equal(topics & TopicMap.AllTopics, TopicMap.FromInfoview((byte)g.Int(1, 256), mask), "an active infoview carries its topics");
                Assert.Equal(AttentionTopic.None, TopicMap.FromInfoview(0, mask), "no infoview, no topics");
                ushort panels = (ushort)g.Int(0, 65536);
                AttentionTopic fromPanels = TopicMap.FromPanels(panels);
                Assert.Equal((panels & ViewPanels.TransportationOverview) != 0, (fromPanels & AttentionTopic.Transport) != 0);
                Assert.Equal((panels & ViewPanels.Statistics) != 0, (fromPanels & AttentionTopic.Statistics) != 0);
                Assert.Equal(AttentionTopic.None, fromPanels & ~(AttentionTopic.Transport | AttentionTopic.Statistics), "other panels raise nothing");
                var report = new ViewReport { Infoview = (byte)g.Int(0, 3), InfomodeMask = mask, PanelFlags = panels };
                Assert.Equal(TopicMap.FromInfoview(report.Infoview, mask) | fromPanels, TopicMap.FromView(report));

                TopicScope scope = TopicMap.ScopeOf(topics);
                Assert.Equal((topics & InfomodeTopics.ResidentTopics) != 0, (scope & TopicScope.ResidentsOfInView) != 0);
                Assert.Equal((topics & AttentionTopic.Wealth) != 0, (scope & TopicScope.HouseholdsOfInView) != 0);
                Assert.Equal((topics & AttentionTopic.Profitability) != 0, (scope & TopicScope.CompaniesOfInView) != 0);
                Assert.Equal((topics & AttentionTopic.Transport) != 0, (scope & TopicScope.AllLines) != 0);
                Assert.Equal((topics & AttentionTopic.Statistics) != 0, (scope & TopicScope.StatisticEntities) != 0);
            });
            Assert.Equal(AttentionTopic.None, TopicMap.FromView(null));
            Assert.Equal(TopicScope.None, TopicMap.ScopeOf(AttentionTopic.None));
        }

        private static ITypeCatalog EmptyCatalog() => new TypeCatalogBuilder(PolicyTableModel.Parse(MiniJson.Parse("{\"version\": 4, \"types\": []}"))).Build();

        private static TypeDescriptorInput T(ulong hash, string name) => new TypeDescriptorInput
        {
            StableTypeHash = hash, FullName = name, AssemblyName = "Game", Kind = TypeKind.Serializable, SizeInChunk = 8
        };

        [Test]
        public void TheTopicMapTakesPolicyTopicsFirstThenTheDefaults()
        {
            const string policies = "{\"version\": 4, \"types\": [ { \"type\": \"Game.Net.Road\", \"topics\": [ \"Noise\" ] } ]}";
            var builder = new TypeCatalogBuilder(PolicyTableModel.Parse(MiniJson.Parse(policies)));
            builder.Add(T(0x1, "Game.Net.Road"));                  // policy says Noise; the default would say Traffic
            builder.Add(T(0x2, "Game.Citizens.Citizen"));          // default: the resident topics
            builder.Add(T(0x3, "Game.Routes.TransportLine"));      // default: Transport
            builder.Add(T(0x4, "Game.Objects.Unrelated"));         // nothing
            ITypeCatalog catalog = builder.Build();
            TopicMap map = TopicMap.CreateDefault(catalog);
            AttentionTopic Of(string name)
            {
                Assert.True(catalog.TryGetByName(name, out TypeDescriptor d));
                return map.TopicsOf(d.Rank);
            }
            Assert.Equal(AttentionTopic.Noise, Of("Game.Net.Road"), "the policy table wins");
            Assert.Equal(InfomodeTopics.ResidentTopics, Of("Game.Citizens.Citizen"));
            Assert.Equal(AttentionTopic.Transport, Of("Game.Routes.TransportLine"));
            Assert.Equal(AttentionTopic.None, Of("Game.Objects.Unrelated"));
            Assert.Equal(AttentionTopic.None, map.TopicsOf(new TypeRank(60000)), "ranks outside the catalog have no topics");

            catalog.TryGetByName("Game.Objects.Unrelated", out TypeDescriptor unrelated);
            Assert.True(map.TryMap("Game.Objects.Unrelated", AttentionTopic.Crime | (AttentionTopic)0x80000000u));
            Assert.Equal(AttentionTopic.Crime, map.TopicsOf(unrelated.Rank), "undefined bits are dropped");
            Assert.True(map.Matches(unrelated.Rank, AttentionTopic.Crime | AttentionTopic.Mail));
            Assert.False(map.Matches(unrelated.Rank, AttentionTopic.None), "no topic matches nothing");
            Assert.False(map.Matches(unrelated.Rank, AttentionTopic.Mail));
            map.Map("Game.Objects.Unrelated", AttentionTopic.None);
            Assert.Equal(AttentionTopic.None, map.TopicsOf(unrelated.Rank), "None clears");

            Assert.False(map.TryMap("Game.Not.There", AttentionTopic.Crime));
            map.Map("Game.Not.There", AttentionTopic.Mail);
            Assert.False(map.TryMap("", AttentionTopic.Crime));
            Assert.False(map.TryMap(null, AttentionTopic.Crime));
            Assert.SequenceEqual(new[] { "Game.Not.There" }, map.UnresolvedNames, "unknown names are remembered once");

            var names = new HashSet<string>();
            foreach (KeyValuePair<string, AttentionTopic> entry in TopicMap.DefaultTypeTopics)
            {
                Assert.True(names.Add(entry.Key), "default listed twice: " + entry.Key);
                Assert.True(entry.Value != AttentionTopic.None && (entry.Value & ~TopicMap.AllTopics) == 0, entry.Key);
            }
        }
    }
}
