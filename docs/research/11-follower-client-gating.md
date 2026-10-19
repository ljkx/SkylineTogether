# 11 – Follower-client system gating audit and the single apply point

> **Historical research report.** Written before and during the implementation; it describes the game, not the
> mod, and is not maintained. For how the mod works, see [docs/ARCHITECTURE.md](../ARCHITECTURE.md). Code is cited
> by file and member and described in prose; no game code is reproduced.

Research for **Skyline Together** (clean-room, host-authoritative co-op mod for Cities: Skylines II).
All paths are relative to the root of the game's decompiled assemblies unless stated otherwise.
Line numbers refer to the decompiled files as read on 2026-10-10. Prior reports are cited as `02 §x`, `03 §x` and so on.

Legend:

- **[V]** verified: read in the decompiled code at the cited location.
- **[I]** inference: follows from verified code plus general Unity DOTS knowledge, but was not proven line by line.

Classification codes used in the tables:

| Code | Meaning |
|---|---|
| **D** | Derived or structural. Output is a function of replicated state. Safe on clients. |
| **G** | Gameplay-mutating. Writes money, XP or statistics; creates events, icons, chirps or service requests; uses `RandomSeed` for gameplay; adds or removes `Abandoned`, `Condemned`, `Destroyed`, `OnFire` and the like; or creates sim agents. |
| **M** | Mixed. Mostly derived, but has at least one gameplay-relevant write or an overwrite hazard. |
| **C** | Cosmetic or local-only (presentation, audio, local UI, local tool preview). |

Client action codes:

| Code | Meaning |
|---|---|
| **KEEP** | Leave enabled on clients. |
| **OFF** | Disable on clients (`Enabled = false`, re-asserted after every load, see §8.3). |
| **OFF+feed** | Disable on clients and replicate its *output* from the host. |
| **INTENT** | Leave the UI in place but patch the mutating call so that it sends a request to the host. |

**Out of scope:** the editor (`SystemUpdatePhase.EditorSimulation`, `EditorTutorialSystem`, `ApplyPrefabsSystem`'s `SaveInstance` path and editor-only UI systems). The editor uses the same Modification pipeline, but co-op is only specified for `GameMode.Game`. Every statement below assumes `ToolSystem.actionMode.IsGame()` (`Game/Game.Simulation/SimulationSystem.cs:284-287`) [V].

---

## 0. TL;DR

1. **One apply point, resolved from code.** All host deltas (create, update, delete, tags) are applied by a client-only system registered as
   `updateSystem.UpdateAfter<NetApplySystem, EndFrameBarrier>(SystemUpdatePhase.MainLoop)`.
   This places it immediately after `EndFrameBarrier` playback and before `RaycastSystem`, `ToolSystem`, `LoadGameSystem` and `ModificationSystem` (`Game/Game.Common/SystemOrder.cs:47-60`; `Game/Game/UpdateSystem.cs:178-181, 382-441`) [V].
   It is the only position that is before **all** of the following, as the code requires:
   - `OriginalDeletedSystem` (PreTool, `SystemOrder.cs:699`);
   - `SubElementDeleteSystem` (PostTool, `:725`, and nowhere else);
   - `CourseSplitSystem` (PostTool, `:724`);
   - `Generate*` (Modification1, `:95-103`);
   - every initializer;
   - `ValidationSystem` and all mod enforcers in ModificationEnd (`:264`, 09 §5.4).

   Report 02 §9.1 was right. The other positions fail:
   - 03 §9.6 ("before ModificationSystem") is too late for deletions, because it misses PreTool and PostTool.
   - 08 §12.4's "after ModificationEnd" loses tags (`PrepareCleanUpSystem`, `SystemOrder.cs:50`; 02 §5.2).
   - 09 §5.4's "Modification1" misses PreTool and PostTool, and a `ModificationBarrier1` write is only played back at the end of Modification1 (`SystemOrder.cs:86`) [V].
   See §6.
2. **No second "re-apply" pass is needed** if clients disable every initializer whose overwrite is not derived. That covers the sim-agent initializers, `DefaultPoliciesSystem`, `Routes.InitializeSystem`, the random-localization initializers, `ResourcesInitializeSystem` and `ParkInitializeSystem`.
   The initializers that stay enabled only overwrite fields that are client-local (lane positions, navigation targets), derived from prefab data, or rewritten every frame by the TransformFrame stream. See §2 and the overwrite table.
3. **Three consumers must stay enabled on clients, or their producers leak:**
   - `IconCommandSystem` keeps a `TempJob` queue list and has no `Enabled` guard (`Game/Game.Notifications/IconCommandSystem.cs:727-731`).
   - `TriggerSystem.CreateActionBuffer` has only a log-only assert (`Game/Game.Triggers/TriggerSystem.cs:405-411`; `Unity.Entities/Unity.Assertions/Assert.cs:1,21-27`).
   - `CityStatisticsSystem.GetStatisticsEventQueue` is a persistent queue with a log-only assert (`Game/Game.Simulation/CityStatisticsSystem.cs:840-845`).

   Alternatively, every producer that does not check `Enabled` must be disabled together with its consumer. The game already uses the guard pattern in a few places, e.g. `ModifiedSystem.cs:412,441-448` and `CityStatisticsSystem.SafeStatisticQueue` at `:64-85`. §4 lists every pair.
4. **Hidden gameplay writers outside GameSimulation** (03 open question 3) [V]:
   - **Modification2:** `Game.Events.InitializeSystem` (random event targets, `Ignite`/`Impact`… command events, triggers, icons), `DamageSystem`, `DestroySystem`.
   - **Modification3:** `DefaultPoliciesSystem` (appends to `Policy` buffers).
   - **Modification4:** all `Add*`/`Ignite`/`Impact`/`Submerge`/`FaceWeather`/`Endanger`/`Spectate`/`TripReset` command consumers, `HouseholdInitializeSystem` (spawns citizens, pets and cars, adds money), `Routes.InitializeSystem` (route number, vehicle model).
   - **Modification4B:** `ObjectEmergeSystem` (converts occupants into creatures when buildings or vehicles are deleted).
   - **Modification5:** `CitizenInitializeSystem`, `CompanyInitializeSystem` (rent queue), `StorageInitializeSystem`, `HouseholdPetInitializeSystem`, `MeetingInitializeSystem`, `ResourcesInitializeSystem` (additive), `ParkInitializeSystem`, `EventJournalSystem`, `ZoneBuiltRequirementSystem`.
   - **ModificationEnd:** `ZoneCheckSystem` (`Condemned` and icons), `XPBuiltSystem` (also writes prefab state), `NetXPSystem`, `XPSystem`, `MilestoneSystem` (money, creditworthiness), `ServiceRequestSystem` (random `UpdateFrame`, dispatch), `TriggerSystem`, `CreateChirpSystem`, `LifePathEventSystem`, `NotificationTriggerSystem`, the three `*RequirementSystem`s, `RandomLocalizationInitializeSystem`.
   - **ToolUpdate:** `UpgradeDeletedSystem` (creates `Permanent` definitions when an upgrade is deleted).
5. **Clock.** A Harmony *prefix that only changes inputs* cannot force the tick count. The count is a local variable capped by `num7 ≤ 8`, by CPU back-pressure and by the pathfinding deadline (`SimulationSystem.cs:220-257`) [V]. Replace `OnUpdate` for clients (prefix returning `false`, §7).
   Once the 45 `PathfindSetupSystem.GetQueue` callers plus `ServiceCoverageSystem` and `ResourceAvailabilitySystem` are disabled, client-side pathfinding enqueues all use `uint.MaxValue` deadlines (`RoutePathSystem.cs:634`, `CoveragePreviewSystem.cs:409`, `ToolFeedbackSystem.cs:1490`). `pendingSimulationFrame` then stays `uint.MaxValue` and there is no back-pressure [V].
   `selectedSpeed` must still mirror the host, because `RenderingSystem` freezes interpolation when it is 0 (`Game/Game.Rendering/RenderingSystem.cs:186-189`) [V].
6. **03's cosmetic whitelist mostly holds** (§3). Corrections:
   - `ClimateSystem` emits trigger actions, but already guards them with `TriggerSystem.Enabled` (`ClimateSystem.cs:479`).
   - `WetnessSystem` creates `SubObjectsUpdated` events, which make `SubObjectSystem` add or remove sub-objects (`WetnessSystem.cs:89-95`). Treat those sub-objects as local children.
   - `ZoneAmbienceSystem`/`TrafficAmbienceSystem` reset their maps from accumulators fed by GameSimulation producers. With those producers off, client ambient audio decays to zero (`ZoneAmbienceSystem.cs:20-24`). Keep `SpawnableAmbienceSystem`, or ship the maps.
   - Wind, sun and daylight feed pollution, power output and road safety only through systems that are disabled on clients.
7. **Runtime `Deserialize` of system state:**
   - **Side-effect-free** for `Residential/Commercial/IndustrialDemandSystem`, `TaxSystem` and `TradeSystem` *if* the context has the current version and all `FormatTags`.
   - **Not side-effect-free** for `CityStatisticsSystem.PostDeserialize`, which can destroy or create statistic entities (`CityStatisticsSystem.cs:787-789, 826`).
   - **Not possible** for `CityServiceBudgetSystem`, which is not `ISerializable` (`CityServiceBudgetSystem.cs:26`) [V].

   A default-constructed `Context` has an uncreated `ContextFormat`, so `format.Has(...)` indexes an empty list. Tag bits can only be set through the internal `EnableTag` (`Colossal.Core/Colossal.Serialization.Entities/ContextFormat.cs:11-36`) [V]. See §5.

---

## 1. Frame skeleton recap (only what the decisions depend on)

| Order inside one rendered frame | Registration | Citation |
|---|---|---|
| `UpdateWorldTimeSystem`, `PathfindQueueSystem`, **`EndFrameBarrier`** (plays back last frame's sim and UI ECBs) | `UpdateBefore` MainLoop | `SystemOrder.cs:47-49`; `EndFrameBarrier.cs:34-43` [V] |
| **← proposed `NetApplySystem` (`UpdateAfter<NetApplySystem, EndFrameBarrier>`)** | two-type registration, placed right after the parent | `UpdateSystem.cs:178-181, 278-292, 382-441` [V] |
| `RaycastSystem`, `PrefabSystem`, `CityConfigurationSystem` | `UpdateAt` | `SystemOrder.cs:55-57` [V] |
| `ToolSystem` → PreTool (`OriginalDeletedSystem`) → ToolUpdate (tools, `UpgradeDeletedSystem`, ClearTool/ApplyTool) → PostTool (`ToolFeedbackSystem`, `SelectedUpdateSystem`, `CourseSplitSystem`, **`SubElementDeleteSystem`**, `MapTileSystem`, `ToolReadyBarrier`) | | `SystemOrder.cs:58, 694-726` [V] |
| `LoadGameSystem` (only while loading) | | `:59` |
| `ModificationSystem` → Modification1 … ModificationEnd | | `:60, 78-306` [V] |
| `UnlockSystem`, `AllowBarrier<EndFrameBarrier>`, `PreRenderSystem`, `PathfindSetupSystem`, `PathfindResultSystem`, `AchievementTriggerSystem`, `UIUpdateSystem`, `RenderingSystem`, `SaveGameSystem` | | `:61-69` [V] |
| `PrepareCleanUpSystem` (snapshots tags and `Deleted`) | `UpdateAfter` MainLoop | `:50` [V] |
| Cleanup: `AudioManager`, `AnimatedSystem`, `BatchUploadSystem`, `CleanUpSystem`, `CompleteCullingSystem`, `CompleteEnabledSystem` | | `:51-54, 691-692` [V] |
| LateUpdate: `DebugSystem`, **`SimulationSystem`** (PreSimulation → N × GameSimulation → PostSimulation), `CompleteRenderingSystem`, `GizmosSystem`, `AutoSaveSystem` | | `:70, 74-77`; `SimulationSystem.cs:272-295` [V] |

Facts the rest of the report relies on:

- `PreSimulation` has **no registered systems** in vanilla. The only references are the two `Update(PreSimulation)` calls (`SimulationSystem.cs:168, 272`; grep of `SystemUpdatePhase.PreSimulation` over `Game/`) [V].
- `PostSimulation` contains only `WaterSystem` (`SystemOrder.cs:631`) [V].
- `LoadSimulation` (`SystemOrder.cs:611-630`) runs only while `m_LoadingCount > 0`, which is set to 1024 **only for `Purpose.NewGame`** (`SimulationSystem.cs:160, 164-186`) [V]. A client that joins by loading the host's save never runs it [I: assumes the join uses `Purpose.LoadGame`].
- `UpdateWorldTimeSystem` is not in the decompiled sources. It is Unity's world-time system [I].
- An exception in any system is caught and logged, and the phase continues (`UpdateSystem.cs:201-214, 253-262`) [V].

---

## 2. Classification of every system outside GameSimulation

Each table lists systems in registration order. "Write site" cites the code that makes a system G or M. D and C rows cite only the registration line plus a one-line reason.

### 2.1 MainLoop, LateUpdate and Cleanup

| System | Reg. | Class | Client | Evidence |
|---|---|---|---|---|
| `UpdateWorldTimeSystem` | `SystemOrder.cs:47` | D | KEEP | engine time [I] |
| `PathfindQueueSystem` | `:48` | D | KEEP | worker queues. Needed for local route, coverage and tool previews and graph maintenance (enqueue sites in §7.2) [V] |
| `EndFrameBarrier` | `:49` | D | KEEP | ECB playback + `lastElapsedTime` (`EndFrameBarrier.cs:34-43`) [V] |
| `PrepareCleanUpSystem` | `:50` | D | KEEP | tag snapshot (02 §5.2) |
| `RaycastSystem` | `:55` | C | KEEP | local raycasts |
| `PrefabSystem` | `:56` | D | KEEP | prefab update phase |
| `CityConfigurationSystem` | `:57` | D | KEEP | `OnUpdate` is empty (`CityConfigurationSystem.cs:180-182`). Its settings must equal the host's at load. `OnGameLoaded` toggles `UnlockAllSystem.Enabled` (`:195-198`) [V] |
| `ToolSystem` | `:58` | C | KEEP + apply interception | local previews (§6.5) |
| `LoadGameSystem` | `:59` | D | KEEP | load only (`LoadGameSystem.cs:51-57`) [V] |
| `ModificationSystem` | `:60` | D | KEEP | runs Modification phases |
| `UnlockSystem` | `:61` | **M** | KEEP, fed by host `Unlock` events | `UnlockPrefab` writes the prefab `Locked` enable bit and creates `Unlock` event entities (`UnlockSystem.cs:206-213`). Fixpoint over requirement prefabs (`:143-177`). Processes any `Unlock` event idempotently, only if still locked (`:179-204`, check at `:192`). Re-enables itself in `OnGamePreload` (`:121-125`) [V] |
| `AllowBarrier<EndFrameBarrier>` | `:62` | D | KEEP | |
| `PreRenderSystem` | `:63` | C | KEEP | PreCulling |
| `PathfindSetupSystem` | `:64` | D (idle) | KEEP | returns immediately with no active queues (`PathfindSetupSystem.cs:314-317`). Uses `RandomSeed.Next()` only when queues exist (`:341-342`) [V] |
| `PathfindResultSystem` | `:65` | M | KEEP | applies completed path, coverage and availability results and creates `PathUpdated` events through `EndFrameBarrier` (`PathfindResultSystem.cs:209-273, 246-254`). On clients the only sources are local route, coverage and tool queries (§7.2) [V] |
| `AchievementTriggerSystem` | `:66` | C | KEEP (product decision) | platform achievement calls only (`AchievementTriggerSystem.cs:66, 72, 94, 110`). No ECS writes found [V] |
| `UIUpdateSystem` | `:67` | – | KEEP | see §2.10 |
| `RenderingSystem` | `:68` | C | KEEP | chases `SimulationSystem.frameIndex` (`RenderingSystem.cs:175-200`) [V] |
| `SaveGameSystem` | `:69` | D | KEEP | save only |
| `DebugWatchSystem` | `:71` | C | KEEP | debug |
| `DebugSystem` (LateUpdate) | `:70` | G (dev only) | OFF in sessions | debug actions write money, XP, triggers (matrix grep: `PlayerMoney`, `XPSystem`, `TriggerAction` hits) [V hits / I usage] |
| `SimulationSystem` | `:74` | clock | REPLACE `OnUpdate` | §7 |
| `CompleteRenderingSystem`, `GizmosSystem` | `:75-76` | C | KEEP | |
| `AutoSaveSystem` | `:77` | D | OFF | 05 §9.4 |
| Cleanup (`AudioManager`, `AnimatedSystem`, `BatchUploadSystem`, `CleanUpSystem`, `CompleteCullingSystem`, `CompleteEnabledSystem`) | `:51-54, 691-692` | C/D | KEEP | presentation and tag cleanup |

### 2.2 PreTool, ToolUpdate, ApplyTool, PostTool

| System | Reg. | Class | Client | Evidence |
|---|---|---|---|---|
| `OriginalDeletedSystem` | `:699` | D | KEEP | sets `m_OriginalDeleted` when a `Temp.m_Original` is `Deleted` or gone (`OriginalDeletedSystem.cs:31-49, 113-128`). Consumed by `ToolBaseSystem.GetAllowApply` and `GetRaycastResult` (`ToolBaseSystem.cs:533-540, 555-559`) [V] |
| Tool systems (`AreaToolSystem` … `WaterToolSystem`) | `:700-710` | C | KEEP (preview); apply → INTENT | local `Temp` previews (02 §2-4) |
| **`UpgradeDeletedSystem`** | `:711` | **G** (structural) | **OFF** | for a deleted `ServiceUpgrade` (query `:618`), creates `CreationFlags.Permanent` definitions that rebuild the owner's sub-areas and sub-nets (`UpgradeDeletedSystem.cs:250, 348`). Enabled in game (`:625`). On a client this would duplicate the host-replicated rebuilt entities [V code / I duplication] |
| `ToolClearSystem` | `:712` | C | KEEP | clears local Temps |
| ApplyTool systems (`ToolApplySystem` … `ApplyWaterSourcesSystem`) | `:713-721` | **G** | INTENT (never let a client reach `applyMode == Apply`) | `ToolApplySystem` subtracts Σ`Temp.m_Cost` from `PlayerMoney`; Apply* promote Temps to permanent entities (02 §4) |
| `ToolFeedbackSystem` | `:722` | C | KEEP | query `Applied+Created` or `Deleted` buildings, upgrades and stops (`ToolFeedbackSystem.cs:1302-1341`). Creates local `Feedback` containers (`:1399`) and `Transaction`-layer happy/sad icons (`:1445-1447, 936`). Enables itself in game (`:1366-1370`). With `Applied` on replicated creations, every player sees feedback for host-applied placements (cosmetic) [V] |
| `SelectedUpdateSystem` | `:723` | C | KEEP | moves local selection off deleted entities (`SelectedUpdateSystem.cs:24-60`) [V] |
| `CourseSplitSystem` | `:724` | D | KEEP | splits local `NetCourse` definitions (02 §2.4) |
| **`SubElementDeleteSystem`** | `:725` | D | KEEP | for `Deleted` owners: deletes sub-areas, sub-routes and owned vehicles. Deletes sub-nets, or detaches them and tags neighbours `Updated` (`SubElementDeleteSystem.cs:76-79, 97-130, 146-160`). Idempotent with host deletions (`Deleted` twice is harmless) [V/I] |
| `MapTileSystem` | `:726` | D | KEEP | removes deleted map tiles from `m_StartTiles` (`MapTileSystem.cs:110-125`) [V] |

### 2.3 Modification1

| System | Reg. | Class | Client | Evidence |
|---|---|---|---|---|
| `RoutePathReadySystem` | `:94` | M | KEEP | on `PathUpdated` events: gate-bypass and pathfind warning icons, `HiddenRoute` add/remove (`RoutePathReadySystem.cs:151-170, 193-197, 258-262, 362`). Derived from local route paths. Icon adds are deduplicated (§4) [V] |
| `GenerateObjectsSystem`, `GenerateNodesSystem`, `GenerateZonesSystem`, `GenerateAreasSystem`, `GenerateWaypointsSystem`, `GenerateNotificationsSystem`, `GenerateBrushesSystem`, `GenerateAggregatesSystem`, `GenerateWaterSourcesSystem` | `:95-103` | D | KEEP | definitions → Temps. Only local definitions exist on clients (02 §3.2) |
| `LoanSystem` | `:104` | **G** | OFF (or KEEP + INTENT) | `PlayerMoney.Add` and `Loan` write from `m_ActionQueue` (`LoanSystem.cs:36-46`). The queue is fed only by `ChangeLoan` (`:130-135`), which `LoanUISystem.cs:76` calls [V] |
| `CitySystem` | `:105` | D | KEEP | caches money (`CitySystem.cs:45-49`). Creates the City entity only in `PostDeserialize` (`:78-114`) [V] |
| `UnlockAllSystem` | `:106` | M | KEEP (config-driven) | disabled by default (`UnlockAllSystem.cs:37`), enabled from `CityConfigurationSystem.unlockAll` (`CityConfigurationSystem.cs:197`). Creates `Unlock` events (`UnlockAllSystem.cs:41-55`). Must match the host's setting [V] |
| **`AddMeetingSystem`** | `:107` | **G** | OFF | drains `m_MeetingQueue` and creates `CoordinatedMeeting` event entities plus `AttendingEvent` (`AddMeetingSystem.cs:68-90, 178-192`). Producers are `LeisureSystem.cs:1012` and `TouristFindTargetSystem.cs:308` (GameSimulation) [V] |
| `ElectricityGraphDeleteSystem`, `WaterPipeGraphDeleteSystem` | `:108-109` | D | KEEP | flow-graph maintenance (02 §6) |

### 2.4 Modification2

| System | Reg. | Class | Client | Evidence |
|---|---|---|---|---|
| **`Game.Events.InitializeSystem`** | `:110` | **G** | **OFF** | for every `Created+Event` (query `InitializeSystem.cs:260`) it:<br>- enforces the concurrent limit by adding `Deleted` (`:304-307`);<br>- picks **random targets** and creates `Ignite`/`Impact`/`AddAccidentSite`/`AddHealthProblem`/`Damage`/`Destroy`/`Spectate`/`AddCriminal` command events (`:309-348, 415-439`; `RandomSeed.Next()` at `:454, 503, 589, 731`);<br>- enqueues trigger actions without an `Enabled` check (`:298, 349-371`);<br>- adds `Destroyed` notification icons (`:659-684`).<br>If a client receives a replicated event entity carrying `Created`, all of this would run a second time [V] |
| `GenerateEdgesSystem`, `GenerateRoutesSystem` | `:111-112` | D | KEEP | definitions |
| `TrafficRoutesSystem` | `:113` | C | KEEP, exclude from replication | creates and deletes local traffic-route visualisation route and segment entities (`TrafficRoutesSystem.cs:649-727`) [V]. They look like persistent route entities, so the change scanner must filter them [I] |
| `AudioGroupingSystem`, `WeatherAudioSystem` | `:114-115` | C | KEEP | audio entities (`AudioGroupingSystem.cs:291-293`) [V] |
| `HouseholdAndCitizenRemoveSystem` | `:116` | M | KEEP | cascades `Deleted` from deleted households and citizens to seekers, current transports and pets, and emits `RentersUpdated` (`HouseholdAndCitizenRemoveSystem.cs:132-190, 239-240`). Idempotent with host cascades [V/I] |
| `GroundHeightSystem` | `:117` | M | KEEP | rewrites `Transform`/`Curve`/`Node`/area node heights from **local** terrain and water when the change is ≥ 0.01 m, and tags `Updated` (`GroundHeightSystem.cs:609-613, 671-680, 752-756, 782-790`). Converges once terrain is synced (07 §2.2). The anti-entropy hash must tolerate sub-centimetre height noise [V/I] |
| `Game.Buildings.InitializeSystem` | `:118` | D (dup hazard) | KEEP | adds *default* `CrimeProducer`/`MailProducer` on `Created` (`InitializeSystem.cs:143-153`). Re-adds missing archetype consumers (`:173-208`). Strips consumers when `Destroyed` (`:169-172`). **Creates a gate bypass route + 2 segments for `Created` gate buildings** (`:154-168`). Those routes must be treated as locally regenerated children and never replicated [V code / I policy] |
| `OutsideConnectionInitializeSystem` | `:119` | M | OFF+feed | random `RandomLocalizationIndex` for created outside connections (`OutsideConnectionInitializeSystem.cs:95-128`; `RandomSeed.Next()` at `:218`) overwrites host names [V] |
| **`DamageSystem`** | `:120` | **G** | OFF | consumes `Event+Damage`; writes `Damaged`, removes `MaintenanceConsumer` (`DamageSystem.cs:46-86, 121`) [V] |
| **`DestroySystem`** | `:121` | **G** | OFF | consumes `Event+Destroy`; adds `Destroyed`, creates rubble areas with a random prefab, strips consumers (`DestroySystem.cs:104, 145-209, 350, 387`) [V] |

### 2.5 Modification2B

All rows are D and KEEP (02 §6). They regenerate references, geometry, aggregates, sub-objects and flow edges from replicated source data:

- `FindOwnersSystem2` `:122`
- `Game.Routes.ReferencesSystem` `:123`
- `Game.Areas.GeometrySystem` `:124`
- `Game.Net.ReferencesSystem` `:125`
- `ServiceUpgradeReferencesSystem` `:126`
- `SubAreaReferencesSystem` `:127`
- `NodeReductionSystem` `:128`
- `NodeAlignSystem` `:129`
- `AggregateSystem` `:130`
- `Game.Objects.SubObjectSystem` `:131`. It falls back to `RandomSeed.Next()` when the owner lacks `PseudoRandomSeed` (02 §8), so `PseudoRandomSeed` must be replicated.
- `ElementSystem` `:132`
- `ElectricityEdgeGraphSystem` `:133`
- `WaterPipeEdgeGraphSystem` `:134`

### 2.6 Modification3

| System | Reg. | Class | Client | Evidence |
|---|---|---|---|---|
| **`DefaultPoliciesSystem`** | `:135` | **G** (append) | **OFF+feed** | for every `Created` entity with `Policy` (`DefaultPoliciesSystem.cs:106`) it **appends** default policies unconditionally (`:35-62`, adds at `:54, 58`). A host `Policy` payload written earlier would be duplicated [V] |
| `SubObjectReferencesSystem`, `FindOwnersSystem`, `AttachSystem`, `CompositionSelectSystem` | `:136-139` | D | KEEP | |
| `TutorialDeactivationSystem` | `:140` | C (per player) | KEEP or OFF | tutorials |
| `ElectricityOutsideConnectionGraphSystem`, `WaterPipeOutsideConnectionGraphSystem` | `:141-142` | D | KEEP | |

### 2.7 Modification4 and Modification4B

| System | Reg. | Class | Client | Evidence |
|---|---|---|---|---|
| `DistrictModifierInitializeSystem`, `BuildingModifierInitializeSystem`, `RouteModifierInitializeSystem` | `:143-145` | D | KEEP | modifiers derived from `Policy` |
| `ModifiedSystem` (Policies) | `:146` | M | KEEP (input-starved) | consumes `Event+Modify` (`ModifiedSystem.cs:396, 400`). Sets `Policy` flags or appends when active and missing (idempotent, `:113-192`). Refreshes option and modifier fields (`:250-298`). Trigger buffer is guarded by `TriggerSystem.Enabled` (`:412, 441-448`). Sends `Telemetry.Policy` (`:451-453`). Clients must never create `Modify` (`PoliciesUISystem.cs:239` → INTENT) [V] |
| `SubNetReferencesSystem`, `NetCompositionSystem`, `NetCompositionMeshRefSystem`, `Game.Net.GeometrySystem`, `NetComponentsSystem`, `AttachPositionSystem`, `AlignSystem`, `OutsideConnectionSystem`, `LaneSystem`, `BlockSystem` | `:147-156` | D | KEEP | 02 §6. `LaneSystem` needs `PseudoRandomSeed`. `BlockSystem` creates blocks with empty `Cell` buffers (02 §6) |
| **`HouseholdInitializeSystem`** | `:157` | **G** | **OFF** | §2.11 |
| **`Game.Routes.InitializeSystem`** | `:158` | **G** (overwrite) | **OFF+feed** | on `Created` routes: `RouteNumber.m_Number = FindFreeRouteNumber` (`Routes/InitializeSystem.cs:41-50`); `VehicleModel` cleared and re-selected with `RandomSeed` (`:134-155`, seed at `:246`); query `:219` [V] |
| `ComponentsSystem` (Vehicles) | `:159` | D | KEEP | |
| `HouseholdPetRemoveSystem` | `:160` | M | KEEP | cascading pet removal (convergent) [I] |
| **`ImpactSystem`, `IgniteSystem`, `FaceWeatherSystem`, `AddHealthProblemSystem`, `SpectateSystem`, `EndangerSystem`, `AddAccidentSiteSystem`, `AddCriminalSystem`, `SubmergeSystem`** | `:161-169` | **G** (command consumers) | OFF | each drains `Event+<Command>` (queries: `ImpactSystem.cs:472`, `IgniteSystem.cs:187`, `FaceWeatherSystem.cs:163`, `SpectateSystem.cs:113`, `EndangerSystem.cs:155`, `AddAccidentSiteSystem.cs:165`, `AddCriminalSystem.cs:155`, `SubmergeSystem.cs:163`). They add `OutOfControl`/`OnFire`/`FacingWeather`/`InDanger`/`AccidentSite`/`Criminal`/`Flooded` and create journal-data events. `AddHealthProblemSystem` also writes `HealthProblem`, icons, **unguarded** trigger actions and statistics (`AddHealthProblemSystem.cs:203-245, 275, 453`) [V] |
| `ServiceUpgradeSystem` | `:170` | D | KEEP | no command-buffer writes found in grep [V partial] |
| **`TripResetSystem`** | `:171` | **G** (command) | OFF | drains `ResetTrip` (`TripResetSystem.cs:321`). Rewrites lanes, paths, targets, `CurrentBuilding`, `Arrived`, `TripNeeded` (`:72-243`) [V] |
| **`EventJournalInitializeSystem`** | `:172` | **G** | OFF+feed | creates `EventJournalEntry` entities for `Created` journal events (`EventJournalInitializeSystem.cs:46-88, 167-168`). Replicated events would create duplicate journals [V] |
| `TutorialActivationSystem` | `:173` | C (per player) | KEEP or OFF | |
| **`ObjectEmergeSystem`** (4B) | `:174` | **G** | OFF | on deleted buildings or passenger vehicles (`ObjectEmergeSystem.cs:527-529`), turns occupants into spawned creatures (`:228-267`) [V] |
| `LaneReferencesSystem`, `Game.Buildings.ReferencesSystem`, `LaneOverlapSystem` | `:175-178` | D | KEEP | |
| `BuildingPoliciesSystem` | `:177` | M | KEEP (input-starved) | on `Modify` for buildings: turned-off icon (`BuildingPoliciesSystem.cs:85-89`) [V] |
| `AreaConnectionSystem` | `:179` | M | KEEP, replicate area `PseudoRandomSeed` | assigns a random `PseudoRandomSeed` to areas lacking one (`AreaConnectionSystem.cs:219-231`) [V] |
| `RoadConnectionSystem` | `:180` | M | KEEP | recomputes building road connection, road-connection icons and `RoadConnectionUpdated` events (`RoadConnectionSystem.cs:615-629`). Derived from nets [V] |
| `ParkedVehiclesSystem` | `:181` | M | KEEP with `Applied` on replicated creations | query: buildings with `Created` or `Temp` and **without `Applied`** (`ParkedVehiclesSystem.cs:1683-1700`). It *creates* parked vehicles (`:467-477`) [V]. A replicated building creation tagged `Applied` is skipped [V query / I effect] |
| `TrafficLightInitializationSystem`, `SecondaryObjectSystem`, `SecondaryLaneSystem` | `:182-184` | D | KEEP | need `PseudoRandomSeed` (02 §8) |
| `ElectricityBuildingGraphSystem`, `WaterPipeBuildingGraphSystem` | `:185-186` | D | KEEP | flow graph |
| `BuildingStateEfficiencySystem` | `:187` | M | KEEP | rewrites its own `Efficiency` factor on `Updated` buildings (query `BuildingStateEfficiencySystem.cs:82`). Host replication of `Efficiency` must not fight it [I] |

### 2.8 Modification5

| System | Reg. | Class | Client | Evidence |
|---|---|---|---|---|
| `RemovedSystem` | `:188` | M | KEEP | for deleted buildings, workplaces and companies:<br>- removes `PropertyRenter` and deletes lodging renters (`RemovedSystem.cs:49-71`);<br>- removes `Worker`/`TravelPurpose` (`:113-122`);<br>- removes icons (`:151-155, 223-228`).<br>Trigger buffer guarded (`:362, 373`). Convergent with host deltas [V/I] |
| `Game.Creatures.InitializeSystem` | `:189` | M | KEEP | §2.11. Runs on **every `Updated` creature** (`:661`) |
| `NetObjectInitializeSystem` | `:190` | D | KEEP | |
| `UpdateCollectSystem` ×4, `ZoneBuiltRequirementSystem`… | `:191-195` | D / **G** | KEEP / **OFF** | `ZoneBuiltRequirementSystem` creates `Unlock` events (`ZoneBuiltRequirementSystem.cs:167-172`) [V] |
| `SegmentCurveSystem`, `Search*` ×5, `LocalEffectSystem`, `Creatures.ReferencesSystem` | `:196-203` | D | KEEP | |
| `GroupSystem` | `:204` | M | KEEP [I] | creature grouping (`GroupSystem.cs:499-548`) |
| `BlockReferencesSystem`, `SecondaryObjectReferencesSystem`, `SecondaryLaneReferencesSystem`, `SurfaceUpdateSystem`, `WaypointConnectionSystem`, `SpawnLocationConnectionSystem`, `LaneConnectionSystem` | `:205-211` | D | KEEP | |
| `Game.Vehicles.InitializeSystem` | `:212` | M | KEEP | §2.11. Runs on **every `Updated` vehicle** (`:1330`) |
| `Game.Vehicles.ReferencesSystem` | `:213` | D | KEEP | |
| `UpdateGroupSystem` | `:214` | M | KEEP | §2.11 |
| `CellCheckSystem`, `LotHeightSystem`, `Objects.OverrideSystem`, `LaneBlockSystem` | `:215-218` | D | KEEP | |
| `FixParkingLocationSystem` | `:219` | M | KEEP | when parking lanes or spawn locations are deleted (`FixParkingLocationSystem.cs:1781-1787`), relocates parked vehicles with `RandomSeed` (`:954, 1029-1047`). Host relocation is replicated afterwards [V/I] |
| `Net.OverrideSystem`, `FixLaneObjectsSystem`, `CurrentDistrictSystem`, `ServiceDistrictSystem` | `:220-223` | D | KEEP | |
| `Game.Net.InitializeSystem` | `:224` | M | KEEP | on `Created` edges, resets `ServiceCoverage` and `ResourceAvailability` buffers and copies water-pipe capacities from the prefab (`Net/InitializeSystem.cs:44-63, 106`) [V]. Host coverage values arrive in a later delta [I] |
| `EdgeMappingSystem` | `:225` | D | KEEP | |
| `CargoTransportStationInitializeSystem` | `:226` | D | KEEP | adds `PropertyRenter{self}` (idempotent, `CargoTransportStationInitializeSystem.cs:34-39`) [V] |
| **`ResourcesInitializeSystem`** | `:227` | **G** (additive) | **OFF+feed** | on `Created`, **adds** initial resources (`ResourcesInitializeSystem.cs:64-74, 134-142`; query Created\|Updated `:222-237`) [V] |
| `BatteryInitializeSystem` | `:228` | M | KEEP | battery-empty icon (`BatteryInitializeSystem.cs:75-79`) [V] |
| **`ParkInitializeSystem`** | `:229` | **G** (overwrite) | **OFF+feed** | `park.m_Maintenance = pool` on `Created` (`ParkInitializeSystem.cs:54-59, 116`) [V] |
| **`CitizenInitializeSystem`** | `:230` | **G** | **OFF** | §2.11 |
| **`HouseholdPetInitializeSystem`** | `:231` | **G** | **OFF** | §2.11 |
| **`MeetingInitializeSystem`** | `:232` | **G** | **OFF** | adds `AttendingMeeting` to attendees and edits the attendee buffer (`MeetingInitializeSystem.cs:36-48, 90`) [V] |
| **`Game.Citizens.CompanyInitializeSystem`** | `:233` | **G** | **OFF** | §2.11 plus the rent-queue leak (§4) |
| **`StorageInitializeSystem`** | `:234` | **G** | **OFF** | frame-seeded brand selection for `Created` storage companies (`StorageInitializeSystem.cs:51-58, 108`) [V] |
| `CityServiceWorkplaceInitializeSystem` | `:235` | D | KEEP | `WorkProvider.m_MaxWorkers` derived from prefab and upgrades (`CityServiceWorkplaceInitializeSystem.cs:107-116`) [V] |
| `LaneHiddenSystem`, `SubObjectHiddenSystem`, `LanePoliciesSystem`, `LaneDataUnknownEscalateSystem`, `IconDeletedSystem`, `IconAnimationSystem` | `:236-241` | D/C | KEEP | |
| `InitializeSchoolSystem` | `:242` | M | KEEP | drops students when a school loses levels (`InitializeSchoolSystem.cs:42-52`). Convergent [V/I] |
| `CostSystem` | `:243` | D | KEEP | |
| **`EventJournalSystem`** | `:244` | **G** | OFF+feed | updates journal entries from `AddEventJournalData` (`EventJournalSystem.cs:59-101, 146`) [V] |
| `TutorialTriggerSystem` | `:245` | C (per player) | KEEP or OFF | |
| `ElectricityRoadConnectionGraphSystem`, `WaterPipeRoadConnectionGraphSystem` | `:246-247` | D | KEEP | drain the road-edge queues filled by Buildings Initialize/Destroy |

### 2.9 ModificationEnd

| System | Reg. | Class | Client | Evidence |
|---|---|---|---|---|
| `InstanceCountSystem`, `LaneDataSystem`, `RouteDataSystem`, `ParkingLaneDataSystem`, `LanesModifiedSystem`, `RoutesModifiedSystem` | `:248-253` | D | KEEP | pathfind graph |
| `RoutePathSystem` | `:254` | D | KEEP | local route segment paths. Enqueue with `uint.MaxValue` (`RoutePathSystem.cs:634`) [V] |
| `BoardingVehicleSystem` | `:255` | D | KEEP | |
| `MarkerCreateSystem` | `:256` | C | KEEP | `Marker`-layer infoview icons (`MarkerCreateSystem.cs:415-489`) [V] |
| `PathOwnerTargetMovedSystem` | `:257` | M (inert) | KEEP | randomly nulls path elements of path owners that target a moved entity (`PathOwnerTargetMovedSystem.cs:38-55`). Inert on clients because no movement or pathfinding runs there [V/I] |
| **`ZoneCheckSystem`** | `:258` | **G** | **OFF+feed** | adds or removes `Condemned` plus the condemned icon (`ZoneCheckSystem.cs:303-312`), triggered whenever zones update (`:473-475`) [V] |
| `AreaResourceSystem` | `:259` | D | KEEP | recomputes map-feature resources (`AreaResourceSystem.cs:844-866`) [V] |
| `SurfaceExpandSystem` | `:260` | D | KEEP | |
| `ConnectionWarningSystem` | `:261` | M | KEEP | dead-end, track and road connection warnings from lanes (`ConnectionWarningSystem.cs:695-812, 2214-2256`). Deduplicated with host icons (§4) [V] |
| `CoveragePreviewSystem`, `HeatmapPreviewSystem` | `:262-263` | C | KEEP | tool previews |
| `ValidationSystem` (+`.Components`) | `:264-265` | C | KEEP | validates local `Temp+Updated` only (02 §10.2) |
| `AnimationUpdateSystem`, `AnimationSystem` | `:266-267` | C | KEEP | |
| `QuantityUpdateSystem` | `:268` | C/D | KEEP | |
| `UnspawnedSystem` | `:269` | M | KEEP | toggles `Unspawned` on `Updated` vehicles (`UnspawnedSystem.cs:130-138, 207-219`) [V] |
| **`ServiceRequestSystem`** | `:270` | **G** | **OFF** | random `UpdateFrame` for new `RequestGroup` entities (`ServiceRequestSystem.cs:36-43`); dispatch handling destroys requests and writes `Dispatched` (`:109-137`) [V] |
| `IconCommandSystem` | `:271` | M (infra) | **KEEP (mandatory)** | §4 |
| **`XPBuiltSystem`** | `:272` | **G** | OFF | XP for `Created` objects (`XPBuiltSystem.cs:55-79, 184`); adds `PlacedSignatureBuildingData` to the **prefab** (`:66`); grid-built XP flag (`:111-118`) [V] |
| **`NetXPSystem`** | `:273` | **G** | OFF | XP for created nets (`NetXPSystem.cs:128, 268-271`) [V] |
| **`XPSystem`** | `:274` | **G** | OFF | `XP.m_XP += amount` (`XPSystem.cs:32-37`) [V] |
| **`MilestoneSystem`** | `:275` | **G** | OFF | `MilestoneLevel++`, `MilestoneReachedEvent` + `Unlock`, `PlayerMoney.Add(reward)`, `Creditworthiness += loanLimit` (`MilestoneSystem.cs:71-118`). Gated on the local tutorial state (`:73-76`) [V] |
| `WaterSourceInitializeSystem` | `:276` | M | KEEP | overwrites `WaterSourceData` height, radius, pollution, modifier and id on `Created+PrefabRef` water sources (`WaterSourceInitializeSystem.cs:38-46, 87`) [V]. Tool water sources have no `PrefabRef` (`GenerateWaterSourcesSystem.cs:131`), so this affects only prefab-placed sources [V] |
| `ResetOverriddenSystem` | `:277` | D | KEEP | |
| **`TriggerSystem`** | `:278` | **G** | OFF (with all unguarded producers, §4) | `RandomSeed.Next()` (`TriggerSystem.cs:458`). Uses the **camera position** (`:483-487`). Feeds chirp, life-path, radio and tutorial queues (`:461-466`). Re-enables itself in `OnGamePreload` (`:387-391`) [V] |
| **`LifePathEventSystem`** | `:279` | **G** | OFF+feed | creates `LifePathEvent` entities and chirp data (`LifePathEventSystem.cs:62-80`) [V] |
| **`CreateChirpSystem`** | `:280` | **G** | OFF+feed | creates `Chirp` entities with `RandomSeed` (`CreateChirpSystem.cs:141, 175-185`) [V] |
| `RadioTagSystem` | `:281` | C | KEEP or OFF | queue getters assert `Enabled` (`RadioTagSystem.cs:137-151`). Only `TriggerSystem` fills them [V] |
| `SchoolUpdatedSystem` | `:282` | M | KEEP | removes `Student` from citizens of deleted or downgraded schools (`SchoolUpdatedSystem.cs:47-91, 139-140`). Convergent [V/I] |
| `WaterPoweredInitializeSystem` | `:283` | D | KEEP | recomputes estimates on `Temp`/`Created` (`WaterPoweredInitializeSystem.cs:64-76`) [V] |
| `TutorialSystem` | `:284` | M (per player) | see risk R9 | per-player tutorials can emit **`Unlock` events** (`TutorialSystem.cs:300-315, 970-1000`) [V] |
| `EditorTutorialSystem` | `:285` | – | out of scope | |
| `EventAchievementTriggerSystem` | `:286` | C | KEEP | platform achievements |
| **`StrictObjectBuiltRequirementSystem`, `ObjectBuiltRequirementSystem`, `PrefabUnlockedRequirementSystem`** | `:287-289` | **G** | OFF | write prefab `UnlockRequirementData` progress and create `Unlock` events (`StrictObjectBuiltRequirementSystem.cs:50-54`, `ObjectBuiltRequirementSystem.cs:66`, `PrefabUnlockedRequirementSystem.cs:54`) [V] |
| `ElectricityGraphReferencesSystem`, `WaterPipeGraphReferencesSystem` | `:290-291` | D | KEEP | |
| `CityServiceEfficiencySystem` | `:292` | M | KEEP | budget-driven `Efficiency` factor (`CityServiceEfficiencySystem.cs:162-182`) [V] |
| `LocaleMeshStateSystem`, `MeshGroupSystem`, `RequiredBatchesSystem`, `NetCompositionMeshSystem`, `Game.Effects.SearchSystem` | `:293-297` | C/D | KEEP | |
| **`DevTreeSystem`** | `:298` | **G** | OFF | +points per `MilestoneReachedEvent` (`DevTreeSystem.cs:129-145`). `Purchase` deducts points and emits `Unlock` (`:148-170`), called from `DevTreeUISystem.cs:165` → INTENT [V] |
| `ApplyPrefabsSystem` | `:299` | – | editor only | |
| `IconClusterSystem` | `:300` | C | KEEP | |
| `GameModeGovernmentSubsidiesSystem` | `:301` | D | KEEP | computes `m_MonthlySubsidy` from replicated money and budget (`GameModeGovernmentSubsidiesSystem.cs:71-89`) [V] |
| `CityServiceBudgetSystem` | `:302` | D | KEEP *if* inputs are replicated, else OFF + custom sync | writes only its own arrays and `Collected*` aggregates (`CityServiceBudgetSystem.cs:124-250, 299-426`). **Not `ISerializable`** (`:26`). Re-enables itself in game (`:827`) [V] |
| `UniqueAssetTrackingSystem` | `:303` | D | KEEP | tracks placed unique objects (`UniqueAssetTrackingSystem.cs:32-34`) [V] |
| `TerrainSystem` | `:304` | D | KEEP | cascades (07) |
| **`RandomLocalizationInitializeSystem`** | `:305` | **G** (overwrite) | **OFF+feed** | regenerates `RandomLocalizationIndex` with `RandomSeed` for every `Created` entity carrying it (`RandomLocalizationInitializeSystem.cs:46-51, 109`) [V] |
| **`NotificationTriggerSystem`** | `:306` | **G** | OFF (with `TriggerSystem`) | trigger actions for created and deleted icons, using an **unguarded** `CreateActionBuffer` (`NotificationTriggerSystem.cs:64, 154`) [V] |

### 2.10 UIUpdate systems that write ECS or call gameplay mutators

All writers found by grep of `EntityManager.Add/Set/Remove/Create/Destroy`, `CreateCommandBuffer()` and mutator calls in `Game/Game.UI*` [V]. Writes made through `EndFrameBarrier` become visible at the start of the next MainLoop, i.e. *before* `NetApplySystem` [V `SystemOrder.cs:49`].

| UI system | Write site | Class | Client |
|---|---|---|---|
| `ActionsSection` (selected info) | `AddComponent<Deleted>(selectedEntity)` via `EndFrameBarrier` (`ActionsSection.cs:256`) | G | INTENT |
| `UpgradesSection` | delete upgrade (`UpgradesSection.cs:96`) | G | INTENT |
| `ColorSection`, `VisualCustomizeSection` | colours and mesh customisation (`ColorSection.cs:42`; `VisualCustomizeSection.cs:80, 103, 199, 259, 265`) | G (persistent visual) | INTENT |
| `TransportationOverviewUISystem` | delete line, colour, active and vehicle settings (`TransportationOverviewUISystem.cs:199, 217, 276, 290, 304, 318`) | G | INTENT |
| `PoliciesUISystem` | creates `Modify` events (`PoliciesUISystem.cs:239`) | G | INTENT |
| `TaxationUISystem` | `TaxSystem.SetTaxRate` (`TaxationUISystem.cs:287`) | G | INTENT |
| `ServiceBudgetUISystem` | `SetServiceBudget`, `ServiceFeeSystem.SetFee` (`ServiceBudgetUISystem.cs:113, 251-262`) | G | INTENT |
| `LoanUISystem` | `LoanSystem.ChangeLoan` (`LoanUISystem.cs:76`) | G | INTENT |
| `DevTreeUISystem` | `DevTreeSystem.Purchase` (`DevTreeUISystem.cs:165`) | G | INTENT |
| `MapTilesUISystem` | `MapTilePurchaseSystem.PurchaseSelection` (`MapTilesUISystem.cs:190`) | G | INTENT |
| `NameSystem` | rename via ECB (`NameSystem.cs:228, 237`) | G (persistent) | INTENT |
| `TimeUISystem` | `selectedSpeed` (05 §9.2) | clock | INTENT |
| `ChirperUISystem` | chirp `Liked` flag + `Updated` (`ChirperUISystem.cs:140-155`) | C or G (product decision) | local or INTENT |
| `ChirpLinkSystem` | `ChirpLink` buffers (`ChirpLinkSystem.cs:270-301`) | D | KEEP |
| `InfoviewsUISystem` | `NotificationIconDisplayData` enable bits on prefabs (`InfoviewsUISystem.cs:195-199`) | C | KEEP |
| `UIHighlightSystem`, `ToolbarUISystem`, `UpgradeMenuUISystem` | `UIHighlight` add/remove (`UIHighlightSystem.cs:125-136`; `ToolbarUISystem.cs:947-978`; `UpgradeMenuUISystem.cs:387`) | C | KEEP |
| `PrefabUISystem` | helper requirement entities (`PrefabUISystem.cs:1309-1311`) | C | KEEP |

### 2.11 Initializer overwrite table (task 2)

"Unconditional" means the field is written for every entity the query matches. "Trigger" is the query condition. The last column decides whether a host payload written at the apply point survives.

| Initializer (phase) | Trigger (query) | Fields overwritten or added | Conditional? | Verdict |
|---|---|---|---|---|
| `CitizenInitializeSystem` (Mod5) | `Citizen+HouseholdMember+Created`, not `Temp` (`CitizenInitializeSystem.cs:292`) | enable bits `Arrived/MailSender/CrimeVictim/CarKeeper/HasJobSeeker := false` (`:83-87`); `Citizen.m_PseudoRandom`, `m_Health`, `m_WellBeing`, `m_LeisureCounter` (`:92-102`); `Male` flag OR (`:103-106`); **`PrefabRef` added** (`:108-111`); **appends to the household's `HouseholdCitizen` buffer** (`:112-116`); age bits, education, `BicycleOwner` (`:121-209`); **`m_BirthDay` re-derived from sentinel values 0..3** (`:119-183, 210`); trigger actions (`:143-147`) | unconditional except the age branch chosen by the incoming `m_BirthDay` | **Must not run on host payloads.** A real birthday is misread as a sentinel and the household buffer is duplicated. **OFF**; ship full post-init state |
| `HouseholdInitializeSystem` (Mod4) | `Household+PrefabRef+HouseholdCitizen+CurrentBuilding+Resources`, not `Temp` — **no `Created`**; `CurrentBuilding` on the household is the marker (`HouseholdInitializeSystem.cs:344`) | `Money +=` random initial wealth (`:163`); **spawns citizens** (`:167-180`), pets (`:183-190`) and a car (`:220`); `Household.m_Resources = random` (`:229`); tourist statistics (`:240`); `PropertySeeker`/`LodgingSeeker` (`:239, 248`); removes `CurrentBuilding` (`:250`) | runs only while the marker is present | Host post-init payloads lack the marker, so the system would not match [V]. It still creates agents → **OFF** |
| `Game.Citizens.CompanyInitializeSystem` (Mod5) | `CompanyData+Profitability+PrefabRef+Created` (`CompanyInitializeSystem.cs:244`) | `CompanyData{random seed, brand}` (`:88-99`); `Profitability = 127` (`:96-99`); `ServiceAvailable` (`:100-107`); **starting resources added** (`:109-131`); **`RentAction` enqueued** (`:134-141`, queue `:272`); `LodgingProvider` | unconditional | **OFF** (overwrite plus queue leak) |
| `StorageInitializeSystem` (Mod5) | `StorageCompany+Created` (`:108`) | `StorageCompany.m_RandomSeed/m_Brand` (`:51-58`) | unconditional | **OFF** |
| `HouseholdPetInitializeSystem` (Mod5) | `HouseholdPet+Created` (`:123`) | **`AddBuffer<HouseholdAnimal>` on the household, replacing the buffer** with only the new pets (`:73-80`) | unconditional | **OFF** (would drop replicated pets) |
| `MeetingInitializeSystem` (Mod5) | `CoordinatedMeeting+Created` (`:90`) | `AttendingMeeting` on attendees; edits the attendee buffer (`:36-48`) | conditional on attendee state | **OFF** |
| `Game.Creatures.InitializeSystem` (Mod5) | **`Creature+Updated`**, not only `Created` (`Creatures/InitializeSystem.cs:661`) | `Transform` for `Unspawned` creatures with `TripSource` (`:151-199`); `HumanNavigation` reset (`:201-213`, unconditional); `Human` `Cold`/`Homeless` flags recomputed (`:216-235`); **`HumanCurrentLane`/`AnimalCurrentLane.m_LanePosition` re-randomised** (`:236-304`); `AnimalNavigation` | lane position and navigation unconditional; transform only with `Unspawned+TripSource` | KEEP. These fields are client-local sim state. **Never tag moving creatures `Updated` for position streams** (§6.3) |
| `Game.Vehicles.InitializeSystem` (Mod5) | **`Vehicle+Updated`** (`Vehicles/InitializeSystem.cs:1330`) | `CarNavigation`, `CarCurrentLane.m_LanePosition` random (`:341-350`); transform and trailers for `Unspawned+TripSource` or reversing cars (`:274-340`); **`ParkedCar` lane and curve position re-chosen when `TripSource` is present** (`:457-515`); train bogies (`:518-580`); `TreeFixJob` re-adds non-created cars to `LaneObject` buffers (`:46-83`) | conditional on `TripSource`/`Unspawned` for transforms | KEEP. Exclude `TripSource` from vehicle payloads [I]. No `Updated` for position streams |
| `UpdateGroupSystem` (Mod5) | `UpdateFrame` + (`Created` or `Deleted`) (`UpdateGroupSystem.cs:715-723`); `Updated+InterpolatedTransform` without `Created` (`:724`) | `UpdateFrame` := least-populated group **unless the chunk has `Applied`** (`:408-460`); `TransformFrame` ×4 and `InterpolatedTransform` reset from `Transform` for created moving objects (`:470-485`); **for any `Updated` moving object, all 4 `TransformFrame`s and `InterpolatedTransform` are reset** (`:561-614, 770-785`) | `Applied` keeps the host `UpdateFrame` | KEEP. Create with `Applied` + host `UpdateFrame`. Stream `TransformFrame` in *later* frames without tags |
| `Game.Routes.InitializeSystem` (Mod4) | `Route+RouteNumber+Created` (`:219`) | `RouteNumber.m_Number` (`:41-50`); `VehicleModel` cleared + random re-select (`:134-155`) | unconditional | **OFF+feed** |
| `DefaultPoliciesSystem` (Mod3) | `Created+Policy+PrefabRef` (`:106`) | **appends** defaults (`:54, 58`) | unconditional | **OFF+feed** |
| `RandomLocalizationInitializeSystem` (ModEnd) | `Created+RandomLocalizationIndex+PrefabRef`, not outside connections (`:109`) | `RandomLocalizationIndex` regenerated (`:46-51`) | unconditional where counts exist | **OFF+feed** |
| `OutsideConnectionInitializeSystem` (Mod2) | `Created+OutsideConnection+RandomLocalizationIndex` (`:192`) | same buffer, random or nearest (`:95-128`) | unconditional | **OFF+feed** |
| `ResourcesInitializeSystem` (Mod5) | `Resources+PrefabRef` + (`Created`\|`Updated`) (`:222-237`) | **adds** `InitialResourceData` (`:64-74, 134-142`); `ResourceConsumer.m_ResourceAvailability` | addition only on `Created` | **OFF+feed** |
| `ParkInitializeSystem` (Mod5) | `Park+PrefabRef+Created` (`:116`) | `Park.m_Maintenance` (`:54-59`) | unconditional | **OFF+feed** |
| `Game.Buildings.InitializeSystem` (Mod2) | `Building+PrefabRef+Updated` (`:325`) | adds default producers and consumers; strips them when `Destroyed`; **creates gate bypass routes on `Created`** (`:143-208`) | additive or removal only | KEEP. Bypass routes are local children |
| `Game.Net.InitializeSystem` (Mod5) | created non-Temp edges (`:106`) | `ServiceCoverage`/`ResourceAvailability` := default; water-pipe capacities from the prefab (`:44-63`) | unconditional | KEEP. Send coverage in the next delta [I] |
| `WaterSourceInitializeSystem` (ModEnd) | `WaterSourceData+PrefabRef+Created` (`:87`) | `m_Height`, `m_Radius`, `m_Polluted`, `m_Modifier=1`, `m_Id=-1` (`:38-46`) | unconditional | KEEP. For prefab water sources, send `WaterSourceData` in the next delta, or create without `Created` [I] |
| `CargoTransportStationInitializeSystem` (Mod5) | `CargoTransportStation+Resources+Created` (`:75`) | adds `PropertyRenter{self}` (`:34-39`) | idempotent | KEEP |
| `CityServiceWorkplaceInitializeSystem`, `WaterPoweredInitializeSystem`, `NetObjectInitializeSystem`, modifier initializers | various | prefab- or graph-derived values | derived | KEEP |
| `EventJournalInitializeSystem` (Mod4) | `Event+Created+PrefabRef+JournalEvent` (`:167`) | creates journal entities (`:46-88`) | unconditional | **OFF+feed** |

**Answer to task 2 [V facts / I policy].** Only three classes of initializer can stay enabled under host authority:

1. Additive or idempotent structural ones (Buildings/Cargo).
2. Ones whose overwritten fields are client-local sim state that clients never simulate (Vehicles/Creatures navigation, `UpdateGroupSystem` frames).
3. Prefab-derived ones (Net, WaterSource, Workplace).

Every initializer that writes host-authoritative values from randomness or appends to buffers must be **OFF** on clients. The host then ships the *post-initialization* component set. This removes the need for the "re-apply after Mod4/5" pass that 03 §9.6 step 4 proposed.

---

## 3. Verification of 03's cosmetic whitelist (task 3)

All of these are registered in GameSimulation unless noted (`SystemOrder.cs:359-363, 438, 454, 471-472, 521`; `PlanetarySystem` and `LightingSystem` in PreCulling at `:640, 645`) [V].

| System | Creates entities or events? | Feeds gameplay? | Verdict |
|---|---|---|---|
| `TimeSystem` | no. `OnUpdate` only recomputes cached time, date and year (`TimeSystem.cs:198-211`). Writes `TimeData` only in `PostDeserialize` (`:69-87`) and debug (`:160`) [V] | everything reads it; pure function of `frameIndex` + `TimeData` | **C/D, KEEP** |
| `ClimateSystem` | trigger actions (`ClimateSystem.cs:486-518`), **guarded by `m_TriggerSystem.Enabled`** (`:479`). Weather prefab selection uses priority sorting, no `RandomSeed` in the lines read (`:711-760`) [V partial] | climate values are read by GameSimulation systems (`FireHazardSystem`, `WeatherHazardSystem`, `TouristSpawnSystem`, `SoilWaterSystem`, `PowerPlantAISystem`… grep) [V], all OFF on clients | **C, KEEP**. Host owns `OverridableProperty` overrides (`:631-645`) |
| `PlanetarySystem` (PreCulling) | no | sun light → `PowerPlantAISystem.cs:491` (solar), OFF on clients [V] | **C, KEEP** |
| `LightingSystem` (PreCulling) | no | `dayLightBrightness` → `RoadSafetySystem.cs:296`, OFF on clients [V] | **C, KEEP** |
| `StreetLightSystem` | only `BatchesUpdated`/`EffectsUpdated` tags; brightness derived from `PseudoRandomSeed` (`StreetLightSystem.cs:79-151, 204-236`; interval 16 at `:317`) [V] | no | **C, KEEP**. Exclude street-light state from replication [I] |
| `WindSystem` / `WindSimulationSystem` | no ECS entities | wind map → `AirPollutionSystem.cs:40-41, 127`, `PowerPlantAISystem`, `WeatherPhenomenonSystem` (grep), all OFF on clients [V] | **C, KEEP** |
| `WetnessSystem` | **yes**: `SubObjectsUpdated` events when the snow threshold flips (`WetnessSystem.cs:89-95`), which make `SubObjectSystem` (Mod2B) add or remove snow-dependent sub-objects. Random per chunk (`:64, 201`) [V] | `Surface` only | **C, KEEP**. Never replicate `Surface`; treat snow-dependent sub-objects as local children [I] |
| `DirtynessSystem` | no (`Surface`, random `:103, 204`) [V] | no | **C, KEEP** |
| `SnowSystem` | no (GPU, `SnowSystem.cs:323-328`; interval 4 at `:228`) [V] | no | **C, KEEP** |
| `ZoneAmbienceSystem` | no | map read by `TerrainAttractivenessSystem.cs:36, 178` (host) and `AudioGroupingSystem` (client audio) [V] | **C, but starved**: each update copies the accumulator into `m_Value` and clears it (`ZoneAmbienceSystem.cs:20-24`). The accumulator is filled by `SpawnableAmbienceSystem` (GameSimulation, `SpawnableAmbienceSystem.cs:309-311`) [V]. **KEEP `SpawnableAmbienceSystem` on clients** (it only writes the map) or ship the `CellMapSystem` map [I] |
| `TrafficAmbienceSystem` | no | fed by `CarNavigationSystem` (`CarNavigationSystem.cs:118-136, 464`), OFF on clients [V] | **C, starved**. Ship the map, or accept silent traffic ambience [I] |

**Conclusion [V/I]:** none of the whitelisted systems feeds gameplay on the client, because every gameplay consumer is a GameSimulation system that is OFF there. The only structural side effect is `WetnessSystem` → `SubObjectsUpdated`. Two ambience maps need a producer or a sync.

---

## 4. NativeQueue producer/consumer pairs across the gating boundary (task 4)

Rules derived from code:

- **R-a:** a consumer that is disabled while a producer still runs leaks (persistent queue) or accumulates `TempJob` queues (list-of-queues pattern).
- **R-b:** a producer that is disabled while its consumer runs is harmless (it drains an empty queue).
- `Unity.Assertions.Assert.IsTrue` is compiled with `UNITY_ASSERTIONS` defined and forwards to `UnityEngine.Assertions.Assert` (`Unity.Entities/Unity.Assertions/Assert.cs:1, 21-27`) [V]. The engine assert logs rather than throws in a player build [I]. So the "isn't running" asserts below do **not** stop the leak.

| # | Consumer (phase) | Container and guard | Producers outside GameSimulation | Verdict for clients |
|---|---|---|---|---|
| Q1 | `TriggerSystem` (ModEnd) | `List<NativeQueue<TriggerAction>>` (`TempJob`), created per call; assert only (`TriggerSystem.cs:405-411`); disposed in `OnUpdate` (`:419-448`) or `OnStopRunning` (`:395-402`) [V] | **Guarded** (check `Enabled`): `ModifiedSystem.cs:412`, `RemovedSystem.cs:362`, `ApplyNetSystem.cs:932`, `ApplyObjectsSystem.cs:949`, `ClimateSystem.cs:479`. **Unguarded:** `CitizenInitializeSystem.cs:328`, `AddHealthProblemSystem`, `Game.Events.InitializeSystem.cs:298`, `NotificationTriggerSystem.cs:154`. GameSimulation producers include `EarlyGameOutsideConnectionTriggerSystem` and about 25 more (grep list) [V] | TriggerSystem **OFF** ⇒ every unguarded producer **OFF**. That holds for the table above |
| Q2 | `CreateChirpSystem`, `LifePathEventSystem`, `RadioTagSystem`, `TutorialEventActivationSystem` | persistent queues filled only by `TriggerSystem` (`TriggerSystem.cs:461-466`); `RadioTagSystem` asserts (`RadioTagSystem.cs:137-151`) [V] | none (only `TriggerSystem`, plus `LifePathEventSystem` → chirp queue) | harmless when `TriggerSystem` is OFF (R-b) |
| Q3 | `CityStatisticsSystem` (GameSimulation) | persistent `m_StatisticsEventQueue`; `GetStatisticsEventQueue` asserts only (`CityStatisticsSystem.cs:840-845`); **`GetSafeStatisticsQueue` checks `Enabled` per enqueue** (`:64-85, 847-851`) [V] | Safe: `HouseholdInitializeSystem.cs:376`. **Unsafe: `AddHealthProblemSystem.cs:453`** (Mod4). All other unsafe users are GameSimulation (grep list in §0 notes) [V] | OFF together. `AddHealthProblemSystem` must be OFF |
| Q4 | `XPSystem` (ModEnd) | persistent `m_XPQueue`, no guard (`XPSystem.cs:77-81, 95`) [V] | `XPBuiltSystem.cs:195-222`, `NetXPSystem.cs:296-299` (ModEnd); `XPAccumulationSystem` (GameSimulation) [V] | all three **OFF** together |
| Q5 | `XPSystem.m_XPMessages` → `MilestoneUISystem` | drained by `TransferMessages` (`XPSystem.cs:66-75`; `MilestoneUISystem.cs:330`) [V] | – | empty on clients: no XP popups. Forward host XP messages for UI [I] |
| Q6 | `PropertyProcessingSystem` (GameSimulation) | persistent `m_RentActionQueue`, assert only (`PropertyProcessingSystem.cs:744-749`) [V] | **`CompanyInitializeSystem.cs:134-141, 272` (Mod5)**; `Household/Commercial/IndustrialFindPropertySystem` (GameSimulation) [V] | `CompanyInitializeSystem` **must** be OFF with it |
| Q7 | `AddMeetingSystem` (Mod1) | persistent `m_MeetingQueue` (`AddMeetingSystem.cs:167`) [V] | `LeisureSystem.cs:1012`, `TouristFindTargetSystem.cs:308` (GameSimulation) | producers OFF ⇒ consumer harmless. Disable for clarity |
| Q8 | `IconCommandSystem` (ModEnd) | `List<NativeQueue<Command>>` (`TempJob`) per `CreateCommandBuffer`, **no guard** (`IconCommandSystem.cs:698, 727-731`); drained in `OnUpdate` (`:740-770`); adds deduplicated via `FindIcon` (`:157, 563`) [V] | **many enabled client systems**: `ToolFeedbackSystem`, `RoutePathReadySystem`, `ConnectionWarningSystem`, `RoadConnectionSystem`, `MarkerCreateSystem`, `BatteryInitializeSystem`, `BuildingPoliciesSystem`, `IconDeletedSystem`, `RemovedSystem`… [V] | **must stay KEEP.** Host icons go through it as commands (§6.4) |
| Q9 | `PathfindSetupSystem` / `PathfindResultSystem` (MainLoop) | per-caller `SetupQueue` (`PathfindSetupSystem.cs:412-436`) [V] | none on clients. All 45 `GetQueue` callers are GameSimulation files (grep) [V] | harmless (R-b) |
| Q10 | `ElectricityRoadConnectionGraphSystem`, `WaterPipeRoadConnectionGraphSystem` (Mod5) | road-edge queues | `Game.Buildings.InitializeSystem.cs:189, 200`, `DestroySystem.cs:205, 209` | consumer KEEP; `DestroySystem` OFF (R-b) |
| Q11 | `ZoneAmbienceSystem`, `TrafficAmbienceSystem` | map accumulators (not queues), `AddWriter` | `SpawnableAmbienceSystem`, `CarNavigationSystem` (GameSimulation) | §3 |
| Q12 | `AchievementTriggerSystem` (MainLoop) | `m_TransportedResourceQueue` + counters (`AchievementTriggerSystem.cs:362-371`) | transport AIs, `ExtractorCompanySystem`, `DeathCheckSystem` (GameSimulation); `BulldozeToolSystem.cs:1633` (local) [V] | harmless |
| Q13 | `EndFrameBarrier` | ECB | GameSimulation systems and UI | always played back |
| Q14 | `ModifiedSystem` | one-frame `Event+Modify` entities (not a queue) | `PoliciesUISystem.cs:239` | INTENT |

---

## 5. UI readers of disabled systems and runtime `Deserialize` (task 5)

### 5.1 Who reads what [V, grep of `Game/Game.UI*`]

| Disabled system | UI readers |
|---|---|
| `ResidentialDemandSystem` | `CityInfoUISystem` (`buildingDemand` at `:197-199`, factor arrays at `:238-252`) |
| `CommercialDemandSystem` | `CityInfoUISystem:200, 259`; `OutsideConnectionsInfoviewUISystem` |
| `IndustrialDemandSystem` | `CityInfoUISystem:201-203`; `OutsideConnectionsInfoviewUISystem` |
| `CityStatisticsSystem` | `StatisticsUISystem`, `TaxationUISystem`, `ToolbarBottomUISystem`, `Population/Healthcare/Police/Post/Roads/Tourism/Transport` infoviews |
| `CityServiceBudgetSystem` (KEEP or sync) | `BudgetUISystem`, `ServiceBudgetUISystem`, `ToolbarBottomUISystem` |
| `TaxSystem` | `TaxationUISystem`, `DeveloperInfoUISystem`, `AverageHappinessSection`, `ProfitabilitySection` |
| `TradeSystem` | `Electricity/WaterInfoviewUISystem` |
| `CityProductionStatisticSystem` | `ProductionUISystem`, `TempExtractorTooltipSystem` |
| `CountHouseholdDataSystem`, `CountWorkplacesSystem` | `PopulationInfoviewUISystem` |
| `CountCompanyDataSystem` | `OutsideConnectionsInfoviewUISystem` |
| `ElectricityStatisticsSystem` / `WaterStatisticsSystem` | electricity and water infoviews |
| `ServiceFeeSystem` | `ServiceBudgetUISystem`, `WealthInfoviewUISystem`, `ResidentsSection`, `AverageHappinessSection`, `DeveloperInfoUISystem` |
| `GameModeGovernmentSubsidiesSystem` (KEEP) | `BudgetUISystem`, `StatisticsUISystem` |
| `XPSystem` / `MilestoneSystem` | `MilestoneUISystem` (messages `:330`; XP itself from `CitySystem.XP` `:400`) |
| `DevTreeSystem` | `DevTreeUISystem` |

### 5.2 Is runtime `Deserialize` side-effect-free?

| System | `ISerializable`? | What `Deserialize` writes | Side effects | Verdict |
|---|---|---|---|---|
| `ResidentialDemandSystem` | yes (`:26`) | `m_HouseholdDemand`, `m_BuildingDemand` (NativeValues), 3 factor arrays, `m_LastHouseholdDemand`, `m_LastBuildingDemand` (`:405-465`). The UI properties return the `m_Last*` fields (`:280-282`) [V] | none. Does **not** complete `m_ReadDependencies` (`:268-270`) [V] | OK after a job sync (§5.3) |
| `CommercialDemandSystem` | yes (`:28`) | demands, factors, resource and building demand arrays (`:492-530`). Branches on `format.Has(FormatTags.FishResource)` [V] | none | OK with a full-tag context |
| `IndustrialDemandSystem` | yes (`:29`) | analogous (`:1003+`) [V partial] | none [I] | OK |
| `TaxSystem` | yes (`:28`) | `m_TaxRates[i]` (`:284-315`). **Without the `FishResource` tag it shifts rates 51..90 and zeroes the fish rates** (`:306-313`) [V] | `PostDeserialize` resets rates only for old versions (`:351-360`) | OK only with the full current tag set. Complete `Readers` (`:215, 235`) first |
| `TradeSystem` | yes (`:27`) | `m_TradeBalances` (`:442-458`), tag-dependent [V] | none | OK |
| `CityStatisticsSystem` | yes (`:22`) | `m_SampleCount`, `m_LastSampleFrameIndex`, **replaces the pending event queue** (`:882-929`); completes `m_Writers` first [V] | `PostDeserialize` → `InitializeLookup` can **destroy all statistic entities** (`:787-789`) or **create missing ones** (`:826`) [V] | `Deserialize` is OK, but it does **not** carry the statistics, which live in `CityStatistic` buffers on statistic entities (`:461-474`). Replicate those buffers as entity data. Never call `PostDeserialize` at runtime |
| `CityServiceBudgetSystem` | **no** (`:26`) | – | – | KEEP it running on replicated inputs, or OFF + custom sync of `m_Income`/`m_Expenses` |

### 5.3 Context requirements [V facts / I recipe]

- `Context(purpose, version, guid)` leaves `m_Format` default (`Context.cs:21-28`). `ContextFormat.Has` indexes `m_Bits[num >> 5]` on an uncreated list (`ContextFormat.cs:31-35`). `EnableTag` is `internal` (`:26-29`) [V]. So a mod cannot build a valid tagged context without reflection.
- The context the game creates while loading a save carries that save's version and its written tags (`EntityDeserializer.cs:270-295`). `LoadGameSystem` keeps it in `m_Context` until the next load, which disposes it through the setter (`LoadGameSystem.cs:22-33`) [V]. When the client loads the host's freshly written save, this context has `Version.current` and all current tags [I]. A cached copy shares `m_Bits` and becomes dangling after the next load [I from the setter code]. **Refresh the cached copy on every `onOnSaveGameLoaded`, or build an owned context with `Version.current` and set every `FormatTags` bit by reflection** (count from `SerializerSystem.cs:145`).
- Thread safety: these systems hand out arrays to jobs and track readers manually (`TaxSystem.AddReader` `:368-371`; `ResidentialDemandSystem.m_ReadDependencies`). Deserialize inside `NetApplySystem`, *after* its structural batch. A structural change completes all tracked jobs (08 §9) [I that manual readers are covered; verify with the safety system in a development build].

---

## 6. The single apply point (task 6)

### 6.1 Contradiction matrix

| Report | Claimed position | Code check | Verdict |
|---|---|---|---|
| 02 §9.1 | MainLoop start, before `ToolSystem`; deletions before PostTool | `SubElementDeleteSystem` is only in PostTool and writes through `ToolReadyBarrier` (`SystemOrder.cs:697-698, 725`); `OriginalDeletedSystem` is PreTool (`:699`) [V] | **correct** |
| 03 §1.1/§9.6 | before `ModificationSystem`, then re-apply after Mod4/5 | a point between `ToolSystem` and `ModificationSystem` misses PreTool and PostTool for deletions. Re-apply is unnecessary with the OFF table in §2.11 | **too late for deletes**; re-apply superseded |
| 08 §12.4 | before Modification1, *or* after ModificationEnd | tags added after ModificationEnd are snapshotted by `PrepareCleanUpSystem` (`:50`) and removed in Cleanup without any Modification system seeing them (02 §5.2). `SafeCommandBufferSystem` barriers are closed by then (02 §1.5) [V] | "after ModificationEnd" **rejected** |
| 09 §5.4 | Modification1 or a dedicated barrier before ModificationEnd | `ModificationBarrier1` plays back at the end of Modification1 (`:86`), so Mod1 `Generate*` miss it in that frame; PreTool and PostTool are missed [V] | **rejected**. Its real requirement (atomic, before ModificationEnd enforcers) is met by MainLoop start |

### 6.2 Decision

```csharp
// client only (host skips registration or keeps the system disabled)
updateSystem.UpdateAfter<NetApplySystem, EndFrameBarrier>(SystemUpdatePhase.MainLoop);
```

- Placement is deterministic. A two-type `UpdateAfter` child is emitted immediately after its parent (`UpdateSystem.cs:178-181, 278-292, 426-440`) [V]. It is therefore after `EndFrameBarrier` playback (`EndFrameBarrier.cs:34-43`) and before `RaycastSystem` (`SystemOrder.cs:55`) [V]. It does not depend on mod load order.
- What it sees: all of last frame's simulation and UI ECBs have been played back [V]. On clients that is only local UI and preview changes.
- What follows it in the same frame, in order:
  1. PreTool `OriginalDeletedSystem`.
  2. PostTool `SubElementDeleteSystem`.
  3. Modification1..5 regeneration.
  4. ModificationEnd (icons, validation, mod enforcers).
  5. `UnlockSystem` (MainLoop, after Modification), which consumes `Unlock` events created by the applier in the same frame [V `SystemOrder.cs:61`].
  6. `PrepareCleanUpSystem` and Cleanup (destroys `Deleted`, clears tags).
- Write mechanism: direct `EntityManager` batch calls on the main thread. `EndFrameBarrier` has just been a sync point, so the extra stall is small [I]. Do structure first, then raw data writes (08 §12.4 A/B).

### 6.3 Tag set

| Operation | Tags to add | Why (code) |
|---|---|---|
| **Create** (any replicated entity) | the prefab or host archetype, which already contains `Created`+`Updated` (02 §9.1.2), **plus `Applied`** and the host's `UpdateFrame` shared value | `Applied` makes `UpdateGroupSystem` keep the given `UpdateFrame` (`UpdateGroupSystem.cs:408-416`). It makes `ParkedVehiclesSystem` skip the building (`ParkedVehiclesSystem.cs:1683-1700`, `None(Applied)`). Other `Applied` consumers are cosmetic (`ToolFeedbackSystem.cs:1302-1320`; `PreCullingSystem` action flags) or lane rechecks (`RoutePathSystem.cs:339`) [V] |
| **Update**: static objects, nets, areas, routes, zone cells | `Updated` on the entity **and its topological closure** (02 §7), plus `BatchesUpdated` for visuals; `Applied` if the host change came from a tool | derived systems key on `Updated` (02 §6). `GroundHeightSystem`, `Buildings.InitializeSystem`, `BuildingStateEfficiencySystem` and `CityServiceWorkplaceInitializeSystem` re-run, which is harmless |
| **Update**: visual-only (`Damaged`, `Destroyed` visuals, colour, `Unspawned`) | `BatchesUpdated` only | avoids re-running `Updated` consumers |
| **Update**: effects | `EffectsUpdated` | as the game's own `StreetLightSystem.cs:151` |
| **Update**: lane pathfinding data | `PathfindUpdated` | `LaneDataSystem`/`LanesModifiedSystem` (02 §5.1) |
| **Update**: moving objects (`TransformFrame`, lane state) | **no tags**. Write the frame slot directly | `Updated` on a moving object resets all 4 `TransformFrame`s and `InterpolatedTransform` (`UpdateGroupSystem.cs:561-614, 724`) and re-randomises lane positions (`Vehicles/InitializeSystem.cs:341`; `Creatures/InitializeSystem.cs:256-258`) [V] |
| **Delete** | `Deleted` on the entity and on every first-class child the host deleted; `Updated` on surviving neighbours (closure); `Applied` mirrored from the host for VFX | `SubElementDeleteSystem` (PostTool) cleans sub-areas, sub-nets, sub-routes and owned vehicles (`SubElementDeleteSystem.cs:76-160`). `CleanUpSystem` destroys the entity at the end of the frame (02 §5.2). Never call `DestroyEntity` directly |
| **Events** | create only `Event+Unlock` (for `UnlockSystem`). **Never** replicate `Damage`/`Destroy`/`Ignite`/`Impact`/`AddHealthProblem`/`Modify`/`ResetTrip`/`RentersUpdated`/`PathTargetMoved` command events | their consumers are OFF, or would double-apply (§2.4, §2.7) |
| **Icons** | not as entities. Issue `IconCommandBuffer.Add/Remove(owner, prefab, priority, layer, flags, target)` from `NetApplySystem` | `IconCommandSystem` stays on and deduplicates (`IconCommandSystem.cs:157, 563`). Locally derived icons (connection warnings, road connection) then converge with host icons [V/I] |

### 6.4 Why no post-ModificationEnd re-apply

- With the §2.11 OFF set, no enabled initializer overwrites a host-authoritative field that the applier wrote, with three exceptions:
  - `UpdateGroupSystem`'s `TransformFrame` reset on creation, overwritten by the next stream frame;
  - `Game.Net.InitializeSystem`'s coverage reset;
  - prefab `WaterSourceInitializeSystem`.

  For these three, the host sends the affected fields in the delta *after* the creation tick (same apply point, next frame) [I]. This keeps a single apply point.
- A late fixup pass after ModificationEnd could only write values without tags (02 §5.2). Derived consumers would not see them until the next `Updated` [V ordering]. Rejected.

### 6.5 Coexistence with client-local tool previews

1. Local `Temp` entities are never replicated and never touched by `NetApplySystem`. Search trees exclude `Temp` (02 §3.3) [V].
2. Because the applier runs before PreTool, a host deletion of an entity that a local preview edits (`Temp.m_Original`) is seen the same frame by `OriginalDeletedSystem`:
   - `GetAllowApply()` returns false (`ToolBaseSystem.cs:533-540`);
   - `GetRaycastResult(..., out forceUpdate)` forces a preview rebuild (`:555-559`) [V].
3. Host updates to originals (`Updated`) are picked up when the tool next rebuilds its definitions (it sets `applyMode = Clear` on input, 02 §2.3). Stale preview geometry for one or two frames is acceptable because the host re-validates every intent [I].
4. Clients never let `ToolOutputSystem` reach `ApplyTool`. Otherwise `ToolApplySystem` charges money for all Temps and Apply* create permanent entities locally (02 §4.1-4.2) [V]. The click becomes an intent (definitions or change-set, 02 §10.4). The local Temps are cleared through `ClearTool` (`SystemOrder.cs:712`).
5. `ValidationSystem` validates local Temps against the replicated world (`SystemOrder.cs:264`), so preview errors match the host world state [V/I].
6. `ToolFeedbackSystem` shows transaction icons for `Applied+Created` replicated buildings. Remote placements therefore get the same feedback (`ToolFeedbackSystem.cs:1302-1320`) [V].

---

## 7. Clock follow (task 7)

### 7.1 Is a Harmony prefix that forces the tick count enough?

The tick count `num` is a local variable (`SimulationSystem.cs:220`). It is computed from `selectedSpeed`, `Time.deltaTime` and `m_Timer` (`:228-238`), then capped by three things:

1. the pathfinding deadline (`:231-245`);
2. CPU back-pressure through `EndFrameBarrier.lastElapsedTime/frameDuration` unless `PerformancePreference.SimulationSpeed` (`:246-253`);
3. a hard cap `num7 = clamp(round(selectedSpeed*num3*2), 1, 8)` (`:255-257`) [V].

A prefix that only adjusts `m_SelectedSpeed`/`m_Timer` cannot exceed 8 ticks per frame or bypass the caps. It also cannot catch up after a stall. **Not enough** [V].

Recommended: a client prefix on `SimulationSystem.OnUpdate` that returns `false` and reimplements the body:

1. **Loading:** run the original while `m_IsLoading` or `GameManager.instance.isGameLoading` (`:202-219`).
2. **Tick count:** `ticks = clamp(hostTargetFrame − frameIndex, 0, maxCatchUp)`. `hostTargetFrame` is interpolated from host clock packets minus a jitter margin [I].
3. **Phases:** call `Update(PreSimulation)`, then per tick `frameIndex++` and `Update(GameSimulation, frameIndex, i)`, then `Update(PostSimulation)`, preserving the order at `:272-295`.
4. **Private setters:** `frameIndex` has a private setter (`:68`), so use reflection, or `Deserialize` for hard jumps. `Deserialize` also zeroes `frameTime` and `m_Timer` (`:140-146`) [V].
5. **Interpolation outputs:** set `frameTime` to the host sub-tick phase and `smoothSpeed` to the host's effective speed. Readers are `RenderingSystem.cs:186-195`, `ObjectInterpolateSystem.cs:3042-3130`, `AudioManager.cs:1375`, `ClimateRenderSystem.cs:506`, `PathfindSetupSystem.cs:329` (grep) [V].
6. **No skipping:** keep ticks contiguous except on hard resync. Kept GameSimulation systems (Time, Climate, Wind, ambience) fire by `(frameIndex & (interval−1)) == offset` (`UpdateSystem.cs:241`). Skipped indices skip their updates [V].
7. **Smoothing:** `RenderingSystem` soft-clamps its own frame toward the simulation within ±15 frames with an atan curve (`RenderingSystem.cs:175-200`). Small catch-up bursts are smoothed; jumps over 15 frames visibly lag [V].

### 7.2 `PathfindResultSystem.pendingSimulationFrame` back-pressure on clients

- `pendingSimulationFrame = min(result-side pending, setup-side pending)` (`PathfindResultSystem.cs:123`; `PathfindSetupSystem.cs:224`) [V].
- It is recomputed every frame from items with a `m_ResultFrame` (`PathfindResultSystem.cs:165, 258, 270`; `PathfindSetupSystem.cs:427-433`). Graph-maintenance actions (create, update, delete, density, time, flow) never touch it (`PathfindResultSystem.cs:428-449`) [V].
- Client-side enqueuers that remain enabled all pass `uint.MaxValue` [V]:
  - `RoutePathSystem.cs:634`
  - `CoveragePreviewSystem.cs:409`
  - `ToolFeedbackSystem.cs:1490`

  The graph updaters (`LanesModifiedSystem.cs:1252, 1293, 1308`; `RoutesModifiedSystem.cs:1439, 1485, 1500`) use the deadline-free overload [V].
- Deadline producers are all GameSimulation and OFF on clients [V]:
  - the 45 `m_PathfindSetupSystem.GetQueue` caller files;
  - `ServiceCoverageSystem.cs:659`;
  - `ResourceAvailabilitySystem.cs:1198` (`frameIndex+64`).
- **Result: no back-pressure on clients** [V].
- Hazard if a stale deadline survives the switch to follower mode: `pending − frameIndex − 1` is computed in `uint` (`SimulationSystem.cs:233, 242`). A pending frame at or below `frameIndex` wraps, and the `(int)` cast can make `num5` negative. That clamps the vanilla tick count to 0 until the worker finishes [V arithmetic]. The replacement `OnUpdate` ignores `pendingSimulationFrame`, and gating is applied at load before the first tick [I].

### 7.3 Neutralising local pause sources

- Writers of `selectedSpeed` (05 §9.2):
  - `TimeUISystem` (intent);
  - the forced-pause barrier;
  - `ErrorDialogManager`;
  - `ClimateRenderSystem` lightning (`ClimateRenderSystem.cs:444-449` per 05);
  - photo mode;
  - loading (`SimulationSystem.cs:158, 213, 218`);
  - debug and benchmark.
- With the replacement `OnUpdate`, the tick count ignores them [I]. They still matter in three places:
  1. `RenderingSystem` sets `frameDelta = 0` when `selectedSpeed < 1e-5` (`RenderingSystem.cs:186-189`), which freezes interpolation [V];
  2. UI shows "paused";
  3. audio uses `smoothSpeed`.
- Recipe:
  - Register `UpdateBefore<ClockFollowSystem, SimulationSystem>(SystemUpdatePhase.LateUpdate)` (two-type, placed right before, `UpdateSystem.cs:173-176, 396-413`).
  - Each frame, it writes the host speed into `m_SelectedSpeed`. The setter is ignored while loading (`:72-85`).
  - Patch the lightning-pause and error-dialog writers on clients, or let them write and overwrite them in the same frame [I].

---

## 8. Consolidated client gating list

### 8.1 OFF (or OFF+feed) outside GameSimulation

| Phase | Systems |
|---|---|
| ToolUpdate | `UpgradeDeletedSystem` |
| ApplyTool | Apply* (never triggered on clients) |
| Mod1 | `LoanSystem` (or keep + INTENT), `AddMeetingSystem` |
| Mod2 | `Game.Events.InitializeSystem`, `OutsideConnectionInitializeSystem`, `DamageSystem`, `DestroySystem` |
| Mod3 | `DefaultPoliciesSystem` |
| Mod4 | `HouseholdInitializeSystem`, `Game.Routes.InitializeSystem`, `ImpactSystem`, `IgniteSystem`, `FaceWeatherSystem`, `AddHealthProblemSystem`, `SpectateSystem`, `EndangerSystem`, `AddAccidentSiteSystem`, `AddCriminalSystem`, `SubmergeSystem`, `TripResetSystem`, `EventJournalInitializeSystem` |
| Mod4B | `ObjectEmergeSystem` |
| Mod5 | `ZoneBuiltRequirementSystem`, `ResourcesInitializeSystem`, `ParkInitializeSystem`, `CitizenInitializeSystem`, `HouseholdPetInitializeSystem`, `MeetingInitializeSystem`, `Game.Citizens.CompanyInitializeSystem`, `StorageInitializeSystem`, `EventJournalSystem` |
| ModEnd | `ZoneCheckSystem`, `ServiceRequestSystem`, `XPBuiltSystem`, `NetXPSystem`, `XPSystem`, `MilestoneSystem`, `TriggerSystem`, `LifePathEventSystem`, `CreateChirpSystem`, the three `*RequirementSystem`s, `DevTreeSystem`, `RandomLocalizationInitializeSystem`, `NotificationTriggerSystem` |
| LateUpdate | `AutoSaveSystem`, `DebugSystem` actions |
| LoadSimulation | all (never run for `LoadGame`) |

### 8.2 GameSimulation

Everything in `SystemOrder.cs:307-602` is OFF, except:

- `TimeSystem` (`:359`), `ClimateSystem` (`:360`), `SnowSystem` (`:361`), `WindSimulationSystem` (`:362`), `WindSystem` (`:363`);
- `ZoneAmbienceSystem` (`:438`) + `SpawnableAmbienceSystem` (`:461`);
- `TrafficAmbienceSystem` (`:454`);
- `WetnessSystem`, `DirtynessSystem` (`:471-472`);
- `StreetLightSystem` (`:521`).

Line numbers are from the SystemOrder listing [V].

### 8.3 How to gate robustly

- Several systems reset `Enabled` in `OnGamePreload`/`OnGameLoadingComplete` [V, grep]:
  - `UnlockSystem.cs:124`, `TriggerSystem.cs:390`, `CreateChirpSystem.cs:445`, `LifePathEventSystem.cs:175`, `NotificationTriggerSystem.cs:139`, `RadioTagSystem.cs:55`;
  - `CityStatisticsSystem.cs:435`, `CityServiceBudgetSystem.cs:827`, `GameModeGovernmentSubsidiesSystem.cs:62`;
  - `ToolFeedbackSystem.cs:1369`, `UpgradeDeletedSystem.cs:625`, `ParkedVehiclesSystem.cs:1723`;
  - the tutorial systems.

  `CityConfigurationSystem.OnGameLoaded` sets `UnlockAllSystem.Enabled` (`:197`). Apply the gate table after each load completes and re-assert it every frame with a cheap guard [I].
- Prefer `Enabled = false` over Harmony-skipping `OnUpdate`. `SystemBase.Update` then skips `OnUpdate` and calls `OnStopRunning` (03 §7). `OnStopRunning` disposes the `TriggerSystem` and `IconCommandSystem` queue lists (`TriggerSystem.cs:395-402`; `IconCommandSystem.cs:716-724`) [V].
- Build the list from a declared table so that a game update that renames or adds systems fails loudly (03 §9.6).

---

## 9. Risks

| # | Risk | Evidence | Mitigation |
|---|---|---|---|
| R1 | Unguarded producers leak when their consumer is OFF | §4 Q1, Q3, Q4, Q6 [V] | gate table. Startup self-check: every OFF consumer has all producers OFF |
| R2 | A forgotten append or random initializer duplicates or rewrites host data | §2.11 [V] | a test harness that creates every replicated archetype on a client and diffs against the host payload after one frame [I] |
| R3 | `Updated` on moving objects causes teleport and jitter | `UpdateGroupSystem.cs:561-614`; `Vehicles/InitializeSystem.cs:341` [V] | applier rule in §6.3 |
| R4 | Locally regenerated children diverge or duplicate: gate bypass routes, snow sub-objects, traffic-route visualisations | `Buildings/InitializeSystem.cs:154-168`; `WetnessSystem.cs:89-95`; `TrafficRoutesSystem.cs:649-727` [V] | never replicate them. Exclude them from change detection and hashing |
| R5 | Runtime `Deserialize` with a bad context shifts tax rates or corrupts arrays | `TaxSystem.cs:306-313`; `ContextFormat.cs:31-35` [V] | owned full-tag context (§5.3) |
| R6 | `CityStatisticsSystem.PostDeserialize` destroys or creates statistic entities | `CityStatisticsSystem.cs:787-789, 826` [V] | never call it at runtime. Replicate `CityStatistic` buffers |
| R7 | Local `GroundHeightSystem` height corrections differ from the host by < 1 terrain LSB | `GroundHeightSystem.cs:609-613`; 07 §2.2 [V] | epsilon in the anti-entropy comparison for heights |
| R8 | `UpdateFrame` mismatch for creations replicated without the host value | `UpdateGroupSystem.cs:408-460` [V] | create with `Applied` + host `UpdateFrame`, or hash by network ID instead of `UpdateFrame` |
| R9 | Per-player tutorials emit `Unlock` events and gate milestones | `TutorialSystem.cs:300-315, 970-1000`; `MilestoneSystem.cs:73-76` [V] | disable tutorials for clients in sessions, or filter tutorial `Unlock`s. The host's tutorial state must not block milestones (host-side setting) |
| R10 | `ModifiedSystem` sends telemetry if host `Modify` events are replayed | `ModifiedSystem.cs:451-453` [V] | replicate `Policy` and modifier *state*, not `Modify` events |
| R11 | System-state `Deserialize` races jobs that still read the arrays | `ResidentialDemandSystem.cs:268-270`; `TaxSystem.cs:215` [V] | deserialize after the structural batch (job sync) [I] |
| R12 | Ambience audio decays on clients | `ZoneAmbienceSystem.cs:20-24` [V] | keep `SpawnableAmbienceSystem`, or ship the maps |
| R13 | XP popups, chirps, life-path and radio news missing on clients | §4 Q2, Q5 [V] | forward the host's XP messages and chirp entities |
| R14 | Mods: enforcers in ModificationEnd (09 §5.4) and extra GameSimulation systems shift update offsets (03 §2.1) | – | identical mod set; per-mod gate entries; atomic apply at MainLoop start |
| R15 | `UnityEngine.Assertions` behaviour in the shipped player (log vs throw) not verified | `Unity.Assertions/Assert.cs:21-27` [V]; engine side [I] | treat as log-only; never rely on the assert |

---

## 10. Open questions

1. Verify in a development build that `EntityManager` structural batches in `NetApplySystem` complete the manually tracked reader handles of demand, tax and trade arrays (§5.3).
2. `IndustrialDemandSystem.Deserialize` was only partly read (`:1003+`). Confirm there are no further tag-dependent branches.
3. `GroupSystem` and `HouseholdPetRemoveSystem` were classified from grep only. Read their queries before the gate table is finalised.
4. Which UI paths read `MilestoneSystem.currentXP/nextRequiredXP` (`MilestoneSystem.cs:41-51`)? They go stale when the system is OFF; ship them or compute them client-side.
5. Do any vanilla `Created`-reacting systems not covered here (inside GameSimulation, but kept on clients) exist? The kept GameSimulation set in §8.2 contains none from a grep of their queries [I].
