/*
 * Cities: Skylines II UI Module
 *
 * Id: SkylineTogether
 * Author: Skyline Together
 * Version: 0.2.0
 * Dependencies:
 */

// Hand-written ES module (no build step). The game provides React, its binding API and its own components as globals.
// Everything the game provides is feature-detected; nothing may throw while the module is imported or rendered. Where a
// game component is missing (or throws while rendering), a built-in look that copies the vanilla styles is used.
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

function send(name) {
  try {
    api.trigger.apply(null, [GROUP, name].concat(Array.prototype.slice.call(arguments, 1)));
  } catch (e) {
    console.warn("SkylineTogether: trigger " + name + " failed: " + e);
  }
}

function merge() {
  return Object.assign.apply(null, [{}].concat(Array.prototype.slice.call(arguments)));
}

function list(v) {
  return Array.isArray(v) ? v : [];
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
  "st.ui.steam.missing.title": "Steam is not available",
  "st.ui.steam.missing.text": "Skyline Together needs the Steam version of the game, with Steam running and logged in.",
  "st.ui.tab.host": "Host",
  "st.ui.tab.join": "Join",
  "st.ui.profile.name": "Your name",
  "st.ui.profile.namePlaceholder": "Empty: your Steam name",
  "st.ui.host.title": "Host this city",
  "st.ui.host.intro": "Open the city you are playing to your friends. They join with your Steam ID or a Steam invite.",
  "st.ui.host.sessionName": "Session name",
  "st.ui.host.sessionNamePlaceholder": "Empty: \"{NAME}'s city\"",
  "st.ui.host.password": "Password",
  "st.ui.host.passwordPlaceholder": "Optional",
  "st.ui.host.passwordKept": "Unchanged (a password is set)",
  "st.ui.host.removePassword": "Remove password",
  "st.ui.host.maxPlayers": "Max players",
  "st.ui.host.maxPlayersHint": "Including you.",
  "st.ui.host.requireMods": "Require the same mods",
  "st.ui.host.requireModsHint": "Refuse players whose code mods differ from yours. They get a list of what to enable, disable or update.",
  "st.ui.host.allowDirect": "Allow direct IP connections",
  "st.ui.host.allowDirectHint": "Also accept connections on a UDP port (needs port forwarding). Steam connections always work.",
  "st.ui.host.allowDirectNextTime": "Applies the next time you host.",
  "st.ui.host.port": "Port",
  "st.ui.host.start": "Host this city",
  "st.ui.host.needCity": "Load or start a city first, then host it from the game.",
  "st.ui.host.unavailable": "Hosting is not possible right now.",
  "st.ui.join.title": "Join a friend",
  "st.ui.join.intro": "Enter the host's Steam ID (shown in their Skyline Together panel) or IP address and port.",
  "st.ui.join.target": "Host",
  "st.ui.join.targetPlaceholder": "Steam ID or IP:port",
  "st.ui.join.password": "Password",
  "st.ui.join.passwordPlaceholder": "If the host set one",
  "st.ui.join.start": "Join",
  "st.ui.join.steamHint": "Got a Steam invite? Accept it in Steam and the game joins by itself.",
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
  "st.ui.end.title.full": "The session is full",
  "st.ui.end.text.full": "Ask the host to raise the player limit, or try again when someone leaves.",
  "st.ui.end.title.notAllowed": "You can't join this session",
  "st.ui.end.text.notAllowed": "The host does not accept you in this session.",
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
  "st.ui.players.title": "Players",
  "st.ui.players.count": "{N} / {MAX}",
  "st.ui.players.kick": "Kick",
  "st.ui.players.makeViewer": "Make viewer",
  "st.ui.players.makeBuilder": "Make builder",
  "st.ui.players.kickConfirm": "Sure?",
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
  "st.ui.settings.title": "Session settings",
  "st.ui.settings.livePassword": "New password",
  "st.ui.settings.apply": "Apply",
  "st.ui.chat.title": "Chat",
  "st.ui.chat.placeholder": "Message (Enter to send)",
  "st.ui.chat.overlayPlaceholder": "Say something… (Enter to send, Esc to close)",
  "st.ui.chat.hint": "Press Enter to chat",
  "st.ui.chat.empty": "No messages yet.",
  "st.ui.net.show": "Show network",
  "st.ui.net.hide": "Hide network",
  "st.ui.net.diagnostics": "Write diagnostics",
  "st.ui.leave.client": "Leave session",
  "st.ui.leave.host": "End session",
  "st.ui.leave.confirm": "Click again to confirm",
  // robust branch: recovery after an unexpected disconnect, self-test line
  "st.ui.recovery.lost.title": "Connection lost",
  "st.ui.recovery.lost.text": "Reconnecting in {S} s (attempt {N} of {MAX}). The city is paused until then; changes you make now are not kept.",
  "st.ui.recovery.attempt.title": "Reconnecting (attempt {N} of {MAX})",
  "st.ui.recovery.ended.title": "The session has ended",
  "st.ui.recovery.ended.text": "You can return to the main menu, or keep playing an offline copy of the host's city.",
  "st.ui.recovery.failed.title": "Not connected",
  "st.ui.recovery.failed.text": "Reconnecting did not work. You can try again, return to the main menu, or keep an offline copy.",
  "st.ui.recovery.reconnectNow": "Reconnect now",
  "st.ui.recovery.reconnect": "Reconnect",
  "st.ui.recovery.cancel": "Cancel",
  "st.ui.recovery.mainMenu": "Return to main menu",
  "st.ui.recovery.stayOffline": "Stay offline (keep a copy)",
  "st.ui.recovery.badge": "Disconnected",
  "st.ui.selftest.details": "Details",
  "st.ui.blocked": "Hosting and joining are unavailable: {REASON}",
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

// The vanilla design tokens (the game's :root custom properties) with their values as fallbacks.
const T = {
  text: "var(--normalTextColor, #F0FBFF)",
  dim: "rgba(240, 251, 255, 0.62)",
  dimmer: "rgba(240, 251, 255, 0.42)",
  accent: "var(--accentColorNormal, #4bc3f1)",
  accentDark: "var(--accentColorDark, #1e83aa)",
  selected: "var(--selectedColor, #1e83aa)",
  good: "var(--positiveColor, #8bdb46)",
  warn: "var(--warningColor, #ffa42d)",
  bad: "var(--negativeColor, #e95f4a)",
  panelDark: "var(--panelColorDark, rgba(24, 33, 51, 0.71))",
  gradient: "linear-gradient(var(--panelGradientStart, rgba(45, 79, 92, 0.9)), var(--panelGradientEnd, rgba(39, 55, 78, 0.9)))",
  section: "var(--sectionBackgroundColor, rgba(38, 49, 71, 0.5))",
  sectionHeader: "var(--sectionHeaderColor, rgba(21, 25, 43, 0.5))",
  divider: "var(--dividerColor, rgba(255, 255, 255, 0.1))",
  field: "var(--toolbarFieldColor, rgba(6, 10, 16, 0.45))",
  fieldBorder: "var(--toolbarFieldBorderColor, rgba(255, 255, 255, 0.08))",
  hover: "var(--hoverColor, rgba(255, 255, 255, 0.1))",
  radius: "var(--panelRadius, 4rem)",
  blur: "var(--panelBlur, blur(5px))",
  yes: "var(--dialogButtonYesColor, #63b900)",
  flat: "#00193e",
  flatHover: "#002e71",
  red: "linear-gradient(90deg, var(--gradientRedEdge, #C9395B), var(--gradientRedMiddle, #D7454E), var(--gradientRedMiddle, #D7454E), var(--gradientRedEdge, #C9395B))",
  progress: "linear-gradient(90deg, var(--accentColorDark, #1e83aa), var(--accentColorNormal, #4bc3f1))",
};
const FS = {
  xs: "var(--fontSizeXS, 12rem)",
  s: "var(--fontSizeS, 14rem)",
  m: "var(--fontSizeM, 16rem)",
  l: "var(--fontSizeL, 18rem)",
  xl: "var(--fontSizeXL, 20rem)",
  xxl: "var(--fontSizeXXL, 22rem)",
};

// The mod's icon (two people), shipped inline. Tinted vanilla buttons draw their icon as a CSS mask, which the game
// only ever feeds with files, so those use the game's own glyph of the same motif instead.
const ICON_DATA =
  "data:image/svg+xml;utf8," +
  encodeURIComponent(
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" fill="#ffffff">' +
      '<circle cx="11" cy="10" r="5"/><circle cx="22" cy="11" r="4" fill-opacity=".75"/>' +
      '<path d="M1 27c0-5.5 4.5-9 10-9s10 3.5 10 9z"/><path fill-opacity=".75" d="M19 27c0-3-1-5.6-2.8-7.3 1.5-.9 3.2-1.4 5-1.4 4.8 0 8.8 3.1 8.8 8.7z"/>' +
      "</svg>"
  );
const ICON_GLYPH = "Media/Game/Icons/Population.svg";

const S = {
  layer: { position: "absolute", left: 0, top: 0, width: "100%", height: "100%", pointerEvents: "none" },
  gamePanelPos: { position: "absolute", left: "10rem", top: "62rem", pointerEvents: "auto", zIndex: 1000 },
  screenBackdrop: { position: "absolute", left: 0, top: 0, width: "100%", height: "100%", background: "rgba(0, 0, 0, 0.5)", pointerEvents: "auto", zIndex: 999 },
  screenPos: { position: "absolute", left: "50%", top: "50%", transform: "translate(-50%, -50%)", pointerEvents: "auto", zIndex: 1000 },
  fbPanel: { display: "flex", flexDirection: "column", borderRadius: T.radius, overflow: "hidden", color: T.text, fontSize: FS.m, pointerEvents: "auto", boxShadow: "0 4rem 24rem rgba(0, 0, 0, 0.35)" },
  fbHeader: { display: "flex", alignItems: "center", minHeight: "44rem", padding: "0 6rem 0 12rem", background: T.panelDark, backdropFilter: T.blur, color: T.accent },
  fbContent: { background: T.gradient, backdropFilter: T.blur, overflowY: "auto" },
  headerRow: { display: "flex", alignItems: "center", justifyContent: "center", flex: 1, minHeight: "24rem" },
  headerIcon: { width: "22rem", height: "22rem", marginRight: "8rem" },
  headerTitle: { textTransform: "uppercase", fontSize: FS.l, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" },
  closeX: { width: "32rem", height: "32rem", display: "flex", alignItems: "center", justifyContent: "center", color: T.dim, fontSize: FS.xl, cursor: "pointer" },
  body: { padding: "10rem 12rem 12rem 12rem" },
  section: { marginBottom: "10rem", borderRadius: T.radius, overflow: "hidden", background: T.section },
  sectionHeader: { display: "flex", alignItems: "center", minHeight: "30rem", padding: "0 10rem", background: T.sectionHeader, color: T.dim, fontSize: FS.s, textTransform: "uppercase" },
  sectionBody: { padding: "8rem 10rem" },
  label: { color: T.dim, fontSize: FS.xs, textTransform: "uppercase", marginBottom: "4rem", marginTop: "6rem" },
  hint: { color: T.dim, fontSize: FS.s, lineHeight: 1.35 },
  input: { width: "100%", height: "34rem", padding: "0 10rem", background: T.field, border: "2rem solid", borderColor: T.fieldBorder, borderRadius: "6rem", color: T.text, fontSize: FS.m },
  inputFocus: { borderColor: T.accent },
  mono: { fontFamily: "monospace", letterSpacing: "0.5rem" },
  row: { display: "flex", alignItems: "center" },
  spread: { display: "flex", alignItems: "center", justifyContent: "space-between" },
  toggleRow: { display: "flex", alignItems: "center", padding: "8rem 0", borderTop: "1rem solid", borderColor: T.divider },
  chip: { padding: "1rem 7rem", borderRadius: "10rem", fontSize: FS.xs, textTransform: "uppercase", border: "1rem solid", whiteSpace: "nowrap" },
  tabs: { display: "flex", marginBottom: "10rem", borderRadius: T.radius, overflow: "hidden", background: T.sectionHeader },
  tab: { flex: 1, height: "38rem", display: "flex", alignItems: "center", justifyContent: "center", textTransform: "uppercase", fontSize: FS.m, color: T.dim, cursor: "pointer", borderBottom: "3rem solid transparent" },
  tabOn: { color: T.text, background: "rgba(255, 255, 255, 0.06)", borderBottomColor: T.accent },
  progressOuter: { height: "6rem", background: "rgba(0, 0, 0, 0.35)", borderRadius: "3rem", overflow: "hidden" },
  error: { color: T.bad },
  notice: { padding: "8rem 10rem", borderRadius: T.radius, background: "rgba(255, 164, 45, 0.14)", borderLeft: "3rem solid", borderColor: T.warn, color: T.text, fontSize: FS.s, marginBottom: "10rem" },
  link: { color: T.accent, fontSize: FS.s, cursor: "pointer" },
};

// ------------------------------------------------------------------------------------------- game components

const MAIN_MENU_PATH = "game-ui/menu/components/main-menu-screen/main-menu-screen.tsx";
const MENU_BUTTON_PATH = "game-ui/common/input/button/labeled-icon-button.tsx";
const MENU_THEME_PATH = "game-ui/menu/themes/menu-item.module.scss";
const CHECKBOX_PATH = "game-ui/common/input/toggle/checkbox/checkbox.tsx";
const FOCUS_PATH = "game-ui/common/focus/focus-key.ts";

// Resolved in register(); V.ok turns false when a game component throws while rendering (see Safe).
const V = { ok: true, Panel: null, Button: null, Tooltip: null, FloatingButton: null, Checkbox: null, LabeledIconButton: null, menuTheme: null, focusDisabled: undefined };

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
  }
  V.Checkbox = comp(regGet(moduleRegistry, CHECKBOX_PATH, "Checkbox"));
  V.LabeledIconButton = comp(regGet(moduleRegistry, MENU_BUTTON_PATH, "LabeledIconButton"));
  V.menuTheme = regGet(moduleRegistry, MENU_THEME_PATH, "classes");
  V.focusDisabled = regGet(moduleRegistry, FOCUS_PATH, "FOCUS_DISABLED") || undefined;
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

function withTooltip(node, tooltip) {
  if (!tooltip || !V.ok || !V.Tooltip) return node;
  return Safe(h(V.Tooltip, { tooltip: tooltip, direction: "right" }, node), function () { return node; });
}

// ------------------------------------------------------------------------------------------------ shared state

// Shared by separately appended components (main menu entry, top-left button, roots).
const store = { open: false, menuOpen: false, tab: null, typing: 0, topLeftMounted: false, dismissedEnd: -1, listeners: new Set() };
let menuButtonInstalled = false;
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

// Opens the multiplayer screen of the main menu (the vanilla menu entry calls it).
function openPanel() {
  setStore({ menuOpen: true });
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

function usePhotoMode() {
  const game = GAME_BINDINGS && GAME_BINDINGS.game;
  if (!game || !game.activeGamePanel$) return false;
  try {
    const panel = api.useValue(game.activeGamePanel$);
    return !!(panel && game.GamePanelType && panel.__Type === game.GamePanelType.PhotoMode);
  } catch (e) {
    return false;
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

// --------------------------------------------------------------------------------------------------- primitives

function FallbackButton(p) {
  const [hover, setHover] = React.useState(false);
  const kind = p.kind || "flat";
  let look;
  if (kind === "primary") look = { background: T.yes, color: "#ffffff", textTransform: "uppercase" };
  else if (kind === "danger") look = { backgroundImage: T.red, color: "#ffffff", textTransform: "uppercase" };
  else if (kind === "link") look = { color: T.accent, padding: 0, height: "auto" };
  else look = { background: hover ? T.flatHover : T.flat, color: "#ffffff", border: "1rem solid rgba(0, 0, 0, 0.2)" };
  return h(
    "div",
    {
      style: merge(
        { borderRadius: kind === "flat" ? "8rem" : "5rem", cursor: p.disabled ? "default" : "pointer", opacity: p.disabled ? 0.45 : hover && kind !== "flat" ? 0.9 : 1 },
        p.baseStyle,
        look,
        p.style
      ),
      onMouseEnter: function () {
        setHover(true);
      },
      onMouseLeave: function () {
        setHover(false);
      },
      onClick: p.onSelect,
    },
    p.children
  );
}

// kind: primary (the vanilla green), flat (vanilla dark), danger (red), link (text)
function Btn(p) {
  const kind = p.kind || "flat";
  const disabled = !!p.disabled;
  const onSelect = function () {
    if (!disabled && p.onClick) p.onClick();
  };
  const base = merge(
    { display: "flex", alignItems: "center", justifyContent: "center", flexShrink: 0, whiteSpace: "nowrap" },
    p.small ? { height: "28rem", padding: "0 10rem", fontSize: FS.s } : { height: "38rem", padding: "0 16rem", fontSize: FS.m },
    p.grow ? { flex: 1 } : null
  );
  const fallback = function () {
    return h(FallbackButton, { kind: kind, disabled: disabled, onSelect: onSelect, baseStyle: base, style: p.style }, p.children);
  };
  if (!V.ok || !V.Button || kind === "danger" || kind === "link") return fallback();
  return Safe(
    h(V.Button, { variant: kind === "primary" ? "primary" : "flat", disabled: disabled, onSelect: onSelect, focusKey: V.focusDisabled, style: merge(base, p.style) }, p.children),
    fallback
  );
}

// A plain input: while it has focus the game turns keyboard actions off (InputManager.hasInputFieldFocus), so typing
// never triggers game or mod hotkeys; vk-* attributes give the on-screen keyboard (gamepad) a title and type.
function Field(p) {
  const [focus, setFocus] = React.useState(false);
  return h("input", {
    ref: p.inputRef,
    type: p.password ? "password" : "text",
    value: p.value == null ? "" : String(p.value),
    placeholder: p.placeholder || "",
    maxLength: p.max || 128,
    "vk-title": p.title || p.placeholder || "",
    "vk-description": p.description || "",
    "vk-type": p.password ? "password" : "text",
    style: merge(S.input, focus ? S.inputFocus : null, p.mono ? S.mono : null, p.style),
    onFocus: function () {
      setFocus(true);
      store.typing++;
      if (p.onFocus) p.onFocus();
    },
    onBlur: function () {
      setFocus(false);
      store.typing = Math.max(0, store.typing - 1);
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

function Labeled(p) {
  return h("div", { style: { marginBottom: "6rem" } }, h("div", { style: S.label }, p.label), p.children, p.hint ? h("div", { style: merge(S.hint, { marginTop: "4rem" }) }, p.hint) : null);
}

function Switch(p) {
  return h(
    "div",
    {
      style: { width: "38rem", height: "22rem", borderRadius: "11rem", padding: "3rem", flexShrink: 0, background: p.checked ? T.accentDark : "rgba(255, 255, 255, 0.18)", opacity: p.disabled ? 0.5 : 1, cursor: "pointer" },
      onClick: p.onToggle,
    },
    h("div", { style: { width: "16rem", height: "16rem", borderRadius: "8rem", background: "#ffffff", marginLeft: p.checked ? "16rem" : "0rem", transition: "margin-left 120ms" } })
  );
}

function Toggle(p) {
  const toggle = function () {
    if (!p.disabled && p.onChange) p.onChange(!p.checked);
  };
  const fallback = function () {
    return h(Switch, { checked: !!p.checked, disabled: p.disabled, onToggle: toggle });
  };
  const box = V.ok && V.Checkbox ? Safe(h(V.Checkbox, { checked: !!p.checked, disabled: !!p.disabled, onChange: toggle }), fallback) : fallback();
  return h(
    "div",
    { style: merge(S.toggleRow, p.first ? { borderTop: "none" } : null) },
    h("div", { style: { flex: 1, marginRight: "12rem" } }, h("div", { style: { fontSize: FS.m } }, p.label), p.hint ? h("div", { style: merge(S.hint, { marginTop: "2rem" }) }, p.hint) : null),
    box
  );
}

function Stepper(p) {
  const value = Math.max(p.min, Math.min(p.max, p.value || p.min));
  return h(
    "div",
    { style: S.row },
    h(Btn, { small: true, disabled: value <= p.min, onClick: function () { p.onChange(value - 1); } }, "−"),
    h("div", { style: { width: "44rem", textAlign: "center", fontSize: FS.l } }, String(value)),
    h(Btn, { small: true, disabled: value >= p.max, onClick: function () { p.onChange(value + 1); } }, "+")
  );
}

function Section(p) {
  return h(
    "div",
    { style: S.section },
    p.title ? h("div", { style: S.sectionHeader }, h("div", { style: { flex: 1 } }, p.title), p.right || null) : null,
    h("div", { style: S.sectionBody }, p.children)
  );
}

function Chip(p) {
  return h("div", { style: merge(S.chip, { color: p.color || T.dim, borderColor: p.color || T.dim }, p.style) }, p.children);
}

function Dot(p) {
  const size = p.size || 10;
  return h("div", { style: { width: size + "rem", height: size + "rem", borderRadius: size / 2 + "rem", background: p.color, flexShrink: 0, marginRight: (p.gap == null ? 8 : p.gap) + "rem" } });
}

function Avatar(p) {
  const name = String(p.name || "?");
  return h(
    "div",
    { style: { width: "28rem", height: "28rem", borderRadius: "14rem", background: p.color || T.accent, color: "rgba(12, 26, 37, 0.85)", display: "flex", alignItems: "center", justifyContent: "center", fontSize: FS.m, fontWeight: "bold", flexShrink: 0, marginRight: "10rem", border: "2rem solid rgba(255, 255, 255, 0.35)" } },
    name.charAt(0).toUpperCase()
  );
}

function Progress(p) {
  const pct = Math.max(0, Math.min(100, Math.round((p.value || 0) * 100)));
  return h("div", { style: merge(S.progressOuter, p.style) }, h("div", { style: { width: pct + "%", height: "100%", backgroundImage: T.progress, transition: "width 300ms" } }));
}

// Signal bars like a phone: green under 80 ms, amber under 250 ms, red above.
function PingBars(p) {
  const ping = p.ping;
  if (ping == null || ping < 0) return null;
  const n = ping < 80 ? 4 : ping < 150 ? 3 : ping < 250 ? 2 : 1;
  const color = n >= 3 ? T.good : n === 2 ? T.warn : T.bad;
  const bars = [];
  for (let i = 0; i < 4; i++) {
    bars.push(h("div", { key: i, style: { width: "3rem", height: 4 + i * 3 + "rem", marginLeft: "2rem", borderRadius: "1rem", background: i < n ? color : "rgba(255, 255, 255, 0.18)" } }));
  }
  return h("div", { style: { display: "flex", alignItems: "flex-end", height: "14rem" } }, bars);
}

function CloseX(p) {
  return h("div", { style: S.closeX, onClick: p.onClick }, "✕");
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

// ------------------------------------------------------------------------------------------------ panel shell

function PanelShell(p) {
  const width = p.width || "420rem";
  const header = h("div", { style: S.headerRow }, h("img", { src: ICON_DATA, style: S.headerIcon }), h("div", { style: S.headerTitle }, p.title));
  const fallback = function () {
    return h(
      "div",
      { style: merge(S.fbPanel, { width: width }) },
      h("div", { style: S.fbHeader }, header, h(CloseX, { onClick: p.onClose })),
      h("div", { style: merge(S.fbContent, { maxHeight: p.maxHeight || "72vh" }) }, h("div", { style: S.body }, p.children))
    );
  };
  if (!V.ok || !V.Panel) return fallback();
  return Safe(h(V.Panel, { header: header, onClose: p.onClose, style: { width: width, maxHeight: p.maxHeight || "72vh" } }, h("div", { style: S.body }, p.children)), fallback);
}

// --------------------------------------------------------------------------------------------------- lobby

function SteamMissing() {
  return h(
    "div",
    { style: { padding: "8rem 2rem" } },
    h("div", { style: { fontSize: FS.l, color: T.bad, marginBottom: "6rem" } }, t("st.ui.steam.missing.title")),
    h("div", { style: S.hint }, t("st.ui.steam.missing.text"))
  );
}

function Lobby(p) {
  const s = p.state;
  const st = useStore();
  const [name, setName] = React.useState(s.playerName || "");
  const tab = st.tab || (p.inGame ? "host" : "join");
  const saveName = function () {
    send("saveHostSettings", name, s.sessionNameSetting || "", s.keepPassword || "");
  };
  return h(
    "div",
    null,
    h(
      "div",
      { style: S.tabs },
      ["host", "join"].map(function (id) {
        return h("div", { key: id, style: merge(S.tab, tab === id ? S.tabOn : null), onClick: function () { setStore({ tab: id }); } }, t("st.ui.tab." + id));
      })
    ),
    h(Labeled, { label: t("st.ui.profile.name") }, h(Field, { value: name, onChange: setName, onBlur: saveName, placeholder: t("st.ui.profile.namePlaceholder"), title: t("st.ui.profile.name"), max: 64 })),
    tab === "host" ? h(HostTab, { state: s, name: name, inGame: p.inGame }) : h(JoinTab, { state: s, name: name, inGame: p.inGame }),
    s.blocked ? h("div", { style: merge(S.notice, { marginTop: "8rem", color: T.bad }) }, t("st.ui.blocked", { REASON: s.blocked })) : null,
    h(SelfTest, { state: s })
  );
}

function HostSettingsFields(p) {
  const s = p.state;
  const [maxPlayers, setMaxPlayers] = React.useState(s.maxPlayers || 8);
  const [allowDirect, setAllowDirect] = React.useState(!!s.allowDirect);
  const [requireMods, setRequireMods] = React.useState(s.requireSameMods !== false);
  const [port, setPort] = React.useState(String(s.portSetting || 27015));
  const save = function (next) {
    const v = merge({ maxPlayers: maxPlayers, allowDirect: allowDirect, requireMods: requireMods, port: port }, next);
    const portNumber = parseInt(v.port, 10);
    send("saveHostOptions", v.maxPlayers, !!v.allowDirect, !!v.requireMods, portNumber >= 1024 && portNumber <= 65535 ? portNumber : 0);
  };
  return h(
    "div",
    null,
    h(
      "div",
      { style: merge(S.toggleRow, { borderTop: "none" }) },
      h("div", { style: { flex: 1 } }, h("div", null, t("st.ui.host.maxPlayers")), h("div", { style: S.hint }, t("st.ui.host.maxPlayersHint"))),
      h(Stepper, { value: maxPlayers, min: 2, max: 16, onChange: function (v) { setMaxPlayers(v); save({ maxPlayers: v }); } })
    ),
    h(Toggle, { label: t("st.ui.host.requireMods"), hint: t("st.ui.host.requireModsHint"), checked: requireMods, onChange: function (v) { setRequireMods(v); save({ requireMods: v }); } }),
    h(Toggle, {
      label: t("st.ui.host.allowDirect"),
      hint: t("st.ui.host.allowDirectHint") + (p.live ? " " + t("st.ui.host.allowDirectNextTime") : ""),
      checked: allowDirect,
      onChange: function (v) { setAllowDirect(v); save({ allowDirect: v }); },
    }),
    allowDirect
      ? h(Labeled, { label: t("st.ui.host.port") }, h(Field, { value: port, onChange: setPort, onBlur: function () { save({}); }, mono: true, max: 5, style: { width: "120rem" }, title: t("st.ui.host.port") }))
      : null
  );
}

function HostTab(p) {
  const s = p.state;
  const [session, setSession] = React.useState(s.sessionNameSetting || "");
  const [password, setPassword] = React.useState("");
  // review fix: an untouched (empty) password field keeps the stored host password instead of wiping it
  const save = function (pw) {
    send("saveHostSettings", p.name, session, pw !== undefined ? pw : password.length > 0 ? password : s.keepPassword || "");
  };
  const reason = !p.inGame ? t("st.ui.host.needCity") : !s.canHost ? t("st.ui.host.unavailable") : "";
  return h(
    "div",
    null,
    h("div", { style: merge(S.hint, { margin: "4rem 0 8rem 0" }) }, t("st.ui.host.intro")),
    h(Labeled, { label: t("st.ui.host.sessionName") }, h(Field, { value: session, onChange: setSession, onBlur: function () { save(); }, placeholder: t("st.ui.host.sessionNamePlaceholder", { NAME: p.name || s.playerName || "…" }), title: t("st.ui.host.sessionName"), max: 64 })),
    h(
      Labeled,
      { label: t("st.ui.host.password") },
      h(Field, { value: password, onChange: setPassword, onBlur: function () { save(); }, password: true, placeholder: s.hasPassword ? t("st.ui.host.passwordKept") : t("st.ui.host.passwordPlaceholder"), title: t("st.ui.host.password") }),
      s.hasPassword ? h("div", { style: merge(S.link, { marginTop: "4rem" }), onClick: function () { setPassword(""); save(""); } }, t("st.ui.host.removePassword")) : null
    ),
    h(Section, null, h(HostSettingsFields, { state: s, live: false })),
    reason ? h("div", { style: merge(S.hint, { marginBottom: "8rem" }) }, reason) : null,
    h(Btn, { kind: "primary", disabled: !p.inGame || !s.canHost, onClick: function () { save(); send("host"); }, style: { width: "100%" } }, t("st.ui.host.start"))
  );
}

function JoinTab(p) {
  const s = p.state;
  const [target, setTarget] = React.useState(s.joinTarget || "");
  const [password, setPassword] = React.useState("");
  const canJoin = !!s.canJoin && target.trim().length > 0;
  const join = function () {
    if (!canJoin) return;
    send("saveHostSettings", p.name, s.sessionNameSetting || "", s.keepPassword || "");
    send("join", target.trim(), password);
  };
  return h(
    "div",
    null,
    h("div", { style: merge(S.hint, { margin: "4rem 0 8rem 0" }) }, t("st.ui.join.intro")),
    h(Labeled, { label: t("st.ui.join.target") }, h(Field, { value: target, onChange: setTarget, onEnter: join, mono: true, placeholder: t("st.ui.join.targetPlaceholder"), title: t("st.ui.join.target"), max: 96 })),
    h(Labeled, { label: t("st.ui.join.password") }, h(Field, { value: password, onChange: setPassword, onEnter: join, password: true, placeholder: t("st.ui.join.passwordPlaceholder"), title: t("st.ui.join.password") })),
    p.inGame ? h("div", { style: S.notice }, t("st.ui.join.replaceWarning")) : null,
    h(Btn, { kind: "primary", disabled: !canJoin, onClick: join, style: { width: "100%", marginTop: "4rem" } }, t("st.ui.join.start")),
    h("div", { style: merge(S.hint, { marginTop: "10rem" }) }, t("st.ui.join.steamHint"))
  );
}

// --------------------------------------------------------------------------------------------------- session

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
  const doing = p.activity;
  const color = pl.color || (doing && doing.color) || T.accent;
  const joining = pl.state !== "Live";
  return h(
    "div",
    { style: { display: "flex", alignItems: "center", padding: "6rem 0", borderTop: p.first ? "none" : "1rem solid", borderColor: T.divider } },
    h(Avatar, { name: pl.name, color: color }),
    h(
      "div",
      { style: { flex: 1, minWidth: 0 } },
      h(
        "div",
        { style: S.row },
        h("div", { style: { fontSize: FS.m, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" } }, pl.name),
        pl.you ? h("div", { style: { color: T.dim, fontSize: FS.s, marginLeft: "6rem" } }, "(" + t("st.ui.session.you") + ")") : null,
        pl.role ? h(Chip, { color: pl.host ? T.accent : T.dim, style: { marginLeft: "8rem" } }, roleText(pl.role)) : null
      ),
      joining
        ? h("div", { style: { marginTop: "4rem" } }, h("div", { style: { color: T.warn, fontSize: FS.s, marginBottom: "3rem" } }, playerStateText(pl)), pl.state === "Downloading" || pl.state === "Loading" ? h(Progress, { value: pl.progress }) : null)
        : doing && doing.activity
          ? h("div", { style: { color: T.dim, fontSize: FS.s, marginTop: "2rem" } }, doing.activity)
          : null
    ),
    !pl.host && !joining ? h("div", { style: { display: "flex", alignItems: "center", marginLeft: "8rem" } }, h(PingBars, { ping: pl.ping }), h("div", { style: { color: T.dim, fontSize: FS.xs, marginLeft: "5rem", width: "44rem" } }, t("st.ui.players.ping", { N: pl.ping }))) : null,
    s.mode === "Hosting" && !pl.host && !joining // gameplay branch: viewers watch, builders build
      ? h(Btn, { small: true, kind: "flat", style: { marginLeft: "8rem" }, onClick: function () { send("toggleRole", pl.id); } }, pl.role === "viewer" ? t("st.ui.players.makeBuilder") : t("st.ui.players.makeViewer"))
      : null,
    s.mode === "Hosting" && !pl.host
      ? h(Btn, { small: true, kind: confirm.armed ? "danger" : "flat", style: { marginLeft: "8rem" }, onClick: function () { confirm.run(function () { send("kick", pl.id); }); } }, confirm.armed ? t("st.ui.players.kickConfirm") : t("st.ui.players.kick"))
      : null
  );
}

function Players(p) {
  const s = p.state;
  const players = list(s.players);
  const doing = useActivities();
  return h(
    Section,
    { title: t("st.ui.players.title"), right: h("div", { style: { color: T.dim } }, s.mode === "Hosting" ? t("st.ui.players.count", { N: players.length, MAX: s.maxPlayers || "?" }) : String(players.length)) },
    players.map(function (pl, i) {
      return h(PlayerRow, { key: pl.id, player: pl, state: s, activity: doing[pl.id], first: i === 0 });
    })
  );
}

function Invite(p) {
  const s = p.state;
  const flash = useFlash();
  const id = s.inviteSteamId || s.steamId || "";
  return h(
    Section,
    { title: t("st.ui.invite.title") },
    h("div", { style: S.label }, s.mode === "Hosting" ? t("st.ui.invite.steamId") : t("st.ui.invite.hostSteamId")),
    h(
      "div",
      { style: S.row },
      h("div", { style: merge(S.mono, { flex: 1, fontSize: FS.l, userSelect: "text" }) }, id),
      h(Btn, { small: true, onClick: function () { send("copySteamId"); flash.fire(); } }, flash.on ? t("st.ui.invite.copied") : t("st.ui.invite.copy"))
    ),
    s.canInvite ? h(Btn, { style: { width: "100%", marginTop: "8rem" }, onClick: function () { send("inviteFriends"); } }, t("st.ui.invite.steam")) : null,
    s.mode === "Hosting" && s.directPort > 0 ? h("div", { style: merge(S.hint, { marginTop: "6rem" }) }, t("st.ui.invite.direct", { PORT: s.directPort })) : null
  );
}

function LiveSettings(p) {
  const s = p.state;
  const [open, setOpen] = React.useState(false);
  const [password, setPassword] = React.useState("");
  return h(
    Section,
    { title: t("st.ui.settings.title"), right: h("div", { style: S.link, onClick: function () { setOpen(!open); } }, open ? "▲" : "▼") },
    open
      ? h(
          "div",
          null,
          h(
            Labeled,
            { label: t("st.ui.settings.livePassword") },
            h(
              "div",
              { style: S.row },
              h(Field, { value: password, onChange: setPassword, password: true, placeholder: s.hasPassword ? t("st.ui.host.passwordKept") : t("st.ui.host.passwordPlaceholder"), title: t("st.ui.settings.livePassword"), style: { flex: 1, marginRight: "8rem" } }),
              h(Btn, { small: true, disabled: password.length === 0, onClick: function () { send("saveHostSettings", s.playerName || "", s.sessionNameSetting || "", password); setPassword(""); } }, t("st.ui.settings.apply"))
            ),
            s.hasPassword ? h("div", { style: merge(S.link, { marginTop: "4rem" }), onClick: function () { send("saveHostSettings", s.playerName || "", s.sessionNameSetting || "", ""); } }, t("st.ui.host.removePassword")) : null
          ),
          h(HostSettingsFields, { state: s, live: true })
        )
      : null
  );
}

function ChatLine(p) {
  const l = p.line;
  if (l.system) return h("div", { style: { color: T.dim, fontStyle: "italic", marginBottom: "3rem", fontSize: p.size || FS.s } }, l.text);
  return h(
    "div",
    { style: { marginBottom: "3rem", fontSize: p.size || FS.s, lineHeight: 1.35 } },
    h("span", { style: { color: l.color || T.accent, fontWeight: "bold" } }, l.name + ": "),
    h("span", { style: { color: T.text } }, l.text)
  );
}

function Chat(p) {
  const lines = list(p.state.chat);
  const [text, setText] = React.useState("");
  const boxRef = React.useRef(null);
  React.useEffect(function () {
    if (boxRef.current) boxRef.current.scrollTop = boxRef.current.scrollHeight;
  }, [lines.length]);
  const sendLine = function () {
    if (text.trim().length === 0) return;
    send("chat", text);
    setText("");
  };
  return h(
    Section,
    { title: t("st.ui.chat.title") },
    h(
      "div",
      { style: { height: "150rem", overflowY: "auto", marginBottom: "8rem" }, ref: boxRef },
      lines.length === 0 ? h("div", { style: S.hint }, t("st.ui.chat.empty")) : lines.map(function (l, i) { return h(ChatLine, { key: l.seq !== undefined ? l.seq : i, line: l }); })
    ),
    h(Field, { value: text, onChange: setText, onEnter: sendLine, placeholder: t("st.ui.chat.placeholder"), title: t("st.ui.chat.title"), max: 500 })
  );
}

function NetLine() {
  const n = useJson(netBinding);
  return h(
    "div",
    { style: merge(S.row, { marginTop: "2rem", marginBottom: "10rem" }) },
    n.show && n.text ? h("div", { style: merge(S.mono, { flex: 1, color: T.dim, fontSize: FS.xs }) }, n.text) : h("div", { style: { flex: 1 } }),
    h("div", { style: merge(S.link, { marginLeft: "10rem", fontSize: FS.xs }), onClick: function () { send("toggleNetHud"); } }, n.show ? t("st.ui.net.hide") : t("st.ui.net.show")),
    h("div", { style: merge(S.link, { marginLeft: "10rem", fontSize: FS.xs }), onClick: function () { send("writeDiagnostics"); } }, t("st.ui.net.diagnostics"))
  );
}

// robust branch: the startup self-test in one line (details go to the diagnostics file)
function SelfTest(p) {
  const s = p.state;
  if (!s.probes) return null;
  const off = s.probes.indexOf("off:") >= 0;
  return h(
    "div",
    { style: merge(S.hint, { fontSize: FS.xs, marginTop: "10rem", color: off ? T.warn : T.dimmer }) },
    s.probes + " ",
    h("span", { style: { color: T.accent, cursor: "pointer" }, onClick: function () { send("writeDiagnostics"); } }, t("st.ui.selftest.details"))
  );
}

// robust branch: a client session ended unexpectedly in the host's city: automatic rejoin, or the player's choice
function RecoveryView(p) {
  const s = p.state;
  const r = s.recovery || {};
  let title;
  let text;
  let buttons = [];
  if (r.phase === "Waiting") {
    title = t("st.ui.recovery.lost.title");
    text = t("st.ui.recovery.lost.text", { S: r.seconds || 0, N: (r.attempt || 0) + 1, MAX: r.max || 0 });
    buttons = [
      h(Btn, { key: "now", kind: "primary", style: { width: "100%" }, onClick: function () { send("reconnect"); } }, t("st.ui.recovery.reconnectNow")),
      h(Btn, { key: "menu", style: { width: "100%" }, onClick: function () { send("mainMenu"); } }, t("st.ui.recovery.mainMenu")),
      h(Btn, { key: "stay", style: { width: "100%" }, onClick: function () { send("stayOffline"); } }, t("st.ui.recovery.stayOffline")),
    ];
  } else if (r.phase === "Attempting") {
    title = t("st.ui.recovery.attempt.title", { N: r.attempt || 1, MAX: r.max || 0 });
    text = STAGES.indexOf(r.attemptStage) >= 0 ? t("st.ui.join.stage." + r.attemptStage) : "";
    buttons = [h(Btn, { key: "cancel", style: { width: "100%" }, onClick: function () { send("cancelReconnect"); } }, t("st.ui.recovery.cancel"))];
  } else {
    title = r.hostEnded ? t("st.ui.recovery.ended.title") : t("st.ui.recovery.failed.title");
    text = r.hostEnded ? t("st.ui.recovery.ended.text") : t("st.ui.recovery.failed.text");
    if (r.canReconnect) buttons.push(h(Btn, { key: "again", kind: "primary", style: { width: "100%" }, onClick: function () { send("reconnect"); } }, t("st.ui.recovery.reconnect")));
    buttons.push(h(Btn, { key: "menu", kind: r.canReconnect ? null : "primary", style: { width: "100%" }, onClick: function () { send("mainMenu"); } }, t("st.ui.recovery.mainMenu")));
    buttons.push(h(Btn, { key: "stay", style: { width: "100%" }, onClick: function () { send("stayOffline"); } }, t("st.ui.recovery.stayOffline")));
  }
  return h(
    "div",
    null,
    h(
      "div",
      { style: merge(S.row, { marginBottom: "8rem" }) },
      h(Dot, { color: r.phase === "NeedsChoice" ? T.bad : T.warn, size: 10 }),
      h("div", { style: { flex: 1, fontSize: FS.m } }, title)
    ),
    r.reason ? h("div", { style: merge(S.hint, { marginBottom: "6rem" }) }, r.reason) : null,
    text ? h("div", { style: merge(S.notice, { marginBottom: "8rem" }) }, text) : null,
    r.phase === "Attempting" ? h(Progress, { value: s.progress, style: { marginBottom: "10rem" } }) : null,
    h("div", { style: { display: "flex", flexDirection: "column", marginBottom: "10rem" } }, buttons.map(function (b, i) { return h("div", { key: i, style: { marginBottom: "6rem" } }, b); })),
    list(s.players).length > 0 ? h(Players, { state: s }) : null,
    h(SelfTest, { state: s })
  );
}

function SessionView(p) {
  const s = p.state;
  const hosting = s.mode === "Hosting";
  const confirm = useConfirm();
  return h(
    "div",
    null,
    h(
      "div",
      { style: merge(S.row, { marginBottom: "8rem" }) },
      h(Dot, { color: T.good, size: 10 }),
      h("div", { style: { flex: 1, fontSize: FS.m } }, hosting ? t("st.ui.session.hosting") : t("st.ui.session.connected")),
      h(Chip, { color: T.good }, t("st.ui.session.live"))
    ),
    s.hold ? h("div", { style: S.notice }, s.status || t("st.ui.session.holding")) : null,
    !hosting ? h("div", { style: merge(S.hint, { marginBottom: "8rem" }) }, t("st.ui.session.speed", { SPEED: speedText(s) })) : null,
    s.sync ? h("div", { style: merge(S.hint, { fontSize: FS.xs, marginBottom: "8rem", color: T.dimmer }) }, s.sync) : null,
    h(Players, { state: s }),
    h(Invite, { state: s }),
    hosting ? h(LiveSettings, { state: s }) : null,
    h(Chat, { state: s }),
    h(NetLine, null),
    h(SelfTest, { state: s }),
    h(
      Btn,
      { kind: "danger", style: { width: "100%" }, onClick: function () { confirm.run(function () { send("leave"); }); } },
      confirm.armed ? t("st.ui.leave.confirm") : hosting ? t("st.ui.leave.host") : t("st.ui.leave.client")
    )
  );
}

function PanelContent(p) {
  const s = p.state;
  const mode = s.mode || "None";
  if (s.steam === false) return h(SteamMissing, null);
  if (mode === "Hosting" || mode === "Client") return h(SessionView, { state: s });
  if (mode === "Recovering") return h(RecoveryView, { state: s });
  if (mode === "Joining") {
    return h(
      "div",
      null,
      h("div", { style: merge(S.hint, { marginBottom: "8rem" }) }, s.status || t("st.ui.join.overlayTitleUnknown")),
      h(Progress, { value: s.progress, style: { marginBottom: "10rem" } }),
      h(Btn, { onClick: function () { send("leave"); }, style: { width: "100%" } }, t("st.ui.join.cancel"))
    );
  }
  return h(Lobby, { state: s, inGame: p.inGame });
}

function panelTitle(s) {
  const mode = s.mode || "None";
  return (mode === "Hosting" || mode === "Client") && s.sessionName ? s.sessionName : t("st.ui.title");
}

// -------------------------------------------------------------------------------------------- join overlay

const STAGES = ["connecting", "handshaking", "waiting", "downloading", "loading"];

function StageRow(p) {
  const marker =
    p.state === "done"
      ? h("div", { style: { width: "14rem", height: "14rem", borderRadius: "7rem", background: T.good } })
      : p.state === "active"
        ? h("div", { style: { width: "14rem", height: "14rem", borderRadius: "7rem", border: "3rem solid", borderColor: T.accent } })
        : h("div", { style: { width: "14rem", height: "14rem", borderRadius: "7rem", border: "2rem solid rgba(255, 255, 255, 0.25)" } });
  return h(
    "div",
    { style: { display: "flex", alignItems: "flex-start", padding: "7rem 0" } },
    h("div", { style: { width: "26rem", paddingTop: "4rem", flexShrink: 0 } }, marker),
    h(
      "div",
      { style: { flex: 1 } },
      h("div", { style: { fontSize: FS.l, color: p.state === "pending" ? T.dimmer : T.text } }, t("st.ui.join.stage." + p.id)),
      p.state === "active" && p.hint ? h("div", { style: merge(S.hint, { marginTop: "3rem" }) }, p.hint) : null,
      p.state === "active" && p.progress != null ? h(Progress, { value: p.progress, style: { marginTop: "8rem" } }) : null
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
  const title = j.session ? t("st.ui.join.overlayTitle", { SESSION: j.session }) : t("st.ui.join.overlayTitleUnknown");
  return h(
    "div",
    { style: merge(S.screenBackdrop, { background: "rgba(6, 10, 18, 0.9)", zIndex: 2000 }) },
    h(
      "div",
      { style: merge(S.screenPos, { width: "560rem", zIndex: 2001 }) },
      h(
        "div",
        { style: merge(S.fbPanel, { width: "100%" }) },
        h("div", { style: merge(S.fbHeader, { minHeight: "52rem" }) }, h("div", { style: S.headerRow }, h("img", { src: ICON_DATA, style: S.headerIcon }), h("div", { style: S.headerTitle }, title))),
        h(
          "div",
          { style: merge(S.fbContent, { padding: "14rem 22rem 18rem 22rem" }) },
          j.target ? h("div", { style: merge(S.hint, S.mono, { marginBottom: "8rem" }) }, t("st.ui.join.via", { TARGET: j.target })) : null,
          STAGES.map(function (id, i) {
            return h(StageRow, { key: id, id: id, state: i < at ? "done" : i === at ? "active" : "pending", hint: i === at ? hint : null, progress: i === at ? progress : null });
          }),
          h(Progress, { value: j.progress != null ? j.progress : s.progress, style: { marginTop: "12rem", height: "4rem" } }),
          h("div", { style: { display: "flex", justifyContent: "flex-end", marginTop: "14rem" } }, h(Btn, { onClick: function () { send("leave"); } }, t("st.ui.join.cancel")))
        )
      )
    )
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
    if (r === "Banned" || r === "NotInvited") return "notAllowed";
    if (r === "HostPreparing") return "notReady";
    if (r === "HostShuttingDown") return "closing";
    return "failed";
  }
  if (e.kind === "timeout") return "timeout";
  if (e.kind === "connectionLost") return "lost";
  if (e.kind === "kicked") return "kicked";
  if (e.kind === "sessionEnded") return "ended";
  return "failed";
}

function ModList(p) {
  if (p.items.length === 0) return null;
  return h(
    "div",
    { style: { marginBottom: "10rem" } },
    h("div", { style: merge(S.row, { marginBottom: "4rem" }) }, h(Dot, { color: p.color }), h("div", { style: { fontSize: FS.m, textTransform: "uppercase", color: p.color } }, p.title + " (" + p.items.length + ")")),
    h(
      "div",
      { style: { maxHeight: "140rem", overflowY: "auto", paddingLeft: "18rem" } },
      p.items.map(function (m, i) {
        return h(
          "div",
          { key: i, style: { display: "flex", padding: "3rem 0", borderTop: i === 0 ? "none" : "1rem solid", borderColor: T.divider, fontSize: FS.s } },
          h("div", { style: { flex: 1 } }, m.name || "?"),
          h("div", { style: { color: T.dim, marginLeft: "10rem" } }, p.versions ? t("st.ui.end.mods.versionLine", { HOST: m.host || "?", YOU: m.you || "?" }) : m.version || "")
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
  const canRetry = copy !== "kicked" && copy !== "ended" && copy !== "mods" && copy !== "game" && copy !== "build";
  const dismiss = function () {
    setStore({ dismissedEnd: e.seq });
    send("dismissEnd");
  };
  const retry = function () {
    if (needsPassword && password.length === 0) return;
    setStore({ dismissedEnd: e.seq });
    send("retryJoin", needsPassword ? password : s.keepPassword || "");
  };
  const isMods = copy === "mods" && (list(e.missing).length + list(e.extra).length + list(e.versions).length > 0);
  return h(
    "div",
    { style: merge(S.screenBackdrop, { background: "rgba(4, 8, 14, 0.82)", zIndex: 2100 }) },
    h(
      "div",
      { style: merge(S.screenPos, { width: isMods ? "640rem" : "540rem", zIndex: 2101 }) },
      h(
        "div",
        { style: merge(S.fbPanel, { width: "100%" }) },
        h(
          "div",
          { style: merge(S.fbHeader, { minHeight: "52rem", color: T.bad }) },
          h("div", { style: S.headerRow }, h("div", { style: { width: "24rem", height: "24rem", borderRadius: "12rem", background: T.bad, color: "#ffffff", display: "flex", alignItems: "center", justifyContent: "center", fontWeight: "bold", marginRight: "10rem" } }, "!"), h("div", { style: S.headerTitle }, title))
        ),
        h(
          "div",
          { style: merge(S.fbContent, { padding: "14rem 22rem 18rem 22rem" }) },
          h("div", { style: { fontSize: FS.m, marginBottom: "12rem", lineHeight: 1.4 } }, t("st.ui.end.text." + copy)),
          isMods
            ? h(
                "div",
                null,
                h(ModList, { title: t("st.ui.end.mods.missing"), color: T.good, items: list(e.missing) }),
                h(ModList, { title: t("st.ui.end.mods.extra"), color: T.bad, items: list(e.extra) }),
                h(ModList, { title: t("st.ui.end.mods.versions"), color: T.warn, items: list(e.versions), versions: true })
              )
            : null,
          copy === "game" && e.hostGame ? h("div", { style: merge(S.mono, { marginBottom: "12rem" }) }, t("st.ui.end.versions", { HOST: e.hostGame, YOU: e.yourGame || "?" })) : null,
          copy === "build" && e.hostProto ? h("div", { style: merge(S.mono, { marginBottom: "12rem" }) }, t("st.ui.end.versions", { HOST: e.hostProto, YOU: e.yourProto || "?" })) : null,
          needsPassword
            ? h(Labeled, { label: t("st.ui.join.password") }, h(Field, { value: password, onChange: setPassword, onEnter: retry, password: true, placeholder: t("st.ui.join.passwordPlaceholder"), title: t("st.ui.join.password") }))
            : null,
          e.text && !isMods ? h("div", { style: { marginTop: "6rem", padding: "8rem 10rem", background: "rgba(0, 0, 0, 0.25)", borderRadius: T.radius } }, h("div", { style: S.label }, t("st.ui.end.details")), h("div", { style: merge(S.hint, { userSelect: "text" }) }, e.text)) : null,
          h(
            "div",
            { style: { display: "flex", alignItems: "center", marginTop: "16rem" } },
            h("div", { style: S.link, onClick: function () { send("copyText", reportText(e, title)); flash.fire(); } }, flash.on ? t("st.ui.end.copied") : t("st.ui.end.copy")),
            h("div", { style: { flex: 1 } }),
            h(Btn, { onClick: dismiss }, t("st.ui.end.back")),
            canRetry ? h(Btn, { kind: "primary", disabled: needsPassword && password.length === 0, onClick: retry, style: { marginLeft: "10rem" } }, needsPassword ? t("st.ui.end.retryPassword") : t("st.ui.end.retry")) : null
          )
        )
      )
    )
  );
}

// --------------------------------------------------------------------------------------------- chat overlay

// first time each chat line (by its session-independent sequence number) was seen, for the fade-out
const seenAt = {};
const FADE_AFTER_MS = 10000;

function anotherInputFocused() {
  try {
    const el = document.activeElement;
    return !!el && (el.tagName === "INPUT" || el.tagName === "TEXTAREA");
  } catch (e) {
    return false;
  }
}

// Bottom-left chat: recent lines fade out after 10 s; Enter opens the input (the game ignores the keyboard while it has
// focus), Enter sends and closes, Esc closes.
function ChatOverlay(p) {
  const lines = list(p.state.chat);
  const [text, setText] = React.useState("");
  const [typingHere, setTypingHere] = React.useState(false);
  const tick = React.useState(0)[1];
  const inputRef = React.useRef(null);
  const now = Date.now();
  lines.forEach(function (l) {
    if (l && l.seq !== undefined && seenAt[l.seq] === undefined) seenAt[l.seq] = now;
  });
  React.useEffect(function () {
    const onKey = function (e) {
      if (keyOf(e) !== "Enter" || store.typing > 0 || anotherInputFocused() || !inputRef.current) return;
      if (e.preventDefault) e.preventDefault();
      try {
        inputRef.current.focus();
      } catch (err) {
        // the input is not mounted
      }
    };
    window.addEventListener("keydown", onKey);
    const timer = setInterval(function () {
      tick(function (n) {
        return n + 1;
      });
    }, 1000);
    return function () {
      window.removeEventListener("keydown", onKey);
      clearInterval(timer);
    };
  }, []);
  const recent = lines.slice(-(typingHere ? 14 : 8)).filter(function (l) {
    return typingHere || now - (seenAt[l.seq] || 0) < FADE_AFTER_MS + 1500;
  });
  const close = function () {
    setText("");
    try {
      if (inputRef.current) inputRef.current.blur();
    } catch (e) {
      // already closed
    }
  };
  const sendLine = function () {
    if (text.trim().length > 0) send("chat", text);
    close();
  };
  return h(
    "div",
    { style: { position: "absolute", left: "10rem", bottom: "140rem", width: "460rem", pointerEvents: "none", zIndex: 900 } },
    h(
      "div",
      { style: { padding: typingHere ? "8rem 10rem" : "0", borderRadius: T.radius, background: typingHere ? T.panelDark : "transparent", backdropFilter: typingHere ? T.blur : "none" } },
      recent.map(function (l) {
        const fresh = typingHere || now - (seenAt[l.seq] || 0) < FADE_AFTER_MS;
        return h(
          "div",
          { key: l.seq, style: { opacity: fresh ? 1 : 0, transition: "opacity 1s", padding: typingHere ? "0" : "2rem 8rem", marginBottom: "2rem", borderRadius: "3rem", background: typingHere ? "transparent" : "rgba(10, 16, 26, 0.55)", textShadow: "0 1rem 2rem rgba(0, 0, 0, 0.8)" } },
          h(ChatLine, { line: l, size: FS.m })
        );
      })
    ),
    h(
      "div",
      { style: { marginTop: "4rem", pointerEvents: "auto", opacity: typingHere ? 1 : 0, height: typingHere ? "auto" : "0rem", overflow: "hidden" } },
      h(Field, {
        inputRef: inputRef,
        value: text,
        onChange: setText,
        onEnter: sendLine,
        onEscape: close,
        onFocus: function () { setTypingHere(true); },
        onBlur: function () { setTypingHere(false); },
        placeholder: t("st.ui.chat.overlayPlaceholder"),
        title: t("st.ui.chat.title"),
        max: 500,
      })
    ),
    !typingHere && recent.length > 0
      ? h("div", { style: { color: T.dimmer, fontSize: FS.xs, marginTop: "2rem", pointerEvents: "auto", cursor: "pointer" }, onClick: function () { if (inputRef.current) inputRef.current.focus(); } }, t("st.ui.chat.hint"))
      : null
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
            style: {
              position: "absolute",
              left: (Number(tag.x) * 100).toFixed(2) + "%",
              top: (Number(tag.y) * 100).toFixed(2) + "%",
              transform: "translate(-50%, -130%)",
              padding: "2rem 9rem",
              borderRadius: "11rem",
              background: "rgba(16, 22, 34, 0.82)",
              border: "2rem solid",
              borderColor: tag.color || T.accent,
              color: T.text,
              fontSize: FS.s,
              whiteSpace: "nowrap",
            },
          },
          h("span", { style: { color: tag.color || T.text, fontWeight: "bold" } }, tag.name),
          tag.activity ? h("span", { style: { color: T.dim } }, " · " + tag.activity) : null
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
    { style: { position: "absolute", right: "-4rem", top: "-4rem", minWidth: "18rem", height: "18rem", padding: "0 4rem", borderRadius: "9rem", background: color, color: "rgba(12, 26, 37, 0.9)", fontSize: FS.xs, fontWeight: "bold", display: "flex", alignItems: "center", justifyContent: "center", pointerEvents: "none" } },
    mode === "Joining" ? "…" : mode === "Recovering" ? "!" : String(count)
  );
}

function FallbackRoundButton(p) {
  return h(
    "div",
    {
      style: { width: "40rem", height: "40rem", borderRadius: "6rem", display: "flex", alignItems: "center", justifyContent: "center", background: p.selected ? T.selected : T.panelDark, backdropFilter: T.blur, cursor: "pointer", pointerEvents: "auto" },
      onClick: p.onSelect,
    },
    h("img", { src: ICON_DATA, style: { width: "26rem", height: "26rem" } })
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
  const photo = usePhotoMode();
  React.useEffect(function () {
    store.topLeftMounted = true;
    emit();
    return function () {
      store.topLeftMounted = false;
    };
  }, []);
  if (photo) return null;
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
  const color = statusColor(mode) || T.dimmer;
  return h(
    "div",
    {
      style: { position: "absolute", left: "10rem", top: p.menu ? "10rem" : "100rem", height: "36rem", padding: "0 14rem", display: "flex", alignItems: "center", borderRadius: "18rem", background: T.panelDark, backdropFilter: T.blur, color: T.text, fontSize: FS.m, cursor: "pointer", pointerEvents: "auto", zIndex: 1000 },
      onClick: p.onClick,
    },
    h("img", { src: ICON_DATA, style: { width: "20rem", height: "20rem", marginRight: "8rem" } }),
    h(Dot, { color: color, size: 8 }),
    mode === "None" ? t("st.ui.menu.multiplayer") : mode === "Joining" ? t("st.ui.join.overlayTitleUnknown") : mode === "Recovering" ? t("st.ui.menu.multiplayer") + " · " + t("st.ui.recovery.badge") : t("st.ui.menu.multiplayer") + " · " + list(s.players).length
  );
}

// ------------------------------------------------------------------------------------------------- roots

function GameRoot() {
  useI18n();
  const s = useJson(stateBinding);
  const st = useStore();
  const photo = usePhotoMode();
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
    setStore({ open: false });
  };
  return h(
    "div",
    { style: S.layer },
    live && !photo ? h(NameTags, null) : null, // presence branch: under the panel
    live && !photo ? h(ChatOverlay, { state: s }) : null,
    !photo && (!topLeftRegistered || (noTopLeft && !st.topLeftMounted)) ? h(FallbackToggle, { state: s, onClick: function () { setStore({ open: !store.open }); } }) : null,
    st.open && !photo ? h("div", { style: S.gamePanelPos }, h(PanelShell, { title: panelTitle(s), onClose: close, width: "420rem", maxHeight: "78vh" }, h(PanelContent, { state: s, inGame: true }))) : null,
    h(JoinOverlay, { state: s }),
    h(EndOverlay, { state: s })
  );
}

function MenuRoot() {
  useI18n();
  const s = useJson(stateBinding);
  const st = useStore();
  const close = function () {
    setStore({ menuOpen: false });
  };
  return h(
    "div",
    { style: S.layer },
    menuButtonInstalled ? null : h(FallbackToggle, { state: s, menu: true, onClick: function () { setStore({ menuOpen: !store.menuOpen }); } }),
    st.menuOpen ? h("div", { style: S.screenBackdrop, onClick: close }) : null,
    st.menuOpen ? h("div", { style: S.screenPos }, h(PanelShell, { title: panelTitle(s), onClose: close, width: "600rem", maxHeight: "82vh" }, h(PanelContent, { state: s, inGame: false }))) : null,
    h(JoinOverlay, { state: s }),
    h(EndOverlay, { state: s })
  );
}

// ---- title-screen "Multiplayer" entry (in the vanilla main menu list, between Load Game and Editor) ----

function MenuEntry() {
  useI18n();
  const label = t("st.ui.menu.multiplayer");
  // never break the vanilla menu: a failing game button falls back to a plain entry in the same place
  return Safe(h(V.LabeledIconButton, { tinted: true, src: ICON_GLYPH, theme: V.menuTheme, onSelect: openPanel }, label), function () {
    return h(
      "div",
      { style: { height: "56rem", display: "flex", alignItems: "center", padding: "0 20rem", marginTop: "12rem", borderRadius: T.radius, background: "var(--menuPanel1, rgba(16, 19, 29, 0.3))", color: "var(--menuText1Normal, rgba(255, 255, 255, 0.8))", fontSize: FS.xl, textTransform: "uppercase", cursor: "pointer" }, onClick: openPanel },
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
