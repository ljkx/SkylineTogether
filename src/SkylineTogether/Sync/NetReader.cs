using System.Collections.Generic;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Hash128 = Colossal.Hash128;

namespace SkylineTogether.Sync
{
    /// <summary>Per-read state shared by every copy of a <see cref="NetReader"/>.</summary>
    public sealed class NetReaderState
    {
        public NativeArray<Entity> Table;
        /// <summary>The component's derived keys, already resolved to local entities (Entity.Null when not found).</summary>
        public readonly List<Entity> Keys = new List<Entity>();
        /// <summary>A reference could not be resolved (unknown id or derived key not found).</summary>
        public bool Unresolved;

        /// <summary>
        /// crashfix: when set, a reference whose bound entity no longer exists or carries <c>Deleted</c> resolves to
        /// <c>Entity.Null</c> and counts as unresolved. A stale binding (an entity the local game destroyed, a despawn
        /// applied by an earlier batch of the same frame) otherwise became a component or buffer reference to a destroyed
        /// entity, which the game's Burst jobs dereference without a check (a native crash, not an exception).
        /// </summary>
        public bool CheckLiveness;

        /// <summary>crashfix: the entity manager the liveness check uses (valid while <see cref="CheckLiveness"/>).</summary>
        public EntityManager EntityManager;

        /// <summary>crashfix: references dropped because their entity was destroyed or deleted (diagnostics).</summary>
        public long DeadReferences;

        /// <summary>crashfix: see <see cref="NetReader.KeepOnUnresolved"/>.</summary>
        public bool KeepOnUnresolved;

        /// <summary>crashfix: when not null, every resolved reference is added (spawn validation follows references).</summary>
        public List<Entity> Collect;

        /// <summary>crashfix: true when the entity may be referenced (exists and is not being deleted).</summary>
        public bool IsLive(Entity e)
        {
            if (!CheckLiveness || e == Entity.Null) return true;
            return EntityManager.Exists(e) && !EntityManager.HasComponent<Game.Common.Deleted>(e);
        }
    }

    /// <summary>
    /// The game's <see cref="BinaryReader"/> plus derived keys: a negative reference <c>-2 - k</c> resolves to the
    /// component's k-th derived key. Mirrors <see cref="NetWriter"/>.
    /// </summary>
    public struct NetReader : IReader
    {
        private BinaryReader _inner;
        private NetReaderState _state;

        public NetReader(BinaryReader inner, NetReaderState state)
        {
            _inner = inner;
            _state = state;
        }

        public Context context => _inner.context;

        public void Initialize(Context context, NativeArray<byte> buffer, NativeReference<int> position, NativeArray<Entity> entityTable) => _inner.Initialize(context, buffer, position, entityTable);

        public ReaderBlock Begin() => _inner.Begin();

        public ReaderBlock Begin(out int size) => _inner.Begin(out size);

        public bool End(ReaderBlock block) => _inner.End(block);

        public void Read(out Entity value)
        {
            _inner.Read(out int index);
            if (index >= 0)
            {
                NativeArray<Entity> table = _state.Table;
                value = index < table.Length ? table[index] : Entity.Null;
                if (index > 0 && value != Entity.Null && !_state.IsLive(value))
                {
                    value = Entity.Null;
                    _state.DeadReferences++;
                }
                if (index > 0 && value == Entity.Null) _state.Unresolved = true;
                else if (value != Entity.Null) _state.Collect?.Add(value);
                return;
            }
            if (index == -1)
            {
                value = Entity.Null;
                return;
            }
            int key = -2 - index;
            value = key < _state.Keys.Count ? _state.Keys[key] : Entity.Null;
            if (value != Entity.Null && !_state.IsLive(value))
            {
                value = Entity.Null;
                _state.DeadReferences++;
            }
            if (value == Entity.Null) _state.Unresolved = true;
            else _state.Collect?.Add(value);
        }

        /// <summary>crashfix: true once a reference of the current component could not be resolved (unknown, destroyed or deleted).</summary>
        public bool Unresolved => _state != null && _state.Unresolved;

        /// <summary>crashfix: clears the unresolved flag and returns its previous value (per-element bookkeeping of buffers).</summary>
        public bool TakeUnresolved()
        {
            if (_state == null) return false;
            bool was = _state.Unresolved;
            _state.Unresolved = false;
            return was;
        }

        /// <summary>crashfix: sets the unresolved flag again (restores what <see cref="TakeUnresolved"/> took).</summary>
        public void MarkUnresolved()
        {
            if (_state != null) _state.Unresolved = true;
        }

        /// <summary>
        /// crashfix: true when a component whose references did not all resolve must keep the entity's current value
        /// instead of writing nulls over it (set by the codec for existing entities).
        /// </summary>
        public bool KeepOnUnresolved => _state != null && _state.KeepOnUnresolved;

        public void Read(NativeArray<Entity> value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                Read(out Entity e);
                value[i] = e;
            }
        }

        public void Read(NativeList<Entity> value)
        {
            _inner.Read(out int count);
            if (value.Length != count) value.ResizeUninitialized(count);
            for (int i = 0; i < count; i++)
            {
                Read(out Entity e);
                value[i] = e;
            }
        }

        public void Read<TSerializable>(NativeArray<TSerializable> value) where TSerializable : struct, ISerializable
        {
            for (int i = 0; i < value.Length; i++)
            {
                Read(out TSerializable v);
                value[i] = v;
            }
        }

        public void Read<TSerializable>(out TSerializable value) where TSerializable : struct, ISerializable
        {
            value = default(TSerializable);
            value.Deserialize(this);
        }

        public void Read<TSerializable>(TSerializable value) where TSerializable : class, ISerializable => value.Deserialize(this);

        public void Read(NativeArray<int> value) => _inner.Read(value);
        public void Read(NativeArray<int2> value) => _inner.Read(value);
        public void Read(NativeArray<ushort> value) => _inner.Read(value);
        public void Read(NativeArray<byte> value) => _inner.Read(value);
        public void Read(NativeArray<float4> value) => _inner.Read(value);
        public void Read(NativeArray<float2> value) => _inner.Read(value);
        public void Read(NativeArray<byte> value, int stride) => _inner.Read(value, stride);
        public void Read(NativeList<int> value) => _inner.Read(value);
        public void Read(out Bezier4x3 curve) => _inner.Read(out curve);
        public void Read(out string value) => _inner.Read(out value);
        public void Read(out Color value) => _inner.Read(out value);
        public void Read(out Color32 value) => _inner.Read(out value);
        public void Read(out quaternion value) => _inner.Read(out value);
        public void Read(out float4 value) => _inner.Read(out value);
        public void Read(out float3 value) => _inner.Read(out value);
        public void Read(out float2 value) => _inner.Read(out value);
        public void Read(out int4 value) => _inner.Read(out value);
        public void Read(out int3 value) => _inner.Read(out value);
        public void Read(out int2 value) => _inner.Read(out value);
        public void Read(out bool4 value) => _inner.Read(out value);
        public void Read(out bool3 value) => _inner.Read(out value);
        public void Read(out bool2 value) => _inner.Read(out value);
        public void Read(out uint4 value) => _inner.Read(out value);
        public void Read(out Hash128 value) => _inner.Read(out value);
        public void Read(out char value) => _inner.Read(out value);
        public void Read(out float value) => _inner.Read(out value);
        public void Read(out double value) => _inner.Read(out value);
        public void Read(out int value) => _inner.Read(out value);
        public void Read(out uint value) => _inner.Read(out value);
        public void Read(out short value) => _inner.Read(out value);
        public void Read(out ushort value) => _inner.Read(out value);
        public void Read(out sbyte value) => _inner.Read(out value);
        public void Read(out byte value) => _inner.Read(out value);
        public void Read(out long value) => _inner.Read(out value);
        public void Read(out ulong value) => _inner.Read(out value);
        public void Read(out bool value) => _inner.Read(out value);
        public void Skip(int size) => _inner.Skip(size);
    }
}
