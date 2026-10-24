# FAQ

### How many players can play together?

The host sets **Max players** between 2 and 16, including the host; the default is 8. The design targets 2 to 8
players; larger sessions need a fast upload on the host.

### Where do I get it?

Subscribe to [Skyline Together on Paradox Mods](https://mods.paradoxplaza.com/mods/163578/Windows) (recommended), or
use the setup from the [GitHub releases](https://github.com/ljkx/SkylineTogether/releases). Use one of the two, not
both. See [Install](getting-started.md#install).

### Does every player need to own the game?

Yes. Every player runs their own copy of Cities: Skylines II (Steam version) with Skyline Together installed.

### Do I need to forward ports?

No, not for joining by Steam ID or Steam invite: those connections go through Steam's relay network. Only the optional
direct IP connections need a session password and a UDP port (default 27015) forwarded on the host's router.

### Can I play without Steam?

No. Skyline Together uses Steam for player identity and networking, including direct IP connections. Steam must be
running and logged in.

### Whose city is it?

The host's. The host's game runs the simulation and keeps the real save. The other players' games show the host's city
and send their actions to the host, which checks them and applies them for everyone. When the session ends, the city
continues in the host's save.

### Can other players save?

They can save a copy of the host's city on their own PC (autosave is off for them). That copy is theirs; it does not
change the host's city. See [Saving](getting-started.md#saving).

### Why does the host's game pause when someone joins?

The host's game saves the city and sends it to the joining player. The simulation is paused from that moment until the
player has loaded the city, so the copy they get does not fall behind. A speed the host picks meanwhile is applied
when the player is in.

### Who controls the game speed?

The host's game. Builders can change the speed with the normal buttons; their game sends the request to the host, which
applies it for everyone. Viewers cannot change the speed.

### Can I stop a player from building?

Yes: in the player list, **…** → **Make viewer**. A viewer can only watch and chat. **Make builder** gives the
permissions back. To let new players only watch at first, turn on **New players join as viewers** in the host options.

### Can I ban a player?

Yes. In the player list, **…** → **Ban** (click twice to confirm) removes the player and keeps them out of your sessions:
by Steam ID, and also by address for a player who joined by direct IP. **Kick** only removes them; they may join again. The **Banned players** list in the host options has an **Unban** button per entry. To keep
strangers out from the start, use a password or **Friends and invited only**.

### Can someone help me manage the session?

Yes: **…** → **Make co-host**. A co-host can kick builders and viewers and switch them between builder and viewer. A
co-host cannot remove the host or another co-host, cannot ban and cannot change your settings or password.

### Can I use my mods?

Yes. Skyline Together is built to be played with mods. Traffic, Move It, Anarchy, Better Bulldozer, Tree Controller,
Water Features, Plop the Growables, 529 Tiles, Historical Start, Zoning Toolkit, Line Tool, Find It, Placemaker, Cim
Census and Achievement Fixer have dedicated support: their data, tools and simulation settings are shared by all
players. ExtraLib, Cim Route Highlighter, I18n Everywhere, Extended Tooltip, Asset Icon Library and Unified Icon Library are checked to affect only their own player. Every other code mod is analysed
automatically when a session starts and handled by conservative rules, so its saved data replicates and its simulation
runs on the host. For those mods (and mods that declare it), settings that change the simulation use the host's values
during the session. All players should enable the same gameplay mods;
if they differ, the join screen lists exactly what to enable, disable or update. See
[Mods](getting-started.md#mods) and [MOD-COMPATIBILITY.md](../design/MOD-COMPATIBILITY.md).

### Do all players need the same mods?

With **Require the same mods** on (the default), every player must enable the same code mods in the same versions.
Mods that only change a player's own screen (translations, tooltips, icon libraries, overlays) may differ. The mods
with dedicated support and the automatic analysis are described in the previous answer; see
[MOD-COMPATIBILITY.md](../design/MOD-COMPATIBILITY.md) for details and known limits. Asset mods (buildings, props)
should be the same on every PC, or objects using a missing asset are not created on the PC that lacks it; right after
joining, the player is told which of the host's asset packs are missing (the host can require the same asset packs
instead). A player without one of the host's DLC or content packs cannot join; see
[Troubleshooting](troubleshooting.md#different-dlc-asset-packs-or-mod-settings).

### Can I play with friends who have different mods?

Yes, if the host allows it. With **Let players join with different mods** on (the default), a player refused for
different code mods or asset packs sees the list of differences and can click **Join anyway**. The host's city always
takes priority and differences are fixed where possible, but parts of the city can look or behave differently, and
the cities can drift apart where a mod runs on one PC only. Missing DLC, another game version or another Skyline
Together build still refuse the join. See
[Troubleshooting](troubleshooting.md#joining-anyway-with-different-mods).

The exceptions are mods that save their own data in the city (Zoning Toolkit, Traffic and others): the game crashes
loading a city with data of a mod it does not have, so a player without such a mod of the host's is always refused,
with no **Join anyway**, and the screen lists them under **Enable these mods to join**. Mods only the joining player has
are fine. See [Mods required for this city](troubleshooting.md#mods-required-for-this-city).

### Can I use it together with another multiplayer mod?

No. Disable other multiplayer mods; two at once conflict. While the old "CS2 Multiplayer Mod" is enabled on the host's
or the joining player's PC, the join is refused and the host's panel shows a warning; see
[Two multiplayer mods at once](troubleshooting.md#two-multiplayer-mods-at-once).

### Is the connection encrypted? Is my password sent?

Connections by Steam ID are encrypted by Steam Networking Sockets; optional direct IP connections may be accepted without
Steam authentication and may be unencrypted, which is why they always need the session password (the port stays closed
without one). The session password itself is never sent: the joining player's game
proves that it knows the password with a challenge and response. See [SECURITY.md](../../SECURITY.md) for details and
limits.

### What happens if my connection drops?

A joined player's game pauses the city and reconnects automatically up to 6 times, each time with a fresh copy of the
host's city. Changes made while disconnected are not kept. See
[Leaving, disconnects and reconnecting](getting-started.md#leaving-disconnects-and-reconnecting).

### Something is different on another player's screen. What now?

Small differences are found and repaired automatically in the background. If something stays different, see
[Troubleshooting → desync](troubleshooting.md#something-looks-different-for-another-player-desync) and
[report it](troubleshooting.md#reporting-a-bug).

### Can I check a build without a second PC?

Yes, with the solo test: it records what a joining player would receive and replays it on the same PC the way a
joining player's game would load it. See [testing.md](testing.md#solo-test-one-pc).

### How do I report a bug?

Open an issue on [GitHub](https://github.com/ljkx/SkylineTogether/issues/new/choose) with the diagnostics file and log
of every player. [Reporting a bug](troubleshooting.md#reporting-a-bug) explains where the files are, for both install
routes, and what they contain.

### Which languages does the UI support?

English and German. The activity on name tags ("building", "zoning", …) and some technical details on the join screens
are English only.
