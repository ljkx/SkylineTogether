# Debugging in game

Nobody can attach the test framework to a running game, so the mod is built to explain itself: a log, a diagnostics
dump, a startup self-test, guards around every feature and a status line in the panel.

## Deploy a build

```sh
tools/build.sh --deploy
```

copies the DLLs, PDBs and the UI module into
`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\SkylineTogether`. Restart the game after each
deploy. A local mod takes precedence over a Paradox Mods subscription of the same name.

## Logs

All paths are under `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\`.

| File | Contents |
|---|---|
| `Logs\SkylineTogether.log` | The mod's log (`Diagnostics/GameLog.cs`, through the game's `LogManager`). Lines look like `[category] message`. |
| `Logs\UI.log` | The game's UI log; look here when the panel or the title screen page misbehaves |
| `Logs\Modding.log` | The game's mod loading |
| `Player.log` | Unity's log (crashes, native errors) |

The minimum level is Info and is not configurable. Categories (`Contracts/Environment/LogCategory.cs`): net, session,
capture, apply, motion, join, gate, probe, ui, config, protocol, replication, integrity, intents, scheduling, fields,
compat, diagnostics. Error pop-ups of the game's logger are suppressed for this log.

The first line after start is `Skyline Together <assembly version> loading`. The assembly version is pinned to
`1.0.0.0` (the network id system is saved by assembly-qualified name), so it does not tell builds apart; see
[Build identity](#build-identity).

## Diagnostics dump

**Write diagnostics** (panel → Network & diagnostics, the **Details** link of the self-test line, or Options → Skyline
Together → Multiplayer) writes `Logs\SkylineTogether-diagnostics-<yyyyMMdd-HHmmss>.txt` (`Diagnostics/DiagnosticsDump.cs`)
with these sections:

- Versions (mod build, game version, locale)
- Self-test and features (every probe result, which features are off and why)
- Session (mode, recovery state, peers with ping, recent join timings, players, the last system messages)
- Replication (the sync status line, per-frame timings and byte rates from `Perf`)
- Presence
- Settings (passwords left out)
- Mods (with their compatibility class), adapters, missing assets
- The last 400 log lines

It is written automatically when a client hits a local fault and when the host trips a critical feature.

## Self-test (probes)

`Diagnostics/Probes.cs` resolves, in one place, every non-public member and every type the mod finds by name, the Steam
APIs it uses and the UI module. It runs at load (types and members), after each city load (system instances, field
values) and about 20 seconds after the menu or a city appears (the UI module observed its bindings). Probe ids look like
`P-CTX`, `P-SYSTEMS`, `P-GATE`, `P-COSMETIC`, `P-STEAM`, `P-RELAY`, `P-UI`.

Results go to the log, the diagnostics dump and the one-line summary "Self-test: X/Y checks passed" in the panel and on
the title screen. A failed probe switches its feature off (`Diagnostics/Features.cs`), so the feature never touches a
member that was not verified. A failed critical feature (session, Steam, replication, speed) blocks hosting and joining
with "Hosting and joining are unavailable: …". After a game update, the self-test is the first thing to check.

## Guards

Feature code runs inside `Guard` (`Diagnostics/Guard.cs`, Core `Diagnostics/FeatureHealth.cs`): an exception is logged
(rate-limited to once per 10 s per feature) and counted. Five failures within 30 seconds trip the feature: a
non-critical feature switches off for the session with a notification; a critical one ends the session (host) or
leaves it (client), and diagnostics are written.

## Status lines

- **Panel → Network & diagnostics** shows the sync status line (`SyncController.Status`): queued updates, terrain,
  water, values, agents, flow, the integrity state ("verified N objects, M repairs"), movers, the follower clock and
  missing assets.
- **Show network** (panel, or Options → Show network line) adds "ping · in KB/s · out KB/s" and the queued bytes.

## Integrity and desync

`Sync/IntegritySync.cs` logs each confirmed difference ("differs from the host; requesting …"), repairs that did not
help ("still differ from the host after 2 repairs; not repaired again for 10 min"), objects given up for the session
and per-component explanations of what differs. Search the client's log for `[integrity]`. The behaviour is
described in [Troubleshooting → desync](../guide/troubleshooting.md#something-looks-different-for-another-player-desync).

## Solo test

The solo test (record a session on the host, replay it through the client path on the same PC) exercises the joining
player's code without a second PC and writes `verdict.txt` / `verdict.json` naming the components that differ. It is the
fastest way to check a change to capture, codec or apply code. See the [testing guide](../guide/testing.md#solo-test-one-pc)
and ARCHITECTURE [§13.5](../design/ARCHITECTURE.md#135-single-machine-test-harnesses).

## Build identity

Two different values identify a build:

- The installer's **build id**: the first 12 hex digits of the SHA-256 of `SkylineTogether.dll`
  (`tools/installer/make_installer.py`). Printed by `Install.cmd` and in `HOW-TO-TEST.txt`.
- The in-game **mod build**: the game layer assembly's MVID, both halves XORed (`Session/GameInfoProvider.cs`). It is
  what the host compares when a player joins, and it appears as "mod build" in the diagnostics dump and in replay
  verdicts.

Both change with every compile.
