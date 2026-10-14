using System;
using System.Globalization;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Who is at the other end of a connection (ARCH §14.2): the authenticated SteamID (0 when unknown, e.g. LAN UDP),
    /// the remote address (direct connections) and the persona name. Identity is <see cref="SteamId"/> plus
    /// <see cref="Address"/>; the display name is cosmetic and not part of equality.
    /// <para>
    /// w8/security: a transport sets <see cref="SteamId"/> only when Steam authenticated the peer (a SteamID claimed in a
    /// message is never an identity), and <see cref="Direct"/> for connections that arrived on a direct-IP listen socket;
    /// the host requires the session password for both a direct and an unauthenticated connection.
    /// </para>
    /// </summary>
    public readonly struct PeerIdentity : IEquatable<PeerIdentity>
    {
        public readonly ulong SteamId;
        public readonly string Address;
        public readonly string DisplayName;
        /// <summary>The connection arrived on a direct-IP listen socket (not Steam P2P).</summary>
        public readonly bool Direct;
        /// <summary>An in-process connection (the solo test recorder): no network, trusted like the host itself.</summary>
        public readonly bool Local;

        public PeerIdentity(ulong steamId, string address, string displayName)
            : this(steamId, address, displayName, false, false)
        {
        }

        public PeerIdentity(ulong steamId, string address, string displayName, bool direct, bool local = false)
        {
            SteamId = steamId;
            Address = address;
            DisplayName = displayName;
            Direct = direct;
            Local = local;
        }

        public bool Equals(PeerIdentity other) => SteamId == other.SteamId && string.Equals(Address, other.Address, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is PeerIdentity other && Equals(other);

        public override int GetHashCode() => SteamId.GetHashCode() ^ (Address == null ? 0 : StringComparer.Ordinal.GetHashCode(Address));

        public override string ToString()
        {
            return (DisplayName ?? "?") + " (" + SteamId.ToString(CultureInfo.InvariantCulture)
                   + (string.IsNullOrEmpty(Address) ? string.Empty : ", " + Address) + ")";
        }

        public static bool operator ==(PeerIdentity a, PeerIdentity b) => a.Equals(b);

        public static bool operator !=(PeerIdentity a, PeerIdentity b) => !a.Equals(b);
    }
}
