# 14 – Progression, unlocks, notifications, chirper, achievements and tutorials

> **Historical research report.** Written before and during the implementation; it describes the game, not the
> mod, and is not maintained. For how the mod works, see [docs/ARCHITECTURE.md](../ARCHITECTURE.md). Code is cited
> by file and member and described in prose; no game code is reproduced.

Research for **Skyline Together** (clean-room, host-authoritative co-op mod for Cities: Skylines II).
Every path is relative to the root of the game's decompiled assemblies unless stated otherwise.
Line numbers refer to the decompiled files as read on 2026-10-10.

Legend:

- **[V]** verified: read in the decompiled code at the cited location.
- **[I]** inference: follows from verified code plus general Unity DOTS knowledge, but is not proven line by line.

Prior reports this builds on: 03 §6.6/§7/§9, 04 §0.9/§6/§7, 05 §8, 09 §2.4–2.5.

---

## 0. TL;DR (design-relevant)

1. **Use one pattern for all progression: replicate the state, then synthesize the event locally.** Every progression "event" the UI reacts to is an ECS entity that lives for one frame: `Event`+`Unlock`, `Event`+`MilestoneReachedEvent`. The game systems that create those events also apply the rewards. So clients must **disable every reward-applying producer** and receive the resulting *state* (`XP`, `MilestoneLevel`, `DevTreePoints`, `PlayerMoney`, `Creditworthiness`, prefab `Locked` bits). Then a client-side apply system **creates the matching event entity itself**, so the vanilla UI (toolbar, milestone popup, dev tree, "new" highlights) refreshes exactly as on the host. This removes the 03 §6.6 double-application hazard by construction. [I, built on the facts below]
2. **The prefab `Locked` set is authoritative, gameplay-relevant city state.** It is saved per prefab (`ResolvePrefabsSystem.cs:122-150`, `BeginPrefabSerializationSystem.cs:75-97`) [V]. Simulation systems read it, e.g. crime and property scoring check whether the police and healthcare services are unlocked (`CrimeAccumulationSystem.cs:290`, `PropertyUtils.cs:629-641`) [V]. UI refresh is driven **only** by `Unlock` event entities (`ToolbarUISystem.cs:151, 216-227`; 15 UI systems query `Unlock`) [V]. A raw enable-bit write on a client therefore changes nothing on screen. Clients must apply unlocks by creating `Event`+`Unlock(prefab)` entities, which vanilla `UnlockSystem` turns into the bit flip (`UnlockSystem.cs:179-224`) [V].
3. **Icons must be replicated, not regenerated.** There are 42 `IconCommandBuffer` producers. Most are `GameSimulation` systems that add icons on *edge-triggered* threshold crossings and keep private flag bits. One example is `GarbageAccumulationSystem.AddWarningIfNeeded` (`GarbageAccumulationSystem.cs:216-223`) [V]. Clients should keep `IconCommandSystem` running and feed it replicated add/remove commands through its own public `CreateCommandBuffer()` API (`IconCommandSystem.cs:727-732`) [V]. They should drop vanilla non-`Temp`, non-`Marker`, non-`Transaction` commands. Marker icons depend on the local infoview and must stay per player (`MarkerCreateSystem.cs:415-489`) [V].
4. **Chirps, life-path events and radio news must be generated on the host only and replicated as entities or events.** Chirp prefab, sender, likes, virality and text variant all come from wall-clock-seeded RNG (`CreateChirpSystem.cs:141-183, 509`; `RandomLocalizationInitializeSystem.cs:49-50, 123`) [V]. Clients must also stop `RandomLocalizationInitializeSystem` from re-rolling the text index of replicated chirps [I].
5. **Tutorials are per player, but they leak into shared state.** Tutorial progress is stored in the user's settings (`SharedSettings.userState.shownTutorials`, `TutorialSystem.cs:109`; `UserState.cs:9-14`) [V], not in the save. However, tutorials **create `Unlock` events**, including for UI groups via `ForceUIGroupUnlock` (`TutorialSystem.cs:973-991`; `ForceUIGroupUnlock.cs:18-19`). They **unlock map tiles** when `unlockMapTiles` is set (`TutorialSystem.cs:308-317, 897-906, 1051-1062`). And the host's tutorial state **gates city milestones** (`MilestoneSystem.cs:73-76`) [V]. All three need co-op handling (§8).
6. **Achievements are already off for every peer.** Achievements are disabled whenever any code mod is loaded. `CityConfigurationSystem.PostDeserialize` adds every loaded mod to `usedMods` (`CityConfigurationSystem.cs:255-263`; `ModManager.cs:222-228`). `AchievementTriggerSystem.OnGameLoaded` then ANDs `achievementsEnabled` with `usedMods.Count == 0` (`AchievementTriggerSystem.cs:427-430`) [V]. Recommended policy: **leave them disabled on all peers** and never patch the gate (§7).
7. **A system's `Enabled` flag is sometimes UI state.** `BudgetUISystem` shows the "Government" subsidy line only if `GameModeGovernmentSubsidiesSystem.Enabled` is true (`GameModeGovernmentSubsidiesSystem.cs:35-38`; `BudgetUISystem.cs:54`) [V]. **Neutralize host-only systems on clients with a Harmony prefix on `OnUpdate`, not with `Enabled = false`, wherever UI reads `Enabled`.** [I]

---

## 1. XP and milestones

### 1.1 Authoritative data [V]

| Data | Where | Persisted |
|---|---|---|
| `XP { m_XP, m_MaximumPopulation, m_MaximumIncome, m_XPRewardRecord }` | city entity (`Game.City/XP.cs:6-44`) | yes (`ISerializable`) |
| `MilestoneLevel.m_AchievedMilestone` | city entity (`Game.City/MilestoneLevel.cs`) | yes |
| `DevTreePoints.m_Points` | city entity (`Game.City/DevTreePoints.cs`) | yes |
| `PlayerMoney`, `Creditworthiness` (milestone rewards) | city entity | yes |
| Milestone prefab `Locked` bit | milestone prefab entity | yes (prefab state, §2.1) |
| `MilestoneData { m_Index, m_Reward, m_DevTreePoints, m_MapTiles, m_LoanLimit, m_XpRequried, m_Major, m_IsVictory }` | milestone prefab (`Game.Prefabs/MilestoneData.cs:5-22`) | no; rebuilt from the prefab and rewritten by the game mode (§10) |
| `MilestoneUISystem.m_victoryPopupShown` | managed system field (`MilestoneUISystem.cs:998-1013`) | yes (system serializer) |
| `XPSystem.m_XPMessages` (`XPMessage{frame, amount, reason}`) | managed queue (`XPSystem.cs:55, 37`) | no |

### 1.2 Producers and phases [V]

| System | Phase / interval | What it does | Citation |
|---|---|---|---|
| `XPAccumulationSystem` | `GameSimulation`, every `262144/32 = 8192` ticks | Updates `XP.m_MaximumPopulation`/`m_MaximumIncome` **directly**. Enqueues `XPGain` for Population and Happiness. Reads `CityStatistic` buffers. | `XPAccumulationSystem.cs:42-76, 98, 110-113`; SystemOrder.cs:418 |
| `XPBuiltSystem` | `ModificationEnd` | For every **`Created`, non-`Temp` entity with `PrefabRef`**, enqueues `PlaceableObjectData.m_XPReward` / `ServiceUpgradeData.m_XPReward`, and **adds `PlacedSignatureBuildingData` to the signature prefab**. One-shot +25 XP for the first electricity consumer with supply; sets `XPRewardFlags.ElectricityGridBuilt`. Runs only when `actionMode.IsGame()`. | `XPBuiltSystem.cs:45-83, 105-123, 172, 184-186, 190-223`; SystemOrder.cs:272 |
| `NetXPSystem` | `ModificationEnd` | XP for created minus deleted net length (`Created`/`Deleted` `Edge`s), largest category only. | `NetXPSystem.cs:117-135, 254, 268-271`; SystemOrder.cs:273 |
| `XPSystem` | `ModificationEnd` | Drains `m_XPQueue` into `XP.m_XP` and pushes an `XPMessage` per non-zero gain. | `XPSystem.cs:28-41, 107-121`; SystemOrder.cs:274 |
| `MilestoneSystem` | `ModificationEnd`, every frame | **Returns immediately if `TutorialSystem.tutorialIntroUnfinished`.** If `CitySystem.XP >= next.m_XpRequried`, it increments `m_AchievedMilestone` (**at most one per frame**) and calls `NextMilestone`. That creates `Event+MilestoneReachedEvent` and `Event+Unlock(milestonePrefab)` through `ModificationEndBarrier`, and **writes `PlayerMoney.Add(m_Reward)` and `Creditworthiness += m_LoanLimit` directly with EntityManager**. It also caches `m_LastRequired/m_NextRequired/m_Progress/m_NextMilestone`. | `MilestoneSystem.cs:70-92, 94-116`; SystemOrder.cs:275 |
| `DevTreeSystem` | `ModificationEnd` (UpdateAfter) | Adds `m_DevTreePoints` (or a default curve) for **every `MilestoneReachedEvent` entity it sees** | `DevTreeSystem.cs:31-56, 128-146`; SystemOrder.cs:298 |
| `MilestoneSystem.UnlockAllMilestones` | called by `UnlockAllSystem` | Creates `Unlock` events, adds all rewards to money and creditworthiness, and sets the level. Creates **no** `MilestoneReachedEvent`. | `MilestoneSystem.cs:144-170`; `UnlockAllSystem.cs:41-61` |

Other facts [V]:
- `XPSystem` and its queue: producers call `GetQueue`/`AddQueueWriter` (`XPSystem.cs:77-86`). If the consumer is disabled while producers run, the persistent queue grows without bound (pattern from 03 §7).
- `NetXPSystem` nets created and deleted edges *within one rendered frame* (`NetXPSystem.cs:119-123`), so the XP from a net edit depends on how the host batches tool applications. That is fine under host authority. [I]
- `CitySystem` (Modification1) caches `m_Money` and `m_XP` from the city entity every frame (`CitySystem.cs:45-52`; SystemOrder.cs:105). `MilestoneSystem` and the UI read these caches (`CitySystem.cs:31-33`). **Keep `CitySystem` running on clients.**

### 1.3 Rewards and where they land [V]

| Reward | Applied by | Mechanism |
|---|---|---|
| Money | `MilestoneSystem.NextMilestone` | `PlayerMoney.Add(milestone.m_Reward)` (`MilestoneSystem.cs:103-105`) |
| Loan limit / creditworthiness | same | `Creditworthiness.m_Amount += m_LoanLimit` (`:106-108`) |
| Dev points | `DevTreeSystem.AppendPointsJob` | from `MilestoneReachedEvent` entities (`DevTreeSystem.cs:33-42`) |
| Feature and asset unlocks | `UnlockSystem` | `Unlock(milestonePrefab)` → milestone `Locked` off → requirement fixed point unlocks everything that requires that milestone (`UnlockSystem.cs:143-177`) |
| Map-tile permits | **derived, not stored** | `MapTilePurchaseSystem.GetAvailableTiles` = `max(9, startTiles)` + Σ `MilestoneData.m_MapTiles` over milestones with **`Locked` disabled** − owned tiles (`MapTilePurchaseSystem.cs:52, 163, 255-273`) |

[I] `ComponentType.Exclude<Locked>()` on an enableable component also matches entities whose `Locked` is *disabled* (Entities 1.x semantics). That is how unlocked milestones are counted.

### 1.4 Consumers and UI bindings [V]

`MilestoneUISystem` (UIUpdate, SystemOrder.cs:967), group `milestone` (`MilestoneUISystem.cs:284-298`):

| Binding | Source | Client risk |
|---|---|---|
| `achievedMilestone` | `MilestoneLevel` singleton (`:374-381`) | fine if `MilestoneLevel` is replicated |
| `totalXP` | `CitySystem.XP` (`:398-401`) | fine (CitySystem runs) |
| `achievedMilestoneXP`, `nextMilestoneXP` | **`MilestoneSystem.lastRequiredXP/nextRequiredXP` fields** (`:388-396`), computed only in `MilestoneSystem.OnUpdate` (`MilestoneSystem.cs:79-82`) | **Stuck at 0 on a client where `MilestoneSystem` never runs.** These are the only external readers (grep) |
| `maxMilestoneReached` | locked-milestone query empty (`:178-191, 383-386`) | fine if prefab `Locked` is replicated |
| `milestones`, `milestoneDetails` | refreshed **only** when a `MilestoneReachedEvent` exists or a milestone prefab is Created/Updated/Deleted (`:316-324`) | **without a local `MilestoneReachedEvent` the milestone list never refreshes on clients** |
| `unlockedMilestone` (the popup) | set in `PublishReachedMilestones` from `MilestoneReachedEvent` entities, only if `SharedSettings.userInterface.blockingPopupsEnabled && !unlockAll` (`:403-427`) | per-player setting |
| `xpMessageAdded` (raw event, "+N XP road") | `XPSystem.TransferMessages(this)` → `AddMessage(XPMessage)` (`:330, 359-372`; `XPSystem.cs:67-75`) | **no messages on clients without `XPSystem`**. `AddMessage` is public, so a mod can feed it |
| `victoryPopupShown`, `reachedPopulationGoal` | `CheckPopulationVictory` runs **only on consoles** (`:326-329, 333-357`). The flag is serialized (`:998-1006`) | PC: practically inert |
| `unlockAll` | `CityConfigurationSystem.unlockAll` (`:296`) | needs the config replicated |

`PublishReachedMilestones` also calls `Telemetry.MilestoneUnlocked` and `PlatformManager.IndicateAchievementProgress(TheLastMileMarker, …)` (`:417-418`). The latter is gated by `achievementsEnabled` (§7) [V].

### 1.5 Client strategy

- **Disable on clients:** `XPAccumulationSystem` (GameSimulation), `XPBuiltSystem`, `NetXPSystem`, `XPSystem`, `DevTreeSystem`, `UnlockAllSystem`. Disable the XP producers and their consumer together (§1.2). [I]
- **`MilestoneSystem` on clients:** replace `OnUpdate` with a Harmony prefix that recomputes only the four cached fields (copy of `MilestoneSystem.cs:77-91` without the increment branch), then return false. Do **not** disable it outright, or `achievedMilestoneXP`/`nextMilestoneXP` freeze. [I]
- **Replicate state:** `XP` (all four fields), `MilestoneLevel`, `DevTreePoints`, `PlayerMoney`, `Creditworthiness`, milestone `Locked` bits (prefab channel, §2).
- **Synthesize events on the client:** when the replicated `MilestoneLevel.m_AchievedMilestone` rises from `a` to `b`, the client apply system creates `Event+MilestoneReachedEvent(milestonePrefab(i), i)` for i in (a, b] **before `ModificationSystem`**. `DevTreeSystem` is off, so nothing is applied twice. `MilestoneUISystem` refreshes the list and shows the popup, honoring each player's own `blockingPopupsEnabled`. [I]
- **XP toasts:** host Harmony postfix on `MilestoneUISystem.AddMessage` (or on `XPSystem.TransferMessages`) broadcasts `(amount, reason)`. Clients call `MilestoneUISystem.AddMessage(new XPMessage(frame, amount, reason))` (constructor public, `XPMessage.cs`) [I].
- **Host gate:** in a session, patch the host so `MilestoneSystem` ignores `tutorialIntroUnfinished`, or warn the host. Otherwise the host player's unfinished intro tutorial blocks every player's milestones (`MilestoneSystem.cs:73-76`; `TutorialSystem.cs:231-233`). [V for the gate, I for the policy]

---

## 2. Unlocks

### 2.1 Authoritative data [V]

- `Locked` is `IComponentData, IEnableableComponent, IEmptySerializable` on prefab entities (`Game.Prefabs/Locked.cs:8`). Unlocked means the bit is disabled.
- **Persistence:** on save, `BeginPrefabSerializationSystem` marks every prefab whose `Locked` bit is disabled as dirty. It does the same for prefabs whose `PlacedSignatureBuildingData` presence differs from `SignatureBuildingData`, or that carry collected budget data (`BeginPrefabSerializationSystem.cs:75-97`). On load, `ResolvePrefabsSystem` restores `Locked` from the saved `PrefabComponents.Locked` flag for `Purpose.LoadGame`. Prefabs absent from the save default to *locked* (`ResolvePrefabsSystem.cs:122-150`). **A save-based join therefore carries the host's unlock set.**
- `UnlockRequirement` buffer on the prefab: `{ m_Prefab, m_Flags: RequireAll|RequireAny }` (`UnlockRequirement.cs`, `UnlockFlags.cs`).
- `UnlockRequirementData.m_Progress` (`UnlockRequirementData.cs:69-72`) is **not serializable**. It is reset to 0 on every load by `ResetUnlockRequirementSystem` (Deserialize phase, `ResetUnlockRequirementSystem.cs:20-29`; SystemOrder.cs:858). Requirement systems recompute it (§2.2). The UI shows it as `progress` (`PrefabUISystem.cs:2116-2128`).
- `PlacedSignatureBuildingData` (`IEmptySerializable`) on signature prefabs: added once by `XPBuiltSystem` (`XPBuiltSystem.cs:64-67`) and never removed (grep). It is read by the UI (`DestroyedBuildingSection.cs:108`).
- `CityConfigurationSystem.unlockAll` / `unlockMapTiles` / `unlimitedMoney`: a getter returns `true` if either the session value or the loaded value is set (`CityConfigurationSystem.cs:91-143`). Overrides are applied on preload (`:184-193`) and loaded from the save (`:396-432`).

### 2.2 Producers of `Unlock` events [V]

Every unlock is an `Event+Unlock` entity. `PrepareCleanUpSystem` collects every `Event` entity at the end of MainLoop for `CleanUpSystem` (`PrepareCleanUpSystem.cs:21-28, 47-52`; SystemOrder.cs:50, 54).

| Producer | Phase | Trigger | Citation |
|---|---|---|---|
| `MilestoneSystem` | ModificationEnd | milestone reached | `MilestoneSystem.cs:101-102` |
| `DevTreeSystem.Purchase` | called from UI (EndFrameBarrier, plays back at the start of the next frame) | node purchase | `DevTreeSystem.cs:157-175` |
| `UnlockSystem` (fixed point) | **MainLoop** after `ModificationSystem` | requirement closure; `createEvent:true` for derived unlocks | `UnlockSystem.cs:143-177, 206-213`; SystemOrder.cs:61 |
| `ObjectBuiltRequirementSystem` | ModificationEnd | count of built non-`Native` objects of the listed prefabs; full recount on load | `ObjectBuiltRequirementSystem.cs:~45-70, 126-157`; SystemOrder.cs:288 |
| `StrictObjectBuiltRequirementSystem` | ModificationEnd | same, strict | `StrictObjectBuiltRequirementSystem.cs:49-54, 115`; SystemOrder.cs:287 |
| `PrefabUnlockedRequirementSystem` | ModificationEnd | reacts to `Unlock` events | `PrefabUnlockedRequirementSystem.cs:42-54, 102-104`; SystemOrder.cs:289 |
| `ZoneBuiltRequirementSystem` | Modification5 | zoned building counts and squares | `ZoneBuiltRequirementSystem.cs:167-170, 224-228, 339`; SystemOrder.cs:195 |
| `ProcessingRequirementSystem` | GameSimulation | produced resource amount | `ProcessingRequirementSystem.cs:48-65, 104`; SystemOrder.cs:456 |
| `TransportRequirementSystem` | GameSimulation | transported passengers | `TransportRequirementSystem.cs:50-53, 111-124, 165`; SystemOrder.cs:597 |
| `UnlockAllSystem` | Modification1, one-shot; `Enabled = unlockAll` at `OnGameLoaded` | unlock-all option | `UnlockAllSystem.cs:34-61`; `CityConfigurationSystem.cs:195-198` |
| `TutorialSystem.ManualUnlock` (+`ForceUIGroupUnlockData`), `ClearTutorialLocks`, map-tiles feature | ModificationEnd | **per-player tutorial progress** | `TutorialSystem.cs:308-317, 504-518, 623-653, 887-940, 973-1027, 1048-1062` |
| Third-party mods (e.g. HistoricalStart) | Deserialize | local settings | 09 §2.5 |

`UnlockSystem` details [V]:
- Enabled only in game mode (`UnlockSystem.cs:121-125`). On `OnGameLoaded` it sets `m_Loaded`, forcing one full requirement pass (`:127-140`).
- `ProcessEvents` flips `Locked` off only if it is still enabled, so it is idempotent (`:179-204`). The fixed-point loop repeats `CheckUnlockRequirementsJob` until no new unlocks appear (`:150-171`).
- The rule: a prefab unlocks when none of its `RequireAll` prefabs is locked, and at least one `RequireAny` prefab is unlocked if any `RequireAny` entries exist (`:45-63`).

### 2.3 Consumers that react when `Locked` toggles [V]

UI systems detect unlocks **only through `Unlock` event entities** in the frame. Most use `PrefabUtils.HasUnlockedPrefab<T>`, which checks whether any `Unlock.m_Prefab` has component `T` (`PrefabUtils.cs:16-37`):

| System | Query / reaction | Citation |
|---|---|---|
| `ToolbarUISystem` (toolbar, asset menu, themes, packs) | `HasUnlockedPrefab<UIObjectData>` → updates `toolbarGroups`, `assetCategories`, `assets`, `themes`, `assetPacks`; re-activates the selected tool | `ToolbarUISystem.cs:151-156, 211-229` |
| `PrefabUISystem` | `Unlock` with `UIObjectData` **or** a change in `UnlockRequirementData` order version → `prefabDetails` | `PrefabUISystem.cs:1328, 1349-1367` |
| `DevTreeUISystem` | `DevTreeNodeData` → nodes; `ServiceData` → services | `DevTreeUISystem.cs:109, 148-161` |
| `UIHighlightSystem` | adds `UIHighlight` ("new" markers) to the prefab, its group and menu; first update after load skipped; cleared on PreDeserialize | `UIHighlightSystem.cs:44-55, 106-116, 133-147` |
| `SignatureBuildingUISystem` | `unlockedSignatures` popup list (skipped if blocking popups are off or `unlockAll`) | `SignatureBuildingUISystem.cs:38-60, 101-109` |
| `FeatureUISystem`, `InfoviewsUISystem`, `PoliciesUISystem`, `StatisticsUISystem`, `TransportationOverviewUISystem`, `TutorialsUISystem`, `UpgradeMenuUISystem`, `EditorTutorialsUISystem` | `Unlock` queries | grep list (`*.cs:31/122/156/257/116/153/100/36`) |
| `AchievementTriggerSystem` (MainLoop) | `Unlock` query, for unlock achievements | `AchievementTriggerSystem.cs:382` |
| `PrefabUnlockedRequirementSystem` | chained unlock | `PrefabUnlockedRequirementSystem.cs:102` |
| `TaxationUISystem` | `Locked` order version | `TaxationUISystem.cs:170` |
| Simulation (host): `FireHazardSystem`, `ResidentialDemandSystem`, `CommercialDemandSystem`, `CrimeAccumulationSystem`, happiness/property scoring | `Exclude<Locked>` / `HasEnabledComponent<Locked>` | `FireHazardSystem.cs:308`; `ResidentialDemandSystem.cs:346`; `CommercialDemandSystem.cs:426`; `CrimeAccumulationSystem.cs:290`; `PropertyUtils.cs:629-641` |

Ordering [V]: within MainLoop, `ModificationSystem` (60) runs before `UnlockSystem` (61), then `AchievementTriggerSystem` (66), then `UIUpdateSystem` (67), with `PrepareCleanUpSystem` last (`SystemOrder.cs:50, 60-67`). An `Unlock` entity created **before `ModificationSystem`** is therefore seen by `PrefabUnlockedRequirementSystem` (ModificationEnd), `UnlockSystem`, achievements and every UI system in the same frame, then cleaned up.

### 2.4 Design: "unlocked PrefabID set" replication (prefab-state channel)

Authoritative payload per prefab (keyed by the join-time PrefabID dictionary index, 04 §1.3):
`{ unlocked: bool (Locked disabled), placedSignature: bool, requirementProgress: int? (only prefabs with UnlockRequirementData) }`.

**Host side** [I]:
1. Register a `PrefabStateSyncSystem` in MainLoop with `UpdateAfter<UnlockSystem>`. Every frame in which any `Unlock` entity exists (cheap `IsEmptyIgnoreFilter`), plus every N seconds as a safety net, it scans `Locked` (enable mask) and `PlacedSignatureBuildingData` over the few thousand prefab entities, diffs them against a bitset, and emits `PrefabStateDelta { epoch++, unlocked[], relocked[], signaturePlaced[] }`. The scan cost is trivial. Watching the scan rather than the `Unlock` events also catches events produced at any phase and direct writes by mods.
2. Send `UnlockRequirementData.m_Progress` changes at low rate (every ~2 s), only for changed values (tens of ints).
3. Every ~30 s, broadcast a hash of the whole unlocked bitset for anti-entropy.

**Join:** the save snapshot already carries the `Locked` set and `PlacedSignatureBuildingData` (§2.1). After load, send the current `UnlockRequirementData` progress table, because progress is reset on load.

**Client apply** (custom system in MainLoop **before `ModificationSystem`**, the commit point from 03 §1.1) [I]:
1. For each newly *unlocked* prefab P whose `Locked` is still enabled locally: `CreateEntity(Event, Unlock{m_Prefab=P})`. Vanilla `UnlockSystem.ProcessEvents` flips the bit (`UnlockSystem.cs:192-195`), and every UI consumer in §2.3 refreshes. `UIHighlightSystem` shows the per-player "new" markers. **Never** write the enable bit directly for unlocks, or the UI goes stale.
2. For `placedSignature`: `AddComponent<PlacedSignatureBuildingData>(prefab)`.
3. For `requirementProgress`: `SetComponentData(UnlockRequirementData)`.
4. **Relock** (rare: mods such as 529 Tiles' relock, or desync repair): `SetComponentEnabled<Locked>(P, true)`. Do **not** emit `Unlock(P)`, because `ProcessEvents` would unlock it again. To refresh the UI, emit a harmless `Unlock` entity that points to an *already-unlocked* prefab with the needed marker component (`UIObjectData`, `ServiceData`, `DevTreeNodeData`, `PolicyData`). `HasUnlockedPrefab` only checks component presence (`PrefabUtils.cs:25`), and `ProcessEvents` ignores unlocked prefabs (`UnlockSystem.cs:192`). [I; verify in game]

**What must be off or neutralized on clients so the client set equals the host set:**
- `UnlockSystem`: Harmony prefix so clients run only `ProcessEvents`, never the requirement fixed point (`UnlockSystem.cs:150-171`). Requirement buffers can legitimately differ per machine, e.g. HistoricalStart rewrites `UnlockRequirement` from local settings (09 §2.5). [I]
- `ObjectBuiltRequirementSystem`, `StrictObjectBuiltRequirementSystem`, `ZoneBuiltRequirementSystem`, `PrefabUnlockedRequirementSystem` (the Modification-phase ones run on clients by default), plus the GameSimulation ones that are already off.
- `UnlockAllSystem`. On a client joining an `unlockAll` city, `OnGameLoaded` would enable it (`CityConfigurationSystem.cs:195-198`). It calls `MilestoneSystem.UnlockAllMilestones`, which **adds rewards to money and creditworthiness locally** (`MilestoneSystem.cs:153-169`).
- `TutorialSystem` unlock side effects: see §8.4.

**Convergence rule** [I]: exact equality for everything except tutorial-family prefabs (`TutorialData`, `TutorialPhaseData`, `TutorialTriggerData`, `TutorialListData`) and the `UIGroupPrefab` targets of `ForceUIGroupUnlock`. For those, a client may legitimately be *ahead* of the host (per-player tutorials, §8). Never relock them on a client.

### 2.5 `CityConfigurationSystem` options

`unlockAll`, `unlockMapTiles`, `unlimitedMoney` and `naturalDisasters` are host-owned. They travel in the save (`CityConfigurationSystem.cs:320-325, 396-411`). They matter on clients only for UI (`milestone.unlockAll` binding, `MapTilesUISystem.cs:97`, `BudgetUISystem.cs:57`) and must never trigger local mutations. Two consumers mutate state from these flags on every peer: `UnlockAllSystem` (`unlockAll`) and `TutorialSystem` (`unlockMapTiles`, §8.4). [V]

---

## 3. Development tree

### 3.1 Data and producers [V]

- `DevTreePoints.m_Points` on the city entity.
- **Purchased nodes are encoded as `DevTreeNodePrefab` entities with `Locked` disabled.** A node prefab is `ManualUnlockable` (`DevTreeNodePrefab.cs:7`). It carries `DevTreeNodeData{m_Cost, m_Service}`, an optional `DevTreeNodeRequirement` buffer, and `DevTreeNodeAutoUnlock` when the cost is 0 (`:45-86`).
- `DevTreeSystem.Purchase(Entity)` checks `m_Cost <= points`, that the node is still locked, that the service is unlocked, and that at least one listed required node is unlocked if any are listed (`DevTreeSystem.cs:157-201`). It then **sets `points` immediately** through `SetSingleton` (`:167-168`) and queues `Event+Unlock(node)` on `EndFrameBarrier`, which plays back at the start of the next frame (`:169-171`).
- Points come in only from `MilestoneReachedEvent` (`:128-146`).

### 3.2 UI path [V]

`DevTreeUISystem` (UIUpdate) has bindings `devTree.points` (getter, updated every frame), `services`, `serviceDetails`, `nodes` and `nodeDetails` (refreshed on a `DevTreeNodeData`/`ServiceData` unlock or a modified node), plus the trigger `devTree.purchaseNode(Entity)` → `m_DevTreeSystem.Purchase(node)` (`DevTreeUISystem.cs:139-165`).

### 3.3 Node effects [V + I]

`DevTreeNodePrefab` has no modifier fields (`DevTreeNodePrefab.cs:10-22`). Only `DevTreeSystem`, `DevTreeUISystem`, `MilestoneUISystem`, `PrefabUISystem` and `Telemetry` reference the type [V, grep]. **Node effects therefore materialize only through other prefabs whose `UnlockRequirement` lists the node.** Examples are upgrades, policies and buildings, which `UnlockSystem`'s fixed point unlocks [I]. Gameplay-side effects then come from those unlocked prefabs, through host simulation reading `Locked` (§2.3).

### 3.4 Client strategy [I]

- Intercept `devTree.purchaseNode` on clients. Translate the local `Entity` to a PrefabID and send a `PurchaseDevNode(prefabId)` intent. The host calls `DevTreeSystem.Purchase(entity)`, which validates.
- Replicate `DevTreePoints` (state) and the node `Locked` bit (prefab channel, §2.4). The client synthesizes `Unlock(node)` and the dev-tree UI refreshes.
- `DevTreeSystem` stays off on clients (§1.5). Its `Purchase` must never run on a client: it would subtract points locally before the host confirms.

---

## 4. Notifications and icons

### 4.1 Authoritative data [V]

- **Icon entity:** `Icon { m_Location, m_Priority, m_ClusterLayer, m_Flags, m_ClusterIndex }` (`ISerializable`; it writes location, priority, layer and flags but not the cluster index, `Game.Notifications/Icon.cs:8, 30-55`). It also has `PrefabRef` (the notification prefab), `Owner` (except Transaction icons), optional `Target`, `Animation`, `Hidden`, `DisallowCluster`, `Temp` (`IconCommandSystem.cs:301-337`). Icons have `PrefabRef`, so the save includes them (04 §0.1).
- `IconElement` buffer on the owner is `IEmptySerializable` (`IconElement.cs:8`). It is rebuilt on load by `IconElementSystem` (`Game.Serialization/IconElementSystem.cs:47-49`; SystemOrder.cs:813). It is derived and must never be replicated.
- `IconClusterLayer { Default, Marker, Transaction }` (`IconClusterLayer.cs`). Transaction icons have **no owner** and are fire-and-forget animations (`IconCommandSystem.cs:167, 184, 333`).

### 4.2 Pipeline [V]

- Producers call `IconCommandSystem.CreateCommandBuffer()`, which allocates a `TempJob` `NativeQueue` per caller (`IconCommandSystem.cs:727-732`). They then enqueue `Add`/`Remove`/`Update`/`Remove(owner, priority)` commands (`IconCommandBuffer.cs:60-125`).
- `IconCommandSystem.OnUpdate` (ModificationEnd, SystemOrder.cs:271) merges all queues, sorts by owner and buffer index, and plays them back on `ModificationEndBarrier` (`IconCommandSystem.cs:740-803`; job `:107-125`).
- **`Add` is idempotent** per (owner, prefab, target unless `IgnoreTarget`, `SecondaryLocation`). An existing icon is updated in place (`FindIcon`, `:186-238`) and a missing one is created (`:301-337`).
- `OnStopRunning` disposes the queues (`:716-725`). **If the system is disabled while producers still call `CreateCommandBuffer`, the queues leak** (they are only freed in `OnUpdate`). [V for the code, I for the leak]

### 4.3 Producers (42 files; phases from SystemOrder.cs) [V]

- **GameSimulation (31):** `AccidentCreatureSystem`, `AccidentSiteSystem`, `AccidentVehicleSystem`, `BatteryAISystem`, `BuildingUpkeepSystem` (incl. level-up **Transaction** icon `:757`), `BuyingCompanySystem`, `CityServiceUpkeepSystem`, `CompanyMoveAwaySystem`, `DeathCheckSystem`, `DeathcareFacilityAISystem`, `DestroyAbandonedSystem`, `DispatchElectricitySystem`, `DispatchWaterSystem`, `ElectricityStatusSystem` (bottleneck `:66-70`), `FireSimulationSystem`, `GarbageAccumulationSystem` (`:216-223`), `GarbageFacilityAISystem`, `GarbageTruckAISystem`, `HealthProblemSystem`, `MeetingSystem` (Transaction `:142`), `RentAdjustSystem`, `ServiceCompanySystem`, `SewageOutletAISystem`, `TrafficBottleneckSystem`, `TransportLineSystem`, `WaterDamageSystem`, `WaterPumpingStationAISystem`, `WeatherDamageSystem`, `WorkProviderSystem`.
- **Modification phases (run on clients by default):** `RoutePathReadySystem` (Mod1), `Game.Events.InitializeSystem` (Mod2), `AddHealthProblemSystem` (Mod4), `BuildingPoliciesSystem`, `RoadConnectionSystem` (Mod4B, road-connection warning `:615-619`), `BatteryInitializeSystem`, `RemovedSystem`, `IconDeletedSystem` (Mod5), `ZoneCheckSystem` (condemned `:304-312`), `ConnectionWarningSystem`, `ValidationSystem`, `MarkerCreateSystem` (ModificationEnd).
- **Tools/local:** `ToolFeedbackSystem` (PostTool, Transaction icons `:936, 1447`), `Battery` prefab helper (`Battery.cs:54`).
- **Tool pipeline:** `GenerateNotificationsSystem` (Mod1, SystemOrder.cs:100) builds icons from `CreationDefinition+IconDefinition`. Only `DefaultToolSystem` creates those, when the selected entity is an icon (`DefaultToolSystem.cs:362-366`; `GenerateNotificationsSystem.cs:40-100, 149-159`). `ApplyNotificationsSystem` (ApplyTool, SystemOrder.cs:717) moves `Temp` icons onto originals on apply, or deletes them; `ToolErrorFlags.TemporaryOnly` icons are always deleted (`ApplyNotificationsSystem.cs:80-125`).

### 4.4 Can clients regenerate icons? (open question from 04 §10)

**No, not in general.** [V + I]
- Many simulation icons are **edge-triggered**: they are added when a value crosses a threshold *within the job that mutates that value*, and a component flag records the state. Examples: `GarbageAccumulationSystem` adds the icon when `m_Garbage` crosses `m_WarningGarbageLimit` relative to the *old* value and sets `GarbagePilingUpWarning` (`GarbageAccumulationSystem.cs:216-223`), and `ElectricityStatusSystem` adds or removes bottleneck icons (`:66-70`). Regenerating them would mean reimplementing about 30 systems' thresholds and hysteresis, and keeping the private old-value and flag state in sync.
- Some Modification-phase producers *are* pure functions of replicated structure (road connection, condemned, connection warnings). Because `Add` is idempotent, letting them run would usually agree with the host. But their `Remove` commands could fight host state during transient orderings. [I]
- **Marker icons are per player.** They depend on the local active infoview (`MarkerCreateSystem.cs:415, 465, 473, 484-489, 500-870`) and must **not** be replicated.

### 4.5 Client strategy [I]

1. Keep `IconCommandSystem`, `IconDeletedSystem`, `IconClusterSystem`, `IconAnimationSystem`, `MarkerCreateSystem` and the rendering systems enabled on clients (`NotificationIconLocationSystem`, `NotificationIconBufferSystem`, `NotificationIconRenderSystem`; SystemOrder.cs:671, 673, 693).
2. **Replicate icon state per owner** as a set of `(prefabId, priority, clusterLayer, flags, targetNetId?, customLocation?)` for `Default`-layer, non-`Temp` icons. Apply diffs through **one replication-owned `IconCommandBuffer`** obtained from `CreateCommandBuffer()` each frame, before ModificationEnd. Vanilla then creates the `Icon` entities, `IconElement`, appear animations and clusters exactly as on the host.
3. **Filter vanilla commands on clients:** Harmony-prefix `IconCommandSystem.OnUpdate` and drop commands from non-replication queues unless they are `Temp` (tool previews), `Marker` layer (local infoview) or `Transaction` layer from `ToolFeedbackSystem` (local cost feedback). Allow `IconDeletedSystem`'s removals for deleted owners and targets. They are idempotent with host removals (`IconDeletedSystem.cs:71-123`).
4. **Transaction icons from the simulation** (level-up sparkle `BuildingUpkeepSystem.cs:757`, meeting `MeetingSystem.cs:142`) are events. Replicate them as `IconEvent(ownerNetId, prefabId)` and re-emit them locally as `Add(..., IconClusterLayer.Transaction)`.
5. At join, the snapshot already contains host icons (persisted). Marker icons from the host's infoview may be in the snapshot. Remove non-local `Marker` icons after load, or let `MarkerCreateSystem.PostDeserialize` (SystemOrder.cs:893) reconcile them. Verify which happens. [I]

### 4.6 UI consumers [V]

`NotificationsSection` (selected-entity panel) and `TempNotificationTooltipSystem`/`RaycastNotificationTooltipSystem` read `IconElement` and `Icon` (grep; SystemOrder.cs:919-921). `NotificationTriggerSystem` turns `Icon` Created/Deleted into chirp triggers (§5.2).

---

## 5. Chirper and life path

### 5.1 Data [V]

| Data | Where | Persisted |
|---|---|---|
| `Chirp { m_Sender, m_CreationFrame, m_Likes, m_TargetLikes, m_InactiveFrame, m_ViralFactor, m_ContinuousFactor, m_Flags(Liked) }` | chirp entity + `PrefabRef` + `ChirpEntity` buffer + `RandomLocalizationIndex` buffer | yes (`Chirp.cs:6-83`, `ChirpEntity.cs:7`, `RandomLocalizationIndex.cs:9`) |
| `LifePathEvent { m_EventPrefab, m_Target, m_Date }` | life-path entity | yes (`LifePathEvent.cs:6-33`) |
| `LifePathEntry` buffer on followed citizens | citizen | yes (`LifePathEntry.cs:6`) |
| `Followed { m_Priority, m_StartedFollowingAsChild }` | citizen; max 50 per city | yes (`Followed.cs:6`; `LifePathEventSystem.cs:132, 198-214`) |
| `ChirpLink` buffer on linked entities | citizens and buildings | yes (`ChirpLink.cs:6`), but maintained by a UI system (`ChirpLinkSystem.cs:270-303`) |
| `TriggerSystem.m_TriggerFrames` (prefab → last-fired frame) | managed hash map | yes (`TriggerSystem.cs:512-545`) |
| `ChirpLinkSystem.m_CachedChirpData` (sender and link names cached at creation) | managed | yes (`ChirpLinkSystem.cs:350-378`) |

### 5.2 Producers and phases [V]

- **`TriggerAction` producers** call `TriggerSystem.CreateActionBuffer()`, which allocates a `TempJob` queue and **asserts `Enabled`** (`TriggerSystem.cs:405-411`). There are 29 GameSimulation producers (e.g. `CitizenHappinessSystem`, `CrimeCheckSystem`, `StatisticTriggerSystem`, `PollutionTriggerSystem`, `EarlyGameOutsideConnectionTriggerSystem`, `LoanUpdateSystem`). These also run outside the simulation: `Game.Events.InitializeSystem` (Mod2), `AddHealthProblemSystem`, `Policies.ModifiedSystem` (Mod4), `CitizenInitializeSystem`, `RemovedSystem` (Mod5), `NotificationTriggerSystem` (ModificationEnd), `ApplyNetSystem`, `ApplyObjectsSystem` (ApplyTool), `DebugSystem` (grep + SystemOrder). `NotificationTriggerSystem` emits `NewNotification`/`NotificationResolved` for every created or deleted non-`Temp` `Icon` (`NotificationTriggerSystem.cs:51-65, 120-160`).
- **`TriggerSystem`** (ModificationEnd, SystemOrder.cs:278) merges the queues. It matches trigger prefabs by target type, checks `TriggerLimitData` intervals against `m_TriggerFrames` and checks conditions (`TriggerSystem.cs:110-201`). It routes to `CreateChirpSystem`, `LifePathEventSystem`, `RadioTagSystem` (normal and emergency) or **`TutorialEventActivationSystem`** (only for unlocked tutorials) (`:203-266`). Quirk: for traffic-accident triggers the condition value is the **distance from the local `Camera.main`** (`:140-143, 483-487`), so the host's camera influences which accident chirps fire. It also fires `Telemetry.CityStats()` on a wall-clock timer (`:497-502`).
- **`CreateChirpSystem`** (ModificationEnd) uses `RandomSeed.Next()` (wall clock, 03 §3), giving one RNG per update (`CreateChirpSystem.cs:141, 509`). It picks a random chirp variant (`:217-229`) and a random sender among employees, household members or all citizens (`:245-330`). It rolls likes, active days and viral factor (`:161-183`). It suppresses repeats of a chirper prefab seen in the last 18000 frames (`:53, 147`). It appends `LifePathEntry` for life-path chirps (`:206-213`).
- **`RandomLocalizationInitializeSystem`** (ModificationEnd, UpdateAfter) assigns the text variant to **every `Created`, non-`Temp` entity with `RandomLocalizationIndex`**, using `RandomSeed.Next().GetRandom(entity.Index+1)` (`RandomLocalizationInitializeSystem.cs:49-50, 109, 123`). A replicated chirp created on a client would **get a different text** unless this is prevented.
- **`LifePathEventSystem`** (ModificationEnd) turns queued events into chirps (if `m_IsChirp`) or `LifePathEvent` entities. It acts only for senders that have a `LifePathEntry` buffer, i.e. followed citizens (`LifePathEventSystem.cs:54-81`). `FollowCitizen`/`UnfollowCitizen` are called from the UI (`LifePathUISystem.cs:85-86, 110-117`; `ActionsSection.cs:283, 288`).
- **`ChirpLikeCountSystem`** (GameSimulation, interval 64) grows `m_Likes` toward `m_TargetLikes` with random skips (`ChirpLikeCountSystem.cs:29-42, 67-70`). **`ObsoleteChirpSystem`** (GameSimulation, interval 65536) deletes the oldest chirps above `LimitSettingData.m_MaxChirpsLimit` (`ObsoleteChirpSystem.cs:76-95`).
- **`ChirpLinkSystem`** (UIUpdate) caches names on chirp creation and maintains `ChirpLink` buffers on linked entities (`ChirpLinkSystem.cs:162-268`). It initializes from scratch after load if the cache is empty (`:380-387`).

### 5.3 UI [V]

`ChirperUISystem` (UIUpdate) has bindings `chirper.chirps` (refreshed when `Chirp` changes), the raw event `chirpAdded` (needs `Created` + `RandomLocalizationIndex` + `PrefabRef`), and the triggers `addLike`/`removeLike` (toggle `ChirpFlags.Liked` through `EndFrameBarrier` + `Updated`) and `selectLink` (`ChirperUISystem.cs:104-157`). `Liked` is written only by these two triggers and read only for display (`:237`) [V, grep]. **It has no gameplay effect.**

### 5.4 Answers [V + I]

- **Are chirp likes shared state?** `m_Likes` is simulation state from the host (`ChirpLikeCountSystem`). `ChirpFlags.Liked` is persisted in the save, but it is a cosmetic per-user toggle. **Recommendation:** treat `Liked` as a per-player overlay. Clients intercept `addLike`/`removeLike` and keep a local `HashSet<chirpNetId>`. Replication masks the `Liked` bit, and the host's own flag stays host-local, which is what lands in the host save. [I]
- **Can chirps be generated only on the host and replicated?** Yes, and they must be. Replicate chirp creation as an entity: `Chirp` (sender as a NetId), `PrefabRef`, the `ChirpEntity` buffer (NetIds) and the **`RandomLocalizationIndex` buffer**. Write the localization index *after* `RandomLocalizationInitializeSystem` runs, or patch it to skip entities tagged as network-created. Keep `ChirpLinkSystem` and `ChirperUISystem` running on clients, since they derive links and caches from `Created`. Do not replicate `ChirpLink`. Replicate `m_Likes` updates at low rate, deletions from `ObsoleteChirpSystem`, `LifePathEvent` entities and `LifePathEntry` appends. [I]
- **Followed citizens** are city state, capped at 50 per city (`LifePathEventSystem.cs:132, 200`). Recommendation: shared and host-authoritative. Follow and unfollow become intents. Per-player following would need the host to generate entries for the union of all players' follows. [I]

### 5.5 Client strategy for `TriggerSystem` [I]

Two options:
- **A (simplest):** disable `TriggerSystem`, `CreateChirpSystem`, `LifePathEventSystem`, `RadioTagSystem`, `NotificationTriggerSystem` and every client-running action producer (list in §5.2). Otherwise `CreateActionBuffer` asserts and its `TempJob` queues are never disposed (`TriggerSystem.cs:394-411`). Downside: tutorials driven by `TutorialActivationEventData` never fire on clients.
- **B (recommended):** keep `TriggerSystem` running on clients. Keep `CreateChirpSystem`, `LifePathEventSystem` and `RadioTagSystem` running too, but Harmony-prefix them so they **drain and discard** their input queues. Then client-side triggers derived from replicated state (new icons, new events, policy changes) still reach `TutorialEventActivationSystem`, so per-player tutorials work, while chirps, life paths and radio come only from the host. Leave `m_TriggerFrames` to host authority; client values are throwaway.
- **Radio (either option):** replicate `RadioTag{eventPrefabId, targetNetId, segmentType, emergencyDelay}` from the host and enqueue it into the client's `RadioTagSystem` input queues (`GetInputQueue`/`GetEmergencyInputQueue`, `RadioTagSystem.cs:135+`). That way every player's radio reports the same city news (`Radio.cs:1543-1561, 1683-1705`).

---

## 6. Event journal and statistics UI

### 6.1 Event journal [V]

- **Data:** journal entity = `EventJournalEntry{m_Event, m_StartFrame}` + `PrefabRef` + `EventJournalData` buffer (`{type, value}` counts such as deaths and injuries) + `EventJournalCityEffect` buffer (`{type, startValue, value}`) + `EventJournalPending{m_StartFrame}` / `EventJournalCompleted` tags. All are serializable (`EventJournalEntry.cs:6`, `EventJournalData.cs:6`, `EventJournalCityEffect.cs:6`, `EventJournalPending.cs:6`, `EventJournalCompleted.cs:8`). The event entity carries `JournalEvent{m_JournalEntity}` (`JournalEvent.cs:6`).
- **Producers:**
  - `EventJournalInitializeSystem` (Mod4) creates a journal entity for every **`Created`** event with `JournalEvent` (`EventJournalInitializeSystem.cs:46-76, 167`; SystemOrder.cs:172).
  - `EventJournalSystem` (Mod5, SystemOrder.cs:244) starts pending entries and snapshots city effects from `Population.m_AverageHappiness`, `CityServiceBudgetSystem.GetTotalTaxIncome()`, `BudgetSystem.GetTotalTradeWorth()` and `Tourism.m_CurrentTourists` (`EventJournalSystem.cs:576-583`). It tracks effects while active, completes fire entries when no target is `OnFire` (`:272-315`), and adds data from `AddEventJournalData` event entities (`:211-263, 634-644`).
  - `AddEventJournalData` comes from `AddHealthProblemSystem`, `AddMeetingSystem`, `FaceWeatherSystem`, `IgniteSystem`, `SubmergeSystem`, `FindEventAttendantsSystem` and `HealthProblemSystem` (grep). These are simulation consequences.
- **UI:** `EventJournalUISystem` reads the managed `EventJournalSystem.eventJournal` list (last 100) and subscribes to the `eventEntryAdded`/`eventEventDataChanged` callbacks (`EventJournalUISystem.cs:38-53, 76-80`). The list is rebuilt from entities on `OnGameLoaded`, sorted by start frame (`EventJournalSystem.cs:516-545`).
- **Client strategy [I]:** replicate journal entities as state (entry, prefab, both buffers, pending and completed tags, with `m_Event` and `JournalEvent.m_JournalEntity` remapped). Disable `EventJournalInitializeSystem`, otherwise replicated `Created` events spawn **duplicate** journal entries. Disable `EventJournalSystem`'s mutating jobs. A client-side companion maintains `eventJournal` (public `NativeList`) and invokes `eventEntryAdded` / `eventEventDataChanged` when replicated entries arrive or change, reproducing `EventJournalSystem.cs:558-575`.

### 6.2 Statistics UI [V]

- **Data:** statistic entities (`CityStatistic` buffer + `StatisticParameter` + `PrefabRef`) created by `CityStatisticsSystem.InitializeLookup` (`CityStatisticsSystem.cs:715-833`). There are also system fields `m_SampleCount` and `m_LastSampleFrameIndex`, advanced on each 8192-tick sample (`:389-395, 479-480`) and serialized with the pending event queue (`:864-880`).
- **UI:** `StatisticsUISystem` binds `statistics.sampleCount` from `m_CityStatisticsSystem.sampleCount` (`StatisticsUISystem.cs:275, 305`). Its `data` binding reads `GetStatisticDataArrayLong(type, param)` and maps samples to dates with `GetSampleFrameIndex` (`:510-642`; `CityStatisticsSystem.cs:709-713`). It refreshes categories on `Unlock` with `UIObjectData` or a change in `UnlockRequirementData` order version (`:257, 308`), and greys out locked categories using `Locked` (`:349, 396, 441`).
- **Client strategy [I]:** replicate new samples per statistic key, one per 8192 ticks (append to `CityStatistic`). Replicate `m_SampleCount` and `m_LastSampleFrameIndex` through `CityStatisticsSystem.Deserialize` with an empty event queue (`:882-908`), or through reflection. `GetStatisticsEventQueue` asserts `Enabled` (`:840-845`). Its only non-simulation caller is `AddHealthProblemSystem` (Mod4) [V, grep], which must be off on clients anyway, because it also produces icons, triggers and journal data.

---

## 7. Achievements

### 7.1 Facts [V]

- **Gate:** `PlatformManager.achievementsEnabled` (`Colossal.PSI.Common/PlatformManager.cs:102-112`). `UnlockAchievement` and `IndicateAchievementProgress` are no-ops when it is false (`:256-292`).
- **Who sets it:** `MenuUISystem` sets it to `true` for a new game (`MenuUISystem.cs:770`) or to `!isReadonly` for continue/load (`:731, 804`) before `GameManager.Load`. `DebugUISystem` clears it when the debug menu is used (`DebugUISystem.cs:200, 207`).
- **`AchievementTriggerSystem.OnGameLoaded`** ANDs it with `usedMods.Count == 0 && !unlimitedMoney && !unlockAll && !unlockMapTiles` (`AchievementTriggerSystem.cs:427-430`).
- **`usedMods`** includes every *loaded* code mod and every UI module asset, added in `CityConfigurationSystem.PostDeserialize` (`CityConfigurationSystem.cs:255-263`; `ModManager.cs:217-228`). `usedMods` is also **loaded from the save** (`:412-421`) and written to it for `Purpose.SaveGame` (`:326-337`). So **any session with Skyline Together loaded has achievements off. And any save written during such a session keeps them off forever, even when later loaded unmodded.**
- **Triggers:**
  - `AchievementTriggerSystem` (MainLoop, SystemOrder.cs:66) checks city-wide conditions every frame when enabled (`:433-438`): created object achievements, parks, unique buildings, `Unlock` events, transport lines, energy mix, followed citizens, infoviews and more (queries `:380-417`). Progress buffers are per user: `UserDataProgressBuffer` uses `PlatformManager.UserDataLoad/Store` (`:77-110`). Three progress counters are serialized **in the city save** (`:959-988`).
  - `EventAchievementTriggerSystem` (ModificationEnd) tracks created events with `EventAchievement` and unlocks after a frame delay (`EventAchievementTriggerSystem.cs:57-59, 98, 113, 135`).
  - Simulation helpers read the gate (`ExtractorCompanySystem.cs:771-772`; `TransportBoardingHelpers.cs:229`).
  - The bulldozer adds progress (`BulldozeToolSystem.cs:1633`).
  - The UI calls `IndicateAchievementProgress` from `MilestoneUISystem.cs:418`, `MapTilePurchaseSystem.cs:334-338`, `PoliciesUISystem` and `PhotoModeUISystem` (grep).

### 7.2 Recommended policy [I, backed by the facts]

**Disable on all peers, which is also the vanilla outcome.** Do not patch the `usedMods` gate. It is the platform's own rule, and Skyline Together being loaded already trips it (§7.1). Practical consequences:
- On clients, `AchievementTriggerSystem` and `EventAchievementTriggerSystem` can stay as they are. With the gate off they do nothing (`:433-438`). To reduce per-frame cost, Harmony-skip their `OnUpdate` on clients without touching `Enabled`.
- Tell players in the UI that co-op saves are permanently marked as modded (`usedMods` is persisted).
- If a future platform rule ever allowed it, "earn from replicated state on every peer" would *technically* work: the triggers read world state, `Unlock` events and `Created` entities, all of which a client sees after replication. But serialized per-city progress (`:959-988`) and per-user buffers would diverge across peers. Not recommended.

---

## 8. Tutorials and advisor

### 8.1 Where the state lives [V]

- **Per user, not per city:** `SharedSettings.userState.shownTutorials : Dictionary<string,bool>` and `autoplayTutorials` (`UserState.cs:9-14`; `TutorialSystem.cs:105-109, 178-189`), and `SharedSettings.gameplay.showTutorials` (`TutorialSystem.cs:162-176`; `GameplaySettings.cs:26`).
- **Runtime tags** on tutorial prefab entities (`TutorialActive`, `TutorialCompleted`, `TutorialShown`, phase equivalents, `ForceActivation`, `AdvisorActivation`) are **not serializable** (no `ISerializable` in `Game.Tutorials`, grep). They are cleared on `PreDeserialize` (`TutorialSystem.cs:1216-1235`) and rebuilt from user settings on load (`ReadSettings`, `:329-373`).
- **But** tutorial, phase, trigger and list prefabs carry **`Locked`**, which the save persists like any prefab state (§2.1). Tutorials unlock them through `ManualUnlock` (`:973-991`).

### 8.2 Systems [V]

- `TutorialSystem` (ModificationEnd, SystemOrder.cs:284) is `Enabled = gameMode.IsGame()` (`:294-299`).
- The activation systems run under `TutorialActivationSystem` (Mod4, SystemOrder.cs:173), including `TutorialEventActivationSystem` fed by `TriggerSystem` (`TutorialActivationSystem.cs:24`). Triggers run under `TutorialTriggerSystem` (Mod5, SystemOrder.cs:245) and deactivation under `TutorialDeactivationSystem` (Mod3, SystemOrder.cs:140).
- UI: `GameTutorialsUISystem`/`TutorialsUISystem` (UIUpdate, SystemOrder.cs:993). Bindings: `tutorials.activeList`, `activateTutorial`, `forceTutorial`, `completeActiveTutorialPhase`, `completeActiveTutorial`, `setAutoplayTutorials`, `tutorialsEnabled`, `introActive`, `listIntroActive`, `listOutroActive`, `next`, `completeListIntro/Outro` (`TutorialsUISystem.cs:154-163`; `GameTutorialsUISystem.cs:27-34`).

### 8.3 Do tutorials mutate city state, pause the simulation, or block tools? [V + I]

- **Mutate city state: yes**, in three ways.
  1. `ManualUnlock` creates `Unlock` events for the tutorial prefab **and for every `ForceUIGroupUnlockData` UI group** ("UIGroups listed here will unlock whenever this prefab unlocks", `ForceUIGroupUnlock.cs:18-19`; `TutorialSystem.cs:981-990`). These go into the persisted `Locked` set.
  2. When `unlockMapTiles` is set, `OnGameLoadingComplete`, `ClearTutorialLocks` and `CompleteCurrentTutorialList` unlock the "Map Tiles" feature prefab and call `MapTilePurchaseSystem.UnlockMapTiles()`. That removes `Native` from every locked map tile (`TutorialSystem.cs:308-317, 897-906, 1051-1062`; `MapTilePurchaseSystem.cs:297-309, 346-353`). Which branch fires depends on the **local player's** `showTutorials` and `shownTutorials["ListIntro"]`.
  3. `tutorialIntroUnfinished` gates `MilestoneSystem` (`MilestoneSystem.cs:73-76`).
  Trigger systems also read `Created` objects, tools and selection (`TutorialObjectPlacementTriggerSystem.cs:622-669, 776`; `TutorialObjectSelectionTriggerSystem.cs:127-131`), so another player's replicated build can complete *your* "place X" phase. That is acceptable co-op semantics. [I]
- **Pause the simulation:** no C# write to `SimulationSystem.selectedSpeed` exists in `Game.Tutorials` or the tutorial UI systems [V, grep]. Any pause happens on the JS side through the `time.simulationPausedBarrier` (05 §9.2), which cannot be verified from C#. [I]
- **Block tools:** no tool-disabling code in C# [V, grep]. Tutorial UI groups that are still locked hide toolbar groups through `Locked` (§2.3). [I]

### 8.4 Recommended per-player handling [I]

- Keep the tutorial systems **running locally on every peer**, driven by each player's own user settings. Through `TriggerSystem` option B (§5.5) and replicated `Created` entities, client tutorials react to the shared city.
- **Clients:** let `ManualUnlock` events through only for tutorial-family prefabs and their `ForceUIGroupUnlock` UI groups. These change only that player's UI, and the §2.4 convergence rule tolerates a client being ahead on them. Harmony-block `MapTilePurchaseSystem.UnlockMapTiles()` and the "Map Tiles" feature `Unlock` on clients: map-tile `Native` state and the feature bit come from the host.
- **Host:** the host's tutorial unlocks of UI groups spread to all clients through the prefab channel. That is harmless (monotonic, UI-only). Patch `MilestoneSystem` to ignore `tutorialIntroUnfinished` in a session (§1.5).
- Advisor: `ForceAdvisor` tags are local, runtime-only (`TutorialSystem.cs:399-429`). Treat them as per player.

---

## 9. Local-only systems: confirmation and the replicated data they read

| System | Local? | Replicated data it reads | Citation |
|---|---|---|---|
| `AudioManager` / `Radio` | **Local.** Radio channel is per user (saved as `m_LastSaveRadioChannel` in the city save, `AudioManager.cs:1528-1556`) | `RadioTagSystem` events (city news and emergencies), `BrandPopularitySystem.ReadBrandPopularity` (GameSimulation, interval 2048, SystemOrder.cs:425) for ads, `TimeSystem.normalizedTime` | `Radio.cs:1543-1561, 1683-1705, 1737-1742`; `AudioManager.cs:947` |
| Photo mode (`PhotoModeRenderSystem`, `PhotoModeUISystem`) | **Local**, but it **writes `SimulationSystem.selectedSpeed` (0–8)** and must be overridden on clients (05 §9.2) | `frameIndex`/time for lighting | `PhotoModeRenderSystem.cs:765-780` |
| Cinematic camera (`CinematicCameraUISystem`) | **Local.** Sequences are saved as `CinematicCameraAsset` to the user's cloud or local asset DB | none of the city state | `CinematicCameraUISystem.cs:124-126, 284-318` |
| `RichPresenceUpdateSystem` (UIUpdate, every 5 s) | **Local** | `Population.m_Population/m_AverageHappiness/m_AverageHealth`, `CitySystem.moneyAmount` (cached `PlayerMoney`), `MilestoneLevel`, `cityName`, local tool and photo-mode state | `RichPresenceUpdateSystem.cs:142-173, 180-227, 272-281`; SystemOrder.cs:899 |
| `MarkerIconSystem` | Local (selected and followed marker) | entity positions | 04 §6 |
| `UIHighlightSystem` | Local "new" markers | `Unlock` events | §2.3 |
| `NotificationUISystem` (top-left toasts) | Local | none | 05 §8.1 |

All values these systems read come from replicated city-entity state (`Population`, `PlayerMoney`, `MilestoneLevel`) or from replicated event streams (radio tags). No extra replication is needed beyond §1, §5.5 and the brand-popularity system state (owned by the simulation report). [I]

---

## 10. Game-mode rules

### 10.1 `GameModeSystem` [V]

- It serializes only the mode prefab's `PrefabID` (`GameModeSystem.cs:111-122`). `Deserialize` resolves it into `m_NextMode` (`:124-135`).
- `OnUpdate` runs **in the Deserialize phase only** (`UpdateBefore<GameModeSystem>(SystemUpdatePhase.Deserialize)`, SystemOrder.cs:797). It restores defaults for the previous mode, falls back to `"NormalMode"`, then calls `StoreDefaultData` and `ApplyMode` (`GameModeSystem.cs:55-92`).
- `ModeSetting.ApplyMode` writes the `ModeSettingData` singleton (subsidies, wealth support, natural-resource refill, demand and tax tweaks) (`ModeSetting.cs:148-170`). It then applies local mode prefabs, which **rewrite prefab data**: `MilestonesMode` rewrites `MilestoneData` reward, dev points, tiles, loan limit and XP (`MilestonesMode.cs:42-62`); `UnlockAtStartMode` replaces `UnlockRequirement` buffers (`UnlockAtStartMode.cs:25-46`); `XPParametersMode` changes XP parameters (`XPParametersMode.cs:27`). Global query mode prefabs are applied too (`ModeSetting.cs:171-183`).
- **Client strategy:** *derive locally.* Every peer loading the host snapshot applies the same mode from the same `PrefabID`, provided the prefab sets match (mod-set handshake, 05 §1.5). There is no mid-session mode change in vanilla. Include the mode `PrefabID` in the session handshake and verify that `currentModeName` matches after load (`GameModeSystem.cs:90`). [I]

### 10.2 Where the rules are applied [V]

| Rule | System | Phase | Effect |
|---|---|---|---|
| Government subsidies | `GameModeGovernmentSubsidiesSystem` | registered **twice**: `UpdateAfter` ModificationEnd (SystemOrder.cs:301) and GameSimulation (SystemOrder.cs:491); interval 8192 | `Enabled` set on load from `ModeSettingData` (`:50-68`). `OnUpdate` computes `m_MonthlySubsidy` from `PlayerMoney` and `CityServiceBudgetSystem.GetTotalExpenses()` (`:71-90`). `CityServiceBudgetSystem` books it as income (`CityServiceBudgetSystem.cs:887, 250`). **`BudgetUISystem` shows the "Government" line iff `Enabled`** (`BudgetUISystem.cs:54`; `:35-38`) |
| Wealth support | `GameModeWealthSupportSystem` | GameSimulation (SystemOrder.cs:490) | adjusts household resources (`GameModeWealthSupportSystem.cs:40-46, 81`) |
| Natural resource refill | `GameModeNaturalResourcesAdjustSystem` | GameSimulation (SystemOrder.cs:492) | host simulation |
| Unlimited money | `PlayerMoney.m_Unlimited` set from `CityConfigurationSystem.unlimitedMoney` on `NewGame`/`LoadGame` (`CitySystem.cs:122-127`). `money` returns 2e9 when set (`PlayerMoney.cs:15-25`) | Deserialize (PostDeserialize) | the component is replicated. The UI hides "Loan Interest" when set (`BudgetUISystem.cs:57`) |
| Map-tile upkeep | `CityServiceBudgetSystem` skips tile upkeep if `unlockMapTiles` (`CityServiceBudgetSystem.cs:891`) | host | |

**Client strategy [I]:** all of these are host-owned. On clients, neutralize `GameModeGovernmentSubsidiesSystem` with an `OnUpdate` prefix, **keeping `Enabled` as set on load**, so the budget UI matches. Replicate its `monthlySubsidy` and `LastSubsidyCoverPerDay` (read by `StatisticsUISystem`, `:272`) with the budget system state. Disable the other two with the rest of GameSimulation.

---

## 11. Deliverable: per-subsystem tables

Strategy codes: **RS** replicate state · **RE** replicate event · **DL** derive locally · **PP** per player · **L** local only.

### 11.1 XP and milestones

| Authoritative data | Producers and phase | Client strategy | Double-application risks | UI values that must match |
|---|---|---|---|---|
| `XP` (4 fields), `MilestoneLevel`, `PlayerMoney`, `Creditworthiness` | `XPAccumulationSystem` (GS/8192), `XPBuiltSystem`, `NetXPSystem`, `XPSystem`, `MilestoneSystem` (ModEnd) | **RS** for state. **DL**: synthesize `MilestoneReachedEvent` from `MilestoneLevel` deltas. **RE**: XP messages through `MilestoneUISystem.AddMessage` | Client `XPBuiltSystem` on replicated `Created` buildings → XP twice and `PlacedSignatureBuildingData` added locally. Client `MilestoneSystem` → money and creditworthiness added twice. Client `DevTreeSystem` on a synthesized event → points twice. `UnlockAllSystem` → all rewards again | `milestone.achievedMilestone`, `totalXP`, `achievedMilestoneXP`, `nextMilestoneXP` (needs the `MilestoneSystem` field shim), `milestones`, `milestoneDetails`, `unlockedMilestone` popup (each player's own popup setting), `xpMessageAdded` toasts, money, loan limit |

### 11.2 Unlocks

| Authoritative data | Producers and phase | Client strategy | Double-application risks | UI values that must match |
|---|---|---|---|---|
| Prefab `Locked` bits, `PlacedSignatureBuildingData`, `UnlockRequirementData.m_Progress`; config `unlockAll`/`unlockMapTiles` | `UnlockSystem` (MainLoop), requirement systems (Mod5/ModEnd/GS), `MilestoneSystem`, `DevTreeSystem.Purchase`, `UnlockAllSystem` (Mod1), `TutorialSystem`, mods | **RS** (prefab-state channel), applied by synthesizing `Event+Unlock` (**DL** for the UI). Progress **RS**. Tutorial-family and UI-group bits: **PP** (client may be ahead) | Client requirement loop or requirement systems unlocking on local data that differs (mods). `UnlockAllSystem` on join. Writing enable bits without events (stale UI). Emitting `Unlock` for a relocked prefab (re-unlocks it) | Toolbar groups, asset menu, themes, packs, `prefabDetails` (locked and progress), dev tree, policies, infoviews, statistics categories, signature popup, "new" highlights, map-tile permits (derived from milestone `Locked`) |

### 11.3 Development tree

| Authoritative data | Producers and phase | Client strategy | Double-application risks | UI values that must match |
|---|---|---|---|---|
| `DevTreePoints`, node prefab `Locked` | `DevTreeSystem` (ModEnd, from `MilestoneReachedEvent`); `DevTreeSystem.Purchase` from `devTree.purchaseNode` | Intent `PurchaseDevNode(PrefabID)` → host. **RS** points + node bits. **DL** UI refresh through synthesized `Unlock` | Client `Purchase` subtracting points locally. Client `DevTreeSystem` adding points from the synthesized milestone event | `devTree.points`, `nodes`, `nodeDetails`, `services`, `serviceDetails`; downstream unlocks (upgrades, policies) |

### 11.4 Notifications and icons

| Authoritative data | Producers and phase | Client strategy | Double-application risks | UI values that must match |
|---|---|---|---|---|
| `Icon` entities (prefab, owner, target, priority, layer, flags, location) — persisted | 31 GameSimulation systems, 12 Modification-phase systems, tools; consumer `IconCommandSystem` (ModEnd) | **RS** per-owner icon set, applied through a replication-owned `IconCommandBuffer`. **RE** for simulation Transaction icons. **PP/L** for Marker icons and tool previews | Client Modification-phase producers adding or removing in parallel (filter). Disabling `IconCommandSystem` (queues leak). Duplicate journal or chirp triggers through `NotificationTriggerSystem` on replicated icons (handled in §5.5) | Icons in the world and clusters, `NotificationsSection` in the info panel, notification tooltips |

### 11.5 Chirper and life path

| Authoritative data | Producers and phase | Client strategy | Double-application risks | UI values that must match |
|---|---|---|---|---|
| Chirp entities (+`ChirpEntity`, `RandomLocalizationIndex`), `m_Likes`, `LifePathEvent`, `LifePathEntry`, `Followed`, `TriggerSystem.m_TriggerFrames` (host) | `TriggerSystem`/`CreateChirpSystem`/`LifePathEventSystem` (ModEnd), `ChirpLikeCountSystem`/`ObsoleteChirpSystem` (GS), `RandomLocalizationInitializeSystem` (ModEnd) | **RS** entities from the host. Keep `TriggerSystem` with muted outputs (option B). **RE** radio tags. `Liked` = **PP**. `ChirpLink`/name cache = **DL** | Client chirp generation from local triggers (duplicate random chirps). `RandomLocalizationInitializeSystem` re-rolling text. Replicating `ChirpLink` while `ChirpLinkSystem` also adds it | `chirper.chirps` (text variant, sender, likes, links), `chirpAdded`, life-path panels (`lifePath.followedCitizens`, `lifePathItems`), radio news |

### 11.6 Event journal and statistics

| Authoritative data | Producers and phase | Client strategy | Double-application risks | UI values that must match |
|---|---|---|---|---|
| Journal entities (entry, data, city-effect buffers, pending and completed tags); `CityStatistic` buffers + `m_SampleCount`/`m_LastSampleFrameIndex` | `EventJournalInitializeSystem` (Mod4), `EventJournalSystem` (Mod5), `AddEventJournalData` producers (GS/Mod4); `CityStatisticsSystem` (GS/8192) | **RS** both. Client companion maintains `eventJournal` and invokes callbacks. Statistics scalars through `Deserialize` | Client `EventJournalInitializeSystem` duplicating entries for replicated `Created` events. Client `EventJournalSystem` overwriting city-effect values with local snapshots | `eventJournal.events`, `eventMap` (counts, effects); `statistics.sampleCount`, `data`, sample dates |

### 11.7 Achievements

| Authoritative data | Producers and phase | Client strategy | Double-application risks | UI values that must match |
|---|---|---|---|---|
| Per-user platform achievements; 3 progress counters in the city save; `usedMods` in the save | `AchievementTriggerSystem` (MainLoop), `EventAchievementTriggerSystem` (ModEnd), UI and tool callers; gate `PlatformManager.achievementsEnabled` | **L**, effectively disabled on all peers (vanilla gate with mods loaded) | none while disabled. If ever enabled: every peer would "earn" from the same replicated state, and the per-city counters would diverge | `AchievementsUISystem` shows the disabled state (`AchievementsUISystem.cs:47`) |

### 11.8 Tutorials and advisor

| Authoritative data | Producers and phase | Client strategy | Double-application risks | UI values that must match |
|---|---|---|---|---|
| User settings (`shownTutorials`, `showTutorials`, `autoplayTutorials`); tutorial prefab `Locked` bits (persisted) | `TutorialSystem` (ModEnd), activation (Mod4), triggers (Mod5), deactivation (Mod3); `TriggerSystem` → `TutorialEventActivationSystem` | **PP**. Clients may unlock tutorial-family prefabs and UI groups locally. Block `UnlockMapTiles` and the feature unlock on clients. Host ignores the intro gate for milestones | Client tutorial unlocking map tiles or the "Map Tiles" feature (city state). Host tutorial blocking milestones for everyone | Each player's own tutorial panel. Shared: map-tile panel visibility, milestone progress |

### 11.9 Local-only presentation

| Authoritative data | Producers and phase | Client strategy | Double-application risks | UI values that must match |
|---|---|---|---|---|
| none (reads `Population`, `PlayerMoney`, `MilestoneLevel`, radio tags, brand popularity) | Radio (`AudioManager`, Cleanup), photo mode (Rendering/UI), cinematic camera (UI), `RichPresenceUpdateSystem` (UIUpdate) | **L** | Photo mode writes `selectedSpeed` (override per 05 §9.3) | Rich presence money and population equal the host's when the city entity is replicated |

### 11.10 Game-mode rules

| Authoritative data | Producers and phase | Client strategy | Double-application risks | UI values that must match |
|---|---|---|---|---|
| Mode `PrefabID` (`GameModeSystem`), `ModeSettingData`, `PlayerMoney.m_Unlimited`, config flags | `GameModeSystem` (Deserialize only), subsidies (ModEnd + GS), wealth support and resources (GS), `CitySystem.PostDeserialize` | **DL** mode application from the snapshot (verify `currentModeName`). **RS** subsidy figures. Host-only simulation | Toggling `Enabled` on the subsidies system on clients (budget UI line disappears). A mode mismatch from different prefab sets silently changing milestone rewards and requirements | Budget panel lines ("Government", "Loan Interest", "Tile Upkeep"), statistics subsidy series, milestone rewards shown in `milestoneDetails` |

---

## 12. Open questions

1. **JS-side pausing:** do the milestone popup, signature popup, advisor and tutorial panels subscribe to `time.simulationPausedBarrier` and pause the simulation (05 §9.2)? Must be checked with `-uiDeveloperMode`. If they do, the client-side popups must not send pause requests to the host.
2. **`Exclude<Locked>` semantics:** confirm in a running game that queries with `ComponentType.Exclude<Locked>()` match prefabs with `Locked` *disabled*, as `MapTilePurchaseSystem.cs:163` assumes. Check the Entities package version.
3. **Relock UI refresh:** verify the "harmless `Unlock` on an already-unlocked prefab" trick (§2.4 step 4) refreshes the toolbar without side effects. `AchievementTriggerSystem` and `PrefabUnlockedRequirementSystem` also read `Unlock`, but are off or gated on clients.
4. **Marker icons in snapshots:** does `MarkerCreateSystem.PostDeserialize` (SystemOrder.cs:893) remove the host's infoview markers on a joining client, or do they linger until the client toggles an infoview?
5. **Icon filter granularity:** is `Temp`/`Marker`/`Transaction` enough to separate legitimate local icon commands from simulation ones? `ValidationSystem` and `ConnectionWarningSystem` preview icons seem to carry `Temp`. Confirm that no non-`Temp` command is needed locally, e.g. `ConnectionWarningSystem` for applied but unconnected objects.
6. **`RandomLocalizationIndex` beyond chirps:** the same re-roll hazard applies to every replicated `Created` entity with that buffer (buildings, citizens, districts, …). Who owns that fix: the entity-replication layer (04) or this report?
7. **`m_TriggerFrames` keys** are prefab `Entity` handles in the save (`TriggerSystem.cs:517-545`). The save remaps them, so the join is fine. Do host trigger frames need to be exposed for anything else? Probably not.
8. **`UnlockRequirementData.m_Progress` refresh:** vanilla refreshes `prefabDetails` only on `UnlockRequirementData` *order* version changes or unlocks (`PrefabUISystem.cs:1355-1360`), so progress values can look stale even in single player. Should clients force a refresh after progress deltas for a better experience than vanilla?
9. **`GameModeGovernmentSubsidiesSystem` double registration** (ModificationEnd + GameSimulation): does the ModificationEnd slot honor the 8192 interval? It only matters for which frame the host value updates.
10. **Followed citizens: shared or per player?** This needs a product decision (§5.4). Per player requires host-side union generation and per-player filtering of `LifePathEntry`.
11. **Achievements with a future "verified co-op" mode:** out of scope while `usedMods` gates them. Revisit only if Paradox changes the rule.
