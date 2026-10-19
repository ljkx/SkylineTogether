# Skyline Together

[![CI](https://github.com/ljkx/SkylineTogether/actions/workflows/ci.yml/badge.svg)](https://github.com/ljkx/SkylineTogether/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Co-op multiplayer for **Cities: Skylines II**: build one city together with friends over Steam.

One player hosts their city. Friends join, get a copy of it, and from then on everything anyone builds is shared live.
The host's game runs the simulation. The other players' games show the host's city and send their actions to the host,
which checks and applies them for everyone. Differences are detected and repaired automatically.

**Get it on [Paradox Mods](https://mods.paradoxplaza.com/mods/163578/Windows)** or from the
[Releases page](https://github.com/ljkx/SkylineTogether/releases). Back up your saves before you host or join.

## Features

- **Host and join** by Steam ID, Steam invite or (optionally) direct IP. Optional session password, 2 to 16 players
  (default 8).
- **Shared city**: roads, buildings, zoning, districts, transport lines, terrain, water, budget, taxes, policies,
  milestones, traffic, citizens and the numbers in the info panels.
- **Every player builds** with the normal game tools; your own actions show instantly and the host confirms them.
- **Roles**: builders can do everything; viewers can only watch and chat; co-hosts help manage players. The host can
  also have other players' builds checked against what a normal game allows (off by default).
- **Presence**: other players' cursors with name tags, their building previews in their colour, and chat.
- **Host-controlled game speed** that builders can change for everyone.
- **Automatic integrity checks** that find and repair objects that differ from the host.
- **Reconnect** after a lost connection, with the city paused meanwhile.
- **Mod compatibility checks**: the host can require the same code mods and the joining player gets a list of what to
  change; dedicated support for 12 popular mods.
- **Solo test**: record a session and verify it on one PC, without a second player.
- English and German UI.

## Requirements

- Cities: Skylines II, **Steam version**, with Steam running and logged in, on Windows.
- Every player needs the **same build** of Skyline Together, the **same game version** and, by default, the **same code
  mods**.

## Known limitations

- Every player needs the same Skyline Together version, the same game version and the same gameplay mods.
- A game update can switch features off until the mod is updated; the self-test in the panel shows which.
- Objects from assets a player lacks are left out on that player's game.
- Repairs of differences happen in the background and can take a moment.
- The host's upload speed limits large sessions.

More in [Troubleshooting](docs/guide/troubleshooting.md) and [SECURITY.md](SECURITY.md#known-limitations).

## Install

Use **one** of the two ways, not both: with a local copy and a subscription at the same time the game loads only one
of them, and Skyline Together warns at start.

**From Paradox Mods** (recommended)

1. Subscribe to [Skyline Together](https://mods.paradoxplaza.com/mods/163578/Windows) on the website, or in the game's
   main menu open **Paradox Mods** and search for **Skyline Together**. Make sure it is enabled in your active playset.
2. Restart the game. Subscriptions update automatically, so everyone who subscribed has the same version after a
   restart; the panel's footer shows the build id to compare.

**With the setup from GitHub** (the same release as on Paradox Mods; new versions may appear here first)

1. Close the game.
2. Download `SkylineTogether-Setup.zip` from the latest release on the
   [Releases page](https://github.com/ljkx/SkylineTogether/releases) (not the green "Code" button: that is the source
   code), or [build it yourself](docs/dev/building.md#packaging-and-the-player-installer).
3. Unzip it and double-click **`Install.cmd`**. It installs the mod into
   `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\SkylineTogether` and prints a **build id**:
   every player must see the same one.

A release published on both has the same build id, so players of both ways can play together. Disable any other
multiplayer mod. Details, manual installation and uninstalling: [Getting started](docs/guide/getting-started.md#install).

## Quick start

**Host**

1. Load a city.
2. Click the Skyline Together button in the top-left corner and press **Host this city**.
3. Send your friends your Steam ID (copy icon at the top of the panel), or use **Invite friends**.

**Join**

1. On the title screen click **Multiplayer**, then **Join a friend**.
2. Paste the host's Steam ID (or `IP:port`) and the password if there is one, and press **Join**.
   Or accept the host's Steam invite.

Your game downloads and loads the host's city; the host's game pauses until you are in. Press **Enter** to chat.

More: [Getting started](docs/guide/getting-started.md) (roles, building previews, speed, saving, settings).

## FAQ

- **Port forwarding?** Not needed for Steam ID and invite joins. Only optional direct IP connections need a forwarded
  UDP port (default 27015).
- **Whose city is it?** The host's. Other players can save a copy, but the session continues in the host's save.
- **Who controls the speed?** The host's game. Builders' speed changes apply for everyone.
- **Which mods work?** With the default settings, every player needs the same code mods. Mods that only change a
  player's own screen may differ.

More in the [FAQ](docs/guide/faq.md).

## Troubleshooting

A refused join explains why (different builds, game versions or mods, full session, password). For those, connection
problems and things that look different for another player, see [Troubleshooting](docs/guide/troubleshooting.md).

To report a bug, every player clicks **Write diagnostics** in the panel, then:

- **GitHub setup**: run `Collect-Logs.cmd` from the setup folder and attach the zip from your Desktop.
- **Paradox Mods**: attach `Logs\SkylineTogether.log` and the newest `Logs\SkylineTogether-diagnostics-*.txt` from
  `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\`.

Open a [bug report](https://github.com/ljkx/SkylineTogether/issues/new/choose) with the files of every player. They
contain player names and Steam IDs; see [Reporting a bug](docs/guide/troubleshooting.md#reporting-a-bug) for what else
is in them.

## Compatibility

- **Game**: built and tested against game version 1.6.2f1 (Steam). A game update can switch features off until the mod
  is updated; the startup self-test shows which.
- **Code mods**: every player needs the same world-affecting mods in the same versions (the host can turn this check
  off). Local-only mods (translations, tooltips, icon libraries, overlays) may differ. Traffic, Move It, Anarchy, Better
  Bulldozer, Tree Controller, Water Features, Plop the Growables, 529 Tiles, Historical Start, Zoning Toolkit, Line Tool
  and Find It have dedicated support. Every other mod is analysed automatically when a session starts: its saved data
  replicates with its entities, its simulation runs on the host, its tools are captured like player edits, and the
  decisions are explained in the diagnostics dump; see [MOD-COMPATIBILITY.md](docs/design/MOD-COMPATIBILITY.md).
- **Assets**: use the same asset subscriptions on every PC; objects whose asset a player lacks are not created on that
  player's game, and the joining player is told which asset pack or mod is missing and what to subscribe to.
- **Other multiplayer mods**: not compatible; disable them.
- **Mod authors**: declare what your mod does, react to sessions and send data to your mod on other PCs, without a
  reference to Skyline Together: see [MOD-INTEGRATION.md](docs/MOD-INTEGRATION.md).

## Building from source

Requires Windows, the game, the .NET SDK 8 or newer and Git Bash. No NuGet package is used.

```sh
tools/build.sh                 # Release build, UI checks, tests
tools/build.sh --deploy        # ... then copy the mod into the local Mods folder
tools/build.sh --package       # ... then write dist/SkylineTogether-<version>.zip
python tools/installer/make_installer.py   # then build dist/SkylineTogether-Setup.zip
python tools/paradox/publish_paradox.py check   # check a Paradox Mods upload (docs/dev/publishing-paradox-mods.md)
```

Add `--game-dir "<path>"` if the game is not in the default Steam folder. Core and its tests also build without the
game. See [docs/dev/building.md](docs/dev/building.md); the [architecture](docs/ARCHITECTURE.md) explains how the mod works
and the [code map](docs/dev/code-map.md) is the place to start reading the code.

| Folder | Contents |
|---|---|
| `src/SkylineTogether.Core/` | Pure .NET core (no game references): protocol, codecs, hashing, sessions, replication logic |
| `src/SkylineTogether/` | The game layer (the `IMod`), compiled against the game's own DLLs |
| `ui/` | The hand-written UI module |
| `tests/SkylineTogether.Core.Tests/` | Unit tests of the core |
| `tools/` | Build script, installer, UI smoke test, repository checks |
| `docs/` | [User guide, developer docs, design and research](docs/README.md) |

## Contributing

Bug reports with logs, and compatibility reports for other mods, are very welcome. For code, see
[CONTRIBUTING.md](CONTRIBUTING.md); for security problems, [SECURITY.md](SECURITY.md). Everyone taking part follows the
[Code of Conduct](CODE_OF_CONDUCT.md). Changes are listed in the [CHANGELOG](CHANGELOG.md).

Skyline Together is an independent implementation, written from the project's own research into the game's behaviour
and from the public interfaces of the mods it supports. No code from other multiplayer projects was used or consulted,
and no game code is included or redistributed. Contributions follow the same
[clean-room policy](CONTRIBUTING.md#clean-room-policy).

## License

[MIT](LICENSE). Cities: Skylines II is a trademark of Paradox Interactive; this project is not affiliated with or
endorsed by Paradox Interactive or Colossal Order. The game's assemblies are referenced at build time from your own
installation and are never redistributed. Third-party notices: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
