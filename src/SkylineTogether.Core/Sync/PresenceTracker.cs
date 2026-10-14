using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// An order-independent 64-bit signature of a set of replicated component types (the type hashes of an
    /// entity's archetype). Two archetypes that hold the same replicated types have the same signature, whatever order
    /// the archetype lists them in; adding or removing one type changes it.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class TypeSetSignature
    {
        /// <summary>The signature of the empty set.</summary>
        public const ulong Empty = 0x9E3779B97F4A7C15UL;

        /// <summary>Adds one type hash to a signature (commutative, so the order of additions does not matter).</summary>
        public static ulong Add(ulong signature, ulong typeHash) => signature + Scramble(typeHash);

        /// <summary>The signature of a set of type hashes (duplicates count once).</summary>
        public static ulong Of(IEnumerable<ulong> typeHashes)
        {
            ulong s = Empty;
            if (typeHashes == null) return s;
            var seen = new HashSet<ulong>();
            foreach (ulong h in typeHashes)
            {
                if (seen.Add(h)) s = Add(s, h);
            }
            return s;
        }

        // splitmix64 finaliser: a sum of scrambled values is a good set hash (a plain sum or xor of raw type hashes
        // would cancel for related hashes)
        private static ulong Scramble(ulong x)
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }

    /// <summary>
    /// Host: the replicated component set (<see cref="TypeSetSignature"/>) each structure entity had when it
    /// was last sent. The simulation adds and removes components without an <c>Updated</c> tag (a building catches
    /// fire, is flooded, damaged or endangered, an accident site appears on a road), which the tag-driven capture never
    /// sees; comparing the current set with this baseline finds them, and the caller re-sends the entity as a full
    /// structure record. Not thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class PresenceTracker
    {
        private readonly Dictionary<uint, ulong> _sent = new Dictionary<uint, ulong>();

        /// <summary>Entities with a baseline.</summary>
        public int Count => _sent.Count;

        /// <summary>The set was sent (or both peers have it from the join snapshot): it becomes the baseline.</summary>
        public void Record(uint id, ulong signature)
        {
            if (id != 0) _sent[id] = signature;
        }

        /// <summary>
        /// True when the entity has a baseline and its current set differs. An id without a baseline gets
        /// <paramref name="signature"/> as its baseline and is reported as unchanged (it was never sent, so nobody
        /// holds an older set of it).
        /// </summary>
        public bool Changed(uint id, ulong signature)
        {
            if (id == 0) return false;
            if (!_sent.TryGetValue(id, out ulong old))
            {
                _sent[id] = signature;
                return false;
            }
            return old != signature;
        }

        /// <summary>The baseline of an id, if any.</summary>
        public bool TryGet(uint id, out ulong signature) => _sent.TryGetValue(id, out signature);

        /// <summary>The entity is gone (despawned or unbound).</summary>
        public void Forget(uint id) => _sent.Remove(id);

        public void Clear() => _sent.Clear();
    }

    /// <summary>
    /// A budgeted rolling pass over a snapshot of ids. <see cref="Refill"/> takes a new snapshot once the
    /// previous pass is complete (and not more often than the caller allows); <see cref="Next"/> hands out one id at a
    /// time. Not thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class RollingIdPass
    {
        private readonly List<uint> _ids = new List<uint>();
        private int _cursor;

        /// <summary>True while the current snapshot still has ids to hand out.</summary>
        public bool InProgress => _cursor < _ids.Count;

        /// <summary>Ids in the current snapshot.</summary>
        public int Count => _ids.Count;

        /// <summary>Starts a new pass over <paramref name="ids"/> (copied).</summary>
        public void Refill(IEnumerable<uint> ids)
        {
            _ids.Clear();
            _cursor = 0;
            if (ids != null) _ids.AddRange(ids);
        }

        /// <summary>The next id of the pass; false when the pass is complete.</summary>
        public bool Next(out uint id)
        {
            if (_cursor >= _ids.Count)
            {
                id = 0;
                return false;
            }
            id = _ids[_cursor++];
            return true;
        }

        public void Clear()
        {
            _ids.Clear();
            _cursor = 0;
        }
    }
}
