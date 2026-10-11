namespace SkylineTogether.Core
{
    /// <summary>Receives transport events from <see cref="ITransport.Poll"/> on the network thread.</summary>
    [ThreadAffinity(ThreadKind.Network)]
    public interface ITransportEventSink
    {
        void OnConnected(IConnection connection);

        void OnDisconnected(IConnection connection, int reason, string text);

        /// <summary>One received message. The buffer is valid only during the call; copy what must outlive it.</summary>
        void OnMessage(IConnection connection, Lane lane, byte[] buffer, int offset, int count);
    }
}
