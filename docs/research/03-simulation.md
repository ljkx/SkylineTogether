# 03 – Simulation architecture and determinism (Cities: Skylines II)

> **Historical research report.** Written before and during the implementation; it describes the game, not the
> mod, and is not maintained. For how the mod works, see [docs/ARCHITECTURE.md](../ARCHITECTURE.md). Code is cited
> by file and member and described in prose; no game code is reproduced.

Research for **Skyline Together** (clean-room co-op mod). Every path below is relative to
the root of the game's decompiled assemblies unless stated otherwise.
Line numbers refer to the decompiled files as read on 2026-10-10.

Legend used throughout:

- **[V]** verified: read in the decompiled code at the cited location.
- **[I]** inference: follows from verified code plus general Unity DOTS/Burst knowledge, but not proven line by line.
- **[E]** estimate: a rough number for planning only, not measured in a running game.

---

## 0. TL;DR

1. **Deterministic lockstep is not feasible** with the vanilla simulation, short of rewriting large parts of it. Nondeterminism is structural, not incidental. It comes from a wall-clock-seeded global RNG that rendering, tools and audio share; random streams keyed by chunk index; more than 200 parallel `NativeQueue` writers whose order depends on thread timing; pathfinding results applied whenever a worker thread finishes; command buffers played back once per *rendered* frame (so results depend on how many ticks were batched into a frame); a GPU water simulation with a `smoothDeltaTime` time step; GPU readbacks for terrain and water; and per-machine Entity IDs. See §5 and §8.
2. **The recommended model is host-authoritative state replication with a follower client.** The host runs the full vanilla simulation. Clients advance `SimulationSystem.frameIndex` in step with the host, so time, date, climate and rendering interpolation stay correct. Clients disable all gameplay systems in `SystemUpdatePhase.GameSimulation` and the few gameplay-producing `ModificationEnd` systems (XP, milestones, triggers, achievements). They apply host deltas *before* `ModificationSystem` runs, so vanilla initialization, reference and rendering systems process them as if the changes were local. See §9.
3. The game already slices its work very regularly. A day is `262144` ticks. Most per-entity systems process 1/16 of their entities per invocation, using an `UpdateFrame` shared component (0..15). Moving objects write a 4-deep `TransformFrame` ring every 16 ticks, and rendering already interpolates 32 ticks behind. This maps well onto snapshot replication with built-in interpolation (§2, §9.5).
4. Much authoritative state lives in **managed system fields, not ECS components**: tax rates, demand values and factors, statistics, trade, production, electricity and water statistics, `frameIndex`. Most of these systems implement `ISerializable`, which gives a ready-made, version-aware encoding for syncing system-level state (§6.0, §9.4).

---

## 1. The frame loop: where simulation ticks happen

### 1.1 Order of work in one rendered frame [V]

`GameManager.Update()` → `UpdateWorld()` → `UpdateSystem.Update(SystemUpdatePhase.MainLoop)`, then later `GameManager.LateUpdate()` → `LateUpdateWorld()` → `Update(SystemUpdatePhase.LateUpdate)`
(`Game/Game.SceneFlow/GameManager.cs:711-733`, `2401-2424`).

The MainLoop registration order is in `Game/Game.Common/SystemOrder.cs`:

| Order | System (phase) | Citation |
|---|---|---|
| first (UpdateBefore) | `UpdateWorldTimeSystem`, `PathfindQueueSystem`, `EndFrameBarrier` | SystemOrder.cs:47-49 |
| | `ToolSystem` (runs `PreTool`/`ToolUpdate` sub-phases) | SystemOrder.cs:58; `Game.Tools/ToolSystem.cs:255,327` |
| | `ModificationSystem`: runs `Modification1..5`, `ModificationEnd` | SystemOrder.cs:60; `Game.Common/ModificationSystem.cs:17-27` |
| | `UnlockSystem`, `PathfindSetupSystem`, `PathfindResultSystem`, `UIUpdateSystem`, `RenderingSystem` | SystemOrder.cs:61-68 |
| LateUpdate | **`SimulationSystem`** (runs 0..8 sim ticks) | SystemOrder.cs:74 |
| Cleanup | `CleanUpSystem` etc. | SystemOrder.cs:54 |

Consequences, all **[V]** from the code above:

- **`EndFrameBarrier` plays back at the very start of the next frame's MainLoop.** It is registered with `UpdateBefore` (`UpdateSystem.UpdateBefore` uses `addIndex - 1000000`, `Game/Game/UpdateSystem.cs:163-166`). Most simulation jobs record structural changes into `m_EndFrameBarrier.CreateCommandBuffer()` (example: `BirthSystem.cs:300`). So *all* structural changes from *all* ticks of frame N are applied together at the start of frame N+1 (`Game/Game/EndFrameBarrier.cs:34-43`).
- **The Modification phases (entity initialization, references, `Created`/`Updated` processing, `UpdateGroupSystem`) run once per rendered frame, not once per tick.** They live inside `ModificationSystem`, which is in MainLoop.
- A natural "commit point" for network-applied changes is **before `ModificationSystem` in MainLoop**. Changes injected there are processed by the vanilla pipeline in the same frame (§9.3).

### 1.2 `SimulationSystem` (`Game/Game.Simulation/SimulationSystem.cs`) [V]

State:
- `frameIndex` (uint, private setter, l.68): the global simulation tick. It is the **only** value `SimulationSystem.Serialize` writes (l.135-146).
- `frameTime` (l.70): sub-tick fraction `m_Timer*60` used for interpolation (l.270).
- `selectedSpeed` (l.72-85): ignored while `m_IsLoading`. `smoothSpeed` (l.87): UI- and interpolation-smoothed speed.
- `frameDuration` (l.111): measured CPU cost of one tick. `performancePreference` (FrameRate / Balanced / SimulationSpeed, l.20-25, read from settings l.120).

Tick count per rendered frame (`OnUpdate`, l.188-296):
1. `selectedSpeed == 0` → 0 ticks (pause, l.221-225).
2. Otherwise `m_Timer += Time.deltaTime * selectedSpeed` (l.228-237), then `num = floor(m_Timer*60)` (l.238). **Nominal rate: 60 ticks per real second at speed 1.**
3. **Pathfinding back-pressure:** if `PathfindResultSystem.pendingSimulationFrame` is set, ticks are capped so `frameIndex` never passes a pending path result's deadline, and speed is scaled by `pending/48` (`PENDING_FRAMES_SPEED_FACTOR = 1/48`, l.38, l.231-245).
4. **CPU back-pressure:** unless `PerformancePreference.SimulationSpeed`, ticks are capped by `(EndFrameBarrier.lastElapsedTime - currentElapsedTime)/frameDuration` (l.246-253).
5. Hard cap `num7 = clamp(round(selectedSpeed*num3*2), 1, 8)` ticks per frame (l.255-257).
6. Each tick runs `frameIndex++`, then `UpdateSystem.Update(GameSimulation, frameIndex, i)` (or `EditorSimulation` in the editor) (l.277-288). `PreSimulation` and `PostSimulation` run once per frame around the loop (l.272, 295).

Speed values: the UI maps index 0..2 to `selectedSpeed = 2^index`, giving **1, 2, 4** (`Game/Game.UI.InGame/TimeUISystem.cs:228-240`). Photo mode allows 0..8 (`Game/Game.Rendering/PhotoModeRenderSystem.cs:765-779`).

Other code that sets `selectedSpeed = 0` locally [V]:
- Error dialogs: `Game/Game.UI/ErrorDialogManager.cs:270-292`.
- Lightning, if `pauseSimulationOnLightning`: `Game/Game.Rendering/ClimateRenderSystem.cs:445-449`.
- Game loading: `SimulationSystem.cs:216-219`.
- The "paused after loading" setting: l.213.

A co-op mod must override these on clients.

Loading [V]:
- `OnGamePreload` sets `m_IsLoading`, and for `Purpose.NewGame` `m_LoadingCount = 1024` (l.155-162).
- `UpdateLoadingProgress` then runs **1024 warm-up ticks**, 8 per frame, in the `LoadSimulation` phase (l.164-186). This phase contains only vehicle navigation/movement, transport AIs, `TrafficSpawnerAISystem` and `RandomTrafficDispatchSystem` (SystemOrder.cs:611-630).

**Implication [V+I]:** the number of ticks per rendered frame depends on wall-clock `deltaTime`, measured job cost, and pathfinding thread progress. Combined with once-per-frame command-buffer playback (§1.1), the *same* sequence of ticks gives different results depending on how ticks were grouped into frames. This alone rules out lockstep without patching the frame loop.

### 1.3 Time scale [V, arithmetic]

- `TimeSystem.kTicksPerDay = 262144` (`Game/Game.Simulation/TimeSystem.cs:18`). `TimeSettingsPrefab.m_DaysPerYear = 12` by default (`Game/Game.Prefabs/TimeSettingsPrefab.cs:10`).
- Date and time are a **pure function** of `frameIndex` and the `TimeData` singleton (`m_FirstFrame`, starting year/month/hour; `Game/Game.Common/TimeData.cs`): see `TimeSystem.GetTicks` (l.89-96) and `GetTimeOfDay`/`GetTimeOfYear`/`GetYear` (l.103-150).
- At the nominal 60 ticks/s (speed 1): 16 ticks ≈ 0.27 s, 256 ≈ 4.3 s, 1024 ≈ 17 s, 8192 ≈ 2.3 min, one "day" (262144) ≈ 72.8 min, one year (12 days) ≈ 14.6 h. **[V arithmetic; the real rate is lower when capped by §1.2 steps 3-5.]**

---

## 2. The update-interval mechanism (frame slicing)

### 2.1 System-level slicing [V]

- `GameSystemBase.GetUpdateInterval(phase)` defaults to 1 and `GetUpdateOffset(phase)` defaults to -1 (`Game/Game/GameSystemBase.cs:131-139`).
- `UpdateSystem.GetInterval` throws unless the interval is a power of two (`UpdateSystem.cs:294-307`).
- Dispatch rule: a system runs on tick `frameIndex` iff `(frameIndex & (interval-1)) == offset` (`UpdateSystem.cs:241`).
- **Offsets of -1 are auto-assigned** in `UpdateSystem.Refresh()` (l.309-380). Systems with offset -1 in a phase are grouped by interval and spread across residues with a bit-reversal-like stride (l.346-368), so that systems sharing an interval do not all fire on the same tick. `UpdateAfter<A,B>`/`UpdateBefore<A,B>` children inherit the parent's offset when the intervals match (l.382-455, 457-517).
- **[I] Determinism hazard:** auto-assigned offsets depend on registration order and on *which systems are registered*. A mod that registers an extra `GameSimulation` system with an interval and default offset shifts the offsets of vanilla systems registered after it. Host and clients must therefore run an **identical mod set**, or offsets will differ. This matters even for a follower design, because cosmetic systems kept on clients would fire on different ticks.
- `SystemData.m_ResetInterval` resets a system's job dependency when more iterations than its interval run inside one frame (`UpdateSystem.cs:36, 247-250`). This is a scheduling detail, not a correctness issue.

### 2.2 Entity-level slicing: `UpdateFrame` [V]

- `Game.Simulation.UpdateFrame` is an `ISharedComponentData` with `uint m_Index` that is **serialized** with the save (`Game/Game.Simulation/UpdateFrame.cs`).
- `UpdateGroupSystem` (Modification5, SystemOrder.cs:214) assigns new entities to the **least populated** of 16 groups per category: moving objects, trees, buildings, nets, lanes, companies, households, citizens, household pets (`Game/Game.Simulation/UpdateGroupSystem.cs:92-188`, `FindUpdateIndex` l.522-536). A prefab can force a group with `UpdateFrameData` (l.500-507). Temp/controller entities inherit their original's group (l.298-356).
- Helper formulas (`Game/Game.Simulation/SimulationUtils.cs`):
  - `GetUpdateFrame(frame, updatesPerDay, groupCount) = (frame / (262144/(updatesPerDay*groupCount))) & (groupCount-1)`
  - `GetUpdateFrameWithInterval(frame, interval, groupCount) = (frame/interval) & (groupCount-1)`
  - `GetUpdateFrameRare(...)`
- Typical pattern (`BirthSystem.cs:246-248, 280, 105`): the interval is `262144/(kUpdatesPerDay*16)`. On each invocation the job skips every chunk whose `UpdateFrame` differs from the current group. Every entity is therefore processed exactly `kUpdatesPerDay` times per day.
- Moving objects: `CarMoveSystem.OnUpdate` filters `UpdateFrame(frameIndex & 0xF)` and writes `TransformFrame` slot `(frameIndex>>4)&3` (`CarMoveSystem.cs:418-423`). **Each vehicle or human is simulated once every 16 ticks.** `TransformFrame` is a 4-element buffer: position, velocity, rotation, flags, state, activity (`Game/Game.Objects/TransformFrame.cs`).
- Rendering interpolation: `ObjectInterpolateSystem.CalculateUpdateFrames` uses `num = simFrame - updateFrameIndex - 32` and interpolates between two of the four frames with a Bezier/slerp (`Game/Game.Rendering/ObjectInterpolateSystem.cs:4332-4360`). **Rendering is intentionally about 32 ticks (~0.5 s at speed 1) behind the simulation.** That is a ready-made jitter buffer for replicated movement.
- `RenderingSystem.frameIndex = SimulationSystem.frameIndex + offset` (`Game/Game.Rendering/RenderingSystem.cs:180-195`). The client's rendering stays correct only if its `SimulationSystem.frameIndex` advances.

### 2.3 Interval table of important `GameSimulation` systems [V]

Each row is "interval (ticks) / offset". Computed from `GetUpdateInterval` and `kUpdatesPerDay`. All files are under `Game/Game.Simulation/`.

| Domain | System (file:line of GetUpdateInterval) | Interval | Per-entity period |
|---|---|---|---|
| Vehicles/humans | `CarNavigationSystem`, `CarMoveSystem`, `HumanMoveSystem`, `*AISystem` (e.g. `AmbulanceAISystem.cs:972`, offsets 0..13) | 16 | 16 |
| Traffic lights | `TrafficLightSystem.cs:731` | 4 | – |
| Stuck objects | `StuckMovingObjectSystem.cs:322` | 4 | – |
| Citizens | `CitizenBehaviorSystem.cs:1048` (16/off 11), `CitizenHappinessSystem.cs:994` (16) | 16 | 256 |
| Households | `HouseholdBehaviorSystem.cs:459` (kUpdatesPerDay=256) | 64 | 1024 |
| Birth / death | `BirthSystem.cs:246`, `DeathCheckSystem.cs:380` (16/day) | 1024 | 16384 |
| Aging, graduation | `AgingSystem.cs:202` (1/day) → 16384; `GraduationSystem.cs:253` | 16384 | 262144 |
| Partners/divorce | `LookForPartnerSystem.cs:208`, `DivorceSystem.cs:247` (4/day) | 4096 | 65536 |
| Move-in/out | `HouseholdSpawnSystem.cs:204`, `HouseholdFindPropertySystem.cs:955`, `HouseholdMoveAwaySystem.cs:284` | 16 | – |
| Jobs/schools | `FindJobSystem.cs:523`, `FindSchoolSystem.cs:429` (16); `CitizenFindJobSystem.cs:281` (256/day → 64) | 16/64 | – |
| Companies | `ProcessingCompanySystem.cs:487`, `ExtractorCompanySystem.cs:626`, `ServiceCompanySystem.cs:309` (`EconomyUtils.kCompanyUpdatesPerDay=256`) | 64 | 1024 |
| Company AI | `CommercialAISystem.cs:241`, `IndustrialAISystem.cs:310`, `OfficeAISystem.cs:188` (32/day) | 512 | 8192 |
| Profitability | `CompanyProfitabilitySystem.cs:187`, `CompanyDividendSystem.cs:215` (1/day) | 16384 | 262144 |
| Trade, resources | `ResourceBuyerSystem.cs:1179` (16), `TradeSystem.cs:284` (128/day → 2048), `ResourceFlowSystem.cs:385` (→1024) | – | – |
| Demand | `ResidentialDemandSystem.cs:284` (16/off 10), `CommercialDemandSystem.cs:349` (16/off 4), `IndustrialDemandSystem.cs:769` (16/off 7) | 16 | – |
| Zone spawn | `ZoneSpawnSystem.cs:1047` (16/off 13), `CommercialSpawnSystem.cs:167`, `IndustrialSpawnSystem.cs:238` | 16 | – |
| Building upkeep, level, abandon | `BuildingUpkeepSystem.cs:1064` (16/day) | 1024 | 16384 |
| Condemned / abandoned | `CondemnedBuildingSystem.cs:72` (64), `DestroyAbandonedSystem.cs:103` (4096) | – | – |
| Construction | `BuildingConstructionSystem.cs:626` | 64 | – |
| Rent, renters | `RentAdjustSystem.cs:626`, `PropertyRenterSystem.cs:221` (16/day) | 1024 | 16384 |
| Taxes, wages | `TaxSystem.cs:237`, `PayWageSystem.cs:267` (32/day) | 512 | 8192 |
| Budget | `BudgetApplySystem.cs:84` (1024/day → 256), `BudgetSystem.cs:78` (32768), `CityServiceUpkeepSystem.cs:492` (64/day → 256) | – | – |
| Fees, loans | `ServiceFeeSystem.cs:350` (2048), `UtilityFeeSystem.cs:414` (128), `LoanUpdateSystem.cs:100` (32/day → 8192) | – | – |
| XP | `XPAccumulationSystem.cs:110` (32/day → 8192) | 8192 | – |
| Statistics | `CityStatisticsSystem.cs:399` (8192 / offset 0), `StatisticTriggerSystem.cs:213` | 8192 | – |
| Cell maps | `LandValueSystem.cs:292` (32/day → 8192), `AirPollutionSystem.cs:84` / `NoisePollutionSystem.cs:56` / `GroundPollutionSystem.cs:55` (128/day → 2048), `PopulationToGridSystem.cs:100` | – | – |
| Utilities | `ElectricityFlowSystem` (`kUpdateInterval=128`, 3-phase state machine, l.311, 456-470), `WaterPipeFlowSystem` (128) | 128 | – |
| Garbage/mail/crime accumulation | `GarbageAccumulationSystem.cs:353` (1024), `MailAccumulationSystem.cs:359` (64), `CrimeAccumulationSystem.cs:254` (64) | – | – |
| Service dispatch | `*DispatchSystem` (16; group counts in `SimulationUtils.cs:29-57`) | 16 | – |
| Service buildings | `HospitalAISystem.cs:723` (256/off 16), `FireStationAISystem.cs:609` (256/off 112) … | 256 | – |
| Hazards | `FireHazardSystem.cs:277` (4096), `WeatherHazardSystem.cs:167` (2048), `CrimeCheckSystem.cs:282` (1/day → 16384), `FireSimulationSystem.cs:677` (64) | – | – |
| Time/climate | `TimeSystem`, `ClimateSystem` (1, no override), `WindSystem.cs:55` (1) | 1 | – |

The full list of 271 overrides was extracted from all `override int GetUpdateInterval` occurrences in `Game/` (Game.Simulation plus a few in Game.Prefabs, Game.Triggers, Game.Tools, Game.Effects).

---

## 3. Randomness

### 3.1 `Game.Common.RandomSeed` (`Game/Game.Common/RandomSeed.cs`) [V]

The type keeps one static `Unity.Mathematics.Random` stream, seeded from the wall-clock time (`DateTime.Now.Ticks`)
when the type is initialized (l.8). `Next()` draws a new seed from that stream (l.12-18). `GetRandom(index)` derives a
generator for one index by mixing the seed with the index multiplied by a fixed odd constant (l.20-24).

- **One global stream, seeded from wall-clock time at type initialization.** It is never reseeded on load.
- `RandomSeed.Next()` has **196 call sites**; 115 of the files are in `Game.Simulation`, and others are in `Game.Rendering` (4 files), `Game.Tools` (7), `Game.Audio`, `Game.Objects`, `Game.Triggers`, `Game.Serialization`, and more. Examples: tools at `Game/Game.Tools/NetToolSystem.cs:5576`, `GenerateObjectsSystem.cs:1841`, `CoveragePreviewSystem.cs:405`. So **local rendering, tool use and audio advance the same stream that seeds simulation jobs** [V call sites; the consequence is I].
- Typical job usage: the system calls `RandomSeed.Next()` once per update on the main thread (`BirthSystem.cs:298`), and the job derives `random = m_RandomSeed.GetRandom(unfilteredChunkIndex)` (`BirthSystem.cs:112`). **The per-job stream is keyed by chunk index, not by entity.** [V]
- **[I]** `unfilteredChunkIndex` is the chunk's position in the query's matching-chunk list. It depends on archetype creation order and on chunk allocation and compaction history: entity create/destroy order, component add/remove, swap-back on removal. It also counts client-local structural changes, such as `Temp` tool entities and UI entities that create archetypes. Two machines with identical gameplay can therefore draw different random numbers for the same entity.

### 3.2 Per-entity and per-frame seeds [V]

- `Citizen.m_PseudoRandom` (ushort, serialized; `Game/Game.Citizens/Citizen.cs:11, 119-141`) is set once at creation from `RandomSeed.Next().GetRandom(0)` (`Game/Game.Citizens/CitizenInitializeSystem.cs:79, 92, 326`). `Citizen.GetPseudoRandom(CitizenPseudoRandom reason)` (Citizen.cs:38) hashes it with fixed reason constants (`CitizenPseudoRandom.cs`: WorkOffset, Death, SleepOffset, …). Example: the death roll at `DeathCheckSystem.cs:196`. **These rolls are deterministic per citizen once the citizen exists.**
- `PseudoRandomSeed` (ushort) on objects and vehicles, taken from `CreationDefinition.m_RandomSeed` (`Game/Game.Tools/GenerateObjectsSystem.cs:1183, 1381`; `CreationDefinition.cs:19`). `ZoneSpawnSystem` fills it from its job random (`ZoneSpawnSystem.cs:628`).
- Frame-seeded examples:
  - `IndustrialSpawnSystem.cs:127` uses `new Random(m_SimulationFrame)`.
  - `FindJobSystem.cs:342` uses `new Random(1 + (m_SimulationFrame ^ citizen.m_PseudoRandom))`.
  - `HouseholdSpawnSystem.cs:255` uses `RandomSeed.Next().GetRandom((int)frameIndex)`, which is still global-stream based.
  - `ExtractorAISystem.cs:232` uses a constant `new Random(346745637u)`.
- **`UnityEngine.Random`:** `ResourceExporterSystem.cs:473` uses `RandomSeed.Next().GetRandom(UnityEngine.Random.Range(0, int.MaxValue))`, which is engine-global RNG state.

---

## 4. Multithreading model (what runs where) [V]

- Simulation systems schedule Burst jobs: `IJobChunk` with `ScheduleParallel` plus `IJob` "apply" steps (e.g. `BirthSystem.cs:303, 312`). There are 1379 `[BurstCompile]` attributes in `Game/`, all with default arguments. No `FloatMode`/`FloatPrecision` override appears anywhere in the codebase (grep).
- Cross-system data flows through system-owned containers handed out with `GetXxx(out JobHandle deps)` and `AddWriter`/`AddReader` (e.g. `CityStatisticsSystem.GetStatisticsEventQueue`, `BudgetApplySystem.cs:110-114`). **There are 214 `NativeQueue<T>.ParallelWriter` fields in 123 `Game.Simulation` files** (grep).
- Pattern: a parallel job enqueues, then a single `IJob` drains with `TryDequeue` and applies changes. Examples:
  - `GarbageCollectorDispatchSystem.DispatchVehiclesJob` appends `ServiceDispatch` to the vehicle's buffer in dequeue order (`GarbageCollectorDispatchSystem.cs:403-428`, scheduled at l.558-559).
  - `ZoneSpawnSystem.SpawnBuildingJob.SelectLocation` picks the max-priority location with strict `>`, so ties go to the first dequeued (`ZoneSpawnSystem.cs:600-611`).
  - `BuildingUpkeepSystem` uses level-up/level-down queues (`BuildingUpkeepSystem.cs:143-145, 316-318, 403-456`).
- Pathfinding runs on dedicated job "threads": `PathfindQueueSystem` uses `m_MaxThreadCount = max(1, JobWorkerCount/2)` (`Game/Game.Pathfind/PathfindQueueSystem.cs:337`) and polls `JobHandle.IsCompleted` (l.725-742).

---

## 5. Sources of nondeterminism (catalogue)

| # | Source | Evidence | Severity for lockstep |
|---|---|---|---|
| N1 | Global RNG seeded from `DateTime.Now`, shared with rendering, tools and audio | `RandomSeed.cs:8`; call sites in Game.Rendering/Tools/Audio | Fatal unless patched |
| N2 | Job RNG keyed by `unfilteredChunkIndex` | `BirthSystem.cs:112` and most jobs | Fatal: needs identical chunk layout [I] |
| N3 | `NativeQueue.ParallelWriter` drain order equals thread completion order | 214 fields; `GarbageCollectorDispatchSystem.cs:403-428`, `ZoneSpawnSystem.cs:600-611` | Fatal for any order-sensitive consumer (dispatch order, tie-breaks, first-come property and job assignment) |
| N4 | Float sums in nondeterministic order (statistics events, float `m_Change`) | `BudgetApplySystem.cs:38-55`; `CityStatistic` stores `double` (`Game/Game.City/CityStatistic.cs`) | Bit-level drift [I] |
| N5 | Pathfinding results applied when the worker *finishes*, not at a fixed tick. `m_ResultFrame` is only a deadline (`frameIndex + maxDelayFrames`) | `PathfindResultSystem.cs:208-271` (applies on `PathfindActionState.Completed`); `PathfindSetupSystem.cs:427-428`; result system runs once per rendered frame in MainLoop | Fatal |
| N6 | Pathfind setup spreading uses `Time.deltaTime * smoothSpeed` | `PathfindSetupSystem.cs:326-331` | Fatal |
| N7 | Ticks per rendered frame depend on `deltaTime`, measured job cost and pathfinding progress; commands played back once per frame | `SimulationSystem.cs:228-257`; `EndFrameBarrier.cs:34-43` | Fatal |
| N8 | GPU water simulation: time step from `Time.smoothDeltaTime`, async readbacks | `WaterSystem.GetTimeStep` (`WaterSystem.cs:1406-1424`), `CheckReadbacks` (l.1426+) | Fatal for flood, hydro and water-pump logic. Readers include `FloodCheckSystem`, `WaterDamageSystem`, `WaterPumpingStationAISystem`, `PowerPlantAISystem`, `LandValueSystem` (grep of `GetSurfaceData`) |
| N9 | Terrain CPU heights come from `AsyncGPUReadback` after terraforming | `TerrainSystem.cs:2308-2314, 2615-2635, 609-631` | Medium (consistent after settling, unless GPUs differ) [I] |
| N10 | Entity indices differ between machines (local `Temp` entities from tools, UI/render entities) | `GenerateObjectsSystem` creates `Temp` entities (`GenerateObjectsSystem.cs:989-993`) | Fatal for anything ordered or hashed by `Entity`. **No Entity can be used as a network ID** |
| N11 | Burst default float mode can emit different code per CPU target (FMA, SSE/AVX dispatch) | no `FloatMode` overrides anywhere (grep) | Possible cross-CPU drift [I] |
| N12 | Systems in `ModificationEnd` run once per rendered frame: `XPSystem`, `MilestoneSystem`, `CityServiceBudgetSystem`, `DevTreeSystem`, `TriggerSystem`, `IconCommandSystem`, `ServiceRequestSystem` | SystemOrder.cs:270-302 | Frame-rate dependent |
| N13 | Auto-assigned update offsets depend on the registered system set (mods) | `UpdateSystem.Refresh` l.309-380 | Requires an identical mod set |
| N14 | Engine-global `UnityEngine.Random` | `ResourceExporterSystem.cs:473` | Fatal unless patched |
| N15 | `UpdateFrame` group assignment = least-populated group, in chunk iteration order | `UpdateGroupSystem.cs:247-262, 522-536` | Fatal if chunk order differs (follows from N2/N10) |
| N16 | Local pauses: error dialogs, lightning, photo mode, loading | §1.2 | Session-control issue |

Deterministic or derivable parts [V]: date and time (`TimeSystem`, a pure function of `frameIndex` + `TimeData`); climate (`ClimateSystem.SampleClimate` evaluates prefab curves on the date, `ClimateSystem.cs:607-623`, though `OverridableProperty` overrides exist, l.625-640); per-citizen pseudo-random rolls (§3.2); integer electricity and water flow on a given graph (`ElectricityFlowEdge.m_Capacity/m_Flow` are `int`, `ElectricityFlowEdge.cs:15-17`; graph indices are still creation-order dependent).

---

## 6. Simulation domains, authoritative state, change rates, volumes

### 6.0 Where authoritative state lives

1. **ECS components and buffers** on entities. These are serialized via `ISerializable` on each component (e.g. `Citizen.Serialize`, `Citizen.cs:119-141`).
2. **Managed system fields.** These systems implement `ISerializable` [V, grep of class declarations]:
   - `SimulationSystem` (frameIndex), `CitySystem` (City entity ref)
   - `TaxSystem` (`NativeArray<int> m_TaxRates`, 92 entries, `TaxSystem.cs:193, 276-322, 345`)
   - `ServiceFeeSystem`, `TradeSystem`
   - `ResidentialDemandSystem` (household/building demand + factor arrays, `ResidentialDemandSystem.cs:385-403`), `CommercialDemandSystem`, `IndustrialDemandSystem`
   - `CityStatisticsSystem`, `CityProductionStatisticSystem`, `CityProductionCapacityCalculationSystem`, `ProductionSpecializationSystem`, `ProductionChainDataSystem`
   - `CountHouseholdDataSystem`, `CountCompanyDataSystem`, `CountWorkplacesSystem`, `CountStudyPositionsSystem`, `CountResidentialPropertySystem`, `CountVehicleDataSystem`, `CountConsumptionSystem`, `CountCityStoredResourceSystem`
   - `ElectricityFlowSystem`, `ElectricityStatisticsSystem`, `ElectricityTradeSystem`, `WaterPipeFlowSystem`, `WaterStatisticsSystem`, `WaterTradeSystem`
   - `GarbageAccumulationSystem`, `DeathCheckSystem`, `FireHazardSystem`, `CitizenHappinessSystem`, `ResourceAvailabilitySystem`, `TransportLineSystem`, `TransportUsageTrackSystem`
   - `ClimateSystem`, `SnowSystem`, `WindSimulationSystem`, `WaterSystem`, `TerrainSystem`, `PlanetarySystem`
   - `CellMapSystem<T>` (all cell maps), `MapTileSystem`, `TriggerSystem`, `NameSystem`, `MilestoneUISystem`, `CityInfoUISystem`
3. **GPU textures**: water and terrain (WaterSystem, TerrainSystem).

### 6.1 Population (households, citizens, birth/death/move-in/out)

- **Authoritative components [V]:**
  - `Citizen` (22 bytes serialized: flags, wellbeing, health, counters, birthday, pseudo-random; `Citizen.cs:9-29, 119-141`)
  - `Household` (flags, `m_Resources`, consumption, income, …; `Household.cs:6-22`)
  - `HouseholdMember`, `HouseholdCitizen` buffer, `CurrentBuilding`, `Worker` (workplace, commute, level, shift; `Worker.cs`), `Student`, `HealthProblem`, `TravelPurpose`, `CurrentTransport`, `Criminal`, `CrimeVictim`, `CitizenHappiness`
  - `PropertyRenter` (property, rent; `Game/Game.Buildings/PropertyRenter.cs`)
  - the `Resources` buffer (`Resource` is a `ulong` enum + `int` amount; `Game/Game.Economy/Resources.cs`, `Resource.cs:3`)
  - the `Population` singleton on the City entity (`m_Population`, `m_PopulationWithMoveIn`, averages; `Game/Game.City/Population.cs`), written by `CountHouseholdDataSystem`/`ResourceBuyerSystem`
- **Systems [V]:**
  - `HouseholdSpawnSystem` creates households from `householdDemand` (`HouseholdSpawnSystem.cs:229-255, 136`)
  - `HouseholdFindPropertySystem` → `PropertyProcessingSystem` (RentAction queue, single-threaded drain, `PropertyProcessingSystem.cs:317-370`)
  - `HouseholdMoveAwaySystem` (adds `Deleted`, `HouseholdMoveAwaySystem.cs:198`)
  - `BirthSystem` (`SpawnBaby`, `BirthSystem.cs:151-153`), `DeathCheckSystem` (sets `HealthProblemFlags.Dead`, `DeathCheckSystem.cs:120-139, 196`)
  - `AgingSystem`, `GraduationSystem`, `LookForPartnerSystem`/`PartnerSystem`/`DivorceSystem`/`LeaveHouseholdSystem`
  - `CitizenBehaviorSystem`, `CitizenHappinessSystem`, `FindJobSystem`, `FindSchoolSystem`, `TouristSpawnSystem`, `CommuterSpawnSystem`
  - `HouseholdAndCitizenRemoveSystem` (Modification2) and `HouseholdInitializeSystem` (Modification4) / `CitizenInitializeSystem` (Modification5) (SystemOrder.cs:157, 230)
- **Change rate [V from intervals]:** every citizen is touched by `CitizenBehaviorSystem` every 256 ticks, and every household by `HouseholdBehaviorSystem` every 1024 ticks. Household money (`Resources[Money]`) changes on almost every economic tick: wages every 512 ticks, purchases via `ResourceBuyerSystem` every 16-tick slice. Births and deaths are rolled per citizen every 16384 ticks.
- **Volume [E] for a 100k-population city:**
  - ~100k resident citizens plus ~5-15k tourist and commuter citizens
  - ~40-45k households
  - ~110 B/citizen (Citizen 22 + HouseholdMember + CurrentBuilding + Worker/Student + TravelPurpose…) → **~10-14 MB raw**
  - ~120-150 B/household incl. a Resources buffer of ~4-6 entries → **~5-6 MB raw**

### 6.2 Companies and economy (resources, profitability, trade)

- **Components [V]:** `CompanyData`, `Profitability` (`m_Profitability` byte, `m_LastTotalWorth`; `Game/Game.Companies/Profitability.cs`), `WorkProvider`, `Employee` buffer, `ServiceAvailable`, `ResourceSeller`, `BuyingCompany`, `TradeCost` buffer, `StorageCompany`, `TransportCompany`, the `Resources` buffer, and `PropertyRenter`.
- **Systems [V]:** `ProcessingCompanySystem`/`ExtractorCompanySystem`/`ServiceCompanySystem` (64-tick slices, each company every 1024), `CommercialAISystem`/`IndustrialAISystem`/`OfficeAISystem` (512), `CompanyProfitabilitySystem`/`CompanyDividendSystem` (16384), `CompanyMoveAwaySystem` (1024), `ResourceBuyerSystem`, `ResourceExporterSystem`, `StorageTransferSystem`, `TradeSystem` (2048, system-level state), `CommercialFindPropertySystem`/`IndustrialFindPropertySystem`, `CommercialSpawnSystem`/`IndustrialSpawnSystem`, `WorkProviderSystem`.
- **Volume [E]:** ~3-8k companies × ~150-300 B (resources buffer + employees buffer refs) → **~1-2 MB**. Company resources change every 1024 ticks per company plus purchases and deliveries.

### 6.3 Buildings: spawning, leveling, abandonment, condemnation

- **Components [V]:**
  - `Building` (road edge, curve position, option mask, flags; `Game/Game.Buildings/Building.cs`)
  - `BuildingCondition.m_Condition` (`BuildingCondition.cs`)
  - `PrefabRef`, `Renter` buffer, `PropertyOnMarket`/`PropertyToBeOnMarket`, `Abandoned`, `Condemned`, `UnderConstruction` (with `m_NewPrefab` for level-up), `Efficiency` buffer, `ElectricityConsumer`, `WaterConsumer`, `GarbageProducer`, `CrimeProducer`, `MailProducer`, `CitizenPresence`, `Destroyed`, `OnFire`
- **Spawning [V]:** `ZoneSpawnSystem` (16/off 13) gathers candidates in parallel into queues and selects the best per zone type (`ZoneSpawnSystem.cs:156-164, 600-611`). It then creates a **`CreationDefinition` + `ObjectDefinition` entity** (`ZoneSpawnSystem.cs:615-668`), which is the **same mechanism as player tools**: `GenerateObjectsSystem` (Modification1) turns definitions into real entities. A host could forward the definition (prefab, transform, `m_RandomSeed`) and let clients' vanilla pipeline build the building and its sub-objects [I; verify that sub-object generation depends only on the definition seed].
- **Leveling and abandonment [V]:** `BuildingUpkeepSystem.BuildingUpkeepJob` (1024-tick slices) accumulates `m_Condition`. Past `levelingCost` it requests level-up resources and queues a level-up. Below `-abandonCost` it queues a level-down or abandonment (`BuildingUpkeepSystem.cs:173-287`). `LevelupJob` picks a new prefab with a random roll (`SelectSpawnableBuilding`, l.721, 761) and adds `UnderConstruction{m_NewPrefab}` (l.726-728). `LeveldownJob` adds `Abandoned` (l.481). `CondemnedBuildingSystem` (64) and `DestroyAbandonedSystem` (4096) remove buildings. `ZoneCheckSystem` (`Game/Game.Buildings/ZoneCheckSystem.cs`) condemns buildings after zoning edits, which is tool-driven.
- **Volume [E]:** ~10-20k buildings × ~150-300 B → **~2-6 MB**. Structural events (spawn, level, abandon, demolish) number **a few per minute** in a growing city. Condition and efficiency drift continuously.

### 6.4 Budget, money, taxes, loans, service fees

- **State [V]:**
  - `PlayerMoney` on the City entity: private `int m_Money`, `m_Unlimited`, clamp ±2e9 (`Game/Game.City/PlayerMoney.cs`)
  - `Loan` (`m_Amount`, `m_LastModified`; `Game/Game.Simulation/Loan.cs`), `Creditworthiness`
  - `ServiceFee` buffer (`PlayerResource`, `float m_Fee`; `Game/Game.City/ServiceFee.cs`)
  - `TaxSystem.m_TaxRates` (92 ints, system field)
  - per-service budgets (`ServiceBudgetData`, `CityServiceBudgetSystem`)
  - the `City` entity also carries `MilestoneLevel`, `XP`, `DevTreePoints`, `Policy`/`CityModifier`/`TradeCost`/`SpecializationBonus` buffers, `DangerLevel` (`CitySystem.cs:76-110`)
- **Money flow [V]:**
  - `BudgetApplySystem` (every 256 ticks) adds `(Σincome − Σexpense)/1024` from `CityServiceBudgetSystem`'s arrays (`BudgetApplySystem.cs:30-60, 84-87`).
  - `CityServiceBudgetSystem` (ModificationEnd, so per rendered frame) derives those arrays from statistics, tax rates, fees, loans and upkeep (`CityServiceBudgetSystem.cs:100-215`).
  - Other `PlayerMoney` writers: `CityServiceUpkeepSystem`, `LoanUpdateSystem`, `MapTilePurchaseSystem`, `MilestoneSystem` (milestone reward, `MilestoneSystem.cs:103-105`), `ResourceProducerSystem`, `ResidentAISystem`, `GameModeGovernmentSubsidiesSystem` (grep).
  - Player-initiated changes: `LoanSystem` (Modification1), tax and fee UI, tile purchase.
- **Rate [V]:** money changes every 256 ticks (~4 s at speed 1), plus events. **Volume:** < 1 KB of singleton state.

### 6.5 City statistics

- `CityStatisticsSystem` (8192 / offset 0) drains a `NativeQueue<StatisticsEvent>` fed by many systems and stores samples in `CityStatistic` buffers (`double m_Value`, `m_TotalValue`) on per-statistic entities found via `m_StatisticsLookup` (`CityStatisticsSystem.cs:288, 327, 399-407`; `Game/Game.City/CityStatistic.cs`). Buffers hold up to ~32 samples (l.327) [V].
- **Volume [E]:** ~100-300 statistic keys × 32 samples × 16 B → **~50-150 KB**, changing every 8192 ticks.

### 6.6 Milestones, XP, development points, unlocks

- `XPAccumulationSystem` (8192, GameSimulation) and `XPBuiltSystem`/`NetXPSystem` (ModificationEnd; XP for things built) enqueue `XPGain`. `XPSystem` (ModificationEnd) adds them to `XP.m_XP` (`XPSystem.cs:22-37`).
- `MilestoneSystem` (ModificationEnd) compares XP with thresholds. It increments `MilestoneLevel.m_AchievedMilestone`, emits `MilestoneReachedEvent` + `Unlock` event entities, and adds the milestone reward to `PlayerMoney` and `Creditworthiness` (`MilestoneSystem.cs:71-110`).
- `DevTreeSystem` adds dev-tree points **for every `MilestoneReachedEvent` it sees** (`Game/Game.City/DevTreeSystem.cs:30-42`). `UnlockSystem` (MainLoop) processes `Unlock` events.
- **[V+I] Double-application hazard:** if a client both receives replicated `DevTreePoints`/`PlayerMoney` *and* runs `MilestoneSystem`/`DevTreeSystem` on a replicated event, rewards are applied twice. **Replicate either the event or the resulting state, never both.**

### 6.7 Time, date, weather, climate

- `TimeSystem` runs every tick and is a pure function of `frameIndex`, the `TimeData` singleton and `TimeSettingsData` (§1.3). `ClimateSystem` samples prefab curves at `normalizedDate` (`ClimateSystem.cs:453, 465-483, 607-623`) and selects weather prefabs (l.711-813).
- `WeatherPhenomenonSystem` (16/off 0) and `WeatherHazardSystem` (2048) spawn lightning, tornadoes and hail events randomly.
- **Clients can compute time, date and climate locally** once `frameIndex`, `TimeData` and the climate prefab are equal. Weather *events* must come from the host.

### 6.8 Traffic and pathfinding

- **State [V]:**
  - Vehicles: `Car`/`CarCurrentLane`/`CarNavigation`/`PathOwner`/`PathElement` buffer/`Target`/`Moving`/`Transform` + 4×`TransformFrame`
  - Humans: `HumanCurrentLane`, …
  - Parked cars, lane occupancy and reservations (`NetLaneReservationSystem`), traffic lights, flow and density (`TrafficFlowSystem` 512, `NetEdgeDensitySystem` 1024), which feed back into pathfind costs
- **Pathfinding [V]:** queries are queued with `PathfindSetupSystem.GetQueue(system, maxDelayFrames, spreadFrames)` (`PathfindSetupSystem.cs:410-435`). They are solved on worker threads (`PathfindQueueSystem`) and applied by `PathfindResultSystem` (MainLoop) as soon as `m_State == Completed` (`PathfindResultSystem.cs:208-271`). The simulation stalls if a deadline is about to pass (§1.2).
- **Volume [E]:**
  - Moving vehicles ~5-15k, active pedestrians ~5-20k, parked cars ~20-40k.
  - Each moving object writes 1 `TransformFrame` (48 B raw) per 16 ticks.
  - At speed 1 that is 3.75 updates/s per object → **~15-35k objects × 3.75/s × ~14 B quantized ≈ 0.8-1.8 MB/s for full replication**. That is too much, so interest management is mandatory (§9.5).
  - Paths: a `PathElement` buffer of tens of elements per vehicle, changing on each new trip.

### 6.9 City services (garbage, health, police, fire, education, mail, deathcare, transport)

- Each service follows the pattern *accumulation → request entity → dispatch → vehicle AI → service building AI*:
  - Accumulation: `GarbageAccumulationSystem`, `CrimeAccumulationSystem`, `MailAccumulationSystem`, `HealthProblemSystem`.
  - Requests: `ServiceRequest` + `GarbageCollectionRequest`/`HealthcareRequest`/…, handled by `ServiceRequestSystem` (ModificationEnd).
  - Dispatch: `*DispatchSystem` (16).
  - Vehicle AI: `*AISystem` (16, offsets per vehicle type, `SimulationUtils.cs:63-90`).
  - Building AI: `*FacilityAISystem`/`HospitalAISystem`/`SchoolAISystem` (256, distinct offsets).
- Coverage: `ServiceCoverageSystem` uses pathfinding `CoverageAction`s, so it is asynchronous [V, `Game.Pathfind/CoverageAction.cs`, `PathfindQueueSystem.Enqueue(CoverageAction…)` l.516-523].
- Education: `ApplyToSchoolSystem` (512), `FindSchoolSystem`, `GraduationSystem`, `SchoolAISystem`.
- **Volume [E]:** request entities ~ thousands; service vehicles ~ hundreds to a few thousand; building `Efficiency` buffers change every 32-256 ticks.

### 6.10 Events (fires, disasters, accidents, crime)

- Event entities live in `Game.Events`: `Fire`, `OnFire`, `Destruction`, `TrafficAccident`/`AccidentSite`, `Crime`/`Criminal`, `WeatherPhenomenon`, `Flood`/`Flooded`, `InDanger`, `Ignite`/`Impact`/`Submerge` command components, and journal entities.
- Spawners: `FireHazardSystem` (4096), `WeatherHazardSystem` (2048), `CrimeCheckSystem` (16384 per citizen), `VehicleOutOfControlSystem`, `FloodCheckSystem` (GPU water based).
- Progression: `FireSimulationSystem` (64), `AccidentSiteSystem` (64), `CollapsedBuildingSystem`, `WaterDamageSystem`, `EventTickSystem` (256).
- **Rate:** rare, discrete, high gameplay importance. They must be host-only and reliably replicated.

### 6.11 Utilities and cell maps (supporting domains)

- Electricity and water are integer flow graphs with their own graph entities (`ElectricityFlowEdge`/`Node`, `WaterPipeEdge`/`Node`), solved every 128 ticks in phases.
- Cell maps (`CellMapSystem<T>`): `kTextureSize` is 256² for air, ground and noise pollution, ground water and natural resources; 128² for land value, availability, soil water and terrain attractiveness; 64² for population, wind and ambience grids (`Game.Simulation/*System.cs` `kTextureSize`).
- **Volume [E]:** roughly 0.5-2 MB raw in total, each map refreshed every 2048-8192 ticks. Fields are smooth, so they compress very well.

### 6.12 Aggregate estimate for a 100k city [E]

| Tier | Content | Raw size | Change rate |
|---|---|---|---|
| Singletons and system state | money, taxes, fees, loans, XP, milestones, demand, budget arrays, statistics | ~0.2-0.5 MB | seconds to minutes |
| Persistent per-entity sim state | citizens, households, companies, buildings, services, events | **~25-60 MB** | each entity every 1024-16384 ticks; money and resources faster |
| Moving objects | vehicles, humans: transform frames, lane state | ~2-5 MB | every 16 ticks per object |
| Fields | cell maps | ~1-2 MB | every 2048-8192 ticks |
| Static or structural | nets, lanes, zones, areas, trees, props | tens of MB | only on edits (tools, spawn) |

These figures argue for a **save-file-based initial join** plus **tiered delta replication**, not a continuous per-entity mirror of everything.

---

## 7. Supporting facts for a follower client

- **Disabling a system works [V].** `SystemBase.Update()` calls `OnUpdate()` only if `Enabled && ShouldRunSystem()`, and calls `OnStopRunning` when it is turned off (`Unity.Entities/Unity.Entities/SystemBase.cs:38-70`). `UpdateSystem` still calls `Update()`, which then becomes a no-op. Third-party mods already disable vanilla systems this way, e.g. `UniqueAssetTrackingSystem.Enabled = false` (`thirdparty/Anarchy/Anarchy/Patches/UniqueAssetTrackingSystemOnCreatePatch.cs:23`), and patch vanilla `OnUpdate` with Harmony (`thirdparty/Anarchy/Anarchy/Patches/ToolbarUISystemOnUpdatePatch.cs`).
- **Producer/consumer queues [V].** Many systems own persistent `NativeQueue`s that are drained only in their own `OnUpdate`, e.g. `CityStatisticsSystem` statistics events, `XPSystem.m_XPQueue` (`XPSystem.cs:57, 77-80, 95`), `TriggerSystem` action buffers, `PropertyProcessingSystem.m_RentActionQueue` (l.685, 744-748). **[I]** Disabling a consumer while a producer still runs leaks memory without bound. Disable producer/consumer pairs together, or keep consumers running with empty input.
- **Initialization systems overwrite fields on new entities [V].** `CitizenInitializeSystem` assigns `m_PseudoRandom` and more (`CitizenInitializeSystem.cs:79-92`). `HouseholdInitializeSystem`, `Game.Vehicles.InitializeSystem`, `Game.Creatures.InitializeSystem` and `CompanyInitializeSystem` run in Modification4/5 (SystemOrder.cs:157, 189, 212, 230, 233). Host values must be applied *after* these run, or written so the initializers do not override them.
- **UI reads system fields [V].** For example, `ZoneSpawnSystem` and the UI read `ResidentialDemandSystem.buildingDemand` (`ResidentialDemandSystem.cs:282`; `ZoneSpawnSystem.cs:1101`). If these systems are disabled on clients, their fields go stale unless the host refreshes them, e.g. through each system's own `Deserialize`.
- **Notifications and icons** come from simulation systems through `IconCommandSystem` (ModificationEnd, SystemOrder.cs:271). With the simulation disabled, clients get no icon updates, so icons must be replicated [I].
- **Rendering needs `frameIndex` and `frameTime` to advance** (§2.2). A client that stays paused shows no interpolation and no day/night progress.

---

## 8. Critical question: is deterministic lockstep feasible?

**Verdict: no, not with the vanilla simulation.** Lockstep requires bit-identical state evolution from identical inputs. The code violates this in at least seven independent, structural ways: N1, N2, N3, N5, N7, N8 and N10 in §5. Making it deterministic would require:

1. Replacing `RandomSeed.Next()` with a per-(system, frame) deterministic seed and isolating tools and rendering from it. That alone is patchable.
2. Making every job seed per *entity* instead of per chunk index. That means rewriting about 100 Burst jobs; decompiled jobs cannot be patched without recompiling them.
3. Sorting the drain of more than 200 parallel queues, or making the consumers order-independent. Also a rewrite.
4. Making pathfinding complete at a fixed tick, which means synchronous waits that destroy performance.
5. Fixing ticks per frame and command-buffer playback per tick.
6. Excluding or replacing the GPU water simulation.
7. Guaranteeing identical Entity allocation, which is impossible while clients use tools locally.
8. Guaranteeing identical Burst code generation across CPUs.

Even after all of that, any single divergence cascades through a chaotic economic simulation. Desync recovery would still need full state transfer, which is the very capability a host-authoritative design is built on. **Lockstep therefore costs more than it saves.** [I, based on the verified sources]

## 9. Recommended split: host-authoritative with a follower client

### 9.1 Authority model

- **Host:** runs the complete vanilla simulation, the authoritative clock (`frameIndex`), speed and pause, all random rolls, pathfinding and the GPU water simulation. It is the authority for every persistent component and every system-level `ISerializable` state.
- **Clients:** do **not** simulate gameplay. They send *intents* (tool applications, UI actions such as tax, fee, loan and policy changes, and speed requests) to the host. The host validates, applies and broadcasts the result. Clients render, interpolate, animate, play audio, preview tools with local `Temp` entities, and run the vanilla Modification pipeline on host-sent changes.

### 9.2 What clients may simulate locally (cosmetic or derivable)

| Keep running on clients | Why it is safe |
|---|---|
| `TimeSystem`, `ClimateSystem` (curve sampling), `PlanetarySystem`, `LightingSystem` | pure function of `frameIndex` + `TimeData` [V §1.3, §6.7] |
| All Rendering, PreCulling, Cleanup phase systems; `ObjectInterpolateSystem`; animation and effects; `AudioManager` | presentation only |
| `StreetLightSystem`, `WindSystem`/`WindSimulationSystem` (visual wind), `WetnessSystem`, `DirtynessSystem`, `SnowSystem` (visual surface), `ZoneAmbienceSystem`, `TrafficAmbienceSystem` | cosmetic; host remains the authority for gameplay inputs (e.g. wind feeding pollution) [I: confirm these do not feed gameplay on clients] |
| `WaterSystem` GPU simulation (visual only) | host decides floods, water damage, pumping and hydro output; client water is purely visual and may differ slightly |
| Modification1..5 systems (Generate*, References, Initialize*, `UpdateGroupSystem`, graph systems) | process host-originated structural changes the same way as local ones |
| Tool systems in preview mode | local `Temp` entities only; final apply goes through the host |

### 9.3 What must come from the host

- **Structural changes:** entity creation and deletion for buildings, citizens, households, companies, vehicles, creatures, events and requests, plus component add/remove (`Abandoned`, `Condemned`, `UnderConstruction`, `OnFire`, `Destroyed`, `HealthProblem` …). Use **network IDs**, never `Entity` indices (N10).
  - For player edits and zone spawns, forward the **definition** (`CreationDefinition`/`ObjectDefinition`/`NetCourse` with seeds), so clients' vanilla `Generate*` systems rebuild identical geometry [I].
  - Simulation-created agents (households, citizens, vehicles) are replicated as entities with their component payload.
- **Discrete gameplay events:** milestones, unlocks, dev-tree points, notifications and icons, chirps, fires, disasters, accidents, crimes, building level-ups and abandonment.
- **Singletons and system state:** `PlayerMoney`, `Loan`, `Creditworthiness`, `XP`, `MilestoneLevel`, `DevTreePoints`, `Population`, `ServiceFee`, `TaxSystem` rates, demand systems, `CityServiceBudgetSystem` arrays (or the inputs to them), `CityStatisticsSystem` samples, production and trade statistics.
- **The clock:** `frameIndex` and speed. Clients never decide their own tick count.

### 9.4 Reusing the game's own serialization for system-level state [I]

Most system-state holders already implement `Serialize<TWriter>`/`Deserialize<TReader>` (§6.0). Example: `ResidentialDemandSystem` writes household demand, building demand, three factor arrays and the last values (`ResidentialDemandSystem.cs:385-403`). Driving these through a custom `IWriter`/`IReader` would let the host ship a compact snapshot of every such system every N ticks, and let clients `Deserialize` it into their disabled copies. UI and rendering would then see correct values with no per-field glue. This needs verifying: `Colossal.Serialization.Entities` writer and reader contracts, how `Context`/version is handled, and whether entity references inside system state can be remapped to network IDs.

### 9.5 Moving objects

Options, ordered by fidelity per byte:

1. **Snapshot `TransformFrame`s with interest management.** The host sends, for objects near each client's camera, the frame written on that tick (each object updates once per 16 ticks, §2.2). Clients write it into the matching `TransformFrame` slot. The vanilla `ObjectInterpolateSystem`, with its built-in 32-tick lag, then interpolates for free. Example budget [E]: 3k visible objects × 3.75 Hz × ~14 B ≈ **160 KB/s at speed 1**, scaling linearly with speed and dropping further with delta coding.
2. **Lane-state replication.** Send `CarCurrentLane` (lane network ID, curve position, speed) at a lower rate plus path changes. Clients run a lightweight cosmetic mover along lanes and accept corrections. This is cheaper, but needs custom movement code.
3. **Far-field.** Do not replicate distant objects individually. Show them only when the camera approaches, or show synthetic ambient traffic.

### 9.6 How to run the follower simulation without breaking the game

1. **Clock.** Keep `SimulationSystem` running on clients so `frameIndex`, `frameTime`, `smoothSpeed`, `RenderingSystem` and `TimeSystem` behave normally. Add a Harmony prefix on `SimulationSystem.OnUpdate` (or replace it) that sets the tick count to `min(desired, hostFrame − clientFrame)`, i.e. *chase the host's frame*. Suppress local pause sources (§1.2, N16). `frameIndex` has a private setter, so for hard resyncs set it through reflection or through `SimulationSystem.Deserialize`, which also resets `m_Timer` (l.140-146).
2. **Disable gameplay systems.**
   - Iterate the `GameSimulation` phase registrations: everything in SystemOrder.cs:307-602 except the cosmetic whitelist in §9.2.
   - Also disable: `XPBuiltSystem`, `NetXPSystem`, `XPSystem`, `MilestoneSystem`, `DevTreeSystem` (if `DevTreePoints` are replicated), `TriggerSystem`/`CreateChirpSystem`/`LifePathEventSystem` (if chirps and life paths are replicated), `AchievementTriggerSystem`/`EventAchievementTriggerSystem`, `ServiceRequestSystem`, the `LoadSimulation` warm-up, and `PathfindSetupSystem` queues (no simulation queries exist on clients anyway).
   - Build the list from a declared table rather than by reflection-scanning everything, so a game update fails loudly instead of silently.
3. **Keep producer/consumer pairs consistent** (§7). Either disable the consumer only when all producers are disabled, or keep the consumer enabled and let it drain empty queues.
4. **Apply host deltas at a fixed point.** Use a custom system registered with `UpdateBefore<ModificationSystem>`-style ordering in MainLoop, or in `Modification1` via `UpdateAt`/`UpdateBefore`. It creates and destroys entities and writes components, adding `Updated`/`BatchesUpdated` tags where the vanilla pipeline needs them. Re-apply authoritative payloads after the Modification4/5 initializers for newly created entities (§7).
5. **System-state sync** through §9.4 at low frequency, e.g. every 256-1024 ticks, plus on-change for player-driven values such as taxes and fees.
6. **Inspection on demand.** When a client opens an info panel for an entity, subscribe to that entity's full component set at a higher rate. Everyone sees the same numbers without replicating every citizen continuously.
7. **Convergence and anti-entropy.** The host periodically sends per-domain checksums (e.g. hashed per `UpdateFrame` group, so the work is spread over 16 ticks like the game does). Clients request re-sends of mismatching groups. Fall back to a fresh save-file snapshot on large divergence.

### 9.7 Remaining risks for a follower design

- Some systems hidden in non-simulation phases may still mutate gameplay state on clients, for example `Game.Buildings.InitializeSystem`, `DamageSystem`, `DestroySystem` (Modification2) and `ZoneCheckSystem`. Each Modification-phase system needs a classification audit.
- Entity references inside replicated components (e.g. `Worker.m_Workplace`, `PropertyRenter.m_Property`, `PathElement.m_Target`) must be remapped through the network-ID table. Out-of-order arrival requires deferred patching.
- A client that runs tools locally creates `Temp` entities and archetypes. This is fine under host authority, but rules out any reliance on matching entity indices or chunk order.

---

## 10. Open questions

1. Is `GenerateObjectsSystem` and sub-object/sub-net generation deterministic given only the `CreationDefinition` (prefab, transform, `m_RandomSeed`)? Its job also uses `RandomSeed.Next().GetRandom(jobIndex)` (`GenerateObjectsSystem.cs:979, 1421`, apparently only for animation speed). If yes, forwarding definitions is far cheaper than replicating the resulting entities.
2. Can the `Colossal.Serialization.Entities` `IWriter`/`IReader` be implemented by a mod for in-memory system-state snapshots? How do `Context`/version gates (e.g. `PlayerMoney.Deserialize` checks `reader.context.version`) behave outside a save or load?
3. Exactly which Modification-phase systems produce gameplay effects (money, XP, notifications) from structural changes? Examples: `XPBuiltSystem`, `CityServiceBudgetSystem`'s estimates, `ServiceUpgradeSystem`.
4. Does disabling `GameSimulation` systems on clients break any UI binding that expects their containers to be initialized? Most create containers in `OnCreate`, which still runs.
5. What are measured real-world numbers for a 100k city: entity counts per archetype, save size, moving-object counts? They are needed to size bandwidth tiers. They require an in-game measurement build (out of scope for this static analysis).
6. Burst determinism across CPUs is irrelevant under host authority, but would matter if any client-side prediction (e.g. local vehicle movement, option 9.5-2) is attempted.
