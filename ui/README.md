# ui/

Home of the hand-written UI module (no bundler, no Node dependency at runtime; see
[ARCHITECTURE.md](../docs/ARCHITECTURE.md#ui-module)):

- `SkylineTogether.mjs`: the ES module the game loads from `coui://ui-mods/SkylineTogether.mjs`. Its first block
  comment must contain the exact line `Cities: Skylines II UI Module`, followed by `Id:`, `Author:`, `Version:` and
  `Dependencies:`. It renders nothing when the UI globals are missing.

`register(moduleRegistry)`:

- inserts a "Multiplayer" entry into the vanilla main menu list (extends `MainMenuNavigation` of
  `game-ui/menu/components/main-menu-screen/main-menu-screen.tsx`, after Load Game, with the game's
  `LabeledIconButton` and the `menu-item` theme); it opens the multiplayer screen of the `Menu` root,
- appends a button to `GameTopLeft` (the game's `FloatingButton`, badge with the player count),
- appends the `Game` root (panel, chat dock, name tags, join overlay, error screen) and the `Menu` root.

Game components come from `window["cs2/ui"]` (Button, Panel, Tooltip, FloatingButton) and the module registry
(LabeledIconButton, menu theme, Checkbox, FOCUS_DISABLED). Everything is feature-detected; an error boundary switches
to a built-in look (modelled on the game's styles, using the game's CSS custom properties) when one throws.

Bindings (group `skylineTogether`): `state`, `join`, `net`, `presence`, `perf` (the performance overlay) from
`SessionUISystem`; `replay` from `ReplayUISystem`; `friends` and `overlayAvailable` from `InviteUISystem`. Triggers:
`ban`, `cancelReconnect`, `chat`, `copySteamId`, `copyText`, `dismissEnd`, `dismissResync`, `host`, `inviteFriend`,
`inviteFriends`, `join`, `joinAnyway`, `kick`, `leave`, `mainMenu`, `openInviteOverlay`, `reconnect`, `recordStart`,
`recordStop`, `refreshFriends`, `replayRefresh`, `replayStart`, `replayStop`, `resync`, `retryJoin`, `saveHostOptions`,
`saveHostSettings`, `setAllowJoinWithDifferentMods`, `setFriendsOnly`, `setNewPlayersViewer`, `setRequireSameAssets`,
`stayOffline`,
`toggleCoHost`, `toggleNetHud`, `togglePerfHud`, `toggleRole`, `unban`, `writeDiagnostics`. `tools/ui_smoke.mjs`
checks that each is declared on the C# side.

The `Version` in the module header is the UI module's own version for the game's module registry; it is independent
of the release version.

Texts: every string is a key (`st.ui.*`) translated through `cs2/l10n` from `src/SkylineTogether/Data/locale.<id>.json`
(C# `LocaleSource`), with the module's `EN` table as the fallback.

Checks: `node --check ui/SkylineTogether.mjs` and `node tools/ui_smoke.mjs` (renders every state with and without the
game components, including malformed and extreme data; checks bindings and triggers against `SessionUISystem.cs`, the
texts against the locale tables and, with the game installed, the CSS tokens against the game's stylesheet).
`node tools/ui_smoke.mjs --html <folder>` writes HTML snapshots of every state to look at in a browser.

The game project's `DeployMod` target copies `SkylineTogether.mjs` next to the DLLs when the file exists.
