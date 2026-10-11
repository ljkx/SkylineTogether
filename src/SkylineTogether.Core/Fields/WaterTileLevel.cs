namespace SkylineTogether.Core.Fields
{
    /// <summary>
    /// Which channels of <c>WaterSystem.WaterTexture</c> (RGBA32F: x = depth, y/z = velocity, w = pollution;
    /// <c>Game.Simulation/SurfaceWater.cs</c> [V]) a water correction tile carries (ARCH §4.10.10). The values are
    /// part of the wire format.
    /// </summary>
    public enum WaterTileLevel : byte
    {
        /// <summary>Depth (1 mm steps) and pollution (u8); quality Low and Medium. The client simulation re-derives
        /// the velocity, so the decoder writes zero velocity.</summary>
        DepthPollution = 0,

        /// <summary>Depth, pollution and velocity as two half floats; quality High.</summary>
        DepthPollutionVelocity = 1
    }
}
