using System;
using System.Collections.Generic;
using SkylineTogether.Core;
using SkylineTogether.Core.Modding;
using SkylineTogether.Sync;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// Finds mod state that changed without an update tag (docs/design/MOD-COMPATIBILITY.md): the capture only sees
    /// entities tagged <c>Created</c>/<c>Updated</c>/<c>Deleted</c>, but mods also write their own components directly
    /// (Traffic edits its lane connection data in place, Better Bulldozer adds its removal buffer to the owner, Plop the
    /// Growables' level lock and Anarchy's info-panel toggles add or remove components, Water Features edits water
    /// sources). Watched are every serializable component of another mod plus the vanilla types adapters name.
    /// <para>
    /// Per capture, a fast pass reads the chunk change versions of the watched types (<c>ArchetypeChunk.DidChange</c>
    /// against the version of the previous pass; enabled-bit changes bump them too) and hashes only the entities of
    /// changed chunks; a slow sweep re-hashes 256 known entities per frame to catch removals (an entity that lost its
    /// last watched type leaves the query). The hash covers the watched types only (presence, values and, on the host,
    /// enabled bits), except for water sources and mod entities, whose whole replicated state is hashed. A change is
    /// reported with whether the set of watched types changed (the caller sends an upsert) or only values (values).
    /// Changes of ordinary entities are reported at most once a second each (simulation-driven mod values such as a
    /// lake's height), mod data entities without delay (Traffic's data must arrive with its node). The bookkeeping (known
    /// states, the limit, the sweep, since when each change may have happened) is Core's <see cref="ModStateLedger"/>; a
    /// reported change is consumed whether or not the caller sends it.
    /// </para>
    /// Main thread only; the caller catches exceptions.
    /// </summary>
    public sealed class ModStateWatcher
    {
        private const int SweepPerFrame = 256;

        /// <summary>One detected change.</summary>
        public struct Change
        {
            /// <summary>0 when the entity is not bound yet (the host binds and spawns it if its family replicates).</summary>
            public uint NetId;
            public Entity Entity;
            /// <summary>The set of watched types changed (or the entity was not known): send an upsert, not values.</summary>
            public bool PresenceChanged;
            /// <summary>The earliest time the change may have happened (<see cref="ModStateLedger.Change.Since"/>).</summary>
            public double Since;
        }

        private readonly EntityCodec _codec;
        private readonly NetIdSystem _ids;
        private readonly ILog _log;
        private readonly HashSet<int> _watched = new HashSet<int>();
        private readonly List<ComponentType> _types = new List<ComponentType>();
        private readonly ModStateLedger _ledger = new ModStateLedger();
        private readonly List<(uint Id, double Since)> _retries = new List<(uint, double)>();
        private DynamicComponentTypeHandle[] _handles;
        private readonly List<uint> _sweep = new List<uint>();
        private readonly Func<TypeIndex, bool> _isWatched;
        private EntityQuery _query;
        private bool _hasQuery;
        private uint _lastVersion;
        private bool _bits;

        public ModStateWatcher(EntityCodec codec, NetIdSystem ids, ILog log)
        {
            _codec = codec;
            _ids = ids;
            _log = log;
            _isWatched = t => _watched.Contains(t.Value);
        }

        /// <summary>True while watching (a session runs and at least one type is watched).</summary>
        public bool Active { get; private set; }

        /// <summary>Number of entities with a known watched state.</summary>
        public int Known => _ledger.Known;

        /// <summary>
        /// Starts watching and takes the baseline (host: at session start after binding the city; client: when it went
        /// live). <paramref name="enabledBits"/>: include enabled bits in the hash (host only: a client's local mod
        /// systems may flip bits whose host value arrives anyway).
        /// </summary>
        public void Start(bool enabledBits, double now)
        {
            Stop();
            _ledger.Reset(now);
            _bits = enabledBits;
            BuildTypes();
            if (_types.Count == 0) return;
            EntityManager em = _codec.EntityManager;
            _query = em.CreateEntityQuery(new EntityQueryDesc
            {
                Any = _types.ToArray(),
                None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Prefabs.PrefabData>() },
            });
            _hasQuery = true;
            int n = 0;
            NativeArray<Entity> entities = _query.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in entities)
                {
                    uint id = _ids.GetNetId(e);
                    if (id == 0) continue;
                    Hash(e, id, out ulong p, out ulong v);
                    _ledger.Set(id, p, v);
                    n++;
                }
            }
            finally
            {
                entities.Dispose();
            }
            _lastVersion = em.GlobalSystemVersion;
            // active only with a complete baseline: one that threw half-way reported every entity it had not hashed yet as
            // changed (a burst of proposals)
            Active = true;
            _log.Info(LogCategory.Compat, "mod state watcher: " + _types.Count + " watched types, baseline of " + n + " entities");
        }

        public void Stop()
        {
            Active = false;
            if (_hasQuery)
            {
                try
                {
                    _query.Dispose();
                }
                catch (Exception)
                {
                    // the world may be gone
                }
                _hasQuery = false;
            }
            _ledger.Reset(0);
            _retries.Clear();
            _sweep.Clear();
        }

        private void BuildTypes()
        {
            _watched.Clear();
            _types.Clear();
            int count = TypeManager.GetTypeCount();
            for (int i = 1; i < count; i++)
            {
                Type type;
                try
                {
                    type = TypeManager.GetType(i);
                }
                catch (Exception)
                {
                    continue;
                }
                if (type == null || type.Assembly == typeof(Game.Common.Owner).Assembly || type.Assembly == typeof(ModStateWatcher).Assembly) continue;
                TypeIndex index;
                try
                {
                    index = TypeManager.GetTypeIndex(type);
                }
                catch (Exception)
                {
                    continue;
                }
                TypeHandler h = TypeHandlers.Get(index);
                if (h == null || !h.FromMod || ModCompat.IsLocalType(index)) continue;
                if (_watched.Add(index.Value)) _types.Add(ComponentType.ReadOnly(index));
            }
            foreach (int index in ModCompat.WatchedVanillaTypes)
            {
                if (_watched.Add(index)) _types.Add(ComponentType.ReadOnly(new TypeIndex { Value = index }));
            }
        }

        /// <summary>Records an entity's current state as known (after the applier wrote it or the capture sent it).</summary>
        public void Refresh(Entity e)
        {
            if (!Active) return;
            uint id = _ids.GetNetId(e);
            if (id == 0 || !_codec.EntityManager.Exists(e)) return;
            Hash(e, id, out ulong p, out ulong v);
            _ledger.Set(id, p, v);
        }

        public void Forget(uint id) => _ledger.Forget(id);

        /// <summary>The changes since the previous call.</summary>
        public void Collect(double now, List<Change> output)
        {
            if (!Active) return;
            EntityManager em = _codec.EntityManager;
            _query.CompleteDependency();
            uint since = _lastVersion;
            double changedSince = _ledger.BeginCollect(now);
            // the handle array is kept (the watched types are fixed between Start calls); handles are fresh
            if (_handles == null || _handles.Length != _types.Count) _handles = new DynamicComponentTypeHandle[_types.Count];
            DynamicComponentTypeHandle[] handles = _handles;
            for (int i = 0; i < handles.Length; i++) handles[i] = em.GetDynamicComponentTypeHandle(_types[i]);
            EntityTypeHandle entityHandle = em.GetEntityTypeHandle();
            NativeArray<ArchetypeChunk> chunks = _query.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                foreach (ArchetypeChunk chunk in chunks)
                {
                    bool changed = false;
                    for (int i = 0; i < handles.Length && !changed; i++)
                    {
                        if (chunk.Has(ref handles[i]) && chunk.DidChange(ref handles[i], since)) changed = true;
                    }
                    if (!changed) continue;
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityHandle);
                    for (int k = 0; k < entities.Length; k++) Check(entities[k], changedSince, now, output);
                }
            }
            finally
            {
                chunks.Dispose();
            }
            _lastVersion = em.GlobalSystemVersion;

            _retries.Clear();
            _ledger.TakeRetries(now, _retries);
            foreach ((uint id, double heldSince) in _retries)
            {
                Entity e = _ids.GetEntity(id);
                if (e != Entity.Null && em.Exists(e) && !em.HasComponent<Game.Common.Deleted>(e)) Check(e, heldSince, now, output);
            }

            // slow sweep (removals of the last watched type, anything the change versions missed)
            _sweep.Clear();
            _ledger.NextSweep(SweepPerFrame, now, _sweep, out double sweepSince);
            foreach (uint id in _sweep)
            {
                Entity e = _ids.GetEntity(id);
                if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e))
                {
                    Forget(id);
                    continue;
                }
                Check(e, sweepSince, now, output);
            }
        }

        private void Check(Entity e, double since, double now, List<Change> output)
        {
            uint id = _ids.GetNetId(e);
            if (id == 0)
            {
                output.Add(new Change { NetId = 0, Entity = e, PresenceChanged = true, Since = since });
                return;
            }
            Hash(e, id, out ulong p, out ulong v);
            if (!_ledger.Observe(id, p, v, _ids.GetFamily(id) == Family.ModEntity, since, now, out ModStateLedger.Change c)) return;
            output.Add(new Change { NetId = id, Entity = e, PresenceChanged = c.PresenceChanged, Since = c.Since });
        }

        private void Hash(Entity e, uint id, out ulong presence, out ulong value)
        {
            Family family = _ids.GetFamily(id);
            bool whole = family == Family.ModEntity || family == Family.WaterSource;
            _codec.HashSubset(e, whole ? null : _isWatched, _bits, out presence, out value);
        }
    }
}
