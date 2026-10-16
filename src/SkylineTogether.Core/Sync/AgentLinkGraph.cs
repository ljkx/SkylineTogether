using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Sync
{
    /// <summary>One link of the agent link graph (ARCHITECTURE.md#reference-safety): a reverse buffer or a forward component.</summary>
    public enum AgentLink : byte
    {
        // reverse buffers on the owner: one element per member
        /// <summary><c>Game.Buildings.Renter.m_Renter</c> on a building (households, companies) or a hotel company (tourist households).</summary>
        Renter,
        /// <summary><c>Game.Companies.Employee.m_Worker</c> on a company or a city service building.</summary>
        Employee,
        /// <summary><c>Game.Buildings.Student.m_Student</c> on a school.</summary>
        SchoolStudent,
        /// <summary><c>Game.Buildings.Patient.m_Patient</c> on a hospital.</summary>
        Patient,
        /// <summary><c>Game.Buildings.Occupant.m_Occupant</c> on a prison or shelter.</summary>
        Occupant,
        /// <summary><c>Game.Citizens.HouseholdCitizen.m_Citizen</c> on a household.</summary>
        HouseholdCitizen,
        /// <summary><c>Game.Citizens.HouseholdAnimal.m_HouseholdPet</c> on a household.</summary>
        HouseholdAnimal,

        // forward components on the member
        /// <summary><c>Game.Buildings.PropertyRenter.m_Property</c> of a household or company.</summary>
        PropertyRenter,
        /// <summary><c>Game.Citizens.HomelessHousehold.m_TempHome</c> of a homeless household.</summary>
        HomelessHome,
        /// <summary><c>Game.Citizens.TouristHousehold.m_Hotel</c> of a tourist household.</summary>
        TouristHotel,
        /// <summary><c>Game.Citizens.Worker.m_Workplace</c> of a citizen.</summary>
        Worker,
        /// <summary><c>Game.Citizens.Student.m_School</c> of a citizen.</summary>
        CitizenStudent,
        /// <summary><c>Game.Citizens.CurrentBuilding.m_CurrentBuilding</c> of a citizen.</summary>
        CurrentBuilding,
        /// <summary><c>Game.Citizens.HouseholdMember.m_Household</c> of a citizen.</summary>
        HouseholdMember,
        /// <summary><c>Game.Citizens.HouseholdPet.m_Household</c> of a household pet.</summary>
        HouseholdPet,
    }

    /// <summary>What a reference points at, as the client sees it.</summary>
    public enum LinkTarget : byte
    {
        /// <summary>An entity that exists and is not being deleted.</summary>
        Live,
        /// <summary><c>Entity.Null</c>.</summary>
        Null,
        /// <summary>An entity that no longer exists (destroyed; the slot may be reused by another version).</summary>
        Destroyed,
        /// <summary>An entity carrying <c>Deleted</c>: the game destroys it at the end of the frame.</summary>
        Deleted,
    }

    /// <summary>The vanilla-equivalent clean-up of a link whose target is gone.</summary>
    public enum LinkCleanup : byte
    {
        /// <summary>Nothing to do (the reference is valid, or Null is a normal value of the field).</summary>
        Keep,
        /// <summary>Remove the element from the reverse buffer.</summary>
        DropElement,
        /// <summary>Set the field to <c>Entity.Null</c> (the field is optional in the game).</summary>
        ClearReference,
        /// <summary>Remove the component from the holder (the game removes it when its target goes).</summary>
        RemoveComponent,
        /// <summary>
        /// Delete the holder itself, with its own clean-up: the game deletes a citizen or a household pet whose household is
        /// gone (<c>HouseholdAndCitizenRemoveSystem</c>, <c>HouseholdPetRemoveSystem</c>, <c>CitizenBehaviorSystem</c>). Removing
        /// the link instead left a citizen without <c>HouseholdMember</c> and a pet without <c>HouseholdPet</c>, its
        /// update-group marker, which the game never expects (ARCHITECTURE.md#reference-safety).
        /// </summary>
        DeleteHolder,
    }

    /// <summary>The rule of one <see cref="AgentLink"/>.</summary>
    public sealed class AgentLinkRule
    {
        internal AgentLinkRule(AgentLink link, string name, bool buffer, bool nullAllowed, LinkCleanup onGone, AgentLink reverse)
        {
            Link = link;
            Name = name;
            IsBuffer = buffer;
            NullAllowed = nullAllowed;
            OnGone = onGone;
            Reverse = reverse;
        }

        public AgentLink Link { get; }

        /// <summary>The audit's name of the link ("Companies.Employee.m_Worker").</summary>
        public string Name { get; }

        /// <summary>True for a reverse buffer (an element per member), false for a forward component.</summary>
        public bool IsBuffer { get; }

        /// <summary>True when the game keeps <c>Entity.Null</c> in the field as a normal value (an optional reference).</summary>
        public bool NullAllowed { get; }

        /// <summary>What the game does to the holder when the target is gone.</summary>
        public LinkCleanup OnGone { get; }

        /// <summary>The reverse buffer on the target that lists the holder (forward components only; equal to <see cref="Link"/> otherwise).</summary>
        public AgentLink Reverse { get; }

        /// <summary>The clean-up for a reference of this link that points at <paramref name="target"/>.</summary>
        public LinkCleanup Decide(LinkTarget target)
        {
            switch (target)
            {
                case LinkTarget.Live:
                    return LinkCleanup.Keep;
                case LinkTarget.Null:
                    if (IsBuffer) return LinkCleanup.DropElement; // a reverse list never names nobody in the game
                    return NullAllowed ? LinkCleanup.Keep : OnGone;
                default:
                    return OnGone;
            }
        }

        public override string ToString() => Name;
    }

    /// <summary>
    /// A read-only view of a world's agent links for <see cref="AgentLinkGraph.PlanDeletion{TEntity}"/>: the game layer
    /// implements it over the <c>EntityManager</c>, the tests over a small in-memory model.
    /// </summary>
    public interface IAgentLinkView<TEntity>
    {
        /// <summary>True when the entity exists and is not being deleted (its links may be changed).</summary>
        bool IsUsable(TEntity entity);

        /// <summary>The target of a forward link; false when the holder has no such component.</summary>
        bool TryGetForward(TEntity holder, AgentLink link, out TEntity target);

        /// <summary>Adds the members of a reverse buffer to <paramref name="members"/>; false when the owner has no such buffer.</summary>
        bool GetMembers(TEntity owner, AgentLink link, List<TEntity> members);
    }

    /// <summary>One change of a cascade plan.</summary>
    public struct AgentLinkOp<TEntity>
    {
        /// <summary><see cref="LinkCleanup.DropElement"/>, <see cref="LinkCleanup.ClearReference"/> or <see cref="LinkCleanup.RemoveComponent"/>.</summary>
        public LinkCleanup Action;
        public TEntity Holder;
        public AgentLink Link;
        /// <summary>The element to drop (<see cref="LinkCleanup.DropElement"/>) or the target being cleared.</summary>
        public TEntity Target;

        public override string ToString() => Action + " " + Link + " of " + Holder + " -> " + Target;
    }

    /// <summary>
    /// The agent link graph (ARCHITECTURE.md#reference-safety): which buffers and components reference households,
    /// citizens, pets, companies and their buildings, the pairs of forward and reverse links, and what the game does to
    /// each when the referenced entity is deleted. Clients switch the game's removal systems off (they also delete and
    /// create entities, which is host business), so the client applies the reference part itself: when it despawns an
    /// entity (<see cref="PlanDeletion{TEntity}"/>) and whenever it finds a dangling reference (<see cref="AgentLinkRule.Decide"/>).
    /// </summary>
    public static class AgentLinkGraph
    {
        private static readonly AgentLinkRule[] RuleTable = Build();

        /// <summary>Every link, indexed by <see cref="AgentLink"/>.</summary>
        public static IReadOnlyList<AgentLinkRule> Rules => RuleTable;

        public static AgentLinkRule Rule(AgentLink link) => RuleTable[(int)link];

        public static int Count => RuleTable.Length;

        /// <summary>The forward links (member side), in rule order.</summary>
        public static readonly AgentLink[] Forward =
        {
            AgentLink.PropertyRenter, AgentLink.HomelessHome, AgentLink.TouristHotel, AgentLink.Worker, AgentLink.CitizenStudent,
            AgentLink.CurrentBuilding, AgentLink.HouseholdMember, AgentLink.HouseholdPet,
        };

        /// <summary>The reverse buffers (owner side), in rule order.</summary>
        public static readonly AgentLink[] Reverse =
        {
            AgentLink.Renter, AgentLink.Employee, AgentLink.SchoolStudent, AgentLink.Patient, AgentLink.Occupant, AgentLink.HouseholdCitizen,
            AgentLink.HouseholdAnimal,
        };

        private static AgentLinkRule[] Build()
        {
            var rules = new AgentLinkRule[15];
            void Buffer(AgentLink link, string name) => rules[(int)link] = new AgentLinkRule(link, name, true, false, LinkCleanup.DropElement, link);
            void Data(AgentLink link, string name, bool nullAllowed, LinkCleanup onGone, AgentLink reverse) => rules[(int)link] = new AgentLinkRule(link, name, false, nullAllowed, onGone, reverse);
            Buffer(AgentLink.Renter, "Buildings.Renter.m_Renter");
            Buffer(AgentLink.Employee, "Companies.Employee.m_Worker");
            Buffer(AgentLink.SchoolStudent, "Buildings.Student.m_Student");
            Buffer(AgentLink.Patient, "Buildings.Patient.m_Patient");
            Buffer(AgentLink.Occupant, "Buildings.Occupant.m_Occupant");
            Buffer(AgentLink.HouseholdCitizen, "Citizens.HouseholdCitizen.m_Citizen");
            Buffer(AgentLink.HouseholdAnimal, "Citizens.HouseholdAnimal.m_HouseholdPet");
            // RemovedSystem: a deleted property takes the renter's PropertyRenter; a deleted hotel only clears the guest's
            // hotel; a deleted workplace takes Worker, a deleted school Student (each with its travel purpose). A null
            // property (a company before it found one), temporary home, hotel and current building are normal values the
            // game checks for; a null workplace or school is not. A citizen or pet whose household is gone is deleted with
            // it (the host does the same and sends the despawns); a null household is a passing state the game repairs
            // itself (CitizenBehaviorSystem gives such a citizen a new household), so it is kept until the host's record.
            Data(AgentLink.PropertyRenter, "Buildings.PropertyRenter.m_Property", true, LinkCleanup.RemoveComponent, AgentLink.Renter);
            Data(AgentLink.HomelessHome, "Citizens.HomelessHousehold.m_TempHome", true, LinkCleanup.ClearReference, AgentLink.Renter);
            Data(AgentLink.TouristHotel, "Citizens.TouristHousehold.m_Hotel", true, LinkCleanup.ClearReference, AgentLink.Renter);
            Data(AgentLink.Worker, "Citizens.Worker.m_Workplace", false, LinkCleanup.RemoveComponent, AgentLink.Employee);
            Data(AgentLink.CitizenStudent, "Citizens.Student.m_School", false, LinkCleanup.RemoveComponent, AgentLink.SchoolStudent);
            Data(AgentLink.CurrentBuilding, "Citizens.CurrentBuilding.m_CurrentBuilding", true, LinkCleanup.RemoveComponent, AgentLink.Occupant);
            Data(AgentLink.HouseholdMember, "Citizens.HouseholdMember.m_Household", true, LinkCleanup.DeleteHolder, AgentLink.HouseholdCitizen);
            Data(AgentLink.HouseholdPet, "Citizens.HouseholdPet.m_Household", true, LinkCleanup.DeleteHolder, AgentLink.HouseholdAnimal);
            return rules;
        }

        /// <summary>
        /// The reverse buffers a forward link's target may list the holder in. <c>CurrentBuilding</c> is listed by a
        /// hospital (<c>Patient</c>) or a prison or shelter (<c>Occupant</c>); every other forward link has one.
        /// </summary>
        public static int ReverseOf(AgentLink forward, AgentLink[] output)
        {
            if (output == null || output.Length < 2) throw new ArgumentException("needs room for two links", nameof(output));
            if (forward == AgentLink.CurrentBuilding)
            {
                output[0] = AgentLink.Patient;
                output[1] = AgentLink.Occupant;
                return 2;
            }
            AgentLinkRule rule = Rule(forward);
            if (rule.IsBuffer) return 0;
            output[0] = rule.Reverse;
            return 1;
        }

        /// <summary>
        /// The forward links whose holders a reverse buffer lists (the member side of <paramref name="reverse"/>):
        /// a <c>Renter</c> list names renters (<c>PropertyRenter</c>), homeless households (<c>HomelessHousehold</c>) and,
        /// on a hotel, its guests (<c>TouristHousehold</c>).
        /// </summary>
        public static int ForwardOf(AgentLink reverse, AgentLink[] output)
        {
            if (output == null || output.Length < 3) throw new ArgumentException("needs room for three links", nameof(output));
            int n = 0;
            foreach (AgentLink f in Forward)
            {
                if (f == AgentLink.CurrentBuilding ? reverse == AgentLink.Patient || reverse == AgentLink.Occupant : Rule(f).Reverse == reverse) output[n++] = f;
            }
            return n;
        }

        private sealed class Scratch<TEntity>
        {
            [ThreadStatic] public static Scratch<TEntity> Instance;
            public readonly List<TEntity> Members = new List<TEntity>();
            public readonly AgentLink[] Links = new AgentLink[3];
            public readonly AgentLink[] Reverses = new AgentLink[2];
            public readonly HashSet<TEntity> Seen = new HashSet<TEntity>();
            public readonly HashSet<(TEntity, AgentLink)> Planned = new HashSet<(TEntity, AgentLink)>();
            public readonly HashSet<TEntity> Deleting = new HashSet<TEntity>();
            public readonly List<AgentLinkOp<TEntity>> Ops = new List<AgentLinkOp<TEntity>>();
        }

        private static Scratch<TEntity> ScratchOf<TEntity>() => Scratch<TEntity>.Instance ?? (Scratch<TEntity>.Instance = new Scratch<TEntity>());

        /// <summary>
        /// The reference clean-up for <paramref name="deleted"/>, which is about to be deleted (it is still readable),
        /// added to <paramref name="ops"/> in the order to apply them. Three sides, as the game's removal systems do it:
        /// <list type="bullet">
        /// <item>as an owner: every member its reverse buffers list whose forward link points at it (or at nothing that
        /// still exists) loses that link (<see cref="AgentLinkRule.OnGone"/>: a renter loses <c>PropertyRenter</c>, a guest
        /// its hotel, a worker <c>Worker</c>, a pupil <c>Student</c>; a household member is deleted, <see cref="LinkCleanup.DeleteHolder"/>);</item>
        /// <item>as a member: every owner its forward links name drops it from the matching reverse buffer (a household
        /// leaves its building's and its hotel's renters, a citizen its household, workplace, school, hospital);</item>
        /// <item>every entity of <paramref name="referrers"/> (the entities that may hold a link naming it, a superset:
        /// <see cref="AgentReferrerIndex{TEntity}"/>) loses each of its links that really names it. This covers what the two
        /// sides above do not reach (a citizen inside a demolished building that no list names, a citizen still listed by its
        /// old household, a worker whose company's list does not name it), which on a client is out of step for a while.</item>
        /// </list>
        /// Never deletes or creates an entity itself (a <see cref="LinkCleanup.DeleteHolder"/> op asks the caller to; see
        /// <see cref="PlanDeletion"/>). No change of the same link of the same holder is planned twice; the cost is linear in
        /// the lists, links and referrers read.
        /// </summary>
        public static void PlanCascade<TEntity>(IAgentLinkView<TEntity> view, TEntity deleted, List<AgentLinkOp<TEntity>> ops, IReadOnlyList<TEntity> referrers = null)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (ops == null) throw new ArgumentNullException(nameof(ops));
            Scratch<TEntity> s = ScratchOf<TEntity>();
            EqualityComparer<TEntity> eq = EqualityComparer<TEntity>.Default;
            s.Planned.Clear();
            try
            {
                // as an owner: its members' forward links
                foreach (AgentLink reverse in Reverse)
                {
                    s.Members.Clear();
                    if (!view.GetMembers(deleted, reverse, s.Members)) continue;
                    int forwards = ForwardOf(reverse, s.Links);
                    for (int m = 0; m < s.Members.Count; m++)
                    {
                        TEntity member = s.Members[m];
                        if (eq.Equals(member, deleted) || !view.IsUsable(member)) continue;
                        for (int f = 0; f < forwards; f++)
                        {
                            AgentLink forward = s.Links[f];
                            if (!view.TryGetForward(member, forward, out TEntity at)) continue;
                            // a member the owner lists loses its link to it, as in the game; one that names another live owner
                            // (a list out of date here) keeps it, one that names nothing usable (null, gone) loses it too
                            if (!eq.Equals(at, deleted) && view.IsUsable(at)) continue;
                            if (!s.Planned.Add((member, forward))) continue;
                            ops.Add(new AgentLinkOp<TEntity> { Action = Rule(forward).OnGone, Holder = member, Link = forward, Target = at });
                        }
                    }
                }
                // as a member: out of its owners' reverse buffers
                AgentLink[] reverses = s.Reverses;
                foreach (AgentLink forward in Forward)
                {
                    if (!view.TryGetForward(deleted, forward, out TEntity owner)) continue;
                    if (eq.Equals(owner, default) || eq.Equals(owner, deleted) || !view.IsUsable(owner)) continue;
                    int n = ReverseOf(forward, reverses);
                    for (int r = 0; r < n; r++)
                    {
                        s.Members.Clear();
                        if (!view.GetMembers(owner, reverses[r], s.Members) || !s.Members.Contains(deleted)) continue;
                        if (!s.Planned.Add((owner, reverses[r]))) continue;
                        ops.Add(new AgentLinkOp<TEntity> { Action = LinkCleanup.DropElement, Holder = owner, Link = reverses[r], Target = deleted });
                    }
                }
                if (referrers == null || referrers.Count == 0) return;
                // every other holder of a link naming it
                s.Seen.Clear();
                for (int i = 0; i < referrers.Count; i++)
                {
                    TEntity holder = referrers[i];
                    if (eq.Equals(holder, default) || eq.Equals(holder, deleted) || !s.Seen.Add(holder) || !view.IsUsable(holder)) continue;
                    foreach (AgentLink forward in Forward)
                    {
                        if (!view.TryGetForward(holder, forward, out TEntity at) || !eq.Equals(at, deleted) || !s.Planned.Add((holder, forward))) continue;
                        ops.Add(new AgentLinkOp<TEntity> { Action = Rule(forward).OnGone, Holder = holder, Link = forward, Target = deleted });
                    }
                    foreach (AgentLink reverse in Reverse)
                    {
                        s.Members.Clear();
                        if (!view.GetMembers(holder, reverse, s.Members) || !s.Members.Contains(deleted) || !s.Planned.Add((holder, reverse))) continue;
                        ops.Add(new AgentLinkOp<TEntity> { Action = LinkCleanup.DropElement, Holder = holder, Link = reverse, Target = deleted });
                    }
                }
            }
            finally
            {
                s.Seen.Clear();
                s.Members.Clear();
                s.Planned.Clear();
            }
        }

        /// <summary>
        /// The whole clean-up of deleting <paramref name="root"/>: the entities deleted with it, <paramref name="deletions"/>
        /// (the root first, then every holder a <see cref="LinkCleanup.DeleteHolder"/> rule takes along: the citizens and pets
        /// of a removed household, as the game deletes them, and theirs in turn), and the link changes, <paramref name="ops"/>,
        /// of all of them (<see cref="PlanCascade"/>, with <paramref name="referrersOf"/> giving each one's referrers; may be
        /// null). Ops on an entity that is deleted anyway are left out, and no <see cref="LinkCleanup.DeleteHolder"/> op is
        /// returned. The caller applies the ops, then tags every deletion (the root included) as deleted. Both lists are
        /// cleared first.
        /// </summary>
        public static void PlanDeletion<TEntity>(IAgentLinkView<TEntity> view, TEntity root, Func<TEntity, IReadOnlyList<TEntity>> referrersOf, List<AgentLinkOp<TEntity>> ops, List<TEntity> deletions)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (ops == null) throw new ArgumentNullException(nameof(ops));
            if (deletions == null) throw new ArgumentNullException(nameof(deletions));
            ops.Clear();
            deletions.Clear();
            Scratch<TEntity> s = ScratchOf<TEntity>();
            List<AgentLinkOp<TEntity>> all = s.Ops;
            HashSet<TEntity> deleting = s.Deleting;
            all.Clear();
            deleting.Clear();
            try
            {
                deletions.Add(root);
                deleting.Add(root);
                for (int d = 0; d < deletions.Count; d++)
                {
                    int from = all.Count;
                    PlanCascade(view, deletions[d], all, referrersOf?.Invoke(deletions[d]));
                    for (int i = from; i < all.Count; i++)
                    {
                        AgentLinkOp<TEntity> op = all[i];
                        if (op.Action == LinkCleanup.DeleteHolder && view.IsUsable(op.Holder) && deleting.Add(op.Holder)) deletions.Add(op.Holder);
                    }
                }
                foreach (AgentLinkOp<TEntity> op in all)
                {
                    if (op.Action == LinkCleanup.DeleteHolder || op.Action == LinkCleanup.Keep || deleting.Contains(op.Holder)) continue;
                    ops.Add(op);
                }
            }
            finally
            {
                all.Clear();
                deleting.Clear();
            }
        }

        /// <summary>
        /// Notes in <paramref name="index"/> every agent link <paramref name="holder"/> holds: each target of its forward links
        /// and each member of its reverse buffers gets <paramref name="holder"/> as a referrer. Called for every holder whose
        /// links were written (the client's apply, an agent value message) and by the sweep, so the index stays a superset of
        /// the real links. <paramref name="alive"/> prunes referrers that are gone (optional). Returns the entries added.
        /// </summary>
        public static int IndexHolder<TEntity>(IAgentLinkView<TEntity> view, TEntity holder, AgentReferrerIndex<TEntity> index, Func<TEntity, bool> alive = null)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (index == null) throw new ArgumentNullException(nameof(index));
            Scratch<TEntity> s = ScratchOf<TEntity>();
            int added = 0;
            foreach (AgentLink forward in Forward)
            {
                if (view.TryGetForward(holder, forward, out TEntity target) && index.Add(target, holder, alive)) added++;
            }
            foreach (AgentLink reverse in Reverse)
            {
                s.Members.Clear();
                if (!view.GetMembers(holder, reverse, s.Members)) continue;
                for (int m = 0; m < s.Members.Count; m++)
                {
                    if (index.Add(s.Members[m], holder, alive)) added++;
                }
            }
            s.Members.Clear();
            return added;
        }
    }
}
