# Building

## Requirements

- **Windows with Cities: Skylines II installed** for the full build. The game layer compiles against the assemblies in
  the game's `Cities2_Data\Managed` folder; nothing from the game is copied into the repository or the build output.
  Without the game you can still build and test Core (see [Without the game](#without-the-game)).
- **.NET SDK 8.0** (8.0.425 or a later 8.0 SDK; `global.json` pins it, see
  [Reproducible builds](#reproducible-builds)). No NuGet package is referenced or downloaded
  ([decision 0001](../decisions/0001-build-without-nuget.md)).
- **Git Bash** (or another bash) for `tools/build.sh`.
- Optional: **Node.js** for the UI checks, **Python 3** for the installer and the repository checks.

## tools/build.sh

```sh
tools/build.sh                 # Release build, UI checks, Core tests
tools/build.sh --deploy        # ... then copy the mod into the local Mods folder
tools/build.sh --package       # ... then write dist/SkylineTogether-<version>.zip
tools/build.sh --game-dir "D:/Games/Cities Skylines II"   # game installed somewhere else
tools/build.sh --deploy-dir "<folder>"                     # deploy somewhere else
tools/build.sh --skip-ui-checks
```

What it does, in order (any failure stops it before anything is deployed or packaged):

1. Finds a `dotnet` that has an SDK `global.json` accepts: `$DOTNET`, the `dotnet` on the PATH, `$DOTNET_ROOT`,
   `%LOCALAPPDATA%\Microsoft\dotnet`, `~/.dotnet`, `C:\Program Files\dotnet`, and prints the SDK version.
2. `dotnet build SkylineTogether.sln -c Release -p:DeployMod=false`, then checks that `SkylineTogether.dll` and
   `SkylineTogether.Core.dll` exist in `src/SkylineTogether/bin/Release/net472`.
3. With Node on the PATH: `node --check ui/SkylineTogether.mjs` and `node tools/ui_smoke.mjs`.
4. Runs the Core tests (`dotnet tests/SkylineTogether.Core.Tests/bin/Release/net8.0/SkylineTogether.Core.Tests.dll`).
5. `--deploy`: copies `SkylineTogether.dll`, `SkylineTogether.Core.dll`, their `.pdb` files and
   `ui/SkylineTogether.mjs` into the Mods folder.
6. `--package`: writes `dist/SkylineTogether-<version>.zip` with a `SkylineTogether` folder (the files above, `LICENSE`,
   `THIRD-PARTY-NOTICES.txt` and a `README.txt`); it fails when any of these files is missing, and warns when the tree
   has uncommitted changes (the release tools refuse such a package). The version comes from `<Version>` in
   `Directory.Build.props`. With Python the zip is reproducible ([Reproducible builds](#reproducible-builds)).

## Game path and Directory.Build.props

`Directory.Build.props` defines four paths for every project. Each can be overridden by an MSBuild property
(`-p:GameDir=...`) or an environment variable of the same name:

| Property | Default |
|---|---|
| `GameDir` | `C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II` |
| `GameManagedPath` | `$(GameDir)\Cities2_Data\Managed` |
| `CS2UserDataPath` | `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II` |
| `ModDeployDir` | `$(CS2UserDataPath)\Mods\SkylineTogether` |

There is no automatic search for the game: if it is not in the default Steam folder, pass `--game-dir` to the build
script or set `GameDir` (or `GameManagedPath`, for a copy of only the Managed folder).

It also sets C# 10, deterministic builds, warnings as errors (`CS1591`, missing XML comments, is the only global
exception), nullable and implicit usings off, and the version. The .NET analyzers that ship inside the SDK run on
every build (analysis level 8.0, no package): the default rules plus the ones `.editorconfig` raises (culture-invariant
formatting and comparison, disposal, ignored results, unused private state), so a finding fails the build like any
other warning. `.editorconfig` also says which rules stay off on purpose and why. Both projects generate XML
documentation only so that the compiler checks doc comments (`cref`s, parameter names); the files are not shipped.

Both projects compile against the game's own runtime (`NoStdLib`, `FrameworkPathOverride=$(GameManagedPath)`), not
against a reference pack. The game project references every DLL in the Managed folder (`Private=false`, never copied)
except test assemblies. Core references only `mscorlib`, `System`, `System.Core`, `netstandard`, `System.Memory`,
`System.Runtime.CompilerServices.Unsafe` and `System.IO.Compression`. Core code must only use APIs that exist in
netstandard2.0, so it also compiles for the net8.0 tests (decision 0001 lists the usual traps).

A plain `dotnet build` of the game project also deploys to `ModDeployDir` after every build (the `DeployMod` target);
add `-p:DeployMod=false` to skip that.

## Without the game

When `$(GameManagedPath)\mscorlib.dll` does not exist, `SkylineTogether.Core.csproj` targets **netstandard2.1** and
compiles against the reference assemblies that ship inside the .NET SDK (`packs/NETStandard.Library.Ref`), so restore
still downloads nothing. The build prints:

```
SkylineTogether.Core: no game assemblies in '...'; compiling against netstandard2.1 (compile check only, not a shippable build)
```

That output is only a compile check and must never be shipped. The test project does not depend on it: it compiles
the Core sources itself for net8.0. The game project and the solution build still need the game.

```sh
dotnet build src/SkylineTogether.Core/SkylineTogether.Core.csproj -c Release
dotnet build tests/SkylineTogether.Core.Tests/SkylineTogether.Core.Tests.csproj -c Release
dotnet tests/SkylineTogether.Core.Tests/bin/Release/net8.0/SkylineTogether.Core.Tests.dll
```

To try this on a PC that has the game, point the build at a folder without it, for example
`GameDir='C:\nonexistent' dotnet build ...` in Git Bash. Use a separate clone, because switching the target framework
changes the restore output in `obj/`.

Without the game, the tests that cross-check the hashes against the game's `System.IO.Hashing.dll` are not compiled,
so four tests fewer run. This is what CI does ([.github/workflows/ci.yml](../../.github/workflows/ci.yml)).

## Packaging and the player installer

```sh
tools/build.sh --package               # dist/SkylineTogether-<version>.zip
python tools/installer/make_installer.py
```

`make_installer.py` takes `dist/SkylineTogether-<version>.zip` of the `<Version>` in `Directory.Build.props` (or
`--zip <path>`), checks that its DLL was built from the clean `HEAD` (`--allow-dirty` for a local test) and writes
`dist/SkylineTogether-Setup.zip`
(and the same files loose in `dist/SkylineTogether-Setup/`):

- `Install-SkylineTogether.ps1`: the installer, built from `tools/installer/Install-SkylineTogether.template.ps1` with
  the release zip embedded as base64 and its SHA-256 for verification. It must stay ASCII (Windows PowerShell 5.1 reads
  it as ANSI); the script checks that.
- `Install.cmd` and `Collect-Logs.cmd`: double-click wrappers (`Collect-Logs.cmd` runs the installer with
  `-CollectLogs`).
- `TWO-PLAYER-CHECKLIST.txt`: the two-player checklist from `tools/installer/TWO-PLAYER-CHECKLIST.txt`.

The installer also takes `-Uninstall` (with `-RemoveSettings`) and, with `-CollectLogs`, `-IncludeDumps` (crash dumps
are left out by default).

The **build id** printed by the installer and written into `TWO-PLAYER-CHECKLIST.txt` is the first 12 hex digits of the
SHA-256 of `SkylineTogether.dll`. The game computes the same value from the DLL it loaded and compares it when a player
joins. It changes whenever the DLL's bytes change; see [Reproducible builds](#reproducible-builds) and
[releasing.md](releasing.md).

For Paradox Mods, `python tools/paradox/publish_paradox.py` uploads the files of the same release zip with the game's
ModPublisher; see [publishing-paradox-mods.md](publishing-paradox-mods.md). The default build does not change for it.

## Reproducible builds

The build is deterministic: building the same commit again gives a byte-identical `SkylineTogether.dll`, so the same
build id, on any machine and in any checkout folder, as long as these inputs are the same:

- **the source files, including their line endings.** The deterministic PDB records a checksum of every source file
  and embedded resource, and the DLL embeds the PDB's id. `.gitattributes` therefore checks every text file out with
  LF on every platform, whatever `core.autocrlf` says (only `.ps1`, `.cmd`, `.bat` and `.sln` files are CRLF, and
  none of them is compiled). Before this rule, a CRLF and an LF checkout of the same commit gave different build ids.
  If an old clone still has CRLF files, refresh it once: `git rm -r --cached -q . && git reset --hard`.
- **the .NET SDK, which brings the compiler.** `global.json` pins SDK 8.0.425 and allows later 8.0 feature bands and
  patches (`rollForward: latestFeature`). A different SDK can emit different IL, so a release states the SDK version
  `tools/build.sh` printed.
- **the game assemblies** in `GameManagedPath` that the projects compile against (a game update can change them).
- **the commit itself**: the DLL's informational version carries the commit hash (Source Link).

The checkout path does not matter (`DeterministicSourcePaths` maps it to `/_/`).

The release zip (`tools/build.sh --package`) and the setup zip (`make_installer.py`) are written by
`tools/release_common.py`: entries in sorted order, every timestamp set to the commit time, fixed attributes. Packaging
the same build twice gives byte-identical zips (with the same Python, whose zlib compresses), so the published SHA-256
values can be re-derived from the source. Without Python, `build.sh` falls back to `zip`, Windows `tar` or PowerShell
and warns that the zip is not reproducible.

## Editor settings

`.editorconfig` has the indentation and C# style settings; its line endings match `.gitattributes`.
