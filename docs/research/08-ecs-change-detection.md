# 08 - ECS change detection and bulk data access at scale

> **Historical research report.** Written before and during the implementation; it describes the game, not the
> mod, and is not maintained. For how the mod works, see [docs/ARCHITECTURE.md](../ARCHITECTURE.md). Code is cited
> by file and member and described in prose; no game code is reproduced.

Research for **Skyline Together** (host-authoritative co-op for Cities: Skylines II).
Topic: how to find out cheaply, at the scale of a whole city, *which* entities and components changed, how to read and write
their raw bytes for component types known only at runtime, and how to remap `Entity` references so that a generic
"replicate the selected component types for the selected archetypes" engine works.

All paths below are relative to the root of the game's decompiled assemblies unless stated otherwise.
`UE/` is short for `Unity.Entities/Unity.Entities/`. Line numbers refer to the decompiled files.

How the claims are marked:

- **[V]** verified: read in the decompiled code at the cited location.
- **[I]** inference: my reasoning, an estimate, or general knowledge of Unity DOTS that this decomp does not confirm directly.

---

## 0. Executive summary

1. The game ships **Unity Entities 1.0.x** [I: inferred from API surface]. It is a **release build with safety checks
   stripped**: no `AtomicSafetyHandle`, and the `Check*` methods are empty `[Conditional]` stubs [V]. Every
   `Unity.Entities` API we need for a generic replicator is **public**, except the convenience `EntityManager.*Raw*` methods,
   which are `internal`.
2. **Change versions are per (chunk, component type)**, not per entity, and they record *write access*, not actual writes.
   Any RW pointer, RW `NativeArray`, RW `ComponentLookup` write, RW enabled-bit access or structural move stamps the chunk
   column with the current `GlobalSystemVersion` [V]. CS2 jobs routinely take RW handles over whole chunk slices
   (`UpdateFrame` slicing), so a version bump means "maybe changed". **Change versions make a good coarse filter, but you
   must byte-compare against a shadow copy to get exact deltas.** Unity's own `EntityDiffer` uses exactly this
   two-stage design [V].
3. **Entities move between chunks constantly in CS2.** Tag components such as `Updated`, `BatchesUpdated` and `Created` are
   added and removed every frame [V]. Adding a tag to a single entity moves it to another chunk, and removing entities
   swap-backs the last entity into the hole, which bumps only the *order* version [V]. So a shadow copy keyed by
   chunk position is wrong. **Shadow state must be keyed per entity**, by `Entity.Index` with a version check or by
   network slot.
4. Runtime-typed access is fully covered by public APIs:
   - `ComponentType.ReadOnly(Type)` and `TypeManager.GetTypeIndex(Type)` resolve a type at runtime.
   - `DynamicComponentTypeHandle` with `ArchetypeChunk.GetChangeVersion/DidChange/GetDynamicComponentDataArrayReinterpret<byte>`
     gives untyped component access.
   - `GetUntypedBufferAccessor` (returns `UnsafeUntypedBufferAccessor`) gives untyped buffer access.
   - `GetEnableableBits` reads enabled bits.
   - `TypeManager.GetTypeInfo` gives sizes and `TypeManager.GetEntityOffsets(typeIndex, out count)` gives the
     `Entity` field offsets used for remapping [V].
5. **Cross-machine identifiers:** `TypeIndex` values are process-local. Mod types are appended at mod-load time
   (`ModManager.AfterLoadAssembly` calls `TypeManager.InitializeAdditionalTypes`) [V]. On the wire, use
   `TypeInfo.StableTypeHash`, which hashes namespace, name, assembly and field layout recursively [V]; resolve it with
   `TypeManager.GetTypeIndexFromStableTypeHash` [V]. For archetypes use `EntityArchetype.StableHash` [V].
6. **Entity identity:** `Entity` = (Index, Version). Indices are reused aggressively: the lowest free bit in the first
   non-full block of 8192 is taken first. The version is odd while the entity is alive and is incremented on allocation
   and again on destruction [V]. Raw `Entity` values are meaningless on another machine, so a network-id layer is
   mandatory. I recommend a **side table indexed by `Entity.Index` (with version check), not a component**.
   - Adding a non-zero-sized component to game entities is *not* an in-place chunk operation: `AreLayoutCompatible` fails,
     so every entity is memcpy'd into new chunks [V].
   - Such a component would also be stripped from saves [V].
   - The game itself uses exactly this dense-table-by-`Entity.Index` pattern for save/load remapping [V].
7. **Structural changes are sync points.** Every `EntityManager` structural call completes all jobs (`BeforeStructuralChange`
   then `CompleteAllJobsAndInvalidateArrays`) [V]. The receiver must batch creates and destroys per tick: either one
   main-thread block, or ECB playback at one of the game's `SafeCommandBufferSystem` barriers.
8. **Section 12 gives a concrete recipe**, and section 13 gives cost estimates. Summary: for about 1M replicated
   entities with about 8 replicated types, a full per-tick change scan costs about 1-3 ms single-threaded in Burst, or
   well under 1 ms parallel. Shadow memory equals the replicated payload, roughly 30-100 MB. The real limit is
   **bandwidth**, not CPU, so the type selection and the delta encoding matter more than the scan.

---

## 1. Entities package version, build flavor, toolchain

| Fact | Evidence |
|---|---|
| The package is 1.0.x. Many `[Obsolete("... (RemovedAfter Entities 1.0)")]` overloads are still present. `IJobChunk.Execute(in ArchetypeChunk, int, bool, in v128)` is the 1.0 signature. `EntityQueryDesc` has `Disabled/Absent/Present`. `IAspect`, `RefRW`, `EnabledRefRW` and `ComponentLookup` all exist. | [V] `UE/ArchetypeChunk.cs:109,120,131` (obsolete overloads); `UE/IJobChunk.cs:9`; `UE/EntityQueryImpl.cs:1066-1074` (Disabled/Absent/Present). Exact patch version: [I]; the DLL file version is `0.0.0.0` (checked with PowerShell on `Cities2_Data\Managed\Unity.Entities.dll`). |
| Safety checks are stripped. `DynamicComponentTypeHandle` has no safety handle fields, and `Check*` methods are empty `[Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]` stubs. | [V] `UE/DynamicComponentTypeHandle.cs:9-27`; `Unity.Entities/Unity.Entities.LowLevel.Unsafe/UnsafeUntypedBufferAccessor.cs:37-45` |
| Consequence: nothing stops a race between our main-thread reads or writes and running jobs. We must complete dependencies ourselves (`EntityQuery.CompleteDependency()` at `UE/EntityQueryImpl.cs:815-818`, `EntityManager.CompleteDependencyBeforeRO/RW<T>` at `UE/EntityManager.cs:2660-2670`) or schedule our own jobs with a correct `Dependency`. | [V] / [I] |
| Mods can ship Burst code. `ExecutableAsset` loads `<mod>_win_x86_64` Burst libraries via `BurstRuntime.LoadAdditionalLibrary`. | [V] `Colossal.IO.AssetDatabase/Colossal.IO.AssetDatabase/ExecutableAsset.cs:113,249-256`; `Game/Game.Modding/ModManager.cs:66,177`. Third-party mods use `[BurstCompile]`, e.g. `thirdparty/Anarchy/.../CheckTransformSystem.cs`. |
| The game runs on Mono: managed DLLs are in `Cities2_Data\Managed`. Calling `internal` Entities methods is possible with a publicized reference assembly or with reflection. | [I] (folder listing verified; access-check behavior not verified) |

**Recommendation:** use only the public API surface described below. Treat the internal `EntityManager.GetComponentDataRawRO/RW`,
`SetComponentDataRaw`, `GetBufferRawRO/RW` and `GetBufferLength` (`UE/EntityManager.cs:2730-2797`) as optional fast paths
behind reflection, never as a requirement.

---

## 2. Memory model relevant to replication

- **Chunk:** 16 KiB, with 16320 bytes of component storage and at most 128 entities [V] `UE/Chunk.cs:28-37`.
  Data is SoA. Column `i` lives at `chunk.Buffer + archetype->Offsets[i] + SizeOfs[i]*entityIndex`
  [V] `UE/ChunkDataUtility.cs:231-244`. Column 0 is always `Entity` [V] `UE/EntityComponentStore.cs:3575-3577`,
  `UE/ArchetypeChunk.cs:95-102`.
- **Chunk identity:** `ArchetypeChunk.SequenceNumber` (public) is assigned from a world-unique, monotonically increasing
  counter when a chunk is created (`AddEmptyChunk`). It is never reused within a world
  [V] `UE/ChunkDataUtility.cs:1360`, `UE/EntityComponentStore.cs:1349,2300-2301`, `UE/ArchetypeChunk.cs:38`.
  `ChunkIndex` (the slot) *is* reused. A chunk can keep its SequenceNumber while switching archetype in place
  (`ChangeArchetypeInPlace` and `MoveArchetype`, see §3.3).
- **Per-chunk metadata** lives in `ArchetypeChunkData`, which is SoA across all chunks of an archetype:
  - `uint ChangeVersions[typeCount][chunkCapacity]`
  - entity counts
  - shared component values
  - enabled bits (one `v128` per enableable type per chunk), plus disabled counts

  [V] `UE/ArchetypeChunkData.cs:28-50,230-260`.
  The **order version is the change version of column 0** [V] `UE/ArchetypeChunkData.cs:240-242,258-260`.
- **Buffers** (`IBufferElementData`): the chunk column holds a `BufferHeader { byte* Pointer @0; int Length @8; int Capacity @12; }`
  followed by internal-capacity storage. `Pointer != null` means the elements live on the heap
  [V] `UE/BufferHeader.cs:9-38`. **Never send the raw chunk bytes of a buffer column**, because they contain a
  process-local pointer.

---

## 3. Chunk change versions: exact semantics

### 3.1 Version counters

| Item | Semantics | Evidence |
|---|---|---|
| `GlobalSystemVersion` (world-wide `uint`) | Incremented, skipping 0, in `BeforeUpdateVersioning` *and* `AfterUpdateVersioning` around every system update that actually runs (`Enabled && ShouldRunSystem()`). Also incremented twice around `OnCreate`. | [V] `UE/ChangeVersionUtility.cs:16-23`; `UE/SystemState.cs:351-365,403-418`; `UE/SystemBase.cs:38-80`; `UE/ComponentSystemBase.cs:126-130` |
| `LastSystemVersion` (per system) | Set to the global version at the end of the system's last update. Before each update, every query owned by the system gets `RequiredChangeVersion = m_LastSystemVersion`. | [V] `UE/SystemState.cs:29,158,357,363,474` |
| `DidChange(changeVersion, requiredVersion)` | `requiredVersion == 0` returns **true** (first run). Otherwise returns `(int)(changeVersion - requiredVersion) > 0`, a wrap-safe comparison that is valid while the distance is under 2^31. | [V] `UE/ChangeVersionUtility.cs:7-14` |
| Chunk change version for type T | Written by **every RW access path**, whether or not anything was written: `GetComponentDataWithTypeRW`, `GetOptionalComponentDataWithTypeRW`, `GetComponentDataRW`, `GetEnabledRefRW` and others. | [V] `UE/ChunkDataUtility.cs:171-185,200-212,222-229,238-244,253-261` |
| `ArchetypeChunk.GetNativeArray(ref handle)` with an RW handle | Stamps the version (it calls the `...RW` path when `!IsReadOnly`). | [V] `UE/ArchetypeChunk.cs:652-661` |
| `ComponentLookup<T>` indexer set / `GetRefRW` | Stamps the chunk that holds the target entity. | [V] `UE/ComponentLookup.cs:36-41,178-185` |
| `ComponentLookup<T>.DidChange(Entity, version)`, `BufferLookup<T>.DidChange` | Per-entity test, but at chunk granularity. | [V] `UE/ComponentLookup.cs:130-144`; `UE/BufferLookup.cs:91-105` |
| Enabled bits | `SetComponentEnabled` uses `GetEnabledRefRW`, which **bumps T's change version** for the chunk. | [V] `UE/EntityComponentStore.cs:3520-3545`; `UE/ChunkDataUtility.cs:253-261` |
| `GetChangeVersion(ref DynamicComponentTypeHandle)` for a type the archetype lacks | Returns `0`. | [V] `UE/ArchetypeChunk.cs:174-183` |

Wrap-around estimate [I]: CS2 has about 734 system classes (counted `: GameSystemBase|: SystemBase|: ComponentSystemBase`
under `Game/`). Assume a few hundred run per frame. That is roughly 2 increments × 300 × 60 fps ≈ 36k/s, and more at
high sim speed, so 2^31 is reached after about 16 h. **Do not compare against stored "last seen global versions" with
`DidChange` across very long sessions. Store the per-chunk version snapshot and test for inequality instead**, which
is what Unity's differ does (§11).

### 3.2 Query-level change filters

- `EntityQuery.SetChangedVersionFilter(ComponentType)`, `AddChangedVersionFilter`, `SetOrderVersionFilter` and
  `AddOrderVersionFilter` are public. They accept **at most 2** changed filters, and the filters are **OR-ed**
  (changed A *or* changed B *or* order changed) [V] `UE/EntityQueryImpl.cs:741-784`, `UE/MatchingArchetype.cs:130-158`.
- The filter's `RequiredChangeVersion` is set automatically only for queries owned by a system, to that system's
  `LastSystemVersion` [V] `UE/SystemState.cs:351-359`. `SetChangedFilterRequiredVersion` is `internal`
  [V] `UE/EntityQuery.cs:421-424`.
- `ToArchetypeChunkArray` and `ToArchetypeChunkList` apply the filter, including enableable filtering
  [V] `UE/EntityQueryImpl.cs:345-372`.
- **Conclusion:** query filters cannot express "changed since *my* per-peer baseline for K types". A replicator should
  enumerate chunks without a filter and test versions per chunk and per type itself (§12).
- Cheap global short-circuit: `EntityQuery.GetCombinedComponentOrderVersion()` and `EntityManager.GetComponentOrderVersion(ComponentType)`.
  These per-type counters advance on any structural change that touches the type, but **not** on value writes
  [V] `UE/EntityQueryImpl.cs:830-838`, `UE/EntityManager.cs:2407-2415`, `UE/ChunkDataUtility.cs:766,803,990`.

### 3.3 What bumps what (sender-side truth table)

| Event | Component change versions | Order version | Entity position |
|---|---|---|---|
| RW access to column T in a chunk | T stamped | - | - |
| New entities allocated into a chunk (`Allocate`; used by create, instantiate and move-into) | **all** columns stamped | stamped | appended | 
| Entities removed (destroy, or move-out) | none (the swapped-in entity's data is copied silently) | stamped | **last entities swap into the hole** |
| Whole chunk changes archetype in place (`ChangeArchetypeInPlace`): only when layouts are compatible, i.e. zero-sized tags or shared components added or removed | cloned from the source; new types get the current version | stamped | unchanged, SequenceNumber kept |
| `MoveArchetype` (whole chunk re-filed) | all stamped (`AddToChunkList(...globalSystemVersion)`) | stamped | unchanged |
| Single entity gets a tag (`AddComponent<Updated>(e)`) | destination chunk: all stamped (Allocate) | both chunks stamped | **entity moves to another chunk** |

Evidence [V]:
- Allocate: `UE/ChunkDataUtility.cs:751-767`.
- Remove: `UE/ChunkDataUtility.cs:769-813`.
- In place: `UE/ChunkDataUtility.cs:918-929` (inside `ChangeArchetypeInPlace`, 815-963).
- Layout compatibility: `UE/ChunkDataUtility.cs:630-648`.
- MoveArchetype: `UE/ChunkDataUtility.cs:965-992`.
- AddToChunkList: `UE/ArchetypeChunkData.cs:279-289`.
- CloneChangeVersions: `UE/ChunkDataUtility.cs:994-1025`.
- Chunk vs batch move: `UE/EntityComponentStore.cs:3380-3420`.

**Implications for replication:**
1. "Order version changed" means the entity-to-slot mapping changed, and data **may** have moved without any
   component-version bump (swap-back). Any shadow comparison for such a chunk must use per-entity keys.
2. Moves are conservative: the destination always looks fully changed. Combined with the byte diff this is
   harmless, but it means a lot of false-positive scanning.

---

## 4. How the game itself detects changes

**Primary mechanism: tag components plus a cleanup pass [V].**
- The tags are zero-field structs with `Size = 1`: `Game.Common.Created`, `Updated`, `Deleted`, `Applied`, `BatchesUpdated`,
  plus `EffectsUpdated` and `PathfindUpdated`. See `Game/Game.Common/Updated.cs:6-9`, `Created.cs`, `Deleted.cs`, `Applied.cs`, `BatchesUpdated.cs`.
- `PrepareCleanUpSystem` (MainLoop) gathers two lists:
  - entities with `Deleted` or `Event`;
  - entities with any of {Created, Updated, Applied, EffectsUpdated, BatchesUpdated, PathfindUpdated} and without `Deleted`.

  See `Game/Game.Common/PrepareCleanUpSystem.cs:23-52`.
- `CleanUpSystem` (`SystemUpdatePhase.Cleanup`) then destroys the first list and removes the six tags from the second
  with one `RemoveComponent(NativeArray<Entity>, ComponentTypeSet)`. See `Game/Game.Common/CleanUpSystem.cs:24-56` and
  `Game/Game.Common/SystemOrder.cs:50,54`.
- Usage is widespread: 139 files query `ReadOnly<Updated>()`, 235 query `ReadOnly<Deleted>()`, and 150 add `Updated`,
  `BatchesUpdated` or similar (grep counts under `Game/`).

**Secondary mechanisms (rare) [V]:**
- `SetChangedVersionFilter` / `AddChangedVersionFilter`: `Game/Game.Simulation/CountHouseholdDataSystem.cs:1042-1046,1096`;
  `Game/Game.UI.InGame/ChirperUISystem.cs:103-104` (combined with `AddOrderVersionFilter`).
- `EntityManager.GetComponentOrderVersion<T>()` as a "did anything structural happen" check in UI systems, for example
  `Game/Game.UI.InGame/TutorialsUISystem.cs:170-176`, `LifePathUISystem.cs:93-101`, `TaxationUISystem.cs:170`.
- Another system's `LastSystemVersion` as a "did system X run" stamp: `Game/Game.Rendering/BatchDataSystem.cs:2570-2638`.
- `ArchetypeChunk.DidChange` is **not used anywhere in `Game/`** (grep: 0 hits).

**`UpdateFrame` slicing:** `Game.Simulation.UpdateFrame` is a shared component [V] `Game/Game.Simulation/UpdateFrame.cs:6`.
- 28 files call `SetSharedComponentFilter(new UpdateFrame(...))`.
- 54 files early-return in jobs via `chunk.GetSharedComponent(m_UpdateFrameType).m_Index != m_UpdateFrameIndex` before
  taking RW arrays, e.g. `Game/Game.Simulation/HouseholdBehaviorSystem.cs:127-133` [V].
- Consequence [I]: on each simulation tick, roughly 1/16 of the chunks of sim-processed archetypes get their RW columns
  stamped whether or not values changed. That bounds the false-positive rate of a pure version filter. It also means
  "changed" chunks rotate deterministically through slices.

**Replication relevance:**
- `Created` / `Updated` / `Deleted` exist only between the modification phases and `Cleanup` of the same frame. A host
  replicator running **after `ModificationEnd` and before `Cleanup`** sees them [I, ordering from
  `Game/Game/SystemUpdatePhase.cs:3-38` and `SystemOrder.cs`] and can use them as *hints*.
- They are not complete: plain sim writes produce no tag, which is why version plus byte diff is required.
- On the receiver, applying replicated bytes does **not** trigger the game's reactive systems unless we add `Updated` /
  `BatchesUpdated` ourselves [I]. Whether we want that depends on whether derived components are replicated or recomputed
  locally (to be designed in a separate doc).
- `Game.Tools.Temp` entities (tool previews) must be excluded from replication [I].

---

## 5. Runtime-typed components: `TypeManager`, `TypeIndex`, stable identity

| API | Notes | Evidence |
|---|---|---|
| `TypeManager.GetTypeIndex(Type)` | Throws if the type is unregistered. `TryGetTypeIndex` is `internal`. | [V] `UE/TypeManager.cs:1357,1407-1415` |
| `ComponentType.ReadOnly(Type)`, `ReadWrite(Type)`, `ReadOnly(TypeIndex)`, `FromTypeIndex`, `Exclude(...)`, implicit from `Type` | All public. | [V] `UE/ComponentType.cs:61-177` |
| `TypeManager.GetTypeInfo(TypeIndex)` returns `ref readonly TypeInfo` | Fields: `SizeInChunk`, `ElementSize`, `BufferCapacity`, `AlignmentInBytes`, `Category`, `EntityOffsetCount`, `BlobAssetRefOffsetCount`, `WeakAssetRefOffsetCount`, `UnityObjectRefOffsetCount`, `TypeSize`, `StableTypeHash`, `MemoryOrdering`, `IsZeroSized`, `HasBlobAssetRefs`, `EnableableType`. | [V] `UE/TypeManager.cs:116-235,961-970` |
| `TypeManager.GetEntityOffsets(TypeIndex, out int count)` returns `EntityOffsetInfo*` (`{ int Offset; }`) | Byte offsets of every `Entity` field inside one element: the struct itself, or one buffer element. Public. | [V] `UE/TypeManager.cs:111-114,882-893` |
| `TypeIndex` flag properties | `IsBuffer`, `IsSharedComponentType`, `IsManagedType`, `IsZeroSized`, `IsEnableable`, `IsChunkComponent`, `IsCleanupComponent`, `HasEntityReferences`, `HasNativeContainer`, `IsChunkSerializable`, `IsBakingOnlyType`, `IsTemporaryBakingType`, `Index`. | [V] `UE/TypeIndex.cs:23-194`, flag constants `UE/TypeManager.cs:487-535` |
| `TypeManager.GetTypeIndexFromStableTypeHash(ulong)` | Hash map lookup. Returns `TypeIndex.Null` if unknown. | [V] `UE/TypeManager.cs:1683-1694` |
| `StableTypeHash` | FNV1A64 over namespace, nested declaring types, name, assembly name, generic args, `[TypeVersion]`, and recursively each instance field's type hash, index and `[FieldOffset]`. | [V] `UE/TypeHash.cs:57-149` |
| `EntityArchetype.StableHash` | FNV combination of the member types' `StableTypeHash` in memory order: a cross-machine archetype id. | [V] `UE/EntityArchetype.cs:26`; `UE/EntityComponentStore.cs:2372-2381,2629` |
| Mod type registration | `ModManager.AfterLoadAssembly` calls `TypeManager.InitializeAdditionalTypes(assembly)`, which appends new types. | [V] `Game/Game.Modding/ModManager.cs:147-151`; `UE/TypeManager.cs:623-678` |

**Rules:**
- Never send a `TypeIndex`. Send `StableTypeHash`, or better a session-local `ushort` assigned in the handshake from a
  sorted list of `StableTypeHash`.
- During the handshake, compare the full set of replicated `StableTypeHash` values plus `SizeInChunk`/`ElementSize` and
  refuse to start on mismatch. That set covers game version and mod set. Matching hashes imply identical field
  type/order, hence identical sequential layout on the same runtime [I: very strong but not absolute. Primitive field
  sizes are fixed on x64 Mono/Burst; `bool` marshalling is irrelevant in chunks].

**Eligibility filter for "raw-byte replicable" types** (all checkable at runtime from `TypeIndex` / `TypeInfo`):
- Accept: `IsComponentType` (plain `IComponentData`) or `IsBuffer`.
- Reject: `IsManagedType`, `IsSharedComponentType` (handle separately), `IsChunkComponent`, `HasNativeContainer`,
  `TypeInfo.HasBlobAssetRefs`, `HasUnityObjectRefs`, `HasWeakAssetRefs`, `IsCleanupComponent`, baking-only types.
- Zero-sized tags carry no data. Their presence is part of the archetype signature (§12.5).
- Known gap [V]: `EntityRemapUtility.CalculateOffsetsRecurse` only recurses into non-primitive value-type fields and stops
  at `Entity`, `BlobAssetReferenceData`, `UntypedWeakReferenceId` and `UntypedUnityObjectRef`
  (`UE/EntityRemapUtility.cs:129-185`). [I] An `Entity` hidden inside a `fixed byte[]`, a pointer, or an opaque
  `FixedList`/`FixedBytes` payload would **not** appear in `EntityOffsets`. A one-time startup audit should list replicated
  types whose fields include `FixedList*`, `fixed` buffers or pointers.
- No `BlobAssetReference<` appears anywhere in `Game/` (grep, 0 hits), so blob refs are a non-issue for vanilla types [V].

---

## 6. Dynamic and raw access APIs

### 6.1 Chunk-level, untyped (preferred)

| API | Behavior | Evidence |
|---|---|---|
| `EntityManager.GetDynamicComponentTypeHandle(ComponentType)` | Creates a handle stamped with the current `GlobalSystemVersion`. Registers no dependency. | [V] `UE/EntityManager.cs:2447-2450` |
| `SystemState` / `ComponentSystemBase.GetDynamicComponentTypeHandle(ComponentType)` | Calls `AddReaderWriter` first, so the system's `Dependency` includes jobs touching that type. | [V] `UE/SystemState.cs:569-573`; `UE/ComponentSystemBase.cs:263-266` |
| `DynamicComponentTypeHandle.Update(SystemBase / ref SystemState)` | Refreshes `GlobalSystemVersion`. Required every frame before an RW handle stamps versions correctly. | [V] `UE/DynamicComponentTypeHandle.cs:39-47` |
| `chunk.Has(ref dyn)`, `GetChangeVersion(ref dyn)`, `DidChange(ref dyn, v)`, `GetOrderVersion()`, `DidOrderChange(v)` | Type index looked up per archetype with a 1-entry cache (linear scan on miss). | [V] `UE/ArchetypeChunk.cs:115-118,174-183,239-247,581-591`; `UE/ChunkDataUtility.cs:117-134` |
| `chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref dyn, expectedTypeSize)` | Returns `NativeArray<byte>` of `Count * SizeOfs[type]` bytes over the chunk column. **RO handle: no stamp. RW handle: stamps.** Size check only exists in checks builds. Use `NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr` to get a raw pointer. | [V] `UE/ArchetypeChunk.cs:730-744` (the internal `GetDynamicComponentDataPtr` at 746-760 does the same thing) |
| `chunk.GetUntypedBufferAccessor(ref dyn)` returns `UnsafeUntypedBufferAccessor` | `GetUnsafeReadOnlyPtrAndLength(i, out len)`, `GetUnsafePtr(i)`, `GetBufferLength`, `ResizeUninitialized(i, len)` (retains old data). RW handle stamps. | [V] `UE/ArchetypeChunk.cs:817-835`; `UnsafeUntypedBufferAccessor.cs:57-100` |
| `chunk.GetEnableableBits(ref dyn)` returns `v128` | Read-only, no stamp. Bit i = entity i enabled. | [V] `UE/ArchetypeChunk.cs:302-317` |
| `chunk.IsComponentEnabled(ref dyn, i)`, `SetComponentEnabled(ref dyn, i, bool)`, `SetComponentEnabledForAll(ref dyn, bool)`, `GetEnabledMask(ref dyn)` | The set calls stamp the version. `GetEnabledMask` with an RW handle also stamps. | [V] `UE/ArchetypeChunk.cs:291-300,351-362,388-446` |
| `chunk.GetNativeArray(EntityTypeHandle)` / `GetEntityDataPtrRO` | Column 0, no stamp. | [V] `UE/ArchetypeChunk.cs:95-102,669-675` |
| `DynamicSharedComponentTypeHandle`: `GetSharedComponentIndex`, `GetDynamicSharedComponentDataAddress` (unmanaged), `GetSharedComponentDataBoxed` | For shared components such as `UpdateFrame`. | [V] `UE/ArchetypeChunk.cs:486-555` |
| `EntityQuery.ToArchetypeChunkArray(Allocator)` / `ToArchetypeChunkList(NativeList)` / `ToArchetypeChunkListAsync` | Chunk enumeration (filter applied). | [V] `UE/EntityQuery.cs:175-205`; `UE/EntityQueryImpl.cs:345-372` |
| `IJobChunk` + `JobChunkExtensions.ScheduleParallel` | `DynamicComponentTypeHandle` is a plain struct, usable in Burst jobs. The game's own `EntitySerializer` stores one in a job. | [V] `UE/IJobChunk.cs:9`; `Colossal.Core/Colossal.Serialization.Entities/EntitySerializer.cs:55-61,448` |

### 6.2 Per-entity random access

- `EntityManager.GetStorageInfo(Entity)` returns `EntityStorageInfo { ArchetypeChunk Chunk; int IndexInChunk; }` (public).
  `EntityManager.GetChunk(Entity)` and `EntityManager.Exists(Entity)` are also public [V] `UE/EntityManager.cs:1386-1400,2612-2615`;
  `UE/EntityStorageInfo.cs:3-7`.
- In jobs, use `EntityStorageInfoLookup`, obtainable publicly through `SystemState` /
  `InternalCompilerInterface.GetEntityStorageInfoLookup` [V] `Unity.Entities/Unity.Entities.Internal/InternalCompilerInterface.cs:172-184`.
- The `internal` raw helpers (`EntityManager.GetComponentDataRawRO/RW`, `SetComponentDataRaw`, `GetBufferRawRO/RW`,
  `GetBufferLength`, `AddComponentRaw`) each complete the type's dependencies first [V] `UE/EntityManager.cs:2730-2809`.
  Call them only via reflection or a publicized reference. The public alternative is `GetStorageInfo` followed by
  `GetDynamicComponentDataArrayReinterpret<byte>` on that chunk, plus pointer arithmetic `IndexInChunk * SizeInChunk`.
- `EntityManager.GetComponentLookup<T>` is `internal`. The public route for typed lookups is `SystemState.GetComponentLookup`
  [V] `UE/EntityManager.cs:2699-2723`. It is not needed for a generic engine.

### 6.3 The RO/RW rule (critical)

On the **sender**, every handle must be **ReadOnly**. An RW handle used "just to read" stamps change versions. That
corrupts our own detection, and every other system that uses change filters would see spurious changes
[V: ArchetypeChunk.cs:743, ChunkDataUtility.cs:238-244].

On the **receiver**, use RW handles only on chunks we actually write, and call `Update(ref state)` first so that the stamp
is the current version.

---

## 7. Raw component bytes and `Entity` remapping

### 7.1 Sizes

- `IComponentData`: bytes per entity = `TypeInfo.SizeInChunk` (= `ElementSize`) = `archetype->SizeOfs[i]`.
- Buffer: the column stride is `SizeInChunk` (header plus internal capacity). The real payload is `length × ElementSize`,
  read via `UnsafeUntypedBufferAccessor`.

[V] `UE/ArchetypeChunk.cs:739-743,827-834`; `UE/ChunkDataUtility.cs:1272-1292` (uses `ElementSize` for both kinds).

### 7.2 How the engine walks entity fields

`ChunkDataUtility.ClearEntityReferences` is the canonical way to walk `Entity` fields with `TypeInfo` offsets, for both
components and buffers [V] `UE/ChunkDataUtility.cs:1257-1319`:

In outline (`UE/ChunkDataUtility.cs:1301-1319`): the method fetches the type's table of entity offsets, then visits
every element of the array (its address is the base address plus the element size times the index) and, inside each
element, every `Entity` field at the offsets of the table, which it rewrites.

For a buffer, the same loop runs over `BufferHeader.GetElementPointer(header)` with `elementCount = header->Length`
(lines 1276-1285).

### 7.3 Unity's remap containers

`EntityRemapUtility.EntityRemapInfo { int SourceVersion; Entity Target; }` is a dense array indexed by
`source.Index`. `RemapEntity` returns `Null` on a version mismatch [V] `UE/EntityRemapUtility.cs:13-18,77-98`.
`EntityManager.CreateEntityRemapArray(allocator)` sizes it to `HighestEntityIndex()+1` [V] `UE/EntityManager.cs:2401-2404`.
`HighestEntityIndex()` walks all chunks, so it is O(chunks) [V] `UE/EntityManager.cs:858-866`.

The game's save system does the same: `EntitySerializer.FillEntityTableJob` writes `m_EntityTable[entity.Index] =
(serializedIndex, entity.Version)`, and `EntityDeserializer` builds `serializedIndex → Entity`
[V] `Colossal.Core/Colossal.Serialization.Entities/EntitySerializer.cs:223-282`, `EntityDeserializer.cs:95-111,482-538`.

### 7.4 Padding and determinism

A byte-wise `memcmp` sees padding bytes. Padding written by whole-struct copies is copied along. Field-wise writes leave
stale padding. The effect is at worst a false-positive "changed" [I]. It is never a false negative, and it never
corrupts the receiver, which receives exactly what the host has.

Floats are compared bitwise (`-0` vs `+0`, NaN payloads). That is fine for host-authoritative copying.

---

## 8. Enableable components

- CS2 uses 13 enableable types, for example `PropertySeeker`, `HasJobSeeker`, `CarKeeper`, `Locked`, `PrefabData`
  (enableable!), and `CustomMeshColor` (an enableable **buffer**) [V] (grep `IEnableableComponent` in `Game/`;
  e.g. `Game/Game.Agents/PropertySeeker.cs:6`, `Game/Game.Prefabs/PrefabData.cs:7`, `Game/Game.Rendering/CustomMeshColor.cs:8`).
- Enabled state is one bit per entity per chunk (`v128`, because capacity is at most 128). Toggling it bumps the type's
  change version [V] (§3.1).
- Replicate it as part of the type's per-entity record: one bit, from `GetEnableableBits` on the sender. On the receiver,
  apply with `SetComponentEnabled(ref dyn, i, v)` or `EntityManager.SetComponentEnabled(Entity, ComponentType, bool)`
  [V] `UE/EntityManager.cs:1963-2004`.
- Queries skip disabled entities by default. A replicator query should use `EntityQueryOptions.IgnoreComponentEnabledState`
  [V] `UE/EntityQueryOptions.cs:14` together with `IncludeDisabledEntities` where relevant, so that disabled components
  still replicate.

---

## 9. Structural changes, sync points, ECB

- **Every `EntityManager` structural change completes *all* jobs.** `BeginStructuralChanges` calls `BeforeStructuralChange`,
  which calls `CompleteAllJobsAndInvalidateArrays`, unless the caller is already in an exclusive transaction
  [V] `UE/EntityDataAccess.cs:391-407`. Additive changes (create, instantiate) also call
  `CompleteAllJobsAndCheckDeallocateAndThrow` [V] `UE/EntityDataAccess.cs:411-443`.
  - **Cost:** a sync point stalls the worker threads. CS2 overlaps many long sim and render jobs, so each extra sync point
    can cost milliseconds of lost parallelism [I].
  - **Mitigation:** do *all* receiver structural changes in one batch per network tick.
- **Batch APIs:** `CreateEntity(EntityArchetype, int count, Allocator)` returns the entities (`UE/EntityManager.cs:2043-2070`);
  `DestroyEntity(NativeArray<Entity>)` (2097); `AddComponent(EntityQuery, ComponentType/ComponentTypeSet)` (1480-1503);
  `AddComponent(NativeArray<Entity>, ...)` (1544, 1575) [V].
- **In place vs copy:**
  - `AddComponent(EntityQuery, T)` re-files whole chunks *in place* only if `AreLayoutCompatible`: same chunk capacity
    and identical non-zero-sized types. That holds only for zero-sized tags and shared components
    [V] `UE/EntityComponentStore.cs:3380-3395`, `UE/ChunkDataUtility.cs:630-648`.
  - Any data-carrying component forces a full per-entity copy into new chunks, which gets new SequenceNumbers and has all
    versions stamped [V] (§3.3).
- **ECB:**
  - The public typed API covers `CreateEntity`, `Instantiate`, `DestroyEntity`, `AddComponent(ComponentType)`, `SetComponent<T>`,
    `SetComponentEnabled(ComponentType, bool)` and others [V] `UE/EntityCommandBuffer.cs:1186-1357`.
  - The raw variants `UnsafeAddComponent` / `UnsafeSetComponent(Entity, TypeIndex, int size, void*)` are `internal`
    [V] `UE/EntityCommandBuffer.cs:1257,1337,3964,3998`. Playback calls the internal `SetComponentDataRaw` [V] `UE/EntityCommandBuffer.cs:484`.
  - So a generic receiver should **not** go through ECB for data. Use ECB or `EntityManager` only for structure, then write
    bytes directly into chunks.
- **Game barriers:** `ModificationBarrier1..5`, `2B`, `4B` and `ModificationEndBarrier` are `SafeCommandBufferSystem :
  EntityCommandBufferSystem`. They forbid `CreateCommandBuffer()` after they have updated in a frame
  [V] `Game/Game/SafeCommandBufferSystem.cs:7-30`; `Game/Game.Common/SystemOrder.cs:78-93`.
- **Cleanup components** (`ICleanupComponentData`): when an entity that carries one is destroyed, it is moved to a "cleanup
  residue" archetype instead of being deallocated, and the `Entity` stays valid [V] `UE/EntityComponentStore.cs:4386-4424`,
  `UE/ICleanupComponentData.cs:6`. This is the idiomatic, exact destroy detector. The cost is a structural add on every
  tracked entity (see §10.3).

---

## 10. Entity identity and network ids

### 10.1 Facts [V]

- `struct Entity { int Index; int Version; }`. `CompareTo` orders by Index only [V] `UE/Entity.cs:9-33`.
- Allocator: up to 16384 data blocks × 8192 slots. Allocation scans blocks in order and takes the **first free bit**.
  `Version = ++versions[slot]` [V] `UE/EntityComponentStore.cs:720-804`.
- Destroy: `versions[slot]++` and the bit is cleared [V] `UE/EntityComponentStore.cs:857-875`.
- Alive means `(Version & 1) != 0 && versions[slot] == Version` [V] `UE/EntityComponentStore.cs:660-674`.
- **Consequence:** indices are reused immediately. Equal `Index` values on host and client mean nothing. `(Index, Version)` is
  unique only within one process's history.

### 10.2 Detecting creation and destruction

| Method | Cost | Verdict |
|---|---|---|
| `EntityManager.GetCreatedAndDestroyedEntities(state, created, destroyed)` (marked `[Obsolete]`) | Copies **every entity of every archetype** into a list, then sorts and merges: O(N log N) per call over the whole world | [V] `UE/EntityManager.cs:2179-2189`; `UE/EntityComponentStore.cs:487-540,5496-5531`. Too expensive per tick at city scale [I: about 1-3M entities means tens of ms]. Fine as a debug or consistency check. |
| Game tags `Created` / `Deleted` | Free, but only visible between modification and `Cleanup`, and only set by game code paths | Use as hints only |
| Cleanup component on every replicated entity | Exact, but a structural add per entity, which changes archetypes and doubles the archetype count | Possible, see §10.3 |
| **Per-chunk entity snapshot + order version** (recommended) | Re-scan only chunks whose `GetOrderVersion()` differs from the snapshot, or whose SequenceNumber is new or gone. Compare their `Entity` column against the side table. | Exact for the replicated set; cost proportional to churn |

### 10.3 Where the NetId lives

**Option A: component `NetId : IComponentData`.**
- Every replicated archetype gets a new data column. Adding it to existing game entities is a full copy of all their chunk
  data (§9).
- It would be stripped from saves. The serializer strips types with no `ISerializable` / `IEmptySerializable`, see
  `EntitySerializer.FillArchetypeMapJob` [V] `Colossal.Core/.../EntitySerializer.cs:96-136`,
  `ComponentSerializerLibrary.cs:32-86`.
- Game code that creates entities from stored archetypes would create them *without* the component, so they need a
  catch-up pass.
- It changes chunk capacities slightly [I].

**Option B: side table (recommended).**
- `NativeArray<uint> netIdByEntityIndex` and `NativeArray<int> versionByEntityIndex`, both dense and grown on demand. Plus
  `NativeArray<Entity> entityByNetSlot` and a free-list of slots.
- No structural changes, no archetype or save pollution, maximal compatibility with the game and other mods.
- Exactly the pattern of `EntityRemapInfo` and of the game's own entity table (§7.3).
- Lookup is O(1) with a version check. Memory is about 8 B × the highest index (1M entities is about 8 MB).

**Option C: zero-sized tag `Replicated` plus side table.**
- Tags are layout-compatible, so they are added in place [V §9]. Useful if a query must match "replicated entities only".
  Not required.

### 10.4 Wire encoding of `Entity` fields

Encode each `Entity` field as a tagged id:

| Tag | Meaning |
|---|---|
| `Null` | `Entity.Null` |
| `Net(slot)` | A replicated entity |
| `Prefab(stableId)` | A prefab entity |
| `Local` | Not replicated; written as Null on the receiver, plus a diagnostic counter |

Prefab entities come from asset loading on each machine. Most game state points at them, e.g. `PrefabRef.m_Prefab`
[V] `Game/Game.Prefabs/PrefabRef.cs:6-8`. Map them through `PrefabSystem` identity (`TryGetPrefab(PrefabID, ...)`,
`GetEntity(PrefabBase)`) [V] `Game/Game.Prefabs/PrefabSystem.cs:705-710,939`. Build a `prefabEntity.Index → stable prefab
id` dense table once after load [I].

### 10.5 Bootstrap at join time

Option [I]: reuse the save pipeline's dense table (host: `EntitySerializer` entity table; client: `EntityDeserializer.m_EntityTable`)
so that `NetId = serialized index` on both sides. Both tables are private, so this requires reflection or Harmony, and it
must be validated in a separate join-flow study.

The alternative is for the host to send an explicit `(NetId, archetype StableHash, component bytes)` snapshot for the
replicated set right after the client loads.

---

## 11. Reference implementation inside Unity: `EntityManagerDiffer` / `EntityDiffer`

- Public: `EntityManagerDiffer(EntityManager src, allocator, EntityQueryDesc = null)` and `GetChanges(EntityManagerDifferOptions, allocator)`.
  By default it tracks entities with `EntityGuid` (16 bytes) against a **shadow world**
  [V] `UE/EntityManagerDiffer.cs:6-56`, `UE/EntityGuid.cs:8-30`, `UE/EntityManagerDifferOptions.cs:5-16`.
- Algorithm:
  1. Match chunks by `SequenceNumber` [V] `UE/EntityDiffer.cs:102-190`.
  2. For matched entities, skip a component if the source chunk version **equals** the shadow chunk version
     (`CanCompareChunkVersions`) [V] `UE/EntityDiffer.cs:991-999`.
  3. Otherwise `UnsafeUtility.MemCmp` the bytes. When referential equality is on, compare with `Entity` and blob fields
     masked out [V] `UE/EntityDiffer.cs:1231-1270`.
- **Notable [V]:** in this build `GatherArchetypeChunkChanges.ChunksAreDifferent` is `return true;`
  (`UE/EntityDiffer.cs:192-195`), so every matched chunk is treated as cloned. [I] Either Colossal patched it or a
  compiler simplification occurred; in any case the shadow-world differ here does more work than upstream.
- **Verdict:**
  - The design is right: version-equality prefilter, then memcmp, with entity identity by GUID.
  - Using `EntityManagerDiffer` itself is impractical. It needs `EntityGuid` on every entity (a structural add), keeps a full
    shadow *world* (duplicates all chunks, including unreplicated columns), produces managed-heavy `EntityChanges`, and
    `ChunksAreDifferent` forces full work.
  - We replicate its *ideas* in a leaner, type-selective form.

---

## 12. Recipe: generic "replicate selected component types for selected archetypes" engine

### 12.1 Configuration (both peers, verified in the handshake)

```
ReplicationSet {
  RepType[] types;          // index = wire type id (ushort), sorted by StableTypeHash
  QueryDesc[] scopes;       // which archetypes: All/Any/None (e.g. None = Temp, Deleted)
}
RepType {
  ulong stableHash; TypeIndex local;               // local via GetTypeIndexFromStableTypeHash
  int elemSize;                                     // TypeInfo.ElementSize
  bool isBuffer, isEnableable;
  int* entityOffsets; int entityOffsetCount;        // TypeManager.GetEntityOffsets
  DynamicComponentTypeHandle ro, rw;                // ro = ComponentType.ReadOnly(local)
  float priority; int maxHz;                        // scheduling knobs
}
```

Handshake: exchange `(stableHash, SizeInChunk, ElementSize, EntityOffsetCount, IsBuffer, IsEnableable)` for every type,
and refuse on mismatch. Reject ineligible types (§5).

### 12.2 Sender data structures (host)

- `ChunkRecord` keyed by `ArchetypeChunk.SequenceNumber`: `{ EntityArchetype arch; uint orderVersion; uint[] typeVersions (per RepType, 0 if absent); NativeArray<Entity> entitiesSnapshot (≤128) }`.
- `IdTable` (side table): `netSlotByEntityIndex[int]`, `versionByEntityIndex[int]`, `entityByNetSlot[]`, plus a free-list.
- `Shadow` per RepType: for components, `NativeList<byte>` laid out by **net slot**
  (`slot * elemSize`) plus a "present" bitset. For buffers, per-slot `(offset, length)` into an arena, with arena
  compaction.
- Per-peer state:
  - `ackedTick`, plus a per-slot "dirty since ack" bitset per type, *or* a simpler reliable-ordered channel. With a
    reliable stream the shadow *is* the peer's state; peers that join late get a snapshot first, then the stream.

### 12.3 Sender per-tick algorithm

Run in a `SystemBase` placed after `ModificationEnd` and before `Cleanup` [I], or right after the last sim system of the
tick.

```
0. if all GetCombinedComponentOrderVersion(scopes) unchanged AND no type-version work → still need step 2 (values),
   so this is only a micro-optimization for structure.
1. chunks = scopeQuery.ToArchetypeChunkListAsync(...)   // query options: IgnoreComponentEnabledState, IncludeDisabledEntities as needed
   scopeQuery.CompleteDependency()  — or schedule the jobs below with Dependency (preferred)
2. Burst IJobChunk / IJobParallelForDefer over chunks (parallel):
   rec = records[seq]  (new chunk ⇒ rec = null)
   orderChanged = rec == null || chunk.GetOrderVersion() != rec.orderVersion || chunk.Archetype != rec.arch
   if orderChanged:
       diff chunk Entity column vs rec.entitiesSnapshot → emit SPAWN candidates (entities not in IdTable)
       (DESPAWN is detected in step 3 from removed records / missing entities)
   for each RepType t present in archetype:
       v = chunk.GetChangeVersion(ref t.ro)
       if !orderChanged && rec != null && v == rec.typeVersions[t]: continue      // equality test, wrap-safe
       base = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref t.ro, t.elemSize).GetUnsafeReadOnlyPtr()
       for i in 0..chunk.Count:
           slot = IdTable.slotOf(entities[i])                 // O(1) dense lookup + version check
           src  = base + i*t.elemSize
           if !shadow.present(slot) || memcmp(src, shadow[slot], elemSize) != 0 || enabledBitChanged:
               emit DELTA(slot, t) ; memcpy(shadow[slot], src)   // stage per worker, merge later
       (buffers: accessor.GetUnsafeReadOnlyPtrAndLength(i, out len); compare len and bytes vs arena)
   rec.update(orderVersion, typeVersions, entitiesSnapshot)
3. Main thread: chunks whose SequenceNumber vanished, and snapshot entities no longer present anywhere
   (check IdTable entries touched this tick via EntityManager.Exists or
   storageInfoLookup.Exists) ⇒ DESPAWN(slot), free slot after the peer acks.
   (Entities that merely *moved* chunk show up as "missing from old chunk" AND "present in new chunk",
   so the slot survives. Resolve by checking Exists(entity) before despawning.)
4. Serialize (Burst): for each DELTA: [slot varint][type u16][enabled bit][payload], where
   payload = bytes with each Entity field (at entityOffsets) rewritten to a tagged NetRef (§10.4).
   Optional: XOR against the previous shadow value before overwriting, then entropy-code/compress (zeros dominate).
5. Every N ticks, convergence check: per type, a 64-bit xxHash3 over (slot-ordered) shadow bytes for a rotating
   slice of slots (Unity.Collections xxHash3.Hash64, Unity.Collections/Unity.Collections/xxHash3.cs:540-560).
   The client hashes its live values for the same slice and requests a resync of the slice on mismatch.
```

Notes:
- Equality on per-chunk versions (`v == rec.typeVersions[t]`) replaces `DidChange` against a global baseline. It is
  correct across wrap-around and needs no per-peer version bookkeeping. Unity's differ uses the same idea [V §11].
- New chunks (unknown SequenceNumber) and in-place archetype switches (same SequenceNumber, different `Archetype`) take the
  "orderChanged" path. Freshly allocated chunks have all versions stamped, so they are scanned fully [V §3.3].
- **Spawn ordering:** emit SPAWN records (with archetype `StableHash` and full initial values of all replicated types)
  *before* deltas that reference them. A DELTA whose payload references a slot not yet spawned on the peer is still valid,
  because the receiver defers unresolved refs (see 12.4).

### 12.4 Receiver apply (client)

```
Per network tick (one main-thread block, e.g. own SystemBase before Modification1, or after ModificationEnd):
A. Structural batch (single sync point):
   - group SPAWNs by archetype StableHash → EntityArchetype via CreateArchetype(types from GetTypeIndexFromStableTypeHash)
     cache; ents = EntityManager.CreateEntity(arch, n, Allocator.Temp); bind slots.
   - archetype changes for existing entities (component add/remove on host): AddComponent/RemoveComponent with
     NativeArray<Entity> batches per (from,to) pair.
   - DESPAWNs: DestroyEntity(NativeArray<Entity>).
B. Data (no structural changes; schedule as a Burst job or run main-thread after query.CompleteDependency()):
   for each DELTA(slot, t, enabled, payload):
       e = entityBySlot[slot]; info = storageInfoLookup[e]              // chunk + IndexInChunk
       rw.Update(ref state); base = chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref t.rw, elemSize) ptr
           (cache per chunk: call once per (chunk,type) per tick → one version stamp per chunk/type)
       dst = base + info.IndexInChunk * elemSize
       copy payload → dst, then for each entity offset: decode NetRef → local Entity
           Net(slot)  → entityBySlot[slot] (if unbound: write Entity.Null and push (e,t,offset,slot) to pending list)
           Prefab(id) → prefabEntityById[id]
       buffers: acc = chunk.GetUntypedBufferAccessor(ref t.rw); acc.ResizeUninitialized(i, len); memcpy; remap per element
       enableable: chunk.SetComponentEnabled(ref t.rw, i, enabled)
   resolve pending list for slots bound this tick.
C. Optional: add game tags (Updated/BatchesUpdated) to touched entities so dependent game systems react (design decision,
   see doc on reactive systems) - that is itself a structural change (tag ⇒ entity moves chunk unless whole-chunk).
```

The receiver must never let local game systems write the replicated types for replicated entities. Otherwise the host
and client diverge between deltas. That means disabling or patching the authoritative-state systems on clients, which is
outside this document. The convergence check in step 12.3.5 detects violations.

### 12.5 Archetype and structure replication

- An entity's archetype is described by its `EntityArchetype.StableHash` plus the list of member `StableTypeHash` values,
  sent once per archetype per session (dictionary-coded).
- Zero-sized tags and non-replicated data types are part of that list. On the receiver, the non-replicated data
  components are default-initialized and computed locally [I].
- Archetype change detection on the sender: in step 2, an entity found in a chunk whose `Archetype` differs from the
  entity's last-known archetype means a STRUCTURE(slot, newArchetypeId) record. Ignore transient game tags
  (Created/Updated/Applied/BatchesUpdated/EffectsUpdated/PathfindUpdated) in the structure signature, otherwise every
  frame produces structure churn [I].

### 12.6 Shared components

- Shared components (e.g. `UpdateFrame`) are per chunk. Read `GetSharedComponentIndex` / `GetDynamicSharedComponentDataAddress`
  (unmanaged) [V] `UE/ArchetypeChunk.cs:486-547`.
- Replicate them as part of the structure record when needed. `UpdateFrame` assignment can usually be left to the client
  [I, verify per system].

---

## 13. Cost estimates

Assumptions [I]:
- N = 1,000,000 replicated entities in C ≈ 25,000 chunks (40 per chunk on average; CS2 archetypes are wide).
- K = 8 replicated types averaging 24 B.
- Payload S = N·K·24 ≈ 192 MB if *every* type were on every entity. Realistically about 30-100 MB, because types are
  archetype-specific.
- Sim tick ≈ 60 Hz at 1×. Burst numbers for one core at about 3-4 GHz. Mono (non-Burst) is about 5-15× slower.

| Step | Work | Estimate |
|---|---|---|
| Chunk list (`ToArchetypeChunkList`) | C items | 0.1-0.3 ms |
| Version checks | C × K × ~5 ns (lookup cache plus array read) | ≈1 ms; ≈0.15 ms on 8 workers. **Mono: 10-15 ms, so Burst is mandatory.** |
| False-positive scan per tick | UpdateFrame slicing touches ≈1/16 of sim chunks, so ≈1.5k chunks × 40 × K compares | ≈0.5M compares of 24 B, about 1.5-3 ms single-threaded, ≈0.3 ms parallel |
| Order-changed chunks (tag churn, spawn/despawn) | Proportional to game edits (Updated tags) | Usually small, 0.1-1 ms; bursts on large tool applies |
| Shadow memory | Equal to the replicated payload | 30-100 MB, plus the side table (≈8 MB per 1M index range) and chunk records (≈C × (32 + 4K + 128·8) ≈ 26 MB worst case; the entity snapshots can be dropped by deriving them from the side table) |
| Serialization | Proportional to changed records | ≈10-20 ns per record in Burst |
| Receiver apply | Per record: slot lookup + storage-info lookup + memcpy + remap ≈ 20-40 ns; per (chunk, type): 1 handle reinterpret | 100k records per tick is about 3 ms |
| Receiver structural batch | One sync point plus O(n) allocation and copy | Sync stall 0.5-5 ms depending on in-flight jobs; create ≈50-100 ns per entity |
| One-time: adding a data `NetId` component to all game entities (Option A) | Full per-entity copy of every column of affected chunks | Hundreds of ms for a large city, and it repeats for new archetypes. **Avoid it (use Option B).** |
| `GetCreatedAndDestroyedEntities` per call | Copy plus sort of all entities | Tens of ms for 1-3M entities. Debug only. |

Bandwidth reality check [I]: 20k changed (slot, type) records per tick × ~30 B is about 600 KB per tick, or about 36 MB/s at
60 Hz. That is far too much for a Steam P2P link (an order of 0.1-1 MB/s per peer).

So the engine must:
- (a) replicate only authoritative, low-frequency state, never per-frame motion (vehicles and citizens moving should not be
  raw-replicated);
- (b) cap the rate per type (`maxHz`) and prioritize;
- (c) XOR-delta plus compress.

Compression note: `Unity.Core.Compression.CodecService` (LZ4 via P/Invoke to `liblz4`) is `internal`
[V] `Unity.Entities/Unity.Core.Compression/CodecService.cs:7,53-54`, and no `liblz4` was found in
`Cities2_Data\Plugins\x86_64` (listing checked), so plan for a managed compressor [I].

---

## 14. Risks and pitfalls (summary)

1. **RW-access stamping:** an RW handle on the sender corrupts change detection for everyone (§6.3). Code review rule:
   sender handles are created from `ComponentType.ReadOnly(...)` only.
2. **Chunk churn:** entities move between chunks every frame because of tag add/remove. A per-chunk positional shadow
   breaks, so the shadow must be per entity (§3.3, §4).
3. **No safety system:** races with game jobs are silent. Always use `CompleteDependency` or a correct `Dependency`
   chain (§1).
4. **`Entity` refs invisible to `EntityOffsets`** (inside `fixed` buffers, pointers or opaque `FixedBytes`) would be copied
   raw and point at garbage on the receiver. Audit the replicated types at startup (§5).
5. **Prefab entities differ per machine.** Every `Entity` field pointing to a prefab needs prefab-id remapping (§10.4).
6. **Layout equality relies on identical game version and mod set.** Enforce it in the handshake with `StableTypeHash` and
   sizes (§5).
7. **Receiver-local systems overwriting replicated state** cause divergence between deltas. This requires a
   system-gating design, out of scope here. Detect it with hash checks (§12.3.5).
8. **Structural sync points on the receiver.** Keep them to one per tick, and avoid single-entity `EntityManager` calls
   in loops (§9).
9. **GlobalSystemVersion wrap** after many hours makes `DidChange`-style comparisons against old baselines wrong. Use
   per-chunk version equality (§3.1).
10. **`ChunksAreDifferent` returns true** in this build's `EntityDiffer`. Do not rely on Unity's differ for performance (§11).

---

## 15. Open questions (for follow-up research)

- Exact Entities patch version. Only matters for API details; the 1.0.x surface was verified directly.
- Which CS2 components are authoritative state and which are derived and recomputed by game systems from authoritative
  inputs (for example `Game.Net` geometry vs. `Edge`/`Node`). This decides the replicated set and whether the receiver adds
  `Updated` tags.
- Whether the join-time "save index = NetId" bootstrap (§10.5) can be made reliable via the private entity tables in
  `EntitySerializer` and `EntityDeserializer`.
- Real numbers: entity, chunk and archetype counts in large saves, and the per-tick fraction of stamped chunks per
  candidate type. Measure in game with a diagnostic system before fixing the budget numbers in §13.
- Whether any vanilla replicated-candidate type hides `Entity` values inside `FixedList`/`fixed` storage (risk 4).
