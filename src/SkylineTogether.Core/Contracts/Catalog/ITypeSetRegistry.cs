using System.Collections.Generic;

namespace SkylineTogether.Core
{
    /// <summary>
    /// The session-global, append-only dictionary of replicated signatures (ARCH §3.6); there is no per-peer state.
    /// The host adds sets and emits a TYPESET record before their first use in the shared TX section; peers that start
    /// from a snapshot receive the whole registry as a keyframe (TSET section) first. Implemented by core-identity's
    /// <c>TypeSetRegistry</c>. Ids start at 1 (<see cref="TypeSetId.None"/> = 0).
    /// </summary>
    public interface ITypeSetRegistry
    {
        /// <summary>Host: the id of a sorted signature; <paramref name="added"/> = true when it was new (a TYPESET record
        /// must precede its first use in the shared TX).</summary>
        TypeSetId GetOrAdd(IReadOnlyList<TypeRank> sortedTypes, out bool added);

        /// <summary>False for an unknown id: a resync condition on clients (ARCH §12.2).</summary>
        bool TryGet(TypeSetId id, out IReadOnlyList<TypeRank> types);

        /// <summary>The types of a known set; throws <see cref="UnknownTypeSetException"/> for an unknown id.</summary>
        IReadOnlyList<TypeRank> Get(TypeSetId id);

        /// <summary>The ordinal of <paramref name="type"/> in the set (its bit in dirty masks and enabled bits); -1 if
        /// absent. Ordinals are below 64.</summary>
        int OrdinalOf(TypeSetId set, TypeRank type);

        /// <summary>Client: a TYPESET record or keyframe entry. Defining a known id again with identical types succeeds;
        /// false when it contradicts a known definition.</summary>
        bool Define(TypeSetId id, IReadOnlyList<TypeRank> sortedTypes);

        int Count { get; }

        /// <summary>TSET section: the whole registry as of now.</summary>
        void WriteKeyframe(Buffers.ByteWriter writer);

        /// <summary>Loads a keyframe; throws <see cref="DecodeException"/> on malformed input or a contradiction.</summary>
        void ReadKeyframe(Buffers.ByteReader reader);

        void Clear();
    }
}
