namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// Optional capability of an <see cref="IConnection"/> the host accepted: the session tells it when the peer was
    /// admitted (password and compatibility passed). A transport keeps per-connection resources that a stranger must
    /// not get, such as a large receive buffer or a player slot, until then (ARCHITECTURE.md#security-model).
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public interface IAdmissionAware
    {
        /// <summary>The session admitted this connection's peer.</summary>
        void OnAdmitted();
    }
}
