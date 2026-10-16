# Sync coverage

This document answers one question for every piece of state Cities: Skylines II keeps: **does every
player see exactly the same, and how?** It has two parts:

1. **Runtime state the UI and the renderer read** (hand-written, below): totals that systems compute every frame,
   panel caches, the chirper, weather, time, events in progress, and every other thing a player can see that is not
   simply a saved component.
2. **Every persistent state of the game** (generated, at the end): all serializable components and buffers and all
   serializable systems of the decompiled `Game` assembly, each with its sync status. The list is extracted by
   `tools/sync_coverage.py`, exactly as the game's own serializer finds it (`ComponentSerializerLibrary`,
   `SystemSerializerLibrary`, the save query of `Game.Serialization/SerializerSystem.cs`), and classified by
   `tools/sync_coverage_rules.txt`.

```
python tools/sync_coverage.py --write docs/design/SYNC-COVERAGE.md   # regenerate the table
python tools/sync_coverage.py --check                                # exit 1 on unclassified items, gaps or stale rules
python tools/sync_coverage.py --list                                 # raw inventory (no rules)
```

Run `--check` after every game patch: a new component, buffer or system has no rule and fails the check, so nothing new
can slip through unaudited.

## How to read a status

Clients never run the gameplay simulation (`GameSimulation` systems are off, [follower clock](../ARCHITECTURE.md#follower-clock); only a cosmetic allow-list runs
with the follower clock). So every value a `GameSimulation` system writes must either come from the host or be computed
again on the client from host data. The statuses:

| Status | Meaning |
|---|---|
| `structure` | structure codec: the host captures entities tagged `Created`/`Updated`/`Deleted` and streams full records; clients apply and the vanilla pipeline re-derives everything else |
| `presence` | structure codec plus the **presence watch** (new): components the simulation adds or removes *without* an `Updated` tag are found by comparing component sets and re-sent |
| `scan` | structure value scan: values hashed every 0.25-1 s (lines, the City entity, and now events), changes re-sent |
| `values` | attention values (`AttentionValues`): simulation values of buildings, edges, areas and now nodes, by camera and selection priority |
| `keyed` | keyed state on derived entities (zone cells, station stops, sub-element deletions, flow status of the flow graphs) |
| `global` | `GlobalStateSync`: a system's own `ISerializable` state, a custom slice of state, or a typed channel (taxes, budgets, names, ...) |
| `actions` | `ActionSync` intents and the unlock set, or a client proposal channel |
| `statistics`, `cellmap`, `terrain`, `water`, `movers`, `icons` | the dedicated channels of those branches |
| `agents` | agent replication (`AgentSync`) |
| `clock` | the follower clock and what is a pure function of it (date, time of day, season, climate) |
| `derived` | computed identically on every peer from synced inputs |
| `snapshot` | arrives with the join snapshot and never changes during a session (or only with synced inputs) |
| `local` | per player by design |
| `transient` | one-frame, tool-time or render-time data |
| `hostonly` | bookkeeping only the host simulation reads (dispatch requests, seekers, cursors, AI inputs); every outcome a player can see is synced |
| `excluded` | irrelevant in a session (achievements, editor) |
| `gap` | not synced and not justified: **there are none** (the generator fails on any) |

## Gaps found and fixed by the sync audit

| # | What players saw before | Root cause | Fix |
|---|---|---|---|
| 1 | Fires, collapsed or damaged buildings, flooded and endangered buildings, accident sites, tornadoes and lightning, floods, tsunamis and every other event were invisible on clients | `Game.Events.Event` entities were not a replicated family; `IgniteSystem`, `DamageSystem`, `WaterDamageSystem`, `AccidentSiteSystem` and others add or remove `OnFire`, `Damaged`, `Flooded`, `InDanger`, `AccidentSite`, `FacingWeather` without an `Updated` tag, so the capture never saw them | Events, journal entries, chirps and life-path events are a **host-only structure family** (`FamilyClassifier.IsHostOnly`): captured, value-scanned, never proposed by clients, refused by the host's validation, left out of the integrity check. References to entities that do not replicate travel as null instead of dropping the component (`EntityCodec.LenientRefs`). The **presence watch** (`EventSync`, `PresenceTracker`) compares each structure entity's replicated component set with the set last sent: every `BatchesUpdated` entity each frame, the watched types every 0.25 s, everything else on a 0.25 ms/frame rolling pass. `Game.Events.InitializeSystem` is gated on clients (it would ignite, damage and pick random targets). Weather phenomena get their `HotspotFrame` buffer sized and fed every frame on clients (`EventInterpolateSystem` indexes it unchecked, an empty buffer would read out of bounds) |
| 2 | The chirper differed per player: clients got only chirps their own triggers made (weather, policies, tools) | chirps were not replicated and `CreateChirpSystem` ran on clients | chirps replicate from the host; `CreateChirpSystem` is gated and its queue emptied (`EventSync.DrainChirpQueue`); likes stay per player; sender and link names come from the host's `ChirpLinkSystem` cache (channel `Chirp.Links`), so chirps show names even while a citizen is not known locally |
| 3 | The event journal was empty or wrong on clients | journal entries were not replicated | part of fix 1; `EventJournalSystem` keeps the list and its callbacks on clients |
| 4 | Info panels froze at the join snapshot: happiness factors, production capacity, stored resources, company and worker counts, consumption, free properties and shelters, study positions, bicycle and parking counts, electricity and water production, consumption, import and export, garbage accumulation, fire hazard dry days | their writers are `GameSimulation` systems | each system's own saved state is mirrored as a blob (`GlobalStateSync.Panels`), paced to its update rate (`ChannelPacer`) |
| 5 | The production panel froze (consumption, resource usage, production chains) | `CityProductionStatisticSystem` is simulation; its full blob would append to a queue on clients forever | custom channel `Production.Statistic` with only the panel's tables, chains in a stable order |
| 6 | The budget panel's fee income (electricity, water, healthcare, education, garbage) froze | the income is computed (on clients too) from `ServiceFeeSystem.GetServiceFees()`, a cache of `CollectedCityServiceFeeData` buffers on *service prefabs*, which never replicate, refreshed only by the simulation | channel `Fees.Collected` writes the host's cache into the client's |
| 7 | Economy panels' trade worth, household and company wealth, workers, tourists, lodging stayed at the join snapshot | `BudgetSystem` is a `GameSimulation` system | it is a pure read of the replicated city statistics: added to the follower clock's allow-list |
| 8 | The post office panel's mail rates froze; hail never fell on clients | runtime fields of `MailAccumulationSystem` and `ClimateSystem.hail`/`rainbow`, written by simulation | channel `Runtime` |
| 9 | A placed signature building stayed in the clients' toolbars (and could be placed again) | `PlacedSignatureBuildingData` is added to the prefab by `XPBuiltSystem`, which is gated on clients | channel `Signatures` adds and removes it on the client's prefabs |
| 10 | Traffic lights never changed on clients (vehicles drove through red lights) | node `TrafficLights` were not in any value stream; lane signals and signal heads are written by the simulation | nodes join the attention values, hashed by phase (not by the ticking timer) so a junction is sent when its lights change; `SignalFollower` derives lane signals, signal heads and moveable bridges with the game's own public functions (`TrafficLightSystem.UpdateLaneSignal`, `UpdateTrafficLightState`, `UpdateMoveableBridge`) |
| 11 | Node wear and other node values froze | nodes were not attention candidates | part of fix 10 |
| 12 | Trees never grew on clients | tree growth changes `Tree` values with `BatchesUpdated` only; trees are not attention candidates | the presence watch re-sends a tree's values when it has `BatchesUpdated` with an unchanged component set; clients tag it `BatchesUpdated` so the mesh is chosen again |
| 13 | Following a citizen only worked for the player who clicked it | the followed list (50 citizens, life paths) is city state written by `LifePathEventSystem.FollowCitizen` | channel `Follow`: client deltas, the host follows through the vanilla API, agent replication brings `Followed` and the life path to everyone |
| 14 | The Electricity and Water infoviews coloured power lines, transformers, water and sewage pipes from the join snapshot's flows on clients (bottlenecks, disconnected lines and pipe pollution never changed) | flow values live on the derived flow graph (`ElectricityFlowEdge`, `WaterPipeEdge`), written only by the GameSimulation flow systems, and flow edges have no portable identity | The **[flow-status channel](../ARCHITECTURE.md#statistics-icons-and-flow-status)** (`FlowStatusSync`, `MessageKind.FlowStatus`). A flow edge is keyed by its start and end flow node, a flow node by its owner's net id and role (net node or edge, valve, building transformer/producer/consumer/charge/discharge node, source, sink; `FlowKey`). While a peer's view report has the Electricity or Water topic, the host runs a time-sliced pass over the graph (0.75 ms per frame, at most one start per second) and sends the edges the peer lacks or whose value changed significantly (flags and capacities exactly, flows by 1/64), in-view first, within a 16 KB/s per-peer budget; activation is a keyframe. Clients write the values into their own derived edges after completing the jobs that read or write the edge type, and while their own infoview has the topic, local passes re-apply cached values to edges the graph systems rebuilt. The kept graph systems only create edges and rewrite direction and capacity on building and road-connection updates (`ElectricityBuildingGraphSystem.CreateOrUpdateFlowEdge`, `ElectricityGraphUtils.TrySetFlowEdge`, water likewise), never flow or flags; the flow systems themselves are GameSimulation (off on clients; a warning is logged if one runs). Building consumer state (`ElectricityConsumer`, `WaterConsumer`) keeps coming from the attention values under the same topics |
| 15 | Roads, power lines and pipes with a utility connection would not have replicated at all | `ElectricityNodeConnection`/`WaterPipeNodeConnection` were flow-graph family markers, but the graph systems add them to the net nodes and edges themselves, so those nets classified as derived `FlowGraph` (rule 8 precedes the net rules) | they are no markers any more; all six flow-graph connection components (`*NodeConnection`, `*ValveConnection`, `*BuildingConnection`) are excluded from structure records (`TypeHandlers.Excluded`): they point at derived flow nodes, and as presence-only components a spawn would have added them with null references that the edge graph system takes for an existing flow node |
| 16 | A joining player saw airplanes hanging in mid-air that the host did not have (and planes the host had landed or removed stayed) | aircraft are movers like every vehicle, but motion streams only inside each client's interest (at most 1 km around the camera pivot); farther movers are pinned until a pose refresh that, with tens of thousands of movers, reaches each after minutes, and a plane is visible from anywhere on the map. Nothing ever removed a mover that a client had with an id the host no longer used (the orphan sweep at going live only removes movers without an id) | reach classes ([movers](../ARCHITECTURE.md#movers), `MotionReach`): aircraft stream everywhere, ships and mainline trains up to 4 km, at half rate beyond the client's own radius. The **mover roster** (`MoverRoster`, a pass over all ids about every half minute and a fast pass when a player's motion stream starts): clients remove movers the host does not have and ask for those they lack; the host despawns movers whose entity vanished without a `Deleted` tag |
| 17 | The integrity check re-sent households and whole buckets of agents all session long in an active city | the game moves citizens between households without update tags; the host promoted a move only when its rolling agent scan reached the citizen, which in a large city takes longer than the check's agent confirmation window (45 s), and a household's own member list was no key link | household member multisets are key links; a key watch over the chunk change versions of the membership components touches the agents that really changed, so moves stream within a frame or two ([agents](../ARCHITECTURE.md#agents)) |

## Runtime state the UI and the renderer read (not saved components)

| State | Who writes it | On clients | Status |
|---|---|---|---|
| Budget panel income and expense lines, totals | `CityServiceBudgetSystem` (ModificationEnd, runs on clients) | computed from synced inputs: tax rates (`Tax`), statistics (`StatisticsSync`), fees and loan (City scan), collected fees (fix 6), subsidies (`GameModeGovernmentSubsidiesSystem`, allow-list), map tile upkeep (map tiles as structure), service upkeep (buildings, budgets, employees via agents) | derived |
| Trade worth, wealth, workers, tourists, lodging | `BudgetSystem` | fix 7 | derived |
| Demand bars and their UI smoothing | demand systems; `CityInfoUISystem` | demand blobs; UI smoothing runs locally | global + derived |
| Notifications (icons) | many simulation systems | `IconSync`, icon authority filter | icons |
| Chirper feed, likes, names | `CreateChirpSystem`, `ChirpLikeCountSystem`, `ChirpLinkSystem` | fix 2 | scan + global |
| Event journal | `EventJournalSystem` | fix 3 | scan |
| City name | `CityConfigurationSystem` | channel `CityName` | global |
| Achievements | `AchievementTriggerSystem` | inert for any modded city | excluded |
| Date, time of day, day and night, sun, moon, lighting | `TimeSystem`, `PlanetarySystem`, `LightingSystem` | follower clock; `TimeData` from the snapshot | clock |
| Season, temperature, precipitation, clouds, fog, aurora, weather selection | `ClimateSystem` | allow-list; pure function of the replicated date (no randomness in `UpdateWeather`/`SelectRandomWeather`) | clock |
| Hail, rainbow | `WeatherPhenomenonSystem` | fix 8 | global |
| Snow cover, wind, wetness and dirt of objects | `SnowSystem`, wind systems, `WetnessSystem`, `DirtynessSystem` | simulated locally for visuals (documented fidelity exception) | local |
| Fires, collapses, disasters, floods, tsunamis, tornadoes, lightning, accidents, emergencies, evacuation | event systems | fix 1; water levels also via `WaterSync`; vehicles of the emergency services via movers | scan + presence |
| Traffic accidents | `AddAccidentSiteSystem`, `AccidentSiteSystem`, accident AIs | event entities, `AccidentSite` (presence), `InvolvedInAccident` (movers) | scan + presence + movers |
| Traffic lights | `TrafficLightSystem` | fix 10 | values + derived |
| Tourism | `TourismSystem`, attractiveness | City `Tourism` (scan), attractiveness values, statistics | scan + values |
| Trade and outside connections | trade and company systems | outside connections as structure; their resources as values; electricity and water trade blobs; trade statistics; `TradeSystem` balances are AI input | structure + values + global |
| Loans and creditworthiness | `LoanUpdateSystem` | City scan, channel `Loan` | scan + global |
| Service fees and budgets | UI, simulation | channels `Fee`, `Budget` | global |
| Map tile purchase state | `MapTilePurchaseSystem` | intent `PurchaseMapTiles`, tiles as structure (`Native`) | actions + structure |
| Unlocks, milestones, development points, XP | `UnlockSystem`, `MilestoneSystem`, `DevTreeSystem`, `XPSystem` | unlock set, City scan, signature buildings (fix 9) | actions + scan + global |
| Policies | `Policies.ModifiedSystem` | channel `Policy` | global |
| Info views | `InfoviewsUISystem` | per player; their data: cell maps, attention values (topic priority), flow status (fix 14) | local |
| Electricity and water flow colours of lines, transformers and pipes; electricity and water tooltips; transformer panel | `ElectricityFlowSystem`, `WaterPipeFlowSystem` (GameSimulation) via the flow edges, read by `NetColorSystem`, `ObjectColorSystem`, `RaycastElectricityTooltipSystem`, `RaycastWaterTooltipSystem`, `TransformerSection` | fix 14 (while the topic is active; otherwise the values of the last time it was) | keyed |
| Service coverage | coverage systems | `ServiceCoverage` buffers of edges (values) | values |
| Garbage, healthcare, education, deathcare, police, fire, post panels | panel UI systems | building values, statistics, fix 4, fix 8 | values + statistics + global |
| Selected entity panel | `SelectedInfoUISystem` | time-aligned inspection samples (`AttentionValues`), agents of the selection first (`AgentSync`) | values + agents |
| Followed citizens, life path | `LifePathEventSystem` | fix 13 | actions + agents |
| Radio, tutorials, camera, tools, photo mode | local systems | per player (tutorials off in sessions) | local |

## Remaining justified exclusions

| State | Why it is not synced | Player impact |
|---|---|---|
| Transient agents (service requests, job and school seekers, partner search, meetings) | dispatch bookkeeping of the host simulation; references to them arrive as null (`EntityCodec` lenient references, as agent replication does) | none visible: dispatched vehicles, their targets, icons and building values are synced |
| Object wetness, dirt and snow, wind, snow cover | cosmetic, simulated locally | slight visual differences of weather effects |
| Per-player state (camera, selection, info views, tools, radio, chirp likes, palette, tutorials, victory pop-up seen) | by design | none |
| Achievements | inert in modded cities | none |
| Selected object's traffic routes (the info panel's routes button: `TrafficRoutesSystem` live paths) | built every frame from each vehicle's and citizen's path, current lane and navigation lanes, which only the host's simulation advances; a client's copies are frozen at the join (snapshot movers) or empty (movers spawned later). The live path entities are tool-local on every peer (`FamilyRules` rule 1) and `TrafficRoutesSystem` is off on clients (`ClientGate`) | joining players see no traffic routes for a selection; the host sees them as in single player |

## Risks and limits

- **Volume of events.** Health and crime events target citizens; their entities replicate (small spawn and despawn
  records). Journal entries and chirps are value-scanned with the line scan budget (1 ms per frame), so a city with very
  many of them refreshes line values a little slower.
- **Presence watch re-sends** use full structure upserts, so clients re-derive the entity (`Updated`). The rolling pass is
  budgeted (0.25 ms per frame) and the watched types are swept every 0.25 s, at most 256 entities per frame. A component
  that a mod toggles every frame on many buildings would cause many upserts; the diagnostics dump counts them
  (`presence watch: N re-sends`).
- **Traffic light phases** travel on the attention stream, so junctions far from every camera update later; lights near a
  player's camera follow within the attention latency (well under a second at default budgets).
- **Blob channels** decode with an exact-length check; a channel that fails once is off for the session (logged).
- **`CreateChirpSystem.GetQueue`** asserts that the system is enabled; release builds strip Unity assertions.
- **Flow status volume.** A keyframe is every keyed edge of the network (about 12-20 bytes each), sent in-view first at
  16 KB/s per peer, so in a city with 100 000 flow edges the far side of the map converges within about two minutes of
  opening the infoview; afterwards only significant changes travel. Passes are time-sliced (0.75 ms per frame on the host,
  0.375 ms on a client with the infoview open) and run only while someone has the topic.
- **Mover roster volume.** A pass lists every mover id once, as runs: about one to two bytes per mover once ids are
  spread out by reuse (a city with 90 000 movers: roughly 150 KB per pass, so 5 KB/s at one pass per half minute,
  broadcast). The fast pass after a join sends that within a second or two.
- **Flow keys** need bound owners. Edges touching an owner without a net id are not keyed and keep their local values;
  duplicate keys are dropped on both sides. The first pass logs the number of keyed and ambiguous edges per network.
- Nothing here is tested in game yet; every new path is behind `Features.Events`, `Features.GlobalState` or
  `Features.SimValues` (flow status: `Features.FlowStatus`) and contained by `Guard`.

## Generated inventory

<!-- GENERATED:BEGIN (tools/sync_coverage.py; do not edit by hand) -->

Generated from **748** serializable components and buffers and **73** serializable systems of the decompiled Game assembly. Items without a rule: **0**; items with status `gap`: **0**.

### Counts per status

| Status | Components and buffers | Systems | Meaning |
|---|---:|---:|---|
| `structure` | 112 | 0 | structure codec (S): host captures tagged changes, clients apply and re-derive |
| `presence` | 14 | 0 | structure codec + presence watch: components the simulation adds or removes without an Updated tag |
| `scan` | 45 | 0 | structure value scan: entity values hashed every 0.25-1 s, changes re-sent |
| `values` | 86 | 1 | simulation values (attention scan, SimValues stream) |
| `keyed` | 8 | 0 | keyed state on derived entities (zone cells, sub-element deletions, line rows, flow status) |
| `global` | 5 | 25 | global state channel (GlobalStateSync: a system's own ISerializable blob or a typed channel) |
| `actions` | 2 | 0 | UI intent / unlock channel (ActionSync) |
| `statistics` | 2 | 1 | statistics history channel (StatisticsSync) |
| `cellmap` | 0 | 12 | cell map channel (CellMapSync) |
| `terrain` | 0 | 2 | terrain channel (TerrainSync) |
| `water` | 1 | 1 | water channel (WaterSync); water sources travel as structure |
| `movers` | 84 | 0 | mover channel (MoverHost/MoverClient: existence, discrete state, motion) |
| `icons` | 5 | 0 | notification icon channel (IconSync) |
| `clock` | 0 | 4 | follower clock (frame index, speed) and the local pure functions of it |
| `derived` | 109 | 7 | derived identically on every peer from synced inputs (the vanilla system keeps running) |
| `snapshot` | 164 | 6 | arrives with the join snapshot and is invariant during a session (or changes only with synced inputs) |
| `local` | 5 | 8 | per-player by design (camera, UI, tool, preferences) |
| `transient` | 2 | 0 | one-frame command, tool-time or render-time data; never outlives a frame or a tool action |
| `agents` | 76 | 0 | agent replication (AgentSync): citizens, households, pets, companies and their reverse buffers |
| `hostonly` | 26 | 5 | host-internal simulation bookkeeping (dispatch requests, seekers, cursors, AI inputs); every outcome a player sees is synced |
| `excluded` | 2 | 1 | irrelevant in a session (achievements, tutorials, editor-only, telemetry) |
| **total** | **748** | **73** | |

### Systems with saved state (global state)

| System | Phases | Status | Mechanism | Note |
|---|---|---|---|---|
| `Game.Achievements.AchievementTriggerSystem` | MainLoop | `excluded` | - | achievements are inert for any modded city (usedMods gate) |
| `Game.Areas.MapTileSystem` | PostTool | `snapshot` | join snapshot | start tiles of the map; tile purchases travel as intents (ActionSync) and map tiles as structure |
| `Game.Audio.AudioManager` | Cleanup | `local` | - | per-player audio state |
| `Game.City.CityConfigurationSystem` | MainLoop | `global` | GlobalChannel.CityName + join snapshot | city name both ways; theme, traffic side, options come with the snapshot and are invariant |
| `Game.Effects.EffectFlagSystem` | GameSimulation | `clock` | cosmetic allow-list (FollowerClock) | season and night flags are pure functions of the replicated date and time |
| `Game.Net.AirwaySystem` | Deserialize | `derived` | rebuilt at load on every peer | airway graph from the map |
| `Game.Prefabs.Modes.GameModeSystem` | Deserialize | `snapshot` | join snapshot (GameModeSystem.overrideMode) | the host's game mode |
| `Game.Prefabs.PrefabSystem` | MainLoop | `snapshot` | handshake: same mods, same versions | prefab set; prefabs travel as ranks |
| `Game.Rendering.MarkerIconSystem` | Rendering | `local` | - | marker icons are presentation |
| `Game.Rendering.TerrainMaterialSystem` | PreCulling | `terrain` | TerrainSync (splatmap tiles) | terrain paint |
| `Game.Rendering.WaterRenderSystem` | PreCulling | `local` | - | render state |
| `Game.Routes.RoutePathSystem` | ModificationEnd | `derived` | gated except in the route-tool preview window | lazy update set; line paths are host data (line state, [transport lines](../ARCHITECTURE.md#structure-and-transport-lines)) |
| `Game.Simulation.AirPollutionSystem` | GameSimulation | `cellmap` | CellMapSync | air pollution map |
| `Game.Simulation.AvailabilityInfoToGridSystem` | GameSimulation | `cellmap` | CellMapSync | availability map |
| `Game.Simulation.CitizenHappinessSystem` | GameSimulation | `global` | blob "Happiness" (5 s) | happiness factors of the city info panel |
| `Game.Simulation.CityProductionCapacityCalculationSystem` | GameSimulation | `global` | blob "Production.Capacity" (5 s) | production panel capacity |
| `Game.Simulation.CityProductionStatisticSystem` | GameSimulation | `global` | custom channel "Production.Statistic" (2 s) | consumption, usage and production chains (the full blob would grow a queue on clients) |
| `Game.Simulation.CityStatisticsSystem` | GameSimulation | `statistics` | StatisticsSync (sample count, last sample frame, CityStatistic buffers) | charts and every statistic the panels read |
| `Game.Simulation.CitySystem` | Modification1 | `snapshot` | join snapshot | the City entity reference; the City's components are scanned (structure value scan) |
| `Game.Simulation.ClimateSystem` | GameSimulation, EditorSimulation | `clock` | cosmetic allow-list; hail and rainbow via custom channel "Runtime" | climate and weather selection are deterministic functions of the replicated date (no randomness); hail and rainbow come from the host's weather phenomena |
| `Game.Simulation.CommercialDemandSystem` | GameSimulation | `global` | blob "Demand.Commercial" | demand bars |
| `Game.Simulation.CountCityStoredResourceSystem` | GameSimulation | `global` | blob "Count.StoredResources" (2 s) | stored resources |
| `Game.Simulation.CountCompanyDataSystem` | GameSimulation | `global` | blob "Count.Companies" (2 s) | company, worker and production counters |
| `Game.Simulation.CountConsumptionSystem` | GameSimulation | `global` | blob "Count.Consumption" (2 s) | consumption counters |
| `Game.Simulation.CountHouseholdDataSystem` | GameSimulation | `global` | blob "Count.Household" | household counters |
| `Game.Simulation.CountResidentialPropertySystem` | GameSimulation | `global` | blob "Count.ResidentialProperties" (2 s) | free and total properties, shelters |
| `Game.Simulation.CountStudyPositionsSystem` | GameSimulation | `global` | blob "Count.StudyPositions" (2 s) | education panel study positions |
| `Game.Simulation.CountVehicleDataSystem` | GameSimulation | `global` | blob "Count.Vehicles" (2 s) | bicycle and parking counters |
| `Game.Simulation.CountWorkplacesSystem` | GameSimulation | `global` | blob "Count.Workplaces" | workplace counters |
| `Game.Simulation.DeathCheckSystem` | GameSimulation | `snapshot` | join snapshot | death-rate curve flag, set once per save |
| `Game.Simulation.ElectricityFlowSystem` | GameSimulation | `derived` | flow graph topology rebuilt on every peer | source and sink node of the derived flow graph (flow keys Source/Sink); the system stays off on clients, its flow values come by flow key (FlowStatusSync) |
| `Game.Simulation.ElectricityStatisticsSystem` | GameSimulation | `global` | blob "Electricity.Statistics" (1 s) | electricity panel production and consumption |
| `Game.Simulation.ElectricityTradeSystem` | GameSimulation | `global` | blob "Electricity.Trade" (1 s) | electricity import and export |
| `Game.Simulation.FireHazardSystem` | GameSimulation | `global` | blob "FireHazard" (5 s) | dry days of the fire hazard view |
| `Game.Simulation.GarbageAccumulationSystem` | GameSimulation | `global` | blob "Garbage.Accumulation" (2 s) | garbage panel |
| `Game.Simulation.GroundHeightSystem` | Modification2 | `derived` | runs on every peer (Modification2) | derived from terrain (TerrainSync) |
| `Game.Simulation.GroundPollutionSystem` | GameSimulation | `cellmap` | CellMapSync | ground pollution map |
| `Game.Simulation.GroundWaterSystem` | GameSimulation | `cellmap` | CellMapSync | groundwater map |
| `Game.Simulation.IndustrialDemandSystem` | GameSimulation | `global` | blob "Demand.Industrial" | demand bars |
| `Game.Simulation.LandValueSystem` | GameSimulation | `cellmap` | CellMapSync | land value map |
| `Game.Simulation.NaturalResourceSystem` | GameSimulation, EditorSimulation | `cellmap` | CellMapSync | natural resources map |
| `Game.Simulation.NoisePollutionSystem` | GameSimulation | `cellmap` | CellMapSync | noise map |
| `Game.Simulation.PlanetarySystem` | PreCulling | `clock` | runs on every peer (PreCulling) | sun and moon from the replicated date and time |
| `Game.Simulation.PopulationToGridSystem` | GameSimulation | `cellmap` | CellMapSync | population map |
| `Game.Simulation.ProcessingCompanySystem` | GameSimulation | `hostonly` | host-only input | produced-resource totals feed only unlock requirements, which the host evaluates (unlock channel) |
| `Game.Simulation.ProductionChainDataSystem` | - | `snapshot` | - | serializes nothing |
| `Game.Simulation.ProductionSpecializationSystem` | GameSimulation | `values` | City SpecializationBonus (structure value scan) | its queue is host-internal input; Serialize drains the queue, so it must never be mirrored as a blob |
| `Game.Simulation.ResidentialDemandSystem` | GameSimulation | `global` | blob "Demand.Residential" | demand bars |
| `Game.Simulation.ResourceAvailabilitySystem` | GameSimulation | `hostonly` | host-only cursor | round-robin cursor of the host's availability update; results travel as net values |
| `Game.Simulation.ServiceFeeSystem` | GameSimulation | `global` | custom channel "Fees.Collected" (1 s) | the fee cache the budget panel's fee income is computed from (CollectedCityServiceFeeData on service prefabs, which do not replicate); the fee queue is host-internal |
| `Game.Simulation.SimulationSystem` | LateUpdate | `clock` | follower clock (frame index, speed) | [follower clock](../ARCHITECTURE.md#follower-clock) |
| `Game.Simulation.SnowSystem` | GameSimulation, EditorSimulation | `local` | cosmetic allow-list | snow cover is simulated locally for visuals |
| `Game.Simulation.SoilWaterSystem` | - | `cellmap` | CellMapSync | soil water map |
| `Game.Simulation.TaxSystem` | GameSimulation | `global` | GlobalChannel.Tax (both ways) | tax rates |
| `Game.Simulation.TelecomCoverageSystem` | GameSimulation | `cellmap` | CellMapSync | telecom map |
| `Game.Simulation.TerrainAttractivenessSystem` | GameSimulation | `cellmap` | CellMapSync | terrain attractiveness map |
| `Game.Simulation.TerrainSystem` | ModificationEnd | `terrain` | TerrainSync (heightmap tiles, stamps) | terrain |
| `Game.Simulation.TradeSystem` | GameSimulation | `hostonly` | host-only input | trade balances feed company trade costs on the host only; companies are agents |
| `Game.Simulation.TrafficAmbienceSystem` | GameSimulation | `cellmap` | CellMapSync | traffic ambience map |
| `Game.Simulation.TransportLineSystem` | GameSimulation | `hostonly` | host-only input | maximum transport speeds used by the host's vehicle AIs |
| `Game.Simulation.TransportUsageTrackSystem` | GameSimulation | `hostonly` | host-only input | transport usage feeds unlock requirements (host); results travel on the unlock channel |
| `Game.Simulation.WaterPipeFlowSystem` | GameSimulation | `derived` | flow graph topology rebuilt on every peer | source and sink node (flow keys Source/Sink); the system stays off on clients, its flow values come by flow key (FlowStatusSync) |
| `Game.Simulation.WaterStatisticsSystem` | GameSimulation | `global` | blob "Water.Statistics" (1 s) | water panel |
| `Game.Simulation.WaterSystem` | PostSimulation | `water` | WaterSync | water surface, sources, sea level |
| `Game.Simulation.WaterTradeSystem` | GameSimulation | `global` | blob "Water.Trade" (1 s) | water import and export |
| `Game.Simulation.WindSimulationSystem` | GameSimulation, EditorSimulation | `local` | cosmetic allow-list | wind cells for visuals |
| `Game.Simulation.WindSystem` | GameSimulation, EditorSimulation | `local` | cosmetic allow-list | wind for visuals |
| `Game.Simulation.ZoneAmbienceSystem` | GameSimulation | `derived` | cosmetic allow-list | from replicated buildings |
| `Game.Triggers.TriggerSystem` | ModificationEnd | `local` | runs locally; chirp creation gated (EventSync drains the queue) | trigger cooldowns drive tutorials and radio (local); chirps come from the host |
| `Game.UI.InGame.ChirpLinkSystem` | UIUpdate | `global` | custom channel "Chirp.Links" (2 s) | sender and link names of chirps (citizens do not replicate as entities yet) |
| `Game.UI.InGame.CityInfoUISystem` | UIUpdate | `derived` | runs on every peer (UIUpdate) | smoothed demand and happiness from the replicated demand blobs and City values |
| `Game.UI.InGame.MilestoneUISystem` | UIUpdate | `local` | - | whether this player saw the victory pop-up |
| `Game.UI.NameSystem` | - | `global` | GlobalChannel.Name (both ways) | custom names |

### Components and buffers by namespace

#### Game.Achievements (1)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `EventAchievementTrackingData` | component | `excluded` | - | achievements |

#### Game.Agents (5)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `HasJobSeeker` | component (enableable) | `agents` | agent replication (components of households and citizens) | tax payers, moving away, property seekers |
| `JobSeeker` | component | `hostonly` | TransientAgent family; references to it arrive as null | job search bookkeeping; the resulting jobs replicate with the agents |
| `MovingAway` | component | `agents` | agent replication (components of households and citizens) | tax payers, moving away, property seekers |
| `PropertySeeker` | component (enableable) | `agents` | agent replication (components of households and citizens) | tax payers, moving away, property seekers |
| `TaxPayer` | component | `agents` | agent replication (components of households and citizens) | tax payers, moving away, property seekers |

#### Game.Areas (28)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `Area` | component | `structure` | structure codec | lots, districts, surfaces, spaces, map tiles |
| `Batch` | component (empty) | `derived` | rendering | derived |
| `BorderDistrict` | component | `derived` | district systems on every peer | persistent structure |
| `ChangedDistrict` | component (empty) | `transient` | one-frame marker | - |
| `Clear` | component (empty) | `structure` | structure codec | lots, districts, surfaces, spaces, map tiles |
| `Clip` | component (empty) | `structure` | structure codec | lots, districts, surfaces, spaces, map tiles |
| `CurrentDistrict` | component | `derived` | district systems on every peer | persistent structure |
| `District` | component | `structure` | structure codec | lots, districts, surfaces, spaces, map tiles |
| `DistrictModifier` | buffer | `derived` | from replicated district policies | - |
| `Expand` | buffer (empty) | `derived` | Areas.GeometrySystem | derived |
| `Extractor` | component | `values` | attention values | extractor amounts |
| `Geometry` | component (empty) | `derived` | Areas.GeometrySystem | derived |
| `HangaroundLocation` | component (empty) | `structure` | structure codec | lots, districts, surfaces, spaces, map tiles |
| `LabelExtents` | buffer (empty) | `derived` | labels | derived |
| `LabelVertex` | buffer (empty) | `derived` | labels | derived |
| `Lot` | component (empty) | `structure` | structure codec | lots, districts, surfaces, spaces, map tiles |
| `MapFeatureElement` | buffer | `values` | attention values | map feature amounts |
| `MapTile` | component (empty) | `structure` | structure codec | lots, districts, surfaces, spaces, map tiles |
| `Navigation` | component (empty) | `structure` | structure codec | lots, districts, surfaces, spaces, map tiles |
| `Node` | buffer | `structure` | structure codec | lots, districts, surfaces, spaces, map tiles |
| `ServiceDistrict` | buffer | `structure` | structure codec | lots, districts, surfaces, spaces, map tiles |
| `Space` | component (empty) | `structure` | structure codec | lots, districts, surfaces, spaces, map tiles |
| `Storage` | component | `values` | attention values | storage amounts |
| `SubArea` | buffer (empty) | `derived` | reference systems | derived |
| `Surface` | component (empty) | `structure` | structure codec | lots, districts, surfaces, spaces, map tiles |
| `Terrain` | component (empty) | `structure` | structure codec | lots, districts, surfaces, spaces, map tiles |
| `Triangle` | buffer (empty) | `derived` | Areas.GeometrySystem | derived |
| `WoodResource` | buffer | `values` | attention values | forest amounts |

#### Game.Buildings (89)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `Abandoned` | component | `presence` | structure codec + presence watch | abandoned buildings |
| `AdminBuilding` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `AttractivenessProvider` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `Battery` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `BicycleParkingFacility` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `Building` | component | `structure` | structure codec (deferred road fields) | [host capture](../ARCHITECTURE.md#host-capture) |
| `BuildingCondition` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `BuildingEfficiency` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `BuildingModifier` | buffer | `structure` | structure codec | policies of buildings (GlobalChannel.Policy) |
| `BuildingNotifications` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `CarParkingFacility` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `CargoTransportStation` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `CitizenPresence` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `CityEffectProvider` | component (empty) | `structure` | structure codec | marker |
| `CommercialProperty` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `Condemned` | component (empty) | `presence` | structure codec + presence watch | condemned buildings (ZoneCheckSystem is host-only) |
| `ConnectedBuilding` | buffer (empty) | `derived` | RoadConnectionSystem on every peer | road choice reconciled to the host's |
| `CrimeProducer` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `DeathcareFacility` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `DisasterFacility` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `EarlyDisasterWarningDuration` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `EarlyDisasterWarningSystem` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `Efficiency` | buffer | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `ElectricityConsumer` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `ElectricityProducer` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `EmergencyGenerator` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `EmergencyShelter` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `Extension` | component | `structure` | structure codec | building extensions |
| `ExtractorFacility` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `ExtractorProperty` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `FireStation` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `FirewatchTower` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `GarbageFacility` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `GarbageProducer` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `GoodsDeliveryFacility` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `Hospital` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `IndustrialProperty` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `InstalledUpgrade` | buffer (empty) | `derived` | reference systems on every peer | from replicated upgrades |
| `LeisureProvider` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `LocalEffectProvider` | component (empty) | `structure` | structure codec | marker |
| `Lot` | component (empty) | `structure` | structure codec | lot marker |
| `MailProducer` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `MaintenanceDepot` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `ModifiedServiceCoverage` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `Occupant` | buffer | `agents` | data on buildings; occupants are citizens | [agents](../ARCHITECTURE.md#agents) |
| `OfficeProperty` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `Park` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `ParkMaintenance` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `ParkingFacility` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `Patient` | buffer | `agents` | data on buildings; patients are citizens | [agents](../ARCHITECTURE.md#agents) |
| `PoliceStation` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `PollutionEmitModifier` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `PostFacility` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `Prison` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `PropertyOnMarket` | component | `presence` | structure codec + presence watch, values by attention | property market |
| `PropertyRenter` | component | `agents` | agent replication (households, companies) | renters are agents |
| `PropertyToBeOnMarket` | component (empty) | `presence` | structure codec + presence watch | property market |
| `PublicTransportStation` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `RenewableElectricityProduction` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `Renter` | buffer (empty) | `agents` | data on buildings; renters are agents | [agents](../ARCHITECTURE.md#agents) |
| `RescueTarget` | component | `presence` | structure codec + presence watch | targets of disaster rescue |
| `ResearchFacility` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `ResidentialProperty` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `ResourceConsumer` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `ResourceNeeding` | buffer | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `ResourceProducer` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `RoadMaintenance` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `School` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `ServiceUpgrade` | component (empty) | `structure` | structure codec | service upgrades |
| `ServiceUsage` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `SewageOutlet` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `Signature` | component (empty) | `structure` | structure codec | signature marker |
| `SpawnLocationElement` | buffer (empty) | `derived` | on every peer | from sub-objects |
| `StorageProperty` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `Student` | buffer (empty) | `agents` | data on buildings; students are citizens | [agents](../ARCHITECTURE.md#agents) |
| `StudentsRemoved` | component (empty) | `agents` | marker of student removal (citizens) | host simulation |
| `TelecomConsumer` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `TelecomFacility` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `TrafficSpawner` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `Transformer` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `TransportDepot` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `TransportStation` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `Warehouse` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `WastewaterTreatmentPlant` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `WaterConsumer` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `WaterPowered` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `WaterPumpingStation` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `WaterTower` | component | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |
| `WelfareOffice` | component (empty) | `values` | attention values (structure entities: buildings, extensions, upgrades) | simulation state of buildings; presence changes in structure records |

#### Game.Citizens (41)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `Adult` | component (empty) | `agents` | agent replication | citizens, households, pets |
| `Arrived` | component (empty, enableable) | `agents` | agent replication | citizens, households, pets |
| `AttendingMeeting` | component | `agents` | agent replication | citizens, households, pets |
| `BicycleOwner` | component (empty, enableable) | `agents` | agent replication | citizens, households, pets |
| `CarKeeper` | component (empty, enableable) | `agents` | agent replication | citizens, households, pets |
| `Child` | component (empty) | `agents` | agent replication | citizens, households, pets |
| `Citizen` | component | `agents` | agent replication | citizens, households, pets |
| `CommuterHousehold` | component | `agents` | agent replication | citizens, households, pets |
| `CoordinatedMeeting` | component | `hostonly` | TransientAgent family | meeting bookkeeping |
| `CoordinatedMeetingAttendee` | buffer | `hostonly` | TransientAgent family | meeting bookkeeping |
| `CrimeVictim` | component (enableable) | `agents` | agent replication | citizens, households, pets |
| `Criminal` | component | `agents` | agent replication | citizens, households, pets |
| `CurrentBuilding` | component | `agents` | agent replication | citizens, households, pets |
| `CurrentTransport` | component | `agents` | agent replication | citizens, households, pets |
| `Elderly` | component (empty) | `agents` | agent replication | citizens, households, pets |
| `Followed` | component | `actions` | channel "Follow" (client deltas, host LifePathEventSystem.FollowCitizen) + agent replication | the followed list is part of the city and shared |
| `HasSchoolSeeker` | component | `agents` | agent replication | citizens, households, pets |
| `HealthProblem` | component | `agents` | agent replication | citizens, households, pets |
| `HomelessHousehold` | component | `agents` | agent replication | citizens, households, pets |
| `Household` | component | `agents` | agent replication | citizens, households, pets |
| `HouseholdAnimal` | buffer (empty) | `agents` | agent replication | citizens, households, pets |
| `HouseholdCitizen` | buffer (empty) | `agents` | agent replication | citizens, households, pets |
| `HouseholdMember` | component | `agents` | agent replication | citizens, households, pets |
| `HouseholdNeed` | component | `agents` | agent replication | citizens, households, pets |
| `HouseholdPet` | component | `agents` | agent replication | citizens, households, pets |
| `LeaveHouseholdTag` | component (empty) | `agents` | agent replication | citizens, households, pets |
| `Leisure` | component | `agents` | agent replication | citizens, households, pets |
| `LeisureSeekerCooldown` | component | `agents` | agent replication | citizens, households, pets |
| `LodgingSeeker` | component (empty) | `agents` | agent replication | citizens, households, pets |
| `LookingForPartner` | buffer | `hostonly` | TransientAgent family | partner search bookkeeping |
| `MailSender` | component (enableable) | `agents` | agent replication | citizens, households, pets |
| `PetTrip` | buffer | `agents` | agent replication | citizens, households, pets |
| `ResourceBought` | component | `agents` | agent replication | citizens, households, pets |
| `SchoolSeeker` | component | `hostonly` | TransientAgent family; references arrive as null | school search bookkeeping |
| `SchoolSeekerCooldown` | component | `agents` | agent replication | citizens, households, pets |
| `Student` | component | `agents` | agent replication | citizens, households, pets |
| `Teen` | component (empty) | `agents` | agent replication | citizens, households, pets |
| `TouristHousehold` | component | `agents` | agent replication | citizens, households, pets |
| `TravelPurpose` | component | `agents` | agent replication | citizens, households, pets |
| `TripNeeded` | buffer | `agents` | agent replication | citizens, households, pets |
| `Worker` | component | `agents` | agent replication | citizens, households, pets |

#### Game.City (17)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `City` | component | `scan` | City entity value scan (1 s, change-driven) | money, loan, XP, milestone, population, tourism, danger level, modifiers |
| `CityModifier` | buffer | `scan` | City entity value scan (1 s, change-driven) | money, loan, XP, milestone, population, tourism, danger level, modifiers |
| `CityServiceUpkeep` | component (empty) | `snapshot` | marker on service prefabs | prefab data |
| `CityStatistic` | buffer | `statistics` | StatisticsSync | statistic entities |
| `DangerLevel` | component | `scan` | City entity value scan | disaster danger level of the city |
| `DevTreePoints` | component | `scan` | City entity value scan (1 s, change-driven) | money, loan, XP, milestone, population, tourism, danger level, modifiers |
| `MilestoneLevel` | component | `scan` | City entity value scan (1 s, change-driven) | money, loan, XP, milestone, population, tourism, danger level, modifiers |
| `PlayerMoney` | component | `scan` | City entity value scan (1 s, change-driven) | money, loan, XP, milestone, population, tourism, danger level, modifiers |
| `Population` | component | `scan` | City entity value scan (1 s, change-driven) | money, loan, XP, milestone, population, tourism, danger level, modifiers |
| `ServiceFee` | buffer | `scan` | City entity value scan + GlobalChannel.Fee (both ways) | fees |
| `ServiceFeeCollector` | component (empty) | `structure` | structure codec | marker on fee-collecting buildings |
| `ServiceImportBudget` | buffer | `scan` | City entity value scan (1 s, change-driven) | money, loan, XP, milestone, population, tourism, danger level, modifiers |
| `SpecializationBonus` | buffer | `scan` | City entity value scan (1 s, change-driven) | money, loan, XP, milestone, population, tourism, danger level, modifiers |
| `StatisticParameter` | component | `statistics` | StatisticsSync (statistic entities) | statistic keys |
| `TaxRates` | buffer | `scan` | City entity value scan (1 s, change-driven) | money, loan, XP, milestone, population, tourism, danger level, modifiers |
| `Tourism` | component | `scan` | City entity value scan (1 s, change-driven) | money, loan, XP, milestone, population, tourism, danger level, modifiers |
| `XP` | component | `scan` | City entity value scan (1 s, change-driven) | money, loan, XP, milestone, population, tourism, danger level, modifiers |

#### Game.Common (9)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `Destroyed` | component | `presence` | structure codec (Updated) + presence watch | destroyed buildings and trees (tree regrowth removes it without a tag) |
| `Native` | component (empty) | `structure` | structure codec | map tiles not owned |
| `Overridden` | component (empty) | `structure` | structure codec | overridden objects and cells |
| `Owner` | component | `structure` | structure codec (net id or derived key) | owner of replicated and derived entities |
| `PointOfInterest` | component | `structure` | structure codec | points of interest of objects |
| `PseudoRandomSeed` | component | `structure` | structure codec | seeds of replicated entities |
| `RandomLocalizationIndex` | buffer | `structure` | structure codec | generated names and chirp variants |
| `Target` | component | `movers` | mover state | vehicle targets |
| `TimeData` | component | `snapshot` | join snapshot | start date of the city; time runs with the follower clock |

#### Game.Companies (27)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `BuyingCompany` | component | `agents` | agent replication | companies |
| `CommercialCompany` | component (empty) | `agents` | agent replication | companies |
| `CompanyData` | component | `agents` | agent replication | companies |
| `CompanyNotifications` | component | `agents` | agent replication | companies |
| `CompanyStatisticData` | component | `agents` | agent replication | companies |
| `CurrentTrading` | buffer | `agents` | agent replication | companies |
| `Employee` | buffer | `agents` | data on buildings; employees are citizens | [agents](../ARCHITECTURE.md#agents) |
| `ExtractorCompany` | component (empty) | `agents` | agent replication | companies |
| `FreeWorkplaces` | component | `agents` | agent replication | companies |
| `IndustrialCompany` | component (empty) | `agents` | agent replication | companies |
| `LodgingProvider` | component | `agents` | agent replication | companies |
| `OfficeCompany` | component (empty) | `agents` | agent replication | companies |
| `ProcessingCompany` | component (empty) | `agents` | agent replication | companies |
| `Profitability` | component | `agents` | agent replication | companies |
| `ResourceBuyer` | component | `agents` | agent replication | companies |
| `ResourceExporter` | component | `agents` | agent replication | companies |
| `ResourceSeller` | component (empty) | `agents` | agent replication | companies |
| `ServiceAvailable` | component | `agents` | agent replication | companies |
| `ServiceCompanyData` | component | `agents` | agent replication | companies |
| `StorageCompany` | component | `agents` | agent replication | companies |
| `StorageLimitData` | component | `agents` | agent replication | companies |
| `StorageTransfer` | component | `agents` | agent replication | companies |
| `StorageTransferRequest` | buffer | `agents` | agent replication | companies |
| `TradeCost` | buffer | `agents` | agent replication | companies |
| `TransportCompany` | component (empty) | `agents` | agent replication | companies |
| `TransportCompanyData` | component | `agents` | agent replication | companies |
| `WorkProvider` | component | `values` | attention values (service buildings) | worker capacity of service buildings |

#### Game.Creatures (22)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `ActivityProp` | component (empty) | `movers` | mover channel | residents, pets, wildlife |
| `AmbienceEmitter` | component (empty) | `structure` | structure codec | marker on objects |
| `Animal` | component | `movers` | mover channel | residents, pets, wildlife |
| `AnimalCurrentLane` | component | `movers` | mover channel | residents, pets, wildlife |
| `AnimalNavigation` | component | `movers` | mover channel | residents, pets, wildlife |
| `Creature` | component | `movers` | mover channel | residents, pets, wildlife |
| `CreatureSpawner` | component (empty) | `structure` | structure codec | spawner marker on objects |
| `CurrentVehicle` | component | `movers` | mover channel | residents, pets, wildlife |
| `Divert` | component | `movers` | mover channel | residents, pets, wildlife |
| `Domesticated` | component | `agents` | pets of households | agent state |
| `GroupCreature` | buffer (empty) | `movers` | mover channel | residents, pets, wildlife |
| `GroupMember` | component | `movers` | mover channel | residents, pets, wildlife |
| `Human` | component | `movers` | mover channel | residents, pets, wildlife |
| `HumanCurrentLane` | component | `movers` | mover channel | residents, pets, wildlife |
| `HumanNavigation` | component | `movers` | mover channel | residents, pets, wildlife |
| `OwnedCreature` | buffer (empty) | `derived` | reference systems | - |
| `Pet` | component | `movers` | mover channel | residents, pets, wildlife |
| `Queue` | buffer | `movers` | mover channel | residents, pets, wildlife |
| `Resident` | component | `movers` | mover channel | residents, pets, wildlife |
| `RideNeeder` | component | `movers` | mover channel | residents, pets, wildlife |
| `Stumbling` | component (empty) | `movers` | mover channel | residents, pets, wildlife |
| `Wildlife` | component | `movers` | mover channel | residents, pets, wildlife |

#### Game.Economy (2)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `ResourceInfo` | component | `snapshot` | resource prefabs | prefab data |
| `Resources` | buffer | `values` | attention values (buildings); agents: agent replication; movers: mover allow-list | stored resources |

#### Game.Effects (1)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `EnabledEffect` | buffer (empty) | `derived` | EffectControlSystem on every peer | from replicated state |

#### Game.Events (31)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `AccidentSite` | component | `presence` | structure codec + presence watch | added and removed without a tag |
| `AttendingEvent` | component | `agents` | citizens | agent state |
| `CalendarEvent` | component (empty) | `scan` | host-only Event family: structure codec + value scan | fires, accidents, weather phenomena, floods, health, crime, calendar events |
| `Crime` | component (empty) | `scan` | host-only Event family: structure codec + value scan | fires, accidents, weather phenomena, floods, health, crime, calendar events |
| `DangerLevel` | component | `scan` | host-only Event family: structure codec + value scan | fires, accidents, weather phenomena, floods, health, crime, calendar events |
| `Destruction` | component (empty) | `scan` | host-only Event family: structure codec + value scan | fires, accidents, weather phenomena, floods, health, crime, calendar events |
| `Duration` | component | `scan` | host-only Event family: structure codec + value scan | fires, accidents, weather phenomena, floods, health, crime, calendar events |
| `Event` | component (empty) | `scan` | host-only Event family: structure codec + value scan | fires, accidents, weather phenomena, floods, health, crime, calendar events |
| `EventJournalCityEffect` | buffer | `scan` | host-only Event family | the event journal |
| `EventJournalCompleted` | component (empty) | `scan` | host-only Event family | the event journal |
| `EventJournalData` | buffer | `scan` | host-only Event family | the event journal |
| `EventJournalEntry` | component | `scan` | host-only Event family | the event journal; EventJournalSystem keeps the list and callbacks on clients |
| `EventJournalPending` | component | `scan` | host-only Event family | the event journal |
| `FacingWeather` | component | `presence` | structure codec + presence watch | event state on buildings |
| `FindingEventParticipants` | component (empty) | `agents` | meetings of citizens | agent state |
| `Fire` | component (empty) | `scan` | host-only Event family: structure codec + value scan | fires, accidents, weather phenomena, floods, health, crime, calendar events |
| `Flood` | component (empty) | `scan` | host-only Event family: structure codec + value scan | fires, accidents, weather phenomena, floods, health, crime, calendar events |
| `Flooded` | component | `presence` | structure codec + presence watch | removed without a tag |
| `HealthEvent` | component (empty) | `scan` | host-only Event family: structure codec + value scan | fires, accidents, weather phenomena, floods, health, crime, calendar events |
| `HotspotFrame` | buffer (empty) | `derived` | EventSync writes the frames from the replicated hotspot | never saved; EventInterpolateSystem indexes it unchecked |
| `InDanger` | component | `presence` | structure codec + presence watch | removed with EffectsUpdated only |
| `InvolvedInAccident` | component | `movers` | mover discrete state | vehicles and creatures in an accident |
| `JournalEvent` | component | `scan` | host-only Event family: structure codec + value scan | fires, accidents, weather phenomena, floods, health, crime, calendar events |
| `OnFire` | component | `presence` | structure codec + presence watch, values by attention | IgniteSystem adds it with BatchesUpdated only |
| `SpectatorEvent` | component (empty) | `scan` | host-only Event family: structure codec + value scan | fires, accidents, weather phenomena, floods, health, crime, calendar events |
| `SpectatorSite` | component | `presence` | structure codec + presence watch | event state on buildings |
| `TargetElement` | buffer | `scan` | host-only Event family: structure codec + value scan | fires, accidents, weather phenomena, floods, health, crime, calendar events |
| `TrafficAccident` | component (empty) | `scan` | host-only Event family: structure codec + value scan | fires, accidents, weather phenomena, floods, health, crime, calendar events |
| `VehicleLaunch` | component | `scan` | host-only Event family: structure codec + value scan | fires, accidents, weather phenomena, floods, health, crime, calendar events |
| `WaterLevelChange` | component | `scan` | host-only Event family: structure codec + value scan | fires, accidents, weather phenomena, floods, health, crime, calendar events |
| `WeatherPhenomenon` | component | `scan` | host-only Event family: structure codec + value scan | fires, accidents, weather phenomena, floods, health, crime, calendar events |

#### Game.Net (82)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `Aggregate` | component (empty) | `structure` | structure codec | net nodes and edges |
| `AggregateElement` | buffer | `structure` | structure codec | net nodes and edges |
| `Aggregated` | component (empty) | `derived` | AggregateSystem, reconciled to the host's AggregateElement list | [client apply](../ARCHITECTURE.md#client-apply) |
| `AreaLane` | component | `derived` | lanes | derived |
| `ArrowMaterial` | shared (empty) | `derived` | arrows | derived |
| `ArrowPosition` | buffer (empty) | `derived` | arrows | derived |
| `Bottleneck` | component | `icons` | traffic bottleneck marker; the notification travels on IconSync | host simulation (TrafficBottleneckSystem) |
| `BuildOrder` | component | `structure` | structure codec | net nodes and edges |
| `CarLane` | component | `derived` | lanes | derived |
| `Composition` | component (empty) | `derived` | CompositionSelect on every peer | derived |
| `ConnectedEdge` | buffer (empty) | `derived` | reference systems | derived |
| `ConnectedNode` | buffer | `structure` | structure codec | net nodes and edges |
| `ConnectionLane` | component | `derived` | lanes | derived |
| `CoverageServiceType` | shared | `derived` | coverage | shared component of coverage data |
| `Curve` | component | `structure` | structure codec | net nodes and edges |
| `CutRange` | buffer | `structure` | structure codec | net nodes and edges |
| `Density` | component | `values` | attention values | density |
| `Edge` | component | `structure` | structure codec | net nodes and edges |
| `EdgeColor` | component (empty) | `derived` | rendering (infoview colours from replicated values) | derived |
| `EdgeGeometry` | component (empty) | `derived` | Net.GeometrySystem | derived |
| `EdgeLane` | component | `derived` | lanes | derived |
| `EdgeMapping` | component (empty) | `derived` | rendering | derived |
| `ElectricityConnection` | component (empty) | `derived` | flow graph | derived |
| `Elevation` | component | `structure` | structure codec | net nodes and edges |
| `EndNodeGeometry` | component (empty) | `derived` | Net.GeometrySystem | derived |
| `Fixed` | component | `structure` | structure codec | net nodes and edges |
| `GarageLane` | component | `derived` | lanes | derived |
| `Gate` | component (empty) | `structure` | structure codec | net nodes and edges |
| `HangingLane` | component | `derived` | lanes | derived |
| `LabelExtents` | component (empty) | `derived` | labels | derived |
| `LabelMaterial` | shared (empty) | `derived` | labels | derived |
| `LabelPosition` | buffer (empty) | `derived` | labels | derived |
| `LabelVertex` | buffer (empty) | `derived` | labels | derived |
| `LandValue` | component | `values` | attention values | land value of edges |
| `Lane` | component | `derived` | lanes regenerated on every peer (LaneSystem) | portable lane keys when referenced |
| `LaneColor` | component (empty) | `derived` | rendering | derived |
| `LaneCondition` | component | `derived` | lanes (wear shown from edges) | derived |
| `LaneConnection` | component | `derived` | lanes | derived |
| `LaneFlow` | component | `derived` | lanes | traffic flow shown from the replicated Road flow vectors |
| `LaneGeometry` | component (empty) | `derived` | lanes | derived |
| `LaneObject` | buffer (empty) | `derived` | reference systems rebuild it from movers | derived |
| `LaneOverlap` | buffer (empty) | `derived` | lanes | derived |
| `LaneReservation` | component | `movers` | reservations follow the movers | client-side reference systems |
| `LaneSignal` | component | `derived` | from node TrafficLights | [traffic lights](../ARCHITECTURE.md#events-chirps-and-traffic-lights) |
| `LocalConnect` | component (empty) | `structure` | structure codec | net nodes and edges |
| `Marker` | component (empty) | `structure` | structure codec | net nodes and edges |
| `MasterLane` | component | `derived` | lanes | derived |
| `NetCondition` | component | `values` | attention values | wear |
| `NetCustomColorVariation` | component | `structure` | structure codec | net nodes and edges |
| `Node` | component | `structure` | structure codec | net nodes and edges |
| `NodeColor` | component (empty) | `derived` | rendering | derived |
| `NodeGeometry` | component (empty) | `derived` | Net.GeometrySystem | derived |
| `NodeLane` | component | `derived` | lanes | derived |
| `Orphan` | component (empty) | `structure` | structure codec | net nodes and edges |
| `OutsideConnection` | component | `structure` | structure codec | net nodes and edges |
| `ParkingLane` | component | `derived` | lanes | derived |
| `PedestrianLane` | component | `derived` | lanes | derived |
| `Pollution` | component | `values` | attention values | net pollution |
| `ResourceAvailability` | buffer | `values` | attention values | availability buffers |
| `ResourceConnection` | component | `derived` | flow graph | derived |
| `Road` | component | `structure` | structure codec; traffic flow and LightsOff by attention values | [simulation values](../ARCHITECTURE.md#simulation-values) |
| `Roundabout` | component (empty) | `structure` | structure codec | net nodes and edges |
| `SecondaryFlow` | component | `values` | attention values | secondary flow of edges |
| `SecondaryLane` | component (empty) | `derived` | lanes | derived |
| `ServiceCoverage` | buffer | `values` | attention values | coverage buffers |
| `SlaveLane` | component | `derived` | lanes | derived |
| `Standalone` | component (empty) | `structure` | structure codec | net nodes and edges |
| `StartNodeGeometry` | component (empty) | `derived` | Net.GeometrySystem | derived |
| `SubFlow` | buffer (empty) | `derived` | flow | derived |
| `SubLane` | buffer (empty) | `derived` | lanes | derived |
| `SubNet` | buffer (empty) | `derived` | reference systems | derived |
| `SubReplacement` | buffer | `structure` | structure codec | net nodes and edges |
| `SubwayTrack` | component (empty) | `structure` | structure codec | net nodes and edges |
| `Taxiway` | component (empty) | `structure` | structure codec | net nodes and edges |
| `TrackLane` | component | `derived` | lanes | derived |
| `TrafficLights` | component | `values` | attention values (fast path for junctions in view) | node signal state |
| `TrainTrack` | component (empty) | `structure` | structure codec | net nodes and edges |
| `TramTrack` | component (empty) | `structure` | structure codec | net nodes and edges |
| `Upgraded` | component | `structure` | structure codec | net nodes and edges |
| `UtilityLane` | component | `derived` | lanes | derived |
| `WaterPipeConnection` | component | `derived` | flow graph | derived |
| `Waterway` | component (empty) | `structure` | structure codec | net nodes and edges |

#### Game.Notifications (4)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `Animation` | component | `icons` | IconSync (per-owner icon sets) | every warning and problem indicator |
| `DisallowCluster` | component (empty) | `icons` | IconSync (per-owner icon sets) | every warning and problem indicator |
| `Icon` | component | `icons` | IconSync (per-owner icon sets) | every warning and problem indicator |
| `IconElement` | buffer (empty) | `icons` | IconSync (per-owner icon sets) | every warning and problem indicator |

#### Game.Objects (41)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `ActivityLocation` | component (empty) | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `Aligned` | component | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `AssetStamp` | component (empty) | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `Attached` | component | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `Attachment` | component (empty) | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `BlockedLane` | buffer | `derived` | lane blocking derived on every peer | from objects on lanes |
| `Color` | component (empty) | `structure` | structure codec | colour marker |
| `Crane` | component (empty) | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `Damaged` | component | `presence` | structure codec + presence watch | added by DamageSystem with BatchesUpdated only |
| `Decoration` | component (empty, enableable) | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `ElectricityOutsideConnection` | component (empty) | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `Elevation` | component | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `Marker` | component (empty) | `structure` | structure codec | marker objects |
| `Moving` | component | `movers` | MoverClient | motion |
| `NetObject` | component | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `Object` | component (empty) | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `ObjectGeometry` | component (empty) | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `OutsideConnection` | component (empty) | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `Pillar` | component (empty) | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `Placeholder` | component (empty) | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `Plant` | component | `values` | attention values | plant growth |
| `Quantity` | component | `values` | attention values | stored quantities |
| `Relative` | component | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `Secondary` | component (empty) | `derived` | SecondaryObjectSystem on every peer | derived objects |
| `SpawnLocation` | component | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `Stack` | component | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `Static` | component (empty) | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `Stopped` | component (empty) | `movers` | mover discrete state | movers |
| `StreetLight` | component | `derived` | StreetLightDerive from replicated owner flags | derived on every peer |
| `SubObject` | buffer (empty) | `derived` | SubObjectSystem on every peer | derived children |
| `Surface` | component | `local` | excluded (cosmetic: WetnessSystem, DirtynessSystem) | documented fidelity exception ([known limitations](../ARCHITECTURE.md#known-limitations)) |
| `TrafficLight` | component | `derived` | from replicated node TrafficLights | [traffic lights](../ARCHITECTURE.md#events-chirps-and-traffic-lights) |
| `Transform` | component | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `TransformFrame` | buffer | `movers` | motion channel | interpolation frames |
| `Tree` | component | `values` | attention values + structure codec | tree growth state |
| `TripSource` | component | `movers` | spawn bundle | movers |
| `UnderConstruction` | component | `presence` | structure codec + presence watch, values by attention | construction progress |
| `UniqueObject` | component (empty) | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `Unspawned` | component (empty) | `movers` | spawn bundle | movers |
| `UtilityObject` | component (empty) | `structure` | structure codec | objects: buildings, props, trees, decals, stops |
| `WaterPipeOutsideConnection` | component (empty) | `structure` | structure codec | objects: buildings, props, trees, decals, stops |

#### Game.Pathfind (5)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `CoverageElement` | buffer (empty) | `derived` | coverage pathfinding | derived |
| `PathElement` | buffer | `scan` | route segments: host path state (lanes as keys); movers: on demand | [transport lines](../ARCHITECTURE.md#structure-and-transport-lines) |
| `PathInformation` | component | `scan` | route segments: host path state | [transport lines](../ARCHITECTURE.md#structure-and-transport-lines) |
| `PathInformations` | buffer | `hostonly` | pathfinding of agents | host simulation |
| `PathOwner` | component | `movers` | mover state | vehicles and creatures |

#### Game.Policies (1)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `Policy` | buffer | `global` | GlobalChannel.Policy (Modify events) + structure codec | policies of the city, districts, buildings, lines |

#### Game.Prefabs (165)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `AdditionalBuildingTerraformElement` | buffer | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `AdminBuildingData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `AggregateNetData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `AircraftData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `AirplaneData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `AmbulanceData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `AreaData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `AreaGeometryData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `AttractionData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `BatteryData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `BicycleData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `BrandData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `BridgeData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `BuildingData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `BuildingExtensionData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `BuildingPropertyData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `BuildingTerraformData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `CarData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `CarLaneData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `CarTractorData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `CarTrailerData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `CargoTransportStationData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `CargoTransportVehicleData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ConsumptionData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ContentData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ContentPrerequisiteData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `CoverageData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `DeathcareFacilityData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `DeliveryTruckData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `DisasterFacilityData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `DistrictData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ElectricityConnectionData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `EmergencyGeneratorData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `EmergencyShelterData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ExtractorAreaData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ExtractorFacilityData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `FenceData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `FireEngineData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `FireStationData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `FirewatchTowerData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `FixedNetElement` | buffer | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `GarbageFacilityData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `GarbagePoweredData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `GarbageTruckData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `GateData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `GroundWaterPoweredData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `HearseData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `HelicopterData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `HospitalData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `IndustrialProcessData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `LeisureProviderData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `LocalConnectData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `LocalModifierData` | buffer | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `Locked` | component (empty, enableable) | `actions` | GlobalChannel.Unlocks (replayed as Unlock events) | milestones, dev tree, mods |
| `LodMesh` | buffer (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `LotData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `MailBoxData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `MaintenanceDepotData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `MaintenanceVehicleData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `MapTileData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ModPrerequisiteData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `MoveableBridgeData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `MovingObjectData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `MultipleUnitTrainData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `NavigationAreaData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `NetArrowData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `NetCustomColorData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `NetData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `NetFenceData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `NetGeometryComposition` | buffer (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `NetGeometryData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `NetGeometrySection` | buffer (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `NetLaneArchetypeData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `NetLaneData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `NetLaneGeometryData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `NetNameData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `NetObjectData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `NetZoneData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `NotificationIconData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `NotificationIconDisplayData` | component (enableable) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ObjectData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ObjectGeometryData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `OfficeBuilding` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ParkData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ParkingFacilityData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ParkingLaneData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ParkingSpaceData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PathwayData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PersonalCarData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PillarData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PipelineData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PlaceableNetData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PlaceableObjectData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PlacedSignatureBuildingData` | component (empty) | `global` | custom channel "Signatures" (1 s) | written by the gated XPBuiltSystem; without it a placed signature building stayed in the clients' toolbars |
| `PlaceholderBuildingData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PlantData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PoliceCarData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PoliceStationData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PollutionData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PostFacilityData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PostVanData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PowerLineData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PowerPlantData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PrefabData` | component (enableable) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PrefabRef` | component | `structure` | structure codec (prefab rank) | on every replicated entity |
| `PrisonData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PublicTransportStationData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `PublicTransportVehicleData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ResearchFacilityData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ResourceProductionData` | buffer | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `RoadData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `RouteConnectionData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `RouteData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `SchoolData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ServiceBudget` | component | `snapshot` | prefab data | default budget of a service prefab; current budgets travel on GlobalChannel.Budget |
| `ServiceObjectData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ServiceUpgradeData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `SewageOutletData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `SignatureBuildingData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `SolarPoweredData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `SpaceData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `SpawnableBuildingData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `StackData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `StaticObjectData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `StorageAreaData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `StorageCompanyData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `SubMesh` | buffer (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `SurfaceData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `TaxiData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `TaxiwayData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `TelecomFacilityData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `TrackData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `TrackLaneData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `TrafficSpawnerData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `TrainCarriageData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `TrainData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `TrainEngineData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `TrainObjectData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `TransformerData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `TransportDepotData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `TransportLineData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `TransportStationData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `TransportStopData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `TreeData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `UIHighlight` | component (empty) | `local` | - | per-player UI highlight of a new asset |
| `UpkeepModifierData` | buffer | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `UtilityLaneData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `VehicleData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `VehicleSideEffectData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `WarehouseData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `WastewaterTreatmentPlantData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `WaterPipeConnectionData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `WaterPoweredData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `WaterPumpingStationData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `WaterTowerData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `WatercraftData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `WaterwayData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `WelfareOfficeData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `WindPoweredData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `WorkRouteData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `WorkStopData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `WorkVehicleData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `WorkplaceData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ZoneBlockData` | component (empty) | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |
| `ZoneData` | component | `snapshot` | prefab entities (handshake: same mods, versions, settings) | prefab data is invariant during a session |

#### Game.Rendering (18)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `Animated` | buffer (empty) | `derived` | rendering state on every peer | from replicated entities |
| `Bone` | buffer (empty) | `derived` | rendering state on every peer | from replicated entities |
| `BoneHistory` | buffer (empty) | `derived` | rendering state on every peer | from replicated entities |
| `CullingInfo` | component (empty) | `derived` | rendering state on every peer | from replicated entities |
| `CustomMeshColor` | buffer (enableable) | `structure` | structure codec + intent SetMeshColor | building colours |
| `Emissive` | buffer (empty) | `derived` | rendering state on every peer | from replicated entities |
| `InterpolatedTransform` | component (empty) | `derived` | rendering; EventSync sets it for weather phenomena | - |
| `LightState` | buffer (empty) | `derived` | rendering state on every peer | from replicated entities |
| `LocaleMesh` | component (empty) | `derived` | rendering state on every peer | from replicated entities |
| `MeshBatch` | buffer (empty) | `derived` | rendering state on every peer | from replicated entities |
| `MeshColor` | buffer (empty) | `derived` | rendering state on every peer | from replicated entities |
| `MeshColorPalette` | buffer | `local` | per-player palette (ACTION-COVERAGE row 26) | colours applied to buildings sync |
| `MeshGroup` | buffer (empty) | `derived` | rendering state on every peer | from replicated entities |
| `Momentum` | buffer (empty) | `derived` | rendering state on every peer | from replicated entities |
| `PlaybackLayer` | buffer (empty) | `derived` | rendering state on every peer | from replicated entities |
| `RouteBufferIndex` | component (empty) | `derived` | rendering state on every peer | from replicated entities |
| `Skeleton` | buffer (empty) | `derived` | rendering state on every peer | from replicated entities |
| `Swaying` | component (empty) | `derived` | rendering state on every peer | from replicated entities |

#### Game.Routes (43)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `AccessLane` | component | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `AirplaneStop` | component (empty) | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `BicycleParking` | component (empty) | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `BoardingVehicle` | component | `keyed` | stop state by key | station stops are sub-objects |
| `BusStop` | component (empty) | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `CarParking` | component (empty) | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `Color` | component | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `Connected` | component | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `ConnectedRoute` | buffer (empty) | `keyed` | data on stops | stop links |
| `CurrentRoute` | component | `movers` | mover forward reference | vehicles |
| `CurveElement` | buffer (empty) | `derived` | SegmentCurveSystem from host path state | derived |
| `DispatchedRequest` | buffer | `hostonly` | service requests | host simulation |
| `FerryStop` | component (empty) | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `HiddenRoute` | component (empty) | `local` | per-player overlay | masked out of replication |
| `MailBox` | component | `values` | attention values | mail boxes |
| `ParkingSpace` | component (empty) | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `PathTargets` | component | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `Position` | component | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `Route` | component | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `RouteInfo` | component | `scan` | route value scan | line state |
| `RouteLane` | component | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `RouteModifier` | buffer | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `RouteNumber` | component | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `RouteSegment` | buffer (empty) | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `RouteVehicle` | buffer (empty) | `scan` | data on routes | vehicles of a line |
| `RouteWaypoint` | buffer (empty) | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `Segment` | component | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `ShipStop` | component (empty) | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `SubRoute` | buffer (empty) | `derived` | on every peer | derived |
| `SubwayStop` | component (empty) | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `TakeoffLocation` | component | `structure` | structure codec | airports |
| `TaxiStand` | component | `keyed` | stop state by key | stops |
| `TrainStop` | component (empty) | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `TramStop` | component (empty) | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `TransportLine` | component | `scan` | route value scan | line state |
| `TransportStop` | component | `keyed` | stop state by key / structure codec | stops |
| `VehicleModel` | buffer | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `VehicleTiming` | component | `scan` | route value scan | line state |
| `VerifiedPath` | component (empty) | `derived` | marker of the path systems | derived |
| `WaitingPassengers` | component | `keyed` | stop state by key + route value scan | station stops are sub-objects |
| `Waypoint` | component | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `WorkRoute` | component (empty) | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |
| `WorkStop` | component (empty) | `structure` | structure codec + route value scan | lines, waypoints, segments, stops |

#### Game.Simulation (43)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `BatteryChargeNode` | component (empty) | `derived` | flow graph | derived topology |
| `BatteryDischargeNode` | component (empty) | `derived` | flow graph | derived topology |
| `CollectedCityServiceBudgetData` | component | `derived` | CityServiceBudgetSystem runs on clients (ModificationEnd) | from replicated buildings and budgets; worker counts depend on agent replication |
| `CollectedCityServiceFeeData` | buffer | `global` | custom channel "Fees.Collected" | lives on service prefabs; clients get the host's cache |
| `CollectedCityServiceUpkeepData` | buffer | `derived` | CityServiceBudgetSystem runs on clients | as above |
| `CollectedServiceBuildingBudgetData` | component | `derived` | CityServiceBudgetSystem runs on clients | as above |
| `ConnectedFlowEdge` | buffer (empty) | `derived` | flow graph | derived topology |
| `Creditworthiness` | component | `scan` | City entity value scan (1 s) | loan panel |
| `Dispatched` | component | `hostonly` | service requests (TransientAgent family) | requests and dispatch are host simulation |
| `ElectricityBuildingConnection` | component | `derived` | flow graph; excluded from structure records (TypeHandlers) | derived topology; names the building flow nodes of flow keys (transformer, producer, consumer, charge, discharge) |
| `ElectricityFlowEdge` | component | `keyed` | topology derived; flow, capacity and flags by flow key (FlowStatusSync, MessageKind.FlowStatus) to peers with the Electricity topic | infoview colours of power lines and transformers ([flow status](../ARCHITECTURE.md#statistics-icons-and-flow-status)) |
| `ElectricityFlowNode` | component (empty) | `derived` | flow graph | derived topology |
| `ElectricityNodeConnection` | component | `derived` | flow graph; excluded from structure records (TypeHandlers) | derived topology on net nodes and edges; names the Net flow node of flow keys |
| `ElectricityValveConnection` | component | `derived` | flow graph; excluded from structure records (TypeHandlers) | derived topology; names the Valve flow node of flow keys |
| `EvacuationRequest` | component | `hostonly` | TransientAgent family | host simulation |
| `FireRescueRequest` | component | `hostonly` | TransientAgent family | host simulation |
| `FloodCounterData` | component | `hostonly` | host-only counter | flood bookkeeping of the host's water danger systems; floods arrive as events |
| `GarbageCollectionRequest` | component | `hostonly` | TransientAgent family | host simulation |
| `GarbageTransferRequest` | component | `hostonly` | TransientAgent family | host simulation |
| `GoodsDeliveryRequest` | component | `hostonly` | TransientAgent family | host simulation |
| `HealthcareRequest` | component | `hostonly` | TransientAgent family | host simulation |
| `Loan` | component | `scan` | City entity value scan + GlobalChannel.Loan (both ways) | loan |
| `MailTransferRequest` | component | `hostonly` | TransientAgent family | host simulation |
| `MaintenanceConsumer` | component | `values` | attention values | on buildings and nets |
| `MaintenanceRequest` | component | `hostonly` | TransientAgent family | host simulation |
| `PoliceEmergencyRequest` | component | `hostonly` | TransientAgent family | host simulation |
| `PolicePatrolRequest` | component | `hostonly` | TransientAgent family | host simulation |
| `PostVanRequest` | component | `hostonly` | TransientAgent family | host simulation |
| `PrisonerTransportRequest` | component | `hostonly` | TransientAgent family | host simulation |
| `RandomTrafficRequest` | component | `hostonly` | TransientAgent family | host simulation |
| `ServiceBudgetData` | buffer | `global` | GlobalChannel.Budget (both ways) | service budgets |
| `ServiceDispatch` | buffer | `hostonly` | service requests (TransientAgent family) | host simulation; vehicles' targets travel with the movers |
| `ServiceRequest` | component | `hostonly` | TransientAgent family | host simulation |
| `TaxiRequest` | component | `hostonly` | TransientAgent family | host simulation |
| `TradeNode` | component (empty) | `derived` | flow graph | derived topology |
| `TransportVehicleRequest` | component | `hostonly` | TransientAgent family | host simulation |
| `UpdateFrame` | shared | `movers` | spawn bundle (host UpdateFrame) / prefab archetype | shared component; set on spawn |
| `WaterPipeBuildingConnection` | component | `derived` | flow graph; excluded from structure records (TypeHandlers) | derived topology; names the building flow nodes of flow keys (producer, consumer) |
| `WaterPipeEdge` | component | `keyed` | topology derived; fresh and sewage flow, capacities, pollution and flags by flow key (FlowStatusSync, MessageKind.FlowStatus) to peers with the Water topic | infoview colours of water and sewage pipes ([flow status](../ARCHITECTURE.md#statistics-icons-and-flow-status)) |
| `WaterPipeNode` | component | `derived` | flow graph | derived topology |
| `WaterPipeNodeConnection` | component | `derived` | flow graph; excluded from structure records (TypeHandlers) | derived topology on net nodes and edges; names the Net flow node of flow keys |
| `WaterPipeValveConnection` | component | `derived` | flow graph; excluded from structure records (TypeHandlers) | derived topology; names the Valve flow node of flow keys |
| `WaterSourceData` | component | `water` | WaterSource family (structure) | water sources |

#### Game.Tools (3)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `EditorContainer` | component | `excluded` | editor only | - |
| `LocalTransformCache` | component | `transient` | tool-time cache | - |
| `Recent` | component | `structure` | structure codec (refund bookkeeping) | persistent structure |

#### Game.Triggers (6)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `Chirp` | component | `scan` | host-only Event family: structure codec + value scan | chirps and life-path events; CreateChirpSystem gated on clients |
| `ChirpEntity` | buffer | `scan` | host-only Event family: structure codec + value scan | chirps and life-path events; CreateChirpSystem gated on clients |
| `ChirpLink` | buffer | `structure` | structure codec (buffer on linked entities) | chirp references of buildings |
| `LifePathEntry` | buffer | `agents` | on followed citizens | agent state |
| `LifePathEvent` | component | `scan` | host-only Event family: structure codec + value scan | chirps and life-path events; CreateChirpSystem gated on clients |
| `RadioEvent` | component | `local` | radio is per player | - |

#### Game.UI (1)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `CustomName` | component (empty) | `global` | GlobalChannel.Name | custom name marker |

#### Game.Vehicles (56)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `Aircraft` | component | `movers` | mover channel | vehicles |
| `AircraftCurrentLane` | component | `movers` | mover channel | vehicles |
| `AircraftNavigation` | component | `movers` | mover channel | vehicles |
| `AircraftNavigationLane` | buffer | `movers` | mover channel | vehicles |
| `Airplane` | component (empty) | `movers` | mover channel | vehicles |
| `Ambulance` | component | `movers` | mover channel | vehicles |
| `Bicycle` | component (empty) | `movers` | mover channel | vehicles |
| `Blocker` | component | `movers` | mover channel | vehicles |
| `Car` | component | `movers` | mover channel | vehicles |
| `CarCurrentLane` | component | `movers` | mover channel | vehicles |
| `CarNavigation` | component | `movers` | mover channel | vehicles |
| `CarNavigationLane` | buffer | `movers` | mover channel | vehicles |
| `CarTrailer` | component (empty) | `movers` | mover channel | vehicles |
| `CarTrailerLane` | component | `movers` | mover channel | vehicles |
| `CargoTransport` | component | `movers` | mover channel | vehicles |
| `Controller` | component (empty) | `movers` | mover channel | vehicles |
| `DeliveryTruck` | component | `movers` | mover channel | vehicles |
| `EvacuatingTransport` | component (empty) | `movers` | mover channel | vehicles |
| `FireEngine` | component | `movers` | mover channel | vehicles |
| `GarbageTruck` | component | `movers` | mover channel | vehicles |
| `GoodsDeliveryVehicle` | component | `movers` | mover channel | vehicles |
| `GuestVehicle` | buffer (empty) | `movers` | mover channel | vehicles |
| `Hearse` | component | `movers` | mover channel | vehicles |
| `Helicopter` | component (empty) | `movers` | mover channel | vehicles |
| `LayoutElement` | buffer | `movers` | mover channel | vehicles |
| `LoadingResources` | buffer | `movers` | mover channel | vehicles |
| `MaintenanceVehicle` | component | `movers` | mover channel | vehicles |
| `Odometer` | component | `movers` | mover channel | vehicles |
| `OutOfControl` | component (empty) | `movers` | mover channel | vehicles |
| `OwnedVehicle` | buffer (empty) | `derived` | reference systems rebuild it from replicated vehicles | - |
| `ParkMaintenanceVehicle` | component (empty) | `movers` | mover channel | vehicles |
| `ParkedCar` | component | `movers` | mover channel | vehicles |
| `ParkedTrain` | component | `movers` | mover channel | vehicles |
| `Passenger` | buffer (empty) | `movers` | mover channel | vehicles |
| `PassengerTransport` | component (empty) | `movers` | mover channel | vehicles |
| `PersonalCar` | component | `movers` | mover channel | vehicles |
| `PoliceCar` | component | `movers` | mover channel | vehicles |
| `PostVan` | component | `movers` | mover channel | vehicles |
| `PrisonerTransport` | component (empty) | `movers` | mover channel | vehicles |
| `Produced` | component | `movers` | mover channel | vehicles |
| `PublicTransport` | component | `movers` | mover channel | vehicles |
| `ReturnLoad` | component | `movers` | mover channel | vehicles |
| `RoadMaintenanceVehicle` | component (empty) | `movers` | mover channel | vehicles |
| `Rocket` | component (empty) | `movers` | mover channel | vehicles |
| `Taxi` | component | `movers` | mover channel | vehicles |
| `Train` | component | `movers` | mover channel | vehicles |
| `TrainBogieFrame` | buffer (empty) | `movers` | mover channel | vehicles |
| `TrainCurrentLane` | component | `movers` | mover channel | vehicles |
| `TrainNavigation` | component | `movers` | mover channel | vehicles |
| `TrainNavigationLane` | buffer | `movers` | mover channel | vehicles |
| `Vehicle` | component (empty) | `movers` | mover channel | vehicles |
| `Watercraft` | component | `movers` | mover channel | vehicles |
| `WatercraftCurrentLane` | component | `movers` | mover channel | vehicles |
| `WatercraftNavigation` | component | `movers` | mover channel | vehicles |
| `WatercraftNavigationLane` | buffer | `movers` | mover channel | vehicles |
| `WorkVehicle` | component | `movers` | mover channel | vehicles |

#### Game.Zones (7)

| Type | Kind | Status | Mechanism | Note |
|---|---|---|---|---|
| `Block` | component | `derived` | BlockSystem on every peer | zone blocks are derived |
| `BuildOrder` | component | `derived` | BlockSystem on every peer | zone blocks are derived |
| `Cell` | buffer | `keyed` | zone cells by block key (ZoneCells records) | cell zone types; state and height derived |
| `CurvePosition` | component | `derived` | BlockSystem on every peer | zone blocks are derived |
| `SubBlock` | buffer (empty) | `derived` | BlockSystem on every peer | zone blocks are derived |
| `VacantLot` | buffer | `derived` | CellCheckSystem | derived |
| `ValidArea` | component | `derived` | BlockSystem on every peer | zone blocks are derived |

<!-- GENERATED:END -->
