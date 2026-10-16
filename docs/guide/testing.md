# Testing a build

Skyline Together is in early testing, and most bugs only show up in real sessions. This page describes the two-player
test that comes with the installer (`HOW-TO-TEST.txt` in the setup zip) and the solo test that needs only one PC.
How to report what you find: [Troubleshooting → Collecting logs](troubleshooting.md#collecting-logs).

## Two-player test

### Setup (both players)

1. Close Cities: Skylines II.
2. Run `Install.cmd` from the setup zip. Both players must see the **same build id**.
3. For the first test, turn your Paradox playset off (or use an empty playset) on both PCs, so the mod lists match.
   Disable any other multiplayer mod.
4. Start Steam and log in on both PCs.

### Connect

- **Host:** load a city (a copy of a save you don't mind breaking), click the round Skyline Together button in the
  top-left corner, **Host this city**, then send your Steam ID (copy icon at the top of the panel) or use **Invite
  friends**.
- **Friend:** title screen → **Multiplayer** → **Join a friend** → paste the Steam ID → **Join**. The host's game pauses
  until the friend is in.

### Checklist

Do each step, then both players look at the same spot and compare.

1. Join works; both see each other in the player list and as cursor rings with name tags; chat works (Enter).
2. The host builds a road and zones it, the friend sees it. The friend builds a road and zones it, the host sees it.
3. Buildings grow on the zones and both see the same buildings (wait a minute at speed 3).
4. The friend places a service building (for example a school), the host deletes something; both are identical.
5. **Bulldoze:** each player bulldozes a road, a building and a tree. Nobody crashes, both are identical.
6. **Building previews:** one player starts drawing a road without finishing it; the other sees the outline in that
   player's colour. Same for placing a building, zoning and drawing a district. Esc removes it. Raise the road with
   the elevation steps: the other sees it rise to its height with drop lines to the ground and "+10 m" on the name
   tag; lowered into a tunnel, it is dashed on the surface.
7. **Train line:** build two train stations, rails with a deliberate gap and a train line through both. Both players
   open the line panel: it must show the same "not connected" segment. Close the gap: both must show the line
   connected at the same time.
8. The friend draws a bus line; the host changes its colour or vehicles; identical on both.
9. Taxes, budget or a policy changed by the friend reach the host, and the other way round.
10. **Speed:** the friend presses play or a speed button; the host's game follows.
11. Terraform a hill (both players), paint some ore; identical.
12. **Traffic:** both see cars and pedestrians moving near their camera.
13. The friend leaves and joins again; it works and the city is identical.
14. Later: enable the same gameplay mods on both PCs (Traffic, Move It, Anarchy, ...) and repeat steps 2-8 with them
    (Move It: move a building; Traffic: lane connections).
15. **Different mods:** the friend enables one extra gameplay mod and joins. The refusal screen lists it and offers
    **Join anyway** (host option **Let players join with different mods**, on by default); after clicking it the
    friend joins, both see the "joined with different mods" line in the chat, and steps 2-4 still work. With the option
    off, the screen has no **Join anyway** button.

### If something is wrong

- Both players: **Write diagnostics** in the panel (Network & diagnostics), then run `Collect-Logs.cmd`.
- Send both zips with a short note: which step, what each player saw. Screenshots help.

## Solo test (one PC)

The solo test checks the joining player's side of a session without a second PC. It records exactly what a joining
player would receive from your game, and later replays that recording through the real joining path of the mod, with no
network involved, and compares the result with what your game had.

### Record

1. Load a city.
2. Open the Skyline Together panel. On the **Solo test** tab (or, while hosting, the **Solo test (no second PC
   needed)** foldout) click **Record test session**.
   - If you are not hosting, your game starts a session without network first ("Solo test session: nobody can join;
     the test recorder plays the client.").
   - The test recorder joins like a player: your game saves the city for it, it takes a player slot, and from then on
     it records everything it receives. The panel shows the recording time, size and number of checkpoints.
3. Build and play as usual for 5 to 10 minutes. Verification checkpoints are taken 10 seconds after the recorder joined,
   then every 60 seconds.
4. Click **Stop recording**. A final checkpoint is taken and the recording is saved. Save your city if you want to keep
   it.

Recordings are stored in
`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\ModsData\SkylineTogether\Recordings\<date-time> <city>\`
(`stream.strec`, `snapshot.bin`, `recording.json`).

### Replay and verify

1. Not in a session: in the panel's **Solo test** tab, or on the title screen **Multiplayer** → **Solo test**.
2. Pick a recording (the newest 30 are listed) and a speed (1×, 2×, 4× or max).
3. Click **Replay & verify**. This loads the recorded city in place of the one you are playing, so save first. Nothing
   is sent over the network.
4. At the end the result appears in the panel: **Replay passed**, **Replay found differences** or **Replay
   incomplete**, with the path of the full report.

The report is written next to the recording as `verdict.txt` and `verdict.json`: objects, transport lines, icons and
global values compared with the host at every checkpoint, apply errors and timings. A replay passes only with a complete
recording, a complete final checkpoint, no apply error and no difference at the last checkpoint; differences at earlier
checkpoints that went away are listed as transient. `Collect-Logs.cmd` includes the verdicts of the 3 newest
recordings.

A recording can only be replayed by a build with the same protocol version. Technical details:
[ARCHITECTURE.md](../ARCHITECTURE.md#solo-test-record-and-replay).
