# Documentation

## For players

- [Getting started](guide/getting-started.md): install, host, join, roles, chat, building previews, speed, saving
- [Troubleshooting](guide/troubleshooting.md): refused joins, connection problems, desync, reporting a bug
- [FAQ](guide/faq.md)
- [Checking a build](guide/testing.md): the two-player checklist and the solo test

## For contributors

Start with the [architecture](ARCHITECTURE.md) (how the mod works, written from the code) and the
[code map](dev/code-map.md) (where things are).

- [Building](dev/building.md): `tools/build.sh`, game path, building without the game, packaging
- [Tests](dev/testing.md): unit tests, UI smoke test, repository checks, CI
- [Debugging in game](dev/debugging.md): logs, diagnostics dump, self-test, status lines
- [Releasing](dev/releasing.md): versions, build id, installer, release steps, and the
  [release notes template](dev/release-notes-template.md)
- [Publishing on Paradox Mods](dev/publishing-paradox-mods.md): ModPublisher, the page config, new versions,
  installed twice
- [Mod integration](MOD-INTEGRATION.md): for authors of other mods
- [CONTRIBUTING.md](../CONTRIBUTING.md), [CODE_OF_CONDUCT.md](../CODE_OF_CONDUCT.md), [SECURITY.md](../SECURITY.md),
  [CHANGELOG.md](../CHANGELOG.md)

## Design and research

- [ARCHITECTURE.md](ARCHITECTURE.md): the architecture as built
- [SYNC-COVERAGE.md](design/SYNC-COVERAGE.md): what is synced, for every piece of game state
- [ACTION-COVERAGE.md](design/ACTION-COVERAGE.md): how every player action is synced
- [MOD-COMPATIBILITY.md](design/MOD-COMPATIBILITY.md): how third-party mods are kept in sync
- [Decision records](decisions/)
- [Research digest](research/00-DIGEST.md) and the [research reports](research/) on the game's internals
- [history/](history/README.md): the design documents written before the implementation (original architecture,
  implementation plan, the three proposals); historical, not authoritative
