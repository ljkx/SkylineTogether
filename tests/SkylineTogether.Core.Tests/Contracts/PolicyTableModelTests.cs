using System.Collections.Generic;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Contracts.DataFileSamples;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>policies.json parsing (ARCH §4.2.7, Appendix F), including family-scoped entries and stand-in resolvers.</summary>
    public sealed class PolicyTableModelTests
    {
        private static ConfigValidationException Invalid(string singleQuotedTypes)
        {
            string text = "{ 'version': 1, 'types': [ " + singleQuotedTypes + " ] }";
            return Assert.Throws<ConfigValidationException>(() => PolicyTableModel.Parse(J(text)), "expected policies.json to be rejected: " + singleQuotedTypes);
        }

        private static TypePolicyEntry Find(PolicyTableModel model, string type, Family? family = null)
        {
            foreach (TypePolicyEntry entry in model.Types)
            {
                if (entry.TypeName == type && entry.Family == family) return entry;
            }
            Assert.Fail("no entry for " + type + (family.HasValue ? " on " + family.Value : string.Empty));
            return null;
        }

        [Test]
        public void ParsesTheSample()
        {
            PolicyTableModel model = PolicyTableModel.Parse(J(Policies));
            Assert.Equal(3, model.Version);
            Assert.Equal(11, model.Types.Count);
            Assert.Equal(4, model.FamilyDefaults.Count);
            Assert.Equal(ReplicationClass.S, model.FamilyDefaults[Family.NetEdge]);
            Assert.Equal(ReplicationClass.A, model.FamilyDefaults[Family.Agent]);
            Assert.Equal(ReplicationClass.M, model.FamilyDefaults[Family.Mover]);
            Assert.Equal(ReplicationClass.ALow, model.FamilyDefaults[Family.TransientAgent], "family names match case-insensitively");

            TypePolicyEntry surface = Find(model, "Game.Objects.Surface");
            Assert.Equal(TypeFlags.Excluded, surface.Flags);
            Assert.Null(surface.Class);
            Assert.Null(surface.Family);
            Assert.Null(surface.Codec);
            Assert.Equal(0, surface.ReverseBuffers.Count);

            TypePolicyEntry road = Find(model, "Game.Net.Road");
            Assert.Equal(TypeFlags.PlayerAuthorable | TypeFlags.AuthorableMask, road.Flags);
            Assert.Equal("Road", road.AuthorableMask);
            Assert.Equal(AttentionTopic.Traffic, road.Topics);

            TypePolicyEntry building = Find(model, "Game.Buildings.Building");
            Assert.Equal(TypeFlags.DeferredFields | TypeFlags.AuthorableMask, building.Flags);
            Assert.Equal("Building", building.MergePolicy);

            TypePolicyEntry chirp = Find(model, "Game.Triggers.Chirp");
            Assert.Equal(TypeFlags.Merge, chirp.Flags);
            Assert.Equal("Chirp", chirp.MergePolicy);

            TypePolicyEntry pathOnMovers = Find(model, "Game.Pathfind.PathInformation");
            Assert.Equal(TypeFlags.OnDemand, pathOnMovers.Flags);
            Assert.Equal(ReplicationClass.OnDemand, pathOnMovers.Class);

            TypePolicyEntry pathOnSegments = Find(model, "Game.Pathfind.PathInformation", Family.Segment);
            Assert.Equal(ReplicationClass.S, pathOnSegments.Class);
            Assert.Equal(TypeFlags.Source | TypeFlags.CacheReceived | TypeFlags.Guarded | TypeFlags.PresenceSafe, pathOnSegments.Flags);
            Assert.Equal("SegmentPathEndpoints", pathOnSegments.StandInResolver);

            TypePolicyEntry renter = Find(model, "Game.Buildings.Renter");
            Assert.Equal(TypeFlags.DataReplicatedPresence | TypeFlags.HashAsSet, renter.Flags);
            Assert.Equal(CodecKind.EntityRefList, renter.Codec);

            TypePolicyEntry member = Find(model, "Game.Citizens.HouseholdMember");
            Assert.Equal(TypeFlags.ForwardReference, member.Flags);
            Assert.SequenceEqual(new[] { "Game.Citizens.HouseholdCitizen" }, member.ReverseBuffers);

            TypePolicyEntry upgraded = Find(model, "Game.Net.Upgraded");
            Assert.Equal(TouchFlags.PathfindUpdated | TouchFlags.Updated, upgraded.ApplyTags);
            Assert.Equal(TypeFlags.PlayerAuthorable, upgraded.Flags);

            Assert.Equal(TypeFlags.PresenceAuthorable, Find(model, "Game.Net.TrafficLights").Flags);

            TypePolicyEntry pathOwner = Find(model, "Game.Vehicles.PathOwner");
            Assert.Equal(ReplicationClass.OnDemand, pathOwner.Class);
            Assert.Equal(TypeFlags.OnDemand, pathOwner.Flags, "class OnDemand implies the OnDemand flag");
        }

        [Test]
        public void MinimalTableParses()
        {
            PolicyTableModel model = PolicyTableModel.Parse(J("{ 'version': 1 }"));
            Assert.Equal(0, model.Types.Count);
            Assert.Equal(0, model.FamilyDefaults.Count);
        }

        [Test]
        public void ClassAndExclusionRules()
        {
            Assert.Contains("use \"excluded\": true", Invalid("{ 'type': 'A.B', 'class': 'Excluded' }").Message);
            Assert.Contains("an excluded type has no class", Invalid("{ 'type': 'A.B', 'excluded': true, 'class': 'S' }").Message);
            Assert.Contains("class OnDemand", Invalid("{ 'type': 'A.B', 'onDemand': true, 'class': 'V' }").Message);
            Assert.Contains("exclude each other", Invalid("{ 'type': 'A.B', 'onDemand': true, 'excluded': true }").Message);
            Assert.Contains("unknown ReplicationClass", Invalid("{ 'type': 'A.B', 'class': 'X' }").Message);
            Assert.Contains("unknown CodecKind", Invalid("{ 'type': 'A.B', 'codec': 'Binary' }").Message);
        }

        [Test]
        public void PolicyFunctionRules()
        {
            Assert.Contains("mergePolicy and deferredFields exclude each other", Invalid("{ 'type': 'A.B', 'mergePolicy': 'X', 'deferredFields': 'Y' }").Message);
            Assert.Contains("needs a \"standInResolver\"", Invalid("{ 'type': 'A.B', 'presenceSafe': true }").Message);
            Assert.Contains("stand-in resolver makes the type presence-safe", Invalid("{ 'type': 'A.B', 'presenceSafe': false, 'standInResolver': 'S' }").Message);
            Assert.Contains("needs \"reverseBuffers\"", Invalid("{ 'type': 'A.B', 'forwardReference': true }").Message);
            Assert.Contains("make the type a forward reference", Invalid("{ 'type': 'A.B', 'forwardReference': false, 'reverseBuffers': [ 'C.D' ] }").Message);
            Assert.Contains("its own reverse buffer", Invalid("{ 'type': 'A.B', 'reverseBuffers': [ 'A.B' ] }").Message);
            Assert.Contains("playerAuthorable and presenceAuthorable exclude each other", Invalid("{ 'type': 'A.B', 'playerAuthorable': true, 'presenceAuthorable': true }").Message);
            Assert.Contains("identifier", Invalid("{ 'type': 'A.B', 'mergePolicy': 'Chirp Liked' }").Message);
            Assert.Contains("identifier", Invalid("{ 'type': 'A.B', 'authorableMask': '' }").Message);
        }

        [Test]
        public void EntriesAreUniquePerTypeAndFamily()
        {
            Assert.Contains("listed twice without a family", Invalid("{ 'type': 'A.B' }, { 'type': 'A.B', 'source': true }").Message);
            Assert.Contains("listed twice for the family Segment", Invalid("{ 'type': 'A.B', 'family': 'Segment' }, { 'type': 'A.B', 'family': 'segment' }").Message);
            PolicyTableModel ok = PolicyTableModel.Parse(J("{ 'version': 1, 'types': [ { 'type': 'A.B' }, { 'type': 'A.B', 'family': 'Segment' }, { 'type': 'A.B', 'family': 'Mover' } ] }"));
            Assert.Equal(3, ok.Types.Count);
        }

        [Test]
        public void NamesAndListsAreValidated()
        {
            Assert.Contains("unknown Family \"Building\"", Invalid("{ 'type': 'A.B', 'family': 'Building' }").Message);
            Assert.Contains("unknown AttentionTopic", Invalid("{ 'type': 'A.B', 'topics': [ 'Sound' ] }").Message);
            Assert.Contains("duplicate AttentionTopic", Invalid("{ 'type': 'A.B', 'topics': [ 'Crime', 'crime' ] }").Message);
            Assert.Contains("unknown TouchFlags", Invalid("{ 'type': 'A.B', 'applyTags': [ 'Created' ] }").Message);
            Assert.Contains("expected an array", Invalid("{ 'type': 'A.B', 'topics': 'Crime' }").Message);
            Assert.Contains("unknown member \"buffer\"", Invalid("{ 'type': 'A.B', 'buffer': true }").Message);
            Assert.Contains("true or false", Invalid("{ 'type': 'A.B', 'source': 1 }").Message);
        }

        [Test]
        public void FamilyDefaultsAreValidated()
        {
            ConfigValidationException Bad(string defaults)
            {
                return Assert.Throws<ConfigValidationException>(() => PolicyTableModel.Parse(J("{ 'version': 1, 'familyDefaults': " + defaults + " }")));
            }

            Assert.Contains("listed twice", Bad("{ 'Agent': 'A', 'agent': 'V' }").Message);
            Assert.Contains("expected a ReplicationClass name", Bad("{ 'Agent': 1 }").Message);
            Assert.Contains("unknown Family", Bad("{ 'People': 'A' }").Message);
            Assert.Contains("expected an object", Bad("[]").Message);
            var model = PolicyTableModel.Parse(J("{ 'version': 1, 'familyDefaults': { '_note': 'skipped', 'Generic': 'S' } }"));
            Assert.Equal(1, model.FamilyDefaults.Count);
        }

        [Test]
        public void ParsedListsAreReadOnly()
        {
            PolicyTableModel model = PolicyTableModel.Parse(J(Policies));
            Assert.True(model.Types is System.Collections.ObjectModel.ReadOnlyCollection<TypePolicyEntry>);
            Assert.False(model.FamilyDefaults is Dictionary<Family, ReplicationClass>, "the defaults are wrapped read-only");
        }
    }
}
