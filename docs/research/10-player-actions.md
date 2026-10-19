# 10 — Player actions outside the Temp/Apply pipeline, and intercepting tool apply on clients

> **Historical research report.** Written before and during the implementation; it describes the game, not the
> mod, and is not maintained. For how the mod works, see [docs/ARCHITECTURE.md](../ARCHITECTURE.md). Code is cited
> by file and member and described in prose; no game code is reproduced.

Project: **Skyline Together** (clean-room, host-authoritative co-op mod for Cities: Skylines II).
Scope: (A) a full catalogue of player actions that change city state through UI triggers or other non-tool paths,
with their exact mutation mechanism, consumer, replay parameters, side effects and sync class; (B) the cleanest way to
capture, suppress or predict a local tool apply on a client, and to commit it on the host while the host's own player
may be using a tool.

Conventions
- Paths are relative to the root of the game's decompiled assemblies (folder `Game`) unless prefixed `thirdparty/`
  (= `...\mod_audit\thirdparty`). `file.cs:N` = line N. `SystemOrder.cs` = `Game.Common/SystemOrder.cs`.
- **[V]** = read in the decompiled code. **[I]** = inference or design reasoning.
- Binding names are written `group.name`. Sections derived from `InfoSectionBase` use their class name as the group
  (for example `ActionsSection.cs:76`, `UpgradesSection.cs:34`, `TitleSection.cs:22`) [V].
- Read first: report 02 (tool pipeline). Its findings are referenced as "02 §x".

---

## 0. TL;DR

1. **41 files in `Game.UI.InGame` register 182 `TriggerBinding`s** (plus a few `CallBinding`s in
   `CinematicCameraUISystem`) [V, grep count]. About **45 bindings change shared city state**. They use **eight distinct
   mutation mechanisms** (§2): event entities on `EndFrameBarrier`, `Deleted` on `EndFrameBarrier`, direct
   `EntityManager`/`DynamicBuffer` writes, managed-system setters, a queued action (`LoanSystem`), tool activation, and
   so on. There is **no single choke point**.
2. **There are a few good choke points** [V]:
   - `PoliciesUISystem.ModifyPolicy` (`Game.UI.InGame/PoliciesUISystem.cs:237-242`) is the only producer of
     `Game.Policies.Modify` events. It carries **11 UI actions**: policies, out-of-service toggles, emptying, ticket
     price, vehicle count, day/night schedule, line active state.
   - `NameSystem.SetCustomName` (`Game.UI/NameSystem.cs:214-241`) handles all renames.
   - `TaxSystem` setters, `CityServiceBudgetSystem.SetServiceBudget`, `ServiceFeeSystem.SetFee`,
     `LoanSystem.ChangeLoan`, `DevTreeSystem.Purchase(Entity)` and `MapTilePurchaseSystem.PurchaseSelection` are
     public, stable entry points for the economy and progression.
3. **The modifier question from report 04 is resolved** [V]:
   - `DistrictModifier`, `BuildingModifier` and `RouteModifier` (and the `m_OptionMask` fields) are recomputed from
     `Policy` buffers **only** in two places: by `Game.Policies.ModifiedSystem` when a `Modify` event arrives, and by
     the `*ModifierInitializeSystem`s on `Created` entities (Modification4).
   - `CityModifier` is additionally recomputed **every 256 simulation frames** by `CityModifierUpdateSystem`. That
     recompute also folds in `CityEffectProvider` buildings weighted by their efficiency.
   - Consequence: writing a `Policy` buffer directly does **not** update modifiers on existing entities. Replicate
     policies as `Modify` events, or replicate the modifier buffers too.
4. **Host execution of UI commands must respect `SafeCommandBufferSystem`.** Many handlers call
   `EndFrameBarrier.CreateCommandBuffer()`, which **throws** between the barrier's playback (start of MainLoop) and
   `AllowBarrier<EndFrameBarrier>` (after `ModificationSystem`) (`Game/SafeCommandBufferSystem.cs:15-22`;
   `SystemOrder.cs:49,62`) [V]. The host must therefore run replayed UI commands after `AllowBarrier<EndFrameBarrier>`
   or in `UIUpdate`, not at MainLoop start.
5. **Some actions are dangerous for sync**:
   - `TimeUISystem` forces `selectedSpeed = 0` whenever the window loses focus or a UI "pause barrier" is observed
     (`TimeUISystem.cs:115-137`). On the host this would pause everyone.
   - Photo mode can set simulation speed in the range 0–8 (`Game.Rendering/PhotoModeRenderSystem.cs:765-780`).
   - `SimulationSystem` applies the local `pausedAfterLoading` setting on load (`SimulationSystem.cs:213`).
   - `VisualCustomizeSection.toggleHistorical` writes `Building.m_Flags` directly **without any tag**, but it changes
     simulation results (`BuildingUpkeepSystem.cs:241,280`).
   - `SelectVehiclesSection` and `DistrictsSection.removeDistrict` write buffers directly without tags.
   - `SelectionToolSystem` (service districts) writes `ServiceDistrict` **outside Apply**.
6. **Recommended client tool-apply design (Part B)**: **predict locally, ship the change-set, host commits via state
   replication, originator reconciles.**
   - (a) Capture the pre-apply Temp set with a mod system `UpdateBefore<…>(SystemUpdatePhase.ApplyTool)`. That phase
     runs only on apply frames.
   - (b) Let the vanilla apply run locally as a prediction.
   - (c) Capture the exact local result set (entities tagged `Applied`/`Created`/`Updated`/`Deleted`) right after
     `ToolOutputBarrier` playback, before Modification1.
   - (d) The host validates and commits the change-set with the direct-write path from 02 §9, without touching its own
     Temp namespace. It then broadcasts with network IDs.
   - (e) The originator binds its predicted entities to the host IDs, or rolls back by re-syncing the touched entities.
   - Optionally suppress local commits by rewriting the active tool's `applyMode` from `Apply` to `Clear` in a system
     ordered just before `ToolOutputSystem`.
   - Definition replay on the host is kept only as a fallback for when the host's tool is idle.

---

## 1. Method and coverage

- Enumerated every `new TriggerBinding`/`new CallBinding` in `Game.UI.InGame` (41 + 1 files, 182 triggers) and in
  `Game.UI` and `Game.UI.Menu` [V]. Read every handler that could change shared state, then followed it into the
  consuming system and its phase in `SystemOrder.cs`.
- Followed non-UI public setters used by the UI: `TaxSystem`, `CityServiceBudgetSystem`, `ServiceFeeSystem`,
  `LoanSystem`, `DevTreeSystem`, `MapTilePurchaseSystem`, `NameSystem`, `CityConfigurationSystem`,
  `SimulationSystem.selectedSpeed`, `WaterSystem`, `LifePathEventSystem`, `TutorialSystem`, `MeshColorPaletteSystem`.
- Checked `Game.Settings` for settings that influence the simulation (§6.3).
- `CityConfigurationSystem` options other than `cityName` are written in game only by `DebugSystem`
  (`leftHandTraffic`, `Game.Debug/DebugSystem.cs:4805`). The new-game menu writes them into `userState`
  (`Game.UI.Menu/MenuUISystem.cs:830-850`). They are therefore session options, not in-game actions [V].
- **When UI handlers run** [I]: `GameManager.Update` calls `UpdateWorld()` (MainLoop), then `UpdateUI()`
  (`m_UIManager.Update()`), then `PostUpdateWorld()` (Cleanup) (`Game.SceneFlow/GameManager.cs:712-725,1804-1808`).
  Trigger callbacks are dispatched by the UI view update, so they run **after MainLoop and before Cleanup**. This is
  consistent with all handlers being allowed to use `EndFrameBarrier`, whose window is open from `AllowBarrier`
  (MainLoop) until the next playback. Consequences [V via 02 §5.2]:
  - `EndFrameBarrier` commands become visible at the **start of the next MainLoop**.
  - Tags written directly with `EntityManager` in a handler are added after `PrepareCleanUpSystem`'s snapshot, so they
    survive into the next frame's Modification.

---

## 2. Mutation mechanism taxonomy

| Id | Mechanism | When visible | Examples | Interception |
|---|---|---|---|---|
| **M1** | One-frame **event entity** (`Event` + payload) on `EndFrameBarrier` | next MainLoop; consumed in Modification; destroyed in Cleanup (`Event` is in the delete snapshot, 02 §5.2) | `Modify` (policies), `Unlock` (dev tree), `ColorUpdated` (route colour) | Prefix the producer, **or** a mod system in MainLoop after `EndFrameBarrier` playback that reads and destroys the events before their consumer runs (client suppression) [I] |
| **M2** | `Deleted` added via `EndFrameBarrier` | next MainLoop; `UpgradeDeletedSystem` (ToolUpdate), `SubElementDeleteSystem` (PostTool), references systems (Mod2B/5) | Delete district, upgrade or line | Prefix the handler. Origin cannot be told apart from simulation deletions afterwards |
| **M3** | Component/buffer set via `EndFrameBarrier` (+`Updated`/`BatchesUpdated`) | next MainLoop | Route `Color`, `CustomName`, chirp `Liked`, `CustomMeshColor` enable | Prefix the handler |
| **M4** | **Direct** `EntityManager`/`DynamicBuffer` write on the main thread, **no tag** | immediately; nothing reacts except systems that read every frame | `VehicleModel`, `ServiceDistrict` (UI remove), `Building.m_Flags` Historical, `ServiceFee`, `CustomMeshColor` buffer contents, palette | Prefix the handler or diff the state |
| **M5** | Direct write **with a tag** | immediately; tag survives to next Modification | Map tile `Native` removal + `Updated`, `Followed` + `LifePathEntry` | Prefix the API |
| **M6** | Managed system field or property (`NativeArray` or C# field) | immediately | `TaxSystem.m_TaxRates`, `SimulationSystem.selectedSpeed`, `CityConfigurationSystem.cityName`, `NameSystem.m_Names`, `DevTreePoints` singleton | Prefix the setter or diff the state |
| **M7** | Queued action consumed by a system | `LoanSystem.OnUpdate` (Modification1) | `LoanSystem.ChangeLoan` | Prefix `ChangeLoan` |
| **M8** | **Tool activation**: the action only changes which tool is active; the real mutation happens later via Temp/Apply or a tool-internal direct write | when the player applies | move, relocate, rebuild, lot edit, district paint, service-district selection, map-tile selection | Part B; `SelectionToolSystem` needs special handling (§7.7) |

---

## 3. Master action table

Columns:
- **Class**: `S` = SHARED city state (must become a host command); `P` = PER-PLAYER meta; `L` = LOCAL presentation;
  `T` = tool state (local, but it leads to a Part B action).
- **Params**: entities map to network IDs (`NID`), prefabs to `PrefabID`.
- All handler lines are in `Game.UI.InGame/` unless stated otherwise.

### 3.1 Policy family (M1 → `Game.Policies.ModifiedSystem`, Modification4)

Common path [V]:
- `PoliciesUISystem.ModifyPolicy(target, policy, active, adjustment)` creates an `Event`+`Modify` entity on
  `EndFrameBarrier` (`PoliciesUISystem.cs:157,237-242`). `Modify{m_Entity, m_Policy, m_Flags, m_Adjustment}` is
  defined in `Game.Policies/Modify.cs`.
- `ModifiedSystem` (`SystemOrder.cs:146`, Modification4; query `Event+Modify`, `ModifiedSystem.cs:396,400`) edits the
  target's `Policy` buffer. It adds, updates or removes the entry. A slider policy is removed only when it is
  deactivated **and** its adjustment equals the slider default (`:111-171`).
- It then calls `RefreshEffects` (`:250-299`), which recomputes District/Building/Route/City options and modifier
  buffers, and adds `Updated` to the target via `ModificationBarrier4` (`:113,298,436`).
- Special case: an `Extension` target with an `Inactive` building option toggles `ExtensionFlags.Disabled` (`:174-186`).
- A `ServiceUpgrade` target adds `Updated` to its owner, and to itself if it has `CoverageElement` (`:187-194`).
- Side effects:
  - `TriggerType.PolicyActivated` on activation (`:163`).
  - `TriggerType.FreePublicTransport` when the ticket-price policy is removed (`:198-211`).
  - Telemetry (`:451-454`).

| # | Binding (file:line) | Handler → call | Params for replay | Extra side effects | Class |
|---|---|---|---|---|---|
| P1 | `policies.setPolicy` (`PoliciesUISystem.cs:161`) | `SetSelectedInfoPolicy(policy, active, adj)` `:227-230` → `ModifyPolicy(selectedEntity,…)` | target NID, policy PrefabID, bool, float | — | S |
| P2 | `policies.setCityPolicy` (`:162`) | `SetCityPolicy` `:191-195` → target = `CitySystem.City` | policy PrefabID, bool, float | achievement progress `CallingtheShots` (`:197-225`, per-player) | S |
| P3 | `ActionsSection.toggle` (`ActionsSection.cs:154`) | `OnToggle` `:227-237`: route → "Route Out of Service", service building or upgrade → "Out of Service" (names resolved `:208-225`) | target NID, policy PrefabID, bool (`!disabled`) | — | S |
| P4 | `ActionsSection.toggleEmptying` (`:155`) | `OnToggleEmptying` `:239-242` → "Empty" policy | target NID, bool | — | S |
| P5 | `UpgradesSection.toggle` (`UpgradesSection.cs:55`) | `OnToggle(entity)` `:77-83` → "Out of Service" on the extension or upgrade → `ExtensionFlags.Disabled` (`ModifiedSystem.cs:174-186`) | upgrade NID, bool | owner `Updated` | S |
| P6 | `TicketPriceSection.setTicketPrice` (`TicketPriceSection.cs:37`) | `OnSetTicketPrice` `:49-52` → ticket policy, `active = price>0`, clamped adjustment | route NID, float adjustment | `FreePublicTransport` trigger when set to 0 | S |
| P7 | `VehicleCountSection.setVehicleCount` (`VehicleCountSection.cs:248`) | `OnSetVehicleCount` `:270-276` converts the count into a policy **adjustment** with `CalculateVehicleCountJob.CalculateAdjustmentFromVehicleCount` (uses the route's `stableDuration`) | route NID, **float adjustment** (send the computed value, not the count) | — | S |
| P8 | `ScheduleSection.setSchedule` (`ScheduleSection.cs:37`) | `OnSetSchedule` `:50-67` → **two** `Modify` events (day policy, night policy) | route NID, schedule enum (or the 2 Modify tuples) | — | S |
| P9 | `LinesSection.toggle` (`LinesSection.cs:234`) | `OnToggle` `:315-319` → `TransportationOverviewUISystem.SetLineState` `:240-247` → Out of Service | route NID, bool | — | S |
| P10 | `transportationOverview.setActive` (`TransportationOverviewUISystem.cs:127`) | `SetLineState` `:240-247` | route NID, bool | — | S |
| P11 | `transportationOverview.setSchedule` (`:130`) | `SetLineSchedule` `:249-270` → 2 Modify events | route NID, schedule | — | S |

### 3.2 Deletions outside tools (M2)

| # | Binding | Handler | Consumers (phase) | Params | Side effects | Class |
|---|---|---|---|---|---|---|
| D1 | `ActionsSection.delete` (`:153`) | `OnDelete` `:244-258`: plays a sound, then `EndFrameBarrier.AddComponent<Deleted>(selectedEntity)`. Allowed only for District, ServiceUpgrade, TransportLine+Route (`deletable :306`) | district: `ServiceDistrictSystem` removes it from `ServiceDistrict` buffers (`Game.Areas/ServiceDistrictSystem.cs:73-86`, Mod5 `SystemOrder.cs:223`). Upgrade: `UpgradeDeletedSystem` (ToolUpdate `:711`) and `SubElementDeleteSystem` (PostTool `:725`). Line: `Game.Routes.ElementSystem` deletes waypoints and segments (`Game.Routes/ElementSystem.cs:47,60,107`; Mod2B `:132`) | target NID | **no refund, no money** (no Temp, so `ToolApplySystem` is not involved) [V: no money code in handler]; sound local | S |
| D2 | `UpgradesSection.delete` (`UpgradesSection.cs:52`) | `OnDelete(entity)` `:91-98` → `Deleted` via `EndFrameBarrier` | `UpgradeDeletedSystem` (`Game.Tools/UpgradeDeletedSystem.cs:618`, query `ServiceUpgrade+Deleted+Transform`, not Temp). It **regenerates the owner's prefab sub-nets and sub-areas that the upgrade had cleared**, as `CreationFlags.Permanent` definitions with seeds from a time-seeded `RandomSeed` (`:134,245-253,343-349`), and puts `Updated` on the owner and its sub-objects (`:157,273,324-338`) | upgrade NID | as D1 | S |
| D3 | `transportationOverview.delete` (`TransportationOverviewUISystem.cs:123`) | `DeleteLine` `:195-201` | as for lines in D1 | route NID | — | S |

### 3.3 Economy (M6/M4/M7)

| # | Binding | Handler → mechanism | Consumer (phase) | Params | Side effects | Class |
|---|---|---|---|---|---|---|
| E1 | `serviceBudget.setServiceBudget` (`ServiceBudgetUISystem.cs:113`) | `SetServiceBudget` `:251-255` → `CityServiceBudgetSystem.SetServiceBudget` (`Game.Simulation/CityServiceBudgetSystem.cs:1084-1119`): direct write to the `ServiceBudgetData` buffer on a dedicated singleton entity (created `:834`); `Updated` on the singleton via **EndFrameBarrier** (`:1118`) | `CityServiceEfficiencySystem` (query `ServiceBudgetData+Updated`, `Game.Buildings/CityServiceEfficiencySystem.cs:162`; ModificationEnd `SystemOrder.cs:292`); budget/upkeep systems | service PrefabID, int % | telemetry when the panel closes (`:293-301`) | S |
| E2 | `serviceBudget.setServiceFee` (`:114`) | `SetServiceFee` `:257-265` → static `ServiceFeeSystem.SetFee` (`Game.Simulation/ServiceFeeSystem.cs:508-520`) on the City `ServiceFee` buffer; direct, **no tag**; `Parking` ignored | `ServiceFeeSystem` and fee consumers (GameSimulation, `SystemOrder.cs:417`) | `PlayerResource`, float | — | S |
| E3 | `serviceBudget.resetService` (`:115`) | `ResetService` `:267-279` = E1(100) + E2(default fee) for each `CollectedCityServiceFeeData` of the service | as E1/E2 | service PrefabID | — | S |
| E4 | `taxation.setTaxRate` (`TaxationUISystem.cs:110`; group `"taxation"` `:36`) | `SetTaxRate` `:273-282` → `TaxSystem.TaxRate` setter (`Game.Simulation/TaxSystem.cs:219-231`; clamps and runs `EnsureAreaTaxRateLimits`) | `TaxSystem` (GameSimulation `SystemOrder.cs:485`) and readers of `GetTaxRates()` | int | telemetry | S |
| E5 | `taxation.setAreaTaxRate` (`:111`) | `SetAreaTaxRate` `:284-292` → `TaxSystem.SetTaxRate(TaxAreaType,int)` `:482-486` (stores the offset from the base rate, then clamps) | same | areaType, int | — | S |
| E6 | `taxation.setResourceTaxRate` (`:112`) | `SetResourceTaxRate` `:294-319` → `SetResidential/Commercial/Industrial/OfficeTaxRate` | same | resource idx, areaType, int | — | S |
| E7 | `loan.acceptLoanOffer` (`LoanUISystem.cs:47`) | `AcceptLoanOffer` `:73-80` → `LoanSystem.ChangeLoan(amount)` (`Game.Tools/LoanSystem.cs:130-137`, clamped by `ClampLoanAmount :139-144` against `PlayerMoney` and `Creditworthiness`) → queued `LoanAction` | `LoanSystem.OnUpdate` (Modification1, `SystemOrder.cs:104`) → `LoanActionJob` adds `PlayerMoney += new − old` and sets `Loan{m_Amount, m_LastModified=frameIndex}` (`LoanSystem.cs:33-47,107-123`) | target loan amount (int) | **money** | S |
| E8 | `loan.requestLoanOffer` / `loan.resetLoanOffer` (`:46,:48`) | UI-only offer state `m_RequestedOfferDifference` (`:66-71,82-85`) | — | — | — | L |

Notes:
- **Tax state is one managed `NativeArray<int>(92)`** (`TaxSystem.cs:193,345`), serialized by
  `TaxSystem.Serialize` (`:276-283`) [V]. The simplest correct replay is "set this rate" on the host followed by a
  broadcast of the **whole 92-int array**. The clamping rules (`EnsureAreaTaxRateLimits :488-533`) make per-field
  deltas order-dependent [I].
- Budgets and fees have no events. After host execution, broadcast the resulting `ServiceBudgetData` buffer and the
  City `ServiceFee` buffer. For the budget, add `Updated` to the singleton on clients so `CityServiceEfficiencySystem`
  reacts [V query; I recipe].

### 3.4 Progression and land

| # | Binding | Handler → mechanism | Consumer (phase) | Params | Side effects | Class |
|---|---|---|---|---|---|---|
| G1 | `devTree.purchaseNode` (`DevTreeUISystem.cs:144`) | `PurchaseNode` `:163-166` → `DevTreeSystem.Purchase(Entity)` (`Game.City/DevTreeSystem.cs:157-175`) | `points` setter **directly** writes the `DevTreePoints` singleton (M6, `:94-113`); `Unlock` event on EndFrameBarrier (M1, archetype `:122`, `:169-171`) → `UnlockSystem` (MainLoop `SystemOrder.cs:61`): `ProcessEvents` turns off `Locked` on the node prefab (`Game.Prefabs/UnlockSystem.cs:179-215`), then cascades requirements and emits new `Unlock` events (`:143-177,206-213`) | node PrefabID | dev points, unlocks (services/assets), telemetry | S |
| G2 | `mapTiles.purchaseMapTiles` (`MapTilesUISystem.cs:108`) | `PurchaseMapTiles` `:188-191` → `MapTilePurchaseSystem.PurchaseSelection` (`Game.Simulation/MapTilePurchaseSystem.cs:311-344`) | `UpdateStatus`; **direct** `PlayerMoney.Subtract(cost)`; `UnlockTile` removes `Native` and adds `Updated` via `EntityManager` (M5, `:346-353`) | list of tile NIDs (the selection) + expected cost | **money**, upkeep (`CalculateOwnedTilesUpkeep :425-428`), achievements `TheExplorer`/`EverythingTheLightTouches` (`:334-338`) | S |
| G3 | `mapTiles.setMapTileViewActive` (`:107`) | `SetMapTileViewActive` `:172-186` → `MapTilePurchaseSystem.selecting` activates `SelectionToolSystem` with `SelectionType.MapTiles` (`MapTilePurchaseSystem.cs:92-114`) | — | — | — | T/L |

Important G2 detail [V]:
- The selection lives in the `SelectionElement` buffer of the SelectionToolSystem selection entity
  (`MapTilePurchaseSystem.cs:355-367`).
- `TryGetSelections` returns false unless `selecting` is true, which requires the active tool to be the selection tool
  in MapTiles mode.
- On the host, a replay must therefore not reuse `PurchaseSelection`. The host player may be using another tool, or
  selecting tiles themselves. Reimplement it instead: validate the cost (`UpdateStatus :200-253`), subtract money, and
  call `UnlockTile` for each tile NID [I].

### 3.5 Names and cosmetics

| # | Binding | Handler → mechanism | Consumer | Params | Class |
|---|---|---|---|---|---|
| N1 | `TitleSection.renameEntity` (`TitleSection.cs:40`) | `OnRename` `:48-52` → `NameSystem.SetCustomName` (`Game.UI/NameSystem.cs:214-241`): **resolves `Controller` to the controlling entity** (`:220-223`); writes the managed `Dictionary<Entity,string> m_Names` (`:162`); adds or removes `CustomName` and adds `BatchesUpdated` via EndFrameBarrier (`:228-239`). An empty or blank name clears it | labels/render (`BatchesUpdated`); `NameSystem.OnUpdate` drops names of `Deleted` entities (`:176-197`) | entity NID (after Controller resolution), string | S |
| N2 | `transportationOverview.rename` (`TransportationOverviewUISystem.cs:126`) | `SetLineName` `:231-238` → same | same | route NID, string | S |
| N3 | `toolbarBottom.setCityName` (`ToolbarBottomUISystem.cs:77`) | `SetCityName` `:108-112` → `CityConfigurationSystem.cityName` (plain property, `Game.City/CityConfigurationSystem.cs:70`) | UI, save | string | S |
| C1 | `ColorSection.setColor` (`ColorSection.cs:32`) | `OnSetColor` `:36-54` (routes with TransportLine or WorkRoute): `SetComponent(Game.Routes.Color)` on the route and `AddComponent(Color)` on each `RouteVehicle` via EndFrameBarrier; `Event+ColorUpdated` entity (`:33,51-52`) | `MeshColorSystem` and `PreCullingSystem` (`Game.Rendering/MeshColorSystem.cs:1241`, `PreCullingSystem.cs:2448`; PreCulling `SystemOrder.cs:649`) | route NID, RGBA | S (cosmetic) |
| C2 | `transportationOverview.setColor` (`:125`) | `SetLineColor` `:211-229`, same as C1 | same | route NID, Color32 | S (cosmetic) |
| C3 | `VisualCustomizeSection.updateMeshColor / pasteColorSet / applyColor / resetToDefault` (`VisualCustomizeSection.cs:60,64-66`) | **Direct** `DynamicBuffer<CustomMeshColor>` write on the selected entity (`:85-99,161-170,243-279`); component enable and `BatchesUpdated` via EndFrameBarrier (`:101-107`). Reset disables the component and clears the buffer directly (`:255-267`) | `MeshColorSystem` (PreCulling) | entity NID, `ColorSet` (3 colours) or reset | S (cosmetic) |
| C4 | `VisualCustomizeSection.setToAll` (`:62`) | `SetToAll` `:172-217`: for **every** entity with the same prefab that has `CustomMeshColor`, writes the colour directly and enables it via ECB | same | prefab PrefabID, ColorSet (host enumerates the entities) | S (cosmetic) |
| C5 | `VisualCustomizeSection.add/remove/updateGlobalColorSet` (`:67-69`) | `MeshColorPaletteSystem.AddPalette/RemovePalette/UpdatePalette` (`Game.Rendering/MeshColorPaletteSystem.cs:72-127`): direct buffer write on a palette entity that is **saved with the city** (`PostDeserialize :28-36`) | UI | op + index + colour | S (cosmetic) or P (design choice) |
| C6 | `VisualCustomizeSection.toggleHistorical` (`:72`) | `OnToggleHistorical` `:75-83`: **direct** `SetComponentData(Building)` with `m_Flags ^= Historical`, **no tag** | `BuildingUpkeepSystem` (GameSimulation): historical buildings never level up (`BuildingUpkeepSystem.cs:239-245`) and are never abandoned (`:278-282`) | building NID, bool | **S (simulation-relevant)** |
| C7 | `VisualCustomizeSection.copyColorSet / commitMeshColor` (`:61,:63`) | clipboard (`:237-241`) / telemetry only (`:156-159`) | — | — | L |

### 3.6 Services, routes, districts

| # | Binding | Handler → mechanism | Consumer | Params | Class |
|---|---|---|---|---|---|
| R1 | `SelectVehiclesSection.selectVehicles` (`SelectVehiclesSection.cs:218`) | `SelectVehicleModel(primary, secondary)` `:222-273`: **direct** `DynamicBuffer<VehicleModel>` edit on the selected entity, **no tag** | `TransportLineSystem` (GameSimulation `SystemOrder.cs:588`), `TransportDepotAISystem`, `TransportPathfindSetup`, `Game.Routes.InitializeSystem` (all hold `BufferLookup<VehicleModel>`) | target NID, primary/secondary vehicle PrefabID (Null allowed) | S |
| R2 | `SelectVehiclesSection.deselectVehicles` (`:219`) | `DeselectVehicleModel` `:275-310`, direct, `RemoveAtSwapBack` (**order-dependent**) | same | same | S |
| R3 | `DistrictsSection.removeDistrict` (`DistrictsSection.cs:70`) | `RemoveServiceDistrict(district)` `:150-170`: **direct** `ServiceDistrict` buffer `RemoveAt` on the selected building, **no tag** | service pathfind setups and `ServiceCoverageSystem` (all read `ServiceDistrict`) | building NID, district NID | S |
| R4 | `DistrictsSection.toggleSelectionTool` (`:71`) | `:172-182` → `SelectionToolSystem` with `SelectionType.ServiceDistrict`, owner = building. The **tool** then writes `ServiceDistrict` directly (§7.7) | — | — | T (leads to S) |
| R5 | `DistrictsSection.toggleDistrictTool` (`:72`) | `:184-194` → `AreaToolSystem` with the default district prefab (Temp/Apply) | — | — | T |
| R6 | `DistrictsSection.disableTool` (`:73`) | default tool | — | — | T |

### 3.7 Time and global simulation controls

| # | Binding | Mechanism | Params | Class |
|---|---|---|---|---|
| T1 | `time.setSimulationPaused` / `time.setSimulationSpeed` (`TimeUISystem.cs:94-95`) | `SimulationSystem.selectedSpeed` (M6), `IndexToSpeed` = 2^clamp(i,0,2) (`:205-231`). While the "simulationPausedBarrier" event binding has observers, the change is deferred (`:207-225`) | bool / speed index | **S (host only)** |
| T2 | (no trigger) `TimeUISystem.OnUpdate` | forces `selectedSpeed = 0` when the app loses focus (`AppState.Constrained`, `:99-110`) or any UI observes the pause barrier, and restores it afterwards (`:115-137`) | — | **danger: must be disabled or overridden on the host and on clients** [I] |
| T3 | photo mode widget "Simulation Speed" (`PhotoModeRenderSystem.cs:765-780`, set through `WidgetBindings("photoMode")`, `PhotoModeUISystem.cs:118-119`) | `selectedSpeed` in the range 0–8 | float | S (block on clients) |
| T4 | photo mode "Time of Day" (`PhotoModeRenderSystem.cs:745-763`) | `PlanetarySystem.time/overrideTime` (visual sun position) | — | L |
| T5 | `tool.setWaterSimSpeed` (`ToolUISystem.cs:255-258`), `tool.setSimulateBackdropWater` (`:265-268`) | `WaterSystem.WaterSimSpeed`, `WaterSystem.simulateBackdrop` | int / bool | S if reachable in game (see Q6) |

Note T5 [V]: `ToolUISystem` registers **two** triggers named `tool.setWaterSimSpeed`. The first, at `:165`, sets a local
property (`:417-420`). The second, at `:255`, sets `WaterSystem.WaterSimSpeed`. Which one the binding layer keeps is
not visible in this code.

### 3.8 Follow, chirps, tutorials (per-player meta stored in the shared world)

| # | Binding | Mechanism | Notes | Class |
|---|---|---|---|---|
| F1 | `ActionsSection.follow` (`ActionsSection.cs:152`, `:279-291`); `lifePath.followCitizen` / `lifePath.unfollowCitizen` (`LifePathUISystem.cs:85-86`) | `LifePathEventSystem.FollowCitizen/UnfollowCitizen` (`Game.Triggers/LifePathEventSystem.cs:198-230`): **direct** `AddComponentData(Followed{m_Priority = frameIndex})` + `AddBuffer<LifePathEntry>`; unfollow destroys the entries | `Followed` is serialized (`Game.Citizens/Followed.cs`, `ISerializable`). The global limit `kMaxFollowed = 50` (`:132`) is **shared** across players. Consumers: `LifePathEventSystem` (ModificationEnd `SystemOrder.cs:279`), `AchievementTriggerSystem` | P (in a shared save; see Q3) |
| F2 | `chirper.addLike` / `chirper.removeLike` (`ChirperUISystem.cs:123-124`) | `Chirp.m_Flags ^ Liked` + `Updated` via EndFrameBarrier (`:141-157`) | only the UI reads `Liked` (`:237`) [V: grep] | P |
| F3 | `tutorials.*` (`TutorialsUISystem.cs:155-163`), `tutorials.completeIntro` etc. (`GameTutorialsUISystem.cs:27-29`), `menu.applyTutorialSettings` (`Game.UI.Menu/MenuUISystem.cs:487,607-614`) | `TutorialSystem.SetTutorial/ForceTutorial/CompleteCurrentTutorialPhase/CompleteTutorial/mode/tutorialEnabled/autoplayTutorials` (`TutorialsUISystem.cs:553-604`), `SharedSettings` | the tutorial system unlocks **tutorial prefabs** with `Unlock` events (`Game.Tutorials/TutorialSystem.cs:256-262`). These are generic `Unlock` events, so unlock replication must ignore tutorial prefabs [I] | P |

---

## 4. Verification: are the modifier buffers recomputed from `Policy`?

[V] All four `Refresh*` helper families are called from exactly these places (grep of `Refresh*Modifiers` and
`Refresh*Options`):

| Buffer / field | Recomputed by | Trigger | Phase |
|---|---|---|---|
| `District.m_OptionMask`, `DistrictModifier` | `ModifiedSystem.RefreshEffects` (`Game.Policies/ModifiedSystem.cs:252-262`) | `Modify` event on that district | Mod4 |
| | `DistrictModifierInitializeSystem` (`Game.Policies/DistrictModifierInitializeSystem.cs:37-51,181`) | `Created` District, not Temp, **only if `policies.Length != 0`** | Mod4 (`SystemOrder.cs:143`) |
| `Building.m_OptionMask`, `BuildingModifier` | `ModifiedSystem` (`:263-273`) | `Modify` | Mod4 |
| | `BuildingModifierInitializeSystem` (`:43-48,181`) | `Created` Building, not Temp | Mod4 (`:144`) |
| `Route.m_OptionMask`, `RouteModifier` | `ModifiedSystem` (`:274-284`) | `Modify` | Mod4 |
| | `RouteModifierInitializeSystem` (`:36-50,203`) | `Created` Route with `Policy`, not Temp | Mod4 (`:145`) |
| `City.m_OptionMask`, `CityModifier` | `ModifiedSystem` (`:285-297`) | `Modify` | Mod4 |
| | **`CityModifierUpdateSystem`** (`Game.Simulation/CityModifierUpdateSystem.cs:105-205,269-297`) | **every 256 frames** (`GetUpdateInterval = 256`, `:269-271`), unconditionally for the City | GameSimulation (`SystemOrder.cs:591`) |

Details [V]:
- Each `RefreshXModifiers` **clears** the buffer and rebuilds it from active policies. Slider policies interpolate
  `m_Range` by the normalised adjustment (for example `DistrictModifierInitializeSystem.cs:96-126`).
- `RefreshCityModifiers` additionally adds every `CityEffectProvider` building that is not `Deleted`, `Destroyed` or
  `Temp`. Non-signature buildings contribute `lerp(range, efficiency)`; signature buildings contribute `range.max`.
  Installed upgrades are included (`CityModifierUpdateSystem.cs:149-204`; query in `ModifiedSystem.cs:397`).
  **`CityModifier` therefore depends on simulation state** (building efficiency).
- `DefaultPoliciesSystem` (Mod3, `SystemOrder.cs:135`) **appends** prefab default policies to the `Policy` buffer of
  every `Created` (non-Temp) entity that has `Policy` (`Game.Policies/DefaultPoliciesSystem.cs:31-58,106`). On
  `NewGame` (or an old save) it also appends city defaults (`:124-150`).

Consequences [I]:
1. Replicating policy **changes** as `Modify` events, replayed on every machine before Mod4, keeps
   District/Building/Route modifiers exact. Direct `Policy` buffer writes on existing entities would leave stale
   modifiers until the entity is re-created.
2. For **remote creation** via direct replication (02 §9): if a client creates a building with the host's `Policy`
   buffer already filled and `Created` set, `DefaultPoliciesSystem` will **append the defaults a second time**. Create
   with an **empty** `Policy` buffer, let Mod3 add defaults and Mod4 compute modifiers, then reconcile with the host's
   buffer through `Modify` events. Alternatively, write the final buffer after Modification3.
3. `CityModifier` must be either replicated as state or allowed to converge from replicated `Policy` + replicated
   building efficiency within 256 frames. If clients do not run the economy simulation (report 03), replicate it.
4. Report 04 lists `DistrictModifier` as "derived but saved". That is confirmed: it is never recomputed on load. Only
   `Created`, `Modify` and (for the City only) the 256-frame update recompute it.

---

## 5. Replay recipes per action family

General rule [I]: the client never mutates shared state for these actions. It sends an **intent**; the host executes
the vanilla API; the host broadcasts the **resulting state**, or a deterministic event where that is exact.

| Family | Client interception (prefix, skip original) | Host execution | Broadcast to clients | Client apply |
|---|---|---|---|---|
| **Policies** (P1–P11) | single choke point `PoliciesUISystem.ModifyPolicy` (`:237`), or the three public `SetPolicy/SetCityPolicy/SetSelectedInfoPolicy` overloads (`:186-235`). Send `(targetNID, policyPrefabID, active, adjustment)`. Per-player achievement progress stays local | call `PoliciesUISystem.SetPolicy(target, policy, active, adj)` **from UIUpdate or after `AllowBarrier<EndFrameBarrier>`** (§0.4) with a "replaying" guard flag | the same tuple, plus the resulting `Policy` buffer as a checksum | inject `Event+Modify` (archetype as `PoliciesUISystem.cs:157`) before Mod4, e.g. via EndFrameBarrier in UIUpdate; verify the buffer afterwards. Suppress local `TriggerAction`s if chirps and notifications are replicated separately (§7.8) |
| **UI deletions** (D1–D3) | prefix `ActionsSection.OnDelete`, `UpgradesSection.OnDelete`, `TransportationOverviewUISystem.DeleteLine` | add `Deleted` to the host entity via EndFrameBarrier (same timing as vanilla) | the normal entity-deletion replication (02 §9.1 item 4). **Do not let clients run `UpgradeDeletedSystem` for replicated upgrade deletions**; replicate the host's regenerated sub-nets and sub-areas instead (they come from time-seeded randomness, `UpgradeDeletedSystem.cs:134`) | standard |
| **Taxes** (E4–E6) | prefix the `TaxSystem.TaxRate` setter, `SetTaxRate`, `Set*TaxRate`, or the 3 UI handlers | call the same setter (main thread, after `m_TaxSystem.Readers.Complete()`, as in `TaxationUISystem.cs:275`) | full `m_TaxRates[92]` (via `GetTaxRates()` `:365`) | overwrite the array (main thread, after `Readers.Complete()`) |
| **Budget/fees** (E1–E3) | prefix `CityServiceBudgetSystem.SetServiceBudget`, `ServiceFeeSystem.SetFee` | same calls | `ServiceBudgetData` buffer + City `ServiceFee` buffer | overwrite; add `Updated` on the budget singleton |
| **Loan** (E7) | prefix `LoanSystem.ChangeLoan` | `ChangeLoan(amount)` (it re-clamps against host money) | `Loan` component + `PlayerMoney` (money is host-owned anyway) | overwrite |
| **Dev tree** (G1) | prefix `DevTreeSystem.Purchase(Entity)` | `Purchase(node)` (validates points, lock state and requirements, `:165`) | `DevTreePoints` + list of newly unlocked prefab IDs (or the full unlocked set) | set `DevTreePoints`; inject `Unlock` events (or `SetComponentEnabled<Locked>(false)`) so `UnlockSystem` cascades locally (`UnlockSystem.cs:143-215`) |
| **Map tiles** (G2) | prefix `MapTilePurchaseSystem.PurchaseSelection`; read the selection buffer and send tile NIDs | reimplemented purchase (cost check, money, `UnlockTile`), **not** `PurchaseSelection` (§3.4) | tile `Native` removal (entity replication) + money | standard; clear the local selection |
| **Names** (N1–N3) | prefix `NameSystem.SetCustomName` and the `CityConfigurationSystem.cityName` setter | same calls (resolve the Controller on the host) | `(NID, name)`; city name | call `SetCustomName` locally (needs EndFrameBarrier timing) or write `m_Names` + `CustomName` |
| **Route colour** (C1–C2) | prefix the handlers | same | `(routeNID, Color)` | `SetComponent(Color)` on the route and its vehicles + `Event+ColorUpdated` |
| **Mesh colour** (C3–C5) | prefix the handlers | same | `(NID, ColorSet or reset)`; palette buffer | write `CustomMeshColor`, enable, add `BatchesUpdated` |
| **Historical** (C6) | prefix `OnToggleHistorical` | direct flag write | `Building.m_Flags` (Historical bit) | write; no tag needed |
| **Vehicle models** (R1–R2) | prefix the handlers | same | **the full `VehicleModel` buffer** (avoids `RemoveAtSwapBack` order divergence) | overwrite the buffer |
| **Service districts** (R3, R4 tool) | prefix `RemoveServiceDistrict`; for the tool, see §7.7 | apply the list | **the full `ServiceDistrict` buffer** of the building | overwrite + `Updated` on the building (as the tool does, `SelectionToolSystem.cs:421`) |
| **Sim speed** (T1, T3) | prefix `TimeUISystem.SetSimulationPaused/SetSimulationSpeed`; block photo-mode speed on clients; replace the `TimeUISystem.OnUpdate` forced pause | host sets `selectedSpeed` (or a session vote policy) | speed | clients follow the host tick stream (report 03) |
| **Follow** (F1) | prefix `LifePathEventSystem.FollowCitizen/UnfollowCitizen` | host applies (shared 50 limit) | `Followed` component | standard. Alternative: keep it per-player (Q3) |
| **Chirp likes, tutorials** | none (allow locally) | — | — | stay local; stop them leaking into replication (exclude the `Liked` bit and tutorial `Locked` state from hashes) |

Two cross-cutting rules [I]:
- **Timing on the host.** Replayed UI commands that internally call `EndFrameBarrier.CreateCommandBuffer()` (policies,
  names, colours, deletions, budget, dev tree) must run in `UIUpdate` or in a system ordered after
  `AllowBarrier<EndFrameBarrier>` in MainLoop (`SystemOrder.cs:62`). Otherwise `SafeCommandBufferSystem` throws
  (`Game/SafeCommandBufferSystem.cs:15-22`; `EndFrameBarrier : SafeCommandBufferSystem`,
  `Game.Common/EndFrameBarrier.cs:8`) [V].
- **Selected-entity handlers.** Many section handlers use `selectedEntity` implicitly (`InfoSectionBase.cs:44`). The
  host must call the explicit-target overloads (`PoliciesUISystem.SetPolicy(target,…)`) or a reimplementation. It must
  never change the host player's selection.

---

## 6. Local-only list

### 6.1 Presentation and camera (L) [V registrations]
- `camera.focusEntity` (`CameraUISystem.cs:21`).
- `cinematicCamera.*` — 20 bindings, including save/load/delete of local sequence files (`CinematicCameraUISystem.cs:100-133`).
- `photoMode.*` (`PhotoModeUISystem.cs:118-130`) **except** the "Simulation Speed" widget (T3).
- `radio.*` (`RadioUISystem.cs:88-96`).
- `infoviews.setActiveInfoview/setInfomodeActive/setZoneAlpha` (`InfoviewsUISystem.cs:131-133`).
- `statistics.*` (`StatisticsUISystem.cs:284-288`).
- `game.togglePanel/showPanel/…` (`GamePanelUISystem.cs:97-223`), `game.setActiveScreen` (`GameScreenUISystem.cs:55`).
- `eventJournal.openJournal/closeJournal` (`EventJournalUISystem.cs:44-48`).
- `selectedInfo.selectEntity/clearSelection/setSelectedRoute` (`SelectedInfoUISystem.cs:178-180`).
- `ActionsSection.focus` (`:150`); `ActionsSection.toggleTrafficRoutes` (`:157`, `TrafficRoutesSystem.routesVisible`).
- `UpgradesSection.focus` (`:54`).
- `transportationOverview.select/showLine/hideLine/resetVisibility/toggleHighlight/setSelectedPassengerType/setSelectedCargoType`
  (`:124,128-129,131-134`). Note: `HiddenRoute` is `IEmptySerializable` (`Game.Routes/HiddenRoute.cs`), so the host's
  hidden lines go into the save. Exclude it from sync and from hashes [V/I]. `Highlighted` is not serialized
  (`Game.Tools/Highlighted.cs`).
- `chirper.selectLink` (`ChirperUISystem.cs:125,159-170`); `notification.selectNotification` (`Game.UI.Menu/NotificationUISystem.cs:146`).
- `milestone.clearUnlockedMilestone` (`MilestoneUISystem.cs:292,501-511`).
- `signatureBuildings.removeUnlockedSignature` (`SignatureBuildingUISystem.cs:40,86-93`).
- `production.selectResource` (`ProductionCompanyUISystem.cs:207,361-371`).
- `loan.requestLoanOffer/resetLoanOffer`; `mapTiles.setMapTileViewActive`.
- `tool.setContourMode` (`UndergroundViewSystem.globalContourLinesOn`, `ToolUISystem.cs:564-567`).
- `app.*`, `audio.playSound`, `input.*` (`Game.UI/AppBindings.cs:200-211`, `AudioBindings.cs:15`,
  `InputBindings.cs:44-45`, `InputActionBindings.cs:556`, `InputHintBindings.cs:194`).

### 6.2 Tool state (T, local until applied) [V]
- `tool.selectTool/selectToolMode/setSelectedSnapMask/elevationUp/elevationDown/elevationScroll/setElevationStep/toggleParallelMode/setParallelOffset/setUndergroundMode/setDistance/selectBrush/setBrushHeight/Size/Strength/Angle/setColor/setParentMesh*/setGroupIndex*/setProbability*`
  (`ToolUISystem.cs:168-253`, handlers `:316-675`).
- `toolbar.*` (`ToolbarUISystem.cs:167-177`; `setAgeMask` and `setDecorationMode` write `ObjectToolSystem`, `:590-600`).
- `upgradeMenu.selectUpgrade/clearUpgradeSelection` (`UpgradeMenuUISystem.cs:132-133,379-392`). Also removes the
  `UIHighlight` "new" badge from the prefab, which is per-player.
- `ActionsSection.toggleMove` (`:151,265-277`, `ObjectToolSystem.StartMoving`), `ActionsSection.toggleLotTool`
  (`:156,194-206`), `UpgradesSection.relocate` (`:53,85-89`).
- `DestroyedBuildingSection.toggleRebuild` (`DestroyedBuildingSection.cs:68,85-94`; `UpgradeToolSystem` with
  `prefab = null`).
- `DistrictsSection.toggleSelectionTool/toggleDistrictTool/disableTool`.

### 6.3 Settings and menu [V]
- `Game.Settings.*` is per-user, but some settings matter:
  - `GameplaySettings.pausedAfterLoading` sets `selectedSpeed` on load (`SimulationSystem.cs:213`). Override it for
    joiners.
  - `dayNightVisual` and `snowVisual` are visual only (`PlanetarySystem.cs:422`, `SnowSystem.cs:204`,
    `BatchDataSystem.cs:2387`).
  - `GeneralSettings.autoSave*` makes each machine save locally. Recommend disabling auto-save on clients [I].
- `menu.saveGame/quicksave` (`MenuUISystem.cs:477,481,886-979`) produce local files. Allow them, but label a client
  save as a snapshot [I].
- `menu.loadGame/quickload/continueGame/newGame/startEditor/exitToMainMenu` (`:474-485`) are **session control**:
  - client → "leave session";
  - host → "end session" (or a resync of everyone if the host loads another save) [I].
- `Game.Debug.DebugSystem` and `DebugUISystem` hold many direct city mutations (for example LHT, `DebugSystem.cs:4805`).
  Block them on clients or route them through the host [I].

---

## 7. Part B — Intercepting tool apply on clients

### 7.1 Verified anatomy of an apply frame

`ToolSystem.OnUpdate`: `PreTool` → `ToolUpdate()` → `PostTool` (`Game.Tools/ToolSystem.cs:250-261`). Inside
`ToolUpdate()`:
- When `activeTool` changed, the previous tool is disabled and updated once more; the new tool becomes `m_LastTool`
  (`:312-320`).
- Then `Update(ToolUpdate)` runs (`:327`).
- **`ToolSystem.applyMode` returns `m_LastTool.applyMode`** (`:147-157`). `ToolBaseSystem.applyMode` has a
  **protected setter** (`ToolBaseSystem.cs:182`).

ToolUpdate phase order (`SystemOrder.cs:694-711`):

```
[Before] AllowBarrier<ToolOutputBarrier>
[At]     Area/Bulldoze/Default/Net/Object/Route/Selection/Upgrade/Zone/Terrain/Water ToolSystem   (only m_LastTool enabled)
         UpgradeDeletedSystem
[After]  ToolOutputSystem  ──► applyMode==Clear: Update(ClearTool) = ToolClearSystem
                              applyMode==Apply: Update(ApplyTool) = ToolApplySystem, ApplyZones, ApplyObjects,
                                                ApplyNet, ApplyNotifications, ApplyAreas, ApplyBrushes,
                                                ApplyRoutes, ApplyWaterSources        (ToolOutputSystem.cs:22-30; SystemOrder.cs:712-721)
         ToolOutputBarrier  (playback of everything above)
```

On the click frame, the tool sets `applyMode = Apply`. In the same update it queues `DestroyDefinitions` and the
definitions for the next preview into `ToolOutputBarrier` (example: `NetToolSystem.Apply`, `NetToolSystem.cs:6688-6731`;
02 §2.3). **Those queued commands play back only after the ApplyTool phase** [V from ordering]. So at the start of
`ApplyTool`:
- (i) every Temp entity is exactly the validated preview of the previous frame. Apply is only chosen when
  `GetAllowApply()` is true, which means no `Error` entities and no deleted originals (`ToolBaseSystem.cs:533-540`);
- (ii) the `CreationDefinition` entities that produced those Temps still exist (no `Updated`; 02 §2.2);
- (iii) nothing has been committed yet.

`ToolApplySystem` subtracts Σ`Temp.m_Cost` (excluding `TempFlags.Cancel`) for **all** Temps in the world
(`ToolApplySystem.cs:42-74,118-126`). With unlimited money, `PlayerMoney.money` only reports 2e9; `Subtract` still
changes the internal value (`Game.City/PlayerMoney.cs:15-40`).

There is **no persistent undo**. The only undo actions are in-progress ones: "Undo Net Control Point"
(`NetToolSystem.cs:5532`, `ObjectToolSystem.cs:2649`), "Undo Area Node" (`AreaToolSystem.cs:2715`) and
"Undo Waypoint" (`RouteToolSystem.cs:1099`). They edit control points before apply. Interception cannot break an undo
list [V].

### 7.2 Candidate interception points

| Id | Where | Registration | Can capture | Can suppress | Verdict |
|---|---|---|---|---|---|
| IP1 | ToolUpdate, immediately before `ToolOutputSystem` | `UpdateBefore<Gate, ToolOutputSystem>(SystemUpdatePhase.ToolUpdate)` (two-type overload, 02 §1.2) | knows `applyMode` and the active tool; Temps visible | **yes**: rewrite `m_ToolSystem.activeTool.applyMode` (protected setter, via reflection or a Harmony accessor; third-party precedent of a tool exposing it: `thirdparty/CS2-MoveIt/Code/MoveIt/Tool/Definitions.cs:118`) to `Clear` (or `None`) | **gate** for "no-predict" mode |
| IP2 | first system of ApplyTool | `UpdateBefore<CaptureTemps>(SystemUpdatePhase.ApplyTool)` (mods' UpdateBefore run before vanilla UpdateAt, 02 §1.2) | **complete pre-apply Temp set + definitions**; runs only on apply frames | no (the phase is already chosen; `TempFlags.Cancel` is honoured only by Objects, Net, Water and ToolApply: `ApplyObjectsSystem.cs:76,337`, `ApplyNetSystem.cs:156,484`, `ApplyWaterSourcesSystem.cs:47`, but **not** by zones, areas or routes) | **capture A** |
| IP3 | after `ToolOutputBarrier` playback, before Modification1 | `UpdateBefore<CaptureResult>(SystemUpdatePhase.Modification1)` (precedent: TreeController registers before Modification1, 02 §10.1) | **exact committed change-set**: entities with `Applied`+`Created` (new), `Applied`+`Updated` (changed originals), `Deleted` originals (from capture A's `TempFlags.Delete/Replace/Combine`), plus PostTool's `SubElementDeleteSystem` effects | no (already committed) → rollback only | **capture B** |
| IP4 | Harmony prefix on each tool's `Apply` | per tool | partial | yes, but breaks state machines | rejected |
| IP5 | Harmony prefix on `ToolApplySystem`/`Apply*` `OnUpdate` | — | — | yes, but Temps stay hidden or leak | rejected |

Why capture B happens before Modification1 [V]: `Applied` is also added later by derived systems that promote Temp
children (`LaneSystem.cs:9275`, `SubObjectSystem.cs:3975`, `SecondaryObjectSystem.cs:4096`,
`SecondaryLaneSystem.cs:1911`, `AlignSystem.cs:928`, `RoadConnectionSystem.cs:2223`, `AreaConnectionSystem.cs:902`).
Before Modification1, the `Applied` set contains only the primary Apply* outputs and `SubElementDeleteSystem`'s
(`SubElementDeleteSystem.cs:329`). Those are exactly the first-class entities that 02 §9.1 item 5 says must be
replicated. Regenerated children are rebuilt on every machine.

### 7.3 Recommended design: predict, capture, host commits the change-set, reconcile

**Client (acting player)**
1. **IP2, capture A.** Build `ToolActionHeader`:
   - client sequence number;
   - `toolID` and prefab;
   - for every Temp: `Temp.m_Original` → `(NID, revision)`, plus `TempFlags`;
   - Σcost computed exactly as `ToolApplySystem` does (`:59-66`);
   - optionally the definition entities (02 §3.1), with `CreationFlags.Permanent` ones filtered out because they are
     simulation leftovers (02 §3.4) [I].

   Read on the main thread. Temp components are not written by the tool's pending jobs, but completing the relevant
   dependencies is cheap [I].
2. **Let vanilla apply run (prediction).** The player gets exact vanilla behaviour: splits, upgrades, auto-connected
   sub-nets, sounds, and the tool state machine continuing from real entities. That last point matters: continuous
   road drawing keeps the last control point (`NetToolSystem.cs:6695-6721`). Local money is deducted as a prediction
   and later overwritten by the host's `PlayerMoney`.
3. **IP3, capture B.** Collect the change-set:
   - for created and updated entities, the source components from the 02 §4/§9.1 catalogue: `PrefabRef`,
     `Transform`/`Node`/`Edge`/`Curve`, `Elevation`, `Owner`, `Attached`, `Upgraded`, `PseudoRandomSeed`, `BuildOrder`,
     zone `Cell` buffers, area `Node` buffers, route waypoints, and so on;
   - deleted originals as NIDs.

   Tag new local entities with a mod component `Predicted{seq}`. Send `ToolActionCommand{header, changeSet}`.
4. **Client-side suppression of host-owned side effects** of the local `Created`: XP (`XPBuiltSystem`, `NetXPSystem`;
   02 §9.1 item 7), milestones and achievements. Money is overwritten anyway [I].

**Host**
1. **Validate**:
   - permission or role;
   - each touched original still exists with the same revision (otherwise conflict → reject);
   - prefabs unlocked;
   - Σcost ≤ money (unless unlimited);
   - optionally build area / owned tiles;
   - mod-parity rules (for example Anarchy removes `Error`s in Mod5/ModEnd,
     `thirdparty/Anarchy/Anarchy/AnarchyMod.cs:133-134`, so validation differs if mod sets differ).
2. **Commit** with the same direct-write applier that clients use for host broadcasts (02 §9). Run it at MainLoop
   start, before `ToolSystem`:
   - create with prefab archetypes;
   - write source components;
   - add `Created/Updated/Deleted` + **`Applied`** + the topological closure (02 §7);
   - subtract Σcost from `PlayerMoney`;
   - set `Recent{hostFrame, cost}` (`Game.Tools/Recent.cs`), as Apply would (02 §4.2) [I];
   - queue the `TriggerAction`s Apply would have produced (for example `ObjectCreated`, 02 §4.2) through
     `TriggerSystem.CreateActionBuffer()` (pattern in `ModifiedSystem.cs:412,441-448`) [I].
3. **Host player's tool is not disturbed.** The host never touches the Temp namespace, `applyMode`, or
   `ToolClearSystem` for remote actions. If the host's preview referenced an original that the remote action deleted,
   `OriginalDeletedSystem` (PreTool) forces a rebuild and blocks apply for that frame
   (`OriginalDeletedSystem.cs:33-45`; `ToolBaseSystem.cs:533-559`). This is the mechanism vanilla built for exactly
   this case [V].
4. Assign NIDs. Broadcast the authoritative change-set plus `{seq: predictedLocalId → NID}` to all.

**Originator reconcile**
- *Accepted*: bind each `Predicted` entity to its NID. Apply the authoritative components only if they differ (they
  normally do not, because the data came from the client). Remove `Predicted`.
- *Rejected or diverged*: destroy the predicted creations. Ask for (or receive) the authoritative state of every
  touched original and recreate or restore them through the normal replication path. Deleted originals still exist on
  the host, so they come back with their original NIDs and new local identities.

**Other clients**: apply the broadcast change-set like any host change.

### 7.4 Optional "no-predict" mode via IP1

When a client must not predict (spectator role, permission denied, a local hash mismatch, or a host-configured
"authoritative wait"), the gate at IP1 rewrites `applyMode` from `Apply` to `Clear`:
- `ToolClearSystem` then marks **all** Temps `Deleted` and un-hides originals (`ToolClearSystem.cs:93-107,218`). This
  is the same cleanup that runs on an ordinary mouse move, so validation and Temp lifecycles stay consistent [V].
- Capture A must then run in the gate itself, since ApplyTool will not run. The Temps are the same set.

Risks [I]:
- The tool's state machine has already advanced as if the apply happened (for example NetToolSystem clears its control
  points and keeps the last one as the next start, `NetToolSystem.cs:6695-6721`). Until the host result arrives, the
  next segment starts at a position without a node. If the player continues drawing before then, the host may receive
  a course that does not connect to the previous one. Fix: block further applies until the result arrives, or keep
  prediction on.
- `Apply → None` is **not** a safe alternative: the tool has already queued `DestroyDefinitions` plus new definitions,
  so Temps would be left in an undefined state.

### 7.5 Definition replay on the host (fallback only)

Report 02 §10.2 already lists the conflicts. They are confirmed here:
- `ToolClearSystem` clears all Temps on any `Clear` frame (`ToolClearSystem.cs:218`).
- Apply* systems commit all Temps.
- Errors from foreign Temps block `GetAllowApply` (`ToolBaseSystem.cs:533-540`).
- `ToolApplySystem` charges for all Temps.
- `UpgradeDeletedSystem` and Generate use time-seeded randomness.

Use replay only when the host's `activeTool` is `DefaultToolSystem` **and** no Temp exists. Even then, verify the
result against the client's capture B [I]. The definitions already carry the per-tool choices (§7.6), so replay needs
no extra tool state.

### 7.6 Per-tool state (verified names)

Tool IDs and classes:
`"Net Tool"` `NetToolSystem`, `"Object Tool"` `ObjectToolSystem`, `"Zone Tool"` `ZoneToolSystem`,
`"Area Tool"` `AreaToolSystem`, `"Route Tool"` `RouteToolSystem`, `"Bulldoze Tool"` `BulldozeToolSystem`,
`"Terrain Tool"` `TerrainToolSystem`, `"Upgrade Tool"` `UpgradeToolSystem`, `"Water Tool"` `WaterToolSystem`,
plus `SelectionToolSystem` and `DefaultToolSystem` (`SystemOrder.cs:700-710`; `toolID` at
`NetToolSystem.cs:5175`, `ObjectToolSystem.cs:2317`, `ZoneToolSystem.cs:396`, `AreaToolSystem.cs:2616`,
`RouteToolSystem.cs:1034`, `BulldozeToolSystem.cs:1302`, `TerrainToolSystem.cs:195`, `UpgradeToolSystem.cs:66`,
`WaterToolSystem.cs:265`) [V].

| Tool | State (file:line) [V] | Baked into definitions? |
|---|---|---|
| base | `selectedSnap`, `brushType/brushSize/brushAngle/brushStrength`, `color`, `requireUnderground`, `applyMode` (`ToolBaseSystem.cs:146-186`) | snap results are baked into positions and `CoursePos` [I] |
| Net | `Mode{Straight, SimpleCurve, ComplexCurve, Continuous, Grid, Replace, Point}` (`:37`); `mode`/`actualMode` (`:5179-5196`), `placementColor` (`:5230`), `elevation`, `elevationStep` (`:5246-5262`), `parallelCount`/`parallelOffset` (`:5278-5307`), `underground` (`:5324`), `selectedSnap` (`:5357`), `prefab`, `lane` (`:5374,5421`), `upgradeOnly/allowParallel/allowGrid/allowReplace/serviceUpgrade` (`:5448-5456`), `m_AppliedUpgrade`, `m_RandomSeed = RandomSeed.Next()` on apply (`:6618,6691`) | `NetCourse` (elevation, curve, `CoursePos` flags), `Upgraded`/`SubReplacement` on definitions, `m_RandomSeed` (02 §3.1) |
| Object | `Mode{Create, Upgrade, Move, Brush, Stamp, Line, Curve}` (`:35`); `mode`/`actualMode`/`isUpgradeMode` (`:2321-2356`), `ageMask` (`:2375`), `decorationMode` (`:2393`), `prefab`, `transform` (`:2396,2444`), `selectedSnap` (`:2471`), `distance` (`:2487`), override fields (`:2505-2515`), `underground` (`:2517`), `state` (`:2535`); `StartMoving(entity)` target | `ObjectDefinition` (pos/rot/elevation/age/etc.), `CreationDefinition` flags `Upgrade/Relocate/…`, `m_Owner` |
| Zone | `Mode{FloodFill, Marquee, Paint}` (`:28`), `prefab` (`:402`), `overwrite` (`:418`) | **yes**: `Zoning.m_Flags` = `FloodFill/Marquee/Paint` + `Zone/Dezone` + `Overwrite` (`ZoneToolSystem.cs:233-275`; `Game.Tools/ZoningFlags.cs`) [V] |
| Area | `Mode{Edit, Generate}` (`:31`), `state`, `recreate` (`:2638`), `underground` (`:2640`), `prefab` (`:2648`) | `Areas.Node` buffer + flags |
| Route | `prefab` (`:1036`), `state`, `moveStartPosition` (`:1057`), `underground` (`:1061`) | `WaypointDefinition` |
| Bulldoze | `Mode{MainElements, SubElements, Everything}` (`:30`), `underground`, `allowManipulation`, `debugBypassBulldozeConfirmation`, `prefab` (`:1308-1328`); confirmation via `EventConfirmationRequested` (`:1268`, handled by `ToolUISystem.cs:123-124`) | `CreationFlags.Delete` definitions |
| Terrain | `prefab` (`:197`), `brushSize/brushStrength` (`:201-217`), `brushHeight` (`:253`) | `BrushDefinition` |
| Upgrade | `prefab` (`:68`; `null` = rebuild) | `Upgrade/Repair` flags |
| Water | `Mode{EditSource, AddSource}` (`:43`), `attribute`, `showWaterSourceNames` (`:267-271`) | `WaterSourceDefinition` |
| Selection | `selectionType`, `selectionOwner`, `requestSelectionUpdate` (used in `DistrictsSection.cs:165-181`, `MapTilePurchaseSystem.cs:103-106`) | n/a (no Apply, §7.7) |

**With the recommended change-set design the host needs none of this state.** The results already contain the
outcome of every choice: snapping, elevation, upgrade mode, zoning mode, route type. This state matters only for
definition replay, where it is also mostly baked in, or for UI mirroring (showing other players' cursors and tools,
optional) [I].

### 7.7 Tool mutations that bypass Apply (must be handled separately)

1. **`SelectionToolSystem` with service districts** [V]:
   - The tool never reports `Apply`. Every branch sets `Clear` or `None` (`SelectionToolSystem.cs:813-947`).
   - On a click it runs `UpdateServiceDistrictsJob`, which **writes the owner's `ServiceDistrict` buffer directly** and
     adds `Updated` to the owner via `ToolOutputBarrier` (`:390-423,1045-1060`).
   - In MapTiles mode during a game, it only edits the selection buffer (`:1034-1039`); in the editor it edits start
     tiles (`:303-341`).

   Capture: a mod system in Modification1 (or IP3) that finds buildings with `ServiceDistrict` + `Updated` while the
   active tool is `SelectionToolSystem` with `selectionType == ServiceDistrict`. Send the **full buffer**. In no-predict
   mode, restore it locally [I].
2. **Terrain brushes** [V]:
   - `TerrainToolSystem` returns `Apply` on **every frame** while the brush is held (`TerrainToolSystem.cs:544-645`).
   - `ApplyBrushesSystem` calls `TerrainSystem.ApplyBrush(type, bounds, brush, texture)` and
     `TerrainMaterialSystem.ApplyBrush` directly, then tags the Brush Temps `Applied+Deleted`
     (`ApplyBrushesSystem.cs:239-312`). This is a GPU heightmap change, not entity state.

   Capture A at IP2 per frame: the `Brush` component + prefab of each `Temp+Brush` entity. Stream it as a "stroke" to
   the host, which replays it through `TerrainSystem.ApplyBrush`. The authoritative heightmap is synced as in report 07
   [I].
3. **`UpgradeDeletedSystem`** (ToolUpdate) reacts to **any** `ServiceUpgrade+Deleted`, including replicated ones. It
   creates Permanent definitions with time-seeded seeds (`UpgradeDeletedSystem.cs:134,245-253,343-349`). Disable it on
   clients, or filter it to locally originated deletions. Otherwise clients regenerate duplicate sub-nets and sub-areas
   next to the host's replicated ones [I].
4. **`DefaultToolSystem` dragging** of `Moving` objects and markers writes `Transform` via `ToolOutputBarrier` and
   applies (`DefaultToolSystem.cs:956-958,1001-1089`; gated by `allowManipulation`, `:1036`). This is editor or debug
   use [I]; block it in co-op.
5. **Third-party tools that write directly** bypass every capture point:
   - MoveIt (02 §7);
   - ZoningToolkit always reports `Clear` and tags entities with `Updated` itself
     (`thirdparty/ZoningToolkit/ZoningToolkitModToolSystem.cs:36-63,191-198,456`).

   Tools that use Temp/Apply (LineTool, TreeController: `thirdparty/LineTool-CS2/Code/Systems/LineToolSystem.cs:798`,
   `thirdparty/Tree_Controller/Tree_Controller/Tools/TreeControllerTool.cs:675-679`) are covered by IP2 and IP3
   automatically. The direct writers need generic change detection (report 08) as a safety net [I].

### 7.8 Risks of the recommended design

| Risk | Detail | Mitigation [I] |
|---|---|---|
| Trusting client geometry | the host commits what the client computed | revision checks, cost recomputation from prefab data, area ownership; friends-only sessions accept the residual risk |
| Concurrent edits | the host and a client edit the same entity in the same RTT window | per-entity revision numbers; reject the later action; the client rolls back |
| Missing side effects | direct commit skips Apply-only bookkeeping: `Recent`, `TriggerAction(ObjectCreated)`, money | the host reproduces them explicitly (§7.3 step 2) |
| Duplicate derived side effects on clients | clients' local `Created` processing (XP, milestones, `DefaultPoliciesSystem` defaults, `UpgradeDeletedSystem`) | disable or neutralize the listed systems on clients; create entities with an empty `Policy` buffer (§4) |
| Event re-firing | replaying `Modify` on clients enqueues `TriggerAction`s locally (`ModifiedSystem.cs:163,198-211`), which could produce duplicate chirps or notifications if those are replicated too | choose one source: replicate trigger outcomes from the host and drop local `TriggerSystem` output on clients, **or** replicate only inputs |
| Prediction flicker on reject | predicted roads vanish and originals reappear | rare in co-op; show a toast |
| Capture B completeness | `Applied` before Modification1 covers Apply* outputs; zone `Cell` edits live on existing blocks (`ApplyZonesSystem` copies cells to the original blocks, 02 §4.4) and show up as `Applied+Updated` blocks | treat blocks as keyed children (02 §9.1 item 5) |
| Barrier timing | `EndFrameBarrier` is unavailable at MainLoop start (§0.4) | run host commits of UI commands in UIUpdate or after `AllowBarrier`; run tool change-sets with `EntityManager` or `ModificationBarrier1`-style writes before `ToolSystem` (02 §9.1) |
| Focus pause | `TimeUISystem` pauses on focus loss (`:115-137`) | Harmony-replace that logic in co-op |
| Mod parity | Anarchy and other mods change validation | session mod-set equality check (report 05 / 09) |

---

## 8. Open questions

1. **Exact cost recomputation on the host.** `Temp.m_Cost` is produced by Generate systems, for example
   `GenerateObjectsSystem` creation, relocation and refund costs (02 §3.2). Can the host recompute it from the
   change-set without running Generate, or should it trust the client's Σcost within a tolerance?
2. **`CityModifier` on clients**: replicate the buffer, or let `CityModifierUpdateSystem` converge? That depends on
   whether clients run the simulation (report 03).
3. **`Followed` (life path) ownership**: shared (in the save, a 50-entry global limit) or per-player overlay? If per
   player, the host's save will only contain the host's follows.
4. **Trigger dispatch thread**: confirm that cohtml `TriggerBinding` callbacks run inside `m_UIManager.Update()`
   (`GameManager.cs:1806`) and never inside MainLoop. If any run inside MainLoop, handler timing assumptions change.
5. **Two `tool.setWaterSimSpeed` registrations** (`ToolUISystem.cs:165,255`): which handler wins, and is the water speed
   UI reachable in game mode or only in the editor?
6. **`HiddenRoute` in saves**: it is serialized. Should the host strip it from the save sent to joiners, or should each
   client keep its own?
7. **Palette (`MeshColorPalette`)**: shared city data or per-player preference? It is saved with the city
   (`MeshColorPaletteSystem.cs:28-36`).
8. **Interception mechanics**: Harmony prefixes on private UI handlers (several in the tables above) are brittle across
   game patches. Prefer the public API choke points (`ModifyPolicy` is private but unique; `SetPolicy` is public).
   Decide the policy per action.
9. **Ordering guarantee of the two-type `UpdateBefore<T, ToolOutputSystem>`** when `ToolOutputSystem` itself is
   registered with `UpdateAfter` (`SystemOrder.cs:695`). Check `UpdateSystem.AddSystemUpdate` (02 §1.2) to confirm
   that the gate runs after all tools and before the output system.
10. **Telemetry and achievements**: `PlatformManager.IndicateAchievementProgress` runs in UI handlers (policies, map
    tiles). Should achievements be granted per player for shared actions, or only to the acting player?
