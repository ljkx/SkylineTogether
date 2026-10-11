namespace SkylineTogether.Core
{
    /// <summary>
    /// The session's type catalog (ARCH §3.6): every serializable component, buffer and shared component type, ranked
    /// by stable type hash. Equal <see cref="Fingerprint"/>s (which include <see cref="PolicyVersion"/>) are required
    /// for a join. Immutable after construction, so it may be read from any thread.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public interface ITypeCatalog
    {
        int Count { get; }

        /// <summary>Hash over (stable type hash, sizes, kind, codec kind) of every type and the policy version.</summary>
        ulong Fingerprint { get; }

        /// <summary>Version of the policy table the catalog was built with.</summary>
        int PolicyVersion { get; }

        /// <summary>The descriptor of a rank; ranks outside 0 ... Count-1 throw <c>ArgumentOutOfRangeException</c>.</summary>
        TypeDescriptor this[TypeRank rank] { get; }

        bool TryGetByName(string fullName, out TypeDescriptor descriptor);

        bool TryGetByStableHash(ulong stableTypeHash, out TypeDescriptor descriptor);
    }
}
