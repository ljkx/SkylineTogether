using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Identity
{
    /// <summary>
    /// Prefab ranks (ARCH §3.5): every prefab entity of this peer (including disabled ones), sorted ordinally by
    /// <see cref="PrefabIdentity.Key"/>; <see cref="PrefabRank"/> = position in that list. Equal <see cref="Digest"/>s on
    /// two peers mean equal ranks, so no prefab table is ever transferred.
    /// <para>
    /// Duplicates (two prefab entities with one <c>PrefabID</c>: the game logs a warning and the first registration
    /// wins, <c>Game.Prefabs/PrefabSystem.cs:151-176</c>) keep a rank each and are part of the digest input, so a
    /// load-order difference shows up as a mismatch instead of silent shadowing; they are listed in
    /// <see cref="Duplicates"/>. Ranks among identical keys are ordered by source hint and then by the machine-local
    /// entity index, so their mapping may differ between machines: that is why they are reported.
    /// </para>
    /// <para>
    /// <see cref="Digest"/> = xxHash64 (seed 0) over <c>varint count</c>, then per rank <c>varint utf8Length</c> and the
    /// UTF-8 bytes of the key. <see cref="ClassSignatureDigest"/> = xxHash64 over <c>varint count</c> and the class
    /// signatures (u64 little-endian) in rank order. Immutable once built; any thread may read it.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class PrefabIndex : IPrefabIndex
    {
        private const int DenseEntityIndexLimit = 1 << 20;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        private readonly PrefabIdentity[] _sorted;
        private readonly string[] _keys;
        private readonly ulong[] _signatures;
        private readonly Dictionary<string, int> _rankByKey;
        private readonly int[] _rankByEntityIndexDense;          // rank + 1; 0 = not a prefab
        private readonly Dictionary<int, int> _rankByEntityIndexSparse;
        private readonly ReadOnlyCollection<PrefabIdentity> _sortedView;
        private readonly ReadOnlyCollection<string> _duplicates;
        private readonly ReadOnlyCollection<string> _duplicateDetails;
        private readonly ReadOnlyCollection<string> _problems;

        private struct Item
        {
            public string Key;
            public string Hint;
            public int EntityIndex;
            public int Original;
        }

        private PrefabIndex(PrefabIdentity[] sorted, string[] keys, ulong[] signatures, Dictionary<string, int> rankByKey,
                            int[] dense, Dictionary<int, int> sparse, List<string> duplicates, List<string> duplicateDetails,
                            List<string> problems, ulong digest, ulong classDigest)
        {
            _sorted = sorted;
            _keys = keys;
            _signatures = signatures;
            _rankByKey = rankByKey;
            _rankByEntityIndexDense = dense;
            _rankByEntityIndexSparse = sparse;
            _sortedView = new ReadOnlyCollection<PrefabIdentity>(sorted);
            _duplicates = duplicates.AsReadOnly();
            _duplicateDetails = duplicateDetails.AsReadOnly();
            _problems = problems.AsReadOnly();
            Digest = digest;
            ClassSignatureDigest = classDigest;
        }

        /// <summary>
        /// Builds the index. <paramref name="classSignatures"/> is parallel to <paramref name="prefabs"/> (entry i belongs
        /// to prefab i, in input order) and may be null (all signatures 0). Throws for a null prefab list or a signature
        /// list of a different length. Entity indices below 0 are not mapped; an entity index given twice keeps its first
        /// rank and is reported in <see cref="Problems"/>.
        /// </summary>
        public static PrefabIndex Build(IReadOnlyList<PrefabIdentity> prefabs, IReadOnlyList<ulong> classSignatures)
        {
            return Build(prefabs, classSignatures, null);
        }

        /// <summary>
        /// As <see cref="Build(IReadOnlyList{PrefabIdentity}, IReadOnlyList{ulong})"/>, and reports the result to
        /// <paramref name="log"/> (category <see cref="LogCategory.Join"/>): a summary with the digest, every duplicate
        /// key with its sources (warnings, the first <see cref="MaxLoggedFindings"/>) and every input problem.
        /// </summary>
        public static PrefabIndex Build(IReadOnlyList<PrefabIdentity> prefabs, IReadOnlyList<ulong> classSignatures, ILog log)
        {
            PrefabIndex index = BuildCore(prefabs, classSignatures);
            if (log != null) index.Report(log);
            return index;
        }

        /// <summary>Most duplicate or problem lines one <see cref="Build(IReadOnlyList{PrefabIdentity}, IReadOnlyList{ulong}, ILog)"/> logs.</summary>
        public const int MaxLoggedFindings = 20;

        private static PrefabIndex BuildCore(IReadOnlyList<PrefabIdentity> prefabs, IReadOnlyList<ulong> classSignatures)
        {
            if (prefabs == null) throw new ArgumentNullException(nameof(prefabs));
            int n = prefabs.Count;
            if (classSignatures != null && classSignatures.Count != n)
            {
                throw new ArgumentException("classSignatures has " + classSignatures.Count.ToString(CultureInfo.InvariantCulture)
                                            + " entries for " + n.ToString(CultureInfo.InvariantCulture) + " prefabs", nameof(classSignatures));
            }

            var items = new Item[n];
            for (int i = 0; i < n; i++)
            {
                PrefabIdentity p = prefabs[i];
                items[i] = new Item { Key = p.Key, Hint = p.SourceHint ?? string.Empty, EntityIndex = p.EntityIndex, Original = i };
            }
            Array.Sort(items, CompareItems);

            var sorted = new PrefabIdentity[n];
            var keys = new string[n];
            var signatures = new ulong[n];
            var rankByKey = new Dictionary<string, int>(n, StringComparer.Ordinal);
            var duplicates = new List<string>();
            var duplicateDetails = new List<string>();
            var problems = new List<string>();
            int maxEntityIndex = -1;
            for (int rank = 0; rank < n; rank++)
            {
                Item item = items[rank];
                sorted[rank] = prefabs[item.Original];
                keys[rank] = item.Key;
                signatures[rank] = classSignatures != null ? classSignatures[item.Original] : 0UL;
                if (!rankByKey.ContainsKey(item.Key)) rankByKey.Add(item.Key, rank);
                if (item.EntityIndex > maxEntityIndex) maxEntityIndex = item.EntityIndex;
            }

            // duplicates: runs of equal keys (adjacent after the sort)
            for (int rank = 0; rank < n;)
            {
                int end = rank + 1;
                while (end < n && string.Equals(keys[end], keys[rank], StringComparison.Ordinal)) end++;
                if (end - rank > 1)
                {
                    duplicates.Add(keys[rank]);
                    var detail = new StringBuilder(keys[rank]).Append(" (").Append((end - rank).ToString(CultureInfo.InvariantCulture)).Append("x");
                    for (int r = rank; r < end; r++)
                    {
                        string hint = sorted[r].SourceHint;
                        detail.Append(r == rank ? ": " : ", ").Append(string.IsNullOrEmpty(hint) ? "?" : hint);
                    }
                    duplicateDetails.Add(detail.Append(')').ToString());
                }
                rank = end;
            }

            // entity index -> rank: a dense table for the usual low prefab entity indices, a dictionary otherwise
            int[] dense = null;
            Dictionary<int, int> sparse = null;
            if (maxEntityIndex >= 0 && maxEntityIndex < Math.Max(DenseEntityIndexLimit, 4 * n)) dense = new int[maxEntityIndex + 1];
            else if (maxEntityIndex >= 0) sparse = new Dictionary<int, int>(n);
            for (int rank = 0; rank < n; rank++)
            {
                int index = sorted[rank].EntityIndex;
                if (index < 0) continue;
                bool known = dense != null ? dense[index] != 0 : sparse.ContainsKey(index);
                if (known)
                {
                    problems.Add("prefab entity index " + index.ToString(CultureInfo.InvariantCulture) + " is given for "
                                 + keys[rank] + " and an earlier prefab; the earlier rank is kept");
                    continue;
                }
                if (dense != null) dense[index] = rank + 1;
                else sparse.Add(index, rank);
            }

            ulong digest = ComputeDigest(keys);
            ulong classDigest = ComputeClassDigest(signatures);
            return new PrefabIndex(sorted, keys, signatures, rankByKey, dense, sparse, duplicates, duplicateDetails, problems, digest, classDigest);
        }

        /// <summary>
        /// Multiset difference of two key lists for the readable mismatch report (ARCH §7.9): <paramref name="missing"/>
        /// = keys the host has more often than the client, <paramref name="extra"/> = keys the client has more often than
        /// the host, each once per surplus occurrence, in ordinal order. Null lists and null entries count as empty.
        /// </summary>
        public static void Diff(IReadOnlyList<string> hostKeys, IReadOnlyList<string> clientKeys, out IReadOnlyList<string> missing, out IReadOnlyList<string> extra)
        {
            var balance = new Dictionary<string, int>(StringComparer.Ordinal);
            if (hostKeys != null)
            {
                foreach (string key in hostKeys)
                {
                    if (key == null) continue;
                    balance.TryGetValue(key, out int count);
                    balance[key] = count + 1;
                }
            }
            if (clientKeys != null)
            {
                foreach (string key in clientKeys)
                {
                    if (key == null) continue;
                    balance.TryGetValue(key, out int count);
                    balance[key] = count - 1;
                }
            }
            var missingList = new List<string>();
            var extraList = new List<string>();
            foreach (KeyValuePair<string, int> pair in balance)
            {
                for (int i = 0; i < pair.Value; i++) missingList.Add(pair.Key);
                for (int i = 0; i > pair.Value; i--) extraList.Add(pair.Key);
            }
            missingList.Sort(StringComparer.Ordinal);
            extraList.Sort(StringComparer.Ordinal);
            missing = missingList.AsReadOnly();
            extra = extraList.AsReadOnly();
        }

        // ------------------------------------------------------------------ IPrefabIndex

        public int Count => _sorted.Length;

        public ulong Digest { get; }

        public PrefabRank RankOfEntityIndex(int prefabEntityIndex)
        {
            if (prefabEntityIndex < 0) return PrefabRank.None;
            if (_rankByEntityIndexDense != null)
            {
                if (prefabEntityIndex < _rankByEntityIndexDense.Length)
                {
                    int stored = _rankByEntityIndexDense[prefabEntityIndex];
                    if (stored != 0) return new PrefabRank(stored - 1);
                }
                return PrefabRank.None;
            }
            if (_rankByEntityIndexSparse != null && _rankByEntityIndexSparse.TryGetValue(prefabEntityIndex, out int rank)) return new PrefabRank(rank);
            return PrefabRank.None;
        }

        public int EntityIndexOf(PrefabRank rank)
        {
            return IsValid(rank) ? Math.Max(-1, _sorted[rank.Value].EntityIndex) : -1;
        }

        /// <summary>The key (<c>"type|name|hash"</c>) of a rank, or a bracketed placeholder for none and unknown ranks.</summary>
        public string Describe(PrefabRank rank)
        {
            if (rank.IsNone) return "<no prefab>";
            return IsValid(rank) ? _keys[rank.Value] : "<unknown prefab rank " + rank.Value.ToString(CultureInfo.InvariantCulture) + ">";
        }

        /// <summary>The class signature of a rank (ARCH §5.4.1 rule 5); 0 for none and unknown ranks.</summary>
        public ulong ClassSignature(PrefabRank rank) => IsValid(rank) ? _signatures[rank.Value] : 0UL;

        // ------------------------------------------------------------------ extras

        /// <summary>The prefabs in rank order.</summary>
        public IReadOnlyList<PrefabIdentity> Sorted => _sortedView;

        /// <summary>Every key that occurs more than once, once each, in rank order.</summary>
        public IReadOnlyList<string> Duplicates => _duplicates;

        /// <summary>Report lines for <see cref="Duplicates"/>: key, count and source hints, e.g. <c>"T|n|h (2x: ModA, ModB)"</c>.</summary>
        public IReadOnlyList<string> DuplicateDetails => _duplicateDetails;

        /// <summary>Input anomalies found while building (repeated entity indices).</summary>
        public IReadOnlyList<string> Problems => _problems;

        /// <summary>xxHash64 over the class signatures in rank order (diagnostics: equal under content parity).</summary>
        public ulong ClassSignatureDigest { get; }

        /// <summary>The lowest rank of <paramref name="key"/> (the first of duplicates).</summary>
        public bool TryGetRank(string key, out PrefabRank rank)
        {
            if (key != null && _rankByKey.TryGetValue(key, out int value))
            {
                rank = new PrefabRank(value);
                return true;
            }
            rank = PrefabRank.None;
            return false;
        }

        /// <summary>The key of a rank, or null for none and unknown ranks.</summary>
        public string KeyOf(PrefabRank rank) => IsValid(rank) ? _keys[rank.Value] : null;

        /// <summary>A copy of the keys in rank order (the host's list for a prefab mismatch report, ARCH §7.9).</summary>
        public List<string> CopyKeys() => new List<string>(_keys);

        public override string ToString()
        {
            return "PrefabIndex(" + Count.ToString(CultureInfo.InvariantCulture) + " prefabs, " + _duplicates.Count.ToString(CultureInfo.InvariantCulture)
                   + " duplicate keys, digest " + Digest.ToString("X16", CultureInfo.InvariantCulture) + ")";
        }

        private void Report(ILog log)
        {
            log.Info(LogCategory.Join, "prefab index: " + Count.ToString(CultureInfo.InvariantCulture) + " prefabs, "
                     + _duplicates.Count.ToString(CultureInfo.InvariantCulture) + " duplicate key(s), digest "
                     + Digest.ToString("X16", CultureInfo.InvariantCulture) + ", class-signature digest "
                     + ClassSignatureDigest.ToString("X16", CultureInfo.InvariantCulture));
            // duplicates are legal (PrefabSystem logs a warning and the first registration wins) but make the mapping of
            // their ranks machine-dependent, so a load-order difference between peers shows up here and in the digest
            LogFindings(log, "duplicate prefab id ", _duplicateDetails);
            LogFindings(log, string.Empty, _problems);
        }

        private static void LogFindings(ILog log, string prefix, IReadOnlyList<string> findings)
        {
            int shown = Math.Min(findings.Count, MaxLoggedFindings);
            for (int i = 0; i < shown; i++) log.Warn(LogCategory.Join, "prefab index: " + prefix + findings[i]);
            if (findings.Count > shown)
            {
                log.Warn(LogCategory.Join, "prefab index: " + (findings.Count - shown).ToString(CultureInfo.InvariantCulture)
                                           + " more line(s) like the above (see the diagnostics dump)");
            }
        }

        private bool IsValid(PrefabRank rank) => rank.Value >= 0 && rank.Value < _sorted.Length;

        private static int CompareItems(Item a, Item b)
        {
            int c = string.CompareOrdinal(a.Key, b.Key);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Hint, b.Hint);
            if (c != 0) return c;
            c = a.EntityIndex.CompareTo(b.EntityIndex);
            return c != 0 ? c : a.Original.CompareTo(b.Original);
        }

        private static ulong ComputeDigest(string[] keys)
        {
            var stream = new XxHash64Stream(0);
            var writer = new ByteWriter(256);
            writer.WriteVarUInt32((uint)keys.Length);
            stream.Append(writer.Buffer, 0, writer.Length);
            byte[] scratch = new byte[256];
            foreach (string key in keys)
            {
                int length = Utf8.GetByteCount(key);
                if (scratch.Length < length) scratch = new byte[Math.Max(length, scratch.Length * 2)];
                Utf8.GetBytes(key, 0, key.Length, scratch, 0);
                writer.Reset();
                writer.WriteVarUInt32((uint)length);
                stream.Append(writer.Buffer, 0, writer.Length);
                stream.Append(scratch, 0, length);
            }
            return stream.Finish();
        }

        private static ulong ComputeClassDigest(ulong[] signatures)
        {
            var writer = new ByteWriter(16 + signatures.Length * 8);
            writer.WriteVarUInt32((uint)signatures.Length);
            foreach (ulong signature in signatures) writer.WriteUInt64(signature);
            return XxHash64.Hash(writer.Buffer, 0, writer.Length);
        }
    }
}
