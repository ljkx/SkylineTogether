using System.Collections.Generic;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Transport
{
    /// <summary>The Steam lane plan (ARCHITECTURE.md#steam-networking-sockets): one transport lane per <see cref="Lane"/>, shared by weight.</summary>
    public sealed class LanePlanTests
    {
        [Test]
        public void EveryLaneHasItsOwnTransportLane()
        {
            var seen = new HashSet<int>();
            foreach (Lane lane in new[] { Lane.Reliable, Lane.Unreliable, Lane.Bulk })
            {
                int index = LanePlan.IndexOf(lane);
                Assert.InRange(index, 0, LanePlan.Count - 1);
                Assert.True(seen.Add(index), "lane " + lane + " shares a transport lane");
                Assert.Equal(lane, LanePlan.LaneOf(index), "round trip of " + lane);
            }
            Assert.Equal(0, LanePlan.IndexOf(Lane.Reliable), "lane R is Steam's default lane 0 (SendMessageToConnection)");
        }

        [Test]
        public void UnknownTransportLanesCountAsReliable()
        {
            Assert.Equal(Lane.Reliable, LanePlan.LaneOf(-1));
            Assert.Equal(Lane.Reliable, LanePlan.LaneOf(LanePlan.Count));
            Assert.Equal(Lane.Reliable, LanePlan.LaneOf(65535));
        }

        [Test]
        public void LanesShareByWeightWithoutStarvation()
        {
            int[] priorities = LanePlan.Priorities();
            ushort[] weights = LanePlan.Weights();
            Assert.Equal(LanePlan.Count, priorities.Length);
            Assert.Equal(LanePlan.Count, weights.Length);
            foreach (int p in priorities) Assert.Equal(priorities[0], p, "equal priorities: bulk is never starved by a busy reliable lane");
            foreach (ushort w in weights) Assert.True(w > 0, "every lane has a share");
            int r = weights[LanePlan.IndexOf(Lane.Reliable)], u = weights[LanePlan.IndexOf(Lane.Unreliable)], b = weights[LanePlan.IndexOf(Lane.Bulk)];
            Assert.True(r > b, "control outweighs bulk");
            Assert.True(u > b, "presence and motion outweigh bulk");
            // fresh arrays: a caller that changes one cannot change the plan
            LanePlan.Weights()[0] = 99;
            Assert.Equal((ushort)r, LanePlan.Weights()[0]);
        }
    }
}
