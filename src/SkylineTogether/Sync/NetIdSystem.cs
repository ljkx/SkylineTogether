using Colossal.Serialization.Entities;
using Game;
using Game.Serialization;
using SkylineTogether.Core;
using SkylineTogether.Core.Sync;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// Network identity of replicated entities (ARCH §3.3-3.4): dense side tables, never a component, so binding costs
    /// no structural change. <see cref="WriterTable"/> (index = <c>Entity.Index</c>, value = <c>Entity(netId,
    /// version)</c>) and <see cref="ReaderTable"/> (index = net id, value = the local entity) plug straight into the
    /// game's <c>BinaryWriter</c>/<c>BinaryReader</c>. The bindings of tracked entities are saved into every save
    /// (the join snapshot included) by the game's own system serializer, so a joiner's freshly loaded entities carry
    /// the host's ids without any table transfer. Without the mod, the block is skipped by the game
    /// (<c>ObsoleteSystemSerializer</c> skips it by its size); a save without the block resets the tables
    /// (<c>SetDefaults</c>).
    /// <para>
    /// Single-player safety: the bindings are written only into a join snapshot (<see cref="WritingSnapshot"/>, set by
    /// <c>SnapshotService</c> around its save); every other save, including the host's own saves and autosaves during a
    /// session, carries an empty block of 12 bytes. Loaded bindings are kept only by a joining client; anyone else
    /// reads and drops them. The tables never grow without a session.
    /// </para>
    /// </summary>
    [FormerlySerializedAs("SkylineTogether.Sync.NetIdSystem, SkylineTogether")]
    public sealed class NetIdSystem : GameSystemBase, IDefaultSerializable, ISerializable, IPostDeserialize
    {
        private const int FormatVersion = 1;

        /// <summary>Net ids above this are treated as corrupt (a bound table of this size would already be ~1 GB).</summary>
        private const uint MaxNetId = 1u << 26;

        /// <summary>True while the host's join snapshot is being written (the only save that carries the bindings).</summary>
        public static volatile bool WritingSnapshot;

        private NativeList<Entity> _writer;
        private NativeList<Entity> _reader;
        private NativeList<byte> _family;
        private int _bound;

        public NetIdAllocator Ids { get; private set; } = new NetIdAllocator();

        /// <summary>Index = <c>Entity.Index</c>; value = <c>Entity(netId, version)</c>, default when unbound.</summary>
        public NativeArray<Entity> WriterTable => _writer.AsArray();

        /// <summary>Index = net id; value = the local entity (<c>Entity.Null</c> when unbound). Index 0 is always null.</summary>
        public NativeArray<Entity> ReaderTable => _reader.AsArray();

        public int BoundCount => _bound;

        protected override void OnCreate()
        {
            base.OnCreate();
            _writer = new NativeList<Entity>(1024, Allocator.Persistent);
            _reader = new NativeList<Entity>(1024, Allocator.Persistent);
            _family = new NativeList<byte>(1024, Allocator.Persistent);
            Clear();
        }

        protected override void OnDestroy()
        {
            if (_writer.IsCreated) _writer.Dispose();
            if (_reader.IsCreated) _reader.Dispose();
            if (_family.IsCreated) _family.Dispose();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
        }

        public void Clear()
        {
            // review fix: a client session can end on exit after the world destroyed this system (lists disposed)
            if (!_writer.IsCreated || !_reader.IsCreated || !_family.IsCreated) return;
            _writer.Clear();
            _reader.Clear();
            _family.Clear();
            // a session's tables (one entry per entity of the city) are released when it ends
            if (_writer.Capacity > 4096) _writer.Capacity = 1024;
            if (_reader.Capacity > 4096) _reader.Capacity = 1024;
            if (_family.Capacity > 4096) _family.Capacity = 1024;
            _reader.Add(Entity.Null);
            _family.Add(0);
            _writer.Add(default(Entity));
            _bound = 0;
            Ids = new NetIdAllocator();
        }

        /// <summary>The entity's net id, 0 when unbound (or when the index now belongs to another entity).</summary>
        public uint GetNetId(Entity entity)
        {
            if (entity.Index <= 0 || entity.Index >= _writer.Length) return 0;
            Entity e = _writer[entity.Index];
            return e.Version == entity.Version && e.Index > 0 ? (uint)e.Index : 0;
        }

        /// <summary>The local entity bound to <paramref name="netId"/>, or <c>Entity.Null</c>.</summary>
        public Entity GetEntity(uint netId)
        {
            return netId > 0 && netId < (uint)_reader.Length ? _reader[(int)netId] : Entity.Null;
        }

        public Family GetFamily(uint netId)
        {
            return netId > 0 && netId < (uint)_family.Length ? (Family)_family[(int)netId] : Family.Generic;
        }

        public void Bind(Entity entity, uint netId, Family family)
        {
            if (entity == Entity.Null || netId == 0 || netId >= int.MaxValue) return;
            Entity old = GetEntity(netId);
            if (old != Entity.Null && old != entity) UnbindEntityIndex(old);
            uint previous = GetNetId(entity);
            if (previous != 0 && previous != netId) UnbindId(previous);
            EnsureReader((int)netId);
            EnsureWriter(entity.Index);
            if (_reader[(int)netId] == Entity.Null) _bound++;
            _reader[(int)netId] = entity;
            _family[(int)netId] = (byte)family;
            _writer[entity.Index] = new Entity { Index = (int)netId, Version = entity.Version };
            Ids.Reserve(netId);
        }

        public void Unbind(uint netId)
        {
            Entity e = GetEntity(netId);
            if (e != Entity.Null) UnbindEntityIndex(e);
            UnbindId(netId);
        }

        private void UnbindId(uint netId)
        {
            if (netId == 0 || netId >= (uint)_reader.Length) return;
            if (_reader[(int)netId] != Entity.Null) _bound--;
            _reader[(int)netId] = Entity.Null;
            _family[(int)netId] = 0;
        }

        private void UnbindEntityIndex(Entity e)
        {
            if (e.Index > 0 && e.Index < _writer.Length && _writer[e.Index].Version == e.Version) _writer[e.Index] = default(Entity);
        }

        private void EnsureReader(int index)
        {
            if (index < _reader.Length) return;
            int size = System.Math.Max(index + 1, _reader.Length * 2);
            int old = _reader.Length;
            _reader.Resize(size, NativeArrayOptions.ClearMemory);
            _family.Resize(size, NativeArrayOptions.ClearMemory);
            for (int i = old; i < size; i++) _reader[i] = Entity.Null;
        }

        private void EnsureWriter(int index)
        {
            if (index < _writer.Length) return;
            int size = System.Math.Max(index + 1, _writer.Length * 2);
            _writer.Resize(size, NativeArrayOptions.ClearMemory);
        }

        /// <summary>Drops bindings whose entity no longer exists.</summary>
        public int Prune()
        {
            int dropped = 0;
            for (int id = 1; id < _reader.Length; id++)
            {
                Entity e = _reader[id];
                if (e == Entity.Null) continue;
                if (!EntityManager.Exists(e))
                {
                    Unbind((uint)id);
                    dropped++;
                }
            }
            return dropped;
        }

        // ------------------------------------------------------------------ persistence (every save)

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            bool include = WritingSnapshot && Mod.Sessions?.Host != null && _reader.IsCreated;
            writer.Write(FormatVersion);
            writer.Write(include ? Ids.Next : 0u);
            int count = 0;
            if (include)
            {
                for (int id = 1; id < _reader.Length; id++)
                {
                    if (_reader[id] != Entity.Null && (Family)_family[id] != Family.Prefab) count++;
                }
            }
            writer.Write(count);
            if (count == 0) return;
            for (int id = 1; id < _reader.Length; id++)
            {
                Entity e = _reader[id];
                if (e == Entity.Null || (Family)_family[id] == Family.Prefab) continue;
                writer.Write(e);
                writer.Write((uint)id);
                writer.Write(_family[id]);
            }
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            // runs inside the deserializer's exclusive entity transaction: containers only
            Clear();
            reader.Read(out int format);
            if (format != FormatVersion) return; // a newer build's block: the reader skips the rest (logged by the game)
            reader.Read(out uint next);
            reader.Read(out int count);
            if (count < 0 || next > MaxNetId) return;
            // only a client joining the host's session uses the host's ids; any other load reads them and drops them
            bool keep = Mod.Sessions?.Client != null;
            for (int i = 0; i < count; i++)
            {
                reader.Read(out Entity e);
                reader.Read(out uint id);
                reader.Read(out byte family);
                if (keep && e != Entity.Null && id < MaxNetId) Bind(e, id, (Family)family);
            }
            if (keep) Ids.Reserve(next > 0 ? next - 1 : 0);
        }

        public void SetDefaults(Context context)
        {
            Clear();
        }

        public void PostDeserialize(Context context)
        {
            try
            {
                if (_bound == 0) return;
                int dropped = Prune();
                Mod.Log?.Info(LogCategory.Replication, "net ids after load: " + _bound + " bound" + (dropped > 0 ? ", " + dropped + " dropped" : ""));
            }
            catch (System.Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Replication, "net ids after load", ex);
            }
        }
    }
}
