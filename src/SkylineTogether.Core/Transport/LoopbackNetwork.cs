using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// The in-process "network" shared by the <see cref="LoopbackTransport"/>s of one <see cref="LoopbackTransport.CreatePair()"/>
    /// call and the clients created from them. One lock serialises every state change of every transport and
    /// connection on it; sinks are always called outside the lock.
    /// </summary>
    internal sealed class LoopbackNetwork
    {
        /// <summary>The first individual-account SteamID (account number 0), so synthetic ids look like real ones in logs.</summary>
        private const ulong SyntheticSteamIdBase = 76561197960265728UL;

        internal readonly object Lock = new object();
        internal readonly List<LoopbackTransport> Transports = new List<LoopbackTransport>();

        private int _nextConnectionId;
        private int _nextIdentity;

        /// <summary>A connection id unique on this network (call under <see cref="Lock"/>).</summary>
        internal int NextConnectionId() => ++_nextConnectionId;

        /// <summary>A synthetic identity for a transport created without one (call under <see cref="Lock"/>).</summary>
        internal PeerIdentity NextIdentity(string role)
        {
            int number = ++_nextIdentity;
            string n = number.ToString(CultureInfo.InvariantCulture);
            return new PeerIdentity(SyntheticSteamIdBase + (ulong)number, "loopback:" + n, "Loopback " + role + " " + n);
        }
    }
}
