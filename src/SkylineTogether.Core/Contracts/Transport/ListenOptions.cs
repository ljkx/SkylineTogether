namespace SkylineTogether.Core
{
    /// <summary>Host listen configuration (ARCHITECTURE.md#transport, #security-model).</summary>
    public sealed class ListenOptions
    {
        /// <summary>Steam P2P virtual port.</summary>
        public int VirtualPort;

        /// <summary>UDP port of the direct-IP listen socket.</summary>
        public int IpPort;

        /// <summary>Open a direct-IP listen socket as well (requires the session password challenge).</summary>
        public bool AllowIp;

        /// <summary>
        /// Maximum simultaneous admitted connections (the session's player limit; <c>SessionHost.SetMaxPlayers</c> raises it
        /// while hosting). Connections still in the handshake are limited separately.
        /// </summary>
        public int MaxConnections = 8;

        /// <summary>
        /// Connections that have not been admitted yet (in the handshake) at once; further ones are refused at the
        /// transport, so strangers holding handshakes open cannot take the players' slots.
        /// </summary>
        public int MaxPendingConnections = 8;

        /// <summary>Connections in the handshake at once from one remote identity (Steam ID or address).</summary>
        public int MaxPendingPerRemote = 2;

        /// <summary>
        /// Whether a transport may accept another connection: <c>null</c> when it may, otherwise the reason it is refused
        /// ("server full" when <paramref name="admitted"/> reached <see cref="MaxConnections"/>, "server busy" when too many
        /// handshakes are open in total or from this remote).
        /// </summary>
        public string Refusal(int admitted, int pending, int pendingFromRemote)
        {
            if (admitted >= MaxConnections) return "server full";
            if (pending >= MaxPendingConnections) return "server busy";
            if (pendingFromRemote >= MaxPendingPerRemote) return "server busy";
            return null;
        }
    }
}
