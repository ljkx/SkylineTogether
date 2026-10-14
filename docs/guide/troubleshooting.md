# Troubleshooting

- [Join refused](#join-refused)
- [Join fails or times out](#join-fails-or-times-out)
- [Different builds, game versions or mods](#different-builds-game-versions-or-mods)
- [Connection: Steam relay, direct IP and firewalls](#connection-steam-relay-direct-ip-and-firewalls)
- [Hosting and joining are unavailable](#hosting-and-joining-are-unavailable)
- [Something looks different for another player (desync)](#something-looks-different-for-another-player-desync)
- [Repeated errors and feature switch-offs](#repeated-errors-and-feature-switch-offs)
- [Missing assets](#missing-assets)
- [The panel does not appear](#the-panel-does-not-appear)
- [Collecting logs](#collecting-logs)

## Join refused

When the host's game refuses a join, the joining player sees a screen with a title, an explanation and, for most
reasons, a box **Details from the host** with the host's own text. **Copy details** copies everything for a bug report.

The host checks, in this order: protocol version, a second connection of the same player, mod build, game version,
mods (only with **Require the same mods** on), player limit, password.

| Screen title | Cause | What to do |
|---|---|---|
| **Skyline Together versions differ** | The protocol versions differ, or the two DLLs are different builds. Every compile is a different build, even with the same version number. | Install the same `SkylineTogether-Setup.zip` on every PC and compare the build id the installer prints. |
| **Game versions differ** | The game versions differ (shown as "Host: … · You: …"). | Update the game on both PCs (Steam may hold back an update until the game restarts). |
| **Your mods don't match the host's** | The host requires the same code mods and yours differ. The screen lists what to **Enable or subscribe**, **Disable** and **Update to the host's version**. | Change the mods as listed, restart the game, join again. See [Different builds, game versions or mods](#different-builds-game-versions-or-mods). |
| **The session is full** | The session already has as many players as **Max players** (which counts the host). | Ask the host to raise the limit, or wait until someone leaves. |
| **Wrong password** | The password does not match. | Enter the right password in the field on the screen and click **Join with this password**. |
| **This session needs a password** | No password was entered (for example after accepting a Steam invite, which does not carry the password). | Ask the host for it and enter it on the screen. |
| **The host isn't ready yet** | An earlier join attempt of yours is still running on the host (the host heard from it in the last 15 seconds). | Wait a moment and click **Try again**, or restart the game. |

Two more screens exist for reasons the host does not send in the current version: **You can't join this session** and
**The host is closing the session**.

## Join fails or times out

| What you see | Meaning | What to do |
|---|---|---|
| **No answer from the host** ("The host did not answer. Check the address or Steam ID and that the host is hosting.") | Nothing answered within 30 seconds. | Check the Steam ID (17 digits) or the address. The host must have clicked **Host this city** and still be in the city. |
| "The host did not send the city in time. Try joining again." | The host's save of the city did not arrive within 5 minutes. | Very large cities on slow PCs; try again. The host's log shows how long saving took. |
| "The city download stalled (nothing received for 120 s). Try joining again." | The download stopped. | Usually a network problem on either side; try again. |
| "The host did not confirm the join after the city loaded. Try joining again." | Your game loaded the city but the host did not confirm within 2 minutes. | Try again; send both players' logs if it repeats. |
| "The downloaded city is damaged (checksum mismatch). Try joining again." | The download arrived corrupted. | Try again. |
| **Connection lost** / **Joining failed** | The connection broke, or something failed while joining. The details line says what. | Try again; if it repeats, [collect logs](#collecting-logs) from both PCs. |
| "The host's city uses map or climate assets you do not have (…)" | The host's map or climate comes from a Paradox Mods asset you lack. | Subscribe to the same map on Paradox Mods (or use a vanilla map) and join again. |

On the host, a player that sends nothing for 3 minutes while joining is dropped, with the chat line "&lt;name&gt; stopped
responding while joining."

## Different builds, game versions or mods

- **Build.** The game compares the identity of the Skyline Together DLL, so every player must install the very same
  build. The installer prints a **build id** (the start of the DLL's SHA-256); compare it before you play. Building the
  mod yourself from the same commit does not guarantee an identical DLL on another PC; share one setup zip instead.
- **Game version.** Compared exactly. Update both games.
- **Mods.** Only code mods count (assets don't). Mods are matched by name and compared by version. Mods that only
  affect a player's own screen never block a join; see [Mods](getting-started.md#mods). If a mod is reported that you
  believe is local-only, report it with its name so it can be classified.
- For the first test, the simplest setup is an empty playset on both PCs.
- Disable any other multiplayer mod; the installer warns about a known conflicting one.

## Connection: Steam relay, direct IP and firewalls

- **Joining by Steam ID or invite** uses Steam Networking Sockets peer-to-peer connections, which Steam routes through
  Valve's relay network (with NAT traversal and encryption). No port forwarding is needed. Steam must be running and
  logged in on both PCs.
- **Direct IP** (host setting **Allow direct IP connections**, off by default) listens on a UDP port (default 27015,
  setting **Direct connection port**). It needs that UDP port forwarded on the host's router and allowed in the host's
  firewall. It still runs through Steam's networking library, so Steam must be running for direct IP joins too. If
  the port is already in use, direct IP stays off (the log says "could not listen on UDP port …") while Steam joins
  keep working.
- **Steam invites** need the Steam overlay only for the overlay's invite dialog. The **Invite friends** list in the
  panel invites directly without the overlay. If an invite cannot be read, join with the host's Steam ID instead.
- If the self-test reports the Steam relay check as a warning (see the panel's self-test line), Steam ID joins may be
  slower to connect or fail on restrictive networks.

## Hosting and joining are unavailable

At startup, after a city loads and once the UI is up, Skyline Together runs a self-test of the game internals it uses.
The result is shown as "Self-test: X/Y checks passed" (title screen Multiplayer menu and the panel, with a **Details**
link that writes diagnostics). A failed check switches its feature off. If a critical feature (session, Steam,
replication or speed control) fails, the panel shows "Hosting and joining are unavailable: …".

This usually means a game update changed something Skyline Together relies on. [Write diagnostics](#collecting-logs) and
report it with your game version.

## Something looks different for another player (desync)

The host's game hashes the replicated objects in a rolling pass (about 0.5 ms per frame) and sends the hashes with its
updates. Each joined player's game compares them with its own world, starting 10 seconds after joining:

1. A group of objects whose hash differs is narrowed down to single objects.
2. A difference must be seen twice, at least 1.5 seconds apart, before it counts (45 seconds for citizens and other
   agents, which change often).
3. The player's game then asks the host to send that object again. Repairs are rate-limited, so a large difference
   takes a while to heal.
4. An object that still differs after two repairs is left alone for 10 minutes ("unresolved"); after two such periods it
   is given up for the rest of the session.

The **Network & diagnostics** foldout of the panel shows the state, for example "verified 12,345 objects, 3 repairs,
1 unresolved". Repairs happen in the background and need no action. Not everything is compared: citizen values, for
example, are not.

If something stays different (a road, a building, a line, a number in a panel):

- Note what differs and where, take a screenshot on both PCs.
- Click **Write diagnostics** on both PCs, then [collect logs](#collecting-logs).
- Leaving and joining again gives the joining player a fresh copy of the host's city.

## Repeated errors and feature switch-offs

Each part of Skyline Together runs behind a guard. When a part fails 5 times within 30 seconds:

- a non-critical feature switches off for the session: "Skyline Together: … hit repeated errors and is off for this
  session. Please use "Write diagnostics" and send us the file."
- if the replication on a joined player's game fails repeatedly, that game leaves the session: "This game hit repeated
  errors in …, so it left the session. Reconnect to get a fresh copy of the city."
- if the host cannot apply one player's changes repeatedly, that player is removed: "The host could not apply your
  changes. Join again to get a fresh copy of the city."
- a critical failure on the host ends the session, and diagnostics are written automatically.

All of these are bugs. Please report them with the diagnostics file and logs.

## Missing assets

When the host uses an asset (a building, prop or other prefab) that a player's game does not have, objects of that
asset are not created on that player's game. The sync status line, one notification and the diagnostics file list the
missing assets. If that player tries to place an object whose asset the host lacks, the host refuses it and it is
undone. Use the same asset subscriptions on every PC.

## The panel does not appear

If the Skyline Together button or panel does not load, a notification says so. You can still host, join and leave from
**Options → Skyline Together → Multiplayer**. Please send the diagnostics file (button on the same page) and `UI.log`.

## Collecting logs

1. **Write diagnostics** on every PC involved: in the panel's **Network & diagnostics** foldout, the **Details** link of
   the self-test line, or Options → Skyline Together → Multiplayer. It writes
   `SkylineTogether-diagnostics-<date>-<time>.txt` next to the log and shows the path. It contains versions, the
   self-test results, the session and its players, network numbers, settings without passwords, mods, and the last
   400 log lines. Diagnostics are also written automatically after a critical failure.
2. Double-click **`Collect-Logs.cmd`** in the setup folder. It zips the following into
   `SkylineTogether-logs-<date>-<time>.zip` on your Desktop:
   - `Logs\SkylineTogether*` (the mod's log and the diagnostics files), `Logs\Modding.log`, `Logs\UI.log`,
   - `Player.log` and `Player-prev.log` (the game's Unity logs),
   - the verdict files of the 3 newest solo test recordings,
   - the game's last two crash reports (`.log`, `.dmp`, `.txt`, `.json` under 200 MB).
3. Attach the zips of **all** players to the bug report, with a short note: what you did, what each player saw.

Locations, under `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\`:

| File | Path |
|---|---|
| Mod log | `Logs\SkylineTogether.log` |
| Diagnostics | `Logs\SkylineTogether-diagnostics-*.txt` |
| Game log | `Player.log` |
| Solo test recordings | `ModsData\SkylineTogether\Recordings\` |

Privacy: passwords and chat messages are not written to the diagnostics. Logs and diagnostics do contain player names
and Steam IDs. If you do not want to post them publicly, say so in the issue.
