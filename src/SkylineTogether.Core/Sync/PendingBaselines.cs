using System.Collections.Generic;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Client: net ids whose converged state still has to be hashed as the baseline of the player's edits. The applier
    /// writes thousands of entities while a player only watches; hashing each of them right away (a full serialization
    /// per entity, every frame it is written) was the client's largest capture cost. They are marked here instead and
    /// hashed later: all at once right before a tool may edit them, or a budgeted slice per frame otherwise. An id
    /// marked again before it was taken is hashed once. First marked, first taken. Allocation-free once grown; not
    /// thread-safe.
    /// </summary>
    public sealed class PendingBaselines
    {
        private readonly HashSet<uint> _pending = new HashSet<uint>();
        private readonly Queue<uint> _order = new Queue<uint>();

        /// <summary>Ids waiting.</summary>
        public int Count => _pending.Count;

        public bool Contains(uint id) => _pending.Contains(id);

        /// <summary>The id's state changed and needs a new baseline.</summary>
        public void Mark(uint id)
        {
            if (id == 0 || !_pending.Add(id)) return;
            _order.Enqueue(id);
            // ids removed while queued stay in the order queue until taken; keep it from growing without bound
            if (_order.Count > 4 * _pending.Count + 1024) Compact();
        }

        /// <summary>The id got its baseline another way (or is gone): it no longer waits.</summary>
        public bool Remove(uint id) => _pending.Remove(id);

        /// <summary>The next waiting id (oldest first); false when none waits.</summary>
        public bool TryTake(out uint id)
        {
            while (_order.Count > 0)
            {
                id = _order.Dequeue();
                if (_pending.Remove(id)) return true;
            }
            id = 0;
            return false;
        }

        public void Clear()
        {
            _pending.Clear();
            _order.Clear();
        }

        private void Compact()
        {
            int n = _order.Count;
            for (int i = 0; i < n; i++)
            {
                uint id = _order.Dequeue();
                if (_pending.Contains(id)) _order.Enqueue(id); // a duplicate entry is skipped by TryTake
            }
        }
    }
}
