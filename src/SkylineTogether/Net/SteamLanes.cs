using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using SkylineTogether.Core.Transport;
using Steamworks;

namespace SkylineTogether.Net
{
    /// <summary>
    /// Steam Networking Sockets lanes (ARCHITECTURE.md#steam-networking-sockets): every <see cref="Core.Lane"/> becomes
    /// a Steam lane of its own, with the priorities and weights of <see cref="LanePlan"/>, so a reliable bulk stream
    /// (snapshot chunks) no longer shares one ordered stream with control messages, chat and presence.
    /// <para>
    /// The game's Steamworks.NET has both calls, but neither binding is usable as declared:
    /// <c>ConfigureConnectionLanes</c> takes a single <c>out int</c> and <c>out ushort</c> where the native function reads
    /// one priority and one weight per lane, and <c>SendMessages</c> marshals an array of message structs where the native
    /// function takes an array of pointers to messages from <c>AllocateMessage</c>. This class calls the same exports of
    /// <c>steam_api64</c> with the native signatures, on the interface pointer Steamworks.NET holds internally, and writes the
    /// message fields at the offsets of the native <c>SteamNetworkingMessage_t</c>.
    /// </para>
    /// <para>
    /// Lanes are used only after <see cref="Probe"/> passed: a 64-bit process, the message layout this code expects
    /// (checked against Steamworks.NET's own struct), the interface pointer found, and a loopback socket pair that
    /// configures three lanes, sends a message on the bulk lane and receives it there intact. Otherwise every message
    /// goes to lane 0 as before and <see cref="PeerOutbox"/>'s scheduling is the only fairness. Thread-safe after the
    /// probe (Steam Networking Sockets is; the scratch arrays are per thread).
    /// </para>
    /// </summary>
    internal static class SteamLanes
    {
        // native SteamNetworkingMessage_t on 64-bit Windows (steamnetworkingtypes.h, default packing): m_pData,
        // m_cbSize, m_conn, m_identityPeer (136 bytes), m_nConnUserData, m_usecTimeReceived, m_nMessageNumber,
        // m_pfnFreeData, m_pfnRelease, m_nChannel, m_nFlags, m_nUserData, m_idxLane
        private const int OffsetData = 0;
        private const int OffsetSize = 8;
        private const int OffsetConnection = 12;
        private const int OffsetFlags = 196;
        private const int OffsetLane = 208;

        private static readonly byte[] ProbePayload = { 0x53, 0x54, 0x4C, 0x02, 0xA5 };

        [ThreadStatic]
        private static IntPtr[] _one;

        [ThreadStatic]
        private static long[] _result;

        private static IntPtr _sockets;

        /// <summary>True once <see cref="Probe"/> passed: connections configure and use lanes.</summary>
        public static bool Available { get; private set; }

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_ISteamNetworkingSockets_ConfigureConnectionLanes")]
        private static extern EResult NativeConfigureLanes(IntPtr self, HSteamNetConnection connection, int lanes, [In] int[] priorities, [In] ushort[] weights);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_ISteamNetworkingSockets_SendMessages")]
        private static extern void NativeSendMessages(IntPtr self, int count, [In] IntPtr[] messages, [Out] long[] results);

        /// <summary>
        /// Checks everything lanes depend on and turns them on when it all holds; returns null when lanes are available,
        /// otherwise why not (the probe line, warning only: without lanes the session works as before). Main thread,
        /// Steam initialised.
        /// </summary>
        public static string Probe()
        {
            Available = false;
            if (IntPtr.Size != 8) return "not a 64-bit process";
            if (Marshal.OffsetOf(typeof(SteamNetworkingMessage_t), nameof(SteamNetworkingMessage_t.m_pData)).ToInt32() != OffsetData
                || Marshal.OffsetOf(typeof(SteamNetworkingMessage_t), nameof(SteamNetworkingMessage_t.m_cbSize)).ToInt32() != OffsetSize
                || Marshal.OffsetOf(typeof(SteamNetworkingMessage_t), nameof(SteamNetworkingMessage_t.m_conn)).ToInt32() != OffsetConnection
                || Marshal.OffsetOf(typeof(SteamNetworkingMessage_t), nameof(SteamNetworkingMessage_t.m_nFlags)).ToInt32() != OffsetFlags
                || Marshal.OffsetOf(typeof(SteamNetworkingMessage_t), nameof(SteamNetworkingMessage_t.m_idxLane)).ToInt32() != OffsetLane)
            {
                return "the message layout of this Steamworks.NET differs from the expected one";
            }
            MethodInfo accessor = typeof(SteamNetworkingSockets).Assembly.GetType("Steamworks.CSteamAPIContext")
                ?.GetMethod("GetSteamNetworkingSockets", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (accessor == null || accessor.ReturnType != typeof(IntPtr)) return "the Steam Networking Sockets interface is not reachable";
            IntPtr sockets = (IntPtr)accessor.Invoke(null, null);
            if (sockets == IntPtr.Zero) return "the Steam Networking Sockets interface is not initialised";

            var identityA = new SteamNetworkingIdentity();
            var identityB = new SteamNetworkingIdentity();
            identityA.SetLocalHost();
            identityB.SetLocalHost();
            if (!SteamNetworkingSockets.CreateSocketPair(out HSteamNetConnection a, out HSteamNetConnection b, false, ref identityA, ref identityB)) return "no loopback socket pair for the self-test";
            try
            {
                EResult configured = NativeConfigureLanes(sockets, a, LanePlan.Count, LanePlan.Priorities(), LanePlan.Weights());
                if (configured != EResult.k_EResultOK) return "configuring lanes failed (" + configured + ")";
                int bulk = LanePlan.IndexOf(Core.Lane.Bulk);
                EResult sent = SendOn(sockets, a, bulk, Constants.k_nSteamNetworkingSend_ReliableNoNagle, ProbePayload, 0, ProbePayload.Length);
                if (sent != EResult.k_EResultOK) return "sending on a lane failed (" + sent + ")";
                var received = new IntPtr[4];
                int n = 0;
                for (int attempt = 0; attempt < 50 && n == 0; attempt++)
                {
                    n = SteamNetworkingSockets.ReceiveMessagesOnConnection(b, received, received.Length);
                    if (n == 0) Thread.Sleep(2);
                }
                if (n <= 0) return "the self-test message did not arrive";
                string problem = null;
                for (int i = 0; i < n; i++)
                {
                    SteamNetworkingMessage_t m = SteamNetworkingMessage_t.FromIntPtr(received[i]);
                    if (problem == null && i == 0)
                    {
                        var bytes = new byte[Math.Max(0, m.m_cbSize)];
                        if (bytes.Length > 0) Marshal.Copy(m.m_pData, bytes, 0, bytes.Length);
                        if (m.m_idxLane != bulk) problem = "the self-test message arrived on lane " + m.m_idxLane + " instead of " + bulk;
                        else if (!Same(bytes, ProbePayload)) problem = "the self-test message arrived damaged";
                    }
                    SteamNetworkingMessage_t.Release(received[i]);
                }
                if (problem != null) return problem;
                if (n != 1) return "the self-test sent one message and received " + n;
            }
            finally
            {
                SteamNetworkingSockets.CloseConnection(a, 0, "lane self-test", false);
                SteamNetworkingSockets.CloseConnection(b, 0, "lane self-test", false);
            }
            _sockets = sockets;
            Available = true;
            return null;
        }

        /// <summary>Configures the lanes of one connection; false when lanes are unavailable or Steam refused.</summary>
        public static bool Configure(HSteamNetConnection connection, out EResult result)
        {
            result = EResult.k_EResultDisabled;
            if (!Available) return false;
            result = NativeConfigureLanes(_sockets, connection, LanePlan.Count, LanePlan.Priorities(), LanePlan.Weights());
            return result == EResult.k_EResultOK;
        }

        /// <summary>Sends one message on lane <paramref name="lane"/> of a connection whose lanes are configured.</summary>
        public static EResult Send(HSteamNetConnection connection, int lane, int flags, byte[] buffer, int offset, int count)
        {
            return Available ? SendOn(_sockets, connection, lane, flags, buffer, offset, count) : EResult.k_EResultDisabled;
        }

        private static EResult SendOn(IntPtr sockets, HSteamNetConnection connection, int lane, int flags, byte[] buffer, int offset, int count)
        {
            IntPtr message = SteamNetworkingUtils.AllocateMessage(count);
            if (message == IntPtr.Zero) return EResult.k_EResultFail;
            if (count > 0) Marshal.Copy(buffer, offset, Marshal.ReadIntPtr(message, OffsetData), count);
            Marshal.WriteInt32(message, OffsetConnection, unchecked((int)connection.m_HSteamNetConnection));
            Marshal.WriteInt32(message, OffsetFlags, flags);
            Marshal.WriteInt16(message, OffsetLane, unchecked((short)lane));
            IntPtr[] one = _one ?? (_one = new IntPtr[1]);
            long[] result = _result ?? (_result = new long[1]);
            one[0] = message;
            result[0] = 0;
            // Steam takes ownership of the message whatever the outcome (it is released on failure too)
            NativeSendMessages(sockets, 1, one, result);
            one[0] = IntPtr.Zero;
            return result[0] >= 0 ? EResult.k_EResultOK : (EResult)(int)(-result[0]);
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }
    }
}
