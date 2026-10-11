using System.Globalization;

namespace SkylineTogether.Core
{
    /// <summary>Where a client connects to: a SteamID (P2P) or a host name and port (direct IP).</summary>
    public sealed class Endpoint
    {
        /// <summary>The host's SteamID for P2P connections; 0 for direct IP.</summary>
        public ulong SteamId;

        /// <summary>Host name or IP address for direct connections; null for P2P.</summary>
        public string Host;

        /// <summary>UDP port for direct connections.</summary>
        public int Port;

        /// <summary>Steam P2P virtual port.</summary>
        public int VirtualPort;

        public override string ToString()
        {
            var ci = CultureInfo.InvariantCulture;
            return Host != null
                ? Host + ":" + Port.ToString(ci)
                : "steam:" + SteamId.ToString(ci) + "/" + VirtualPort.ToString(ci);
        }
    }
}
