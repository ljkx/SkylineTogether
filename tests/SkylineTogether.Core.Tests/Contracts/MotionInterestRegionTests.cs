using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>Motion interest regions with enter/leave hysteresis (ARCHITECTURE.md#simulation-values).</summary>
    public sealed class MotionInterestRegionTests
    {
        private static MotionInterestRegion Disc(float radius)
        {
            return new MotionInterestRegion { CenterX = 100, CenterZ = -50, Radius = radius, LeaveRadius = radius * 1.25f };
        }

        [Test]
        public void EntersInsideTheRadius()
        {
            MotionInterestRegion region = Disc(300);
            Assert.True(region.Contains(100, -50, false));
            Assert.True(region.Contains(399, -50, false));
            Assert.True(region.Contains(100, 250, false), "exactly on the radius counts as inside");
            Assert.False(region.Contains(401, -50, false));
        }

        [Test]
        public void LeavesOnlyBeyondTheLeaveRadius()
        {
            MotionInterestRegion region = Disc(300);
            // 340 m away: outside the enter radius, inside the leave radius (375 m)
            Assert.False(region.Contains(440, -50, false), "a mover outside interest does not enter at 340 m");
            Assert.True(region.Contains(440, -50, true), "a mover inside interest stays until 375 m");
            Assert.True(region.Contains(475, -50, true));
            Assert.False(region.Contains(476, -50, true));
        }

        [Test]
        public void LeaveRadiusBelowRadiusDoesNotShrinkTheRegion()
        {
            var region = new MotionInterestRegion { CenterX = 0, CenterZ = 0, Radius = 300, LeaveRadius = 0 };
            Assert.True(region.Contains(299, 0, true));
            Assert.False(region.Contains(301, 0, true));
        }

        [Test]
        public void FootprintPolygonExtendsTheRegion()
        {
            var region = new MotionInterestRegion
            {
                CenterX = 0, CenterZ = 0, Radius = 100, LeaveRadius = 125,
                // a square footprint from x 200..400, z -100..100
                FrustumXZ = new float[] { 200, -100, 400, -100, 400, 100, 200, 100 }
            };
            Assert.True(region.Contains(300, 0, false), "inside the footprint, far outside the disc");
            Assert.False(region.Contains(150, 0, false), "between disc and footprint");
            Assert.False(region.Contains(450, 0, false));
            // hysteresis on the footprint: scaled by 1.25 around the centre while inside
            Assert.True(region.Contains(450, 0, true), "450 m lies inside the footprint scaled to x 250..500");
            Assert.False(region.Contains(510, 0, true));
        }

        [Test]
        public void DegeneratePolygonsAndNonFiniteInputAreHandled()
        {
            var region = new MotionInterestRegion { CenterX = 0, CenterZ = 0, Radius = 100, LeaveRadius = 125, FrustumXZ = new float[] { 1, 2, 3, 4 } };
            Assert.False(region.Contains(1000, 0, false), "fewer than three vertices are ignored");
            Assert.True(region.Contains(10, 0, false));
            Assert.False(region.Contains(float.NaN, 0, true));
            Assert.False(region.Contains(0, float.PositiveInfinity, true));
            var zero = new MotionInterestRegion { CenterX = 0, CenterZ = 0, Radius = 0, LeaveRadius = 0 };
            Assert.False(zero.Contains(0, 0, false), "a zero radius contains nothing");
        }
    }
}
