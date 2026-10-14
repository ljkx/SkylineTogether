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
    [SettingsUIGroupOrder(PlayerGroup, HostGroup, JoinGroup, SessionGroup, DiagnosticsGroup)]
    [SettingsUIShowGroupName(PlayerGroup, HostGroup, JoinGroup, SessionGroup, DiagnosticsGroup)]
    public sealed class SkylineTogetherSettings : ModSetting
    {
        public const string MainSection = "Main";
        public const string PlayerGroup = "Player";
        public const string HostGroup = "Host";
        public const string JoinGroup = "Join";
        public const string SessionGroup = "Session";
        public const string DiagnosticsGroup = "Diagnostics";

        /// <summary>Every option shown on the Options page (texts: <c>st.options.&lt;name&gt;.label/.desc</c>).</summary>
        public static readonly string[] OptionNames =
        {
            nameof(PlayerName), nameof(ShowBuildingPreviews), nameof(SessionName), nameof(HostPassword), nameof(MaxPlayers), nameof(RequireSameMods), nameof(RequireSameAssets), nameof(FriendsOnly), nameof(EnforcePlacementChecks), nameof(AllowDirectConnections),
            nameof(DirectPort), nameof(JoinTarget), nameof(JoinPassword), nameof(HostButton), nameof(JoinButton), nameof(LeaveButton), nameof(ShowNetworkHud),
            nameof(WriteDiagnosticsButton),
        };

        public SkylineTogetherSettings(IMod mod)
            : base(mod)
        {
            SetDefaults();
        }

        /// <summary>Changes whenever settings are applied (the UI state binding rebuilds only then).</summary>
        public static int Version { get; private set; }

        public override void Apply()
        {
            base.Apply();
            Version++;
        }

        [SettingsUISection(MainSection, PlayerGroup)]
        [SettingsUITextInput]
        public string PlayerName { get; set; }

        /// <summary>Draw the other players' live tool previews (roads being drawn, buildings at their cursor, ...).</summary>
        [SettingsUISection(MainSection, PlayerGroup)]
        public bool ShowBuildingPreviews { get; set; }

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

        /// <summary>
        /// Refuse joiners whose asset packs differ (ARCHITECTURE.md#handshake-and-admission). Off (the default): they join,
        /// are told which packs differ, and objects of packs they lack are left out on their side.
        /// </summary>
        [SettingsUISection(MainSection, HostGroup)]
        public bool RequireSameAssets { get; set; }

        /// <summary>Only the host's Steam friends and players invited from the panel may join (others: "not invited").</summary>
        [SettingsUISection(MainSection, HostGroup)]
        public bool FriendsOnly { get; set; }

        /// <summary>
        /// Refuse other players' builds that fail the host's placement checks (locked prefabs, unbought map tiles, impossible
        /// geometry). Off: failures are only logged ("would refuse"), so a check that misjudges legitimate play never undoes it.
        /// </summary>
        [SettingsUISection(MainSection, HostGroup)]
        public bool EnforcePlacementChecks { get; set; }

        /// <summary>
        /// Also accept direct connections on a UDP port. They are only opened while a host password is set
        /// (the host refuses direct joiners without one anyway, <c>HostOptions.PasswordForUnauthenticated</c>).
        /// </summary>
        [SettingsUISection(MainSection, HostGroup)]
        [SettingsUIDisableByCondition(typeof(SkylineTogetherSettings), nameof(NoHostPassword))]
        public bool AllowDirectConnections { get; set; }

        /// <summary>
        /// The host's ban list (<c>Core.Session.BanList.Serialize</c>: one "steam|ip TAB id TAB time TAB
        /// name" line per ban), kept with this player's settings; edited in the panel's unban list only.
        /// </summary>
        [SettingsUIHidden]
        public string BannedPlayers { get; set; }

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
            set => Diagnostics.Guard.RunAlways(Diagnostics.Features.Ui, "Options page: host", () => Mod.Sessions?.StartHosting());
        }

        [SettingsUISection(MainSection, SessionGroup)]
        [SettingsUIButton]
        [SettingsUIDisableByCondition(typeof(SkylineTogetherSettings), nameof(CannotJoin))]
        public bool JoinButton
        {
            set => Diagnostics.Guard.RunAlways(Diagnostics.Features.Ui, "Options page: join", () => Mod.Sessions?.StartJoining());
        }

        [SettingsUISection(MainSection, SessionGroup)]
        [SettingsUIButton]
        [SettingsUIDisableByCondition(typeof(SkylineTogetherSettings), nameof(NotInSession))]
        public bool LeaveButton
        {
            set => Diagnostics.Guard.RunAlways(Diagnostics.Features.Ui, "Options page: leave", () => Mod.Sessions?.Leave());
        }

        /// <summary>Shows the network line (ping, KB/s in and out) in the Skyline Together panel.</summary>
        [SettingsUISection(MainSection, DiagnosticsGroup)]
        public bool ShowNetworkHud { get; set; }

        [SettingsUISection(MainSection, DiagnosticsGroup)]
        [SettingsUIButton]
        public bool WriteDiagnosticsButton
        {
            set => Diagnostics.Guard.RunAlways(Diagnostics.Features.Ui, "Options page: write diagnostics", WriteDiagnostics);
        }

        /// <summary>Writes the diagnostics text file next to the log and tells the player where it is.</summary>
        public static void WriteDiagnostics()
        {
            string path = Diagnostics.DiagnosticsDump.Write();
            SkylineTogether.Session.SessionManager.Notify(path != null ? LocaleSource.Resolve("st.toast.diagnostics", "PATH", path) : LocaleSource.Resolve("st.toast.diagnosticsFailed"));
        }

        public bool CannotHost() => Mod.Sessions == null || !Mod.Sessions.CanHost;

        public bool CannotJoin() => Mod.Sessions == null || !Mod.Sessions.CanJoin;

        public bool NotInSession() => Mod.Sessions == null || !Mod.Sessions.InSession;

        public bool NoHostPassword() => string.IsNullOrEmpty(HostPassword);

        public override void SetDefaults()
        {
            PlayerName = "";
            SessionName = "";
            HostPassword = "";
            MaxPlayers = 8;
            RequireSameMods = true;
            RequireSameAssets = false;
            AllowDirectConnections = false;
            FriendsOnly = false;
            EnforcePlacementChecks = false;
            BannedPlayers = "";
            DirectPort = 27015;
            JoinTarget = "";
            JoinPassword = "";
            ShowNetworkHud = false;
            ShowBuildingPreviews = true;
        }
    }
}
