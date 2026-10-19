using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// Resolves a host name the player typed (for a direct connection) on a worker thread, so the game's main thread
    /// never waits for DNS (a lookup can take many seconds to time out). The main thread starts it and polls
    /// <see cref="IsDone"/> once per frame; an IP literal completes at once. IPv4 is preferred, as the transport did.
    /// </summary>
    public sealed class HostLookup
    {
        private readonly Task<IPAddress[]> _task;
        private readonly IPAddress _literal;

        /// <summary>Starts resolving <paramref name="host"/>; <paramref name="resolve"/> defaults to <see cref="Dns.GetHostAddresses(string)"/>.</summary>
        public HostLookup(string host, Func<string, IPAddress[]> resolve = null)
        {
            Host = (host ?? "").Trim();
            if (Host.Length == 0) throw new ArgumentException("Enter the host's IP address or Steam ID.");
            if (IsLiteral(Host, out _literal)) return;
            Func<string, IPAddress[]> lookup = resolve ?? Dns.GetHostAddresses;
            string name = Host;
            _task = Task.Run(() => lookup(name));
        }

        /// <summary>The name as typed (trimmed).</summary>
        public string Host { get; }

        /// <summary>True once the lookup finished (successfully or not).</summary>
        public bool IsDone => _task == null || _task.IsCompleted;

        /// <summary>
        /// The address to connect to (an IP literal) once <see cref="IsDone"/>; null when the name could not be resolved
        /// (<paramref name="error"/> then says why).
        /// </summary>
        public string Result(out string error)
        {
            error = null;
            if (_literal != null) return _literal.ToString();
            if (_task == null || !_task.IsCompleted)
            {
                error = "still resolving";
                return null;
            }
            if (_task.IsFaulted || _task.IsCanceled)
            {
                Exception e = _task.Exception?.GetBaseException();
                error = "could not resolve \"" + Host + "\"" + (e != null ? " (" + e.Message + ")" : "");
                return null;
            }
            IPAddress[] all = _task.Result ?? Array.Empty<IPAddress>();
            IPAddress ip = Array.Find(all, a => a.AddressFamily == AddressFamily.InterNetwork) ?? (all.Length > 0 ? all[0] : null);
            if (ip == null)
            {
                error = "could not resolve \"" + Host + "\"";
                return null;
            }
            return ip.ToString();
        }

        /// <summary>True when <paramref name="host"/> is an IPv4 or IPv6 literal (no lookup needed).</summary>
        public static bool IsLiteral(string host, out IPAddress address)
        {
            address = null;
            string h = (host ?? "").Trim();
            if (h.StartsWith("[", StringComparison.Ordinal) && h.EndsWith("]", StringComparison.Ordinal)) h = h.Substring(1, h.Length - 2);
            return h.Length > 0 && IPAddress.TryParse(h, out address);
        }
    }
}
