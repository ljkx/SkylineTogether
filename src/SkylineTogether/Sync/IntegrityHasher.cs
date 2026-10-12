using System;
using System.Collections.Generic;
using SkylineTogether.Core;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Sync;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// The integrity hash of one bound entity: what the rolling verification compares between host and client
    /// (ARCH §8.1, playable subset). It must cover only what replication actually keeps equal, or every cycle would
    /// "repair" values that were never meant to travel. The rules, decided per family:
    /// <list type="bullet">
    /// <item><b>Line families</b> (route, waypoint, segment): every replicated component, its value or its presence, as
    /// the codec sends it. These families are value-scanned by the host (changes without update tags are streamed), so
    /// the client follows every value. Not verified: <c>HiddenRoute</c> (per-player visibility).</item>
    /// <item><b>Every other replicated family</b> (nets, objects, areas, aggregates, water sources, mod entities): only
    /// the structural source values listed in <see cref="StructuralTypeNames"/> (prefab, owner, transform, attachment,
    /// net geometry and topology, area nodes). Their simulation values (building condition, resources, renters, road
    /// traffic, tree growth, ...) change on the host without update tags and are not streamed in this version, so a
    /// client legitimately holds older values; hashing them would turn the verification into a slow value sync.</item>
    /// <item><b>Presence-only values</b>: a value that references something no peer can name travels as presence only
    /// and the client keeps its own value. The host hashes such a component as presence (the codec cannot serialize it);
    /// the client records which types each received record carried as presence only (<see cref="OnRecord"/>) and hashes
    /// them as presence too, whatever its local value. Entities that came with the join snapshot have no record; their
    /// values are the host's own, so both sides resolve (or fail to resolve) the same references.</item>
    /// <item><b>Derived references</b> (lanes, station stops) are reduced to kind, owner and prefab
    /// (<see cref="EntityCodec.TryHashValue"/>): each peer regenerates them with its own index and rounding.</item>
    /// </list>
    /// Members are combined with a wrapping sum, so the archetype's type order (which depends on each machine's type
    /// registration) does not matter. Main thread only.
    /// </summary>
    public sealed class IntegrityHasher
    {
        /// <summary>Structural source values verified on non-line families (types this game lacks are skipped).</summary>
        public static readonly string[] StructuralTypeNames =
        {
            "Game.Prefabs.PrefabRef",
            "Game.Common.Owner",
            "Game.Objects.Transform",
            "Game.Objects.Attached",
            "Game.Objects.Elevation",
            "Game.Net.Node",
            "Game.Net.Edge",
            "Game.Net.Curve",
            "Game.Net.Elevation",
            "Game.Net.Upgraded",
            "Game.Net.Fixed",
            "Game.Net.AggregateElement",
            "Game.Areas.Node",
            "Game.Areas.Area",
        };

        /// <summary>Components never verified on line families.</summary>
        private static readonly HashSet<string> NotVerified = new HashSet<string>(StringComparer.Ordinal)
        {
            "Game.Routes.HiddenRoute",
        };

        private const ulong EntityDomain = 0x3C6EF372FE94F82BUL;
        private const ulong PresenceMarker = 0xA54FF53A5F1D36F1UL;

        private readonly EntityCodec _codec;
        private readonly ILog _log;
        private readonly Dictionary<uint, ulong[]> _presenceOnly = new Dictionary<uint, ulong[]>();
        private readonly List<ComponentHash> _members = new List<ComponentHash>(64);
        private readonly HashSet<ulong> _recordTypes = new HashSet<ulong>();
        private readonly List<ulong> _masked = new List<ulong>();
        private List<TypeHandler> _structural;
        private readonly Dictionary<EntityArchetype, TypeHandler[]> _lineMembers = new Dictionary<EntityArchetype, TypeHandler[]>();
        private readonly Dictionary<EntityArchetype, TypeHandler[]> _structuralMembers = new Dictionary<EntityArchetype, TypeHandler[]>();

        public IntegrityHasher(EntityCodec codec, ILog log)
        {
            _codec = codec;
            _log = log;
        }

        /// <summary>Entities with at least one presence-only value recorded (client).</summary>
        public int MaskedEntities => _presenceOnly.Count;

        /// <summary>Families the verification covers: the replicated ones (prefabs and singletons are not entities of the city).</summary>
        public static bool IsVerified(Family family) => FamilyClassifier.IsReplicated(family) && !FamilyClassifier.IsHostOnly(family);

        /// <summary>Families whose every replicated value is verified (value-scanned by the host).</summary>
        public static bool HashesAllValues(Family family) => family == Family.Route || family == Family.Waypoint || family == Family.Segment;

        /// <summary>The entity's integrity hash.</summary>
        public ulong Hash(Entity e, uint netId, Family family)
        {
            Collect(e, netId, family, _members);
            ulong sum = 0;
            foreach (ComponentHash m in _members)
            {
                unchecked
                {
                    sum += HashMix.Mix64(HashMix.Combine(m.TypeHash, m.State == ComponentHashState.Value ? m.ValueHash : PresenceMarker));
                }
            }
            return HashMix.Combine(HashMix.Combine(EntityDomain ^ (ulong)family, sum), (ulong)_members.Count);
        }

        /// <summary>The members of the entity's integrity hash (explanation of a persistent mismatch).</summary>
        public void Collect(Entity e, uint netId, Family family, List<ComponentHash> output)
        {
            output.Clear();
            EntityManager em = _codec.EntityManager;
            _presenceOnly.TryGetValue(netId, out ulong[] mask);
            // w2/ids perf pass: the members of an archetype are cached (no type enumeration or name lookup per entity)
            EntityArchetype archetype = em.GetChunk(e).Archetype;
            bool all = HashesAllValues(family);
            Dictionary<EntityArchetype, TypeHandler[]> cache = all ? _lineMembers : _structuralMembers;
            if (!cache.TryGetValue(archetype, out TypeHandler[] members))
            {
                members = BuildMembers(archetype, all);
                cache[archetype] = members;
            }
            for (int i = 0; i < members.Length; i++) output.Add(Member(members[i], e, mask));
        }

        private TypeHandler[] BuildMembers(EntityArchetype archetype, bool all)
        {
            TypeHandler[] present = _codec.HandlersOf(archetype);
            var list = new List<TypeHandler>(all ? present.Length : 16);
            if (all)
            {
                foreach (TypeHandler h in present)
                {
                    if (!NotVerified.Contains(h.Name)) list.Add(h);
                }
            }
            else
            {
                foreach (TypeHandler h in Structural())
                {
                    if (Array.IndexOf(present, h) >= 0) list.Add(h);
                }
            }
            return list.ToArray();
        }

        private ComponentHash Member(TypeHandler h, Entity e, ulong[] mask)
        {
            if (h.Kind != SyncComponentKind.Presence && !Contains(mask, h.Hash) && _codec.TryHashValue(h, e, out ulong value))
            {
                return new ComponentHash(h.Hash, value, ComponentHashState.Value);
            }
            return new ComponentHash(h.Hash, 0, ComponentHashState.Presence);
        }

        private static bool Contains(ulong[] mask, ulong typeHash)
        {
            if (mask == null) return false;
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] == typeHash) return true;
            }
            return false;
        }

        private List<TypeHandler> Structural()
        {
            if (_structural != null) return _structural;
            _structural = new List<TypeHandler>();
            foreach (string name in StructuralTypeNames)
            {
                Type type = FamilyClassifier.FindType(name);
                TypeHandler h = null;
                if (type != null)
                {
                    TypeIndex index = TypeManager.GetTypeIndex(type);
                    if (index != TypeIndex.Null) h = TypeHandlers.Get(index);
                }
                if (h == null) _log.Debug(LogCategory.Integrity, "structural type not verified (not found or not replicated): " + name);
                else _structural.Add(h);
            }
            return _structural;
        }

        /// <summary>
        /// Client, after a record was applied: remembers which of the entity's data components the host sent as presence
        /// only. Spawns and upserts carry the complete type set; a values record carries every value the host could
        /// serialize, so the entity's other data components are presence-only for now.
        /// </summary>
        public void OnRecord(SyncRecord r, Entity e)
        {
            _masked.Clear();
            if (r.Kind == SyncRecordKind.Spawn || r.Kind == SyncRecordKind.Upsert)
            {
                foreach (SyncComponent c in r.Components)
                {
                    if (c.Kind != SyncComponentKind.Presence) continue;
                    TypeHandler h = TypeHandlers.GetByHash(c.TypeHash);
                    if (h != null && h.Kind != SyncComponentKind.Presence) _masked.Add(c.TypeHash);
                }
            }
            else if (r.Kind == SyncRecordKind.Values)
            {
                EntityManager em = _codec.EntityManager;
                if (e == Entity.Null || !em.Exists(e)) return;
                _recordTypes.Clear();
                foreach (SyncComponent c in r.Components) _recordTypes.Add(c.TypeHash);
                foreach (TypeHandler h in _codec.HandlersOf(e))
                {
                    if (h.Kind != SyncComponentKind.Presence && !_recordTypes.Contains(h.Hash)) _masked.Add(h.Hash);
                }
            }
            else
            {
                return;
            }
            if (_masked.Count == 0) _presenceOnly.Remove(r.NetId);
            else _presenceOnly[r.NetId] = _masked.ToArray();
        }

        /// <summary>The id was unbound (despawn).</summary>
        public void Forget(uint netId) => _presenceOnly.Remove(netId);

        public void Clear() => _presenceOnly.Clear();
    }
}
