# 07 - Terrain and Water Systems (research for Skyline Together)

Scope: how Cities: Skylines II stores, modifies, saves and reads terrain heights, water, snow and the
CPU "cell maps", and what that means for a host-authoritative co-op mod that must converge to an identical state on every machine.

All paths are relative to `<audit>\decomp`. Line numbers refer to the decompiled files.
Labels used below:
- **[V]** verified: read in the decompiled code.
- **[I]** inference: reasoning from the code, not directly provable from C# (most often because the logic lives in compute or raster shaders whose source is not in the decompiled assemblies).

---

## 0. TL;DR for the design

1. **Terrain state = one 4096x4096 R16_UNorm GPU texture** (`TerrainSystem.m_Heightmap`) plus a few floats. Roads, building lots and terrain areas are *not* baked into it. They are rasterised every time into a derived cascade texture, and the CPU height array is read back from that cascade. Only the base heightmap is saved. [V]
2. **Terraforming is not deterministic as written.** The tool reads `Time.deltaTime` when it builds the stroke, the system reads `Time.unscaledDeltaTime` when it applies it, the number of stamps depends on how far the mouse moved in one frame, and the result is computed by a GPU compute shader (`AdjustTerrain`). Replaying *mouse input* will diverge. Replaying the exact `Brush` structs with a pinned delta should be very close, but bit-exact results across GPU vendors are not guaranteed. [V for the timing inputs, I for GPU bit-exactness]
3. **Recommended terrain sync:** the host owns the heightmap. Replay brush structs on clients for instant feedback, then send **authoritative dirty-rect tiles of `m_Heightmap`** (ushort, compressed) once a stroke settles. Verify with tile hashes. Clients write tiles with `Graphics.CopyTexture` and then call the public invalidation hooks (`OnAreaChanged`, `WaterSystem.TerrainWillChangeFromBrush`, `UpdateMinMax`).
4. **Water simulation can never be deterministic across machines.** It runs once per *rendered frame* from `RenderPipelineManager.beginFrameRendering`, not per simulation tick. Its timestep comes from `Time.smoothDeltaTime` with exponential smoothing. Which tiles are active comes from async GPU readbacks. The maths is float32 compute on the GPU. [V]
5. **Recommended water sync:** gameplay that depends on water (pumping, floods, water damage, soil water, fish, placement validation) is computed only on the host and replicated through ECS. Clients run water locally for visuals only, and the host corrects them with **periodic tiled snapshots of `WaterSystem.WaterTexture`** (a public `RenderTexture`, 2048x2048 RGBA32F). Water sources (`Game.Simulation.WaterSourceData` plus `Transform`), sea level, legacy/new mode and simulation parameters must be replicated exactly.
6. **Cell maps** (pollution, groundwater, natural resources, land value and so on) are CPU `NativeArray`s that drive gameplay. Several are seeded from `DateTime.Now.Ticks`, so they cannot be lockstepped. They are small (64² to 256²) and update rarely. Send host snapshots so client info views and tooltips are correct. **Snow** is GPU, tick-driven and visual only. Its sync is optional.

---

## 1. TerrainSystem: storage

File: `Game/Game.Simulation/TerrainSystem.cs` (5212 lines). Class `Game.Simulation.TerrainSystem : GameSystemBase, IDefaultSerializable, ISerializable` (line 40).

### 1.1 Constants and dimensions [V]

| Item | Value | Where |
|---|---|---|
| `kDefaultHeightmapWidth/Height` | 4096 | 2292-2294 |
| `kDownScaledHeightmapScale` | 4 (backdrop CPU copy is 1024²) | 2296 |
| `kDefaultMapSize` (playable) | 14336 m x 14336 m | 2298 |
| `kDefaultWorldSize` (with backdrop) | 4 x playable = 57344 m | 2302 |
| `kDefaultHeightScaleOffset` | (4096, 0): height range 0..4096 m | 2306 |
| Default fill value | 8191 of 65535 (~512 m) | `SetDefaultHeights` 3510-3519 |
| Brush speeds | Soften 1000, Shift 2000, Level/Slope 4000 | 2286-2290, `GetTerrainAdjustmentSpeed` 2599-2607 |

Horizontal resolution is 14336 / 4096 = **3.5 m per texel**. Vertical resolution is 4096 / 65535 ≈ **0.0625 m per LSB** [I, arithmetic].

### 1.2 Textures and CPU arrays [V]

| Field | Type / format | Role |
|---|---|---|
| `m_Heightmap` (2320) | `RenderTexture` R16_UNorm, `enableRandomWrite`, "TerrainHeights" (`SetHeightmap` 3258-3282) | **Base, authoritative, saved terrain.** Written only by `SetHeightmap` (load/replace) and `ApplyToTerrain` (brushes). Grep shows no other writer (3277, 3976). |
| `worldHeightmap` (2510) / `m_WorldMapEditable` (2328) | R16 texture, same pixel size as heightmap, covering 4x area | Backdrop ("world map"). Editable only in the editor: `SetWorldHeightmap` creates `m_WorldMapEditable` only when `isEditor` (3284-3316). `ApplyToTerrain` returns early when the target is null (3743), so in-game brushes touch only `m_Heightmap`. |
| `m_HeightmapCascade` (2322) | R16 `Tex2DArray`, 4 slices, not random-write ("TerrainHeightsCascade", 3354-3364) | **Derived** composite: base heightmap blit + building lots + net lanes (flatten/clip) + terrain areas, rasterised per cascade (`RenderCascade` 4886-4978, `DrawHeightAdjustments` 4780-4863). Slice `baseLod` always covers exactly the playable area (3414). Slices above `baseLod` follow the camera (4337-4403). |
| `m_HeightmapObjectsLayer` (2324) | R16 Tex2D | Derived "raised lanes" layer used by water (`DrawHeightLaneRaised`, called only when new water sources are in use, 4952-4955). |
| `m_DownscaledHeightmap` (2330) | R16, 1/4 size | Derived from the cascade by the `DownsampleTerrain` kernel (3244-3256). Exists only with a backdrop. |
| `m_CPUHeights` (2310) | `NativeArray<ushort>` | **CPU copy read back from `m_HeightmapCascade` slice `baseLod`** (3624, 3672). It therefore includes roads and lots, not just the base terrain. |
| `m_CPUHeightsDownscaled` (2314) | `NativeArray<ushort>` | CPU copy of the downscaled map (backdrop sampling). |

### 1.3 GetHeightData [V]

`GetHeightData(bool waitForPending = false)` (2615-2636) returns a `TerrainHeightData` struct (`Game/Game.Simulation/TerrainHeightData.cs` 6-34) that wraps `m_CPUHeights`, resolution `(w, 65536, h)`, scale and offset. With `waitForPending` it blocks on the GPU readback. Readers must register job handles with `AddCPUHeightReader` / `AddCPUDownsampleHeightReader` (2638-2646). `OnUpdate` completes those handles before anything is mutated (3681-3700).

CPU heights are refreshed **asynchronously**:
- `TriggerAsyncChange` (3648-3656) leads to `OnHeightsChanged` (3663-3678), which requests an `AsyncGPUReadback` of the dirty rectangle `m_LastWrite` from the cascade.
- `UpdateGPUReadback` (3597-3646) copies the data in once it is done (`WriteCPUHeights` 3587-3595), then calls `GroundHeightSystem.AfterReadHeights`.
- The latency depends on frame timing. `AsyncGPUReadbackHelper` is in `Colossal.Core/Colossal.Rendering/AsyncGPUReadbackHelper.cs` 9-100.

---

## 2. Terrain modification pipeline (brushes)

### 2.1 Data types [V]
- `Game.Tools.Brush` (`Game/Game.Tools/Brush.cs` 6-23): `m_Tool` (terraforming prefab entity), `m_Position`, `m_Target`, `m_Start`, `m_Angle`, `m_Size`, `m_Strength`, `m_Opacity`.
- `Game.Tools.BrushDefinition` (`Game/Game.Tools/BrushDefinition.cs` 7-24): `m_Tool`, `m_Line` (`Line3.Segment`), `m_Angle`, `m_Size`, `m_Strength`, `m_Time`, `m_Target`, `m_Start`.
- `Game.Prefabs.TerraformingData { m_Type, m_Target }`. `TerraformingType` = Shift, Level, Soften, Slope. `TerraformingTarget` = None, Height, Ore, Oil, FertileLand, GroundWater, Material.
- `TerraformingBrushData.m_ToolStrength` overrides the speed constant (`ApplyBrush` 3971-3975).
- `BrushData { m_Archetype, m_Priority, m_Resolution }` with buffer `BrushCell { m_Opacity }` (CPU mask). `BrushPrefab.m_Texture` is a `Texture2D` (GPU mask).

### 2.2 Frame-by-frame flow [V]

1. **`TerrainToolSystem`** (`Game/Game.Tools/TerrainToolSystem.cs`), tool update phase. While the mouse is held, every frame:
   - `UpdateDefinitions` (690-724) schedules `CreateDefinitionsJob` (68-98).
   - That job creates an entity with `CreationDefinition`, a `BrushDefinition` and `Updated`.
   - `m_Line` runs from the previous raycast point to the current one (80-87).
   - **`m_Time = UnityEngine.Time.deltaTime`** (709).
   - Strength is scaled by brush size for Shift and Soften (695-701).
   - Rotation uses `Time.deltaTime` too (`RotateBrush` 658-662).
2. **`GenerateBrushesSystem`** (`Game/Game.Tools/GenerateBrushesSystem.cs`), phase `Modification1` (`Game/Game.Common/SystemOrder.cs` 101):
   - The line is split into `num3 = 1 + floor(len / (size*0.25))` stamps (80).
   - Strength is `(s⁴(1-t) + s²t)·sign(s)` with `t = m_Time/num3` (78-95). This depends on frame time.
   - `m_Opacity = 1/num3` (96).
   - One **`Temp`** brush entity is created per stamp (99-109).
3. **`ToolOutputSystem`** (`Game/Game.Tools/ToolOutputSystem.cs` 20-31) runs the `ApplyTool` phase only when `applyMode == Apply`.
4. **`ApplyBrushesSystem`** (`Game/Game.Tools/ApplyBrushesSystem.cs`), phase `ApplyTool` (SystemOrder 719). Query: `Temp` + `Brush` (239). For each brush it dispatches on `TerraformingData.m_Target` (267-290):
   - **Height**: `ApplyHeight` (302-314), which calls `TerrainSystem.ApplyBrush(type, bounds, brush, BrushPrefab.m_Texture)`. Level/Slope with negative strength is ignored. Negative Soften becomes `abs*2`.
   - **Ore/Oil/FertileLand**: CPU Burst job `ApplyCellMapBrushJob` on `NaturalResourceSystem` (88-181, 316-342).
   - **GroundWater**: the same job on `GroundWaterSystem` (`GroundWaterModifier` 71-85).
   - **Material**: `TerrainMaterialSystem.ApplyBrush` (`Game/Game.Rendering/TerrainMaterialSystem.cs` 815-829). This is a GPU splatmap paint into `m_UserSplatmap` / `m_UserSplatmapWorld` (`ApplyToSplatmap` 831-883).
   - Each brush entity then gets `Applied` + `Deleted` (291). **Brush entities are transient. They are never saved, and there is no persistent stroke record.**
5. **`TerrainSystem.ApplyBrush`** (3968-3980):
   - Calls `WaterSystem.TerrainWillChangeFromBrush(area)`.
   - Then `ApplyToTerrain(m_Heightmap, null, UnityEngine.Time.unscaledDeltaTime, ...)`, the same for `m_WorldMapEditable` (editor only), then `UpdateMinMax`, then `TriggerAsyncChange`.
6. **`ApplyToTerrain`** (3741-3962), on the GPU:
   - Computes the strength `x2 = clamp(delta, 0.05, 1) * strength * toolBaseStrength / heightScale` (3774). Very small strengths go through a `pow(...,0.055)` curve (3765-3773).
   - Computes the texel rectangle (3794-3814), grows `m_LastWrite` (3860-3870), and sets compute parameters.
   - Dispatches the `AdjustTerrain` compute kernel: ShiftTerrain, LevelTerrain, SlopeTerrain, or SmoothTerrain preceded by HorzBlur/VertBlur passes with temporary RTs (3876-3955).
   - The shader also receives `_BrushTexture`, `_WorldTexture` and **`_WaterTexture` = `m_WaterSystem.WaterTexture`** (3945-3947). Its use cannot be checked because the shader source is not available [I]. In-game, `_ClampArea` keeps a 2-texel border fixed when a world map exists (3941-3942).
   - `Brush.m_Opacity` is **not used** by `ApplyToTerrain` or by `ApplyCellMapBrushJob`, so each stamp applies full strength [V, by reading both].
7. **Cascade refresh**: `OnAreaChanged(area)` (called at 3779) grows `m_UpdateArea` and pushes the area to `GroundHeightSystem.GetUpdateBuffer()` (5149-5161). In `ModificationEnd`, `TerrainSystem.OnUpdate` then runs `UpdateCascades`, which works out viewports (4074-4440). In `PreCulling`, `TerrainRenderSystem` (`Game/Game.Rendering/TerrainRenderSystem.cs` 179) calls `RenderCascades` (4442-4514). That re-blits the heightmap, rasterises lots, lanes and areas, and calls `TriggerAsyncChange` again for the base slice (4496-4511).
8. **Objects follow the ground**: `GroundHeightSystem` (`Game/Game.Simulation/GroundHeightSystem.cs`, phase `Modification2`, SystemOrder 117) moves its update bounds through New → Pending → Reading → Ready as GPU work and readback finish (1071-1155). In `OnUpdate` (1203-1286), `UpdateHeightsJob` (587+) **rewrites `Transform`, `Curve` and `Node` heights** of objects, nets and areas inside the changed bounds, using CPU terrain *and water surface* data (`ObjectUtils.AdjustPosition(..., ref m_TerrainHeightData, ref m_WaterSurfaceData, ...)`, around line 609). An entity is rewritten if its height moves by ≥ 0.01 m (610), and it is tagged `Updated`.

### 2.3 Undo [V]
There is no terraform undo. A grep for "undo" in `Game.Tools`, `Game.Simulation` and the editor UI finds only net/area/object/route tools. In `NetToolSystem` it is `"Undo Net Control Point"`, around line 5532. Every brush stamp is a destructive GPU write.

### 2.4 Is brush application deterministic?
- **Inputs vary with frame timing [V]:** `Time.deltaTime` (TerrainToolSystem 709), `Time.unscaledDeltaTime` (TerrainSystem 3976), the number of stamps per frame (GenerateBrushesSystem 80), and raycast positions that depend on frame time.
- **Mitigation available [V]:** `ApplyToTerrain` clamps delta to at least 0.05 (3774). At ≥ 20 fps the delta term is therefore always 0.05, so the only per-frame factor left is inside `Brush.m_Strength`, which is already baked into the `Brush` struct.
- **Conclusion:** capturing the final `Brush` structs (plus type, prefab texture identity and the clamped delta) on the host and replaying them in the same order gives *the same inputs* to the same kernels.
- **GPU output [I]:** float maths in compute shaders (pow, normalize, bilinear brush-texture sampling, blur) on R16_UNorm is not guaranteed bit-identical across GPU vendors or drivers. Same-vendor, same-driver results are likely identical. Cross-vendor results may differ by about 1 LSB along brush edges, and errors build up over long strokes.
- **Verdict:** use replay for low-latency visuals only. Use **heightmap tile transfer for correctness**.

---

## 3. Terrain save/load [V]

- `Serialize` (3079-3095) writes `SerializeHeightmap(worldHeightmap)`, then `SerializeHeightmap(m_Heightmap)`, then `heightScaleOffset`, `playableOffset`, `playableArea`, `worldOffset`, `worldSize` and `worldHeightMinMax`.
- `SerializeHeightmap` (3057-3077) does a **synchronous** `AsyncGPUReadback.RequestIntoNativeArray(...).WaitForCompletion()`, applies `NativeCompression.FilterDataBeforeWrite(..., stride 2)` (byte-plane filtering before generic compression [I on exact algorithm]), and writes the bytes.
- `Deserialize` (3133-3173) / `DeserializeHeightmap` (3097-3131) build `Texture2D` R16 maps and call `InitializeTerrainData` (3196-3206), which runs `SetHeightmap`, `SetWorldHeightmap` and `FinalizeTerrainData` (3318-3444). `FinalizeTerrainData` rebuilds the cascade and RTs, writes `m_CPUHeights` straight from the loaded data (3438-3441) and sets shader globals.
- The cascade, objects layer and downscaled map are **not saved**. They are rebuilt from ECS state after load (`UpdateCascades(isLoaded: true)`, 4089-4116).
- `TerrainMaterialSystem.Serialize` (886-891) saves the user splatmaps (material painting) and a render-settings entity.
- Runtime replacement API: `ReplaceHeightmap(Texture2D)` (3208-3224) and `ReplaceWorldHeightmap` (3226-3237) are public. They call `WaterSystem.TerrainWillChange()` and turn off water active-cell culling. Both reset offsets to defaults, so they are suitable only for a full replace (as on join), not for patches.

---

## 4. How other systems read terrain [V]

- `Game/Game.Simulation/TerrainUtils.cs`:
  - `SampleHeight` (82-93) bilinearly samples `m_CPUHeights` (`SampleHeightInternal` 121-137) and falls back to the backdrop (`SampleHeightBackdrop` 95-119).
  - `SampleHeight(..., out normal)` (139-157), `GetHeightRange` (59-80), `Raycast` (159+), and the space conversions `ToHeightmapSpace` / `ToWorldSpace` (10-43).
- Callers of `GetHeightData()` include simulation systems (CarMove/CarNavigation, Human/Animal move, ZoneSpawn, AreaSpawn, AreaLotSimulation, WaterDamage, FloodCheck, LandValue, NaturalResource, TerrainAttractiveness, SoilWater, WindSimulation, PowerPlantAI, Attraction, Climate, WeatherPhenomenon), tools (Net, Object, Zone, Selection, Validation, CourseSplit, Generate{Nodes,Edges,Areas}), and `GroundHeightSystem`, `AlignSystem`, `SubObjectSystem`, `LaneSystem`. These were found by grepping `.GetHeightData(`.
- **Consequence:** because CPU heights come from the cascade, the "effective ground" that snapping and simulation see is base heightmap + lots + roads + areas. Base heightmap and ECS state together fully determine it, apart from GPU rasterisation differences [I].
- `WindSimulationSystem.OnUpdate` (`Game/Game.Simulation/WindSimulationSystem.cs` ~338-350) and `SnowSystem.SnowTransfer` (495) read terrain too.

---

## 5. Making terrain identical on a remote machine

### 5.1 Options compared

| Approach | Bandwidth | Exactness | Notes |
|---|---|---|---|
| Replay mouse input / `BrushDefinition` | tiny | **No**: frame-time and stamp-count dependent (§2.2) | Reject |
| Replay `Brush` structs (host-captured) with pinned delta | ~48 B per stamp | Same inputs; GPU output possibly ±1 LSB across vendors [I] | Good for latency |
| Transfer `m_Heightmap` dirty tiles | per 64×64 tile 8 KiB raw, compresses well | **Bit-exact** | Authoritative |
| Full heightmap | 32 MiB raw (4096²×2) | Bit-exact | Join / resync only |

### 5.2 Recommended protocol (host-authoritative, converging)

1. **Capture on the host.** Use a Harmony prefix on `TerrainSystem.ApplyBrush(TerraformingType, Bounds2, Brush, Texture)` (3968) to record `(type, area, Brush, brushPrefabId, clampedDelta = clamp(Time.unscaledDeltaTime, .05, 1))`. Alternatively, hook `ApplyBrushesSystem.ApplyHeight` (302) and `ApplyCellMapBrush` (316) so ore/oil/fertility/groundwater stamps are covered too.
2. **Predict on clients (optional).**
   - Apply the received stamps by calling private `ApplyToTerrain(m_Heightmap, null, delta, ...)` through reflection or an open delegate, so the delta is pinned and not read from the local `Time`.
   - Do the same bookkeeping as `ApplyBrush`: `WaterSystem.TerrainWillChangeFromBrush`, `UpdateMinMax`, `TriggerAsyncChange`.
   - Clients' own terrain tool input must **not** apply locally. Turn it into a request to the host.
3. **Correct authoritatively.**
   - After a stroke settles (mouse released, plus about 200 ms), the host reads back the union dirty rectangle of `m_Heightmap`. Track it independently: `m_LastWrite` is consumed by the readback logic at 3665-3666.
   - Split it into tiles (for example 64×64) and send changed tiles: hash and ushort data, delta-encoded and LZ4/zstd compressed.
4. **Apply on clients.**
   - Upload the tile into a cached `Texture2D` (R16_UNorm).
   - Run `Graphics.CopyTexture(src, 0, 0, 0, 0, w, h, terrainSystem.heightmap, 0, 0, x, y)`. `heightmap` is public (2500) and is the `RenderTexture` `m_Heightmap`.
   - Then call `OnAreaChanged(worldBounds)` (public, 5149), `WaterSystem.TerrainWillChangeFromBrush(worldBounds)` (public, 911) and `UpdateMinMax(default, worldBounds)` (public, 3982; it only uses the area).
   - The cascade re-render then sets `m_LastWrite` and triggers the CPU readback for you (4496-4511).
5. **Verify.**
   - Every N seconds, or after each tile batch, compare per-tile hashes of `m_Heightmap` (async readback on both sides) and re-send tiles that differ.
   - **Do not** hash `m_CPUHeights` / the cascade as the authority. They include GPU-rasterised roads and lots and may legitimately differ by 1 LSB [I].
6. **Join.** The save (or a custom snapshot) carries `TerrainSystem.Serialize` data. Then run one full hash comparison.

### 5.3 Side effects to neutralise on clients
- `GroundHeightSystem.UpdateHeightsJob` rewrites `Transform`, `Curve` and `Node` from *local* CPU heights and water (§2.2 step 8). One LSB of height (0.0625 m) is more than its 0.01 m threshold. For host-authoritative entities, either suppress these writes on clients (patch `GroundHeightSystem.OnUpdate`) or let the replicated host transforms always win (they arrive after).
- Ore, oil, fertility and groundwater brushes write CPU cell maps. Replicate those maps (§8), not the strokes.
- Material painting (`TerrainMaterialSystem.ApplyBrush`) is GPU splatmap state that is saved. Sync it with the same tile approach, or by replaying stamps (it is visual only).

---

## 6. Water

### 6.1 Storage [V]
`Game/Game.Simulation/WaterSystem.cs`, class `WaterSystem` (implements `IGPUSystem`; registered with `updateSystem.RegisterGPUSystem<WaterSystem>()` at SystemOrder 46; also `UpdateAt PostSimulation`, 631).

| Item | Value | Where |
|---|---|---|
| Sim texture size `m_TexSize` | 2048x2048 | `InitTextures` 935-964 |
| `kMapSize` | 14336 m, so `kCellSize` = 7 m | 392, 464 |
| `WaterTexture` = `waterTextures[0]` | R32G32B32A32_SFloat "WaterRT0": **x = depth, y,z = velocity, w = pollution** (decoded by `SurfaceWater(float4)`, `Game/Game.Simulation/SurfaceWater.cs`) | 212, 617 |
| `WaterRenderTexture` = `waterTextures[1]` | RGBA32F scratch / "previous" (reset each `VelocityStep`, 698-699) | 213, 623 |
| Backdrop | `waterBackdropTextures[0/1]` 1024² RGBA32F | 216-217 |
| Derived | `seaPropagationTexture` R16F, `maxHeightDownscaled` R16F, `seaPropagationDownscaled`, `downdScaledFlowTextures[0..3]` RGBA16F (flow, rendering) | 214-235 |
| Defaults | sea level 511.7, `kMaxSourceHeight` 250, `kMaxSourceRadius` 2500, `kMaxSeaLevel` 2000 | 394-402 |

### 6.2 Simulation loop [V]
- **Driver:** `UpdateSystem.OnBeginFrame`, hooked to `RenderPipelineManager.beginFrameRendering` (`Game/Game/UpdateSystem.cs` 96, 105-130), calls `WaterSystem.OnSimulateGPU` (1579-1585), which calls `Simulate` (1461-1577) **once per rendered frame**. If `IsAsync`, it runs on async compute (UpdateSystem 111-123).
- **Per step:** for `WaterSimSpeed` iterations: `EvaporateStep`, `SourceStep(LastFrameSourceCache)`, `VelocityStep`, `DepthStep`, `CheckReadbacks`, and the backdrop steps if enabled (1524-1545). Afterwards, `CopyToHeightmapStep` (1546).
- `m_NextSimulationFrame += 4*MaxSpeed/WaterSimSpeed` (1540). `MaxSpeed` comes from `TerrainPropertiesPrefab.m_WaterMaxSpeed` (`Game/Game.Simulation/TerrainInitializeSystem.cs` 54).
- **Timestep:** `GetTimeStep()` (1406-1425):
  - 1.0 while `IsNewMap`; 0 if paused (`selectedSpeed == 0`); else `TimeStepOverride` if set.
  - Otherwise `m_lastFrameTimeStep = lerp(prev, min(1, selectedSpeed*0.25 * min(smoothDeltaTime*30, 1)), smoothDeltaTime*0.2)`.
  - **This depends on frame rate and on history.**
- **ECS side:** `WaterSystem.OnUpdate` (1610-1631) schedules `SourceJob` / `SourceJobLegacy`, which gather all `WaterSourceData` + `Transform` into a double-buffered `NativeList<WaterSourceCache>` that the next GPU frame consumes.
- **Active-tile culling:** `ActiveWaterTilesHelper` (`Game/Game.Simulation/ActiveWaterTilesHelper.cs`) keeps a GPU "active" buffer read back asynchronously (95-146). Tiles that are not active are not simulated (`UpdateActiveIndices`, called at `WaterSimulation.cs` 628). Which tiles are active depends on readback timing. `UseActiveCellsCulling` (613) can turn culling off.
- **Terrain coupling:**
  - `TerrainWillChangeFromBrush` (911-927) sets `WaterSimSpeed = 0` for 15 GPU frames and accumulates the change area. `TerrainWillChange` (929-933) does the same for 1 frame.
  - While that counter runs, `RestoreHeightFromHeightmap`, sea propagation and `MaxHeightStep` run (1475-1516).
  - All kernels sample the terrain cascade and objects layer (for example `VelocityStep` 701-702, `DepthStep` 818-819). **Water reads the derived cascade, so roads, dams and lots shape water.**
- **Parameters** (`IWaterSimulation`, `WaterSimulation.cs` 240-254): `MaxVelocity` 12, `Damping` 0.995, `Evaporation` 0.001, `RainConstant` 5e-5, `PollutionDecayRate` 0.001, `Fluidness` 0.15, `WindVelocityScale` 0.08, `WaterSourceSpeed` 2. All are public setters, and third-party mods change them. For example `thirdparty/Water_Features/Water_Features/Systems/ChangeWaterSystemValues.cs` 115-182 changes Evaporation, Fluidness and Damping at runtime.

### 6.3 Water sources [V]
- `Game.Simulation.WaterSourceData` (`Game/Game.Simulation/WaterSourceData.cs` 6-74):
  - Fields: `m_ConstantDepth` (type code), `m_Radius`, `m_Height`, `m_Multiplier`, `m_Polluted`, `m_Id`, `m_Modifier`.
  - Serialised: everything except `m_Modifier`, which is reset to 1 on load (72).
  - Initialised from the prefab `Game.Prefabs.WaterSourceData` by `WaterSourceInitializeSystem` (`Game/Game.Simulation/WaterSourceInitializeSystem.cs` 32-48), phase `ModificationEnd` (SystemOrder 276).
- **New sources** (`m_UseLegacyWaterSources == false`, the default for new maps: `SetDefaults` 1204):
  - `WaterSimulation.SourceJob` (`WaterSimulation.cs` 25-82) caches the position, `m_Radius * m_Modifier` and `m_Height`.
  - `SourceStep` (501-564): a source with pollution dispatches `AddKernel` with `amount = 0.3165952 * 3 * height`. Otherwise it dispatches `AddConstantKernel` with a target surface height (`m_Position.y + m_Height`). Sea level is a global (`SeaLevel` 558-569; the setter calls `UpdateSeaLevel` 1218-1223, which triggers sea removal, propagation and restore in `Simulate` 1517-1523).
- **Legacy sources:** `WaterSimulationLegacy.SourceJobLegacy` (`Game/Game.Simulation/WaterSimulationLegacy.cs` 19-100). `m_ConstantDepth` 2 = river, 3 = sea. The height is raised by `WaterLevelChange` events, i.e. tsunamis and floods (`HandleSource` 46-82). Conversion is in `UpgradeToNewWaterSystem` (`WaterSystem.cs` 1242-1295), which destroys sea sources and derives sea level from them.
- **Sources driven by the simulation:**
  - `SewageOutletAISystem` writes `m_Height` and `m_Polluted` (`Game/Game.Simulation/SewageOutletAISystem.cs` 108-116).
  - `WaterPumpingStationAISystem` writes a negative `m_Height` (`Game/Game.Simulation/WaterPumpingStationAISystem.cs` 164-165) and reads surface water to compute capacity and pollution (150-167).
  - These are ECS components and must replicate like any other.
- **Events:** `WaterLevelChangeSystem` (`Game/Game.Simulation/WaterLevelChangeSystem.cs` 40-81) computes the tsunami intensity from `SimulationSystem.frameIndex`. This is deterministic given the frame index, but it feeds only the legacy sources.

### 6.4 CPU readback (SurfaceDataReader) [V]
- `SurfaceDataReader : BaseDataReader<SurfaceWater, float4>` (`Game/Game.Simulation/SurfaceDataReader.cs` 14-129; base `Game/Game.Simulation/BaseDataReader.cs` 13-200) is **internal**.
- The full-resolution CPU array `m_CPU` (2048² `SurfaceWater`) is refreshed **one 256×256 tile per readback** (`m_ReadbackDistribution = 8`, so 64 tiles; `GetReadbackBounds` 136-142, 165-192). A readback is issued every 30 water frames (`DepthReadbackRequestInterval` 737, `CheckReadbacks` 1436-1446).
- **So the CPU water view is a stale mosaic.** A full refresh takes 64×30 = 1920 GPU frames (about 32 s at 60 fps) [I, arithmetic], unless `FullReadback` is set (set only after `IsNewMap`, 1553) or an area is set (after terrain changes, 1495-1499).
- Public accessors: `GetSurfaceData`, `GetSurfacesData` (with backdrop), `GetVelocitiesSurfaceData`, `GetMexHeightSurfaceData`, `GetDepths`, plus the matching `Add*Reader` calls (786-832). Sampling helpers are in `Game/Game.Simulation/WaterUtils.cs` (`SampleDepth` 48-96, `SamplePolluted` 98, `SampleVelocity` 116, `SampleHeight` 135-201, `Raycast` 222).
- **Gameplay consumers** (grep of `GetSurfaceData` and friends): WaterPumpingStationAI, PowerPlantAI, FloodCheck, WaterDamage, SoilWater, NaturalResource, LandValue, AreaResource, Climate, WeatherPhenomenon, WindSimulation, Watercraft move/navigation, GroundHeightSystem, and **ValidationSystem / ValidationHelpers** (placement validity, for example `Game/Game.Objects/ValidationHelpers.cs` 2338-2379), plus many tools.

### 6.5 Water save/load [V]
- `Serialize` (1030-1070) writes: frame counters, size, then a **full synchronous readback of `WaterTexture`** (RGBA32F, filtered for compression), then sea level, `m_SimulateBackdrop`, `m_NextSourceId`, backdrop size, `m_UseLegacyWaterSources`, and a full readback of `WaterBackdropRenderTexture`.
- `Deserialize` (1097-1194) calls `WaterSimulation.LoadWaterData` (`WaterSimulation.cs` 468-499). That function uploads the float4 array into a `ComputeBuffer`, dispatches the `Load` kernel into the target texture, and also seeds the CPU reader (`surfaceReader.LoadData`). It then clears derived textures, resets active tiles and requests readbacks.
- `PostDeserialize` (1638-1649) runs one `Simulate` and fixes old source heights.
- `Save()` (1587-1591) pauses the simulation (`WaterSimSpeed = 0`) and requests an async readback.

### 6.6 Is water deterministic across machines? **No.** [V]
- It is stepped per rendered frame, not per simulation tick (UpdateSystem 105-130). Different fps means a different number of steps.
- The timestep depends on `Time.smoothDeltaTime` with smoothing state (1416-1424).
- Active-tile sets depend on async readback completion (`ActiveWaterTilesHelper`).
- `TerrainWillChange*` pauses depend on local GPU-frame counts (925-932).
- The CPU view is a mosaic whose timing depends on frame count (§6.4).
- Float32 GPU compute with parallel reductions / atomics in kernels [I] can also give different results across GPUs.
- Simulation-driven sources (pump, outlet) take their values from ECS, so they match only if the ECS state matches.

### 6.7 Keeping water consistent: recommended design
1. **Gameplay authority.** Only the host runs the systems that read water and affect gameplay (pumping capacity, flood check, water damage, soil water / flood start, natural resources, land value, placement validation results). Their outputs replicate as ECS state. Clients never derive gameplay from their own water.
2. **Client visuals.** Clients run `WaterSystem` normally, with sources, sea level, legacy flag, backdrop flag and `IWaterSimulation` parameters mirrored exactly from the host. Pausing follows the host's `selectedSpeed`, since `GetTimeStep` returns 0 when paused. Identical terrain plus identical sources keeps the water *close* but not equal.
3. **Snapshot correction.**
   - Read API: `WaterSystem.WaterTexture` is a public `RenderTexture` (617), so the host can `AsyncGPUReadback` any region without blocking. This is the same call `Serialize` uses, minus `WaitForCompletion`.
   - Schedule: every few seconds the host reads back one or more 256×256 tiles, round-robin plus "hot" tiles near recent changes, and sends them quantised. Depth as float16 or a ushort with mm precision; velocity as float16×2; pollution as a byte. Then compress. One tile is 1 MiB raw RGBA32F, about 448 KiB quantised before compression [I, arithmetic].
   - Client write API: `Graphics.CopyTexture` from an RGBAFloat `Texture2D` region into `WaterSystem.WaterTexture`, or reproduce the `Load`-kernel path through reflection on `WaterSimulation.LoadWaterData` (internal generic). Afterwards:
     - set `UseActiveCellsCulling = false` for a few frames, or call `ResetActive` through reflection, so newly wet tiles are simulated;
     - set `ForceUpdateFlow = true` (591) to refresh flow;
     - optionally update the CPU mosaic through reflection on the internal `m_depthsReader.LoadData`, though it also refreshes by itself.
   - Do not write `WaterRenderTexture`. It is overwritten every step (698-699). Sea propagation and max-height are derived and are rebuilt by `TerrainWillChange()` (public), which runs the restore and propagation passes.
   - Full resync on join: the save already includes `WaterTexture` and the backdrop. To force exact equality after join, you can set `TimeStepOverride` / pause water on both sides at the snapshot moment.
4. **Optional "frozen client water".** For bandwidth-limited sessions, set `WaterSimSpeed = 0` on clients and rely only on host snapshots. `GetTimeStep` and `Simulate` still render the last state, and `CopyToHeightmapStep` keeps running when `WaterSimSpeed == 0` (1511-1514). This gives exact water at snapshot rate but no smooth motion.
5. **Bandwidth figures:** full `WaterTexture` = 2048² × 16 B = 64 MiB raw. Backdrop = 16 MiB raw. Depth-only as ushort = 8 MiB. Full snapshots are acceptable only at join. Steady state must use tiles and deltas [I, arithmetic].

---

## 7. Snow [V]
- `Game/Game.Simulation/SnowSystem.cs`. It is *not* a CellMapSystem.
- Two 1024² R16G16_UNorm RTs (`CreateTexture` 385-395), a 1024×1 backdrop RT and compute buffers.
- Updated in `GameSimulation` (SystemOrder 361) every **4 simulation frames** (`GetUpdateInterval` 226-229) with a **fixed timestep 0.2** (460). It is therefore tick-driven, unlike water.
- Inputs: `ClimateSystem` temperature and precipitation, `WindSimulationSystem.constantWind`, `TimeSystem.normalizedTime`, **sun direction from `PlanetarySystem` (rendering)**, the terrain cascade, the base heightmap, and **`WaterSystem.WaterTexture`** (455-500).
- Saved through `Serialize` with a synchronous readback (244-266) and loaded through the `LoadR16G16_UNorm` kernel (288-370).
- Consumers are rendering only: `TerrainRenderSystem` 141 and `TerrainMaterialSystem` 1431. No CPU gameplay reads it (grep of `m_SnowSystem.`).
- Verdict: snow is cosmetic. It may drift because water differs. Optionally sync it at join (it is in the save) and send a 4 MiB-raw snapshot rarely, or not at all.

---

## 8. CellMapSystem derivatives (pollution, groundwater, etc.)

### 8.1 Base class [V]
`Game/Game.Simulation/CellMapSystem.cs` 11-232:
- `CellMapSystem<T>` holds a `NativeArray<T> m_Map` of `textureSize²` over `kMapSize` 14336 m.
- Serialisation is Burst jobs (`SerializeJob` / `DeserializeJob`, stride-compressed when `T : IStrideSerializable`, 14-84).
- Public access: `GetMap(readOnly, out deps)`, `GetData(...)` (returns `CellMapData<T>`, `Game/Game.Simulation/CellMapData.cs`), `AddReader`, `AddWriter` (154-179).
- **A mod can therefore read and overwrite any cell map on the main thread**: `GetMap(false, out deps)`, `deps.Complete()`, copy, `AddWriter(default)`.

### 8.2 Inventory [V] (grep `: CellMapSystem<`)

| System | Size | Update interval (frames; 262144 = 1 day) | Random? | Gameplay relevance |
|---|---|---|---|---|
| `AirPollutionSystem` | 256² | 262144/128 = 2048 | `RandomSeed.Next()` (129) | health, land value |
| `GroundPollutionSystem` | 256² | 2048 | `RandomSeed.Next()` (99) | health, fertility |
| `NoisePollutionSystem` | 256² | 2048 | | happiness, land value |
| `GroundWaterSystem` | 256² (`GroundWater {short amount, polluted, max}`) | 128 | | **pumping stations** (`WaterPumpingStationAISystem` 136-149 consumes it) |
| `SoilWaterSystem` | 128² | per 1024/day ×8 slices (323-324) | | **reads CPU water + terrain; starts and stops floods** (StartFlood around 80-118) |
| `NaturalResourceSystem` | 256² | 8192 (1 in editor) | `RandomSeed.Next()` (433) | industry (ore/oil/fertility/fish) |
| `LandValueSystem` | 128² | 262144/32 | | rent, zoning |
| `TerrainAttractivenessSystem` | 128² | 262144/16 | | attractiveness |
| `PopulationToGridSystem` | 64² | 262144/32 | | |
| `AvailabilityInfoToGridSystem` | 128² | 262144/32 | | |
| `WindSystem` | 64² | `kUpdateInterval` | | air pollution spread |
| `ZoneAmbienceSystem`, `TrafficAmbienceSystem` | 64² | 2048, 256 | | ambience / land value |
| `TelecomCoverageSystem` | 128² | 4096 | | telecom |

`Game/Game.Common/RandomSeed.cs` 6-23: `RandomSeed.Next()` draws from a static `Unity.Mathematics.Random` **seeded with `DateTime.Now.Ticks`** (line 8). [V] So cell-map simulations that use it can never produce matching results on two machines.

### 8.3 Do they matter for sync? Yes.
- They change gameplay (economy, health, flooding) and are visible in client info views and tooltips (`PollutionTooltipSystem`, `NaturalResourcesTooltipSystem`, `LandValueTooltipSystem`, info-view UI systems).
- Host-authoritative plan: clients do **not** run these simulation systems, or their results are overwritten. When the host system updates (infrequent, see the table), it sends a delta-compressed full map (largest: 256² × a few bytes ≈ 128-384 KiB raw, which compresses heavily). Clients write it through `GetMap(readOnly:false, ...)`.
- **SoilWaterSystem must be host-only.** It reads non-deterministic CPU water and *creates and deletes flood event entities*.

---

## 9. Ordering cheat-sheet [V] (`Game/Game.Common/SystemOrder.cs`)
- `GenerateBrushesSystem`, `GenerateWaterSourcesSystem`: Modification1 (101, 103).
- `GroundHeightSystem`: Modification2 (117).
- `WaterSourceInitializeSystem`: ModificationEnd (276). `TerrainSystem`: after ModificationEnd (304).
- `SnowSystem`, `GroundWaterSystem`, `WaterLevelChangeSystem`, `FloodCheckSystem`, `WaterDangerSystem`, `WaterDamageSystem`: GameSimulation (361, 406, 519, 548, 580-581).
- `WaterSystem` (ECS part): PostSimulation (631). GPU part: every rendered frame (46).
- `TerrainMaterialSystem`, `TerrainRenderSystem` (calls `RenderCascades`), `WaterRenderSystem`: PreCulling (641-643).
- `ApplyBrushesSystem`, `ApplyWaterSourcesSystem`: ApplyTool (719, 721), which runs only when `applyMode == Apply` (`ToolOutputSystem` 20-31).

---

## 10. Risks and unknowns
- **Shader sources are not available** (`AdjustTerrain`, the water update shader, `snowUpdate`, the BuildingLot/Lane/Area raster shaders). GPU bit-exactness across vendors and what `_WaterTexture` does in terraforming are unknowns [I].
- Cascade rasterisation (roads, lots) could differ slightly between GPUs, so CPU heights and therefore client-side snapping and validation could differ by an LSB. Placement validation must be decided by the host.
- `m_LastWrite`, `ApplyToTerrain`, `m_depthsReader`, `ResetActive` and `LoadWaterData` are private or internal. Using them needs reflection or Harmony and can break on game updates.
- Water snapshots are large. Use tile scheduling and quantisation, and avoid synchronous readbacks (`Serialize` uses `WaitForCompletion` and would stall the host).
- Mods such as Water_Features change water parameters and sources at runtime (`thirdparty/Water_Features/...`). Every peer needs the same mod set and settings, or the host must replicate parameters.
- Async GPU readback latency means a client's CPU heights lag its own GPU writes by a few frames. Tools on clients should wait for `GroundHeightSystem` readiness before sending placement requests.
