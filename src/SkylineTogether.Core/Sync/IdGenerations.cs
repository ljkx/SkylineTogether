using System;
using SkylineTogether.Core.Identity;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Client: the generation of each bound net id as the host announced it (ARCHITECTURE.md#net-ids). The host's
    /// <see cref="NetIdAllocator"/> increments an id's 8-bit generation on every reuse; entity records and the join
    /// snapshot's bindings carry it. An id the client bound itself (its own predicted edit with a granted id) has no
    /// known generation until the host's record of it arrives. Main thread. Not thread-safe.
    /// </summary>
    public sealed class IdGenerationTable
    {
        private const int PageShift = 13;
        private const int PageSize = 1 << PageShift;
        private const int PageMask = PageSize - 1;

        private readonly SlotBitSet _known = new SlotBitSet((int)NetIdAllocator.MaxId + 1);
        private byte[][] _pages = new byte[16][];

        /// <summary>Ids with a known generation.</summary>
        public int KnownCount => _known.Count;

        /// <summary>Records the host's generation of <paramref name="id"/>.</summary>
        public void Set(uint id, byte generation)
        {
            if (id == 0 || id > NetIdAllocator.MaxId) return;
            int page = (int)(id >> PageShift);
            if (page >= _pages.Length) Array.Resize(ref _pages, Math.Max(page + 1, _pages.Length * 2));
            byte[] p = _pages[page];
            if (p == null)
            {
                p = new byte[PageSize];
                _pages[page] = p;
            }
            p[id & PageMask] = generation;
            _known.Set((int)id);
        }

        /// <summary>The generation of <paramref name="id"/> is no longer known (bound locally, or unbound).</summary>
        public void Forget(uint id)
        {
            if (id == 0 || id > NetIdAllocator.MaxId) return;
            _known.Clear((int)id);
        }

        /// <summary>The host's generation of <paramref name="id"/>; false when it is not known here.</summary>
        public bool TryGet(uint id, out byte generation)
        {
            generation = 0;
            if (id == 0 || id > NetIdAllocator.MaxId || !_known.Get((int)id)) return false;
            int page = (int)(id >> PageShift);
            byte[] p = page < _pages.Length ? _pages[page] : null;
            if (p != null) generation = p[id & PageMask];
            return true;
        }

        public void Clear()
        {
            _known.Reset();
            _pages = new byte[16][];
        }
    }

    /// <summary>What the applier does with an entity record whose id is already bound here.</summary>
    public enum BoundRecordVerdict : byte
    {
        /// <summary>The record describes the bound entity: written into it (upsert).</summary>
        Write,
        /// <summary>
        /// The binding is stale (the host reused the id for another entity): the bound entity is despawned the way a
        /// despawn record does it, and the record creates the new entity (a values record, which cannot create, asks
        /// the host for the id).
        /// </summary>
        Replace,
    }

    /// <summary>
    /// Client (ARCHITECTURE.md#client-apply): the check of an entity record against the entity its id is bound to here. A
    /// stale binding (the host reused the id after a despawn this client never applied) must never turn into a write
    /// of one entity's components into another, which leaves archetypes the game's jobs index unchecked (a building
    /// with edge components). Pure.
    /// </summary>
    public static class BoundRecordCheck
    {
        /// <summary>
        /// Decides from the generations (when this client knows the bound id's generation, it is the whole answer: the
        /// same generation is the same host entity, whatever changed in place), else from the family the id was bound
        /// with and the class of the prefabs (a prefab can change in place, as when a building finishes its
        /// construction, but never across classes).
        /// </summary>
        /// <param name="recordGeneration">The generation the record carries.</param>
        /// <param name="boundGenerationKnown">Whether this client knows the generation of the bound id.</param>
        /// <param name="boundGeneration">That generation.</param>
        /// <param name="recordFamily">The record's family.</param>
        /// <param name="boundFamily">The family the id is bound with here.</param>
        /// <param name="prefabClassesKnown">Whether both prefab classes are known (the record names a prefab, the entity has one).</param>
        /// <param name="samePrefabClass">Whether the two prefabs are of the same class.</param>
        public static BoundRecordVerdict Check(byte recordGeneration, bool boundGenerationKnown, byte boundGeneration, Family recordFamily, Family boundFamily,
                                               bool prefabClassesKnown, bool samePrefabClass)
        {
            if (boundGenerationKnown) return recordGeneration == boundGeneration ? BoundRecordVerdict.Write : BoundRecordVerdict.Replace;
            if (recordFamily != boundFamily) return BoundRecordVerdict.Replace;
            if (prefabClassesKnown && !samePrefabClass) return BoundRecordVerdict.Replace;
            return BoundRecordVerdict.Write;
        }
    }

    /// <summary>
    /// Client: whether a mover record describes the mover its id is bound to here (ARCHITECTURE.md#movers): by the id's
    /// generation when known (a mover record carries it, <see cref="SyncRecord.Generation"/>), else by the prefab, which
    /// a vehicle or creature never changes (a different prefab is another mover under a reused id). Pure.
    /// </summary>
    public static class MoverBindingCheck
    {
        public static BoundRecordVerdict Check(byte recordGeneration, bool boundGenerationKnown, byte boundGeneration, bool prefabsKnown, bool samePrefab)
        {
            return BoundRecordCheck.Check(recordGeneration, boundGenerationKnown, boundGeneration, Family.Mover, Family.Mover, prefabsKnown, samePrefab);
        }
    }
}
