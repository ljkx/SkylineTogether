# Proposal B: Skyline Together, architecture for scale and efficiency

> **Historical design record.** Written before the implementation and not maintained; the code differs in many
> places. The current architecture is [docs/ARCHITECTURE.md](../ARCHITECTURE.md); see [README.md](README.md).

Status: architecture proposal (angle: SCALE AND EFFICIENCY), 2026-10-10.
Scope: complete design of a host-authoritative co-op mod for Cities: Skylines II (2-8 players, Steam relay and direct
connections, cities of 100k+ population, saves of 20-300 MB), optimised for modest relay bandwidth and modest CPU,
while still converging on complete sync of everything that matters.

Conventions

- `R01`..`R15` are the research reports in `docs/research/` (`R11 §6.2` = report 11, section 6.2). `D` is the digest.
- Code references are relative to the decompiled tree `mod_audit/decomp` (for example `Game/Game.Common/SystemOrder.cs:49`).
  Lines marked **[V]** were re-read for this proposal; other code facts are taken from the cited report.
- **[E]** marks an estimate that must be replaced by in-game measurements (see §13.3). **[I]** marks design inference.
- "Client" = any non-host peer. "Tick" = one `SimulationSystem.frameIndex` step (60 per second at speed 1, R03 §1.2).
- Every type and system name written in `PascalCase` without a namespace that is not a game type is a type proposed
  here (all of them live in the `SkylineTogether.*` namespaces, §12).

---

## 0. Executive summary

### 0.1 The problem in numbers

A 100k-population city holds roughly 25-60 MB of persistent per-entity simulation state, tens of MB of structure,
15-35k moving objects and about 100 MB of GPU fields (water 64 MiB raw, terrain 32 MiB raw) (R03 §6.12, R07 §3, §6.5,
R15 §9.2) [E]. A Steam relay link gives on the order of 0.1-1 MB/s per peer and its throughput is not guaranteed
(R06 §5.4, R08 §13). Mirroring raw state is out of the question (R08 §13 computes ~36 MB/s for naive per-tick deltas;
R15 §9.3 computes 0.9-3.5 MB/s just for quantised motion of all movers).

Deterministic lockstep, which would need only inputs, is infeasible (R03 §8): wall-clock seeded RNG, chunk-index job
RNG, 214 parallel queues, async pathfinding, frame-batched command playback and a GPU water simulation.

### 0.2 Thesis

**Send the minimum that cannot be recomputed, in an order that follows what players are looking at, with a hard
bandwidth budget, and prove convergence with hashes instead of with volume.**

Concretely:

1. **Causes where effects are deterministic, effects where they are not.** Player structure (nets, objects, areas,
   routes, zone paint) is sent as source components; every client regenerates geometry, lanes, zone blocks,
   sub-objects, secondary objects, compositions, flow-graph topology, district membership, road connections, search
   trees and render data with the vanilla Modification pipeline (R02 §6, R13 §12). Simulation outcomes (agents,
   economy, events, icons) are sent as results because the simulation is not reproducible.
2. **Bounded bandwidth, not bounded staleness of everything.** Each peer has a byte budget fed by Steam's measured send
   rate. All continuous streams share it through a priority scheduler. A bigger city never costs more bandwidth; it only
   makes *unobserved* state converge more slowly.
3. **Attention drives freshness.** Every client reports its view (camera, zoom, selected and followed entity, open
   panel, active infoview) at 10 Hz in ~40 bytes. The host promotes exactly the state that this view can show:
   moving objects near the camera, building state in view, the data behind an active infoview, and the closure of an
   inspected entity. Everything else converges through a fair rolling refresh with a target staleness of 30-120 s of
   wall-clock time.
4. **Latest value wins.** The host keeps one shadow copy of what it has captured and one dirty bit per (entity,
   component type) per peer. Values that were superseded before they could be sent are never sent. A slow or congested peer receives
   fewer, fresher updates instead of an ever-growing backlog.
5. **Cheap CPU without Burst.** Structure and existence changes are captured from the game's own one-frame tags
   (`Created`/`Updated`/`Deleted`/`Applied`, R02 §5), which name exactly the changed entities. Silent simulation writes
   are found by a time-sliced chunk scanner that filters by chunk change version and compares with native `memcmp`
   (R08 §12). Cadences are tiered, so total main-thread cost stays at ~1.5-3 ms per frame on the host [E].
6. **Convergence by incremental hashing.** The host maintains per-bucket additive hashes of everything it has sent,
   updated in O(1) per change. Clients verify received state and re-hash their live data in a rolling window. Mismatches
   are repaired at entity granularity. A full snapshot resync is the last resort, and a reconnecting client is repaired
   by hash comparison instead of by reloading.

### 0.3 Key decisions

| # | Decision | Where |
|---|---|---|
| K1 | Host-authoritative follower model. Clients run **no gameplay simulation**: every system registered in `GameSimulation`, `EditorSimulation` and `LoadSimulation` is disabled except a cosmetic whitelist, which also disables the simulation systems of every mod generically. | §6, R03 §9, R11 §8 |
| K2 | Identity = dense host-assigned **NetSlot** (24-bit) + generation, held in side tables indexed by `Entity.Index`, persisted by a mod system `NetIdSystem : IDefaultSerializable` inside every save. Prefab ids and type ids on the wire are **ranks in sorted `PrefabID` / `StableTypeHash` sets**, so no tables are transmitted when the handshake digests match. | §3, R12 §1.4 |
| K3 | Two capture paths on the host: **EDC** (event-driven capture from game tags; immediate, transactional, used for structure and existence) and **SDC** (scan-driven capture; time-sliced chunk scanner for silent value writes). | §4.3 |
| K4 | Wire values are **raw component bytes under a per-type field policy**, entity fields remapped to compact NetRefs through `TypeManager.GetEntityOffsets`. The game's `ISerializable` is used only for managed system state. Packets are LZ4-compressed with the game's own `NativeCompression`. | §4.5 |
| K5 | Per-peer scheduler with four priorities: **P0** structure/events/control (immediate), **P1** attention state, **P2** motion (unreliable), **P3** rolling refresh; token bucket fed by Steam congestion data. | §4.7 |
| K6 | **Existence global, motion local** for vehicles (R15 §9); **interest-scoped existence** for pedestrians and animals; agents (citizens, households, companies) exist globally but their internal state is refreshed by attention and rolling refresh. | §2, §4.7 |
| K7 | Local derivation is maximised, including traffic-light visuals from the 6-byte node `TrafficLights` state through the public static helpers `TrafficLightSystem.UpdateLaneSignal` / `UpdateTrafficLightState` (`Game/Game.Simulation/TrafficLightSystem.cs:782, 878` [V]). | §6.3 |
| K8 | One client apply point right after `EndFrameBarrier` playback (R11 §6.2). All replication operations are **idempotent upserts keyed by NetSlot**, grouped in host **epochs**; structural transactions are atomic within one frame. | §4.6 |
| K9 | Client actions: tools predict locally and ship the resulting change-set (R10 §7); UI actions are captured by **event interception** and **authoritative-input diffing**; direct-write mod tools are captured generically by **local change capture**. No Harmony is required for v1. | §5 |
| K10 | Join = host save snapshot (`SaveGameSystem`, R12 §4) taken right after a capture boundary, **cached and shared by concurrent joiners**, plus a shared epoch ring for catch-up, plus a mandatory post-join hash sweep. Rejoin repairs by hash diff without reloading when the client world is intact. | §7 |
| K11 | Transport = Steam Networking Sockets P2P with three lanes (custom `ConfigureConnectionLanes` P/Invoke, R06 §5.6), direct IP through the same API, and an optional managed UDP transport later. Network I/O and encoding run off the main thread. | §9 |
| K12 | Code is split into a pure .NET **Core** (protocol, codecs, identity, shadows, scheduler, hashing, session state machines; unit-tested against a fake world and a network simulator), a thin **Game** layer (ECS adapters, systems, Steam, mod adapters) and a hand-written **UI** module (`.mjs`, no bundler). | §11, §12 |

### 0.4 Headline budget (100k city, per client) [E]

| Stream | Priority | Speed 1 | Speed 4 (default caps) | Notes |
|---|---|---|---|---|
| Motion frames (interest set ~2-3k movers, ~11 B/frame, far field half rate) | P2 | 70-125 KB/s | ≤150 KB/s (quality scaled) | R15 §9.3; scheduler lowers far-field rate and radius under budget |
| Vehicle existence (spawn/despawn/park swaps) | P0/P1 | 3-6 KB/s | 12-24 KB/s | R15 §9.3 gives 8-20 KB/s for all agents; creatures are interest-scoped here |
| Agent existence (households, citizens, companies, requests) | P0 | 1-5 KB/s | 4-20 KB/s | spawn bundles carry full state |
| Attention state (in-view buildings, inspected closure, infoview topic) | P1 | 5-20 KB/s | 10-40 KB/s | bounded by budget share |
| Rolling refresh (everything else) | P3 | 15-40 KB/s | 15-40 KB/s (wall-clock paced) | staleness grows instead of bandwidth |
| Global and system state, statistics, budget, demand | P1 | 1-3 KB/s | 2-6 KB/s | XOR-delta vs last sent |
| Icons, events, chirps, journal, unlocks | P0 | 0.5-2 KB/s | 1-6 KB/s | |
| Cell maps, water correction tiles | P3 | 5-25 KB/s | 5-25 KB/s | configurable |
| Structure (tool applies, zone spawns) | P0 | bursts (0.2-2 KB per edit) | bursts | large Move It operations: one burst of 10-500 KB |
| Hash beacons, view reports, clock, headers | P0 | 3-5 KB/s | 3-5 KB/s | |
| **Total** | | **~120-250 KB/s** | **≤300 KB/s default cap** | paused: 2-5 KB/s |

Host upload is the sum over clients (star topology): ~0.4-0.75 MB/s for three clients at speed 1. Join transfers the
compressed save (20-300 MB) on a bulk lane: 10 s to 2.5 min at 2-5 MB/s, up to 5 min at 1 MB/s (R06 §5.4).

Host main thread: ~1.5-3 ms/frame for capture; clients: 1-4 ms/frame apply budget plus ~1 ms motion writing [E].
Host memory: shadow store 30-80 MB, slot tables ~16 MB, epoch ring 128 MB (configurable) [E].

---

## 1. Scale model and design principles

### 1.1 What a large city contains and what this design actually sends

Estimates for a 100k city (R03 §6, R04 §4, R12 §1.2, R15 §9.2) [E]:

| Family | Count [E] | Raw state [E] | Changes | Sent by this design |
|---|---|---|---|---|
| Net nodes and edges (incl. building sub-nets) | 50-150k | ~10-20 MB | on edits | source components on edit (P0); nothing otherwise |
| Net lanes | 200-600k | tens of MB | on owner edit | **never** (regenerated; only lane *keys* when an agent references a lane, §3.7) |
| Prefab sub-objects, secondary objects | 100-400k | | on owner edit | **never** as entities (regenerated); tree growth of keyed sub-objects at low rate |
| Zone blocks and cells | 20-60k blocks | | painting, spawns | only `Cell.m_Zone` overlays keyed by block (RLE), on change |
| Buildings | 10-20k | 2-6 MB | condition/efficiency every 64-1024 ticks | in-view P1, otherwise rolling P3; structural changes P0 |
| Props, trees, decals (top level) | 50-300k | | on edits; tree growth | edits P0; growth rolling P3 |
| Citizens | 100-115k | 10-14 MB | each every 256 ticks | spawn/despawn P0; state by attention + rolling |
| Households | 40-45k | 5-6 MB | money every few ticks | same |
| Companies | 3-8k | 1-2 MB | every 1024 ticks | same |
| Moving vehicles / creatures | 5-15k / 5-20k | 2-5 MB | every 16 ticks | vehicles: existence global, motion interest; creatures: interest-scoped |
| Parked vehicles | 20-40k | | park/unpark | existence global (archetype swaps) |
| Service requests, events, icons, chirps | thousands | | event-driven | existence + state, P0/P3 |
| City singletons, system state | | 0.2-0.5 MB | seconds-minutes | change-driven XOR deltas |
| Cell maps | 15 maps, 64²-256² | 1-2 MB | 128-262144 ticks | on host update, XOR-delta + LZ4 |
| Terrain heightmap | 4096² R16 | 32 MiB | on terraform | dirty 64×64 tiles after a stroke settles |
| Water | 2048² RGBA32F | 64 MiB (+16 backdrop) | per rendered frame (GPU) | client simulates visually; quantised correction tiles |

The design sends **all authoritative information** eventually, but the volume that must move *continuously* is small:
structure moves only on edits, derived families never move, and simulation state moves in proportion to the budget.

### 1.2 Principles

(Design principles are numbered D1-D8 so they cannot be confused with the priorities P0-P3 of §4.7.)

- **D1 Derive before you send.** If a client can compute a value bit-identically from replicated inputs with vanilla
  code, the value is not replicated (§6.3). If the computation is only nearly deterministic (node lanes, aggregate merge
  survivors, GPU raster heights), the inputs are replicated and the outputs are checked by key or hash and corrected
  (§3.7, §8).
- **D2 Never send twice what a peer already has, and never send stale intermediates.** One shadow, per-peer dirty
  bitsets, full-value coalesced sends (§4.4).
- **D3 Bandwidth is a budget.** All continuous traffic goes through a per-peer weighted scheduler (§4.7). P0 traffic
  is never dropped, but it is accounted, so it temporarily displaces P1-P3.
- **D4 Freshness follows attention.** Spatial interest for motion and building state, semantic interest for infoviews
  and info panels (§4.7.4).
- **D5 Use the game's own signals and slicing.** The game's change tags give exact change sets for edits and spawns;
  the `UpdateFrame` slicing tells the motion sampler which 1/16 of movers changed on a tick (R15 §7.2).
- **D6 Idempotence over exactness of timing.** Every operation is an upsert keyed by NetSlot. A duplicated or
  reordered (within a lane) state record cannot corrupt a client, which makes join boundaries, reconnects and repairs
  simple (§4.6.3).
- **D7 Main thread only for ECS, everything else off it.** Capture copies bytes out; encoding, compression, hashing
  and socket I/O run on worker threads in Core; apply copies bytes in (§9.8).
- **D8 Fail safe.** Any exception in capture or apply marks the affected class "suspect", stops applying it, and
  schedules targeted repair; persistent failure escalates to a snapshot resync. Nothing throws into the game loop
  (`UpdateSystem` already isolates per-system exceptions, `Game/Game/UpdateSystem.cs:201-214`, R02 §1.2).


---

## 2. Authority model per state category

Legend for the capture column: **EDC** = event-driven capture from game tags (§4.3.1), **SDC** = scan-driven capture
(§4.3.2), **SYS** = system-state codec (§4.3.3), **SPEC** = special-purpose channel. Priorities P0-P3 are defined in
§4.7. "Class" names (S, V, A, M, G) are the replication classes of §4.2.

The single rule behind every row: **the host owns every value that the simulation or a player can change; a client
owns only its camera, UI, tool previews and per-player preferences.** Per-player state is never replicated; it only
influences the host's priorities through the view report.

### 2.1 Persistent structures (class S)

| Category | Authority | Minimal authoritative representation on the wire | Derived locally on every client | Capture → priority | Rate / size [E] |
|---|---|---|---|---|---|
| **Nets**: nodes, edges, building sub-nets | host | `PrefabRef` (prefab rank), `Node`, `Edge` (NetRefs), `Curve`, `Game.Net.Elevation`, `Upgraded`, `PseudoRandomSeed`, `BuildOrder`, `Road.m_Flags` (half-align bits are a source for zone blocks, R13 §4.1), `Owner`, `Fixed`, `Standalone`/`Orphan`/`LocalConnect`/`OutsideConnection` presence, `TrafficLights` configuration flags, `NetCustomColorVariation`, ordered `ConnectedNode` (R13 §2.4), mod components on edges (e.g. `ZoningInfo`, Traffic data). Plus the host's tag set as `TOUCH` records (the topological closure the host already tagged, R02 §7). | geometry (`EdgeGeometry`, `NodeGeometry`, `Composition`), `ConnectedEdge`, `SubNet`, lanes, zone blocks, secondary lanes and objects, flow-graph topology, search trees, `BorderDistrict`, labels, aggregates (reconciled, row "aggregates") | EDC transaction → P0 | 0.2-2 KB per tool apply |
| Net simulation values | host | `Road` flow words (16 floats), `NetCondition`, net `Pollution`, `LandValue`, `Density`, `ServiceCoverage` and `ResourceAvailability` buffers | infoview colours | SDC class V; topic boost (traffic, coverage, land value infoviews) → P1 in view / P3 | coverage buffers dominate; XOR-friendly |
| **Net lanes** | derived | nothing per lane. When replicated agent state references a lane, a portable lane key `PLK` is sent (§3.7, R13 §2.5). | everything (`LaneSystem` or the Traffic mod's replacement) | — | 0 |
| Lane simulation state | host (unused on clients) | not replicated, except signals (derived from node `TrafficLights`, row "traffic lights") | lane signals; parking occupancy (verify `ParkingLaneDataSystem` writes only occupancy, §13.2) | — | 0 |
| **Objects**: buildings, props, trees, decals, area-spawned objects, upgrades | host | `PrefabRef`, `Transform`, `Game.Objects.Elevation`, `Owner`, `Attached`, `PseudoRandomSeed`, `Building.m_OptionMask` and `m_Flags` (`m_RoadEdge`/`m_CurvePosition` are excluded by field mask, R13 §8), `Extension`, `ServiceUpgrade`, `CustomMeshColor` (+enabled bit), `Native`/`Overridden` presence, `Policy` + modifier buffers, `RandomLocalizationIndex`, `UnderConstruction` (incl. host `m_Speed`), `Tree` + `Decoration`/`LumberResource` enabled bits, mod components (`TransformRecord`, `PreventOverride`, `PloppedBuilding`, `DeciduousData`, ...) | prefab sub-objects, secondary objects, sub-lanes, `Building.m_RoadEdge` (`RoadConnectionSystem`), `CurrentDistrict`, `Renter`, `InstalledUpgrade`, `ConnectedBuilding`, `SpawnLocationElement`, flow-graph connections, all rendering data | EDC → P0 (spawn bundles for sim-spawned buildings: building + sub-nets + sub-areas in one transaction, R13 §5) | spawn bundle 0.3-2 KB, a few per minute |
| Building simulation state | host | `BuildingCondition`, `Abandoned`/`Condemned`/`Destroyed`/`Damaged`/`OnFire` presence and data, `Efficiency` buffer, service facility components, `ElectricityConsumer`, `WaterConsumer`, `GarbageProducer`, `CrimeProducer`, `MailProducer`, `Occupant`/`Patient` buffers, building `Resources`, `PropertyOnMarket` | infoview colours, effects | presence changes: EDC → P0; values: SDC class V → P1 in view, P3 otherwise | in view ~5-20 KB/s |
| Prefab sub-objects and secondary objects | derived | `Tree{m_State,m_Growth}` of tree sub-objects keyed by `(owner slot, prefab rank, aligned index or quantised local position)` (R13 §3.1); stop sub-objects referenced by `Connected` use the same key | all | SDC (keyed records) → P3 | small |
| **Areas**: lots, districts, surfaces, spaces, map tiles, extractor and storage areas | host | `PrefabRef`, `Area.m_Flags`, `Game.Areas.Node` buffer, `Owner`, `PseudoRandomSeed` (lots), `District.m_OptionMask`, district `Policy` + `DistrictModifier`, map-tile ownership (`Native` presence), `Extractor`/`Storage`/`WoodResource`/`MapFeatureElement` amounts (simulation values) | `Geometry`, `Triangle`, `Expand`, `SubArea`, labels, `CurrentDistrict`/`BorderDistrict` of other entities | EDC → P0; amounts SDC → P3 | small |
| **Zones** | host | per block: `Cell.m_Zone` array translated to zone prefab ranks, RLE-coded, keyed by block key `(owner edge slot, side, quantised m_Position, m_Size)` (R13 §4.3). `Overridden` cells omitted. | block geometry (`BlockSystem`), cell `m_State`/`m_Height`, `VacantLot`, overlap resolution (`CellCheckSystem`) | EDC (blocks tagged `Applied+Updated` or created) → P0, applied one frame after the edge transaction | tens of bytes per edit |
| **Routes** and transport lines | host | `Route`, `TransportLine` settings, `Game.Routes.Color`, `RouteNumber`, `VehicleModel` buffer, `Policy`, `RouteModifier`, `Waypoint{m_Index}` + `Position` + `Connected` + `Owner`, `Segment{m_Index}` + `Owner`, ordered `RouteWaypoint`/`RouteSegment` (R13 §9) | `AccessLane`, `RouteLane`, `PathTargets`, segment `PathElement`, `CurveElement`, `ConnectedRoute`, `SubRoute` | EDC → P0 | small |
| Route simulation values | host | `WaitingPassengers`, `BoardingVehicle`, vehicle timing | — | SDC class V; boosted while a line panel is open | small |
| **Aggregates** (street identity) | host | `Aggregate` + ordered `AggregateElement` (edge slots) + `RandomLocalizationIndex` | membership is regenerated by `AggregateSystem` and then reconciled to the host's list (§4.6.6, R13 §7.1) | EDC → P0 | small |

### 2.2 Simulation state

| Category | Authority | Minimal authoritative representation | Derived locally | Capture → priority | Rate / size [E] |
|---|---|---|---|---|---|
| **Citizens** | host | existence: spawn bundle with full state; state: `Citizen`, `HouseholdMember`, `Worker`, `Game.Citizens.Student`, `CurrentBuilding`, `CurrentTransport`, `TravelPurpose`, `HealthProblem`, `Criminal`, `CrimeVictim`, age tag, `CarKeeper` enabled bit, `Followed` | `HouseholdCitizen`, building `Student`, resident links, names (from `PseudoRandomSeed`/household index) | spawn/despawn EDC → P0; state SDC class A → P1 when inspected or in an active infoview topic, else rolling P3 | spawn ~70 B; rolling ~15 KB/s |
| **Households** | host | `Household`, `PropertyRenter` (relationship changes are promoted to P0 because they change building visuals and renter lists, §4.6.5), `Resources` buffer, `HouseholdNeed`, `TaxPayer`, `MovingAway`, `PropertySeeker` enabled bit, tourist/commuter/homeless markers, `HouseholdPet` | `Renter`, `HouseholdAnimal`, `OwnedVehicle` | as citizens | rolling ~15-30 KB/s |
| **Companies** | host | `CompanyData` (brand as prefab rank), `PropertyRenter`, `WorkProvider`, `Employee` buffer, `Resources`, `TradeCost`, `Profitability`, `CompanyStatisticData`, `ServiceAvailable`, storage/transport components, `CurrentTrading` | `Renter` on the building | as citizens; profitability topic boost | rolling ~5-10 KB/s |
| Seekers, meetings, partner search | host | the explicit save-query entities (`JobSeeker`, `SchoolSeeker`, `LookingForPartner`, `CoordinatedMeeting`, R04 §2.1) | — | EDC existence, SDC P3 | small |
| **Services** | host | facility state on buildings (row above), `ServiceRequest` entities (existence + `Dispatched`), `ServiceDispatch` buffers on vehicles (on demand), service coverage (net row) | — | requests: EDC → P3; dispatch buffers: inspection closures | ~1-3 KB/s |
| **Economy**: money, loans, creditworthiness | host | City entity `PlayerMoney`, `Loan`, `Creditworthiness`, `Population`, `Tourism`, `DangerLevel`, `TradeCost`, `SpecializationBonus` | `CitySystem` caches (keep it running, R14 §1.2) | SDC class G hot list (scanned every frame), change-driven, ≤4 Hz → P0 | <0.2 KB/s |
| **Budget** | player-authored inputs, host outputs | input: `ServiceBudgetData` buffer (intent path). Outputs: `CityServiceBudgetSystem` income (14 ints), expenses (15 ints), totals and per-service maps (`Game/Game.Simulation/CityServiceBudgetSystem.cs:759-806` [V]); collected budget data on service prefabs (prefab-state channel) | `GameModeGovernmentSubsidiesSystem` (keeps running, R11 §2.9) | SYS (reflection codec, the system is not `ISerializable`, R11 §5.2) → P1, ≤1 Hz on change | ~0.1-0.3 KB per change |
| **Taxes and fees** | player-authored, host-validated | `TaxSystem.m_TaxRates` (92 ints) through `TaxSystem.Serialize`; City `ServiceFee` buffer | — | SYS / SDC G, on change | tiny |
| **Statistics** | host | `CityStatistic` buffers on statistic entities with an **append codec** (only new samples), `CityStatisticsSystem` sample count and frame | statistics UI | SDC G (append) + SYS, every 8192 ticks | ~1-5 KB per sample period |
| **Demand, counts, production, trade** | host | `Residential/Commercial/IndustrialDemandSystem`, `Count*Systems`, `TradeSystem`, production and utility statistics systems through their own `Serialize` with an owned full-tag `Context` (R11 §5.3) | demand bars, infoview panels | SYS, XOR-delta vs last sent: demand ≤1 Hz, others ≤0.25 Hz | 1-3 KB/s |
| **Progression**: XP, milestones, dev tree, unlocks | host | `XP` (4 fields), `MilestoneLevel`, `DevTreePoints`, `Creditworthiness`; prefab state: unlocked bitset over prefab ranks, `PlacedSignatureBuildingData`, `UnlockRequirementData.m_Progress`; `MilestoneSystem` cached XP fields; XP messages as events | UI refresh is driven by **client-synthesised** `Event+Unlock` and `Event+MilestoneReachedEvent` entities (R14 §1.5, §2.4); map-tile permits (pure function of milestone `Locked` bits, R14 §1.3) | SDC G + SPEC prefab-state channel → P0 | tiny |
| **Time and date** | host | clock message: `frameIndex`, speed, sub-tick phase (§6.2); `TimeData` singleton on change | date, time of day, lighting, sun (`TimeSystem`, `PlanetarySystem`, `LightingSystem`) | SPEC clock (unreliable 10-30 Hz) | ~0.3 KB/s |
| **Weather and climate** | host | climate prefab (in the snapshot), weather state and `OverridableProperty` overrides (custom codec of `ClimateSystem` fields, audited for side effects), weather phenomena as event entities | climate curves sampled locally (`ClimateSystem.cs:607-623`), wind and snow visuals, rain rendering | SYS ≤0.2 Hz + on change; hash-verified | tiny |
| **Events**: fires, crime, accidents, disasters, floods | host | event entities (`Event` + type + `Duration` + `TargetElement`), target components (`OnFire`, `AccidentSite`, `Flooded`, `InDanger`, `Destroyed`), journal entities (`EventJournalEntry` + buffers) | event journal list is rebuilt by a client companion (R14 §6.1) | EDC → P0; journal values SDC P3 | event-driven |
| **Notifications and icons** | host (Marker icons per player) | per-owner icon set `(prefab rank, priority, layer, flags, target slot, location)` applied through one replication-owned `IconCommandBuffer` (`Game/Game.Notifications/IconCommandBuffer.cs:60-125` [V]); simulation Transaction icons as `IconEvent` | `IconElement`, clustering, animations; Marker icons from the local infoview | SPEC from EDC of `Icon` entities → P0 | event-driven |
| **Chirps, life path, radio** | host (likes per player) | chirp entities with `RandomLocalizationIndex` (`Liked` bit masked out), `m_Likes` updates, `LifePathEvent`/`LifePathEntry`, `RadioTag` events | `ChirpLink`, chirp name cache (`ChirpLinkSystem`) | EDC → P0; likes P3 | event-driven |

### 2.3 Moving agents (class M)

| Category | Authority | Minimal authoritative representation | Derived locally | Capture → priority | Rate / size [E] |
|---|---|---|---|---|---|
| **Vehicles** (moving, incl. trailers and train members) | host | existence (global): spawn bundle = archetype kind (moving/stopped/controller), prefab rank, host `UpdateFrame`, `PseudoRandomSeed`, role component, `Car`/`Train`/... flags, forward refs (`Owner`, `PersonalCar.m_Keeper`, `Target`, `CurrentRoute`, `Controller`/`LayoutElement`), lane key, `Unspawned`/`TripSource` as on host, `Transform`, `Moving`; effect-relevant state on change (`Car.m_Flags`, role `m_State`); motion frames in the interest set | `OwnedVehicle`, `GuestVehicle`, `Passenger`, `RouteVehicle`, `LaneObject` (from Created/Deleted, R15 §4.5), interpolation, animation, lights, sirens (with client `EffectsUpdated`, R15 §3.5), traffic ambience map (optional) | existence EDC → P0; motion → P2 unreliable; lane key changes ≤0.1 Hz → P3; cargo, passengers, path buffers only through inspection closures | existence 3-6 KB/s at speed 1; motion budgeted |
| **Parked vehicles** | host | existence (global, persistent): parked archetype, `ParkedCar` lane key + curve position; moving↔parked swaps as structure records | `LaneObject` | EDC → P0 | park/unpark events |
| **Citizens on foot, pets, wildlife** (creatures) | host | **interest-scoped existence**: spawned on a client when it enters that client's interest set (baseline = spawn bundle + 4 frames), locally removed when it leaves (hysteresis); always included when part of an inspected or followed closure | as vehicles | P1 on entry, motion P2 | ~5-10× cheaper than global creature existence [E] |
| **Traffic lights** | host | node `TrafficLights` (6 bytes, `Game/Game.Net/TrafficLights.cs` [V]) for junctions in the interest set | lane `LaneSignal.m_Signal` and pole `TrafficLight.m_State` through the public static helpers (`TrafficLightSystem.cs:782, 878` [V]) | SDC class V (interest only) → P1 | 1-5 KB/s |

### 2.4 Fields, terrain, water, mod state, per-player state

| Category | Authority | Minimal authoritative representation | Derived locally | Capture → priority | Rate / size [E] |
|---|---|---|---|---|---|
| **Terrain heightmap** | host | strokes from clients are intents (captured `Brush` structs, R07 §5.2); the host sends dirty 64×64 `ushort` tiles of `TerrainSystem.m_Heightmap` after a stroke settles, plus tile hashes | cascade, CPU heights, object heights (`GroundHeightSystem` on the host; results replicate as `Transform`/`Node`/`Curve` deltas) | SPEC terrain tiles → P1 burst; optional predicted stroke replay for smooth visuals | 10-40 KB per large stroke |
| **Terrain material paint** | host | user splatmap tiles (visual), low priority | rendering | SPEC → P3 | rare |
| **Water** | host for gameplay; visuals simulated per client | water sources are entities (incl. mod sources without `PrefabRef`, R09 §2.9) via EDC/SDC; global `WaterSystem` parameters and sea level as session state; quantised correction tiles of `WaterSystem.WaterTexture` (256×256, depth f16 + velocity 2×f16 + pollution u8) | the GPU water simulation for visuals | sources P0; tiles P3 round-robin + hot tiles | 5-20 KB/s configurable |
| **Snow** | client-local (cosmetic) | none (optional rare snapshot) | `SnowSystem` (R07 §7) | — | 0 |
| **Cell maps**: air/ground/noise pollution, groundwater, soil water, natural resources, land value, attractiveness, population, availability, telecom, wind, ambience | host | full map on each host update, XOR-delta against the last sent map + LZ4 | wind and ambience visuals may run locally (`SpawnableAmbienceSystem` kept, R11 §3) | SPEC → P3; infoview topic boost to P1 | 2-10 KB/s averaged |
| **Mod state** | host | (a) mod `ISerializable`/`IEmptySerializable` components: generic replication (they are in the save query, R09 §1.1); (b) mod system state: per-mod codec in an adapter; (c) sim-relevant mod settings: session-settings adapters; (d) prefab-level mutations: parity by handshake | whatever the mod derives locally (adapter decides) | EDC/SDC, SYS, SPEC | mod-dependent |
| **Policies** | player-authored, host-validated | `Policy` buffers + `DistrictModifier`/`BuildingModifier`/`RouteModifier`/`CityModifier` buffers + option masks as state. For district and route targets the client additionally synthesises the same `Modify` event so dependent regeneration runs (`ModifiedSystem` is idempotent, R10 §4) | modifier initialisers on created entities | EDC (target `Updated`) → P0 | tiny |
| **Names** | player-authored | custom names keyed by NetSlot `(slot, UTF-8 string)`; city name; generated names as `RandomLocalizationIndex` indices (never strings) | generated names, addresses (`NameSystem.cs:390-491`), locale-dependent texts | SPEC names → P0 | tiny |
| **Camera, selection, active tool, infoview, open panels** | per player | never replicated as state; summarised in the view report for attention | — | view report (unreliable 10 Hz, ~40 B) | 0.4 KB/s up |
| **Tutorials, chirp likes, achievements, radio channel, photo mode, cinematic camera** | per player | none (achievements are off for every peer anyway, R14 §7) | — | — | 0 |
| **Session settings** (speed policy, permissions, sim-relevant mod settings) | host | session-settings message on change | — | P0 | tiny |


---

## 3. Entity identity

### 3.1 NetSlot, generation and NetId

- **`NetSlot`**: a dense unsigned index (24 bits, up to 16.7 M) into flat arrays on every peer. It is what the wire
  carries (varint: 3 bytes up to 2²¹ slots).
- **`Gen`**: an 8-bit generation, incremented whenever a slot is reused. It travels only in `SPAWN`/`DESPAWN` records,
  in unreliable motion slot assignments and in repair requests, to reject stale references.
- **`NetId`** = `(Gen << 24) | NetSlot`, a `uint` used for persistence and diagnostics.

Dense slots instead of ever-growing ids keep varints short and let both sides use arrays instead of hash maps
(R08 §10.3 Option B recommends exactly this dense side-table pattern, which the game's own save serializer also uses,
R08 §7.3).

**Allocation (host only, `SlotAllocator` in Core).** Free-list, lowest slot first. A freed slot is reused only after
every connected peer has acknowledged the epoch that carried its `DESPAWN` and the slot is older than the epoch ring's
tail (§7.5), so that no in-flight unreliable record or repair response can address a reused slot.

### 3.2 Which entities get a slot

The candidate set is the game's own definition of persistent state: the save query of `SerializerSystem`
(`PrefabRef` / explicit singleton and graph types / any mod `ISerializable` component, minus `Temp`, `Deleted`,
`NetCompositionData`, `EffectInstance`, `LivePath`; `Game/Game.Serialization/SerializerSystem.cs:171-214`, R04 §2.1,
R09 §1.1). Using this query gives automatic coverage of mod components (entities without `PrefabRef`, pattern P10
in R09 §1.3, such as Water Features sources, are included, R09 §5.1).

`ArchetypeClassifier` (Game layer, cached per `EntityArchetype`) then removes derived and local families. First match
wins:

| Rule | Result | Reason |
|---|---|---|
| `Temp`, `PrefabData` (prefab entities), `LivePath`, `PrefabRef.m_Prefab == Entity.Null`, `MIT_ControlPoint` | excluded | tool-local or prefab (R09 §2.3) |
| `Game.Net.Lane` | derived (lane key only) | regenerated by `LaneSystem` (R13 §2) |
| `Secondary` | derived | regenerated by `SecondaryObjectSystem` (R13 §3.2) |
| `Game.Zones.Block` | derived (block key) | regenerated by `BlockSystem` (R13 §4) |
| `Icon` | special icon channel | (R14 §4) |
| `ElectricityFlowNode/Edge`, `WaterPipeNode/Edge` and their connection entities | derived topology | built by the graph systems in Modification phases; created without `Created` (`Game/Game.Simulation/ElectricityFlowSystem.cs:410-413` [V]) |
| `Object` + `Owner` where the owner prefab's sub-object list contains this prefab, and the object is not a `ServiceUpgrade`/`Extension` | derived (sub-object key) | regenerated and reused by `SubObjectSystem` (R13 §3.1) |
| gate bypass routes owned by a building (created by `Game.Buildings.InitializeSystem`, R11 §2.4) | derived | local child |
| `Vehicle`, `Creature` | class M | §2.3 |
| `Citizen`, `Household`, `HouseholdPet`, `CompanyData`, seekers, `CoordinatedMeeting`, `ServiceRequest`, `Chirp`, `LifePathEvent`, `EventJournalEntry` | class A | §2.2 |
| `Node`, `Edge`, static `Object`, `Area`, `Route`/`Waypoint`/`Segment`, `Aggregate`, `WaterSourceData`, mod-only persistent entities, `Event` entities | class S (events: S existence, V values) | §2.1 |
| `Game.City.City`, the `ServiceBudgetData` singleton, `TimeData`, statistic entities | class G | §2.2 |

The sub-object rule is an inference [I] from R02 §9.1 item 5 and R13 §12; the diagnostics dump (§12.3) prints per-rule
counts so the classification can be audited in the first in-game session (§13.2 item 6).

### 3.3 Side tables

| Table | Host | Client | Size for 1 M slots [E] |
|---|---|---|---|
| `slotByEntityIndex[int]` + `versionByEntityIndex[int]` | yes | yes (reverse mapping for intents, hashing and local change capture) | 8 MB |
| `entityBySlot[Entity]`, `genBySlot[byte]`, `classBySlot[byte]`, `archetypeIdBySlot[ushort]` | yes | yes | ~12 MB |
| `revisionBySlot[ushort]` | yes | yes | 2 MB |

All lookups are O(1) with a version check (an `Entity` whose version differs from the recorded one is a different
entity; R08 §10.1).

### 3.4 Persistence and the join map

`NetIdSystem : GameSystemBase, IDefaultSerializable, IPostDeserialize` (R12 §1.4) is serialised into **every** save
(join snapshot, host autosave, manual save). It writes `(Entity, NetId)` pairs plus the allocator state through the
save's own entity table, so on a client the stock `BinaryReader` hands it entities that are already remapped to the
freshly loaded ones (R12 §1.4, §2.1). Consequences:

- the join needs no separate id list, reflection or Harmony;
- a session resumed from a host save keeps all slots;
- loading such a save without the mod skips the block (`ObsoleteSystemSerializer`, R12 §6.3).

Gotchas carried over from R12 §1.5: fixed `AssemblyVersion` plus a self-alias `[FormerlySerializedAs]`; never touch
`EntityManager` inside `Deserialize` (it runs inside an exclusive transaction); never create mod-only entities for
session data (R12 §3).

### 3.5 Prefab identity: ranks, not tables

After prefabs are loaded, every peer computes `PrefabRank` = position of the prefab's `PrefabID` (type name, prefab
name, hash; `Game/Game.Prefabs/PrefabID.cs:15-36`, R04 §1.3) in the **lexicographically sorted list of all registered
prefabs**. `PrefabIndex` (Core) holds `prefabEntityByRank[]` and `rankByPrefabEntityIndex[]`.

- The handshake exchanges a 128-bit digest of the sorted list. Equal digest means equal ranks: no table is sent.
- If the digests differ (an extra UI-only asset pack, for example) but every prefab referenced by the city resolves,
  the host sends an explicit `(rank → PrefabID)` table for the referenced set only (R04 §1.3 recommends a referenced-set
  dictionary).
- Duplicate `PrefabID`s (first registration wins, `PrefabSystem.cs:152-172`) are listed in the handshake report.
- Zone types (`Cell.m_Zone` holds a session-local `ZoneType.m_Index`, R04 §4.5) are translated to and from zone prefab
  ranks.

### 3.6 Type identity and archetypes

- `TypeRank` = position of `(StableTypeHash, viewIndex)` in the sorted list of replicated type views (§4.2).
  `StableTypeHash` covers namespace, name, assembly and field layout (R08 §5). The handshake digest covers
  `(StableTypeHash, SizeInChunk, ElementSize, EntityOffsetCount, IsBuffer, IsEnableable, policyVersion)`.
- **Archetypes**: a spawned entity normally uses its prefab's baked archetype (`ObjectData.m_Archetype`,
  `NetData.m_EdgeArchetype`, `MovingObjectData.m_StoppedArchetype`, ...; R04 §1.1). A `SPAWN` record therefore carries
  only the prefab rank plus an optional delta (types added or removed relative to the baked archetype). Archetypes
  without a prefab (water sources, mod records, City) are defined once per session with an `ARCHDEF` record and
  referenced by a session archetype id.
- **Structural signature = serializable types only**, i.e. the save's stripped archetype (R04 §2.2). Non-serializable
  components never travel and never count as a structural change: lifecycle tags, `Highlighted`, runtime caches, and
  `Game.Tools.Hidden`, which the host's own tool previews put on originals (`Game/Game.Tools/Hidden.cs` has no
  serializer interface [V]; R02 §3.2). Presence-only serializable tags such as `Overridden` (`IEmptySerializable`
  [V]) do travel.

### 3.7 Derived-with-identity keys (no slots)

Some derived entities are referenced by replicated state. They are addressed by portable keys and resolved lazily on
the client through per-owner caches that are invalidated whenever the owner is `Updated` or `Deleted`:

| Family | Key | Users | Notes |
|---|---|---|---|
| Lanes | `PLK = (owner slot, lane prefab rank, master/slave flags, start/middle/end PathNode with owner index re-encoded as slot)`; for node lanes the middle counter is ignored and a geometric fallback with reversed match is used (R13 §2.5) | `CarCurrentLane`, `ParkedCar`, `HumanCurrentLane`, `TrainCurrentLane` lane fields | `PathNode` packs `owner.Index << 32` (`Game/Game.Pathfind/PathNode.cs:20-49`); the generic entity remapper never sees it, so lane-typed fields have a dedicated codec |
| Zone blocks | `(owner edge slot, side, m_Position quantised to 1/64 m, m_Size)` | zone `Cell.m_Zone` overlay | R13 §4.3 |
| Prefab sub-objects | `(owner slot, prefab rank, Aligned.m_SubObjectIndex or quantised local position)` | tree growth, stops referenced by `Connected` | R13 §3.1, §9 |
| Icons | `(owner slot, notification prefab rank, target slot)` | icon channel | the same identity `IconCommandSystem.FindIcon` uses (R14 §4.2) |
| Flow-graph edges | `(owner slot of start flow node, owner slot of end flow node, role)` via `ElectricityNodeConnection` / `ElectricityBuildingConnection` / valve connections | flow values under electricity/water infoview attention only | [I]; first verify that topology regenerates identically (§13.2) |

### 3.8 Revisions

`revisionBySlot` is incremented by the host whenever an S-class entity is captured by EDC. Clients put the revision of
every original they edited into tool change-sets; the host rejects a change-set whose revisions are stale (R10 §7.3,
§7.8). Two bytes per slot buy cheap, exact conflict detection.

---

## 4. Replication engine

### 4.1 Data flow

```
HOST, main thread (ECS only)                               HOST, worker threads (Core)
───────────────────────────────────────────────────────    ─────────────────────────────────────────────
MainLoop: EndFrameBarrier playback
  HostIngressSystem      commits client change-sets  ◄──── inbound queue (decoded intents)
  HostMotionSamplerSystem  TransformFrame slots ──────────► MotionEncoder (per peer, Q-B) ──► lane U (unreliable)
  [ToolSystem, Modification1 … ModificationEnd]
  HostCaptureSystem  (UpdateBefore SaveGameSystem)
     EDC  (tags → transactions, slots, despawns)  ──┐
     SDC  (budgeted chunk scan → shadow + dirty)   ├──► ShadowStore, PeerDirtySets, EpochBuilder
     SYS/SPEC (system state, prefab bits, names…)  ┘        │
UIUpdate: HostCommandSystem executes UI intents              ▼
LateUpdate: SimulationSystem (host sim)                   PeerScheduler (token bucket, P0…P3)
                                                             │ RecordEncoder + LZ4 ──────────► lane R (reliable)
                                                             │ BlobSender (snapshots, tiles) ► lane B (bulk)

CLIENT, main thread                                        CLIENT, worker threads
───────────────────────────────────────────────────────    ─────────────────────────────────────────────
MainLoop: EndFrameBarrier playback                         decode, decompress, merge epochs (coalescing)
  ClientApplySystem  (UpdateAfter EndFrameBarrier) ◄────── ApplyPlan queue
  [ToolSystem: ToolCaptureApplySystem / ToolCaptureResultSystem]
  [Modification…; AggregateReconcileSystem after AggregateSystem]
  LocalChangeCaptureSystem (UpdateBefore PrepareCleanUpSystem) ──► intents ──► lane R
  SpeedMirrorSystem (UpdateBefore RenderingSystem)
PreCulling: MotionWriterSystem (UpdateBefore PreCullingSystem) ◄── MotionStager (staging rings)
LateUpdate: ClientClockSystem (SimulationSystem disabled after load)
```

### 4.2 Schema and per-type policies

**Discovery (`SchemaDiscovery`, Game layer).** At session start every peer enumerates the serializable component
types from `SerializerSystem.componentLibrary` (R01 §4.1) and builds a `TypeSchema` per type from `TypeManager`:
`TypeIndex`, `StableTypeHash`, `SizeInChunk`, `ElementSize`, `IsBuffer`, `IsEnableable`, zero-sized flag and the entity
offsets from `TypeManager.GetEntityOffsets` (R08 §5, §7.2). Types failing the raw-byte eligibility filter of R08 §5
(managed, blob refs, native containers, chunk components) are excluded or need a custom codec. A startup audit lists
types with `FixedList`/`fixed`/pointer fields, where entity references could hide (R08 risk 4).

**Policy (`TypePolicy`, Core, curated table plus defaults):**

```csharp
sealed class TypePolicy {
    ReplicationClass Class;      // S, V, A, M, G, Excluded, Derived
    uint[] AuthWordMask;         // which 4-byte words are authoritative (others: derived/padding, never sent, never hashed)
    TypeView[] Views;            // optional split of one type into views with their own class (e.g. Road flags vs flow)
    CodecId Codec;               // Raw, Quantized<T>, AppendBuffer, SparseBuffer, LaneKeyFields
    TagRule ApplyTags;           // UpdatedClosure | BatchesUpdated | EffectsUpdated | PathfindUpdated | None
    AttentionTopic Topics;       // infoview topics that boost this type (§4.7.4)
    bool PlayerAuthorable;       // local client edits become intents (§5.3) instead of being reverted
    HashQuantizer Quantizer;     // e.g. terrain-snapped heights hashed at 1/8 m (§8.1)
    bool ResetByKeptInitializer; // re-applied one frame after spawn (§4.6.6)
}
```

Defaults for types not in the curated table (this is the generic mod path): class = class of the entity's archetype,
full word mask, `Raw` codec, `UpdatedClosure` for class S and `None` otherwise. `IEmptySerializable` data types are
presence-only (the game itself rebuilds their data, R04 §0.2). Non-serializable types are never replicated: by the
game's own definition they are runtime or derived state.

Curated examples:

| Type | Class | Field policy | Codec | Apply tags | Why |
|---|---|---|---|---|---|
| `Game.Buildings.Building` | S | `m_OptionMask`, `m_Flags` authoritative; `m_RoadEdge`, `m_CurvePosition` excluded (`Game/Game.Buildings/Building.cs` [V]) | Raw | UpdatedClosure | road edge is recomputed by `RoadConnectionSystem`; raw writes would desync `ConnectedBuilding` (R13 §8) |
| `Game.Net.Road` | split | view 0: `m_Flags` (S); view 1: the four `float4` flow vectors (V, topic Traffic) (`Game/Game.Net/Road.cs` [V]) | Raw | view 0: UpdatedClosure; view 1: None | half-align flags are a structural source; flow is simulation output |
| `Game.Objects.Transform`, `Game.Net.Node`, `Game.Net.Curve` | S | all; heights hash-quantised | Raw | UpdatedClosure | R02 §9.1 |
| `Game.Citizens.Citizen` | A | all (`Game/Game.Citizens/Citizen.cs` [V], 22 B serialised) | Quantized (flags u32, 4 bytes as bytes, counters varint) | None | topics Happiness, Health, Wellbeing, Education, Age (§4.7.4) |
| `Game.Citizens.Household` | A | all | Quantized | None | topic Wealth |
| `Game.Economy.Resources` (buffer) | A/V | all | SparseBuffer (resource index u8 + zigzag varint amount) | None | changes constantly on households and companies |
| `Game.City.CityStatistic` (buffer) | G | all | AppendBuffer (drop-front k, append n) | None | history ring changes by one sample per 8192 ticks |
| `Game.Net.TrafficLights` | V | all (6 B) | Raw | None (client derivation system) | §6.3 |
| `Game.Zones.Cell` (buffer on derived blocks) | S (block overlay) | `m_Zone` only, as zone prefab rank | RLE per block | block `Updated` | R13 §4.3 |
| `Game.Policies.Policy` + modifier buffers | S | all | Raw | UpdatedClosure (+ synthesised `Modify` for districts/routes, §2.4) | R10 §4 |
| `Game.Rendering.CustomMeshColor` (enableable buffer) | S | all + enabled bit | Raw | BatchesUpdated | R10 §3.5 |
| `Game.Objects.TransformFrame` | M | motion stream only | Q-B frames (§4.5.6) | none | R15 |
| `CarCurrentLane`, `ParkedCar`, `HumanCurrentLane`, `TrainCurrentLane` | M | lane fields as PLK, flags; per-tick curve position excluded except for `ParkedCar` | LaneKeyFields | none | R15 §5 |
| `Game.Triggers.Chirp` | A | `Liked` bit masked out | Raw | None | likes are per player (R14 §5.4) |
| `Game.Citizens.CarKeeper` (enableable, empty-serializable) | A | enabled bit only | — | None | R15 §4.5 |
| `Game.Areas.Surface` (wetness/snow), `Game.Routes.HiddenRoute` | Excluded | — | — | — | cosmetic local / per player (R11 §3, R10 §6.1) |
| `Game.Net.ServiceCoverage`, `ResourceAvailability` (buffers) | V | all | SparseBuffer | None; `ResetByKeptInitializer` | `Game.Net.InitializeSystem` resets them on created edges (R11 §2.11) |

### 4.3 Host capture

#### 4.3.1 Event-driven capture (EDC)

`HostCaptureSystem` is registered with `updateSystem.UpdateBefore<HostCaptureSystem, SaveGameSystem>(SystemUpdatePhase.MainLoop)`.
That slot is after `ModificationSystem` (`SystemOrder.cs:60`), so derived data and all one-frame tags of the frame
exist, and before `SaveGameSystem` (`:69`) and `PrepareCleanUpSystem` (`:50`, `UpdateAfter`), so a join snapshot taken
in the same frame lines up exactly with an epoch boundary (§7.2).

Per frame:

1. Query `Any(Created, Updated, Deleted, Applied)`, `None(Temp)`, iterate chunks with RO handles and classify archetypes.
2. **`Deleted`** (and not `Created` in the same frame): emit `DESPAWN(slot, gen)`; clear the slot's dirty bits for all
   peers; mark the slot for deferred free. `Created+Deleted` in one frame: nothing is sent.
3. **`Created`**: allocate a slot, capture all replicated types into the shadow, emit `SPAWN` (prefab rank + archetype
   delta + host `UpdateFrame` + payloads). Agents' spawn bundles (household + citizens, building + sub-nets + sub-areas)
   land in the same epoch because they are created in the same frame (EndFrameBarrier playback). Creatures get a slot
   here too, but their `SPAWN` is delivered per peer by the interest manager (§2.3), not in the shared P0 content.
4. **`Updated`/`Applied` on class S or G**: memcmp each replicated type against the shadow; emit `VALUE` records for the
   types that differ and a `TOUCH` flag so the client tags the entity `Updated` like the host did. This replicates the
   host's exact closure tag set (R02 §7) instead of recomputing it. Revision is incremented.
5. **`Updated` on class V, A, M**: the entity is pushed into the SDC hot set for this frame (it is compared
   immediately) instead of being captured wholesale; if the hot set is full, the entity simply waits for its class
   cadence. Archetype changes (vehicle park/unpark swaps, `Abandoned` added, Tree Controller prefab swaps) are detected
   by comparing the current structural signature (§3.6) with `archetypeIdBySlot` and emitted as `STRUCT` records.
6. **Unbound-entity sweep**: chunks of slot-bearing classes whose order version changed are checked for entities
   without a slot (creations without `Created`, such as Better Bulldozer record entities created with
   `EntityManager.CreateEntity`, R09 §2.2). They are spawned like step 3.
7. All S-class records of the frame form one **transaction** (`TX`) inside the epoch.

Archetype signatures contain serializable types only (§3.6), so transient tags (`Created`, `Updated`, `Deleted`,
`Applied`, `BatchesUpdated`, `EffectsUpdated`, `PathfindUpdated`, `Highlighted`, `Hidden`) never cause structural
records (R08 §12.5, R09 §5.6). Cost is proportional to the number of tagged entities, typically a few hundred per
frame: ≤0.5 ms [E].

#### 4.3.2 Scan-driven capture (SDC)

Silent writes (simulation jobs writing values without tags) are found by `ChunkScanner`, which applies the two-stage
design of R08 §12.3 in a time-sliced, Burst-free form:

- **Per class**: one `EntityQuery` with RO `DynamicComponentTypeHandle`s (created from `ComponentType.ReadOnly`, never
  RW, R08 §6.3), options `IgnoreComponentEnabledState`. A chunk list refreshed when the combined order version of the
  query changes.
- **Per chunk record** keyed by `ArchetypeChunk.SequenceNumber`: archetype, order version, change version per type.
- **Version filter**: a type whose chunk change version equals the recorded one is skipped (equality, not `DidChange`,
  so wrap-around is harmless, R08 §3.1).
- **Byte compare**: for changed columns, each entity's authoritative words are compared with the shadow at its slot
  with `UnsafeUtility.MemCmp` (native) or a masked word loop for partially authoritative types. A difference updates
  the shadow, the per-type version, every peer's dirty bit and the bucket hash (§8.2).
- **Membership**: a changed order version triggers an entity-column diff against the slot table (new entities →
  spawn as a safety net for EDC; missing entities checked with `EntityManager.Exists` → despawn).
- **Buffers**: length + bytes via `GetUntypedBufferAccessor`; never the raw chunk column, which holds a process-local
  pointer (R08 §2).
- **Enabled bits**: `GetEnableableBits` compared with the shadow bitset.
- **Job safety**: `query.CompleteDependency()` before reading (safety checks are stripped, R08 §1).

**Cadence instead of brute force.** Each class has a full-cycle period. Per frame, the scanner takes
`ceil(chunks_c / (period_c × fps))` chunks from each class cursor, within a main-thread budget (default 1.0 ms at
≥50 fps, 2.0 ms at ≤30 fps, adaptive). Defaults [E]:

| Class | Period | Typical chunks [E] | Chunks per frame at 30 fps | Changed entities per second [E] |
|---|---|---|---|---|
| G (singletons, hot list) | every frame | <10 | <10 | tens |
| V (buildings, net values, events, traffic lights) | 4 s | ~3-6k | 25-50 | ~5-15k |
| A (agents, requests, chirps, journal) | 60 s | ~3-5k | 2-3 | ~2-4k |
| S (safety net; EDC is primary) | 20 s | ~5-15k | 8-25 | ~0 |
| Hot set (inspected closure, followed, selected; ≤256 entities) | every frame | per entity | — | — |

At ~0.5 µs per chunk version check and ~0.2 µs managed overhead per changed entity (Mono, no Burst) [E], the scan
costs ~0.1-0.5 ms per frame; the budget leaves headroom to shorten periods automatically when CPU is free. The R08 §13
"Burst is mandatory" conclusion assumed a full scan every tick; tiered cadences remove that requirement.

#### 4.3.3 System state (SYS)

`SystemStateRegistry` maps a stable system id to a codec:

| Codec kind | Systems | Mechanism |
|---|---|---|
| `GameSerializableCodec` | `TaxSystem`, `ResidentialDemandSystem`, `CommercialDemandSystem`, `IndustrialDemandSystem`, `TradeSystem` | the system's own `Serialize` into a `BinaryWriter` over a `NativeList<byte>` (a write `Context` from the 3-argument constructor is safe, R01 §2.3). Client: `Deserialize` with an owned `LoadGame` context with `Version.current` and every `FormatTags` bit enabled by reflection (R11 §5.3), run after the structural batch (job sync). Verified side-effect-free (R11 §5.2). |
| `GameSerializableCodec` (after audit) | `Count*Systems`, `CityProductionStatisticSystem`, electricity/water statistics and trade systems, `TransportLineSystem` | enabled one by one after a side-effect audit of each `Deserialize` (§13.2) |
| `ReflectionCodec` | `CityServiceBudgetSystem` (14 income + 15 expense ints, totals, per-service maps; `CityServiceBudgetSystem.cs:759-806` [V]), `MilestoneSystem` cached XP fields (R14 §1.4), `CityStatisticsSystem` sample count and frame (R14 §6.2) | cached `FieldInfo`s; `NativeArray` structs share memory with the system, so element writes land in the system's arrays |
| `CustomCodec` | `ClimateSystem` weather state, Water Features and vanilla `WaterSystem` parameters (public setters, R07 §6.2), `CityConfigurationSystem.cityName` | explicit fields |

Change detection hashes the serialised blob; a changed blob is sent as `XOR(previous, current)` zero-run-length coded
(keyframe every 30 s or on request). Rate limits: demand ≤1 Hz, budget ≤1 Hz, others ≤0.25 Hz [E].

#### 4.3.4 Special channels (SPEC)

- **Prefab state**: every frame that contains an `Unlock` entity (cheap emptiness test) and every 10 s, scan the
  `Locked` enabled bits of all prefab entities into a bitset over prefab ranks and diff it (R14 §2.4). Also
  `PlacedSignatureBuildingData` presence, `UnlockRequirementData.m_Progress` (≤0.5 Hz) and the collected budget data on
  service prefabs.
- **Icons**: EDC of `Icon` entities becomes `ICON_ADD/ICON_REMOVE(owner slot, prefab rank, target slot, priority,
  layer, flags[, location])`; simulation Transaction-layer icons become `ICON_EVENT` (R14 §4.5).
- **Names**: 1 Hz diff of `NameSystem.m_Names` (private dictionary, by reflection; R10 §3.5) translated to slots, and
  of `CityConfigurationSystem.cityName`.
- **Cell maps**: each `CellMapSystem<T>` map is hashed on a tick aligned to its update interval (R07 §8.2); a changed
  map is sent as XOR-delta + LZ4. Read with `GetMap(readOnly: true, out deps)` + `deps.Complete()` (R07 §8.1).
- **Terrain**: dirty rectangles come from brushes the host applies (host player's brushes seen in the `ApplyTool`
  phase, client strokes executed by the host through the public `TerrainSystem.ApplyBrush`). After a stroke settles
  (~200 ms without new stamps), the union rectangle of `TerrainSystem.heightmap` is read back asynchronously and sent as
  64×64 tiles (R07 §5.2).
- **Water**: round-robin `AsyncGPUReadback` of one 256×256 tile of `WaterSystem.WaterTexture` every few seconds plus
  "hot" tiles near recent water-source changes, quantised and compressed (R07 §6.7).

#### 4.3.5 Motion sampling (host)

`HostMotionSamplerSystem` runs right after ingress at the start of MainLoop, where `EndFrameBarrier` playback has just
synchronised the movers' jobs (any remaining dependency is completed RO). For each tick `T` in
`(lastSampled, frameIndex]` it filters movers with `SetSharedComponentFilter(new UpdateFrame(T & 15))`, exactly like the
movers (R15 §7.2), reads slot `(T >> 4) & 3` of `TransformFrame`, tests `Transform.xz` against each client's interest
region and hands `(client, interest slot, frame)` tuples to the Core `MotionEncoder`. Slots stay valid for 64 ticks, so
sampling a frame late is safe (R15 §2.3). Per tick only ~1/16 of movers are touched (~1.3k of 21k), a few hundred
nanoseconds each in managed code: ~0.2-0.5 ms per client per frame [E].

### 4.4 Shadow store, dirty sets, coalescing and epochs

- **`ShadowStore`** (Core, unmanaged memory, no GC pressure): per type view a paged slab indexed by slot
  (`authWords × 4` bytes per slot, pages of 4096 slots allocated on first use), a presence bitset, an enabled bitset,
  and for buffers an arena of `(offset, length)` ranges with compaction. The shadow is "what has been captured and
  will be sent". It is also the source for encoding (no ECS access on worker threads), repair responses and host-side
  hashing. Size ≈ replicated payload: 30-80 MB [E].
- **`PeerDirtySet`**: per peer, per type view, a paged bitset over slots ("this peer does not have the latest value").
  ~0.3-1 MB per peer [E]. Set by SDC/EDC on change; cleared when the record is handed to the transport for that peer.
- **Coalescing by construction**: the encoder reads the shadow at send time, so a value that changed ten times while
  waiting is sent once, in its latest form. Bandwidth pressure turns into staleness, never into an unbounded queue.
- **Full values for pulled records**: P1/P3 records carry all authoritative words of the type (no per-peer baselines to
  maintain); the per-packet LZ4 pass removes most redundancy. Word-delta encoding against per-peer baselines is an
  optional later optimisation for the hottest types, enabled only if telemetry shows it pays (§12.4, M9).
- **Epochs**: one host frame with output = one epoch (`uint`, monotonic). An epoch contains the frame's P0 content
  (transactions, despawns, events, icons, prefab state, names, clock anchors) plus whatever P1/P3 records the scheduler
  pulled for that peer. P0 content is encoded once and shared by all peers; it is also appended to the **`EpochLog`**
  ring (default 128 MB or 10 minutes) used for join catch-up and short rejoins.
- **Ordering**: a slot's `VALUE` records are only pulled after its `SPAWN` has been queued for that peer, and its dirty
  bits are cleared at `DESPAWN`; all of this goes through the one reliable ordered lane, so a client never sees values
  for an entity it has not spawned (§9.2).

### 4.5 Serialization and wire records

#### 4.5.1 Decision: raw bytes with remapping, game `ISerializable` only where it pays

| Criterion | Raw component bytes + entity-offset remap (chosen for entity data) | Game `ISerializable` per component (R01 §11.2 Approach B) |
|---|---|---|
| CPU (Mono, no Burst) | `memcmp`/`memcpy` native; remap only at known offsets | one generic delegate call per component, field-by-field writes, managed reader |
| Change detection | same bytes as the shadow compare | needs a separate serialisation to compare |
| Size | fixed size; LZ4 removes zeros; field masks drop derived words | sometimes smaller (narrowed fields such as `Building.m_Flags` as byte, R01 §12.4) |
| Robustness | requires identical layouts: enforced by the `StableTypeHash`+size digest (same game build and mod set are mandatory anyway, requirement 6) | version-aware branches (irrelevant within one build) |
| Hidden entity indices | handled by dedicated codecs (`PathNode` in lanes) and the startup audit | handled by the game's code, but external references become `Entity.Null` unless a custom `IReader` is written (R01 §3) |

Managed system state has no raw layout to copy, so it uses the game's `Serialize/Deserialize` (§4.3.3).

#### 4.5.2 Envelope and epoch message

Every message: `u8 kind`, `u8 flags` (bit0 = LZ4-compressed body, bit1 = continuation), body. Kinds: `Control`,
`Epoch`, `Motion`, `MotionControl`, `Clock`, `View`, `Intent`, `IntentResult`, `Hash`, `Repair`, `Blob`, `Chat`.

```
Epoch (lane R, reliable ordered):
  u32  epoch
  u32  hostTick                 // SimulationSystem.frameIndex at capture
  u8   sectionCount
  repeat: u8 sectionType, varint byteLength, payload
sectionType: TX (structural transaction), VAL (pulled values), SYS, PFB (prefab state), ICON, EVT (chirp/journal/XP
             message/radio/milestone), NAME, MAP (cell map delta), TER (terrain tile), CLK (clock anchor), HASH, SES
```

#### 4.5.3 Records

```
NetRef  = varint v:   0 = Entity.Null, 1 = local-only/unreplicated (client writes Null and keeps a diagnostic count),
                      even v ≥ 2 → slot = v/2 − 1,  odd v ≥ 3 → prefab rank = (v − 3)/2
SPAWN   = varint slot, u8 gen, varint prefabRank+1 (0 = none), varint archetypeRef (0 = prefab archetype),
          u8 updateFrame (0xFF = none), u8 flags {Applied, HasDelta, Touch},
          [HasDelta: varint addCount, typeRank…, varint removeCount, typeRank…],
          varint componentCount, repeat { varint typeRank, payload }
DESPAWN = varint slot, u8 gen, u8 flags {TouchNeighbours}
STRUCT  = varint slot, varint addCount, typeRank…, varint removeCount, typeRank…   (+ payloads of added types)
VALUE   = varint slot, varint typeRank, u8 hdr {bits0-1 encoding: FULL | BUFFER | REMOVE, bit2 enabled, bit3 touch},
          payload
TOUCH   = varint slot                                     (tag Updated, no data change)
ARCHDEF = varint archetypeId, varint typeCount, typeRank…
TX      = varint txId, varint recordCount, records…        (applied atomically in one client frame)
```

- **FULL**: the authoritative words in schema order; each entity field (8 bytes in memory) is replaced by a `NetRef`
  varint (typically 2-3 bytes); other words are little-endian raw unless the type has a quantised codec.
- **BUFFER**: `varint length`, `u8 op` (`0` replace all, `1` drop-front k + append n, `2` sparse element updates with an
  element bitmap), elements encoded like FULL.
- **Lane fields**: `LaneKeyFields` codec writes a PLK (owner slot varint + compressed PathNode low bits + prefab rank,
  ~6-10 bytes).

#### 4.5.4 Typical sizes [E]

| Item | Bytes on the wire (before LZ4) |
|---|---|
| Edge spawn (Edge, Curve 52 B, PrefabRef, seed, BuildOrder, Elevation, Upgraded, Road flags) | ~90-110 |
| Node spawn | ~45 |
| Closure `TOUCH` | 2-4 |
| Building spawn bundle (building + lot + 2-6 sub-nets) | 300-900 |
| Citizen spawn (quantised) | 35-60 |
| Household money update (`Resources` sparse) | 6-9 |
| `DESPAWN` | 4-5 |
| Icon add/remove | 6-9 |

#### 4.5.5 Compression

The body of every reliable message larger than 256 bytes is LZ4-compressed on a worker thread with the game's
`Colossal.AssetPipeline.Native.NativeCompression.CompressData(..., CompressionFormat.LZ4, level 1)` (the same native
backend `CompressionUtils` uses, `Colossal.IO.AssetDatabase/Colossal.Compression/CompressionUtils.cs` [V]); blobs use
ZSTD. Core depends only on an `ICompressor` interface and ships a managed fallback for unit tests. Saves are already
ZSTD-compressed and are not recompressed (R06 §5.4).

#### 4.5.6 Motion records

Report 15's Q-B encoding is adopted (R15 §9.1): per datagram `u8 kind, u32 T, u16 seq`, then records of ~10-11 bytes
(`u16` interest slot, position delta at 1/64 m with absolute escape, yaw 12 bits, pitch 7 bits, speed 10 bits + sign,
1-bit "flags unchanged" else 18 bits, human state/activity/timer bits). The tick is implicit because a datagram for
tick `T` only contains entities with `UpdateFrame == T & 15`. Interest slots are assigned on the reliable lane
(`MotionControl`: `u16 islot ↔ slot`, `UpdateFrame`, a 4-frame Q-A baseline of ~120 bytes on entry, R15 §7.2).

### 4.6 Client apply

#### 4.6.1 Exact system ordering

| Phase / position | System | Registration | Job |
|---|---|---|---|
| MainLoop, right after `EndFrameBarrier` playback (`SystemOrder.cs:49`) | `ClientApplySystem` | `UpdateAfter<ClientApplySystem, EndFrameBarrier>(MainLoop)` | intercept local UI events, apply epochs (§4.6.2) |
| PreTool (`OriginalDeletedSystem`), ToolUpdate, PostTool (`SubElementDeleteSystem`) | vanilla | — | see host deletions in the same frame (R11 §6.2) |
| ApplyTool, first | `ToolCaptureApplySystem` | `UpdateBefore<ToolCaptureApplySystem>(ApplyTool)` | capture A (R10 §7.2 IP2) |
| Modification1, first | `ToolCaptureResultSystem` | `UpdateBefore<ToolCaptureResultSystem>(Modification1)` | capture B (R10 §7.2 IP3) |
| Modification2B, after `AggregateSystem` | `AggregateReconcileSystem` | `UpdateAfter<AggregateReconcileSystem, AggregateSystem>(Modification2B)` | force host aggregate membership (R13 §7.1) |
| Modification4B, after `TrafficLightInitializationSystem` | `TrafficLightDeriveSystem` | `UpdateAfter<TrafficLightDeriveSystem, TrafficLightInitializationSystem>(Modification4B)` | apply node light state to lanes and poles (§6.3) |
| MainLoop, before `PrepareCleanUpSystem` | `LocalChangeCaptureSystem` | `UpdateBefore<LocalChangeCaptureSystem, PrepareCleanUpSystem>(MainLoop)` | turn local edits into intents; rolling verification slice (§5.2, §8.3) |
| MainLoop, before `RenderingSystem` (`:68`) | `SpeedMirrorSystem` | `UpdateBefore<SpeedMirrorSystem, RenderingSystem>(MainLoop)` | re-assert host speed after UI writers (R11 §7.3) |
| PreCulling, before `PreCullingSystem` | `MotionWriterSystem` | `UpdateBefore<MotionWriterSystem, PreCullingSystem>(PreCulling)` | JIT ring writer (R15 §2.6) |
| LateUpdate, before `SimulationSystem` (`:74`) | `ClientClockSystem` | `UpdateBefore<ClientClockSystem, SimulationSystem>(LateUpdate)` | drive the clock (§6.2) |
| UIUpdate | `SessionUISystem` | `UpdateAt<SessionUISystem>(UIUpdate)` | bindings (§11) |

#### 4.6.2 Per-frame apply pipeline

1. **Intercept local UI events** (§5.3) before their consumers run.
2. **Take work** from the inbound `ApplyPlan` queue (built on a worker thread by merging consecutive epochs: structural
   operations kept in order, values coalesced to the latest per `(slot, type)`). Whole transactions only; the time
   budget (default 3 ms) is checked between transactions and between value batches.
3. **Structural batch** (one sync point, R08 §9): creations grouped by `(archetype, UpdateFrame)` with
   `EntityManager.CreateEntity(archetype, count, Allocator.Temp)`, then
   `EntityManager.SetSharedComponent<UpdateFrame>(NativeArray<Entity>, value)` (batch overload,
   `Unity.Entities/Unity.Entities/EntityManager.cs:1190` [V]) and `AddComponent<Applied>(NativeArray<Entity>)`;
   archetype changes grouped by `(from, to)` with `AddComponent/RemoveComponent(NativeArray<Entity>, ComponentTypeSet)`;
   deletions by adding `Deleted` (never `DestroyEntity`: `CleanUpSystem` destroys them and `SubElementDeleteSystem`
   handles sub-nets, R11 §6.3).
4. **Data writes**: per `(chunk, type)` one RW dynamic handle access (`Update(this)` first), raw copy of the payload,
   entity fields decoded from `NetRef`s; buffers via `ResizeUninitialized` + copy; enabled bits via
   `ArchetypeChunk.SetComponentEnabled`. Unresolved slots are written as `Entity.Null` and recorded in a
   `PendingRefs` table that is patched when the slot spawns (out-of-order safety).
5. **Tags** per the rules of §4.6.4 (batched adds).
6. **Special channels**: icon commands through one `IconCommandBuffer` from `IconCommandSystem.CreateCommandBuffer()`
   (`Game/Game.Notifications/IconCommandSystem.cs:727` [V]); synthesised `Event+Unlock` and
   `Event+MilestoneReachedEvent` entities (R14); names; system-state `Deserialize` (after the structural batch, R11 §9
   risk R11); cell maps (`GetMap(false)` + `AddWriter`); terrain tiles (`Graphics.CopyTexture` + `OnAreaChanged` +
   `WaterSystem.TerrainWillChangeFromBrush` + `UpdateMinMax`, R07 §5.2).
7. **Bookkeeping**: the applied-hash ledger (§8.3) and the "touched this frame" bitset used by local change capture.

#### 4.6.3 Idempotent operations

- `SPAWN` for a slot that is already bound is an upsert (archetype adjusted, values written).
- `DESPAWN` of an unknown slot is ignored.
- `VALUE` for an unknown slot is parked for up to 16 epochs, then dropped with a repair request.
- Duplicated or replayed records are harmless.

This is what makes join boundaries, catch-up and repair simple (§7, §8).

#### 4.6.4 Tag rules (from R11 §6.3 and R15)

| Operation | Tags added on the client | Reason |
|---|---|---|
| Create (any class) | prefab archetype already contains `Created`+`Updated`; plus `Applied` and the host `UpdateFrame` | `Applied` keeps the given `UpdateFrame` (`UpdateGroupSystem.cs:408-416`) and makes `ParkedVehiclesSystem` skip the building (R11 §6.3) |
| Update, class S (`TOUCH`) | `Updated` (+ `BatchesUpdated`) on exactly the host's tagged set; `Applied` if the host had it | identical derived regeneration |
| Update, visual-only types | `BatchesUpdated` | R11 §6.3 |
| Update, effect-relevant vehicle state (`Car.m_Flags`, role `m_State`, `ParkedCar` presence, light bits in staged frames) | `EffectsUpdated` (on every `LayoutElement` member for trains) | R15 §3.5 |
| Update, lane pathfinding data | `PathfindUpdated` | R11 §6.3 |
| Motion | **none** | `Updated` would reset all four `TransformFrame`s and re-randomise lanes (`UpdateGroupSystem.cs:561-614`, R15 §0.4) |
| Moving ↔ parked swap | vanilla type sets including `Updated` | R15 §4.2 |
| Delete | `Deleted` on the entity and every first-class child the host deleted; `TOUCH` on surviving neighbours | R11 §6.3 |
| Events | only `Event+Unlock` and `Event+MilestoneReachedEvent` are synthesised; never command events (`Damage`, `Ignite`, ...) | their consumers are disabled or would double-apply (R11 §2.4) |

#### 4.6.5 Relationship changes

When a `PropertyRenter` (household or company) or `CurrentBuilding` changes, the client tags the property `Updated`
(or emits an `Event+RentersUpdated`) so that `SubObjectSystem` regenerates renter-dependent placeholders exactly as on
the host (R13 §3.1). Relationship changes are promoted to P0 so that building visuals and renter lists do not wait for
the rolling refresh.

#### 4.6.6 Post-creation fix-ups without extra traffic

Kept initialisers overwrite three kinds of host values on creation: coverage buffers on new edges
(`Game.Net.InitializeSystem`), prefab water sources (`WaterSourceInitializeSystem`) and transform frames
(`UpdateGroupSystem`) (R11 §6.4). Types flagged `ResetByKeptInitializer` are kept in a one-frame stash from the `SPAWN`
payload and re-applied at the next frame's apply point. `AggregateReconcileSystem` restores the host's aggregate
membership and order after `AggregateSystem` in the same frame (before `PrepareCleanUpSystem` destroys a merge loser,
R13 §7.1). Zone overlays are applied one frame after the edge transaction, once the client's `BlockSystem` has
regenerated blocks (R13 §4.3).

#### 4.6.7 Time-aligned application of inspection data

Inspection closures are sampled on the host at `frameIndex % 256 == 0` (the info panel cadence,
`SelectedInfoUISystem.cs:181, 383`, R15 §8). The client applies them when its own clock reaches that tick and calls
`SelectedInfoUISystem.RequestUpdate()`, so every peer shows panel numbers from the same simulated tick. All other
state is applied as soon as it arrives.

#### 4.6.8 Budgets, backlog and safety

- Apply budget 3 ms per frame (configurable); a single transaction is never split.
- If the queued work exceeds 2 s of epochs, the worker thread merges more aggressively (values are coalesced, so the
  backlog is bounded by the number of distinct dirty `(slot, type)` pairs, not by time).
- If the backlog exceeds 64 MB or 30 s, the client requests a hash-based resync (§8.5), and if that fails, a snapshot.
- Every apply step runs inside a try/catch per record class; an exception marks the class "suspect", skips it for the
  rest of the frame, logs a diagnostic record and schedules class repair (§8.5).

### 4.7 Bandwidth budgeting and prioritisation

#### 4.7.1 Streams

| Stream | Lane (§9.2) | Reliability | Priority | Content |
|---|---|---|---|---|
| Control | R | reliable ordered | P0 | handshake, session, intents and results, chat |
| Epoch P0 sections | R | reliable ordered | P0 | transactions, existence, events, icons, prefab state, names |
| Attention values | R | reliable ordered | P1 | in-view V changes, inspected closure, infoview topic data, system state |
| Motion | U | unreliable, sequenced | P2 | Q-B frames |
| Rolling values | R | reliable ordered | P3 | everything else that is dirty, cell maps |
| Bulk | B | reliable | lowest | snapshots, terrain and water tile bursts, repair dumps |
| Clock, view report, hash beacons | U / R | mixed | P0 | small |

#### 4.7.2 Budget source

`LinkEstimator` polls `SteamNetworkingSockets.GetConnectionRealTimeStatus` per connection (`m_nSendRateBytesPerSecond`,
`m_cbPendingReliable`, `m_cbPendingUnreliable`, `m_usecQueueTime`, ping, quality; R06 §4.1) every 100 ms.
Budget `B = min(userCap, 0.85 × sendRate)`, then AIMD on queue time: above 150 ms → −20 %; below 30 ms while
backlogged → +5 % per second. Defaults: per-client cap 300 KB/s, floor 48 KB/s, host total cap 1.5 MB/s (session
setting "upload budget"). The host total is split into equal per-peer shares, and a share a peer cannot use (small
link, idle view) is redistributed to the others; with seven clients at speed 4 this is what lowers motion quality
first (§4.7.5).

#### 4.7.3 Scheduler (per peer, every frame, worker thread)

```
budget = tokenBucket.Take(frameDt)                       // bytes allowed this frame
send all pending P0 (account it; P0 can drive the bucket negative)
shares = {P1: 0.40, P2: 0.45, P3: 0.15} of what is left    // work-conserving deficit round robin
P2: motion records for this frame's tick groups, sorted by priority
    (followed/selected > in-frustum near > in-frustum far > outside frustum);
    when over share: far tier at 1/2, then 1/4 rate (client extrapolates, R15 §2.6), then shrink the radius
P1: attention dirty bits in order: inspected closure > in-view V types > infoview-topic types of in-view owners
    > system-state deltas
P3: rolling cursor over all remaining dirty bits, class-weighted (V before A), then cell maps and water tiles
unused share flows to the next queue; leftover bytes carry over (bounded)
```

#### 4.7.4 Attention model

The client sends a `ViewReport` (unreliable, 10 Hz or on change >5 %, ~40 bytes): camera pivot x/z (2×f32), zoom
(f16), yaw (u16), active infoview id (u8) and infomode mask (u32), selected slot, followed slot, open-panel flags and the
panel's target slot (line panel, district panel, budget, statistics). The host derives, per client:

1. **Motion interest**: disc around the pivot, radius `clamp(k × zoom, 300 m, 2500 m)`, frustum footprint, hysteresis
   1.25 (R15 §7.1).
2. **In-view static set**: at 1 Hz or after a large camera move, a query of the static object and net quad trees
   (`Game.Objects.SearchSystem.GetStaticSearchTree`, `Game.Net.SearchSystem.GetNetSearchTree`; iteration API public in
   `Colossal.Collections/Colossal.Collections/UnsafeQuadTree.cs:379` [V]) → a per-client bitset over slots. Changes of
   V-class types on these slots are P1 for that client.
3. **Inspection closure**: the selected or followed entity's closure (citizen → household, creature, vehicle,
   workplace, school, home; vehicle → layout members, passengers, dispatch requests, owner; line → route, waypoints,
   segments, route vehicles; R15 §8.4), depth 1-2, recomputed when a forward reference in it changes. These slots
   join the SDC hot set (scanned every frame) and their records are P1. Creatures in a closure are spawned even when
   outside motion interest.
4. **Infoview topics**: the infoview selects topic flags; each topic boosts types for owners in the in-view set:

| Topic (infoview / infomode) | Boosted data | Source of the mapping |
|---|---|---|
| Happiness, Health, Wellbeing, Education, Age | `Citizen` of residents of in-view buildings | `ObjectColorSystem.GetBuildingStatusColors` reads `Citizen` through `Renter` → `HouseholdCitizen` (`Game/Game.Rendering/ObjectColorSystem.cs:904-965` [V]) |
| Wealth | `Household` + household `Resources` of residents | `ObjectColorSystem.cs:876-903` [V] |
| Profitability | `CompanyStatisticData` of renters | `ObjectColorSystem.cs:993-1017` [V] |
| Crime, mail, garbage, electricity, water consumption | `CrimeProducer`, `MailProducer`, `GarbageProducer`, `ElectricityConsumer`, `WaterConsumer` | `ObjectColorSystem.cs:850-875, 984-992, 1048, 1161` [V] |
| Traffic | `Road` view 1 (flow) of in-view edges | `Game/Game.Net/Road.cs` [V] |
| Service coverage (per service) | `ServiceCoverage` of in-view edges | R04 §4.1 |
| Pollution, land value, natural resources, groundwater, noise, telecom | the corresponding cell map (P1 delta), `LandValue` of in-view edges | R07 §8.2 |
| Electricity / water network | consumer/producer components; flow-edge values by flow key (§3.7) | R03 §6.11 |
| Transport | `WaitingPassengers`, route vehicles of the open line | R15 §8.2 |

Unobserved state still converges: P3 drains every dirty bit eventually. With the default budget, the expected
staleness of unobserved agent state is 30-120 s of wall-clock time and of unobserved building state 4-20 s [E]; both
are reported live in the network HUD (§11.1).

#### 4.7.5 Degradation order under a small budget

1. Far-field motion rate halves, then quarters.
2. Motion radius shrinks towards 300 m.
3. P3 share shrinks to 5 % (staleness grows; nothing is lost).
4. Water correction tiles pause.
5. Cell-map deltas are coalesced to one per map per minute.
6. P1 is never starved below 30 % of the remainder after P0.

Structure, existence, events and economy (P0) are never degraded.


---

## 5. Client action path

A client never commits shared state. Three capture mechanisms turn local actions into intents; the host validates,
executes with vanilla code or the shared applier, and the result comes back through normal replication.

### 5.1 Tool applies: predict, capture, commit, reconcile (R10 §7.3)

**Client.**

1. `ToolCaptureApplySystem` (first in `ApplyTool`, which only runs on apply frames) records a `ToolActionHeader`:
   client sequence number, tool id, prefab rank, Σcost computed exactly as `ToolApplySystem` does
   (`ToolApplySystem.cs:42-74`), and for each `Temp` its original `(slot, revision)` and `TempFlags`.
2. The vanilla apply runs locally as the **prediction**. The player gets exact vanilla behaviour, including splits,
   upgrades and the tool state machine continuing from real entities (R10 §7.3 step 2).
3. `ToolCaptureResultSystem` (first in `Modification1`) collects the entities tagged `Applied` (+`Created`/`Updated`) and
   the deleted originals. It encodes them with the **same Core `RecordEncoder`** the host uses: new entities get local
   ids `L1..Ln` instead of slots, changed originals are `VALUE` records by slot, deleted originals are `DESPAWN` by slot,
   zone `Cell` edits become block overlays. Predicted entities are remembered in a client table
   `PredictedEntities: localId → Entity` (no marker component, so no structural change).
4. The `ToolChangeSet` intent is sent on lane R (P0). Typical size: 0.2-3 KB; a 500-object Move It operation
   ~30-100 KB before LZ4 [E].

**Host** (`HostIngressSystem`, MainLoop right after `EndFrameBarrier`, before `ToolSystem`):

1. Validate: role permission; every referenced original exists with the stated revision (otherwise reject as a
   conflict); prefabs unlocked (`Locked` bit) unless unlock-all; Σcost ≤ money unless unlimited (the client's Σcost is
   checked against prefab cost data within a tolerance; exact recomputation without running `Generate` is an open
   question, R10 §8.1); build area / owned tiles.
2. Commit with the shared `WorldApplier` (the same code clients use, R02 §9.1): create from prefab archetypes, write
   components, tag `Created`/`Updated`/`Applied` and the closure, subtract the cost from `PlayerMoney`, set `Recent`,
   queue the `TriggerAction` that `Apply*` would have produced (R10 §7.3 step 2).
3. The host owns the `BuildOrder` counter: committed edges get host values (`BuildOrder` drives node-lane order and zone
   block priority, R13 §2.4, §4.1); corrected values go back to the originator.
4. The same frame's `HostCaptureSystem` assigns slots through EDC. The originator receives
   `IntentResult{seq, Accepted, [(localId → slot, gen)]}` in that epoch. **Echo suppression**: for the originator, the
   epoch replaces the `SPAWN` payloads of its own entities by `BIND(localId → slot)` records plus `VALUE`s only for
   components the host changed (the originator's copy of that epoch is encoded separately; everyone else shares one
   encoding).

**Originator reconcile.** Accepted: bind predicted entities into the side tables. Rejected: add `Deleted` to the
predicted creations and request repair of the touched originals; the host re-sends them from its shadow (upsert keeps
their slots), so a predicted split is undone exactly.

**Other host-side behaviour.** The host player's own tool is never disturbed: the host does not touch the `Temp`
namespace; a remote deletion of an original under the host's preview is handled by vanilla `OriginalDeletedSystem`
(R10 §7.3, R02 §9.1). Definition replay is not used (R13 §12).

**Optional no-predict gate** (spectators, permission denied): a system right before `ToolOutputSystem` rewrites the
active tool's `applyMode` from `Apply` to `Clear` through reflection on the protected setter (R10 §7.4).

**Terrain.** Each apply frame of the terrain tool yields `Temp+Brush` stamps; capture A streams them as
`TerrainStroke` intents (~48 B per stamp). The client's own apply is a prediction that the host's tiles overwrite
after the stroke settles (R07 §5.2).

### 5.2 Local change capture: direct-write tools and mods

Many tools never use `Temp`/`Apply` (Move It, Tree Controller brush, Better Bulldozer, Zoning Toolkit, Water
Features, the Anarchy components tool, the vanilla service-district selection tool, map-tile purchase; R09 §5.10,
R10 §7.7). Instead of an intent type per tool, the client uses one generic mechanism that exploits the follower
property: **on a client nothing but our applier and the local player should change replicated state.**

`LocalChangeCaptureSystem` runs before `PrepareCleanUpSystem` and inspects:

1. slot-bearing entities with `Created`/`Updated`/`Deleted`/`Applied` or an archetype change that were **not**
   produced by `ClientApplySystem` this frame, or whose authoritative values no longer match the applied-hash ledger
   (§8.3);
2. unbound entities in slot-bearing classes (mod tools that create entities without `Created`);
3. the `SelectedEntityWatch`: the selected entity and the target of the open panel, whose player-authorable types are
   compared with the ledger every frame. Many UI handlers write the selected entity directly without any tag:
   Historical flag, vehicle models, service districts, mesh colours (R10 §3.5-3.6).

Changes on **player-authorable** types become a `LocalEditChangeSet` intent with the same format as tool change-sets.
Changes on other types are treated as rogue local writes: logged with the type name (diagnostics) and repaired from the
host. Move It drags produce a change per frame, so local edits are coalesced per entity and sent at most at 10 Hz while
the tool is active; the final state is always sent. Derived systems that tag closures without changing authoritative
values (for example `SubElementDeleteSystem` touching neighbours) do not produce intents because the ledger comparison
finds no difference.

### 5.3 UI intents

| Action family | Vanilla mechanism (R10 §2-3) | Client capture | Intent | Host execution (`HostCommandSystem`, UIUpdate) |
|---|---|---|---|---|
| Policies, out-of-service, emptying, ticket price, vehicle count, schedules, line state (11 actions) | `Event+Modify` via `EndFrameBarrier` (`PoliciesUISystem.cs:237-242`) | `ClientApplySystem` step 1 sees local `Modify` entities before `ModifiedSystem` (Mod4) and destroys them | `PolicyIntent{target slot, policy rank, active, adjustment}` | `PoliciesUISystem.SetPolicy(target, …)` |
| Delete district, upgrade, line | `Deleted` via `EndFrameBarrier` | step 1 sees `Deleted` on a slot-bearing entity not ordered by the host → removes it | `DeleteIntent{slot, revision}` | `Deleted` via `EndFrameBarrier` |
| Dev-tree purchase | `DevTreeSystem.Purchase`: points written directly + `Unlock` event (R14 §3) | step 1 sees a local `Unlock` for a dev-tree node → destroys it, restores `DevTreePoints` from the ledger | `DevNodeIntent{prefab rank}` | `DevTreeSystem.Purchase(node)` |
| Taxes (base, area, resource) | direct writes to `TaxSystem.m_TaxRates` | `InputDiffWatcher` compares the 92-int array every frame with the last applied one | `TaxIntent{full array}` (clamping is order-dependent, R10 §3.3) | write array after `Readers.Complete()`, run clamps through the base-rate setter |
| Service budget | `CityServiceBudgetSystem.SetServiceBudget` | diff of the `ServiceBudgetData` singleton buffer | `BudgetIntent{service rank, percent}` | same call |
| Service fees | `ServiceFeeSystem.SetFee` (no tag) | diff of the City `ServiceFee` buffer | `FeeIntent{resource, fee}` | same call |
| Loan | `LoanSystem.ChangeLoan` queue (Mod1) | `LoanSystem` stays enabled on clients (prediction); diff of `Loan` | `LoanIntent{amount}` | `ChangeLoan(amount)`; absolute host values overwrite |
| Map tiles | `MapTilePurchaseSystem.PurchaseSelection` removes `Native` + `Updated` | local change capture (map tiles are authorable) | `TilePurchaseIntent{slots}` | reimplemented purchase: cost check, money, `UnlockTile` (R10 §3.4) |
| Rename entity, line, city | `NameSystem.SetCustomName`, `CityConfigurationSystem.cityName` | 1 Hz diff of `m_Names` (reflection) and `cityName` | `NameIntent{slot, string}` | same calls |
| Route colour, mesh colour, Historical, vehicle models, service districts | direct component/buffer writes (some via `EndFrameBarrier`) | local change capture / `SelectedEntityWatch` | `LocalEditChangeSet` | shared applier + `Updated`/`BatchesUpdated` as the vanilla handler does |
| Follow citizen | `LifePathEventSystem.FollowCitizen` adds `Followed` | local change capture | `FollowIntent{slot, on}` | same call (shared 50-entry limit, R14 §5.4) |
| Simulation speed / pause | `TimeUISystem` writes `selectedSpeed` | `SpeedMirrorSystem` sees a change that is not the forced-pause barrier (barrier observer count read by reflection, R05 §9.2) | `SpeedIntent{index, paused}` | session policy: host only / any player / majority |
| Chirp likes, tutorials, infoviews, camera, radio | local | — | — | — |

Results come back as replicated state; `IntentResult{seq, status, reason}` drives a toast on rejection. Host execution
of UI intents happens in `UIUpdate` because those APIs create `EndFrameBarrier` command buffers, which throw at
MainLoop start (R10 §0.4).

### 5.4 Permissions, conflicts, abuse limits

Roles per player (Host, Co-mayor, Builder, Viewer) map to intent classes (structure, economy, policies, speed,
destructive mod tools). Conflicts are detected by slot revisions; the host processes intents in arrival order and
rejects stale ones. Limits: ≤50 intents per second per player, change-sets ≤4 MB, violations logged.

### 5.5 Latency

| Event | Visible to the actor | Visible to others |
|---|---|---|
| Tool apply, local edit, UI change | immediately (prediction) | RTT/2 to host + ≤1 host frame + RTT/2 to the client |
| Rejection | snap back after one RTT | nothing |
| Host player's own edit | immediately | ≤1 host frame + RTT/2 |

---

## 6. Follower simulation on clients

### 6.1 Gating mechanism (`GateController`)

1. After every load on a client (`OnGameLoadingComplete(LoadGame, Game)`), enumerate the private
   `UpdateSystem.m_Systems` list (`List<SystemData>` with `m_Phase` and `m_System`; `Game/Game/UpdateSystem.cs` [V]) by
   reflection.
2. In `GameSimulation`, `EditorSimulation` and `LoadSimulation`, **disable every system except the cosmetic
   whitelist**: `TimeSystem`, `ClimateSystem`, `SnowSystem`, `WindSimulationSystem`, `WindSystem`,
   `ZoneAmbienceSystem` + `SpawnableAmbienceSystem`, `TrafficAmbienceSystem`, `WetnessSystem`, `DirtynessSystem`,
   `StreetLightSystem` (R11 §3, §8.2) and `GameModeGovernmentSubsidiesSystem` (a pure function of replicated money and
   expenses whose `Enabled` flag the budget UI reads, R14 §10.2). This disables every mod's simulation systems
   generically (Water Features, Tree Controller's seasonal system, Plop's spawn tagger, ...).
3. Outside the simulation phases, disable the declared list of R11 §8.1 (initialisers, event command consumers, XP,
   milestones, dev tree, requirement systems, `ZoneCheckSystem`, `ServiceRequestSystem`, `UpgradeDeletedSystem`,
   `RandomLocalizationInitializeSystem`, `DefaultPoliciesSystem`, `EventJournal*`, `AddMeetingSystem`, `AutoSaveSystem`,
   ...), the moving-object list of R15 §10.3 (`ObjectEmergeSystem`, `GroupSystem`, `FixParkingLocationSystem`,
   `FixLaneObjectsSystem`, `TripResetSystem`, `PathOwnerTargetMovedSystem`, `BoardingVehicleSystem`), `TriggerSystem`
   with every unguarded producer (option A of R14 §5.5), `UnlockAllSystem`, `GroundHeightSystem` (§6.5) and the
   entries contributed by mod adapters (§10).
4. **Producer/consumer self-check**: a declared table of queue pairs (R11 §4) is verified at session start; a disabled
   consumer with an enabled producer is reported and the producer is disabled too.
5. **Re-assertion**: several systems re-enable themselves in `OnGamePreload`/`OnGameLoadingComplete` (R11 §8.3). The
   gate table is re-applied after every load and checked every 60 frames (a few hundred boolean reads).
6. The table is declarative and versioned. A game update that renames a system makes the startup check fail loudly
   (diagnostics + refusal to host/join) instead of silently running a gameplay system on clients.
7. `GateController` records each system's previous `Enabled` value and restores it when the session ends. A client
   always leaves a session by returning to the main menu (the city it was showing is not its own).

### 6.2 Clock follower without Harmony

During loading `SimulationSystem` runs unchanged (its loading path, `SimulationSystem.cs:189-219` [V]). After loading,
the client sets `SimulationSystem.Enabled = false` (nothing in the game reads that flag; grep [V]) and
`ClientClockSystem` (LateUpdate, before `SimulationSystem`) does the stepping itself:

```
target   = hostFrameEstimate(nowLocal) − D                    // from Clock anchors (hostFrameIndex, hostTime, rate)
ticks    = clamp(target − frameIndex, 0, 16)                   // catch up at most 16 ticks per frame
if (target − frameIndex > 600) hardJump(target)                // via the private setter; logged
updateSystem.Update(SystemUpdatePhase.PreSimulation)
for i in 0..ticks-1:  frameIndex++ (cached private-setter delegate);
                      updateSystem.Update(SystemUpdatePhase.GameSimulation, frameIndex, i)   // public, UpdateSystem.cs:223 [V]
updateSystem.Update(SystemUpdatePhase.PostSimulation)          // WaterSystem ECS part, as vanilla (SimulationSystem.cs:272-295 [V])
frameTime   = fractional part of target;  smoothSpeed = EMA of the host's effective speed   (private setters)
```

- Ticks stay contiguous, so whitelisted interval systems fire on the same ticks as on the host (R11 §7.1).
- Before any later load on the client (snapshot resync, leaving to the main menu) `LifecycleSystem` re-enables
  `SimulationSystem` so that its own loading logic runs.
- `D` (clock lag) = `clamp(ceil((RTT/2 + 2σ_jitter) × 60 × speed) + 8, 8, 64)` ticks (R15 §2.6), ≈0.1-0.25 s.
- `SpeedMirrorSystem` writes the host speed into `selectedSpeed` before `RenderingSystem` every frame, so forced local
  pauses (focus loss barrier, error dialogs, lightning, photo mode) cannot freeze interpolation (R11 §7.3).
- On the host, `SpeedAuthoritySystem` (LateUpdate, before `SimulationSystem`) enforces the session speed policy
  against the same local pause sources when the host enables "pause menu does not pause the session".

### 6.3 What clients still compute

| Derivation | Inputs that are replicated | Code |
|---|---|---|
| Net geometry, compositions, lanes, zone blocks, secondary lanes/objects, sub-objects, aggregates, flow-graph topology, district membership, road connections, search trees | structure (S class), `PseudoRandomSeed`, `BuildOrder`, policies, renters | Modification1-5 (R02 §6, R11 §2.5-2.8) |
| Date, time of day, climate curves, sun and lighting, wind visuals, snow and wetness visuals, street lights | clock, `TimeData`, climate prefab and overrides | whitelisted simulation systems (R11 §3) |
| Traffic-light lane signals and pole states | node `TrafficLights` (6 B) | `TrafficLightDeriveSystem` calls `TrafficLightSystem.UpdateLaneSignal(TrafficLights, ref LaneSignal)` for the node's signalled sub-lanes and `UpdateTrafficLightState(TrafficLights, ref TrafficLight)` for its pole sub-objects (`TrafficLightSystem.cs:782-975` [V]); group masks come from the client's own `TrafficLightInitializationSystem` (`TrafficLightInitializationSystem.cs:263-595` [V]) |
| Infoview colours, notification icon rendering, clusters | replicated component values, icons | `ObjectColorSystem`, `IconClusterSystem` |
| Map-tile permits | milestone `Locked` bits | `MapTilePurchaseSystem.GetAvailableTiles` (R14 §1.3) |
| Government subsidy line | money, expenses | `GameModeGovernmentSubsidiesSystem` |
| Modifiers of created entities | `Policy` | `*ModifierInitializeSystem` (Mod4) |
| Chirp links and name caches, event journal list | replicated chirps and journal entities | `ChirpLinkSystem`, client journal companion (R14 §6.1) |
| Vehicle/creature reference buffers, `LaneObject` | forward references | `Vehicles/Creatures.ReferencesSystem` (R15 §4.5) |
| Water visuals | sources, parameters, sea level | GPU `WaterSystem`, corrected by tiles |
| Tool previews, validation, coverage previews, route path previews | world state | vanilla tools (local `Temp` only) |

### 6.4 No double application

1. Rewards are never computed on clients: XP, milestone money and creditworthiness, dev points, unlock-all rewards
   (R14 §1.5). State arrives as absolute values; replicated money, XP and points are written, never added.
2. The UI-refresh events that vanilla needs are synthesised from state deltas only: `Event+Unlock` for newly unlocked
   prefab ranks (vanilla `UnlockSystem.ProcessEvents` flips the bit idempotently, R14 §2.4) and
   `Event+MilestoneReachedEvent` for each newly reached level (R14 §1.5). `Modify` events are synthesised only for
   district and route policy changes (idempotent, §2.4).
3. Command events (`Damage`, `Destroy`, `Ignite`, `Impact`, `AddHealthProblem`, `ResetTrip`, `PathTargetMoved`, ...)
   are never replicated; their consumers are disabled (R11 §2.4, §2.7).
4. Initialisers that append or randomise host-owned fields are disabled, so spawn payloads are written once and stay
   (R11 §2.11). Kept initialisers only overwrite client-local or prefab-derived fields; the three exceptions are
   re-applied from the spawn stash (§4.6.6).
5. `MilestoneSystem` is disabled; its cached `lastRequiredXP/nextRequiredXP/progress` fields, which the milestone UI
   reads (`MilestoneUISystem.cs:388-396`), are written from replicated host values by the reflection codec.
6. XP toasts and simulation Transaction icons are replicated as events and fed to `MilestoneUISystem.AddMessage` and
   the icon buffer (R14 §1.5, §4.5).

### 6.5 Housekeeping

- **`GroundHeightSystem`**: disabled on clients. Its private `m_ReadyUpdates` list, fed by `TerrainSystem`, would
  otherwise only grow (`GroundHeightSystem.cs:1071-1155, 1203-1286` [V]); `GroundHeightDrain` clears it each frame
  through a cached `FieldInfo`. Height corrections after terrain edits are computed on the host and arrive as
  `Transform`/`Node`/`Curve` deltas (the host's `GroundHeightSystem` tags them `Updated`, so EDC captures them).
- `IconCommandSystem` stays enabled (its queues leak otherwise, R11 §4 Q8).
- Autosave is off on clients; manual client saves are allowed and labelled "copy of a co-op session"; "save a copy of
  the host's city" downloads a host-authored snapshot (R12 §6.2).
- Event-driven tutorials do not fire on clients (`TriggerSystem` off). On the host, an unfinished intro tutorial would
  block milestones for everyone (R14 §1.5): starting a session offers to mark the intro as completed.

---

## 7. Join flow

### 7.1 Sequence

| # | Host | Client |
|---|---|---|
| 1 | listens (Steam P2P / direct IP); lobby data `st.proto`, `st.game`, `st.mods` digest, `st.ping` (R06 §10) | finds the session (friends' lobbies, invite, rich-presence `connect`, or IP) |
| 2 | `Hello`: protocol, mod build hash, **digests** of: game version (`m_Version` + `fullVersion`), `FormatTags` names, replicated type schema, code mods + UI modules (+versions, platform ids), sorted `PrefabID` set, content prerequisites, sim-relevant session settings, locale | same |
| 3 | on any digest mismatch: exchange the full list for that category only | shows a readable report: missing/extra/different-version mods, missing DLC/content, prefab and type differences; offers PDX "subscribe all" for missing mods (R06 §8) |
| 4 | admits, then enqueues the snapshot on the `"SaveLoadGame"` task queue (or reuses a cached one, §7.3) | "Waiting for host snapshot" |
| 5 | snapshot frame: `HostCaptureSystem` closes epoch **E** (slot sweep included), then `SaveGameSystem.RunOnce` writes to a temp file (R12 §4.3); joiner tracking starts: ring cursor E+1, a joiner dirty bitset recording every shadow change after E; broadcast "host is preparing a snapshot" to everyone | |
| 6 | streams the manifest (snapshot id, E, tick, size, XxHash128, slot-set digest, game mode, climate/render prefab ids) and 256 KiB chunks on lane B (window 2-8 MiB, resumable) | writes chunks straight into a `SaveGameData` asset of a file-backed transient database (R12 §4.3); verifies chunk and blob hashes |
| 7 | keeps simulating; the joiner's backlog accumulates | sets load overrides (`overrideLoadedOptions = false`, `overrideThemeName = null`, `GameModeSystem.overrideMode` = host mode, `pausedAfterLoading = true` in memory, achievements off; R12 §4.2), disables mod load-time mutators (§10), arms the mod-only-entity pre-load sweep (R12 §3), enqueues `GameManager.Load(GameMode.Game, Purpose.LoadGame, saveGameData)` |
| 8 | | Deserialize: `NetIdSystem` binds slots; `PostDeserialize` validates |
| 9 | | `OnGameLoadingComplete`: restore settings, apply gating, disable `SimulationSystem`, start `ClientClockSystem` at the snapshot tick, validate (placeholder prefabs, slot-set digest, per-class counts) |
| 10 | sends ring epochs E+1..head, then the joiner's dirty bits (P3 share temporarily raised to 50 % for this peer) | applies merged epochs in bounded batches ("catching up 0-100 %") |
| 11 | runs a **post-join verification sweep**: hash beacons of all buckets for this peer at high rate (~1-2 min) | repairs mismatches (§8) |
| 12 | marks the client Live when its backlog is below two epochs and ≥95 % of its "dirty since E" set is drained | tools and intents unlocked (before Live the player can watch but not act) |

### 7.2 Why the snapshot boundary is exact enough

`HostCaptureSystem` runs immediately before `SaveGameSystem` in the same MainLoop (§4.3.1), so every entity created up
to that point has a slot and every structural change up to that point is in epoch ≤E. Changes made later in the frame
(UI handlers after MainLoop, the simulation in LateUpdate) land in E+1 and are not in the snapshot. Value changes that
the host's scanner had not yet seen at E are delivered through the joiner's dirty bitset, and the rare value that
changed and changed back before a scan is caught by the post-join sweep. Because every operation is an idempotent
upsert (§4.6.3), an overlap between snapshot and backlog is harmless.

### 7.3 Snapshot cost, caching and multiple joiners

- The Serialize phase is a host stall (synchronous GPU readbacks in `WaterSystem.Serialize`, R12 §6.1); everyone sees a
  "host is preparing a snapshot" banner and timeouts are suspended. Compression and the file write continue
  asynchronously (`SaveGameSystem.cs:69-77`).
- **Snapshot cache**: a snapshot is reused for any joiner arriving within 120 s or while the ring since E is below
  64 MB. Each joiner gets its own dirty bitset since E and its own ring cursor; only one stall is paid.
- At most two concurrent snapshot transfers (protects the host upload); further joiners queue.
- The ring holds P0 content only (structure, existence, events), so catch-up volume stays small even after minutes of
  transfer; values come from the shadow at send time.

### 7.4 Mismatch detection

| Check | Source | Rule |
|---|---|---|
| Protocol, mod build | constants | exact |
| Game build | `Game.Version.current` (`m_Version`, `fullVersion`; `Version.Equals` only compares `m_Version`, R12 §7) | both fields equal |
| Format tags | `FormatTags` names (42) | equal list |
| Type schema | sorted `(StableTypeHash, sizes, offsets, flags)` (R08 §5, R12 §7) | equal digest (raw-byte replication requires identical layouts) |
| Code mods and UI modules | `ModManager.ListModsEnabled()` + versions + platform ids (R05 §1.5) | equal sets, except a host-approved list of client-optional UI-only mods (§10.2) |
| Content | `GameManager.ArePrerequisitesMet(referencedContent)` (R12 §4.3, §7) | pass |
| Prefab set | digest of sorted `PrefabID`s; referenced prefab ids resolve with `TryGetPrefab` | equal digest preferred; referenced set must resolve; duplicate ids reported |
| Climate / water-render / terrain-render prefabs | `ClimateSystem.currentClimate`, `WaterRenderSystem`, `TerrainMaterialSystem` (R12 §4.3) | must resolve on the client; v1 refuses with a clear message if they came from a save package |
| Game mode | `GameModeSystem.currentModeName` | applied as `overrideMode`, verified after load |
| Sim-relevant mod settings | session-settings adapters (§10.3) | host values pushed to clients |
| Locale | active locale id | warning only (random names depend on locale counts, R13 §7.3) |

Digests first, details only on mismatch: a matching handshake costs a few hundred bytes.

### 7.5 Fast rejoin and resync without reloading

- **Short drop** (≤5 min, client still in the city): the host keeps the peer's dirty bitsets alive during a grace
  period, plus a short per-peer log of the `(slot, type)` pairs it sent in recent epochs. On reconnect the client
  reports its last applied epoch; if it is still in the ring, the host re-marks as dirty every pair sent after that
  epoch (they may have been lost with the connection), replays ring epochs from there and continues with the
  preserved dirty bits: an exact resume with no snapshot. If the send log no longer reaches back far enough, a bucket
  sweep (§8.4) finds what is missing.
- **Longer drop or detected divergence**: hash-based resync (§8.5). Host and client compare bucket hashes for every
  class at a high beacon rate and repair only mismatching buckets: cost proportional to the divergence, not to the city
  size.
- **Snapshot resync** only when the client left the city, when structural mismatches exceed a threshold (default 5 % of
  S-class buckets), or after repeated repair failures.
- **Host leaves**: live host migration is infeasible (R12 §8.3). The host can push its latest session save to clients
  in idle bulk bandwidth (at most every 30 minutes) so any player can restart the session from it.


---

## 8. Divergence detection and repair

Goal: prove that every client holds what the host sent, find local divergence, and repair it at the smallest possible
granularity, with cost proportional to the divergence rather than to the city size.

### 8.1 Member hashes and normalisation

`h(slot, typeRank) = XXH64(normalised authoritative words, seed = typeRank)` (`System.IO.Hashing` ships in the game's
Managed folder, R06 §6). Normalisation:

- entity fields → canonical `NetRef` integers (slot, prefab rank, null, local); lane fields → canonical PLK;
- only words in the policy's authoritative mask (derived fields and padding are never hashed);
- heights of terrain-snapped types (`Transform.y`, `Node.y`, `Curve` control-point y, area node y) quantised to 1/8 m,
  because GPU rasterisation of the terrain cascade may differ by one LSB (0.0625 m) between machines (R07 §5.2,
  R11 risk R7);
- floats otherwise compared bitwise (host-authoritative copies are exact);
- buffers: length + normalised elements; enabled bits included;
- existence member per slot: `mix(slot, gen, archetypeId, class)`;
- reverse buffers are never hashed (their order differs between peers, R04 §5).

### 8.2 Host bucket sums (incremental)

Per class, bucket = `slot >> 10` (1024 slots). Bucket sum = `Σ mix64(h)` over members, addition modulo 2⁶⁴, which is
commutative, so chunk iteration order is irrelevant. Every shadow write subtracts the old member hash and adds the new
one: **O(1) per change, no scanning**. Memory: ~1k buckets × 6 classes × 8 bytes for 1 M slots.

### 8.3 Client hash ledger and rolling live verification

- `HashLedger` stores the member hash of the last *applied* value per `(slot, typeRank)` (32-bit truncated, ~12 MB
  for 3 M pairs [E]) and maintains the same bucket sums. It proves "received = sent".
- **Rolling live verification**: each frame, within ~0.3-0.5 ms, the client re-hashes a slice of live ECS data
  (S class first, then in-view V, then the rest) and compares it with the ledger. A mismatch means a local write:
  local change capture turns it into an intent if the type is player-authorable (§5.2); otherwise it is a rogue write,
  logged by type and repaired from the host. A full cycle over everything takes ~10-20 minutes [E]; S-class and
  in-view state are covered every ~30-60 s.

### 8.4 Beacon protocol

The host emits `HASH{class, bucket, sum, count}` beacons inside epochs on lane R, so a beacon is ordered after the
records it covers. A beacon for bucket *b* is emitted to peer *p* only when *p* has no dirty bits in *b* (a check over
the bucket's 16 bitset words per type), otherwise the bucket is skipped this round. Default rate: 32 buckets per second
per peer (~0.5 KB/s); during a post-join sweep or hash resync up to 1024 buckets per second. The client compares the
beacon with its ledger bucket sum after applying everything before it.

### 8.5 Repair ladder

| Level | Trigger | Action | Cost |
|---|---|---|---|
| 1. Bucket detail | bucket sum mismatch | host sends per-slot member hashes of the bucket (≤1024 × ~6 bytes compressed); client lists the differing slots | ~6 KB |
| 2. Entity re-send | differing slots | host marks those `(slot, type)` dirty for the peer at P1; missing entities get `SPAWN`; entities absent on the host are answered with "not present" and the client adds `Deleted` | per entity |
| 3. Class resync | >10 % of a class's buckets mismatch, or a codec reports a suspect class | the class is re-sent through dirty bits (icons, prefab bits, zone overlays, a component class) | class size |
| 4. Snapshot resync | structural mismatch >5 % of S buckets, repeated failure, or structural apply exceptions | §7 join path with "Resyncing" overlay | snapshot |

Repair traffic is accounted in the P1 share.

### 8.6 Special domains

| Domain | Verification | Repair |
|---|---|---|
| System state blobs | 64-bit hash in each delta; checked after apply | keyframe |
| Prefab unlock bitset | full-bitset hash every 30 s; tutorial-family prefabs and forced UI groups excluded (a client may legitimately be ahead, R14 §2.4) | bit list; relock through the R14 §2.4 step 4 trick |
| Icons | per-owner icon-set hash as a member of the owner's bucket | remove-all + add for that owner |
| Zone cells | per-block overlay hash, bucketed by owner edge slot | overlay re-send |
| Terrain heightmap | per-64×64-tile hash of `m_Heightmap` on both sides (async readback) after edits and every 5 minutes | bit-exact tile re-send |
| Water | none (visual by design) | periodic correction tiles |
| Cell maps | hash in each delta | keyframe |
| Derived families (lanes, sub-objects, blocks) | diagnostic mode only: per-owner set hash `(prefab, quantised geometry)` | tag the owner `Updated` on the client (regenerate); log |

### 8.7 Safe-state policy

- An exception while applying a class marks it suspect, skips it, logs a diagnostics record and triggers level 3.
- An exception in a structural transaction pauses that client's intents, applies nothing further from the transaction,
  and requests level 3 for class S; a second failure escalates to level 4.
- The network HUD shows the current level ("Repairing 12 entities", "Resyncing city"); nothing throws into the game
  loop.

---

## 9. Transport

### 9.1 Interfaces and implementations

Core defines `ITransport` (listen, connect, poll, close, events), `IConnection` (send with lane and reliability,
real-time statistics) and `ILinkStats`. Implementations:

| Implementation | Assembly | Purpose |
|---|---|---|
| `SteamSocketsTransport` | Game | primary: `SteamNetworkingSockets` v012 P2P by SteamID (`ConnectP2P`, `CreateListenSocketP2P`, one poll group on the host), Steam Datagram Relay and ICE (R06 §5.2) |
| `SteamIpTransport` | Game | direct connections through the same API (`CreateListenSocketIP`, `ConnectByIPAddress`, `IP_AllowWithoutAuth`), encrypted but unauthenticated, so protected by a session password challenge (§9.6) |
| `LoopbackTransport` | Core | in-process pairs for unit tests |
| `NetSimTransport` | Core | wraps any transport with loss, latency, jitter, reordering and bandwidth caps for tests |
| `SteamSocketPairTransport` | Game | `CreateSocketPair` for the in-game single-machine self-test (§12.3) |
| `UdpTransport` (later) | Core | managed reliability over `System.Net.Sockets` for non-Steam stores; needs an external relay or port forwarding (R06 §5.7) |

### 9.2 Lanes

Three lanes per connection, configured with a custom P/Invoke because the Steamworks.NET binding of
`ConfigureConnectionLanes` is broken for more than one lane (flat exports
`SteamAPI_SteamNetworkingSockets_SteamAPI_v012`, `SteamAPI_ISteamNetworkingSockets_ConfigureConnectionLanes`;
R06 §5.6):

| Lane | Content | Reliability | Priority / weight |
|---|---|---|---|
| R (0) | control, intents, epochs (P0/P1/P3 values), hash beacons | reliable ordered | priority 0, weight 1 |
| U (1) | motion, clock, view reports, cursors | unreliable, no delay | priority 0, weight 1 |
| B (2) | snapshots, repair dumps, resume-point saves, large tile bursts | reliable | priority 1 (only leftover bandwidth) |

Fallback if lanes are unavailable: a second connection on virtual port 1 for bulk (R06 §5.3 B).

### 9.3 Packetisation

- One reliable epoch message per peer per frame (typically 1-30 KB, LZ4), split at 256 KiB (message cap 512 KiB,
  `Constants.cs:253`); sent with `k_nSteamNetworkingSend_ReliableNoNagle` because batching is done by us.
- Motion datagrams ≤1200 bytes (one MTU), each independently decodable, sent with
  `k_nSteamNetworkingSend_UnreliableNoDelay` (dropped rather than queued when the link is saturated; the next frame's
  data supersedes them).
- `SendMessageToConnection` only (the `SendMessages` binding passes structs by value, R06 §5.6); message fields read
  through a blittable mirror struct to avoid `Marshal.PtrToStructure` allocations (R06 §5.6 item 4).

### 9.4 Blob transfer (`BlobSender` / `BlobReceiver`, Core)

Manifest on lane R; chunks on lane B as `{u32 blobId, u32 index, u32 length, u64 XxHash64, payload ≤256 KiB}`.
Sender window: keep `m_cbPendingReliable` between 2 and 8 MiB (R06 §5.4). Receiver writes each chunk at
`index × chunkSize` into a preallocated file, never holding the blob in managed memory. After a reconnect the receiver
requests missing ranges. Final XxHash128 check. `SendBufferSize` is raised to 8-16 MiB (on the bulk connection when
the two-connection fallback is used).

### 9.5 Congestion and budget

`LinkEstimator` (§4.7.2) feeds each peer's token bucket from Steam's estimated send rate and queue time. Steam's own
`SendRateMin/Max` are read with `GetConfigValue` at startup and logged (the defaults of this `steam_api64` build are an
open question, R06 §12), then set per connection from the session's upload budget. Built-in `FakePacketLoss/Lag/
Reorder` config values are exposed in a developer menu for chaos testing (R06 §4.2).

### 9.6 Encryption and authentication

SNS connections are authenticated with Steam-issued certificates and encrypted end to end by default (R06 §5.2); the
`Unencrypted` option is never set. The peer's SteamID (`m_identityPeer`) is the player identity; the host checks it
against the lobby membership or an invite list. Direct IP connections are encrypted but unauthenticated, so the
handshake adds an HMAC-SHA256 challenge over a nonce with the session password (`HMACSHA256` is available in this Mono,
R06 §6).

### 9.7 NAT traversal

P2P by SteamID goes through SDR relays or ICE automatically; users never forward ports (R06 §5.2). Relay status from
`GetRelayNetworkStatus` is shown in the HUD. Direct IP covers LAN and port-forwarded hosts.

### 9.8 Threading

- **Network thread** (background): `ReceiveMessagesOnPollGroup`, release messages, decode envelopes into pooled
  buffers, push to per-kind `ConcurrentQueue`s; drain outbound queues; pump the bulk window.
- **Codec workers** (1-2 threads, Core): epoch merge and decode, record encoding, LZ4, hashing of the shadow, motion
  encoding.
- **Main thread**: ECS reads and writes only (capture, apply, gating, clock). No `EntityManager` access off the main
  thread: safety checks are compiled out, so violations would corrupt silently (R06 §7).
- Steam callbacks (connection status, lobby) arrive on the main thread through the game's `RunCallbacks` pump
  (R06 §2.3) and are forwarded into the session state machines.
- No per-message allocations (pooled byte arrays and unmanaged blocks), because the game uses incremental GC with 3 ms
  slices (R06 §1).

### 9.9 Lobby, invites, presence, timeouts

- Steam lobby (`FriendsOnly`, `Private` or `Public`) with data `st.proto`, `st.game`, `st.mods`, `st.ping`, `st.city`
  (R06 §10).
- Rich-presence keys `connect` and `steam_player_group` (the game overwrites `steam_display` every 5 s, R06 §4.8).
- Invites through the overlay; `GameLobbyJoinRequested_t` and `GameRichPresenceJoinRequested_t` handled; cold start
  through `+connect_lobby` on the command line (launcher pass-through to verify, R06 §12).
- `TimeoutConnected` raised to 60 s while a host snapshot stall is announced; the clock stream doubles as an
  application heartbeat.

---

## 10. Mod compatibility

### 10.1 Generic mechanisms (cover any mod)

1. **Save-query coverage**: every mod `ISerializable`/`IEmptySerializable` component is replicated with the default
   policy, including on entities without `PrefabRef` (R09 §5.1). Entity fields inside mod components are remapped
   through `TypeManager` offsets (Better Bulldozer, Traffic; R09 §5.2).
2. **Enabled bits, archetype changes and in-place `PrefabRef` swaps** are first-class records (Tree Controller,
   R09 §5.3).
3. **Atomic transactions at the single apply point**, before every ModificationEnd enforcer and Modification4B mod
   system (R09 §5.4, R11 §6).
4. **Phase-wide gating** disables every mod's `GameSimulation` systems on clients (§6.1).
5. **No capture of local churn**: `Updated`/`BatchesUpdated`/`Highlighted` alone never cause sends; value comparison
   does (R09 §5.6).
6. **Local change capture** turns direct writes by mod tools on clients into intents (§5.2).
7. **Load-time mutator suppression** on joiners plus a host-authoritative world from the snapshot (R09 §5.7).
8. **Session settings channel** for simulation-relevant mod settings (R09 §5.8).
9. **Parity by handshake** for prefab-level mutations and mod versions (R09 §5.9; checks listed in §7.4).
10. **Unknown-writer diagnostics**: rolling verification reports rogue writes per component type; a cheap per-frame
    change-version check on columns the applier did not write can name the type early, so a missing adapter is easy to
    identify from a user's log.

### 10.2 The mods the user runs

| Mod | What it does to the world (R09) | Generic coverage | Adapter work |
|---|---|---|---|
| Move It | direct writes to `Transform`, `Node`, `Curve`, area nodes, elevations + `Updated` | local change capture (coalesced to 10 Hz); `MIT_ControlPoint` / `PrefabRef(Null)` entities excluded | none required |
| Better Bulldozer | direct deletion of sub-elements, owner buffer + record entities with Entity refs, auto-removal and frame-counted deletion systems | local change capture + unbound-entity sweep; remap of its buffers | gate `RemoveRegeneratedSubelementPrefabsSystem`, `AutomaticallyRemove*`, `HandleDeleteInXFramesSystem` on clients; include deleted derived sub-objects as sub-object keys in the intent; settings sync for auto-removal toggles |
| Line Tool | vanilla `Temp`/`Apply` pipeline with time-varying seeds | tool capture ships resolved results, so seed re-rolls do not matter | none |
| Tree Controller | brush direct writes (`Tree`, `PrefabRef` swaps, `Tree` add/remove, `Decoration` bit), Rendering-phase bit toggles, seasonal simulation | local change capture with archetype and enabled-bit records; simulation systems gated generically | gate `LumberAndPauseTreeGrowthSystem` and `DestroyFoliageSystem` on clients; settings sync (`DisableTreeGrowth`, `UseDeadModelDuringWinter`, `FreeVegetation`) |
| Water Features | sources without `PrefabRef`, host-only feedback simulations on GPU water, global water parameters, per-city settings in serialised systems | sources replicated via the save query; simulations gated generically | system-state codecs for its serialised systems and the vanilla `WaterSystem` parameters; its `BeforeSerializeSystem` writes rest heights into snapshots (R09 §2.9), so the host re-sends live `WaterSourceData` right after a join |
| Zoning Toolkit | `ZoningInfo` on edges; block resizing at Modification4B on every peer | `ZoningInfo` travels in the edge transaction, so the client's own system derives the same blocks from the replicated value (R09 §2.10 option i) | verification only |
| Plop the Growables | tag components; enforcement replacing vanilla zone checks; settings buttons that mutate many buildings | tags included atomically in spawn bundles | gate `PloppedBuildingSystem`, `SelectiveZoneCheckSystem` on clients; suppress `ExistingBuildingSystem` on joiners; settings buttons host-only |
| 529 Tiles | load-time unlock/relock driven by local settings; upkeep patch | tile `Native` replicated; budget computed on the host | suppress the `PostDeserialize<FiveTwentyNineSystem>` runner on joiners (disable the generic runner instance before load); settings sync for UI parity |
| Historical Start | load-time unlock events and requirement rewrites | unlock bits through the prefab-state channel | suppress `HistoricalStartSystem` on joiners; settings sync so requirement displays match |
| Anarchy | own components, enforcement systems, prefab mutations, Harmony patches | components generic; `Transform` and `TransformRecord` live in the same chunk, so one capture emits both (R09 §2.1) | gate `PreventOverrideSystem`, `RemoveOverridenSystem`, `ResetTransformSystem`, `SetRetainingWallSegmentElevationSystem`, `PreventCullingSystem` on clients; suppress its `OnGameLoadingComplete` cleanup on joiners; settings sync (unique buildings, clearance). Client tool previews keep Anarchy's relaxed validation; the host commits the resulting state |
| Traffic | replaces vanilla `LaneSystem`; data components with Entity refs | data generic + remap; lanes derived locally by its own lane system | verification adapter for its `ModificationDataSyncSystem` on clients (R09 §6) |
| Road Speed Adjuster | unknown, probably lane or composition data (no source) | generic if it uses components | inspect the DLL before release (R09 §6) |
| Find It, Extended Tooltip, I18N Everywhere, Unified Icon Library, Asset Icon Library, Skyve, Toggleable Overlays, Employment Tracker, Cim Census | local UI and tools that activate vanilla tools | nothing to replicate; their tag noise is never captured | none; the host may whitelist them as client-optional (requirement 6 stays strict by default) |
| China Pack (assets) | content prefabs | prefab digest and referenced-prefab resolution (§7.4) | none |

Mod parity is required for all world-changing mods: same set, same versions, same simulation-relevant settings
(pushed from the host).

### 10.3 Adapter interface

```csharp
public interface IModAdapter {
    string ModAssemblyName { get; }                      // matched against loaded code mods; no compile-time reference
    bool IsPresent(ModContext ctx);
    IEnumerable<SystemRef> ClientGate();                 // systems disabled on clients (resolved by type name)
    IEnumerable<SystemRef> LoadTimeMutators();           // systems disabled on a joiner during the snapshot load
    IEnumerable<TypePolicyOverride> Policies();          // classes, masks, authorable flags for its components
    ISessionSettingsCodec Settings { get; }              // simulation-relevant ModSetting properties (reflection)
    IEnumerable<ISystemStateCodec> SystemState { get; }  // serialised mod systems
    IEnumerable<string> LegitimateDirectWriteTools();    // tools whose local writes are intents, not rogue writes
}
```

Adapters use reflection only, so an absent mod costs nothing and a mod update that renames a type disables just that
adapter entry with a logged warning.

---

## 11. UI and UX

### 11.1 Surfaces

- **Main menu** (`moduleRegistry.append("Menu", ...)`, R05 §7.3): a "Co-op" entry opening a panel with
  - *Host*: pick a save (C# enumerates save metadata), session name, privacy (friends, private, public), password for
    direct IP, max players, default roles, upload budget, motion quality;
  - *Join*: friends' sessions from the Steam lobby list filtered by `st.proto`, with a ping estimate from `st.ping`;
    direct IP, port and password;
  - *Settings*: local preferences.
- **In-game HUD** (`append("Game", ...)`):
  - a compact strip: session badge, player avatars with ping dots, current speed owner;
  - an expandable player list: name, role, ping, link quality, in/out KB/s, staleness, role toggles and kick for the
    host;
  - chat: toggle hotkey; a focused text input masks keyboard actions automatically (R05 §7.4); system lines for joins,
    leaves, rejections;
  - network panel: per-stream KB/s, budget, queue time, epochs behind, apply and scan milliseconds, rolling staleness
    p50/p95 per class, hash health, last repair;
  - host session panel: roles, speed policy, budget, client-optional mods.
- **Toasts** through `NotificationSystem.Push` with progress states: join progress, resync, host saving (R05 §8.1).
- **Pause menu**: "Leave session" / "End session" via `moduleRegistry.extend` once the module path is found with
  `-uiDeveloperMode` (R05 §14); the HUD carries the same buttons as a fallback.
- **Optional presence**: other players' cursors and camera markers at 5 Hz (~12 B each) and "player X is using the Road
  tool" labels.

### 11.2 Session badge states

`Connecting` → `Handshake` (mismatch report on failure) → `Downloading city NN %` → `Loading` → `Catching up NN %` →
`Live`; side states `Host saving`, `Repairing N`, `Resyncing city`, `Lagging` (backlog >2 s), `Disconnected
(reconnecting)`. Colours: green (Live), yellow (catching up, repairing, lagging), red (resync, disconnected).

### 11.3 How the UI is built

- **One hand-written ES module** `SkylineTogether.mjs`, no bundler. Its first block comment carries the required
  header (`Cities: Skylines II UI Module`, `Id`, `Author`, `Version`, `Dependencies`; `UIModuleAsset.cs:27-110`,
  R05 §7.3). The game exposes React and the `cs2/*` APIs as window globals: community builds map `react` to
  `window.React` and `cs2/api` to `window["cs2/api"]` with `externalsType: "window"`
  (`thirdparty/Anarchy/Anarchy/UI/webpack.config.js:31-43` [V]). The module therefore uses `React.createElement`
  (no JSX), `bindValue`/`useValue`/`trigger` from `window["cs2/api"]`, and plain elements with inline styles
  (`cs2/ui` components when available). This keeps the build free of a Node toolchain.
- **C#**: `SessionUISystem : UISystemBase` at `UIUpdate`, binding group `"skylineTogether"`: `GetterValueBinding`s
  (status, speed owner, budget), `RawValueBinding`s with patches (players, chat log, network statistics),
  `TriggerBinding`s (host, join, leave, sendChat, kick, setRole, requestResync, setSessionSetting), an `EventBinding`
  (incoming chat) and `CallBinding`s for validation (R05 §7.1-7.2). Updates are diff-based.
- **Localisation**: `MemorySource` per locale through `LocalizationManager.AddSource` (R05 §6).
- **Settings**: `SkylineTogetherSettings : ModSetting` holds local preferences only (unique class name, R05 §5.3).
  Session settings live in the host panel and are replicated; nothing simulation-relevant is read from a client's
  settings file (R05 §5.4).


---

## 12. Code structure and milestones

### 12.1 Repository layout

```
SkylineTogether/
  src/
    SkylineTogether.Core/        netstandard2.0, no Unity or game references, no external packages
      Protocol/                  MessageKind, Envelope, ByteWriter/ByteReader (pooled, varint, zigzag, bits), records, NetRef
      Identity/                  SlotAllocator, SlotTable, PrefabIndex, TypeIndexTable, ArchetypeDictionary
      Replication/Schema/        TypeSchema, TypePolicy, TypeView, PolicyTable, ReplicationClass, AttentionTopic
      Replication/Shadow/        ShadowStore, UnmanagedPages, BufferArena
      Replication/Dirty/         PeerDirtySet, PagedBitset
      Replication/Epochs/        EpochBuilder, Transaction, EpochLog (ring), ApplyPlanMerger (client coalescing)
      Replication/Codecs/        FullWordsCodec, BufferCodec, QuantizedCodecs, XorRle, LaneKeyCodec, ZoneOverlayCodec
      Replication/Scheduling/    PeerScheduler, TokenBucket, LinkEstimator, AttentionSets, RollingCursor
      Replication/Motion/        MotionFrameCodec (Q-A/Q-B), InterestSlotTable, MotionStager, ClockModel
      Integrity/                 Xxh64 (managed), MemberHasher, BucketSums, HashLedger, BeaconPlanner, RepairCoordinator
      Session/                   SessionHost, SessionClient, Handshake, JoinCoordinator, PermissionTable, ChatLog, SessionSettings
      Transport/                 ITransport, IConnection, LoopbackTransport, NetSimTransport, BlobSender, BlobReceiver
      Abstractions/              ICompressor, IReplicaWorld, IClockSource, ILog
    SkylineTogether.Game/        net472 against the game's Managed DLLs (Private=false), unsafe enabled
      SkylineTogetherMod.cs      IMod entry (no instance field initialisers: GetUninitializedObject, R05 §1.3)
      Systems/Host/              HostIngressSystem, HostMotionSamplerSystem, HostCaptureSystem, HostCommandSystem, SpeedAuthoritySystem
      Systems/Client/            ClientApplySystem, ToolCaptureApplySystem, ToolCaptureResultSystem, AggregateReconcileSystem,
                                 TrafficLightDeriveSystem, LocalChangeCaptureSystem, SpeedMirrorSystem, MotionWriterSystem,
                                 ClientClockSystem, GroundHeightDrain
      Systems/Common/            NetIdSystem, SessionUISystem, GateController, LifecycleSystem, DiagnosticsSystem
      Ecs/                       SchemaDiscovery, ArchetypeClassifier, ChunkScanner, RawComponentReader/Writer, EntityRemapper,
                                 WorldApplier, StructuralBatch, TagRules, PendingRefs, PrefabRankBuilder, ZoneTypeMap,
                                 LaneKeyResolver, BlockKeyResolver, SubObjectKeyResolver, InterestQueries, ReflectionContract
      Codecs/                    GameSerializableCodec, CityServiceBudgetCodec, MilestoneCacheCodec, StatisticsCodec, ClimateCodec,
                                 PrefabStateChannel, IconChannel, NameChannel, CellMapChannel, TerrainTileChannel, WaterTileChannel
      Join/                      SnapshotService, SnapshotLoader, PreloadSweep, LoadMutatorSuppressor, ResumePointService
      Transport/                 SteamSocketsTransport, SteamLanes (P/Invoke), SteamIpTransport, SteamSocketPairTransport,
                                 SteamLobbyService, RichPresenceService
      Compat/                    IModAdapter, adapters for the mods of §10.2, SessionSettingsSync
      Diagnostics/               InGameSelfTest, DiagnosticsDump, NetLog
    SkylineTogether.UI/
      SkylineTogether.mjs        hand-written ES module (header comment, React.createElement, window["cs2/api"])
  tests/
    SkylineTogether.Core.Tests/  net8.0 test project: codec golden vectors, FakeWorld, NetSim, property tests
  tools/
    PolicyTemplateGen/           lists the game's ISerializable component types and fields (System.Reflection.Metadata)
                                 to author the curated policy table offline
    EpochReplay/                 replays epoch streams recorded in game against FakeWorld for debugging
```

Core has no dependency on Unity, Steam or the game. Everything that touches ECS, Steam, GPU or the game's systems lives
in Game. `IReplicaWorld` is the seam: the Game layer implements it with ECS; tests implement it with `FakeWorld`
(entities as byte arrays per type view), so the replication engine is tested end to end outside Unity
(requirement 3).

### 12.2 Main types and responsibilities

| Type | Assembly | Responsibility |
|---|---|---|
| `SlotAllocator`, `SlotTable` | Core | NetSlot allocation (deferred reuse), slot ↔ index tables, revisions |
| `PrefabIndex`, `TypeIndexTable` | Core | ranks from sorted identity lists, 128-bit digests, fallback tables |
| `PolicyTable` | Core | curated per-type policies + generic defaults; versioned |
| `ShadowStore` | Core | captured values per (slot, view) in unmanaged pages; buffer arena |
| `PeerDirtySet` | Core | per-peer paged bitsets; coalescing |
| `EpochBuilder`, `EpochLog` | Core | per-frame P0 content, transactions, ring for catch-up and rejoin |
| `PeerScheduler`, `TokenBucket`, `LinkEstimator` | Core | budgets and priorities P0-P3; congestion response |
| `AttentionSets` | Core | per-peer in-view, inspection and topic bitsets from view reports |
| `RecordEncoder` / `RecordDecoder` | Core | wire records, NetRefs, codecs, LZ4 through `ICompressor` |
| `ApplyPlanMerger` | Core | merges epochs into ordered structure + coalesced values for the client apply |
| `MotionFrameCodec`, `MotionStager`, `ClockModel` | Core | Q-B frames, staging rings and extrapolation maths, host-clock estimation and lag D |
| `MemberHasher`, `BucketSums`, `HashLedger`, `RepairCoordinator` | Core | integrity and repair ladder |
| `SessionHost`, `SessionClient`, `Handshake`, `JoinCoordinator` | Core | session state machines, digests and reports, snapshot cache and joiner tracking |
| `BlobSender` / `BlobReceiver` | Core | chunked, windowed, resumable transfers |
| `NetIdSystem` | Game | persistence of slot assignments in saves |
| `HostCaptureSystem` (+`ChunkScanner`, EDC, channels) | Game | everything the host sends, captured at one point in the frame |
| `HostIngressSystem`, `HostCommandSystem` | Game | validation and execution of intents |
| `ClientApplySystem` (+`WorldApplier`, `StructuralBatch`, `TagRules`) | Game | the single client apply point |
| `LocalChangeCaptureSystem` | Game | local edits → intents; rolling verification |
| `GateController`, `ClientClockSystem`, `SpeedMirrorSystem` | Game | follower mode |
| `HostMotionSamplerSystem`, `MotionWriterSystem` | Game | moving objects |
| `SnapshotService`, `SnapshotLoader` | Game | join snapshots and loading |
| `SteamSocketsTransport`, `SteamLanes`, `SteamLobbyService` | Game | transport and discovery |
| `IModAdapter` implementations | Game | per-mod gate lists, policies, settings and codecs |
| `ReflectionContract` | Game | validates every reflection target at startup and disables dependent features with a clear message |
| `InGameSelfTest`, `DiagnosticsDump` | Game | single-machine verification and support data |

### 12.3 Testing strategy (nobody can run the game during development)

1. **Core unit tests**: codec golden vectors and fuzzed round trips; slot reuse rules; dirty-set coalescing;
   scheduler budget adherence and fairness; epoch merge semantics; idempotent apply on `FakeWorld`; bucket repair
   convergence under injected divergence; blob transfer under loss and reordering (`NetSimTransport`); handshake
   reports; clock model under jitter.
2. **World fuzz (property tests)**: random spawn/despawn/value/archetype operations on a host `FakeWorld`, transport
   through `NetSimTransport` with a bandwidth cap, a client `FakeWorld` at the other end. Assertions: bucket sums
   converge within a bounded time after the operations stop; bytes per second stay within the budget; no unbounded
   queue growth.
3. **Compile-time checks** of the Game layer against the real DLLs; `ReflectionContract` checks at runtime.
4. **In-game single-machine self-test** (the game is single-instance, R06 §1): host mode with an internal "shadow
   client" that decodes the host's own epoch stream into a verification store and compares member hashes with live
   ECS data, which exercises schema discovery, classification, capture and remapping without a second PC; plus a
   loopback session over `SteamSocketPairTransport` that exercises the transport and blob paths.
5. **Diagnostics dump** (one JSON + log zip): counts per class and archetype, schema and policy digests, gate table
   state, slot statistics, budgets, staleness, hash health, last 1000 log lines. This is what the user sends back after
   each in-game test.

### 12.4 Milestones

| Milestone | Content | Exit criteria (checked in game by the user) |
|---|---|---|
| M0 Foundations | solution, build against game DLLs, logging, diagnostics dump, settings, UI stub (menu entry, HUD with fake data), `ReflectionContract` | mod loads, HUD shows, dump written |
| M1 Transport and session | Steam P2P with lanes, lobby/invite, handshake digests and reports, chat, ping, direct IP | two players connected; chat works; a deliberate mod mismatch produces a readable report |
| M2 Join and follower | `NetIdSystem`, snapshot via `SaveGameSystem`, blob transfer, client load path, gating, clock follower, speed mirror and authority, pre-load sweep, load-time mutator suppression | client sees the host's city as of the join; clocks in sync; client money/XP/population do not move on their own for 10 minutes; no leaks |
| M3 Structure | schema discovery, classifier, EDC, transactions, client apply and tag rules, prefab ranks, zone overlays, aggregate reconciliation, names, policies | host-built roads, buildings, zones, routes and districts appear identically on clients; bulldozing and upgrades work; S-class beacons agree |
| M4 Client actions | tool capture/predict/commit/reconcile, local change capture, UI intents, permissions, terrain strokes and tiles | a client builds, zones, terraforms, sets taxes/budget/policies; conflicts rejected cleanly; Move It works from a client |
| M5 Simulation state | `ChunkScanner`, shadow, dirty sets, scheduler and budget, attention, agent existence, system-state codecs, prefab state and synthesised progression events, icons, events, chirps, journal, statistics, budget outputs | money, demand, statistics and milestones identical; info panels show identical numbers; infoviews correct in view; bandwidth within budget |
| M6 Moving objects | existence stream, motion sampler, Q-B, staging and JIT writer, extrapolation, effects, selection support, creature interest scoping, traffic-light derivation | traffic identical near the camera at speeds 1-4 without backward snaps; sirens and lights correct; vehicles clickable |
| M7 Fields and mods | cell maps, water sources, parameters and tiles, terrain verification, adapters for the listed mods, session settings sync | the user's mod list works in a two-player session |
| M8 Integrity and resilience | full bucket hashing for every class, repair ladder, fast rejoin, snapshot cache and multiple joiners, resume-point saves, safe-state policy, watchdogs | injected divergences repaired automatically; a dropped client resumes without reloading; three players join one after another |
| M9 Scale tuning | measurement-driven cadences and budgets, optional word-delta for hot types, optional Harmony quality pack (icon command filter, client tutorial triggers), optional managed UDP transport | a 100k city with four players over relay stays within budget and responsive |

Harmony is not needed for M0-M8. Every mechanism above uses public APIs, reflection on private fields and properties,
or system enable flags. The optional pack in M9 would bundle `0Harmony.dll` only if a measured benefit justifies it:
filtering non-replicated icon commands on clients (R14 §4.5) and muting chirp/life-path outputs so that client-side
tutorial triggers can stay on (R14 §5.5 option B).

---

## 13. Risks, mitigations and first in-game verifications

### 13.1 Risks

| # | Risk | Impact | Mitigation |
|---|---|---|---|
| 1 | Relay throughput lower or more variable than assumed | laggy motion, slow joins | budget auto-tuning from Steam statistics; quality scaling (§4.7.5); join ETA shown; direct IP option |
| 2 | Host snapshot stall of seconds in big cities (synchronous GPU readbacks in Serialize, R12 §6.1) | hitch for everyone at each join | announced banner, suspended timeouts, snapshot cache shared by concurrent joiners; measured in M2 |
| 3 | Raw-byte layouts differ between peers | corrupted state | schema digest in the handshake (refuse); same build and mod set are mandatory anyway |
| 4 | Entity indices hidden in non-`Entity` fields (`PathNode`, `FixedList`, `fixed`, pointers) | garbage references | startup audit (R08 §5); dedicated lane-key codec; unknown hidden-ref types excluded from raw replication |
| 5 | Gate table misses a gameplay writer outside `GameSimulation` (vanilla or mod) | client drift, possible double effects | producer/consumer self-check; rolling verification names the type; adapters; strict mod parity |
| 6 | Kept initialisers overwrite replicated fields on creation | wrong values for a frame or permanently | spawn stash re-apply (§4.6.6); self-test compares post-spawn hashes |
| 7 | Archetype classification error (prefab sub-object rule) | duplicates or missing objects | per-rule counts in the dump; duplicate-child check after upgrades; rule overridable per prefab |
| 8 | Node-lane order and traffic-light group masks not identical across peers (R13 §2.4) | wrong lane keys, wrong lights | PLK with geometric fallback; group-mask layout hash in diagnostics; fallback: replicate pole states for in-view junctions |
| 9 | Local change capture misreads a derived write as a player edit | bogus intents | ledger comparison on authoritative words only; authorable flags; host validation; logging |
| 10 | Private members used by reflection change in a game patch (`UpdateSystem.m_Systems`, `SimulationSystem` setters, `CityServiceBudgetSystem` arrays, `NameSystem.m_Names`, `GroundHeightSystem.m_ReadyUpdates`, `TimeUISystem` barrier, `ContextFormat.EnableTag`) | feature failure | `ReflectionContract` validates at startup and disables just the dependent feature with a readable message (for example: budget outputs fall back to keeping `CityServiceBudgetSystem` running on clients) |
| 11 | Steamworks.NET or SDK change breaks the lanes P/Invoke | no lanes | export check at startup; second-connection fallback (R06 §5.3) |
| 12 | Water visuals diverge between correction tiles; Water Features feedback loops | visual differences | gameplay from water is host-only; correction tiles; documented expectation |
| 13 | Huge atomic transactions (a 100k-entity Move It) | multi-frame spike on clients | accepted (rare); progress overlay for transactions above a size threshold |
| 14 | Interest-scoped creatures break a UI path that expects the creature to exist | UI exception | inspection closures include creatures; `CurrentTransport` null is handled by vanilla checks (`CitizenUIUtils.cs:244` uses `HasComponent` [V]); setting to fall back to global creature existence |
| 15 | Host main-thread cost higher than estimated in Mono | lower host frame rate | budgets with adaptive cadences; measurement in M5 |
| 16 | Host memory (shadow 30-80 MB, ring 128 MB) | pressure on low-RAM hosts | configurable ring size with disk spill; shadow pages allocated on use |
| 17 | Climate/water-render/terrain-render prefab from a save package missing on clients (R12 §4.3) | `[Missing]` placeholders | handshake check; v1 refuses with a clear message |
| 18 | A residual double application of rewards somewhere | money/XP drift on clients | G-class hashes detect it within seconds; absolute values overwrite; audit through the dump |
| 19 | Host alt-tab stalls the simulation (`Application.runInBackground` unknown, R06 §12) | everyone pauses | verify in M1; document; HUD shows "host paused (window inactive)" |
| 20 | A game update changes system ordering or names | silent misbehaviour | declarative versioned gate and order tables; startup check refuses hosting on an unknown build until tables are confirmed |

### 13.2 Verify first, in this order

| # | Check | How | Expected | Fallback if not |
|---|---|---|---|---|
| 1 | Reflection contract and schema discovery | load the mod, open the dump | all contract entries green; replicated type count ≈ serializable types on replicated archetypes | fix targets before anything else |
| 2 | Identity digests stable across machines | same mods on two PCs, compare `StableTypeHash` and sorted-`PrefabID` digests | equal | explicit tables (§3.5) |
| 3 | `NetIdSystem` round trip | host save with the block, reload, check bound slot count; load without the mod | all bound; clean load without the mod | self-alias / `AssemblyVersion` fix (R12 §1.5) |
| 4 | Snapshot and load path | host snapshot via `SaveGameSystem` into a temp file; client loads through a transient DB and the public `Load` with the overrides | loads; no first-frame local ticks; game mode correct; Serialize duration and file size logged | reflection on the private `Load` overload (R01 §10.3) |
| 5 | Gating completeness | client in follower mode without replication for 10 minutes | money, XP, population, demand, unlocks unchanged; memory stable; no exceptions | extend gate table; producer/consumer pairs |
| 6 | Classification audit | dump per-rule counts; upgrade a building on the host | no duplicated sub-objects on the client; player-placed attached objects keep slots | adjust the sub-object rule |
| 7 | Clock follower | `SimulationSystem` disabled after load; manual phase driving | time, day/night, interpolation and speed changes behave like vanilla | Harmony prefix on `SimulationSystem.OnUpdate` (R11 §7.1) |
| 8 | Apply point and tags | host places, upgrades and deletes roads and buildings with sub-nets | client geometry, lanes, blocks identical; no dangling sub-nets; no duplicate sub-nets after upgrade deletion | revisit tag rules (R11 §6.3) |
| 9 | `Applied` + host `UpdateFrame` on spawn | dump `UpdateFrame` of spawned vehicles on both sides | equal | second `SetSharedComponent` after Modification5 (R15 §4.4) |
| 10 | Raw write + remap | in-game self-test hash comparison over a sample of every replicated type | all equal | per-type custom codec |
| 11 | Traffic-light group masks | per-junction group-mask layout hash host vs client | equal | replicate pole states for in-view junctions |
| 12 | Motion | speeds 1 and 4 with 100-200 ms latency (Steam fake lag) | smooth, no backward snaps, lights and sirens correct | tune D and staging depth; reduce far field |
| 13 | Transport | lanes P/Invoke; bulk throughput over relay; RTT/jitter distribution | lanes active; throughput logged | second connection for bulk |
| 14 | Local change capture | Move It and Tree Controller brush on a client | edits appear on host and other clients; no rogue-write warnings during normal play | adapter entries |
| 15 | Measurement run | 10-minute "measure" command in a large city (§13.3) | numbers for every [E] value | retune cadences and budgets |

### 13.3 Measurement plan

The `measure` command of `DiagnosticsSystem` records for ten minutes: entity and chunk counts per class and archetype;
EDC events per second by kind; SDC changed entities per second per class and the fraction of chunks whose versions
changed per cycle; agent spawn/despawn rates; mover counts and interest-set sizes per zoom level; bytes per second per
stream and per peer; host capture and client apply milliseconds per frame (p50/p95); snapshot Serialize time, file size
and transfer throughput. These numbers replace the estimates in §0.4, §1.1 and §4.3.2 and set the defaults for
cadences, budget shares, the interest radius factor `k` and the clock lag `D`.

---

## Appendix A. Frame order summary

**Host** (game registrations from `Game/Game.Common/SystemOrder.cs:47-77` via R02 §1.3, R11 §1):

| Order | System | Note |
|---|---|---|
| MainLoop, UpdateBefore | `UpdateWorldTimeSystem`, `PathfindQueueSystem`, `EndFrameBarrier` | vanilla; playback of last frame's simulation commands |
| right after `EndFrameBarrier` | `HostIngressSystem`, then `HostMotionSamplerSystem` | `UpdateAfter<HostIngressSystem, EndFrameBarrier>`, `UpdateAfter<HostMotionSamplerSystem, HostIngressSystem>` |
| MainLoop, UpdateAt | `RaycastSystem` … `ToolSystem` (PreTool/ToolUpdate/PostTool) … `ModificationSystem` (Mod1…ModEnd) … `UnlockSystem`, `AllowBarrier<EndFrameBarrier>`, `PreRenderSystem`, pathfinding, `UIUpdateSystem`, `RenderingSystem` | vanilla |
| before `SaveGameSystem` | `HostCaptureSystem` | `UpdateBefore<HostCaptureSystem, SaveGameSystem>` |
| MainLoop, UpdateAt | `SaveGameSystem` (snapshots) | vanilla |
| MainLoop, UpdateAfter | `PrepareCleanUpSystem` | vanilla |
| UIUpdate | `HostCommandSystem`, `SessionUISystem` | `UpdateAt` |
| Cleanup | `CleanUpSystem` etc. | vanilla |
| LateUpdate | `SpeedAuthoritySystem`, then `SimulationSystem` (0-8 ticks) | `UpdateBefore<SpeedAuthoritySystem, SimulationSystem>` |
| worker threads | encoding, LZ4, scheduling, hashing, sockets | Core |

**Client**:

| Order | System | Note |
|---|---|---|
| MainLoop, UpdateBefore | `EndFrameBarrier` | local UI commands of the last frame |
| right after `EndFrameBarrier` | `ClientApplySystem` | `UpdateAfter<ClientApplySystem, EndFrameBarrier>` |
| PreTool / ToolUpdate / ApplyTool / PostTool | vanilla tools (+ `ToolCaptureApplySystem` first in ApplyTool) | prediction |
| Modification1 first | `ToolCaptureResultSystem` | `UpdateBefore<…>(Modification1)` |
| Modification2B after `AggregateSystem` | `AggregateReconcileSystem` | |
| Modification4B after `TrafficLightInitializationSystem` | `TrafficLightDeriveSystem` | |
| MainLoop before `RenderingSystem` | `SpeedMirrorSystem` | |
| MainLoop before `PrepareCleanUpSystem` | `LocalChangeCaptureSystem`, `GroundHeightDrain` | |
| PreCulling before `PreCullingSystem` | `MotionWriterSystem` | |
| UIUpdate | `SessionUISystem` | |
| LateUpdate before `SimulationSystem` | `ClientClockSystem` (`SimulationSystem` itself disabled after load) | |

## Appendix B. Bandwidth model

- Motion: `bytes/s ≈ N_interest × 3.75 × speed × 11 B × (near fraction + far fraction × far rate)` (R15 §9.1-9.3).
  Example: 2.5k movers, 1/3 near, far at 1/2 rate, speed 1 → 2.5k × 3.75 × 11 × (0.33 + 0.67 × 0.5) ≈ 69 KB/s.
- Existence: `events/s × average record size`; vehicles ~100-200 events/s at speed 1 × ~25 B ≈ 3-5 KB/s (R15 §9.3,
  scaled down by interest-scoped creatures).
- Rolling refresh: `Σ_class (entities × fraction changed per cycle × average record) / period`, capped by the P3 share;
  e.g. households 45k × 0.9 × 60 B / 60 s ≈ 40 KB/s raw before LZ4, so the cap (default ~30-45 KB/s at a 300 KB/s
  budget) stretches the effective period instead of raising bandwidth.
- Attention: `in-view V entities × change fraction per scan × record / scan period` + closure records at the 256-tick
  cadence; e.g. 2k buildings × 0.2 × 40 B / 4 s ≈ 4 KB/s.
- Host upload ≈ Σ per-client totals (no multicast on P2P); P0 encoding is shared, P1-P3 encoding is per peer.

## Appendix C. Glossary

| Term | Meaning |
|---|---|
| NetSlot / NetId | dense host-assigned entity index / slot plus generation for persistence |
| Epoch | one host frame's output; unit of ordering, catch-up and acknowledgement |
| Transaction (TX) | the structural records of one epoch; applied atomically on clients |
| EDC | event-driven capture from the game's one-frame tags |
| SDC | scan-driven capture by the time-sliced chunk scanner |
| Shadow | host copy of every captured value, indexed by slot |
| Dirty bit | per peer and type view: "this peer lacks the latest value" |
| Attention | per-peer set of entities and types the player can currently observe |
| Rolling refresh | budgeted fair delivery of all remaining dirty values |
| PLK | portable lane key (owner slot + lane identity without entity indices) |
| Member hash / bucket sum | per-(slot, type) hash / additive sum per 1024 slots |
| Local change capture | client detection of local edits, converted into intents |

