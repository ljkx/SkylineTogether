# Proposal A — Maximum Fidelity ("Mirror City")

Architecture proposal for **Skyline Together**, a from-scratch, host-authoritative co-op mod for Cities: Skylines II.
Angle: **maximum fidelity** — the host is the single source of truth for all persistent city state *and* for the
visible agents; every client is a *mirror* that runs only a follower simulation.

- Date: 2026-10-10. Status: proposal for review (one of several competing proposals).
- Sources: research digest and reports `docs/research/00`–`15` (cited as **R01 §x** … **R15 §x**), plus spot checks in
  the decompiled code made for this proposal (cited as `file.cs:line`, paths relative to the decomp root
  `<audit>\decomp`).
- Markers: **[V]** verified in code (by a research report or by this proposal's spot checks), **[I]** design inference,
  **[M]** must be measured or verified in game.

Spot checks made for this proposal (all [V]):

| Fact | Where |
|---|---|
| `UpdateSystem.Update(SystemUpdatePhase, uint updateIndex, int iterationIndex)` is public, so a mod can drive `GameSimulation` ticks itself | `Game/Game/UpdateSystem.cs:223` |
| `SimulationSystem.frameIndex/frameTime/smoothSpeed` have private setters; `selectedSpeed` setter is ignored while `m_IsLoading`; nothing in `Game` reads `SimulationSystem.Enabled` | `Game/Game.Simulation/SimulationSystem.cs:68-87, 188-296`; grep |
| `RenderingSystem.PrepareRendering` reads only `frameIndex`, `frameTime`, `selectedSpeed`, `smoothSpeed` from `SimulationSystem` | `Game/Game.Rendering/RenderingSystem.cs:173-221` |
| Lifecycle hooks (`OnGamePreload`, `OnGameLoaded`, `OnGameLoadingComplete`) are delivered through `GameManager`/`LoadGameSystem` events **regardless of `Enabled`** | `Game/Game/GameSystemBase.cs:17-31, 59-104` |
| The UI binding registry is `IBindingRegistry` with public `bindings`, `AddBinding`, `RemoveBinding`; trigger bindings have a public `active` flag that suppresses the C# callback | `Game/Game.SceneFlow/UserInterface.cs:43`; `Colossal.UI.Binding/CompositeBinding.cs:42-60`; `RawTriggerBindingBase.cs:10-38` |
| UI module header parser: first block comment, line exactly `Cities: Skylines II UI Module`, then `Id:`, `Author:`, `Version:`, `Dependencies:` | `Colossal.IO.AssetDatabase/UIModuleAsset.cs:44-107` |
| Community bundles resolve `react`, `react-dom`, `cs2/*`, `cohtml/cohtml` as **window globals** (`externalsType: "window"`) | `thirdparty/Anarchy/Anarchy/UI/webpack.config.js:31-43` |
| `OverlayRenderSystem.GetBuffer(out JobHandle)` + `Buffer.DrawCircle/DrawLine/DrawCurve` are public (remote cursors/ghosts) | `Game/Game.Rendering/OverlayRenderSystem.cs:106-251, 603, 636`; registered in Rendering (`SystemOrder.cs:688`) |
| `MapTilePurchaseSystem.UnlockTile(EntityManager, Entity)` is public static; `SelectedInfoUISystem.RequestUpdate()` is public | `Game/Game.Simulation/MapTilePurchaseSystem.cs:346`; `Game/Game.UI.InGame/SelectedInfoUISystem.cs:343` |
| `SubObjectSystem` treats an owner's child as *its own* (regenerable) iff it is in the owner's `SubObject` buffer, has `Owner == owner`, and has none of `ServiceUpgrade`, `Building`, `Secondary` | `Game/Game.Objects/SubObjectSystem.cs:1172-1195` |
| `Buildings.InitializeSystem` creates gate bypass `Route` + 2 `Segment` entities (owner = gate building) on every peer | `Game/Game.Buildings/InitializeSystem.cs:154-168` |
| `GroundHeightSystem` keeps its pending bounds in `m_New/Pending/Reading/ReadyUpdates`; only `OnUpdate` consumes `m_ReadyUpdates` | `Game/Game.Simulation/GroundHeightSystem.cs:1071-1286` |
| Audio effect entities carry `EffectInstance`, which the save query excludes | `Game/Game.Audio/AudioGroupingSystem.cs:289-297`; R04 §2.1 |

---

## 0. Executive summary

**The model.** The host runs the complete vanilla simulation. Clients load the host's save (join snapshot), then
become *mirrors*: every gameplay-producing system is disabled on them, their clock follows the host's
`frameIndex`, and the host streams every change to persistent state and every visible agent. Clients keep running
the vanilla *derivation* pipeline (Modification phases, rendering, effects, audio, UI), so geometry, lanes,
sub-objects, zone blocks, search trees and batches are rebuilt locally from replicated source data, exactly as the
game does after loading a save. Player actions on a client are executed locally as a **prediction**, captured as a
**proposed state delta** (a "change-set intent"), validated and committed by the host, and re-broadcast as
authoritative state. Divergence is detected by hashing and repaired automatically, with a fresh snapshot as the last
resort.

**Design principles.**

| # | Principle | Consequence |
|---|---|---|
| P1 | **Single writer.** Only the host writes persistent components and agent state. | Clients never derive gameplay values; their copies are overwritten by host values. |
| P2 | **One engine, two directions.** The same capture → canonical delta → apply machinery carries host→client state and client→host proposals. | Mods that write ECS directly (Move It, Tree Controller, Better Bulldozer, Water Features, Zoning Toolkit, Anarchy) are covered without per-mod intent types (R09 §5.10). |
| P3 | **Replicate what the game saves, derive what the game derives, key what is derived but stateful.** | The game's save query (`SerializerSystem.cs:171-214`, R04 §2.1) defines the replication scope; mod components join automatically (R09 §1.1). |
| P4 | **Apply where the game applies its own structural changes.** | Host deltas land right after `EndFrameBarrier` playback, before `ToolSystem` (R11 §6.2). |
| P5 | **Never replicate reward-carrying events; replicate the resulting state and synthesize UI events.** | No double money/XP/dev points (R14 §0). |
| P6 | **Converge by construction, verify by hashing, repair by re-send, reload as last resort.** | Every failure path ends in a resync, never in a crash. |
| P7 | **No mandatory Harmony.** Patch-free toolbox: `Enabled` gating, lifecycle-event unsubscription, UI binding interposition, a short audited reflection list, P/Invoke for Steam lanes. | Smaller blast radius on game patches; 0Harmony is not bundled in v1 (§11.1, App. B). |
| P8 | **Pure Core, thin Game layer.** Protocol, codecs, identity, scheduling, sessions and consistency live in a Unity-free `SkylineTogether.Core` with unit tests. | The Game layer is adapters plus safety nets (§11). |

**Fidelity targets (service levels).**

| State | When identical on every peer | Mechanism (§) |
|---|---|---|
| Persistent structure: nets, buildings, props, trees, areas, zoning, routes, water sources, names, policies | bit-exact source data one host frame + ½ RTT after the host commit; derived data rebuilt in the same client frame | S-class transactions (§3.5) |
| City economy and progression: money, loans, budget, fees, taxes, XP, milestones, dev points, unlocks | player-driven: one host frame + ½ RTT; simulation-driven: ≤ 250 ms | S/G-class (§3.11) |
| Agent existence: every citizen, household, company, vehicle, creature, parked car | exact set, ½ RTT after the host frame | S-class |
| Visible agents (in each client's interest region) | same trajectories, shown `D` ticks later (`D` ≈ 0–40 ticks, adaptive) | M-class motion (§3.9) |
| Selected entity and its closure (info panel) | exact, sampled at the same `frameIndex % 256 == 0` boundary as the host | on-demand inspection (§3.10) |
| Other per-entity simulation state (citizen wellbeing, household money, company stock, building condition …) | converges; target staleness p95 ≤ 5 s in view, ≤ 60 s elsewhere (budget-dependent, shown in diagnostics) | V-class, prioritized (§3.8) |
| Icons, chirps, life path, event journal, statistics | icons in view: one host frame + ½ RTT; statistics at sample cadence | S/G-class |
| Terrain heightmap and material splatmap | bit-exact ≈ 0.3 s after a stroke settles | tile sync (§3.11.6) |
| Cell maps (pollution, groundwater, natural resources, land value, …) | exact at the host's update cadence | bulk XOR deltas (§3.11.5) |
| Time, date, weather | exact (pure function of `frameIndex` + replicated weather state) | clock + G-class |
| Water surface | visual approximation, corrected by tiled snapshots; all water-driven gameplay is host-only and therefore exact | bulk (§3.11.7) |
| Camera, selection, tool previews, infoviews, UI, radio, photo mode, chirp likes, tutorials | per player, by design | — |

**Headline numbers** (100k-population city, speed 1, [I]/[M]): per client ≈ **150–450 KB/s** steady state
(motion 80–250, background state 30–120, structure 2–20, fields 20–80, global 2–5), ≈ ×2 at speed 4 under the
budget cap; host upload = sum over clients; join = 20–300 MB snapshot (≈ 1–5 min at 1–5 MB/s). Host main-thread
budget for replication ≈ 2.5 ms/frame; client apply ≈ 1–3 ms/frame.

---

## 1. Authority model per state category

Legend for **Class**: **S** structural/critical (reliable, immediate, all peers), **V** convergent value (reliable,
prioritized and coalesced), **M** motion (unreliable, interest-managed), **G** global/system state (reliable, at
cadence), **K** keyed state on locally derived entities, **B** bulk blob (bulk lane), **L** local only, **O**
on-demand (inspection-scoped). "Client systems" names what runs on the client: **KEEP** (vanilla runs), **OFF**
(disabled by the gate table, §5.2), **OFF+feed** (disabled, host output written instead).

### 1.1 Persistent structure

| Category | Authoritative data | Authority | Class | Client mechanism | Client systems | Refs |
|---|---|---|---|---|---|---|
| Net nodes and edges, incl. building sub-nets | `Node`, `Edge`, `Curve`, `Game.Net.Elevation`, `Upgraded`, `BuildOrder`, `Road.m_Flags` (half-align bits), `Fixed`, `LocalConnect`/`Standalone`/`Orphan`, `TrafficLights` config, ordered `ConnectedNode`, `PseudoRandomSeed`, `Owner`, `Aggregated` | host | S | first-class entities (NetId), created from the host archetype; `Updated` mirrored on the host's touched closure | `Generate*`, `Net.ReferencesSystem`, `CompositionSelect`, `Net.GeometrySystem`, `LaneSystem`, `BlockSystem` **KEEP** | R02 §6-§9, R13 §5, §11 |
| Aggregates (streets) | `Aggregate`, `PrefabRef`, ordered `AggregateElement`, `RandomLocalizationIndex` | host | S | first-class; `AggregateReconcileSystem` after `AggregateSystem` (Mod2B) forces host membership/order | `AggregateSystem` KEEP | R13 §7.1 |
| Lanes (edge, node, object, connection) | none (topology derived) | derived | — / K | regenerated locally by `LaneSystem`; addressed by portable lane key (PLK) only when a replicated value points at a lane | KEEP | R13 §2 |
| Top-level objects: buildings, props, trees, decals, service upgrades, extensions, transport stops, area-spawned objects | `Transform`, `PrefabRef`, `Owner`, `Attached`, `Game.Objects.Elevation`, `Relative`, `Building.m_OptionMask`/`m_Flags`, `Extension`, `ServiceUpgrade`, `CustomMeshColor`, `Native`, `Overridden`, `Policy`, `PseudoRandomSeed`, `UnderConstruction`, `Tree`, `Plant`, mod components | host | S (structure), V (sim values) | first-class; created with host `UpdateFrame`; `Applied` mirrored (see §3.6.3 for the `ParkedVehiclesSystem` rule) | `SubObjectSystem`, `SecondaryObjectSystem`, `RoadConnectionSystem` KEEP | R02 §9.1, R04 §4.3, R13 §6.2 |
| Prefab sub-objects, secondary objects (fences, street lights, signs, poles, building props) | derived set; state on them: `Tree`, `Decoration` enabled bit, Move It/Anarchy transform overrides, signal pole `TrafficLight.m_State` | derived + host-owned state | K | regenerated by `SubObjectSystem` / `SecondaryObjectSystem`; host state attached by key `(ownerNetId, prefab, alignIndex \| qpos)` one frame later | KEEP | R13 §3, §11 |
| Areas: districts, lots, surfaces, spaces, extractor/storage areas, map tiles | `Area`, `Game.Areas.Node` buffer, `PrefabRef`, `Owner`, `District.m_OptionMask`, `Policy`, `PseudoRandomSeed` (lots), `Extractor`/`Storage` amounts (V), map-tile ownership = absence of `Native` | host | S (+V) | first-class; `Geometry`/`Triangle` derived | `Areas.GeometrySystem` KEEP | R04 §4.4 |
| Zone blocks | none (derived from edges) | derived | — | regenerated by `BlockSystem` | KEEP | R13 §4 |
| Zone cells | `Cell.m_Zone` (as zone `PrefabID`) | host | K | overlay by block key `(edgeNetId, side, qpos, size)` one frame after the structure; block tagged `Updated`; `m_State`, `m_Height`, `VacantLot` are never written | `CellCheckSystem` KEEP | R13 §4.3 |
| Routes and lines | `Route`, `TransportLine`, `Game.Routes.Color`, `RouteNumber`, `VehicleModel`, `Policy`, `RouteModifier`; `Waypoint` + `Position` + `Connected` + `Owner`; `Segment` + `Owner`; ordered `RouteWaypoint`/`RouteSegment` | host | S | first-class; `AccessLane`, `RouteLane`, `PathTargets`, `PathElement`, `CurveElement` derived locally | `Routes.InitializeSystem` **OFF+feed**; `WaypointConnectionSystem`, `RoutePathSystem`, `SegmentCurveSystem` KEEP | R13 §9 |
| Gate bypass routes | none | derived (both peers) | — | never replicated (excluded by family rule, §2.3) | `Buildings.InitializeSystem` KEEP | spot check above |
| Water sources | `Game.Simulation.WaterSourceData` + `Transform` (no `PrefabRef`), Water Features components | host | S | first-class (identity does not depend on `PrefabRef`) | `WaterSourceInitializeSystem` KEEP (prefab sources: host data arrives next frame) | R07 §6.3, R09 §2.9 |
| Flow graphs (electricity, water pipes) | graph topology derived; flows are simulation state | derived + host state | K (optional, infoviews) | graph regenerated by the `*GraphSystem`s; flow values keyed by `(ownerNetId, role)` | KEEP | R04 §4.10 |

### 1.2 City and simulation state

| Category | Authoritative data | Class | Client mechanism | Client systems | Refs |
|---|---|---|---|---|---|
| Citizens, households, pets, companies | `Citizen`, `Household`, `HouseholdMember`, `PropertyRenter`, `Worker`, `Student`, `CurrentBuilding`, `TravelPurpose`, `HealthProblem`, `Criminal`, `Resources`, `CompanyData`, `Employee`, `WorkProvider`, `Profitability`, seekers, meetings; runtime-maintained reverse buffers (`Renter`, `HouseholdCitizen`, `HouseholdAnimal`) are replicated as data because the systems that maintain them are OFF on clients | existence S, values V | first-class global entities (needed as reference targets) | all initializers OFF (`CitizenInitialize`, `HouseholdInitialize`, `CompanyInitialize`, `StorageInitialize`, `HouseholdPetInitialize`, `MeetingInitialize`) | R11 §2.11, R04 §4.7 |
| Money, loans, credit | `PlayerMoney` (private `m_Money` copied raw), `Loan`, `Creditworthiness` on the City entity | S | City entity is first-class; client-local writes (tool cost prediction) are reverted to the host value | `LoanSystem` KEEP (prediction only, §4.4) | R03 §6.4 |
| Budget, fees | `ServiceBudgetData` buffer (budget entity), `ServiceFee` buffer (City) | S | entity data | `CityServiceEfficiencySystem` OFF+feed (`Efficiency` replicated) | R10 §3.3 |
| Budget arrays (income/expense lines) | `CityServiceBudgetSystem` arrays (not `ISerializable`) | G (1–4 Hz) | read on host / written on client by reflection (App. B) | **OFF+feed** | R11 §5.2 |
| Taxes | `TaxSystem.m_TaxRates` (92 ints) | G on change | `TaxSystem` `Deserialize` with a full-tag context, after `Readers.Complete()` | `TaxSystem` OFF (GameSimulation) | R11 §5.2-§5.3 |
| Trade, subsidies, production/electricity/water statistics, `Count*` systems | system state (`ISerializable`) | G (per system cadence) | `SystemStateSync` descriptors (§3.11.1) | OFF (GameSimulation); `GameModeGovernmentSubsidiesSystem` KEEP (derived, its `Enabled` drives budget UI) | R14 §10 |
| Demand | `Residential/Commercial/IndustrialDemandSystem` state | G (4 Hz) | `Deserialize` (full-tag context) | OFF | R11 §5.2 |
| Statistics | `CityStatistic` buffers on statistic entities; `CityStatisticsSystem.m_SampleCount/m_LastSampleFrameIndex` | V (sample cadence) + G | entity data + `Deserialize` with empty event queue; **never** `PostDeserialize` | OFF | R11 §5.2, R14 §6.2 |
| Progression | `XP`, `MilestoneLevel`, `DevTreePoints` (City); prefab `Locked` bits; `PlacedSignatureBuildingData`; `UnlockRequirementData.m_Progress` | S / prefab-state channel | state + **synthesized** `Event+Unlock` and `Event+MilestoneReachedEvent` (§3.6.5); map-tile permits derived from milestone `Locked` | `XPBuilt`, `NetXP`, `XP`, `XPAccumulation`, `DevTree`, `UnlockAll`, requirement systems OFF; `UnlockSystem` OFF (bits written directly); `MilestoneSystem` OFF + cached UI fields fed | R14 §1-§3 |
| Time and clock | `SimulationSystem.frameIndex`, `TimeData` singleton | clock stream + S | `SessionClockSystem` (follower mode) drives `frameIndex` (§5.1) | `TimeSystem` KEEP; `SimulationSystem` replaced | R03 §1.3, R11 §7 |
| Weather and climate | climate prefab, current/next weather, temperature base, `OverridableProperty` overrides; weather phenomena are event entities | G on change + S | `ClimateSystem` KEEP (pure function of date); host weather digest every 256 ticks, applied on mismatch | `WeatherPhenomenonSystem`, `WeatherHazardSystem` OFF | R03 §6.7, R11 §3 |
| Planetary (latitude/longitude) | `PlanetarySystem` state | in save | — | KEEP | R04 §6 |
| Services | service building state (`Efficiency`, occupants, patients, students, vehicles, `ServiceDispatch`), coverage buffers on edges | V (in-view boosted) | entity data | all service AIs/dispatch OFF; `ServiceRequestSystem` OFF | R03 §6.9 |
| Service requests | `ServiceRequest` + type entities | O (scoped) | replicated only inside inspection closures (§3.10) | OFF | R15 §8.3 |
| Events: fire, crime, accidents, floods, weather phenomena, health and calendar events | persistent `Game.Events.*` entities, `Duration`, `TargetElement`, target components (`OnFire`, `AccidentSite`, `InvolvedInAccident`, `Flooded`, `Damaged`, `Destroyed`, `Criminal` …) | S (existence, add/remove), V (values) | first-class; one-frame `Game.Common.Event` command entities are **never** replicated | `Events.InitializeSystem`, `Damage`, `Destroy`, all command consumers OFF | R11 §2.4, §2.7 |
| Event journal | journal entities + buffers | S | entity data; client companion refreshes `EventJournalSystem.eventJournal` and invokes its callbacks | `EventJournalInitialize`, `EventJournalSystem` OFF+feed | R14 §6.1 |
| Notifications (icons) | per-owner icon set (prefab, priority, layer, flags, target) | S (in view), V (elsewhere) | replication-owned `IconCommandBuffer` (`IconCommandSystem.CreateCommandBuffer()`), so vanilla creates `Icon` entities, `IconElement`, animations and clusters; simulation Transaction icons replayed as events | `IconCommandSystem` **KEEP (mandatory)**; marker icons local | R14 §4 |
| Chirps, life path, radio | `Chirp` + `ChirpEntity` + `RandomLocalizationIndex`, `LifePathEvent`, `LifePathEntry`, `Followed`; radio tags | S | entity data; radio tags enqueued into `RadioTagSystem` input queues | `TriggerSystem` and all unguarded producers OFF, `CreateChirp`, `LifePathEvent` OFF+feed, `RandomLocalizationInitialize` OFF+feed | R14 §5 |
| XP toasts | `XPMessage(frame, amount, reason)` | event | host taps `XPSystem` queue; clients call public `MilestoneUISystem.AddMessage` | — | R14 §1.5 |

### 1.3 Moving agents

| Category | Authoritative data | Class | Client mechanism | Refs |
|---|---|---|---|---|
| Vehicles (cars, trucks, trains incl. layout members, aircraft, watercraft, bicycles) | existence; spawn payload: prefab, `UpdateFrame`, `PseudoRandomSeed`, role component, `Car`/role flags, `Owner`, `Target`, `CurrentRoute`, `Controller`/`LayoutElement`, lane (PLK), `Unspawned`/`TripSource`, `Transform`, `Moving` | S (existence, swaps) | spawned from the prefab archetype (moving/stopped/controller) with host `UpdateFrame` + `Applied`; reference systems rebuild links; never tagged `Updated` for motion | R15 §4 |
| Vehicle motion | `TransformFrame` (position, velocity, rotation, flags, state) per 16-tick step | M | per-tick frames for entities with `u = T & 15` inside each peer's interest region; client staging ring + just-in-time slot writer in PreCulling + extrapolation; `Transform`/`Moving` kept coherent | R15 §2.6, §7 |
| Parked cars and parked trains | `ParkedCar`/`ParkedTrain` (lane PLK + curve position), `Transform` | S | first-class global | R15 §5 |
| Creatures (residents, pets, wildlife, domesticated) | existence, `Resident`/`Pet`, `CurrentVehicle`, `HumanCurrentLane` flags, `Human.m_Flags`; frames as vehicles | S + M | as vehicles; `SubObjectsUpdated` events synthesized for activity prop changes | R15 §2.6 step 7 |
| Paths and navigation | `PathOwner`, `PathElement`, navigation lanes | O | only for line visualizer, traffic routes and selected vehicles | R15 §5, §8.4 |
| Traffic lights | node `TrafficLights` state + pole `TrafficLight.m_State` | K (interest-managed) | written by key; `TrafficLightSystem` OFF | R15 §9.3 |

### 1.4 Environment

| Category | Authoritative data | Class | Client mechanism | Refs |
|---|---|---|---|---|
| Terrain heightmap | `TerrainSystem.m_Heightmap` (4096² R16) | B (tiles) | client strokes are predicted locally and proposed; host applies stamps with a pinned delta, sends dirty 64×64 tiles; clients `Graphics.CopyTexture` + `OnAreaChanged` + `WaterSystem.TerrainWillChangeFromBrush` + `UpdateMinMax` | R07 §5.2 |
| Terrain material paint | `TerrainMaterialSystem` user splatmap | B (tiles) | as heightmap (visual) | R07 §5.3 |
| Water surface | `WaterSystem.WaterTexture` (2048² RGBA32F) | B (tiles, quantized) | client runs the GPU water sim for smooth visuals with mirrored sources/params; host tiles correct it; water-driven gameplay is host-only | R07 §6.7 |
| Sea level, water sim parameters, legacy flag | `WaterSystem` globals, `IWaterSimulation` params | G on change | public setters | R07 §6.2 |
| Snow | `SnowSystem` textures | B (rare) | client simulates (tick-driven, fixed step); host snapshot on weather change or every ~5 game-minutes | R07 §7 |
| Wind | `WindSimulationSystem` cells, constant wind | B (≈ 30 s) | client simulates for visuals, corrected | R11 §3 |
| Cell maps | air/ground/noise pollution, groundwater, soil water, natural resources, land value, attractiveness, telecom, population/availability grids, ambience | B (on host update) | `CellMapSystem<T>.GetMap(false, out deps)` + `AddWriter` | R07 §8 |

### 1.5 Mod state, names, policies, options, per-player state

| Category | Authoritative data | Class | Client mechanism | Refs |
|---|---|---|---|---|
| Mod components | every `ISerializable`/`IEmptySerializable` component from a non-Game assembly (auto-included in the save query) | S/V by schema | generic codecs (§3.2); entity references remapped; enabled bits replicated | R09 §1.1, §5 |
| Mod system state | `IDefaultSerializable` mod systems (e.g. Water Features `ChangeWaterSystemValues`, `TidesAndWavesSystem`, `SeasonalStreamsSystem`) | G | per-profile: their own `Deserialize` (audited) or settings bridge | R09 §2.9 |
| Mod settings that affect simulation/validation | `ModSetting` properties (per profile) | session settings | host pushes values; client writes them in memory (no `ApplyAndSave`), restores on leave | R09 §5.8 |
| Mod prefab mutations | `UnlockRequirement`, `MilestoneData`, `PlaceableObjectData` cost, added prefabs | invariant | "same mods + same settings" enforced by handshake | R09 §5.9 |
| Custom names | `NameSystem` names (keyed by `Entity` in vanilla) | S | NetId-keyed table; applied with public `NameSystem.SetCustomName` from the UIUpdate phase | R13 §7.2 |
| City name | `CityConfigurationSystem.cityName` | S | setter | R10 §3.5 |
| Generated names | `RandomLocalizationIndex` buffer + `PseudoRandomSeed` | S (entity data) | written at spawn; re-roll prevented because the initializer is OFF; strings localized per player | R13 §7.3 |
| Policies and modifiers | `Policy` buffers (City, districts, buildings, routes) **and** `CityModifier`/`DistrictModifier`/`BuildingModifier`/`RouteModifier`, option masks | S | state (never replay `Modify` events: they re-send telemetry and re-enqueue triggers, R11 §9 risk R10) | `ModifiedSystem` KEEP (input-starved), `DefaultPoliciesSystem` OFF+feed, modifier initializers KEEP (outputs overwritten) | R10 §4, R11 §2.6 |
| City options, game mode | theme, left-hand traffic, disasters, unlimited money, unlock-all, unlock-map-tiles; game mode prefab | join snapshot | `overrideLoadedOptions=false`, `GameModeSystem.overrideMode = host mode` | R12 §4.2 |
| Speed and pause | session speed | clock stream | host-only setter; clients send requests through interposed time-control bindings (§10.4) | R05 §9 |
| Followed citizens | `Followed` + `LifePathEntry` | S (shared, 50-cap) | intent | R14 §5.4 |
| Camera, selection, follow target, infoview, tool state and previews, UI panels, photo mode, cinematic camera, radio channel, notifications/toasts, key bindings | — | L | local; host camera from the snapshot is replaced by the client's own after join | R10 §6, R12 §4.2 |
| `HiddenRoute`, chirp `Liked` | — | L overlay | masked out of replication and hashing (`HiddenRoute` is a local marker in archetype signatures) | R10 §6.1, R14 §5.4 |
| Mesh colour palette | `MeshColorPalette` entity (saved with the city) | S | shared city data; edits are `EditPalette` intents | R10 §3.5 C5 |
| Tutorials, achievements | — | L | tutorials disabled for all peers during a session (they emit `Unlock` events, unlock map tiles, and gate milestones); achievements are already off with mods loaded | R14 §7-§8 |

---

## 2. Entity identity

### 2.1 NetId

- `NetId` is a `uint32`; `0` means null. Only the host allocates, sequentially from `NetIdSystem.m_NextNetId`. Ids are
  never reused within a save lineage.
- Churn estimate [I]: ~150–300 agent lifecycle events/s (R15 §9.3) ≈ 1 M ids per play hour, so 2³¹ ids last
  > 2000 h. If `m_NextNetId > 2³⁰` at session start (before any peer is connected), the host compacts: renumbers all
  live ids densely. Ids are only meaningful within one session and in the host's saves.
- **Provisional ids** (client predictions): `0x8000_0000 | (peerSlot << 27) | seq27`, allocated by the creating client,
  valid until the host maps them to real NetIds (§4.6).

### 2.2 Side tables (no ECS component)

A NetId component was rejected (R12 §1.2: full chunk copy of every replicated archetype, ~100–300 ms, archetype
doubling, 331 baked-archetype creation sites without it). Instead (R08 §10.3 option B, R12 §1.4):

| Structure (Core: `EntitySideTable`) | Content | Size for 1.5 M entity indices |
|---|---|---|
| `int[] netIdByIndex`, `int[] versionByIndex` | dense, indexed by `Entity.Index`, version-checked | 12 MB |
| `Dictionary<uint, long> entityByNetId` (Entity packed as `long`) | reverse map | ~40 MB worst case; replaced by a dense `int[] slotByNetId` window when ids are compact |
| `int[] slotByNetId` → shadow slot (host only) | dense shadow index, recycled after a despawn is acknowledged by all peers | 6 MB |

All side-table mutation happens on the main thread inside the capture/apply systems. Worker threads only see
immutable per-frame copies (§3.3.5).

### 2.3 Replication scope and family classification

Every entity in the world falls into exactly one family. The classifier (`FamilyClassifier`, Game layer, rules data
in Core) evaluates an archetype once and an entity only when the archetype rule is ambiguous.

| Family | Rule (evaluated in this order) | Treatment |
|---|---|---|
| **Excluded** | not matched by the save query: needs `Any(PrefabRef, LoadedIndex, ElectricityFlowNode/Edge, WaterPipeNode/Edge, ServiceRequest, WaterSourceData, City, SchoolSeeker, JobSeeker, CityStatistic, ServiceBudgetData, FloodCounterData, CoordinatedMeeting, LookingForPartner, TimeData, MeshColorPalette, <mod serializables>)` and `None(NetCompositionData, EffectInstance, LivePath, Temp, Deleted)` (`SerializerSystem.cs:171-214`) | never replicated (audio entities, tool previews, compositions, UI-only entities) |
| **Local artefact** | `PrefabRef.m_Prefab == Entity.Null` (Move It control points, R09 §2.3), any type listed as local by a mod profile (`MIT_*`), `Game.Common.Event` one-frame entities | never replicated |
| **Prefab** | has `PrefabData` | identity only via `PrefabID` (§2.8); state through the prefab-state channel |
| **Derived (keyed)** | `Lane`; `Secondary`; zone `Block`; prefab sub-objects (SubObjectSystem's own criterion: `Owner` = an owner whose `SubObject` buffer lists it, and no `ServiceUpgrade`/`Building`/`Secondary`, spot check above); electricity/water flow-graph entities; `Icon` (routed to the icon channel); gate bypass routes and their segments (`PrefabRef` ∈ {all `GateData.m_BypassPathPrefab`}); airway entities (`AirwaySystem`) | regenerated by vanilla systems on every peer; host-owned *state* on them travels through the K channel (§3.11.8) |
| **Scoped** | `ServiceRequest` entities | NetId assigned on the host, but replicated only inside inspection closures (§3.10); references to them from replicated data encode as "non-replicated" unless in scope |
| **First-class** | everything else in the save query: nets, aggregates, top-level and area-spawned objects, buildings, upgrades, areas, routes, waypoints, segments, water sources, City, budget, `TimeData`, statistic entities, palette, citizens, households, pets, companies, seekers, meetings, persistent event entities, journal entries, chirps, life-path events, vehicles, creatures, mod record entities (Better Bulldozer `OwnerRecord`) | NetId; full replication |

Mis-classification is the main duplicate/missing-entity risk. It is caught at runtime by the existence digests per
archetype family (§7.2) and during development by **discovery mode** (§7.6), which logs every save-query entity
that a *client* created outside the applier and outside a predicted tool apply — by definition a locally derived
family that the shipped rule table must list.

### 2.4 Assigning NetIds on the host

1. **Session start** (host enables hosting, before any peer connects): one pass over all first-class chunks assigns
   ids to entities without one (≈ 1 M entities × ~20 ns ≈ 20 ms [I]); ids restored from the save's `NetIdSystem`
   block are kept.
2. **Runtime, tag-driven**: the capture system (§3.3.1) assigns an id to every first-class entity carrying `Created`
   at capture time and emits a Spawn record in the same transaction.
3. **Runtime, discovery**: the amortized structural scan (§3.3.2) finds first-class entities that appeared without a
   `Created` tag (e.g. `EntityManager.CreateEntity` in mods) and spawns them; it also detects entities destroyed
   directly (no `Deleted` tag) by version-checking side-table entries in slices (≈ 4 k `Exists` checks per frame,
   full sweep ≈ 6 s at 1 M ids [I]).

### 2.5 Persistence and the join map

- `NetIdSystem : GameSystemBase, IDefaultSerializable, IPostDeserialize` (R12 §1.4) writes `(Entity, NetId)` pairs
  through the save's own entity table (`BinaryWriter.Write(Entity)` → save index, `CSE/BinaryWriter.cs:128-141`).
  On the client, `Deserialize` receives entities already remapped to the freshly created ones
  (`CSE/BinaryReader.cs:146-157`) — no private-table reflection.
- `Deserialize` runs inside an exclusive entity transaction: it only fills containers; `PostDeserialize` validates
  (`Exists`), rebuilds the side table and the reverse map.
- Format block: `{u16 formatVersion, u32 nextNetId, NativeList<Entity> entities, NativeList<int> ids}`; never empty
  (empty blocks throw, `CSE/ComponentSystemSerializer.cs:36-41`). Size ≈ 8 MB raw per 1 M pairs, ZStd-compressed with
  the save.
- Type identity is the assembly-qualified name: keep `AssemblyVersion` fixed and add
  `[FormerlySerializedAs("SkylineTogether.Game.Identity.NetIdSystem, SkylineTogether")]` (R12 §1.5).
- Saves without the block (or without the mod) load cleanly: the block is skipped (`ObsoleteSystemSerializer`,
  R12 §6.3); `SetDefaults` clears the table.
- The mod never creates entities whose only persistent components are its own (they would leak through `ClearSystem`,
  R12 §3).

### 2.6 Derived-entity keys

Derived entities never get NetIds. Replicated values that point at them, and K-channel state stored on them, use
portable keys. Every key is quantized (R13 §13: Burst float differences between CPUs).

| Family | Key | Resolution on the client |
|---|---|---|
| Edge, edge-connection, object lanes | PLK = `(ownerNetId, lanePrefabIdx, masterSlaveFlags, P(start), P(middle), P(end))`, `P(pathNode)` = owner index in the high 32 bits replaced by a NetId, low 32 bits unchanged | exact match in the owner's `SubLane` set |
| Node (intersection) lanes | PLK without the middle counter + quantized end points; reversed match allowed (curve position `1 − t`) | geometric fallback (R13 §2.5) |
| Prefab sub-objects | `(ownerNetId, prefabIdx, Aligned.m_SubObjectIndex \| qpos(1/64 m))` | mirrors `FindOldSubObject` (R13 §3.1) |
| Secondary objects | `(ownerNetId, prefabIdx, qpos)` | geometric |
| Zone blocks | `(edgeNetId, side = sign(dot(dir, edgeRight)), qpos(Block.m_Position.xz), size)` | exact, fallback nearest within 1 m on the same side (R13 §4.3) |
| Flow-graph edges/nodes | `(ownerNetId, role)` | by owner |
| Icons | `(ownerNetId, notificationPrefabIdx, targetNetId)` | handled by `IconCommandSystem.FindIcon` (idempotent) |

Each client keeps lazy key→entity caches per owner, invalidated when the owner gets `Updated`/`Deleted` or a lane is
`Deleted`, rebuilt after the owner's regeneration (R13 §2.5 mapping table).

### 2.7 Archetype, type and system dictionaries

- **Types**: the host sends the replicated type set as `(StableTypeHash, SizeInChunk, ElementSize, EntityOffsetCount,
  IsBuffer, IsEnableable, codec)`; wire `typeId` = index in the list sorted by `StableTypeHash` (R08 §5, §12.1). Any
  mismatch is a hard join failure.
- **Archetypes**: an `ArchetypeDef` record (sent once per peer per session, lazily) lists the `typeId`s of a host
  archetype after removing transient tags (`Created`, `Updated`, `Applied`, `BatchesUpdated`, `EffectsUpdated`,
  `PathfindUpdated`, `Deleted`) and local markers (`Highlighted`, `Hidden`, `Temp`, `Error`, `Warning`, `Animation`,
  `HiddenRoute`, `RecentlyChanged`, `UpdateNextFrame`, profile-listed types). The client caches `EntityArchetype` per `archId` via
  `EntityManager.CreateArchetype`.
- **Systems**: `SystemStateSync` descriptors are identified by type full name, mapped to a `systemId` in the
  handshake.

### 2.8 Prefabs and other machine-local indices

- **Prefab dictionary** (R04 §1.3): the host enumerates every registered prefab's `PrefabID` (type, name, hash),
  sorts them, and assigns `prefabIdx` (varuint). The handshake carries a fingerprint (XxHash128 over the sorted list);
  entries are sent lazily (`PrefabDef` record) the first time an index is used on a connection, or all at once during
  the join for the snapshot's referenced set. Clients resolve with `PrefabSystem.TryGetPrefab(PrefabID)` →
  `GetEntity(prefab)` into a dense `Entity[] prefabEntityByIdx`, and keep `int[] prefabIdxByEntityIndex` for the
  reverse direction (intents). Unresolved ids are a join-blocking mismatch (§6.7). Duplicate `PrefabID`s (first
  registration wins, `PrefabSystem.cs:151-176`) are flagged.
- **Zone types**: `ZoneType.m_Index` inside `Cell`/`VacantLot` is session-local (`ZoneSystem.cs:176-179`); a
  translator maps it through the zone prefab's `prefabIdx`.
- **`UpdateFrame`** (shared component): sent explicitly in spawn records (§3.6.3).
- **`PathNode`** packs `owner.Index` into a `ulong` (`PathNode.cs:20-49`); it appears only in `Lane` and
  `ParkingLane` (grep of `public PathNode m_`), i.e. in derived lane entities, so it never travels in first-class
  data; PLKs re-encode it (§2.6).

### 2.9 Revisions (conflict detection)

The host keeps a `uint16` **structural revision** per NetId, incremented whenever an S-class type of that entity
changes. Every S record carries the new revision; clients store it in `int[] revisionByIndex`. Intents carry the base
revision of every entity they modify or delete; the host rejects on mismatch (§4.6).

---

## 3. Replication engine

### 3.1 Pipeline overview

```
HOST (one capture per rendered frame, end of MainLoop)                    CLIENT (start of next MainLoop)
───────────────────────────────────────────────────────                    ────────────────────────────────
ECS chunks ──(RO dynamic handles, main thread, budgeted)──► staging pages        network thread
   │  Pass S  tag-driven structure (Created/Updated/Deleted…)                    │ ConcurrentQueue<Txn>
   │  Pass D  discovery (order versions, liveness sweep)                         ▼
   │  Pass V  value scan (chunk change versions)        ┌──────────────► ClientFrameStartSystem
   │  Pass M  motion frames for ticks since last send   │  canonical      (after EndFrameBarrier, before
   │  Pass G/P/I/K global, prefab state, icons, keyed   │  records         ToolSystem): capture local UI
   ▼                                                    │                  effects → apply txns → one
worker threads: canonical diff vs shadow, digests,      │                  structural batch → data writes →
per-peer dirty marks, encoding, compression             │                  events, system state, icons
   ▼                                                    │                        ▼
per-peer scheduler (budget, priority, interest) ────────┘            vanilla Modification1..End regenerate
   ▼                                                                   derived data from replicated sources
transport lanes: Control (reliable, ordered) · Bulk (reliable) · motion (unreliable)
```

The same canonical form, codecs and applier run in the other direction for client proposals (§4), and on the host
to commit validated proposals.

### 3.2 Schema: what is replicated and how it is encoded

**Type universe.** `SchemaBuilder` (Game layer) starts from the game's own serializer library
(`SerializerSystem.componentLibrary`, `SerializerSystem.cs:41`; R01 §4.1): every `ISerializable` type is a data
candidate, every `IEmptySerializable` type a presence candidate (enabled bit included for enableable ones). Mod types
are in the library automatically (R09 §1.1). Overrides (shipped table + mod profiles) then adjust codec and class.
The resulting `ReplicationSchema` (Core) is validated against both peers in the handshake (§2.7).

**Codecs** (Core: `CodecKind`; Game: `ComponentIO`):

| Codec | Encoding | Used for |
|---|---|---|
| `Raw` | `SizeInChunk` bytes copied from the chunk column; every `Entity` field at the offsets from `TypeManager.GetEntityOffsets` (public, R08 §5) rewritten to a canonical **NetRef** | default for eligible `IComponentData`; private fields included (`PlayerMoney.m_Money`, R04 §4.10) |
| `RawBuffer` | `u16/varuint length` + elements (each remapped) via `UnsafeUntypedBufferAccessor` (never the raw chunk column, which holds a process-local pointer, R08 §2) | eligible buffers |
| `Presence` | archetype membership only | zero-size tags and data-free `IEmptySerializable` types |
| `Enableable` | one bit per entity, sent with the type's data or alone | `Decoration`, `Locked`, `LumberResource`, `CarKeeper`, `PropertySeeker`, `CustomMeshColor` (R08 §8, R09 §1.1) |
| `Masked` | `Raw` with excluded byte ranges (field offsets via `UnsafeUtility.GetFieldOffset`) that stay client-derived | `Building.m_RoadEdge`/`m_CurvePosition` (recomputed by `RoadConnectionSystem`, R13 §8), `Chirp.m_Flags` `Liked` bit (R14 §5.4) |
| `Translated` | `Raw` + translator for machine-local integers | `Cell.m_Zone`, `VacantLot` zone index (§2.8) |
| `GameSerializable` | the type's own `Serialize`/`Deserialize` through `NetWriter`/`NetReader` (§3.11.2) | types failing the raw-eligibility audit (hidden entity refs in `FixedList*`/`fixed`/pointers, R08 §5) |
| `Custom` | hand-written | motion frames (§3.9), icons (§3.11.4), names, terrain/water tiles, cell maps |

**Canonical NetRef** (`uint32` inside canonical bytes, varuint on the wire): `0` null; `netId << 2 | 0`;
`prefabIdx << 2 | 1`; `provisional << 2 | 2`; tag `3` = extended, followed by a subcode: `0` reference to a
non-replicated entity (written as `Entity.Null`, counted in diagnostics), `1 + family` **KeyRef** to a derived-keyed
entity followed by its key (§2.6) — used when a replicated value points at a lane (`CarCurrentLane`, `ParkedCar`,
Traffic's connection buffers), a sub-object (`Connected` stops) or a block. In canonical bytes the extended form is
stored as an index into a per-type side list of keys.

**Raw-eligibility audit** (startup, both peers): reject managed, shared, chunk, blob, weak-asset and
`UnityObjectRef` types (R08 §5 filter); flag types whose fields include `FixedList*`, `fixed` arrays or pointers
(entity offsets cannot see inside them) → `GameSerializable` or excluded with a log line. No `BlobAssetReference`
exists in `Game` (R08 §5).

**Classes per (family, type)** — default by family, overridden per type:

| Class | Default members | Examples of overrides |
|---|---|---|
| S | all replicated types of structure families (nets, aggregates, static objects, areas, routes, water sources, City/budget entities, statistic entities, chirps, journal, persistent event entities) and every archetype change | `OnFire`, `Destroyed`, `Damaged`, `Abandoned`, `Condemned`, `UnderConstruction`, `Policy`, modifiers, `Building.m_Flags` are S even on agent-adjacent families |
| V | value types of agent and simulation families: citizens, households, companies, building `Efficiency`/`BuildingCondition`/consumers/producers, service facility state, `Extractor`/`Storage` amounts, `Tree.m_Growth` on first-class trees | — |
| M | `TransformFrame` of movers (custom codec) | — |
| N (never) | `InterpolatedTransform`, rendering types, `Surface` wetness/snow, `StreetLight` state, `HiddenRoute`, navigation (`CarNavigation`, `HumanNavigation`, `Blocker`), `PathOwner`/`PathElement` (O-scoped only), pathfind caches, reverse buffers rebuilt by KEEP'd reference systems (`SubObject`, `SubNet`, `SubArea`, `SubLane`, `ConnectedEdge`, `InstalledUpgrade`, `ConnectedBuilding`, `OwnedVehicle`, `Passenger`, `GuestVehicle`, `LaneObject`, `IconElement`) | — |
| data despite `IEmptySerializable` | reverse buffers whose runtime maintainers are OFF on clients: `Renter`, `HouseholdCitizen`, `HouseholdAnimal`, building `Student`; route `RouteWaypoint`/`RouteSegment` (the runtime source of truth, R13 §9); edge `Aggregated` | — |

**Co-replication groups** (always written in the same transaction and frame): `Transform` + Anarchy
`TransformRecord`; building creation + Plop the Growables `SpawnedBuilding`/`PloppedBuilding`; edge creation +
Zoning Toolkit `ZoningInfo`; a train controller + all `LayoutElement` members; a vehicle + its passengers'
`CurrentVehicle`; new edges + the aggregate they join (R09 §5.4).

### 3.3 Host capture

`HostCaptureSystem`, registered `UpdateBefore<HostCaptureSystem, SaveGameSystem>(SystemUpdatePhase.MainLoop)` — after
`ModificationEnd`, `UIUpdate` and `Rendering`, before `PrepareCleanUpSystem` (so every tag of the frame is still
present, R02 §5.2) and before the save snapshot point (epoch boundary, R12 §5.2). It reads only through
`ComponentType.ReadOnly` handles — an RW handle would stamp change versions and corrupt detection for everyone
(R08 §6.3) — after `query.CompleteDependency()` for the read types (safety checks are stripped, R08 §1). All work is
main-thread managed code (no Burst, no `IJobChunk`, hard constraint 1); heavy byte work is moved to plain .NET worker
threads on copied data.

#### 3.3.1 Pass S — tag-driven structure (every frame, cost ∝ edits)

Query `Q_tagged` = first-class ∪ derived-keyed scope with
`Any(Created, Updated, Deleted, Applied, BatchesUpdated, EffectsUpdated, PathfindUpdated)`,
`EntityQueryOptions.IncludeDisabledEntities | IgnoreComponentEnabledState`. For each entity:

1. **First-class with `Deleted`** → `Despawn(netId)`; its shadow slot is freed when every peer has acknowledged the
   transaction.
2. **First-class without NetId** (normally `Created`) → assign NetId (§2.4) → `Spawn` record with `archId`, shared
   values (`UpdateFrame`, `CoverageServiceType`), tag mask (`Applied`), and the canonical data of every replicated
   type (S and V, so a spawn is complete and atomic) plus enabled bits.
3. **Archetype signature changed** (replicated types only; transient tags and local markers ignored, R08 §12.5) →
   `AddTypes`/`RemoveTypes` with data for added types.
4. **S-class types** → canonicalize, `memcmp` against the shadow, emit `SetData` on difference and bump the structural
   revision (§2.9).
5. **Touches**: emit `Touch(netId, tagMask)` mirroring the host's `Updated`/`Applied`/`PathfindUpdated` on
   first-class non-movers, so clients re-derive exactly what the host re-derived (the vanilla tool tags the whole
   closure, R02 §7). `BatchesUpdated` is mirrored only together with a data or archetype change (host hover
   highlights produce `BatchesUpdated` noise, R09 §1.3 P9); `EffectsUpdated` is generated on the client by schema
   flags (§3.6.3). Movers never get `Updated` (R15 §0.4).
6. **Derived entities** with tags → Pass K (§3.11.8).

#### 3.3.2 Pass D — discovery (amortized, ~0.1–0.2 ms/frame)

Per-chunk records keyed by `ArchetypeChunk.SequenceNumber` hold the order version; chunks with a new sequence number
or a changed `GetOrderVersion()` are rescanned for entities without NetIds (spawned without `Created`) and for
archetype changes without tags (R08 §12.3 step 2). A slice of the side table is liveness-checked every frame
(§2.4, item 3). This pass catches mods that create, destroy or restructure entities directly.

#### 3.3.3 Pass V — value scan (amortized, budgeted)

A cursor walks all chunks of first-class archetypes. Per chunk and per replicated data type it compares
`chunk.GetChangeVersion(ref roHandle)` with the recorded version (equality test, wrap-safe, R08 §3.1). Changed
columns are copied (native `UnsafeUtility.MemCpy`) together with the `Entity` column into a pooled staging page, and
entity fields are canonicalized on the main thread (side-table lookups). Worker threads compare against the canonical
shadow, update digests and set per-peer dirty bits. V-type differences go to the scheduler; S-type differences found
here (values written without tags, e.g. by mods or rendering-phase systems) are emitted as S records in the next
transaction.

- Budget: default 1.0 ms main thread per frame, adaptive between 0.25 and 2 ms based on measured frame time.
- Why amortized: CS2 jobs take RW handles over whole `UpdateFrame` slices, so roughly 1/16 of simulation chunks are
  stamped per tick whether or not values changed (R08 §4); Mono version checks over ~25 k chunks × 8 types cost
  10–15 ms (R08 §13) — too much per frame, fine spread over ~1–2 s. **[M]** chunk counts and per-call costs in a
  100k city are measured in milestone M1.
- Inspected entities (§3.10) bypass the cursor: their chunks are checked every frame.

#### 3.3.4 Pass M — motion (every frame, ∝ ticks × movers/16)

For each simulation tick `T` in `(lastSentTick, frameIndex]` (0–8 per frame): query movers with
`SetSharedComponentFilter(new UpdateFrame(T & 15))` (exactly how `CarMoveSystem` slices, R15 §7.2), read `Transform`,
`TransformFrame` slot `(T >> 4) & 3` and flags, test each peer's interest region, quantize (§3.9.2). Slots stay valid
for 64 ticks, so reading at the end of the next MainLoop is safe (R15 §7.2). ≈ 1.3 k entities per tick for ~21 k
movers; ≈ 0.5–1 ms per frame in Mono at speed 4 **[M]**.

#### 3.3.5 Passes G, P, I, K and threading

- **G** (global/system state, §3.11.1) at per-system cadences; **P** (prefab state, §3.11.3) when an `Unlock` entity
  exists or every 5 s; **I** (icons, §3.11.4) from `Icon` entities with tags; **K** (keyed derived state, §3.11.8).
- Threading: main thread = ECS reads into staging + canonicalization; `WorkerPool` (Core, 2–3 dedicated threads) =
  diff, digests, record encoding, per-message compression; network thread = sending (§8.6). Workers never touch ECS;
  buffers come from pools (incremental GC with a 3 ms slice, R06 §1).

#### 3.3.6 Shadow and digests

- **Canonical shadow** per type: fixed-size arena `slot × SizeInChunk` plus a buffer arena (`offset,length`) and a
  presence bitset; holds the last captured host value in canonical form, so serialization never reads live ECS.
  Size ≈ replicated payload, 30–100 MB (R08 §13).
- **Digests**: for each `(typeId, bucket = netId & 4095)` a 64-bit accumulator = XOR of `xxHash64(netId ‖ canonical
  bytes)`, updated in O(1) whenever the shadow changes (remove old hash, add new). Used by anti-entropy (§7).
- **Per-peer replication state**: `ushort dirtyTypeMask[slot]` + `byte priority/staleness[slot]` ≈ 3 MB per peer at
  1 M slots.

| Host per-frame CPU (main thread) | Typical | Worst case / mitigation |
|---|---|---|
| Pass S | 0.05–0.3 ms | big tool applies or terrain re-snaps: 2–10 ms for one frame (accepted) |
| Pass D | 0.1–0.2 ms | budgeted |
| Pass V | 1.0 ms | budgeted, adaptive |
| Pass M | 0.3–1.0 ms | interest radius shrinks under load |
| Passes G/P/I/K | < 0.3 ms | — |

### 3.4 Change classes and lanes

| Class | Content | Delivery | Lane (§8.2) | Scope | Budget share (default) |
|---|---|---|---|---|---|
| S | spawns, despawns, archetype changes, S-type data, touches, prefab state, names, icons in view, intent acks, dictionary defs | reliable, ordered, inside transactions | Control | all live peers | uncapped (alert if > 30 %) |
| V | per-entity value updates | reliable, ordered, coalesced (always the latest shadow value) | Control | per-peer priority | ≈ 30 % |
| G | system state, clock-adjacent globals, budget arrays, demand | reliable | Control | all | ≈ 5 % |
| K | keyed state on derived entities (zone cells are S-priority) | reliable | Control | all (zone cells) / interest (lights, trees) | inside S/V shares |
| M | motion frames, view-entry baselines, clock samples | unreliable, sequenced | Control lane, unreliable flag | interest | ≈ 50 % |
| O | inspection closures, scoped entities | reliable | Control | requesting peer | top priority inside V |
| B | snapshot, terrain/splatmap tiles, water tiles, cell maps, snow/wind | reliable, chunked | Bulk | all | ≈ 15 % (snapshot: all spare capacity) |

V shares the ordered Control stream with S so a V value can never overtake the S record that created or changed the
same entity (no per-type revision bookkeeping needed); the scheduler keeps the reliable backlog short
(`m_cbPendingReliable` ≤ 128 KB) to bound head-of-line delay (R06 §5.3-§5.4). Moving V to its own lower-priority lane
(with per-type revisions) is a later optimization if measurements demand it.

### 3.5 Transactions, ordering and consistency

- **Epoch** = one host capture = one transaction (`Txn`), tagged with the host `frameIndex` at capture. Empty
  transactions are still sent as heartbeats (they carry the clock).
- **Content order inside a `Txn`**: dictionary records (`ArchetypeDef`, `PrefabDef`) → the peer's `AckMap`
  (provisional → NetId, §4.6) → structure (despawn, spawn, add/remove types) → data (`SetData`, enabled bits,
  touches) → keyed state → events and icons → globals. The client still applies structure as one batch and resolves
  references after all spawns of the transaction exist, so order only matters for dictionaries and the ack map.
- **Atomicity**: a transaction is applied completely within one client frame — never split (S records of one host
  frame form one state). V records are self-contained single-entity/type values. Co-replication groups (§3.2) never
  span transactions.
- **Strict order**: the client applies transactions in epoch order; a gap (should not happen on a reliable stream)
  triggers a resync.
- **Snapshot boundary**: the transaction captured in the frame of a join snapshot is flagged; the joiner's backlog
  starts at the next epoch (§6.5).

### 3.6 Client apply

#### 3.6.1 Client frame order (registrations)

| Order | System (registration) | Role |
|---|---|---|
| 1 | vanilla `EndFrameBarrier` (`SystemOrder.cs:49`) | plays back last frame's UI command buffers |
| 2 | **`ClientFrameStartSystem`** — `UpdateAfter<ClientFrameStartSystem, EndFrameBarrier>(MainLoop)` (placed immediately after its parent, R11 §6.2) | capture stage A (local UI effects, §4.2), then apply host transactions, keyed state, events, globals |
| 3 | `RaycastSystem`, `PrefabSystem`, `CityConfigurationSystem` | vanilla |
| 4 | `ToolSystem`: PreTool (`OriginalDeletedSystem` sees host deletions this frame) → ToolUpdate (**`NoPredictGateSystem`** `UpdateBefore<…, ToolOutputSystem>(ToolUpdate)`) → ApplyTool (**`ToolApplyCaptureSystem`** `UpdateBefore<…>(ApplyTool)`) → PostTool (`SubElementDeleteSystem`) | local tools, prediction, capture |
| 5 | **`ClientChangeCaptureSystem`** — `UpdateBefore<…>(SystemUpdatePhase.Modification1)` | capture stage C (tool and mod results, §4.2) |
| 6 | `ModificationSystem` Mod1 … ModEnd; **`AggregateReconcileSystem`** `UpdateAfter<…, AggregateSystem>(Modification2B)` | derived regeneration; host aggregate membership enforced |
| 7 | `UIUpdateSystem` incl. **`SessionUISystem`**, **`ClientUIApplySystem`** (`UpdateAt`, UIUpdate: names via `SetCustomName`, which needs the open `EndFrameBarrier` window, R10 §0.4) | UI |
| 8 | `PreRenderSystem` → PreCulling: **`MotionJitWriterSystem`** `UpdateBefore<…, PreCullingSystem>(PreCulling)` | motion slots (§3.9.3) |
| 9 | Rendering: **`RemoteGhostOverlaySystem`** `UpdateBefore<…, OverlayRenderSystem>(Rendering)` | remote cursors/previews (§4.7) |
| 9b | **`ClientChangeFinalizeSystem`** — `UpdateBefore<…, SaveGameSystem>(MainLoop)` | capture stage C2: finalize and send this frame's `ChangeSet` (§4.2) |
| 10 | `PrepareCleanUpSystem`, Cleanup | vanilla |
| 11 | LateUpdate: **`SessionClockSystem`** `UpdateBefore<…, SimulationSystem>(LateUpdate)` (follower mode; `SimulationSystem` disabled, §5.1) | ticks the KEEP'd simulation set |

#### 3.6.2 `ClientFrameStartSystem` steps

1. **Capture stage A** (local UI effects that just appeared through `EndFrameBarrier`) — before anything else
   mutates the world (§4.2).
2. **Dequeue** complete transactions in epoch order: live — all available; catch-up — until 3 ms or 64 transactions
   per frame.
3. **Decode** records into an op list (worker-decoded payloads are already in pooled buffers).
4. **Structural batch** — the frame's single sync point (every structural `EntityManager` call completes all jobs,
   R08 §9):
   - despawns: add `Deleted` (never `DestroyEntity`; `CleanUpSystem` destroys at the end of the frame and
     `SubElementDeleteSystem` handles sub-nets, sub-areas, sub-routes and owned vehicles, R11 §6.3);
   - spawns grouped by `(archId, UpdateFrame, CoverageServiceType, tagMask)`: `EntityManager.Instantiate(prototype,
     n, Allocator.Temp)` from a cached prototype entity per group (prototype = the client archetype + shared values +
     Unity `Prefab` tag so no game query sees it; destroyed on session end/preload); fallback
     `CreateEntity(archetype, n)` + `SetSharedComponent`;
   - archetype changes grouped by (from, to) with `AddComponent/RemoveComponent(NativeArray<Entity>, ComponentTypeSet)`;
   - tag adds: `Updated` (touches and closure), `Applied`, `BatchesUpdated`, `EffectsUpdated`, `PathfindUpdated`.
5. **Data writes** into the final chunk positions: per `(chunk, type)` one RW
   `GetDynamicComponentDataArrayReinterpret<byte>` (one version stamp per chunk/type), `memcpy` canonical bytes,
   rewrite NetRefs to local entities (`entityByNetId`, `prefabEntityByIdx`), buffers via `ResizeUninitialized` + copy,
   `SetComponentEnabled` for enabled bits; update the client shadow (S types) and revisions.
6. **Pending references**: a NetRef to an entity not yet spawned writes `Entity.Null` and records
   `(entity, type, offset, netId)`; resolved at the end of this step for spawns of the same transaction, otherwise
   retried each frame; TTL 10 s → repair request (§7.4).
7. **Synthesized events** (§3.6.5) via `EntityManager.CreateEntity`.
8. **System state and globals** (after the batch, which completed jobs; plus `Readers.Complete()` where a system
   tracks readers manually, R11 §5.3).
9. **Icon commands** through one `IconCommandSystem.CreateCommandBuffer()` buffer (played back at ModificationEnd).
10. **Keyed state** (§3.11.8) for keys whose owners were regenerated in earlier frames.
11. **`UpdateFrame` fixups** for entities created last frame without `Applied` (second `SetSharedComponent`, R15 §4.4).
12. **Gate check**: re-assert the gate table if any flag changed (systems re-enable themselves on preload, R11 §8.3).

#### 3.6.3 Tag rules

| Operation | Tags added on the client | Reason |
|---|---|---|
| Create (any first-class) | archetype already contains `Created`+`Updated` (R02 §9.1.2); **`Applied`** when the host entity had it, **and always** for movers and for archetypes with an `OwnedVehicle` buffer | `Applied` keeps the given `UpdateFrame` (`UpdateGroupSystem.cs:408-416`) and stops `ParkedVehiclesSystem` from spawning parked service vehicles (`ParkedVehiclesSystem.cs:1683-1700`); simulation-spawned (zoned) buildings get no `Applied`, which avoids spurious `ToolFeedbackSystem` sparkles, and their `UpdateFrame` is fixed next frame; **[M]** confirm that `PreCullingFlags.Applied` (`PreCullingSystem.cs:1254-1285`) triggers no placement effect on spawned agents |
| Update of static objects, nets, areas, routes | host touches (`Updated` on the host's touched set) + client-computed closure for net data changes: edge → both nodes + aggregate; node → connected edges, their other nodes, aggregates (Move It's closure, R02 §7) | derived systems key on `Updated` |
| Visual-only change (`Damaged`, `Destroyed` visuals, colours, `Unspawned`) | `BatchesUpdated` | avoids re-running `Updated` consumers |
| Effect-relevant change (schema flag: `Car.m_Flags`, role states with boarding bits, `ParkedCar` presence, `Stopped`, `OnFire`, `Efficiency`) | `EffectsUpdated` (each `LayoutElement` member for trains) | `EffectControlSystem` only re-evaluates tagged entities (R15 §3.5) |
| Lane-relevant change on edges (policies, `Upgraded`, traffic config) | `PathfindUpdated` mirrored | client pathfind graph serves local previews |
| Motion | **none** — `Updated` resets all 4 `TransformFrame`s and re-randomizes lane positions (R15 §0.4) | — |
| Delete | `Deleted` on the entity and every first-class child the host deleted; host touches for surviving neighbours | R11 §6.3 |
| Events | only synthesized ones (§3.6.5); never `Damage`/`Destroy`/`Ignite`/`Impact`/`AddHealthProblem`/`Modify`/`ResetTrip`/`RentersUpdated`/`PathTargetMoved` from the host | consumers are OFF or would double-apply (R11 §6.3) |

#### 3.6.4 Creation and initializers

Creation uses the **host archetype** (minus transient tags and local markers), so components that host systems added
after spawn (`CrimeProducer`, `MailProducer`, `Efficiency`, mod components) are present immediately; KEEP'd
initializers that `AddComponent` an existing type are no-ops. All initializers that append, overwrite or randomize
host-owned fields are OFF (§5.2), so no post-initializer re-apply pass exists (R11 §6.4). Three remaining overwrites
are corrected by the next host delta: `UpdateGroupSystem`'s frame reset for created movers, `Game.Net.InitializeSystem`'s
coverage reset, and `WaterSourceInitializeSystem` for prefab water sources (R11 §6.4).

#### 3.6.5 Synthesized events

| Trigger in replicated state | Event created by the client applier | Consumers that refresh |
|---|---|---|
| prefab becomes unlocked (prefab-state channel) | `Event + Unlock{prefab}`, and the `Locked` bit is written directly (client `UnlockSystem` is OFF) | toolbar, asset menu, dev tree, policies, infoviews, statistics, "new" highlights (R14 §2.3) |
| prefab relocked (mods, repair) | `Locked` enabled + harmless `Unlock` on an already-unlocked prefab with the needed marker component (`UIObjectData`, `ServiceData`, `DevTreeNodeData`, `PolicyData`) **[M]** | same (R14 §2.4 step 4) |
| `MilestoneLevel` rises from a to b | `Event + MilestoneReachedEvent` for i in (a, b] | milestone list and popup (each player's own popup setting) (R14 §1.5) |
| renter set of a building changes | `Event + RentersUpdated{building}` | `SubObjectSystem` placeholder regeneration (R13 §3.1) |
| human activity prop change in the motion stream | `Event + SubObjectsUpdated` | hand-held props (R15 §2.6 step 7) |
| route colour change | `Event + ColorUpdated` | `MeshColorSystem` (R10 §3.5) |
| simulation Transaction icon event | `IconCommandBuffer.Add(..., IconClusterLayer.Transaction)` | level-up sparkle, meeting icons (R14 §4.5) |
| XP message | `MilestoneUISystem.AddMessage(new XPMessage(frame, amount, reason))` | XP toasts (R14 §1.5) |
| chirp spawn | none (spawned entity carries `Created`) | `ChirperUISystem.chirpAdded`, `ChirpLinkSystem` |

### 3.7 Wire formats

All encodings live in Core (`PacketWriter`/`PacketReader`: little-endian, LEB128 varints, zig-zag for signed deltas,
bounds-checked reads, pooled buffers).

```
Message      := kind:u8  flags:u8  length:varuint  payload            (one Steam message ≤ 512 KiB, R06 §4.2)
Txn          := epochDelta:varuint  hostFrameDelta:varuint  txnFlags:u8  recordCount:varuint  Record*
Record       := op:u8  body
  Spawn      := netIdDelta:zz  archId:varuint  updateFrame:u8  [coverage:u8]  tagMask:u8  revision:u16
                enabledBits:bitset(enableableCount)  payload(type)* (archetype order, replicated types only)
  Despawn    := netIdDelta:zz
  AddTypes   := netId  count:u8  (typeId:varuint payload)*      RemoveTypes := netId count:u8 typeId*
  SetData    := netId  typeId:varuint  [revision:u16 if S]  payload
  SetEnabled := netId  typeId  bit:u8
  Touch      := netIdDelta  tagMask:u8 (Updated|Applied|PathfindUpdated|BatchesUpdated)
  KeyedSet   := family:u8  key  typeId  payload           KeyedDelete := family key
  Global     := systemId:varuint  payload                 PrefabState := (prefabIdx flags:u8)*
  Icon       := ownerNetId  op:u8(add/remove/event)  prefabIdx  priority:u8  layer:u8  flags:u16  [targetNetId]
  NameSet    := netId  utf8String                          AckMap := clientSeq  status:u8  (provisional netId)*
  ArchetypeDef := archId  typeId*                          PrefabDef := prefabIdx  type:str  name:str  hash:16B
payload(Raw) := canonical bytes with NetRefs as varuint, then byte-RLE of zero runs (most canonical deltas are sparse)
```

- **Batch compression**: a Control message aggregates records of one or more transactions up to ~64 KB and is
  compressed as a whole (LZ4 frame through an `ICompressor`; Game layer uses the game's native ZSTD/LZ4,
  `CompressionUtils`, R06 §5.4; Core tests use a managed implementation).
- **No XOR deltas on Control**: a per-peer base would need per-peer shadows (×7 memory); full canonical values plus
  batch compression are robust under coalescing. XOR is used only for motion (vs. the entity's previous frame in the
  same packet stream, with absolute escapes) and for bulk fields (vs. the last acknowledged version, small per-peer
  bases).

| Typical record | Uncompressed | After batch compression [I] |
|---|---|---|
| Building spawn (all replicated types) | 250–500 B | 120–250 B |
| Citizen spawn | 70–110 B | 40–60 B |
| Vehicle spawn | 48–80 B (R15 §9.3) | 30–50 B |
| Citizen value update (`Citizen`, 22–24 B) | ~30 B | 8–14 B |
| Edge spawn incl. curve | 120–200 B | 70–120 B |
| Touch | 2–4 B | 1–2 B |
| Motion frame (Q-B) | 10–11 B (R15 §9.1) | — (already entropy-dense) |

### 3.8 Bandwidth budgeting and prioritization

**Per-peer budget** `B_peer = min(0.8 × Steam's estimated send rate (m_nSendRateBytesPerSecond), user cap)`; default
caps 600 KB/s over relay, 2 MB/s direct/LAN; host total upload cap (user setting, default 2 MB/s) divided among peers.
Shares as in §3.4, rebalanced every 250 ms (unused share flows to V, then B).

**Interest model.** Each client sends a `ViewReport` (unreliable, 10 Hz or on > 5 % change, ~40 B):
`{pivot.xz, zoom, camera position, forward, fovY, aspect, farClip, followedNetId, selectedNetId, activeInfoview}`
(R15 §7.1). The host keeps a coarse spatial grid (128 m cells over the 14.3 km map, 112×112) mapping spatial entities
(and, through anchors, non-spatial ones: citizen → household → `PropertyRenter` building; company → building) to
cells. Interest region = disc `r = clamp(k·zoom, 300 m, 2500 m)` ∪ capped frustum footprint, with hysteresis
(enter `r`, leave `1.25 r`).

**V scheduler** (Core `PriorityScheduler`): every dirty `(slot, typeMask)` sits in one of 8 priority levels per peer:

| Level | Members |
|---|---|
| 0 | inspection closure of this peer (§3.10) |
| 1 | entities in the interest region whose dirty types are visible (`Efficiency`, `BuildingCondition`, consumers, service state, `Tree`) |
| 2 | anchored entities (households, companies, citizens of in-region buildings) |
| 3–6 | everything else, promoted one level per staleness quantum (2 s, 4 s, 8 s, 16 s) |
| 7 | historical and statistics bulk (sample cadence) |

Each send tick (every frame) pulls from level 0 downward until the V share is spent, serializing the **current**
shadow value (coalescing). Staleness promotion guarantees every change is delivered in bounded time (≈ 60 s at a
50 KB/s V share for a 100k city [I]).

**Degradation ladder** (when the budget is short): motion far-field to half rate → motion interest radius shrinks →
V staleness grows → bulk tiles slow down → S is never throttled (if S alone exceeds the link for > 10 s, the client is
told to resync).

### 3.9 Moving agents (vehicles, citizens, animals)

#### 3.9.1 Existence (global, S-class)

Every vehicle, creature, parked car and train member is first-class. Spawn payloads carry the full replicated set
(§1.3); movers spawn from the prefab-derived host archetype (moving, stopped or controller, R15 §4.1) with the host
`UpdateFrame` and `Applied` (§3.6.3). Parked ↔ moving swaps arrive as archetype changes with the vanilla type sets
(R15 §3.1, §4.2). Reference systems rebuild `OwnedVehicle`, `CarKeeper.m_Car`, `GuestVehicle`, `Passenger`,
`CurrentTransport`, `OwnedCreature`, `RouteVehicle` and `LaneObject` from `Created`/`Deleted` (R15 §4.5); owners and
`CurrentVehicle` targets are spawned in the same or an earlier transaction (co-replication). Lane references
(`CarCurrentLane.m_Lane`, `ParkedCar.m_Lane`, train front/rear lanes) use PLKs (§2.6), sent at spawn and on lane
change; lane remaps target only live lane entities (Burst indexers in `Vehicles.ReferencesSystem` are unguarded,
R15 §6). `TripSource` is excluded from vehicle payloads so `Vehicles.InitializeSystem` does not reposition them
(R11 §2.11).

#### 3.9.2 Motion stream (M-class)

- For each tick `T` the host sends, per peer, frames of entities with `u = T & 15` inside that peer's region:
  `Motion := tickT:varuint count:u16 (peerSlot:varuint frame:Q-B)*` (tick implicit for all entries).
- **Q-B** (R15 §9.1): position delta at 1/64 m (12/12/10 bits, escape to absolute), yaw 12 + pitch 7 bits, speed
  10 bits + sign, flags "unchanged" bit (else 18 bits), human state 3 bits + activity-unchanged + timer "+1" bits —
  ≈ 10–11 B. Velocity matters for the Bézier tangents (`ObjectInterpolateSystem.cs:4354`); the exact f16 velocity is
  added (+4 B) for followed/inspected entities.
- **Entry baseline**: when an entity enters a peer's region, all 4 host slots are sent (Q-A, ≈ 112–128 B) so the
  client interpolates immediately (R15 §7.2).
- `peerSlot` = a per-peer dense index (2 B typical) assigned on interest entry; mapping sent reliably.

#### 3.9.3 Client: staging ring, just-in-time slot writer, clock lag

- Per replicated mover a staging ring holds the last 8 `(T, frame)` entries; out-of-order inserts are harmless.
- `MotionJitWriterSystem` (PreCulling, before `PreCullingSystem`, after `RenderingSystem` computed the render tick
  `R`, R15 §2.6) writes, for near-camera movers, the staged frames for `F1 = u + 16·((R − u − 32) >> 4)`, `F1 + 16`
  and prefilled `F1 + 32` into slots `(F·>>4)&3` (the vanilla ring then always holds exactly what
  `ObjectInterpolateSystem` will read, R15 §2.2-§2.3).
- Missing frames are extrapolated (`p + v·4/15`, same rotation/flags, `m_StateTimer + 1`), marked synthetic and
  replaced when the real frame arrives; otherwise the object would snap back 64 ticks (R15 §2.6 step 4).
- `Transform`/`Moving` are kept coherent after each write (`Transform = p_latest + v_latest·2/15`), without
  `Updated` (R15 §2.6 step 6); culling needs `Transform` within ~278 m (`PreCullingSystem.cs:1152-1171`).
- **Clock lag `D`**: the client clock runs `D = max(0, L − 16 + margin)` ticks behind the host, where `L` = measured
  one-way delay in ticks (latency + jitter + one host frame) × 60 × speed. Typical: speed 1, 100 ms → `D ≈ 0–4`;
  speed 4, 120 ms → `D ≈ 25–37` (R15 §2.6 step 5). `D` adapts slowly (±1 tick/s) to avoid visible time warps.
- Side effects reproduced: `EffectsUpdated` when the OR of light bits over the 4 slots changes; `SubObjectsUpdated`
  events for activity prop changes (R15 §2.6 step 7).
- **Selection**: replicated movers are hit-testable through `LaneObject` buffers that `Vehicles/Creatures.ReferencesSystem`
  maintain from the replicated lanes; movers off-lane use the moving search tree (R15 §5).
- **Leaving interest**: when a mover leaves the region, the JIT writer pins its last pose into all 4 slots with zero
  velocity, so the vanilla ring never wraps back 64 ticks; it stays visible but static (existence and `Transform` remain
  exact at its last known position). The interest radius is kept ≥ the distance at which LOD still draws movers; hiding
  far movers with `Hidden` + `BatchesUpdated` is an option to evaluate (R15 open question 3). A camera jump shows newly
  covered movers stale for one round trip until their entry baselines arrive (R15 §11 risk 12).

### 3.10 On-demand inspection and scoped replication

When a client selects or follows an entity (`ViewReport.selectedNetId/followedNetId`), the host computes its closure
(R15 §8.2): citizen → household, creature, vehicle, workplace, school, residence, company; vehicle → layout members,
passengers, owner, `ServiceDispatch` requests (scoped entities spawned on that client only, ref-counted); building →
renters, occupants, owned vehicles, upgrades, icons; line → route, waypoints, segments, route vehicles and their
`PathOwner`/`PathElement`/navigation lanes. The closure is level 0 for that peer (sent within one RTT) and refreshed
on every `frameIndex % 256 == 0` boundary (the panel refresh cadence, `SelectedInfoUISystem.cs:181, 383`). After
applying a boundary snapshot the client calls `SelectedInfoUISystem.RequestUpdate()`, so every player's panel shows
values sampled at the same host tick. The selection is shown only once the closure is complete (`CitizenUIUtils` throws
on a missing household, R15 §6).

### 3.11 Global state, prefab state, icons and fields

#### 3.11.1 System state (`SystemStateSync`)

| System | Mode | Cadence | Notes |
|---|---|---|---|
| `TaxSystem` | `GameSerializable` (`Deserialize`, full-tag context) | on change | after `Readers.Complete()`; missing `FishResource` tag would shift rates (R11 §5.2) |
| `Residential/Commercial/IndustrialDemandSystem` | `GameSerializable` | 4 Hz | UI reads `m_Last*` fields |
| `TradeSystem` | `GameSerializable` | on change, ≤ 1 Hz | |
| `CityStatisticsSystem` | `GameSerializable` with empty event queue | per sample (8192 ticks) | never `PostDeserialize` (R11 §5.2) |
| `CityServiceBudgetSystem` | reflection on its income/expense arrays (not `ISerializable`) | 2 Hz | client system OFF |
| `MilestoneSystem` cached UI fields (`m_LastRequired`, `m_NextRequired`, `m_Progress`, `m_NextMilestone`) | reflection | on change | client system OFF, so `achievedMilestoneXP/nextMilestoneXP` do not freeze (R14 §1.4) |
| `CountHouseholdData`, `CountWorkplaces`, `CountCompanyData`, `CountStudyPositions`, `CountResidentialProperty`, `CountVehicleData`, `CountConsumption`, `CountCityStoredResource`, `CityProductionStatistic`, `ProductionSpecialization`, `ElectricityStatistics`, `WaterStatistics`, `Electricity/WaterTrade` | `GameSerializable` after a per-system side-effect audit; else reflection on the UI-read fields | 1 Hz / per update | audit is a milestone-M4 task [M] |
| `GameModeGovernmentSubsidiesSystem` | KEEP (derived from replicated money and budget) | — | its `Enabled` drives the budget UI line (R14 §0.7) |
| `ClimateSystem` | digest every 256 ticks; on mismatch apply weather state (prefab ids, current/next weather) | on change | climate curves are local pure functions |
| `WaterSystem` globals | public setters (`SeaLevel`, legacy flag, sim speed, `IWaterSimulation` params) | on change | |
| `NameSystem`, `CityConfigurationSystem.cityName` | NameSet records | on change | |
| Mod systems (`IDefaultSerializable`) | per profile (§9) | on change | |

#### 3.11.2 `NetWriter`/`NetReader` and contexts

`NetContextFactory` builds the read context once per session: `new Context(Purpose.LoadGame, Game.Version.current,
default, formatTagCount, Allocator.Persistent)`, then sets every `FormatTags` bit through the internal
`ContextFormat.EnableTag` via reflection (or `UnsafeUtility.As` on the bit list) (R01 §11.1-§11.2, R11 §5.3);
writes use the 3-argument constructor. `NetWriter : IWriter` / `NetReader : IReader` are structs that keep cursor and
buffer in native containers (writers and readers are copied by value into nested calls, R01 §2.2) and map
`Write(Entity)`/`Read(out Entity)` to NetRefs, including index-only entities (`PathNode`, R01 §3).

#### 3.11.3 Prefab-state channel

Host pass P scans the few thousand prefab entities: `Locked` enable mask, `PlacedSignatureBuildingData` presence,
`UnlockRequirementData.m_Progress` (low rate, not saved, reset on load) (R14 §2.4). It emits `PrefabState` records
(unlocked, relocked, signature placed, progress) and a 30 s hash. Clients apply as in §3.6.5. Tutorial-family prefabs
and their forced UI groups are excluded (tutorials are off in sessions, §5.3).

#### 3.11.4 Icons

Host pass I watches `Icon` entities (`Created`, `Deleted`, changed `Icon`/`Target`/`Animation`) and emits per-owner
add/remove records for `Default`-layer, non-`Temp` icons; simulation `Transaction` icons become icon events. Clients
issue them through a replication-owned `IconCommandBuffer` (R14 §4.5), so vanilla maintains `Icon` entities,
`IconElement`, clustering and animations. Client-local derived producers (road connection, connection warnings,
`RoutePathReadySystem`) agree with the host because their inputs are identical; `Add` is idempotent
(`IconCommandSystem.cs:186-338`). Residual disagreements are removed by the per-owner icon digest (§7.2). If flicker
is observed, the optional Harmony filter H1 (App. B) drops non-`Temp`/`Marker`/`Transaction` vanilla commands on
clients.

#### 3.11.5 Cell maps

On each host cell-map update (detected by a cheap per-map digest every 64 ticks) the host sends an XOR delta against
the last version acknowledged by that peer, zero-RLE + ZSTD, on the Bulk lane. Clients write with
`GetMap(readOnly: false, out deps)`, `deps.Complete()`, copy, `AddWriter(default)` (R07 §8.1). Largest maps are 256²
(≈ 128–512 KB raw), deltas typically < 10 KB.

#### 3.11.6 Terrain heightmap and splatmap

- Host captures every applied stamp (`Brush` struct, type, brush prefab, clamped delta) from its own tools (the
  stage-B capture system also runs on the host and reads the `Temp+Brush` entities at the start of ApplyTool, R07 §2.2;
  `Temp` entities are outside the save query, so pass S never sees them) and from client proposals (§4.3), and
  forwards them to other clients for immediate visuals.
- After a stroke settles (no stamp for 200 ms) the host reads back the union dirty rectangle of `m_Heightmap` with
  a non-blocking `AsyncGPUReadback`, splits it into 64×64 tiles (8 KiB raw each), and sends changed tiles
  (`XxHash64` + zero-RLE + ZSTD) on the Bulk lane.
- Clients upload into a cached R16 `Texture2D`, `Graphics.CopyTexture` into `TerrainSystem.heightmap`, then call
  `OnAreaChanged`, `WaterSystem.TerrainWillChangeFromBrush` and `UpdateMinMax` (all public, R07 §5.2).
- Splatmap (material painting) uses the same tile protocol (R07 §5.3).
- Authority check hashes `m_Heightmap` tiles only, never the cascade or CPU heights (they include GPU-rasterized roads,
  R07 §5.2 step 5).

#### 3.11.7 Water

Clients run `WaterSystem` for smooth visuals with mirrored sources, sea level and parameters; pause follows the host
speed (`GetTimeStep` returns 0 when paused). The host reads back one 256×256 tile of `WaterTexture` per second
round-robin plus "hot" tiles near recent source/terrain changes (non-blocking `AsyncGPUReadback`), quantizes (depth
u16 mm, velocity f16×2, pollution u8 = 7 B/texel ≈ 448 KB/tile raw), compresses, and sends on Bulk. Clients
`Graphics.CopyTexture` the region into `WaterTexture`, set `ForceUpdateFlow = true`, and briefly disable active-cell
culling so newly wet tiles simulate (R07 §6.7). Full refresh period ≈ 64 s; low-bandwidth mode freezes client water
(`WaterSimSpeed = 0`) and shows snapshots only.

#### 3.11.8 K channel — state on locally derived entities

| Family | Key (§2.6) | Replicated state | Applied |
|---|---|---|---|
| zone cells | block key | `m_Zone` per cell as zone `prefabIdx` (RLE), excluding `Overridden` cells | at frame start once the block exists (normally the frame after the edge change), plus `Updated` on the block (mirrors `ApplyZonesSystem`, R13 §4.3) |
| prefab sub-objects | sub-object key | `Tree`, `Decoration` bit, Move It/Anarchy transform overrides, mod components, keyed delete (Better Bulldozer) | frame after the owner's regeneration (`SubObjectSystem` playback at Mod2B) |
| secondary objects | secondary key | secondary-tree growth, signal pole `TrafficLight.m_State` (interest-managed) | as above (Mod4B) |
| traffic lights | node NetId (first-class) | `TrafficLights` state | S/V data |
| lanes (optional, interest) | PLK | signal/occupancy for infoviews | v2 |
| flow edges (optional) | `(ownerNetId, role)` | flow/capacity for the electricity/water infoviews | v2 |

Host detection: derived entities with tags in pass S (regeneration or mod writes) plus an amortized version scan over
K-whitelisted types on derived families (tree growth is simulation-driven). Keys that do not resolve within 2 s
trigger a per-owner repair (§7.4).

#### 3.11.9 Snow, wind and surfaces

- Snow: client simulates (tick-driven, fixed 0.2 step, rendering-only consumers, R07 §7); the host sends a compressed
  1024² snapshot on weather changes and at most every 5 game-minutes.
- Wind: 64² cells + constant wind every ~30 s; client wind keeps running for visuals.
- **Deliberate fidelity exception**: object `Surface` wetness/snow/dirt (`WetnessSystem`, `DirtynessSystem`, per-chunk
  randomness, R11 §3) stays client-simulated from replicated climate. It is visually equivalent but not
  bit-identical, and it would cost hundreds of thousands of V updates. Snow-dependent placeholder sub-objects are
  therefore excluded from strict sub-object digests (§7.2).

---

## 4. Client action path

### 4.1 Principle: capture effects, not inputs

The game has 182 trigger bindings using eight different mutation mechanisms (R10 §0-§2), and the user's mods add many
direct writes outside the Temp pipeline (R09 §5.10). Re-implementing every handler as a bespoke intent would be large,
brittle and never complete. Instead, on a client:

1. the vanilla or mod code runs **locally** — that is the prediction, with exact vanilla behaviour (snapping, splits,
   auto-connected sub-nets, tool state machines, sounds);
2. the **effects** on replicated state are captured at a few well-defined points of the frame (§4.2);
3. effects with a vanilla executor on the host become **semantic intents** (the host calls the same public API, so
   side effects such as triggers, `Updated` on the budget singleton or modifier recompute happen naturally); all other
   effects become a generic **ChangeSet intent** (a proposed state delta in the canonical format of §3);
4. the host validates, commits, and re-broadcasts authoritative state; the originator binds or rolls back.

A client therefore never needs to "own" anything; every replicated value it holds is either host-confirmed (shadow)
or a tracked prediction. The **host's own player** needs no intents at all: host actions mutate the authoritative world
directly (vanilla tools, UI and mods run unmodified) and are captured by §3.3 exactly like simulation changes.

### 4.2 Capture points

| Stage | System and position | Sees | Produces |
|---|---|---|---|
| **A** frame start | first step of `ClientFrameStartSystem` (after `EndFrameBarrier` playback, before host deltas are applied) | effects of last frame's UI handlers, which become visible through `EndFrameBarrier` (R10 §1): `Event+Modify`, `Event+Unlock` of dev-tree nodes, `Event+ColorUpdated`, `Deleted` on first-class entities, `Updated`/`BatchesUpdated` with changed replicated data; immediate untagged UI writes found by a version check over **watched types** (`ServiceFee`, `ServiceBudgetData`, `VehicleModel`, `ServiceDistrict`, `Building.m_Flags`, `CustomMeshColor`, `MeshColorPalette`, `Loan`, `Native` on map tiles, `Followed`) and reverted host-only types (`PlayerMoney`, `DevTreePoints`, `XP`); managed state (`TaxSystem.GetTaxRates()`, `CityConfigurationSystem.cityName`) diffed against the shadow | semantic intents |
| **B** apply frame | `ToolApplyCaptureSystem`, `UpdateBefore<…>(SystemUpdatePhase.ApplyTool)` (the phase runs only on apply frames, R10 §7.2 IP2) | the validated pre-apply `Temp` set: `Temp.m_Original` + flags, Σ`Temp.m_Cost` excluding `Cancel` computed exactly as `ToolApplySystem` (`ToolApplySystem.cs:59-66`), active tool id/prefab/mode, `Temp+Brush` stamps | `ToolHeader` (attached to stage C), `TerrainStroke` intents |
| **C** before Modification1 | `ClientChangeCaptureSystem`, `UpdateBefore<…>(SystemUpdatePhase.Modification1)` (R10 §7.2 IP3) | results of the local apply (`Applied+Created` new entities, `Applied+Updated` originals, `Deleted` originals, `SubElementDeleteSystem` effects) and of mod tools writing directly in ToolUpdate/PostTool (Move It, Tree Controller brush, Better Bulldozer, Zoning Toolkit, Water Features water tool, Anarchy components tool, `SelectionToolSystem` service districts, R10 §7.7); keyed derived entities with `Applied`/`Updated` (zone blocks after zoning) | opens a `ChangeSet` (entity list, deletions, provisional ids for predicted creations) |
| **C2** end of MainLoop | `ClientChangeFinalizeSystem`, `UpdateBefore<…, SaveGameSystem>(MainLoop)` | the same entities after Modification and mod post-apply processing (e.g. Anarchy `AnarchyPlopSystem` adds `TransformRecord`/`PreventOverride`/`Elevation` after the apply, before ModificationEnd, R09 §2.1; Plop the Growables tags) | re-reads S-types and archetypes, finalizes and sends the `ChangeSet` |
| **E** binding interposition | `BindingInterposer` (§10.4) | time controls, save/quicksave/load/continue/exit menu actions | `SpeedRequest`; "save a copy of the host city"; leave-session dialog |

Candidate entities are those tagged at the capture point plus watched-type chunks whose change version moved since the
applier's last write. Every candidate is diffed against the **client shadow** (last host-confirmed canonical value);
only differences count. Rules: `Temp`, local markers and derived non-keyed entities are ignored; differences in V-class
or host-only types are never proposals and are reverted to the shadow value; differences in S-class types of
first-class entities are, by construction, local edits (simulation is off, fighting systems are OFF or masked —
verified by the write auditor, §7.6). Because the shadow is updated by the applier *before* host-triggered local
writes happen (e.g. names applied through `SetCustomName`), the applier's own effects never look like edits.

### 4.3 Intent catalogue

| Intent | Payload | Captured at | Host executor (phase) | Prediction |
|---|---|---|---|---|
| `ChangeSet` | `seq`, `ToolHeader {toolId, prefabIdx, mode, costSum}`, base revisions, creates `(provisionalId, archId, canonical data, enabled bits)`, updates `(netId, typeId, data)`, deletes `(netId)`, keyed sets/deletes | C + C2 | `HostFrameStartSystem` applier at MainLoop start (§4.5) | yes |
| `TerrainStroke` | per apply frame: `(TerraformingType, Bounds2, Brush, brushPrefabIdx, clampedDelta)` list; ore/oil/fertility/groundwater/material stamps | B | private `TerrainSystem.ApplyToTerrain` with the pinned delta via reflection (fallback public `ApplyBrush`); cell-map brushes and `TerrainMaterialSystem.ApplyBrush` | yes (local `ApplyBrushesSystem` ran) |
| `SetPolicy` | `(targetNetId, policyPrefabIdx, active, adjustment)` from `Event+Modify` (the only producer is `PoliciesUISystem.ModifyPolicy`, R10 §3.1) | A | `PoliciesUISystem.SetPolicy(target, …)` in `HostUIIntentSystem` (UIUpdate, open `EndFrameBarrier` window) | yes (client `ModifiedSystem` processes the event; its trigger output is guarded by `TriggerSystem.Enabled`) |
| `SetTaxRates` | changed `(index, value)` pairs of the 92-int array | A (managed diff) | `TaxSystem` setters after `Readers.Complete()` (clamps re-applied, R10 §3.3) | yes |
| `SetServiceBudget`, `SetServiceFee` | `(servicePrefabIdx, %)`, `(PlayerResource, fee)` | A | `CityServiceBudgetSystem.SetServiceBudget`, `ServiceFeeSystem.SetFee` | yes |
| `ChangeLoan` | target amount (from the `Loan` diff after the client's own `LoanSystem` applied the queued action) | A | `LoanSystem.ChangeLoan` (re-clamped on host money) | yes; money reverted |
| `PurchaseDevNode` | `nodePrefabIdx` (from `Event+Unlock` on a `DevTreeNodeData` prefab + `DevTreePoints` drop) | A | `DevTreeSystem.Purchase(entity)` (validates points, lock state, requirements, R14 §3.1) | yes (bit flipped locally, points reverted) |
| `PurchaseMapTiles` | tile NetIds + expected cost (tiles that lost `Native`) | A | host re-implementation: cost check as `UpdateStatus`, `PlayerMoney.Subtract`, `MapTilePurchaseSystem.UnlockTile` per tile (`PurchaseSelection` needs the host's selection tool, R10 §3.4) | yes; money reverted |
| `Delete` | `netId` (district, upgrade, line via UI) | A | `Deleted` through `EndFrameBarrier` (UIUpdate), as vanilla | yes; respawn from shadow on reject |
| `Rename`, `SetCityName` | `(netId after Controller resolution, string)`, `string` | A | `NameSystem.SetCustomName`; `cityName` setter | yes |
| `SetRouteColor`, `SetMeshColor`, `SetHistorical`, `SetVehicleModels`, `SetServiceDistricts`, `EditPalette` | full new value (buffers sent whole to avoid order divergence, R10 §5) | A / C | direct writes + the vanilla tags (`Event+ColorUpdated`, `BatchesUpdated`, `Updated`) | yes |
| `Follow`/`Unfollow` | citizen NetId (`Followed` is shared, 50-cap) | A | `LifePathEventSystem.FollowCitizen/UnfollowCitizen` | yes |
| `SpeedRequest` | speed index or pause | E | session speed policy (host-only, vote, or anyone) | no (HUD shows "requested") |
| `Presence` | cursor, tool, prefab, preview outline (§4.7) | UI, 10 Hz | relayed | — |
| `ViewReport`, `Inspect` | §3.8, §3.10 | UI | interest/inspection | — |
| `Chat`, `PingMarker` | text; world position | UI | relayed, logged | — |

Mod settings buttons that mutate the whole world (Plop the Growables "all historical", Tree Controller "destroy
foliage", Better Bulldozer "safely remove", Water Features "cleanup cycle", R09 §2) are declared host-only in their
profiles: on clients the capture reverts their effects and shows "only the host can run this action".

### 4.4 Prediction policy

| Action class | Predict locally | Reason |
|---|---|---|
| Building tools, zoning, districts, routes, upgrades, bulldozing, Move It, Tree Controller, Line Tool, Anarchy | yes | vanilla semantics and instant feedback; tool state machines continue from real entities (`NetToolSystem.cs:6695-6721`, R10 §7.3) |
| Terraforming | yes (local stamps) + host tiles converge | latency of the brush matters |
| Policies, taxes, budget, fees, names, colours, vehicle models, districts | yes | sliders and toggles must respond instantly; host clamps rarely differ |
| Money, XP, dev points, milestones | **never** (host value written back every frame) | host-only state |
| Speed/pause | no | one clock |
| No-predict mode (viewer role, host "authoritative wait" setting, or a client whose hash is currently bad) | `NoPredictGateSystem` rewrites the active tool's `applyMode` from `Apply` to `Clear` through the protected setter (reflection) just before `ToolOutputSystem`; capture A/B moves into the gate | `Apply → None` is unsafe (R10 §7.4); further applies are blocked until the result arrives |

### 4.5 Host validation and execution

`HostIntentQueue` (Core) orders intents per client by `seq`; intents from peers that are not live are rejected.

**Validation of a `ChangeSet`** (`ChangeSetValidator`):
1. role and per-action permissions (§4.6);
2. every updated or deleted NetId exists and has the client's base revision (§2.9) — otherwise `Rejected(Conflict)`;
3. references resolve (existing NetIds or provisional ids of the same set);
4. only S-class "player-writable" types appear; V-class and host-only types are rejected;
5. created prefabs are unlocked (prefab `Locked` bit) unless unlock-all;
6. cost: `ToolHeader.costSum` must be ≥ the host's recomputation where possible (placeable object construction cost
   from prefab data; nets by composition and length) and within tolerance otherwise; `PlayerMoney ≥ cost` unless
   unlimited money (R10 §8.1 open question: trust within tolerance in v1, tighten later);
7. created objects lie inside owned map tiles (host area query);
8. mod-parity rules from profiles (e.g. Anarchy allows otherwise invalid placements; host settings decide, R09 §2.1).

**Execution** (`HostFrameStartSystem`, MainLoop start — the same position as the client applier, before the host
player's `ToolSystem`): the shared applier creates entities from the archetype, writes data, adds
`Created`/`Updated`/`Applied`, and touches the closure; then it subtracts the cost from `PlayerMoney` (as
`ToolApplySystem` does), adds `Recent {frame, cost}` where `Apply*` would (R02 §4.2), queues the `TriggerAction`s Apply
would have produced (`ObjectCreated`) through `TriggerSystem.CreateActionBuffer()` when the trigger system is enabled
(pattern `ModifiedSystem.cs:412, 441-448`), and assigns NetIds. The host's Modification pipeline regenerates derived
data, and the same frame's capture broadcasts the authoritative result with the originator's `AckMap`. The host
player's own tool is never disturbed: the host never touches the Temp namespace; if the host's preview referenced an
original that a remote action deleted, `OriginalDeletedSystem` forces a rebuild and blocks that frame's apply
(`OriginalDeletedSystem.cs:31-49`, `ToolBaseSystem.cs:533-559`, R10 §7.3).

Semantic UI intents run in `HostUIIntentSystem` (UIUpdate) because their APIs create `EndFrameBarrier` command buffers,
which throw outside the barrier window (`SafeCommandBufferSystem.cs:15-22`, R10 §0.4); explicit-target overloads are
used so the host player's selection is never changed.

### 4.6 Results, binding, rollback, conflicts, permissions

- **Ack**: the transaction that carries the committed state starts, for the originator, with
  `AckMap {clientSeq, status ∈ Accepted | Rejected(reason) | Partial, provisional → NetId[]}`.
- **Accepted**: the client binds each predicted entity to its NetId (side table), so the following `Spawn` records
  for those NetIds are applied as updates (data overwritten only where different; archetype fixed if different).
- **Rejected or timed out (10 s)**: the rollback plan recorded at capture runs — predicted creations get `Deleted`,
  modified entities get their shadow values back (+ `Updated`), deleted originals are respawned from their shadow data
  through the normal spawn path (they still exist on the host with the same NetId), managed state and predicted
  unlocks are reverted (relock trick, §3.6.5), and a toast names the reason ("conflicts with Anna's edit",
  "not enough money", "no permission").
- **Conflicts**: first committed wins (revision check). The host player's edits bump revisions like any other.
- **Roles and permissions** (host-managed, part of session settings): `Host`, `CoHost`, `Builder`, `Viewer`; per-flag
  overrides for build, demolish, terraform, zoning, economy (taxes/budget/loans), policies, progression purchases,
  map tiles, speed control, mod tools, chat.

### 4.7 Latency hiding and remote presence

| Technique | Where |
|---|---|
| Local prediction of tools and UI actions | §4.4 |
| Local tool previews and validation against the replicated world (`ValidationSystem` validates only local Temps, R11 §6.5) | vanilla |
| Smooth clock: PLL over host clock samples, `frameTime` from the fractional target | §5.1 |
| Built-in 32-tick interpolation buffer + staging ring + extrapolation for agents | §3.9.3 |
| Inspection fast path: panel opens on local data, refreshed when the closure arrives | §3.10 |
| Remote presence: each client sends `Presence` (10 Hz, unreliable, ≤ 200 B): cursor world position, active tool id, prefab, preview outline (≤ 8 Bézier curves for nets, ≤ 32 object footprints as position + radius, brush circle), player colour; others draw it with `OverlayRenderSystem.GetBuffer()` (`DrawCurve`/`DrawCircle`/`DrawLine`) in `RemoteGhostOverlaySystem`, never as ECS `Temp` entities (the shared Temp namespace would interfere with the local tool, R02 §10.2) | §3.6.1 row 9 |
| Pending indicator: count of unconfirmed intents in the HUD; predicted entities flash once on reject | HUD |

---

## 5. Follower simulation on clients

### 5.1 Clock: `SessionClockSystem`

Registered `UpdateBefore<SessionClockSystem, SimulationSystem>(SystemUpdatePhase.LateUpdate)` on every peer.

**During the load** the vanilla `SimulationSystem` runs untouched (loading progress, `m_IsLoading`), with
`SharedSettings.instance.gameplay.pausedAfterLoading = true` forced in memory so the first post-load frame runs no
local ticks (`SimulationSystem.cs:202-214`, R12 §5.3).

**Follower mode** starts on the second frame after `OnGameLoadingComplete(LoadGame, Game)` (when `m_IsLoading` is
false): `SessionClockSystem` sets `SimulationSystem.Enabled = false` (nothing in the game reads that flag, spot check)
and replaces its body each frame (R11 §7.1 recipe, patch-free because `UpdateSystem.Update(phase, index, iteration)` is
public):

1. `target = PLL.hostFrame(now) − D` from clock samples (every transaction header plus a 10 Hz unreliable
   `ClockSample {hostFrame, frameTime, smoothSpeed, sessionSpeed, hostTimeMs}`).
2. `ticks = clamp(floor(target) − frameIndex, 0, 16)`; if behind by more than 512 ticks, hard-jump `frameIndex`
   (cosmetic KEEP systems tolerate skipped ticks; `TimeSystem` recomputes from `frameIndex`) and log it.
3. Write `selectedSpeed` (session speed), `smoothSpeed` (host effective speed) and `frameTime` (fraction of the
   target) — the latter two through private-setter delegates built once by reflection.
4. `Update(PreSimulation)`; for each tick: `frameIndex++` (setter delegate), `Update(GameSimulation, frameIndex, i)`
   when `ToolSystem.actionMode.IsGame()`; then `Update(PostSimulation)` — the order of `SimulationSystem.cs:272-295`.
   Only the KEEP set acts (§5.2); `PostSimulation` is `WaterSystem`'s ECS part.
5. No pathfinding back-pressure exists on clients once the GameSimulation queue producers are off (R11 §7.2); the
   replacement ignores `pendingSimulationFrame`.

Local pause writers (`TimeUISystem` focus/barrier pause, `ErrorDialogManager`, lightning, photo mode, R05 §9.2) are
overwritten by the clock system and by a tiny `SpeedReassertSystem` registered
`UpdateBefore<SpeedReassertSystem, RenderingSystem>(MainLoop)`, because `RenderingSystem` freezes interpolation when it
sees speed 0 (`RenderingSystem.cs:186-189`).

**Host (authority mode)**: `SimulationSystem` stays enabled; `SessionClockSystem` re-asserts the session speed every
frame (so a host's pause menu, error dialog or alt-tab does not pause everyone, R10 §3.7 T2), and publishes clock
samples. The session speed changes only through the interposed time-control bindings (§10.4) or approved requests.

### 5.2 Gate table (clients)

Mechanism: `GateSystem` resolves every entry by type name at startup (game and mod assemblies) and sets
`Enabled = false` already in the `OnGamePreload` of the session load (its handler runs after the vanilla handlers
because mods subscribe later), so nothing gameplay-relevant touches the freshly loaded host state — e.g. no
`GroundHeightSystem` re-snap from local CPU heights and no `UnlockSystem` requirement pass. It re-asserts in
`OnGameLoaded` (after `CityConfigurationSystem` re-enables `UnlockAllSystem`, `CityConfigurationSystem.cs:195-198`), in
`OnGameLoadingComplete`, and every frame (systems re-enable themselves in `OnGamePreload`/`OnGameLoadingComplete`,
R11 §8.3). `SimulationSystem` is excluded from this (it must run the loading logic, §5.1). Entries are a declared table so a renamed or missing system fails
loudly; entries marked *critical* must resolve or the client refuses to join (fail closed). The GameSimulation row is
**phase-based**: `GateSystem` enumerates `(phase, system)` pairs from `UpdateSystem`'s private `m_Systems` list
(`Game/Game/UpdateSystem.cs:15-48, 80`) by reflection and disables every GameSimulation system — vanilla or mod — that
is not on the KEEP whitelist, logging unknown mod systems; if the reflection fails, it falls back to the declared
vanilla list plus mod profiles. `Enabled = false` is preferred to skipping `OnUpdate`, because `OnStopRunning`
disposes queue lists (`TriggerSystem.cs:395-402`, `IconCommandSystem.cs:716-724`).

| Phase | OFF on clients | OFF + host output fed |
|---|---|---|
| GameSimulation (`SystemOrder.cs:307-602`) | **everything except** the KEEP set: `TimeSystem`, `ClimateSystem`, `SnowSystem`, `WindSimulationSystem`, `WindSystem`, `ZoneAmbienceSystem` + `SpawnableAmbienceSystem`, `TrafficAmbienceSystem`, `WetnessSystem`, `DirtynessSystem`, `StreetLightSystem` (R11 §8.2) | demand, taxes, trade, statistics, cell maps, counts (G/B) |
| ToolUpdate | `UpgradeDeletedSystem` (would duplicate host-rebuilt sub-nets/areas, R13 §5) | — |
| Modification1 | `AddMeetingSystem` | — |
| Modification2 | `Game.Events.InitializeSystem`, `DamageSystem`, `DestroySystem`, `GroundHeightSystem` (+ drain, §5.3) | `OutsideConnectionInitializeSystem` |
| Modification3 | — | `DefaultPoliciesSystem` |
| Modification4 | `HouseholdInitializeSystem`, `ImpactSystem`, `IgniteSystem`, `FaceWeatherSystem`, `AddHealthProblemSystem`, `SpectateSystem`, `EndangerSystem`, `AddAccidentSiteSystem`, `AddCriminalSystem`, `SubmergeSystem`, `TripResetSystem` | `Game.Routes.InitializeSystem`, `EventJournalInitializeSystem` |
| Modification4B | `ObjectEmergeSystem`, `BuildingStateEfficiencySystem` (`Efficiency` replicated) | — |
| Modification5 | `ZoneBuiltRequirementSystem`, `CitizenInitializeSystem`, `HouseholdPetInitializeSystem`, `MeetingInitializeSystem`, `Game.Citizens.CompanyInitializeSystem`, `StorageInitializeSystem`, `GroupSystem`, `FixParkingLocationSystem`, `FixLaneObjectsSystem` | `ResourcesInitializeSystem`, `ParkInitializeSystem`, `EventJournalSystem` |
| ModificationEnd | `ZoneCheckSystem`, `ServiceRequestSystem`, `XPBuiltSystem`, `NetXPSystem`, `XPSystem`, `TriggerSystem`, `NotificationTriggerSystem`, `StrictObjectBuilt`/`ObjectBuilt`/`PrefabUnlockedRequirementSystem`, `DevTreeSystem`, `ParkingLaneDataSystem`, `PathOwnerTargetMovedSystem`, `BoardingVehicleSystem`, `CityServiceEfficiencySystem` | `MilestoneSystem` (UI fields), `LifePathEventSystem`, `CreateChirpSystem`, `RandomLocalizationInitializeSystem`, `CityServiceBudgetSystem` (arrays) |
| MainLoop | `UnlockSystem` (bits written by the applier) | — |
| Modification1 (config) | `UnlockAllSystem` (would add all milestone rewards locally, R14 §2.4) | — |
| LateUpdate | `AutoSaveSystem`, `DebugSystem` actions, `SimulationSystem` (replaced, §5.1) | — |
| Tutorials (Mod3/Mod4/Mod5/ModEnd) | `TutorialSystem`, `TutorialActivationSystem`, `TutorialTriggerSystem`, `TutorialDeactivationSystem` (session rule, §5.3) | — |
| Mods | per profile (§9.3) | per profile |

Producer/consumer pairs (R11 §4): `TriggerSystem` is OFF only together with every unguarded producer
(`CitizenInitializeSystem`, `AddHealthProblemSystem`, `Events.InitializeSystem`, `NotificationTriggerSystem`, all OFF);
`CityStatisticsSystem` with `AddHealthProblemSystem`; `XPSystem` with `XPBuiltSystem`/`NetXPSystem`/`XPAccumulationSystem`;
`PropertyProcessingSystem` with `CompanyInitializeSystem`. `IconCommandSystem` stays enabled (no `Enabled` guard on its
`TempJob` queue list, R11 §4 Q8). A startup self-check verifies the pairs in the table; diagnostics watch the sizes of
known queues by reflection.

### 5.3 Special replacements

| System | Treatment | Why |
|---|---|---|
| `SimulationSystem` | disabled after the first post-load frame; `SessionClockSystem` runs the phases | tick count must follow the host, not local `deltaTime` (R11 §7.1) |
| `GroundHeightSystem` | OFF; `GateSystem` clears its `m_ReadyUpdates` list by reflection each frame (the list otherwise grows because `TerrainSystem` keeps adding bounds, spot check) | its height re-snaps from local CPU heights race with host heights (R07 §5.3); host heights arrive as S data |
| `UnlockSystem` | OFF; applier writes `Locked` bits and creates `Event+Unlock` for the UI | clients must not run the requirement fixpoint on possibly different requirement buffers (R14 §2.4) |
| `MilestoneSystem` | OFF; cached UI fields written by reflection | rewards must not apply twice; UI fields must not freeze (R14 §1.4-§1.5) |
| `CityServiceBudgetSystem` | OFF; arrays written by reflection | not `ISerializable` (R11 §5.2) |
| `TriggerSystem` family | OFF (option A of R14 §5.5) | chirps, life path and radio come from the host; tutorials are off |
| Tutorials | off for every peer during a session (systems disabled, tutorial UI hidden by the HUD) | they emit `Unlock` events, may unlock map tiles, and the host's unfinished intro gates milestones (`MilestoneSystem.cs:73-76`, R14 §8); **[M]** verify the host gate is open with tutorials off, else the host is prompted to mark the intro as shown |
| `AutoSaveSystem` | OFF on clients; host autosaves stay on the `"SaveLoadGame"` queue and are announced | R12 §6 |
| `ParkedVehiclesSystem`, `UpdateGroupSystem`, `Vehicles/Creatures.InitializeSystem`, `RoadConnectionSystem` | KEEP | `Applied` rule (§3.6.3); only client-local navigation fields are touched (R11 §2.11); `m_RoadEdge` masked |
| `IconCommandSystem` | KEEP (mandatory) | replicated icons flow through it |
| `ModifiedSystem` (policies) | KEEP, input-starved (fed only by local predictions) | R11 §2.7 |

### 5.4 What clients still simulate

Time, date and climate curves; snow, wind, wetness, dirt and street-light brightness (cosmetic); ambience maps;
the GPU water simulation (visual, corrected); every Modification-phase derivation (net geometry, compositions, lanes,
blocks and cell flags, sub-objects, secondary objects, aggregates, flow graphs, references, search trees, districts,
road connections); pathfinding only for local previews (route and coverage previews, `uint.MaxValue` deadlines,
R11 §7.2); rendering, animation, effects, audio and radio; tool previews and validation; UI.

### 5.5 Double-application safeguards

| Hazard | Prevention |
|---|---|
| Milestone money and creditworthiness twice | `MilestoneSystem` OFF; state replicated; `MilestoneReachedEvent` synthesized for UI only |
| Dev points twice | `DevTreeSystem` OFF (would add points per synthesized event) |
| XP for replicated buildings and roads | `XPBuiltSystem`, `NetXPSystem`, `XPSystem`, `XPAccumulationSystem` OFF |
| Unlock-all rewards | `UnlockAllSystem` OFF |
| Default policies appended to replicated `Policy` buffers | `DefaultPoliciesSystem` OFF (R10 §4) |
| Citizens/pets/cars spawned for replicated households; birthday corruption; household buffer duplication | all agent initializers OFF (R11 §2.11) |
| Random events re-targeted, journal entries duplicated | `Events.InitializeSystem`, `EventJournalInitializeSystem` OFF |
| Re-rolled names and chirp texts | `RandomLocalizationInitializeSystem`, `OutsideConnectionInitializeSystem` OFF |
| Parked service vehicles spawned locally | `Applied` on creations with `OwnedVehicle` |
| Sub-nets/sub-areas regenerated after replicated upgrade deletion | `UpgradeDeletedSystem` OFF |
| Policy telemetry/triggers replayed | `Modify` events never replicated (state is) |
| Money charged twice for predicted tool use | client `PlayerMoney` is host-only and reverted at capture; the host charges once |
| Icons duplicated or fought | single source (replication buffer), idempotent `Add`, per-owner digest repair |

---

## 6. Join flow

### 6.1 Sequence

| # | Host | Client | Refs |
|---|---|---|---|
| 0 | "Host session": NetId pass (§2.4), lobby created, relay initialized | finds the session (friends list, lobby browser, invite, `+connect_lobby`, direct IP) | R06 §4.6-§4.7 |
| 1 | accepts the connection (SteamID in lobby, or password challenge for direct IP) | `Hello {protocol, mod build, game version, steamId}` | §8.4 |
| 2 | `HelloReply {compatibility manifest}` (§6.7) | `CompatibilityEvaluator` → `MismatchReport`; blocking issues stop here with explanations and fixes; otherwise `JoinRequest` | R12 §7 |
| 3 | queues a snapshot on `TaskManager` `"SaveLoadGame"` (shared with joiners arriving within 10 s); broadcasts `HostBusy(Snapshot)` | "Host is preparing the city" | R12 §4.3 |
| 4 | in one frame: `HostCaptureSystem` closes epoch E, then `SaveGameSystem.RunOnce()` writes to a temp file (stream + `Context(Purpose.SaveGame, Version.current, Hash128.Empty)`) | — | R12 §5.1-§5.2 |
| 5 | starts the joiner's cursor at E+1 in the shared `TransactionLog` | — | R12 §5.2 |
| 6 | `SnapshotManifest` (Control), 256 KiB chunks (Bulk) | writes chunks straight into the `SaveGameData` asset of a transient asset database; per-chunk and whole-blob hash checks | R06 §5.4 |
| 7 | — | pre-load preparation (§6.4) | R12 §4.2 |
| 8 | — | enqueues `GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, saveGameData)` on `"SaveLoadGame"` (public overload, no reflection) | R12 §4.1 |
| 9 | — | Deserialize phase: `ClearSystem` → `PreLoadSweepSystem` → `SerializerSystem` (incl. `NetIdSystem.Deserialize`) → resolvers → `PostDeserialize<NetIdSystem>` → `OnGameLoaded` (full derived rebuild) | R12 §9 |
| 10 | — | `OnGameLoadingComplete(LoadGame, Game)`: restore `pausedAfterLoading`, re-assert the gate table (first applied at `OnGamePreload`, §5.2), disable autosave, restore the client's own camera, dispose the transient DB, post-load validation (§6.4) | R12 §5.4 |
| 11 | sends join extras: `UnlockRequirementData` progress, session settings, roles, players, presence | applies the backlog (≤ 3 ms or 64 transactions per frame) under a "Catching up n %" overlay; clock follows the applied transactions' host frames | §6.5 |
| 12 | marks the client live when its acknowledged epoch ≥ head − 2; starts motion baselines and inspection | `Live`; tools and actions enabled | — |

### 6.2 Snapshot production (host)

- `SaveGameSystem` is driven directly (R12 §4.3), not `GameManager.Save`, which waits for a GPU frame, runs
  `CleanupMemory()`/`GC.Collect`, writes a package and changes the "Continue" pointer (`GameManager.cs:925-1002`).
- The snapshot is taken in the same frame right after the capture, so it holds exactly state(E). The only mutation in
  between is `TrimPathsSystem`, which is idempotent for the joiner (R12 §5.2). Mechanics: the `"SaveLoadGame"` queue
  task only raises a flag and awaits; in its next run `HostCaptureSystem` closes epoch E, sets
  `SaveGameSystem.stream/context` and calls `RunOnce()`, which merely enables the system and returns a task
  (`SaveGameSystem.cs:59-64`); `SaveGameSystem.OnUpdate` then runs the Serialize phase immediately after the capture in
  the same MainLoop (`SaveGameSystem.cs:66-84`), and the queue task completes when the write finishes.
- **Stall**: the Serialize phase is synchronous and does blocking GPU readbacks (water 64+16 MiB, terrain, snow;
  `WaterSystem.cs:1042-1063`). Expect 0.5–3 s on large cities **[M]**. Clients receive `HostBusy` first: their clocks
  simply wait, the HUD shows a banner, and timeouts are suspended.
- After the save, `TrimPathsSystem` and the prefab pass stamp change versions (R12 §6.1); Pass V's budget absorbs
  the false-positive burst.
- Package-bundled climate/water-render/terrain-render prefabs are not in a bare snapshot (R12 §4.3): the manifest
  carries their `PrefabID`s; v1 refuses the join with an explanation if a client cannot resolve them; v2 ships the
  prefab assets (reflection on internal `PrefabSystem.SavePrefabAssets` into a transient DB).
- `SaveGameSystem.referencedContent` provides content names for `ArePrerequisitesMet` (R12 §4.3).

### 6.3 Transfer

- `SnapshotManifest {snapshotId, totalBytes, chunkSize = 256 KiB, chunkCount, XxHash128, epoch E, frameIndex F0,
  stateDigest}`; saves are already ZSTD-compressed (`SerializerSystem.cs:102`), so no re-compression.
- `BlobChunk {snapshotId:u32, index:u32, len:u32, xxh64:u64, payload}` on the Bulk lane; sender window 2–4 MiB pending
  reliable (back-pressure from `m_cbPendingReliable`, R06 §5.4); Control traffic of live peers keeps priority.
- The receiver writes into a preallocated `FileStream` (no 300 MB managed buffers, GC concern R06 §1).
- Resume: a reconnect within 2 min sends a have-bitmap; only missing chunks are resent.
- Throughput **[M]**: 300 MB at 1–5 MB/s ≈ 1–5 min. v2 option "warm join": if the client holds a host-authored copy of
  the same city lineage (§6.8), send a block-hash delta instead of the full file.

### 6.4 Client load preparation and validation

| Setting / action | Value | Why |
|---|---|---|
| `CityConfigurationSystem.overrideLoadedOptions` | `false` | all city options come from the save (`CityConfigurationSystem.cs:423-432`) |
| `CityConfigurationSystem.overrideThemeName` | `null` | otherwise overrides the saved theme |
| `GameModeSystem.overrideMode` | host's `currentModeName` | `GameModeSystem` runs before `SerializerSystem` and would apply a stale mode, which rewrites prefab data (R12 §4.2) |
| `MapMetadataSystem.mapName` | host map name | UI, legacy water-render prefab name |
| `ApplyOptions` | **not called** | it persists options into the client's user state |
| `PlatformManager.instance.achievementsEnabled` | `false` | not the client's city |
| `SharedSettings.instance.gameplay.pausedAfterLoading` | `true` in memory, restored after | no local ticks on the first frame (R12 §5.3) |
| `PreLoadSweepSystem` (`UpdateBefore<…>(SystemUpdatePhase.Deserialize)`, after `ClearSystem`, before `SerializerSystem`) | armed for session loads | destroys mod-only persistent entities left from the client's previous city (R12 §3) |
| Mod session settings | host values written into the mods' `ModSetting` instances (memory only) | load-time mutators then compute host-identical prefab data (Historical Start, 529 Tiles milestone tiles, Tree Controller vegetation cost) |
| Mod load-time mutators that are not idempotent | Deserialize-phase systems disabled for this load; `OnGameLoaded`/`OnGameLoadingComplete` handlers unsubscribed (delegate to the private `GameSystemBase.GameLoaded`/`GameLoadingComplete` of that instance, built by reflection, removed from `LoadGameSystem.onOnSaveGameLoaded`/`GameManager.onGameLoadingComplete`) and re-subscribed when the session ends | lifecycle hooks ignore `Enabled` (spot check); list per profile (§9.3) |
| Client capture | disabled until live | load-time effects must not become proposals |

Post-load validation: count of unresolved prefab placeholders (instances whose prefab has a negative
`PrefabData.m_Index`, R12 §5.4); every NetId from the block resolves; the `stateDigest` from the manifest (per family
entity counts and per type digest totals at epoch E) matches the client's digests computed on worker threads from the
loaded world. Any failure → automatic second attempt, then abort with the diagnostics dump.

### 6.5 Catch-up

- Backlog transactions are applied in epoch order from the first frame after `OnGameLoadingComplete`, bounded per frame
  (§3.6.2 step 2). During catch-up the client's `frameIndex` is set to the host frame stamped in the last applied
  transaction (hard set via the clock's setter delegate); the PLL target takes over when live.
- Cap: if the backlog for a joiner exceeds 256 MB, or its apply rate stays below the production rate for 60 s, the
  host abandons it and starts a fresh snapshot.
- Intents from a joiner are rejected until it is live.

### 6.6 Multiple joiners

Joiners arriving within 10 s share one snapshot and backlog; each has its own cursor. Bulk bandwidth is split fairly;
live peers keep Control priority. Host memory for 7 clients [I]: shadow 30–100 MB + per-peer state 3–5 MB each +
`TransactionLog` ≤ 256 MB (shared ring) + snapshot file on disk.

### 6.7 Mismatch detection and explanation

| Manifest field | Source | Rule |
|---|---|---|
| Protocol version, Skyline Together build hash | constants | exact |
| Game version | `Game.Version.current` (`m_Version`, `m_VersionExtra`) + `fullVersion` | exact (`Version.Equals` compares only `m_Version`, R12 §7) |
| Format tags | `Enum.GetNames(typeof(FormatTags))` | exact |
| Serializable type set | every serializer in `SerializerSystem.componentLibrary` and `systemLibrary`: AQN + `StableTypeHash` + sizes | exact (catches mod layout changes; `StableTypeHash` ignores the assembly version, so mod versions are also compared) |
| Replication schema | type list with codecs and classes | exact |
| World-affecting code mods (profiled or unknown) | `ModManager.ListModsEnabled()` + `asset.version` + `platformID` | exact set and versions |
| Local-only mods (profiled as such, §9.3) | same | may differ (warning only): in a follower model they never write replicated state, and if they did, client capture would turn it into a proposal or revert it |
| Content and DLC | host `referencedContent` names → client `ArePrerequisitesMet`; `PlatformManager.IsDlcOwned` | must pass |
| Prefab set | XxHash128 over sorted `PrefabID`s; list of duplicate and hashless ids | equal; on mismatch the set difference is computed and shown |
| Game mode, climate/water-render/terrain-render prefabs | `GameModeSystem.currentModeName`; three `PrefabID`s | must resolve |
| Sim-relevant mod settings | per profile | host values forced (not blocking) |
| Locale | active locale | info (names are localized per player) |

**UX**: the lobby list already greys out sessions whose `st.game`/`st.mods`/`st.content` hashes differ. The mismatch
dialog groups *Blocking* (game version, mods with versions "host 1.4.2 / you 1.4.0", missing assets with their PDX mod
where known, DLC), *Warnings* (local-only mods, locale) and offers fixes: "Subscribe and enable missing mods"
(`PdxSdkPlatform.SubscribeAll` + `EnableModInActivePlayset`, then restart, R06 §8), "Open mod page", "Copy report".

### 6.8 Rejoin, saves, session end, resume points

- **Short drop**: if the connection resumes (or the client reconnects within 2 min) and the log still holds its last
  acknowledged epoch, replication resumes from there; otherwise a fresh snapshot (R12 §8.1).
- **Host saves** (manual or autosave) are allowed, serialized on `"SaveLoadGame"`, announced as `HostBusy(Save)`; they
  contain the `NetIdSystem` block, so a resumed session keeps NetIds (R12 §6.1).
- **Client saves**: the interposed save/quicksave bindings (§10.4) offer "Save a copy of the host's city": the host
  sends a fresh host-authored snapshot + `SaveInfo`, and the client builds a package with public APIs
  (`AddAsset<SaveGameData>`, `AddAsset<SaveGameMetadata>`, R12 §6.2).
- **Resume points** (optional): the host pushes its latest session save to clients every N minutes (Bulk, lowest
  priority). If the host leaves, any client can host from it and the others rejoin. Live host migration is not
  feasible (GPU state, pathfinding queues, wall-clock RNG exist only on the host, R12 §8.2).
- **Session end**: host exits or loads another city → `SessionEnd` → clients return to the menu (or follow the host
  into the new session if it re-hosts). Teardown hooks `OnGamePreload(Cleanup, MainMenu)`, not `IMod.OnDispose`
  (R05 §1.4).

---

## 7. Divergence detection and repair

### 7.1 Expected sources of divergence

Missed gate entries or unprofiled mod systems writing replicated types; family mis-classification; derived
regeneration that is not peer-stable (node lanes, aggregate merges, sub-object seed reuse, R13 §13); unresolved keyed
state; application bugs; different element order in reverse buffers (R04 §5 caveat); GPU-side differences (terrain
cascade, water); mod version or setting drift; crashes mid-transaction.

### 7.2 Digests

| Digest | Unit | Computation | Coverage |
|---|---|---|---|
| Entity data | `(typeId, bucket = netId & 4095)` | XOR of `xxHash64(netId ‖ canonical bytes)`; masked fields excluded; set-semantics buffers sorted first | host: incremental from the shadow; client: live re-hash of requested buckets |
| Existence | `(family, bucket)` | XOR of `xxHash64(netId ‖ archId)` | missing/extra entities and archetype drift |
| Derived sets | owner NetId | multiset hash of `(prefabIdx, qpos)` of derived children per family (sub-objects without snow placeholders, secondary objects, zone blocks; lane counts per owner) | rotating owners, prioritized near players |
| Keyed state | `(family, owner bucket)` | hash of K payloads (zone cells, trees, lights) | rotating |
| Global | per system state / global record | hash of the canonical payload | every 5–30 s |
| Prefab state | unlocked bitset | `xxHash64` | 30 s |
| Fields | terrain/splatmap tile, cell map | `xxHash64` per 64×64 tile / per map | after strokes + rotating; cell maps on update |
| Water | per 256² tile | not strict: mean absolute depth error from a coarse readback | above threshold → priority tile |

### 7.3 Anti-entropy loop

The host sends `HashPage {epoch, typeId, bucketStart, count, digests[]}` messages at low priority, rotating so S-class
types are fully covered every 64 s and V-class types every 256 s. The client stages the requested buckets right after
applying transaction `epoch` (so both sides hash the same epoch), hashes them on worker threads (≤ 0.5 ms main thread
per frame), and answers `HashReport {mismatches}`. V-type pages skip buckets that contain entities still dirty for that
peer (their values are legitimately pending); staleness promotion (§3.8) makes every bucket clean eventually. Buckets
containing entities of the client's unacknowledged predictions are reported as "pending" instead of compared, so a
repair never overwrites an in-flight proposal.

### 7.4 Repair ladder

1. **Data mismatch** `(type, bucket)` → the host resends canonical values of that type for all entities in the bucket
   (level 0).
2. **Existence mismatch** → the host sends the bucket's `(netId, archId)` list; the client spawns what is missing
   (host sends spawns) and deletes local entities with NetIds the host does not have.
3. **Derived-set mismatch** → the client touches the owner (`Updated` → regeneration) and the host resends K state;
   after 3 failures the owner is logged as a residual (cosmetic families) or its references are re-resolved
   (lanes used by parked cars).
4. **Global or field mismatch** → resend the system state, tiles or map.
5. **Full resync** (§7.5) when: > 1 % of buckets mismatch in one pass; the same bucket mismatches 3 times after repair;
   an exception occurs in the applier; > 1000 references stay unresolved for 30 s; a transaction gap is detected; the
   client falls more than 2 min behind; S traffic alone exceeds the link for 10 s.

### 7.5 Full resync and fail-safe

- **Client**: freeze the applier and capture, show "Resynchronizing…", request a snapshot, and reuse the join pipeline
  (§6). The world stays visible (frozen) meanwhile.
- **Host**: an exception in capture moves the session to safe mode — deltas stop, the session pauses, clients are
  told to wait; after recovery all clients resync; repeated failure ends the session gracefully with a diagnostics dump.
- Every system body runs inside `SafeRun` (exception → counted, logged with system, phase and record context, routed to
  `SafeModeController`). The game would catch per-system exceptions anyway (`UpdateSystem.cs:201-214`), but the session
  must know.

### 7.6 Development and diagnostics tools

- **Write auditor** (client, diagnostics mode): after ModificationEnd, compares chunk change versions of replicated
  types with the versions recorded right after the applier's writes; any newer stamp from another system reveals a
  "fighter" (archetype + type logged) to add to the gate table or a profile.
- **Discovery mode** (client): logs save-query entities created locally outside the applier and outside predictions —
  locally derived families missing from the classifier (§2.3).
- **Replica diff**: on demand, host and client dump canonical hashes per entity for one owner subtree or bucket; a diff
  tool shows the differing types and bytes.
- **Diagnostics dump** (JSON file + HUD panel): session, peers, schema, gate resolution, profiles, per-class records/s,
  per-lane bytes/s, apply and scan times, staleness percentiles, pending references, unresolved prefabs, mismatch
  history, last 200 errors.

---

## 8. Transport

### 8.1 Stack

```
ITransport / ITransportConnection (Core)      Send(lane, reliability, bytes) · Poll() · Stats · Close(reason)
 ├─ SteamP2PTransport   (Game)  CreateListenSocketP2P / ConnectP2P (virtual port 0), one poll group, SDR relay + ICE
 ├─ SteamIpTransport    (Game)  CreateListenSocketIP / ConnectByIPAddress (UDP, default port 27920): LAN, port forward
 ├─ RawUdpTransport     (Core, M10, optional) System.Net.Sockets + own reliability/fragmentation; LAN only, no Steam needed
 ├─ LoopbackTransport   (Core tests) and SteamSocketPairTransport (CreateSocketPair, in-process game tests, R06 §5.1)
 └─ SimulatedNetwork    (Core tests) loss, latency, jitter, reordering, duplication, bandwidth cap
```

Steam P2P is primary: authenticated, encrypted, NAT traversal and relay without port forwarding (R06 §5.2). Direct IP
uses the same Steam sockets API over UDP. A non-Steam store (Game Pass) has no Steam relay (R06 §9); the raw UDP
transport covers LAN only in v1.

### 8.2 Lanes and message mapping

| Lane | Priority/weight | Reliable traffic | Unreliable traffic |
|---|---|---|---|
| 0 Control | 0 / 1 | handshake, transactions (S/V/G/K/O), intents and acks, chat, hash pages, session control (`ReliableNoNagle`, batched per frame) | motion, clock samples, presence, view reports (`UnreliableNoDelay`, each ≤ 1200 B to avoid fragmentation loss) |
| 1 Bulk | 1 / 1 | snapshot chunks, terrain/splatmap/water tiles, cell maps, snow/wind | — |

Lanes need custom P/Invoke because the Steamworks.NET binding is broken for more than one lane (R06 §5.6): get the
interface with `SteamAPI_SteamNetworkingSockets_SteamAPI_v012()`, call
`SteamAPI_ISteamNetworkingSockets_ConfigureConnectionLanes(self, conn, 2, int[] priorities, ushort[] weights)`, and send
per lane with `SteamAPI_ISteamNetworkingSockets_SendMessages(self, n, IntPtr[] msgs, long[] results)` using messages from
`SteamNetworkingUtils.AllocateMessage` with `m_idxLane` set (the wrapper's `SendMessages` marshals structs by value).
A startup self-test (loopback socket pair) validates the imports; on failure the Bulk lane becomes a **second
connection** on virtual port 1, which works with the stock `SendMessageToConnection` (R06 §5.3 B).

### 8.3 Blob transfer

§6.3 (snapshot) and the same chunk protocol for large fields (cell-map deltas, terrain/water tile batches): manifest,
indexed chunks with per-chunk XxHash64, back-pressure window, receiver-side reassembly into pooled buffers or files,
have-bitmap resume.

### 8.4 Security

- Steam sockets authenticate peers with Steam certificates and encrypt (AES-GCM) by default; the `Unencrypted` config
  value is never set (R06 §5.2). `m_identityPeer` (SteamID) is the player identity.
- The host accepts SteamIDs that are lobby members (or hold an invite token); direct IP uses a session password with an
  HMAC-SHA256 challenge (the password never crosses the wire), because IP connections may be unauthenticated
  (`IP_AllowWithoutAuth`).
- Every decoder is bounds-checked with per-kind size limits; a malformed message disconnects that peer and logs; it
  never throws into the game.
- The host validates every intent (§4.5); clients trust only the host (friends co-op threat model: no anti-cheat).
- Raw UDP (LAN): HMAC on every packet with a key derived from the password and session nonces; not encrypted
  (documented).

### 8.5 NAT, discovery and invites

- `SteamNetworkingUtils.InitRelayNetworkAccess()` in `OnLoad`; relay status shown in the HUD (`GetRelayNetworkStatus`).
- P2P by SteamID uses relay or ICE automatically; read the effective `P2P_Transport_ICE_Enable` at runtime (R06 §5.2).
- Lobbies: `CreateLobby(FriendsOnly | Private | Public, 8)` with data `st.proto`, `st.game`, `st.mods`, `st.content`,
  `st.ping` (ping location string), `st.city`, `st.players`; lists filtered on `st.proto`.
- Invites: `ActivateGameOverlayInviteDialog(lobby)`; `GameLobbyJoinRequested_t` and `GameRichPresenceJoinRequested_t`
  handled; rich presence `connect` = `+connect_lobby <id>` and `steam_player_group` (the game owns `steam_display`,
  R06 §4.8); cold start: command line parsed in `OnLoad`, join started when the main menu is ready **[M]** (the
  Paradox Launcher may drop the argument).
- Direct IP: the host panel shows LAN address and port; the join panel takes `host:port` + password.

### 8.6 Threading and pooling

- A background **network thread** (≈ 1 ms wait) polls `ReceiveMessagesOnPollGroup`, parses into pooled buffers,
  queues per connection (`ConcurrentQueue`), drains the outbound queues, and pumps the Bulk window.
- Steam callbacks (connection status, lobby) arrive on the main thread through the game's `RunCallbacks` pump
  (R06 §2.3) and are posted to the session state machine; all `Callback<T>`/`CallResult<T>` are held in fields and
  disposed at session end; the game's Steamworks.NET assembly is referenced, never shipped (R06 §3.2).
- Message fields are read at fixed offsets with unsafe code (no `FromIntPtr` allocation); no LINQ or boxing on hot
  paths; byte arrays from `ArrayPool` (incremental GC with 3 ms slices, R06 §1).
- ECS is touched only on the main thread inside the mod's systems (ECS thread checks are compiled out, R06 §7).

### 8.7 Quality of service

`GetConnectionRealTimeStatus` (with a dummy lane-status argument, R06 §5.6) gives ping, quality, in/out bytes per
second, send rate, pending reliable/unreliable bytes and queue time. They feed the budget (§3.8), the HUD and the
diagnostics. `TimeoutConnected` is raised to 30 s while the host announces `HostBusy`.

---

## 9. Mod compatibility

### 9.1 Generic mechanisms (cover any mod)

| # | Mechanism | Covers |
|---|---|---|
| G1 | Save-query scope (§2.3): every mod `ISerializable`/`IEmptySerializable` component and every mod-only persistent entity is replicated; identity never depends on `PrefabRef` | Water Features sources, Better Bulldozer records, Anarchy/Tree Controller/Plop/Zoning Toolkit components (R09 §5.1) |
| G2 | Raw codec with entity-offset remap (or `GameSerializable` when the audit fails); KeyRefs for references to derived entities | entity fields in Better Bulldozer and Traffic components (R09 P11) |
| G3 | Enabled bits, archetype changes and in-place `PrefabRef` swaps as first-class records | Tree Controller (`Decoration`, `LumberResource`, `Tree` add/remove, prefab swaps) |
| G4 | Client effect capture (stages A/C/C2) turns direct writes into `ChangeSet` proposals | Move It, Tree Controller brush, Better Bulldozer tools, Zoning Toolkit, Water Features tool, Anarchy components tool, Line Tool, Plop (R09 §5.10) |
| G5 | Phase-based gate: every GameSimulation system of every mod is OFF on clients unless whitelisted (§5.2); Modification/Rendering enforcers listed per profile | Water Features simulation, Tree Controller seasonal/growth systems, Plop's spawned tagging |
| G6 | Join neutralization: per-profile Deserialize systems disabled and lifecycle handlers unsubscribed for the join load (§6.4) | 529 Tiles, Historical Start, Plop, Anarchy, Better Bulldozer, Tree Controller load hooks (R09 §5.7) |
| G7 | Session settings bridge: profile lists `ModSetting` properties that affect simulation or validation; host values written in memory on clients, restored on leave | all world-affecting mods (R09 §5.8) |
| G8 | Local-artefact filters: `PrefabRef(Entity.Null)`, profile-listed local types; tag noise (`Highlighted`, camera-driven `Updated`) never becomes data because capture diffs values | Move It control points, Anarchy `PreventCullingSystem` |
| G9 | Co-replication groups (§3.2) | Anarchy, Plop, Zoning Toolkit |
| G10 | Compatibility manifest: world-affecting and unknown mods must match exactly (version + settings fingerprint); local-only mods may differ (§6.7) | all |
| G11 | Harmony audit (diagnostics): if another mod loaded 0Harmony, enumerate `Harmony.GetAllPatchedMethods()` by reflection and flag patches on `Game.Simulation`/`Game.Tools` apply systems (R09 §3 caveat) | unknown mods |

### 9.2 Mod profile format (data in Core, resolved by reflection in the Game layer)

```csharp
sealed class ModProfile {
  string AssemblyName; VersionRange Versions; ModClass Class;           // WorldAffecting | LocalOnly | Content
  string[] ClientOffSystems;                                            // Enabled = false on clients
  string[] JoinDisableSystems; (string Type, Hook Hook)[] JoinUnsubscribe;
  string[] LocalTypes; string[][] CoReplicationGroups;
  (string SettingsType, string Property, SettingKind Kind)[] SessionSettings;
  (string SettingsType, string Property)[] HostOnlyActions;             // world-wide buttons
  string[] DerivedFamilyRules; string[] KeyedTypes; string Notes;
}
```

No compile-time reference to any mod; a profile whose types do not resolve is reported and the mod is treated as
"unknown" (must match exactly).

### 9.3 The user's mods

| Mod | Class | Generic coverage | Client OFF / join neutralization | Session settings / host-only actions | Notes |
|---|---|---|---|---|---|
| **Move It** | world-affecting tool, no schema impact | all edits are direct `ComponentLookup` writes (`QGeneric.cs:124-170`) → one `ChangeSet` per action (nodes, edges, objects, area nodes, elevations together) | none (`MIT_RemoveOverriddenSystem` only runs while the local tool is active; its effects are captured) | — | `MIT_*` types and `PrefabRef(Null)` control points are local; undo produces new writes, captured again (R09 §2.3); needed only by players who use it |
| **Better Bulldozer** | world-affecting | `PermanentlyRemovedSubElementPrefab` (buffer with `Entity`), `OwnerRecord` record entities (first-class, no geometry); tool deletions and the vehicle/cim/animal remover captured as proposals (agent deletion permission) | keep `RemoveRegeneratedSubelementPrefabsSystem`, `HandleDeleteInXFramesSystem` and `AutomaticallyRemove*` on clients (pure derivation from replicated records + forced settings, converges); join: unsubscribe its `OnGameLoadingComplete` orphan cleanup, disable `CleanUpOwnerRecordsSystem` | `AllowRemovingSubElementNetworks`, `AllowRemovingExtensions`, `AutomaticRemoval*`; host-only: "Safely remove" | same version on all peers (R09 §2.2) |
| **Line Tool** | world-affecting via the vanilla pipeline, no schema impact | results captured like any object placement; time-varying seeds irrelevant because results replicate (R09 §2.6) | none | — | needed only by players who use it |
| **Tree Controller** | world-affecting | `DeciduousData`, `Evergreen`, `LumberResource` (enableable), `NoTreeGrowth`; brush writes (state, prefab swap, `Tree` add/remove, `Decoration` bit) captured | `LumberAndPauseTreeGrowthSystem` (Rendering-phase writes), `DeciduousSystem`, `FindTreesAndBushesSystem`, `ReloadFoliageColorDataSystem` world part; join: disable its load conversion hook | `DisableTreeGrowth`, `UseDeadModelDuringWinter`, `FreeVegetation` (prefab cost must match host); host-only: "Destroy foliage", "Safely remove", pause/resume growth buttons | same version (R09 §2.8) |
| **Water Features** | world-affecting (simulation) | water sources without `PrefabRef` + 6 components (first-class); dummy sea source created by the host replicates; water tool edits captured; mod system state (`ChangeWaterSystemValues`, `TidesAndWavesSystem`, `SeasonalStreamsSystem`) arrives in the snapshot and its `Deserialize` copies host values into the mod settings (desired) | all its GameSimulation systems OFF (phase gate); global `WaterSystem` parameters replicated as G | evaporation, fluidness, damping, sim speed, water damage, tides/streams parameters; host-only: cleanup cycle | snapshot holds "rest" heights (`BeforeSerializeSystem`), live heights follow as V updates (R09 §2.9); same version |
| **Zoning Toolkit** | world-affecting | `ZoningInfo` on edges, co-replicated with edge creation; tool writes captured | keep `ZoningToolkitModSystem` (Mod4B block resizing derives from replicated `ZoningInfo`; new replicated roads arrive with it) | — | same version; `Highlighted` noise ignored (R09 §2.10) |
| **Plop the Growables** | world-affecting | `PloppedBuilding`, `SpawnedBuilding`, `LevelLocked` co-replicated with buildings; level-lock toggle captured | `PloppedBuildingSystem`, `SelectiveZoneCheckSystem` (vanilla `ZoneCheckSystem` already OFF); join: disable `ExistingBuildingSystem` | `SpawnedZoneDespawn`, `LockPloppedBuildings`; host-only: "All historical", "Remove all historical", "Remove all abandonment" | same version (R09 §2.7) |
| **529 Tiles** | world-affecting (load time + economy patches) | tile ownership (`Native`) replicated; its Harmony cost/upkeep patches only matter on the host (money is host-only) | join: run `PostDeserialize` with host settings but one-shot actions (`RelockAllTiles`, `NoStartingTiles`) forced off, so milestone tile data matches the host; tile `Native` verified after load | `UnlockAll`, `ExtraTilesAtStart/End`, `AssignToMilestones`, `UpkeepModifier` | client capture is disabled during load so local tile changes never become purchases (R09 §2.4) |
| **Historical Start** | world-affecting (prefab unlocks, requirement rewrites, XP rewards) | unlocks travel in the snapshot and the prefab-state channel | join: allowed to run with host settings (prefab data parity; its `Unlock` events are idempotent with the snapshot's bits) | `Unlock*` toggles | same version (R09 §2.5) |
| **Anarchy** | world-affecting | `PreventOverride`, `TransformRecord`, `DoNotForceUpdate`; `Transform` + `TransformRecord` co-replicated; components tool and info-panel toggles captured; post-apply processing included via stage C2 | `PreventOverrideSystem`, `RemoveOverridenSystem`, `SetRetainingWallSegmentElevationSystem`, `ResetTransformSystem` (host values arrive atomically); join: unsubscribe `AnarchyPlopSystem.OnGameLoadingComplete` | `AllowPlacingMultipleUniqueBuildings`, `MinimumClearanceBelowElevatedNetworks` (validation must match the host) | on the host, `AnarchyPlopSystem` may also post-process a remote commit when the host player has the same tool/prefab/toggle active (risk §12, R09 P3); same version |
| **Traffic** | world-affecting (replaces `LaneSystem`) | its serializable buffers/entities replicate with remap; lane references through KeyRefs (PLK); its tools follow a Temp pipeline → captured at C/C2 | keep `TrafficLaneSystem` and its data-sync system (derivation, identical inputs) — **[M]** verify against its DLL (no local source; R09 §3, R13 §2.5) | its tool state is not needed (results replicate) | same version required; PLK exactness must be re-verified |
| **Road Speed Adjuster** | unknown → world-affecting | generic G1–G4 | — | — | decompile its DLL in milestone M8 to build a profile; must match exactly until then (R09 §3) |
| Find It | local-only | placement goes through vanilla tools | — | — | may differ between peers |
| Extended Tooltip, Employment Tracker, Cim Census | local-only (read-only panels) | read replicated data; their numbers converge within the V staleness target (inspected entities are exact) | — | — | may differ |
| I18N Everywhere | local-only (localization) | names are synced as indices, localized per player | — | — | may differ |
| Unified Icon Library, Asset Icon Library | local-only (UI assets) | — | — | — | may differ unless they register prefabs (prefab fingerprint decides) |
| Toggleable Overlays | local-only (rendering) | tag noise ignored | — | — | may differ |
| Skyve | external tool | useful for playset parity before joining | — | — | — |
| China Pack (assets) | content | prefab fingerprint must match; missing assets listed with their PDX ids | — | — | auto-fix: subscribe |

### 9.4 Unknown mods

Default class "must match exactly". If an unknown mod has no serializable types and adds no prefabs, it is downgraded
to "warning only" (it cannot change replicated state on the host's schema; on a client, any write to replicated state
is reverted or proposed). Discovery mode and the write auditor (§7.6) produce the data for new profiles.

---

## 10. UI and UX

### 10.1 Surfaces

| Surface | Content | Built with |
|---|---|---|
| Main menu entry | "Multiplayer" button → panel: *Host* (load a city, then host it), *Join* (friends' sessions, lobby browser with compatibility badges, direct `host:port` + password), player name and colour, relay status | `moduleRegistry.append("Menu", …)` |
| In-game HUD | player list (name, colour, role, ping, state: live/catching up/resyncing, current tool), sync pill (green *Synced*, yellow *Catching up*, orange *Repairing n*, red *Resyncing*; tooltip with staleness p95 and KB/s), session speed indicator ("paused by host", "speed 2 requested by Anna"), host controls (roles, permissions, kick, session settings, category locks, resume points) | `append("GameTopRight", …)` |
| Chat | toggle with a hotkey; message list with timestamps and player colours; system lines (joins, leaves, rejections) | HUD panel; a focused text field masks game hotkeys (R05 §7.4) |
| World overlays | remote cursors and preview outlines (`OverlayRenderSystem`), name tags positioned from a world-to-screen binding, ping markers | C# overlay + small JS layer |
| Full-screen states | "Downloading city 43 % (85/200 MB, 1:12 left)", "Loading", "Catching up 80 %", "Resynchronizing", "Host is saving / preparing" | HUD overlay |
| Toasts | joins/leaves, rejected actions with reason, desync repaired, snapshot progress | `NotificationSystem.Push/Pop` (works in menu and game, R05 §8.1) |
| Dialogs | mismatch report, leave/end session confirmation, kick reason | own queue over `appBindings.ShowConfirmationDialog` (single callback slot, R05 §8.2) |
| Pause menu | "Leave session" / "End session" | `moduleRegistry.extend(<pause menu module>, …)` once the module path is found with `-uiDeveloperMode` **[M]**; HUD button as fallback |
| Options | player name, colour, default port, upload cap, motion quality, chat options, key bindings (chat, player list, ping), diagnostics toggles (verbose log, write auditor, discovery mode), "Copy diagnostics" | `ModSetting` + `AutomaticSettings` (`SkylineTogetherSettings`, unique class name, R05 §5) |

### 10.2 Hand-written `.mjs` without a bundler — feasible

- The loader only needs the header comment (spot check of `UIModuleAsset.cs:44-107`) and an ES module with a default
  `register(moduleRegistry)` export (R05 §7.3).
- Community bundles do not ship React or the game APIs: webpack resolves `react`, `react-dom`, `cs2/api`,
  `cs2/bindings`, `cs2/l10n`, `cs2/ui`, `cs2/input`, `cs2/utils` and `cohtml/cohtml` from `window`
  (`externalsType: "window"`, spot check). A hand-written module can read the same globals.
- Write components with `React.createElement` (no JSX), inline style objects (or one injected `<style>` element), and
  only the documented binding helpers (`bindValue`, `useValue`, `trigger`, `call`, R05 §7.3). Avoid undocumented
  `cs2/ui` component internals in v1.
- Strings are localized in C# (`LocalizationManager.AddSource` with a `MemorySource`) and delivered through bindings,
  so the JS needs no l10n dependency.
- Hot reload during development: local modules are watched (R05 §7.3).

```js
/*
 * Cities: Skylines II UI Module
 *
 * Id: SkylineTogether
 * Author: Skyline Together
 * Version: 0.1.0
 * Dependencies:
 */
const React = window.React;
const api = window["cs2/api"];
const h = React.createElement;
const session$ = api.bindValue("skylineTogether", "session", { state: "idle" });
const players$ = api.bindValue("skylineTogether", "players", []);

function SyncPill() {
  const s = api.useValue(session$);
  const color = { live: "#3c3", catchingUp: "#cc3", repairing: "#e93", resyncing: "#e33" }[s.sync] || "#888";
  return h("div", { style: { background: color, borderRadius: "8rem", padding: "2rem 8rem" } }, s.syncLabel);
}
function CoopHud() {
  const players = api.useValue(players$);
  return h("div", { style: { pointerEvents: "auto" } }, h(SyncPill),
    players.map(p => h("div", { key: p.id, style: { color: p.color } }, p.name + " · " + p.ping + " ms")));
}
function MenuEntry() {
  return h("button", { onClick: () => api.trigger("skylineTogether", "openMultiplayer") }, "Multiplayer");
}
export default function register(moduleRegistry) {
  moduleRegistry.append("Menu", MenuEntry);
  moduleRegistry.append("GameTopRight", CoopHud);
}
```

### 10.3 C# bindings (`SessionUISystem : UISystemBase`, UIUpdate, group `skylineTogether`)

- Values (pushed on change): `session` (state, role, host, sync state, staleness, speeds, pending intents), `players`
  (`RawValueBinding`, patchable), `chat` (`RawValueBinding` with patches), `netStats`, `lobbies`, `joinProgress`,
  `mismatch`, `sessionSettings`, `nameTags` (screen positions, updated per frame while observed).
- Triggers: `openMultiplayer`, `host`, `stopHosting`, `join(lobbyId)`, `joinDirect(address, password)`, `leave`,
  `sendChat(text)`, `setRole(playerId, role)`, `kick(playerId, reason)`, `setPermission(playerId, flag, value)`,
  `setSessionSetting(key, value)`, `requestSpeed(index)`, `refreshLobbies`, `fixMods`, `copyDiagnostics`, `resync`,
  `ping(x, y, z)`.
- `UISystemBase` gating caveat: the first main menu after boot gets no `OnGamePreload`, so the system checks
  `GameManager.instance.gameMode` itself (R05 §7.2).

### 10.4 Interposed vanilla controls (`BindingInterposer`)

During a session the interposer finds vanilla trigger bindings by path in
`GameManager.instance.userInterface.bindings`, removes them (`RemoveBinding` detaches the cohtml handler) and adds a
proxy binding with the same group and name (a typed `TriggerBinding<…>` where the argument types are public and simple,
otherwise a `RawTriggerBinding` that consumes the JSON arguments); at session end the originals are restored. The
vanilla JS keeps calling the same path; only the handler changes.

| Path | On clients | On the host |
|---|---|---|
| `time.setSimulationPaused`, `time.setSimulationSpeed` | `SpeedRequest` to the host | sets the session speed (re-asserted every frame, §5.1) |
| `menu.saveGame`, `menu.quicksave` | "Save a copy of the host's city" (§6.8) | original handler (invoked through the kept binding object) + `HostBusy(Save)` broadcast |
| `menu.loadGame`, `menu.quickload`, `menu.continueGame`, `menu.newGame`, `menu.startEditor` | dialog "Leave the session first?"; on confirm the session ends, the originals are restored and the player repeats the action | dialog "End the session for everyone?", same flow |
| `menu.exitToMainMenu` | dialog, then leave and `GameManager.instance.MainMenu()` | dialog, `SessionEnd` broadcast, then `MainMenu()` |

### 10.5 Session states shown to the player

`Idle` → `Hosting(n)` | `Connecting` → `Handshake` → (`Mismatch(report)`) → `Downloading(%)` → `Loading` →
`CatchingUp(%)` → `Live` ⇄ `Repairing(k)` → (`Resyncing`) ; side states `HostBusy(reason)`,
`Disconnected(reason, reconnecting n s)`, `SessionEnded(reason)`.

---

## 11. Code structure and milestones

### 11.1 Assemblies and build

```
SkylineTogether/
  src/SkylineTogether.Core/      netstandard2.1 (the game ships the netstandard 2.1 facade and System.Memory, R06 §1,
                                 so Span/ArrayPool need no extra DLL), C# 9, AllowUnsafeBlocks; no Unity/game references;
                                 only external dependency: System.IO.Hashing (the game's own copy at runtime)
  src/SkylineTogether.Game/      net472-style, references the game's Managed folder only (mscorlib/netstandard from the
                                 game, Game.dll, Colossal.*.dll, Unity.Entities/Collections/Mathematics.dll,
                                 UnityEngine.*.dll, com.rlabrecque.steamworks.net.dll and System.IO.Hashing.dll with
                                 Private=false); AssemblyVersion pinned to 1.0.0.0 (NetIdSystem AQN, R12 §1.5)
  src/SkylineTogether.UI/        SkylineTogether.mjs (hand-written), images
  tests/SkylineTogether.Core.Tests/   net8.0 xUnit, references Core (netstandard2.1) and System.IO.Hashing from NuGet
  tools/ReplicaDiff/             offline diff of diagnostics/replica dumps
  Directory.Build.props          GameManagedPath, output to the user Mods folder (%CSII_USERDATAPATH%\Mods\SkylineTogether)
```

- No Entities source generators, no IL post-processing, no Burst, no custom `IJobChunk`, no `SystemAPI`: systems
  derive from `GameSystemBase`, use `GetEntityQuery`, `ToArchetypeChunkArray`, dynamic type handles and
  `EntityManager` (hard constraint 1).
- 0Harmony is **not** bundled in v1 (App. B lists optional patches and their justification).
- The Core DLL ships next to the mod DLL (non-game third-party DLLs are resolved from the mod folder, R05 §1.2); the
  `IMod` class is top-level, has no instance field initializers (instances are created with
  `GetUninitializedObject`, R05 §1.3), and finds its folder with `ModManager.TryGetExecutableAsset`.

### 11.2 Main types

**Core** (`SkylineTogether.Core.*`)

| Namespace | Types | Responsibility |
|---|---|---|
| `Protocol` | `PacketWriter`, `PacketReader`, `MessageKind`, `Txn`, `RecordOp`, `NetRef`, `ProtocolVersion` | wire encoding, varints, bounds-checked decoding, pooled buffers |
| `Transport` | `ITransport`, `ITransportConnection`, `Lane`, `LoopbackTransport`, `SimulatedNetworkTransport`, `RawUdpTransport`, `ConnectionStats` | transport abstraction and test transports |
| `Blob` | `BlobManifest`, `BlobSender`, `BlobReceiver`, `ChunkBitmap` | chunked transfer, hashing, back-pressure, resume |
| `Session` | `HostSession`, `ClientSession`, `JoinStateMachine`, `PeerInfo`, `Roles`, `Permissions`, `SessionSettings`, `HandshakeManifest`, `CompatibilityEvaluator`, `MismatchReport`, `TransactionLog` | session lifecycle, handshake, join/catch-up/resync, backlog ring |
| `Identity` | `NetIdAllocator`, `EntitySideTable`, `ProvisionalIdMap`, `PrefabDictionary`, `ZoneTypeTranslator`, `RevisionTable`, `KeyCodec` (PLK, sub-object, block keys) | identity and dictionaries |
| `Schema` | `ReplicationSchema`, `TypeDescriptor` (stable hash, sizes, entity offsets, field masks, codec, class), `ArchetypeDescriptor`, `FamilyRules`, `CodecKind` | what is replicated and how |
| `Replication` | `CanonicalCodec`, `Shadow`, `DigestAccumulator`, `ChangeRecordBuilder`, `TransactionBuilder`, `TransactionApplierModel`, `PeerReplicationState`, `PriorityScheduler`, `BandwidthBudget`, `InterestGrid`, `WorkerPool` | capture-to-record pipeline independent of ECS |
| `Motion` | `MotionQuantizer` (Q-A/Q-B), `MotionPacket`, `StagingRing`, `SlotPlanner` (R15 §2.2 maths), `Extrapolator`, `ClockSync` (PLL, lag `D`) | agents and clock |
| `Consistency` | `HashPagePlanner`, `HashReportEvaluator`, `RepairPlanner`, `ResyncPolicy` | anti-entropy |
| `Intents` | `IntentKind`, `ChangeSetIntent`, `UiIntent` variants, `IntentTracker` (client predictions, rollback plans), `HostIntentQueue`, `ChangeSetValidatorModel` | client→host path |
| `Compat` | `ModProfile`, `ModClass`, `ProfileRegistry` (data) | mod rules |
| `Diagnostics` | `Counters`, `RingLog`, `DumpModel`, `StalenessHistogram` | observability |

**Game** (`SkylineTogether.Game.*`)

| Area | Types | Responsibility |
|---|---|---|
| `Mod` | `SkylineTogetherMod : IMod`, `SkylineTogetherSettings : ModSetting`, `Registration` | `OnLoad` registrations (App. A), settings, localization |
| `Systems.Common` | `SessionLifecycleSystem`, `SessionClockSystem`, `SpeedReassertSystem`, `GateSystem`, `NetIdSystem` (`IDefaultSerializable`), `PreLoadSweepSystem`, `ToolApplyCaptureSystem`, `DiagnosticsSystem` | lifecycle hooks, clock, gating, persistence, apply-frame capture |
| `Systems.Host` | `HostFrameStartSystem`, `HostUIIntentSystem`, `HostCaptureSystem`, `XpMessageTapSystem` (`UpdateAfter<…, XPSystem>(ModificationEnd)`) | commit proposals, run UI intents, capture |
| `Systems.Client` | `ClientFrameStartSystem`, `NoPredictGateSystem`, `ClientChangeCaptureSystem`, `ClientChangeFinalizeSystem`, `AggregateReconcileSystem`, `ClientUIApplySystem`, `MotionJitWriterSystem`, `RemoteGhostOverlaySystem` | apply, capture, motion, presence |
| `Ecs` | `ChunkReader` (RO dynamic handles, staging), `ChunkWriter` (RW writes, buffers, enabled bits), `StructuralBatch`, `ArchetypeCache`, `PrototypeCache`, `EntityRemapper` (NetRef ↔ Entity, KeyRef resolution), `FamilyClassifier`, `SchemaBuilder`, `ShadowFeeder` | ECS adapters; the only place that touches chunks |
| `State` | `SystemStateSync`, `NetContextFactory`, `NetWriter`/`NetReader`, `PrefabStateSync`, `IconSync`, `NameSync`, `MilestoneUiFeed`, `BudgetArraysSync`, `StatisticsSync`, `CellMapSync`, `TerrainSync`, `WaterSync`, `SnowWindSync`, `KeyedStateSync`, `ClimateSync` | global state and fields |
| `Snapshot` | `HostSnapshotService`, `ClientSnapshotLoader`, `LoadOverrides`, `PostLoadValidator`, `ResumePointService`, `SaveCopyService` | join and saves |
| `Intents` | `UiEffectCapture`, `ChangeSetCapture`, `ManagedStateDiff`, `HostIntentExecutor`, `ChangeSetValidator`, `RollbackExecutor` | effect capture, host execution |
| `Platform` | `SteamP2PTransport`, `SteamIpTransport`, `SteamNative` (lanes, `SendMessages`), `SteamLobbyService`, `RichPresenceService`, `CommandLineJoin`, `ModParityService` (PDX SDK) | platform services |
| `Compat` | `ProfileResolver`, `SessionSettingsBridge`, `LifecycleUnsubscriber`, built-in profiles (§9.3) | mod support |
| `UI` | `SessionUISystem`, `BindingInterposer`, `NotificationBridge`, `DialogQueue`, `LocaleSource` | UI backend |
| `Diagnostics` | `SafeRun`, `SafeModeController`, `ReflectionHandles` (validated at startup, App. B), `WriteAuditor`, `DiscoveryMode`, `DiagnosticsDumper`, `MeasurementProbe` | safety and observability |

### 11.3 Testing strategy

- **Core unit tests** (CI, no game): codec round trips (raw remap, buffers, masks, translators, KeyRefs), record and
  transaction encoding, decoder fuzzing, NetId and provisional mapping, a **ToyWorld** in-memory ECS model (archetypes,
  chunks with change/order versions, tags, entity offsets) to test capture (tag-driven, version-driven, discovery),
  apply, shadow and digests end-to-end; anti-entropy convergence with injected faults; scheduler staleness bounds and
  budget adherence; interest hysteresis; motion quantizer and slot planner against R15's formulas; clock PLL under
  jitter and speed changes; blob transfer with loss/reorder/resume; compatibility evaluator; intent tracker
  (predict/ack/reject/rollback); session state machines over `SimulatedNetworkTransport`.
- **Type check** of the Game layer against the real DLLs on every build.
- **In-game self-tests** (diagnostics menu, single machine): Steam socket-pair loopback incl. lanes; schema and
  eligibility audit; gate resolution; full-tag context creation and a `TaxSystem` deserialize round trip; snapshot
  production and transient-DB load; **record/replay harness**: a host session records snapshot S + transaction log L;
  later, in single player, "replay client mode" loads S and applies L through the real client applier, then compares
  digests with the host's — the client path is testable without a second machine (single-instance limit, R06 §1).
- **User test plan** per milestone (below), with the diagnostics dump attached to every report.

### 11.4 Milestones

| M | Scope | Exit criteria (in game, by the user) |
|---|---|---|
| M0 Foundation | Core libraries + tests; mod loads; settings; UI module (menu entry, HUD skeleton); Steam lobby/invite/connect; handshake with mismatch report; chat in menu; diagnostics dump | two PCs: create lobby, invite, connect, see mismatch report for a deliberately missing mod, chat |
| M1 Spectator join + measurement | host snapshot → transfer → client load (transient DB, overrides, NetId block); gate table; follower clock; `MeasurementProbe` (entity/chunk/archetype counts, scan costs, snapshot time and size) | client joins a 100k city, time and weather advance in step, no errors or growing queues for 30 min; measurement report sent back |
| M2 Structure | host capture Pass S/D, client applier (spawn/despawn/archetype/data/tags), families and keys, zoning overlay, aggregates reconcile, names, policies | host builds roads, buildings, zones, districts, lines; client sees identical results incl. derived geometry; digests clean |
| M3 Client actions | stages A/B/C/C2, intents, host validation/commit, ack/bind/rollback, revisions, permissions, no-predict gate, binding interposition (speed, save, leave) | client builds and edits with instant feedback; conflicts rejected cleanly; every UI action of R10 §3 works from a client |
| M4 City state | Pass V + scheduler + interest; system state sync; prefab state + synthesized events; icons; chirps, journal, statistics, XP toasts; inspection closures | economy panels, milestones, unlocks, notifications and info panels match the host; staleness within targets |
| M5 Agents | agent existence, motion stream, staging + JIT writer, clock lag, effects, selection | traffic and pedestrians move smoothly on clients at speeds 1/2/4; sirens and lights correct; vehicles clickable |
| M6 Environment | terrain strokes + tiles, splatmap, water tiles, cell maps, snow, wind, climate digest | terraforming from either side converges bit-exactly; infoviews match |
| M7 Convergence | hash pages, repair ladder, full resync, safe mode, write auditor, discovery mode | injected faults repair automatically; forced exceptions end in a resync, never a crash |
| M8 Mods | profile system, session settings bridge, join neutralization, built-in profiles (§9.3), Traffic and Road Speed Adjuster verification | each listed mod works from host and client |
| M9 Scale and polish | compression tuning, budgets, 300 MB saves, rejoin, resume points, remote presence, HUD polish, localization | 8 players, large city, 2-hour session without resync |
| M10 Alternative transports | direct IP polish, raw UDP LAN fallback | LAN play without Steam relay |

---

## 12. Risks and what to verify first

### 12.1 Risks

| # | Risk | Impact | Mitigation |
|---|---|---|---|
| 1 | Mono cost of managed chunk scanning and applying in 100k+ cities | host frame time | budgets and amortization (§3.3), worker threads for byte work, measurement in M1; interest radius and V staleness absorb overload |
| 2 | Family mis-classification (missing derived family → duplicates; wrong derived → missing entities) | visible duplicates/holes | save-query scope, SubObjectSystem's own criterion, existence digests, discovery mode (§7.6) |
| 3 | A client system still writes replicated state (fighter) | flicker, repair churn | gate table + phase gate, write auditor, digests; profiles for mods |
| 4 | Node-lane and other non-peer-stable derivations (R13 §2.4) | lane references fail, parked cars misplaced | KeyRefs with geometric fallback; per-owner repair; minimize lane references |
| 5 | Snapshot stall on the host (synchronous GPU readbacks) | everyone hitches 0.5–3 s per join/save | `HostBusy` messaging, shared snapshots for simultaneous joiners, resume points |
| 6 | Join time for 300 MB over relay | minutes of waiting | progress UI, resume, direct IP option, v2 warm join |
| 7 | Bandwidth at speed 4 with many clients | motion degradation | per-peer budgets, degradation ladder, upload cap setting |
| 8 | Reflection touch points break on a game patch (App. B) | feature loss | validated `ReflectionHandles` at startup; each has a fallback or disables its feature cleanly; reported in the handshake |
| 9 | Steam lanes P/Invoke breaks | snapshot blocks commands | startup self-test, dual-connection fallback |
| 10 | Host commits client-computed geometry | invalid placements accepted | revisions, prefab unlocks, cost and tile checks; friends-only threat model |
| 11 | Mod post-apply processors keyed on local tool context run on remote commits on the host (Anarchy) | small divergence from the acting player's intent | stage C2 includes the client's post-processing; host differences are re-broadcast; v2 adapter disables the processor during remote-commit frames |
| 12 | `UnlockSystem`-less unlock application or relock trick misbehaves | stale toolbar | verify early; fallback: keep `UnlockSystem` enabled and patch out its requirement loop (optional Harmony H2) |
| 13 | Tutorial gating of milestones on the host | milestones blocked | verify; host prompt to mark the intro as shown |
| 14 | Water is not bit-identical | visual differences between snapshots | gameplay host-only; tile corrections; documented exception |
| 15 | Surface wetness/dirt and snow-dependent placeholders not bit-identical | cosmetic | documented exception (§3.11.9) |
| 16 | UI module globals (`window["cs2/api"]`, `React`) change in a game update | HUD missing | feature-detect in JS, C#-side notifications still work |
| 17 | Cross-store (Game Pass) players | cannot join | out of scope v1; raw UDP LAN; EOS later (R06 §9) |
| 18 | Mono `Type.GetType` with a different AQN version for `NetIdSystem` | NetIds lost after a mod update | pinned `AssemblyVersion` + `FormerlySerializedAs` self-alias; test before release (R12 §10) |

### 12.2 Verify first (ordered experiments for the first in-game sessions)

1. **Measurement build (single player)**: entity, chunk and archetype counts per family, chunks stamped per tick per
   type, Mono cost of `GetChangeVersion` and staging per chunk, `SaveGameSystem.RunOnce` duration and size, mover
   counts — fixes the budgets in §3.3/§3.8.
2. **Snapshot round trip (single player)**: transient-DB load through the public `GameManager.Load` with the overrides
   of §6.4; `NetIdSystem` block survives; first post-load frame runs no ticks.
3. **Follower clock**: disable `SimulationSystem` after load and drive phases from `SessionClockSystem`; time, weather,
   rendering interpolation and audio behave; no errors.
4. **Gate table**: apply on a loaded city; watch logs and known queue sizes for 30 min; confirm `UnlockSystem`-less
   unlock synthesis refreshes the toolbar and the relock trick works.
5. **Applier on a single machine (record/replay)**: replay a recorded host log on the snapshot; digests match; derived
   geometry, zoning overlay and aggregates correct.
6. **Full-tag context**: build it by reflection; `TaxSystem`/demand `Deserialize` round trip without side effects.
7. **Steam**: socket pair with two lanes through the custom imports; relay connection between two PCs; throughput of
   the Bulk lane.
8. **Motion**: JIT slot writer with recorded frames; smooth at speeds 1/2/4 with simulated jitter; effects refresh.
9. **Capture stages**: vanilla tool apply, a policy toggle, a tax change, Move It and Tree Controller edits on a
   client produce the expected intents and nothing else (write auditor clean).
10. **Tutorial gate**: with tutorials disabled in session, `MilestoneSystem` is not blocked on the host.

---

## Appendix A — Registrations (`OnLoad`)

```csharp
// common
us.UpdateBefore<SessionClockSystem, SimulationSystem>(SystemUpdatePhase.LateUpdate);
us.UpdateBefore<SpeedReassertSystem, RenderingSystem>(SystemUpdatePhase.MainLoop);
us.UpdateAt<SessionUISystem>(SystemUpdatePhase.UIUpdate);
us.UpdateAt<NetIdSystem>(SystemUpdatePhase.MainLoop);
us.UpdateAfter<PostDeserialize<NetIdSystem>>(SystemUpdatePhase.Deserialize);
us.UpdateBefore<PreLoadSweepSystem>(SystemUpdatePhase.Deserialize);
us.UpdateAt<GateSystem>(SystemUpdatePhase.MainLoop);            // cheap per-frame re-check
us.UpdateBefore<ToolApplyCaptureSystem>(SystemUpdatePhase.ApplyTool); // host: terrain stamps; client: stage B
// host role (systems idle unless hosting)
us.UpdateAfter<HostFrameStartSystem, EndFrameBarrier>(SystemUpdatePhase.MainLoop);
us.UpdateAt<HostUIIntentSystem>(SystemUpdatePhase.UIUpdate);
us.UpdateBefore<HostCaptureSystem, SaveGameSystem>(SystemUpdatePhase.MainLoop);
us.UpdateAfter<XpMessageTapSystem, XPSystem>(SystemUpdatePhase.ModificationEnd);
// client role (systems idle unless joined)
us.UpdateAfter<ClientFrameStartSystem, EndFrameBarrier>(SystemUpdatePhase.MainLoop);
us.UpdateBefore<NoPredictGateSystem, ToolOutputSystem>(SystemUpdatePhase.ToolUpdate);
us.UpdateBefore<ClientChangeCaptureSystem>(SystemUpdatePhase.Modification1);
us.UpdateAfter<AggregateReconcileSystem, AggregateSystem>(SystemUpdatePhase.Modification2B);
us.UpdateAt<ClientUIApplySystem>(SystemUpdatePhase.UIUpdate);
us.UpdateBefore<MotionJitWriterSystem, PreCullingSystem>(SystemUpdatePhase.PreCulling);
us.UpdateBefore<RemoteGhostOverlaySystem, OverlayRenderSystem>(SystemUpdatePhase.Rendering);
us.UpdateBefore<ClientChangeFinalizeSystem, SaveGameSystem>(SystemUpdatePhase.MainLoop);
```

`HostFrameStartSystem` and `ClientFrameStartSystem` are both children of `EndFrameBarrier`; only one is active per role.
No mod system is registered in GameSimulation (avoids shifting auto-assigned update offsets, R05 §12).

## Appendix B — Internal access points

| # | Access | Kind | Used for | Fallback if it fails |
|---|---|---|---|---|
| 1 | `SimulationSystem.frameIndex/frameTime/smoothSpeed` private setters | reflection (setter delegates) | follower clock | client refuses to join (critical) |
| 2 | `ContextFormat.EnableTag` / `Context.m_Format` | reflection or `UnsafeUtility.As` | full-tag read context | system-state sync falls back to reflection on fields; affected systems listed |
| 3 | `ToolBaseSystem.applyMode` protected setter | reflection | no-predict gate | no-predict mode unavailable (prediction always on) |
| 4 | `GroundHeightSystem.m_ReadyUpdates` | reflection | drain while disabled | keep the system enabled; host heights overwrite (epsilon in digests) |
| 5 | `MilestoneSystem` cached fields | reflection | milestone UI on clients | milestone XP bar shows 0 (cosmetic) |
| 6 | `CityServiceBudgetSystem` arrays | reflection | budget panel on clients | keep the system enabled on replicated inputs (R11 §5.2) |
| 7 | `XPSystem.m_XPMessages` (read without dequeue) | reflection | XP toasts | toasts missing (cosmetic) |
| 8 | `TerrainSystem.ApplyToTerrain` | reflection | pinned-delta stamps on host | public `ApplyBrush` (tiles still converge) |
| 9 | `WaterSystem` active-cell reset | reflection | water tile writes | disable culling for a few frames via public flag |
| 10 | `UpdateSystem.m_Systems` | reflection | phase-based gate | declared lists + profiles |
| 11 | `GameSystemBase.GameLoaded/GameLoadingComplete` (private) | reflection (delegate construction) | unsubscribe mod load hooks on join | force host settings only; repair after load |
| 12 | `EventJournalSystem` callbacks | reflection if not public | journal UI on clients | journal opens with stale list until reload |
| 13 | Steam flat API: `SteamAPI_SteamNetworkingSockets_SteamAPI_v012`, `ConfigureConnectionLanes`, `SendMessages` | P/Invoke | lanes | second connection for Bulk |

**Optional Harmony patches** (not bundled in v1; enabled only if in-game testing shows the need, each justified):

| # | Patch | Justification |
|---|---|---|
| H1 | prefix on `IconCommandSystem.OnUpdate` (clients): drop vanilla non-`Temp`/`Marker`/`Transaction` commands | only if client-derived icon producers visibly flicker against replicated icons (R14 §4.5) |
| H2 | prefix on `UnlockSystem.OnUpdate` (clients): run only `ProcessEvents` | only if direct `Locked` writes + synthesized events fail to refresh some UI (R14 §2.4) |
| H3 | prefix on `GameManager.Save` (clients): block saves started by other mods | safety net beyond binding interposition (R12 §6.2) |

## Appendix C — Message catalog

| Kind | Lane / reliability | Direction | Content |
|---|---|---|---|
| `Hello`, `HelloReply`, `JoinRequest`, `JoinAccepted/Denied` | Control / reliable | both | handshake, manifest (§6.7) |
| `SnapshotManifest`, `BlobChunk`, `BlobHave` | Control + Bulk / reliable | host → client, client → host | snapshot transfer (§6.3) |
| `Txn` (batched, compressed) | Control / reliable | host → client | records (§3.7) |
| `Motion`, `MotionBaseline`, `PeerSlotMap` | Control / unreliable (map reliable) | host → client | §3.9 |
| `ClockSample` | Control / unreliable | host → client | §5.1 |
| `ViewReport`, `Presence` | Control / unreliable | client → host (presence relayed) | §3.8, §4.7 |
| `Intent` (ChangeSet, TerrainStroke, semantic) | Control / reliable | client → host | §4.3 |
| `HashPage`, `HashReport`, `RepairRequest` | Control / reliable | both | §7.3 |
| `FieldTiles` (terrain, splatmap, water), `CellMapDelta`, `SnowWindSnapshot` | Bulk / reliable | host → client | §3.11 |
| `SessionSettings`, `PlayerList`, `Roles`, `HostBusy`, `SessionEnd`, `Kick` | Control / reliable | host → client | session control |
| `Chat`, `PingMarker`, `SpeedRequest` | Control / reliable | both | social, speed |

## Appendix D — Default budgets and constants

| Item | Default |
|---|---|
| Host replication main-thread budget | 2.5 ms/frame (Pass V adaptive 0.25–2 ms) |
| Client apply budget during catch-up | 3 ms or 64 transactions per frame |
| Per-peer downstream cap | 600 KB/s relay, 2 MB/s direct/LAN; host total upload cap 2 MB/s |
| Class shares | S uncapped, M 50 %, V 30 %, B 15 %, G 5 % |
| Reliable backlog target | ≤ 128 KB pending on Control; Bulk window 2–4 MiB |
| Interest radius | `clamp(k·zoom, 300 m, 2500 m)`, leave at 1.25× |
| Motion | Q-B ≈ 10–11 B/frame; entry baseline 4 × Q-A |
| Clock lag `D` | `max(0, L − 16 + 4)` ticks, adapting ±1 tick/s |
| Digests | 4096 buckets per type; S full coverage 64 s, V 256 s |
| Pending reference TTL | 10 s |
| Keyed-state resolve timeout | 2 s |
| Intent ack timeout | 10 s |
| Join backlog cap | 256 MB or 10 min |
| Snapshot chunk | 256 KiB with XxHash64; blob XxHash128 |
| Short-drop resume window | 2 min |

