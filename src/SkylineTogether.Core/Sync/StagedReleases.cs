using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Host: net ids whose despawn is written into a batch that has not been handed to the peers yet
    /// (ARCHITECTURE.md#net-ids). The quarantine of such an id may only start once that batch went out: an id released
    /// earlier and then lost with its batch (an exception later in the frame's capture, a batch that was never sent)
    /// would be freed while every client still holds the old binding, and its reuse would turn the next spawn into a
    /// write to the wrong entity.
    /// <list type="bullet">
    /// <item><see cref="Stage"/>: the despawn record is in the batch being built (the id stays allocated);</item>
    /// <item><see cref="Commit"/>: the batch was handed to every peer; each staged id starts its quarantine;</item>
    /// <item>a frame that ends without a send keeps its ids staged; the next batch writes their despawns again
    /// (<see cref="Rewrite(Action{uint})"/>), so a despawn is never lost and its id never freed without it;</item>
    /// <item>a part of the batch that is rolled back (<see cref="SyncBatch.Writer.RollBack"/>) rewrites the despawns
    /// staged since its mark (<see cref="Rewrite(Action{uint}, int)"/>).</item>
    /// </list>
    /// Main thread. Not thread-safe.
    /// </summary>
    public sealed class StagedReleases
    {
        private readonly List<uint> _ids = new List<uint>();
        private readonly HashSet<uint> _set = new HashSet<uint>();

        /// <summary>Ids staged and not committed.</summary>
        public int Count => _ids.Count;

        /// <summary>The current position (for <see cref="Rewrite(Action{uint}, int)"/> after a partial rollback).</summary>
        public int Mark => _ids.Count;

        /// <summary>Total despawns written again because their batch was not sent or was rolled back (diagnostics).</summary>
        public long Rewritten { get; private set; }

        public bool Contains(uint id) => _set.Contains(id);

        /// <summary>The despawn of <paramref name="id"/> is in the batch being built. Staging an id twice is ignored.</summary>
        public void Stage(uint id)
        {
            if (id == 0 || !_set.Add(id)) return;
            _ids.Add(id);
        }

        /// <summary>
        /// The batch carrying every staged despawn was handed to the peers: the ids enter the quarantine of
        /// <paramref name="allocator"/>. Returns the number released.
        /// </summary>
        public int Commit(NetIdAllocator allocator)
        {
            if (allocator == null) throw new ArgumentNullException(nameof(allocator));
            int released = 0;
            foreach (uint id in _ids)
            {
                if (allocator.Release(id)) released++;
            }
            _ids.Clear();
            _set.Clear();
            return released;
        }

        /// <summary>Writes the despawn of every staged id again (a new batch after a frame that sent nothing).</summary>
        public void Rewrite(Action<uint> writeDespawn) => Rewrite(writeDespawn, 0);

        /// <summary>Writes the despawns staged since <paramref name="mark"/> again (that part of the batch was rolled back).</summary>
        public void Rewrite(Action<uint> writeDespawn, int mark)
        {
            if (writeDespawn == null) throw new ArgumentNullException(nameof(writeDespawn));
            if (mark < 0) mark = 0;
            for (int i = mark; i < _ids.Count; i++)
            {
                writeDespawn(_ids[i]);
                Rewritten++;
            }
        }

        /// <summary>Forgets everything (session start and end: no peer holds an id any more).</summary>
        public void Clear()
        {
            _ids.Clear();
            _set.Clear();
        }
    }
}
