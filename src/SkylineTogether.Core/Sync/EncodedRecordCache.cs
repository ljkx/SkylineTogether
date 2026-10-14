using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Records encoded once and appended to every batch that needs them in the same phase of a frame. The host sends
    /// each peer's deferred values from the same world state: with several peers, an entity whose value changed was
    /// serialized once per peer, and a rebaseline that several peers ask for once per request. Keys are an id and a
    /// record kind (<see cref="Key"/>); an entry holds the records exactly as one write produced them (an entity record
    /// and the enabled-bits record that follows it), so a hit is byte-identical to writing again.
    /// <para>
    /// The caller clears the cache whenever the state the records were written from may have changed (a new frame
    /// phase) and keeps everything that is written per batch (prefab records) out of it. Allocation-free once grown;
    /// stores nothing beyond <see cref="MaxBytes"/>. Not thread-safe.
    /// </para>
    /// </summary>
    public sealed class EncodedRecordCache
    {
        /// <summary>Encoded bytes kept at most (larger frames write the rest per batch, as before).</summary>
        public const int MaxBytes = 8 << 20;

        private struct Entry
        {
            public int Offset;
            public int Length;
            public int Records;
        }

        private readonly Dictionary<ulong, Entry> _entries = new Dictionary<ulong, Entry>();
        private byte[] _bytes = new byte[16 * 1024];
        private int _length;

        /// <summary>Entries stored since the last <see cref="Clear"/>.</summary>
        public int Count => _entries.Count;

        /// <summary>Appends served from the cache (lifetime).</summary>
        public long Hits { get; private set; }

        /// <summary>Entries stored (lifetime).</summary>
        public long Stores { get; private set; }

        /// <summary>The key of an id's record of one kind.</summary>
        public static ulong Key(uint netId, SyncRecordKind kind) => ((ulong)(byte)kind << 32) | netId;

        /// <summary>Appends the cached records of <paramref name="key"/> to <paramref name="w"/>; false when there are none.</summary>
        public bool TryAppend(ulong key, SyncBatch.Writer w)
        {
            if (!_entries.TryGetValue(key, out Entry e)) return false;
            w.AppendEncoded(_bytes, e.Offset, e.Length, e.Records);
            Hits++;
            return true;
        }

        /// <summary>
        /// Stores what <paramref name="w"/> received since <paramref name="startLength"/> / <paramref name="startRecords"/>
        /// (its <see cref="SyncBatch.Writer.Length"/> and <see cref="SyncBatch.Writer.RecordCount"/> before the write).
        /// </summary>
        public void Store(ulong key, SyncBatch.Writer w, int startLength, int startRecords)
        {
            int length = w.Length - startLength;
            int records = w.RecordCount - startRecords;
            if (records <= 0 || length <= 0 || _length + length > MaxBytes) return;
            if (_bytes.Length < _length + length) Array.Resize(ref _bytes, Math.Min(MaxBytes, Math.Max(_length + length, _bytes.Length * 2)));
            w.CopyBytes(startLength, length, _bytes, _length);
            _entries[key] = new Entry { Offset = _length, Length = length, Records = records };
            _length += length;
            Stores++;
        }

        /// <summary>Forgets every entry (the state they were written from changed).</summary>
        public void Clear()
        {
            if (_entries.Count == 0) return;
            _entries.Clear();
            _length = 0;
        }
    }
}
