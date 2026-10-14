/*
 * Cities: Skylines II UI Module
 *
 * Id: SkylineTogether
 * Author: Skyline Together
 * Version: 0.3.0
 * Dependencies:
 */

// Hand-written ES module (no build step). The game provides React, its binding API and its own components as globals.
// Everything the game provides is feature-detected; nothing may throw while the module is imported or rendered. Where a
// game component or theme is missing (or throws while rendering), a built-in look that copies the vanilla styles is used.
//
// Styling rules learned from the game's own stylesheet (Content/Game/UI/index.css), which this module follows:
//  - the game's CSS never writes var(--token, fallback): Gameface drops such a declaration, which is why text and fields
//    used to fall back to the browser defaults (black text, white inputs). Tokens are used as plain var(--token) only.
//  - backgrounds are always background-color / background-image, borders are written per side.
//  - <body> sets no text colour (the default is black): every surface here sets its colour explicitly. The join and
//    error screens, which can show over the loading screen, use literal colours only.
const React = window.React;
const api = window["cs2/api"];
const UI = window["cs2/ui"] || null;
const L10N = window["cs2/l10n"] || null;
const GAME_BINDINGS = window["cs2/bindings"] || null;
// Review fix: the module used to throw at import when a global was missing; now it registers nothing (ARCH §11.2).
const ready = !!(React && React.createElement && React.useState && api && api.bindValue && api.useValue && api.trigger);
const h = ready ? React.createElement : function () { return null; };
const GROUP = "skylineTogether";

function bind(name, fallback) {
  return ready ? api.bindValue(GROUP, name, fallback) : null;
}
// state: the panel's model (SessionUISystem.BuildState); join: the joiner's progress (stage, bytes, speed, ETA);
// net: the network HUD line ({show, text}); presence: remote players' name tags ({tags: [{id, name, color, x, y, on, activity}]})
const stateBinding = bind("state", "{}");
const joinBinding = bind("join", "{}");
const netBinding = bind("net", "{}");
const presenceBinding = bind("presence", "{\"tags\":[]}");
// solo testing: recording and replay state, recordings, last verdict (ReplayUISystem)
const replayBinding = bind("replay", "{}");
// Steam invites (InviteUISystem): friends is a JSON array of {id, name, state, inSession}; C# asks Steam only while the
// binding is observed, so only the open invite popover reads it. Missing or empty: only the Steam overlay button shows.
// overlayAvailable: false when the Steam overlay cannot open (then the overlay button hides; null = unknown).
const friendsBinding = bind("friends", "");
const overlayBinding = bind("overlayAvailable", null);

function parse(json) {
  try {
    const v = JSON.parse(json || "{}");
    return v !== null && typeof v === "object" ? v : {};
  } catch (e) {
    return {};
  }
}

function useJson(binding) {
  let raw = null;
  try {
    raw = api.useValue(binding);
  } catch (e) {
    raw = null;
  }
  return parse(raw);
}

// null while the friends binding is missing or not an array
function useFriends() {
  let raw = null;
  try {
    raw = api.useValue(friendsBinding);
  } catch (e) {
    raw = null;
  }
  if (typeof raw !== "string" || raw.length === 0) return null;
  try {
    const v = JSON.parse(raw);
    return Array.isArray(v) ? v : null;
  } catch (e) {
    return null;
  }
}

// false only when C# says the Steam overlay cannot open
function useOverlayAvailable() {
  try {
    return api.useValue(overlayBinding) !== false;
  } catch (e) {
    return true;
  }
}

function send(name) {
  try {
    api.trigger.apply(null, [GROUP, name].concat(Array.prototype.slice.call(arguments, 1)));
  } catch (e) {
    console.warn("SkylineTogether: trigger " + name + " failed: " + e);
  }
}

// the vanilla main menu's own screen switch (menu-bindings.ts: setActiveScreen, MenuScreen.NewGame = 1, LoadGame = 2)
function openVanillaScreen(screen) {
  try {
    api.trigger("menu", "setActiveScreen", screen);
  } catch (e) {
    console.warn("SkylineTogether: menu screen " + screen + " unavailable: " + e);
  }
}

function merge() {
  return Object.assign.apply(null, [{}].concat(Array.prototype.slice.call(arguments)));
}

function list(v) {
  return Array.isArray(v) ? v : [];
}

function cx() {
  let out = "";
  for (let i = 0; i < arguments.length; i++) if (arguments[i]) out += (out ? " " : "") + arguments[i];
  return out || undefined;
}

// ---------------------------------------------------------------------------------------------------------- texts

// English fallback of every UI text. The game's localization (cs2/l10n) serves the same keys from
// src/SkylineTogether/Data/locale.<id>.json (C# LocaleSource); tools/ui_smoke.mjs checks that the tables agree.
const EN = {
  "st.ui.title": "Skyline Together",
  "st.ui.menu.multiplayer": "Multiplayer",
  "st.ui.button.tooltip": "Play this city together with friends: host it, join a friend's city, chat.",
  "st.ui.button.players": "{N} players in the session",
  "st.ui.close": "Close",
  "st.ui.back": "Back",
  "st.ui.steam.missing.title": "Steam is not available",
  "st.ui.steam.missing.text": "Skyline Together needs the Steam version of the game, with Steam running and logged in.",
  "st.ui.steam.ok": "Steam connected",
  "st.ui.tab.host": "Host",
  "st.ui.tab.join": "Join",
  "st.ui.tab.solo": "Solo test",
  "st.ui.nav.join": "Join a friend",
  "st.ui.nav.host": "Host a city",
  "st.ui.nav.solo": "Solo test",
  "st.ui.profile.name": "Your name",
  "st.ui.profile.namePlaceholder": "Empty: your Steam name",
  "st.ui.profile.nameHint": "Shown to the other players, on your cursor and in the chat.",
  "st.ui.host.title": "Host this city",
  "st.ui.host.subtitle": "Open the city you are playing to your friends",
  "st.ui.host.intro": "Open the city you are playing to your friends. They join with your Steam ID or a Steam invite.",
  "st.ui.host.sessionName": "Session name",
  "st.ui.host.sessionNamePlaceholder": "Empty: \"{NAME}'s city\"",
  "st.ui.host.sessionNameHint": "What your friends see while they join.",
  "st.ui.host.password": "Password",
  "st.ui.host.passwordPlaceholder": "Optional",
  "st.ui.host.passwordKept": "Unchanged (a password is set)",
  "st.ui.host.passwordHint": "Leave empty for no password.",
  "st.ui.host.removePassword": "Remove password",
  "st.ui.host.maxPlayers": "Max players",
  "st.ui.host.maxPlayersHint": "Including you.",
  "st.ui.host.requireMods": "Require the same mods",
  "st.ui.host.requireModsHint": "Refuse players whose code mods differ from yours. They get a list of what to enable, disable or update.",
  "st.ui.host.allowDirect": "Allow direct IP connections",
  "st.ui.host.allowDirectHint": "Also accept connections on a UDP port (needs port forwarding and a host password). Steam connections always work.",
  "st.ui.host.allowDirectNextTime": "Applies the next time you host.",
  "st.ui.host.friendsOnly": "Friends and invited only",
  "st.ui.host.friendsOnlyHint": "Only your Steam friends and players you invite from this panel can join through Steam. Direct IP joiners still need the password.",
  "st.ui.host.allowDirectNeedsPassword": "Set a password first: Steam does not check direct connections, so the password is what keeps strangers out.",
  "st.ui.host.bans": "Banned players",
  "st.ui.host.bansHint": "Banned players can't join your sessions until you unban them.",
  "st.ui.host.unban": "Unban",
  "st.ui.host.banAddress": "address {ADDRESS}",
  "st.ui.host.port": "Port",
  "st.ui.host.portHint": "The UDP port players connect to directly (1024 to 65535).",
  "st.ui.host.start": "Host this city",
  "st.ui.host.needCity": "Load or start a city first, then host it from the game.",
  "st.ui.host.unavailable": "Hosting is not possible right now.",
  "st.ui.host.settingsTitle": "Host settings",
  "st.ui.host.settingsNote": "Saved right away and used the next time you host.",
  "st.ui.host.menu.title": "Hosting starts from inside your city",
  "st.ui.host.menu.step1": "Load a saved city or start a new one.",
  "st.ui.host.menu.step2": "Click the Skyline Together button in the top-left corner of the screen.",
  "st.ui.host.menu.step3": "Press \"Host this city\" and invite your friends.",
  "st.ui.host.menu.load": "Load game",
  "st.ui.host.menu.new": "New game",
  "st.ui.host.info": "Your game keeps running the city as usual. Friends who join get a copy of it and from then on everything anyone builds is shared live.",
  "st.ui.join.title": "Join a friend",
  "st.ui.join.subtitle": "Play together in a friend's city",
  "st.ui.join.intro": "Enter the host's Steam ID (shown in their Skyline Together panel) or IP address and port.",
  "st.ui.join.target": "Host's Steam ID (or IP:port) - paste it here",
  "st.ui.join.targetShort": "Host",
  "st.ui.join.targetPlaceholder": "Steam ID or IP:port",
  "st.ui.join.password": "Password",
  "st.ui.join.passwordPlaceholder": "If the host set one",
  "st.ui.join.passwordHint": "Only needed when the host set a password.",
  "st.ui.join.start": "Join",
  "st.ui.join.steamHint": "Got a Steam invite? Accept it in Steam and the game joins by itself.",
  "st.ui.join.info": "The host's game saves the city and sends it to you; your game then loads it. Large cities take a minute. After that everything you and your friends build is shared live.",
  "st.ui.join.findId": "The host finds their Steam ID at the top of their Skyline Together panel and can copy it with one click.",
  "st.ui.join.replaceWarning": "Joining replaces the city you are playing with the host's city. Save first.",
  "st.ui.join.overlayTitle": "Joining {SESSION}",
  "st.ui.join.overlayTitleUnknown": "Joining a session",
  "st.ui.join.via": "Host: {TARGET}",
  "st.ui.join.stage.connecting": "Connecting to the host",
  "st.ui.join.stage.handshaking": "Checking versions and mods",
  "st.ui.join.stage.waiting": "The host is saving the city",
  "st.ui.join.stage.downloading": "Downloading the city",
  "st.ui.join.stage.loading": "Loading the city",
  "st.ui.join.hint.connecting": "Reaching the host through Steam…",
  "st.ui.join.hint.handshaking": "Making sure both games run the same versions and mods.",
  "st.ui.join.hint.waiting": "The host's game saves the city for you. Large cities take a moment.",
  "st.ui.join.hint.downloading": "{DONE} MB of {TOTAL} MB · {SPEED} MB/s · {ETA} left",
  "st.ui.join.hint.downloadingStart": "Starting the download…",
  "st.ui.join.hint.loading": "Your game is loading the host's city. The session starts right after.",
  "st.ui.join.cancel": "Cancel",
  "st.ui.join.percent": "{P} %",
  "st.ui.time.seconds": "{N} s",
  "st.ui.time.minutes": "{M}:{S} min",
  "st.ui.end.retry": "Try again",
  "st.ui.end.retryPassword": "Join with this password",
  "st.ui.end.back": "Back",
  "st.ui.end.copy": "Copy details",
  "st.ui.end.copied": "Copied",
  "st.ui.end.details": "Details from the host",
  "st.ui.end.title.badPassword": "Wrong password",
  "st.ui.end.text.badPassword": "The host refused the password. Check it with the host and try again.",
  "st.ui.end.title.passwordRequired": "This session needs a password",
  "st.ui.end.text.passwordRequired": "Ask the host for the password and enter it below.",
  "st.ui.end.title.mods": "Your mods don't match the host's",
  "st.ui.end.text.mods": "The host requires the same code mods. Change these, restart the game and join again.",
  "st.ui.end.mods.missing": "Enable or subscribe",
  "st.ui.end.mods.extra": "Disable",
  "st.ui.end.mods.versions": "Update to the host's version",
  "st.ui.end.mods.versionLine": "host {HOST} · you {YOU}",
  "st.ui.end.title.game": "Game versions differ",
  "st.ui.end.text.game": "Both players need the same game version. Update the game and join again.",
  "st.ui.end.versions": "Host: {HOST} · You: {YOU}",
  "st.ui.end.title.build": "Skyline Together versions differ",
  "st.ui.end.text.build": "Both players need exactly the same Skyline Together version. Update the mod on both PCs.",
  "st.ui.end.builds": "Host build {HOST} · Your build {YOU}",
  "st.ui.end.title.full": "The session is full",
  "st.ui.end.text.full": "Ask the host to raise the player limit, or try again when someone leaves.",
  "st.ui.end.title.notAllowed": "You can't join this session",
  "st.ui.end.text.notAllowed": "The host's session is open to their Steam friends and invited players only, or direct connections need a password. Ask the host for an invite.",
  "st.ui.end.title.notReady": "The host isn't ready yet",
  "st.ui.end.text.notReady": "The host's session is still starting, or an earlier join attempt is still running. Try again in a moment.",
  "st.ui.end.title.closing": "The host is closing the session",
  "st.ui.end.text.closing": "The session is shutting down.",
  "st.ui.end.title.timeout": "No answer from the host",
  "st.ui.end.text.timeout": "Check the Steam ID or address, and that the host is hosting right now.",
  "st.ui.end.title.lost": "Connection lost",
  "st.ui.end.text.lost": "The connection to the host was interrupted.",
  "st.ui.end.title.kicked": "You were removed from the session",
  "st.ui.end.text.kicked": "The host removed you from the session.",
  "st.ui.end.title.banned": "You are banned from this session",
  "st.ui.end.text.banned": "The host banned you. Only the host can lift the ban.",
  "st.ui.end.title.ended": "The session has ended",
  "st.ui.end.text.ended": "The host ended the session.",
  "st.ui.end.title.failed": "Joining failed",
  "st.ui.end.text.failed": "Something went wrong while joining.",
  "st.ui.session.host": "Host",
  "st.ui.session.player": "Player",
  "st.ui.session.you": "you",
  "st.ui.session.live": "Live",
  "st.ui.session.hosting": "You are hosting",
  "st.ui.session.connected": "Connected",
  "st.ui.session.speed": "Game speed (set by the host): {SPEED}",
  "st.ui.speed.held": "paused while a player joins",
  "st.ui.speed.paused": "paused",
  "st.ui.speed.value": "{N}×",
  "st.ui.session.holding": "A player is joining – the game is paused until they are in.",
  "st.ui.invite.title": "Invite friends",
  "st.ui.invite.steamId": "Your Steam ID",
  "st.ui.invite.hostSteamId": "Host's Steam ID",
  "st.ui.invite.copy": "Copy",
  "st.ui.invite.copied": "Copied",
  "st.ui.invite.steam": "Invite through Steam",
  "st.ui.invite.direct": "Or your public IP address and port {PORT}.",
  "st.ui.friends.title": "Invite Steam friends",
  "st.ui.friends.search": "Search friends",
  "st.ui.friends.invite": "Invite",
  "st.ui.friends.sent": "Sent",
  "st.ui.friends.inSession": "In session",
  "st.ui.friends.state.ingame": "Playing Cities: Skylines II",
  "st.ui.friends.state.online": "Online",
  "st.ui.friends.state.away": "Away",
  "st.ui.friends.state.offline": "Offline",
  "st.ui.friends.groupOnline": "Online ({N})",
  "st.ui.friends.groupOffline": "Offline ({N})",
  "st.ui.friends.none": "No friend matches the search.",
  "st.ui.friends.empty": "No Steam friends to show.",
  "st.ui.friends.overlay": "Open the Steam overlay",
  "st.ui.friends.refresh": "Refresh",
  "st.ui.friends.overlayOnly": "Invite friends through Steam's invite dialog, or share your Steam ID.",
  "st.ui.friends.noOverlay": "The Steam overlay is not available. Share your Steam ID with your friends: they paste it on the Join page.",
  "st.ui.players.title": "Players",
  "st.ui.players.count": "{N} / {MAX}",
  "st.ui.players.kick": "Kick",
  "st.ui.players.makeViewer": "Make viewer",
  "st.ui.players.makeBuilder": "Make builder",
  "st.ui.players.kickConfirm": "Sure?",
  "st.ui.players.ban": "Ban",
  "st.ui.players.banConfirm": "Ban for good?",
  "st.ui.players.makeCoHost": "Make co-host",
  "st.ui.players.removeCoHost": "Remove co-host",
  "st.ui.players.actions": "Player actions",
  "st.ui.players.state.Handshaking": "connecting…",
  "st.ui.players.state.WaitingForSnapshot": "waiting for the city…",
  "st.ui.players.state.Downloading": "downloading {P} %",
  "st.ui.players.state.Loading": "loading {P} %",
  "st.ui.players.state.Offline": "offline",
  "st.ui.players.ping": "{N} ms",
  "st.ui.role.host": "Host",
  "st.ui.role.cohost": "Co-host",
  "st.ui.role.builder": "Builder",
  "st.ui.role.viewer": "Viewer",
  "st.ui.session.cohost": "You are a co-host: you can remove builders and viewers and switch them between building and watching.",
  "st.ui.settings.title": "Session settings",
  "st.ui.settings.livePassword": "New password",
  "st.ui.settings.apply": "Apply",
  "st.ui.chat.title": "Chat",
  "st.ui.chat.placeholder": "Message (Enter to send)",
  "st.ui.chat.overlayPlaceholder": "Say something… (Enter to send, Esc to close)",
  "st.ui.chat.hint": "Press Enter to chat",
  "st.ui.chat.empty": "No messages yet.",
  "st.ui.chat.unread": "{N} new",
  "st.ui.chat.collapse": "Hide the chat",
  "st.ui.chat.expand": "Show the chat",
  "st.ui.net.show": "Show network",
  "st.ui.net.hide": "Hide network",
  "st.ui.net.diagnostics": "Write diagnostics",
  "st.ui.diag.title": "Network & diagnostics",
  "st.ui.leave.client": "Leave session",
  "st.ui.leave.host": "End session",
  "st.ui.leave.confirm": "Click again to confirm",
  // robust branch: recovery after an unexpected disconnect, self-test line
  "st.ui.recovery.lost.title": "Connection lost",
  "st.ui.recovery.lost.text": "Reconnecting in {S} s (attempt {N} of {MAX}). The city is paused until then; changes you make now are not kept.",
  "st.ui.recovery.attempt.title": "Reconnecting (attempt {N} of {MAX})",
  "st.ui.recovery.ended.title": "The session has ended",
  "st.ui.recovery.ended.text": "You can return to the main menu, or keep playing an offline copy of the host's city.",
  "st.ui.recovery.kicked.title": "You were removed from the session",
  "st.ui.recovery.kicked.text": "The host removed you. You can return to the main menu, or keep playing an offline copy of the host's city.",
  "st.ui.recovery.banned.title": "You were removed and banned",
  "st.ui.recovery.banned.text": "The host removed you and banned you from this session. You can return to the main menu, or keep playing an offline copy of the host's city.",
  "st.ui.recovery.failed.title": "Not connected",
  "st.ui.recovery.failed.text": "Reconnecting did not work. You can try again, return to the main menu, or keep an offline copy.",
  "st.ui.recovery.reconnectNow": "Reconnect now",
  "st.ui.recovery.reconnect": "Reconnect",
  "st.ui.recovery.cancel": "Cancel",
  "st.ui.recovery.mainMenu": "Return to main menu",
  "st.ui.recovery.stayOffline": "Stay offline (keep a copy)",
  "st.ui.recovery.badge": "Disconnected",
  "st.ui.resync.title": "Your city differs from the host's",
  "st.ui.resync.text": "Object repairs could not keep up. Rejoin with a fresh copy of the host's city? It takes about as long as joining.",
  "st.ui.resync.now": "Resync now",
  "st.ui.resync.later": "Not now",
  "st.ui.selftest.details": "Details",
  // solo testing (record/replay verdict)
  "st.ui.test.title": "Solo test (no second PC needed)",
  "st.ui.test.subtitle": "Check the sync on this PC, no second player needed",
  "st.ui.test.intro": "Record a session on this PC, then replay it the way a joining player receives it. The replay checks that every object, line, icon and global value ends up exactly as on the host.",
  "st.ui.test.record": "Record test session",
  "st.ui.test.recordHint": "Adds a test recorder to your session (or hosts this city without network first) and records everything a joining player would receive. Build and play as usual, then stop.",
  "st.ui.test.stop": "Stop recording",
  "st.ui.test.needCity": "Load a city first to record a test session.",
  "st.ui.test.recordings": "Recordings",
  "st.ui.test.none": "No recordings yet.",
  "st.ui.test.replay": "Replay & verify",
  "st.ui.test.speed": "Speed",
  "st.ui.test.speedFast": "max",
  "st.ui.test.refresh": "Refresh",
  "st.ui.test.stopReplay": "Stop replay",
  "st.ui.test.replayBanner": "Replaying a recording: nothing is sent over the network. At the end the verdict is saved next to the recording and the session ends.",
  "st.ui.test.replayHint": "Replaying loads the recorded city in place of the one you are playing. Save first.",
  "st.ui.test.verdict.passed": "Replay passed",
  "st.ui.test.verdict.failed": "Replay found differences",
  "st.ui.test.verdict.incomplete": "Replay incomplete",
  "st.ui.test.verdictFile": "Full report: {FOLDER}",
  "st.ui.test.copyPath": "Copy path",
  "st.ui.test.folder": "Recordings folder: {FOLDER}",
  "st.ui.blocked": "Hosting and joining are unavailable: {REASON}",
  "st.ui.footer.build": "Build {BUILD}",
};

// The game's translate(), refreshed by every root on render (useLocalization is a hook).
let translateFn = null;
function useI18n() {
  if (!L10N || typeof L10N.useLocalization !== "function") return;
  try {
    const loc = L10N.useLocalization();
    translateFn = loc && typeof loc.translate === "function" ? loc.translate : null;
  } catch (e) {
    translateFn = null;
  }
}

function t(key, vars) {
  const fallback = Object.prototype.hasOwnProperty.call(EN, key) ? EN[key] : key;
  let s = null;
  if (translateFn) {
    try {
      s = translateFn(key, fallback);
    } catch (e) {
      s = null;
    }
  }
  if (typeof s !== "string" || s.length === 0) s = fallback;
  if (vars) {
    for (const k in vars) s = s.split("{" + k + "}").join(String(vars[k]));
  }
  return s;
}

// ---------------------------------------------------------------------------------------------------------- look

// The game's design tokens (index.css :root, and body.overwrite-legacy for the current interface style). Plain var()
// only: these properties exist in every game version this module supports. Alpha variants of white are literals.
const T = {
  text: "#f0fbff",
  textDim: "rgba(240, 251, 255, 0.72)",
  textDimmer: "rgba(240, 251, 255, 0.5)",
  textFaint: "rgba(240, 251, 255, 0.32)",
  accent: "var(--accentColorNormal)",
  accentLight: "var(--accentColorLight)",
  accentDark: "var(--accentColorDark)",
  titleBlue: "var(--highlightBrightBlue)",
  selected: "var(--selectedColor)",
  good: "var(--positiveColor)",
  warn: "var(--warningColor)",
  bad: "var(--negativeColor)",
  panelDark: "var(--panelColorDark)",
  panelGradient: "linear-gradient(var(--panelGradientStart), var(--panelGradientEnd))",
  sectionHeader: "var(--sectionHeaderColor)",
  section: "var(--sectionBackgroundColor)",
  divider: "var(--dividerColor)",
  hover: "var(--hoverColor)",
  radius: "var(--panelRadius)",
  radiusInner: "var(--panelRadiusInner)",
  blur: "var(--panelBlur)",
  field: "rgba(6, 10, 16, 0.5)",
  fieldBorder: "rgba(255, 255, 255, 0.12)",
  progressTrack: "rgba(255, 255, 255, 0.12)",
  // main menu screens (menu-theme)
  menuPanel: "var(--menuPanel1)",
  menuPanel2: "var(--menuPanel2)",
  menuBlur: "var(--menuBlur)",
  menuText: "var(--menuText1Normal)",
  menuText2: "var(--menuText2Normal)",
  menuInverted: "var(--menuText1Inverted)",
  menuTitle: "var(--menuTitleNormal)",
  menuHover: "var(--menuHoverColor)",
  menuActive: "var(--menuActiveColor)",
  menuSelected: "var(--menuSelectedColor)",
  menuFocused: "var(--menuFocusedColor)",
  menuBorder: "var(--menuBorderColor1)",
  menuBorder2: "var(--menuBorderColor2)",
  menuControl: "var(--menuControl1)",
  menuControlBorder: "var(--menuControlBorder)",
  greenButton: "linear-gradient(90deg, var(--gradientGreenEdge), var(--gradientGreenMiddle), var(--gradientGreenMiddle), var(--gradientGreenEdge))",
  greenButtonHover: "linear-gradient(90deg, var(--gradientLightGreenEdge), var(--gradientLightGreenMiddle), var(--gradientLightGreenMiddle), var(--gradientLightGreenEdge))",
  redButton: "linear-gradient(90deg, var(--gradientRedEdge), var(--gradientRedMiddle), var(--gradientRedMiddle), var(--gradientRedEdge))",
  redButtonHover: "linear-gradient(90deg, var(--gradientLightRedEdge), var(--gradientLightRedMiddle), var(--gradientLightRedMiddle), var(--gradientLightRedEdge))",
  greyButton: "linear-gradient(90deg, var(--gradientGreyEdge), var(--gradientGreyMiddle), var(--gradientGreyMiddle), var(--gradientGreyEdge))",
};
const FS = {
  xs: "var(--fontSizeXS)",
  s: "var(--fontSizeS)",
  m: "var(--fontSizeM)",
  l: "var(--fontSizeL)",
  xl: "var(--fontSizeXL)",
  xxl: "var(--fontSizeXXL)",
};
// Literal colours for the screens that can show over the loading screen (no inherited or theme colour there).
const OV = {
  backdrop: "rgba(7, 11, 17, 0.94)",
  header: "#141b22",
  title: "#4ac0f0",
  card: "linear-gradient(rgb(45, 79, 92), rgb(39, 55, 78))",
  cardSolid: "#2a3e52",
  text: "#f0fbff",
  dim: "rgba(240, 251, 255, 0.75)",
  faint: "rgba(240, 251, 255, 0.45)",
  divider: "rgba(255, 255, 255, 0.12)",
  well: "rgba(0, 0, 0, 0.25)",
  track: "rgba(0, 0, 0, 0.35)",
  accent: "#4bc3f1",
  accentDark: "#1e83aa",
  good: "#8bdb46",
  warn: "#ffa42d",
  bad: "#e95f4a",
  green: "linear-gradient(90deg, #0aae7a, #13a658, #13a658, #0aae7a)",
  greenHover: "linear-gradient(90deg, #19d89b, #2fc95a, #2fc95a, #19d89b)",
  greenBorder: "#48d991",
  button: "rgba(255, 255, 255, 0.1)",
  buttonHover: "rgba(255, 255, 255, 0.2)",
  buttonBorder: "rgba(255, 255, 255, 0.35)",
};

// a border on the given sides, written per side like the game's own stylesheet
function border(width, color, sides) {
  const o = {};
  const list2 = sides || ["Top", "Right", "Bottom", "Left"];
  for (const s of list2) {
    o["border" + s + "Style"] = "solid";
    o["border" + s + "Width"] = width;
    o["border" + s + "Color"] = color;
  }
  return o;
}

// The game's own glyph of the mod's motif (people); drawn as a tinted mask like the game's icons. (An inline
// data: SVG image does not render in the game's browser.)
const ICON_GLYPH = "Media/Game/Icons/Population.svg";
const G = {
  close: "Media/Glyphs/Close.svg",
  back: "Media/Glyphs/TriangleArrowLeft.svg",
  check: "Media/Glyphs/Checkmark.svg",
  more: "Media/Glyphs/More.svg",
  copy: "Media/Glyphs/Copy.svg",
  refresh: "Media/Glyphs/ArrowCircular.svg",
  warning: "Media/Glyphs/Warning.svg",
  info: "Media/Glyphs/Info.svg",
  plus: "Media/Glyphs/Plus.svg",
  down: "Media/Glyphs/ThickStrokeArrowDown.svg",
  right: "Media/Glyphs/ThickStrokeArrowRight.svg",
  up: "Media/Glyphs/ThickStrokeArrowUp.svg",
  lock: "Media/Glyphs/Lock.svg",
};

const S = {
  layer: { position: "absolute", left: 0, top: 0, width: "100%", height: "100%", pointerEvents: "none" },
  gamePanelPos: { position: "absolute", left: "10rem", top: "62rem", pointerEvents: "auto", zIndex: 1000 },
  fill: { position: "absolute", left: 0, top: 0, right: 0, bottom: 0 },
  center: { display: "flex", alignItems: "center", justifyContent: "center" },
  row: { display: "flex", flexDirection: "row", alignItems: "center" },
  col: { display: "flex", flexDirection: "column", alignItems: "stretch" },
  ellipsis: { overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" },
  ids: { letterSpacing: "1rem" },
  link: { color: T.accent, cursor: "pointer" },
};

// ------------------------------------------------------------------------------------------- game components

const MAIN_MENU_PATH = "game-ui/menu/components/main-menu-screen/main-menu-screen.tsx";
const MENU_BUTTON_PATH = "game-ui/common/input/button/labeled-icon-button.tsx";
const MENU_THEME_PATH = "game-ui/menu/themes/menu-item.module.scss";
const CHECKBOX_PATH = "game-ui/common/input/toggle/checkbox/checkbox.tsx";
const FOCUS_PATH = "game-ui/common/focus/focus-key.ts";
const SUB_SCREEN_PATH = "game-ui/menu/components/shared/sub-screen/sub-screen.tsx";
// the vanilla themes (CSS module class maps) this module borrows; their class names change with every game build
const THEME_PATHS = {
  options: "game-ui/menu/components/options-screen/options-screen.module.scss",
  optionPage: "game-ui/menu/components/options-screen/option-page/option-page.module.scss",
  optionHeader: "game-ui/menu/components/options-screen/option-page/option-page-header.module.scss",
  rowField: "game-ui/menu/widgets/field/row-field.module.scss",
  stringField: "game-ui/menu/widgets/string-input-field/string-input-field.module.scss",
  toggleField: "game-ui/menu/widgets/toggle-field/toggle-field.module.scss",
  secondaryButton: "game-ui/menu/themes/secondary-button.module.scss",
  primaryButton: "game-ui/common/input/button/themes/primary-button.module.scss",
  dialogButton: "game-ui/common/input/button/themes/dialog-button.module.scss",
  roundButton: "game-ui/common/input/button/themes/round-highlight-button.module.scss",
  tabs: "game-ui/common/tabs/tabs.module.scss",
  infoSection: "game-ui/game/components/selected-info-panel/shared-components/info-section/info-section.module.scss",
  infoRow: "game-ui/game/components/selected-info-panel/shared-components/info-row/info-row.module.scss",
  tinted: "game-ui/common/image/tinted-icon.module.scss",
};

// Resolved in register(); V.ok turns false when a game component throws while rendering (see Safe).
const V = { ok: true, Panel: null, Button: null, Tooltip: null, FloatingButton: null, Scrollable: null, Checkbox: null, LabeledIconButton: null, SubScreen: null, menuTheme: null, focusDisabled: undefined, themes: {} };

function comp(c) {
  return typeof c === "function" || (c !== null && typeof c === "object" && c.$$typeof !== undefined) ? c : null;
}

function regGet(moduleRegistry, path, name) {
  try {
    const registry = moduleRegistry && moduleRegistry.registry;
    const module = registry && typeof registry.get === "function" ? registry.get(path) : null;
    return module ? module[name] : null;
  } catch (e) {
    return null;
  }
}

function resolveVanilla(moduleRegistry) {
  if (UI) {
    V.Panel = comp(UI.Panel);
    V.Button = comp(UI.Button);
    V.Tooltip = comp(UI.Tooltip);
    V.FloatingButton = comp(UI.FloatingButton);
    V.Scrollable = comp(UI.Scrollable);
  }
  V.Checkbox = comp(regGet(moduleRegistry, CHECKBOX_PATH, "Checkbox"));
  V.LabeledIconButton = comp(regGet(moduleRegistry, MENU_BUTTON_PATH, "LabeledIconButton"));
  V.SubScreen = comp(regGet(moduleRegistry, SUB_SCREEN_PATH, "SubScreen"));
  V.menuTheme = regGet(moduleRegistry, MENU_THEME_PATH, "classes");
  V.focusDisabled = regGet(moduleRegistry, FOCUS_PATH, "FOCUS_DISABLED") || undefined;
  for (const key in THEME_PATHS) {
    const classes = regGet(moduleRegistry, THEME_PATHS[key], "classes");
    if (classes && typeof classes === "object") V.themes[key] = classes;
  }
}

// a vanilla class name, or null (then the caller uses its built-in style); none at all once a game component failed
function vc(theme, key) {
  if (!V.ok) return null;
  const classes = V.themes[theme];
  const name = classes ? classes[key] : null;
  return typeof name === "string" && name.length > 0 ? name : null;
}

// Error boundary around game components: if one throws, the built-in look renders instead (and from then on).
let Boundary = null;
if (ready && typeof React.Component === "function") {
  Boundary = class SkylineTogetherBoundary extends React.Component {
    constructor(props) {
      super(props);
      this.state = { failed: false };
    }

    static getDerivedStateFromError() {
      return { failed: true };
    }

    componentDidCatch(error) {
      V.ok = false;
      console.warn("SkylineTogether: a game component failed (" + error + "); using the built-in look");
    }

    render() {
      if (this.state.failed) return typeof this.props.fallback === "function" ? this.props.fallback() : null;
      return this.props.children;
    }
  };
}

function Safe(node, fallback) {
  return Boundary ? h(Boundary, { fallback: fallback }, node) : node;
}

function withTooltip(node, tooltip, direction) {
  if (!tooltip || !V.ok || !V.Tooltip) return node;
  return Safe(h(V.Tooltip, { tooltip: tooltip, direction: direction || "right" }, node), function () { return node; });
}

// ------------------------------------------------------------------------------------------------ shared state

// Shared by separately appended components (main menu entry, top-left button, roots).
const store = {
  open: false, menuOpen: false, tab: null, gameTab: null, typing: 0, topLeftMounted: false, menuEntryMounted: false, dismissedEnd: -1,
  // chat dock: expanded, last chat line seen while expanded, focus request; invite popover; invites sent (by Steam ID)
  chatOpen: true, chatSeen: -1, chatFocus: false, invitesOpen: false, sent: {},
  listeners: new Set(),
};
// SessionUISystem.KeepPassword: "leave the stored password unchanged" (also before the first state arrived, when
// s.keepPassword is still missing: an empty string there used to erase the saved host password)
const KEEP_PASSWORD = "keep";
let menuButtonInstalled = false;
let menuScreenInstalled = false;
let topLeftRegistered = false;

function emit() {
  store.listeners.forEach(function (l) {
    try {
      l();
    } catch (e) {
      // a listener of an unmounted component
    }
  });
}

function setStore(patch) {
  Object.assign(store, patch);
  emit();
}

// tools/ui_smoke.mjs drives the shared state through this hook; it exists only when the smoke test asks for it.
if (typeof window !== "undefined" && window.__skylineTogetherSmoke) {
  window.__skylineTogetherSmoke.store = store;
  window.__skylineTogetherSmoke.V = V;
}

// Opens the multiplayer screen of the main menu (the vanilla menu entry calls it); joining is the default page there.
function openPanel() {
  setStore({ menuOpen: true, tab: store.tab === "solo" || store.tab === "host" ? store.tab : "join" });
}

function useStore() {
  const pair = React.useState(0);
  const force = pair[1];
  React.useEffect(function () {
    const l = function () {
      force(function (n) {
        return n + 1;
      });
    };
    store.listeners.add(l);
    return function () {
      store.listeners.delete(l);
    };
  }, []);
  return store;
}

// the type of the open game panel (photo mode, chirper, ...), or ""
function useGamePanel() {
  const game = GAME_BINDINGS && GAME_BINDINGS.game;
  if (!game || !game.activeGamePanel$ || !game.GamePanelType) return "";
  try {
    const panel = api.useValue(game.activeGamePanel$);
    if (!panel || panel.__Type === undefined) return "";
    const types = game.GamePanelType;
    for (const name of ["PhotoMode", "Chirper", "Radio", "LifePath", "Journal", "Notifications"]) {
      if (types[name] !== undefined && panel.__Type === types[name]) return name;
    }
    return "other";
  } catch (e) {
    return "";
  }
}

function keyOf(e) {
  if (!e) return "";
  if (e.key === "Enter" || e.keyCode === 13) return "Enter";
  if (e.key === "Escape" || e.key === "Esc" || e.keyCode === 27) return "Escape";
  return e.key || "";
}

function useConfirm() {
  const [armed, setArmed] = React.useState(false);
  const timer = React.useRef(null);
  React.useEffect(function () {
    return function () {
      if (timer.current) clearTimeout(timer.current);
    };
  }, []);
  return {
    armed: armed,
    run: function (action) {
      if (armed) {
        setArmed(false);
        action();
        return;
      }
      setArmed(true);
      if (timer.current) clearTimeout(timer.current);
      timer.current = setTimeout(function () {
        setArmed(false);
      }, 3000);
    },
  };
}

function useFlash() {
  const [on, setOn] = React.useState(false);
  const timer = React.useRef(null);
  React.useEffect(function () {
    return function () {
      if (timer.current) clearTimeout(timer.current);
    };
  }, []);
  return {
    on: on,
    fire: function () {
      setOn(true);
      if (timer.current) clearTimeout(timer.current);
      timer.current = setTimeout(function () {
        setOn(false);
      }, 2000);
    },
  };
}

// hover state for the built-in look (the vanilla classes bring their own :hover)
function useHover() {
  const [hover, setHover] = React.useState(false);
  return [hover, { onMouseEnter: function () { setHover(true); }, onMouseLeave: function () { setHover(false); } }];
}

// --------------------------------------------------------------------------------------------------- primitives

// A monochrome game glyph tinted with a colour (how the game's TintedIcon draws Media/Glyphs).
function Glyph(p) {
  const size = p.size || 16;
  return h("div", {
    className: vc("tinted", "tintedIcon") || undefined,
    style: merge(
      { width: size + "rem", height: size + "rem", flexShrink: 0, backgroundColor: p.color || T.text, maskImage: "url(" + p.src + ")", maskSize: "contain", maskPosition: "center", maskRepeat: "no-repeat" },
      p.style
    ),
  });
}

// Built-in buttons: the same looks as the game's themes (dialog green, flat navy, menu secondary, ...).
const FB_LOOK = {
  primary: function (hv, dis) {
    return dis
      ? merge({ backgroundImage: T.greyButton, color: "#bfbfbf" }, border("2rem", "#a1a1a1"))
      : merge({ backgroundImage: hv ? T.greenButtonHover : T.greenButton, color: "#ffffff" }, border("2rem", hv ? "#8affc5" : "#48d991"));
  },
  danger: function (hv, dis) {
    return merge({ backgroundImage: dis ? T.greyButton : hv ? T.redButtonHover : T.redButton, color: "#ffffff" }, border("2rem", dis ? "#a1a1a1" : hv ? "#ff9ca2" : "#f36d76"));
  },
  flat: function (hv, dis) {
    return merge({ backgroundColor: hv && !dis ? "#002e71" : "#00193e", color: dis ? "rgba(255, 255, 255, 0.45)" : "#ffffff" }, border("1rem", "rgba(0, 0, 0, 0.2)"));
  },
  menuPrimary: function (hv, dis) {
    return FB_LOOK.primary(hv, dis);
  },
  menuSecondary: function (hv, dis) {
    return dis ? { backgroundColor: "rgba(34, 34, 34, 0.75)", color: "rgba(102, 102, 102, 0.8)" } : { backgroundColor: hv ? "rgba(255, 255, 255, 0.75)" : "rgba(255, 255, 255, 0.15)", color: hv ? "rgba(12, 26, 37, 0.8)" : "rgba(255, 255, 255, 0.8)" };
  },
  icon: function (hv) {
    return { backgroundColor: hv ? "rgba(255, 255, 255, 0.2)" : "rgba(255, 255, 255, 0)" };
  },
  tab: function (hv, dis, sel) {
    return sel ? { backgroundImage: "linear-gradient(var(--gradientHighlightStart), var(--gradientHighlightEnd))", color: "#ffffff" } : { backgroundColor: hv ? "rgba(255, 255, 255, 0.15)" : "rgba(255, 255, 255, 0.1)", color: "rgba(255, 255, 255, 0.8)" };
  },
  navItem: function (hv, dis, sel) {
    return sel ? { backgroundColor: T.menuFocused, color: T.menuInverted } : { backgroundColor: hv ? T.menuHover : "rgba(0, 0, 0, 0)", color: T.menuText };
  },
};
const FB_SHAPE = {
  primary: { borderRadius: "5rem", padding: "8rem 14rem", fontSize: FS.m, textTransform: "uppercase" },
  danger: { borderRadius: "5rem", padding: "8rem 14rem", fontSize: FS.m, textTransform: "uppercase" },
  flat: { borderRadius: "8rem", padding: "5rem 12rem", fontSize: FS.s },
  menuPrimary: { borderRadius: "5rem", padding: "10rem 24rem", fontSize: FS.xl, textTransform: "uppercase" },
  menuSecondary: { borderRadius: "4rem", padding: "6rem 24rem", fontSize: FS.s, textTransform: "uppercase" },
  icon: { borderRadius: "50%", width: "28rem", height: "28rem", padding: 0 },
  tab: { borderRadius: "9rem 9rem 0 0", padding: "7rem 15rem 5rem", fontSize: FS.l, textTransform: "uppercase" },
  navItem: { borderRadius: "4rem", padding: "8rem 12rem", fontSize: FS.xl, textTransform: "uppercase", justifyContent: "flex-start", textAlign: "left" },
};

// the vanilla theme for each kind: { theme: {button: "..."} } or { variant: "..." }, null when unavailable
function vanillaButton(kind) {
  if (kind === "primary" || kind === "danger") {
    const b = vc("dialogButton", "button");
    if (!b) return null;
    return { theme: { button: kind === "danger" ? cx(b, vc("dialogButton", "negative")) : b } };
  }
  if (kind === "flat") return { variant: "flat" };
  if (kind === "menuPrimary") return { variant: "primary" };
  if (kind === "menuSecondary") return vc("secondaryButton", "button") ? { theme: { button: vc("secondaryButton", "button") } } : null;
  if (kind === "icon") return vc("roundButton", "button") ? { theme: { button: vc("roundButton", "button") } } : null;
  if (kind === "tab") return vc("tabs", "tab") ? { theme: { button: vc("tabs", "tab") } } : null;
  if (kind === "navItem") return vc("options", "item") ? { theme: { button: cx(vc("options", "item"), vc("options", "primary")) } } : null;
  return null;
}

// layout the vanilla themes leave to the caller (the flat theme has no padding; dialog buttons are sized for dialogs)
const CENTERED = { display: "flex", flexDirection: "row", alignItems: "center", justifyContent: "center", whiteSpace: "nowrap" };
const VANILLA_LAYOUT = {
  primary: merge(CENTERED, { padding: "7rem 14rem" }),
  danger: merge(CENTERED, { padding: "7rem 14rem" }),
  flat: merge(CENTERED, { padding: "5rem 12rem", fontSize: FS.s }),
  menuPrimary: CENTERED,
  menuSecondary: null,
  icon: null,
  tab: null,
  navItem: { display: "flex", flexDirection: "row", alignItems: "center", justifyContent: "flex-start" },
};

function FallbackButton(p) {
  const [hover, hv] = useHover();
  const kind = p.kind;
  const look = (FB_LOOK[kind] || FB_LOOK.flat)(hover, p.disabled, p.selected);
  return h(
    "div",
    merge(hv, {
      style: merge(
        { display: "flex", flexDirection: "row", alignItems: "center", justifyContent: "center", flexShrink: 0, whiteSpace: "nowrap", cursor: p.disabled ? "default" : "pointer", opacity: p.disabled && kind !== "primary" ? 0.55 : 1 },
        FB_SHAPE[kind] || FB_SHAPE.flat,
        look,
        p.style
      ),
      onClick: p.onSelect,
    }),
    p.children
  );
}

// kind: primary (dialog green), danger (dialog red), flat (vanilla navy), menuPrimary (big green), menuSecondary,
// icon (round highlight, children = Glyph), tab, navItem (options menu entry)
function Btn(p) {
  const kind = p.kind || "flat";
  const disabled = !!p.disabled;
  const onSelect = function () {
    if (!disabled && p.onClick) p.onClick();
  };
  const fallback = function () {
    return h(FallbackButton, { kind: kind, disabled: disabled, selected: !!p.selected, onSelect: onSelect, style: p.style }, p.children);
  };
  const vanilla = V.ok && V.Button ? vanillaButton(kind) : null;
  if (!vanilla) return fallback();
  const props = merge(vanilla, { disabled: disabled, selected: !!p.selected, onSelect: onSelect, focusKey: V.focusDisabled, style: merge(VANILLA_LAYOUT[kind], p.style) });
  return Safe(h(V.Button, props, p.children), fallback);
}

function IconBtn(p) {
  return withTooltip(
    h(Btn, { kind: "icon", onClick: p.onClick, disabled: p.disabled, style: merge({ width: (p.box || 28) + "rem", height: (p.box || 28) + "rem", padding: 0, display: "flex", alignItems: "center", justifyContent: "center", flexShrink: 0 }, p.style) }, h(Glyph, { src: p.src, size: p.size || 14, color: p.color || T.text })),
    p.tooltip,
    p.tooltipDirection || "down"
  );
}

function LinkText(p) {
  const [hover, hv] = useHover();
  return h("span", merge(hv, { style: merge(S.link, { color: p.color || T.accent, textDecoration: hover ? "underline" : "none" }, p.style), onClick: p.onClick }), p.children);
}

// A plain input: while it has focus the game turns keyboard actions off (InputManager.hasInputFieldFocus), so typing
// never triggers game or mod hotkeys; vk-* attributes give the on-screen keyboard (gamepad) a title and type.
// look: "game" (in-game panels), "menu" (the vanilla options string field), "overlay" (literal colours)
function Field(p) {
  const [focus, setFocus] = React.useState(false);
  // final review: an input unmounted while focused (Enter on Join, a session ending while typing) never gets its blur;
  // the typing count then stayed above zero and Enter never opened the chat again
  const focusedRef = React.useRef(false);
  React.useEffect(function () {
    return function () {
      if (focusedRef.current) {
        focusedRef.current = false;
        store.typing = Math.max(0, store.typing - 1);
      }
    };
  }, []);
  const look = p.look || "game";
  const menuClass = look === "menu" ? vc("stringField", "stringField") : null;
  let style;
  if (menuClass) style = { flex: "0 1 50%", minWidth: 0 };
  else if (look === "menu")
    style = merge({ flex: "0 1 50%", minWidth: 0, backgroundColor: T.menuControl, color: T.menuText, borderRadius: "3rem", padding: "4rem 8rem", fontSize: "1em" }, border("2rem", focus ? T.accent : T.menuControlBorder));
  else if (look === "overlay")
    style = merge({ width: "100%", height: "36rem", padding: "0 10rem", backgroundColor: "rgba(0, 0, 0, 0.35)", color: OV.text, borderRadius: "4rem", fontSize: "16rem" }, border("2rem", focus ? OV.accent : "rgba(255, 255, 255, 0.18)"));
  else
    style = merge(
      { width: "100%", height: "32rem", padding: "0 10rem", backgroundColor: focus ? "rgba(255, 255, 255, 0.08)" : T.field, color: T.text, borderRadius: T.radiusInner, fontSize: FS.m },
      border("2rem", focus ? T.accent : T.fieldBorder)
    );
  return h("input", {
    ref: p.inputRef,
    type: p.password ? "password" : "text",
    value: p.value == null ? "" : String(p.value),
    placeholder: p.placeholder || "",
    maxLength: p.max || 128,
    "vk-title": p.title || p.placeholder || "",
    "vk-description": p.description || "",
    "vk-type": p.password ? "password" : "text",
    className: menuClass || undefined,
    style: merge(style, p.ids ? S.ids : null, p.style),
    onFocus: function () {
      setFocus(true);
      if (!focusedRef.current) {
        focusedRef.current = true;
        store.typing++;
      }
      if (p.onFocus) p.onFocus();
    },
    onBlur: function () {
      setFocus(false);
      if (focusedRef.current) {
        focusedRef.current = false;
        store.typing = Math.max(0, store.typing - 1);
      }
      if (p.onBlur) p.onBlur();
    },
    onChange: function (e) {
      if (p.onChange) p.onChange(e && e.target ? e.target.value : "");
    },
    onKeyDown: function (e) {
      const k = keyOf(e);
      if (k === "Enter" && p.onEnter) p.onEnter();
      if (k === "Escape") {
        if (p.onEscape) p.onEscape();
        try {
          if (e.target && e.target.blur) e.target.blur();
        } catch (err) {
          // already blurred
        }
      }
      if (e && e.stopPropagation) e.stopPropagation();
    },
  });
}

// The vanilla checkbox (with the options-screen theme in the menu), or a built-in box of the same look.
function Check(p) {
  const toggle = function () {
    if (!p.disabled && p.onChange) p.onChange(!p.checked);
  };
  const fallback = function () {
    return h(
      "div",
      {
        style: merge({ width: "24rem", height: "24rem", padding: "3rem", borderRadius: "3rem", flexShrink: 0, cursor: "pointer", opacity: p.disabled ? 0.5 : 1, backgroundColor: p.menu ? T.menuControl : "rgba(0, 0, 0, 0)" }, border("2rem", p.menu ? T.menuControlBorder : T.accentLight)),
        onClick: toggle,
      },
      p.checked ? h(Glyph, { src: G.check, size: 14 }) : null
    );
  };
  if (!V.ok || !V.Checkbox) return fallback();
  return Safe(h(V.Checkbox, { checked: !!p.checked, disabled: !!p.disabled, className: p.menu ? vc("toggleField", "toggle") || undefined : undefined, onChange: toggle }), fallback);
}

function Stepper(p) {
  const value = Math.max(p.min, Math.min(p.max, p.value || p.min));
  return h(
    "div",
    { style: merge(S.row, { flexShrink: 0 }) },
    h(IconBtn, { src: G.down, size: 12, disabled: value <= p.min, onClick: function () { p.onChange(value - 1); }, style: { transform: "rotate(90deg)" } }),
    h("div", { style: { width: "40rem", textAlign: "center", fontSize: p.menu ? FS.l : FS.m, color: p.menu ? T.menuText : T.text } }, String(value)),
    h(IconBtn, { src: G.down, size: 12, disabled: value >= p.max, onClick: function () { p.onChange(value + 1); }, style: { transform: "rotate(-90deg)" } })
  );
}

function Chip(p) {
  return h(
    "div",
    { style: merge({ padding: "1rem 7rem", borderRadius: "10rem", fontSize: FS.xs, textTransform: "uppercase", whiteSpace: "nowrap", color: p.color || T.textDim, flexShrink: 0 }, border("1rem", p.color || T.textDimmer), p.style) },
    p.children
  );
}

function Dot(p) {
  const size = p.size || 10;
  return h("div", { style: merge({ width: size + "rem", height: size + "rem", borderRadius: size / 2 + "rem", backgroundColor: p.color, flexShrink: 0, marginRight: (p.gap == null ? 8 : p.gap) + "rem" }, p.style) });
}

function Progress(p) {
  const pct = Math.max(0, Math.min(100, Math.round((p.value || 0) * 100)));
  return h(
    "div",
    { style: merge({ height: (p.height || 6) + "rem", backgroundColor: p.track || T.progressTrack, borderRadius: (p.height || 6) / 2 + "rem", overflow: "hidden" }, p.style) },
    h("div", { style: { width: pct + "%", height: "100%", backgroundColor: p.color || T.accent, borderRadius: (p.height || 6) / 2 + "rem", transition: "width 300ms" } })
  );
}

// Signal bars like a phone: green under 80 ms, amber under 250 ms, red above.
function PingBars(p) {
  const ping = p.ping;
  if (ping == null || ping < 0) return null;
  const n = ping < 80 ? 4 : ping < 150 ? 3 : ping < 250 ? 2 : 1;
  const color = n >= 3 ? T.good : n === 2 ? T.warn : T.bad;
  const bars = [];
  for (let i = 0; i < 4; i++) {
    bars.push(h("div", { key: i, style: { width: "3rem", height: 4 + i * 3 + "rem", marginLeft: "2rem", borderRadius: "1rem", backgroundColor: i < n ? color : "rgba(255, 255, 255, 0.18)" } }));
  }
  return h("div", { style: { display: "flex", flexDirection: "row", alignItems: "flex-end", height: "13rem" } }, bars);
}

// Vanilla scroll area (thin game scrollbar) or a plain scrolling div.
function Scroll(p) {
  const style = merge({ flex: p.flex ? 1 : undefined, maxHeight: p.maxHeight }, p.style);
  const fallback = function () {
    return h("div", { ref: p.scrollRef, className: p.className, style: merge({ overflowY: "auto" }, style) }, p.children);
  };
  if (!V.ok || !V.Scrollable) return fallback();
  return Safe(h(V.Scrollable, { ref: p.scrollRef, className: p.className, style: style }, p.children), fallback);
}

function mb(bytes) {
  return (Math.max(0, Number(bytes) || 0) / 1048576).toFixed(1);
}

function duration(seconds) {
  const sec = Math.round(Number(seconds));
  if (!(sec >= 0) || !isFinite(sec)) return "–";
  if (sec < 60) return t("st.ui.time.seconds", { N: sec });
  const s = sec % 60;
  return t("st.ui.time.minutes", { M: Math.floor(sec / 60), S: (s < 10 ? "0" : "") + s });
}

function clock(ms) {
  try {
    const d = new Date(ms);
    const m = d.getMinutes();
    return d.getHours() + ":" + (m < 10 ? "0" : "") + m;
  } catch (e) {
    return "";
  }
}

// ------------------------------------------------------------------------------------- in-game panel pieces

// A section of a vanilla in-game info panel (info-section: 6rem padding, divider lines between sections).
function Section(p) {
  const cls = vc("infoSection", "infoSection");
  return h(
    "div",
    { className: cls || undefined, style: cls ? merge({ position: "relative" }, p.style) : merge({ padding: "6rem", position: "relative" }, p.first ? null : border("2rem", T.divider, ["Top"]), p.style) },
    p.title
      ? h(
          "div",
          { style: merge(S.row, { padding: "4rem 10rem 6rem 10rem", minHeight: "24rem" }) },
          h("div", { style: merge(S.ellipsis, { flex: 1, fontSize: FS.s, textTransform: "uppercase", color: T.textDim, fontWeight: "bold" }) }, p.title),
          p.right || null
        )
      : null,
    h("div", { style: p.pad === false ? null : { padding: "0 4rem" } }, p.children)
  );
}

// A foldout section (vanilla info-section foldout: arrow glyph, uppercase header).
function Foldout(p) {
  const [open, setOpen] = React.useState(!!p.initialOpen);
  const [hover, hv] = useHover();
  return h(
    Section,
    { pad: false },
    h(
      "div",
      merge(hv, {
        style: merge(S.row, { padding: "6rem 10rem", borderRadius: T.radiusInner, cursor: "pointer", backgroundColor: hover ? T.hover : "rgba(0, 0, 0, 0)" }),
        onClick: function () { setOpen(!open); },
      }),
      h(Glyph, { src: open ? G.down : G.right, size: 10, color: T.textDim, style: { marginRight: "8rem" } }),
      h("div", { style: merge(S.ellipsis, { flex: 1, fontSize: FS.s, textTransform: "uppercase", color: T.textDim, fontWeight: "bold" }) }, p.title),
      p.right || null
    ),
    open ? h("div", { style: { padding: "2rem 4rem 4rem 4rem" } }, p.children) : null
  );
}

function Hint(p) {
  return h("div", { style: merge({ color: T.textDim, fontSize: FS.s, lineHeight: 1.35 }, p.style) }, p.children);
}

function Label(p) {
  return h("div", { style: merge({ color: T.textDimmer, fontSize: FS.xs, textTransform: "uppercase", marginBottom: "4rem", letterSpacing: "0.5rem" }, p.style) }, p.children);
}

// a labelled in-game field (label above, optional hint below)
function GameField(p) {
  return h("div", { style: merge({ padding: "5rem 6rem" }, p.style) }, h(Label, null, p.label), p.children, p.hint ? h(Hint, { style: { marginTop: "4rem", fontSize: FS.xs } }, p.hint) : null);
}

// a row with a label (and hint) left, a control right (vanilla info-row proportions)
function GameRow(p) {
  return h(
    "div",
    { style: merge(S.row, { padding: "6rem 6rem", minHeight: "36rem" }, p.style) },
    h("div", { style: { flex: 1, marginRight: "10rem", minWidth: 0 } }, h("div", { style: { color: T.text, fontSize: FS.s } }, p.label), p.hint ? h(Hint, { style: { marginTop: "2rem", fontSize: FS.xs } }, p.hint) : null),
    p.children
  );
}

function Notice(p) {
  const color = p.color || T.warn;
  return h(
    "div",
    { style: merge(S.row, { alignItems: "flex-start", padding: "8rem 10rem", margin: "4rem 6rem", borderRadius: T.radiusInner, backgroundColor: "rgba(0, 0, 0, 0.22)" }, border("3rem", color, ["Left"]), p.style) },
    h(Glyph, { src: p.icon || G.warning, size: 14, color: color, style: { marginRight: "8rem", marginTop: "2rem" } }),
    h("div", { style: { flex: 1, color: T.text, fontSize: FS.s, lineHeight: 1.35 } }, p.children)
  );
}

// The in-game panel: the vanilla cs2/ui Panel (title bar with close button, scrolling content, footer), or a copy.
function PanelShell(p) {
  const width = p.width || "420rem";
  const maxHeight = p.maxHeight || "78vh";
  const header = h(
    "div",
    { style: merge(S.row, { justifyContent: "center", minWidth: 0 }) },
    h(Glyph, { src: ICON_GLYPH, size: 20, color: T.titleBlue, style: { marginRight: "8rem" } }),
    h("div", { style: merge(S.ellipsis, { textTransform: "uppercase" }) }, p.title),
    p.status ? h(Dot, { color: p.status, size: 8, gap: 0, style: { marginLeft: "8rem" } }) : null
  );
  const fallback = function () {
    return h(
      "div",
      { style: merge(S.col, { width: width, maxHeight: maxHeight, borderRadius: T.radius, pointerEvents: "auto", boxShadow: "0 4rem 20rem rgba(0, 0, 0, 0.3)" }) },
      h(
        "div",
        { style: merge(S.row, { minHeight: "40rem", padding: "6rem 10rem", backgroundColor: T.panelDark, backdropFilter: T.blur, color: T.titleBlue, borderTopLeftRadius: T.radius, borderTopRightRadius: T.radius }) },
        h("div", { style: { width: "28rem", flexShrink: 0 } }),
        h("div", { style: merge(S.row, { flex: 1, justifyContent: "center", minWidth: 0 }) }, header),
        h(IconBtn, { src: G.close, size: 12, onClick: p.onClose })
      ),
      h(
        "div",
        { style: merge(S.col, { flex: 1, minHeight: 0, backgroundColor: "rgba(42, 55, 83, 0.6)", backgroundImage: T.panelGradient, backdropFilter: T.blur, color: T.text }, p.footer ? null : { borderBottomLeftRadius: T.radius, borderBottomRightRadius: T.radius }) },
        h(Scroll, { flex: true }, p.children)
      ),
      p.footer
        ? h("div", { style: { padding: "8rem 10rem", backgroundColor: T.panelDark, backdropFilter: T.blur, borderBottomLeftRadius: T.radius, borderBottomRightRadius: T.radius } }, p.footer)
        : null
    );
  };
  if (!V.ok || !V.Panel) return fallback();
  return Safe(
    h(V.Panel, { header: header, onClose: p.onClose, footer: p.footer ? h("div", { style: { padding: "8rem 10rem" } }, p.footer) : undefined, style: { width: width, maxHeight: maxHeight } }, h("div", { style: { color: T.text } }, p.children)),
    fallback
  );
}

// ---------------------------------------------------------------------------------------------- session view

function speedText(s) {
  if (s.speedHeld) return t("st.ui.speed.held");
  if (s.speedPaused) return t("st.ui.speed.paused");
  if (s.speedValue > 0) return t("st.ui.speed.value", { N: s.speedValue });
  return s.speed || "";
}

function roleText(role) {
  return role ? t("st.ui.role." + role) : "";
}

function playerStateText(p) {
  const pct = Math.round((p.progress || 0) * 100);
  if (p.state === "Live") return "";
  return t("st.ui.players.state." + p.state, { P: pct });
}

// what each remote player is doing, by player id (presence branch)
function useActivities() {
  const map = {};
  list(useJson(presenceBinding).tags).forEach(function (tag) {
    if (tag && tag.id !== undefined) map[tag.id] = tag;
  });
  return map;
}

function PlayerRow(p) {
  const pl = p.player;
  const s = p.state;
  const confirm = useConfirm();
  const confirmBan = useConfirm();
  const [menu, setMenu] = React.useState(false);
  const [hover, hv] = useHover();
  const doing = p.activity;
  const color = pl.color || (doing && doing.color) || T.accent;
  const joining = pl.state !== "Live";
  const hosting = s.mode === "Hosting";
  // w8/security: a co-host may remove builders and viewers and switch them (the host checks every request)
  const coHostHere = s.mode === "Client" && s.localRole === "cohost" && !pl.host && !pl.you && (pl.role === "builder" || pl.role === "viewer");
  const canManage = (hosting && !pl.host) || coHostHere;
  const actionGap = { marginLeft: "6rem", marginTop: "4rem" };
  return h(
    "div",
    { style: { borderRadius: T.radiusInner, backgroundColor: menu ? "rgba(0, 0, 0, 0.18)" : hover ? T.hover : "rgba(0, 0, 0, 0)" } },
    h(
      "div",
      merge(hv, { style: merge(S.row, { padding: "6rem 6rem 6rem 10rem", minHeight: "36rem" }) }),
      h(Dot, { color: color, size: 10, gap: 10, style: border("2rem", "rgba(255, 255, 255, 0.25)") }),
      h(
        "div",
        { style: { flex: 1, minWidth: 0 } },
        h(
          "div",
          { style: S.row },
          h("div", { style: merge(S.ellipsis, { fontSize: FS.m, color: T.text, minWidth: 0 }) }, pl.name),
          pl.you ? h("div", { style: { color: T.textDimmer, fontSize: FS.s, marginLeft: "6rem", flexShrink: 0 } }, "(" + t("st.ui.session.you") + ")") : null,
          pl.role ? h(Chip, { color: pl.host ? T.accent : T.textDim, style: { marginLeft: "8rem" } }, roleText(pl.role)) : null
        ),
        joining
          ? h("div", { style: { marginTop: "4rem" } }, h("div", { style: { color: T.warn, fontSize: FS.xs, marginBottom: "3rem" } }, playerStateText(pl)), pl.state === "Downloading" || pl.state === "Loading" ? h(Progress, { value: pl.progress, height: 4 }) : null)
          : doing && doing.activity
            ? h("div", { style: merge(S.ellipsis, { color: T.textDimmer, fontSize: FS.xs, marginTop: "2rem" }) }, doing.activity)
            : null
      ),
      !pl.host && !joining
        ? h("div", { style: merge(S.row, { marginLeft: "8rem", flexShrink: 0 }) }, h(PingBars, { ping: pl.ping }), h("div", { style: { color: T.textDimmer, fontSize: FS.xs, marginLeft: "5rem", width: "42rem", textAlign: "right" } }, pl.ping >= 0 ? t("st.ui.players.ping", { N: pl.ping }) : ""))
        : null,
      canManage ? h(IconBtn, { src: G.more, size: 14, tooltip: t("st.ui.players.actions"), style: { marginLeft: "6rem" }, onClick: function () { setMenu(!menu); } }) : null
    ),
    canManage && menu
      ? h(
          "div",
          { style: merge(S.row, { justifyContent: "flex-end", flexWrap: "wrap", padding: "0 6rem 8rem 6rem" }) },
          !joining && pl.role !== "cohost" // gameplay branch: viewers watch, builders build
            ? h(Btn, { kind: "flat", style: actionGap, onClick: function () { send("toggleRole", pl.id); } }, pl.role === "viewer" ? t("st.ui.players.makeBuilder") : t("st.ui.players.makeViewer"))
            : null,
          hosting && !joining // w8/security: co-hosts help moderate
            ? h(Btn, { kind: "flat", style: actionGap, onClick: function () { send("toggleCoHost", pl.id); } }, pl.role === "cohost" ? t("st.ui.players.removeCoHost") : t("st.ui.players.makeCoHost"))
            : null,
          h(Btn, { kind: confirm.armed ? "danger" : "flat", style: actionGap, onClick: function () { confirm.run(function () { send("kick", pl.id); }); } }, confirm.armed ? t("st.ui.players.kickConfirm") : t("st.ui.players.kick")),
          hosting
            ? h(Btn, { kind: confirmBan.armed ? "danger" : "flat", style: actionGap, onClick: function () { confirmBan.run(function () { send("ban", pl.id); }); } }, confirmBan.armed ? t("st.ui.players.banConfirm") : t("st.ui.players.ban"))
            : null
        )
      : null
  );
}

function Players(p) {
  const s = p.state;
  const players = list(s.players);
  const doing = useActivities();
  return h(
    Section,
    { title: t("st.ui.players.title"), right: h("div", { style: { color: T.textDimmer, fontSize: FS.s } }, s.mode === "Hosting" ? t("st.ui.players.count", { N: players.length, MAX: s.maxPlayers || "?" }) : String(players.length)) },
    players.map(function (pl) {
      return h(PlayerRow, { key: pl.id, player: pl, state: s, activity: doing[pl.id] });
    })
  );
}

// The session header: status, Steam ID with copy, invites.
function SessionHeader(p) {
  const s = p.state;
  const hosting = s.mode === "Hosting";
  const flash = useFlash();
  const st = useStore();
  const id = s.inviteSteamId || s.steamId || "";
  const overlay = useOverlayAvailable();
  // the host invites from the friends list (works without the overlay); a player can only open Steam's dialog
  const canInviteHere = !!s.canInvite && (hosting || overlay);
  return h(
    Section,
    { first: true },
    h(
      "div",
      { style: merge(S.row, { padding: "4rem 6rem 8rem 6rem" }) },
      h(Dot, { color: T.good, size: 10 }),
      h("div", { style: { flex: 1, fontSize: FS.m, color: T.text } }, hosting ? t("st.ui.session.hosting") : t("st.ui.session.connected")),
      h(Chip, { color: T.good }, t("st.ui.session.live"))
    ),
    !hosting ? h(Hint, { style: { padding: "0 6rem 6rem 6rem" } }, t("st.ui.session.speed", { SPEED: speedText(s) })) : null,
    !hosting && s.localRole === "cohost" ? h(Hint, { style: { padding: "0 6rem 6rem 6rem" } }, t("st.ui.session.cohost")) : null,
    s.hold ? h(Notice, null, s.status || t("st.ui.session.holding")) : null,
    h(
      "div",
      { style: { margin: "4rem 6rem 2rem 6rem", padding: "8rem 10rem", borderRadius: T.radiusInner, backgroundColor: "rgba(0, 0, 0, 0.2)" } },
      h(Label, null, hosting ? t("st.ui.invite.steamId") : t("st.ui.invite.hostSteamId")),
      h(
        "div",
        { style: S.row },
        h("div", { style: merge(S.ellipsis, S.ids, { flex: 1, fontSize: FS.l, color: T.text }) }, id),
        h(IconBtn, { src: flash.on ? G.check : G.copy, size: 14, color: flash.on ? T.good : T.text, tooltip: flash.on ? t("st.ui.invite.copied") : t("st.ui.invite.copy"), onClick: function () { send("copySteamId"); flash.fire(); } })
      ),
      canInviteHere
        ? h(
            "div",
            { style: merge(S.row, { marginTop: "8rem" }) },
            h(
              Btn,
              { kind: "flat", selected: hosting && st.invitesOpen, style: { flex: 1 }, onClick: function () { if (hosting) setStore({ invitesOpen: !store.invitesOpen }); else send("inviteFriends"); } },
              h(Glyph, { src: G.plus, size: 12, style: { marginRight: "6rem" } }),
              t("st.ui.invite.title")
            )
          )
        : null,
      hosting && s.directPort > 0 ? h(Hint, { style: { marginTop: "6rem", fontSize: FS.xs } }, t("st.ui.invite.direct", { PORT: s.directPort })) : null
    )
  );
}

const FRIEND_ORDER = { ingame: 0, online: 1, away: 2, offline: 3 };
function friendColor(state) {
  return state === "ingame" ? T.good : state === "online" ? T.accent : state === "away" ? T.warn : "rgba(255, 255, 255, 0.3)";
}

function FriendRow(p) {
  const f = p.friend;
  const st = useStore();
  const [hover, hv] = useHover();
  const sent = !!st.sent[f.id];
  const state = FRIEND_ORDER[f.state] !== undefined ? f.state : "offline";
  return h(
    "div",
    merge(hv, { style: merge(S.row, { padding: "5rem 8rem", minHeight: "36rem", borderRadius: T.radiusInner, backgroundColor: hover ? T.hover : "rgba(0, 0, 0, 0)" }) }),
    h(Dot, { color: friendColor(state), size: 8, gap: 10 }),
    h(
      "div",
      { style: { flex: 1, minWidth: 0 } },
      h("div", { style: merge(S.ellipsis, { fontSize: FS.s, color: state === "offline" ? T.textDimmer : T.text }) }, f.name || f.id),
      h("div", { style: { fontSize: FS.xs, color: T.textDimmer } }, t("st.ui.friends.state." + state))
    ),
    f.inSession
      ? h(Chip, { color: T.good }, t("st.ui.friends.inSession"))
      : h(
          Btn,
          {
            kind: sent ? "flat" : "primary",
            disabled: sent,
            style: { padding: "3rem 10rem", fontSize: FS.xs, marginLeft: "6rem" },
            onClick: function () {
              send("inviteFriend", String(f.id));
              const next = Object.assign({}, store.sent);
              next[f.id] = true;
              setStore({ sent: next });
            },
          },
          sent ? h(Glyph, { src: G.check, size: 10, style: { marginRight: "4rem" } }) : null,
          sent ? t("st.ui.friends.sent") : t("st.ui.friends.invite")
        )
  );
}

// The host's invite popover beside the panel: Steam friends (online first) with Invite buttons, or the overlay only.
function InvitePopover() {
  const friends = useFriends();
  const overlayOk = useOverlayAvailable();
  const [query, setQuery] = React.useState("");
  React.useEffect(function () {
    send("refreshFriends");
  }, []);
  const close = function () { setStore({ invitesOpen: false }); };
  const q = query.trim().toLowerCase();
  const all = list(friends)
    .filter(function (f) { return f && f.id !== undefined; })
    .filter(function (f) { return q.length === 0 || String(f.name || "").toLowerCase().indexOf(q) >= 0; })
    .sort(function (a, b) {
      const sa = FRIEND_ORDER[a.state] !== undefined ? FRIEND_ORDER[a.state] : 3;
      const sb = FRIEND_ORDER[b.state] !== undefined ? FRIEND_ORDER[b.state] : 3;
      if (sa !== sb) return sa - sb;
      return String(a.name || "").localeCompare(String(b.name || ""));
    });
  const online = all.filter(function (f) { return f.state !== "offline" && FRIEND_ORDER[f.state] !== undefined; });
  const offline = all.filter(function (f) { return !(f.state !== "offline" && FRIEND_ORDER[f.state] !== undefined); });
  const overlay = function () { send("openInviteOverlay"); };
  const group = function (title, items) {
    if (items.length === 0) return null;
    return h("div", { key: title }, h(Label, { style: { padding: "8rem 8rem 2rem 8rem", marginBottom: 0 } }, title), items.map(function (f) { return h(FriendRow, { key: f.id, friend: f }); }));
  };
  return h(
    "div",
    { style: { position: "absolute", left: "100%", top: "0rem", marginLeft: "8rem", width: "320rem", pointerEvents: "auto" } },
    h(
      "div",
      { style: merge(S.col, { borderRadius: T.radius, boxShadow: "0 4rem 20rem rgba(0, 0, 0, 0.3)" }) },
      h(
        "div",
        { style: merge(S.row, { minHeight: "40rem", padding: "6rem 6rem 6rem 12rem", backgroundColor: T.panelDark, backdropFilter: T.blur, color: T.titleBlue, borderTopLeftRadius: T.radius, borderTopRightRadius: T.radius }) },
        h("div", { style: merge(S.ellipsis, { flex: 1, textTransform: "uppercase" }) }, friends ? t("st.ui.friends.title") : t("st.ui.invite.title")),
        friends ? h(IconBtn, { src: G.refresh, size: 13, tooltip: t("st.ui.friends.refresh"), onClick: function () { send("refreshFriends"); } }) : null,
        h(IconBtn, { src: G.close, size: 12, tooltip: t("st.ui.close"), onClick: close })
      ),
      h(
        "div",
        { style: { padding: "8rem", backgroundColor: "rgba(42, 55, 83, 0.6)", backgroundImage: T.panelGradient, backdropFilter: T.blur, color: T.text, borderBottomLeftRadius: T.radius, borderBottomRightRadius: T.radius } },
        friends
          ? h(
              "div",
              null,
              h(Field, { value: query, onChange: setQuery, placeholder: t("st.ui.friends.search"), title: t("st.ui.friends.search"), max: 64 }),
              h(
                Scroll,
                { maxHeight: "320rem", style: { marginTop: "6rem" } },
                friends.length === 0
                  ? h(Hint, { style: { padding: "10rem 8rem" } }, t("st.ui.friends.empty"))
                  : all.length === 0
                    ? h(Hint, { style: { padding: "10rem 8rem" } }, t("st.ui.friends.none"))
                    : [group(t("st.ui.friends.groupOnline", { N: online.length }), online), group(t("st.ui.friends.groupOffline", { N: offline.length }), offline)]
              )
            )
          : h(Hint, { style: { padding: "2rem 4rem 8rem 4rem" } }, overlayOk ? t("st.ui.friends.overlayOnly") : t("st.ui.friends.noOverlay")),
        overlayOk ? h("div", { style: merge(S.row, { marginTop: "8rem" }) }, h(Btn, { kind: "flat", style: { flex: 1 }, onClick: overlay }, t("st.ui.friends.overlay"))) : null
      )
    )
  );
}

function LiveSettings(p) {
  const s = p.state;
  const [password, setPassword] = React.useState("");
  return h(
    Foldout,
    { title: t("st.ui.settings.title") },
    h(
      GameField,
      { label: t("st.ui.settings.livePassword") },
      h(
        "div",
        { style: S.row },
        h(Field, { value: password, onChange: setPassword, password: true, placeholder: s.hasPassword ? t("st.ui.host.passwordKept") : t("st.ui.host.passwordPlaceholder"), title: t("st.ui.settings.livePassword"), style: { flex: 1, width: "auto", marginRight: "8rem" } }),
        h(Btn, { kind: "flat", disabled: password.length === 0, onClick: function () { send("saveHostSettings", s.playerName || "", s.sessionNameSetting || "", password); setPassword(""); } }, t("st.ui.settings.apply"))
      ),
      s.hasPassword ? h("div", { style: { marginTop: "6rem", fontSize: FS.s } }, h(LinkText, { onClick: function () { send("saveHostSettings", s.playerName || "", s.sessionNameSetting || "", ""); } }, t("st.ui.host.removePassword"))) : null
    ),
    h(HostOptions, { state: s, live: true })
  );
}

function NetLine() {
  const n = useJson(netBinding);
  return h(
    "div",
    { style: { padding: "2rem 6rem" } },
    n.show && n.text ? h("div", { style: { color: T.textDim, fontSize: FS.xs, marginBottom: "6rem", lineHeight: 1.35 } }, n.text) : null,
    h(
      "div",
      { style: merge(S.row, { fontSize: FS.xs }) },
      h(LinkText, { onClick: function () { send("toggleNetHud"); } }, n.show ? t("st.ui.net.hide") : t("st.ui.net.show")),
      h("div", { style: { width: "14rem" } }),
      h(LinkText, { onClick: function () { send("writeDiagnostics"); } }, t("st.ui.net.diagnostics"))
    )
  );
}

// robust branch: the startup self-test in one line (details go to the diagnostics file)
function SelfTest(p) {
  const s = p.state;
  if (!s.probes) return null;
  const off = s.probes.indexOf("off:") >= 0;
  return h(
    "div",
    { style: merge({ fontSize: FS.xs, lineHeight: 1.35, color: off ? T.warn : p.menu ? T.menuText2 : T.textDimmer }, p.style) },
    s.probes + " ",
    h(LinkText, { onClick: function () { send("writeDiagnostics"); } }, t("st.ui.selftest.details"))
  );
}

function Diagnostics(p) {
  const s = p.state;
  return h(
    Foldout,
    { title: t("st.ui.diag.title") },
    s.sync ? h("div", { style: { color: T.textDimmer, fontSize: FS.xs, lineHeight: 1.35, padding: "2rem 6rem 6rem 6rem" } }, s.sync) : null,
    h(NetLine, null),
    h(SelfTest, { state: s, style: { padding: "6rem 6rem 2rem 6rem" } })
  );
}

// robust branch: a client session ended unexpectedly in the host's city: automatic rejoin, or the player's choice
function RecoveryView(p) {
  const s = p.state;
  const r = s.recovery || {};
  let title;
  let text;
  let buttons = [];
  const full = { width: "100%", marginTop: "6rem" };
  if (r.phase === "Waiting") {
    title = t("st.ui.recovery.lost.title");
    text = t("st.ui.recovery.lost.text", { S: r.seconds || 0, N: (r.attempt || 0) + 1, MAX: r.max || 0 });
    buttons = [
      h(Btn, { key: "now", kind: "primary", style: full, onClick: function () { send("reconnect"); } }, t("st.ui.recovery.reconnectNow")),
      h(Btn, { key: "menu", style: full, onClick: function () { send("mainMenu"); } }, t("st.ui.recovery.mainMenu")),
      h(Btn, { key: "stay", style: full, onClick: function () { send("stayOffline"); } }, t("st.ui.recovery.stayOffline")),
    ];
  } else if (r.phase === "Attempting") {
    title = t("st.ui.recovery.attempt.title", { N: r.attempt || 1, MAX: r.max || 0 });
    text = STAGES.indexOf(r.attemptStage) >= 0 ? t("st.ui.join.stage." + r.attemptStage) : "";
    buttons = [h(Btn, { key: "cancel", style: full, onClick: function () { send("cancelReconnect"); } }, t("st.ui.recovery.cancel"))];
  } else {
    // w8/security: removed (or banned) after loading the city says so; it used to read "the session has ended"
    const why = r.banned ? "banned" : r.kicked ? "kicked" : r.hostEnded ? "ended" : "failed";
    title = t("st.ui.recovery." + why + ".title");
    text = t("st.ui.recovery." + why + ".text");
    if (r.canReconnect) buttons.push(h(Btn, { key: "again", kind: "primary", style: full, onClick: function () { send("reconnect"); } }, t("st.ui.recovery.reconnect")));
    buttons.push(h(Btn, { key: "menu", kind: r.canReconnect ? "flat" : "primary", style: full, onClick: function () { send("mainMenu"); } }, t("st.ui.recovery.mainMenu")));
    buttons.push(h(Btn, { key: "stay", style: full, onClick: function () { send("stayOffline"); } }, t("st.ui.recovery.stayOffline")));
  }
  return h(
    "div",
    null,
    h(
      Section,
      { first: true },
      h("div", { style: merge(S.row, { padding: "4rem 6rem 6rem 6rem" }) }, h(Dot, { color: r.phase === "NeedsChoice" ? T.bad : T.warn, size: 10 }), h("div", { style: { flex: 1, fontSize: FS.m, color: T.text } }, title)),
      r.reason ? h(Hint, { style: { padding: "0 6rem 6rem 6rem" } }, r.reason) : null,
      text ? h(Notice, { color: r.phase === "NeedsChoice" ? T.bad : T.warn }, text) : null,
      r.phase === "Attempting" ? h(Progress, { value: s.progress, style: { margin: "6rem" } }) : null,
      h("div", { style: { padding: "0 6rem 6rem 6rem" } }, buttons)
    ),
    list(s.players).length > 0 ? h(Players, { state: s }) : null,
    s.probes ? h(Section, null, h(SelfTest, { state: s, style: { padding: "2rem 6rem" } })) : null
  );
}

// w8/security: the integrity check found more divergence than object repairs fix: offer a rejoin with a fresh snapshot
function ResyncOffer(p) {
  const s = p.state;
  if (!s.resync) return null;
  return h(
    Section,
    null,
    h(
      Notice,
      { color: T.warn },
      h("div", { style: { fontWeight: "bold", marginBottom: "3rem" } }, t("st.ui.resync.title")),
      t("st.ui.resync.text"),
      h("div", { style: { marginTop: "4rem", color: T.textDimmer, fontSize: FS.xs } }, s.resync)
    ),
    h(
      "div",
      { style: merge(S.row, { padding: "2rem 6rem 6rem 6rem" }) },
      h(Btn, { kind: "primary", style: { flex: 1, marginRight: "6rem" }, onClick: function () { send("resync"); } }, t("st.ui.resync.now")),
      h(Btn, { kind: "flat", style: { flex: 1 }, onClick: function () { send("dismissResync"); } }, t("st.ui.resync.later"))
    )
  );
}

function SessionView(p) {
  const s = p.state;
  const hosting = s.mode === "Hosting";
  return h(
    "div",
    null,
    h(SessionHeader, { state: s }),
    !hosting ? h(ResyncOffer, { state: s }) : null,
    hosting ? h(RecordControls, null) : h(ReplayBanner, null),
    h(Players, { state: s }),
    hosting ? h(LiveSettings, { state: s }) : null,
    h(Diagnostics, { state: s })
  );
}

// w8/security: the build id, exactly as the installer prints it (players compare it when a join is refused)
function BuildLine(p) {
  const s = p.state;
  if (!s.build) return null;
  return h("div", { style: merge({ color: p.menu ? T.menuText2 : T.textFaint, fontSize: FS.xs, marginTop: p.menu ? "6rem" : "6rem", textAlign: p.menu ? "left" : "center" }, p.style) }, t("st.ui.footer.build", { BUILD: s.build }));
}

function LeaveButton(p) {
  const confirm = useConfirm();
  const hosting = p.state.mode === "Hosting";
  return h(
    Btn,
    { kind: "danger", style: { width: "100%" }, onClick: function () { confirm.run(function () { send("leave"); }); } },
    confirm.armed ? t("st.ui.leave.confirm") : hosting ? t("st.ui.leave.host") : t("st.ui.leave.client")
  );
}

// ------------------------------------------------------------------------------- solo testing (record/replay)

const SPEEDS = [1, 2, 4, 0];

function verdictColor(status) {
  return status === "passed" ? T.good : status === "failed" ? T.bad : T.warn;
}

function VerdictCard(p) {
  const v = p.verdict;
  if (!v || !v.status) return null;
  const kind = v.status === "passed" || v.status === "failed" ? v.status : "incomplete";
  const color = verdictColor(v.status);
  const textColor = p.menu ? T.menuText : T.text;
  return h(
    "div",
    { style: merge({ padding: "10rem 12rem", margin: p.menu ? "0 0 12rem 0" : "4rem 6rem", borderRadius: T.radiusInner, backgroundColor: "rgba(0, 0, 0, 0.22)" }, border("3rem", color, ["Left"])) },
    h("div", { style: merge(S.row, { marginBottom: "4rem" }) }, h(Glyph, { src: kind === "passed" ? G.check : G.warning, size: 14, color: color, style: { marginRight: "8rem" } }), h("div", { style: { flex: 1, fontSize: p.menu ? FS.l : FS.m, color: textColor, fontWeight: "bold" } }, t("st.ui.test.verdict." + kind))),
    h("div", { style: { color: p.menu ? T.menuText2 : T.textDim, fontSize: FS.s, lineHeight: 1.35 } }, v.summary || ""),
    v.folder
      ? h(
          "div",
          { style: { marginTop: "6rem", fontSize: FS.xs, color: p.menu ? T.menuText2 : T.textDimmer, lineHeight: 1.35 } },
          t("st.ui.test.verdictFile", { FOLDER: v.folder }) + " ",
          h(LinkText, { onClick: function () { send("copyText", v.folder); } }, t("st.ui.test.copyPath"))
        )
      : null
  );
}

function RecordingRow(p) {
  const r = p.rec;
  const [hover, hv] = useHover();
  const sel = p.selected;
  const menu = p.menu;
  const bg = sel ? (menu ? T.menuSelected : T.selected) : hover ? (menu ? T.menuHover : T.hover) : "rgba(0, 0, 0, 0)";
  const fg = sel && menu ? T.menuInverted : menu ? T.menuText : T.text;
  return h(
    "div",
    merge(hv, { style: merge(S.row, { padding: menu ? "8rem 12rem" : "6rem 8rem", borderRadius: "4rem", cursor: "pointer", backgroundColor: bg, color: fg }), onClick: function () { p.onSelect(r.id); } }),
    h(Dot, { color: r.verdict ? verdictColor(r.verdict) : sel && menu ? T.menuInverted : T.textFaint, size: 8, gap: 10 }),
    h(
      "div",
      { style: { flex: 1, minWidth: 0 } },
      h("div", { style: merge(S.ellipsis, { fontSize: menu ? FS.m : FS.s }) }, r.city || r.id || ""),
      h("div", { style: { fontSize: FS.xs, opacity: 0.75 } }, (r.when || "") + (r.duration ? " · " + r.duration : "") + " · " + (r.mb || "0") + " MB")
    )
  );
}

function SpeedPicker(p) {
  return h(
    "div",
    { style: S.row },
    h("div", { style: { marginRight: "10rem", color: p.menu ? T.menuText : T.textDim, fontSize: p.menu ? FS.l : FS.s } }, t("st.ui.test.speed")),
    SPEEDS.map(function (v, i) {
      const sel = v === p.value;
      const [first, last] = [i === 0, i === SPEEDS.length - 1];
      return h(Segment, { key: String(v), selected: sel, first: first, last: last, menu: p.menu, onClick: function () { p.onChange(v); } }, v === 0 ? t("st.ui.test.speedFast") : v + "×");
    })
  );
}

// one option of a segmented control (the options-page section tabs)
function Segment(p) {
  const [hover, hv] = useHover();
  const tabCls = vc("optionHeader", "tab");
  if (tabCls) {
    return h("div", merge(hv, { className: cx(tabCls, p.selected && "selected", p.first && vc("optionHeader", "first"), p.last && vc("optionHeader", "last")), style: { cursor: "pointer", fontSize: p.menu ? undefined : FS.s }, onClick: p.onClick }), p.children);
  }
  const edge = p.first ? { borderTopLeftRadius: "4rem", borderBottomLeftRadius: "4rem" } : p.last ? { borderTopRightRadius: "4rem", borderBottomRightRadius: "4rem" } : null;
  return h(
    "div",
    merge(hv, {
      style: merge(
        { padding: "4rem 12rem", textTransform: "uppercase", fontWeight: "bold", fontSize: p.menu ? FS.m : FS.s, cursor: "pointer", color: p.selected ? "#ffffff" : "rgba(255, 255, 255, 0.5)", backgroundColor: p.selected ? "rgba(255, 255, 255, 0.3)" : hover ? "rgba(255, 255, 255, 0.1)" : "rgba(0, 0, 0, 0)" },
        border("2rem", "rgba(255, 255, 255, 0.5)"),
        p.first ? null : { borderLeftWidth: "0rem" },
        edge
      ),
      onClick: p.onClick,
    }),
    p.children
  );
}

function useRecordings() {
  const r = useJson(replayBinding);
  const [selected, setSelected] = React.useState("");
  const [speed, setSpeed] = React.useState(1);
  const recs = list(r.recordings);
  let chosen = recs.length > 0 ? recs[0].id : "";
  for (const x of recs) if (x.id === selected) chosen = selected;
  return { r: r, recs: recs, chosen: chosen, setSelected: setSelected, speed: speed, setSpeed: setSpeed };
}

// in-game lobby: record a test session, pick a recording and replay it, the last verdict
function SoloTestSection(p) {
  const m = useRecordings();
  const r = m.r;
  return h(
    "div",
    null,
    h(Section, { first: true }, h(Hint, { style: { padding: "4rem 6rem 6rem 6rem" } }, t("st.ui.test.intro")), h(VerdictCard, { verdict: r.verdict })),
    h(
      Section,
      null,
      h("div", { style: { padding: "4rem 6rem" } }, h(Btn, { kind: "primary", disabled: !p.inGame || !r.canRecord, onClick: function () { send("recordStart"); }, style: { width: "100%" } }, t("st.ui.test.record"))),
      h(Hint, { style: { padding: "2rem 6rem 6rem 6rem", fontSize: FS.xs } }, p.inGame ? t("st.ui.test.recordHint") : t("st.ui.test.needCity"))
    ),
    h(
      Section,
      { title: t("st.ui.test.recordings"), right: h(IconBtn, { src: G.refresh, size: 13, tooltip: t("st.ui.test.refresh"), onClick: function () { send("replayRefresh"); } }) },
      m.recs.length === 0
        ? h(Hint, { style: { padding: "2rem 6rem 8rem 6rem" } }, t("st.ui.test.none"))
        : h(Scroll, { maxHeight: "200rem" }, m.recs.map(function (x) { return h(RecordingRow, { key: x.id, rec: x, selected: x.id === m.chosen, onSelect: m.setSelected }); })),
      m.recs.length > 0 ? h("div", { style: { padding: "8rem 6rem 4rem 6rem" } }, h(SpeedPicker, { value: m.speed, onChange: m.setSpeed })) : null,
      m.recs.length > 0 ? h("div", { style: { padding: "4rem 6rem" } }, h(Btn, { kind: "primary", disabled: !r.canReplay || !m.chosen, onClick: function () { send("replayStart", m.chosen, m.speed); }, style: { width: "100%" } }, t("st.ui.test.replay"))) : null,
      m.recs.length > 0 && p.inGame ? h(Hint, { style: { padding: "2rem 6rem 4rem 6rem", fontSize: FS.xs } }, t("st.ui.test.replayHint")) : null,
      r.root ? h("div", { style: { padding: "4rem 6rem 6rem 6rem", fontSize: FS.xs, color: T.textDimmer, lineHeight: 1.35 } }, t("st.ui.test.folder", { FOLDER: r.root })) : null
    )
  );
}

// while hosting: start or stop the test recorder
function RecordControls() {
  const r = useJson(replayBinding);
  if (r.recording) {
    return h(
      Section,
      null,
      h(
        "div",
        { style: merge(S.row, { padding: "4rem 6rem 8rem 6rem" }) },
        h(Dot, { color: T.bad, size: 10 }),
        h("div", { style: { flex: 1, color: T.text, fontSize: FS.s } }, r.recordStatus || "")
      ),
      h("div", { style: { padding: "0 6rem 4rem 6rem" } }, h(Btn, { kind: "flat", disabled: !!r.stopping, onClick: function () { send("recordStop"); }, style: { width: "100%" } }, t("st.ui.test.stop")))
    );
  }
  if (!r.canRecord) return null;
  return h(
    Foldout,
    { title: t("st.ui.test.title") },
    h(Hint, { style: { padding: "2rem 6rem 8rem 6rem", fontSize: FS.xs } }, t("st.ui.test.recordHint")),
    h("div", { style: { padding: "0 6rem 4rem 6rem" } }, h(Btn, { kind: "flat", onClick: function () { send("recordStart"); }, style: { width: "100%" } }, t("st.ui.test.record")))
  );
}

// while replaying: what is happening, how far, and the way out
function ReplayBanner() {
  const r = useJson(replayBinding);
  if (!r.replaying) return null;
  return h(
    Section,
    null,
    h(Notice, { icon: G.info, color: T.accent }, t("st.ui.test.replayBanner")),
    r.replayStatus ? h(Hint, { style: { padding: "4rem 6rem" } }, r.replayStatus) : null,
    h(Progress, { value: r.progress, style: { margin: "6rem" } }),
    h("div", { style: { padding: "0 6rem 4rem 6rem" } }, h(Btn, { kind: "flat", onClick: function () { send("replayStop"); }, style: { width: "100%" } }, t("st.ui.test.stopReplay")))
  );
}

// ------------------------------------------------------------------------------------------ in-game lobby

function SteamMissing(p) {
  return h(Notice, { color: T.bad, style: p && p.style }, h("div", { style: { fontWeight: "bold", marginBottom: "3rem" } }, t("st.ui.steam.missing.title")), t("st.ui.steam.missing.text"));
}

// host options shared by the lobby, the live session settings and the main menu (saved on every change)
function useHostOptions(s) {
  const [maxPlayers, setMaxPlayers] = React.useState(s.maxPlayers || 8);
  const [allowDirect, setAllowDirect] = React.useState(!!s.allowDirect);
  const [requireMods, setRequireMods] = React.useState(s.requireSameMods !== false);
  const [port, setPort] = React.useState(String(s.portSetting || 27015));
  const save = function (next) {
    const v = merge({ maxPlayers: maxPlayers, allowDirect: allowDirect, requireMods: requireMods, port: port }, next);
    const portNumber = parseInt(v.port, 10);
    send("saveHostOptions", v.maxPlayers, !!v.allowDirect, !!v.requireMods, portNumber >= 1024 && portNumber <= 65535 ? portNumber : 0);
  };
  return {
    maxPlayers: maxPlayers, allowDirect: allowDirect, requireMods: requireMods, port: port, setPort: setPort, save: save,
    setMax: function (v) { setMaxPlayers(v); save({ maxPlayers: v }); },
    setDirect: function (v) { setAllowDirect(v); save({ allowDirect: v }); },
    setMods: function (v) { setRequireMods(v); save({ requireMods: v }); },
  };
}

// w8/security: the host's ban list with an unban button per entry (stored with the host's settings)
function BanList(p) {
  const bans = list(p.state.bans);
  if (bans.length === 0) return null;
  const menu = !!p.menu;
  return h(
    "div",
    { style: { padding: menu ? "4rem 16rem 8rem 16rem" : "4rem 6rem" } },
    h(menu ? "div" : Label, { style: menu ? { fontSize: FS.l, color: T.menuText, marginBottom: "4rem" } : null }, t("st.ui.host.bans")),
    h(menu ? "div" : Hint, { style: menu ? { fontSize: FS.m, color: T.menuText2, marginBottom: "6rem" } : { fontSize: FS.xs, marginBottom: "4rem" } }, t("st.ui.host.bansHint")),
    bans.map(function (b) {
      return h(
        "div",
        { key: b.key, style: merge(S.row, { padding: "4rem 0", minHeight: "32rem" }, border("1rem", menu ? T.menuBorder : T.divider, ["Top"])) },
        h(
          "div",
          { style: { flex: 1, minWidth: 0 } },
          h("div", { style: merge(S.ellipsis, { color: menu ? T.menuText : T.text, fontSize: menu ? FS.m : FS.s }) }, b.name || b.id),
          h("div", { style: merge(S.ellipsis, { color: menu ? T.menuText2 : T.textDimmer, fontSize: FS.xs }) }, b.kind === "ip" ? t("st.ui.host.banAddress", { ADDRESS: b.id }) : b.id)
        ),
        h(Btn, { kind: menu ? "menuSecondary" : "flat", style: { marginLeft: "8rem" }, onClick: function () { send("unban", b.key); } }, t("st.ui.host.unban"))
      );
    })
  );
}

function HostOptions(p) {
  const s = p.state;
  const o = useHostOptions(s);
  const [friendsOnly, setFriendsOnly] = React.useState(!!s.friendsOnly);
  // w8/security: direct connections need a password (Steam does not check those players)
  const directOk = !!s.hasPassword;
  return h(
    "div",
    null,
    h(GameRow, { label: t("st.ui.host.maxPlayers"), hint: t("st.ui.host.maxPlayersHint") }, h(Stepper, { value: o.maxPlayers, min: 2, max: 16, onChange: o.setMax })),
    h(GameRow, { label: t("st.ui.host.requireMods"), hint: t("st.ui.host.requireModsHint") }, h(Check, { checked: o.requireMods, onChange: o.setMods })),
    h(GameRow, { label: t("st.ui.host.friendsOnly"), hint: t("st.ui.host.friendsOnlyHint") }, h(Check, { checked: friendsOnly, onChange: function (v) { setFriendsOnly(v); send("setFriendsOnly", !!v); } })),
    h(
      GameRow,
      { label: t("st.ui.host.allowDirect"), hint: directOk ? t("st.ui.host.allowDirectHint") + (p.live ? " " + t("st.ui.host.allowDirectNextTime") : "") : t("st.ui.host.allowDirectNeedsPassword") },
      h(Check, { checked: o.allowDirect && directOk, disabled: !directOk, onChange: o.setDirect })
    ),
    o.allowDirect && directOk
      ? h(GameRow, { label: t("st.ui.host.port") }, h(Field, { value: o.port, onChange: o.setPort, onBlur: function () { o.save({}); }, ids: true, max: 5, style: { width: "100rem", textAlign: "center" }, title: t("st.ui.host.port") }))
      : null,
    h(BanList, { state: s })
  );
}

function GameHostTab(p) {
  const s = p.state;
  const [session, setSession] = React.useState(s.sessionNameSetting || "");
  const [password, setPassword] = React.useState("");
  // review fix: an untouched (empty) password field keeps the stored host password instead of wiping it
  const save = function (pw) {
    send("saveHostSettings", p.name, session, pw !== undefined ? pw : password.length > 0 ? password : s.keepPassword || KEEP_PASSWORD);
  };
  const reason = !s.canHost ? t("st.ui.host.unavailable") : "";
  return h(
    "div",
    null,
    h(
      Section,
      { first: true },
      h(Hint, { style: { padding: "4rem 6rem 4rem 6rem" } }, t("st.ui.host.intro")),
      p.nameField,
      h(GameField, { label: t("st.ui.host.sessionName") }, h(Field, { value: session, onChange: setSession, onBlur: function () { save(); }, placeholder: t("st.ui.host.sessionNamePlaceholder", { NAME: p.name || s.playerName || "…" }), title: t("st.ui.host.sessionName"), max: 64 })),
      h(
        GameField,
        { label: t("st.ui.host.password") },
        h(Field, { value: password, onChange: setPassword, onBlur: function () { save(); }, password: true, placeholder: s.hasPassword ? t("st.ui.host.passwordKept") : t("st.ui.host.passwordPlaceholder"), title: t("st.ui.host.password") }),
        s.hasPassword ? h("div", { style: { marginTop: "6rem", fontSize: FS.s } }, h(LinkText, { onClick: function () { setPassword(""); save(""); } }, t("st.ui.host.removePassword"))) : null
      )
    ),
    h(Section, { title: t("st.ui.host.settingsTitle") }, h(HostOptions, { state: s, live: false })),
    h(
      Section,
      null,
      reason ? h(Hint, { style: { padding: "2rem 6rem 6rem 6rem" } }, reason) : null,
      h("div", { style: { padding: "4rem 6rem" } }, h(Btn, { kind: "primary", disabled: !s.canHost, onClick: function () { save(); send("host"); }, style: { width: "100%" } }, t("st.ui.host.start")))
    )
  );
}

function GameJoinTab(p) {
  const s = p.state;
  const [target, setTarget] = React.useState(s.joinTarget || "");
  const [password, setPassword] = React.useState("");
  const canJoin = !!s.canJoin && target.trim().length > 0;
  const join = function () {
    if (!canJoin) return;
    send("saveHostSettings", p.name, s.sessionNameSetting || "", s.keepPassword || KEEP_PASSWORD);
    send("join", target.trim(), password);
  };
  return h(
    "div",
    null,
    h(
      Section,
      { first: true },
      h(Hint, { style: { padding: "4rem 6rem 4rem 6rem" } }, t("st.ui.join.intro")),
      p.nameField,
      h(GameField, { label: t("st.ui.join.target") }, h(Field, { value: target, onChange: setTarget, onEnter: join, ids: true, placeholder: t("st.ui.join.targetPlaceholder"), title: t("st.ui.join.target"), max: 96 })),
      h(GameField, { label: t("st.ui.join.password") }, h(Field, { value: password, onChange: setPassword, onEnter: join, password: true, placeholder: t("st.ui.join.passwordPlaceholder"), title: t("st.ui.join.password") }))
    ),
    h(
      Section,
      null,
      h(Notice, null, t("st.ui.join.replaceWarning")),
      h("div", { style: { padding: "4rem 6rem" } }, h(Btn, { kind: "primary", disabled: !canJoin, onClick: join, style: { width: "100%" } }, t("st.ui.join.start"))),
      h(Hint, { style: { padding: "4rem 6rem 4rem 6rem", fontSize: FS.xs } }, t("st.ui.join.steamHint"))
    )
  );
}

// The tab bar of the in-game lobby (vanilla tabs: rounded tops, highlight gradient on the selected one).
function Tabs(p) {
  const bar = vc("tabs", "tabBar");
  return h(
    "div",
    { className: bar || undefined, style: merge({ paddingTop: "8rem" }, bar ? { "--minTabWidth": "0rem" } : merge(S.row, { justifyContent: "center", padding: "8rem 10rem 0 10rem" }), border("2rem", T.divider, ["Bottom"])) },
    p.tabs.map(function (tab, i) {
      return h(Btn, { key: tab.id, kind: "tab", selected: p.value === tab.id, style: merge({ flex: 1, minWidth: 0 }, i > 0 && !bar ? { marginLeft: "4rem" } : null), onClick: function () { p.onChange(tab.id); } }, tab.label);
    })
  );
}

function GameLobby(p) {
  const s = p.state;
  const st = useStore();
  const [name, setName] = React.useState(s.playerName || "");
  // its own page memory: in a city hosting comes first, in the main menu joining does
  const tab = st.gameTab === "join" || st.gameTab === "solo" ? st.gameTab : "host";
  const saveName = function () {
    send("saveHostSettings", name, s.sessionNameSetting || "", s.keepPassword || KEEP_PASSWORD);
  };
  const nameField = h(GameField, { label: t("st.ui.profile.name") }, h(Field, { value: name, onChange: setName, onBlur: saveName, placeholder: t("st.ui.profile.namePlaceholder"), title: t("st.ui.profile.name"), max: 64 }));
  return h(
    "div",
    null,
    h(Tabs, { value: tab, onChange: function (id) { setStore({ gameTab: id }); }, tabs: [{ id: "host", label: t("st.ui.tab.host") }, { id: "join", label: t("st.ui.tab.join") }, { id: "solo", label: t("st.ui.tab.solo") }] }),
    s.steam === false ? h(SteamMissing, { style: { margin: "8rem" } }) : null,
    s.blocked ? h(Notice, { color: T.bad, style: { margin: "8rem" } }, t("st.ui.blocked", { REASON: s.blocked })) : null,
    tab === "host" ? h(GameHostTab, { state: s, name: name, nameField: nameField }) : tab === "join" ? h(GameJoinTab, { state: s, name: name, nameField: nameField }) : h(SoloTestSection, { inGame: true }),
    s.probes ? h(Section, null, h(SelfTest, { state: s, style: { padding: "2rem 6rem" } })) : null
  );
}

function PanelContent(p) {
  const s = p.state;
  const mode = s.mode || "None";
  if (mode === "Hosting" || mode === "Client") return h(SessionView, { state: s });
  if (mode === "Recovering") return h(RecoveryView, { state: s });
  if (mode === "Joining") {
    return h(
      "div",
      null,
      h(ReplayBanner, null),
      h(
        Section,
        { first: true },
        h(Hint, { style: { padding: "4rem 6rem 8rem 6rem" } }, s.status || t("st.ui.join.overlayTitleUnknown")),
        h(Progress, { value: s.progress, style: { margin: "0 6rem 10rem 6rem" } }),
        h("div", { style: { padding: "0 6rem 4rem 6rem" } }, h(Btn, { kind: "flat", onClick: function () { send("leave"); }, style: { width: "100%" } }, t("st.ui.join.cancel")))
      )
    );
  }
  return h(GameLobby, { state: s });
}

function panelTitle(s) {
  const mode = s.mode || "None";
  return (mode === "Hosting" || mode === "Client") && s.sessionName ? s.sessionName : t("st.ui.title");
}

// ----------------------------------------------------------------------------------- main menu: multiplayer

// A row of the vanilla options page: label left (large), control right; hovering it shows its text in the info column.
function MenuRow(p) {
  const [hover, hv] = useHover();
  const field = vc("rowField", "field");
  const label = vc("rowField", "label");
  const enter = function () {
    if (!field) hv.onMouseEnter();
    if (p.onInfo) p.onInfo(p.info ? { title: p.label, text: p.info } : null);
  };
  const leave = function () {
    if (!field) hv.onMouseLeave();
    if (p.onInfo) p.onInfo(null);
  };
  return h(
    "div",
    {
      className: field || undefined,
      style: field
        ? merge({ cursor: p.onClick ? "pointer" : "default" }, p.style)
        : merge(S.row, { padding: "8rem 16rem", minHeight: "64rem", borderRadius: "4rem", color: T.menuText, fontSize: FS.m, backgroundColor: hover ? T.menuHover : "rgba(0, 0, 0, 0)", cursor: p.onClick ? "pointer" : "default" }, p.style),
      onMouseEnter: enter,
      onMouseLeave: leave,
      onClick: p.onClick,
    },
    h("div", { className: label || undefined, style: label ? null : { flex: 1, marginRight: "16rem", fontSize: FS.l } }, p.label),
    p.children
  );
}

// a group title inside an options page (vanilla label widget: uppercase, large)
function MenuGroup(p) {
  return h(
    "div",
    { style: merge(S.row, { padding: "16rem 16rem 4rem 16rem", textTransform: "uppercase", fontSize: FS.l, color: T.menuTitle, fontWeight: "bold" }, p.style) },
    h("div", { style: { flex: 1 } }, p.children),
    p.right || null
  );
}

function MenuText(p) {
  return h("div", { style: merge({ padding: "8rem 16rem", color: T.menuText, fontSize: FS.l, lineHeight: 1.4 }, p.style) }, p.children);
}

function MenuNotice(p) {
  const color = p.color || T.warn;
  return h(
    "div",
    { style: merge(S.row, { alignItems: "flex-start", margin: "8rem 16rem", padding: "12rem 14rem", borderRadius: "4rem", backgroundColor: "rgba(0, 0, 0, 0.25)" }, border("3rem", color, ["Left"]), p.style) },
    h(Glyph, { src: p.icon || G.warning, size: 18, color: color, style: { marginRight: "12rem", marginTop: "2rem" } }),
    h("div", { style: { flex: 1, color: T.menuText, fontSize: FS.m, lineHeight: 1.4 } }, p.children)
  );
}

// The detail panel of a page: header (title, subtitle), main column + info column, footer with the actions.
function MenuPage(p) {
  const header = vc("optionHeader", "optionPageHeader");
  const page = vc("optionPage", "optionPage");
  const main = vc("optionPage", "mainColumn");
  const scroll = vc("optionPage", "scrollable");
  const info = vc("optionPage", "infoColumn");
  const infoTitle = vc("optionPage", "infoTitle");
  const infoText = vc("optionPage", "infoDescription");
  return h(
    "div",
    { style: merge(S.col, { flex: 1, height: "100%", minHeight: 0 }) },
    h(
      "div",
      { className: header || undefined, style: header ? null : merge(S.row, { margin: "0 24rem", padding: "24rem 0 16rem 0", justifyContent: "space-between" }, border("2rem", T.menuBorder, ["Bottom"])) },
      h(
        "div",
        { style: { minWidth: 0 } },
        h("div", { style: { fontSize: FS.xl, fontWeight: "bold", textTransform: "uppercase", color: T.menuTitle } }, p.title),
        p.subtitle ? h("div", { style: { fontSize: FS.m, color: T.menuText2, marginTop: "4rem" } }, p.subtitle) : null
      ),
      p.headerRight || null
    ),
    h(
      "div",
      { className: page || undefined, style: merge({ flex: 1, minHeight: 0 }, page ? null : S.row, { alignItems: "stretch" }) },
      h(
        "div",
        { className: main || undefined, style: main ? null : merge(S.col, { flex: 1, position: "relative" }) },
        h(Scroll, { flex: true, className: scroll || undefined, style: scroll ? null : { padding: "16rem 24rem" } }, p.children)
      ),
      p.info
        ? h(
            "div",
            { className: info || undefined, style: info ? { width: "38%" } : merge({ overflow: "hidden", margin: "24rem 0", padding: "0 24rem", width: "38%", fontSize: FS.l }, border("1rem", T.menuBorder, ["Left"])) },
            h("div", { className: infoTitle || undefined, style: infoTitle ? null : { color: T.menuTitle, fontWeight: "bold", marginBottom: "10rem", textTransform: "uppercase" } }, p.info.title),
            h("div", { className: infoText || undefined, style: merge({ color: T.menuText }, infoText ? null : { lineHeight: 1.4 }) }, p.info.text),
            p.info.extra || null
          )
        : null
    ),
    p.footer
      ? h("div", { style: merge(S.row, { padding: "16rem 24rem", justifyContent: "flex-end" }, border("1rem", T.menuBorder, ["Top"])) }, p.footer)
      : null
  );
}

function infoBlock(title, text, extra) {
  return { title: title, text: text, extra: extra };
}

function MenuJoinPage(p) {
  const s = p.state;
  const [name, setName] = React.useState(s.playerName || "");
  const [target, setTarget] = React.useState(s.joinTarget || "");
  const [password, setPassword] = React.useState("");
  const [hoverInfo, setHoverInfo] = React.useState(null);
  const canJoin = !!s.canJoin && s.steam !== false && target.trim().length > 0;
  const saveName = function () { send("saveHostSettings", name, s.sessionNameSetting || "", s.keepPassword || KEEP_PASSWORD); };
  const join = function () {
    if (!canJoin) return;
    send("saveHostSettings", name, s.sessionNameSetting || "", s.keepPassword || KEEP_PASSWORD);
    send("join", target.trim(), password);
  };
  const info = hoverInfo || infoBlock(t("st.ui.join.title"), t("st.ui.join.info"), h("div", { style: { marginTop: "16rem", color: T.menuText2, lineHeight: 1.4 } }, t("st.ui.join.steamHint")));
  return h(
    MenuPage,
    {
      title: t("st.ui.join.title"),
      subtitle: t("st.ui.join.subtitle"),
      info: info,
      footer: h(Btn, { kind: "menuPrimary", disabled: !canJoin, onClick: join, style: { minWidth: "220rem" } }, t("st.ui.join.start")),
    },
    s.steam === false ? h(MenuNotice, { color: T.bad }, h("div", { style: { fontWeight: "bold" } }, t("st.ui.steam.missing.title")), t("st.ui.steam.missing.text")) : null,
    h(MenuText, { style: { color: T.menuText2, fontSize: FS.m } }, t("st.ui.join.intro")),
    h(MenuRow, { label: t("st.ui.profile.name"), info: t("st.ui.profile.nameHint"), onInfo: setHoverInfo }, h(Field, { look: "menu", value: name, onChange: setName, onBlur: saveName, placeholder: t("st.ui.profile.namePlaceholder"), title: t("st.ui.profile.name"), max: 64 })),
    h(MenuRow, { label: t("st.ui.join.targetShort"), info: t("st.ui.join.intro") + " " + t("st.ui.join.findId"), onInfo: setHoverInfo }, h(Field, { look: "menu", value: target, onChange: setTarget, onEnter: join, ids: true, placeholder: t("st.ui.join.targetPlaceholder"), title: t("st.ui.join.target"), max: 96 })),
    h(MenuRow, { label: t("st.ui.join.password"), info: t("st.ui.join.passwordHint"), onInfo: setHoverInfo }, h(Field, { look: "menu", value: password, onChange: setPassword, onEnter: join, password: true, placeholder: t("st.ui.join.passwordPlaceholder"), title: t("st.ui.join.password") }))
  );
}

function Step(p) {
  return h(
    "div",
    { style: merge(S.row, { padding: "6rem 0" }) },
    h("div", { style: merge(S.center, { width: "30rem", height: "30rem", borderRadius: "15rem", marginRight: "14rem", flexShrink: 0, color: T.menuText, fontWeight: "bold", fontSize: FS.m }, border("2rem", T.menuBorder2)) }, String(p.n)),
    h("div", { style: { flex: 1, color: T.menuText, fontSize: FS.l, lineHeight: 1.35 } }, p.children)
  );
}

function MenuHostPage(p) {
  const s = p.state;
  const inGame = !!s.inGame;
  const [name, setName] = React.useState(s.playerName || "");
  const [session, setSession] = React.useState(s.sessionNameSetting || "");
  const [password, setPassword] = React.useState("");
  const [hoverInfo, setHoverInfo] = React.useState(null);
  const [friendsOnly, setFriendsOnly] = React.useState(!!s.friendsOnly);
  const directOk = !!s.hasPassword; // w8/security: direct connections need a password
  const o = useHostOptions(s);
  // review fix: an untouched (empty) password field keeps the stored host password instead of wiping it
  const save = function (pw) {
    send("saveHostSettings", name, session, pw !== undefined ? pw : password.length > 0 ? password : s.keepPassword || KEEP_PASSWORD);
  };
  const info = hoverInfo || infoBlock(t("st.ui.host.title"), t("st.ui.host.info"), h("div", { style: { marginTop: "16rem", color: T.menuText2, lineHeight: 1.4 } }, t("st.ui.host.settingsNote")));
  return h(
    MenuPage,
    {
      title: t("st.ui.host.title"),
      subtitle: t("st.ui.host.subtitle"),
      info: info,
      footer: [
        h("div", { key: "why", style: { flex: 1, color: T.menuText2, fontSize: FS.m, marginRight: "16rem" } }, !inGame ? t("st.ui.host.needCity") : !s.canHost ? t("st.ui.host.unavailable") : ""),
        h(Btn, { key: "host", kind: "menuPrimary", disabled: !inGame || !s.canHost, onClick: function () { save(); send("host"); }, style: { minWidth: "220rem" } }, t("st.ui.host.start")),
      ],
    },
    s.steam === false ? h(MenuNotice, { color: T.bad }, h("div", { style: { fontWeight: "bold" } }, t("st.ui.steam.missing.title")), t("st.ui.steam.missing.text")) : null,
    !inGame
      ? h(
          "div",
          { style: { margin: "4rem 16rem 12rem 16rem", padding: "16rem 20rem", borderRadius: "4rem", backgroundColor: "rgba(0, 0, 0, 0.22)" } },
          h("div", { style: merge(S.row, { marginBottom: "8rem" }) }, h(Glyph, { src: G.info, size: 18, color: T.accent, style: { marginRight: "10rem" } }), h("div", { style: { fontSize: FS.l, fontWeight: "bold", color: T.menuTitle, textTransform: "uppercase" } }, t("st.ui.host.menu.title"))),
          h(Step, { n: 1 }, t("st.ui.host.menu.step1")),
          h(Step, { n: 2 }, t("st.ui.host.menu.step2")),
          h(Step, { n: 3 }, t("st.ui.host.menu.step3")),
          h(
            "div",
            { style: merge(S.row, { marginTop: "12rem" }) },
            h(Btn, { kind: "menuSecondary", onClick: function () { openVanillaScreen(2); }, style: { marginRight: "10rem" } }, t("st.ui.host.menu.load")),
            h(Btn, { kind: "menuSecondary", onClick: function () { openVanillaScreen(1); } }, t("st.ui.host.menu.new"))
          )
        )
      : null,
    h(MenuGroup, null, t("st.ui.host.settingsTitle")),
    h(MenuRow, { label: t("st.ui.profile.name"), info: t("st.ui.profile.nameHint"), onInfo: setHoverInfo }, h(Field, { look: "menu", value: name, onChange: setName, onBlur: function () { save(); }, placeholder: t("st.ui.profile.namePlaceholder"), title: t("st.ui.profile.name"), max: 64 })),
    h(MenuRow, { label: t("st.ui.host.sessionName"), info: t("st.ui.host.sessionNameHint"), onInfo: setHoverInfo }, h(Field, { look: "menu", value: session, onChange: setSession, onBlur: function () { save(); }, placeholder: t("st.ui.host.sessionNamePlaceholder", { NAME: name || s.playerName || "…" }), title: t("st.ui.host.sessionName"), max: 64 })),
    h(MenuRow, { label: t("st.ui.host.password"), info: t("st.ui.host.passwordHint"), onInfo: setHoverInfo }, h(Field, { look: "menu", value: password, onChange: setPassword, onBlur: function () { save(); }, password: true, placeholder: s.hasPassword ? t("st.ui.host.passwordKept") : t("st.ui.host.passwordPlaceholder"), title: t("st.ui.host.password") })),
    s.hasPassword ? h("div", { style: { padding: "0 16rem 8rem 16rem", textAlign: "right", fontSize: FS.m } }, h(LinkText, { onClick: function () { setPassword(""); save(""); } }, t("st.ui.host.removePassword"))) : null,
    h(MenuRow, { label: t("st.ui.host.maxPlayers"), info: t("st.ui.host.maxPlayersHint"), onInfo: setHoverInfo }, h(Stepper, { menu: true, value: o.maxPlayers, min: 2, max: 16, onChange: o.setMax })),
    h(MenuRow, { label: t("st.ui.host.requireMods"), info: t("st.ui.host.requireModsHint"), onInfo: setHoverInfo, onClick: function () { o.setMods(!o.requireMods); } }, h(Check, { menu: true, checked: o.requireMods, onChange: o.setMods })),
    h(MenuRow, { label: t("st.ui.host.friendsOnly"), info: t("st.ui.host.friendsOnlyHint"), onInfo: setHoverInfo, onClick: function () { setFriendsOnly(!friendsOnly); send("setFriendsOnly", !friendsOnly); } },
      h(Check, { menu: true, checked: friendsOnly, onChange: function (v) { setFriendsOnly(v); send("setFriendsOnly", !!v); } })),
    h(
      MenuRow,
      { label: t("st.ui.host.allowDirect"), info: directOk ? t("st.ui.host.allowDirectHint") : t("st.ui.host.allowDirectNeedsPassword"), onInfo: setHoverInfo, onClick: directOk ? function () { o.setDirect(!o.allowDirect); } : undefined },
      h(Check, { menu: true, checked: o.allowDirect && directOk, disabled: !directOk, onChange: o.setDirect })
    ),
    !directOk ? h("div", { style: { padding: "0 16rem 8rem 16rem", color: T.menuText2, fontSize: FS.m } }, t("st.ui.host.allowDirectNeedsPassword")) : null,
    o.allowDirect && directOk
      ? h(MenuRow, { label: t("st.ui.host.port"), info: t("st.ui.host.portHint"), onInfo: setHoverInfo }, h(Field, { look: "menu", value: o.port, onChange: o.setPort, onBlur: function () { o.save({}); }, ids: true, max: 5, title: t("st.ui.host.port"), style: { flex: "0 1 140rem", textAlign: "center" } }))
      : null,
    h(BanList, { state: s, menu: true })
  );
}

function MenuSoloPage(p) {
  const s = p.state;
  const m = useRecordings();
  const r = m.r;
  const inGame = !!s.inGame;
  const info = infoBlock(
    t("st.ui.test.title"),
    t("st.ui.test.intro"),
    h(
      "div",
      { style: { marginTop: "16rem", color: T.menuText2, lineHeight: 1.4 } },
      h("div", null, inGame ? t("st.ui.test.replayHint") : t("st.ui.test.needCity")),
      r.root ? h("div", { style: { marginTop: "12rem", fontSize: FS.s } }, t("st.ui.test.folder", { FOLDER: r.root })) : null
    )
  );
  return h(
    MenuPage,
    {
      title: t("st.ui.nav.solo"),
      subtitle: t("st.ui.test.subtitle"),
      info: info,
      headerRight: h(Btn, { kind: "menuSecondary", onClick: function () { send("replayRefresh"); } }, t("st.ui.test.refresh")),
      footer: [
        inGame ? h(Btn, { key: "rec", kind: "menuSecondary", disabled: !r.canRecord, onClick: function () { send("recordStart"); }, style: { marginRight: "12rem" } }, t("st.ui.test.record")) : null,
        h(Btn, { key: "replay", kind: "menuPrimary", disabled: !r.canReplay || !m.chosen, onClick: function () { send("replayStart", m.chosen, m.speed); }, style: { minWidth: "220rem" } }, t("st.ui.test.replay")),
      ],
    },
    h("div", { style: { padding: "8rem 16rem 0 16rem" } }, h(VerdictCard, { verdict: r.verdict, menu: true })),
    h(MenuGroup, { right: m.recs.length > 0 ? h(SpeedPicker, { menu: true, value: m.speed, onChange: m.setSpeed }) : null }, t("st.ui.test.recordings")),
    m.recs.length === 0
      ? h(MenuText, { style: { color: T.menuText2 } }, t("st.ui.test.none"))
      : h("div", { style: { padding: "4rem 8rem" } }, m.recs.map(function (x) { return h(RecordingRow, { key: x.id, menu: true, rec: x, selected: x.id === m.chosen, onSelect: m.setSelected }); })),
    !inGame ? h(MenuText, { style: { color: T.menuText2, fontSize: FS.m, marginTop: "8rem" } }, t("st.ui.test.needCity")) : null
  );
}

function MenuSessionPage(p) {
  // a session is running while the title screen shows (rare: the game returned to the menu during a session)
  const s = p.state;
  return h(
    MenuPage,
    { title: panelTitle(s), subtitle: s.status || "", footer: h(Btn, { kind: "menuSecondary", onClick: function () { send("leave"); } }, t("st.ui.leave.client")) },
    h(MenuText, null, s.mode === "Hosting" ? t("st.ui.session.hosting") : s.mode === "Recovering" ? t("st.ui.recovery.badge") : t("st.ui.session.connected"))
  );
}

// the left column: the pages, and the Steam / self-test status at the bottom
function MenuNav(p) {
  const s = p.state;
  const menuCls = vc("options", "menu");
  return h(
    "div",
    { className: menuCls || undefined, style: merge(S.col, menuCls ? null : { width: "25%", marginRight: "12rem", backgroundColor: T.menuPanel, backdropFilter: T.menuBlur, borderRadius: T.radius }, { padding: "16rem" }) },
    p.items.map(function (item) {
      return h("div", { key: item.id, style: { marginBottom: "2rem" } }, h(Btn, { kind: "navItem", selected: p.value === item.id, onClick: function () { p.onChange(item.id); }, style: { width: "100%" } }, item.label));
    }),
    h("div", { style: { flex: 1 } }),
    h(
      "div",
      { style: merge({ paddingTop: "12rem", marginTop: "12rem" }, border("1rem", T.menuBorder, ["Top"])) },
      h(
        "div",
        { style: merge(S.row, { color: T.menuText2, fontSize: FS.s, marginBottom: "6rem" }) },
        h(Dot, { color: s.steam === false ? T.bad : T.good, size: 8 }),
        s.steam === false ? t("st.ui.steam.missing.title") : t("st.ui.steam.ok")
      ),
      s.blocked ? h("div", { style: { color: T.bad, fontSize: FS.xs, lineHeight: 1.35, marginBottom: "6rem" } }, t("st.ui.blocked", { REASON: s.blocked })) : null,
      h(SelfTest, { state: s, menu: true }),
      h(BuildLine, { state: s, menu: true })
    )
  );
}

function MultiplayerContent(p) {
  const s = p.state;
  const st = useStore();
  const mode = s.mode || "None";
  const page = st.tab === "host" || st.tab === "solo" ? st.tab : "join";
  const content = vc("options", "content");
  const detail = vc("options", "detail");
  const items = [{ id: "join", label: t("st.ui.nav.join") }, { id: "host", label: t("st.ui.nav.host") }, { id: "solo", label: t("st.ui.nav.solo") }];
  let body;
  if (mode === "Hosting" || mode === "Client" || mode === "Recovering") body = h(MenuSessionPage, { state: s });
  else if (page === "host") body = h(MenuHostPage, { key: "host", state: s });
  else if (page === "solo") body = h(MenuSoloPage, { key: "solo", state: s });
  else body = h(MenuJoinPage, { key: "join", state: s });
  return h(
    "div",
    { className: content || undefined, style: content ? null : merge(S.row, { width: "100%", height: "100%", alignItems: "stretch" }) },
    h(MenuNav, { state: s, items: items, value: page, onChange: function (id) { setStore({ tab: id }); } }),
    h("div", { className: detail || undefined, style: detail ? null : merge(S.col, { flex: 1, backgroundColor: T.menuPanel, backdropFilter: T.menuBlur, borderRadius: T.radius }) }, body)
  );
}

// The built-in copy of the vanilla sub-screen (title header with back button, content area); Escape closes it.
function MenuScreenFallback(p) {
  React.useEffect(function () {
    const onKey = function (e) {
      if (keyOf(e) === "Escape" && store.typing === 0) p.onClose();
    };
    window.addEventListener("keydown", onKey);
    return function () {
      window.removeEventListener("keydown", onKey);
    };
  }, []);
  return h(
    "div",
    { style: merge(S.fill, S.col, { pointerEvents: "auto", color: "#ffffff", padding: "48rem 64rem 40rem 64rem" }) },
    h(
      "div",
      { style: merge(S.row, { marginBottom: "8rem" }) },
      h(
        "div",
        { style: merge(S.center, { width: "40rem", height: "40rem", marginRight: "12rem", borderRadius: "8rem", cursor: "pointer" }, border("2rem", "#ffffff")), onClick: p.onClose },
        h(Glyph, { src: G.back, size: 20 })
      ),
      h("div", { style: { fontSize: "40rem", lineHeight: 1.2, fontWeight: "bold", color: "#ffffff", textTransform: "uppercase" } }, t("st.ui.menu.multiplayer"))
    ),
    h("div", { style: { flex: 1, position: "relative" } }, h("div", { style: S.fill }, p.children))
  );
}

// The "Multiplayer" screen of the main menu, in place of the main menu screen (like Load Game or Options).
function MultiplayerScreen(p) {
  const s = p.state;
  const close = function () {
    setStore({ menuOpen: false });
  };
  const content = h(MultiplayerContent, { state: s });
  const fallback = function () {
    return h(MenuScreenFallback, { onClose: close }, content);
  };
  if (!V.ok || !V.SubScreen) return fallback();
  return Safe(h(V.SubScreen, { focusKey: p.focusKey, className: p.className, title: t("st.ui.menu.multiplayer"), onClose: close }, content), fallback);
}

// -------------------------------------------------------------------------------- join overlay (literal colours)

const STAGES = ["connecting", "handshaking", "waiting", "downloading", "loading"];

function OvButton(p) {
  const [hover, hv] = useHover();
  const primary = p.kind === "primary";
  const disabled = !!p.disabled;
  return h(
    "div",
    merge(hv, {
      style: merge(
        { display: "flex", alignItems: "center", justifyContent: "center", height: "40rem", padding: "0 20rem", borderRadius: "5rem", fontSize: "16rem", textTransform: "uppercase", whiteSpace: "nowrap", cursor: disabled ? "default" : "pointer", color: "#ffffff", opacity: disabled ? 0.5 : 1 },
        primary ? merge({ backgroundImage: hover && !disabled ? OV.greenHover : OV.green }, border("2rem", OV.greenBorder)) : merge({ backgroundColor: hover && !disabled ? OV.buttonHover : OV.button }, border("2rem", OV.buttonBorder)),
        p.style
      ),
      onClick: function () {
        if (!disabled && p.onClick) p.onClick();
      },
    }),
    p.children
  );
}

// The card of the join and error screens: solid backdrop, explicit light text on a dark card. The backdrop is a
// viewport-sized box centred on its layer, so it covers the whole screen also from the menu's (centred) content area.
function OverlayCard(p) {
  return h(
    "div",
    { style: merge(S.center, { position: "absolute", left: "50%", top: "50%", width: "100vw", height: "100vh", marginLeft: "-50vw", marginTop: "-50vh", backgroundColor: OV.backdrop, pointerEvents: "auto", zIndex: p.z || 2000, color: OV.text, fontFamily: "var(--fontFamily)" }) },
    h(
      "div",
      { style: merge(S.col, { width: p.width || "560rem", maxHeight: "86vh", borderRadius: "6rem", backgroundColor: OV.cardSolid, backgroundImage: OV.card, boxShadow: "0 8rem 40rem rgba(0, 0, 0, 0.55)", overflow: "hidden" }, border("1rem", "rgba(255, 255, 255, 0.08)")) },
      h(
        "div",
        { style: merge(S.row, { minHeight: "56rem", padding: "10rem 20rem", backgroundColor: OV.header }) },
        p.icon || h(Glyph, { src: ICON_GLYPH, size: 24, color: OV.title, style: { marginRight: "12rem" } }),
        h("div", { style: merge(S.ellipsis, { flex: 1, fontSize: "20rem", fontWeight: "bold", textTransform: "uppercase", color: p.titleColor || OV.title }) }, p.title)
      ),
      h("div", { style: { padding: "18rem 24rem 20rem 24rem", overflowY: "auto" } }, p.children)
    )
  );
}

function StageRow(p) {
  const done = p.state === "done";
  const active = p.state === "active";
  const marker = done
    ? h("div", { style: merge(S.center, { width: "20rem", height: "20rem", borderRadius: "10rem", backgroundColor: OV.good }) }, h(Glyph, { src: G.check, size: 12, color: "#10331a" }))
    : active
      ? h("div", { style: merge(S.center, { width: "20rem", height: "20rem", borderRadius: "10rem" }, border("3rem", OV.accent)) }, h("div", { style: { width: "6rem", height: "6rem", borderRadius: "3rem", backgroundColor: OV.accent } }))
      : h("div", { style: merge({ width: "20rem", height: "20rem", borderRadius: "10rem" }, border("2rem", "rgba(255, 255, 255, 0.25)")) });
  return h(
    "div",
    { style: { display: "flex", flexDirection: "row", alignItems: "flex-start", padding: "7rem 0" } },
    h("div", { style: { width: "34rem", paddingTop: "1rem", flexShrink: 0 } }, marker),
    h(
      "div",
      { style: { flex: 1, minWidth: 0 } },
      h("div", { style: { fontSize: "17rem", color: p.state === "pending" ? OV.faint : OV.text, fontWeight: active ? "bold" : "normal" } }, t("st.ui.join.stage." + p.id)),
      active && p.hint ? h("div", { style: { marginTop: "4rem", fontSize: "14rem", color: OV.dim, lineHeight: 1.35 } }, p.hint) : null,
      active && p.progress != null ? h(Progress, { value: p.progress, height: 6, track: OV.track, color: OV.accent, style: { marginTop: "8rem" } }) : null
    )
  );
}

function JoinOverlay(p) {
  const s = p.state;
  const j = useJson(joinBinding);
  if ((s.mode || "None") !== "Joining") return null;
  const stage = STAGES.indexOf(j.stage) >= 0 ? j.stage : STAGES.indexOf(s.stage) >= 0 ? s.stage : "connecting";
  const at = STAGES.indexOf(stage);
  let hint = t("st.ui.join.hint." + stage);
  let progress = null;
  if (stage === "downloading") {
    progress = j.total > 0 ? j.done / j.total : 0;
    hint = j.total > 0 ? t("st.ui.join.hint.downloading", { DONE: mb(j.done), TOTAL: mb(j.total), SPEED: mb(j.bps), ETA: duration(j.eta) }) : t("st.ui.join.hint.downloadingStart");
  } else if (stage === "loading") {
    progress = j.load || 0;
  }
  const overall = j.progress != null ? j.progress : s.progress;
  const title = j.session ? t("st.ui.join.overlayTitle", { SESSION: j.session }) : t("st.ui.join.overlayTitleUnknown");
  return h(
    OverlayCard,
    { title: title, z: 2000 },
    j.target ? h("div", { style: merge(S.ids, { marginBottom: "10rem", fontSize: "14rem", color: OV.dim }) }, t("st.ui.join.via", { TARGET: j.target })) : null,
    h(
      "div",
      { style: { padding: "6rem 14rem", borderRadius: "4rem", backgroundColor: OV.well } },
      STAGES.map(function (id, i) {
        return h(StageRow, { key: id, id: id, state: i < at ? "done" : i === at ? "active" : "pending", hint: i === at ? hint : null, progress: i === at ? progress : null });
      })
    ),
    h(
      "div",
      { style: merge(S.row, { marginTop: "16rem" }) },
      h(Progress, { value: overall, height: 4, track: OV.track, color: OV.accent, style: { flex: 1 } }),
      h("div", { style: { width: "56rem", textAlign: "right", fontSize: "14rem", color: OV.dim } }, t("st.ui.join.percent", { P: Math.round(Math.max(0, Math.min(1, overall || 0)) * 100) }))
    ),
    h("div", { style: { display: "flex", flexDirection: "row", justifyContent: "flex-end", marginTop: "18rem" } }, h(OvButton, { onClick: function () { send("leave"); } }, t("st.ui.join.cancel")))
  );
}

// --------------------------------------------------------------------------------------------- error screen

function endCopy(e) {
  const r = e.kind === "refused" ? e.refuse : e.kind;
  if (e.kind === "passwordRequired") return "passwordRequired";
  if (e.kind === "refused") {
    if (r === "BadPassword") return "badPassword";
    if (r === "Mismatch") return e.reportKind === "game" ? "game" : e.reportKind === "build" ? "build" : "mods";
    if (r === "ProtocolVersion") return "build";
    if (r === "Full") return "full";
    if (r === "Banned") return "banned";
    if (r === "NotInvited") return "notAllowed";
    if (r === "HostPreparing") return "notReady";
    if (r === "HostShuttingDown") return "closing";
    return "failed";
  }
  if (e.kind === "timeout") return "timeout";
  if (e.kind === "connectionLost") return "lost";
  if (e.kind === "kicked") return "kicked";
  if (e.kind === "banned") return "banned";
  if (e.kind === "sessionEnded") return "ended";
  return "failed";
}

function ModList(p) {
  if (p.items.length === 0) return null;
  return h(
    "div",
    { style: { marginBottom: "12rem" } },
    h("div", { style: merge(S.row, { marginBottom: "6rem" }) }, h(Dot, { color: p.color }), h("div", { style: { fontSize: "15rem", fontWeight: "bold", textTransform: "uppercase", color: p.color } }, p.title + " (" + p.items.length + ")")),
    h(
      "div",
      { style: { maxHeight: "150rem", overflowY: "auto", padding: "4rem 12rem", borderRadius: "4rem", backgroundColor: OV.well } },
      p.items.map(function (m, i) {
        return h(
          "div",
          { key: i, style: merge({ display: "flex", flexDirection: "row", padding: "5rem 0", fontSize: "14rem" }, i === 0 ? null : border("1rem", OV.divider, ["Top"])) },
          h("div", { style: merge(S.ellipsis, { flex: 1, color: OV.text }) }, m.name || "?"),
          h("div", { style: { color: OV.dim, marginLeft: "10rem", flexShrink: 0 } }, p.versions ? t("st.ui.end.mods.versionLine", { HOST: m.host || "?", YOU: m.you || "?" }) : m.version || "")
        );
      })
    )
  );
}

function reportText(e, title) {
  const lines = [title, e.text || ""];
  list(e.missing).forEach(function (m) { lines.push("+ " + m.name + " " + (m.version || "")); });
  list(e.extra).forEach(function (m) { lines.push("- " + m.name + " " + (m.version || "")); });
  list(e.versions).forEach(function (m) { lines.push("~ " + m.name + " (host " + (m.host || "?") + ", you " + (m.you || "?") + ")"); });
  return lines.join("\n");
}

function EndOverlay(p) {
  const s = p.state;
  const st = useStore();
  const [password, setPassword] = React.useState("");
  const flash = useFlash();
  const e = s.end;
  if (!e || !e.kind || st.dismissedEnd === e.seq) return null;
  const copy = endCopy(e);
  const title = t("st.ui.end.title." + copy);
  const needsPassword = copy === "badPassword" || copy === "passwordRequired";
  const canRetry = copy !== "kicked" && copy !== "banned" && copy !== "notAllowed" && copy !== "ended" && copy !== "mods" && copy !== "game" && copy !== "build";
  const dismiss = function () {
    setStore({ dismissedEnd: e.seq });
    send("dismissEnd");
  };
  const retry = function () {
    if (needsPassword && password.length === 0) return;
    setStore({ dismissedEnd: e.seq });
    send("retryJoin", needsPassword ? password : s.keepPassword || KEEP_PASSWORD);
  };
  const isMods = copy === "mods" && (list(e.missing).length + list(e.extra).length + list(e.versions).length > 0);
  const icon = h("div", { style: merge(S.center, { width: "26rem", height: "26rem", borderRadius: "13rem", backgroundColor: needsPassword ? OV.warn : OV.bad, marginRight: "12rem", flexShrink: 0 }) }, h(Glyph, { src: needsPassword ? G.lock : G.warning, size: 14, color: "#ffffff" }));
  return h(
    OverlayCard,
    { title: title, z: 2100, width: isMods ? "660rem" : "560rem", icon: icon, titleColor: OV.text },
    h("div", { style: { fontSize: "16rem", marginBottom: "14rem", lineHeight: 1.45, color: OV.text } }, t("st.ui.end.text." + copy)),
    isMods
      ? h(
          "div",
          null,
          h(ModList, { title: t("st.ui.end.mods.missing"), color: OV.good, items: list(e.missing) }),
          h(ModList, { title: t("st.ui.end.mods.extra"), color: OV.bad, items: list(e.extra) }),
          h(ModList, { title: t("st.ui.end.mods.versions"), color: OV.warn, items: list(e.versions), versions: true })
        )
      : null,
    copy === "game" && e.hostGame ? h("div", { style: merge(S.ids, { marginBottom: "14rem", fontSize: "15rem", color: OV.text }) }, t("st.ui.end.versions", { HOST: e.hostGame, YOU: e.yourGame || "?" })) : null,
    copy === "build" && e.hostProto ? h("div", { style: merge(S.ids, { marginBottom: "14rem", fontSize: "15rem", color: OV.text }) }, t("st.ui.end.versions", { HOST: e.hostProto, YOU: e.yourProto || "?" })) : null,
    copy === "build" && e.hostBuild ? h("div", { style: merge(S.ids, { marginBottom: "14rem", fontSize: "15rem", color: OV.text }) }, t("st.ui.end.builds", { HOST: e.hostBuild, YOU: e.yourBuild || "?" })) : null,
    needsPassword
      ? h(
          "div",
          { style: { marginBottom: "10rem" } },
          h("div", { style: { fontSize: "12rem", textTransform: "uppercase", color: OV.faint, marginBottom: "5rem" } }, t("st.ui.join.password")),
          h(Field, { look: "overlay", value: password, onChange: setPassword, onEnter: retry, password: true, placeholder: t("st.ui.join.passwordPlaceholder"), title: t("st.ui.join.password") })
        )
      : null,
    e.text && !isMods
      ? h(
          "div",
          { style: { marginTop: "4rem", padding: "10rem 12rem", borderRadius: "4rem", backgroundColor: OV.well } },
          h("div", { style: { fontSize: "12rem", textTransform: "uppercase", color: OV.faint, marginBottom: "4rem" } }, t("st.ui.end.details")),
          h("div", { style: { fontSize: "14rem", color: OV.dim, lineHeight: 1.35 } }, e.text)
        )
      : null,
    h(
      "div",
      { style: merge(S.row, { marginTop: "18rem" }) },
      h("div", { style: merge(S.row, { cursor: "pointer", color: OV.accent, fontSize: "14rem" }), onClick: function () { send("copyText", reportText(e, title)); flash.fire(); } }, h(Glyph, { src: flash.on ? G.check : G.copy, size: 14, color: OV.accent, style: { marginRight: "6rem" } }), flash.on ? t("st.ui.end.copied") : t("st.ui.end.copy")),
      h("div", { style: { flex: 1 } }),
      h(OvButton, { onClick: dismiss }, t("st.ui.end.back")),
      canRetry ? h(OvButton, { kind: "primary", disabled: needsPassword && password.length === 0, onClick: retry, style: { marginLeft: "10rem" } }, needsPassword ? t("st.ui.end.retryPassword") : t("st.ui.end.retry")) : null
    )
  );
}

// ------------------------------------------------------------------------------------------------ chat dock

// first time each chat line (by its session-independent sequence number) was seen: the time shown on hover
const seenAt = {};

function anotherInputFocused() {
  try {
    const el = document.activeElement;
    return !!el && (el.tagName === "INPUT" || el.tagName === "TEXTAREA");
  } catch (e) {
    return false;
  }
}

function ChatLine(p) {
  const l = p.line;
  const [hover, hv] = useHover();
  const time = seenAt[l.seq] ? clock(seenAt[l.seq]) : "";
  return h(
    "div",
    merge(hv, { style: merge(S.row, { alignItems: "flex-start", padding: "3rem 8rem", borderRadius: "3rem", backgroundColor: hover ? "rgba(255, 255, 255, 0.05)" : "rgba(0, 0, 0, 0)" }) }),
    h(
      "div",
      { style: { flex: 1, minWidth: 0, fontSize: FS.s, lineHeight: 1.35 } },
      l.system
        ? h("span", { style: { color: T.textDimmer, fontStyle: "italic" } }, l.text)
        : [h("span", { key: "n", style: { color: l.color || T.accent, fontWeight: "bold" } }, l.name + "  "), h("span", { key: "t", style: { color: T.text } }, l.text)]
    ),
    h("div", { style: { width: "38rem", textAlign: "right", fontSize: FS.xs, color: T.textFaint, flexShrink: 0, paddingTop: "1rem", opacity: hover ? 1 : 0 } }, time)
  );
}

// The chat box docked on the right: collapsible, unread badge, player colours, Enter to type, Esc to leave.
function ChatDock(p) {
  const lines = list(p.state.chat);
  const st = useStore();
  const [text, setText] = React.useState("");
  const [typingHere, setTypingHere] = React.useState(false);
  const [preview, setPreview] = React.useState(-1);
  const inputRef = React.useRef(null);
  const scrollRef = React.useRef(null);
  const previewTimer = React.useRef(null);
  const now = Date.now();
  lines.forEach(function (l) {
    if (l && l.seq !== undefined && seenAt[l.seq] === undefined) seenAt[l.seq] = now;
  });
  const lastSeq = lines.length > 0 && lines[lines.length - 1].seq !== undefined ? lines[lines.length - 1].seq : -1;
  const open = st.chatOpen;
  let unread = 0;
  if (!open) for (const l of lines) if (!l.system && !l.you && l.seq > st.chatSeen) unread++;
  const focusInput = function () {
    try {
      if (inputRef.current) inputRef.current.focus();
    } catch (e) {
      // not mounted
    }
  };
  React.useEffect(function () {
    // Enter (while nothing else takes typing) opens the chat and focuses the input
    const onKey = function (e) {
      if (keyOf(e) !== "Enter" || store.typing > 0 || anotherInputFocused()) return;
      if (e.preventDefault) e.preventDefault();
      if (!store.chatOpen) setStore({ chatOpen: true, chatFocus: true });
      else focusInput();
    };
    window.addEventListener("keydown", onKey);
    return function () {
      window.removeEventListener("keydown", onKey);
      if (previewTimer.current) clearTimeout(previewTimer.current);
    };
  }, []);
  React.useEffect(function () {
    if (open) {
      if (store.chatSeen !== lastSeq) store.chatSeen = lastSeq;
      try {
        if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
      } catch (e) {
        // not mounted
      }
      if (store.chatFocus) {
        store.chatFocus = false;
        focusInput();
      }
    } else if (lastSeq > store.chatSeen) {
      // collapsed: show the newest line under the header for a few seconds
      const last = lines[lines.length - 1];
      if (last && !last.system && !last.you) {
        setPreview(lastSeq);
        if (previewTimer.current) clearTimeout(previewTimer.current);
        previewTimer.current = setTimeout(function () {
          setPreview(-1);
        }, 8000);
      }
    }
  }, [open, lastSeq]);
  const blur = function () {
    try {
      if (inputRef.current) inputRef.current.blur();
    } catch (e) {
      // already blurred
    }
  };
  const sendLine = function () {
    if (text.trim().length > 0) send("chat", text);
    setText("");
    blur();
  };
  const toggle = function () {
    setStore({ chatOpen: !store.chatOpen, chatSeen: lastSeq });
  };
  const previewLine = !open && preview >= 0 ? lines.filter(function (l) { return l.seq === preview; })[0] : null;
  const [hover, hv] = useHover();
  return h(
    "div",
    // right side, above the toolbar (two rows of --toolbarHeight) and left of the right-hand button column
    { style: { position: "absolute", right: "62rem", bottom: "calc(var(--toolbarHeight) * 2 + 20rem)", width: "340rem", pointerEvents: "auto", zIndex: 900 } },
    previewLine
      ? h("div", { style: { marginBottom: "6rem", padding: "6rem 10rem", borderRadius: T.radius, backgroundColor: T.panelDark, backdropFilter: T.blur } }, h(ChatLine, { line: previewLine }))
      : null,
    h(
      "div",
      { style: merge(S.col, { borderRadius: T.radius, boxShadow: "0 4rem 20rem rgba(0, 0, 0, 0.25)" }) },
      h(
        "div",
        merge(hv, {
          style: merge(S.row, { minHeight: "36rem", padding: "4rem 6rem 4rem 12rem", cursor: "pointer", color: T.titleBlue, backgroundColor: hover ? "var(--panelColorDark-hover)" : T.panelDark, backdropFilter: T.blur, borderTopLeftRadius: T.radius, borderTopRightRadius: T.radius }, open ? null : { borderBottomLeftRadius: T.radius, borderBottomRightRadius: T.radius }),
          onClick: toggle,
        }),
        h("div", { style: { flex: 1, textTransform: "uppercase", fontSize: FS.m } }, t("st.ui.chat.title")),
        unread > 0 ? h("div", { style: { minWidth: "20rem", height: "20rem", padding: "0 6rem", borderRadius: "10rem", backgroundColor: T.accent, color: "#0c1a25", fontSize: FS.xs, fontWeight: "bold", display: "flex", alignItems: "center", justifyContent: "center", marginRight: "8rem" } }, t("st.ui.chat.unread", { N: unread })) : null,
        withTooltip(h("div", { style: merge(S.center, { width: "26rem", height: "26rem" }) }, h(Glyph, { src: open ? G.down : G.up, size: 11, color: T.textDim })), open ? t("st.ui.chat.collapse") : t("st.ui.chat.expand"), "up")
      ),
      open
        ? h(
            "div",
            { style: merge(S.col, { backgroundColor: "rgba(42, 55, 83, 0.6)", backgroundImage: T.panelGradient, backdropFilter: T.blur, borderBottomLeftRadius: T.radius, borderBottomRightRadius: T.radius }) },
            h(
              Scroll,
              { scrollRef: scrollRef, style: { height: "220rem", padding: "6rem 4rem" } },
              lines.length === 0 ? h(Hint, { style: { padding: "8rem" } }, t("st.ui.chat.empty")) : lines.map(function (l, i) { return h(ChatLine, { key: l.seq !== undefined ? l.seq : i, line: l }); })
            ),
            h(
              "div",
              { style: merge({ padding: "8rem" }, border("2rem", T.divider, ["Top"])) },
              h(Field, {
                inputRef: inputRef,
                value: text,
                onChange: setText,
                onEnter: sendLine,
                onEscape: function () { setText(""); },
                onFocus: function () { setTypingHere(true); },
                onBlur: function () { setTypingHere(false); },
                placeholder: typingHere ? t("st.ui.chat.placeholder") : t("st.ui.chat.hint"),
                title: t("st.ui.chat.title"),
                max: 500,
              })
            )
          )
        : null
    )
  );
}

// ---------------------------------------------------------------------------------------------- name tags

// Remote players' name tags over their cursors (positions are fractions of the screen, from C#).
function NameTags() {
  const tags = list(useJson(presenceBinding).tags);
  return h(
    "div",
    { style: S.layer },
    tags
      .filter(function (tag) {
        return tag && tag.on;
      })
      .map(function (tag) {
        return h(
          "div",
          {
            key: tag.id,
            style: merge(
              {
                position: "absolute",
                left: (Number(tag.x) * 100).toFixed(2) + "%",
                top: (Number(tag.y) * 100).toFixed(2) + "%",
                transform: "translate(-50%, -130%)",
                padding: "2rem 9rem",
                borderRadius: "11rem",
                backgroundColor: "rgba(16, 22, 34, 0.82)",
                color: T.text,
                fontSize: FS.s,
                whiteSpace: "nowrap",
              },
              border("2rem", tag.color || "#4bc3f1")
            ),
          },
          h("span", { style: { color: tag.color || T.text, fontWeight: "bold" } }, tag.name),
          tag.activity ? h("span", { style: { color: T.textDim } }, " · " + tag.activity) : null
        );
      })
  );
}

// --------------------------------------------------------------------------------------------- buttons

function statusColor(mode) {
  return mode === "Joining" ? T.warn : mode === "Hosting" || mode === "Client" ? T.good : mode === "Recovering" ? T.bad : null;
}

function Badge(p) {
  const s = p.state;
  const mode = s.mode || "None";
  const color = statusColor(mode);
  if (!color) return null;
  const count = list(s.players).length;
  return h(
    "div",
    { style: { position: "absolute", right: "-4rem", top: "-4rem", minWidth: "18rem", height: "18rem", padding: "0 4rem", borderRadius: "9rem", backgroundColor: color, color: "rgba(12, 26, 37, 0.9)", fontSize: FS.xs, fontWeight: "bold", display: "flex", alignItems: "center", justifyContent: "center", pointerEvents: "none" } },
    mode === "Joining" ? "…" : mode === "Recovering" ? "!" : String(count)
  );
}

function FallbackRoundButton(p) {
  return h(
    "div",
    {
      style: { width: "40rem", height: "40rem", borderRadius: "6rem", display: "flex", alignItems: "center", justifyContent: "center", backgroundColor: p.selected ? T.selected : T.panelDark, backdropFilter: T.blur, cursor: "pointer", pointerEvents: "auto" },
      onClick: p.onSelect,
    },
    h(Glyph, { src: ICON_GLYPH, size: 24 })
  );
}

function tooltipNode(s) {
  const count = list(s.players).length;
  const live = s.mode === "Hosting" || s.mode === "Client";
  return h(
    "div",
    { style: { maxWidth: "280rem" } },
    h("div", { style: { fontWeight: "bold", marginBottom: "3rem" } }, t("st.ui.title")),
    h("div", null, live ? t("st.ui.button.players", { N: count }) : t("st.ui.button.tooltip"))
  );
}

// The round button in the game's top-left button row (moduleRegistry "GameTopLeft").
function TopLeftButton() {
  useI18n();
  const s = useJson(stateBinding);
  const st = useStore();
  const panel = useGamePanel();
  React.useEffect(function () {
    store.topLeftMounted = true;
    emit();
    return function () {
      store.topLeftMounted = false;
    };
  }, []);
  if (panel === "PhotoMode") return null;
  const toggle = function () {
    setStore({ open: !store.open });
  };
  const fallback = function () {
    return h(FallbackRoundButton, { selected: st.open, onSelect: toggle });
  };
  const button = V.ok && V.FloatingButton ? Safe(h(V.FloatingButton, { src: ICON_GLYPH, selected: st.open, onSelect: toggle, focusKey: V.focusDisabled }), fallback) : fallback();
  return withTooltip(h("div", { style: { position: "relative", pointerEvents: "auto" } }, button, h(Badge, { state: s })), tooltipNode(s));
}

// Used when the top-left row or the main menu entry is unavailable.
function FallbackToggle(p) {
  const s = p.state;
  const mode = s.mode || "None";
  const color = statusColor(mode) || T.textFaint;
  return h(
    "div",
    {
      style: { position: "absolute", left: "10rem", top: p.menu ? "10rem" : "100rem", height: "36rem", padding: "0 14rem", display: "flex", alignItems: "center", borderRadius: "18rem", backgroundColor: T.panelDark, backdropFilter: T.blur, color: T.text, fontSize: FS.m, cursor: "pointer", pointerEvents: "auto", zIndex: 1000 },
      onClick: p.onClick,
    },
    h(Glyph, { src: ICON_GLYPH, size: 20, style: { marginRight: "8rem" } }),
    h(Dot, { color: color, size: 8 }),
    mode === "None" ? t("st.ui.menu.multiplayer") : mode === "Joining" ? t("st.ui.join.overlayTitleUnknown") : mode === "Recovering" ? t("st.ui.menu.multiplayer") + " · " + t("st.ui.recovery.badge") : t("st.ui.menu.multiplayer") + " · " + list(s.players).length
  );
}

// ------------------------------------------------------------------------------------------------- roots

function GameRoot() {
  useI18n();
  const s = useJson(stateBinding);
  const st = useStore();
  const panel = useGamePanel();
  const photo = panel === "PhotoMode";
  const social = panel === "Chirper" || panel === "Radio" || panel === "LifePath" || panel === "Journal" || panel === "Notifications";
  const [noTopLeft, setNoTopLeft] = React.useState(false);
  React.useEffect(function () {
    // the top-left row may not exist in this game version: then the classic toggle appears
    const timer = setTimeout(function () {
      if (!store.topLeftMounted) setNoTopLeft(true);
    }, 3000);
    return function () {
      clearTimeout(timer);
    };
  }, []);
  const mode = s.mode || "None";
  const live = mode === "Hosting" || mode === "Client";
  const recoveryPhase = (s.recovery && s.recovery.phase) || "";
  React.useEffect(function () {
    if (mode === "Recovering") setStore({ open: true }); // robust branch: the recovery choice must be seen
  }, [mode, recoveryPhase]);
  const close = function () {
    setStore({ open: false, invitesOpen: false });
  };
  const showInvites = st.open && st.invitesOpen && mode === "Hosting" && !!s.canInvite;
  return h(
    "div",
    { style: S.layer },
    live && !photo ? h(NameTags, null) : null, // presence branch: under the panel
    live && !photo && !social ? h(ChatDock, { state: s }) : null,
    !photo && (!topLeftRegistered || (noTopLeft && !st.topLeftMounted)) ? h(FallbackToggle, { state: s, onClick: function () { setStore({ open: !store.open }); } }) : null,
    st.open && !photo
      ? h(
          "div",
          { style: S.gamePanelPos },
          h(
            PanelShell,
            { title: panelTitle(s), status: statusColor(mode), onClose: close, width: "420rem", maxHeight: "78vh", footer: h("div", null, live ? h(LeaveButton, { state: s }) : null, h(BuildLine, { state: s, style: live ? null : { marginTop: 0 } })) },
            s.steam === false && mode === "None" ? h(SteamMissing, { style: { margin: "8rem" } }) : h(PanelContent, { state: s })
          ),
          showInvites ? h(InvitePopover, null) : null
        )
      : null,
    h(JoinOverlay, { state: s }),
    h(EndOverlay, { state: s })
  );
}

function MenuRoot() {
  useI18n();
  const s = useJson(stateBinding);
  const st = useStore();
  const [noMenuEntry, setNoMenuEntry] = React.useState(false);
  React.useEffect(function () {
    // final review: extend() succeeding does not prove the vanilla navigation renders our entry; without it there was
    // no way into multiplayer from the title screen, so the corner toggle appears when the entry never mounted
    const timer = setTimeout(function () {
      if (!store.menuEntryMounted) setNoMenuEntry(true);
    }, 2500);
    return function () {
      clearTimeout(timer);
      // the menu screen must not pop up again by itself the next time the title screen is shown (after a join); with
      // the screen installed in place of the main menu screen, its own wrapper resets this (it replaces this root)
      if (!menuScreenInstalled) store.menuOpen = false;
    };
  }, []);
  const close = function () {
    setStore({ menuOpen: false });
  };
  // the multiplayer screen normally replaces the main menu screen (installMenuScreen); this layer is the fallback
  const ownScreen = st.menuOpen && !menuScreenInstalled;
  return h(
    "div",
    { style: S.layer },
    menuButtonInstalled && !(noMenuEntry && !st.menuEntryMounted) ? null : h(FallbackToggle, { state: s, menu: true, onClick: function () { setStore({ menuOpen: !store.menuOpen }); } }),
    ownScreen ? h("div", { style: merge(S.fill, { backgroundColor: "rgba(10, 14, 20, 0.85)", backdropFilter: T.menuBlur, pointerEvents: "auto" }) }) : null,
    ownScreen ? h(MenuScreenFallback, { onClose: close }, h(MultiplayerContent, { state: s })) : null,
    h(JoinOverlay, { state: s }),
    h(EndOverlay, { state: s })
  );
}

// ---- title-screen "Multiplayer" entry (in the vanilla main menu list, between Load Game and Editor) ----

function MenuEntry() {
  useI18n();
  React.useEffect(function () {
    store.menuEntryMounted = true;
    return function () {
      store.menuEntryMounted = false;
    };
  }, []);
  const label = t("st.ui.menu.multiplayer");
  // never break the vanilla menu: a failing game button falls back to a plain entry in the same place
  return Safe(h(V.LabeledIconButton, { tinted: true, src: ICON_GLYPH, theme: V.menuTheme, onSelect: openPanel }, label), function () {
    return h(
      "div",
      { style: { height: "56rem", display: "flex", alignItems: "center", padding: "0 20rem", marginTop: "12rem", borderRadius: T.radius, backgroundColor: T.menuPanel, color: T.menuText, fontSize: FS.xl, textTransform: "uppercase", cursor: "pointer" }, onClick: openPanel },
      label
    );
  });
}

function installMainMenuButton(moduleRegistry) {
  try {
    if (!V.LabeledIconButton || !V.menuTheme || !comp(regGet(moduleRegistry, MAIN_MENU_PATH, "MainMenuNavigation"))) return false;
    moduleRegistry.extend(MAIN_MENU_PATH, "MainMenuNavigation", function (Original) {
      return function SkylineTogetherMainMenuNavigation(props) {
        try {
          const children = React.Children.toArray(props.children);
          // Continue, New Game, Load Game, then ours
          children.splice(Math.min(3, children.length), 0, h(MenuEntry, { key: "skyline-together-multiplayer" }));
          return h(Original, Object.assign({}, props, { children: children }));
        } catch (e) {
          return h(Original, props);
        }
      };
    });
    return true;
  } catch (e) {
    console.warn("SkylineTogether: main menu button unavailable (" + e + "); using the corner button");
    return false;
  }
}

// The multiplayer screen takes the main menu screen's place while it is open, exactly like the vanilla screens
// (Load Game, Options) do: same area, same header, the vanilla Back action (Escape, gamepad B) closes it.
function installMenuScreen(moduleRegistry) {
  try {
    if (!comp(regGet(moduleRegistry, MAIN_MENU_PATH, "MainMenuScreen"))) return false;
    moduleRegistry.extend(MAIN_MENU_PATH, "MainMenuScreen", function (Original) {
      const render = function SkylineTogetherMainMenuScreen(props, ref) {
        useI18n();
        const st = useStore();
        const s = useJson(stateBinding);
        React.useEffect(function () {
          return function () {
            // the title screen went away (a city loads, another vanilla screen opened): start on the main menu next time
            store.menuOpen = false;
          };
        }, []);
        const original = function () {
          return h(Original, Object.assign({}, props, ref ? { ref: ref } : null));
        };
        if (!st.menuOpen) return original();
        return h(
          "div",
          { style: merge(S.fill, { pointerEvents: "auto" }) },
          h(MultiplayerScreen, { state: s, focusKey: props.focusKey, className: props.className }),
          h(JoinOverlay, { state: s }),
          h(EndOverlay, { state: s })
        );
      };
      return typeof React.forwardRef === "function" ? React.forwardRef(render) : render;
    });
    return true;
  } catch (e) {
    console.warn("SkylineTogether: multiplayer screen unavailable (" + e + "); using an overlay");
    return false;
  }
}

export default function register(moduleRegistry) {
  if (!ready) {
    console.warn("SkylineTogether: UI globals missing; the panel is unavailable (use the Options page)");
    return;
  }
  try {
    resolveVanilla(moduleRegistry);
  } catch (e) {
    console.warn("SkylineTogether: game components unavailable (" + e + "); using the built-in look");
  }
  menuButtonInstalled = installMainMenuButton(moduleRegistry);
  // In-game report: replacing the vanilla MainMenuScreen rendered NOTHING after clicking Multiplayer (the vanilla
  // sub-screen needs context of the real menu screen stack). The multiplayer page is drawn as our own full-screen
  // layer over the title screen instead (MenuRoot), which is known to render. installMenuScreen stays for later.
  menuScreenInstalled = false;
  try {
    moduleRegistry.append("GameTopLeft", TopLeftButton);
    topLeftRegistered = true;
  } catch (e) {
    topLeftRegistered = false;
    console.warn("SkylineTogether: no top-left button row (" + e + "); using the classic toggle");
  }
  moduleRegistry.append("Game", GameRoot);
  moduleRegistry.append("Menu", MenuRoot);
}
