# 05 - Mod platform, UI and settings

> **Historical research report.** Written before and during the implementation; it describes the game, not the
> mod, and is not maintained. For how the mod works, see [docs/ARCHITECTURE.md](../ARCHITECTURE.md). Code is cited
> by file and member and described in prose; no game code is reproduced.

Research report for **Skyline Together** (clean-room co-op mod for Cities: Skylines II).

- All paths are relative to the root of the game's decompiled assemblies unless prefixed with `thirdparty/` (the published sources of those mods).
- **[V]** = verified by reading the decompiled code (file:line given). **[I]** = inference / design recommendation, not directly proven by code.
- Line numbers refer to the decompiled files as they exist in the audit folder.

---

## 0. TL;DR for the design

1. A code mod is any managed DLL in the asset database that has a top-level type implementing `Game.Modding.IMod`. The game instantiates **every** type assignable to `IMod` in that assembly with `FormatterServices.GetUninitializedObject` - **constructors and instance field initializers never run** [V `Game/Game.Modding/ModManager.cs:120-123`]. All initialization has to happen in `OnLoad`.
2. `OnLoad(UpdateSystem)` runs **once at boot**, after `CreateSystems()` and before prefabs are loaded and before `onWorldReady` [V `Game/Game.SceneFlow/GameManager.cs:621-635`]. It can also run **later at runtime** when the user enables a mod in the playset (`Initialize(..., reinitialize: true)`, `GameManager.cs:1644`). `OnDispose` only runs during application quit (`GameManager.cs:802`).
3. System registration through `UpdateSystem.UpdateAt/UpdateBefore/UpdateAfter` is permanent. There is no unregister API [V `Game/Game/UpdateSystem.cs:158-181`]. Systems run in main menu and in game alike unless they gate themselves. The exceptions are `GameSimulation`/`EditorSimulation`/`LoadSimulation`, which only tick when the simulation actually steps.
4. The authoritative speed knob is `SimulationSystem.selectedSpeed` (0 = paused). At least six independent code paths write to it locally (TimeUISystem forced pause, error dialogs, lightning, photo mode, the loading logic, debug/benchmark). A co-op mod has to **re-assert the host's speed every frame, right before `SimulationSystem` runs**. A `UpdateBefore<T>(SystemUpdatePhase.LateUpdate)` registration gives that slot [V/I, section 9].
5. UI: ship one `.mjs` UI module with the required header comment. The game registers its folder under `coui://ui-mods/` and passes it to the JS side via binding `app.activeUIModsLocation` [V `Game/Game.Modding/ModManager.cs:464-476`, `Colossal.IO.AssetDatabase/Colossal.IO.AssetDatabase/UIModuleAsset.cs:27-110`]. The module's default export `register(moduleRegistry)` can `append("Menu"|"Game"|...)` or `extend/override` vanilla components [V third-party `thirdparty/Anarchy/Anarchy/UI/types/modding.d.ts:4-22`]. The C# side talks to it via `ValueBinding`/`GetterValueBinding`/`TriggerBinding`/`CallBinding` registered from a `UISystemBase`.
6. Programmatic flow control: `GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, asset)`, `Load(GameMode.Game, Purpose.NewGame, mapMetadata)` and `GameManager.instance.MainMenu()` [V `GameManager.cs:1162-1285`]. `Load` has **no re-entrancy guard**. Serialize calls through `TaskManager.instance.EnqueueTask("SaveLoadGame", ..., 1)` the same way the vanilla menu does [V `Game/Game.UI.Menu/MenuUISystem.cs:716-805`].

---

## 1. Code mod discovery and loading

### 1.1 `IMod`
[V] `Game/Game.Modding/IMod.cs:3-8`: the interface has two methods, `OnLoad`, which receives the game's
`UpdateSystem`, and `OnDispose`.

### 1.2 Discovery: `ExecutableAsset`
- `ExecutableAsset.GetModAssets()` enumerates every IL assembly asset in `AssetDatabase.global`. It skips (with a warning) assemblies that duplicate a game assembly from the `Managed` folder [V `Colossal.IO.AssetDatabase/Colossal.IO.AssetDatabase/ExecutableAsset.cs:309-336`]. **[I]** Never ship game/Unity DLLs with the mod. Third-party networking DLLs (e.g. a transport library) are fine and get resolved as references.
- `ExecutableAsset.ResolveModAssets(typeof(IMod), assets)` groups duplicates by assembly name, resolves references first next to the asset (same sub-path), then through a resolver. It marks `isMod = true` only if a **top-level type in `MainModule.Types` directly lists `IMod` in its interfaces** [V `ExecutableAsset.cs:338-396`]. **[I]** Implement `IMod` directly on a top-level class. A nested class, or inheritance through a base class defined in another assembly, would not be detected.
- Duplicate resolution (`GetUniqueVersionAsset`) prefers: already loaded, then local (user `Mods` folder), then higher assembly version [V `ExecutableAsset.cs:179-189`]. **[I]** A local dev build overrides the PDX Mods subscription. Keep that in mind when testing with two clients.
- `isRequired` is true for mods, and for non-mod DLLs only when some required asset references them [V `ExecutableAsset.cs:161-173`].

### 1.3 Loading: `ModManager.ModInfo.Load`
[V] `Game/Game.Modding/ModManager.cs:92-145`:
1. Early exit unless `state == Unknown && asset.isRequired`.
2. Error states: `IsNotModWarning`, `IsNotUniqueWarning`, `MissedDependenciesError` (lists missing reference names in `loadError`).
3. `asset.LoadAssembly(AfterLoadAssembly, out uniqueAsset)`, which:
   - recursively loads references first,
   - loads the DLL **from bytes**: `Assembly.Load(bytes[, pdbBytes])` [V `ExecutableAsset.cs:229-242`]. **[I]** `Assembly.Location` is therefore empty. To find the mod folder (for native libs, config, UI images), use `GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset)` and then `asset.path` [V `ModManager.cs:550-565`].
   - loads optional Burst code: a sibling `"<AssemblyName>_win_x86_64"` library through `BurstRuntime.LoadAdditionalLibrary`. Non-local libraries are first copied to `EnvPath.kTempDataPath/BurstRuntime/<guid>/` [V `ExecutableAsset.cs:243-257`].
4. `AfterLoadAssembly` calls `TypeManager.InitializeAdditionalTypes(assembly)` (registers the mod's ECS component types) and `SerializerSystem.SetDirty()` (the serializer picks up mod serializable components) [V `ModManager.cs:147-151`].
5. Instances: `foreach (Type item in asset.assembly.GetTypesDerivedFrom<IMod>()) m_Instances.Add((IMod)FormatterServices.GetUninitializedObject(item));` [V `ModManager.cs:120-123`]. `GetTypesDerivedFrom` is just `assembly.GetTypes().Where(typeof(T).IsAssignableFrom)` with **no abstract/interface filter** [V `Colossal.Core/Colossal.Reflection/ReflectionUtils.cs:509-514`].
   - **Pitfall [V+I]:** constructors and instance field initializers do not run. `private readonly List<X> m_List = new();` on the mod class stays `null`. Static initializers still run normally.
   - **Pitfall [I]:** an abstract base class or a derived interface implementing `IMod` inside the mod assembly makes `GetUninitializedObject` throw, which puts the mod into `GeneralError`. Two concrete `IMod` classes both get instantiated and both receive `OnLoad`.
   - **Pitfall [I]:** `assembly.GetTypes()` throws `ReflectionTypeLoadException` if any type references a missing optional assembly. Keep optional integrations behind separate assemblies or use reflection.
6. Then `OnLoad(updateSystem)` is called for each instance, and `state = Loaded`.

Exceptions bubble to `InitializeMods`, which calls `modInfo.Dispose()` (so `OnDispose` is called even after a failed `OnLoad`) and logs [V `ModManager.cs:434-462`]. A per-mod failure notification is pushed to the notification UI with a dialog offering "Mod page" / "Disable" [V `ModManager.cs:267-337`].

### 1.4 When `OnLoad` / `OnDispose` run
Boot sequence in `GameManager.Initialize()` [V `Game/Game.SceneFlow/GameManager.cs:577-680`]:

| Step | Line | Notes |
|---|---|---|
| `InitializeLocalization()`, `settings = new SharedSettings(...)` | 596-597 | |
| `CreateWorld()` (World "Game", `UpdateSystem`, `LoadGameSystem`, `SaveGameSystem`) | 598, 2379-2390 | single ECS world = `World.DefaultGameObjectInjectionWorld` |
| `InitializeUI()` (cohtml, `UserInterface`, `NotificationUISystem`, `OptionsUISystem`, `State.UIReady`) | 599, 1754-1789 | |
| `m_ModManager = new ModManager(disableCodeModding)` | 611 | pushes "ModsLoading" notification |
| `settings.LoadUserSettings()` | 616 | |
| `CreateSystems()` = `SystemOrder.Initialize(m_UpdateSystem)` | 621, 2392-2398 | all vanilla registrations happen **before** mods |
| **`InitializeModManager()`, the mods' `OnLoad`** | 624, 674-680 | then UI modules are registered (`InitializeUIModules`) |
| `settings.Apply()` | 626 | |
| `m_State = BootingPrefabs; await LoadPrefabs(...)` | 629-630 | **prefabs are not loaded yet during `OnLoad`** |
| `m_State = WorldReady`, `onWorldReady` | 632-635 | systems created in `OnLoad` get `OnWorldReady()` |
| `await MainMenu()` (or auto-load via `-startGame=`/`-continuelastsave`) | 641-660 | |

- **Runtime re-initialization [V]:** when the PDX playset changes at runtime, newly enabled IL assemblies trigger `m_ModManager?.Initialize(m_UpdateSystem, reinitialize: true)`. Disabling a loaded code mod only triggers `RequireRestart()` [V `GameManager.cs:1584-1645`, `ModManager.cs:527-548`]. **[I]** `OnLoad` may therefore run while the player is already in the main menu or even in a city. Do not assume boot context, and read `GameManager.instance.gameMode` in `OnLoad`.
- **`OnDispose` [V]:** only from `TerminateGame()`: `m_Cts.Cancel(); m_ModManager?.Dispose(); DestroyWorld(); ...` [V `GameManager.cs:801-803`]. Settings are saved before that (`SaveAllSettings`, line 798). **[I]** Network shutdown must not depend only on `OnDispose`. Also hook "leaving the city" (`OnGameLoadingComplete` with `GameMode.MainMenu`), and disconnect cleanly before quit.

### 1.5 Useful `ModManager` queries (mod-set handshake)
- `GameManager.instance.modManager` is an `IEnumerable<ModInfo>`. `ModInfo` exposes `name` (= assembly full name via `asset.fullName`), `assemblyFullName`, `isLoaded`, `state`, `asset` (gives `asset.version`, `asset.GetMeta().platformID`, `asset.isLocal`) [V `ModManager.cs:31-79`, `ExecutableAsset.cs:149`].
- `ListModsEnabled()` = loaded code mods + **all UI module names** [V `ModManager.cs:222-228`].
- Game build: `Game.Version.current` (`Colossal.Version`, `fullVersion` string) [V `Game/Game/Version.cs:825`, `Colossal.Core/Colossal/Version.cs:109`].
- **[I]** Skyline Together should exchange (game `fullVersion`, own assembly version, sorted list of loaded code-mod assembly names + versions, UI module ids) during join, and refuse or warn on mismatch. See section 3.4 for why the mod set affects simulation scheduling.

---

## 2. `UpdateSystem`: system registration and ordering

[V] `Game/Game/UpdateSystem.cs`:
- `UpdateAt<T>(phase)` registers with `addIndex = ++m_AddIndex` (line 158-161).
- `UpdateBefore<T>(phase)` registers with `addIndex = ++m_AddIndex - 1000000`, i.e. at the **start** of the phase (line 163-166).
- `UpdateAfter<T>(phase)` registers with `addIndex = ++m_AddIndex + 1000000`, i.e. at the **end** of the phase (line 168-171).
- `UpdateBefore<T, Other>(phase)` / `UpdateAfter<T, Other>(phase)` attach `T` relative to `Other` through `m_RefMap` (negative addIndex = before, non-negative = after) (line 173-181, 278-292, 382-455).
- Sorting: by phase, then addIndex (`SystemData.CompareTo`, line 39-47). Because vanilla registers first (`SystemOrder.Initialize`), a mod's `UpdateAt` lands **after all vanilla `UpdateAt` systems of that phase** but before vanilla `UpdateAfter` ones. A mod's `UpdateBefore(phase)` lands after vanilla `UpdateBefore` systems but before every `UpdateAt`.
- Per-system exceptions are caught and logged as Critical ("System update error during {phase}->{system}") (line 201-214). One failing system does not stop the phase.
- `GameSystemBase.GetUpdateInterval(phase)` (must be a power of 2) and `GetUpdateOffset(phase)` (`-1` = auto) control stepping inside the simulation phases (`Update(phase, updateIndex, iterationIndex)`, line 223-269; `GameSystemBase.cs:131-139`).
- **No unregister API exists.** Systems registered in `OnLoad` live for the whole process.
- Entities 1.x semantics [V `Unity.Entities/Unity.Entities/SystemState.cs:420-445`]: a system runs every time its phase runs unless it calls `RequireForUpdate` (or uses `RequireMatchingQueriesForUpdate`) or sets `Enabled = false`.

### 2.1 Top-level frame structure
[V] `GameManager.Update()` (`GameManager.cs:712-725`): `InputManager.Update()`, UI input dispatch, `UpdateWorld()` (= phase `MainLoop`, line 2401-2408), `UpdateUI()` (cohtml), `PostUpdateWorld()` (= phase `Cleanup`), `MainThreadDispatcher.UpdateUpdaters()`.
[V] `GameManager.LateUpdate()` (line 727-733): `LateUpdateWorld()` = phases `LateUpdate` and `DebugGizmos` (line 2418-2426).
World phases run only when `shouldUpdateWorld` = `m_State >= State.WorldReady` (line 296). The enum order is `Booting, Terminated, UIReady, BootingPrefabs, WorldDisposed, WorldReady, Quitting, Loading` (line 124-134), so the world **also updates while `State.Loading`** [V].

`MainLoop` registration order [V `Game/Game.Common/SystemOrder.cs:47-77`]: (before) `UpdateWorldTimeSystem`, `PathfindQueueSystem`, `EndFrameBarrier`; (at) `RaycastSystem`, `PrefabSystem`, `CityConfigurationSystem`, `ToolSystem`, `LoadGameSystem`, `ModificationSystem`, `UnlockSystem`, `AllowBarrier<EndFrameBarrier>`, `PreRenderSystem`, `PathfindSetupSystem`, `PathfindResultSystem`, `AchievementTriggerSystem`, `UIUpdateSystem`, `RenderingSystem`, `SaveGameSystem`, `DebugWatchSystem`; (after) `PrepareCleanUpSystem`.
`LateUpdate`: `DebugSystem`, `SimulationSystem`, `CompleteRenderingSystem`, `GizmosSystem`, `AutoSaveSystem`.

### 2.2 Which phases run where (main menu vs in game)

| Phase | Driver (file:line) | Main menu | Loading | In game, paused | In game, running |
|---|---|---|---|---|---|
| MainLoop | `GameManager.UpdateWorld` 2406 | yes | yes | yes | yes |
| Modification1..ModificationEnd | `ModificationSystem.OnUpdate` (`Game/Game.Common/ModificationSystem.cs:19-26`), unconditional | yes | yes | yes | yes |
| PreTool / ToolUpdate / PostTool | `ToolSystem.OnUpdate` (`Game/Game.Tools/ToolSystem.cs:252-261, 327`). The tool action barrier is blocked when `!gameMode.IsGameOrEditor() \|\| isGameLoading` (line 253) | yes (input blocked) | yes (blocked) | yes | yes |
| ClearTool / ApplyTool | `ToolOutputSystem` depending on `ToolSystem.applyMode` (`Game/Game.Tools/ToolOutputSystem.cs:21-29`) | rarely | - | when tools apply | when tools apply |
| UIUpdate | `UIUpdateSystem` (`Game/Game.UI/UIUpdateSystem.cs:19`) | yes | yes | yes | yes |
| UITooltip | `TooltipUISystem` when its widget binding is observed and the mouse is not over UI (`Game/Game.UI.Tooltip/TooltipUISystem.cs:48-55`) | - | - | yes | yes |
| Rendering, PreCulling, CompleteRendering, Raycast, PrefabUpdate | `RenderingSystem:134`, `PreRenderSystem:23`, `CompleteRenderingSystem:53`, `RaycastSystem:815`, `PrefabSystem:759` | yes | yes | yes | yes |
| PreSimulation / PostSimulation | `SimulationSystem.OnUpdate` (`Game/Game.Simulation/SimulationSystem.cs:272, 295`), **every frame, even paused** | yes | yes | yes | yes |
| GameSimulation | `SimulationSystem` (line 284-287), only for steps where `num != 0` and `ToolSystem.actionMode.IsGame()` | **no** | no | **no** | yes (0-8 steps/frame) |
| EditorSimulation | same, `actionMode.IsEditor()` (line 280-283) | no | no | no | editor only |
| LoadSimulation | `SimulationSystem.UpdateLoadingProgress` (line 164-184), only `Purpose.NewGame` (1024 frames, 8 per rendered frame) | no | new game only | no | no |
| Deserialize | `LoadGameSystem.OnUpdate` (`Game/Game.Serialization/LoadGameSystem.cs:51-57`), once per load | - | once | - | - |
| Serialize | `SaveGameSystem:82`, when saving | - | - | on save | on save |
| PrefabReferences | `ResolvePrefabsSystem:521`, `BeginPrefabSerializationSystem:272` | - | on load | on save | on save |
| LateUpdate, DebugGizmos, Cleanup | `GameManager` 2406-2424 | yes | yes | yes | yes |

Notes:
- `ToolSystem.actionMode` is set only in `ToolSystem.OnGamePreload` (`ToolSystem.cs:180-183`). On the first main menu after boot it is still `GameMode.Other` (no preload happens, see section 3). Either way `GameSimulation` does not run in menu [V].
- **[I]** Network pump / message processing should live in `MainLoop` (`UpdateBefore`, so incoming commands are applied before tools and modification run) or in `UIUpdate`. Never put it in `GameSimulation`, because nothing ticks there while paused or in menu.

---

## 3. GameManager: game mode, states, loading events

### 3.1 `GameMode`
[V] `Game/Game/GameMode.cs`: `[Flags] None=0, Other=1, Game=2, Editor=4, MainMenu=8, GameOrEditor=6, All=0xF`.
[V] `Game/Game/GameModeExtensions.cs`: `IsGame()`, `IsEditor()`, `IsGameOrEditor()`, `ToRichPresence()`.
`GameManager.gameMode` starts as `GameMode.Other` (`GameManager.cs:262`) and is assigned at the **start** of a load (`gameMode = mode;` line 1062). During loading it already holds the target mode, so combine it with `GameManager.instance.isGameLoading` (line 264-274: true for `Booting`, `BootingPrefabs`, `Loading`).

### 3.2 Events and `GameSystemBase` hooks
[V] `GameManager` events (line 306-314): `onGameSaveLoad(saveName, previewUri, start, success)`, `onGamePreload(purpose, mode)`, `onGameLoadingComplete(purpose, mode)`, `onWorldReady()`, `onFullscreenOverlayOpened()`.

[V] `Game/Game/GameSystemBase.cs:17-31` subscribes every `GameSystemBase` to these events in `OnCreate`, and exposes virtuals (line 111-129):
- `OnWorldReady()`: once at boot (`GameManager.cs:635`).
- `OnGamePreload(Purpose, GameMode)`: raised by `LoadSimulationData` (`GameManager.cs:1031-1038`) **before** deserialization.
- `OnGameLoaded(Context)`: raised by `LoadGameSystem.onOnSaveGameLoaded` after the `Deserialize` phase. Only for systems in the default world (`GameSystemBase.cs:21-26`, `LoadGameSystem.cs:51-57`).
- `OnGameLoadingComplete(Purpose, GameMode)`: after the loading screen finished (`GameManager.cs:1136`).
- `OnFocusChanged(bool)`.
- Exceptions in `OnGamePreload`/`OnGameLoaded`/`OnWorldReady` **disable the system** (`Enabled = false`). In `OnGameLoadingComplete` they are only logged (line 60-109).

### 3.3 Load sequence (`GameManager.Load` private overload)
[V] `GameManager.cs:1053-1152`:
1. Rejects an invalid descriptor unless purpose is `NewGame`/`NewMap`/`Cleanup` (1056-1060).
2. `oldMode = gameMode; gameMode = mode; m_State = State.Loading;` (1061-1063).
3. Loading screen shown (`SetScreenActive<LoadingScreen>()`, 1096).
4. `HandleLocaleCaching` (1103).
5. `if (mode != MainMenu || oldMode != Other)` then `LoadSimulationData` = **`onGamePreload`**, then `LoadGameSystem.RunOnce()` (Deserialize phase, **`OnGameLoaded`**) (1105-1109). **The very first main menu after boot gets no preload and no deserialize.**
6. Calls `appBindings.SetGameActive()/SetEditorActive()/SetMainMenuActive()`, telemetry session, target framerate (120 in menu) (1110-1128).
7. `await loading`, `CleanupMemory()`, **`onGameLoadingComplete(purpose, mode)`**, `m_State = WorldReady` (1130-1137).

`MainMenu()` = `Load(GameMode.MainMenu, Purpose.Cleanup, null)` + menu music (1266-1285). Every "exit to menu" therefore raises `OnGamePreload(Cleanup, MainMenu)`, a Deserialize with an invalid descriptor (defaults), and `OnGameLoadingComplete(Cleanup, MainMenu)` [V].

`Purpose` here is `Colossal.Serialization.Entities.Purpose` = `SaveGame, NewGame, LoadGame, SaveMap, NewMap, LoadMap, Cleanup` [V `Colossal.Core/Colossal.Serialization.Entities/Purpose.cs`]. Do not confuse it with `Game.Citizens.Purpose` [V `Game/Game.Citizens/Purpose.cs`].

**[I] Recommended lifecycle hooks for Skyline Together:**
- "Entered city": `OnGameLoadingComplete(purpose, mode)` with `mode.IsGame()` (safe point: world deserialized, loading screen gone, `SimulationSystem` already decided paused/unpaused).
- "Left city": `OnGamePreload(Purpose.Cleanup, GameMode.MainMenu)` (earliest; the world is still intact before deserialize resets it). Disconnect or stop replication here.
- Main menu ready (including first boot): `OnGameLoadingComplete(_, GameMode.MainMenu)`.

---

## 4. Programmatic navigation: load save, new game, main menu

### 4.1 Public API
[V] `GameManager.cs`:
- `Task<bool> Load(GameMode mode, Purpose purpose, IAssetData asset = null)` (1162-1215) accepts `MapMetadata`, `SaveGameMetadata` (preferred, keeps session GUID), `MapData`, `SaveGameData` (warns that the session GUID is lost), or `null` (only valid for NewGame/NewMap/Cleanup).
- `Task<bool> Load(GameMode mode, Purpose purpose, Colossal.Hash128 guid)` looks up `AssetDatabase.global.TryGetAsset(guid, ...)` (1217-1225).
- `Task<bool> MainMenu()` (1266-1285).
- `Task<bool> Save(string saveName, SaveInfo meta, ILocalAssetDatabase database, Texture/AsyncRequest preview)` (945-1018). It builds the package in `AssetDatabase.GetTransient(0)` and copies it into `database`. **Side effect:** on success it sets `settings.userState.lastSaveGameMetadata` (the "Continue" button) and calls `Launcher.SaveLastSaveMetadata` (995-999).
- **No re-entrancy guard:** `Load` does not check `state == Loading` [V 1053-1064]. The vanilla menu serializes all save/load work through `TaskManager.instance.EnqueueTask("SaveLoadGame", ..., 1)` (`MenuUISystem.cs:716-719, 750-753, 784-787`), and quitting waits for that queue (`TaskManager.instance.Complete("SaveLoadGame")`, `GameManager.cs:794`).

### 4.2 What the vanilla menu sets up before calling `Load`
[V] `Game/Game.UI.Menu/MenuUISystem.cs`:
- **New game** (755-782): `m_MapMetadataSystem.mapName/prefabReferences`, `m_CityConfigurationSystem.overrideLoadedOptions = true`, `overrideThemeName = args.theme`, `m_GameModeSystem.overrideMode = args.gameMode`, `ApplyOptions(cityName, options)` (writes `override*` fields **and** `SharedSettings.userState`, 818-853), `PlatformManager.instance.achievementsEnabled = true`, `m_TimeSystem.startingYear`, then `GameManager.instance.Load(GameMode.Game, Purpose.NewGame, mapInfo.metaData)`.
- **Load game** (789-815): same pattern with `overrideThemeName = null`, `achievementsEnabled = !saveInfo.isReadonly`, then `Load(GameMode.Game, Purpose.LoadGame, saveInfo.metaData)`.
- **Continue** (721-747): uses `settings.userState.lastSaveGameMetadata` and does **not** set `overrideLoadedOptions`.
- **Exit to main menu** (1071-1086): `overrideLoadedOptions = false; overrideThemeName = null; await GameManager.instance.MainMenu();`.

[V] `CityConfigurationSystem` deserialization (`Game/Game.City/CityConfigurationSystem.cs:423-438`): with `overrideLoadedOptions == false` all options (city name, disasters, theme, LHT, unlimited money, unlock all, map tiles) come **from the save**. With `true` and `Purpose.LoadGame`, only theme and LHT come from the save, and the rest is kept from the override fields. The flag resets to false after each load.

**[I] Client join recipe:** after receiving the host's save into a (transient) local asset database:
1. Set `CityConfigurationSystem.overrideLoadedOptions = false`, so the host's options win exactly.
2. Set `MapMetadataSystem.mapName/prefabReferences` from the received `SaveInfo` (keeps UI/metadata consistent).
3. Enqueue `GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, receivedSaveGameMetadata)` on the `"SaveLoadGame"` task queue.
4. Do not let the client's `Save()` side effects pollute "Continue" (see section 9.4).
5. Leaving: `GameManager.instance.MainMenu()` (also reset `overrideLoadedOptions`/`overrideThemeName` like the menu does).

---

## 5. Settings: `Setting`, `ModSetting`, options UI, persistence

### 5.1 `Setting` base
[V] `Game/Game.Settings/Setting.cs`:
- `abstract void SetDefaults()` (163). `virtual void Apply()` raises `onSettingsApplied` (157-161). `virtual async void ApplyAndSave()` = `Apply()` + `AssetDatabase.global.SaveSpecificSetting(GetType().Name)` (151-155).
- Equality and hashing are reflection-based over public properties (`[IgnoreEquals]` excludes) (26-71).
- `RegisterInOptionsUI(Setting, name, addPrefix)` goes through `World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<OptionsUISystem>().RegisterSetting(...)` (179-188).

### 5.2 `ModSetting`
[V] `Game/Game.Modding/ModSetting.cs`:
- ctor `ModSetting(IMod mod)`: `id = <ModAssembly>.<ModNamespace>.<ModTypeName>`, `name = GetType().Name`. Registers itself in a static dictionary and auto-generates a `ProxyBinding` value for every public read/write `ProxyBinding` property (`InitializeKeyBindings`) (36-64).
- `RegisterInOptionsUI()` calls `RegisterInOptionsUI(id, addPrefix: true)`. `UnregisterInOptionsUI()` (46-54).
- Localization key helpers (303-371): `GetSettingsLocaleID()` = `Options.SECTION[id]`, `GetOptionLabelLocaleID(opt)` = `Options.OPTION[id.name.opt]`, `GetOptionDescLocaleID`, `GetOptionWarningLocaleID`, `GetOptionTabLocaleID(tab)` = `Options.TAB[id.tab]`, `GetOptionGroupLocaleID`, `GetEnumValueLocaleID<T>(v)` = `Options.{id}.{TYPE}[{v}]`, `GetBindingKeyLocaleID(action)` = `Options.OPTION[id/action/binding]`, `GetBindingKeyHintLocaleID`, `GetBindingMapLocaleID`.
- Key bindings: `RegisterKeyBindings()` builds `ProxyAction.Info`s from the binding properties + class-level `SettingsUI*ActionAttribute`s and calls `InputManager.instance.AddActions(...)`. It installs watchers so rebinds write back into the properties (141-231). `[AfterDecode] ApplyKeyBindings()` pushes loaded bindings into the `InputManager` (269-277). `GetAction(name)` = `InputManager.instance.FindAction(id, name)` (289-292).

### 5.3 Persistence: `AssetDatabase.global.LoadSettings`
[V] `Colossal.IO.AssetDatabase/Colossal.IO.AssetDatabase/AssetDatabase.cs:613-666`:
- `LoadSettings(string name, object obj, object defaultObj = null, bool userSetting = false)`: for every database holding a `SettingAsset` named `name`, `JSON.WriteInto(fragment.variant, ref obj)` (mutates the existing instance) and records `source=obj`, `default=defaultObj`.
- If none exists, it creates a fragment in the user database in the file from `[FileLocation("...")]` (`.coc` extension forced, `FileLocationAttribute.cs`) or `FallbackSettings.coc`.
- `SaveSpecificSetting(settingName)` finds the setting whose fragment `source.GetType().Name == settingName` and rewrites that file (792-830). **[I]** The settings **class name** must be unique across all mods (it is the save key used by `ApplyAndSave`). Prefix it, e.g. `SkylineTogetherSettings`.
- `defaultObj` is used for "save only changed values" cleanup (`cleanupSettings` config, `GameManager.cs:592`).

Canonical third-party pattern [V `thirdparty/CS2-MoveIt/Code/MoveIt/Mod.cs:23-43`, `thirdparty/Anarchy/Anarchy/AnarchyMod.cs:106-127`]:
```csharp
Settings = new MySettings(this);
Settings.RegisterKeyBindings();
Settings.RegisterInOptionsUI();
GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
AssetDatabase.global.LoadSettings(nameof(MyMod), Settings, new MySettings(this));
```
Settings class with `[FileLocation(...)]`, `[SettingsUITabOrder]`, `[SettingsUIGroupOrder]`, `[SettingsUIShowGroupName]`, `[SettingsUIKeyboardAction(...)]` [V `thirdparty/CS2-MoveIt/Code/MoveIt/Settings/Settings.cs:11-80`].

### 5.4 Options UI generation (`AutomaticSettings`)
[V] `Game/Game.UI.Menu/AutomaticSettings.cs`:
- `FillSettingsPage` iterates **public instance properties**. It skips `[SettingsUIHidden]`, `[SettingsUIDeveloper]` and platform-excluded ones (989-1017).
- Widget mapping `GetWidgetType` (1049-1149):
  - `bool` r/w = toggle. `[SettingsUIButton]` or write-only = button. `[SettingsUIConfirmation]` = button with confirmation.
  - `int` needs `[SettingsUISlider]` or `[SettingsUIDropdown]`. `float` needs `[SettingsUISlider]`.
  - `string` r/w needs `[SettingsUITextInput]`, `[SettingsUIDropdown]` or `[SettingsUIDirectoryPicker]`. Read-only string = text field (`[SettingsUIMultilineText]` for multiline). Read-only `LocalizedString` = localized field.
  - `ProxyBinding` = key binding. Enum = dropdown.
  - Otherwise there is no widget (a property without the needed attribute is silently not shown).
- On edit, the accessor calls `setterAction?.Invoke(value)` (**before** the property is assigned), then `property.SetValue`, then `setting.ApplyAndSave()` (e.g. 1206-1213).
- `[SettingsUISetter(typeof(T), nameof(Method))]`: a `void Method(TValue)` lookup (static, or instance if `T` is the setting type). Enums also accept `int`/underlying type (423-462).
- `[SettingsUIDisableByCondition(type, method[, invert])]`, `[SettingsUIHideByCondition(...)]`: `Func<bool>`, evaluated live (464-510). `[SettingsUIValueVersion(type, method)]` (`Func<int>`) drives `itemsVersion`/`valueVersion` refresh of dropdowns and read-only fields (512-517, `Game/Game.UI.Widgets/DropdownField.cs:17,68`, `ReadonlyField.cs:22,44-46`). `[SettingsUIDropdown(type, method)]` returns `DropdownItem<T>[]` (836-844).
- `OptionsUISystem.RegisterSetting` builds the page **once** (`setting.GetPageData(id, addPrefix).BuildPage()`) and keeps the page index if it is re-registered with the same id (`Game/Game.UI.Menu/OptionsUISystem.cs:621-648`). **[I]** Page structure is static; values, visibility and dropdown items are dynamic. A live session list belongs in a custom UI panel, not the options page.

**[I] Settings for Skyline Together:** keep options in `ModSetting` for local-only preferences (player name, default port, UI toggles, key bindings). **Never** use options to change simulation-relevant state on one client. Session rules (host settings) must be replicated from the host as data, not read from each client's settings file.

---

## 6. Localization

- [V] `Colossal.Core/Colossal/IDictionarySource.cs:5-10`: `IEnumerable<KeyValuePair<string,string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string,int> indexCounts); void Unload();`
- [V] `Colossal.Localization/Colossal.Localization/MemorySource.cs`: dictionary-backed source. It handles indexed keys (`KEY:0`, `KEY:1`) by updating `indexCounts`.
- [V] `LocalizationManager.AddSource(localeId, source)` (`Colossal.Localization/Colossal.Localization/LocalizationManager.cs:313-355`): records it as a user source. If the locale is active it loads immediately and fires `onActiveDictionaryChanged`. If the locale is the fallback, it loads and back-fills missing entries into the active dictionary. `RemoveSource` reloads (357-380). The manager is reached via `GameManager.instance.localizationManager` (`GameManager.cs:300`). Other API: `activeLocaleId`, `onActiveDictionaryChanged`, `GetSupportedLocales()`.
- [V] The same `LocalizationManager` feeds cohtml via `settings.localizationManager = new UILocalizationManager(localizationManager)` (`GameManager.cs:1761`). **[I]** Keys added from C# are available to the React UI (`cs2/l10n`), so one source can serve options, notifications, dialogs and the custom HUD.
- [V] Third-party mods load extra languages from embedded JSON into `MemorySource` per locale (`thirdparty/Anarchy/Anarchy/AnarchyMod.cs:197`).

---

## 7. UI: Colossal.UI.Binding and `UISystemBase`

### 7.1 Binding primitives
All live in `Colossal.UI.Binding/Colossal.UI.Binding/`. The path is `group + "." + name` (`BindingBase.cs:32`).
- `ValueBinding<T>(group, name, initial, writer?, comparer?)`: `Update(newValue)` pushes only if `!comparer.Equals`. `TriggerUpdate()` forces a push. It sends on subscribe [V `ValueBinding.cs:6-55`].
- `GetterValueBinding<T>(group, name, Func<T> getter, ...)`: an `IUpdateBinding`. `Update()` polls the getter **only while observed** (`active`) and pushes on change [V `GetterValueBinding.cs:47-66`]. It must be added with `AddUpdateBinding` to be polled.
- `RawValueBinding(group, name, Action<IJsonWriter>)`: a full re-write on every `Update()` (returns `true` always). `PatchBegin()/PatchEnd()` send `.patch` events [V `RawValueBinding.cs:7-57`].
- `TriggerBinding`, `TriggerBinding<T>`, up to 4 args: JS to C# fire-and-forget. Callback exceptions are caught and logged [V `TriggerBinding.cs:7-70`].
- `CallBinding<..., TResult>`: JS to C# with return value (Promise on the JS side) [V `CallBinding.cs`].
- `EventBinding` / `EventBinding<T>`: C# to JS event (`Trigger(value)`) [V `EventBinding.cs`]. `observerCount` reveals whether JS listens (`EventBindingBase.cs:14-16`), a fact vanilla uses as a "barrier" (section 9.2).
- Readers/writers: `ValueWriters.Create<T>()`, `ListWriter`, `CollectionWriter`, `ValueWriter<T>` for `IJsonWritable`, `ValueReader<T>` for `IJsonReadable`, `EnumWriter/EnumReader`, nullable wrappers.

### 7.2 `UISystemBase`
[V] `Game/Game.UI/UISystemBase.cs`:
- `AddBinding(b)` registers with `GameManager.instance.userInterface.bindings`. `AddUpdateBinding(b)` also polls it every `OnUpdate` (48-58, 40-46). Bindings are removed in `OnDestroy`.
- `virtual GameMode gameMode => GameMode.All`. In `OnGamePreload`: `Enabled = (gameMode & mode) != 0` (60-64).
- **Caveat [V+I]:** since the first main menu after boot gets no `OnGamePreload` (section 3.3), a `UISystemBase` with `gameMode => GameMode.Game` is **still enabled in the first main menu** and only gets disabled after the first load/exit cycle. Systems that must be menu-only or game-only should additionally check `GameManager.instance.gameMode`.
- Register UI systems at `SystemUpdatePhase.UIUpdate` (that is what every vanilla UI system does, `SystemOrder.cs:899-948`, and what MoveIt does, `thirdparty/CS2-MoveIt/Code/MoveIt/Mod.cs:41`). Because `UIUpdate` runs in the menu too, a "Multiplayer" main-menu panel's C# backend works there.

### 7.3 Shipping a UI module (.mjs)
[V] `Colossal.IO.AssetDatabase/Colossal.IO.AssetDatabase/UIModuleAsset.cs`:
- Extension `.mjs` (line 27). `PostCreate` parses the first block comment. It requires a line exactly `Cities: Skylines II UI Module`, then `Id:`, `Author:`, `Version:`, `Dependencies:` (comma separated) (45-110). `name` = module id. `couiPath => "coui://ui-mods/" + fileName` (41).

[V] `ModManager.InitializeUIModules()` (`Game/Game.Modding/ModManager.cs:464-476`): for every `UIModuleAsset` in the global DB it calls `UIManager.defaultUISystem.AddHostLocation("ui-mods", Path.GetDirectoryName(path), isLocal)` and adds `couiPath` to `appBindings.AddActiveUIModLocation(list)`. That updates binding `app.activeUIModsLocation` (a `HashSet<string>`, `Game/Game.UI/AppBindings.cs:206, 248-259`), which the game's JS uses to import the modules. Runtime add/remove: `AddUIModule/RemoveUIModule` (478-496). **[I]** The third bool argument of `AddHostLocation` is "shouldWatch" (cf. `GameManager.cs:1774` named argument), so local modules hot-reload during development.

Build pipeline used by the community (third-party, toolchain) [V `thirdparty/Anarchy/Anarchy/UI/webpack.config.js`]:
- Output to `%CSII_USERDATAPATH%\Mods\<id>`, ES module (`library.type: "module"`, `experiments.outputModule`), `publicPath: coui://ui-mods/`.
- Externals from `window`: `react`, `react-dom`, `cs2/modding`, `cs2/api`, `cs2/bindings`, `cs2/l10n`, `cs2/ui`, `cs2/input`, `cs2/utils`, `cohtml/cohtml`.
- The Terser banner emits the exact header the parser expects (`Cities: Skylines II UI Module`, `Id`, `Author`, `Version`, `Dependencies`).

JS registration API [V `thirdparty/Anarchy/Anarchy/UI/types/modding.d.ts:4-22`]:
```ts
type AppendHookTargets = "Menu" | "Editor" | "Game" | "GameTopLeft" | "GameTopRight" | "GameBottomRight" | "UniversalModMenu";
type ModuleRegistry = { get, add, override(path, export, value), extend(path, exportOrScss, cb), append(path|target, component, index?), hasAppend, find(query), registry, reset };
export default register: ModRegistrar = (moduleRegistry) => { ... }
```
Examples: `moduleRegistry.append("Menu", Comp)` (main menu), `append("Game", Comp)` (in-game HUD), `extend("game-ui/game/components/toolbar/top/toggles.tsx", "PhotoModeToggle", Button)` [V `thirdparty/CS2-MoveIt/UI/src/index.tsx:13-16`, `thirdparty/Anarchy/Anarchy/UI/src/index.tsx:22-43`].
JS binding API [V `thirdparty/Anarchy/Anarchy/UI/types/api.d.ts:28-47`]: `bindValue<T>(group, name, fallback)`, `useValue`, `bindTrigger`, `trigger(group, name, ...args)`, `call<T>(group, name, ...args)`, `bindEvent`, `bindMap`, `useMapValue`.
Debugging: `-uiDeveloperMode` launch option enables the cohtml debugger (`GameManager.cs:372-375`). Third-party comments point at `localhost:9444`.

**[I] Skyline Together UI plan:**
- One module `SkylineTogether.mjs`: `append("Menu", MultiplayerMenuEntry)` (host/join panel), `append("Game", CoopHud)` (player list, chat, connection/latency status, "waiting for host" overlay), and optionally `extend` the time controls to show host-owned speed.
- One C# `UISystemBase` at `UIUpdate`, binding group `"skylineTogether"`. Expose `GetterValueBinding`s for session state, `RawValueBinding` for player lists (patchable), `TriggerBinding`s for actions (host, join, kick, send chat) and `CallBinding`s for validation.
- Keep binding updates diff-based (`GetterValueBinding` comparers / `ValueBinding.Update`), because JSON push cost is per change.

### 7.4 Text input and hotkeys
[V] cohtml text focus sets `InputManager.instance.hasInputFieldFocus` (`Game/Game.SceneFlow/UserInterface.cs:168-171`). With keyboard+mouse that changes the active device mask to Mouse only (`mask 2`), so **keyboard actions are disabled while a UI text field is focused** (`Game/Game.Input/InputManager.cs:644-651`, `DeviceType Keyboard=1, Mouse=2, Gamepad=4` at 112-119). **[I]** A chat box built with a normal input element will not trigger game or mod hotkeys.

---

## 8. Notifications, dialogs, chirper, pause menu

### 8.1 Notifications (top-left toasts)
[V] `Game/Game.PSI/NotificationSystem.cs:22-35` (static):
`Push(identifier, LocalizedString? title, LocalizedString? text, string titleId, string textId, string thumbnail, ProgressState? progressState, int? progress, Action onClicked)`, `Pop(identifier, float delay, ...)`, `Exist(identifier)`.
- `titleId`/`textId` resolve to `Menu.NOTIFICATION_TITLE[id]` / `Menu.NOTIFICATION_DESCRIPTION[id]` (`Game/Game.UI.Menu/NotificationUISystem.cs:164-172`).
- Backed by `NotificationUISystem` (group `notification`, `UIUpdate`, no `gameMode` override, so it runs in all modes) (`NotificationUISystem.cs:137-153`, `SystemOrder.cs:904`).
- `ProgressState` (Indeterminate/Complete/Warning/Failed...) gives progress UI (see `ModManager.cs:240-242, 351`).
- **[I]** Ideal for "Connecting...", "Downloading city 43%", "Player X joined/left", "Desync detected, resyncing". Works in menu and in game.

### 8.2 Dialogs
[V] `GameManager.instance.userInterface.appBindings`:
- `ShowConfirmationDialog(ConfirmationDialog, Action<int>)`, `ShowConfirmationDialogAndWait(ConfirmationDialog)`, `ShowMessageDialog(MessageDialog, Action<int>)`, `ShowConfirmationDialog(DismissibleConfirmationDialog, Action<int,bool>)` (`Game/Game.UI/AppBindings.cs:380-404`).
- Constructors: `ConfirmationDialog(title?, message, confirmAction, cancelAction?, params otherActions)` and the variant with `details, copyButton` (`Game/Game.UI/ConfirmationDialog.cs:8-13`). `MessageDialog(...)` likewise (`MessageDialog.cs:8-13`).
- Callback index: 0 = confirm. Usage in `ModManager.cs:317-336` shows 0 = OK, 2..n = other actions. **[V] Single callback slot:** showing a second dialog overwrites `m_ConfirmationDialogCallback`. **[I]** Queue dialogs yourself.
- Error dialogs (`ErrorDialogManager`) **pause the simulation locally** (`HandlePause`, `Game/Game.UI/ErrorDialogManager.cs:270-293, 605`). See section 9.

### 8.3 Chirper
[V] `Game/Game.UI.InGame/ChirperUISystem.cs:79-125`: chirps are ECS entities (`Game.Triggers.Chirp` + `RandomLocalizationIndex` + `PrefabRef`), exposed via `RawValueBinding("chirper","chirps")`, `RawEventBinding("chirper","chirpAdded")` and triggers `addLike/removeLike/selectLink`. Creation goes through `Game.Triggers.CreateChirpSystem` with chirp prefabs.
**[I]** Chirps are part of simulated, saved world state. Using them for chat would inject non-replicated entities and pollute saves. Use a custom HUD panel for chat instead.

### 8.4 Pause menu / game screens
[V] `Game/Game.UI.InGame/GameScreenUISystem.cs`: binding `game.activeScreen` (`GameScreen` enum `Main=0, FreeCamera=1, PauseMenu=10, SaveGame=11, NewGame=12, LoadGame=13, Options=14`), trigger `game.setActiveScreen`, `isMenuActive => activeScreen >= PauseMenu`, `canUseSaveSystem` (false while a save is in progress). `SetScreen(...)` also sets cursor lock/hide (98-105).
**[I]** Opening the pause menu does not itself write `selectedSpeed` in C#. Pausing on menu open is done from JS through the `time.simulationPausedBarrier` subscription (section 9.2). A mod can add a "Disconnect"/"Session" entry to the pause menu with `moduleRegistry.extend(<pause-menu module path>, ...)`. The module path must be discovered with `-uiDeveloperMode` (`moduleRegistry.find(...)`); it is not in the C# code.

Main menu bindings worth knowing (group `menu`) [V `MenuUISystem.cs:397-488`]: `continueGame`, `newGame(NewGameArgs, bool)`, `loadGame(LoadGameArgs, bool)`, `saveGame(string)`, `quicksave`, `quickload(bool)`, `exitToMainMenu`, `startEditor`, `activeScreen`.

---

## 9. Simulation speed and pause: detection and authority

### 9.1 `SimulationSystem`
[V] `Game/Game.Simulation/SimulationSystem.cs`:
- `uint frameIndex` (simulation tick, **serialized in the save**: `Serialize` writes `frameIndex`, 135-146), `float frameTime`, `float selectedSpeed` (setter **ignored while `m_IsLoading`**, 72-85), `float smoothSpeed`, `loadingProgress`, `frameDuration`, `PerformancePreference performancePreference` (Balanced/FrameRate/SimulationSpeed, from `SharedSettings.general`) (68-119).
- `OnGamePreload`: `selectedSpeed = 0`, loading mode on, `m_LoadingCount = 1024` for NewGame (155-162).
- After loading completes: `selectedSpeed = gameplay.pausedAfterLoading ? 0 : 1` (205-214). While `GameManager.isGameLoading`: forced 0 (216-219).
- Step count per rendered frame (220-268): accumulates `deltaTime * selectedSpeed`, at 60 ticks per sim-second. It is throttled by pending pathfinding (`PathfindResultSystem.pendingSimulationFrame`), by `performancePreference` relative to frame time, and capped at `max(1, min(8, round(selectedSpeed*throttle*2)))` (speed 1 = up to 2 ticks/frame, 2 = 4, 4 = 8). Each step does `frameIndex++` and runs `GameSimulation` with `(frameIndex, i)`.
- **[I] Consequence for co-op:** the number of ticks executed per wall-clock second is machine-dependent and non-deterministic (frame time, pathfinding backlog, performance preference). Two clients with the same `selectedSpeed` will **not** stay tick-aligned. Host-authoritative replication (host runs the sim, clients converge to host state / tick) is the only robust model. If clients run the sim at all, the host must pace them by `frameIndex` targets, not by speed.

### 9.2 Who writes `selectedSpeed` (all must be overridden for perfect sync)
[V] Writers found by grep:
| Writer | Location | Behaviour |
|---|---|---|
| Time controls (JS triggers `time.setSimulationPaused(bool)`, `time.setSimulationSpeed(int)`) | `Game/Game.UI.InGame/TimeUISystem.cs:94-95, 205-226` | index 0..2 maps to speed 1,2,4 (`IndexToSpeed`, 228-240) |
| Forced pause: app not focused (`AppState.Constrained`) **or** any JS observer on `time.simulationPausedBarrier` | `TimeUISystem.cs:93, 119-142` | sets 0, restores `m_SpeedBeforePause` when released |
| Error dialogs | `Game/Game.UI/ErrorDialogManager.cs:270-293, 605` | sets 0, restores cached speed when the queue empties |
| Lightning VFX with `pauseSimulationOnLightning` | `Game/Game.Rendering/ClimateRenderSystem.cs:150, 444-449` | sets 0 |
| Photo mode "Simulation Speed" property | `Game/Game.Rendering/PhotoModeRenderSystem.cs:765-780` | 0..8 |
| Loading logic | `SimulationSystem.cs:158, 213, 218` | see above |
| Debug / benchmark | `Game/Game.Debug/DebugSystem.cs:1267`, `Game/Game.UI.Menu/BenchmarkUISystem.cs:791` | |

- Read-side bindings: `time.simulationPaused` (bool), `time.simulationSpeed` (index), `time.ticks`, `time.day`, `time.timeSettings` (`TimeUISystem.cs:84-92`).
- `AppState.Constrained` comes from `PlatformManager.onAppStateChanged`, forwarded only from PSIs implementing `IAppStateSupport` (`Colossal.PSI.Common/Colossal.PSI.Common/PlatformManager.cs:347-353`). **[I]** No `IAppStateSupport` implementation exists in the decompiled Steam/PDX PSI assemblies (grep), so on PC alt-tab probably does **not** pause. Still enforce authority regardless.

### 9.3 Recommended authority mechanism [I, built on V facts]
- `TimeUISystem` runs in `UIUpdate` (MainLoop, during `Update`). `ErrorDialogManager` and other writers act at arbitrary times. `SimulationSystem` consumes `selectedSpeed` in `LateUpdate`.
- Register a `SpeedAuthoritySystem` with `updateSystem.UpdateBefore<SpeedAuthoritySystem, SimulationSystem>(SystemUpdatePhase.LateUpdate)` (or plain `UpdateBefore<SpeedAuthoritySystem>(SystemUpdatePhase.LateUpdate)`; vanilla has no `UpdateBefore` in LateUpdate, so it sorts first [V `SystemOrder.cs:70-77`, `UpdateSystem.cs:163-166`]). Each frame:
  1. Compare `selectedSpeed` with the last value it wrote. A difference means a local writer changed it. If the change came from the player's own time-control UI, send a "speed request" to the host. Otherwise (barrier, error, lightning) treat it as local noise.
  2. Write the host-authoritative speed back (it is ignored automatically while loading).
- Host side: the same system broadcasts host speed and `frameIndex`.
- Also neutralize the JS-side pause barrier for clients (e.g. a "paused by host / by player X" overlay in the HUD) so players understand state. **[I]** Consider a co-op rule: the pause menu does not pause the shared sim, or it sends a "pause request" that the host may honour.
- Force `GameplaySettings.pausedAfterLoading` semantics through the host (clients should start paused until the host says otherwise).

### 9.4 Autosave and save side effects
[V] `Game/Game/AutoSaveSystem.cs:68-160`: in `GameMode.Game`, when `SharedSettings.general.autoSave` is on, it periodically calls `GameManager.instance.Save(...)` into the autosave target and prunes old autosaves.
**[I]** Disable autosave behaviour on clients during a session (their state is host-derived). Prefer patching via the session rather than mutating the user's `GeneralSettings`; at minimum restore it afterwards. Avoid `GameManager.Save` side effects on `lastSaveGameMetadata` when storing received snapshots (section 4.1).

---

## 10. Input actions for mods (`ProxyAction`)

- [V] Mod actions are created from a `ModSetting`: public `ProxyBinding` properties with `[SettingsUIKeyboardBinding(BindingKeyboard.X, actionName, alt, ctrl, shift)]` / `[SettingsUIMouseBinding]` / `[SettingsUIGamepadBinding]` (`Game/Game.Settings/SettingsUIKeyboardBindingAttribute.cs:138-176`). Class-level `[SettingsUIKeyboardAction(name, ActionType, ...)]` defines rebind/modifier options, usages, interactions, processors (`SettingsUIKeyboardActionAttribute.cs:9-31`; consumed in `ModSetting.cs:149-197`). `[SettingsUIBindingMimic(map, action)]` copies the current binding of a built-in action and follows its rebinds (`ModSetting.cs:119-139, 219-228`).
- [V] `RegisterKeyBindings()` must be called (once) to create the actions, then `LoadSettings` restores the user's rebinds through `[AfterDecode] ApplyKeyBindings` (`ModSetting.cs:141-231, 269-277`). The action map name equals the `ModSetting.id`.
- [V] `ProxyAction` API (`Game/Game.Input/ProxyAction.cs`): `shouldBeEnabled` (setter; throws for built-in actions, 322-347), `enabled` (read), `WasPressedThisFrame()`, `WasReleasedThisFrame()`, `WasPerformedThisFrame()`, `IsPressed()`, `IsInProgress()`, `ReadValue<T>()`, `onInteraction` event (365), `CreateBarrier(...)` / `CreateActivator(...)` (661-669).
- [V] Built-in maps: `"Shortcuts"`, `"Tool"`, `"Camera"`, `"Menu"`, `"Navigation"`, `"Photo mode"`, `"Editor"`... (`InputManager.cs:196-216`). `InputManager.instance.FindAction(map, name)` / `TryFindAction` (570-580).
- [V] Third-party usage: `Settings.GetAction(name)` then `action.shouldBeEnabled = true` (`thirdparty/Anarchy/Anarchy/Systems/Common/AnarchyUISystem.cs:301-306`, `.../AnarchyComponentsToolSystem.cs:269-270`).
- **[I]** Skyline Together hotkeys: "toggle chat", "toggle player list", "push to talk" (if ever), "ping location". Enable them only while in a session/in game (set `shouldBeEnabled` from `OnGameLoadingComplete`).

---

## 11. Threading helpers

[V] `Colossal.Core/Colossal.Core/MainThreadDispatcher.cs`:
- `RunOnMainThread(Action)` executes inline on the main thread, otherwise it registers a one-shot updater (32-42).
- `RegisterUpdater(Func<bool>)` is a `ConcurrentDictionary<Guid, Func<bool>>` (**no ordering guarantee**), executed in `GameManager.Update` **after** the world and UI update (`GameManager.cs:723`).
- `WaitXFrames(n)` uses a non-thread-safe `List`, so call it from the main thread only (44-49).
- **[I]** For network messages, use a lock-free queue (`ConcurrentQueue`) filled by the socket thread and drained inside a dedicated ECS system at a well-defined phase (e.g. `UpdateBefore(MainLoop)`). Do not use `MainThreadDispatcher` updaters, whose order is undefined and which run after the world update.

---

## 12. Scheduling determinism caveat (important for "perfect sync")

[V] `UpdateSystem.Refresh()` (`Game/Game/UpdateSystem.cs:309-380`) assigns **automatic offsets** to systems with `GetUpdateInterval > 1` and `GetUpdateOffset == -1`. It walks all such systems of a phase sorted by `(interval, updateIndex)` and accumulates the running offset `num3` **across interval groups without resetting it** (lines 343-368).
**[I] Consequence:** adding any mod system with an interval > 1 and auto offset to `GameSimulation` can shift the auto-assigned offsets of *vanilla* systems with larger intervals, i.e. change on which `frameIndex` they run. Clients with a different mod set (or different registration order) therefore run the simulation on a different schedule even at identical `frameIndex`.
- Skyline Together itself should only register `GameSimulation` systems with interval 1 or an explicit `GetUpdateOffset`.
- The join handshake should require the same code-mod set (section 1.5) for any mode where clients simulate.
- Mod iteration order is the `Dictionary<Identifier, ModInfo>` insertion order from asset enumeration (`ModManager.cs:181, 400-432`). **[I]** It is not guaranteed identical across machines.

---

## 13. Concrete skeleton (design inference, matches verified APIs)

```csharp
public sealed class SkylineTogetherMod : IMod            // top-level, implements IMod directly
{
    // NO instance field initializers (GetUninitializedObject); use statics or assign in OnLoad.
    public static SkylineTogetherMod Instance;
    public static SkylineTogetherSettings Settings;      // unique class name (SaveSpecificSetting key)

    public void OnLoad(UpdateSystem us)
    {
        Instance = this;
        GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset); // asset.path -> mod folder
        Settings = new SkylineTogetherSettings(this);
        Settings.RegisterKeyBindings();
        Settings.RegisterInOptionsUI();
        GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
        AssetDatabase.global.LoadSettings(nameof(SkylineTogetherSettings), Settings, new SkylineTogetherSettings(this));

        us.UpdateBefore<NetPumpSystem>(SystemUpdatePhase.MainLoop);                 // drain inbound queue first
        us.UpdateBefore<SpeedAuthoritySystem, SimulationSystem>(SystemUpdatePhase.LateUpdate);
        us.UpdateAt<SessionUISystem>(SystemUpdatePhase.UIUpdate);                  // bindings "skylineTogether.*"
        // gameplay command application / replication systems: see other reports (Modification phases etc.)
    }
    public void OnDispose() { /* best-effort disconnect; only called on quit */ }
}
```
Lifecycle (in a `GameSystemBase`): `OnGameLoadingComplete(p, m)` with `m.IsGame()` = session start/resume. `OnGamePreload(Purpose.Cleanup, GameMode.MainMenu)` = session end. Client join = `TaskManager.instance.EnqueueTask("SaveLoadGame", () => GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, meta), 1)` with `CityConfigurationSystem.overrideLoadedOptions = false`.

---

## 14. Open questions / not verifiable from C#

1. Exact JS module paths of the pause menu and time controls, for `extend/override`. They live in the game's bundled JS (not in the allowed sources). They must be found at runtime with `-uiDeveloperMode` and `moduleRegistry.find(...)`.
2. Which vanilla JS components subscribe to `time.simulationPausedBarrier` (pause menu? options? tutorials?). This is inferred from `TimeUISystem` but the subscriber side is JS.
3. Whether any PC PSI fires `AppState.Constrained` (no implementer found in the decompiled PSI assemblies).
4. Whether loading a save directly from a memory stream is possible without reflection. The public `Load` requires an `IAssetData`. The descriptor-based overload is private (`GameManager.cs:1053`). A transient `ILocalAssetDatabase` (as `Save` uses `AssetDatabase.GetTransient(0)`) is the likely route; see the save/serialization report.
5. `UniversalModMenu` append target semantics (declared in third-party typings, behaviour defined in game JS).
