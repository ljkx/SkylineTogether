using System;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Transport seam (ARCH §9.1): Steam P2P and direct IP in the game layer; loopback, network simulator and LAN UDP
    /// in core-transport.
    /// </summary>
    public interface ITransport : IDisposable
    {
        TransportKind Kind { get; }

        [ThreadAffinity(ThreadKind.Main)]
        void Listen(ListenOptions options);

        [ThreadAffinity(ThreadKind.Main)]
        IConnection Connect(Endpoint endpoint);

        /// <summary>Delivers pending connection events and received messages to <paramref name="sink"/>.</summary>
        [ThreadAffinity(ThreadKind.Network)]
        void Poll(ITransportEventSink sink);
    }
}
