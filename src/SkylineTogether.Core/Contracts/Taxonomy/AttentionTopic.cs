using System;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Infoview and panel topics of the attention model (ARCH §4.8.3): data matching an active topic of a peer is
    /// promoted to P1 for that peer.
    /// </summary>
    [Flags]
    public enum AttentionTopic : uint
    {
        None = 0,
        Happiness = 1,
        Health = 2,
        Wellbeing = 4,
        Education = 8,
        Age = 16,
        Wealth = 32,
        Profitability = 64,
        Crime = 128,
        Mail = 256,
        Garbage = 512,
        Electricity = 1024,
        Water = 2048,
        Traffic = 4096,
        Coverage = 8192,
        Pollution = 16384,
        LandValue = 32768,
        NaturalResources = 65536,
        Groundwater = 131072,
        Noise = 262144,
        Telecom = 524288,
        /// <summary>The transport infoview or the transportation overview: line values of every line.</summary>
        Transport = 1048576,
        /// <summary>The statistics panel: city statistic buffers.</summary>
        Statistics = 2097152
    }
}
