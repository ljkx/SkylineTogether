# Proposal C: "Buildable & Robust" architecture for Skyline Together

> **Historical design record.** Written before the implementation and not maintained; the code differs in many
> places. The current architecture is [docs/ARCHITECTURE.md](../ARCHITECTURE.md); see [README.md](README.md).

| | |
|---|---|
| Project | Skyline Together: a clean-room, from-scratch co-op mod for Cities: Skylines II (Unity Entities 1.0, Mono, C#) |
| Angle | Must be buildable and testable without running the game. Must survive game patches. The pure-.NET core should be as large as possible. Self-healing replication. Every milestone must be playable. The goal is still complete sync of everything that matters. |
| Scope | 2–8 players, host-authoritative. Steam relay plus direct connection. Large cities (100k+ population, 20–300 MB saves). |
| Inputs | Research digest and reports `docs/research/00`–`15`, cited as **R01 §x** … **R15 §x**. Decompiled game code under `mod_audit/decomp`, cited as `File.cs:line`. Lines marked **[V\*]** were re-read in this pass to anchor a design decision. |
| Clean room | This design comes only from the research reports and the decompiled game code. No other co-op project was consulted. |

---

## 0. Executive summary

**Model.** The host runs the full vanilla simulation. Clients are *followers*: they never run the `GameSimulation` phase. They apply host state at one fixed point in the frame, and they derive everything else through the unmodified vanilla Modification pipeline. A player action on a client is *predicted* by running vanilla code locally. A generic capture engine turns it into a *proposal*. The host validates and commits the proposal, and the result comes back to everyone as ordinary state.

Robustness and buildability rest on eight principles:

| # | Principle | Why it makes the mod buildable and patch-tolerant |
|---|---|---|
| **P1** | **The save format is the replication contract.** Replicated state is the game's own persistent set: `ISerializable` data plus presence of `IEmptySerializable` zero-size tags, on entities matched by the vanilla save query (`SerializerSystem.cs:171-209` [V\*]). From that set we subtract a curated exclusion table and add a few explicit channels. The wire codec *is* the game's versioned `Serialize/Deserialize`, driven through our own `IWriter`/`IReader` (R01 §11.2). | Colossal maintains these formats across patches. A new serializable type added by a patch or a mod is replicated by default. Private fields are handled automatically (e.g. `PlayerMoney.m_Money`, `PlayerMoney.cs` [V\*]). Hidden entity indices are handled automatically (`PathNode` writes through `Write(Entity, ignoreVersion)`, `PathNode.cs:133-149` [V\*]). |
| **P2** | **Every record is an idempotent, NetId-keyed upsert or delete.** | Join snapshot, catch-up backlog, repair, duplicate delivery and proposal acknowledgement all go through **one** apply path. Re-applying a record, or applying it on top of a snapshot that already contains it, is harmless. Unit tests can drive it exhaustively. |
| **P3** | **Clients never run `GameSimulation`.** A replacement clock driver runs only an explicit cosmetic allow-list. Gameplay systems in other phases are switched off through a name-based gate table that is checked at startup. | New gameplay systems added by game patches or mods to `GameSimulation` are neutralised on clients *by default*, with no code change (R03 §9, R11 §8). |
| **P4** | **One change-detection engine runs in both directions.** On the host, detected changes are broadcast. On the client, any divergence from host-confirmed state is either a *proposal* (inside an action window, or in a player-only category) or a *leak* (repaired automatically). | Vanilla tools, ~45 UI actions (R10 §3) and direct-write mods (MoveIt, Tree Controller, Zoning Toolkit, …; R09 §5) are covered **without patching a single UI handler or tool**. |
| **P5** | **Self-healing ladder.** Incremental Merkle digests detect divergence. Repair escalates: entity → bucket → domain rebaseline → full resync by snapshot reload. Any fault (exception, protocol gap, persistent unresolved reference) jumps to the safe end of the ladder. | Bugs, unanticipated mod behaviour and game-patch drift degrade to "repair traffic" or a reload, never to a corrupted session or a crash. |
| **P6** | **Public API first, reflection second, Harmony never by default.** Every non-public access is a named *probe* with a tested fallback (Appendix A). | Removes the most brittle class of breakage. Harmony is not shipped in M0–M6. |
| **P7** | **Pure Core.** Protocol, transport, sessions, identity tables, scheduling, digests, repair, proposals, clock maths, motion and tile codecs live in `SkylineTogether.Core` (netstandard2.0, no Unity references). It is unit-tested on the .NET SDK, including a fake-ECS replication harness. | Roughly 80 % of the logic is tested before anyone starts the game. The Unity layer is a thin set of adapters. |
| **P8** | **Zero world pollution.** No custom components and no mod entities. NetIds live in a side table, persisted through one `IDefaultSerializable` system block that vanilla skips when the mod is absent (R12 §1.4, §6.3). | Saves stay loadable without the mod. There are no structural chunk copies, and the archetype count is not doubled (R12 §1.2). |

**Milestones (all playable):** M0 lobby and chat with compatibility report → M1 join-by-snapshot "spectate", clock-synced, manual or periodic resync → M2 live structure, economy and progression from host to clients with self-healing → M3 clients build (predict and propose) → M4 living-city state (agents, buildings, events, icons, statistics, demand) → M5 traffic and pedestrians in motion, plus on-demand inspection → M6 terrain, water, cell maps and full mod adapters → M7 scale, rejoin and resume-point hardening.

**Bandwidth (100k city, per client):** ~40–120 KB/s for world state at speed 1. Motion within interest is ~40–250 KB/s, capped by a per-client budget (default 250 KB/s). The typical total is **~150–350 KB/s**. The join snapshot is 20–300 MB, sent once over a separate bulk lane (details in §3.9).

### 0.1 What "identical" means here

| Sync class | Content | Guarantee |
|---|---|---|
| **Exact** | All persistent structure (nets, objects, areas, routes, zoning), city singletons (money, loan, XP, milestones, dev points), policies, budgets, taxes, fees, names, prefab unlock state, persisted agent state, mod components | Bit-identical canonical encoding after apply. Verified by digests and repaired on mismatch. |
| **Converged** | Simulation state that changes continuously (building condition and efficiency, agent state, statistics, demand, cell maps, icons) | Identical within a bounded latency (one scan cycle per domain, 2–120 s). Verified the same way. |
| **Visual-equivalent** | Motion of vehicles and pedestrians (quantised, interest-managed), water surface, snow, derived children whose generation is not peer-stable (node lanes, node secondary objects; R13 §2.4, §3.2), ambience | Visually indistinguishable or periodically corrected. Excluded from exact audits. |
| **Per player by design** | Camera, selection, tool previews, infoviews, tool state, chirp "likes", radio channel, `HiddenRoute`, UI "new" highlights | Never replicated. |

---

## 1. Authority model per state category

The **host is the only authority** for all shared state. Clients hold a replica plus local presentation state. The table states, per category, who changes the state, how it travels, and what clients run locally. Channel names (`D-…`) are defined in §3.1.

| Category | Authoritative data | Changed by | Replication | Client side | Key refs |
|---|---|---|---|---|---|
| **Nets** (nodes, edges, building sub-nets) | `Node`, `Edge`, `Curve`, `Owner`, `Game.Net.Elevation`, `Upgraded`, `BuildOrder`, `Road` (incl. half-align flags), `Fixed`, `ConnectedNode`, `PseudoRandomSeed`, flags/tags, `TrafficLights`, mod components (e.g. `ZoningInfo`) | Host tools, accepted proposals, simulation (spawned sub-nets) | D-STRUCT: tag sweep, records, tag mirroring and closure tagging | Geometry, compositions, lanes, blocks, aggregates and search trees are derived by vanilla Mod1–Mod5 | R02 §6–9, R04 §4.1, R13 §2, §4 |
| **Aggregates** (streets) | `Aggregate` + ordered `AggregateElement` (edge NetIds), `RandomLocalizationIndex` | Derived on the host by `AggregateSystem` | D-STRUCT as first-class entities, plus client reconciliation after `AggregateSystem` | `AggregateReconcileSystem` forces the host's membership and order | R13 §7.1 |
| **Objects** (buildings, props, trees, upgrades/extensions, area-spawned objects) | `Transform`, `PrefabRef`, `Building` (option mask, flags), `UnderConstruction`, `Attached`, `Owner`, `Elevation`, `PseudoRandomSeed`, `Tree`, `Policy`, `CustomMeshColor`, `Native`, `Overridden`, mod components | Tools, proposals, simulation (zone spawn, construction, level-up, abandonment) | D-STRUCT for structure, D-BUILDING for simulation state | Prefab sub-objects and secondary objects are derived. `Building.m_RoadEdge` is kept local (recomputed by `RoadConnectionSystem`) | R04 §4.3, R13 §3, §8 |
| **Areas** (lots, districts, surfaces, map tiles, sub-areas) | `Area`, `Game.Areas.Node` buffer, `District.m_OptionMask`, `Policy`, map-tile ownership (= absence of `Native`), `Extractor`/`Storage` amounts | Tools, proposals (incl. tile purchase), simulation | D-STRUCT and D-BUILDING | `Geometry`/`Triangle` derived; `CurrentDistrict`/`BorderDistrict` derived | R04 §4.4, R13 §8 |
| **Zoning** | `Cell.m_Zone` per block cell, keyed by block | Zone tool, proposals | D-ZONE overlay by `BlockKey` (blocks themselves are derived) | `BlockSystem` regenerates blocks. `CellCheckSystem` recomputes `m_State`, `m_Height`, `VacantLot` | R13 §4.2-4.3 |
| **Routes** (lines, waypoints, segments) | `Route`, `TransportLine`, `Color`, `RouteNumber`, `VehicleModel`, `Policy`, `RouteModifier`; `Waypoint`+`Position`+`Connected`+`Owner`; `Segment`+`Owner`; ordered `RouteWaypoint`/`RouteSegment` | Route tool, line UI (proposals) | D-STRUCT | `AccessLane`, `RouteLane`, `PathElement`, `CurveElement` derived. `Routes.InitializeSystem` OFF, so host values persist | R13 §9 |
| **Citizens, households, pets, companies** | All persisted components (`Citizen`, `Household`, `HouseholdMember`, `PropertyRenter`, `Worker`, `Student`, `CompanyData`, `Employee`, `Resources`, …) | Host simulation only | Existence: D-AGENT tag sweep. State: D-AGENT slices (120 s cycle) plus on-demand inspection boost | All agent initializers OFF; reverse buffers derived | R03 §6.1-6.2, R11 §2.11 |
| **Economy** | `PlayerMoney`, `Loan`, `Creditworthiness`, `ServiceFee` buffer (City entity); `ServiceBudgetData` entity; `TaxSystem.m_TaxRates[92]` | Simulation (`BudgetApplySystem` etc.), player actions | D-SINGLETON (every 8 frames), D-SYSSTATE (tax), proposals for player changes | `CityServiceBudgetSystem` keeps running on replicated inputs. Its public totals are hash-verified (fallback: reflected feed) | R03 §6.4, R10 §3.3, R11 §5.2 |
| **Statistics** | `CityStatistic` buffers on statistic entities; `CityStatisticsSystem` sample count and last frame | Simulation | D-STATS slices (60 s); scalars through D-SYSSTATE (`Deserialize` only, never `PostDeserialize`) | UI reads replicated buffers | R11 §5.2, R14 §6.2 |
| **Demand** | Residential, Commercial and Industrial demand system state | Simulation | D-SYSSTATE (`ISerializable` with full-tag context, after reader sync) | UI reads the deserialized fields | R11 §5.2 |
| **Progression** | `XP`, `MilestoneLevel`, `DevTreePoints` (City); prefab `Locked` bits, `PlacedSignatureBuildingData`, `UnlockRequirementData.m_Progress` | Simulation, dev-tree purchases (proposal) | D-SINGLETON and D-PREFAB | Client **synthesizes** `Event+MilestoneReachedEvent` and `Event+Unlock` so the vanilla UI refreshes. All reward producers OFF | R14 §0-3 |
| **Time / weather / climate** | `SimulationSystem.frameIndex`, speed, pause; `TimeData`; `ClimateSystem` weather state; weather phenomena events | Host clock, speed requests | ClockSync (unreliable, ≤30 Hz); `ClimateSystem` via D-SYSSTATE; events through D-EVENT | Time, date and climate curves derived locally from `frameIndex` (R03 §1.3, §6.7) | R03, R05 §9, R11 §7 |
| **Services** | Service-facility components, `Efficiency`, consumers/producers, `Occupant`/`Patient` | Simulation | D-BUILDING slices (10 s cycle) | Service requests and dispatch are host-only; sent on demand for inspection panels | R03 §6.9, R15 §8.3 |
| **Events, disasters, fires, crime** | Event entities (`Event`, `Duration`, `TargetElement`), target components (`OnFire`, `Destroyed`, `AccidentSite`, `Flooded`, `Criminal`…), journal entities | Host simulation | D-EVENT (tag sweep + hot structural queries) | All command consumers and event initializers OFF; journal list fed by a client companion | R11 §2.4, R14 §6.1 |
| **Notifications / icons** | Icon set per owner (prefab, priority, layer, flags, target) | ~42 host producers | D-ICON operations applied through the client's own `IconCommandBuffer` | `IconCommandSystem` stays ON (mandatory). Marker icons stay per player | R14 §4 |
| **Chirps, life path, radio** | `Chirp` (+`ChirpEntity`, `RandomLocalizationIndex`), `LifePathEvent`, `LifePathEntry`, `Followed` | Host | D-EVENT entities; radio tags added later (M6) | `CreateChirpSystem`, `TriggerSystem`, `RandomLocalizationInitializeSystem` OFF. Likes per player | R14 §5 |
| **Moving agents** (vehicles, creatures, animals; parked cars) | Existence plus discrete state (role components, flags, owner, target, route, layout, current vehicle, `ParkedCar`); motion = `TransformFrame` ring | Host simulation | D-MOVER (global existence) and D-MOTION (interest-managed, unreliable) | Spawned with host `UpdateFrame` + `Applied`; **never** tagged `Updated`. Motion goes through a staging ring and a JIT slot writer | R15 |
| **Terrain** | `TerrainSystem` base heightmap (4096², R16) | Host terraform, client strokes (proposal) | D-TERRAIN dirty 64×64 tiles, compressed, plus rotating tile-hash audit | Local brush application = prediction only. Tiles overwrite it | R07 §5 |
| **Water** | Water sources (`WaterSourceData`+`Transform`, entities); `WaterSystem` parameters; surface texture | Host | Sources via D-STRUCT; parameters via D-WATERPARAMS; surface via periodic D-WATER tiles | The client runs the water simulation for visuals only. All water-reading gameplay is host-only | R07 §6 |
| **Snow** | GPU texture | Host | Not synced (cosmetic; arrives with the join snapshot) | Runs locally | R07 §7 |
| **Cell maps** (pollution, groundwater, natural resources, land value, …) | 14 `CellMapSystem` maps | Host simulation | D-CELLMAP (per-map XOR delta + compression) | Their systems never run on clients (GameSimulation) | R07 §8 |
| **Mod state** | Mod components on entities; mod system state; mod settings | Mods on the host; mod tools on clients (proposals) | Generic through P1; system state is opt-in per adapter; settings are fingerprinted (and adopted per adapter) | Per-mod gate entries (§9) | R09 |
| **Names** | `NameSystem` custom names; city name | Player | D-NAMES (diff of public `NameSystem.Serialize` output through `NetWriter`) | `SetCustomName` applied in `LateApply`. Generated names derive from replicated `RandomLocalizationIndex` | R13 §7.2-7.3, `NameSystem.cs:209-241,549` [V\*] |
| **Policies** | `Policy` buffers + modifiers + option masks | Player | State replication (no `Modify` events from the host) | Client `Modify` events exist only as a local prediction | R10 §4 |
| **Per-player** | Camera, selection, infoview, tool state, photo mode, radio channel, chirp likes, tutorials (disabled in sessions), achievements (inert), `HiddenRoute`, `UIHighlight` | Local | Not replicated. The camera pose is reported to the host for interest management only | — | R10 §6, R14 §7-8 |

---

## 2. Entity identity

### 2.1 NetId

- **Type:** `uint`. The value `0` means "none".
- **Allocation:** monotonic `nextNetId`, allocated only by the host and never reused within a session. Over 4·10⁹ ids give months of continuous play even at ~300 spawns/s.
- **Persistence:** `nextNetId` is persisted with the id map (§2.5).
- **Provisional ids (P-ids):** these exist only inside client proposals. A P-id is `playerSlot << 24 | localCounter`, with its own NetRef tag (§2.8). P-ids are never stored in the world.

### 2.2 Side table, not a component (R08 §10.3, R12 §1)

`Core.Identity.NetIdTable` is pure C#, so it is unit-tested:

- `int[] netIdByIndex` and `int[] versionByIndex` form a dense table indexed by `Entity.Index`. It grows in 8192-slot blocks, matching the entity allocator (R08 §10.1).
- An open-addressing `NetIdMap` maps `uint netId → EntityKey(int index, int version)`.
- Lookup is O(1) with a version check.
- Memory is about 12 B per index slot plus 16 B per bound entity, roughly 30–40 MB for 1.5 M tracked entities.
- The Game layer wraps it as `EntityMap`, which converts `EntityKey ⇄ Entity`.
- There is no structural cost and no archetype duplication (R12 §1.2).

### 2.3 Tracked set and family classification

**Candidates.** The tracked set starts from the vanilla save query:

- `Any(LoadedIndex, PrefabRef, flow-graph types, ServiceRequest, WaterSourceData, City, …, every non-Game serializable type)`
- `None(NetCompositionData, EffectInstance, LivePath, Temp, Deleted)`

(`SerializerSystem.cs:171-209` [V\*].) Two side effects of that query help us:

- `LivePath` excludes the traffic-route visualisation routes that `TrafficRoutesSystem` creates locally (`TrafficRoutesSystem.cs:456-640` [V\*]).
- `Temp` excludes all tool previews.

**Families.** `FamilyClassifier` assigns a family per *archetype* and caches the result by archetype, so it costs one lookup per chunk. Only "owned object" archetypes need a per-entity check. Rules are evaluated in this order:

| # | Rule (component presence) | Family | Tracked | Create archetype source |
|---|---|---|---|---|
| 1 | `Temp`, `LivePath`, or `PrefabRef.m_Prefab == Null` (MoveIt control points, R09 §2.3) | ToolLocal | no | — |
| 2 | `PrefabData`/`LoadedIndex` | Prefab | identity only (§2.6) | — |
| 3 | `Game.Net.Lane` | Lane | derived (key §2.7) | — |
| 4 | `Game.Zones.Block` | ZoneBlock | derived (zone overlay §3.1) | — |
| 5 | `Game.Notifications.Icon` | Icon | icon channel | `IconCommandBuffer` |
| 6 | `Game.Net.Node` / `Game.Net.Edge` | NetNode / NetEdge | yes | `NetData.m_NodeArchetype` / `m_EdgeArchetype` |
| 7 | `Aggregate` | Aggregate | yes (+ reconcile) | `AggregateNetData.m_Archetype` |
| 8 | `Vehicle` or `Creature` | Mover | yes (mover rules §3.7) | `ObjectData.m_Archetype` (moving), `MovingObjectData.m_StoppedArchetype`, `TrainObjectData.m_ControllerArchetype/m_StoppedControllerArchetype` |
| 9 | `Game.Objects.Object` + `Secondary` | SecondaryObject | derived | — |
| 10 | `Object` + `Owner`, and neither `ServiceUpgrade` nor `Building`, and the owner is not an `Area` | PrefabSubObject | derived (mirrors `SubObjectSystem.FillOldSubObjectsBuffer`, `SubObjectSystem.cs:1180-1182` [V\*]) | — |
| 11 | any other `Object` (top-level, upgrade/extension, sub-building, area-spawned) | Object | yes | `ObjectData.m_Archetype` |
| 12 | `Game.Areas.Area` | Area | yes | `AreaData.m_Archetype` |
| 13 | `Route` + `Owner` | OwnedRoute (gate bypass, `Buildings/InitializeSystem.cs:154-168` [V\*]) | derived | — |
| 14 | `Route`; `Waypoint`/`Segment` whose owner route is tracked | Route / Waypoint / Segment | yes | `RouteData.m_RouteArchetype/m_WaypointArchetype/m_ConnectedArchetype/m_SegmentArchetype` |
| 15 | `Citizen`, `Household`, `HouseholdPet`, `CompanyData` | Agent | yes | `ArchetypeData.m_Archetype` (bakes `Created+Updated`, `ArchetypePrefab.cs:27-41` [V\*]) |
| 16 | `Game.Events.Event`, `EventJournalEntry`, `Chirp`, `LifePathEvent` | Event | yes | `EventData`, `ChirpData`, `LifePathEventData` archetypes, raw for journals |
| 17 | `CityStatistic` | Statistic | yes (slow) | raw |
| 18 | `City`, `ServiceBudgetData`, `TimeData`, `MeshColorPalette` | Singleton | yes | arrive by snapshot only |
| 19 | `WaterSourceData` (may lack `PrefabRef`, R09 §2.9) | WaterSource | yes | raw from the record |
| 20 | flow graph (`ElectricityFlowNode/Edge`, `WaterPipeNode/Edge`, connection types) | FlowGraph | derived (rebuilt by the graph systems in Mod1–Mod5) | — |
| 21 | `ServiceRequest`, `JobSeeker`, `SchoolSeeker`, `LookingForPartner`, `CoordinatedMeeting`, `FloodCounterData` | HostTransient | no | — |
| 22 | matched *only* through a non-Game serializable type (e.g. Better Bulldozer `OwnerRecord` entities) | ModEntity | yes | raw from the record |
| 23 | anything else in the save query | Generic | yes (low priority) | raw from the record |

Rule 23 is the patch-tolerant default: unknown persistent entities are replicated, not dropped.

### 2.4 When NetIds are assigned

1. **Session start (host):** a baseline pass binds every tracked entity, budgeted over a few frames, together with the first full scan (§3.2).
2. **Every frame (host):** `TagSweepSystem` binds tracked entities carrying `Created`. It runs at the end of MainLoop, registered `UpdateBefore<TagSweepSystem, SaveGameSystem>(MainLoop)` (`UpdateSystem.cs:173-176` [V\*]). Because it runs *immediately before* `SaveGameSystem` (`SystemOrder.cs:69` [V\*]), every entity in a snapshot already has a NetId.
3. **Bind-on-reference (host):** if the serializer meets a reference to a tracked but unbound entity, it binds it immediately and emits its Create record in the same epoch. No dangling references can leave the host.
4. **Proposal commit (host):** each P-id create gets a NetId when it is committed (§4.5).
5. **Clients never allocate NetIds.** They receive them in records, in the snapshot's id block, or in `Bind` records.

### 2.5 Carrying NetIds through the join snapshot and through saves

The id map is persisted by `NetIdSystem : GameSystemBase, IDefaultSerializable, IPostDeserialize`, following the design in R12 §1.4:

- **Serialize:** writes `formatVersion`, `sessionId`, `nextNetId`, then `NativeList<Entity>` and `NativeList<int>` of the bound pairs. Entities go through the save's own entity table, so unsaved entities come out as `-1` (`BinaryWriter.cs:128-141`). The block is never empty (`ComponentSystemSerializer.cs:36-41`).
- **Deserialize:** runs inside the deserializer's exclusive transaction, so it touches containers only. It receives entities already remapped to the client's new entities (`BinaryReader.cs:146-157`) and binds them.
- **PostDeserialize:** drops dead entries and counts bindings per family for the post-load check (§6.5).
- **Without the mod:** the block's type name does not resolve and vanilla skips it through `ObsoleteSystemSerializer` (R12 §6.3).
- **Version tolerance:** the `AssemblyVersion` is fixed, and a self-alias `[FormerlySerializedAs("SkylineTogether.Ecs.Identity.NetIdSystem, SkylineTogether")]` protects against AQN version drift (R12 §1.5).
- **Registration:** `World.GetOrCreateSystemManaged<NetIdSystem>()` in `OnLoad`, then `SerializerSystem.SetDirty()` (public) so the system library picks it up. `updateSystem.UpdateAfter<PostDeserialize<NetIdSystem>>(SystemUpdatePhase.Deserialize)` registers the post-load hook.

The same block lets the host *resume* a session from its own save with identical NetIds (the "resume point" in §6.6).

### 2.6 Prefab mapping by PrefabID

**The dictionary.**

- Enumerate all prefab entities (`PrefabData`, including disabled ones).
- Resolve each prefab with `PrefabSystem.TryGetPrefab<PrefabBase>(PrefabData, out p)` (`PrefabSystem.cs:658` [V\*]) and `p.GetPrefabID()` (`PrefabBase.cs:394` [V\*]).
- Sort the list ordinally by `type|name|hash`. The **index in the sorted list is the wire id** (`prefabIdx`, varint).

**Validation.**

- **Fingerprint:** `xxHash64` over the sorted list. Peers with equal fingerprints have identical indices with **zero transfer**.
- **Mismatch:** the join is refused and the host sends its list for a diff report (§6.7).
- **Duplicate PrefabIDs** (first registration wins, `PrefabSystem.cs:151-176`) are detected at session start. Their sources go into the fingerprint, so a load-order difference shows up as a mismatch.
- **Prefabs added mid-session** (playset change) invalidate the dictionary and trigger a full resync.

**Resolution.**

- **Client:** `prefabIdx → Entity` through `PrefabSystem.GetEntity(PrefabBase)` (`PrefabSystem.cs:705`).
- **Host:** a dense `prefabEntity.Index → prefabIdx` table for the writer (R08 §10.4).
- **Machine-local indices are never sent:** `PrefabData.m_Index` and `ZoneType.m_Index` (zones travel as zone `prefabIdx`, R04 §4.5).

### 2.7 Derived entities: no NetIds, keyed references

Derived families (lanes, prefab sub-objects, secondary objects, zone blocks, owned routes, flow graph) are regenerated on every peer by vanilla systems and never get NetIds. When replicated data *points* at one, the writer encodes a **DerivedKey** reference. The reader resolves it against the client's own derived children, caching results per owner and invalidating them when the owner gets `Updated` or `Deleted`.

| Kind | Key | Resolution | Ref |
|---|---|---|---|
| `Lane` | `(ownerNetId, lanePrefabIdx, master/slave flags, P(start), P(middle), P(end))`, where `P(pathNode)` = owner index re-encoded as NetId plus the low 32 bits unchanged | Exact match. For node lanes, ignore the middle counter, then fall back to the nearest curve endpoints within ε (reversed match allowed) | R13 §2.5 |
| `SubObject` | `(ownerNetId, prefabIdx, Aligned.m_SubObjectIndex or position quantised to 1/64 m in owner space)` | Mirrors `FindOldSubObject` | R13 §3.1, §11 |
| `Block` | `(edgeNetId, side, m_Position quantised to 1/64 m, m_Size)` | Exact, then nearest within 1 m on the same side | R13 §4.3 |
| `OwnedRoute` | `(ownerNetId, prefabIdx)` | Unique per owner | `Buildings/InitializeSystem.cs:154-168` |

**Unresolvable references.** These decode as `Entity.Null`, and the record is queued for a retry (§3.8). If repeated repair does not converge, the type is put on the *tolerated* list (§7.3) instead of causing a repair storm.

**References avoided on purpose.** Vehicle current lanes and paths are never replicated (motion comes from frames, R15 §5). Only `ParkedCar.m_Lane` and route `Connected` stops (when the stop is a sub-object) actually need DerivedKeys.

### 2.8 Type identity and the NetRef wire encoding

**`TypeCatalog`.** Built at session start, after all mods have registered their types:

- It walks `TypeManager` exactly like `ComponentSerializerLibrary.Initialize` (`ComponentSerializerLibrary.cs:25-106` [V\*]).
- Each type is classified as `Serializable` (component, buffer or shared), `EmptyTag` (zero-size `IEmptySerializable`), `EmptyDerived` (data-carrying or buffer `IEmptySerializable`), with an `Enableable` / `ISerializeAsEnabled` flag.
- The session **type id** is the index in the list sorted by `TypeInfo.StableTypeHash` (R08 §5).
- The catalog **fingerprint** is a hash over `(StableTypeHash, SizeInChunk, ElementSize, kind)`. A mismatch refuses the join.

**`NetRef`** is one varint, `value << 2 | tag`, and is canonical, so it is used for hashing:

| Tag | Meaning |
|---|---|
| `0` | value 0 = `Null`, value 1 = local or unresolvable |
| `1` | `Net(netId)` |
| `2` | `Prefab(prefabIdx)` |
| `3` | extended. The next byte gives the kind: `Provisional(pid)` (proposals only), or `DerivedKey(kind, payload)` |

`Write(Entity, ignoreVersion:true)` (used by `PathNode`) resolves by index only, through `netIdByIndex`.

---

## 3. Replication engine

### 3.1 Domains

A *domain* is a set of families or channels with one detector, one cadence and one priority class. Priority classes: **P0** = critical and small, **P1** = structure and visible state, **P2** = bulk simulation state, **P3** = background.

| Domain | Content | Detection (§3.2) | Cadence | Class | Codec |
|---|---|---|---|---|---|
| D-STRUCT | NetNode, NetEdge, Aggregate, Object, Area, Route/Waypoint/Segment, WaterSource, ModEntity, Generic | tag sweep + hot queries + slice scan | every frame; scan cycle 60 s | P1 (structural records mandatory) | `SerializableCodec<T>` |
| D-SINGLETON | City entity, budget entity, `TimeData`, palette | poll all types | every 8 frames | P0 | `SerializableCodec<T>` |
| D-PREFAB | unlock bitset, signature flags, requirement progress | poll on `Unlock` events + every 2 s (R14 §2.4) | event / 2 s | P0 | `PrefabStateCodec` (RLE bitsets) |
| D-SYSSTATE | `TaxSystem`, Residential/Commercial/Industrial demand, `TradeSystem`, `CityStatisticsSystem` scalars, `Count*` systems, Electricity/Water statistics, `ClimateSystem` (allow-list, verified entries only) | hash of `system.Serialize(NetWriter)` | 64–1024 ticks per entry | P0/P2 | `SystemStateCodec` |
| D-BUILDING | simulation (non-source) types of buildings, facilities, companies' buildings, areas' amounts, trees | slice scan | cycle 10 s | P1 | `SerializableCodec<T>` |
| D-AGENT | Agent family: existence and state | tag sweep (existence); slice scan (state) | every frame / cycle 120 s | P1 / P2 | `SerializableCodec<T>` |
| D-EVENT | events, journal, chirps, life path | tag sweep + slice | every frame / 10 s | P1 | `SerializableCodec<T>` |
| D-STATS | statistic entities | slice | 60 s | P3 | `SerializableCodec<T>` |
| D-MOVER | mover existence and discrete-state allow-list | tag sweep + slice | every frame / 5 s | P1 | `SerializableCodec<T>` (allow-list) |
| D-MOTION | `TransformFrame` slots of movers in interest | per simulated tick | per tick | per-client budget, unreliable | `MotionCodec` (Q-B, R15 §9.1) |
| D-ICON | icon entities `Created`/`Deleted` + per-owner icon-set digest | tag sweep | every frame | P1 | `IconOpCodec` |
| D-ZONE | `Cell.m_Zone` per `BlockKey` | sweep of blocks with `Updated`/`Applied` | every frame | P1 | `ZoneOverlayCodec` (RLE) |
| D-NAMES | custom names | diff of `NameSystem.Serialize` | every 30 frames | P1 | `NameSetCodec` |
| D-CELLMAP | 14 cell maps | per-map hash, staggered | 2 s per map | P3 | `CellMapCodec` (XOR + compress) |
| D-TERRAIN | heightmap tiles | brush capture + rotating audit | event / 2 s audit | P1 (bulk lane) | `HeightTileCodec` |
| D-WATER / D-WATERPARAMS | surface tiles / `WaterSystem` public parameters | round-robin + hot tiles / poll | 1 tile/s / 1 s | P3 / P1 | `WaterTileCodec` / `WaterParamsCodec` |
| D-SESSION | clock, speed, players, chat, permissions, settings | events | — | Control lane | `ControlCodec` |

**Domains assign *families* to detectors and cadences, not individual types.** Every replicated type of a family travels in that family's records. For example, the Object family with `Building` is slice-scanned on the D-BUILDING cycle (10 s), while nets, areas and routes use the D-STRUCT cycle (60 s).

**Each type is also flagged `Source` or `Sim`** in `exclusions.json` (default `Sim`). The source list per family follows R02 §4/§9.1 and R04 §7 T1: `Transform`, `PrefabRef`, `Node`, `Edge`, `Curve`, `Owner`, `Attached`, `Elevation`, `Upgraded`, area `Node`, `Policy`, `CustomMeshColor`, waypoints, and so on. Only `Source` changes bump `rev_struct`, which drives the proposal conflict checks (§4.5).

### 3.2 Change detection

All detection is **main-thread, managed code** (hard constraint 1). It uses only read-only handles on the host (R08 §6.3). It reads after `query.CompleteDependency()`, which is nearly free at frame start because `EndFrameBarrier` has just completed the simulation producers (`EndFrameBarrier.cs` OnUpdate `producerHandle.Complete()` [V\*]).

**(a) Tag sweep.** `TagSweepSystem` runs at the end of MainLoop, before `SaveGameSystem`.

- Query: `Any(Created, Updated, Deleted, BatchesUpdated, EffectsUpdated, Applied)` plus `None(Temp)`.
- Per chunk, classify the archetype (cached). For tracked entities, append `(EntityKey, tagBits)` to the *sweep list*. Bind `Created` entities. Store the NetId of `Deleted` entities.
- Zone blocks with `Updated`/`Applied` go to the zone list.
- Every lifecycle tag passes this point exactly once. Tags added before `PrepareCleanUpSystem` are cleared in this frame's Cleanup. Tags added later (UI handlers, LateUpdate simulation) survive into the next MainLoop (R02 §5.2). The only systems between the sweep and `PrepareCleanUpSystem` are `SaveGameSystem` and `DebugWatchSystem`, and neither adds lifecycle tags. Entities tagged `Deleted` are still alive here.
- Cost: O(tagged entities), about 50–100 ns per entry.

**(b) Hot structural queries.** Small queries for gameplay-visible structural tags: `OnFire`, `Destroyed`, `Abandoned`, `Condemned`, `UnderConstruction`, `InDanger`, `Flooded`, `Damaged`, `Unspawned`.

- `GetCombinedComponentOrderVersion()` is checked every frame (R08 §3.2).
- Only when it changes is the small entity set diffed against the previous frame's set.
- This catches structural changes that come without lifecycle tags within one frame.

**(c) Slice scanner (catch-all).** Per domain, a chunk cursor walks `query.ToArchetypeChunkArray()`. For each chunk:

1. Compare the chunk's order version, `Archetype` and per-type change versions against a `ChunkRecord` keyed by `SequenceNumber`, using equality (wrap-safe, R08 §3.1). If nothing changed, skip the chunk.
2. Otherwise compute per entity and per replicated type a **32-bit raw hash**: xxHash64 of the bytes from `GetDynamicComponentDataArrayReinterpret<byte>` (components), `GetUntypedBufferAccessor` (buffer elements) and `GetEnableableBits`. Compare it with the stored raw hash.
3. Put differences into the dirty set as `(netId, typeMask)`. An archetype change becomes a signature change.

The shadow state is keyed **per entity (NetId)**, never by chunk position, because tags move entities between chunks every frame (R08 §3.3). Memory is about 4 B per (entity, type). Cost: ~1.5 GB/s of hashing in Mono, so one full pass over 1 M entities × 5 types × 40 B ≈ 130 ms, spread over the cycle. Each frame gets a hard budget: host default 1.5 ms, 8 ms during baseline (adaptive).

**(d) Dirty set and flush.** `HostReplicationSystem` runs at frame start, `UpdateAfter<HostReplicationSystem, EndFrameBarrier>(MainLoop)` (`UpdateSystem.cs:178-181`, `AddSystemUpdate :382-455` [V\*]).

- It merges last frame's sweep list, the hot-query diffs and the slice-scan output into a `DirtySet` keyed by NetId. Repeated changes coalesce, latest value wins.
- **Mandatory** (never deferred): creates, deletes, signature changes, and the values of entities created in this epoch.
- **Budgeted** (priority + age, §3.9): plain value updates.
- For each processed entry, the changed types are serialized through codecs, giving canonical bytes. A type is emitted only if its canonical hash differs from the last-sent hash. Then the `RecordStore` (last-sent per-type hashes, `rev_struct`, `rev_sim`) and the bucket digests (§7) are updated.

**(e) Pollers.**

- Singletons: full compare every 8 frames.
- System state: hash of `Serialize` output per entry cadence.
- Names: diff of the `NameSystem.Serialize` pair list.
- Prefab state: masked scan of `Locked` enable bits over a few thousand prefab entities (R14 §2.4).
- Water parameters.

**(f) Custom detectors:**

- Terrain: brush stamps captured in ApplyTool, plus a rotating `AsyncGPUReadback` tile-hash audit.
- Water tiles.
- Cell maps: hash of `GetMap` contents.
- Icons.
- Motion (§3.7.4).

**(g) Existence audit.** Entities can disappear without a `Deleted` tag, for example through a direct `DestroyEntity` by a mod. To catch these, the `NetIdTable` is walked in slices of about 20k ids per frame, checking `EntityManager.Exists`, which includes the version check.

- **Host:** a vanished bound entity produces a `Delete` record and its id is unbound.
- **Client:** a vanished bound entity is a leak and triggers a `RepairRequest` (§7.3).

The same engine runs on clients to find local divergence (§4.2). There the shadow is the **host-confirmed** canonical hash, recorded whenever a record is applied.

### 3.3 Replicated versus derived

**Default rule (P1).** For a tracked family, replicate:

- every `Serializable` type (data),
- every `EmptyTag` (presence),
- enable bits for enableable types (not for `ISerializeAsEnabled`, R01 §12.9),
- the `UpdateFrame` shared value on creates. It must be exact for movers (R15 §2.5); for other families it only selects the host simulation slice and is informational on followers.

Never replicate `EmptyDerived` data (geometry, `Composition`, reverse buffers such as `SubObject`, `SubLane`, `ConnectedEdge`, `Renter`, `OwnedVehicle`, `IconElement`). The record **signature** (type set) still lists their *presence*, so families created from a raw archetype get the same derived components the save format would restore. The enable bits of enableable `EmptyDerived` types are state and are replicated (e.g. `CarKeeper`, R15 §4.5). Non-serializable components are runtime-only by the game's own definition (R04 §2.2).

**Global exclusions**, data-driven (`exclusions.json`, Appendix B), resolved by type name:

| Type(s) | Reason |
|---|---|
| `Game.Objects.Surface` (not the area tag `Game.Areas.Surface`) | wetness and snow; cosmetic, local (R11 §3) |
| `CurrentDistrict`, `BorderDistrict` | geometry functions, recomputed (R13 §8) |
| `HiddenRoute` | per player (R10 §6.1) |
| `TransformFrame` | motion channel |
| `PathOwner`, `PathElement`, `PathInformation`, all `*Navigation*`, `*CurrentLane` except via the mover allow-list, `Blocker`, `TripSource` | per-tick or derivable (R15 §4.3, §5) |
| `UnlockRequirementData` | prefab channel |
| `Icon` | icon channel |
| `ServiceDispatch` | on demand (R15 §8.3) |

**Merge policies.** A small set of types is written field-selectively, using a hand-written `Merge(ref T local, in T host)`:

| Type | Policy |
|---|---|
| `Building` | keep local `m_RoadEdge` and `m_CurvePosition`. `RoadConnectionSystem` recomputes them, and raw writes would desynchronise `ConnectedBuilding` (R13 §8) |
| `Chirp` | mask the `Liked` flag (per player, R14 §5.4) |
| `Transform`/`Moving` on movers | written only at create or swap. Afterwards they are owned by the motion applier |

**Mover allow-list** (D-MOVER), from R15 §10.1:

- `PrefabRef`, `PseudoRandomSeed`, `Owner`, `Target`
- the role component (`PersonalCar`, `PublicTransport`, `DeliveryTruck`, service roles, …)
- `Car`/`Train`/`Watercraft`/`Aircraft` flag components
- `CurrentRoute`, `Controller`/`LayoutElement`
- `CurrentVehicle`, `Resident`/`Pet`, `Human`/`Animal`
- `ParkedCar`/`ParkedTrain` (lane through DerivedKey)
- `Unspawned`
- the archetype kind (moving, stopped or controller)
- the `CarKeeper` enable bit (R15 §4.5)

### 3.4 Serialization

**Codecs** (Game layer, one instance per catalogued type, built through `MakeGenericType` on Mono's JIT; no code generation):

- `SerializableComponentCodec<T> where T : unmanaged, IComponentData, ISerializable`
- `SerializableBufferCodec<T> where T : unmanaged, IBufferElementData, ISerializable`
- `EmptyTagCodec` (presence only)
- `EnableBitCodec`
- `UpdateFrameCodec` (1 byte)
- hand-written codecs for the custom channels

**Writing:** `value.Serialize(netWriter)`.

**Applying:**

```csharp
T v = em.HasComponent<T>(e) ? em.GetComponentData<T>(e) : default;
v.Deserialize(netReader);   // into the existing value: keeps non-serialized runtime fields (R01 §11.2)
em.SetComponentData(e, v);
```

Buffers are replaced element-wise: `ResizeUninitialized(n)`, then `default` and `Deserialize` per element.

**`NetWriter : IWriter`** is a struct (passed by value into nested calls, R01 §2.2). It holds a reference to the class `NetWriteState`, which owns:

- a Core `ByteWriter`,
- an `IEntityEncoder`: NetIdTable, host prefab table and DerivedKey providers.

Encoding:

- Primitives use the stock little-endian layout (`BinaryWriter.cs:305-364`).
- `Begin()`/`End()` blocks use a 4-byte length prefix, as `BinaryWriter.cs:27-49`, because some `Deserialize` implementations skip blocks.
- `Write(Entity)` writes a `NetRef`.
- Context: `new Context(Purpose.SaveGame, Version.current, default)`. Writers never read the format (R01 §2.3).

**`NetReader : IReader`** is a struct with a reference to `NetReadState`, which holds the shared cursor (the required semantics, R01 §2.2).

- `Read(out Entity)` decodes a `NetRef`. An unbound `Net(id)` returns `Entity.Null` and flags the current (entity, type) for a retry (§3.8).
- `context` is an **owned full-tag LoadGame context**, created once per session:

```csharp
var ctx = new Context(Purpose.LoadGame, Version.current, default,
                      Enum.GetNames(typeof(FormatTags)).Length, Allocator.Persistent); // Context.cs:30-37 [V*]
var fmt = ctx.format;                                       // struct copy shares m_Bits.Ptr
ref var bits = ref UnsafeUtility.As<ContextFormat, UnsafeList<uint>>(ref fmt); // ContextFormat.cs:9 [V*]
for (int i = 0; i < tagCount; i++) bits[i >> 5] |= 1u << i;  // the effect of internal EnableTag()
```

This avoids reflection, and it removes the null-pointer crash of a tag-less context and tax-shift bugs like `TaxSystem.cs:306-313` (R01 §12.1, R11 §5.3). The tags are contiguous from 0 (`FormatTags.cs` [V\*]).

**System state.** `SystemStateCodec` calls `system.Serialize(netWriter)`. On clients it calls `system.Deserialize(netReader)` after completing that system's readers (e.g. `TaxSystem.Readers`). Only entries verified side-effect-free are enabled (R11 §5.2: demand, tax and trade verified; `CityStatisticsSystem.PostDeserialize` is **never** called; `CityServiceBudgetSystem` is not serializable). Each entry must pass an in-game self-test before it is enabled: serialize, deserialize into the live system, re-serialize, and get identical bytes.

**Canonical hashing** (Core `CanonicalHash`, xxHash64, the same implementation on all peers):

- `typeHash` = xxHash64(canonical type bytes), stored as 32 bits.
- `entityHash` = `mix64(netId) + Σ_t mix64(typeId, typeHash_t) + mix64(signatureId) + mix64(enableBits)` (mod 2⁶⁴).
- Because the hash is taken over the `ISerializable` output, never over raw chunk bytes, padding, runtime-only fields and values that `Deserialize` recomputes cannot cause false mismatches. For example, `Curve.m_Length` is recomputed in `Deserialize` (R01 §12.4).

### 3.5 Wire formats

All integers are varints unless noted.

```
Message (any lane)   : u8 msgType | u8 flags(bit0 compressed, bit1 fragment) | payload
Fragment             : u32 fragId | u16 index | u16 count | bytes          (epochs > 480 KiB; Steam cap 512 KiB, R06 §4.2)
Epoch (World lane)   : epoch | hostFrame | u16 domainMask | recordCount | Record*
Record kinds:
  DefineTypeSet  : typeSetId | n | typeId*                       (dictionary-coded archetype signatures)
  Bind           : pid | netId                                   (proposal results; precede creates)
  Delete         : netId | u8 cause
  Create         : netId | u8 family | typeSetId | u8 flags(Applied, HasUpdateFrame, Repair)
                   | [u8 updateFrame] | nTypes | (typeId | len | bytes)* | enableBits
  Update         : netId | nTypes | (typeId | len | bytes)* | [enableBits]
  Signature      : netId | typeSetId
  Touch          : netId | u8 tags(Updated, BatchesUpdated, EffectsUpdated, Applied)
  ZoneOverlay    : BlockKey | RLE(zone prefabIdx per cell)
  IconOp         : u8 op(Add/Remove/RemovePriority) | ownerRef | prefabIdx | priority | layer | flags | targetRef | [float3]
  NameSet        : netId | utf8                                   (empty = clear)
  PrefabState    : unlocked[] | relocked[] | signature[] | progress(prefabIdx, value)*
  SystemState    : entryId | len | bytes
  XpToast        : amount | reason
  MilestoneHint  : fromLevel | toLevel                            (client synthesizes events)
  Digest*        : see §7
Motion (unreliable)  : seq | hostTick | n | (clientSlot | Q-B frame)*   (≤ 1200 B per message)
```

**Compression.** Epoch payloads of 256 B or more are compressed. The codec is negotiated in the handshake: ZStd through the game's native `CompressionUtils` (R06 §5.4), or a managed Deflate fallback in Core, used by tests and as a last resort.

**Typical sizes** (before compression; compression gives 2–4×):

| Record | Size |
|---|---|
| Edge Create | 120–200 B |
| Node Create | ~60 B |
| Building Create | 150–300 B |
| Citizen Create | 80–120 B |
| Mover Create | 60–100 B |
| Update | 10–60 B |
| Delete | 3–5 B |
| Touch | 4–5 B |
| Epoch header | ~10 B |

### 3.6 Apply point and frame ordering

Placement uses only the public `UpdateSystem` registrations. Two-type children sit immediately before or after their parent and do not depend on mod load order (`UpdateSystem.cs:173-181, 382-455` [V\*]).

| Order in frame | Host | Client | Registration |
|---|---|---|---|
| MainLoop start | `EndFrameBarrier` playback (vanilla) | same | `SystemOrder.cs:49` |
| 1 | `HostCommitSystem`: applies accepted client proposals (structural) | `ProposalBuildSystem`: turns last frame's sweep into proposals or leaks | `UpdateAfter<X, EndFrameBarrier>(MainLoop)`, registered first |
| 2 | `HostReplicationSystem`: dirty flush, slice scans, pollers, motion capture, epoch assembly and send | `NetApplySystem`: applies whole epochs atomically, mirrors tags, synthesizes events, issues icon commands, re-asserts the gate table | `UpdateAfter<X, EndFrameBarrier>(MainLoop)`, registered second |
| 3 | vanilla `RaycastSystem` … `ToolSystem` (PreTool → ToolUpdate → ApplyTool → PostTool) | same, with local previews and predicted applies | `SystemOrder.cs:55-58` |
| 3a | — | `ApplyCaptureSystem` (first in ApplyTool, apply frames only) | `UpdateBefore<X>(SystemUpdatePhase.ApplyTool)` |
| 3b | — | `ApplyGateSystem` (Viewer role only) | `UpdateBefore<X, ToolOutputSystem>(ToolUpdate)` |
| 4 | vanilla `ModificationSystem` (Mod1 … ModificationEnd) | same; derives everything replicated | `SystemOrder.cs:60` |
| 4a | — | `AggregateReconcileSystem` | `UpdateAfter<X, AggregateSystem>(Modification2B)` |
| 5 | `UnlockSystem`, `AllowBarrier<EndFrameBarrier>` (vanilla) | same | `SystemOrder.cs:61-62` |
| 6 | `LateApplySystem`: UI-command executors that need `EndFrameBarrier` | `LateApplySystem`: `SetCustomName` and other ECB-based applies | `UpdateAfter<X, AllowBarrier<EndFrameBarrier>>(MainLoop)` |
| 7 | PreCulling (inside `PreRenderSystem`) | `MotionApplySystem`, the JIT slot writer | `UpdateBefore<X, PreCullingSystem>(SystemUpdatePhase.PreCulling)` |
| 8 | UI, Rendering | same; `SessionUISystem` | `UpdateAt<X>(UIUpdate)` |
| 9 | `TagSweepSystem` | `TagSweepSystem` | `UpdateBefore<X, SaveGameSystem>(MainLoop)` |
| 10 | `SaveGameSystem` (when snapshotting), `PrepareCleanUpSystem`, Cleanup | same | vanilla |
| LateUpdate | `SpeedAuthoritySystem` → `SimulationSystem` (0–8 ticks) | `SimulationSystem` disabled → `ClientClockSystem` | `UpdateBefore<X, SimulationSystem>(LateUpdate)` / `UpdateAfter<X, SimulationSystem>(LateUpdate)` |

**Why this point (R11 §6).** `NetApplySystem` precedes `OriginalDeletedSystem` (PreTool), so local previews of deleted originals rebuild. It precedes `SubElementDeleteSystem` (PostTool only), so sub-nets of deleted owners are cleaned. It precedes `Generate*`, every initializer, `ValidationSystem` and the mod enforcers in ModificationEnd. Atomic application here satisfies the mod requirements, e.g. `Transform` and Anarchy's `TransformRecord` arrive in the same record (R09 §5.4).

**Reading at frame start.** Host serialization of the frame-*N* sweep happens at frame *N+1* start, after `EndFrameBarrier`, where all simulation jobs are complete. That avoids the sync stalls that reading at the end of MainLoop would cause. Values may include last LateUpdate's ticks. That is correct state replication, and it is idempotent (P2).

### 3.7 Applying records and structural changes

`NetApplySystem` selects **whole epochs only**. All structural epochs are applied. Value-only epochs are applied up to a 3 ms budget, with the rest deferred to later frames. One `Applier` (shared with `HostCommitSystem`) performs, per selected batch:

1. **Binds:** P-id → NetId for the originator's predicted entities (§4.6).
2. **Deletes:** batch `AddComponent(NativeArray<Entity>, ComponentType.ReadWrite<Deleted>())`. Never call `DestroyEntity`; `CleanUpSystem` destroys them (R02 §5.2). Deleted creates from the same epoch are skipped.
3. **Creates:** group by resolved archetype and call `CreateEntity(archetype, n, Allocator.Temp)` (`EntityManager.cs:2053` [V\*]), then bind NetIds. **Archetype resolution:** take the prefab-baked archetype for the record's family (table in §2.3; for movers, moving vs stopped vs controller comes from flags). Add the record's replicated types missing from it. For families without prefab archetypes (ModEntity, WaterSource, Generic), build the archetype from the record's type set plus `Created+Updated`. A record for an already-bound NetId is an **upsert** (P2).
4. **Signature reconciliation:** compute `add = S_host \ types(A)` (any listed type, `EmptyDerived` presence included) and `remove = (types(A) ∩ (Serializable ∪ EmptyTag)) \ S_host`, then batch `AddComponent`/`RemoveComponent(NativeArray<Entity>, type)` per type. `EmptyDerived` and runtime types are never removed.
5. **Shared `UpdateFrame`:** batch `SetSharedComponent(NativeArray<Entity>, UpdateFrame)` per value (`EntityManager.cs:1190` [V\*]). Creates also get `Applied` when the host had it, and **always** for movers, so `UpdateGroupSystem` keeps the host phase (`UpdateGroupSystem.cs:408-446`, R15 §4.4).
6. **Data writes:** codecs, plus retries of pending references. All entities of the epoch exist by now, so intra-epoch references resolve.
7. **Enable bits.**
8. **Derived-link fix-ups:** for each replicated `AggregateElement`, set the edges' `Aggregated.m_Aggregate` (presence-only data, R04 §4.1) before Mod2B, so `AggregateSystem` appends instead of inventing aggregates (R13 §7.1).
9. **Tags:**
   - mirrored `Touch` tags;
   - `Updated` on every non-mover entity whose source types changed;
   - **closure tags** for nets: edge → both nodes → their connected edges' other nodes → aggregates. This is the rule MoveIt uses, R02 §7, `QSegment.cs:26-43`, `QNode.cs:29-51`;
   - `BatchesUpdated` for visual-only changes;
   - `EffectsUpdated` where the host had it.
   - **Movers never get `Updated`** (`UpdateGroupSystem.cs:561-615`, `Vehicles/InitializeSystem.cs:341`; R11 §6.3). The only exception is a stopped↔moving swap, which uses the vanilla type sets (R15 §4.2).
10. **Post hooks:**
    - `MilestoneHint` → `Event+MilestoneReachedEvent(prefab(i), i)` for each step (`MilestoneReachedEvent.cs` [V\*]);
    - `PrefabState` → `Event+Unlock(prefab)` for each unlocked prefab (R14 §2.4);
    - `IconOp` → the client's own `IconCommandSystem.CreateCommandBuffer()` (`IconCommandSystem.cs:727`, `IconCommandBuffer.cs:60,93` [V\*]);
    - `XpToast` → `MilestoneUISystem.AddMessage(new XPMessage(...))` (R14 §1.4).
11. **Bookkeeping:** host-confirmed hashes, incremental bucket digests (§7), `knownEpoch`.

**Sync points.** Structural steps happen in one batch right after `EndFrameBarrier`, which has just synchronised, so the extra stall is small (R11 §6.2).

**Mid-apply failure.** An exception inside the applier marks the client **Faulted**. A full resync follows (§7.4), because ECS changes cannot be rolled back.

### 3.7.4 Moving objects (D-MOVER + D-MOTION), summarised from R15

- **Existence is global and reliable.** Spawn, despawn and swap records come through D-MOVER. Clients spawn from prefab archetypes with the host `UpdateFrame` and `Applied`. Vanilla reference systems rebuild `OwnedVehicle`, `Passenger`, `LaneObject` and the other links (R15 §4.5).
- **Motion is interest-managed and unreliable.**
  - `MotionCaptureSystem` (host, frame start) iterates mover chunks with `query.SetSharedComponentFilter(new UpdateFrame(T & 15))` (`EntityQuery.cs:395` [V\*]) for every tick `T` simulated in the last LateUpdate. Each pass touches only the 1/16 of movers whose slot was just written.
  - For each entity it tests the position against each client's interest region and encodes a Q-B frame of about 11 B (R15 §9.1). When an entity enters a client's interest, a 4-slot baseline is sent.
  - This replaces R15's Burst `IJobChunk` recommendation (hard constraint 1). The cost is about 1.3k entities per tick, ≤ 2–3 ms at speed 4 in managed code, budgeted with a half-rate far field.
- **Client side.** `MotionApplySystem` keeps a Core `StagingRing` (8 frames per entity). It JIT-writes slots `(F1>>4)&3` and the next slot, with `F1 = u + 16·((R−u−32)>>4)`, and pre-fills `F1+32`. It extrapolates missing frames with `p + v·4/15`. It keeps `Transform` and `Moving` coherent for culling and audio. It adds `EffectsUpdated` when the lights window changes and emits `SubObjectsUpdated` events for activity props (R15 §2.6).
- **Clock lag.** The client clock runs `D` ticks behind the host (§5.1).
- **Selection.** Clients keep `LaneObject` buffers maintained by vanilla `Vehicles.ReferencesSystem` from `ParkedCar`/lane data where resolvable. Otherwise they insert into the moving search tree so movers can be clicked (R15 §5). This is verified in M5.

### 3.8 Consistency and ordering

- **Epoch = transaction = one host frame.** Records inside an epoch are ordered `DefineTypeSet` → `Bind` → `Delete` → `Create` → `Signature` → `Update` → `Touch` → channel records.
- **Idempotency (P2):** a Create for a bound NetId is an upsert. A Delete for an unknown NetId is ignored. An Update for an unknown NetId is queued (≤ 600 epochs), then a repair is requested. Duplicate epochs are dropped by number.
- **Pending references:** the record bytes are kept in the `PendingRefs` table. When the referenced NetId binds, the decode is re-run. After 600 epochs, a `RepairRequest` is sent.
- **No gaps:** the World lane is reliable and ordered. A gap (a fragment lost after a reconnect) is a protocol error and leads to a full resync.
- **Concurrency with local prediction:** the client holds back incoming payloads for `(netId, typeId)` pairs covered by pending proposals until the matching `ProposalResult` (§4.6).

### 3.9 Bandwidth budgeting and prioritisation

**Lanes:** Control, World, Motion and Bulk (§8.2).

**World stream.** One World stream is shared by all clients, so epochs and digests are identical for everyone.

- **Mandatory:** structural records (creates, deletes, signatures, binds), P0 pollers, D-ICON and D-ZONE.
- **Rate-limited:** value updates. They come from a weighted fair queue across P1/P2/P3 with age boost. The rate is the **minimum** client budget (a token bucket fed from `GetConnectionRealTimeStatus.m_nSendRateBytesPerSecond × 0.8`, capped by the user setting; R06 §4.1).
- **Slow clients:** a client whose `m_cbPendingReliable` stays above 8 MB for more than 30 s gets a warning, then a resync. It does not stall the others.
- Coalescing in the dirty set means a deferred update is sent once, with its latest value.

**Motion** is per client: `budget_c − world share`. Its priority order is: followed or selected entities, then distance to the pivot, then screen size. The far field runs at half rate, and motion is skipped entirely when over budget (clients extrapolate).

**Bulk** carries snapshot chunks, domain rebaselines, terrain and water tiles. It uses only residual budget, and it pauses while the World backlog exceeds 1 MB.

**Estimates** (100k population, R03 §6, R15 §9; to be measured in M4/M5):

| Stream | Speed 1 | Speed 4 | Notes |
|---|---|---|---|
| World: structure and edits | 5–20 KB/s average, bursts up to MBs | ×1–2 | a road stroke is 5–20 KB; spawns are a few per minute |
| World: building state (10 s cycle) | 15–30 KB/s | ×1.5 | compressed |
| World: agent state (120 s cycle) | 15–30 KB/s | ×2 | 150k agents |
| World: singletons, system state, prefab, icons, names | 3–8 KB/s | ×2 | |
| World: cell maps | 3–8 KB/s | ×1 | |
| Mover existence and discrete state | 8–20 KB/s | ×3–4 | R15 §9.3 |
| Motion (1–3k in interest, Q-B) | 40–125 KB/s | 165–500 KB/s → capped at 250 | R15 §9.3 |
| Water tiles (optional quality) | 10–60 KB/s | same | adaptive, bulk |
| **Total per client** | **≈ 150–300 KB/s** | **≈ 300–450 KB/s** (capped) | host upload ≈ Σ over clients |

### 3.10 CPU budgets (managed, main thread)

| Work | Host per frame | Client per frame |
|---|---|---|
| Tag sweep | ≤ 0.5 ms (bursts on big edits) | ≤ 0.5 ms |
| Dirty flush and serialization | ≤ 2 ms budget (mandatory parts unbounded) | — |
| Slice scans | 1.5 ms (8 ms during baseline) | 1 ms (proposal and leak checks), 8 ms during initial hashing |
| Motion | ≤ 2 ms at speed 4 | JIT writer ≤ 1 ms |
| Apply | proposals: rare | ≤ 3 ms value budget; structural unbounded |

Each system measures itself (Stopwatch) and adapts its budget to keep the frame-time overhead under 5 %. Timings appear in the diagnostics HUD.

---

## 4. Client action path

### 4.1 Principle: predict with vanilla code, propose the difference, commit on the host

The acting client runs the action with **unmodified vanilla code**: tools apply locally and UI handlers run locally. The follower-side capture engine (§3.2, client mode) then compares the result against host-confirmed state and turns the difference into a **proposal**. The host validates it, commits it with the same `Applier` the clients use (§3.7), and broadcasts the result as ordinary epochs.

This design has three consequences:

- **No UI handler or tool is patched.** R10 §1 counts 182 trigger bindings and 8 mutation mechanisms. Patching them individually would be the most brittle part of any design.
- **The host never touches its own Temp namespace, `applyMode` or `ToolClearSystem`.** Remote actions therefore cannot disturb the host player's tool. If a remote deletion invalidates the host's preview, `OriginalDeletedSystem` handles it, as vanilla designed (R02 §2.2, R10 §7.3).
- **Mod tools that write directly are covered generically** (MoveIt, Tree Controller brush, Zoning Toolkit, Better Bulldozer, Anarchy component tool, Water Features water tool). This is the generic mechanism hard constraint 4 asks for (R09 §5.10).

### 4.2 Capture on the client

**1. `ApplyCaptureSystem`** runs first in ApplyTool, which happens only on apply frames (R10 §7.2 IP2). It snapshots:

- the active tool's `toolID`;
- Σ `Temp.m_Cost` over Temps without `TempFlags.Cancel`, using the `ToolApplySystem` formula (`ToolApplySystem.cs:42-74`);
- for every `Temp.m_Original`: its NetId and `TempFlags`;
- terrain stamps: each `Temp+Brush` entity's `Brush` struct and tool `prefabIdx` (R10 §7.7.2).

It then opens an **ActionWindow** for 8 frames.

**2. The vanilla apply runs as a prediction.** Permanent entities are created and originals are updated or deleted. `ToolApplySystem` subtracts money locally; the host's `PlayerMoney` overwrites it later.

**3. `TagSweepSystem`** (end of MainLoop) lists tracked tagged entities, zone blocks tagged `Applied`/`Updated`, and the NetIds of deleted entities.

**4. `ProposalBuildSystem`** runs at the start of the next frame, *before* `NetApplySystem` overwrites anything. It classifies every sweep entry:

| Entry | Classification |
|---|---|
| unbound + `Created`, tracked family | **create** (P-id + full record) |
| bound, canonical hash ≠ host-confirmed hash | **update** (changed types only) |
| bound + `Deleted`, not deleted by an applied host record this frame | **delete** |
| bound, tags only | **touch** |
| zone block | **zone overlay** (`BlockKey` → zone `prefabIdx` per cell) |

**5. Gating.**

- **Structural entries** (create, update of source types, delete, touch, zone overlay, terrain stroke) are proposals **only inside an ActionWindow**. A window is open in two cases:
  - for 8 frames after a vanilla apply (step 1);
  - while `ToolSystem.activeTool` (`ToolSystem.cs:84` [V\*]) is a **non-vanilla tool type**, plus 30 frames after. This covers mod tools.
- **Player-only state categories** (table in §4.4) are always proposable. Their diffs are found by `StatePollers`, which use the same "compare with host-confirmed" logic.
- **Adapters** can declare mod component types as player-mutable, for example Anarchy `PreventOverride`/`TransformRecord` toggled from info panels, or Plop `LevelLocked` (§9).
- **Everything else is a leak:**
  - `LeakTracker` counts it per component type for diagnostics.
  - If the divergence persists for 2 frames, it requests a repair of that entity (§7.3), which restores host state.
  - Unbound tracked entities older than 2 s that were created outside any window are deleted locally.

### 4.3 Proposal format (Core, `Proposals/Proposal.cs`)

```
Proposal {
  u32  seq;            u8 kind;            // ToolApply | ModToolEdit | StateEdit | TerrainStroke | Mixed
  u32  baseEpoch;      // client's knownEpoch when the action happened (optimistic concurrency)
  i64  cost;           // Σ Temp.m_Cost (tool applies) or observed money delta (tile purchase)
  u16  toolKind;       // toolID dictionary index (diagnostics, permissions)
  Create[]  { u32 pid; u8 family; u32 typeSetId; TypePayload[]; enableBits }   // P-id refs allowed inside payloads
  Update[]  { u32 netId; TypePayload[]; enableBits }
  Delete[]  { u32 netId }
  Touch[]   { u32 netId; u8 tags }
  ZoneOverlay[]   { BlockKey; RLE zone prefabIdx[] }
  TerrainStroke   { Stamp[] { u8 terraformingType; Brush{pos,target,start,angle,size,strength}; u32 toolPrefabIdx } }
  StateEdit[]     { u16 category; bytes payload }      // §4.4
}
```

Proposals are sent on the Control lane. Typical size is 0.2–20 KB; a big road stroke can reach 100 KB.

### 4.4 Categories and host executors

| Category | Client detection (diff against host-confirmed) | Host executor (vanilla public API where possible) | Host phase |
|---|---|---|---|
| Tool change-set (vanilla tools, Line Tool, Find It-launched tools, Tree Controller tool via its Temp pipeline) | ActionWindow entries | `Applier` (direct state commit, §4.5) | frame start |
| Mod direct-write edits (MoveIt, Zoning Toolkit, Better Bulldozer, Anarchy tool, Water Features tool) | entries inside the mod-tool window | `Applier`. Transform and `TransformRecord` are atomic per entity record | frame start |
| Zoning | zone blocks in the window | write `Cell.m_Zone` on matching host blocks + `Updated` (mirrors `ApplyZonesSystem.cs:104-140`, R13 §4.2) | frame start |
| Terrain stroke | `Temp+Brush` stamps | `TerrainSystem.ApplyBrush(type, bounds, brush, texture)` per stamp (`TerrainSystem.cs:3968`, public [V\*]); material: `TerrainMaterialSystem.ApplyBrush` (`:815`) | frame start |
| Tax rates | `TaxSystem.GetTaxRates()` array (`TaxSystem.cs:363` [V\*]) | replay the changed indices through `SetTaxRate(TaxAreaType,int)` / `SetResidentialTaxRate(...)` etc. (`:482`, `:684`) after `Readers.Complete()`, so clamping stays vanilla | LateApply |
| Service budget | `ServiceBudgetData` buffer | `CityServiceBudgetSystem.SetServiceBudget(prefab, %)` (`:1084` [V\*]) | LateApply (uses `EndFrameBarrier`) |
| Service fees | City `ServiceFee` buffer | `ServiceFeeSystem.SetFee(resource, fees, value)` (`:508` [V\*]) | LateApply |
| Loan | City `Loan.m_Amount` (`LoanSystem` stays ON on clients as the predictor) | `LoanSystem.ChangeLoan(amount)` (`:130` [V\*]), which re-clamps against host money | LateApply |
| Policies (all 11 UI forms, R10 §3.1) | `Policy` buffers on any tracked entity | `PoliciesUISystem.SetPolicy(target, policy, active, adjustment)` per changed entry (`:186` [V\*]) | LateApply |
| Names | `NameSystem` pair list (`NameSystem.Serialize`, `:549`) | `NameSystem.SetCustomName(entity, name)` (`:214` [V\*]); city name: `CityConfigurationSystem.cityName` | LateApply |
| Route colour | `Game.Routes.Color` | small executor equivalent to `ColorSection.OnSetColor` (route + `RouteVehicle` colours + `Event+ColorUpdated`, R10 §3.5 C1) | LateApply |
| Mesh colours | `CustomMeshColor` buffer and enable bit | write buffer, enable, `BatchesUpdated` (R10 C3/C4) | frame start |
| Vehicle models | `VehicleModel` buffer | write the full buffer (avoids `RemoveAtSwapBack` order divergence, R10 R1/R2) | frame start |
| Service districts | `ServiceDistrict` buffer (also written by `SelectionToolSystem` outside Apply, R10 §7.7.1) | write the full buffer + `Updated` | frame start |
| Historical toggle | `Building.m_Flags` Historical bit | write the bit (simulation-relevant, R10 C6) | frame start |
| Dev tree | `DevTreePoints` drop + node unlocked locally (`DevTreeSystem.Purchase` runs from the UI, `:157` [V\*]) | `DevTreeSystem.Purchase(nodeEntity)` per node; it validates | LateApply |
| Map tiles | `MapTile` lost `Native` (local `PurchaseSelection`, `MapTilePurchaseSystem.cs:311-344` [V\*]) + observed money delta | validate permits (`GetAvailableTiles()`, `:255`), money ≥ cost, tiles still `Native`; then `PlayerMoney.Subtract` + `MapTilePurchaseSystem.UnlockTile(em, tile)` (public static, `:346` [V\*]) | frame start |
| UI deletions (district, service upgrade, line; R10 D1–D3) | `Deleted` on tracked District/ServiceUpgrade/Route outside a tool window (an explicit category) | add `Deleted` at commit (same position as vanilla `EndFrameBarrier` playback) | frame start |
| Follow citizen | `Followed` presence (shared, 50-entry city limit) | `LifePathEventSystem.FollowCitizen/UnfollowCitizen` (R10 F1) | frame start |
| Simulation speed | **HUD buttons only** (explicit `SpeedRequest`; §5.1) | session policy: anyone / host only / majority vote | — |

### 4.5 Host validation and commit (`HostCommitSystem`)

The proposal is processed atomically in arrival order (FIFO across clients) and interleaved with the host player's native actions.

1. **Permission.** The player role allows the category. Roles: *Viewer* (none), *Builder* (all), *Custom* (flags: spend money, bulldoze, terrain, economy/policies, mod tools).
2. **Conflict (optimistic).** Every Update/Delete/Touch target, and every existing NetId referenced by a Create, must satisfy `rev_struct(netId) ≤ baseEpoch`. `rev_struct` is the epoch in which the host last sent a change to a source type of that entity. Zone overlays check the per-`BlockKey` overlay revision. Any violation rejects the whole proposal with `Conflict`.
3. **Existence.** Referenced NetIds are alive. Prefabs are unlocked (`Locked` disabled), unless `unlockAll` is set.
4. **Economy.** `cost ≥ 0`. `PlayerMoney.money ≥ cost`, unless `m_Unlimited` (`PlayerMoney.cs` [V\*]). Optionally, creation costs are cross-checked against prefab construction cost within a tolerance (R10 §8 Q1).
5. **Sanity.** At most 50k entities per proposal. Floats must be finite. Positions must lie within world bounds. Families must be allowed for the category.
6. **Commit** through the `Applier`:
   - **Creates:** from prefab archetypes. Each create mirrors the client's local apply tags (`Applied`, `Created`, `Updated`), **except** that building creations are committed *without* `Applied`. That way the host's own `ParkedVehiclesSystem` spawns parked service vehicles (`ParkedVehiclesSystem.cs:1683-1700`), while the clients' copy of that system is OFF to prevent duplicates. Creates committed with `Applied` get `UpdateFrame = mix32(netId) & 15`, because `UpdateGroupSystem` keeps the frame for `Applied` entities.
   - **Updates:** write source types, add `Updated` plus closure tags (§3.7.9). **Deletes** get `Deleted` plus closure `Updated` on the neighbours.
   - **Sanitise** `Recent.m_ModificationFrame` to the host's `frameIndex`.
   - **Money:** subtract `cost` from `PlayerMoney` on the City entity.
   - **Other channels:** terrain strokes, zone overlays and state edits go to their executors. Executors that need `EndFrameBarrier` are queued to `LateApplySystem`, because `SafeCommandBufferSystem` throws before `AllowBarrier` (`SafeCommandBufferSystem.cs` [V\*], R10 §0.4).
7. **Result.** P-id creates get NetIds immediately. A `ProposalResult{seq, Accepted|Rejected(reason), binds}` record is queued into the **next epoch**, before that epoch's creates.

**Known gap (later milestone).** Vanilla Apply queues `TriggerAction(ObjectCreated)` (R10 §7.3). The commit does not, so chirps and tutorials about remote builds are missing until an executor adds it through `TriggerSystem.CreateActionBuffer()` (guarded by `Enabled`).

### 4.6 Results and reconciliation on the originator

**Accepted.**

- `Bind` records bind each P-id entity to its NetId *before* the epoch's Create records for those NetIds.
- Those creates then **upsert** onto the predicted entities (P2), so nothing is duplicated. Any difference introduced by the host overwrites the prediction.
- The `ProposalTracker` then releases held payloads for the affected `(netId, typeId)` pairs.

**Rejected or timed out** (10 s).

- Predicted creates get `Deleted`.
- The client sends a `RepairRequest` for every updated or deleted original. The host answers with full records in the World stream (flagged `Repair`), which re-create or restore those entities through the normal upsert path.
- A toast explains the reason: "Your edit conflicted with Alice's change", "Not enough money", or "No permission".

**Held payloads.** While a proposal is pending, incoming host values for the pairs it touches are buffered rather than applied. This prevents visible flicker back to the old value. Only the newest buffered value is applied after the result arrives.

### 4.7 Prediction and preview details

- **Previews.** Local Temps are never replicated or touched. `ValidationSystem` validates them against the replicated world, so preview errors match the host world (R11 §6.5).
- **Locally predicted entities miss host-only extras until the commit arrives:**
  - default policies (`DefaultPoliciesSystem` OFF);
  - random name index (`RandomLocalizationInitializeSystem` OFF);
  - parked service vehicles (`ParkedVehiclesSystem` OFF).
  These arrive with the host's records one RTT later.
- **Viewer role (no prediction).** `ApplyGateSystem` rewrites the active tool's `applyMode` from Apply to Clear just before `ToolOutputSystem`, through the protected setter (`ToolBaseSystem.cs:182` [V\*]; reflection probe A4). `ToolClearSystem` then clears the Temps cleanly (R10 §7.4).
  - **Fallback if the probe fails:** viewers' local applies are classified as leaks and reverted.
- **Continuous drawing** (net tool keeps the last node) works naturally, because the prediction runs the real tool state machine (R10 §7.3).

### 4.8 Conflicts

The concurrency model is **optimistic, host-ordered and atomic per proposal**.

- **The host player's actions win by happening first.** They bump `rev_struct` without a round trip.
- **Two clients editing the same entity:** the second proposal's `baseEpoch` predates the first commit, so it is rejected and that client rolls back (§4.6).
- **Zoning:** conflicts are checked per `BlockKey` revision.
- **Economy setters** (taxes, budgets, fees, loan) are last-writer-wins. They are idempotent state edits, so no revision check is needed.
- Rejections are expected to be rare in co-op. Each one is shown as a toast and counted in diagnostics.

---

## 5. Follower simulation on clients

### 5.1 Clock: replace the driver, never run `GameSimulation`

**Host side.**

- `SimulationSystem` runs normally.
- `SpeedAuthoritySystem`, registered `UpdateBefore<X, SimulationSystem>(LateUpdate)`, owns `SessionClock{speed, paused}`.
- **Host speed changes.** When `selectedSpeed` changed since the last write *and* the vanilla pause barrier is inactive, the change becomes the new session speed. The barrier state is read through probe A3: `TimeUISystem.m_SimulationPausedBarrierBinding.observerCount`, `TimeUISystem.cs:73` [V\*]; `EventBindingBase.observerCount` is public.
- **Every frame** it writes the session speed back. That neutralises host-local writers such as lightning, photo mode and the focus barrier (R05 §9.2), unless the session option *PauseWhenHostInMenu* is set.
- It broadcasts `ClockSync{hostFrame, frameTime, smoothSpeed, speed, paused, sendTime}` unreliably at 30 Hz or less.

**Client side.**

- **While loading,** vanilla `SimulationSystem` runs. `pausedAfterLoading` is forced to `true` in memory so that no local tick can run on the first frame (`SimulationSystem.cs:202-219` [V\*], R12 §5.3).
- **From `OnGameLoadingComplete` on (catch-up and live),** `SimulationSystem.Enabled = false`. `ClientClockSystem`, registered `UpdateAfter<X, SimulationSystem>(LateUpdate)`, does the following each frame:
  1. Run `ClockController` (Core): offset and RTT estimate, jitter EWMA, and lag `D = clamp(ceil((oneWay + 2·jitter)·60·speed) + 8, 16, 160)` ticks (R15 §2.6). The target is `estHostFrame − D`.
  2. `ticks = clamp(target − frameIndex + carry, 0, 16)`. A gap larger than 1024 ticks causes a hard jump.
  3. `UpdateSystem.Update(PreSimulation)`. Then for each tick: `frameIndex++` (probe A1: private setter, `SimulationSystem.cs:68` [V\*]), followed by `CosmeticRunner.Tick(frameIndex, i)`. Then `Update(PostSimulation)`, which runs the ECS part of `WaterSystem` (`SystemOrder.cs:631`).
  4. Set `frameTime` and `smoothSpeed` (probe A1), and set `selectedSpeed = paused ? 0 : speed` through the public setter. `RenderingSystem` freezes interpolation at speed 0 and extrapolates with `smoothSpeed` (`RenderingSystem.cs:186-210` [V\*]).
- **Fallback if probe A1 fails:** set `frameIndex` through the public `SimulationSystem.Deserialize(netReader)` with a 4-byte payload. It resets `frameTime`/`m_Timer` (`SimulationSystem.cs:140-146` [V\*]), so motion is less smooth but still correct.

**`CosmeticRunner`** calls `system.Update()` directly on an allow-list resolved by name: `TimeSystem`, `ClimateSystem`, `SnowSystem`, `WindSimulationSystem`, `WindSystem`, `ZoneAmbienceSystem`, `SpawnableAmbienceSystem`, `TrafficAmbienceSystem`, `WetnessSystem`, `DirtynessSystem`, `StreetLightSystem` (R11 §3, §8.2).

- It honours each system's `GetUpdateInterval(GameSimulation)`, with offset `= hash(typeName) & (interval−1)`. The phase does not matter on a follower.
- Adapters can append cosmetic mod systems.
- **Everything else in `GameSimulation` never runs on clients,** including game-patch additions and mod simulation systems (Water Features, Tree Controller seasons, Plop `SpawnedBuildingSystem`).
- **Belt and braces:** all *other* systems registered in `GameSimulation` also get `Enabled = false`, so producers guarded by `Enabled` stop enqueueing (R11 §4 R-a). The list comes from probe A7 (`UpdateSystem.m_Systems` phase data), with a declared-list fallback. `GameModeGovernmentSubsidiesSystem` is excluded because its `Enabled` flag is UI state (R14 §0.7, §10.2).

**Pathfinding back-pressure** disappears on clients: the remaining queries use `uint.MaxValue` deadlines, and the replacement driver ignores `pendingSimulationFrame` (R11 §7.2).

### 5.2 Gate table (client), data-driven

`gates.json` is embedded, and every entry is resolved by type name at session start. If a *critical* entry fails to resolve, the self-test fails and the client refuses to join with a clear message. A non-critical failure is logged as a warning.

**Re-assertion.** `GateSystem` re-asserts the table every frame (inside `NetApplySystem`) and after every load, because several systems re-enable themselves in `OnGamePreload` (R11 §8.3).

**Mechanism.** `Enabled = false` is preferred over skipping `OnUpdate`. `SystemBase.Update` then skips the system and calls `OnStopRunning`, which disposes queue lists (R03 §7, R11 §8.3).

| Phase | OFF on clients | Notes |
|---|---|---|
| ToolUpdate | `UpgradeDeletedSystem` | would duplicate the host-replicated rebuilt sub-nets and sub-areas (R13 §5) |
| Modification1 | `AddMeetingSystem` | `LoanSystem` stays **ON** as the predictor (§4.4) |
| Modification2 | `Game.Events.InitializeSystem`, `OutsideConnectionInitializeSystem`, `DamageSystem`, `DestroySystem` | random targets and command events (R11 §2.4) |
| Modification3 | `DefaultPoliciesSystem` | appends defaults (R10 §4) |
| Modification4 | `HouseholdInitializeSystem`, `Game.Routes.InitializeSystem`, `ImpactSystem`, `IgniteSystem`, `FaceWeatherSystem`, `AddHealthProblemSystem`, `SpectateSystem`, `EndangerSystem`, `AddAccidentSiteSystem`, `AddCriminalSystem`, `SubmergeSystem`, `TripResetSystem`, `EventJournalInitializeSystem` | R11 §2.7 |
| Modification4B | `ObjectEmergeSystem`, `ParkedVehiclesSystem` | §4.5 explains the parked-vehicle split |
| Modification5 | `ZoneBuiltRequirementSystem`, `ResourcesInitializeSystem`, `ParkInitializeSystem`, `CitizenInitializeSystem`, `HouseholdPetInitializeSystem`, `MeetingInitializeSystem`, `Game.Citizens.CompanyInitializeSystem`, `StorageInitializeSystem`, `EventJournalSystem`, `GroupSystem`, `FixParkingLocationSystem`, `FixLaneObjectsSystem` | R11 §2.8, R15 §4.6 |
| ModificationEnd | `ZoneCheckSystem`, `ServiceRequestSystem`, `XPBuiltSystem`, `NetXPSystem`, `XPSystem`, `MilestoneSystem` (+ UI field feed, §5.4), `TriggerSystem`, `LifePathEventSystem`, `CreateChirpSystem`, `RadioTagSystem`, `StrictObjectBuiltRequirementSystem`, `ObjectBuiltRequirementSystem`, `PrefabUnlockedRequirementSystem`, `DevTreeSystem`, `RandomLocalizationInitializeSystem`, `NotificationTriggerSystem`, `ParkingLaneDataSystem`, `PathOwnerTargetMovedSystem`, `BoardingVehicleSystem`, tutorial systems | R11 §2.9, R14 §5.5 option A |
| Modification1 (config) | `UnlockAllSystem` | its `UnlockAllMilestones` adds rewards locally (R14 §2.4) |
| LateUpdate | `AutoSaveSystem`; `SimulationSystem` once loading completes; debug actions | R12 §6.2 |
| GameSimulation / EditorSimulation / LoadSimulation | never run (§5.1) | — |
| Mod systems | per-adapter entries (§9.3) | — |

**KEEP (derived or infrastructure):**

- all `Generate*`, reference, geometry, composition, lane, block, cell-check, sub-object and secondary systems;
- `UpdateGroupSystem`;
- `Vehicles`/`Creatures` `Initialize` and `References` (with the mover rules);
- `IconCommandSystem` (**mandatory**: its queues would leak otherwise, `IconCommandSystem.cs:727-731`);
- `UnlockSystem`: processes synthesized `Unlock` events. Its requirement fixed point equals the host's when requirement buffers match (mod-settings parity); otherwise the D-PREFAB audit repairs it;
- `ModifiedSystem` (input-starved except for local predictions);
- `CityServiceBudgetSystem` (on replicated inputs, verified);
- `GameModeGovernmentSubsidiesSystem`, `CitySystem`, `GroundHeightSystem`, `ValidationSystem`, the tools.

**Startup self-check:**

1. Every gate name resolves.
2. Every consumer that is OFF has all of its known producers OFF. Pairs from R11 §4:
   - `TriggerSystem` ↔ `CitizenInitializeSystem`, `AddHealthProblemSystem`, `Game.Events.InitializeSystem`, `NotificationTriggerSystem`;
   - `CityStatisticsSystem` ↔ `AddHealthProblemSystem`;
   - `XPSystem` ↔ `XPBuiltSystem`, `NetXPSystem`;
   - `PropertyProcessingSystem` ↔ `CompanyInitializeSystem`.
3. `IconCommandSystem` is ON.
4. Game-assembly systems that appear in neither the KEEP list nor the OFF list are reported as **unclassified**. This is post-patch maintenance information, not an error.

### 5.3 What clients still run

- the full Modification pipeline, which derives geometry, lanes, blocks, sub-objects, aggregates, flow graphs and search trees from replicated sources;
- rendering, animation, effects, audio and interpolation;
- the cosmetic allow-list;
- local tool previews and predicted applies;
- local pathfinding for previews and route display (deadline-free);
- UI.

### 5.4 Avoiding double application of events and rewards

| Hazard | Rule |
|---|---|
| Milestone rewards (money, creditworthiness, dev points) | State is replicated (City entity). `MilestoneSystem` and `DevTreeSystem` are OFF. For each step of `MilestoneLevel` the client synthesizes `Event+MilestoneReachedEvent(milestonePrefab(i), i)` before `ModificationSystem`, so `MilestoneUISystem` refreshes the list and shows the popup according to each player's own setting (R14 §1.5). `MilestoneSystem`'s UI fields `m_LastRequired/m_NextRequired/m_Progress/m_NextMilestone` (`MilestoneSystem.cs:17-23, 41-51` [V\*]) are fed every frame through probe A2. |
| XP from building | `XPBuiltSystem`, `NetXPSystem` and `XPSystem` are OFF. XP toasts are mirrored: the host peeks `XPSystem.m_XPMessages` (probe A6, `NativeQueue.ToArray`), sends `XpToast` records, and clients call `MilestoneUISystem.AddMessage` (public, R14 §1.4). |
| Unlocks | Clients never write `Locked` for unlocks. They create `Event+Unlock(prefab)`, and `UnlockSystem` flips the bit idempotently so that every UI consumer refreshes (R14 §2.3-2.4). Relock (rare): write the bit directly, then use the refresh trick from R14 §2.4 step 4 (to be verified in game). |
| Policies | The host sends no `Modify` events. Policy and modifier buffers and option masks travel as state (R10 §4, R11 §9 R10). |
| Initializer randomness and appends | The initializers in the OFF list do not run (R11 §2.11), so host post-initialization values persist and no re-apply pass is needed (R11 §6.4). |
| Command events (`Damage`, `Destroy`, `Ignite`, …) | Never replicated. Their effects arrive as state. |
| Journals | `EventJournalInitializeSystem` and `EventJournalSystem` are OFF. Journal entities are replicated. A client companion keeps `EventJournalSystem.eventJournal` up to date and fires its callbacks (R14 §6.1; M4). |
| Chirps, life path | Generators are OFF. Entities are replicated, including `RandomLocalizationIndex` (the initializer is OFF, so there is no re-roll; R14 §5.4). |
| Parked service vehicles | `ParkedVehiclesSystem` is OFF on clients. The host spawns them, and they replicate as movers. |
| Upgrade deletion regeneration | `UpgradeDeletedSystem` is OFF; the host replicates the result. |
| Movers | Never tagged `Updated`. Created with `Applied` and the host `UpdateFrame` (R15 §4.4). |
| Tutorials | Disabled on every peer for the session: `SharedSettings.gameplay.showTutorials = false` in memory, restored on leave. This removes tutorial `Unlock` leakage, the map-tile unlock by tutorial (`TutorialSystem.cs:308`), and the host's intro gate on milestones (`TutorialSystem.cs:233`, `MilestoneSystem.cs:73-76` [V\*]). Per-player tutorials are a later option (R14 §8.4). |
| Achievements | Already inert through the vanilla `usedMods` gate (R14 §7). Left untouched; the UI explains it. |

### 5.5 Host-side session adjustments

1. Tutorials are off for the session (above).
2. Host saves (manual, quick and auto) stay enabled. They are serialized through `TaskManager` `"SaveLoadGame"` so they never collide with a join snapshot. Each save is announced to clients ("Host is saving") and suppresses timeouts for its duration (R12 §6.1).
3. `SpeedAuthoritySystem` is active (§5.1).
4. After every host save or snapshot, the slice-scan false-positive burst caused by `TrimPathsSystem` is rate-limited (R12 §10.9).

---

## 6. Join flow

### 6.1 Sequence

| # | Host | Client | Messages / APIs |
|---|---|---|---|
| 1 | Creates a Steam lobby (FriendsOnly/Private/Public) and a P2P listen socket. Direct mode also opens an IP listen socket. | Joins through the lobby, an invite, `+connect_lobby`, or a direct address. | §8 |
| 2 | — | `Hello{protocol, modVersion+dllHash, Version.current (m_Version + fullVersion), FormatTags hash, code-mod/UI-module manifest, DLC set, catalog fingerprint, prefab fingerprint, mod-settings fingerprints, Steam identity}` | Control |
| 3 | `CompatibilityChecker` (Core) compares the manifests. Reply: `Welcome{sessionId, slot, role, session settings}` or `Refuse{MismatchReport}` | Shows the report (§6.7). | Control |
| 4 | If the host's baseline (first full scan of every domain, §3.2) is not done, it is finished first. Typically 5–15 s, once per session. | "Waiting for host to prepare" | — |
| 5 | Enqueues the snapshot task on `TaskManager` `"SaveLoadGame"` (R05 §4.1). The pre-snapshot bind pass runs. The current epoch is closed as `E`. Then `SaveGameSystem.stream = FileStream(temp)`, `context = new Context(Purpose.SaveGame, Version.current, Hash128.Empty)`, `await RunOnce()` (`SaveGameSystem.cs:22-84` [V\*]; not `GameManager.Save`, which garbage-collects, packages and moves "Continue", R12 §4.3). `referencedContent` is read afterwards. | "Host is preparing the city (≈x s hitch)" | Control broadcast `HostBusy` |
| 6 | `SnapshotManifest{id, bytes, sha256, chunk = 256 KiB, epoch E, frameIndex F0, gameModeName, climate/water-render/terrain-render PrefabIDs, referenced content names, per-family NetId counts}` | Pre-check: `ArePrerequisitesMet(contents)` (`GameManager.cs:1847`), `TryGetPrefab` of the three render/climate IDs (R12 §4.3 gap). Replies `Ready` or `Refuse`. | Control |
| 7 | Starts the joiner's backlog cursor at epoch `E` in the shared `EpochLog` ring. Streams chunks over the Bulk lane, keeping ≤ 4 MiB in flight. | `AssetDatabase.GetTransient(0, <modTemp>/<sessionId>)` → `AddAsset<SaveGameData>("SkylineTogetherJoin")` → writes chunks into `GetWriteStream()` in order (R12 §4.3). Checks each chunk's xxHash64. | Bulk |
| 8 | Keeps playing. The World epochs for the joiner accumulate (cap 64 MB or 10 min, otherwise restart from step 5). | Buffers epochs in memory, spilling to a temp file above 32 MB. | World |
| 9 | — | Verifies SHA-256. Then sets: `CityConfigurationSystem.overrideLoadedOptions = false`, `overrideThemeName = null`, `GameModeSystem.overrideMode = host mode`, `MapMetadataSystem.mapName`, `PlatformManager.achievementsEnabled = false`, `SharedSettings.gameplay.pausedAfterLoading = true` (in memory only) (R12 §4.2). Arms `GateSystem` for `OnGamePreload` and arms the pre-load sweep (R12 §3). Enqueues `GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, saveGameData)` on `"SaveLoadGame"` (`GameManager.cs:1162` [V\*]). | — |
| 10 | — | Deserialize phase: `ClearSystem`, the sweep, then `SerializerSystem`, which includes `NetIdSystem.Deserialize` binding the NetIds. Then `PostDeserialize<NetIdSystem>`, then `OnGameLoaded`, which rebuilds all derived data (R04 §0.7). | — |
| 11 | — | `OnGameLoadingComplete(LoadGame, Game)`: restore `pausedAfterLoading`, disable `AutoSaveSystem`, disable `SimulationSystem`, enable `ClientClockSystem`, restore the client camera (the host camera arrives with the save, R12 §4.2), dispose the transient DB, run the post-load checks (§6.5). | — |
| 12 | — | **Catch-up:** applies backlog epochs (≤ 6 ms or 200 epochs per frame) and runs the initial canonical-hash pass (≈ 8 ms per frame budget) at the same time. The overlay reads "Catching up 63 %". | World |
| 13 | For each domain whose first full scan cycle *after* `E` has completed, sends `DomainDigest` pages tagged with the current epoch. | Compares the digests at the matching epoch and requests repairs (§7). | World |
| 14 | Marks the client **Live** once its acknowledged epoch is ≥ head − 30. Its view reports are then included in interest management. | Enables proposals and tools. The HUD shows "Synced". | Control |

### 6.2 Snapshot production (host)

- **One snapshot service.** Every snapshot is serialized on the `"SaveLoadGame"` queue, because `SaveGameSystem` keeps single-slot state (R12 §10.8).
- **Shared snapshots.** Joiners arriving within 20 s share one snapshot. Each joiner gets its own backlog cursor into the shared ring.
- **The stall is real.** `WaterSystem.Serialize` blocks on GPU readbacks (R12 §6.1). Clients see `HostBusy` instead of a lag spike, and the timeout watchdogs are suspended.
- **Snapshot boundary.** `TagSweepSystem` runs immediately before `SaveGameSystem` in the same MainLoop (§3.6), so every saved tracked entity has a NetId. Changes in the snapshot frame that also travel in epoch `E` are idempotent (P2).
- **Cleanup.** The temp file is deleted once every joiner of that snapshot is live or has left.

### 6.3 Transfer (`Core.Transport.BlobSender` / `BlobReceiver`)

- Chunks are 256 KiB, below the 512 KiB Steam message cap with headroom (R06 §5.4).
- Each chunk carries an xxHash64. The whole blob is checked with SHA-256.
- **Window:** the sender keeps `m_cbPendingReliable` ≤ 4 MiB and tops up from the network thread.
- **Resume:** a missing-ranges bitmap covers reconnects.
- **Rate:** whatever Steam's bandwidth estimate allows. Progress and an ETA are shown to both sides.
- 200 MB takes 40–100 s at 2–5 MB/s (R06 §5.4, R12 §6).

### 6.4 Loading on the client: why the public path

Everything in step 9 uses public members:

- `Load(GameMode, Purpose, IAssetData)` accepts a `SaveGameData` from a file-backed transient DB, fires `onGamePreload`, `OnGameLoaded` and `onGameLoadingComplete`, and never touches "Continue" (R12 §4.1).
- **Game mode must be set explicitly.** `GameModeSystem` runs *before* `SerializerSystem`, so without `overrideMode` the client would apply a stale mode (R12 §4.2).
- **No local tick on the first frame.** `pausedAfterLoading = true` in memory prevents it (R12 §5.3).
- **Gates are active from the first frame.** `GateSystem` applies the gate table in its own `OnGamePreload` and keeps re-asserting it. No gameplay system ever runs on the client, even during the load frames.
- **Pre-load sweep.** It destroys entities matched only by non-Game serializable types and not cleared by `ClearSystem`, such as third-party mod-only entities left over from the client's previous city (R12 §3). Skyline Together itself creates none (P8).

### 6.5 Catching up and verification

- **Backlog.** Epochs apply in order, and records are idempotent upserts. Values already in the snapshot are harmless; that is P2 again.
- **Post-load checks.**
  - Prefab placeholder count: instances whose prefab has a negative `PrefabData.m_Index`, R12 §5.4. It must be 0.
  - NetId counts per family against the manifest, within ±1 %. Exact equality is checked later by the digests.
  - A `currentModeName` check (R14 §10.1).
  - The unlock bitset hash.
- **Initial hashing.** The client computes canonical hashes for every tracked entity, budgeted. These become the host-confirmed baseline.
- **Verification timing.** It waits until the host has completed one full scan cycle per domain after `E`. Until then the host's last-sent hashes may lag its live state (§7.2). In the meantime the client is "Live (verifying)" and fully playable.

### 6.6 Multiple joiners, rejoin, slow links, host loss

| Situation | Handling |
|---|---|
| Multiple joiners | One snapshot is shared by joiners arriving within 20 s. At most 2 transfers run concurrently and fair-share the Bulk budget. Each joiner has its own `EpochLog` cursor. |
| Rejoin | If the transport drops for less than 60 s and the backlog is still in the ring, the client resumes from its last acknowledged epoch without reloading (its world is intact). Otherwise it does a fresh snapshot join. |
| Slow links | The ETA is shown. If the backlog exceeds 64 MB or 10 min, the host restarts with a fresh snapshot (R12 §5.2). |
| Host loss | No live migration (R12 §8.3). **Resume point:** with the host's opt-in, the host pushes its latest save to clients over idle Bulk bandwidth every 30 min (M7). Any peer can then host from that file, and its `NetIdSystem` block preserves identities. |

### 6.7 Mismatch detection

| Field | Source | Rule |
|---|---|---|
| Protocol version, mod version, mod DLL SHA-256 | constants; the mod folder via `ModManager.TryGetExecutableAsset` (R05 §1.3) | exact |
| Game build | `Game.Version.current` `m_Version` + `fullVersion` (R12 §7) | exact |
| Format tags | names of `FormatTags` (`FormatTags.cs` [V\*]) | exact |
| Code mods + UI modules | `ModManager` enumeration and `ListModsEnabled()`, with versions, PDX ids and DLL hashes | **exact set** (hard constraint 6). Optional host relaxation: an explicitly local-only allow-list (§9.2) |
| DLC / content | `PlatformManager.IsDlcOwned`; `SaveGameSystem.referencedContent` → `ArePrerequisitesMet` (R12 §7) | must pass |
| Type catalog fingerprint | §2.8 | exact (captures struct layouts) |
| Prefab fingerprint | §2.6 | exact. On mismatch the host sends its list and the client reports missing and extra IDs per source mod or asset pack |
| Render/climate prefabs | PrefabIDs from the snapshot manifest | must resolve (otherwise "map package content missing", R12 §4.3) |
| Mod settings fingerprints | probe A5 over `ModSetting.instances` (internal static, `ModSetting.cs:17` [V\*]): canonical JSON of public read/write properties, excluding `ProxyBinding`s | **block** on simulation-relevant settings declared by adapters (§9.3); **warn** on others; the host may push its values (adapter opt-in) |

**The `MismatchReport`** is built in Core (testable) as a list of `{category, item, hostValue, clientValue, fix hint}` entries. The UI shows it grouped by category, with copy-to-clipboard, a list of PDX mod ids for Skyve, and an optional "Subscribe missing mods" button through `PdxSdkPlatform.SubscribeAll` (explicit user action plus restart, R06 §8).

---

## 7. Divergence detection and repair

### 7.1 Hashes and buckets

- **Entity hash.** §3.4: computed over canonical `ISerializable` bytes, the signature and the enable bits.
- **Buckets.** Domain *d* has `B_d = nextPow2(max(256, entityCount/64))` buckets. Entity *e* falls into bucket `mix32(netId) & (B_d−1)`.
- **Bucket hash** = Σ entityHash (mod 2⁶⁴). It is order-independent (reverse-buffer order cannot matter, R04 §5) and incrementally updatable (add, subtract, replace).
- **Domain digest** = xxHash64 of the bucket vector.
- **Host side:** bucket hashes describe the *last-sent* state and are maintained incrementally whenever a record is emitted.
- **Client side:** bucket hashes are maintained incrementally when records are applied, plus live recomputation for audits.

### 7.2 Exchanges

| Message | Direction | When | Content |
|---|---|---|---|
| `Heartbeat` | host → all (World lane) | every 2 s | `epoch`, `domainDigest[d]` for all domains (~20 × 8 B) |
| `DigestPage` | host → all | on mismatch or rotation; one page per second | `domain, page, epoch, bucketHash[256]` |
| `BucketDetailRequest` | client → host | client bucket ≠ host bucket at the same epoch | `domain, bucket, epoch` |
| `BucketDetail` | host → client | reply | `[(netId, entityHash)]` from the last-sent store |
| `RepairRequest` | client → host | entity mismatch, missing entity, leak, stuck pending reference | `netIds[]` (≤ 512 per message) |
| Repair records | host → client (World lane, or Bulk for big sets) | reply | full `Create` records (upserts) flagged `Repair` |

**Epoch alignment.** Digests and details carry their epoch. The client evaluates them right after applying exactly that epoch and before the next one, inside `NetApplySystem`.

**Two complementary checks:**

1. **Client integrity.** Incremental client buckets catch lost or misapplied records. A **rolling live recomputation** of one client bucket per frame (≈ 64 entities × 5 types × 0.5 µs ≈ 0.2 ms) catches untagged local leaks that the capture engine missed.
2. **Host coverage.** The slice scanner already re-hashes live host state each cycle and emits whatever was missed. A completed cycle gives a coverage guarantee: after one cycle, last-sent equals live as of scan time.

**Special domains:**

| Domain | Check |
|---|---|
| Terrain | 64×64 tile hashes, rotating audit through `AsyncGPUReadback` on both sides, no stalls (R07 §5.2) |
| Cell maps | per-map hash |
| Prefab state | bitset hash every 30 s (R14 §2.4) |
| Names | hash of the canonical pair list |
| Icons | per-owner icon-set hash domain |
| Zones | per-`BlockKey` zone hash domain |
| Water, motion | excluded from exact audits; corrected by periodic snapshots and the motion stream |

### 7.3 Repair ladder

| Level | Trigger | Action |
|---|---|---|
| 0 Leak repair | Client-side divergence outside an action window (§4.2) that persists for 2 frames | `RepairRequest` for those NetIds |
| 1 Entity repair | `BucketDetail` diff shows ≤ 64 entities | `RepairRequest`. Extras (bound on the client but absent on the host) get `Deleted` locally |
| 2 Bucket rebaseline | > 64 mismatches in one bucket | the host sends every record of the bucket |
| 3 Domain rebaseline | > 10 % of a domain's buckets mismatched, or > 3 bucket rebaselines per minute in a domain | the host streams all records of the domain over Bulk (e.g. 110k citizens ≈ 10 MB); the client upserts and deletes extras |
| 4 Full resync | apply exception (Faulted); World-lane gap or protocol error; > 1000 pending references older than 600 epochs; ≥ 2 domain rebaselines within 10 min; catalog or prefab dictionary change; host loaded another city; user request | snapshot reload (§6), the same path as a join |

**Tolerance.** An entity/type that still mismatches after 2 repair attempts is put on a tolerated list for 10 min and logged with full details. Typical causes are DerivedKeys that will not resolve (R13 §2.4 node lanes) or sub-centimetre `GroundHeightSystem` corrections when GPU rasterisation differs by 1 LSB (R07 §5.3, R11 §9 R7). Tolerance prevents repair storms. If more than 1 % of a domain is tolerated, the domain escalates to level 3.

**Leak attribution for maintenance.** `LeakTracker` keeps counts per component type and per family. The diagnostics dump lists the top leaking types. After a game patch, that list points at the gameplay system that is missing from the gate table.

### 7.4 Faults fail into a safe state

- **Every Game-layer system** wraps `OnUpdate` in try/catch. On a fault it logs with context, increments a counter, and moves the session to `Faulted`.
- **Client fault:** automatic full resync with exponential backoff (30 s, 2 min, 10 min). After 3 failed resyncs it stops, writes a diagnostics dump, and shows a "Leave session / Report" dialog. It never keeps a corrupted replica.
- **Host fault:**
  - A **capture domain** that faults is disabled ("degraded: buildings state paused"). The HUD shows it, and clients are offered a resync.
  - A **commit** that faults rejects the proposal and logs it.
  - **Snapshot** faults are retried once, then the join is refused with the reason.
- **Self-test gates.** Critical probe or gate-table failures prevent hosting or joining, with an explanation, instead of risking corruption (§11.3).

### 7.5 Full resync procedure

1. The client announces `ResyncStart`. Its proposals are frozen and pending ones are rejected.
2. The host runs §6 steps 5–14 for that client, sharing a snapshot when possible.
3. The client reloads while its connection stays up.
4. If a full resync is needed for *everyone* (the host loaded another save, or the host's capture was reinitialised), the host broadcasts `SessionReset` and all clients rejoin from one shared snapshot.

---

## 8. Transport

### 8.1 Abstraction (Core) and implementations

```csharp
public interface ITransport : IDisposable {
    TransportKind Kind { get; }
    void Listen(ListenOptions o);  IConnection Connect(Endpoint e);
    void Poll(Action<TransportEvent> sink);          // network thread
}
public interface IConnection {
    int Id { get; }  PeerIdentity Peer { get; }
    SendResult Send(Lane lane, byte[] buf, int offset, int count, Delivery d); // copies; thread-safe
                                                                     // (no Span: Core stays netstandard2.0, no System.Memory dependency)
    ConnectionStats Stats { get; }                                    // rtt, out/in B/s, pending reliable, quality
    void Close(int reason, string text, bool linger);
}
public enum Lane : byte { Control = 0, World = 1, Motion = 2, Bulk = 3 }
public enum Delivery : byte { ReliableOrdered, UnreliableSequenced }
```

| Implementation | Assembly | Use |
|---|---|---|
| `LoopbackTransport` | Core | Tests. In-process, with an impairment model for loss, latency, jitter, reorder and duplication. |
| `SocketTransport` | Core (System.Net.Sockets; available in Mono, R06 §6) | Direct connection without Steam (LAN, non-Steam stores). TCP carries the reliable lanes over one stream with lane-tagged frames and a small priority scheduler. UDP carries Motion. A password challenge-response (HMAC-SHA256, nonce) authenticates peers. No encryption, so the UI labels it "LAN/trusted only". |
| `SteamSocketsTransport` | Game | **Primary.** `SteamNetworkingSockets.CreateListenSocketP2P(0)`/`ConnectP2P(identity)` with a poll group. Steam Datagram Relay and ICE provide NAT traversal. AES-GCM encryption and certificate authentication are on by default (R06 §5.2). |
| `SteamIpTransport` | Game | Direct-IP mode with the same Steam API (`CreateListenSocketIP`/`ConnectByIPAddress`, `IP_AllowWithoutAuth` only when needed). Still encrypted. Needs port forwarding. |

### 8.2 Lanes

| Lane | Content | Delivery | Steam lane (priority, weight) |
|---|---|---|---|
| Control | handshake, proposals, results, chat, repair requests, ClockSync (unreliable flag), view reports, session events | reliable (ClockSync/view: unreliable) | (0, 1) |
| World | epochs (fragmented above 480 KiB), digests | reliable ordered | (1, 4) |
| Motion | Q-B frames ≤ 1200 B | unreliable, no-delay | (1, 2) |
| Bulk | snapshot chunks, domain rebaselines, terrain and water tiles | reliable | (2, 1) |

- **Steam lanes** are configured through custom P/Invoke on the flat API: `SteamAPI_SteamNetworkingSockets_SteamAPI_v012` plus `SteamAPI_ISteamNetworkingSockets_ConfigureConnectionLanes`. The Steamworks.NET binding for this is broken (R06 §5.6). Priority 0 is assumed to be the highest; the exact priority and weight semantics are checked in spike 7 (§12.2).
- **Fallback** (probe A9 fails): a second connection on virtual port 1 carries Bulk only.
- **Messages** go out through `SendMessageToConnection`. The broken `SendMessages` wrapper is never used.
- **Receiving:** messages are read at fixed offsets with unsafe code, avoiding the allocating `FromIntPtr` (R06 §5.6).

### 8.3 Framing and compression

- **Message layout:** §3.5. Fragmentation applies above 480 KiB.
- **Compression:** negotiated per session; ZStd through `CompressionUtils` (native, from the game) or managed Deflate. Epoch payloads of 256 B or more are compressed. Bulk tiles are compressed by their codecs.
- **Buffers:** pooled byte buffers. Nothing is allocated per message on the hot path, because Mono's incremental GC has a 3 ms slice (R06 §1).

### 8.4 Lobby, invites, presence

- **Lobby:** `SteamMatchmaking.CreateLobby(FriendsOnly | Private | Public, maxMembers)`.
- **Lobby data:** `st.proto`, `st.game`, `st.mods` (manifest hash), `st.city`, `st.pop`, `st.players`, `st.ping` (`ConvertPingLocationToString`) (R06 §10).
- **Invites:** `ActivateGameOverlayInviteDialog`.
- **Joining:** callbacks for `GameLobbyJoinRequested_t` and `GameRichPresenceJoinRequested_t`, kept alive in fields so the GC does not collect them (R06 §3.1). On a cold start, `+connect_lobby` is parsed in `OnLoad`; whether the launcher forwards it must be verified in game.
- **Rich presence:** `connect` and `steam_player_group` only. The game owns `steam_display` and overwrites it every 5 s (R06 §4.8).
- **Steam guard:** if `SteamworksPlatform.isInitialized` is false (non-Steam store), only `SocketTransport` is offered.

### 8.5 Threading

- **Network thread** (`NetworkThread`, background, 1 ms cadence):
  - polls the poll group or sockets;
  - parses into pooled buffers;
  - pushes to per-lane `ConcurrentQueue`s;
  - drains outbound queues;
  - pumps blob windows;
  - writes received snapshot chunks to the open write stream.
- **Main thread:** consumes the queues at the start of `HostReplicationSystem`/`NetApplySystem` and in `SessionUISystem`.
- **Steam callbacks** arrive on the main thread through the game's `RunCallbacks` (R06 §2.3) and feed the session state machine.
- **ECS is never touched off the main thread.** Its thread checks are compiled out of the player build (R06 §7).
- **No `MainThreadDispatcher`,** because it is unordered (R05 §11).

---

## 9. Mod compatibility

### 9.1 Generic mechanisms (cover unknown mods)

1. **Mod components replicate automatically (P1).** Any mod `ISerializable` or `IEmptySerializable` type enters the save query, the `TypeCatalog` and the replicated set. Entity fields inside mod components are remapped through `NetWriter` (e.g. Better Bulldozer `OwnerRecord`, Traffic connection buffers; R09 §5.2).
2. **Identity does not depend on `PrefabRef`.** Mod-only persistent entities (Better Bulldozer records, Water Features sources) are tracked. Tool-local entities (MoveIt control points with `PrefabRef(Null)`) are excluded (§2.3).
3. **Enable bits, archetype changes and in-place `PrefabRef` swaps** are first-class in records (Tree Controller `Decoration`/`LumberResource`, R09 §2.8).
4. **Atomic per-entity records applied before ModificationEnd.** Mod enforcers see consistent state, e.g. Anarchy `Transform` together with `TransformRecord`, Plop's tags arriving with the building, `ZoningInfo` arriving with the edge (R09 §5.4).
5. **Mod simulation systems never run on clients.** They are `GameSimulation`, which clients never execute (§5.1).
6. **Direct-write mod tools work through client capture.** Proposals are accepted while a non-vanilla tool is active (§4.2).
7. **Local-only churn is filtered.** `Highlighted` is not serializable. `Updated`/`BatchesUpdated` written by a mod on the client without a data change produce no proposal, because hashes are equal (R09 §5.6).
8. **Load-time mutators are tolerated.** They run on the joiner with identical settings, which settings parity guarantees. Any world-state difference they cause is fixed by post-load verification and repair (§6.5, §7). Prefab-level effects match because settings match (R09 §5.7).
9. **Per-mod gate entries** cover enforcement systems outside `GameSimulation`. They are resolved by type name through `AppDomain` type lookup. No compile-time reference to any other mod exists, so a missing mod cannot cause a `ReflectionTypeLoadException` (R05 §1.3).
10. **Leak telemetry** (§7.3) exposes unknown mods that write replicated state on clients, so they can get an adapter entry.

### 9.2 Manifest and settings parity

- **Exact code-mod and UI-module set, versions and DLL hashes** (hard constraint 6). The reason is beyond data formats: a different mod set shifts `UpdateSystem.Refresh` offsets on the host and changes derivation on clients (R05 §12, R03 §2.1).
- **Optional host setting: "Allow differing local-only mods".** It applies only to a built-in allow-list of verified presentation mods (Find It, Extended Tooltip, I18N Everywhere, Unified Icon Library, Asset Icon Library, Toggleable Overlays, Employment Tracker, Cim Census, Skyve) **and** only if the mod registers no serializable types and no `GameSimulation` systems. Both conditions are checked at runtime. **Default: off (strict).**
- **Settings.** Fingerprints are generic (§6.7). Adapters declare which settings are simulation-relevant (block on mismatch) and whether the host may push its values on join. A push is applied through the property setters plus `Apply()`, in memory only, and restored on leave.

### 9.3 Per-mod adapter table (initial `adapters.json`)

| Mod | Class (R09) | Client OFF systems | Client KEEP (derived) | Sim-relevant settings (block) | Player-mutable types / notes |
|---|---|---|---|---|---|
| **Anarchy** | world-changing | `PreventOverrideSystem`, `RemoveOverridenSystem`, `SetRetainingWallSegmentElevationSystem`, `ResetTransformSystem`, `PreventCullingSystem` | `AnarchyPlopSystem` (post-processes the client's own predicted applies; reacts wrongly only in rare local-tool states, which the leak repair corrects), definition and Temp systems (tool pipeline) | `AllowPlacingMultipleUniqueBuildings`, `MinimumClearanceBelowElevatedNetworks`, network upgrade prefab options (they add prefabs) | `PreventOverride`, `TransformRecord`, `DoNotForceUpdate` are player-mutable (info-panel toggles, component tool) |
| **Better Bulldozer** | world-changing | none | `RemoveRegeneratedSubelementPrefabsSystem`, `HandleDeleteInXFramesSystem`, auto-removal systems. They delete *derived* children deterministically from replicated owner buffers and settings. | `AutomaticRemoval*`, `AllowRemoving*` | `PermanentlyRemovedSubElementPrefab` buffer and `OwnerRecord` entities replicate generically (Entity refs remapped) |
| **Move It** | direct writes | none (`MIT_RemoveOverriddenSystem` is local-only) | tool and overlay systems | — | proposals through the mod-tool window. Control points excluded (`PrefabRef` null). The undo queue is local; undo results travel as new proposals |
| **529 Tiles** | load-time + economy patch | none | `PostDeserialize<FiveTwentyNineSystem>` runs with parity (milestone prefab data must match); post-load audit repairs tile `Native` | all tile and upkeep options | its Harmony cost patch affects only predicted cost; the host is authoritative |
| **Historical Start** | load-time prefab | none | `HistoricalStartSystem` with parity (keeps `UnlockRequirement` rewrites identical) | all `Unlock*` options | unlock bits verified by D-PREFAB |
| **Line Tool** | vanilla pipeline | none | all | — | captured by ApplyTool prediction; resolved points and seeds arrive as result state |
| **Plop the Growables** | world-changing | `PloppedBuildingSystem`, `SelectiveZoneCheckSystem` | `ExistingBuildingSystem` (load-time, parity) | `SpawnedZoneDespawn`, `LockPloppedBuildings` | `LevelLocked` is player-mutable (info-panel toggle). Settings buttons (bulk mutations) are host-only and disabled on clients |
| **Tree Controller** | world-changing | `LumberAndPauseTreeGrowthSystem` (Rendering phase), `DestroyFoliageSystem` | tool, definition and Temp systems; its `GameSimulation` systems never run | `DisableTreeGrowth`, `UseDeadModelDuringWinter`, `FreeVegetation` | brush is a mod-tool proposal (enable bits and `PrefabRef` swaps). Optional cosmetic `ReloadFoliageColorDataSystem` on the allow-list |
| **Water Features** | simulation | none outside `GameSimulation` | custom water tool (proposals) | evaporation, fluidness, damping, speed, `WaterCausesDamage`, tides/streams | sources replicate generically (no `PrefabRef`). Global water parameters via D-WATERPARAMS. Its `IDefaultSerializable` systems: compare-only, opt-in sync after verification |
| **Zoning Toolkit** | world-changing | none | `ZoningToolkitModSystem` (Mod4B block sizing, derived from replicated `ZoningInfo`) | — | `ZoningInfo` arrives in the edge record. The tool is a mod-tool proposal |
| **Traffic** | lane replacement | none | `TrafficLaneSystem` etc. (derive lanes) | all | data entities and buffers replicate generically. **To verify in game:** whether `GeneratedConnection` references lanes, which would need DerivedKey `Lane` support (R13 §2.5) |
| **Road Speed Adjuster** | unknown | none (generic) | — | all settings (generic block) | treated as world-changing until inspected; generic coverage applies |
| Find It, Extended Tooltip, I18N Everywhere, Unified/Asset Icon Library, Skyve, Toggleable Overlays, Employment Tracker, Cim Census | local-only | none | all | — | no adapter needed. Data panels show replicated state |
| China Pack | content | — | — | — | covered by the prefab fingerprint |

### 9.4 Adapter format

```json
{ "mod": "Anarchy", "assembly": "Anarchy",
  "clientOff":   ["Anarchy.Systems.OverridePrevention.PreventOverrideSystem", "..."],
  "clientKeep":  ["Anarchy.Systems.OverridePrevention.AnarchyPlopSystem"],
  "cosmetic":    [],
  "playerMutableTypes": ["Anarchy.Components.PreventOverride", "Anarchy.Components.TransformRecord"],
  "simSettings": ["AllowPlacingMultipleUniqueBuildings", "MinimumClearanceBelowElevatedNetworks"],
  "pushSettings": false,
  "systemStateSync": [] }
```

Adapters are embedded resources and can be overridden by a file in the mod folder, so they can be updated without a rebuild. They are pure data, parsed and validated in Core and unit-tested.

---

## 10. UI/UX

### 10.1 How it is built: a hand-written `.mjs` is feasible

- **Module header.** The game loads every `.mjs` whose first block comment contains the line `Cities: Skylines II UI Module` followed by `Id:`, `Author:`, `Version:` and `Dependencies:` (`UIModuleAsset.cs:43-110` [V\*]). The folder is registered as `coui://ui-mods/` (R05 §7.3).
- **Libraries are browser globals.** The community toolchain declares `externalsType: "window"`, with `react` → `window.React` and `cs2/api`, `cs2/ui`, `cs2/l10n`, … → `window["cs2/…"]` (third-party `Anarchy/UI/webpack.config.js` [V\*]). A module written without a bundler can therefore read the same globals.
- **Toolchain:** none. JSX is replaced by `React.createElement`, and styles are inline or injected through a `<style>` element. This removes the Node/webpack toolchain dependency (hard constraint 1 spirit).

```js
/*
 * Cities: Skylines II UI Module
 * Id: SkylineTogether
 * Author: SkylineTogether
 * Version: 0.1.0
 * Dependencies:
 */
const React = window.React, api = window["cs2/api"], h = React.createElement;
const session$ = api.bindValue("skylineTogether", "session", null);   // RawValueBinding JSON
function CoopHud() {
  const s = api.useValue(session$);
  if (!s || s.state === "Idle") return null;
  return h("div", { style: hudStyle }, h(StatusChip, { s }), h(PlayerList, { s }), h(Chat, { s }));
}
export default function register(moduleRegistry) {
  moduleRegistry.append("Menu", CoopMenuEntry);   // Host / Join panel in the main menu
  moduleRegistry.append("Game", CoopHud);         // in-game HUD
}
```

- **Graceful degradation.** `cs2/ui` components (Panel, Button) are used when present, otherwise plain styled `div`/`button`. If the module fails, the C# side still works: notifications appear through `NotificationSystem.Push`, which works in every mode (R05 §8.1), and settings stay in the Options page.

### 10.2 Screens and features

| Area | Content | C# side |
|---|---|---|
| **Main menu** ("Co-op" entry, `append("Menu")`) | Host tab: pick a save, lobby type, max players (2–8), password, role defaults, speed policy, "allow clients to build". Join tab: friends' lobbies (`lobbies` binding: city, population, players, ping estimate from `st.ping`), direct IP:port + password. A compatibility pre-check before connecting. | `SessionUISystem` (`UpdateAt<…>(UIUpdate)`, `GameMode.All`, own menu/game gating because the first boot menu has no preload, R05 §7.2) |
| **In-game HUD** (`append("Game")`) | Player list (name, role, ping, state: downloading / catching up / live / resyncing); status chip (Synced ✓ / Catching up 63 % / Verifying / Repairing n / Resyncing / Host saving / Desync → auto-fixing); session speed control (sends `SpeedRequest`); "Invite" (Steam overlay); Leave | `RawValueBinding("skylineTogether","session")` (patch updates) |
| **Chat** | Toggle hotkey (`ProxyBinding` on `ModSetting`, R05 §10); a focused text input masks game hotkeys (R05 §7.4); system messages (joins, leaves, resyncs, rejected proposals) | `TriggerBinding<string>("skylineTogether","sendChat")` |
| **Network panel** | RTT, quality, in/out KB/s, pending reliable, epoch lag, per-domain repairs and leaks, motion budget use, relay status (`GetRelayNetworkStatus`) | stats binding at 2 Hz |
| **Dialogs** | Mismatch report (§6.7), conflict and rejection toasts, resync prompts, the "session ended / resume from save?" prompt. Dialog callbacks are queued, because `appBindings` keeps a single callback slot (R05 §8.2). | `AppBindings.ShowMessageDialog` + an own queue |
| **Settings** (`SkylineTogetherSettings : ModSetting`; the class name is unique, R05 §5.3) | Display name (defaults to Steam persona), default port, upload cap, motion quality (interest radius and far-field rate), water quality, show net HUD, chat key, diagnostics level, "Write diagnostics dump" button, Steam network simulation (fake loss/lag; developer mode only, R06 §4.2) | `AutomaticSettings` page (R05 §5.4) |
| **Diagnostics** | "Copy diagnostics" / "Write dump" (JSON under `ModsData/SkylineTogether/dumps/`): session state, epochs, queues, last 1000 log lines, per-domain counters, leak table, gate and probe report, timings | `DiagnosticsSystem` |

---

## 11. Code structure and milestones

### 11.1 Solution layout

```
SkylineTogether/
  Directory.Build.props              GameManagedPath, LangVersion 9, Deterministic, TreatWarningsAsErrors
  src/SkylineTogether.Core/          netstandard2.0, NO Unity/game references        -> SkylineTogether.Core.dll
    Protocol/      ByteWriter/ByteReader, VarInt, NetRef, MessageType, RecordWriter/Reader, EpochFrame,
                   Fragmenter, ICompressor (+DeflateCompressor), ControlCodec
    Identity/      NetIdTable, NetIdMap, EntityKey, ProvisionalIdAllocator, PrefabDictionaryModel, TypeCatalogModel
    Replication/   DomainRegistry, DirtySet, ReplicationScheduler (WFQ, budgets), EpochAssembler, EpochLog (ring+cursors),
                   RecordStore (last-sent hashes, rev_struct/rev_sim), BucketDigest, CanonicalHash (xxHash64), PendingRefs
    Repair/        RepairCoordinator (ladder, tolerance), LeakTracker
    Proposals/     Proposal model, ProposalTracker (client: P-ids, held masks, timeouts), ProposalValidator (pure rules),
                   PermissionModel
    Session/       HostSession / ClientSession state machines, Handshake, CompatibilityChecker, MismatchReport,
                   SessionSettings, PlayerRegistry, ChatLog, AdapterModel (JSON parse/validate)
    Transport/     ITransport, IConnection, LoopbackTransport (+impairments), SocketTransport (TCP+UDP),
                   BlobSender/BlobReceiver, TokenBucket, BandwidthEstimator
    Time/          ClockController, JitterEstimator
    Motion/        MotionCodecQB, StagingRing, Extrapolator, InterestRegion, MotionScheduler
    Tiles/         TileGrid, TileDiff, HeightTileCodec, WaterTileQuantizer, CellMapDelta
    Diagnostics/   ILog, LogRing, Counters, DumpBuilder (JSON)
  src/SkylineTogether/               net472, references game Managed DLLs + Core      -> SkylineTogether.dll (IMod)
    Mod/           SkylineTogetherMod : IMod, SkylineTogetherSettings : ModSetting, LocaleSource, ModPaths
    Ecs/Identity/  EntityMap, NetIdSystem (IDefaultSerializable, IPostDeserialize), PrefabMap, FamilyClassifier,
                   DerivedKeys (LaneKey, SubObjectKey, BlockKey, OwnedRouteKey)
    Ecs/Serialization/ TypeCatalog, ComponentCodec + generic codecs, NetWriter (IWriter), NetReader (IReader),
                   ContextFactory (full-tag), SystemStateEntries
    Ecs/Host/      TagSweepSystem, HostReplicationSystem (SliceScanner, Pollers, HotQueries), HostCommitSystem,
                   Executors/*, MotionCaptureSystem, TerrainCapture, WaterSnapshot, CellMapCapture, PrefabStateCapture,
                   NameCapture, IconCapture, ZoneCapture, SpeedAuthoritySystem, SnapshotService
    Ecs/Client/    NetApplySystem, Applier (shared), ArchetypeResolver, ClosureTagger, EventSynthesizer, IconApplier,
                   ZoneApplier, ProposalBuildSystem, ApplyCaptureSystem, ApplyGateSystem, ClientClockSystem,
                   CosmeticRunner, GateSystem, MotionApplySystem, AggregateReconcileSystem, LateApplySystem,
                   TerrainApplier, WaterApplier, CellMapApplier, MilestoneUiFeed, JoinLoader
    Ecs/Common/    SessionRuntime (bridges Core sessions <-> ECS), FrameBudget, SelfTestRunner, ReflectionProbes,
                   DiagnosticsSystem
    Net/           SteamSocketsTransport, SteamLanesInterop (DllImport flat API), SteamIpTransport, SteamLobbyService,
                   SteamPresence, NetworkThread, ZstdCompressor (game CompressionUtils)
    Ui/            SessionUISystem
    Data/          gates.json, cosmetic.json, exclusions.json, adapters.json, sysstate.json (embedded resources)
  ui/SkylineTogether.mjs             hand-written ES module (+ optional locale JSON)
  tests/SkylineTogether.Core.Tests/  net8.0, xUnit, references Core only
  tools/build.ps1                    dotnet build -c Release /p:GameManagedPath=...; copy to %USERPROFILE%\AppData\LocalLow\
                                     Colossal Order\Cities Skylines II\Mods\SkylineTogether\
```

- **Core has no Unity dependency.** Game types appear only as opaque ids and byte payloads. That is what makes about 80 % of the logic testable on the .NET SDK.
- **Only the Game assembly contains a type implementing `IMod`.** It is top-level and implements the interface directly (R05 §1.2). There are no instance field initializers, because `GetUninitializedObject` skips them (R05 §1.3).
- **No Entities source generators or IL post-processing.** Systems are plain `GameSystemBase` subclasses using `EntityManager`, `EntityQuery` and chunk APIs. `OnCreateForCompiler` defaults to empty (`ComponentSystemBase.cs:142` [V\*]). There is no `SystemAPI`, `IJobEntity`, custom `IJobChunk` or Burst.

### 11.2 Main types and responsibilities

| Type | Assembly | Responsibility |
|---|---|---|
| `HostSession` / `ClientSession` | Core | State machines: Idle → Connecting → Handshaking → (Host: Hosting / Preparing) / (Client: AwaitingSnapshot → Downloading → Loading → CatchingUp → Verifying → Live) ↔ Resyncing, plus Faulted and Closed. Event-driven through an `ISessionEnvironment` that the Game layer implements. |
| `CompatibilityChecker` | Core | Manifest comparison producing a `MismatchReport`. |
| `EpochAssembler` / `EpochLog` | Core | Builds epochs from records, fragments them, keeps the backlog ring with per-peer cursors and caps. |
| `ReplicationScheduler` | Core | Per-domain cadence, priority classes, WFQ with aging, token buckets, slow-client policy. |
| `RecordStore` / `BucketDigest` | Core | Last-sent hashes, revisions, incremental bucket sums, digest pages. |
| `RepairCoordinator` | Core | Digest compare, detail and repair requests, the escalation ladder, tolerance. |
| `ProposalTracker` / `ProposalValidator` | Core | Client: P-ids, pending proposals, held payloads, rollback plans. Host: pure validation rules (permissions, revisions, economy, sanity). |
| `ClockController` | Core | Host-frame estimation and lag `D`. |
| `MotionCodecQB` / `StagingRing` | Core | Quantised frames and JIT slot selection maths (`F1`, slots, extrapolation). |
| `BlobSender` / `BlobReceiver` | Core | Chunked, windowed, resumable transfers. |
| `TypeCatalog` | Game | Runtime type scan, codec construction (`MakeGenericType`), eligibility, session type ids. |
| `FamilyClassifier` | Game | Archetype → family cache and per-entity owner checks. |
| `NetWriter` / `NetReader` | Game | `IWriter`/`IReader` over Core buffers, `NetRef` entity mapping, full-tag context. |
| `TagSweepSystem`, `HostReplicationSystem` | Game | Host change detection and the epoch pipeline. |
| `Applier` | Game | Shared create, upsert, signature, tag, closure and hook logic (host commit and client apply). |
| `ProposalBuildSystem`, `ApplyCaptureSystem` | Game | Client capture. |
| `ClientClockSystem`, `CosmeticRunner`, `GateSystem` | Game | Follower simulation. |
| `SnapshotService`, `JoinLoader` | Game | Join on both ends. |
| `SelfTestRunner`, `ReflectionProbes` | Game | Startup verification and degradation decisions (§11.3, Appendix A). |

### 11.3 Test strategy (no game needed until each milestone's in-game check)

1. **Core unit tests:**
   - protocol and `NetRef` round-trips and fuzzing;
   - epoch fragmentation;
   - `EpochLog` cursors and caps;
   - scheduler fairness and budget accounting;
   - digest add/subtract invariants;
   - repair ladder transitions;
   - proposal tracker (held masks, timeouts);
   - session state machines with fake environments (transfer interruption, refusal, resync loops, faults);
   - `ClockController` under jitter traces;
   - motion codec error bounds and staging-slot maths (from R15 §2.2-2.3 formulas);
   - blob transfer over the impaired loopback;
   - compatibility reports;
   - tile diff and quantisation;
   - adapter JSON validation.
2. **Replication harness (Core tests).**
   - A `FakeWorld` implements the same `IWorldAdapter` contract (enumerate tracked entities, read typed byte payloads with refs, apply records, tags) that the Game layer implements on ECS.
   - Scenario tests: churn, create-then-delete in one epoch, reference chains across epochs, lost or duplicated epochs, out-of-order delivery, client leak injection, host missed-change injection, proposal conflicts.
   - Assertion: digests converge within N epochs. The ladder escalates exactly as specified.
3. **Compile-time verification.** CI builds against the real game DLLs (`GameManagedPath`), so all public APIs used are type-checked. All non-public members are reached only through `ReflectionProbes`; nothing else uses reflection.
4. **In-game `SelfTestRunner`** (main menu and session start). It writes a report to the log and the HUD:
   - (a) probes resolve (Appendix A);
   - (b) gate, cosmetic and adapter names resolve, and producer/consumer pairs are consistent;
   - (c) catalog built, counts per kind, and eligibility of mod types;
   - (d) **codec round-trip on live entities:** for N samples per family and type, `Serialize` → `Deserialize` into a temporary value → `Serialize` must give identical bytes. No ECS writes are involved;
   - (e) system-state entries round-trip;
   - (f) Steam lane P/Invoke symbol presence;
   - (g) compressor availability;
   - (h) UI module observed (binding observer count > 0);
   - (i) unclassified systems list.
   Critical failures block hosting and joining.
5. **Chaos in game.** Steam `FakePacketLoss`, `Lag`, `Reorder` and `Dup` config values (R06 §4.2), set through settings in developer mode.
6. **Two-machine scripts** per milestone (below). Unity single-instance mode prevents two clients on one PC (R06 §1).

### 11.4 Milestones (each playable)

| M | Deliverable | Playable result | Exit criteria (in game, 2 PCs) |
|---|---|---|---|
| **M0 Foundation** | Core protocol, transport and sessions; Steam lobby + P2P + lanes (+ fallback); `SocketTransport`; handshake + `MismatchReport`; `.mjs` menu and HUD (player list, chat); settings; diagnostics; `SelfTestRunner` (catalog, probes, codec round-trip on the loaded city) | Players find each other, chat, and see an exact compatibility report | Lobby invite works; chat RTT is shown; a deliberately mismatched mod is reported correctly; the self-test is green on a 100k save |
| **M1 Join and spectate** | `SnapshotService` (`SaveGameSystem` → temp file), blob transfer, `JoinLoader` (transient DB, overrides, `pausedAfterLoading`), `NetIdSystem` persistence, `GateSystem` + `ClientClockSystem` + `CosmeticRunner`, speed authority, AutoSave off, **manual and periodic auto-resync** (stopgap) | Clients watch the host's city with synced time, weather and day/night, refreshed by resync | Join a 100k city in under 3 min. The client's replica digest stays unchanged over 10 min without host input (proves no local simulation). Resync works. No new errors in the log. |
| **M2 Live structure and city state** | `TypeCatalog`, codecs, `FamilyClassifier`, `TagSweepSystem`, `HostReplicationSystem` (structural + singletons + system state + prefab + names + zone + icons), `NetApplySystem` + `Applier` (creates, upserts, closure and tag mirroring, synthesized events), digests + repair ladder + domain rebaseline + full-resync fallback, `AggregateReconcileSystem`, first adapter entries | The host builds or bulldozes roads, zones and buildings, and clients see it live. Money, milestones, unlocks, budgets, taxes, policies and names match | Scripted host session (50 roads, upgrades, zoning, districts, policies, renames, milestone reached). All digests match. Zero repairs in steady state. A forced divergence (debug command) is repaired. |
| **M3 Clients build** | `ApplyCaptureSystem`, `ProposalBuildSystem`, `ProposalTracker`, `HostCommitSystem` + executors (§4.4), permissions, conflict handling, viewer gate, mod-tool windows | Real co-op: everyone builds, zones, sets budgets, taxes and policies | Two players build simultaneously for 30 min. Rejected conflicts roll back cleanly. Digests match. MoveIt, Line Tool and Zoning Toolkit edits from the client replicate. |
| **M4 Living city** | Slice scanner for D-BUILDING, D-AGENT, D-EVENT and D-STATS; hot queries; cell maps; journal companion; XP toasts; budget verification; chirps | Info panels, infoviews, statistics, demand, fires, crime, abandonment and chirps are consistent | Spot-check 30 info panels (buildings, citizens, companies): values match after one cycle; infoviews match; event journal matches |
| **M5 Traffic and agents** | D-MOVER, D-MOTION (capture, interest, Q-B, staging, JIT writer, extrapolation, clock lag), effects, selection, on-demand inspection closures, follow camera | Cars, trams, trains, pedestrians and pets move identically near each player; clicking and following work | Visual comparison of the same street on both screens (≤ 1 car-length error at speed 1–4); upload within budget with 3 clients; no snap-backs |
| **M6 Terrain, water, mods** | D-TERRAIN (tiles + strokes), D-WATER + D-WATERPARAMS, full adapters, settings push, Traffic and Road Speed Adjuster verification, optional cosmetic mod systems | Terraform together; Water Features scenarios; every listed mod works | Terrain tile hashes match after strokes; water visually consistent; adapter checklist passes |
| **M7 Scale and resilience** | Performance tuning on 300 MB saves, shared snapshots for multiple joiners, short-drop resume, resume-point backups, host-save UX, budget auto-tuning, polish (radio tags, per-player tutorials as an option) | 8 players in a large city for hours | 4-hour soak test with 4 clients: no full resync except deliberate ones; host overhead < 10 % frame time |

---

## 12. Risks, mitigations, and what to verify in game first

### 12.1 Risk table

| # | Risk | Impact | Mitigation |
|---|---|---|---|
| 1 | Managed (Mono) cost of codecs, scans and apply on huge cities (no Burst allowed) | Frame-time overhead, slow joins | Tag-driven detection; chunk-version prefilter; budgeted slices with adaptive cycles; serialization only on change; frame-start reads avoid sync stalls; measured in M0 (self-test timing) and M4 |
| 2 | `ISerializable` behaviour at runtime with our context (version branches, `Purpose`) | Wrong values or a crash | Owned full-tag `LoadGame` reader context (no null `format`); codec round-trip self-test over all families before hosting; system state only from a verified allow-list |
| 3 | The join snapshot stalls the host (GPU readbacks) and transfer is slow on relays | Hitch for everyone; minutes to join | `HostBusy` UX; shared snapshots; bulk lane with windowing and resume; backlog cap with a restart |
| 4 | A game patch renames, adds or removes systems or types | Leaks, or a critical gate missing | Name-based data tables; self-test lists unresolved and unclassified systems; leak telemetry; repair keeps the session playable; peers must match game versions anyway |
| 5 | Reflection probes break after a patch | Clock smoothness, milestone XP bar, host pause filter, viewer gate | Each probe has a fallback (Appendix A); critical-path features use public APIs |
| 6 | Client-side vanilla Modification systems write replicated state (`GroundHeightSystem` heights, `BuildingStateEfficiencySystem`) | Repair churn | Leak repair with tolerance; equal inputs mostly give equal outputs; measured in M2; switch a system OFF if it churns |
| 7 | Derived identity is not peer-stable (node lanes, node secondary objects, aggregate merges) | Parked-car lane refs fail; visual differences | DerivedKey with geometric fallback; aggregate reconciliation; tolerance list; lane refs kept off the wire where possible (R13 §2.5) |
| 8 | Proposal model trusts client-computed geometry (validation is partial) | A modified client could place invalid things | Friends-only sessions; revision, prefab, cost and bounds checks; permissions; documented trust model (R10 §7.8) |
| 9 | 32-tick interpolation window, network jitter | Movers jump or snap back | Staging ring, JIT writer, extrapolation, clock lag `D`; tuned in M5 (R15 §11) |
| 10 | Bandwidth at speed 4 with many clients | Laggy motion, slow state | Per-client budgets, interest radius and far-field rate settings; shared World stream rate set by the slowest client; slow-client policy |
| 11 | Steam lanes P/Invoke or flat API changes | Bulk blocks control traffic | Probe plus two-connection fallback |
| 12 | Water GPU simulation drift; `CopyTexture` acceptance unknown | Visual water mismatch | Host-only gameplay; periodic tiles; a "water quality" setting; M6 verification of the write path (R07 §6.7) |
| 13 | Unknown mods with load-time or enforcement behaviour | Repair churn after join | Post-load verification and repair; leak telemetry; adapter updates without a rebuild |
| 14 | Tutorials disabled in sessions | Feature loss | A product decision with a later per-player option (R14 §8.4) |
| 15 | Missing triggers for client-committed builds (chirps and tutorials about remote builds) | Minor flavour loss | Later executor through `TriggerSystem.CreateActionBuffer` |
| 16 | `CityServiceBudgetSystem` on clients diverges (its inputs come partly from disabled systems) | Budget panel numbers differ | Hash its public totals against the host; fallback reflected feed of its arrays (probe A8) |
| 17 | Save pollution: `usedMods` marks the city modded; `NetIdSystem` block | Achievements off for that save | Vanilla rule anyway (R14 §7); the block is skipped without the mod (R12 §6.3); documented in the UI |

### 12.2 What must be verified in game first (spike order)

Each spike is a small debug build. A spike passes when its criterion holds on the maintainer's machine and saves.

1. **Codec round-trip** of every serializable type on a live 100k city, using the owned full-tag context. *Pass:* 100 % identical bytes. Report total time per family.
2. **Snapshot cost:** `SaveGameSystem` into a `FileStream` on 20/100/300 MB cities. *Pass:* time and size logged; no "Continue" pointer change.
3. **Public load path:** transient `SaveGameData` + `GameManager.Load(Game, LoadGame, data)` with `overrideMode`, `overrideLoadedOptions=false` and `pausedAfterLoading`. *Pass:* the city loads, the `NetIdSystem` block round-trips with bindings for 100 % of tracked entities, the mode matches, and no tick runs on the first frame.
4. **Follower gating:** `SimulationSystem` disabled + `ClientClockSystem` with probe A1 + cosmetic runner + gate table. *Pass:* day/night and weather advance smoothly; the digest of all domains is unchanged after 10 min; no errors; no growing queues (memory flat).
5. **Applier on live data:** create, update and delete nets, buildings and areas from host-format records at the apply point. *Pass:* geometry, lanes, blocks and sub-objects regenerate identically. Compare the digest and the screen with the host. Closure tags are sufficient.
6. **Scan performance:** managed chunk-version prefilter plus raw hashing on 1–2 M entities. *Pass:* µs per chunk and per entity recorded; budgets confirmed.
7. **Steam:** lane P/Invoke, relay throughput, and a 200 MB blob between two real accounts. *Pass:* lanes configured or fallback used; throughput recorded.
8. **Prediction capture:** client road, zone, building, upgrade and bulldoze through vanilla tools produce correct proposals. *Pass:* host commit result equals the client prediction (digest of the touched entities).
9. **Synthesized progression events:** `MilestoneReachedEvent` and `Unlock` on the client refresh the toolbar, milestone panel and dev tree. *Pass:* UI matches the host.
10. **UI module:** the hand-written `.mjs` loads, `append("Menu")` and `append("Game")` render, and bindings work.
11. **Water and terrain write paths** (M6): `CopyTexture` into `TerrainSystem.heightmap` + `OnAreaChanged`; `WaterTexture` tile writes accepted by the simulation.
12. **`+connect_lobby` cold start** through the Paradox Launcher (R06 §12.1).

### 12.3 Open decisions (product)

- Session speed policy (host only / anyone / vote) and whether the host's pause menu pauses the session.
- `Followed` citizens: shared (current design) or per player.
- Water quality default (bandwidth versus fidelity).
- Strict versus relaxed local-only mod policy (default strict, per hard constraint 6).

---

## Appendix A: Non-public access (probes) and fallbacks

| Probe | Target | Used for | Fallback |
|---|---|---|---|
| A1 | `SimulationSystem.frameIndex/frameTime/smoothSpeed` private setters (`SimulationSystem.cs:68-87` [V\*]) | client clock | `SimulationSystem.Deserialize` for `frameIndex` (public); `frameTime = 0` (stepwise interpolation) |
| A2 | `MilestoneSystem.m_LastRequired/m_NextRequired/m_Progress/m_NextMilestone` (`MilestoneSystem.cs:17-23, 41-51` [V\*]) | client milestone XP bar | bar shows 0 on clients |
| A3 | `TimeUISystem.m_SimulationPausedBarrierBinding` (`TimeUISystem.cs:73` [V\*]) | host pause-barrier filter | the host's barrier pauses the session |
| A4 | `ToolBaseSystem.applyMode` protected setter (`ToolBaseSystem.cs:182` [V\*]) | viewer no-predict gate | viewer applies are reverted as leaks |
| A5 | `ModSetting.instances` internal static (`ModSetting.cs:17` [V\*]) | generic settings fingerprints | adapter-declared settings only |
| A6 | `XPSystem.m_XPMessages` | XP toasts | no toasts on clients |
| A7 | `UpdateSystem.m_Systems` phase data | disable all `GameSimulation` systems | declared list |
| A8 | `CityServiceBudgetSystem` arrays | budget feed if verification fails | numbers may differ; flagged |
| A9 | `steam_api64` flat exports (lanes) | Steam lanes | second connection for Bulk |

**Public APIs carry the critical path.** Examples:

- `UpdateSystem.UpdateAt/Before/After` (incl. two-type);
- `SaveGameSystem.stream/context/RunOnce`;
- `GameManager.Load(…, IAssetData)`;
- `AssetDatabase.GetTransient`;
- `TaskManager.EnqueueTask`;
- `ISerializable` on components and systems;
- `Context` constructor + `UnsafeUtility.As`;
- `EntityManager` batch APIs;
- `DynamicComponentTypeHandle` and chunk accessors;
- `PrefabSystem.TryGetPrefab` and `GetEntity`;
- `IconCommandSystem.CreateCommandBuffer`;
- `TerrainSystem.ApplyBrush`, `heightmap`, `OnAreaChanged`, `UpdateMinMax`;
- `WaterSystem.WaterTexture` and its parameters;
- the economy setters in §4.4;
- `NameSystem.Serialize/SetCustomName`;
- Steamworks.NET (the game's copy only).

**Harmony:** none in M0–M6. It would be added only if an in-game spike shows a requirement that no public or probe path can meet, and then each patch would be justified individually.

## Appendix B: Initial exclusion and merge-policy table (`exclusions.json`)

| Type | Policy | Reason |
|---|---|---|
| `Game.Objects.Surface` | exclude | cosmetic wetness and snow; local `WetnessSystem` (R11 §3) |
| `Game.Areas.CurrentDistrict`, `Game.Areas.BorderDistrict` | exclude | derived by `CurrentDistrictSystem` (R13 §8) |
| `Game.Routes.HiddenRoute` | exclude | per player (R10 §6.1) |
| `Game.Objects.TransformFrame` | exclude (motion channel) | R15 |
| `PathOwner`, `PathElement`, `PathInformation`, `*Navigation*`, `*NavigationLane`, `Blocker`, `TripSource` | exclude (on-demand inspection only) | R15 §5 |
| `*CurrentLane` (car, human, train, watercraft, aircraft) | exclude, except at mover create (lane via DerivedKey, optional) | R15 §5 |
| `Game.Prefabs.UnlockRequirementData` | D-PREFAB channel | R14 §2 |
| `Game.Notifications.Icon` | D-ICON channel | R14 §4 |
| `Game.Simulation.ServiceDispatch` | on demand | R15 §8.3 |
| `Game.Buildings.Building` | merge: keep local `m_RoadEdge`, `m_CurvePosition` | R13 §8 |
| `Game.Triggers.Chirp` | merge: mask `Liked` | R14 §5.4 |
| `Game.Objects.Transform`, `Game.Objects.Moving` on movers | create/swap only | R15 §2.6 |
| everything else serializable on tracked families | replicate (default, P1) | — |
