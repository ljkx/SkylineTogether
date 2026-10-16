using System.Collections.Generic;
using Game.Buildings;
using Game.Companies;
using SkylineTogether.Core.Sync;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// The reference clean-up the game does when an entity is deleted, done on a client at the moment the host's
    /// despawn is applied, and for every entity deleted locally (<see cref="RemovalCascadeSystem"/>), ARCHITECTURE.md#reference-safety.
    ///
    /// On the host, deleting a building runs <c>Game.Simulation.RemovedSystem</c> (Modification5): every renter loses its
    /// <c>PropertyRenter</c>, tourists of a hotel lose <c>TouristHousehold.m_Hotel</c>, every employee loses <c>Worker</c>
    /// and a work <c>TravelPurpose</c>. <c>SchoolUpdatedSystem</c> removes <c>Student</c> from the pupils of a deleted school;
    /// <c>HouseholdAndCitizenRemoveSystem</c> and <c>HouseholdPetRemoveSystem</c> take a deleted household, citizen or pet
    /// out of the reverse lists (<c>Renter</c>, <c>HouseholdCitizen</c>, <c>HouseholdAnimal</c>, school <c>Student</c>,
    /// <c>Patient</c>, <c>Occupant</c>). Other lists the game only prunes later in its simulation: a dead worker stays in
    /// its company's <c>Employee</c> list until <c>WorkProviderSystem</c> refreshes it, a departed tourist in its hotel's
    /// <c>Renter</c> list until <c>LodgingProviderSystem</c> does, and the guests of a deleted hotel company keep their hotel
    /// until <c>HouseholdBehaviorSystem</c> clears it. Clients switch all of these off (<see cref="ClientGate"/>; the
    /// follower clock stops the simulation): they also delete and create entities and raise triggers, which is host
    /// business, and the host sends the result. But the result arrives later than the despawn (agents travel in a budgeted
    /// value stream; owner lists after the batch), and the lazy prunes never come, so a client's buildings, companies and
    /// households kept naming entities that no longer exist. Game code reads such references unchecked (the extractor
    /// vehicle panel reads its building's first renter, the citizen panel the citizen's household; Burst jobs index
    /// lookups with them: a native crash once the entity is gone).
    ///
    /// This class does the reference part of all of them at once, with the same end state (<see cref="AgentLinkGraph"/>):
    /// it removes list entries naming the deleted entity, clears or removes the members' links to it, never deletes or
    /// creates an entity, never raises a trigger or an icon. The host's later records carry the same values, so the
    /// replica converges either way; integrity hashes agree as soon as the host's own clean-up has been captured.
    /// </summary>
    internal static class DeletionCascade
    {
        /// <summary>Clean-ups made this session (diagnostics: <c>SyncController.Describe</c>).</summary>
        public static long Removed;

        /// <summary>Entities cascaded by locally deleted entities this session (<see cref="RemovalCascadeSystem"/>).</summary>
        public static long LocalDeletions;

        private static readonly List<AgentLinkOp<Entity>> Ops = new List<AgentLinkOp<Entity>>();
        private static readonly List<Entity> Members = new List<Entity>();
        private static AgentLinkAccess _access;

        /// <summary>
        /// Entities already cascaded this frame (by the applier), so the frame's local-deletion pass skips them; cleared by
        /// <see cref="RemovalCascadeSystem"/>. Main thread only.
        /// </summary>
        public static readonly HashSet<Entity> DoneThisFrame = new HashSet<Entity>();

        /// <summary>
        /// Applies the client-safe clean-up for <paramref name="deleted"/>, which is about to receive (or just received)
        /// <c>Deleted</c>. Returns the number of components and list entries removed; the entities changed are added to
        /// <paramref name="touched"/> (the applier's set: the client never proposes them back; may be null).
        /// </summary>
        public static int Apply(EntityManager em, Entity deleted, HashSet<Entity> touched)
        {
            if (deleted == Entity.Null || !em.Exists(deleted)) return 0;
            AgentLinkAccess access = _access ?? (_access = new AgentLinkAccess(em));
            access.EntityManager = em;
            access.Touched = touched;
            try
            {
                DoneThisFrame.Add(deleted);
                int n = 0;
                Ops.Clear();
                AgentLinkGraph.PlanCascade(access, deleted, Ops);
                // a hotel company renting in the deleted building: its guests have no hotel any more (the host deletes the
                // hotel company itself; RemovedSystem clears the guests in the same pass)
                if (em.HasBuffer<Renter>(deleted)) n += ReleaseHotelGuests(em, access, deleted);
                for (int i = 0; i < Ops.Count; i++) n += access.Apply(Ops[i]);
                Removed += n;
                return n;
            }
            finally
            {
                access.Touched = null;
                Ops.Clear();
            }
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
            DoneThisFrame.Clear();
            _access = null;
        }
    }
}
