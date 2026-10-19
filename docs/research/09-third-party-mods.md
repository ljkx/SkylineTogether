# 09 — Third-party mods: what they change and how a generic sync layer would see it

> **Historical research report.** Written before and during the implementation; it describes the game, not the
> mod, and is not maintained. For how the mod works, see [docs/ARCHITECTURE.md](../ARCHITECTURE.md). Code is cited
> by file and member and described in prose; no game code is reproduced.

Project: **Skyline Together** (clean-room, host-authoritative co-op for Cities: Skylines II).
Scope: the 10 mods with local source under `thirdparty/`, plus 11 further mods without published source.

## Conventions

- Paths starting `thirdparty/` refer to the published sources of those mods. All other code paths are relative to the root of the game's decompiled assemblies.
- **[V]** means verified: read in code at the cited lines. **[I]** means inference: reasoning from verified facts, not observed directly. **[W]** means taken from the web (Traffic only). **[U]** means unverified because there is no source, so the claim is a classification from the mod's stated purpose.
- "Design A" replicates the final ECS state of persistent entities generically, whatever system wrote it. Host-authoritative: the host diffs and sends, and clients apply.
- "Design B" replays tool intents. The originating client sends the intent, the host re-executes it through the same tool code, and the results are then replicated or replayed.

---

## 1. Vanilla baseline that decides how mods look to a sync layer

### 1.1 Mod components are saved automatically, and that tells us what is persistent
- [V] `ComponentSerializerLibrary.Initialize` walks every type in the `TypeManager`. Any type implementing `IEmptySerializable` or `ISerializable` gets a serializer, **including types from other assemblies (mods)**. Those types are also returned in `serializableComponents` (`Colossal.Core/Colossal.Serialization.Entities/ComponentSerializerLibrary.cs:45-98`, especially lines 53-56 and 76-79).
- [V] `SerializerSystem.CreateQuery` builds the save query as `Any` of {`PrefabRef`, `WaterSourceData`, ..., **plus every mod serializable component**}. Its `None` list is {`NetCompositionData`, `EffectInstance`, `LivePath`, `Temp`, `Deleted`} (`Game/Game.Serialization/SerializerSystem.cs:82-83, 171-209`).
  - [I] Consequence: an entity with **no `PrefabRef`** is still saved as soon as it carries one mod-serializable component. Examples are BetterBulldozer record entities and water sources.
  - [I] Consequence: joining through a host save (snapshot) automatically carries all mod-serializable component data, with Entity references remapped by the save writer. The same data must also flow at runtime for Design A.
- [V] Enableable handling (`ComponentSerializerLibrary.cs:57-71, 82, 86`) works as follows:
  - An enableable `IEmptySerializable` that is not `ISerializeAsEnabled` is saved with its **enabled bit** (`EnableableEmptyComponentSerializer`).
  - This covers `Game.Objects.Decoration` (`Game/Game.Objects/Decoration.cs:8`), Tree Controller's `LumberResource`, and `Game.Prefabs.Locked` (`Game/Game.Prefabs/Locked.cs:8`).
  - **A generic layer must replicate enabled bits as well as values.**
- [V] Systems implementing `IDefaultSerializable`, `IJobSerializable` or `ISerializable` are serialized per save (`Colossal.Core/Colossal.Serialization.Entities/SystemSerializerLibrary.cs:21-57`). Water Features uses this to store **per-city mod settings inside the save** (§2.9).
- [V] Components that are **not** serializable, and therefore local or transient by the game's own definition:
  - `Updated` (`Game/Game.Common/Updated.cs:7`) and `BatchesUpdated` (`Game/Game.Common/BatchesUpdated.cs:7`).
  - `Highlighted` (`Game/Game.Tools/Highlighted.cs:7`) and `Temp` (`Game/Game.Tools/Temp.cs:5`).
- [V] Serializable vanilla components that mods write directly:
  - `Overridden` (`Game/Game.Common/Overridden.cs:8`, empty) and `Attached` (`Game/Game.Objects/Attached.cs:6`).
  - `Transform` (`Game/Game.Objects/Transform.cs:8`) and `Tree` (`Game/Game.Objects/Tree.cs:6`).
  - `Upgraded` (`Game/Game.Net/Upgraded.cs:7`), `Game.Net.Elevation` (`Game/Game.Net/Elevation.cs:7`) and `Game.Objects.Elevation` (`Game/Game.Objects/Elevation.cs:6`).
  - `Building` (`Game/Game.Buildings/Building.cs:6`), `Condemned` (`Game/Game.Buildings/Condemned.cs:8`), `Native` (`Game/Game.Common/Native.cs:8`), `PrefabRef` (`Game/Game.Prefabs/PrefabRef.cs:6`).
  - `Block`, `ValidArea` and `Cell` (`Game/Game.Zones/Block.cs:8`, `ValidArea.cs:7`, `Cell.cs:7`), and `WaterSourceData` (`Game/Game.Simulation/WaterSourceData.cs:6`).
  - `CullingInfo` (`Game/Game.Rendering/CullingInfo.cs:8`, empty-serializable).

### 1.2 Update phases and the tool pipeline (the reference for "through the vanilla pipeline")
- [V] Phase list: `Game/Game/SystemUpdatePhase.cs:3-37`.
- [V] Tools run in `ToolUpdate` (`Game/Game.Common/SystemOrder.cs:700-711`). Definitions are written to `ToolOutputBarrier` (lines 694-696).
- [V] `Generate*Systems` run in `Modification1` (lines 95-103). `Apply*Systems` run in `ApplyTool` (lines 713-721).
- [V] `ToolBaseSystem.applyMode` (`Game/Game.Tools/ToolBaseSystem.cs:182`) gates applying.
- [V] Applied entities receive `Applied`, `Created` and `Updated`, for example `Game/Game.Tools/ApplyObjectsSystem.cs:924` and `ApplyNetSystem.cs:901-903`. These tags are removed in the `Cleanup` phase by `CleanUpSystem` (`Game/Game.Common/CleanUpSystem.cs:24-32`; `SystemOrder.cs:54`).
  - [I] So "post-apply" mod systems that query `Applied` (Anarchy) see a newly applied entity for exactly one frame, and only on the machine that ran the apply.
- [V] Further vanilla systems referenced below:
  - `BlockSystem` in `Modification4` (`SystemOrder.cs:156`).
  - `CompositionSelectSystem` in `Modification3` (line 139).
  - `ZoneCheckSystem` (line 258) and `ValidationSystem` (line 264) in `ModificationEnd`.
  - `BuildingConstructionSystem` (line 586) and `TreeGrowthSystem` (line 470) in `GameSimulation`.
  - `ObjectColorSystem` in `Rendering` (line 666).

### 1.3 Write-pattern taxonomy used in this report
| Code | Pattern | Seen by Design A? | Seen by Design B? |
|---|---|---|---|
| P1 | Modifies tool **input** (CreationDefinition / ObjectDefinition / NetCourse) before `Modification1` | yes (result) | only if the mod's UI state travels with the intent |
| P2 | Modifies **Temp** entities before apply (Modification3/5) | yes (result) | only if UI state travels |
| P3 | **Post-apply** processor keyed on `Applied` **plus local active tool/UI state** | yes (result) | **no**: the host's active tool is not the remote player's |
| P4 | **Direct write to persistent entities** from a tool/UI/settings button (ECB on `ToolOutputBarrier` or `EntityManager`), bypassing Temp | yes | **no**, unless each mod action becomes its own intent type |
| P5 | Continuous **enforcement/derivation** system running on every machine (Modification*/Rendering/ToolUpdate) | must not fight replication | runs everywhere and can diverge |
| P6 | **Simulation-phase** system (GameSimulation): changes state over time | yes (host only) | host only |
| P7 | **Load-time** mutation (`Deserialize` phase / `PostDeserialize` / `OnGameLoadingComplete`) driven by **local settings** | runs on each joiner, so must be neutralized or made identical | same |
| P8 | **Prefab-data** mutation (prefab entities are not world state and not diffed) | invisible | invisible |
| P9 | Local-only writes onto persistent entities (`Highlighted`, `Updated`, `BatchesUpdated`, camera-driven) | **must be filtered** (false dirties) | harmless |
| P10 | Persistent entities **without `PrefabRef`** or with `PrefabRef(Entity.Null)` | layer must not key identity on PrefabRef | n/a |
| P11 | **Entity references inside mod components** | require ID remapping | require ID remapping |

---

## 2. Per-mod analysis (local source)

### 2.1 Anarchy (Yenyang)
**Registration** [V] (`thirdparty/Anarchy/Anarchy/AnarchyMod.cs:128-155`):
- Harmony `PatchAll`.
- Systems: `DisableToolErrorsSystem` (Modification5), `EnableToolErrorsSystem` (ModificationEnd), `AnarchyPlopSystem`, `PreventOverrideSystem` and `RemoveOverridenSystem` (before ModificationEnd), `PreventCullingSystem` (ToolUpdate), `ModifyNetCompositionDataSystem` (Modification3), `ResetNetCompositionDataSystem`, `ResetTransformSystem`, `HandleClearUpdateNextFrameSystem` and `SetRetainingWallSegmentElevationSystem` (ModificationEnd), `CheckTransformSystem` and `HandleUpdateNextFrameSystem` (Modification1), `ObjectDefinitionSystem` and `NetworkDefinitionSystem` (before Modification1), `TempNetworkSystem` (before `CompositionSelectSystem`, Modification3), `AnarchyComponentsToolSystem` (ToolUpdate), `CopyAnarchyComponentsSystem` (Modification2).

**Own components** [V] (`thirdparty/Anarchy/Anarchy/Components/*.cs`):
| Component | Serializable | Meaning |
|---|---|---|
| `PreventOverride` | `IEmptySerializable` | object must not be culled or overridden |
| `TransformRecord {float3 m_Position; quaternion m_Rotation}` | `ISerializable` (`TransformRecord.cs:15, 136-158`) | the "true" transform, enforced every update; **local** to owner if the object is a sub-object |
| `DoNotForceUpdate` | `IEmptySerializable` | marker on lanes |
| `HeightRangeRecord`, `UpdateNextFrame`, `ClearUpdateNextFrame`, `SetEndElevationsToZero` | none (transient) | helpers |

**What it changes**
- P3 [V] `AnarchyPlopSystem.OnUpdate` (`Systems/OverridePrevention/AnarchyPlopSystem.cs:222-344`) handles entities with `Applied` and without `Owner`/`Temp`, **only if the local `m_ToolSystem.activeTool`/`activePrefab`, the local `AnarchyUISystem.AnarchyEnabled`, `LockElevation` and `m_ElevationChangeIsNegative` match**. In that case it:
  - removes `Overridden` (238-239);
  - **detaches** objects: removes `Attached` and **rewrites the parent's `SubObject` buffer** without the entity (261-277);
  - adds `TransformRecord` (278-297) and adds a `Game.Objects.Elevation` with `ElevationFlags.Lowered` (299-308);
  - adds `PreventOverride` (311-315), and adds `PreventOverride` + `DoNotForceUpdate` to the sub-lanes of a "Lane Editor Container" (319-329).
- P7 [V] `AnarchyPlopSystem.OnGameLoadingComplete` (140-219) removes `TransformRecord`/`PreventOverride` from inappropriate entities, rewrites `TransformRecord` to local space (152-178), and **adds `Deleted` to NetObjects that carry `TransformRecord`** (210-213). This runs on every machine that loads.
- P5 [V] `PreventOverrideSystem` (`PreventOverrideSystem.cs:55-68`) removes `Overridden` and adds `Updated` on any `PreventOverride`+`Overridden` entity, every frame, on every machine.
- P5 + local state [V] `RemoveOverridenSystem` (`RemoveOverridenSystem.cs:119-145`) removes `Overridden` and adds `Updated` on **all** owned+overridden+updated entities, **when the local active tool is Bulldoze/Default, or Object/Line/Net with local Anarchy on**.
- P5 [V] `ResetTransformSystem` (ModificationEnd, `Systems/ObjectElevation/ResetTransformSystem.cs:60-160`) **overwrites `Game.Objects.Transform` from `TransformRecord`** (world, owner-local or node-local) whenever the entity is `Updated`. In the editor it also rewrites sub-object transforms (136-158).
  - [I] On every machine, any Transform change that arrives without a matching `TransformRecord` is snapped back.
- Local state [V] `CheckTransformSystem` (Modification1, `CheckTransformSystem.cs:150-253`) updates `TransformRecord` from the current Transform **only for entities selected by the local tool**. Those are MoveIt's selection read **by reflection** (157-188), `ToolSystem.selected` (189-216), or a "Transform Gizmo" tool (217-253).
  - [I] A remote MoveIt move replayed on the host without the host's MoveIt selection would be snapped back by `ResetTransformSystem`.
- P4 [V] `AnarchyComponentsToolSystem` writes `PreventOverride`/`TransformRecord` add/remove directly via `ToolOutputBarrier` (`AnarchyComponentsToolSystem.cs:404-488, 494-583, 646-735`). It also adds `Highlighted`+`BatchesUpdated` to persistent entities (P9; 288-289, 500-513).
- P4 [V] `SelectedInfoPanelTogglesSystem` (info-panel buttons) adds `TransformRecord`/`PreventOverride` directly (`Systems/Common/SelectedInfoPanelTogglesSystem.cs:201-223`).
- P4 [V] `CopyAnarchyComponentsSystem` adds `PreventOverride`/`TransformRecord` to entities while the MoveIt tool is active (`Systems/MoveItIntegration/CopyAnarchyComponentsSystem.cs:158-193`).
- P1 [V] `ObjectDefinitionSystem` edits `CreationDefinition`/`ObjectDefinition` (elevation, brush constraint), depending on the local tool and settings (`Systems/Common/ObjectDefinitionSystem.cs:81-246`). `NetworkDefinitionSystem` edits `NetCourse` elevations (`NetworkDefinitionSystem.cs:67-457`).
- P2 [V] `TempNetworkSystem` (enabled only while the local active tool is NetTool, 135-139) rewrites **Temp** edges/nodes in Modification3 (141-145): `Temp.m_Flags |= Modify`, add/remove `Game.Net.Elevation`, set `Upgraded` flags (231-604). It reads the local `NetworkAnarchyUISystem` composition, side-upgrade and replace state (156-171) and a local key state, `SecondaryApplyMimic` (189-194).
- P5 + local state [V] `SetRetainingWallSegmentElevationSystem` (ModificationEnd, `SetRetainingWallSegmentElevationSystem.cs:54-82, 106-332`) runs on **any `Applied` or `Updated` edge**, whether or not a tool is involved. It is enabled only while the local tool is NetTool (52). It strips Raised/Lowered flags from elevated `Upgraded`, adds or removes `Game.Net.Elevation` on edges and nodes, and tags `UpdateNextFrame`.
- P8 [V] Prefab and derived-data mutations:
  - `DisableToolErrorsSystem` sets `ToolErrorFlags.DisableInGame|DisableInEditor` on **ToolError prefab entities** each frame while a tool is active (`Systems/ErrorChecks/DisableToolErrorsSystem.cs:66-117`), per local Anarchy state (`AnarchyUISystem.GetAllowableErrorTypes`, `Systems/Common/AnarchyUISystem.cs:210-220`). `EnableToolErrorsSystem` re-enables them at ModificationEnd.
  - `ModifyNetCompositionDataSystem` edits `NetCompositionData.m_HeightRange` using `Settings.MinimumClearanceBelowElevatedNetworks` (`ClearanceViolation/ModifyNetCompositionDataSystem.cs:76-151`). `NetCompositionData` entities are excluded from saves (`SerializerSystem.cs:203`).
  - `ExtendedRoadUpgrades/UpgradesManager.cs:81-203` **adds new NetPrefabs** (upgrade modes) to the `PrefabSystem`.
- Harmony [V]:
  - `UniqueAssetTrackingSystem.IsPlacedUniqueAsset` returns `false` when `AllowPlacingMultipleUniqueBuildings` is on (`Patches/UniqueAssetTrackingSystemIsPlacedUniqueAssetPatch.cs:14-28`). This is a validation rule.
  - `ToolUISystem.GetElevationRange` becomes ±1000 (`Patches/ToolUISystemGetElevationRangePatch.cs:17-31`).
  - `NetToolSystem.InitializeRaycast` postfix (`Patches/NetToolSystem_InitializeRaycast.cs:31-34`).
- P9 [V] `PreventCullingSystem` (ToolUpdate) adds `Updated` to `PreventOverride` props that **failed camera culling**, every `PropRefreshFrequency` frames (`PreventCullingSystem.cs:103-186`). This is camera-dependent and differs per machine.

**Settings affecting simulation or validation** [V] (`Settings/AnarchyModSettings.cs:138-454`):
- `AllowPlacingMultipleUniqueBuildings`, `MinimumClearanceBelowElevatedNetworks`, `PreventOverrideInEditor`, `ConstrainBrush`, `ShowElevationToolOption`.
- `NetworkAnarchyToolOptions`, `NetworkUpgradesToolOptions`, `NetworkUpgradesPrefabs` (the last one adds prefabs).
- `PreventAccidentalPropCulling`/`PropRefreshFrequency` (local).
- Non-persisted session state: the per-session `AnarchyEnabled` toggle and `ElevationLock`.

**Requirements**
- Design A:
  - Replicate `PreventOverride`, `TransformRecord`, `DoNotForceUpdate`, the `Overridden` **removal**, `Attached` removal, the parent `SubObject` buffer, `Game.Objects.Elevation`, `Game.Net.Elevation` and `Upgraded`.
  - **Apply `TransformRecord` and `Transform` in the same frame**, before ModificationEnd. Otherwise `ResetTransformSystem` snaps clients back [I].
  - On clients, `RemoveOverridenSystem`, `PreventOverrideSystem` and `SetRetainingWallSegmentElevationSystem` should be suppressed (Harmony-skip `OnUpdate` on non-host), or the replicated `Overridden`/Elevation state will be fought locally [I].
  - Never diff `Highlighted`/`Updated`/`BatchesUpdated`.
  - Neutralize the `OnGameLoadingComplete` cleanup on joiners. It mutates and deletes after load; with a host snapshot it should be a no-op [I], but it must not run *after* replicated state is applied.
  - Host settings must govern `AllowPlacingMultipleUniqueBuildings`/clearance, because they decide what validation accepts.
- Design B:
  - The intent must carry `AnarchyEnabled`, the elevation/lock state, `NetworkAnarchyUISystem` composition/upgrade choices, `ObjectToolSystem.actualMode`, and `SecondaryApplyMimic` pressed.
  - The host must **temporarily impersonate the remote tool context** (active tool, active prefab, Anarchy toggle) during the replayed apply frame. Otherwise `AnarchyPlopSystem` (P3), `RemoveOverridenSystem`, `TempNetworkSystem` (P2) and `SetRetainingWallSegmentElevationSystem` take the host's local state [I].
  - The Anarchy components tool, the info-panel toggles and the MoveIt copy need their own intent types (P4).
  - **Requires an identical Anarchy version on all peers.** The custom upgrade NetPrefabs must exist for PrefabID resolution.

### 2.2 Better Bulldozer (Yenyang)
**Registration** [V] (`thirdparty/BetterBulldozer/BetterBulldozer/BetterBulldozerMod.cs:105-128`):
- ToolUpdate: `SubElementBulldozerTool`, `HandleDeleteInXFramesSystem`, `SafelyRemoveSystem`, `RestoreFencesAndHedgesSystem`, `RestoreBrandingObjects`, `RemoveVehiclesCimsAndAnimalsTool`, `RemoveExistingOwnedGrassSurfaces`.
- ModificationEnd: `AutomaticallyRemoveFencesAndHedges`, `AutomaticallyRemoveBrandingObjects`, `RemoveRegeneratedSubelementPrefabsSystem`.
- Before Modification1: `AutomaticallyRemoveManicuredGrassSurfaceSystem`.
- Deserialize: `CleanUpOwnerRecordsSystem`.
- Modification5: `HandleUpdateNextFrameSystem`.

**Own components** [V]:
| Component | Serializable | Notes |
|---|---|---|
| `PermanentlyRemovedSubElementPrefab {Entity m_RecordEntity}` (buffer, on the **owner** building/net) | `ISerializable` (`Components/PermanentlyRemovedSubElementPrefab.cs:61, 90-103`) | **Entity ref** (P11) to a record entity |
| `OwnerRecord {Entity m_Owner}` on **standalone record entities** that also hold `PrefabRef(prefab)` | `ISerializable` (`Components/OwnerRecord.cs:13, 30-43`) | **Entity ref**, and a **new persistent entity with PrefabRef but no geometry** (P10) |
| `DeleteInXFrames {int m_FramesRemaining}` | none | frame countdown |
| `UpdateNextFrame` | none | helper |

**What it changes**
- P4 [V] `SubElementBulldozerTool`, on apply (`Tools/SubElementBulldozerTool.cs:625-743`):
  - adds `Deleted` **directly** to permanent sub-objects, sub-lanes and edges, and to orphaned edge nodes, via `ToolOutputBarrier` ECB (641-714); adds `Updated` to neighbours (658-695);
  - adds the `PermanentlyRemovedSubElementPrefab` buffer to the owner (717-722);
  - with `EntityManager.CreateEntity`, **creates record entities** carrying `OwnerRecord` + `PrefabRef` (727-741);
  - removes the buffer and adds `Updated` for "restore" (350-355);
  - while hovering, adds `Highlighted`/`BatchesUpdated` to many permanent entities (P9; 306-536).
- P5 [V] `RemoveRegeneratedSubelementPrefabsSystem` (ModificationEnd): for owners with the buffer that are `Updated` (60-115), it deletes regenerated sub-elements whose prefab matches a record, with `DeleteInXFrames` set to 30 and Transform y forced to 0 (551-582).
  - After load it also deletes **orphaned** owned entities (`Owner.m_Owner == Null`, 4 levels deep) in `OnGameLoadingComplete` (120-236). This is P7.
- P5 [V] `HandleDeleteInXFramesSystem` (**ToolUpdate**, i.e. per rendered frame, not per sim tick) decrements the counter and then adds `Deleted` (`Systems/HandleDeleteInXFramesSystem.cs:84-110`).
  - [I] The deletion timing depends on frame rate, so peers delete at different times. Final state converges only if inputs are equal.
- P5/settings [V]:
  - `AutomaticallyRemoveFencesAndHedges` and `AutomaticallyRemoveBrandingObjects` are enabled by settings (`AutomaticallyRemoveFencesAndHedgesSystem.cs:123-131, 253-256`; `AutomaticallyRemoveBrandingObjects.cs:78-86, 268-277`). They tag sub-lanes and sub-objects of any `Updated` owner for deletion **on every machine**.
  - `AutomaticallyRemoveManicuredGrassSurfaceSystem` **destroys CreationDefinition entities** for grass surfaces (P1; `AutomaticallyRemoveManicuredGrassSurfaceSystem.cs:57-63, 140`).
- P4 [V] `RemoveVehiclesCimsAndAnimalsTool` adds `Deleted` to **moving and parked vehicles, cims and animals** within a radius of the cursor, while apply is held (`Tools/RemoveVehiclesCimsAndAnimalsTool.cs:207-230, 256-338`).
- Harmony [V]:
  - `BulldozeToolSystem.InitializeRaycast` lets the vanilla bulldozer hit markers, surfaces, fences, lane-editor nets and **MovingObjects** (`Patches/BulldozeToolSystemInitializeRaycastPatch.cs:20-91`).
  - `ToolBaseSystem.GetRaycastResult` prefix (`Patches/ToolBaseSystemGetRaycastResultPatch.cs:20-117`).
  - These deletions go through the vanilla bulldoze/Temp pipeline [I].
- P7/P9 [V]:
  - `CleanUpOwnerRecordsSystem` (Deserialize) deletes records whose owner lost the buffer (`CleanUpOwnerRecordsSystem.cs:38-39, 63-87`).
  - The `SafelyRemove` settings button strips all mod data (`Settings/BetterBulldozerModSettings.cs:126-130`; `SafelyRemoveSystem.cs:109, 129`). This is a world mutation triggered from the Options UI.

**Settings affecting the world** [V] (`Settings/BetterBulldozerModSettings.cs:35-105`): `AllowRemovingSubElementNetworks`, `AllowRemovingExtensions`, `AutomaticRemovalManicuredGrass`, `AutomaticRemovalFencesAndHedges`, `AutomaticRemovalBrandingObjects`.

**Requirements**
- Design A:
  - Replicate the deletions, the owner buffer and the **record entities**. Record entities need identity that is not based on geometry, and their **Entity fields must be remapped** to network IDs (P11).
  - Agents deleted by the vehicle/cim tool are simulation entities. The host simply deletes them, and the agent sync (another doc) propagates the deletion.
  - Clients should **not** run `RemoveRegeneratedSubelementPrefabsSystem`, the automatic-removal systems or `HandleDeleteInXFrames`. Otherwise they delete derived sub-elements on their own frame-based schedule, possibly ahead of or behind the host [I].
  - Host settings govern automatic removals.
- Design B:
  - The sub-element tool, the vehicle/cim tool and restore all bypass Temp, so each needs a dedicated intent: `{owner, mainEntities[], prefabEntities[], selectionMode}` / `{position, radius}`.
  - The automatic-removal settings must be **host-wide** (session settings), not per-peer.
  - The record entities that `EntityManager.CreateEntity` creates on the host must be mapped back to clients.

### 2.3 CS2-MoveIt (Quboid)
**Registration** [V] (`thirdparty/CS2-MoveIt/Code/MoveIt/Mod.cs:33-42`): `MIT` tool (ToolUpdate), `MIT_InputSystem` (PreTool), `MIT_PostToolSystem` (PostTool), `MIT_RemoveOverriddenSystem` (before ModificationEnd), overlay and UI systems.

**Own components** [V] (`Components/Components.cs:8-48`; `Overlays/Utils.cs:47-177`): `MIT_Identity`, `MIT_ControlPoint {Entity m_Entity, m_Parent, m_Node; ...}` and `MIO_*` overlay components. **None are serializable.**
- [V] Control-point entities use the archetype {`PrefabRef`, `CullingInfo`, `MIT_ControlPoint`} with `PrefabRef(Entity.Null)` (`Managers/ControlPointManager.cs:26-30, 48-64`).
  - [I] Because `PrefabRef` is in the save query (`SerializerSystem.cs:176`), these tool-local entities **would be saved, and a PrefabRef-keyed generic layer would treat them as world entities** if one existed during a snapshot/diff (P10). Filter on `MIT_ControlPoint` or on `PrefabRef.m_Prefab == Entity.Null`.

**What it changes**
- P4 [V] **All moves and rotations are direct `ComponentLookup` writes on persistent entities**, plus `Updated`/`BatchesUpdated`:
  - `Game.Objects.Transform` position and rotation (`QAccessor/QEntity/QGeneric.cs:124-170`);
  - net `Node` position/rotation and `Curve` beziers (`QNode.cs`, `QSegment.cs`, `QLane.cs`, `QControlPoint.cs`);
  - area node buffers (`QSurface.cs:22-140`);
  - `Game.Objects.Elevation`/`Game.Net.Elevation` add, remove or set (`QEntity.cs:241-288`);
  - `MVManipControlPoint.cs:43-54` adds `Game.Net.Elevation` to edges and nodes;
  - tags added at `QAccessor/QEntity/Utils.cs:25-31` and `QObject.cs:131-135`.
  - The Temp/definition creation path is **entirely commented out** (`Tool/Creation.cs:17-351`).
- P5 [V] `MIT_RemoveOverriddenSystem` removes `Overridden` from every `Updated`+`Overridden` entity while MoveIt runs locally (`Systems/RemoveOverriddenSystem.cs:16-25`; enabled only via `Start()`, `Systems/MIT_System.cs:11-29`).
- [V] There is a local undo/redo queue (`Managers/QueueManager.cs:106-112`) that stores states with Entity handles.
- [V] Cross-mod: Anarchy reads MoveIt's selected-entity set by reflection to keep `TransformRecord` in sync (`thirdparty/Anarchy/Anarchy/Systems/ObjectElevation/CheckTransformSystem.cs:157-188`).

**Requirements**
- Design A: the natural fit, since final Transform/Node/Curve/area-node/Elevation values are replicated. Specifically:
  - batch a whole MoveIt action as one atomic change-set, because nodes and edges must agree [I];
  - downstream derived geometry (`EdgeGeometry`, lanes, blocks) is either recomputed by vanilla on clients from the same inputs, or replicated;
  - replicate `TransformRecord` with Transform when Anarchy is present;
  - never replicate `MIT_*`/`MIO_*` entities.
- Design B: MoveIt has **no vanilla intent**.
  - Either serialize MoveIt actions (`TransformAction` deltas plus selection as network IDs), which needs deep coupling to MoveIt internals, or treat MoveIt as a "direct-write" source and capture the resulting component deltas generically. That second option is Design A for this mod.
  - The undo queue is local and holds local Entity handles, so undo must also be captured as deltas [I].

### 2.4 529 Tiles (algernon)
**Registration** [V]:
- `FiveTwentyNineSystem` implements `IPostDeserialize`, registered `UpdateAfter<PostDeserialize<FiveTwentyNineSystem>, PostDeserialize<MapTileSystem>>(Deserialize)` (`thirdparty/FiveTwentyNineTiles/Code/Mod.cs:80`; `FiveTwentyNineSystem.cs:27`).
- Harmony on `MapTilePurchaseSystem` (`Patches/MapTilePurchaseSystemPatches.cs:21-120`).

**Own components** [V]: `CustomMilestone` (private, **not serializable**, `FiveTwentyNineSystem.cs:304-306`).

**What it changes** (all P7, driven by **local** settings, on every load) [V] (`FiveTwentyNineSystem.cs:53-162`):
- `UnlockAll`: removes `Native` from every locked `MapTile` (via `MapTilePurchaseSystem.UnlockTile`, which removes `Native` and adds `Updated`; `Game/Game.Simulation/MapTilePurchaseSystem.cs:346-353`).
- `ExtraTilesAtStart`: creates a new entity with `MilestoneData` + `CustomMilestone` (102-104), and enables the "Map Tiles" feature prefab by removing `Locked`/`UnlockRequirement` (209-225).
- `ExtraTilesAtEnd`/`AssignToMilestones`: rewrites `MilestoneData.m_MapTiles` on milestone prefab entities (P8; 108-140, 232-299).
- `RelockAllTiles`/`NoStartingTiles`: adds `Native` and `Updated` to every unlocked tile and clears `MapTileSystem.m_StartTiles` (reflected; 143-161, 63-66).

Harmony [V]:
- The transpiler caps the owned-tile count at 441 for cost scaling, makes the first 9 tiles free, and multiplies `m_Upkeep` (42-96).
- A postfix scales `CalculateOwnedTilesUpkeep` by `UpkeepModifier` (115-120), which is set from settings (`ModSettings.cs:202`). Vanilla `CityServiceBudgetSystem` uses that result for city expenses (`Game/Game.Simulation/CityServiceBudgetSystem.cs:891`), so **this setting affects money**.

**Requirements**
- Design A: `Native` on `MapTile` is serializable (`Game/Game.Common/Native.cs:8`, `Game/Game.Areas/MapTile.cs:8`) and is replicated like any other state.
  - **Danger:** a joiner loading the host snapshot runs `PostDeserialize` with **its own** settings and can unlock or relock all tiles locally [I].
  - Neutralize this on clients: skip `FiveTwentyNineSystem.PostDeserialize` when joining, or force the host's settings before load. Then re-sync `Native`.
  - Milestone-prefab edits and the upkeep multiplier are invisible to a diff (P8/patch). Make the settings session-wide and host-sent.
- Design B: tile purchases are vanilla actions (`MapTilePurchaseSystem`). The cost and upkeep are computed by the host's patched code, so the host's settings govern. Load-time behaviour needs the same neutralization.

### 2.5 Historical Start (algernon)
**Registration** [V]: `HistoricalStartSystem` `UpdateAfter` in `Deserialize` (`thirdparty/HistoricalStart/Code/Mod.cs:68`). There are no components and no patches.

**What it changes** (P7 + P8, local settings, each load) [V] (`HistoricalStartSystem.cs:31-317`):
- For prefabs matching names or unlock requirements, it creates `Game.Common.Event`+`Game.Prefabs.Unlock` **event entities** (296-301), which vanilla processes into prefab `Locked` disablement.
- It **rewrites `UnlockRequirement` buffers** on prefab entities, swapping the Transportation service requirement for milestone 4 (255-262).
- It divides `PlaceableObjectData.m_XPReward` by 10 (303-309). The `ServiceUpgradeData` branch writes back an unmodified struct (311-316), which is an upstream bug and a no-op.
- Settings [V]: `UnlockBus`, `UnlockTrams`, `UnlockTrains`, `UnlockShips`, `UnlockFarming`, `UnlockMining`, `UnlockOil`, `UnlockBasicHighways`, `UnlockAllHighways`, `UnlockDistricts` (`ModSettings.cs:33-78`).
- [V] The prefab `Locked` state is persisted per prefab in the save (`Game/Game.Serialization/ResolvePrefabsSystem.cs:62-75, 129-151`; `BeginPrefabSerializationSystem.cs:83`).
  - [I] The unlocks therefore travel with a host save, but the `UnlockRequirement` swap and the XP change do not.

**Requirements**
- Design A: prefab `Locked` bits are not world entities. The layer needs a **prefab-unlock channel** (host sends its unlocked-prefab set; client applies). Joiners must not run `HistoricalStartSystem` with differing settings, or must be forced to the host's settings [I].
- Design B: same as A. XP rewards affect host-side progression only (host-authoritative).

### 2.6 Line Tool (algernon)
**Registration** [V]: `LineToolSystem : ObjectToolBaseSystem` (ToolUpdate), plus UI and tooltip systems (`thirdparty/LineTool-CS2/Code/Mod.cs:120-122`; `Systems/LineToolSystem.cs:39, 130`). There are **no components.**

**What it changes**
- [V] **Uses the vanilla pipeline:** a copied `CreateDefinitions` job writes `CreationDefinition` + `ObjectDefinition` (+ `OwnerDefinition`) + `Updated` via `ToolOutputBarrier` (`Systems/CreateDefinitions.cs:409-572`; `LineToolSystem.cs:1035-1099`). Apply happens with `applyMode = ApplyMode.Apply` (798).
- [V] **Non-deterministic inputs:**
  - per-point `RandomSeed.Next()` seeds that **re-roll every 2 seconds of `UnityEngine.Time.time`** while randomization is on (900-903, 1004-1026);
  - `m_FixedRandomSeed` (1039; incremented by `UpdateRandomSeed`, 557-559);
  - `CreationDefinition.m_RandomSeed` from the job random (`CreateDefinitions.cs:423-429`);
  - random rotation seeded from position (983-997);
  - random age (`CreateDefinitions.cs:496`).
- [V] Point generation depends on local terrain height data, mode, spacing and offset settings (895), and on Tree Controller age state (`AgeMask`, `LineToolSystem.cs:1045`; tool ordering checks at 599).

**Requirements**
- Design A: placed objects arrive as ordinary new persistent objects (Transform, PrefabRef, Tree, and so on). No mod-specific work is needed, and the mod is not required on clients except for using the tool [I].
- Design B: the intent must carry the **fully resolved list** `(prefab, position, rotation, randomSeed, fixedSeed, ageMask, elevation)` per point, **not** the line parameters. Re-running `CalculatePoints` on the host would give different seeds, terrain samples and time-based re-rolls [I].
  - Better still, send the final CreationDefinitions.
  - Anarchy (P3) also post-processes Line Tool results (`AnarchyPlopSystem.cs:31-37, 292`), so Anarchy's context must travel too.

### 2.7 Plop the Growables (algernon)
**Registration** [V] (`thirdparty/PlopTheGrowables/Code/Mod.cs:61-82`):
- `ExistingBuildingSystem` (after, Deserialize).
- `SpawnedBuildingSystem` (after `BuildingConstructionSystem`, GameSimulation).
- `PloppedBuildingSystem` (ModificationEnd).
- `SelectiveZoneCheckSystem` (after Plopped, ModificationEnd).
- **The vanilla `ZoneCheckSystem` is disabled** (`Mod.cs:74`).

**Own components** [V]: `PloppedBuilding`, `SpawnedBuilding` and `LevelLocked` (legacy). All three are `IEmptySerializable` (`Systems/PloppedBuilding.cs:15`, `SpawnedBuilding.cs:15`, `LevelLocked.cs:15`).

**What it changes**
- P6 [V] `SpawnedBuildingSystem` tags `Building`+`UnderConstruction` zoned buildings with `SpawnedBuilding` (`SpawnedBuildingSystem.cs:31-41`).
- P5 [V] `PloppedBuildingSystem` (**ModificationEnd, every machine**): any zoned `Building` **without** `UnderConstruction`/`SpawnedBuilding`/`PloppedBuilding`/`Signature`/`ExtractorProperty` gets `PloppedBuilding`. If `LockPloppedBuildings` is on, it also gets `Building.m_Flags |= Historical` (`PloppedBuildingSystem.cs:52, 65-82`).
  - [I] **Race:** a client that receives a replicated spawned building before its `SpawnedBuilding` tag (or after `UnderConstruction` was removed) mis-tags it as plopped and possibly historical. With the client's own `LockPloppedBuildings` setting, the result diverges.
- P5 [V] `SelectiveZoneCheckSystem` replaces vanilla zone checking. It adds or removes `Condemned` for buildings outside valid zones, skipping plopped ones when `SpawnedZoneDespawn` is set (`SelectiveZoneCheckSystem.cs:80, 369-375`). Vanilla's equivalent also runs at ModificationEnd (`Game/Game.Buildings/ZoneCheckSystem.cs:301-307`).
- P7 [V] `ExistingBuildingSystem` on load (`ExistingBuildingSystem.cs:136-170`) tags untagged buildings `SpawnedBuilding`, converts `LevelLocked` to `Historical` (+`BuildingCondition`), and clears upgrade components (`ResourceNeeding`, `BuildingUpgradeElement`) on historical buildings (211-233).
- P4 [V] Settings buttons `AllBuildingsHistorical`, `RemoveAllHistorical` and `RemoveAllAbandonment` mutate many buildings directly from the Options UI (`ModSettings.cs:89-121`; `ExistingBuildingSystem.cs:64-107, 187-233`). Effects include removing `Abandoned`, adding `PropertyToBeOnMarket`, setting `BuildingCondition`, adding consumers, and adding `Updated` to road edges.
- P4 [V] The info-panel toggle adds or removes `LevelLocked` directly (`Systems/PlopTheGrowablesUISystem.cs:34-63`).
- Settings [V]: `SpawnedZoneDespawn`, `LockPloppedBuildings` (`ModSettings.cs:48-70`).

**Requirements**
- Design A:
  - Replicate `PloppedBuilding`, `SpawnedBuilding`, `Building.m_Flags` and `Condemned`. Creation packets must be **atomic**: archetype including the mod tags, in one apply, before ModificationEnd.
  - **Suppress `PloppedBuildingSystem` and `SelectiveZoneCheckSystem` on clients.** Vanilla `ZoneCheckSystem` must also be host-only.
  - Settings buttons are world mutations, so the host executes them, or clients send them as commands.
- Design B: plopping a growable goes through the vanilla ObjectTool, and the host's `PloppedBuildingSystem` tags it. The settings buttons and the LevelLock toggle need dedicated commands. `LockPloppedBuildings` must be a host/session setting.

### 2.8 Tree Controller (Yenyang)
**Registration** [V] (`thirdparty/Tree_Controller/Tree_Controller/TreeControllerMod.cs:116-131`):
- ToolUpdate: `TreeControllerTool`, `DestroyFoliageSystem`, `ModifyVegetationPrefabsSystem`.
- Before Modification1: `TreeObjectDefinitionSystem`.
- Before GameSimulation: `FindTreesAndBushesSystem`, `DeciduousSystem`, `SafelyRemoveSystem`. At GameSimulation: `ReloadFoliageColorDataSystem`.
- **Rendering**: `LumberAndPauseTreeGrowthSystem` (before `ObjectColorSystem`).
- ModificationEnd: `DetectAreaChangeSystem`. Before Modification5: `ModifyTempVegetationSystem`.
- Harmony: brush strength, toolbar, and `WindControl.SetGlobalProperties`.

**Own components** [V] (`Components/*.cs`):
| Component | Serializable |
|---|---|
| `DeciduousData {TreeState m_PreviousTreeState; bool m_TechnicallyDead; bool m_PermanentDecoration}` | `ISerializable` (`DeciduousData.cs:14, 36-65`) |
| `Evergreen` | `IEmptySerializable` |
| `LumberResource` (**IEnableableComponent**) | `IEmptySerializable`; the enabled bit is saved (§1.1) |
| `NoTreeGrowth` | `IEmptySerializable` (legacy) |
| `RecentlyChanged`, `Vegetation` (prefab tag) | none |

**What it changes**
- P4 [V] `TreeControllerTool` brush/radius jobs write **directly** to permanent trees via `ToolOutputBarrier` (`Tools/TreeControllerTool.cs:818-954`):
  - `Tree.m_State` changes;
  - **`PrefabRef` swaps** (943), which change the prefab of an existing entity;
  - add or remove the `Tree` component (912-924);
  - add `Decoration` and toggle its enabled bit (878-916);
  - remove `Evergreen`/`DeciduousData`;
  - add `RecentlyChanged`/`Updated`/`BatchesUpdated`.
- P1/P2 [V]:
  - `TreeObjectDefinitionSystem` edits `CreationDefinition`/`ObjectDefinition` (age, prefab, brush constraint) and **destroys definitions** (`Systems/TreeObjectDefinitionSystem.cs:67-213`).
  - `ModifyTempVegetationSystem` edits **Temp** trees and plants: `PseudoRandomSeed`, `Tree` state/growth, stumps, `Decoration` enabled = local `ObjectToolSystem.decorationMode` (`ModifyTempVegetationSystem.cs:56-171`).
- P6 [V] `DeciduousSystem` changes `Tree.m_State` to/from `Dead` by season and sets `Decoration` (`DeciduousSystem.cs:124-245`). `FindTreesAndBushesSystem` adds `DeciduousData`/`Evergreen`/`LumberResource` (`FindTreesAndBushesSystem.cs:63-186`). Both depend on `UseDeadModelDuringWinter`.
- P5 [V] **Rendering-phase world writes:** `LumberAndPauseTreeGrowthSystem.OnUpdate` toggles `Decoration` (= the vanilla growth pause, because `TreeGrowthSystem` only processes trees with **disabled** `Decoration`: `Game/Game.Simulation/TreeGrowthSystem.cs:271`) and `LumberResource` enabled bits. It runs on every machine, per `DisableTreeGrowth`, and inside wood-resource areas (`LumberAndPauseTreeGrowthSystem.cs:108-191, 435-489`).
  - On load it converts `NoTreeGrowth` to `Decoration` and sets `DeciduousData.m_PermanentDecoration` (86-105, 495-534). This is P7.
- P8 [V]:
  - `ModifyVegetationPrefabsSystem` sets `PlaceableObjectData` cost to 0 when `FreeVegetation` is on (`ModifyVegetationPrefabsSystem.cs:39-59, 99-108`). This affects placement cost, i.e. money.
  - `ReloadFoliageColorDataSystem` rewrites foliage color data on prefab submeshes and adds `Evergreen` to plants (207, 490). This is visual.
- P4 [V] Settings buttons:
  - `DestroyFoliageSettings` makes `DestroyFoliageSystem` add `Deleted` to **every unowned tree/plant** (`DestroyFoliageSystem.cs:49-97`; `Settings/TreeControllerSettings.cs:259-262`).
  - `SafelyRemoveButton` strips mod data (`SafelyRemoveSystem.cs:89-96`).
  - `PauseTreeGrowthButton`/`ResumeTreeGrowthButton` (`TreeControllerSettings.cs:130-148`).
- Local only [V]: the wind patch (`Patches/WindGlobalPropertiesPatch.cs:15-48`).

**Requirements**
- Design A:
  - Replicate the `Tree` component (presence and value), the **`Decoration` enabled bit**, the **`LumberResource` enabled bit**, `DeciduousData`, `Evergreen` and **in-place `PrefabRef` changes**. The layer must support archetype changes and PrefabRef changes on an existing network ID.
  - Suppress `LumberAndPauseTreeGrowthSystem`, `DeciduousSystem` and `FindTreesAndBushesSystem` on clients, or force identical settings. Otherwise clients flip Decoration bits locally every frame [I].
  - Host settings govern `DisableTreeGrowth`, `UseDeadModelDuringWinter` and `FreeVegetation`.
- Design B: the TreeController brush bypasses Temp, so it needs its own intent `{position, radius, ages, prefab set, decorationMode, ...}` plus a host-side executor. The settings buttons also need commands. Object-tool placement carries the definition-time modifications only if the age and constraint UI state travels.

### 2.9 Water Features (Yenyang)
**Registration** [V] (`thirdparty/Water_Features/Water_Features/WaterFeaturesMod.cs:122-158`):
- `CustomWaterToolSystem` (ToolUpdate).
- GameSimulation/EditorSimulation: `FindWaterSourcesSystem`, `AutofillingLakesSystem`, `DetentionBasinSystem`, `RetentionBasinSystem`, `ChangeWaterSystemValues`, `TidesAndWavesSystem`, `SeasonalStreamsSystem`, `DisableSeasonalStreamSystem`, `DisableWavesAndTidesSystem`, `AutomatedWaterSourceSystem`, `RemoveFloodedSystem`.
- Serialize: `BeforeSerializeSystem` before, the restore systems after.

**Own components** [V] (`Components/*.cs`, all `ISerializable`, all **on `WaterSourceData` entities**):
- `AutofillingLake {m_MaximumWaterHeight}`
- `AutomatedWaterSource {m_MaximumWaterHeight, float4 m_PreviousWaterHeights, m_PreviousFlowRate}`
- `DetentionBasin {m_MaximumWaterHeight, m_SnowAccumulation}`
- `RetentionBasin {m_MaximumWaterHeight, m_MinimumWaterHeight, m_SnowAccumulation}`
- `SeasonalStreamsData {m_OriginalAmount, m_SnowAccumulation}`
- `TidesAndWavesData {m_OriginalAmount}`

**Serialized systems (settings stored in the save)** [V]:
- `ChangeWaterSystemValues : IDefaultSerializable` writes EvaporationRate, Fluidness, ForceWaterSimulationSpeed and WaterCausesDamage, and **on load copies them into the global mod settings** (`Systems/ChangeWaterSystemValues.cs:21, 46-87`).
- `TidesAndWavesSystem` handles wave/tide height, frequency, classification and damping (`TidesAndWavesSystem.cs:26, 66-101`).
- `SeasonalStreamsSystem` handles snowmelt, flow, seasonality, multipliers and stormwater (`SeasonalStreamsSystem.cs:34, 68-130`).

**What it changes**
- P4/P10 [V] `CustomWaterToolSystem` **creates water-source entities directly** with archetypes {`WaterSourceData`, `Transform`, (`Created` | one mod component)}. They have **no `PrefabRef`** (`Tools/CustomWaterToolSystem.cs:368-373, 1102-1154`). The tool also deletes (534-535) and edits WaterSourceData/Transform/mod components in place (651-869).
- P6 [V] Sim systems **read the locally simulated water surface** (`m_WaterSystem.GetSurfaceData`, `WaterUtils.SampleHeight`) and rewrite `WaterSourceData.m_Height`/mod component state every update. Examples: `AutofillingLakesSystem.cs:86-173`, `AutomatedWaterSourceSystem.cs:90-199`, `DetentionBasinSystem.cs:91-212`, `RetentionBasinSystem.cs:99-219`.
  - [I] These are feedback loops on GPU water simulation that is computed per machine, so they **must run on the host only**.
- P6/P10 [V] `TidesAndWavesSystem` **creates a dummy sea `WaterSourceData` entity** at (0,0) with radius 0 (195-238). It modulates sea sources with `TimeSystem.normalizedTime/normalizedDate` (240-247). It also sets `WaterSimSpeed` and `Damping` directly on `WaterSystem` (155-191, 236).
- Global simulation parameters [V]: `ChangeWaterSystemValues.OnUpdate` writes `WaterSystem.WaterSimulation.Evaporation/Fluidness/Damping` and `WaterSimSpeed` (131-185). `OnGameLoadingComplete` **disables the vanilla `WaterDamageSystem` and `SubmergeSystem`** per `WaterCausesDamage` (123-128).
- [V] `RemoveFloodedSystem` removes `Flooded`/`Damaged` (`RemoveFloodedSystem.cs:68-86`).
- [V] `BeforeSerializeSystem` (Serialize phase) temporarily resets `WaterSourceData.m_Height` to original/maximum values before writing (`BeforeSerializeSystem.cs:195-297`).
  - [I] A host-side snapshot for joiners triggers this, so the snapshot holds "rest" heights, not live heights.
- P8 [V] `AddPrefabsSystem` adds water-source prefabs and UI categories (`AddPrefabsSystem.cs:143-156, 210-268`).
- Settings button [V]: `WaterCleanUpCycleButton` temporarily sets evaporation to 0.1 (`Settings/WaterFeaturesSettings.cs:151-155`; `ChangeWaterSystemValues.cs:144-151`).

**Requirements**
- Design A:
  - Identity for `WaterSourceData` entities must not rely on `PrefabRef` (P10).
  - Replicate `WaterSourceData` + `Transform` + the mod components.
  - **Run all Water Features sim systems on the host only.**
  - Replicate the **global `WaterSystem` parameters** (evaporation, fluidness, damping, sim speed, damage on/off) as session state, because they are not entity state.
  - The water surface itself is a vanilla per-machine GPU simulation, covered elsewhere. With identical sources and parameters it can still drift [I].
- Design B: the custom water tool bypasses Temp, so it needs place/move/edit/delete intents. The serialized per-city settings must be host-sent after any change, not only through the load snapshot.

### 2.10 Zoning Toolkit
**Registration** [V]: `ZoningToolkitModToolSystem` (ToolUpdate), `ZoningToolkitModSystem` (**Modification4B**), and the UI system (`thirdparty/ZoningToolkit/Mod.cs:45-51`).

**Own components** [V]: `ZoningInfo {ZoningMode zoningMode}` is `ISerializable` and sits on **road edge entities** (`ZoningInfo.cs:14-30`). `ZoningInfoUpdated` (non-serializable) sits on blocks (34).

**What it changes**
- P4 [V] The tool writes `ZoningInfo` **directly** onto permanent edges via `ToolOutputBarrier`. It adds `ZoningInfoUpdated` to their sub-blocks and `Updated` to the edge and its nodes (`ZoningToolkitModToolSystem.cs:159-205, 300-317`). It also adds and removes `Highlighted` on permanent edges (P9; 35-63, 190).
- P5 [V] `ZoningToolkitModSystem` (every machine, Modification4B, after vanilla `BlockSystem` regenerated blocks in Modification4, `SystemOrder.cs:156`):
  - For blocks with `Created`/`Deleted` or `ZoningInfoUpdated`, it **sets `Block.m_Size.y` and `ValidArea.m_Area.w` to 0 or 6** by side (`ZoningToolkitModSystem.cs:106-191, 225-489`; `Utils/BlockUtils.cs:54-111`). Occupied cells are skipped (113-140).
  - For a newly **applied** road the mode comes from the system's **local UI field `zoningMode`** (356-359), inherited from deleted neighbours (385-440), or from existing `ZoningInfo` (443-446). It then writes `ZoningInfo` onto the edge (475).
- [V] The vanilla `BlockSystem` keys old blocks by `Block` value (`Game/Game.Zones/BlockSystem.cs:140-162`).
  - [I] So every edge update regenerates the blocks, and Zoning Toolkit must re-apply sizes on every peer.

**Requirements**
- Design A, two options:
  - (i) Replicate `ZoningInfo` on edges and let each peer re-derive blocks. This requires the mod on every peer and requires `ZoningInfo` to arrive **in the same apply as the edge**. Otherwise the peer's local `zoningMode` or `Default` is used.
  - (ii) Treat blocks as replicated: `Block`, `ValidArea` and `Cell` are serializable. Then suppress the mod system and vanilla `BlockSystem` deletion/re-creation churn on clients.
  - Option (i) is cheaper. Option (ii) is safer when mod versions differ.
- Design B: the toolkit tool needs an intent `{edges[], mode}`. Road placement intents must carry the placing player's `zoningMode`, because the host's `ZoningToolkitModSystem.zoningMode` would otherwise apply (356-359).

---

## 3. Mods without local source

| Mod | Class | Evidence / reasoning | Sync impact |
|---|---|---|---|
| **Traffic** (krzychu124) | **World-changing, simulation-relevant** | [W] Components include `ModifiedLaneConnections` (buffer, `ISerializable`, holds `Entity edgeEntity, modifiedConnections`), `GeneratedConnection` (buffer, `ISerializable`: `Entity sourceEntity/targetEntity`, index maps, `PathMethod method`, `bool isUnsafe`), `DataOwner {Entity}` (`ISerializable`), plus `ModifiedConnections`, `ModifiedPriorities`, `ModifiedIntersection`, `CustomPriority`, `LanePriority`, `TrafficUpgrade` and others ([Components](https://github.com/krzychu124/Traffic/tree/master/Code/Components), [ModifiedLaneConnections.cs](https://raw.githubusercontent.com/krzychu124/Traffic/master/Code/Components/LaneConnections/ModifiedLaneConnections.cs), [GeneratedConnection.cs](https://raw.githubusercontent.com/krzychu124/Traffic/master/Code/Components/LaneConnections/GeneratedConnection.cs)). [W] `Mod.cs` disables vanilla `LaneSystem` and runs `TrafficLaneSystem` (before LaneSystem, Modification4). It has its own Temp pipeline: `GenerateLaneConnectionsSystem`/`GenerateEdgePrioritiesSystem` (Modification3), `ApplyLaneConnectionsSystem`/`ApplyPrioritiesSystem` (ApplyTool, before ApplyNetSystem), `ModificationDataSyncSystem` (Modification4B), and validation after vanilla ([Mod.cs](https://raw.githubusercontent.com/krzychu124/Traffic/master/Code/Mod.cs)). [W] Its README says custom settings are stored in the save and reset when the road network is updated ([repo](https://github.com/krzychu124/Traffic)). | **Must be on all peers, same version.** Lane generation on every machine goes through its replacement lane system [I]. Design A: replicate its data entities and buffers with **Entity remapping** (P11), and accept that lanes are re-derived locally. Design B: tool apply follows a vanilla-like Temp flow, but the intent must carry Traffic tool state. |
| **Road Speed Adjuster** | **Probably world-changing [U]** | No source found, and web searches returned nothing. The name implies per-road speed limits, which in CS2 live on lane/composition data used by pathfinding [I]. | Treat as world-changing until its DLL is inspected. Require it on all peers and verify which components it writes (needs a decompile pass). |
| Find It | Local-only (UI) [U] | Asset browser that activates vanilla/prefab tools. Placement happens through vanilla tools [I]. | Placement is synced via vanilla. The mod should be on peers who use it, but it is not required. |
| Extended Tooltip | Local-only [U] | Read-only tooltip info. | None. |
| I18N Everywhere | Local-only [U] | Localization sources. | None. Locale differences only affect strings; never sync localized text, sync IDs. |
| Unified Icon Library | Local-only [U] | Icon/thumbnail assets. | None. |
| Asset Icon Library | Local-only [U] | Icon thumbnails for prefabs. | None. |
| Skyve | Local-only, external [U] | Mod/playset manager outside the game. | Useful for **mod-set parity checks** (compare playsets before joining). |
| Toggleable Overlays | Local-only [U] | Overlay/infoview rendering toggles. | None. If it writes `Updated`/`BatchesUpdated` for redraw, the generic layer must filter those (P9). |
| Employment Tracker | Local-only [U] | Read-only statistics panel. | None. The client shows replicated data. |
| Cim Census | Local-only [U] | Read-only population statistics panel. | None. |

Caveat [I]: "local-only" for [U] mods is a functional classification. Any of them may still install Harmony patches with side effects. A runtime guard is cheap: enumerate `Harmony.GetAllPatchedMethods()` per mod and flag any patch on `Game.Simulation.*`, `Game.Tools.*Apply*` or `*System.OnUpdate` in simulation phases.

---

## 4. Per-mod requirements table

Legend: A = generic final-state replication; B = intent replay. "All peers" means the mod must be installed on all peers with the same version.

| Mod | World-changing | Mod serializable comps | Direct (non-Temp) writes (P4) | Per-machine mutating systems (P5/P7) | Sim-relevant settings | A requires | B requires | All peers? |
|---|---|---|---|---|---|---|---|---|
| Anarchy | Yes | `PreventOverride`(E), `TransformRecord`(S), `DoNotForceUpdate`(E) | Components tool, info-panel toggles, MoveIt copy | PreventOverride, RemoveOverriden, ResetTransform, SetRetainingWall, CheckTransform, load cleanup | AllowMultipleUnique, Clearance, PreventOverrideInEditor | Replicate comps + Overridden/Attached/SubObject/Elevation/Upgraded; **Transform+TransformRecord atomic**; suppress P5 on clients; filter Highlighted/Updated | Carry Anarchy/elevation/network-anarchy UI state; host impersonates remote tool context for P3; own intents for P4 | **Yes** |
| Better Bulldozer | Yes | `PermanentlyRemovedSubElementPrefab`(S, Entity), `OwnerRecord`(S, Entity) | Sub-element delete, vehicle/cim delete, restore, SafelyRemove | RemoveRegenerated (+orphan cleanup on load), Auto-remove fences/branding, DeleteInXFrames (frame-based), CleanUpOwnerRecords | Auto-removal toggles, AllowRemoving* | Replicate deletions, owner buffers and **record entities (PrefabRef, no geometry)**; remap Entity fields; suppress P5 on clients | Dedicated intents for every tool action; session-wide auto-removal settings | Yes (data refs) |
| MoveIt | Yes | none | **All edits** (Transform/Node/Curve/area nodes/Elevation) | MIT_RemoveOverridden (local tool only) | none | Natural fit: atomic change-set per action; ignore `MIT_*`/`MIO_*` and `PrefabRef(Null)` entities | No vanilla intent: serialize MoveIt actions, or fall back to delta capture | Only for users of the tool (A) / Yes (B) |
| 529 Tiles | Yes (load-time + economy) | none | none | **PostDeserialize unlock/relock** with local settings | UnlockAll/Relock/ExtraTiles*, UpkeepModifier | Replicate `Native` on MapTiles; **block or force-host-settings on joiner PostDeserialize**; session settings for upkeep/cost patches | Same, plus host-side purchase cost | **Yes** (same settings) |
| Historical Start | Yes (prefab unlocks) | none | none | Deserialize-phase unlock events + UnlockRequirement rewrite | Unlock* toggles | **Prefab-unlock channel**; joiners use host settings | Same | Yes (same settings) |
| Line Tool | Yes (via vanilla pipeline) | none | none | none | spacing/random options (UI) | Nothing special | Send **resolved points + seeds** or final definitions, not line params; Anarchy context | Only for users (A) / Yes (B) |
| Plop the Growables | Yes | `PloppedBuilding`, `SpawnedBuilding`, `LevelLocked` (all E) | Settings buttons, LevelLock toggle | **PloppedBuildingSystem (race)**, SelectiveZoneCheck (replaces vanilla), load conversion | SpawnedZoneDespawn, LockPloppedBuildings | Atomic creation incl. tags; suppress Plopped/SelectiveZoneCheck on clients; replicate Historical flag/Condemned | Commands for buttons/toggle; host-wide settings | **Yes** |
| Tree Controller | Yes | `DeciduousData`(S), `Evergreen`(E), `LumberResource`(E, enableable), `NoTreeGrowth`(E) | Brush (Tree state, **PrefabRef swap**, add/remove Tree, Decoration bit), DestroyFoliage, SafelyRemove | **Rendering-phase Decoration/LumberResource toggling**, Deciduous/FindTrees (sim), load conversion | DisableTreeGrowth, UseDeadModelDuringWinter, FreeVegetation | Replicate **enabled bits**, archetype + PrefabRef changes; suppress P5/P6 on clients | Brush intent + host executor; commands for buttons; age/constraint UI state | **Yes** |
| Water Features | Yes (simulation) | 6 comps, all S, on WaterSourceData | Water tool create/move/edit/delete (**no PrefabRef**) | All sim systems read **local GPU water** (host-only), dummy sea source creation | Evaporation, Fluidness, Damping, SimSpeed, WaterCausesDamage, tides/streams params (saved per city) | Non-PrefabRef identity; host-only sim systems; replicate **WaterSystem global params** as session state | Tool intents; push settings changes live | **Yes** |
| Zoning Toolkit | Yes | `ZoningInfo`(S) on edges | Tool writes ZoningInfo directly | **Block resize at Modification4B on every peer**; uses local `zoningMode` for new roads | (UI zoning mode) | `ZoningInfo` atomic with edge, then re-derive; or replicate Block/ValidArea/Cell and suppress | Edge-set intent; road intents carry player's zoningMode | Yes (option i) |
| Traffic | Yes (lanes/pathing) | Many (S, with Entity refs) | No (own Temp pipeline) [W] | Replacement lane system on every peer [W] | n/a | Replicate data entities with Entity remap; lanes re-derived | Carry Traffic tool state | **Yes** |
| Road Speed Adjuster | Probably [U] | unknown | unknown | unknown | unknown | Verify by decompile | Verify | Assume yes |
| Find It, Extended Tooltip, I18N Everywhere, Unified/Asset Icon Library, Skyve, Toggleable Overlays, Employment Tracker, Cim Census | No [U] | — | — | — | — | Filter any Updated/BatchesUpdated noise | — | No |

(S = `ISerializable`, E = `IEmptySerializable`.)

---

## 5. Design implications for a generic layer (derived)

1. **Entity identity must not depend on `PrefabRef`** [I, from §2.2, §2.3, §2.9].
   - Track every entity matched by the vanilla save query: `SerializerSystem` uses `Any(PrefabRef, WaterSourceData, ..., all mod serializables)` with `None(Temp, Deleted, NetCompositionData, ...)` (`SerializerSystem.cs:171-209`).
   - Exclude tool-local artefacts: `PrefabRef.m_Prefab == Entity.Null`, `MIT_ControlPoint`.
   - Reusing the save system's own component set gives automatic coverage of any future mod component.
2. **Entity fields inside mod components need remapping** (BetterBulldozer, Traffic) [V]. Reuse the save writer's `Write(Entity)` semantics. For example, serialize components with the game's `ISerializable` implementation into a writer whose Entity write emits network IDs. That gives versioned, mod-authored formats for free [I].
3. **Enabled bits and archetype/PrefabRef changes** must be first-class diff items (Tree Controller, `Decoration`, `LumberResource`) [V].
4. **Atomic apply before `ModificationEnd`.** Several mods enforce or derive in ModificationEnd (`ResetTransformSystem`, `PloppedBuildingSystem`, `SelectiveZoneCheckSystem`, `RemoveRegenerated...`) or in Modification4B (Zoning Toolkit). Replicated change-sets must therefore land in a single frame, early (for example in Modification1 or a dedicated barrier), with all mod tags included [I].
5. **Client-side suppression list** (Harmony-skip `OnUpdate` when not host) [I]:
   - Anarchy: `PreventOverrideSystem`, `RemoveOverridenSystem`, `SetRetainingWallSegmentElevationSystem`, `ResetTransformSystem`. `ResetTransformSystem` is optional if Transform and TransformRecord arrive together.
   - BetterBulldozer: `RemoveRegeneratedSubelementPrefabsSystem`, `AutomaticallyRemove*`, `HandleDeleteInXFramesSystem`.
   - PlopTheGrowables: `PloppedBuildingSystem`, `SelectiveZoneCheckSystem`, plus vanilla `ZoneCheckSystem`.
   - Tree Controller: `LumberAndPauseTreeGrowthSystem`, `DeciduousSystem`, `FindTreesAndBushesSystem`, `DestroyFoliageSystem`.
   - Water Features: all GameSimulation systems.
   - Zoning Toolkit: `ZoningToolkitModSystem`, only if blocks are replicated.
   - Rather than hard-coding this list, consider a **generic guard**: on clients, block structural or value writes to replicated components from any system outside the replication applier. That is hard in ECS, so in practice use a per-mod suppression registry plus divergence detection [I].
6. **Change detection must ignore local-only churn** [V]:
   - `Highlighted` written by the ZoningToolkit, BetterBulldozer and Anarchy tools;
   - `Updated` from Anarchy `PreventCullingSystem`, which depends on the camera;
   - `BatchesUpdated`.
   - Diff by value or version against component data, not by the presence of `Updated`.
7. **Load-time mutators must be neutralized on joiners** [V/I]: 529 Tiles `PostDeserialize`, `HistoricalStartSystem`, `ExistingBuildingSystem`, and the `OnGameLoadingComplete` hooks of Anarchy and BetterBulldozer. Either force the host's mod settings before the snapshot load, or skip them and re-sync after.
8. **Session settings channel** [I]: settings that change simulation or validation must come from the host and be locked on clients:
   - Anarchy unique/clearance; BetterBulldozer auto-removal; 529 upkeep/tiles; Historical Start unlocks; Plop lock/despawn; Tree Controller growth/winter/free;
   - Water Features evaporation/fluidness/damping/speed/damage plus tides/streams. These are partly saved through `IDefaultSerializable` systems, but only at load time.
   - This needs per-mod adapters, because each mod stores settings differently (`ModSetting` subclasses).
9. **Prefab-level state** (Locked bits, UnlockRequirement, MilestoneData, PlaceableObjectData cost, ToolErrorData, foliage colors, added prefabs) is invisible to an entity diff [V]. Unlock state needs its own channel. The rest is covered by "same mods, same settings".
10. **Design B is incomplete for this mod set** [I]:
    - MoveIt, Tree Controller (brush), BetterBulldozer (sub-element, cims), Water Features (water tool), Zoning Toolkit, the Anarchy components tool, and every settings button bypass the Temp pipeline.
    - Anarchy post-apply depends on the local tool context.
    - Line Tool depends on time-varying random seeds.
    - **Recommendation: Design A as the authority, with Design B only as an optional latency optimisation for pure vanilla tools.**

---

## 6. Open questions
- Road Speed Adjuster: no public source; which components and systems it writes is to be confirmed from its documentation or in game.
- Traffic: verify which of `ModifiedConnections`/`DataOwner` entities carry `PrefabRef` (affects identity), and whether `ModificationDataSyncSystem` (Modification4B) deletes data on edge updates. Peers would then race unless suppressed.
- The exact intra-phase order of mod systems registered with `UpdateAt` versus vanilla (for example Anarchy `EnableToolErrorsSystem` versus `ValidationSystem`, both ModificationEnd) was not verified; ordering depends on `UpdateSystem` registration order (see doc on `UpdateSystem`).
- Whether vanilla tool-local entities other than MoveIt's (for example `CreationDefinition` without `Temp`) can match the save query between ToolUpdate and Modification1 was not checked.
- How much water-surface divergence remains with identical sources and parameters (vanilla GPU simulation determinism) is covered elsewhere.
