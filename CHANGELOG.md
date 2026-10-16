# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.0-alpha.3] - 2026-10-16

Third test build: crash fixes from the first real sessions (bulldozing elevated roads, households and companies on
joining players, joining without a mod whose data is in the city), join anyway, moving planes, elevation in building
previews. Protocol 5: everyone must install this build.

### Added

- **Join anyway with different mods**: when the host requires the same mods or asset packs, a player whose mods differ
  still sees the list of differences and can choose **Join anyway** (with a warning that the cities can drift apart or
  behave differently). The host is told who joined with which differences (notification and chat line). New host
  option **Let players join with different mods** (on by default; off makes those refusals final). It never skips a
  different build, game version, missing DLC, the password, bans or a full session.
- **Paradox Mods publishing**: the page configuration (`src/SkylineTogether/Properties/PublishConfiguration.xml`), a
  thumbnail and screenshots (`tools/paradox/make_images.py`), and `tools/paradox/publish_paradox.py`, which checks the
  version, changelog and images, scans the upload for personal data and runs the game's own ModPublisher on the files of
  the tested release zip (`docs/dev/publishing-paradox-mods.md`). Nothing is uploaded without the maintainer's
  confirmation; the default build is unchanged.
- **Installed twice**: when the game finds Skyline Together more than once (the local Mods folder and a Paradox Mods
  subscription), a notification, the log and the diagnostics name every copy with its build id and say how to keep
  one. The installer warns when the mod is also subscribed on Paradox Mods.
- The log and the diagnostics say where the mod was loaded from (local Mods folder or Paradox Mods id and version,
  never a path).
- **Building previews in 3D**: other players' elevated roads, bridges, raised nodes and objects placed at an
  elevation are shown at their real height, with a faint shadow on the ground and thin drop lines down to the terrain;
  tunnels are drawn dashed and dimmer on the surface with a downward arrow. Raising or lowering a road with the
  elevation steps is visible to everyone as it happens, and the name tag shows the net tool's elevation
  ("building Small Road (+10 m)").

### Changed

- Wire change, protocol 5 (protocol 4: the hello carries a join flags byte; protocol 5: the mover roster record):
  earlier builds are refused with "Skyline Together versions differ".
- DLC the joining player lacks is now reported before different code mods.
- The tool preview inside presence messages has format version 2 (heights with their own precision, the tool
  elevation, elevated and underground pieces); it has its own version byte, so the protocol version is unchanged and
  a preview of another version is ignored.

### Fixed

- **Crash when joining without a mod that stores data in the city**: a player who lacked one of the host's mods that
  save their own data in the city (observed with Zoning Toolkit: "Not serializable type:
  ZoningToolkit.Components.ZoningInfo" in Player.log) was admitted when the host did not require the same mods or the
  player chose **Join anyway**, and the game crashed while loading the city. Such a player is now always refused, whatever the host's
  options say, with the screen **Mods required for this city** ("Enable these mods to join: ZoningToolkit (this city
  contains its data – loading it without the mod would crash the game)") and no **Join anyway**; other differences
  stay overridable and are listed below. The host decides from its own mods (saved component types and saved system
  state found by the mod analysis, a failed scan, or a built-in list) and counts the entities in the city that carry
  each mod's data for the wording. The joining player's game checks the host's list again right before it loads the
  city and stops instead of loading. The host is told who could not join and why, and its log lists the mods every
  joiner needs.
- The old "CS2 Multiplayer Mod" (by name or Paradox Mods id) on either PC now refuses the join with "Disable the 'CS2
  Multiplayer Mod' – two multiplayer mods at once conflict" on the side that has it; a host that runs it sees a
  warning in the panel and a notification when hosting starts.
- The build id in game differed from the installer's ("build ... (mvid)" in the log): the game loads mods from the
  DLL's bytes, so the DLL's location was empty. The DLL is now found through the game's mod manager, for local and
  Paradox Mods installs, and the panel shows the installer's id.
- Joining players no longer see wrong traffic routes after selecting a building or vehicle (the info panel's routes
  button): the routes were built from the path data of the moment they joined, so they started where a vehicle used to
  be and ran across the map in straight lines. Joining players now see no traffic routes for a selection; the host's
  are unchanged.
- The log names the reason when a joining player's route tool closes and the host re-sends its transport lines.
- **Planes stuck in the sky on joining players**: vehicles and pedestrians move on clients only inside the motion
  interest around the camera (at most 1 km); everything farther away stays where it was until a rare position refresh.
  An airplane far away is still in plain view, so it hung in mid-air where the host's plane had long left. Aircraft
  (airplanes, helicopters, rockets) now move on every client anywhere on the map, ships and mainline trains up to 4 km
  from the camera; beyond the normal interest they are updated at half rate.
- **Vehicles and pedestrians only one player has**: a mover the host had removed could stay on a client for the rest of
  the session (a despawn that failed, a mover the host lost without one, or one the joining player's snapshot still
  held). The host now sends a roster of its movers (every id about every half minute, and a fast pass right after a
  player joins); clients remove movers the host does not have and ask for the ones they lack.
- **Households repaired over and over**: the integrity check kept re-sending households (and whole buckets of agents)
  in an active city. Citizens move between households without the game tagging anything (students moving out, couples
  moving together, divorces), and the host found such moves only when its rolling agent scan reached them, which in a
  large city takes longer than the check waits before it repairs an agent. The host now watches the membership
  components' chunk change versions and streams a move within a frame or two, and a household's member list counts as
  a key link like the citizen's own household.
- **Crash when a joining player bulldozes (elevated) roads**: the host's game (and earlier the joining player's) crashed
  natively after a joining player bulldozed an elevated road (bulldozing often merges the remaining segments of a node
  into a new one). A change could reach the host with a new road segment that did not name its nodes (it was written
  before its new node had an id) or with a node removed under a segment that stays, and the host applied it; its
  safety net only left such a segment untagged, which the game's jobs still follow. Now:
  - the joining player binds every new object of a change before writing any of it, keeps the ids it deletes until the
    change is sent, and checks its own change before sending it (a log line names what would dangle);
  - the host checks every change of another player against its own city after the change (every kept road segment
    ends at two existing nodes, a removed node takes all its segments with it, owned objects keep their owner) and
    refuses it as a whole if anything would dangle, so it is rolled back on the player's side (`Invalid` or
    `Conflict`, with one log line naming the problem);
  - both sides apply the host's updates with the same rule: a new segment without valid nodes is not created, and a
    segment or object that would dangle (its node or owner gone) is removed cleanly instead of being kept, then asked
    for again.
- **Joining players' game crashing shortly after joining** (a native crash with no error, even with nobody building):
  when households, citizens and companies disappeared on a joining player (the host's despawns, or entities the
  player's game removed itself), buildings, hotels, companies and households kept naming them: a departed tourist stayed
  in its hotel's guest list, dead workers stayed in their company's employee list, and the join snapshot itself could
  carry employee entries naming nobody (the game saves a worker that is already gone that way). The game reads these
  lists without checking, so the next panel or job that touched one crashed. Joining players now do the game's own
  clean-up the moment an agent goes (renters, guests, employees, pupils, patients, occupants, household members, homes,
  hotels, workplaces and schools), also for what the player's own game deletes, and never take an empty entry from the
  host. After every update they apply, the objects it touched are checked, and a background check repairs any dangling
  link it finds within 20 seconds (instead of only logging it) and asks the host for the object again.

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

[Unreleased]: https://github.com/ljkx/SkylineTogether/compare/v0.1.0-alpha.3...main
[0.1.0-alpha.3]: https://github.com/ljkx/SkylineTogether/releases/tag/v0.1.0-alpha.3
[0.1.0-alpha.2]: https://github.com/ljkx/SkylineTogether/releases/tag/v0.1.0-alpha.2
[0.1.0-alpha.1]: https://github.com/ljkx/SkylineTogether/releases/tag/v0.1.0-alpha.1
