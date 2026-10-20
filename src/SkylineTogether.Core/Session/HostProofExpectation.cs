using System.Collections.Generic;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// Joiner: which Steam P2P hosts are known to have a session password, so a join to them sets
    /// <see cref="ClientOptions.ExpectPassword"/> and ends when the host authenticates it without a challenge
    /// (<see cref="ClientEndKind.HostNotProven"/>). A direct (IP) join always requires the host's proof
    /// (<see cref="SessionClient.HostProofRequired"/>); over Steam P2P the host is the Steam account Steam authenticated,
    /// so the proof is required only when the session is known to have a password:
    /// <list type="bullet">
    /// <item>the invite's Steam lobby says "password required" (the latest lobby data counts: "no password" clears it);</item>
    /// <item>the host challenged this game in an earlier join this game session (a join that ended asking for the
    /// password, a wrong password, or any challenged join), so a retry with the typed password and an automatic rejoin
    /// expect the challenge again.</item>
    /// </list>
    /// A password the player saved in the join settings never counts on its own (it may belong to another session). A
    /// join that ended because the host did not prove the password forgets the host, so the player's next try is not
    /// refused again for a host that removed its password. Main thread. Pure.
    /// </summary>
    public sealed class HostProofExpectation
    {
        /// <summary>Hosts remembered at most (the oldest is forgotten first).</summary>
        public const int MaxHosts = 64;

        private readonly List<ulong> _hosts = new List<ulong>();

        /// <summary>A Steam lobby of <paramref name="host"/> was read (an invite): it says whether the session has a password.</summary>
        public void OnLobby(ulong host, bool passwordRequired)
        {
            if (host == 0) return;
            if (passwordRequired) Remember(host);
            else _hosts.Remove(host);
        }

        /// <summary>
        /// A join to <paramref name="host"/> ended (<paramref name="challenged"/>: the host sent this game a password
        /// challenge during it).
        /// </summary>
        public void OnEnded(ulong host, ClientEndKind kind, RefuseReason refused, bool challenged)
        {
            if (host == 0) return;
            if (kind == ClientEndKind.HostNotProven)
            {
                _hosts.Remove(host);
                return;
            }
            if (challenged || kind == ClientEndKind.PasswordRequired || kind == ClientEndKind.Refused && refused == RefuseReason.BadPassword) Remember(host);
        }

        /// <summary>Whether a Steam P2P join to <paramref name="host"/> must see the host prove the password.</summary>
        public bool Expects(ulong host) => host != 0 && _hosts.Contains(host);

        private void Remember(ulong host)
        {
            _hosts.Remove(host);
            _hosts.Add(host);
            if (_hosts.Count > MaxHosts) _hosts.RemoveAt(0);
        }
    }
}
