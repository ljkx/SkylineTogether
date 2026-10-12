using System;
using System.Collections.Generic;
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

        /// <summary>Serializes the entity's value (data and buffer kinds only).</summary>
        public virtual void Write(EntityManager em, Entity e, NetWriter w)
        {
        }

        /// <summary>Deserializes into the entity's value (which must exist).</summary>
        public virtual void Read(EntityManager em, Entity e, NetReader r)
        {
        }

        public override string ToString() => Name;
    }

    internal sealed class PresenceHandler : TypeHandler
    {
    }

    internal sealed class DataHandler<T> : TypeHandler where T : unmanaged, IComponentData, ISerializable
    {
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
            em.SetComponentData(e, value);
        }
    }

    internal sealed class BufferHandler<T> : TypeHandler where T : unmanaged, IBufferElementData, ISerializable
    {
        private const int MaxElements = 1 << 20;

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
            DynamicBuffer<T> buffer = em.GetBuffer<T>(e);
            int old = buffer.Length;
            var elements = new T[count];
            for (int i = 0; i < count; i++)
            {
                T element = i < old ? buffer[i] : default(T);
                element.Deserialize(r);
                elements[i] = element;
            }
            buffer = em.GetBuffer<T>(e);
            buffer.ResizeUninitialized(count);
            for (int i = 0; i < count; i++) buffer[i] = elements[i];
        }
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
            var items = new Entity[count];
            for (int i = 0; i < count; i++) r.Read(out items[i]);
            DynamicBuffer<Game.Routes.RouteWaypoint> buffer = em.GetBuffer<Game.Routes.RouteWaypoint>(e);
            buffer.Clear();
            for (int i = 0; i < count; i++) buffer.Add(new Game.Routes.RouteWaypoint(items[i]));
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
            var items = new Entity[count];
            for (int i = 0; i < count; i++) r.Read(out items[i]);
            DynamicBuffer<Game.Routes.RouteSegment> buffer = em.GetBuffer<Game.Routes.RouteSegment>(e);
            buffer.Clear();
            for (int i = 0; i < count; i++) buffer.Add(new Game.Routes.RouteSegment(items[i]));
        }
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
                Mod.Log?.Warn(LogCategory.Replication, "a replicated component type is unknown here (stable hash 0x" + hash.ToString("X16") + "); is a mod missing?");
            }
            return handler;
        }

        public static TypeHandler Get<T>() => Get(ComponentType.ReadWrite<T>());

        private static TypeHandler Create(TypeIndex index)
        {
            if (index == TypeIndex.Null || index.IsSharedComponentType || index.IsManagedComponent || index.IsChunkComponent || index.IsCleanupComponent) return null;
            Type type = TypeManager.GetType(index);
            if (type == null || Excluded.Contains(type.FullName ?? "")) return null;
            TypeHandler special = null;
            if (type == typeof(Game.Routes.RouteWaypoint)) special = new RouteWaypointsHandler();
            else if (type == typeof(Game.Routes.RouteSegment)) special = new RouteSegmentsHandler();
            if (special != null)
            {
                special.Kind = SyncComponentKind.Buffer;
                special.Index = index;
                special.Type = ComponentType.ReadWrite(index);
                special.Hash = TypeManager.GetTypeInfo(index).StableTypeHash;
                special.Name = type.FullName;
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
            return handler;
        }
    }
}
