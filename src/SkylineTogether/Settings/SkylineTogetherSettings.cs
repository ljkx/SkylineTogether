using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace SkylineTogether.Settings
{
    /// <summary>
    /// Local preferences (never simulation state): the player's name, how to host and what to join. The in-game panel
    /// edits the same values; the Options page is the fallback with Host/Join/Leave buttons.
    /// </summary>
    [FileLocation("ModsSettings/SkylineTogether/SkylineTogether")]
    [SettingsUIGroupOrder(PlayerGroup, HostGroup, JoinGroup, SessionGroup)]
    [SettingsUIShowGroupName(PlayerGroup, HostGroup, JoinGroup, SessionGroup)]
    public sealed class SkylineTogetherSettings : ModSetting
    {
        public const string MainSection = "Main";
        public const string PlayerGroup = "Player";
        public const string HostGroup = "Host";
        public const string JoinGroup = "Join";
        public const string SessionGroup = "Session";

        public SkylineTogetherSettings(IMod mod)
            : base(mod)
        {
            SetDefaults();
        }

        [SettingsUISection(MainSection, PlayerGroup)]
        [SettingsUITextInput]
        public string PlayerName { get; set; }

        [SettingsUISection(MainSection, HostGroup)]
        [SettingsUITextInput]
        public string SessionName { get; set; }

        [SettingsUISection(MainSection, HostGroup)]
        [SettingsUITextInput]
        public string HostPassword { get; set; }

        [SettingsUISection(MainSection, HostGroup)]
        [SettingsUISlider(min = 2, max = 16, step = 1)]
        public int MaxPlayers { get; set; }

        [SettingsUISection(MainSection, HostGroup)]
        public bool RequireSameMods { get; set; }

        [SettingsUISection(MainSection, HostGroup)]
        public bool AllowDirectConnections { get; set; }

        [SettingsUISection(MainSection, HostGroup)]
        [SettingsUISlider(min = 1024, max = 65535, step = 1)]
        public int DirectPort { get; set; }

        /// <summary>A Steam ID (17 digits) or an address such as <c>203.0.113.7:27015</c>.</summary>
        [SettingsUISection(MainSection, JoinGroup)]
        [SettingsUITextInput]
        public string JoinTarget { get; set; }

        [SettingsUISection(MainSection, JoinGroup)]
        [SettingsUITextInput]
        public string JoinPassword { get; set; }

        [SettingsUISection(MainSection, SessionGroup)]
        [SettingsUIButton]
        [SettingsUIDisableByCondition(typeof(SkylineTogetherSettings), nameof(CannotHost))]
        public bool HostButton
        {
            set => Mod.Sessions?.StartHosting();
        }

        [SettingsUISection(MainSection, SessionGroup)]
        [SettingsUIButton]
        [SettingsUIDisableByCondition(typeof(SkylineTogetherSettings), nameof(CannotJoin))]
        public bool JoinButton
        {
            set => Mod.Sessions?.StartJoining();
        }

        [SettingsUISection(MainSection, SessionGroup)]
        [SettingsUIButton]
        [SettingsUIDisableByCondition(typeof(SkylineTogetherSettings), nameof(NotInSession))]
        public bool LeaveButton
        {
            set => Mod.Sessions?.Leave();
        }

        public bool CannotHost() => Mod.Sessions == null || !Mod.Sessions.CanHost;

        public bool CannotJoin() => Mod.Sessions == null || !Mod.Sessions.CanJoin;

        public bool NotInSession() => Mod.Sessions == null || !Mod.Sessions.InSession;

        public override void SetDefaults()
        {
            PlayerName = "";
            SessionName = "";
            HostPassword = "";
            MaxPlayers = 8;
            RequireSameMods = true;
            AllowDirectConnections = false;
            DirectPort = 27015;
            JoinTarget = "";
            JoinPassword = "";
        }
    }
}
