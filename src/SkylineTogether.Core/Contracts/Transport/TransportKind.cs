namespace SkylineTogether.Core
{
    /// <summary>Transport implementations (ARCH §9.1).</summary>
    public enum TransportKind : byte
    {
        /// <summary>Steam Networking Sockets P2P by SteamID (relay and ICE).</summary>
        SteamP2P,
        /// <summary>Direct IP through the same Steam API (password challenge).</summary>
        SteamIp,
        /// <summary>Steam socket pair for the in-game single-machine self-test.</summary>
        SteamSocketPair,
        /// <summary>In-process pair (Core tests).</summary>
        Loopback,
        /// <summary>Network simulator wrapper (Core tests).</summary>
        NetSim,
        /// <summary>LAN UDP without Steam (milestone M9).</summary>
        Udp
    }
}
