# Mod compatibility

How Skyline Together keeps the user's installed Paradox mods in sync between host and clients. Research basis:
`docs/research/09-third-party-mods.md` (R09). Every statement about a mod below was checked against the **installed
version**, decompiled from `.cache/mods/pdx_mods/<id>_<version>` (decompiles under `mod_audit/decomp_mods/<name>`).
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
| **Mod entity family** | Entities defined only by mod data replicate as `ModEntity` even when they carry a `PrefabRef` (Traffic's lane connection data: `PrefabRef(Traffic.FakePrefab)`; Better Bulldozer's removal records: `PrefabRef` of the removed prefab). Before, they fell to `Generic` and never replicated. Move It's control points (`PrefabRef(Entity.Null)`) are tool-local through their adapter-local type. | `FamilyClassifier.Markers/Classify` |
| **Adapter-local types** | Types an adapter names (Traffic connectors, lane handles, temp data; Move It control points and overlays) make their entity tool-local ([families](../ARCHITECTURE.md#families)). | `ModAdapter.LocalTypes` → `FamilyMarker.AdapterLocal` |
| **Dependency binding** | A replicated value that references an unbound mod entity (a node's `ModifiedLaneConnections` → its data entity; an owner's removal buffer → record entities) no longer travels as "presence only": the capture binds the entity (host: new id; client: id from its granted pool) and spawns it in the same batch, so references resolve on apply. | `EntityCodec.DependencyBinder`, `SyncController.HostBindDependency/ClientBindDependency/WriteDependencies` |
| **Mod state watcher** | Mods write their own components without `Updated` (in-place buffer edits, structural add/remove from UI buttons). Per capture, chunk change versions of every mod component type (+ vanilla types adapters name) select candidates; a hash of the watched types (whole state for mod entities and water sources) finds real changes; a slow sweep (256/frame) catches removals. Host: upsert when the watched type set changed, values otherwise, max 1/s per ordinary entity, immediately for mod entities. Client: proposals (`DirectTool` flag → `ModTools` permission). The applier's own writes are refreshed into the baseline first, so nothing ping-pongs. | `Compat/ModStateWatcher`, `SyncController.CaptureModChanges` |
| **Enabled bits** | Enableable components (vanilla `Decoration` on trees placed outside decoration mode, Tree Controller's `LumberResource`, `CustomMeshColor`) now replicate their enabled bit (`SyncRecordKind.EnabledBits = 110`, right after the entity record, both directions; `ISerializeAsEnabled` types excluded). Before, every replicated tree was created with `Decoration` enabled. | `EntityCodec.WriteEntity/Apply` |
| **Edit hold** | A direct-write tool (Move It drag) proposes every frame; the host's echo of an older proposal would snap the object back. While the player keeps editing an entity (0.6 s after its last proposal) the client skips host records for it and applies the latest one when the hold ends, so the result always converges to the host's. Despawns are never held. | `SyncController.ApplyFromHost/ReleaseHolds` |
| **Pipeline tools** | Mod tools that apply through the Temp pipeline (Traffic, Line Tool, Find It's picker) are not direct-write tools: idle frames propose nothing, applies come through `ApplyTool` like vanilla ones. Every other mod tool is direct-write (fail safe). | `ModAdapter.PipelineTools` |
| **Placement relaxations** | The host's placement checks of client proposals (unlocks, owned map tiles, geometry) refuse only what a vanilla tool cannot produce. Mods that legitimately allow more switch checks off while they are loaded on the host: Anarchy and Line Tool tiles and geometry, Find It and Plop the Growables unlocks. A direct-write tool's proposal (Move It) skips them when the host runs any code mod ([placement checks](../ARCHITECTURE.md#placement-checks)). | `ModAdapter.RelaxedPlacementChecks`, `ModCompat.RelaxedPlacementChecks`, Core `PlacementPolicy` |
| **Client gates** | Mod systems that derive host-owned state are off on live clients (`ClientOffSystems`); load-time mutators driven by the player's own settings are off from the start of the join (`JoinOffSystems`, incl. the game's `PostDeserialize<T>` wrapper). Restored exactly at session end (`SystemSwitch`). Mod simulation systems are already off on clients (follower clock); adapters may keep ones that write no replicated state (`ClientSimulationSystems`). | `Compat/ModCompat` |
| **Mod settings channel** | Simulation-relevant `ModSetting` properties are the host's for everyone (`SyncRecordKind.ModSettings = 111`, host → clients, changes polled 1/s, everything on join). Clients write through the property setter (mods that react to a change do), call `Setting.Apply()`, and restore + re-save their own values at session end. Only bool/int/long/float/double/enum/string properties listed by an adapter are touched; buttons never. | `Compat/ModSettingsSync`, Core `SettingValueText`, `HostSettingsTracker`, `ClientSettingsTracker` |
| **Missing assets** | A record whose `PrefabRef` cannot be resolved (asset pack or runtime prefab missing) is never applied (an entity without its prefab breaks game systems); binding is retried lazily (runtime prefabs created after the join's prefab table), with a name fallback for runtime prefabs (§7). The host says where each non-base-game prefab comes from (`PrefabSource` record), so the joiner's message names the package and Paradox Mods id or the mod and what to do; the snapshot manifest lists the city's asset sources, checked before the load. The status line, one toast and the diagnostics dump list the missing assets. A client proposal of an object whose prefab the host lacks is refused (rollback). | `EntityCodec.MissingPrefab/NamesUnknownPrefab/NameFallback`, `Compat/AssetSources`, `SyncController.Validate`, Core `Modding/AssetSource` |
| **Join classification** | Mods that only affect their own player never block a join (missing on either side or another version). Built-in lists of known client-local and known world-affecting mods (by assembly name and Paradox id), then each peer's analysis of its own copy (§6): a mod that is client-local by its declaration or by the scan gets `ClientLocalHint` (manifest flag bit 2); otherwise `RegistersTypesOrSystemsOrPrefabs`. Unknown without a hint: world-affecting (fail closed). The structured `MismatchReport` and its one-line text simply leave the local mods out. | Core `Session/ModCompatibility`, `MismatchReport.CompareMods`, `Compat/ModAssemblyScanner`, Core `Modding/ModClassifier` |
| **Join check of DLC, asset packs and settings** | After the password the host compares, per category whose handshake digest differs, the joiner's DLC (refused when one of the host's is missing), asset packs by Paradox Mods id and contents (told; refused with the host option "require the same asset packs") and the current values of the settings of §3 (told; the host's values apply). Admitted joiners get the differences as a join notice ([handshake](../ARCHITECTURE.md#handshake-and-admission)). | Core `Session/CompatibilityCheck`, `CompatibilityDigests`, `Session/GameInfoProvider`, `AssetSources.Packages`, `ModSettingsSync.CurrentValues` |
| **Adapter version check** | Every name a built-in adapter relies on is resolved when the mod is detected; any missing name turns the whole adapter off with one log line and a diagnostics entry, and the mod gets the generic treatment (§5). | `ModCompat.Check`, Core `Modding/AdapterCheck` |
| **Generic analysis** | Every loaded code mod is scanned (types, IL bytes, the game's system registrations, Harmony's patch list) and classified with explained, conservative rules; its declarations are applied (§6). | `Compat/ModAssemblyScanner`, `HarmonyPatches`, `GenericModAdapter`, Core `Modding/ModClassifier` |
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
| **I18N Everywhere** (75426), **Extended Tooltip** (78188), **Asset Icon Library** (79634), **Unified Icon Library** (74417), **Toggleable Overlays** (80931), **Employment Tracker** (76662), **Cim Census** (85825), **Skyve** (75804) | L | Localization, tooltips, icons, overlays, statistics panels, playset manager. Some patch UI code (Harmony) or add `Highlighted`/`BatchesUpdated` (excluded from replication); Employment Tracker declares an unused serializable `DeleteTimer`. | Never block a join (built-in list); nothing to sync. | Implemented | — |
| **China Pack** (100454) | asset pack (`.cok`), no code | Prefabs (buildings/props). | Not a code mod (not in the mod list); its prefabs resolve by PrefabID; the join's content check and the missing-asset report cover a player without it. | Covered | — |

## 3. Settings that are the host's

| Mod | Properties |
|---|---|
| Anarchy | `AllowPlacingMultipleUniqueBuildings`, `MinimumClearanceBelowElevatedNetworks` |
| Better Bulldozer | `AllowRemovingSubElementNetworks`, `AllowRemovingExtensions`, `AutomaticRemovalManicuredGrass`, `AutomaticRemovalFencesAndHedges`, `AutomaticRemovalBrandingObjects` |
| Tree Controller | `UseDeadModelDuringWinter`, `DisableTreeGrowth`, `FreeVegetation` |
| Water Features | `EvaporationRate`, `Fluidness`, `Damping`, `ForceWaterSimulationSpeed`, `WaterCausesDamage`, `EnableSeasonalStreams`, `SimulateSnowMelt`, `ConstantFlowRate`, `StreamSeasonality`, `StreamStormwaterEffects`, `MinimumMultiplier`, `MaximumMultiplier`, `EnableWavesAndTides`, `WaveHeight`, `WaveFrequency`, `TideHeight`, `TideClassification` |
| Plop the Growables | `SpawnedZoneDespawn`, `LockPloppedBuildings` |

Not synced, by design: Traffic (UI/overlay settings only), Move It, Line Tool, Find It, Zoning Toolkit (no simulation
settings), 529 Tiles and Historical Start (their settings act only at load, which only the host performs in a session),
all UI-only mods. Settings objects are found on the mod's `IMod` instance by type, not through `ModSetting`'s static
registry (mods register a second "defaults" instance there).

## 4. Integration notes

- Record kinds 110 (`EnabledBits`, accepted from clients), 111 (`ModSettings`, host only) and 112 (`PrefabSource`, host
  only, written right before the `Prefab` record of a non-base-game prefab); manifest mod flag bit 2
  (`ClientLocalHint`). Message kind 110 (`ModChannel`, both directions, the host relays; branch range 110-114). The
  snapshot manifest has an optional trailing list of asset sources (written only when non-empty, so manifests of cities
  with base game prefabs only keep their bytes).
- `EntityCodec.Apply` gained an optional `skip` filter (edit hold); `WriteEntity` appends an `EnabledBits` record when
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
- New feature switch `mods` (mod integration API): when the guard trips it, mods see no session and send nothing; the
  rest of the session is unaffected.

## 5. Adapter version check

Every built-in adapter (§2) is knowledge about one version of its mod, verified against its decompile. When the mod is
detected, every name the adapter relies on is resolved: tool-local and never-replicated types, watched vanilla types,
client-off, join-off and kept systems (`PostDeserialize:T` wrappers included), pipeline tools, the settings type and
every synchronised setting property (readable, writable, of a supported type). If any of them is missing, the mod was
updated and the adapter as a whole is turned off (half of its rules could contradict the other half): one warning
line (`Traffic 1.2.3: adapter disabled, 2 of 14 names not found in this version (type ..., system ...); the generic
analysis applies instead`) and the same text in the diagnostics. The mod then gets exactly the generic treatment of
§6, and the rest of Skyline Together keeps working. Host and joiner run the same mod version (join check), so they
decide the same. Pure logic and texts: Core `AdapterCheck` (tests in `Modding/IlScannerAndAdapterCheckTests.cs`).

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
Checked offline against all installed mods: runtime prefab creation is found in Find It, Water Features, Anarchy
(network upgrades) and Traffic (its fake prefab), matching §2.

**Decisions** (Core `ModClassifier`, pure and deterministic, tested in `Modding/ModClassifierTests.cs`):

| Question | Rule (conservative) |
|---|---|
| Must it match on every peer? | Client-local only when the mod declares `client-local`, or when nothing in it can touch shared state: no saved component, no saved system, no prefab type, no runtime prefab, no tool, no system in a world phase (anything but UI, tooltips, rendering, culling, raycast, gizmos, editor), no Harmony patch outside presentation namespaces (`Game.UI`, `Colossal.UI`, localization, audio, rendering, input), and a readable patch list if it references Harmony. Unknown phases and failed scans count as world-affecting. The built-in lists of Core `ModCompatibility` still decide first for the join check. |
| How does each component travel? | Saved by the game: replicated (references as net ids, referenced mod entities bound with it, enabled bits, untagged changes watched). Not saved: never sent (local or derived data; the entity is unaffected). Declarations can exclude a saved type or mark tool entities, never force an unsaved type onto the wire. |
| Which systems run on clients? | `GameSimulation`: off (follower clock, like every vanilla simulation system). `PreSimulation`/`PostSimulation`/`LoadSimulation`: host only (they run with the simulation step; none of the installed mods uses these phases). Modification, tool, prefab, serialization and other phases: on every peer, like vanilla derivation. Declarations override. |
| How are its tools captured? | Direct-write unless declared `pipeline`: a missed direct write would lose a client's edit, an extra one only costs a redundant proposal. A tool that uses `CreationDefinition` gets a hint, not a different treatment (Move It does, and is direct-write). |
| Which settings are the host's? | Only declared `host-setting` properties (validated); a heuristic cannot tell a UI setting from a rule. |

Systems that save state or run after load, and saved components named like tool data, get hints for the mod's author
in the dump; they change nothing. The result becomes a `GenericModAdapter` that runs next to a built-in adapter of the
same mod (it only adds generic and declared rules).

## 7. Missing assets and runtime prefabs

- **Source of every prefab.** The host writes a `PrefabSource` record before the `Prefab` record of each prefab that is
  not base game: `Package` (display name and Paradox Mods id of an asset pack or a code mod's assets), `LocalAsset`
  (the host's own, unpublished), or `RuntimePrefab` (no asset: created in code; the mod is named when the prefab's type
  or one of its prefab components comes from it; never guessed, since the game creates a few prefabs in code too).
  Never a file path.
- **Join notice.** Right after admission the joiner is told which of the host's asset packs it lacks or has in
  another version (by Paradox Mods id and the digest of each pack's prefab ids), independent of the city; the host
  option "require the same asset packs" refuses such a joiner instead.
- **Join message.** The join snapshot's manifest lists the city's asset sources with prefab counts. Before loading, the
  joiner compares them with its own (packages by Paradox Mods id among its loaded prefabs, runtime prefabs by the
  creating mod being loaded, the host's local assets by prefab name, which also finds DLC content without a package id;
  runtime prefabs of an unnamed mod are left to the per-record report) and shows one toast naming what is missing
  and what to do, e.g. `The package "China Pack" (Paradox Mods 100454) (12 prefabs): subscribe to Paradox Mods 100454,
  restart the game and join again.` The join goes on; objects using the missing prefabs are left out. Each missing
  prefab is also logged and listed in the diagnostics with its explanation (Core `MissingAssets.Describe/Summary`), and
  the first missing record in a session raises the same kind of toast from the source records.
- **Runtime prefabs bind across peers** when their `PrefabID` matches (type, name, and the hash of their asset, which
  is empty for prefabs without one). When the exact id does not resolve, a local prefab with the same type and name is
  taken if the match is unambiguous and it is the same kind of prefab: the host's id has no hash, or the local prefab
  has no asset or no hash (a mod that reused a local file as the asset of a generated prefab). Two assets of different
  packages that share a name never match; two candidates are reported as missing rather than guessed (Core
  `MissingAssets.ChooseNameFallback`). The fallback is tried again only after the prefab count changed (a mod added
  prefabs), so records of a truly missing asset cost a dictionary lookup.
- Records whose prefab is missing are still never applied, and binding is still retried lazily (prefabs created after
  the join's prefab table).

## 8. Integration API for mod authors

Documented for mod authors in [`../MOD-INTEGRATION.md`](../MOD-INTEGRATION.md). Summary of the implementation
(`Compat/ModIntegration`):

- No compile-time dependency: an attribute class named `SkylineTogetherAttribute` (any namespace) is read with
  `CustomAttributeData` (never constructed) on types, settings properties and the assembly; a static class named
  `SkylineTogetherIntegration` provides `ApiVersion` (a const, read without running the mod's code), `Declarations`
  lines (`"role: target"`), delegate fields Skyline Together sets (`IsInSession`, `IsHost`, `IsClient`, `LocalPlayer`,
  `Send`) and callbacks it calls (`OnSessionStarted(bool)`, `OnSessionEnded()`, `OnMessage(int, byte[])`).
- Roles: `replicate`, `never-replicate`, `tool-local` (components); `host-only`, `everywhere`, `join-off` (systems);
  `direct-write`, `pipeline` (tools); `host-setting` (settings); `client-local`, `world` (the mod). Parsing (Core
  `ModDeclarations`) never throws; validation against the scan (Core `ModClassifier`) reports unknown roles, missing or
  wrong-kind targets, unsaved types declared `replicate`, unsupported settings, contradicting roles (both dropped) and
  newer API versions; every problem is ignored and listed.
- Declarations are read and the class bound once the game reports its mods initialized (session driver), or at the
  first session start, whichever comes first.
- Channel (Core `ModChannelMessage`, `ModChannelRouter`, `ModChannelBudget`): message kind 110, channel = the mod's
  assembly name, payload ≤ 16 KiB, 30 messages and 64 KiB per second per sender (one second of burst) checked by the
  sender and again by the host, which sets the sender id, relays client messages (everyone, host, one player; never back
  to the sender; only to live players) and drops undecodable or over-budget messages with one log line. Messages for a
  mod that is not installed or not listening are dropped and counted.
- A throwing callback is logged; after 3 failures the mod's callbacks are off for the game. All handlers run under the
  `mods` feature guard, so nothing reaches the session.
- Verified offline: a sample mod with the attribute and the class from the guide compiles against the game's
  assemblies, and its declarations, problems and bound delegates come out as documented.

## 9. Residual risks

- Nothing here could be run in the game; names and behaviour were verified against the decompiled installed versions,
  and the scan was run offline (outside the game, without system phases and Harmony) against every installed mod. The
  phase lists and Harmony's patch list are only available in the game.
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
- Missing local assets of the host cannot be fixed by the joiner; packages are detected by Paradox Mods id only (a
  package without an id counts as present).
