using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Host: which <b>release frame</b> each peer has applied (ARCHITECTURE.md#net-ids condition (a)). A release frame is one host
    /// frame's worth of replication messages; <see cref="SealFrame"/> closes it and records, per admitted peer, how many
    /// reliable <c>Epoch</c> messages had been queued for that peer by then (its watermark). A peer acknowledges with the
    /// number of <c>Epoch</c> messages it has <i>applied</i> (<see cref="ApplyAckTracker"/>), so it has applied frame F once
    /// its acknowledged count reaches F's watermark. Frames sealed before a peer was admitted count as applied by it (its
    /// stream starts after them, and its join snapshot is taken after its admission). Release frames gate net id reuse
    /// only; they are unrelated to the ledger epochs (<c>SyncRecordKind.Epoch</c>, conflict bases).
    /// <para>Host main thread. Not thread-safe.</para>
    /// </summary>
    public sealed class ApplyAckBook
    {
        private sealed class PeerProgress
        {
            public uint Baseline;
            public long Acked;
            public uint AckedFrame;
            public readonly Queue<(uint Frame, long Watermark)> Marks = new Queue<(uint, long)>();
        }

        /// <summary>Marks kept per peer at most; beyond it the oldest are merged (a peer that never acknowledges just stays behind).</summary>
        public const int MaxMarksPerPeer = 1 << 16;

        private readonly Dictionary<byte, PeerProgress> _peers = new Dictionary<byte, PeerProgress>();
        private uint _frame;

        /// <summary>The last sealed frame (0 before the first seal).</summary>
        public uint CurrentFrame => _frame;

        /// <summary>Peers tracked.</summary>
        public int PeerCount => _peers.Count;

        /// <summary>A peer was admitted: its stream starts now; every frame sealed so far counts as applied by it.</summary>
        public void AddPeer(byte peer)
        {
            _peers[peer] = new PeerProgress { Baseline = _frame, AckedFrame = _frame };
        }

        /// <summary>A peer left: it no longer holds anything back.</summary>
        public void RemovePeer(byte peer) => _peers.Remove(peer);

        public bool HasPeer(byte peer) => _peers.ContainsKey(peer);

        /// <summary>
        /// Closes the current release frame. <paramref name="sentCount"/> returns the number of reliable <c>Epoch</c> messages
        /// queued so far for a peer. Returns the sealed frame's number.
        /// </summary>
        public uint SealFrame(Func<byte, long> sentCount)
        {
            if (sentCount == null) throw new ArgumentNullException(nameof(sentCount));
            _frame++;
            foreach (KeyValuePair<byte, PeerProgress> pair in _peers)
            {
                PeerProgress p = pair.Value;
                long watermark = sentCount(pair.Key);
                if (watermark <= p.Acked && p.Marks.Count == 0)
                {
                    p.AckedFrame = _frame; // nothing of this frame is outstanding for this peer
                    continue;
                }
                if (p.Marks.Count >= MaxMarksPerPeer) p.Marks.Dequeue(); // keeps memory bounded; can only delay acks
                p.Marks.Enqueue((_frame, watermark));
            }
            return _frame;
        }

        /// <summary>A peer reports how many <c>Epoch</c> messages it has applied (monotonic; smaller values are ignored).</summary>
        public void OnAck(byte peer, long appliedCount)
        {
            if (!_peers.TryGetValue(peer, out PeerProgress p) || appliedCount <= p.Acked) return;
            p.Acked = appliedCount;
            while (p.Marks.Count > 0 && p.Marks.Peek().Watermark <= appliedCount)
            {
                p.AckedFrame = p.Marks.Dequeue().Frame;
            }
            if (p.Marks.Count == 0) p.AckedFrame = _frame;
        }

        /// <summary>The newest frame the peer has applied (the current frame for unknown peers).</summary>
        public uint AckedFrame(byte peer) => _peers.TryGetValue(peer, out PeerProgress p) ? p.AckedFrame : _frame;

        /// <summary>The newest frame every tracked peer has applied; the current frame when no peer is tracked.</summary>
        public uint MinAckedFrame
        {
            get
            {
                uint min = _frame;
                foreach (PeerProgress p in _peers.Values)
                {
                    if (p.AckedFrame < min) min = p.AckedFrame;
                }
                return min;
            }
        }

        /// <summary>Release frames the slowest peer is behind (diagnostics).</summary>
        public uint MaxLag => _frame - MinAckedFrame;

        public void Clear()
        {
            _peers.Clear();
            _frame = 0;
        }
    }

    /// <summary>
    /// Host: peers whose applied release frame stopped advancing while the host kept sealing frames. Such a
    /// peer pins the id quarantine (released ids wait for every peer's acknowledgement) and is falling behind for good:
    /// its replication feature switched off, or its apply is stuck, while its connection still answers pings. After
    /// <see cref="LimitSeconds"/> it is reported (once), and the host drops it with a rejoin offer: a rejoin with a fresh
    /// snapshot is the catch-up path. Main thread.
    /// </summary>
    public sealed class AckStallWatch
    {
        private readonly Dictionary<byte, (uint Frame, double Since, bool Reported)> _peers = new Dictionary<byte, (uint, double, bool)>();
        private readonly List<byte> _stalled = new List<byte>();

        public AckStallWatch(double limitSeconds)
        {
            if (!(limitSeconds > 0)) throw new ArgumentOutOfRangeException(nameof(limitSeconds));
            LimitSeconds = limitSeconds;
        }

        public double LimitSeconds { get; }

        /// <summary>
        /// Records each peer's applied frame (<paramref name="ackedFrame"/>) against the host's current frame and returns the
        /// peers that have been behind without any progress for longer than the limit (each peer once, until it advances
        /// again). A peer that is fully caught up never counts as stalled, however long nothing new is sent.
        /// </summary>
        public IReadOnlyList<byte> Update(IEnumerable<byte> peers, Func<byte, uint> ackedFrame, uint currentFrame, double now)
        {
            _stalled.Clear();
            foreach (byte peer in peers)
            {
                uint acked = ackedFrame(peer);
                if (!_peers.TryGetValue(peer, out (uint Frame, double Since, bool Reported) s) || acked != s.Frame || acked >= currentFrame)
                {
                    _peers[peer] = (acked, now, false);
                    continue;
                }
                if (s.Reported || now - s.Since <= LimitSeconds) continue;
                _peers[peer] = (s.Frame, s.Since, true);
                _stalled.Add(peer);
            }
            return _stalled;
        }

        /// <summary>Seconds <paramref name="peer"/> has gone without progress while behind (0 when caught up or unknown).</summary>
        public double StalledFor(byte peer, double now) => _peers.TryGetValue(peer, out (uint Frame, double Since, bool Reported) s) ? now - s.Since : 0;

        public void Forget(byte peer) => _peers.Remove(peer);

        public void Clear() => _peers.Clear();
    }

    /// <summary>
    /// Client: which of the host's <c>Epoch</c> messages have been applied. Each received message gets the next sequence
    /// number (<see cref="OnReceived"/>); a consumer that applies it later holds it (<see cref="Hold"/>) and releases it
    /// once applied (<see cref="Release"/>). <see cref="AppliedThrough"/> is the highest sequence number below which
    /// nothing is held any more: what the client acknowledges (<see cref="ApplyAckMessage"/>). Messages nobody holds
    /// count as applied at once. Holds may be released in any order. Main thread. Not thread-safe.
    /// </summary>
    public sealed class ApplyAckTracker
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

        /// <summary>
        /// A message that is not an <c>Epoch</c> but names net ids (simulation, agent and attention values) arrived and
        /// is applied later: nothing received from now on is acknowledged until it is released, so the host cannot free
        /// and reuse an id this message names before it was applied (ARCHITECTURE.md#net-ids). Everything received so far
        /// stays acknowledgeable. Returns the sequence number to <see cref="Release"/>.
        /// </summary>
        public long HoldNext()
        {
            long seq = _received + 1;
            AddHold(seq);
            return seq;
        }

        /// <summary>A consumer will apply message <paramref name="seq"/> later (sequence numbers must be held in non-decreasing order).</summary>
        public void Hold(long seq)
        {
            if (seq <= 0 || seq > _received) throw new ArgumentOutOfRangeException(nameof(seq));
            AddHold(seq);
        }

        private void AddHold(long seq)
        {
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

    /// <summary>
    /// Client: a FIFO of received messages that name net ids and are applied later (agent values, attention values,
    /// inspection samples). Each one holds the apply acknowledgement (<see cref="ApplyAckTracker.HoldNext"/>) from its
    /// arrival until it is applied or dropped, so a held message can never be applied after the host reused one of the
    /// ids it names. Every way out of the queue (<see cref="TryDequeue"/>, <see cref="Clear"/>, eviction) releases the
    /// hold. Main thread. Not thread-safe.
    /// </summary>
    public sealed class AckHeldQueue<T>
    {
        private readonly Queue<(T Item, long Seq)> _queue = new Queue<(T, long)>();
        private ApplyAckTracker _acks;

        public int Count => _queue.Count;

        /// <summary>Messages dropped by <see cref="Clear"/> or <see cref="EvictOldest"/> (diagnostics).</summary>
        public long Dropped { get; private set; }

        /// <summary>
        /// The tracker of the current client session (null outside one: nothing is held). Messages queued on another
        /// tracker are dropped and their holds released first.
        /// </summary>
        public void Attach(ApplyAckTracker acks)
        {
            if (ReferenceEquals(acks, _acks)) return;
            Clear();
            _acks = acks;
        }

        public void Enqueue(T item)
        {
            long seq = _acks != null ? _acks.HoldNext() : 0;
            _queue.Enqueue((item, seq));
        }

        /// <summary>
        /// Takes the oldest message and releases its hold: the caller applies it before anything else can acknowledge
        /// (acknowledgements are sent at the end of the frame).
        /// </summary>
        public bool TryDequeue(out T item)
        {
            if (_queue.Count == 0)
            {
                item = default(T);
                return false;
            }
            (T value, long seq) = _queue.Dequeue();
            if (seq != 0) _acks?.Release(seq);
            item = value;
            return true;
        }

        /// <summary>The oldest message, left in the queue (its hold stays).</summary>
        public bool TryPeek(out T item)
        {
            if (_queue.Count == 0)
            {
                item = default(T);
                return false;
            }
            item = _queue.Peek().Item;
            return true;
        }

        /// <summary>
        /// Takes the oldest message and hands its hold to the caller, who releases it (<see cref="Release"/>) once the
        /// message is applied or dropped (an inspection sample that waits for its tick keeps it meanwhile).
        /// </summary>
        public bool TryDequeueHeld(out T item, out long hold)
        {
            if (_queue.Count == 0)
            {
                item = default(T);
                hold = 0;
                return false;
            }
            (item, hold) = _queue.Dequeue();
            return true;
        }

        /// <summary>Releases a hold handed out by <see cref="TryDequeueHeld"/> (0 is ignored).</summary>
        public void Release(long hold)
        {
            if (hold != 0) _acks?.Release(hold);
        }

        /// <summary>Drops the oldest message (a full queue); false when empty.</summary>
        public bool EvictOldest()
        {
            if (!TryDequeue(out _)) return false;
            Dropped++;
            return true;
        }

        /// <summary>Drops every message and releases every hold.</summary>
        public void Clear()
        {
            while (_queue.Count > 0)
            {
                (T _, long seq) = _queue.Dequeue();
                if (seq != 0) _acks?.Release(seq);
                Dropped++;
            }
        }
    }

    /// <summary>The body of <c>MessageKind.ApplyAck</c> (client → host): the number of host <c>Epoch</c> messages applied.</summary>
    public static class ApplyAckMessage
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
            if (v > long.MaxValue) throw new DecodeException("frame ack out of range");
            return (long)v;
        }
    }
}
