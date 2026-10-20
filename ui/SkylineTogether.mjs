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
// game component or theme is missing (or throws while rendering), a built-in look modelled on the game's styles is used.
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
// The module used to throw at import when a global was missing; now it registers nothing (ARCHITECTURE.md#ui-module).
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
// the performance overlay ({show, frame, features, peers, queue}; PerfOverlay.cs)
const perfBinding = bind("perf", "{}");
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
    const v = JSON.parse(typeof json === "string" && json.length > 0 ? json : "{}");
    return v !== null && typeof v === "object" && !Array.isArray(v) ? v : {};
  } catch (e) {
    return {};
  }
}

// The last parsed value per binding: presence updates up to 20 times a second and several components read the same
// binding, so each new string is parsed once (not once per component and render). Treat the result as read-only.
const parsed = new Map();
function useJson(binding) {
  let raw = null;
  try {
    raw = api.useValue(binding);
  } catch (e) {
    raw = null;
  }
  const hit = parsed.get(binding);
  if (hit && hit.raw === raw) return hit.value;
  const value = parse(raw);
  parsed.set(binding, { raw: raw, value: value });
  return value;
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

// the entries of a list that are objects (a null or a stray value in a binding's array never reaches a component)
function obj(v) {
  return v !== null && typeof v === "object" && !Array.isArray(v) ? v : {};
}

function objs(v) {
  return list(v).filter(function (x) {
    return x !== null && typeof x === "object" && !Array.isArray(x);
  });
}

// a value from a binding as display text: strings and numbers only (an object as a React child throws)
function str(v) {
  return typeof v === "string" ? v : typeof v === "number" && isFinite(v) ? String(v) : "";
}

// a finite number from a binding, or the fallback
function num(v, fallback) {
  const n = typeof v === "number" ? v : typeof v === "string" && v.trim().length > 0 ? Number(v) : NaN;
  return isFinite(n) ? n : fallback;
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
  "st.ui.loading": "Loading…",
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
  "st.ui.host.requireAssets": "Require the same asset packs",
  "st.ui.host.requireAssetsHint": "Refuse players whose asset packs differ from yours. When off, they join, are told which packs differ, and objects from packs they don't have are left out in their game.",
  "st.ui.host.allowModOverride": "Let players join with different mods",
  "st.ui.host.allowModOverrideHint": "When a player's mods or asset packs don't match what you require, they see what differs and can still choose \"Join anyway\". Their game may then behave differently; your city stays authoritative. DLC, version and password checks always apply.",
  "st.ui.host.allowDirect": "Allow direct IP connections",
  "st.ui.host.allowDirectHint": "Also accept connections on a UDP port (needs port forwarding and a host password). Steam connections always work.",
  "st.ui.host.allowDirectNextTime": "Applies the next time you host.",
  "st.ui.host.friendsOnly": "Friends and invited only",
  "st.ui.host.friendsOnlyHint": "Only your Steam friends and players you invite from this panel can join through Steam. Direct IP joiners still need the password.",
  "st.ui.host.newPlayersViewer": "New players join as viewers",
  "st.ui.host.newPlayersViewerHint": "Players joining for the first time in this session can watch but not build until you make them builders in the player list. Players who were here before keep their role when they rejoin.",
  "st.ui.host.allowDirectNeedsPassword": "Set a password first: Steam does not check direct connections, so the password is what keeps strangers out.",
  "st.ui.host.bans": "Banned players",
  "st.ui.host.bansHint": "Banned players can't join your sessions until you unban them.",
  "st.ui.host.unban": "Unban",
  "st.ui.host.banAddress": "address {ADDRESS}",
  "st.ui.host.port": "Port",
  "st.ui.host.portHint": "The UDP port players connect to directly (1024 to 65535).",
  "st.ui.host.portInvalid": "Enter a port from 1024 to 65535.",
  "st.ui.host.start": "Host this city",
  "st.ui.host.needCity": "Load or start a city first, then host it from there.",
  "st.ui.host.unavailable": "Hosting isn't possible right now. Wait until the city has finished loading, and leave any running session first.",
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
  "st.ui.join.target": "Host's Steam ID or IP address",
  "st.ui.join.targetShort": "Host",
  "st.ui.join.targetPlaceholder": "Steam ID or IP:port",
  "st.ui.join.badSteamId": "A Steam ID has 17 digits. Copy it from the host's Skyline Together panel.",
  "st.ui.join.badPort": "The port after the colon must be a number from 1 to 65535.",
  "st.ui.join.badTarget": "Enter the host's Steam ID (17 digits) or an address like 203.0.113.7:27015.",
  "st.ui.join.password": "Password",
  "st.ui.join.passwordPlaceholder": "If the host set one",
  "st.ui.join.passwordHint": "Only needed when the host set a password.",
  "st.ui.join.start": "Join",
  "st.ui.join.unavailable": "Joining isn't possible right now. Wait until the game has finished loading, and leave any running session first.",
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
  "st.ui.join.hint.downloading": "{DONE} of {TOTAL} MB",
  "st.ui.join.hint.speed": "{SPEED} MB/s",
  "st.ui.join.hint.eta": "{ETA} left",
  "st.ui.join.hint.downloadingStart": "Starting the download…",
  "st.ui.join.hint.loading": "Your game is loading the host's city. The session starts right after.",
  "st.ui.join.cancel": "Cancel",
  "st.ui.join.patience": "Large cities take a few minutes to download and load.",
  "st.ui.join.percent": "{P} %",
  "st.ui.time.seconds": "{N} s",
  "st.ui.time.minutes": "{M}:{S} min",
  "st.ui.end.retry": "Try again",
  "st.ui.end.retryPassword": "Join with this password",
  "st.ui.end.joinAnyway": "Join anyway",
  "st.ui.end.joinAnywayWarning": "Different mods can make your cities drift apart or behave differently. The host's city is authoritative; differences are repaired where possible.",
  "st.ui.end.back": "Back",
  "st.ui.end.copy": "Copy details",
  "st.ui.end.copied": "Copied",
  "st.ui.end.details": "Details",
  "st.ui.end.title.badPassword": "Wrong password",
  "st.ui.end.text.badPassword": "The host didn't accept the password. Check it with the host and try again. After several wrong tries the host's game blocks new attempts for a while.",
  "st.ui.end.title.lockout": "Too many wrong passwords",
  "st.ui.end.text.lockout": "The host's game blocks new attempts from your connection for a while after several wrong passwords. Wait until the time below has passed, check the password with the host, then try again.",
  "st.ui.end.title.passwordRequired": "This session needs a password",
  "st.ui.end.text.passwordRequired": "Ask the host for the password and enter it below.",
  "st.ui.end.title.mods": "Your mods don't match the host's",
  "st.ui.end.text.mods": "The host requires the same code mods. Change these, restart the game and join again.",
  "st.ui.end.text.modsOverridable": "Your code mods differ from the host's. Best: change these, restart the game and join again. The host also lets you join anyway.",
  "st.ui.end.mods.missing": "Enable or subscribe",
  "st.ui.end.mods.extra": "Disable",
  "st.ui.end.mods.versions": "Update to the host's version",
  "st.ui.end.mods.versionLine": "host {HOST} · you {YOU}",
  "st.ui.end.title.game": "Game versions differ",
  "st.ui.end.text.game": "You both need the same game version. Update the game on the PC with the older version (restarting Steam usually does it), then join again.",
  "st.ui.end.versions": "Host: {HOST} · You: {YOU}",
  "st.ui.end.title.build": "Skyline Together versions differ",
  "st.ui.end.text.build": "You both need exactly the same Skyline Together build. Install the same release on both PCs (the build id is at the bottom of the panel), then join again.",
  "st.ui.end.builds": "Host build {HOST} · Your build {YOU}",
  "st.ui.end.title.content": "You're missing DLC or content packs the host has",
  "st.ui.end.text.content": "The host owns these DLC or content packs, and their city can contain objects from them that your game can't load. Get them (Steam or the Paradox launcher), restart the game and join again.",
  "st.ui.end.content.missing": "Get these",
  "st.ui.end.content.extra": "Only you have these",
  "st.ui.end.title.assets": "Your asset packs don't match the host's",
  "st.ui.end.text.assets": "The host requires the same asset packs. Subscribe to the missing ones, disable the ones the host doesn't have, update the others, restart the game and join again.",
  "st.ui.end.text.assetsOverridable": "Your asset packs differ from the host's. Best: subscribe to the missing ones, disable the ones the host doesn't have, update the others, restart the game and join again. The host also lets you join anyway.",
  "st.ui.end.assets.missing": "Subscribe",
  "st.ui.end.assets.extra": "Disable (the host doesn't have them)",
  "st.ui.end.assets.versions": "Update to the host's version",
  "st.ui.end.paradoxId": "Paradox Mods {ID}",
  "st.ui.end.title.modsRequired": "Mods required for this city",
  "st.ui.end.text.modsRequired": "The host's city stores data of mods you don't have. Your game would crash loading it without them, so you can't join until you enable them – not even with Join anyway. Enable or subscribe to them, restart the game and join again.",
  "st.ui.end.text.modsRequiredRest": "The host's city stores data of mods you don't have. Your game would crash loading it without them, so you can't join until you enable them – not even with Join anyway. Enable or subscribe to them, restart the game and join again. The host lets you join anyway with the other differences once these are enabled.",
  "st.ui.end.mods.required": "Enable these mods to join",
  "st.ui.end.mods.requiredInCity": "This city contains its data – loading it without the mod would crash the game.",
  "st.ui.end.mods.requiredMaybe": "It can store data in a city – loading the city without the mod can crash the game.",
  "st.ui.end.mods.others": "Other differences",
  "st.ui.end.title.conflict": "Two multiplayer mods at once",
  "st.ui.end.text.conflictYou": "The 'CS2 Multiplayer Mod' is enabled – two multiplayer mods at once conflict. Disable it in your playset or unsubscribe from it, restart the game and join again.",
  "st.ui.end.text.conflictHost": "The host has the 'CS2 Multiplayer Mod' enabled – two multiplayer mods at once conflict. Ask the host to disable it and restart the game, then join again.",
  "st.ui.end.title.full": "The session is full",
  "st.ui.end.text.full": "Ask the host to raise the player limit, or try again when someone leaves.",
  "st.ui.end.title.notAllowed": "You can't join this session",
  "st.ui.end.text.notAllowed": "This session is open to the host's Steam friends and invited players only. Ask the host to send you a Steam invite. Joining by IP address also needs the host's password.",
  "st.ui.end.title.notReady": "The host isn't ready yet",
  "st.ui.end.text.notReady": "The host's session is still starting, or an earlier join attempt is still running. Try again in a moment.",
  "st.ui.end.title.closing": "The host is closing the session",
  "st.ui.end.text.closing": "The session is shutting down. Join again once the host hosts a new one.",
  "st.ui.end.title.timeout": "No answer from the host",
  "st.ui.end.text.timeout": "The host didn't answer in time. Check the Steam ID or address and make sure the host is hosting right now, then try again.",
  "st.ui.end.title.lost": "Connection lost",
  "st.ui.end.text.lost": "The connection to the host was interrupted. Try again; if it keeps happening, check your internet connection.",
  "st.ui.end.title.kicked": "You were removed from the session",
  "st.ui.end.text.kicked": "The host removed you from the session. You can join again if the host lets you.",
  "st.ui.end.title.banned": "You are banned from this session",
  "st.ui.end.text.banned": "The host banned you. Only the host can lift the ban.",
  "st.ui.end.title.ended": "The session has ended",
  "st.ui.end.text.ended": "The host ended the session.",
  "st.ui.end.title.failed": "Joining failed",
  "st.ui.end.text.failed": "Something went wrong while joining. Try again. If it keeps happening, use Copy details and attach them and the diagnostics file (Options → Skyline Together → Write diagnostics) to a bug report on GitHub (github.com/ljkx/SkylineTogether/issues).",
  "st.ui.end.title.loadFailed": "Your game couldn't load the host's city",
  "st.ui.end.text.loadFailed": "The city arrived, but loading it failed. Try again. If it keeps failing, make sure you have the host's map and assets. If it still fails, use Copy details and attach them to a bug report on GitHub (github.com/ljkx/SkylineTogether/issues).",
  "st.ui.session.host": "Host",
  "st.ui.session.player": "Player",
  "st.ui.session.you": "you",
  "st.ui.session.live": "Live",
  "st.ui.session.hosting": "You are hosting",
  "st.ui.session.connected": "Connected",
  "st.ui.session.menuNote": "A session is still running. Leave it before you host or join another one.",
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
  "st.ui.invite.direct": "Direct connection: your public IP address and port {PORT}.",
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
  "st.ui.players.unnamed": "Player {N}",
  "st.ui.players.empty": "No players yet.",
  "st.ui.players.kick": "Kick",
  "st.ui.players.makeViewer": "Make viewer",
  "st.ui.players.makeBuilder": "Make builder",
  "st.ui.players.kickConfirm": "Confirm kick",
  "st.ui.players.ban": "Ban",
  "st.ui.players.banConfirm": "Confirm ban",
  "st.ui.players.confirmHint": "Click again to confirm.",
  "st.ui.players.makeCoHost": "Make co-host",
  "st.ui.players.removeCoHost": "Remove co-host",
  "st.ui.players.actions": "Player actions",
  "st.ui.players.state.Handshaking": "connecting…",
  "st.ui.players.state.WaitingForSnapshot": "waiting for the city…",
  "st.ui.players.state.Downloading": "downloading {P} %",
  "st.ui.players.state.Loading": "loading {P} %",
  "st.ui.players.state.Offline": "offline",
  "st.ui.players.ping": "{N} ms",
  "st.ui.players.pingHint": "Time for a round trip to the host. Under 80 ms feels instant; above 250 ms actions show up late.",
  "st.ui.role.host": "Host",
  "st.ui.role.cohost": "Co-host",
  "st.ui.role.builder": "Builder",
  "st.ui.role.viewer": "Viewer",
  "st.ui.session.cohost": "You are a co-host: you can remove builders and viewers and switch them between building and watching.",
  "st.ui.settings.title": "Session settings",
  "st.ui.settings.livePassword": "New password",
  "st.ui.settings.apply": "Apply",
  "st.ui.chat.title": "Chat",
  "st.ui.chat.hostBadge": "Host",
  "st.ui.chat.placeholder": "Message (Enter to send)",
  "st.ui.chat.overlayPlaceholder": "Say something… (Enter to send, Esc to close)",
  "st.ui.chat.hint": "Press Enter to chat",
  "st.ui.chat.empty": "No messages yet.",
  "st.ui.chat.unread": "{N} new",
  "st.ui.chat.newBelow": "{N} new below",
  "st.ui.chat.count": "{N} / {MAX}",
  "st.ui.chat.collapse": "Hide the chat",
  "st.ui.chat.expand": "Show the chat",
  "st.ui.net.show": "Show network stats",
  "st.ui.net.hide": "Hide network stats",
  "st.ui.net.diagnostics": "Write diagnostics",
  "st.ui.perf.show": "Show performance",
  "st.ui.perf.hide": "Hide performance",
  "st.ui.perf.title": "Performance",
  "st.ui.perf.frame": "Frame budget {MS} of {BUDGET} ms · {OVER} frames over",
  "st.ui.perf.feature": "{AVG} / {P95} of {CAP} ms",
  "st.ui.perf.queue": "Apply queue: {N}",
  "st.ui.perf.peers": "Peers",
  "st.ui.perf.peer": "in {IN} · out {OUT} KB/s · {PING} ms",
  "st.ui.perf.deferred": "{N} waiting",
  "st.ui.diag.title": "Network & diagnostics",
  "st.ui.leave.client": "Leave session",
  "st.ui.leave.host": "End session",
  "st.ui.leave.confirm": "Click again to confirm",
  // recovery after an unexpected disconnect, self-test line
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
  "st.ui.resync.text": "Parts of your city no longer match the host's, and automatic repairs couldn't fix them. Rejoin with a fresh copy of the host's city? It takes about as long as joining.",
  "st.ui.resync.now": "Rejoin now",
  "st.ui.resync.later": "Not now",
  "st.ui.selftest.details": "Details",
  // solo testing (record/replay verdict)
  "st.ui.test.title": "Solo test (no second PC needed)",
  "st.ui.test.subtitle": "Check the sync on this PC, no second player needed",
  "st.ui.test.intro": "Record a session on this PC, then replay it the way a joining player receives it. The replay checks that every object, line, icon and global value ends up exactly as on the host.",
  "st.ui.test.record": "Record test session",
  "st.ui.test.recordHint": "Records everything a joining player would receive while you play (if you aren't hosting, the city is hosted privately for the recording). Build and play as usual, then stop the recording.",
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
  "st.ui.conflictMod": "The 'CS2 Multiplayer Mod' ({MOD}) is enabled. Disable it and restart the game – two multiplayer mods at once conflict. While it is enabled, nobody can join you and you can't join others.",
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
  // text alphas chosen for WCAG AA (4.5:1) on the panel gradient's lightest colour (rgb 45, 79, 92): text 8.4:1,
  // textDim 6.1:1, textDimmer 4.7:1; textFaint is for decorative marks (dots, borders), never for text
  text: "#f0fbff",
  textDim: "rgba(240, 251, 255, 0.8)",
  textDimmer: "rgba(240, 251, 255, 0.66)",
  textFaint: "rgba(240, 251, 255, 0.32)",
  focus: "var(--focusedColor)",
  // the game's negative red is 2.6:1 on the panel: as text it uses a lighter tint (5.0:1)
  badText: "#ffb0a5",
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
  dim: "rgba(240, 251, 255, 0.8)",
  faint: "rgba(240, 251, 255, 0.66)",
  divider: "rgba(255, 255, 255, 0.12)",
  well: "rgba(0, 0, 0, 0.25)",
  track: "rgba(0, 0, 0, 0.35)",
  accent: "#4bc3f1",
  accentDark: "#1e83aa",
  good: "#8bdb46",
  warn: "#ffa42d",
  bad: "#e95f4a",
  badText: "#ffb0a5",
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
const KEEP_PASSWORD = "keep";
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

// Keyboard reach for the built-in buttons (divs): focusable with Tab, Enter or Space presses them, and a focus ring.
// The key never reaches the window handlers (Enter there opens the chat).
function usePressable(onPress, disabled) {
  const [focus, setFocus] = React.useState(false);
  return [
    focus,
    {
      tabIndex: disabled ? -1 : 0,
      onFocus: function () { setFocus(true); },
      onBlur: function () { setFocus(false); },
      onKeyDown: function (e) {
        const k = keyOf(e);
        if (k !== "Enter" && k !== " " && k !== "Spacebar") return;
        if (e && e.stopPropagation) e.stopPropagation();
        if (e && e.preventDefault) e.preventDefault();
        if (!disabled && onPress) onPress();
      },
    },
  ];
}

// Window-level keys of an open surface (Escape closes, Enter confirms); ignored while a text field has focus (the
// field handles its own Enter and Escape). Only the newest registered surface reacts, so stacked layers close one by
// one; a modal surface (a dialog) also keeps Enter from opening the chat.
const keyStack = [];
function useWindowKeys(handlers, active, modal) {
  const ref = React.useRef(handlers);
  ref.current = handlers;
  React.useEffect(function () {
    if (!active) return undefined;
    const entry = { ref: ref, modal: !!modal };
    keyStack.push(entry);
    const onKey = function (e) {
      if (keyStack[keyStack.length - 1] !== entry || store.typing > 0 || anotherInputFocused()) return;
      const fn = ref.current && ref.current[keyOf(e)];
      if (typeof fn !== "function") return;
      if (e && e.preventDefault) e.preventDefault();
      if (e && e.stopPropagation) e.stopPropagation();
      fn();
    };
    window.addEventListener("keydown", onKey);
    return function () {
      window.removeEventListener("keydown", onKey);
      const i = keyStack.indexOf(entry);
      if (i >= 0) keyStack.splice(i, 1);
    };
  }, [active]);
}

// a dialog (the error screen) is open: Enter belongs to it, not to the chat
function modalOpen() {
  for (const entry of keyStack) if (entry.modal) return true;
  return false;
}

function anotherInputFocused() {
  try {
    const el = document.activeElement;
    return !!el && (el.tagName === "INPUT" || el.tagName === "TEXTAREA");
  } catch (e) {
    return false;
  }
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
  const [focus, kb] = usePressable(p.onSelect, p.disabled);
  const kind = p.kind;
  const look = (FB_LOOK[kind] || FB_LOOK.flat)(hover || focus, p.disabled, p.selected);
  return h(
    "div",
    merge(hv, kb, {
      style: merge(
        { display: "flex", flexDirection: "row", alignItems: "center", justifyContent: "center", flexShrink: 0, whiteSpace: "nowrap", cursor: p.disabled ? "default" : "pointer", opacity: p.disabled && kind !== "primary" ? 0.55 : 1 },
        FB_SHAPE[kind] || FB_SHAPE.flat,
        look,
        focus && !p.disabled ? { boxShadow: "0 0 0 2rem " + T.focus } : null,
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
  // invalid input: an amber border (the message under the field says what is wrong)
  const edge = p.invalid ? (look === "overlay" ? OV.warn : T.warn) : null;
  let style;
  if (menuClass) style = merge({ flex: "0 1 50%", minWidth: 0 }, edge ? border("2rem", edge) : null);
  else if (look === "menu")
    style = merge({ flex: "0 1 50%", minWidth: 0, backgroundColor: T.menuControl, color: T.menuText, borderRadius: "3rem", padding: "4rem 8rem", fontSize: "1em" }, border("2rem", edge || (focus ? T.accent : T.menuControlBorder)));
  else if (look === "overlay")
    style = merge({ width: "100%", height: "36rem", padding: "0 10rem", backgroundColor: "rgba(0, 0, 0, 0.35)", color: OV.text, borderRadius: "4rem", fontSize: "16rem" }, border("2rem", edge || (focus ? OV.accent : "rgba(255, 255, 255, 0.18)")));
  else
    style = merge(
      { width: "100%", height: "32rem", padding: "0 10rem", backgroundColor: focus ? "rgba(255, 255, 255, 0.08)" : T.field, color: T.text, borderRadius: T.radiusInner, fontSize: FS.m },
      border("2rem", edge || (focus ? T.accent : T.fieldBorder))
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
  const pct = Math.max(0, Math.min(100, Math.round(num(p.value, 0) * 100)));
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
  const v = num(s.speedValue, 0);
  if (v > 0) return t("st.ui.speed.value", { N: v });
  return str(s.speed);
}

const ROLES = { host: 1, cohost: 1, builder: 1, viewer: 1 };
const PLAYER_STATES = { Handshaking: 1, WaitingForSnapshot: 1, Downloading: 1, Loading: 1, Offline: 1 };

function roleText(role) {
  return ROLES[role] ? t("st.ui.role." + role) : "";
}

function playerStateText(p) {
  if (!PLAYER_STATES[p.state]) return "";
  return t("st.ui.players.state." + p.state, { P: Math.round(Math.max(0, Math.min(1, num(p.progress, 0))) * 100) });
}

function playerName(pl) {
  const name = str(pl.name).trim();
  return name.length > 0 ? name : t("st.ui.players.unnamed", { N: num(pl.id, 0) });
}

// What a remote player is doing. Its own small component: the presence binding changes up to 20
// times a second, and only this line re-renders then, not the whole player list with its menus.
function ActivityLine(p) {
  const tags = useJson(presenceBinding).tags;
  let activity = "";
  for (const tag of objs(tags)) if (tag.id === p.id) activity = str(tag.activity);
  if (!activity) return null;
  return h("div", { style: merge(S.ellipsis, { color: T.textDimmer, fontSize: FS.xs, marginTop: "2rem" }) }, activity);
}

// role badge: text always, the host's in the accent colour (never colour alone)
function RoleChip(p) {
  const text = roleText(p.role);
  if (!text) return null;
  return h(Chip, { color: p.role === "host" ? T.accent : p.role === "cohost" ? T.accentLight : T.textDim, style: { marginLeft: "8rem" } }, text);
}

function PlayerRow(p) {
  const pl = p.player;
  const s = p.state;
  const confirm = useConfirm();
  const confirmBan = useConfirm();
  const [menu, setMenu] = React.useState(false);
  const [hover, hv] = useHover();
  const color = str(pl.color) || T.accent;
  const live = pl.state === "Live" || !PLAYER_STATES[pl.state];
  const joining = !live;
  const hosting = s.mode === "Hosting";
  const ping = num(pl.ping, -1);
  // a co-host may remove builders and viewers and switch them (the host checks every request)
  const coHostHere = s.mode === "Client" && s.localRole === "cohost" && !pl.host && !pl.you && (pl.role === "builder" || pl.role === "viewer");
  const canManage = (hosting && !pl.host) || coHostHere;
  const actionGap = { marginLeft: "6rem", marginTop: "4rem" };
  // the host's own row has no ping, and neither has a player's own row before the first measurement
  const showPing = !pl.host && live && !(pl.you && ping <= 0) && ping >= 0;
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
          h("div", { style: merge(S.ellipsis, { flexShrink: 1, fontSize: FS.m, color: T.text, minWidth: 0 }) }, playerName(pl)),
          pl.you ? h("div", { style: { color: T.textDimmer, fontSize: FS.s, marginLeft: "6rem", flexShrink: 0 } }, "(" + t("st.ui.session.you") + ")") : null,
          h(RoleChip, { role: pl.role })
        ),
        joining
          ? h("div", { style: { marginTop: "4rem" } }, h("div", { style: { color: pl.state === "Offline" ? T.badText : T.warn, fontSize: FS.xs, marginBottom: "3rem" } }, playerStateText(pl)), pl.state === "Downloading" || pl.state === "Loading" ? h(Progress, { value: pl.progress, height: 4 }) : null)
          : pl.you ? null : h(ActivityLine, { id: pl.id })
      ),
      showPing
        ? withTooltip(h("div", { style: merge(S.row, { marginLeft: "8rem", flexShrink: 0 }) }, h(PingBars, { ping: ping }), h("div", { style: { color: T.textDimmer, fontSize: FS.xs, marginLeft: "5rem", minWidth: "42rem", textAlign: "right" } }, t("st.ui.players.ping", { N: Math.round(ping) }))), t("st.ui.players.pingHint"), "left")
        : null,
      canManage ? h(IconBtn, { src: G.more, size: 14, tooltip: t("st.ui.players.actions"), style: { marginLeft: "6rem" }, onClick: function () { setMenu(!menu); } }) : null
    ),
    canManage && menu
      ? h(
          "div",
          { style: merge(S.row, { justifyContent: "flex-end", flexWrap: "wrap", padding: "0 6rem 8rem 6rem" }) },
          !joining && pl.role !== "cohost" // viewers watch, builders build
            ? h(Btn, { kind: "flat", style: actionGap, onClick: function () { send("toggleRole", num(pl.id, -1)); } }, pl.role === "viewer" ? t("st.ui.players.makeBuilder") : t("st.ui.players.makeViewer"))
            : null,
          hosting && !joining // co-hosts help moderate
            ? h(Btn, { kind: "flat", style: actionGap, onClick: function () { send("toggleCoHost", num(pl.id, -1)); } }, pl.role === "cohost" ? t("st.ui.players.removeCoHost") : t("st.ui.players.makeCoHost"))
            : null,
          h(Btn, { kind: confirm.armed ? "danger" : "flat", style: actionGap, onClick: function () { confirm.run(function () { send("kick", num(pl.id, -1)); }); } }, confirm.armed ? t("st.ui.players.kickConfirm") : t("st.ui.players.kick")),
          hosting
            ? h(Btn, { kind: confirmBan.armed ? "danger" : "flat", style: actionGap, onClick: function () { confirmBan.run(function () { send("ban", num(pl.id, -1)); }); } }, confirmBan.armed ? t("st.ui.players.banConfirm") : t("st.ui.players.ban"))
            : null,
          confirm.armed || confirmBan.armed ? h(Hint, { style: { width: "100%", textAlign: "right", marginTop: "4rem", fontSize: FS.xs } }, t("st.ui.players.confirmHint")) : null
        )
      : null
  );
}

function Players(p) {
  const s = p.state;
  const players = objs(s.players);
  const max = num(s.maxPlayers, 0);
  return h(
    Section,
    { title: t("st.ui.players.title"), right: h("div", { style: { color: T.textDimmer, fontSize: FS.s } }, s.mode === "Hosting" && max > 0 ? t("st.ui.players.count", { N: players.length, MAX: max }) : String(players.length)) },
    players.length === 0 ? h(Hint, { style: { padding: "2rem 10rem 8rem 10rem" } }, t("st.ui.players.empty")) : null,
    players.map(function (pl, i) {
      return h(PlayerRow, { key: pl.id !== undefined ? String(pl.id) : "i" + i, player: pl, state: s });
    })
  );
}

// The session header: status, Steam ID with copy, invites.
function SessionHeader(p) {
  const s = p.state;
  const hosting = s.mode === "Hosting";
  const flash = useFlash();
  const st = useStore();
  const id = str(s.inviteSteamId) || str(s.steamId);
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
    s.hold ? h(Notice, null, str(s.status) || t("st.ui.session.holding")) : null,
    h(
      "div",
      { style: { margin: "4rem 6rem 2rem 6rem", padding: "8rem 10rem", borderRadius: T.radiusInner, backgroundColor: "rgba(0, 0, 0, 0.2)" } },
      h(Label, null, hosting ? t("st.ui.invite.steamId") : t("st.ui.invite.hostSteamId")),
      h(
        "div",
        { style: S.row },
        h("div", { style: merge(S.ellipsis, S.ids, { flex: 1, fontSize: FS.l, color: id ? T.text : T.textDimmer }) }, id || "–"),
        id ? h(IconBtn, { src: flash.on ? G.check : G.copy, size: 14, color: flash.on ? T.good : T.text, tooltip: flash.on ? t("st.ui.invite.copied") : t("st.ui.invite.copy"), onClick: function () { send("copySteamId"); flash.fire(); } }) : null
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
      hosting && num(s.directPort, 0) > 0 ? h(Hint, { style: { marginTop: "6rem", fontSize: FS.xs } }, t("st.ui.invite.direct", { PORT: num(s.directPort, 0) })) : null
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
      h("div", { style: merge(S.ellipsis, { fontSize: FS.s, color: state === "offline" ? T.textDimmer : T.text }) }, str(f.name) || str(f.id)),
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
  useWindowKeys({ Escape: close }, true);
  const q = query.trim().toLowerCase();
  const all = objs(friends)
    .filter(function (f) { return str(f.id).length > 0; })
    .filter(function (f) { return q.length === 0 || str(f.name).toLowerCase().indexOf(q) >= 0; })
    .sort(function (a, b) {
      const sa = FRIEND_ORDER[a.state] !== undefined ? FRIEND_ORDER[a.state] : 3;
      const sb = FRIEND_ORDER[b.state] !== undefined ? FRIEND_ORDER[b.state] : 3;
      if (sa !== sb) return sa - sb;
      return str(a.name).localeCompare(str(b.name));
    });
  const online = all.filter(function (f) { return f.state !== "offline" && FRIEND_ORDER[f.state] !== undefined; });
  const offline = all.filter(function (f) { return !(f.state !== "offline" && FRIEND_ORDER[f.state] !== undefined); });
  const overlay = function () { send("openInviteOverlay"); };
  const group = function (title, items) {
    if (items.length === 0) return null;
    return h("div", { key: title }, h(Label, { style: { padding: "8rem 8rem 2rem 8rem", marginBottom: 0 } }, title), items.map(function (f) { return h(FriendRow, { key: str(f.id), friend: f }); }));
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
  const apply = function () {
    if (password.length === 0) return;
    send("saveHostSettings", str(s.playerName), str(s.sessionNameSetting), password);
    setPassword("");
  };
  return h(
    Foldout,
    { title: t("st.ui.settings.title") },
    h(
      GameField,
      { label: t("st.ui.settings.livePassword") },
      h(
        "div",
        { style: S.row },
        h(Field, { value: password, onChange: setPassword, onEnter: apply, password: true, placeholder: s.hasPassword ? t("st.ui.host.passwordKept") : t("st.ui.host.passwordPlaceholder"), title: t("st.ui.settings.livePassword"), style: { flex: 1, width: "auto", marginRight: "8rem" } }),
        h(Btn, { kind: "flat", disabled: password.length === 0, onClick: apply }, t("st.ui.settings.apply"))
      ),
      s.hasPassword ? h("div", { style: { marginTop: "6rem", fontSize: FS.s } }, h(LinkText, { onClick: function () { send("saveHostSettings", str(s.playerName), str(s.sessionNameSetting), ""); } }, t("st.ui.host.removePassword"))) : null
    ),
    h(HostOptions, { state: s, live: true })
  );
}

function NetLine() {
  const n = useJson(netBinding);
  const perf = useJson(perfBinding);
  return h(
    "div",
    { style: { padding: "2rem 6rem" } },
    n.show && str(n.text) ? h("div", { style: { color: T.textDim, fontSize: FS.xs, marginBottom: "6rem", lineHeight: 1.35 } }, str(n.text)) : null,
    h(
      "div",
      { style: merge(S.row, { fontSize: FS.xs }) },
      h(LinkText, { onClick: function () { send("toggleNetHud"); } }, n.show ? t("st.ui.net.hide") : t("st.ui.net.show")),
      h("div", { style: { width: "14rem" } }),
      h(LinkText, { onClick: function () { send("togglePerfHud"); } }, perf.show ? t("st.ui.perf.hide") : t("st.ui.perf.show")),
      h("div", { style: { width: "14rem" } }),
      h(LinkText, { onClick: function () { send("writeDiagnostics"); } }, t("st.ui.net.diagnostics"))
    )
  );
}

// the startup self-test in one line (details go to the diagnostics file)
function SelfTest(p) {
  const s = p.state;
  const probes = str(s.probes);
  if (!probes) return null;
  const off = probes.indexOf("off:") >= 0;
  return h(
    "div",
    { style: merge({ fontSize: FS.xs, lineHeight: 1.35, color: off ? T.warn : p.menu ? T.menuText2 : T.textDimmer }, p.style) },
    probes + " ",
    h(LinkText, { onClick: function () { send("writeDiagnostics"); } }, t("st.ui.selftest.details"))
  );
}

function Diagnostics(p) {
  const s = p.state;
  return h(
    Foldout,
    { title: t("st.ui.diag.title") },
    str(s.sync) ? h("div", { style: { color: T.textDimmer, fontSize: FS.xs, lineHeight: 1.35, padding: "2rem 6rem 6rem 6rem" } }, str(s.sync)) : null,
    h(NetLine, null),
    h(SelfTest, { state: s, style: { padding: "6rem 6rem 2rem 6rem" } })
  );
}

// a client session ended unexpectedly in the host's city: automatic rejoin, or the player's choice
function RecoveryView(p) {
  const s = p.state;
  const r = obj(s.recovery);
  const attempt = num(r.attempt, 0);
  const max = num(r.max, 0);
  let title;
  let text;
  let buttons = [];
  const full = { width: "100%", marginTop: "6rem" };
  if (r.phase === "Waiting") {
    title = t("st.ui.recovery.lost.title");
    text = t("st.ui.recovery.lost.text", { S: Math.max(0, num(r.seconds, 0)), N: attempt + 1, MAX: max });
    buttons = [
      h(Btn, { key: "now", kind: "primary", style: full, onClick: function () { send("reconnect"); } }, t("st.ui.recovery.reconnectNow")),
      h(Btn, { key: "menu", style: full, onClick: function () { send("mainMenu"); } }, t("st.ui.recovery.mainMenu")),
      h(Btn, { key: "stay", style: full, onClick: function () { send("stayOffline"); } }, t("st.ui.recovery.stayOffline")),
    ];
  } else if (r.phase === "Attempting") {
    title = t("st.ui.recovery.attempt.title", { N: attempt || 1, MAX: max });
    text = STAGES.indexOf(r.attemptStage) >= 0 ? t("st.ui.join.stage." + r.attemptStage) : "";
    buttons = [h(Btn, { key: "cancel", style: full, onClick: function () { send("cancelReconnect"); } }, t("st.ui.recovery.cancel"))];
  } else {
    // removed (or banned) after loading the city says so; it used to read "the session has ended"
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
      str(r.reason) ? h(Hint, { style: { padding: "0 6rem 6rem 6rem" } }, str(r.reason)) : null,
      text ? h(Notice, { color: r.phase === "NeedsChoice" ? T.bad : T.warn }, text) : null,
      r.phase === "Attempting" ? h(Progress, { value: s.progress, style: { margin: "6rem" } }) : null,
      h("div", { style: { padding: "0 6rem 6rem 6rem" } }, buttons)
    ),
    objs(s.players).length > 0 ? h(Players, { state: s }) : null,
    str(s.probes) ? h(Section, null, h(SelfTest, { state: s, style: { padding: "2rem 6rem" } })) : null
  );
}

// the integrity check found more divergence than object repairs fix: offer a rejoin with a fresh snapshot
function ResyncOffer(p) {
  const s = p.state;
  const detail = str(s.resync);
  if (!detail) return null;
  return h(
    Section,
    null,
    h(
      Notice,
      { color: T.warn },
      h("div", { style: { fontWeight: "bold", marginBottom: "3rem" } }, t("st.ui.resync.title")),
      t("st.ui.resync.text"),
      h("div", { style: { marginTop: "4rem", color: T.textDimmer, fontSize: FS.xs } }, detail)
    ),
    h(
      "div",
      { style: merge(S.row, { padding: "2rem 6rem 6rem 6rem" }) },
      h(Btn, { kind: "primary", style: { flex: 1, marginRight: "6rem" }, onClick: function () { send("resync"); } }, t("st.ui.resync.now")),
      h(Btn, { kind: "flat", style: { flex: 1 }, onClick: function () { send("dismissResync"); } }, t("st.ui.resync.later"))
    )
  );
}

// the old multiplayer mod is loaded in this game: nobody can join and this game cannot join others (stays while it is)
function ConflictWarning(p) {
  const mod = str(p.state.conflictMod);
  if (!mod) return null;
  return h(Notice, { color: T.bad, style: { margin: "8rem" } }, t("st.ui.conflictMod", { MOD: mod }));
}

function SessionView(p) {
  const s = p.state;
  const hosting = s.mode === "Hosting";
  return h(
    "div",
    null,
    h(SessionHeader, { state: s }),
    h(ConflictWarning, { state: s }),
    !hosting ? h(ResyncOffer, { state: s }) : null,
    hosting ? h(RecordControls, null) : h(ReplayBanner, null),
    h(Players, { state: s }),
    hosting ? h(LiveSettings, { state: s }) : null,
    h(Diagnostics, { state: s })
  );
}

// the build id, exactly as the installer prints it (players compare it when a join is refused)
function BuildLine(p) {
  const s = p.state;
  if (!str(s.build)) return null;
  return h("div", { style: merge({ color: p.menu ? T.menuText2 : T.textDimmer, fontSize: FS.xs, marginTop: "6rem", textAlign: p.menu ? "left" : "center" }, p.style) }, t("st.ui.footer.build", { BUILD: str(s.build) }));
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
  const v = obj(p.verdict);
  if (!str(v.status)) return null;
  const kind = v.status === "passed" || v.status === "failed" ? v.status : "incomplete";
  const color = verdictColor(v.status);
  const textColor = p.menu ? T.menuText : T.text;
  return h(
    "div",
    { style: merge({ padding: "10rem 12rem", margin: p.menu ? "0 0 12rem 0" : "4rem 6rem", borderRadius: T.radiusInner, backgroundColor: "rgba(0, 0, 0, 0.22)" }, border("3rem", color, ["Left"])) },
    h("div", { style: merge(S.row, { marginBottom: "4rem" }) }, h(Glyph, { src: kind === "passed" ? G.check : G.warning, size: 14, color: color, style: { marginRight: "8rem" } }), h("div", { style: { flex: 1, fontSize: p.menu ? FS.l : FS.m, color: textColor, fontWeight: "bold" } }, t("st.ui.test.verdict." + kind))),
    h("div", { style: { color: p.menu ? T.menuText2 : T.textDim, fontSize: FS.s, lineHeight: 1.35 } }, str(v.summary)),
    str(v.folder)
      ? h(
          "div",
          { style: { marginTop: "6rem", fontSize: FS.xs, color: p.menu ? T.menuText2 : T.textDimmer, lineHeight: 1.35 } },
          t("st.ui.test.verdictFile", { FOLDER: str(v.folder) }) + " ",
          h(LinkText, { onClick: function () { send("copyText", str(v.folder)); } }, t("st.ui.test.copyPath"))
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
    // the verdict as a glyph (check or warning) in its colour, so it does not rely on colour alone
    r.verdict
      ? h(Glyph, { src: r.verdict === "passed" ? G.check : G.warning, size: 12, color: sel && menu ? T.menuInverted : verdictColor(r.verdict), style: { marginRight: "10rem" } })
      : h(Dot, { color: sel && menu ? T.menuInverted : T.textFaint, size: 8, gap: 14 }),
    h(
      "div",
      { style: { flex: 1, minWidth: 0 } },
      h("div", { style: merge(S.ellipsis, { fontSize: menu ? FS.m : FS.s }) }, str(r.city) || str(r.id)),
      h("div", { style: merge(S.ellipsis, { fontSize: FS.xs, opacity: 0.8 }) }, [str(r.when), str(r.duration), (str(r.mb) || "0") + " MB"].filter(function (x) { return x.length > 0; }).join(" · "))
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
  const recs = objs(r.recordings).filter(function (x) { return str(x.id).length > 0; });
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
      str(r.root) ? h("div", { style: { padding: "4rem 6rem 6rem 6rem", fontSize: FS.xs, color: T.textDimmer, lineHeight: 1.35 } }, t("st.ui.test.folder", { FOLDER: str(r.root) })) : null
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
        h("div", { style: { flex: 1, color: T.text, fontSize: FS.s } }, str(r.recordStatus))
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
    str(r.replayStatus) ? h(Hint, { style: { padding: "4rem 6rem" } }, str(r.replayStatus)) : null,
    h(Progress, { value: r.progress, style: { margin: "6rem" } }),
    h("div", { style: { padding: "0 6rem 4rem 6rem" } }, h(Btn, { kind: "flat", onClick: function () { send("replayStop"); }, style: { width: "100%" } }, t("st.ui.test.stopReplay")))
  );
}

// ------------------------------------------------------------------------------------------ in-game lobby

function SteamMissing(p) {
  return h(Notice, { color: T.bad, style: p && p.style }, h("div", { style: { fontWeight: "bold", marginBottom: "3rem" } }, t("st.ui.steam.missing.title")), t("st.ui.steam.missing.text"));
}

// host options shared by the lobby, the live session settings and the main menu (saved on every change)
// the direct-connection port as typed: a whole number from 1024 to 65535, or 0
function portOf(text) {
  const t0 = str(text).trim();
  if (!/^[0-9]{4,5}$/.test(t0)) return 0;
  const n = parseInt(t0, 10);
  return n >= 1024 && n <= 65535 ? n : 0;
}

function useHostOptions(s) {
  const [maxPlayers, setMaxPlayers] = React.useState(Math.max(2, Math.min(16, Math.round(num(s.maxPlayers, 8)))));
  const [allowDirect, setAllowDirect] = React.useState(!!s.allowDirect);
  const [requireMods, setRequireMods] = React.useState(s.requireSameMods !== false);
  const [requireAssets, setRequireAssets] = React.useState(s.requireSameAssets === true);
  const [allowOverride, setAllowOverride] = React.useState(s.allowModOverride !== false);
  const [port, setPortText] = React.useState(String(Math.round(num(s.portSetting, 27015))));
  const save = function (next) {
    const v = merge({ maxPlayers: maxPlayers, allowDirect: allowDirect, requireMods: requireMods, port: port }, next);
    // an invalid port is sent as 0, which keeps the saved one (the field says what is wrong)
    send("saveHostOptions", v.maxPlayers, !!v.allowDirect, !!v.requireMods, portOf(v.port));
  };
  return {
    maxPlayers: maxPlayers, allowDirect: allowDirect, requireMods: requireMods, requireAssets: requireAssets, allowOverride: allowOverride, port: port, save: save,
    // "join anyway" only skips what is required: nothing to allow while neither mods nor asset packs are
    overrideApplies: requireMods || requireAssets,
    portInvalid: portOf(port) === 0,
    // digits only (a pasted "27015 " or "port 27015" keeps its number)
    setPort: function (v) { setPortText(str(v).replace(/[^0-9]/g, "").slice(0, 5)); },
    setMax: function (v) { setMaxPlayers(v); save({ maxPlayers: v }); },
    setDirect: function (v) { setAllowDirect(v); save({ allowDirect: v }); },
    setMods: function (v) { setRequireMods(v); save({ requireMods: v }); },
    setAssets: function (v) { setRequireAssets(!!v); send("setRequireSameAssets", !!v); },
    setOverride: function (v) { setAllowOverride(!!v); send("setAllowJoinWithDifferentMods", !!v); },
  };
}

// the host's ban list with an unban button per entry (stored with the host's settings)
function BanList(p) {
  const bans = objs(p.state.bans).filter(function (b) { return str(b.key).length > 0; });
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
        { key: str(b.key), style: merge(S.row, { padding: "4rem 0", minHeight: "32rem" }, border("1rem", menu ? T.menuBorder : T.divider, ["Top"])) },
        h(
          "div",
          { style: { flex: 1, minWidth: 0 } },
          h("div", { style: merge(S.ellipsis, { color: menu ? T.menuText : T.text, fontSize: menu ? FS.m : FS.s }) }, str(b.name) || str(b.id) || str(b.key)),
          h("div", { style: merge(S.ellipsis, { color: menu ? T.menuText2 : T.textDimmer, fontSize: FS.xs }) }, b.kind === "ip" ? t("st.ui.host.banAddress", { ADDRESS: str(b.id) }) : str(b.id))
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
  const [newViewer, setNewViewer] = React.useState(!!s.newPlayersViewer);
  // direct connections need a password (Steam does not check those players)
  const directOk = !!s.hasPassword;
  return h(
    "div",
    null,
    h(GameRow, { label: t("st.ui.host.maxPlayers"), hint: t("st.ui.host.maxPlayersHint") }, h(Stepper, { value: o.maxPlayers, min: 2, max: 16, onChange: o.setMax })),
    h(GameRow, { label: t("st.ui.host.requireMods"), hint: t("st.ui.host.requireModsHint") }, h(Check, { checked: o.requireMods, onChange: o.setMods })),
    h(GameRow, { label: t("st.ui.host.requireAssets"), hint: t("st.ui.host.requireAssetsHint") }, h(Check, { checked: o.requireAssets, onChange: o.setAssets })),
    h(GameRow, { label: t("st.ui.host.allowModOverride"), hint: t("st.ui.host.allowModOverrideHint") }, h(Check, { checked: o.allowOverride, disabled: !o.overrideApplies, onChange: o.setOverride })),
    h(GameRow, { label: t("st.ui.host.friendsOnly"), hint: t("st.ui.host.friendsOnlyHint") }, h(Check, { checked: friendsOnly, onChange: function (v) { setFriendsOnly(v); send("setFriendsOnly", !!v); } })),
    h(GameRow, { label: t("st.ui.host.newPlayersViewer"), hint: t("st.ui.host.newPlayersViewerHint") }, h(Check, { checked: newViewer, onChange: function (v) { setNewViewer(v); send("setNewPlayersViewer", !!v); } })),
    h(
      GameRow,
      { label: t("st.ui.host.allowDirect"), hint: directOk ? t("st.ui.host.allowDirectHint") + (p.live ? " " + t("st.ui.host.allowDirectNextTime") : "") : t("st.ui.host.allowDirectNeedsPassword") },
      h(Check, { checked: o.allowDirect && directOk, disabled: !directOk, onChange: o.setDirect })
    ),
    o.allowDirect && directOk
      ? h(
          GameRow,
          { label: t("st.ui.host.port"), hint: o.portInvalid ? null : t("st.ui.host.portHint") },
          h(Field, { value: o.port, onChange: o.setPort, onBlur: function () { o.save({}); }, onEnter: function () { o.save({}); }, invalid: o.portInvalid, ids: true, max: 5, style: { width: "100rem", textAlign: "center" }, title: t("st.ui.host.port"), description: t("st.ui.host.portHint") })
        )
      : null,
    o.allowDirect && directOk && o.portInvalid ? h(FieldError, { style: { padding: "0 6rem 6rem 6rem" } }, t("st.ui.host.portInvalid")) : null,
    h(BanList, { state: s })
  );
}

// why the Host button is disabled, or "" (Steam and blocked reasons have their own notice above)
function hostReason(s) {
  if (s.canHost || s.steam === false || str(s.blocked)) return "";
  return s.inGame === false ? t("st.ui.host.needCity") : t("st.ui.host.unavailable");
}

// why the Join button is disabled although the address is fine, or ""
function joinReason(s) {
  if (s.canJoin || s.steam === false || str(s.blocked)) return "";
  return t("st.ui.join.unavailable");
}

// The host's Steam ID or address as typed or pasted. Spaces inside a Steam ID go, and a pasted Steam profile link (or
// any text around a Steam ID, like "steam id: 7656…") gives the bare ID.
function normalizeTarget(raw) {
  const text = str(raw);
  const compact = text.replace(/\s+/g, "");
  if (/^[0-9]+$/.test(compact)) return compact;
  const id = text.match(/(?:^|[^0-9])(7656119[0-9]{10})(?![0-9])/);
  if (id && !/^[0-9.]+:[0-9]+$/.test(text.trim())) return id[1];
  return text.replace(/^\s+/, "");
}

// what is wrong with a join target ("" when it looks fine or is empty), the same rules as the mod's own parser:
// a Steam ID (15 or more digits), host:port, [IPv6]:port or a plain host name or address
function targetProblem(raw) {
  const t0 = str(raw).trim();
  if (t0.length === 0) return "";
  if (/^[0-9]+$/.test(t0)) return t0.length >= 15 && t0.length <= 20 ? "" : t("st.ui.join.badSteamId");
  const v6 = t0.match(/^\[([0-9a-fA-F:.]+)\](?::([0-9]{1,5}))?$/);
  if (v6) return !v6[2] || (+v6[2] >= 1 && +v6[2] <= 65535) ? "" : t("st.ui.join.badPort");
  if (/^[0-9a-fA-F:]+$/.test(t0) && t0.split(":").length > 2) return ""; // a bare IPv6 address
  const m = t0.match(/^([A-Za-z0-9.-]+)(?::([0-9]*))?$/);
  if (!m || m[1].length === 0) return t("st.ui.join.badTarget");
  if (m[2] !== undefined && !(m[2].length > 0 && +m[2] >= 1 && +m[2] <= 65535)) return t("st.ui.join.badPort");
  return "";
}

// an inline validation message under a field (warning glyph and text: not colour alone)
function FieldError(p) {
  const menu = !!p.menu;
  return h(
    "div",
    { style: merge(S.row, { alignItems: "flex-start", fontSize: menu ? FS.m : FS.xs, color: T.warn, lineHeight: 1.35 }, p.style) },
    h(Glyph, { src: G.warning, size: menu ? 14 : 12, color: T.warn, style: { marginRight: "6rem", marginTop: "1rem" } }),
    h("div", { style: { flex: 1, minWidth: 0 } }, p.children)
  );
}

function GameHostTab(p) {
  const s = p.state;
  const [session, setSession] = React.useState(str(s.sessionNameSetting));
  const [password, setPassword] = React.useState("");
  // an untouched (empty) password field keeps the stored host password instead of wiping it
  const save = function (pw) {
    send("saveHostSettings", p.name, session, pw !== undefined ? pw : password.length > 0 ? password : str(s.keepPassword) || KEEP_PASSWORD);
  };
  const reason = hostReason(s);
  return h(
    "div",
    null,
    h(
      Section,
      { first: true },
      h(Hint, { style: { padding: "4rem 6rem 4rem 6rem" } }, t("st.ui.host.intro")),
      p.nameField,
      h(GameField, { label: t("st.ui.host.sessionName"), hint: t("st.ui.host.sessionNameHint") }, h(Field, { value: session, onChange: setSession, onBlur: function () { save(); }, placeholder: t("st.ui.host.sessionNamePlaceholder", { NAME: p.name || str(s.playerName) || "…" }), title: t("st.ui.host.sessionName"), max: 64 })),
      h(
        GameField,
        { label: t("st.ui.host.password"), hint: s.hasPassword ? null : t("st.ui.host.passwordHint") },
        h(Field, { value: password, onChange: setPassword, onBlur: function () { save(); }, password: true, placeholder: s.hasPassword ? t("st.ui.host.passwordKept") : t("st.ui.host.passwordPlaceholder"), title: t("st.ui.host.password"), max: 64 }),
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
  const [target, setTarget] = React.useState(str(s.joinTarget));
  const [password, setPassword] = React.useState("");
  const problem = targetProblem(target);
  const canJoin = !!s.canJoin && target.trim().length > 0 && !problem;
  const reason = target.trim().length > 0 && !problem ? joinReason(s) : "";
  const join = function () {
    if (!canJoin) return;
    send("saveHostSettings", p.name, str(s.sessionNameSetting), str(s.keepPassword) || KEEP_PASSWORD);
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
      h(
        GameField,
        { label: t("st.ui.join.target"), hint: problem ? null : t("st.ui.join.findId") },
        h(Field, { value: target, onChange: function (v) { setTarget(normalizeTarget(v)); }, onEnter: join, invalid: !!problem, ids: true, placeholder: t("st.ui.join.targetPlaceholder"), title: t("st.ui.join.target"), description: t("st.ui.join.intro"), max: 96 }),
        problem ? h(FieldError, { style: { marginTop: "4rem" } }, problem) : null
      ),
      h(GameField, { label: t("st.ui.join.password"), hint: t("st.ui.join.passwordHint") }, h(Field, { value: password, onChange: setPassword, onEnter: join, password: true, placeholder: t("st.ui.join.passwordPlaceholder"), title: t("st.ui.join.password"), max: 64 }))
    ),
    h(
      Section,
      null,
      h(Notice, null, t("st.ui.join.replaceWarning")),
      reason ? h(Hint, { style: { padding: "2rem 6rem 2rem 6rem" } }, reason) : null,
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
  const [name, setName] = React.useState(str(s.playerName));
  // its own page memory: in a city hosting comes first, in the main menu joining does
  const tab = st.gameTab === "join" || st.gameTab === "solo" ? st.gameTab : "host";
  const saveName = function () {
    send("saveHostSettings", name, str(s.sessionNameSetting), str(s.keepPassword) || KEEP_PASSWORD);
  };
  const nameField = h(GameField, { label: t("st.ui.profile.name") }, h(Field, { value: name, onChange: setName, onBlur: saveName, placeholder: t("st.ui.profile.namePlaceholder"), title: t("st.ui.profile.name"), max: 64 }));
  return h(
    "div",
    null,
    h(Tabs, { value: tab, onChange: function (id) { setStore({ gameTab: id }); }, tabs: [{ id: "host", label: t("st.ui.tab.host") }, { id: "join", label: t("st.ui.tab.join") }, { id: "solo", label: t("st.ui.tab.solo") }] }),
    s.steam === false ? h(SteamMissing, { style: { margin: "8rem" } }) : null,
    str(s.blocked) ? h(Notice, { color: T.bad, style: { margin: "8rem" } }, t("st.ui.blocked", { REASON: str(s.blocked) })) : null,
    h(ConflictWarning, { state: s }),
    tab === "host" ? h(GameHostTab, { state: s, name: name, nameField: nameField }) : tab === "join" ? h(GameJoinTab, { state: s, name: name, nameField: nameField }) : h(SoloTestSection, { inGame: true }),
    str(s.probes) ? h(Section, null, h(SelfTest, { state: s, style: { padding: "2rem 6rem" } })) : null
  );
}

// Before the first state value arrives (or when C# sent only why it cannot work) there is nothing to edit yet: the
// forms would start empty and saving them would overwrite the stored settings.
function stateReady(s) {
  return typeof s.mode === "string" && s.mode.length > 0;
}

function NotReady(p) {
  const s = p.state;
  const blocked = str(s.blocked);
  if (p.menu) return blocked ? h(MenuNotice, { color: T.bad }, t("st.ui.blocked", { REASON: blocked })) : h(MenuText, { style: { color: T.menuText2 } }, t("st.ui.loading"));
  return blocked ? h(Notice, { color: T.bad, style: { margin: "8rem" } }, t("st.ui.blocked", { REASON: blocked })) : h(Hint, { style: { padding: "12rem" } }, t("st.ui.loading"));
}

function PanelContent(p) {
  const s = p.state;
  if (!stateReady(s)) return h(NotReady, { state: s });
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
        h(Hint, { style: { padding: "4rem 6rem 8rem 6rem" } }, str(s.status) || t("st.ui.join.overlayTitleUnknown")),
        h(Progress, { value: s.progress, style: { margin: "0 6rem 10rem 6rem" } }),
        h("div", { style: { padding: "0 6rem 4rem 6rem" } }, h(Btn, { kind: "flat", onClick: function () { send("leave"); }, style: { width: "100%" } }, t("st.ui.join.cancel")))
      )
    );
  }
  return h(GameLobby, { state: s });
}

function panelTitle(s) {
  const mode = s.mode || "None";
  return (mode === "Hosting" || mode === "Client") && str(s.sessionName) ? str(s.sessionName) : t("st.ui.title");
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
  const [name, setName] = React.useState(str(s.playerName));
  const [target, setTarget] = React.useState(str(s.joinTarget));
  const [password, setPassword] = React.useState("");
  const [hoverInfo, setHoverInfo] = React.useState(null);
  const problem = targetProblem(target);
  const canJoin = !!s.canJoin && s.steam !== false && target.trim().length > 0 && !problem;
  const reason = target.trim().length > 0 && !problem ? joinReason(s) : "";
  const saveName = function () { send("saveHostSettings", name, str(s.sessionNameSetting), str(s.keepPassword) || KEEP_PASSWORD); };
  const join = function () {
    if (!canJoin) return;
    send("saveHostSettings", name, str(s.sessionNameSetting), str(s.keepPassword) || KEEP_PASSWORD);
    send("join", target.trim(), password);
  };
  const info = hoverInfo || infoBlock(t("st.ui.join.title"), t("st.ui.join.info"), h("div", { style: { marginTop: "16rem", color: T.menuText2, lineHeight: 1.4 } }, t("st.ui.join.steamHint")));
  return h(
    MenuPage,
    {
      title: t("st.ui.join.title"),
      subtitle: t("st.ui.join.subtitle"),
      info: info,
      footer: [
        h("div", { key: "why", style: { flex: 1, color: T.menuText2, fontSize: FS.m, marginRight: "16rem" } }, reason),
        h(Btn, { key: "join", kind: "menuPrimary", disabled: !canJoin, onClick: join, style: { minWidth: "220rem" } }, t("st.ui.join.start")),
      ],
    },
    s.steam === false ? h(MenuNotice, { color: T.bad }, h("div", { style: { fontWeight: "bold" } }, t("st.ui.steam.missing.title")), t("st.ui.steam.missing.text")) : null,
    h(MenuText, { style: { color: T.menuText2, fontSize: FS.m } }, t("st.ui.join.intro")),
    h(MenuRow, { label: t("st.ui.profile.name"), info: t("st.ui.profile.nameHint"), onInfo: setHoverInfo }, h(Field, { look: "menu", value: name, onChange: setName, onBlur: saveName, placeholder: t("st.ui.profile.namePlaceholder"), title: t("st.ui.profile.name"), max: 64 })),
    h(MenuRow, { label: t("st.ui.join.targetShort"), info: t("st.ui.join.intro") + " " + t("st.ui.join.findId"), onInfo: setHoverInfo }, h(Field, { look: "menu", value: target, onChange: function (v) { setTarget(normalizeTarget(v)); }, onEnter: join, invalid: !!problem, ids: true, placeholder: t("st.ui.join.targetPlaceholder"), title: t("st.ui.join.target"), description: t("st.ui.join.intro"), max: 96 })),
    problem ? h(FieldError, { menu: true, style: { padding: "0 16rem 8rem 16rem" } }, problem) : null,
    h(MenuRow, { label: t("st.ui.join.password"), info: t("st.ui.join.passwordHint"), onInfo: setHoverInfo }, h(Field, { look: "menu", value: password, onChange: setPassword, onEnter: join, password: true, placeholder: t("st.ui.join.passwordPlaceholder"), title: t("st.ui.join.password"), max: 64 })),
    h(MenuNotice, { icon: G.info, color: T.accent }, t("st.ui.join.replaceWarning"))
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
  const [name, setName] = React.useState(str(s.playerName));
  const [session, setSession] = React.useState(str(s.sessionNameSetting));
  const [password, setPassword] = React.useState("");
  const [hoverInfo, setHoverInfo] = React.useState(null);
  const [friendsOnly, setFriendsOnly] = React.useState(!!s.friendsOnly);
  const [newViewer, setNewViewer] = React.useState(!!s.newPlayersViewer);
  const directOk = !!s.hasPassword; // direct connections need a password
  const o = useHostOptions(s);
  // an untouched (empty) password field keeps the stored host password instead of wiping it
  const save = function (pw) {
    send("saveHostSettings", name, session, pw !== undefined ? pw : password.length > 0 ? password : str(s.keepPassword) || KEEP_PASSWORD);
  };
  const info = hoverInfo || infoBlock(t("st.ui.host.title"), t("st.ui.host.info"), h("div", { style: { marginTop: "16rem", color: T.menuText2, lineHeight: 1.4 } }, t("st.ui.host.settingsNote")));
  return h(
    MenuPage,
    {
      title: t("st.ui.host.title"),
      subtitle: t("st.ui.host.subtitle"),
      info: info,
      footer: [
        h("div", { key: "why", style: { flex: 1, color: T.menuText2, fontSize: FS.m, marginRight: "16rem" } }, !inGame ? t("st.ui.host.needCity") : hostReason(s)),
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
    h(MenuRow, { label: t("st.ui.host.sessionName"), info: t("st.ui.host.sessionNameHint"), onInfo: setHoverInfo }, h(Field, { look: "menu", value: session, onChange: setSession, onBlur: function () { save(); }, placeholder: t("st.ui.host.sessionNamePlaceholder", { NAME: name || str(s.playerName) || "…" }), title: t("st.ui.host.sessionName"), max: 64 })),
    h(MenuRow, { label: t("st.ui.host.password"), info: t("st.ui.host.passwordHint"), onInfo: setHoverInfo }, h(Field, { look: "menu", value: password, onChange: setPassword, onBlur: function () { save(); }, password: true, placeholder: s.hasPassword ? t("st.ui.host.passwordKept") : t("st.ui.host.passwordPlaceholder"), title: t("st.ui.host.password"), max: 64 })),
    s.hasPassword ? h("div", { style: { padding: "0 16rem 8rem 16rem", textAlign: "right", fontSize: FS.m } }, h(LinkText, { onClick: function () { setPassword(""); save(""); } }, t("st.ui.host.removePassword"))) : null,
    h(MenuRow, { label: t("st.ui.host.maxPlayers"), info: t("st.ui.host.maxPlayersHint"), onInfo: setHoverInfo }, h(Stepper, { menu: true, value: o.maxPlayers, min: 2, max: 16, onChange: o.setMax })),
    h(MenuRow, { label: t("st.ui.host.requireMods"), info: t("st.ui.host.requireModsHint"), onInfo: setHoverInfo, onClick: function () { o.setMods(!o.requireMods); } }, h(Check, { menu: true, checked: o.requireMods, onChange: o.setMods })),
    h(MenuRow, { label: t("st.ui.host.requireAssets"), info: t("st.ui.host.requireAssetsHint"), onInfo: setHoverInfo, onClick: function () { o.setAssets(!o.requireAssets); } }, h(Check, { menu: true, checked: o.requireAssets, onChange: o.setAssets })),
    h(
      MenuRow,
      { label: t("st.ui.host.allowModOverride"), info: t("st.ui.host.allowModOverrideHint"), onInfo: setHoverInfo, onClick: o.overrideApplies ? function () { o.setOverride(!o.allowOverride); } : undefined },
      h(Check, { menu: true, checked: o.allowOverride, disabled: !o.overrideApplies, onChange: o.setOverride })
    ),
    h(MenuRow, { label: t("st.ui.host.friendsOnly"), info: t("st.ui.host.friendsOnlyHint"), onInfo: setHoverInfo, onClick: function () { setFriendsOnly(!friendsOnly); send("setFriendsOnly", !friendsOnly); } },
      h(Check, { menu: true, checked: friendsOnly, onChange: function (v) { setFriendsOnly(v); send("setFriendsOnly", !!v); } })),
    h(MenuRow, { label: t("st.ui.host.newPlayersViewer"), info: t("st.ui.host.newPlayersViewerHint"), onInfo: setHoverInfo, onClick: function () { setNewViewer(!newViewer); send("setNewPlayersViewer", !newViewer); } },
      h(Check, { menu: true, checked: newViewer, onChange: function (v) { setNewViewer(v); send("setNewPlayersViewer", !!v); } })),
    h(
      MenuRow,
      { label: t("st.ui.host.allowDirect"), info: directOk ? t("st.ui.host.allowDirectHint") : t("st.ui.host.allowDirectNeedsPassword"), onInfo: setHoverInfo, onClick: directOk ? function () { o.setDirect(!o.allowDirect); } : undefined },
      h(Check, { menu: true, checked: o.allowDirect && directOk, disabled: !directOk, onChange: o.setDirect })
    ),
    !directOk ? h("div", { style: { padding: "0 16rem 8rem 16rem", color: T.menuText2, fontSize: FS.m } }, t("st.ui.host.allowDirectNeedsPassword")) : null,
    o.allowDirect && directOk
      ? h(MenuRow, { label: t("st.ui.host.port"), info: t("st.ui.host.portHint"), onInfo: setHoverInfo }, h(Field, { look: "menu", value: o.port, onChange: o.setPort, onBlur: function () { o.save({}); }, onEnter: function () { o.save({}); }, invalid: o.portInvalid, ids: true, max: 5, title: t("st.ui.host.port"), description: t("st.ui.host.portHint"), style: { flex: "0 1 140rem", textAlign: "center" } }))
      : null,
    o.allowDirect && directOk && o.portInvalid ? h(FieldError, { menu: true, style: { padding: "0 16rem 8rem 16rem" } }, t("st.ui.host.portInvalid")) : null,
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
      str(r.root) ? h("div", { style: { marginTop: "12rem", fontSize: FS.s } }, t("st.ui.test.folder", { FOLDER: str(r.root) })) : null
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
    { title: panelTitle(s), subtitle: s.mode === "Hosting" ? t("st.ui.session.hosting") : s.mode === "Recovering" ? t("st.ui.recovery.badge") : t("st.ui.session.connected"), footer: h(Btn, { kind: "menuSecondary", onClick: function () { send("leave"); } }, s.mode === "Hosting" ? t("st.ui.leave.host") : t("st.ui.leave.client")) },
    h(MenuText, null, t("st.ui.session.menuNote"))
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
      str(s.blocked) ? h("div", { style: { color: T.badText, fontSize: FS.xs, lineHeight: 1.35, marginBottom: "6rem" } }, t("st.ui.blocked", { REASON: str(s.blocked) })) : null,
      str(s.conflictMod) ? h("div", { style: { color: T.badText, fontSize: FS.xs, lineHeight: 1.35, marginBottom: "6rem" } }, t("st.ui.conflictMod", { MOD: str(s.conflictMod) })) : null,
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
  if (!stateReady(s)) body = h(MenuPage, { title: t("st.ui.menu.multiplayer") }, h(NotReady, { state: s, menu: true }));
  else if (mode === "Hosting" || mode === "Client" || mode === "Recovering") body = h(MenuSessionPage, { state: s });
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
  const press = function () {
    if (!disabled && p.onClick) p.onClick();
  };
  const [focus, kb] = usePressable(press, disabled);
  const lit = (hover || focus) && !disabled;
  return h(
    "div",
    merge(hv, kb, {
      style: merge(
        { display: "flex", alignItems: "center", justifyContent: "center", height: "40rem", padding: "0 20rem", borderRadius: "5rem", fontSize: "16rem", textTransform: "uppercase", whiteSpace: "nowrap", cursor: disabled ? "default" : "pointer", color: "#ffffff", opacity: disabled ? 0.5 : 1 },
        primary ? merge({ backgroundImage: lit ? OV.greenHover : OV.green }, border("2rem", OV.greenBorder)) : merge({ backgroundColor: lit ? OV.buttonHover : OV.button }, border("2rem", OV.buttonBorder)),
        focus && !disabled ? { boxShadow: "0 0 0 2rem " + OV.accent } : null,
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
    const total = num(j.total, 0);
    const done = Math.max(0, Math.min(total, num(j.done, 0)));
    const bps = num(j.bps, 0);
    const eta = num(j.eta, -1);
    progress = total > 0 ? done / total : 0;
    // size always; speed and time left only once they are known (no "0.0 MB/s · – left" at the start)
    const parts = [t("st.ui.join.hint.downloading", { DONE: mb(done), TOTAL: mb(total) })];
    if (bps > 0) parts.push(t("st.ui.join.hint.speed", { SPEED: mb(bps) }));
    if (eta >= 0 && bps > 0) parts.push(t("st.ui.join.hint.eta", { ETA: duration(eta) }));
    hint = total > 0 ? parts.join(" · ") : t("st.ui.join.hint.downloadingStart");
  } else if (stage === "loading") {
    progress = num(j.load, 0);
  }
  const overall = Math.max(0, Math.min(1, num(j.progress, num(s.progress, 0))));
  const session = str(j.session);
  const target = str(j.target);
  const title = session ? t("st.ui.join.overlayTitle", { SESSION: session }) : t("st.ui.join.overlayTitleUnknown");
  return h(
    OverlayCard,
    { title: title, z: 2000 },
    target ? h("div", { style: merge(S.ids, S.ellipsis, { marginBottom: "10rem", fontSize: "14rem", color: OV.dim }) }, t("st.ui.join.via", { TARGET: target })) : null,
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
      h("div", { style: { minWidth: "56rem", textAlign: "right", fontSize: "14rem", color: OV.dim } }, t("st.ui.join.percent", { P: Math.round(overall * 100) }))
    ),
    h(
      "div",
      { style: merge(S.row, { marginTop: "18rem" }) },
      h("div", { style: { flex: 1, fontSize: "13rem", color: OV.faint, lineHeight: 1.35, marginRight: "12rem" } }, t("st.ui.join.patience")),
      h(OvButton, { onClick: function () { send("leave"); } }, t("st.ui.join.cancel"))
    )
  );
}

// --------------------------------------------------------------------------------------------- error screen

// the copy (title, text) for an end of a join or session; null for an end the player chose (left)
function endCopy(e) {
  const r = e.kind === "refused" ? e.refuse : e.kind;
  if (e.kind === "left") return null;
  if (e.kind === "passwordRequired") return "passwordRequired";
  if (e.kind === "refused") {
    // a lockout (the host says when to try again) is not a wrong password: waiting is what helps
    if (r === "BadPassword") return e.lockout === true ? "lockout" : "badPassword";
    if (r === "Mismatch") {
      if (e.reportKind === "game") return "game";
      if (e.reportKind === "build" || e.reportKind === "protocol") return "build";
      // DLC the joiner lacks, asset packs when the host requires the same (the lists are the report's)
      if (e.reportKind === "content") return "content";
      if (e.reportKind === "assets") return "assets";
      // the old multiplayer mod on one side, mods with city data the player lacks: never "join anyway"
      if (e.reportKind === "conflict") return str(e.conflictYou) ? "conflictYou" : "conflictHost";
      if (objs(e.required).length > 0) return "modsRequired";
      return "mods";
    }
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
  if (e.kind === "loadFailed") return "loadFailed";
  if (e.kind === "protocolError") return "build";
  return "failed";
}

// the ends a new attempt cannot fix: the player has to change something first (or cannot join at all)
const NO_RETRY = { kicked: 1, banned: 1, notAllowed: 1, ended: 1, mods: 1, modsRequired: 1, conflictYou: 1, conflictHost: 1, game: 1, build: 1, content: 1, assets: 1 };

// the list titles of the screens with lists: code mods, DLC and content packs, asset packs
const END_LISTS = {
  mods: { missing: "st.ui.end.mods.missing", extra: "st.ui.end.mods.extra", versions: "st.ui.end.mods.versions" },
  modsRequired: { missing: "st.ui.end.mods.missing", extra: "st.ui.end.mods.extra", versions: "st.ui.end.mods.versions" },
  content: { missing: "st.ui.end.content.missing", extra: "st.ui.end.content.extra", versions: "" },
  assets: { missing: "st.ui.end.assets.missing", extra: "st.ui.end.assets.extra", versions: "st.ui.end.assets.versions", ids: true },
};

// a list entry's second column: the version (or host and joiner versions), with the Paradox Mods id where the list shows ids
function entryDetail(p, m) {
  const parts = [];
  if (p.versions) parts.push(t("st.ui.end.mods.versionLine", { HOST: str(m.host) || "?", YOU: str(m.you) || "?" }));
  else if (str(m.version)) parts.push(str(m.version));
  if (p.ids && str(m.id)) parts.push(t("st.ui.end.paradoxId", { ID: str(m.id) }));
  return parts.join(" · ");
}

// the mods the host's city stores data of and the player lacks: each with why it is required (never "join anyway")
function RequiredList(p) {
  if (p.items.length === 0) return null;
  return h(
    "div",
    { style: { marginBottom: "12rem" } },
    h(
      "div",
      { style: merge(S.row, { marginBottom: "6rem" }) },
      h(Glyph, { src: G.warning, size: 12, color: OV.bad, style: { marginRight: "8rem" } }),
      h("div", { style: { fontSize: "15rem", fontWeight: "bold", textTransform: "uppercase", color: OV.badText } }, t("st.ui.end.mods.required") + " (" + p.items.length + ")")
    ),
    h(
      "div",
      { style: merge({ maxHeight: "170rem", overflowY: "auto", padding: "4rem 12rem", borderRadius: "4rem", backgroundColor: OV.well }, border("2rem", OV.bad, ["Left"])) },
      p.items.map(function (m, i) {
        return h(
          "div",
          { key: i, style: merge({ padding: "5rem 0" }, i === 0 ? null : border("1rem", OV.divider, ["Top"])) },
          h(
            "div",
            { style: { display: "flex", flexDirection: "row", fontSize: "14rem" } },
            h("div", { style: merge(S.ellipsis, { flex: 1, minWidth: 0, color: OV.text, fontWeight: "bold" }) }, str(m.name) || str(m.id) || "?"),
            str(m.version) ? h("div", { style: { color: OV.dim, marginLeft: "10rem", flexShrink: 0 } }, str(m.version)) : null
          ),
          h("div", { style: { fontSize: "13rem", color: OV.dim, lineHeight: 1.35, marginTop: "2rem", overflowWrap: "break-word", wordBreak: "break-word" } }, t(m.inCity === true ? "st.ui.end.mods.requiredInCity" : "st.ui.end.mods.requiredMaybe"))
        );
      })
    )
  );
}

function ModList(p) {
  if (p.items.length === 0) return null;
  return h(
    "div",
    { style: { marginBottom: "12rem" } },
    h(
      "div",
      { style: merge(S.row, { marginBottom: "6rem" }) },
      h(Glyph, { src: p.icon, size: 12, color: p.color, style: { marginRight: "8rem" } }),
      h("div", { style: { fontSize: "15rem", fontWeight: "bold", textTransform: "uppercase", color: p.color } }, p.title + " (" + p.items.length + ")")
    ),
    h(
      "div",
      { style: { maxHeight: "150rem", overflowY: "auto", padding: "4rem 12rem", borderRadius: "4rem", backgroundColor: OV.well } },
      p.items.map(function (m, i) {
        return h(
          "div",
          { key: i, style: merge({ display: "flex", flexDirection: "row", padding: "5rem 0", fontSize: "14rem" }, i === 0 ? null : border("1rem", OV.divider, ["Top"])) },
          h("div", { style: merge(S.ellipsis, { flex: 1, minWidth: 0, color: OV.text }) }, str(m.name) || str(m.id) || "?"),
          h("div", { style: { color: OV.dim, marginLeft: "10rem", flexShrink: 0 } }, entryDetail(p, m))
        );
      })
    )
  );
}

function reportText(e, title, text) {
  const lines = [title, text];
  if (str(e.text)) lines.push(str(e.text));
  objs(e.required).forEach(function (m) { lines.push("! " + str(m.name) + " " + str(m.version) + (m.inCity === true ? " (this city contains its data)" : " (can store data in a city)")); });
  objs(e.missing).forEach(function (m) { lines.push("+ " + str(m.name) + " " + str(m.version)); });
  objs(e.extra).forEach(function (m) { lines.push("- " + str(m.name) + " " + str(m.version)); });
  objs(e.versions).forEach(function (m) { lines.push("~ " + str(m.name) + " (host " + (str(m.host) || "?") + ", you " + (str(m.you) || "?") + ")"); });
  if (str(e.hostGame)) lines.push("game: host " + str(e.hostGame) + ", you " + (str(e.yourGame) || "?"));
  if (str(e.hostBuild)) lines.push("build: host " + str(e.hostBuild) + ", you " + (str(e.yourBuild) || "?"));
  if (e.overridable === true) lines.push("the host lets players join anyway");
  if (str(e.target)) lines.push("host: " + str(e.target));
  return lines.join("\n");
}

function VersionLine(p) {
  return h("div", { style: merge(S.ids, { marginBottom: "14rem", padding: "8rem 12rem", borderRadius: "4rem", backgroundColor: OV.well, fontSize: "15rem", color: OV.text }) }, p.children);
}

function EndOverlay(p) {
  const s = p.state;
  const st = useStore();
  const [password, setPassword] = React.useState("");
  const flash = useFlash();
  const e = obj(s.end);
  const copy = str(e.kind) ? endCopy(e) : null;
  const shown = !!copy && st.dismissedEnd !== e.seq;
  const needsPassword = copy === "badPassword" || copy === "passwordRequired" || copy === "lockout";
  const canRetry = !!copy && !NO_RETRY[copy];
  // the host refused different mods or asset packs but lets players join anyway (the lists stay; the choice is the player's)
  const canJoinAnyway = (copy === "mods" || copy === "assets") && e.overridable === true && objs(e.required).length === 0;
  const dismiss = function () {
    setStore({ dismissedEnd: e.seq });
    send("dismissEnd");
  };
  const joinAnyway = function () {
    if (!canJoinAnyway) return;
    setStore({ dismissedEnd: e.seq });
    send("joinAnyway");
  };
  const retry = function () {
    if (!canRetry || (needsPassword && password.length === 0)) return;
    setStore({ dismissedEnd: e.seq });
    send("retryJoin", needsPassword ? password : str(s.keepPassword) || KEEP_PASSWORD);
  };
  // Escape goes back, Enter tries again (the password field handles its own Enter)
  useWindowKeys({ Escape: dismiss, Enter: canRetry && !needsPassword ? retry : undefined }, shown, true);
  if (!shown) return null;
  const title = t("st.ui.end.title." + (copy === "conflictYou" || copy === "conflictHost" ? "conflict" : copy));
  const text = canJoinAnyway ? t(copy === "assets" ? "st.ui.end.text.assetsOverridable" : "st.ui.end.text.modsOverridable")
    : copy === "modsRequired" && e.restOverridable === true ? t("st.ui.end.text.modsRequiredRest")
    : t("st.ui.end.text." + copy);
  const required = copy === "modsRequired" ? objs(e.required) : [];
  const missing = objs(e.missing);
  const extra = objs(e.extra);
  const versions = objs(e.versions);
  const lists = END_LISTS[copy] || null;
  const others = missing.length + extra.length + versions.length > 0;
  const isMods = !!lists && (others || required.length > 0);
  const icon = h("div", { style: merge(S.center, { width: "26rem", height: "26rem", borderRadius: "13rem", backgroundColor: needsPassword ? OV.warn : OV.bad, marginRight: "12rem", flexShrink: 0 }) }, h(Glyph, { src: needsPassword ? G.lock : G.warning, size: 14, color: "#ffffff" }));
  return h(
    OverlayCard,
    { title: title, z: 2100, width: isMods ? "660rem" : "560rem", icon: icon, titleColor: OV.text },
    h("div", { style: { fontSize: "16rem", marginBottom: "14rem", lineHeight: 1.45, color: OV.text } }, text),
    isMods
      ? h(
          "div",
          null,
          h(RequiredList, { items: required }),
          required.length > 0 && others ? h("div", { style: { fontSize: "13rem", textTransform: "uppercase", color: OV.faint, marginBottom: "6rem" } }, t("st.ui.end.mods.others")) : null,
          h(ModList, { title: t(lists.missing), color: OV.good, icon: G.plus, items: missing, ids: lists.ids }),
          h(ModList, { title: t(lists.extra), color: OV.badText, icon: G.close, items: extra, ids: lists.ids }),
          lists.versions ? h(ModList, { title: t(lists.versions), color: OV.warn, icon: G.refresh, items: versions, versions: true, ids: lists.ids }) : null
        )
      : null,
    copy === "game" && str(e.hostGame) ? h(VersionLine, null, t("st.ui.end.versions", { HOST: str(e.hostGame), YOU: str(e.yourGame) || "?" })) : null,
    copy === "build" && str(e.hostProto) && !str(e.hostBuild) ? h(VersionLine, null, t("st.ui.end.versions", { HOST: str(e.hostProto), YOU: str(e.yourProto) || "?" })) : null,
    copy === "build" && str(e.hostBuild) ? h(VersionLine, null, t("st.ui.end.builds", { HOST: str(e.hostBuild), YOU: str(e.yourBuild) || "?" })) : null,
    needsPassword
      ? h(
          "div",
          { style: { marginBottom: "10rem" } },
          h("div", { style: { fontSize: "12rem", textTransform: "uppercase", color: OV.faint, marginBottom: "5rem" } }, t("st.ui.join.password")),
          h(Field, { look: "overlay", value: password, onChange: setPassword, onEnter: retry, onEscape: dismiss, password: true, placeholder: t("st.ui.join.passwordPlaceholder"), title: t("st.ui.join.password"), max: 64 })
        )
      : null,
    canJoinAnyway
      ? h(
          "div",
          { style: merge(S.row, { alignItems: "flex-start", marginTop: "4rem", padding: "10rem 12rem", borderRadius: "4rem", backgroundColor: OV.well }, border("1rem", OV.warn, ["Left"])) },
          h(Glyph, { src: G.warning, size: 14, color: OV.warn, style: { marginRight: "8rem", marginTop: "2rem", flexShrink: 0 } }),
          h("div", { style: { flex: 1, minWidth: 0, fontSize: "14rem", color: OV.text, lineHeight: 1.4, overflowWrap: "break-word", wordBreak: "break-word" } }, t("st.ui.end.joinAnywayWarning"))
        )
      : null,
    str(e.text) && !isMods
      ? h(
          "div",
          { style: { marginTop: "4rem", padding: "10rem 12rem", borderRadius: "4rem", backgroundColor: OV.well } },
          h("div", { style: { fontSize: "12rem", textTransform: "uppercase", color: OV.faint, marginBottom: "4rem" } }, t("st.ui.end.details")),
          h("div", { style: { fontSize: "14rem", color: OV.dim, lineHeight: 1.35, overflowWrap: "break-word", wordBreak: "break-word" } }, str(e.text))
        )
      : null,
    h(
      "div",
      { style: merge(S.row, { marginTop: "18rem" }) },
      h(LinkText, { color: OV.accent, style: merge(S.row, { fontSize: "14rem" }), onClick: function () { send("copyText", reportText(e, title, text)); flash.fire(); } }, h(Glyph, { src: flash.on ? G.check : G.copy, size: 14, color: OV.accent, style: { marginRight: "6rem" } }), flash.on ? t("st.ui.end.copied") : t("st.ui.end.copy")),
      h("div", { style: { flex: 1 } }),
      h(OvButton, { onClick: dismiss }, t("st.ui.end.back")),
      canJoinAnyway ? h(OvButton, { onClick: joinAnyway, style: merge({ marginLeft: "10rem" }, border("2rem", OV.warn)) }, t("st.ui.end.joinAnyway")) : null,
      canRetry ? h(OvButton, { kind: "primary", disabled: needsPassword && password.length === 0, onClick: retry, style: { marginLeft: "10rem" } }, needsPassword ? t("st.ui.end.retryPassword") : t("st.ui.end.retry")) : null
    )
  );
}

// ------------------------------------------------------------------------------------------------ chat dock

// first time each chat line (by its session-independent sequence number) was seen: the time shown next to it
const seenAt = {};
let seenCount = 0;
const CHAT_MAX = 500;
// the counter shows from here on (most messages are short; a counter on every keystroke is noise)
const CHAT_COUNT_FROM = 400;
const WRAP = { overflowWrap: "break-word", wordBreak: "break-word" };

function ChatLine(p) {
  const l = p.line;
  const at = seenAt[l.seq];
  const time = at ? clock(at) : "";
  return h(
    "div",
    { style: merge(S.row, { alignItems: "flex-start", padding: "3rem 8rem" }) },
    h(
      "div",
      { style: merge({ flex: 1, minWidth: 0, overflow: "hidden", fontSize: FS.s, lineHeight: 1.35 }, WRAP) },
      l.system
        ? // the session's own lines: an info mark and italics, never a name a player could choose
          [h(Glyph, { key: "i", src: G.info, size: 12, color: T.textDim, style: { marginRight: "4rem", display: "inline-block", verticalAlign: "middle" } }), h("span", { key: "s", style: { color: T.textDim, fontStyle: "italic" } }, str(l.text))]
        : // the name is isolated from the text, so a right-to-left name or message does not swap them around; the host's
          // lines carry a badge no player name can imitate
          [l.host === true ? h("span", { key: "b", style: { marginRight: "4rem", padding: "0 4rem", borderRadius: "3rem", fontSize: FS.xs, fontWeight: "bold", textTransform: "uppercase", color: T.text, backgroundColor: str(l.color) || T.accent } }, t("st.ui.chat.hostBadge")) : null,
           h("span", { key: "n", style: { color: str(l.color) || T.accent, fontWeight: "bold", unicodeBidi: "isolate" } }, str(l.name) || "?"), h("span", { key: "c", style: { color: str(l.color) || T.accent, fontWeight: "bold" } }, ": "), h("span", { key: "t", style: { color: T.text, unicodeBidi: "isolate" } }, str(l.text))]
    ),
    time ? h("div", { style: { marginLeft: "8rem", fontSize: FS.xs, color: T.textDimmer, flexShrink: 0, paddingTop: "1rem" } }, time) : null
  );
}

// is a scrolling box (nearly) at its end
function atEnd(el) {
  try {
    return !el || el.scrollHeight - el.scrollTop - el.clientHeight < 24;
  } catch (e) {
    return true;
  }
}

// The chat box docked on the right: collapsible, unread badge, player colours, time stamps, Enter to type, Esc to
// leave. New lines scroll it down only while it is at the bottom; scrolled up, a "new messages" button jumps down.
function ChatDock(p) {
  const lines = objs(p.state.chat);
  const st = useStore();
  const [text, setText] = React.useState("");
  const [typingHere, setTypingHere] = React.useState(false);
  const [preview, setPreview] = React.useState(-1);
  // the newest line seen at the bottom while scrolled up; -1 while following the newest line
  const [readUpTo, setReadUpTo] = React.useState(-1);
  const inputRef = React.useRef(null);
  const scrollRef = React.useRef(null);
  const previewTimer = React.useRef(null);
  const now = Date.now();
  lines.forEach(function (l) {
    if (l.seq !== undefined && seenAt[l.seq] === undefined) {
      seenAt[l.seq] = now;
      // the history keeps the last 100 lines: forget times of lines long gone
      if (++seenCount > 400) {
        const keep = {};
        lines.forEach(function (x) { if (seenAt[x.seq] !== undefined) keep[x.seq] = seenAt[x.seq]; });
        for (const k in seenAt) delete seenAt[k];
        Object.assign(seenAt, keep);
        seenCount = lines.length;
      }
    }
  });
  const lastSeq = lines.length > 0 ? num(lines[lines.length - 1].seq, -1) : -1;
  const open = st.chatOpen;
  let unread = 0;
  if (!open) for (const l of lines) if (!l.system && !l.you && num(l.seq, -1) > st.chatSeen) unread++;
  let below = 0;
  if (open && readUpTo >= 0) for (const l of lines) if (num(l.seq, -1) > readUpTo) below++;
  const focusInput = function () {
    try {
      if (inputRef.current) inputRef.current.focus();
    } catch (e) {
      // not mounted
    }
  };
  const toBottom = function () {
    try {
      if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    } catch (e) {
      // not mounted
    }
    setReadUpTo(-1);
  };
  React.useEffect(function () {
    // Enter (while nothing else takes typing and no dialog is open) opens the chat and focuses the input
    const onKey = function (e) {
      if (keyOf(e) !== "Enter" || store.typing > 0 || anotherInputFocused() || modalOpen()) return;
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
      // follow the newest line unless the player scrolled up to read
      if (readUpTo < 0) toBottom();
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
  const onScroll = function () {
    const end = atEnd(scrollRef.current);
    if (end && readUpTo >= 0) setReadUpTo(-1);
    else if (!end && readUpTo < 0) setReadUpTo(lastSeq);
  };
  const blur = function () {
    try {
      if (inputRef.current) inputRef.current.blur();
    } catch (e) {
      // already blurred
    }
  };
  const sendLine = function () {
    const line = text.trim();
    if (line.length > 0) {
      send("chat", line.slice(0, CHAT_MAX));
      toBottom(); // your own line always shows
    }
    setText("");
    blur();
  };
  const toggle = function () {
    setStore({ chatOpen: !store.chatOpen, chatSeen: lastSeq });
  };
  const previewLine = !open && preview >= 0 ? lines.filter(function (l) { return l.seq === preview; })[0] : null;
  const [hover, hv] = useHover();
  const [headFocus, headKb] = usePressable(toggle, false);
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
        merge(hv, headKb, {
          style: merge(S.row, { minHeight: "36rem", padding: "4rem 6rem 4rem 12rem", cursor: "pointer", color: T.titleBlue, backgroundColor: hover || headFocus ? "var(--panelColorDark-hover)" : T.panelDark, backdropFilter: T.blur, borderTopLeftRadius: T.radius, borderTopRightRadius: T.radius }, open ? null : { borderBottomLeftRadius: T.radius, borderBottomRightRadius: T.radius }, headFocus ? { boxShadow: "0 0 0 2rem " + T.focus } : null),
          onClick: toggle,
        }),
        h("div", { style: { flex: 1, textTransform: "uppercase", fontSize: FS.m } }, t("st.ui.chat.title")),
        unread > 0 ? h("div", { style: { minWidth: "20rem", height: "20rem", padding: "0 6rem", borderRadius: "10rem", backgroundColor: T.accent, color: "#0c1a25", fontSize: FS.xs, fontWeight: "bold", display: "flex", alignItems: "center", justifyContent: "center", marginRight: "8rem" } }, t("st.ui.chat.unread", { N: unread > 99 ? "99+" : unread })) : null,
        withTooltip(h("div", { style: merge(S.center, { width: "26rem", height: "26rem" }) }, h(Glyph, { src: open ? G.down : G.up, size: 11, color: T.textDim })), open ? t("st.ui.chat.collapse") : t("st.ui.chat.expand"), "up")
      ),
      open
        ? h(
            "div",
            { style: merge(S.col, { backgroundColor: "rgba(42, 55, 83, 0.6)", backgroundImage: T.panelGradient, backdropFilter: T.blur, borderBottomLeftRadius: T.radius, borderBottomRightRadius: T.radius }) },
            h(
              "div",
              { style: { position: "relative" } },
              // a plain scrolling box (not the vanilla Scrollable): the dock must set and read its scroll position
              h(
                "div",
                { ref: scrollRef, onScroll: onScroll, style: { height: "220rem", padding: "6rem 4rem", overflowY: "auto", overflowX: "hidden" } },
                lines.length === 0 ? h(Hint, { style: { padding: "8rem" } }, t("st.ui.chat.empty")) : lines.map(function (l, i) { return h(ChatLine, { key: l.seq !== undefined ? String(l.seq) : "i" + i, line: l }); })
              ),
              below > 0
                ? h(
                    "div",
                    { style: { position: "absolute", left: 0, right: 0, bottom: "6rem", display: "flex", flexDirection: "row", justifyContent: "center" } },
                    h(Btn, { kind: "flat", onClick: toBottom, style: { padding: "3rem 10rem", fontSize: FS.xs } }, h(Glyph, { src: G.down, size: 10, style: { marginRight: "6rem" } }), t("st.ui.chat.newBelow", { N: below }))
                  )
                : null
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
                max: CHAT_MAX,
              }),
              text.length >= CHAT_COUNT_FROM
                ? h("div", { style: { marginTop: "4rem", textAlign: "right", fontSize: FS.xs, color: text.length >= CHAT_MAX ? T.warn : T.textDimmer } }, t("st.ui.chat.count", { N: text.length, MAX: CHAT_MAX }))
                : null
            )
          )
        : null
    )
  );
}

// ---------------------------------------------------------------------------------------------- name tags

// Remote players' name tags over their cursors (positions are fractions of the screen, from C#).
function NameTags() {
  const tags = objs(useJson(presenceBinding).tags);
  return h(
    "div",
    { style: S.layer },
    tags
      .filter(function (tag) {
        return tag.on && isFinite(num(tag.x, NaN)) && isFinite(num(tag.y, NaN));
      })
      .map(function (tag) {
        return h(
          "div",
          {
            key: str(tag.id) || str(tag.name),
            style: merge(
              {
                position: "absolute",
                // kept on screen: a cursor at the very edge would push its tag out of view
                left: (Math.max(0.04, Math.min(0.96, num(tag.x, 0))) * 100).toFixed(2) + "%",
                top: (Math.max(0.05, Math.min(1, num(tag.y, 0))) * 100).toFixed(2) + "%",
                maxWidth: "320rem",
                overflow: "hidden",
                textOverflow: "ellipsis",
                transform: "translate(-50%, -130%)",
                padding: "2rem 9rem",
                borderRadius: "11rem",
                backgroundColor: "rgba(16, 22, 34, 0.82)",
                color: T.text,
                fontSize: FS.s,
                whiteSpace: "nowrap",
              },
              border("2rem", str(tag.color) || "#4bc3f1")
            ),
          },
          h("span", { style: { color: str(tag.color) || T.text, fontWeight: "bold", unicodeBidi: "isolate" } }, str(tag.name)),
          str(tag.activity) ? h("span", { style: { color: T.textDim } }, " · " + str(tag.activity)) : null
        );
      })
  );
}

// the performance overlay (right edge, under the top bar): the shared frame budget per feature, bytes per
// second per peer and the apply queue; read-only, never takes the mouse
function PerfHud() {
  const p = useJson(perfBinding);
  if (!p.show) return null;
  const frame = obj(p.frame);
  const features = objs(p.features);
  const peers = objs(p.peers);
  const num = function (v, d) {
    const x = Number(v);
    return isFinite(x) ? x.toFixed(d) : "0";
  };
  const row = { display: "flex", flexDirection: "row", alignItems: "center", marginTop: "2rem" };
  return h(
    "div",
    { style: { position: "absolute", right: "10rem", top: "62rem", width: "300rem", padding: "6rem 8rem", borderRadius: "6rem", backgroundColor: "rgba(16, 22, 34, 0.82)", color: T.text, fontSize: FS.xs, lineHeight: 1.3, pointerEvents: "none" } },
    h("div", { style: { fontWeight: "bold", marginBottom: "3rem" } }, t("st.ui.perf.title")),
    h("div", { style: { color: T.textDim } }, t("st.ui.perf.frame", { MS: num(frame.ms, 2), BUDGET: num(frame.budget, 0), OVER: num(frame.over, 0) })),
    features.map(function (f, i) {
      const cap = Number(f.cap) > 0 ? Number(f.cap) : 1;
      const share = Math.max(0, Math.min(1, Number(f.avg) / cap));
      const hot = Number(f.p95) >= cap;
      return h(
        "div",
        { key: "f" + i, style: row },
        h("div", { style: merge(S.ellipsis, { width: "92rem", color: T.textDim }) }, str(f.n)),
        h(
          "div",
          { style: { width: "70rem", height: "6rem", marginRight: "6rem", borderRadius: "3rem", backgroundColor: T.progressTrack } },
          h("div", { style: { width: (share * 100).toFixed(1) + "%", height: "6rem", borderRadius: "3rem", backgroundColor: hot ? OV.warn : OV.accent } })
        ),
        h("div", { style: { color: hot ? T.warn : T.textDim } }, t("st.ui.perf.feature", { AVG: num(f.avg, 2), P95: num(f.p95, 2), CAP: num(f.cap, 2) }))
      );
    }),
    h("div", { style: { marginTop: "5rem", fontWeight: "bold" } }, t("st.ui.perf.peers")),
    peers.map(function (q, i) {
      return h(
        "div",
        { key: "p" + i, style: row },
        h("div", { style: merge(S.ellipsis, { width: "92rem", color: T.textDim }) }, str(q.name)),
        h("div", { style: { color: T.textDim } }, t("st.ui.perf.peer", { IN: num(q.in, 1), OUT: num(q.out, 1), PING: num(q.ping, 0) }) + (Number(q.deferred) > 0 ? " · " + t("st.ui.perf.deferred", { N: num(q.deferred, 0) }) : ""))
      );
    }),
    h("div", { style: { marginTop: "5rem", color: Number(p.queue) > 64 ? T.warn : T.textDim } }, t("st.ui.perf.queue", { N: num(p.queue, 0) }))
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
  const count = objs(s.players).length;
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
  const count = objs(s.players).length;
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
    mode === "Joining" ? t("st.ui.join.overlayTitleUnknown") : mode === "Recovering" ? t("st.ui.menu.multiplayer") + " · " + t("st.ui.recovery.badge") : mode === "Hosting" || mode === "Client" ? t("st.ui.menu.multiplayer") + " · " + objs(s.players).length : t("st.ui.menu.multiplayer")
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
    if (mode === "Recovering") setStore({ open: true }); // the recovery choice must be seen
  }, [mode, recoveryPhase]);
  const close = function () {
    setStore({ open: false, invitesOpen: false });
  };
  // Escape closes the panel (the invite popover registers after it, so it closes first)
  useWindowKeys({ Escape: close }, st.open && !photo);
  const showInvites = st.open && st.invitesOpen && mode === "Hosting" && !!s.canInvite;
  return h(
    "div",
    { style: S.layer },
    live && !photo ? h(NameTags, null) : null, // under the panel
    live && !photo ? h(PerfHud, null) : null, // only while toggled on
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
