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

### Property-based tests and the decoder contract

`Framework/Gen.cs` holds a small seeded generator for randomized tests:

- `Prop.ForAll(name, seed, iterations, g => ...)` runs a property with a fresh `Gen` per case; a failure names the
  case seed, so it reproduces on its own. `Gen` leans towards edge cases (`Size`, `UIntEdgy`, hostile `String`s) and
  mutates byte arrays (`Mutate`).
- `DecoderContract.Check` / `DecoderContract.Fuzz` hold every decoder of untrusted bytes to its contract: it returns or
  throws `DecodeException`, nothing else, and allocates in proportion to its input (a length field claiming megabytes
  must not cost megabytes).
- Model-based tests (for example `Sync/NetIdAllocatorModelTests.cs`) apply random operations to the real class and to
  a plain reference model and compare them after every step. `Session/SessionSimulationTests.cs` is the multi-peer
  simulation: a host and up to six clients over `NetSimTransport`, a random script of joins, leaves, kicks, cut links,
  chat and presence, invariants every frame and full convergence at the end.

Keep seeds fixed and iteration counts small enough that the whole suite stays well under a minute.

### What is not tested yet

```sh
python tools/test_inventory.py                 # per Core folder: types, tested directly/indirectly, game-reachable
python tools/test_inventory.py --untested      # types no test names (GAME = the game layer reaches it)
python tools/test_inventory.py --members       # public members of tested types that no test names
python tools/test_inventory.py --unused        # types the game layer does not reach (dead-code candidates)
```

The inventory is static (identifier references with comments and strings stripped, no coverage tool), so it shows
which areas have no test at all, not which branches run.

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
- a main menu entry, top-left button or root that was not registered,
- a plain object rendered as a child (React throws on that: a binding field that was not a string),
- a window listener or store subscription that an effect adds and its cleanup does not remove (Enter and Escape are
  pressed on every surface first),
- a `var(--token)` the game's stylesheet does not define (only when the game's `index.css` is found through `GameDir`
  or the default Steam folder).

The states include partial and malformed data, values of the wrong type, 200 players, very long names and messages,
and right-to-left and emoji text. `node tools/ui_smoke.mjs --html <folder>` also writes one HTML snapshot per state
and surface (built-in look, inline styles, the game's tokens and fonts when installed) to look at the layout in a
normal browser; Gameface is not Chrome, so a snapshot is a layout check, not the exact in-game look.

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
