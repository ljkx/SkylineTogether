using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Hash128 = Colossal.Hash128;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// A portable reference to a derived entity (a lane or a prefab sub-object such as a station stop): its tracked
    /// owner, its prefab, its index among the owner's children and its position quantised to 1/64 m. Derived entities
    /// are regenerated on every peer, so they are found again by key instead of by id (ARCH §3.7).
    /// </summary>
    public struct DerivedKey
    {
        public const byte LaneKind = 0;
        public const byte SubObjectKind = 1;

        public byte Kind;
        public uint OwnerNetId;
        public uint PrefabNetId;
        public int Index;
        public int X, Y, Z;
    }

    /// <summary>Per-write state shared by every copy of a <see cref="NetWriter"/> (writers are passed by value).</summary>
    public sealed class NetWriterState
    {
        /// <summary>A non-null entity reference had neither a net id nor a derived key: the value must not be sent.</summary>
        public bool Unresolved;
        public NativeArray<Entity> Table;
        /// <summary>Builds a derived key for an unbound entity; null disables keys.</summary>
        public KeyBuilder Keys;
        /// <summary>
        /// w2/simvals: unbound references this predicate accepts are written as null instead of making the value
        /// unresolved (simulation values that point at service requests, <c>EntityCodec.SimValues</c>); null = off.
        /// </summary>
        public System.Func<Entity, bool> NullRefs;
        /// <summary>Derived keys referenced by the component being written, in order of first use.</summary>
        public readonly System.Collections.Generic.List<DerivedKey> UsedKeys = new System.Collections.Generic.List<DerivedKey>();
        private readonly System.Collections.Generic.Dictionary<Entity, int> _keyIndex = new System.Collections.Generic.Dictionary<Entity, int>();

        // mod compatibility: dependency discovery (docs/design/MOD-COMPATIBILITY.md). A component can reference a mod's
        // data entity that is not bound yet (Traffic's lane connection data, Better Bulldozer's removal records); the
        // codec binds and sends such entities with the referencing one instead of dropping the value.
        /// <summary>Non-null entity references of the component that had neither a net id nor a derived key.</summary>
        public readonly System.Collections.Generic.List<Entity> UnresolvedEntities = new System.Collections.Generic.List<Entity>();

        public delegate bool KeyBuilder(Entity entity, out DerivedKey key);

        public void BeginComponent()
        {
            Unresolved = false;
            UsedKeys.Clear();
            _keyIndex.Clear();
            UnresolvedEntities.Clear();
        }

        /// <summary>Index of the entity's derived key in <see cref="UsedKeys"/>, or -1.</summary>
        public int KeyFor(Entity entity)
        {
            if (_keyIndex.TryGetValue(entity, out int index)) return index;
            if (Keys == null || !Keys(entity, out DerivedKey key)) return -1;
            index = UsedKeys.Count;
            UsedKeys.Add(key);
            _keyIndex[entity] = index;
            return index;
        }
    }

    /// <summary>
    /// The game's <see cref="BinaryWriter"/> with one addition: it notices references to entities that have no net id.
    /// Everything else is the save format byte for byte, so each component's own <c>Serialize</c> defines the wire
    /// format. Entity references are written as net ids through the table (index = <c>Entity.Index</c>, value =
    /// <c>Entity(netId, version)</c>).
    /// </summary>
    public struct NetWriter : IWriter
    {
        private BinaryWriter _inner;
        private NetWriterState _state;

        public NetWriter(BinaryWriter inner, NetWriterState state)
        {
            _inner = inner;
            _state = state;
        }

        public Context context => _inner.context;

        public void Initialize(Context context, NativeList<byte> buffer, NativeArray<Entity> entityTable) => _inner.Initialize(context, buffer, entityTable);

        public WriterBlock Begin() => _inner.Begin();

        public bool End(WriterBlock block) => _inner.End(block);

        public bool End(WriterBlock block, out int size) => _inner.End(block, out size);

        public void Write(Entity value)
        {
            if (!TryWriteKey(value, false)) _inner.Write(value);
        }

        public void Write(Entity value, bool ignoreVersion)
        {
            if (!TryWriteKey(value, ignoreVersion)) _inner.Write(value, ignoreVersion);
        }

        /// <summary>Bound and null entities go through the table; an unbound one becomes a derived key (written as
        /// <c>-2 - keyIndex</c>) or marks the value unresolved.</summary>
        private bool TryWriteKey(Entity value, bool ignoreVersion)
        {
            if (value == Entity.Null) return false;
            NativeArray<Entity> table = _state.Table;
            if (value.Index >= 0 && value.Index < table.Length)
            {
                Entity e = table[value.Index];
                if (e.Index > 0 && (ignoreVersion || e.Version == value.Version)) return false;
            }
            int key = _state.KeyFor(value);
            if (key < 0)
            {
                if (_state.NullRefs != null && _state.NullRefs(value))
                {
                    _inner.Write(-1); // reads back as Entity.Null (NetReader, BinaryReader)
                    return true;
                }
                _state.Unresolved = true;
                _state.UnresolvedEntities.Add(value); // mod compatibility: candidates for dependency binding
                return false;
            }
            _inner.Write(-2 - key);
            return true;
        }

        public void Write(NativeArray<Entity> value)
        {
            for (int i = 0; i < value.Length; i++) Write(value[i]);
        }

        public void Write(NativeList<Entity> value)
        {
            _inner.Write(value.Length);
            for (int i = 0; i < value.Length; i++) Write(value[i]);
        }

        public void Write<TSerializable>(NativeArray<TSerializable> value) where TSerializable : struct, ISerializable
        {
            for (int i = 0; i < value.Length; i++) Write(value[i]);
        }

        public void Write<TSerializable>(TSerializable value) where TSerializable : ISerializable => value.Serialize(this);

        public void Write(NativeList<int> value) => _inner.Write(value);
        public void Write(NativeArray<int> value) => _inner.Write(value);
        public void Write(NativeArray<int2> value) => _inner.Write(value);
        public void Write(NativeArray<ushort> value) => _inner.Write(value);
        public void Write(NativeArray<byte> value) => _inner.Write(value);
        public void Write(NativeArray<float4> value) => _inner.Write(value);
        public void Write(NativeArray<float2> value) => _inner.Write(value);
        public void Write(NativeArray<byte> value, int stride) => _inner.Write(value, stride);
        public void Write(Bezier4x3 curve) => _inner.Write(curve);
        public void Write(string value) => _inner.Write(value);
        public void Write(Color value) => _inner.Write(value);
        public void Write(Color32 value) => _inner.Write(value);
        public void Write(quaternion value) => _inner.Write(value);
        public void Write(float4 value) => _inner.Write(value);
        public void Write(float3 value) => _inner.Write(value);
        public void Write(float2 value) => _inner.Write(value);
        public void Write(int4 value) => _inner.Write(value);
        public void Write(int3 value) => _inner.Write(value);
        public void Write(int2 value) => _inner.Write(value);
        public void Write(bool4 value) => _inner.Write(value);
        public void Write(bool3 value) => _inner.Write(value);
        public void Write(bool2 value) => _inner.Write(value);
        public void Write(uint4 value) => _inner.Write(value);
        public void Write(Hash128 hash) => _inner.Write(hash);
        public void Write(char value) => _inner.Write(value);
        public void Write(float value) => _inner.Write(value);
        public void Write(double value) => _inner.Write(value);
        public void Write(int value) => _inner.Write(value);
        public void Write(uint value) => _inner.Write(value);
        public void Write(short value) => _inner.Write(value);
        public void Write(ushort value) => _inner.Write(value);
        public void Write(sbyte value) => _inner.Write(value);
        public void Write(byte value) => _inner.Write(value);
        public void Write(long value) => _inner.Write(value);
        public void Write(ulong value) => _inner.Write(value);
        public void Write(bool value) => _inner.Write(value);
    }
}
