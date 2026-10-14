using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// Bounds what one peer may have waiting in a host-side inbox (proposals, terrain strokes, map patches).
    /// Messages are copied into a queue on arrival and applied later under a frame budget; without a bound, a client that
    /// sends faster than the host applies (a bug, a mod, a hostile client) grows the host's memory without end. Each
    /// admitted message is counted until it is released; a message over the item or byte limit is refused and the peer is
    /// reported once (<see cref="TakeOverflow"/>), so the caller can disconnect it with an explanation. Main thread only.
    /// </summary>
    public sealed class InboundQuota
    {
        private sealed class Usage
        {
            public int Items;
            public long Bytes;
            public bool Overflowed;
            public bool Reported;
        }

        private readonly Dictionary<byte, Usage> _byPeer = new Dictionary<byte, Usage>();

        public InboundQuota(int maxItemsPerPeer, long maxBytesPerPeer)
        {
            if (maxItemsPerPeer < 1) throw new ArgumentOutOfRangeException(nameof(maxItemsPerPeer));
            if (maxBytesPerPeer < 1) throw new ArgumentOutOfRangeException(nameof(maxBytesPerPeer));
            MaxItems = maxItemsPerPeer;
            MaxBytes = maxBytesPerPeer;
        }

        public int MaxItems { get; }

        public long MaxBytes { get; }

        /// <summary>Messages refused because their peer was over its limit (session total).</summary>
        public long Dropped { get; private set; }

        /// <summary>
        /// Counts a message of <paramref name="bytes"/> for <paramref name="peer"/>; false (and nothing counted) when the peer
        /// already has <see cref="MaxItems"/> messages or would exceed <see cref="MaxBytes"/>. A single message larger than
        /// the byte limit is refused even when nothing else is waiting.
        /// </summary>
        public bool TryAdmit(PeerId peer, int bytes)
        {
            if (bytes < 0) bytes = 0;
            if (!_byPeer.TryGetValue(peer.Value, out Usage u)) _byPeer[peer.Value] = u = new Usage();
            if (u.Items >= MaxItems || u.Bytes + bytes > MaxBytes)
            {
                u.Overflowed = true;
                Dropped++;
                return false;
            }
            u.Items++;
            u.Bytes += bytes;
            return true;
        }

        /// <summary>A message counted by <see cref="TryAdmit"/> was applied or discarded.</summary>
        public void Release(PeerId peer, int bytes)
        {
            if (!_byPeer.TryGetValue(peer.Value, out Usage u)) return;
            if (bytes < 0) bytes = 0;
            u.Items = Math.Max(0, u.Items - 1);
            u.Bytes = Math.Max(0, u.Bytes - bytes);
        }

        /// <summary>Messages of <paramref name="peer"/> waiting now.</summary>
        public int Pending(PeerId peer) => _byPeer.TryGetValue(peer.Value, out Usage u) ? u.Items : 0;

        /// <summary>Bytes of <paramref name="peer"/> waiting now.</summary>
        public long PendingBytes(PeerId peer) => _byPeer.TryGetValue(peer.Value, out Usage u) ? u.Bytes : 0;

        /// <summary>
        /// True exactly once after <paramref name="peer"/> first went over its limit (until <see cref="Forget"/>): the caller
        /// logs it and disconnects the peer.
        /// </summary>
        public bool TakeOverflow(PeerId peer)
        {
            if (!_byPeer.TryGetValue(peer.Value, out Usage u) || !u.Overflowed || u.Reported) return false;
            u.Reported = true;
            return true;
        }

        /// <summary>The peer left (or its queue was dropped): its counts start from zero.</summary>
        public void Forget(PeerId peer) => _byPeer.Remove(peer.Value);

        /// <summary>Every peer's counts start from zero (session start or end).</summary>
        public void Clear()
        {
            _byPeer.Clear();
            Dropped = 0;
        }
    }
}
