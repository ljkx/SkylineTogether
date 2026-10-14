# Skyline Together

A from-scratch, host-authoritative **co-op multiplayer mod for Cities: Skylines II**: 2 to 8 players build one city
together over Steam (relay or direct connection), in cities of 100k+ population.

The host runs the complete vanilla simulation. Clients join by loading a snapshot of the host's save and then
follow the host: they never run the simulation themselves, they rebuild geometry and presentation locally from
replicated source data, and every value and status indicator (money, line connectivity, warning icons, ...) comes
from the host. A client's own actions are predicted with the unmodified game code, sent to the host as proposals,
validated and committed there, and replicated back to everyone. Incremental hashes prove convergence and divergence
is repaired automatically.

- Design: [`docs/design/ARCHITECTURE.md`](docs/design/ARCHITECTURE.md) (authoritative) and
  [`docs/design/IMPLEMENTATION-PLAN.md`](docs/design/IMPLEMENTATION-PLAN.md) (modules, contracts, milestones).
- Research on the game's internals with code citations: [`docs/research/`](docs/research/00-DIGEST.md).
- Decision records: [`docs/decisions/`](docs/decisions/).

Status: **early testing.** Hosting, joining (Steam invites or Steam ID), structure/line/economy/terrain sync, agents,
integrity repair, shared building previews and chat all work in builds; real two-PC sessions are under way and bugs
are expected. Back up your save before hosting.

**Install for testing:** build `dist/SkylineTogether-Setup.zip` (see *Building* below / `tools/installer/`), unzip,
double-click `Install.cmd`. Both players must show the same build id. `Collect-Logs.cmd` zips logs for bug reports.

## Solo testing (no second PC)

"Record test session" in the Skyline Together panel (in a city, or while hosting) adds a test recorder that joins the
session like a player and records everything it receives into
`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\ModsData\SkylineTogether\Recordings\`, with
verification checkpoints. "Replay & verify" (panel, also from the main menu) loads the recorded snapshot as a joiner and
plays the recording through the real client path without any network, then writes `verdict.txt` / `verdict.json`
next to the recording (objects, lines, icons and global state compared with the host, apply errors, timings). See
ARCHITECTURE §13.5.

## Repository layout

```
Directory.Build.props        shared properties: game paths, deploy folder, C# version, warnings as errors
SkylineTogether.sln
src/SkylineTogether.Core/    pure .NET core (no game references): contracts, buffers, hashing, JSON, protocol,
                             replication engine, ... (one folder per module)
src/SkylineTogether/         the game layer (the IMod), compiled against the game's own DLLs
ui/                          the hand-written UI module (SkylineTogether.mjs)
tests/SkylineTogether.Core.Tests/   unit tests of the core (a net8.0 console app with its own small test framework)
tools/build.sh               Release build, checks, tests, deploy and packaging
docs/                        design, research and decision records
```

## Building

Requirements:

- Windows with Cities: Skylines II installed (the build compiles against the DLLs in `Cities2_Data\Managed`).
- The .NET SDK 8 or newer. No NuGet package is needed or downloaded
  ([decision 0001](docs/decisions/0001-build-without-nuget.md)).
- Git Bash (or another bash) for `tools/build.sh`. Node.js is optional (UI module checks).

```sh
tools/build.sh                 # Release build + checks + tests
tools/build.sh --deploy        # ... then copy the mod into the local Mods folder
tools/build.sh --package       # ... then write dist/SkylineTogether-<version>.zip
tools/build.sh --game-dir "D:/Games/Cities Skylines II"   # game installed somewhere else
```

The default game folder is `C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II`. Override it with
`--game-dir`, or with the `GameDir` (or `GameManagedPath`) environment variable or MSBuild property. A plain
`dotnet build` of the game project also deploys to the Mods folder after every build; add `-p:DeployMod=false` to
skip that.

Running the unit tests directly:

```sh
dotnet run --project tests/SkylineTogether.Core.Tests -c Release            # all tests
dotnet run --project tests/SkylineTogether.Core.Tests -c Release -- Json   # only tests whose name contains "Json"
```

The exit code is the number of failed tests. Tests are methods marked `[Test]` in
`tests/SkylineTogether.Core.Tests/<Area>/*Tests.cs`, using the `Assert` helpers of `Framework/`.

## Installing

Copy `SkylineTogether.dll`, `SkylineTogether.Core.dll` (plus the `.pdb` files and `SkylineTogether.mjs`, when
present) into

```
%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\SkylineTogether
```

`tools/build.sh --deploy` does exactly that; the packaged zip contains a `SkylineTogether` folder that you extract
into the `Mods` folder. Local mods take precedence over Paradox Mods subscriptions of the same name. Every player
of a session needs the same build of the mod, the same game version and the same versions of every mod that changes
the shared world (mods that only change what one player sees, such as tooltips or icon packs, may differ). To
uninstall, delete the folder.

## Other mods

Other code mods and asset packs work in a session: saved mod data replicates with its entities, mod simulation runs
on the host, mod tools are captured like player edits, and a player who lacks an asset pack or a mod's generated
prefabs is told which one and what to subscribe to. Twelve popular mods have hand-verified adapters, every other mod is
analysed automatically when the game starts a session (explained in the diagnostics dump), and mod authors can declare
what their mod does and talk to their mod on other machines without referencing Skyline Together:
[`docs/MOD-INTEGRATION.md`](docs/MOD-INTEGRATION.md). Details: [`docs/design/MOD-COMPATIBILITY.md`](docs/design/MOD-COMPATIBILITY.md).

## Clean room

Skyline Together is designed and written only from its own research reports, the decompiled game code and the
sources of third-party mods it must be compatible with. No other co-op project was consulted.

## License

[MIT](LICENSE). Cities: Skylines II is a trademark of Paradox Interactive; this project is not affiliated with or endorsed by Paradox Interactive or Colossal Order. The game's assemblies are referenced at build time from your own installation and are never redistributed.
