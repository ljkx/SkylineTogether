# Action coverage

This document covers every player action from research 10 (`docs/research/10-player-actions.md`) and the vanilla tool
actions. For each one it lists how the action syncs in both directions and where it is implemented. The design references are ARCH §5 and R10 §2-§7.

## How each action travels

| Path | Client → host | Host → clients | Code |
|---|---|---|---|
| **Tool** | The vanilla tool applies locally, so the client sees its own change at once (prediction). At the end of the frame the capture collects the entities tagged `Applied`, the Temp originals and `Deleted`, plus the zone cells and the tool cost, and sends them as a proposal batch with a `ProposalHeader` (seq, base epoch, flags). | The entity capture (`Created`/`Updated`/`Deleted`) runs every frame. | `SyncController.OnToolApply`, `CaptureClient`, `CaptureHost` |
| **UI delete** | The client sends a `Despawn` with the `UiDelete` flag for entities that the vanilla UI deleted outside any tool. This applies only to districts, service upgrades and lines whose owner still exists. | Entity capture. | `SyncController.IsUiDeletion` |
| **Global** | A delta of a global channel: taxes, budgets, fees, loan, policies (`Modify` events), names, city name. | The full channel state at ≤ 1 Hz when it changed, plus a 30 s keyframe. | `GlobalStateSync` |
| **Intent** | `GlobalChannel.Action`, carrying `ActionIntent` records. The host validates each one, runs the vanilla data path, and marks the target as authored. | The target is streamed by the capture (`_forceCapture`). | `ActionSync` |
| **Unlocks** | (The host's own result.) | `GlobalChannel.Unlocks` sends the full set of unlocked prefabs. Clients replay it as vanilla `Unlock` events, re-lock where needed, and show milestone pop-ups. | `ActionSync.HostCapture`, `ApplyUnlocks`, `Milestones` |
| **Speed** | A speed request (control message). Viewers are refused. | `SpeedChanged`. | `SpeedAuthoritySystem`, `SessionHost` |

Every proposal is validated on the host (ARCH §5.4.1, `SyncController.Validate`, `ProposalRules`):

- **Role permission.** `RolePermissions`: builder or co-host may do everything; a viewer may only chat. The host toggles the role in the player list.
- **Rate limit.** 120 proposals per second per player, with a burst of 240.
- **Size.** At most 200k entities per proposal.
- **Existence and conflicts.** Every entity the proposal edits or deletes must still exist. Its last authored change must not be newer than the client's base epoch, unless that change came from the same player (`AuthoredLedger`).
- **Id ranges.** New entities must use ids from the player's granted ranges.
- **Money.** The charge must be affordable unless money is unlimited. A refund needs at least one removed or changed entity.

When a proposal is refused, the originator receives `ProposalResult` and then a resend of every entity it touched:

- An entity the host still has comes back as its full host record.
- An entity the host does not have (the client's predicted creations) comes back as a despawn.
- Every painted zone block comes back with the host's cells.

The player sees a toast such as "The host undid your last change: another player changed the same thing first."
Floods and malformed proposals count as violations, and 20 of them within 10 s kick the player.

A client edit of an existing entity never writes host-owned types (`EntityCodec.HostOwnedTypes`):

- line state: route number, vehicle models, colour, paths, lanes, connections, route vehicles;
- service districts and mesh colours, because those travel as intents;
- the simulation buffers of buildings.

So a tool edit made within the same round trip cannot revert a UI value or simulation data.

The host re-bases the build order of a client's fresh edges onto its own counter (`BuildOrderPlanner`,
`BuildOrderAllocator`, the counter from `GenerateEdgesSystem.GetBuildOrder()`). Zone priority therefore matches on every
peer. Edges created by splitting a host edge keep the sub-range they inherited.

## Table

Status values:

- **OK**: synced both ways by the listed path.
- **Local**: per-player by design.
- **Partial**: synced, with the limitation noted.

| # | Action (R10 id) | Path | Status | Notes |
|---|---|---|---|---|
| 1 | Place road, track, path, pipe or power line (net tool, every mode, parallel and grid) | Tool | OK | Build orders re-based on the host. Splits keep the inherited ranges. |
| 2 | Net upgrades: lanes, trees, sound barriers, wide sidewalks, one-way flip, replace | Tool | OK | `ApplyNetSystem` tags the originals `Applied`+`Updated`. `Upgraded` replicates. |
| 3 | Roundabouts and other node or edge objects | Tool | OK | Created objects plus the updated nodes. |
| 4 | Place a building, prop, tree, decal or surface (object tool, brush, stamp, line or curve modes) | Tool | OK | Line Tool and Tree Controller use the same apply path (mod-specific work is handled on another branch). |
| 5 | Move or relocate a building or object (`toggleMove`, `UpgradesSection.relocate`) | Tool | OK | The original gets `Updated` and is captured as a Temp original. |
| 6 | Building upgrades and extensions (place) | Tool | OK | `ServiceUpgrade` objects are first-class. The host's initializers and `UpgradeDeletedSystem` stay host-only. |
| 7 | Building upgrade or extension removal (`UpgradesSection.delete`, D2) | UI delete | OK | The host's `UpgradeDeletedSystem` regenerates sub-nets and areas, and they are broadcast (it is gated on clients). |
| 8 | Rebuild a destroyed building (`DestroyedBuildingSection.toggleRebuild`) | Tool | OK | The Upsert with its complete type set removes `Destroyed`/`Damaged` on the host. Simulation buffers stay the host's. |
| 9 | Lot editing (`toggleLotTool`) | Tool | OK | Area node edits. |
| 10 | Demolish (bulldozer, main elements) | Tool | OK | Refunds through the `Recent` cost and the cost check. |
| 11 | Bulldozer "sub-elements" and "everything" modes | Tool + Intent `DeleteSubElements` | OK | Derived props and fences have no net id. They are sent as derived keys, which are resolved strictly (same kind, prefab and owner, ≤ 1 m). The host replays them to every other client. |
| 12 | Zoning (paint, fill, marquee, dezone) | Tool (zone cells) | Partial | Last writer wins per block. There is no per-block revision check yet. |
| 13 | District painting and editing (area tool) | Tool | OK | |
| 14 | District delete (`ActionsSection.delete`, D1) | UI delete | OK | |
| 15 | Map tile purchase (G2) | Intent `PurchaseMapTiles` | OK | Detected at frame start from the un-`Native`-ed tiles and the money the vanilla handler took. The host checks permits (`GetAvailableTiles`, which honours mod patches), the tiles and the money. Refused purchases re-add `Native`. |
| 16 | Dev-tree purchase (G1) | Intent `DevTreePurchase` | OK | Detected from the local `Unlock` event plus the drop in points. The host runs `DevTreeSystem.Purchase` in the late phase. The prediction is held for 3 s against re-locks. |
| 17 | Milestone unlocks (host simulation) | Unlocks | OK | Clients replay `Unlock` events, so the vanilla UI refreshes, and they get `MilestoneReachedEvent` pop-ups. Tutorial prefabs are excluded. |
| 18 | Policies: city, district, building, line (P1, P2) | Global `Policy` | OK | Replayed as vanilla `Modify` events. A target deleted meanwhile counts as a conflict. |
| 19 | Service building on/off, upgrade on/off, line active (P3, P5, P9, P10) | Global `Policy` | OK | |
| 20 | Emptying (P4) | Global `Policy` | OK | |
| 21 | Line ticket price, vehicle count, day/night schedule (P6-P8, P11) | Global `Policy` | OK | |
| 22 | Line vehicle models (R1, R2) | Intent `SetVehicleModels` | OK | The whole buffer is sent, which avoids the swap-back order. Detected on the selected line. Host edits travel by the line value scan or a forced capture. |
| 23 | Service districts of a building (R3 remove, R4 selection tool) | Intent `SetServiceDistricts` | OK | The whole buffer. Detected on the selected building or the selection tool's owner. The host tags `Updated`. |
| 24 | Line colour (C1, C2) | Intent `SetRouteColor` | OK | Detected from `ColorUpdated` events. Clients colour the line's vehicles and get the event when the host value arrives. |
| 25 | Building mesh colours, paste, reset, "set to all" (C3, C4) | Intent `SetMeshColor` | OK | Every entity tagged `BatchesUpdated` is checked against the last state. The applier restores the enable bit and the render refresh. |
| 26 | Global colour palette (C5) | — | Local | Treated as a per-player preference (R10 Q7). The colours applied to buildings still sync (row 25). |
| 27 | Historical toggle (C6) | Intent `SetHistorical` | OK | This vanilla write has no tag at all. It is watched on the selected building. |
| 28 | Rename an entity or line (N1, N2) | Global `Name` | OK | `SetCustomName` now runs in the late phase. Before, it threw at frame start because the EndFrameBarrier was closed. |
| 29 | City name (N3) | Global `CityName` | OK | |
| 30 | Taxes (E4-E6) | Global `Tax` | OK | |
| 31 | Service budgets (E1, E3) | Global `Budget` | OK | `SetServiceBudget` now runs in the late phase (same EndFrameBarrier fix as row 28). |
| 32 | Service fees (E2) | Global `Fee` | OK | |
| 33 | Loan (E7) | Global `Loan` | OK | Re-clamped against the host's money. |
| 34 | Transport line draw, edit, stops (route tool) | Tool | OK | Guarded preview window, then the host's line state. `RouteNumber` and `VehicleModel` take host values (host initializers on creation; never overwritten by client edits). |
| 35 | Line delete (D1, D3) | UI delete | OK | |
| 36 | Cargo lines | as rows 21, 22, 24, 34, 35 | OK | Cargo lines are transport lines. |
| 37 | Outside connections (road or rail connected to the map edge) | Tool | OK | `OutsideConnectionInitializeSystem` is host-only. |
| 38 | Water source add, edit, remove (water tool) | Tool | OK | `WaterSource` family. Mod water tools are handled on another branch. |
| 39 | Terraforming | Terrain stamps (`TerrainSync`) | OK | |
| 40 | Disasters and emergencies | Host simulation | OK | No player trigger exists apart from rebuild (row 8) and policies (rows 18-19). |
| 41 | Pause and speed (T1, T3) | Speed | OK | Clients send requests (viewers' requests are refused). On a host with live players, TimeUISystem's forced pause (pause menu, focus loss) no longer pauses everyone. A pause that someone chose is kept. |
| 42 | Follow citizen (F1) | Global | OK | Channel `Follow` (`GlobalStateSync.Panels.cs`, SYNC-COVERAGE fix 13): clients send follow/unfollow deltas, the host follows through the vanilla API, agent replication brings `Followed` and the life path to everyone. Needs the Names permission, so viewers cannot follow. Chirp likes (F2) and tutorials (F3) stay local per-player meta; tutorial prefabs stay out of the unlock set. |
| 43 | Signature building notice, milestone notice clear, camera, info views, photo mode | — | Local | Presentation (R10 §6.1). |
| 44 | Viewer role | No-predict gate | OK | `ViewerGateSystem` rewrites `Apply` to `Clear` before `ToolOutputSystem`. The host refuses viewers' proposals in any case. |
| 45 | Direct-write mod tools (Move It, Zoning Toolkit, ...) | Tool (direct-write flag) | Partial | Generic un-applied capture while a non-vanilla tool is active (flag `DirectTool`, permission `ModTools`). Mod-specific robustness is handled on another branch. |

## Integration notes

- New record kinds use the reserved range: `SyncRecordKind.Epoch` = 80, `ProposalHeader` = 81, `ProposalResult` = 82. Global channel ids are string hashes: `"Action"`, `"Unlocks"`. No new `MessageKind`.
- The host stamps every structural broadcast with its epoch number. This is the last record, so routing by first record still works. Joiners get the current epoch in their first per-peer batch.
- `ActionSync.AfterWrite` is the applier hook for state that vanilla keeps beside a component value.
- `EntityCodec.Apply` skips `HostOwnedTypes` only when the host applies a client proposal to an entity that already exists.
