using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Host: which epoch each peer has applied (ARCH §3.1 condition (a), §4.5.4). An epoch is one host frame's worth of
    /// replication messages; <see cref="Seal"/> closes it and records, per admitted peer, how many reliable
    /// <c>Epoch</c> messages had been queued for that peer by then (its watermark). A peer acknowledges with the number
    /// of <c>Epoch</c> messages it has <i>applied</i> (<see cref="EpochAckTracker"/>), so it has applied epoch E once its
    /// acknowledged count reaches E's watermark. Epochs sealed before a peer was admitted count as applied by it (its
    /// stream starts after them, and its join snapshot is taken after its admission).
    /// <para>Host main thread. Not thread-safe.</para>
    /// </summary>
    public sealed class EpochAckBook
    {
        private sealed class PeerProgress
        {
            public uint Baseline;
            public long Acked;
            public uint AckedEpoch;
            public readonly Queue<(uint Epoch, long Watermark)> Marks = new Queue<(uint, long)>();
        }

        /// <summary>Marks kept per peer at most; beyond it the oldest are merged (a peer that never acknowledges just stays behind).</summary>
        public const int MaxMarksPerPeer = 1 << 16;

        private readonly Dictionary<byte, PeerProgress> _peers = new Dictionary<byte, PeerProgress>();
        private uint _epoch;

        /// <summary>The last sealed epoch (0 before the first seal).</summary>
        public uint CurrentEpoch => _epoch;

        /// <summary>Peers tracked.</summary>
        public int PeerCount => _peers.Count;

        /// <summary>A peer was admitted: its stream starts now; every epoch sealed so far counts as applied by it.</summary>
        public void AddPeer(byte peer)
        {
            _peers[peer] = new PeerProgress { Baseline = _epoch, AckedEpoch = _epoch };
        }

        /// <summary>A peer left: it no longer holds anything back.</summary>
        public void RemovePeer(byte peer) => _peers.Remove(peer);

        public bool HasPeer(byte peer) => _peers.ContainsKey(peer);

        /// <summary>
        /// Closes the current epoch. <paramref name="sentCount"/> returns the number of reliable <c>Epoch</c> messages
        /// queued so far for a peer. Returns the sealed epoch's number.
        /// </summary>
        public uint Seal(Func<byte, long> sentCount)
        {
            if (sentCount == null) throw new ArgumentNullException(nameof(sentCount));
            _epoch++;
            foreach (KeyValuePair<byte, PeerProgress> pair in _peers)
            {
                PeerProgress p = pair.Value;
                long watermark = sentCount(pair.Key);
                if (watermark <= p.Acked && p.Marks.Count == 0)
                {
                    p.AckedEpoch = _epoch; // nothing of this epoch is outstanding for this peer
                    continue;
                }
                if (p.Marks.Count >= MaxMarksPerPeer) p.Marks.Dequeue(); // keeps memory bounded; can only delay acks
                p.Marks.Enqueue((_epoch, watermark));
            }
            return _epoch;
        }

        /// <summary>A peer reports how many <c>Epoch</c> messages it has applied (monotonic; smaller values are ignored).</summary>
        public void OnAck(byte peer, long appliedCount)
        {
            if (!_peers.TryGetValue(peer, out PeerProgress p) || appliedCount <= p.Acked) return;
            p.Acked = appliedCount;
            while (p.Marks.Count > 0 && p.Marks.Peek().Watermark <= appliedCount)
            {
                p.AckedEpoch = p.Marks.Dequeue().Epoch;
            }
            if (p.Marks.Count == 0) p.AckedEpoch = _epoch;
        }

        /// <summary>The newest epoch the peer has applied (the current epoch for unknown peers).</summary>
        public uint AckedEpoch(byte peer) => _peers.TryGetValue(peer, out PeerProgress p) ? p.AckedEpoch : _epoch;

        /// <summary>The newest epoch every tracked peer has applied; the current epoch when no peer is tracked.</summary>
        public uint MinAckedEpoch
        {
            get
            {
                uint min = _epoch;
                foreach (PeerProgress p in _peers.Values)
                {
                    if (p.AckedEpoch < min) min = p.AckedEpoch;
                }
                return min;
            }
        }

        /// <summary>Epochs the slowest peer is behind (diagnostics).</summary>
        public uint MaxLag => _epoch - MinAckedEpoch;

        public void Clear()
        {
            _peers.Clear();
            _epoch = 0;
        }
    }

    /// <summary>
    /// Client: which of the host's <c>Epoch</c> messages have been applied. Each received message gets the next sequence
    /// number (<see cref="OnReceived"/>); a consumer that applies it later holds it (<see cref="Hold"/>) and releases it
    /// once applied (<see cref="Release"/>). <see cref="AppliedThrough"/> is the highest sequence number below which
    /// nothing is held any more: what the client acknowledges (<see cref="EpochAckMessage"/>). Messages nobody holds
    /// count as applied at once. Holds may be released in any order. Main thread. Not thread-safe.
    /// </summary>
    public sealed class EpochAckTracker
    {
        private readonly List<long> _seqs = new List<long>();
        private readonly List<int> _counts = new List<int>();
        private int _head;
        private long _received;

        /// <summary>Messages received so far (the sequence number of the latest).</summary>
        public long Received => _received;

        /// <summary>Messages currently held by consumers.</summary>
        public int HeldCount
        {
            get
            {
                int n = 0;
                for (int i = _head; i < _counts.Count; i++) n += _counts[i];
                return n;
            }
        }

        /// <summary>Assigns the next sequence number to a received message and returns it.</summary>
        public long OnReceived() => ++_received;

        /// <summary>A consumer will apply message <paramref name="seq"/> later (sequence numbers must be held in non-decreasing order).</summary>
        public void Hold(long seq)
        {
            if (seq <= 0 || seq > _received) throw new ArgumentOutOfRangeException(nameof(seq));
            int last = _counts.Count - 1;
            if (last >= _head && _seqs[last] == seq)
            {
                _counts[last]++;
                return;
            }
            if (last >= _head && _seqs[last] > seq) throw new ArgumentException("holds must be taken in sequence order", nameof(seq));
            _seqs.Add(seq);
            _counts.Add(1);
        }

        /// <summary>A consumer applied (or dropped) message <paramref name="seq"/>. Unknown sequence numbers are ignored.</summary>
        public void Release(long seq)
        {
            int lo = _head, hi = _seqs.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                long v = _seqs[mid];
                if (v == seq)
                {
                    if (_counts[mid] > 0) _counts[mid]--;
                    break;
                }
                if (v < seq) lo = mid + 1;
                else hi = mid - 1;
            }
            while (_head < _counts.Count && _counts[_head] == 0) _head++;
            if (_head == _counts.Count)
            {
                _seqs.Clear();
                _counts.Clear();
                _head = 0;
            }
            else if (_head > 1024 && _head * 2 > _counts.Count)
            {
                _seqs.RemoveRange(0, _head);
                _counts.RemoveRange(0, _head);
                _head = 0;
            }
        }

        /// <summary>Every message up to this sequence number has been applied.</summary>
        public long AppliedThrough => _head < _seqs.Count ? _seqs[_head] - 1 : _received;

        public void Clear()
        {
            _seqs.Clear();
            _counts.Clear();
            _head = 0;
            _received = 0;
        }
    }

    /// <summary>The body of <c>MessageKind.EpochAck</c> (client → host): the number of host <c>Epoch</c> messages applied.</summary>
    public static class EpochAckMessage
    {
        public static void Write(ByteWriter w, long appliedCount)
        {
            if (appliedCount < 0) throw new ArgumentOutOfRangeException(nameof(appliedCount));
            w.WriteVarUInt64((ulong)appliedCount);
        }

        /// <summary>Throws <see cref="DecodeException"/> for malformed input.</summary>
        public static long Read(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            ulong v = r.ReadVarUInt64();
            if (v > long.MaxValue) throw new DecodeException("epoch ack out of range");
            return (long)v;
        }
    }
}
