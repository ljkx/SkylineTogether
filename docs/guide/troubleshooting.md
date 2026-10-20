# Troubleshooting

- [Join refused](#join-refused)
- [Join fails or times out](#join-fails-or-times-out)
- [Different builds, game versions or mods](#different-builds-game-versions-or-mods)
- [Mods required for this city](#mods-required-for-this-city)
- [Two multiplayer mods at once](#two-multiplayer-mods-at-once)
- [Different DLC, asset packs or mod settings](#different-dlc-asset-packs-or-mod-settings)
- [Connection: Steam relay, direct IP and firewalls](#connection-steam-relay-direct-ip-and-firewalls)
- [Hosting and joining are unavailable](#hosting-and-joining-are-unavailable)
- [Something looks different for another player (desync)](#something-looks-different-for-another-player-desync)
- [Repeated errors and feature switch-offs](#repeated-errors-and-feature-switch-offs)
- [Missing assets](#missing-assets)
- [The panel does not appear](#the-panel-does-not-appear)
- [Reporting a bug](#reporting-a-bug)

## Join refused

When the host's game refuses a join, the joining player sees a screen with a title, an explanation and, for most
reasons, a box **Details** with the host's own text. **Copy details** copies everything for a bug report.

The host checks, in this order: bans, **Friends and invited only**, protocol version, password (always for direct IP
joiners), and only then build, game version, the old "CS2 Multiplayer Mod" on either side, DLC and content packs, mods
that store data in the host's city (always), the other mods (only with **Require the same mods** on), asset packs (only
with **Require the same asset packs** on), joins of yours that did not finish, a second connection of the same player
and the player limit. A player who has not passed the password learns nothing about versions, mods, DLC or assets.
The texts on these screens come from the host in your game's language (English when your game has no translation).

| Screen title | Cause | What to do |
|---|---|---|
| **Skyline Together versions differ** | The protocol versions differ, the two DLLs are different builds, or the host sent data your version cannot read. Every compile is a different build, even with the same version number. The screen shows "Host build … · Your build …" (or the network protocol of each side). | Use the same release on every PC (the same Paradox Mods version, or the same setup zip) and compare the build ID at the bottom of the Skyline Together panel. |
| **Game versions differ** | The game versions differ (shown as "Host: … · You: …"). | Update the game on the PC with the older version (Steam may hold back an update until the game restarts). |
| **Mods required for this city** | The host's city stores data of mods you don't have. Loading the city without them would crash your game, so the join is refused whatever the host's options say, and there is no **Join anyway**. The screen lists them under **Enable these mods to join**, each with "This city contains its data" or "It can store data in a city"; other differences are listed below them. | Enable or subscribe to the listed mods, restart the game and join again. See [Mods required for this city](#mods-required-for-this-city). |
| **Two multiplayer mods at once** | You, or the host, have the old "CS2 Multiplayer Mod" enabled. | Disable it on the PC that has it, restart the game and join again. See [Two multiplayer mods at once](#two-multiplayer-mods-at-once). |
| **Your mods don't match the host's** | The host requires the same code mods and yours differ. The screen lists what to **Enable or subscribe**, **Disable** and **Update to the host's version**. | Change the mods as listed, restart the game, join again. See [Different builds, game versions or mods](#different-builds-game-versions-or-mods). |
| **You're missing DLC or content packs the host has** | The host owns DLC or content packs that you don't; the host's city can contain objects from them, which your game can't load. The screen lists them under **Get these** (and, for information, what only you have). | Get the listed DLC (Steam or the Paradox launcher), restart the game and join again. See [Different DLC, asset packs or mod settings](#different-dlc-asset-packs-or-mod-settings). |
| **Your asset packs don't match the host's** | The host turned on **Require the same asset packs** and yours differ. The screen lists what to **Subscribe**, **Disable (the host doesn't have them)** and **Update to the host's version**, each with its Paradox Mods id. "The asset packs differ (too many to list)" in the details means one side has too many packs to compare one by one. | Change the asset packs as listed, restart the game, join again; or ask the host to turn the option off. |
| **The session is full** | The session already has as many players as **Max players** (which counts the host). | Ask the host to raise the limit, or wait until someone leaves. |
| **Wrong password** | The password does not match. | Enter the right password in the field on the screen and click **Join with this password**. |
| **Too many wrong passwords** | There were too many wrong passwords from your Steam ID or address ("Try again in …": three wrong guesses are free, then each one locks you out longer, up to 10 minutes), or several join attempts of yours were running at the same time ("Too many join attempts at once" in **Details**). | Wait the time shown in **Details**, check the password with the host, then try again. |
| **This session needs a password** | No password was entered (for example after accepting a Steam invite, which does not carry the password). | Ask the host for it and enter it on the screen. |
| **The host isn't ready yet** | An earlier join attempt of yours is still running on the host (the host heard from it in the last 15 seconds). | Wait a moment and click **Try again**, or restart the game. |
| **Please wait before joining again** | Your last joins did not finish, and each one paused the game for everyone ("You can join again in …": the first such join costs nothing, unless the host dropped it for making no progress; then 30 seconds, 2, 10 and 30 minutes, and longer, up to 4 hours, once your failed joins have held everyone's game for more than half an hour in total). | Wait the time shown, then click **Try again**. If your joins keep failing, see [Join fails or times out](#join-fails-or-times-out). |
| **You are banned from this session** | The host banned you (by Steam ID, and by address if you joined by direct IP). | Only the host can lift it (host options, **Banned players**, **Unban**). |
| **You can't join this session** | The host allows only Steam friends and invited players, or you connected by direct IP to a host without a password. | Ask the host for an invite (or to set a password for direct IP). |
| **The host is closing the session** | The host stopped hosting or left the city while you were joining. | Join again when the host hosts again. |
| **You were removed from the session** | The host or a co-host kicked you. | You can join again unless the host also banned you. |
| **The session has ended** | The host ended the session or left the city. | Join again when the host hosts again. |

## Join fails or times out

The screen's title is one of those in the first column; the **Details** box shows one of the quoted lines and says what
happened.

| Title / details line | Meaning | What to do |
|---|---|---|
| **Your game couldn't load the host's city** | The city arrived, but loading it failed (for example missing map or climate assets). | Try again. If it keeps failing, see [Missing assets](#missing-assets) and attach the copied details to a [bug report](#reporting-a-bug). |
| **No answer from the host** ("The host didn't answer in time. Check the Steam ID or address and make sure the host is hosting right now, then try again.") | Nothing answered within 30 seconds, or the connection could not be started. | Check the Steam ID (17 digits) or the address. The host must have clicked **Host this city** and still be in the city. |
| **No answer from the host**: "The host could not save the city (…). Try joining again." or "The host did not send the city in time. Try joining again." | The host's game did not finish saving the city within 6 minutes (your game waits 7). | Very large cities on slow PCs; try again. The host's log shows how long saving took. |
| **No answer from the host**: "The city download stalled (nothing received for 120 s). Try joining again." | The download stopped. | Usually a network problem on either side; try again. |
| **No answer from the host**: "The host did not confirm the join after the city loaded. Try joining again." | Your game loaded the city but the host did not confirm within 2 minutes. | Try again; if it repeats, [report it](#reporting-a-bug) with both players' logs. |
| **Your game couldn't load the host's city**: "Loading the host's city did not finish in 20 minutes. Try joining again." | Your game took longer than 20 minutes to load the city. | Close other programs and try again; very large cities need a fast PC. |
| **No answer from the host**: "The host stopped responding (nothing received for N s). …" | The host's game froze, closed or lost its connection. | Ask the host; join again when they are hosting. |
| **Joining failed**: "The downloaded city is damaged (checksum mismatch). Try joining again." | The download arrived corrupted. | Try again. |
| **The host stopped your join** | The host's game stopped your join for a technical reason: the download or the load made no progress, the join took too long, or your game stopped responding or could not keep up. **Details** says which. | Try again; if it repeats, [collect logs](#reporting-a-bug) from both PCs. |
| **Connection lost** / **Joining failed** | The connection broke, or something failed while joining. The details line says what. | Try again (a joined player's game reconnects by itself); if it repeats, [collect logs](#reporting-a-bug) from both PCs. |
| **Couldn't confirm the host** | The host could not prove that it knows the session password: on a direct IP join it did not ask for the password at all, or it answered the check wrongly. You may have connected to someone else, or the host changed the password. | Check the Steam ID or address and the password with the host, then enter the password on the screen and click **Join with this password**. |
| "Looking up …" (joining by host name) | The name is resolved in the background before the connection starts. | Wait; if the name cannot be resolved, check it or use the IP address. |
| "The host's city uses map or climate assets you do not have (…)" | The host's map or climate comes from a Paradox Mods asset you lack. | Subscribe to the host's map on Paradox Mods (or use the same map) and join again. |

On the host, a player that sends nothing for 3 minutes while joining is dropped, with the chat line "&lt;name&gt; stopped
responding while joining." A join is also dropped when its download makes no progress for 3 minutes, its game stops
working on the load for 5 minutes or the load takes more than 20, the whole join takes more than 30 minutes
("&lt;name&gt; took too long to join."), or it keeps asking for the city again ("&lt;name&gt; could not finish
joining."). The joining player is told why and can try again after a short wait.

## Different builds, game versions or mods

- **Build.** The game compares the build ID of the Skyline Together DLL, so every player must install the very same
  build. The build ID is the start of the DLL's SHA-256: the panel's footer and the diagnostics show it, the GitHub
  installer prints it, and a refusal shows both players' ids; compare it before you play. Building the mod yourself
  from the same commit does not guarantee an identical DLL on another PC; use a published release instead.
  A Paradox Mods subscription and the GitHub setup of the same release have the same build ID. If the footer shows
  "(mvid)" after the id, the game could not find the DLL file; please [report it](#reporting-a-bug) with the log.
- **Installed twice.** The notification "Skyline Together is installed twice" means the game found a copy in the
  local `Mods` folder and a Paradox Mods subscription (or two of either). Only one is loaded, and it may not be the
  build you expect. Delete `Mods\SkylineTogether` (or run the installer with `-Uninstall`) or unsubscribe, then
  restart the game.
- **Game version.** Compared exactly. Update both games.
- **Mods.** Only code mods count here (DLC and asset packs have their own check, see
  [Different DLC, asset packs or mod settings](#different-dlc-asset-packs-or-mod-settings)). Mods are matched by name
  and compared by version. Mods that only
  affect a player's own screen never block a join; see [Mods](getting-started.md#mods). If a mod is reported that you
  believe is local-only, report it with its name so it can be classified. If the host lets players join with different
  mods (the default), the refusal screen also offers **Join anyway**; see
  [Joining anyway with different mods](#joining-anyway-with-different-mods).
- The simplest setup is an empty playset on every PC.
- Disable any other multiplayer mod; the installer warns about a known conflicting one, and the game refuses joins
  while the old "CS2 Multiplayer Mod" is enabled on either PC (see
  [Two multiplayer mods at once](#two-multiplayer-mods-at-once)).

## Mods required for this city

Some mods save their own data in the city (for example Zoning Toolkit's zoning settings on roads, Traffic's lane
connections). The game cannot load a city that contains data of a mod it does not have: it crashes while loading
(Player.log ends with "Not serializable type: …"). So a player who lacks one of the host's mods that store city data
is always refused, even when the host turned **Require the same mods** off or lets players join with different mods,
and the refusal screen has no **Join anyway** for them:

- The title is **Mods required for this city**. Under **Enable these mods to join** each mod says why: "This city
  contains its data – loading it without the mod would crash the game." (the host's city holds its data right now) or
  "It can store data in a city – loading the city without the mod can crash the game." (the mod can save data, but
  none was found in this city; it is required anyway, because nothing proves the save has no trace of it).
- Other differences (mods without city data, extra mods only you have, other versions) are listed below under **Other
  differences**. If the host lets players join with different mods, the text says that those can be passed with **Join
  anyway** once the required mods are enabled.
- The host sees "&lt;name&gt; can't join: they don't have …, which store data in this city." as a notification and a chat
  line.
- How the host's game decides: every loaded mod is scanned (saved component types and saved system state count; a scan
  that fails counts too), and a built-in list knows mods that save data (Traffic, Anarchy, Better Bulldozer, Water
  Features, Zoning Toolkit, Plop the Growables, Tree Controller). The host's log lists them when hosting starts
  ("joiners must have these mods …"), and the diagnostics file marks each one with "stores city data".
- Your own game checks again right before it loads the city: if the host's notice names a mod that stores city data and
  your game lacks it, the join stops with the same screen instead of loading.
- A mod only you have is not a problem for loading (it is like adding a mod to a save); it stays a normal difference.

## Two multiplayer mods at once

Skyline Together does not work together with the old "CS2 Multiplayer Mod" (found by its name or its Paradox Mods id).
While it is enabled:

- on a joining player's PC: the join is refused with "The "CS2 Multiplayer Mod" is enabled, and two multiplayer mods
  can't run at the same time." Disable it in your playset or unsubscribe, restart the game and join again;
- on the host's PC: every player is refused ("The host has the "CS2 Multiplayer Mod" enabled …"). The host's panel keeps
  a red warning while it is enabled, a notification says so when hosting starts, and each refused player is named in
  the chat. Disable it and restart the game.

No option or **Join anyway** skips this. The installer warns about it as well.

## Different DLC, asset packs or mod settings

After the password, the host compares a digest per category with the joining player's and, where one differs, the
lists behind it:

- **DLC and content packs** (those that add buildings or other objects; radio-only packs don't count). A player who
  lacks one the host has is refused ("You're missing DLC or content packs the host has"), because the host's city can
  contain objects from it. DLC only the joining player has does not block; they are told "You have DLC or content packs
  the host doesn't have: … Building with them is undone in this session."
- **Asset packs** (Paradox Mods packages that add objects, also the assets of code mods), compared by Paradox Mods id
  and by their contents, so another version of a pack counts as different. By default a difference does not block. The
  joining player is told, as a notification and a chat line:
  - "The host has asset packs you don't have: … Objects from them are left out in your game. Subscribe to them and join
    again to see everything."
  - "These asset packs are at a different version from the host's: … Some objects may be missing or look different;
    update them to the host's version."
  - "You have asset packs the host doesn't have: … The host can't place their objects, so building with them is undone."
  - "Your asset packs differ from the host's (too many to list). …" when one side has too many packs to send the list.

  The host sees "&lt;name&gt; doesn't have N of your asset packs …" and the log names them. With **Require the same
  asset packs** on (host options; off by default) any difference refuses the join instead. Assets you made yourself
  (not published) are not compared here; see [Missing assets](#missing-assets).
- **Mod settings** that affect the simulation (those the host pushes to everyone, see
  [mod compatibility](../design/MOD-COMPATIBILITY.md)): never a refusal. The host's values apply for the session and the
  joining player is told "While you play together, the host's values apply to these mod settings: … Yours come back when
  you leave."
- **Code mods** when the host turned **Require the same mods** off: "Your code mods differ from the host's (the host
  allows it): … Parts of the city may look or behave differently for you." Mods that store data in the host's city are
  still required ([Mods required for this city](#mods-required-for-this-city)).

## Joining anyway with different mods

When the host requires the same mods (or the same asset packs) and has **Let players join with different mods** on
(the default), the refusal screen still lists what differs (what to enable, disable or update), says that the host
also lets you join anyway, and shows a **Join anyway** button next to **Back** with the warning "Different mods can make
your cities drift apart or behave differently. The host's city always takes priority; differences are fixed where
possible." The best fix is still to match the host's mods and restart the game.

- **Join anyway** connects again at once. You are told "You joined with different mods: …" as a notification and a chat
  line, plus the usual asset pack lines. The host gets "&lt;name&gt; joined with different mods: …" as a notification and
  a chat line; the host's log and diagnostics file list the accepted differences.
- Your game remembers the choice for that host until you quit the game, so an automatic reconnect or a rejoin does not
  stop at the screen again.
- One **Join anyway** covers code mods and asset packs. The screen shows the code mods first; asset packs that differ as
  well are listed in the notice after joining.
- What it means in play: the host's city always takes priority. Objects whose asset or mod you lack are left out in your
  game, data of a mod you don't have is skipped (the log names it), the host's mod settings apply to the mods you both
  have, messages of a mod you don't have are ignored, and the integrity check repairs objects that differ. What a mod
  does only on one PC (its own simulation, tools, overlays) can still differ. If the cities drift apart, match the mods
  and rejoin.
- It never skips: a different Skyline Together build or protocol version, a different game version, DLC or content
  packs you lack (those are checked first), mods that store data in the host's city
  ([Mods required for this city](#mods-required-for-this-city)), the old "CS2 Multiplayer Mod"
  ([Two multiplayer mods at once](#two-multiplayer-mods-at-once)), the password, bans, the friends-only policy or a full
  session.
- With the option off, the screen has no button and says that the host requires the same mods.

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

At startup (including a background check, within the first minute, that the mod's code still fits this game version),
after a city loads and once the UI is up, Skyline Together runs a self-test of the game internals it uses. The result
is shown as "Self-test: X of Y checks passed" (title screen Multiplayer menu and the panel, with a **Write diagnostics**
link). A failed check switches its feature off, and the line names it ("Switched off: …"). If a critical feature
(session, Steam networking, city sync or game speed) fails, the panel shows "Hosting and joining are unavailable: …".

This usually means a game update changed something Skyline Together relies on. [Write diagnostics](#reporting-a-bug) and
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
- Click **Write diagnostics** on both PCs, then [collect logs](#reporting-a-bug).
- Leaving and joining again gives the joining player a fresh copy of the host's city.

## Repeated errors and feature switch-offs

Each part of Skyline Together runs behind a guard. When a part fails 5 times within 30 seconds:

- a non-critical feature switches off for the session: "Skyline Together: … hit repeated errors and is off for this
  session. Please use "Write diagnostics" and attach the file to a bug report on GitHub
  (github.com/ljkx/SkylineTogether/issues)."
- if the city sync on a joined player's game fails repeatedly, that game leaves the session: "Your game hit repeated
  errors in …, so it left the session. Reconnect to get a fresh copy of the city."
- if the host cannot apply one player's changes repeatedly, that player is disconnected (not kicked: their game
  offers to join again): "The host could not process your changes. Join again to get a fresh copy of the city."
- a critical failure on the host ends the session, and diagnostics are written automatically.

All of these are bugs. Please [report them](#reporting-a-bug) with the diagnostics file and logs.

## Missing assets

When the host uses an asset (a building, prop or other prefab) that a player's game does not have, objects of that
asset are not created on that player's game. Right after joining, the player is told which of the host's asset packs
they lack ([Different DLC, asset packs or mod settings](#different-dlc-asset-packs-or-mod-settings)). Before the city
loads, the joining player gets a notification naming the
missing asset packs (with their Paradox Mods ids) and mods whose generated prefabs are missing, and what to do; the
sync status line, one notification during the session and the diagnostics file list every missing asset with where it
comes from. Assets the host made locally (not published) cannot be downloaded: the host has to publish them or stop
using them. If a player tries to place an object whose asset the host lacks, the host refuses it and it is undone. Use
the same asset subscriptions on every PC.

## The panel does not appear

If the Skyline Together button or panel does not load, a notification says so. You can still host, join and leave from
**Options → Skyline Together → Multiplayer**. Please [report it](#reporting-a-bug) with the diagnostics file (button on
the same page) and `Logs\UI.log`.

## Reporting a bug

Report bugs as a [GitHub issue](https://github.com/ljkx/SkylineTogether/issues/new/choose) with the files of **every**
player in the session and a short note: what you did, what each player saw. Screenshots help.

1. **Write diagnostics** on every PC: in the panel's **Network & diagnostics** foldout, the **Write diagnostics** link
   of the self-test line, or Options → Skyline Together → Multiplayer. It writes
   `SkylineTogether-diagnostics-<date>-<time>.txt` next to the log and shows the path (your user folder as
   `%USERPROFILE%`; paste it into Explorer's address bar to open the folder). It contains versions, the
   self-test results, the session and its players, network numbers, settings without passwords, mods, and the last
   400 log lines. Diagnostics are also written automatically after a critical failure.
2. Collect the files. This depends on how you installed Skyline Together:
   - **GitHub setup:** double-click **`Collect-Logs.cmd`** in the setup folder. It writes
     `SkylineTogether-logs-<date>-<time>.zip` to your Desktop with the mod's log and diagnostics files,
     `Logs\Modding.log`, `Logs\UI.log`, the game's `Player.log` and `Player-prev.log`, the verdicts of the 3 newest
     solo test recordings and the text files of the game's last two crash reports. Paths to your user folder are
     replaced by `%USERPROFILE%` in the copied logs. Crash dumps (`.dmp`) are left out unless you ask for them (see
     below). If the zip is larger than 25 MB, it says so; attach the files from it separately then.
   - **Paradox Mods:** there is no collector; attach the files from the table below yourself. The mod log and the
     newest diagnostics file are the most important ones.
3. Attach the zip or the files to the issue. GitHub accepts files up to 25 MB.

Locations, under `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\`:

| File | Path |
|---|---|
| Mod log | `Logs\SkylineTogether.log` |
| Diagnostics | `Logs\SkylineTogether-diagnostics-*.txt` |
| UI log | `Logs\UI.log` |
| Mod loading log | `Logs\Modding.log` |
| Game log | `Player.log` (and `Player-prev.log` for the previous run) |
| Solo test recordings | `ModsData\SkylineTogether\Recordings\` |

**What the files contain.** Passwords and chat messages are never written to the logs or the diagnostics. They do
contain:

- player names, Steam IDs and the session name;
- IP addresses only shortened (`203.0.113.x`). The full IP addresses of players banned by address stay in the host's
  settings file, which is not part of a bug report;
- in the game's own logs (`Player.log`, `Modding.log`, `UI.log`), paths that include your Windows user name
  (`Collect-Logs.cmd` replaces them; if you attach these files yourself, replace the user name in a text editor
  first).

Skyline Together's own files (`SkylineTogether.log` and the diagnostics file) write paths in your user folder as
`%USERPROFILE%`, so they do not contain your Windows user name. Files written by versions before 0.2.0 contain your
user name and full IP addresses; remove them before you attach these files.

If you would rather not post this publicly, say so in the issue and ask for another way to send the files.

**Crash dumps.** A crash dump (`.dmp`) can contain anything that was in the game's memory, including passwords. Never
attach one to a public issue. If a crash dump is needed, the issue will ask for it; send it only privately. To include
dumps in the zip, run in the setup folder:

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-SkylineTogether.ps1 -CollectLogs -IncludeDumps
```
