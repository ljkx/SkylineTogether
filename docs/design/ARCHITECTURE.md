# Skyline Together: Architecture

| | |
|---|---|
| Status | **Authoritative design, version 1.1**, 2026-10-11. Version 1.1 resolves the design review of version 1.0 and implements the user requirement that transport lines and every other player-visible status indicator are host-authoritative (§4.11). Every finding, its verdict and the sections it changed are listed in the Review log at the end. This document supersedes proposals A, B and C. Changes to it require an explicit decision record (Appendix H). |
| Product | Skyline Together: a from-scratch, host-authoritative co-op mod for Cities: Skylines II (Unity Entities 1.0, Mono, C#). 2 to 8 players, Steam relay and direct connection, cities of 100k+ population, saves of 20 to 300 MB. |
| Goal | Everything that matters is identical for all players, it converges automatically, and the experience is good: instant feedback for your own actions, smooth traffic, the same numbers in every info panel, and no crash when something goes wrong. |
| Inputs | Research digest and reports `docs/research/00`-`15` (cited **R01 §x** ... **R15 §x**). Proposals `docs/design/proposal-A/B/C` (cited **P-A §x**, **P-B §x**, **P-C §x**). The three judges' verdicts (cited **J1** player experience, **J2** engineering risk, **J3** scale and performance). Decompiled game code (cited `File.cs:line`, paths relative to `mod_audit/decomp/Game` unless another assembly is named). |
| Clean room | Designed only from the research reports, the proposals and the decompiled game and engine code. No other co-op project was consulted. |
| Companion | `docs/design/IMPLEMENTATION-PLAN.md` turns this design into modules, contracts and milestones. |
| As built | This is the target design. The code implements a playable subset of it (`src/SkylineTogether/Sync/SyncController.cs` describes which), so names and frame positions differ in places. Sections marked "As built" (§3.1, §4.2, §9.7, §13.5) describe the shipped code. Known differences: the systems and frame positions actually registered are in `src/SkylineTogether/Mod.cs` (not Appendix A); the host accepts up to 120 proposals per second per player with a burst of 240 (not 50); the handshake compares only the protocol version, the mod build, the game version and the code mod list (the data-file digests of §4.2.7 are not populated, and no `gates.json`/`policies.json`/`adapters.json` ships); integrity repair runs per entity (`IntegritySync`) with a bucket rebaseline and a player-confirmed resync above it (`RepairEscalation`, §8.5 as built; the beacon-based `RepairCoordinator` of Core is not used by the game); there is no UDP transport (direct IP connections use Steam Networking Sockets); the build script is `tools/build.sh` and `tools/ApiCheck` does not exist yet. A newcomer's map of the code is [docs/dev/code-map.md](../dev/code-map.md). |

**Markers.** **[V]** = re-verified in the decompiled code while writing this document (the citation is given). **[V-R]** = verified by a research report or a judge (the report is cited). **[E]** = estimate to be replaced by a measurement (§15.3). **[I]** = design inference. **[M]** = must be confirmed in game (listed in §16.2).

**Terms.** *Tick*: one `SimulationSystem.frameIndex` step (60 per second at speed 1, R03 §1.2). *Frame*: one rendered Unity frame. *Host*: the peer that runs the real simulation. *Client*: every other peer. *Epoch*: the replication output of one host frame. *Slot*: a dense network identity (§3.1).

---

## 0. Executive summary

### 0.1 The model

The host runs the complete vanilla simulation. Clients join by loading a snapshot of the host's save. From then on they are **followers**: they never execute the `GameSimulation` phase, their clock follows the host's `frameIndex`, and every gameplay system that could produce or change shared state is switched off. Clients keep running the vanilla *derivation* pipeline (Modification phases, rendering, effects, audio, UI). Geometry, lanes, zone blocks, sub-objects, search trees and render data are therefore rebuilt locally from replicated source data, exactly as the game does after loading a save (R02 §6, R04 §0.7). Derivations whose output is a status the player reads (transport-line connectivity, warning icons, road choices) are the exception: they come from the host (D11, §4.11).

Before the first player is admitted, the host binds every persistent entity of its city to a network identity and records a hash baseline of every replicated value (host session start, §4.4.8), so that joins and replication only ever carry differences. The host streams changes at one fixed capture point per frame. Clients apply them at one fixed apply point per frame, right after `EndFrameBarrier` playback (R11 §6.2). A client action is **predicted** by running the unmodified vanilla tool or UI code locally. The resulting state difference is captured as a **proposal**. The host validates and commits it with the same applier the clients use, and the authoritative result reaches everyone as ordinary replication. Incremental hashes prove convergence. Divergence is repaired at entity granularity, then by bucket, then by domain rebaseline, and only as the last resort by reloading a snapshot.

### 0.2 Design principles

| # | Principle | Consequence |
|---|---|---|
| D1 | **Host is the only writer of shared state.** Clients own only camera, selection, tool previews, UI and per-player preferences. | Clients never compute gameplay values. Their copies are overwritten by host values. |
| D2 | **The save format is the replication contract.** The tracked entity set is the vanilla save query; the wire codec is each type's own `ISerializable.Serialize/Deserialize`, driven through our `IWriter`/`IReader` (P-C P1, J2). | Colossal maintains the formats across patches. Private fields and entity indices hidden in integers (`PathNode`) are handled by the game's own code. Mod components join automatically. |
| D3 | **Derive what the game derives, send what it cannot recompute.** Structure travels as source components; everything the Modification pipeline rebuilds is never sent (R02 §6, R13 §12). | Bandwidth is spent on simulation outcomes and edits, not on geometry. |
| D4 | **Bandwidth is a per-peer budget; congestion becomes staleness, never backlog.** One host shadow, per-peer latest-value-wins dirty bits, a token bucket fed by Steam's measured rate (P-B, J3). | A slow relay link degrades only that player's freshness of unobserved state. |
| D5 | **Freshness follows attention.** What a player can see (camera, open panel, infoview, selection) is promoted; everything else converges by a bounded rolling refresh (P-B §4.7.4, P-A §3.8). | Large cities do not cost more bandwidth; they only converge unobserved state more slowly. |
| D6 | **Fail closed, fail visible, fail into resync.** Clients never run `GameSimulation`; every non-public access is a named probe with a tested fallback; every apply step decodes into scratch memory before it touches a chunk, and structural apply faults freeze the follower and lead to a resync, never to a kept corrupt replica (P-C P3/P5/P6, J2). | Game patches and unknown mods degrade into repair traffic or a reload, not into silent divergence or a crash. |
| D7 | **Idempotent upserts keyed by slot.** Join backlog, catch-up, repair and proposal acknowledgement use one apply path (P-B D6, P-C P2). An upsert reconciles in place only when generation, family and prefab match; otherwise it replaces the local entity (§4.7.3). | Overlaps and duplicates are harmless. The whole path is unit-testable. |
| D8 | **Main thread only for ECS; everything else on worker threads.** ECS safety checks are compiled out of the player (R06 §7, R08 §1). | No race corrupts the world silently. |
| D9 | **Read at frame start, tag at frame end.** The host records keys and tags just before `PrepareCleanUpSystem`, and reads component values at the next frame start right after `EndFrameBarrier` completed the simulation jobs (P-C §3.6, J3). | No forced completion of in-flight jobs on the host. Host main-thread time matters twice, because `SimulationSystem` caps ticks by measured frame cost (R03 §1.2). |
| D10 | **No Harmony by default.** Public APIs, enable flags, reflection probes with fallbacks and P/Invoke for Steam lanes. Optional patches are listed with a justification and stay off unless a measurement demands them (Appendix B). | Smallest blast radius for game patches and other mods. `0Harmony.dll` is not shipped in v1. |
| D11 | **Every status indicator a player sees is host data.** Warning icons, line connectivity, stop and vehicle counts, road, power and water connection states come from the host. A kept client system may derive presentation (curves, render buffers, clusters) from host data, but never a status of its own; client-local producers of persistent icons are filtered out (§4.10.4, §4.11). | Two players can never see "not connected" on one screen and "connected" on the other. |

### 0.3 Key decisions

| # | Decision | Origin | Why |
|---|---|---|---|
| K1 | Start from Proposal B's scale-first engine (per-peer scheduler, attention, latest-value-wins, tiered scan cadences, dense slots). | P-B (judge average 7.2/10, highest) | Only proposal that bounds bandwidth and CPU everywhere (J3). |
| K2 | Wire codec = the game's `ISerializable` through `NetWriter`/`NetReader`; a raw-byte xxHash is used only as a change-detection prefilter. Values are written into chunks in place, per `(chunk, type)`, never through per-entity `EntityManager.SetComponentData`. | P-C §3.4 + J3 apply requirement | Patch-robust, handles private fields and hidden entity refs (J2); chunk-level writes avoid the per-call dependency completion of `SetComponentData` (J3). |
| K3 | Clients never execute `GameSimulation`. After a load, vanilla `SimulationSystem` is kept alive (paused) until its own first post-load update has cleared its loading flag, then a replacement clock driver takes over and calls an explicit cosmetic allow-list that writes no replicated state; every other `GameSimulation` system is also disabled. | P-C §5.1 + P-A/P-B belt-and-braces; review | Systems added by patches or mods cannot run on clients (fail closed, J2); the speed setter stays writable (§6.1). |
| K4 | Existence of **all** movers (vehicles, creatures, parked cars, train members) is global; motion is interest-managed; movers leaving interest are **pinned** at their last pose. | P-A §3.9 (J1) | Every reference resolves, no pop-in, no 64-tick snap-backs, no runaway extrapolation. Fixes the B and C fatal flaws on out-of-interest movers. |
| K5 | Reverse buffers whose runtime maintainers are off on clients (`Renter`, `HouseholdCitizen`, `HouseholdAnimal`, building `Student`, `RouteWaypoint`, `RouteSegment`, vehicle `Controller`, `GroupCreature`) are replicated **as data** and hashed as sets. | P-A §3.2, J2 fatal flaw | They would otherwise go permanently stale on clients (`RenterSystem` only runs in the Deserialize phase, `SystemOrder.cs:815` [V]). |
| K6 | Traffic lights: replicate the 6-byte node `TrafficLights`, derive lane signals and pole states on clients with the public static `TrafficLightSystem.UpdateLaneSignal` / `UpdateTrafficLightState` / `UpdateMoveableBridge` (`TrafficLightSystem.cs:782, 878, 977` [V]). In-view junctions are on a fast path. | P-B §6.3 (J1, J3) | Signals animate correctly at a few hundred bytes per second. Fixes C's fatal flaw. |
| K7 | Inspection closures are sampled on the host at multiples of 256 ticks, applied when the client clock reaches the sample tick, followed by `SelectedInfoUISystem.RequestUpdate()` (`SelectedInfoUISystem.cs:343` [V]); the host also forces its own panel refresh at each sample tick. | P-A §3.10, P-B §4.6.7; review | Simulation values (V/A) in every player's panel come from the same host tick at every periodic refresh, the host's included; structure values apply on arrival (§4.7.9). |
| K8 | Client actions are predicted with vanilla code and proposed as state differences. Capture is **scope-limited**: entities the local apply tagged `Applied`, plus every `Temp` original (and its owned sub-elements) recorded before the apply that afterwards changed, because only `ApplyNetSystem` tags updated originals `Applied` (§5.2). Direct-write mod tools use a whitelist of player-authorable types; everything else is a leak and is repaired, never proposed. Incoming host values for pairs covered by a pending proposal are held until its result. | P-B §5, P-C §4, P-A §4 (J2 graft); review | Covers vanilla tools, ~45 UI actions and direct-write mods without patching handlers. Fixes C's action-window flaw. |
| K9 | Every UI action is predicted locally (policies, taxes, budget, fees, loan, dev tree, names, colours); rejection rolls back with a toast that names the reason. | P-A §4.4, J1 | Instant feedback for the most common actions. Fixes B's flaw of destroying local `Modify`/`Unlock` events. |
| K10 | Vanilla time controls and save/exit menu actions are interposed through the binding registry (`IBindingRegistry.RemoveBinding`, `IBindingRegistry.cs:7` [V]) when the probe succeeds; otherwise speed changes are detected from `selectedSpeed` diffs and leaving is detected from lifecycle events. | P-A §10.4 + P-B §5.3 fallback | Buttons keep working and never look broken (J1); a game update that renames bindings only loses comfort, not correctness (J2). |
| K11 | Identity = dense 24-bit `NetSlot` + 8-bit generation in side tables indexed by `Entity.Index`, persisted by a mod `NetIdSystem : IDefaultSerializable` in every save. Prefab and type ids are ranks in sorted `PrefabID` / `StableTypeHash` lists. | P-B §3, R12 §1.4 | O(1) arrays, 3-byte varints, zero structural cost, saves stay loadable without the mod. |
| K12 | Join = cached host snapshot shared by joiners within 120 s, an epoch ring holding only P0 content, a per-joiner dirty set since the snapshot epoch, and verification that starts only after one full host scan cycle past that epoch ("Live (verifying)"). The joiner's load skips the vanilla load-time rewriters of replicated values (`ResetBuildOrderSystem`, `CitizenSystem`, `RandomLocalizationSystem`), so the loaded world equals the host's snapshot bit for bit. | P-B §7 + P-C §6.5; review | Bounded catch-up for 300 MB cities; no false verification failures (fixes A's fatal flaw); no resync loop from compacted build orders (§7.4). |
| K13 | Repair ladder: entity, bucket, domain rebaseline over the bulk lane, then snapshot reload; a per-(entity, type) tolerance list prevents repair storms. Structural apply exceptions go straight to a resync; value-codec exceptions quarantine the type and rebaseline its domain. | P-C §7.3, P-B §8.5, J2, J3 | Reloads become rare; a corrupt structural batch is never kept. |
| K14 | Transport = Steam Networking Sockets P2P (relay and ICE) and direct IP over the same API, three lanes via custom P/Invoke with a second-connection fallback; network I/O and encoding off the main thread. | R06 §5, all proposals | Encrypted, authenticated, NAT traversal without port forwarding. |
| K15 | Remote presence (cursors, tool ghosts, name tags, ping markers) is drawn with `OverlayRenderSystem.GetBuffer()` (`OverlayRenderSystem.cs:603` [V]), never as ECS `Temp` entities. | P-A §4.7 (J1) | Players see each other work without touching the shared `Temp` namespace (R02 §10.2). |
| K16 | Code = pure .NET Core (netstandard2.0, no game references, unit-tested against a fake world and a network simulator) + thin game layer (one assembly) + hand-written UI module (`.mjs`, no bundler). Algorithmic logic of the game layer (canonical encoding, family rules, scan scheduling, apply planning, gate planning, load handover) lives in Core behind small seams; game types only forward to `EntityManager`. | all proposals; review | Most logic is tested before anyone starts the game (hard constraint 3); the FakeWorld executes the same apply programs as the game. |
| K17 | **Host session start**: before the lobby opens, the host binds every tracked entity (from the save's slot block, then a sweep) and primes the hash and value shadows without emitting records. | review | Joins and the first snapshot never carry a second copy of the city; replication starts from an exact baseline (§4.4.8). |
| K18 | **Delivery is never lossy on the reliable lane**: an application-level outbox per peer keeps Steam's pending bytes inside a window; large transactions are split into epoch parts and applied atomically; per-record identities (`RESULT`/`BIND` origin, type-set keyframes, motion assignment generations) make every record decodable for exactly the peer it concerns. | review | No silent gap, no session close on a legitimate large edit (§4.5-4.6, §4.9.3, §9). |
| K19 | **Transport lines are host data end to end**: stops and their `Connected` links, waypoint lanes, segment paths and path status, line values and vehicle lists replicate from the host; `RoutePathSystem`, `RoutePathReadySystem`, `WaypointConnectionSystem` and `ConnectionWarningSystem` are off on clients (the first and the third only run inside a guarded route-tool preview window); an icon authority filter drops every client-local persistent icon before `IconCommandSystem` plays it back; a UI-level line digest proves equality. | user requirement; review of the code paths (§4.11) | Fixes the "line not connected for one player only" failure class by construction, not by hoping that local derivation agrees. |

### 0.4 What "identical" means

| Sync class | Content | Guarantee |
|---|---|---|
| **Exact** | Persistent structure (nets, objects, areas, routes, zoning, water sources), transport-line state (stops and their `Connected` links, waypoint lanes, segment paths and path status, §4.11), city singletons (money, loan, XP, milestones, dev points), policies, budgets, taxes, fees, names, prefab unlock state, every notification icon (status indicators, §4.10.4), mod components, existence of every agent and mover | Bit-identical canonical encoding after apply (icons: identical per-owner sets). Verified by digests and repaired on mismatch. |
| **Converged** | Simulation values that change continuously (building condition and efficiency, citizen and household state, company stock, line vehicle lists and passenger counts, statistics, demand, cell maps, utility flow status in the infoviews, traffic-light state) | Identical within a bounded latency: in view p95 ≤ 5 s, elsewhere p95 ≤ 120 s at the default budget [E]. Verified by digests. |
| **Time-aligned** | Simulation values (V/A) shown in the info panel of the selected or followed entity | Identical values sampled at the same host tick on every client, and on the host at each periodic refresh (K7). Structure values in the same panel apply on arrival, and a client panel may also refresh when the applier tags the selected entity; it then still shows the last sample's simulation values. The host's own tag-triggered refreshes show its live values until the next sample tick (§4.7.9). |
| **Visual-equivalent** | Motion of movers inside the interest region (quantised frames, about 1/64 m), water surface (client simulation plus correction tiles), snow and wetness, derived children whose generation is not peer-stable (node lanes, node secondary objects, R13 §2.4, §3.2) | Indistinguishable on screen or periodically corrected. Excluded from exact audits. |
| **Per player by design** | Camera, selection, tool state and previews, infoviews, photo mode, radio channel, chirp likes, `HiddenRoute`, UI highlights | Never replicated. |

### 0.5 Service levels (targets at the default budget, 100k city) [E]

| What | Target |
|---|---|
| Host edit visible on a client | 1 host frame + ½ RTT; derived geometry rebuilt in the same client frame |
| Client edit visible to the actor | immediately (prediction); confirmed after 1 RTT + 1 host frame |
| Client edit visible to other clients | RTT/2 + 1 host frame + RTT/2 |
| Agent existence (spawn/despawn of any citizen, vehicle, creature) | ½ RTT after the host frame |
| Motion near the camera | same trajectories, shown `D` ticks behind the host, `D` adaptive, typically 0-40 ticks (§4.9.6) |
| Info panel of the selected entity | identical simulation numbers from the same host sample tick (K7) |
| Status indicators (warning icons, line connectivity, stop counts) | the host's state, ½ RTT after the host frame; never computed on a client (§4.11) |
| Building state in view / infoview data in view | p95 ≤ 5 s |
| Unobserved agent and building state | p95 ≤ 120 s (bounded by staleness promotion, §4.8.4) |
| Terrain after a stroke settles | bit-exact about 0.3 s later |
| Join of a 100k city (100-200 MB save) | 1-4 min over relay at 1-5 MB/s, plus load time; the host stalls once per snapshot (announced) |
| Host session start ("Preparing session") | binding and shadow priming, time-sliced while the session is paused: 5-20 s for a 100k city, once per hosting [E] |

### 0.6 Headline budgets (100k city, per client) [E]

| Stream | Priority | Speed 1 | Speed 4 (default caps) |
|---|---|---|---|
| Motion frames (2-3k movers in interest, ~12 B per frame incl. one keyframe in four, far field at half rate) | P2 | 75-135 KB/s | ≤ 150 KB/s, quality scaled |
| Mover and agent existence (global, all movers incl. creatures) | P0 | 8-20 KB/s | 30-80 KB/s |
| Attention state (in-view buildings, closure, infoview topics, lights) | P1 | 5-20 KB/s | 10-40 KB/s |
| Rolling refresh (everything else dirty) | P3 | 15-40 KB/s | 15-40 KB/s (wall-clock paced) |
| System state, statistics, budget, demand | P1 | 1-3 KB/s | 2-6 KB/s |
| Icons, events, chirps, journal, unlocks, names | P0 | 0.5-2 KB/s | 1-6 KB/s |
| Cell maps, water correction tiles (default "low") | P3 / bulk | 3-10 KB/s | 3-10 KB/s |
| Structure (tool applies, zone spawns) | P0 | bursts of 0.2-2 KB per edit | bursts |
| Clock, view reports, beacons, presence | P0/U | 3-6 KB/s | 3-6 KB/s |
| **Total** | | **≈ 120-250 KB/s** | **≤ 300 KB/s default per-client cap** |

Host upload is the sum over clients (star topology), capped by a host total (default 1.5 MB/s) that is split fairly. Host main thread: ≤ 3 ms per frame for replication at 60 fps [E]; client: ≤ 3 ms apply budget plus ≤ 1 ms motion writing [E]. Host memory: hash shadow ~8 B per (slot, type), value shadow 30-60 MB, epoch ring 128 MB (configurable, disk spill) [E].

---

## 1. How the judges' findings were resolved

### 1.1 Fatal flaws and their resolution

| Flaw (judge) | Resolution in this design | Where |
|---|---|---|
| A: post-load validation compares epoch-E digests with the loaded snapshot; V-class values lag, so almost every join fails (J1) | Post-load checks only placeholders, slot-binding coverage and per-family counts within ±1 %. Value verification starts per domain only after the host completes one full scan cycle past E. The client is playable as "Live (verifying)" meanwhile. | §7.6 |
| B and C: globally existing movers that leave interest keep cycling stale ring frames or extrapolate without bound; Transform never refreshed (J1) | Pinning: on interest exit the JIT writer writes the last pose into all 4 `TransformFrame` slots with zero velocity and keeps `Transform`/`Moving` coherent. Out-of-interest movers receive low-rate pose refreshes (re-pin). Extrapolation is capped at 32 ticks, then pinned. | §4.9.5 |
| C: traffic signals never animate on clients (J1) | Node `TrafficLights` on a fast path for in-view junctions; client derivation through the public static helpers after `TrafficLightInitializationSystem` (Mod4B, `SystemOrder.cs:182` [V]). | §4.9.9 |
| C: one shared World stream rate-limited to the slowest client (J1, J3) | Per-peer dirty bits and per-peer scheduling; only P0 content is encoded once and shared. A slow peer gets fewer, fresher updates. If a peer cannot even sustain P0, the host is told and may cap the session speed. | §4.5, §4.8 |
| B: pedestrians and animals exist only inside interest; refs dangle, rogue-write loop, pop-in (J1) | All movers have global existence (K4). No out-of-scope NetRef is needed. | §4.9.1 |
| B: policy toggles and dev-tree purchases destroyed locally, visibly lag (J1) | All UI actions predicted; host results overwrite; rejections roll back with a toast. | §5.3, §5.4 |
| C: vanilla speed buttons silently overwritten; no presence; inspection unspecified (J1) | Binding interposition turns the vanilla buttons into speed requests (fallback: diff detection); presence overlay; time-aligned inspection closures. | §5.7, §11.4, §4.7.9 |
| B and C: `Renter`, `HouseholdCitizen`, `HouseholdAnimal`, building `Student` treated as derived, go stale, never detected (J2) | Replicated as data and hashed as sets (K5). An audit rule covers every data-carrying `IEmptySerializable` type. | §4.2.4 |
| B: excludes `Game.Areas.Surface` (empty tag) instead of `Game.Objects.Surface` (5-byte wetness component) (J2) | The exclusion names `Game.Objects.Surface` (`Game.Objects/Surface.cs:6-16` [V]); `Game.Areas.Surface` is an `IEmptySerializable` tag (`Game.Areas/Surface.cs:8` [V]) and replicates as presence. | §4.2.5 |
| C: action-window gating turns `GroundHeightSystem` re-snaps into proposals (J2) | Scope-limited capture (K8); `GroundHeightSystem` is off on clients with its queue drained. | §5.2, §6.4 |
| A: reflective unsubscription of other mods' lifecycle delegates; binding replacement; clock reflection failure refuses join (J2) | No delegate unsubscription by default (join-time mutators are disabled by enable flag or tolerated under settings parity and repaired). Binding interposition has a fallback. Clock falls back to the public `SimulationSystem.Deserialize` (`SimulationSystem.cs:140-146` [V]). | §7.4, §5.7, §6.1 |
| B: an apply exception marks the class suspect and continues with a partially applied batch (J2) | Every value is decoded into scratch memory and copied only after a complete, length-exact decode; spawns become visible to vanilla systems (`Created`) only after the whole transaction succeeded. Structural exceptions → Faulted → the follower freezes → resync. Value-codec exceptions → type quarantined + domain rebaseline; a second fault in 10 min → resync. | §4.7.3-4.7.4, §12.2 |
| All: capture before `SaveGameSystem` runs before mod `UpdateAt(MainLoop)` systems, whose tags are wiped unseen (J2); C finds no entities created without `Created` (J2) | The tag sweep is registered immediately before `PrepareCleanUpSystem` (two-type registration places it next to its parent, `UpdateSystem.cs:173-181, 382-441` [V]); a separate snapshot-bind pass runs before `SaveGameSystem` only in snapshot frames. An order-version discovery pass and a liveness sweep find entities created or destroyed without tags. | §4.4.1, §4.4.6, §7.3 |
| C: `GroundHeightSystem` and `CityServiceBudgetSystem` on clients cause repair bursts (J3) | Both off on clients; heights arrive from the host; budget arrays are fed by a probe with a "keep it on" fallback. | §6.4 |
| C: per-entity `EntityManager.Get/SetComponentData` apply; all encoding on the main thread (J3) | Chunk-level in-place deserialisation through RW dynamic handles; value shadow lets workers encode per-peer records. | §4.7.4, §4.5 |
| A: repair ladder jumps to a full reload on 1 % mismatch; no domain step (J3) | Domain rebaseline step before reload; reload only for structural mismatch > 5 %, apply exceptions, protocol gaps. | §8.5 |
| A: heavy bulk defaults, never-reused 32-bit NetIds, ~400 MB host memory (J3) | Water "low" default (one depth tile per 16 s plus hot tiles), no periodic snow/wind snapshots, dense reusable slots, P0-only ring with a cap. | §4.10, §3.1, §15 |
| A and B: host motion sampling costed as if Burst; data captured at end of MainLoop (J3) | Motion sampled at frame start after `EndFrameBarrier`, pointer-based column reads, union-of-interest prefilter before touching frame buffers, budgeted far field. | §4.9.3 |

### 1.2 Ideas grafted from the verdicts

Grafted and where they live: pinning (§4.9.5); time-aligned inspection (§4.7.9); attention model with infoview topics (§4.8.3); traffic-light derivation (§4.9.9); per-peer latest-value-wins scheduling with AIMD (§4.8); short-drop resume and hash-diff resync (§7.8); held payloads and bind-before-create (§5.5); canonical hashing with tolerance list (§8); verification after a full scan cycle (§7.6); binding interposition (§5.7); presence overlay (§11.4); speed re-assertion and HostBusy (§6.2, §7.2); predicted UI actions (§5.3); K channel for state on derived entities (§4.10.7); effect-relevant mover state on change (§4.9.4); adaptive clock lag (§4.9.6); parked service vehicles spawned only by the host (§5.4.3); synthesized progression events (§4.10.3); actionable diagnostics (§13); echo suppression (§5.5.3); `ISerializable` codec with owned full-tag context (§4.3); self-test runner (§13.4); fail-closed clock (§6.1); probes with fallbacks (Appendix B); split capture timing (§4.4); dense slots and ranks (§3); sparse and versioned codecs (§4.6.4); one idempotent apply path (§4.7); reverse-buffer audit rule (§4.2.4); height-quantised hashing (§8.1); record/replay harness and shadow client (§13.5); write auditor and leak tracker (§13.3); unbound-entity discovery (§4.4.6); measurement build first (§15.3, IMPLEMENTATION-PLAN M2); warm join by block delta (§7.10, later milestone).

---

## 2. Authority model per state category

The single rule behind every row: **the host owns every value that the simulation or a player can change; a client owns only its camera, UI, tool previews and per-player preferences.** Per-player state is never replicated; it only influences the host's priorities through the view report (§4.8.3).

Column legend. **Class** = replication class (§4.2.2): **S** structure, **G** global singleton, **V** simulation value on a structure entity, **A** agent, **A-low** transient agent, **M** mover, **K** keyed state on a derived entity, **CH** dedicated channel, **L** local only. **Capture** = how the host finds changes (§4.4): **EDC** event-driven from game tags, **SDC** scan-driven, **POLL** poller, **SPEC** special. **Prio** = delivery priority (§4.8.1). **Client systems**: KEEP (vanilla runs), OFF (gated, §6.3), OFF+feed (gated, host output written instead).

### 2.1 Persistent structure

| Category | Authoritative data on the wire | Class / capture / prio | Derived locally on clients | Client systems | Refs |
|---|---|---|---|---|---|
| Net nodes and edges (incl. building sub-nets) | `PrefabRef`, `Node`, `Edge`, `Curve`, `Game.Net.Elevation`, `Upgraded`, `PseudoRandomSeed`, `BuildOrder` (host-assigned, §5.4.3; exact on joiners because vanilla `ResetBuildOrderSystem` is skipped in session loads, §7.4), `Road` (its flags carry the half-align bits that drive zone blocks, R13 §4.1; only the tool-set flags are player-authorable, the traffic-flow floats and `LightsOff` are simulation output, §3.8), `Owner`, `Fixed`, `Standalone`/`Orphan`/`LocalConnect`/`OutsideConnection` presence, ordered `ConnectedNode`, `NetCustomColorVariation`, mod components on edges (`ZoningInfo`, Traffic data) | S / EDC / P0, plus host touch set | `EdgeGeometry`, `NodeGeometry`, `Composition`, `ConnectedEdge`, `SubNet`, lanes, zone blocks, secondary lanes and objects, flow-graph topology, search trees, labels | `Generate*`, references, `CompositionSelect`, `Net.GeometrySystem`, `LaneSystem` (or the Traffic mod's replacement), `BlockSystem` KEEP | R02 §6-§9, R13 §2, §4 |
| Net simulation values | `Road` flow vectors and `LightsOff` flag, `NetCondition`, net `Pollution`, `LandValue`, `Density`, `ServiceCoverage` and `ResourceAvailability` buffers, node `TrafficLights` | V / SDC (lights: fast path) / P1 in view or infoview topic, else P3 | infoview colours, lane signals and signal poles (§4.9.9), street-light pole states (§6.4) | `TrafficLightSystem` and `StreetLightSystem` never run (GameSimulation); `TrafficLightInitializationSystem` KEEP | R04 §4.1, R15 §9.3 |
| Lanes | nothing per lane; a portable lane key when replicated data points at a lane (§3.7) | derived | everything | KEEP | R13 §2 |
| Aggregates (street identity) | `Aggregate`, `PrefabRef`, ordered `AggregateElement` (edge refs), `RandomLocalizationIndex` | S / EDC / P0 | membership regenerated by `AggregateSystem`, then forced to the host list by `AggregateReconcileSystem` (§4.7.8) | `AggregateSystem` KEEP | R13 §7.1 |
| Top-level objects: buildings, props, trees, decals, upgrades, extensions, transport stops, area-spawned objects | `PrefabRef`, `Transform`, `Game.Objects.Elevation`, `Owner`, `Attached`, `Relative`, `PseudoRandomSeed`, `Building` (`m_RoadEdge`/`m_CurvePosition` are deferred fields applied by `RoadConnectionReconcileSystem`, §4.2.5, §4.11.5), `Extension`, `ServiceUpgrade`, `CustomMeshColor` (+enabled bit), `Native`/`Overridden` presence, `Policy` + modifier buffers, `RandomLocalizationIndex`, `UnderConstruction` (host `m_Speed`), `Tree` + `Decoration`/`LumberResource` enabled bits, `Recent`, mod components (`TransformRecord`, `PreventOverride`, `PloppedBuilding`, ...) | S / EDC / P0 | prefab sub-objects, secondary objects, sub-lanes, `ConnectedBuilding` and building connection lanes (`RoadConnectionSystem`, whose road choice is reconciled to the host's), `CurrentDistrict`, `InstalledUpgrade`, `SpawnLocationElement`, rendering data | `SubObjectSystem`, `SecondaryObjectSystem` KEEP; `RoadConnectionSystem` KEEP (its icons are filtered, its road choice reconciled) | R02 §9.1, R04 §4.3, R13 §3, §8 |
| Building simulation state | `BuildingCondition`, `Abandoned`/`Condemned`/`Destroyed`/`Damaged`/`OnFire` presence and data, `Efficiency` buffer, service facility components (incl. `Battery`, `WorkProvider`), `ElectricityConsumer`, `WaterConsumer`, `GarbageProducer`, `CrimeProducer`, `MailProducer`, `Occupant`/`Patient` buffers, building `Resources`, `PropertyOnMarket`, `Building` street-light flags (`StreetLightsOff`, `Illuminated`); **`Renter` and building `Student` buffers as data** (§4.2.4), re-captured together with the forward reference that changed them (§4.2.6) | presence changes S / EDC / P0; values V / SDC / P1 in view, else P3 | infoview colours, effects (with client `EffectsUpdated`, §4.7.7), street-light pole states | all building AIs never run; `BuildingStateEfficiencySystem`, `CityServiceEfficiencySystem`, `BatteryInitializeSystem`, `CityServiceWorkplaceInitializeSystem`, `RemovedSystem` OFF | R03 §6.9, R11 §2.8 |
| Prefab sub-objects and secondary objects | the derived set itself is not sent; host-owned state on them travels by key: tree state, `Decoration`/`LumberResource` bits, player overrides (Move It, Anarchy), keyed deletions (Better Bulldozer records), state of station stops (transport stops are spawned as building sub-objects, `SubObjectSystem.cs:2549` [V]): `TransportStop`, `BoardingVehicle`, `TaxiStand`, `WaitingPassengers`, `ConnectedRoute` | K / EDC on derived entities + SDC / P0 for player edits and stop links, P1/P3 for stop values, P3 for growth | the children themselves | KEEP | R13 §3, P-A §3.11.8, §4.11 |
| Areas: lots, districts, surfaces, spaces, map tiles, extractor and storage areas | `PrefabRef`, `Area`, `Game.Areas.Node` buffer, `Owner`, `PseudoRandomSeed` (lots), `District.m_OptionMask`, district `Policy` + `DistrictModifier`, map-tile ownership (absence of `Native`), `Extractor`/`Storage`/`WoodResource`/`MapFeatureElement` amounts | S / EDC / P0; amounts V / SDC / P3 | `Geometry`, `Triangle`, `Expand`, `SubArea`, labels, `CurrentDistrict`/`BorderDistrict` of other entities | `Areas.GeometrySystem` KEEP | R04 §4.4, R13 §8 |
| Zone cells | `Cell.m_Zone` per block cell as zone prefab rank, RLE, keyed by block key (§3.7); `Overridden` cells omitted | K / EDC (blocks tagged `Created`/`Updated`/`Applied`) / P0, applied once the client block exists | block geometry (`BlockSystem`), cell `m_State`/`m_Height`, `VacantLot`, overlap resolution (`CellCheckSystem`) | KEEP | R13 §4.2-4.3 |
| Routes and transport lines (§4.11) | `Route` (incl. `RouteFlags.Complete`), `Game.Routes.Color`, `RouteNumber`, `VehicleModel` buffer, `Policy`, `RouteModifier`, **ordered `RouteWaypoint`/`RouteSegment` as data** (§4.2.4); waypoints: `Waypoint`, `Position`, `Connected`, `AccessLane`, `RouteLane`, `Owner`; segments: `Segment`, `Owner`, **path state `PathTargets`, `PathInformation`, `PathElement`** (lanes as derived keys); stops: **`ConnectedRoute` as data** | S / EDC + path-result trigger (§4.4.2 step 3a) / P0; line values `TransportLine`, `RouteInfo`, `WaitingPassengers`, `VehicleTiming`, `TransportStop`, `BoardingVehicle`, **`RouteVehicle` as data** V / SDC + co-capture / P1 under the Transport topic or an open line panel, else P3 | `CurveElement`, route render buffers and search tree (from host path state and a synthesised `PathUpdated`), `SubRoute`, path-target ready positions | `Routes.InitializeSystem`, `RoutePathReadySystem` (replaced by `RouteReadyBridgeSystem`) OFF; `WaypointConnectionSystem`, `RoutePathSystem` OFF except inside a guarded route-tool preview window (§4.11.4); `SegmentCurveSystem`, `Routes.SearchSystem`, `Routes.ReferencesSystem` KEEP | R13 §9, §4.11 |
| Gate bypass routes | the routes themselves: none (created on every peer by `Buildings.InitializeSystem`); their `HiddenRoute` presence and segment path state: K channel | derived + K | both peers; path state and visibility from the host | `RoutePathSystem`/`RoutePathReadySystem` OFF (above) | P-C §2.3 rule 13, §4.11.3 |
| Water sources | `Game.Simulation.WaterSourceData` + `Transform` (no `PrefabRef` for mod sources), Water Features components | S / EDC / P0 | — | `WaterSourceInitializeSystem` KEEP (host values re-applied at the end of the creation frame, §4.7.8) | R07 §6.3, R09 §2.9 |
| Flow graphs (electricity, water pipes) | topology derived; flow status (`ElectricityFlowEdge`/`WaterPipeEdge` flow, capacity, `Bottleneck`/`BeyondBottleneck`/`Disconnected` flags) is simulation output, sent by flow key to every peer that has the Electricity or Water topic active (§4.11.5) | derived + CH flow-status channel | graph topology | KEEP (flow simulation is GameSimulation, off) | R04 §4.10 |

### 2.2 City and simulation state

| Category | Authoritative data | Class / capture / prio | Client side | Refs |
|---|---|---|---|---|
| Citizens | `Citizen`, `HouseholdMember`, `Worker`, `Game.Citizens.Student`, `CurrentBuilding`, `CurrentTransport`, `TravelPurpose`, `HealthProblem`, `Criminal`, `CrimeVictim`, age tags, `CarKeeper`/`BicycleOwner` enabled bits, `Followed`, `GroupMember` | existence A / EDC / P0; state A / SDC 60 s / P1 when inspected or under an infoview topic, else P3 | all initializers OFF; names from `PseudoRandomSeed`/household; the citizen `PrefabRef` stays the host's because the load-time re-roll (`CitizenSystem`) is skipped in session loads (§7.4) | R03 §6.1, R11 §2.11 |
| Households | `Household`, `PropertyRenter` (changes promoted to P0, §4.7.8), `Resources`, `HouseholdNeed`, `TaxPayer`, `MovingAway`, `PropertySeeker` enabled bit, tourist/commuter/homeless markers, `HouseholdPet`; **`HouseholdCitizen` and `HouseholdAnimal` as data**, re-captured in the transaction of every member spawn, despawn or move (§4.2.6) | as citizens | `HouseholdInitializeSystem`, `HouseholdAndCitizenRemoveSystem`, `HouseholdPetRemoveSystem` OFF (the host sends every cascaded despawn) | R11 §2.11, J2 |
| Companies | `CompanyData` (brand as prefab rank), `PropertyRenter`, `WorkProvider`, `Employee`, `Resources`, `TradeCost`, `Profitability`, `CompanyStatisticData`, `ServiceAvailable`, storage and transport components, `CurrentTrading` | as citizens; profitability infoview topic | `CompanyInitializeSystem`, `StorageInitializeSystem` OFF | R04 §4.7 |
| Service requests, seekers, meetings | `ServiceRequest` + typed request entities, `JobSeeker`, `SchoolSeeker`, `LookingForPartner`, `CoordinatedMeeting` | A-low / EDC + SDC / P3 only; references to them tolerate staleness (§4.7.5) | `ServiceRequestSystem`, `MeetingInitializeSystem`, `AddMeetingSystem` OFF | R15 §8.3 |
| Money, loans, credit | City entity: `PlayerMoney` (private `m_Money` handled by its own `Serialize`, `Game.City/PlayerMoney.cs:43-51` [V]), `Loan`, `Creditworthiness`, `Population`, `Tourism`, `DangerLevel`, `SpecializationBonus` | G / hot poll every frame, change-driven ≤ 4 Hz / P0 | client-local writes (predicted tool cost, loan prediction) are overwritten by host values; `CitySystem` KEEP (caches money for UI and rich presence, R14 §1.2) | R03 §6.4 |
| Budget and fees | `ServiceBudgetData` buffer (budget entity), City `ServiceFee` buffer | G / hot poll / P0 | edits are predicted UI actions (§5.3) | R10 §3.3 |
| Budget output arrays | `CityServiceBudgetSystem` income (14) and expense (15) arrays, totals, per-service maps (not `ISerializable`, R11 §5.2) | CH / POLL ≤ 1 Hz / P1 | `CityServiceBudgetSystem` OFF+feed through probe; fallback KEEP (§6.4) | P-B §4.3.3 |
| Taxes | `TaxSystem.m_TaxRates` (92 ints) via `TaxSystem.Serialize` | CH / POLL on change / P1 | `Deserialize` with the owned full-tag context after `Readers.Complete()` | R11 §5.2-5.3 |
| Demand, counts, trade, production and utility statistics | the systems' own `Serialize` output (allow-list of entries verified side-effect-free and round-trip tested, §4.10.1) | CH / POLL (demand ≤ 1 Hz, others ≤ 0.25 Hz) / P1 | UI reads deserialised fields | R11 §5.2, R14 §10 |
| City statistics | `CityStatistic` buffers on statistic entities; `CityStatisticsSystem` sample count and last sample frame via `Deserialize` only (never `PostDeserialize`) | V / SDC at sample cadence / P1 under the statistics topic, else P3 | statistics UI | R11 §5.2, R14 §6.2 |
| Progression | City `XP`, `MilestoneLevel`, `DevTreePoints`; prefab `Locked` bits, `PlacedSignatureBuildingData`, `UnlockRequirementData.m_Progress`; `MilestoneSystem` cached UI fields; XP messages | G + CH (prefab state) / POLL / P0 | client synthesises `Event+Unlock` and `Event+MilestoneReachedEvent` (§4.10.3); all reward producers OFF; `UnlockSystem` KEEP | R14 §1-3 |
| Time and date | clock stream (`frameIndex`, speed, phase), `TimeData` singleton | CH clock (unreliable 10-30 Hz) + G | date, time of day, lighting, sun computed locally from `frameIndex` (`TimeSystem`, `PlanetarySystem`, `LightingSystem`) | R03 §1.3, §6.7 |
| Weather and climate | climate prefab (snapshot), weather state and `OverridableProperty` overrides through a custom `ClimateSystem` codec, weather phenomena as event entities | CH / POLL ≤ 0.2 Hz + on change / P1 | climate curves sampled locally (pure function of date) | R03 §6.7, R11 §3 |
| Events: fire, crime, accidents, disasters, floods, health and calendar events | persistent `Game.Events.*` entities (`Event` + type + `Duration` + `TargetElement`), target components (`OnFire`, `AccidentSite`, `InvolvedInAccident`, `Flooded`, `InDanger`, `Damaged`, `Destroyed`, `Criminal`), journal entities | existence and target presence S / EDC / P0; values V / SDC / P3 | one-frame command events (`Damage`, `Ignite`, ...) are never replicated; their consumers are OFF | R11 §2.4, §2.7 |
| Event journal | journal entities and buffers | as events | client companion refreshes `EventJournalSystem.eventJournal` and invokes its callbacks (§4.10.6) | R14 §6.1 |
| Notifications (icons): every warning and problem indicator on buildings, nets, lanes, lines, waypoints, vehicles and citizens | per-owner icon set (prefab rank, priority, layer, flags, target, location); owners that are derived (lanes, station stops) are addressed by derived keys | CH / EDC of `Icon` entities / P0 | replication-owned `IconCommandBuffer` from `IconCommandSystem.CreateCommandBuffer()` (`IconCommandSystem.cs:727` [V]); `IconCommandSystem` KEEP (mandatory); the **icon authority filter** drops every client-local `Add`/`Remove` of a persistent icon before playback (§4.10.4); Marker, `Temp` and transaction-layer icons stay local | R14 §4, §4.11 |
| Chirps, life path, radio | `Chirp` (+`RandomLocalizationIndex`, `Liked` bit masked), `LifePathEvent`, `LifePathEntry`, `Followed`; radio tags (later milestone) | A / EDC / P0; likes P3 | `CreateChirpSystem`, `TriggerSystem`, `LifePathEventSystem`, `RandomLocalizationInitializeSystem` OFF; likes per player | R14 §5 |

### 2.3 Moving agents

| Category | Authoritative data | Class / capture / prio | Client mechanism | Refs |
|---|---|---|---|---|
| Vehicles (cars, trucks, buses, trams, trains incl. layout members, aircraft, watercraft, bicycles) | **global existence**: spawn bundle = archetype kind (moving/stopped/controller), prefab rank, host `UpdateFrame`, `PseudoRandomSeed`, role component, `Car`/`Train`/... flags, forward refs (`Owner`, `PersonalCar.m_Keeper`, `Target`, `CurrentRoute`, `LayoutElement`, `Controller`), lane keys, `Unspawned`/`TripSource` as on host, `Transform`, `Moving` | M / EDC / P0 | spawned from the prefab archetype with host `UpdateFrame` + `Applied`; vanilla reference systems rebuild `OwnedVehicle` and `LaneObject`; `Passenger`, `RouteVehicle` and `GuestVehicle` are data-replicated, because simulation systems edit them after spawn while the kept reference systems react only to `Created`/`Deleted` (§4.2.4) | R15 §4 |
| Vehicle discrete state | effect-relevant: `Car.m_Flags`, role `m_State`, `Aircraft` flags, `Stopped`/`ParkedCar` presence; lane keys of current lanes; cargo and passengers | M / motion sampler (in interest) + SDC 60 s / P1 in interest, else P3 | client adds `EffectsUpdated` (every `LayoutElement` member for trains) | R15 §3.5 |
| Motion | `TransformFrame` per 16-tick step (position, velocity, rotation, flags, human state) | M / per-tick sampler / P2 unreliable, interest only | staging ring + JIT slot writer + extrapolation; **pinning** on interest exit; low-rate pose refresh out of interest | R15 §2.6, §7 |
| Parked cars and parked trains | `ParkedCar`/`ParkedTrain` (lane key + curve position), `Transform` | M / EDC / P0 | global, persistent; moving↔parked swaps use the vanilla type sets | R15 §4.2, §5 |
| Creatures (residents, pets, wildlife) | **global existence** (fixes J1's flaw against interest-scoped creatures): `Resident`/`Pet`, `CurrentVehicle`, `Human.m_Flags`, `HumanCurrentLane` lane key and flags, `GroupMember`; frames as vehicles | M / EDC / P0; motion P2 | as vehicles; `SubObjectsUpdated` events synthesised for activity props | R15 §2.6 step 7 |
| Paths and navigation | `PathOwner`, `PathElement`, navigation lanes | on demand (inspection of a line or of a vehicle with route display) | sent only to the requesting peer, never hashed | R15 §5, §8.4 |
| Traffic lights | node `TrafficLights` (6 bytes, `Game.Net/TrafficLights.cs:6-19` [V]) | V / fast path for in-view junctions / P1; else P3 | lane `LaneSignal` and pole `TrafficLight` derived (§4.9.9) | P-B §6.3 |

### 2.4 Environment and fields

| Category | Authoritative data | Class / capture / prio | Client mechanism | Refs |
|---|---|---|---|---|
| Terrain heightmap | `TerrainSystem.m_Heightmap` (4096² R16) | CH / brush capture + settle detection / bulk | client strokes are predicted locally and proposed; host sends dirty 64×64 tiles after a stroke settles; clients `Graphics.CopyTexture` + `OnAreaChanged` + `WaterSystem.TerrainWillChangeFromBrush` + `UpdateMinMax` (all public) | R07 §5.2 |
| Terrain material paint | user splatmap tiles | CH / bulk, low priority | same tile protocol | R07 §5.3 |
| Water surface | `WaterSystem.WaterTexture` (2048² RGBA32F) | CH / round-robin + hot tiles / bulk, quality setting (default "low") | client runs the GPU water simulation for visuals with mirrored sources and parameters; host correction tiles; all water-driven gameplay is host-only | R07 §6.7 |
| Sea level, water simulation parameters | `WaterSystem` public parameters, Water Features globals | CH / POLL on change / P1 | public setters | R07 §6.2, R09 §2.9 |
| Snow | `SnowSystem` textures | L (cosmetic; arrives with the join snapshot) | client simulates (tick-driven, fixed step) | R07 §7 |
| Wind | `WindSimulationSystem` cells | L | client simulates for visuals | R11 §3 |
| Object wetness, snow, dirt | `Game.Objects.Surface` (5 bytes) | L (excluded; documented fidelity exception) | `WetnessSystem`, `DirtynessSystem` on the cosmetic allow-list | R11 §3, P-A §3.11.9 |
| Street lights (on/off) | the owners' replicated flags: `Road.LightsOff`, `Building.StreetLightsOff`/`Illuminated`, `Watercraft.LightsOff` | V (part of those components) | pole `StreetLight` states derived from the owner flags (`StreetLightDerive`, §6.4); `StreetLightSystem` is **not** cosmetic because it writes those replicated flags from client-local inputs (`StreetLightSystem.cs:83-151` [V]) | R11 §5 |
| Cell maps | air, ground and noise pollution, groundwater, soil water, natural resources, land value, attractiveness, telecom, population and availability grids, traffic ambience | CH / per-map hash on its update tick / P3 (P1 under the matching infoview topic) | written with `GetMap(readOnly: false)` + `deps.Complete()` + `AddWriter`; zone ambience recomputed locally (`SpawnableAmbienceSystem` + `ZoneAmbienceSystem` on the allow-list, R11 §3) | R07 §8 |

### 2.5 Mod state, names, policies, options, per-player state

| Category | Authoritative data | Class / capture / prio | Client mechanism | Refs |
|---|---|---|---|---|
| Mod components | every `ISerializable`/`IEmptySerializable` component from a non-Game assembly (in the save query automatically) | by family default / EDC + SDC | generic codec; entity references remapped by `NetWriter`; enabled bits replicated | R09 §1.1, §5 |
| Mod system state | `IDefaultSerializable` mod systems (e.g. Water Features) | CH / per-adapter codec, opt-in after round-trip verification | their own `Deserialize` | R09 §2.9 |
| Mod settings that affect simulation or validation | `ModSetting` properties declared by adapters | session settings / P0 | host values written in memory on clients before the join load and restored on leave | R09 §5.8 |
| Mod prefab mutations | `UnlockRequirement`, `MilestoneData`, `PlaceableObjectData` cost, added prefabs | invariant | "same mods, same versions, same settings" enforced by the handshake | R09 §5.9 |
| Custom names | `NameSystem` names (keyed by `Entity` in vanilla) | CH / POLL of `NameSystem.Serialize` output (`NameSystem.cs:549` [V]) / P0 | `NameSystem.SetCustomName` (`NameSystem.cs:214` [V]) in the UI-update apply slot | R13 §7.2 |
| City name | `CityConfigurationSystem.cityName` | CH / POLL / P0 | setter | R10 §3.5 |
| Generated names | `RandomLocalizationIndex` + `PseudoRandomSeed` | entity data | written at spawn; re-roll prevented because the initializer is OFF; strings localised per player | R13 §7.3 |
| Policies and modifiers | `Policy` buffers (City, districts, buildings, routes), `CityModifier`/`DistrictModifier`/`BuildingModifier`/`RouteModifier`, option masks | S / EDC / P0 | state only; the host never sends `Modify` events; client `Modify` exists only as a local prediction (§5.3) | R10 §4, R11 §9 |
| City options, game mode | theme, left-hand traffic, disasters, unlimited money, unlock-all, unlock-map-tiles; game mode prefab | join snapshot | `overrideLoadedOptions = false`, `GameModeSystem.overrideMode` = host mode (§7.4) | R12 §4.2 |
| Speed and pause | session speed | clock stream | host authority; clients send speed requests (§5.7) | R05 §9 |
| Followed citizens | `Followed` + `LifePathEntry` (shared, 50-entry city limit) | A / P0 | follow is a predicted UI action | R14 §5.4 |
| Mesh colour palette | `MeshColorPalette` entity (saved with the city) | G | edits are predicted UI actions | R10 §3.5 |
| Camera, selection, follow target, infoview, tool state and previews, panels, photo mode, cinematic camera, radio channel, toasts, key bindings | — | L | never replicated; the host camera from the snapshot is replaced by the client's own camera after join | R10 §6, R12 §4.2 |
| `HiddenRoute`, chirp `Liked` | — | L overlay | masked out of replication and hashing | R10 §6.1, R14 §5.4 |
| Tutorials | — | L, disabled for every peer during a session | `SharedSettings.gameplay.showTutorials` forced false in memory; the intro list never activates, so `TutorialSystem.tutorialIntroUnfinished` stays false and `MilestoneSystem` is not blocked (`MilestoneSystem.cs:72-75`, `TutorialSystem.cs:231-233` [V]) | R14 §8 |
| Achievements | — | L | already inert for any modded city (`usedMods` gate) | R14 §7 |

---

## 3. Entity identity

Entity handles are machine-local: indices are reused immediately and versions are per process (R08 §10.1, R12 §1). Every replicated entity therefore gets a network identity, kept in side tables, never in an ECS component (a data component would force a copy of every chunk and is missing from 331 baked-archetype creation sites, R12 §1.2).

### 3.1 NetSlot, generation and NetId

- **`NetSlot`**: a dense unsigned index, 24 bits (1 ... 16 777 215; 0 = none). It is what references carry on the wire (varint: 3 bytes up to 2²¹ slots).
- **Generation**: 8 bits, incremented whenever a slot is reused. It travels only in `SPAWN`/`DESPAWN`/`BIND` records, motion slot assignments and repair messages, to reject stale operations. A `SPAWN` whose generation, family or prefab differs from the client's binding of that slot replaces the local entity instead of reconciling it (§4.7.3), so a missed despawn followed by reuse can never produce a hybrid entity.
- **`NetId`** = `(gen << 24) | slot` (`uint`), used for persistence and diagnostics.
- **Allocation (host only)**: lowest free slot first. A freed slot is reused only after (a) every connected peer has acknowledged the epoch that carried its `DESPAWN` and (b) the epoch-log tail has passed that epoch (§4.5.4). No in-flight record, repair response or unreliable motion datagram can then address a reused slot, so references need no generation (P-B §3.1). Peers that were not connected (resume beyond the log tail, hash resync) are covered by the generation check above.
- **As built (w2/ids)**: `NetIdAllocator` (Core) implements the allocation above. The "epoch" of the reuse condition is a *release frame*: one host frame, sealed at frame start by `ApplyAckBook` with a per-peer watermark of the reliable `Epoch` messages queued so far; clients acknowledge the number of `Epoch` messages they applied (`MessageKind.ApplyAck` = 60, sent in order with their proposals, `ApplyAckTracker`). A released id is freed once every peer applied its release frame, no join is in progress (join barrier) and at least 3 s passed; tables stay bounded and session start compacts the ids. Release frames are unrelated to the *ledger epochs* of the conflict check (`SyncRecordKind.Epoch`, `AuthoredLedger`, a proposal's `BaseEpoch`, §5.4.1).
- **Spawn state (host only)**: each slot carries a `spawnEmitted` bit. Binding a slot leaves it false; the capture flush emits `SPAWN` for every bound entity whose bit is false and then sets it (§4.4.2). Session-start binding (§4.4.8) sets it true, because every peer receives those entities through its snapshot.
- **Provisional ids (`LocalId`)**: a client-local counter used only inside proposals for entities the client created by prediction. They never enter the world or the side tables of other peers (§5.5.1). On the wire a `LocalId` or proposal sequence number is always qualified by the originating peer (`BIND` and `RESULT` carry `origin`, §4.6.3), because every client counts from 1.

Dense slots keep varints short and let every peer use flat arrays instead of hash maps (R08 §10.3). This also fixes J3's objection to never-reused 32-bit ids.

### 3.2 Tracked set and family classification

**Candidates.** The tracked set is the game's own definition of persistent state: the save query of `SerializerSystem` (`Any(PrefabRef, LoadedIndex, explicit singleton and graph types, every non-Game serializable type)`, `None(Temp, Deleted, NetCompositionData, EffectInstance, LivePath)`; `Game.Serialization/SerializerSystem.cs:171-214`, R04 §2.1, R09 §1.1). Mod components and mod-only entities are covered automatically, including entities without `PrefabRef` such as Water Features sources (R09 §5.1).

**Families.** The rule table below is implemented once, in Core (`FamilyRules.Classify` over a presence mask of the marker types plus a few per-entity facts, unit-tested rule by rule). The game layer's `FamilyClassifier` only builds the presence mask per archetype and caches the result, so classification costs one lookup per chunk; only the "owned object" rule needs a per-entity fact (membership in the owner's `SubObject` buffer). First match wins:

| # | Rule (component presence) | Family | Treatment |
|---|---|---|---|
| 1 | `Temp`, `LivePath`, `PrefabRef.m_Prefab == Entity.Null` (Move It control points, R09 §2.3), or a type an adapter lists as local (`MIT_*`) | ToolLocal | never replicated |
| 2 | `PrefabData` | Prefab | identity by rank only (§3.5); state through the prefab-state channel (§4.10.3) |
| 3 | `Game.Net.Lane` | Lane | derived; portable key when referenced (§3.7) |
| 4 | `Game.Zones.Block` | ZoneBlock | derived; cells through the K channel (§4.10.7) |
| 5 | `Game.Notifications.Icon` | Icon | icon channel (§4.10.4) |
| 6 | `Game.Objects.Object` + `Secondary` | SecondaryObject | derived; keyed state (§4.10.7) |
| 7 | `Object` + `Owner`, listed in the owner's `SubObject` buffer, and none of `ServiceUpgrade`, `Building`, `Secondary` (the criterion `SubObjectSystem` itself uses, `Game.Objects/SubObjectSystem.cs:1172-1195`, [V-R] P-A) | PrefabSubObject | derived; keyed state |
| 8 | `ElectricityFlowNode/Edge`, `WaterPipeNode/Edge` (the `*NodeConnection`, `*ValveConnection` and `*BuildingConnection` components live on net nodes, edges and buildings and are no markers; they are excluded from structure records) | FlowGraph | derived (rebuilt by the graph systems in Modification phases); flow values by flow key (§4.11.5) |
| 9 | `Route` + `Owner` whose prefab is a gate bypass path (`Game.Buildings/InitializeSystem.cs:154-168`, [V-R] P-A) | OwnedRoute | derived on every peer; `HiddenRoute` presence and segment path state through the K channel (§4.11.3) |
| 10 | `Game.Net.Node` / `Game.Net.Edge` | NetNode / NetEdge | S |
| 11 | `Aggregate` | Aggregate | S (+ client reconcile) |
| 12 | `Vehicle` or `Creature` | Mover | M |
| 13 | any other `Object` (top-level, upgrade, extension, sub-building, area-spawned) | Object | S |
| 14 | `Game.Areas.Area` | Area | S |
| 15 | `Route`; `Waypoint`/`Segment` whose owner route is tracked | Route / Waypoint / Segment | S |
| 16 | `Citizen`, `Household`, `HouseholdPet`, `CompanyData` | Agent | A |
| 17 | `ServiceRequest` (+ typed requests), `JobSeeker`, `SchoolSeeker`, `LookingForPartner`, `CoordinatedMeeting` | TransientAgent | A-low |
| 18 | `Game.Events.Event` (persistent event entities), `EventJournalEntry`, `Chirp`, `LifePathEvent` | Event | existence S (events) or A (chirps, journal, life path); values V |
| 19 | `CityStatistic` | Statistic | V |
| 20 | `City`, `ServiceBudgetData`, `TimeData`, `MeshColorPalette` | Singleton | G |
| 21 | `WaterSourceData` (may lack `PrefabRef`) | WaterSource | S |
| 22 | matched only through a non-Game serializable type (e.g. Better Bulldozer `OwnerRecord` entities) | ModEntity | S |
| 23 | anything else in the save query | Generic | S, low priority (patch-tolerant default: unknown persistent entities are replicated, not dropped) |

The diagnostics dump prints per-rule entity counts. Discovery mode (§13.3) reports save-query entities that a client created outside the applier and outside its own predictions; by construction those belong to a locally derived family that the rule table must list.

### 3.3 Side tables

`SlotTable` (Core, pure arrays, unit-tested; wrapped by the game layer as `EntityMap`):

| Array | Indexed by | Purpose | Size at 1 M slots and 1.5 M entity indices [E] |
|---|---|---|---|
| `slotByEntityIndex`, `versionByEntityIndex` | `Entity.Index` | entity → slot, version-checked | 12 MB |
| `entityIndexBySlot`, `entityVersionBySlot` | slot | slot → entity | 8 MB |
| `genBySlot`, `familyBySlot`, `flagsBySlot` | slot | generation, family, flags (`spawnEmitted`) | 3 MB |
| `prefabBySlot` | slot | prefab rank at binding (upsert check, §4.7.3) | 4 MB |
| `typeSetBySlot` | slot | current replicated type set (§3.6) | 4 MB |
| `revisionBySlot` | slot | authored revision (§3.8) | 2 MB |

All lookups are O(1) with a version check: an `Entity` whose version differs from the recorded one is a different entity. Tables grow in 8192-index blocks, matching the entity allocator (R08 §10.1). Only the main thread mutates them, and on the host only inside the capture window (§4.5.5); workers see immutable per-frame copies of what they need.

**Resolution contract (slot → chunk).** `EntityManager.GetStorageInfo` does not compare versions: it returns whatever entity currently owns the index (`EntityDataAccess.cs:1453-1461` → `EntityComponentStore.GetEntityInChunk`, which checks only the allocated bit, `EntityComponentStore.cs:692-708` [V]), while `EntityManager.Exists` compares the version (`EntityComponentStore.cs:660-672` [V]). Bound entities can be destroyed locally on a client (sub-elements of a deleted owner, rollbacks, kept mod deletion systems), and entity indices are reused at once. Every slot → chunk resolution therefore (1) calls `EntityManager.Exists(entity)` first, (2) checks `chunk.Has(ref dynamicHandle)` before every column access, because a missing type yields a null column (`ArchetypeChunk.cs:730-738` [V]), and (3) on failure unbinds the slot (if the entity is gone) and parks or drops the record; it never writes. A per-frame client liveness pass unbinds bound entities tagged `Deleted`, and a budgeted `Exists` sweep (about 20k slots per frame) catches the rest (§4.7.3).

### 3.4 Persistence and the join map

`NetIdSystem : GameSystemBase, IDefaultSerializable, IPostDeserialize` (R12 §1.4) is serialised into every save: join snapshots, host autosaves and manual saves.

- **Serialize** writes `formatVersion`, a session-lineage id, the allocator high-water mark, then a `NativeList<Entity>` and a `NativeList<uint>` of bound pairs. Entities go through the save's own entity table (`BinaryWriter.Write(Entity)` writes the save index, `BinaryWriter.cs:128-141`), so unsaved entities come out as `-1`. The block is never empty (empty blocks throw, `ComponentSystemSerializer.cs:36-41`, R12 §1.4).
- **Deserialize** runs inside the deserializer's exclusive transaction, so it touches only its own containers, never `EntityManager` (R12 §10). The stock reader hands it entities that are already remapped to the freshly loaded ones (`BinaryReader.cs:146-157`), so no private table or Harmony is needed.
- **PostDeserialize** drops dead entries, builds the side tables and counts bindings per family for the post-load check (§7.6).
- **Without the mod**, the block's type name does not resolve and vanilla skips it (`ObsoleteSystemSerializer`, R12 §6.3).
- **Version tolerance**: the game assembly's `AssemblyVersion` is pinned (1.0.0.0) and the system carries a `[FormerlySerializedAs]` self-alias, because type identity is the assembly-qualified name (R12 §1.5) [M].
- **Registration**: the system is created as a world system in `OnLoad`, registered `UpdateAt` in MainLoop (its `OnUpdate` does nothing), `SerializerSystem.SetDirty()` is called so the system library picks it up, and `PostDeserialize<NetIdSystem>` is registered `UpdateAfter` in the Deserialize phase.
- The mod never creates entities whose only persistent components are its own: they would survive `ClearSystem` and leak into the next load (R12 §3).

The same block lets the host resume a session from its own save with identical slots (resume points, §7.10). A save made outside a session (the normal case the first time a city is hosted) has no block; host session start then binds the whole city once before any peer is admitted (§4.4.8).

### 3.5 Prefab identity: ranks, not tables

After prefabs are loaded, every peer enumerates all prefab entities (`PrefabData`, including disabled ones), resolves each with `PrefabSystem.TryGetPrefab` and `GetPrefabID()` (type name, prefab name, hash; `Game.Prefabs/PrefabID.cs:15-36`, R04 §1.3), and sorts the list ordinally. **`PrefabRank`** = position in that list.

- **Handshake**: a 64-bit xxHash digest of the sorted list. Equal digests mean equal ranks, so no table is transferred (P-B §3.5, P-C §2.6). A mismatch refuses the join by default (hard constraint 6) and the host sends its list for a readable difference report (§7.9).
- **Duplicates** (first registration wins, `PrefabSystem.cs:151-176`) and hashless prefabs created in code are part of the digest input and are listed in the report, so a load-order difference shows up as a mismatch instead of silent shadowing.
- **Prefabs added mid-session** (playset change) invalidate the ranks and trigger a session reset (§7.8).
- **Machine-local indices are never sent**: `PrefabData.m_Index`, `ZoneType.m_Index` (zones travel as zone prefab ranks, `ZoneSystem.cs:176-179`, R04 §4.5), `PathNode` owner indices, and `UpdateFrame` of non-movers.

### 3.6 Type identity, type sets and archetypes

- **`TypeCatalog`** is built at session start, after every mod has registered its types (mod types are appended to `TypeManager` at load, R08 §5). It walks the serializer library exactly like `ComponentSerializerLibrary` (R01 §4.1) and classifies each type: `Serializable` (data through `ISerializable`), `EmptyTag` (zero-size `IEmptySerializable`), `EmptyData` (data-carrying `IEmptySerializable`, e.g. geometry and reverse buffers), with flags for buffer, shared, enableable and `ISerializeAsEnabled` (whose enabled bit is ignored, R01 §2).
- **`TypeRank`** = index in the list sorted by `TypeInfo.StableTypeHash` (R08 §5). The **catalog fingerprint** hashes `(StableTypeHash, SizeInChunk, ElementSize, kind, codec kind, policy version)`. A mismatch refuses the join. `StableTypeHash` ignores assembly versions (R12 §7), so mod versions are compared separately (§7.9).
- **Replicated signature** of an entity = the sorted set of its replicated type ranks: `Serializable` ∪ `EmptyTag` ∪ presence of `EmptyData` types. Non-serializable runtime types and lifecycle tags (`Created`, `Updated`, `Deleted`, `Applied`, `BatchesUpdated`, `EffectsUpdated`, `PathfindUpdated`, `Highlighted`, `Hidden`) never count, so tool hover noise and per-frame tag churn never produce structural records (R08 §12.5, R09 §5.6).
- **`TypeSetId`**: signatures are dictionary-coded per session in one session-global, append-only registry (never per peer). A `TYPESET` definition record precedes first use in the shared P0 stream (§4.6.3), so the epoch log and short-drop replays contain it. Every peer that starts from a snapshot (joiners and snapshot resyncs) first receives a **type-set keyframe**, the whole registry as of sending, before log epoch E is applied (§7.1 step 12). A later definition of an id the client already knows must be identical. An unknown id in a record is a `Faulted` condition that leads to a resync, never a session-closing decode error (§12.2).
- **Archetypes for creation**: the family's prefab-baked archetype (`ObjectData.m_Archetype`, `NetData` node/edge archetypes, `MovingObjectData.m_StoppedArchetype`, train controller archetypes, `AreaData.m_Archetype`, `RouteData` archetypes, `AggregateNetData.m_Archetype`, agent `ArchetypeData.m_Archetype`; R04 §1.1, R15 §4.1), plus every replicated type of the record's type set that the baked archetype lacks. Families without prefab archetypes (water sources, mod entities, generic) build the archetype from the type set. Prefab archetypes contain `Created` and `Updated` (e.g. `Game.Prefabs/BuildingPrefab.cs`, `MovingObjectPrefab.cs`); the applier creates from a **staging variant without `Created`/`Updated`** and adds them in one batch after the whole transaction succeeded (§4.7.3). Archetypes are cached per `(prefab rank, type set)`.

### 3.7 Derived entities: keys instead of slots

Derived families are regenerated on every peer and never get slots. When replicated data points at one, the writer encodes a **derived key** inside the `NetRef` (§4.3.3). The reader resolves it against the client's own children through per-owner caches that are invalidated when the owner is `Updated` or `Deleted` (R13 §2.5). Floats in keys are quantised to 1/64 m because Burst float results may differ between CPUs (R13 §13).

| Kind | Key | Resolution | Used by |
|---|---|---|---|
| Lane | owner slot, lane prefab rank, master/slave flags, start/middle/end `PathNode` with the owner index re-encoded as a slot (`PathNode` packs `owner.Index << 32`, `Game.Pathfind/PathNode.cs:20-49`) | exact match; node lanes ignore the middle counter and fall back to the nearest curve end points within ε, reversed match allowed (R13 §2.5) | `ParkedCar`, current-lane fields of movers in interest, Traffic mod buffers that point at lanes |
| Prefab sub-object | owner slot, prefab rank, `Aligned.m_SubObjectIndex` or position quantised in owner space | mirrors `FindOldSubObject` (R13 §3.1) | K channel, `Connected` stops |
| Secondary object | owner slot, prefab rank, quantised position | geometric | K channel (trees, poles) |
| Zone block | owner edge slot, side, `m_Position` quantised to 1/64 m, `m_Size` | exact, then nearest within 1 m on the same side (R13 §4.3) | K channel (cells) |
| Owned route | owner slot, prefab rank | unique per owner | references from routes |
| Owned-route element | owned-route key, element kind (waypoint or segment), index in `RouteWaypoint`/`RouteSegment` | exact | K channel: path state of gate-bypass segments (§4.11.3) |
| Icon | owner `NetRef` (slot or derived key), notification prefab rank, target `NetRef` | `IconCommandSystem.FindIcon` identity (R14 §4.2) | icon channel |
| Flow edge | slots of the owners of start and end flow nodes, role | by owner | flow-status channel under the Electricity/Water topics (§4.11.5) |

A key that does not resolve decodes as `Entity.Null`, is retried for 2 s, then reported to the repair coordinator, which puts the (entity, type) on the tolerance list after two failed repairs (§8.5). Types whose policy marks a reference as **presence-safe** decode an unresolved key to a local stand-in instead of `Entity.Null`, because the vanilla UI interprets null-ness (segment path end points, §4.11.3). Lane references are resolved only to live lane entities, because `Vehicles.ReferencesSystem` indexes the lane's `Owner` and the owner's `SubLane` without checks once the lane is a `CarLane` (`Game.Vehicles/ReferencesSystem.cs:61-69` [V]). `Entity.Null` is safe there: the job is guarded by `m_CarLaneData.HasComponent(value.m_Lane)` [V].

**Re-resolution after local regeneration.** Keys are resolved when a record is applied, but a client regenerates derived children on its own schedule: a station rebuild can replace the stop sub-object a waypoint's `Connected` points at, and every local lane rebuild replaces lane entities that `RouteLane`, `PathElement` or `ParkedCar` reference, while the host may keep its entity and never re-send the referencing value. The client therefore keeps a **received-value cache** (Core `ReceivedValueCache`): the last received canonical bytes of every replicated pair whose bytes contained a derived key when the reader decoded them, plus every pair of a type with the policy flag `cacheReceived` (the line-state types the route guard restores, §4.11.4), and an index from each referenced derived owner to the dependent pairs (Core `DerivedRefIndex`). When a derived owner's children are regenerated (its cache invalidation, above), the dependent pairs are re-applied from the cached bytes in `ClientEndOfFrameSystem`, after every Modification phase, so they resolve against the new children (§4.7.5). Movers in motion interest are excluded (their lane fields are refreshed with their discrete state).

### 3.8 Revisions

The host keeps `revisionBySlot` (16 bits). It counts **authored** changes only, so a proposal is rejected for a real conflicting edit and never because the simulation touched the same component:

- **Bumps**: (a) a commit of a proposal that writes the entity; (b) an EDC capture (`Updated`/`Applied` from the tag sweep, including the host player's own tools) in which the entity's **authorable projection** changed; (c) a signature change (`STRUCT`), despawn or slot reuse.
- **Never bumps**: scan-driven capture (SDC), hot-set comparisons, motion sampling. Simulation jobs write silently (for example `TrafficFlowSystem` and `StreetLightSystem` write `Road`, `Game.Simulation/StreetLightSystem.cs:292` [V]); those changes are replicated but are not edits.
- **Authorable projection**: per type, the policy table (§4.2.7) names the player-authorable fields. For most source types the whole type is authorable. Mixed types have an `authorableMask` function: `Road` → `m_Flags` without `LightsOff` (the vanilla net apply copies only `m_Flags` from the tool's temp, `Game.Tools/ApplyNetSystem.cs:629-647` [V]; the four traffic-flow vectors and `LightsOff` are simulation output); `Building` → the historical flag. The host stores a 32-bit hash of the projection for those pairs and bumps the revision only when it changes.
- Proposals carry the base revision of every original they edit or delete; the host rejects a proposal with a stale revision as a conflict (§5.6). Zone overlays carry a per-block-key revision. Economy setters (taxes, budget, fees, loan) are last-writer-wins and carry no revision.
- Test (core-replication): a simulation-only change to an edge never makes a proposal based on that edge stale.

---

## 4. Replication engine

### 4.1 Overview and data flow

```
HOST main thread (ECS only)                                    HOST worker threads (Core)
────────────────────────────────────────────────────────────    ───────────────────────────────────────────
MainLoop  EndFrameBarrier playback (completes sim jobs)
  HostCaptureFlushSystem   [capture window: the only time engine state is mutated]
     join last frame's workers, drain the engine command queue
     EDC: last frame's sweep → SPAWN/DESPAWN/STRUCT/VALUE/TOUCH
     motion sampler (ticks of last LateUpdate)                     ┌► P0 encoder (once, shared by all peers)
     SDC slice + hot set → hash shadow, value shadow, dirty bits ──┤  per-peer scheduler P1/P2/P3 (reads shadow)
     channels / pollers → channel records                          │  motion datagrams per peer
     ingress hook: validate + commit proposals, repairs            │  compression, epoch parts, epoch log
     close epoch, hand frame token to workers  ────────────────────┘  per-peer outbox (window on Steam)
  [ToolSystem ... ModificationSystem ... UIUpdate: HostCommandSystem]    ▼
  SnapshotBindSystem       (snapshot frames only, before SaveGameSystem) network thread: Steam sockets
  TagSweepSystem           keys + tag bits, Unlock flag, forward refs of deleted agents (before PrepareCleanUp)
LateUpdate  SpeedAuthoritySystem → SimulationSystem (0-8 ticks)

CLIENT network thread / workers                                 CLIENT main thread (ECS only)
────────────────────────────────────────────────────────────    ───────────────────────────────────────────
receive → decode → decompress → epoch parts                    MainLoop  EndFrameBarrier playback
epoch merge (ApplyPlanMerger) → ApplyPlan queue ──────────────►   ClientApplySystem (capture stage A, apply program
motion staging rings ─────────────────┐                               from ApplyPlanner, street-light derive,
                                      │                               synthesised events incl. route PathUpdated,
                                      │                               gate re-assert)
                                      │                          [ToolSystem + ToolCaptureApply (stage B)]
                                      │                          ToolCaptureResult (stage C, first in Mod1)
                                      │                          [Modification: RouteReadyBridge, AggregateReconcile,
                                      │                           RoadConnectionReconcile, TrafficLightDerive,
                                      │                           IconAuthorityFilter (before IconCommandSystem)]
                                      │                          SpeedMirrorSystem (before PreRenderSystem)
                                      └─────────────────────────► MotionWriterSystem (PreCulling, JIT slots)
                                                                  RouteGuardSystem (after PathfindResultSystem;
                                                                      only inside route-tool preview windows)
                                                                  ClientEndOfFrameSystem (spawn-payload re-apply,
                                                                      derived-reference re-resolution, liveness
                                                                      unbind, hooks: stage C2/D, rolling
                                                                      verification; before PrepareCleanUp)
                                                                LateUpdate  ClientClockSystem (load handover, then
                                                                      follower clock; SimulationSystem off)
```

The same codecs, records and applier carry host→client state, client→host proposals, and host-side commits of proposals (P-A P2, P-C P4).

### 4.2 What is replicated

#### 4.2.1 Type catalog

The catalog (§3.6) lists every serializable component, buffer and shared component type with its kind. A per-type **policy** (§4.2.7) then decides class, codec, merge rule, tag rule, attention topics and whether players may author it. Unknown types (a new game patch, an unknown mod) get the default policy of their family, so they are replicated rather than silently dropped.

#### 4.2.2 Replication classes

| Class | Families | Existence | Value capture | Delivery |
|---|---|---|---|---|
| **S** structure | nets, aggregates, static objects, areas, routes/waypoints/segments, water sources, mod entities, generic, event existence and target presence | EDC → P0 | EDC (tags) + SDC safety net (20 s) | P0, encoded once and shared; value-only updates coalesced to ≤ 10 Hz per entity (§4.4.5) |
| **G** global | City, budget entity, `TimeData`, palette | snapshot | hot poll every frame | P0, change-driven, ≤ 4 Hz |
| **V** value | simulation types on S entities: building state, net values, area amounts, top-level tree growth, traffic lights, event values, statistics | — | SDC (4 s), hot set, light fast path | per peer: P1 if in attention, else P3 |
| **A** agent | citizens, households, companies, pets, chirps, journal entries, life-path events | EDC → P0 | SDC (60 s), hot set | per peer: P1 if in attention, else P3 |
| **A-low** transient agent | service requests, seekers, meetings | EDC → P3 | SDC (60 s) | P3 only; references to them tolerate staleness |
| **M** mover | vehicles, creatures, parked cars, train members | EDC → P0 | discrete state: motion sampler for movers in interest + SDC (60 s); motion: per tick | discrete: P1 in interest, else P3; motion: P2 unreliable |
| **K** keyed | state on derived entities: zone cells, trees on derived objects, player overrides on sub-objects, keyed deletions, station-stop state, gate-bypass route state | — | EDC on derived entities, path-result trigger + SDC (stop values 4 s, growth 120 s) | player edits and stop links P0; stop values P1/P3; growth P3 |
| **CH** channel | system state, prefab state, icons, names, cell maps, terrain, water, flow status, clock | — | pollers / special | §4.10, §4.11.5 |
| **OnDemand** | paths and navigation of inspected vehicles (segment paths of lines are S, §4.11.3), `ServiceDispatch` | — | inspection closure | P1 to the requesting peer only; never hashed |
| **Excluded / Derived** | everything else | — | — | never |

#### 4.2.3 Default rule

For a tracked family, replicate (P-C §3.3):

- every `Serializable` type as data, through its own `ISerializable`;
- every `EmptyTag` type as presence;
- the presence of `EmptyData` types in the signature, so entities created from raw archetypes get the same derived components a loaded save would get, but not their data (the game rebuilds it, R04 §0.2), except the audited list of §4.2.4;
- the enabled bit of enableable types (not for `ISerializeAsEnabled` types, R01 §2);
- the host `UpdateFrame` shared value for movers only (it must be exact for interpolation, R15 §2.5).

Non-serializable components are runtime or derived state by the game's own definition (R04 §2.2) and never travel.

#### 4.2.4 Presence-only types replicated as data (reverse-buffer audit rule)

**Rule** (P-A §3.2, J2): a data-carrying `IEmptySerializable` type is replicated as data when all of its runtime writers are `GameSimulation` systems or systems that are off on clients, and something on a client reads it (UI, rendering, derivation). Game.dll has about 100 data-carrying `IEmptySerializable` types [V]; most are rebuilt by systems that stay on (geometry, `SubObject`, `SubNet`, `SubLane`, `ConnectedEdge`, `InstalledUpgrade`, `OwnedVehicle`, `OwnedCreature`, `LaneObject`, `IconElement`, `ConnectedBuilding` with the road-choice reconcile of §4.11.5, ...). A kept system that maintains a buffer only on `Created`/`Deleted` while a simulation system edits it in between does not count as a runtime writer that stays on. The initial list:

| Type | Element / field [V] | Why it cannot be derived on a client |
|---|---|---|
| `Game.Buildings.Renter` (buffer) | `Entity m_Renter` | written at runtime only by `GameSimulation` systems (property processing, move-away, removal); rebuilt only by `RenterSystem` in the Deserialize phase (`SystemOrder.cs:815` [V]) |
| `Game.Citizens.HouseholdCitizen` (buffer) | `Entity m_Citizen` | appended by `CitizenInitializeSystem` (off on clients, R11 §2.11); rebuilt only by `HouseholdCitizenSystem` in Deserialize (`SystemOrder.cs:829` [V]) |
| `Game.Citizens.HouseholdAnimal` (buffer) | `Entity m_HouseholdPet` | `HouseholdPetInitializeSystem` off on clients |
| `Game.Buildings.Student` (buffer) | `Entity m_Student` | school enrolment runs in `GameSimulation` |
| `Game.Routes.RouteWaypoint`, `RouteSegment` (buffers) | `Entity m_Waypoint`, `Entity m_Segment` | runtime source of truth of a route (R13 §9) |
| `Game.Vehicles.Controller` | controller entity | set by spawning code in `GameSimulation`; rebuilt by `ControllerSystem` only in Deserialize (R15 §4.5) |
| `Game.Creatures.GroupCreature` (buffer) | group members | `GroupSystem` off on clients; rebuilt by `GroupCreatureSystem` only in Deserialize (R15 §4.5) |
| `Game.Vehicles.Passenger` (buffer, on vehicles) | `Entity m_Passenger` | boarding and alighting edit it in `ResidentAISystem` and `PetAISystem` (`ResidentAISystem.cs:3590, 3790, 3813`, `PetAISystem.cs:749-788` [V]); the kept `Creatures.ReferencesSystem` reacts only to `Created`/`Deleted` creatures (`Game.Creatures/ReferencesSystem.cs:115-120, 195-200` [V]); read by vehicle panels and line usage (`TransportUIUtils.cs:86-140` [V]) |
| `Game.Routes.RouteVehicle` (buffer, on routes) | `Entity m_Vehicle` | depots and transport AIs append at runtime and `TransportLineSystem` removes vehicles whose `CurrentRoute` changed (`TransportDepotAISystem.cs:691`, `TransportLineSystem.cs:266-310` [V]); the kept `Vehicles.ReferencesSystem` adds and removes only on `Created`/`Deleted` (`Game.Vehicles/ReferencesSystem.cs:466-477, 698-706` [V]); read as the line's vehicle count (`TransportUIUtils.cs:86-98` [V]) |
| `Game.Routes.ConnectedRoute` (buffer, on stops) | `Entity m_Waypoint` | maintained at runtime by `WaypointConnectionSystem` (`WaypointConnectionSystem.cs:63-140` [V]), which is off on clients (§4.11), and in Deserialize by `ConnectedRouteSystem` (`SystemOrder.cs:834` [V]); read by the stop and line panels |
| `Game.Vehicles.GuestVehicle` (buffer, on buildings) | `Entity m_Vehicle` | `DeliveryTruckAISystem` removes trucks that change target (`DeliveryTruckAISystem.cs:430-436` [V]); `Vehicles.ReferencesSystem` adds only on `Created` (`:324-326` [V]) |

These types use the `EntityRefList` codec (each element is a list of `NetRef`s through the type's entity offsets, §4.3.5). They are **hashed as multisets** (order-insensitive), because after a join the Deserialize-phase rebuild fills them in chunk order while the host has insertion order (R04 §5); an apply always writes the host's exact order. The host re-captures them in the same transaction as the forward reference that changed them (§4.2.6), and the client systems that would edit them locally on a despawn (`HouseholdAndCitizenRemoveSystem`, `HouseholdPetRemoveSystem`, `RemovedSystem`) are off (Appendix E). Kept reference systems that also add or remove entries on `Created`/`Deleted` (`Passenger`, `RouteVehicle`, `GuestVehicle`) use `TryAddUniqueValue`/`RemoveValue` on the buffer the applier has just written with the host's content, so their edits are no-ops. The audit is repeated for every game patch with the `TypeAudit` tool (IMPLEMENTATION-PLAN), which lists every data-carrying `IEmptySerializable` type together with its writers and their phases, and adapters can add mod types.

`Game.Net.Aggregated` is different: `AggregateSystem` (kept) maintains it, so it is not replicated. The applier pre-sets it from the replicated `AggregateElement` list before Modification2B so that `AggregateSystem` appends to the host's aggregate instead of inventing one (P-C §3.7 step 8), and `AggregateReconcileSystem` enforces the host's membership afterwards (§4.7.8).

#### 4.2.5 Exclusions and merge policies

| Type | Policy | Reason |
|---|---|---|
| `Game.Objects.Surface` (wetness, snow, dirt; 5 bytes, `Game.Objects/Surface.cs:6-16` [V]) | excluded | cosmetic, simulated locally by `WetnessSystem`/`DirtynessSystem` (R11 §3); J2's correction of P-B. `Game.Areas.Surface` is an empty `IEmptySerializable` tag [V] and travels as presence |
| `CurrentDistrict`, `BorderDistrict` | excluded | geometry functions recomputed by `CurrentDistrictSystem` (R13 §8) |
| `HiddenRoute` | excluded on transport lines; K state on gate-bypass routes | on lines it is a per-player view toggle (`TransportationOverviewUISystem.cs:279-319` [V], R10 §6.1); on gate-bypass routes it is simulation output of `RoutePathReadySystem` (`RoutePathReadySystem.cs:186-199` [V], §4.11.3) |
| `TransformFrame` | motion channel | R15 |
| `Transform`, `Moving` on movers | written at spawn, at moving↔parked swaps and by pose refreshes; owned by the motion writer otherwise; never hashed | R15 §2.6 |
| `PathOwner`, `PathElement`, `PathInformation`, `*Navigation*`, `*NavigationLane`, `Blocker` | OnDemand, **except on route segments**: there `PathInformation`, `PathElement` and `PathTargets` are S-class line path state (family-scoped policy, §4.11.3) | per-tick or derivable on movers (R15 §5); on segments they are the line's connectivity, which the line UI displays |
| `TripSource` | excluded | would make `Vehicles.InitializeSystem` reposition vehicles and pick random parking spaces on clients (R15 §4.3) |
| `CarCurrentLane`, `HumanCurrentLane`, `TrainCurrentLane`, `WatercraftCurrentLane`, `AircraftCurrentLane` | lane fields as derived keys plus flags; per-tick curve positions dropped | R15 §5 |
| `ServiceDispatch` | OnDemand | R15 §8.3 |
| `Game.Notifications.Icon` | icon channel | R14 §4 |
| `Game.Buildings.Building` | **deferred fields**: `m_RoadEdge`, `m_CurvePosition` are replicated and hashed, but the column write keeps the local values; `RoadConnectionReconcileSystem` applies the host's road choice after the kept `RoadConnectionSystem` together with the `ConnectedBuilding` move and a `RoadConnectionUpdated` event (§4.11.5) | a direct column write would desynchronise `ConnectedBuilding` (R13 §8); keeping them purely local would let a client show another address or road connection than the host |
| `Game.Triggers.Chirp` | **merge**: `Liked` flag kept local | per player (R14 §5.4) |

**Merge mechanism.** A merge-policy type has a hand-written `Mask(ref T)` and `Merge(ref T local, in T incoming)`. The canonical bytes are `Serialize(Mask(copy))`, so masked fields never reach the wire or the hash. Apply deserialises into a temporary, then merges into the chunk slot. A **deferred-field** type (`Building`) is not masked: its canonical bytes and hash contain the fields, `Merge` keeps the local fields in the column, and the applier hands the incoming values to the system that applies them consistently later in the frame.

**Authorable masks** are a different, narrower rule: the field stays replicated and hashed, but only part of it may be changed by a player. A type with an `authorableMask` (for example `Road`: `m_Flags` without `LightsOff`; `Building`: the historical flag) has a hand-written `ProjectAuthorable(ref T)` and `MergeAuthorable(ref T live, in T proposed)`. The host commits a proposal's value through `MergeAuthorable` (simulation fields keep the host's live value), and revisions follow the projection (§3.8).

#### 4.2.6 Co-replication groups

Everything captured in one host frame is one transaction, applied atomically in one client frame before any Modification system runs (R09 §5.4, R11 §6.2). This keeps mod invariants intact: `Transform` together with Anarchy's `TransformRecord` (same entity, same record), a building together with Plop the Growables' tags, an edge together with Zoning Toolkit's `ZoningInfo`, a household with its citizens, a building with its sub-nets and sub-areas, a train controller with all `LayoutElement` members, a vehicle with its passengers' `CurrentVehicle`, and new edges with the aggregate they join. Partners created in different host frames rely on reliable in-order delivery plus pending references (§4.7.5). A transaction too large for one message travels as several epoch parts and is still applied atomically (§4.6.2).

**Reverse-buffer co-capture.** The host's simulation edits the data-replicated reverse buffers of §4.2.4 silently (for example `HouseholdAndCitizenRemoveSystem` removes a dead citizen from `HouseholdCitizen` without tagging the household, `Game.Citizens/HouseholdAndCitizenRemoveSystem.cs:100-117` [V]), so the scan would find the change only after its cycle (60 s for A-class). Instead, whenever the host captures a spawn, a despawn or a changed value of a forward-reference type, it re-serialises the affected owners' reverse buffers from live ECS and emits them as P0 values in the **same** transaction:

| Forward reference (on the member) | Reverse buffer (on the owner) |
|---|---|
| `HouseholdMember.m_Household` | `HouseholdCitizen` |
| `HouseholdPet.m_Household` | `HouseholdAnimal` |
| `PropertyRenter.m_Property` | `Renter` |
| `Game.Citizens.Student.m_School` | `Game.Buildings.Student` |
| `GroupMember.m_Leader` | `GroupCreature` |
| `Controller.m_Controller` | `LayoutElement` (train and trailer controllers) |
| `CurrentVehicle.m_Vehicle` (creatures) | `Passenger` (vehicle) |
| `CurrentRoute.m_Route` (vehicles) | `RouteVehicle` (route) |
| `Game.Routes.Connected.m_Connected` (waypoints) | `ConnectedRoute` (stop) |
| `Target.m_Target` (vehicles with `DeliveryTruck`) | `GuestVehicle` (building) |

The new owner comes from the live value. The old owner comes from the tag sweep for despawns (it reads the forward references of `Deleted` agents before `CleanUpSystem` destroys them, §4.4.1) and from the value shadow for value changes (the old canonical bytes are decoded through the type's codec in the game layer). A client therefore never holds a reverse buffer that points at a despawned entity, and `Event+RentersUpdated` is synthesised only after the `Renter` buffer of that property has been applied in the same transaction (§4.7.8). An owner that is a derived entity (a station stop, which is a building sub-object) gets its re-captured buffer as a `KEYED` record under its sub-object key in the same transaction. Rows whose owner buffers simulation systems also edit without a forward-reference change (`RouteVehicle` cleanup in `TransportLineSystem`, `GuestVehicle` removals) are additionally covered by the V scan of their owners (4 s).

**As built (w3/agents).** Citizens, households, household pets and companies (`Family.Agent`) are replicated with identity; transient agents (requests, seekers, meetings) are not, and every reference to them travels as null (the codec's lenient write, also used for service requests and icons).

- *Existence* is global and rides the structure stream (`SyncController`): agents are bound at host session start (`BindCity`) and before every save (`BindCreated`, so the join snapshot carries their ids), spawned from the game's `Created` tag and despawned from `Deleted`. Clients create them from the prefab's `ArchetypeData.m_Archetype` (the archetype `BirthSystem`, `HouseholdSpawnSystem` and the company spawn systems use) and never tag them `Updated`. `FamilyClassifier.IsHostReplicated` adds agents to the replicated families on the host only (`Features.Agents`, decided at session start); `IsReplicated` stays false, so clients can never propose, edit or delete an agent.
- *Reverse buffers as data* (§4.2.4): `Renter`, `Game.Buildings.Student`, `HouseholdCitizen` and `HouseholdAnimal` have list handlers (net ids in host order; unresolved entries left out); the integrity hash compares them as multisets (`SetHash`). `Employee`, `Occupant`, `Patient`, `PropertyRenter`, `Worker` and the other agent references are plain `ISerializable` data that now resolve because agents have ids.
- *Key links and co-capture* (§4.2.6): household membership and property are the agent's key (`AgentRefs.KeyHash`); a change found by the scan is an upsert in the next broadcast. Every spawn, despawn and key change collects the old and new owners (`AgentRefs.CollectOwners`): agent owners go as full records in the same batch, building owners as their reverse buffers in one `MessageKind.AgentValues` broadcast right after it. The renters, workers and students of a despawned building are rechecked first.
- *Values* (`AgentSync`): a rolling scan (`RollingScan`, about 0.8 ms per frame with peers, 0.25 ms alone) hashes each agent's replicated component set (plus enabled bits) and its simulation values. Changes go into each live peer's `AgentPlanner` (coalesced, latest value wins): focus first (closure of the peer's selected and followed entity, `ClosureBuilder` over renters, households, members, employees, students, occupants, a pedestrian's citizen, a vehicle's passengers, keeper and owner), then the households and companies around its camera with their members, then the rest oldest first, within 16 kB/s per peer (focus never below 4 kB per frame) and never behind a backlogged link. Value changes travel as `MessageKind.AgentValues` (90, `SimValueMessage` format, no tags); component-set changes as per-peer upserts in `Epoch` messages. Clients ask again for agents whose values referenced something not known yet (`MessageKind.AgentRefresh` = 91, throttled by `RefreshThrottle`), refresh the open info panel and report the followed entity (`ViewReport.Followed`).
- *Movers*: `SyncRecordKind.MoverAgents` (120) in the movers batches carries `Resident.m_Citizen` / `Pet.m_HouseholdPet`, `PersonalCar.m_Keeper` and a vehicle's `Owner` (written right after the spawn, so the kept reference systems derive `CurrentTransport` and `OwnedVehicle` as on the host; later owner changes keep `OwnedVehicle` in step).
- *Client gates*: `HouseholdAndCitizenRemoveSystem`, `HouseholdPetRemoveSystem`, `RemovedSystem`, `SchoolUpdatedSystem`, `AddHealthProblemSystem` are off (the host sends every cascade); `Game.Serialization.CitizenSystem` (the load-time citizen prefab re-roll) is off for the whole client session, so snapshot citizens keep the host's prefab. Snapshot agents the host never bound are removed when the client goes live (guarded).
- *Integrity*: agents are verified for existence and `HouseholdMember`, `HouseholdPet`, `HouseholdCitizen`, `HouseholdAnimal` (all promoted to the broadcast stream, so no repair storm); their values and the citizen prefab are not verified.
- Chirps, life-path events and journal entries (`Family.Event`) and following a citizen were added later; see [SYNC-COVERAGE.md](SYNC-COVERAGE.md) fixes 1-3 and 13.

#### 4.2.7 Policy table

`policies.json` (embedded resource, overridable by a file in the mod folder without a rebuild, P-C §9.4) holds per-type overrides, optionally scoped to one family (for example `PathInformation` is OnDemand on movers but S-class line state on the `Segment` family, §4.11.3): class, codec, merge or deferred-field policy, `authorableMask` (§4.2.5), `source` (S rather than V on structure families), tag rule (§4.7.7), attention topics (§4.8.3), `playerAuthorable` (§5.2), `noReapply` (opt-out from the end-of-frame re-application of spawn payloads, §4.7.8), `cacheReceived` (kept in the client's received-value cache, §3.7), `presenceSafe` (unresolved derived keys decode to a stand-in, §4.11.3), `guarded` (restored by the route guard inside preview windows, §4.11.4), `onDemand`, `excluded`, `hashAsSet`. It is parsed and validated in Core (unit-tested). Defaults come from the family. The policy version is part of the catalog fingerprint.

**Data-file parity.** Every data file (`policies.json`, `gates.json`, `cosmetic.json`, `adapters.json`, `sysstate.json`) can be overridden from the mod folder, so the declared version number alone does not prove that two peers run the same tables. `Hello` therefore carries a digest of the **effective content** of every data file after overrides (canonical JSON, xxHash64 per file); a mismatch blocks the join and the report names the file and which side uses an override (§7.9).

### 4.3 Codec: the game's `ISerializable` on the wire

Each component type implements `Serialize<TWriter>(TWriter writer)` and `Deserialize<TReader>(TReader reader)`, with writer and reader passed by value (`Colossal.Serialization.Entities/ISerializable.cs:5-7` [V]). We drive them with our own writer and reader (R01 §11.2, P-C §3.4). Hidden entity indices (`PathNode` writes `Write(Entity, ignoreVersion: true)`, `PathNode.cs:133-149`), private fields (`PlayerMoney`), recomputed fields (`Curve.Deserialize` recomputes `m_Length`) and version branches (`TrafficLights.Deserialize` checks `reader.context.version >= Version.nextLaneSignal`, `Game.Net/TrafficLights.cs` [V]) are therefore handled by the game's own code.

#### 4.3.1 `NetWriter` and `NetReader`

- Both are structs (copied by value into nested calls) that hold a reference to a class state object containing a Core `CanonicalWriter`/`CanonicalReader` and the entity mapper, the same reason the stock reader keeps its cursor in a `NativeReference` (R01 §2.2).
- **Every byte is defined in Core.** `CanonicalWriter`/`CanonicalReader` (core-codec) implement each primitive and composite of the game's `IWriter`/`IReader` (about 45 and 50 members, `Colossal.Serialization.Entities/IWriter.cs`, `IReader.cs` [V]) in Core types: `float3` and `quaternion` as float tuples, `Bezier4x3` as 12 floats, `Hash128` as four `uint`s, `Color`/`Color32`, strings, primitive arrays, `Begin`/`End` length blocks, and entity references through a Core `IEntityRefCodec` that emits `NetRef`s. `NetWriter`/`NetReader` forward one line per member. Golden byte vectors in the Core tests pin the layout.
- Primitives use the stock little-endian layout; `Begin()`/`End()` blocks use a 4-byte length prefix like the stock writer, because some `Deserialize` implementations skip blocks (P-C §3.4).
- Every read is bounded: a reader is opened over exactly the record's declared length, an overrun throws, and the applier checks that the whole length was consumed (§4.7.4).
- `Write(Entity)` writes a `NetRef` (§4.3.3): a bound replicated entity → slot; a prefab entity → prefab rank; a derived entity with a key provider → derived key; a tracked entity without a slot on the host → **bind on reference** (allocate a slot now and emit its `SPAWN` in the same epoch, so no dangling reference leaves the host, P-C §2.4); anything else → `Local` (decodes to `Entity.Null`, counted in diagnostics). `Write(Entity, ignoreVersion: true)` resolves by index only.
- `Read(out Entity)` decodes a `NetRef`. An unbound slot decodes to `Entity.Null` and flags the current `(slot, type)` record for a retry (§4.7.5).
- Write context: `new Context(Purpose.SaveGame, Version.current, default)` (writers never read the format, R01 §2.3). Read context: the owned full-tag context below.

#### 4.3.2 Owned full-tag read context

`Context` has a public five-argument constructor that allocates format storage (`Colossal.Serialization.Entities/Context.cs:30-37` [V]). `ContextFormat`'s only field is `UnsafeList<uint> m_Bits` and `EnableTag` is internal (`ContextFormat.cs:9, 26-29` [V]). `ctx.format` returns a struct copy that shares the bit storage, so:

```csharp
var ctx = new Context(Purpose.LoadGame, Version.current, default,
                      Enum.GetNames(typeof(FormatTags)).Length, Allocator.Persistent);
var fmt = ctx.format;                                                    // copy shares m_Bits.Ptr
ref var bits = ref UnsafeUtility.As<ContextFormat, UnsafeList<uint>>(ref fmt);
for (int i = 0; i < tagCount; i++) bits[i >> 5] |= 1u << (i & 31);       // effect of internal EnableTag
```

Startup guard (probe `P-CTX`): `UnsafeUtility.SizeOf<ContextFormat>() == UnsafeUtility.SizeOf<UnsafeList<uint>>()` and `ctx.format.Has(t)` is true for every `FormatTags` value. Fallback: invoke the internal `EnableTag` by reflection. If both fail, component replication is impossible and the probe is critical: the mod refuses to host or join with an explanation. A tag-less context would crash on the first `format.Has` (R01 risk 1) and shift tax rates (R11 §5.3). The context is created once per session and disposed at session end.

#### 4.3.3 `NetRef` encoding

One varint `v = (payload << 2) | tag`. The same bytes are used for hashing, so references are canonical.

| Tag | Payload |
|---|---|
| 0 | 0 = `Entity.Null`, 1 = `Local` (not replicated; decodes to `Entity.Null`) |
| 1 | slot |
| 2 | prefab rank |
| 3 | extended; the next byte is the subkind: 0 = provisional `LocalId` (proposals only), 1 = lane key, 2 = sub-object key, 3 = secondary-object key, 4 = block key, 5 = owned-route key, 6 = flow-edge key, 7 = owned-route element key; key fields follow |

#### 4.3.4 Canonical bytes and member hashes

- **Canonical bytes** of `(slot, type)` = the `Serialize` output through `NetWriter` (of the masked copy for merge-policy types). Two peers with equal canonical bytes hold equal authoritative values; padding, runtime-only fields and recomputed fields cannot cause false mismatches (P-C §3.4, J2).
- **Member hash** `h(slot, type) = xxHash64(canonical bytes, seed = typeRank)`, stored as 32 bits. Buffers are hashed as count plus elements, or as a multiset for `hashAsSet` types. Existence member `= mix(slot, gen, typeSetId, family)`.
- Heights need no quantisation: replicated values are host-authoritative and `GroundHeightSystem` is off on clients (§6.4), so client heights are never recomputed. Residual differences go to the tolerance list (§8.5).

#### 4.3.5 Codecs

| Codec | Used for | Notes |
|---|---|---|
| `SerializableComponentCodec<T>` (`T : unmanaged, IComponentData, ISerializable`) | components | apply: copy the current value to a stack temporary, deserialise into the temporary, verify the whole record was consumed, then copy it into the chunk column (§4.7.4) |
| `SerializableBufferCodec<T>` (`T : unmanaged, IBufferElementData, ISerializable`) | buffers | count + elements through `UnsafeUntypedBufferAccessor`; apply: decode all elements (`default` + `Deserialize` each) into a scratch array first, then `ResizeUninitialized(n)` and copy |
| `SharedComponentCodec<T>` | shared serializable components at spawn | batch `SetSharedComponent` per value (`EntityManager.cs:1190` [V]) |
| `EmptyTagCodec` | presence | signature only |
| `EnableBitCodec` | enableable types | `ArchetypeChunk.IsComponentEnabled` / `SetComponentEnabled` with dynamic handles (`ArchetypeChunk.cs:291, 441` [V]) |
| `EntityRefListCodec` | §4.2.4 types | elements are entity fields located with `TypeManager.GetEntityOffsets` (R08 §5) |
| `UpdateFrameCodec` | movers | 1 byte |
| `ZoneOverlayCodec` | zone cells | RLE of zone prefab ranks per block (§4.10.7) |
| `MotionCodec` Q-A / Q-B | motion | §4.9 |
| channel codecs | system state, prefab state, icons, names, cell maps, tiles | §4.10 |

Codec instances are created per catalogued type with `MakeGenericType` (Mono JIT; no code generation, hard constraint 1) and invoked through a non-generic interface once per batch. Inside the generic method, `slot.Serialize(writer)` on a `ref T` compiles to a constrained call, so there is no boxing.

#### 4.3.6 Codec self-test

At session start (host and client) and on demand, `CodecSelfTest` takes up to N live samples per catalogued type: `Serialize` → `Deserialize` into a temporary → `Serialize` again; the bytes must be identical. No ECS write happens. A failing type is excluded with a diagnostic; a failing type in a structure family refuses hosting (P-C §11.3, J2). The test also records microseconds per type for the measurement report.

### 4.4 Host capture

The host reads ECS only through `ComponentType.ReadOnly` dynamic handles (an RW handle would stamp change versions and corrupt detection for us and for game systems, R08 §6.3) and only on the main thread (R06 §7).

#### 4.4.1 Tag sweep (end of MainLoop)

`TagSweepSystem` is registered `UpdateBefore<TagSweepSystem, PrepareCleanUpSystem>(SystemUpdatePhase.MainLoop)`. A two-type registration places a system directly next to its parent (`UpdateSystem.cs:173-181, 382-441` [V]), so it runs after every vanilla MainLoop system and every mod system registered `UpdateAt`/`UpdateBefore` in MainLoop, and before `PrepareCleanUpSystem` snapshots and `CleanUpSystem` removes the one-frame tags (`PrepareCleanUpSystem.cs:21-54`, R02 §5). Tags added later (cohtml trigger handlers in `GameManager.UpdateUI`, which runs after MainLoop, `GameManager.cs:713-724` [V]; LateUpdate simulation; mod systems registered `UpdateAfter` in MainLoop) survive into the next frame and are seen by the next sweep.

Per frame it queries `Any(Created, Updated, Deleted, Applied, BatchesUpdated, EffectsUpdated, PathfindUpdated)`, `None(Temp)` over tracked and keyed-derived archetypes and records into a Core `SweepBuffer`: `(entity index, version, tag bits, family)`, the slots of bound entities tagged `Deleted`, and zone blocks tagged `Created`/`Updated`/`Applied`. It also records two things that exist only until `PrepareCleanUpSystem`:

- **Unlock events**: whether any `Event`+`Unlock` entity exists (cheap emptiness test). `MilestoneSystem` creates them through the ModificationEnd barrier and `UnlockSystem` creates them in MainLoop; `PrepareCleanUpSystem` deletes every `Event` entity at the end of the same frame (`PrepareCleanUpSystem.cs:21-28`, `MilestoneSystem.cs:60, 96-102`, `UnlockSystem.cs:117, 206-213` [V]), so a frame-start test would never see them. A set flag makes the next two capture flushes scan the prefab unlock bits (§4.10.3).
- **Forward references of deleted agents**: for `Deleted` entities of the member families of §4.2.6, the owner entity of each forward reference (household, property, school, group leader, controller), so the capture flush can re-capture the owner's reverse buffer in the same transaction. These are the only component reads in the sweep; the data of a deleted entity is final.

Otherwise **it reads no component data** (D9). Cost is proportional to the number of tagged entities (≤ 0.3 ms typical [E]).

#### 4.4.2 Capture flush (next frame start)

`HostCaptureFlushSystem` is registered `UpdateAfter<HostCaptureFlushSystem, EndFrameBarrier>(SystemUpdatePhase.MainLoop)`. `EndFrameBarrier.OnUpdate` has just completed its producer handle (`Game/EndFrameBarrier.cs:34-43` [V]), so the simulation jobs of the last LateUpdate are done; each query still calls `CompleteDependency()` for remaining readers (safety checks are stripped, R08 §1). The system brackets the **capture window** (§4.5.5): every mutation of engine state (slot table, shadows, dirty sets, peer sets, epoch log) happens between its first and last step. Steps:

0. **Open the window.** Join the previous frame's workers (`WaitForFrameWork`), then drain the engine command queue: acknowledgements, peer additions and removals, repair requests, view reports and results that arrived from `SessionController.Update` outside the window.
1. **Despawns.** For each `Deleted` bound slot that was not also `Created` in the same frame: `DESPAWN(slot, gen)`; clear that slot's dirty bits for every peer; mark it for deferred free (§3.1). `Created` + `Deleted` in one frame sends nothing. The owners recorded by the sweep get their reverse buffers re-captured (§4.2.6).
2. **Spawns.** For each `Created` tracked entity that still exists, and for every bound slot whose `spawnEmitted` bit is still false (entities bound by a commit, by the snapshot bind pass or by discovery): allocate a slot if it has none, serialise every replicated type into canonical bytes (hash shadow; value shadow for V/A/M types), emit `SPAWN` (prefab rank, type set, host `UpdateFrame` for movers, flags, payloads) and set `spawnEmitted`. A spawn is complete and atomic. Household + citizens and building + sub-nets + sub-areas land in one transaction because they are created in the same frame (EndFrameBarrier playback, R13 §5). Owners named by the spawned entity's forward references get their reverse buffers re-captured.
3. **Updates of S and G entities** (`Updated`/`Applied`): serialise each replicated type, compare the canonical hash, emit `VALUE` for changed types (subject to §4.4.5), `STRUCT` when the signature changed, and `TOUCH(slot, tags)` mirroring the host's `Updated`/`PathfindUpdated` so clients re-derive exactly the host's closure (R02 §7). `BatchesUpdated` is mirrored only together with a data or signature change (host hover highlights produce `BatchesUpdated` noise, R09 §1.3). The revision is bumped when the authorable projection changed (§3.8).
3a. **Path results** (line connectivity, §4.11.3): the pathfinder writes a route segment's `PathInformation` and `PathElement` without tagging the segment; its result arrives as an `Event + PathUpdated(owner, positions)` entity that `EndFrameBarrier` created just before this flush (`PathfindResultSystem.cs:246-254`; `RoutePathSystem` always requests events, `PathfindQueueSystem.cs:511-625` [V]). The flush queries those events and re-captures the path-state types (`PathTargets`, `PathInformation`, `PathElement`) of every owner that is a bound segment as S updates of this epoch (P0, never coalesced: line connectivity changes rarely and must be prompt); owners that are segments of gate-bypass routes go to the K channel. The S scan (20 s) remains the safety net.
4. **Updates of V, A, M entities**: the entity joins this frame's hot set (§4.4.4) instead of being captured wholesale. Signature changes (parked↔moving swaps, `Abandoned` added, Tree Controller prefab swaps) become `STRUCT` records.
5. **Keyed derived entities** with tags (zone blocks, sub-objects, secondary objects) go to the K channel (§4.10.7).
6. **Motion sampling** for every tick simulated in the last LateUpdate (§4.9.3).
7. **SDC slice and hot set** within budget (§4.4.3, §4.4.4). A captured change of a forward-reference type re-captures the old and new owners' reverse buffers (§4.2.6).
8. **Channels and pollers** (§4.10), including the prefab-unlock scan when the sweep saw `Unlock` events.
9. **Ingress hook**: proposals are validated and committed through the shared applier (§5.4.2), and host-side repair re-serialisations run, inside the window, after every read of this flush. Commits bind slots and queue `RESULT`/`BIND` records into this epoch; the committed creations are spawned by step 2 of the next flush.
10. **Close the epoch**, split it into parts if its transaction exceeds the per-part cap (§4.6.2), and hand the frame token to the workers (§4.5.5).

Record order inside the frame's transaction: type-set definitions → results and binds (with origin) → despawns → spawns → structural changes → values → touches → keyed state → channel records. A `BIND` therefore always precedes the `SPAWN` it binds (same or earlier epoch). The client applies all structural operations as one batch and resolves references after all spawns of the transaction exist.

#### 4.4.3 Scan-driven capture (SDC)

Silent writes (simulation jobs writing without tags) are found by a time-sliced, Burst-free version of R08 §12.3 (P-B §4.3.2). The bookkeeping (chunk records keyed by sequence number, version equality, cursors, cadences, budget adaptation) is the Core `ScanScheduler` over abstract chunk records, unit-tested with synthetic version streams; the game-layer `ChunkScanner` only enumerates chunks, reads versions and columns, and executes the visits the scheduler asks for:

- **Per class** one `EntityQuery` with RO `DynamicComponentTypeHandle`s (`EntityManager.GetDynamicComponentTypeHandle`, `EntityManager.cs:2447` [V]) and `IgnoreComponentEnabledState`. The chunk list is refreshed when the query's order version changes.
- **Chunk record** keyed by `ArchetypeChunk.SequenceNumber` (`ArchetypeChunk.cs:38` [V]): archetype, order version, per-type change versions. A column whose change version equals the recorded one is skipped (equality, not `DidChange`, so wrap-around is harmless, R08 §3.1).
- **Raw prefilter**: for changed columns, a 32-bit raw hash per entity over `GetDynamicComponentDataArrayReinterpret<byte>` (`ArchetypeChunk.cs:730` [V]), buffer elements via `GetUntypedBufferAccessor` (`:817` [V], never the raw buffer column, which holds a process-local pointer, R08 §2), and enabled bits via `GetEnableableBits` (`:302` [V]). Equal to the stored raw hash → skip.
- **Canonical check**: otherwise serialise to canonical bytes and compare the canonical hash. A difference updates the hash shadow, the value shadow (V/A/M), every peer's dirty bit and the bucket sum (§8.2). Both hashes are stored (8 bytes per `(slot, type)`). SDC never bumps revisions (§3.8).
- **Membership**: a changed order version triggers an entity-column diff against the slot table (unbound entities → bind, and every bound slot whose `spawnEmitted` bit is false → spawn, as a safety net for EDC; missing bound entities → `Exists` check → despawn). Entities bound at session start or covered by a snapshot are never re-spawned.
- **Baseline**: the first visit of every chunk happens during host session start (§4.4.8), which records the chunk's versions and primes the shadows without dirty bits, so the first live cycle only pays for what changed since.

Cadences and budget (defaults, adaptive) [E]:

| Class | Full-cycle period | Typical chunks | Chunks per frame at 30 fps |
|---|---|---|---|
| G | every frame (hot poll) | < 10 | all |
| V | 4 s | 3-6k | 25-50 |
| A, A-low | 60 s | 3-5k | 2-3 |
| M discrete (out of interest) | 60 s | 1-3k | 1-2 |
| S safety net | 20 s | 5-15k | 8-25 |
| K trees | 120 s | 1-3k | < 1 |

Main-thread budget: 1.0 ms per frame at ≥ 50 fps, 2.0 ms at ≤ 30 fps. Periods shorten when time is left and lengthen under load; measured staleness is shown in the HUD. Tiered cadences remove R08's "Burst is mandatory" conclusion, which assumed a full scan every tick (J3). After host saves, `TrimPathsSystem` stamps path owners (R12 §6.1); path types are excluded, so only the prefilter pays and the burst is rate-limited.

#### 4.4.4 Hot set

Entities compared every frame, bypassing the cursor: the union of all peers' inspection closures (§4.8.3), signalised junctions in any peer's view (§4.9.9), entities touched by proposals in the last 2 s, and the singletons. Cap: 2048 entities. Per-entity access follows the resolution contract of §3.3 (`Exists`, then `EntityManager.GetStorageInfo(entity)`, `EntityManager.cs:1386` [V], then `chunk.Has` before the RO column pointer).

#### 4.4.5 Coalescing of S value updates

A value-only update of an S entity is sent at once if the entity's previous S send is at least 100 ms old. Otherwise `(slot, type)` joins a shared S-pending set that is flushed at ≤ 10 Hz with the value re-serialised from live ECS at flush time. Spawns, despawns, signature changes and touches are never deferred. This stops a host-side Move It drag or terrain re-snap from emitting a burst per frame. G-class singletons use the same mechanism with a 250 ms interval (≤ 4 Hz), so money and population updates never flood P0.

**Shadow semantics of P0 classes.** For S and G the hash shadow (and therefore the bucket sum) is updated **when the record is emitted** into the shared P0 section, not when the change is first detected: a value waiting in the S- or G-pending set keeps its previously emitted hash. Because P0 is shared and ordered, a beacon placed after the transaction in any peer's message then always describes exactly what that peer has received (§8.4). Change detection compares against the emitted hash, so a pending value that changes again simply stays pending.

#### 4.4.6 Discovery, liveness and the snapshot bind pass

- **Discovery**: chunks of slot-bearing families whose order version changed are checked for entities without a slot (creations without `Created`, e.g. Better Bulldozer record entities made with `EntityManager.CreateEntity`, R09 §2.2); they are bound and spawned as in §4.4.2.
- **Liveness sweep**: about 20k slots per frame are checked with `EntityManager.Exists` (version-checked); a bound entity that vanished without `Deleted` (a mod's direct `DestroyEntity`) gets a `DESPAWN` (P-C §3.2 g).
- **Snapshot bind pass**: `SnapshotBindSystem` (`UpdateBefore<SnapshotBindSystem, SaveGameSystem>(MainLoop)`) runs only in a frame that writes a join snapshot. Since host session start bound the whole city (§4.4.8), it only binds the few tracked entities created since the last capture flush or missed by EDC and discovery (an unbound sweep restricted to chunks whose order version changed since session start, ≤ 2 ms [E]), so that every entity written by `SaveGameSystem` is in the `NetIdSystem` block. Their `spawnEmitted` bit stays false, so the next capture flush spawns them for the peers that are already connected; on the joiner those few `SPAWN`s are harmless upserts onto the snapshot's entities (§7.3). Entities that were bound before the snapshot produce no records at all.

#### 4.4.7 Host CPU budget per frame [E]

| Work | Typical | Worst case and mitigation |
|---|---|---|
| Tag sweep | 0.05-0.3 ms | large edits 2-5 ms for one frame (accepted) |
| EDC in the capture flush | 0.1-0.5 ms | big tool applies or terrain re-snaps: one-frame spikes; S coalescing (§4.4.5) |
| SDC + hot set | 1.0 ms (2.0 ms at ≤ 30 fps) | adaptive periods |
| Motion sampling | 0.5-2 ms at speed 4 | pointer column reads, union-of-interest prefilter, far field at half rate (§4.9.3) |
| Channels and pollers | < 0.3 ms | cadences |
| Ingress commits and repair re-serialisation (inside the window) | rare, < 0.5 ms | a large client proposal: 2-10 ms in one frame (accepted, like a large host edit) |
| **Total** | **≈ 2-3 ms** | the host's own tick budget shrinks by the same amount (`SimulationSystem` caps ticks by measured frame cost, R03 §1.2), so every millisecond is accounted |

#### 4.4.8 Host session start: bind and prime

A save made outside a session has no `NetIdSystem` block, and even a session save holds only bindings, not hashes. Without an explicit start step, the first snapshot's bind pass would bind about a million entities and the next capture flush would emit a `SPAWN` for each of them in one transaction (hundreds of MB, far above every message cap), and the empty hash shadow would make the first scan cycle mark every pair dirty for every peer. Host session start prevents both. It runs after the city is loaded (or when the player hosts the city that is already running, §7.11) and **before the listen sockets and the lobby accept anyone**:

1. **Catalog and identity**: build the type catalog and prefab ranks, load the data files, run the codec self-test (§4.3.6) and the gate and adapter resolution. A critical failure stops hosting with an explanation.
2. **Bind existing**: if the city has a `NetIdSystem` block of the same lineage, restore its bindings and allocator state (`BindExisting`, §3.4).
3. **Bind sweep**: every remaining tracked entity gets a slot (lowest free first), its family and type set, with `spawnEmitted = true`. No record is emitted: no peer exists yet, and every future peer receives these entities through its snapshot.
4. **Prime**: every bound slot is serialised once; the canonical and raw hashes go into the hash shadow, the canonical bytes of V/A/A-low/M/K types into the value shadow, the members into the bucket sums, and every visited chunk's versions into the scan scheduler. This uses `ObserveSource.Prime`: it records baselines and sets **no** dirty bits. The `BuildOrder` allocator learns the current maximum (§5.4.3).
5. **Time slicing**: steps 3-4 run at about 8 ms per frame. The session is paused while they run (a host that loaded a save is paused anyway, because session loads force `pausedAfterLoading`, §6.1), so the baseline is exact; the host's own edits during this time are captured by EDC as usual. The HUD shows "Preparing session n %". For a 100k city this takes 5-20 s [E], measured in M2/M3.
6. **Open**: listen sockets and the lobby are created. A connection attempt that arrives earlier (direct IP) gets `Refuse{HostPreparing}` with a retry hint.

Afterwards the snapshot bind pass, discovery and the membership safety net only ever see entities that are genuinely new, and a join adds no per-entity P0 traffic for the existing city (test: a Core end-to-end run that hosts a 1 M-entity FakeWorld and asserts that a join emits no `SPAWN` for pre-existing slots).

### 4.5 Shadows, dirty sets and epochs

#### 4.5.1 Hash shadow

`HashShadow` (Core, unmanaged memory) stores, per `(slot, typeRank)`, the 32-bit raw hash and the 32-bit canonical hash of the last captured value (for S and G: of the last **emitted** value, §4.4.5), plus a presence bit and an enabled bit, and for types with an authorable mask the 32-bit hash of the authorable projection (§3.8). Storage is per type, in pages of 4096 slots allocated on first use: about 8 bytes per replicated pair. Every canonical change updates the bucket sums in O(1) (§8.2). The hash shadow covers every replicated class and is filled once by host session start (§4.4.8).

#### 4.5.2 Value shadow

`ValueShadow` (Core, unmanaged arena of `(offset, length)` ranges with free lists and incremental compaction) stores the canonical bytes of the last captured value for the classes that are scheduled per peer: V, A, A-low, M discrete state, mover poses and K growth state. It is the only source the workers read when they encode per-peer records, so no ECS access happens off the main thread and a value that changed ten times while waiting is sent once, in its latest form (P-B §4.4). Size ≈ replicated payload of those classes, 30-60 MB for a 100k city [E].

S-class and G-class values are not kept as bytes. They are P0: encoded into the shared epoch section during the capture flush. Joiners receive them through the epoch log (§4.5.4); repairs re-serialise from live ECS on the main thread within the repair budget.

#### 4.5.3 Peer dirty sets

`PeerDirtySet` (Core) per peer: paged `ulong` masks indexed by slot, one bit per position in the slot's type set, plus one summary bit per 64 slots for fast skipping. **Every** capture that changes a V/A/A-low/M/K value sets the bit for **every** peer, whatever path found the change (SDC, hot set, motion sampler); attention only changes the priority at which a peer receives it (P1 or P3), never whether its bit is set. The bit is cleared when the message carrying the record has been **accepted** by that peer's outbox (§9.3); a message the outbox cannot take is never dropped, so no captured value is lost. A signature change sets all bits of the slot. A despawn clears the slot. Worst case 8 bytes per slot per peer (8 MB at 1 M slots); pages exist only where something is dirty [E]. Each page keeps a "dirty since" timestamp for staleness promotion (§4.8.4). A joiner gets its own dirty set created at the snapshot (§7.3).

#### 4.5.4 Epochs and the epoch log

- **Epoch** = one host frame with output (`uint`, monotonic), stamped with the host `frameIndex` at capture.
- **P0 content** (the frame's transaction, events, icons, prefab state, names, system state, clock anchor, proposal results and binds with their origin peer) is encoded **once** and shared by every peer. It is appended to the **`EpochLog`** ring (default 128 MB or 10 minutes, disk spill configurable), which serves join catch-up and short rejoins. Results and binds stay in the shared section so the log and replays contain them; each client acts only on those whose origin is itself (§5.5).
- **Epoch parts**: a transaction larger than the per-part cap (8 MiB uncompressed) is split by the encoder into parts `{epoch, part, partCount}`; the client applies the epoch only after its last part has arrived, still atomically (§4.6.2, §4.7.10).
- **Per-peer message** for the frame = the shared P0 section + that peer's P1/P3 value sections + its hash beacons. Motion travels separately on the unreliable lane.
- **Ordering**: a slot's dirty bits are first set when its `SPAWN` is captured, so a value can never be encoded for a peer before the spawn that precedes it in the same or an earlier message; a despawn clears the bits, so no value follows it. Everything rides the one reliable ordered lane (§9.2).
- **Acknowledgement**: each client acknowledges its last applied epoch about 10 times per second. Acks drive slot reuse (§3.1), ring trimming and resume points for short drops (§7.8).

#### 4.5.5 Thread phases

The host engine's state (slot table, shadows, dirty sets, pending sets, peer sets, epoch log, type-set registry) is mutated only inside the **capture window** (main thread, from the start to the end of `HostCaptureFlushSystem`, §4.4.2) and read by the workers only outside it (for the rest of the frame). The next capture flush first joins the workers of the previous frame. No locks are taken inside either window.

- **Command queue**: every engine call made outside the window (acknowledgements, `AddPeer`/`RemovePeer`, repair requests, `MarkDirty`, `StartRebaseline`, view reports and inspection requests arriving through `SessionController.Update`) is appended to a single-threaded `EngineCommandQueue` and executed at the start of the next window, after `WaitForFrameWork`. Read-only queries (statistics) are always allowed.
- **Binding** happens only inside the window: proposal commits run in the ingress hook of the capture flush (step 9), and bind-on-reference happens while serialising inside the window.
- **Assertion**: debug builds stamp every mutating engine method with a phase check (`[ThreadAffinity(Main)]` plus an "inside capture window" flag) and throw on a violation; a Core stress test runs the workers concurrently with randomised main-thread calls through the queue.
- On clients, workers decode and merge epochs into `ApplyPlan`s, and motion frames reach the main thread through single-producer/single-consumer staging rings.

### 4.6 Wire format

All encodings live in Core: little-endian, LEB128 varints, zig-zag for signed deltas, bounds-checked reads, pooled buffers (no allocation per message; the game runs an incremental GC with 3 ms slices, R06 §1).

#### 4.6.1 Envelope

Every message: `u8 kind`, `u8 flags` (bit 0 compressed, bit 1 fragment, bit 2 has-uncompressed-size), optional `varint uncompressedSize`, body. A compressed body (flags = compressed | has-uncompressed-size) is `varint uncompressedSize | u8 compressionKind | compressed bytes`: the kind byte names the codec (`CompressionKind`), so a receiver decodes each message by what it names (wave 1 integration; pinned by the `EnvelopeCodec` golden vectors). Kinds: `Control`, `Hello`, `Welcome`, `Refuse`, `Epoch`, `Motion`, `MotionControl`, `Clock`, `View`, `Presence`, `Proposal`, `Ack`, `HashDetail`, `RepairRequest`, `BlobManifest`, `BlobChunk`, `BlobHave`, `Chat`, `Ping`. The full catalogue is Appendix C.

#### 4.6.2 Epoch message

```
Epoch (lane R, reliable ordered):
  u32 epoch | u32 hostTick | u8 flags {SnapshotBoundary, CatchUp, ChannelKeyframe, MultiPart} | u8 sectionCount
  [MultiPart: varint partIndex, varint partCount]
  repeat: u8 sectionType | varint byteLength | payload
sectionType:
  TX    shared P0 records (type sets, results and binds with origin, despawns, spawns, structs, S values,
        touches, keyed edits)
  VAL   per-peer pulled values (V, A, A-low, M discrete, poses, K growth and stop values, flow status, G)
  SYS   system state blobs            PFB   prefab state            ICON  icon operations
  EVT   events (XP toasts, milestone hints, journal callbacks, radio)  NAME  names
  MAP   cell-map deltas                CLK   clock anchor            HASH  bucket beacons
  SES   session settings, roles, players    INSP  inspection-closure samples (with sample tick)
  TSET  type-set keyframe (whole registry; first section a joiner or resyncing client receives)
```

**Parts.** The host encoder never emits a message whose uncompressed body exceeds the per-part cap (8 MiB). A larger transaction (a Move It paste or move of tens of thousands of objects, a Tree Controller forest brush, a Line Tool array, a bulk action of Plop the Growables or Better Bulldozer, an upgrade cascade) is split at record boundaries into parts that share the epoch number; part 0 carries the header sections, the TX records are distributed in order, and the per-peer sections ride in the last part. The client buffers parts (spilling to a temp file above 32 MB) and hands the epoch to the merger only when `partCount` parts have arrived. The cap on the total size of one epoch is 256 MiB uncompressed; a well-formed epoch above it is a **resync** condition on the client, not a protocol violation (§12.2).

#### 4.6.3 Records

```
NetRef    = §4.3.3
TYPESET   = varint typeSetId, varint count, typeRank...
SPAWN     = varint slot, u8 gen, varint prefabRank+1 (0 = none), varint typeSetId, u8 flags {Mover, HasUpdateFrame, Repair}
            [u8 updateFrame], varint payloadCount, repeat { varint typeRank, varint length, canonical bytes }, enabledBits
DESPAWN   = varint slot, u8 gen, u8 flags {DeleteChildrenAsHost}
STRUCT    = varint slot, varint typeSetId, varint addedPayloadCount, repeat { varint typeRank, varint length, bytes }
VALUE     = varint slot, varint typeRank, u8 hdr {bit0 enabled-bit present, bit1 enabled value, bit2 presence-only}, varint length, bytes
TOUCH     = varint slot, u8 tags {Updated, BatchesUpdated, PathfindUpdated}
BIND      = u8 origin (peer id), varint localId, varint slot, u8 gen    (proposal results; precede the spawns they bind;
                                                                          only the origin peer acts on it)
POSE      = varint slot, varint length, payload {Transform (quantised), u8 flags}   (out-of-interest mover re-pin, §4.9.5)
KEYED     = u8 family, key, varint typeRank, varint length, bytes;   KEYED_DEL = u8 family, key
ZONE      = block key, varint revision, varint length, RLE(zone prefab rank per cell)
ICON      = u8 op {bits 0-5: Add, Remove, RemoveAll, Event; bit 6: target present; bit 7: location present}, NetRef owner,
            varint prefabRank+1 (0 = none), u8 priority, u8 layer, u16 flags, [NetRef target], [float3 location]
NAME      = NetRef target, varint utf8Length, utf8 (empty = clear)
PFB       = varint length, payload: bitset diff over prefab ranks {unlocked, relocked}, signature-placed list, (prefabRank, progress)*
SYS       = u32 entryId (stable hash of the entry's string id), varint length, blob (the system's own Serialize output)
RESULT    = u8 origin (peer id), varint proposalSeq, u8 status {bit0 rejected, bit1 conflict slot present}, varint reasonCode,
            [varint conflictSlot]
HASH      = u8 class, varint bucket, u64 sum, varint count
TSETKEY   = varint length, payload: varint count, repeat { varint typeSetId, varint n, typeRank... }   (TSET section only)
```

Payload bytes are canonical (§4.3.4). Entity references inside them are `NetRef`s. A `RESULT` or `BIND` whose origin is not the local peer is skipped by the client (it is still part of the shared section, so the epoch log and replays stay identical for everyone). Test: two clients that use the same proposal sequence numbers and `LocalId` values never act on each other's results.

#### 4.6.4 Encodings that depend on history

- Component and buffer values are always sent whole (canonical bytes). Per-packet compression removes most redundancy. No per-peer baselines are kept, which keeps latest-value-wins simple and robust (P-B §4.4). Per-type delta encodings for the hottest buffers (sparse `Resources`, append-only `CityStatistic`) are a measured optimisation for milestone M9, not part of the baseline.
- **Cell maps** use versioned XOR deltas: the host keeps the last four versions of each map (shared by all peers); a peer receives `XOR(current, last version it acknowledged)` with zero-run-length coding, or a keyframe if that version left the ring (P-A §3.11.5).
- **System state** blobs are sent whole when their hash changes, rate-limited at the source (§4.10.1).

#### 4.6.5 Compression

Message bodies of 256 bytes or more are compressed. The baseline codec is ZSTD level 1 through the game's native `CompressionUtils`/`NativeCompression` (R06 §5.4), behind Core's `ICompressor`. LZ4 is used only if a startup probe proves that the native LZ4 path works (R08 notes that no liblz4 may ship; J3). A pure C# LZ4-block implementation in Core (no native dependency) serves the unit tests and is the last-resort fallback. The codec is negotiated in the handshake (`HelloMessage.CompressionMask`, `WelcomeMessage.Compression`, `CompressionNegotiation`). **Handshake rule:** `Hello`, `Welcome`, `Refuse` and every control message sent before the session codec is known are compressed with LZ4 (`CompressionNegotiation.Handshake`), which every peer and every protocol version can decode, so a host can always refuse readably. Each peer decodes the whole connection with a codec built on its best local compressor (every envelope names its codec, and LZ4 blocks always decode through the managed fallback); only the *encoding* codec switches, once `Welcome` is sent or processed. The decoder therefore never changes under the network thread. Saves are already ZSTD-compressed and are never recompressed. A decoder rejects any message whose declared uncompressed size exceeds 8 MiB (one epoch part; bulk chunks: 256 KiB) before decompressing it. Because the host encoder splits larger transactions into parts, a message above the cap can only come from a broken or hostile peer.

#### 4.6.6 Typical sizes (before compression) [E]

| Record | Bytes |
|---|---|
| Edge spawn (Edge, Curve, PrefabRef, seed, BuildOrder, Elevation, Road, ...) | 120-200 |
| Node spawn | ~60 |
| Building spawn bundle (building + lot + 2-6 sub-nets) | 300-900 |
| Citizen spawn | 70-110 |
| Vehicle or creature spawn | 50-100 |
| Value update (household money, citizen state) | 10-40 |
| Touch | 2-4 |
| Despawn | 3-5 |
| Icon add/remove | 6-12 |
| Segment path state (`PathInformation` + `PathElement` with lane keys, per segment) | 0.3-3 KB |
| Motion frame (Q-B delta / Q-B keyframe) | 10-11 / ~16 (R15 §9.1; §4.9.3) |

### 4.7 Client apply

#### 4.7.1 Client frame order

| Order | System | Registration | Work |
|---|---|---|---|
| 1 | vanilla `EndFrameBarrier` (`SystemOrder.cs:49` [V]) | — | plays back last frame's UI command buffers |
| 2 | `ClientApplySystem` | `UpdateAfter<ClientApplySystem, EndFrameBarrier>(MainLoop)` | gate check, capture stage A, apply programs, street-light derivation, channels (§4.7.2) |
| 3 | vanilla `RaycastSystem`, `PrefabSystem`, `CityConfigurationSystem` | — | — |
| 4 | vanilla `ToolSystem`: PreTool (`OriginalDeletedSystem` sees host deletions of this frame) → ToolUpdate → ApplyTool → PostTool (`SubElementDeleteSystem` cleans sub-nets of host deletions) | — | local tools and prediction (R11 §6.2) |
| 4a | `NoPredictGateSystem` | `UpdateBefore<NoPredictGateSystem, ToolOutputSystem>(ToolUpdate)` | viewers only (§5.6) |
| 4b | `ToolCaptureApplySystem` | `UpdateBefore<ToolCaptureApplySystem>(ApplyTool)` | capture stage B (apply frames only) |
| 5 | `ToolCaptureResultSystem` | `UpdateBefore<ToolCaptureResultSystem>(Modification1)` | capture stage C |
| 6 | vanilla `ModificationSystem` Mod1 ... ModificationEnd | — | derived regeneration from replicated sources |
| 6a | `RouteReadyBridgeSystem` | `UpdateAfter<RouteReadyBridgeSystem, RoutePathReadySystem>(Modification1)` (the vanilla system is off on clients) | path-target ready positions from `PathUpdated` events and after loads; no icons (§4.11.3) |
| 6b | `AggregateReconcileSystem` | `UpdateAfter<AggregateReconcileSystem, AggregateSystem>(Modification2B)` | host aggregate membership (§4.7.8) |
| 6c | `RoadConnectionReconcileSystem` | `UpdateAfter<RoadConnectionReconcileSystem, RoadConnectionSystem>(Modification4B)` | host road choice of buildings, `ConnectedBuilding` move, `RoadConnectionUpdated` (§4.11.5) |
| 6d | `TrafficLightDeriveSystem` | `UpdateAfter<TrafficLightDeriveSystem, TrafficLightInitializationSystem>(Modification4B)` | lane signals and poles (§4.9.9) |
| 6e | `IconAuthorityFilterSystem` | `UpdateBefore<IconAuthorityFilterSystem, IconCommandSystem>(ModificationEnd)` | drops client-local persistent icon commands before playback (§4.10.4) |
| 7 | vanilla `UnlockSystem`, `AllowBarrier<EndFrameBarrier>` | — | processes synthesised `Unlock` events |
| 8 | `SpeedMirrorSystem` | `UpdateBefore<SpeedMirrorSystem, PreRenderSystem>(MainLoop)` | re-assert session speed before `RenderingSystem.PrepareRendering`, which `PreRenderSystem.OnUpdate` calls first (`PreRenderSystem.cs:20-23` [V]) |
| 9 | vanilla `PreRenderSystem` → PreCulling: `MotionWriterSystem` | `UpdateBefore<MotionWriterSystem, PreCullingSystem>(PreCulling)` | JIT slot writer, pinning (§4.9.6) |
| 9a | `RouteGuardSystem` | `UpdateAfter<RouteGuardSystem, PathfindResultSystem>(MainLoop)` | inside route-tool preview windows only: restores host line state on entities outside the local edit before the UI reads it (§4.11.4) |
| 10 | vanilla `UIUpdateSystem`: `SessionUISystem`, `ClientUiApplySystem` | `UpdateAt<…>(UIUpdate)` | bindings; names through `SetCustomName` (needs the open `EndFrameBarrier` window, R10 §0.4) |
| 11 | vanilla `RenderingSystem` → Rendering: `PresenceOverlaySystem` | `UpdateBefore<PresenceOverlaySystem, OverlayRenderSystem>(Rendering)` | remote cursors and ghosts (§11.4) |
| 12 | `ClientEndOfFrameSystem` (game-client-apply) | `UpdateBefore<ClientEndOfFrameSystem, PrepareCleanUpSystem>(MainLoop)` | re-application of this frame's spawn payloads after every kept initializer ran (§4.7.8), re-resolution of references into derived children regenerated this frame (§3.7), liveness unbind of locally deleted bound entities (§3.3), then the registered end-of-frame hooks: capture stage C2 and D, local change capture, rolling verification slice; `GroundHeightSystem` drain |
| 13 | vanilla `PrepareCleanUpSystem`, UI, Cleanup | — | — |
| 14 | `ClientClockSystem` | `UpdateBefore<ClientClockSystem, SimulationSystem>(LateUpdate)` | load handover, then follower clock (`SimulationSystem` disabled only after its own first post-load update, §6.1); evaluates the tool-scoped gate windows for the next frame (§6.3) |

`HostCaptureFlushSystem` and `ClientApplySystem` are siblings after `EndFrameBarrier`; only the one for the current role does work.

#### 4.7.2 Per-frame pipeline of `ClientApplySystem`

1. **Gate check**: every 60 frames and after every load, re-assert the gate table (several systems re-enable themselves on preload, R11 §8.3). If the client is `Faulted`, nothing below runs until the resync (§12.2).
2. **Capture stage A** (§5.2): local UI effects of the last frame, before anything mutates the world.
3. **Take work**: whole merged `ApplyPlan`s from the worker queue (complete epochs only; multi-part epochs are merged after their last part). Live: all structural work plus values up to the budget. Catch-up: up to 6 ms or 200 epochs per frame.
4. **Plan**: the Core `ApplyPlanner` turns the plan plus an abstract view of the client's bindings (slot → entity, generation, family, prefab, type set; liveness) into an ordered **apply program**: unbinds and replacements (§4.7.3), creation groups, add/remove type sets, value writes, enabled bits, parks, tags and closure requests, synthesised events placed after the values they depend on (`RentersUpdated` after the property's `Renter` buffer, `PathUpdated` after a segment's path state, §4.11.3), and the final `Created`/`Updated` additions. Results and binds of other peers are skipped; values for `(slot, type)` pairs covered by a pending proposal are set aside as held payloads (§5.5.4); values superseded in the same plan by a despawn of the slot or by a structural change that removes the type are dropped. The FakeWorld of the Core tests executes the same programs, so the game executor is specified by the Core tests.
5. **Structural batch** (§4.7.3).
6. **Data writes** (§4.7.4).
7. **Finalise**: if every step succeeded, add `Created` + `Updated` (and `Applied` for movers, whose host `UpdateFrame` was set at creation) to the entities created by this plan in one batch per group. If a step threw, add `Deleted` to every entity this plan created, stop, and enter `Faulted` (§12.2).
8. **Pending references** retried (§4.7.5).
9. **Tags** (§4.7.7), including the street-light derivation for owners whose light flags changed (§6.4).
10. **Channels**: icon commands through one `IconCommandBuffer` (played back by `IconCommandSystem`); synthesised events (§4.10.3); system state `Deserialize` after the batch, with manual reader handles completed first (`TaxSystem.Readers`, R11 §5.3); cell maps; terrain and water tiles; keyed state (deferred zone overlays); mover lane sync (§4.9.7). The spawn payloads of this frame are kept for the end-of-frame re-application (§4.7.8).
11. **Inspection samples** whose tick the clock has reached (§4.7.9).
12. **Bookkeeping**: ledger hashes and bucket sums (§8.3), acknowledged epoch, the received-value cache and derived-reference index (§3.7), the "written this frame" bitset that local change capture uses to ignore the applier's own writes.

#### 4.7.3 Structural batch

One sync point per frame (every structural `EntityManager` call completes all jobs, R08 §9):

- **Liveness first**: every bound entity the program touches is checked with `EntityManager.Exists` (§3.3); a dead binding is unbound and its records are re-planned as for an unbound slot (a value is parked, a spawn creates).
- **Despawns**: `AddComponent(NativeArray<Entity>, ComponentType.ReadWrite<Deleted>())` (`EntityManager.cs:1544` [V]); never `DestroyEntity`. `CleanUpSystem` destroys at the end of the frame, and `SubElementDeleteSystem` handles sub-nets, sub-areas, sub-routes and owned vehicles of deleted owners in PostTool (R11 §6.3). The slot is unbound at once.
- **Spawns**: grouped by archetype (§3.6) and, for movers, by `UpdateFrame`: `CreateEntity(stagingArchetype, n, Allocator.Temp)` (`EntityManager.cs:2053` [V]) from the staging variant without `Created`/`Updated`; for movers `SetSharedComponent(NativeArray<Entity>, UpdateFrame)` (`:1190` [V]); slots are bound in the side tables. `Created` + `Updated` (+ `Applied` for movers) are added only in the finalise step, after the whole transaction succeeded, so no vanilla Modification system can ever see a zero-filled entity (an `Edge` with `Entity.Null` nodes and so on).
- **Upserts**: a `SPAWN` for a slot that is already bound is reconciled in place **only** if the binding's generation, family and prefab rank equal the record's. Otherwise the local entity gets `Deleted`, the slot is unbound, and a fresh entity is created from the correct archetype; the same rule applies to `SPAWN` flagged `Repair` and to the entity a `BIND` attached to a slot (a predicted entity of another prefab is replaced, not reconciled). Reconciliation = signature reconciliation plus value writes.
- **Signature changes**, grouped by `(from, to)`: `AddComponent`/`RemoveComponent(NativeArray<Entity>, in ComponentTypeSet)` (`:1575, 1584` [V]). Added = host set minus local types. Removed = local types that are `Serializable`, `EmptyTag` or data-replicated (§4.2.4) and absent in the host set. Presence of other `EmptyData` types is never removed, because local derivation owns it.
- **Moving↔parked swaps** use the vanilla type sets, including `Updated` (R15 §4.2).
- **Client liveness pass** (`ClientEndOfFrameSystem`, before `PrepareCleanUpSystem`): bound entities tagged `Deleted` this frame by anything other than the applier (sub-elements of a deleted owner, rollbacks, kept mod deletion systems) are unbound; a budgeted `Exists` sweep of about 20k slots per frame catches direct destructions. Later records for those slots then follow the unbound path instead of resolving to a reused index.

#### 4.7.4 In-place data writes

Records are grouped by `(type, chunk)`. Resolution follows §3.3: `EntityManager.Exists`, then `EntityManager.GetStorageInfo(entity)` (`EntityManager.cs:1386` [V]) for chunk and index, then `chunk.Has(ref handle)`; a failed check parks or drops the record and never writes. Per type, one RW `DynamicComponentTypeHandle` (refreshed each frame); per `(chunk, type)`, one `GetDynamicComponentDataArrayReinterpret<byte>` call gives the column base pointer and stamps the column's change version once. Each record then runs **decode-then-commit**:

```csharp
T* target = (T*)((byte*)columnBase + indexInChunk * sizeof(T));
T temp = *target;                              // keeps non-serialised runtime fields (R01 §11.2)
reader.Open(record.Payload, record.Offset, record.Length);   // bounded to exactly the record's bytes
temp.Deserialize(netReader);                   // merge-policy types: Merge(ref temp, incoming)
if (!reader.IsRecordConsumed) throw new DecodeException(...);   // all bytes read and no block left open
*target = temp;                                // only a complete, length-exact value reaches the chunk
```

A codec exception therefore leaves the live component untouched. Freshly created entities start from the archetype's zero-initialised value, so for them this equals "default then deserialise". Buffers decode every element (`default` + `Deserialize`) into a pooled scratch array first and verify the length; only then `ResizeUninitialized(n)` and a block copy, so no element with garbage `Entity` fields is ever visible. Enabled bits use `ArchetypeChunk.SetComponentEnabled(ref handle, index, value)` (`ArchetypeChunk.cs:441` [V]). Frames without a structural batch complete the written types' dependencies first (`query.CompleteDependency()`); at frame start few jobs are in flight. This replaces per-entity `SetComponentData` calls, each of which would complete dependencies again (J3, `EntityDataAccess.cs:748-757`).

The client computes the member hash of each applied value with the same xxHash64 over the received canonical bytes, so ledger and host shadow agree by construction.

#### 4.7.5 Pending references

When `Read(out Entity)` meets a slot that is not bound yet, the whole record (`slot, type, canonical bytes`) is parked in `PendingRefs` with its missing slots and written with `Entity.Null` for now. When a missing slot binds, the record is applied again (decode-then-commit once more). Time limits: references into S, G, A and M families 600 epochs (≈ 10 s), then a repair request for the referencing entity and the missing slot; references into A-low families 120 s without escalation, because those families arrive at P3. Derived keys are retried for 2 s and then handed to the tolerance logic (§8.5). While a pair is parked, the rolling verifier and the leak tracker treat it as **pending** (its live value legitimately differs from the ledger), so parking never triggers repair requests by itself (§8.3).

**Re-resolution.** A record that did resolve can go stale later, when the client regenerates the derived child it points at (a station stop sub-object replaced by a rebuild, a lane replaced by a local lane rebuild) while the host keeps its own entity and never re-sends the value. `ClientEndOfFrameSystem` therefore re-applies, from the received-value cache, every cached pair whose derived-reference index names an owner whose children were regenerated this frame (decode-then-commit again; the keys now resolve to the new children). A pair waiting for this re-application is pending as well. Typical volume: the waypoints of one station after its upgrade, the parked cars of one rebuilt street.

#### 4.7.6 Idempotency

- `SPAWN` for a bound slot is an upsert, reconciled in place only when generation, family and prefab match (§4.7.3).
- `DESPAWN` of an unknown slot is ignored.
- `VALUE` for an unknown slot is parked for up to 600 epochs, then dropped with a repair request.
- The epoch merger never lets a coalesced `VALUE` outlive a later `DESPAWN` of its slot or a later `STRUCT` that removes its type; such values are dropped during merging.
- Duplicate epochs are dropped by number; replayed records are harmless. A type-set definition for a known id must be identical; an unknown type-set id is a resync condition (§3.6).

This is what makes join boundaries, catch-up, repair and proposal acknowledgement simple (D7).

#### 4.7.7 Tag rules

| Operation | Tags added on the client | Reason |
|---|---|---|
| Create, non-mover | `Created` + `Updated` added in the finalise step (the staging archetype lacks them, §4.7.3); **no `Applied`**; `UpdateFrame` assigned locally | `Applied` + `Created` buildings would trigger local tool feedback (coverage search and feedback icons, `Game.Tools/ToolFeedbackSystem.cs:1302-1318, 1385-1420` [V]) and change LOD fading (`Game.Rendering/BatchInstanceSystem.cs:379, 425` [V]); `ParkedVehiclesSystem` is off on clients anyway; `UpdateFrame` only selects simulation slices, which clients do not run |
| Create, mover | host `UpdateFrame` at creation; `Created` + `Updated` + **`Applied`** in the finalise step | `UpdateGroupSystem` keeps the given `UpdateFrame` only with `Applied` (R15 §4.4) |
| Update of an S entity (`TOUCH` or source change) | `Updated` on exactly the host's touched set, plus the client-computed net closure (edge → both nodes; node → connected edges and their other nodes; aggregates) | derived systems key on `Updated`; the game does not propagate the closure (R02 §7) |
| Visual-only change (policy tag rule) | `BatchesUpdated` | avoids re-running `Updated` consumers |
| Effect-relevant change (`Car.m_Flags`, `Aircraft` flags, role `m_State` with boarding bits, `ParkedCar`/`Stopped` presence, `OnFire`, `Efficiency`) | `EffectsUpdated` (every `LayoutElement` member for trains) | `EffectControlSystem` re-evaluates only tagged entities (R15 §3.5) |
| Lane-relevant edge change (policies, `Upgraded`, traffic configuration) | `PathfindUpdated` mirrored | local previews use the client's pathfinding graph |
| Motion | **none** | `Updated` would reset all four `TransformFrame`s and re-randomise lane offsets (`UpdateGroupSystem.cs:561-615`, `Vehicles/InitializeSystem.cs:341`, R15 §0.4) |
| Moving↔parked swap | vanilla type sets including `Updated` | R15 §4.2 |
| Delete | `Deleted` on the entity and on every first-class child the host deleted; host touches on surviving neighbours | R11 §6.3 |
| Street-light flag change (`Road.LightsOff`, `Building.StreetLightsOff`/`Illuminated`, `Watercraft.LightsOff`) | `EffectsUpdated` on the owner's `StreetLight` sub-objects after deriving their state; `BatchesUpdated` on a building whose `Illuminated` changed; `EffectsUpdated` on the watercraft | mirrors what `StreetLightSystem` does on the host (`StreetLightSystem.cs:83-151, 195-238` [V]) |
| Events | only synthesised ones (§4.10.3): `Unlock`, `MilestoneReachedEvent`, `RentersUpdated` (only after the property's `Renter` buffer was applied in the same transaction), `ColorUpdated`, `SubObjectsUpdated`, `PathUpdated(segment, waypoint positions)` after a segment's path state was applied (§4.11.3), `RoadConnectionUpdated` from the road-choice reconcile (§4.11.5); never command events (`Damage`, `Ignite`, `Impact`, `AddHealthProblem`, `Modify`, `ResetTrip`, `PathTargetMoved`) | their consumers are off or would double-apply (R11 §2.4); the two derivation events drive kept presentation systems exactly as on the host |

#### 4.7.8 Post-creation fix-ups

- **Kept initializers** react to `Created`/`Updated` and change values on top of the host's payload: `Game.Net.InitializeSystem` resets `ServiceCoverage`/`ResourceAvailability` on created edges; `WaterSourceInitializeSystem` writes prefab water-source values; `Creatures.InitializeSystem` recomputes `Human.m_Flags` (`Cold` from the local temperature, `Homeless`) on updated creatures (R15 §4.3); `UpdateGroupSystem` resets the frames of created movers (the JIT writer refills them). Others double-apply host initialisation: `BatteryInitializeSystem` adds the initial charge to a spawned battery's `m_StoredEnergy` again and adds or removes the battery-empty icon (`Game.Buildings/BatteryInitializeSystem.cs:56-90` [V]); `CityServiceWorkplaceInitializeSystem` adds the efficiency grace period to the owner's `WorkProvider.m_EfficiencyCooldown` again and rewrites `m_MaxWorkers` (`CityServiceWorkplaceInitializeSystem.cs:103-120` [V]). A hand-kept list would keep missing such systems, so **re-application is the default**: `ClientEndOfFrameSystem` (after ModificationEnd, before `PrepareCleanUpSystem`) re-writes the canonical bytes of every replicated type of every entity spawned or upserted this frame from the payloads kept by the apply (except the types the motion writer owns on movers, §4.2.5), without tags, and then checks the entity's replicated signature against the host's type set (a difference is a leak, repaired next frame). Entities the applier changed structurally or tagged this frame without a full payload (a `STRUCT` swap, a `TOUCH`; for example a moving↔parked creature whose `Human` flags `Creatures.InitializeSystem` recomputes) and owners of spawned or deleted upgrades (`InstalledUpgrade`) are re-hashed against the ledger in the same pass; a difference becomes an immediate leak repair (no two-frame wait), logged with the writer the write auditor names. The policy flag `noReapply` opts a type out (none by default). The two double-applying systems above are additionally gated off (Appendix E), because their only effects are replicated values and icons. `TrafficLightInitializationSystem`'s reset of `TrafficLights` is undone right after it (§4.9.9). Test (M3): spawning every building family on a client yields canonical equality with the host right after the creation frame.
- **Aggregates**: `AggregateReconcileSystem` forces the host's membership and element order after `AggregateSystem` in the same frame, before `PrepareCleanUpSystem` destroys a merge loser (R13 §7.1).
- **Road connections**: `RoadConnectionReconcileSystem` (Modification4B, right after the kept `RoadConnectionSystem`) compares the host's road choice of each building (the deferred fields of `Building`, §4.2.5, handed over by the applier) with the local `m_RoadEdge`/`m_CurvePosition`. On a difference it moves the building between the two edges' `ConnectedBuilding` buffers, writes the host's fields and emits `Event + RoadConnectionUpdated{building, old, new}` through `ModificationBarrier4B`, which is what `RoadConnectionSystem` itself does (`RoadConnectionSystem.cs:596-645` [V]), so the Modification5 consumers (`SpawnLocationConnectionSystem`, `ElectricityRoadConnectionGraphSystem`, `WaterPipeRoadConnectionGraphSystem`, `SystemOrder.cs:210, 246-247` [V]) follow. It adds no `Updated` (that would re-run the local choice) and no icon (the warning is host data, §4.10.4). Building connection lanes built for a different local choice stay until the building's next update; they are lanes, visual-equivalent (§0.4).
- **Route path events**: after a segment's path state was applied, the program creates `Event + PathUpdated(segment, Position of waypoint i, Position of waypoint i+1)` at frame start, the same event the host's pathfinder produced; `SegmentCurveSystem` rebuilds `CurveElement`, `Routes.SearchSystem` and `RouteBufferSystem` refresh, and `RouteReadyBridgeSystem` sets the segment's path-target ready positions (§4.11.3).
- **Zone overlays** are applied at frame start to blocks that exist. Overlays whose block has not been regenerated yet (normally the block appears in the frame of the edge change) are retried in the following frames; after 2 s the owner edge is repaired (R13 §4.3).
- **Relationship changes**: when `PropertyRenter` or `CurrentBuilding` changes, the host promotes the record to P0 and re-captures the old and new properties' `Renter` buffers in the same transaction (§4.2.6). After applying that transaction's `Renter` buffer of a property, the client emits `Event+RentersUpdated` for it, so `SubObjectSystem` regenerates renter-dependent placeholders exactly as on the host (R13 §3.1). `RemovedSystem`, the other consumer of that event, is off on clients: it would remove `Renter` entries, clear `HighRentWarning` and remove icons locally (`Game.Simulation/RemovedSystem.cs:196-229` [V]).
- **Policies** arrive as state only; the host's own touches are mirrored, so no `Modify` event is ever replayed (R10 §4).

#### 4.7.9 Time-aligned inspection data

The info panel's periodic refresh is **relative to its last refresh**, not aligned: `UIUpdateState.Advance()` fires when `frameIndex − lastTick ≥ 256` and then sets `lastTick = frameIndex`; it also refreshes at once whenever the selected entity carries `Updated` or `BatchesUpdated`, and `RequestUpdate()` forces the next refresh (`Game.UI/UIUpdateState.cs:28-38`, `SelectedInfoUISystem.cs:181, 343-346, 383-386` [V]). The design uses that behaviour:

- **Host**: samples each peer's inspection closure (§4.8.3) in the first capture flush after its `frameIndex` crossed a multiple of 256 and stamps the sample with that tick. Non-sample updates of V/A values of closure entities are not sent to that peer in between. The host also calls its own `SelectedInfoUISystem.RequestUpdate()` at every sample tick, so its periodic refresh falls on the same phase as the clients'.
- **Client**: keeps the sample until its own clock reaches the stamped tick, applies it and calls `RequestUpdate()`; its next periodic refresh then also falls 256 ticks later, on the next sample tick.
- **What is aligned**: the simulation (V/A) values of the closure on every client, and on the host at each periodic refresh (forced at the sample tick). Structure (S) values apply on arrival, and a client panel may refresh in between because the applier tagged the selected entity (`Updated` for an S change, `BatchesUpdated` for a visual rule); such a refresh shows current S values and still the last sample's V/A values, because those were withheld. The host cannot withhold its live world: when its own selected entity is tagged (an upgrade, a policy change), its panel refreshes at once with live values, which the clients receive at the next sample tick. The design does not claim more than that.
- When a view report announces a new selection, the host sends one immediate sample (stamped with the current tick), so the panel, which opens instantly on local data, refreshes within one round trip. All other state applies on arrival.

#### 4.7.10 Budgets and backlog

- Apply budget 3 ms per frame (configurable); a transaction is never split across frames, even when it arrived as several epoch parts. A very large host edit (tens of thousands of objects) therefore costs the client one longer frame (a "Applying large change" hint shows when an apply exceeds 50 ms); this is accepted in exchange for atomicity.
- Workers merge consecutive epochs (structure kept in order, values coalesced to the latest per `(slot, type)`, superseded values dropped, §4.7.6), so the backlog is bounded by distinct dirty pairs, not by time.
- A backlog above 64 MB or 30 s (not counting one single epoch that is still being assembled from its parts, which is bounded by the 256 MiB epoch cap instead) triggers a hash-based resync (§7.8); if that fails, a snapshot resync.

### 4.8 Bandwidth budgeting and prioritisation

#### 4.8.1 Streams

| Stream | Lane | Reliability | Priority | Content |
|---|---|---|---|---|
| Control | R | reliable ordered | P0 | handshake, session, proposals and results, chat, repair requests |
| Epoch P0 | R | reliable ordered | P0 | transactions, existence, events, icons, prefab state, names, system state |
| Attention values | R | reliable ordered | P1 | inspection samples, in-view V/A, infoview topics, in-interest mover state, repair responses |
| Motion | U | unreliable | P2 | Q-B frames, entry baselines, pins |
| Rolling values | R | reliable ordered | P3 | all remaining dirty values, cell maps, poses |
| Bulk | B | reliable | lowest | snapshots, terrain and water tiles, domain rebaselines, resume points |
| Clock, view reports, presence | U | unreliable | P0 (small) | |

#### 4.8.2 Budget source

`LinkEstimator` polls `SteamNetworkingSockets.GetConnectionRealTimeStatus` every 100 ms (`m_nSendRateBytesPerSecond`, `m_cbPendingReliable`, `m_cbPendingUnreliable`, `m_usecQueueTime`, ping, quality; R06 §4.1). Per-peer budget `B = min(userCap, 0.85 × sendRate)`, then AIMD on queue time: above 150 ms → −20 %; below 30 ms while backlogged → +5 % per second. Defaults: per-client cap 300 KB/s over relay and 2 MB/s direct or LAN, floor 48 KB/s, host total cap 1.5 MB/s (session setting "upload budget"). The host total is split into equal per-peer shares and any share a peer cannot use is redistributed (work-conserving). With seven clients at speed 4, this is what lowers motion quality first (P-B §4.7.2, J3).

#### 4.8.3 Attention model

Each client sends a `ViewReport` (unreliable, 10 Hz or on a change above 5 %, about 40 bytes): camera pivot x/z, zoom, yaw, camera position, active infoview id and infomode mask, selected slot, followed slot, open-panel flags (including the transportation overview) and the panel's target slot (line, stop, district, budget, statistics). The host derives per peer:

1. **Motion interest**: a disc around the pivot with radius `clamp(k × zoom, 300 m, 2500 m)` plus the frustum footprint, entered at `r` and left at `1.25 r` (R15 §7.1). The radius never drops below the distance at which movers are still drawn [M].
2. **In-view static set**: at 1 Hz or after a large camera move, a query of the static object and net quad trees (`Game.Objects.SearchSystem`, `Game.Net.SearchSystem`; quad-tree iteration is public, P-B §4.7.4 [V-R]) produces a per-peer bitset over slots.
3. **Inspection closure** of the selected or followed entity, depth 1-2 (R15 §8.2): citizen → household, home, workplace, school, current vehicle or creature, pets; vehicle → layout members, passengers, owner, target, and on demand its dispatch requests and path; building → renters, occupants, owned vehicles, upgrades; line → route, waypoints, segments, stops (station stops by key), route vehicles and their passengers, and on demand vehicle paths; stop → its connected routes and their waypoints. Recomputed when a forward reference in it changes. Closure members join the host's hot set.
4. **Infoview topics**, mapped to what `ObjectColorSystem` actually reads (P-B §4.7.4, J1):

| Topic | Boosted data | Source of the mapping |
|---|---|---|
| Happiness, health, wellbeing, education, age | `Citizen` of residents of in-view buildings | `ObjectColorSystem` reads `Citizen` through `Renter` → `HouseholdCitizen` (`Game.Rendering/ObjectColorSystem.cs:904-965`, [V-R] P-B) |
| Wealth | `Household` and household `Resources` of residents | `ObjectColorSystem.cs:876-903` [V-R] |
| Profitability | `CompanyStatisticData` of renters | `ObjectColorSystem.cs:993-1017` [V-R] |
| Crime, mail, garbage, electricity, water | `CrimeProducer`, `MailProducer`, `GarbageProducer`, `ElectricityConsumer`, `WaterConsumer` | `ObjectColorSystem.cs:850-875, 984-992` [V-R] |
| Traffic | `Road` flow vectors of in-view edges | `Game.Net/Road.cs` |
| Service coverage | `ServiceCoverage` of in-view edges | R04 §4.1 |
| Pollution, land value, natural resources, groundwater, noise, telecom | the matching cell map (P1 delta), `LandValue` of in-view edges | R07 §8.2 |
| Electricity and water networks | consumer and producer components incl. their connection flags; the flow-status channel (flow, capacity, bottleneck and disconnected flags by flow key, §4.11.5) | R03 §6.11 |
| Transport (infoview or the transportation overview open) | line values of every line: `TransportLine`, `RouteVehicle`, segment `RouteInfo`, `WaitingPassengers`, `VehicleTiming`, stop state | R15 §8.2, §4.11.3 |
| Statistics panel open | `CityStatistic` buffers | R14 §6.2 |

#### 4.8.4 Scheduler and staleness promotion

Per peer, every frame, on a worker thread:

```
budget = tokenBucket.Take(frameDt)                 // bytes allowed this frame
queue all pending P0 into the peer's outbox (accounted; may drive the bucket negative; never dropped)
if the outbox's reliable window is full: build no P1/P3 section this frame (their dirty bits stay set)
remainder split P1 0.40 / P2 0.45 / P3 0.15 (work-conserving deficit round robin; unused share flows on)
P2  motion for this frame's tick groups, in order: followed/selected > in frustum near > in frustum far > outside frustum
P1  inspection samples > in-view V/A of the in-view static set > infoview-topic data > in-interest mover state > repair responses
P3  rolling cursor over the remaining dirty bits, class-weighted (V before A before A-low), then cell maps, water tiles, mover poses
```

**Staleness promotion** (P-A §3.8): a dirty page older than 2, 4, 8, 16 and 32 s is promoted one level, so every change is delivered within a bounded time even when the attention classes are busy. Measured staleness (p50/p95 per class) is shown in the network panel. With the default budget, unobserved building state converges in 4-20 s and unobserved agent state in 30-120 s [E].

#### 4.8.5 Degradation ladder and automatic speed cap

Under a small budget (P-B §4.7.5):

1. Far-field motion drops to 1/2, then 1/4 rate (the client extrapolates, then pins).
2. The motion radius shrinks towards 300 m.
3. The P3 share shrinks to 5 % (staleness grows; nothing is lost).
4. Water correction tiles pause.
5. Cell-map deltas are coalesced to one per map per minute.
6. P1 never drops below 30 % of what remains after P0.

P0 is never degraded. The P0 backlog of a peer is the bytes waiting in its outbox (§9.3) plus what Steam reports as pending. If P0 alone exceeds what a peer's link sustains for 30 s (that backlog stays above 2 s), the HUD tells everyone "Anna's connection cannot keep up at speed 3", and with the session option **automatic speed cap** (default on) the host lowers the session speed one step until that peer has had 60 s of headroom. If a peer's outbox exceeds 64 MB or 60 s anyway, the host disconnects that peer with the reason "connection too slow for this session"; it can rejoin.

### 4.9 Moving agents

#### 4.9.1 Existence: global for every mover

Every vehicle (including trailers and train members), creature (residents on foot, pets, wildlife), parked car and parked train exists on every client (K4). Spawns and despawns are P0. A 100k city produces roughly 150-300 mover lifecycle events per second at speed 1, about 8-20 KB/s; four times that at speed 4 (R15 §9.3) [E]. Global existence costs more than interest-scoped creatures (P-B), but every reference resolves (`CurrentTransport`, `CurrentVehicle`, `Passenger`), no UI path meets a missing entity, nothing pops in after a camera move, and hashing needs no out-of-scope semantics (J1). P0 overload is handled by the automatic speed cap (§4.8.5).

#### 4.9.2 Spawn recipe

From R15 §4.7:

1. Archetype by kind: moving (`ObjectData.m_Archetype`), stopped (`MovingObjectData.m_StoppedArchetype`), or train controller (moving or stopped); prefab rank mapped to the client prefab.
2. Host `UpdateFrame` + `Applied` (the frame phase must be exact, R15 §2.5).
3. Payload (the mover allow-list, P-C §3.3): `PrefabRef`, `PseudoRandomSeed`, role component (`PersonalCar`, `PublicTransport`, `DeliveryTruck`, service roles, ...), `Car`/`Train`/`Watercraft`/`Aircraft` flags, `Owner`, `Target`, `CurrentRoute`, `Transform`, `Moving`, `LayoutElement` (controllers), `Controller` (members, data-replicated, §4.2.4), current-lane keys and flags, `ParkedCar`/`ParkedTrain`, `Unspawned` presence (never `TripSource`, §4.2.5). Creatures add `Resident`/`Pet`, `CurrentVehicle`, `Human`/`Animal` flags, `HumanCurrentLane` key and flags, `GroupMember`.
4. Owners and targets exist before their movers (created in earlier frames, or earlier in the same transaction). A train controller and all its members arrive in one transaction.
5. The vanilla reference systems (Modification5) rebuild `OwnedVehicle`, `CarKeeper.m_Car`, `BicycleOwner`, `GuestVehicle`, `Passenger`, `CurrentTransport`, `OwnedCreature`, `RouteVehicle`, and the `LaneObject` or moving-search-tree insertion (R15 §4.5). `Vehicles.InitializeSystem` resets client-local navigation and lane offset (harmless); `Creatures.InitializeSystem` recomputes `Human.m_Flags`, which the end-of-frame re-application restores (§4.7.8).
6. Despawn = `Deleted`; the reference systems remove the links.

#### 4.9.3 Host motion sampling

Inside the capture flush (frame start, after `EndFrameBarrier`), for each tick `T` in `(lastSampled, frameIndex]` (0-8 per frame, `SimulationSystem.cs:255`):

1. Query movers with `SetSharedComponentFilter(new UpdateFrame(T & 15))`, exactly how the movers slice (`CarMoveSystem.cs:420-423`, R15 §7.2). About 1/16 of movers are touched per tick.
2. Read the `Transform` column through `GetDynamicComponentDataArrayReinterpret` with a pointer loop (no per-entity `EntityManager` calls).
3. Test the position against a 128 m grid bitmap of the **union** of all peers' interest regions (rebuilt at 10 Hz). Only movers inside the union go on.
4. For those, read `TransformFrame` slot `(T >> 4) & 3` and the raw hash of the discrete allow-list types (§4.9.4). Slots stay valid for 64 ticks, so reading at the next frame start is safe (R15 §7.2).
5. Append `(peer, interest slot, frame)` tuples for every peer whose region contains the mover; workers encode the datagrams.

Cost: at speed 4, up to 8 ticks per frame, about 10k `Transform` reads per frame for 21k movers plus frame reads for the 2-3k movers in the union [E]. Without the pointer loop and the union prefilter this would be 3-6 ms of managed code (J3); with them the target is 0.5-2 ms, budgeted, with the far field at half rate.

**Datagrams**: per tick `T` and peer: `u8 kind | u32 T | u16 seq | records`, at most 1200 bytes, sent unreliable no-delay. The tick is implicit because a datagram for `T` only carries movers whose `UpdateFrame == T & 15` (R15 §9.1). The unreliable lane may drop or reorder datagrams (R06 §5.2), and lanes are not ordered relative to each other, so **every record is decodable on its own**:

- **Q-B record header**: `u16` interest slot, then `u8 hdr = keyframe(1 bit) | refAge(3 bits) | assignGen(4 bits)`.
- **Keyframe** (~16 bytes): absolute position quantised to 1/64 m, yaw 12 bits, pitch 7 bits, speed 10 bits + sign, flags 18 bits, human state/activity/timer, all absolute. The host sends a keyframe for a mover on every fourth of its samples (once per 64 ticks, the life of the `TransformFrame` ring) and whenever a delta would need the absolute escape.
- **Delta** (~10-11 bytes): position delta at 1/64 m against the mover's **reference keyframe** at tick `T − 16·refAge` (not against the previous frame), with the absolute escape; yaw, pitch and speed as above; "flags unchanged since the reference" and "state unchanged since the reference" bits, otherwise the absolute values. The Q-A baseline frames of an interest entry also count as reference frames.
- **Client rule**: a delta is decoded only if the staging ring holds a non-synthetic frame for the reference tick with the same assignment generation; otherwise it is dropped and the mover extrapolates until the next keyframe (≤ 64 ticks, extrapolation is capped at 32 ticks and then pins, §4.9.5). Errors therefore stay bounded under loss and reordering; they never accumulate. Average cost rises from ~10.5 to ~12 bytes per frame (R15 §9.1 measured the delta-only variant).
- Followed and inspected movers carry exact f16 velocity (+4 bytes), because velocity shapes the Hermite curve (`ObjectInterpolateSystem.cs:4354`, R15 §2.2).
- Test (core-motion): decoding through `NetSimTransport` with 5 % loss and reordering keeps the position error bounded (≤ 1/128 m on decoded frames, no drift).

**Interest entry**: the peer receives a reliable `MotionControl` assignment (`u16` interest slot ↔ slot, `UpdateFrame`, 4-bit assignment generation, valid-from tick) and a **Q-A baseline** of all four slots (~120 bytes), so the client interpolates at once (R15 §7.2); the mover's dirty discrete state is promoted to P1. **Interest exit**: a reliable release (interest slot, generation) with a pin instruction (§4.9.5).

**Interest-slot reuse**: Q-B records reference interest slots on lane U while assignments travel on lane R, so a released interest slot is reused only after the client has acknowledged the release (the 10 Hz `Ack` carries the last applied `MotionControl` sequence), and every reuse increments the slot's generation. The client drops records whose generation does not match its current assignment of that interest slot, and records that reference an unassigned interest slot. A datagram for the old mover can therefore never be written into the new mover's staging ring.

#### 4.9.4 Discrete state and effects

The allow-list of §4.9.2 is captured in two ways: (a) for movers inside the union of interest, by the motion sampler's raw hash on every sampled tick, then canonical serialisation and the value shadow; (b) for all movers, by the SDC cycle (60 s). Either way a change sets the dirty bit for **every** peer (§4.5.3); the peers that see the mover receive it at P1, the others at P3. Clients add `EffectsUpdated` per the tag rules (§4.7.7), so sirens, headlights and boarding effects change on time (J1: never on a multi-second slice).

#### 4.9.5 Interest, pinning and pose refresh

- **Pinning** (P-A §3.9.3, J1): when a mover leaves a peer's interest, that client's JIT writer writes the last known pose into all four `TransformFrame` slots with zero velocity and sets `Transform` and `Moving` to match. The vanilla ring never wraps back 64 ticks and extrapolation never runs away. The mover stays visible but static, beyond the draw distance by construction (radius ≥ the mover LOD distance).
- **Pose refresh**: out-of-interest movers get a `POSE` record (host `Transform`, quantised) through the P3 rolling refresh, at most one per mover per 30 s [E]; the client re-pins. Far movers are therefore roughly in place when the player zooms out, and `Transform` stays within `PreCullingSystem`'s ~278 m slack (`PreCullingSystem.cs:1152-1171`, R15 §2.4).
- **Camera jumps**: newly covered movers show their pinned or refreshed pose until their entry baseline arrives (≤ 1 RTT).
- **Extrapolation cap**: a mover in interest whose frames stop arriving is extrapolated for at most 32 ticks, then pinned.

#### 4.9.6 Client staging, JIT slot writer, extrapolation and clock lag

- **Staging ring** per mover: the last 8 `(T, frame, assignGen, synthetic)` entries; out-of-order inserts are harmless; Q-A baselines fill four entries at once; only non-synthetic entries of the current assignment generation serve as delta references (§4.9.3).
- **`MotionWriterSystem`** (PreCulling, before `PreCullingSystem`, after `PrepareRendering` computed the render tick `R`): for movers in interest, with `u` its `UpdateFrame`: `n = R − u − 32`, `F1 = u + 16·(n >> 4)`; write `frame(F1)` into slot `(F1 >> 4) & 3`, `frame(F1 + 16)` into the next slot, and prefill `frame(F1 + 32)` (R15 §2.6). The vanilla ring then holds exactly what `ObjectInterpolateSystem` will read.
- **Extrapolation**: a missing frame is synthesised as `p + v · 4/15` with the same rotation and flags and `m_StateTimer + 1`, marked synthetic and replaced when the real frame arrives; without it the object would snap back 64 ticks (R15 §2.6 step 4).
- **Coherence**: after writing, `Transform.m_Position = p_latest + v_latest · 2/15`, `Transform.m_Rotation = q_latest`, `Moving.m_Velocity = v_latest`, never `Updated` (R15 §2.6 step 6).
- **Side effects**: `EffectsUpdated` when the OR of the light bits over the four slots changes (every `LayoutElement` member for trains); a `SubObjectsUpdated` event when a human's activity prop changes, `BatchesUpdated` for activity 24 (R15 §2.6 step 7).
- **Trains**: `TrainBogieFrame` slots are written with the same slot math from the latest replicated `TrainCurrentLane` front and rear lanes; bogies snap to lanes within 2 m, and unresolved lanes degrade gracefully to unsnapped bogies (R15 §3.2).
- **Clock lag `D`**: the client's `frameIndex` runs `D` ticks behind the host estimate (§6.1). The reader at render tick `R` needs `frame(F1 + 16)` with `F1 + 16 ≤ R − 16` (R15 §2.2-2.3); a frame of tick `T` arrives `L` ticks after `T` (one-way latency + jitter p95 + one host frame, converted with 60 × speed); and the render tick leads the client's simulation tick by up to 15 ticks (`RenderingSystem.PrepareRendering` clamps the offset to ±15, `RenderingSystem.cs:174-221` [V]). Hence

  `D = clamp(⌈L + max(0, renderLead) − 16 + 4⌉, 0, 160)`,

  with `renderLead = RenderingSystem.frameIndex − SimulationSystem.frameIndex` measured as a moving average, and `D` slewing by at most ±1 tick per second so time never visibly warps (P-A §3.9.3). Examples [E]: speed 1, 100 ms + 30 ms jitter → `L ≈ 9` → `D ≈ 0`; speed 4, 120 ms → `L ≈ 33` → `D ≈ 21` (88 ms). R15 §2.6's `D ≥ L + 8` ignores the 16-tick cushion of the 32-tick render delay; P-A's `L − 16` is right once the render lead is measured.

#### 4.9.7 Lanes, `LaneObject` and selection

- `ParkedCar`/`ParkedTrain` lane keys are persistent state.
- Current-lane keys of movers are sent at spawn and on change: P1 for peers that see the mover, P3 otherwise (R15 §5).
- `MoverLaneSync` (inside the client apply) moves a mover between the lanes' `LaneObject` buffers when its lane key changes, with the public static `NetUtils.AddLaneObject`, `UpdateLaneObject` and `RemoveLaneObject` (`Game.Net/NetUtils.cs:439, 452, 494` [V]). Vanilla `Vehicles.ReferencesSystem` inserts on `Created` and removes on `Deleted` (R15 §4.5). Movers stay clickable through `RaycastSystem`, which uses lane `LaneObject` buffers plus the moving search tree (R15 §5).

#### 4.9.8 Trains, trailers and parked swaps

Train controllers and members, and tractors with trailers, spawn and despawn in one transaction with `Controller`/`LayoutElement`. Parked↔moving swaps are `STRUCT` records with the vanilla type sets including `Updated`; `UpdateGroupSystem` then resets the frames and the JIT writer refills them (R15 §4.2). `ParkedVehiclesSystem` is off on clients; parked service vehicles are spawned only by the host and replicate as movers (§5.4.3).

#### 4.9.9 Traffic lights

**Host.** Signalised junctions in any peer's in-view set form a fast-path list in the hot set; their 6-byte `TrafficLights` value is compared every frame and a change is P1 for the peers that see the junction. `TrafficLightSystem` updates each junction once per 64 ticks (interval 4 with a 16-group `UpdateFrame` filter, `Game.Simulation/TrafficLightSystem.cs:731-750` [V]), so at speed 1 a junction changes at most about once per second. Other junctions converge through the SDC cycle (V, P3).

**Client.** `TrafficLightDeriveSystem` runs right after `TrafficLightInitializationSystem` (Modification4B, `SystemOrder.cs:182` [V]) for every junction whose `TrafficLights` changed or that the initializer just reset:

1. Re-apply the latest host `TrafficLights` value (the initializer resets it on created or updated nodes).
2. Skip junctions flagged `IsSubNode`, as the vanilla job does.
3. For every sub-lane with `LaneSignal` (for moveable bridges also the lanes of the connected edges' sub-nets, as the job does), call `TrafficLightSystem.UpdateLaneSignal(lights, ref signal)`, then clear `m_Petitioner` and set `m_Priority = m_Default`, exactly like the vanilla `UpdateLaneSignals` (`TrafficLightSystem.cs:441-452, 782` [V]).
4. For every sub-object with `TrafficLight`, call `UpdateTrafficLightState(lights, ref light)` (`:878` [V]).
5. For moveable bridges, call `UpdateMoveableBridge(lights, transform, bridgeData, ref pointOfInterest)` (`:977` [V]) and add `EffectsUpdated`.

Group masks come from the client's own `TrafficLightInitializationSystem`. A per-junction group-mask layout hash is part of the diagnostics; if it differs from the host's for a junction, that junction falls back to replicating its pole `TrafficLight` states through the K channel [M]. Light states apply on arrival. On the host, lights lead the interpolated cars by about 32 ticks; on clients by 16-32 ticks, which looks the same.

### 4.10 Global state, prefab state, icons and fields

#### 4.10.1 System state

`SystemStateRegistry` (data file `sysstate.json`, allow-list):

| Entry | Mechanism | Cadence |
|---|---|---|
| `TaxSystem` | own `Serialize`/`Deserialize` with the full-tag context, after `Readers.Complete()` | on change |
| `ResidentialDemandSystem`, `CommercialDemandSystem`, `IndustrialDemandSystem` | own `Serialize`/`Deserialize` | ≤ 1 Hz |
| `TradeSystem` | own codec | ≤ 0.25 Hz |
| `CityStatisticsSystem` | `Deserialize` only (sample count, last sample frame); **never** `PostDeserialize`, which can create or destroy statistic entities (R11 §5.2) | per sample |
| `Count*` systems, `CityProductionStatisticSystem`, production specialisation, electricity and water statistics and trade | own codec after a per-system audit; each entry must pass the in-game round trip (serialise, deserialise into the live system, re-serialise, identical bytes) before it is enabled | ≤ 0.25 Hz |
| `ClimateSystem` | custom codec of weather state and overrides | ≤ 0.2 Hz and on change |
| `WaterSystem` parameters (+ Water Features parameters through its adapter) | public setters | on change |
| `CityConfigurationSystem.cityName` | setter | on change |

Change detection hashes the serialised blob at each entry's cadence; a changed blob becomes a `SYS` record in the shared P0 section. Clients deserialise after the structural batch (which synchronised jobs), completing manual reader handles first (R11 §5.3).

**Entry identity and enabled sets.** A `SYS` record names its entry by a stable 32-bit hash of the entry's string id (for example `"TaxSystem"`), never by a position in the registry, so a different registry can never route a demand blob into `TaxSystem`. Each peer enables an entry only after its own in-game round trip passed, so the enabled sets of host and client can differ even with identical files: the host announces its enabled entry ids in `Welcome`; a client that has not enabled one of them disables that channel locally and shows "Degraded: <panel>"; a `SYS` record for an entry the client has not enabled is skipped, never decoded.

#### 4.10.2 Singletons, budget arrays and milestone fields

City, budget entity, `TimeData` and palette are G entities with normal codecs. `CityServiceBudgetSystem` is not `ISerializable`; its income and expense arrays and totals (`CityServiceBudgetSystem.cs:759-806`, [V-R] P-B) are read on the host at ≤ 1 Hz and written on clients by reflection (probe `P-BUDGET`; the `NativeArray` structs share memory with the system), with the system off on clients. Fallback: keep it on, fed by replicated inputs, and hash its public totals. `MilestoneSystem`'s cached UI fields (`m_LastRequired`, `m_NextRequired`, `m_Progress`, `m_NextMilestone`; `MilestoneSystem.cs:17-23, 41-51`, [V-R] P-C) are fed through probe `P-MILESTONE`, so the XP bar does not freeze (R14 §1.4); fallback: the bar shows 0 on clients.

#### 4.10.3 Prefab state and synthesised progression events

- **Host**: in the two capture flushes after a frame in which the tag sweep saw `Unlock` event entities (they are destroyed at the end of the frame that created them, so only the sweep before `PrepareCleanUpSystem` can see them, §4.4.1), and every 10 s as a safety net, scan the `Locked` enabled bits of all prefab entities into a bitset over prefab ranks and diff it; also `PlacedSignatureBuildingData` presence, `UnlockRequirementData.m_Progress` (≤ 0.5 Hz; not saved, reset on load, R14 §1.3), and the collected budget, fee and upkeep data on service prefabs (≤ 1 Hz). Output: `PFB` records.
- **Client unlocks**: create `Event+Unlock(prefab)` entities; vanilla `UnlockSystem` (kept on) flips the bit idempotently and every UI consumer refreshes, because they react only to `Unlock` entities (`PrefabUtils.HasUnlockedPrefab`, R14 §2.3). `UnlockSystem`'s requirement fixpoint over replicated progress reproduces the host's result under settings parity; the 30 s bitset audit repairs any difference (§8.6).
- **Client relocks** (rare, mods or repair): write the `Locked` bit directly, then a harmless `Unlock` event on an already unlocked prefab with UI marker data to refresh the UI (R14 §2.4 step 4) [M].
- **Milestones**: when `MilestoneLevel` rises from a to b, create `Event+MilestoneReachedEvent` for each i in (a, b]; each player's own popup setting applies (R14 §1.5).
- **XP toasts**: `XpMessageTapSystem` (host, `UpdateAfter<XpMessageTapSystem, XPSystem>(ModificationEnd)`) peeks `XPSystem`'s message queue without dequeuing (probe `P-XP`) and emits `EVT` records; clients call the public `MilestoneUISystem.AddMessage` (`MilestoneUISystem.cs:359` [V]).
- Tutorial-family prefabs and forced UI groups are excluded from the audit; tutorials are off in sessions (§2.5).

#### 4.10.4 Icons

The host captures `Icon` entities from the sweep (`Created`, `Deleted`, changed `Icon`/`Target`) and emits per-owner `ICON` add/remove records for default-layer icons that are not `Temp` or `Marker`; simulation `Transaction`-layer icons (level-up sparkles, meeting icons) become icon events (R14 §4.5). Owners and targets travel as `NetRef`s, so icons on derived entities (dead-end warnings on lanes, station stops) are addressed by their derived keys. Clients issue them through one replication-owned `IconCommandBuffer` from `IconCommandSystem.CreateCommandBuffer()` (`IconCommandSystem.cs:727` [V]), so vanilla maintains `Icon` entities, `IconElement`, clustering and animations. `IconCommandSystem` stays on (its queues would leak otherwise, R11 §4). `Add` is idempotent per owner, prefab and target (R14 §4.2).

**Icon authority filter (clients).** Several kept client systems also produce icons for persistent entities from their own derivation: `RoadConnectionSystem` adds and removes the "no road access" warning (`RoadConnectionSystem.cs:611-620` [V]), `BuildingPoliciesSystem` the "turned off" icon (`BuildingPoliciesSystem.cs:83-90` [V]), and so could any mod. Under D11 such icons must never be shown, and "identical inputs" is not a guarantee. `IconCommandSystem` keeps one private `List<NativeQueue<IconCommandBuffer.Command>> m_Queues` (one queue per `CreateCommandBuffer()` call) and a private `JobHandle m_Dependencies`, and drains all queues in its `OnUpdate` (`IconCommandSystem.cs:698-770` [V]). `IconAuthorityFilterSystem`, registered directly before it in ModificationEnd, reads both through probe `P-ICONQUEUES`, completes `m_Dependencies`, and rewrites every queue except the replication-owned ones (whose list indices it records when it creates them) according to the Core `IconCommandFilter`:

| Command (`IconCommandBuffer.CommandFlags`, `IconCommandBuffer.cs:10-19` [V]) | Kept? | Why |
|---|---|---|
| any command with the `Temp` flag | kept | tool previews and validation errors (`ValidationSystem.cs:1518-1522` [V]) are local by design |
| layer `Marker` | kept | presentation markers (`MarkerCreateSystem.cs:415-489` [V]), not status |
| layer `Transaction` | kept | short local feedback animations (`ToolFeedbackSystem.cs:936` [V]); host transaction icons arrive as icon events |
| `Update` | kept | location refresh of existing icons (`IconDeletedSystem`) |
| `Remove`/`All` whose owner or target is `Deleted` or gone | kept | bookkeeping of `IconDeletedSystem` (`IconDeletedSystem.cs:17-137` [V]) |
| every other `Add`, `Remove`, `All` | **dropped** | a local status decision; the host's version arrives on the icon channel |

The filter runs once per frame before playback, so every command passes it exactly once; dropped commands are counted per notification prefab (which names the producer in practice: road access, turned off, a mod's icon) and shown in the diagnostics ("local icon producers"). An adapter can declare notification prefabs that a local-only mod owns for its own presentation (`localIconPrefabs`); the filter keeps those. Pure icon producers are additionally gated off (`ConnectionWarningSystem`, `RoutePathReadySystem`, §6.4), which saves their CPU. **Fallback** if `P-ICONQUEUES` fails: the pure producers stay gated off, and the remaining local icons (`RoadConnectionSystem`, `BuildingPoliciesSystem`, mods) are corrected by the per-owner icon-set digest (§8.6), with brief flicker; the optional Harmony patch H1 (Appendix B) would remove that flicker and needs a decision record.

#### 4.10.5 Names

The host serialises `NameSystem` through `NetWriter` with its public `Serialize` (`NameSystem.cs:549` [V]) at 1 Hz; when the hash changed it diffs the `(NetRef, string)` pair list against the previous one and emits `NAME` records (P0); the city name is polled. Clients call `NameSystem.SetCustomName(entity, name)` (`NameSystem.cs:214` [V]) from `ClientUiApplySystem` in UIUpdate, where the `EndFrameBarrier` window is open (R10 §0.4). Generated names derive from the replicated `RandomLocalizationIndex`. Custom names of aggregates are re-applied by slot after aggregate reconciliation, because vanilla keys names by `Entity` (R13 §7.2).

#### 4.10.6 Events, chirps, journal, radio, statistics

Persistent event entities and journal entries replicate like other entities. `EventJournalInitializeSystem` and `EventJournalSystem` are off on clients; a client `JournalCompanion` keeps `EventJournalSystem.eventJournal` current and invokes its change callbacks (R14 §6.1; probe `P-JOURNAL`, fallback: the list refreshes when the panel is reopened). Chirps carry their `RandomLocalizationIndex`, and the initializer that would re-roll it is off; the `Liked` flag is masked and likes stay per player. Radio tags (later milestone): the host taps `RadioTagSystem` input events and clients enqueue them locally (probe `P-RADIO`). Statistics are V (§2.2).

#### 4.10.7 K channel: state on derived entities

| Family | Key (§3.7) | Replicated state | Applied |
|---|---|---|---|
| Zone cells | block key | `m_Zone` per cell as zone prefab rank (RLE), `Overridden` cells omitted | at frame start once the block exists, plus `Updated` on the block (mirrors `ApplyZonesSystem`, R13 §4.3) |
| Prefab sub-objects | sub-object key | `Tree` state, `Decoration`/`LumberResource` bits, player-authored overrides declared by adapters (Move It, Anarchy transforms), keyed deletions (Better Bulldozer records, consumed by that mod's own deletion system on clients) | in the frame after the owner's regeneration (`SubObjectSystem` runs in Mod2B) |
| Secondary objects | secondary key | tree growth; pole `TrafficLight` state only as the fallback of §4.9.9 | after Mod4B regeneration |
| Station stops (prefab sub-objects with `TransportStop`) | sub-object key | stop values `TransportStop` (active flag, comfort, loading factor; written by `TransportStopSystem`, `TransportStopSystem.cs:83-106` [V]), `BoardingVehicle`, `TaxiStand`, `WaitingPassengers`; the `ConnectedRoute` list (co-captured with waypoint `Connected`, §4.2.6) | stop links at P0 with the transaction that changed them; values at the V cadence, P1 under the Transport topic or an open line or stop panel |
| Gate-bypass routes (OwnedRoute) | owned-route key; owned-route element key for segments | `HiddenRoute` presence (simulation output of `RoutePathReadySystem`, §4.2.5); the segments' `PathTargets`, `PathInformation`, `PathElement` (path-result trigger, §4.4.2 step 3a) | after `Buildings.InitializeSystem` created the route on the client; then a synthesised `PathUpdated` (§4.11.3) |

Host detection: derived entities with tags in the sweep (regeneration or mod writes), the path-result trigger for gate-bypass segments, plus an SDC pass over K-listed types on derived families at the cadence of their class (stop values 4 s like V; tree growth 120 s). Which types are K types on which derived family is policy data; `TypeAudit` lists every serializable type with a `GameSimulation` writer that occurs on a derived family, so the list stays complete after patches. A key that does not resolve within 2 s triggers a per-owner repair.

#### 4.10.8 Cell maps

The host hashes each `CellMapSystem<T>` map on a tick aligned with its update interval (`GetMap(readOnly: true, out deps)`, `deps.Complete()`, R07 §8.1). A changed map becomes a new version in a four-version ring; each peer receives the XOR delta against the version it last acknowledged, zero-run-length coded and compressed, at P3 (P1 under the matching infoview topic). Clients write with `GetMap(readOnly: false, out deps)`, `deps.Complete()`, copy, `AddWriter(default)`. Maps: air, ground and noise pollution, groundwater, soil water, natural resources, land value, attractiveness, telecom, population and availability grids, and traffic ambience; zone ambience is recomputed locally by the kept `SpawnableAmbienceSystem` + `ZoneAmbienceSystem` (R11 §3).

#### 4.10.9 Terrain

- The host captures every applied brush stamp: its own (`ToolCaptureApplySystem` in its host role, first in ApplyTool, reading `Temp`+`Brush` entities, R07 §2.2) and those of client strokes, which it executes through the public `TerrainSystem.ApplyBrush` (`TerrainSystem.cs:3968`, [V-R] P-C). The private pinned-delta `ApplyToTerrain` is not used; tiles make the result exact anyway.
- **Frame-time compensation**: `ApplyBrush` scales every stamp by the calling machine's `Time.unscaledDeltaTime`, clamped to [0.05, 1] (`TerrainSystem.cs:3766-3775, 3968-3979` [V]), so a stamp replayed on a machine with another frame time would differ, and several stamps replayed in one frame would all use that frame's time. Each captured stamp therefore records the originator's clamped frame time `dt₀`. A machine that replays it (the host for a client stroke, other clients for forwarded stamps) pre-scales the brush strength so that `clamp(dt_local) · f(s′) = clamp(dt₀) · f(s)`, where `f` is the game's strength mapping (identity for |s| ≥ 0.002, `sign(s) · 0.002 · (|s|/0.002)^0.055` below); `s′` is obtained with the inverse of `f`. The math is a pure Core function with unit tests; an in-game self-test compares one stamp's height delta at a forced 15 fps and 60 fps.
- The host forwards stamps (with `dt₀`) to other clients (P0), which apply them locally through `ApplyBrush` for immediate visuals.
- After a stroke settles (200 ms without stamps), the host reads back the union dirty rectangle of `TerrainSystem.heightmap` with a non-blocking `AsyncGPUReadback`, splits it into 64×64 R16 tiles (8 KiB raw each) and sends changed tiles (xxHash64, zero-run-length, ZSTD) on the bulk lane.
- Clients upload into a cached R16 `Texture2D`, `Graphics.CopyTexture` it into `TerrainSystem.heightmap`, then call `OnAreaChanged`, `WaterSystem.TerrainWillChangeFromBrush` and `UpdateMinMax` (all public, R07 §5.2) [M].
- The authority check hashes `m_Heightmap` tiles only, never the cascade or CPU heights, which include GPU-rasterised roads (R07 §5.2); a rotating audit runs every 5 minutes and after strokes, with async readbacks on both sides.
- Object height re-snaps after terrain edits come from the host's `GroundHeightSystem` as coalesced S updates (§4.4.5).
- The material splatmap uses the same tile protocol at low priority (R07 §5.3).

#### 4.10.10 Water

Clients run `WaterSystem` for visuals with mirrored sources, sea level and parameters; the simulation pauses with the session speed. Correction tiles: the host reads one 256×256 tile of `WaterSystem.WaterTexture` round-robin with a non-blocking `AsyncGPUReadback`, plus "hot" tiles near recent source or terrain changes, quantises depth to u16 millimetres and pollution to u8 (velocity omitted by default; the client simulation re-derives it) [M], compresses, and sends on the bulk lane. Quality setting: Off / **Low (default: one tile per 16 s plus hot tiles)** / Medium (one per 4 s) / High (one per second, plus velocity as 2×f16). Client write path: `Graphics.CopyTexture` of the region into `WaterTexture`, flow update, and briefly disabled active-cell culling (probe `P-WATER`; fallback: tiles are skipped and water is purely client-simulated) [M]. Water gameplay (pumping, floods, damage, soil water) runs only on the host, so this affects visuals only (R07 §6).

#### 4.10.11 Snow, wind and surfaces

Client-local and cosmetic: snow arrives with the join snapshot and is simulated locally (R07 §7); wind cells are simulated locally; object wetness and dirt (`Game.Objects.Surface`) are excluded (§4.2.5). No periodic snow or wind snapshots are sent (J3).

### 4.11 Transport lines and status indicators (host-authoritative)

The user's requirement, from experience with another co-op mod in which a train line showed "not connected" for one player and "connected" for another: transport lines must look and behave identically for every player, and the same must hold for every other status indicator. Principle D11 applies: what the UI shows as a status is host data; a client derives only presentation from it.

#### 4.11.1 What the line UI shows and where it comes from [V]

| UI element | Vanilla source | Data it reads |
|---|---|---|
| Line list (transportation overview) and line panel | `TransportUIUtils.BuildTransportLine`, `GetStopCount`, `GetRouteLength`, `GetRouteVehiclesCount` (`Game.UI.InGame/TransportUIUtils.cs:41-140`), `LineSection.OnProcess` (`LineSection.cs:37-48`) | stop count = waypoints whose `Connected` target has `TransportStop`/`WorkStop`; length = Σ segment `PathInformation.m_Distance`; vehicles, cargo and capacity = `RouteVehicle` → `Passenger`/`Resources`; schedule and active = `Route` options; colour; visibility = `HiddenRoute` (per player) |
| Line diagram ("line visualizer") | `LineVisualizerSection` | a segment is drawn **broken** when its `PathInformation.m_Origin` and `m_Destination` are both null (`LineVisualizerSection.cs:580-585`); segment lengths from `PathInformation`, `RouteLane` and lane curves (`:703-721`); stops from `Connected` and `WaitingPassengers`; vehicles from `RouteVehicle` |
| Line warnings | `NotificationsSection` lists the non-Marker icons of the line **and of every waypoint** (`NotificationsSection.cs:111-121, 354-363`) | waypoint "path not found" = `RouteConfigurationData.m_PathfindNotification`, set and cleared by `RoutePathReadySystem` from the adjacent segments' `PathInformation.m_Distance < 0` (`RoutePathReadySystem.cs:229-262`); line "not enough vehicles" = `TransportLineData.m_VehicleNotification` with `TransportLineFlags.NotEnoughVehicles`, set by `TransportLineSystem` (`TransportLineSystem.cs:222-245`); network connection warnings on lanes and stops (§4.11.5) |
| Route-tool validation | `ValidationHelpers.ValidateRoute` | `PathfindFailed` error (warning on a complete line) when a temp segment's `PathInformation.m_Distance < 0` (`ValidationHelpers.cs:47-68`) |

#### 4.11.2 Why the line state diverges when clients derive it [V]

- **Segment paths are asynchronous pathfinding results.** `RoutePathSystem` (ModificationEnd, `SystemOrder.cs:254`) enqueues a search per updated segment (`RoutePathSystem.cs:552-635`) and also re-queues segments whenever a lane of their path is deleted or lanes are applied (`:387-510`), which on a client happens whenever it regenerates lanes after a replicated road edit. `PathfindResultSystem` writes the result without tagging the segment; a failed search clears `PathElement` and writes `PathInformation` with null origin and destination and distance −1 (`PathfindJobs.cs:1083-1131, 1237-1266`). The result depends on the local pathfinding graph and on timing.
- **Waypoint links are re-derived.** `WaypointConnectionSystem` (Modification5) sets `Connected` to null when the connected stop is missing or `Deleted` (`WaypointConnectionSystem.cs:1132-1140`), re-snaps `AccessLane`, `RouteLane` and `Position` from local lanes, and maintains the stops' `ConnectedRoute` lists (`:63-140`). Station stops are building sub-objects (`SubObjectSystem.cs:2549`), i.e. derived entities that a client regenerates on its own schedule.
- **Warnings are produced from local results.** `RoutePathReadySystem` (Modification1) turns path results into waypoint icons, toggles `HiddenRoute` on gate-bypass routes and writes the gate-bypass `Efficiency` factor (`RoutePathReadySystem.cs:136-275`).
- **Vehicle lists go stale.** `Vehicles.ReferencesSystem` maintains `RouteVehicle` only for `Created`/`Deleted` vehicles, while depots, transport AIs and `TransportLineSystem` edit it at runtime; `Passenger` likewise (§4.2.4).

A client that derived any of these would show "not connected", a wrong stop or vehicle count, or a stale warning while the host is fine: exactly the reported failure.

#### 4.11.3 Line state is host data

| Data | Class / capture / priority | Client |
|---|---|---|
| route: `Route` (incl. `RouteFlags.Complete`), `RouteNumber`, `Color`, `VehicleModel`, policies and modifiers, ordered `RouteWaypoint`/`RouteSegment` (data) | S / EDC / P0 | applied |
| waypoints: `Waypoint`, `Position`, `Connected`, `AccessLane`, `RouteLane` (lanes and station stops as derived keys) | S / EDC / P0 | applied; re-resolved after local regeneration (§3.7) |
| segments: `Segment`, path state `PathTargets`, `PathInformation`, `PathElement` (family-scoped policy, `source`, `cacheReceived`; `PathInformation` end points `presenceSafe`) | S / EDC + path-result trigger (§4.4.2 step 3a) / P0 | applied, then a synthesised `PathUpdated` |
| line values: `TransportLine` (flags `RequireVehicles`/`NotEnoughVehicles`, interval, ticket price), segment `RouteInfo`, waypoint `WaitingPassengers` and `VehicleTiming`, stop `TransportStop`, `BoardingVehicle`, `TaxiStand` | V / SDC (4 s) / P1 under the Transport topic, the transportation overview, or an open line or stop panel (time-aligned in the closure, §4.7.9), else P3 | applied |
| vehicle lists: `RouteVehicle` (route), `Passenger` (vehicle), stop `ConnectedRoute` (data, §4.2.4) | co-captured with `CurrentRoute`, `CurrentVehicle`, waypoint `Connected` (§4.2.6) in the same transaction; V/M scan as safety net | applied; kept reference systems' edits are no-ops |
| station stops (derived) | K by sub-object key (§4.10.7) | applied after regeneration |
| gate-bypass routes (derived) | K: `HiddenRoute` presence, segment path state (§4.10.7) | applied, then a synthesised `PathUpdated` |
| line and waypoint icons, connection warnings | icon channel, P0 | the only source of icons (§4.10.4) |

**Off on clients** (Appendix E): `RoutePathReadySystem` (replaced by `RouteReadyBridgeSystem`), `WaypointConnectionSystem` and `RoutePathSystem` (except inside a preview window, §4.11.4), `ConnectionWarningSystem`, plus the already gated `TransportLineSystem`, `TransportStopSystem` (GameSimulation), `BoardingVehicleSystem` and `Routes.InitializeSystem`. **Kept**: `SegmentCurveSystem`, `Routes.SearchSystem`, `RouteBufferSystem`, `Routes.ReferencesSystem`, `ElementSystem`, and `RouteDataSystem`/`RoutesModifiedSystem` (the local pathfinding graph used by previews).

**Presentation derived from host data.**

- After applying path state of a bound segment, the apply program synthesises `Event + PathUpdated(segment, Position of waypoint i, Position of waypoint i+1)` (§4.7.8), the event the host's pathfinder produced. Its consumers rebuild exactly what the host rebuilt: `SegmentCurveSystem` the segment's `CurveElement` (Modification5), `Routes.SearchSystem` the route search tree, `RouteBufferSystem` the render buffers (PreCulling) (`SegmentCurveSystem.cs:1453-1473`, `Game.Routes/SearchSystem.cs:77-80`, `Game.Rendering/RouteBufferSystem.cs:799-932` [V]).
- `RouteReadyBridgeSystem` replaces `RoutePathReadySystem` on clients with only its non-status branches: it copies each `PathUpdated` event's positions into the segment's `PathTargets.m_ReadyStartPosition`/`m_ReadyEndPosition` (runtime fields that are not serialised) and, in the first update after a session load, initialises them from the waypoint positions (`RoutePathReadySystem.cs:84-130, 385` [V]). It writes no icon, no `HiddenRoute` and no `Efficiency`.
- **Presence-safe path end points.** The broken display depends only on whether `PathInformation.m_Origin`/`m_Destination` are null. If a lane key in them does not resolve on a client, it decodes to a local stand-in (the waypoint's `RouteLane` lane, else the waypoint itself) instead of `Entity.Null`, so a key problem can never show a connected segment as broken; the pair is pending under the derived-key retry and tolerance rules (§3.7).
- **Re-resolution** (§3.7, §4.7.5): when a client regenerates a station's stop sub-objects or a street's lanes, the waypoints' `Connected`/`AccessLane`/`RouteLane` and the segments' path state are re-applied from the received-value cache, so no waypoint keeps pointing at a deleted stop.

#### 4.11.4 Route-tool previews on clients (guarded preview window)

Drawing or editing a line needs local `WaypointConnectionSystem` and `RoutePathSystem` results for the temp entities: snapping to lanes, the road-following preview and the `PathfindFailed` validation. A **tool-scoped gate** (§6.3) enables exactly these two systems while the client's active tool is `RouteToolSystem`, and for 2 s after it is deactivated or applied (so path results for the temp and predicted entities can land). Because they are not restricted to temp entities, the window has a guard:

- **`RouteGuardSystem`** (MainLoop, right after `PathfindResultSystem`, before the UI update; active only while a window is open or lingering): for every tracked route, waypoint and segment, and every stop's `ConnectedRoute`, that is not part of the local edit (stage-B original set, predicted creations, pairs of pending proposals), any live value of a `guarded` line-state type that differs from the received-value cache is restored with decode-then-commit before the UI reads it; restored segments get a synthesised `PathUpdated`. Detection is driven by chunk change versions of the guarded types on route-family chunks, so an idle window costs almost nothing. Local results therefore exist only for temp previews and for the player's own predicted line.
- The predicted line keeps its locally derived lanes and paths until the host's committed entities arrive (bind before create, §5.5.1); derived line state is host-only and never proposed (§5.2). The host's `SPAWN` of a committed segment carries a path that the host has not computed yet (empty `PathElement`, zero distance), so for 5 s after a `BIND` the apply planner holds such uncomputed path-state values of a bound prediction (op `Hold`, §4.7.2) and lets the host's first real path result overwrite the prediction; the originator's line therefore never flickers from its preview path to "no path" and back.
- If the guard is unavailable (its self-test failed), the window never opens: the client still draws lines, with straight preview segments and without local path validation, and sees the host's result one round trip after applying (Appendix G).

#### 4.11.5 Every other status indicator

| Indicator | What the UI reads | Authority on clients |
|---|---|---|
| Building warnings and problems (no electricity, water or sewage, garbage, crime, fire, sickness, death, abandonment, high rent, no customers or workers, turned off, ...) | icons on the building (`NotificationsSection`) and condition components | host icons only (icon channel + authority filter, §4.10.4); components S/V |
| Road access ("no road connection") and the building's address | the icon from `RoadConnectionSystem`; `Building.m_RoadEdge`/`m_CurvePosition` (address, connected state) | host icon only; the host's road choice applied by `RoadConnectionReconcileSystem` after the kept `RoadConnectionSystem`, with the `ConnectedBuilding` move and a `RoadConnectionUpdated` event (§4.7.8) |
| Network connection warnings: dead ends, track not connected, missing car/pedestrian/train/ship/bicycle connection of buildings and stops, water and sewage pipe and resource line not connected | icons from `ConnectionWarningSystem` (`ConnectionWarningSystem.cs:681-820, 2200-2260, 2355-2400` [V]), which writes nothing but icons (`:435`) | system off; host icons, lane-owned ones addressed by lane keys |
| Electricity and water connectivity | `ElectricityConsumer.m_Flags` (`Connected`, `NoElectricityWarning`, `BottleneckWarning`) and `WaterConsumer.m_Flags` (`WaterConnected`, `SewageConnected`) on buildings (`Game.Buildings/ElectricityConsumerFlags.cs`, `WaterConsumerFlags.cs` [V]); in the infoviews the flow edges' flow, capacity and `Bottleneck`/`BeyondBottleneck`/`Disconnected` flags (`Game.Simulation/ElectricityFlowEdge.cs`, `ElectricityFlowEdgeFlags.cs` [V]) | consumer flags V (host); **flow-status channel** (CH, per peer): while a peer has the Electricity or Water topic active, the host sends the flow-edge values of its in-view networks by flow key as `KEYED` records in that peer's `VAL` section (keyframe on activation, deltas on change, cadence ≤ 1 Hz), applied to the client's derived flow edges; icons host |
| Service coverage | `ServiceCoverage` buffers on edges in the coverage infoviews; coverage-related problem icons | V (host; the end-of-frame re-application undoes `Game.Net.InitializeSystem`'s reset, §4.7.8); icons host; the placement tool's coverage preview is local tool feedback |
| Street lights and traffic lights | owner light flags; node `TrafficLights` | host flags with local pole derivation (§6.4, §4.9.9) |
| Transport lines | §4.11.1 | §4.11.3 |

#### 4.11.6 Verification

- **Digest coverage**: every line-state type is a member of the S or V bucket sums (exact canonical hashes, `hashAsSet` for the data-replicated lists); icons are per-owner icon-set hashes in their owners' buckets (owners include lines, waypoints, buildings and, by their key's owner slot, lanes and stops); K state and flow status are hashed per key (§8.6).
- **UI-level line digest** (`LineStateProbe`, game-integrity): per line, one row of exactly what the vanilla UI computes, obtained by calling the same public functions: `TransportUIUtils.BuildTransportLine` (stops, vehicles, cargo, capacity, length, schedule, active), the broken flag per segment, the non-Marker icon prefabs of the line and of each waypoint, `TransportLine.m_Flags` and `Route.m_Flags`; plus a hash of the whole line list. `/st lines compare` requests the host's rows (sampled at the same host tick) and lists every differing line and field. The probe runs in the record/replay verdict, in the post-join verification sweep (with the S class) and, in diagnostics mode, every 60 s.
- **Acceptance test ST-LINES** (the user's scenario; two PCs, plus record/replay on one PC; scripts in IMPLEMENTATION-PLAN §8):
  1. (M3; the warning check from M5b, when icons replicate) The host builds two train stations, a track with a deliberate gap and a train line through both. Both players: the line panel, the line diagram and the line list show the same stops, length and broken segment, and the same "path not found" warning. The host closes the gap: both show the line connected at the same moment (≤ 1 s apart), warning gone; `/st lines compare` reports no difference.
  2. (M3) The host upgrades one station (its stop sub-objects regenerate) and moves a stop: the line stays connected on both screens; `/st lines compare` clean.
  3. (M4) The client draws a bus line, moves a waypoint and re-routes one of the host's lines to a neighbouring stop: the preview follows the roads and validates like vanilla; after applying, both screens show the same lines within one round trip, both stops list the re-routed line correctly, and the host's untouched lines never change on the client during the edit.
  4. (M5b warnings, M6 vehicles) With vehicles running: the vehicle count, usage, waiting passengers and "not enough vehicles" warning are identical in the line panel and the line list (time-aligned while the panel is open); the host deletes a depot: the warning appears on both.
  5. (M5b; infoview colours M5c) Status indicators: the host bulldozes the road in front of a building, cuts a power line and a water pipe and leaves a dead-end road; both players see the same road-access, electricity, water and dead-end warnings and the same electricity and water infoview colours; the client restores the road: the warnings disappear on both.
  6. Pass criterion for every step: the UI-level line digest and the per-owner icon digests are equal on both PCs, and the diagnostics dumps list no icon repair for the owners involved (an icon repair would mean a client showed an icon the host did not have, or missed one).

---

## 5. Client action path

### 5.1 Principle: predict with vanilla code, propose the difference

The game has 182 trigger bindings using eight different mutation mechanisms (R10 §0-2), and the user's mods add many direct writes outside the `Temp` pipeline (R09 §5.10). Re-implementing every handler would be large, brittle and never complete. On a client therefore:

1. The vanilla or mod code runs locally. That is the **prediction**, with exact vanilla behaviour: snapping, splits, auto-connected sub-nets, tool state machines that continue from real entities (R10 §7.3), sounds and UI feedback.
2. The **effects** on replicated state are captured at a few fixed points of the frame (§5.2), scoped by the game's own tags.
3. Effects that have a public vanilla executor on the host become **semantic intents** (the host calls the same API, so side effects such as triggers or `Updated` on the budget singleton happen naturally). All other effects become a **change set** in the canonical record format of §4.6.
4. The host validates, commits with the shared applier, and the authoritative result comes back through normal replication. The originator binds or rolls back.

The host's own player needs no intents: host actions change the authoritative world directly and are captured like simulation changes (§4.4).

### 5.2 Capture stages

| Stage | System and position | Sees | Produces |
|---|---|---|---|
| **A** frame start | step 2 of `ClientApplySystem`, after `EndFrameBarrier` playback and before host epochs are applied | effects of last frame's UI handlers that became visible through `EndFrameBarrier` (R10 §1): local `Event+Modify` (policies, out-of-service, ticket price, vehicle count, schedules, line state; the only producer is `PoliciesUISystem.ModifyPolicy`, R10 §3.1), `Event+Unlock` of dev-tree nodes, `Event+ColorUpdated`, `Deleted` on tracked entities that no host record deleted; **watched authorable types** on the selected entity and the open panel's target (untagged direct writes: `Building` historical flag, `VehicleModel`, `ServiceDistrict`, `CustomMeshColor`, `Followed`) compared with the ledger; **managed-state diffs**: `TaxSystem.GetTaxRates()`, `ServiceBudgetData`, City `ServiceFee`, `Loan` (after the client's own `LoanSystem` applied the queued action), map tiles that lost `Native` plus the money delta, `NameSystem` pair list (1 Hz), `cityName` | semantic intents |
| **B** apply frame | `ToolCaptureApplySystem`, first in ApplyTool (that phase runs only on apply frames, R10 §7.2 IP2) | the validated pre-apply `Temp` set: each `Temp.m_Original` (slot, revision, replicated signature) and `TempFlags`, plus the slots of each original's owned sub-elements (`SubNet`, `SubArea`, `SubRoute` and non-derived `SubObject` entries); Σ `Temp.m_Cost` without `Cancel`, computed as `ToolApplySystem` does (`ToolApplySystem.cs:42-74`); active tool id, prefab and mode; `Temp`+`Brush` stamps with the clamped frame time | tool header, the **original set**; `TerrainStroke` intents |
| **C** before Modification1 | `ToolCaptureResultSystem` (R10 §7.2 IP3) | the candidate set: (a) entities tagged `Applied` by the local apply (new entities `Applied`+`Created`; net originals `Applied`+`Updated`); (b) every member of the stage-B original set that now has `Updated` or `Deleted`, or a changed signature; (c) zone blocks tagged `Updated` whose original was in the stage-B set; (d) tracked entities created in this apply frame without `Applied` (mod apply systems) | opens a change set: creates get `LocalId`s, updates and deletes by slot, zone overlays by block key |
| **C2** end of frame | `ClientEndOfFrameSystem` (before `PrepareCleanUpSystem`) | the same entity set after Modification and mod post-processing (e.g. `AnarchyPlopSystem` adds `TransformRecord`, `PreventOverride`, `Elevation` after the apply, R09 §2.1) | re-reads the values, finalises and sends the change set |
| **D** direct-write tools | `ClientEndOfFrameSystem` | while a direct-write tool is active (`ToolSystem.activeTool` is not a vanilla tool type, or is a tool an adapter declares) and for 30 frames after: slot-bearing entities with `Created`/`Updated`/`Deleted` or a signature change that the applier did not write this frame, whose **player-authorable** types differ from the ledger; unbound tracked entities created by the tool | local-edit change set, coalesced per entity to ≤ 10 Hz while the tool is active; the final state is always sent |
| **E** vanilla controls | `BindingInterposer` (§5.7) | time controls, save/load/exit menu actions | speed requests, "save a copy", leave/end dialogs |

**Why the original set and not only `Applied`.** Only `ApplyNetSystem` tags updated originals `Applied` (`m_ApplyUpdatedTypes = {Applied, Updated}`, `Game.Tools/ApplyNetSystem.cs:901-903` [V]). `ApplyObjectsSystem` (`:595`), `ApplyAreasSystem` (`:173-176`), `ApplyRoutesSystem` (`:315-318`) and `ApplyZonesSystem` (`:104-107`) add only `Updated` to `Temp.m_Original` [V]; `Applied` goes only onto new entities. Building relocation, rebuild and upgrade in place, moved district or lot nodes and edited route waypoints would otherwise never become candidates and would be reverted as leaks. Tests (M4), one per apply system: objects (relocate, upgrade, rebuild), areas (node edit), routes (waypoint move), zoning; each asserts that the proposal contains the update and that the leak tracker stays at 0.

**Rules (scope-limited capture, K8):**

- Only the entity sets above are candidates. Each candidate is diffed against the ledger (the last host-confirmed canonical value, §8.3); only differences count.
- Differences on host-only types (`PlayerMoney`, `XP`, `DevTreePoints`, `MilestoneLevel`, simulation values, and the derived line state `AccessLane`, `RouteLane`, `PathTargets`, `PathInformation`, `PathElement`, `ConnectedRoute`, `RouteVehicle`) are never proposed. The local prediction (for example the money a tool apply deducted, or the path of a predicted line computed inside the preview window, §4.11.4) stays visible until the next host value overwrites it.
- Differences on player-authorable types (policy flag `playerAuthorable` and the authorable projection of mixed types, §4.2.5, plus adapters' `playerMutableTypes`) inside the candidate sets are proposed.
- Differences that only the host can produce correctly are expressed as **semantic intents**, not as values: a rebuild of a destroyed building or an upgrade in place (`TempFlags.Upgrade`) becomes `ReplaceObjectPrefab` (§5.3), because the vanilla apply also removes host-only types (`RescueTarget`, `Abandoned`, `Destroyed`, `Damaged`) and adds `PropertyToBeOnMarket` (`ApplyObjectsSystem.cs:378-397` [V]); the host performs those itself (§5.4.3).
- Every other difference is a **leak**: counted per type by `LeakTracker`, never proposed; if it persists for two frames, a repair request restores the host state (§8.5). Pairs that are pending for a known reason (parked references, held payloads, re-application, tolerated pairs, pending proposals) are never leaks (§8.3). Tracked entities created outside any capture window are deleted locally after 2 s.
- `GroundHeightSystem` is off on clients (§6.4), so terrain-driven height re-snaps cannot turn into proposals (fixes J2's flaw in P-C).
- Capture is disabled until the client is live, so load-time mutators can never become proposals (P-A §6.4).

### 5.3 Intent catalogue

Every UI action is predicted locally (K9).

| Intent | Payload | Stage | Host executor (phase) |
|---|---|---|---|
| `ChangeSet` (tool apply) | seq, base epoch, tool header (tool kind, prefab rank, mode, cost sum), base revisions, creates (`LocalId`, type set, payloads with provisional `NetRef`s), updates (slot, payloads), deletes (slot), touches, zone overlays | B + C + C2 | shared `WorldApplier` in the ingress hook of the capture flush (frame start) |
| `LocalEditChangeSet` (Move It, Tree Controller brush, Better Bulldozer, Zoning Toolkit, Water Features tool, Anarchy components tool, vanilla service-district selection tool, R10 §7.7) | same format | D | same |
| `TerrainStroke` | per apply frame: (terraforming type, `Brush` fields incl. strength and opacity, tool prefab rank for `Brush.m_Tool`, brush texture prefab rank, area, clamped frame time `dt₀`) per stamp, about 60 bytes each; ore, oil, fertility, groundwater and material stamps | B | `TerrainSystem.ApplyBrush` per stamp (`TerrainSystem.cs:3968`) with the frame-time compensation of §4.10.9, `TerrainMaterialSystem.ApplyBrush` |
| `ReplaceObjectPrefab` (rebuild a destroyed building, upgrade in place) | object slot, base revision, new prefab rank, cost | C (from a `TempFlags.Upgrade` original) | host executor reproducing `ApplyObjectsSystem`'s upgrade path (§5.4.3) through the shared applier, frame start |
| `SetPolicy` | target slot, policy prefab rank, active, adjustment (from the local `Event+Modify`) | A | `PoliciesUISystem.SetPolicy(target, policy, active, adjustment)` (`PoliciesUISystem.cs:186` [V]) in `HostCommandSystem` (UIUpdate) |
| `SetTaxRates` | changed (index, value) pairs of the 92-int array | A | `TaxSystem.SetTaxRate(TaxAreaType, int)` (`TaxSystem.cs:482` [V]), `SetResidentialTaxRate(jobLevel, rate)` (`:684` [V]) and siblings, after `Readers.Complete()`, so vanilla clamping applies (R10 §3.3) |
| `SetServiceBudget` | service prefab rank, percent | A | `CityServiceBudgetSystem.SetServiceBudget(prefab, percent)` (`CityServiceBudgetSystem.cs:1084` [V]) |
| `SetServiceFee` | resource, fee | A | `ServiceFeeSystem.SetFee(resource, fees, value)` on the City `ServiceFee` buffer (public static, `ServiceFeeSystem.cs:508` [V]) |
| `ChangeLoan` | target amount | A | `LoanSystem.ChangeLoan(amount)` (`Game.Tools/LoanSystem.cs:130` [V]), re-clamped against host money |
| `PurchaseDevNode` | node prefab rank | A | `DevTreeSystem.Purchase(Entity)` (`Game.City/DevTreeSystem.cs:157` [V]), which validates points, lock state and requirements |
| `PurchaseMapTiles` | tile slots, observed cost | A | re-implemented purchase: permits (`MapTilePurchaseSystem.GetAvailableTiles`, `:255` [V]), money, tiles still `Native`; then `PlayerMoney.Subtract` (`Game.City/PlayerMoney.cs:38` [V]) and `MapTilePurchaseSystem.UnlockTile(entityManager, tile)` (public static, `:346` [V]); vanilla `PurchaseSelection` needs the host's selection tool (R10 §3.4) |
| `Delete` (district, service upgrade, line from the UI) | slot, revision | A | `Deleted` through `EndFrameBarrier` in UIUpdate, as vanilla |
| `Rename`, `SetCityName` | target slot, string (≤ 64 characters) | A | `NameSystem.SetCustomName`, `cityName` setter (UIUpdate) |
| `SetRouteColor` | route slot, colour | A | equivalent of `ColorSection.OnSetColor`: route and `RouteVehicle` colours + `Event+ColorUpdated` (R10 §3.5) |
| `SetMeshColor`, `SetHistorical`, `SetVehicleModels`, `SetServiceDistricts`, `EditPalette` | the full new value (buffers sent whole, avoiding `RemoveAtSwapBack` order divergence, R10 §5) | A | direct write plus the vanilla tags (`BatchesUpdated` / `Updated`), frame start |
| `Follow` / `Unfollow` | citizen slot | A | `LifePathEventSystem.FollowCitizen` / `UnfollowCitizen` (shared 50-entry limit) |
| `SpeedRequest` | speed index or pause | E | session speed policy (§5.7) |
| `Presence` | cursor, tool, prefab, ghost outline (§11.4) | UI, 10 Hz | relayed unreliably |
| `ViewReport` | §4.8.3 | UI, 10 Hz | attention model |
| `Chat`, `PingMarker` | text ≤ 500 characters; world position | UI | relayed and logged |

Mod settings buttons that change the whole world (Plop the Growables "all historical", Tree Controller "destroy foliage", Better Bulldozer "safely remove", Water Features cleanup) are declared host-only by their adapters; on clients the capture reverts their effects and shows "only the host can run this action" (P-A §4.3).

**Prediction policy.** Tools, zoning, districts, routes (with the guarded preview window, §4.11.4), upgrades, bulldozing, Move It, Tree Controller, Line Tool, Anarchy: predict. Terraforming: predict, then host tiles converge. Policies, taxes, budget, fees, loan, names, colours, vehicle models, service districts, dev tree, map tiles, follow: predict. Money, XP, dev points, milestones: never (host values). Speed: no. **No-predict gate** (viewer role, or the host setting "authoritative wait"): `NoPredictGateSystem` rewrites the active tool's `applyMode` from `Apply` to `Clear` through its protected setter just before `ToolOutputSystem` (`ToolBaseSystem.cs:182`, probe `P-APPLYMODE`; `Apply → None` is unsafe, R10 §7.4); fallback: a viewer's local applies are reverted as leaks.

### 5.4 Host validation and commit

#### 5.4.1 Validation

`ProposalValidator` (Core: pure rules over facts supplied by the game layer):

1. The sender is live and its role allows the category (§5.6); rate and size limits hold (≤ 50 proposals per second per player; a change set may arrive in parts of ≤ 4 MB, ≤ 64 MB and ≤ 200k entities in total, validated only once complete).
2. **Conflicts**: every updated or deleted slot, and every existing slot a create references, still has the base revision stated in the proposal. Revisions count authored changes only (§3.8), so simulation writes never cause a conflict. Zone overlays check the per-block-key revision. The host player's own edits bump revisions without a round trip, so the host wins by acting first.
3. **Existence**: referenced slots are alive; provisional references resolve inside the proposal.
4. **Types**: updates contain only player-authorable types (mixed types are committed through their authorable projection, §4.2.5); creates belong to families allowed for the category (no agents; movers only where an adapter allows it).
5. **Prefabs**: created prefabs are unlocked (`Locked` disabled) unless unlock-all is on. **Every `PrefabRef` change** (in-place swap or `ReplaceObjectPrefab`) keeps the family and the **prefab class**: the host computes per prefab rank, at session start, a class signature over the presence of the prefab-data types that vanilla jobs index by prefab (`ObjectData`, `StaticObjectData`, `BuildingData`, `BuildingExtensionData`, `ServiceUpgradeData`, `SpawnableBuildingData`, `NetData`, `NetGeometryData`, `RoadData`, `TrackData`, `AreaData`, `RouteData`, `TreeData`, `PlantData`, ...); the new prefab must have the same signature as the old one and be unlocked. Adapters may declare additional allowed swap classes (Tree Controller). Without this rule, an edge whose `PrefabRef` points at a building prefab would make every peer's Burst jobs read `NetData` that does not exist, with safety checks stripped (R08 §1).
6. **Geometry consistency**: after the proposal's own updates, every edge's `Curve` end points lie within 0.1 m of its start and end nodes' positions, and every created edge references nodes that exist or are created by the proposal.
7. **Economy**: cost ≥ 0; `PlayerMoney.money ≥ cost` unless `m_Unlimited` (`Game.City/PlayerMoney.cs:13` [V]); the cost is cross-checked against prefab construction costs within a tolerance where computable (R10 §8.1: trust within tolerance in v1).
8. **Area**: created objects lie inside owned map tiles (host area query), unless host settings allow otherwise (Anarchy).
9. **Sanity**: finite floats, positions inside the 14.3 km playable area, sane buffer lengths.

Any violation rejects the whole proposal.

#### 5.4.2 Commit

On receipt (network thread) the host immediately echoes `ProposalReceived{seq}` on the control lane, so the originator's timeout measures host processing, not queueing (§5.5.2). The commit itself is the **ingress hook** of the capture flush (`HostIngress.CommitInsideWindow`, step 9 of §4.4.2): it runs at frame start inside the capture window, after every read of the flush and before `ToolSystem`, so engine state is never mutated concurrently with the workers (§4.5.5) and the host player's tool is never disturbed: the host never touches the `Temp` namespace, and if a remote deletion invalidates the host's preview, vanilla `OriginalDeletedSystem` forces a rebuild (R10 §7.3, `OriginalDeletedSystem.cs:31-49`).

- **Creates** through the shared `WorldApplier` from prefab archetypes, with provisional references resolved, **without `Applied`**: the host's own `ParkedVehiclesSystem` then spawns parked service vehicles for new buildings (its query excludes `Applied`, `ParkedVehiclesSystem.cs:1683-1700`, R15 §4.6), and `UpdateGroupSystem` assigns load-balanced `UpdateFrame`s. Slots are bound at once with `spawnEmitted = false`, so the next capture flush emits their `SPAWN` for every peer (§4.4.2; test: client A's committed entity appears on client B).
- **Updates**: authorable types written (mixed types through `MergeAuthorable`), `Updated` plus the net closure, revisions bumped. **Deletes**: `Deleted` plus closure touches.
- **Zone overlays**: `Cell.m_Zone` written on the matching host blocks plus `Updated` (mirrors `ApplyZonesSystem.cs:104-140`, R13 §4.2).
- **Terrain strokes**: `TerrainSystem.ApplyBrush` per stamp.
- **Semantic UI intents** that create `EndFrameBarrier` command buffers are queued to `HostCommandSystem` in UIUpdate, because `SafeCommandBufferSystem` throws before `AllowBarrier<EndFrameBarrier>` (R10 §0.4). Explicit-target overloads are used, so the host player's selection never changes.
- A `RESULT` record and the `BIND` records, both stamped with the originating peer, are queued into the current epoch's transaction (front of the TX section); the `SPAWN`s of the committed creations follow in the next epoch.

#### 5.4.3 Host-only side effects reproduced explicitly

A direct commit skips some apply-only bookkeeping of the vanilla `Apply*System`s (R10 §7.3); the host reproduces it in `HostSideEffects`. The list below was built by reading every apply system (`ApplyNetSystem`, `ApplyObjectsSystem`, `ApplyAreasSystem`, `ApplyRoutesSystem`, `ApplyZonesSystem`) for effects other than copying the temp's components:

- **Money**: the validated cost is subtracted from `PlayerMoney`, as `ToolApplySystem` would.
- **Refund window**: `Recent` (`m_ModificationFrame`, `m_ModificationCost`, `Game.Tools/Recent.cs` [V]) is set on created and modified objects and nets with the vanilla refund arithmetic (`ApplyObjectsSystem.cs:561-596`, `ApplyNetSystem.cs:650-681` [V]).
- **Triggers**: `TriggerAction` is queued through `TriggerSystem.CreateActionBuffer()` (`TriggerSystem.cs:405` [V]) when the trigger system is enabled (pattern of `ModifiedSystem.cs:412, 441-448`): `ObjectCreated` with the prefab's instance count for created objects (`ApplyObjectsSystem.cs:432-438` [V]) and `BicycleRoadBan` when a committed `Upgraded` change adds `ForbidSecondary` (`ApplyNetSystem.cs:682-687` [V]), so chirps and tutorials about remote builds work (fixes the gap J1 noted in P-C).
- **Moved path targets**: a committed `Transform` change of a `Building` or `TransportStop`, and a committed `Position` change of a route `Waypoint`, creates an event entity `Event + PathTargetMoved(original, oldPosition, newPosition)` (same archetype as `ApplyObjectsSystem.m_PathTargetEventArchetype`, `ApplyObjectsSystem.cs:608-663, 923`; `ApplyRoutesSystem.cs:322-333, 619` [V]). The host's `PathOwnerTargetMovedSystem` (ModificationEnd) then re-paths citizens and vehicles that are on their way to the old position.
- **Re-linked waypoints**: a committed change of an existing waypoint's `Connected` target (including adding or removing `Connected`) removes the waypoint from the old stop's `ConnectedRoute` list and adds it to the new stop's, as `ApplyRoutesSystem`'s `PatchTempReferencesJob` does (`ApplyRoutesSystem.cs:21-72, 334-346` [V]); the host's `WaypointConnectionSystem` maintains the lists only for created and deleted waypoints (`WaypointConnectionSystem.cs:63-140` [V]), so a direct commit would otherwise leave both lists wrong on every peer.
- **Moved movers** (only where an adapter allows moving them): the current-lane component gets its `Obsolete` flag and `Moving` is reset, as `ApplyObjectsSystem.Update` does (`:612-659` [V]).
- **Parked vehicles in the applied set**: a parked car or train that is itself part of a committed relocation gets `FixParkingLocation{m_ResetLocation = entity}` unless it is a non-controller train member (`ApplyObjectsSystem.cs:363, 417, 442-451` [V]).
- **Rebuild and upgrade in place** (`ReplaceObjectPrefab`): the executor reproduces the `TempFlags.Upgrade` path (`ApplyObjectsSystem.cs:378-397, 404-411` [V]): validated `PrefabRef` swap (§5.4.1); `Destroyed`/`Damaged` removed when the new state has none; `RescueTarget` and `Abandoned` removed; `PropertyToBeOnMarket` added to signature buildings that are neither on the market nor about to be; the mesh-colour buffer reset; when the new prefab has no `SubLane`/`SubObject` buffer, the old sub-lanes and owned sub-objects get `Deleted` (`RemoveOldSubItems`, `:453-470` [V]); `Updated`. The client never proposes those host-only types as values.
- **Deleted objects with kept sub-nets**: when a committed delete removes an object, its sub-nets that the tool kept lose `Owner` and get `Updated`, as `ApplyObjectsSystem.Delete` does (`:484-505` [V]); the client proposes this as the authorable signature change it observed (stage C, set (b)).
- **`BuildOrder`**: the proposal's ranges are re-based onto the host counter. `GenerateEdgesSystem` advances its counter past every created edge's `BuildOrder` (`UpdateBuildOrderJob`, `Game.Tools/GenerateEdgesSystem.cs:1919-1942` [V]), and vanilla `ResetBuildOrderSystem` compacts all build orders and resets that counter on every load (`Game.Serialization/ResetBuildOrderSystem.cs:62-148, 188-201` [V]). The host's `BuildOrderAllocator` keeps the monotonic maximum (+1) of every `BuildOrder` observed since session start and assigns new ranges that preserve relative order and widths; committed edges then advance the host's own counter through `UpdateBuildOrderJob`. The allocator's value is also the snapshot manifest's `hostBuildOrderNext`, from which joiners set their counter (§7.4). Unique values keep node-lane order and zone-block priority deterministic (R13 §2.4, §4.1).
- **Parked service vehicles**: spawned only by the host (creates without `Applied`), replicated as movers; `ParkedVehiclesSystem` is off on clients.
- **XP**: the host's `XPBuiltSystem` grants XP for created entities (R14 §1.1); XP replicates as state.
- **Initializer outputs** (default policies, `RouteNumber`, `VehicleModel`, `RandomLocalizationIndex`) are produced by the host's initializers on committed entities and replicate back; the client's prediction lacks them for one round trip (P-C §4.7).

### 5.5 Results on the originator

#### 5.5.1 Accepted: bind before create

`RESULT{origin, seq, Accepted}` and `BIND(origin, localId → slot, gen)` precede the `SPAWN`s of those slots (same or next epoch). Only the client whose peer id equals `origin` acts on them; every other client skips them. The client binds each predicted entity in its side tables, so the following `SPAWN` records upsert onto the predicted entities: no duplicate, no flicker, and any host difference overwrites the prediction (P-C §4.6). If the host's `SPAWN` names another family or prefab than the predicted entity has, the prediction is replaced rather than reconciled (§4.7.3).

#### 5.5.2 Rejected or timed out

The rollback plan recorded at capture runs: predicted creations get `Deleted`; modified or deleted originals are restored through a repair request (the host answers with full records flagged `Repair`, which upsert or re-create them); predicted managed state (tax, budget, fee, loan, names) is restored from the ledger; predicted unlocks are reverted with the relock path. A toast names the reason ("conflicts with Anna's edit", "not enough money", "no permission", "outside your map tiles"), and predicted entities flash once in the presence overlay.

**Timeouts** measure host processing, not known delays:

- The 10 s timer starts when `ProposalReceived{seq}` arrives (the host echoes it on receipt, §5.4.2). Before that echo, only the connection-level timeout applies (30 s).
- Timers are suspended while the host has announced `HostBusy` (snapshot or save stalls, §6.2) and resume with the remaining time after `HostReady`.
- A timed-out proposal leaves a **tombstone** for its seq (kept 5 minutes). A late `RESULT{Accepted}` for a tombstoned seq is logged and its `BIND`s are ignored, so the following `SPAWN`s create fresh entities instead of binding to entities that the rollback already marked `Deleted`; the originals the rollback restored are re-sent by the host anyway. A late `Rejected` for a tombstoned seq is ignored. Test (core-intents): late acceptance after a timeout produces exactly one entity per created slot and no structural fault.

#### 5.5.3 Echo suppression

Once the plain upsert path is proven (milestone M4), the host encodes the originator's copy of the P0 section with `BIND` records instead of full `SPAWN` payloads for that player's committed entities, plus `VALUE` records only for `(slot, type)` pairs whose canonical hash differs from the proposal's bytes. All other peers share the common encoding (P-B §5.1).

#### 5.5.4 Held payloads

While a proposal is pending, incoming host values for the `(slot, type)` pairs it covers are held instead of applied. When the result arrives, only the newest held value per pair is applied, or it is dropped if the result's own records supersede it (P-C §4.6). Without this, a slider would visibly jump back to the old value for one round trip.

### 5.6 Conflicts, permissions and abuse limits

- **Concurrency**: optimistic, host-ordered, atomic per proposal. Two clients editing the same entity: the later proposal's base revision is stale, it is rejected and rolled back. Zoning conflicts are checked per block key. Economy setters are last-writer-wins.
- **Roles**: Host, Co-host, Builder, Viewer. **Permission flags**: build, demolish, terraform, zoning, economy (taxes, budget, fees, loan), policies, progression (dev tree), map tiles, speed, mod tools, names, chat. Each role has defaults; the host can override them per player. Viewers get the no-predict gate.
- **Limits**: ≤ 50 proposals per second per player, change-set parts ≤ 4 MB (≤ 64 MB and ≤ 200k entities per proposal), chat ≤ 5 messages per second; violations are logged, and repeated violations of these limits kick the player (§14.3). Repair traffic is different: it is generated automatically, so the host throttles it and never kicks for its rate (§8.5).

### 5.7 Speed control and interposed vanilla controls

- **Session speed policy** (host setting): host only / host and co-hosts (default) / anyone / majority vote.
- **`BindingInterposer`** (probe `P-BINDINGS`): during a session, the vanilla trigger bindings are found by group and name in `GameManager.instance.userInterface.bindings`, removed with `IBindingRegistry.RemoveBinding` (`Colossal.UI.Binding/IBindingRegistry.cs:7` [V]) and replaced by a proxy binding with the same group and name; the originals are restored at session end (P-A §10.4). The vanilla JavaScript keeps calling the same path.

| Binding | On clients | On the host |
|---|---|---|
| `time.setSimulationPaused`, `time.setSimulationSpeed` (registered by `TimeUISystem`, `TimeUISystem.cs:94-95` [V]) | `SpeedRequest` to the host | sets the session speed (re-asserted every frame, §6.2) |
| menu save, quicksave | "Save a copy of the host's city" (§7.10) | original handler + `HostBusy(Save)` broadcast |
| menu load, quickload, continue, new game, editor | dialog "Leave the session first?"; on confirm the session ends, the originals are restored, and the player repeats the action | dialog "End the session for everyone?", same flow |
| exit to main menu | dialog, then leave and `GameManager.MainMenu()` | dialog, `SessionEnd` broadcast, then `MainMenu()` |

The menu binding names are resolved at runtime [M]; the pause-menu module paths are not visible in C# (R05 §14).

- **Fallback** when the probe fails: time controls act locally, and `SpeedMirrorSystem` turns a `selectedSpeed` change that is not the forced-pause barrier (observer count of `TimeUISystem`'s `simulationPausedBarrier` event binding, `TimeUISystem.cs:93` [V]) into a `SpeedRequest`, then re-asserts the session speed (P-B §5.3). Leaving is detected in `OnGamePreload` of any non-session load and ends the session cleanly. Client saves are allowed and labelled as a copy. Correctness never depends on the interposer, only comfort (J2).

### 5.8 Latency summary

| Event | Visible to the actor | Visible to others |
|---|---|---|
| Tool apply, local edit, UI change on a client | immediately (prediction) | RTT/2 to the host + ≤ 1 host frame + RTT/2 |
| Rejection | snap-back after one RTT, with a toast | nothing |
| Host player's own edit | immediately | ≤ 1 host frame + RTT/2 |
| Speed change request | after one RTT | same moment for everyone |

---

## 6. Follower simulation on clients

### 6.1 Clock driver: clients never execute `GameSimulation`

- **During the load**, vanilla `SimulationSystem` runs unchanged (loading progress, `m_IsLoading`), with `SharedSettings.instance.gameplay.pausedAfterLoading = true` forced in memory (R12 §5.3).
- **Why the hand-over must wait one update.** `OnGamePreload` sets `selectedSpeed = 0` and the private `m_IsLoading = true`; while that flag is set, the public `selectedSpeed` setter ignores every write (`SimulationSystem.cs:72-85, 155-162` [V]). Only `SimulationSystem.OnUpdate` clears the flag, and only once `GameManager.instance.isGameLoading` is false (`:202-215` [V]). `GameManager` invokes `onGameLoadingComplete` **before** it sets `m_State = State.WorldReady` (`Game.SceneFlow/GameManager.cs:1136-1137` [V]), so `isGameLoading` (`state == Loading`, `:264-274` [V]) is still true inside every `OnGameLoadingComplete` handler. Disabling `SimulationSystem` there would leave the flag set for the whole session: the speed would stay 0, `RenderingSystem` would stop advancing the render tick (`RenderingSystem.cs:186-189` [V]), and `WaterSystem` and `ClimateRenderSystem` would behave as paused (`WaterSystem.cs:1412-1417` [V]).
- **Load hand-over** (the Core `LoadHandover` state machine, executed by `ClientClockSystem` in LateUpdate before `SimulationSystem`):
  1. `OnGameLoadingComplete`: gates are re-asserted; `SimulationSystem` **stays enabled** and `pausedAfterLoading` stays forced.
  2. Frame N, the first LateUpdate with `GameManager.instance.state == State.WorldReady`: the hand-over notes it and does nothing else. `SimulationSystem` then runs its post-load update: it clears `m_IsLoading`, sets `selectedSpeed = 0` because `pausedAfterLoading` is forced, computes 0 ticks and runs only the Pre/PostSimulation phases. (If the loading-progress task still reports less than 1, the first update only finishes the progress bookkeeping and the flag clears one update later, `:202-208` [V]; the hand-over simply waits.)
  3. From frame N+1, each LateUpdate before `SimulationSystem`: probe `P-SIMLOADING` reads `m_IsLoading`. As soon as it is false, `SimulationSystem.Enabled = false` (nothing in Game reads that flag, [V-R] P-A; the disabled system is skipped in this very phase), `pausedAfterLoading` is restored to the user's value, and the follower clock starts at the snapshot frame. The clock writes `selectedSpeed` and reads it back.
  4. Fallbacks: if the flag is still set 10 frames after `WorldReady`, `m_IsLoading = false` and `m_SelectedSpeed` are written by reflection (same probe). If the probe itself is unavailable, the read-back of a sentinel speed written after disabling decides; on failure `SimulationSystem` is re-enabled (still paused) for one more frame and the hand-over retries, up to 60 frames, then the client enters `Faulted` with a diagnostic (resync).
  - The host uses the same state machine only to restore `pausedAfterLoading` after `SimulationSystem`'s first post-load update (on the host it stays enabled), because a restore inside `OnGameLoadingComplete` would let that update read the user's setting and run up to two real ticks at speed 1 (the per-frame cap is `round(selectedSpeed · 2)`, `:255` [V]) before the session is ready.
- **Self-check every frame**: `selectedSpeed` must read back what `ClientClockSystem` wrote; a mismatch is logged and corrected once through the probe; a persistent mismatch shows "Degraded: clock". The in-game self-test asserts the same after every session load.
- **After the hand-over**, `ClientClockSystem` (`UpdateBefore<ClientClockSystem, SimulationSystem>(LateUpdate)`) does each frame:
  1. `ClockModel` (Core) estimates the host frame from clock anchors (every epoch header plus 10-30 Hz unreliable `Clock` samples `{hostFrame, frameTime, smoothSpeed, sessionSpeed, paused, hostTimeMs}`), with RTT, offset and jitter, and computes `D` (§4.9.6). `target = estimatedHostFrame − D`. During catch-up `frameIndex` follows the host tick of the last applied epoch.
  2. `ticks = clamp(floor(target) − frameIndex, 0, 16)`; a gap above 1024 ticks is a logged hard jump.
  3. `UpdateSystem.Update(SystemUpdatePhase.PreSimulation)`; per tick `frameIndex++` (probe `P-CLOCK`: private setter delegate, `SimulationSystem.cs:68` [V]) and `CosmeticRunner.Tick(frameIndex, i)`; then `UpdateSystem.Update(SystemUpdatePhase.PostSimulation)`, which runs the ECS part of `WaterSystem` (R11 §8.2), the same order as `SimulationSystem` (`SimulationSystem.cs:272-295`).
  4. `frameTime` = fractional part of the target, `smoothSpeed` = moving average of the host's effective speed (private setters through the probe), `selectedSpeed` = 0 when paused, else the session speed (public setter). `RenderingSystem` freezes interpolation at speed 0 (`RenderingSystem.cs:186-189` [V]).
- **Fallback** when `P-CLOCK` fails: `frameIndex` is set through the public `SimulationSystem.Deserialize` with a 4-byte payload; it resets `frameTime` (`SimulationSystem.cs:140-146` [V]), so interpolation becomes stepwise but stays correct (P-C appendix A). The join is never refused for this (fixes J2's objection to P-A).
- **`CosmeticRunner`** calls `system.Update()` directly on the allow-list resolved by name (`cosmetic.json`): `TimeSystem`, `ClimateSystem`, `SnowSystem`, `WindSimulationSystem`, `WindSystem`, `ZoneAmbienceSystem`, `SpawnableAmbienceSystem`, `WetnessSystem`, `DirtynessSystem` (R11 §3, §8.2). It honours `GetUpdateInterval`/`GetUpdateOffset` (public, `Game/GameSystemBase.cs:131-136` [V]; offset −1 becomes a hash of the type name) and calls `ResetDependency()` (public, `:141` [V]) between ticks like `UpdateSystem` does (`UpdateSystem.cs:240-248` [V]); the interval and offset arithmetic is the Core `CosmeticSchedule`. Adapters can add cosmetic mod systems. `TrafficAmbienceSystem` is not run (its input comes from car navigation, R11 §3); its map is replicated (§4.10.8). `StreetLightSystem` is **not** cosmetic: it writes the replicated `Road`, `Building` and `Watercraft` light flags from client-local inputs (electricity flow, which is off on clients; local `UpdateFrame` slices; the client clock), which would flip back after every host overwrite (`StreetLightSystem.cs:83-151, 170-180, 315-352` [V]); clients derive pole states from the host's flags instead (§6.4).
- **Cosmetic write audit** (self-test with a city loaded, and whenever `cosmetic.json` or an adapter adds an entry): every cosmetic system is run once with the write auditor armed (§13.3); if any replicated column's change version moves, the entry is rejected with the system and type named.
- **Belt and braces**: every other system registered in `GameSimulation`, `EditorSimulation` and `LoadSimulation` also gets `Enabled = false`, so producers guarded by `Enabled` stop enqueueing (R11 §4). The list comes from `UpdateSystem.m_Systems` (private `List<SystemData>`, `UpdateSystem.cs:78` [V]; probe `P-SYSTEMS`) with a declared-list fallback. `GameModeGovernmentSubsidiesSystem` stays enabled: it is a pure function of replicated money and expenses, and the budget UI reads its `Enabled` flag (R14 §10.2).
- No pathfinding back-pressure exists on clients (the remaining queries use `uint.MaxValue` deadlines, R11 §7.2); the driver ignores `pendingSimulationFrame`.
- **Before any later load** on a client (resync, leaving), `SimulationSystem` is re-enabled so its loading logic runs; `OnGamePreload` sets `m_IsLoading` and only its own `OnUpdate` clears it (`SimulationSystem.cs:155-215`, [V-R] J2).

A system added to `GameSimulation` by a game patch or any mod can therefore never run on a client (fail closed, K3).

### 6.2 Speed authority and mirroring

- **Host**: `SpeedAuthoritySystem` (`UpdateBefore<SpeedAuthoritySystem, SimulationSystem>(LateUpdate)`) owns `SessionClock {speed, paused}`. It accepts changes from the interposed time controls, approved requests, or (fallback) a host `selectedSpeed` change that is not the forced-pause barrier. It re-asserts the session speed every frame, so the focus-loss barrier, error dialogs, lightning and photo mode (R05 §9.2) never pause or freeze anyone, unless the session option "pause when the host opens the pause menu" is on. While hosting, `Application.runInBackground = true` (restored afterwards), so alt-tabbing the host does not stall the session [M] (R06 §12).
- **Clients**: `ClientClockSystem` writes `selectedSpeed` every frame; `SpeedMirrorSystem` re-asserts it right before `PreRenderSystem`, whose `OnUpdate` starts with `RenderingSystem.PrepareRendering()` (`PreRenderSystem.cs:20-23` [V]). (P-A and P-B placed the mirror before `RenderingSystem`, which is too late for `PrepareRendering`.)
- **`HostBusy`**: before a snapshot or save stall, the host broadcasts `HostBusy(reason, expected duration)`. Clients freeze their clock target (extrapolation stops after 32 ticks), raise timeouts (`TimeoutConnected` 60 s), suspend their proposal timers (§5.5.2) and show a banner (R12 §6.1). `HostReady` ends the state.

### 6.3 Gate table

`gates.json` (embedded, overridable from the mod folder) lists the systems that are off on clients, resolved by type name in game and mod assemblies at session start (P-C §5.2).

- **Critical** entries must resolve, or the client refuses to join with a clear message; others log a warning.
- **Mechanism**: `Enabled = false`; `SystemBase.Update` then skips the system and `OnStopRunning` disposes queue lists (R03 §7, R11 §8.3).
- **When**: applied in the session load's `OnGamePreload` (our handler runs after the vanilla handlers because mods subscribe later, P-A §5.2), re-asserted in `OnGameLoaded` (after `CityConfigurationSystem` re-enables `UnlockAllSystem`, `CityConfigurationSystem.cs:195-198`), in `OnGameLoadingComplete`, and every 60 frames. Previous states are recorded and restored when the session ends; a client always returns to the main menu when it leaves. `SimulationSystem` is the one exception: it is disabled by the load hand-over (§6.1), never by the table.
- **Session-load set**: a separate section of `gates.json` lists vanilla **Deserialize-phase** systems that rewrite replicated values on every load and are therefore disabled only for the duration of a session load (from its `OnGamePreload` until `OnGameLoadingComplete`), on clients and for snapshot resyncs: `ResetBuildOrderSystem`, `CitizenSystem`, `RandomLocalizationSystem` (§7.4). The version- or format-gated data-migration systems of `Game.Serialization.DataMigration` are no-ops under version parity (each checks `context.version` or `context.format`, verified) and stay on.
- **Tool-scoped gates**: a third section of `gates.json` lists systems that are off on clients except while a given local tool is active, plus a linger time: `{ "tool": "Game.Tools.RouteToolSystem", "systems": ["Game.Routes.WaypointConnectionSystem", "Game.Routes.RoutePathSystem"], "lingerSeconds": 2, "guard": "RouteGuard" }`. The Core `PreviewWindow` state machine opens the window when `ToolSystem.activeTool` is that tool, closes it `lingerSeconds` after the tool was deactivated or last applied, and never opens it while the named guard is unavailable or the client is not live; `ClientClockSystem` evaluates the windows in LateUpdate and the game layer flips `Enabled` at the window edges, so a window opened in frame N takes effect from frame N+1 (§4.11.4). Adapters can declare tool-scoped entries for mod tools.
- **Producer/consumer self-check** (R11 §4): `TriggerSystem` may be off only together with `CitizenInitializeSystem`, `AddHealthProblemSystem`, `Game.Events.InitializeSystem` and `NotificationTriggerSystem`; `CityStatisticsSystem` with `AddHealthProblemSystem`; `XPSystem` with `XPBuiltSystem`, `NetXPSystem`, `XPAccumulationSystem`; `PropertyProcessingSystem` with `CompanyInitializeSystem`. `IconCommandSystem` must be on (its queue list has no `Enabled` guard, R11 §4). The checks are the Core `GatePlanner` over the parsed table and the resolved system list (unit-tested); the game layer only resolves names and flips `Enabled`.
- **Unclassified report**: Game-assembly systems that are on neither the keep list nor the off list are listed in the diagnostics after a patch (maintenance information, not an error).
- The initial table is Appendix E.

### 6.4 Special replacements

| System | Client treatment | Why |
|---|---|---|
| `SimulationSystem` | disabled by the load hand-over after `WorldReady`, as soon as its own post-load update cleared `m_IsLoading` (normally the next frame); replaced by `ClientClockSystem` | §6.1 |
| `StreetLightSystem` | never run (GameSimulation, not cosmetic); **`StreetLightDerive`** in the apply step: for every owner whose `Road.LightsOff`, `Building.StreetLightsOff`/`Illuminated` or `Watercraft.LightsOff` changed on apply, call the public static `StreetLightSystem.UpdateStreetLightState(ref StreetLight, Road/Building/Watercraft)` (`StreetLightSystem.cs:375-410` [V]) on the owner's `SubObject` entries with `StreetLight` (recursively for buildings, as the vanilla job does) and tag per §4.7.7 | newly derived poles take their state from the owner's flags in `SubObjectSystem`/`SecondaryObjectSystem` (`Game.Objects/SubObjectSystem.cs:2756-2760, 2953-2957`, `SecondaryObjectSystem.cs:3464, 3598` [V]); R11 §5 |
| `BatteryInitializeSystem`, `CityServiceWorkplaceInitializeSystem` (Mod5) | off | they re-apply host initialisation to replicated values (battery charge, workplace grace period, max workers) and write battery icons (§4.7.8) |
| `HouseholdAndCitizenRemoveSystem` (Mod2), `HouseholdPetRemoveSystem` (Mod4), `RemovedSystem` (Mod5) | off | on a despawn they edit data-replicated reverse buffers, delete whole households with pets and vehicles, clear `HighRentWarning` and remove icons locally (`HouseholdAndCitizenRemoveSystem.cs:100-117, 180-241`, `RemovedSystem.cs:196-229` [V]); the host sends every cascaded despawn and co-captures the buffers (§4.2.6) |
| `RoutePathReadySystem` (Mod1) | off; **`RouteReadyBridgeSystem`** sets path-target ready positions | its other outputs (waypoint path icons, `HiddenRoute` of gate-bypass routes, gate-bypass `Efficiency`) are host data (§4.11.3) |
| `WaypointConnectionSystem` (Mod5), `RoutePathSystem` (ModEnd) | off, except inside a guarded route-tool preview window (§4.11.4) | they re-derive `Connected`, waypoint lanes, stop route lists and segment paths from local lanes and the local pathfinder (§4.11.2) |
| `ConnectionWarningSystem` (ModEnd) | off | writes nothing but connection-warning icons, which are host data (§4.11.5) |
| `RoadConnectionSystem` (Mod4B) | kept; icons filtered; road choice reconciled by **`RoadConnectionReconcileSystem`** | it also builds `ConnectedBuilding`, building connection lanes and the `RoadConnectionUpdated` events that flow-graph and spawn-location derivation need (§4.11.5) |
| `IconCommandSystem` (ModEnd) | kept (mandatory); **`IconAuthorityFilterSystem`** right before it | client-local persistent icon commands are dropped (§4.10.4) |
| `GroundHeightSystem` | off; its private `m_ReadyUpdates` list is cleared every frame by reflection (probe `P-GROUNDHEIGHT`; the list otherwise only grows, `GroundHeightSystem.cs:1071-1286`, [V-R] P-A) | its re-snaps from local CPU heights race host heights (R07 §5.3); host heights arrive as S data (J3). Fallback: keep it on and let host values overwrite (tolerance list) |
| `CityServiceBudgetSystem` | off + feed (§4.10.2); fallback keep on | not `ISerializable` (R11 §5.2) |
| `MilestoneSystem` | off + feed of cached UI fields | rewards must not apply twice; UI fields must not freeze (R14 §1.4) |
| `UnlockSystem` | **kept** | processes the synthesised `Unlock` events (§4.10.3) |
| `TriggerSystem` and its unguarded producers | off (option A of R14 §5.5) | chirps, life path and radio come from the host; tutorials are off |
| Tutorial systems | off; `showTutorials` false in memory on every peer | §2.5 |
| `AutoSaveSystem` | off on clients | R12 §6.2 |
| `ParkedVehiclesSystem` | off on clients | the host spawns parked service vehicles (§5.4.3) |
| `UpgradeDeletedSystem` (ToolUpdate) | off | would duplicate host-rebuilt sub-nets and sub-areas with time-seeded seeds (R13 §5) |
| `ModifiedSystem` (policies) | kept, input-starved (only local predictions) | R11 §2.7 |
| `LoanSystem` | kept (predicts the loan UI; host values overwrite) | R10 §3.3 |
| `Vehicles`/`Creatures` `InitializeSystem` and `ReferencesSystem`, `UpdateGroupSystem` | kept | derived links (their `Created`/`Deleted` edits of data-replicated lists are no-ops, §4.2.4); `Applied` rule for movers |
| `BuildingStateEfficiencySystem`, `CityServiceEfficiencySystem` | off | `Efficiency` is replicated (P-A §5.2) |

### 6.5 What clients still compute

| Derivation | Replicated inputs | Code |
|---|---|---|
| Net geometry, compositions, lanes, zone blocks, secondary lanes and objects, sub-objects, aggregates, flow-graph topology, district membership, building connection lanes and `ConnectedBuilding` (road choice reconciled to the host's), search trees | structure, `PseudoRandomSeed`, `BuildOrder`, policies, renters, host road choices | Modification1-5 (R02 §6, R11 §2.5-2.8), `RoadConnectionReconcileSystem` |
| Route curves, route render buffers, route search tree, path-target ready positions | host line state + synthesised `PathUpdated` | `SegmentCurveSystem`, `RouteBufferSystem`, `Routes.SearchSystem`, `RouteReadyBridgeSystem` (§4.11.3) |
| Date, time of day, climate curves, sun and lighting, wind, snow and wetness visuals | clock, `TimeData`, climate prefab and overrides | cosmetic allow-list (§6.1) |
| Street-light pole states | host `Road`/`Building`/`Watercraft` light flags | `StreetLightDerive` (§6.4) |
| Traffic-light lane signals and pole states | node `TrafficLights` | `TrafficLightDeriveSystem` (§4.9.9) |
| Infoview colours, notification rendering, clusters | replicated values, host icons, flow status | `ObjectColorSystem`, `IconClusterSystem` (no client-side status icons, §4.10.4) |
| Map-tile permits | milestone `Locked` bits | `MapTilePurchaseSystem.GetAvailableTiles` (R14 §1.3) |
| Government subsidy line | money, expenses | `GameModeGovernmentSubsidiesSystem` |
| Modifiers of created entities | `Policy` | `*ModifierInitializeSystem` (Mod4); host values overwrite |
| Chirp links, name caches, event journal list | replicated chirps and journal entities | `ChirpLinkSystem`, `JournalCompanion` |
| Vehicle and creature reference buffers, `LaneObject` | forward references, lane keys | reference systems (R15 §4.5), `MoverLaneSync` |
| Zone ambience | buildings | `SpawnableAmbienceSystem` + `ZoneAmbienceSystem` |
| Water visuals | sources, parameters, sea level | GPU `WaterSystem`, corrected by tiles |
| Tool previews, validation, coverage and route previews | the replicated world | vanilla tools (local `Temp` only; `ValidationSystem` validates only local temps, R11 §6.5); route previews inside the guarded preview window (§4.11.4) |
| Rendering, animation, effects, audio | replicated state + motion | presentation systems (R15 §3.6) |

### 6.6 No double application

| Hazard | Prevention |
|---|---|
| Milestone money and creditworthiness twice | `MilestoneSystem` off; state replicated; `MilestoneReachedEvent` synthesised for UI only |
| Dev points twice | `DevTreeSystem` off (it would add points per synthesised event, R14 §3) |
| XP for replicated buildings and roads | `XPBuiltSystem`, `NetXPSystem`, `XPSystem`, `XPAccumulationSystem` off |
| Unlock-all rewards | `UnlockAllSystem` off (R14 §2.4) |
| Default policies appended to replicated `Policy` buffers | `DefaultPoliciesSystem` off (R10 §4) |
| Citizens, pets and cars spawned for replicated households; birthday corruption; household buffer duplication | all agent initializers off (R11 §2.11) |
| Random events re-targeted, journal entries duplicated | `Events.InitializeSystem`, `EventJournalInitializeSystem` off |
| Re-rolled names and chirp texts | `RandomLocalizationInitializeSystem`, `OutsideConnectionInitializeSystem` off |
| Parked service vehicles spawned locally | `ParkedVehiclesSystem` off |
| Sub-nets and sub-areas regenerated after a replicated upgrade deletion | `UpgradeDeletedSystem` off |
| Policy telemetry and triggers replayed | `Modify` events never replicated; state is |
| Money charged twice for a predicted tool use | client `PlayerMoney` is host-only and overwritten; the host charges once |
| Icons duplicated or fought over | single replication buffer, idempotent `Add`, icon authority filter for local producers, per-owner digest repair |
| Command events applied twice | never replicated; their consumers are off (R11 §2.4) |
| Battery charge and service-workplace grace period added again for spawned buildings and upgrades | `BatteryInitializeSystem`, `CityServiceWorkplaceInitializeSystem` off; end-of-frame re-application of spawn payloads as the generic safety net (§4.7.8) |
| Households, pets and vehicles deleted locally when the last member the client knows of dies | `HouseholdAndCitizenRemoveSystem`, `HouseholdPetRemoveSystem`, `RemovedSystem` off; reverse buffers co-captured (§4.2.6) |
| Build orders compacted and citizen prefabs re-rolled by the joiner's load | session-load set (§6.3, §7.4) |
| A line shown "not connected", with other stops or vehicles, or with stale warnings on one client | line state is host data; `RoutePathSystem`, `RoutePathReadySystem`, `WaypointConnectionSystem` off outside guarded preview windows; data-replicated vehicle and stop lists; re-resolution after local regeneration; UI-level line digest (§4.11) |
| Status icons produced or removed locally (road access, policies, connection warnings, mods) | icon authority filter; `ConnectionWarningSystem` off (§4.10.4, §4.11.5) |
| Another road choice (address, road access) on a client | `RoadConnectionReconcileSystem` applies the host's choice (§4.11.5) |

---

## 7. Join flow

### 7.1 Sequence

| # | Host | Client |
|---|---|---|
| 0 | host flow (§7.11): the city is loaded or already running; host session start binds and primes it (§4.4.8) | — |
| 1 | Steam lobby and P2P listen socket (plus an IP listen socket in direct mode), opened only after session start completed; lobby data §9.7 | finds the session: friends' lobbies, invite, rich-presence `connect`, `+connect_lobby`, or direct address + password |
| 2 | — | `Hello`: protocol, mod build hash, `Version.current` (`m_Version` + `fullVersion`), `FormatTags` digest, catalog fingerprint, prefab digest, code-mod and UI-module manifest digest, DLC/content digest, simulation-relevant mod-settings digest, **data-file digests** (§4.2.7), locale, Steam identity |
| 3 | admission (lobby membership or invite, ban list; direct IP: password challenge, §14.2) and `CompatibilityChecker` → `Welcome{session id, peer id, role, session settings, enabled system-state entry ids}` or `Refuse{MismatchReport}`; on a digest mismatch only that category's full list is exchanged | shows the readable report (§7.9); disables system-state channels it has not enabled itself (§4.10.1) |
| 4 | snapshot service: reuse the cached snapshot (≤ 120 s old and epoch log since its epoch ≤ 64 MB) or enqueue a new snapshot task on the `TaskManager` `"SaveLoadGame"` queue; broadcast `HostBusy(Snapshot)` | "Host is preparing the city" |
| 5 | snapshot frame: `SnapshotBindSystem` binds the few tracked entities created since the last flush (§4.4.6); `SaveGameSystem.stream` = temp `FileStream`, `context = new Context(Purpose.SaveGame, Version.current, Hash128.Empty)`, `RunOnce()` (`SaveGameSystem.cs:59-84` [V]; not `GameManager.Save`, which waits for a GPU frame, collects garbage, packages and moves "Continue", R12 §4.3). The epoch produced by this frame's sweep is **E**. Joiner tracking starts: log cursor at E, joiner dirty set created | — |
| 6 | `SnapshotManifest{id, size, xxHash128, chunk = 256 KiB, epoch E, frameIndex F0, hostBuildOrderNext, game mode name, climate/water-render/terrain-render PrefabIDs, referenced content names, per-family slot counts}` on lane R | pre-checks: `ArePrerequisitesMet(referenced content)` (`GameManager.cs:1847-1861`), `TryGetPrefab` for the three render/climate ids (R12 §4.3) → `Ready` or `Refuse` |
| 7 | streams chunks on lane B (§7.5) | writes chunks straight into a `SaveGameData` asset of a file-backed transient database (`AssetDatabase.GetTransient(0, root)` + `AddAsset<SaveGameData>` + `GetWriteStream`, R12 §4.3); per-chunk and blob hashes |
| 8 | keeps simulating; the joiner's backlog is the log from E plus its dirty set | buffers incoming epochs (spills to a temp file above 32 MB) |
| 9 | pushes simulation-relevant mod settings (session settings) | sets the load overrides (§7.4), arms the gate controller for `OnGamePreload` (including the session-load set: `ResetBuildOrderSystem`, `CitizenSystem`, `RandomLocalizationSystem`), arms the pre-load sweep, disables adapters' load-time mutators, enqueues `GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, saveGameData)` on `"SaveLoadGame"` (public overload, `GameManager.cs:1162` [V]) |
| 10 | — | Deserialize phase: `ClearSystem` → `PreLoadSweepSystem` → `GameModeSystem` (applies `overrideMode`) → `SerializerSystem` (including `NetIdSystem.Deserialize`) → resolvers (the session-load set does not run) → `PostDeserialize<NetIdSystem>` → `OnGameLoaded`: `GenerateEdgesSystem.GetBuildOrder().value = hostBuildOrderNext`; full derived rebuild (R04 §0.7) |
| 11 | — | `OnGameLoadingComplete(LoadGame, Game)`: re-enable the session-load set, re-assert gates, disable `AutoSaveSystem`, restore the client's own camera, dispose the transient database, post-load checks (§7.6), start the budgeted initial ledger pass. `SimulationSystem` stays enabled and paused; the load hand-over disables it once its own post-load update cleared `m_IsLoading` (normally one frame after `WorldReady`), restores `pausedAfterLoading` and starts `ClientClockSystem` at F0 (§6.1) |
| 12 | sends the **type-set keyframe** (TSET section), then log epochs E..head, channel keyframes (system state, prefab state, names, cell maps), then the joiner's dirty values (its P3 share raised to 50 % while catching up) | loads the keyframe into its registry, then applies merged epochs in bounded batches: "Catching up n %" (§7.7) |
| 13 | marks the client **live** when its acknowledged epoch ≥ head − 2 and 95 % of its joiner dirty set is drained; its view reports now drive attention and its proposals are accepted | tools and actions enabled; status **Live (verifying)** |
| 14 | per class, after that class's first full scan cycle that started after E has completed, enables beacons for this peer at a high rate (≈ 1-2 min sweep) | compares and repairs (§8); status **Synced** when the sweep finishes clean |

### 7.2 Snapshot production and `HostBusy`

- **One snapshot service.** Every snapshot and every host save is serialised on the `"SaveLoadGame"` queue, because `SaveGameSystem` keeps single-slot stream and context state (R12 §10).
- **The stall is real.** The Serialize phase is synchronous and does blocking GPU readbacks (`WaterSystem.Serialize`, `WaterSystem.cs:1042-1063`); expect 0.5-3 s on large cities [M]. Clients receive `HostBusy` first: their clocks hold, a banner shows, timeouts are suspended (§6.2). Compression and the file write continue asynchronously (`SaveGameSystem.cs:66-84` [V]).
- **Snapshot cache.** A snapshot is reused for any joiner arriving within 120 s, or while the epoch log since its epoch is below 64 MB; each joiner gets its own log cursor and dirty set, so only one stall is paid. At most two transfers run concurrently and fair-share the bulk budget; further joiners queue.
- **Cleanup.** The temp file is deleted once every joiner of that snapshot is live or gone.
- **Package content gap.** A bare `SaveGameSystem` snapshot lacks package-bundled climate, water-render and terrain-render prefabs (R12 §4.3). v1 refuses the join with a clear message if the client cannot resolve them; shipping those assets is a later milestone.
- After a save, `TrimPathsSystem` stamps path owners (R12 §6.1); path types are excluded, so only the prefilter pays.

### 7.3 Why the snapshot boundary is exact

- Session start (§4.4.8) and the bind pass before `SaveGameSystem` (§4.4.6) put every saved tracked entity into the `NetIdSystem` block, so the joiner's slots match the host's.
- The joiner's load does not run the vanilla Deserialize-phase rewriters of replicated values (session-load set, §6.3): `ResetBuildOrderSystem` would compact every `Game.Net.BuildOrder` and `Game.Zones.BuildOrder` into a gap-free sequence (`ResetBuildOrderSystem.cs:62-148` [V]) while the host's live values have gaps from every deleted or split edge, so nearly every S bucket would mismatch, trigger the level-4 resync, and the resync's reload would compact again; `CitizenSystem` re-rolls every citizen's `PrefabRef` with `RandomSeed.Next()` (`Game.Serialization/CitizenSystem.cs:38-53, 95-111`, `CitizenUtils.cs:232-262` [V]); `RandomLocalizationSystem` re-rolls out-of-range name indices, which only matters under a locale difference (`RandomLocalizationIndex.cs:30-47` [V]). Under version and content parity all three are safe to skip. The joiner then sets the build-order counter from `hostBuildOrderNext`.
- The snapshot frame's own tag sweep runs after `SaveGameSystem`, so epoch E contains that frame's changes from both before and after the save. Before-save changes are idempotent upserts over the snapshot; after-save changes are new. Changes made by that frame's UI handlers and LateUpdate keep their tags into the next frame and land in E+1.
- `SPAWN` records for entities bound by the bind pass upsert onto the snapshot's entities. A `DESPAWN` for an entity deleted before the save is ignored (the save query excludes `Deleted`).
- The joiner dirty set records every value captured after the snapshot. A value that changed before the snapshot but was not captured yet is already in the snapshot and is re-sent when captured, which is harmless (D7).
- `TrimPathsSystem`'s mutation during the save is idempotent for the joiner (R12 §5.2).

### 7.4 Client load preparation

| Setting or action | Value | Why |
|---|---|---|
| `CityConfigurationSystem.overrideLoadedOptions` | `false` | all city options come from the save (`CityConfigurationSystem.cs:423-438`) |
| `CityConfigurationSystem.overrideThemeName` | `null` | otherwise the client's theme overrides the saved one |
| `GameModeSystem.overrideMode` | host `currentModeName` | `GameModeSystem` runs before `SerializerSystem` (`SystemOrder.cs:797` [V]) and would apply a stale mode that rewrites prefab data (R12 §4.2) |
| `MapMetadataSystem.mapName` | host map name | UI, legacy water-render prefab name |
| `ApplyOptions` | **not called** | it persists options into the client's user state |
| `PlatformManager.achievementsEnabled` | `false` | not the client's city |
| `gameplay.pausedAfterLoading`, `gameplay.showTutorials` | `true` / `false` in memory; `pausedAfterLoading` is restored only by the load hand-over (§6.1), `showTutorials` when the session ends | no local tick on the first frame (R12 §5.3); tutorials off (§2.5) |
| session-load set (`ResetBuildOrderSystem`, `CitizenSystem`, `RandomLocalizationSystem`) | `Enabled = false` from the session load's `OnGamePreload` until `OnGameLoadingComplete` | they rewrite replicated values on every load (§7.3) |
| `GenerateEdgesSystem.GetBuildOrder().value` | the manifest's `hostBuildOrderNext`, set in `OnGameLoaded` | the counter `ResetBuildOrderSystem` would otherwise have set; keeps local predictions above every existing build order |
| `PreLoadSweepSystem` (`UpdateAfter<PreLoadSweepSystem, ClearSystem>(Deserialize)`; `ClearSystem` is `UpdateBefore` in Deserialize, `SystemOrder.cs:796` [V]) | armed for session loads | destroys mod-only persistent entities left from the previous city that `ClearSystem` does not clear (R12 §3) |
| simulation-relevant mod settings | host values written into the mods' `ModSetting` instances in memory | load-time mutators then compute host-identical prefab data (Historical Start, 529 Tiles, Tree Controller costs) |
| adapters' load-time mutators | their Deserialize-phase systems are disabled (`Enabled = false`) for this load. Lifecycle handlers of other mods are **not** unsubscribed by default (fragile, J2): they run under settings parity, and any world difference they cause is repaired after the load | R09 §5.7 |
| client capture | disabled until live | load-time effects must never become proposals |

### 7.5 Transfer

`BlobSender`/`BlobReceiver` (Core): manifest on lane R; chunks `{u32 blobId, u32 index, u32 length, u64 xxHash64, payload ≤ 256 KiB}` on lane B. The sender keeps `m_cbPendingReliable` between 2 and 8 MiB through the outbox's bulk window, which always leaves headroom for lane R (§9.3; R06 §5.4), and the connection's send buffer is raised to 16 MiB. The receiver writes each chunk at `index × chunkSize` into the transient asset's write stream and never holds the blob in managed memory. After a reconnect within 2 minutes it sends a have-bitmap and only missing chunks are resent. The final xxHash128 is checked. Both sides show progress and an ETA. 200 MB at 2-5 MB/s takes 40-100 s (R06 §5.4).

### 7.6 Post-load checks and verification

- **Placeholders**: instances whose prefab has a negative `PrefabData.m_Index` (R12 §5.4) must be zero, otherwise the join aborts with a "missing content" report.
- **Slot coverage**: `NetIdSystem` bound ≥ 99.9 % of tracked entities, and per-family counts are within ±1 % of the manifest.
- **Game mode**: `currentModeName` equals the host's.
- **No value digests are compared here.** The host's shadow lags its live state by up to one scan cycle, so comparing the manifest's digests with the snapshot would fail almost every join (J1's fatal flaw in P-A).
- **Initial ledger**: the client hashes its loaded world in a budgeted background pass (≈ 8 ms per frame during catch-up); unchanged values keep these hashes as their host-confirmed baseline. Because the session-load set keeps the loaded world bit-identical to the snapshot (§7.3), this baseline equals the host's values at E.
- **Load-parity census** (diagnostic mode; mandatory part of the M2 test script): in the snapshot frame, between `SnapshotBindSystem` and `SaveGameSystem`, the host computes per replicated type the count and the multiset hash of the canonical bytes of every bound entity (a synchronous pass of a few seconds, acceptable in diagnostics); the joiner computes the same census right after its load and reports every type whose census differs. This is how load-time rewriters other than the three known ones are found (vanilla after a patch, or mods); each finding becomes a session-load gate entry or an adapter `joinDisable` entry.
- **Verification**: per class, the host enables beacons for the joiner only after that class's first full scan cycle that started after E has completed. Until then the client is "Live (verifying)" and fully playable (P-C §6.5).
- A failure of the mandatory checks triggers one automatic retry with a fresh snapshot, then an abort with a diagnostics dump.

### 7.7 Catch-up

Log epochs E..head are applied in order (≤ 6 ms or 200 epochs per frame), with values coalesced by the workers; the joiner's dirty values flow at the raised P3 share. The client clock follows the host tick of the last applied epoch and switches to the smooth target when fewer than two epochs are pending. Proposals from a joiner are rejected until it is live. If the log since E exceeds 128 MB or 10 minutes before the joiner has loaded, or the joiner's apply rate stays below the production rate for 60 s, the host abandons the attempt and starts over with a fresh snapshot.

### 7.8 Rejoin, resync without reloading, multiple joiners

- **Short drop** (client still in the city, reconnect within 5 minutes): the host keeps the peer's dirty set during the grace period plus a per-peer log of the `(slot, type)` pairs it sent in recent epochs. On reconnect the client reports its last applied epoch; if the epoch log still holds it, the host re-marks as dirty every pair sent after that epoch, replays log epochs from there and continues with the preserved dirty bits: an exact resume without a snapshot (P-B §7.5). If the send log does not reach back far enough, a high-rate bucket sweep (§8.4) finds what is missing. The client keeps its type-set registry; definitions issued while it was away are in the replayed epochs.
- **Per-peer state that is not in the log is reset on resume**: both sides clear the peer's motion interest table (the host sends `MotionControl.ReleaseAll` and re-enters every mover of the current interest with a fresh assignment generation and Q-A baseline, §4.9.3); the host re-sends the current inspection sample, and cell-map keyframes for every map whose delta base the client cannot confirm. Otherwise movers whose assignment or baseline was in flight at the disconnect would stay frozen until they leave and re-enter interest.
- **Longer drop or detected divergence**: hash-based resync. Host and client compare bucket sums for every class at a high beacon rate and repair only mismatching buckets; the cost is proportional to the divergence, not to the city size.
- **Snapshot resync** only when the client has left the city, or on the level-4 triggers of §8.5. It reuses the join path while the connection stays up (including the session-load set and a fresh type-set keyframe); the world stays visible (frozen) under a "Resyncing city" overlay.
- **Multiple joiners** share a snapshot (§7.2) and have their own cursors and dirty sets.
- **Session reset**: when the host loads another city or its capture must be reinitialised (prefab set changed), it broadcasts `SessionReset` and every client rejoins from one shared snapshot.

### 7.9 Mismatch detection and explanation

| Check | Source | Rule |
|---|---|---|
| Protocol version, mod build | constants; SHA-256 of both mod assemblies (mod folder via `ModManager.TryGetExecutableAsset`, R05 §1.3) | exact |
| Game build | `Game.Version.current` `m_Version` and `fullVersion` (`Version.Equals` compares only `m_Version`, R12 §7) | both equal |
| Format tags | names of `FormatTags` | equal list |
| Type catalog | fingerprint (§3.6), includes the policy-table version | equal (struct layouts and policies) |
| Data files | xxHash64 of the effective (post-override) content of `policies.json`, `gates.json`, `cosmetic.json`, `adapters.json`, `sysstate.json` (§4.2.7) | equal; the report names the file and which side uses an override |
| Code mods and UI modules | `ModManager` enumeration and `ListModsEnabled()`, with versions, PDX ids and DLL hashes (R05 §1.5) | exact set (hard constraint 6). Optional host setting "allow differing local-only mods" for a built-in verified list (Find It, Extended Tooltip, I18N Everywhere, Unified Icon Library, Asset Icon Library, Toggleable Overlays, Employment Tracker, Cim Census, Skyve), applied only if the mod registers no serializable types, no `GameSimulation` systems and no prefabs (checked at runtime). Default: strict |
| DLC and content | `PlatformManager.IsDlcOwned`; referenced content → `ArePrerequisitesMet` (R12 §7) | must pass |
| Prefab set | digest of sorted `PrefabID`s including duplicates (§3.5) | equal; on mismatch the host sends its list and the client reports missing and extra ids grouped by source mod or asset pack |
| Render and climate prefabs | manifest | must resolve |
| Game mode | `GameModeSystem.currentModeName` | applied as `overrideMode`, verified after load |
| Simulation-relevant mod settings | declared by adapters; generic fingerprints through `ModSetting.instances` (probe `P-MODSETTINGS`) | block on a mismatch of declared settings unless the adapter allows the host to push them (then pushed); warn on others |
| Locale | active locale | warning only (random names depend on locale string counts, R13 §7.3) |

Digests come first (a matching handshake costs a few hundred bytes); full lists are exchanged only for mismatching categories. The `MismatchReport` is built in Core (testable) as entries `{category, item, host value, client value, fix hint}`. The UI groups them into *Blocking* and *Warnings*, offers "Copy report", lists PDX ids for Skyve, and offers "Subscribe to missing mods" through `PdxSdkPlatform.SubscribeAll` (explicit user action, then restart; R06 §8). The lobby list greys out sessions whose game, mod or content digests differ from the client's.

### 7.10 Saves, resume points, session end, warm join

- **Host saves** (manual, quick, auto) stay enabled, run on the `"SaveLoadGame"` queue, are announced as `HostBusy(Save)`, and contain the `NetIdSystem` block.
- **Client saves**: the interposed save command offers "Save a copy of the host's city": the host sends a fresh (or cached) host-authored snapshot with save metadata, and the client writes a normal save package with public APIs (`AddAsset<SaveGameData>`, `AddAsset<SaveGameMetadata>`, R12 §6.2). Without the interposer, a local save is allowed and labelled as a copy.
- **Resume points** (opt-in): the host pushes its latest session save to clients over idle bulk bandwidth at most every 30 minutes; if the host leaves, any player can host from it and the `NetIdSystem` block keeps identities. Live host migration is infeasible: GPU state, pathfinding queues and the wall-clock RNG exist only on the host (R12 §8).
- **Session end**: when the host exits or loads another city, clients receive `SessionEnd` and return to the main menu (or follow the host into its new session). Teardown hooks the game lifecycle (`OnGamePreload` to the main menu), not `IMod.OnDispose`, which runs only on quit (R05 §1.4).
- **Warm join** (milestone M9): a client that holds a host-authored copy of the same city lineage (a resume point or a saved copy) receives a block-hash delta (1 MiB blocks hashed with xxHash128) instead of the full file (P-A §6.3).

### 7.11 Host flow (main menu and "Host this city")

`HostFlow` (game-join) is the only way a session is started; `SessionRuntime.StartHosting(settings, password, source)` takes a `HostSource`:

| Source | When | Steps |
|---|---|---|
| `Save(saveId)` | main menu "Co-op → Host", the user picks a save | 1. `HostSessionAdjustments.Apply()`: `showTutorials = false` and `pausedAfterLoading = true` in memory, **before** the load, because `TutorialSystem.OnGameLoadingComplete` switches to intro mode at load when tutorials are on (`Game.Tutorials/TutorialSystem.cs:301-306` [V]) and an active intro list blocks `MilestoneSystem` (`MilestoneSystem.cs:72-75` [V]). 2. Mirror the vanilla load-game preparation for that save (`MenuUISystem.LoadGame`, `Game.UI.Menu/MenuUISystem.cs:789-805` [V]): `MapMetadataSystem.mapName` and `prefabReferences` from the `SaveInfo`, `overrideThemeName = null`, `GameModeSystem.overrideMode = saveInfo.gameMode`, but `overrideLoadedOptions = false` and no `ApplyOptions` (the save's own options are kept). 3. Enqueue `GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, saveInfo.metaData)` on `"SaveLoadGame"`. 4. `OnGameLoadingComplete` → load hand-over restores `pausedAfterLoading` after the first post-load simulation update (§6.1); the session stays paused. 5. Host session start (§4.4.8). 6. Listen sockets and lobby → `Hosting`. The host unpauses when ready. |
| `CurrentCity` | HUD or pause-menu entry "Host this city" in a running game | refused with an explanation if the tutorial intro list is active (`TutorialSystem.tutorialIntroUnfinished`, `:233` [V]), because it would block milestones on the host for the whole session; otherwise steps 1 (tutorials off), 5 and 6, with the session speed set to paused during session start. |
| `LobbyOnly` | milestone M1 transport tests | lobby, listen sockets, chat, mismatch report and the blob transfer test, without a city. |

**Save list.** The UI's `saves` binding lists `Metadata<SaveInfo>` assets from `AssetDatabase.global.GetAssets(default(SearchFilter<SaveGameMetadata>))` (as `MenuHelpers` does, `Game.UI.Menu/MenuHelpers.cs:69-83` [V]), with the vanilla filter that hides the built-in tutorial save (`MenuUISystem.cs:563-576` [V]): id, display name, city name, population, last modified, game mode, and whether its content prerequisites are met. The settings page offers the same flow without the `.mjs` module (§11.6).

---

## 8. Divergence detection and repair

Goal: prove that every client holds what the host sent, find local divergence, and repair it at the smallest granularity, with cost proportional to the divergence rather than to the city size.

### 8.1 Member hashes and normalisation

`h(slot, type)` per §4.3.4: canonical `NetRef`s, merge-masked fields excluded, multisets for `hashAsSet` types (§4.2.4), enabled bits included, plus an existence member per slot. Excluded from exact audits: motion (`TransformFrame`), mover `Transform`/`Moving`, OnDemand types, water, snow, `Game.Objects.Surface`, and derived families (checked only in diagnostic mode).

### 8.2 Host bucket sums

Per class, bucket = `slot >> 10` (1024 slots). Bucket sum = Σ `mix64(h)` modulo 2⁶⁴: commutative, so chunk iteration order never matters. Every hash-shadow change subtracts the old member hash and adds the new one: O(1) per change, no scanning (P-B §8.2). Memory: about 1k buckets × 7 classes × 8 bytes at 1 M slots.

### 8.3 Client ledger and rolling live verification

- **`HashLedger`** (Core) stores the member hash of the last *received* value per `(slot, type)`, whether it was applied, parked for a missing reference or held for a pending proposal (32-bit; about 12 MB for 3 M pairs [E]), and maintains the same bucket sums. It proves "received = sent"; the rolling verification below proves "live = received".
- **Rolling live verification**: each frame, within 0.3-0.5 ms, the client re-hashes a slice of live ECS data (S first, then in-view V, then everything else) and compares it with the ledger. A difference inside an active capture candidate set on an authorable type is a proposal (already captured, §5.2); any other difference is a leak: counted by `LeakTracker` and repaired after two frames. A full cycle takes about 10-20 minutes; S and in-view state are covered every 30-60 s [E].
- **Pending pairs are never compared**: pairs covered by pending proposals, pairs parked in `PendingRefs` (written with `Entity.Null` while the ledger holds the received hash, §4.7.5), held payloads (§5.5.4), pairs awaiting this frame's end-of-frame re-application (§4.7.8) or a re-resolution (§4.7.5), presence-safe pairs decoded with a stand-in (§4.11.3) and tolerated pairs (§8.5). Without this rule every parked record would be "repaired" again and again until its target bound.
- **Client repair requests** are coalesced (one request per 100 ms per class) and rate-limited to ≤ 5 per second with ≤ 256 slots each, below the host's acceptance limit (§14.3).

### 8.4 Beacons

The host emits `HASH{class, bucket, sum, count}` beacons inside each peer's epoch messages, so a beacon is ordered after the records it covers. The sum a beacon carries must describe what that peer **has been sent**, never only what the host has captured:

- **S and G** (P0, shared): the shadow is updated when a record is emitted (§4.4.5), so a beacon is always consistent with the shared section that precedes it, including while values wait in the S- or G-pending set.
- **V, A, A-low, M, K** (per peer): a beacon for bucket b goes to peer p only when p has no dirty bits in b (16 summary bits per bucket); otherwise the bucket is skipped this round. Every capture sets the bit for every peer (§4.5.3), so no captured value can be missing from a peer without its bit.
- **Confirmation**: a client raises level 1 only when **two** beacons for the same bucket, at least 250 ms apart (the longest coalescing interval), both mismatch its ledger after applying everything before them. Only level-2-confirmed per-slot differences count toward the thresholds of §8.5.

Default rate: 32 buckets per second per peer (≈ 0.5 KB/s); during post-join verification and hash resync up to 1024 buckets per second. The client compares a beacon with its ledger's bucket sum after applying everything before it. The ledger records every received value, including parked and held ones (§8.3), so pending pairs never cause a beacon mismatch.

### 8.5 Repair ladder

| Level | Trigger | Action | Cost |
|---|---|---|---|
| 0 Leak repair | a client leak persists for two frames (§5.2) | `RepairRequest` for those slots | per entity |
| 1 Bucket detail | the same bucket mismatches on two beacons ≥ 250 ms apart (§8.4) | the host sends per-slot member hashes of the bucket from its hash shadow (≤ 1024 × ~6 bytes compressed); the client lists the differing slots | ~6 KB |
| 2 Entity re-send | ≤ 64 confirmed differing slots | the host marks those pairs dirty for the peer at P1 (V/A/M), or re-serialises S entities from live ECS inside its next capture window into the next epoch flagged `Repair`; missing entities get `SPAWN` (generation and prefab checked, §4.7.3); entities the host does not have are answered "not present" and the client adds `Deleted` | per entity |
| 3 Domain rebaseline | > 64 confirmed mismatches in a bucket; > 10 % of a class's buckets confirmed; > 3 confirmed bucket repairs per minute in a class; a value-codec exception (the type is quarantined) | the host streams every record of the class over the bulk lane (110k citizens ≈ 10 MB, P-C §7.3); the client upserts and deletes extras | class size |
| 4 Snapshot resync | confirmed structural mismatch > 5 % of S buckets; a structural apply exception (Faulted); a protocol gap; an oversized epoch; an unknown type-set id; > 1000 pending references older than 600 epochs; ≥ 2 rebaselines of the same class within 10 minutes; catalog or prefab-set change; the host loaded another city; user request | join path (§7) | snapshot |

**Who decides.** The client's `RepairRequest` carries only a *hint* of the level it would like. The host keeps the ladder state per peer and decides: level 0-2 requests are served within the peer's P1 repair share and throttled when they exceed it (excess requests are dropped and the client asks again later; the host never kicks for repair-request rate). At most **one** domain rebaseline runs per peer at a time and at most three per peer per 10 minutes across all classes (a second one of the same class within 10 minutes is already a level-4 trigger); a further rebaseline request turns into a single snapshot-resync offer. A peer that needs more than three snapshot resyncs within 30 minutes is disconnected with the reason "repair loop" and both sides write a diagnostics dump, because something systematic is wrong.

**Tolerance** (P-C §7.3, J2): an `(entity, type)` that still mismatches after two repair attempts is masked for that peer for 10 minutes and logged with full detail (typical cause: unresolvable derived keys of node lanes, R13 §2.4). Masking replaces escalation; only if more than 1 % of a class is tolerated does the class escalate to level 3. Repair traffic is accounted in the P1 share (level 3 uses bulk).

**As built (w8/security, `Core/Integrity/RepairEscalation.cs`, `Sync/IntegritySync.cs`).** The game verifies 128-id buckets with `EntityDigest` and repairs entities through `DivergenceTracker` (the beacon/ledger-based `RepairCoordinator` above is not wired in). Above it: a **bucket rebaseline** (level 3 per bucket) when a bucket detail shows more than `EntityRepairLimit` (64) differences, or the bucket still differs after its repairs for two detail rounds: the client asks the host to re-send every id of the host's detail and of its own list (the existing `ResendEntities` request; the host answers absent ids with a despawn), once per bucket per 10 minutes and at most 4 buckets a minute. A **resync offer** (level 4) when more than 10 % of at least 20 compared buckets mismatch in two consecutive verification cycles, 8 bucket rebaselines happen within 10 minutes, or the tracker gave up on ≥ 50 objects and ≥ 1 % of the verified ones: the panel offers "Resync now" (leave with `ClientEndKind.Resync`, the recovery reconnects at once with a fresh snapshot) or "Not now". Offers are never forced, at most one per 30 minutes, and none after two accepted resyncs within an hour; the history survives the rejoin. The host-side ladder state and the "repair loop" disconnect are not implemented.

### 8.6 Special domains

| Domain | Verification | Repair |
|---|---|---|
| System state | 64-bit hash in each `SYS` record and periodically | keyframe |
| Prefab unlock bitset | hash every 30 s (tutorial-family prefabs excluded) | bit list; relock path (§4.10.3) |
| Icons | per-owner icon-set hash as a member of the owner's bucket (owners include lines, waypoints and buildings; icons on derived owners such as lanes and station stops are bucketed by their key's owner slot) | `RemoveAll` + adds for that owner |
| Line state at UI level | `LineStateProbe` rows and line-list hash (§4.11.6), compared on demand, after joins with the S class and every 60 s in diagnostics mode | repair of the differing line's slots (level 2) |
| Flow status (infoviews) | hash per flow key in each keyframe and delta | keyframe for that peer |
| K state of station stops and gate-bypass routes | hash per key, bucketed by the owner slot | keyed re-send |
| Zone cells | per-block overlay hash, bucketed by owner edge slot | overlay re-send |
| Names | hash of the canonical pair list | full list |
| Terrain heightmap | 64×64 tile hashes with async readback on both sides, after strokes and every 5 minutes | tile re-send |
| Cell maps | hash in each delta | keyframe |
| Water | none (visual by design) | correction tiles |
| Derived families (lanes, sub-objects, blocks) | diagnostic mode only: per-owner multiset hash of (prefab, quantised position) | the client touches the owner (`Updated` → regeneration); log |
| Traffic-light group masks | diagnostic per-junction layout hash | K-channel fallback for that junction (§4.9.9) |

---

## 9. Transport

### 9.1 Interfaces and implementations

Core defines `ITransport` (listen, connect, poll events, close), `IConnection` (send with lane and delivery, real-time statistics, close) and the statistics types. Implementations:

| Implementation | Assembly | Purpose |
|---|---|---|
| `SteamSocketsTransport` | game | primary: `ISteamNetworkingSockets` (v012) P2P by SteamID (`ConnectP2P`, `CreateListenSocketP2P`, one poll group on the host), Steam Datagram Relay and ICE; `InitRelayNetworkAccess` at startup (R06 §5.2) |
| `SteamIpTransport` | game | direct connections through the same API (`CreateListenSocketIP`, `ConnectByIPAddress`); encrypted; `IP_AllowWithoutAuth` only when needed, protected by the session password challenge (§14.2) |
| `SteamSocketPairTransport` | game | `CreateSocketPair` for the in-game single-machine self-test (R06 §5.1) |
| `LoopbackTransport`, `NetSimTransport` | Core | tests: in-process pairs; impairment model (loss, latency, jitter, reordering, duplication, bandwidth cap) |
| `UdpTransport` (milestone M9) | Core | `System.Net.Sockets` with own reliability, for LAN without Steam; HMAC per packet; not encrypted, labelled "LAN/trusted only" (R06 §5.7) |

The game's `com.rlabrecque.steamworks.net.dll` is referenced, never shipped: `steam_api64` refuses a second dispatch mode (R06 §3.2). Every `Callback<T>`/`CallResult<T>` is held in a field, because they unregister when garbage-collected (R06 §3.1).

### 9.2 Lanes

| Lane | Content | Reliability | Steam lane priority / weight |
|---|---|---|---|
| R (0) | control, proposals, epochs (P0/P1/P3), beacons, repair | reliable ordered | 0 / 1 |
| U (1) | motion, clock, view reports, presence | unreliable, no delay | 0 / 1 |
| B (2) | snapshots, domain rebaselines, resume points, terrain and water tiles | reliable | 1 / 1 (leftover bandwidth) |

The Steamworks.NET bindings of `ConfigureConnectionLanes` (scalars instead of arrays) and `SendMessages` (structs by value) are broken (R06 §5.6). The game layer therefore uses custom P/Invoke on the flat exports `SteamAPI_SteamNetworkingSockets_SteamAPI_v012`, `SteamAPI_ISteamNetworkingSockets_ConfigureConnectionLanes` and `SteamAPI_ISteamNetworkingSockets_SendMessages`, with messages from `SteamNetworkingUtils.AllocateMessage` and `m_idxLane` set (P-A §8.2). A startup self-test on a socket pair validates the imports and the lane semantics (probe `P-LANES`). **Fallback**: a second connection on virtual port 1 carries bulk, and the first connection carries R and U through `SendMessageToConnection` (R06 §5.3).

### 9.3 Packetisation

- One reliable epoch message per peer per frame (typically 1-30 KB compressed; large transactions as several epoch parts, §4.6.2), fragmented above 480 KiB (message cap 512 KiB, `Constants.cs:253`, R06 §4.2), sent `ReliableNoNagle` because batching is done by us.
- **Per-peer outbox** (Core `PeerOutbox`, wrapping the connection): Steam refuses reliable messages with `k_EResultLimitExceeded` when the connection's send buffer is full (R06 §5.4 item 3), and with lanes `SendBufferSize` is per connection, shared by R and B. The outbox therefore keeps an application-level queue per lane and hands a message to Steam only while `m_cbPendingReliable + size` stays inside a window: lane R up to `SendBufferSize − 1 MiB`; lane B only while the R queue is empty and the pending bytes stay below the bulk window (2-8 MiB), which always leaves headroom for R. A send that still returns `Busy` keeps the message at the head of its queue and retries on the next pump (~1 ms); nothing is dropped and no gap can appear. The scheduler builds per-peer value sections only when the R window has room (§4.8.4), and dirty bits are cleared only for messages the outbox accepted (§4.5.3). The outbox's queued bytes count toward the peer's P0 backlog, the automatic speed cap and the "connection too slow" disconnect (64 MB or 60 s, §4.8.5). Unit test: a fake `IConnection` that answers `Busy` for random stretches; every message arrives once, in order.
- Motion datagrams ≤ 1200 bytes, each record independently decodable (keyframe references and assignment generations, §4.9.3), sent `UnreliableNoDelay` (dropped rather than queued when the link is saturated; the next frame supersedes them).
- Received message fields are read through blittable mirror structs at fixed offsets, without `Marshal.PtrToStructure` allocations (R06 §5.6).

### 9.4 Blob transfer

§7.5. The same chunk protocol carries domain rebaselines, resume points and large tile batches.

### 9.5 Congestion and link statistics

`LinkEstimator` feeds each peer's token bucket (§4.8.2). Steam's `SendRateMin/Max` and `SendBufferSize` are read with `GetConfigValue` at startup and logged (their defaults in this `steam_api64` build are unknown, R06 §12), then set per connection from the session's upload budget. The `FakePacketLoss`, `FakePacketLag`, `FakePacketReorder` and `FakePacketDup` configuration values are exposed in developer settings for chaos tests (R06 §4.2). Relay status (`GetRelayNetworkStatus`) is shown in the network panel.

### 9.6 Threading and pooling

- **Network thread** (background, ~1 ms cadence): `ReceiveMessagesOnPollGroup`, release messages, decode envelopes into pooled buffers, per-kind `ConcurrentQueue`s, pump the per-peer outboxes within their windows (§9.3), write received snapshot chunks, echo `ProposalReceived` on receipt of a proposal.
- **Codec workers** (1-2 threads): epoch encoding and decoding, epoch merge, compression, motion encoding, shadow reads for per-peer records.
- **Main thread**: ECS reads and writes only (capture, apply, gating, clock). ECS thread checks are compiled out of the player, so violations would corrupt silently (R06 §7).
- **Steam callbacks** (connection status, lobby, join requests) arrive on the main thread through the game's `RunCallbacks` pump (`GameManager.Update` → `PlatformManager.Update`, R06 §2.3) and are posted to the session state machines. `MainThreadDispatcher` is not used (unordered, logs every call, R05 §11, R06 §7).
- **Allocation**: pooled byte arrays and unmanaged blocks; no LINQ or boxing on hot paths (incremental GC with 3 ms slices, R06 §1).

### 9.7 Lobby, invites, presence and timeouts

- Steam lobby (`FriendsOnly` by default, `Private` or `Public`, up to 8 members) with data `st.proto`, `st.game`, `st.mods`, `st.content`, `st.city`, `st.pop`, `st.players`, `st.ping` (ping-location string) and `st.pw` (direct IP needs a password) (R06 §10).
- Invites through `ActivateGameOverlayInviteDialog`; `GameLobbyJoinRequested_t` and `GameRichPresenceJoinRequested_t` handled; cold start through `+connect_lobby`, parsed in `OnLoad` and acted on once the main menu is ready [M] (the Paradox launcher may drop the argument, R06 §12).
- Rich presence uses only `connect` and `steam_player_group`; the game rewrites `steam_display` every 5 s (R06 §4.8).
- As built (w6/invites, `Session/SteamInvites.cs`, `Session/SteamFriendList.cs`, `UI/InviteUISystem.cs`): the lobby is only for invites and presence, the session stays on the P2P transport to the host's SteamID. `FriendsOnly`, member limit = max players, data `st_mod`, `st_host`, `st_name`, `st_proto`, `st_build`, `st_game`, `st_pw`, `st_connect` (Core `LobbyInfo`); left when hosting stops; a live client joins the host's lobby when it can find it. Rich presence `connect`, `status`, `steam_player_group`, `steam_player_group_size`. Invites: overlay dialog (with `IsOverlayEnabled` and a 3 s `GameOverlayActivated_t` watchdog) or directly from the panel's friends list (`InviteUserToGame`, `InviteUserToLobby` fallback; no overlay needed). A lobby invite's host comes from the lobby data (`RequestLobbyData`, the friend's `connect`, or entering the lobby). In the main menu the join starts at once; in a city a toast asks first.
- Timeouts: the clock stream doubles as a heartbeat; 3 s of silence shows "connection problems", 30 s disconnects and starts reconnect attempts for 2 minutes (short-drop resume, §7.8). `TimeoutConnected` is raised to 60 s while `HostBusy` is announced.
- If `SteamworksPlatform` is not initialised (a non-Steam store), only the later `UdpTransport` is offered.

---

## 10. Mod compatibility

### 10.1 Generic mechanisms (cover any mod)

1. **Save-query coverage.** Every mod `ISerializable`/`IEmptySerializable` component and every mod-only persistent entity is replicated with its family's default policy (R09 §1.1). Entity references inside mod components are remapped by `NetWriter` (Better Bulldozer, Traffic; R09 §5.2).
2. **Identity does not depend on `PrefabRef`** (water sources, record entities), and tool-local entities with `PrefabRef(Entity.Null)` are excluded (Move It control points, R09 §2.3).
3. **Enabled bits, signature changes and in-place `PrefabRef` swaps** are first-class records (Tree Controller `Decoration`/`LumberResource`, R09 §2.8).
4. **Atomic transactions at the single apply point**, before every Modification4B and ModificationEnd mod system, keep co-replicated components together (§4.2.6, R09 §5.4).
5. **Mod simulation systems never run on clients**: clients never execute `GameSimulation` (§6.1).
6. **Direct-write mod tools** are captured generically by stage D (§5.2) with whitelists of player-authorable types.
7. **Local churn is filtered**: `Highlighted`, camera-driven `Updated` or `BatchesUpdated` without a value change produce nothing, because capture compares canonical values (R09 §5.6).
8. **Load-time mutators** are disabled during the join load when an adapter lists them, otherwise they run under settings parity and their world effects are repaired (§7.4).
9. **Per-mod gate entries** cover enforcement systems outside `GameSimulation`, resolved by type name. There is no compile-time reference to any other mod, so an absent mod costs nothing and a renamed type disables just that entry with a logged warning (R05 §1.3).
10. **Session settings channel** for simulation-relevant mod settings (R09 §5.8).
11. **Parity by handshake** for prefab mutations and mod versions (§7.9, R09 §5.9).
12. **Unknown-writer diagnostics**: the leak tracker and write auditor name the component types and systems that write replicated state on clients (§13.3).
13. **Harmony audit** (diagnostics, read-only): if another mod loaded `0Harmony`, its patched methods are enumerated by reflection and patches on simulation or tool-apply systems are flagged (R09 §3). Skyline Together itself ships no Harmony.

### 10.2 Manifest and settings parity

The exact set of code mods and UI modules, their versions and DLL hashes must match (hard constraint 6): beyond data formats, a different mod set shifts the auto-assigned update offsets on the host and changes derivation on clients (R05 §12, R03 §2.1). The optional local-only relaxation is described in §7.9. Adapters declare which settings are simulation-relevant (mismatch blocks the join) and whether the host may push its values on join; a push sets the properties in memory before the join load and restores them when the session ends.

### 10.3 Adapter format

Adapters are data (embedded `adapters.json`, overridable by a file in the mod folder), parsed and validated in Core (P-C §9.4):

```json
{ "mod": "Anarchy", "assembly": "Anarchy", "versions": "*", "class": "WorldAffecting",
  "clientOff": ["Anarchy.Systems.OverridePrevention.PreventOverrideSystem", "..."],
  "clientKeep": ["Anarchy.Systems.OverridePrevention.AnarchyPlopSystem"],
  "cosmetic": [],
  "joinDisable": [],
  "localTypes": [],
  "playerMutableTypes": ["Anarchy.Components.PreventOverride", "Anarchy.Components.TransformRecord"],
  "directWriteTools": ["Anarchy.Tools.AnarchyComponentsToolSystem"],
  "hostOnlyActions": [],
  "simSettings": ["AllowPlacingMultipleUniqueBuildings", "MinimumClearanceBelowElevatedNetworks"],
  "pushSettings": true,
  "resendAfterJoinTypes": [],
  "systemStateSync": [],
  "toolScoped": [],
  "localIconPrefabs": [],
  "notes": "..." }
```

Field meanings: `clientOff` systems disabled on clients; `clientKeep` informational; `cosmetic` systems added to the client's cosmetic runner; `joinDisable` Deserialize-phase systems disabled during the join load; `localTypes` tool-local types never replicated; `playerMutableTypes` authorable for proposals; `directWriteTools` tool systems that open a stage-D window; `hostOnlyActions` settings buttons reverted on clients; `simSettings` block or push; `resendAfterJoinTypes` types the host re-sends to a joiner because the mod changes them only for serialisation; `systemStateSync` opt-in codecs for the mod's serialised systems (each must pass the round-trip self-test); `toolScoped` tool-scoped gate entries for the mod's tools (§6.3); `localIconPrefabs` notification prefabs that the icon authority filter keeps because a local-only mod uses them for its own presentation (§4.10.4).

### 10.4 The user's mods

| Mod | What it does to the world (R09) | Generic coverage | Adapter entries |
|---|---|---|---|
| **Move It** | direct writes to `Transform`, `Node`, `Curve`, area nodes and elevations, plus `Updated` | stage D captures one change set per action; `PrefabRef(Null)` control points and `MIT_*` types are local; undo produces new edits | `directWriteTools`: its tool; `localTypes`: `MIT_*` |
| **Better Bulldozer** | direct deletion of sub-elements; owner buffer `PermanentlyRemovedSubElementPrefab` and `OwnerRecord` entities with entity references; auto-removal and frame-counted deletion systems | buffers and record entities replicate generically with remapping; tool deletions and the vehicle/cim/animal remover are captured (agent deletion requires the demolish permission) | keep `RemoveRegeneratedSubelementPrefabsSystem`, `HandleDeleteInXFramesSystem` and the auto-removal systems on clients (they delete derived children deterministically from replicated records under settings parity, P-C, P-A); `joinDisable`: `CleanUpOwnerRecordsSystem`; `simSettings`: removal toggles; `hostOnlyActions`: "safely remove" |
| **Line Tool** | vanilla `Temp`/`Apply` pipeline with seeds that re-roll over time | stages B/C capture resolved results, so seed re-rolls do not matter (R09 §2.6) | none |
| **Tree Controller** | brush writes (`Tree` state, `PrefabRef` swaps, `Tree` add/remove, `Decoration` bit), Rendering-phase bit toggles, seasonal simulation | stage D with signature and enabled-bit records; its simulation systems never run on clients | `clientOff`: `LumberAndPauseTreeGrowthSystem`, `DestroyFoliageSystem`; `simSettings`: `DisableTreeGrowth`, `UseDeadModelDuringWinter`, `FreeVegetation`; `hostOnlyActions`: "destroy foliage", "safely remove", growth buttons; optional `cosmetic`: `ReloadFoliageColorDataSystem` |
| **Water Features** | sources without `PrefabRef`, feedback simulations on the GPU water surface, global water parameters, per-city settings in serialised systems | sources replicate generically; its simulation systems never run on clients; parameters through the system-state channel | `systemStateSync`: its parameter systems (after verification); `resendAfterJoinTypes`: `WaterSourceData` (its pre-serialise system writes rest heights into snapshots, R09 §2.9); `simSettings`: evaporation, fluidness, damping, speed, water damage, tides and streams; `hostOnlyActions`: cleanup cycle; `directWriteTools`: its water tool |
| **Zoning Toolkit** | `ZoningInfo` on edges; block resizing at Modification4B on every peer | `ZoningInfo` arrives in the edge transaction, so the client's own system derives identical blocks (R09 §2.10) | keep `ZoningToolkitModSystem`; `directWriteTools`: its tool |
| **Plop the Growables** | tag components; enforcement replacing vanilla zone checks; settings buttons that change many buildings | tags arrive atomically with buildings | `clientOff`: `PloppedBuildingSystem`, `SelectiveZoneCheckSystem`; `joinDisable`: `ExistingBuildingSystem`; `playerMutableTypes`: `LevelLocked`; `simSettings`: `SpawnedZoneDespawn`, `LockPloppedBuildings`; `hostOnlyActions`: all bulk buttons |
| **529 Tiles** | load-time unlock and relock from local settings; upkeep patch | tile `Native` replicated and verified after load; money is host-only | `simSettings` (pushed): all tile and upkeep options, with one-shot actions (`RelockAllTiles`, `NoStartingTiles`) forced off during the join load (P-A §9.3) |
| **Historical Start** | load-time unlock events and requirement rewrites | unlock bits through the prefab-state channel | runs on the joiner under pushed settings, because the client's kept `UnlockSystem` needs identical requirement data (§4.10.3); `simSettings`: all `Unlock*` options |
| **Anarchy** | own components, enforcement systems, prefab mutations, Harmony patches | components generic; `Transform` and `TransformRecord` in one record | `clientOff`: `PreventOverrideSystem`, `RemoveOverridenSystem`, `ResetTransformSystem`, `SetRetainingWallSegmentElevationSystem`, `PreventCullingSystem`; keep `AnarchyPlopSystem` (post-processes the client's own predictions; captured at stage C2; host commits carry no `Applied`, so it never touches remote commits on the host); `playerMutableTypes`: `PreventOverride`, `TransformRecord`, `DoNotForceUpdate`; `simSettings` (pushed): unique-building and clearance options |
| **Traffic** | replaces vanilla `LaneSystem`; data components with entity references; its own Temp pipeline (`ApplyLaneConnectionsSystem`/`ApplyPrioritiesSystem` in ApplyTool before `ApplyNetSystem`, `ModificationDataSyncSystem` in Modification4B, [W] R09 §3) | data generic with remapping; lanes derived locally by its own system; lane references as derived keys | keep its lane and data-sync systems; verify `LaneKey`/`PathNode` semantics with it installed [M] (R13 §2.5). Analysed early, not in M8: see below |
| **Road Speed Adjuster** | unknown (no source, R09 §3) | generic | strict parity; analysed early (below) |

**Early analysis of Traffic and Road Speed Adjuster** (milestone M1 research task, before the M3 and M4 contracts freeze). Both are world-changing mods on the user's list, and under the capture rules of §5.2 a client's lane-connection, priority or speed edit would most likely be reverted as a leak unless an adapter declares it. The installed DLLs of both mods are decompiled (third-party mods are not covered by the clean-room exclusion) to identify: their tool systems and the phase of their apply systems relative to capture stages B, C and C2; their serializable component and buffer types and every `Entity` field (lane references need derived keys); their systems that must be `clientOff` or kept; and their settings. The result is a pair of adapters (`playerMutableTypes`, `directWriteTools` or a stage-C2 extension for applies that run inside Modification phases, `clientOff`/`clientKeep`, `simSettings`) and an update of this table. "Traffic installed" is part of the M3 record/replay script, so `LaneKey` stability is checked when the structure path is first tested.
| Find It, Extended Tooltip, I18N Everywhere, Unified Icon Library, Asset Icon Library, Skyve, Toggleable Overlays, Employment Tracker, Cim Census | local UI, panels and tool launchers (R09 §3) | nothing to replicate; their data panels read replicated state (inspected entities are time-aligned, §4.7.9) | class `LocalOnly` (eligible for the host's optional relaxation, §7.9) |
| China Pack (assets) | content prefabs | prefab digest and referenced-content check | none |

### 10.5 Unknown mods

Default class: "must match exactly". A mod that registers no serializable types, no `GameSimulation` systems and no prefabs may be treated as local-only when the host enables the relaxation. Discovery mode, the write auditor and the leak tracker produce the data needed to write an adapter (§13.3).

---

## 11. UI and UX

### 11.1 Surfaces

| Surface | Content | Built with |
|---|---|---|
| Main menu entry "Co-op" | **Host**: pick a save from the `saves` binding (§7.11), session name, privacy (friends, private, public), password for direct IP, max players (2-8), default role, speed policy, upload budget, motion quality; then "Preparing session n %" (§4.4.8). **Join**: friends' sessions from the lobby list with compatibility badges and a ping estimate (`st.ping`); direct `host:port` + password. Relay status | `moduleRegistry.append("Menu", ...)` |
| In-game HUD strip | sync pill, player avatars with ping dots, current speed and who set it ("speed 2 requested by Anna"), invite button; outside a session: "Host this city" (§7.11) | `append("GameTopRight", ...)` |
| Player list | name, colour, role, ping, link quality, state (downloading / catching up / live / resyncing), current tool; host: role and permission toggles, kick, ban | expandable panel |
| Chat | hotkey toggle; timestamps and player colours; system lines (joins, leaves, rejections, resyncs); a focused text input masks game hotkeys (R05 §7.4) | HUD panel |
| Network panel | per-stream KB/s, budget, queue time, epochs behind, apply and scan milliseconds, staleness p50/p95 per class, hash health, last repairs, top leaking types, relay status | HUD panel (2 Hz) |
| Host session panel | roles and permissions, speed policy, automatic speed cap, upload budget, local-only relaxation, resume points, "resync everyone", end session | HUD panel |
| Full-screen states | "Downloading city 43 % (85/200 MB, 1:12 left)", "Loading", "Catching up 80 %", "Resyncing city", "Host is saving / preparing" | HUD overlay |
| Toasts | joins and leaves, rejected actions with reasons, desync repaired, snapshot progress | `NotificationSystem.Push/Pop` (works in menu and game, R05 §8.1) |
| Dialogs | mismatch report, leave/end confirmations, kick reason, resume prompt | own queue over `AppBindings.ShowConfirmationDialog`/`ShowMessageDialog` (single callback slot, R05 §8.2) |
| Pause menu | "Leave session" / "End session" | `moduleRegistry.extend(<pause menu module>, ...)` once the module path is found with `-uiDeveloperMode` [M]; HUD buttons as fallback |
| World overlays | remote cursors, tool ghosts, name tags, ping markers, flash on rejected predictions | §11.4 |

### 11.2 Hand-written `.mjs` module (no bundler)

Feasible and chosen (all three proposals agree; R05 §7.3):

- The loader needs only the header comment: the first block comment must contain a line exactly `Cities: Skylines II UI Module`, then `Id:`, `Author:`, `Version:`, `Dependencies:` (`Colossal.IO.AssetDatabase/UIModuleAsset.cs:44-107`, R05 §7.3). The module's folder is registered as host `ui-mods` and imported through `coui://ui-mods/<file>`.
- Community bundles do not ship React or the game APIs: they resolve `react`, `react-dom`, `cs2/api`, `cs2/bindings`, `cs2/l10n`, `cs2/ui` and `cohtml/cohtml` from `window` (`externalsType: "window"`, third-party Anarchy `webpack.config.js:31-43`, [V-R] P-A, P-B, P-C). A hand-written module reads the same globals.
- Components use `React.createElement` (no JSX), inline style objects or one injected `<style>` element, and only the documented binding helpers (`bindValue`, `useValue`, `trigger`, `call`). Undocumented `cs2/ui` component internals are avoided in v1.
- Strings are localised in C# and delivered through bindings, so the module has no l10n dependency.
- **Feature detection**: if a global is missing, the module renders nothing and logs to the console; the C# side keeps working (toasts, settings page), and the settings page carries every action needed to test a milestone (§11.6), so a broken module never blocks a test.
- **Checked before it reaches the game**: `tools/build.sh` runs `node --check ui/SkylineTogether.mjs` and a smoke script (`tools/ui_smoke.mjs`) that imports the module with stub `window.React` and `window["cs2/api"]`, calls `register()` and renders every component with sample binding values, failing on exceptions and on binding names that the C# side does not declare (the list is generated from `SessionUISystem`'s constants). Node is optional on the build machine; without it the step is skipped with a warning.
- Local modules hot-reload during development (`AddHostLocation` with watch enabled, R05 §7.3).

```js
/*
 * Cities: Skylines II UI Module
 * Id: SkylineTogether
 * Author: Skyline Together
 * Version: 1.0.0
 * Dependencies:
 */
const React = window.React, api = window["cs2/api"], h = React && React.createElement;
const session$ = api && api.bindValue("skylineTogether", "session", { state: "Idle" });
function SyncPill() {
  const s = api.useValue(session$);
  const bg = { Synced: "#3c3", CatchingUp: "#cc3", Verifying: "#cc3", Repairing: "#e93", Resyncing: "#e33" }[s.sync] || "#888";
  return h("div", { style: { background: bg, borderRadius: "8rem", padding: "2rem 8rem" } }, s.syncLabel);
}
function CoopMenuEntry() {
  return h("button", { onClick: () => api.trigger("skylineTogether", "openCoop") }, "Co-op");
}
export default function register(moduleRegistry) {
  if (!React || !api) { console.warn("SkylineTogether: UI globals missing"); return; }
  moduleRegistry.append("Menu", CoopMenuEntry);
  moduleRegistry.append("GameTopRight", SyncPill);
}
```

### 11.3 C# bindings

`SessionUISystem : UISystemBase` (UIUpdate), binding group `skylineTogether`:

- **Values** (pushed on change, diff-based): `session` (state, role, sync, staleness, speeds, pending intents, session-start progress), `players` (`RawValueBinding`, patchable), `chat` (`RawValueBinding` with patches), `netStats` (2 Hz), `lobbies`, `saves` (id, display name, city, population, last modified, game mode, prerequisites met; §7.11), `joinProgress`, `mismatch`, `sessionSettings`, `nameTags` (screen positions, while observed).
- **Triggers**: `openCoop`, `host(saveId, settingsJson)`, `hostCurrentCity(settingsJson)`, `stopHosting`, `join(lobbyId)`, `joinDirect(address, password)`, `leave`, `sendChat(text)`, `setRole(playerId, role)`, `setPermission(playerId, flag, value)`, `kick(playerId, reason)`, `ban(playerId)`, `setSessionSetting(key, value)`, `requestSpeed(index)`, `refreshLobbies`, `refreshSaves`, `subscribeMissingMods`, `copyDiagnostics`, `writeDump`, `requestResync`, `ping(x, y, z)`.
- All user-facing strings are resolved in C# from localisation keys (§11.7) and delivered through the bindings.
- The first main menu after boot gets no `OnGamePreload`, so the system checks `GameManager.instance.gameMode` itself instead of relying on `UISystemBase` gating (R05 §7.2).

### 11.4 Remote presence

Each client sends `Presence` at up to 10 Hz, unreliable, one datagram (≤ 1200 bytes, never fragmented): cursor world position, active tool kind, prefab rank, brush radius, player colour and the live tool preview (blueprint branch: `ToolPreviewCodec`, versioned on its own, ≤ 1100 bytes; empty when not building). The preview is read from the local tool's own `Temp` output (net edges as Bézier + width, object footprints as oriented rectangles or discs, area outlines, painted zone cells per block, line paths, water sources, nodes, sub-elements last), quantised (points as i16 steps of 1/16 m ... 1 m around an anchor, sizes in 1/16 m, yaw in 1/65536 turn) and sent whole in every message (a keyframe): a change goes out at once and is repeated twice 200 ms apart, so a lost message heals quickly; a viewer's preview is dropped by the host. The host relays it. `PresenceSystem` (Rendering, before `OverlayRenderSystem`) eases the previews between keyframes and draws them with `OverlayRenderSystem.GetBuffer(out JobHandle)` and `Buffer.DrawCurve`/`DrawCircle`/`DrawLine` (`OverlayRenderSystem.cs:133-251, 603, 636` [V]); never as ECS `Temp` entities, whose shared namespace would interfere with the local tool (R02 §10.2). Name tags are positioned through a world-to-screen binding. Ping markers (hotkey + click) show for 5 s. A prediction that the host rejected flashes once (P-A §4.7).

### 11.5 Session states and the sync pill

`Idle` → `Hosting(n)` | `Connecting` → `Handshake` → (`Mismatch(report)`) → `Downloading(%)` → `Loading` → `CatchingUp(%)` → `Live (verifying)` → `Synced` ⇄ `Repairing(k)` → (`Resyncing`). Side states: `HostBusy(reason)`, `Lagging` (backlog > 2 s), `Degraded(feature)`, `Disconnected (reconnecting n s)`, `SessionEnded(reason)`.

Pill colours: green *Synced*; yellow *Catching up*, *Verifying*, *Repairing n*, *Lagging*; orange *Degraded*; red *Resyncing*, *Disconnected*. The tooltip shows staleness p95, KB/s in and out, epochs behind and pending intents (P-A §10.1, J1).

### 11.6 Settings

`SkylineTogetherSettings : ModSetting` (the class name is unique, because `ApplyAndSave` keys settings by class name, R05 §5.3) holds local preferences only: display name (defaults to the Steam persona), player colour, default port, per-peer upload cap, host total upload cap, motion quality (interest radius factor, far-field rate), water quality, show network panel, chat and ping hotkeys (`ProxyBinding`), diagnostics level (verbose log, write auditor, discovery mode), a "Write diagnostics dump" button and, in developer mode, Steam fake loss and lag. No simulation-relevant value is ever read from a client's settings file; session settings come from the host (R05 §5.4).

**Fallback session controls** (a "Session" group on the same page, built only from `AutomaticSettings` widgets, so it works when the `.mjs` module does not): a save dropdown (`SettingsUIDropdown` over the save list) with "Host selected save", "Host this city", "Join by address" (`SettingsUITextInput` fields for `host:port` and password, `Game.UI.Menu/AutomaticSettings.cs:1091-1097` [V]) with a "Join" button, "Leave session", and "Show session status" (a message dialog with phase, peers, sync state and the last error). These call the same `SessionRuntime` methods as the bindings.

### 11.7 Localisation

String tables are data: an embedded `Data/locale.en-US.json` (keys for the settings page, UI labels, toasts, mismatch fix hints, reject reasons, fault messages), overridable and extensible by `Data/locale.<id>.json` files in the mod folder, loaded from milestone M0 through `LocaleSource` (an `IDictionarySource` registered with `LocalizationManager.AddSource` per locale, R05 §6); English is the fallback for missing keys. Strings for the JavaScript module are resolved in C# through `LocalizationManager.activeDictionary` and delivered through bindings. A Core test checks that every key referenced in code (a generated key list) and in the data files exists in the English table.

---

## 12. Error handling and fail-safe

### 12.1 `SafeRun` and fault categories

Every game-layer system derives from `SkylineSystemBase`, whose sealed `OnUpdate` wraps the body in try/catch. An exception is logged with system, phase, session state and record context, counted, and routed to `SafeModeController` with a category: `StructuralApply`, `ValueApply`, `Capture`, `Commit`, `Snapshot`, `Transport`, `Decode`, `Ui`, `Probe`. `UpdateSystem` already isolates per-system exceptions (`UpdateSystem.cs:196-214` [V]), but the session must know about them.

### 12.2 Fault responses

| Fault | Client | Host |
|---|---|---|
| Structural apply exception | the entities the failing plan created get `Deleted` (they never received `Created`, §4.7.3); `Faulted`: applying stops at once and the follower **freezes** (gates kept, clock target frozen, capture off, "Resyncing city" overlay) until a snapshot resync, which starts immediately; repeated faults back off (2 min, 10 min); after three failed resyncs it stops, writes a dump and shows "Leave session / Report". A corrupted replica is never kept and never keeps running (P-C §7.4, J2) | — |
| Value-codec exception | the live component is untouched (decode-then-commit, §4.7.4); the record is skipped, the type is quarantined for the session and its class is rebaselined (§8.5 level 3); a second fault of the same type within 10 minutes → resync | capture of that type is disabled, the HUD shows "Degraded", clients are offered a resync |
| Unknown type-set id, oversized but well-formed epoch (> 256 MiB) | `Faulted` → snapshot resync as above | — |
| Capture-domain exception | — | the domain is disabled ("Degraded: building state paused"); clients are offered a resync after recovery; repeated failure ends the session gracefully with a dump |
| Commit exception | — | the proposal is rejected ("host error") and logged; the host world stays authoritative and the next capture replicates whatever state resulted |
| Snapshot exception | — | retried once, then the join is refused with the reason |
| Decode error, malformed message, or a single message above its per-part cap (only a broken or hostile peer sends one, since the host encoder splits epochs) | the session is closed with a reason | that peer is disconnected and logged |
| Outbox of a peer above 64 MB or 60 s | — | that peer is disconnected with "connection too slow for this session"; it can rejoin (§4.8.5) |
| Protocol gap (missing epoch after a reconnect) | resync | — |
| Probe failure | the feature is disabled with a message; critical probes refuse hosting and joining | same |

Nothing throws into the game loop.

### 12.3 Probes and degradation

`ProbeRegistry` resolves every non-public member at startup (Appendix B). Each probe has an id, a criticality, a fallback and a user-visible message. Results appear in the self-test report, the diagnostics dump and, for mismatches between peers, the handshake report.

### 12.4 Watchdogs and timeouts

Clock and heartbeat timeouts (§9.7); apply backlog watchdog (§4.7.10); proposal timeout (10 s from the host's receipt echo, suspended during `HostBusy`, §5.5.2); pending-reference time limits (§4.7.5); join progress watchdog (no progress for 60 s → retry); load hand-over watchdog (60 frames, §6.1); host session-start progress (no progress for 60 s → stop hosting with a dump); gate re-assertion (§6.3); outbox size per peer (§9.3); memory watch on shadows and the epoch log; in diagnostics mode, sizes of known vanilla queues (by reflection) to catch producer leaks.

---

## 13. Diagnostics and observability

### 13.1 Logging

A dedicated log file (`ModsData/SkylineTogether/Logs/`), levels and categories (`net`, `session`, `capture`, `apply`, `motion`, `join`, `gate`, `probe`, `ui`), rate limiting of repeated messages, a ring of the last 1000 lines for dumps, and no per-frame allocations.

### 13.2 HUD and network panel

The sync pill and the network panel (§11.1) show what a player can act on: staleness per class, budgets, bytes per stream, apply and scan milliseconds, repairs, the top leaking types, the join progress with an ETA, and the pending-intent counter (J1).

### 13.3 Write auditor, discovery mode and leak tracker

- **Write auditor** (clients, diagnostics mode): after ModificationEnd it compares the chunk change versions of replicated columns with the versions recorded right after the applier wrote them. A newer stamp means another system took RW access; the stamp's global system version is matched against each system's `LastSystemVersion`, which names the "fighter" system and type to add to the gate table or an adapter (P-A §7.6).
- **Discovery mode** (clients): logs save-query entities created outside the applier and outside predictions, which reveals derived families missing from the classifier (§3.2).
- **`LeakTracker`**: counts leaks per component type and family; the dump lists the top types, which after a game patch point at the missing gate entry (P-C §7.3).
- **Local icon producers**: the icon authority filter counts dropped commands per notification prefab (§4.10.4); a new non-zero prefab after a patch or a mod update names a local status derivation to gate, to leave filtered, or (for a local-only mod's own presentation) to declare in its adapter. The route guard counts restored pairs per type during preview windows (§4.11.4).

### 13.4 Self-test runner

At the main menu and at session start (P-C §11.3): (a) probes resolve; (b) gate, cosmetic and adapter names resolve and producer/consumer pairs are consistent; (c) the catalog is built, with counts per kind; with a city loaded, the codec self-test runs on live samples (§4.3.6); (d) system-state entries round-trip; (e) Steam lane imports and a socket-pair loopback; (f) compressor availability (ZSTD, LZ4 probe); (g) the UI module is observed (binding observer count > 0; otherwise the settings-page fallback is announced); (h) unclassified systems; (i) the full-tag context guard; (j) after every session load, the load hand-over completed and `selectedSpeed` reads back what the clock wrote (§6.1); (k) with a city loaded, the cosmetic write audit (§6.1); (l) the terrain frame-time compensation on one stamp at a forced 15 fps and 60 fps (§4.10.9, M8); (m) the icon authority filter: probe `P-ICONQUEUES` resolves, and a synthetic non-`Temp` `Add` from a scratch queue is dropped while a `Temp` one is kept (§4.10.4); (n) the route guard: on a scratch copy of a line, a deliberately changed `Connected` and `PathInformation` are restored and a `PathUpdated` is synthesised; if this fails, route-tool preview windows stay closed (§4.11.4); (o) `LineStateProbe` computes rows for every line without exceptions. Critical failures block hosting and joining with an explanation.

### 13.5 Single-machine test harnesses

The game runs single-instance (R06 §1), so the client path needs single-machine harnesses:

- **Shadow client** (host, P-B §12.3): an internal client decodes the host's own epoch stream into a hash ledger and compares member hashes with live ECS, exercising catalog, classification, capture and remapping.
- **Record/replay** (P-A §11.3): the host records snapshot S plus its epoch stream L, its class digests and its `LineStateProbe` rows at fixed ticks; later, in single player, "replay client mode" loads S through the client load path and applies L through the real `ClientApplySystem` (with the client gate table, the icon authority filter and the line systems off), then compares digests and line rows with the recorded host values. This exercises the client apply path without a second PC.
- **As built (w5/replay, "Record test session" / "Replay & verify")**: the host records through a virtual *recorder peer*: a real `SessionClient` on a `LoopbackTransport` pair whose host end is attached to the host's `CompositeTransport` (Steam plus in-process parts; a solo test host has no network part at all). The host admits it through the normal handshake, takes a join snapshot for it with the same `ISnapshotProvider` (join-asset container included), streams it and treats it as live; the recorder acknowledges every `Epoch` message, every `MotionControl`, reports the host's camera as its view and sends the icon full-sync request a client sends on going live. Its `RecordingTransport` writes every message it receives, raw (envelope, compression, fragments), with host time and frame into `ModsData/SkylineTogether/Recordings/<time> <city>/stream.strec` (checksummed entries; a crash leaves a readable prefix); a `LoadDone` barrier marks where its instant load reported success. Verification checkpoints go to the recorder only, inside its `Epoch` stream (`SyncRecordKind` 160-162): at a host frame end with no deferred values waiting for it, every bucket's integrity hashes (the rolling verification's `IntegrityHasher`), the members of every entity whose hash changed since the previous checkpoint, then a summary with the `LineCheck` rows, the icon digests per owner and the global channels' last-sent hashes (first checkpoint 10 s after the recorder is live, then every 60 s, and a final one on stop). The replay plays the stream into `SessionManager.StartReplay` through a `PlaybackTransport` (the join at once, the barrier until the client's own `LoadDone`, then recorded pace or 2x/4x/max with backpressure on the apply queue; what the client sends is decoded, counted and dropped), so the real loader, gates, clock, movers and receive path run. `ReplayChecker` compares each checkpoint record right after its batch was applied (icons once the reconciliation settled) and names differing components from the recorded members; `ReplayVerdict` (passed only with a complete stream, a complete checkpoint, no apply error and no difference at the last checkpoint; earlier ones that went away are listed as transient) goes to `verdict.txt`/`verdict.json` next to the recording and to the panel. A replay never enters recovery and ends like a leave.
- **Replica diff**: on demand, host and client dump canonical hashes per entity for an owner subtree or a bucket; `tools/ReplicaDiff` shows the differing types and bytes.
- **Load-parity census** (§7.6): the host's per-type census at the snapshot and the joiner's census after its load; works on one machine through record/replay as well (the replay client loads S and compares with the recorded census).

### 13.6 Diagnostics dump

One zip (JSON plus logs) under `ModsData/SkylineTogether/dumps/`: session state, peers, versions and manifests, catalog and policy digests, per-rule family counts, gate state (incl. preview-window history), probe and self-test reports, slot statistics, budgets, staleness, hash health, repair and leak history, icon repairs per owner, the icon authority filter's dropped commands per producer, route-guard restores, road-choice reconciles, re-resolutions, the current `LineStateProbe` rows, pending references, unresolved prefabs, timing histograms, the last 1000 log lines and the last 200 errors with context. Written from the settings page, the HUD, or automatically on `Faulted`. This is what the user sends back after each in-game test.

### 13.7 Measurement mode

A `measure` command records for ten minutes (P-B §13.3): entity, chunk and archetype counts per family; EDC events per second by kind; SDC changed entities per second per class and the fraction of chunks with changed versions per cycle; agent spawn and despawn rates; mover counts and interest-set sizes per zoom level; bytes per second per stream and peer; capture and apply milliseconds (p50/p95); snapshot Serialize time, size and transfer throughput; codec microseconds per type; host session-start duration.

**What exists when.** The full engine arrives over M3-M6, but the measurement build is M2. M2 therefore ships a read-only **`MeasurementProbe`** (game-ecs-schema) with stand-ins that need no shadow and no engine:

| Number | M2 stand-in | Replaced by the real measurement in |
|---|---|---|
| entity/chunk/archetype counts per family | census (exact) | — |
| EDC events per second by kind | `TagCounterSystem`, registered before `PrepareCleanUpSystem`, counting tagged entities per family and tag | — (exact) |
| SDC change rate per class | per-class chunk change-version sampler: fraction of chunks whose versions moved per 4, 20 and 60 s window (no shadow, so an upper bound) | M5a (real SDC) |
| bytes per entity and bandwidth models | canonical size per family, sampled through the codecs | M3 (S), M5a (V/A) |
| mover counts and interest-set sizes | movers per camera-distance ring sampled from `Transform` at 1 Hz | M6 (real interest sets) |
| codec µs per type, session-start binding time, snapshot Serialize time and size | measured directly | — |
| capture/apply ms, bytes per stream | — | M3/M5a/M6 (provisional until then) |

These numbers replace the [E] values they cover before cadences and shares are fixed; §15.3 lists which values stay provisional until M5/M6.

---

## 14. Security

### 14.1 Threat model

Co-op among friends; admitted peers are semi-trusted. Goals: no unauthorised joins, no remote code execution, no crash or resource exhaustion from malformed or hostile messages, no file-system abuse, no leak of personal data. Non-goal: anti-cheat against a modified client of an admitted player. The host validates every proposal (§5.4.1), but a malicious admitted player could still propose odd edits that pass validation; this is documented.

### 14.2 Authentication and admission

- **Steam P2P**: peers are authenticated with Steam certificates and traffic is encrypted end to end by default; the `Unencrypted` option is never set (R06 §5.2). The player identity is the peer's SteamID (`m_identityPeer`). The host admits SteamIDs that are lobby members or hold an invite token, and refuses banned SteamIDs (ban list persisted on the host).
- **Direct IP**: encrypted but possibly unauthenticated (`IP_AllowWithoutAuth`), so the host requires the **session password** through a challenge: the host sends a 16-byte nonce; the client answers `HMAC-SHA256(key = PBKDF2-SHA256(password, session salt, 10 000 iterations), hostNonce ‖ clientNonce ‖ sessionId)`. The password never crosses the wire. `HMACSHA256` is available in the game's Mono and in netstandard2.0 (R06 §6); the `Rfc2898DeriveBytes` overload that takes a hash algorithm is not part of netstandard2.0, so Core implements PBKDF2-HMAC-SHA256 itself over `HMACSHA256` (a few lines) and tests it against the published PBKDF2-HMAC-SHA256 vectors (RFC 7914 §11). Three failures per address within a minute block that address for a minute.
- **LAN UDP transport** (later): an HMAC on every packet with a key derived from the password and both nonces; not encrypted, labelled as such.
- **As built (w8/security, `SessionHost`, `Session/JoinSecurity.cs`, `Session/SessionCrypto.cs`)**: the transport reports a Steam ID only when Steam authenticated it, and marks connections from the direct-IP socket (`PeerIdentity.Direct`); the hello's Steam ID claim is never an identity. Order of the handshake: ban list (Steam ID; address for direct and unauthenticated connections) → join policy (`JoinPolicy.FriendsAndInvited`: Steam friends or players invited from the panel, else `NotInvited`) → protocol version (without versions while a password is still owed) → a second connection of the same Steam user → password challenge right after the hello, whenever a password is set and always for direct or unauthenticated connections (refused with `NotInvited` when the host has none; the game opens the IP socket only with a password) → only then build id, game version, mods (detailed `MismatchReport`) and capacity. The key is PBKDF2-HMAC-SHA256 with 10 000 rounds (derived once per password on the host), every challenge has a fresh nonce and accepts one answer, client nonces under 8 bytes are refused. Wrong answers are limited per Steam ID and per address (`PasswordAttemptLimiter`: three free, then 5 s doubling up to 10 min, forgotten after 15 min; two open challenges per key). The lobby-membership admission above is not implemented. In-process connections (`PeerIdentity.Local`, the test recorder) need no Steam identity. Stopping the host refuses connections still in the handshake with `HostShuttingDown`; players get `SessionEnd`.

### 14.3 Message validation and limits

- Every decoder is bounds-checked; varints are limited to their type width; counts are checked against the remaining bytes; unknown kinds are dropped.
- Per-kind size caps: epoch part ≤ 8 MiB uncompressed and epoch ≤ 256 MiB in total (§4.6.2), proposal part ≤ 4 MB and proposal ≤ 64 MB / 200k entities in total, chat ≤ 500 characters, names ≤ 64 characters, view report and presence fixed maximums. Declared uncompressed sizes are checked before decompressing (decompression-bomb guard); partially received multi-part messages are capped in count and bytes per peer.
- Client→host rate limits per kind: proposals 50/s, chat 5/s, view reports 20/s, presence 20/s, repair requests 10/s with ≤ 512 slots each. Exceeding the repair limit only throttles (requests are dropped; the client retries), because repair traffic is automatic; the expensive levels are decided and capped by the host (§8.5).
- Slots, type ranks and prefab ranks are validated against local tables and are never used to load types, assemblies or files.
- Strings are stripped of control characters before they reach the UI.
- A malformed message disconnects the sending peer (on the host) or closes the session (on a client), with a log entry. A well-formed message that exceeds a semantic limit of the replica (unknown type-set id, oversized epoch) leads to a resync instead (§12.2).
- Proposals are validated for structural safety, not only for permissions: `PrefabRef` changes keep the prefab class and family, curves stay attached to their nodes (§5.4.1), so a hostile or buggy proposal cannot make every peer's jobs read prefab data that does not exist.

### 14.4 Permissions, kick and ban

Roles and permission flags (§5.6) gate every proposal category. Host-only commands (kick, ban, roles, session settings, resync everyone, end session) are accepted only from the host peer; a co-host cannot kick the host. Kick closes the connection with a reason; ban also records the SteamID.

**As built (w8/security, `Session.Moderation`)**: the host may kick, ban and set every role (co-host, builder, viewer). A co-host may kick builders and viewers and switch them between builder and viewer, through `ControlKind.PlayerAction` requests the host checks against the sender's current role; it cannot touch the host or other co-hosts, cannot ban, cannot appoint co-hosts and cannot change the host's options, password, join policy or ban list. Gameplay permissions of a co-host are those of a builder. `Kick` sends `ControlKind.Kick`, `Ban` sends `ControlKind.Ban` (the client ends with `ClientEndKind.Kicked` / `Banned`; after loading the city the recovery panel says "removed" / "removed and banned"). A ban records the authenticated Steam ID, or the address (without port) of a connection without Steam identity; the list (`BanList`) is persisted in the host's settings file and edited in the host options (unban).

### 14.5 Local safety

Snapshot files are written only into the mod's temp directory with locally generated names, never with paths from the network. The transient database folder is deleted after the load. Blob sizes are capped (manifest size ≤ 2 GB and below the free disk space minus a margin). No executable content is ever received. No reflection is driven by network data. Logs never contain passwords. The mod sends only the Steam identity and persona name, never other personal data.

---

## 15. Performance and memory budgets

### 15.1 CPU per frame (main thread) [E]

| Work | Host | Client |
|---|---|---|
| Tag sweep / end-of-frame capture | 0.05-0.3 ms | 0.1-0.5 ms (stage C2, stage D, rolling verification 0.3-0.5 ms) |
| Capture flush (EDC) | 0.1-0.5 ms (one-frame spikes on big edits) | — |
| SDC + hot set | 1.0 ms at ≥ 50 fps, 2.0 ms at ≤ 30 fps (adaptive) | — |
| Motion | 0.5-2 ms at speed 4 (sampling) | ≤ 1 ms (JIT writer) |
| Channels and pollers | < 0.3 ms | channels inside the apply budget |
| Apply | proposals: rare | ≤ 3 ms live, ≤ 6 ms during catch-up |
| **Target total** | **≤ 3 ms typical** | **≤ 4 ms typical** |

Every system measures itself with a `Stopwatch` and adapts its budget so the mod's frame-time overhead stays under about 5 %. On the host this matters twice, because `SimulationSystem` caps ticks by measured frame cost (R03 §1.2).

### 15.2 Memory [E]

| Structure | Host | Client |
|---|---|---|
| Slot side tables (§3.3) | ~33 MB at 1 M slots | ~33 MB |
| Hash shadow / ledger | ~8 B per replicated pair (~40-50 MB) | ~4-8 B per pair (12-24 MB) |
| Value shadow | 30-60 MB | — |
| Peer dirty sets | ≤ 8 MB per peer, usually far less | — |
| Epoch log | 128 MB (configurable, disk spill) | backlog buffer and epoch-part assembly (spill above 32 MB) |
| Per-peer outbox | normally < 1 MB; disconnect above 64 MB | — |
| Cell-map version ring | ~15 MB | — |
| Staging rings, pending references | — | ~1-5 MB |
| Received-value cache and derived-reference index (§3.7; line state incl. segment paths, parked-car lanes, waypoint links) | — | ~2-20 MB |
| Snapshot | on disk (temp) | on disk (transient database) |

### 15.3 Measurement plan

Milestone M2 ships the measurement mode with the `MeasurementProbe` stand-ins (§13.7). Its report replaces: the entity and chunk counts behind §3.3 and §15.2; the EDC event rates and the upper bounds of the SDC change rates behind §4.4.3 and §4.4.7 (scan cadences and the scan budget get provisional defaults); bytes per entity behind §0.6 and §4.6.6; mover counts per distance ring behind the interest radius factor `k` (provisional); codec and snapshot timings behind §0.5 and §7.2; the session-start duration. Values that stay **provisional** until the real subsystem is measured: SDC cadences, budget shares and P3 convergence times (M5a), bytes per stream for V/A and channels (M5a/M5b), attention shares (M5c), motion bytes, interest-set sizes, the clock-lag margin and the far-field rate (M6), water quality default (M8). Each later milestone's measurement run updates its rows; contracts do not depend on these numbers, only defaults do.

---

## 16. Risks and first in-game verifications

### 16.1 Risks

| # | Risk | Impact | Mitigation |
|---|---|---|---|
| 1 | Mono cost of serialisation, scanning and apply in 100k+ cities (no Burst) | host frame time, slow joins | tag-driven capture, chunk-version and raw-hash prefilters, tiered cadences, pointer column reads, frame-start reads, budgets; measured in M2 |
| 2 | `ISerializable` behaviour at runtime with our context (version and purpose branches) | wrong values or a crash | owned full-tag `LoadGame` context with a startup guard; codec self-test on every type before hosting; system state only from a verified allow-list |
| 3 | Host snapshot stall and slow relay transfer | everyone hitches; minutes to join | `HostBusy` UX, snapshot cache, bulk lane with windowing and resume, backlog caps with restart, warm join later |
| 4 | A game patch renames, adds or removes systems or types | leaks, or a gameplay system running on clients | clients never execute `GameSimulation`; name-based data tables with critical entries; unclassified-system report; leak telemetry; write auditor; repair keeps the session playable |
| 5 | Reflection probes break after a patch | lost comfort features | every probe has a fallback (Appendix B); the critical path uses public APIs |
| 6 | A client-side kept system writes replicated state | repair churn | gate table, write auditor, tolerance list; switch the system off when found |
| 7 | Derived identity not peer-stable (node lanes, node secondary objects, aggregate merges, traffic-light group masks) | parked-car lane references fail; small visual differences | derived keys with geometric fallback, aggregate reconciliation, tolerance list, per-junction K-channel fallback; lane references kept to the minimum |
| 8 | Host commits client-computed geometry | an admitted modified client could place invalid things | revision, unlock, cost, area and sanity checks; permissions; friends-only threat model (§14.1) |
| 9 | The 32-tick interpolation window under jitter at speed 4 | movers jump or snap back | staging, JIT writer, extrapolation with cap, pinning, adaptive `D` with measured render lead |
| 10 | Bandwidth with many clients at speed 4 | lower motion quality, slower convergence | per-peer budgets, degradation ladder, host total cap, automatic speed cap for P0 overload |
| 11 | Steam lanes P/Invoke or flat API change | bulk blocks control traffic | startup probe; second-connection fallback |
| 12 | Water write path not accepted; drift between correction tiles | visual water differences | gameplay is host-only; quality setting; probe fallback (client-simulated water only) |
| 13 | Unknown mods with load-time or enforcement behaviour | repair churn after joins | post-load verification, repair, adapters updatable without a rebuild |
| 14 | `NetIdSystem` type name unresolvable after a mod update | slot identities lost for old saves | pinned `AssemblyVersion` + `FormerlySerializedAs` self-alias; verified before release [M] |
| 15 | Package-bundled climate/render prefabs missing on clients | `[Missing]` placeholders | handshake pre-check, clear refusal in v1 |
| 16 | Global creature existence costs bandwidth at speed 4 | less motion budget | measured in M2/M6; automatic speed cap; if needed, a later decision record may add interest-scoped creatures with explicit out-of-scope references |
| 17 | Residual double application of a reward | money/XP drift | G-class hashes detect within seconds; absolute values overwrite; audit in the dump |
| 18 | Host alt-tab stalls the simulation | everyone pauses | `Application.runInBackground` while hosting [M]; HUD shows "host window inactive" if it still happens |
| 19 | Tutorials disabled in sessions | feature loss | product decision (Appendix G) with a later per-player option |
| 20 | The `.mjs` globals change in a game update | HUD missing | feature detection; build-time syntax and smoke check; C# toasts and the settings-page session controls keep every action available (§11.6) |
| 21 | The load hand-over depends on private `SimulationSystem` state (`m_IsLoading`) | client speed stuck at 0 (frozen movers, paused water) | hand-over waits for vanilla's own update instead of writing the flag; probe `P-SIMLOADING` only verifies and is the fallback; sentinel read-back; self-test after every load (§6.1) |
| 22 | Other load-time rewriters of replicated values (vanilla after a patch, or mods) | joiners start diverged; repair storms after joins | session-load set, adapter `joinDisable`, the load-parity census in M2 and after patches (§7.6) |
| 23 | Kept client systems re-apply host initialisation on spawned entities | values double-applied until a repair | end-of-frame re-application of spawn payloads by default; write auditor; spawn-equality test per building family (§4.7.8) |
| 24 | A client derives a status of its own (line connectivity, stop or vehicle counts, warnings) | players see different states, the failure the user reported from another mod | line state is host data with the line systems off; icon authority filter; data-replicated lists; re-resolution; UI-level line digest and acceptance test ST-LINES (§4.11) |
| 25 | `P-ICONQUEUES` breaks after a patch (`IconCommandSystem` internals change) | client-local icons from kept producers become visible | pure producers stay gated off; per-owner icon digest repairs the rest; self-test (m) reports it; Harmony H1 as the documented last resort (Appendix B) |
| 26 | Lane keys inside replicated segment paths do not resolve (node lanes, R13 §2.4) | gaps in a drawn line curve on a client | path end points are presence-safe, so connectivity and warnings stay exact; curve gaps are visual-equivalent and logged with the tolerance list; re-resolution after regeneration |

### 16.2 Verify first, in this order

Each item is a small in-game check done by the user with a debug build; the diagnostics dump is the evidence.

1. **Build and load**: both DLLs load from the local Mods folder; the `.mjs` module renders the menu entry; the self-test report shows all probes (M0).
2. **Codec round trip** on a live 100k city with the owned full-tag context: 100 % identical bytes; time per family recorded (M2).
3. **Snapshot and public load path**: host flow from a save and "Host this city"; host session-start duration (binding) on 20/100/300 MB cities; `SaveGameSystem` into a temp file (time and size logged; "Continue" unchanged); transient-database load through the public `GameManager.Load` with the overrides and the session-load set; `NetIdSystem` round trip; no tick on the first frame; game mode correct; the load-parity census reports no differing type (M2).
4. **Follower gating and clock**: the load hand-over disables `SimulationSystem` after `WorldReady` only once `m_IsLoading` is false (log the number of frames it waited); `selectedSpeed` reads back; movers interpolate; clock driver with `P-CLOCK`, cosmetic runner (write audit clean) and gate table; day/night and weather advance; a replica fingerprint is unchanged after 10 minutes without host input; memory flat; no errors (M2).
5. **Measurement run** (§13.7) on the user's largest city (M2).
6. **Applier on live data** through record/replay on one machine, once without and once with Traffic installed: host creates, updates and deletes nets, buildings and areas; client geometry, lanes, blocks and sub-objects identical; `LaneKey` resolution rate logged; no dangling or duplicated sub-nets after upgrade deletion; spawned buildings of every family canonically equal after the creation frame (M3).
7. **Classification audit**: per-rule counts; upgrading a building on the host creates no duplicate sub-objects on the client (M3).
7a. **Transport lines, structure** (acceptance test ST-LINES steps 1-2, §4.11.6): line panel, diagram and list identical including a deliberately broken segment (its warning icon is checked from M5b); connected on both screens at the same moment after the gap is closed; station upgrade keeps the line connected; `/st lines compare` clean; the road-choice reconcile count is logged (M3).
8. **`Applied` + host `UpdateFrame`** on mover spawns: values equal on both sides (M6).
9. **Steam**: lane imports on a socket pair; relay throughput; a 200 MB blob between two accounts; RTT and jitter distribution (M1).
10. **Prediction capture**: vanilla tools and every UI action on a client produce the expected proposals and nothing else; one check per apply system (object relocate, upgrade and rebuild; area node edit; route waypoint move; zoning) with the leak tracker at 0; a relocated service building re-paths its vehicles on the host; the write auditor is clean (M4).
10a. **Route-tool preview window** (ST-LINES step 3): a client draws and edits a line with the vanilla preview; the guard's restore counter shows host lines protected; self-test (n) green (M4).
11. **Synthesised progression events** refresh the toolbar, milestone panel and dev tree within one frame of the host's unlock; the relock path works (M5b).
11a. **Status indicators and line values** (ST-LINES steps 4-5): icon authority filter active (self-test (m)), dropped-command counters plausible, road-access, electricity, water and dead-end warnings identical, flow-status colours identical in the infoviews, line vehicle counts and "not enough vehicles" identical (M5b, vehicles M6).
12. **Motion** at speeds 1 and 4 with Steam fake lag: smooth, no snap-backs, lights and sirens correct, pinning on interest exit (M6).
13. **Traffic-light derivation** and group-mask equality (M6).
14. **Binding interposer** for time controls; menu binding names (M4).
15. **Terrain and water write paths** (M8).
16. **`+connect_lobby` cold start** through the launcher; `Application.runInBackground` (M1).

---

## Appendix A. Frame order and registrations

### A.1 Host frame

| Order | System | Registration |
|---|---|---|
| MainLoop, before | `UpdateWorldTimeSystem`, `PathfindQueueSystem`, `EndFrameBarrier` | vanilla (`SystemOrder.cs:47-49` [V]) |
| right after `EndFrameBarrier` | `HostCaptureFlushSystem` (capture window; calls the registered ingress hook, `HostIngress.CommitInsideWindow`, as its step 9, §4.4.2) | `UpdateAfter<HostCaptureFlushSystem, EndFrameBarrier>(MainLoop)` |
| MainLoop | `RaycastSystem` ... `ToolSystem` (PreTool, ToolUpdate, ApplyTool, PostTool; `ToolCaptureApplySystem` first in ApplyTool captures the host player's terrain stamps) ... `ModificationSystem` (Mod1 ... ModEnd; `XpMessageTapSystem` after `XPSystem`) ... `UnlockSystem`, `AllowBarrier<EndFrameBarrier>`, `PreRenderSystem` ... `UIUpdateSystem` (`HostCommandSystem`, `SessionUISystem`), `RenderingSystem` (`PresenceOverlaySystem`) | vanilla (`SystemOrder.cs:55-68` [V]) |
| before `SaveGameSystem` | `SnapshotBindSystem` (snapshot frames only) | `UpdateBefore<SnapshotBindSystem, SaveGameSystem>(MainLoop)` |
| MainLoop | `SaveGameSystem` | vanilla (`:69`) |
| before `PrepareCleanUpSystem` | `TagSweepSystem` | `UpdateBefore<TagSweepSystem, PrepareCleanUpSystem>(MainLoop)` |
| MainLoop, after | `PrepareCleanUpSystem` | vanilla (`:50`) |
| (outside ECS) | cohtml UI update, then Cleanup phase, then platform update (Steam callbacks) | `GameManager.Update` (`GameManager.cs:713-724` [V]) |
| LateUpdate | `SpeedAuthoritySystem` → `SimulationSystem` (0-8 ticks) | `UpdateBefore<SpeedAuthoritySystem, SimulationSystem>(LateUpdate)` |

### A.2 Client frame

§4.7.1.

### A.3 Registration code (`OnLoad`)

```csharp
var us = updateSystem;
// common
us.UpdateAt<NetIdSystem>(SystemUpdatePhase.MainLoop);                                   // persistence only
us.UpdateAfter<PostDeserialize<NetIdSystem>>(SystemUpdatePhase.Deserialize);
us.UpdateAfter<PreLoadSweepSystem, ClearSystem>(SystemUpdatePhase.Deserialize);
us.UpdateAt<SessionLifecycleSystem>(SystemUpdatePhase.MainLoop);
us.UpdateAt<SessionUISystem>(SystemUpdatePhase.UIUpdate);
us.UpdateAt<DiagnosticsSystem>(SystemUpdatePhase.UIUpdate);
us.UpdateBefore<ToolCaptureApplySystem>(SystemUpdatePhase.ApplyTool);                   // host: stamps; client: stage B
us.UpdateBefore<PresenceOverlaySystem, OverlayRenderSystem>(SystemUpdatePhase.Rendering);
// host role (idle unless hosting)
us.UpdateAfter<HostCaptureFlushSystem, EndFrameBarrier>(SystemUpdatePhase.MainLoop);   // ingress commits run inside it
us.UpdateAt<HostCommandSystem>(SystemUpdatePhase.UIUpdate);
us.UpdateAfter<XpMessageTapSystem, XPSystem>(SystemUpdatePhase.ModificationEnd);
us.UpdateBefore<SnapshotBindSystem, SaveGameSystem>(SystemUpdatePhase.MainLoop);
us.UpdateBefore<TagSweepSystem, PrepareCleanUpSystem>(SystemUpdatePhase.MainLoop);
us.UpdateBefore<SpeedAuthoritySystem, SimulationSystem>(SystemUpdatePhase.LateUpdate);
// client role (idle unless joined)
us.UpdateAfter<ClientApplySystem, EndFrameBarrier>(SystemUpdatePhase.MainLoop);
us.UpdateBefore<NoPredictGateSystem, ToolOutputSystem>(SystemUpdatePhase.ToolUpdate);
us.UpdateBefore<ToolCaptureResultSystem>(SystemUpdatePhase.Modification1);
us.UpdateAfter<RouteReadyBridgeSystem, RoutePathReadySystem>(SystemUpdatePhase.Modification1);         // vanilla one is off on clients
us.UpdateAfter<AggregateReconcileSystem, AggregateSystem>(SystemUpdatePhase.Modification2B);
us.UpdateAfter<RoadConnectionReconcileSystem, RoadConnectionSystem>(SystemUpdatePhase.Modification4B);
us.UpdateAfter<TrafficLightDeriveSystem, TrafficLightInitializationSystem>(SystemUpdatePhase.Modification4B);
us.UpdateBefore<IconAuthorityFilterSystem, IconCommandSystem>(SystemUpdatePhase.ModificationEnd);
us.UpdateBefore<SpeedMirrorSystem, PreRenderSystem>(SystemUpdatePhase.MainLoop);
us.UpdateBefore<MotionWriterSystem, PreCullingSystem>(SystemUpdatePhase.PreCulling);
us.UpdateAfter<RouteGuardSystem, PathfindResultSystem>(SystemUpdatePhase.MainLoop);                   // preview windows only
us.UpdateAt<ClientUiApplySystem>(SystemUpdatePhase.UIUpdate);
us.UpdateBefore<ClientEndOfFrameSystem, PrepareCleanUpSystem>(SystemUpdatePhase.MainLoop);   // re-apply, liveness, hooks
us.UpdateBefore<ClientClockSystem, SimulationSystem>(SystemUpdatePhase.LateUpdate);             // load hand-over, then clock
// diagnostics (M2 measurement build)
us.UpdateBefore<TagCounterSystem, PrepareCleanUpSystem>(SystemUpdatePhase.MainLoop);            // idle unless measuring
```

Siblings anchored at the same parent run in registration order (`UpdateSystem.cs:382-441` [V]). No mod system is registered in `GameSimulation`, so auto-assigned update offsets of vanilla systems do not shift (R05 §12). `TagSweepSystem` (host) and `ClientEndOfFrameSystem` (client) are siblings before `PrepareCleanUpSystem`; only the one for the current role does work.

---

## Appendix B. Non-public access (probes) and fallbacks

| Probe | Target | Used for | Fallback | Critical |
|---|---|---|---|---|
| `P-CTX` | `ContextFormat` layout for `UnsafeUtility.As`, or internal `ContextFormat.EnableTag` by reflection (`ContextFormat.cs:9, 26` [V]) | full-tag read context (§4.3.2) | the other method | **yes** |
| `P-CLOCK` | `SimulationSystem.frameIndex/frameTime/smoothSpeed` private setters (`SimulationSystem.cs:68-87` [V]) | client clock | public `SimulationSystem.Deserialize` for `frameIndex` | no |
| `P-SIMLOADING` | `SimulationSystem.m_IsLoading` (read; write only as fallback) and `m_SelectedSpeed` (fallback write) (`SimulationSystem.cs:62-85, 161, 211` [V]) | verifying the load hand-over (§6.1) | sentinel read-back of `selectedSpeed`; retry with `SimulationSystem` enabled; then `Faulted` | no |
| `P-SYSTEMS` | `UpdateSystem.m_Systems` (`List<SystemData>`, `UpdateSystem.cs:78` [V]) | disabling every simulation-phase system on clients | declared list | no |
| `P-GROUNDHEIGHT` | `GroundHeightSystem.m_ReadyUpdates` | drain while disabled | keep the system on (tolerance) | no |
| `P-BUDGET` | `CityServiceBudgetSystem` income/expense arrays | budget panel on clients | keep the system on | no |
| `P-MILESTONE` | `MilestoneSystem` cached UI fields | XP bar on clients | bar shows 0 | no |
| `P-XP` | `XPSystem` message queue (peek) | XP toasts | none on clients | no |
| `P-BINDINGS` | vanilla trigger bindings in the registry (`time.*`, menu actions) | interposed controls (§5.7) | speed-diff detection, lifecycle-based leave | no |
| `P-BARRIER` | `TimeUISystem` paused-barrier event binding observer count (`TimeUISystem.cs:93` [V]) | telling the forced pause from a speed change | treat host pauses as session pauses unless the option says otherwise | no |
| `P-APPLYMODE` | `ToolBaseSystem.applyMode` protected setter (`ToolBaseSystem.cs:182`) | no-predict gate | viewer applies reverted as leaks | no |
| `P-MODSETTINGS` | `ModSetting.instances` (internal static, `ModSetting.cs:17`) | generic settings fingerprints | adapter-declared settings only | no |
| `P-JOURNAL` | `EventJournalSystem` list and callbacks | journal panel on clients | refresh on reopen | no |
| `P-RADIO` | `RadioTagSystem` inputs | radio news on clients | none | no |
| `P-WATER` | water flow update and active-cell reset | water correction tiles | tiles skipped | no |
| `P-LANES` | `steam_api64` flat exports (`ConfigureConnectionLanes`, `SendMessages`) | Steam lanes | second connection for bulk | no |
| `P-ZSTD`, `P-LZ4` | `CompressionUtils` / `NativeCompression` | compression | managed LZ4 (Core) / ZSTD | no |
| `P-RUNINBG` | `Application.runInBackground` (public) | host keeps simulating when alt-tabbed | warning | no |
| `P-ICONQUEUES` | `IconCommandSystem.m_Queues` (`List<NativeQueue<IconCommandBuffer.Command>>`) and `m_Dependencies` (`JobHandle`) (`IconCommandSystem.cs:698-700` [V]) | icon authority filter on clients (§4.10.4) | pure icon producers stay gated off; per-owner icon digest repairs the rest | no |

**Optional Harmony patches** (not shipped in v1; each needs a decision record backed by a measurement): **H1** a prefix on `IconCommandSystem.OnUpdate` on clients that drops vanilla non-`Temp`/`Marker`/`Transaction` icon commands; superseded by the reflection-based icon authority filter and kept only as the fallback if `P-ICONQUEUES` fails and the residual flicker is measured (R14 §4.5); **H2** a prefix on `UnlockSystem.OnUpdate` on clients that runs only `ProcessEvents`, only if the requirement loop is observed to unlock prefabs the host has not (R14 §2.4); **H3** a prefix on `GameManager.Save` on clients, only if other mods start saves that collide with sessions (R12 §6.2).

---

## Appendix C. Message catalogue

| Kind | Lane | Direction | Content |
|---|---|---|---|
| `Hello`, `Welcome`, `Refuse` | R | both | handshake digests, session id, role, settings; mismatch report |
| `Control` | R | both | session settings, roles, players, `HostBusy`, `HostReady`, `SessionEnd`, `SessionReset`, kick, ban, chat, speed requests and changes, resync requests, `ProposalReceived{seq}` (host → originator, on receipt), line-digest rows on request (`/st lines compare`, §4.11.6) |
| `Epoch` | R | host → client | sections and records (§4.6.2-4.6.3), possibly as several parts of one epoch |
| `Ack` | U (10 Hz) and R (on reconnect) | client → host | last applied epoch, last applied `MotionControl` sequence (releases it confirms) |
| `Proposal` | R | client → host | change sets (possibly in parts), terrain strokes, semantic intents (§5.3) |
| `Motion` | U | host → client | Q-B datagrams per tick (keyframes and keyframe-referenced deltas with assignment generations) |
| `MotionControl` | R | host → client | interest slot assign (with generation and valid-from tick) / release / release-all, Q-A baselines, pin instructions |
| `Clock` | U | host → client | clock samples |
| `View` | U | client → host | view report |
| `Presence` | U | client → host → others | cursors and ghosts |
| `HashDetail`, `RepairRequest` | R | both | bucket details, repair requests with a level hint; the host decides (§8.5) |
| `BlobManifest`, `BlobChunk`, `BlobHave` | R / B / R | both | snapshots, rebaselines, resume points, tiles |
| `Ping` | U | both | RTT measurement |

---

## Appendix D. Default constants

| Item | Default |
|---|---|
| Per-peer cap | 300 KB/s relay, 2 MB/s direct/LAN; floor 48 KB/s |
| Host total upload cap | 1.5 MB/s |
| Shares after P0 | P1 40 %, P2 45 %, P3 15 % (work-conserving) |
| AIMD | queue time > 150 ms → −20 %; < 30 ms while backlogged → +5 %/s |
| Scan periods | G every frame; V 4 s; A, A-low, M discrete 60 s; S safety 20 s; K trees 120 s |
| Scan budget | 1.0 ms (≥ 50 fps), 2.0 ms (≤ 30 fps) |
| Hot set cap | 2048 entities |
| S value coalescing | ≤ 10 Hz per entity |
| Apply budget | 3 ms live; 6 ms or 200 epochs during catch-up |
| Interest radius | `clamp(k × zoom, 300 m, 2500 m)`, leave at 1.25 ×, ≥ mover draw distance |
| Clock lag `D` | `clamp(⌈L + max(0, renderLead) − 16 + 4⌉, 0, 160)` ticks, slew ±1 tick/s |
| Extrapolation cap | 32 ticks, then pin |
| Pose refresh (out of interest) | ≤ 1 per mover per 30 s |
| Staleness promotion quanta | 2, 4, 8, 16, 32 s |
| Pending references | 600 epochs (S/G/A/M targets), 120 s (A-low targets) |
| Derived-key retry | 2 s |
| Proposal timeout | 10 s from `ProposalReceived`; 30 s connection-level before it; suspended during `HostBusy`; tombstones kept 5 min |
| Beacons | 32 buckets/s per peer; 1024/s during verification and hash resync; level 1 after two mismatching beacons ≥ 250 ms apart |
| Tolerance | after 2 failed repairs, masked 10 min; escalate if > 1 % of a class |
| Repair (client) | coalesced per 100 ms; ≤ 5 requests/s, ≤ 256 slots each |
| Repair (host) | accepts ≤ 10/s with ≤ 512 slots (excess throttled, never a kick); ≤ 1 concurrent and ≤ 3 rebaselines per peer per 10 min; > 3 snapshot resyncs in 30 min → disconnect "repair loop" |
| Epoch log | 128 MB or 10 min |
| Epoch size | part ≤ 8 MiB uncompressed; epoch ≤ 256 MiB in total |
| Outbox (per peer) | lane R window `SendBufferSize − 1 MiB`; bulk window 2-8 MiB and only with an empty R queue; disconnect above 64 MB or 60 s queued |
| Steam send buffer | 16 MiB per connection |
| Motion | keyframe every 4th sample of a mover (64 ticks); 4-bit assignment generation; interest-slot reuse only after the release is acknowledged |
| Snapshot cache | 120 s or ≤ 64 MB of log since its epoch; ≤ 2 concurrent transfers |
| Blob chunk | 256 KiB with xxHash64; blob xxHash128; window 2-8 MiB |
| Short-drop resume | 5 min grace; motion interest reset on resume |
| Heartbeat | 3 s warning, 30 s disconnect, 2 min reconnect window; 60 s during `HostBusy` |
| Host session start | ~8 ms per frame, session paused; watchdog 60 s without progress |
| Load hand-over | disable `SimulationSystem` as soon as `m_IsLoading` is false after `WorldReady` (normally one frame); reflection fallback after 10 frames; retry up to 60 frames |
| Route-tool preview window | open while `RouteToolSystem` is active; linger 2 s after deactivation or the last apply; only with a green route guard |
| Flow-status channel | only for peers with the Electricity or Water topic; keyframe on activation; deltas ≤ 1 Hz |
| Line digest | `/st lines compare` on demand; after joins with the S class; every 60 s in diagnostics mode |
| Received-value cache | pairs with derived keys + `cacheReceived` types; soft cap 32 MB (beyond it, the oldest non-line pairs fall back to repair requests) |
| Limits | proposals 50/s, change-set part ≤ 4 MB (≤ 64 MB and ≤ 200k entities per proposal), chat 5/s and ≤ 500 characters, names ≤ 64 characters, uncompressed message ≤ 8 MiB |

---

## Appendix E. Initial client gate table

Resolved by type name; the phase column is informational (the gate works whatever the phase).

| Phase | Off on clients |
|---|---|
| `GameSimulation`, `EditorSimulation`, `LoadSimulation` | never executed (§6.1); additionally every system there is disabled except `GameModeGovernmentSubsidiesSystem`. The cosmetic runner calls only `cosmetic.json` systems, which excludes `StreetLightSystem` (§6.1) |
| Deserialize (**session-load set**, only from a session load's `OnGamePreload` until `OnGameLoadingComplete`, §6.3) | `ResetBuildOrderSystem`, `CitizenSystem`, `RandomLocalizationSystem` |
| ToolUpdate | `UpgradeDeletedSystem` |
| Modification1 | `AddMeetingSystem`; `UnlockAllSystem`; `RoutePathReadySystem` (replaced by `RouteReadyBridgeSystem`) |
| Modification2 | `Game.Events.InitializeSystem`, `OutsideConnectionInitializeSystem`, `DamageSystem`, `DestroySystem`, `GroundHeightSystem` (+ drain), `HouseholdAndCitizenRemoveSystem` |
| Modification3 | `DefaultPoliciesSystem` |
| Modification4 | `HouseholdInitializeSystem`, `Game.Routes.InitializeSystem`, `HouseholdPetRemoveSystem`, `ImpactSystem`, `IgniteSystem`, `FaceWeatherSystem`, `AddHealthProblemSystem`, `SpectateSystem`, `EndangerSystem`, `AddAccidentSiteSystem`, `AddCriminalSystem`, `SubmergeSystem`, `TripResetSystem`, `EventJournalInitializeSystem` |
| Modification4B | `ObjectEmergeSystem`, `ParkedVehiclesSystem`, `BuildingStateEfficiencySystem` |
| Modification5 | `RemovedSystem`, `ZoneBuiltRequirementSystem`, `ResourcesInitializeSystem`, `BatteryInitializeSystem`, `ParkInitializeSystem`, `CitizenInitializeSystem`, `HouseholdPetInitializeSystem`, `MeetingInitializeSystem`, `Game.Citizens.CompanyInitializeSystem`, `StorageInitializeSystem`, `CityServiceWorkplaceInitializeSystem`, `EventJournalSystem`, `GroupSystem`, `FixParkingLocationSystem`, `FixLaneObjectsSystem`; `WaypointConnectionSystem` (**tool-scoped**: on inside the route-tool preview window, §4.11.4) |
| ModificationEnd | `ZoneCheckSystem`, `ServiceRequestSystem`, `XPBuiltSystem`, `NetXPSystem`, `XPSystem`, `XPAccumulationSystem`, `MilestoneSystem` (+ feed), `TriggerSystem`, `LifePathEventSystem`, `CreateChirpSystem`, `RadioTagSystem`, `StrictObjectBuiltRequirementSystem`, `ObjectBuiltRequirementSystem`, `PrefabUnlockedRequirementSystem`, `DevTreeSystem`, `RandomLocalizationInitializeSystem`, `NotificationTriggerSystem`, `ParkingLaneDataSystem`, `PathOwnerTargetMovedSystem`, `BoardingVehicleSystem`, `CityServiceEfficiencySystem`, `CityServiceBudgetSystem` (+ feed), `ConnectionWarningSystem`, `TutorialSystem` and the other tutorial systems; `RoutePathSystem` (**tool-scoped**, as `WaypointConnectionSystem`) |
| LateUpdate | `AutoSaveSystem`; `SimulationSystem` is disabled by the load hand-over after `WorldReady`, once its own update cleared `m_IsLoading` (§6.1), never by the table |
| Mods | per adapter (§10.4) |

**Kept (informational)**: all `Generate*`, reference, geometry, composition, lane, block, cell-check, sub-object, secondary, aggregate and search systems; `UpdateGroupSystem`; `Vehicles`/`Creatures` initialize and reference systems; `IconCommandSystem` (mandatory; client-local commands filtered, §4.10.4), `IconDeletedSystem`, `MarkerCreateSystem`; `UnlockSystem`; `ModifiedSystem`; `BuildingPoliciesSystem` (predicted policy toggles; its icons filtered); `LoanSystem`; `GameModeGovernmentSubsidiesSystem`; `CitySystem`; `ValidationSystem`; the tools; `RoadConnectionSystem` (icons filtered, road choice reconciled, §4.11.5); `SegmentCurveSystem`, `Routes.SearchSystem`, `Routes.ReferencesSystem`, `Routes.ElementSystem`, `RouteBufferSystem` (fed by host line state, §4.11.3); `TrafficLightInitializationSystem`; `CurrentDistrictSystem`; `UnspawnedSystem`; animation and mesh systems; pathfinding setup/result and `RouteDataSystem`/`RoutesModifiedSystem` (local previews); `ToolFeedbackSystem`.

Sources: R11 §8.1, R14 §5.5, R15 §10.3, P-A §5.2, P-B §6.1, P-C §5.2; design review of version 1.0 (session-load set, removal systems, double-applying initializers, street lights); the line and status-indicator analysis of §4.11 (route systems, `ConnectionWarningSystem`). The M3 spawn-equality test and the write auditor classify the kept Modification-phase systems that `TypeAudit` lists as writers of replicated types (for example `CargoTransportStationInitializeSystem`, `InitializeSchoolSystem`, `WaterPoweredInitializeSystem`, `ServiceUpgradeSystem`); until then the end-of-frame re-application (§4.7.8) covers them.

---

## Appendix F. Policy defaults

- **Family default classes**: §3.2 and §4.2.2.
- **Player-authorable by default** (derived from what each vanilla apply system copies from its temp; "presence" = only adding or removing the component is authorable, because the vanilla apply adds a default value, `updateValue: false`):
  - nets (`ApplyNetSystem.cs:385-470` [V]): `Node`, `Edge`, `Curve`, `Owner`, `PrefabRef` (class-checked, §5.4.1), `Game.Net.Elevation`, `Upgraded`, `SubReplacement` buffer, `NetCustomColorVariation`, `Road` (authorable mask: `m_Flags` without `LightsOff`); presence of `LocalConnect`, `TrafficLights`, `Orphan`, `Native`, `Standalone`, `LandValue`, `TramTrack`, `TrainTrack`, `Waterway`, `Pollution`, `Marker`;
  - objects (`ApplyObjectsSystem.cs:352-414` [V]): `Transform`, `Attached`, `Game.Objects.Elevation`, `Owner` removal on kept sub-nets of a deleted object; `PrefabRef` only through `ReplaceObjectPrefab` (rebuild, upgrade in place) or adapter-declared swaps (Tree Controller); `Tree`, `Decoration`/`LumberResource` enabled bits;
  - areas: `Game.Areas.Node` buffer; routes: `Waypoint`, `Position`, `Connected`, `Game.Routes.Color`; zones: cell overlays (§4.10.7);
  - UI-edited: `Policy`, `CustomMeshColor`, `VehicleModel`, `ServiceDistrict`, `Building` (authorable mask: historical flag), `Followed`;
  - mod types through adapters.
- **Re-application after creation** (§4.7.8): default for every replicated type; `noReapply` opts out (none by default). `TrafficLights` is additionally restored by `TrafficLightDeriveSystem` right after its initializer.
- **Authorable masks**: `Road` (`m_Flags` without `LightsOff`), `Building` (historical flag); used for proposal commits and revisions (§3.8, §4.2.5).
- **Tag rules**: `BatchesUpdated` for `Damaged`, `Destroyed` visuals, `CustomMeshColor`, `Unspawned`, colour components, `Building.Illuminated`; `EffectsUpdated` for `Car`, `Aircraft`, `Watercraft`, role components with boarding states, `ParkedCar`/`ParkedTrain`/`Stopped` presence, `OnFire`, `Efficiency`, and the `StreetLight` sub-objects of owners whose light flags changed; `PathfindUpdated` for lane-relevant edge components (`Upgraded`, edge policies, traffic configuration).
- **`hashAsSet`**: the types of §4.2.4.
- **Line state** (§4.11.3): on the `Segment` family `PathTargets`, `PathInformation`, `PathElement` are `source` (S) instead of OnDemand; `PathInformation` on segments is `presenceSafe` for `m_Origin`/`m_Destination` (stand-in: the waypoint's `RouteLane` lane, else the waypoint); `cacheReceived` and `guarded`: `Route`, `Waypoint`, `Position`, `Connected`, `AccessLane`, `RouteLane`, `PathTargets`, `PathInformation`, `PathElement`, `RouteWaypoint`, `RouteSegment`, `ConnectedRoute`; V on structure families: `TransportLine`, `RouteInfo`, `WaitingPassengers`, `VehicleTiming`, `TransportStop`, `BoardingVehicle`, `TaxiStand`, `RouteVehicle`; K types on station stops: `TransportStop`, `BoardingVehicle`, `TaxiStand`, `WaitingPassengers`, `ConnectedRoute`; K types on gate-bypass routes: `HiddenRoute` presence and the segments' path state; `HiddenRoute` on transport lines excluded (per player).
- **Deferred fields**: `Building.m_RoadEdge`, `m_CurvePosition` (applied by `RoadConnectionReconcileSystem`, §4.2.5).
- **Attention topics**: §4.8.3.
- **Excluded / OnDemand / merge**: §4.2.5.

---

## Appendix G. Open product decisions (defaults chosen)

| Decision | Default | Alternatives |
|---|---|---|
| Speed policy | host and co-hosts | host only, anyone, majority vote |
| Host pause menu pauses the session | no | yes |
| Automatic speed cap for weak links | on | off |
| `Followed` citizens | shared (vanilla 50-entry city limit) | per player |
| Water correction quality | Low | Off, Medium, High |
| Local-only mod relaxation | off (strict, hard constraint 6) | on for the verified list |
| Tutorials | off for every peer during sessions | per-player tutorials (later) |
| Default role for new players | Builder | Viewer |
| Session speed after host session start | paused (the host unpauses) | the speed the save was saved with |
| "Host this city" while the tutorial intro is active | refused with an explanation (§7.11) | host anyway with milestones blocked on the host |
| Route-tool preview on clients | vanilla preview inside a guarded preview window (§4.11.4) | straight preview segments without local path finding (also the automatic fallback when the guard self-test fails) |
| Line visibility toggle in the transportation overview | per player (vanilla view preference, `HiddenRoute`) | shared |

---

## Appendix H. Decision records and change process

This document is the contract. A change to an interface, a class, a frame position, a probe, or a default in Appendix D needs a short decision record in `docs/decisions/NNNN-title.md` (context, decision, consequences, how it was verified) and an update of this document in the same change. Data files (`gates.json`, `cosmetic.json`, `policies.json`, `adapters.json`, `sysstate.json`, `locale.*.json`) can change without a decision record but need a unit test (Core parsing), a passing `ApiCheck` name resolution (every type and system name resolves against `Game.dll` and, where installed, the mod DLLs) and, for behavioural changes, a diagnostics dump that shows the effect in game. Every data file except the locale tables is part of the handshake digest (§4.2.7), so all peers of a session must use the same effective files.

---

## Appendix I. Glossary

| Term | Meaning |
|---|---|
| Slot / NetId | dense host-assigned entity index / slot plus generation (§3.1) |
| Epoch | one host frame's replication output; unit of ordering, catch-up and acknowledgement |
| Transaction | the structural records of one epoch, applied atomically on clients |
| EDC / SDC | event-driven capture from the game's one-frame tags / scan-driven capture by the time-sliced chunk scanner |
| Canonical bytes | a type's `Serialize` output through `NetWriter`; the unit of wire transfer and hashing |
| Hash shadow / value shadow | host's per-(slot, type) hashes / canonical bytes of the last captured values |
| Dirty bit | per peer: "this peer lacks the latest value of this (slot, type)" |
| Attention | the per-peer set of entities and types the player can currently observe |
| Rolling refresh | budgeted fair delivery of all remaining dirty values |
| Ledger | client's per-(slot, type) hashes of the last applied values |
| Beacon | host bucket sum sent when the peer has nothing pending in that bucket |
| Derived key | portable identity of a regenerated entity (lane, sub-object, block, ...) |
| K channel | replication of host-owned state stored on derived entities |
| Pinning | writing a mover's last pose into all four frame slots with zero velocity |
| Proposal | a client's captured state difference or semantic intent, committed only by the host |
| Held payload | a host value withheld on the originator while its own proposal on that pair is pending |
| Probe | a validated non-public access with a fallback |
| Gate table | the list of systems disabled on clients (plus the session-load set disabled only during session loads) |
| Capture window | the part of the host frame (the capture flush) in which engine state may be mutated |
| Host session start | binding and shadow priming of the whole city before the first peer is admitted |
| Load hand-over | the frame-exact switch from vanilla `SimulationSystem` to the follower clock after a client load |
| Epoch part | one message of an epoch whose transaction exceeds the per-message cap |
| Outbox | the per-peer application-level send queue that keeps Steam's pending bytes inside a window |
| Original set | the `Temp` originals (and their owned sub-elements) recorded at capture stage B |
| Icon authority filter | client system that drops client-local persistent icon commands before `IconCommandSystem` plays them back (§4.10.4) |
| Preview window | tool-scoped period in which selected derivation systems run on a client for its own tool preview (§6.3, §4.11.4) |
| Route guard | restores host line state on entities outside the local edit while a preview window is open (§4.11.4) |
| Received-value cache | client copy of the last received canonical bytes of pairs with derived references or guarded types (§3.7) |
| Re-resolution | re-applying cached bytes after the client regenerated the derived children they reference (§3.7, §4.7.5) |
| Line digest | per-line row of exactly what the vanilla line UI computes, compared between host and client (§4.11.6) |
| Path-result trigger | host capture of a segment's path state on the pathfinder's `PathUpdated` event (§4.4.2 step 3a) |

---

## Review log

Version 1.1 answers the design review of version 1.0 (42 findings: 8 critical, 22 major, 12 minor). Every finding that cites game code was re-checked in the decompiled sources before it was accepted; the verdict column says what was confirmed and what was corrected. "Plan" refers to `IMPLEMENTATION-PLAN.md` version 1.1.

| # | Sev. | Area | Verdict | Resolution |
|---|---|---|---|---|
| 1 | critical | Follower clock after a client load | **Accepted.** Confirmed: `selectedSpeed` ignores writes while `m_IsLoading` (`SimulationSystem.cs:72-85`), the flag is cleared only in `OnUpdate` once `isGameLoading` is false (`:202-215`), and `onGameLoadingComplete` runs before `m_State = WorldReady` (`GameManager.cs:1136-1137`). Correction: a premature `pausedAfterLoading` restore lets that update run at most two ticks at speed 1 (`round(speed · 2)`, `:255`), not eight; the conclusion is unchanged. | Load hand-over waits for the first post-load update of vanilla `SimulationSystem`, disables it after `WorldReady` once the flag is clear (normally the next frame), restores `pausedAfterLoading` only then; probe `P-SIMLOADING` verifies and is the fallback; per-frame read-back self-check (§6.1, K3, §4.7.1, §7.1 step 11, §7.4, App. B, §13.4 j, §16.1 risk 21, §16.2 item 4). The same state machine restores the host's setting (§7.11). Plan: core-follower `LoadHandover`, game-follower. |
| 2 | critical | Join snapshot exactness | **Accepted.** Confirmed: `ResetBuildOrderSystem` runs unconditionally (`SystemOrder.cs:857`, `ResetBuildOrderSystem.cs:62-148, 188-201`); `CitizenSystem` re-rolls every citizen `PrefabRef` with `RandomSeed.Next()` (its `Exclude<RandomLocalization>` names a prefab class, so every citizen matches). Additionally found: `RandomLocalizationSystem` re-rolls out-of-range name indices (matters only under a locale difference). Checked and not added: the `DataMigration` systems are gated on `context.version`/`format`. | Session-load set of vanilla Deserialize rewriters disabled for session loads; manifest field `hostBuildOrderNext` restores the counter of `GenerateEdgesSystem`; load-parity census in the M2 script finds further rewriters (§6.3, §7.1 steps 6/9/10/11, §7.3, §7.4, §7.6, §5.4.3, App. E, K12, §16.1 risk 22). |
| 3 | major | Capture stage C candidates | **Accepted.** Confirmed: only `ApplyNetSystem` tags updated originals `Applied` (`:901-903`); objects, areas, routes and zones add only `Updated` to `Temp.m_Original` (`ApplyObjectsSystem.cs:595`, `ApplyAreasSystem.cs:175`, `ApplyRoutesSystem.cs:317`, `ApplyZonesSystem.cs:106`). | Stage B records the original set (originals and owned sub-elements); stage C candidates = `Applied` ∪ changed members of the original set ∪ updated zone blocks of recorded originals ∪ tracked entities created in the apply frame; one M4 test per apply system (§5.2, K8, App. F). |
| 4 | major | Apply-only side effects of host commits | **Accepted with a correction.** Confirmed `PathTargetMoved` for moved buildings and transport stops (`ApplyObjectsSystem.cs:608-663`), the upgrade/rebuild path (`:378-397`) and `RemoveOldSubItems` (`:404-411, 453-470`). Correction: `FixParkingLocation` is added to parked cars and trains that are themselves part of the applied set (`:363, 417, 442-451`), not to vehicles owned by a moved object. Also found while reviewing all apply systems: `PathTargetMoved` for moved route waypoints (`ApplyRoutesSystem.cs:322-333`), the `BicycleRoadBan` trigger (`ApplyNetSystem.cs:682-687`), and `Owner` removal on kept sub-nets of a deleted object (`ApplyObjectsSystem.cs:484-505`). | `HostSideEffects` reproduces every effect; rebuild and upgrade in place become the semantic intent `ReplaceObjectPrefab`, so host-only types never travel in a change set (§5.4.3, §5.3, §5.2 rules). |
| 5 | major | Kept Modification-phase initializers on spawned entities | **Accepted.** Confirmed `BatteryInitializeSystem` (`:56-90`, which also adds or removes the battery-empty icon on every `Created`/`Updated`) and `CityServiceWorkplaceInitializeSystem` (`:103-120`). | End-of-frame re-application of the payload of every spawned or upserted entity is the default (`noReapply` opt-out replaces `resetByKeptInitializer`); entities changed without a full payload and upgrade owners are re-hashed against the ledger; both systems are also gated off; M3 spawn-equality test per building family (§4.7.8, §4.7.1, §4.2.7, §6.4, §6.6, App. E/F, §16.1 risk 23). |
| 6 | minor | Kept removal systems on clients | **Accepted.** Confirmed `HouseholdAndCitizenRemoveSystem` (`:100-117, 180-241`) and `RemovedSystem` (`:196-229`). | Both, plus `HouseholdPetRemoveSystem`, are off on clients; the host co-captures reverse buffers in the same transaction as the forward reference that changed them (old owner from the sweep or the value shadow); `RentersUpdated` is synthesised only after the `Renter` buffer of the property was applied (§4.2.4, §4.2.6, §4.4.1, §4.4.2, §4.7.7, §4.7.8, §6.4, App. E). |
| 7 | minor | Prefab-state capture trigger | **Accepted.** Confirmed: `Event` entities die at the end of their frame (`PrepareCleanUpSystem.cs:21-28`). | The tag sweep records whether `Unlock` events existed; the next two capture flushes scan the unlock bits; the 10 s scan stays as a safety net (§4.4.1, §4.10.3). |
| 8 | minor | Time-aligned inspection | **Accepted.** Confirmed `UIUpdateState.Advance` is relative (`:28-38`) and the tag-triggered refreshes (`SelectedInfoUISystem.cs:383-386`). | The host calls `RequestUpdate()` at each sample tick; the claim is narrowed to V/A values on every client and on the host at each periodic refresh; S values, client tag-triggered refreshes and the host's own tag-triggered refreshes (live values) are documented (K7, §0.4, §4.7.9). |
| 9 | minor | Terrain stroke replay | **Accepted.** Confirmed `ApplyBrush` uses `Time.unscaledDeltaTime` clamped to [0.05, 1] (`TerrainSystem.cs:3766-3775, 3968-3979`). Also found: the stroke payload lacked `Brush.m_Tool` (used to look up the brush tool strength) and the opacity. | Stamps carry the clamped frame time of the originator; replaying machines pre-scale the strength through the inverse of the strength mapping of the game (Core function); payload completed; self-test at 15 and 60 fps (§4.10.9, §5.3, §13.4 l). |
| 10 | critical | `RESULT`/`BIND` without origin | **Accepted.** | Both records carry the origin peer, stay in the shared section, and only the origin acts on them; two-client unit test (§3.1, §4.5.4, §4.6.3, §5.4.2, §5.5.1). |
| 11 | critical | Type-set dictionary for joiners | **Accepted**, option (b). | One session-global registry, no per-peer state; a type-set keyframe (TSET section) precedes log epoch E for joiners and snapshot resyncs; redefinitions must match; an unknown id is a resync, not a session close (§3.6, §4.6.2-4.6.3, §4.7.6, §7.1 step 12, §7.8, §12.2). |
| 12 | critical | No binding of the existing city at session start | **Accepted** (merged with 31). | Host session start: catalog, `BindExisting`, bind sweep with no records, shadow priming without dirty bits, time-sliced while paused, lobby opened afterwards; the snapshot bind pass only binds genuinely new entities; `spawnEmitted` bit; 1 M-entity FakeWorld join test (§4.4.8, K17, §4.4.6, §4.4.3, §3.1, §7.1 steps 0-1). |
| 13 | critical | Slot → entity resolution | **Accepted.** Confirmed: `GetStorageInfo` → `GetEntityInChunk` checks only the allocated bit (`EntityDataAccess.cs:1453-1461`, `EntityComponentStore.cs:692-708`); `Exists` compares versions (`:660-672`); a missing type yields a null column (`ArchetypeChunk.cs:730-738`). | Resolution contract (`Exists`, then `GetStorageInfo`, then `chunk.Has`; unbind and park on failure); client liveness pass and budgeted `Exists` sweep; the merger drops values superseded by a despawn or by a removing `STRUCT` (§3.3, §4.4.4, §4.7.3, §4.7.4, §4.7.6). |
| 14 | critical | Apply failure mid-frame | **Accepted.** Confirmed: prefab archetypes contain `Created`/`Updated`. | Decode-then-commit for components (length-exact) and buffers (scratch, then resize and copy); spawns from a staging archetype without `Created`/`Updated`, which are added only after the whole transaction succeeded; on failure the creations of the plan get `Deleted`, the follower freezes and the first resync starts at once (D6, §1.1, §3.6, §4.3.5, §4.7.2-4.7.4, §12.2). |
| 15 | major | `StreetLightSystem` on the cosmetic list | **Accepted.** Confirmed RW access to `Road`, `Building`, `Watercraft` and the client-local inputs (`StreetLightSystem.cs:83-151, 170-180`). | Removed from the cosmetic list; the flags of the host replicate; `StreetLightDerive` uses the public static `UpdateStreetLightState` on sub-objects and tags like vanilla; cosmetic write audit at startup (§2.1, §2.4, §6.1, §6.4, §6.5, §4.7.7, App. E/F). |
| 16 | major | Revision conflicts from simulation writes | **Accepted.** Confirmed `Road` mixes tool flags and traffic floats; also found that the vanilla net apply copies only `Road.m_Flags` from the temp (`ApplyNetSystem.cs:629-647`), which defines the authorable part. | Revisions count authored changes only (commits, EDC changes of the authorable projection, structure); SDC never bumps; `authorableMask` for `Road` and `Building`; host commits merge only authorable fields; validator test (§3.8, §4.2.5, §4.4.2, §4.4.3, §5.4.1, §5.4.2, App. F). |
| 17 | major | Transport back-pressure | **Accepted.** | Per-peer outbox with an R window and a bulk window that leaves headroom; `Busy` keeps the message queued; value sections are built only when the window has room; dirty bits cleared only on acceptance; the queue counts toward the speed cap and a 64 MB / 60 s disconnect; fake-`Busy` unit test (§9.3, §9.6, §4.5.3, §4.8.4, §4.8.5, §7.5, §12.2, App. D). |
| 18 | major | Motion deltas over the unreliable lane | **Accepted.** | Keyframes every fourth sample; deltas reference a named keyframe tick; flags, state and timer absolute in keyframes; a delta without its reference is dropped; NetSim loss and reorder test (§4.9.3, §4.9.6, §0.6, §4.6.6). |
| 19 | major | Interest-slot mapping across lanes | **Accepted.** | 4-bit assignment generation in assignments and in every record; a released interest slot is reused only after the client acknowledged the release (`Ack` carries the last applied `MotionControl` sequence) (§4.9.3, App. C, App. D). |
| 20 | major | Large legitimate transactions | **Accepted.** | Epoch parts (≤ 8 MiB each, ≤ 256 MiB per epoch) applied atomically after the last part; an oversized well-formed epoch is a resync; the host encoder splits; proposals may arrive in parts as well (§4.5.4, §4.6.2, §4.6.5, §4.7.10, §5.4.1, §5.6, §12.2, §14.3, App. D). |
| 21 | major | Beacons vs values not yet sent | **Accepted**, with a different mechanism for S/G: instead of skipping buckets with pending entries (which would almost never verify the City bucket, because money changes every tick), the S/G hash shadow is updated when the record is emitted. | Shadow-at-emission for P0 classes; every capture sets the dirty bit of every peer (motion sampler included); level 1 needs two mismatching beacons ≥ 250 ms apart; only confirmed differences count toward rebaselines (§4.4.5, §4.5.1, §4.5.3, §4.9.4, §8.4, §8.5). |
| 22 | major | Slot reuse and the `SPAWN` upsert | **Accepted.** | Reconcile in place only if generation, family and prefab match; otherwise delete, unbind and recreate; same rule for `Repair` spawns and predictions attached by `BIND` (D7, §3.1, §4.7.3, §4.7.6, §5.5.1, §8.5). |
| 23 | major | Host engine threading | **Accepted.** | Capture window as the only mutation phase; `EngineCommandQueue` for calls from outside it, drained after `WaitForFrameWork`; proposal commits and repair re-serialisations moved into the capture flush (ingress hook); debug phase assertions and a Core concurrency stress test (§4.5.5, §4.4.2, §4.1, §5.4.2, App. A). |
| 24 | major | Commit-bound entities never spawned | **Accepted.** | `spawnEmitted` bit per slot; the flush spawns every bound entity whose bit is false; the membership net checks it too; two-client E2E test (§3.1, §4.4.2, §4.4.3, §5.4.2). |
| 25 | major | Proposal timeout races | **Accepted.** | `ProposalReceived` echo; timer from the echo; suspended during `HostBusy`; tombstones make late acceptances ignore their binds; tracker test (§5.4.2, §5.5.2, §6.2, §9.6, §12.4, App. C/D). |
| 26 | major | Version skew through data-file overrides | **Accepted.** | Digest of the effective content of every data file in `Hello`; `SYS` entries identified by stable string hashes; the enabled entries of the host in `Welcome`, and the client disables what it has not enabled (§4.2.7, §4.10.1, §7.1 steps 2-3, §7.9, App. H). |
| 27 | major | `PrefabRef` swap validation | **Accepted.** | Prefab class signature per rank; swaps keep family and class and require an unlocked prefab; adapters may widen; curve end points must match their nodes (§5.4.1, §14.3, App. F). |
| 28 | major | Repair traffic from parked references | **Accepted.** | The ledger records received (also parked and held) values; the verifier skips pending pairs (parked, held, awaiting re-application, tolerated, covered by proposals); client repair requests are coalesced and limited below the host limit; the host throttles and never kicks for repair rate (§4.7.5, §8.3, §8.4, §8.5, §5.6, §14.3). |
| 29 | minor | Short-drop resume and motion state | **Accepted.** | Interest tables reset on both sides on resume (`ReleaseAll`, fresh baselines and generations); inspection sample and cell-map keyframes re-sent when needed (§7.8). |
| 30 | minor | Host-side limits for expensive repairs | **Accepted**, reconciled with 28. | The level requested by the client is a hint; the host keeps the ladder per peer, allows one concurrent and three rebaselines per 10 minutes, turns excess into one snapshot offer, and disconnects only on a repair loop (> 3 snapshot resyncs in 30 minutes) with dumps (§8.5, §14.3, App. D). |
| 31 | critical | First join of a never-hosted city; empty shadow | **Accepted** (merged with 12). | Adds `ObserveSource.Prime`, the scanner baseline at session start, and the E2E assertion that a join emits no `SPAWN` for pre-existing slots (§4.4.8, §4.5.1, §4.4.3). |
| 32 | major | Main-menu Host flow | **Accepted.** Confirmed the vanilla load preparation (`MenuUISystem.cs:789-805`) and the tutorial intro switch at load (`TutorialSystem.cs:301-306`). | Host flow with `HostSource {Save, CurrentCity, LobbyOnly}`, `saves` binding, `host(saveId, settingsJson)` and `hostCurrentCity` triggers; adjustments before the load; "Host this city" refused while the tutorial intro list is active; M1 hosts lobby-only (§7.11, §11.1, §11.3, App. G; Plan §5, §7.4, §7.12, §7.14, §7.16). |
| 33 | major | Testability of the game layer | **Accepted.** | Core owns canonical encoding (`CanonicalWriter`/`Reader`, golden vectors), family rules, scan scheduling, apply planning (the FakeWorld executes the same programs), gate planning, the load hand-over and cosmetic scheduling; game types forward (K16, §3.2, §4.3.1, §4.4.3, §4.7.2, §6.1, §6.3; Plan: new modules core-codec and core-follower). |
| 34 | major | Unchecked `.mjs`, no fallback | **Accepted.** Confirmed `SettingsUITextInputAttribute` support (`AutomaticSettings.cs:1091-1097`). | `node --check` and a smoke script in the build (skipped without Node); session controls on the settings page (host, host this city, join by address, leave, status) (§11.2, §11.6, §13.4 g, §16.1 risk 20; Plan §2.6-2.7). |
| 35 | major | M2 measurement build vs module content | **Accepted.** | `MeasurementProbe` stand-ins (tag counter, chunk change-version sampler, canonical sizes, mover distance rings, timings); the list of values M2 replaces and of those that stay provisional (§13.7, §15.3, App. A.3). |
| 36 | major | Traffic and Road Speed Adjuster | **Accepted.** | Research task in M1 (decompile both installed DLLs; adapters before the M3/M4 contracts freeze); "Traffic installed" in the M3 record/replay script (§10.4, §16.2 item 6; Plan §5, §8). |
| 37 | minor | Milestone granularity | **Accepted.** | M5 split into M5a (values without attention), M5b (channels), M5c (attention, inspection, per-peer priorities), each with its own script (Plan §5, §8; §15.3). |
| 38 | minor | Dependency graph vs contracts | **Accepted.** | Cross-module hooks moved to game-bootstrap interfaces, `ClientEndOfFrameSystem` moved to game-client-apply, `LobbyInfo` moved to core-session, `IWorldLoader.OpenBlobTarget` added; dependency table regenerated and checked by `ApiCheck` (Plan §6.1, §7). |
| 39 | minor | Game project references | **Accepted.** Confirmed `cohtml.Net.dll`, `Colossal.UI.dll`, `System.IO.Compression.dll` and `UnityEngine.IMGUIModule.dll` are in `Managed` (173 DLLs). | Reference every `Managed/*.dll` with `Private=false` except a test-assembly denylist (Plan §2.4). |
| 40 | minor | Core compile errors | **Accepted.** | `NoWarn CS1591`; PBKDF2-HMAC-SHA256 implemented over `HMACSHA256` with RFC 7914 vectors; one fallback wording (managed LZ4) (§14.2; Plan §2.1-2.3, §7.4). |
| 41 | minor | Localisation | **Accepted.** | Embedded `locale.en-US.json` from M0, overrides per locale, strings for JS resolved in C#, key-coverage test (§11.3, §11.7; Plan §3, §5, §7.12). |
| 42 | minor | Pre-game verification gaps | **Accepted.** | `ApiCheck` resolves every type and system name of the data files against `Game.dll` and installed mod DLLs; tests multi-target `net8.0;net472` (App. H; Plan §2.4-2.5). |

**Changes found while verifying, beyond the findings.** (1) `HashLedger` now records received rather than applied values, so held and parked pairs cannot cause beacon mismatches (§8.3). (2) The authorable defaults of Appendix F are derived from what each vanilla apply system copies, with presence-only entries where the apply adds default values. (3) The proposal limits allow parts (a Move It edit of tens of thousands of objects from a client would otherwise be rejected). (4) "Host this city" must not run while the tutorial intro list is active, because that list blocks `MilestoneSystem` on the host (`MilestoneSystem.cs:72-75`). (5) The host's panel cannot withhold live values on its own tag-triggered refreshes, so finding 8's claim is narrowed one step further (§4.7.9). (6) Re-checking finding 38 against the final contracts found five undeclared module references, now declared in the plan's dependency table (checked by `ApiCheck`): game-steam-transport → core-session (`SessionController`, lobby types), game-ecs-schema → core-replication (`PolicyTable`), game-join → game-follower (`HostSessionAdjustments`, `FollowerMode`), game-integrity → core-session (`SessionController`), game-fields → core-session (`WaterQuality`).

### User requirement U1: transport lines and every status indicator host-authoritative

**Requirement** (from the user's experience with another co-op mod, where a train line showed "not connected" for one player and "connected" for another): line state must be fully host-authoritative, covering stops and waypoints with their `Connected` links, segments, segment paths and path status, the warnings and problems the line UI shows, vehicle counts and line lists; the same principle applies to every other player-visible status indicator; with digest coverage and an explicit acceptance test.

**Verification in the decompiled code** (details and citations in §4.11.1-4.11.2):

| Finding | Evidence | Consequence for version 1.0 |
|---|---|---|
| The line diagram draws a segment **broken** when its `PathInformation` has null origin and destination; length and stop count come from `PathInformation` and waypoint `Connected` | `LineVisualizerSection.cs:580-585, 703-721`, `TransportUIUtils.cs:41-98` | the "not connected" display is a pathfinding result, not route structure |
| Segment paths are asynchronous pathfinder results requested by `RoutePathSystem`, also on every local lane deletion or lane apply; failures write null end points and distance −1 | `RoutePathSystem.cs:387-510, 552-635`, `PathfindJobs.cs:1083-1131, 1237-1266` | version 1.0 kept `RoutePathSystem` on clients (§2.1): each client computed its own connectivity, the reported failure class |
| Line warnings are icons on the line and on every waypoint; the "path not found" icon comes from `RoutePathReadySystem` on local path results | `NotificationsSection.cs:111-121`, `RoutePathReadySystem.cs:229-262` | version 1.0 kept `RoutePathReadySystem` on clients and relied on "identical inputs" for client icon producers (§4.10.4) |
| `WaypointConnectionSystem` clears `Connected` when the stop is missing or deleted, re-snaps lanes and maintains `ConnectedRoute`; station stops are building sub-objects (derived on clients) | `WaypointConnectionSystem.cs:63-140, 1132-1140`, `SubObjectSystem.cs:2549` | a client-side stop regeneration could disconnect a waypoint on one client only; `ConnectedRoute` would go stale with the system off |
| `RouteVehicle` and `Passenger` are edited at runtime by simulation systems; the kept reference systems react only to `Created`/`Deleted` | `Game.Vehicles/ReferencesSystem.cs:466-477, 698-706`, `TransportLineSystem.cs:266-310`, `ResidentAISystem.cs:3590-3813`, `Game.Creatures/ReferencesSystem.cs:115-200` | version 1.0 listed both as rebuilt by kept systems (§4.2.4): line vehicle counts and usage would drift |
| Other client-side status producers: `ConnectionWarningSystem` (dead ends, track and pipe connection warnings; icons only), `RoadConnectionSystem` (road-access icon), `BuildingPoliciesSystem` (turned-off icon); `IconCommandSystem` keeps its producers' queues in a private list | `ConnectionWarningSystem.cs:435, 681-820, 2200-2400`, `RoadConnectionSystem.cs:596-645`, `BuildingPoliciesSystem.cs:83-90`, `IconCommandSystem.cs:698-770` | local status icons were possible on clients; a filter without Harmony is feasible |
| Electricity and water connectivity in the infoviews uses flow-edge flags that only the host's flow simulation updates | `ElectricityFlowEdge.cs`, `ElectricityFlowEdgeFlags.cs`, `ElectricityConsumerFlags.cs`, `WaterConsumerFlags.cs` | version 1.0 sent flow values only "optionally" |
| A host commit that re-links an existing waypoint to another stop skips `ApplyRoutesSystem`'s `ConnectedRoute` bookkeeping; `WaypointConnectionSystem` handles only created and deleted waypoints | `ApplyRoutesSystem.cs:21-72, 334-346`, `WaypointConnectionSystem.cs:63-140` | a host-side gap in §5.4.3 (stop line lists wrong on every peer after a client re-routed a line) |

**Resolution** (sections changed): principle D11 and decision K19 (§0.2, §0.3); §4.11 (new: sources, divergence analysis, host-authoritative line state, guarded route-tool preview window, every other status indicator, verification and acceptance test ST-LINES); path-result trigger (§4.4.2 step 3a); family-scoped policies and the flags `source`, `cacheReceived`, `presenceSafe`, `guarded` (§4.2.5, §4.2.7, App. F); `Passenger`, `RouteVehicle`, `ConnectedRoute`, `GuestVehicle` as data with co-capture (§4.2.4, §4.2.6, §2.3); K channel for station stops and gate-bypass routes, owned-route element keys (§3.7, §4.10.7, §4.3.3); received-value cache, derived-reference index and re-resolution after local regeneration (§3.7, §4.7.5, §4.7.1); synthesised `PathUpdated` and `RouteReadyBridgeSystem` (§4.7.7, §4.7.8); icon authority filter with probe `P-ICONQUEUES` replacing the Harmony option H1 (§4.10.4, App. B); `RoadConnectionReconcileSystem` with `Building` road fields as deferred fields (§4.2.5, §4.7.8, §4.11.5); re-linked waypoints in the host-side effects (§5.4.3); flow-status channel (§2.1, §4.8.3, §4.11.5); tool-scoped gates (§6.3); gate table (§6.4, App. E); frame order and registrations (§4.1, §4.7.1, App. A.3); digests and diagnostics (§8.6, §13.3-13.5); risks 24-26 (§16.1); verification items 7a, 10a, 11a (§16.2); product decisions (App. G). Plan: contracts in core-contracts, core-identity, core-replication, core-follower, core-integrity, game-ecs-schema, game-host-capture, game-client-apply, game-state-channels, game-follower and game-integrity; milestone exit criteria and scripts M3, M4, M5b, M5c and M6.
