using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// The data a host writes into its Steam lobby. The lobby exists only for invites and presence (Steam's invite dialog
    /// and the friends list's "Join game" need one); the session itself still runs over the P2P transport to
    /// <see cref="HostSteamId"/>. A joiner reads the host's Steam ID from here and never trusts anything else in it: the
    /// host checks the protocol, the mods and the password in the handshake as for every other join.
    /// </summary>
    public sealed class LobbyInfo
    {
        /// <summary>Marks a Skyline Together lobby (other mods may create lobbies for the same game).</summary>
        public const string KeyTag = "st_mod";
        public const string TagValue = "skylinetogether";
        public const string KeyHost = "st_host";
        public const string KeyName = "st_name";
        public const string KeyProtocol = "st_proto";
        public const string KeyBuild = "st_build";
        public const string KeyGame = "st_game";
        public const string KeyPassword = "st_pw";
        public const string KeyConnect = "st_connect";

        /// <summary>Steam limits a lobby data value to 8 KB; session names are cut far below that.</summary>
        public const int MaxNameLength = 64;

        public ulong HostSteamId;
        public string SessionName = "";
        public int Protocol;
        public string Build = "";
        public string GameVersion = "";
        public bool PasswordRequired;

        /// <summary>The key/value pairs to write with <c>SetLobbyData</c> (every value non-null).</summary>
        public IReadOnlyList<KeyValuePair<string, string>> ToPairs()
        {
            string name = SessionName ?? "";
            if (name.Length > MaxNameLength) name = name.Substring(0, MaxNameLength);
            return new[]
            {
                new KeyValuePair<string, string>(KeyTag, TagValue),
                new KeyValuePair<string, string>(KeyHost, HostSteamId.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(KeyName, name),
                new KeyValuePair<string, string>(KeyProtocol, Protocol.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(KeyBuild, Build ?? ""),
                new KeyValuePair<string, string>(KeyGame, GameVersion ?? ""),
                new KeyValuePair<string, string>(KeyPassword, PasswordRequired ? "1" : "0"),
                new KeyValuePair<string, string>(KeyConnect, InviteLink.Format(HostSteamId)),
            };
        }

        /// <summary>
        /// Reads a lobby through <paramref name="get"/> (Steam's <c>GetLobbyData</c>: "" for a missing key). False when it
        /// is not a Skyline Together lobby or carries no valid host Steam ID; the other fields are optional.
        /// </summary>
        public static bool TryRead(Func<string, string> get, out LobbyInfo info)
        {
            info = null;
            if (get == null) return false;
            if (!string.Equals(Safe(get, KeyTag), TagValue, StringComparison.Ordinal)) return false;
            string host = Safe(get, KeyHost).Trim();
            if (!IsSteamIdText(host, out ulong id))
            {
                // older or foreign writers: the connect string carries the same ID
                if (!InviteLink.TryParse(Safe(get, KeyConnect), out id)) return false;
            }
            int.TryParse(Safe(get, KeyProtocol), NumberStyles.Integer, CultureInfo.InvariantCulture, out int protocol);
            string name = Safe(get, KeyName);
            if (name.Length > MaxNameLength) name = name.Substring(0, MaxNameLength);
            info = new LobbyInfo
            {
                HostSteamId = id,
                SessionName = name,
                Protocol = protocol,
                Build = Safe(get, KeyBuild),
                GameVersion = Safe(get, KeyGame),
                PasswordRequired = Safe(get, KeyPassword) == "1",
            };
            return true;
        }

        /// <summary>A plain Steam ID of 15 to 20 digits (the same rule as <see cref="InviteLink"/>).</summary>
        public static bool IsSteamIdText(string text, out ulong id)
        {
            id = 0;
            if (string.IsNullOrEmpty(text) || text.Length < 15 || text.Length > 20) return false;
            foreach (char c in text)
            {
                if (c < '0' || c > '9') return false;
            }
            return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;
        }

        private static string Safe(Func<string, string> get, string key)
        {
            try
            {
                return get(key) ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
