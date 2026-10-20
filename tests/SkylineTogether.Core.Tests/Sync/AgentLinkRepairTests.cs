using System;
using System.Collections.Generic;
using System.Linq;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// The agent link graph (ARCHITECTURE.md#reference-safety): the rule table, the deletion cascade plan run over an
    /// in-memory model of households, citizens, companies and buildings, the audit's repair decisions, and the pacing and
    /// bookkeeping of the reference sweep.
    /// </summary>
    public sealed class AgentLinkRepairTests
    {
        // ------------------------------------------------------------------ an in-memory world

        /// <summary>Entities are ints (0 = null); forward links are components with one target, reverse links lists.</summary>
        private sealed class Model : IAgentLinkView<int>
        {
            public readonly HashSet<int> Alive = new HashSet<int>();
            public readonly HashSet<int> Deleted = new HashSet<int>();
            public readonly Dictionary<(int, AgentLink), int> Forward = new Dictionary<(int, AgentLink), int>();
            public readonly Dictionary<(int, AgentLink), List<int>> Lists = new Dictionary<(int, AgentLink), List<int>>();

            public int New()
            {
                int e = Alive.Count + Deleted.Count + 100;
                while (Alive.Contains(e)) e++;
                Alive.Add(e);
                return e;
            }

            public void Link(int holder, AgentLink link, int target) => Forward[(holder, link)] = target;

            public void List(int owner, AgentLink link, params int[] members)
            {
                if (!Lists.TryGetValue((owner, link), out List<int> list)) Lists[(owner, link)] = list = new List<int>();
                list.AddRange(members);
            }

            public List<int> Members(int owner, AgentLink link) => Lists.TryGetValue((owner, link), out List<int> list) ? list : new List<int>();

            public bool Has(int holder, AgentLink link) => Forward.ContainsKey((holder, link));

            public int Target(int holder, AgentLink link) => Forward[(holder, link)];

            public LinkTarget StateOf(int e)
            {
                if (e == 0) return LinkTarget.Null;
                if (!Alive.Contains(e)) return LinkTarget.Destroyed;
                return Deleted.Contains(e) ? LinkTarget.Deleted : LinkTarget.Live;
            }

            public bool IsUsable(int e) => StateOf(e) == LinkTarget.Live;

            public bool TryGetForward(int holder, AgentLink link, out int target) => Forward.TryGetValue((holder, link), out target);

            public bool GetMembers(int owner, AgentLink link, List<int> members)
            {
                if (!Lists.TryGetValue((owner, link), out List<int> list)) return false;
                members.AddRange(list);
                return true;
            }

            /// <summary>What the game layer's executor does with one op (<c>AgentLinkAccess.Apply</c>).</summary>
            public int Apply(AgentLinkOp<int> op)
            {
                if (!IsUsable(op.Holder)) return 0;
                if (op.Action == LinkCleanup.DropElement) return Lists.TryGetValue((op.Holder, op.Link), out List<int> list) ? list.RemoveAll(m => m == op.Target) : 0;
                if (!TryGetForward(op.Holder, op.Link, out int at) || (at != op.Target && IsUsable(at))) return 0;
                return Clean(op.Holder, op.Link, op.Action);
            }

            private int Clean(int holder, AgentLink link, LinkCleanup action)
            {
                switch (action)
                {
                    case LinkCleanup.ClearReference:
                        if (Forward[(holder, link)] == 0) return 0;
                        Forward[(holder, link)] = 0;
                        return 1;
                    case LinkCleanup.RemoveComponent:
                        return Forward.Remove((holder, link)) ? 1 : 0;
                    default:
                        return 0;
                }
            }

            /// <summary>The referrer index the client keeps (null: the deletion finds holders through the lists only).</summary>
            public AgentReferrerIndex<int> Index;

            /// <summary>Entities the last deletions took along (citizens and pets of a household).</summary>
            public readonly List<int> TakenAlong = new List<int>();

            /// <summary>Notes every holder's links in <see cref="Index"/> (what the client does when it goes live).</summary>
            public void IndexAll()
            {
                Index = Index ?? new AgentReferrerIndex<int>(16);
                foreach (int e in Alive) AgentLinkGraph.IndexHolder(this, e, Index);
            }

            /// <summary>
            /// Deletes <paramref name="e"/> the way the client does: deletion plan (with the referrer index when there is one),
            /// executor, Deleted tags; destroyed at the end of the frame.
            /// </summary>
            public int Delete(int e)
            {
                var ops = new List<AgentLinkOp<int>>();
                var deletions = new List<int>();
                Func<int, IReadOnlyList<int>> referrers = null;
                if (Index != null)
                {
                    referrers = t =>
                    {
                        var list = new List<int>();
                        Index.Get(t, list);
                        return list;
                    };
                }
                AgentLinkGraph.PlanDeletion(this, e, referrers, ops, deletions);
                Assert.Equal(e, deletions[0]);
                Assert.True(ops.All(op => op.Action != LinkCleanup.DeleteHolder && op.Action != LinkCleanup.Keep));
                int n = 0;
                foreach (AgentLinkOp<int> op in ops) n += Apply(op);
                foreach (int d in deletions)
                {
                    if (d != e) TakenAlong.Add(d);
                    Deleted.Add(d);
                    Index?.RemoveTarget(d);
                }
                return n;
            }

            /// <summary>End of the frame: deleted entities are destroyed.</summary>
            public void EndFrame()
            {
                foreach (int e in Deleted) Alive.Remove(e);
                Deleted.Clear();
            }

            /// <summary>The audit's repair over every live holder (what <c>ReferenceAudit.Repair</c> decides); returns the changes.</summary>
            public int Audit()
            {
                int n = 0;
                foreach (var key in Lists.Keys.ToList())
                {
                    if (!IsUsable(key.Item1)) continue;
                    AgentLinkRule rule = AgentLinkGraph.Rule(key.Item2);
                    n += Lists[key].RemoveAll(m => rule.Decide(StateOf(m)) == LinkCleanup.DropElement);
                }
                foreach (var key in Forward.Keys.ToList())
                {
                    if (!IsUsable(key.Item1) || !Forward.ContainsKey(key)) continue;
                    LinkCleanup action = AgentLinkGraph.Rule(key.Item2).Decide(StateOf(Forward[key]));
                    if (action == LinkCleanup.DeleteHolder) n += 1 + Delete(key.Item1); // a citizen or pet whose household is gone
                    else n += Clean(key.Item1, key.Item2, action);
                }
                return n;
            }

            /// <summary>Every reference of a live holder that the game would follow into nothing.</summary>
            public List<string> Dangling()
            {
                var bad = new List<string>();
                foreach (KeyValuePair<(int, AgentLink), List<int>> pair in Lists)
                {
                    if (!IsUsable(pair.Key.Item1)) continue;
                    foreach (int m in pair.Value)
                    {
                        if (StateOf(m) != LinkTarget.Live) bad.Add(pair.Key.Item2 + " of " + pair.Key.Item1 + " -> " + m + " (" + StateOf(m) + ")");
                    }
                }
                foreach (KeyValuePair<(int, AgentLink), int> pair in Forward)
                {
                    if (!IsUsable(pair.Key.Item1)) continue;
                    if (AgentLinkGraph.Rule(pair.Key.Item2).Decide(StateOf(pair.Value)) != LinkCleanup.Keep) bad.Add(pair.Key.Item2 + " of " + pair.Key.Item1 + " -> " + pair.Value + " (" + StateOf(pair.Value) + ")");
                }
                return bad;
            }
        }

        /// <summary>A small city: a building with a household and a company, a hotel company with a tourist household, a school, a hospital.</summary>
        private sealed class City
        {
            public readonly Model M = new Model();
            public int Building, Household, Citizen, Partner, Pet, Company, HotelBuilding, Hotel, Tourist, School, Hospital, Pupil;

            public City()
            {
                Building = M.New();
                Household = M.New();
                Citizen = M.New();
                Partner = M.New();
                Pet = M.New();
                Company = M.New();
                HotelBuilding = M.New();
                Hotel = M.New();
                Tourist = M.New();
                School = M.New();
                Hospital = M.New();
                Pupil = M.New();
                // the household and the company rent in the building; the hotel company in the hotel building
                M.Link(Household, AgentLink.PropertyRenter, Building);
                M.Link(Company, AgentLink.PropertyRenter, Building);
                M.List(Building, AgentLink.Renter, Household, Company);
                M.Link(Hotel, AgentLink.PropertyRenter, HotelBuilding);
                M.List(HotelBuilding, AgentLink.Renter, Hotel);
                // members both ways
                foreach (int c in new[] { Citizen, Partner, Pupil }) M.Link(c, AgentLink.HouseholdMember, Household);
                M.List(Household, AgentLink.HouseholdCitizen, Citizen, Partner, Pupil);
                M.Link(Pet, AgentLink.HouseholdPet, Household);
                M.List(Household, AgentLink.HouseholdAnimal, Pet);
                // jobs, school, hospital
                M.Link(Citizen, AgentLink.Worker, Company);
                M.Link(Partner, AgentLink.Worker, Hotel);
                M.List(Company, AgentLink.Employee, Citizen);
                M.List(Hotel, AgentLink.Employee, Partner);
                M.Link(Pupil, AgentLink.CitizenStudent, School);
                M.List(School, AgentLink.SchoolStudent, Pupil);
                M.Link(Pupil, AgentLink.CurrentBuilding, Hospital);
                M.List(Hospital, AgentLink.Patient, Pupil);
                // a tourist household staying in the hotel
                M.Link(Tourist, AgentLink.TouristHotel, Hotel);
                M.List(Hotel, AgentLink.Renter, Tourist);
            }

            /// <summary>Deletes, ends the frame and returns what still dangles.</summary>
            public List<string> DeleteAndSettle(params int[] entities)
            {
                foreach (int e in entities) M.Delete(e);
                M.EndFrame();
                return M.Dangling();
            }
        }

        private static void NoneDangling(List<string> dangling) => Assert.Equal(0, dangling.Count, string.Join("; ", dangling));

        // ------------------------------------------------------------------ the rule table

        [Test]
        public void EveryLinkHasOneRuleWithAUniqueName()
        {
            Assert.Equal(Enum.GetValues(typeof(AgentLink)).Length, AgentLinkGraph.Count);
            var names = new HashSet<string>();
            for (int i = 0; i < AgentLinkGraph.Count; i++)
            {
                AgentLinkRule rule = AgentLinkGraph.Rules[i];
                Assert.Equal((AgentLink)i, rule.Link);
                Assert.True(names.Add(rule.Name), "duplicate name " + rule.Name);
            }
            Assert.Equal(AgentLinkGraph.Count, AgentLinkGraph.Forward.Length + AgentLinkGraph.Reverse.Length);
        }

        [Test]
        public void ReverseBuffersDropElementsAndForwardLinksNameTheirReverseBuffer()
        {
            foreach (AgentLink link in AgentLinkGraph.Reverse)
            {
                AgentLinkRule rule = AgentLinkGraph.Rule(link);
                Assert.True(rule.IsBuffer, link.ToString());
                Assert.Equal(LinkCleanup.DropElement, rule.OnGone);
                Assert.True(AgentLinkGraph.ForwardOf(link, new AgentLink[3]) > 0, link + " lists nobody's forward link");
            }
            foreach (AgentLink link in AgentLinkGraph.Forward)
            {
                AgentLinkRule rule = AgentLinkGraph.Rule(link);
                Assert.False(rule.IsBuffer, link.ToString());
                Assert.True(rule.OnGone == LinkCleanup.RemoveComponent || rule.OnGone == LinkCleanup.ClearReference || rule.OnGone == LinkCleanup.DeleteHolder, link.ToString());
                // only a household's members are deleted with it
                if (rule.OnGone == LinkCleanup.DeleteHolder) Assert.True(link == AgentLink.HouseholdMember || link == AgentLink.HouseholdPet, link.ToString());
                Assert.True(AgentLinkGraph.Rule(rule.Reverse).IsBuffer, link + " reverse");
                // a cleared reference is only used where the game itself keeps null
                if (rule.OnGone == LinkCleanup.ClearReference) Assert.True(rule.NullAllowed, link.ToString());
            }
        }

        [Test]
        public void RenterListsNameRentersHomelessHouseholdsAndHotelGuests()
        {
            var output = new AgentLink[3];
            int n = AgentLinkGraph.ForwardOf(AgentLink.Renter, output);
            Assert.Equal(3, n);
            Assert.SequenceEqual(new[] { AgentLink.PropertyRenter, AgentLink.HomelessHome, AgentLink.TouristHotel }, output.Take(n));
            n = AgentLinkGraph.ForwardOf(AgentLink.Patient, output);
            Assert.Equal(1, n);
            Assert.Equal(AgentLink.CurrentBuilding, output[0]);
            var reverse = new AgentLink[2];
            Assert.Equal(2, AgentLinkGraph.ReverseOf(AgentLink.CurrentBuilding, reverse));
            Assert.SequenceEqual(new[] { AgentLink.Patient, AgentLink.Occupant }, reverse);
            Assert.Equal(1, AgentLinkGraph.ReverseOf(AgentLink.Worker, reverse));
            Assert.Equal(AgentLink.Employee, reverse[0]);
            Assert.Equal(0, AgentLinkGraph.ReverseOf(AgentLink.Employee, reverse));
        }

        // ------------------------------------------------------------------ audit decisions

        [Test]
        public void ABufferElementNamingNobodyOrAGoneEntityIsDropped()
        {
            foreach (AgentLink link in AgentLinkGraph.Reverse)
            {
                AgentLinkRule rule = AgentLinkGraph.Rule(link);
                Assert.Equal(LinkCleanup.Keep, rule.Decide(LinkTarget.Live));
                Assert.Equal(LinkCleanup.DropElement, rule.Decide(LinkTarget.Null), link + ": a null element (snapshot leftover) is dropped");
                Assert.Equal(LinkCleanup.DropElement, rule.Decide(LinkTarget.Destroyed));
                Assert.Equal(LinkCleanup.DropElement, rule.Decide(LinkTarget.Deleted));
            }
        }

        [Test]
        public void ComponentLinksFollowTheGamesRemovalRules()
        {
            AgentLinkRule worker = AgentLinkGraph.Rule(AgentLink.Worker);
            Assert.Equal(LinkCleanup.RemoveComponent, worker.Decide(LinkTarget.Destroyed));
            Assert.Equal(LinkCleanup.RemoveComponent, worker.Decide(LinkTarget.Null), "the game never keeps a null workplace");
            AgentLinkRule renter = AgentLinkGraph.Rule(AgentLink.PropertyRenter);
            Assert.Equal(LinkCleanup.Keep, renter.Decide(LinkTarget.Null), "a company without a property yet");
            Assert.Equal(LinkCleanup.RemoveComponent, renter.Decide(LinkTarget.Deleted));
            AgentLinkRule hotel = AgentLinkGraph.Rule(AgentLink.TouristHotel);
            Assert.Equal(LinkCleanup.ClearReference, hotel.Decide(LinkTarget.Destroyed));
            Assert.Equal(LinkCleanup.Keep, hotel.Decide(LinkTarget.Null));
            // the game deletes a citizen or a pet whose household is gone (a pet without HouseholdPet has no update-group marker)
            Assert.Equal(LinkCleanup.DeleteHolder, AgentLinkGraph.Rule(AgentLink.HouseholdMember).Decide(LinkTarget.Destroyed));
            Assert.Equal(LinkCleanup.Keep, AgentLinkGraph.Rule(AgentLink.HouseholdMember).Decide(LinkTarget.Null), "the game gives such a citizen a new household itself");
            Assert.Equal(LinkCleanup.DeleteHolder, AgentLinkGraph.Rule(AgentLink.HouseholdPet).Decide(LinkTarget.Deleted));
            Assert.Equal(LinkCleanup.Keep, AgentLinkGraph.Rule(AgentLink.CurrentBuilding).Decide(LinkTarget.Null));
            Assert.Equal(LinkCleanup.ClearReference, AgentLinkGraph.Rule(AgentLink.HomelessHome).Decide(LinkTarget.Destroyed));
        }

        [Test]
        public void TheAuditRepairsSnapshotNullsAndAStaleHotelGuestList()
        {
            // the crash: an Employee list with null workers (a save writes a worker that is gone as null) and a hotel's
            // renter list naming a tourist household destroyed on this client
            var city = new City();
            Model m = city.M;
            m.List(city.Company, AgentLink.Employee, 0, 0);
            m.Alive.Remove(city.Tourist); // destroyed without a clean-up
            Assert.Equal(3, m.Dangling().Count);
            int repaired = m.Audit();
            Assert.Equal(3, repaired);
            NoneDangling(m.Dangling());
            Assert.SequenceEqual(new[] { city.Citizen }, m.Members(city.Company, AgentLink.Employee));
            Assert.Equal(0, m.Members(city.Hotel, AgentLink.Renter).Count);
            Assert.Equal(0, m.Audit(), "a second pass finds nothing");
        }

        [Test]
        public void TheAuditRemovesLinksToGoneOwnersAndKeepsNormalNulls()
        {
            var city = new City();
            Model m = city.M;
            int newCompany = m.New();
            m.Link(newCompany, AgentLink.PropertyRenter, 0); // still looking for a property: valid
            m.Alive.Remove(city.Company); // a workplace gone without a clean-up
            m.Audit();
            NoneDangling(m.Dangling());
            Assert.False(m.Has(city.Citizen, AgentLink.Worker));
            Assert.True(m.Has(newCompany, AgentLink.PropertyRenter));
            Assert.Equal(1, m.Members(city.Building, AgentLink.Renter).Count, "the building's list drops the gone company");
        }

        // ------------------------------------------------------------------ the deletion cascade

        [Test]
        public void ADepartingTouristLeavesItsHotelsRenterList()
        {
            var city = new City();
            NoneDangling(city.DeleteAndSettle(city.Tourist));
            Assert.Equal(0, city.M.Members(city.Hotel, AgentLink.Renter).Count);
        }

        [Test]
        public void ADeadCitizenLeavesItsHouseholdWorkplaceSchoolAndHospital()
        {
            var city = new City();
            NoneDangling(city.DeleteAndSettle(city.Pupil));
            Assert.SequenceEqual(new[] { city.Citizen, city.Partner }, city.M.Members(city.Household, AgentLink.HouseholdCitizen));
            Assert.Equal(0, city.M.Members(city.School, AgentLink.SchoolStudent).Count);
            Assert.Equal(0, city.M.Members(city.Hospital, AgentLink.Patient).Count);
            NoneDangling(city.DeleteAndSettle(city.Citizen));
            Assert.Equal(0, city.M.Members(city.Company, AgentLink.Employee).Count);
        }

        [Test]
        public void AClosedCompanyDismissesItsWorkersAndLeavesItsBuilding()
        {
            var city = new City();
            NoneDangling(city.DeleteAndSettle(city.Company));
            Assert.False(city.M.Has(city.Citizen, AgentLink.Worker));
            Assert.SequenceEqual(new[] { city.Household }, city.M.Members(city.Building, AgentLink.Renter));
            Assert.True(city.M.Has(city.Partner, AgentLink.Worker), "another company's worker keeps the job");
        }

        [Test]
        public void AClosedHotelClearsItsGuestsHotel()
        {
            var city = new City();
            NoneDangling(city.DeleteAndSettle(city.Hotel));
            Assert.True(city.M.Has(city.Tourist, AgentLink.TouristHotel));
            Assert.Equal(0, city.M.Target(city.Tourist, AgentLink.TouristHotel));
            Assert.False(city.M.Has(city.Partner, AgentLink.Worker));
            Assert.Equal(0, city.M.Members(city.HotelBuilding, AgentLink.Renter).Count);
        }

        [Test]
        public void ADemolishedBuildingEvictsItsRentersButNotOnesThatMovedOn()
        {
            var city = new City();
            Model m = city.M;
            // this client's list is out of date: a household that moved to another building is still listed
            int other = m.New();
            int moved = m.New();
            m.Link(moved, AgentLink.PropertyRenter, other);
            m.List(other, AgentLink.Renter, moved);
            m.List(city.Building, AgentLink.Renter, moved);
            NoneDangling(city.DeleteAndSettle(city.Building));
            Assert.False(m.Has(city.Household, AgentLink.PropertyRenter));
            Assert.False(m.Has(city.Company, AgentLink.PropertyRenter));
            Assert.Equal(other, m.Target(moved, AgentLink.PropertyRenter));
        }

        [Test]
        public void ARemovedHouseholdLeavesItsBuildingAndTakesItsMembersAndPetsAlong()
        {
            var city = new City();
            Model m = city.M;
            NoneDangling(city.DeleteAndSettle(city.Household));
            Assert.SequenceEqual(new[] { city.Company }, m.Members(city.Building, AgentLink.Renter));
            // the game (and the host) deletes the members with it: they are never left without their household link
            foreach (int member in new[] { city.Citizen, city.Partner, city.Pupil, city.Pet })
            {
                Assert.Contains(member, m.TakenAlong);
                Assert.False(m.Alive.Contains(member), member + " outlived its household");
            }
            // and they leave every list they were on: workplace, school, hospital
            Assert.Equal(0, m.Members(city.Company, AgentLink.Employee).Count);
            Assert.Equal(0, m.Members(city.Hotel, AgentLink.Employee).Count);
            Assert.Equal(0, m.Members(city.School, AgentLink.SchoolStudent).Count);
            Assert.Equal(0, m.Members(city.Hospital, AgentLink.Patient).Count);
        }

        [Test]
        public void ABatchThatDeletesAHouseholdWithItsMembersLeavesNothingBehind()
        {
            // the host's household removal deletes the members in the same frame: one batch, any order
            var city = new City();
            NoneDangling(city.DeleteAndSettle(city.Citizen, city.Household, city.Partner, city.Pupil, city.Pet));
            var again = new City();
            NoneDangling(again.DeleteAndSettle(again.Household, again.Pet, again.Pupil, again.Partner, again.Citizen));
        }

        [Test]
        public void TheCascadeNeverPlansAChangeOfTheDeletedEntityItself()
        {
            var city = new City();
            foreach (int e in new[] { city.Building, city.Household, city.Citizen, city.Company, city.Hotel, city.Tourist, city.School, city.Hospital, city.Pet })
            {
                var ops = new List<AgentLinkOp<int>>();
                AgentLinkGraph.PlanCascade(city.M, e, ops);
                Assert.True(ops.All(op => op.Holder != e), "planned a change of " + e);
                Assert.True(ops.All(op => op.Action != LinkCleanup.Keep));
            }
        }

        [Test]
        public void RandomDeletionsWithCascadeAndAuditNeverLeaveADanglingLink()
        {
            var random = new Random(17);
            for (int round = 0; round < 50; round++)
            {
                var city = new City();
                Model m = city.M;
                if (round % 2 == 0) m.IndexAll(); // with and without the referrer index
                List<int> all = m.Alive.ToList();
                int deletions = 1 + random.Next(5);
                for (int i = 0; i < deletions; i++) m.Delete(all[random.Next(all.Count)]);
                // something destroyed behind the client's back (a local system, a snapshot leftover)
                bool behindItsBack = random.Next(2) == 0;
                if (behindItsBack) m.Alive.Remove(all[random.Next(all.Count)]);
                m.EndFrame();
                if (!behindItsBack) NoneDangling(m.Dangling()); // the cascade alone keeps a consistent graph consistent
                m.Audit();
                m.EndFrame(); // what the audit deleted (a member of a household destroyed behind its back) is gone now
                m.Audit();
                NoneDangling(m.Dangling());
            }
        }

        // ------------------------------------------------------------------ the referrer index

        [Test]
        public void ADemolishedBuildingIsLeftByEveryoneInsideItAlthoughNoListNamesThem()
        {
            // the real session: the player bulldozed a building and 540 citizens kept CurrentBuilding naming it (only a
            // hospital's or shelter's list names the citizens inside it)
            var city = new City();
            Model m = city.M;
            var inside = new List<int>();
            for (int i = 0; i < 50; i++)
            {
                int c = m.New();
                m.Link(c, AgentLink.CurrentBuilding, city.Building);
                inside.Add(c);
            }
            m.Link(city.Citizen, AgentLink.CurrentBuilding, city.Building);
            var withoutIndex = new City();
            int visitor = withoutIndex.M.New();
            withoutIndex.M.Link(visitor, AgentLink.CurrentBuilding, withoutIndex.Building);
            Assert.Equal(1, withoutIndex.DeleteAndSettle(withoutIndex.Building).Count, "the lists alone miss a visitor");
            m.IndexAll();
            NoneDangling(city.DeleteAndSettle(city.Building));
            foreach (int c in inside) Assert.False(m.Has(c, AgentLink.CurrentBuilding));
            Assert.True(m.Alive.Contains(city.Citizen) && m.Has(city.Citizen, AgentLink.Worker), "only the link to the building goes");
        }

        [Test]
        public void ADeletedHouseholdTakesAlongAMemberItsOwnListDoesNotNameYet()
        {
            // the key links travel on a different stream than the lists: the newest crash had citizens whose household was
            // destroyed while its list did not name them
            var city = new City();
            Model m = city.M;
            int newcomer = m.New();
            m.Link(newcomer, AgentLink.HouseholdMember, city.Household);
            m.Link(newcomer, AgentLink.Worker, city.Company);
            m.List(city.Company, AgentLink.Employee, newcomer);
            m.IndexAll();
            NoneDangling(city.DeleteAndSettle(city.Household));
            Assert.Contains(newcomer, m.TakenAlong);
            Assert.False(m.Members(city.Company, AgentLink.Employee).Contains(newcomer), "it left its workplace's list too");
        }

        [Test]
        public void AStaleListNamingADeletedCitizenIsCleanedThroughTheIndex()
        {
            // a school still lists a pupil that moved to another school: the pupil's own link leads elsewhere
            var city = new City();
            Model m = city.M;
            int otherSchool = m.New();
            m.Link(city.Pupil, AgentLink.CitizenStudent, otherSchool);
            m.List(otherSchool, AgentLink.SchoolStudent, city.Pupil);
            m.IndexAll();
            NoneDangling(city.DeleteAndSettle(city.Pupil));
            Assert.Equal(0, m.Members(city.School, AgentLink.SchoolStudent).Count);
            Assert.Equal(0, m.Members(otherSchool, AgentLink.SchoolStudent).Count);
        }

        /// <summary>Counts the work a plan does through the view (calls and list elements copied).</summary>
        private sealed class CountingView : IAgentLinkView<int>
        {
            private readonly Model _m;
            public long Work;

            public CountingView(Model m) => _m = m;

            public bool IsUsable(int entity)
            {
                Work++;
                return _m.IsUsable(entity);
            }

            public bool TryGetForward(int holder, AgentLink link, out int target)
            {
                Work++;
                return _m.TryGetForward(holder, link, out target);
            }

            public bool GetMembers(int owner, AgentLink link, List<int> members)
            {
                int before = members.Count;
                bool found = _m.GetMembers(owner, link, members);
                Work += 1 + members.Count - before;
                return found;
            }
        }

        private static long PlanWork(int workerCount)
        {
            var m = new Model();
            int company = m.New();
            var workers = new List<int>();
            for (int i = 0; i < workerCount; i++)
            {
                int w = m.New();
                m.Link(w, AgentLink.Worker, company);
                workers.Add(w);
            }
            m.List(company, AgentLink.Employee, workers.ToArray());
            m.IndexAll();
            var referrers = new List<int>();
            m.Index.Get(company, referrers);
            Assert.Equal(workerCount, referrers.Count);
            var view = new CountingView(m);
            var ops = new List<AgentLinkOp<int>>();
            var deletions = new List<int>();
            AgentLinkGraph.PlanDeletion(view, company, t => referrers, ops, deletions);
            Assert.Equal(workerCount, ops.Count, "one change per worker, not one per list and referrer");
            return view.Work;
        }

        [Test]
        public void PlanningIsLinearInTheReferrersOfALargeWorkplace()
        {
            // counted, not timed (a wall-clock limit failed on slow machines and debug builds)
            long small = PlanWork(2000), large = PlanWork(20000);
            Assert.True(large <= 12 * small, "ten times the workers cost " + large + " against " + small + " (a quadratic plan costs about a hundred times)");
        }

        // ------------------------------------------------------------------ pacing and bookkeeping

        [Test]
        public void TheSweepGetsMoreTimeOnlyWhileBehindItsLap()
        {
            var pacer = new SweepPacer(10, 0.3, 2.0);
            pacer.StartLap(100);
            Assert.Near(0.3, pacer.BudgetMs(101, 0.5), 1e-9); // ahead
            Assert.Near(0.3, pacer.BudgetMs(105, 0.5), 1e-9); // on schedule
            double behind = pacer.BudgetMs(105, 0.4);
            Assert.True(behind > 0.3 && behind < 2.0, behind.ToString());
            Assert.Near(2.0, pacer.BudgetMs(105, 0.1), 1e-9); // far behind
            Assert.Near(2.0, pacer.BudgetMs(111, 0.99), 1e-9); // overdue
            pacer.LapDone(112);
            Assert.Equal(1L, pacer.Laps);
            Assert.Near(12, pacer.LastLapSeconds, 1e-9);
            Assert.False(pacer.InLap);
            pacer.StartLap(112, 20);
            Assert.Near(20, pacer.LapSeconds, 1e-9);
        }

        [Test]
        public void ASweepAtTheMinimumBudgetStillFinishesItsLapInTime()
        {
            // a lap of 6000 chunks at 0.05 ms per chunk in 60 frames a second: the budget adapts so the lap ends in time
            var pacer = new SweepPacer(10, 0.3, 2.0);
            const int chunks = 6000;
            const double perChunkMs = 0.05;
            double now = 0;
            int visited = 0;
            pacer.StartLap(now);
            while (visited < chunks && now < 60)
            {
                double budget = pacer.BudgetMs(now, (double)visited / chunks);
                visited += Math.Max(1, (int)(budget / perChunkMs));
                now += 1.0 / 60;
            }
            Assert.True(now <= 10.5, "lap took " + now + " s");
        }

        [Test]
        public void RepairLogLinesAreLimitedPerLinkAndSummarisedPerMinute()
        {
            var stats = new LinkRepairStats(3);
            int logged = 0;
            for (int i = 0; i < 10; i++)
            {
                if (stats.Note(1, 2, true, i % 2 == 0, 5 + i)) logged++;
            }
            Assert.Equal(LinkRepairStats.LinesPerLinkPerMinute, logged);
            Assert.True(stats.Note(2, 1, true, false, 6), "another link has its own limit");
            Assert.Equal(21L, stats.Repaired);
            Assert.Equal(20L, stats.RepairedOf(1));
            Assert.Equal(10L, stats.RepairedAfterApply);
            Assert.Null(stats.TakeSummary(30, null));
            string summary = stats.TakeSummary(65.0, i => "link" + i);
            Assert.Equal("21 in the last 60 s (link1 20, link2 1)", summary);
            Assert.Null(stats.TakeSummary(130, null), "nothing repaired since");
            Assert.True(stats.Note(1, 1, true, false, 70), "a new minute logs again");
            stats.NoteRequested(4);
            Assert.Equal(4L, stats.Requested);
            stats.Reset();
            Assert.Equal(0L, stats.Repaired);
        }

        [Test]
        public void RepairRequestsWaitAreDeduplicatedAndLimitedPerId()
        {
            var queue = new RepairRequestQueue(3, 4);
            Assert.True(queue.Add(7, 0));
            Assert.False(queue.Add(7, 1), "already queued");
            Assert.False(queue.Add(0, 1));
            Assert.True(queue.Add(8, 1));
            var due = new List<uint>();
            Assert.Equal(0, queue.TakeDue(2.9, 10, null, due));
            Assert.Equal(1, queue.TakeDue(3.0, 10, null, due));
            Assert.SequenceEqual(new uint[] { 7 }, due);
            due.Clear();
            // an id that arrived meanwhile is not asked for
            Assert.Equal(0, queue.TakeDue(10, 10, id => id != 8, due));
            Assert.Equal(0, queue.Count);
            // at most MaxPerWindow requests per id and window
            for (int i = 0; i < RepairRequestQueue.MaxPerWindow - 1; i++)
            {
                Assert.True(queue.Add(7, 20 + i * 10));
                queue.TakeDue(30 + i * 10, 10, null, due);
            }
            Assert.False(queue.Add(7, 60));
            Assert.Equal(1L, queue.Refused);
            Assert.True(queue.Add(7, 20 + RepairRequestQueue.WindowSeconds), "a new window");
            // bounded
            Assert.True(queue.Add(1, 400) && queue.Add(2, 400) && queue.Add(3, 400));
            Assert.False(queue.Add(4, 400));
            Assert.Equal(1L, queue.Dropped);
        }
    }
}
