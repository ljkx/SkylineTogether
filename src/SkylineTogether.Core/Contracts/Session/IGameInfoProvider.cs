namespace SkylineTogether.Core
{
    /// <summary>Supplies this peer's handshake digests and manifest (game-bootstrap <c>GameInfoProvider</c>).</summary>
    public interface IGameInfoProvider
    {
        HandshakeDigests GetDigests();

        /// <summary>The detailed manifest; the prefab id list only when <paramref name="includePrefabList"/> is true.</summary>
        HandshakeManifest BuildManifest(bool includePrefabList);
    }
}
