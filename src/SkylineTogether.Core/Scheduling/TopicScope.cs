using System;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// Which entities beyond the in-view static set an active topic needs (ARCHITECTURE.md#simulation-values): the host's attention
    /// queries (game-host-capture) collect them into the peer's attention sets
    /// for a peer whose topics ask for them. See <see cref="TopicMap.ScopeOf"/>.
    /// </summary>
    [Flags]
    public enum TopicScope : byte
    {
        None = 0,

        /// <summary>Citizens of the households renting in-view buildings (happiness, health, wellbeing, education and
        /// age colours read <c>Citizen</c> through <c>Renter</c> → <c>HouseholdCitizen</c>, <c>ObjectColorSystem.cs:904-965</c>).</summary>
        ResidentsOfInView = 1,

        /// <summary>Households renting in-view buildings, with their <c>Resources</c> (wealth colour, <c>ObjectColorSystem.cs:876-903</c>).</summary>
        HouseholdsOfInView = 2,

        /// <summary>Companies renting in-view buildings (profitability colour reads <c>CompanyStatisticData</c>,
        /// <c>ObjectColorSystem.cs:993-1017</c>).</summary>
        CompaniesOfInView = 4,

        /// <summary>Every transport line with its waypoints, segments and stops (line values of every line).</summary>
        AllLines = 8,

        /// <summary>The city statistic entities (<c>CityStatistic</c> buffers).</summary>
        StatisticEntities = 16
    }
}
