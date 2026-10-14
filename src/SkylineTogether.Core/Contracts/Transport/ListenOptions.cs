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

        /// <summary>Maximum simultaneous connections (players 2-8 plus joiners in flight).</summary>
        public int MaxConnections = 8;
    }
}
