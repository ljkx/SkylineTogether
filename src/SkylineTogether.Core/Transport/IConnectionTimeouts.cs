using System;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// Optional capability of an <see cref="IConnection"/>: its keepalive timeout ("no packet for this long ends the
    /// connection", Steam's <c>k_ESteamNetworkingConfig_TimeoutConnected</c>) can be changed while it is open. The
    /// session raises it while the joiner's game loads the city, when either main thread may stall for a long time,
    /// and restores it once the joiner is live (ARCHITECTURE.md#live-session).
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public interface IConnectionTimeouts
    {
        /// <summary>The timeout last set through <see cref="SetConnectedTimeout"/>, 0 while the transport default applies.</summary>
        int ConnectedTimeoutMilliseconds { get; }

        /// <summary>Sets the keepalive timeout of this connection.</summary>
        void SetConnectedTimeout(int milliseconds);
    }

    /// <summary>The session's keepalive timeouts and a helper that applies them to connections that support it.</summary>
    public static class ConnectionTimeouts
    {
        /// <summary>Normal keepalive timeout of a connection (ARCHITECTURE.md#live-session: 30 s of silence disconnects).</summary>
        public const int NormalMilliseconds = 30000;

        /// <summary>Keepalive timeout while a joiner downloads and loads the city (long main-thread stalls are expected).</summary>
        public const int JoiningMilliseconds = 120000;

        /// <summary>
        /// Applies <see cref="JoiningMilliseconds"/> or <see cref="NormalMilliseconds"/> to <paramref name="connection"/>
        /// when it implements <see cref="IConnectionTimeouts"/>; never throws (a failure only keeps the old timeout).
        /// </summary>
        public static void Apply(IConnection connection, bool joining, ILog log = null)
        {
            if (!(connection is IConnectionTimeouts t)) return;
            int wanted = joining ? JoiningMilliseconds : NormalMilliseconds;
            if (t.ConnectedTimeoutMilliseconds == wanted) return;
            try
            {
                t.SetConnectedTimeout(wanted);
            }
            catch (Exception ex)
            {
                log?.Warn(LogCategory.Net, "could not change the connection timeout: " + ex.Message);
            }
        }
    }
}
