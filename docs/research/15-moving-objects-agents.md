# 15 – Moving objects and agent visibility on follower clients

> **Historical research report.** Written before and during the implementation; it describes the game, not the
> mod, and is not maintained. For how the mod works, see [docs/ARCHITECTURE.md](../ARCHITECTURE.md). Code is cited
> by file and member and described in prose; no game code is reproduced.

Research for **Skyline Together** (clean-room, host-authoritative co-op mod for Cities: Skylines II).
All paths are relative to the root of the game's decompiled assemblies (most are under `Game/`).
Line numbers refer to the decompiled files as read on 2026-10-10.

Builds on: 03 §2.2, §6.8, §9.5 (frame slicing, 32-tick interpolation lag, snapshot idea), 04 §4.7-4.8 (agent and
vehicle taxonomy), 08 §13 (bandwidth reality check).

Legend:

- **[V]** verified: read in the decompiled code at the cited location (arithmetic derived directly from verified code is also marked [V]).
- **[I]** inference or design proposal: follows from verified code plus general Unity DOTS knowledge, not proven line by line. Numeric estimates are marked **[I, estimate]**.

---

## 0. TL;DR

1. **Interpolation reads exactly four per-entity inputs**: the `UpdateFrame` shared component (`u`, 0..15), the 4-slot
   `TransformFrame` buffer, `RenderingSystem.frameIndex` and `RenderingSystem.frameTime`. It shows the state of
   **exactly `R − 32 + frameTime`**, where `R` is the render tick. The two slots it blends are the ones the movers wrote
   at host ticks `F1` and `F1+16`, with `F1 ≡ u (mod 16)` (§2.2). [V]
2. **The client's `UpdateFrame` must equal the host's.** Otherwise every entity is shown shifted in time by `(u − u')`
   ticks: up to 15 ticks, which is ~3.5 m at 50 km/h and ~7.5 m at 108 km/h. Cars in a queue would overlap or open gaps.
   The game assigns `UpdateFrame` to new entities in `UpdateGroupSystem` and re-randomises it unless the entity carries the
   `Applied` tag (`Game.Simulation/UpdateGroupSystem.cs:408-446`). So **spawn on the client with the host's `UpdateFrame`
   plus `Applied`** (§4.4). [V for mechanism, I for recipe]
3. **The vanilla ring only tolerates a 32-tick window.** A frame for host tick `T` may be written into slot `(T>>4)&3`
   only while the reader's render tick is in `[T−16, T+16)`. Earlier writes corrupt the frame being displayed, later
   writes arrive after it was needed (§2.3). The host stays inside this window because `RenderingSystem` keeps
   `R` within ±15 ticks of the simulation tick (`Game.Rendering/RenderingSystem.cs:175-221`). [V] A network client cannot
   hit a ±16-tick window reliably, especially at speed 4 (±67 ms). **Recommendation: buffer frames in a client-side
   staging ring and copy them into the vanilla slots "just in time" in `PreCulling`, before `PreCullingSystem`. Run the
   client clock `D` ticks behind the host, with `D` ≥ latency + jitter, and extrapolate if a frame is missing** (§2.5). [I]
4. **Never tag a moving object `Updated` on the client to "refresh" it.** `UpdateGroupSystem.MovingObjectsUpdatedJob`
   resets all four frames of any `Updated` + `InterpolatedTransform` entity to its static `Transform`
   (`UpdateGroupSystem.cs:561-615`). `Game.Vehicles.InitializeSystem` re-runs on every `Updated` vehicle and overwrites
   navigation, lane offset and, for unspawned vehicles, `Transform` (`Game.Vehicles/InitializeSystem.cs:1330`, `255-352`,
   `606-739`). Use `EffectsUpdated`/`BatchesUpdated` instead. [V]
5. **Effects (lights, sirens, boarding sounds) are only re-evaluated for entities tagged
   `Updated`/`Deleted`/`EffectsUpdated`/`BatchesUpdated`** (`Game.Effects/EffectControlSystem.cs:1087-1098`). On the host,
   movers and AIs add `EffectsUpdated` when light bits in the 4-frame window change (`Game.Simulation/CarMoveSystem.cs:265-279`)
   or when `CarFlags.Emergency` toggles (`Game.Simulation/PoliceCarAISystem.cs:904-914, 982-985`). **The client receiver must
   reproduce this**, or sirens and headlights stay stale. [V]
6. **Several Modification-phase systems would create or mutate gameplay entities on a follower client and must be
   disabled or neutralised there:**
   - `ParkedVehiclesSystem` spawns parked service vehicles for any `Created` service building without `Applied` (`Game.Vehicles/ParkedVehiclesSystem.cs:1683-1699, 1727-1790`).
   - `ObjectEmergeSystem` spawns creatures and edits citizens when buildings or vehicles with passengers are deleted (`Game.Objects/ObjectEmergeSystem.cs:570-610, 228-370`).
   - `GroupSystem` forms travel groups (`Game.Creatures/GroupSystem.cs:499-548`).
   - `FixParkingLocationSystem` relocates parked cars (`Game.Vehicles/FixParkingLocationSystem.cs:1043-1067, 1200-1271, 1385-1389`).
   - `FixLaneObjectsSystem` rewrites current lanes (`Game.Net/FixLaneObjectsSystem.cs:331-742`).
   - `ParkingLaneDataSystem` and `PathOwnerTargetMovedSystem`.

   [V for behaviour, I for the decision]
7. **Hit-testing (clicking a vehicle) needs either a `LaneObject` buffer entry on its lane or an entry in the moving
   search tree** (`Game.Common/RaycastSystem.cs:948-1023`, `Game.Objects/RaycastJobs.cs:870-1016`). The moving search tree
   only contains objects that are **not** on a lane with a `LaneObject` buffer (`Game.Serialization/LaneObjectSystem.cs:93-104`,
   `Game.Vehicles/ReferencesSystem.cs:343-353`), so the host **cannot** select vehicles by area with it alone. Recommended
   host selection: a Burst chunk scan filtered by `UpdateFrame(T & 15)`, which touches only the 1/16 of moving objects
   whose frame was just written (§7). [V for tree semantics, I for recipe]
8. **Info panels refresh every 256 simulation ticks**, or immediately when the selected entity is `Updated`/`BatchesUpdated`
   (`Game.UI.InGame/SelectedInfoUISystem.cs:181, 383`; `Game.UI/UIUpdateState.cs:28-37`). That cadence is the natural
   subscription rate for on-demand inspection. Aligning snapshots to `frameIndex % 256 == 0` on every peer gives identical
   numbers (§8). [V for cadence, I for alignment]
9. **Bandwidth** [I, estimate]:
   - Full replication of ~21k moving objects, quantised: ~0.9 / 1.7 / 3.5 MB/s at speeds 1 / 2 / 4. Infeasible.
   - With interest management (~3k objects), quantised: ~125 / 250 / 500 KB/s.
   - Same, with half-rate far field: ~85 / 165 / 330 KB/s.
   - Global existence (spawn/despawn/swap stream, no motion) for all agents: ~10-20 KB/s at speed 1.
   - Recommendation: **existence global, motion local** (§9).

---

## 1. Data model of a moving object

| Component | Fields (file:line) | Raw size | Persisted? | Role |
|---|---|---|---|---|
| `Game.Objects.TransformFrame` (buffer, `InternalBufferCapacity(4)`) | `float3 m_Position`, `float3 m_Velocity`, `quaternion m_Rotation`, `TransformFlags m_Flags` (uint), `ushort m_StateTimer`, `TransformState m_State` (byte), `byte m_Activity` (`Game.Objects/TransformFrame.cs:7-22`) | 12+12+16+4+2+1+1 = **48 B** per slot, 192 B per entity [V fields; I exact size: sequential layout without padding] | Only `m_StateTimer`, `m_State` and `m_Activity` are saved (`TransformFrame.cs:57-75`). Positions are rebuilt on load (§3.1). [V] | 4-deep ring that movers write and `ObjectInterpolateSystem` reads |
| `Game.Objects.Transform` | `float3 m_Position`, `quaternion m_Rotation` (`Game.Objects/Transform.cs:10-12`) | 28 B | yes | Simulation pose. Rendering uses it for coarse culling (§2.4) |
| `Game.Objects.Moving` | `float3 m_Velocity`, `float3 m_AngularVelocity` (`Game.Objects/Moving.cs:9-11`) | 24 B | yes | Velocity. Read by audio and the "Driving" animation clip state (§3.4) |
| `Game.Rendering.InterpolatedTransform` | `float3`, `quaternion`, `TransformFlags` (`Game.Rendering/InterpolatedTransform.cs:11-15`) | 32 B | `IEmptySerializable` (l.9) | Render pose, written each frame for near-camera entities |
| `Game.Simulation.UpdateFrame` (shared) | `uint m_Index` | 4 B per chunk | yes (03 §2.2) | Phase `u` of the 16-tick update cycle |
| `Game.Vehicles.TrainBogieFrame` (buffer, cap 4) | `Entity m_FrontLane`, `Entity m_RearLane` (`Game.Vehicles/TrainBogieFrame.cs:6-12`) | 16 B per slot | `IEmptySerializable` | Lane per slot used to snap bogies to rails |
| `Game.Objects.Stopped` | tag (`Game.Objects/Stopped.cs:8`) | 0 | `IEmptySerializable` | Marks the "stopped" archetype (parked, stumbling, ...) |

`TransformFlags` (`Game.Objects/TransformFlags.cs:6-26`) carries every per-frame visual bit: main/extra/rear/interior/
warning/work lights, turn signals, braking, reversing, boarding left/right, pantograph, signal animations, taking off,
landing, flying. `TransformState` (`Game.Objects/TransformState.cs:3-12`) is `Default/Idle/Move/Start/End/Action/Done`. [V]

---

## 2. Q1 – Interpolation inputs and timing

### 2.1 Writers: what a frame contains and when it is written

- `CarMoveSystem.OnUpdate` filters `UpdateFrame(frameIndex & 0xF)` and writes slot `(frameIndex >> 4) & 3`
  (`Game.Simulation/CarMoveSystem.cs:420-423`). Its query excludes `TripSource`, `Temp`, `Deleted` and `OutOfControl`
  (l.414), so vehicles still waiting inside a building get no frames. [V]
- Content of a car frame, with `dt = 4/15 s` (16 ticks, l.81-82):
  - `m_Position = Transform + v·dt/2`, the midpoint of the step (l.181-182, 207-212). `Transform` then advances by `v·dt` (l.262).
  - `m_Rotation` is the heading towards the navigation target.
  - Flags (l.213-261): rear lights always on. Main or high beams when `dayLightBrightness ± rand < 0.5`, with the random
    value from `PseudoRandomSeed.kLightState` (l.93, 214-225). Braking, reversing and turn signals come from
    `CarCurrentLane.m_LaneFlags`. Warning/work/interior lights and signal animations come from `Car.m_Flags`.

  [V]
- `HumanMoveSystem` uses the same slicing (`Game.Simulation/HumanMoveSystem.cs:767-769, 803`). `m_State` is `Move` when
  `m_MaxSpeed ≥ 0.1` and there is no target activity, otherwise `Idle` plus `m_Activity` (l.359-367).
  `ObjectUtils.UpdateAnimation` fills `m_StateTimer` (l.379). When the activity changes it emits a
  `SubObjectsUpdated` event (hand-held props) and `BatchesUpdated` for activity 24 (l.406-424). [V]
- `TrainMoveSystem` writes both `TransformFrame` and `TrainBogieFrame` into the same slot index
  (`Game.Simulation/TrainMoveSystem.cs:97-105, 192-263, 370`). [V]
- Other writers of `TransformFrame`: `CarTrailerMoveSystem`, `WatercraftMoveSystem`, `AircraftMoveSystem`,
  `AnimalMoveSystem`, `ObjectCollisionSystem`, `VehicleOutOfControlSystem` (all RW handles; see the grep summary in §10).
  All run in the `GameSimulation` phase (`Game.Common/SystemOrder.cs:337-357`). [V]

### 2.2 Reader: `ObjectInterpolateSystem.CalculateUpdateFrames`

`ObjectInterpolateSystem.CalculateUpdateFrames` (`Game.Rendering/ObjectInterpolateSystem.cs:4332-4338`) subtracts
the chunk's update frame and a fixed lag of two 16-frame periods from the frame index. Bits 4-5 of the result select
the first of the four history slots and the next slot (modulo 4) is the second; the low four bits plus the fractional
frame time, scaled to 0..1 over 16 frames, give the blend position between the two slots.

- **Inputs [V]:**
  - `simulationFrameIndex` and `simulationFrameTime` are **`RenderingSystem.frameIndex` and `frameTime`**, not the simulation's (`ObjectInterpolateSystem.cs:3041-3042, 3094-3095, 3129-3130`).
  - `updateFrameIndex` is `PreCullingData.m_UpdateFrame`, which `PreCullingSystem` copies from the chunk's `UpdateFrame` shared component (`Game.Rendering/PreCullingSystem.cs:660, 690, 927-932`).
  - An overload also returns `updateFrameChanged = ±1` when the slot pair advanced since the previous render frame (`m_PrevFrameIndex`, l.4340-4350, 3154). Human animation uses it to switch clips (l.4158-4219).
- **Blend [V]:** `CalculateTransform` evaluates a cubic Bézier `p1, p1 + v1·4/45, p2 − v2·4/45, p2` and slerps the
  rotation. Flags switch from frame 1 to frame 2 at `framePosition ≥ 0.5` (l.4352-4360). `4/45 = dt/3` makes it a
  Hermite curve, so **velocity is part of the visual result** and cannot be dropped without changing the curve.
- **Who calls it [V]:**
  1. `UpdateTransformDataJob`, for each `PreCullingData` with `NearCamera` and without `Temp`/`Relative` (l.249-264): plain objects (l.1353-1386), skeletons (l.1388-1533) and animated characters (l.1535-1584).
  2. `UpdateTrailerTransformDataJob`, for car trailers and train layouts (l.1702-1774, 1874-1947).
  3. `UpdateQueryTransformDataJob`, only for `Temp` entities and icon-carrying movers (query l.2927-2956).
  4. `PreCullingSystem.QueryCullingJob`, for culling bounds (`PreCullingSystem.cs:1117`).
  5. `SelectedInfoUISystem.GetInterpolatedPosition`, for selection and follow-camera positions (`Game.UI.InGame/SelectedInfoUISystem.cs:653-668`).
  6. `LineVisualizerSection`, for vehicle positions on a transport line (`Game.UI.InGame/LineVisualizerSection.cs:893-898`).

**Derivation [V, arithmetic on the code above].** Let `n = R − u − 32` and let `F1 = u + 16·(n >> 4)`, the largest tick
`≤ R − 32` with `F1 ≡ u (mod 16)`. A mover wrote tick `F1` into slot `(F1 >> 4) & 3 = (n >> 4) & 3` (because
`0 ≤ u < 16`), which is exactly `updateFrame1`. Slot `updateFrame2` holds tick `F1 + 16`. Also
`F1 + (n & 15) = u + n = R − 32`, so **the rendered pose is the host state at tick `R − 32 + frameTime`, independent of
`u`**, provided the writer and reader agree on `u`.

### 2.3 The 32-tick write window

For a frame of host tick `T`, written into slot `s = (T >> 4) & 3` [V, arithmetic]:

- It is read as frame 2 for `R ∈ [T+16, T+32)` and as frame 1 for `R ∈ [T+32, T+48)`. It must therefore be present
  **before `R` reaches `T+16`**.
- Before it is written, slot `s` holds tick `T−64`, which is still being read for `R ∈ [T−48, T−16)`. Writing `T` while
  `R < T−16` overwrites a frame on screen and causes a visible jump.
- **Safe write window: `R ∈ [T−16, T+16)`, 32 ticks wide.**

On the host this always holds. When `T` is written, the simulation tick is `T`, and `RenderingSystem.PrepareRendering`
keeps `R = simFrame + offset` with the offset clamped to ±15 ticks (`num = 15`, `RenderingSystem.cs:175, 193-221`).
`PrepareRendering` runs at the start of `PreRenderSystem`, before the `PreCulling` phase
(`Game.Rendering/PreRenderSystem.cs:20-24`; `SystemOrder.cs:63`). [V]

**Real-time width of the window** [V, arithmetic: 60 ticks/s × speed nominal, 03 §1.2]:

| Speed | Ticks per second | 32-tick window |
|---|---|---|
| 1 | 60 | 533 ms |
| 2 | 120 | 267 ms |
| 4 | 240 | 133 ms (±67 ms) |

### 2.4 Other per-entity inputs that matter on the client

- **`Transform` must be roughly correct.** `QueryCullingJob` first tests `Transform.m_Position` with a 277.78 m slack, and
  only then interpolates frames to decide `PassedCulling` (`PreCullingSystem.cs:1152-1171`). An entity whose `Transform`
  is more than ~280 m stale never becomes visible. [V]
- **`Moving.m_Velocity`** drives audio speed (`Game.Audio/AudioManager.cs:1370-1372`) and whether "Driving" clips play
  (`ObjectInterpolateSystem.cs:540-558`). [V]
- **`PseudoRandomSeed`** selects light flicker phase and animation variants (`ObjectInterpolateSystem.cs:1507-1513,
  1544, 4146`). It must match the host for identical visuals (04 §3). [V]
- **`Human.m_Flags`** feeds `ActivityCondition` for clip choice (`ObjectInterpolateSystem.cs:4557-4564`).
  **`CurrentVehicle` + vehicle prefab `ActivityLocationElement`** select props when entering or exiting (l.4534-4555). [V]

### 2.5 What happens if the client's `UpdateFrame` differs from the host's

With client phase `u'` ≠ host phase `u` [V, arithmetic]:

- The reader computes `F1' ≡ u' (mod 16)`, but the slot it reads holds a frame of a tick `≡ u`.
- Displayed time becomes `R − 32 + (u − u')`: a constant per-entity time shift of up to ±15 ticks (0.25 s of simulation time).
- The write window from §2.3 shifts by the same amount.

Visual effect [I]: relative position errors of `|u − u'| × v / 60` per tick of difference, up to ~3.5 m at 13.9 m/s and
~7.5 m at 30 m/s. Neighbours on the same lane with different errors overlap or open gaps. The follow camera and the
selection marker use the same maths (`SelectedInfoUISystem.cs:663-667`), so they stay consistent with what the client
renders, but not with what the host shows. **Requirement: replicate `UpdateFrame` exactly** (§4.4).

### 2.6 Smooth motion with late or out-of-order snapshots

Recommended design [I]:

1. **Stamp every frame with its host tick `T`.** `T` is implicit per packet if the host sends, for tick `T`, only
   entities with `u = T & 15` (§7.2).
2. **Keep a client staging ring** per replicated entity: the last 4-8 `(T, frame)` entries in a native hash map or
   side table. Insert ignores entries older than the newest stored entry for the same slot, so reordering is harmless.
3. **Run a JIT slot writer** in `SystemUpdatePhase.PreCulling`, ordered before `PreCullingSystem` (after
   `RenderingSystem.frameIndex` has been computed for this frame, `PreRenderSystem.cs:22-23`). For every replicated
   moving entity, at least those near the camera:
   - compute `n = R − u − 32` and `F1 = u + 16·(n>>4)`;
   - write staged `frame(F1)` and `frame(F1+16)` into slots `(F1>>4)&3` and `((F1>>4)+1)&3`;
   - prefill `frame(F1+32)` into the next slot, because `R` can advance up to ~8+ ticks before the next frame (03 §1.2).

   The vanilla 4-slot ring then always holds exactly what the reader will use, and arrival time no longer matters as
   long as data is early.
4. **Extrapolate missing frames.** If `frame(F1+16)` has not arrived, synthesise
   `pos = p(F1) + v(F1)·4/15`, with the same rotation, flags and state, and `m_StateTimer + 1` for humans. Mark it
   synthetic and overwrite it when the real frame arrives (a small correction). Without this, the slot still holds the
   frame from `T−64` and **the object visibly snaps backwards by 64 ticks**.
5. **Clock lag.** Run the client's `SimulationSystem.frameIndex` `D` ticks behind the host, extending 03 §9.6's "chase
   the host" with a target lag, where `D ≥ (one-way latency + jitter) × 60 × speed + 8`. Example: at speed 4 with
   120 ms latency + jitter, `D ≈ 37` ticks ≈ 0.15 s. Time and date lag by `D` ticks, which is negligible against
   262144 ticks/day. Alternative without lagging the clock: the JIT writer writes `frame(F1 − D')` with `D'` a multiple
   of 16, delaying only motion. That is more complex, because spawns and despawns must then be delayed by `D'` too.
6. **Keep `Transform` and `Moving` coherent.** After writing slots, set `Transform.m_Position = p_latest + v_latest·2/15`
   (the inverse of the loader formula `TransformFrameSystem.cs:127-128` and of `CarMoveSystem.cs:181-182, 262`),
   `Transform.m_Rotation = q_latest` and `Moving.m_Velocity = v_latest`. Do **not** add `Updated` (§3.1).
7. **Reproduce side effects of the movers:**
   - Add `EffectsUpdated` when the OR of `MainLights|ExtraLights|WarningLights|WorkLights` over the 4 slots changes, to the vehicle or to every `LayoutElement` vehicle (mirrors `CarMoveSystem.cs:265-299`).
   - Emit `SubObjectsUpdated` event entities when a human's activity set in the ring changes and the prop differs, and `BatchesUpdated` for activity 24 (mirrors `HumanMoveSystem.cs:406-424`; event archetype `Event + SubObjectsUpdated`, l.761).

`CalculateUpdateFrames` is a static helper inlined into Burst jobs (four call sites in jobs, see §2.2). Patching it to
change the 32-tick lag is therefore not reliable. [I]

---

## 3. Q2 – Supporting systems and which must run on clients

### 3.1 `TransformFrame` buffer lifecycle

| Event | What happens | Citation |
|---|---|---|
| Archetype creation | `MovingObjectPrefab.GetArchetypeComponents` adds `UpdateFrame` to every moving-object prefab, and `TransformFrame` + `InterpolatedTransform` **only if `Moving` is in the set** | `Game.Prefabs/MovingObjectPrefab.cs:20-29` [V] |
| Entity `Created` (non-Temp) | `UpdateGroupSystem.CheckCreated` assigns `UpdateFrame` (unless `Applied`), `ResizeUninitialized(4)` and fills all 4 slots with `new TransformFrame(transform)` (zero velocity), and sets `InterpolatedTransform = Transform` | `Game.Simulation/UpdateGroupSystem.cs:408-485` [V] |
| Temp (tool preview) created | Copies the original's frames, or static frames | `UpdateGroupSystem.cs:325-406` [V] |
| **Entity `Updated` (not `Created`) with `InterpolatedTransform`** | **All 4 slots reset to `Transform`.** For humans, state and activity are kept, `m_StateTimer = k`, and slots are rotated using the current phase | `UpdateGroupSystem.cs:724` (query), `561-615` [V] |
| Moving → parked | Removes `Moving, TransformFrame, InterpolatedTransform, Swaying, CarNavigation, CarNavigationLane, CarCurrentLane, PathOwner, Target, Blocker, PathElement`; adds `ParkedCar, Stopped, Updated` | `Game.Simulation/PersonalCarAISystem.cs:1128-1142` [V] |
| Parked → moving | Removes `ParkedCar, Stopped`; adds the moving set + `Updated` (variants for personal car, taxi, service car, trailer) | `Game.Events/ImpactSystem.cs:472-529` (same sets used by AIs) [V] |
| Game load | `TransformFrameSystem` (`Deserialize` phase): resize to 4 if needed, `InterpolatedTransform = Transform`, then back-extrapolate `pos_i = Transform − v·(((offset − i) mod 4)·4/15 + 2/15)`. Humans and animals keep their state and activity from navigation | `Game.Serialization/TransformFrameSystem.cs:49-131`; `SystemOrder.cs:835` [V] |

Consequences [I]:

- A client that joins via the host's save gets plausible frames for free, because the loader runs the same rebuild.
- From then on, only the JIT writer (§2.6) should touch the ring.
- The writer must run after Modification5, since `UpdateGroupSystem` resets frames there for `Created` entities
  (`SystemOrder.cs:214`). The `PreCulling` phase satisfies this, because `ModificationSystem` precedes `PreRenderSystem`
  in MainLoop (`SystemOrder.cs:60, 63`).

### 3.2 `TrainBogieFrame`

- Written per slot by `TrainMoveSystem` (`TrainMoveSystem.cs:97-105`). [V]
- Reset to the current front and rear lanes by `Game.Vehicles.InitializeSystem.UpdateBogieFrames` on every `Updated`
  train (`Game.Vehicles/InitializeSystem.cs:577-580, 784-803`) and by `TrainBogieFrameSystem` on load
  (`Game.Serialization/TrainBogieFrameSystem.cs:27-44`). [V]
- Read in `UpdateInterpolatedLayoutAnimations` for the slots `updateFrame1/2`
  (`ObjectInterpolateSystem.cs:1874-1947`). Bogies snap to the nearest point on those lane curves within 2 m, and are
  left unsnapped if the lane has no `Curve` (l.2027-2068). [V]
- **Client:** replicate bogie lanes per frame using the lane identity from 04 §4.2, or accept unsnapped bogies, which
  degrades gracefully: `positionValid = false`, l.2067. [V/I]

### 3.3 `InterpolatedTransform`

Written each render frame for `NearCamera` entities only (`ObjectInterpolateSystem.cs:252`). Far entities keep a stale
value, which is why `SelectedInfoUISystem` re-interpolates from frames when the entity is not near the camera
(`SelectedInfoUISystem.cs:656-668`). Nothing needs to be sent for it. [V]

### 3.4 Animation

- Characters: `UpdateInterpolatedAnimationBody` chooses body clips from `frame.m_State`, `frame.m_Activity`,
  `ActivityCondition(Human)` and the prop ID (`CurrentVehicle` + vehicle prefab `ActivityLocationElement`). Non-moving
  clips are timed from `m_StateTimer` (`ObjectInterpolateSystem.cs:4135-4252`). Clip transitions are triggered by
  `updateFrameChanged` (l.4158, 4193). [V]
- Vehicles and skeletal props: procedural bones (steering, swaying, pantograph, catenary height via the lane search
  tree) (`ObjectInterpolateSystem.cs:1388-1533, 3282+, 3625-3690`). "Driving" clip playback depends on
  `Moving.m_Velocity ≠ 0` (l.540-558). [V]
- Lights: `AnimateInterpolatedLight` uses `TransformFlags`, `RenderingSystem.frameIndex/frameTime` and a
  `PseudoRandomSeed` stream (l.1505-1527, 3889-4115). [V]
- Infrastructure systems, all presentation-only [V for registration, I for "presentation-only"]:
  - Cleanup: `AnimatedSystem` (`SystemOrder.cs:52`).
  - Rendering: `AnimatedSystem.Prepare` (l.662), `InitializeAnimatedSystem`, `InitializeBonesSystem`, `InitializeBoneHistoriesSystem`, `InitializeLightsSystem` (l.653-656), `ProceduralSkeletonSystem`, `ProceduralEmissiveSystem` (l.674-675).

### 3.5 Effects and audio driven by vehicle state

`EffectControlSystem` (PreCulling, `SystemOrder.cs:637`) re-evaluates enabled effects only for entities with
`Updated`, `Deleted`, `EffectsUpdated` or `BatchesUpdated` (`EffectControlSystem.cs:1087-1098`). [V]

Conditions read from moving objects (`Game.Effects/EffectControlData.cs`) [V]:

| Condition | Source | Line |
|---|---|---|
| `Parked` | `ParkedCar`/`ParkedTrain` | 274 |
| `Emergency` (sirens) | `Car.m_Flags & Emergency` or `Aircraft` flags | 286 |
| `TakingOff`, `Landing`, `Flying` | frame flags | 304-380 |
| `Stopped` | tag | 382 |
| `Boarding` | `PublicTransport`/`Taxi`/`CargoTransport`/`PersonalCar` `m_State` | 401 |
| `MainLights`, `ExtraLights`, `WarningLights`, `WorkLights` | OR / AND over the 4 frames | 449-560 |

Producers of `EffectsUpdated` (grep) [V]:

- `CarMoveSystem`, `TrainMoveSystem` (`TrainMoveSystem.cs:218, 260`), `AircraftMoveSystem`
- `AmbulanceAISystem`, `FireEngineAISystem`, `PoliceCarAISystem`, `GarbageTruckAISystem`, `MaintenanceVehicleAISystem`, `FireAircraftAISystem`
- `StreetLightSystem`, `TrafficLightSystem`, `BuildingEfficiencySystem`, `ExtractorFacilityAISystem`
- `InDangerSystem`, `EndangerSystem`, `WeatherPhenomenonSystem`
- `ObjectInterpolateSystem`, editor only (l.1490-1502)

**Client rule [I]:** after applying a replicated change to `Car.m_Flags`, `Aircraft.m_Flags`, any role `m_State`
containing a boarding bit, `ParkedCar` presence or `Stopped`, add `EffectsUpdated` (to each `LayoutElement` vehicle for
trains). Audio needs `Moving.m_Velocity` (§2.4) and the `EnabledEffect` buffers maintained by `EffectControlSystem`.

### 3.6 Client systems that must keep running (moving-object scope)

| Phase | Systems | Why | Citation |
|---|---|---|---|
| LateUpdate | `SimulationSystem` (clock chase, see 03) | `frameIndex`/`frameTime` feed `RenderingSystem` | `SystemOrder.cs:74`; `RenderingSystem.cs:182, 211` [V] |
| MainLoop | `PreRenderSystem` → `RenderingSystem.PrepareRendering` | render tick `R` | `PreRenderSystem.cs:20-24` [V] |
| PreCulling | `PreCullingSystem`, `EffectControlSystem`, `MeshColorSystem` (route colours via `CurrentRoute`, `Game.Rendering/MeshColorSystem.cs:920, 945`), `LightingSystem` | culling, effects, colours | `SystemOrder.cs:632-650` [V] |
| Rendering | `ObjectInterpolateSystem`, `RelativeObjectSystem` (passengers and riders), `AnimatedSystem.Prepare`, `EffectTransformSystem`, `SFXCullingSystem`, `LightCullingSystem`, `ObjectColorSystem`, `BatchInstanceSystem`, the `Initialize*` systems, `Procedural*` | presentation | `SystemOrder.cs:651-690` [V] |
| Cleanup | `AudioManager`, `AnimatedSystem`, `BatchUploadSystem`, `CleanUpSystem` (removes `Applied`/`Updated`/... tags, `Game.Common/CleanUpSystem.cs:24-28`) | presentation and tag hygiene | `SystemOrder.cs:51-54` [V] |
| Modification5 | `UpdateGroupSystem` (frames and `UpdateFrame` for `Created`); `Game.Vehicles.ReferencesSystem` and `Game.Creatures.ReferencesSystem` (derived links, §4.5); `Game.Vehicles.InitializeSystem` and `Game.Creatures.InitializeSystem` (harmless if host values are re-applied, §4.3) | spawn pipeline | `SystemOrder.cs:189, 203, 212-214` [V] |
| ModificationEnd | `UnspawnedSystem` (propagates `Unspawned` to trailers, passengers and relative sub-objects, `Game.Objects/UnspawnedSystem.cs:53-140`), `AnimationUpdateSystem`/`AnimationSystem`, `MeshGroupSystem`, `Game.Effects.SearchSystem` | presentation | `SystemOrder.cs:266-269, 294, 297` [V] |

---

## 4. Q3 – Spawning vehicles, creatures and parked cars on clients

### 4.1 Archetypes

- **Moving vs stopped [V]:** `MovingObjectPrefab.RefreshArchetype` builds:
  - `ObjectData.m_Archetype` from `Moving` + every component's archetype contribution + `Created` + `Updated`;
  - `MovingObjectData.m_StoppedArchetype` from `Stopped` + contributions + `Created` + `Updated` (`MovingObjectPrefab.cs:31-59`; `MovingObjectData` has only `m_StoppedArchetype`, `Game.Prefabs/MovingObjectData.cs:6-9`).

  Components add navigation types only when `Moving` is present. Example: trains add `TrainNavigation`,
  `TrainCurrentLane` and `TrainBogieFrame`, and controllers additionally get `PathOwner`, `PathElement`, `Target`,
  `Blocker` and `TrainNavigationLane` (`Game.Prefabs/TrainPrefab.cs:63-75`).
- **Trains [V]:** four archetypes: moving, stopped, moving controller (+`LayoutElement`) and stopped controller
  (`TrainPrefab.cs:78-130`).
- **Vanilla spawn pattern [V]:** `PersonalCarSelectData.CreateVehicle`:
  1. `CreateEntity(m_ObjectData.m_Archetype or m_MovingObjectData.m_StoppedArchetype)`;
  2. set `Transform`, the role component (`PersonalCar(keeper, state)`), `PrefabRef`, `PseudoRandomSeed`;
  3. optionally add `TripSource(source, delay)` + `Unspawned` (`Game.Prefabs/PersonalCarSelectData.cs:270-278, 285-293`).

  Trailers get `Controller(tractor)` and both are added to the tractor's `LayoutElement` (l.120-149).
- **Recipe [I]:** create client entities from the **same prefab archetype**, after mapping the host prefab to the client
  prefab by `PrefabID` (04 §1.3). All required components then exist and vanilla systems find what they expect.
  `RequiredComponentSystem` runs only in the `Deserialize` phase (`SystemOrder.cs:803`). It is a save-migration helper
  that adds components missing from old saves (e.g. `TrainBogieFrame` l.534, creature `MeshGroup`/`Animated` l.675-676,
  watercraft `Swaying` l.743), so it is **not** a runtime dependency. [V]

### 4.2 Stopped ↔ moving swaps

Swaps are structural: `RemoveComponent(set)` + `AddComponent(set incl. Updated)` (§3.1). On the client, apply the same
type sets in one ECB batch. Afterwards:

- write `Transform` (the parking pose, or the start pose from the host);
- let `UpdateGroupSystem` reset frames (`Updated` on a moving entity, `UpdateGroupSystem.cs:561-615`);
- let the JIT writer fill real frames.

[I, based on V]

### 4.3 Modification-phase initializers and what they overwrite

**`Game.Vehicles.InitializeSystem`** (Modification5, `SystemOrder.cs:212`) runs on **every** `Updated` vehicle, not
only on `Created` ones (query `Updated + Vehicle`, `Game.Vehicles/InitializeSystem.cs:1330`). Per branch [V]:

| Vehicle kind | Overwritten | Lines |
|---|---|---|
| Car (has `CarNavigation`) | `CarNavigation = {m_TargetPosition = Transform.pos}`; `CarCurrentLane.m_LanePosition = random(−0.25, 0.25)` from **`RandomSeed.Next()`** (l.1393, i.e. different on each machine); `TransformTarget` lane flag | 341-350 |
| Car + `Unspawned` + `TripSource` | `Transform` recomputed from spawn locations or path (`InitializeRoadVehicle`); trailers repositioned and `CarTrailerLane` initialised | 275-340, 606-739 |
| Reversing car that cannot reverse | `Transform.m_Rotation` re-aimed; mesh batches reset | 293-310 |
| Watercraft, aircraft | navigation reset; lane flags; spawn-location `Transform` when unspawned | 354-447 |
| Parked car + `Unspawned` + `TripSource` | **picks a parking space randomly** and sets `ParkedCar` + `Transform` | 448-517 |
| Train | spawn path init and carriage placement when unspawned or temp; else `TrainNavigation` pivots reset; `TrainBogieFrame` reset | 518-582 |
| `TreeFixJob` (non-Created, with lane) | re-adds the vehicle to its lane's `LaneObject` buffer and removes it from the moving search tree | 46-83 |

**`Game.Creatures.InitializeSystem`** (Modification5, `SystemOrder.cs:189`), query `Creature + Updated` (l.661) [V]:

- `Transform` from the spawn location when `Unspawned + TripSource` (l.150-198).
- `HumanNavigation` reset (l.202-212).
- `Human.m_Flags`: `Cold` recomputed from the local `ClimateSystem.temperature`, `Homeless` from the household (l.222-235, 703).
- `HumanCurrentLane.m_LanePosition` randomised (l.256), plus lane flags.
- The same for animals.
- May remove `TripSource` via ECB if the source was deleted.

**Rule [I]:** apply host payloads **after** Modification5, or write them so the initialiser has nothing to change:
spawn already spawned, without `TripSource`/`Unspawned` unless the host has them. Re-apply `Transform`,
`ParkedCar`, `CarCurrentLane` and `Human.m_Flags` from the host after the frame in which the entity was created. The
client never needs `CarNavigation`/`HumanNavigation` values, because the movers are disabled.

### 4.4 `UpdateFrame` on spawned entities

- `UpdateGroupSystem.CheckCreated`, non-Temp path [V] (`UpdateGroupSystem.cs:408-465`):
  - **if the chunk has `Applied`**, the existing `UpdateFrame` is kept and only the group counter is incremented;
  - otherwise a new index is chosen: the controller's for layout members, a prefab-forced `UpdateFrameData`, or the least-populated group.

  It is written with `SetSharedComponent` through `ModificationBarrier5` (l.446, 711), and the same index is applied to
  every `LayoutElement` vehicle (l.447-459).
- `Applied` is removed by `CleanUpSystem` at the end of the frame (`CleanUpSystem.cs:24-28`). Its other effects [V]:
  - `PreCullingSystem` adds `PreCullingFlags.Applied` to culling actions (l.1254-1285, 1895-1897);
  - `ParkedVehiclesSystem` skips buildings that have it (§4.6);
  - `NotificationTriggerSystem`/`AreaResourceSystem` use it for tools.
- **Recipe [I]:** create the entity with the host's `UpdateFrame` value and add `Applied`. Alternatively, let vanilla
  pick and correct with a second `SetSharedComponent` after Modification5. That costs a second chunk move and one frame
  with the wrong phase.

### 4.5 Derived links rebuilt by vanilla on `Created`/`Deleted`

**`Game.Vehicles.ReferencesSystem`** (Modification5, `SystemOrder.cs:213`; queries
`Game.Vehicles/ReferencesSystem.cs:953-971`) [V]:

- `InitializeCurrentLaneJob` (`Created` cars): **re-snaps `CarCurrentLane.m_Lane` to the nearest non-master car lane of
  the same carriageway group** of the lane's owner (l.35-102). A replicated lane may be swapped for a neighbour; harmless
  for rendering.
- `Created`:
  - `OwnedVehicle` added to `Owner.m_Owner` (l.287-295);
  - `CarKeeper.m_Car` / `BicycleOwner.m_Bicycle` set from `PersonalCar.m_Keeper` (l.296-316);
  - `GuestVehicle` on the delivery target (l.317-335);
  - `LaneObject` insertion or moving-search-tree insertion per lane type (l.336-470);
  - `RouteVehicle` from `CurrentRoute` (l.470-477).
- `Deleted`: removal of the same links. **For vehicles owned by a `TransportDepot`, it adds
  `m_MaintenanceRequirement += odometer/range`** (l.494-523), a gameplay write. On a client it is harmless only if the
  depot component is overwritten by host replication, not delta-added. [V/I]
- `UpdateLayoutReferencesJob` (`Deleted`): clears `Controller` and `LayoutElement` back-references (l.105-150).
- Minor: `m_ResourceNeedingUpdateQueue` is enqueued (l.328-334) but no dequeue appears in this file (l.942-979). It may
  grow on any peer that creates delivery trucks. [V for what the file shows; I for the leak]

**`Game.Creatures.ReferencesSystem`** (Modification5, query `Creature + (Created|Deleted)`, l.318-327) [V]:

- `OwnedCreature` on the owner (l.80-88);
- `CurrentTransport.m_CurrentTransport` on the citizen or pet (l.89-112);
- `Passenger` on `CurrentVehicle.m_Vehicle` (l.113-122);
- `LaneObject` or moving tree for humans and animals (l.123-154);
- inverse operations on `Deleted` (l.170-229).

**Load-time equivalents** (Deserialize phase): `OwnedVehicleSystem`, `GuestVehicleSystem`, `ControllerSystem`,
`PassengerSystem`, `ResidentSystem`, `PetSystem`, `OwnedCreatureSystem`, `CarKeeperSystem`, `GroupCreatureSystem`,
`LaneObjectSystem` (`SystemOrder.cs:811, 820-828`). `CarKeeper` is `IEmptySerializable` **and `IEnableableComponent`**:
`m_Car` is derived, but the enabled bit is gameplay state and must be replicated (`Game.Citizens/CarKeeper.cs:6-8`;
`Game.Serialization/CarKeeperSystem.cs:46-55`). [V]

**Consequence [I]:**

- Replicate only forward references: `Owner`, `PersonalCar.m_Keeper`, `Target`, `CurrentVehicle`, `Resident`/`Pet`, `CurrentRoute`, `LayoutElement` on controllers, `Controller` on members, and lane fields.
- Let the reference systems rebuild `OwnedVehicle`, `CarKeeper.m_Car`, `GuestVehicle`, `Passenger`, `CurrentTransport`, `OwnedCreature` and `RouteVehicle` from the `Created`/`Deleted` tags.
- Required ordering: owners (household, building, route) must exist before the vehicle's `Created` frame, and `CurrentVehicle` targets must exist before the creature.

### 4.6 Vanilla systems that would spawn or mutate agents on clients (must be gated)

| System (phase, SystemOrder) | Behaviour on a follower | Citation | Action [I] |
|---|---|---|---|
| `ParkedVehiclesSystem` (Mod4B, l.181) | For any `Updated + Building + OwnedVehicle + (Created \| Temp)` **without `Applied`**, spawns parked police cars, fire engines, ambulances, hearses, ... | `Game.Vehicles/ParkedVehiclesSystem.cs:1683-1699, 1727-1790` | Add `Applied` to host-replicated buildings, or keep only its `Temp` (tool preview) path; never let it spawn for `Created` non-Temp |
| `ObjectEmergeSystem` (Mod4B, l.174) | When buildings or vehicles with passengers are deleted, creates creatures, adds `CurrentTransport`/`TravelPurpose`, removes `CurrentBuilding` | `Game.Objects/ObjectEmergeSystem.cs:570-610, 228-370` | Disable; the host replicates the result |
| `GroupSystem` (Mod5, l.204) | Forms household travel groups (`GroupMember`/`GroupCreature`), edits followers' paths | `Game.Creatures/GroupSystem.cs:374-548` | Disable; replicate `GroupMember` if needed |
| `FixParkingLocationSystem` (Mod5, l.219) | On parking-lane or spawn-location updates, moves parked cars, toggles `Unspawned`, adds `Updated` | `Game.Vehicles/FixParkingLocationSystem.cs:1043-1067, 1200-1271, 1385-1389` | Disable |
| `FixLaneObjectsSystem` (Mod5, l.221) | On lane delete or update, rewrites `*CurrentLane` of objects on the lane | `Game.Net/FixLaneObjectsSystem.cs:331-742` | Disable |
| `ParkingLaneDataSystem` (ModEnd, l.251) | Recomputes parking-lane occupancy from `LaneObject`/`ParkedCar` | query in file | Disable; replicate lane occupancy state |
| `PathOwnerTargetMovedSystem` (ModEnd, l.257) | Invalidates paths on `PathTargetMoved` events | query in file | Disable |
| `TripResetSystem` (Mod4) | Reacts to `ResetTrip` events | query in file | Inert without events; disable for safety |

### 4.7 Spawn recipe (client)

[I, built from §4.1-4.6]

1. **Before `ModificationSystem`** (03 §9.6 step 4), in one ECB:
   - `CreateEntity(archetype)`, where the archetype is moving, stopped or controller according to the host flags;
   - `SetSharedComponent(UpdateFrame(u_host))` and add `Applied`;
   - set `PrefabRef`, `PseudoRandomSeed`, the role component (`PersonalCar`, `PublicTransport`, `DeliveryTruck`, ...), `Car.m_Flags`, `Owner`, `Target`, `CurrentRoute`, `Transform`, `Moving`;
   - set the lane component (`CarCurrentLane`/`ParkedCar`/`HumanCurrentLane` with remapped lane), `Unspawned`/`TripSource` as on the host, and `CurrentVehicle`/`Resident`/`Pet` for creatures;
   - for trains and trailers, create all members in the same batch with `Controller` and `LayoutElement`.
2. Let Modification5 run. The reference systems rebuild links, and `UpdateGroupSystem` keeps the phase because of `Applied`.
3. In the same frame's `PreCulling`, or the next frame, the JIT writer fills frames from staging.
4. Despawn: add `Deleted`. The reference systems remove the links, and the despawn of passengers and trailers comes from the host as separate records.

---

## 5. Q4 – Lane and path references: needed or not

| Component | Read on a client by (non-simulation) | Needed? | Recommendation [I] |
|---|---|---|---|
| `CarCurrentLane` / `CarTrailerLane` / `WatercraftCurrentLane` / `AircraftCurrentLane` (lane, curve position, flags) | `Vehicles.ReferencesSystem` (`LaneObject` vs tree on `Created`); `SelectedInfoUISystem.FilterPositionTarget` for **`Unspawned`** vehicles (l.818-858); `LineVisualizerSection` (vehicle progress, l.927-949); `TrafficRoutesSystem` (tool route display) [V] | Partly | Send `m_Lane` (lane identity) + flags at spawn and on lane change (low rate, ~1 per 5-20 s per vehicle [I, estimate]). Skip curve position and per-tick fields. Turn-signal and braking visuals already come from frame flags |
| `HumanCurrentLane` | `Creatures.ReferencesSystem`; `CitizenUIUtils` (`PathEndReached` → citizen state, `Game.UI.InGame/CitizenUIUtils.cs:347-354`); `FilterPositionTarget` [V] | Partly | Lane + `m_Flags` at spawn and on change; on demand for a selected citizen |
| `TrainCurrentLane` | `ReferencesSystem`, `InitializeSystem.UpdateBogieFrames`, `LineVisualizerSection`, `FilterPositionTarget` [V] | Yes for trains | Front and rear lane at spawn and on change. Bogie lanes per frame are optional |
| `ParkedCar` / `ParkedTrain` | `EffectControlData` (Parked, l.274), `VehiclesSection`, `VehicleUIUtils`, `ParkingLaneDataSystem`, `ReferencesSystem` [V] | **Yes** | Persistent state; replicate lane + curve position |
| `PathOwner`, `PathElement`, `PathInformation`, `CarNavigationLane` etc. | `LineVisualizerSection.GetVehiclePosition` (l.816-1016); `TrafficRoutesSystem` (selected building or road routes, `Game.Tools/TrafficRoutesSystem.cs:974-992`); `VehicleCountSection`/`TransportUIUtils` (`PathInformation`) [V] | Only for these UI features | **On demand**: when a line panel is open, or routes are shown for a selected entity, the host streams these for the vehicles concerned (§8.4) |
| `Target` | `CitizenSection`/`AnimalSection` (destination), `VehicleUIUtils.GetDestination`, `Vehicles.ReferencesSystem` (`GuestVehicle`) [V] | Yes | Persistent forward reference; replicate on change |
| `CarNavigation`, `HumanNavigation`, `Blocker` | Overwritten by the initialisers, otherwise only simulation and debug [V] | No | Omit |
| `LaneObject` buffer (on lanes) | Raycast selection (`RaycastJobs.cs:1002-1010`), `RoadsInfoviewUISystem`, `BikesInfoviewUISystem`, `FixLaneObjectsSystem`, `ParkingLaneDataSystem` [V] | For clicking | Either let `ReferencesSystem` maintain it from replicated lanes (updated on lane change), or keep it empty and put every replicated mover into the moving search tree with bounds updated by the JIT writer (simpler, needs no lane ordering) |
| `TrainBogieFrame` | Rendering (§3.2) | Optional | Per-frame lanes, or omit |

---

## 6. Q5 – What still reads navigation data when movers and AIs are disabled

`GameSimulation` movers and AIs to disable: `SystemOrder.cs:311-357`, plus `StuckMovingObjectSystem` (l.517),
`TrafficSpawnerAISystem` (l.525), `VehicleSpawnSystem` (l.546), `CreatureSpawnSystem` (l.551),
`RandomTrafficDispatchSystem` (l.558), `CreatureSpawnerSystem` (l.601), and the whole `LoadSimulation` phase
(l.611-630). [V]

Remaining readers outside simulation, and their failure modes [V for reads; I for impact]:

| Reader (phase) | Reads | Failure mode on a client |
|---|---|---|
| `Vehicles.InitializeSystem` (Mod5) | `CarNavigation`, `CarCurrentLane`, `PathOwner`, `PathElement`, `TripSource`, lanes' `Curve`/`Owner`, `SpawnLocationElement` | No throw found: lookups are guarded by `HasComponent`/`TryGet`, indexers only on archetype-guaranteed components. Mutates `Transform` for unspawned vehicles and randomises the lane offset (§4.3) |
| `Vehicles.ReferencesSystem.InitializeCurrentLaneJob` (Mod5) | `m_OwnerData[lane]`, `m_LaneData[owner]` indexers (l.68-69) when the lane is a `CarLane` | If a remapped lane entity is a `CarLane` without `Owner`/`SubLane` (never in vanilla), the Burst indexer is invalid. **Remap lanes only to live lane entities** |
| `Creatures.InitializeSystem` (Mod5) | `HumanCurrentLane`, `PathElement`, `TripSource` | Mutates as in §4.3 |
| `GroupSystem`, `ObjectEmergeSystem`, `FixLaneObjectsSystem`, `FixParkingLocationSystem`, `PathOwnerTargetMovedSystem`, `ParkingLaneDataSystem` | navigation, paths, lanes | Gameplay mutations (§4.6): **disable** |
| `TrafficRoutesSystem` (Mod2, `SystemOrder.cs:113`) | `PathOwner`/`PathElement`/navigation lanes of all movers, round robin per `UpdateFrame` (l.974-992) | No throw. The routes overlay is empty unless paths exist on the client (§8.4) |
| `SelectedInfoUISystem.TryGetPosition` / follow camera | `TransformFrame`, `UpdateFrame`, `CurrentTransport`, `CurrentVehicle`, `Unspawned`, current lanes, `CurrentBuilding` (l.578-651, 789-872); `OrbitCameraController.TryGetPosition` (`Game/OrbitCameraController.cs:286-289`) | Safe (`TryGet*`). Wrong position if `UpdateFrame` mismatches or frames are stale |
| `LineVisualizerSection` (UI) | `PathOwner`, `CarCurrentLane`, `CarNavigationLane`, `PathElement`, `TransformFrame`, ... (l.816-1016) | Safe. Shows "unknown path" positions when data is missing |
| `CitizenUIUtils.GetStateKey` (UI) | **`GetComponentData<Household>(GetComponentData<HouseholdMember>(e).m_Household)`** (l.238-239) | **Throws** if the household entity or component is missing on the client. All related entities of a selected citizen must exist (§8) |
| `MeshColorSystem` (PreCulling), `RouteRenderSystem` | `CurrentRoute` | Wrong line colour on buses if missing |
| `EffectControlData` (PreCulling) | `Car`/role states, `ParkedCar`, `Stopped`, frame flags | Stale effects unless `EffectsUpdated` (§3.5) |
| `RaycastSystem` (MainLoop) | moving search tree, `LaneObject` | Vehicles cannot be clicked unless one of the two is maintained (§5) |
| `DeveloperInfoUISystem` | many navigation components | Debug only |

---

## 7. Q6 – Interest management

### 7.1 Camera API on the client

All [V]:

- `CameraUpdateSystem.activeViewer` (`Game.Rendering/CameraUpdateSystem.cs:134`), `position` / `direction` / `zoom` /
  `nearClipPlane` (l.166-172, updated in `OnUpdate`, l.269-273), `activeCameraController` (l.174-216),
  `TryGetLODParameters` (l.257-264), `gamePlayController` / `orbitCameraController` (l.136-140).
- `Viewer`: `position`, `forward`, `right`, `camera`, `visibilityDistance` (far clip), `frustumPlanes`, world-space
  frustum `bounds` (`Game.Rendering/Viewer.cs:44-58, 85+`).
- `IGameCameraController`: `pivot`, `zoom`, `position`, `rotation` (`Game.Rendering/IGameCameraController.cs`).
  `CameraController.pivot` (l.176) and `zoom` (l.224) in `Game/CameraController.cs`.
- Follow target: `OrbitCameraController.followedEntity` (`Game/OrbitCameraController.cs:47, 234-245`).

**Client → host "view report" [I]**, unreliable, e.g. every 100 ms or on change > 5 %:
`{pivot.xz, zoom, camera.position, forward, fovY, aspect, farClip, followedNetId, selectedNetId}`, about 40 B.

The host derives an XZ interest region:

- a disc around the pivot with radius `r = clamp(k·zoom, 300 m, 2500 m)`;
- the frustum footprint capped at `r_far`;
- hysteresis: enter at `r`, leave at `1.25·r`.

The `NearCamera` LOD radius that decides interpolation is not a fixed distance (`RenderingUtils.CalculateLod` on bounds
size), so `k` should be tuned in game.

### 7.2 Host-side selection

- **Moving search tree semantics [V]:** `Game.Objects.SearchSystem` maintains only the static tree itself (l.344-360).
  The moving tree (l.325, 427-431) is written by:
  - `LaneObjectUpdater` (navigation systems, `Game.Net/LaneObjectUpdater.cs:55-131`);
  - `Vehicles.ReferencesSystem`, `Creatures.ReferencesSystem`, `Vehicles.InitializeSystem`;
  - `ResidentAISystem`, `PetAISystem`, `FixParkingLocationSystem`, and `LaneObjectSystem` on load.

  **Entries exist only for objects whose current lane has no `LaneObject` buffer** (areas, connections, flying aircraft)
  (`LaneObjectSystem.cs:93-104`; `Vehicles/ReferencesSystem.cs:343-353, 409-419`). Cars and pedestrians on normal lanes
  live in their lane's `LaneObject` buffer instead, sorted by curve position (`LaneObjectUpdater.cs:17-52`).
- **Area query via vanilla structures [I]:** query `Game.Net.SearchSystem.GetLaneSearchTree` (used by
  `ObjectInterpolateSystem.cs:2963`) for lanes intersecting the region, gather their `LaneObject` buffers, and union the
  moving tree hits. This mirrors the raycast path (`RaycastJobs.ExtractLaneObjectsJob`). It is precise but needs job
  dependencies on both trees.
- **Recommended [I]:** a Burst `IJobChunk` over `TransformFrame + UpdateFrame` (excluding `Temp`/`Deleted`), with
  `SetSharedComponentFilter(new UpdateFrame(T & 15))` exactly like the movers (`CarMoveSystem.cs:423`). For each entity,
  test `Transform.xz` against each client's region and emit slot `(T >> 4) & 3`.
  - Per tick this touches only the ~1/16 of moving objects whose frame was just written. For ~21k movers that is ~1.3k entities × C clients.
  - Cost: microseconds in Burst [I, estimate].
  - Run it once per rendered frame in `PostSimulation` (`Game.Simulation/SimulationSystem.cs:295`), for each tick
    `T ∈ (lastSent, frameIndex]`. That is up to 8 ticks per frame (03 §1.2). Slots stay valid for 64 ticks, so reading
    a few ticks late is safe [V, arithmetic §2.3].
  - Complete the movers' jobs through a proper read-only dependency first, because `TransformFrame` is written in
    parallel jobs (§2.1).
- **Enter-interest baseline [I]:** when an entity enters a client's region, send **all 4 host slots**. The host ring
  already holds ticks `T−48..T`, so the client can interpolate immediately with no stale pose. Leaving the region sends
  nothing (existence stays, §9.3).

---

## 8. Q7 – Info panels and the follow camera

### 8.1 How panels update [V]

- `SelectedInfoUISystem` composes about 75 sections: top, middle and bottom, plus title, line visualizer, household
  sidebar and developer (`SelectedInfoUISystem.cs:222-297`).
- Bindings refresh when the selection changes, when the selected entity has `Updated`/`BatchesUpdated`, or every
  **256 simulation ticks** via `UIUpdateState(256)` (l.181, 383; `UIUpdateState.cs:28-37`). `RequestUpdate()` forces a
  refresh (l.343-346).
- The panel position is updated every frame through `TryGetPosition` (l.532-540, 578-651).
- Sections read through `EntityManager`, `ComponentLookup` and utility classes (`CitizenUIUtils`, `VehicleUIUtils`,
  `CompanyUIUtils`, `EconomyUtils`, `TransportUIUtils`) plus system data.

### 8.2 Components per entity kind

The lists come from grepping each section's `GetComponentData`/`TryGetComponent`/`HasComponent`/`TryGetBuffer`/lookup
types (§10). [V for the lists; I for the "minimal set"]

**Citizen** (`CitizenSection`, `StatusSection`, `HouseholdSidebarSection`, `NotificationsSection`):
- Selected entity: `Citizen`, `HouseholdMember`, `Worker`, `Game.Citizens.Student`, `CurrentBuilding`, `CurrentTransport`, `TravelPurpose`, `HealthProblem`, `Criminal`, age tag.
- Its household: `Household`, `PropertyRenter`, `TouristHousehold`/`CommuterHousehold`/`HomelessHousehold`, `Resources` buffer for wealth (`CitizenUIUtils.cs:17-54`; `HouseholdSidebarSection.cs:268-318`), `HouseholdCitizen`, `HouseholdAnimal`.
- Its creature (`CurrentTransport`): `Target`, `Divert`, `InvolvedInAccident`, `HumanCurrentLane.m_Flags`, `Unspawned`, `CurrentVehicle` (`CitizenSection.cs:113-140`; `CitizenUIUtils.cs:236-354`).
- Workplace/school/residence buildings: `PrefabRef` + names (`NameSystem`), and `CompanyData` for the company.
- `IconElement` icons; singleton `CitizenHappinessParameterData`.

**Vehicle** (`VehicleSection` + role sections, `PassengersSection`, `CargoSection`, `LoadSection`, `VehicleWithLineSection`):
- `Vehicle`, role component (`PersonalCar`, `Taxi`, `PublicTransport`, `CargoTransport`, `DeliveryTruck`, `PoliceCar`, `FireEngine`, `GarbageTruck`, `PostVan`, `Ambulance`, `Hearse`, `MaintenanceVehicle`), `Bicycle`, `Owner` (+ owner `Household`/`PropertyRenter`, `OutsideConnection`).
- `Controller`/`LayoutElement`, `Passenger` buffers per layout vehicle (+ passengers' `Resident`/`Pet`, `PseudoRandomSeed`) (`PassengersSection.cs:62-111`).
- `Target` (+ waypoint `Connected`), `CurrentRoute`, `ParkedCar`/`ParkedTrain`, `InvolvedInAccident`.
- **`ServiceDispatch` buffer + the request entities it references** (`PoliceEmergencyRequest`, `AccidentSite`, ...) (`VehicleUIUtils.cs:192, 209, 306-345, 408-432`).
- Cargo `Resources`/`LoadingResources` and the role component's load fields. Prefab data is local.

**Household** (`HouseholdSidebarSection`, `ResidentsSection`, `VehiclesSection`): `Household`, `HouseholdCitizen` (+
each member's `Citizen`, `HealthProblem`, `TravelPurpose`), `HouseholdAnimal`/`HouseholdPet`, `PropertyRenter`,
`Resources`, `HomelessHousehold`, **`OwnedVehicle`** (+ each vehicle's role component and `ParkedCar`)
(`VehiclesSection.cs:44`).

**Company** (`CompanySection`, `EmployeesSection`, `StorageSection`, `TradedResourcesSection`): `CompanyData`,
`PropertyRenter`, `WorkProvider`, `Employee` buffer (+ `Citizen` of employees), `Resources`, `CompanyStatisticData`,
`ServiceAvailable`, `LodgingProvider`, `PropertyOnMarket`, company type tags, building `Efficiency`. Uses
`ResourceSystem` and `TimeSystem` (local).

**Building** (Efficiency, Level, Upkeep, Residents, Employees, Electricity/Water/Garbage/..., Vehicles,
DispatchedVehicles, Notifications, AverageHappiness):
- `Building`, `Renter`, `Efficiency`, `BuildingCondition`, `UnderConstruction`, `Abandoned`, `Destroyed`.
- `ElectricityConsumer`, `WaterConsumer`, `GarbageProducer`, `CrimeProducer`, `MailProducer`.
- Service facility components, `Occupant`/`Patient`/`Student` buffers, `InstalledUpgrade`, `Policy`.
- `OwnedVehicle` (+ vehicles), `IconElement`, `CurrentDistrict`.
- **`AverageHappinessSection` reads system-level data**: `AirPollutionSystem`, `GroundPollutionSystem`, `NoisePollutionSystem`, `TelecomCoverageSystem`, `LocalEffectSystem`, `TaxSystem`, `CitySystem`, `ServiceFee`. These must be covered by system-state sync (03 §9.4), not by per-entity fetch.

**Transport line** (`LineSection`, `VehicleCountSection`, `TicketPriceSection`, `ScheduleSection`, `ColorSection`,
`LinesSection`, `LineVisualizerSection`):
- `Route`, `TransportLine`, `RouteWaypoint`, `RouteSegment` (+ segment `PathElement` for length), `RouteVehicle` (+ vehicles' `PathInformation`/`VehicleTiming`), `RouteModifier`, `Policy`, `Game.Routes.Color`, `VehicleModel`.
- Stops' `WaitingPassengers`, `ConnectedRoute`.
- For the visualizer, every route vehicle's `PathOwner`, `CarCurrentLane`/`TrainCurrentLane`/..., navigation-lane buffers, `PathElement`, `TransformFrame` + `UpdateFrame`, `Passenger`, cargo `Resources` (`LineVisualizerSection.cs:816-1016, 1039+`).

### 8.3 Sections that scan globally, not per entity [V]

- `DispatchedVehiclesSection` queries **all** vehicles with `ServiceDispatch` to find those serving the selected entity
  (`Game.UI.InGame/DispatchedVehiclesSection.cs:263, 89-126`).
- `VehicleCountSection` and `LineSection` count `RouteVehicle` entries.
- `TrafficRoutesSystem` scans all movers' paths.

These need either global replication of the small fields involved (`ServiceDispatch` changes at dispatch rate, which is
low) or a host-computed answer. [I]

### 8.4 On-demand inspection protocol (proposal)

[I]

1. **Subscribe.** Client → host: `Inspect{netId, kind, flags}` on select, `Uninspect` on deselect, `Follow{netId}` from
   `OrbitCameraController.followedEntity`.
2. **Closure.** The host computes the entity's closure from the §8.2 table, depth 1-2. For example:
   - citizen: citizen, household, creature, vehicle, workplace, school, residence;
   - vehicle: vehicle, layout members, passengers, `ServiceDispatch` requests, owner;
   - line: route, waypoints, segments, route vehicles.

   The closure is recomputed whenever a forward reference in it changes.
3. **Snapshot and deltas.** The host sends a full component snapshot of the closure (types from §8.2) through the
   generic replication engine (08 §12) on a priority channel. Afterwards it sends deltas stamped at ticks with
   `frameIndex % 256 == 0`.
4. **Identical numbers.** When a stamped snapshot is applied, the client calls `SelectedInfoUISystem.RequestUpdate()`
   (l.343-346) in the same frame. The host's own `SelectedInfoUISystem` is likewise forced to refresh on the same
   256-boundaries. A small patch makes the `UIUpdateState` phase global instead of "time since selection" (l.181, 383).
   Every viewer then shows values sampled at the same host tick.
5. **Follow and line visualizer.** While an entity is followed or a line panel is open, the host treats the followed
   vehicle and the line's route vehicles as in interest regardless of distance, so their frames stream at full rate. For
   the visualizer and traffic-route overlays it also streams `PathOwner`/`PathElement`/navigation-lane buffers of those
   vehicles at the 256-tick cadence, and on path change.
6. **Exception safety.** Never expose an entity to the UI before its closure has arrived. `CitizenUIUtils.GetStateKey`
   throws on a missing household (§6). While the closure is incomplete, keep the client selection on a placeholder or
   delay `SetSelection`.
7. **Alternative.** The host could render section JSON for the selection and ship it. Every `Entity` written to JSON
   would need remapping, and localisation stays client-side. This is not investigated here (open question 4).

---

## 9. Q8 – Bandwidth model

### 9.1 Per-entity bytes

From the real layouts in §1. [V sizes; I encodings]

| Encoding | Vehicle frame | Human frame | Notes |
|---|---|---|---|
| Raw `Transform` + `Moving` + `TransformFrame` + 4 B id | 28+24+48+4 = **104 B** | 104 B | Naive component copy |
| Raw `TransformFrame` + 4 B id | **52 B** | 52 B | `Transform`/`Moving` derived on the client (§2.6 step 6) |
| Q-A "light": 3 B varint id, 3×f32 position, 32-bit smallest-three quaternion, 3×f16 velocity, 3 B flags, human +4 B state/activity/timer | **28 B** | 32 B | Simple, robust |
| Q-B "delta" (recommended): 2 B per-client slot id, position delta vs previous frame at 1/64 m (12+12+10 bits, escape to absolute), yaw 12 + pitch 7 bits (humans yaw 10), speed 10 bits + sign (direction from rotation; vertical from Δpos), flags "unchanged" 1 bit (else 18 bits), human state 3 + activity-unchanged 1 + timer "+1" 1 bit | **≈10-11 B** | ≈10 B | Tick implicit per packet (§7.2). Exact sim velocity is lost, which slightly changes Bézier tangents (`ObjectInterpolateSystem.cs:4354`). Send full f16 velocity (+4 B) if exactness matters |
| Baseline on interest entry | 4 × Q-A ≈ 112-128 B | | Once per entry (§7.2) |

### 9.2 City assumptions

[I, estimate]

- 100k population: ~10k moving vehicles including trailers and train cars, plus ~11k moving creatures, for **≈21k
  entities with `TransformFrame`**. 03 §6.8 gives 5-15k vehicles and 5-20k pedestrians.
- Passengers inside vehicles have no frames: they are `Relative` and `Unspawned` (`ObjectEmergeSystem` type sets,
  l.250-269).
- Parked cars (20-40k) are static and handled by persistent replication.
- Visible or interest set: zoomed in 1-3k; zoomed out ~3-5k (LOD culls small movers).
- Update rate per entity: one frame per 16 ticks, i.e. **3.75 / 7.5 / 15 Hz at speeds 1 / 2 / 4** [V, arithmetic; the
  real tick rate can be lower under back-pressure, 03 §1.2].

### 9.3 Bandwidth table

[I, estimate]

| Scenario | Bytes per update | Speed 1 | Speed 2 | Speed 4 |
|---|---|---|---|---|
| All 21k, raw 3 components | 104 | 8.2 MB/s | 16.4 MB/s | 32.8 MB/s |
| All 21k, raw `TransformFrame` | 52 | 4.1 MB/s | 8.2 MB/s | 16.4 MB/s |
| All 21k, Q-A | 30 | 2.4 MB/s | 4.7 MB/s | 9.5 MB/s |
| All 21k, Q-B | 11 | 0.87 MB/s | 1.7 MB/s | 3.5 MB/s |
| Interest 3k, raw `TransformFrame` | 52 | 585 KB/s | 1.17 MB/s | 2.3 MB/s |
| Interest 3k, Q-A | 30 | 340 KB/s | 675 KB/s | 1.35 MB/s |
| **Interest 3k, Q-B** | 11 | **124 KB/s** | **248 KB/s** | **495 KB/s** |
| Interest 3k, Q-B, far two-thirds at half rate (client extrapolates or interpolates the skipped frame) | ~7.4 eff. | 83 KB/s | 165 KB/s | 330 KB/s |
| Interest 1k (street level), Q-B | 11 | 41 KB/s | 83 KB/s | 165 KB/s |

Additional streams [I, estimate]:

- **Existence for all agents** (spawn ~48 B, despawn ~5 B, stopped↔moving swap ~12 B, `Unspawned`/`CurrentVehicle`
  toggles ~6 B). With ~150-300 lifecycle events/s at speed 1, assuming average trips of ~1-2 real minutes for ~10k
  vehicles plus creatures: **~8-20 KB/s at speed 1, ×speed**.
- Lane changes (`CarCurrentLane.m_Lane`, ~6 B, ~1 per 10 s per vehicle): ~6 KB/s for 10k vehicles at speed 1, or
  interest-only.
- Inspection closures: a few KB per 256 ticks per client.
- View reports: ~0.4 KB/s per client.
- Packet headers: ~3 KB/s at 60 packets/s.
- Traffic lights: discrete `TrafficLight.m_State` (`Game.Objects/TrafficLight.cs:6-12`) and `TrafficLights` changes
  every few seconds per junction: ~1-5 KB/s city-wide. Alternatively send for interest junctions only.

**Budget conclusion [I]:**

- With Steam P2P at ~0.1-1 MB/s per peer (08 §13), Q-B with interest management fits comfortably at speeds 1-2 for 1-3
  clients.
- At speed 4, cap each client at ~250-300 KB/s with a priority scheduler: distance to pivot, screen size, followed or
  selected entities first. Send far objects every second frame.
- The host's upload is the sum over clients: 4 clients at speed 4 is ~1.3-2 MB/s with half-rate far field. This is the
  real limit; expose a "motion quality" setting.

---

## 10. Deliverables

### 10.1 Replication recipe for moving objects

[I, from §2-§9]

1. **Identity and existence (global, reliable, ordered):**
   - every vehicle, creature, parked car and train member has a NetId (08 §10);
   - spawn records carry archetype kind (moving / stopped / controller), prefab `PrefabID` index, `UpdateFrame`, `PseudoRandomSeed`, role component, `Car`/`Human`/`Animal` flags, forward references (`Owner`, `Keeper`, `Target`, `CurrentRoute`, `CurrentVehicle`, `Resident`/`Pet`, `Controller`/`LayoutElement`), the lane component, `Unspawned`/`TripSource`, and `Transform` + `Moving`;
   - despawn and swap records follow the vanilla type sets (§3.1, §4.2).
2. **Client spawn:** prefab archetype + `UpdateFrame(u_host)` + `Applied`, before `ModificationSystem`. Let the vanilla
   reference systems rebuild links. Re-apply host-owned fields after Modification5 (§4.3-4.7).
3. **Motion (interest-managed, unreliable, sequenced):**
   - for each tick `T`, the host sends Q-B frames of entities with `u = T & 15` inside each client's region, plus a 4-slot baseline on entry;
   - the client stages frames and JIT-writes slots in `PreCulling` before `PreCullingSystem`, extrapolating missing frames;
   - the client keeps `Transform`/`Moving` coherent;
   - the client clock runs `D` ticks behind the host (§2.6).
4. **Visual side effects on the client:** `EffectsUpdated` on light-window or role-state changes, `SubObjectsUpdated`
   on human activity prop changes, and **never `Updated`** for motion (§2.6 step 7, §3.5).
5. **Selection:** maintain the moving search tree entry (bounds from the latest frame) or `LaneObject` buffers for
   every replicated mover, so `RaycastSystem` can hit it (§5).
6. **Inspection:** subscription closures at the 256-tick cadence, aligned across peers (§8.4).

### 10.2 Client systems to keep

See §3.6 for the moving-object scope. Summary:

- `SimulationSystem` (patched clock);
- `PreRenderSystem`/`RenderingSystem` and all `PreCulling`/`Rendering`/`Cleanup` systems listed there;
- `UpdateGroupSystem`, `Game.Vehicles.ReferencesSystem`, `Game.Creatures.ReferencesSystem`;
- `Game.Vehicles.InitializeSystem`, `Game.Creatures.InitializeSystem` (with host re-apply);
- `UnspawnedSystem`, `AnimationUpdateSystem`/`AnimationSystem`, `MeshGroupSystem`, `Game.Effects.SearchSystem`;
- `ParkedVehiclesSystem` only for `Temp` previews (client tools).

### 10.3 Client systems to disable

- **GameSimulation** (`SystemOrder.cs:311-357, 517-558, 588, 601`):
  - navigation: `CarNavigationSystem`(+`Actions`), `TrainNavigationSystem`, `WatercraftNavigationSystem`, `AircraftNavigationSystem`, `HumanNavigationSystem`(+`Groups`, `Actions`), `AnimalNavigationSystem`(+`Groups`, `Actions`);
  - vehicle AIs: all vehicle `*AISystem` and their `Actions`;
  - creature AIs: `PetAISystem`, `WildlifeAISystem`, `DomesticatedAISystem`, `ResidentAISystem`(+`Actions`);
  - movement: `VehicleOutOfControlSystem`, `CarMoveSystem`, `CarTrailerMoveSystem`, `TrainMoveSystem`, `WatercraftMoveSystem`, `AircraftMoveSystem`, `HumanMoveSystem`, `AnimalMoveSystem`, `ObjectCollisionSystem`, `StuckMovingObjectSystem`;
  - spawners: `TrafficSpawnerAISystem`, `VehicleSpawnSystem`, `CreatureSpawnSystem`, `RandomTrafficDispatchSystem`, `CreatureSpawnerSystem`;
  - `TransportLineSystem`;
  - `TrafficLightSystem` (replicate light states instead).
- **LoadSimulation phase** entirely (l.611-630).
- **Modification** (§4.6): `ObjectEmergeSystem`, `GroupSystem`, `FixParkingLocationSystem`, `FixLaneObjectsSystem`
  (if the client does not maintain `LaneObject`, otherwise review), `TripResetSystem`; `ParkedVehiclesSystem`'s
  `Created` path.
- **ModificationEnd:** `ParkingLaneDataSystem`, `PathOwnerTargetMovedSystem`, `BoardingVehicleSystem` (host-owned
  `BoardingVehicle`).
- Mind producer/consumer queue pairs (03 §7).

### 10.4 Inspection protocol

§8.4.

### 10.5 Bandwidth table

§9.3.

---

## 11. Risks

1. **32-tick slot window** (§2.3). Writing network frames straight into the ring gives jumps and backwards snaps under
   jitter, especially at speed 4. Mitigation: staging + JIT writer + extrapolation + clock lag. [V for the window, I for the mitigation]
2. **`UpdateFrame` divergence** gives per-entity time offsets up to 15 ticks. Spawn with `Applied` and replicate the
   value. Watch vanilla paths that set `UpdateFrame` (`UpdateGroupSystem.cs:358-370, 446-459`). [V]
3. **`Updated` on movers resets their frames** (`UpdateGroupSystem.cs:561-615`) and re-runs the vehicle and creature
   initialisers (random lane offsets from `RandomSeed.Next()`, spawn repositioning). A generic receiver that adds
   `Updated` "to be safe" freezes traffic for up to 32 ticks per update. [V]
4. **Vanilla Modification systems create or mutate agents on clients:** `ParkedVehiclesSystem`, `ObjectEmergeSystem`,
   `GroupSystem`, `FixParkingLocationSystem`, `FixLaneObjectsSystem`, `ParkingLaneDataSystem`, and the `TransportDepot`
   write in `Vehicles.ReferencesSystem` (§4.5-4.6). Each would cause divergence or duplicates. [V]
5. **Effects not refreshing** without `EffectsUpdated`: sirens stay on or off, headlights wrong at dusk (§3.5). [V]
6. **Selection impossible** if neither `LaneObject` nor the moving tree is maintained (§5). [V]
7. **UI exceptions** from incomplete closures (`CitizenUIUtils.cs:238-239`). [V]
8. **Burst-inlined helpers** (`CalculateUpdateFrames`, `CalculateTransform`) cannot be patched reliably. All
   adaptation must happen in data (slots, clock), not in the interpolation code. [I]
9. **Host CPU sync point.** Reading `TransformFrame` after the movers requires completing their jobs. Schedule the
   sender as a Burst job with read-only handles and proper dependencies in `PostSimulation`. [I]
10. **Lane identity remapping** for `CarCurrentLane`, `ParkedCar`, `TrainBogieFrame` and `HumanCurrentLane`: lanes are
    regenerated per machine (04 §4.2). A wrong or stale lane entity in `InitializeCurrentLaneJob` indexers is unsafe
    (§6). [V/I]
11. **Bandwidth at speed 4 with several clients** (§9.3). Needs a per-client budget and priority scheduler. [I]
12. **Far-field staleness.** Entities outside interest keep stale frames and `Transform`. Hysteresis and entry
    baselines hide this, but a camera teleport (panel "go to") may show stale poses for one round trip. [I]
13. **Clock lag `D`** delays every client view of the world, including tool results, by `D` ticks. This is acceptable
    in practice (≤0.2 s) but must be accounted for in tool-feedback UX. [I]

---

## 12. Open questions

1. Measured counts in a real 100k city: moving vehicles, moving creatures, parked cars, interest-set sizes per zoom
   level, lifecycle events per second. Needs an in-game diagnostic build.
2. The effective `NearCamera` distance per vehicle size and LOD setting, to tune the interest radius `k`.
3. Whether `Game.Tools.Hidden` + `BatchesUpdated` can serve as a purely cosmetic "out of interest" hide for far movers.
   `ParkedVehiclesSystem.cs:525-526` hides originals this way, but the effects on UI and culling are unverified.
4. Whether section JSON can be rendered on the host and remapped. This needs the `IJsonWriter` `Entity` write path in
   `Colossal.UI.Binding` and a check of how `NameSystem.BindName` serialises.
5. Exact replication policy for `ServiceDispatch` buffers and service-request entities. `DispatchedVehiclesSection` and
   vehicle state keys need them.
6. Whether `TrafficRoutesSystem` can be fed on demand with path buffers only for matching vehicles. That needs
   host-side matching identical to its job.
7. Real-network jitter distribution for Steam P2P/relay, to size `D` and the staging ring depth.

---

### Appendix – grep summary of `TransformFrame` access

[V] Files with `BufferTypeHandle<TransformFrame>` or `BufferLookup<TransformFrame>`:

- **RW:**
  - Simulation: `Game.Simulation/` `AircraftMoveSystem`, `AnimalMoveSystem`, `CarMoveSystem`, `CarTrailerMoveSystem`, `HumanMoveSystem`, `ObjectCollisionSystem`, `TrainMoveSystem`, `UpdateGroupSystem`, `VehicleOutOfControlSystem`, `WatercraftMoveSystem`.
  - Load: `Game.Serialization/TransformFrameSystem`.
- **RO:**
  - Effects and objects: `Game.Effects/EffectControlData`, `Game.Objects/SubObjectSystem`.
  - Rendering: `Game.Rendering/` `BatchInstanceSystem`, `MeshGroupSystem`, `ObjectInterpolateSystem`, `PreCullingSystem`, `RelativeObjectSystem`, `RouteBufferSystem`, `UtilityLodUpdateSystem`.
  - Tools and UI: `Game.Tools/ApplyObjectsSystem`, `Game.Tools/TrafficRoutesSystem`, `Game.UI.InGame/LineVisualizerSection`.
