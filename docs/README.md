# Documentation

## For players

- [Getting started](guide/getting-started.md): install, host, join, roles, chat, building previews, speed, saving
- [Troubleshooting](guide/troubleshooting.md): refused joins, connection problems, desync, collecting logs
- [FAQ](guide/faq.md)
- [Testing a build](guide/testing.md): the two-player checklist and the solo test

## For contributors

Start with the [code map](dev/code-map.md); it also gives a reading order for the architecture document.

- [Building](dev/building.md): `tools/build.sh`, game path, building without the game, packaging
- [Tests](dev/testing.md): unit tests, UI smoke test, repository checks, CI
- [Debugging in game](dev/debugging.md): logs, diagnostics dump, self-test, status lines
- [Releasing](dev/releasing.md): versions, build id, installer, release steps
- [Mod integration](MOD-INTEGRATION.md): for authors of other mods
- [CONTRIBUTING.md](../CONTRIBUTING.md), [SECURITY.md](../SECURITY.md), [CHANGELOG.md](../CHANGELOG.md)

## Design and research

- [ARCHITECTURE.md](design/ARCHITECTURE.md): the design (target; the code implements a subset, see its "As built" notes)
- [IMPLEMENTATION-PLAN.md](design/IMPLEMENTATION-PLAN.md): the original module plan
- [SYNC-COVERAGE.md](design/SYNC-COVERAGE.md): what is synced, for every piece of game state
- [ACTION-COVERAGE.md](design/ACTION-COVERAGE.md): how every player action is synced
- [MOD-COMPATIBILITY.md](design/MOD-COMPATIBILITY.md): how third-party mods are kept in sync
- [Research digest](research/00-DIGEST.md) and the [research reports](research/) on the game's internals
- [Decision records](decisions/)
- Proposals [A](design/proposal-A-max-fidelity.md), [B](design/proposal-B-scale-efficiency.md) and
  [C](design/proposal-C-buildable-robust.md): the three competing designs ARCHITECTURE.md was derived from
  (historical)
