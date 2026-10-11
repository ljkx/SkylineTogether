namespace SkylineTogether.Core
{
    /// <summary>
    /// Prefab ranks of this peer (ARCH §3.5): the sorted <c>PrefabID</c> list and its digest (compared in the
    /// handshake). Implemented by core-identity's <c>PrefabIndex</c> and game-ecs-schema's <c>PrefabRankMap</c>.
    /// </summary>
    public interface IPrefabIndex
    {
        int Count { get; }

        /// <summary>64-bit digest of the sorted prefab list, duplicates included.</summary>
        ulong Digest { get; }

        /// <summary>The rank of a prefab entity index; <see cref="PrefabRank.None"/> when the index is not a prefab.</summary>
        PrefabRank RankOfEntityIndex(int prefabEntityIndex);

        /// <summary>The entity index of a rank; -1 when the rank is unknown.</summary>
        int EntityIndexOf(PrefabRank rank);

        /// <summary>A human-readable name for diagnostics ("type|name|hash").</summary>
        string Describe(PrefabRank rank);

        /// <summary>Presence mask of the prefab-data types vanilla jobs index by prefab (ARCH §5.4.1 rule 5).</summary>
        ulong ClassSignature(PrefabRank rank);
    }
}
