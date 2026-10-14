# Code map

A short map of the code for new contributors: where things are, how data flows through a session, and what to read in
the design documents first. The design itself is in [ARCHITECTURE.md](../design/ARCHITECTURE.md); it describes the
target design, and the code implements a playable subset of it (see its "As built" row and notes).

## Projects

| Project | Target | What it is |
|---|---|---|
| `src/SkylineTogether.Core` | net472 (game present) or netstandard2.1 (compile check without the game) | Pure .NET: no Unity, Game or Colossal reference. Protocol, codecs, hashing, session state machines, scheduling, replay checking. Everything testable lives here. |
| `src/SkylineTogether` | net472, compiled against the game's `Cities2_Data\Managed` assemblies | The game layer: the `IMod`, ECS systems, Steam transport, UI bindings, mod adapters. |
| `tests/SkylineTogether.Core.Tests` | net8.0 console app | Compiles the Core sources directly and tests them with its own small framework ([testing.md](testing.md)). |
| `ui/SkylineTogether.mjs` | ES module, no bundler | The in-game UI (panel, title screen page, chat dock, join and error screens). See [ui/README.md](../../ui/README.md). |

## Core folders (`src/SkylineTogether.Core`)

| Folder | Contents |
|---|---|
| `Buffers` | `ByteWriter` / `ByteReader` (little-endian, LEB128 varints), `BufferPool` |
| `Contracts` | Shared interfaces and models: identity (`NetId`, `NetRef`, `PeerId`), transport (`ITransport`, `IConnection`), session and handshake models, locale tables, environment (`ILog`, `IClock`), and the engine interfaces the repair ladder is written against (`IHostReplicationEngine`, `IHashShadowView`, `ISlotTable`) |
| `Diagnostics` | `FeatureHealth` (guards that switch a failing feature off), `PerfCounters` |
| `Fields` | Terrain and water tiles and cell maps: tile codecs, grids, stroke settling |
| `Follower` | `FollowerClockModel`: the client's clock model (the gates themselves are in the game layer's `ClientGate`) |
| `Hashing` | xxHash3/xxHash64, digests, set hashes |
| `Identity` | Family rules, build order, `SlotBitSet` (network id allocation itself is `Sync/NetIdAllocator`) |
| `Integrity` | Bucket sums, `DivergenceTracker`, line and icon checks, `RepairEscalation` (rebaseline and resync above per-entity repair); also the beacon-based repair ladder (`RepairCoordinator`, `HostRepairResponder`, `HashLedger`, `BeaconPlanner`) that the game does not use |
| `Intents` | Client proposals and semantic intents |
| `Json` | Strict JSON reader/writer |
| `Modding` | Mod analysis and integration: `ModClassifier` (decisions about any mod from its scanned `ModFacts`), `ModDeclarations`, `AdapterCheck`, `ModChannelRouter`/`ModChannelBudget`, `AssetSource`/`MissingAssets`, `IlScanner` |
| `Motion` | Encoding of moving vehicles and citizens |
| `Protocol` | Envelope framing, fragmentation, compression, handshake and other messages, `Limits`, `ProtocolVersion` |
| `Replay` | Solo test: recording format, recorder peer, playback transport, `ReplayChecker`, `ReplayVerdict` |
| `Scheduling` | Token buckets, byte budgets, attention and agent planners, rolling scans; also bandwidth controls the game does not use yet (`HostUploadBudget`, `LinkEstimator`, `AutoSpeedCap`, `DegradationController`) |
| `Session` | `SessionHost`, `SessionClient`, `PeerChannel`, `RejoinController`, presence, mod compatibility, `SessionCrypto`, `JoinSecurity` (bans, join policy, password lockout, co-host rules), `BuildId` |
| `Sync` | `SyncBatch` records, proposal guards (rate limit, conflicts), id allocation, global channels, terrain/water/mover/integrity records |
| `Testing` | Fakes for tests and tools (`ManualClock`, `DeterministicRandom`, `ListLog`, `NullLog`) |
| `Transport` | Loopback, network simulator, composite transport, outbox, blob transfer, send rate control |

## Game layer folders (`src/SkylineTogether`)

| Folder | Contents |
|---|---|
| `Mod.cs` | The entry point: `OnLoad` creates the controllers and registers the systems; `OnDispose` |
| `Compat` | Mod compatibility: `ModAdapter` and 12 adapters (Traffic, Move It, Anarchy, ...), `ModCompat` (registry, adapter checks), `ModAssemblyScanner` and `HarmonyPatches` (scan of any mod), `GenericModAdapter`, `ModIntegration` (the API for other mods, mod channel), `AssetSources`, `ModStateWatcher`, `ModSettingsSync` |
| `Data` | Embedded locale tables (`locale.en-US.json`, `locale.de-DE.json`, `locale-keys.generated.json`) |
| `Diagnostics` | `Probes` (self-test), `Features`, `Guard`, `Perf`, `GameLog`, `DiagnosticsDump` |
| `Net` | `SteamSocketsTransport` (Steam Networking Sockets: P2P by Steam ID, optional direct IP) |
| `Presence` | Cursors, name tags and other players' building previews |
| `Replay` | `ReplayController`: Record test session, Replay & verify |
| `Session` | `SessionManager` (host/join/leave, recovery), snapshot save and load, Steam invites, toasts |
| `Settings` | `SkylineTogetherSettings` (Options page), `LocaleSource` |
| `Sync` | `SyncController` (capture, send, receive, apply), `EntityCodec`, `NetIdSystem`, client gates, terrain/water/agents/movers/flow/icons/integrity syncs |
| `Systems` | `SpeedAuthoritySystem` |
| `UI` | `SessionUISystem`, `ReplayUISystem`, `InviteUISystem`: bindings and triggers for the UI module |

## Startup

`Mod.OnLoad` runs each step guarded (a failing step is logged and the next one still runs): settings and locale, Steam
relay access, the startup probes (`Probes.RunStartup`), the controllers (`SessionManager`, `SyncController`,
`PresenceController`, `FollowerClock`, `MoverSync`, `SteamInvites`, `ReplayController`, ...) and the ECS systems. A
`SessionDriver` MonoBehaviour ticks the session every frame. The systems and their frame positions are registered in
`Mod.cs`; that list is authoritative (ARCHITECTURE Appendix A shows the original plan).

## Data flow in a session

All real work happens on the main thread in `Sync/SyncController.cs`; the ECS systems in `Sync/SyncSystems.cs` are thin
wrappers that call it at the right point of the frame.

```
 host                                                           client
 ────                                                           ──────
 game simulation changes entities
   │
 SyncCaptureSystem → SyncController.CaptureOutgoing
   ├ terrain, water, cell maps, statistics
   ├ CaptureHost: changed chunks → EntityCodec.WriteEntity
   ├ simulation values, flow status, global state, actions
   └ IntegritySync: bucket digests appended
   │
 SessionHost.Broadcast → PeerChannel (envelope, compression,
   fragments) → PeerOutbox → SteamSocketsTransport  ───────────▶  SessionClient → inbox
                                                                  SyncApplySystem → SyncController.ApplyIncoming
                                                                    ├ ApplyFromHost: SyncBatch → EntityCodec.Apply
                                                                    ├ global state, values, agents, events, signals
                                                                    └ IntegritySync.OnHostBatch: compare digests,
                                                                      request repairs
                                                                  the player uses a vanilla tool (local prediction)
                                                                  SyncController.CaptureClient
 SyncController.ApplyProposal  ◀───────────────────────────────── MessageKind.Proposal / ActionIntent records
   ├ rate limit (120/s, burst 240)
   ├ Validate: role, conflicts (base revision), rules
   ├ refused → RejectProposal (the client is rolled back)
   └ accepted → EntityCodec.Apply; captured and broadcast next frame
```

- **Identity.** Replicated entities get a network id (`NetIdSystem`, saved with the city so join snapshots carry them).
  Entities the game re-derives on every PC (sub-objects, lanes, ...) are matched by derived keys instead.
- **Joining.** The host saves the city into a snapshot (`Session/SnapshotService`), streams it to the joiner, which
  loads it through the game's normal load path (`SnapshotLoader`) and then applies everything that happened since.
- **Clients don't simulate.** On clients, the game's simulation systems are switched off (`ClientGate`,
  `FollowerClock`); values arrive from the host.
- **Integrity.** See [Troubleshooting → desync](../guide/troubleshooting.md#something-looks-different-for-another-player-desync)
  for the behaviour and `Sync/IntegritySync.cs` for the code.

## Reading order for ARCHITECTURE.md

ARCHITECTURE.md is long (about 2500 lines). Read in this order and skip the rest until you need it:

1. The preamble's **Terms** and **Appendix I (Glossary)**.
2. **§0.1-0.3**: the model, design principles and key decisions.
3. **§2**: who is authoritative for which kind of state.
4. **§4.1**: replication overview. It describes a worker-thread design; compare with `SyncController.cs`, which is what
   runs.
5. **§5.1 and §5.4**: the client action path and host validation.
6. **§3.1-3.2**: entity identity and family classification.
7. **§8 intro and §8.5**: divergence detection and repair (only per-entity repair is wired in).
8. **§13.5**: the solo test (record and replay), as built.

Look for the "As built" notes (§3.1, §4.2, §9.7, §13.5): they describe the shipped code.

Other design documents:

| Document | What it answers |
|---|---|
| [SYNC-COVERAGE.md](../design/SYNC-COVERAGE.md) | For every piece of game state: is it the same for every player, and how? Includes a generated inventory of all serializable components and systems |
| [ACTION-COVERAGE.md](../design/ACTION-COVERAGE.md) | For every player action: how it travels in both directions and where it is implemented |
| [MOD-COMPATIBILITY.md](../design/MOD-COMPATIBILITY.md) | How third-party mods are kept in sync, per mod |
| [IMPLEMENTATION-PLAN.md](../design/IMPLEMENTATION-PLAN.md) | The original module plan and contracts (partly outdated, see its "As built" row) |
| [research/00-DIGEST.md](../research/00-DIGEST.md) | Summary of the research reports on the game's internals |
| [decisions/](../decisions/) | Decision records |
