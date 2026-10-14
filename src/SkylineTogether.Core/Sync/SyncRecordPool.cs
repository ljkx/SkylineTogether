using System.Collections.Generic;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Reusable decode storage for <see cref="SyncBatch.Read(byte[], int, int, SyncRecordPool)"/> and
    /// <see cref="SimValueMessage.Read(byte[], int, int, SyncRecordPool, out byte, out uint, out uint)"/>: the record
    /// objects, their component lists, the list of records and the byte reader are kept and reset for the next decode,
    /// so decoding entity, despawn, epoch and clock records allocates nothing once the pool has grown to the usual
    /// batch size (a fresh decode allocated a record, a component list and its growth per record, plus the list).
    /// <para>
    /// Lifetime: the records a decode returns belong to the pool and stay valid until the next decode with the same
    /// pool. A consumer that keeps something beyond that keeps the per-kind payloads (arrays and lists of the rarer
    /// record kinds are allocated fresh by every decode), never a <see cref="SyncRecord"/> or its
    /// <see cref="SyncRecord.Components"/>. One pool per decode site; not thread-safe.
    /// </para>
    /// </summary>
    public sealed class SyncRecordPool
    {
        /// <summary>Records kept for reuse at most (a huge catch-up batch decodes the rest into fresh objects).</summary>
        public const int MaxPooledRecords = 16384;

        /// <summary>Component slots one pooled record keeps at most (a larger list is replaced, not kept).</summary>
        public const int MaxPooledComponents = 256;

        private readonly List<SyncRecord> _records = new List<SyncRecord>();
        private readonly List<SyncRecord> _output = new List<SyncRecord>();
        private int _used;

        internal readonly ByteReader Reader = new ByteReader();

        /// <summary>Record objects created by this pool so far (for tests and diagnostics).</summary>
        public int Created { get; private set; }

        /// <summary>Starts a decode: the output list is emptied and every pooled record is free again.</summary>
        internal List<SyncRecord> Begin()
        {
            _output.Clear();
            _used = 0;
            return _output;
        }

        /// <summary>A reset record, appended to the output list.</summary>
        internal SyncRecord Take()
        {
            SyncRecord r;
            if (_used < _records.Count)
            {
                r = _records[_used];
                r.Reset();
                if (r.Components.Capacity > MaxPooledComponents) r.Components.Capacity = 0;
                _used++;
            }
            else
            {
                r = new SyncRecord();
                Created++;
                if (_records.Count < MaxPooledRecords)
                {
                    _records.Add(r);
                    _used++;
                }
            }
            _output.Add(r);
            return r;
        }
    }
}
