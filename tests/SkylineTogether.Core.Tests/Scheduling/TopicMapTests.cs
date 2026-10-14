using System;
using System.Collections.Generic;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Scheduling;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Scheduling
{
    /// <summary>
    /// Attention topics (ARCHITECTURE.md#simulation-values), which the game layer uses to decide which values a player with an infoview or a
    /// panel open gets first: the infomode tables, the view-report encoding (infoview + infomode mask + panel flags),
    /// and topic scopes.
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
            });
            Assert.Equal(AttentionTopic.None, TopicMap.FromView(null));
        }
    }
}
