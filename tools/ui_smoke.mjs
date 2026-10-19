// UI smoke test: renders the module's components with a stub React for many session states, with and without the
// game's own components (cs2/ui, registry modules), and fails on
//  - an exception while importing, registering, rendering or running an event handler,
//  - a binding or trigger name the C# side (src/SkylineTogether/UI/*UISystem.cs) does not declare,
//  - a text key that renders raw (missing from the module's English table),
//  - a state whose English render lacks a text it must show, or shows one it must not (EXPECT below),
//  - English texts that differ from src/SkylineTogether/Data/locale.en-US.json, keys missing in another locale table
//    or in locale-keys.generated.json,
//  - a main menu entry, top-left button or root that was not registered,
//  - a plain object rendered as a child (React throws on that: a binding field that was not a string),
//  - a window listener or store subscription that an effect adds and its cleanup does not remove,
//  - a var(--token) the game's stylesheet does not define (only when the game's index.css is found: env GameDir or the
//    default Steam folder; skipped otherwise).
// Usage: node tools/ui_smoke.mjs [--html <dir>] [file:///<absolute path to ui/SkylineTogether.mjs>]
//   --html <dir>  also writes one HTML snapshot per state and surface (built-in look, every style inline, the game's
//                 design tokens and fonts when the game is installed) plus an index.html, to look at the layout in a
//                 normal browser. Gameface is not Chrome: treat the snapshots as a layout check, not as the real look.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const repo = new URL("../", import.meta.url);
const read = (rel) => fs.readFileSync(fileURLToPath(new URL(rel, repo)), "utf8");
let failed = 0;
const fail = (msg) => { failed++; console.log("FAIL " + msg); };
const args = process.argv.slice(2);
const htmlAt = args.indexOf("--html");
const htmlDir = htmlAt >= 0 ? args.splice(htmlAt, 2)[1] : null;
if (htmlAt >= 0 && !htmlDir) { console.log("--html needs a folder"); process.exit(2); }

// the game's stylesheet (for the token check and the snapshots), when the game is installed here
const gameUi = (() => {
  const dirs = [process.env.GameDir, "C:/Program Files (x86)/Steam/steamapps/common/Cities Skylines II"].filter(Boolean);
  for (const d of dirs) {
    const ui = path.join(d, "Cities2_Data", "Content", "Game", "UI");
    if (fs.existsSync(path.join(ui, "index.css"))) return ui;
  }
  return null;
})();
const gameCss = gameUi ? fs.readFileSync(path.join(gameUi, "index.css"), "utf8") : "";

// ------------------------------------------------------------------------------------------- C# declarations
// every UI system of the module's group (SessionUISystem, ReplayUISystem for solo testing, InviteUISystem for Steam
// invites: its bindings may still be unused by the panel, so only their declaration is checked)
const cs = read("src/SkylineTogether/UI/SessionUISystem.cs") + "\n" + read("src/SkylineTogether/UI/ReplayUISystem.cs") + "\n" + read("src/SkylineTogether/UI/InviteUISystem.cs");
const csBindings = new Set([...cs.matchAll(/GetterValueBinding<[^>]+>\(Group, "([^"]+)"/g)].map((m) => m[1]));
const csTriggers = new Set([...cs.matchAll(/TriggerBinding(?:<[^>]*>)?\(Group, "([^"]+)"/g)].map((m) => m[1]));
for (const b of ["state", "join", "net", "presence", "perf"]) if (!csBindings.has(b)) fail("SessionUISystem lost the binding " + b);
for (const t of ["host", "join", "leave", "chat", "kick", "saveHostSettings", "writeDiagnostics", "toggleNetHud", "togglePerfHud", "saveHostOptions", "retryJoin", "dismissEnd", "copySteamId", "copyText", "inviteFriends",
  "ban", "toggleCoHost", "unban", "setFriendsOnly", "setNewPlayersViewer", "resync", "dismissResync", "joinAnyway", "setAllowJoinWithDifferentMods"]) {
  if (!csTriggers.has(t)) fail("SessionUISystem lost the trigger " + t);
}
// the invite contract the panel consumes
for (const b of ["friends", "overlayAvailable"]) if (!csBindings.has(b)) fail("InviteUISystem lost the binding " + b);
for (const t of ["inviteFriend", "openInviteOverlay", "refreshFriends"]) if (!csTriggers.has(t)) fail("InviteUISystem lost the trigger " + t);

// ------------------------------------------------------------------------------------------------ stubs
let values = {};
let forceTrue = false;
let runEffects = false;
const triggers = [];
const handlers = [];
const cleanups = [];
globalThis.window = globalThis;
window.__skylineTogetherSmoke = {};
// window key listeners (effects add them: Enter opens the chat, Escape closes panels); the effects pass presses keys
const winListeners = new Map();
window.addEventListener = (type, fn) => { if (!winListeners.has(type)) winListeners.set(type, new Set()); winListeners.get(type).add(fn); };
window.removeEventListener = (type, fn) => { if (winListeners.has(type)) winListeners.get(type).delete(fn); };
class Component { constructor(props) { this.props = props; this.state = {}; } setState() {} }
window.React = {
  createElement(type, props, ...children) { return { type, props: props || {}, children }; },
  // the forced passes start every flag true and every text non-empty, so guarded paths (send, retry) run too
  useState(init) { return [forceTrue && init === false ? true : forceTrue && init === "" ? "smoke" : typeof init === "function" ? init() : init, () => {}]; },
  useRef(init) { return { current: init === undefined ? null : init }; },
  // the effects pass runs every effect once after the render and its cleanup after the key presses
  useEffect(fn) {
    if (!runEffects) return;
    try { const c = fn(); if (typeof c === "function") cleanups.push(c); }
    catch (e) { fail("effect: " + (e && e.stack)); }
  },
  Component,
  Children: { toArray(c) { return (Array.isArray(c) ? c.flat(Infinity) : [c]).filter((x) => x != null && x !== false); } },
};
const unknownBindings = new Set();
window["cs2/api"] = {
  bindValue(g, n, f) {
    if (g !== "skylineTogether" || !csBindings.has(n)) unknownBindings.add(g + "." + n);
    return { g, n, f };
  },
  useValue(b) { return b && values[b.n] !== undefined ? values[b.n] : b ? b.f : undefined; },
  trigger(...a) { triggers.push(a); },
};
const vanillaUi = {
  Button(p) { return { type: "vanilla-button", props: p, children: [p.children] }; },
  Panel(p) { return { type: "vanilla-panel", props: p, children: [p.header, p.children] }; },
  Tooltip(p) { return { type: "vanilla-tooltip", props: p, children: [p.tooltip, p.children] }; },
  Scrollable(p) { return { type: "vanilla-scrollable", props: p, children: [p.children] }; },
  FloatingButton(p) { return { type: "vanilla-floating", props: p, children: [] }; },
  MenuButton(p) { return { type: "vanilla-menu-button", props: p, children: [p.children] }; },
};
window["cs2/ui"] = vanillaUi;
const de = JSON.parse(read("src/SkylineTogether/Data/locale.de-DE.json"));
let locale = null;
window["cs2/l10n"] = { useLocalization() { return { translate(id, fallback) { return locale && locale[id] !== undefined ? locale[id] : fallback; } }; } };

const MAIN_MENU = "game-ui/menu/components/main-menu-screen/main-menu-screen.tsx";
// fake class maps of the vanilla themes the module borrows (the real names change with every game build)
const themeStub = (keys) => Object.fromEntries(keys.map((k) => [k, k + "_x1"]));
const registry = new Map([
  [MAIN_MENU, { MainMenuNavigation: (p) => ({ type: "div", props: {}, children: [p.children] }), MainMenuScreen: (p) => ({ type: "vanilla-main-menu", props: p, children: [] }), ModsUIButton() {} }],
  ["game-ui/menu/components/shared/sub-screen/sub-screen.tsx", { SubScreen: (p) => ({ type: "vanilla-sub-screen", props: p, children: [p.title, p.children] }) }],
  ["game-ui/menu/components/options-screen/options-screen.module.scss", { classes: themeStub(["content", "menu", "scrollable", "item", "primary", "detail"]) }],
  ["game-ui/menu/components/options-screen/option-page/option-page.module.scss", { classes: themeStub(["optionPage", "mainColumn", "scrollable", "infoColumn", "infoTitle", "infoDescription"]) }],
  ["game-ui/menu/components/options-screen/option-page/option-page-header.module.scss", { classes: themeStub(["optionPageHeader", "tab", "first", "last"]) }],
  ["game-ui/menu/widgets/field/row-field.module.scss", { classes: themeStub(["field", "label"]) }],
  ["game-ui/menu/widgets/string-input-field/string-input-field.module.scss", { classes: themeStub(["stringField"]) }],
  ["game-ui/menu/widgets/toggle-field/toggle-field.module.scss", { classes: themeStub(["toggle"]) }],
  ["game-ui/menu/themes/secondary-button.module.scss", { classes: themeStub(["button", "icon", "label"]) }],
  ["game-ui/common/input/button/themes/dialog-button.module.scss", { classes: themeStub(["button", "negative"]) }],
  ["game-ui/common/input/button/themes/round-highlight-button.module.scss", { classes: themeStub(["button"]) }],
  ["game-ui/common/tabs/tabs.module.scss", { classes: themeStub(["tabBar", "tab"]) }],
  ["game-ui/game/components/selected-info-panel/shared-components/info-section/info-section.module.scss", { classes: themeStub(["infoSection", "content"]) }],
  ["game-ui/common/image/tinted-icon.module.scss", { classes: themeStub(["tintedIcon"]) }],
  ["game-ui/common/input/button/labeled-icon-button.tsx", { LabeledIconButton: (p) => ({ type: "vanilla-labeled", props: p, children: [p.children] }) }],
  ["game-ui/menu/themes/menu-item.module.scss", { classes: { button: "button_uFa", icon: "icon_zVk", label: "label_NLv" } }],
  ["game-ui/common/input/toggle/checkbox/checkbox.tsx", { Checkbox: (p) => ({ type: "vanilla-checkbox", props: p, children: [] }) }],
  ["game-ui/common/focus/focus-key.ts", { FOCUS_DISABLED: { debugName: "disabled" } }],
]);

const target = args[0] || new URL("../ui/SkylineTogether.mjs", import.meta.url).href;
const mod = await import(target);
const smoke = window.__skylineTogetherSmoke;
const appended = {};
const extended = {};
mod.default({
  registry,
  append(targetName, component) {
    if (typeof component !== "function") throw new Error("append(" + targetName + ") without a component");
    appended[targetName] = component;
  },
  extend(path, name, cb) {
    if (!registry.has(path) || !registry.get(path)[name]) throw new Error("extend of a missing module export " + path + "@" + name);
    extended[path + "@" + name] = cb(registry.get(path)[name]);
  },
  get(path, name) {
    const m = registry.get(path);
    if (!m || !m[name]) throw new Error("Module " + path + "@" + name + " was not found.");
    return m[name];
  },
});
for (const slot of ["Game", "Menu", "GameTopLeft"]) if (!appended[slot]) fail("nothing appended to " + slot);
if (!extended[MAIN_MENU + "@MainMenuNavigation"]) fail("the main menu entry was not installed");
// the vanilla MainMenuScreen must NOT be replaced: in game that rendered nothing (the page is our own layer in "Menu")
if (extended[MAIN_MENU + "@MainMenuScreen"]) fail("the main menu screen was replaced (renders nothing in game); use the Menu layer");
if (!smoke.store) fail("the smoke hook is missing");

// ---------------------------------------------------------------------------------------------- renderer
const rawKeys = new Set();
let renderLabel = "";
// every text rendered for the current state (EXPECT)
let caseTexts = new Set();
function render(node) {
  if (node == null || typeof node === "boolean") return 0;
  if (typeof node === "string" || typeof node === "number") {
    if (/^st\.(ui|toast|chat|options)\./.test(String(node))) rawKeys.add(String(node));
    caseTexts.add(String(node));
    return 0;
  }
  if (Array.isArray(node)) return node.reduce((n, c) => n + render(c), 0);
  if (typeof node !== "object" || node.type === undefined) {
    fail(renderLabel + ": a " + typeof node + " rendered as a child (React throws): " + String(JSON.stringify(node)).slice(0, 80));
    return 0;
  }
  if (typeof node.type === "function") {
    const props = Object.assign({}, node.props, { children: node.children.length === 1 ? node.children[0] : node.children });
    if (node.type.prototype instanceof Component) {
      const inst = new node.type(props);
      return render(inst.render());
    }
    return render(node.type(props));
  }
  let n = 1;
  for (const c of node.children || []) n += render(c);
  const p = node.props || {};
  for (const h of ["onClick", "onSelect", "onChange", "onKeyDown", "onBlur", "onFocus", "onMouseEnter", "onMouseLeave"]) {
    if (typeof p[h] === "function") handlers.push([h, p[h], node.type]);
  }
  return n;
}
function runHandlers(label) {
  for (const [name, fn, type] of handlers.splice(0)) {
    try {
      if (name === "onChange") fn(type === "vanilla-checkbox" ? true : { target: { value: "76561190000000001" } });
      else if (name === "onKeyDown") { fn({ key: "Enter", stopPropagation() {}, target: { blur() {} } }); fn({ key: "Escape", stopPropagation() {}, target: { blur() {} } }); }
      else fn({ stopPropagation() {}, preventDefault() {} });
    } catch (e) { fail(label + " handler " + name + ": " + (e && e.stack)); }
  }
}

// ----------------------------------------------------------------------------------------------- cases
const base = { steam: true, steamId: "76561190000000000", players: [], chat: [], canHost: true, canJoin: true, keepPassword: "\u0001keep",
  playerName: "Me", sessionNameSetting: "", hasPassword: false, joinTarget: "", lastEnd: "", status: "", sync: "", speed: "", sessionName: "", progress: 0, directPort: 0,
  inGame: true, maxPlayers: 8, allowDirect: false, requireSameMods: true, requireSameAssets: false, portSetting: 27015, canInvite: false, inviteSteamId: "", hold: false, stage: "",
  speedValue: 1, speedPaused: false, speedHeld: false, end: null, build: "2f076aad4234", friendsOnly: false, localRole: "", resync: "", bans: [], allowModOverride: true };
// bans, co-hosts, removal after loading, resync offers
const bans = [{ key: "steam:76561190000000007", name: "Griefer", kind: "steam", id: "76561190000000007" }, { key: "ip:203.0.113.9", name: "", kind: "ip", id: "203.0.113.9" }];
const coPlayers = [{ id: 0, name: "Host", host: true, you: false, state: "Live", ping: 40, progress: 1, color: "#f2b84b", role: "host" },
  { id: 1, name: "Me", host: false, you: true, state: "Live", ping: 0, progress: 1, color: "#3fb6e8", role: "cohost" },
  { id: 2, name: "Bo", host: false, you: false, state: "Live", ping: 155, progress: 1, color: "#5fcf7a", role: "builder" },
  { id: 3, name: "Cy", host: false, you: false, state: "Live", ping: 80, progress: 1, color: "#ef6a6a", role: "viewer" },
  { id: 4, name: "Di", host: false, you: false, state: "Live", ping: 60, progress: 1, color: "#a07af0", role: "cohost" }];
const tags = { tags: [{ id: 1, name: "Anna", color: "#3fb6e8", x: 0.4, y: 0.6, on: true, activity: "building Small Road" }, { id: 2, name: "Bo", color: "#5fcf7a", x: 2, y: -1, on: false, activity: "" }] };
const net = { show: true, text: "ping 40 ms · in 12.0 KB/s · out 3.1 KB/s" };
const players = [{ id: 0, name: "Host", host: true, you: true, state: "Live", ping: 0, progress: 1, color: "#f2b84b", role: "host" },
  { id: 1, name: "Anna", host: false, you: false, state: "Downloading", ping: 30, progress: 0.4, color: "#3fb6e8", role: "builder" },
  { id: 2, name: "Bo", host: false, you: false, state: "Live", ping: 155, progress: 1, color: "#5fcf7a", role: "builder" },
  { id: 3, name: "Cy", host: false, you: false, state: "WaitingForSnapshot", ping: 300, progress: 0, color: "#ef6a6a", role: "viewer" }];
const chat = [{ name: "", text: "You are hosting", system: true, seq: 1, you: false, color: "" }, { name: "Anna", text: "yo", system: false, seq: 2, you: false, color: "#3fb6e8" }];
const mods = { seq: 3, kind: "refused", refuse: "Mismatch", text: "Your mods differ from the host's.", target: "76561190000000009", reportKind: "mods",
  missing: [{ name: "Anarchy", version: "3.1", id: "300" }], extra: [{ name: "Traffic", version: "2.0", id: "200" }], versions: [{ name: "Move It", host: "1.0", you: "0.9" }],
  hostGame: "", yourGame: "", hostProto: "", yourProto: "" };
const end = (kind, refuse, extra) => Object.assign({ seq: 7, kind, refuse: refuse || "", text: "detail from the host", target: "1.2.3.4:27015", reportKind: "", missing: [], extra: [], versions: [], hostGame: "", yourGame: "", hostProto: "", yourProto: "" }, extra);
const join = (stage, extra) => Object.assign({ active: true, stage, session: "Anna's city", target: "76561190000000009", done: 0, total: 0, bps: 0, eta: -1, load: 0, progress: 0 }, extra);
const friends = [{ id: "76561190000000001", name: "Anna", state: "ingame", inSession: true }, { id: "76561190000000002", name: "Bo", state: "online", inSession: false },
  { id: "76561190000000003", name: "Cy", state: "away", inSession: false }, { id: "76561190000000004", name: "Dee", state: "offline", inSession: false }, { id: "76561190000000005", name: "", state: "weird", inSession: false }];
// the performance overlay as PerfOverlay.cs writes it
const perf = { show: true, frame: { ms: 3.42, budget: 10, over: 2 }, features: [{ n: "apply", p: 0, avg: 1.2, p95: 4.1, cap: 6, clip: 0 }, { n: "value scan", p: 1, avg: 0.9, p95: 1.0, cap: 1, clip: 12 },
  { n: "maps+stats", p: 3, avg: 0, p95: 0, cap: 0, clip: 0 }], peers: [{ name: "Anna", in: 1.5, out: 22.3, ping: 40, queued: 0, deferred: 120 }, { name: "", in: "x", out: null, ping: -1, queued: 0, deferred: -1 }], queue: 80 };
const cases = {
  lobbyGame: { state: { ...base, mode: "None" } },
  lobbyMenu: { state: { ...base, mode: "None", inGame: false, canHost: false, hasPassword: true, allowDirect: true } },
  nosteam: { state: { ...base, mode: "None", steam: false, canHost: false, canJoin: false } },
  hosting: { state: { ...base, mode: "Hosting", hold: true, status: "A player is joining – the game is paused until they are in. Anna is downloading the city (40 %).", sessionName: "X", directPort: 27015,
    sync: "12,345 objects in sync; verifying 9,000 objects every 30 s, 0 repairs sent; 812 movers", speed: "paused while a player joins", speedHeld: true, canInvite: true, inviteSteamId: "76561190000000000",
    players, chat }, net, presence: tags, friends, overlayAvailable: true, perf },
  hostingNoHud: { state: { ...base, mode: "Hosting", status: "Hosting", sessionName: "X", speed: "2×", speedValue: 2, canInvite: true }, net: { show: false, text: "" }, presence: { tags: [] }, friends: [], overlayAvailable: false },
  // invites without the InviteUISystem values (older C#, or not yet sent): only the overlay button
  hostingNoFriends: { state: { ...base, mode: "Hosting", status: "Hosting", sessionName: "X", canInvite: true, players, chat: [] } },
  clientNoOverlay: { state: { ...base, mode: "Client", status: "Connected", sessionName: "X", canInvite: true, players, chat }, overlayAvailable: false },
  joiningConnect: { state: { ...base, mode: "Joining", stage: "connecting", status: "Connecting…" }, join: join("connecting") },
  joiningWait: { state: { ...base, mode: "Joining", stage: "waiting" }, join: join("waiting") },
  joiningDownload: { state: { ...base, mode: "Joining", stage: "downloading", progress: 0.2 }, join: join("downloading", { done: 41943040, total: 104857600, bps: 3145728, eta: 20, progress: 0.2 }) },
  joiningDownloadStart: { state: { ...base, mode: "Joining", stage: "downloading" }, join: join("downloading") },
  joiningLoad: { state: { ...base, mode: "Joining", stage: "loading", progress: 0.75 }, join: join("loading", { load: 0.5, progress: 0.75, eta: 125 }) },
  client: { state: { ...base, mode: "Client", status: "Connected", sessionName: "X", speed: "1×", canInvite: true, inviteSteamId: "76561190000000009",
    sync: "12,345 objects in sync, terrain: 4 tiles applied; verified 9,000 objects, 0 repairs; icons match; movers +10 -2, 140 in motion; clock follows the host",
    players: [{ id: 0, name: "Host", host: true, you: false, state: "Live", ping: 40, progress: 1, color: "#f2b84b", role: "host" }, { id: 1, name: "Me", host: false, you: true, state: "Live", ping: 0, progress: 1, color: "#3fb6e8", role: "builder" }], chat }, net, presence: tags },
  endMods: { state: { ...base, mode: "None", end: mods } },
  // "join anyway": the host lets players join with different mods (the lists stay, plus the button and the warning)
  endModsOverridable: { state: { ...base, mode: "None", end: Object.assign({}, mods, { overridable: true }) } },
  endModsRequired: { state: { ...base, mode: "None", end: Object.assign({}, mods, { overridable: false }) } },
  endAssetsOverridable: { state: { ...base, mode: "None", requireSameAssets: true, end: end("refused", "Mismatch", { reportKind: "assets", overridable: true, missing: [{ name: "Bus Stops", version: "1.0", id: "75426" }],
    extra: [{ name: "Fences", version: "", id: "90001" }], versions: [{ name: "Trees", id: "80010", host: "2.1", you: "2.2" }] }) } },
  endLongModsOverridable: { state: { ...base, mode: "None", end: Object.assign({}, mods, { overridable: true, text: "Your mods differ from the host's. ".repeat(40),
    missing: Array.from({ length: 200 }, (_, i) => ({ name: "A missing mod with a very long display name number " + i + " and a suffix that does not end", version: "1.2.3-beta+" + "x".repeat(20), id: String(1000 + i) })),
    extra: Array.from({ length: 120 }, (_, i) => ({ name: "Extra_mod_without_any_spaces_in_its_name_" + i + "_".repeat(30), version: "", id: "" })),
    versions: Array.from({ length: 80 }, (_, i) => ({ name: "Version mod " + i, host: "10.20.30." + i, you: "9.9.9-" + "rc".repeat(10) })) }) } },
  // never a button for DLC, for a non-mismatch refusal or for a value that is not exactly true
  endContentOverridable: { state: { ...base, mode: "None", end: end("refused", "Mismatch", { reportKind: "content", overridable: true, missing: [{ name: "Beach Properties", version: "", id: "" }] }) } },
  endFullOverridable: { state: { ...base, mode: "None", end: end("refused", "Full", { overridable: true }) } },
  endOverridableHostile: { state: { ...base, mode: "None", end: Object.assign({}, mods, { overridable: "yes" }) } },
  lobbyNoOverride: { state: { ...base, mode: "None", allowModOverride: false } },
  // mods that store data in the host's city: never "join anyway" (even if a value claims overridable), the reason per mod
  endModsCityData: { state: { ...base, mode: "None", end: Object.assign({}, mods, { overridable: true, missing: [], extra: [], versions: [],
    required: [{ name: "ZoningToolkit", version: "1.0.0.0", id: "", inCity: true }, { name: "AreaBucket", version: "1.0.0.0", id: "", inCity: false }] }) } },
  endModsCityDataRest: { state: { ...base, mode: "None", end: Object.assign({}, mods, { overridable: false, restOverridable: true,
    required: [{ name: "ZoningToolkit", version: "1.0.0.0", id: "", inCity: true }] }) } },
  endModsCityDataHostile: { state: { ...base, mode: "None", end: Object.assign({}, mods, { required: [null, 3, { name: { n: 1 }, inCity: "yes" }], restOverridable: "yes" }) } },
  // the old multiplayer mod on one side
  endConflictYou: { state: { ...base, mode: "None", end: end("refused", "Mismatch", { reportKind: "conflict", conflictYou: "CS2MultiplayerMod", overridable: true }) } },
  endConflictHost: { state: { ...base, mode: "None", end: end("refused", "Mismatch", { reportKind: "conflict", conflictHost: "CS2MultiplayerMod" }) } },
  lobbyConflictMod: { state: { ...base, mode: "None", conflictMod: "CS2MultiplayerMod 1.0.0.0" } },
  hostingConflictMod: { state: { ...base, mode: "Hosting", status: "Hosting", sessionName: "X", conflictMod: "CS2MultiplayerMod 1.0.0.0", players, chat } },
  lobbyNothingRequired: { state: { ...base, mode: "None", inGame: false, requireSameMods: false, requireSameAssets: false } },
  hostingNoOverride: { state: { ...base, mode: "Hosting", status: "Hosting", sessionName: "X", allowModOverride: false, requireSameAssets: true, players, chat } },
  // the compatibility check: DLC the joiner lacks, asset packs the host requires (lists with Paradox Mods ids)
  endContent: { state: { ...base, mode: "None", end: end("refused", "Mismatch", { reportKind: "content", missing: [{ name: "Beach Properties", version: "", id: "" }], extra: [{ name: "Modern Architecture", version: "", id: "" }] }) } },
  endAssets: { state: { ...base, mode: "None", requireSameAssets: true, end: end("refused", "Mismatch", { reportKind: "assets", missing: [{ name: "Bus Stops", version: "1.0", id: "75426" }],
    extra: [{ name: "Fences", version: "", id: "90001" }], versions: [{ name: "Trees", id: "80010", host: "2.1", you: "2.2" }] }) } },
  endAssetsUnknown: { state: { ...base, mode: "None", end: end("refused", "Mismatch", { reportKind: "assets", text: "The host requires the same asset packs. The asset packs differ (too many to list)." }) } },
  endPassword: { state: { ...base, mode: "None", end: end("refused", "BadPassword") } },
  endPasswordRequired: { state: { ...base, mode: "None", end: end("passwordRequired") } },
  endGame: { state: { ...base, mode: "None", end: end("refused", "Mismatch", { reportKind: "game", hostGame: "1.2.3", yourGame: "1.2.2" }) } },
  endBuild: { state: { ...base, mode: "None", end: end("refused", "ProtocolVersion", { reportKind: "protocol", hostProto: "2", yourProto: "1" }) } },
  endFull: { state: { ...base, mode: "None", end: end("refused", "Full") } },
  endPreparing: { state: { ...base, mode: "None", end: end("refused", "HostPreparing") } },
  endTimeout: { state: { ...base, mode: "None", end: end("timeout") } },
  endLost: { state: { ...base, mode: "None", end: end("connectionLost") } },
  endKicked: { state: { ...base, mode: "None", end: end("kicked") } },
  endBanned: { state: { ...base, mode: "None", end: end("banned") } },
  endRefusedBanned: { state: { ...base, mode: "None", end: end("refused", "Banned") } },
  endNotInvited: { state: { ...base, mode: "None", end: end("refused", "NotInvited") } },
  endClosing: { state: { ...base, mode: "None", end: end("refused", "HostShuttingDown") } },
  endBuildIds: { state: { ...base, mode: "None", end: end("refused", "Mismatch", { reportKind: "build", hostBuild: "2f076aad4234", yourBuild: "0123456789ab" }) } },
  hostingSecurity: { state: { ...base, mode: "Hosting", status: "Hosting", sessionName: "X", hasPassword: true, allowDirect: true, friendsOnly: true, newPlayersViewer: true, localRole: "host", bans,
    players: coPlayers.map((p) => Object.assign({}, p, { you: p.host })), chat }, net, presence: tags },
  lobbyBans: { state: { ...base, mode: "None", bans, friendsOnly: true } },
  lobbyMenuNoPassword: { state: { ...base, mode: "None", inGame: false, hasPassword: false, allowDirect: true, bans } },
  clientCoHost: { state: { ...base, mode: "Client", status: "Connected", sessionName: "X", localRole: "cohost", players: coPlayers, chat,
    resync: "34 of 200 checked areas still differed from the host after repairs, 2 checks in a row" }, net, presence: tags },
  recoveryKicked: { state: { ...base, mode: "Recovering", recovery: { phase: "NeedsChoice", attempt: 0, max: 6, seconds: 0, reason: "The host removed you from the session: Removed by the host", canReconnect: false, hostEnded: true, kicked: true, banned: false, attemptStage: "" } } },
  recoveryBanned: { state: { ...base, mode: "Recovering", recovery: { phase: "NeedsChoice", attempt: 0, max: 6, seconds: 0, reason: "The host removed you from the session and banned you: Banned by the host", canReconnect: false, hostEnded: true, kicked: false, banned: true, attemptStage: "" } } },
  endEnded: { state: { ...base, mode: "None", end: end("sessionEnded") } },
  endFailed: { state: { ...base, mode: "None", end: end("failed") } },
  // recovery after an unexpected disconnect, self-test line, blocked hosting
  recoveryWaiting: { state: { ...base, mode: "Recovering", probes: "Self-test: 30/31 checks passed, 1 warnings", recovery: { phase: "Waiting", attempt: 2, max: 6, seconds: 4, reason: "Lost the connection to the host (timed out).", canReconnect: true, hostEnded: false, attemptStage: "downloading" }, players: [{ id: 0, name: "Host", host: true, you: false, state: "Offline", ping: 0, progress: 1, color: "#fff", role: "host" }] } },
  recoveryAttempt: { state: { ...base, mode: "Recovering", progress: 0.3, recovery: { phase: "Attempting", attempt: 2, max: 6, seconds: 4, reason: "Lost the connection to the host (timed out).", canReconnect: true, hostEnded: false, attemptStage: "downloading" } } },
  recoveryChoice: { state: { ...base, mode: "Recovering", recovery: { phase: "NeedsChoice", attempt: 2, max: 6, seconds: 4, reason: "Lost the connection to the host (timed out).", canReconnect: true, hostEnded: false, attemptStage: "downloading" } } },
  recoveryEnded: { state: { ...base, mode: "Recovering", recovery: { phase: "NeedsChoice", attempt: 2, max: 6, seconds: 4, reason: "Lost the connection to the host (timed out).", canReconnect: true, hostEnded: false, attemptStage: "downloading", hostEnded: true, canReconnect: false } } },
  lobbyBlocked: { state: { ...base, mode: "None", canHost: false, canJoin: false, blocked: "replication: self-test failed: P-CTX", probes: "Self-test: 29/31 checks passed; off: replication" } },
  // solo testing: recordings and a verdict in the lobby, recording while hosting, a replay running
  soloLobby: { state: { ...base, mode: "None" }, replay: { canRecord: true, canReplay: true, recording: false, replaying: false, root: "C:/Users/x/AppData/LocalLow/Colossal Order/Cities Skylines II/ModsData/SkylineTogether/Recordings",
    recordings: [{ id: "20261012-120000 Riverside", city: "Riverside", when: "2026-10-12 12:00", duration: "5:12", mb: "312.4", verdict: "passed" }, { id: "20261012-110000 Riverside", city: "", when: "2026-10-12 11:00", duration: "", mb: "1.0", verdict: "" }],
    verdict: { status: "failed", summary: "Failed: 2 objects differ (NetEdge: 2)", folder: "C:/x/20261012-120000 Riverside" } } },
  soloMenu: { state: { ...base, mode: "None", inGame: false }, replay: { canRecord: false, canReplay: true, recordings: [], verdict: { status: "incomplete", summary: "Incomplete: no checkpoint was completed", folder: "" } } },
  soloRecording: { state: { ...base, mode: "Hosting", status: "Hosting", sessionName: "X (test)", players }, replay: { recording: true, stopping: false, recordStatus: "Recording 1:05 · 40.2 MB · 1 checkpoints", recordings: [] } },
  soloHostIdle: { state: { ...base, mode: "Hosting", status: "Hosting", sessionName: "X" }, replay: { canRecord: true, recording: false } },
  soloReplaying: { state: { ...base, mode: "Client", status: "Connected", sessionName: "X (test)" }, replay: { replaying: true, replayStatus: "Replayed 0:40 of the recording · 1 checkpoints compared · 0 objects differ", progress: 0.3 } },
  soloReplayLoading: { state: { ...base, mode: "Joining", stage: "loading", progress: 0.7 }, join: join("loading", { load: 0.4, progress: 0.7 }), replay: { replaying: true, replayStatus: "Loading the recorded city…", progress: 0.1 } },
  // robustness: partial data, values of the wrong type, huge lists, long, right-to-left and emoji texts
  missingFields: { state: { mode: "Hosting", players: [null, {}, { id: 3 }, { name: "Only a name" }, 7, "x", [1]], chat: [null, {}, { text: "no name" }, { name: "Anna" }, 5], bans: [null, {}, { key: "k" }], end: null, recovery: null },
    presence: { tags: [null, { id: 1 }, { on: true, x: "a", y: null }] }, friends: [null, {}, { id: 5 }], net: { show: true }, replay: { recordings: [null, {}, { id: "r1" }], verdict: {} } },
  hostileTypes: { state: { ...base, mode: "Client", sessionName: { a: 1 }, status: ["x"], sync: { n: 1 }, speed: {}, speedValue: "fast", maxPlayers: "lots", steamId: 76561190000000000, inviteSteamId: { id: 1 }, directPort: "27015",
    progress: "half", build: { b: 1 }, probes: { p: 1 }, blocked: 0, resync: { r: 1 }, localRole: { x: 1 }, canInvite: true,
    players: [{ id: "a", name: { first: "A" }, host: "yes", you: 0, state: { s: 1 }, ping: "fast", progress: "x", color: { c: 1 }, role: { r: 1 } }, { id: 2, name: 42, state: "Downloading", progress: "0.5", ping: "80", role: "admin" }],
    chat: [{ name: ["A"], text: { t: 1 }, system: "no", seq: "1", you: 1, color: 5 }, { name: "B", text: 12, seq: 2 }] },
    net: { show: true, text: { t: 1 } }, presence: { tags: [{ id: 1, name: { n: 1 }, color: [1], x: "0.5", y: 0.5, on: true, activity: { a: 1 } }] },
    perf: { show: true, frame: "x", features: [null, { n: { a: 1 }, avg: {}, cap: "c" }], peers: [{ name: { n: 1 } }, null], queue: {} },
    join: { stage: "downloading", session: { s: 1 }, target: [1], done: "x", total: "y", bps: {}, eta: "z", progress: "q" }, friends: [{ id: { i: 1 }, name: { n: 1 } }, { id: 7, name: 7, state: {} }],
    replay: { recordings: [{ id: { i: 1 } }, { id: "r", city: { c: 1 }, mb: {}, when: [], verdict: { v: 1 } }], verdict: { status: "passed", summary: { s: 1 }, folder: [1] }, recordStatus: {}, replaying: true, replayStatus: { r: 1 }, progress: "p" } },
  hostingHostile: { state: { ...base, mode: "Hosting", maxPlayers: { m: 1 }, portSetting: "port", allowDirect: true, hasPassword: true, bans: [{ key: "k", name: { n: 1 }, id: [1], kind: "ip" }], players: [{ id: 0, host: true, you: true, role: "host" }] }, net: { show: true, text: 5 } },
  endHostile: { state: { ...base, mode: "None", end: { seq: "s", kind: "refused", refuse: "Mismatch", reportKind: "mods", text: { t: 1 }, missing: [null, { name: { n: 1 } }, 3], extra: "nope", versions: [{ name: "M", host: {}, you: [] }], hostGame: 1 } } },
  recoveryHostile: { state: { ...base, mode: "Recovering", recovery: { phase: "Waiting", attempt: "two", max: {}, seconds: "soon", reason: { r: 1 } }, players: "none" } },
  hugeSession: { state: { ...base, mode: "Hosting", sessionName: "Big", maxPlayers: 16, canInvite: true,
    players: Array.from({ length: 200 }, (_, i) => ({ id: i, name: "Player number " + i + " with a rather long Steam name", host: i === 0, you: i === 0, state: i % 7 === 3 ? "Downloading" : "Live", ping: (i * 37) % 400, progress: (i % 10) / 10, color: "#3fb6e8", role: i === 0 ? "host" : i % 5 === 0 ? "viewer" : i % 9 === 0 ? "cohost" : "builder" })),
    chat: Array.from({ length: 100 }, (_, i) => ({ name: "P" + i, text: "message " + i + " ".repeat(i % 3) + "lorem ipsum dolor sit amet ".repeat(i % 6), system: i % 17 === 0, seq: 1000 + i, you: i % 4 === 0, color: "#5fcf7a" })),
    bans: Array.from({ length: 60 }, (_, i) => ({ key: "steam:" + i, name: "Banned " + i, kind: "steam", id: "7656119000000" + String(1000 + i) })) },
    friends: Array.from({ length: 300 }, (_, i) => ({ id: String(76561190000001000 + i), name: "Friend " + i, state: ["ingame", "online", "away", "offline"][i % 4], inSession: i % 11 === 0 })), overlayAvailable: true,
    presence: { tags: Array.from({ length: 200 }, (_, i) => ({ id: i, name: "Player number " + i, color: "#3fb6e8", x: (i % 20) / 20, y: (i % 10) / 10, on: true, activity: "building" })) } },
  longTexts: { state: { ...base, mode: "Client", sessionName: "A session name that goes on and on and on well beyond the panel width".repeat(2), canInvite: true, localRole: "cohost", status: "Connected",
    resync: "34 of 200 checked areas still differed from the host after repairs, 2 checks in a row ".repeat(3), sync: "x".repeat(600),
    players: [{ id: 0, name: "H".repeat(64), host: true, you: false, state: "Live", ping: 40, progress: 1, color: "#f2b84b", role: "host" }, { id: 1, name: "Supercalifragilisticexpialidocious_and_more_without_any_spaces", host: false, you: true, state: "Live", ping: 0, progress: 1, color: "#3fb6e8", role: "cohost" },
      { id: 2, name: "W".repeat(64), host: false, you: false, state: "Loading", ping: 999, progress: 0.99, color: "#5fcf7a", role: "builder" }],
    chat: [{ name: "N".repeat(64), text: "a".repeat(500), system: false, seq: 1, you: false, color: "#3fb6e8" }, { name: "Bo", text: "https://example.com/" + "very/long/path/".repeat(30), system: false, seq: 2, you: false, color: "#5fcf7a" }, { name: "", text: "System line ".repeat(40), system: true, seq: 3, you: false, color: "" }] },
    net: { show: true, text: "ping 40 ms · in 12.0 KB/s · out 3.1 KB/s ".repeat(5) }, presence: { tags: [{ id: 2, name: "W".repeat(64), color: "#5fcf7a", x: 0.99, y: 0.01, on: true, activity: "building a very long road name ".repeat(4) }] } },
  endLongMods: { state: { ...base, mode: "None", end: Object.assign({}, mods, { text: "detail ".repeat(200), missing: Array.from({ length: 40 }, (_, i) => ({ name: "A mod with a very long display name number " + i + " ".repeat(3) + "and suffix", version: "1.2.3-beta+" + "x".repeat(20), id: String(i) })) }) } },
  rtlEmoji: { state: { ...base, mode: "Hosting", sessionName: "مدينة سارة 🏙️", canInvite: true,
    players: [{ id: 0, name: "דנה 🚀", host: true, you: true, state: "Live", ping: 0, progress: 1, color: "#f2b84b", role: "host" }, { id: 1, name: "سارة", host: false, you: false, state: "Live", ping: 45, progress: 1, color: "#3fb6e8", role: "builder" }, { id: 2, name: "👨‍👩‍👧‍👦🎉🔥", host: false, you: false, state: "Downloading", ping: 30, progress: 0.3, color: "#5fcf7a", role: "viewer" }],
    chat: [{ name: "سارة", text: "مرحبا! hello 👋 שלום", system: false, seq: 1, you: false, color: "#3fb6e8" }, { name: "👨‍👩‍👧‍👦🎉🔥", text: "🏗️🛣️🚧".repeat(40), system: false, seq: 2, you: false, color: "#5fcf7a" }] },
    presence: { tags: [{ id: 1, name: "سارة", color: "#3fb6e8", x: 0.5, y: 0.5, on: true, activity: "تبني طريقا" }] }, friends: [{ id: "76561190000000011", name: "עמית 🎮", state: "ingame", inSession: false }] },
  // inline validation: a too short Steam ID, a port below 1024
  lobbyInvalid: { state: { ...base, mode: "None", joinTarget: "12345", portSetting: 80, allowDirect: true, hasPassword: true } },
  lobbyBadPort: { state: { ...base, mode: "None", inGame: false, joinTarget: "203.0.113.7:99999", portSetting: 80, allowDirect: true, hasPassword: true } },
  blockedOnly: { state: { blocked: "the session layer did not start (see the log)" } },
  endLoadFailed: { state: { ...base, mode: "None", end: end("loadFailed") } },
  endProtocolError: { state: { ...base, mode: "None", end: end("protocolError") } },
  endLeft: { state: { ...base, mode: "None", end: end("left") } },
  endUnknownKind: { state: { ...base, mode: "None", end: end("somethingNew", "AlsoNew") } },
  joiningUnknownStage: { state: { ...base, mode: "Joining", stage: "teleporting", progress: 2 }, join: { active: true, stage: "teleporting", progress: -1 } },
  garbage: { state: "{not json", net: "", presence: "null", join: "[1,2", replay: "{oops", friends: "{bad", perf: "{\"show\":true,\"features\":7,\"peers\":null" },
  empty: { state: "{}" },
  arrayState: { state: "[1,2,3]", presence: "[]", join: "\"text\"", replay: "42" },
  // the Paradox Mods screenshots (tools/paradox/make_images.py): a believable session with invented names, no Steam IDs
  // on screen and no build id in the footer, so the images do not go stale with the next release
  showcaseHosting: { state: { ...base, mode: "Hosting", build: "", hold: true, sessionName: "Riverside", directPort: 27015, canInvite: true, inviteSteamId: "76561190000000000",
    status: "A player is joining – the game is paused until they are in. Priya is downloading the city (60 %).", speed: "paused while a player joins", speedHeld: true,
    sync: "48,210 objects in sync; verifying 9,000 objects every 30 s, 0 repairs sent; 1,904 movers",
    players: [{ id: 0, name: "Mara", host: true, you: true, state: "Live", ping: 0, progress: 1, color: "#f2b84b", role: "host" },
      { id: 1, name: "Jonas", host: false, you: false, state: "Live", ping: 38, progress: 1, color: "#3fb6e8", role: "builder" },
      { id: 2, name: "Priya", host: false, you: false, state: "Downloading", ping: 52, progress: 0.6, color: "#5fcf7a", role: "builder" },
      { id: 3, name: "Theo", host: false, you: false, state: "Live", ping: 71, progress: 1, color: "#a07af0", role: "viewer" }],
    chat: [{ name: "", text: "You are hosting Riverside", system: true, seq: 1, you: false, color: "" }, { name: "Jonas", text: "I'll take the harbour district", system: false, seq: 2, you: false, color: "#3fb6e8" }] },
    net: { show: false, text: "" }, presence: { tags: [{ id: 1, name: "Jonas", color: "#3fb6e8", x: 0.599, y: 0.458, on: true, activity: "building Small Road" }] },
    friends: [{ id: "76561190000000001", name: "Jonas", state: "ingame", inSession: true }, { id: "76561190000000002", name: "Priya", state: "ingame", inSession: true },
      { id: "76561190000000003", name: "Lena", state: "online", inSession: false }, { id: "76561190000000004", name: "Sam", state: "away", inSession: false },
      { id: "76561190000000005", name: "Alex", state: "offline", inSession: false }], overlayAvailable: true },
  showcaseLobby: { state: { ...base, mode: "None", build: "", inGame: false, canHost: false, playerName: "Priya", hasPassword: true, allowDirect: true } },
  showcaseJoining: { state: { ...base, mode: "Joining", build: "", stage: "downloading", progress: 0.55 },
    join: join("downloading", { session: "Riverside", target: "", done: 115343360, total: 209715200, bps: 6291456, eta: 15, progress: 0.55 }) },
};
// texts an English render of a state must show (has) or must not show (lacks)
const JOIN_ANYWAY = "Join anyway";
const WARNING = "Different mods can make your cities drift apart or behave differently. The host's city is authoritative; differences are repaired where possible.";
const EXPECT = {
  endMods: { has: ["The host requires the same code mods. Change these, restart the game and join again.", "Anarchy"], lacks: [JOIN_ANYWAY, WARNING] },
  endModsRequired: { has: ["The host requires the same code mods. Change these, restart the game and join again."], lacks: [JOIN_ANYWAY, WARNING] },
  endModsOverridable: { has: [JOIN_ANYWAY, WARNING, "Your code mods differ from the host's. Best: change these, restart the game and join again. The host also lets you join anyway.", "Anarchy", "Traffic", "Move It", "Back"] },
  endAssetsOverridable: { has: [JOIN_ANYWAY, WARNING, "Bus Stops"], lacks: ["The host requires the same asset packs."] },
  endLongModsOverridable: { has: [JOIN_ANYWAY, WARNING, "Version mod 79"] },
  endAssets: { lacks: [JOIN_ANYWAY] },
  endContentOverridable: { lacks: [JOIN_ANYWAY, WARNING] },
  endFullOverridable: { lacks: [JOIN_ANYWAY] },
  endOverridableHostile: { lacks: [JOIN_ANYWAY] },
  lobbyGame: { has: ["Let players join with different mods"] },
  lobbyMenu: { has: ["Let players join with different mods"] },
  lobbyNoOverride: { has: ["Let players join with different mods"] },
  endModsCityData: { has: ["Mods required for this city", "Enable these mods to join (2)", "ZoningToolkit", "AreaBucket",
    "This city contains its data – loading it without the mod would crash the game.", "It can store data in a city – loading the city without the mod can crash the game.",
    "The host's city stores data of mods you don't have. Your game would crash loading it without them, so you can't join until you enable them – not even with Join anyway. Enable or subscribe to them, restart the game and join again."],
    lacks: [JOIN_ANYWAY, WARNING, "Other differences", "Try again"] },
  endModsCityDataRest: { has: ["Enable these mods to join (1)", "Other differences", "Anarchy", "Traffic", "Move It",
    "The host's city stores data of mods you don't have. Your game would crash loading it without them, so you can't join until you enable them – not even with Join anyway. Enable or subscribe to them, restart the game and join again. The host lets you join anyway with the other differences once these are enabled."],
    lacks: [JOIN_ANYWAY, WARNING] },
  endModsCityDataHostile: { lacks: [JOIN_ANYWAY] },
  endConflictYou: { has: ["Two multiplayer mods at once", "The 'CS2 Multiplayer Mod' is enabled – two multiplayer mods at once conflict. Disable it in your playset or unsubscribe from it, restart the game and join again."], lacks: [JOIN_ANYWAY, "Try again"] },
  endConflictHost: { has: ["The host has the 'CS2 Multiplayer Mod' enabled – two multiplayer mods at once conflict. Ask the host to disable it and restart the game, then join again."], lacks: [JOIN_ANYWAY] },
  lobbyConflictMod: { has: ["The 'CS2 Multiplayer Mod' (CS2MultiplayerMod 1.0.0.0) is enabled. Disable it and restart the game – two multiplayer mods at once conflict. While it is enabled, nobody can join you and you can't join others."] },
  hostingConflictMod: { has: ["The 'CS2 Multiplayer Mod' (CS2MultiplayerMod 1.0.0.0) is enabled. Disable it and restart the game – two multiplayer mods at once conflict. While it is enabled, nobody can join you and you can't join others."] },
  hosting: { lacks: ["Two multiplayer mods at once"] },
};
for (const name of Object.keys(EXPECT)) if (!cases[name]) fail("EXPECT names an unknown state " + name);
const passes = [
  { name: "vanilla", ui: vanillaUi, locale: null, forceTrue: false, effects: true },
  { name: "vanilla-de-forced", ui: vanillaUi, locale: de, forceTrue: true, effects: false },
  { name: "builtin", ui: null, locale: null, forceTrue: true, effects: true },
];
// after a render with effects: press Enter and Escape on the window listeners, then unmount (run every cleanup) and
// check that nothing was left subscribed
const KEYS = ["Enter", "Escape"].map((key) => ({ key, preventDefault() {}, stopPropagation() {}, target: { blur() {} } }));
function settle(label) {
  if (!runEffects) return;
  for (const ev of KEYS) {
    for (const fn of [...(winListeners.get("keydown") || [])]) {
      try { fn(ev); } catch (e) { fail(label + " key " + ev.key + ": " + (e && e.stack)); }
    }
  }
  for (const c of cleanups.splice(0).reverse()) {
    try { c(); } catch (e) { fail(label + " effect cleanup: " + (e && e.stack)); }
  }
  let left = 0;
  for (const set of winListeners.values()) { left += set.size; set.clear(); }
  if (left > 0) fail(label + ": " + left + " window listener(s) left after unmount");
  if (smoke.store.listeners.size > 0) { fail(label + ": " + smoke.store.listeners.size + " store listener(s) left after unmount"); smoke.store.listeners.clear(); }
}
const roots = ["GameTopLeft", "Game", "Menu"];
let total = 0;
for (const pass of passes) {
  smoke.V.ok = pass.ui !== null; // the built-in look, as after a game component failed
  locale = pass.locale;
  forceTrue = pass.forceTrue;
  runEffects = pass.effects;
  for (const [name, c] of Object.entries(cases)) {
    caseTexts = new Set();
    values = {};
    for (const k of Object.keys(c)) values[k] = typeof c[k] === "string" || typeof c[k] === "boolean" ? c[k] : JSON.stringify(c[k]);
    for (const tab of ["host", "join", "solo"]) {
      for (const slot of roots) {
        // every surface at once: panel, invite popover, expanded and collapsed chat dock
        Object.assign(smoke.store, { open: true, menuOpen: true, tab, gameTab: tab, dismissedEnd: -1, typing: 0, invitesOpen: true, chatOpen: tab !== "solo", chatSeen: -1, sent: tab === "join" ? { "76561190000000002": true } : {} });
        renderLabel = pass.name + " " + name + " " + slot;
        try { total += render({ type: appended[slot], props: {}, children: [] }); }
        catch (e) { fail(pass.name + " " + name + " " + slot + ": " + (e && e.stack)); }
        settle(renderLabel);
        runHandlers(pass.name + " " + name + " " + slot);
      }
    }
    // the multiplayer page (open, per page) as the Menu layer over the title screen, and closed
    for (const [menuOpen, tab] of [[true, "join"], [true, "host"], [true, "solo"], [false, "join"]]) {
      Object.assign(smoke.store, { menuOpen, tab, dismissedEnd: -1, typing: 0 });
      let nodes = 0;
      renderLabel = pass.name + " " + name + " multiplayer page";
      try { nodes = render({ type: appended["Menu"], props: {}, children: [] }); total += nodes; }
      catch (e) { fail(pass.name + " " + name + " multiplayer page: " + (e && e.stack)); }
      settle(renderLabel);
      if (menuOpen && nodes < 20) fail(pass.name + " " + name + " multiplayer page rendered almost nothing (" + nodes + " nodes)");
      runHandlers(pass.name + " " + name + " multiplayer page");
    }
    renderLabel = pass.name + " " + name + " main menu";
    try { total += render({ type: extended[MAIN_MENU + "@MainMenuNavigation"], props: { children: [{ type: "a", props: {}, children: [] }, { type: "b", props: {}, children: [] }, { type: "c", props: {}, children: [] }, false, { type: "d", props: {}, children: [] }] }, children: [] }); }
    catch (e) { fail(pass.name + " " + name + " main menu: " + (e && e.stack)); }
    settle(renderLabel);
    runHandlers(pass.name + " " + name + " main menu");
    // English passes: the state shows what it must (texts are whole rendered strings)
    const want = EXPECT[name];
    if (want && pass.locale === null) {
      for (const text of want.has || []) if (!caseTexts.has(text)) fail(pass.name + " " + name + ": does not show " + JSON.stringify(text));
      for (const text of want.lacks || []) if (caseTexts.has(text)) fail(pass.name + " " + name + ": shows " + JSON.stringify(text));
    }
  }
  console.log("ok pass " + pass.name);
}
console.log("rendered " + total + " nodes over " + passes.length + " passes and " + Object.keys(cases).length + " states");
if (rawKeys.size > 0) fail("text keys rendered raw (missing in the English table): " + [...rawKeys].join(", "));
if (unknownBindings.size > 0) fail("bindings the C# side does not provide: " + [...unknownBindings].join(", "));
const triggerNames = new Set(triggers.filter((t) => t[0] === "skylineTogether").map((t) => t[1]));
for (const t of triggerNames) if (!csTriggers.has(t)) fail("trigger the C# side does not provide: " + t);
// the only foreign trigger: the vanilla main menu's own screen switch (Load game / New game from the Host page)
for (const t of triggers) if (t[0] !== "skylineTogether" && !(t[0] === "menu" && t[1] === "setActiveScreen")) fail("trigger outside the group: " + t[0] + "." + t[1]);
console.log("triggers used: " + [...triggerNames].sort().join(", "));
for (const t of ["host", "join", "leave", "chat", "kick", "saveHostSettings", "saveHostOptions", "retryJoin", "dismissEnd", "copySteamId", "copyText", "inviteFriends", "toggleNetHud", "writeDiagnostics", "toggleRole",
  "ban", "toggleCoHost", "unban", "setFriendsOnly", "setNewPlayersViewer", "resync", "dismissResync", "joinAnyway", "setAllowJoinWithDifferentMods",
  "inviteFriend", "openInviteOverlay", "refreshFriends", "recordStart", "recordStop", "replayStart", "replayStop", "replayRefresh", "reconnect", "cancelReconnect", "stayOffline", "mainMenu"]) {
  if (!triggerNames.has(t)) fail("no UI path fires the trigger " + t);
}

// --------------------------------------------------------------------------------------------- locale tables
const src = read("ui/SkylineTogether.mjs");
const m = src.match(/const EN = (\{[\s\S]*?\n\});/);
if (!m) fail("no EN table in the module");
else {
  const EN = (0, eval)("(" + m[1] + ")");
  const en = JSON.parse(read("src/SkylineTogether/Data/locale.en-US.json"));
  const keyList = JSON.parse(read("src/SkylineTogether/Data/locale-keys.generated.json")).keys;
  for (const [k, v] of Object.entries(EN)) {
    if (en[k] !== v) fail("locale.en-US.json differs from the module for " + k + ": " + JSON.stringify(en[k]) + " vs " + JSON.stringify(v));
    if (de[k] === undefined) fail("locale.de-DE.json lacks " + k);
    if (!keyList.includes(k)) fail("locale-keys.generated.json lacks " + k);
  }
  for (const k of Object.keys(de)) if (!k.startsWith("_") && en[k] === undefined) fail("locale.de-DE.json has a key English lacks: " + k);
  for (const [k, v] of Object.entries(en)) {
    if (k.startsWith("_") || de[k] === undefined) continue;
    const ph = (s) => (String(s).match(/\{[A-Z]+\}/g) || []).sort().join(",");
    if (ph(v) !== ph(de[k])) fail("placeholders differ between English and German for " + k);
  }
  // literal keys; prefixes ("st.ui.tab." + id) are covered by the raw-key check of the rendered output
  for (const used of src.matchAll(/\bt\("([^"]+)"/g)) if (!used[1].endsWith(".") && EN[used[1]] === undefined) fail("t() of a key missing in EN: " + used[1]);
}

// ------------------------------------------------------------------------------------- the game's design tokens
// Gameface drops a declaration whose var() names an undefined property (and the module never writes fallbacks), so
// every token the module uses must exist in the game's own stylesheet.
if (gameCss) {
  const defined = new Set([...gameCss.matchAll(/(--[A-Za-z0-9-]+)\s*:/g)].map((m) => m[1]));
  const code = src.replace(/\/\/.*$/gm, "");
  const missing = [...new Set([...code.matchAll(/var\((--[A-Za-z0-9-]+)\)/g)].map((m) => m[1]))].filter((v) => !defined.has(v));
  if (missing.length > 0) fail("CSS tokens the game's index.css does not define: " + missing.join(", "));
  else console.log("design tokens: all defined in the game's index.css");
} else {
  console.log("design tokens: skipped (the game's index.css was not found; set GameDir)");
}

// ------------------------------------------------------------------------------------------------ snapshots
function writeSnapshots(dir) {
  fs.mkdirSync(dir, { recursive: true });
  smoke.V.ok = false; // the built-in look: every style is inline (the vanilla classes exist only in the game)
  locale = null;
  forceTrue = false;
  runEffects = false;
  const files = [];
  const write = (file, title, body) => {
    fs.writeFileSync(path.join(dir, file), page(title, body));
    files.push([file, title]);
  };
  for (const [name, c] of Object.entries(cases)) {
    values = {};
    for (const k of Object.keys(c)) values[k] = typeof c[k] === "string" || typeof c[k] === "boolean" ? c[k] : JSON.stringify(c[k]);
    const st = typeof c.state === "object" ? c.state : {};
    const lobby = (st.mode || "None") === "None" && !st.end;
    for (const tab of lobby ? ["host", "join", "solo"] : ["host"]) {
      Object.assign(smoke.store, { open: true, menuOpen: false, tab, gameTab: tab, dismissedEnd: -1, typing: 0, invitesOpen: true, chatOpen: true, chatSeen: -1, sent: {} });
      write(name + ".game" + (lobby ? "-" + tab : "") + ".html", name + " (in game" + (lobby ? ", " + tab : "") + ")", toHtml({ type: appended["Game"], props: {}, children: [] }) + '<div style="position:absolute;left:10rem;top:10rem">' + toHtml({ type: appended["GameTopLeft"], props: {}, children: [] }) + "</div>");
      Object.assign(smoke.store, { open: false, menuOpen: true });
      write(name + ".menu-" + tab + ".html", name + " (main menu, " + tab + ")", toHtml({ type: appended["Menu"], props: {}, children: [] }));
    }
  }
  fs.writeFileSync(path.join(dir, "index.html"), "<!doctype html><meta charset=\"utf-8\"><title>UI snapshots</title><ul>" + files.map(([f, t]) => '<li><a href="' + f + '">' + esc(t) + "</a></li>").join("") + "</ul>");
  console.log("snapshots: " + files.length + " pages in " + dir);
}

function esc(s) {
  return String(s).replace(/[&<>"]/g, (ch) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[ch]);
}

const UNITLESS = new Set(["flex", "flexGrow", "flexShrink", "opacity", "zIndex", "fontWeight", "lineHeight", "order"]);
function cssText(style) {
  return Object.entries(style || {})
    .filter(([, v]) => v !== null && v !== undefined && v !== "" && typeof v !== "boolean")
    .map(([k, v]) => {
      const prop = k.startsWith("--") ? k : k.replace(/[A-Z]/g, (m) => "-" + m.toLowerCase());
      const val = typeof v === "number" && !UNITLESS.has(k) && v !== 0 ? v + "px" : String(v);
      return prop + ":" + val + (/^mask/.test(k) ? ";-webkit-" + prop + ":" + val : "");
    })
    .join(";");
}

function toHtml(node) {
  if (node == null || typeof node === "boolean") return "";
  if (typeof node === "string" || typeof node === "number") return esc(node);
  if (Array.isArray(node)) return node.map(toHtml).join("");
  if (typeof node !== "object" || node.type === undefined) return "";
  if (typeof node.type === "function") {
    const props = Object.assign({}, node.props, { children: node.children.length === 1 ? node.children[0] : node.children });
    try {
      return toHtml(node.type.prototype instanceof Component ? new node.type(props).render() : node.type(props));
    } catch (e) {
      return "<pre style=\"color:red\">" + esc(e && e.stack) + "</pre>";
    }
  }
  const p = node.props || {};
  const tag = /^[a-z]+$/.test(node.type) ? node.type : "div";
  let attrs = p.style ? ' style="' + esc(cssText(p.style)) + '"' : "";
  if (p.tabIndex !== undefined) attrs += ' tabindex="' + esc(p.tabIndex) + '"';
  if (tag === "input") return "<input" + attrs + ' type="' + (p.type === "password" ? "password" : "text") + '" value="' + esc(p.value || "") + '" placeholder="' + esc(p.placeholder || "") + '">';
  return "<" + tag + attrs + ">" + (node.children || []).map(toHtml).join("") + "</" + tag + ">";
}

// a 1920 x 1080 frame like the game's UI at 1080p (1rem = 1px there), over a backdrop with dark and bright parts (the
// panels are translucent). <body> sets black text like Gameface's default, so a surface without its own colour shows.
function page(title, body) {
  const tokens = gameCss ? [...gameCss.matchAll(/:root\s*\{([^}]*)\}/g)].map((m) => m[1]).join(";") : "";
  const base = gameUi ? pathToFileURL(gameUi + path.sep).href : "";
  return "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + esc(title) + "</title>" + (base ? '<base href="' + esc(base) + '">' : "") +
    "<style>:root{" + tokens + "}" +
    "@font-face{font-family:Overpass;src:url(\"Fonts/Overpass-Regular.ttf\")}@font-face{font-family:Overpass;font-weight:bold;src:url(\"Fonts/Overpass-Bold.ttf\")}" +
    "html{font-size:1px}body{margin:0;font-family:Overpass,\"Noto Sans\",sans-serif;font-size:14rem;color:#000;background:#111}" +
    ".frame{position:relative;width:1920px;height:1080px;overflow:hidden;background-image:linear-gradient(120deg,#26302a 0%,#5d6b58 45%,#b9c2b2 75%,#e4e8e0 100%)}" +
    "input{outline:none;font-family:inherit;box-sizing:border-box}div{box-sizing:border-box}" +
    "</style></head><body><div class=\"frame\">" + body + "</div></body></html>";
}
if (htmlDir) writeSnapshots(htmlDir);
process.exit(failed ? 1 : 0);
