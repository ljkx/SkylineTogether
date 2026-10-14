using System.Collections.Generic;
using Colossal.Mathematics;
using SkylineTogether.Core;
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
        /// When set, a reference whose bound entity no longer exists or carries <c>Deleted</c> resolves to
        /// <c>Entity.Null</c> and counts as unresolved. A stale binding (an entity the local game destroyed, a despawn
        /// applied by an earlier batch of the same frame) otherwise became a component or buffer reference to a destroyed
        /// entity, which the game's Burst jobs dereference without a check (a native crash, not an exception).
        /// </summary>
        public bool CheckLiveness;

        /// <summary>The entity manager the liveness check uses (valid while <see cref="CheckLiveness"/>).</summary>
        public EntityManager EntityManager;

        /// <summary>References dropped because their entity was destroyed or deleted (diagnostics).</summary>
        public long DeadReferences;

        /// <summary>See <see cref="NetReader.KeepOnUnresolved"/>.</summary>
        public bool KeepOnUnresolved;

        /// <summary>When not null, every resolved reference is added (spawn validation follows references).</summary>
        public List<Entity> Collect;

        /// <summary>
        /// When not null, every reference is added as it was written (a net id, -1 for null, -2 - k for derived key k),
        /// resolved or not: the host's placement checks name a new edge's nodes before they exist (<c>EntityCodec.TryPeekRefs</c>).
        /// </summary>
        public List<int> RawRefs;

        /// <summary>
        /// Zero bytes every read buffer carries beyond its last valid byte. A primitive read checks the position
        /// before it reads (<see cref="Limit"/>); the longest read the game's reader can still make past the limit (a 4-byte
        /// UTF-8 char after a 1-byte check) stays inside the allocation.
        /// </summary>
        public const int Padding = 16;

        /// <summary>
        /// The read position (shared with the game's reader) and the end of the bytes this read may consume
        /// (the component's payload, the blob). The game's <c>BinaryReader</c> indexes its <c>NativeArray</c> without a
        /// bounds check in a player build and copies array reads with an unchecked <c>MemCpy</c>, so a truncated or
        /// hostile payload read past its end reads (or, through a wire count, allocates) arbitrary memory. Every read of
        /// <see cref="NetReader"/> checks against this limit first and throws a <see cref="DecodeException"/> instead.
        /// A negative limit turns the check off.
        /// </summary>
        public NativeReference<int> Position;

        /// <summary>See <see cref="Position"/>.</summary>
        public int Limit = -1;

        /// <summary>Reads refused because they would have run past the payload (diagnostics).</summary>
        public long Overruns;

        /// <summary>Binds the bounds check to a read of the bytes before <paramref name="limit"/>.</summary>
        public void Bound(NativeReference<int> position, int limit)
        {
            Position = position;
            Limit = limit;
        }

        /// <summary>The bytes left before <see cref="Limit"/> (int.MaxValue when unbounded).</summary>
        public int Remaining => Limit < 0 || !Position.IsCreated ? int.MaxValue : Limit - Position.Value;

        /// <summary>Throws unless <paramref name="bytes"/> more bytes may be read.</summary>
        public void Need(long bytes)
        {
            if (Limit < 0 || !Position.IsCreated) return;
            int p = Position.Value;
            if (p < 0 || bytes < 0 || p > Limit || bytes > Limit - p)
            {
                Overruns++;
                throw new DecodeException("read of " + bytes + " byte(s) at " + p + " runs past the payload end " + Limit);
            }
        }

        /// <summary>Throws when a variable-length read ran past the limit (it stayed inside the padding).</summary>
        public void CheckAfter()
        {
            if (Limit < 0 || !Position.IsCreated) return;
            if (Position.Value > Limit)
            {
                Overruns++;
                throw new DecodeException("read ran past the payload end " + Limit);
            }
        }

        /// <summary>
        /// Entities the batch being applied despawns. They still exist while its records are read, but a
        /// reference to one would dangle a moment later, so it resolves to nothing like a deleted entity.
        /// </summary>
        public readonly HashSet<Entity> Dying = new HashSet<Entity>();

        /// <summary>True when the entity may be referenced (exists and is not being deleted).</summary>
        public bool IsLive(Entity e)
        {
            if (!CheckLiveness || e == Entity.Null) return true;
            if (Dying.Count > 0 && Dying.Contains(e)) return false;
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

        public ReaderBlock Begin()
        {
            return Begin(out _);
        }

        public ReaderBlock Begin(out int size)
        {
            Need(4);
            ReaderBlock block = _inner.Begin(out size);
            // a block that ends outside the payload is malformed (End would move the position there)
            if (size < 0 || size > Remaining) Fail("block of " + size + " byte(s)");
            return block;
        }

        public bool End(ReaderBlock block) => _inner.End(block);

        /// <summary>The bounds check (see <see cref="NetReaderState.Position"/>); a no-op without state.</summary>
        private void Need(long bytes) => _state?.Need(bytes);

        /// <summary>The bytes this read may still consume (int.MaxValue when unbounded).</summary>
        public int Remaining => _state?.Remaining ?? int.MaxValue;

        private void Fail(string what)
        {
            if (_state != null) _state.Overruns++;
            throw new DecodeException(what + " overruns the payload");
        }

        public void Read(out Entity value)
        {
            Need(4);
            _inner.Read(out int index);
            _state.RawRefs?.Add(index);
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

        /// <summary>True once a reference of the current component could not be resolved (unknown, destroyed or deleted).</summary>
        public bool Unresolved => _state != null && _state.Unresolved;

        /// <summary>Clears the unresolved flag and returns its previous value (per-element bookkeeping of buffers).</summary>
        public bool TakeUnresolved()
        {
            if (_state == null) return false;
            bool was = _state.Unresolved;
            _state.Unresolved = false;
            return was;
        }

        /// <summary>Sets the unresolved flag again (restores what <see cref="TakeUnresolved"/> took).</summary>
        public void MarkUnresolved()
        {
            if (_state != null) _state.Unresolved = true;
        }

        /// <summary>
        /// True when a component whose references did not all resolve must keep the entity's current value
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
            Need(4);
            _inner.Read(out int count);
            // the count comes from the wire; each reference is 4 bytes
            if (count < 0 || count > Remaining / 4) Fail("list of " + count + " references");
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

        // array reads are unchecked MemCpy calls in the game's reader: their size is checked first
        public void Read(NativeArray<int> value)
        {
            Need((long)value.Length * 4);
            _inner.Read(value);
        }

        public void Read(NativeArray<int2> value)
        {
            Need((long)value.Length * 8);
            _inner.Read(value);
        }

        public void Read(NativeArray<ushort> value)
        {
            Need((long)value.Length * 2);
            _inner.Read(value);
        }

        public void Read(NativeArray<byte> value)
        {
            Need(value.Length);
            _inner.Read(value);
        }

        public void Read(NativeArray<float4> value)
        {
            Need((long)value.Length * 16);
            _inner.Read(value);
        }

        public void Read(NativeArray<float2> value)
        {
            Need((long)value.Length * 8);
            _inner.Read(value);
        }

        public void Read(NativeArray<byte> value, int stride)
        {
            Need(value.Length);
            if (stride <= 0) Fail("stride " + stride);
            _inner.Read(value, stride);
        }

        public void Read(NativeList<int> value)
        {
            Need(4);
            _inner.Read(out int count);
            if (count < 0 || count > Remaining / 4) Fail("list of " + count + " ints");
            if (value.Length != count) value.ResizeUninitialized(count);
            for (int i = 0; i < count; i++)
            {
                _inner.Read(out int v);
                value[i] = v;
            }
        }

        public void Read(out string value)
        {
            Need(4);
            _inner.Read(out int count);
            // a char is 1 to 4 UTF-8 bytes; the game's reader allocates the wire count unchecked
            if (count < 0 || count > Remaining) Fail("string of " + count + " chars");
            var chars = new char[count];
            for (int i = 0; i < count; i++)
            {
                Need(1);
                _inner.Read(out chars[i]);
            }
            _state?.CheckAfter();
            value = new string(chars);
        }

        public void Read(out char value)
        {
            Need(1);
            _inner.Read(out value);
            _state?.CheckAfter();
        }

        public void Read(out Bezier4x3 curve)
        {
            Need(48);
            _inner.Read(out curve);
        }

        public void Read(out Color value)
        {
            Need(16);
            _inner.Read(out value);
        }

        public void Read(out Color32 value)
        {
            Need(4);
            _inner.Read(out value);
        }

        public void Read(out quaternion value)
        {
            Need(16);
            _inner.Read(out value);
        }

        public void Read(out float4 value)
        {
            Need(16);
            _inner.Read(out value);
        }

        public void Read(out float3 value)
        {
            Need(12);
            _inner.Read(out value);
        }

        public void Read(out float2 value)
        {
            Need(8);
            _inner.Read(out value);
        }

        public void Read(out int4 value)
        {
            Need(16);
            _inner.Read(out value);
        }

        public void Read(out int3 value)
        {
            Need(12);
            _inner.Read(out value);
        }

        public void Read(out int2 value)
        {
            Need(8);
            _inner.Read(out value);
        }

        public void Read(out bool4 value)
        {
            Need(1);
            _inner.Read(out value);
        }

        public void Read(out bool3 value)
        {
            Need(1);
            _inner.Read(out value);
        }

        public void Read(out bool2 value)
        {
            Need(1);
            _inner.Read(out value);
        }

        public void Read(out uint4 value)
        {
            Need(16);
            _inner.Read(out value);
        }

        public void Read(out Hash128 value)
        {
            Need(16);
            _inner.Read(out value);
        }

        public void Read(out float value)
        {
            Need(4);
            _inner.Read(out value);
        }

        public void Read(out double value)
        {
            Need(8);
            _inner.Read(out value);
        }

        public void Read(out int value)
        {
            Need(4);
            _inner.Read(out value);
        }

        public void Read(out uint value)
        {
            Need(4);
            _inner.Read(out value);
        }

        public void Read(out short value)
        {
            Need(2);
            _inner.Read(out value);
        }

        public void Read(out ushort value)
        {
            Need(2);
            _inner.Read(out value);
        }

        public void Read(out sbyte value)
        {
            Need(1);
            _inner.Read(out value);
        }

        public void Read(out byte value)
        {
            Need(1);
            _inner.Read(out value);
        }

        public void Read(out long value)
        {
            Need(8);
            _inner.Read(out value);
        }

        public void Read(out ulong value)
        {
            Need(8);
            _inner.Read(out value);
        }

        public void Read(out bool value)
        {
            Need(1);
            _inner.Read(out value);
        }

        public void Skip(int size) => _inner.Skip(size);
    }
}
