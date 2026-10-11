namespace SkylineTogether.Core.Codec
{
    /// <summary>
    /// The entity seam of <see cref="CanonicalWriter"/>: turns a machine-local entity handle into a portable
    /// <see cref="NetRef"/> (ARCH §4.3.1, §4.3.3). The game implements it with <c>EntityRefBridge</c> over the entity map
    /// (bound slot, prefab rank, derived key, bind on reference inside the capture window, otherwise
    /// <see cref="NetRef.Local"/>); tests use fakes.
    /// <para>
    /// The writer calls the seam for <b>every</b> entity it writes, <c>Entity.Null</c> included, in write order, so an
    /// implementation can count references per record. It must return <see cref="NetRef.Null"/> for the null entity
    /// (index 0, version 0) when <c>ignoreVersion</c> is false.
    /// </para>
    /// </summary>
    public interface IEntityRefWriter
    {
        /// <summary>
        /// The reference for the entity <c>(index, version)</c>. With <c>ignoreVersion</c> the lookup is by index only
        /// and <c>version</c> is 0: this is the game's <c>IWriter.Write(Entity, ignoreVersion: true)</c>, which only
        /// <c>Game.Pathfind.PathNode</c> uses, with an index-only entity (<c>PathNode.cs</c> Serialize, R01 §3).
        /// Index 0 with <c>ignoreVersion</c> must also give <see cref="NetRef.Null"/>: a default <c>PathNode</c>
        /// (<c>m_SearchKey</c> 0) writes it, and only a null reference decodes back to owner index 0, so the value
        /// round-trips and its canonical bytes are equal on every peer.
        /// </summary>
        NetRef EncodeEntity(int index, int version, bool ignoreVersion);
    }
}
