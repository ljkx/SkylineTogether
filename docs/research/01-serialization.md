# 01 – Save/Load serialization and runtime entity serialization (Cities: Skylines II)

Research for **Skyline Together**, a clean-room host-authoritative co-op mod.

All paths are relative to `<audit>\decomp` unless stated otherwise. `file:line` refers to the decompiled source.

Legend:
- **[V]** means verified: read directly in the decompiled code at the cited lines.
- **[I]** means inference: reasoned from verified code, but not executed or proven.
- **[?]** means open: could not be decided from the code alone.

---

## 0. TL;DR and answer to the critical question

**Can we serialize an arbitrary set of entities, or single components or buffers, to bytes at runtime with the game's own serializers? Can we then deserialize them into a running world on another machine, with entity references remapped through our own ID map?**

**Yes.** Everything needed is `public`. One small internal piece, enabling format tags on a `Context`, needs reflection or an `UnsafeUtility.As` trick. Section 11 covers it.

- **[V] Every component's wire format is `ISerializable`.** Each serializable component, buffer element or shared component implements `Colossal.Serialization.Entities.ISerializable`, which has `Serialize<TWriter>(TWriter)` and `Deserialize<TReader>(TReader)` (`Colossal.Core/Colossal.Serialization.Entities/ISerializable.cs:3-8`). These methods are public interface members. They can be called on any component value with **any** `struct` that implements `IWriter` or `IReader`.
- **[V] All entity remapping goes through two calls.** It happens only in `IWriter.Write(Entity)` / `Write(Entity, bool ignoreVersion)` and `IReader.Read(out Entity)` (`IWriter.cs:43-45`, `IReader.cs:43`). The stock `BinaryWriter` / `BinaryReader` remap through a plain `NativeArray<Entity>` "entity table" passed to `Initialize(...)` (`BinaryWriter.cs:20-25,128-156`, `BinaryReader.cs:22-28,146-157`). We can supply **our own table**, or **our own `IWriter`/`IReader` implementation** backed by our net-ID map. Either way we fully control remapping.
- **[V] Component data is not the hard part.** The hard parts are:
  1. The `Context` passed to readers must carry **format tags** and the right `Version`/`Purpose`. Many `Deserialize` methods call `reader.context.format.Has(FormatTags.X)`. On a `Context` built with the 3-argument constructor, that call dereferences a null pointer.
  2. **System state** is serialized separately from entities: 57+ `IDefaultSerializable` systems plus `IJobSerializable` systems. Those deserializers are written for load time and have heavy side effects (`TerrainSystem.Deserialize` rebuilds textures, for example).
  3. **Derived data** is not serialized. Some components are tag-only (`IEmptySerializable`), such as `EdgeGeometry` and `Composition`. Runtime-only components (`Updated`, `Created`, `Temp`) are not serialized at all. The game rebuilds all of it after load through `OnGameLoaded` and the `Deserialize` update phase.
  4. **Prefab references** (`PrefabRef.m_Prefab` and others) point at prefab entities whose `Entity` values differ per machine. They must be mapped through a prefab ID table built from `PrefabID`.
- **[V] The game's high-level pipeline is not usable for partial updates as-is.** `EntityDeserializer` (a) always **creates new entities** (`EntityDeserializer.cs:482-541`) and (b) calls `SetDefaults` on **every registered system that is missing from the stream** (`EntityDeserializer.cs:434-442, 627-631`). If it is used with the game's `SystemSerializerLibrary`, deserializing a "mini save" would therefore **reset most of the simulation's global state**. It is safe only with an empty `SystemSerializerLibrary` (section 11.3).
- **[V] A full save can be produced to memory and loaded remotely, with caveats.** `SaveGameSystem.stream`, `SaveGameSystem.context` and `SaveGameSystem.RunOnce()` are public (`Game.Serialization/SaveGameSystem.cs:22-35,59-64`). So the host can write the simulation data to a `MemoryStream`. Loading requires a **file on disk**: `ReadSystem` reads through `StreamBinaryReader`, which uses `AsyncReadManager.Read(filePath, ...)` (`Colossal.IO.AssetDatabase/Colossal.IO.AssetDatabase/StreamBinaryReader.cs:17-48`). The client must therefore write the bytes to a temp file and point `LoadGameSystem.dataDescriptor` at it with `new AsyncReadDescriptor(name, path)` (`AsyncReadDescriptor.cs:19-22`). The full-behaviour entry point `GameManager.Load(GameMode, Purpose, AsyncReadDescriptor, Hash128, Guid)` is **private** (`Game.SceneFlow/GameManager.cs:1046`) and needs reflection.
- **[V] Recommended join-time ID map: a mod `NetId` component.** Mod components that implement `ISerializable` are picked up automatically by the game's serializer. `ModManager.AfterLoadAssembly` calls `TypeManager.InitializeAdditionalTypes` and `SerializerSystem.SetDirty()` (`Game.Modding/ModManager.cs:146-150`). The library then includes all `ISerializable` types (`ComponentSerializerLibrary.cs:43-99`), and the save query includes non-Game serializable types (`SerializerSystem.cs:82-83,194-197`). A mod `NetId : IComponentData, ISerializable` therefore travels inside a normal save. After the client loads the snapshot, it can rebuild the net-ID-to-entity map by querying `NetId`.

---

## 1. Where the code lives

**[V]** The serialization framework is in **Colossal.Core**, namespace `Colossal.Serialization.Entities`. It is 47 files in `Colossal.Core/Colossal.Serialization.Entities/`:

| Group | Files |
|---|---|
| Contracts | `ISerializable`, `IEmptySerializable`, `IDefaultSerializable`, `IJobSerializable`, `IStrideSerializable`, `ISerializeAsEnabled`, `FormerlySerializedAsAttribute` |
| Streams | `IWriter`, `IReader`, `BinaryWriter`, `BinaryReader`, `WriterBlock`, `ReaderBlock`, `WriterData`, `ReaderData`, `EntityWriterData`, `EntityReaderData` |
| Buffers | `IWriteBuffer`, `IReadBuffer`, `IWriteBufferProvider<T>`, `IReadBufferProvider<T>`, `BufferFormat` |
| Context | `Context`, `ContextFormat`, `Purpose` |
| Component serializers | `ComponentSerializer`, `ComponentSerializerLibrary`, `ComponentSerializerChunk`, `ComponentSerializerType`, `ComponentDataSerializer<T>`, `EnableableComponentDataSerializer<T>`, `BufferElementDataSerializer<T>`, `EnableableBufferElementDataSerializer<T>`, `SharedComponentDataSerializer<T>`, `EmptyComponentSerializer`, `EnableableEmptyComponentSerializer<T>`, `EnableableEmptyBufferElementSerializer<T>`, `ObsoleteComponentSerializer`, `ComponentSerializerException` |
| System serializers | `SystemSerializer`, `SystemSerializerLibrary`, `ComponentSystemSerializer<T>`, `DefaultSystemSerializer<T>`, `JobSystemSerializer<T>`, `ObsoleteSystemSerializer` |
| Drivers | `EntitySerializer<TWriteBuffer>`, `EntityDeserializer<TReadBuffer>` |

**[V]** The game side is in `Game/Game.Serialization/`: `SerializerSystem`, `SaveGameSystem`, `LoadGameSystem`, `WriteSystem`, `ReadSystem`, `WriteBuffer`, `ReadBuffer`, the `IPreSerialize` / `IPreDeserialize` / `IPostDeserialize` hooks and their generic runner systems, prefab-reference systems, and many post-load fix-up systems. Two related files sit elsewhere: the format tag enum `Game/Game/FormatTags.cs` and the version constants `Game/Game/Version.cs`.

---

## 2. Contracts

### 2.1 Interfaces [V]

| Interface | Definition | Meaning |
|---|---|---|
| `ISerializable` | `ISerializable.cs:3-8` | `void Serialize<TWriter>(TWriter writer) where TWriter : IWriter; void Deserialize<TReader>(TReader reader) where TReader : IReader;` The writer and reader are passed **by value**. |
| `IEmptySerializable` | `IEmptySerializable.cs:3-5` | Marker. **Only the presence** of the component in the archetype is saved, never its data (`EmptyComponentSerializer.SerializeData` writes nothing, `EmptyComponentSerializer.cs:64-68`). Data-bearing components use this too, e.g. `Game.Net.Composition` (`Game.Net/Composition.cs:6`), `Game.Net.EdgeGeometry` (`Game.Net/EdgeGeometry.cs:7`) and `Game.Rendering.CullingInfo` (`Game.Rendering/CullingInfo.cs:8`). Their data is recomputed after load. |
| `IDefaultSerializable : ISerializable` | `IDefaultSerializable.cs:3-6` | Adds `void SetDefaults(Context)`. This is the expected interface for **systems** (`SystemSerializerLibrary.cs:42-48,55` logs an error for systems that use plain `ISerializable`). |
| `IJobSerializable` | `IJobSerializable.cs:5-12` | Job-based system serialization: `JobHandle Serialize<TWriter>(EntityWriterData, JobHandle)`, `Deserialize<TReader>(EntityReaderData, JobHandle)`, `SetDefaults(Context)`. Used by cell-map systems (`Game.Simulation/CellMapSystem.cs:107-150`) and others. |
| `IStrideSerializable : ISerializable` | `IStrideSerializable.cs:3-6` | `int GetStride(Context)`. When the stride is non-zero, the column is written through a byte-filter (`NativeCompression.FilterDataBeforeWrite`) for better compression (`BinaryWriter.cs:104-109`, `ComponentDataSerializer.cs:34-56`). Example: `PrefabRef.GetStride` returns 4 (`Game.Prefabs/PrefabRef.cs:55-58`). |
| `ISerializeAsEnabled` | `ISerializeAsEnabled.cs:3-5` | For an `IEnableableComponent`, serialize as a normal component and ignore the enabled bit (`ComponentSerializerLibrary.cs:57,82,86`). Example: `PrefabData` (`Game.Prefabs/PrefabData.cs:7`). |
| `[FormerlySerializedAs("old")]` | `FormerlySerializedAsAttribute.cs:5-13` | Type rename support. Old names go into a type table used when `Type.GetType(name)` fails (`ComponentSerializerLibrary.cs:113-121`, `ComponentSerializer.cs:51-65`). |

### 2.2 `IWriter` / `IReader` and the stock binary format [V]

`IWriter` (`IWriter.cs:9-104`) and `IReader` (`IReader.cs:9-106`) are public interfaces with:
- `Context context { get; }`
- `Initialize(Context, NativeList<byte> buffer, NativeArray<Entity> entityTable)` on the writer, and `Initialize(Context, NativeArray<byte> buffer, NativeReference<int> position, NativeArray<Entity> entityTable)` on the reader
- size-prefixed **blocks**: `Begin()` / `End(block)`
- primitives, Unity.Mathematics vectors, `Bezier4x3`, `string`, `Color`, `Color32`, `quaternion`, `Hash128`, `Entity`, native arrays and lists, and nested `ISerializable` values

`BinaryWriter` (`BinaryWriter.cs:12-370`) and `BinaryReader` (`BinaryReader.cs:12-409`) are **public structs**. Their wire format:
- **Integers:** little-endian, written byte by byte (`BinaryWriter.cs:320-326, 354-364`). `float` and `double` are written as their raw uint/ulong bits (`:305-313`).
- **`bool`:** 1 byte. `bool2`, `bool3` and `bool4` are packed into one byte of bits (`:243-260`).
- **`string`:** an `int` **character count**, then each `char` UTF-8 encoded on its own (`:171-178, 275-303`).
- **Blocks:** `Begin()` writes a 4-byte placeholder. `End()` patches in the payload size and returns `size != 0` (`:27-49`). The reader's `Begin(out size)` reads the size. Its `End(block)` **seeks to the block end** if the payload was not fully consumed and returns `false` (`BinaryReader.cs:36-50`). This is how unknown or obsolete data is skipped.
- **`Entity`:** a single `int` (section 3).
- **Native arrays:** `Write(NativeArray<int|int2|ushort|byte|float4|float2>)` is a raw memcpy without a length prefix (`:68-102`). `NativeList<int>` and `NativeList<Entity>` are length-prefixed (`:59-66, 111-118`).
- **`Write<TSerializable>(value)`** calls `value.Serialize(this)`, i.e. passes a copy of the writer (`:158-161`). **`Read<TSerializable>(out value)`** does `value = default; value.Deserialize(this)` (`BinaryReader.cs:159-163`).

> **Design note [V]: reader position is shared state.** The writer and reader are copied by value into every nested `Serialize`/`Deserialize` call. The buffer is a `NativeList` and the reader's position is a `NativeReference<int>` (`BinaryReader.cs:14-18`), so all copies share the same underlying state. **A custom `IReader` must keep its cursor in shared native storage, not in a plain `int` field.** Otherwise nested reads lose their position.

### 2.3 `Context`, `Purpose`, `ContextFormat`, `Version` [V]

`Context` (`Context.cs:7-66`) is a public struct with:
- `Version version { get; internal set; }`
- `Purpose purpose { get; private set; }`
- `Hash128 instigatorGuid { get; private set; }`
- `ContextFormat format => m_Format` (the field `m_Format` is `internal`)
- a private `UnsafeHashMap<Entity, Hash128>` with `RemapEntityGuid` / `TryGetRemapEntityGuid`. It is used by `PrefabSystem.Serialize` to write asset GUIDs for prefabs saved into the package (`Game.Prefabs/PrefabSystem.cs:838-839`, `:426-437`).

Constructors:
- `Context(Purpose, Version, Hash128)` leaves **`m_Format` = default**, i.e. no bit storage (`Context.cs:21-28`).
- `Context(Purpose, Version, Hash128, int formatTagCount, Allocator)` allocates bit storage with **all bits cleared** (`Context.cs:30-37`).

`ContextFormat` (`ContextFormat.cs:7-36`) holds a single `UnsafeList<uint> m_Bits`. Its constructor, `EnableTag(int)` and `Dispose()` are **internal**. `Has<TFormatTags>(tag)` is public and indexes `m_Bits[num >> 5]` with **no bounds or IsCreated check**. `UnsafeList<T>.this[int]` is a raw `Ptr[index]` access (`Unity.Collections/Unity.Collections.LowLevel.Unsafe/UnsafeList.cs:210-222`). **Calling `format.Has` on a `Context` made with the 3-argument constructor dereferences a null pointer.**

Format tags in use:
- **Readers rely on tags.** `reader.context.format.Has(...)` appears in **46** Game files, e.g. `Citizen.cs:174,179`, `Household.cs:60,69`, `PrefabID.cs:100`, `TerrainSystem.cs:3136`. `reader.context.version` comparisons appear in about **205** files.
- **Writers do not.** No writer checks `format` or `version`. Only `CityConfigurationSystem` branches on `writer.context.purpose` (`Game.City/CityConfigurationSystem.cs:278,326`). A `Context` from the 3-argument constructor is therefore safe for **writing**, but not for reading.

`Purpose` (`Purpose.cs:3-12`) has the values `SaveGame, NewGame, LoadGame, SaveMap, NewMap, LoadMap, Cleanup`. Readers branch on it. For example, `ServiceFee.Deserialize` discards saved fees and uses defaults when `purpose == NewGame` (`Game.City/ServiceFee.cs:20-29`). **Use `Purpose.LoadGame` for runtime reads.**

The tag enum is `Game.FormatTags` (`Game/Game/FormatTags.cs`, about 44 entries such as `ShortLaneOptimization`, `HomelessAndWorkerFix` and `PrefabIDHash`). `Version` is `Colossal.Version` (`Colossal.Core/Colossal/Version.cs:11`), a packed 64-bit build stamp that is itself `ISerializable`. The current game version is `Game.Version.current` (`Game/Game/Version.cs:825`).

How tags get into a save:
- **Write side.** The writer stores **all** tag names of `TFormatTags`, i.e. every tag the build knows (`EntitySerializer.SerializeMetadata`, `EntitySerializer.cs:660-673`).
- **Read side.** The reader re-creates the `Context` with `formatTagCount` and enables each tag found by name. An unknown tag sets `m_UnsupportedFormat` (`EntityDeserializer.cs:256-318`).
- **Consequence [I].** A runtime stream written by the same build must be read with **all tags enabled** and `version = Game.Version.current`.

### 2.4 Data carriers [V]

`WriterData` (`WriterData.cs`), `ReaderData` (`ReaderData.cs`), `EntityWriterData` (`EntityWriterData.cs`) and `EntityReaderData` (`EntityReaderData.cs`) are public structs with **public constructors**. They hand out initialized `TWriter`/`TReader` instances through `GetWriter<TWriter>()` / `GetReader<TReader>()`.
- The plain variants pass `default(NativeArray<Entity>)` as the entity table, so entity refs come out as `-1` / `Entity.Null`.
- The `Entity*` variants pass a real table (`EntityWriterData.cs:15-34`, `EntityReaderData.cs:18-38`).

### 2.5 Buffer providers [V]

- `IWriteBufferProvider<T>.AddBuffer(BufferFormat)` and `IReadBufferProvider<T>.GetBuffer(BufferFormat[, out JobHandle])` are public interfaces (`IWriteBufferProvider.cs`, `IReadBufferProvider.cs`).
- `BufferFormat` is `Raw`, `CompressedLZ4` or `CompressedZStd` (`BufferFormat.cs`).
- The game implements the providers in `WriteSystem` and `ReadSystem` (section 8.4). **Custom in-memory providers are possible**, because the interfaces and the driver classes are public.

---

## 3. Entity reference remapping [V]

**Writer side.** The serializer builds `m_EntityTable` with length `EntityManager.HighestEntityIndex() + 1` (`EntitySerializer.cs:539-540`).
- It is first filled with `{-1,-1}` (`ClearEntityTableJob`, `:222-235`).
- Then, for every serialized entity, `table[liveEntity.Index] = new Entity { Index = saveIndex, Version = liveEntity.Version }` (`FillEntityTableJob`, `:237-281`).
- Save indices are dense and assigned per stripped archetype in chunk order (prefix sums, `FillEntityCountJob` / `AccumulateEntityCountJob`, `:178-220`).

`BinaryWriter.Write(Entity v)` (`BinaryWriter.cs:128-141`) works like this:
- It looks up `table[v.Index]`.
- If the `Version` matches, it writes the save index. Otherwise it writes `-1`, which covers stale references and references to entities outside the query.
- `Write(Entity, ignoreVersion: true)` skips the version check (`:143-156`). Only `Game.Pathfind.PathNode` uses it; it packs an index-only `Entity` (`Game.Pathfind/PathNode.cs:130-139`).

**Reader side.**
- `EntityDeserializer.CreateEntities` creates all entities, one batch per archetype, and fills `m_EntityTable[saveIndex] = newEntity` (`EntityDeserializer.cs:482-538`, `FillEntityTableJob :92-112`).
- `BinaryReader.Read(out Entity)` returns `table[i]` when `0 <= i < length`, else `Entity.Null` (`BinaryReader.cs:146-157`).

**Implications.**
- **[V]** Remapping is purely table-driven and handled by the writer/reader. **No component code knows about save indices.** Components simply call `writer.Write(m_SomeEntity)`, e.g. `Building.Serialize` (`Game.Buildings/Building.cs:16-26`) and `PrefabRef.Serialize` (`Game.Prefabs/PrefabRef.cs:45-53`).
- **[I]** We can remap through our own ID space in either of two ways:
  - **(a) Stock `BinaryWriter`/`BinaryReader` with our own tables.** The writer table is indexed by live `Entity.Index` and holds `{Index = netId, Version = liveVersion}`. The reader table is indexed by `netId` and holds the live entity. This needs **dense** net IDs, and memory scales with `HighestEntityIndex` (8 bytes per slot).
  - **(b) A custom `struct NetWriter : IWriter` / `NetReader : IReader`** that consults a `NativeParallelHashMap`, ignores the `entityTable` parameter, and can record **unresolved** references for deferred patching. The stock reader silently turns unknown indices into `Entity.Null`.
- A custom writer must also support `Write(Entity, ignoreVersion)`, where only `Index` is meaningful (the `PathNode` case).

---

## 4. Component serializers [V]

### 4.1 How serializers are chosen: `ComponentSerializerLibrary.Initialize(SystemBase, out List<ComponentType>)`

`ComponentSerializerLibrary.cs:25-106` iterates **every type in `TypeManager`** and picks a serializer:

| Component kind | Not enableable, or `ISerializeAsEnabled` | Enableable |
|---|---|---|
| `IEmptySerializable` (any kind) | `EmptyComponentSerializer` | `EnableableEmptyComponentSerializer<T>` (component) / `EnableableEmptyBufferElementSerializer<T>` (buffer) |
| `ISerializable` + `IComponentData` | `ComponentDataSerializer<T>` | `EnableableComponentDataSerializer<T>` |
| `ISerializable` + `IBufferElementData` | `BufferElementDataSerializer<T>` | `EnableableBufferElementDataSerializer<T>` |
| `ISerializable` + `ISharedComponentData` | `SharedComponentDataSerializer<T>` | n/a |

- Types from assemblies other than the `system`'s assembly (i.e. **mods**) are also returned in `serializableComponents`, which widens the save query (`:53-56, 76-79`).
- Each serializer gets `Initialize(system, dataSizes, index)`. This is where it captures `ComponentTypeHandle`, `ComponentLookup`, `BufferLookup` etc. **from that system** (`ComponentDataSerializer.cs:205-210`).
- `Update(system, context)` refreshes the handles and computes the stride from `IStrideSerializable` (`ComponentDataSerializer.cs:212-220`).
- Public accessors: `GetSerializerMap()`, `TryGetSerializerIndex(ComponentType, out int)`, `GetSerializerCount()`, `GetSerializer(int)`, `GetTypeTable()`, `GetEntityTypeHandle()`, `Reset()`, `Update(Context)`, `SetDirty()` (`:108-217`).

Counts in Game.dll, by regex:
- About **623** `IComponentData` structs and **121** `IBufferElementData` structs implement `ISerializable` or `IEmptySerializable`.
- That is out of about **998** and **234** in total.
- Only two shared components are serializable: `Game.Simulation.UpdateFrame` (`Game.Simulation/UpdateFrame.cs:6`) and `Game.Net.CoverageServiceType` (`Game.Net/CoverageServiceType.cs:6`).

### 4.2 Per-type data layout

`ComponentSerializer` (`ComponentSerializer.cs:10-83`) is the public abstract base:
- `SerializeData<TWriter>(EntityWriterData, NativeList<ComponentSerializerChunk>, JobHandle)` / `DeserializeData<TReader>(EntityReaderData, NativeArray<Entity>, ComponentSerializerType, JobHandle)`
- `Apply(ExclusiveEntityTransaction, JobHandle)`, used by shared components
- `SerializeType` / `DeserializeType`: a block containing `(byte)ComponentSerializerType` and the `AssemblyQualifiedName`.

Per serializer:
- **`ComponentDataSerializer<T>`** (`ComponentDataSerializer.cs:11-265`) writes one block per (archetype, type). The block holds all selected entities' values in chunk order, either directly or through a stride-filtered temp buffer (`:30-80`). Deserialization writes each value into `m_ComponentData[entity]`, a `ComponentLookup<T>`. **The entity must already have the component** (`:103-194`). It also handles `Enableable` / `Filtered` / `Empty` mismatches between the saved and current serializer type.
- **`EnableableComponentDataSerializer<T>`** first writes **bit-packed enabled flags**, then the values (`EnableableComponentDataSerializer.cs:30-75`).
- **`BufferElementDataSerializer<T>`** writes per entity `int length` and then the elements. In the stride mode it writes the lengths column first, then the element bytes (`BufferElementDataSerializer.cs:31-105`).
- **`SharedComponentDataSerializer<T>`** writes run-length pairs of `(value, count)` per chunk run (`SharedComponentDataSerializer.cs:53-108`). It is applied later through `Apply(ExclusiveEntityTransaction)` (`:194-207`).
- **`ComponentSerializerChunk(ArchetypeChunk, v128 enabledMask)`** has a public constructor (`ComponentSerializerChunk.cs:13-17`). **The mask selects which entities of a chunk are serialized**, so a subset of a chunk is expressible with the game's own jobs.

### 4.3 Type resolution and tolerance on load

- `ComponentSerializer.DeserializeType` resolves names in this order: `Type.GetType(AQN)`, then the `[FormerlySerializedAs]` table with progressively shorter assembly-qualified prefixes. The type must implement `ISerializable` or `IEmptySerializable` (`ComponentSerializer.cs:43-74`).
- Missing types or mismatched serializer kinds fall back to `ObsoleteComponentSerializer`, which skips the block. Missing types get `TypeIndex.Null` and are **dropped from the archetype** (`EntityDeserializer.cs:337-374, 498-516`).
- **[V] Consequence:** a save that contains mod components still loads without the mod; the mod data is skipped with a warning.

---

## 5. System serializers [V]

`SystemSerializerLibrary.Initialize(World)` (`SystemSerializerLibrary.cs:21-64`) iterates `world.Systems` and wraps each serializable system:
- `IJobSerializable` → `JobSystemSerializer<T>`
- `IDefaultSerializable` → `DefaultSystemSerializer<T>`
- plain `ISerializable` → `ComponentSystemSerializer<T>`, with an error log

Wrapper behaviour:
- `ComponentSystemSerializer` writes one block with `writer.Write(m_System)` and reads it with `reader.Read(m_System)`, which is the class overload (`ComponentSystemSerializer.cs:30-71`).
- `DefaultSystemSerializer.SetDefaults` calls `m_System.SetDefaults(context)` (`DefaultSystemSerializer.cs:13-17`).
- `JobSystemSerializer` wraps the system's job-returning `Serialize` / `Deserialize` with begin and end block jobs (`JobSystemSerializer.cs:98-139`).

The system type is identified by `AssemblyQualifiedName` (`SystemSerializer.cs:15-51`).

**[V] Systems missing from the stream receive `SetDefaults`** (`EntityDeserializer.cs:434-442, 627-631`).

Scale:
- **57** files declare a class that implements `IDefaultSerializable` directly. Examples: `PrefabSystem`, `SimulationSystem`, `TerrainSystem`, `WaterSystem`, `CitySystem`, `ClimateSystem`, `TaxSystem`, `ServiceFeeSystem`, the demand systems, the statistics systems and `NameSystem`.
- **16** files implement `IJobSerializable`, e.g. `AirPollutionSystem`, `GroundWaterSystem`, `LandValueSystem`, `NaturalResourceSystem`, `TelecomCoverageSystem`, `WindSystem`. Many more inherit it from `CellMapSystem<T>` (`Game.Simulation/CellMapSystem.cs:11,107-150`).

Examples of state and side effects:
- `SimulationSystem` stores only `frameIndex` and resets its timers on read (`Game.Simulation/SimulationSystem.cs:135-153`).
- `TerrainSystem.Deserialize` sets `m_Loaded`, decodes heightmaps into textures and calls `InitializeTerrainData` (`Game.Simulation/TerrainSystem.cs:3133-3163`). That is a heavy, load-time-only side effect.
- `PrefabSystem.Serialize` writes the list of **enabled (= referenced) prefab IDs** plus obsolete IDs (`Game.Prefabs/PrefabSystem.cs:826-864`).
- Some global state lives on **singleton entities** instead of systems, e.g. `TimeData` and `Game.City.City`. These are part of the entity query (`SerializerSystem.cs:173-193`). `TimeSystem` is only `IPostDeserialize` (`Game.Simulation/TimeSystem.cs:14`).

---

## 6. Save stream layout produced by `EntitySerializer<TWriteBuffer>` [V]

`EntitySerializer.Serialize<TWriter, TFormatTags>(Context, EntityQuery, BufferFormat, params ComponentType[] requireEnabled)` (`EntitySerializer.cs:410-421`) does the following:
1. **Collects chunks** of the query and builds per-chunk enabled masks. Entities whose `requireEnabled` components are disabled are filtered out (`:423-452`). The game passes `PrefabData`, so only **enabled** prefab entities are saved (`SerializerSystem.cs:102`).
2. **Strips each archetype** to the serializable component types. It creates a stripped archetype with `EntityManager.CreateArchetype` when needed (`:454-512`).
3. **Builds the chunk map and the entity table** (section 3).
4. **Emits buffers in this order** (`:582-701`):
   1. A **metadata** buffer, always `Raw`: `Version`, `(byte)BufferFormat`, `ushort` tag count, tag names (`:660-673`).
   2. **Component type table**: count, then per serializer `{byte type, string AQN}` (`:675-687`).
   3. **System type table**: count, then per system `{string AQN}` (`:689-701`).
   4. **Archetype table**: count, then per stripped archetype a block `{int entityCount, int componentCount, int[] serializerIndex}` (`SerializeArchetypeJob`, `:283-333`).
   5. **One buffer per archetype**, containing a block per component type (`:596-625`).
   6. **One buffer per system serializer** (`:627-647`).

The game uses `BufferFormat.CompressedZStd` (`SerializerSystem.cs:102`). `WriteSystem` writes each buffer as `int size` (Raw) or `{int size, int compressedSize}` plus the compressed bytes (`Game.Serialization/WriteSystem.cs:144-184`).

---

## 7. `EntityDeserializer<TReadBuffer>.Deserialize<TReader, TFormatTags>(ref Context, params ComponentType[] extra)` [V]

`EntityDeserializer.cs:178-216` runs these steps:
1. **`DeserializeMetadata`** replaces the caller's `Context` with a new one: the version from the stream, tag bits enabled, and `Allocator.Persistent` (`:256-318`).
2. **Component types** (`:320-381`), then **system types** (`:383-443`). The system step computes `m_SystemDefaults`, i.e. the registered systems absent from the stream.
3. **Archetypes** (`:445-480`), then **`CreateEntities`**, which creates *new* entities and fills the entity table (`:482-541`).
4. **`DeserializeComponents`** for each archetype (`:543-567`).
5. Inside `BeginExclusiveEntityTransaction`: `Apply` (shared components), then `DeserializeSystems`, then `SetDefaults` for the missing systems (`:197-214, 569-632`).

It returns `false` on an unknown format tag. `SerializerSystem` then downgrades the purpose from `LoadGame` to `NewGame` (`SerializerSystem.cs:133-147`).

**[V] Not suitable for partial updates into a live world:**
- It always creates new entities.
- It begins an exclusive entity transaction, which blocks the main-thread `EntityManager` (`:197`).
- With the real `SystemSerializerLibrary` it resets every system that is missing from the stream.

---

## 8. Game-side systems that drive save and load

### 8.1 `SerializerSystem` (`Game.Serialization/SerializerSystem.cs`) [V]

- It lazily builds `ComponentSerializerLibrary` and `SystemSerializerLibrary` when they are dirty (`:73-92`).
- The libraries are exposed publicly as `componentLibrary` and `systemLibrary` (`:41-43`).
- In phase `Serialize` it runs `EntitySerializer<WriteBuffer>.Serialize<BinaryWriter, FormatTags>(SaveGameSystem.context, m_Query, CompressedZStd, PrefabData)` (`:95-109`).
- In phase `Deserialize` it runs `EntityDeserializer<ReadBuffer>.Deserialize<BinaryReader, FormatTags>` and stores the resulting `Context` back into `LoadGameSystem.context` (`:110-155`).
- `m_Query` is `Any` of a fixed list plus all mod serializable types. The fixed list includes `LoadedIndex`, `PrefabRef`, the electricity and water flow types, `ServiceRequest`, `City`, `TimeData` and others. The query excludes `NetCompositionData`, `EffectInstance`, `LivePath`, `Temp` and `Deleted` (`:171-210`).
- `SetDirty()` is public (`:159-169`). It is called by `ModManager.AfterLoadAssembly` (`Game.Modding/ModManager.cs:146-150`).

### 8.2 `SaveGameSystem` [V]

`Game.Serialization/SaveGameSystem.cs:10-90`:
- Public members: `Stream stream {get;set;}`, `Context context {get;set;}` (the setter disposes the old context), `NativeArray<Entity> referencedContent`, `Task RunOnce()`.
- `OnUpdate` first triggers `UpdateSystem.Update(SystemUpdatePhase.Serialize)`. On the following frames it waits for `WriteSystem.writeDependency`, then completes the task (`:66-84`).
- It is updated in `MainLoop` (`Game.Common/SystemOrder.cs:69`).

### 8.3 `LoadGameSystem` [V]

`Game.Serialization/LoadGameSystem.cs:8-71`:
- Public members: `AsyncReadDescriptor dataDescriptor {get;set;}`, `Context context {get;set;}`, `Task RunOnce()`, `EventGameLoaded onOnSaveGameLoaded`.
- `OnUpdate` runs `UpdateSystem.Update(SystemUpdatePhase.Deserialize)`, then fires `onOnSaveGameLoaded` (`:50-57`).
- `GameSystemBase` subscribes every system to that event and calls its virtual `OnGameLoaded(Context)` (`Game/GameSystemBase.cs:25,76,119`). Systems use the hook to rebuild **all** derived data. For example, `Game.Net.GeometrySystem.OnGameLoaded` sets `m_Loaded`, and the next update processes **all** edges and nodes instead of only `Updated` ones (`Game.Net/GeometrySystem.cs:3385-3404`).

### 8.4 `WriteSystem` / `WriteBuffer` / `ReadSystem` / `ReadBuffer` [V]

- `WriteSystem : IWriteBufferProvider<WriteBuffer>` streams finished buffers to `SaveGameSystem.stream` through a `StreamBinaryWriter`, compressing them in jobs (`Game.Serialization/WriteSystem.cs:102-184`).
- **`DisposeWriterJob` disposes the `StreamBinaryWriter`, which disposes the underlying `Stream`** (`WriteSystem.cs:52-61,133-141`; `Colossal.IO.AssetDatabase/.../StreamBinaryWriter.cs:40-43`). [I] With a `MemoryStream`, read the bytes with `ToArray()`, which .NET allows on a closed `MemoryStream`.
- `ReadSystem : IReadBufferProvider<ReadBuffer>` reads from `LoadGameSystem.dataDescriptor` through `StreamBinaryReader`, which works on **file paths only** (`Game.Serialization/ReadSystem.cs:44-131`; `StreamBinaryReader.cs:17-48`).
- `ReadBuffer` allocates `TempJob` buffers (`ReadBuffer.cs`). `WriteBuffer` uses `Persistent` (`WriteBuffer.cs`).

### 8.5 Update phases [V]

From `Game.Common/SystemOrder.cs`.

**`Serialize` phase** (`:731-737`), in order:
1. `TrimPathsSystem` (before). It **mutates** `PathElement` buffers (`Game.Serialization/TrimPathsSystem.cs:72-80`).
2. `PreSerialize<ClimateSystem>`, `PreSerialize<AudioManager>`.
3. `BeginPrefabSerializationSystem`.
4. `SerializerSystem`.
5. `EndPrefabSerializationSystem`.
6. `WriteSystem`.

**`Deserialize` phase** (`:738-898`):
1. Before: `AllowBarrier<DeserializationBarrier>`, about 57 `PreDeserialize<T>` (search trees, batch managers, UI systems, ...), `ClearSystem`, `GameModeSystem`.
2. At:
   1. `SerializerSystem`, then `ReadSystem`, then `FilterLoadedSystem`, then `ResolvePrefabsSystem`, then `RequiredComponentSystem`.
   2. About 45 relation rebuilders and fixers: `ConnectedEdgeSystem`, `SubLaneSystem`, `Sub*System`, `OwnedVehicleSystem`, `HouseholdCitizenSystem`, `RouteWaypointSystem`, `ElectricityGraphSystem`, the `*FixSystem`s, ...
3. After: `CitizenSystem`, `ResetUpdateGroupSizesSystem`, `InitializeObsoleteSystem`, and about 30 `PostDeserialize<T>` (`TimeSystem`, `ClimateSystem`, `CitySystem`, `WaterSystem`, `TaxSystem`, ...).

Supporting details:
- `ClearSystem` destroys every entity in its query before reading (`Game.Serialization/ClearSystem.cs:23-60`). The query is `Any` of `PrefabRef`, `LoadedIndex`, flow nodes and edges, `TimeData`, `CreationDefinition` and others, and excludes `PrefabData` and `NetCompositionData`. **It does not include mod-only component types.**
- `RequiredComponentSystem` adds runtime components that are not serialized, such as `LaneFlow`, `Efficiency`, `MeshBatch` and `NodeGeometry`, and tags some entities `Updated` (`Game.Serialization/RequiredComponentSystem.cs:753-793, 1203, 1391, 1517`).
- `IPostDeserialize.PostDeserialize(Context)`, `IPreDeserialize.PreDeserialize(Context)` and `IPreSerialize.PreSerialize(Context)` (`Game.Serialization/I*.cs`) are run by the generic systems `PostDeserialize<T>`, `PreDeserialize<T>` and `PreSerialize<T>`. These read `LoadGameSystem.context` / `SaveGameSystem.context` (`PostDeserialize.cs:6-24`, `PreDeserialize.cs:6-24`, `PreSerialize.cs:6-24`).

---

## 9. Prefab references [V]

**What gets saved.** Prefab entities are saved as entities: they carry `LoadedIndex`, and the save requires `PrefabData` to be enabled.
- `PrefabData` is enableable, `ISerializeAsEnabled`, and holds `int m_Index` (`Game.Prefabs/PrefabData.cs:7-19`).
- `PrefabRef` is `{Entity m_Prefab}`, serialized as a plain entity reference with stride 4 (`Game.Prefabs/PrefabRef.cs:36-58`).

**Save.** `BeginPrefabSerializationSystem` (`Game.Serialization/BeginPrefabSerializationSystem.cs:260-303`):
- It disables all `PrefabData`, then runs the `PrefabReferences` phase. That phase walks every prefab reference and re-enables the referenced prefabs (`CheckPrefabReferencesSystem.cs:26-33`).
- It then renumbers `PrefabData.m_Index` densely and stores the original index in `LoadedIndex` (`SetPrefabDataIndexJob`, `:109-193`).
- `PrefabSystem.Serialize` writes the `PrefabID`s of the enabled prefabs in `m_Prefabs` order (`PrefabSystem.cs:826-864`).
- `PrefabID` = `{string type, string name, Hash128}`. The hash comes from the asset platform ID or the asset GUID, or is overridden for prefabs saved into the package (`Game.Prefabs/PrefabID.cs:15-36, 84-92`).

**Load.**
- `PrefabSystem.Deserialize` maps each saved index to a live prefab entity by `PrefabID` (`TryGetPrefab(PrefabID)`, `:934-942`). Unknown IDs become obsolete placeholders (`:865-908, 944-959`).
- `ResolvePrefabsSystem.OnUpdate` (`Game.Serialization/ResolvePrefabsSystem.cs:445-540`) builds `prefabArray[loadedIndex] -> loaded stub entity` and calls `PrefabSystem.UpdateLoadedIndices()`. It then runs the `PrefabReferences` phase with `isLoading: true`. There, `PrefabReferences.Check(ref Entity)` rewrites each reference from the loaded stub entity to the actual prefab entity (`Game.Serialization/PrefabReferences.cs:61-123`). Finally the stub prefab entities are destroyed (`:534`).

**Package extras.** `GameManager.Save` also calls `PrefabSystem.SavePrefabAssets`, which writes the climate, water-render and terrain-render prefabs into the **save package** and registers their GUIDs in the `Context` (`Game.Prefabs/PrefabSystem.cs:378-437`; `GameManager.cs:959-960`).

**[I] Implication for runtime sync.** Prefab entities must be mapped through a **prefab table keyed by `PrefabID`**, exchanged at session start (both peers resolve with `PrefabSystem.TryGetPrefab(PrefabID, out PrefabBase)` and `GetEntity(PrefabBase)`, `PrefabSystem.cs:705-714, 934-942`). Raw prefab `Entity` values are not comparable across machines.

---

## 10. `GameManager` save/load APIs and a save produced in memory

### 10.1 Public `GameManager` API [V]

From `Game.SceneFlow/GameManager.cs`:
- `Task<bool> Save(string saveName, SaveInfo meta, ILocalAssetDatabase database, Texture|AsyncRequest preview)` (`:943-1012`). It creates a transient asset database (`AssetDatabase.GetTransient(0)`), adds a `SaveGameData` asset and builds `Context(Purpose.SaveGame, Version.current, saveGameData.id.guid)`. It saves prefab assets, calls the **private** `SaveSimulationData(context, saveGameData.GetWriteStream())` (`:934-941`), adds the preview and `SaveGameMetadata`, and finally packages everything into a `PackageAsset` in the target database.
- `Task<bool> Load(GameMode, Purpose, IAssetData asset = null)` (`:1153-1206`) accepts `MapMetadata`, `SaveGameMetadata`, `MapData` or `SaveGameData`. `Load(GameMode, Purpose, Hash128 guid)` (`:1207-1215`) resolves through `AssetDatabase.global`.
- **Private** `Load(GameMode, Purpose, AsyncReadDescriptor, Hash128 instigatorGuid, Guid sessionGuid)` (`:1046-1144`) does the real work: loading screen, locale caching, `LoadSimulationData`, which fires `onGamePreload` and then sets `LoadGameSystem.dataDescriptor`, `.context` and runs `RunOnce` (`:1026-1033`), plus mode switching, `onGameLoadingComplete` and `m_State = WorldReady`.
- Events: `onGameSaveLoad`, `onGameLoadingComplete`, `onGamePreload` (`:138-140, 306-310`).

### 10.2 Host side: producing the simulation bytes in memory

**[V for the API, I for the exact recipe]**
```csharp
var save = world.GetOrCreateSystemManaged<SaveGameSystem>();
var ms = new MemoryStream();
save.stream  = ms;
save.context = new Context(Purpose.SaveGame, Game.Version.current, Hash128.Empty /* or a guid */);
await save.RunOnce();            // one Serialize phase + write completion
byte[] simData = ms.ToArray();   // stream was disposed by WriteSystem; ToArray still works
```

Caveats:
- This skips `PrefabSystem.SavePrefabAssets`, which is **internal** (`PrefabSystem.cs:378`). Custom climate, water-render and terrain-render prefabs coming from a map package are therefore not bundled, and their `PrefabID` is written without the package GUID override (`PrefabSystem.cs:838-839`).
- **[I]** That is fine when both peers have the same content and the map uses built-in or installed prefabs. To be safe, the host can instead call the public `GameManager.Save(...)` into a transient or temp `ILocalAssetDatabase` and ship the whole package.
- The serialize phase has small side effects on the host: `TrimPathsSystem` and the prefab-enable pass. It also takes noticeable frame time for big cities. **[?]** Measure.

### 10.3 Client side: loading those bytes

**[V for the API, I for the recipe]**
1. Write `simData` to a temp file.
2. Create `var desc = new AsyncReadDescriptor("SkylineTogetherSnapshot", tempPath);` (`AsyncReadDescriptor.cs:19-22`).
3. Call the private `GameManager.Load(GameMode.Game, Purpose.LoadGame, desc, Hash128.Empty, sessionGuid)` through reflection. That gives the same loading screen, events and state machine as a normal load.
   - The lighter alternative is to set `LoadGameSystem.dataDescriptor` and `LoadGameSystem.context` and call `RunOnce()`. **[I]** It skips `onGamePreload`. Systems depend on that hook; for example `SimulationSystem.OnGamePreload` resets the speed and loading counters (`SimulationSystem.cs:155-160`). It also skips the loading screen and `GameManager` state.
4. After `onGameLoadingComplete`, rebuild the net-ID map from the `NetId` component (section 11.5).

---

## 11. Critical question in detail: runtime partial serialization

### 11.1 What is public and what is not

| Item | Access | Notes |
|---|---|---|
| `ISerializable.Serialize/Deserialize` on every component | public | Generic methods. Call them through per-type generic helpers or reflection-built delegates. |
| `IWriter`, `IReader` | public interfaces | Custom implementations are allowed. |
| `BinaryWriter`, `BinaryReader` | public structs | `Initialize(...)` is public. |
| `WriterData`, `ReaderData`, `EntityWriterData`, `EntityReaderData` | public constructors | |
| `ComponentSerializerLibrary` (+ all serializer classes), `SystemSerializerLibrary` | public classes with public constructors and methods | `SerializerSystem.componentLibrary` / `.systemLibrary` getters are public. |
| `ComponentSerializerChunk(chunk, mask)` | public constructor | Selects entity subsets inside a chunk. |
| `EntitySerializer<T>`, `EntityDeserializer<T>` | public classes | Entity table and archetype internals are private. |
| `Context(..., formatTagCount, allocator)` | public | **Tag bits cannot be set without internal access.** |
| `ContextFormat.EnableTag`, `Context.m_Format`, `Context.version` setter | **internal** | Use reflection, or `UnsafeUtility.As<ContextFormat, UnsafeList<uint>>(ref fmt)` and set the bits. Both work because the struct copy shares `m_Bits.Ptr` (`ContextFormat.cs:9`). [I] |
| `SaveGameSystem.stream/context/RunOnce`, `LoadGameSystem.dataDescriptor/context/RunOnce` | public | |
| `GameManager.Load(..., AsyncReadDescriptor, ...)`, `SaveSimulationData`, `LoadSimulationData` | **private** | Use reflection. Mono reflection ignores access modifiers. [I] |
| `PrefabSystem.SavePrefabAssets` | **internal** | Use reflection. |
| `PrefabSystem.TryGetPrefab(PrefabID, out PrefabBase)`, `GetEntity(PrefabBase)`, `PrefabID` | public | |

### 11.2 Approach B (recommended for steady-state deltas): call `ISerializable` directly with our own writer and reader

**[I, built entirely from verified public APIs]**

1. **Context.**
   - For writes: `new Context(Purpose.SaveGame, Game.Version.current, default)`. Writers never inspect the format.
   - For reads: `new Context(Purpose.LoadGame, Game.Version.current, default, Enum.GetNames(typeof(FormatTags)).Length, Allocator.Persistent)`, then **enable every tag**, mirroring what `SerializeMetadata` writes. Cache this context for the whole session and dispose it once.
2. **Writer and reader.**
   - Implement `struct NetWriter : IWriter` holding a `NativeList<byte>` and a read-only `NativeParallelHashMap<int /*entity index*/, (int netId, int version)>`.
   - Implement `struct NetReader : IReader` holding a `NativeArray<byte>`, a **`NativeReference<int>` cursor**, a `NativeParallelHashMap<int netId, Entity>` and an optional `NativeList` of unresolved references.
   - Copy the primitive encodings from `BinaryWriter` / `BinaryReader`, or wrap a `BinaryWriter` for primitives and override only `Write(Entity)` / `Read(out Entity)`.
   - Prefabs: either reserve a net-ID range for prefab entities from the shared `PrefabID` table, or write a tag byte.
   - **Encode "unknown entity" separately from `Entity.Null`**, so the client can defer instead of silently nulling the reference.
3. **Per-type dispatch.**
   - Enumerate serializable types from `SerializerSystem.componentLibrary`: `GetSerializer(i).GetComponentType()` and `GetSerializerType()`. Alternatively scan `TypeManager` the way `ComponentSerializerLibrary.Initialize` does.
   - For each type, build delegates from generic helpers such as:
     ```csharp
     static void W<T>(EntityManager em, Entity e, ref NetWriter w) where T : unmanaged, IComponentData, ISerializable
         => em.GetComponentData<T>(e).Serialize(w);
     static void R<T>(EntityManager em, Entity e, NetReader r) where T : unmanaged, IComponentData, ISerializable
     {   T v = em.HasComponent<T>(e) ? em.GetComponentData<T>(e) : default;
         v.Deserialize(r);
         if (em.HasComponent<T>(e)) em.SetComponentData(e, v); else em.AddComponentData(e, v); }
     ```
   - Write similar helpers for buffers (`DynamicBuffer<T>`: length, then elements; mirror `BufferElementDataSerializer.cs:80-94`), for enableable components (also send `IsComponentEnabled`), for `IEmptySerializable` tags (presence only) and for the two shared components (`SetSharedComponent`).
4. **Apply on the client.**
   - Write into the existing entity, or create one. For creation, use `default(T)` before `Deserialize`, as the game does (`BinaryReader.cs:159-163`).
   - For updates, deserializing into the **existing** value preserves fields that the component does not serialize. **[I]** Choose per type.
   - Then add `Updated` (and `Created` for new entities) so that the normal derived-data systems rebuild geometry, search trees and so on. On load the game uses `OnGameLoaded` instead (section 8.3).
5. **Threading.** Run all of this on the main thread with `EntityManager`, or inside our own jobs. **[I]** Jobs instantiated with a mod-defined `TWriter` are not Burst-AOT-compiled, so they run as managed code. That is acceptable for delta-sized payloads.

**Pros:**
- It works per entity and per component.
- It uses the game's own versioned wire format, including version, purpose and tag branches.
- Remapping is under our full control.

**Cons:**
- We must select which components to send, and handle derived data and archetype changes ourselves.

### 11.3 Approach A (bulk spawn of an entity set): "mini save" with the game's drivers and an empty system library

**[I, built from verified public classes]**

- Create our own `ComponentSerializerLibrary` and call `Initialize(ourSystemBase, out _)`. **[I]** Using our own library avoids interfering with `SerializerSystem`'s instance, which is shared with autosave.
- Create a `SystemSerializerLibrary` and call `Initialize(new World("ST-empty"))`. **[I]** An empty `World.Systems` list yields zero system serializers, so **no system state is written and no `SetDefaults` runs** on read.
- Implement `IWriteBufferProvider<MyBuf>` and `IReadBufferProvider<MyBuf>` over memory (`BufferFormat.Raw` or LZ4).
- Host: `new EntitySerializer<MyBuf>(em, compLib, sysLib, provider).Serialize<BinaryWriter, FormatTags>(ctx, query)`.
- Client: `new EntityDeserializer<MyBuf>(em, compLib, sysLib, provider).Deserialize<BinaryReader, FormatTags>(ref ctx)`.
  - The client gets **correct format tags for free**, because `DeserializeMetadata` rebuilds the `Context` (`EntityDeserializer.cs:256-318`).
  - The client gets **fresh entities with intra-batch references remapped**.
- Gaps:
  - **References to entities outside the batch, including prefabs, become `Entity.Null`** (`BinaryWriter.cs:139-140`).
  - Fixing that requires either (a) including NetId components and patching references afterwards from a side channel, or (b) a custom `TWriter`/`TReader` passed as the generic argument. The drivers are generic over `TWriter`/`TReader` (`EntitySerializer.cs:410`, `EntityDeserializer.cs:178`), so the custom writer can encode external references (`-2` followed by a net ID) through a map reachable from the job. **[I]** This is feasible but intricate.
- Drawbacks:
  - It uses an **exclusive entity transaction** on read (`EntityDeserializer.cs:197`).
  - It may create stripped archetypes on the host (`EntitySerializer.cs:494`).
  - Entity order in the stream is archetype/chunk order, so net IDs must be sent alongside (or carried as a `NetId` component).

### 11.4 Approach C: reuse the per-type `ComponentSerializer` jobs with our own entity tables

**[I]**
- Call `compLib.Update(ctx)` first.
- For each type, call `compLib.GetSerializer(i).SerializeData<BinaryWriter>(new EntityWriterData(ctx, buf, ourWriterTable), chunks, deps)`, with chunks built from `new ComponentSerializerChunk(chunk, mask)`.
- On the client, call `DeserializeData<BinaryReader>(new EntityReaderData(ctx, bytes, pos, ourReaderTable), entitiesInSameOrder, serializerType, deps)`.

Tables:
- **Writer table:** indexed by live `Entity.Index`, holding `{netId, version}`, of length `HighestEntityIndex + 1`.
- **Reader table:** `netId → live entity`.

Trade-offs:
- It gives column-oriented, compressed bulk transfer with the game's own code.
- The target entities must **already have** the component, because the jobs use `ComponentLookup` / `BufferLookup` (`ComponentDataSerializer.cs:95-96,133-183`).
- The order of entities must match exactly.
- **[?]** Whether these jobs are Burst-compiled in the shipped build is unknown. They are created through `MakeGenericType` / `Activator.CreateInstance` (`ComponentSerializerLibrary.cs:66-97`), which Burst cannot see statically.

### 11.5 Building the ID map at join time

**[V for mechanism, I for the design]**
- Add a mod component `struct NetId : IComponentData, ISerializable { public int Value; }`. It is automatically included in the game's save stream (section 0).
- The host assigns `NetId` to every replicated entity **before** producing the join snapshot.
- After the client loads, it queries `NetId` and builds `netId → Entity`.
- A save made during a session carries `NetId`. Loading that save without the mod drops the component harmlessly (`EntityDeserializer.cs:361-374, 498-516`).
- Caveat: entities that carry *only* mod components would also enter the save query (`SerializerSystem.cs:194-197`), and would not be cleared by `ClearSystem` (`ClearSystem.cs:23-53`).

The rejected alternative is deriving the map from save order. Host-side save indices live only in `EntitySerializer`'s private `m_EntityTable`, which is disposed right after the save (`EntitySerializer.cs:373-402`).

### 11.6 Can individual systems' state be serialized at runtime?

**[V API, I usability]** Yes, mechanically:
- `IDefaultSerializable` systems: `system.Serialize(writer)` / `system.Deserialize(reader)`.
- `IJobSerializable` systems: `system.Serialize<TWriter>(new EntityWriterData(...), deps)`.

However, many deserializers are load-time-only, with resets, texture rebuilds or `m_Loaded` flags (`SimulationSystem.cs:140-146`, `TerrainSystem.cs:3133-3163`). **Treat system state case by case.** Prefer syncing the *inputs*, such as tax rates, policies and service fees, through our own commands. Use whole-system deserialization only for systems verified to be side-effect-free, e.g. `CellMapSystem` maps through their job `Deserialize` (`CellMapSystem.cs:123-138`).

### 11.7 Alternative noted: Unity `SerializeUtility.SerializeWorld` / `DeserializeWorld`

- **[V existence]** These are public in Unity.Entities (`Unity.Entities/Unity.Entities.Serialization/SerializeUtility.cs:369-394, 621`), with format version 78 (`:222`).
- **[I]** They dump raw chunk memory for a whole world, including non-serialized runtime components, and require identical type layouts. They are only usable through a staging world. They do not go through the game's versioned `ISerializable` logic and are not recommended.

---

## 12. Pitfalls (checklist)

1. **Reader `Context` without tags crashes** (null `UnsafeList` pointer). Always allocate tag storage and enable all tags (`ContextFormat.cs:31-35`, `UnsafeList.cs:210-222`). Examples of tag reads: `PrefabID.Deserialize` (`PrefabID.cs:100`) and `Citizen.Deserialize` (`Citizen.cs:174,179`).
2. **`Purpose` changes semantics.** Use `LoadGame` for runtime reads; `NewGame` resets fees (`ServiceFee.cs:22-29`) and more. `CityConfigurationSystem` writes differently for `SaveGame` vs `SaveMap` (`CityConfigurationSystem.cs:278,326`).
3. **Version.** Both peers must run an identical `Game.Version.current` (`Game/Game/Version.cs:825`) **and** identical mod and content sets. Exchange and compare them at handshake. Wire formats branch on version in about 205 files.
4. **Partial fields.** Some components do not serialize everything:
   - Derived values are recomputed in `Deserialize`, e.g. `Curve.m_Length` (`Game.Net/Curve.cs:15-24`).
   - Values are narrowed, e.g. `Building.m_Flags` is written as a `byte` (`Building.cs:24-25`).
   - `IEmptySerializable` components carry no data at all (`EdgeGeometry`, `Composition`, `CullingInfo`).
   - Clients must rebuild derived data. Add `Updated` / `Created`, or rely on `OnGameLoaded` after a full load (`GeometrySystem.cs:3385-3404`).
5. **Runtime-only components** (`Updated`, `Created`, `Deleted`, `Temp`) are not serialized (`Game.Common/Updated.cs:7`, `Created.cs:7`, `Deleted.cs:7`, `Game.Tools/Temp.cs:5`). Entities with `Temp` / `Deleted` are excluded from saves (`SerializerSystem.cs:201-208`). `RequiredComponentSystem` re-adds many runtime components after load (`RequiredComponentSystem.cs:753-793`). **A runtime-created entity must receive the same required components.** [I] Spawning through the game's own creation path is safer than raw archetype construction.
6. **Prefab entity identity differs per machine.** Map prefabs through `PrefabID` (`PrefabSystem.cs:934-942`). Never send raw prefab `Entity` values.
7. **Stale or foreign references** become `-1` / `Entity.Null` with the stock writer and reader (`BinaryWriter.cs:128-141`, `BinaryReader.cs:146-157`). Use a custom reader to detect and defer unresolved references, which also covers out-of-order arrival.
8. **`PathNode` uses `Write(Entity, ignoreVersion: true)`** with a fabricated index-only `Entity` (`PathNode.cs:130-145`). A custom writer must handle index-only lookups.
9. **Enableable components.** Enabled bits are part of the data, except for `ISerializeAsEnabled` types (`EnableableComponentDataSerializer.cs:30-75`, `ComponentSerializerLibrary.cs:57,82,86`). Replicate `IsComponentEnabled` explicitly.
10. **Shared components.** `UpdateFrame` (simulation bucket) and `CoverageServiceType` must be replicated through `SetSharedComponent`. The game applies them after deserialization in an exclusive transaction (`SharedComponentDataSerializer.cs:194-207`).
11. **`EntityDeserializer` resets systems missing from the stream** (`EntityDeserializer.cs:627-631`). Never feed it the real `SystemSerializerLibrary` for partial data.
12. **`EntityDeserializer` takes an exclusive entity transaction** (`:197-214`). Do not run it mid-frame alongside other `EntityManager` use.
13. **System deserializers have side effects**, e.g. `TerrainSystem.Deserialize` (`TerrainSystem.cs:3133-3163`). Do not call them at runtime unless audited.
14. **Saving mutates the host world slightly**: `TrimPathsSystem`, `PrefabData` enable states and `m_Index` renumbering (`BeginPrefabSerializationSystem.cs:260-303`). It also blocks for a while. Schedule join snapshots deliberately.
15. **Loading needs a file path.** There is no in-memory load path (`StreamBinaryReader.cs:17-48`). Use a temp file.
16. **The full load entry point is private.** `GameManager.Load(…AsyncReadDescriptor…)` (`GameManager.cs:1046`) needs reflection. Calling `LoadGameSystem.RunOnce` directly skips `onGamePreload`.
17. **Package assets.** Climate, water-render and terrain-render prefabs from the map package are bundled only by `GameManager.Save` (`PrefabSystem.cs:378-397`). A bare `SaveGameSystem` snapshot omits them.
18. **Serializer library lifetime.** `SerializerSystem`'s libraries are rebuilt when dirty (mod load). Their per-serializer type handles are tied to `SerializerSystem`. Prefer our own `ComponentSerializerLibrary` initialized with our own system to avoid clashes with autosave (`ComponentSerializerLibrary.cs:25-31, 131-138`; `SerializerSystem.cs:73-92`).
19. **Writer and reader are passed by value.** Keep all mutable state, such as the cursor and the buffer, in native containers (`BinaryReader.cs:14-18, 159-168`).
20. **Burst.** Our generic instantiations will run as managed code. [I] Budget for that, or pre-batch on the host.

---

## 13. Recommendations for Skyline Together

1. **Join:**
   1. The host assigns `NetId` to all replicated entities.
   2. The host produces simulation bytes, through `SaveGameSystem` into a `MemoryStream` or through `GameManager.Save` to a temp package if package prefabs matter.
   3. The host streams the bytes, plus a `PrefabID` table and version and mod manifests, to the client.
   4. The client writes a temp file and loads it through reflection on the private `GameManager.Load(..., AsyncReadDescriptor, ...)` with `Purpose.LoadGame`.
   5. The client rebuilds the `netId → Entity` map from `NetId`.
2. **Steady state:** Approach B. Use per-component `ISerializable` deltas with a custom `NetWriter` / `NetReader`, a cached "all tags enabled" `LoadGame` context, a net-ID map, a prefab-ID map, deferred reference resolution, and `Updated` / `Created` tagging on apply.
3. **Bulk spawns** (large tool operations): Approach B batched, or Approach A with an empty system library, if profiling shows B too slow.
4. **Global state:** replicate player commands and settings (taxes, fees, policies, time and speed) rather than deserializing systems. For drift recovery, fall back to a full resync with the join path.

---

## 14. Open questions

- **[?]** Are the generic serializer jobs Burst-compiled in the shipped build? This matters for performance in Approach C.
- **[?]** How long does the `Serialize` phase take on large cities? This determines whether a resync is seamless or needs a pause.
- **[?]** What exactly does a client lose when it loads a bare sim snapshot without the package prefab assets, if the map uses custom climate or render settings? Test with a map package.
- **[?]** Which components need explicit `Updated`/`Created` and which required runtime components must be added when spawning raw entities from deltas? This needs a per-domain audit (net, objects, areas, zones, routes) in later reports.
