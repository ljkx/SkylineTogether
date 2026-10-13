using System;
using Colossal.UI.Binding;
using Game.UI;
using SkylineTogether.Diagnostics;
using SkylineTogether.Session;

namespace SkylineTogether.UI
{
    /// <summary>
    /// Steam invite bindings (group <c>skylineTogether</c>, w6/invites), consumed by the panel:
    /// <list type="bullet">
    /// <item><c>friends</c>: a JSON string, an array of <c>{"id": "&lt;SteamID64&gt;", "name": string, "state":
    /// "ingame"|"online"|"away"|"offline", "inSession": bool}</c> (<c>ingame</c> = playing Cities: Skylines II), in-game
    /// friends first. Steam is asked only while the panel observes the binding: every 10 s, sooner after a persona
    /// change, and at once after <c>refreshFriends</c>.</item>
    /// <item><c>overlayAvailable</c>: bool; false when the Steam overlay is off or the last invite dialog never appeared
    /// (then invite from the list, which works without the overlay).</item>
    /// <item>Triggers <c>inviteFriend(steamId)</c> (direct Steam invite), <c>openInviteOverlay()</c> (Steam's invite dialog;
    /// the panel's older <c>inviteFriends</c> trigger takes the same path) and <c>refreshFriends()</c>.</item>
    /// </list>
    /// tools/ui_smoke.mjs checks these names like the panel's own.
    /// </summary>
    public sealed class InviteUISystem : UISystemBase
    {
        private const string Group = SessionUISystem.Group;
        private const double RetrySeconds = 10;

        private string _friends = "[]";
        private double _friendsRetry, _overlayRetry;
        private Func<string> _buildFriends;
        private Func<bool> _buildOverlay;

        protected override void OnCreate()
        {
            base.OnCreate();
            _buildFriends = BuildFriends;
            _buildOverlay = BuildOverlay;
            AddUpdateBinding(new GetterValueBinding<string>(Group, "friends", () => Guard.GetRetrying(Features.Invites, "friends list", _buildFriends, _friends, ref _friendsRetry, RetrySeconds)));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "overlayAvailable", () => Guard.GetRetrying(Features.Invites, "Steam overlay state", _buildOverlay, false, ref _overlayRetry, RetrySeconds)));
            AddBinding(new TriggerBinding<string>(Group, "inviteFriend", steamId => Trigger("inviteFriend", () => Mod.Invites?.InviteFriend(steamId))));
            AddBinding(new TriggerBinding(Group, "openInviteOverlay", () => Trigger("openInviteOverlay", () => Mod.Invites?.OpenInviteDialog())));
            AddBinding(new TriggerBinding(Group, "refreshFriends", () => Trigger("refreshFriends", () => Mod.Invites?.Friends.RequestRefresh())));
        }

        /// <summary>A panel action; runs even when the feature tripped (counted all the same).</summary>
        private static void Trigger(string what, Action action) => Guard.RunAlways(Features.Invites, "panel action " + what, action);

        private string BuildFriends()
        {
            SteamInvites invites = Mod.Invites;
            SessionManager s = Mod.Sessions;
            if (invites == null || s == null) return "[]";
            _friends = invites.Friends.Json(s.Clock.Seconds);
            return _friends;
        }

        private static bool BuildOverlay() => Mod.Invites != null && Mod.Sessions != null && Mod.Invites.OverlayAvailable;
    }
}
