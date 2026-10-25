# Mod compatibility

How Skyline Together keeps third-party Paradox mods in sync between host and clients. Research basis:
`docs/research/09-third-party-mods.md` (R09). Every statement about a mod below was checked against the version
published on Paradox Mods when the adapter was written (ids in §2).
Code lives in `src/SkylineTogether/Compat/` (adapters, watcher, settings channel, assembly scanner, generic analysis,
integration API, asset sources) with small marked hooks (`// mod compatibility`) in `EntityCodec`,
`FamilyClassifier`, `TypeHandlers`, `SyncController`, `FollowerClock`, `GameInfoProvider`, `SnapshotService`,
`SnapshotLoader` and `DiagnosticsDump`; the pure parts (classification, declarations, adapter checks, channel framing
and routing, asset texts, IL walk, comparison, setting values, records) are in Core (`Core/Modding`, `Core/Session`)
with tests (`tests/.../Modding/*`, `tests/.../Session/ModCompatibilityTests.cs`, `tests/.../Sync/ModRecordTests.cs`).

Mods that are not in the table of §2 are handled generically (§6), and mod authors can declare what their mod does
without any reference to Skyline Together (§8, [`../MOD-INTEGRATION.md`](../MOD-INTEGRATION.md)).

## 1. Mechanisms

| Mechanism | What it solves | Where |
|---|---|---|
| Generic codec | Every `ISerializable`/`IEmptySerializable` component of every mod already replicates through the game's own save encoding, entity references as net ids or derived keys ([batch encoding](../ARCHITECTURE.md#batch-encoding)). | `Sync/EntityCodec`, `TypeHandlers` |
| **Mod entity family** | Entities defined only by mod data replicate as `ModEntity` even when they carry a `PrefabRef` (Traffic's lane connection data: `PrefabRef(Traffic.FakePrefab)`; Better Bulldozer's removal records: `PrefabRef` of the removed prefab). Move It's control points (`PrefabRef(Entity.Null)`) are tool-local through their adapter-local type. | `FamilyClassifier.Markers/Classify` |
| **Adapter-local types** | Types an adapter names (Traffic connectors, lane handles, temp data; Move It control points and overlays) make their entity tool-local ([families](../ARCHITECTURE.md#families)). | `ModAdapter.LocalTypes` → `FamilyMarker.AdapterLocal` |
| **Dependency binding** | A replicated value that references an unbound mod entity (a node's `ModifiedLaneConnections` → its data entity; an owner's removal buffer → record entities) does not travel as "presence only": the capture binds the entity (host: new id; client: id from its granted pool) and spawns it in the same batch, so references resolve on apply. | `EntityCodec.DependencyBinder`, `SyncController.HostBindDependency/ClientBindDependency/WriteDependencies` |
| **Mod state watcher** | Mods write their own components without `Updated` (in-place buffer edits, structural add/remove from UI buttons). Per capture, chunk change versions of every mod component type (+ vanilla types adapters name) select candidates; a hash of the watched types (whole state for mod entities and water sources) finds real changes; a slow sweep (256/frame) catches removals. Host: upsert when the watched type set changed, values otherwise, max 1/s per ordinary entity, immediately for mod entities. Client: proposals (`DirectTool` flag → `ModTools` permission). The applier's own writes are refreshed into the baseline first, so nothing ping-pongs. | `Compat/ModStateWatcher`, `SyncController.CaptureModChanges` |
| **Enabled bits** | Enableable components (vanilla `Decoration` on trees placed outside decoration mode, Tree Controller's `LumberResource`, `CustomMeshColor`) replicate their enabled bit (`SyncRecordKind.EnabledBits = 110`, right after the entity record, both directions; `ISerializeAsEnabled` types excluded). | `EntityCodec.WriteEntity/Apply` |
| **Edit hold** | A direct-write tool (Move It drag) proposes every frame; the host's echo of an older proposal would snap the object back. While the player keeps editing an entity (0.6 s after its last proposal) the client skips host records for it and applies the latest one when the hold ends, so the result always converges to the host's. Despawns are never held. | `SyncController.ApplyFromHost/ReleaseHolds` |
| **Pipeline tools** | Mod tools that apply through the Temp pipeline (Traffic, Line Tool, Find It's picker) are not direct-write tools: idle frames propose nothing, applies come through `ApplyTool` like vanilla ones. Every other mod tool is direct-write (fail safe). | `ModAdapter.PipelineTools` |
| **Placement relaxations** | The host's placement checks of client proposals (unlocks, owned map tiles, geometry) refuse only what a vanilla tool cannot produce. Mods that legitimately allow more switch checks off while they are loaded on the host: Anarchy and Line Tool tiles and geometry, Find It and Plop the Growables unlocks. A direct-write tool's proposal (Move It) skips them when the host runs any code mod ([placement checks](../ARCHITECTURE.md#placement-checks)). | `ModAdapter.RelaxedPlacementChecks`, `ModCompat.RelaxedPlacementChecks`, Core `PlacementPolicy` |
| **Client gates** | Mod systems that derive host-owned state are off on live clients (`ClientOffSystems`); load-time mutators driven by the player's own settings are off from the start of the join (`JoinOffSystems`, incl. the game's `PostDeserialize<T>` wrapper). Systems that must run in the session but whose post-load step would redo the host's work are off from the start of the join until the client is live (`LoadOffSystems`); the adapter's `OnClientLive` clears the queued step before they come back (Placemaker's group registry). Restored exactly at session end (`SystemSwitch`). Mod simulation systems are already off on clients (follower clock); adapters may keep ones that write no replicated state (`ClientSimulationSystems`). | `Compat/ModCompat` |
| **Client-created mod entities** | A client only proposes new mod data entities that a proposed value references (dependency binding). Entities a mod creates without update tags as the player's edit and that nothing references (Placemaker's group record and carrier) are named by the adapter (`ClientCreatedTypes`): when the watcher finds such an unbound entity on a client, under the usual proposal rule (a tool applying or recent input), it is bound from the pool and proposed as a spawn; all of a frame's are bound before any is written, so they can name each other. | `ModAdapter.ClientCreatedTypes`, `SyncController.ProposeClientCreated` |
| **Runtime prefabs on demand** | Runtime prefabs a mod creates per city and player from data fully contained in their name (Placemaker's park companions and surface overlays) are created on the peer that lacks them by the mod's own code, so they get the same `PrefabID` (§7). Joiner: requested when the buffered prefab table arrives (`SessionClient.MessageBuffered`), so the game's loader finds them; live: when a `Prefab` record does not resolve. Records that need one wait without a missing-asset report and are asked for again (integrity repair) once it exists; given up after 120 s. A client's own new ones are created on the host on request and named to everyone (`Prefab` record). At most 512 per session. | `ModAdapter.ProvideRuntimePrefab`, `ModCompat.RuntimePrefab/AnnouncePrefab`, `EntityCodec.ResolveRuntimePrefab`, Core `Modding/RuntimePrefabBook`, `PlacemakerNames` |
| **Adapter channel** | A built-in adapter can talk to the host on a channel of its own (`ModIntegration.RegisterInternal`, channel `SkylineTogether.Placemaker`): the mod channel's message kind, routing, budgets and role permissions (viewers may not send), no wire change. | `ModAdapter.InternalChannel/OnChannelMessage`, Core `Modding/PlacemakerMessage` |
| **Mod settings channel** | Simulation-relevant `ModSetting` properties are the host's for everyone (`SyncRecordKind.ModSettings = 111`, host → clients, changes polled 1/s, everything on join). Clients write through the property setter (mods that react to a change do), call `Setting.Apply()`, and restore + re-save their own values at session end. Only bool/int/long/float/double/enum/string properties listed by an adapter are touched; buttons never. | `Compat/ModSettingsSync`, Core `SettingValueText`, `HostSettingsTracker`, `ClientSettingsTracker` |
| **Missing assets** | A record whose `PrefabRef` cannot be resolved (asset pack or runtime prefab missing) is never applied (an entity without its prefab breaks game systems); binding is retried lazily (runtime prefabs created after the join's prefab table), with a name fallback for runtime prefabs (§7). The host says where each non-base-game prefab comes from (`PrefabSource` record), so the joiner's message names the package and Paradox Mods id or the mod and what to do; the snapshot manifest lists the city's asset sources, checked before the load. The status line, one toast and the diagnostics dump list the missing assets. A client proposal of an object whose prefab the host lacks is refused (rollback). | `EntityCodec.MissingPrefab/NamesUnknownPrefab/NameFallback`, `Compat/AssetSources`, `SyncController.Validate`, Core `Modding/AssetSource` |
| **City data is required** | A joiner without one of the host's mods that store data in the city is refused, never overridable: the game's loader crashes on saved types of a mod it does not have ("Not serializable type: ..." in Player.log, observed with Zoning Toolkit). Evidence: saved component types and saved system state of the host's own scan, a failed scan, or the built-in list; the city's entity count per saved type only changes the wording. The joiner repeats the check before it loads the city. The old multiplayer mod (assembly `CS2MultiplayerMod`, Paradox Mods id 150432, detected by name and id only) refuses on either side. (§10) | Core `Session/CityDataRule`, `Modding/CityDataEvidence`, `Compat/CityDataProbe`, `SessionClient.StopForCityData` |
| **Join classification** | Mods that only affect their own player never block a join (missing on either side or another version). Built-in lists of known client-local and known world-affecting mods (by assembly name and Paradox id), then each peer's analysis of its own copy (§6): a mod that is client-local by its declaration or by the scan gets `ClientLocalHint` (manifest flag bit 2); otherwise `RegistersTypesOrSystemsOrPrefabs`. Unknown without a hint: world-affecting (fail closed). A built-in client-local entry checked against one version (ExtraLib, Achievement Fixer, Cim Census, Cim Route Highlighter, Show Text Trends, Skyve, Advanced Simulation Speed, Zone Organizer, Magic Hearse, Simple Mod Checker Plus) holds only while each peer's copy has nothing beyond that version (§5); the peer's `RegistersTypesOrSystemsOrPrefabs` flag says so. The structured `MismatchReport` and its one-line text simply leave the local mods out. | Core `Session/ModCompatibility`, `MismatchReport.CompareMods`, `Compat/ModAssemblyScanner`, Core `Modding/ModClassifier` |
| **Join check of DLC, asset packs and settings** | After the password the host compares, per category whose handshake digest differs, the joiner's DLC (refused when one of the host's is missing), asset packs by Paradox Mods id and contents (told; refused with the host option "require the same asset packs") and the current values of the settings of §3 (told; the host's values apply). Admitted joiners get the differences as a join notice ([handshake](../ARCHITECTURE.md#handshake-and-admission)). | Core `Session/CompatibilityCheck`, `CompatibilityDigests`, `Session/GameInfoProvider`, `AssetSources.Packages`, `ModSettingsSync.CurrentValues` |
| **Adapter version check** | Every name a built-in adapter relies on is resolved when the mod is detected; any missing name turns the whole adapter off with one log line and a diagnostics entry, and the mod gets the generic treatment (§5). | `ModCompat.Check`, Core `Modding/AdapterCheck` |
| **Generic analysis** | Every loaded code mod is scanned (types, IL bytes, the game's system registrations, Harmony's patch list) and classified with explained, conservative rules; its declarations are applied (§6). | `Compat/ModAssemblyScanner`, `HarmonyPatches`, `GenericModAdapter`, Core `Modding/ModClassifier` |
| **Exact game speed** | The speed sync carries the game's 1, 2 and 4 as before and, for any other speed a speed mod selects (0.01 to 8), an exact multiplier after the two bytes older peers read (`SpeedState.Exact`: flag bit 2 of the second byte, then a float): the host's speed is everyone's, the follower clock mirrors it at that rate, and a player's exact request is run by a host whose adapter allows it. Invalid values fall back to the rounded speed. Payloads of the game's own speeds are unchanged. | Core `Session/SessionPayloads` (`SpeedState`), `Systems/SpeedAuthoritySystem`, `Sync/FollowerClock`, `ModAdapter.AllowsFineGameSpeed` |
| **Route policy range** | While a mod computes route modifiers beyond the policy slider (Transport Policy Adjuster) the host accepts a player's route policy value up to `RoutePolicyRangeSlack` slider spans outside the range, instead of clamping to it. | `ModAdapter.RoutePolicyRangeSlack`, `GlobalStateSync.CreateModifyEvent`, Core `GlobalProposalRules` |
| **Integration API** | Attributes and a static class matched by name; session events and a host-relayed channel of opaque bytes per mod (§8). | `Compat/ModIntegration`, Core `Modding/ModDeclarations`, `ModChannelRouter`, `Protocol/ModChannelMessage` |

## 2. Per mod

Class legend: **L** client-local (UI/visual only), **D** data components on entities, **T** tool that writes directly,
**S** simulation/system change, **R** replaces vanilla systems, **P** creates prefabs at runtime, **Ld** load-time mutation.
"Same version on all peers" applies to every non-L mod (the join refuses otherwise).

| Mod (id) | Class | State it creates or changes | How it syncs | Status | Risks / limits |
|---|---|---|---|---|---|
| **Traffic** (80095) | D, R, T(pipeline) | Nodes: `ModifiedLaneConnections` buffer (lane index, carriageway/group, position, edge, **data entity**), `ModifiedConnections` tag. Data entities: `DataOwner`, `GeneratedConnection` buffer (source/target edges), `PrefabRef(FakePrefab)` (FakePrefab added at PreDeserialize on every peer). Edges: `LanePriority` buffer, `ModifiedPriorities`. Replaces `LaneSystem` with `TrafficLaneSystem`. Tool entities (connectors, handles, `DataTemp`, `EditIntersection`). Settings: UI only. | Node/edge records carry the buffers; data entities are `ModEntity`, bound and spawned with the node (dependency binding), in-place edits found by the watcher, deletions (`Deleted` on data entity) replicate as despawns. No value references a lane: every peer re-derives identical lanes when the applier tags the node + closure `Updated`; lane derived keys resolve against Traffic's lanes like vanilla ones. Tools apply through `ApplyTool` (client prediction + proposal; host edits replicate). Tool entities tool-local. | Implemented | Lane regeneration relies on identical composition lanes (same prefabs/version). If a data entity edit ever happened without its node being updated, clients would not regenerate lanes until the next node update (not observed in the code). Load-time validation (`TrafficDataMigrationSystem`) runs on each peer, deterministic on the same data. |
| **Move It** (74324) | T (direct) | Writes `Transform`, node positions, curves, area nodes, elevations of persistent entities directly (+ `Updated`); local undo/redo queue; control points `PrefabRef(Null)` + `MIT_ControlPoint`; overlay entities. No serializable component, no settings that matter. | Direct-write tool: changes captured as change sets (`DirectTool`), both ways; **edit hold** prevents snap-back from host echoes; control points/overlays tool-local; undo/redo are just more direct writes. | Implemented | Undo history is per player (holds local entity handles; replication edits entities in place, so handles stay valid). Entities only touched by derivation may be proposed unchanged while the tool is active (harmless). |
| **Anarchy** (74604) | D, T, S, Ld, P | `PreventOverride`, `TransformRecord`, `DoNotForceUpdate` (serializable); `Overridden` removal, `Attached`/elevation changes post-apply; components tool and info-panel toggles write without tags; network upgrade prefabs (setting) added at load. Settings: unique-building limit, clearance below elevated networks (validation/geometry). | Components replicate in the entity's record together with `Transform` (so `ResetTransformSystem` reproduces it); untagged writes → watcher; post-apply processing runs where the tool applied (proposal carries the result). Host settings: `AllowPlacingMultipleUniqueBuildings`, `MinimumClearanceBelowElevatedNetworks`. Per-player Anarchy toggle stays per player (tool mode). | Implemented | `NetworkUpgradesPrefabs` must match before load (prefabs created at load); a mismatch shows as missing assets. Camera-based `PreventCullingSystem` adds local `Updated` tags (filtered by shadow hashes). |
| **Better Bulldozer** (75250) | D, T (direct), Ld | Owner buffer `PermanentlyRemovedSubElementPrefab` → **record entities** (`OwnerRecord` + `PrefabRef`), added without tag; direct deletion of sub-objects/sub-nets/extensions; vehicle/cim/animal deletion tool; automatic removal settings act on every peer. | Watcher finds the owner's buffer → upsert (+ record entities by dependency binding); every peer removes regenerated sub-elements itself on the owner update (as after a load); sub-net deletions replicate (net family); **mover despawn proposals** from mod tools are accepted (BB's cim/vehicle tool). Host settings: the 2 tool rules and 3 automatic removals. | Implemented | Derived sub-objects disappear on clients with BB's own 30-frame delay. |
| **529 Tiles** (74328) | Ld, S (Harmony on tile purchase) | `PostDeserialize` unlocks/relocks tiles and edits milestones from local settings; upkeep/cost patched in simulation. | `Native` on map tiles is save data and replicates; `PostDeserialize<FiveTwentyNineSystem>` is **off on clients** from join start; purchases run on the host (gameplay intents) with the host's patched costs. | Implemented | Settings stay per player (they only act at load, which is host-only in a session). |
| **Historical Start** (74535) | Ld (prefab unlocks) | Unlock events and `UnlockRequirement` edits at every load from local settings. | `HistoricalStartSystem` **off on clients** from join start; the host's unlocks arrive in the snapshot and through the unlock channel. | Implemented | — |
| **Water Features** (75613) | D, T (direct), S | Water sources without `PrefabRef` (+ 6 mod components), in-place `WaterSourceData` edits without tag; sim systems read local GPU water; global water parameters; per-city settings saved in the city. | Water-source family replicates; `WaterSourceData` watched (values ≤ 1/s per source); sim systems host-only (follower clock); `ChangeWaterSystemValues` kept on clients (applies the host's parameters to local water); host settings: evaporation, fluidness, damping, sim speed, damage, seasonal streams, waves and tides (17 properties). | Implemented | Client water surface is still a local GPU simulation corrected by `WaterSync`. |
| **Zoning Toolkit** (75750) | D, T (direct) | `ZoningInfo` on road edges; block sizes adjusted in Modification4B on every peer (edge's value for edges not applied locally). | `ZoningInfo` replicates in the edge record (atomic with the edge), blocks re-derived identically, zone cell keys match; tool writes with tags (direct-write tool). Zoning mode for new roads travels inside the proposed edge. | Implemented | — |
| **Line Tool** (75816) | T (pipeline) | Places objects via vanilla creation definitions (random seeds resolved locally). | Applied objects proposed like any tool apply; pipeline tool. | Implemented | — |
| **Plop the Growables** (75826) | D, R, S, Ld | `PloppedBuilding`, `SpawnedBuilding`, `LevelLocked` (info-panel toggle without tag); replaces `ZoneCheckSystem` with `SelectiveZoneCheckSystem`; `PloppedBuildingSystem` tags (and makes historical) in ModificationEnd; load-time conversion; settings buttons. | Tags replicate; level lock toggle → watcher; `PloppedBuildingSystem` and `SelectiveZoneCheckSystem` **off on live clients** (host results replicate); `ExistingBuildingSystem` off on joiners; host settings `SpawnedZoneDespawn`, `LockPloppedBuildings`. | Implemented | Options-menu bulk buttons (all historical, remove abandonment) should be pressed on the host; on a client they change the client only until the integrity repair. |
| **Tree Controller** (75993) | D, T (direct), S | `DeciduousData`, `Evergreen`, enableable `LumberResource`; brush writes tree state, swaps prefabs, toggles `Decoration` (with tags); seasonal sim systems; rendering-phase growth/lumber toggling; free-vegetation prefab cost. | Data + **enabled bits** replicate; brush = direct-write tool; seasonal systems host-only; foliage colour system (prefab data only) kept on clients; host settings `UseDeadModelDuringWinter`, `DisableTreeGrowth`, `FreeVegetation`. | Implemented | `DestroyFoliage`/`SafelyRemove` option buttons should be pressed on the host. |
| **Find It** (77240) | L, P | Prefab browser and picker; generated prop variants as runtime prefabs. | Client-local for joining; placement via vanilla tools; generated prefabs resolve by PrefabID (lazy retry); missing on one side → reported, never applied (client proposals refused with rollback). | Implemented | Players without Find It cannot see objects placed from its generated variants. |
| **I18N Everywhere** (75426), **Extended Tooltip** (78188), **Asset Icon Library** (79634), **Unified Icon Library** (74417), **Toggleable Overlays** (80931) | L | Localization (Harmony on `LocalizationDictionary.TryGetValue`), tooltips (UITooltip systems, Harmony on two `Game.UI.Tooltip` systems), prefab thumbnails (`IconReplacerSystem` sets UI icons after a load; mesh icons rendered in the editor only), icon files, overlays. Some add `Highlighted`/`BatchesUpdated` (excluded from replication). | Never block a join (built-in list); nothing to sync. Re-checked against I18N Everywhere 1.5.2, Extended Tooltip 1.7.0, Asset Icon Library 4.1.1 (since 4.1: icons replaced once the icon packs are unpacked, editor-only mesh icons) and Unified Icon Library 1.0.14. | Implemented | — |
| **ExtraLib** (75724) | L, P | A library of other mods (Extra Networks And Areas uses it): toolbar menus and categories as runtime prefabs (`UIAssetMenuPrefab`, `UIAssetParentCategoryPrefab`, `UIAssetChildCategoryPrefab`, `UIAssetCategoryPrefab`), created in the main menu for the `EAM/...` categories of loaded assets, and `UIObjectData` of those assets' prefab entities; runs the entity callbacks other mods register (`EL.AddOnEditEnities`) once in the main menu; UI panels; Harmony on `ToolbarUISystem` (asset menu selection). `MainSystem` is registered in LateUpdate but disabled from its creation and never updates. No saved type, no setting that matters. | Client-local (built-in list, checked against 1.4.11: the analysis finds exactly `MainSystem`, the 2 prefab types and the runtime prefabs of `MainSystem` and `PrefabsHelper`); no adapter. Its prefabs are menus that no object of a city references, named after their category, so peers with the same assets create the same ones; a prefab the host's unlock list names that a client lacks is ignored there. A mod that loads prefab files or creates menus through `PrefabsHelper` counts as creating runtime prefabs in its own analysis (§6). | Implemented | What a mod changes through ExtraLib's entity callbacks belongs to that mod; the scan of that mod cannot see it (Extra Networks And Areas is not checked). |
| **Achievement Fixer** (121256) | L | `AchievementFixerSystem` (MainLoop) switches `PlatformManager.achievementsEnabled` on after each load in game mode, which the game turns off for a modded city (once when the load completes, then every frame for 1800 frames); option buttons unlock, clear or reset the player's own achievements. No entity, no shared state. | Client-local (checked against 1.3.1). A joiner's achievements stay off in the host's city, as without the mod: the system is off on clients from the start of the join (`JoinOffSystems`), and while the session holds the player's own value the flag is set off again every frame (`SnapshotLoader.HoldAchievementsOff`; the mod's write at load completion runs before any gate). The player's value comes back at the session end. On the host the mod works as in single player. | Implemented | Per join load the joiner's log has the mod's "forcing TRUE" lines and two of Skyline Together (its system held off, achievements kept off). |
| **Cim Census** (85825) | L | A statistics panel: `StatisticsCalculationSystem`, a UI system registered in GameSimulation, counts citizens, workers, pets, waiting passengers and vehicles with read-only jobs every 60 updates while the panel's auto refresh is on. Writes nothing. | Client-local (checked against 1.8). The system keeps running on following clients (`ClientSimulationSystems`; the follower clock would switch it off with every simulation system), so a joined player's panel counts its replicated copy of the city. | Implemented | — |
| **Cim Route Highlighter** (76662, assembly `EmploymentTracker`) | L | Highlights the selected building's employees, residents, students and passenger destinations (`Highlighted` + `BatchesUpdated` from `HighlightEmployeesSystem` in MainLoop) and draws routes in the rendering phase; display settings. Declares a saved `DeleteTimer` that no code puts on an entity. | Client-local (checked against 1.26); highlights never replicate. `DeleteTimer` is excluded from replication (adapter) and is no city data by itself: a save lists every saved type of every loaded mod in its type table, and a game without the mod reads that entry without data. The host counts the entities that carry it and requires the mod only when its city has any (§10). | Implemented | A city whose entities carry `DeleteTimer` (no published version is known to write it) requires the mod. |
| **Placemaker** (163638) | D, T (direct + pipeline), S, P, Ld | Builds designed places in a drawn area over many frames (`BuildService`, its tool `BuildToolSystem` active): pieces through the Temp pipeline (ApplyTool), around that direct writes: group record entity (`PlacemakerGroup` + `PlacemakerGroupBlob` receipt, no tags) and a carrier (`PrefabRef` + sub-element buffers + `PlacemakerMember`), `PlacemakerMember` on every piece, water source of a lake, owner lot (`Placemaker Area`), road charges on `PlayerMoney`, park attractiveness on the owner lot; terraforming and ground paint written straight into the GPU terrain; it removes the game's errors from its own temporaries (collisions, tiles). Runtime prefabs: per park statistics a hidden companion building (`Placemaker Park a.. l.. r.. c.. m.. u..[ b]`, cloned from a vanilla park) and overlay copies of surfaces (`Placemaker Overlay (...)`), remembered in each player's own files; fixed ones every peer makes (`Placemaker Area`, `Placemaker Zone Block`, `Placemaker Parking Companion`, `Placemaker Vehicle ...`). Outgrow ("neighbourhood polish") dresses zoned lots by itself (async plans from local terrain, creation definitions, lawn nodes, hidden pieces, terrain paint; `PolishedLot`, `PolishPiece`, `PolishGarden`, `PolishLawnOriginal`, `PolishLook*`, per-city switches `PolishCitySwitches`). Group removal in passes (`PlacemakerRemoval`), park/parking companions and their clean-up, parking fees on parking lanes (`ParkingEconomySystem`, derived from the receipts), companion upkeep (GameSimulation), old-decal clean-up and a registry repair after each load. 13 saved component types. | The 13 components replicate generically (records and switches are mod entities); the record and carrier a client's build creates are proposed as spawns (`ClientCreatedTypes`); `BuildToolSystem` is a direct-write tool, the planning tool `PlacemakerToolSystem` a pipeline tool; all placement checks relaxed while it is loaded. Off on clients from the join: Outgrow and its 6 helpers, group removal, park and parking companions and their clean-up, old-decal clean-up (the host's results replicate); the group registry runs everywhere but is held off until live and its post-load repair cleared (the host's load did it). A client's removal request is the `PlacemakerRemoval` it adds to the record (watched mod state); its companion requests go to the host on the adapter channel and the host's Placemaker creates the companion. **Runtime prefabs on demand** (§7): the host's park and overlay prefabs are created by the joiner's Placemaker from their names (`ParkCompanionPrefabSystem.Want`, `SurfaceCloneSystem.OverlayFor`), before the load from the prefab table, else when a prefab record arrives; a client's own new ones are created on the host and named to everyone. Host settings: 9 (§3). | Implemented | Terraforming and ground paint are direct GPU writes, not brush stamps: the host's reach clients through the terrain audit (heights, within minutes), ground paint not at all; a client's stay on that client until the host's terrain replaces them, so builds that shape terrain (lakes, hills) are best made by the host. Outgrow and its switches act on the host only (a client's switches and look stamps change nothing). Group numbers are allocated per machine: two players finishing builds in the same moment can get the same number. A client's road charge is not charged by the host (vanilla piece costs are). A joiner's Placemaker remembers the prefabs created for the host's city in its own lists, as if it had built them. Not tried in a live two-player game yet. |
| **China Pack** (100454) | asset pack (`.cok`), no code | Prefabs (buildings/props). | Not a code mod (not in the mod list); its prefabs resolve by PrefabID; the join's content check and the missing-asset report cover a player without it. | Covered | — |
| **Show Text Trends** (83733) | L | One UI system (`MoneyUISystem`, UIUpdate) with a binding for the display mode of the money trend (per hour or per month); the mode is its own setting. No entity, no saved type, no patch. | Client-local (built-in list, checked against 1.0, assembly name `Show Text Trends` with spaces); no adapter. | Implemented | — |
| **Prefab Asset Fixes** (113708) | Ld, S (prefab data) | `AssetFixSystem` edits prefab entities in place when a game starts (`OnGamePreload`) and again live whenever a setting changes (each setting's setter calls its `Fix*`): `PrisonData`, the `StorageLimitData` of two clinic prefabs, `ZonePropertiesData` and `BuildingPropertyData` of the Dutch low housing, `IndustrialProcessData` of every industrial prefab (workers per cell and output), `SubObject`/`SubNet` buffers and lane directions of hospitals, bus stations, depots and parking lots, the `UnlockOnBuildData` of the French city hall pair, the `BuildingUpgradeElement` lists of the solar power station. Once per load (Deserialize phase) it also puts `Deleted` on the road edge of parking lot buildings whose sub-net holds a low-voltage marker and shows a "reload the save" dialog. No saved type, no patch. Settings (14 toggles and one slider) in `ModsSettings/StarQ/PrefabAssetFixes`, kept in a static field of the mod class. | Prefab data is not replicated, so every peer must hold the same values: the 14 settings that edit prefabs are the host's (§3) and written through their setters, which redo or undo the edit (the adapter names the static settings object); `AssetFixSystem` is **off on a joined player** from the start of the join (the host's city arrives with the fix done); the player's own settings come back through the same setters when the session ends. World-affecting by the built-in list: same version on every peer. | Implemented (not tried in a live game) | Between the start of the join's load and the first settings record (seconds) the joiner's own settings act on its prefabs; objects it derives in that time (sub-objects of an updated building) use them. The mod's own dialog after its city fix is shown on the host only. |
| **Transport Policy Adjuster** (78622, Harmony) | S, R | Harmony prefixes disable the game's `Policies.ModifiedSystem.OnUpdate` and replace `RouteModifierInitializeSystem.OnUpdate`; its own `ModifiedSystem` (Modification4) applies the policy events with a `GetModifierDelta` that no longer clamps the slider fraction (vanilla saturates it), so a route policy (vehicle count) can go beyond the slider; Harmony also replaces the vehicle count info section (`VehicleCountSection`) with a slider sized by the six maxima of its settings (bus, train, tram, ship, airplane, subway). It writes only vanilla `Policy` and `RouteModifier` buffers; no saved type of its own. | World-affecting (built-in list; the scan agrees: patches of simulation code): every peer derives route modifiers with the mod's copy, so the same version must run everywhere. `Policy` buffers replicate as usual. The host's check of a player's policy value (`GlobalStateSync.CreateModifyEvent`) clamps to the policy's slider range, which would cap the mod's counts, so while the mod is on the host the range of route policies (policy prefabs with `RouteModifierData`) is widened by `RoutePolicyRangeSlack` = 100 slider spans on each side (Core `GlobalProposalRules.RoutePolicyRangeSlack`; non-finite values still refused). The six maxima size the player's own slider (UI): not synced. | Implemented | The factor 100 is an estimate of how far the mod's vehicle counts exceed the slider, not a measured value: a larger request is cut to it. Not tried in a live game. |
| **Magic Hearse v Funeral Director** (123497) | S (host), Ld | `MagicHearseSystem` (adds `Deleted` to dead citizens waiting for a hearse), `CemeteryResetSystem` and `HearseWarningDelaySystem` (health problem timers) in GameSimulation; `FuneralDirectorSystem` (PrefabUpdate, redone at every load and on a setting change) scales `DeathcareFacilityData`, hearse `CarData` speed and, with "control workers", the `WorkplaceData` of deathcare prefabs and `WorkProvider.m_MaxWorkers` of placed buildings. Components `WorkProviderMax`, `MHWarningDelay`, `MHWorkplaceMarker` are runtime markers without a save format. No patch, no saved type. | Client-local (built-in list, checked against 1.9.2): the simulation systems are host-only (follower clock), their results (deleted citizens, storage, timers, workers) replicate, and a joiner needs nothing of it. `FuneralDirectorSystem` is **off on a joined player** from the start of the join (`JoinOffSystems`) so it never rewrites the host's workplaces with the player's own settings. The markers are not saved, so they are never sent. | Implemented | A joined player's info panels show deathcare prefab values (capacities, processing) without the host's scaling, and the hearse's speed scalar does not reach the player's copy of the prefab. Which hearse speed the host's movers use is the host's. |
| **Road Speed Adjuster** (125866) | D, T (direct) | `CustomSpeed` (saved, speed in km/h and mph) on road edges; its tool, and its panel, add it and write the lanes' speed limits (`CarLane`, `TrackLane`) directly; `RoadSpeedApplySystem` (ModificationEnd, every peer) rewrites the lane limits of an edge from its `CustomSpeed` whenever the edge updates. Local file `ModsData/RoadSpeedAdjuster/<city>_<city>.json`: per road a default and a current speed, keyed by the process-local entity index, read and written by `RoadSpeedSaveDataSystem` (opened by city name, saved on every tool use and when the system is destroyed); the same data lives in memory (`SpeedDataManager`). The types `SpeedOverride` and `SpeedCloneRequest` are never used. | The speed limits travel with the city: `CustomSpeed` is in the edge record (generic codec), the tool is a direct-write tool (changes found by the watcher), every peer derives the same lanes. The JSON is **not** needed for equal speed limits, so nothing of it is sent. `RoadSpeedSaveDataSystem` is **off on a joined player** from the start of the join: the player's file for a city of the same name is neither read (its entity indices belong to another game) nor written. Stores city data (built-in list, and the scan): a joiner needs the mod. | Implemented | "Reset to original" needs the saved default of the machine that set the speed (entity index) and does nothing on any other peer; "clear all" there finds no default, removes `CustomSpeed` (the mod's fallback) and lets the road's lanes derive again. Not tried in a live game. |
| **Zone Organizer** (95965) | L, P | When a game has loaded, `ZoneOrganizerSystem` (registered in no phase) creates `UIAssetCategoryPrefab`s (fixed names such as `Low Density Residential`, no asset) and moves the zone prefabs into them (`UIObjectData`, `UIGroupElement` and `UnlockRequirement` buffers of prefab entities). No saved type, no patch, no setting. | Client-local (built-in list, checked against 1.1.2: the analysis finds exactly the runtime prefabs of `CreateUIAssetCategoryPrefab`). The categories are toolbar menus that no city object references, named identically on every peer with the mod; a host's category a joiner lacks is ignored there (as ExtraLib's). | Implemented | Whether the categories show as unlocked on a joined player depends on the host's unlock replay of the zones (not tried live). |
| **Advanced Simulation Speed** (fork 124304, original 79794, assembly `AdvancedSimulationSpeed`) | L | A UI system (UIUpdate; `AdvancedSimSpeedUISystem` in the fork) replaces the time control buttons by previous/next buttons that write `SimulationSystem.selectedSpeed` (0 to 8: doubled or by a fixed step; 0.25x, 3x or 8x occur) and shows `smoothSpeed`. No patch, no saved type; its settings are mode, step and display options; the speed itself is never stored (the game starts every load at 1x or paused). The original 1.0.x fails to start in game 1.6.2 (`AssetDatabase.LoadSettings` missing). | Client-local (built-in list, checked against the fork's assembly 1.0.0.0, Paradox Mods v1.2.4). **The speed sync carries exact speeds** (see §1): a joined player's follower clock runs at 0.25x, 3x or 8x like the host, and the clock model keeps its host-frame estimate at that rate; a player's button press (a change of `selectedSpeed` the clock did not write) becomes a speed request with the exact value, which the host runs exactly only while it has this mod (`AllowsFineGameSpeed`, adapter active), else rounds to 1, 2 or 4. The mod's display on a joined player shows the host's speed (the clock writes it each frame). | Implemented | Verified from code; not tried live. The adapter's name check is for the fork (`AdvancedSimSpeedUISystem`): the original (type `UISystem`) gets the generic treatment, which is client-local too, but a host with it runs only rounded requests. A host at 8x is the host's choice and load. |
| **Skyve** (75804, assembly `Skyve Mod`) | L | A launcher for the external Skyve app: `InstallSkyveUISystem` (UIUpdate) starts the app's `Setup.exe` from a copy in the temp folder only when the player clicks install or update; at start it writes `FolderSettings.json` and `DlcConfig.json` under `ModsSettings/Skyve` and `ModsData/Skyve` and logs the active playset read through the Paradox SDK. The mod contains no network code; the external app (not loaded into the game) does the networking. `AchievementsSystem` (sets the platform's achievements flag after a load) is declared but registered nowhere in 1.3.1. | Client-local (built-in list, checked against 1.3.1). Never part of a join decision. Were the achievements system ever registered, a joined player's flag is kept off by `SnapshotLoader.HoldAchievementsOff` as for Achievement Fixer. | Implemented | The older Skyve builds (assembly `Skyve`) stay on the list by name only. |
| **Simple Mod Checker Plus** (79186) | L | At start: migrates its data folder (quits the game once, while the mod loads), scans the user folder for corrupt `.coc` files and shows a notification; a click on it deletes them **and quits the game** (any time, only by the player); backs up and restores game and mod settings once at start; at shutdown deletes `continue_game.json` and, with its option, the corrupt files. `AutosaveOffCheck` (MainLoop) posts a chirp through Custom Chirps when autosave is off. No saved type, no patch. | Client-local (built-in list, checked against 4.1.8); never sent to or required from other players. Nothing quits or deletes by itself in a session: the start-up work runs before any session, the cleanup quit needs the player's click. `AutosaveOffCheck` is **off on a joined player** from the start of the join (a chirp the host does not have). | Implemented | Settings backups made by the player during a session capture the host's values for the settings of §3 (the player's own return at the end); do not press "create backup" while joined. |

## 3. Settings that are the host's

| Mod | Properties |
|---|---|
| Anarchy | `AllowPlacingMultipleUniqueBuildings`, `MinimumClearanceBelowElevatedNetworks` |
| Better Bulldozer | `AllowRemovingSubElementNetworks`, `AllowRemovingExtensions`, `AutomaticRemovalManicuredGrass`, `AutomaticRemovalFencesAndHedges`, `AutomaticRemovalBrandingObjects` |
| Tree Controller | `UseDeadModelDuringWinter`, `DisableTreeGrowth`, `FreeVegetation` |
| Water Features | `EvaporationRate`, `Fluidness`, `Damping`, `ForceWaterSimulationSpeed`, `WaterCausesDamage`, `EnableSeasonalStreams`, `SimulateSnowMelt`, `ConstantFlowRate`, `StreamSeasonality`, `StreamStormwaterEffects`, `MinimumMultiplier`, `MaximumMultiplier`, `EnableWavesAndTides`, `WaveHeight`, `WaveFrequency`, `TideHeight`, `TideClassification` |
| Plop the Growables | `SpawnedZoneDespawn`, `LockPloppedBuildings` |
| Prefab Asset Fixes | `IndustrialCompanyWorker`, `Prison`, `Hospital`, `USSWHospital`, `HoveringPoles`, `RSClinic`, `LHTBusStation02`, `LHTTaxiDepot01`, `LHTTramDepot01`, `LHTCargoHarbor01`, `RHTBusStation01`, `NLLowHousehold`, `AdditionalTransformers`, `FRCiltyHall` (not `SolarParking`: its fix is empty in this version) |
| Placemaker | `ParkingEconomy`, `DefaultParkingFee`, `SurfaceClones`, `ApplyParkStats`, `RealParkStats`, `ExperimentalParkingCompanion`, `CompanionUpkeepFactor`, `ChargeNetConstruction`, `SelectAsOneThing` |

Not synced, by design: Transport Policy Adjuster (its six vehicle maximums size the player's own slider), Magic Hearse (its systems run on the host; the prefab scaling is host work), Advanced Simulation Speed (mode and step only choose the player's buttons; the speed itself travels with the speed sync), Road Speed Adjuster, Show Text Trends, Zone Organizer, Skyve and Simple Mod Checker Plus (display or personal), Traffic (UI/overlay settings only), Move It, Line Tool, Find It, Zoning Toolkit (no simulation
settings), 529 Tiles and Historical Start (their settings act only at load, which only the host performs in a session),
all UI-only mods, Placemaker's per-build options (clearing trees and props, keeping zoned buildings, collisions,
overlays on paved ground: each builder's own, like a tool mode) and its Outgrow options (Outgrow runs on the host only).
Settings objects are found on the mod's `IMod` instance by type, not through `ModSetting`'s static
registry (mods register a second "defaults" instance there), or given by the adapter when the mod keeps them elsewhere
(Placemaker: a static property of its tool registration).

## 4. Integration notes

- Record kinds 110 (`EnabledBits`, accepted from clients), 111 (`ModSettings`, host only) and 112 (`PrefabSource`, host
  only, written right before the `Prefab` record of a non-base-game prefab); manifest mod flag bit 2
  (`ClientLocalHint`). Mismatch report records `city-data`, `host-city-data`, `city-data-checked`, `rest-overridable`,
  `conflict-host`, `conflict-you` and the report kind `conflict` (text records that older readers skip; no layout
  change, §10). Message kind 110 (`ModChannel`, both directions, the host relays; branch range 110-114). The
  snapshot manifest has an optional trailing list of asset sources (written only when non-empty, so manifests of cities
  with base game prefabs only keep their bytes).
- `EntityCodec.Apply` has an optional `skip` filter (edit hold); `WriteEntity` appends an `EnabledBits` record when
  the entity has enableable replicated types; `HashEntity` includes disabled bits; `HashSubset` serves the watcher.
- Proposal validation: mover despawns are accepted in `DirectTool` proposals; spawns/upserts naming a prefab the host
  cannot resolve are refused with `HostError` (rollback on the originator).
- Everything an adapter names is resolved by reflection; a built-in adapter with any unresolved name is off (§5).
- `TypeHandlers` returns no handler for a mod type declared `never-replicate`, so the codec, the classifier and the
  watcher leave it out together.
- The diagnostics dump has a "Mod compatibility" section: built-in adapters (active, not installed, or disabled with
  the names that were not found), gated systems, the integration API users with their channel counters, the full
  analysis of every loaded mod (one line per component, system, tool, host setting, problem and hint), and every
  missing asset with its source and what to do. It also shows the join class of every loaded mod.
- Runtime prefabs on demand (§7): `SessionClient.MessageBuffered` shows the game layer each message buffered before the
  client is live (read-only); the joiner reads only the names of `Prefab` records from it. The adapter channel uses
  message kind 110 like a mod's channel; the Placemaker payload (`PlacemakerMessage`, kind byte first) is validated
  completely on the host, and the host acts only on requests of players who may use mod tools.
- Feature switch `mods` (mod integration API): when the guard trips it, mods see no session and send nothing; the
  rest of the session is unaffected.

## 5. Adapter version check

Every built-in adapter (§2) is knowledge about one version of its mod, verified against that version. When the mod is
detected, every name the adapter relies on is resolved: tool-local and never-replicated types, watched vanilla types,
client-off, join-off, load-off and kept systems (`PostDeserialize:T` wrappers included), pipeline tools, client-created
types, the members an adapter reads, writes or calls by reflection (`Members`: `Type::member`, Placemaker's 15), the
settings type and every synchronised setting property (readable, writable, of a supported type). If any of them is missing, the mod was
updated and the adapter as a whole is turned off (half of its rules could contradict the other half): one warning
line (`Traffic 1.2.3: adapter disabled, 2 of 14 names not found in this version (type ..., system ...); the generic
analysis applies instead`) and the same text in the diagnostics. The mod then gets exactly the generic treatment of
§6, and the rest of Skyline Together keeps working. Host and joiner run the same mod version (join check), so they
decide the same, unless the host let the joiner in with different mods ("join anyway"); each side then applies its own
decision to its own game, and the host's decides what replicates. Pure logic and texts: Core `AdapterCheck` (tests in `Modding/IlScannerAndAdapterCheckTests.cs`).

The built-in client-local classification of the mods checked for this (ExtraLib, Achievement Fixer, Cim Census, Cim
Route Highlighter, Show Text Trends, Skyve, Advanced Simulation Speed, Zone Organizer, Magic Hearse, Simple Mod Checker
Plus; tests in `Modding/SimpleModSupportTests.cs`) is tied to its version the same way: it lists the parts of that version's scan that look
world-affecting and were found to change nothing shared (Core `ModCompatibility.VerifiedLocalItems`, parts as
`ModClassifier.WorldItems` names them: saved components and systems, tools, systems per world phase, prefab types,
runtime prefabs per type, patches). A copy with any other part (a newer version that saves data, a system in another
phase, unknown phases, an unreadable patch list) logs `X 1.2 differs from the version its built-in classification was
checked against (...)`, sets `RegistersTypesOrSystemsOrPrefabs` in its handshake entry, and is classified like any
unknown mod by the host (tests in `Session/ModCompatibilityTests.cs`, `Modding/LocalModAdapterTests.cs`).

## 6. Any other mod: automatic analysis

Every loaded code mod (also those with an adapter) is scanned once per game, when the first session starts or the
handshake needs it, and the result is logged once per mod (`mod analysis: X 1.0 is world-affecting: ... (reasons)`)
and shown in full in the diagnostics. Nothing of the mod runs for it.

**Scan** (`Compat/ModAssemblyScanner`): ECS component types (data, buffer, tag, shared, managed; saved with
`ISerializable`/`IEmptySerializable` or not; enableable; `ISerializeAsEnabled`; entity fields), systems with the update
phases the game registered them in (read from `UpdateSystem`'s registration lists, the same probe as the follower
clock), whether they save state or run after load (`IPostDeserialize`), tools, prefab and prefab component types,
`ModSetting` properties, and Harmony patches (Harmony's own `GetAllPatchedMethods`/`GetPatchInfo`, called by reflection
on every loaded `0Harmony`; the patching mod is the declaring assembly of each patch method). Method bodies are read as
IL bytes (Core `IlScanner`, exact opcode lengths) and their tokens resolved to find calls of
`PrefabSystem.AddPrefab`/`AddOrUpdatePrefab` (runtime prefabs) and, inside tools, uses of `CreationDefinition`.
Checked offline against the mods of §2: runtime prefab creation is found in Find It, Water Features, Anarchy
(network upgrades), Traffic (its fake prefab) and ExtraLib (menus), matching §2. Calls of ExtraLib's prefab helpers
(`PrefabsHelper.LoadPrefab*`, `GetOrCreate*`, `Create*`) count as runtime prefab creation of the calling mod.

**Decisions** (Core `ModClassifier`, pure and deterministic, tested in `Modding/ModClassifierTests.cs`):

| Question | Rule (conservative) |
|---|---|
| Must it match on every peer? | Client-local only when the mod declares `client-local`, or when nothing in it can touch shared state: no saved component, no saved system, no prefab type, no runtime prefab, no tool, no system in a world phase (anything but UI, tooltips, rendering, culling, raycast, gizmos, editor), no UI system that writes the city (entity writes other than presentation-only components, or calls into the budget, tax, loan and map-tile systems), no Harmony patch outside presentation namespaces (`Game.UI`, `Colossal.UI`, localization, audio, rendering, input) and none of the game's world-writing UI types (`ModPhases`: policies, upgrades, names, colours, transport lines, service budgets, taxes, loans, map tiles, chirps, districts, the editor's map and water panels), and a readable patch list if it references Harmony. Unknown phases and failed scans count as world-affecting. The built-in lists of Core `ModCompatibility` still decide first for the join check. |
| How does each component travel? | Saved by the game: replicated (references as net ids, referenced mod entities bound with it, enabled bits, untagged changes watched). Not saved: never sent (local or derived data; the entity is unaffected). Declarations can exclude a saved type or mark tool entities, never force an unsaved type onto the wire. |
| Which systems run on clients? | `GameSimulation`: off (follower clock, like every vanilla simulation system). `PreSimulation`/`PostSimulation`/`LoadSimulation`: host only (they run with the simulation step; none of the mods of §2 uses these phases). Modification, tool, prefab, serialization and other phases: on every peer, like vanilla derivation. Declarations override. |
| How are its tools captured? | Direct-write unless declared `pipeline`: a missed direct write would lose a client's edit, an extra one only costs a redundant proposal. A tool that uses `CreationDefinition` gets a hint, not a different treatment (Move It does, and is direct-write). |
| Which settings are the host's? | Only declared `host-setting` properties (validated); a heuristic cannot tell a UI setting from a rule. |

Systems that save state or run after load, and saved components named like tool data, get hints for the mod's author
in the dump; they change nothing. Separately from all of these decisions, the scan's saved component types and saved
system states make the mod one that stores data in the city (`ModReport.CityData`, §10): a joiner without it is always
refused, whatever the mod declares. The result becomes a `GenericModAdapter` that runs next to a built-in adapter of the
same mod (it only adds generic and declared rules).

## 7. Missing assets and runtime prefabs

- **Source of every prefab.** The host writes a `PrefabSource` record before the `Prefab` record of each prefab that is
  not base game: `Package` (display name and Paradox Mods id of an asset pack or a code mod's assets), `LocalAsset`
  (the host's own, unpublished), or `RuntimePrefab` (no asset: created in code; the mod is named when the prefab's type
  or one of its prefab components comes from it; never guessed, since the game creates a few prefabs in code too).
  Never a file path.
- **Join notice.** Right after admission the joiner is told which of the host's asset packs it lacks or has in
  another version (by Paradox Mods id and the digest of each pack's prefab ids), independent of the city; the host
  option "require the same asset packs" refuses such a joiner instead (overridable with "join anyway" when the host lets
  players join with different mods).
- **Join message.** The join snapshot's manifest lists the city's asset sources with prefab counts. Before loading, the
  joiner compares them with its own (packages by Paradox Mods id among its loaded prefabs, runtime prefabs by the
  creating mod being loaded, the host's local assets by prefab name, which also finds DLC content without a package id;
  runtime prefabs of an unnamed mod are left to the per-record report) and shows one toast in the player's language
  naming what is missing and what to do, e.g. `China Pack (Paradox Mods 100454): subscribe to it, restart the game and
  join again` (Core `MissingAssets.Summary`, locale keys `st.toast.missingAssets.*`). The join goes on; objects using
  the missing prefabs are left out. Each missing prefab is also logged and listed in the diagnostics with its English
  explanation (`MissingAssets.Describe`), and the first missing record in a session raises the same kind of toast from
  the source records.
- **Runtime prefabs bind across peers** when their `PrefabID` matches (type, name, and the hash of their asset, which
  is empty for prefabs without one). When the exact id does not resolve, a local prefab with the same type and name is
  taken if the match is unambiguous and it is the same kind of prefab: the host's id has no hash, or the local prefab
  has no asset or no hash (a mod that reused a local file as the asset of a generated prefab). Two assets of different
  packages that share a name never match; two candidates are reported as missing rather than guessed (Core
  `MissingAssets.ChooseNameFallback`). The fallback is tried again only after the prefab count changed (a mod added
  prefabs), so records of a truly missing asset cost a dictionary lookup.
- Records whose prefab is missing are still never applied, and binding is still retried lazily (prefabs created after
  the join's prefab table).
- **Created on demand.** Some mods create runtime prefabs per player from data entirely contained in the name, and keep
  them in their own files: Placemaker's park companions (`Placemaker Park a30 l9 r6000 c3000 m16 u20000`: attractiveness,
  leisure, range, capacity, magnitude, upkeep, ` b` for a beach park) and its overlay copies of surfaces
  (`Placemaker Overlay (original)`). The host has those of its own builds, a joiner does not, so objects using them could
  never replicate (seen in a real session). The adapter recognises the canonical names (Core `PlacemakerNames`, bounded)
  and has the joiner's Placemaker create the same prefab through its own code (same type, name and no asset: the same
  `PrefabID`), with Placemaker's own changes to the companion's prefab data. Before the load, from the prefab table
  buffered at the start of the join, so the game's loader finds them in the city; when live, for a `Prefab` record that
  does not resolve: the records that need it wait (no missing-asset report), and their ids are asked for again
  (integrity repair) once the prefab is ready, at the latest after 120 s it becomes an ordinary missing asset. The other
  way round, a client's Placemaker that created such a prefab asks the host (adapter channel) to create it too; the host
  then binds it and sends its `Prefab` record, so the client's pieces can name it. At most 512 such prefabs per session
  (Core `RuntimePrefabBook`).

## 8. Integration API for mod authors

Documented for mod authors in [`../MOD-INTEGRATION.md`](../MOD-INTEGRATION.md). Summary of the implementation
(`Compat/ModIntegration`):

- No compile-time dependency: an attribute class named `SkylineTogetherAttribute` (any namespace) is read with
  `CustomAttributeData` (never constructed) on types, settings properties and the assembly; a static class named
  `SkylineTogetherIntegration` provides `ApiVersion` (a const, read without running the mod's code), `Declarations`
  lines (`"role: target"`), delegate fields Skyline Together sets (`IsInSession`, `IsHost`, `IsClient`, `LocalPlayer`,
  `Send`) and callbacks it calls (`OnSessionStarted(bool)`, `OnSessionEnded()`, `OnMessage(int, byte[])`).
- Roles: `replicate`, `never-replicate`, `tool-local` (components); `host-only`, `everywhere`, `join-off` (systems);
  `direct-write`, `pipeline` (tools); `host-setting` (settings); `client-local`, `world` (the mod); `viewer-message`
  (channel message kinds a viewer may send). Parsing (Core
  `ModDeclarations`) never throws; validation against the scan (Core `ModClassifier`) reports unknown roles, missing or
  wrong-kind targets, unsaved types declared `replicate`, unsupported settings, contradicting roles (both dropped) and
  newer API versions; every problem is ignored and listed.
- Declarations are read and the class bound once the game reports its mods initialized (session driver), or at the
  first session start, whichever comes first.
- Channel (Core `ModChannelMessage`, `ModChannelRouter`, `ModChannelBudget`): message kind 110, channel = the mod's
  assembly name, payload ≤ 16 KiB, 30 messages and 64 KiB per second per sender (one second of burst) checked by the
  sender and again by the host, which sets the sender id, relays client messages (everyone, host, one player; never back
  to the sender; only to live players) and drops undecodable or over-budget messages with one log line. A viewer may
  send only the message kinds the mod declares `viewer-message`; builders, co-hosts and the host may send any kind. Messages for a
  mod that is not installed or not listening are dropped and counted.
- A throwing callback is logged; after 3 failures the mod's message callback is off for the rest of the session;
  session start and end callbacks always run and never count. All handlers run under the
  `mods` feature guard, so nothing reaches the session.
- Verified offline: a sample mod with the attribute and the class from the guide compiles against the game's
  assemblies, and its declarations, problems and bound delegates come out as documented.

## 9. Residual risks

- Mods that store data in the city (§10): a mod whose saved types are not on any entity of this city, and that has no
  saved system, is still required, because the save may carry its trace anyway. Whether the game really crashes for
  every kind of missing saved type (a tag, a managed or shared component, a saved system) is not known; the rule
  treats every kind of saved type alike. A mod that stores data only through means the scan cannot see (for example its own
  files next to the save) is not covered. The one exception is a saved type known never to be written (Cim Route
  Highlighter's `DeleteTimer`): it is required only when the host's city has entities with it. That a type-table entry
  alone loads without the mod was read in the game's loader (`EntityDeserializer`, `ObsoleteComponentSerializer`), not
  yet tried in a live join.

- The built-in adapters follow the versions published when they were written; an adapter whose names no longer resolve turns
  itself off (§5). The phase lists and Harmony's patch list of the generic analysis are only available in the game.
- Unknown mods: the analysis cannot tell what a Modification-phase system writes. Such systems run on every peer like
  vanilla derivation; one that writes host-owned values with randomness or from local settings diverges on clients until
  the integrity repair, and on a client its untagged writes to saved mod components may be proposed to the host. The
  author can declare it `host-only`.
- Unknown mods' settings stay per player unless declared; a setting that changes the rules can make placements differ.
- Options-menu "apply to all" buttons of mods (Plop the Growables, Tree Controller, Better Bulldozer "safely remove")
  mutate the world outside any tool; pressed on a client they are not proposed (the integrity repair restores the host's
  state). Press them on the host.
- While a direct-write mod tool is active, entities changed only by local derivation may be proposed unchanged (no
  baseline hash yet); the host applies identical values.
- Client watcher proposals assume local mod systems do not rewrite replicated mod components outside the apply frame;
  the systems known to do so are gated or host-only.
- Placemaker (§2): terrain shaping and ground paint of a client's build stay local; Outgrow is the host's; group numbers
  of builds finished in the same moment by two players can collide. Runtime prefabs on demand rely on Placemaker
  creating the identical prefab from the name (verified in its code, not yet in a live two-player game).
- Simple mods (§2, written without a live two-player game): Prefab Asset Fixes depends on the setters of the mod redoing
  its edit when a value is written; Transport Policy Adjuster's slack factor and Zone Organizer's category unlocks on a
  joined player are estimates from the code; Road Speed Adjuster's "reset to original" works on the peer that set the
  speed only; Advanced Simulation Speed's exact speeds are carried and mirrored by code that the Core tests check as far
  as bytes and the clock model go, not with the fork running in two games.
- Missing local assets of the host cannot be fixed by the joiner; packages are detected by Paradox Mods id only (a
  package without an id counts as present).

## 10. Mods that store data in the city

**Why.** The game saves the component data of every `ISerializable`/`IEmptySerializable` component type with the
city, mods' types included, and the state of systems that implement `ISerializable`. Its loader cannot read a city that
contains a saved type of a mod it does not have: a joiner without Zoning Toolkit, admitted because the host does not
require the same mods, downloads the snapshot fine, and the game crashes natively while loading, right after
Player.log prints "Not serializable type: ZoningToolkit.Components.ZoningInfo, ZoningToolkit, ...". Admitting
such a joiner is never safe, so no option relaxes this rule.

**The rule** (Core `Session/CityDataRule`, checked first inside `CompatibilityCheck.Compare`):

| Case | Decision |
|---|---|
| The old multiplayer mod (`CS2MultiplayerMod` / Paradox Mods id 150432) in the host's or the joiner's list | refused, kind `conflict`: "The \"CS2 Multiplayer Mod\" is enabled, and two multiplayer mods can't run at the same time." (`st.ui.end.text.conflictYou` / `conflictHost`) for the side that has it; the host's panel warns while it is loaded |
| A host mod that stores city data, missing on the joiner | refused, never overridable (`RequireSameMods`, `AllowModMismatchOverride` and "join anyway" do not matter); "Enable these mods to join: ZoningToolkit (this city contains its data – loading it without the mod would crash the game)" |
| The same, but none of the mod's data is in the current city | refused all the same, worded "it can store data in a city – loading the city without the mod can crash the game": nothing proves the save carries no trace of it (saved system state is always written) |
| A saved type the mod is known never to write (Cim Route Highlighter's `DeleteTimer`), on no entity of the host's city | not city data: the save's type table lists every saved type of every loaded mod, used or not, and the game's loader reads an entry of an unknown type without data ("Not serializable type" warning, then the obsolete serializer); only an archetype with the type carries data. Counted by the host per handshake; outside a loaded city it counts as city data (fail closed) |
| A mod with city data only the joiner has | no difference for this rule (like adding a mod to a save); the usual mod comparison applies |
| Mods without city data, other versions, asset packs | unchanged: refused, told or overridable as the options say |

**Stores city data** means, on the host, for each loaded code mod: the scan (§6) found a component type the game saves
(any storage kind) or a system that saves state (`ISerializable`, `IDefaultSerializable`), or the scan failed, or the
built-in list knows the mod (Traffic, Anarchy, Better Bulldozer, Water Features, Zoning Toolkit, Plop the Growables,
Tree Controller, Placemaker, Road Speed Adjuster: class D in §2 or per-city settings), except saved types the built-in list knows the mod never writes
(Core `CityDataRule.DormantSavedTypes`, `CityDataEvidence.Dormant`): they count only when the loaded city has entities
with them. Declarations (`never-replicate`, `client-local`) never change it,
and neither does the join class: a client-local mod with a saved type is required too. The evidence is
`ModReport.CityData` (Core `Modding/CityDataEvidence`, shown as "city data: ..." per mod in the dump);
`Compat/CityDataProbe` fills the host-local fields of the handshake entry and, in a loaded city, counts the entities
that carry each saved type (one `EntityQuery` per type, prefabs excluded, disabled entities included) and the mod's
saved systems in the world. The host logs the required mods when hosting starts; the dump marks each one.

**On the wire** nothing changed layout: the refusal is a `mods` report with `city-data` records (name, version, id,
in-city flag; each also in `missing` for readers that do not know the record), `overridable` never set and
`rest-overridable` when the other differences could be passed with "join anyway" afterwards; the old mod is a
`conflict` report. When the mods differ and the joiner is admitted, the join notice carries `city-data-checked` and a
`host-city-data` record per host mod with city data. Unknown records are skipped by every reader, so no protocol
version was needed.

**The joiner's own check** (`SessionClient.StopForCityData`): when the join notice arrives, and again right before the
game's load is called, the client compares the host's `host-city-data` list with the mods its game has loaded (read
again; by Paradox Mods id or name). A notice of an older host (no `city-data-checked`) counts every mod the host has and
the joiner lacks. The joiner's own old multiplayer mod counts too. Any hit ends the join as refused with the same report
and screen, and the city is never loaded.

**UI.** The refusal screen **Mods required for this city** lists the required mods under "Enable these mods to join",
each with its reason, then the other differences under "Other differences"; there is no **Join anyway** button (also
not when a report claims to be overridable). The host gets "&lt;name&gt; can't join: they don't have ..., which store
data in this city" as a notification and a chat line (`SessionHost.JoinerBlocked`).
