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
    /// <summary>
    /// A writer that writes nothing and collects the entity references a value serializes (its own <c>Serialize</c>
    /// names them, so no per-type knowledge is needed): the applier checks the references of a value it is about to keep
    /// (<see cref="NetReader.KeptValueIsLive{T}"/>).
    /// </summary>
    public struct EntityRefCollector : IWriter
    {
        private readonly List<Entity> _refs;
        private readonly Context _context;

        public EntityRefCollector(List<Entity> refs, Context context)
        {
            _refs = refs;
            _context = context;
        }

        public Context context => _context;

        public void Initialize(Context context, NativeList<byte> buffer, NativeArray<Entity> entityTable)
        {
        }

        public WriterBlock Begin() => default(WriterBlock);

        public bool End(WriterBlock block) => true;

        public bool End(WriterBlock block, out int size)
        {
            size = 0;
            return true;
        }

        public void Write(Entity value)
        {
            if (value != Entity.Null) _refs.Add(value);
        }

        public void Write(Entity value, bool ignoreVersion) => Write(value);

        public void Write(NativeArray<Entity> value)
        {
            for (int i = 0; i < value.Length; i++) Write(value[i]);
        }

        public void Write(NativeList<Entity> value)
        {
            for (int i = 0; i < value.Length; i++) Write(value[i]);
        }

        public void Write<TSerializable>(NativeArray<TSerializable> value) where TSerializable : struct, ISerializable
        {
            for (int i = 0; i < value.Length; i++) value[i].Serialize(this);
        }

        public void Write<TSerializable>(TSerializable value) where TSerializable : ISerializable => value.Serialize(this);

        public void Write(NativeList<int> value)
        {
        }

        public void Write(NativeArray<int> value)
        {
        }

        public void Write(NativeArray<int2> value)
        {
        }

        public void Write(NativeArray<ushort> value)
        {
        }

        public void Write(NativeArray<byte> value)
        {
        }

        public void Write(NativeArray<float4> value)
        {
        }

        public void Write(NativeArray<float2> value)
        {
        }

        public void Write(NativeArray<byte> value, int stride)
        {
        }

        public void Write(Bezier4x3 curve)
        {
        }

        public void Write(string value)
        {
        }

        public void Write(Color value)
        {
        }

        public void Write(Color32 value)
        {
        }

        public void Write(quaternion value)
        {
        }

        public void Write(float4 value)
        {
        }

        public void Write(float3 value)
        {
        }

        public void Write(float2 value)
        {
        }

        public void Write(int4 value)
        {
        }

        public void Write(int3 value)
        {
        }

        public void Write(int2 value)
        {
        }

        public void Write(bool4 value)
        {
        }

        public void Write(bool3 value)
        {
        }

        public void Write(bool2 value)
        {
        }

        public void Write(uint4 value)
        {
        }

        public void Write(Hash128 hash)
        {
        }

        public void Write(char value)
        {
        }

        public void Write(float value)
        {
        }

        public void Write(double value)
        {
        }

        public void Write(int value)
        {
        }

        public void Write(uint value)
        {
        }

        public void Write(short value)
        {
        }

        public void Write(ushort value)
        {
        }

        public void Write(sbyte value)
        {
        }

        public void Write(byte value)
        {
        }

        public void Write(long value)
        {
        }

        public void Write(ulong value)
        {
        }

        public void Write(bool value)
        {
        }
    }
}
