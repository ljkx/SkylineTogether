## What and why

<!-- What does this change, and why? Link the issue if there is one ("Fixes #123"). -->

## How it was tested

<!-- tools/build.sh output, new or changed tests, and for game-layer changes: what you tried in game (solo test,
     two-player session, which steps of the checklist in docs/guide/testing.md). -->

## Checklist

- [ ] `tools/build.sh` passes locally (or, without the game: the Core build and tests, see docs/dev/building.md)
- [ ] New Core behaviour has tests in `tests/SkylineTogether.Core.Tests`
- [ ] Wire format, save format or data files changed? Then the relevant docs (docs/ARCHITECTURE.md, SYNC-COVERAGE.md, ...)
      are updated
- [ ] User-visible texts are in both `locale.en-US.json` and `locale.de-DE.json`
- [ ] The change is my own work and contains no code copied from other multiplayer mods, proprietary sources or
      decompiled game code (see CONTRIBUTING.md)
- [ ] No personal data (local paths, Steam IDs, logs) in the diff
