using System;
using System.Collections.Generic;
using System.Linq;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// The client's reference safety beyond the link graph (ARCHITECTURE.md#client-apply, #reference-safety): the
    /// update-group markers every created entity needs, the agent spawn rules, the normalised agent links that records and
    /// hashes carry on both sides, the referrer index and the deleted-entity set of the orphan sweep.
    /// </summary>
    public sealed class ReferenceSafetyTests
    {
        // ------------------------------------------------------------------ update-group markers

        private static readonly string[] PetArchetype = { "Game.Prefabs.PrefabRef", "Game.Citizens.HouseholdPet", UpdateGroupMarkers.UpdateFrame, "Game.Common.Created", "Game.Common.Updated" };

        [Test]
        public void APetWithoutItsHouseholdLinkHasNoUpdateGroupMarker()
        {
            // the newest crash: 5- and 6-component archetypes with UpdateFrame and no marker, i.e. household pets whose
            // HouseholdPet the removal clean-up had taken away
            Assert.True(UpdateGroupMarkers.IsComplete(PetArchetype));
            Assert.False(UpdateGroupMarkers.IsComplete(PetArchetype.Where(t => t != "Game.Citizens.HouseholdPet")));
            Assert.False(UpdateGroupMarkers.IsComplete(new[] { "Game.Prefabs.PrefabRef", UpdateGroupMarkers.UpdateFrame, "Game.Common.Deleted", "Game.Citizens.CurrentBuilding", "Game.Citizens.CurrentTransport" }));
            Assert.True(UpdateGroupMarkers.IsComplete(new[] { "Game.Prefabs.PrefabRef", "Game.Common.Created" }), "no UpdateFrame: nothing to pick");
            Assert.True(UpdateGroupMarkers.IsComplete(null));
        }

        [Test]
        public void EveryMarkerOfTheGamesTableCounts()
        {
            Assert.Equal(13, UpdateGroupMarkers.TypeNames.Count);
            foreach (string marker in UpdateGroupMarkers.TypeNames)
            {
                Assert.True(UpdateGroupMarkers.IsMarker(marker), marker);
                Assert.True(UpdateGroupMarkers.IsComplete(new[] { UpdateGroupMarkers.UpdateFrame, marker }), marker);
            }
            Assert.False(UpdateGroupMarkers.IsMarker(UpdateGroupMarkers.UpdateFrame));
            Assert.False(UpdateGroupMarkers.IsMarker("Game.Citizens.HouseholdMember"), "a citizen's household link is not its marker (Citizen is)");
            // a creature that left its vehicle without getting Moving back has none of them
            Assert.False(UpdateGroupMarkers.IsComplete(new[] { UpdateGroupMarkers.UpdateFrame, "Game.Creatures.Human", "Game.Objects.Transform", "Game.Common.Deleted" }));
        }

        // ------------------------------------------------------------------ agent spawns

        private static SpawnComponent C(string type, bool data = true, bool resolved = true) => new SpawnComponent(type, data, resolved);

        [Test]
        public void AnAgentIsCreatedOnlyWithItsMarkerAndItsHousehold()
        {
            Assert.Equal(AgentSpawnVerdict.Create, AgentSpawnRules.Check(new[] { C("Game.Citizens.Citizen"), C("Game.Citizens.HouseholdMember") }, out _));
            Assert.Equal(AgentSpawnVerdict.Create, AgentSpawnRules.Check(new[] { C("Game.Companies.CompanyData"), C("Game.Buildings.PropertyRenter", resolved: false) }, out _),
                         "a property that is not here yet is an optional link");
            Assert.Equal(AgentSpawnVerdict.DefiningLinkMissing, AgentSpawnRules.Check(new[] { C("Game.Citizens.Citizen"), C("Game.Citizens.HouseholdMember", resolved: false) }, out string type));
            Assert.Equal("Game.Citizens.HouseholdMember", type);
            Assert.Equal(AgentSpawnVerdict.DefiningLinkMissing, AgentSpawnRules.Check(new[] { C("Game.Citizens.HouseholdPet", resolved: false), C("Game.Prefabs.PrefabRef") }, out type));
            Assert.Equal("Game.Citizens.HouseholdPet", type);
            Assert.Equal(AgentSpawnVerdict.MarkerWithoutValue, AgentSpawnRules.Check(new[] { C("Game.Citizens.Household", data: false) }, out type));
            Assert.Equal("Game.Citizens.Household", type);
            Assert.Equal(AgentSpawnVerdict.NoMarker, AgentSpawnRules.Check(new[] { C("Game.Prefabs.PrefabRef"), C("Game.Citizens.CurrentBuilding") }, out _));
            Assert.Equal(AgentSpawnVerdict.NoMarker, AgentSpawnRules.Check(null, out _));
        }

        // ------------------------------------------------------------------ normalised links: hash and repair agree

        private static LinkTarget State(int e) => e == 0 ? LinkTarget.Null : e < 0 ? LinkTarget.Destroyed : e >= 1000 ? LinkTarget.Deleted : LinkTarget.Live;

        [Test]
        public void AHostListWithGoneMembersHashesLikeTheClientsRepairedList()
        {
            // the repair loop: the host's company still listed workers that are gone (a save writes them as nobody); the
            // client dropped them, asked the host, got the same list back, and the hashes never agreed
            var host = new List<int> { 5, 0, 7, -3, 1001, 9 };
            var client = new List<int> { 9, 7, 5 };
            Assert.True(AgentLinkNormalization.TryHashMembers(host, State, e => (uint)e, out ulong h));
            Assert.True(AgentLinkNormalization.TryHashMembers(client, State, e => (uint)e, out ulong c));
            Assert.Equal(c, h);
            Assert.Equal(SetHash.Of(new uint[] { 5, 7, 9 }), h);
            Assert.False(AgentLinkNormalization.TryHashMembers(new List<int> { 5, 6 }, State, e => e == 6 ? 0u : (uint)e, out _), "a live member without an id: presence only");
            Assert.True(AgentLinkNormalization.TryHashMembers(new List<int> { 0, -1 }, State, e => 0u, out ulong empty), "gone members need no id");
            Assert.Equal(SetHash.Of(new uint[0]), empty);
            foreach (LinkTarget t in Enum.GetValues(typeof(LinkTarget))) Assert.Equal(t == LinkTarget.Live, AgentLinkNormalization.KeepsElement(t));
        }

        [Test]
        public void AForwardLinkTravelsTheWayTheReadersRepairLeavesIt()
        {
            foreach (AgentLink link in AgentLinkGraph.Forward)
            {
                Assert.Equal(ForwardInRecord.Value, AgentLinkNormalization.ForwardLink(link, LinkTarget.Live, true), link.ToString());
                foreach (LinkTarget gone in new[] { LinkTarget.Null, LinkTarget.Destroyed, LinkTarget.Deleted })
                {
                    ForwardInRecord sent = AgentLinkNormalization.ForwardLink(link, gone, true);
                    // what the reader does with a nobody reference must leave it where the record put it: no repair, no request
                    LinkCleanup reader = AgentLinkGraph.Rule(link).Decide(LinkTarget.Null);
                    if (sent == ForwardInRecord.Omit) Assert.Equal(LinkCleanup.RemoveComponent, AgentLinkGraph.Rule(link).Decide(gone), link + " " + gone);
                    else Assert.True(reader == LinkCleanup.Keep || reader == LinkCleanup.ClearReference || reader == LinkCleanup.DeleteHolder, link + " " + gone);
                }
            }
            Assert.Equal(ForwardInRecord.Omit, AgentLinkNormalization.ForwardLink(AgentLink.Worker, LinkTarget.Destroyed, true), "a workplace that is gone");
            Assert.Equal(ForwardInRecord.Omit, AgentLinkNormalization.ForwardLink(AgentLink.Worker, LinkTarget.Live, false), "a workplace the reader cannot name");
            Assert.Equal(ForwardInRecord.Null, AgentLinkNormalization.ForwardLink(AgentLink.TouristHotel, LinkTarget.Deleted, true));
            Assert.Equal(ForwardInRecord.Null, AgentLinkNormalization.ForwardLink(AgentLink.PropertyRenter, LinkTarget.Null, true), "a company without a property yet");
            Assert.Equal(ForwardInRecord.Omit, AgentLinkNormalization.ForwardLink(AgentLink.PropertyRenter, LinkTarget.Destroyed, true));
            Assert.Throws<ArgumentException>(() => AgentLinkNormalization.ForwardLink(AgentLink.Employee, LinkTarget.Live, true));
        }

        [Test]
        public void OnlyAListThatNamedSomethingGoneIsAskedFromTheHostAgain()
        {
            Assert.True(RepairRequestPolicy.AskHolder(false, true, 0), "a cleared or removed forward link");
            Assert.True(RepairRequestPolicy.AskHolder(false, false, 0));
            Assert.False(RepairRequestPolicy.AskHolder(true, true, 0), "snapshot nulls: the host's normalised record is the same");
            Assert.True(RepairRequestPolicy.AskHolder(true, true, 2));
            Assert.False(RepairRequestPolicy.AskHolder(true, false, 2), "a building's list follows its renters' own records");
        }

        // ------------------------------------------------------------------ the referrer index

        [Test]
        public void TheReferrerIndexKeepsEachPairOnceAndForgetsADeletedTarget()
        {
            var index = new AgentReferrerIndex<int>(2);
            Assert.True(index.Add(1, 10));
            Assert.False(index.Add(1, 10), "known");
            Assert.True(index.Add(1, 11));
            Assert.True(index.Add(2, 10));
            Assert.False(index.Add(0, 10) || index.Add(1, 0) || index.Add(3, 3), "nobody and the entity itself are never noted");
            Assert.Equal(3, index.Count);
            Assert.Equal(2, index.Targets);
            var output = new List<int>();
            Assert.Equal(2, index.Get(1, output));
            Assert.SequenceEqual(new[] { 10, 11 }, output.OrderBy(x => x));
            Assert.True(index.Contains(2, 10) && !index.Contains(2, 11));
            Assert.Equal(2, index.RemoveTarget(1));
            Assert.Equal(0, index.RemoveTarget(1));
            Assert.Equal(1, index.Count);
            Assert.True(index.Add(1, 12), "the freed nodes are reused");
            index.Clear();
            Assert.Equal(0, index.Count);
            Assert.Equal(0, index.Targets);
        }

        [Test]
        public void ReferrersThatAreGoneArePrunedInPassing()
        {
            var alive = new HashSet<int> { 10, 11, 12 };
            var index = new AgentReferrerIndex<int>();
            index.Add(1, 10);
            index.Add(1, 11);
            alive.Remove(10);
            Assert.True(index.Add(1, 12, e => alive.Contains(e)));
            var output = new List<int>();
            index.Get(1, output);
            Assert.SequenceEqual(new[] { 11, 12 }, output.OrderBy(x => x));
            Assert.Equal(1L, index.Pruned);
            Assert.Equal(2, index.Count);
        }

        [Test]
        public void ALargeWorkplaceGetsAHashSetAndStaysExact()
        {
            var index = new AgentReferrerIndex<int>(4);
            var alive = new HashSet<int>();
            for (int w = 1; w <= 5000; w++)
            {
                alive.Add(w + 100);
                Assert.True(index.Add(7, w + 100, e => alive.Contains(e)));
            }
            Assert.Equal(1, index.LargeTargets);
            // every worker noted again (the sweep and every apply do that): nothing is added twice, and a known pair costs
            // one set lookup (counted, not timed: a walk of the chain would ask about every member)
            long aliveCalls = 0;
            for (int round = 0; round < 10; round++)
            {
                for (int w = 1; w <= 5000; w++)
                {
                    Assert.False(index.Add(7, w + 100, e =>
                    {
                        aliveCalls++;
                        return alive.Contains(e);
                    }));
                }
            }
            Assert.Equal(0L, aliveCalls, "re-noting a known pair walks nothing");
            Assert.Equal(5000, index.Count);
            // half of them leave the city; the chain is pruned once it doubled
            for (int w = 1; w <= 2500; w++) alive.Remove(w + 100);
            for (int w = 5001; w <= 10000; w++)
            {
                alive.Add(w + 100);
                index.Add(7, w + 100, e => alive.Contains(e));
            }
            var output = new List<int>();
            index.Get(7, output);
            Assert.True(output.All(e => alive.Contains(e) || e <= 2600), "pruned");
            Assert.True(index.Pruned >= 2500, "pruned " + index.Pruned);
            Assert.Equal(output.Count, output.Distinct().Count());
            foreach (int e in alive) Assert.True(index.Contains(7, e), e.ToString());
            Assert.True(index.RemoveTarget(7) > 0);
            Assert.Equal(0, index.LargeTargets);
            Assert.Equal(0, index.Count);
        }

        // ------------------------------------------------------------------ the orphan sweep's set

        [Test]
        public void TheDeletedSetMatchesIndexAndVersion()
        {
            var set = new IndexVersionSet();
            Assert.True(set.Add(5, 1));
            Assert.False(set.Add(5, 1));
            Assert.True(set.Add(100000, 3), "grows");
            Assert.True(set.Contains(5, 1));
            Assert.False(set.Contains(5, 2), "a reused slot is another entity");
            Assert.False(set.Contains(6, 1));
            Assert.False(set.Contains(-1, 1) || set.Add(-1, 1));
            Assert.False(set.Contains(1 << 30, 1), "beyond the bits");
            Assert.Equal(2, set.Count);
            set.Clear();
            Assert.Equal(0, set.Count);
            Assert.False(set.Contains(5, 1) || set.Contains(100000, 3));
        }
    }
}
