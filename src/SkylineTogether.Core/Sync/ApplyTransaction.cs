using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// The new entities of one batch apply (ARCHITECTURE.md#client-apply): a new entity is kept only once its component
    /// set and its values were written. One whose component set or values failed is destroyed again (it would keep
    /// default values: a null <c>PrefabRef</c>, zero edge nodes, which the game's jobs index unchecked), and when the
    /// apply throws outside the per-record guards every new entity whose values were not written is destroyed. Keyed by
    /// the game's entity (generic for the tests). Main thread. Not thread-safe.
    /// </summary>
    public sealed class SpawnTransaction<T>
    {
        private readonly HashSet<T> _written = new HashSet<T>();
        private readonly HashSet<T> _failed = new HashSet<T>();

        /// <summary>New entities whose component set or values failed and that are not dropped yet.</summary>
        public int FailedCount => _failed.Count;

        public IReadOnlyCollection<T> Failed => _failed;

        /// <summary>A new entity's values were written completely.</summary>
        public void Written(T spawn) => _written.Add(spawn);

        /// <summary>Writing a new entity's component set or values threw: it must not be kept.</summary>
        public void Fail(T spawn) => _failed.Add(spawn);

        /// <summary>The failed entities were dropped (destroyed, unbound).</summary>
        public void ClearFailed()
        {
            foreach (T spawn in _failed) _written.Remove(spawn);
            _failed.Clear();
        }

        /// <summary>A new entity left the apply (dropped by another rule).</summary>
        public void Forget(T spawn)
        {
            _written.Remove(spawn);
            _failed.Remove(spawn);
        }

        public bool IsComplete(T spawn) => _written.Contains(spawn) && !_failed.Contains(spawn);

        /// <summary>
        /// The apply threw: the new entities of <paramref name="spawned"/> that are not complete (values not written, or
        /// failed), appended to <paramref name="output"/>. Complete ones stay.
        /// </summary>
        public void Incomplete(IEnumerable<T> spawned, List<T> output)
        {
            if (spawned == null) throw new ArgumentNullException(nameof(spawned));
            if (output == null) throw new ArgumentNullException(nameof(output));
            foreach (T spawn in spawned)
            {
                if (!IsComplete(spawn)) output.Add(spawn);
            }
        }

        public void Clear()
        {
            _written.Clear();
            _failed.Clear();
        }
    }

    /// <summary>
    /// Client: what to do with a host batch whose apply threw (ARCHITECTURE.md#client-apply). Acknowledging a batch tells
    /// the host it may reuse the ids it despawned, so a failed batch is acknowledged only once its despawns are applied;
    /// one that could not be decoded (its despawns are unknown) or whose despawns could not be applied is never
    /// acknowledged and the session falls back to a rejoin. Pure.
    /// </summary>
    public static class FailedBatchRecovery
    {
        /// <summary>
        /// The ids whose despawn record must be applied after the failure: every despawn of the batch except one that is
        /// followed by an entity record of the same id (an id reused within the batch: that despawn went first, before
        /// the entity steps, and despawning again would delete the new entity). In batch order, without duplicates.
        /// </summary>
        public static void DespawnsToApply(IReadOnlyList<SyncRecord> records, List<uint> output)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            if (output == null) throw new ArgumentNullException(nameof(output));
            var lastEntityRecord = new Dictionary<uint, int>();
            for (int i = 0; i < records.Count; i++)
            {
                if (IsEntityRecord(records[i].Kind)) lastEntityRecord[records[i].NetId] = i;
            }
            var seen = new HashSet<uint>();
            for (int i = 0; i < records.Count; i++)
            {
                SyncRecord r = records[i];
                if (r.Kind != SyncRecordKind.Despawn || r.NetId == 0) continue;
                if (lastEntityRecord.TryGetValue(r.NetId, out int last) && last > i) continue;
                if (seen.Add(r.NetId)) output.Add(r.NetId);
            }
        }

        /// <summary>The ids the batch wrote (spawns, upserts, values), each once: asked for again after the failure.</summary>
        public static void IdsToRequest(IReadOnlyList<SyncRecord> records, List<uint> output)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            if (output == null) throw new ArgumentNullException(nameof(output));
            var seen = new HashSet<uint>();
            foreach (SyncRecord r in records)
            {
                if (IsEntityRecord(r.Kind) && r.NetId != 0 && seen.Add(r.NetId)) output.Add(r.NetId);
            }
        }

        /// <summary>
        /// Whether the failed batch may be acknowledged: only when it was decoded and every despawn it carries is applied
        /// here afterwards (<see cref="CountNotApplied"/>).
        /// </summary>
        public static bool MayAcknowledge(bool decoded, int despawnsNotApplied) => decoded && despawnsNotApplied == 0;

        /// <summary>
        /// The despawns of <paramref name="ids"/> that are not applied: the id is still bound here, or the entity it was
        /// bound to survived (it exists without the deletion tag; a despawn that threw half-way unbinds the id but leaves
        /// the entity, which no id reaches any more while the host reuses the id).
        /// </summary>
        public static int CountNotApplied(IReadOnlyList<uint> ids, Func<uint, bool> stillBound, Func<uint, bool> entitySurvived)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            if (stillBound == null) throw new ArgumentNullException(nameof(stillBound));
            if (entitySurvived == null) throw new ArgumentNullException(nameof(entitySurvived));
            int n = 0;
            foreach (uint id in ids)
            {
                if (stillBound(id) || entitySurvived(id)) n++;
            }
            return n;
        }

        private static bool IsEntityRecord(SyncRecordKind kind) => kind == SyncRecordKind.Spawn || kind == SyncRecordKind.Upsert || kind == SyncRecordKind.Values;
    }
}
