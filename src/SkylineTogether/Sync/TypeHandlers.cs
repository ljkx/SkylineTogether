using System;
using System.Collections.Generic;
using System.Globalization;
using Colossal.Serialization.Entities;
using SkylineTogether.Core;
using SkylineTogether.Core.Sync;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>How one component type is read from and written to an entity, through its own <c>ISerializable</c>.</summary>
    public abstract class TypeHandler
    {
        public TypeIndex Index;
        public ComponentType Type;
        public ulong Hash;
        public SyncComponentKind Kind;
        public string Name;
        /// <summary>
        /// Mod compatibility: the component is enableable and the save format keeps its enabled bit (not
        /// <c>ISerializeAsEnabled</c>), so the bit replicates too (<c>SyncRecordKind.EnabledBits</c>).
        /// </summary>
        public bool SyncEnabledBit;
        /// <summary>Mod compatibility: the type comes from another mod's assembly (not the game's).</summary>
        public bool FromMod;

        /// <summary>Serializes the entity's value (data and buffer kinds only).</summary>
        public virtual void Write(EntityManager em, Entity e, NetWriter w)
        {
        }

        /// <summary>Deserializes into the entity's value (which must exist).</summary>
        public virtual void Read(EntityManager em, Entity e, NetReader r)
        {
        }

        /// <summary>
        /// crashfix: deserializes the value without writing it anywhere, so the reader's unresolved flag tells whether its
        /// references resolve here (spawn validation of structural references). Data components only; others read nothing.
        /// </summary>
        public virtual void Probe(NetReader r)
        {
        }

        /// <summary>crashfix: true when <see cref="Probe"/> reads the value (data components).</summary>
        public virtual bool CanProbe => false;

        /// <summary>
        /// w8/robust: true when decoded values are checked for NaN, infinite and out-of-range floats before they are
        /// written (<see cref="FloatSanity"/>); a value that fails throws a <see cref="DecodeException"/> and is not written.
        /// </summary>
        public virtual bool ChecksFloats => false;

        /// <summary>w8/robust: the field offsets of a value type as Unity lays it out (for <see cref="FloatLayout"/>).</summary>
        internal static int FieldOffset(System.Reflection.FieldInfo f) => Unity.Collections.LowLevel.Unsafe.UnsafeUtility.GetFieldOffset(f);

        /// <summary>
        /// w3/agents: an order-insensitive hash of a reference list (reverse buffers, ARCH §4.2.4); false for every other
        /// type and when an element cannot be named on the other peer.
        /// </summary>
        public virtual bool TryHashAsSet(EntityManager em, Entity e, NetIdSystem ids, out ulong hash)
        {
            hash = 0;
            return false;
        }

        public override string ToString() => Name;
    }

    internal sealed class PresenceHandler : TypeHandler
    {
    }

    internal sealed class DataHandler<T> : TypeHandler where T : unmanaged, IComponentData, ISerializable
    {
        // w8/robust: geometry values are checked before they are written (null: the type is not checked)
        private static readonly FloatLayout Floats = FloatSanity.LayoutOf(typeof(T), FieldOffset);

        public override bool ChecksFloats => Floats != null;

        private void Check(in T value)
        {
            if (Floats != null && !Floats.IsSane(value)) throw new DecodeException(Name + ": NaN, infinite or out-of-range value refused");
        }
        public override void Write(EntityManager em, Entity e, NetWriter w)
        {
            T value = em.GetComponentData<T>(e);
            value.Serialize(w);
        }

        public override void Read(EntityManager em, Entity e, NetReader r)
        {
            // deserialize over the current value: fields the save format does not carry keep their local value
            T value = em.GetComponentData<T>(e);
            value.Deserialize(r);
            Check(value);
            // crashfix: a reference that did not resolve (unknown, destroyed or deleted here) would be written as a null
            // over a valid local reference; an existing entity keeps its value until a record that resolves arrives
            if (r.Unresolved && r.KeepOnUnresolved) return;
            em.SetComponentData(e, value);
        }

        public override void Probe(NetReader r)
        {
            T value = default(T);
            value.Deserialize(r);
            Check(value);
        }

        public override bool CanProbe => true;
    }

    internal sealed class BufferHandler<T> : TypeHandler where T : unmanaged, IBufferElementData, ISerializable
    {
        private const int MaxElements = 1 << 20;

        // w8/robust: area polygons are checked element by element; one bad element refuses the whole buffer (dropping a
        // node would change the shape)
        private static readonly FloatLayout Floats = FloatSanity.LayoutOf(typeof(T), FieldOffset);

        public override bool ChecksFloats => Floats != null;

        public override void Write(EntityManager em, Entity e, NetWriter w)
        {
            DynamicBuffer<T> buffer = em.GetBuffer<T>(e, true);
            w.Write(buffer.Length);
            for (int i = 0; i < buffer.Length; i++)
            {
                T element = buffer[i];
                element.Serialize(w);
            }
        }

        public override void Read(EntityManager em, Entity e, NetReader r)
        {
            r.Read(out int count);
            if (count < 0 || count > MaxElements) throw new DecodeException(Name + ": buffer of " + count + " elements");
            // w8/robust: elements are at least one byte unless their format writes nothing (few, short buffers)
            if (count > 4096 && count > r.Remaining) throw new DecodeException(Name + ": buffer of " + count + " elements in " + r.Remaining + " bytes");
            DynamicBuffer<T> buffer = em.GetBuffer<T>(e);
            int old = buffer.Length;
            var elements = new T[count];
            int kept = 0;
            bool anyUnresolved = r.TakeUnresolved();
            for (int i = 0; i < count; i++)
            {
                T element = i < old ? buffer[i] : default(T);
                element.Deserialize(r);
                if (Floats != null && !Floats.IsSane(element)) throw new DecodeException(Name + ": NaN, infinite or out-of-range element refused");
                // crashfix: an element whose reference did not resolve is left out instead of pointing at nothing
                // (ConnectedNode, AggregateElement, LayoutElement, ...: the game's jobs index such references unchecked)
                if (r.TakeUnresolved())
                {
                    anyUnresolved = true;
                    continue;
                }
                elements[kept++] = element;
            }
            if (anyUnresolved) r.MarkUnresolved();
            buffer = em.GetBuffer<T>(e);
            buffer.ResizeUninitialized(kept);
            for (int i = 0; i < kept; i++) buffer[i] = elements[i];
        }

        /// <summary>w8/robust: decodes the elements without writing them (spawn validation of checked geometry).</summary>
        public override void Probe(NetReader r)
        {
            r.Read(out int count);
            if (count < 0 || count > MaxElements || (count > 4096 && count > r.Remaining)) throw new DecodeException(Name + ": buffer of " + count + " elements");
            for (int i = 0; i < count; i++)
            {
                T element = default(T);
                element.Deserialize(r);
                if (Floats != null && !Floats.IsSane(element)) throw new DecodeException(Name + ": NaN, infinite or out-of-range element refused");
            }
        }

        public override bool CanProbe => Floats != null;
    }

    /// <summary>
    /// Ordered reference buffers the save format marks as derived (<c>IEmptySerializable</c>) but which are source data
    /// during play (ARCH K5): a line's waypoint and segment lists. They travel as lists of net ids.
    /// </summary>
    internal sealed class RouteWaypointsHandler : TypeHandler
    {
        public override void Write(EntityManager em, Entity e, NetWriter w)
        {
            DynamicBuffer<Game.Routes.RouteWaypoint> buffer = em.GetBuffer<Game.Routes.RouteWaypoint>(e, true);
            w.Write(buffer.Length);
            for (int i = 0; i < buffer.Length; i++) w.Write(buffer[i].m_Waypoint);
        }

        public override void Read(EntityManager em, Entity e, NetReader r)
        {
            r.Read(out int count);
            if (count < 0 || count > 65536) throw new DecodeException("route with " + count + " waypoints");
            if (count > r.Remaining / 4) throw new DecodeException("route with " + count + " waypoints" + " in " + r.Remaining + " bytes"); // w8/robust: 4 bytes per reference
            var items = new Entity[count];
            for (int i = 0; i < count; i++) r.Read(out items[i]);
            // crashfix: a line keeps its waypoint list while a waypoint is unknown here (a null element made route
            // systems index nothing; dropping one would misalign the segment list, which is indexed by waypoint)
            if (r.Unresolved && r.KeepOnUnresolved) return;
            DynamicBuffer<Game.Routes.RouteWaypoint> buffer = em.GetBuffer<Game.Routes.RouteWaypoint>(e);
            buffer.Clear();
            for (int i = 0; i < count; i++)
            {
                if (items[i] != Entity.Null) buffer.Add(new Game.Routes.RouteWaypoint(items[i]));
            }
        }
    }

    internal sealed class RouteSegmentsHandler : TypeHandler
    {
        public override void Write(EntityManager em, Entity e, NetWriter w)
        {
            DynamicBuffer<Game.Routes.RouteSegment> buffer = em.GetBuffer<Game.Routes.RouteSegment>(e, true);
            w.Write(buffer.Length);
            for (int i = 0; i < buffer.Length; i++) w.Write(buffer[i].m_Segment);
        }

        public override void Read(EntityManager em, Entity e, NetReader r)
        {
            r.Read(out int count);
            if (count < 0 || count > 65536) throw new DecodeException("route with " + count + " segments");
            if (count > r.Remaining / 4) throw new DecodeException("route with " + count + " segments" + " in " + r.Remaining + " bytes"); // w8/robust: 4 bytes per reference
            var items = new Entity[count];
            for (int i = 0; i < count; i++) r.Read(out items[i]);
            if (r.Unresolved && r.KeepOnUnresolved) return; // crashfix: see RouteWaypointsHandler
            DynamicBuffer<Game.Routes.RouteSegment> buffer = em.GetBuffer<Game.Routes.RouteSegment>(e);
            buffer.Clear();
            for (int i = 0; i < count; i++)
            {
                if (items[i] != Entity.Null) buffer.Add(new Game.Routes.RouteSegment(items[i]));
            }
        }
    }

    /// <summary>
    /// w3/agents: reverse buffers the save format marks as derived (<c>IEmptySerializable</c>) but which only simulation
    /// systems (off on clients) maintain during play (ARCH §4.2.4, K5): building <c>Renter</c> and <c>Student</c>,
    /// household <c>HouseholdCitizen</c> and <c>HouseholdAnimal</c>. They travel as lists of net ids in the host's order;
    /// an element the reader cannot resolve yet (a member spawned in a later batch) is left out, and the integrity hash
    /// treats the list as a multiset (<see cref="TryHashAsSet"/>), because a loaded save rebuilds them in chunk order.
    /// While agents are not replicated their elements have no net id, so the codec sends the buffer as presence only, as
    /// before.
    /// </summary>
    internal abstract class EntityRefListHandler<T> : TypeHandler where T : unmanaged, IBufferElementData
    {
        private const int MaxElements = 1 << 16;

        protected abstract Entity Get(T element);

        protected abstract T Make(Entity entity);

        public override void Write(EntityManager em, Entity e, NetWriter w)
        {
            DynamicBuffer<T> buffer = em.GetBuffer<T>(e, true);
            w.Write(buffer.Length);
            for (int i = 0; i < buffer.Length; i++) w.Write(Get(buffer[i]));
        }

        public override void Read(EntityManager em, Entity e, NetReader r)
        {
            r.Read(out int count);
            if (count < 0 || count > MaxElements) throw new DecodeException(Name + ": list of " + count + " entries");
            if (count > r.Remaining / 4) throw new DecodeException(Name + ": list of " + count + " entries" + " in " + r.Remaining + " bytes"); // w8/robust: 4 bytes per reference
            var items = new Entity[count];
            for (int i = 0; i < count; i++) r.Read(out items[i]);
            DynamicBuffer<T> buffer = em.GetBuffer<T>(e);
            buffer.Clear();
            for (int i = 0; i < count; i++)
            {
                if (items[i] != Entity.Null) buffer.Add(Make(items[i]));
            }
        }

        public override bool TryHashAsSet(EntityManager em, Entity e, NetIdSystem ids, out ulong hash)
        {
            hash = 0;
            if (!em.HasBuffer<T>(e)) return false;
            DynamicBuffer<T> buffer = em.GetBuffer<T>(e, true);
            var acc = new Core.Hashing.SetHash.Accumulator();
            for (int i = 0; i < buffer.Length; i++)
            {
                Entity member = Get(buffer[i]);
                if (member == Entity.Null) continue;
                uint id = ids.GetNetId(member);
                if (id == 0) return false; // not nameable on the other peer: presence only, exactly as the codec sends it
                acc.Add(id);
            }
            hash = acc.Value;
            return true;
        }
    }

    internal sealed class RenterListHandler : EntityRefListHandler<Game.Buildings.Renter>
    {
        protected override Entity Get(Game.Buildings.Renter element) => element.m_Renter;

        protected override Game.Buildings.Renter Make(Entity entity) => new Game.Buildings.Renter { m_Renter = entity };
    }

    internal sealed class HouseholdCitizenListHandler : EntityRefListHandler<Game.Citizens.HouseholdCitizen>
    {
        protected override Entity Get(Game.Citizens.HouseholdCitizen element) => element.m_Citizen;

        protected override Game.Citizens.HouseholdCitizen Make(Entity entity) => new Game.Citizens.HouseholdCitizen(entity);
    }

    internal sealed class HouseholdAnimalListHandler : EntityRefListHandler<Game.Citizens.HouseholdAnimal>
    {
        protected override Entity Get(Game.Citizens.HouseholdAnimal element) => element.m_HouseholdPet;

        protected override Game.Citizens.HouseholdAnimal Make(Entity entity) => new Game.Citizens.HouseholdAnimal(entity);
    }

    internal sealed class StudentListHandler : EntityRefListHandler<Game.Buildings.Student>
    {
        protected override Entity Get(Game.Buildings.Student element) => element.m_Student;

        protected override Game.Buildings.Student Make(Entity entity) => new Game.Buildings.Student(entity);
    }

    /// <summary>
    /// Which component types replicate and how (ARCH §3.6): every type the save format persists. <c>ISerializable</c>
    /// data and buffers travel as data; zero-size types and <c>IEmptySerializable</c> types (derived data such as
    /// geometry) as presence. Shared, managed, chunk and cleanup components never travel, nor do the few tool-local
    /// types listed in <see cref="Excluded"/>.
    /// </summary>
    public static class TypeHandlers
    {
        /// <summary>Persistent in saves but local to one machine's tools.</summary>
        private static readonly HashSet<string> Excluded = new HashSet<string>(StringComparer.Ordinal)
        {
            "Game.Tools.Hidden",
            "Game.Tools.Highlighted",
            "Game.Tools.Error",
            "Game.Tools.Warning",
            "Game.Tools.Override",
            "Game.Tools.Temp",
            "Game.Common.Updated",
            "Game.Common.Created",
            "Game.Common.Deleted",
            "Game.Common.Applied",
            "Game.Common.BatchesUpdated",
            "Game.Common.EffectsUpdated",
            "Game.Common.PathfindUpdated",
            // feat/integrity: maintained by IconCommandSystem on every peer (icons come from the host through IconSync);
            // reconciling its presence would strip a client owner's buffer and orphan its icon entities
            "Game.Notifications.IconElement",
            // w4/flow: the flow-graph connections on net nodes, edges and buildings point at derived flow nodes and edges
            // that every peer builds itself (graph systems in Modification phases). As presence-only components a spawn
            // would add them with null references, which the edge graph system then takes for an existing flow node.
            // Flow values travel by flow key instead (FlowStatusSync, ARCH 4.11.5).
            "Game.Simulation.ElectricityNodeConnection",
            "Game.Simulation.ElectricityValveConnection",
            "Game.Simulation.ElectricityBuildingConnection",
            "Game.Simulation.WaterPipeNodeConnection",
            "Game.Simulation.WaterPipeValveConnection",
            "Game.Simulation.WaterPipeBuildingConnection",
        };

        /// <summary>The excluded type names (probed at startup, <see cref="Diagnostics.Probes"/>).</summary>
        public static IEnumerable<string> ExcludedTypeNames => Excluded;

        private static readonly Dictionary<int, TypeHandler> ByIndex = new Dictionary<int, TypeHandler>();
        private static readonly Dictionary<ulong, TypeHandler> ByHash = new Dictionary<ulong, TypeHandler>();
        private static readonly HashSet<ulong> UnknownHashes = new HashSet<ulong>();

        /// <summary>The handler of a component type, or null when the type does not replicate.</summary>
        public static TypeHandler Get(TypeIndex index)
        {
            int key = index.Value;
            if (ByIndex.TryGetValue(key, out TypeHandler cached)) return cached;
            TypeHandler handler = null;
            try
            {
                handler = Create(index);
            }
            catch (Exception ex)
            {
                Mod.Log?.Warn(LogCategory.Replication, "type " + index + " cannot replicate: " + ex.Message);
            }
            ByIndex[key] = handler;
            if (handler != null) ByHash[handler.Hash] = handler;
            return handler;
        }

        public static TypeHandler Get(ComponentType type) => Get(type.TypeIndex);

        /// <summary>The handler for a stable type hash from the wire, or null (logged once) when this game lacks the type.</summary>
        public static TypeHandler GetByHash(ulong hash)
        {
            if (ByHash.TryGetValue(hash, out TypeHandler handler)) return handler;
            TypeIndex index = TypeManager.GetTypeIndexFromStableTypeHash(hash);
            if (index != TypeIndex.Null) handler = Get(index);
            if (handler == null && UnknownHashes.Add(hash))
            {
                Mod.Log?.Warn(LogCategory.Replication, "a replicated component type is unknown here (stable hash 0x" + hash.ToString("X16", CultureInfo.InvariantCulture) + "); is a mod missing?");
            }
            return handler;
        }

        public static TypeHandler Get<T>() => Get(ComponentType.ReadWrite<T>());

        private static TypeHandler Create(TypeIndex index)
        {
            if (index == TypeIndex.Null || index.IsSharedComponentType || index.IsManagedComponent || index.IsChunkComponent || index.IsCleanupComponent) return null;
            Type type = TypeManager.GetType(index);
            if (type == null || Excluded.Contains(type.FullName ?? "")) return null;
            // mod compatibility: a saved mod type the mod declares never-replicate (its entity still replicates)
            if (Compat.ModCompat.IsExcludedType(type)) return null;
            TypeHandler special = null;
            if (type == typeof(Game.Routes.RouteWaypoint)) special = new RouteWaypointsHandler();
            else if (type == typeof(Game.Routes.RouteSegment)) special = new RouteSegmentsHandler();
            // w3/agents: reverse buffers replicated as data (K5)
            else if (type == typeof(Game.Buildings.Renter)) special = new RenterListHandler();
            else if (type == typeof(Game.Citizens.HouseholdCitizen)) special = new HouseholdCitizenListHandler();
            else if (type == typeof(Game.Citizens.HouseholdAnimal)) special = new HouseholdAnimalListHandler();
            else if (type == typeof(Game.Buildings.Student)) special = new StudentListHandler();
            if (special != null)
            {
                special.Kind = SyncComponentKind.Buffer;
                special.Index = index;
                special.Type = ComponentType.ReadWrite(index);
                special.Hash = TypeManager.GetTypeInfo(index).StableTypeHash;
                special.Name = type.FullName;
                special.FromMod = false;
                return special;
            }
            bool serializable = typeof(ISerializable).IsAssignableFrom(type);
            bool empty = typeof(IEmptySerializable).IsAssignableFrom(type);
            if (!serializable && !empty) return null;
            TypeHandler handler;
            if (serializable && index.IsBuffer)
            {
                handler = (TypeHandler)Activator.CreateInstance(typeof(BufferHandler<>).MakeGenericType(type));
                handler.Kind = SyncComponentKind.Buffer;
            }
            else if (serializable && !index.IsZeroSized && typeof(IComponentData).IsAssignableFrom(type))
            {
                handler = (TypeHandler)Activator.CreateInstance(typeof(DataHandler<>).MakeGenericType(type));
                handler.Kind = SyncComponentKind.Data;
            }
            else
            {
                handler = new PresenceHandler { Kind = SyncComponentKind.Presence };
            }
            handler.Index = index;
            handler.Type = ComponentType.ReadWrite(index);
            handler.Hash = TypeManager.GetTypeInfo(index).StableTypeHash;
            handler.Name = type.FullName;
            handler.SyncEnabledBit = index.IsEnableable && !typeof(ISerializeAsEnabled).IsAssignableFrom(type);
            handler.FromMod = type.Assembly != typeof(Game.Common.Owner).Assembly;
            return handler;
        }
    }
}
