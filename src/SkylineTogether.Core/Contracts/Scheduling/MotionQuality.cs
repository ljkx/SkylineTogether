namespace SkylineTogether.Core
{
    /// <summary>Steps of the motion degradation ladder (ARCHITECTURE.md#live-session).</summary>
    public enum MotionQuality : byte
    {
        Full,
        /// <summary>Far-field motion at half rate.</summary>
        FarHalf,
        /// <summary>Far-field motion at quarter rate.</summary>
        FarQuarter,
        /// <summary>Far field at quarter rate and a shrinking motion radius.</summary>
        Reduced
    }
}
