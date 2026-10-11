using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Threading;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Identity
{
    /// <summary>
    /// The session-global, append-only dictionary of replicated signatures (ARCH §3.6): sorted sets of type ranks
    /// coded as <see cref="TypeSetId"/>s from 1 upwards. The host assigns ids (<see cref="GetOrAdd"/>) and emits a
    /// TYPESET record before an id's first use; clients learn ids from those records (<see cref="Define"/>) and from
    /// the type-set keyframe (<see cref="ReadKeyframe"/>) that precedes the epoch log for joiners and snapshot resyncs.
    /// A set holds at most 64 types (its ordinals are the bits of dirty masks and enabled bits).
    /// <para>
    /// <b>Roles.</b> Ids are host-assigned. Once a registry has learnt a definition from outside (a <see cref="Define"/>
    /// or keyframe), it is a replica and <see cref="GetOrAdd"/> refuses to invent ids for unknown signatures
    /// (<see cref="InvalidOperationException"/>), because a client-made id would later contradict the host's
    /// definition of the same id and force a resync loop. Clients look signatures up with <see cref="TryFind"/>.
    /// </para>
    /// <para>
    /// <b>Keyframe encoding</b> (TSET section, ARCH §4.6.3 <c>TSETKEY</c>): <c>varint count</c>, then per set in
    /// ascending id order <c>varint typeSetId</c>, <c>varint n</c>, <c>n × varint typeRank</c> (ranks strictly
    /// ascending). The per-set part is the TYPESET record's layout. <see cref="ReadKeyframe"/> validates everything
    /// (ids ascending and in range, at most 64 ranks, ranks ascending and inside the catalog, no contradiction with a
    /// known definition) before it changes anything, so a rejected keyframe leaves the registry unchanged.
    /// </para>
    /// <para>
    /// <b>Threading.</b> Thread-safe: on clients the network thread (TSET), the epoch merger (TYPESET records) and the
    /// main thread (planning) use one registry concurrently. Lookups (<see cref="TryGet"/>, <see cref="Get"/>,
    /// <see cref="OrdinalOf"/>, <see cref="Count"/>) take no lock and see every definition published before them;
    /// mutations are serialised by a lock.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class TypeSetRegistry : ITypeSetRegistry
    {
        /// <summary>Most types in one set (ordinals are below 64).</summary>
        public const int MaxTypesPerSet = 64;

        /// <summary>Highest accepted id; larger ids in records or keyframes are rejected (resync), never allocated.</summary>
        public const int MaxTypeSetId = 1 << 20;

        private static readonly ReadOnlyCollection<TypeRank> EmptyView = new ReadOnlyCollection<TypeRank>(Array.Empty<TypeRank>());

        private enum Role : byte { Unset, Authority, Replica }

        private sealed class Entry
        {
            public readonly int Id;
            public readonly TypeRank[] Types;
            public readonly ReadOnlyCollection<TypeRank> View;
            public readonly int Hash;

            public Entry(int id, TypeRank[] types, int hash)
            {
                Id = id;
                Types = types;
                View = new ReadOnlyCollection<TypeRank>(types);
                Hash = hash;
            }
        }

        private readonly struct ContentKey
        {
            public readonly IReadOnlyList<TypeRank> Types;
            public readonly int Hash;

            public ContentKey(IReadOnlyList<TypeRank> types, int hash)
            {
                Types = types;
                Hash = hash;
            }
        }

        private sealed class ContentKeyComparer : IEqualityComparer<ContentKey>
        {
            public bool Equals(ContentKey x, ContentKey y)
            {
                if (x.Hash != y.Hash) return false;
                int n = x.Types.Count;
                if (n != y.Types.Count) return false;
                for (int i = 0; i < n; i++)
                {
                    if (x.Types[i].Value != y.Types[i].Value) return false;
                }
                return true;
            }

            public int GetHashCode(ContentKey key) => key.Hash;
        }

        private readonly struct ParsedSet
        {
            public readonly int Id;
            public readonly TypeRank[] Types;

            public ParsedSet(int id, TypeRank[] types)
            {
                Id = id;
                Types = types;
            }
        }

        private readonly ITypeCatalog _catalog;
        private readonly ILog _log;
        private readonly object _lock = new object();
        private readonly Dictionary<ContentKey, int> _byContent = new Dictionary<ContentKey, int>(new ContentKeyComparer());
        private Entry[] _entries = new Entry[64];
        private int _count;
        private int _maxId;
        private Role _role;
        private long _rejected;
        private string _lastRejection;

        public TypeSetRegistry(ITypeCatalog catalog) : this(catalog, null)
        {
        }

        /// <param name="catalog">The session's type catalog; ranks are validated against its count.</param>
        /// <param name="log">Receives rejected definitions (category <see cref="LogCategory.Replication"/>); may be null.</param>
        public TypeSetRegistry(ITypeCatalog catalog, ILog log)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _log = log;
        }

        /// <summary>Number of defined sets.</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>The highest defined id (0 when empty); the host's next id is one above it.</summary>
        public int HighestId
        {
            get
            {
                lock (_lock) return _maxId;
            }
        }

        /// <summary>True once the registry learnt a definition from outside (client); see the class remarks.</summary>
        public bool IsReplica
        {
            get
            {
                lock (_lock) return _role == Role.Replica;
            }
        }

        /// <summary>Definitions and keyframes rejected since construction or <see cref="Clear"/> (each one is a resync condition).</summary>
        public long RejectedCount => Interlocked.Read(ref _rejected);

        /// <summary>The reason of the last rejection, or null.</summary>
        public string LastRejection => Volatile.Read(ref _lastRejection);

        /// <summary>
        /// Host: the id of a sorted signature; <paramref name="added"/> = true when it is new (a TYPESET record must
        /// precede its first use). Throws <see cref="ArgumentException"/> for an invalid signature (null, unsorted,
        /// duplicate or unknown ranks, more than 64 types) and <see cref="InvalidOperationException"/> on a replica for an
        /// unknown signature or when the ids are exhausted (see <see cref="TryGetOrAdd"/>).
        /// </summary>
        public TypeSetId GetOrAdd(IReadOnlyList<TypeRank> sortedTypes, out bool added)
        {
            string invalid = Validate(sortedTypes);
            if (invalid != null) throw new ArgumentException(invalid, nameof(sortedTypes));
            if (TryGetOrAddCore(sortedTypes, out TypeSetId id, out added, out string error)) return id;
            throw new InvalidOperationException(error);
        }

        /// <summary>As <see cref="GetOrAdd"/>, but reports every failure through <paramref name="error"/> instead of throwing.</summary>
        public bool TryGetOrAdd(IReadOnlyList<TypeRank> sortedTypes, out TypeSetId id, out bool added, out string error)
        {
            error = Validate(sortedTypes);
            if (error != null)
            {
                id = TypeSetId.None;
                added = false;
                return false;
            }
            return TryGetOrAddCore(sortedTypes, out id, out added, out error);
        }

        /// <summary>The id of a known signature without adding it (clients); false for unknown or invalid signatures.</summary>
        public bool TryFind(IReadOnlyList<TypeRank> sortedTypes, out TypeSetId id)
        {
            if (Validate(sortedTypes) == null)
            {
                var probe = new ContentKey(sortedTypes, HashOf(sortedTypes));
                lock (_lock)
                {
                    if (_byContent.TryGetValue(probe, out int value))
                    {
                        id = new TypeSetId(value);
                        return true;
                    }
                }
            }
            id = TypeSetId.None;
            return false;
        }

        /// <summary>The types of a known set (read-only); false and an empty list for an unknown id (a resync condition on clients).</summary>
        public bool TryGet(TypeSetId id, out IReadOnlyList<TypeRank> types)
        {
            Entry entry = Lookup(id.Value);
            if (entry == null)
            {
                types = EmptyView;
                return false;
            }
            types = entry.View;
            return true;
        }

        /// <summary>The types of a known set; throws <see cref="UnknownTypeSetException"/> for an unknown id.</summary>
        public IReadOnlyList<TypeRank> Get(TypeSetId id)
        {
            Entry entry = Lookup(id.Value);
            if (entry == null) throw new UnknownTypeSetException(id);
            return entry.View;
        }

        /// <summary>The ordinal (position) of <paramref name="type"/> in the set: its bit in dirty masks and enabled
        /// bits, below 64. -1 when the set is unknown or does not contain the type.</summary>
        public int OrdinalOf(TypeSetId set, TypeRank type)
        {
            Entry entry = Lookup(set.Value);
            if (entry == null) return -1;
            TypeRank[] types = entry.Types;
            int lo = 0;
            int hi = types.Length - 1;
            ushort wanted = type.Value;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                ushort value = types[mid].Value;
                if (value == wanted) return mid;
                if (value < wanted) lo = mid + 1;
                else hi = mid - 1;
            }
            return -1;
        }

        /// <summary>
        /// Client: a TYPESET record or keyframe entry. Defining a known id again with identical types succeeds; a
        /// contradicting or invalid definition returns false (a resync condition, ARCH §12.2) and is counted and logged.
        /// </summary>
        public bool Define(TypeSetId id, IReadOnlyList<TypeRank> sortedTypes) => TryDefine(id, sortedTypes, out _);

        /// <summary>As <see cref="Define"/>, with the reason of a rejection.</summary>
        public bool TryDefine(TypeSetId id, IReadOnlyList<TypeRank> sortedTypes, out string error)
        {
            if (id.Value <= 0 || id.Value > MaxTypeSetId)
            {
                error = "type-set id " + id.Value.ToString(CultureInfo.InvariantCulture) + " is outside 1 ... " + MaxTypeSetId.ToString(CultureInfo.InvariantCulture);
                Reject(error);
                return false;
            }
            error = Validate(sortedTypes);
            if (error != null)
            {
                error = "definition of " + id + " rejected: " + error;
                Reject(error);
                return false;
            }
            lock (_lock)
            {
                Entry existing = LookupLocked(id.Value);
                if (existing != null)
                {
                    if (SameTypes(existing.Types, sortedTypes))
                    {
                        error = null;
                        return true;
                    }
                    error = Contradiction(id.Value, existing.Types, sortedTypes);
                }
                else
                {
                    if (_role == Role.Unset) _role = Role.Replica;
                    Publish(new Entry(id.Value, Copy(sortedTypes), HashOf(sortedTypes)));
                    error = null;
                    return true;
                }
            }
            Reject(error);
            return false;
        }

        /// <summary>Writes the whole registry as a keyframe (see the class remarks for the encoding).</summary>
        public void WriteKeyframe(ByteWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            lock (_lock)
            {
                Entry[] entries = _entries;
                writer.WriteVarUInt32((uint)_count);
                for (int id = 1; id <= _maxId; id++)
                {
                    Entry entry = entries[id];
                    if (entry == null) continue;
                    writer.WriteVarUInt32((uint)id);
                    writer.WriteVarUInt32((uint)entry.Types.Length);
                    foreach (TypeRank type in entry.Types) writer.WriteVarUInt32(type.Value);
                }
            }
        }

        /// <summary>
        /// Loads a keyframe into the registry (merging with known identical definitions). Throws
        /// <see cref="DecodeException"/> for malformed input or a contradiction; the registry is then unchanged.
        /// </summary>
        public void ReadKeyframe(ByteReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            List<ParsedSet> parsed;
            try
            {
                parsed = ParseKeyframe(reader);
            }
            catch (DecodeException e)
            {
                // malformed content and overruns inside ByteReader alike: counted and logged, the registry is unchanged
                Reject("type-set keyframe rejected: " + e.Message);
                throw;
            }

            lock (_lock)
            {
                foreach (ParsedSet set in parsed)
                {
                    Entry existing = LookupLocked(set.Id);
                    if (existing != null && !SameTypes(existing.Types, set.Types))
                    {
                        throw RejectDecode("type-set keyframe: " + Contradiction(set.Id, existing.Types, set.Types));
                    }
                }
                foreach (ParsedSet set in parsed)
                {
                    if (LookupLocked(set.Id) == null) Publish(new Entry(set.Id, set.Types, HashOf(set.Types)));
                }
                if (_role == Role.Unset && parsed.Count > 0) _role = Role.Replica;
            }
        }

        /// <summary>Parses and validates a keyframe without touching the registry (see <see cref="ReadKeyframe"/>).</summary>
        private List<ParsedSet> ParseKeyframe(ByteReader reader)
        {
            uint count = reader.ReadVarUInt32();
            if (count > MaxTypeSetId || count > (uint)reader.Remaining / 2)
            {
                throw new DecodeException("type-set keyframe declares " + count.ToString(CultureInfo.InvariantCulture) + " sets, more than its "
                                   + reader.Remaining.ToString(CultureInfo.InvariantCulture) + " remaining bytes can hold");
            }
            int catalogCount = _catalog.Count;
            var parsed = new List<ParsedSet>((int)count);
            uint previousId = 0;
            for (uint i = 0; i < count; i++)
            {
                uint id = reader.ReadVarUInt32();
                if (id == 0 || id > MaxTypeSetId) throw new DecodeException("type-set keyframe entry " + i.ToString(CultureInfo.InvariantCulture) + " has the invalid id " + id.ToString(CultureInfo.InvariantCulture));
                if (id <= previousId) throw new DecodeException("type-set keyframe ids are not strictly ascending at id " + id.ToString(CultureInfo.InvariantCulture));
                previousId = id;
                uint n = reader.ReadVarUInt32();
                if (n > MaxTypesPerSet) throw new DecodeException("type set " + id.ToString(CultureInfo.InvariantCulture) + " has " + n.ToString(CultureInfo.InvariantCulture) + " types, more than " + MaxTypesPerSet.ToString(CultureInfo.InvariantCulture));
                if (n > (uint)reader.Remaining) throw new DecodeException("type set " + id.ToString(CultureInfo.InvariantCulture) + " overruns the keyframe");
                var types = new TypeRank[n];
                long previousRank = -1;
                for (int j = 0; j < n; j++)
                {
                    uint rank = reader.ReadVarUInt32();
                    if (rank >= (uint)catalogCount) throw new DecodeException("type set " + id.ToString(CultureInfo.InvariantCulture) + " names type rank " + rank.ToString(CultureInfo.InvariantCulture) + " outside the catalog of " + catalogCount.ToString(CultureInfo.InvariantCulture) + " types");
                    if (rank <= previousRank) throw new DecodeException("type set " + id.ToString(CultureInfo.InvariantCulture) + " is not strictly ascending");
                    previousRank = rank;
                    types[j] = new TypeRank((ushort)rank);
                }
                parsed.Add(new ParsedSet((int)id, types));
            }
            return parsed;
        }

        /// <summary>
        /// Drops every definition and the role (before a snapshot resync or a new session). Call it while nothing decodes
        /// against the registry (the engine is being reset): a lookup that races with it may report a known id as unknown.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                Volatile.Write(ref _entries, new Entry[64]);
                _byContent.Clear();
                Volatile.Write(ref _count, 0);
                _maxId = 0;
                _role = Role.Unset;
                Interlocked.Exchange(ref _rejected, 0);
                Volatile.Write(ref _lastRejection, null);
            }
        }

        /// <summary>A short text of a set for diagnostics, e.g. <c>"typeset:3 {1, 4, 9}"</c>.</summary>
        public string Describe(TypeSetId id)
        {
            Entry entry = Lookup(id.Value);
            return entry == null ? id + " (unknown)" : id + " " + Format(entry.Types);
        }

        public override string ToString() => "TypeSetRegistry(" + Count.ToString(CultureInfo.InvariantCulture) + " sets)";

        // ------------------------------------------------------------------ internals

        private bool TryGetOrAddCore(IReadOnlyList<TypeRank> sortedTypes, out TypeSetId id, out bool added, out string error)
        {
            var probe = new ContentKey(sortedTypes, HashOf(sortedTypes));
            lock (_lock)
            {
                if (_byContent.TryGetValue(probe, out int existing))
                {
                    id = new TypeSetId(existing);
                    added = false;
                    error = null;
                    return true;
                }
                if (_role == Role.Replica)
                {
                    error = "this type-set registry is a replica (it learnt host definitions) and cannot add " + Format(sortedTypes)
                            + "; ids are host-assigned, use TryFind";
                }
                else if (_maxId >= MaxTypeSetId)
                {
                    error = "the type-set ids are exhausted (" + MaxTypeSetId.ToString(CultureInfo.InvariantCulture) + ")";
                }
                else
                {
                    _role = Role.Authority;
                    int newId = _maxId + 1;
                    Publish(new Entry(newId, Copy(sortedTypes), probe.Hash));
                    id = new TypeSetId(newId);
                    added = true;
                    error = null;
                    return true;
                }
            }
            id = TypeSetId.None;
            added = false;
            return false;
        }

        private Entry Lookup(int id)
        {
            if (id <= 0) return null;
            Entry[] entries = Volatile.Read(ref _entries);
            return id < entries.Length ? Volatile.Read(ref entries[id]) : null;
        }

        private Entry LookupLocked(int id)
        {
            Entry[] entries = _entries;
            return id > 0 && id < entries.Length ? entries[id] : null;
        }

        /// <summary>Publishes a new entry; the caller holds the lock and has checked that the id is free.</summary>
        private void Publish(Entry entry)
        {
            Entry[] entries = _entries;
            if (entry.Id >= entries.Length)
            {
                int length = (int)Math.Min(Math.Max((long)entry.Id + 1, (long)entries.Length * 2), (long)MaxTypeSetId + 1);
                var grown = new Entry[length];
                Array.Copy(entries, grown, entries.Length);
                grown[entry.Id] = entry;
                Volatile.Write(ref _entries, grown);
            }
            else
            {
                Volatile.Write(ref entries[entry.Id], entry);
            }
            Volatile.Write(ref _count, _count + 1);
            if (entry.Id > _maxId) _maxId = entry.Id;
            var key = new ContentKey(entry.Types, entry.Hash);
            if (!_byContent.ContainsKey(key)) _byContent.Add(key, entry.Id);
        }

        private string Validate(IReadOnlyList<TypeRank> sortedTypes)
        {
            if (sortedTypes == null) return "the type list is null";
            int n = sortedTypes.Count;
            if (n > MaxTypesPerSet)
            {
                return n.ToString(CultureInfo.InvariantCulture) + " types exceed the " + MaxTypesPerSet.ToString(CultureInfo.InvariantCulture) + " ordinals of a type set";
            }
            int catalogCount = _catalog.Count;
            int previous = -1;
            for (int i = 0; i < n; i++)
            {
                int rank = sortedTypes[i].Value;
                if (rank >= catalogCount)
                {
                    return "type rank " + rank.ToString(CultureInfo.InvariantCulture) + " is outside the catalog of " + catalogCount.ToString(CultureInfo.InvariantCulture) + " types";
                }
                if (rank <= previous) return "the type ranks are not strictly ascending at position " + i.ToString(CultureInfo.InvariantCulture);
                previous = rank;
            }
            return null;
        }

        private void Reject(string reason)
        {
            Interlocked.Increment(ref _rejected);
            Volatile.Write(ref _lastRejection, reason);
            _log?.Warn(LogCategory.Replication, "type sets: " + reason);
        }

        private DecodeException RejectDecode(string reason)
        {
            Reject(reason);
            return new DecodeException(reason);
        }

        private static TypeRank[] Copy(IReadOnlyList<TypeRank> types)
        {
            var copy = new TypeRank[types.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = types[i];
            return copy;
        }

        private static bool SameTypes(TypeRank[] known, IReadOnlyList<TypeRank> other)
        {
            if (known.Length != other.Count) return false;
            for (int i = 0; i < known.Length; i++)
            {
                if (known[i].Value != other[i].Value) return false;
            }
            return true;
        }

        private static int HashOf(IReadOnlyList<TypeRank> types)
        {
            unchecked
            {
                uint h = 2166136261u ^ (uint)types.Count;
                for (int i = 0; i < types.Count; i++)
                {
                    h = (h ^ types[i].Value) * 16777619u;
                }
                return (int)(h ^ (h >> 15));
            }
        }

        private static string Contradiction(int id, TypeRank[] known, IReadOnlyList<TypeRank> other)
        {
            return "typeset:" + id.ToString(CultureInfo.InvariantCulture) + " is defined as " + Format(known) + "; the definition "
                   + Format(other) + " contradicts it";
        }

        private static string Format(IReadOnlyList<TypeRank> types)
        {
            var sb = new StringBuilder("{");
            int shown = Math.Min(types.Count, 16);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(types[i].Value.ToString(CultureInfo.InvariantCulture));
            }
            if (types.Count > shown) sb.Append(", ... (").Append(types.Count.ToString(CultureInfo.InvariantCulture)).Append(" types)");
            return sb.Append('}').ToString();
        }
    }
}
