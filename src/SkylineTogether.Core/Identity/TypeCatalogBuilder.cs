using System;
using System.Collections.Generic;
using System.Globalization;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Identity
{
    /// <summary>
    /// Builds the session's <see cref="ITypeCatalog"/> (ARCH §3.6, §4.2.1) from the inputs game-ecs-schema collects
    /// (<see cref="TypeDescriptorInput"/>) and the policy table (<c>policies.json</c>, ARCH §4.2.7).
    /// <para>
    /// <b>Ranks</b>: <see cref="TypeRank"/> = index in the list sorted by <c>StableTypeHash</c>; at most 65 536 types.
    /// <b>Policy</b>: each descriptor gets the type's family-independent entry (an entry without <c>family</c>), merged
    /// with the catalog facts (buffer, enableable, <c>ISerializeAsEnabled</c>); family-scoped entries are resolved per
    /// family by core-replication's <c>PolicyTable</c>. <b>Codec</b>: the entry's <c>codec</c>, else from the kind:
    /// Serializable → Serializable; EmptyTag → EmptyTag (EnableBit when the type is enableable and not
    /// <c>ISerializeAsEnabled</c>); EmptyData → EntityRefList when the policy replicates it as data (ARCH §4.2.4), else
    /// as EmptyTag; SharedSerializable → Shared.
    /// </para>
    /// <para>
    /// <b>Fingerprint</b> (compared in the handshake; any difference refuses the join): xxHash64 with seed
    /// <c>0x535443415431</c> ("STCAT1") over <c>i32 policyVersion</c>, <c>varint count</c>, then per type in rank order
    /// <c>u64 stableTypeHash</c>, <c>string fullName</c>, <c>string assemblyName</c> (the simple name; varint UTF-8 length + bytes),
    /// <c>u8 kind</c>, <c>u8 facts</c> (bit 0 buffer, 1 enableable, 2 serializeAsEnabled), <c>varint sizeInChunk</c>,
    /// <c>varint elementSize</c>, <c>varint entityOffsetCount</c>, <c>u8 codec</c>. It therefore changes with any input
    /// and with the policy version; the policy file's full content is compared separately (data-file digests,
    /// ARCH §4.2.7).
    /// </para>
    /// Single-threaded; build once per session after every mod registered its types (ARCH §4.4.8 step 1).
    /// </summary>
    public sealed class TypeCatalogBuilder
    {
        /// <summary>The number of distinct <see cref="TypeRank"/> values.</summary>
        public const int MaxTypes = ushort.MaxValue + 1;

        /// <summary>Longest accepted type or assembly name (characters).</summary>
        public const int MaxNameLength = 4096;

        private const ulong FingerprintSeed = 0x535443415431UL;
        private const TypeFlags FactMask = TypeFlags.Buffer | TypeFlags.Enableable | TypeFlags.SerializeAsEnabled;
        private const int MaxLoggedWarnings = 20;

        private readonly PolicyTableModel _policies;
        private readonly ILog _log;
        private readonly List<TypeDescriptorInput> _inputs = new List<TypeDescriptorInput>();
        private readonly Dictionary<ulong, int> _indexByHash = new Dictionary<ulong, int>();

        public TypeCatalogBuilder(PolicyTableModel policies) : this(policies, null)
        {
        }

        /// <param name="policies">The parsed policy table.</param>
        /// <param name="log">Receives a build summary and the warnings (category <see cref="LogCategory.Config"/>); may be null.</param>
        public TypeCatalogBuilder(PolicyTableModel policies, ILog log)
        {
            _policies = policies ?? throw new ArgumentNullException(nameof(policies));
            _log = log;
        }

        /// <summary>Number of distinct types added so far.</summary>
        public int Count => _inputs.Count;

        /// <summary>
        /// Adds one type. Adding the same input twice is ignored; two different inputs with one <c>StableTypeHash</c>
        /// throw <see cref="ArgumentException"/> (a hash collision would make ranks ambiguous: hosting must stop with an
        /// explanation). Invalid inputs (no name, negative sizes, undefined kind) throw as well.
        /// </summary>
        public void Add(in TypeDescriptorInput input)
        {
            if (string.IsNullOrWhiteSpace(input.FullName)) throw new ArgumentException("a catalogued type needs its full name", nameof(input));
            if (input.FullName.Length > MaxNameLength || (input.AssemblyName != null && input.AssemblyName.Length > MaxNameLength))
            {
                throw new ArgumentException("the type or assembly name of " + input.FullName.Substring(0, Math.Min(64, input.FullName.Length)) + "... is too long", nameof(input));
            }
            if ((byte)input.Kind > (byte)TypeKind.SharedSerializable) throw new ArgumentOutOfRangeException(nameof(input), input.Kind, "undefined type kind of " + input.FullName);
            if (input.SizeInChunk < 0 || input.ElementSize < 0 || input.EntityOffsetCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(input), "negative size or entity offset count for " + input.FullName);
            }
            if (_indexByHash.TryGetValue(input.StableTypeHash, out int existing))
            {
                if (SameInput(_inputs[existing], input)) return;
                throw new ArgumentException("stable type hash collision: " + _inputs[existing] + " and " + input, nameof(input));
            }
            _indexByHash.Add(input.StableTypeHash, _inputs.Count);
            _inputs.Add(input);
        }

        /// <summary>Ranks the added types, applies the policy table and computes the fingerprint. The result is a
        /// <see cref="TypeCatalog"/>. Throws <see cref="InvalidOperationException"/> above 65 536 types.</summary>
        public ITypeCatalog Build()
        {
            int n = _inputs.Count;
            if (n > MaxTypes) throw new InvalidOperationException(n.ToString(CultureInfo.InvariantCulture) + " types exceed the " + MaxTypes.ToString(CultureInfo.InvariantCulture) + " type ranks");

            TypeDescriptorInput[] sorted = _inputs.ToArray();
            Array.Sort(sorted, (a, b) => a.StableTypeHash.CompareTo(b.StableTypeHash));

            var warnings = new List<string>();
            Dictionary<string, TypePolicyEntry> unscoped = CollectUnscopedEntries(warnings);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var descriptors = new TypeDescriptor[n];
            for (int rank = 0; rank < n; rank++)
            {
                TypeDescriptorInput input = sorted[rank];
                unscoped.TryGetValue(input.FullName, out TypePolicyEntry entry);
                TypeFlags flags = FactsOf(input) | ((entry?.Flags ?? TypeFlags.None) & ~FactMask);
                CodecKind codec = entry?.Codec ?? DefaultCodec(input.Kind, flags);
                descriptors[rank] = new TypeDescriptor
                {
                    Rank = new TypeRank((ushort)rank),
                    StableTypeHash = input.StableTypeHash,
                    FullName = input.FullName,
                    AssemblyName = SimpleAssemblyName(input.AssemblyName),
                    Kind = input.Kind,
                    Flags = flags,
                    Codec = codec,
                    ClassOverride = entry?.Class ?? ReplicationClass.Excluded,
                    SizeInChunk = input.SizeInChunk,
                    ElementSize = input.ElementSize,
                    EntityOffsetCount = input.EntityOffsetCount,
                    ApplyTags = entry?.ApplyTags ?? TouchFlags.None,
                    Topics = entry?.Topics ?? AttentionTopic.None,
                    MergePolicy = entry?.MergePolicy
                };
                if (!names.Add(input.FullName))
                {
                    warnings.Add("two catalogued types are named " + input.FullName + "; name lookups resolve to the lower rank and its policy entry applies to both");
                }
                CheckConsistency(descriptors[rank], warnings);
            }
            CheckPolicyNames(names, warnings);

            ulong fingerprint = ComputeFingerprint(descriptors, _policies.Version);
            var catalog = new TypeCatalog(descriptors, fingerprint, _policies.Version, warnings);
            Report(catalog);
            return catalog;
        }

        /// <summary>The codec of a type without a policy override (see the class remarks).</summary>
        internal static CodecKind DefaultCodec(TypeKind kind, TypeFlags flags)
        {
            bool enableBit = (flags & TypeFlags.Enableable) != 0 && (flags & TypeFlags.SerializeAsEnabled) == 0;
            switch (kind)
            {
                case TypeKind.Serializable:
                    return CodecKind.Serializable;
                case TypeKind.EmptyData:
                    if ((flags & TypeFlags.DataReplicatedPresence) != 0) return CodecKind.EntityRefList;
                    return enableBit ? CodecKind.EnableBit : CodecKind.EmptyTag;
                case TypeKind.SharedSerializable:
                    return CodecKind.Shared;
                default:
                    return enableBit ? CodecKind.EnableBit : CodecKind.EmptyTag;
            }
        }

        /// <summary>
        /// The simple name of an assembly: a display name (<c>"Game, Version=1.0.0.0, Culture=neutral, ..."</c>) is cut at
        /// its first comma and trimmed; null gives the empty string. Versions stay out of the fingerprint (R12 §7).
        /// </summary>
        internal static string SimpleAssemblyName(string assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName)) return string.Empty;
            int comma = assemblyName.IndexOf(',');
            return (comma >= 0 ? assemblyName.Substring(0, comma) : assemblyName).Trim();
        }

        private static TypeFlags FactsOf(in TypeDescriptorInput input)
        {
            TypeFlags flags = TypeFlags.None;
            if (input.IsBuffer) flags |= TypeFlags.Buffer;
            if (input.IsEnableable) flags |= TypeFlags.Enableable;
            if (input.SerializeAsEnabled) flags |= TypeFlags.SerializeAsEnabled;
            return flags;
        }

        private static bool SameInput(in TypeDescriptorInput a, in TypeDescriptorInput b)
        {
            return a.StableTypeHash == b.StableTypeHash && string.Equals(a.FullName, b.FullName, StringComparison.Ordinal)
                   && string.Equals(SimpleAssemblyName(a.AssemblyName), SimpleAssemblyName(b.AssemblyName), StringComparison.Ordinal)
                   && a.Kind == b.Kind && a.IsBuffer == b.IsBuffer && a.IsEnableable == b.IsEnableable
                   && a.SerializeAsEnabled == b.SerializeAsEnabled && a.SizeInChunk == b.SizeInChunk
                   && a.ElementSize == b.ElementSize && a.EntityOffsetCount == b.EntityOffsetCount;
        }

        private Dictionary<string, TypePolicyEntry> CollectUnscopedEntries(List<string> warnings)
        {
            var unscoped = new Dictionary<string, TypePolicyEntry>(StringComparer.Ordinal);
            if (_policies.Types == null) return unscoped;
            foreach (TypePolicyEntry entry in _policies.Types)
            {
                if (entry == null || string.IsNullOrEmpty(entry.TypeName))
                {
                    warnings.Add("a policy entry without a type name was ignored");
                    continue;
                }
                if (entry.Family.HasValue) continue;
                if (unscoped.ContainsKey(entry.TypeName))
                {
                    warnings.Add("the policy for " + entry.TypeName + " is listed twice without a family; the first entry is used");
                    continue;
                }
                unscoped.Add(entry.TypeName, entry);
            }
            return unscoped;
        }

        private void CheckPolicyNames(HashSet<string> names, List<string> warnings)
        {
            if (_policies.Types == null) return;
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (TypePolicyEntry entry in _policies.Types)
            {
                if (entry == null || string.IsNullOrEmpty(entry.TypeName) || names.Contains(entry.TypeName)) continue;
                if (reported.Add(entry.TypeName))
                {
                    warnings.Add("the policy table names " + entry.TypeName + ", which is not a catalogued type (renamed by a game patch, or its mod is not loaded)");
                }
            }
        }

        private static void CheckConsistency(TypeDescriptor d, List<string> warnings)
        {
            if ((d.Flags & TypeFlags.DataReplicatedPresence) != 0 && d.Kind != TypeKind.EmptyData)
            {
                warnings.Add(d.FullName + ": \"dataReplicated\" applies to data-carrying IEmptySerializable types only (kind " + d.Kind + ")");
            }
            if ((d.Flags & TypeFlags.HashAsSet) != 0 && (d.Flags & TypeFlags.Buffer) == 0)
            {
                warnings.Add(d.FullName + ": \"hashAsSet\" applies to buffer types only");
            }
            if (d.Codec == CodecKind.EntityRefList && d.EntityOffsetCount == 0)
            {
                warnings.Add(d.FullName + ": the EntityRefList codec needs entity fields, but the type has none");
            }
        }

        private static ulong ComputeFingerprint(TypeDescriptor[] descriptors, int policyVersion)
        {
            var writer = new ByteWriter(64 + descriptors.Length * 96);
            writer.WriteInt32(policyVersion);
            writer.WriteVarUInt32((uint)descriptors.Length);
            foreach (TypeDescriptor d in descriptors)
            {
                writer.WriteUInt64(d.StableTypeHash);
                writer.WriteString(d.FullName, 4 * MaxNameLength);
                writer.WriteString(d.AssemblyName, 4 * MaxNameLength);
                writer.WriteByte((byte)d.Kind);
                writer.WriteByte((byte)(d.Flags & FactMask));
                writer.WriteVarUInt32((uint)d.SizeInChunk);
                writer.WriteVarUInt32((uint)d.ElementSize);
                writer.WriteVarUInt32((uint)d.EntityOffsetCount);
                writer.WriteByte((byte)d.Codec);
            }
            return XxHash64.Hash(writer.Buffer, 0, writer.Length, FingerprintSeed);
        }

        private void Report(TypeCatalog catalog)
        {
            if (_log == null) return;
            _log.Info(LogCategory.Config, "type catalog: " + catalog.Count.ToString(CultureInfo.InvariantCulture) + " types ("
                      + catalog.CountOfKind(TypeKind.Serializable).ToString(CultureInfo.InvariantCulture) + " serializable, "
                      + catalog.CountOfKind(TypeKind.EmptyTag).ToString(CultureInfo.InvariantCulture) + " tags, "
                      + catalog.CountOfKind(TypeKind.EmptyData).ToString(CultureInfo.InvariantCulture) + " presence-only data, "
                      + catalog.CountOfKind(TypeKind.SharedSerializable).ToString(CultureInfo.InvariantCulture) + " shared), policy v"
                      + catalog.PolicyVersion.ToString(CultureInfo.InvariantCulture) + ", fingerprint "
                      + catalog.Fingerprint.ToString("X16", CultureInfo.InvariantCulture) + ", "
                      + catalog.Warnings.Count.ToString(CultureInfo.InvariantCulture) + " warning(s)");
            int shown = Math.Min(catalog.Warnings.Count, MaxLoggedWarnings);
            for (int i = 0; i < shown; i++) _log.Warn(LogCategory.Config, "type catalog: " + catalog.Warnings[i]);
            if (catalog.Warnings.Count > shown)
            {
                _log.Warn(LogCategory.Config, "type catalog: " + (catalog.Warnings.Count - shown).ToString(CultureInfo.InvariantCulture) + " more warning(s) in the diagnostics dump");
            }
        }
    }
}
