# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

First regular release: safer joining and passwords, a viewer-first option for hosts, and fixes for crashes and
differences on joined players' games. Skyline Together is available on
[Paradox Mods](https://mods.paradoxplaza.com/mods/163578/Windows) and on GitHub with the same build, so players of both
can play together. Everyone in a session must use this version; earlier versions cannot join (network protocol 7).
From this release on, versions have no pre-release suffix.

### Added

- **New players join as viewers** (host option, off by default): players who join a session for the first time can
  watch and chat, but not build, until the host or a co-host makes them builders.
- **Roles are remembered** for the session: a player who rejoins gets the same role (viewer, builder or co-host).
- **Uninstall cleans up**: the installer's uninstall also removes the mod's temporary files, and its settings (session
  password, ban list) after a confirmation or with `-RemoveSettings`.
- **Checksums**: the release notes list the SHA-256 checksums of both zips, with a `Get-FileHash` line to check a
  download.
- **Third-party notices** (`THIRD-PARTY-NOTICES.txt`, also in the release zip) credit the LZ4 block format and the
  xxHash specification that Core's own implementations follow, and the game components Skyline Together uses.

### Changed

- **Players of earlier versions cannot join** this version, and solo test recordings made with earlier versions cannot
  be replayed (network protocol 7).
  - Developer detail: hardened handshake, id generation in entity records, painted-cell masks in resource paint
    patches, statistics appends.
- **Messages from the host appear in each player's language**: chat lines such as "… joined the city", kick, ban and
  end reasons, and refusal details.
- **Clearer join screens.** A password lockout has its own screen ("Too many wrong passwords") instead of "Wrong
  password", and a host that cannot prove that it knows the password has its own screen ("Couldn't confirm the host")
  instead of "Joining failed".
- **Bug reports go to GitHub issues.** The texts in the game, the installer and the release zip say where to send the
  diagnostics file and logs; the guide explains how to collect them for a Paradox Mods install (which has no
  `Collect-Logs.cmd`) and what they contain.
- **Your Windows user name stays out of the logs.** Skyline Together's log, the diagnostics file and its notifications
  write paths in your user folder as `%USERPROFILE%`, and a joining player's log shortens the host's address after a
  direct join (`203.0.113.x`). `Collect-Logs.cmd` replaces the user folder in the game's logs too, and leaves crash
  dumps out: they can contain passwords and are collected only with `-IncludeDumps`, for sending privately.
- **Dropped, not kicked.** A player who stops responding, cannot keep up or whose changes keep failing is
  disconnected rather than kicked, so their game offers to join again.
- **Clear names in chat.** Player names are unique in a session and never the host's; the host's chat lines carry a
  badge, the session's own lines an info mark. A joining player's load error is no longer posted to everyone.
- **Clearer English and German texts**: no technical terms where a player reads them, button names in quotes, one name
  per setting ("Show network stats", "session password"), and consistent German terms and quotation marks.
- New screenshots on the Paradox Mods page, among them the screen for different mods.

### Security

Joining and passwords are harder to abuse. Details: [SECURITY.md](SECURITY.md).

- **Strangers learn nothing before the password.** Bans and the friends-only rule are checked first, and only hello,
  control and ping messages are read before admission.
  - 64 KiB until the password is proven; message kinds checked before anything is decompressed; 8 MiB per player per
    frame; the network poll keeps its time budget after every message; unfinished fragmented messages dropped after two
    minutes; pings not answered before admission.
- **Passwords are harder to guess and to relay.** The password check is mutual and bound to the connection, and uses
  200 000 PBKDF2 rounds instead of 10 000.
  - The proof covers the host's Steam ID as dialled, both nonces and the session; the host proves the password back
    before the joining game sends its mod lists; the key is derived off the main thread.
- **No one can take over a player's place.** A connection with the Steam ID of a player in the session can no longer
  replace that player before it has proven the password.
  - Connections Steam flags as unauthenticated are identified by their address, and only the direct-IP socket accepts
    them.
- **Joins can no longer stall the host.** The host drops a join whose download or load makes no progress, caps
  re-sends of the city, and makes a player whose joins keep failing wait (30 seconds up to 30 minutes).
  - Handshakes are limited separately from players (8 at once, 2 per Steam ID or address, 10 seconds for the hello),
    and the large receive buffer is given only to admitted players.
- **Bans also cover the address** of a player who joined by direct IP; IPv6 bans and password lockouts cover the /64.
- **Steam invites** are followed only while the lobby belongs to the host it names.
- **Bundled assets stay in their folder.** A host's bundled assets are added only as prefab and texture assets with
  plain names inside the join folder.
- **Logs stay small**: rate-limited and capped at 64 MiB per game start, and per-player warnings no longer grow with
  names a player chooses.

### Fixed

Fixes for crashes and differences on joined players' games, most of them cases where an object's data could end up on
another object.

- **Buildings no longer turn into roads on joined players.** Every object record carries its id's generation: a joined
  player whose game still holds an older object under a reused id deletes it cleanly and creates the new one.
- **Deletions are no longer lost** when an error happens later in the same frame. The host frees the id of a deleted
  object only after the update carrying the deletion was sent, and one failing object no longer costs the frame's
  whole update.
- **No values on the wrong object.** Citizen, company and attention values that wait behind a catch-up can no longer be
  applied to ids reused meanwhile; kept values never point at objects deleted in the same update, and a line whose
  stops are gone is re-sent.
- **Failed updates are cleaned up.** A joined player's game acknowledges a failed update only after applying its
  deletions, otherwise it offers a rejoin; new objects are never kept half made, and a change the host fails to apply
  is rolled back on the player's game.
- **Terrain sync stays on.** One player's terrain strokes the host cannot replay (another mod's brush) no longer
  switch terrain sync off for everyone; such strokes are checked first, and a player whose strokes keep failing is
  ignored.
- **Mods behave on joined players.** A joined player's copy of a mod system no longer sends its own recomputed values
  to the host as the player's edits; mods whose panels write the city count as world-affecting; mod systems switched
  off on joined players stay off when the mod switches them on again; mods always see the session end.
- **Events, disasters and chirps** deleted after the events feature switched itself off are deleted for everyone.
- **Resource painting** merges only the painted cells (it overwrote nearby groundwater with older values) and keeps
  groundwater consistent.
- **Vehicles and passengers.** Riders of a deleted vehicle that its passenger list missed leave it; a vehicle a
  player's game cannot create (a missing asset) is asked for less and less often; no duplicate passenger entries while
  catching up.
- **Fewer needless repairs.** The integrity check no longer re-sends whole areas during a host's bulk edit or a
  catch-up, and the resync offer no longer counts objects given up in earlier sessions.
- **Smoother play on joined players.** The orphan check reads the deleted objects' own lists and scans everything at
  most once a second, and the index of who references whom no longer grows for the whole session.
- **Less network traffic in older cities**: statistics send only the new samples instead of every series' whole
  history (megabytes) at every sample.
- **Max players** raised while hosting takes effect at once (later joiners were refused at the old limit).
- **Joining by host name** no longer freezes the game while the name is looked up.
- **Overlapping joins**: joining is not possible while an earlier join's city is still loading, and an earlier join's
  asset import no longer starts loading a newer, incomplete download.
- **Installer**: works in user folders whose names contain `[` or `]`, keeps the installed copy when an update fails,
  and with `-NoPause` stops instead of waiting while the game is running.
- Smaller fixes: GPU readbacks time out, flow status messages are dropped while that feature is off, a tool edit of an
  object the host updated in the same frame is sent, and the icon filter and viewer gate recover in the next session.

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
