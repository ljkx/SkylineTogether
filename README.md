# Skyline Together

[![CI](https://github.com/ljkx/SkylineTogether/actions/workflows/ci.yml/badge.svg)](https://github.com/ljkx/SkylineTogether/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Co-op multiplayer for **Cities: Skylines II**: build one city together with friends over Steam.

One player hosts their city. Friends join, get a copy of it, and from then on everything anyone builds is shared live.
The host's game runs the simulation. The other players' games show the host's city and send their actions to the host,
which checks and applies them for everyone. Differences are detected and repaired automatically.

> **Status: early testing.** Hosting, joining and the sync of the city work in test builds, and real two-PC sessions are
> under way. Expect bugs, back up your saves and play on a copy of a city. Test builds are on the
> [Releases page](https://github.com/ljkx/SkylineTogether/releases).

## Features

- **Host and join** by Steam ID, Steam invite or (optionally) direct IP. Optional session password, 2 to 16 players
  (default 8).
- **Shared city**: roads, buildings, zoning, districts, transport lines, terrain, water, budget, taxes, policies,
  milestones, traffic, citizens and the numbers in the info panels.
- **Every player builds** with the normal game tools; your own actions show instantly and the host confirms them.
- **Roles**: builders can do everything; viewers can only watch and chat.
- **Presence**: other players' cursors with name tags, their building previews in their colour, and chat.
- **Host-controlled game speed** that builders can change for everyone.
- **Automatic integrity checks** that find and repair objects that differ from the host.
- **Reconnect** after a lost connection, with the city paused meanwhile.
- **Mod compatibility checks**: the host can require the same code mods and the joining player gets a list of what to
  change; dedicated support for 12 popular mods.
- **Solo test**: record a session and replay it through the joining player's code path on one PC.
- English and German UI.

## Requirements

- Cities: Skylines II, **Steam version**, with Steam running and logged in, on Windows.
- Every player needs the **same build** of Skyline Together, the **same game version** and, by default, the **same code
  mods**.

## Install

1. Close the game.
2. Download `SkylineTogether-Setup.zip` from the latest release on the
   [Releases page](https://github.com/ljkx/SkylineTogether/releases) (not the green "Code" button: that is the source
   code), or [build it yourself](docs/dev/building.md#packaging-and-the-player-installer).
3. Unzip it and double-click **`Install.cmd`**. It installs the mod into
   `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\SkylineTogether` and prints a **build id**:
   every player must see the same one.
4. Disable any other multiplayer mod.

Details, manual installation and uninstalling: [Getting started](docs/guide/getting-started.md#install).

## Quick start

**Host**

1. Load a city.
2. Click the round Skyline Together button in the top-left corner and press **Host this city**.
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
problems, things that look different for another player, and how to collect logs for a bug report, see
[Troubleshooting](docs/guide/troubleshooting.md). In short: click **Write diagnostics** in the panel, run
`Collect-Logs.cmd` from the setup folder and attach the zip from every player to the
[issue](https://github.com/ljkx/SkylineTogether/issues/new/choose).

## Compatibility

- **Game**: built against the current Steam version of Cities: Skylines II. A game update can switch features off
  until the mod is updated; the startup self-test shows which.
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

Bug reports from test sessions are the most useful contribution right now. For code, see
[CONTRIBUTING.md](CONTRIBUTING.md); for security problems, [SECURITY.md](SECURITY.md). Changes are listed in the
[CHANGELOG](CHANGELOG.md).

Skyline Together is designed and written only from its own research reports, the decompiled game code and the sources
of third-party mods it must be compatible with. No other co-op project was consulted, and contributions must follow
the same [clean-room policy](CONTRIBUTING.md#clean-room-policy).

## License

[MIT](LICENSE). Cities: Skylines II is a trademark of Paradox Interactive; this project is not affiliated with or
endorsed by Paradox Interactive or Colossal Order. The game's assemblies are referenced at build time from your own
installation and are never redistributed.
