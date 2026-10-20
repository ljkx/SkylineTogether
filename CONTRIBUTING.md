# Contributing to Skyline Together

Thanks for your interest. Everyone taking part in the project follows the [Code of Conduct](CODE_OF_CONDUCT.md).
The most useful contributions are:

- **Bug reports** with logs from all players ([reporting a bug](docs/guide/troubleshooting.md#reporting-a-bug),
  [two-player checklist](docs/guide/testing.md)). Use the issue templates.
- **Mod compatibility reports**: which mod, which version, what went wrong in a session.
- **Code**: fixes and improvements, ideally for an existing issue. For larger changes, open an issue first so we can
  agree on the approach before you invest time.

## Setup

You need Windows with Cities: Skylines II (Steam), the .NET 8 SDK (8.0.425 or later 8.0) and Git Bash. Node.js and Python 3 are
optional (UI checks, installer, repository checks). No NuGet package is used.

```sh
git clone https://github.com/ljkx/SkylineTogether.git
cd SkylineTogether
tools/build.sh             # build, UI checks, tests
tools/build.sh --deploy    # ... and copy the mod into your Mods folder
```

If the game is not in the default Steam folder, add `--game-dir "<path>"`. Without the game you can still build and
test Core. Details: [docs/dev/building.md](docs/dev/building.md). The [architecture](docs/ARCHITECTURE.md) explains how
the mod works and the [code map](docs/dev/code-map.md) where things are.

## Pull requests

- Branch from `main`, keep a pull request to one topic, and describe how you tested it (the template asks).
- `tools/build.sh` must pass: the build treats warnings as errors.
- CI builds Core and runs the tests, the UI smoke test and the repository checks. It cannot build the game layer, so
  for changes in `src/SkylineTogether` run `tools/build.sh` locally and test in game.
- Commit messages: a short summary line in the imperative or descriptive style of the existing history (for example
  "Fix client crash on bulldoze"), then details if needed.

## Code style

Match the code around you. `.editorconfig` holds the mechanical rules:

- C# 10, 4 spaces, Allman braces, block-scoped namespaces, lines up to about 120 characters.
- Private fields `_camelCase`; constants, static readonly fields, types and members `PascalCase`. `var` is fine.
- Nullable reference types and implicit usings are off.
- Public types get a `/// <summary>` that says what the type is for and any rules a caller must know (thread affinity,
  ownership, units). Comments explain *why*; cite the architecture section (`ARCHITECTURE.md#client-apply`) where one
  applies.
- Per-frame code avoids allocations: reuse collections, cache delegates, no LINQ on hot paths.

Rules that keep the architecture intact:

- **Core stays pure .NET.** No Unity, Game or Colossal reference in `src/SkylineTogether.Core`. Use only APIs that exist
  in netstandard2.0 (the game's runtime is Mono/.NET Framework 4.7.2); [decision 0001](docs/decisions/0001-build-without-nuget.md)
  lists the usual traps (`Math.Clamp`, `HashCode`, `Dictionary.TryAdd`, span overloads, ...). No build or CI step
  checks this: the game build compiles Core against the game's own runtime, whose `netstandard.dll` is a 2.1 facade,
  and CI against netstandard2.1, so reviewers check it by hand.
- **Untrusted input.** Everything decoded from the network goes through the limits in `Protocol/Limits.cs`, and decoders
  throw only `DecodeException` on bad input. See [SECURITY.md](SECURITY.md).
- **Game internals.** Every game type or member you use must exist in the game version you build against
  (`tools/build.sh` runs `tools/ApiCheck`). Non-public members are resolved only in `Diagnostics/Probes.cs` and read by
  the features as `Probes.X` (null: missing, the feature degrades); game types found by name come from a list that
  `Probes` checks (`ProbeTypeNames`) or go through `FamilyClassifier.FindType`, which reports a missing name. Only
  `Compat/` (other mods' assemblies), `Net/SteamLanes.cs` and `Session/JoinAssetTransfer.cs` reflect elsewhere;
  `GameLayerReflectionRuleTests` fails for any other `GetField`/`GetProperty`/`GetMethod`/`GetType` by name. So a game
  update switches a feature off instead of crashing.
- **Feature code runs guarded** (`Guard`), so one failing part does not take the session down.
- **Wire format.** A change of any envelope, message or sync batch layout increments `ProtocolVersion.Current` and
  the pinned value in `Protocol/LimitsAndTextTests.cs`, and updates the golden tests
  (`tests/.../Protocol/*GoldenTests.cs`) and the pinned corpus of `Protocol/WireCorpusTests.cs` (which fails for any
  layout change and for a new message, control or record kind without a pinned layout).
- **Never change `AssemblyVersion`** (1.0.0.0): the network id system is saved in cities by assembly-qualified name.

## Tests

- New or changed Core behaviour needs unit tests in `tests/SkylineTogether.Core.Tests/<Area>/`. See
  [docs/dev/testing.md](docs/dev/testing.md) for the framework and filters.
- Bug fixes come with a test that fails without the fix, where the bug is reachable from Core.
- CI fails when fewer tests run than the minimum in `.github/workflows/ci.yml` (`--expect-at-least`); raise it when you
  add tests. It also runs the suite under `tr-TR` and `de-DE` and checks that a failing test fails the run.
- Game layer changes are tested in game: at least the solo test (record and replay), and a two-player session for
  anything that involves another player. Say in the pull request what you tested.

## Texts

Every user-visible text is a key. Add a new key to `src/SkylineTogether/Data/locale.en-US.json`,
`src/SkylineTogether/Data/locale.de-DE.json` and `src/SkylineTogether/Data/locale-keys.generated.json`, and for UI
texts also to the English fallback table in `ui/SkylineTogether.mjs`. `tools/ui_smoke.mjs` and the
`ShippedDataFilesTests` check that they match. If you cannot write German, use the English text in the German table
and say so in the pull request.

## Documentation

- User-visible changes: update the [user guide](docs/guide/) and add a line to [CHANGELOG.md](CHANGELOG.md) under
  *Unreleased*.
- Sync behaviour: update [SYNC-COVERAGE.md](docs/design/SYNC-COVERAGE.md), [ACTION-COVERAGE.md](docs/design/ACTION-COVERAGE.md)
  or [MOD-COMPATIBILITY.md](docs/design/MOD-COMPATIBILITY.md) as appropriate.
- Design changes: update [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (it describes the code as built) and, for
  decisions, add a record in [docs/decisions/](docs/decisions/).
- Run `python tools/check_links.py` after moving or renaming documents.

## Clean-room policy

Skyline Together is written from its own research and design documents. To keep the project free of copyright and
licence problems, contributions must be your own work:

- Do not copy code from other multiplayer mods for Cities: Skylines II or any other game, or from any proprietary or
  incompatibly licensed source. Do not port such code by translating or paraphrasing it line by line either.
- You may study the game's decompiled code to understand how the game works and refer to it (type and member names)
  in research notes and comments. Never quote decompiled code, not even short excerpts; describe the behaviour in your
  own words.
- Code from openly licensed projects (MIT, BSD, Apache 2.0, ...) may be used when its licence allows it and is credited;
  mention it in the pull request.
- The published source code of third-party mods that Skyline Together must be compatible with may be studied for
  compatibility (their data layout and behaviour), under the same no-copy rule. Do not decompile mods that publish no
  source; use their documentation and observe their behaviour in game instead.

By submitting a pull request you confirm that you have the right to contribute the code under the project's
[MIT licence](LICENSE).

## Personal data

The repository is public. Do not commit logs, diagnostics files, recordings, saves, Steam IDs or local absolute paths
(`C:\Users\<name>\...`). Use `%USERPROFILE%` or placeholders in documentation. CI runs `tools/check_personal_data.py`
on the tracked files and on the commit messages of a pull request. Your git name and e-mail address are not checked
in a pull request (they are yours to publish); a noreply address (`<id>+<name>@users.noreply.github.com`) keeps your
e-mail private. Commits pushed to this repository itself must use one, and CI checks the whole history for it.

## Security

Please report vulnerabilities privately; see [SECURITY.md](SECURITY.md).
