# FAQ

### How many players can play together?

The host sets **Max players** between 2 and 16, including the host; the default is 8. The design targets 2 to 8
players. Sessions with more players are untested.

### Does every player need to own the game?

Yes. Every player runs their own copy of Cities: Skylines II (Steam version) with Skyline Together installed.

### Do I need to forward ports?

No, not for joining by Steam ID or Steam invite: those connections go through Steam's relay network. Only the optional
direct IP connections need a UDP port (default 27015) forwarded on the host's router.

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

Yes: in the player list, **…** → **Make viewer**. A viewer can only chat. **Make builder** gives the permissions back.

### Can I ban a player?

Yes. In the player list, **…** → **Ban** (click twice to confirm) removes the player and keeps them out of your sessions:
by Steam ID, or by address for a player who joined by direct IP without a Steam identity. **Kick** only removes them;
they may join again. The **Banned players** list in the host options has an **Unban** button per entry. To keep
strangers out from the start, use a password or **Friends and invited only**.

### Can someone help me manage the session?

Yes: **…** → **Make co-host**. A co-host can kick builders and viewers and switch them between builder and viewer. A
co-host cannot remove the host or another co-host, cannot ban and cannot change your settings or password.

### Which mods work?

With **Require the same mods** on (the default), every player must enable the same code mods in the same versions.
Mods that only change a player's own screen (translations, tooltips, icon libraries, overlays) may differ. Several
popular gameplay mods (for example Traffic, Move It, Anarchy, Better Bulldozer, Line Tool) have dedicated compatibility
code, and every other mod is analysed automatically (its saved data replicates, its simulation runs on the host); see
[MOD-COMPATIBILITY.md](../design/MOD-COMPATIBILITY.md) for the list and the known limits. Asset mods (buildings, props)
should be the same on every PC, or objects using a missing asset are not created on the PC that lacks it; right after
joining, the player is told which of the host's asset packs are missing (the host can require the same asset packs
instead). A player without one of the host's DLC or content packs cannot join; see
[Troubleshooting](troubleshooting.md#different-dlc-asset-packs-or-mod-settings).

### Can I use it together with another multiplayer mod?

No. Disable other multiplayer mods; two at once conflict.

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

### Something is different on my friend's screen. What now?

Small differences are found and repaired automatically in the background. If something stays different, see
[Troubleshooting → desync](troubleshooting.md#something-looks-different-for-another-player-desync) and report it.

### Can I test without a second PC?

Yes, with the solo test: it records what a joining player would receive and replays it through the joining player's
code path on the same PC. See [testing.md](testing.md#solo-test-one-pc).

### Which languages does the UI support?

English and German. Some texts (player activity on name tags, some system chat lines and the host's refusal details)
are English only.
