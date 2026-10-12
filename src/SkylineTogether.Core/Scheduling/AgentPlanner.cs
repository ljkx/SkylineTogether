using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// w3/agents: per-peer delivery plan of agent values (citizens, households, pets, companies; ARCH §4.2.2 class A,
    /// §4.8.3-4.8.4). The host's rolling agent scan reports every agent whose values changed (<see cref="MarkChanged"/>);
    /// each live peer keeps the set of agents it still needs, so repeated changes of one agent coalesce into one entry and
    /// the value is serialised when it is sent (latest value wins). <see cref="Drain"/> offers them to an emitter within
    /// a byte budget in priority order:
    /// <list type="number">
    /// <item><b>focus</b>: agents the peer explicitly asked for again (<see cref="Request"/>) and dirty agents of its focus
    /// set (<see cref="SetFocus"/>: the selected or followed entity's closure: citizen, household, members, renters,
    /// employees ...);</item>
    /// <item><b>view</b>: dirty agents of the peer's camera set (<see cref="SetView"/>: residents and companies of the
    /// buildings around its camera);</item>
    /// <item><b>rolling</b>: every other dirty agent, oldest change first (FIFO bounds staleness).</item>
    /// </list>
    /// <para>
    /// A change is <i>structural</i> when the agent's component set or an enabled bit changed (a citizen got a job, started
    /// a trip, fell ill): values alone cannot express it, so the emitter sends a full record for it. The flag sticks until
    /// the agent was sent to the peer.
    /// </para>
    /// <para>
    /// Joining peers: their join snapshot holds the host's values, so a new peer needs only what changed after it;
    /// <see cref="CurrentStamp"/> taken when its snapshot is prepared and handed to <see cref="AddPeer"/> marks exactly
    /// the agents changed in between. Main thread only.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class AgentPlanner
    {
        /// <summary>The emitter's answer for an agent that does not exist any more (forgotten for the peer, nothing sent).</summary>
        public const int Gone = 0;

        /// <summary>The emitter's answer when the agent does not fit into the remaining budget (stop; it stays dirty).</summary>
        public const int NoRoom = -1;

        private sealed class PeerState
        {
            public readonly HashSet<uint> Dirty = new HashSet<uint>();
            public readonly Queue<uint> Order = new Queue<uint>();
            public readonly HashSet<uint> Focus = new HashSet<uint>();
            public readonly HashSet<uint> View = new HashSet<uint>();
            public readonly HashSet<uint> FocusDirty = new HashSet<uint>();
            public readonly HashSet<uint> ViewDirty = new HashSet<uint>();
            public readonly HashSet<uint> Requested = new HashSet<uint>();
            public readonly HashSet<uint> Structural = new HashSet<uint>();
            public long Sent;
            public long SentBytes;
        }

        private readonly Dictionary<byte, PeerState> _peers = new Dictionary<byte, PeerState>();
        private readonly Dictionary<uint, long> _stamps = new Dictionary<uint, long>();
        private readonly Dictionary<uint, long> _structuralStamps = new Dictionary<uint, long>();
        private readonly List<uint> _scratch = new List<uint>();
        private long _stamp;

        /// <summary>The change counter: increases with every <see cref="MarkChanged"/>.</summary>
        public long CurrentStamp => _stamp;

        public int PeerCount => _peers.Count;

        /// <summary>Agents with a recorded change stamp.</summary>
        public int TrackedCount => _stamps.Count;

        /// <summary>Adds a live peer; every agent changed after <paramref name="sinceStamp"/> becomes dirty for it.</summary>
        public void AddPeer(PeerId peer, long sinceStamp)
        {
            var state = new PeerState();
            _peers[peer.Value] = state;
            foreach (KeyValuePair<uint, long> pair in _stamps)
            {
                if (pair.Value > sinceStamp && state.Dirty.Add(pair.Key)) state.Order.Enqueue(pair.Key);
            }
            foreach (KeyValuePair<uint, long> pair in _structuralStamps)
            {
                if (pair.Value > sinceStamp) state.Structural.Add(pair.Key);
            }
        }

        public void RemovePeer(PeerId peer) => _peers.Remove(peer.Value);

        public bool HasPeer(PeerId peer) => _peers.ContainsKey(peer.Value);

        public void Clear()
        {
            _peers.Clear();
            _stamps.Clear();
            _structuralStamps.Clear();
            _stamp = 0;
        }

        /// <summary>
        /// The agent changed on the host: every peer needs it. <paramref name="structural"/>: its component set or an
        /// enabled bit changed (a full record is needed).
        /// </summary>
        public void MarkChanged(uint id, bool structural = false)
        {
            if (id == 0) return;
            _stamps[id] = ++_stamp;
            if (structural) _structuralStamps[id] = _stamp;
            foreach (PeerState p in _peers.Values)
            {
                if (structural) p.Structural.Add(id);
                if (p.Dirty.Add(id)) p.Order.Enqueue(id);
                if (p.Focus.Contains(id)) p.FocusDirty.Add(id);
                else if (p.View.Contains(id)) p.ViewDirty.Add(id);
            }
        }

        /// <summary>
        /// The agent's full record just went to every peer (a structural upsert in the broadcast stream): nobody needs its
        /// values any more. The change stamp stays, so a joiner whose snapshot is older still gets it.
        /// </summary>
        public void MarkSentToAll(uint id)
        {
            foreach (PeerState p in _peers.Values) RemoveDirty(p, id);
        }

        /// <summary>The agent is gone (despawned, id released): no peer needs it.</summary>
        public void Forget(uint id)
        {
            _stamps.Remove(id);
            _structuralStamps.Remove(id);
            foreach (PeerState p in _peers.Values) RemoveDirty(p, id);
        }

        private static void RemoveDirty(PeerState p, uint id)
        {
            p.Dirty.Remove(id);
            p.FocusDirty.Remove(id);
            p.ViewDirty.Remove(id);
            p.Requested.Remove(id);
            p.Structural.Remove(id);
        }

        /// <summary>
        /// The peer asks for the agent again (focus priority, whether it changed or not); served as a full record, which also
        /// repairs a component set that went wrong.
        /// </summary>
        public void Request(PeerId peer, uint id)
        {
            if (id == 0 || !_peers.TryGetValue(peer.Value, out PeerState p)) return;
            p.Requested.Add(id);
            p.Structural.Add(id);
            if (p.Dirty.Add(id)) p.Order.Enqueue(id);
        }

        /// <summary>Replaces the peer's focus set (closure of its selected and followed entities).</summary>
        public void SetFocus(PeerId peer, IEnumerable<uint> ids)
        {
            if (!_peers.TryGetValue(peer.Value, out PeerState p)) return;
            Replace(p.Focus, p.FocusDirty, p.Dirty, ids);
            // a focus member is never also served from the view tier
            foreach (uint id in p.FocusDirty) p.ViewDirty.Remove(id);
        }

        /// <summary>Replaces the peer's camera set.</summary>
        public void SetView(PeerId peer, IEnumerable<uint> ids)
        {
            if (!_peers.TryGetValue(peer.Value, out PeerState p)) return;
            Replace(p.View, p.ViewDirty, p.Dirty, ids);
            p.ViewDirty.ExceptWith(p.Focus);
        }

        private static void Replace(HashSet<uint> set, HashSet<uint> dirtySubset, HashSet<uint> dirty, IEnumerable<uint> ids)
        {
            set.Clear();
            dirtySubset.Clear();
            if (ids == null) return;
            foreach (uint id in ids)
            {
                if (id == 0 || !set.Add(id)) continue;
                if (dirty.Contains(id)) dirtySubset.Add(id);
            }
        }

        public bool IsInFocus(PeerId peer, uint id) => _peers.TryGetValue(peer.Value, out PeerState p) && p.Focus.Contains(id);

        public bool IsDirty(PeerId peer, uint id) => _peers.TryGetValue(peer.Value, out PeerState p) && p.Dirty.Contains(id);

        public int DirtyCount(PeerId peer) => _peers.TryGetValue(peer.Value, out PeerState p) ? p.Dirty.Count : 0;

        /// <summary>Dirty agents of the peer that need a full record.</summary>
        public int StructuralCount(PeerId peer) => _peers.TryGetValue(peer.Value, out PeerState p) ? p.Structural.Count : 0;

        public bool IsStructural(PeerId peer, uint id) => _peers.TryGetValue(peer.Value, out PeerState p) && p.Structural.Contains(id);

        /// <summary>Dirty agents of the focus tier (requests included).</summary>
        public int FocusDirtyCount(PeerId peer) => _peers.TryGetValue(peer.Value, out PeerState p) ? p.FocusDirty.Count + p.Requested.Count : 0;

        public int ViewDirtyCount(PeerId peer) => _peers.TryGetValue(peer.Value, out PeerState p) ? p.ViewDirty.Count : 0;

        public int FocusCount(PeerId peer) => _peers.TryGetValue(peer.Value, out PeerState p) ? p.Focus.Count : 0;

        public int ViewCount(PeerId peer) => _peers.TryGetValue(peer.Value, out PeerState p) ? p.View.Count : 0;

        public void GetSent(PeerId peer, out long agents, out long bytes)
        {
            agents = bytes = 0;
            if (!_peers.TryGetValue(peer.Value, out PeerState p)) return;
            agents = p.Sent;
            bytes = p.SentBytes;
        }

        /// <summary>
        /// Offers the peer's dirty agents to <paramref name="emit"/> (id, structural) until <paramref name="budgetBytes"/> are
        /// used: requested and focus agents first, then view, then the rest oldest first. <paramref name="emit"/> returns the bytes it wrote
        /// (the agent is clean for this peer), <see cref="Gone"/> or <see cref="NoRoom"/> (stop; it stays dirty). Each dirty
        /// agent is offered at most once per call. Returns the bytes used.
        /// </summary>
        public int Drain(PeerId peer, int budgetBytes, Func<uint, bool, int> emit)
        {
            if (emit == null) throw new ArgumentNullException(nameof(emit));
            if (budgetBytes <= 0 || !_peers.TryGetValue(peer.Value, out PeerState p)) return 0;
            int used = 0;
            // 1: requested and focus
            if (p.Requested.Count + p.FocusDirty.Count > 0)
            {
                _scratch.Clear();
                _scratch.AddRange(p.Requested);
                foreach (uint id in p.FocusDirty)
                {
                    if (!p.Requested.Contains(id)) _scratch.Add(id);
                }
                if (!DrainList(p, ref used, budgetBytes, emit)) return used;
            }
            // 2: camera
            if (p.ViewDirty.Count > 0)
            {
                _scratch.Clear();
                _scratch.AddRange(p.ViewDirty);
                _scratch.Sort();
                if (!DrainList(p, ref used, budgetBytes, emit)) return used;
            }
            // 3: rolling, oldest first
            int offered = p.Order.Count;
            while (offered-- > 0 && used < budgetBytes && p.Order.Count > 0)
            {
                uint id = p.Order.Peek();
                if (!p.Dirty.Contains(id))
                {
                    p.Order.Dequeue(); // stale entry: sent through another tier or forgotten
                    continue;
                }
                int n = emit(id, p.Structural.Contains(id));
                if (n == NoRoom) break;
                p.Order.Dequeue();
                Sent(p, id, n, ref used);
            }
            return used;
        }

        private bool DrainList(PeerState p, ref int used, int budgetBytes, Func<uint, bool, int> emit)
        {
            foreach (uint id in _scratch)
            {
                if (used >= budgetBytes) return false;
                int n = emit(id, p.Structural.Contains(id));
                if (n == NoRoom) return false;
                Sent(p, id, n, ref used);
            }
            return true;
        }

        private static void Sent(PeerState p, uint id, int n, ref int used)
        {
            RemoveDirty(p, id);
            if (n <= 0) return;
            used += n;
            p.Sent++;
            p.SentBytes += n;
        }
    }
}
