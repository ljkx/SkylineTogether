/*
 * Cities: Skylines II UI Module
 *
 * Id: SkylineTogether
 * Author: Skyline Together
 * Version: 0.1.0
 * Dependencies:
 */

// Hand-written ES module (no build step). The game provides React and its binding API as globals.
const React = window.React;
const api = window["cs2/api"];
// Review fix: the module used to throw at import when a global was missing; now it registers nothing (ARCH §11.2).
const ready = !!(React && React.createElement && api && api.bindValue && api.useValue && api.trigger);
const h = ready ? React.createElement : function () { return null; };
const GROUP = "skylineTogether";
const stateBinding = ready ? api.bindValue(GROUP, "state", "{}") : null;
// presence branch: network HUD line ({show, text}) and remote players ({tags: [{id, name, color, x, y, on, activity}]})
const netBinding = ready ? api.bindValue(GROUP, "net", "{}") : null;
const presenceBinding = ready ? api.bindValue(GROUP, "presence", "{\"tags\":[]}") : null;

function parse(json) {
  try {
    return JSON.parse(json || "{}");
  } catch (e) {
    return {};
  }
}

const C = {
  bg: "rgba(24, 30, 40, 0.94)",
  panel: "rgba(255, 255, 255, 0.06)",
  border: "rgba(255, 255, 255, 0.14)",
  text: "#e8edf3",
  dim: "rgba(232, 237, 243, 0.62)",
  accent: "#3fb6e8",
  good: "#5fcf7a",
  warn: "#f2b84b",
  bad: "#ef6a6a",
};

const S = {
  toggle: {
    pointerEvents: "auto",
    position: "absolute",
    left: "12px",
    top: "120px",
    height: "36px",
    padding: "0 12px",
    display: "flex",
    alignItems: "center",
    borderRadius: "18px",
    background: C.bg,
    border: "1px solid " + C.border,
    color: C.text,
    fontSize: "14px",
    cursor: "pointer",
    zIndex: 1000,
  },
  panel: {
    pointerEvents: "auto",
    position: "absolute",
    left: "12px",
    top: "164px",
    width: "380px",
    maxHeight: "70vh",
    display: "flex",
    flexDirection: "column",
    background: C.bg,
    border: "1px solid " + C.border,
    borderRadius: "8px",
    color: C.text,
    fontSize: "14px",
    zIndex: 1000,
    overflow: "hidden",
  },
  header: { display: "flex", alignItems: "center", padding: "10px 12px", borderBottom: "1px solid " + C.border },
  title: { flex: 1, fontSize: "16px", fontWeight: "bold" },
  body: { padding: "10px 12px", overflowY: "auto", flex: 1 },
  section: { marginBottom: "12px" },
  label: { color: C.dim, fontSize: "12px", marginBottom: "4px" },
  input: {
    width: "100%",
    height: "30px",
    padding: "0 8px",
    background: C.panel,
    border: "1px solid " + C.border,
    borderRadius: "4px",
    color: C.text,
    fontSize: "14px",
    marginBottom: "6px",
  },
  button: {
    height: "32px",
    padding: "0 14px",
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    background: C.accent,
    borderRadius: "4px",
    color: "#0b1620",
    fontWeight: "bold",
    cursor: "pointer",
    marginTop: "2px",
  },
  buttonDisabled: { opacity: 0.4, cursor: "default" },
  secondary: { background: C.panel, color: C.text, border: "1px solid " + C.border },
  danger: { background: C.bad, color: "#1c0b0b" },
  row: { display: "flex", alignItems: "center", padding: "4px 0" },
  dot: { width: "8px", height: "8px", borderRadius: "4px", marginRight: "8px" },
  chatBox: { height: "160px", overflowY: "auto", background: C.panel, borderRadius: "4px", padding: "6px 8px", marginBottom: "6px" },
  progressOuter: { height: "8px", background: C.panel, borderRadius: "4px", overflow: "hidden", margin: "6px 0" },
  error: { color: C.bad, marginTop: "6px" },
  mono: { fontFamily: "monospace", userSelect: "text" },
};

function merge() {
  return Object.assign.apply(null, [{}].concat(Array.prototype.slice.call(arguments)));
}

function Button(props) {
  const disabled = !!props.disabled;
  return h(
    "div",
    {
      style: merge(S.button, props.kind === "secondary" ? S.secondary : null, props.kind === "danger" ? S.danger : null, disabled ? S.buttonDisabled : null, props.style),
      onClick: function () {
        if (!disabled && props.onClick) props.onClick();
      },
    },
    props.children
  );
}

function TextField(props) {
  return h("input", {
    type: props.password ? "password" : "text",
    style: S.input,
    value: props.value,
    placeholder: props.placeholder || "",
    "vk-title": props.title || "",
    "vk-type": "text",
    onChange: function (e) {
      props.onChange(e.target.value);
    },
    onKeyDown: function (e) {
      if (e.key === "Enter" && props.onEnter) props.onEnter();
      e.stopPropagation();
    },
  });
}

function stateColor(state) {
  if (state === "Live") return C.good;
  if (state === "Handshaking") return C.dim;
  return C.warn;
}

function stateText(p) {
  switch (p.state) {
    case "Live":
      return p.host ? "host" : p.ping + " ms";
    case "WaitingForSnapshot":
      return "waiting…";
    case "Downloading":
      return "downloading " + Math.round(p.progress * 100) + "%";
    case "Loading":
      return "loading " + Math.round(p.progress * 100) + "%";
    default:
      return p.state;
  }
}

// presence branch: what each remote player is doing, by player id
function activities() {
  const map = {};
  const tags = parse(api.useValue(presenceBinding)).tags || [];
  tags.forEach(function (t) {
    map[t.id] = t;
  });
  return map;
}

function NetLine() {
  const n = parse(api.useValue(netBinding));
  return h(
    "div",
    { style: { display: "flex", alignItems: "center", marginTop: "4px" } },
    n.show && n.text ? h("div", { style: merge(S.mono, { flex: 1, color: C.dim, fontSize: "12px" }) }, n.text) : h("div", { style: { flex: 1 } }),
    h(
      "div",
      { style: { color: C.accent, fontSize: "12px", cursor: "pointer", marginLeft: "8px" }, onClick: function () { api.trigger(GROUP, "toggleNetHud"); } },
      n.show ? "hide network" : "show network"
    ),
    h(
      "div",
      { style: { color: C.accent, fontSize: "12px", cursor: "pointer", marginLeft: "8px" }, onClick: function () { api.trigger(GROUP, "writeDiagnostics"); } },
      "write diagnostics"
    )
  );
}

// Remote players' name tags over their cursors (positions are fractions of the screen, from C#).
function NameTags() {
  const tags = parse(api.useValue(presenceBinding)).tags || [];
  return h(
    "div",
    { style: { position: "absolute", left: 0, top: 0, width: "100%", height: "100%", pointerEvents: "none" } },
    tags
      .filter(function (t) {
        return t.on;
      })
      .map(function (t) {
        return h(
          "div",
          {
            key: t.id,
            style: {
              position: "absolute",
              left: (t.x * 100).toFixed(2) + "%",
              top: (t.y * 100).toFixed(2) + "%",
              transform: "translate(-50%, -130%)",
              padding: "2px 8px",
              borderRadius: "10px",
              background: "rgba(24, 30, 40, 0.82)",
              border: "2px solid " + (t.color || C.accent),
              color: C.text,
              fontSize: "13px",
              whiteSpace: "nowrap",
            },
          },
          h("b", { style: { color: t.color || C.text } }, t.name),
          t.activity ? h("span", { style: { color: C.dim } }, " · " + t.activity) : null
        );
      })
  );
}

function Players(props) {
  const s = props.state;
  const players = s.players || [];
  const doing = activities();
  return h(
    "div",
    { style: S.section },
    h("div", { style: S.label }, "Players (" + players.length + ")"),
    players.map(function (p) {
      return h(
        "div",
        { key: p.id, style: S.row },
        h("div", { style: merge(S.dot, { background: doing[p.id] && doing[p.id].color ? doing[p.id].color : stateColor(p.state) }) }),
        h(
          "div",
          { style: { flex: 1 } },
          p.name + (p.you ? " (you)" : ""),
          doing[p.id] && doing[p.id].activity ? h("div", { style: { color: C.dim, fontSize: "12px" } }, doing[p.id].activity) : null
        ),
        h("div", { style: { color: C.dim, fontSize: "12px", marginRight: "8px" } }, stateText(p)),
        s.mode === "Hosting" && !p.host
          ? h(Button, { kind: "secondary", style: { height: "22px", padding: "0 8px", marginTop: 0 }, onClick: function () { api.trigger(GROUP, "kick", p.id); } }, "Kick")
          : null
      );
    })
  );
}

function Chat(props) {
  const lines = props.state.chat || [];
  const [text, setText] = React.useState("");
  const boxRef = React.useRef(null);
  React.useEffect(function () {
    if (boxRef.current) boxRef.current.scrollTop = boxRef.current.scrollHeight;
  }, [lines.length]);
  const send = function () {
    if (text.trim().length === 0) return;
    api.trigger(GROUP, "chat", text);
    setText("");
  };
  return h(
    "div",
    { style: S.section },
    h("div", { style: S.label }, "Chat"),
    h(
      "div",
      { style: S.chatBox, ref: boxRef },
      lines.map(function (l, i) {
        return h(
          "div",
          { key: i, style: { color: l.system ? C.dim : C.text, marginBottom: "2px" } },
          l.system ? h("i", null, l.text) : h("span", null, h("b", null, l.name + ": "), l.text)
        );
      })
    ),
    props.canSend ? h(TextField, { value: text, onChange: setText, onEnter: send, placeholder: "Message (Enter to send)", title: "Chat" }) : null
  );
}

function Lobby(props) {
  const s = props.state;
  const [name, setName] = React.useState(s.playerName || "");
  const [sessionName, setSessionName] = React.useState(s.sessionNameSetting || "");
  const [hostPassword, setHostPassword] = React.useState("");
  const [target, setTarget] = React.useState(s.joinTarget || "");
  const [joinPassword, setJoinPassword] = React.useState("");
  // review fix: an untouched (empty) password field keeps the stored host password instead of wiping it
  const passwordToSave = function () {
    return hostPassword.length > 0 ? hostPassword : s.keepPassword || "";
  };
  const saveHost = function () {
    api.trigger(GROUP, "saveHostSettings", name, sessionName, passwordToSave());
  };
  if (!s.steam) {
    return h("div", { style: S.error }, "Steam is not available. Skyline Together needs the Steam version of the game, with Steam running and logged in.");
  }
  return h(
    "div",
    null,
    h(
      "div",
      { style: S.section },
      h("div", { style: S.label }, "Your name (empty: Steam name)"),
      h(TextField, { value: name, onChange: setName, title: "Your name" })
    ),
    props.inGame
      ? h(
          "div",
          { style: S.section },
          h("div", { style: S.label }, "Host this city"),
          h(TextField, { value: sessionName, onChange: setSessionName, placeholder: "Session name (optional)", title: "Session name" }),
          h(TextField, { value: hostPassword, onChange: setHostPassword, placeholder: s.hasPassword ? "Password (unchanged)" : "Password (optional)", password: true, title: "Password" }),
          h(
            Button,
            {
              disabled: !s.canHost,
              onClick: function () {
                saveHost();
                api.trigger(GROUP, "host");
              },
            },
            "Host this city"
          )
        )
      : h("div", { style: merge(S.section, { color: C.dim }) }, "To host, load or start a city and open this panel there."),
    h(
      "div",
      { style: S.section },
      h("div", { style: S.label }, "Join a friend"),
      h(TextField, { value: target, onChange: setTarget, placeholder: "Host's Steam ID or IP:port", title: "Host" }),
      h(TextField, { value: joinPassword, onChange: setJoinPassword, placeholder: "Password (if any)", password: true, title: "Password" }),
      h(
        Button,
        {
          disabled: !s.canJoin || target.trim().length === 0,
          onClick: function () {
            saveHost();
            api.trigger(GROUP, "join", target, joinPassword);
          },
        },
        "Join"
      )
    ),
    s.lastEnd ? h("div", { style: S.error }, s.lastEnd) : null
  );
}

function Hosting(props) {
  const s = props.state;
  return h(
    "div",
    null,
    h(
      "div",
      { style: S.section },
      h("div", { style: S.label }, "Session"),
      h("div", null, s.sessionName),
      h("div", { style: { color: s.status === "Hosting" ? C.good : C.warn, marginTop: "4px" } }, s.status),
      s.sync ? h("div", { style: merge(S.label, { marginTop: "4px" }) }, s.sync) : null,
      h("div", { style: merge(S.label, { marginTop: "8px" }) }, "Friends join with your Steam ID"),
      h("div", { style: S.mono }, s.steamId),
      s.directPort > 0 ? h("div", { style: merge(S.label, { marginTop: "6px" }) }, "or your public IP address and port " + s.directPort) : null,
      h(NetLine, null)
    ),
    h(Players, { state: s }),
    h(Chat, { state: s, canSend: true }),
    h(Button, { kind: "danger", onClick: function () { api.trigger(GROUP, "leave"); } }, "End session")
  );
}

function Joining(props) {
  const s = props.state;
  return h(
    "div",
    null,
    h("div", { style: S.section }, s.status),
    h("div", { style: S.progressOuter }, h("div", { style: { width: Math.round((s.progress || 0) * 100) + "%", height: "100%", background: C.accent } })),
    h(Button, { kind: "secondary", onClick: function () { api.trigger(GROUP, "leave"); } }, "Cancel")
  );
}

function Client(props) {
  const s = props.state;
  return h(
    "div",
    null,
    h(
      "div",
      { style: S.section },
      h("div", { style: S.label }, "Session"),
      h("div", null, s.sessionName),
      h("div", { style: { color: C.good, marginTop: "4px" } }, s.status),
      h("div", { style: merge(S.label, { marginTop: "4px" }) }, "Game speed (set by the host): " + (s.speed || "")),
      s.sync ? h("div", { style: S.label }, s.sync) : null,
      h(NetLine, null)
    ),
    h(Players, { state: s }),
    h(Chat, { state: s, canSend: true }),
    h(Button, { kind: "secondary", onClick: function () { api.trigger(GROUP, "leave"); } }, "Leave session")
  );
}

// ---- title-screen "Multiplayer" button (in the vanilla main menu list, between Load Game and Editor) ----

const MAIN_MENU_PATH = "game-ui/menu/components/main-menu-screen/main-menu-screen.tsx";
const MENU_BUTTON_PATH = "game-ui/common/input/button/labeled-icon-button.tsx";
const MENU_THEME_PATH = "game-ui/menu/themes/menu-item.module.scss";
const MENU_ICON =
  "data:image/svg+xml;utf8," +
  encodeURIComponent(
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" fill="#ffffff">' +
      '<circle cx="11" cy="10" r="5"/><circle cx="22" cy="11" r="4"/>' +
      '<path d="M1 27c0-5.5 4.5-9 10-9s10 3.5 10 9z"/><path d="M19 27c0-3-1-5.6-2.8-7.3 1.5-.9 3.2-1.4 5-1.4 4.8 0 8.8 3.1 8.8 8.7z"/>' +
      "</svg>"
  );

// the title-screen button and the panel talk through this tiny event bus
const panelListeners = new Set();
function openPanel() {
  panelListeners.forEach(function (f) {
    try {
      f();
    } catch (e) {
      // a listener of an unmounted root
    }
  });
}
let menuButtonInstalled = false;

function installMainMenuButton(moduleRegistry) {
  try {
    const LabeledIconButton = moduleRegistry.get(MENU_BUTTON_PATH, "LabeledIconButton");
    const theme = moduleRegistry.get(MENU_THEME_PATH, "classes");
    if (!LabeledIconButton || !moduleRegistry.get(MAIN_MENU_PATH, "MainMenuNavigation")) return false;
    function SkylineTogetherMenuButton() {
      return h(LabeledIconButton, { tinted: true, src: MENU_ICON, theme: theme, onSelect: openPanel }, "Multiplayer");
    }
    moduleRegistry.extend(MAIN_MENU_PATH, "MainMenuNavigation", function (Original) {
      return function SkylineTogetherMainMenuNavigation(props) {
        try {
          const children = React.Children.toArray(props.children);
          // Continue, New Game, Load Game, then ours
          children.splice(Math.min(3, children.length), 0, h(SkylineTogetherMenuButton, { key: "skyline-together-multiplayer" }));
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

function makeRoot(inGame) {
  return function SkylineTogetherRoot() {
    const s = parse(api.useValue(stateBinding));
    const [open, setOpen] = React.useState(false);
    React.useEffect(function () {
      const show = function () {
        setOpen(true);
      };
      panelListeners.add(show);
      return function () {
        panelListeners.delete(show);
      };
    }, []);
    const mode = s.mode || "None";
    const count = (s.players || []).length;
    const dot = mode === "None" ? C.dim : mode === "Joining" ? C.warn : C.good;
    let content;
    if (mode === "Hosting") content = h(Hosting, { state: s });
    else if (mode === "Joining") content = h(Joining, { state: s });
    else if (mode === "Client") content = h(Client, { state: s });
    else content = h(Lobby, { state: s, inGame: inGame });
    return h(
      "div",
      null,
      inGame && (mode === "Hosting" || mode === "Client") ? h(NameTags, null) : null, // presence branch: under the panel
      inGame || !menuButtonInstalled
        ? h(
        "div",
        { style: S.toggle, onClick: function () { setOpen(!open); } },
        h("div", { style: merge(S.dot, { background: dot }) }),
        mode === "None" ? "Multiplayer" : mode === "Joining" ? "Joining…" : "Multiplayer · " + count
      )
        : null,
      open
        ? h(
            "div",
            { style: S.panel },
            h(
              "div",
              { style: S.header },
              h("div", { style: S.title }, "Skyline Together"),
              h("div", { style: { cursor: "pointer", padding: "0 4px", color: C.dim }, onClick: function () { setOpen(false); } }, "✕")
            ),
            h("div", { style: S.body }, content)
          )
        : null
    );
  };
}

const GameRoot = makeRoot(true);
const MenuRoot = makeRoot(false);

export default function register(moduleRegistry) {
  if (!ready) {
    console.warn("SkylineTogether: UI globals missing; the panel is unavailable (use the Options page)");
    return;
  }
  menuButtonInstalled = installMainMenuButton(moduleRegistry);
  moduleRegistry.append("Game", GameRoot);
  moduleRegistry.append("Menu", MenuRoot);
}
