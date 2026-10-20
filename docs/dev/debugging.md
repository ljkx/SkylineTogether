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
capture, apply, motion, join, gate, probe, ui, config, protocol, replication, integrity, fields, compat,
diagnostics. Error pop-ups of the game's logger are suppressed for this log.

The log is rate-limited and capped at 64 MiB per game run (then errors only); a line such as `N log line(s) left out
(too many at once)` or `the log reached 64 MiB in this run` marks suppressed lines. Paths in the user folder are
written as `%USERPROFILE%` (`PathRedactor`), and the host's address after a direct join is shortened (`203.0.113.x`,
`AddressMask`), so the file can be attached to a public issue as it is.

The first line after start is `Skyline Together <assembly version> loading`. The assembly version is pinned to
`1.0.0.0` (the network id system is saved by assembly-qualified name), so it does not tell builds apart; see
[Build identity](#build-identity).

## Diagnostics dump

**Write diagnostics** (panel → Network & diagnostics, the **Details** link of the self-test line, or Options → Skyline
Together → Multiplayer) writes `Logs\SkylineTogether-diagnostics-<yyyyMMdd-HHmmss>.txt` (`Diagnostics/DiagnosticsDump.cs`)
with these sections:

- Versions (build ID and how it was found, game version, locale, where the mod is installed and every other copy the game found)
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
`P-CTX`, `P-SYSTEMS`, `P-GATE`, `P-COSMETIC`, `P-STEAM`, `P-STEAMLANES`, `P-RELAY`, `P-UI`. `P-STEAMLANES` only warns:
when its loopback self-test fails, connections run without Steam lanes (every message on lane 0) and the log line
"connected: … , 3 lanes" is missing.

`P-REFS` (`Diagnostics/ReferenceProbe.cs`, Core `Diagnostics/ReferenceCheck.cs`) is the in-game form of
`tools/ApiCheck`: right after load, a background thread resolves every row of the TypeRef and MemberRef tables of
`SkylineTogether.dll` and `SkylineTogether.Core.dll` against the assemblies the game loaded. The log has one line per DLL,
for example `P-REFS SkylineTogether: 1206 type and 5560 member references resolved in 0.05 s, 0 missing, 0 other
failure(s)`. When something does not resolve, the method bodies are read to find which types use it, and the log has a
`P-REFS: MemberRef 0x0A000123 used by SkylineTogether.Sync.MoverClient.Apply(): MissingMethodException: ...` line per
use. The feature of that type (namespace table in `ReferenceProbe`) is switched off. A reference that no method body,
field or signature uses (a custom attribute) is only reported. It checks what the mod was compiled against
(public game API); private members and names looked up at run time are the other probes' job.

Results go to the log ("self-test: X/Y checks passed"), the diagnostics dump and the one-line summary in the player's
language ("Self-test: X of Y checks passed", with "Switched off: …" naming the features that are off) in the panel and
on the title screen. A failed probe switches its feature off (`Diagnostics/Features.cs`), so the feature never touches a
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
- **Show network stats** (panel, or Options → Show network stats) adds "ping · in KB/s · out KB/s" and the queued bytes.

## Integrity and desync

`Sync/IntegritySync.cs` logs each confirmed difference ("differs from the host; requesting …"), repairs that did not
help ("still differ from the host after 2 repairs; not repaired again for 10 min"), objects given up for the session
and per-component explanations of what differs. Search the client's log for `[integrity]`. The behaviour is
described in [Troubleshooting → desync](../guide/troubleshooting.md#something-looks-different-for-another-player-desync).

## Movers

Vehicles and creatures ([ARCHITECTURE.md#movers](../ARCHITECTURE.md#movers)) log under `[replication]` and `[motion]`:

- Host: `peer:N: motion stream started; a full mover roster pass follows` when a player's first view report arrives, then
  `mover roster: pass P sent (M movers, fast pass for a joining player)`; a pass that found movers whose entity vanished
  without a despawn says so (`... movers that vanished here without a despawn were despawned`).
- Client: `movers: N pinned at their snapshot pose, K unbound movers removed` at going live, then
  `movers: first roster pass after joining: removed R movers the host no longer has (A aircraft), asked the host for Q
  missing here`; later passes are logged only when they removed or asked for something.
- The diagnostics dump has a `movers:` and a `mover roster:` line on both sides (far-field frames of aircraft, ships and
  trains, windows, passes, removals, re-sends).

## Solo test

The solo test (record a session on the host, replay it through the client path on the same PC) exercises the joining
player's code without a second PC and writes `verdict.txt` / `verdict.json` naming the components that differ. It is the
fastest way to check a change to capture, codec or apply code. See the [testing guide](../guide/testing.md#solo-test-one-pc)
and [ARCHITECTURE.md](../ARCHITECTURE.md#solo-test-record-and-replay).

## Build identity

One value identifies a build: the **build ID**, the first 12 hex digits of the SHA-256 of `SkylineTogether.dll`. The
installer prints it (`tools/installer/make_installer.py`, `Install.cmd`, `TWO-PLAYER-CHECKLIST.txt`), and
`publish_paradox.py` prints it for the Paradox Mods upload. The game computes it from the DLL it loaded
(`Session/GameInfoProvider.cs`), compares it when a player joins and shows it in the panel's footer, refusals, the log
and the diagnostics dump. It changes with every compile.

The game loads mods from the DLL's bytes, so `Assembly.Location` is empty. The DLL is found through the game's mod
manager (`Diagnostics/InstallInfo.cs`), both in the local Mods folder and in a Paradox Mods subscription. The log
says where it was loaded from and how the id was found:

```
[session] build 88068cbc2833 (the installer shows the same id; players must match it)
[session] loaded from Paradox Mods (id 123456, version 1), build 88068cbc2833, loaded; build id from the DLL file (mod manager)
```

If the file cannot be found, the id falls back to the assembly's MVID. The panel then shows `<id> (mvid)` and the log
says `build id from the assembly's MVID (...)`. Such an id never equals the installer's, so a player with it is
refused by a host whose id came from the file. Please report it with the log.

When the game finds Skyline Together twice (the local Mods folder and a Paradox Mods subscription), the log has a
warning `Skyline Together is installed more than once` with every copy's source and build ID, the diagnostics dump has
an `installed:` line and a notification asks the player to keep one copy; see
[publishing-paradox-mods.md](publishing-paradox-mods.md#installed-twice).
