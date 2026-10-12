using System.Collections.Generic;
using Colossal;

namespace SkylineTogether.Settings
{
    /// <summary>English texts of the Options page.</summary>
    public sealed class LocaleEN : IDictionarySource
    {
        private readonly SkylineTogetherSettings _s;

        public LocaleEN(SkylineTogetherSettings settings)
        {
            _s = settings;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var d = new Dictionary<string, string>
            {
                { _s.GetSettingsLocaleID(), "Skyline Together" },
                { _s.GetOptionTabLocaleID(SkylineTogetherSettings.MainSection), "Multiplayer" },
                { _s.GetOptionGroupLocaleID(SkylineTogetherSettings.PlayerGroup), "You" },
                { _s.GetOptionGroupLocaleID(SkylineTogetherSettings.HostGroup), "Hosting" },
                { _s.GetOptionGroupLocaleID(SkylineTogetherSettings.JoinGroup), "Joining" },
                { _s.GetOptionGroupLocaleID(SkylineTogetherSettings.SessionGroup), "Session" },
            };
            Add(d, nameof(SkylineTogetherSettings.PlayerName), "Player name", "The name other players see. Empty: your Steam name.");
            Add(d, nameof(SkylineTogetherSettings.SessionName), "Session name", "Shown to players who join. Empty: \"<your name>'s city\".");
            Add(d, nameof(SkylineTogetherSettings.HostPassword), "Password", "Players must enter this password to join. Empty: anyone with your Steam ID or address can join.");
            Add(d, nameof(SkylineTogetherSettings.MaxPlayers), "Max players", "Including you.");
            Add(d, nameof(SkylineTogetherSettings.RequireSameMods), "Require the same mods", "Refuse players whose code mods differ from yours. They get a list of what to enable or disable. Turning this off can make the city look or behave differently for them.");
            Add(d, nameof(SkylineTogetherSettings.AllowDirectConnections), "Allow direct IP connections", "Also accept connections on a UDP port (needs port forwarding). Steam connections always work without it.");
            Add(d, nameof(SkylineTogetherSettings.DirectPort), "Direct connection port", "UDP port for direct connections.");
            Add(d, nameof(SkylineTogetherSettings.JoinTarget), "Host", "The host's Steam ID (17 digits, shown in the host's Skyline Together panel) or IP address with port, e.g. 203.0.113.7:27015.");
            Add(d, nameof(SkylineTogetherSettings.JoinPassword), "Password", "The session password, if the host set one.");
            Add(d, nameof(SkylineTogetherSettings.HostButton), "Host this city", "Open the city you are playing to other players. Load or start a city first.");
            Add(d, nameof(SkylineTogetherSettings.JoinButton), "Join", "Connect to the host above and load their city.");
            Add(d, nameof(SkylineTogetherSettings.LeaveButton), "Leave session", "Disconnect (as host: end the session for everyone).");
            return d;
        }

        public void Unload()
        {
        }

        private void Add(Dictionary<string, string> d, string option, string label, string description)
        {
            d[_s.GetOptionLabelLocaleID(option)] = label;
            d[_s.GetOptionDescLocaleID(option)] = description;
        }
    }
}
