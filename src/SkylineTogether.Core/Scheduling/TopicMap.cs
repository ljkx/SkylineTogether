using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// Attention topics per component type (ARCH §4.8.3): which types a peer with an active infoview or panel topic gets
    /// at P1. <see cref="CreateDefault"/> starts from the policy table (<see cref="TypeDescriptor.Topics"/>, i.e. the
    /// <c>topics</c> of <c>policies.json</c>, which win where set) and fills the remaining types from the built-in table
    /// of ARCH §4.8.3, whose types are verified against the decompiled game (<see cref="DefaultTypeTopics"/>).
    /// <para>
    /// View-report encoding (the client game layer builds it, this class decodes it on the host):
    /// <see cref="Protocol.ViewReport.Infoview"/> is 0 when no infoview is active and any other value (an opaque id of
    /// the client's choosing, used only in diagnostics) when one is; <see cref="Protocol.ViewReport.InfomodeMask"/> carries
    /// the <see cref="AttentionTopic"/> bits of the active infomodes, computed with <see cref="InfomodeTopics"/>;
    /// <see cref="Protocol.ViewReport.PanelFlags"/> uses the bits of <see cref="ViewPanels"/>.
    /// </para>
    /// Thread-safe: <see cref="TopicsOf"/> is read by scheduler workers while <see cref="Map"/> may run on the main thread
    /// (single 32-bit writes; a reader sees the old or the new value).
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class TopicMap
    {
        /// <summary>Every defined topic bit (bits 0 ... 21).</summary>
        public const AttentionTopic AllTopics = (AttentionTopic)0x003FFFFFu;

        /// <summary>Topics whose data matters everywhere, not only in view: the line values of every line (transport
        /// infoview, transportation overview) and the city statistics (ARCH §4.8.3 table). The topic data of every other
        /// topic belongs to in-view entities and to the entities of <see cref="ScopeOf"/>.</summary>
        public const AttentionTopic GlobalTopics = AttentionTopic.Transport | AttentionTopic.Statistics;

        private const AttentionTopic ResidentTopics = AttentionTopic.Happiness | AttentionTopic.Health | AttentionTopic.Wellbeing
                                                      | AttentionTopic.Education | AttentionTopic.Age;

        private static readonly ReadOnlyCollection<KeyValuePair<string, AttentionTopic>> Defaults = BuildDefaults();

        private readonly ITypeCatalog _catalog;
        private readonly AttentionTopic[] _topics;
        private readonly object _lock = new object();
        private readonly List<string> _unresolved = new List<string>();

        /// <summary>An empty map over <paramref name="catalog"/> (every type maps to <see cref="AttentionTopic.None"/>).</summary>
        public TopicMap(ITypeCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            int count = Math.Max(0, catalog.Count);
            _topics = new AttentionTopic[Math.Min(count, ushort.MaxValue + 1)];
        }

        /// <summary>
        /// The built-in topic table of ARCH §4.8.3 (full type names, verified in the decompiled game): resident data
        /// (<c>Game.Citizens.Citizen</c>), wealth (<c>Household</c>, household <c>Resources</c>), profitability, the
        /// building producers and consumers, edge flow, coverage and land value, the line values of §4.11.3 and the
        /// statistics buffers.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, AttentionTopic>> DefaultTypeTopics => Defaults;

        /// <summary>Names given to <see cref="Map"/> that the catalog does not know (diagnostics).</summary>
        public IReadOnlyList<string> UnresolvedNames
        {
            get { lock (_lock) return _unresolved.ToArray(); }
        }

        /// <summary>
        /// The policy topics of every catalogued type, plus the built-in defaults for types whose policy names none.
        /// </summary>
        public static TopicMap CreateDefault(ITypeCatalog catalog)
        {
            var map = new TopicMap(catalog);
            for (int i = 0; i < map._topics.Length; i++)
            {
                TypeDescriptor descriptor = catalog[new TypeRank((ushort)i)];
                if (descriptor != null) map._topics[i] = descriptor.Topics & AllTopics;
            }
            foreach (KeyValuePair<string, AttentionTopic> entry in Defaults)
            {
                if (catalog.TryGetByName(entry.Key, out TypeDescriptor descriptor) && descriptor != null && descriptor.Rank.Value < map._topics.Length
                    && map._topics[descriptor.Rank.Value] == AttentionTopic.None)
                {
                    map._topics[descriptor.Rank.Value] = entry.Value;
                }
            }
            return map;
        }

        /// <summary>Sets the topics of a type by full name (replacing what it had; <see cref="AttentionTopic.None"/> clears).
        /// A name the catalog does not know is remembered in <see cref="UnresolvedNames"/> and has no effect.</summary>
        public void Map(string typeFullName, AttentionTopic topics)
        {
            TryMap(typeFullName, topics);
        }

        /// <summary>As <see cref="Map"/>; false when the catalog does not know the name.</summary>
        public bool TryMap(string typeFullName, AttentionTopic topics)
        {
            if (string.IsNullOrEmpty(typeFullName)) return false;
            if (_catalog.TryGetByName(typeFullName, out TypeDescriptor descriptor) && descriptor != null && descriptor.Rank.Value < _topics.Length)
            {
                _topics[descriptor.Rank.Value] = topics & AllTopics;
                return true;
            }
            lock (_lock)
            {
                if (!_unresolved.Contains(typeFullName)) _unresolved.Add(typeFullName);
            }
            return false;
        }

        /// <summary>The topics of a type; <see cref="AttentionTopic.None"/> for ranks outside the catalog.</summary>
        public AttentionTopic TopicsOf(TypeRank type)
        {
            AttentionTopic[] topics = _topics;
            return type.Value < topics.Length ? topics[type.Value] : AttentionTopic.None;
        }

        /// <summary>True when the type has at least one of <paramref name="topics"/>.</summary>
        public bool Matches(TypeRank type, AttentionTopic topics) => topics != AttentionTopic.None && (TopicsOf(type) & topics) != 0;

        /// <summary>
        /// The topics of the active infoview: none when <paramref name="infoviewId"/> is 0 (no infoview), otherwise the
        /// topic bits carried in <paramref name="infomodeMask"/> (see the class remarks; undefined bits are ignored).
        /// </summary>
        public static AttentionTopic FromInfoview(byte infoviewId, uint infomodeMask)
        {
            if (infoviewId == 0) return AttentionTopic.None;
            return (AttentionTopic)infomodeMask & AllTopics;
        }

        /// <summary>The topics raised by open panels: the transportation overview raises <see cref="AttentionTopic.Transport"/>
        /// (every line's values), the statistics panel <see cref="AttentionTopic.Statistics"/>; other panels raise none.</summary>
        public static AttentionTopic FromPanels(ushort panelFlags)
        {
            AttentionTopic topics = AttentionTopic.None;
            if ((panelFlags & ViewPanels.TransportationOverview) != 0) topics |= AttentionTopic.Transport;
            if ((panelFlags & ViewPanels.Statistics) != 0) topics |= AttentionTopic.Statistics;
            return topics;
        }

        /// <summary>The topics of a whole view report (infoview plus panels); none for null.</summary>
        public static AttentionTopic FromView(Protocol.ViewReport report)
        {
            if (report == null) return AttentionTopic.None;
            return FromInfoview(report.Infoview, report.InfomodeMask) | FromPanels(report.PanelFlags);
        }

        /// <summary>Which related entities the host should collect into a peer's topic set for these topics.</summary>
        public static TopicScope ScopeOf(AttentionTopic topics)
        {
            TopicScope scope = TopicScope.None;
            if ((topics & ResidentTopics) != 0) scope |= TopicScope.ResidentsOfInView;
            if ((topics & AttentionTopic.Wealth) != 0) scope |= TopicScope.HouseholdsOfInView;
            if ((topics & AttentionTopic.Profitability) != 0) scope |= TopicScope.CompaniesOfInView;
            if ((topics & AttentionTopic.Transport) != 0) scope |= TopicScope.AllLines;
            if ((topics & AttentionTopic.Statistics) != 0) scope |= TopicScope.StatisticEntities;
            return scope;
        }

        private static ReadOnlyCollection<KeyValuePair<string, AttentionTopic>> BuildDefaults()
        {
            var list = new List<KeyValuePair<string, AttentionTopic>>
            {
                // residents of in-view buildings: ObjectColorSystem reads Citizen through Renter → HouseholdCitizen (ObjectColorSystem.cs:904-965)
                Entry("Game.Citizens.Citizen", ResidentTopics),
                // wealth: Household + household Resources (ObjectColorSystem.cs:876-903, EconomyUtils.GetHouseholdTotalWealth)
                Entry("Game.Citizens.Household", AttentionTopic.Wealth),
                Entry("Game.Economy.Resources", AttentionTopic.Wealth),
                // profitability of renters (ObjectColorSystem.cs:993-1017)
                Entry("Game.Companies.CompanyStatisticData", AttentionTopic.Profitability),
                Entry("Game.Companies.Profitability", AttentionTopic.Profitability),
                // building producers and consumers (ObjectColorSystem.cs:850-875, 984-992, 1048-1070, 1161-1183)
                Entry("Game.Buildings.CrimeProducer", AttentionTopic.Crime),
                Entry("Game.Buildings.MailProducer", AttentionTopic.Mail),
                Entry("Game.Buildings.GarbageProducer", AttentionTopic.Garbage),
                Entry("Game.Buildings.ElectricityConsumer", AttentionTopic.Electricity),
                Entry("Game.Buildings.ElectricityProducer", AttentionTopic.Electricity),
                Entry("Game.Buildings.Battery", AttentionTopic.Electricity),
                Entry("Game.Buildings.WaterConsumer", AttentionTopic.Water),
                Entry("Game.Buildings.WaterPumpingStation", AttentionTopic.Water),
                Entry("Game.Buildings.SewageOutlet", AttentionTopic.Water),
                Entry("Game.Buildings.TelecomFacility", AttentionTopic.Telecom),
                // edges: flow vectors, coverage, land value, pollution (Game.Net/Road.cs; R04 §4.1; R07 §8.2)
                Entry("Game.Net.Road", AttentionTopic.Traffic),
                Entry("Game.Net.ServiceCoverage", AttentionTopic.Coverage),
                Entry("Game.Net.LandValue", AttentionTopic.LandValue),
                Entry("Game.Net.Pollution", AttentionTopic.Pollution | AttentionTopic.Noise),
                // flow status of the electricity and water infoviews (ARCH §4.11.5)
                Entry("Game.Simulation.ElectricityFlowEdge", AttentionTopic.Electricity),
                Entry("Game.Simulation.WaterPipeEdge", AttentionTopic.Water),
                // line values of every line (ARCH §4.11.3)
                Entry("Game.Routes.TransportLine", AttentionTopic.Transport),
                Entry("Game.Routes.RouteVehicle", AttentionTopic.Transport),
                Entry("Game.Routes.RouteInfo", AttentionTopic.Transport),
                Entry("Game.Routes.WaitingPassengers", AttentionTopic.Transport),
                Entry("Game.Routes.VehicleTiming", AttentionTopic.Transport),
                Entry("Game.Routes.TransportStop", AttentionTopic.Transport),
                Entry("Game.Routes.BoardingVehicle", AttentionTopic.Transport),
                Entry("Game.Routes.TaxiStand", AttentionTopic.Transport),
                // statistics panel (R14 §6.2)
                Entry("Game.City.CityStatistic", AttentionTopic.Statistics)
            };
            return list.AsReadOnly();
        }

        private static KeyValuePair<string, AttentionTopic> Entry(string name, AttentionTopic topics) => new KeyValuePair<string, AttentionTopic>(name, topics);
    }
}
