using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SkylineTogether.Core;
using SkylineTogether.Core.Transport;
using Steamworks;
using ILog = SkylineTogether.Core.ILog;

namespace SkylineTogether.Net
{
    /// <summary>
    /// <see cref="ITransport"/> over Steam Networking Sockets (R06 §4-5): P2P by Steam ID through Valve's relays (NAT
    /// traversal, encryption) and, optionally, direct IP connections on a UDP port. One poll group receives every
    /// connection's messages; connection-state changes arrive through the game's own <c>SteamAPI.RunCallbacks</c> pump
    /// and are queued until the next <see cref="Poll"/>. Main thread only, except <see cref="SteamConnection"/>'s
    /// <c>Send</c> and <c>GetStats</c>, which the host's snapshot pump also calls from its background thread (Steam
    /// Networking Sockets is thread-safe; <see cref="IThreadSafeConnection"/>).
    /// <para>
    /// Steam does not estimate a connection's bandwidth; it sends at <c>SendRateMin/Max</c>, by default a
    /// fixed 128-256 KiB/s, which made a 100 MB city take minutes over a relay. Every connection now starts at
    /// 1 MiB/s and a <see cref="SendRateController"/> per connection raises the rate while the link is busy and clean
    /// and backs off on loss or queueing delay (both limits set to the chosen rate, connection scope). The receive
    /// buffer (<c>k_ESteamNetworkingConfig_RecvBufferSize</c>, default 1 MiB = four snapshot chunks) is raised to
    /// 32 MiB, so a joiner whose frame hitches does not drop packets that then have to be resent.
    /// </para>
    /// </summary>
    public sealed class SteamSocketsTransport : ITransport
    {
        private const int AppCloseReason = 1000; // k_ESteamNetConnectionEnd_App_Generic
        private const int MaxMessagesPerPoll = 256;
        // long enough for a SessionEnd or Kick queued behind several MB of snapshot chunks to get out
        private const double ListenLingerSeconds = 10.0;

        /// <summary>
        /// Messages delivered per poll at most, and the time spent delivering them; the rest stays queued in
        /// Steam for the next frame. A flood (or a long backlog after a stall) never freezes one frame.
        /// </summary>
        private const int MaxMessagesPerFrame = 8192;
        private const double MaxPollMilliseconds = 25;

        /// <summary>The send buffer per connection (the outbox's <c>OutboxConfig.SendBufferBytes</c> matches it).</summary>
        private const int SendBufferBytes = 16 << 20;

        /// <summary><c>k_ESteamNetworkingConfig_RecvBufferSize</c> (47) and its value; newer than this Steamworks.NET's enum.</summary>
        private const ESteamNetworkingConfigValue RecvBufferSizeKey = (ESteamNetworkingConfigValue)47;
        private const int RecvBufferBytes = 32 << 20;

        private static readonly SendRateOptions RateOptions = new SendRateOptions();
        private static bool _recvBufferWarned;

        private static readonly List<(HSteamListenSocket Socket, double CloseAt)> DeferredSockets = new List<(HSteamListenSocket, double)>();
        private static readonly System.Diagnostics.Stopwatch DeferredClock = System.Diagnostics.Stopwatch.StartNew();

        private readonly ILog _log;
        private readonly Dictionary<uint, SteamConnection> _connections = new Dictionary<uint, SteamConnection>();
        private readonly Queue<SteamNetConnectionStatusChangedCallback_t> _statusEvents = new Queue<SteamNetConnectionStatusChangedCallback_t>();
        private readonly IntPtr[] _messages = new IntPtr[MaxMessagesPerPoll];
        private Callback<SteamNetConnectionStatusChangedCallback_t> _statusCallback;
        private HSteamNetPollGroup _pollGroup = HSteamNetPollGroup.Invalid;
        private HSteamListenSocket _p2pSocket = HSteamListenSocket.Invalid;
        private HSteamListenSocket _ipSocket = HSteamListenSocket.Invalid;
        private ListenOptions _listen;
        private byte[] _receiveBuffer = new byte[64 * 1024];
        private bool _disposed;

        public SteamSocketsTransport(TransportKind kind, ILog log)
        {
            Kind = kind;
            _log = log;
            _statusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatusChanged);
            _pollGroup = SteamNetworkingSockets.CreatePollGroup();
        }

        public TransportKind Kind { get; }

        /// <summary>The UDP port of the IP listen socket, 0 when not listening on IP.</summary>
        public int ListeningIpPort { get; private set; }

        public void Listen(ListenOptions options)
        {
            _listen = options ?? new ListenOptions();
            PumpDeferredCloses(force: true); // a previous session's socket would hold the port
            SteamNetworkingConfigValue_t[] config = ConnectionOptions();
            _p2pSocket = SteamNetworkingSockets.CreateListenSocketP2P(_listen.VirtualPort, config.Length, config);
            if (_p2pSocket == HSteamListenSocket.Invalid) throw new InvalidOperationException("Steam refused to open the P2P listen socket. Is Steam running and are you logged in?");
            _log.Info(LogCategory.Net, "listening for Steam P2P connections on virtual port " + _listen.VirtualPort);
            if (_listen.AllowIp && _listen.IpPort > 0)
            {
                var address = new SteamNetworkingIPAddr();
                address.Clear(); // all-zero address = every interface
                address.m_port = (ushort)_listen.IpPort;
                _ipSocket = SteamNetworkingSockets.CreateListenSocketIP(ref address, config.Length, config);
                if (_ipSocket == HSteamListenSocket.Invalid)
                {
                    _log.Warn(LogCategory.Net, "could not listen on UDP port " + _listen.IpPort + " (in use?); direct IP joining is unavailable, Steam joining still works");
                }
                else
                {
                    ListeningIpPort = _listen.IpPort;
                    _log.Info(LogCategory.Net, "listening for direct connections on UDP port " + _listen.IpPort);
                }
            }
        }

        public IConnection Connect(Endpoint endpoint)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            SteamNetworkingConfigValue_t[] config = ConnectionOptions();
            HSteamNetConnection handle;
            if (endpoint.SteamId != 0)
            {
                var identity = new SteamNetworkingIdentity();
                identity.SetSteamID64(endpoint.SteamId);
                handle = SteamNetworkingSockets.ConnectP2P(ref identity, endpoint.VirtualPort, config.Length, config);
            }
            else
            {
                SteamNetworkingIPAddr address = Resolve(endpoint.Host, endpoint.Port);
                handle = SteamNetworkingSockets.ConnectByIPAddress(ref address, config.Length, config);
            }
            if (handle == HSteamNetConnection.Invalid) return null;
            SteamNetworkingSockets.SetConnectionPollGroup(handle, _pollGroup);
            var connection = new SteamConnection(this, handle, new PeerIdentity(endpoint.SteamId, endpoint.SteamId != 0 ? null : endpoint.Host + ":" + endpoint.Port, null));
            _connections[handle.m_HSteamNetConnection] = connection;
            ConfigureNew(connection);
            return connection;
        }

        public void Poll(ITransportEventSink sink)
        {
            if (_disposed) return;
            UpdateSendRates();
            while (_statusEvents.Count > 0 && !_disposed)
            {
                try
                {
                    HandleStatus(_statusEvents.Dequeue(), sink);
                }
                catch (Exception ex)
                {
                    _log.Error(LogCategory.Net, "connection event handler failed", ex);
                }
            }
            int delivered = 0;
            long pollStart = System.Diagnostics.Stopwatch.GetTimestamp();
            while (!_disposed)
            {
                if (delivered >= MaxMessagesPerFrame || (System.Diagnostics.Stopwatch.GetTimestamp() - pollStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency > MaxPollMilliseconds) break;
                int n = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(_pollGroup, _messages, _messages.Length);
                if (n <= 0) break;
                delivered += n;
                for (int i = 0; i < n; i++)
                {
                    IntPtr ptr = _messages[i];
                    if (_disposed)
                    {
                        // a handler ended the session: free the rest without delivering it
                        SteamNetworkingMessage_t.Release(ptr);
                        continue;
                    }
                    SteamNetworkingMessage_t msg = SteamNetworkingMessage_t.FromIntPtr(ptr);
                    int size = msg.m_cbSize;
                    if (_receiveBuffer.Length < size) _receiveBuffer = new byte[Math.Max(size, _receiveBuffer.Length * 2)];
                    if (size > 0) Marshal.Copy(msg.m_pData, _receiveBuffer, 0, size);
                    uint handle = msg.m_conn.m_HSteamNetConnection;
                    SteamNetworkingMessage_t.Release(ptr);
                    if (!_connections.TryGetValue(handle, out SteamConnection connection) || connection.Closed) continue;
                    // Review fix: the "connected" status callback is dispatched by the game's SteamAPI.RunCallbacks,
                    // which may run after this poll in the same frame, while the peer's first messages (its hello) are
                    // already in the poll group. They used to be dropped, leaving the handshake to time out. A message
                    // can only arrive on a connected connection, so report it now.
                    if (!connection.Reported && !TryReportConnected(connection, sink)) continue;
                    try
                    {
                        sink.OnMessage(connection, Lane.Reliable, _receiveBuffer, 0, size);
                    }
                    catch (Exception ex)
                    {
                        _log.Error(LogCategory.Net, "message handler failed", ex);
                    }
                }
                if (n < _messages.Length) break;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (SteamConnection c in _connections.Values)
            {
                if (!c.Closed) SteamNetworkingSockets.CloseConnection(c.Handle, AppCloseReason, "shutting down", true);
                c.Closed = true;
            }
            _connections.Clear();
            // Review fix: CloseListenSocket closes every connection accepted on it ungracefully, including lingering
            // ones, so the last reliable messages (SessionEnd, Kick) were lost and clients only saw "lost the
            // connection". The listen sockets now stay open a few seconds longer (see PumpDeferredCloses).
            DeferClose(_p2pSocket);
            DeferClose(_ipSocket);
            _p2pSocket = _ipSocket = HSteamListenSocket.Invalid;
            if (_pollGroup != HSteamNetPollGroup.Invalid) SteamNetworkingSockets.DestroyPollGroup(_pollGroup);
            _pollGroup = HSteamNetPollGroup.Invalid;
            _statusCallback?.Dispose();
            _statusCallback = null;
        }

        /// <summary>
        /// Closes listen sockets of disposed transports once their lingering connections had time to flush (call once
        /// per frame); <paramref name="force"/> closes them all now (before a new listen socket is opened).
        /// </summary>
        public static void PumpDeferredCloses(bool force = false)
        {
            if (DeferredSockets.Count == 0) return;
            double now = DeferredClock.Elapsed.TotalSeconds;
            for (int i = DeferredSockets.Count - 1; i >= 0; i--)
            {
                if (!force && DeferredSockets[i].CloseAt > now) continue;
                try
                {
                    SteamNetworkingSockets.CloseListenSocket(DeferredSockets[i].Socket);
                }
                catch (Exception)
                {
                    // Steam may be shutting down
                }
                DeferredSockets.RemoveAt(i);
            }
        }

        private static void DeferClose(HSteamListenSocket socket)
        {
            if (socket == HSteamListenSocket.Invalid) return;
            DeferredSockets.Add((socket, DeferredClock.Elapsed.TotalSeconds + ListenLingerSeconds));
        }

        // ---------------------------------------------------------------- internals

        /// <summary>Steam callback (dispatched by the game's <c>SteamAPI.RunCallbacks</c>): must never throw into it.</summary>
        private void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t e)
        {
            try
            {
                if (_disposed) return;
                bool ours = _connections.ContainsKey(e.m_hConn.m_HSteamNetConnection)
                            || (e.m_info.m_hListenSocket != HSteamListenSocket.Invalid && (e.m_info.m_hListenSocket == _p2pSocket || e.m_info.m_hListenSocket == _ipSocket));
                if (ours) _statusEvents.Enqueue(e);
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Steam, "Steam connection callback", ex);
            }
        }

        private void HandleStatus(SteamNetConnectionStatusChangedCallback_t e, ITransportEventSink sink)
        {
            uint key = e.m_hConn.m_HSteamNetConnection;
            _connections.TryGetValue(key, out SteamConnection connection);
            switch (e.m_info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    if (connection == null && e.m_info.m_hListenSocket != HSteamListenSocket.Invalid)
                    {
                        int count = 0;
                        foreach (SteamConnection c in _connections.Values) if (!c.Closed) count++;
                        if (_listen != null && count >= _listen.MaxConnections + 4)
                        {
                            SteamNetworkingSockets.CloseConnection(e.m_hConn, AppCloseReason, "server full", false);
                            return;
                        }
                        EResult result = SteamNetworkingSockets.AcceptConnection(e.m_hConn);
                        if (result != EResult.k_EResultOK)
                        {
                            _log.Warn(LogCategory.Net, "could not accept a connection: " + result);
                            SteamNetworkingSockets.CloseConnection(e.m_hConn, AppCloseReason, "accept failed", false);
                            return;
                        }
                        SteamNetworkingSockets.SetConnectionPollGroup(e.m_hConn, _pollGroup);
                        var accepted = new SteamConnection(this, e.m_hConn, IdentityOf(e.m_info, IsDirect(e.m_info)));
                        _connections[key] = accepted;
                        ConfigureNew(accepted);
                    }
                    return;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    if (connection == null || connection.Reported || connection.Closed) return;
                    ReportConnected(connection, e.m_info, sink);
                    return;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                {
                    string text = e.m_info.m_szEndDebug;
                    int reason = e.m_info.m_eEndReason;
                    SteamNetworkingSockets.CloseConnection(e.m_hConn, 0, null, false);
                    if (connection == null) return;
                    _connections.Remove(key);
                    bool wasClosed = connection.Closed;
                    connection.Closed = true;
                    _log.Info(LogCategory.Net, "connection to " + connection.Peer + " ended (" + reason + ": " + text + ")");
                    if (!wasClosed) sink.OnDisconnected(connection, reason, string.IsNullOrEmpty(text) ? Describe(reason) : text);
                    return;
                }
                default:
                    return;
            }
        }

        private void ReportConnected(SteamConnection connection, SteamNetConnectionInfo_t info, ITransportEventSink sink)
        {
            connection.Peer = IdentityOf(info, connection.Peer.Direct || IsDirect(info));
            connection.Reported = true;
            _log.Info(LogCategory.Net, "connected: " + connection.Peer + " (" + info.m_szConnectionDescription + ")");
            sink.OnConnected(connection);
        }

        /// <summary>Reports a connection whose "connected" callback has not been dispatched yet, if Steam says it is connected.</summary>
        private bool TryReportConnected(SteamConnection connection, ITransportEventSink sink)
        {
            if (!SteamNetworkingSockets.GetConnectionInfo(connection.Handle, out SteamNetConnectionInfo_t info)) return false;
            if (info.m_eState != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected) return false;
            try
            {
                ReportConnected(connection, info, sink);
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Net, "connection event handler failed", ex);
            }
            return connection.Reported && !connection.Closed;
        }

        private static string Describe(int reason)
        {
            if (reason >= 4000 && reason < 5000) return "timed out or unreachable";
            if (reason >= 3000 && reason < 4000) return "network problem on the other side";
            if (reason >= 1000 && reason < 2000) return "closed by the other side";
            return "connection closed";
        }

        /// <summary>The connection was accepted on this transport's direct-IP listen socket.</summary>
        private bool IsDirect(SteamNetConnectionInfo_t info) => _ipSocket != HSteamListenSocket.Invalid && info.m_hListenSocket == _ipSocket;

        /// <summary>
        /// The peer as Steam reports it. The Steam ID is set only when Steam authenticated the remote identity (P2P
        /// always is; a direct-IP connection only when both ends had Steam certificates); an unauthenticated direct
        /// connection has an IP identity and reports Steam ID 0. The host treats both direct and unauthenticated
        /// connections as needing the session password (<c>HostOptions.PasswordForUnauthenticated</c>).
        /// </summary>
        private static PeerIdentity IdentityOf(SteamNetConnectionInfo_t info, bool direct)
        {
            ulong steamId = info.m_identityRemote.m_eType == ESteamNetworkingIdentityType.k_ESteamNetworkingIdentityType_SteamID ? info.m_identityRemote.GetSteamID64() : 0;
            string address = null;
            try
            {
                if (!info.m_addrRemote.IsIPv6AllZeros()) info.m_addrRemote.ToString(out address, true);
            }
            catch (Exception)
            {
                address = null;
            }
            string name = null;
            if (steamId != 0)
            {
                try
                {
                    name = SteamFriends.GetFriendPersonaName(new CSteamID(steamId));
                }
                catch (Exception)
                {
                    name = null;
                }
            }
            return new PeerIdentity(steamId, address, name, direct);
        }

        private static SteamNetworkingConfigValue_t[] ConnectionOptions()
        {
            return new[]
            {
                Int32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, SendBufferBytes),
                // the starting rate (Steam's default is a fixed 128-256 KiB/s); the controller takes over
                Int32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, RateOptions.InitialBytesPerSecond),
                Int32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, RateOptions.InitialBytesPerSecond),
                Int32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, 30000),
                Int32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutInitial, 20000),
                // direct-IP connections may arrive without Steam authentication (no certificate exchange,
                // e.g. a LAN or a firewall that blocks Steam's servers); Steam then reports no Steam ID for them. The
                // session layer never admits such a connection without the session password: the host refuses direct
                // and unauthenticated connections when no password is set and challenges them otherwise
                // (SessionHost.NeedsPassword); Steam P2P connections are always authenticated by their Steam ID.
                Int32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_IP_AllowWithoutAuth, 1),
            };
        }

        private static SteamNetworkingConfigValue_t Int32Option(ESteamNetworkingConfigValue key, int value)
        {
            var option = new SteamNetworkingConfigValue_t
            {
                m_eValue = key,
                m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
            };
            option.m_val.m_int32 = value;
            return option;
        }

        private static SteamNetworkingIPAddr Resolve(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Enter the host's IP address or Steam ID.");
            if (port <= 0 || port > 65535) throw new ArgumentException("The port must be between 1 and 65535.");
            host = host.Trim();
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip))
            {
                IPAddress[] all = Dns.GetHostAddresses(host);
                ip = Array.Find(all, a => a.AddressFamily == AddressFamily.InterNetwork) ?? (all.Length > 0 ? all[0] : null);
                if (ip == null) throw new ArgumentException("Could not resolve \"" + host + "\".");
            }
            var address = new SteamNetworkingIPAddr();
            address.Clear();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes();
                uint v4 = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
                address.SetIPv4(v4, (ushort)port);
            }
            else
            {
                address.SetIPv6(ip.GetAddressBytes(), (ushort)port);
            }
            return address;
        }

        internal SendResult Send(SteamConnection connection, Lane lane, Delivery delivery, byte[] buffer, int offset, int count)
        {
            if (connection.Closed || _disposed) return SendResult.Closed;
            if (count > Core.Protocol.Limits.MaxMessageBytes) return SendResult.TooLarge;
            int flags = delivery == Delivery.Unreliable
                ? Constants.k_nSteamNetworkingSend_Unreliable
                : lane == Lane.Bulk ? Constants.k_nSteamNetworkingSend_Reliable : Constants.k_nSteamNetworkingSend_ReliableNoNagle;
            GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            EResult result;
            try
            {
                IntPtr data = count == 0 ? IntPtr.Zero : pin.AddrOfPinnedObject() + offset;
                result = SteamNetworkingSockets.SendMessageToConnection(connection.Handle, data, (uint)count, flags, out long _);
            }
            finally
            {
                pin.Free();
            }
            switch (result)
            {
                case EResult.k_EResultOK:
                case EResult.k_EResultIgnored:
                    return SendResult.Ok;
                case EResult.k_EResultLimitExceeded:
                    return SendResult.Busy;
                case EResult.k_EResultNoConnection:
                case EResult.k_EResultInvalidState:
                    return SendResult.Closed;
                default:
                    return SendResult.Failed;
            }
        }

        /// <summary>Per-connection settings that cannot go into the creation options (newer than this SDK's enum).</summary>
        private void ConfigureNew(SteamConnection connection)
        {
            bool ok = SetInt32(connection, RecvBufferSizeKey, RecvBufferBytes);
            if (ok)
            {
                _log.Debug(LogCategory.Net, connection + ": receive buffer " + RecvBufferBytes + " bytes");
            }
            else if (!_recvBufferWarned)
            {
                _recvBufferWarned = true;
                _log.Info(LogCategory.Net, "Steam does not offer the receive buffer setting; the default (1 MiB) applies");
            }
        }

        private double _nextRateUpdate;

        /// <summary>Runs each connection's send rate controller and applies a changed rate (main thread).</summary>
        private void UpdateSendRates()
        {
            double now = DeferredClock.Elapsed.TotalSeconds;
            if (now < _nextRateUpdate || _connections.Count == 0) return;
            _nextRateUpdate = now + 0.1;
            foreach (SteamConnection c in _connections.Values)
            {
                if (c.Closed || !c.Reported) continue;
                ConnectionStats stats = Stats(c);
                int before = c.Rate.Rate;
                if (!c.Rate.Update(now, stats)) continue;
                int rate = c.Rate.Rate;
                // the limits are set so that min never exceeds max on the way
                bool ok = rate > before
                    ? SetInt32(c, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, rate) & SetInt32(c, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, rate)
                    : SetInt32(c, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, rate) & SetInt32(c, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, rate);
                string line = c + ": send rate " + (before / 1048576.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " -> "
                              + (rate / 1048576.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " MiB/s (" + c.Rate.LastChange
                              + ", pending " + (stats.PendingReliableBytes + stats.PendingUnreliableBytes) + " B, ping " + stats.PingMs + " ms)" + (ok ? "" : "; Steam refused it");
                if (rate < before || !ok) _log.Info(LogCategory.Net, line);
                else _log.Debug(LogCategory.Net, line);
            }
        }

        /// <summary>Sets a connection-scoped Int32 configuration value; false when Steam refuses it.</summary>
        private unsafe bool SetInt32(SteamConnection connection, ESteamNetworkingConfigValue key, int value)
        {
            if (connection.Closed || _disposed) return false;
            try
            {
                int v = value;
                return SteamNetworkingUtils.SetConfigValue(key, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection,
                    new IntPtr(connection.Handle.m_HSteamNetConnection), ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, new IntPtr(&v));
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Net, connection + ": setting " + key + " failed: " + ex.Message);
                return false;
            }
        }

        internal ConnectionStats Stats(SteamConnection connection)
        {
            if (connection.Closed || _disposed) return default(ConnectionStats);
            var status = new SteamNetConnectionRealTimeStatus_t();
            var lanes = new SteamNetConnectionRealTimeLaneStatus_t();
            if (SteamNetworkingSockets.GetConnectionRealTimeStatus(connection.Handle, ref status, 0, ref lanes) != EResult.k_EResultOK) return default(ConnectionStats);
            return new ConnectionStats
            {
                PingMs = status.m_nPing,
                Quality = status.m_flConnectionQualityLocal,
                SendRateBytesPerSecond = status.m_nSendRateBytesPerSecond,
                PendingReliableBytes = status.m_cbPendingReliable + status.m_cbSentUnackedReliable,
                PendingUnreliableBytes = status.m_cbPendingUnreliable,
                QueueTimeMicroseconds = (long)status.m_usecQueueTime,
                OutBytesPerSecond = status.m_flOutBytesPerSec,
                InBytesPerSecond = status.m_flInBytesPerSec,
                RemoteQuality = status.m_flConnectionQualityRemote,
            };
        }

        /// <summary>Sets <c>k_ESteamNetworkingConfig_TimeoutConnected</c> on one open connection (connection scope).</summary>
        internal unsafe bool SetConnectedTimeout(SteamConnection connection, int milliseconds)
        {
            if (connection.Closed || _disposed) return false;
            int value = milliseconds;
            bool ok = SteamNetworkingUtils.SetConfigValue(
                ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection,
                new IntPtr(connection.Handle.m_HSteamNetConnection),
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                new IntPtr(&value));
            if (ok) _log.Debug(LogCategory.Net, connection + ": keepalive timeout " + milliseconds + " ms");
            else _log.Warn(LogCategory.Net, connection + ": Steam refused the keepalive timeout of " + milliseconds + " ms");
            return ok;
        }

        internal void Close(SteamConnection connection, string text, bool linger)
        {
            if (connection.Closed) return;
            connection.Closed = true;
            SteamNetworkingSockets.CloseConnection(connection.Handle, AppCloseReason, text ?? "", linger);
            _connections.Remove(connection.Handle.m_HSteamNetConnection);
        }
    }

    internal sealed class SteamConnection : IThreadSafeConnection, IConnectionTimeouts
    {
        /// <summary>Chooses this connection's send rate (main thread, <see cref="SteamSocketsTransport"/>'s poll).</summary>
        public readonly SendRateController Rate = new SendRateController();

        private static int _nextId;
        private readonly SteamSocketsTransport _transport;

        public SteamConnection(SteamSocketsTransport transport, HSteamNetConnection handle, PeerIdentity peer)
        {
            _transport = transport;
            Handle = handle;
            Peer = peer;
            Id = ++_nextId;
        }

        public HSteamNetConnection Handle { get; }
        public bool Reported;
        public bool Closed;

        public int Id { get; }

        public PeerIdentity Peer { get; set; }

        public SendResult Send(Lane lane, Delivery delivery, byte[] buffer, int offset, int count) => _transport.Send(this, lane, delivery, buffer, offset, count);

        public ConnectionStats GetStats() => _transport.Stats(this);

        public void Close(int reason, string text, bool linger) => _transport.Close(this, text, linger);

        public int ConnectedTimeoutMilliseconds { get; private set; }

        public void SetConnectedTimeout(int milliseconds)
        {
            if (_transport.SetConnectedTimeout(this, milliseconds)) ConnectedTimeoutMilliseconds = milliseconds;
        }

        public override string ToString() => "steam connection " + Id + " (" + Peer + ")";

        /// <summary>This connection's send rate state.</summary>
        public string DescribeRate() => Rate.ToString();
    }
}
