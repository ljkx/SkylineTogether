namespace SkylineTogether.Core
{
    /// <summary>How a system-state entry is captured and applied (ARCH §4.10.1).</summary>
    public enum SystemStateCodecKind : byte
    {
        /// <summary>The system's own <c>Serialize</c>/<c>Deserialize</c> with the owned full-tag context.</summary>
        GameSerializable,
        /// <summary>Host <c>Serialize</c>, client <c>Deserialize</c> only, never <c>PostDeserialize</c> (CityStatisticsSystem).</summary>
        DeserializeOnly,
        /// <summary>Fields read and written through a probe.</summary>
        Reflection,
        /// <summary>A hand-written codec in the game layer (climate state, water parameters, city name).</summary>
        Custom
    }
}
