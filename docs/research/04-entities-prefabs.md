# 04 - Entity taxonomy, prefabs and persistent state

> **Historical research report.** Written before and during the implementation; it describes the game, not the
> mod, and is not maintained. For how the mod works, see [docs/ARCHITECTURE.md](../ARCHITECTURE.md). Code is cited
> by file and member and described in prose; no game code is reproduced.

Research for **Skyline Together** (host-authoritative co-op for Cities: Skylines II).
Sources: decompiled game code only. All paths below are relative to
the root of the game's decompiled assemblies. Line numbers refer to those files.

Legend used throughout:

- **[V]** verified by reading the code at the cited location.
- **[I]** inference (reasoned from code structure/naming, not traced end to end).

Save-format version decompiled: `Game/Game/Version.cs:825` (`Version.current = new Colossal.Version(1, 315290461530777395L, 50274769)`).

---

## 0. Executive summary (design-relevant)

1. **The game already defines "persistent state" mechanically**, and that definition is the best starting point for the authoritative replication set. `SerializerSystem.CreateQuery` (`Game/Game.Serialization/SerializerSystem.cs:171-214`) selects every entity that has `PrefabRef`, `LoadedIndex` or one of 16 named singleton/graph types, **or any `ISerializable`/`IEmptySerializable` component from a non-`Game` assembly (i.e. mods)**. It excludes `Temp`, `Deleted`, `NetCompositionData`, `EffectInstance`, `LivePath` (`:201-208`). [V]
2. **There are three levels of component persistence**, decided per type by interface (`Colossal.Core/Colossal.Serialization.Entities/ComponentSerializerLibrary.cs:25-106`): [V]
   - `ISerializable` (incl. `IStrideSerializable`) - data written.
   - `IEmptySerializable` - only the *presence* of the component is written; its data is **not** (`EmptyComponentSerializer.SerializeData` writes nothing, `EmptyComponentSerializer.cs:64-68`). These are, by construction, values the game rebuilds after load (e.g. `EdgeGeometry`, `Composition`, `ConnectedEdge`, `SubObject`, `SubLane`, `SubNet`, `SubArea`, `InstalledUpgrade`, `Renter`, `HouseholdCitizen`, `OwnedVehicle`, `Triangle`, area `Geometry`).
   - Anything else - stripped from the saved archetype (`EntitySerializer.cs:126-134, 479-496`).
   I extracted **788** serializable component structs in `Game.dll` (counts per namespace in section 2.3). 74 systems serialize global state (section 6).
3. **`Entity` handles are never stable across machines or loads.** On save, every referenced entity is renumbered to a dense index (`EntitySerializer.FillEntityTableJob`, `EntitySerializer.cs:238-281`); on load new entities are created. Replication must use its own stable network IDs and remap every `Entity` field - including entity indices hidden inside non-`Entity` fields, e.g. `PathNode.m_SearchKey` packs `owner.Index << 32` (`Game/Game.Pathfind/PathNode.cs:20-49`, re-encoded only by its custom `Serialize`, `:133-149`). [V]
4. **Prefab identity:** `PrefabData.m_Index` is just the position in `PrefabSystem.m_Prefabs` (`Game/Game.Prefabs/PrefabSystem.cs:146-150`), assigned in asset-database load order and changed by swap-remove (`:225-256`) - **machine-local**. The stable key is `PrefabID` = (`prefab.GetType().Name`, `prefab.name`, `Hash128`), where the hash is `Hash(platformID)` for Paradox-Mods assets, else the asset GUID, else invalid for code-created prefabs (`Game/Game.Prefabs/PrefabID.cs:15-36`). Saves store only `PrefabID`s and re-resolve on load (`PrefabSystem.Serialize/Deserialize`, `:827-911`; `ResolvePrefabsSystem`). [V]
5. **Other machine-local indices** that look like data but are not portable: `ZoneType.m_Index` inside zone `Cell`/`VacantLot` (allocated by `ZoneSystem` in init order, `Game/Game.Prefabs/ZoneSystem.cs:176-179`; remapped on load by `ResolvePrefabsSystem.FixZoneTypeJob`, `Game/Game.Serialization/ResolvePrefabsSystem.cs:300-338`), the `UpdateFrame` shared component (load-balanced by `UpdateGroupSystem`, `Game/Game.Simulation/UpdateGroupSystem.cs:316-370`) and `PathNode` keys. [V]
6. **Reverse/ownership buffers are always derived from forward references.** On load ~40 systems in `SystemUpdatePhase.Deserialize` rebuild them (`Game/Game.Common/SystemOrder.cs:798-860`), e.g. `SubObjectSystem` fills `SubObject` from `Owner`/`Attached` (`Game/Game.Serialization/SubObjectSystem.cs:21-60`), `ConnectedEdgeSystem` fills `ConnectedEdge` from `Edge` (`ConnectedEdgeSystem.cs:17-60`). During play, `*ReferencesSystem`s maintain them on `Created`/`Deleted` (`Game/Game.Net/ReferencesSystem.cs:143-169`, `Game/Game.Objects/SubObjectReferencesSystem.cs:43-72`). **Replicate forward refs only; never replicate reverse buffers.** [V]
7. **After a load, 94 systems override `OnGameLoaded`** and, e.g., `Game.Net.GeometrySystem` processes *all* edges/nodes instead of only `Updated` ones on the first frame (`Game/Game.Net/GeometrySystem.cs:3382-3406`). This is the game's own "recompute every derived cache" path and is the convergence mechanism a snapshot-joining client gets for free. [V]
8. **Simulation is not deterministic across machines out of the box**: `RandomSeed.Next()` draws from a static `Unity.Mathematics.Random` seeded with `DateTime.Now.Ticks` (`Game/Game.Common/RandomSeed.cs:8-17`). GPU-resident authoritative state (water, snow, terrain heightmaps) is read back via `AsyncGPUReadback` for saving (`Game/Game.Simulation/WaterSystem.cs:1030-1096`, `SnowSystem.cs:244-287`). Lockstep without heavy patching is unrealistic; host-authoritative state replication is the right model. [V for the code; I for the conclusion]
9. **Prefab entities carry a small amount of city state**: unlock state (`Locked` enable bit; `Game/Game.Prefabs/Locked.cs:8` is `IEmptySerializable`+`IEnableableComponent`, saved via enable bits), placed signature buildings (`PlacedSignatureBuildingData`), and per-service collected budget data (`CollectedCityServiceBudgetData/FeeData/UpkeepData`, copied across by `ResolvePrefabsSystem.CopyBudgetDataJob`, `ResolvePrefabsSystem.cs:169-245`). Everything else on prefab entities is rebuilt from assets. [V]

---

## 1. Prefabs

### 1.1 PrefabBase and how a prefab becomes an entity [V]

- `PrefabBase : ComponentBase` is a Unity `ScriptableObject` with a list of `ComponentBase` (`Game/Game.Prefabs/PrefabBase.cs:16-27`). Each `ComponentBase` contributes:
  - `GetPrefabComponents(HashSet<ComponentType>)` - components on the **prefab entity**. `PrefabBase` itself adds `PrefabData`, `LoadedIndex`, and `ModPrerequisiteData` if the asset has a `platformID` (`PrefabBase.cs:362-370`).
  - `GetArchetypeComponents(HashSet<ComponentType>)` - components on **instance entities**. `PrefabBase` adds `PrefabRef` (`:372-375`).
- Prefab initialisation systems bake the instance archetypes into prefab-entity components, e.g. `ObjectData.m_Archetype` (`Game/Game.Prefabs/ObjectData.cs:8`), `NetData.m_NodeArchetype/m_EdgeArchetype` (`NetData.cs:9-11`), `AreaData.m_Archetype`, `RouteData.m_RouteArchetype/m_WaypointArchetype/m_ConnectedArchetype/m_SegmentArchetype` (`RouteData.cs:10-16`), `NetLaneArchetypeData.*` (`NetLaneArchetypeData.cs:8-22`), `ZoneBlockData.m_Archetype`. These run in `SystemUpdatePhase.PrefabUpdate` (`SystemOrder.cs:1005-1027`).
- Consequence [I]: given the prefab, a client can create an instance with the exact same archetype as the host by using the prefab's baked archetype; it does not need the host's archetype.

### 1.2 PrefabSystem (`Game/Game.Prefabs/PrefabSystem.cs`) [V]

| Member | Lines | Meaning |
|---|---|---|
| `List<PrefabBase> m_Prefabs` | 44 | dense list; `PrefabData.m_Index` indexes it |
| `Dictionary<PrefabBase, Entity> m_Entities` | 52 | prefab object -> prefab entity |
| `Dictionary<PrefabID, int> m_PrefabIndices` | 62 | **stable lookup** PrefabID -> index |
| `AddPrefab` | 88-189 | creates entity from `GetPrefabComponents` (+`UnlockRequirement`/`Locked` if unlockable, +`Created`/`Updated`), sets `PrefabData.m_Index = m_Prefabs.Count` (146-150), registers PrefabID, legacy mod ID and all `ObsoleteIdentifiers` (151-178). Duplicate IDs only log a warning; first registration wins (152-155, 169-172). |
| `RemovePrefab` | 191-267 | adds `Deleted`, **swap-removes** - the last prefab takes the removed index (225-253); removed entity gets `m_Index = -1000000000` (254). |
| `DuplicatePrefab` | 269-275 | `Clone` + strip `ObsoleteIdentifiers` + `AddPrefab` (runtime-created prefab). |
| `TryGetLegacyModPrefabID` | 277-292 | for numeric platformIDs also registers `(type,name,Hash(int id))`. |
| `GetOrCreateContentPrefab(modId)` | 330-340 | code-created `ContentPrefab` named `"Mod:"+modId` with `ModRequirement`. |
| `IsAvailable` | 484-520 | prefab is skipped if its mod content prefab or `ContentPrerequisite` (DLC) is unavailable. |
| `GetPrefab<T>(PrefabData)` | 643-646 | `m_Prefabs[m_Index]` |
| `UpdatePrefab/UpdatePrefabs` | 306-309, 766-825 | prefab edit = new prefab entity + `ReplacePrefabSystem.ReplacePrefab(old,new)`. |
| `Serialize` | 827-865 | writes the `PrefabID` of every prefab entity whose `PrefabData` is **enabled** (= referenced, see 1.4), plus obsolete IDs. |
| `Deserialize` | 867-911 | reads `PrefabID`s; resolvable ones become `LoadedIndexData(entity, savedIndex)`; unresolved ones go to `m_LoadedObsoleteIDs`. |
| `TryGetPrefab(PrefabID)` | 939-948 | the **only** stable lookup API. |
| `GetLoadedObsoleteID` | 950-957 | unresolved -> `PrefabID("[Missing]","[Missing]")` fallback. |

`PrefabSystem` itself is an `IDefaultSerializable` system (`:24`).

Load order: built-in prefabs come from `assetDatabase.GetAssets(SearchFilter<PrefabAsset>)` and are added in enumeration order in batches (`Game/Game.SceneFlow/GameManager.cs:1925-1966`); mod/playset prefabs are added/removed dynamically (`GameManager.cs:1595-1640`); dependency prefabs are added by `PrefabInitializeSystem` (`Game/Game.Prefabs/PrefabInitializeSystem.cs:110`). [V] Therefore `m_Index` depends on asset enumeration and mod set and **must never be put on the wire**. [I, strongly supported]

### 1.3 PrefabID - the cross-machine identity [V]

`Game/Game.Prefabs/PrefabID.cs`:

The constructor (lines 15-36) stores the prefab's type name and object name. The hash is the override hash when a
valid one is passed. Otherwise, for a prefab loaded from an asset, it is the hash of the asset's platform id (the
Paradox Mods id) when the asset has one, else the asset's GUID. A prefab without an asset keeps an invalid hash.

- Equality = type **and** name **and** hash (`:45-52`). `GetHashCode` uses name and hash only (`:54-57`).
- Serialization writes `type`, `name`, `hash`; the hash is only read if the save has `FormatTags.PrefabIDHash` (`:84-105`, `Game/Game/FormatTags.cs:40`).
- `platformID` is set from the Paradox Mods id (`Colossal.IO.AssetDatabase/Colossal.IO.AssetDatabase/ParadoxModsDataSource.cs:117`). So **all prefabs from one PDX mod share the same hash** (hash of the mod id, not of the asset), and identity within the mod is type+name.
- `PrefabBase.asset` is assigned whenever a prefab is loaded from a `PrefabAsset` (`Colossal.IO.AssetDatabase/Colossal.IO.AssetDatabase/PrefabAsset.cs:405, 544, 597`). Built-in game prefabs are loaded from the game asset database, so they carry the asset GUID as hash. [V for assignment; I that every built-in prefab goes this route]
- Prefabs created in code without an asset (e.g. `DuplicatePrefab`, `ContentPrefab "Mod:<id>"`, third-party mods: `thirdparty/Anarchy/Anarchy/ExtendedRoadUpgrades/UpgradesManager.cs:191` calls `prefabSystem.AddPrefab(fencePrefab)`) have an **invalid hash**: identity is type+name only. Third-party mods commonly look prefabs up as `new PrefabID("StaticObjectPrefab", name)` (`thirdparty/Tree_Controller/Tree_Controller/Domain/CustomSetRepository.cs:140`).
- Renames are handled by `ObsoleteIdentifiers` (`PrefabBase.MarkCurrentPrefabObsolete`, `PrefabBase.cs:80-102`; `PrefabIdentifierInfo.GetPrefabID`, `Game/Game.Prefabs/PrefabIdentifierInfo.cs`), all registered into `m_PrefabIndices`.

**Recommended wire identity for a prefab** [I]: the full `PrefabID` triple (type, name, hash), resolved with `PrefabSystem.TryGetPrefab(PrefabID)` on the receiver. Build a per-session **prefab dictionary** at join time: host sends `(netPrefabIndex -> PrefabID)` for every prefab referenced by replicated state (exactly what `PrefabSystem.Serialize` does for saves); afterwards use the compact `netPrefabIndex` in deltas. Clients report unresolved IDs before gameplay starts (mod/asset mismatch gate).

### 1.4 How saves persist prefab references [V]

Save (`SystemUpdatePhase.Serialize`, `SystemOrder.cs:731-737`):

1. `BeginPrefabSerializationSystem` (`Game/Game.Serialization/BeginPrefabSerializationSystem.cs`) **disables `PrefabData` on every prefab** (`BeginPrefabSerializationJob`, `:30-41`, line 38), then runs `SystemUpdatePhase.PrefabReferences` where `PrimaryPrefabReferencesSystem`/`SecondaryPrefabReferencesSystem` call `PrefabReferences.Check` on every prefab reference, which marks the prefab as referenced (`Game/Game.Serialization/PrefabReferences.cs:61-83`). Prefabs with gameplay state are force-marked: locked state changed, placed signature buildings, collected budgets (`CheckSavedPrefabsJob`, `:50-101`).
2. `SetPrefabDataIndexJob` compacts `PrefabData.m_Index` of the referenced (enabled) prefabs to `0..n-1` (`:110-192`).
3. `SerializerSystem` serializes with `requireEnabled = PrefabData` (`SerializerSystem.cs:102`), i.e. **only referenced prefab entities are written**, and instance `PrefabRef.m_Prefab` is written as an `Entity` remapped through the entity table.
4. `PrefabSystem.Serialize` writes the `PrefabID` list in the same order.

Load (`SystemUpdatePhase.Deserialize`, `SystemOrder.cs:798-802`):

1. `EntityDeserializer` recreates the saved prefab entities (stripped archetype, `PrefabData.m_Index = saved index`, **no `LoadedIndex`** because `LoadedIndex` is not serializable, `Game/Game.Prefabs/LoadedIndex.cs:6`).
2. `PrefabSystem.Deserialize` + `UpdateLoadedIndices` put `LoadedIndex{savedIndex}` onto the matching *live* prefab entity (`PrefabSystem.cs:920-937`).
3. `ResolvePrefabsSystem.OnUpdate` (`ResolvePrefabsSystem.cs:458-553`) builds `savedIndex -> live prefab entity` (`FillLoadedPrefabsJob` 37-86, `CheckActualPrefabsJob` 88-165), restores per-prefab `Locked` state and `PlacedSignatureBuildingData` for `LoadGame`, copies collected budget data (169-245), remaps zone types (252-338), runs the `PrefabReferences` phase in loading mode so every `PrefabRef` etc. is rewritten to the live prefab entity, then:
   - unresolved loaded prefab entities get `LoadedIndex`, a negative `m_Index`, disabled `PrefabData`, and are registered as **obsolete placeholders** (`AddObsoleteID`, 532-545) - instances referencing a missing prefab survive as placeholders;
   - all other loaded prefab entities are destroyed (546).

Components that hold prefab references and get remapped (= every place a replication layer must translate prefab identity) [V]:

- Primary (`Game/Game.Serialization/PrimaryPrefabReferencesSystem.cs:31-276, 392-400`): `PrefabRef`, `UnderConstruction.m_NewPrefab`, `CompanyData.m_Brand`, `Policy.m_Policy`, `ServiceBudgetData.m_Service`, `VehicleModel.m_PrimaryPrefab/m_SecondaryPrefab`, `EditorContainer`, `Chirp` (+`ChirpEntity`), `SubReplacement`.
- Secondary (`SecondaryPrefabReferencesSystem.cs:19-193, 273-279`): prefab-to-prefab references inside `SpawnableBuildingData`, `PlaceholderBuildingData`, `ServiceObjectData`, `NetLaneData`, `TransportLineData`, `ContentPrerequisiteData`, `ModPrerequisiteData` (only relevant if prefab entities themselves were replicated - they should not be).
- System-level prefab references: `CityConfigurationSystem.defaultTheme` and `m_RequiredContent` (`Game/Game.City/CityConfigurationSystem.cs:266-273`), `ClimateSystem.m_CurrentClimate` and weather-effect lists (`Game/Game.Simulation/ClimateSystem.cs:529-538`), `GameModeSystem` writes a `PrefabID` directly (`Game/Game.Prefabs.Modes/GameModeSystem.cs:111-120`), `TerrainMaterialSystem`/`WaterRenderSystem` render-settings prefabs (`PrefabSystem.cs:426-463`).

### 1.5 Prefab entities vs instance entities [V]

| | Prefab entity | Instance entity |
|---|---|---|
| Marker | `PrefabData` (enableable, `PrefabData.cs:7`) + `LoadedIndex` | `PrefabRef{m_Prefab}` (`PrefabRef.cs:6`) |
| Created by | `PrefabSystem.AddPrefab` from assets/code | tools (`Apply*System`, `SystemOrder.cs:714-720`), generators (lanes, sub-objects, blocks), simulation (citizens, vehicles, events) |
| Persisted? | only if referenced; data discarded on load except: `Locked` enable bit, `PlacedSignatureBuildingData`, `CollectedCityServiceBudgetData/FeeData/UpkeepData` | yes, if it matches the serializer query |
| Replicate? | **No** (identity only, via PrefabID) | yes, per tables below |

Note: many prefab components are marked serializable (168 in `Game.Prefabs`, e.g. `BuildingData`, `NetData`) and are written into saves, but `ResolvePrefabsSystem` destroys those loaded prefab entities (`:546`), so that data is effectively unused for live prefabs. [V]

### 1.6 Modded and asset-pack prefabs [V]

- PDX mod assets: hash = `Hash(platformID)`; prefab entity gets `ModPrerequisiteData{m_ContentPrerequisite = ContentPrefab("Mod:"+id)}` (`PrefabBase.cs:377-392`); availability gated by `IsAvailable` (`PrefabSystem.cs:484-520`). `isSubscribedMod` = asset lives in `AssetDatabase<ParadoxMods>` (`PrefabBase.cs:48`).
- Local/packaged assets: hash = asset GUID; `isBuiltin` is true only for game-database assets (`PrefabBase.cs:36-46`).
- `CityConfigurationSystem` records used mods (`usedMods`) in `SaveGame` (`CityConfigurationSystem.cs:326-337`) - a ready-made list for the join-time compatibility check.
- Mod **components** are persisted automatically: any `ISerializable`/`IEmptySerializable` type from another assembly is added to the serializer query (`ComponentSerializerLibrary.cs:53-56, 76-79`). Third-party examples: `Anarchy` `PreventOverride`/`TransformRecord`, `Tree_Controller` `DeciduousData`/`NoTreeGrowth`, `Water_Features` `AutomatedWaterSource`, `ZoningToolkit` `ZoningInfo` (`thirdparty/...`). [V] A generic replication layer should therefore treat "external-assembly serializable components" as replicable state too, or at least detect them and require identical mod sets. [I]

---

## 2. Serialization machinery that defines "persistent"

### 2.1 Selection query [V]

`SerializerSystem.CreateQuery` (`Game/Game.Serialization/SerializerSystem.cs:171-214`) - entity is saved if it has **any** of:
`LoadedIndex` (prefabs), `PrefabRef` (all instances), `ElectricityFlowNode`, `ElectricityFlowEdge`, `WaterPipeNode`, `WaterPipeEdge`, `ServiceRequest`, `Game.Simulation.WaterSourceData`, `Game.City.City`, `SchoolSeeker`, `JobSeeker`, `CityStatistic`, `ServiceBudgetData`, `FloodCounterData`, `CoordinatedMeeting`, `LookingForPartner`, `TimeData`, `MeshColorPalette`, plus every external-assembly serializable type; and has **none** of `NetCompositionData`, `EffectInstance`, `LivePath`, `Temp`, `Deleted`.

So: all prefab instances, all utility flow-graph entities, all service requests, the city entity, the budget entity, time singleton, statistics entities and some agent-seeker entities. Tool previews (`Temp`) never.

### 2.2 Component persistence classes [V]

`ComponentSerializerLibrary.Initialize` (`ComponentSerializerLibrary.cs:25-106`):

- `IEmptySerializable` -> `EmptyComponentSerializer` (presence only) or enableable variant (`EnableableEmptyComponentSerializer`, saves enable bits) (50-71).
- `ISerializable` -> `ComponentDataSerializer` / `BufferElementDataSerializer` / `SharedComponentDataSerializer` or their enableable variants (73-91). `ISerializeAsEnabled` suppresses enable-bit saving.
- `FormerlySerializedAs` maps old type names (113-121) - the save format identifies component types by name.
- Unknown types in a save become `ObsoleteComponentSerializer` (skipped) (`EntityDeserializer.cs:336-373`).

Archetypes are **stripped** to their serializable components (`EntitySerializer.cs:113-136` enqueues non-pure archetypes; `:479-496` creates the stripped archetype). Components that are neither are therefore **not even present** after load and must be re-added by `RequiredComponentSystem` (runs right after `ResolvePrefabsSystem`, `SystemOrder.cs:803`; e.g. re-adds `UpdateFrame` at `Game/Game.Serialization/RequiredComponentSystem.cs:1627, 1666`, `Native` at `:1089`).

### 2.3 Inventory of serializable component types (Game.dll) [V]

Counts of structs implementing `ISerializable`/`IEmptySerializable`, by namespace: Buildings 89, Net 82, Simulation 67, Vehicles 59, Routes 43, Objects 41, Citizens 41, Events 31, Areas 28, Companies 28, Creatures 22, City 18, Rendering 18, Zones 8, Common 9, Pathfind 6, Triggers 6, Agents 5, Notifications 4, Tools 3, UI 2+1, Economy 2, Policies 1, Effects 1, Achievements 1, Prefabs 168. (Full per-type lists are reproduced inside the category tables below; `C` component, `B` buffer, `S` shared; suffix `S`=data saved, `E`=presence only, `en`=enableable.)

### 2.4 Entity remapping [V]

- `CreateEntityTable` sizes a table to `HighestEntityIndex()+1` and maps each saved entity to a dense index ordered by stripped archetype (`EntitySerializer.cs:534-580`, `FillEntityTableJob` 238-281).
- Every `writer.Write(Entity)` goes through this table (`EntityWriterData` built with `m_EntityTable`, `:600, 634`); on read, `EntityReaderData` maps back to the freshly created entities (`EntityDeserializer.cs:492-554`).
- Hidden entity indices: `PathNode` stores `owner.Index` in a `ulong` and serializes it by temporarily constructing an `Entity` (`PathNode.cs:133-149`). `Lane.m_StartNode/m_MiddleNode/m_EndNode` are `PathNode`s (`Game/Game.Net/Lane.cs:8`), so **lanes carry owner entity indices**; any snapshot/delta must re-encode these. [V]

### 2.5 Load pipeline [V]

`SystemOrder.cs:738-898` (Deserialize phase), in order: `PreDeserialize<...>` for search trees/batches/UI (739-795) -> `ClearSystem`, `GameModeSystem` -> `DeserializationBarrier` -> `SerializerSystem` (entities + systems) -> `ReadSystem` -> `FilterLoadedSystem` -> `ResolvePrefabsSystem` -> `RequiredComponentSystem` -> **reverse-reference rebuilders** (804-842, table in section 4) -> fix-ups (`LaneDirectionNetObjectSystem` ... `CitizenSystem`, 843-854) -> `RandomLocalizationSystem`, `ResetUpdateGroupSizesSystem`, `ResetBuildOrderSystem`, `ResetUnlockRequirementSystem`, `InitializeObsoleteSystem`, `ResidentPseudoRandomSystem` (855-860) -> `PostDeserialize<T>` for 30+ systems (861-898).

Then `GameSystemBase.OnGameLoaded` (`Game/Game/GameSystemBase.cs:76, 119`) fires for 94 overriding systems; geometry/lane/composition systems use it to run on **all** entities once (`Game.Net.GeometrySystem.cs:3386-3406`, same pattern in `CompositionSelectSystem`, `EdgeMappingSystem`, `LaneOverlapSystem`, `LaneReferencesSystem`, `NetComponentsSystem`, `Game.Areas.GeometrySystem`, `SurfaceExpandSystem`, `LotHeightSystem`, `SegmentCurveSystem`, `RoutePathReadySystem`, ...).

---

## 3. Lifecycle and generic components [V]

| Component | File | Persist | Role |
|---|---|---|---|
| `Owner{m_Owner}` | `Game/Game.Common/Owner.cs:6` | S | **the** parent link for sub-entities (sub-objects, sub-nets, lanes, sub-areas, blocks, waypoints/segments, upgrades, vehicles->owner building/household, icons) |
| `Created`, `Updated`, `BatchesUpdated`, `Deleted` | `Game/Game.Common` | not saved | one-frame change tags driving the modification pipeline (`SystemOrder.cs:123-218`) |
| `Temp` (`Game.Tools`) | | not saved, query-excluded | tool preview entity |
| `Native` | `Game/Game.Common/Native.cs:8` | E | "belongs to nature/map, not to city" - e.g. unpurchased map tiles; managed by `NativeSystem` |
| `Overridden` | `Game.Common` | E | object hidden/overridden by placement collision |
| `Destroyed{m_Event,m_Cleared}` | `Game/Game.Common/Destroyed.cs:6` | S | collapsed/burnt state |
| `PseudoRandomSeed{m_Seed}` | `Game/Game.Common/PseudoRandomSeed.cs:7` | S | per-entity deterministic variation (mesh variant, colour, names). **Must be replicated** for visual identity. |
| `RandomLocalizationIndex` | `Game.Common` | BS | chosen localized random name variant |
| `UpdateFrame{m_Index}` (shared) | `Game/Game.Simulation/UpdateFrame.cs:6` | SS | which of 16 sub-frames the entity simulates on; assigned by `UpdateGroupSystem` (`UpdateGroupSystem.cs:316-370`, `SystemOrder.cs:214`) |
| `Target`, `PointOfInterest`, `TimeData` | `Game.Common` | S | |
| `PrefabRef` | `Game/Game.Prefabs/PrefabRef.cs:6` | S | prefab of instance |

---

## 4. Instance entity categories

Notation per row: **ID** = identifying (tag) components; **Fwd refs** = forward references that are authoritative; **Rev** = reverse buffers derived from fwd refs (rebuilt on load by the named `Game.Serialization.*` system, in `SystemUpdatePhase.Deserialize`); **Auth data** = persisted data the host owns; **Derived** = `IEmptySerializable` or non-serialized caches rebuilt locally.

### 4.1 Net: nodes, edges, compositions, aggregates [V]

| Kind | ID | Fwd refs | Rev (rebuilder) | Auth data (saved) | Derived (rebuilt) |
|---|---|---|---|---|---|
| Node | `Game.Net.Node{m_Position,m_Rotation}` (`Node.cs:8`, Stride) + `PrefabRef` | `Owner` (if part of a building/sub-net) | `ConnectedEdge` (BE, `ConnectedEdge.cs:8`; `ConnectedEdgeSystem`), owner's `SubNet` (BE, `SubNet.cs:8`; `SubNetSystem`) | `Game.Net.Elevation{float2}`, `Upgraded{CompositionFlags}` (`Upgraded.cs:7`), `TrafficLights`, `Roundabout`(E), `Standalone`(E), `Orphan`(E), `OutsideConnection`, `LocalConnect`(E), `Marker`(E), `NetCustomColorVariation`, `Pollution`, `NetCondition` | `NodeGeometry` (E, `NodeGeometry.cs:7`), `Composition`... via `Game.Net.GeometrySystem`/`CompositionSelectSystem` |
| Edge | `Edge{m_Start,m_End}` (`Edge.cs:6`) + `Curve{Bezier4x3,m_Length}` (`Curve.cs:9`) + `PrefabRef` | `Edge.m_Start/m_End` (node entities), `Owner` | node `ConnectedEdge`, owner `SubNet` | `Game.Net.Elevation`, `Upgraded`, `BuildOrder{m_Start,m_End}` (`BuildOrder.cs:6`), `Road{traffic flow..., m_Flags}`, `Fixed{m_Index}`, `ConnectedNode` (BS, mid-edge nodes, `ConnectedNode.cs:8`), `Density`, `LandValue`, `ServiceCoverage`(BS), `ResourceAvailability`(BS), `NetCondition`, `Pollution`, track/waterway tags (E) | `EdgeGeometry`, `StartNodeGeometry`, `EndNodeGeometry` (E), `Composition{m_Edge,m_StartNode,m_EndNode}` (E, `Composition.cs:6`) pointing to runtime `NetCompositionData` entities (query-excluded), `EdgeMapping`(E), `LabelPosition/LabelVertex`(BE), `ArrowPosition`(BE) |
| Composition entity | `NetCompositionData` | - | - | **never saved** (`SerializerSystem.cs:203`) | recreated by `CompositionSelectSystem` (`SystemOrder.cs:139`) |
| Aggregate (street) | `Aggregate` (E, `Aggregate.cs:8`) + `PrefabRef` | `AggregateElement{edge}` (BS, `AggregateElement.cs:8`) | edge `Aggregated` (E, `Aggregated.cs:6`; `AggregatedSystem`) | element list; custom street name lives in `NameSystem` keyed by aggregate entity | label geometry |

Notes:
- `BuildOrder` counters are global; `ResetBuildOrderSystem` compacts them on load (`Game/Game.Serialization/ResetBuildOrderSystem.cs:8-45`). Host must own the counter. [V]
- `Building.m_RoadEdge` links buildings to edges (section 4.3).

### 4.2 Net lanes [V]

| ID | Fwd refs | Rev | Auth data | Derived |
|---|---|---|---|---|
| `Lane{PathNode m_StartNode,m_MiddleNode,m_EndNode}` (`Lane.cs:8`) + `Curve` + `PrefabRef`; subtype tags `CarLane`, `TrackLane`, `PedestrianLane`, `ParkingLane`, `ConnectionLane`, `UtilityLane`, `GarageLane`, `HangingLane`, `AreaLane`, `EdgeLane`, `NodeLane`, `MasterLane`, `SlaveLane`, `SecondaryLane`(E) | `Owner` (edge/node/building/area), `PathNode` owner indices | owner `SubLane` (BE, `SubLane.cs:9`; `SubLaneSystem`), `LaneObject` (BE, objects on lane; `LaneObjectSystem` from `CarCurrentLane`/`ParkedCar`/`HumanCurrentLane`/`BlockedLane`...), `LaneOverlap`(BE) | flow/condition state: `LaneFlow`, `LaneCondition`, `LaneSignal`, `LaneReservation`, `ParkingLane` occupancy, `GarageLane`, `LaneColor`(E) | `LaneGeometry`(E), overlaps |

- Lanes are **generated** by `Game.Net.LaneSystem` (`SystemOrder.cs:155`) from owner geometry whenever the owner is `Updated`; it reuses existing lane entities keyed by `LaneKey(Lane, prefab, Master/Slave flags)` (`Game/Game.Net/LaneSystem.cs:32-67, 233-244`). [V]
- But lanes are *also* persisted and referenced by vehicles, creatures, paths and routes (`CarCurrentLane.m_Lane`, `ParkedCar.m_Lane`, `PathElement.m_Target`, `AccessLane.m_Lane`, `RouteLane.m_StartLane/m_EndLane`, `PathTargets`). [V]
- Design consequence [I]: lanes are **derived in topology** (clients can generate them deterministically from replicated nets/buildings) but **referenced by identity** (agents). A stable lane identity = (stable owner NetId, `Lane` lane-index bits of the three `PathNode`s, prefab, master/slave flag) - i.e. the game's own `LaneKey` minus the owner entity index. Lane simulation state (signals, flow) is host-owned.

### 4.3 Objects: buildings, props, trees, decals, sub-objects [V]

| Kind | ID | Fwd refs | Rev | Auth data | Derived |
|---|---|---|---|---|---|
| Any object | `Game.Objects.Object` (E, `Object.cs:8`) + `Transform{m_Position,m_Rotation}` (`Transform.cs:8`) + `PrefabRef`; `Static` (E) for non-moving | `Owner` (if sub-object), `Attached{m_Parent,m_OldParent,m_CurvePosition}` (`Attached.cs:6`) | owner/parent `SubObject` (BE, `SubObject.cs:8`; `Game.Serialization.SubObjectSystem` from `Owner` + `Attached`) | `Game.Objects.Elevation`, `Relative`, `NetObject`, `Aligned`, `Surface` (wetness/snow), `Damaged`, `Destroyed`, `PseudoRandomSeed`, `UniqueObject`(E), `Native`, `Overridden`, `CustomMeshColor` (BSen; player-set colour via `Game/Game.UI.InGame/VisualCustomizeSection.cs`) | `ObjectGeometry`(E), `Color`(E, `Color.cs:6`), `CullingInfo`, `MeshBatch`, `MeshColor`, `InterpolatedTransform`, all `Game.Rendering` buffers (E) |
| Building | `Game.Buildings.Building{m_RoadEdge,m_CurvePosition,m_OptionMask,m_Flags}` (`Building.cs:6`) | `m_RoadEdge` -> edge | edge `ConnectedBuilding` (BE; `ConnectedBuildingSystem`), `Renter` (BE, `Renter.cs:6`; `RenterSystem` from `PropertyRenter`/`TouristHousehold`/`HomelessHousehold`), `Student`(BE), `SpawnLocationElement`(BE), `InstalledUpgrade` (BE, `InstalledUpgrade.cs:8`; `InstalledUpgradeSystem` from `ServiceUpgrade`+`Owner`) | `UnderConstruction{m_NewPrefab,m_Progress,m_Speed}`, `BuildingCondition`, `Abandoned`, `Condemned`, service-facility components (`Hospital`, `School`, `PoliceStation`, `GarbageFacility`, ...), `Efficiency`(BS), `BuildingModifier`(BS), `Occupant`(BS), `Patient`(BS), `Signature`(E), `Policy`(BS, `BuildingPrefab` adds it), `Game.Economy.Resources`(BS), property components | rendering, `ConnectedBuilding` |
| Service upgrade / extension | `ServiceUpgrade` (E) or `Extension{m_Flags}` + `Owner` | `Owner` -> main building | main building `InstalledUpgrade` | flags | |
| Tree | `Tree{m_State,m_Growth}` (`Tree.cs:6`) + `Plant{m_Pollution}` | | | growth state (simulated) | |
| Prop / decal | plain static object; decal-ness is a prefab mesh property (`Game/Game.Prefabs/DecalProperties.cs`) - no instance-specific component [I] | `Owner` if part of building | | `Transform`, seed | |
| Secondary object (street lights, traffic signs, utility poles) | `Secondary` (E, `Secondary.cs:8`) + `Owner` | `Owner` | owner `SubObject` | persisted entity | **regenerated** by `SecondaryObjectSystem` on owner update (`SystemOrder.cs:183`; `Game/Game.Objects/SecondaryObjectSystem.cs:117-340`) |
| Placeholder / spawn objects | `Placeholder`(E), `SpawnLocation`, `ActivityLocation`(E) | | | | |

- Sub-objects of buildings/nets (fences, props placed by the prefab) are created by `Game.Objects.SubObjectSystem` on owner `Updated` (`SystemOrder.cs:131`; `Game/Game.Objects/SubObjectSystem.cs:557, 3949-3968`). Player-placed sub-objects are distinguishable only by data, not by tag [I]; replicate all owned objects that the host has but treat prefab-generated ones as derived-with-identity like lanes.

### 4.4 Areas: lots, districts, surfaces, map tiles [V]

All areas: `Game.Areas.Area{m_Flags}` (`Area.cs:6`) + `Game.Areas.Node` buffer (BS, `Node.cs:9` - **the authoritative polygon**) + `PrefabRef`. Derived: `Geometry` (E, `Geometry.cs:8`), `Triangle` (BE, `Triangle.cs:9`), `Expand`(BE), `LabelExtents/LabelVertex`(BE), `Batch`(E), rebuilt by `Game.Areas.GeometrySystem` (`SystemOrder.cs:124`, `OnGameLoaded`). Owner `SubArea` (BE, `SubArea.cs:8`) rebuilt by `SubAreaSystem` from `Area`+`Owner`.

| Kind | Extra ID | Fwd refs | Auth data |
|---|---|---|---|
| Lot (building lot) | `Game.Areas.Lot` (E, `Lot.cs:8`) | `Owner` -> building | nodes |
| District | `District{m_OptionMask}` (`District.cs:6`) | - | nodes, `DistrictModifier`(BS, derived from policies but saved), `Policy`(BS via `DistrictPrefab`); buildings/edges point back via `CurrentDistrict{m_District}`, `BorderDistrict{m_Left,m_Right}`, `ServiceDistrict`(BS on service buildings) |
| Surface (decorative/ground areas) | `Game.Areas.Surface` (E, `Surface.cs:8`) | `Owner` (often building) | nodes |
| Map tile | `MapTile` (E, `MapTile.cs:8`) | - | nodes; **ownership = absence of `Native`** (start tiles: `MapTileSystem.cs:142, 229-235`; purchase: `Game/Game.Simulation/MapTilePurchaseSystem.cs:350`); `MapFeatureElement`(BS) natural resources |
| Space / navigation / hangaround | `Space`(E), `Navigation`(E), `HangaroundLocation`(E) | `Owner` | nodes |
| Extractor / storage area | `Extractor{...}`, `Storage{m_Amount,m_WorkAmount}`, `WoodResource`(BS) | `Owner` -> building | simulated amounts |

`MapTileSystem` additionally serializes `m_StartTiles` (Entity list) (`MapTileSystem.cs:161-186`); tiles are generated on a 23x23 grid (`:231`).

### 4.5 Zones: blocks and cells [V]

| ID | Fwd refs | Rev | Auth data | Derived |
|---|---|---|---|---|
| `Block{m_Position,m_Direction,m_Size}` (`Block.cs:8`) + `PrefabRef` + `CurvePosition` + `ValidArea{int4}` + `Game.Zones.BuildOrder{m_Order}` | `Owner` -> road edge | edge `SubBlock` (BE; `SubBlockSystem`) | `Cell` buffer (BS, `Cell.cs:7`: `m_State`, **`m_Zone` (ZoneType index)**, `m_Height`) - zoning painted by players + occupancy flags; `VacantLot` (BS) | block geometry itself is generated by `BlockSystem` from edges (`SystemOrder.cs:156`), `CellCheckSystem` updates cell flags (`:215`) |

- `ZoneType.m_Index` is a per-session index (`ZoneSystem.cs:176-179`). On the wire, translate to zone prefab `PrefabID`. [V/I]
- Blocks are derived from roads but carry player data (zone painting per cell). Identity of a block = (owner edge NetId, side/position) [I]; zoning can be replicated as "cell paint" commands rather than raw cell buffers.

### 4.6 Routes: lines, stops, waypoints, segments [V]

| Kind | ID | Fwd refs | Rev | Auth data | Derived |
|---|---|---|---|---|---|
| Route / transport line | `Route{m_Flags,m_OptionMask}` + `TransportLine{m_VehicleRequest,m_VehicleInterval,m_UnbunchingFactor,m_Flags,m_TicketPrice}` (`TransportLine.cs:7`) / `WorkRoute`(E) + `PrefabRef` | - | `RouteWaypoint` (BE; `RouteWaypointSystem` from `Waypoint`+`Owner`), `RouteSegment` (BE; `RouteSegmentSystem`), `RouteVehicle`(BE; `RouteVehicleSystem`), owner `SubRoute`(BE; `SubRouteSystem`) | `Game.Routes.Color{Color32}`, `RouteNumber`, `VehicleModel`(BS, prefab refs), `RouteModifier`(BS), `Policy`(BS via `TransportLinePrefab`), `HiddenRoute`(E) | |
| Waypoint | `Waypoint{m_Index}` + `Position` | `Owner` -> route, `Connected{m_Connected}` -> stop, `AccessLane`, `RouteLane` (lanes) | stop `ConnectedRoute` (BE; `ConnectedRouteSystem`) | `WaitingPassengers` | |
| Segment | `Segment{m_Index}` | `Owner` -> route | | `PathElement`(BS), `PathTargets` | `CurveElement` (BE, path curve) via `RoutePathSystem`/`SegmentCurveSystem` |
| Transport stop (object) | `TransportStop{...}` + stop-type tags (`BusStop`(E), `TrainStop`(E), ...) on an object | | `ConnectedRoute` | `BoardingVehicle`, `WaitingPassengers` | |

`TransportLineSystem` serializes `m_MaxTransportSpeed`; `RoutePathSystem` serializes its lazy update set (`Game/Game.Routes/RoutePathSystem.cs:351`). [V]

### 4.7 Agents: citizens, households, companies [V]

| Kind | ID | Fwd refs | Rev | Auth data |
|---|---|---|---|---|
| Citizen | `Citizen{m_PseudoRandom,m_State,m_WellBeing,m_Health,...,m_BirthDay}` (`Citizen.cs:9`) + `PrefabRef` + age tag (`Child`/`Teen`/`Adult`/`Elderly`, E) | `HouseholdMember{m_Household}`, `Worker{m_Workplace}`, `Game.Citizens.Student{m_School}`, `CurrentBuilding`, `CurrentTransport{m_CurrentTransport}` (-> creature), `TravelPurpose`, `HealthProblem`, `Criminal` | household `HouseholdCitizen` (BE; `HouseholdCitizenSystem`), company `Employee` (BS - saved), building `Student` (BE; `StudentSystem`), creature `Resident` <-> `CurrentTransport` (`ResidentSystem`) | all of the above |
| Household | `Household{m_Flags,m_Resources,...}` (`Household.cs:6`) + `PrefabRef` | `PropertyRenter{m_Property,m_Rent}`, `TouristHousehold{m_Hotel}`, `HomelessHousehold{m_TempHome}`, `CommuterHousehold{m_OriginalFrom}` | building `Renter` (`RenterSystem`), `HouseholdAnimal` (BE), `OwnedVehicle` (BE; `OwnedVehicleSystem` from `Vehicle`+`Owner`) | `Game.Economy.Resources`(BS), `HouseholdNeed`, `TaxPayer`, `MovingAway`, `PropertySeeker`(en) |
| Pet | `HouseholdPet{m_Household}` | | household `HouseholdAnimal` | |
| Company | `CompanyData{m_RandomSeed,m_Brand}` (`CompanyData.cs:7`) + type tag (`CommercialCompany`/`IndustrialCompany`/`OfficeCompany`/`ExtractorCompany`/`ProcessingCompany`/`StorageCompany`, E) + `PrefabRef` | `PropertyRenter` -> building, `m_Brand` (prefab) | building `Renter` | `WorkProvider`, `Employee`(BS, `Employee.cs`), `Resources`(BS), `TradeCost`(BS), `Profitability`, `CompanyStatisticData`, `ServiceAvailable`, `StorageTransferRequest`(BS) |
| Seekers/meetings | `JobSeeker`, `SchoolSeeker`, `LookingForPartner`(BS), `CoordinatedMeeting` | owner citizen/household | | short-lived simulation entities (in the explicit query list) |

### 4.8 Vehicles and creatures [V]

| Kind | ID | Fwd refs | Rev | Auth data | Derived |
|---|---|---|---|---|---|
| Vehicle | `Game.Vehicles.Vehicle` (E) + type (`Car`, `Train`, `Aircraft`, `Watercraft`, `Bicycle`(E), role tags `PersonalCar{m_Keeper}`, `DeliveryTruck`, `PublicTransport`, `Ambulance`, ...) + `Transform` + `Moving` + `PrefabRef` | `Owner` -> building/household, `CarCurrentLane{m_Lane,m_ChangeLane,...}` (`CarCurrentLane.cs:8`), `ParkedCar{m_Lane}`, `CurrentRoute`, `Target`, `PathOwner` + `PathElement` (BS), `LayoutElement` (BS, train cars) | owner `OwnedVehicle`, delivery target `GuestVehicle` (BE; `GuestVehicleSystem`), `Controller` (E; `ControllerSystem` from `LayoutElement`), `Passenger` (BE; `PassengerSystem`), lane `LaneObject` | `Odometer`, navigation lanes (BS), `LoadingResources`(BS), state flags | `TransformFrame` (BS but resized/reinitialised by `TransformFrameSystem`), `InterpolatedTransform`, `TrainBogieFrame` |
| Creature (human/animal) | `Creature` + `Human`/`Animal` + `Resident{m_Citizen}` / `Pet{m_HouseholdPet}` / `Wildlife` / `Domesticated` | `CurrentVehicle{m_Vehicle}`, `HumanCurrentLane{m_Lane,...}`, `GroupMember` | `GroupCreature` (BE), `OwnedCreature` (BE), `Passenger` | navigation state, `Queue`(BS) | |

These are **high-churn simulation entities**; replicating them exactly is bandwidth-expensive (section 7). [I]

### 4.9 Events, notifications, requests, chirps [V]

| Kind | ID | Refs | Notes |
|---|---|---|---|
| Event | `Game.Events.Event` (E) + type (`Fire`, `Crime`, `TrafficAccident`, `Flood`, `WeatherPhenomenon`, `HealthEvent`, `CalendarEvent`, ...) + `Duration{m_StartFrame,m_EndFrame}` + `PrefabRef` | `TargetElement{m_Entity}` (BS) | targets carry back-links `OnFire{m_Event,...}`, `AccidentSite{m_Event,...}`, `InvolvedInAccident`, `Flooded`, `Destroyed.m_Event`; `HotspotFrame`(BE) rebuilt; event journal (`EventJournalEntry`, `EventJournalData`) |
| Notification icon | `Icon{m_Location,m_Priority,...}` (`Icon.cs:8`) + `PrefabRef` | `Owner` | owner `IconElement` (BE; `IconElementSystem`). Icons are **simulation outputs** but persisted. |
| Service request | `ServiceRequest` + type (`GarbageCollectionRequest`, `HealthcareRequest`, `FireRescueRequest`, ...) | `Dispatched{m_Handler}`, `ServiceDispatch`(BS on handler) | transient simulation bookkeeping (in explicit query list) |
| Chirp / life path | `Game.Triggers.Chirp` (+`ChirpEntity` BS), `LifePathEvent`, `LifePathEntry`(BS), `RadioEvent` | prefab refs (sender) | social feed; `ChirpLinkSystem` caches chirp data |
| Statistics | `CityStatistic` (BS) + `StatisticParameter` | | history series (explicit query list) |

### 4.10 City, policies, budget, time, utility graphs [V]

- **City entity**: created in `CitySystem.PostDeserialize` for `NewGame` with `Game.City.City{m_OptionMask}` (`City.cs:6`), `MilestoneLevel`, `XP`, `DevTreePoints`, `Policy`(B), `CityModifier`(B), `Loan`, `Creditworthiness`, `DangerLevel`, `TradeCost`(B), `ServiceFee`(B), `PlayerMoney`, `SpecializationBonus`(B), `Population`, `Tourism` (`Game/Game.Simulation/CitySystem.cs:70-135`). No `PrefabRef`; selected by the explicit `City` term in the query. `CitySystem` serializes only the `m_City` handle (`:54-69`). `PlayerMoney` has private `m_Money` + `m_Unlimited` (`Game/Game.City/PlayerMoney.cs:7-58`). Also `ServiceImportBudget`(B), `TaxRates`(B), `CityStatistic`-linked data.
- **Policies**: `Policy{m_Policy (prefab), m_Flags, m_Adjustment}` buffer (`Game/Game.Policies/Policy.cs:7`) on city, districts, buildings, transport lines/work routes (`BuildingPrefab`, `DistrictPrefab`, `TransportLinePrefab`, `WorkRoutePrefab`, `DefaultPolicies` add it). Changes go through `Modify` event entities handled by `Game.Policies.ModifiedSystem`; `CityModifier`/`DistrictModifier`/`BuildingModifier`/`RouteModifier` buffers are recomputed from policies by `*ModifierInitializeSystem` but are also saved. [V for files; I for "recomputed"]
- **Budget**: standalone entity with `ServiceBudgetData{m_Service (prefab), m_Budget}` buffer created by `CityServiceBudgetSystem` (`Game/Game.Simulation/CityServiceBudgetSystem.cs:834`); service fees on city `ServiceFee` buffer; tax rates in `TaxSystem.m_TaxRates` (system state, `TaxSystem.cs:276-324`); loans in `Loan`.
- **Time**: `TimeData{m_FirstFrame,m_StartingYear,...}` singleton created by `TimeSystem.PostDeserialize` (`Game/Game.Simulation/TimeSystem.cs:69-73`); the clock itself is `SimulationSystem.frameIndex` (`SimulationSystem.cs:135-153`).
- **Utility flow graphs**: `ElectricityFlowNode`(E)/`ElectricityFlowEdge`, `WaterPipeNode`/`WaterPipeEdge`, `*BuildingConnection`, `*NodeConnection`, `*ValveConnection`, `BatteryCharge/DischargeNode`, `TradeNode`: persisted graph entities; `ConnectedFlowEdge` (BE) rebuilt by `ConnectedFlowEdgeSystem` and `ElectricityGraphSystem` (`SystemOrder.cs:841-842`); `ElectricityFlowSystem`/`WaterPipeFlowSystem` save source/sink super-node handles (`ElectricityFlowSystem.cs:579`, `WaterPipeFlowSystem.cs:538`). The graph is derived from the network topology (built by `ElectricityGraphSystem`/`WaterPipeGraph*` systems) but flows are simulation state. [V/I]

### 4.11 Names (`Game/Game.UI/NameSystem.cs`) [V]

- Custom names: `Dictionary<Entity,string> m_Names` (`:162`), `SetCustomName` adds/removes the tag `CustomName` (E, `Game/Game.UI/CustomName.cs:8`) (`:214-238`); serialized as (Entity, string) pairs (`:549-580`). Applies to any named entity: buildings, aggregates (streets), districts, lines, citizens, vehicles.
- Generated names are derived: from `RandomLocalizationIndex` + prefab localization (`:357, 415`), citizen names from household/pseudo-random (`:444-468`), building addresses from road aggregate + number (`:472-474`). Only custom names are authoritative.

---

## 5. Ownership / relationship graph summary

Forward (authoritative, replicate; must be remapped to net IDs):

`Owner.m_Owner`, `Edge.m_Start/m_End`, `ConnectedNode.m_Node`, `Attached.m_Parent`, `Building.m_RoadEdge`, `AggregateElement.m_Edge`, `Waypoint`+`Owner`, `Connected.m_Connected`, `AccessLane/RouteLane/PathTargets` lanes, `HouseholdMember.m_Household`, `PropertyRenter.m_Property`, `Worker.m_Workplace`, `Student.m_School`, `CurrentBuilding`, `CurrentTransport`, `Resident.m_Citizen`, `CurrentVehicle.m_Vehicle`, `LayoutElement.m_Vehicle`, `Employee.m_Worker`, `CarCurrentLane.m_Lane`, `ParkedCar.m_Lane`, `PathElement.m_Target`, `TargetElement.m_Entity`, `CurrentDistrict/BorderDistrict/ServiceDistrict`, `Policy.m_Policy` (prefab), `Lane` `PathNode`s (packed owner index), `Dispatched.m_Handler`, `Destroyed/OnFire/AccidentSite.m_Event`.

Reverse (derived; never replicate) and their load-time rebuilders (`SystemOrder.cs:804-842`, all in `Game/Game.Serialization/`):

| Reverse buffer/tag | Rebuilt from | System |
|---|---|---|
| `ConnectedEdge` (+dedupe `ConnectedNode`) | `Edge` | `ConnectedEdgeSystem` |
| `Game.Net.SubLane` | `Lane`+`Owner` | `SubLaneSystem` |
| `SubBlock` | `Block`+`Owner` | `SubBlockSystem` |
| `SubObject` | `Owner`, `Attached` | `SubObjectSystem` |
| `SubNet` | `Owner`+`Node`/`Edge` | `SubNetSystem` |
| `SubArea` | `Area`+`Owner` | `SubAreaSystem` |
| `SubRoute` | `Route`+`Owner` | `SubRouteSystem` |
| `LaneObject`, `BlockedLane` | vehicle/creature current lanes | `LaneObjectSystem` |
| attachment back-refs | `Attached` | `AttachmentSystem` |
| `IconElement` | `Icon`+`Owner` | `IconElementSystem` |
| `LifePathEntry` | life path events | `LifepathEntrySystem` |
| `Renter` | `PropertyRenter`/`TouristHousehold`/`HomelessHousehold` | `RenterSystem` |
| `Game.Buildings.Student` | `Game.Citizens.Student` | `StudentSystem` |
| `ConnectedBuilding` | `Building.m_RoadEdge` | `ConnectedBuildingSystem` |
| `InstalledUpgrade` | `ServiceUpgrade`+`Owner`+`Object` | `InstalledUpgradeSystem` |
| `SpawnLocationElement` | `SpawnLocation`/`HangaroundLocation`/`ParkingLane`+`Owner` | `SpawnLocationElementSystem` |
| `OwnedVehicle` | `Vehicle`+`Owner` | `OwnedVehicleSystem` |
| `GuestVehicle` | `DeliveryTruck`+`Target` | `GuestVehicleSystem` |
| `Controller` | `LayoutElement` | `ControllerSystem` |
| `Passenger` | `CurrentVehicle`/`CurrentTransport` | `PassengerSystem` |
| `Resident` links | `CurrentTransport`+`Citizen` | `ResidentSystem` |
| pets/creatures | | `PetSystem`, `OwnedCreatureSystem`, `GroupCreatureSystem` |
| `CarKeeper` | `PersonalCar.m_Keeper` | `CarKeeperSystem` |
| `HouseholdCitizen` | `HouseholdMember` | `HouseholdCitizenSystem` |
| `HouseholdAnimal` | `HouseholdPet` | `HouseholdAnimalSystem` |
| `RouteWaypoint` / `RouteSegment` / `RouteVehicle` | `Waypoint`/`Segment`/`CurrentRoute` + `Owner` | `RouteWaypointSystem`/`RouteSegmentSystem`/`RouteVehicleSystem` |
| `ConnectedRoute` | `Waypoint`+`Connected` | `ConnectedRouteSystem` |
| `TransformFrame`, `TrainBogieFrame`, `HotspotFrame` | `Transform` etc. | `TransformFrameSystem`, `TrainBogieFrameSystem`, `HotspotFrameSystem` |
| `ResourceAvailability`, `ServiceCoverage` (re-init) | | `AvailabilitySystem`, `ServiceCoverageSystem` |
| `Aggregated` | `AggregateElement` | `AggregatedSystem` |
| `ConnectedFlowEdge`, electricity graph | flow edges | `ConnectedFlowEdgeSystem`, `ElectricityGraphSystem` |

Caveat [V]: reverse buffers are filled in chunk-iteration / insertion order (`ConnectedEdgeSystem.cs:40`, `SubObjectReferencesSystem.cs:62`), so **element order can differ between host and client** even when the sets are equal. Convergence checks must compare sets; code that iterates these buffers can produce order-dependent results. [I for the consequence]

---

## 6. Global system state (systems implementing `IDefaultSerializable` / `IJobSerializable`)

Discovered by `SystemSerializerLibrary.Initialize` over `world.Systems` (`SystemSerializerLibrary.cs:21-64`; plain `ISerializable` systems trigger an error log, `:55`). 74 types found (one is the nested `WaterMaterialParams`). Classification: **A** = authoritative player/city state, **S** = authoritative simulation state (host-owned), **C** = persisted cache/statistic (recomputable), **L** = local/presentation (do not replicate).

| System (file:line of Serialize) | What it saves | Class |
|---|---|---|
| `PrefabSystem` (`Game/Game.Prefabs/PrefabSystem.cs:827`) | PrefabID table | identity (join dictionary) |
| `CitySystem` (`Game/Game.Simulation/CitySystem.cs:54`) | city entity handle | A (structural) |
| `SimulationSystem` (`SimulationSystem.cs:135`) | `frameIndex` (game clock) | S - host clock |
| `CityConfigurationSystem` (`Game/Game.City/CityConfigurationSystem.cs:266`) | city name, default theme, required content, left-hand traffic, natural disasters, camera (local!), unlimited money, unlock all, unlock map tiles, used mods | A (camera part L) |
| `TaxSystem` (`TaxSystem.cs:276`) | tax rate array | A |
| `ServiceFeeSystem` (`ServiceFeeSystem.cs:408`) | pending `FeeEvent` queue | S |
| `MapTileSystem` (`Game/Game.Areas/MapTileSystem.cs:161`) | start tiles | A |
| `GameModeSystem` (`Game/Game.Prefabs.Modes/GameModeSystem.cs:111`) | game mode prefab ID | A |
| `TerrainSystem` (`TerrainSystem.cs:3079`) | world + playable heightmaps (R16 GPU textures), scale/offset/area | A (terraforming) |
| `GroundHeightSystem` (`GroundHeightSystem.cs:1157`) | pending height-update queues | S/C |
| `WaterSystem` (`WaterSystem.cs:1030`) | GPU water texture readback, sea level, backdrop, next source id, legacy flag | S (GPU, nondeterministic) |
| `WaterMaterialParams` (`WaterSystem.cs:116`) | water look params | L/A (map look) |
| `SnowSystem` (`SnowSystem.cs:244`) | snow height texture readback | S |
| `ClimateSystem` (`ClimateSystem.cs:529`) | climate prefab, current/next weather effects, temperature base height | A (climate) / S (weather) |
| `PlanetarySystem` (`PlanetarySystem.cs:526`) | latitude, longitude | A |
| `WindSimulationSystem` (`WindSimulationSystem.cs:214`) | wind cells, constant wind | S |
| `WindSystem`, `AirPollutionSystem`, `GroundPollutionSystem`, `NoisePollutionSystem`, `GroundWaterSystem`, `SoilWaterSystem`, `NaturalResourceSystem`, `LandValueSystem`, `TelecomCoverageSystem`, `TerrainAttractivenessSystem`, `PopulationToGridSystem`, `AvailabilityInfoToGridSystem`, `TrafficAmbienceSystem`, `ZoneAmbienceSystem` (all `CellMapSystem<T>` + `IJobSerializable`) | cell maps | S (natural resources, groundwater) / C (coverage, land value, ambience, population grid) |
| `AirwaySystem` (`Game/Game.Net/AirwaySystem.cs:298`) | helicopter/airplane airway maps (entities) | C (generated) |
| `ElectricityFlowSystem` (`:579`), `WaterPipeFlowSystem` (`:538`) | flow super-nodes, last steps | S |
| `ElectricityTradeSystem`, `WaterTradeSystem`, `TradeSystem` | last import/export, trade balances | S/C |
| `ElectricityStatisticsSystem`, `WaterStatisticsSystem`, `CityStatisticsSystem` (`:864`), `CityProductionStatisticSystem`, `CityProductionCapacityCalculationSystem`, `ProductionChainDataSystem`, `TransportUsageTrackSystem` | statistics/accumulators | C |
| `ResidentialDemandSystem` (`:385`), `CommercialDemandSystem` (`:468`), `IndustrialDemandSystem` (`:953`) | demand values and factors | S (drive zoning growth) / C for UI |
| `CountCompanyDataSystem`, `CountHouseholdDataSystem`, `CountWorkplacesSystem`, `CountStudyPositionsSystem`, `CountResidentialPropertySystem`, `CountVehicleDataSystem`, `CountConsumptionSystem`, `CountCityStoredResourceSystem` | aggregated counts | C |
| `ProcessingCompanySystem`, `ProductionSpecializationSystem`, `ResourceAvailabilitySystem`, `GarbageAccumulationSystem`, `FireHazardSystem`, `DeathCheckSystem`, `CitizenHappinessSystem` | small simulation accumulators/flags | S/C |
| `TransportLineSystem` (`:884`), `RoutePathSystem` (`Game/Game.Routes/RoutePathSystem.cs:351`) | max speed; lazy path update set | C |
| `TriggerSystem` (`Game/Game.Triggers/TriggerSystem.cs:517`) | trigger last-fired frames | S |
| `AchievementTriggerSystem` (`:959`) | achievement progress | L (per player) |
| `MilestoneUISystem` (`Game/Game.UI.InGame/MilestoneUISystem.cs:998`) | victory popup shown | L |
| `CityInfoUISystem` (`:125`), `ChirpLinkSystem` (`:350`) | UI caches | L/C |
| `NameSystem` (`Game/Game.UI/NameSystem.cs:549`) | custom names | **A** |
| `AudioManager` (`Game/Game.Audio/AudioManager.cs:1528`) | radio channel | L |
| `EffectFlagSystem` (`:108`) | season/night flags | C |
| `MarkerIconSystem` (`:378`) | selected/followed marker | L |
| `TerrainMaterialSystem` (`:886`) | terrain render settings, **user splatmap** | A (map paint) |
| `WaterRenderSystem` (`:559`) | render settings, sea wind | A/L |

Notable absences [V]: milestones/XP/dev-tree points, money, loans, budgets, policies, time-of-start live on **entities** (city entity, budget entity, `TimeData`), not in system state. `TimeSystem` is only `IPostDeserialize` (`TimeSystem.cs:14`).

---

## 7. CRITICAL: authoritative vs derived classification (drives the replication design)

Tiers:

- **T1 Authoritative - player-driven structure.** Low churn, must be bit-exact on all peers, replicate as host-validated commands *and* as state (for convergence). Includes identity.
- **T2 Authoritative - simulation-driven.** Host-owned, changes every tick; replicate as periodic state (snapshots/deltas, interest-managed). Clients do not simulate, or simulate only for visuals and get corrected.
- **T3 Persisted derived ("saved cache").** Saved by the game but recomputable from T1/T2; replicate *optionally* (cheaper to recompute; useful as a checksum target).
- **T4 Pure derived.** `IEmptySerializable` data, reverse buffers, non-serialized components. **Never replicate.** Trigger local recomputation by marking owners `Updated` (or via the `OnGameLoaded` path at join).
- **T5 Local-only.** Camera, UI, audio, achievements, tool previews. Never replicate.

| Domain | T1 Authoritative structure | T2 Authoritative simulation | T3 Saved cache | T4 Pure derived (never send) |
|---|---|---|---|---|
| Identity | `PrefabRef` (as PrefabID), stable NetId per instance, `PseudoRandomSeed` | - | - | `Entity` handles, `PrefabData.m_Index`, `ZoneType.m_Index`, `PathNode` packed indices |
| Prefab state | `Locked` enable bits (unlocks), `PlacedSignatureBuildingData` | `CollectedCityServiceBudget/Fee/UpkeepData` | other prefab components | everything else on prefab entities |
| Nets | `Node`, `Edge`, `Curve`, `Owner`, `Game.Net.Elevation`, `Upgraded`, `Fixed`, `ConnectedNode`, `Standalone`/`Orphan`/`LocalConnect`/`OutsideConnection`, `NetCustomColorVariation`, `TrafficLights` config flags, `Aggregate`+`AggregateElement` (street identity) | `Road` flow stats, `NetCondition`, `Pollution`, `TrafficLights` state | `BuildOrder` (counter must still be host-owned), `Density`, `LandValue`, `ServiceCoverage`, `ResourceAvailability` | `EdgeGeometry`, `NodeGeometry`, `Start/EndNodeGeometry`, `Composition`, `NetCompositionData` entities, `ConnectedEdge`, `SubNet`, `Aggregated`, `EdgeMapping`, labels, arrows |
| Lanes | identity only (owner NetId + LaneKey) - topology regenerated locally by `LaneSystem` | `LaneFlow`, `LaneCondition`, `LaneSignal`, `LaneReservation`, parking occupancy | `Lane`/`Curve` data (regenerable) | `SubLane`, `LaneObject`, `LaneOverlap`, `LaneGeometry` |
| Objects / buildings | `Object`+`Transform`+`PrefabRef` for player-placed objects, `Owner`, `Attached`, `Game.Objects.Elevation`, `Relative`, `Building.m_OptionMask`, `Extension`, `ServiceUpgrade`+`Owner`, `CustomMeshColor`, `Native`/`Overridden`, `Policy` buffer | `UnderConstruction`, `BuildingCondition`, `Abandoned`/`Condemned`/`Destroyed`/`Damaged`, facility states, `Efficiency`, `Resources`, `Occupant`/`Patient`, `Tree` growth, `Plant`, `Surface` wetness/snow | `Building.m_RoadEdge` (recomputed on connect), `BuildingModifier`, generated sub-objects and `Secondary` objects (regenerated but have identity) | `SubObject`, `InstalledUpgrade`, `Renter`, `ConnectedBuilding`, `SpawnLocationElement`, `ObjectGeometry`, `Color`, all `Game.Rendering` |
| Areas | `Area` + `Game.Areas.Node` buffer, `District.m_OptionMask`, `Owner`, `MapTile` ownership (`Native` absence), district `Policy` | `Extractor`, `Storage`, `WoodResource`, `MapFeatureElement` amounts | `DistrictModifier`, `CurrentDistrict`/`BorderDistrict` (recomputed by district systems) [I] | `Geometry`, `Triangle`, `Expand`, `SubArea`, labels, batches |
| Zones | `Cell.m_Zone` (as zone PrefabID) = zoning paint | `Cell.m_State` occupancy flags, `VacantLot` | `Block` geometry (generated by `BlockSystem`), `ValidArea`, `Game.Zones.BuildOrder` | `SubBlock` |
| Routes | `Route`, `TransportLine` settings (interval, ticket price, flags), `Waypoint`+`Owner`+`Connected`, `Segment` index, `Color`, `RouteNumber`, `VehicleModel`, `Policy` | `WaitingPassengers`, `BoardingVehicle`, vehicle requests | `PathElement`/`PathTargets` on segments (re-pathfindable), `AccessLane`, `RouteLane` | `RouteWaypoint`, `RouteSegment`, `RouteVehicle`, `SubRoute`, `ConnectedRoute`, `CurveElement` |
| Agents | - (no player authoring) | `Citizen`, `Household`, `HouseholdMember`, `PropertyRenter`, `Worker`, `Student`, `CompanyData`, `Employee`, `Resources`, `TaxPayer`, seekers, meetings | `CompanyStatisticData`, counters | `HouseholdCitizen`, `HouseholdAnimal`, `Renter`, `Student`(building) |
| Vehicles / creatures | - | everything (position, lane, path, cargo, passengers) | `TransformFrame` content | `OwnedVehicle`, `GuestVehicle`, `Controller`, `Passenger`, `LaneObject`, `InterpolatedTransform`, bogie frames |
| Events / icons / requests | player-triggered disasters (as commands) | `Event`+`Duration`+`TargetElement`, `OnFire`/`AccidentSite`/`Flooded`, `ServiceRequest`+`Dispatched` | `Icon` (recomputable from building state) [I], statistics entities, chirps | `IconElement`, `HotspotFrame` |
| City | `City.m_OptionMask`, `Policy`(city), `ServiceFee`, `TaxSystem.m_TaxRates`, `ServiceBudgetData` budget values, `Loan` (player action), `PlayerMoney.m_Unlimited`, `CityConfigurationSystem` options, `NameSystem` custom names, `MapTileSystem` start tiles, `GameModeSystem`, `PlanetarySystem`, climate prefab, `TerrainSystem` heightmaps, `TerrainMaterialSystem` user splatmap | `PlayerMoney.m_Money`, `XP`, `MilestoneLevel`, `DevTreePoints`, `Population`, `Tourism`, `Creditworthiness`, `SimulationSystem.frameIndex`, `TimeData`, `ClimateSystem` weather, `WaterSystem`/`SnowSystem` textures, wind, groundwater, natural resources, demand values, trigger frames, `ServiceFeeSystem` queue, flow-system state | `CityModifier`, `SpecializationBonus`, `CityStatistic` history, all `Count*`/statistics systems, coverage/land-value/ambience cell maps | - |
| Local | - | - | - | camera (in `CityConfigurationSystem`), `AudioManager`, `MarkerIconSystem`, `AchievementTriggerSystem`, `MilestoneUISystem`, `CityInfoUISystem`, `ChirpLinkSystem`, `Temp` entities, `EditorContainer`, `LocalTransformCache` (removed on game load, `Game/Game.Serialization/FilterLoadedSystem.cs:131`) |

Mods: every external-assembly `ISerializable`/`IEmptySerializable` component is T1/T2 by the game's own definition (`ComponentSerializerLibrary.cs:53-56`). [V]

---

## 8. Implications for Skyline Together (inferences)

1. **Join = save snapshot.** The cheapest exact join is: host runs its own `SaveGameSystem` into memory, streams it, client loads it via the normal load path. This automatically: resolves prefabs by `PrefabID`, remaps entities, rebuilds all T4 data, runs `OnGameLoaded` full recompute. Then a NetId table (saved-entity-index -> NetId) is sent so subsequent deltas can address entities. (Entity order in the save = stripped-archetype order, `EntitySerializer.cs:582-626`, so a parallel NetId list in the same order is natural.)
2. **Stable NetIds.** Assign a host-side NetId per replicated T1/T2 entity on creation (store it in a mod `ISerializable` component so it survives host save/load). Never send `Entity`.
3. **Derived-with-identity entities** (lanes, generated sub-objects, secondary objects, zone blocks, composition entities): let clients regenerate them, but address them by a deterministic key derived from their owner NetId + local key (`LaneKey`-style). Composition entities need no identity at all.
4. **Command replication for T1**, state replication for T2, and a periodic **hash of T1 state** (compare sets, not buffer order) to detect divergence and trigger targeted re-sync of an owner subtree (owner + `Owner`-children).
5. **Translate machine-local indices** on the wire: `PrefabData.m_Index` -> PrefabID dictionary index, `ZoneType.m_Index` -> zone prefab, `PathNode` owner index -> NetId, `UpdateFrame` -> send the host value or let the client assign (only affects which sub-frame it ticks; irrelevant if clients do not simulate).
6. **GPU state** (water, snow, terrain heightmaps) must be transferred as compressed textures (as the save does with `NativeCompression.FilterDataBeforeWrite`) and periodically re-synced; it cannot be kept identical by local simulation.

---

## 9. Risks

- Nondeterministic RNG (`RandomSeed` seeded from `DateTime.Now.Ticks`, `Game/Game.Common/RandomSeed.cs:8`) and chunk-index-dependent `GetRandom(index)` (`:20-24`) make local re-simulation diverge immediately.
- Hidden entity references in non-`Entity` fields (`PathNode`), plus possibly others in mods; a generic "remap all `Entity` fields" pass is not sufficient.
- Prefab mismatch: different mod sets yield missing prefabs (placeholders via `[Missing]` obsolete IDs) or duplicate-ID shadowing (first-registered wins, `PrefabSystem.cs:152-172`); code-created prefabs have hashless IDs that can collide across mods.
- Swap-remove on `RemovePrefab` changes indices mid-session when playsets change (`PrefabSystem.cs:225-256`).
- Reverse-buffer order differs between peers; order-sensitive game logic may diverge even with identical forward state.
- GPU-resident authoritative state (water/snow/terrain) requires GPU readback (`AsyncGPUReadback ... WaitForCompletion`, `WaterSystem.cs:1043, 1063`) - expensive/stalling if done often.
- `CityConfigurationSystem` mixes authoritative options with local camera state in one serializer.
- High-churn agents/vehicles (tens of thousands) make exact replication of T2 bandwidth-heavy; needs interest management or acceptance of visual-only approximation for some T2 classes.

## 10. Open questions

- Exactly which prefab-generated sub-objects can also be player-placed/edited (e.g. via mods like Move It or BetterBulldozer `PermanentlyRemovedSubElementPrefab`) - affects whether sub-objects can be treated as derived.
- Whether `Building.m_RoadEdge`, `CurrentDistrict`, `BorderDistrict` and modifier buffers are always recomputed after load or only on update (treat as T3 until verified).
- Whether `Icon` entities can be fully regenerated client-side from replicated building/simulation state.
- How tool `Apply*System`s create entities (order of creation, which IDs) - needed to make command replay produce matching derived entities (covered by the tools research report).
- Whether built-in prefabs ever lack `asset` (hashless ID) in practice; verify at runtime by dumping `PrefabSystem.prefabs` IDs on two machines.
