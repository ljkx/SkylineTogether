using System;
using System.Collections.Generic;
using Game.Buildings;
using Game.Citizens;
using Game.Companies;
using SkylineTogether.Core.Sync;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// The game side of <see cref="AgentLinkGraph"/> (ARCHITECTURE.md#reference-safety): reads and changes each
    /// <see cref="AgentLink"/> through the <c>EntityManager</c>. Main thread only; every change goes through the
    /// <c>EntityManager</c>, which completes the jobs that read or write the type before it writes (structural changes
    /// complete all of them).
    /// </summary>
    internal sealed class AgentLinkAccess : IAgentLinkView<Entity>
    {
        private EntityManager _em;

        /// <summary>Entities changed by the last calls (the applier's set: the client never proposes them back); may be null.</summary>
        public HashSet<Entity> Touched;

        /// <summary>Components removed so far (a structural change: chunk arrays taken before it are stale).</summary>
        public long StructuralChanges { get; private set; }

        public AgentLinkAccess(EntityManager em)
        {
            _em = em;
        }

        public EntityManager EntityManager
        {
            get => _em;
            set => _em = value;
        }

        /// <summary>Elements of agent link buffers read from the wire and left out because they named nobody (diagnostics).</summary>
        public static long WireNullsDropped;

        /// <summary>The component or buffer type of a link.</summary>
        public static ComponentType TypeOf(AgentLink link)
        {
            switch (link)
            {
                case AgentLink.Renter: return ComponentType.ReadOnly<Renter>();
                case AgentLink.Employee: return ComponentType.ReadOnly<Employee>();
                case AgentLink.SchoolStudent: return ComponentType.ReadOnly<Game.Buildings.Student>();
                case AgentLink.Patient: return ComponentType.ReadOnly<Patient>();
                case AgentLink.Occupant: return ComponentType.ReadOnly<Occupant>();
                case AgentLink.HouseholdCitizen: return ComponentType.ReadOnly<HouseholdCitizen>();
                case AgentLink.HouseholdAnimal: return ComponentType.ReadOnly<HouseholdAnimal>();
                case AgentLink.PropertyRenter: return ComponentType.ReadOnly<PropertyRenter>();
                case AgentLink.HomelessHome: return ComponentType.ReadOnly<HomelessHousehold>();
                case AgentLink.TouristHotel: return ComponentType.ReadOnly<TouristHousehold>();
                case AgentLink.Worker: return ComponentType.ReadOnly<Worker>();
                case AgentLink.CitizenStudent: return ComponentType.ReadOnly<Game.Citizens.Student>();
                case AgentLink.CurrentBuilding: return ComponentType.ReadOnly<CurrentBuilding>();
                case AgentLink.HouseholdMember: return ComponentType.ReadOnly<HouseholdMember>();
                case AgentLink.HouseholdPet: return ComponentType.ReadOnly<HouseholdPet>();
                default: throw new ArgumentOutOfRangeException(nameof(link), link, null);
            }
        }

        /// <summary>
        /// The element reference of an agent link buffer type that the generic buffer handler reads from the wire
        /// (<c>Employee</c>, <c>Patient</c>, <c>Occupant</c>), or null for every other type: an element naming nobody is
        /// dropped like an unresolved one (<see cref="BufferHandler{T}"/>). The other reverse buffers travel as id lists
        /// (<see cref="EntityRefListHandler{T}"/>), which never keep a null.
        /// </summary>
        public static Func<T, Entity> WireElementRef<T>() where T : unmanaged, IBufferElementData
        {
            Type t = typeof(T);
            if (t == typeof(Employee)) return (Func<T, Entity>)(object)(Func<Employee, Entity>)(x => x.m_Worker);
            if (t == typeof(Patient)) return (Func<T, Entity>)(object)(Func<Patient, Entity>)(x => x.m_Patient);
            if (t == typeof(Occupant)) return (Func<T, Entity>)(object)(Func<Occupant, Entity>)(x => x.m_Occupant);
            return null;
        }

        /// <summary>The state of a reference target on this client.</summary>
        public LinkTarget StateOf(Entity target)
        {
            if (target == Entity.Null) return LinkTarget.Null;
            if (!_em.Exists(target)) return LinkTarget.Destroyed;
            return _em.HasComponent<Game.Common.Deleted>(target) ? LinkTarget.Deleted : LinkTarget.Live;
        }

        public bool IsUsable(Entity e) => e != Entity.Null && _em.Exists(e) && !_em.HasComponent<Game.Common.Deleted>(e);

        public bool TryGetForward(Entity holder, AgentLink link, out Entity target)
        {
            target = Entity.Null;
            switch (link)
            {
                case AgentLink.PropertyRenter:
                    if (!_em.HasComponent<PropertyRenter>(holder)) return false;
                    target = _em.GetComponentData<PropertyRenter>(holder).m_Property;
                    return true;
                case AgentLink.HomelessHome:
                    if (!_em.HasComponent<HomelessHousehold>(holder)) return false;
                    target = _em.GetComponentData<HomelessHousehold>(holder).m_TempHome;
                    return true;
                case AgentLink.TouristHotel:
                    if (!_em.HasComponent<TouristHousehold>(holder)) return false;
                    target = _em.GetComponentData<TouristHousehold>(holder).m_Hotel;
                    return true;
                case AgentLink.Worker:
                    if (!_em.HasComponent<Worker>(holder)) return false;
                    target = _em.GetComponentData<Worker>(holder).m_Workplace;
                    return true;
                case AgentLink.CitizenStudent:
                    if (!_em.HasComponent<Game.Citizens.Student>(holder)) return false;
                    target = _em.GetComponentData<Game.Citizens.Student>(holder).m_School;
                    return true;
                case AgentLink.CurrentBuilding:
                    if (!_em.HasComponent<CurrentBuilding>(holder)) return false;
                    target = _em.GetComponentData<CurrentBuilding>(holder).m_CurrentBuilding;
                    return true;
                case AgentLink.HouseholdMember:
                    if (!_em.HasComponent<HouseholdMember>(holder)) return false;
                    target = _em.GetComponentData<HouseholdMember>(holder).m_Household;
                    return true;
                case AgentLink.HouseholdPet:
                    if (!_em.HasComponent<HouseholdPet>(holder)) return false;
                    target = _em.GetComponentData<HouseholdPet>(holder).m_Household;
                    return true;
                default:
                    return false;
            }
        }

        public bool GetMembers(Entity owner, AgentLink link, List<Entity> members)
        {
            switch (link)
            {
                case AgentLink.Renter: return Collect<Renter>(owner, members, x => x.m_Renter);
                case AgentLink.Employee: return Collect<Employee>(owner, members, x => x.m_Worker);
                case AgentLink.SchoolStudent: return Collect<Game.Buildings.Student>(owner, members, x => x.m_Student);
                case AgentLink.Patient: return Collect<Patient>(owner, members, x => x.m_Patient);
                case AgentLink.Occupant: return Collect<Occupant>(owner, members, x => x.m_Occupant);
                case AgentLink.HouseholdCitizen: return Collect<HouseholdCitizen>(owner, members, x => x.m_Citizen);
                case AgentLink.HouseholdAnimal: return Collect<HouseholdAnimal>(owner, members, x => x.m_HouseholdPet);
                default: return false;
            }
        }

        private bool Collect<T>(Entity owner, List<Entity> members, Func<T, Entity> get) where T : unmanaged, IBufferElementData
        {
            if (!_em.HasBuffer<T>(owner)) return false;
            DynamicBuffer<T> buffer = _em.GetBuffer<T>(owner, true);
            for (int i = 0; i < buffer.Length; i++) members.Add(get(buffer[i]));
            return true;
        }

        /// <summary>
        /// Removes from the owner's reverse buffer every element naming <paramref name="member"/> (the cascade) or, with
        /// <paramref name="member"/> null, every element whose member is not live (the audit). Returns the number removed.
        /// </summary>
        public int DropElements(Entity owner, AgentLink link, Entity? member)
        {
            switch (link)
            {
                case AgentLink.Renter: return Drop<Renter>(owner, member, x => x.m_Renter);
                case AgentLink.Employee: return Drop<Employee>(owner, member, x => x.m_Worker);
                case AgentLink.SchoolStudent: return Drop<Game.Buildings.Student>(owner, member, x => x.m_Student);
                case AgentLink.Patient: return Drop<Patient>(owner, member, x => x.m_Patient);
                case AgentLink.Occupant: return Drop<Occupant>(owner, member, x => x.m_Occupant);
                case AgentLink.HouseholdCitizen: return Drop<HouseholdCitizen>(owner, member, x => x.m_Citizen);
                case AgentLink.HouseholdAnimal: return Drop<HouseholdAnimal>(owner, member, x => x.m_HouseholdPet);
                default: return 0;
            }
        }

        private int Drop<T>(Entity owner, Entity? member, Func<T, Entity> get) where T : unmanaged, IBufferElementData
        {
            if (!_em.HasBuffer<T>(owner)) return 0;
            // read first: a write handle bumps the chunk's change version (the host's key watch, our own hashes)
            DynamicBuffer<T> read = _em.GetBuffer<T>(owner, true);
            int dead = 0;
            for (int i = 0; i < read.Length; i++)
            {
                if (Matches(get(read[i]), member)) dead++;
            }
            if (dead == 0) return 0;
            DynamicBuffer<T> buffer = _em.GetBuffer<T>(owner);
            int n = 0;
            for (int i = buffer.Length - 1; i >= 0; i--)
            {
                if (!Matches(get(buffer[i]), member)) continue;
                buffer.RemoveAt(i);
                n++;
            }
            if (n > 0) Touched?.Add(owner);
            return n;
        }

        private bool Matches(Entity element, Entity? member) => member.HasValue ? element == member.Value : StateOf(element) != LinkTarget.Live;

        /// <summary>
        /// The clean-up of a forward link whose target is gone, as the game's removal systems do it: the field is set to
        /// null (<see cref="LinkCleanup.ClearReference"/>) or the component removed (<see cref="LinkCleanup.RemoveComponent"/>);
        /// a worker or pupil also loses its work or school travel purpose. Returns the number of changes.
        /// </summary>
        public int CleanForward(Entity holder, AgentLink link, LinkCleanup action)
        {
            if (action == LinkCleanup.Keep || action == LinkCleanup.DropElement) return 0;
            int n = 0;
            switch (link)
            {
                case AgentLink.HomelessHome when action == LinkCleanup.ClearReference:
                    if (!_em.HasComponent<HomelessHousehold>(holder)) return 0;
                    HomelessHousehold homeless = _em.GetComponentData<HomelessHousehold>(holder);
                    if (homeless.m_TempHome == Entity.Null) return 0;
                    homeless.m_TempHome = Entity.Null;
                    _em.SetComponentData(holder, homeless);
                    n = 1;
                    break;
                case AgentLink.TouristHotel when action == LinkCleanup.ClearReference:
                    if (!_em.HasComponent<TouristHousehold>(holder)) return 0;
                    TouristHousehold tourist = _em.GetComponentData<TouristHousehold>(holder);
                    if (tourist.m_Hotel == Entity.Null) return 0;
                    tourist.m_Hotel = Entity.Null;
                    _em.SetComponentData(holder, tourist);
                    n = 1;
                    break;
                case AgentLink.Worker:
                    n += DropTravelPurpose(holder, Game.Citizens.Purpose.GoingToWork, Game.Citizens.Purpose.Working);
                    n += Remove(holder, link);
                    break;
                case AgentLink.CitizenStudent:
                    n += DropTravelPurpose(holder, Game.Citizens.Purpose.GoingToSchool, Game.Citizens.Purpose.Studying);
                    n += Remove(holder, link);
                    break;
                default:
                    if (action != LinkCleanup.RemoveComponent) return 0;
                    n += Remove(holder, link);
                    break;
            }
            if (n > 0) Touched?.Add(holder);
            return n;
        }

        private int Remove(Entity holder, AgentLink link)
        {
            ComponentType type = TypeOf(link);
            if (!_em.HasComponent(holder, type)) return 0;
            _em.RemoveComponent(holder, ComponentType.ReadWrite(type.TypeIndex));
            StructuralChanges++;
            return 1;
        }

        private int DropTravelPurpose(Entity citizen, Game.Citizens.Purpose a, Game.Citizens.Purpose b)
        {
            if (!_em.HasComponent<TravelPurpose>(citizen)) return 0;
            Game.Citizens.Purpose p = _em.GetComponentData<TravelPurpose>(citizen).m_Purpose;
            if (p != a && p != b) return 0;
            _em.RemoveComponent<TravelPurpose>(citizen);
            StructuralChanges++;
            return 1;
        }

        /// <summary>Applies one op of a cascade plan; returns the number of changes.</summary>
        public int Apply(in AgentLinkOp<Entity> op)
        {
            if (!IsUsable(op.Holder)) return 0;
            if (op.Action == LinkCleanup.DropElement) return DropElements(op.Holder, op.Link, op.Target);
            // the plan was made before earlier ops ran: change the link only while it still names the gone target
            if (!TryGetForward(op.Holder, op.Link, out Entity at) || (at != op.Target && IsUsable(at))) return 0;
            return CleanForward(op.Holder, op.Link, op.Action);
        }
    }
}
