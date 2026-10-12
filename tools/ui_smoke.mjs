// UI smoke test: renders the panel module's roots with a stub React for several session states and reports
// exceptions. Unlike the generic smoke script, every binding gets its own value (state, net, presence) and the
// states carry every field the C# side sends (SessionUISystem: sync, speed, progress, net HUD, presence tags).
// Usage: node tools/ui_smoke.mjs file:///<absolute path to ui/SkylineTogether.mjs>
let values = {};
const triggers = [];
globalThis.window = globalThis;
window.React = {
  createElement(type, props, ...children) { return { type, props: props || {}, children }; },
  useState(init) { return [init, () => {}]; },
  useRef() { return { current: null }; },
  useEffect() {},
};
const known = new Set(["state", "net", "presence"]);
const unknownBindings = new Set();
window["cs2/api"] = {
  bindValue(g, n, f) {
    if (g !== "skylineTogether" || !known.has(n)) unknownBindings.add(g + "." + n);
    return { g, n, f };
  },
  useValue(b) { return b && values[b.n] !== undefined ? values[b.n] : b ? b.f : undefined; },
  trigger(...a) { triggers.push(a); },
};
const mod = await import(process.argv[2]);
const appended = {};
mod.default({ append(slot, comp) { appended[slot] = comp; } });
function render(node) {
  if (node == null || typeof node !== "object") return 0;
  if (Array.isArray(node)) return node.reduce((n, c) => n + render(c), 0);
  if (typeof node.type === "function") return render(node.type(Object.assign({}, node.props, { children: node.children })));
  let n = 1;
  for (const c of node.children) n += render(c);
  if (node.props && typeof node.props.onClick === "function") { try { node.props.onClick({ stopPropagation() {} }); } catch (e) { throw new Error("onClick: " + e.stack); } }
  return n;
}
const base = { steam: true, steamId: "76561190000000000", players: [], chat: [], canHost: true, canJoin: true, keepPassword: "\u0001keep",
  playerName: "Me", sessionNameSetting: "", hasPassword: false, joinTarget: "", lastEnd: "", status: "", sync: "", speed: "", sessionName: "", progress: 0, directPort: 0 };
const tags = { tags: [{ id: 1, name: "Anna", color: "#3fb6e8", x: 0.4, y: 0.6, on: true, activity: "building Small Road" }, { id: 2, name: "Bo", color: "#5fcf7a", x: 2, y: -1, on: false, activity: "" }] };
const net = { show: true, text: "ping 40 ms · in 12.0 KB/s · out 3.1 KB/s" };
const cases = {
  none: { state: { ...base, mode: "None", lastEnd: "Wrong password." } },
  nosteam: { state: { ...base, mode: "None", steam: false, canHost: false, canJoin: false } },
  hosting: { state: { ...base, mode: "Hosting", status: "A player is joining – the game is paused until they are in. Anna: downloading the city 40 %.", sessionName: "X", directPort: 27015,
    sync: "12,345 objects in sync; verifying 9,000 objects every 30 s, 0 repairs sent; 812 movers", speed: "paused while a player joins",
    players: [{ id: 0, name: "Host", host: true, you: true, state: "Live", ping: 0, progress: 1 }, { id: 1, name: "Anna", host: false, you: false, state: "Downloading", ping: 30, progress: 0.4 },
      { id: 2, name: "Bo", host: false, you: false, state: "Live", ping: 55, progress: 1 }],
    chat: [{ name: "", text: "hi", system: true }, { name: "Anna", text: "yo", system: false }] }, net, presence: tags },
  hostingNoHud: { state: { ...base, mode: "Hosting", status: "Hosting", sessionName: "X", speed: "2×" }, net: { show: false, text: "" }, presence: { tags: [] } },
  joining: { state: { ...base, mode: "Joining", status: "Downloading the city…", progress: 0.3, sessionName: "X" } },
  client: { state: { ...base, mode: "Client", status: "Connected", sessionName: "X", speed: "1×",
    sync: "12,345 objects in sync, terrain: 4 tiles applied; verified 9,000 objects, 0 repairs; icons match; movers +10 -2, 140 in motion; clock follows the host",
    players: [{ id: 0, name: "Host", host: true, you: false, state: "Live", ping: 40, progress: 1 }, { id: 1, name: "Me", host: false, you: true, state: "Live", ping: 0, progress: 1 }] }, net, presence: tags },
  garbage: { state: "{not json", net: "", presence: "null" },
};
let failed = 0;
for (const [name, c] of Object.entries(cases)) {
  values = {};
  for (const k of Object.keys(c)) values[k] = typeof c[k] === "string" ? c[k] : JSON.stringify(c[k]);
  for (const slot of Object.keys(appended)) {
    // force the panel open: the first boolean state starts true
    const orig = window.React.useState;
    window.React.useState = (init) => [init === false ? true : init, () => {}];
    try { const n = render({ type: appended[slot], props: {}, children: [] }); console.log("ok", name, slot, n, "nodes"); }
    catch (e) { failed++; console.log("FAIL", name, slot, e.stack); }
    window.React.useState = orig;
  }
}
if (unknownBindings.size > 0) { failed++; console.log("FAIL bindings the C# side does not provide:", [...unknownBindings].join(", ")); }
const triggerNames = new Set(triggers.map((t) => t[1]));
const provided = new Set(["host", "join", "leave", "chat", "kick", "saveHostSettings", "writeDiagnostics", "toggleNetHud"]);
for (const t of triggerNames) if (!provided.has(t)) { failed++; console.log("FAIL trigger the C# side does not provide:", t); }
console.log("triggers used:", [...triggerNames].join(", "));
process.exit(failed ? 1 : 0);
