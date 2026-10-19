# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

The first regular release. Skyline Together is available on
[Paradox Mods](https://mods.paradoxplaza.com/mods/163578/Windows) and on GitHub; both carry the same build, so players
of both can play together. Everyone in a session must use this version.

### Added

- **Third-party notices** (`THIRD-PARTY-NOTICES.txt`, also in the release zip) credit the LZ4 block format and the
  xxHash specification that Core's own implementations follow, and the game components Skyline Together uses.
- Release notes list the **SHA-256 checksums** of both zips, with a `Get-FileHash` line to check a download.

### Changed

- **Bug reports go to GitHub issues.** The texts in the game, the installer and the release zip say where to send the
  diagnostics file and logs. The guide explains how to collect them for a Paradox Mods install (which has no
  `Collect-Logs.cmd`) and what they contain: player names, Steam IDs, IP addresses of address bans and, in the game's
  `Player.log`, the Windows user name.
- **Safer log collection.** `Collect-Logs.cmd` replaces the Windows user name in the logs it collects and leaves crash
  dumps out; they can contain passwords and are collected only with `-IncludeDumps`, for sending privately.
- Clearer English and German texts: messages that ask for the diagnostics file say where to attach it, the
  diagnostics option says that the file contains player names and Steam IDs, and the German texts use consistent terms
  and German quotation marks.
- New screenshots on the Paradox Mods page.
- Versions follow Semantic Versioning without a pre-release suffix from this release on.

## [0.1.0-alpha.4] - 2026-10-16

Bug-fix release: fixes crashes of joining players when buildings are bulldozed and when citizens, households and
companies come and go. Everyone in a session must install this release. This is the first version on Paradox Mods.

### Fixed

- **Joining players crashed when they bulldozed something, and a few minutes into a session.**
  - Deleting an entity on a joining player's game (its own bulldozing, a despawn from the host, a household moving
    away) left references to it behind that the game follows without a check: citizens inside a demolished building,
    workers and pupils of a deleted workplace or school, passengers of a deleted vehicle, a mover's deleted owner, owned
    or attached objects of a deleted owner or parent.
  - Every deletion on a joining player's game now cleans every reference to the entity in the same frame, through an
    index of who refers to whom. The vehicles and creatures of a deleted owner, citizen or pet are removed with it,
    riders leave a deleted vehicle the way the game lets them out, and orphaned owned or attached objects are deleted
    (or detached and requested from the host) before the next frame's game systems run.
- **A crash preceded by "UpdateFrame added to unsupported type".**
  - Removing a household took the household link away from its pets, which the game needs to sort them. Citizens and
    pets are now deleted with their household, as the game does.
  - A new object that lacks a required component (an agent whose household has not arrived yet, an incomplete record)
    is no longer created; it is requested from the host again, and a last check before the game's update groups run
    deletes any such object and names its components in the log.
- **Companies and citizens requested from the host over and over after joining.** The host sent agent lists with
  workers and workplaces that no longer exist, which the joining player removed and then requested again. Records and
  integrity hashes now leave out list members and links whose target is gone, on both sides.

## [0.1.0-alpha.3] - 2026-10-16

Bug-fix release with crash fixes found in multiplayer sessions (bulldozing elevated roads, households and companies on
joining players, joining without a mod whose data is in the city), plus **Join anyway**, aircraft that keep moving and
elevation in building previews. Protocol 5: everyone must install this release.

### Added

- **Join anyway with different mods.** When the host requires the same mods or asset packs, a player whose mods differ
  still sees the list of differences and can choose **Join anyway**, with a warning that the cities can drift apart or
  behave differently. The host is told who joined with which differences. New host option **Let players join with
  different mods** (on by default; off makes those refusals final). It never skips a different build, game version,
  missing DLC, the password, bans or a full session.
- **Refusal of the old "CS2 Multiplayer Mod".** When it is enabled on either PC (found by name or Paradox Mods id), the
  join is refused with "Disable the 'CS2 Multiplayer Mod' – two multiplayer mods at once conflict" on the side that
  has it; a host that runs it sees a warning in the panel and a notification when hosting starts.
- **Paradox Mods publishing.** The page configuration (`src/SkylineTogether/Properties/PublishConfiguration.xml`), a
  thumbnail and screenshots (`tools/paradox/make_images.py`), and `tools/paradox/publish_paradox.py`, which checks the
  version, changelog and images, scans the upload for personal data and runs the game's own ModPublisher on the files
  of the release zip (`docs/dev/publishing-paradox-mods.md`).
- **Installed twice.** When the game finds Skyline Together more than once (the local Mods folder and a Paradox Mods
  subscription), a notification, the log and the diagnostics name every copy with its build id and say how to keep
  one. The installer warns when the mod is also subscribed on Paradox Mods.
- The log and the diagnostics say where the mod was loaded from (local Mods folder, or Paradox Mods id and version;
  never a path).
- **Building previews in 3D.** Other players' elevated roads, bridges and raised objects are shown at their real
  height, with a faint shadow and thin drop lines to the ground; tunnels are drawn dashed on the surface with a
  downward arrow. The name tag shows the elevation ("building Small Road (+10 m)").

### Changed

- Network protocol 5: earlier versions are refused with "Skyline Together versions differ".
- DLC the joining player lacks is reported before different code mods.
- The building preview inside presence messages has its own format version 2 (heights, tool elevation, elevated and
  underground pieces); a preview of another version is ignored.

### Fixed

- **Crash when joining without a mod that stores data in the city.**
  - A player who lacked one of the host's mods that save their own data in the city (for example Zoning Toolkit) was
    admitted when the host did not require the same mods or the player chose **Join anyway**, and the game crashed
    while loading the city.
  - Such a player is now always refused, whatever the host's options say, with the screen **Mods required for this
    city** and no **Join anyway**; other differences stay overridable. The joining player's game checks again right
    before it loads the city, and the host's log lists the mods every joiner needs.
- **Crash when a joining player bulldozes (elevated) roads.**
  - The host's game could receive a change with a road segment that did not name its nodes, or a node removed under a
    segment that stays, and crashed natively.
  - The joining player now checks its own change before sending it; the host checks every change against its own city
    and refuses it as a whole if anything would dangle (it is rolled back on the player's side); both sides remove a
    dangling segment or object cleanly and request it again.
- **Joining players' game crashing shortly after joining**, even with nobody building.
  - When households, citizens and companies disappeared on a joining player, buildings, hotels, companies and
    households kept naming them, and the next panel or game job that read such a list crashed.
  - Joining players now do the game's own clean-up the moment an agent goes (renters, guests, employees, pupils,
    patients, occupants, household members, homes, hotels, workplaces and schools), never take an empty entry from the
    host, and repair any dangling link within 20 seconds.
- **Build id.** The id shown in game differed from the installer's ("(mvid)" in the log). The DLL is now found through
  the game's mod manager, for local and Paradox Mods installs, and the panel shows the installer's id.
- **Wrong traffic routes on joining players** after selecting a building or vehicle: joining players now see no
  traffic routes for a selection; the host's are unchanged.
- **Planes stuck in the sky on joining players.** Aircraft now move on every joining player's game anywhere on the
  map, ships and mainline trains up to 4 km from the camera.
- **Vehicles and pedestrians only one player has.** The host sends a roster of its moving vehicles and pedestrians;
  joining players remove those the host does not have and request the ones they lack.
- **Households repaired over and over.** The host now detects citizens moving between households within a frame or
  two, so the integrity check no longer repairs them repeatedly.
- The log names the reason when a joining player's route tool closes and the host re-sends its transport lines.

## [0.1.0-alpha.2] - 2026-10-16

Bug-fix release.

### Fixed

- Joining players' building markers (schools, police stations, stops and others shown while an info view or a build
  menu is open) no longer stay on screen after it closes.

## [0.1.0-alpha.1] - 2026-10-16

First public release (pre-release). Back up your saves before playing.

### Added

- **Sessions**: host a loaded city, join by Steam ID, direct IP (optional, UDP port 27015 by default) or Steam invite;
  password protection with a challenge/response handshake (PBKDF2-HMAC-SHA256, 10 000 rounds); player limit (2-16,
  default 8); version, build and mod checks with a readable refusal screen that lists the mods to enable, disable or
  update. Network protocol 3.
- **Joining**: the host's game saves the city and streams it to the joining player, whose game loads it through the
  normal load path; the host pauses while someone joins; join progress with download size, speed and time left; map and
  climate assets bundled with the city; fast joins (background transfer, Steam send rate control, one Steam lane per
  traffic class).
- **DLC, asset packs and mod settings** are compared on join: a player without one of the host's DLC or content packs
  is refused with the list; different asset packs and mod settings are reported to the joining player, or refused with
  the host option **Require the same asset packs**.
- **Recovery**: automatic reconnect after a lost connection (up to 6 attempts with backoff, city paused meanwhile),
  then a choice between reconnecting, the main menu or an offline copy.
- **Host-authoritative sync**: structures (roads, buildings, zoning, districts, transport lines and their state),
  economy and city state (taxes, budget, fees, loan, policies, names, milestones and unlocks), terrain and water, cell
  maps and statistics, simulation values for info panels, utility flows, traffic lights, tree growth, events and
  disasters, chirps, followed citizens, citizens, households, pets and companies, moving vehicles and pedestrians.
- **Player actions in both directions**: every player action is predicted locally with the game's own tools, validated
  by the host (role, conflicts, rate limits), committed and replicated; refused actions are rolled back. The host
  charges at least the normal price for roads, tracks, pipes, power lines and trees that other players build.
- **Build checks** (host option **Enforce build checks on other players**, off by default): the host refuses builds a
  normal game cannot make (locked prefabs and zone types, map tiles the city does not own, shapes outside the game's
  limits). Anarchy, Line Tool, Find It, Plop the Growables and direct-write mod tools such as Move It relax the checks
  they legitimately bypass. When the option is off, such builds are only logged.
- **Integrity**: rolling hash verification of replicated objects, per-object repair, host-authoritative warning icons
  and transport line checks; when repairs cannot keep up, a bucket is re-sent as a whole, and massive divergence offers
  the player a rejoin with a fresh copy of the city.
- **Game speed**: the host's speed is everyone's speed; builders' speed requests are applied for everyone.
- **Players**: roles (host, co-host, builder, viewer) with permissions, kick and ban (with an unban list in the host
  options), player list with ping, cursor rings with name tags and activity, live building previews of other players in
  their colour, chat.
- **Access control**: "Friends and invited only" join policy; wrong-password lockout per Steam ID and address; the
  password is checked before any version or mod detail is revealed; direct IP connections always need the password;
  refusals for banned, not invited and a host that is shutting down.
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
- **Solo test**: "Record test session" records what a joining player receives; "Replay & verify" replays it through
  the real client path without network and writes a verdict.
- **Player setup**: a self-contained installer (`Install.cmd`) with checks, a build id, log collection
  (`Collect-Logs.cmd`) and a two-player checklist.
- **Project**: MIT licence, user guide and developer documentation, contributing guide, security policy, issue and pull
  request templates, GitHub Actions CI (Core build and tests without the game, UI smoke test, link and personal-data
  checks), `.editorconfig` and `.gitattributes`.

[Unreleased]: https://github.com/ljkx/SkylineTogether/compare/v0.1.0-alpha.4...main
[0.1.0-alpha.4]: https://github.com/ljkx/SkylineTogether/releases/tag/v0.1.0-alpha.4
[0.1.0-alpha.3]: https://github.com/ljkx/SkylineTogether/releases/tag/v0.1.0-alpha.3
[0.1.0-alpha.2]: https://github.com/ljkx/SkylineTogether/releases/tag/v0.1.0-alpha.2
[0.1.0-alpha.1]: https://github.com/ljkx/SkylineTogether/releases/tag/v0.1.0-alpha.1
