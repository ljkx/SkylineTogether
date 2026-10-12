// UI smoke test: renders the module's components with a stub React for many session states, with and without the
// game's own components (cs2/ui, registry modules), and fails on
//  - an exception while importing, registering, rendering or running an event handler,
//  - a binding or trigger name the C# side (src/SkylineTogether/UI/SessionUISystem.cs) does not declare,
//  - a text key that renders raw (missing from the module's English table),
//  - English texts that differ from src/SkylineTogether/Data/locale.en-US.json, keys missing in another locale table
//    or in locale-keys.generated.json,
//  - a main menu entry, top-left button or root that was not registered.
// Usage: node tools/ui_smoke.mjs [file:///<absolute path to ui/SkylineTogether.mjs>] (default: ../ui/SkylineTogether.mjs)
import fs from "node:fs";
import { fileURLToPath } from "node:url";

const repo = new URL("../", import.meta.url);
const read = (rel) => fs.readFileSync(fileURLToPath(new URL(rel, repo)), "utf8");
let failed = 0;
const fail = (msg) => { failed++; console.log("FAIL " + msg); };

// ------------------------------------------------------------------------------------------- C# declarations
const cs = read("src/SkylineTogether/UI/SessionUISystem.cs");
const csBindings = new Set([...cs.matchAll(/GetterValueBinding<string>\(Group, "([^"]+)"/g)].map((m) => m[1]));
const csTriggers = new Set([...cs.matchAll(/TriggerBinding(?:<[^>]*>)?\(Group, "([^"]+)"/g)].map((m) => m[1]));
for (const b of ["state", "join", "net", "presence"]) if (!csBindings.has(b)) fail("SessionUISystem lost the binding " + b);
for (const t of ["host", "join", "leave", "chat", "kick", "saveHostSettings", "writeDiagnostics", "toggleNetHud", "saveHostOptions", "retryJoin", "dismissEnd", "copySteamId", "copyText", "inviteFriends"]) {
  if (!csTriggers.has(t)) fail("SessionUISystem lost the trigger " + t);
}

// ------------------------------------------------------------------------------------------------ stubs
let values = {};
let forceTrue = false;
const triggers = [];
const handlers = [];
globalThis.window = globalThis;
window.__skylineTogetherSmoke = {};
class Component { constructor(props) { this.props = props; this.state = {}; } setState() {} }
window.React = {
  createElement(type, props, ...children) { return { type, props: props || {}, children }; },
  // the forced passes start every flag true and every text non-empty, so guarded paths (send, retry) run too
  useState(init) { return [forceTrue && init === false ? true : forceTrue && init === "" ? "smoke" : typeof init === "function" ? init() : init, () => {}]; },
  useRef(init) { return { current: init === undefined ? null : init }; },
  useEffect() {},
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
  FloatingButton(p) { return { type: "vanilla-floating", props: p, children: [] }; },
  MenuButton(p) { return { type: "vanilla-menu-button", props: p, children: [p.children] }; },
};
window["cs2/ui"] = vanillaUi;
const de = JSON.parse(read("src/SkylineTogether/Data/locale.de-DE.json"));
let locale = null;
window["cs2/l10n"] = { useLocalization() { return { translate(id, fallback) { return locale && locale[id] !== undefined ? locale[id] : fallback; } }; } };

const MAIN_MENU = "game-ui/menu/components/main-menu-screen/main-menu-screen.tsx";
const registry = new Map([
  [MAIN_MENU, { MainMenuNavigation: (p) => ({ type: "div", props: {}, children: [p.children] }), MainMenuScreen() {}, ModsUIButton() {} }],
  ["game-ui/common/input/button/labeled-icon-button.tsx", { LabeledIconButton: (p) => ({ type: "vanilla-labeled", props: p, children: [p.children] }) }],
  ["game-ui/menu/themes/menu-item.module.scss", { classes: { button: "button_uFa", icon: "icon_zVk", label: "label_NLv" } }],
  ["game-ui/common/input/toggle/checkbox/checkbox.tsx", { Checkbox: (p) => ({ type: "vanilla-checkbox", props: p, children: [] }) }],
  ["game-ui/common/focus/focus-key.ts", { FOCUS_DISABLED: { debugName: "disabled" } }],
]);

const target = process.argv[2] || new URL("../ui/SkylineTogether.mjs", import.meta.url).href;
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
if (!smoke.store) fail("the smoke hook is missing");

// ---------------------------------------------------------------------------------------------- renderer
const rawKeys = new Set();
function render(node) {
  if (node == null || typeof node === "boolean") return 0;
  if (typeof node === "string" || typeof node === "number") {
    if (/^st\.(ui|toast|chat|options)\./.test(String(node))) rawKeys.add(String(node));
    return 0;
  }
  if (Array.isArray(node)) return node.reduce((n, c) => n + render(c), 0);
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
  inGame: true, maxPlayers: 8, allowDirect: false, requireSameMods: true, portSetting: 27015, canInvite: false, inviteSteamId: "", hold: false, stage: "",
  speedValue: 1, speedPaused: false, speedHeld: false, end: null };
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
const cases = {
  lobbyGame: { state: { ...base, mode: "None" } },
  lobbyMenu: { state: { ...base, mode: "None", inGame: false, canHost: false, hasPassword: true, allowDirect: true } },
  nosteam: { state: { ...base, mode: "None", steam: false, canHost: false, canJoin: false } },
  hosting: { state: { ...base, mode: "Hosting", hold: true, status: "A player is joining – the game is paused until they are in. Anna is downloading the city (40 %).", sessionName: "X", directPort: 27015,
    sync: "12,345 objects in sync; verifying 9,000 objects every 30 s, 0 repairs sent; 812 movers", speed: "paused while a player joins", speedHeld: true, canInvite: true, inviteSteamId: "76561190000000000",
    players, chat }, net, presence: tags },
  hostingNoHud: { state: { ...base, mode: "Hosting", status: "Hosting", sessionName: "X", speed: "2×", speedValue: 2 }, net: { show: false, text: "" }, presence: { tags: [] } },
  joiningConnect: { state: { ...base, mode: "Joining", stage: "connecting", status: "Connecting…" }, join: join("connecting") },
  joiningWait: { state: { ...base, mode: "Joining", stage: "waiting" }, join: join("waiting") },
  joiningDownload: { state: { ...base, mode: "Joining", stage: "downloading", progress: 0.2 }, join: join("downloading", { done: 41943040, total: 104857600, bps: 3145728, eta: 20, progress: 0.2 }) },
  joiningDownloadStart: { state: { ...base, mode: "Joining", stage: "downloading" }, join: join("downloading") },
  joiningLoad: { state: { ...base, mode: "Joining", stage: "loading", progress: 0.75 }, join: join("loading", { load: 0.5, progress: 0.75, eta: 125 }) },
  client: { state: { ...base, mode: "Client", status: "Connected", sessionName: "X", speed: "1×", canInvite: true, inviteSteamId: "76561190000000009",
    sync: "12,345 objects in sync, terrain: 4 tiles applied; verified 9,000 objects, 0 repairs; icons match; movers +10 -2, 140 in motion; clock follows the host",
    players: [{ id: 0, name: "Host", host: true, you: false, state: "Live", ping: 40, progress: 1, color: "#f2b84b", role: "host" }, { id: 1, name: "Me", host: false, you: true, state: "Live", ping: 0, progress: 1, color: "#3fb6e8", role: "builder" }], chat }, net, presence: tags },
  endMods: { state: { ...base, mode: "None", end: mods } },
  endPassword: { state: { ...base, mode: "None", end: end("refused", "BadPassword") } },
  endPasswordRequired: { state: { ...base, mode: "None", end: end("passwordRequired") } },
  endGame: { state: { ...base, mode: "None", end: end("refused", "Mismatch", { reportKind: "game", hostGame: "1.2.3", yourGame: "1.2.2" }) } },
  endBuild: { state: { ...base, mode: "None", end: end("refused", "ProtocolVersion", { reportKind: "protocol", hostProto: "2", yourProto: "1" }) } },
  endFull: { state: { ...base, mode: "None", end: end("refused", "Full") } },
  endPreparing: { state: { ...base, mode: "None", end: end("refused", "HostPreparing") } },
  endTimeout: { state: { ...base, mode: "None", end: end("timeout") } },
  endLost: { state: { ...base, mode: "None", end: end("connectionLost") } },
  endKicked: { state: { ...base, mode: "None", end: end("kicked") } },
  endEnded: { state: { ...base, mode: "None", end: end("sessionEnded") } },
  endFailed: { state: { ...base, mode: "None", end: end("failed") } },
  garbage: { state: "{not json", net: "", presence: "null", join: "[1,2" },
  empty: { state: "{}" },
};
const passes = [
  { name: "vanilla", ui: vanillaUi, locale: null, forceTrue: false },
  { name: "vanilla-de-forced", ui: vanillaUi, locale: de, forceTrue: true },
  { name: "builtin", ui: null, locale: null, forceTrue: true },
];
const roots = ["GameTopLeft", "Game", "Menu"];
let total = 0;
for (const pass of passes) {
  smoke.V.ok = pass.ui !== null; // the built-in look, as after a game component failed
  locale = pass.locale;
  forceTrue = pass.forceTrue;
  for (const [name, c] of Object.entries(cases)) {
    values = {};
    for (const k of Object.keys(c)) values[k] = typeof c[k] === "string" ? c[k] : JSON.stringify(c[k]);
    for (const tab of ["host", "join"]) {
      for (const slot of roots) {
        Object.assign(smoke.store, { open: true, menuOpen: true, tab, dismissedEnd: -1, typing: 0 });
        try { total += render({ type: appended[slot], props: {}, children: [] }); }
        catch (e) { fail(pass.name + " " + name + " " + slot + ": " + (e && e.stack)); }
        runHandlers(pass.name + " " + name + " " + slot);
      }
    }
    try { total += render({ type: extended[MAIN_MENU + "@MainMenuNavigation"], props: { children: [{ type: "a", props: {}, children: [] }, { type: "b", props: {}, children: [] }, { type: "c", props: {}, children: [] }, false, { type: "d", props: {}, children: [] }] }, children: [] }); }
    catch (e) { fail(pass.name + " " + name + " main menu: " + (e && e.stack)); }
    runHandlers(pass.name + " " + name + " main menu");
  }
  console.log("ok pass " + pass.name);
}
console.log("rendered " + total + " nodes over " + passes.length + " passes and " + Object.keys(cases).length + " states");
if (rawKeys.size > 0) fail("text keys rendered raw (missing in the English table): " + [...rawKeys].join(", "));
if (unknownBindings.size > 0) fail("bindings the C# side does not provide: " + [...unknownBindings].join(", "));
const triggerNames = new Set(triggers.map((t) => t[1]));
for (const t of triggerNames) if (!csTriggers.has(t)) fail("trigger the C# side does not provide: " + t);
for (const t of triggers) if (t[0] !== "skylineTogether") fail("trigger outside the group: " + t[0]);
console.log("triggers used: " + [...triggerNames].sort().join(", "));
for (const t of ["host", "join", "leave", "chat", "kick", "saveHostSettings", "saveHostOptions", "retryJoin", "dismissEnd", "copySteamId", "copyText", "inviteFriends", "toggleNetHud", "writeDiagnostics", "toggleRole"]) {
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
process.exit(failed ? 1 : 0);
