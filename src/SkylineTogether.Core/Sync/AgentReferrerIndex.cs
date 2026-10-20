using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Client: for every entity, the entities that may hold a reference naming it (ARCHITECTURE.md#reference-safety): the
    /// owners whose reverse buffers list it, the holders whose forward agent links point at it, and the movers whose owner,
    /// resident or pet it is. The deletion cascade asks it for the referrers of an entity it deletes, so every list and link
    /// that names the entity is cleaned in the same frame, also the ones the entity's own links do not lead to: on a client
    /// the reverse lists and the members' forward links travel on different streams (key links, values, co-captured
    /// owners), and some links have no reverse list at all (a citizen's <c>CurrentBuilding</c>: a building the player
    /// demolishes is named by everyone inside it).
    /// <para>
    /// The index is a superset: entries are only added (and pruned when their referrer is gone), never removed when a link
    /// changes. Its user reads the referrer's real link before it changes anything, so a stale entry costs a lookup and
    /// nothing else; a missing entry would leave a dangling reference, which is why the client indexes every holder whose
    /// links it writes and the whole graph when it goes live and again in every sweep.
    /// </para>
    /// Chains of referrers are kept in pooled node arrays (no allocation per entry); a target with many referrers (a large
    /// workplace) also gets a hash set, so adding a referrer that is already known stays cheap. Main thread only.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class AgentReferrerIndex<TEntity>
    {
        /// <summary>A chain this long gets a hash set for its membership test.</summary>
        public const int LargeChain = 32;

        private readonly Dictionary<TEntity, int> _heads;
        private readonly Dictionary<TEntity, Large> _large = new Dictionary<TEntity, Large>();
        private readonly EqualityComparer<TEntity> _eq = EqualityComparer<TEntity>.Default;
        private TEntity[] _referrer;
        private int[] _next;
        private int _used, _free = -1;

        private sealed class Large
        {
            public readonly HashSet<TEntity> Members = new HashSet<TEntity>();
            /// <summary>Size after the last prune; the chain is pruned again once it has doubled.</summary>
            public int PrunedAt;
        }

        public AgentReferrerIndex(int capacity = 1024)
        {
            if (capacity < 1) capacity = 1;
            _heads = new Dictionary<TEntity, int>(capacity);
            _referrer = new TEntity[capacity];
            _next = new int[capacity];
        }

        /// <summary>Entries (target, referrer) held.</summary>
        public int Count { get; private set; }

        /// <summary>Entities that have at least one referrer.</summary>
        public int Targets => _heads.Count;

        /// <summary>Targets whose chain has a hash set (diagnostics).</summary>
        public int LargeTargets => _large.Count;

        /// <summary>Entries removed because their referrer was gone (diagnostics).</summary>
        public long Pruned { get; private set; }

        /// <summary>
        /// Notes that <paramref name="referrer"/> may hold a reference naming <paramref name="target"/> (a pair already held
        /// is not added twice). Referrers of the same target for which <paramref name="alive"/> is false are removed in
        /// passing (in a large chain once it has doubled since the last prune). Returns true when the pair was new.
        /// </summary>
        public bool Add(TEntity target, TEntity referrer, Func<TEntity, bool> alive = null)
        {
            if (_eq.Equals(target, default) || _eq.Equals(referrer, default) || _eq.Equals(target, referrer)) return false;
            if (_large.TryGetValue(target, out Large large))
            {
                if (!large.Members.Add(referrer)) return false;
                Push(target, referrer);
                if (alive != null && large.Members.Count >= 2 * Math.Max(LargeChain, large.PrunedAt)) PruneLarge(target, large, alive);
                return true;
            }
            int length = 0;
            if (_heads.TryGetValue(target, out int head))
            {
                int previous = -1;
                for (int node = head; node >= 0;)
                {
                    int next = _next[node];
                    TEntity r = _referrer[node];
                    if (_eq.Equals(r, referrer)) return false;
                    if (alive != null && !alive(r))
                    {
                        if (previous < 0) head = next;
                        else _next[previous] = next;
                        Release(node);
                        Pruned++;
                    }
                    else
                    {
                        previous = node;
                        length++;
                    }
                    node = next;
                }
                if (head < 0) _heads.Remove(target);
                else _heads[target] = head;
            }
            Push(target, referrer);
            if (length + 1 >= LargeChain) MakeLarge(target);
            return true;
        }

        /// <summary>Adds the referrers noted for <paramref name="target"/> to <paramref name="output"/>; returns how many.</summary>
        public int Get(TEntity target, List<TEntity> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (!_heads.TryGetValue(target, out int node)) return 0;
            int n = 0;
            for (; node >= 0; node = _next[node])
            {
                output.Add(_referrer[node]);
                n++;
            }
            return n;
        }

        /// <summary>True when the pair is held.</summary>
        public bool Contains(TEntity target, TEntity referrer)
        {
            if (_large.TryGetValue(target, out Large large)) return large.Members.Contains(referrer);
            if (!_heads.TryGetValue(target, out int node)) return false;
            for (; node >= 0; node = _next[node])
            {
                if (_eq.Equals(_referrer[node], referrer)) return true;
            }
            return false;
        }

        /// <summary>Forgets one pair (a referrer that no longer names the target: a mover despawned). Returns true when it was held.</summary>
        public bool Remove(TEntity target, TEntity referrer)
        {
            if (!_heads.TryGetValue(target, out int head)) return false;
            int previous = -1;
            for (int node = head; node >= 0; node = _next[node])
            {
                if (!_eq.Equals(_referrer[node], referrer))
                {
                    previous = node;
                    continue;
                }
                int next = _next[node];
                if (previous < 0) head = next;
                else _next[previous] = next;
                Release(node);
                if (_large.TryGetValue(target, out Large large)) large.Members.Remove(referrer);
                if (head < 0)
                {
                    _heads.Remove(target);
                    _large.Remove(target);
                }
                else
                {
                    _heads[target] = head;
                }
                return true;
            }
            return false;
        }

        private readonly List<TEntity> _sweepTargets = new List<TEntity>();
        private int _sweepCursor;

        /// <summary>
        /// Prunes the referrers <paramref name="alive"/> rejects, for at most <paramref name="maxTargets"/> targets per call,
        /// round-robin over every target (a lap snapshots the target list). Chains only grow otherwise: a depot's chain
        /// collected every vehicle it ever owned. Returns the entries removed.
        /// </summary>
        public int Sweep(Func<TEntity, bool> alive, int maxTargets)
        {
            if (alive == null) throw new ArgumentNullException(nameof(alive));
            if (_sweepCursor >= _sweepTargets.Count)
            {
                _sweepTargets.Clear();
                _sweepTargets.AddRange(_heads.Keys);
                _sweepCursor = 0;
            }
            int removed = 0;
            int end = Math.Min(_sweepTargets.Count, _sweepCursor + Math.Max(1, maxTargets));
            for (; _sweepCursor < end; _sweepCursor++)
            {
                TEntity target = _sweepTargets[_sweepCursor];
                if (!_heads.TryGetValue(target, out int head)) continue;
                if (!alive(target))
                {
                    removed += RemoveTarget(target);
                    continue;
                }
                int previous = -1;
                for (int node = head; node >= 0;)
                {
                    int next = _next[node];
                    TEntity r = _referrer[node];
                    if (!alive(r))
                    {
                        if (previous < 0) head = next;
                        else _next[previous] = next;
                        if (_large.TryGetValue(target, out Large large)) large.Members.Remove(r);
                        Release(node);
                        Pruned++;
                        removed++;
                    }
                    else
                    {
                        previous = node;
                    }
                    node = next;
                }
                if (head < 0)
                {
                    _heads.Remove(target);
                    _large.Remove(target);
                }
                else
                {
                    _heads[target] = head;
                }
            }
            return removed;
        }

        /// <summary>Forgets the referrers of <paramref name="target"/> (it was deleted); returns how many entries went.</summary>
        public int RemoveTarget(TEntity target)
        {
            _large.Remove(target);
            if (!_heads.TryGetValue(target, out int node)) return 0;
            _heads.Remove(target);
            int n = 0;
            while (node >= 0)
            {
                int next = _next[node];
                Release(node);
                n++;
                node = next;
            }
            return n;
        }

        public void Clear()
        {
            _sweepTargets.Clear();
            _sweepCursor = 0;
            _heads.Clear();
            _large.Clear();
            Array.Clear(_referrer, 0, _used);
            _used = 0;
            _free = -1;
            Count = 0;
            Pruned = 0;
        }

        private void Push(TEntity target, TEntity referrer)
        {
            int head = _heads.TryGetValue(target, out int h) ? h : -1;
            int n = Allocate();
            _referrer[n] = referrer;
            _next[n] = head;
            _heads[target] = n;
            Count++;
        }

        private void MakeLarge(TEntity target)
        {
            var large = new Large();
            for (int node = _heads[target]; node >= 0; node = _next[node]) large.Members.Add(_referrer[node]);
            large.PrunedAt = large.Members.Count;
            _large[target] = large;
        }

        private void PruneLarge(TEntity target, Large large, Func<TEntity, bool> alive)
        {
            int head = _heads[target];
            int previous = -1;
            for (int node = head; node >= 0;)
            {
                int next = _next[node];
                TEntity r = _referrer[node];
                if (!alive(r))
                {
                    if (previous < 0) head = next;
                    else _next[previous] = next;
                    large.Members.Remove(r);
                    Release(node);
                    Pruned++;
                }
                else
                {
                    previous = node;
                }
                node = next;
            }
            if (head < 0)
            {
                _heads.Remove(target);
                _large.Remove(target);
                return;
            }
            _heads[target] = head;
            large.PrunedAt = large.Members.Count;
        }

        private int Allocate()
        {
            if (_free >= 0)
            {
                int n = _free;
                _free = _next[n];
                return n;
            }
            if (_used == _referrer.Length)
            {
                int size = _referrer.Length * 2;
                Array.Resize(ref _referrer, size);
                Array.Resize(ref _next, size);
            }
            return _used++;
        }

        private void Release(int node)
        {
            _referrer[node] = default;
            _next[node] = _free;
            _free = node;
            Count--;
        }
    }
}
