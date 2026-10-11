using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace SkylineTogether.Core.Identity
{
    /// <summary>
    /// The session's type catalog built by <see cref="TypeCatalogBuilder"/> (ARCH §3.6): every serializable type ranked
    /// by <c>StableTypeHash</c> with its effective family-independent policy. Family-scoped policy entries are not
    /// folded into the descriptors; core-replication's <c>PolicyTable</c> resolves them per family. Immutable (the
    /// descriptors must not be modified), so any thread may read it.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class TypeCatalog : ITypeCatalog
    {
        private readonly TypeDescriptor[] _descriptors;
        private readonly ulong[] _hashes;
        private readonly Dictionary<string, TypeDescriptor> _byName;
        private readonly ReadOnlyCollection<TypeDescriptor> _view;
        private readonly ReadOnlyCollection<string> _warnings;

        internal TypeCatalog(TypeDescriptor[] descriptors, ulong fingerprint, int policyVersion, List<string> warnings)
        {
            _descriptors = descriptors;
            _hashes = new ulong[descriptors.Length];
            _byName = new Dictionary<string, TypeDescriptor>(descriptors.Length, StringComparer.Ordinal);
            for (int i = 0; i < descriptors.Length; i++)
            {
                _hashes[i] = descriptors[i].StableTypeHash;
                if (!_byName.ContainsKey(descriptors[i].FullName)) _byName.Add(descriptors[i].FullName, descriptors[i]);
            }
            _view = new ReadOnlyCollection<TypeDescriptor>(descriptors);
            _warnings = warnings.AsReadOnly();
            Fingerprint = fingerprint;
            PolicyVersion = policyVersion;
        }

        public int Count => _descriptors.Length;

        public ulong Fingerprint { get; }

        public int PolicyVersion { get; }

        /// <summary>The descriptor of a rank; throws <see cref="ArgumentOutOfRangeException"/> outside 0 ... Count-1.</summary>
        public TypeDescriptor this[TypeRank rank]
        {
            get
            {
                if (rank.Value >= _descriptors.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(rank), rank, "the catalog has " + _descriptors.Length.ToString(CultureInfo.InvariantCulture) + " types");
                }
                return _descriptors[rank.Value];
            }
        }

        /// <summary>By full type name (ordinal); with two types of one name the lower rank wins (see <see cref="Warnings"/>).</summary>
        public bool TryGetByName(string fullName, out TypeDescriptor descriptor)
        {
            if (fullName != null && _byName.TryGetValue(fullName, out descriptor)) return true;
            descriptor = null;
            return false;
        }

        public bool TryGetByStableHash(ulong stableTypeHash, out TypeDescriptor descriptor)
        {
            int index = Array.BinarySearch(_hashes, stableTypeHash);
            if (index >= 0)
            {
                descriptor = _descriptors[index];
                return true;
            }
            descriptor = null;
            return false;
        }

        /// <summary>True for a rank inside the catalog (decoders validate wire ranks with it).</summary>
        public bool IsValid(TypeRank rank) => rank.Value < _descriptors.Length;

        /// <summary>The descriptors in rank order.</summary>
        public IReadOnlyList<TypeDescriptor> Descriptors => _view;

        /// <summary>Non-fatal findings of the build: policy entries naming unknown types, duplicate names, policy
        /// flags that have no effect on a type's kind.</summary>
        public IReadOnlyList<string> Warnings => _warnings;

        /// <summary>Number of catalogued types of one kind (self-test report, ARCH §13.4 c).</summary>
        public int CountOfKind(TypeKind kind)
        {
            int count = 0;
            foreach (TypeDescriptor d in _descriptors)
            {
                if (d.Kind == kind) count++;
            }
            return count;
        }

        public override string ToString()
        {
            return "TypeCatalog(" + Count.ToString(CultureInfo.InvariantCulture) + " types, policy v" + PolicyVersion.ToString(CultureInfo.InvariantCulture)
                   + ", fingerprint " + Fingerprint.ToString("X16", CultureInfo.InvariantCulture) + ")";
        }
    }
}
