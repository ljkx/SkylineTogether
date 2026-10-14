using System;
using System.Collections.Generic;
using Colossal.Serialization.Entities;
using SkylineTogether.Core;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Sync;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    // Reading single components of a decoded record without applying it (host validation of proposals).
    public sealed partial class EntityCodec
    {
        private const int MaxPeekElements = 1 << 16;

        /// <summary>Offset of the batch loaded by <see cref="BeginPeek"/> in its buffer (-1: none loaded).</summary>
        private int _peekOffset = -1;

        /// <summary>
        /// Host validation (ARCHITECTURE.md#host-validation-and-commit): loads a batch so the components of its decoded
        /// records can be read with <see cref="TryPeek{T}"/>, <see cref="TryPeekBuffer{T}"/>, <see cref="TryPeekRefs{T}"/>
        /// and <see cref="PeekPrefab"/> before (or instead of) applying it. Valid until the next apply or peek.
        /// </summary>
        public void BeginPeek(byte[] data, int offset, int count)
        {
            ResolveHandlers();
            LoadBatch(data, offset, count);
            _peekOffset = offset;
        }

        /// <summary>The record's prefab on this machine (Entity.Null when it names none or an unknown one).</summary>
        public Entity PeekPrefab(SyncRecord r)
        {
            if (_peekOffset < 0 || r == null) return Entity.Null;
            try
            {
                return ReadPrefab(r, _peekOffset);
            }
            catch (Exception)
            {
                return Entity.Null; // malformed: the apply reports it
            }
        }

        /// <summary>The record's value of the data component <typeparamref name="T"/>, decoded without writing it anywhere.</summary>
        public bool TryPeek<T>(SyncRecord r, out T value) where T : unmanaged, IComponentData, ISerializable
        {
            value = default(T);
            TypeHandler h = TypeHandlers.Get<T>();
            if (h == null || h.Kind != SyncComponentKind.Data || !Find(r, h, out SyncComponent c) || !BeginComponent(c, out NetReader reader)) return false;
            try
            {
                value.Deserialize(reader);
                return true;
            }
            catch (Exception)
            {
                value = default(T);
                return false;
            }
        }

        /// <summary>The record's elements of the buffer <typeparamref name="T"/> (cleared first), decoded without writing them.</summary>
        public bool TryPeekBuffer<T>(SyncRecord r, List<T> into) where T : unmanaged, IBufferElementData, ISerializable
        {
            into.Clear();
            TypeHandler h = TypeHandlers.Get<T>();
            if (h == null || h.Kind != SyncComponentKind.Buffer || !Find(r, h, out SyncComponent c) || !BeginComponent(c, out NetReader reader)) return false;
            try
            {
                reader.Read(out int count);
                if (count < 0 || count > MaxPeekElements || count > reader.Remaining) return false;
                for (int i = 0; i < count; i++)
                {
                    T element = default(T);
                    element.Deserialize(reader);
                    into.Add(element);
                }
                return true;
            }
            catch (Exception)
            {
                into.Clear();
                return false;
            }
        }

        /// <summary>
        /// The entity references in the record's value of <typeparamref name="T"/> as they were written (net ids, -1 for
        /// null, -2 - k for derived key k), in order, whether they resolve here or not: a new edge names nodes that the
        /// same proposal creates.
        /// </summary>
        public bool TryPeekRefs<T>(SyncRecord r, List<int> into) where T : unmanaged, IComponentData, ISerializable
        {
            into.Clear();
            TypeHandler h = TypeHandlers.Get<T>();
            if (h == null || h.Kind != SyncComponentKind.Data || !Find(r, h, out SyncComponent c) || !BeginComponent(c, out NetReader reader)) return false;
            _rs.RawRefs = into;
            try
            {
                T value = default(T);
                value.Deserialize(reader);
                return true;
            }
            catch (Exception)
            {
                into.Clear();
                return false;
            }
            finally
            {
                _rs.RawRefs = null;
            }
        }

        private static bool Find(SyncRecord r, TypeHandler h, out SyncComponent component)
        {
            component = default(SyncComponent);
            if (r == null) return false;
            for (int i = 0; i < r.Components.Count; i++)
            {
                SyncComponent c = r.Components[i];
                if (c.TypeHash != h.Hash || c.Kind != h.Kind) continue;
                component = c;
                return true;
            }
            return false;
        }

        /// <summary>Positions the reader on a component's payload (its derived keys resolved), bounded to the payload.</summary>
        private bool BeginComponent(in SyncComponent c, out NetReader reader)
        {
            reader = default(NetReader);
            if (_peekOffset < 0) return false;
            try
            {
                ByteReader keys = _keyReader;
                keys.Reset(c.Data, c.Offset, c.Count);
                uint keyCount = keys.ReadVarUInt32();
                if (keyCount > 4096) return false;
                _rs.Keys.Clear();
                for (uint k = 0; k < keyCount; k++)
                {
                    var key = new DerivedKey
                    {
                        Kind = keys.ReadByte(),
                        OwnerNetId = keys.ReadVarUInt32(),
                        PrefabNetId = keys.ReadVarUInt32(),
                        Index = keys.ReadVarInt32(),
                        X = keys.ReadVarInt32(),
                        Y = keys.ReadVarInt32(),
                        Z = keys.ReadVarInt32(),
                    };
                    _rs.Keys.Add(ResolveKey(key));
                }
                int start = c.Offset - _peekOffset + keys.Position;
                int end = c.Offset - _peekOffset + c.Count;
                if (start < 0 || end < start || end > _batch.Length - NetReaderState.Padding) return false;
                _position.Value = start;
                _rs.Bound(_position, end);
                _rs.Table = _ids.ReaderTable;
                _rs.Unresolved = false;
                _rs.CheckLiveness = false; // a peek resolves nothing it keeps
                _rs.KeepOnUnresolved = true;
                _rs.Collect = null;
                var inner = default(BinaryReader);
                inner.Initialize(_readContext, _batch, _position, _rs.Table);
                reader = new NetReader(inner, _rs);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
