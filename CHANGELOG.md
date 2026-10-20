# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

First regular release: big cities can be joined on slower PCs, safer joining and passwords, a viewer-first option for
hosts, and fixes for crashes and differences on joined players' games. Skyline Together is available on
[Paradox Mods](https://mods.paradoxplaza.com/mods/163578/Windows) and on GitHub with the same build, so players of both
can play together. Everyone in a session must use this version; earlier versions cannot join (network protocol 8).
From this release on, regular releases have no pre-release suffix.

### Added

- **New players join as viewers** (host option, off by default): players who join a session for the first time can
  watch and chat, but not build, until the host or a co-host makes them builders.
- **Roles are remembered** for the session: a player who rejoins gets the same role (viewer, builder or co-host). A
  player who joined by direct IP without Steam is remembered by address only as a viewer (an address can be shared).
- **Uninstall cleans up**: the installer's uninstall also removes the mod's temporary files, and its settings (session
  password, ban list) after a confirmation or with `-RemoveSettings`.
- **Checksums**: the release notes list the SHA-256 checksums of both zips, with a `Get-FileHash` line to check a
  download.
- **Startup check against game updates**: when the game starts, every game type and member Skyline Together uses is
  looked up. Every part of Skyline Together that builds on a game class or interface is also checked, so a game
  interface that gained a member is found. If a game update broke something, only the part of Skyline Together that
  uses it is switched off, instead of an error in the middle of a session:
  - Hosting and joining are blocked only if that part is essential, with the reason.
  - Optional parts are switched off on their own: city statistics, traffic lights, difference repairs, terrain and
    water.
  - Diagnostics and solo testing only log a warning.
  - Developer detail: every type of the game layer is mapped to its feature, and a test fails for a type without a
    mapping (`src/SkylineTogether/Data/mod-types.generated.txt`, rewritten by `tools/build.sh`). See
    [ARCHITECTURE.md](docs/ARCHITECTURE.md#probes-and-self-test) for exactly what is checked.
- **Third-party notices** (`THIRD-PARTY-NOTICES.txt`, also in the release zip) credit the LZ4 block format and the
  xxHash specification that Core's own implementations follow, and the game components Skyline Together uses.

### Changed

- **Players of earlier versions cannot join** this version, and solo test recordings made with earlier versions cannot
  be replayed (network protocol 8).
  - Developer detail: hardened handshake, id generation in entity and mover records, painted-cell masks in resource
    paint patches, statistics appends that resend the newest sample, a load heartbeat in the load progress report.
- **Messages from the host appear in each player's language**: chat lines such as "… joined the city", kick, ban and
  end reasons, and refusal details. So do the joining game's own reasons, the names of the mod's features, the
  self-test line and the network line.
- **Clearer join screens.** A password lockout has its own screen ("Too many wrong passwords") instead of "Wrong
  password", a host that cannot prove that it knows the password has its own screen ("Couldn't confirm the host")
  instead of "Joining failed", a join cooldown says how long to wait ("Please wait before joining again") instead of
  "The host isn't ready yet", and a join the host stopped says so ("The host stopped your join") instead of
  "Connection lost". Waits read "about 10 minutes" instead of "600 s"; the mod screens read as plain sentences.
- **Bug reports go to GitHub issues.** The texts in the game, the installer and the release zip say where to send the
  diagnostics file and logs; the guide explains how to collect them for a Paradox Mods install (which has no
  `Collect-Logs.cmd`) and what they contain.
- **Your Windows user name and IP addresses stay out of the logs.** Skyline Together's log, the diagnostics file and
  its notifications write paths in your user folder as `%USERPROFILE%` and IP addresses shortened (`203.0.113.x`);
  only the host's ban list keeps the full address of a player banned by address. `Collect-Logs.cmd` replaces the user
  folder in the game's logs too, and leaves crash dumps out: they can contain passwords and are collected only with
  `-IncludeDumps`, for sending privately.
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
  - On a direct (IP) join, and on a Steam join to a session known to have a password (its invite says so, or its
    host asked for the password before), a host that skips the password check is left at once ("Couldn't confirm the
    host"), and a joining game reads nothing from a host before it is authenticated.
- **No one can take over a player's place.** A connection with the Steam ID of a player in the session can no longer
  replace that player before it has proven the password.
  - Connections Steam flags as unauthenticated are identified by their address, and only the direct-IP socket accepts
    them.
- **Joins can no longer stall the host.** The host drops a join whose download or load makes no progress, caps
  re-sends of the city, and makes a player whose joins keep failing wait (30 seconds up to 30 minutes, longer once
  their failed joins held everyone's game for more than half an hour).
  - Handshakes are limited separately from players (8 at once, 2 per Steam ID or address, 10 seconds for the hello,
    15 seconds for the password answer, at most 20 connections a minute from one Steam ID or address), and the large
    receive buffer is given only to admitted players.
- **Bans also cover the address** of a player who joined by direct IP; IPv6 bans and password lockouts cover the /64.
- **Steam invites** are followed only while the lobby belongs to the host it names.
- **Bundled assets stay in their folder.** A host's bundled assets are added only as prefab and texture assets with
  plain names inside the join folder.
- **Logs stay small**: rate-limited and capped at 64 MiB per game start, and per-player warnings no longer grow with
  names a player chooses. Strangers connecting over and over can no longer push the host's own warnings out of the log.

### Fixed

Fixes for crashes and differences on joined players' games, most of them cases where an object's data could end up on
another object.

- **Big cities can be joined on slower PCs.** A joining player whose game took more than 5 minutes to load the city
  was dropped (the game's own progress stands still while it reads a saved game) and kept retrying. Loading now counts
  as progress while the joining game keeps working, up to the 20-minute limit; a load that runs out of time is not
  retried automatically, and an automatic rejoin waits as long as the host asks instead of using up its attempts.
- **No more crashes from objects without an owner.** An update whose owner a joined player's game lacks no longer leaves
  an object pointing at nothing; the object is removed and asked for again.
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
  to the host as the player's edits, not even later when the player clicks something (moving the camera or chatting
  no longer counts as editing, and a mod-panel change made by a long slider drag or a held key is no longer lost); mods whose panels write the city count as
  world-affecting, mods that only draw things do not; mod systems switched off on joined players stay off from the
  start of the join, and are not switched on at the session end; mods always see the session end.
- **Events, disasters and chirps** deleted after the events feature switched itself off are deleted for everyone.
- **Resource painting** merges only the painted cells (it overwrote nearby groundwater with older values) and keeps
  groundwater consistent.
- **Vehicles and passengers.** Riders of a deleted vehicle that its passenger list missed leave it, in the same frame;
  a vehicle still held under a reused id is replaced (its passengers released) instead of taking another vehicle's
  data; vehicle updates that fail are not confirmed to the host; a vehicle a player's game cannot create (a missing
  asset) is asked for less and less often; no duplicate passenger entries while catching up. Vehicles deleted on the
  host keep disappearing for joined players after their vehicle sync switched itself off (they stayed, frozen), and a
  truck's or train's trailer replaced on a joined player's game takes the rest of its vehicle with it (a crash).
- **Fewer needless repairs.** The integrity check no longer re-sends whole areas during a host's bulk edit or a
  catch-up, and the resync offer no longer counts objects given up in earlier sessions.
- **Smoother play on joined players.** The orphan check reads the deleted objects' own lists and scans everything at
  most once a second, and the index of who references whom no longer grows for the whole session.
- **Less network traffic in older cities**: statistics send only the newest samples instead of every series' whole
  history (megabytes), also for the income, trade and population statistics the game updates all the time.
- **Max players** raised while hosting takes effect at once (later joiners were refused at the old limit).
- **Joining by host name** no longer freezes the game while the name is looked up.
- **Overlapping joins**: joining is not possible while an earlier join's city is still loading, and an earlier join's
  asset import no longer starts loading a newer, incomplete download.
- **Installer**: works in user folders whose names contain `[` or `]`, keeps the installed copy when an update fails
  (also when putting it back fails: it then says where the copy is), with `-NoPause` stops instead of waiting while the
  game is running, and hides the short form of the user folder in collected logs.
- **Fewer unfair waits.** A join that failed because of the host (its city could not be saved or sent) or because
  the same player connected again no longer makes that player wait; a player who rejoins after a crash keeps their
  name instead of "Name (2)"; a player's last messages before leaving are no longer lost.
- **Joined players are not dropped while catching up** on a slow PC: values waiting for the city's updates no longer
  hold back the confirmations the host watches, and still never overtake the updates sent before them.
- Smaller fixes: a player name with `{...}` in it shows as typed; reads from the graphics card that never finish now
  time out instead of waiting forever; electricity and water flow updates are ignored while that sync is off; a tool
  edit of an object the host changed in the same frame is sent; the warning-icon filter and the viewer restrictions
  recover in the next session.

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
