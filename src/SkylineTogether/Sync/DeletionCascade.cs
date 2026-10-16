using System;
using System.Collections.Generic;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Creatures;
using Game.Vehicles;
using SkylineTogether.Core.Sync;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// The reference clean-up the game does when an entity is deleted, done on a client at the moment the host's
    /// despawn is applied, and for every entity deleted locally (<see cref="RemovalCascadeSystem"/>: the player's own
    /// bulldozing included), ARCHITECTURE.md#reference-safety.
    ///
    /// On the host, deleting a building runs <c>Game.Simulation.RemovedSystem</c> (Modification5): every renter loses its
    /// <c>PropertyRenter</c>, tourists of a hotel lose <c>TouristHousehold.m_Hotel</c>, every employee loses <c>Worker</c>
    /// and a work <c>TravelPurpose</c>. <c>SchoolUpdatedSystem</c> removes <c>Student</c> from the pupils of a deleted school;
    /// <c>HouseholdAndCitizenRemoveSystem</c> and <c>HouseholdPetRemoveSystem</c> delete the citizens and pets of a deleted
    /// household (and a citizen's creature) and take a deleted household, citizen or pet out of the reverse lists
    /// (<c>Renter</c>, <c>HouseholdCitizen</c>, <c>HouseholdAnimal</c>, school <c>Student</c>, <c>Patient</c>, <c>Occupant</c>).
    /// Other lists the game only prunes later in its simulation: a dead worker stays in its company's <c>Employee</c> list
    /// until <c>WorkProviderSystem</c> refreshes it, a departed tourist in its hotel's <c>Renter</c> list until
    /// <c>LodgingProviderSystem</c> does, and the guests of a deleted hotel company keep their hotel until
    /// <c>HouseholdBehaviorSystem</c> clears it. Clients switch all of these off (<see cref="ClientGate"/>; the follower clock
    /// stops the simulation): they also delete and create entities and raise triggers, which is host business, and the host
    /// sends the result. But the result arrives later than the despawn (agents travel in a budgeted value stream; owner
    /// lists after the batch), and the lazy prunes never come, so a client's buildings, companies and households kept
    /// naming entities that no longer exist. Game code reads such references unchecked (Burst jobs index lookups with
    /// them: a native crash once the entity is gone).
    ///
    /// This class does all of it at once, with the same end state (<see cref="AgentLinkGraph.PlanDeletion{TEntity}"/>): list
    /// entries naming the deleted entity are removed and the members' links to it cleared or removed; the citizens and pets
    /// that belonged to a deleted household are deleted with it, as the game does (removing their link instead left a pet
    /// without its update-group marker); movers whose owner, citizen or pet is deleted go too (vanilla
    /// <c>SubElementDeleteSystem</c>, <c>HouseholdAndCitizenRemoveSystem</c>). Every holder is found through the referrer
    /// index (<see cref="Index"/>) as well as through the lists, so a link the lists do not lead to (a citizen inside a
    /// demolished building, a member a household's list does not name yet) is cleaned in the same frame. It never creates an
    /// entity and raises no trigger or icon. Entities it deletes besides the one it was called for are handed to
    /// <see cref="CascadeDeleted"/> (the client unbinds their ids and asks the host for them; the host has usually deleted
    /// them as well and answers with their despawn).
    /// </summary>
    internal static class DeletionCascade
    {
        /// <summary>Clean-ups made this session (diagnostics: <c>SyncController.Describe</c>).</summary>
        public static long Removed;

        /// <summary>Entities cascaded by locally deleted entities this session (<see cref="RemovalCascadeSystem"/>).</summary>
        public static long LocalDeletions;

        /// <summary>Citizens and pets deleted with their household this session.</summary>
        public static long HoldersDeleted;

        /// <summary>Movers deleted with their owner, citizen or pet this session.</summary>
        public static long MoversDeleted;

        /// <summary>
        /// Client: the referrers of every entity (agent links both ways, movers' owner, citizen and pet); set by the
        /// reference audit for the session, null on the host and before the client goes live.
        /// </summary>
        public static AgentReferrerIndex<Entity> Index;

        /// <summary>An entity the cascade deleted besides the one it was called for (set by the controller; may be null).</summary>
        public static Action<Entity> CascadeDeleted;

        /// <summary>
        /// Deletes a mover the way the mover channel removes one (riders released, trailers along, id unbound); set by the
        /// mover client. Without it a mover is tagged <c>Deleted</c> only.
        /// </summary>
        public static Func<EntityManager, Entity, bool> DeleteMover;

        private static readonly List<AgentLinkOp<Entity>> Ops = new List<AgentLinkOp<Entity>>();
        private static readonly List<Entity> Deletions = new List<Entity>();
        private static readonly List<Entity> Members = new List<Entity>();
        private static readonly List<Entity> Referrers = new List<Entity>();
        private static readonly List<Entity> MoverScratch = new List<Entity>();
        private static readonly Func<Entity, IReadOnlyList<Entity>> ReferrersOf = e =>
        {
            Referrers.Clear();
            Index?.Get(e, Referrers);
            return Referrers;
        };
        private static AgentLinkAccess _access;
        private static int _depth;

        /// <summary>
        /// Entities already cascaded this frame (by the applier), so the frame's local-deletion pass skips them; cleared by
        /// <see cref="RemovalCascadeSystem"/>. Main thread only.
        /// </summary>
        public static readonly HashSet<Entity> DoneThisFrame = new HashSet<Entity>();

        /// <summary>
        /// Applies the client-safe clean-up for <paramref name="deleted"/>, which is about to receive (or just received)
        /// <c>Deleted</c> (the caller tags it). Returns the number of components, list entries and entities changed; the
        /// entities changed are added to <paramref name="touched"/> (the applier's set: the client never proposes them back;
        /// may be null).
        /// </summary>
        public static int Apply(EntityManager em, Entity deleted, HashSet<Entity> touched)
        {
            if (deleted == Entity.Null || !em.Exists(deleted)) return 0;
            if (_depth > 0) return 0; // a mover deletion inside the cascade never starts another one
            AgentLinkAccess access = _access ?? (_access = new AgentLinkAccess(em));
            access.EntityManager = em;
            access.Touched = touched;
            _depth++;
            try
            {
                DoneThisFrame.Add(deleted);
                int n = 0;
                AgentLinkGraph.PlanDeletion(access, deleted, ReferrersOf, Ops, Deletions);
                // a hotel company renting in the deleted building: its guests have no hotel any more (the host deletes the
                // hotel company itself; RemovedSystem clears the guests in the same pass)
                for (int d = 0; d < Deletions.Count; d++)
                {
                    if (em.HasBuffer<Renter>(Deletions[d])) n += ReleaseHotelGuests(em, access, Deletions[d]);
                }
                for (int i = 0; i < Ops.Count; i++) n += access.Apply(Ops[i]);
                // movers whose owner, citizen or pet is deleted (theirs are found the same way)
                for (int d = 0; d < Deletions.Count; d++) n += CleanMovers(em, Deletions[d], touched);
                // the citizens and pets deleted with their household
                for (int d = 1; d < Deletions.Count; d++)
                {
                    Entity e = Deletions[d];
                    if (!em.Exists(e) || em.HasComponent<Deleted>(e)) continue;
                    DoneThisFrame.Add(e);
                    em.AddComponent<Deleted>(e);
                    touched?.Add(e);
                    HoldersDeleted++;
                    n++;
                    CascadeDeleted?.Invoke(e);
                }
                if (Index != null)
                {
                    for (int d = 0; d < Deletions.Count; d++) Index.RemoveTarget(Deletions[d]);
                }
                Removed += n;
                return n;
            }
            finally
            {
                _depth--;
                access.Touched = null;
                Ops.Clear();
                Deletions.Clear();
                Referrers.Clear();
            }
        }

        /// <summary>
        /// Deletes <paramref name="holder"/> with its clean-up (the audit found it naming a household that is gone: the game
        /// deletes such a citizen or pet); hands it to <see cref="CascadeDeleted"/>. Returns the number of changes.
        /// </summary>
        public static int DeleteWithCascade(EntityManager em, Entity holder, HashSet<Entity> touched)
        {
            if (holder == Entity.Null || !em.Exists(holder) || em.HasComponent<Deleted>(holder)) return 0;
            int n = Apply(em, holder, touched);
            if (!em.Exists(holder) || em.HasComponent<Deleted>(holder)) return n;
            em.AddComponent<Deleted>(holder);
            touched?.Add(holder);
            HoldersDeleted++;
            CascadeDeleted?.Invoke(holder);
            return n + 1;
        }

        /// <summary>
        /// The movers that name <paramref name="deleted"/> (the referrer index): a vehicle it owns, the creature of a citizen or
        /// pet are deleted (vanilla <c>SubElementDeleteSystem</c>, <c>HouseholdAndCitizenRemoveSystem</c>); a personal car it
        /// keeps loses its keeper.
        /// </summary>
        private static int CleanMovers(EntityManager em, Entity deleted, HashSet<Entity> touched)
        {
            if (Index == null) return 0;
            MoverScratch.Clear();
            Index.Get(deleted, MoverScratch);
            int n = 0;
            foreach (Entity m in MoverScratch)
            {
                if (m == deleted || !em.Exists(m) || em.HasComponent<Deleted>(m)) continue;
                bool vehicle = em.HasComponent<Vehicle>(m);
                if (!vehicle && !em.HasComponent<Creature>(m)) continue;
                bool gone = (vehicle && em.HasComponent<Owner>(m) && em.GetComponentData<Owner>(m).m_Owner == deleted)
                            || (em.HasComponent<Resident>(m) && em.GetComponentData<Resident>(m).m_Citizen == deleted)
                            || (em.HasComponent<Pet>(m) && em.GetComponentData<Pet>(m).m_HouseholdPet == deleted);
                if (gone)
                {
                    bool done = DeleteMover != null && DeleteMover(em, m);
                    if (!done && em.Exists(m) && !em.HasComponent<Deleted>(m)) em.AddComponent<Deleted>(m);
                    DoneThisFrame.Add(m);
                    touched?.Add(m);
                    MoversDeleted++;
                    n++;
                    continue;
                }
                if (em.HasComponent<PersonalCar>(m))
                {
                    PersonalCar car = em.GetComponentData<PersonalCar>(m);
                    if (car.m_Keeper != deleted) continue;
                    car.m_Keeper = Entity.Null;
                    em.SetComponentData(m, car);
                    touched?.Add(m);
                    n++;
                }
            }
            MoverScratch.Clear();
            return n;
        }

        /// <summary>
        /// Notes in <see cref="Index"/> what a mover names: a vehicle's owner and keeper, a creature's citizen or pet (the
        /// mover client calls it for every mover it creates or relinks; the audit for the snapshot's movers). Returns the
        /// entries added.
        /// </summary>
        public static int IndexMover(EntityManager em, Entity mover)
        {
            AgentReferrerIndex<Entity> index = Index;
            if (index == null || mover == Entity.Null || !em.Exists(mover)) return 0;
            int n = 0;
            if (em.HasComponent<Vehicle>(mover))
            {
                if (em.HasComponent<Owner>(mover) && index.Add(em.GetComponentData<Owner>(mover).m_Owner, mover)) n++;
                if (em.HasComponent<PersonalCar>(mover) && index.Add(em.GetComponentData<PersonalCar>(mover).m_Keeper, mover)) n++;
            }
            if (em.HasComponent<Resident>(mover) && index.Add(em.GetComponentData<Resident>(mover).m_Citizen, mover)) n++;
            if (em.HasComponent<Pet>(mover) && index.Add(em.GetComponentData<Pet>(mover).m_HouseholdPet, mover)) n++;
            return n;
        }

        private static int ReleaseHotelGuests(EntityManager em, AgentLinkAccess access, Entity property)
        {
            int n = 0;
            Members.Clear();
            access.GetMembers(property, AgentLink.Renter, Members);
            Entity[] renters = Members.ToArray(); // the guest lists below reuse the scratch list
            foreach (Entity renter in renters)
            {
                if (!access.IsUsable(renter) || !em.HasComponent<LodgingProvider>(renter) || !em.HasBuffer<Renter>(renter)) continue;
                Members.Clear();
                access.GetMembers(renter, AgentLink.Renter, Members);
                foreach (Entity guest in Members)
                {
                    if (!access.IsUsable(guest) || !access.TryGetForward(guest, AgentLink.TouristHotel, out Entity hotel) || hotel != renter) continue;
                    n += access.CleanForward(guest, AgentLink.TouristHotel, LinkCleanup.ClearReference);
                }
            }
            Members.Clear();
            return n;
        }

        public static void ResetSession()
        {
            Removed = 0;
            LocalDeletions = 0;
            HoldersDeleted = 0;
            MoversDeleted = 0;
            DoneThisFrame.Clear();
            Index = null;
            _access = null;
            _depth = 0;
        }
    }
}
