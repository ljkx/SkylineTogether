using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace SkylineTogether.Core.Diagnostics
{
    /// <summary>
    /// Masks network addresses in log lines (ARCHITECTURE.md#logs): a joining player's log does not need the host's full
    /// address to explain a failed connection, and a host's log does not need every direct player's address. An IPv4
    /// address keeps its first three parts (<c>203.0.113.x</c>), an IPv6 address its first two groups
    /// (<c>2001:db8:x:x:x:x:x:x</c>), a host name its first letter and its domain (<c>m***.example.org</c>). Ports and
    /// Steam IDs stay. Only for logs and diagnostics: the address the player typed is still shown to that player.
    /// Pure; thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class AddressMask
    {
        private static readonly Regex Ipv4 = new Regex(@"(?<![\w.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?![\w.])", RegexOptions.CultureInvariant);
        private static readonly Regex BracketedIpv6 = new Regex(@"\[([0-9A-Fa-f:.%\w]+)\]", RegexOptions.CultureInvariant);

        /// <summary>An IP address or host name, masked (see the class summary); "" for null or empty.</summary>
        public static string Host(string host)
        {
            string h = (host ?? "").Trim();
            if (h.Length == 0) return "";
            bool bracketed = h.StartsWith("[", StringComparison.Ordinal) && h.EndsWith("]", StringComparison.Ordinal);
            string inner = bracketed ? h.Substring(1, h.Length - 2) : h;
            if (IPAddress.TryParse(inner, out IPAddress ip))
            {
                string masked = Ip(ip);
                return bracketed ? "[" + masked + "]" : masked;
            }
            return Name(h);
        }

        /// <summary>
        /// A transport address as a connection reports it, masked with its port kept: <c>203.0.113.7:27015</c> →
        /// <c>203.0.113.x:27015</c>, <c>[2001:db8::1]:27015</c> → <c>[2001:db8:x:x:x:x:x:x]:27015</c>, an IPv6 /64 key
        /// (<c>2001:db8:1:2::/64</c>) → <c>2001:db8:x:x:x:x:x:x/64</c>, a name → <see cref="Host"/>. "" for null or empty.
        /// </summary>
        public static string Address(string address)
        {
            string a = (address ?? "").Trim();
            if (a.Length == 0) return "";
            if (a.EndsWith("/64", StringComparison.Ordinal)) return Host(a.Substring(0, a.Length - 3)) + "/64";
            if (a.StartsWith("[", StringComparison.Ordinal))
            {
                int close = a.IndexOf(']');
                if (close > 0) return Host(a.Substring(0, close + 1)) + a.Substring(close + 1);
                return Host(a);
            }
            int colon = a.IndexOf(':');
            if (colon > 0 && colon == a.LastIndexOf(':')) return Host(a.Substring(0, colon)) + a.Substring(colon); // IPv4 or a name with a port
            return Host(a);
        }

        /// <summary>
        /// A key a remote is counted or banned under (<c>steam:…</c>, <c>ip:…</c>), for the log: the address of an
        /// <c>ip:</c> key masked (<see cref="Address"/>), anything else unchanged.
        /// </summary>
        public static string Key(string key)
        {
            if (key == null) return "";
            return key.StartsWith("ip:", StringComparison.Ordinal) ? "ip:" + Address(key.Substring(3)) : key;
        }

        /// <summary>
        /// Where a client connects to, for the log: a direct address masked with its port, a Steam endpoint unchanged
        /// (Steam IDs are in the logs anyway).
        /// </summary>
        public static string EndpointText(Endpoint endpoint)
        {
            if (endpoint == null) return "(none)";
            if (endpoint.Host == null) return endpoint.ToString();
            return Host(endpoint.Host) + ":" + endpoint.Port.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Every IPv4 address and bracketed IPv6 address in a free text (for example Steam's connection description,
        /// "#123 UDP 203.0.113.7:27015") masked. Numbers with more or fewer than four parts (versions) are left alone.
        /// </summary>
        public static string InText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string result = Ipv4.Replace(text, m =>
            {
                for (int g = 1; g <= 4; g++)
                {
                    if (!int.TryParse(m.Groups[g].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int part) || part > 255) return m.Value;
                }
                return m.Groups[1].Value + "." + m.Groups[2].Value + "." + m.Groups[3].Value + ".x";
            });
            return BracketedIpv6.Replace(result, m =>
            {
                string inner = m.Groups[1].Value;
                return inner.IndexOf(':') >= 0 && IPAddress.TryParse(inner, out IPAddress ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
                    ? "[" + Ip(ip) + "]"
                    : m.Value;
            });
        }

        private static string Ip(IPAddress ip)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes();
                return b[0].ToString(CultureInfo.InvariantCulture) + "." + b[1].ToString(CultureInfo.InvariantCulture) + "." + b[2].ToString(CultureInfo.InvariantCulture) + ".x";
            }
            if (ip.IsIPv4MappedToIPv6) return "::ffff:" + Ip(ip.MapToIPv4());
            byte[] v6 = ip.GetAddressBytes();
            string first = ((v6[0] << 8) | v6[1]).ToString("x", CultureInfo.InvariantCulture);
            string second = ((v6[2] << 8) | v6[3]).ToString("x", CultureInfo.InvariantCulture);
            return first + ":" + second + ":x:x:x:x:x:x";
        }

        // "myhost.ddns.example" -> "m***.ddns.example"; "myhost" -> "m***"
        private static string Name(string name)
        {
            int dot = name.IndexOf('.');
            string label = dot >= 0 ? name.Substring(0, dot) : name;
            string rest = dot >= 0 ? name.Substring(dot) : "";
            return (label.Length > 0 ? label.Substring(0, 1) : "") + "***" + rest;
        }
    }
}
