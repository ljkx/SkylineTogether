# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.0-alpha.2] - 2026-10-16

Second test build: a fix for joining players.

### Fixed

- Joining players' building markers (schools, police stations, stops ... shown while an info view or a build menu is
  open) no longer stay on screen after it closes: the icon filter dropped the game's marker removals.

## [0.1.0-alpha.1] - 2026-10-16

First public test build (pre-release). It has not been through a complete two-player session yet; expect bugs and play
on a copy of a city.

### Added

- **Sessions**: host a loaded city, join by Steam ID, direct IP (optional, UDP port 27015 by default) or Steam invite;
  password protection with a challenge/response handshake; player limit (2-16, default 8); version, build and mod
  checks with a readable refusal screen that lists the mods to enable, disable or update.
- **Joining**: the host's game saves the city and streams it to the joining player, whose game loads it through the
  normal load path; the host pauses while someone joins; join progress with download size, speed and time left; map and
  climate assets bundled with the snapshot; faster joins (background transfer, Steam send rate control, snapshot reuse).
- **Recovery**: automatic reconnect after a lost connection (up to 6 attempts with backoff, city paused meanwhile),
  then a choice between reconnecting, the main menu or an offline copy.
- **Host-authoritative sync**: structures (roads, buildings, zoning, districts, transport lines and their state),
  economy and city state (taxes, budget, fees, loan, policies, names, milestones and unlocks), terrain and water, cell
  maps and statistics, simulation values for info panels, utility flows, traffic lights, tree growth, events and
  disasters, chirps, followed citizens, citizens, households, pets and companies, moving vehicles and pedestrians.
- **Player actions in both directions**: every player action is predicted locally with the game's own tools, validated
  by the host (role, conflicts, rate limits), committed and replicated; refused actions are rolled back.
- **Integrity**: rolling hash verification of replicated objects, per-object repair, host-authoritative warning icons
  and transport line checks.
- **Game speed**: the host's speed is everyone's speed; builders' speed requests are applied for everyone.
- **Players**: roles (host, co-host, builder, viewer) with permissions, kick and ban (with an unban list in the host
  options), player list with ping, cursor rings with name tags and activity, live building previews of other players in
  their colour, chat.
- **Access control**: "Friends and invited only" join policy; wrong-password lockout per Steam ID and address; the
  password is checked before any version or mod detail is revealed; refusals for banned, not invited and a host that is
  shutting down.
- **Resync**: when object repairs cannot keep up, a bucket is re-sent as a whole, and massive divergence offers the
  player a rejoin with a fresh copy of the city.
- **UI**: Multiplayer page in the title screen menu, in-game panel, chat dock, join and error screens, notifications,
  Steam friends invite list, Options page as a fallback; English and German texts.
- **Mod compatibility**: classification of code mods into world-affecting and local-only, adapters for 12 mods
  (Traffic, Move It, Anarchy, Better Bulldozer, Tree Controller, Water Features, Plop the Growables, 529 Tiles,
  Historical Start, Zoning Toolkit, Line Tool, Find It), a watcher for mod state, host-owned mod settings, reporting of
  missing assets.
- **Support for any mod**: every loaded code mod is analysed automatically (components, systems and their update
  phases, tools, runtime prefabs, settings, Harmony patches; no mod code runs) and handled by conservative rules that the
  diagnostics explain; built-in adapters switch themselves off when their mod changed; an integration API for mod
  authors without a reference to Skyline Together (declarations, session events, a host-relayed mod channel, see
  `docs/MOD-INTEGRATION.md`); missing assets name the asset pack (with its Paradox Mods id) or mod and what to do, also
  before the joining player's load; deterministic runtime prefabs bind across PCs by type and name.
- **Diagnostics**: startup self-test of every game internal used (failed checks switch their feature off), guarded
  features, "Write diagnostics" report, network line, performance counters.
- **Solo testing**: "Record test session" records what a joining player receives; "Replay & verify" replays it through
  the real client path without network and writes a verdict.
- **Player setup**: a self-contained installer (`Install.cmd`) with checks, a build id, log collection
  (`Collect-Logs.cmd`, including crash reports) and a two-player test checklist.
- **Project**: MIT licence, user guide and developer documentation, contributing guide, security policy, issue and pull
  request templates, GitHub Actions CI (Core build and tests without the game, UI smoke test, link and personal-data
  checks), `.editorconfig` and `.gitattributes`.

### Changed

- Join check of DLC, asset packs and mod settings: a player without one of the host's DLC or content packs is refused
  with the list; different asset packs (by Paradox Mods id and contents) and mod settings are told to the joining
  player right after admission; the new host option "Require the same asset packs" refuses them instead. The handshake
  lost its never-filled fields (protocol version 3).
- Steam connections use one Steam lane per traffic class, so snapshot chunks no longer hold back control messages,
  chat and presence (only after a startup self-test of the lane calls; otherwise everything shares one lane as before).
- Reserved message and control kinds are dropped on receipt before any listener sees them.
- `SkylineTogether.Core` builds against the .NET SDK's netstandard2.1 reference assemblies when the game is not
  installed (compile check for CI); the real build is unchanged.
- One build id everywhere: the game compares and shows the installer's id (the start of the DLL's SHA-256) in the
  panel footer, refusals, the log and the diagnostics.
- The password key is derived with PBKDF2-HMAC-SHA256 (10 000 rounds) instead of a single SHA-256.
- UI polish: inline checks of the join address and the direct port, a pasted Steam profile link keeps only the Steam
  ID, every refusal and failure screen says what to do next (new screen for a city that failed to load), Esc closes
  panels and the error screen (Enter retries there), keyboard focus on the built-in buttons, the chat keeps its place
  while you scroll up (with a "new below" button), shows times and a length counter, and wraps long words; text
  contrast raised to WCAG AA on the panels; repeated notifications replace each other instead of stacking; clearer
  English and German texts.
- The host charges a forged or mod-tool proposal at least the vanilla price of the roads, tracks, pipes, power lines
  and free-standing trees it creates (moving with mod tools stays free); game speed requests are rate-limited per
  player; viewers may send mod channel messages only of kinds the mod declares with `viewer-message`.
- Protocol version 2; every wire change now increments it (see `docs/dev/releasing.md`).
- The host refuses structure proposals a vanilla client cannot make: prefabs and zone types its progression has not
  unlocked, new or moved structures on map tiles the city does not own, shapes outside the game's limits (outside the
  map, edges off their nodes or too long, crossed area outlines), prefab swaps across classes, proposals without a
  header and proposals based on a state of the city too old for the conflict check. Anarchy, Line Tool, Find It, Plop
  the Growables and direct-write mod tools such as Move It relax the checks they legitimately bypass. The toast for a
  refused change names the reason in the player's language.
- Less work per frame: decoded records are pooled, scanned entities are hashed from the record just written, deferred
  and repair records are encoded once for all peers, link statistics are queried once per peer and frame, and clients
  hash edit baselines only when a tool may need them and detect global-state edits once per frame.

### Fixed

- Client crash when chirps were created locally (the chirp system stays enabled; local chirps are dropped instead).
- Client crash on bulldozing, a repair storm in the integrity check and host lag.
- The title screen Multiplayer page rendered nothing in game.
- Many sync fixes found by the sync coverage audit; see [SYNC-COVERAGE.md](docs/design/SYNC-COVERAGE.md).
- Direct IP connections without Steam authentication were admitted without a password, and a Steam ID claimed in the
  hello could replace another player's connection; both are refused now.
- A player removed after loading the city saw "The session has ended" instead of the removal.
- Proposals still queued on the host when their player left were applied afterwards, and could count as violations
  against the next player who got the same id; they are dropped now.
- The UI could throw (and disappear) on unexpected binding data (a missing field, a value of the wrong type, a null in
  a list); saving before the first state arrived could store "keep" as the host password; the player list re-rendered
  with every cursor movement.

[Unreleased]: https://github.com/ljkx/SkylineTogether/compare/v0.1.0-alpha.2...main
[0.1.0-alpha.2]: https://github.com/ljkx/SkylineTogether/releases/tag/v0.1.0-alpha.2
[0.1.0-alpha.1]: https://github.com/ljkx/SkylineTogether/releases/tag/v0.1.0-alpha.1
