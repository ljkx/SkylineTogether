using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// Per-peer delivery plan of simulation values (ARCHITECTURE.md#simulation-values). The host's value scan reports
    /// every entity whose simulation values changed (<see cref="MarkChanged"/>); each live peer keeps the set of entities
    /// it still needs. <see cref="Drain"/> hands them to an emitter within a byte budget in priority order:
    /// <list type="number">
    /// <item>dirty entities in the peer's view (<see cref="SetView"/>), the attention class (P1);</item>
    /// <item>every other dirty entity, oldest change first, the rolling refresh (P3; FIFO order bounds staleness).</item>
    /// </list>
    /// The peer's selected entity is skipped: it travels in time-aligned inspection samples (ARCHITECTURE.md#simulation-values), and sending
    /// it in between would show values from another tick. It stays dirty and is sent normally once deselected.
    /// <para>
    /// Joining peers: the join snapshot already holds the host's values, so a new peer only needs what changed after
    /// it. <see cref="CurrentStamp"/> taken when its snapshot is prepared and handed to <see cref="AddPeer"/> when it goes
    /// live marks exactly the entities changed in between. Not thread-safe (main thread).
    /// </para>
    /// </summary>
    public sealed class AttentionPlanner
    {
        /// <summary>The emitter's answer for an entity that does not exist any more (forgotten, nothing sent).</summary>
        public const int Gone = 0;

        /// <summary>The emitter's answer when the entity does not fit into the remaining budget (stop; it stays dirty).</summary>
        public const int NoRoom = -1;

        private sealed class PeerState
        {
            public readonly HashSet<uint> Dirty = new HashSet<uint>();
            public readonly Queue<uint> Order = new Queue<uint>();
            public readonly HashSet<uint> View = new HashSet<uint>();
            public readonly HashSet<uint> ViewDirty = new HashSet<uint>();
            public uint Selected;
            public long Sent;
            public long SentBytes;
        }

        private readonly Dictionary<byte, PeerState> _peers = new Dictionary<byte, PeerState>();
        private readonly Dictionary<uint, long> _stamps = new Dictionary<uint, long>();
        private readonly List<uint> _scratch = new List<uint>();
        private long _stamp;

        /// <summary>The change counter: increases with every <see cref="MarkChanged"/>.</summary>
        public long CurrentStamp => _stamp;

        /// <summary>Peers known to the planner.</summary>
        public int PeerCount => _peers.Count;

        /// <summary>Entities with a recorded change stamp.</summary>
        public int TrackedCount => _stamps.Count;

        /// <summary>Adds a live peer; every entity changed after <paramref name="sinceStamp"/> becomes dirty for it.</summary>
        public void AddPeer(PeerId peer, long sinceStamp)
        {
            var state = new PeerState();
            _peers[peer.Value] = state;
            foreach (KeyValuePair<uint, long> pair in _stamps)
            {
                if (pair.Value > sinceStamp && state.Dirty.Add(pair.Key)) state.Order.Enqueue(pair.Key);
            }
        }

        public void RemovePeer(PeerId peer) => _peers.Remove(peer.Value);

        public bool HasPeer(PeerId peer) => _peers.ContainsKey(peer.Value);

        /// <summary>Removes every peer and every stamp (session end).</summary>
        public void Clear()
        {
            _peers.Clear();
            _stamps.Clear();
            _stamp = 0;
        }

        /// <summary>The entity's simulation values changed on the host: every peer needs them.</summary>
        public void MarkChanged(uint id)
        {
            if (id == 0) return;
            _stamps[id] = ++_stamp;
            foreach (PeerState p in _peers.Values)
            {
                if (p.Dirty.Add(id)) p.Order.Enqueue(id);
                if (p.View.Contains(id)) p.ViewDirty.Add(id);
            }
        }

        /// <summary>The entity is gone: no peer needs it any more (queue entries are dropped lazily).</summary>
        public void Forget(uint id)
        {
            _stamps.Remove(id);
            foreach (PeerState p in _peers.Values)
            {
                p.Dirty.Remove(id);
                p.ViewDirty.Remove(id);
            }
        }

        /// <summary>Replaces the peer's in-view set (ids of the entities around its camera).</summary>
        public void SetView(PeerId peer, IEnumerable<uint> ids)
        {
            if (!_peers.TryGetValue(peer.Value, out PeerState p)) return;
            p.View.Clear();
            p.ViewDirty.Clear();
            if (ids == null) return;
            foreach (uint id in ids)
            {
                if (!p.View.Add(id)) continue;
                if (p.Dirty.Contains(id)) p.ViewDirty.Add(id);
            }
        }

        public bool IsInView(PeerId peer, uint id) => _peers.TryGetValue(peer.Value, out PeerState p) && p.View.Contains(id);

        /// <summary>The peer's selected entity (0 = none); it is served by inspection samples instead.</summary>
        public void SetSelected(PeerId peer, uint id)
        {
            if (_peers.TryGetValue(peer.Value, out PeerState p)) p.Selected = id;
        }

        public bool IsDirty(PeerId peer, uint id) => _peers.TryGetValue(peer.Value, out PeerState p) && p.Dirty.Contains(id);

        public int DirtyCount(PeerId peer) => _peers.TryGetValue(peer.Value, out PeerState p) ? p.Dirty.Count : 0;

        public int ViewDirtyCount(PeerId peer) => _peers.TryGetValue(peer.Value, out PeerState p) ? p.ViewDirty.Count : 0;

        /// <summary>Entities and bytes the emitter reported for the peer since it was added (diagnostics).</summary>
        public void GetSent(PeerId peer, out long entities, out long bytes)
        {
            entities = bytes = 0;
            if (!_peers.TryGetValue(peer.Value, out PeerState p)) return;
            entities = p.Sent;
            bytes = p.SentBytes;
        }

        /// <summary>
        /// Hands dirty entities of the peer to <paramref name="emit"/> until <paramref name="budgetBytes"/> are used:
        /// in-view entities first, then the rest oldest first. <paramref name="emit"/> returns the bytes it wrote (the
        /// entity is clean for this peer), <see cref="Gone"/> (the entity no longer exists; forgotten for this peer) or
        /// <see cref="NoRoom"/> (stop; it stays dirty). Each dirty entity is offered at most once per call. Returns the
        /// bytes used.
        /// </summary>
        public int Drain(PeerId peer, int budgetBytes, Func<uint, int> emit)
        {
            if (emit == null) throw new ArgumentNullException(nameof(emit));
            if (budgetBytes <= 0 || !_peers.TryGetValue(peer.Value, out PeerState p)) return 0;
            int used = 0;
            // 1: in view
            if (p.ViewDirty.Count > 0)
            {
                _scratch.Clear();
                foreach (uint id in p.ViewDirty)
                {
                    if (id != p.Selected) _scratch.Add(id);
                }
                _scratch.Sort();
                foreach (uint id in _scratch)
                {
                    if (used >= budgetBytes) return used;
                    int n = emit(id);
                    if (n == NoRoom) return used;
                    p.ViewDirty.Remove(id);
                    p.Dirty.Remove(id);
                    if (n > 0)
                    {
                        used += n;
                        p.Sent++;
                        p.SentBytes += n;
                    }
                }
            }
            // 2: rolling, oldest first
            int offered = p.Order.Count;
            bool deferredSelected = false;
            while (offered-- > 0 && used < budgetBytes && p.Order.Count > 0)
            {
                uint id = p.Order.Peek();
                if (!p.Dirty.Contains(id))
                {
                    p.Order.Dequeue(); // stale entry: already sent through the view path or forgotten
                    continue;
                }
                if (id == p.Selected)
                {
                    p.Order.Dequeue();
                    deferredSelected = true;
                    continue;
                }
                int n = emit(id);
                if (n == NoRoom) break;
                p.Order.Dequeue();
                p.Dirty.Remove(id);
                p.ViewDirty.Remove(id);
                if (n > 0)
                {
                    used += n;
                    p.Sent++;
                    p.SentBytes += n;
                }
            }
            if (deferredSelected && p.Dirty.Contains(p.Selected)) p.Order.Enqueue(p.Selected);
            return used;
        }
    }
}
