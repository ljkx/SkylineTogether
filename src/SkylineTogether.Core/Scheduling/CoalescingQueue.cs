using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>What a deferred entry will send; a stronger class absorbs a weaker one for the same id.</summary>
    public enum DeferredClass : byte
    {
        /// <summary>Simulation values only (no re-derivation on the receiver).</summary>
        Values = 1,
        /// <summary>The full replicated state (structure repair, re-sends).</summary>
        Upsert = 2,
    }

    /// <summary>
    /// One peer's deferred updates (ARCH D4: congestion becomes staleness, not backlog): a set of net ids, each marked
    /// with the strongest <see cref="DeferredClass"/> asked for, in the order they first became dirty. Marking an id that
    /// is already queued only upgrades its class (latest value wins: the sender serialises the entity's current state
    /// when it takes the entry), so the queue is bounded by the number of distinct ids, never by time. Removing an id
    /// (despawn, or a fresher full record went out) is O(1). Oldest-first draining gives every entry a bounded wait.
    /// Not thread-safe.
    /// </summary>
    public sealed class CoalescingQueue
    {
        private struct Entry
        {
            public DeferredClass Class;
            public uint Serial;
            public double Since;
        }

        private readonly Dictionary<uint, Entry> _entries = new Dictionary<uint, Entry>();
        private readonly Queue<(uint Id, uint Serial)> _order = new Queue<(uint, uint)>();
        private uint _serial;
        private long _marked, _coalesced;

        /// <summary>Distinct ids waiting.</summary>
        public int Count => _entries.Count;

        /// <summary>Marks that hit an id already waiting (diagnostics: updates saved by coalescing).</summary>
        public long CoalescedCount => _coalesced;

        /// <summary>All marks so far.</summary>
        public long MarkedCount => _marked;

        public bool Contains(uint id) => _entries.ContainsKey(id);

        /// <summary>The class an id waits with (false when it does not wait).</summary>
        public bool TryGetClass(uint id, out DeferredClass cls)
        {
            if (_entries.TryGetValue(id, out Entry e))
            {
                cls = e.Class;
                return true;
            }
            cls = 0;
            return false;
        }

        /// <summary>Queues the id (or upgrades its class when it already waits; its place in line is kept).</summary>
        public void Mark(uint id, DeferredClass cls, double now)
        {
            _marked++;
            if (_entries.TryGetValue(id, out Entry e))
            {
                _coalesced++;
                if (cls > e.Class)
                {
                    e.Class = cls;
                    _entries[id] = e;
                }
                return;
            }
            uint serial = ++_serial;
            _entries[id] = new Entry { Class = cls, Serial = serial, Since = now };
            _order.Enqueue((id, serial));
            if (_order.Count > 2 * _entries.Count + 1024) Compact();
        }

        /// <summary>Drops the id; false when it did not wait.</summary>
        public bool Remove(uint id) => _entries.Remove(id);

        /// <summary>Drops the id only when it waits with a class not stronger than <paramref name="atMost"/>.</summary>
        public bool RemoveIfAtMost(uint id, DeferredClass atMost)
        {
            if (!_entries.TryGetValue(id, out Entry e) || e.Class > atMost) return false;
            _entries.Remove(id);
            return true;
        }

        /// <summary>Takes the oldest waiting entry; false when the queue is empty.</summary>
        public bool TryTake(out uint id, out DeferredClass cls, out double since)
        {
            while (_order.Count > 0)
            {
                (uint candidate, uint serial) = _order.Dequeue();
                if (!_entries.TryGetValue(candidate, out Entry e) || e.Serial != serial) continue;
                _entries.Remove(candidate);
                id = candidate;
                cls = e.Class;
                since = e.Since;
                return true;
            }
            id = 0;
            cls = 0;
            since = 0;
            return false;
        }

        /// <summary>The time the oldest waiting entry became dirty, or <paramref name="none"/> when the queue is empty.</summary>
        public double OldestSince(double none)
        {
            while (_order.Count > 0)
            {
                (uint id, uint serial) = _order.Peek();
                if (_entries.TryGetValue(id, out Entry e) && e.Serial == serial) return e.Since;
                _order.Dequeue();
            }
            return none;
        }

        public void Clear()
        {
            _entries.Clear();
            _order.Clear();
        }

        private void Compact()
        {
            int n = _order.Count;
            for (int i = 0; i < n; i++)
            {
                (uint id, uint serial) = _order.Dequeue();
                if (_entries.TryGetValue(id, out Entry e) && e.Serial == serial) _order.Enqueue((id, serial));
            }
        }
    }

    /// <summary>Thresholds of <see cref="LinkBudget"/>.</summary>
    public sealed class LinkBudgetConfig
    {
        /// <summary>Deferred traffic is sent only while the peer's reliable backlog (outbox queue + transport pending)
        /// stays below this many seconds at its send rate.</summary>
        public double MaxBacklogSeconds = 0.25;

        /// <summary>... and below this many bytes whatever the rate (bounded outbox).</summary>
        public long MaxBacklogBytes = 512 * 1024;

        /// <summary>... but at least this many bytes may always be in flight (tiny or unknown rates).</summary>
        public long MinBacklogBytes = 16 * 1024;

        /// <summary>Send rate assumed while the transport reports none.</summary>
        public float AssumedRateBytesPerSecond = 128 * 1024;

        /// <summary>The peer counts as congested (motion yields) above this backlog in seconds.</summary>
        public double CongestedBacklogSeconds = 0.5;

        /// <summary>Smallest per-frame allowance when the link has room.</summary>
        public int MinFrameBytes = 1024;
    }

    /// <summary>
    /// How much deferrable traffic a peer may receive this frame (ARCH §4.8.4, simplified): structure (P0) is always
    /// queued; deferred values only fill the link up to a short backlog, so a slow link makes values stale instead of
    /// growing the reliable outbox. Pure functions.
    /// </summary>
    public static class LinkBudget
    {
        /// <summary>The rate used for the peer: the transport's estimate, or the assumed rate.</summary>
        public static double Rate(float sendRateBytesPerSecond, LinkBudgetConfig config)
        {
            return sendRateBytesPerSecond > 0 && !float.IsInfinity(sendRateBytesPerSecond) ? sendRateBytesPerSecond : config.AssumedRateBytesPerSecond;
        }

        /// <summary>The backlog (bytes) beyond which nothing deferrable is added.</summary>
        public static long AllowedBacklog(float sendRateBytesPerSecond, LinkBudgetConfig config)
        {
            double byRate = Rate(sendRateBytesPerSecond, config) * config.MaxBacklogSeconds;
            return (long)Math.Max(config.MinBacklogBytes, Math.Min(config.MaxBacklogBytes, byRate));
        }

        /// <summary>Bytes of deferred traffic allowed this frame (0 while the backlog is at or above the allowance).</summary>
        public static int DeferredBytes(long queuedReliableBytes, long pendingReliableBytes, float sendRateBytesPerSecond, double frameSeconds, LinkBudgetConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            long backlog = Math.Max(0, queuedReliableBytes) + Math.Max(0, pendingReliableBytes);
            long allowed = AllowedBacklog(sendRateBytesPerSecond, config);
            if (backlog >= allowed) return 0;
            double dt = frameSeconds > 0 && !double.IsInfinity(frameSeconds) ? Math.Min(frameSeconds, 0.25) : 1.0 / 60.0;
            double perFrame = Math.Max(config.MinFrameBytes, Rate(sendRateBytesPerSecond, config) * dt);
            return (int)Math.Min(allowed - backlog, Math.Min(perFrame, int.MaxValue));
        }

        /// <summary>True when the peer's reliable backlog exceeds <see cref="LinkBudgetConfig.CongestedBacklogSeconds"/> at
        /// its rate (at least twice <see cref="LinkBudgetConfig.MinBacklogBytes"/>): lower-priority streams (motion) yield.</summary>
        public static bool IsCongested(long queuedReliableBytes, long pendingReliableBytes, float sendRateBytesPerSecond, LinkBudgetConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            long backlog = Math.Max(0, queuedReliableBytes) + Math.Max(0, pendingReliableBytes);
            return backlog > Math.Max(2.0 * config.MinBacklogBytes, Rate(sendRateBytesPerSecond, config) * config.CongestedBacklogSeconds);
        }
    }
}
