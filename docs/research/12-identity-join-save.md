# 12 – Entity identity, join bootstrap, session saves and versioning

> **Historical research report.** Written before and during the implementation; it describes the game, not the
> mod, and is not maintained. For how the mod works, see [docs/ARCHITECTURE.md](../ARCHITECTURE.md). Code is cited
> by file and member and described in prose; no game code is reproduced.

Research for **Skyline Together**, a clean-room, host-authoritative co-op mod for Cities: Skylines II.

All paths are relative to the root of the game's decompiled assemblies unless stated otherwise. `UE/` = `Unity.Entities/Unity.Entities/`, `CSE/` = `Colossal.Core/Colossal.Serialization.Entities/`, `CAD/` = `Colossal.IO.AssetDatabase/Colossal.IO.AssetDatabase/`.

Tags:
- **[V]** verified: read in the decompiled code at the cited lines during this study.
- **[I]** inference: reasoned from verified code; not executed.
- **[V→NN]** verified in the prior report NN at the cited lines and consistent with this report's reading; not re-read line-by-line here.

Line-number corrections against the task brief and prior reports [V]:
- The private `GameManager.Load(GameMode, Purpose, AsyncReadDescriptor, Hash128, Guid)` is at `Game/Game.SceneFlow/GameManager.cs:1053-1151` (not 1046). `Save` is at `:945-1015`. Public `Load(GameMode, Purpose, IAssetData)` is `:1162-1215`, `Load(..., Hash128 guid)` is `:1217-1225`.
- `EntitySerializer.Dispose` is `CSE/EntitySerializer.cs:373-408`; host `FillEntityTableJob` is `:237-281`; `FillArchetypeMapJob` is `:95-137`.

---

## 0. Decisions (TL;DR)

| Topic | Decision |
|---|---|
| **(a) Where the NetId lives** | **Runtime side table indexed by `Entity.Index` (+version), persisted by a mod system `NetIdSystem : GameSystemBase, IDefaultSerializable` that writes `(Entity, NetId)` pairs through the game's own save entity table.** No NetId component. Zero structural cost, survives every save (join snapshot, autosave, manual save), loads cleanly without the mod. |
| **(b) Join ID map** | Comes **for free** from (a): on the client, `NetIdSystem.Deserialize` receives the pairs with `Entity` values already remapped to the client's freshly created entities by the stock `BinaryReader`. No private-table reflection, no Harmony, no parallel list. |
| **(c) Client load path** | **Public** `GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, saveGameData)` where `saveGameData` is a `SaveGameData` asset in a **file-backed transient DB** (`AssetDatabase.GetTransient`). No reflection. Never touches `settings.userState.lastSaveGameMetadata`. `onGamePreload`, `OnGameLoaded` and `onGameLoadingComplete` all fire. |
| Host snapshot | `SaveGameSystem` driven directly (stream = temp file), queued on `TaskManager` `"SaveLoadGame"`. Not `GameManager.Save` (avoids `CleanupMemory`/GC, package write, and the "Continue" pointer). |
| Catch-up | `frameIndex` is restored by the snapshot. Host tags every replication message with an epoch; the snapshot is taken at an epoch boundary; host buffers epoch > E for the joiner; client applies after `OnGameLoadingComplete(LoadGame, Game)`. Force `pausedAfterLoading` on the client during the load. |
| Session saves | Host: autosave/manual save allowed, serialized on the `"SaveLoadGame"` queue, announced to clients (stall). Clients: block `GameManager.Save` and autosave; offer "download host save" which writes a host-authored package locally. |
| Handshake | Game `Version.current` (`m_Version` + `fullVersion`), `FormatTags` names, mod version + protocol, loaded code-mod/UI-module list, StableTypeHash set of all `ISerializable` types, content prerequisites (`ArePrerequisitesMet`), game-mode name, referenced PrefabIDs, render/climate prefab availability. |
| Host migration | **Not feasible live.** Offer "resume from last host session save" instead (any peer can host it). Rejoin = fresh snapshot. |

---

## 1. Question 1 – Settling (a): component vs side table

### 1.1 What the save pipeline actually does with a mod `NetId` component

- `ComponentSerializerLibrary.Initialize` walks **every** `TypeManager` type. Any type assignable to `ISerializable` (or `IEmptySerializable`) gets a serializer (`CSE/ComponentSerializerLibrary.cs:45-99`) [V]. Types from an assembly other than `SerializerSystem`'s assembly are additionally returned in `serializableComponents` (`:53-56`, `:76-79`) [V].
- `SerializerSystem.OnUpdate` re-initializes the library when dirty and rebuilds the query from `serializableComponents` (`Game/Game.Serialization/SerializerSystem.cs:80-84`); `CreateQuery` adds each mod type to the query's `Any` set (`:194-197`) [V]. `ModManager.AfterLoadAssembly` registers the mod's types and calls `SerializerSystem.SetDirty()` (`Game/Game.Modding/ModManager.cs:149-150`) [V].
- `EntitySerializer.FillArchetypeMapJob` strips an archetype only of types **missing from `m_SerializerMap`** (`CSE/EntitySerializer.cs:126-135`), and `CreateArchetypeMap` builds the stripped archetype from types that **do** have a serializer (`:482-498`) [V].

**Conclusion [V]:** a `NetId : IComponentData, ISerializable` **is kept** in saves (01 §0/§11.5 is right). 08 §10.3's "stripped from saves" is only true for a NetId that is *not* `ISerializable`. Both reports are consistent once the premise is stated; the real arguments against the component are cost and coverage, below.

### 1.2 Structural cost of adding a data component to every replicated entity

- `EntityComponentStore.AddComponent(ArchetypeChunk*, ...)` loops chunks and calls `Move(chunk, ref filter)` for each (`UE/EntityComponentStore.cs:2909-2928`); the query overload does the same over the matching chunk cache (`:2931+`) [V].
- `Move(ChunkIndex, ...)` re-files a chunk **in place** only if `ChunkDataUtility.AreLayoutCompatible` holds (`UE/EntityComponentStore.cs:3388-3394`); otherwise it moves every entity in the chunk into chunks of the destination archetype (`:3396-3427`) [V].
- `AreLayoutCompatible` requires equal chunk capacity **and** identical non-zero-sized type lists (`UE/ChunkDataUtility.cs:630-648`) [V]. A 4–8 byte `NetId` changes the non-zero-sized type list, so **every chunk of every replicated archetype is copied entity-by-entity** [V].
- Every structural `EntityManager` call is a sync point that completes all jobs [V→08 `UE/EntityDataAccess.cs:391-443`].

Quantification [I]: a large city has ~1–2 M instance entities in ~25–60 k chunks (16 KiB each, `UE/Chunk.cs:28-37` [V→08]). One full pass is therefore ~0.4–1 GB of row copies plus per-entity bookkeeping (~50–100 ns/entity per 08 §13), i.e. **~100–300 ms one-shot**, plus a full job sync. It also:
- doubles the archetype count (each archetype gets a `+NetId` twin) [I];
- gives every moved chunk a new `SequenceNumber` and stamps all change versions [V→08 §3.3], which forces the change detector of report 08 into a full rescan [I];
- lowers chunk capacity slightly (more bytes per row) [I].

For comparison, the game already pays a similar cost on every load: `RequiredComponentSystem` performs ~190 query-wide `EntityManager.AddComponent` calls (e.g. `LaneFlow`, `Efficiency`, `Policy`, `CityModifier`; `Game/Game.Serialization/RequiredComponentSystem.cs:757-777`, grep count 190) [V]. A NetId riding in the save would thus not add cost on the *client's* load, but it would on the host at session start and on every spawn (next section).

### 1.3 Creation paths that would produce entities without the component

These create entities from archetypes baked into prefab data or cached in systems, so a NetId component would be missing until a catch-up pass adds it (one more chunk move per new entity, and a window where an entity exists without identity) [V for the sites, I for the consequence]:

| Path | Example (file:line) |
|---|---|
| Prefab-baked object archetype | `SubObjectSystem` `m_CommandBuffer.CreateEntity(jobIndex, objectData.m_Archetype)` (`Game/Game.Objects/SubObjectSystem.cs:2827, 3083`); tool generation `GenerateObjectsSystem` (`Game/Game.Tools/GenerateObjectsSystem.cs:1224, 1228, 1241`) |
| Net edges/nodes | `GenerateEdgesSystem` `CreateEntity(jobIndex, netData.m_EdgeArchetype)` (`Game/Game.Tools/GenerateEdgesSystem.cs:697, 1161, 1735`) |
| Lanes | `LaneSystem` `CreateEntity(jobIndex, netLaneArchetypeData.m_LaneArchetype)` (`Game/Game.Net/LaneSystem.cs:4795`) |
| Zone blocks | `BlockSystem` `CreateEntity(jobIndex, zoneBlockData.m_Archetype)` (`Game/Game.Zones/BlockSystem.cs:507, 988`) |
| Compositions (excluded from saves anyway) | `CompositionSelectSystem` (`Game/Game.Net/CompositionSelectSystem.cs:1255`) |
| Citizens/households (simulation ECBs) | `BirthSystem` (`Game/Game.Simulation/BirthSystem.cs:79`), `HouseholdSpawnSystem` (`Game/Game.Simulation/HouseholdSpawnSystem.cs:136`) |
| Service/vehicle requests (cached system archetypes) | `TransportCarAISystem` (`Game/Game.Simulation/TransportCarAISystem.cs:1008-1133`) |
| Load | `EntityDeserializer.CreateEntities` creates entities from the archetypes in the stream (`CSE/EntityDeserializer.cs:521-522`) |

Scale [V]: grep `CreateEntity\([^)]*m_\w*Archetype` under `Game/` gives **331 sites in 158 files**; `EntityManager/ECB.Instantiate` is used in only 2 rendering files. `RequiredComponentSystem` creates no entities (0 `CreateEntity` hits) but re-adds non-serialized components after load (above).

### 1.4 Hybrid design (chosen)

**Runtime:** side table, exactly as 08 §10.3 Option B [I]:
- `NativeList<int> netIdByIndex`, `NativeList<int> versionByIndex` (dense, indexed by `Entity.Index`, grown on demand), `NativeParallelHashMap<int, Entity> entityByNetId`, `int nextNetId`. O(1), version-checked; no structural change ever.

**Persistence:** a mod system that the game's own serializer drives:
- `SystemSerializerLibrary.Initialize(World)` wraps **every** system in `world.Systems` that implements `IDefaultSerializable` (or `IJobSerializable`), regardless of assembly (`CSE/SystemSerializerLibrary.cs:32-48`) [V]. So a mod `NetIdSystem : GameSystemBase, IDefaultSerializable` is serialized into every save.
- System buffers are written with an `EntityWriterData` that carries the save's entity table (`CSE/EntitySerializer.cs:627-636`), **after** the table is complete (`m_EntityTableDeps.Complete()`, `:591`) [V]. `BinaryWriter.Write(Entity)` maps a live entity to its save index if the version matches, else writes `-1` (`CSE/BinaryWriter.cs:128-141`) [V].
- On load, system buffers are read with an `EntityReaderData` built on `m_EntityTable` (`CSE/EntityDeserializer.cs:584-585`), and `BinaryReader.Read(out Entity)` returns the newly created entity for that save index (`CSE/BinaryReader.cs:146-157`) [V].
- This is exactly the pattern the game uses for custom names: `NameSystem.Serialize` writes `(Entity, string)` pairs and `Deserialize` skips `Entity.Null` (`Game/Game.UI/NameSystem.cs:549-575`) [V].

Sketch [I]:
```csharp
[FormerlySerializedAs("SkylineTogether.Identity.NetIdSystem, SkylineTogether")] // version-tolerant alias, see 1.5
public sealed class NetIdSystem : GameSystemBase, IDefaultSerializable, IPostDeserialize
{
    public void Serialize<TWriter>(TWriter w) where TWriter : IWriter
    {
        w.Write(kFormatVersion);           // never empty: ComponentSystemSerializer throws on an empty block
        w.Write(m_NextNetId);
        w.Write(m_LiveEntities);           // NativeList<Entity>, each element remapped to a save index (-1 if not saved)
        w.Write(m_NetIdsSameOrder);        // NativeList<int>, length-prefixed
    }
    public void Deserialize<TReader>(TReader r) where TReader : IReader
    {   // runs inside an ExclusiveEntityTransaction: containers only, no EntityManager calls
        r.Read(out int fmt); r.Read(out m_NextNetId); r.Read(m_TmpEntities); r.Read(m_TmpIds);
        Clear(); for (i...) if (m_TmpEntities[i] != Entity.Null) Bind(m_TmpEntities[i], m_TmpIds[i]);
    }
    public void SetDefaults(Context c) { Clear(); m_NextNetId = 1; }   // save without our block, new game, main menu
    public void PostDeserialize(Context c) { /* drop entries whose entity !Exists; host: assign ids to unmapped entities */ }
}
// OnLoad: updateSystem.UpdateAt<NetIdSystem>(SystemUpdatePhase.MainLoop);
//         updateSystem.UpdateAfter<PostDeserialize<NetIdSystem>>(SystemUpdatePhase.Deserialize);
```
Supporting facts [V]:
- `ComponentSystemSerializer.SerializeSystem` throws "Nothing serialized" on an empty block (`CSE/ComponentSystemSerializer.cs:36-41`); exceptions are caught and logged per system (`CSE/EntitySerializer.cs:639-642`).
- `IWriter`/`IReader` provide length-prefixed `NativeList<int>` (`CSE/IReader.cs:37`) and `NativeList<Entity>` (`CSE/BinaryWriter.cs:59-66`, `CSE/BinaryReader.cs:123-131`).
- System deserialization runs inside `BeginExclusiveEntityTransaction` (`CSE/EntityDeserializer.cs:197-213`), so `Deserialize` must not use `EntityManager` [V for the transaction, I for the rule].
- Systems absent from the stream get `SetDefaults` (`CSE/EntityDeserializer.cs:434-442, 627-631`) [V].
- `PostDeserialize<T>` is a public generic system that calls `T.PostDeserialize(LoadGameSystem.context)` (`Game/Game.Serialization/PostDeserialize.cs:6-24`) [V]; vanilla registers its own instances with `UpdateAfter` in the Deserialize phase (`Game/Game.Common/SystemOrder.cs:861-898`) [V].

Why not IPreSerialize to temporarily add a component [I]: it would pay the §1.2 full-world copy on every save and again to remove it. The system-serializer route has no structural cost at all.

Size [I]: 1 M pairs = 4 B save index + 4 B id ≈ 8 MB raw per save, ZStd-compressed with the rest because system buffers use the same `m_BufferFormat` (`CSE/EntitySerializer.cs:630`, `SerializerSystem.cs:102` [V]). Writing 1 M `Write(Entity)` calls in Mono costs tens of ms [I]; an `IJobSerializable` variant can move this into a job if needed (`CSE/SystemSerializerLibrary.cs:35-40` [V]).

### 1.5 Identity-related gotchas found

- **Type identity is the assembly-qualified name.** System and component types are written as `AssemblyQualifiedName` (`CSE/SystemSerializer.cs:15-22`) and resolved with `Type.GetType`, falling back to the `[FormerlySerializedAs]` table by trimming the AQN at commas from the right (`CSE/SystemSerializer.cs:31-44`; table built in `CSE/SystemSerializerLibrary.cs:71-79`) [V]. Whether Mono's `Type.GetType` resolves an AQN whose `Version=` differs from the loaded mod assembly is not provable here [I]. **Mitigation [I]:** keep `AssemblyVersion` constant across releases *and* add a self-alias `[FormerlySerializedAs("Ns.NetIdSystem, SkylineTogether")]`; the trimming loop will hit it after dropping `Version/Culture/PublicKeyToken`.
- Rename safety: renaming the class or namespace later requires a `[FormerlySerializedAs]` with the old name, otherwise the map block becomes obsolete and is skipped (`CSE/EntityDeserializer.cs:422-426`) [V].
- Entities written as `-1` (not in the save query, e.g. `Temp`, `Deleted`, `NetCompositionData`, `EffectInstance`, `LivePath`; `SerializerSystem.cs:198-209`) come back as `Entity.Null` and are dropped [V]. Such entities are by definition derived/local and must not carry NetIds [I].

---

## 2. Question 2 – Settling (b): is save order deterministic, can the host capture its table?

### 2.1 Determinism of `saveIndex -> Entity` [V]

Host:
- Stripped archetypes are taken in `m_StrippedArchetypes` order (`CSE/EntitySerializer.cs:499`). `FillEntityCountJob` + `AccumulateEntityCountJob` give each archetype a start offset as a prefix sum in that order (`:178-220, 543-566`).
- `FillEntityTableJob` assigns `saveIndex = offset + k` in the iteration order of the archetype's values in `m_ChunkMap`, honoring each chunk's enabled mask (`:256-280`).
- `SerializeArchetypeJob` builds the per-archetype chunk list from the **same** multi-map iteration (`:302-310`), and all component serializers write values in that list order (`:612-620`). The archetype table is written in `m_StrippedArchetypes` order (`:595-611`).

Client:
- `CreateEntities` walks archetypes in stream order, calls `EntityManager.CreateEntity(archetype, count)` and `FillEntityTableJob` writes `m_EntityTable[offset + i] = m_Entities[i]` with the same prefix-sum offsets (`CSE/EntityDeserializer.cs:487-538`, job at `:92-112`). Component data is then deserialized into `m_Entities` in order (`:554-559`).

**Conclusion [V]:** the mapping is deterministic *by construction*: it has to be, or component values would land on the wrong entities. `saveIndex` k on the host's table is exactly `m_EntityTable[k]` on the client. Note that the host's ordering itself is not reproducible across runs (hash-set order of archetypes, parallel multi-map insertion; `:461-499, 516-530`) [V for the containers, I for the effect], so only the table produced by *that* serialization is meaningful.

### 2.2 Capturing the host's table [V/I]

- `m_EntityTable` is private and disposed in `EntitySerializer.Dispose` (`CSE/EntitySerializer.cs:361, 399-402`), which `SerializerSystem` calls in `finally` right after `Serialize` (`Game/Game.Serialization/SerializerSystem.cs:97-108`) [V].
- A custom `IWriter` is not injectable: `SerializerSystem` hard-codes `Serialize<BinaryWriter, FormatTags>` (`:102`) [V].
- A Harmony prefix on the closed `EntitySerializer<WriteBuffer>.Dispose` reading `m_EntityTable` via reflection would work [I], but it is unnecessary.

**Resolution:** neither 04 §8.1's parallel list nor 08 §10.5's reflection is needed. The `NetIdSystem` block (§1.4) is serialized *through* the table, so the host never needs to know save indices and the client never needs the deserializer's private table. 01 §11.5's objection (private, disposed) is correct but moot.

---

## 3. Question 3 – `ClearSystem` and the save query including mod types

Facts [V]:
- `ClearSystem` destroys `Any` of 20 vanilla types (`PrefabRef`, `LoadedIndex`, flow graph types, `ServiceRequest`, `City`, `TimeData`, `CreationDefinition`, …) excluding `NetCompositionData` and `PrefabData` (`Game/Game.Serialization/ClearSystem.cs:23-59`). No mod types.
- The save query's `Any` set includes every non-Game `ISerializable`/`IEmptySerializable` type (`SerializerSystem.cs:194-197`).
- `ClearSystem` runs in every Deserialize phase, including `MainMenu()` = `Load(MainMenu, Cleanup, null)` (`SystemOrder.cs:796`; `GameManager.cs:1105-1109`).

Implications [I]:
1. **Mod-only entities leak and duplicate.** An entity whose only "save-query" component is a mod type is saved but never cleared: each load (and each return to the main menu) keeps the old one and adds the saved copy.
2. **Loading without the mod leaves empty entities.** Missing types get `TypeIndex.Null` and are dropped from the archetype (`CSE/EntityDeserializer.cs:361-366, 501-516`) [V], so an archetype made only of mod types turns into component-less entities.
3. **Third-party mods' mod-only entities** (if any exist) from a client's previous city survive into the joined world; the snapshot then adds the host's. That breaks "identical for all players".

Rules for Skyline Together:
- **Never create entities whose only persistent components are ours.** Session data (player list, permissions, NetId map, next id) lives in the `NetIdSystem`/`SessionSystem` system blocks [I].
- **Client pre-load sweep [I]:** register a system with `UpdateBefore(SystemUpdatePhase.Deserialize)` (lands after vanilla `UpdateBefore` systems, i.e. after `ClearSystem`, before `SerializerSystem`; ordering rules in `Game/Game/UpdateSystem.cs:163-166` [V→05]) that destroys entities matching *non-Game serializable types* and *none* of `ClearSystem`'s types. Enable it only for session loads. Report what it destroyed.
- Post-load convergence check should include the set of mod component types present (from `SerializerSystem.componentLibrary`, `SerializerSystem.cs:41`) [I].

---

## 4. Question 4 – Settling (c): the most robust load recipe

### 4.1 Facts

- **Public `Load(GameMode, Purpose, IAssetData)` accepts a `SaveGameData`** and forwards `saveGameData.GetAsyncReadDescriptor()` to the private worker with a new session GUID (`GameManager.cs:1204-1208`) [V]. It only logs a warning about the session GUID.
- The private worker sets `m_State = Loading`, shows the loading screen, then (for any non-boot load) calls `LoadSimulationData`, which raises **`onGamePreload`** and runs `LoadGameSystem.RunOnce()` (`GameManager.cs:1061-1109, 1031-1038`) [V]. `LoadGameSystem.OnUpdate` runs the Deserialize phase, then raises `onOnSaveGameLoaded` → **`OnGameLoaded`** on every `GameSystemBase` (`Game/Game.Serialization/LoadGameSystem.cs:51-57`; `Game/Game/GameSystemBase.cs:21-26, 72-83`) [V]. Afterwards it raises **`onGameLoadingComplete`** and sets `WorldReady` (`GameManager.cs:1136-1137`) [V].
- `instigatorGuid` is only used by the editor (`Game/Game.UI.Editor/MapPanelSystem.cs:584`); game loads ignore it (grep) [V].
- `ReadSystem` reads through `StreamBinaryReader`, which uses `AsyncReadManager.Read(filePath, ...)` from `desc.offset`; `desc.size` is ignored (`Game/Game.Serialization/ReadSystem.cs:44-131`; `CAD/StreamBinaryReader.cs:17-48`) [V]. A real file is required.
- `AssetDatabase.GetTransient(maxChunkSize, rootPath)` creates `AssetDatabase<Transient>` backed by a `FileSystemDataSource` under `EnvPath.kTempDataPath/<guid>` or a given `rootPath` (`CAD/AssetDatabase.cs:301-304`; `CAD/Transient.cs:24-39`) [V]. `AssetData.GetWriteStream()` opens a file and registers the entry on close; `GetAsyncReadDescriptor()` returns `(fileName, path, offset, size)` if the file exists (`CAD/AssetData.cs:123-151`; `CAD/FileSystemDataSource.cs:761-770, 812-844`) [V]. `GameManager.Save` uses exactly this (`AddAsset<SaveGameData>` + `GetWriteStream`, `GameManager.cs:955-965`) [V].
- Only `GameManager.Save` writes `settings.userState.lastSaveGameMetadata` (`GameManager.cs:995-997`; grep finds no other writer except clearing in `SaveHelpers.cs:30-32` and `UserState.cs:55`) [V]. `Load` never does.

**Resolution:** 05 §4/§14.4 is right: use the **public** overload with a `SaveGameData` asset from a transient (file-backed) DB. 01 §10.3's reflection on the private overload is unnecessary; 01's observation that a file is needed remains true, and the transient DB *is* that file.

### 4.2 What the vanilla menu sets before `Load`, and what a co-op client must set

`MenuUISystem.LoadGame` (`Game/Game.UI.Menu/MenuUISystem.cs:789-819`) [V]: `MapMetadataSystem.mapName/prefabReferences`, `CityConfigurationSystem.overrideLoadedOptions = true`, `overrideThemeName = null`, `GameModeSystem.overrideMode = args.gameMode`, `ApplyOptions(...)` (which writes `SharedSettings.userState` **and saves it**, `:821-853`), `achievementsEnabled`, then `Load`.

| Field | Client value | Why |
|---|---|---|
| `CityConfigurationSystem.overrideLoadedOptions` | **`false`** | With `false`, city name, disasters, theme, LHT, unlimited money, unlock-all and map tiles all come from the save (`Game/Game.City/CityConfigurationSystem.cs:423-432`); flag self-resets (`:438`) [V]. |
| `CityConfigurationSystem.overrideThemeName` | `null` | A non-empty value overrides the saved theme in `PostDeserialize` (`:202-227`) [V]. |
| **`GameModeSystem.overrideMode`** | **host's `GameModeSystem.currentModeName`** | `GameModeSystem` runs `UpdateBefore` in Deserialize (`SystemOrder.cs:797`), i.e. **before** `SerializerSystem`; it applies `overrideMode` if set, else the `m_NextMode` left by the *previous* deserialize, else "NormalMode" (`Game/Game.Prefabs.Modes/GameModeSystem.cs:55-91`). The saved mode is only read into `m_NextMode` (`:124-135`) and applied on the *next* load [V]. `ModeSetting.ApplyMode` changes prefab data (`:88-89`) [V], so a wrong mode = divergent simulation parameters [I]. The vanilla flow always sets it from `SaveInfo.gameMode` (`MenuUISystem.cs:730, 768, 802`) [V]. |
| `MapMetadataSystem.mapName` | host's map name | Used for telemetry/UI and to name a legacy temp water-render prefab (`Game/Game.Rendering/WaterRenderSystem.cs:572`) [V]. |
| `MapMetadataSystem.prefabReferences` | `null` | Only feeds later `SavePrefabAssets` (`GameManager.cs:963`; `MenuUISystem.cs:879`) [V]; client saves are blocked (§6). |
| `ApplyOptions` | **do not call** | It persists the host's options into the client's `userState` (`MenuUISystem.cs:826-852`) [V]. |
| `PlatformManager.instance.achievementsEnabled` | `false` [I] | The city is not the client's. |
| `GameplaySettings.pausedAfterLoading` | `true` in memory during the load, restore afterwards (no `ApplyAndSave`) | See §5.3 [V/I]. |

`CityConfigurationSystem.PostDeserialize` also applies the **host's camera** (pivot/rotation/zoom serialized by the host, `CityConfigurationSystem.cs:289-307, 237-254`) [V]. Restore the client's camera or a spawn camera after load [I].

### 4.3 Recipe

**Host (snapshot)** [V for APIs, I for the composition]:
1. `TaskManager.instance.EnqueueTask("SaveLoadGame", SnapshotAsync, 1)`. The vanilla menu and autosave serialize every save/load through this queue (`MenuUISystem.cs:718, 752, 786, 888`; `Game/Game/AutoSaveSystem.cs:139`) [V], and `SaveGameSystem.context/stream` are single-slot (`Game/Game.Serialization/SaveGameSystem.cs:22-35`) [V].
2. Close replication epoch E (§5.2), then `save.stream = new FileStream(temp)`; `save.context = new Context(Purpose.SaveGame, Game.Version.current, Hash128.Empty)`; `await save.RunOnce()` (`SaveGameSystem.cs:59-84`) [V]. `WriteSystem` disposes the stream when done (01 §8.4) [V→01].
3. Read `save.referencedContent` (content prefab entities with enabled `PrefabData`, filled by `EndPrefabSerializationSystem`, `Game/Game.Serialization/EndPrefabSerializationSystem.cs:85-89`) → names via `PrefabSystem.GetPrefabName` (as `GameManager.cs:941`) [V] for the handshake's content check.
4. Stream the file (chunked, hashed; report 06 §5.4).

Not `GameManager.Save` [V]: it awaits a GPU frame (`:954`), calls `CleanupMemory()` (`Resources.UnloadUnusedAssets` + `GC.Collect`, `:925-933, 937`), writes a package and sets the "Continue" pointer (`:980-1002`).

**Client (load)**:
1. Receive into `var db = AssetDatabase.GetTransient(0, rootPath: <mod temp>/<sessionId>)`; `var data = db.AddAsset<SaveGameData>("SkylineTogetherJoin")`; copy bytes into `data.GetWriteStream()` and dispose it [V APIs; I composition].
2. Verify the blob hash; `data.GetAsyncReadDescriptor() != AsyncReadDescriptor.Invalid` (`CAD/FileSystemDataSource.cs:764-769`) [V].
3. Set the fields of §4.2.
4. `TaskManager.instance.EnqueueTask("SaveLoadGame", () => GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, data), 1)`. `Load` has no re-entrancy guard (`GameManager.cs:1053-1063`) [V].
5. After `onGameLoadingComplete(LoadGame, Game)`: validate (§5.4), dispose `db`, delete the folder if still present (`FileSystemDataSource.DeleteData`, `CAD/FileSystemDataSource.cs:489-500` [V]; whether `Dispose` deletes it is [I]).

**Bundled render/climate prefabs (gap) [V/I]:** `GameManager.Save` calls the internal `PrefabSystem.SavePrefabAssets`, which stores the current climate, water-render and terrain-render prefabs **only if they are not read-only/persistent assets or were previously saved into the package** (`Game/Game.Prefabs/PrefabSystem.cs:363-375, 378-463`) [V]. A bare `SaveGameSystem` snapshot writes the `PrefabID` of the host's live prefab (`PrefabSystem.cs:836-841`) [V]. If the host's city uses a climate/render prefab that came from a save package, the client cannot resolve it and gets a `[Missing]` placeholder (`PrefabSystem.cs:950-957`; `ResolvePrefabsSystem.cs:539-546`) [V]. Mitigation: send these three `PrefabID`s in the handshake and have the client `TryGetPrefab` them (`PrefabSystem.cs:939-948`); on failure fall back to a host-side package path (reflection on `SavePrefabAssets` into a transient DB and shipping those `PrefabAsset`s) or refuse with a clear message [I]. **Status (polish):** implemented as `Session/JoinAssetTransfer.cs`: the host calls `SavePrefabAssets` (probe P-JOINASSETS) with `MapMetadataSystem.prefabReferences` into a transient DB and the snapshot context, ships the assets in a `JoinAssetBundle` in front of the save data and lists the three ids in `RenderPrefabIds`; the joiner adds the assets with the same guids, registers the DB globally, adds the prefabs and refuses with `st.toast.join.renderPrefabs` when an id still does not resolve.

---

## 5. Question 5 – Catch-up after load

### 5.1 Clock [V]

`SimulationSystem.Serialize` writes `frameIndex`; `Deserialize` restores it and zeroes `frameTime`/`m_Timer` (`Game/Game.Simulation/SimulationSystem.cs:135-146`). So the client starts at exactly the host's snapshot frame F0. `frameIndex` has a private setter (`:68`); a client that does not simulate needs reflection to follow the host clock (report 03 territory) [V/I].

The snapshot is captured inside `SaveGameSystem.OnUpdate` in MainLoop (`SystemOrder.cs:69`, `SaveGameSystem.cs:79-83`), before `SimulationSystem` runs in LateUpdate (`SystemOrder.cs:74`) [V]. The Serialize phase completes synchronously within that update: tracked jobs are completed (`CSE/EntitySerializer.cs:445`) and all serialize jobs complete in `finally` (`:649-657`) [V]. Only compression/write continues in later frames (`SaveGameSystem.cs:69-77`) [V].

### 5.2 Logging and replaying what happens during the transfer [I]

- Every host replication message (spawn, despawn, structure, delta, global state, events) carries a monotonically increasing **epoch** = one per host sender flush.
- The host sender is registered `UpdateBefore<Sender, SaveGameSystem>(SystemUpdatePhase.MainLoop)`. On the snapshot frame it closes epoch E, then `SaveGameSystem` captures exactly state(E): nothing mutates the world between the two inside MainLoop except `TrimPathsSystem` in the Serialize phase itself (§6.1), whose effect is idempotent for the joiner.
- From E+1 the host appends every outgoing message to a per-joiner backlog (or keeps a shared ring buffer with a per-joiner read cursor). Commands from other clients need no special handling: the host applies them and they appear as deltas.
- Transfer order on the wire: manifest → blob chunks (bulk lane) → backlog (control lane), with backlog continuing live.
- Bound: if the backlog exceeds N MB or T seconds, drop it and restart with a fresh snapshot.
- Alternative (no log): keep a per-joiner shadow copy at E and send `diff(shadow_E, now)` after load. It costs memory equal to the replicated payload (30–100 MB per 08 §13) per joiner; prefer the log.
- The joining client's own commands are rejected until it is live.

### 5.3 First frame after load (pitfall) [V]

In `SimulationSystem.OnUpdate`, the first frame with `!isGameLoading` sets `m_IsLoading = false` and `selectedSpeed = pausedAfterLoading ? 0 : 1`, then **in the same call** computes and runs simulation steps (`SimulationSystem.cs:202-214, 220-288`). The `selectedSpeed` setter is ignored while `m_IsLoading` (`:72-85`). A "speed authority" system running before `SimulationSystem` cannot stop that first step. **Set `SharedSettings.instance.gameplay.pausedAfterLoading = true` in memory before `Load` and restore it after** (`Game/Game.Settings/GameplaySettings.cs:24`) [V for the field, I for the fix].

### 5.4 Safe point to start applying [V/I]

- The NetId map is already rebuilt when `SerializerSystem` finishes, and validated in `PostDeserialize<NetIdSystem>` (§1.4).
- `OnGameLoaded` triggers full derived-data rebuilds on the next system update, e.g. `GeometrySystem` processes all edges (`Game/Game.Net/GeometrySystem.cs:3385-3404` [V→01]). The world keeps updating during `State.Loading` [V→05 `GameManager.cs:296`]. So those rebuilds have run by `onGameLoadingComplete` [I].
- **Start applying in the first frame after `OnGameLoadingComplete(Purpose.LoadGame, GameMode.Game)`**, from the mod's apply system (one structural batch per frame), drain the backlog in bounded batches with a "catching up" overlay, and declare the client live when it is within a few epochs of the head. Exceptions in `OnGameLoadingComplete` are only logged, not fatal (`GameSystemBase.cs:60-70`) [V].
- Post-load validation [I]: count unresolved prefab placeholders (instances whose prefab has negative `PrefabData.m_Index`, set by `ResolvePrefabsSystem.cs:532-545` [V→04]); check that every NetId resolves to an existing entity; compare a host hash of the snapshot (entity counts per archetype `StableHash`, NetId set) with the client.
- Data-migration systems in the Deserialize phase are gated by format tags or version (e.g. `ShortLaneRemoveSystem.cs:33`, `CargoPortCleanupSystem.cs:113`, `HouseholdPetLimitSystem.cs:28`, `BicyclePathfindFixSystem.cs:284`, `PlaceholderCleanupSystem.cs:105`, all in `Game/Game.Serialization.DataMigration/`) [V]. A snapshot from the same build carries all tags (`CSE/EntitySerializer.cs:660-671`) [V], so these are no-ops and the load is an identity for authoritative state [I].

---

## 6. Question 6 – Session saves

### 6.1 Host autosave / manual save during a session

- `AutoSaveSystem` arms on `OnGameLoadingComplete` for `LoadGame`/`NewGame` and, when the interval elapses, enqueues `AutoSave` on `"SaveLoadGame"`, which calls `GameManager.Save` (`Game/Game/AutoSaveSystem.cs:75-82, 97-105, 137-158`) [V].
- **Stall [V]:** `GameManager.Save` → `WaitForGPUFrame` (`GameManager.cs:954`), `CleanupMemory` with `GC.Collect` (`:937, 925-933`), then the Serialize phase in one frame. That phase includes synchronous GPU readbacks, e.g. `WaterSystem.Serialize` calls `AsyncGPUReadback.RequestIntoNativeArray(...).WaitForCompletion()` twice (`Game/Game.Simulation/WaterSystem.cs:1042-1043, 1062-1063`). Packaging runs in `Task.Run` (`:982-1002`).
- **World mutation [V]:** `TrimPathsSystem` (first in the Serialize phase, `SystemOrder.cs:731`) removes consumed `PathElement`s and rewrites `PathOwner` with an RW job over all path owners (`Game/Game.Serialization/TrimPathsSystem.cs:24-37, 67-80`; `Game/Game.Pathfind/PathUtils.cs:113-125`). It is semantically neutral but stamps change versions on all those chunks [V→08 §3.1], so the next host delta scan sees a false-positive burst [I]. `BeginPrefabSerializationSystem` disables/re-enables `PrefabData` and renumbers `m_Index`; `EndPrefabSerializationSystem` restores `m_Index` from `LoadedIndex` and re-enables (`BeginPrefabSerializationSystem.cs:261-302`; `EndPrefabSerializationSystem.cs:23-41`) [V]: net neutral for prefabs.
- Side effect: `lastSaveGameMetadata` is updated (expected for the host) (`GameManager.cs:995`) [V].

Policy [I]: allow both, keep them on the `"SaveLoadGame"` queue (so they never collide with a join snapshot), broadcast "Host is saving" when a save starts (hook `GameManager.onGameSaveLoad`, `GameManager.cs:953, 1004` [V]) so clients show a banner instead of a "lag" indicator, and suppress the replication timeout during that window. Session saves contain the `NetIdSystem` block, so a resumed session keeps the same NetIds.

### 6.2 Clients

- Client autosave would arm on the session load (`AutoSaveSystem.cs:75-82`) [V]. Disable it while in a session: `World.GetExistingSystemManaged<AutoSaveSystem>().Enabled = false` (a disabled system does not update [V→05 `UE/SystemState.cs:420-445`]) [I].
- Manual save and quicksave go through `MenuUISystem.SaveGame/QuickSave` → `GameManager.Save` (`MenuUISystem.cs:886-979`) [V]. Block with a Harmony prefix on `GameManager.Save(string, SaveInfo, ILocalAssetDatabase, ScreenCaptureHelper.AsyncRequest)` on clients, and show a notification [I]. Quickload/Continue (`MenuUISystem.cs:981-1006`) must be blocked too (leaving the session) [I].
- Offer **"Save a copy of the host's city"** instead [I]: request a host snapshot plus host `SaveInfo` fields; build a package locally with public APIs, as `GameManager.Save` does (`AddAsset<SaveGameData>`, `AddAsset<SaveGameMetadata>`, `database.AddAsset(path, transientDb)`; `GameManager.cs:961-991`) [V for the APIs]. The copy is host-authored and bit-exact rather than a replica of possibly divergent client state.

### 6.3 What a mid-session save contains, and loading it without the mod

- Everything a normal save contains, plus the `NetIdSystem` (and optional `SessionSystem`) blocks. With the hybrid design, **no mod components on entities** [I].
- Without the mod: the system AQN does not resolve → `SystemSerializer.DeserializeType` returns false → `ObsoleteSystemSerializer` skips the block (`CSE/SystemSerializer.cs:31-50`; `CSE/EntityDeserializer.cs:403-426`; `CSE/ObsoleteSystemSerializer.cs:27-37`) [V]. `CityConfigurationSystem.usedMods` will list Skyline Together (`CityConfigurationSystem.cs:255-263, 326-336`) [V]: cosmetic.
- If mod components were present, they would be skipped via `ObsoleteComponentSerializer` and dropped from archetypes (`CSE/EntityDeserializer.cs:361-374, 501-516`) [V]. That is harmless except for mod-only archetypes (§3).

---

## 7. Question 7 – Versioning handshake

| Field | Source | Rule |
|---|---|---|
| Protocol version, mod assembly version, mod build hash | own constants; `ModManager.ModInfo.asset.version` [V→05 §1.5] | exact match |
| Game build | `Game.Version.current` (`Game/Game/Version.cs:824-825`) [V]; send `m_Version`/`m_VersionExtra` via `GetComponentNumber` or the `ISerializable` form, plus `fullVersion` (`Colossal.Core/Colossal/Version.cs:109`) [V] | `Version.Equals` compares only `m_Version` (`Version.cs:363-366`) [V]; require equality of both fields for safety [I] |
| Format tags | `Enum.GetNames(typeof(FormatTags))` (`Game/Game/FormatTags.cs:3-47`, 42 tags) [V] | same ordered list (implied by build, cheap to check) |
| Serializable type set | for each serializer in `SerializerSystem.componentLibrary` (`SerializerSystem.cs:41`) and each system in `systemLibrary`: AQN + `TypeManager.GetTypeInfo(t).StableTypeHash` | sets equal. `StableTypeHash` covers namespace, name, **assembly name (not version)**, `[TypeVersion]` and recursive field layout (`UE/TypeHash.cs:57-149`) [V], so a mod update that keeps layout keeps the hash; hence the explicit mod version row |
| Loaded code mods + UI modules | `ModManager.ListModsEnabled()` (`Game/Game.Modding/ModManager.cs:222-228`) [V] + versions/platformIDs [V→05] | equal sets. Mod set also changes system scheduling offsets [V→05 §12] |
| City mod history | `CityConfigurationSystem.usedMods` (`CityConfigurationSystem.cs:147, 412-422`) [V] | informational |
| Content / DLC | host: names from `SaveGameSystem.referencedContent` (§4.3); client: `GameManager.instance.ArePrerequisitesMet(names)` (`GameManager.cs:1847-1861`) [V]; informational `PlatformManager.instance.EnumerateDLCs()` / `IsDlcOwned` (`Colossal.PSI.Common/Colossal.PSI.Common/PlatformManager.cs:829-885`) [V] | must pass. Unowned DLC prefabs are not even registered: `PrefabSystem.IsAvailable` checks `ContentPrerequisite` and mod availability (`PrefabSystem.cs:484-520`, used by `AddPrefab` at `:114-128`) [V] |
| Referenced PrefabIDs | host: distinct prefab IDs referenced by replicated state (or the enabled set after the snapshot, as `PrefabSystem.Serialize` writes it, `PrefabSystem.cs:827-865`) [V]; client: `TryGetPrefab` each (`:939-948`) | zero unresolved, or user-confirmed. Unresolved IDs become `[Missing]` placeholders (`:950-957`; `ResolvePrefabsSystem.cs:539-546`) [V] |
| Prefab set fingerprint | hash of sorted `PrefabID`s of all registered prefabs [I] | warn on mismatch: **duplicate IDs only warn and the first registration wins** (`PrefabSystem.cs:151-159, 164-176`) [V], so the same PrefabID can mean different prefabs on two machines when mod load order differs [I]; code-created prefabs have hashless IDs [V→04 §1.3] |
| Game mode | `GameModeSystem.currentModeName` (`GameModeSystem.cs:28, 90`) [V] | sent and applied as `overrideMode` (§4.2) |
| Climate / water-render / terrain-render PrefabIDs | `ClimateSystem.currentClimate`, `WaterRenderSystem.currentWaterRenderSettings`, `TerrainMaterialSystem.currentTerrainRenderSettings` (`PrefabSystem.cs:428-455`) [V] | must resolve on the client (§4.3 gap) |
| Map name, city name | `MapMetadataSystem.mapName` (`Game/Game.UI/MapMetadataSystem.cs:197`), `CityConfigurationSystem.cityName` [V] | applied / displayed |
| Snapshot manifest | snapshot id, epoch E, `frameIndex` F0, size, XxHash128 | integrity |

---

## 8. Question 8 – Rejoin and host migration

### 8.1 Rejoin [I]
- Short transport drop (Steam session resumes, backlog still in the host's ring buffer): continue from the last acked epoch.
- Otherwise: a full re-join (new snapshot). Client state since the drop is unknown, and a replica-diff protocol would cost more than a fresh snapshot of 20–300 MB (06 §5.4).
- NetIds persist through host saves (§1.4), so a session resumed from a host save keeps identities; clients still rejoin by snapshot.

### 8.2 State that exists only on the host
| State | Evidence |
|---|---|
| GPU water/snow/terrain textures; only obtainable by blocking readback | `WaterSystem.cs:1042-1068` [V]; snow/terrain [V→04 §6, §9] |
| Pathfinding queues and in-flight requests; cleared on load | `PathfindQueueSystem : IPreDeserialize` clears its action lists (`Game/Game.Pathfind/PathfindQueueSystem.cs:23, 404-417`) [V] |
| System-private `NativeQueue`s (fee events, XP, statistics, lane object commands, …) | 77 `private NativeQueue<` fields in 58 `Game/` files (grep) [V]; e.g. `ServiceFeeSystem` pending fee events [V→04 §6] |
| Static RNG seeded from wall clock | `RandomSeed.m_Random = new Random((uint)DateTime.Now.Ticks)` (`Game/Game.Common/RandomSeed.cs:8-18`) [V] |
| Chunk/iteration order, `UpdateFrame` group sizes, reverse-buffer element order | 08 §3.3, 04 §5 caveat [V→08/04] |
| Host-only non-replicated system state (demand accumulators, trigger frames, cell maps) unless explicitly replicated | 04 §6 table [V→04] |

### 8.3 Verdict
**Live host migration is not feasible** [I]. Clients do not run the authoritative simulation, so they lack the state above, and even a full replica would diverge immediately after promotion (wall-clock RNG, in-flight pathfinding). **Feasible alternative ("resume point"):** the host periodically pushes its latest session save (§6.1, host-authored) to clients as a backup. If the host leaves, any client can start a new session from that save, and everyone else re-joins. The UI should say this explicitly ("Session ended; resume from 12:04 save?").

---

## 9. End-to-end join sequence

| # | Host | Client | APIs / evidence |
|---|---|---|---|
| 1 | accept connection, send `Hello` (§7 fields) | send its `Hello`; both compare | §7 |
| 2 | — | pre-check content and PrefabIDs; report missing | `ArePrerequisitesMet` (`GameManager.cs:1847`), `PrefabSystem.TryGetPrefab` (`PrefabSystem.cs:939`) |
| 3 | ensure every replicated entity has a NetId (side table) | — | §1.4 |
| 4 | enqueue snapshot on `"SaveLoadGame"`; close epoch E; `SaveGameSystem.RunOnce` into a temp file | show "Downloading city" | `SaveGameSystem.cs:59-84`; §5.2 |
| 5 | start the per-joiner backlog at E+1 | — | §5.2 |
| 6 | stream manifest + chunks (bulk lane), then the backlog (control lane) | write chunks into `SaveGameData` of a transient DB; verify hashes; buffer backlog | `CAD/AssetDatabase.cs:301`, `CAD/AssetData.cs:123-151` |
| 7 | — | if in a city or menu: set `overrideLoadedOptions=false`, `overrideThemeName=null`, `GameModeSystem.overrideMode=<host mode>`, `mapName`, `achievementsEnabled=false`, `pausedAfterLoading=true` (memory only), arm the pre-load sweep | §4.2, §3, §5.3 |
| 8 | — | enqueue `GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, saveGameData)` | `GameManager.cs:1204-1208` |
| 9 | — | `onGamePreload` → Deserialize phase (`ClearSystem`, sweep, `SerializerSystem` incl. `NetIdSystem.Deserialize`, resolvers) → `PostDeserialize<NetIdSystem>` → `OnGameLoaded` | `GameManager.cs:1031-1038`; `LoadGameSystem.cs:51-57`; `SystemOrder.cs:796-898` |
| 10 | — | `onGameLoadingComplete(LoadGame, Game)`: restore `pausedAfterLoading`, disable autosave, enable the save block, dispose the transient DB, validate (prefab placeholders, NetId set, snapshot hash) | §5.4, §6.2 |
| 11 | — | apply backlog in bounded batches (epoch order) | §5.2 |
| 12 | mark the client live when its ack ≥ head − k; start sending its view/cursor | send `Live`; enable the player's tools | — |

---

## 10. Risks

1. **AQN version drift** may make `NetIdSystem`'s block unreadable after a mod update (§1.5). Mitigate with a fixed `AssemblyVersion` plus a self-alias `[FormerlySerializedAs]`. **Test before release.**
2. **Wrong game mode on the client** if `GameModeSystem.overrideMode` is not set: stale mode from a previous load or "NormalMode" (§4.2) [V].
3. **First-frame local simulation** on the client unless `pausedAfterLoading` is forced (§5.3) [V].
4. **Package-bundled climate/render prefabs** missing on clients with a bare `SaveGameSystem` snapshot (§4.3) [V/I].
5. **Mod-only entities** (ours or third-party) leak across loads and into the joined world (§3) [V/I].
6. **Host save stall** (GPU readbacks, GC, single-frame Serialize phase) appears as a session hitch for everyone (§6.1) [V]. Announce it and suppress timeouts.
7. **Duplicate PrefabID shadowing / hashless IDs** can silently map one prefab ID to different prefabs on different machines (§7) [V/I]. Fingerprint the whole prefab set.
8. **`SaveGameSystem` single-slot state**: any save outside the `"SaveLoadGame"` queue (other mods) can collide with a join snapshot [V/I].
9. **Snapshot is not a fixed point for change detection**: `TrimPathsSystem` and the prefab pass stamp versions, causing a rescan burst after every host save/snapshot [V/I].
10. **Exclusive transaction during `NetIdSystem.Deserialize`**: any `EntityManager` use there corrupts silently (safety checks are stripped, 08 §1) [V/I].
11. **Camera jump**: host camera applied on the client at load (§4.2) [V]. Cosmetic.
12. **Backlog growth** on slow links: cap it and fall back to re-snapshot (§5.2) [I].

## 11. Open questions

- Does Mono's `Type.GetType(AQN)` resolve a system type when the AQN's `Version=` differs from the loaded mod assembly (loaded via `Assembly.Load(bytes)`)? This decides whether the self-alias in §1.5 is required or merely defensive.
- Does disposing a transient DB delete its folder? `FileSystemDataSource.DeleteData` exists (`CAD/FileSystemDataSource.cs:489-500`); the call site was not traced.
- Measure the Serialize-phase duration and blob size for large cities (decides whether the host should show a "preparing city" pause).
- Exact behaviour of in-flight pathfinding on the client after load (requests are cleared by `PathfindQueueSystem.PreDeserialize`). Irrelevant if clients never simulate, but relevant for the "resume point".
- Whether any widely used third-party mod creates mod-only persistent entities (decides how aggressive the §3 sweep must be).
