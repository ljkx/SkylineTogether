using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SkylineTogether.Core.Session
{
    /// <summary>w8/security: who may join a session at all (<see cref="HostOptions.JoinPolicy"/>).</summary>
    public enum JoinPolicy : byte
    {
        /// <summary>Anyone who knows the host's Steam ID (or address) and the password, if one is set.</summary>
        Open,

        /// <summary>
        /// Steam players only when they are the host's Steam friends or were invited from the panel
        /// (<see cref="HostOptions.IsFriendOrInvited"/>); others are refused with <c>NotInvited</c>. Direct connections
        /// without a Steam identity are not affected (they always need the password).
        /// </summary>
        FriendsAndInvited
    }

    /// <summary>One entry of the host's ban list: a Steam ID, or an address for players who joined without Steam authentication.</summary>
    public sealed class BanEntry
    {
        /// <summary>The banned Steam ID; 0 for an address ban.</summary>
        public ulong SteamId;

        /// <summary>The banned address without its port (see <see cref="BanList.AddressHost"/>); null for a Steam ban.</summary>
        public string Address;

        /// <summary>The player's name when they were banned (for the unban list only).</summary>
        public string Name;

        /// <summary>When the ban was added (Unix seconds, UTC).</summary>
        public long BannedAt;

        /// <summary>A stable key of the entry: <c>steam:&lt;id&gt;</c> or <c>ip:&lt;address&gt;</c>.</summary>
        public string Key => SteamId != 0 ? "steam:" + SteamId.ToString(CultureInfo.InvariantCulture) : "ip:" + (Address ?? "");

        public BanEntry Clone() => (BanEntry)MemberwiseClone();
    }

    /// <summary>
    /// w8/security: the host's ban list (persisted by the game layer in its settings, <see cref="Serialize"/>). A Steam
    /// ban matches the authenticated Steam ID of a connection; an address ban matches the remote address (without port)
    /// of a connection that has no Steam identity. Main thread only.
    /// </summary>
    public sealed class BanList
    {
        /// <summary>Entries kept at most (the oldest go first).</summary>
        public const int MaxEntries = 1000;

        private const int MaxNameChars = 64;
        private readonly List<BanEntry> _entries = new List<BanEntry>();

        /// <summary>Bumped on every change (persist and UI refresh key).</summary>
        public int Version { get; private set; }

        public int Count => _entries.Count;

        /// <summary>Copies of the entries, oldest first.</summary>
        public IReadOnlyList<BanEntry> Entries
        {
            get
            {
                var list = new List<BanEntry>(_entries.Count);
                foreach (BanEntry e in _entries) list.Add(e.Clone());
                return list;
            }
        }

        /// <summary>
        /// True when a connection with this authenticated Steam ID (0: none) or this remote address (null: not
        /// meaningful, e.g. a Steam relay) is banned. The host passes the address for direct and unauthenticated
        /// connections only.
        /// </summary>
        public bool IsBanned(ulong steamId, string address)
        {
            string host = AddressHost(address);
            foreach (BanEntry e in _entries)
            {
                if (e.SteamId != 0)
                {
                    if (steamId != 0 && e.SteamId == steamId) return true;
                }
                else if (host != null && string.Equals(e.Address, host, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Bans a Steam ID (when not 0) or else an address. False when nothing could be banned (no identity at all) or
        /// the entry already exists.
        /// </summary>
        public bool Add(ulong steamId, string address, string name, long bannedAt)
        {
            var entry = new BanEntry
            {
                SteamId = steamId,
                Address = steamId != 0 ? null : AddressHost(address),
                Name = CleanName(name),
                BannedAt = bannedAt,
            };
            if (entry.SteamId == 0 && entry.Address == null) return false;
            string key = entry.Key;
            foreach (BanEntry e in _entries)
            {
                if (string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) return false;
            }
            _entries.Add(entry);
            while (_entries.Count > MaxEntries) _entries.RemoveAt(0);
            Version++;
            return true;
        }

        /// <summary>Removes the entry with this <see cref="BanEntry.Key"/>; false when there is none.</summary>
        public bool Remove(string key)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (!string.Equals(_entries[i].Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                _entries.RemoveAt(i);
                Version++;
                return true;
            }
            return false;
        }

        public void Clear()
        {
            if (_entries.Count == 0) return;
            _entries.Clear();
            Version++;
        }

        /// <summary>One line per entry: <c>steam|ip TAB id-or-address TAB unix-seconds TAB name</c>.</summary>
        public string Serialize()
        {
            var sb = new StringBuilder();
            foreach (BanEntry e in _entries)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(e.SteamId != 0 ? "steam" : "ip").Append('\t')
                  .Append(e.SteamId != 0 ? e.SteamId.ToString(CultureInfo.InvariantCulture) : e.Address).Append('\t')
                  .Append(e.BannedAt.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(e.Name ?? "");
            }
            return sb.ToString();
        }

        /// <summary>Reads <see cref="Serialize"/>; malformed lines are skipped (never throws).</summary>
        public static BanList Parse(string text)
        {
            var list = new BanList();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                string[] f = line.Split('\t');
                if (f.Length < 2) continue;
                long at = 0;
                if (f.Length > 2) long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out at);
                string name = f.Length > 3 ? f[3] : "";
                if (f[0] == "steam")
                {
                    if (ulong.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && id != 0) list.Add(id, null, name, at);
                }
                else if (f[0] == "ip")
                {
                    list.Add(0, f[1], name, at);
                }
            }
            list.Version = 0;
            return list;
        }

        /// <summary>
        /// The host part of a transport address, lower case: <c>203.0.113.7:27015</c> → <c>203.0.113.7</c>,
        /// <c>[2001:db8::1]:27015</c> → <c>2001:db8::1</c>; an address without a port is kept. Null for an empty one.
        /// </summary>
        public static string AddressHost(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            string a = address.Trim();
            if (a.StartsWith("[", StringComparison.Ordinal))
            {
                int close = a.IndexOf(']');
                if (close > 1) a = a.Substring(1, close - 1);
            }
            else
            {
                int colon = a.IndexOf(':');
                if (colon > 0 && colon == a.LastIndexOf(':')) a = a.Substring(0, colon); // IPv4 or a name with a port
            }
            if (a.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase) && a.IndexOf('.') > 0) a = a.Substring(7); // IPv4-mapped
            return a.Length == 0 ? null : a.ToLowerInvariant();
        }

        private static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(Math.Min(name.Length, MaxNameChars));
            foreach (char c in name)
            {
                if (sb.Length >= MaxNameChars) break;
                sb.Append(c < 0x20 ? ' ' : c);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// w8/security: limits wrong password answers per key (the authenticated Steam ID, and the address of a direct or
    /// unauthenticated connection). The first <see cref="FreeFailures"/> wrong answers cost nothing; every further one
    /// locks the key out for <see cref="BaseLockoutSeconds"/>, doubled per extra failure up to
    /// <see cref="MaxLockoutSeconds"/>. A key without failures for <see cref="ForgetSeconds"/> starts over; a right
    /// answer clears its keys. Bounded memory (<see cref="MaxEntries"/>). Pure logic: time is passed in. Main thread only.
    /// </summary>
    public sealed class PasswordAttemptLimiter
    {
        public int FreeFailures = 3;
        public double BaseLockoutSeconds = 5;
        public double MaxLockoutSeconds = 600;
        public double ForgetSeconds = 900;
        public int MaxEntries = 4096;

        private sealed class Entry
        {
            public int Failures;
            public double LastFailure;
            public double LockedUntil = double.NegativeInfinity;
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Keys tracked now (diagnostics).</summary>
        public int Tracked => _entries.Count;

        /// <summary>Wrong answers counted in total (diagnostics).</summary>
        public long TotalFailures { get; private set; }

        /// <summary>True when any of the keys is locked out now; <paramref name="retryAfter"/> = seconds until the last lock ends.</summary>
        public bool IsLocked(IReadOnlyList<string> keys, double now, out double retryAfter)
        {
            retryAfter = 0;
            if (keys == null) return false;
            foreach (string key in keys)
            {
                if (key == null || !_entries.TryGetValue(key, out Entry e)) continue;
                if (now - e.LastFailure > ForgetSeconds)
                {
                    _entries.Remove(key);
                    continue;
                }
                if (e.LockedUntil > now) retryAfter = Math.Max(retryAfter, e.LockedUntil - now);
            }
            return retryAfter > 0;
        }

        /// <summary>Counts a wrong answer for each key; returns the longest lockout it caused (0 while free attempts remain).</summary>
        public double OnFailure(IReadOnlyList<string> keys, double now)
        {
            TotalFailures++;
            double longest = 0;
            if (keys == null) return 0;
            foreach (string key in keys)
            {
                if (key == null) continue;
                if (!_entries.TryGetValue(key, out Entry e) || now - e.LastFailure > ForgetSeconds)
                {
                    if (e == null && _entries.Count >= MaxEntries) Prune(now);
                    e = new Entry();
                    _entries[key] = e;
                }
                e.Failures++;
                e.LastFailure = now;
                int over = e.Failures - FreeFailures;
                if (over > 0)
                {
                    double lockout = Math.Min(MaxLockoutSeconds, BaseLockoutSeconds * Math.Pow(2, Math.Min(30, over - 1)));
                    e.LockedUntil = Math.Max(e.LockedUntil, now + lockout);
                    longest = Math.Max(longest, lockout);
                }
            }
            return longest;
        }

        /// <summary>A right answer: the keys start over.</summary>
        public void OnSuccess(IReadOnlyList<string> keys)
        {
            if (keys == null) return;
            foreach (string key in keys)
            {
                if (key != null) _entries.Remove(key);
            }
        }

        private void Prune(double now)
        {
            var stale = new List<string>();
            foreach (KeyValuePair<string, Entry> pair in _entries)
            {
                if (now - pair.Value.LastFailure > ForgetSeconds || pair.Value.LockedUntil <= now) stale.Add(pair.Key);
            }
            foreach (string key in stale) _entries.Remove(key);
            if (_entries.Count < MaxEntries) return;
            // still full of active lockouts: drop the oldest failures
            string oldest = null;
            double at = double.PositiveInfinity;
            foreach (KeyValuePair<string, Entry> pair in _entries)
            {
                if (pair.Value.LastFailure < at)
                {
                    at = pair.Value.LastFailure;
                    oldest = pair.Key;
                }
            }
            if (oldest != null) _entries.Remove(oldest);
        }
    }

    /// <summary>
    /// w8/security: what each role may do to other players (enforced by the host on every request; the UI only offers
    /// what is allowed). The host may do everything. A co-host may kick builders and viewers and switch them between
    /// builder and viewer, but cannot touch the host or another co-host, cannot ban, cannot appoint co-hosts and cannot
    /// change the host's options, password, join policy or ban list (those live on the host's machine). Builders and
    /// viewers moderate nobody.
    /// </summary>
    public static class Moderation
    {
        public static bool CanKick(SessionRole actor, SessionRole target)
        {
            if (target == SessionRole.Host) return false;
            if (actor == SessionRole.Host) return true;
            return actor == SessionRole.CoHost && IsPlayerRole(target);
        }

        /// <summary>Only the host bans (the list is persisted on the host's machine).</summary>
        public static bool CanBan(SessionRole actor, SessionRole target) => actor == SessionRole.Host && target != SessionRole.Host;

        public static bool CanSetRole(SessionRole actor, SessionRole target, SessionRole role)
        {
            if (target == SessionRole.Host || role == SessionRole.Host || !IsAssignable(role)) return false;
            if (actor == SessionRole.Host) return true;
            return actor == SessionRole.CoHost && IsPlayerRole(target) && IsPlayerRole(role);
        }

        /// <summary>Host options, password, join policy and bans.</summary>
        public static bool CanManageSession(SessionRole actor) => actor == SessionRole.Host;

        /// <summary>True for a role that can be given to a player (co-host, builder, viewer).</summary>
        public static bool IsAssignable(SessionRole role) => role == SessionRole.CoHost || role == SessionRole.Builder || role == SessionRole.Viewer;

        private static bool IsPlayerRole(SessionRole role) => role == SessionRole.Builder || role == SessionRole.Viewer;
    }
}
