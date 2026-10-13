using System;
using System.Globalization;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// The Steam "connect" string of a Skyline Together session (rich presence key <c>connect</c> and the invite dialog):
    /// <c>+skylinetogether_join &lt;host Steam ID&gt;</c>. Steam hands it back to a friend's running game in
    /// <c>GameRichPresenceJoinRequested_t</c>, or appends it to the command line when it starts the game.
    /// </summary>
    public static class InviteLink
    {
        public const string Command = "+skylinetogether_join";

        /// <summary>The connect string for a host.</summary>
        public static string Format(ulong hostSteamId) => Command + " " + hostSteamId.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Finds the host's Steam ID in a connect string or a whole command line; false when there is none. Only a plain
        /// 17-digit Steam ID is accepted (never an address), so a link cannot point the game at an arbitrary server.
        /// </summary>
        public static bool TryParse(string text, out ulong hostSteamId) => TryReadId(text, Command, out hostSteamId);

        /// <summary><see cref="TryParse"/> over command-line arguments (joined with spaces).</summary>
        public static bool TryParseArgs(string[] args, out ulong hostSteamId)
        {
            hostSteamId = 0;
            return args != null && TryParse(string.Join(" ", args), out hostSteamId);
        }

        /// <summary>Steam's own launch option for an accepted lobby invite: <c>+connect_lobby &lt;lobby Steam ID&gt;</c>.</summary>
        public const string LobbyCommand = "+connect_lobby";

        /// <summary>
        /// Finds a Steam lobby ID after <see cref="LobbyCommand"/> in a command line (Steam starts the game with it when a
        /// friend's lobby invite is accepted while the game is not running); false when there is none.
        /// </summary>
        public static bool TryParseLobby(string text, out ulong lobbyId) => TryReadId(text, LobbyCommand, out lobbyId);

        /// <summary><see cref="TryParseLobby"/> over command-line arguments (joined with spaces).</summary>
        public static bool TryParseLobbyArgs(string[] args, out ulong lobbyId)
        {
            lobbyId = 0;
            return args != null && TryParseLobby(string.Join(" ", args), out lobbyId);
        }

        private static bool TryReadId(string text, string command, out ulong id)
        {
            id = 0;
            if (string.IsNullOrEmpty(text)) return false;
            int at = text.IndexOf(command, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            int i = at + command.Length;
            if (i < text.Length && text[i] == '=') i++;
            while (i < text.Length && (text[i] == ' ' || text[i] == '"' || text[i] == '\t')) i++;
            int start = i;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++;
            int length = i - start;
            if (length < 15 || length > 20) return false;
            if (!ulong.TryParse(text.Substring(start, length), NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) || value == 0) return false;
            id = value;
            return true;
        }
    }
}
