# Code map

A short orientation for new contributors: where to start reading the code and which document answers what. How the
mod works is described in [ARCHITECTURE.md](../ARCHITECTURE.md), written from the code; its
[module map](../ARCHITECTURE.md#module-map) lists every folder of both projects with its responsibility.

## Projects

| Project | Target | What it is |
|---|---|---|
| `src/SkylineTogether.Core` | net472 (game present) or netstandard2.1 (compile check without the game) | Pure .NET: no Unity, Game or Colossal reference. Protocol, codecs, hashing, session state machines, scheduling, integrity bookkeeping, replay checking. Everything testable lives here. |
| `src/SkylineTogether` | net472, compiled against the game's `Cities2_Data\Managed` assemblies | The game layer: the `IMod`, ECS systems, the entity codec, Steam transport, UI bindings, mod adapters. |
| `tests/SkylineTogether.Core.Tests` | net8.0 console app | Compiles the Core sources directly and tests them with its own small framework ([testing.md](testing.md)). |
| `ui/SkylineTogether.mjs` | ES module, no bundler | The in-game UI (panel, title screen page, chat dock, join and error screens). See [ui/README.md](../../ui/README.md). |

Almost all Core code is reached by the game layer; the exceptions (test fakes and the network simulator) are listed in
[ARCHITECTURE.md](../ARCHITECTURE.md#core-code-the-game-layer-does-not-reach). `python tools/reachability.py --explain
<Type>` shows how the game layer reaches a Core type ([testing.md](testing.md#what-the-mod-never-reaches)).

## Where to start reading

1. [`Mod.cs`](../../src/SkylineTogether/Mod.cs): `OnLoad` creates the controllers and registers the ECS systems with
   their frame positions; that list is authoritative ([frame order](../ARCHITECTURE.md#frame-order)). Every step is
   guarded: a failing step is logged and the next one still runs.
2. [`Session/SessionManager.cs`](../../src/SkylineTogether/Session/SessionManager.cs): hosting, joining, leaving and
   recovery; it drives Core's `SessionHost` and `SessionClient` from the `SessionDriver` MonoBehaviour every frame.
3. [`Sync/SyncSystems.cs`](../../src/SkylineTogether/Sync/SyncSystems.cs): thin ECS systems that call
   `SyncController` at the right point of the frame.
4. `Sync/SyncController*.cs`: one partial class in several files: `SyncController.cs` (state, construction,
   diagnostics, sending), `.Session.cs` (session start and end, peers joining and leaving), `.Apply.cs` (frame start:
   applying batches, proposals and requests), `.Validation.cs` (host validation, refusals and rollback),
   `.Capture.cs` (tool hook, frame-end capture, value scans, deferred queues), `.Client.cs` (the client's proposals,
   route preview window), `.Zones.cs` (zone cells) and `.Mods.cs` (mod compatibility).
5. [`Sync/EntityCodec.cs`](../../src/SkylineTogether/Sync/EntityCodec.cs) and
   [`Core/Sync/SyncBatch.cs`](../../src/SkylineTogether.Core/Sync/SyncBatch.cs): how entities become records and back.
6. The channel classes in `Sync/` (`AgentSync`, `MoverHost`/`MoverClient`, `AttentionValues`, `GlobalStateSync`,
   `TerrainSync`, ...), each described in [data channels](../ARCHITECTURE.md#data-channels).

## Data flow in a session

```
 host                                                           client
 ────                                                           ──────
 game simulation changes entities
   │
 SyncCaptureSystem → SyncController.CaptureOutgoing
   ├ terrain, water, cell maps, statistics
   ├ CaptureHost: changed chunks → EntityCodec.WriteEntity
   ├ simulation values, flow status, global state, actions
   └ IntegritySync: icon sets and line rows; bucket digests per peer
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
 SyncController.ApplyProposal  ◀───────────────────────────────── MessageKind.Proposal (entity records, intents)
   ├ rate limit (120/s, burst 240)
   ├ Validate: role, conflicts (base ledger epoch), rules
   ├ refused → RejectProposal (the client is rolled back)
   └ accepted → EntityCodec.Apply; captured and broadcast next frame
```

- **Identity.** Replicated entities get a network id (`NetIdSystem`; the bindings travel in the join snapshot).
  Entities the game re-derives on every PC (sub-objects, lanes, ...) are matched by derived keys instead.
- **Joining.** The host saves the city into a snapshot (`Session/SnapshotService`), streams it to the joiner, which
  loads it through the game's normal load path (`SnapshotLoader`) and then applies everything that happened since.
- **Clients don't simulate.** On clients, the game's simulation systems are switched off (`FollowerClock`,
  `ClientGate`); values arrive from the host.
- **Integrity.** See [Troubleshooting → desync](../guide/troubleshooting.md#something-looks-different-for-another-player-desync)
  for the behaviour and [integrity](../ARCHITECTURE.md#integrity) for the mechanism.

## Which document answers what

| Document | What it answers |
|---|---|
| [ARCHITECTURE.md](../ARCHITECTURE.md) | How the mod works, as built: model, threads, modules, session, transport, sync pipeline, identity, channels, integrity, mods, diagnostics, budgets, security, known gaps |
| [SYNC-COVERAGE.md](../design/SYNC-COVERAGE.md) | For every piece of game state: is it the same for every player, and how? Includes a generated inventory of all serializable components and systems |
| [ACTION-COVERAGE.md](../design/ACTION-COVERAGE.md) | For every player action: how it travels in both directions and where it is implemented |
| [MOD-COMPATIBILITY.md](../design/MOD-COMPATIBILITY.md) | How third-party mods are kept in sync, per mod |
| [research/00-DIGEST.md](../research/00-DIGEST.md) | Summary of the research reports on the game's internals |
| [decisions/](../decisions/) | Decision records |
| [history/](../history/README.md) | The design documents written before the implementation (historical, not authoritative) |
