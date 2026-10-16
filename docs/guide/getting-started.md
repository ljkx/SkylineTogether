# Getting started

This guide walks you through installing Skyline Together, hosting a city, joining a friend and what to expect during
a session. Skyline Together is in **early testing**: back up your saves and play on a copy of a city you don't mind
breaking.

- [Requirements](#requirements)
- [Install](#install)
- [Host a city](#host-a-city)
- [Join a friend](#join-a-friend)
- [During a session](#during-a-session)
- [Roles: host, builder, viewer](#roles-host-builder-viewer)
- [Game speed](#game-speed)
- [Saving](#saving)
- [Leaving, disconnects and reconnecting](#leaving-disconnects-and-reconnecting)
- [Settings](#settings)

## Requirements

- Cities: Skylines II, **Steam version**, with Steam running and logged in on every PC. All connections go through
  Steam, including direct IP connections.
- Every player needs:
  - the **same build** of Skyline Together (compare the build id the installer prints),
  - the **same game version**,
  - by default, the **same code mods** in the same versions (the host can relax this, see [Mods](#mods)).
- Windows (the installer is a PowerShell script).

## Install

There are two ways to install Skyline Together: a Paradox Mods subscription or the setup from GitHub. Use **one** of
them. If the game finds both a local copy and a subscription, it loads only one of them, and Skyline Together shows a
warning that names both copies and their build ids. Keep one copy and restart the game.

### From Paradox Mods

Once Skyline Together is published on Paradox Mods:

1. In the main menu open **Paradox Mods**, search for **Skyline Together** and subscribe (or subscribe on
   [mods.paradoxplaza.com](https://mods.paradoxplaza.com/games/cities_skylines_2)).
2. Make sure it is enabled in your active playset, then restart the game.
3. Compare the build id in the panel's footer with your friends'. Subscriptions update when the game starts, so after
   a new version everyone needs a restart.

To uninstall, unsubscribe (or disable it in the playset).

New test builds appear on GitHub first. To test one, unsubscribe (or disable the mod in your playset) and use the
installer below. Everyone in the session must use the same build.

### With the installer

1. Close Cities: Skylines II.
2. Get `SkylineTogether-Setup.zip`, either from the
   [Releases page](https://github.com/ljkx/SkylineTogether/releases) when a release is published, or by
   [building it yourself](../dev/building.md#packaging-and-the-player-installer).
3. Unzip it and double-click **`Install.cmd`**. The installer:
   - checks that the game's user folder exists and that the game is not running,
   - copies the mod into `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\SkylineTogether`,
   - prints a **build id** (12 characters). Every player must see the same build id.
   - lists your subscribed code mods and warns about a known conflicting multiplayer mod.
4. If you use another multiplayer mod, disable it in your playset. Two multiplayer mods at once conflict.

The setup folder also contains `Collect-Logs.cmd` (see [Troubleshooting](troubleshooting.md#collecting-logs)) and
`HOW-TO-TEST.txt`, a two-player test checklist (also in [testing.md](testing.md)).

To uninstall, run in the setup folder:

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-SkylineTogether.ps1 -Uninstall
```

or delete the `Mods\SkylineTogether` folder.

### Manually

Extract `SkylineTogether-<version>.zip` (made by `tools/build.sh --package`) into the `Mods` folder above, so the files
end up in `Mods\SkylineTogether`. Local mods take precedence over Paradox Mods subscriptions of the same name, but
don't keep both: see the note at the top of this section.

## Host a city

Hosting always starts from inside a loaded city.

1. Load a city (a copy you can afford to lose) or start a new one.
2. Click the round **Skyline Together** button in the top-left button row of the game screen. The panel opens on the
   **Host** tab.
3. Optional settings (saved as soon as you leave a field):
   - **Your name**: empty uses your Steam name.
   - **Session name**: what joining players see; empty means "&lt;your name&gt;'s city".
   - **Password**: leave empty for no password. Three wrong guesses in a row lock the guesser out for a while (longer
     with every further wrong guess).
   - **Max players**: 2 to 16, including you (default 8).
   - **Require the same mods**: on by default. See [Mods](#mods).
   - **Require the same asset packs**: off by default. When on, players whose asset packs (Paradox Mods packages
     that add objects) differ from yours are refused with a list; when off, they join and are told which packs differ.
   - **Let players join with different mods**: on by default. A player refused by one of the two settings above still
     sees the list of differences and may click **Join anyway**. Turn it off to make those refusals final. See
     [Mods](#mods).
   - **Friends and invited only**: off by default. When on, only your Steam friends and players you invite from the
     panel can join through Steam; others are told they are not invited.
   - **Allow direct IP connections**: off by default, and only available while a password is set (Steam does not check
     direct joiners, so the password does). Also accepts connections on a UDP port (default 27015), which needs port
     forwarding on your router. Steam connections work without it.
4. Click **Host this city**.
5. Invite your friends:
   - click **Invite friends** and then **Invite** next to a Steam friend, or
   - copy **Your Steam ID** (copy icon at the top of the panel) and send it to them. With direct IP enabled, they can
     also use your public IP address and port.

While a player joins, your game saves the city for them and **your simulation is paused until they are in**. The panel
shows "A player is joining – the game is paused until they are in." and the download progress per player.

## Join a friend

Joining replaces the city you are playing with the host's city, so save first.

From the title screen:

1. Click **Multiplayer** (in the main menu, after Load Game).
2. On **Join a friend**, paste the host's Steam ID (17 digits) or `IP:port` into **Host**, and the password if the
   host set one. A pasted Steam profile link works too: the field keeps only the Steam ID. If the entry cannot be a
   Steam ID or an address, a warning under the field says what is wrong and **Join** stays disabled.
3. Click **Join** (or press Enter in either field).

From inside a city, open the Skyline Together panel, **Join** tab, and do the same.

With a **Steam invite**, accept it in Steam. On the title screen the game joins by itself; inside a city a notification
asks you to click it to join (it stays for 30 seconds).

The join screen shows five stages: connecting, checking versions and mods, the host saving the city, downloading
(with size, speed and time left, once they are known) and loading. **Cancel** stops the join. If the host refuses the
join or the join fails, a screen explains why and what to do next; see
[Troubleshooting](troubleshooting.md#join-refused). On that screen Enter tries again (where trying again can help)
and Esc goes back.

## During a session

- **Everything is shared.** Roads, zoning, buildings, transport lines, terrain, water, budget, taxes, policies,
  districts and the other player actions are synced in both directions. The host's game runs the simulation; the other
  players' games show the host's city and send their actions to the host, which checks and applies them.
- **Cursors and name tags.** Each player has a colour. You see the other players' cursors as rings with a name tag
  that shows what they are doing ("building", "zoning", "bulldozing", ...).
- **Building previews.** While another player draws a road, places a building, paints zones or draws a district, you
  see their preview in their colour. Removals are filled red, invalid placements outlined red. Elevated roads, bridges
  and raised objects appear at their real height with thin lines down to the ground, tunnels dashed on the surface
  with a downward arrow, and while a player builds with an elevation their name tag shows it ("+10 m"). You can turn
  this off under Options, **Show other players' building previews**.
- **Chat.** Press **Enter** (when no text field has focus) to open the chat dock above the toolbar; Enter sends, Esc
  clears the line and leaves the chat. Messages are up to 500 characters (a counter appears near the limit); the last
  100 lines are kept, each with the time it arrived. Scrolled up to read, the chat stays put and a **new below** button
  jumps to the newest lines. Click the chat header to collapse it; collapsed, it shows the number of unread messages
  and the newest message for a few seconds.
- **Players list.** The panel lists all players with their role (as a text badge) and ping (signal bars and
  milliseconds). For the host, the **…** button on a player offers **Make viewer** / **Make builder**, **Make co-host** /
  **Remove co-host**, **Kick** and **Ban**; a co-host gets **Make viewer** / **Make builder** and **Kick** for builders
  and viewers. **Kick** and **Ban** ask for a second click (**Confirm kick**, **Confirm ban**) within 3 seconds.
- **Closing the panel.** The **×** in its title bar, the round button again, or Esc (when no text field has focus).
- **Build id.** The panel's footer (and the main menu page) shows **Build &lt;12 characters&gt;**, the same id the
  installer prints. All players must show the same one.
- **Resync.** If your city drifts apart from the host's in many places that repairs cannot fix, the panel offers
  **Rejoin now**: your game rejoins with a fresh copy of the city (as long as a join takes). **Not now** hides the offer
  for 30 minutes.
- **Network & diagnostics.** **Show network stats** adds a line with ping and traffic to the panel. **Write diagnostics**
  writes a report file for bug reports (see [Troubleshooting](troubleshooting.md#collecting-logs)).

## Roles: host, builder, viewer

| Role | Who | Can do |
|---|---|---|
| Host | The player who hosts | Everything, plus player management (kick, ban, roles, co-hosts) and session settings |
| Co-host | Appointed by the host | Everything a builder can do, plus kick builders and viewers and switch them between builder and viewer. Not: the host or other co-hosts, bans, the host's settings |
| Builder | Every player who joins (default) | Everything a player can do in the city: build, demolish, terraform, zone, economy, policies, progression, map tiles, game speed, mod tools, names, chat |
| Viewer | Set by the host | Chat only. A viewer's tools are disabled, their speed requests are ignored and they cannot follow citizens |

The host changes a role with **…** → **Make viewer** / **Make builder** / **Make co-host** in the player list, for
players who are fully in. A kicked player can join again; a banned one cannot until the host unbans them (host options,
**Banned players**). A player removed after loading the city sees "You were removed from the session" (or "removed and
banned") and can return to the main menu or keep an offline copy.

## Game speed

The host's game speed is everyone's speed; the other games never run the simulation themselves. Other players see
"Game speed (set by the host): …" in the panel.

When a builder presses the normal speed buttons, their game sends a request to the host, and the host's game applies it
for everyone (no confirmation). Viewers' requests are ignored. If a startup self-test check of the game's time
controls fails, other players can still request a speed but not a pause.

The host's pause menu or switching away from the game window does not pause a session that has players in it; a pause
the host chooses does.

## Saving

- **Host:** saving and autosave work as usual. The host's save is the real city.
- **Other players:** autosave is off. A manual save is allowed and creates **your own copy** of the host's city on your
  PC; the session continues in the host's city.
- If the host loads another city or returns to the main menu, the session ends.

## Leaving, disconnects and reconnecting

- **End session** (host) or **Leave session** (others) at the bottom of the panel; click twice to confirm. A player
  who leaves stays in a local copy of the city.
- If a joined player loses the connection, their game pauses the city (autosave off) and **reconnects automatically**
  up to 6 times (after 2, 5, 10, 20, 30 and 30 seconds). Each attempt is a complete join with a fresh copy of the city.
  Changes made while disconnected are not kept.
- When reconnecting fails or the session has ended, the panel offers **Reconnect**, **Return to main menu** or **Stay
  offline (keep a copy)**. An offline copy is single-player; saving it creates your own save.

## Settings

Options → **Skyline Together**, tab **Multiplayer**:

| Group | Setting | Default |
|---|---|---|
| You | Player name | empty (your Steam name) |
| You | Show other players' building previews | on |
| Hosting | Session name, Password | empty |
| Hosting | Max players (2-16, including you) | 8 |
| Hosting | Require the same mods | on |
| Hosting | Require the same asset packs | off |
| Hosting | Let players join with different mods | on |
| Hosting | Allow direct IP connections | off |
| Hosting | Direct connection port (1024-65535) | 27015 |
| Joining | Host's Steam ID or IP:port, Password | empty |
| Session | Host this city / Join / Leave session buttons | – |
| Diagnostics | Show network line | off |
| Diagnostics | Write diagnostics (button) | – |

The Options page also works as a fallback when the Skyline Together panel does not load.

### Mods

With **Require the same mods** on (the default), the host refuses players whose code mods differ, and the joining
player gets a list of mods to enable, disable or update. Mods that only change the player's own screen (translations,
tooltips, icon libraries, overlays, Skyve and similar) may differ: Skyline Together classifies them as local-only,
from a built-in list, from the mod's own declaration and from a scan of each mod's code. A mod counts as affecting the
world when it defines saved components, saved systems, prefabs (also created at runtime) or tools, runs systems outside
UI and rendering, or patches game code outside the UI with Harmony. The diagnostics file explains the decision for
every mod.

Gameplay mods such as Traffic, Move It or Anarchy should be enabled on every PC in the same version. Turning **Require
the same mods** off lets players with different mods join, but the city can then look or behave differently for them.
See [docs/design/MOD-COMPATIBILITY.md](../design/MOD-COMPATIBILITY.md) for the per-mod details.

With **Let players join with different mods** on (the default), a player whose mods differ can decide for themselves:
the refusal screen keeps the list of what to enable, disable or update and adds **Join anyway**, with the warning that
different mods can make the cities drift apart or behave differently. The host's city stays authoritative and
differences are repaired where possible; the host sees who joined with which differences. **Join anyway** never skips
a different Skyline Together build or game version, missing DLC, the password, bans or a full session; see
[Troubleshooting](troubleshooting.md#joining-anyway-with-different-mods).

DLC, asset packs and the mod settings the host pushes are checked when a player joins as well: a player without one of
the host's DLC or content packs is refused; different asset packs and mod settings are told to the joining player (the
host's mod settings apply for the session); see
[Troubleshooting](troubleshooting.md#different-dlc-asset-packs-or-mod-settings).

The UI is available in English and German.
