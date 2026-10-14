using System;
using Game.Buildings;
using Game.Citizens;
using Game.Companies;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// The reference clean-up the game does when an entity is deleted, done on a client
    /// at the moment the host's despawn is applied.
    ///
    /// On the host, deleting a building runs <c>Game.Simulation.RemovedSystem</c> (Modification5): every renter loses its
    /// <c>PropertyRenter</c>, tourists of a hotel lose <c>TouristHousehold.m_Hotel</c>, every employee loses
    /// <c>Worker</c> and a work <c>TravelPurpose</c>. <c>SchoolUpdatedSystem</c> removes <c>Student</c> from the pupils of a
    /// deleted school, <c>HouseholdAndCitizenRemoveSystem</c> and <c>HouseholdPetRemoveSystem</c> take a deleted
    /// household, citizen or pet out of the reverse lists (<c>Renter</c>, <c>HouseholdCitizen</c>, <c>HouseholdAnimal</c>,
    /// school <c>Student</c>, <c>Patient</c>, <c>Occupant</c>). Clients gate these systems (<see cref="ClientGate"/>):
    /// they also delete and create entities and raise triggers, which is host business, and the host sends the result.
    /// But the result arrives later than the despawn (agents travel in a budgeted value stream), and until then the
    /// client's households, companies and workers referenced a building that no longer exists. Game code reads such
    /// references unchecked (for example the workplaces info view indexes <c>m_PrefabRefFromEntity[propertyRenter.m_Property]</c>
    /// in a Burst job: a native crash once the entity is gone).
    ///
    /// This class does only the reference part, with the same rules: it removes components and list entries that point
    /// at the deleted entity, never deletes or creates an entity, never raises a trigger or an icon. The host's later
    /// records carry the same values, so the replica converges either way; integrity hashes agree as soon as the host's
    /// own cascade (the same removals) has been captured.
    /// </summary>
    internal static class DeletionCascade
    {
        /// <summary>Clean-ups made this session (diagnostics: <c>SyncController.Describe</c>).</summary>
        public static long Removed;

        /// <summary>
        /// Applies the client-safe clean-up for <paramref name="deleted"/>, which is about to receive <c>Deleted</c>.
        /// Returns the number of components and list entries removed; the entities changed are added to
        /// <paramref name="touched"/> (the applier's set: the client never proposes them back).
        /// </summary>
        public static int Apply(EntityManager em, Entity deleted, System.Collections.Generic.HashSet<Entity> touched)
        {
            _touched = touched; // main thread only (the applier)
            try
            {
                int n = 0;
                // a building (or any property): its renters, its hotel guests, its employees, its pupils
                if (em.HasBuffer<Renter>(deleted)) n += ReleaseRenters(em, deleted);
                if (em.HasBuffer<Employee>(deleted)) n += ReleaseEmployees(em, deleted);
                if (em.HasBuffer<Game.Buildings.Student>(deleted) && em.HasComponent<School>(deleted)) n += ReleaseStudents(em, deleted);
                // a renter (household or company): out of its property's renter list
                n += LeaveProperty(em, deleted);
                // a citizen: out of its household, school, workplace and the building it is in
                if (em.HasComponent<Citizen>(deleted)) n += LeaveCitizenLists(em, deleted);
                // a pet: out of its household's animal list
                if (em.HasComponent<HouseholdPet>(deleted))
                {
                    Entity household = em.GetComponentData<HouseholdPet>(deleted).m_Household;
                    if (Usable(em, household) && em.HasBuffer<HouseholdAnimal>(household))
                    {
                        n += RemoveAll(household, em.GetBuffer<HouseholdAnimal>(household), deleted, x => x.m_HouseholdPet);
                    }
                }
                Removed += n;
                return n;
            }
            finally
            {
                _touched = null;
            }
        }

        private static System.Collections.Generic.HashSet<Entity> _touched;

        private static void Touch(Entity e)
        {
            _touched?.Add(e);
        }

        private static int ReleaseRenters(EntityManager em, Entity property)
        {
            int n = 0;
            // copy first: removing components moves entities between chunks and invalidates buffer handles
            Entity[] renters = Members(em.GetBuffer<Renter>(property, true), x => x.m_Renter);
            foreach (Entity renter in renters)
            {
                if (!Usable(em, renter)) continue;
                if (em.HasComponent<PropertyRenter>(renter))
                {
                    Entity at = em.GetComponentData<PropertyRenter>(renter).m_Property;
                    if (at == property || at == Entity.Null || !em.Exists(at))
                    {
                        em.RemoveComponent<PropertyRenter>(renter);
                        Touch(renter);
                        n++;
                    }
                }
                // a hotel in the building: its guests have no hotel any more (the host deletes the hotel company itself)
                if (em.HasComponent<LodgingProvider>(renter) && em.HasBuffer<Renter>(renter))
                {
                    Entity[] guests = Members(em.GetBuffer<Renter>(renter, true), x => x.m_Renter);
                    foreach (Entity guest in guests)
                    {
                        if (!Usable(em, guest) || !em.HasComponent<TouristHousehold>(guest)) continue;
                        TouristHousehold tourist = em.GetComponentData<TouristHousehold>(guest);
                        if (tourist.m_Hotel == Entity.Null) continue;
                        tourist.m_Hotel = Entity.Null;
                        em.SetComponentData(guest, tourist);
                        Touch(guest);
                        n++;
                    }
                }
            }
            return n;
        }

        private static int ReleaseEmployees(EntityManager em, Entity workplace)
        {
            int n = 0;
            Entity[] workers = Members(em.GetBuffer<Employee>(workplace, true), x => x.m_Worker);
            foreach (Entity worker in workers)
            {
                if (!Usable(em, worker)) continue;
                if (em.HasComponent<TravelPurpose>(worker))
                {
                    Purpose p = em.GetComponentData<TravelPurpose>(worker).m_Purpose;
                    if (p == Purpose.GoingToWork || p == Purpose.Working)
                    {
                        em.RemoveComponent<TravelPurpose>(worker);
                        Touch(worker);
                        n++;
                    }
                }
                if (em.HasComponent<Worker>(worker))
                {
                    Entity at = em.GetComponentData<Worker>(worker).m_Workplace;
                    if (at == workplace || at == Entity.Null || !em.Exists(at))
                    {
                        em.RemoveComponent<Worker>(worker);
                        Touch(worker);
                        n++;
                    }
                }
            }
            return n;
        }

        private static int ReleaseStudents(EntityManager em, Entity school)
        {
            int n = 0;
            Entity[] students = Members(em.GetBuffer<Game.Buildings.Student>(school, true), x => x.m_Student);
            foreach (Entity student in students)
            {
                if (!Usable(em, student) || !em.HasComponent<Game.Citizens.Student>(student)) continue;
                if (em.GetComponentData<Game.Citizens.Student>(student).m_School != school) continue;
                if (em.HasComponent<TravelPurpose>(student))
                {
                    Purpose p = em.GetComponentData<TravelPurpose>(student).m_Purpose;
                    if (p == Purpose.GoingToSchool || p == Purpose.Studying)
                    {
                        em.RemoveComponent<TravelPurpose>(student);
                        Touch(student);
                        n++;
                    }
                }
                em.RemoveComponent<Game.Citizens.Student>(student);
                Touch(student);
                n++;
            }
            return n;
        }

        private static int LeaveProperty(EntityManager em, Entity renter)
        {
            Entity property = Entity.Null;
            if (em.HasComponent<PropertyRenter>(renter)) property = em.GetComponentData<PropertyRenter>(renter).m_Property;
            else if (em.HasComponent<HomelessHousehold>(renter)) property = em.GetComponentData<HomelessHousehold>(renter).m_TempHome;
            if (!Usable(em, property) || !em.HasBuffer<Renter>(property)) return 0;
            return RemoveAll(property, em.GetBuffer<Renter>(property), renter, x => x.m_Renter);
        }

        private static int LeaveCitizenLists(EntityManager em, Entity citizen)
        {
            int n = 0;
            if (em.HasComponent<HouseholdMember>(citizen))
            {
                Entity household = em.GetComponentData<HouseholdMember>(citizen).m_Household;
                if (Usable(em, household) && em.HasBuffer<HouseholdCitizen>(household))
                {
                    n += RemoveAll(household, em.GetBuffer<HouseholdCitizen>(household), citizen, x => x.m_Citizen);
                }
            }
            if (em.HasComponent<Game.Citizens.Student>(citizen))
            {
                Entity school = em.GetComponentData<Game.Citizens.Student>(citizen).m_School;
                if (Usable(em, school) && em.HasBuffer<Game.Buildings.Student>(school))
                {
                    n += RemoveAll(school, em.GetBuffer<Game.Buildings.Student>(school), citizen, x => x.m_Student);
                }
            }
            if (em.HasComponent<Worker>(citizen))
            {
                Entity workplace = em.GetComponentData<Worker>(citizen).m_Workplace;
                if (Usable(em, workplace) && em.HasBuffer<Employee>(workplace))
                {
                    n += RemoveAll(workplace, em.GetBuffer<Employee>(workplace), citizen, x => x.m_Worker);
                }
            }
            if (em.HasComponent<CurrentBuilding>(citizen))
            {
                Entity building = em.GetComponentData<CurrentBuilding>(citizen).m_CurrentBuilding;
                if (Usable(em, building))
                {
                    if (em.HasBuffer<Patient>(building)) n += RemoveAll(building, em.GetBuffer<Patient>(building), citizen, x => x.m_Patient);
                    if (em.HasBuffer<Occupant>(building)) n += RemoveAll(building, em.GetBuffer<Occupant>(building), citizen, x => x.m_Occupant);
                }
            }
            return n;
        }

        /// <summary>An entity whose components may be changed: it exists and is not itself being deleted.</summary>
        private static bool Usable(EntityManager em, Entity e)
        {
            return e != Entity.Null && em.Exists(e) && !em.HasComponent<Game.Common.Deleted>(e);
        }

        private static Entity[] Members<T>(DynamicBuffer<T> buffer, Func<T, Entity> get) where T : unmanaged, IBufferElementData
        {
            var result = new Entity[buffer.Length];
            for (int i = 0; i < buffer.Length; i++) result[i] = get(buffer[i]);
            return result;
        }

        private static int RemoveAll<T>(Entity owner, DynamicBuffer<T> buffer, Entity member, Func<T, Entity> get) where T : unmanaged, IBufferElementData
        {
            int n = 0;
            if (buffer.Length > 0) Touch(owner);
            for (int i = buffer.Length - 1; i >= 0; i--)
            {
                if (get(buffer[i]) != member) continue;
                buffer.RemoveAt(i);
                n++;
            }
            return n;
        }
    }
}
