# Building

## Requirements

- **Windows with Cities: Skylines II installed** for the full build. The game layer compiles against the assemblies in
  the game's `Cities2_Data\Managed` folder; nothing from the game is copied into the repository or the build output.
  Without the game you can still build and test Core (see [Without the game](#without-the-game)).
- **.NET SDK 8 or newer.** No NuGet package is referenced or downloaded
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

1. Finds a `dotnet` that has an SDK 8 or newer: `$DOTNET`, the `dotnet` on the PATH, `$DOTNET_ROOT`,
   `%LOCALAPPDATA%\Microsoft\dotnet`, `~/.dotnet`, `C:\Program Files\dotnet`.
2. `dotnet build SkylineTogether.sln -c Release -p:DeployMod=false`, then checks that `SkylineTogether.dll` and
   `SkylineTogether.Core.dll` exist in `src/SkylineTogether/bin/Release/net472`.
3. `tools/ApiCheck`, if it exists (it does not yet).
4. With Node on the PATH: `node --check ui/SkylineTogether.mjs` and `node tools/ui_smoke.mjs`.
5. Runs the Core tests (`dotnet tests/SkylineTogether.Core.Tests/bin/Release/net8.0/SkylineTogether.Core.Tests.dll`).
6. `--deploy`: copies `SkylineTogether.dll`, `SkylineTogether.Core.dll`, their `.pdb` files and
   `ui/SkylineTogether.mjs` into the Mods folder.
7. `--package`: writes `dist/SkylineTogether-<version>.zip` with a `SkylineTogether` folder (the files above, `LICENSE`
   and a `README.txt`). The version comes from `<Version>` in `Directory.Build.props`.

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

`make_installer.py` takes the newest `dist/SkylineTogether-<version>.zip` and writes `dist/SkylineTogether-Setup.zip`
(and the same files loose in `dist/SkylineTogether-Setup/`):

- `Install-SkylineTogether.ps1`: the installer, built from `tools/installer/Install-SkylineTogether.template.ps1` with
  the release zip embedded as base64 and its SHA-256 for verification. It must stay ASCII (Windows PowerShell 5.1 reads
  it as ANSI); the script checks that.
- `Install.cmd` and `Collect-Logs.cmd`: double-click wrappers (`Collect-Logs.cmd` runs the installer with
  `-CollectLogs`).
- `HOW-TO-TEST.txt`: the two-player checklist from `tools/installer/HOW-TO-TEST.txt`.

The **build id** printed by the installer and written into `HOW-TO-TEST.txt` is the first 12 hex digits of the SHA-256
of `SkylineTogether.dll`. The game computes the same value from the DLL it loaded and compares it when a player joins;
it changes with every compile. See [releasing.md](releasing.md).

For Paradox Mods, `python tools/paradox/publish_paradox.py` uploads the files of the same release zip with the game's
ModPublisher; see [publishing-paradox-mods.md](publishing-paradox-mods.md). The default build does not change for it.

## Line endings and editor settings

The repository stores text files with LF; checkouts use the platform's line endings (`.gitattributes`). Shell scripts
are always LF, `.ps1`/`.cmd` files always CRLF. `.editorconfig` has the indentation and C# style settings.
