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
        public static bool TryParse(string text, out ulong hostSteamId)
        {
            hostSteamId = 0;
            if (string.IsNullOrEmpty(text)) return false;
            int at = text.IndexOf(Command, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            int i = at + Command.Length;
            if (i < text.Length && text[i] == '=') i++;
            while (i < text.Length && (text[i] == ' ' || text[i] == '"' || text[i] == '\t')) i++;
            int start = i;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++;
            int length = i - start;
            if (length < 15 || length > 20) return false;
            if (!ulong.TryParse(text.Substring(start, length), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || id == 0) return false;
            hostSteamId = id;
            return true;
        }

        /// <summary><see cref="TryParse"/> over command-line arguments (joined with spaces).</summary>
        public static bool TryParseArgs(string[] args, out ulong hostSteamId)
        {
            hostSteamId = 0;
            return args != null && TryParse(string.Join(" ", args), out hostSteamId);
        }
    }
}
