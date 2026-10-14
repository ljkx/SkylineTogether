namespace SkylineTogether.Core
{
    /// <summary>Supplies this peer's handshake digests and manifest (the game layer's <c>GameInfoProvider</c>).</summary>
    public interface IGameInfoProvider
    {
        /// <summary>The digests of <see cref="BuildManifest"/>'s lists (cheaper where the lists are cached).</summary>
        HandshakeDigests GetDigests();

        /// <summary>The lists behind the digests, with the digests.</summary>
        HandshakeManifest BuildManifest();
    }
}
