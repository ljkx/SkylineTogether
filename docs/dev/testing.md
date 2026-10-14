# Tests

## Core unit tests

`tests/SkylineTogether.Core.Tests` is a net8.0 console application without any package. It compiles the Core sources
directly (so internal members are testable) plus a small test framework in `Framework/`
([decision 0001](../decisions/0001-build-without-nuget.md) explains why there is no xUnit).

```sh
dotnet run --project tests/SkylineTogether.Core.Tests -c Release                 # all tests
dotnet run --project tests/SkylineTogether.Core.Tests -c Release -- Json         # names containing "Json"
dotnet run --project tests/SkylineTogether.Core.Tests -c Release -- Session -Soak # "Session" but not "Soak"
dotnet run --project tests/SkylineTogether.Core.Tests -c Release -- --list       # list the selected tests
dotnet run --project tests/SkylineTogether.Core.Tests -c Release -- --verbose    # print every result
```

- Plain words are case-insensitive substring filters on the test name (a test runs when it matches any of them); a word
  starting with `-` excludes matching tests.
- Test names are `Area.Class.Method`, for example `Buffers.ByteWriterTests.RoundTripsVarints`.
- The exit code is the number of failed tests, capped at 125. `tools/build.sh` runs the built DLL and fails on a
  non-zero exit code.

The tests build and run without the game ([building.md](building.md#without-the-game)); only the cross-checks against
the game's `System.IO.Hashing.dll` need it (`HAVE_SYSTEM_IO_HASHING`).

### Writing tests

- Put tests in `tests/SkylineTogether.Core.Tests/<Area>/<Subject>Tests.cs`, where `<Area>` matches the Core folder.
- A test is a method marked `[Test]`: no parameters, returns `void`, public or private, static or instance. Instance
  tests get a fresh object per test (disposed afterwards if it is `IDisposable`).
- `[Test(TimeoutSeconds = 120)]` raises the default limit of 60 seconds; `[Test(Skip = "reason")]` skips a test.
- Use the `Assert` helpers in `Framework/Assert.cs` (`Equal`, `True`, `SequenceEqual`, `Throws<T>`, `Near`, ...).
  `Assert.Skip("reason")` skips at run time (for example when an optional repository file is missing).
- Data-driven tests loop over their cases and name the failing case in the assertion message.
- Use the fakes in Core's `Testing/` folder (`ManualClock`, `DeterministicRandom`, `ListLog`) instead of real time and
  randomness, and `LoopbackTransport` / `NetSimTransport` for network scenarios.
- `TestPaths.TryGetRepoFile` finds files in the repository (for tests of the shipped data files).

## UI checks

```sh
node --check ui/SkylineTogether.mjs
node tools/ui_smoke.mjs
```

`tools/ui_smoke.mjs` needs only Node (no game, no packages). It renders the UI module with stub versions of React and
the game's UI modules for many session states, with and without the game's components, and fails on:

- an exception while importing, registering, rendering or running an event handler,
- a binding or trigger name that `src/SkylineTogether/UI/*UISystem.cs` does not declare,
- a text key without an English text, English texts that differ from `src/SkylineTogether/Data/locale.en-US.json`,
  keys missing in another locale or in `locale-keys.generated.json`,
- a main menu entry, top-left button or root that was not registered.

## Repository checks

```sh
python tools/check_links.py          # relative links and #anchors in all tracked Markdown files
python tools/check_personal_data.py  # home-folder paths, real-looking Steam IDs, e-mail addresses
```

## Sync coverage

`tools/sync_coverage.py` regenerates the inventory in [SYNC-COVERAGE.md](../design/SYNC-COVERAGE.md) from the
decompiled game sources and checks that every serializable component and system has a rule in
`tools/sync_coverage_rules.txt`. It needs a local decompilation of the game (`--decomp <folder>`), so it is not part of
`tools/build.sh` or CI. Run `python tools/sync_coverage.py --check --decomp <folder>` after a game update.

## Continuous integration

[.github/workflows/ci.yml](../../.github/workflows/ci.yml) runs on every push to `main` and on pull requests:

| Job | Runner | Steps |
|---|---|---|
| Core build and tests | windows-latest, .NET 8 | Build Core without the game (netstandard2.1 fallback), build and run the tests |
| UI module checks | ubuntu-latest, Node 22 | `node --check`, `tools/ui_smoke.mjs` |
| Repository checks | ubuntu-latest, Python 3.12 | `tools/check_personal_data.py`, `tools/check_links.py` |

The game layer cannot be built on CI (it needs the game's assemblies). Build it locally with `tools/build.sh` before
you open a pull request that touches `src/SkylineTogether`.

## In-game testing

Changes to the game layer need a test in game: the solo test (record and replay) on one PC and, for anything involving
two players, a session. See the [player testing guide](../guide/testing.md) and [debugging.md](debugging.md).
