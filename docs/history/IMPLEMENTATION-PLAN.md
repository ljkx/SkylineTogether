# Skyline Together: Implementation Plan

> **Historical design record.** Written before the implementation and not maintained; the code differs in many
> places. The current architecture is [docs/ARCHITECTURE.md](../ARCHITECTURE.md); see [README.md](README.md).

| | |
|---|---|
| Status | Version 1.1, 2026-10-11. **Historical.** Derived from [ARCHITECTURE-original.md](ARCHITECTURE-original.md) version 1.1 (cited **ARCH §x**), which was authoritative at the time; [docs/ARCHITECTURE.md](../ARCHITECTURE.md) describes the code as built. Version 1.1 applies the design review (ARCH "Review log"): two new Core modules (core-codec, core-follower), M5 split into M5a/M5b/M5c, host flow and host session start, transactional apply, outbox, epoch parts, revised contracts; and the user requirement U1 (host-authoritative transport lines and status indicators, ARCH §4.11): line state, icon authority filter, road-choice reconcile, received-value cache with re-resolution, guarded route-tool preview window, flow-status channel, UI-level line digest and acceptance test ST-LINES. |
| Audience | Engineers implementing modules in parallel, and the maintainer, who builds, deploys and checks each milestone in game. |
| As built | The plan was written before the code. Where the code differs, the code and [docs/dev/code-map.md](../dev/code-map.md) are right: the game layer's folders are `Compat/ Data/ Diagnostics/ Net/ Presence/ Replay/ Session/ Settings/ Sync/ Systems/ UI/`; `Data/` holds only the locale tables; the tests are one net8.0 console project with its own small framework ([decision 0001](../decisions/0001-build-without-nuget.md)); the build script is `tools/build.sh`, the UI smoke test `tools/ui_smoke.mjs`; `tools/ApiCheck`, `TypeAudit`, `ReplicaDiff` and `EpochReplay` do not exist. The milestones of §5 were the order of work, not release versions. |
| Rule | Modules are written without running the game. Every module is type-checked against the real game DLLs, unit-tested where it is pure .NET, and gets an in-game self-test or a scripted check in game. |

How to use this plan: §1-§4 are shared rules (layout, build, conventions, testing). §5 lists the milestones and what is checked in game. §6 gives the dependency graph and the parallel schedule. §7 specifies every module: folder, assembly, responsibility, exact public contracts, dependencies, game APIs (with research references) and tests. A module can be implemented as soon as the contracts of its dependencies exist; contracts are frozen per milestone and changed only through a decision record (ARCH Appendix H).

---

## 1. Solution layout

```
SkylineTogether/
  Directory.Build.props                  shared properties: game paths, deploy dir, C# version, warnings as errors
  SkylineTogether.sln
  src/
    SkylineTogether.Core/                netstandard2.0, NO game or Unity references, NO NuGet packages
      Contracts/                         core-contracts: ids, enums, seams, ApplyPlan, record headers, config models
      Buffers/  Hashing/  Json/  Testing/core-contracts: ByteWriter/ByteReader, XxHash64, MiniJson, test doubles
      Protocol/                          core-protocol
      Codec/                             core-codec (canonical encoding of every IWriter/IReader member)
      Transport/                         core-transport
      Session/                           core-session
      Identity/                          core-identity
      Follower/                          core-follower (gate planning, load hand-over, cosmetic schedule)
      Replication/                       core-replication
      Scheduling/                        core-scheduling
      Motion/                            core-motion
      Intents/                           core-intents
      Integrity/                         core-integrity
      Fields/                            core-fields
    SkylineTogether/                     net472 against the game's Managed DLLs (NoStdLib), unsafe; the IMod assembly
      Bootstrap/                         game-bootstrap (IMod, registration, runtime, logging, settings, probes, self-test)
      Platform/Steam/                    game-steam-transport
      UI/                                game-ui (C# side)
      Ecs/                               game-ecs-schema
      Join/                              game-join
      Follower/                          game-follower
      Host/                              game-host-capture
      Client/                            game-client-apply
      Channels/                          game-state-channels
      Actions/                           game-client-actions
      Motion/                            game-motion
      Integrity/                         game-integrity
      Fields/                            game-fields
      Compat/                            game-compat
      Data/                              embedded JSON: gates, cosmetic, policies, sysstate, adapters, locale.en-US, modules
  ui/
    SkylineTogether.mjs                  hand-written UI module (no bundler), deployed next to the DLLs
    bindings.generated.json              binding names generated from SessionUISystem constants (used by the smoke test)
  tests/
    SkylineTogether.Core.Tests/          net8.0;net472, xUnit, references Core only (unit, property and end-to-end tests)
  tools/
    build.ps1                            build, ApiCheck, UI check, tests, deploy, package
    ApiCheck/                            metadata-only checks: every referenced member exists in the game DLLs; every type and
                                         system name in the data files resolves; folder dependencies match Data/modules.json
    UiSmoke/                             smoke.mjs: imports the .mjs with stub globals and renders every component (Node optional)
    TypeAudit/                           lists serializable component types, fields, entity offsets, writers and their phases,
                                         data-carrying IEmptySerializable types with their runtime writers, K types per derived
                                         family, and icon producers (policy and gate authoring, ARCH §4.2.4, §4.10.7, §4.10.4)
    ReplicaDiff/                         diffs replica hash dumps from host and client
    EpochReplay/                         replays recorded epoch streams against the Core FakeWorld
  docs/  research/  design/  decisions/
```

Two shipped assemblies: `SkylineTogether.Core.dll` (pure .NET) and `SkylineTogether.dll` (game layer, contains the only `IMod`). All game modules live in the one game assembly; their dependencies are logical (folder level), which is why their contracts are specified here.

---

## 2. Build without the Unity toolchain

### 2.1 Principles

- Plain C# compiled by the .NET SDK (any SDK ≥ 6 that can target `net472` and `netstandard2.0`).
- The game assembly compiles against the game's own `mscorlib.dll`, `netstandard.dll`, `System*.dll` and the Unity/Colossal/Game DLLs from `Cities2_Data\Managed` with `Private=false`, so nothing from the game is copied or shipped (R05 §1.2: the loader skips assemblies that duplicate game assemblies).
- No Entities source generators, no IL post-processing, no Burst, no `SystemAPI`, no `IJobEntity`, no `ISystem`, no custom `IJobChunk` scheduling (hard constraint 1). Systems are plain `GameSystemBase` subclasses; `OnCreateForCompiler` defaults to empty (`ComponentSystemBase.cs:142`, [V-R] P-C).
- The game ships `netstandard.dll`, `System.Memory.dll`, `System.IO.Hashing.dll` and `System.Runtime.CompilerServices.Unsafe.dll` in its Managed folder (checked), so a `netstandard2.0` Core loads without extra DLLs. Core still uses no external package: it carries its own xxHash64, JSON reader, PBKDF2-HMAC-SHA256 (over `HMACSHA256`, because the hash-algorithm overload of `Rfc2898DeriveBytes` is not in netstandard2.0) and a managed LZ4-block compressor as the only compression fallback (ARCH §4.6.5).

### 2.2 `Directory.Build.props`

```xml
<Project>
  <PropertyGroup>
    <GameDir Condition="'$(GameDir)' == ''">C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II</GameDir>
    <GameManagedPath Condition="'$(GameManagedPath)' == ''">$(GameDir)\Cities2_Data\Managed</GameManagedPath>
    <CS2UserDataPath Condition="'$(CS2UserDataPath)' == ''">$(USERPROFILE)\AppData\LocalLow\Colossal Order\Cities Skylines II</CS2UserDataPath>
    <ModDeployDir Condition="'$(ModDeployDir)' == ''">$(CS2UserDataPath)\Mods\SkylineTogether</ModDeployDir>
    <LangVersion>10.0</LangVersion>
    <Deterministic>true</Deterministic>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <!-- CS1591 (missing XML comment) would turn every undocumented public member into an error together with
         GenerateDocumentationFile; contracts are documented in this plan, not necessarily in XML -->
    <NoWarn>$(NoWarn);CS1591</NoWarn>
    <Nullable>disable</Nullable>
    <Version>0.1.0</Version>
  </PropertyGroup>
</Project>
```

`GameManagedPath` and `ModDeployDir` can be overridden on the command line (`dotnet build /p:GameDir=D:\Games\CS2`) or by environment variables of the same name.

### 2.3 Core project (`src/SkylineTogether.Core/SkylineTogether.Core.csproj`)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <AssemblyName>SkylineTogether.Core</AssemblyName>
    <RootNamespace>SkylineTogether.Core</RootNamespace>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <AssemblyVersion>1.0.0.0</AssemblyVersion>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="SkylineTogether.Core.Tests" />
  </ItemGroup>
</Project>
```

### 2.4 Game project (`src/SkylineTogether/SkylineTogether.csproj`)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <AssemblyName>SkylineTogether</AssemblyName>
    <RootNamespace>SkylineTogether</RootNamespace>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <!-- pinned: NetIdSystem is saved by assembly-qualified name (ARCH §3.4) -->
    <AssemblyVersion>1.0.0.0</AssemblyVersion>
    <FileVersion>$(Version)</FileVersion>
    <!-- compile against the game's own corlib, not a reference pack -->
    <NoStdLib>true</NoStdLib>
    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>
    <AutomaticallyUseReferenceAssemblyPackages>false</AutomaticallyUseReferenceAssemblyPackages>
    <FrameworkPathOverride>$(GameManagedPath)</FrameworkPathOverride>
    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
    <DebugType>portable</DebugType>
  </PropertyGroup>
  <ItemGroup>
    <!-- every assembly of the game's Managed folder (173 files in the current build, incl. mscorlib, netstandard,
         cohtml.Net, Colossal.UI, System.IO.Compression, UnityEngine.IMGUIModule), never copied; a short denylist
         keeps test-only assemblies out. A hand-maintained list was incomplete (review finding 39). -->
    <Reference Include="$(GameManagedPath)\*.dll"
               Exclude="$(GameManagedPath)\*TestScenarios*.dll;$(GameManagedPath)\Colossal.TestFramework.dll">
      <Private>false</Private>
    </Reference>
    <ProjectReference Include="..\SkylineTogether.Core\SkylineTogether.Core.csproj" />
    <EmbeddedResource Include="Data\*.json" LogicalName="SkylineTogether.Data.%(Filename)%(Extension)" />
  </ItemGroup>
  <Target Name="DeployMod" AfterTargets="Build" Condition="'$(DeployMod)' != 'false'">
    <ItemGroup>
      <DeployFile Include="$(TargetDir)SkylineTogether.dll;$(TargetDir)SkylineTogether.pdb;$(TargetDir)SkylineTogether.Core.dll;$(TargetDir)SkylineTogether.Core.pdb" />
      <DeployFile Include="$(MSBuildThisFileDirectory)..\..\ui\SkylineTogether.mjs" />
    </ItemGroup>
    <MakeDir Directories="$(ModDeployDir)" />
    <Copy SourceFiles="@(DeployFile)" DestinationFolder="$(ModDeployDir)" SkipUnchangedFiles="true" />
  </Target>
</Project>
```

**First task of M0 (verification of this recipe).** On a machine with the .NET SDK: `dotnet build -c Release`. If `FrameworkPathOverride` with `NoStdLib` is rejected by the installed SDK, use the fallback: remove `NoStdLib`/`FrameworkPathOverride`, add the compile-time-only package `Microsoft.NETFramework.ReferenceAssemblies.net472`, keep every game reference with `Private=false`, and rely on `tools/ApiCheck` (below) to prove that every referenced member exists in the game's own assemblies. The outcome is recorded in a decision record.

**`tools/ApiCheck`** (net8.0 console, `System.Reflection.Metadata` only), run after every build by `build.ps1`:

1. **Member resolution**: reads `SkylineTogether.dll` and `SkylineTogether.Core.dll`, enumerates every `TypeRef`/`MemberRef` and verifies that it resolves in the game's Managed folder (including `mscorlib`). It turns "compiles against a reference pack" into "every call exists in the game's Mono profile".
2. **Data-file names**: reads `gates.json`, `cosmetic.json`, `policies.json`, `adapters.json` and `sysstate.json` and resolves every type and system name with `System.Reflection.Metadata` against `Game.dll` and, where installed (`%LOCALAPPDATA%Low\...\Mods` and the subscribed-mods cache), the third-party mod DLLs; a missing critical name fails the build, a missing optional or mod name warns.
3. **Module dependencies**: maps every game-assembly type to its module by namespace folder (ARCH folder table, §1) and checks that each module only references the modules it declares in `Data/modules.json` (generated from §6.1); a violation fails the build.

### 2.5 Tests and tools

- `tests/SkylineTogether.Core.Tests` targets `net8.0;net472` (`<TargetFrameworks>`), uses xUnit, and references the Core project only. Run with `dotnet test`; the `net472` run executes on the installed .NET Framework, which exercises the Framework API surface and behaviour that the game's Mono profile shares (compile-time reference assemblies through `Microsoft.NETFramework.ReferenceAssemblies`, test-project only).
- Tools target `net8.0` and use `System.Reflection.Metadata` (never load game assemblies for execution).

### 2.6 UI module shipping

`ui/SkylineTogether.mjs` is a hand-written ES module (ARCH §11.2) copied by `DeployMod` into `Mods\SkylineTogether\`. The game finds it through the asset database (any `.mjs` whose first block comment carries the `Cities: Skylines II UI Module` header) and serves it as `coui://ui-mods/SkylineTogether.mjs` (R05 §7.3). Local modules hot-reload while the game runs, which makes UI iteration cheap during the user's tests. No bundler and no Node dependency at runtime.

**Checks before deployment** (`build.ps1`, skipped with a warning when `node` is not on the PATH): `node --check ui/SkylineTogether.mjs`; then `node tools/UiSmoke/smoke.mjs`, which installs stub `window.React` (records `createElement` calls) and `window["cs2/api"]` (`bindValue` returns sample values from `tools/UiSmoke/samples.json`, `trigger` records calls), imports the module, calls `register()` with a stub registry, renders every registered component and every panel for each sample session state, and fails on any exception or on a binding or trigger name that `ui/bindings.generated.json` does not list. `bindings.generated.json` is written by the build from the `const string` names in `SessionUISystem`. A broken module still never blocks a test, because the settings page has session controls (ARCH §11.6).

### 2.7 Deployment and packaging

- `tools/build.ps1` order: `dotnet build -c Release` → `ApiCheck` (members, data-file names, module dependencies) → UI checks (Node optional) → `dotnet test` (both target frameworks) → deploy. Any failure stops before deployment.
- Local deploy: `Mods\SkylineTogether\` under `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\` contains `SkylineTogether.dll`, `SkylineTogether.Core.dll`, both `.pdb` files and `SkylineTogether.mjs`. Local mods take precedence over PDX subscriptions of the same name (R05 §1.2), which matters when testing with two machines.
- `tools/build.ps1 -Package` also produces `SkylineTogether-<version>.zip` with the same files plus `README.txt` and the third-party notices.
- Optional user overrides of data tables: a `Data\` folder next to the DLL (`gates.json`, `policies.json`, `adapters.json`, `sysstate.json`, `cosmetic.json`, `locale.<id>.json`) replaces or extends the embedded copy; the self-test report shows which copy is active, and the effective content of every table except the locales is part of the handshake digest (ARCH §4.2.7), so both PCs of a test must use the same overrides.

---

## 3. Coding conventions

1. **Assemblies and namespaces**: Core types live in `SkylineTogether.Core.<Area>`; game types in `SkylineTogether.<Area>`. One public type per file. Private fields `_camelCase`, everything public `PascalCase`.
2. **Core is pure**: no Unity, Game or Colossal types; public APIs take `byte[] buffer, int offset, int count` (no `Span` in public signatures); no static mutable state except pools; deterministic (no `DateTime.Now` in logic, time through `IClock`).
3. **Threads and phases**: ECS is touched only on the main thread inside our systems (ARCH D8). Every public member that is not main-thread-only says so with `[ThreadAffinity(ThreadKind.Any|Network|Worker)]` from core-contracts. Cross-thread handoff uses `ConcurrentQueue<T>` or single-producer/single-consumer rings; never `MainThreadDispatcher`. Host engine state is mutated only inside the capture window (ARCH §4.5.5): mutating engine members carry `[CaptureWindow]` and assert the phase in debug builds; calls from outside the window go through the engine command queue.
4. **Systems**: every game system derives from `SkylineSystemBase` (game-bootstrap), overrides `OnSafeUpdate`, declares the session roles in which it does work, and never throws into the game loop. Registration happens only in `Bootstrap/Registration.cs`, in the order of ARCH Appendix A.
5. **IMod**: `SkylineTogetherMod` is top-level, implements `IMod` directly, and relies on no constructor or instance field initializer (instances are created with `GetUninitializedObject`, R05 §1.3). All state lives in `ModRuntime`.
6. **ECS access**: read-only dynamic handles on the host; RW only in the applier; `CompleteDependency()` before reading; batch structural changes once per frame; never `DestroyEntity` for replicated deletes (add `Deleted`); never touch `EntityManager` inside `NetIdSystem.Deserialize`.
7. **Reflection**: only through `ProbeRegistry` (game-bootstrap); every probe has an id, criticality and fallback (ARCH Appendix B). No other reflection on game types. No compile-time reference to any other mod.
8. **Allocation**: no allocation per message, per entity or per frame on hot paths; pooled `byte[]` (`BufferPool`); no LINQ, no boxing (generic codecs use constrained calls), no closures on hot paths.
9. **Data-driven tables**: gates, cosmetic list, policies, adapters and system-state entries are JSON files resolved by type name at session start; missing critical names fail loudly (self-test), non-critical ones warn.
10. **Logging**: `ILog` with categories; rate-limited; no logging inside per-entity loops except behind `IsEnabled(Trace)`.
11. **Text**: every user-facing string is a key in `Data/locale.en-US.json` (from M0), resolved through `LocaleSource` in C#, also for the `.mjs` module; never hard-coded English. A Core test checks that every key referenced in code (a generated list) and in the data files exists.
12. **Versioning**: `ProtocolVersion` (Core) changes with any wire change; the policy-table version is part of the catalog fingerprint; the game assembly's `AssemblyVersion` stays 1.0.0.0.
13. **Contracts**: public contracts in §7 are frozen at the start of their milestone. Changing one needs a decision record and an update of this plan and, if behaviour changes, of ARCH.
14. **Git**: one branch per module task; small pull requests; Core changes need passing tests; game changes need a clean build and `ApiCheck`.
15. **Thin game layer**: algorithmic decisions (encoding, classification, scheduling, apply planning, gate planning, load hand-over) live in Core and are unit-tested there; a game type that needs a decision calls the Core planner and only executes its output against `EntityManager` (ARCH K16). A pull request that adds such logic to the game assembly without a Core counterpart is sent back.
16. **Apply safety**: every slot → entity resolution goes through `ChunkAccess.TryLocate` (`Exists`, then `GetStorageInfo`, then `Has` per column); every value write is decode-then-commit (ARCH §3.3, §4.7.4).
17. **Status indicators are host data** (ARCH D11, §4.11): client code never adds or removes a persistent icon, never computes line connectivity, stop or vehicle counts, and never keeps a vanilla status producer running outside the gate table's rules. A pull request that adds a client-side status derivation is sent back; a newly found producer goes into the gate table or stays covered by the icon authority filter.

---

## 4. Testing strategy

| Level | What | Where | Who runs it |
|---|---|---|---|
| Unit | every public Core type: canonical encoding (golden vectors), codecs, varints, records, NetRefs, identity tables, family rules, shadows, dirty sets, schedulers, scan scheduler, apply planner, gate planner, tool-scoped preview windows, load hand-over, clock model, motion maths, validators, state machines, parsers, icon command filter, received-value cache and derived-reference index, route-guard planning, line-state report comparison, PBKDF2 vectors, locale keys | `tests/SkylineTogether.Core.Tests` (net8.0 and net472) | CI / engineers |
| Property / fuzz | decoder fuzzing (no exception other than `DecodeException`), record round trips, epoch merge commutation rules, scheduler budget adherence and fairness, engine concurrency stress (workers vs randomised main-thread calls through the command queue) | same | CI |
| End-to-end (Core) | **FakeWorld** host ↔ `NetSimTransport` (loss, latency, jitter, reorder, duplication, bandwidth cap) ↔ FakeWorld clients, driving the real host and client engines: convergence of bucket sums within bounded time, idempotency under duplication, join with snapshot + backlog, join of a 1 M-entity world without per-entity P0 traffic, join after type sets were defined, repair ladder, proposals with conflicts and same seq/LocalId on two clients, committed entities reaching other clients, outbox under `Busy`, reverse-buffer co-capture (`Passenger`, `RouteVehicle`, `ConnectedRoute`, `GuestVehicle`) in the same transaction as the forward reference, re-resolution of references after a client-side regeneration of derived children, path-state capture by the path-result trigger followed by a synthesised `PathUpdated` op | same (`E2E/`) | CI |
| Type check | game assembly compiled against the real DLLs; `tools/ApiCheck` (members, data-file names, module dependencies) | build | engineers |
| UI check | `node --check` and `tools/UiSmoke` (optional Node) | build | engineers |
| In-game self-test | probes, gate/adapter resolution, catalog, codec round trip on live data, system-state round trip, Steam lanes on a socket pair, compressor, UI module observed, icon authority filter, route guard, line probe (ARCH §13.4) | game-bootstrap `SelfTestRunner` | user (one click) |
| Single-machine harnesses | shadow client, record/replay (ARCH §13.5) | game-integrity (basic replay from M3) | user |
| Two-machine scripts | per milestone (§5) | two players | maintainer |

**FakeWorld** (core-replication, `tests/.../Fakes/FakeWorld.cs`): entities as `(slot → typeSet, canonical bytes per type, enabled bits)`, archetype change and tag simulation, a minimal "simulation" that mutates values, and an executor of `ApplyProgram`s. Both the FakeWorld and the game layer's `WorldApplier` execute the programs produced by the Core `ApplyPlanner` (ARCH §4.7.2), so every FakeWorld end-to-end scenario also tests the planning the game applier performs; the game executor only maps program operations to `EntityManager` calls. The game-side self-test `ApplyConformanceTest` (game-client-apply) runs a fixed set of programs on scratch entities in game and compares the result with the FakeWorld's.

Every in-game test ends with **"Write diagnostics dump"**; the zip is the bug report.

---

## 5. Milestones

Each milestone ends with a playable build and a scripted check (§8). Modules marked "(ext.)" extend a module started earlier.

| M | Goal | Modules | Exit criteria (checked in game) |
|---|---|---|---|
| **M0** Foundations | build, deploy, load, observe | core-contracts, core-protocol, game-bootstrap (incl. locale table, settings page with the session-control group), game-ui (menu entry, HUD stub, settings) | the mod loads from the local Mods folder; "Co-op" appears in the main menu; the HUD stub shows in a city; the settings page works and every string comes from `locale.en-US.json`; "Write diagnostics dump" writes a zip; the self-test report lists every probe; `ApiCheck` passes (members, data-file names, module dependencies); the UI smoke check passes where Node is installed |
| **M1** Transport and session | players find each other | core-transport (incl. `PeerOutbox`), core-session, game-steam-transport, game-ui (ext.: host/join panels, lobby list, chat, player list, mismatch dialog), game-compat (manifest provider, data-file digests, adapter parsing); **research task R-MODS**: decompile the installed Traffic and Road Speed Adjuster DLLs and draft their adapters (ARCH §10.4) | hosting is `LobbyOnly` (no city, ARCH §7.11). Two PCs: lobby + invite and direct IP + password connect, from the `.mjs` panels and from the settings-page controls; chat both ways; ping shown; a deliberately missing mod and a differing data-file override each produce a readable report; the lanes probe result is in the dump; a 200 MB test blob transfers with progress and resumes after a reconnect; the outbox counters show no lost message; the R-MODS write-up exists |
| **M2** Join and follow (spectator) + measurement | a client watches the host's city | core-codec, core-identity, core-follower, core-motion (`ClockModel` only), game-ecs-schema (incl. `MeasurementProbe`, census, load-parity census), game-join (host flow from a save and "Host this city", host session start with binding, session-load set, snapshot, client loader), game-follower (load hand-over, gates, cosmetic runner), game-ui (ext.: save list, host from a save, "Host this city", session-start progress), game-bootstrap (ext.: host sources) | the host picks a save in "Co-op → Host" and "Preparing session" completes; the client joins a 100k city (transfer, load, no first-frame tick); the load hand-over self-test is green (speed reads back, movers interpolate); time, day/night and weather in step; the client's replica fingerprint is unchanged after 10 minutes (nothing simulates locally); host speed changes are mirrored; manual resync works; codec self-test green; the load-parity census reports no differing type; `HostBusy` banner during the snapshot; measurement report produced with the M2 stand-ins (ARCH §13.7) |
| **M3** Live structure | host edits appear on clients | core-replication (S and G path, capture window and command queue, priming, `spawnEmitted`, epochs, epoch parts, epoch log, type-set registry and keyframes, apply planner incl. synthesised `PathUpdated`, pending refs, received-value cache and derived-reference index), core-integrity (ledger and bucket sums for the class digest, line-state report), game-host-capture (tag sweep, capture flush, path-result trigger, ingress hook, discovery, liveness, reverse-buffer co-capture incl. `ConnectedRoute`, K capture of station stops and gate-bypass routes), game-join (ext.: shadow priming during session start), game-client-apply (apply programs, decode-then-commit, staging archetypes, liveness pass, end-of-frame re-application, re-resolution, tags, closure, zone overlays, aggregates, road-choice reconcile, route path events and `RouteReadyBridgeSystem`, names, street-light derivation), game-follower (ext.: line-system gates), game-integrity (record/replay harness with line rows, class digest command, `LineStateProbe`, load-parity comparison) | host builds and bulldozes roads, buildings, zones, districts, lines, upgrades, renames: the client is identical including derived geometry; `/st digest S` equal on both sides; record/replay passes with and without Traffic installed; spawned buildings of every family are canonically equal after the creation frame; a Move It paste of 30k objects on the host arrives as epoch parts and applies; **ST-LINES steps 1-2** (ARCH §4.11.6) pass without the icon checks (icons arrive in M5b): same stops, length and broken segment in the line panel, diagram and list, connected on both screens at once after the gap is closed, a station upgrade keeps the line connected, `/st lines compare` clean; no errors after 30 minutes |
| **M4** Client actions | real co-op building | core-intents, game-client-actions (incl. re-linked waypoints in the host-side effects), core-identity (ext.: build-order re-basing), core-follower (ext.: `PreviewWindow`), game-follower (ext.: tool-scoped gates), game-client-apply (ext.: `RouteGuardSystem`), game-ui (ext.: binding interposer, presence overlay) | the client builds, zones, upgrades, rebuilds, relocates and bulldozes with instant feedback (one check per apply system, leak tracker at 0); every UI action of ARCH §5.3 works from the client; conflicting edits roll back with a toast; a proposal sent during a host snapshot is not rolled back; vanilla speed buttons send requests; Move It works from the client; **ST-LINES step 3**: the client draws and edits a line with the vanilla preview, the host's untouched lines never change on the client meanwhile (guard counters), and re-routing a line to another stop leaves both stops' line lists right on every peer; remote cursors and ghosts are visible |
| **M5a** Living city: values | building, agent and company state converge | core-scheduling (token buckets, link estimator, host budget, P0 plus uniform rolling P3, staleness promotion, automatic speed cap), core-replication (ext.: V/A/A-low classes, value shadow, dirty sets, S/G emission semantics), game-host-capture (ext.: SDC through `ScanScheduler`, hot set), game-integrity (ext.: shadow client) | single machine first: the shadow client reports 0 mismatches on a live city; then two PCs: building condition, households and companies converge (spot checks in panels after waiting), network panel within budget, no repair storm |
| **M5b** Living city: channels | the same numbers everywhere | game-state-channels (system state with stable entry ids, budget arrays, milestone fields, prefab state triggered by the sweep, icons with lane-key owners, **icon authority filter**, names, events, cell maps, statistics), game-client-apply (ext.: event synthesis) | money, XP, milestones (with popup), dev points, unlocks (toolbar updates within a frame of the host's unlock), demand bars, statistics and budget panel identical; icons, fires, crime, abandonment, chirps consistent; **ST-LINES** icon checks of steps 1 and 4 and step 5 without the infoview colours: line "path not found" and "not enough vehicles" warnings, road-access, electricity, water and dead-end warnings identical; self-test (m) green and the dropped-command counters name only expected notification prefabs (road access, turned off) |
| **M5c** Living city: attention | what you look at is fresh and aligned | core-scheduling (ext.: attention model, infoview topics incl. the transportation overview, P1 ordering), game-host-capture (ext.: attention queries), game-state-channels (ext.: **flow-status channel**), game-client-apply (ext.: time-aligned inspection) | the info panel of a selected building or citizen shows the same simulation numbers on both screens at every periodic refresh, the host's included; the line panel's numbers are identical while it is open; infoviews correct in view, including the electricity and water network colours (ST-LINES step 5); P1 staleness in view ≤ 5 s |
| **M6** Traffic and agents | smooth, identical traffic | core-motion (keyframe-referenced records, assignment generations), game-motion, core-scheduling (ext.: degradation ladder), game-host-capture (ext.: mover discrete state, `Passenger` and `RouteVehicle` co-capture), game-client-apply (ext.: traffic-light derivation) | traffic and pedestrians near the camera move identically at speeds 1-4, also with Steam fake loss 5 % and lag 120 ms; no backward snaps; sirens, lights and boarding effects correct; traffic lights animate; vehicles can be clicked and followed; pinning when panning away; **ST-LINES step 4**: line vehicle counts, usage and passenger numbers identical in the line panel and the line list; upload within budget with three clients |
| **M7** Integrity and resilience | self-healing | core-integrity (ext.: beacons with confirmation, repair coordinator, host responder with caps), game-integrity (ext.: rolling verification with pending pairs, repair, write auditor, discovery), core-session and game-join (ext.: short-drop resume with motion reset, snapshot cache, resume points) | injected divergences are repaired automatically; a client whose cable is pulled for 30 s resumes without reloading and its movers keep moving; three joiners in a row share a snapshot; a forced apply exception freezes the client and leads to a resync, never a crash |
| **M8** Fields and mods | terrain, water and the targeted mods | core-fields (incl. brush-strength compensation), game-fields, game-compat (ext.: all adapters including Traffic and Road Speed Adjuster, settings sync, Harmony audit) | terraforming from either side converges (tile hashes equal), also when one PC runs below 20 fps; water visually consistent; the full targeted mod list works in a two-player session (per-mod checklist) |
| **M9** Scale and polish | large cities, many players | all (tuning), core-transport (ext.: `UdpTransport`), core-fields + game-join (ext.: warm join), core-replication (ext.: echo suppression default on, optional delta encodings), game-state-channels (ext.: radio), game-ui (ext.: further locales) | a 100k city with 4+ players over relay for 2-4 hours without an unplanned resync; host overhead < 10 % frame time |

---

## 6. Dependency graph and parallel schedule

### 6.1 Module index

| # | Module | First milestone | Assembly | Folder | Depends on |
|---|---|---|---|---|---|
| 1 | core-contracts | M0 | SkylineTogether.Core | `src/SkylineTogether.Core/Contracts` (+ `Buffers`, `Hashing`, `Json`, `Testing`) | — |
| 2 | core-protocol | M0 | SkylineTogether.Core | `src/SkylineTogether.Core/Protocol` | core-contracts |
| 3 | core-transport | M1 | SkylineTogether.Core | `src/SkylineTogether.Core/Transport` | core-contracts, core-protocol |
| 4 | core-session | M1 | SkylineTogether.Core | `src/SkylineTogether.Core/Session` | core-contracts, core-protocol, core-transport |
| 5 | core-identity | M2 | SkylineTogether.Core | `src/SkylineTogether.Core/Identity` | core-contracts |
| 6 | core-replication | M3 | SkylineTogether.Core | `src/SkylineTogether.Core/Replication` | core-contracts, core-protocol, core-identity |
| 7 | core-scheduling | M5a | SkylineTogether.Core | `src/SkylineTogether.Core/Scheduling` | core-contracts, core-protocol |
| 8 | core-motion | M6 (`ClockModel` M2) | SkylineTogether.Core | `src/SkylineTogether.Core/Motion` | core-contracts, core-protocol |
| 9 | core-intents | M4 | SkylineTogether.Core | `src/SkylineTogether.Core/Intents` | core-contracts, core-protocol |
| 10 | core-integrity | M7 (ledger, sums and line-state report M3) | SkylineTogether.Core | `src/SkylineTogether.Core/Integrity` | core-contracts, core-protocol |
| 11 | core-fields | M8 | SkylineTogether.Core | `src/SkylineTogether.Core/Fields` | core-contracts |
| 12 | game-bootstrap | M0 (`SessionRuntime` M1) | SkylineTogether | `src/SkylineTogether/Bootstrap` | M0: core-contracts, core-protocol. Composition root, extended per milestone: core-session (M1), core-codec, core-identity, core-follower (M2), core-replication, core-integrity (M3), core-intents (M4), core-scheduling (M5a), core-motion (M6), core-fields (M8) |
| 13 | game-steam-transport | M1 | SkylineTogether | `src/SkylineTogether/Platform/Steam` | core-contracts, core-transport, core-session, game-bootstrap |
| 14 | game-ui | M0 | SkylineTogether (+ `ui/`) | `src/SkylineTogether/UI`, `ui/` | M0: core-contracts, core-protocol, game-bootstrap; M1: core-session |
| 15 | game-ecs-schema | M2 | SkylineTogether | `src/SkylineTogether/Ecs` | core-contracts, core-codec, core-identity, core-replication (`PolicyTable`), game-bootstrap |
| 16 | game-join | M2 | SkylineTogether | `src/SkylineTogether/Join` | core-contracts, core-session, core-identity, game-bootstrap, game-ecs-schema, game-follower (`HostSessionAdjustments`, `FollowerMode`) |
| 17 | game-follower | M2 (tool-scoped gates M4) | SkylineTogether | `src/SkylineTogether/Follower` | core-contracts, core-follower, core-motion (`ClockModel`), game-bootstrap |
| 18 | game-host-capture | M3 | SkylineTogether | `src/SkylineTogether/Host` | core-contracts, core-replication, game-bootstrap, game-ecs-schema; M5c: core-scheduling |
| 19 | game-client-apply | M3 | SkylineTogether | `src/SkylineTogether/Client` | core-contracts, core-replication, game-bootstrap, game-ecs-schema |
| 20 | game-state-channels | M5b | SkylineTogether | `src/SkylineTogether/Channels` | core-contracts, core-replication, core-fields, game-bootstrap, game-ecs-schema, game-host-capture, game-client-apply; M5c: core-scheduling |
| 21 | game-client-actions | M4 | SkylineTogether | `src/SkylineTogether/Actions` | core-contracts, core-intents, core-identity, game-bootstrap, game-ecs-schema, game-client-apply, game-host-capture |
| 22 | game-motion | M6 | SkylineTogether | `src/SkylineTogether/Motion` | core-contracts, core-motion, core-scheduling, game-bootstrap, game-ecs-schema, game-host-capture, game-client-apply, game-follower |
| 23 | game-integrity | M3 | SkylineTogether | `src/SkylineTogether/Integrity` | core-contracts, core-session, core-integrity, core-replication, game-bootstrap, game-ecs-schema, game-follower, game-host-capture, game-client-apply, game-join; M7: core-intents |
| 24 | game-fields | M8 | SkylineTogether | `src/SkylineTogether/Fields` | core-contracts, core-session, core-fields, core-intents, game-bootstrap, game-client-apply, game-host-capture, game-client-actions |
| 25 | game-compat | M1 | SkylineTogether | `src/SkylineTogether/Compat` | core-contracts, core-session, game-bootstrap (adapter targets through the hook interfaces of game-bootstrap) |
| 26 | core-codec | M2 | SkylineTogether.Core | `src/SkylineTogether.Core/Codec` | core-contracts |
| 27 | core-follower | M2 (`PreviewWindow` M4) | SkylineTogether.Core | `src/SkylineTogether.Core/Follower` | core-contracts |

Core modules depend only on core-contracts plus, where noted, core-protocol, core-transport or core-identity. Cross-module collaboration inside Core goes through the seams of core-contracts (for example `IPeerScheduler` is implemented by core-scheduling and consumed by core-replication), so the Core modules can be built in parallel. The composition root (`SessionRuntime`, game-bootstrap) wires the implementations together.

**Cross-module hooks in the game assembly** live in game-bootstrap (`SkylineTogether.Bootstrap.Hooks`), so no module needs a later module to compile: `IEndOfFrameHook` (implemented by game-client-actions and game-integrity, called by game-client-apply's `ClientEndOfFrameSystem`), `IGateRegistry` (game-follower), `ILocalEditRegistry` (game-client-actions), `ILoadOverrides` (game-join), `ITypeRuleRegistry` (game-ecs-schema), `IIconAuthority` (game-state-channels), `IHostFlow` and `ISaveListSource` (game-join, consumed by `SessionRuntime`, game-ui and the settings page), `IToolScopedGateState` (game-follower, consumed by game-client-apply's `RouteGuardSystem`), `ILocalEditState` (game-client-actions, consumed by `RouteGuardSystem`; absent before M4, when no local edit exists). game-compat and the composition root find the implementations in the `ServiceRegistry` and skip those that are not registered yet in their milestone. The table above is `Data/modules.json`; `ApiCheck` fails the build when a module references a module it does not declare (§2.4).

### 6.2 Parallel schedule (waves)

| Wave | Can start when | Modules (parallel) |
|---|---|---|
| 0 | immediately | core-contracts (one engineer, first; it is small in logic but defines every seam) |
| 1 | core-contracts merged | core-protocol, core-codec, core-identity, core-follower, core-scheduling, core-motion, core-intents, core-integrity, core-fields, game-bootstrap (skeleton, locale table, settings page), game-ui (skeleton) |
| 2 | core-protocol merged | core-transport, core-session, core-replication; game-steam-transport; game-compat (manifest); research task R-MODS (Traffic, Road Speed Adjuster) |
| 3 | M1 green, core-codec, core-identity, core-follower and core-replication (`PolicyTable`) merged | game-ecs-schema, game-join (host flow, session start, loader), game-follower |
| 4 | game-ecs-schema and core-replication merged | game-host-capture, game-client-apply, game-integrity (replay harness) |
| 5 | M3 green | game-client-actions; then game-host-capture and core-replication extensions for M5a; game-state-channels (M5b); attention (M5c); game-motion (M6) |
| 6 | M5c/M6 green | game-fields, game-compat (full), game-integrity (full) |

Core modules of later milestones (scheduling, motion, intents, integrity, fields) can be finished early because they are pure .NET and fully testable; their game-layer counterparts wait for the in-game milestones.

### 6.3 Integration checkpoints

- **After wave 1**: core-codec golden vectors for every `IWriter`/`IReader` member; core-follower `LoadHandover` and `GatePlanner` tests; core-identity `FamilyRules` rule-by-rule tests.
- **After wave 2**: Core end-to-end test with FakeWorlds over `NetSimTransport` (handshake, session start with a 1 M-entity world, join with a fake snapshot without per-entity P0 traffic, join after type sets were defined, S-class replication, idempotency, two clients with colliding seq/LocalId, outbox under `Busy`).
- **After wave 4**: record/replay in game on one machine (host records, replay client applies), with and without Traffic, before any two-machine structure test, including the ST-LINES line scenario with equal `LineStateProbe` rows; `ApplyConformanceTest` green.
- **After wave 5**: shadow-client self-test on the host (capture and codecs on live data) before the two-machine M5a test.

---

## 7. Modules

Format per module: milestone, assembly, folder, dependencies, responsibility, public contracts (C# signatures; bodies omitted), game APIs with research references, tests, done-when. Signatures are binding: names, parameter order and semantics. Private helpers are free. The two Core modules added in version 1.1, core-codec and core-follower, are specified last (§7.26, §7.27) so that the numbers of the other sections stay stable.

### 7.1 core-contracts

- **Milestone** M0 · **Assembly** `SkylineTogether.Core` · **Folder** `src/SkylineTogether.Core/Contracts` (+ `Buffers/`, `Hashing/`, `Json/`, `Testing/`) · **Depends on** —
- **Responsibility**: every type and interface shared between modules: identity primitives, taxonomies, the transport seam, protocol record headers (with origin on results and binds), the `ApplyPlan` and `ApplyProgram` models, the replication (with the capture-window threading contract), scheduling, integrity, catalog, type-set registry and session seams, data-file models, parsers and digests, the locale table model, byte buffers, xxHash64, a minimal JSON reader/writer and test doubles. Implementations here are small and complete (buffers, hashing, JSON, config parsing, digests, test doubles); everything else is an interface implemented elsewhere.

**Public contracts**

```csharp
namespace SkylineTogether.Core
{
    // ---------- identity primitives (ARCH §3) ----------
    public readonly struct NetSlot : IEquatable<NetSlot>, IComparable<NetSlot> {
        public const uint MaxValue = 0x00FFFFFF;
        public static readonly NetSlot None;
        public readonly uint Value;
        public NetSlot(uint value);                          // throws above MaxValue
        public bool IsNone { get; }
        public int Bucket { get; }                           // Value >> 10 (ARCH §8.2)
        public bool Equals(NetSlot other); public int CompareTo(NetSlot other);
        public static bool operator ==(NetSlot a, NetSlot b); public static bool operator !=(NetSlot a, NetSlot b);
    }
    public readonly struct NetId : IEquatable<NetId> {       // (generation << 24) | slot
        public readonly uint Value;
        public NetId(uint value); public NetId(NetSlot slot, byte generation);
        public NetSlot Slot { get; } public byte Generation { get; }
    }
    public readonly struct LocalId : IEquatable<LocalId> { public readonly uint Value; public LocalId(uint value); }
    public readonly struct PrefabRank : IEquatable<PrefabRank> { public static readonly PrefabRank None; public readonly int Value; public PrefabRank(int value); public bool IsNone { get; } }
    public readonly struct TypeRank : IEquatable<TypeRank>, IComparable<TypeRank> { public readonly ushort Value; public TypeRank(ushort value); }
    public readonly struct TypeSetId : IEquatable<TypeSetId> { public static readonly TypeSetId None; public readonly int Value; public TypeSetId(int value); }
    public readonly struct EpochId : IEquatable<EpochId>, IComparable<EpochId> {
        public readonly uint Value; public EpochId(uint value); public EpochId Next();
        public static bool operator <(EpochId a, EpochId b); public static bool operator >(EpochId a, EpochId b);
        public static bool operator <=(EpochId a, EpochId b); public static bool operator >=(EpochId a, EpochId b);
    }
    public readonly struct PeerId : IEquatable<PeerId> { public static readonly PeerId Host; public readonly byte Value; public PeerId(byte value); public bool IsHost { get; } }
    public readonly struct EntityKey : IEquatable<EntityKey> { public readonly int Index; public readonly int Version; public EntityKey(int index, int version); public bool IsNull { get; } }

    public enum NetRefKind : byte { Null, Local, Slot, Prefab, Provisional, Derived }
    public enum DerivedKeyKind : byte { Lane = 1, SubObject = 2, SecondaryObject = 3, Block = 4, OwnedRoute = 5, FlowEdge = 6, OwnedRouteElement = 7 }
    public readonly struct DerivedKey : IEquatable<DerivedKey> {               // ARCH §3.7
        public readonly DerivedKeyKind Kind;
        public readonly NetSlot Owner;
        public readonly PrefabRank Prefab;
        public readonly int Index;                       // aligned sub-object index, block side, lane flags, owned-route element index, -1 if unused
        public readonly int QX, QY, QZ;                  // positions quantised to 1/64 m
        public readonly ulong A, B, C;                   // lane: start/middle/end PathNode (owner index re-encoded as slot); flow edge: end owner, role;
                                                         // owned-route element: A = 0 waypoint / 1 segment
        public DerivedKey(DerivedKeyKind kind, NetSlot owner, PrefabRank prefab, int index, int qx, int qy, int qz, ulong a, ulong b, ulong c);
        public static int Quantize(float metres);        // (int)Math.Round(metres * 64)
    }
    public readonly struct NetRef : IEquatable<NetRef> {                        // ARCH §4.3.3
        public readonly NetRefKind Kind; public readonly uint Value; public readonly DerivedKey Key;
        public static readonly NetRef Null; public static readonly NetRef Local;
        public static NetRef ForSlot(NetSlot slot); public static NetRef ForPrefab(PrefabRank rank);
        public static NetRef ForProvisional(LocalId id); public static NetRef ForDerived(in DerivedKey key);
    }

    // ---------- taxonomies ----------
    public enum ReplicationClass : byte { Excluded, Derived, S, G, V, A, ALow, M, K, Channel, OnDemand }
    public enum Family : byte { ToolLocal, Prefab, Lane, ZoneBlock, Icon, SecondaryObject, PrefabSubObject, FlowGraph, OwnedRoute,
                                NetNode, NetEdge, Aggregate, Mover, Object, Area, Route, Waypoint, Segment, Agent, TransientAgent,
                                Event, Statistic, Singleton, WaterSource, ModEntity, Generic }
    public enum Priority : byte { P0 = 0, P1 = 1, P2 = 2, P3 = 3, Bulk = 4 }
    public enum Lane : byte { Reliable = 0, Unreliable = 1, Bulk = 2 }
    public enum Delivery : byte { ReliableOrdered = 0, Unreliable = 1 }
    [Flags] public enum TouchFlags : byte { None = 0, Updated = 1, BatchesUpdated = 2, PathfindUpdated = 4, EffectsUpdated = 8 }
    [Flags] public enum SpawnFlags : byte { None = 0, Mover = 1, HasUpdateFrame = 2, Repair = 4 }
    public enum TypeKind : byte { Serializable, EmptyTag, EmptyData, SharedSerializable }
    [Flags] public enum TypeFlags : uint { None = 0, Buffer = 1, Enableable = 2, SerializeAsEnabled = 4, PlayerAuthorable = 8, HashAsSet = 16,
                                           NoReapply = 32, OnDemand = 64, Excluded = 128, Merge = 256, DataReplicatedPresence = 512, Source = 1024,
                                           AuthorableMask = 2048, PresenceAuthorable = 4096, ForwardReference = 8192,
                                           CacheReceived = 16384, Guarded = 32768, PresenceSafe = 65536, DeferredFields = 131072 }   // ARCH §3.7, §4.2.5-4.2.7, §4.7.8, §4.11
    public enum CodecKind : byte { Serializable, EmptyTag, EnableBit, EntityRefList, UpdateFrame, Shared, Custom }
    [Flags] public enum AttentionTopic : uint { None = 0, Happiness = 1, Health = 2, Wellbeing = 4, Education = 8, Age = 16, Wealth = 32, Profitability = 64,
        Crime = 128, Mail = 256, Garbage = 512, Electricity = 1024, Water = 2048, Traffic = 4096, Coverage = 8192, Pollution = 16384,
        LandValue = 32768, NaturalResources = 65536, Groundwater = 131072, Noise = 262144, Telecom = 524288, Transport = 1048576, Statistics = 2097152 }
    public enum SessionRole : byte { Host, CoHost, Builder, Viewer }
    [Flags] public enum Permission : uint { None = 0, Build = 1, Demolish = 2, Terraform = 4, Zoning = 8, Economy = 16, Policies = 32, Progression = 64,
                                           MapTiles = 128, Speed = 256, ModTools = 512, Names = 1024, Chat = 2048, All = 4095 }
    public enum SessionPhase : byte { Idle, PreparingSession, Hosting, Connecting, Handshake, Mismatch, AwaitingSnapshot, Downloading, Loading, CatchingUp,
                                      LiveVerifying, Synced, Repairing, Resyncing, HostBusy, Lagging, Degraded, Disconnected, Faulted, Ended }
    public enum FaultCategory : byte { StructuralApply, ValueApply, Capture, Commit, Snapshot, Transport, Decode, Ui, Probe, Handover }
    public enum RejectReason : ushort { None, Conflict, NoPermission, NotEnoughMoney, Locked, OutsideTiles, Invalid, RateLimited, NotLive, HostError, Timeout, HostOnly,
                                        PrefabClass, Geometry }
    public enum MessageKind : byte { Hello = 1, Welcome, Refuse, Control, Epoch, Ack, Proposal, Motion, MotionControl, Clock, View, Presence,
                                     HashDetail, RepairRequest, BlobManifest, BlobChunk, BlobHave, Ping, Chat }
    public enum SectionType : byte { Tx = 1, Values, SysState, PrefabState, Icons, Events, Names, CellMaps, Clock, Hash, Session, Inspection, TypeSets }
    public enum RecordKind : byte { TypeSetDef = 1, Spawn, Despawn, Struct, Value, Touch, Bind, Pose, Keyed, KeyedDelete, Zone, Icon, Name,
                                    PrefabState, SysState, Result, Hash, Event, TerrainStamp, TypeSetKeyframe }
    public enum ThreadKind : byte { Main, Network, Worker, Any }
    [AttributeUsage(AttributeTargets.All)] public sealed class ThreadAffinityAttribute : Attribute { public ThreadAffinityAttribute(ThreadKind kind); public ThreadKind Kind { get; } }
    [AttributeUsage(AttributeTargets.Method)] public sealed class CaptureWindowAttribute : Attribute { }   // host engine: mutation allowed only inside the capture window (ARCH §4.5.5)

    // ---------- environment ----------
    public enum LogLevel : byte { Trace, Debug, Info, Warn, Error, Critical }
    [ThreadAffinity(ThreadKind.Any)] public interface ILog { bool IsEnabled(LogLevel level); void Write(LogLevel level, string category, string message, Exception exception = null); }
    [ThreadAffinity(ThreadKind.Any)] public interface IClock { long Ticks { get; } double Seconds { get; } }          // monotonic
    public enum CompressionKind : byte { None, ManagedLz4, Zstd, Lz4 }
    [ThreadAffinity(ThreadKind.Any)] public interface ICompressor {
        CompressionKind Kind { get; }
        int MaxCompressedSize(int length);
        int Compress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset);
        int Decompress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset, int expectedLength);    // throws DecodeException
    }
    public sealed class DecodeException : Exception { public DecodeException(string message); }

    // ---------- transport seam (ARCH §9.1) ----------
    public enum TransportKind : byte { SteamP2P, SteamIp, SteamSocketPair, Loopback, NetSim, Udp }
    public enum SendResult : byte { Ok, Busy, Closed, TooLarge, Failed }
    public readonly struct PeerIdentity : IEquatable<PeerIdentity> { public readonly ulong SteamId; public readonly string Address; public readonly string DisplayName;
                                                                   public PeerIdentity(ulong steamId, string address, string displayName); }
    public sealed class ListenOptions { public int VirtualPort; public int IpPort; public bool AllowIp; public int MaxConnections = 8; }
    public sealed class Endpoint { public ulong SteamId; public string Host; public int Port; public int VirtualPort; }
    public struct ConnectionStats { public int PingMs; public float Quality; public int SendRateBytesPerSecond; public int PendingReliableBytes;
                                    public int PendingUnreliableBytes; public long QueueTimeMicroseconds; public float OutBytesPerSecond; public float InBytesPerSecond; }
    [ThreadAffinity(ThreadKind.Network)] public interface ITransportEventSink {
        void OnConnected(IConnection connection);
        void OnDisconnected(IConnection connection, int reason, string text);
        void OnMessage(IConnection connection, Lane lane, byte[] buffer, int offset, int count);   // buffer valid only during the call
    }
    public interface ITransport : IDisposable {
        TransportKind Kind { get; }
        [ThreadAffinity(ThreadKind.Main)] void Listen(ListenOptions options);
        [ThreadAffinity(ThreadKind.Main)] IConnection Connect(Endpoint endpoint);
        [ThreadAffinity(ThreadKind.Network)] void Poll(ITransportEventSink sink);
    }
    [ThreadAffinity(ThreadKind.Any)] public interface IConnection {
        int Id { get; } PeerIdentity Peer { get; }
        SendResult Send(Lane lane, Delivery delivery, byte[] buffer, int offset, int count);       // copies the bytes; Busy = Steam LimitExceeded (send buffer full)
        ConnectionStats GetStats();
        void Close(int reason, string text, bool linger);
    }
    // Reliable traffic never calls IConnection.Send directly: it goes through core-transport's PeerOutbox, which keeps
    // PendingReliableBytes inside a window and re-queues on Busy, so a reliable message is never lost (ARCH §9.3).

    // ---------- protocol record headers (ARCH §4.6.3; encoded by core-protocol) ----------
    public struct SpawnHeader { public NetSlot Slot; public byte Generation; public PrefabRank Prefab; public TypeSetId TypeSet; public SpawnFlags Flags; public byte UpdateFrame; public int ComponentCount; }
    public struct DespawnRecord { public NetSlot Slot; public byte Generation; public bool DeleteChildrenAsHost; }
    public struct StructHeader { public NetSlot Slot; public TypeSetId NewTypeSet; public int AddedComponentCount; }
    [Flags] public enum ValueHeaderFlags : byte { None = 0, HasEnabledBit = 1, Enabled = 2, PresenceOnly = 4 }
    public struct ValueHeader { public NetSlot Slot; public TypeRank Type; public ValueHeaderFlags Flags; }
    public struct TouchRecord { public NetSlot Slot; public TouchFlags Flags; }
    public struct BindRecord { public PeerId Origin; public LocalId Local; public NetSlot Slot; public byte Generation; }   // only Origin acts on it (ARCH §4.6.3)
    public enum IconOp : byte { Add, Remove, RemoveAll, Event }
    public struct IconRecord { public IconOp Op; public NetRef Owner; public PrefabRank Prefab; public byte Priority; public byte Layer; public ushort Flags; public NetRef Target; public bool HasLocation; public float X, Y, Z; }
    public struct ResultRecord { public PeerId Origin; public uint ProposalSeq; public RejectReason Reason; public NetSlot ConflictSlot; }
    public struct HashBeacon { public ReplicationClass Class; public int Bucket; public ulong Sum; public int Count; }
    public enum EventRecordKind : byte { XpToast, MilestoneHint, JournalChanged, RadioTag, HostBusy, SpeedChanged }

    // ---------- apply plan (ARCH §4.7) ----------
    public struct PayloadRef { public int Offset; public int Length; }
    public sealed class PayloadArena { public byte[] Buffer { get; } public int Length { get; } public PayloadRef Append(byte[] src, int offset, int count); public void Clear(); }
    public struct ComponentOp { public TypeRank Type; public PayloadRef Payload; public bool HasEnabledBit; public bool Enabled; public bool PresenceOnly; }
    public struct SpawnOp { public SpawnHeader Header; public int FirstComponent; public ulong EnabledBits; }   // components in ApplyPlan.Components
    public struct StructOp { public StructHeader Header; public int FirstComponent; }
    public struct ValueOp { public NetSlot Slot; public ComponentOp Component; }
    public struct PoseOp { public NetSlot Slot; public PayloadRef Payload; }
    public struct KeyedOp { public DerivedKey Key; public TypeRank Type; public PayloadRef Payload; public bool Delete; }
    public struct ChannelOp { public SectionType Section; public PayloadRef Payload; }
    public struct InspectionBatch { public uint SampleTick; public int FirstValue; public int ValueCount; }            // values in ApplyPlan.InspectionValues
    public sealed class ApplyPlan {
        public EpochId FirstEpoch; public EpochId LastEpoch; public uint HostTick; public bool IsCatchUp; public bool IsRepair;
        public readonly List<BindRecord> Binds; public readonly List<DespawnRecord> Despawns; public readonly List<SpawnOp> Spawns;
        public readonly List<StructOp> Structs; public readonly List<ValueOp> Values; public readonly List<TouchRecord> Touches;
        public readonly List<PoseOp> Poses; public readonly List<KeyedOp> Keyed; public readonly List<ComponentOp> Components;
        public readonly List<ChannelOp> Channels; public readonly List<HashBeacon> Beacons; public readonly List<ResultRecord> Results;
        public readonly List<InspectionBatch> Inspections; public readonly List<ValueOp> InspectionValues; public readonly List<TypeSetDefinition> TypeSets;
        public readonly PayloadArena Arena;
        public bool HasStructural { get; }
        public void Clear();
    }
    public struct TypeSetDefinition { public TypeSetId Id; public TypeRank[] Types; }
    public enum ApplyMode : byte { ClientReplication, HostCommit, Repair, Rollback }
    public struct ApplyResult { public int Applied; public int Parked; public int Failed; public int Replaced; public bool Faulted; public FaultCategory FaultCategory; public string FaultDetail; }

    // ---------- apply programs (ARCH §4.7.2-4.7.4): planned in Core (core-replication ApplyPlanner), executed by the FakeWorld and the game ----------
    public enum ApplyOpKind : byte {
        Unbind,              // slot binding is dead or replaced
        MarkDeleted,         // add Deleted (despawn, replacement, failed creation)
        CreateGroup,         // create Count entities from the staging archetype of (Family, Prefab, TypeSet) without Created/Updated; UpdateFrame for movers
        Bind,                // bind Slot (Generation, Family, Prefab) to the next entity of GroupIndex, or to a BIND-attached prediction
        ReconcileSignature,  // add/remove replicated types so the entity matches TypeSet (executor computes the delta from the real archetype)
        WriteValue,          // decode-then-commit Payload into (Slot, Type); merge types through their merge rule
        SetEnabled,          // enabled bit of (Slot, Type)
        Park,                // value waits for a missing reference (Payload, missing slot in Aux)
        Hold,                // value held for a pending proposal
        AddTags,             // TouchFlags on Slot
        ClosureTag,          // expand the net closure of Slot and tag it (executor reads topology)
        Finalize,            // add Created + Updated (+ Applied for movers) to the entities of GroupIndex
        DeriveStreetLights,  // owner light flags changed (ARCH §6.4)
        SynthesizeEvent      // SynthesizedEventKind in Aux, placed after the values it depends on
    }
    public enum SynthesizedEventKind : byte { RentersUpdated, ColorUpdated, SubObjectsUpdated, PathUpdated }   // PathUpdated: segment path state applied (ARCH §4.11.3)
    public struct ApplyOp { public ApplyOpKind Kind; public NetSlot Slot; public byte Generation; public Family Family; public PrefabRank Prefab; public TypeSetId TypeSet;
                            public int GroupIndex; public int Count; public TypeRank Type; public PayloadRef Payload; public bool Flag; public TouchFlags Tags; public byte UpdateFrame; public uint Aux; }
    public sealed class ApplyProgram { public readonly List<ApplyOp> Ops; public ApplyPlan Source; public int CreatedGroups { get; } public void Clear(); }
    public struct BindingView { public bool Bound; public bool Alive; public EntityKey Entity; public byte Generation; public Family Family; public PrefabRank Prefab; public TypeSetId TypeSet;
                                public double BoundFromPredictionAtSeconds; }   // NaN unless the slot was bound to a prediction by a BIND (ARCH §4.11.4)
    [ThreadAffinity(ThreadKind.Main)] public interface IApplyWorldView { BindingView Lookup(NetSlot slot); bool IsPredictionOf(LocalId local, out EntityKey entity, out PrefabRank prefab, out Family family); }
    public interface IPayloadFacts { bool IsUncomputedPathState(TypeRank type, byte[] buffer, int offset, int count); }   // game layer decodes; empty PathElement / zero distance
    [ThreadAffinity(ThreadKind.Main)] public interface IWorldApplier {
        IApplyWorldView View { get; }
        ApplyResult Execute(ApplyProgram program, ApplyMode mode);   // on an exception: MarkDeleted for every entity the program created, then Faulted
    }

    // ---------- catalog and identity seams (ARCH §3.3-3.6) ----------
    public sealed class TypeDescriptor {
        public TypeRank Rank; public ulong StableTypeHash; public string FullName; public string AssemblyName;
        public TypeKind Kind; public TypeFlags Flags; public CodecKind Codec; public ReplicationClass ClassOverride;   // Excluded = use family default
        public int SizeInChunk; public int ElementSize; public int EntityOffsetCount;
        public TouchFlags ApplyTags; public AttentionTopic Topics; public string MergePolicy;
    }
    public interface ITypeCatalog {
        int Count { get; } ulong Fingerprint { get; } int PolicyVersion { get; }
        TypeDescriptor this[TypeRank rank] { get; }
        bool TryGetByName(string fullName, out TypeDescriptor descriptor);
        bool TryGetByStableHash(ulong stableTypeHash, out TypeDescriptor descriptor);
    }
    public interface ITypeSetRegistry {                                  // one session-global, append-only registry; no per-peer state (ARCH §3.6)
        TypeSetId GetOrAdd(IReadOnlyList<TypeRank> sortedTypes, out bool added);   // host; added → a TYPESET record precedes first use in the shared TX
        bool TryGet(TypeSetId id, out IReadOnlyList<TypeRank> types);   // false = unknown id → resync condition on clients
        IReadOnlyList<TypeRank> Get(TypeSetId id);                       // throws UnknownTypeSetException
        int OrdinalOf(TypeSetId set, TypeRank type);                     // -1 if absent; ordinals < 64
        bool Define(TypeSetId id, IReadOnlyList<TypeRank> sortedTypes);  // client: TYPESET record or keyframe entry; false if it contradicts a known definition
        int Count { get; }
        void WriteKeyframe(Buffers.ByteWriter writer);                   // TSET section: the whole registry as of now
        void ReadKeyframe(Buffers.ByteReader reader);                    // throws DecodeException on a contradiction
        void Clear();
    }
    public sealed class UnknownTypeSetException : Exception { public UnknownTypeSetException(TypeSetId id); public TypeSetId Id { get; } }
    public interface IPrefabIndex { int Count { get; } ulong Digest { get; } PrefabRank RankOfEntityIndex(int prefabEntityIndex); int EntityIndexOf(PrefabRank rank); string Describe(PrefabRank rank);
                                    ulong ClassSignature(PrefabRank rank); }   // presence mask of prefab-data types (ARCH §5.4.1 rule 5)
    public interface ISlotTable {
        int BoundCount { get; } int HighWaterMark { get; }
        bool TryGetSlot(EntityKey entity, out NetSlot slot);
        bool TryGetSlotByIndex(int entityIndex, out NetSlot slot);       // index-only lookup for Write(Entity, ignoreVersion)
        bool TryGetEntity(NetSlot slot, out EntityKey entity);           // the caller still checks liveness with EntityManager.Exists (ARCH §3.3)
        byte GenerationOf(NetSlot slot); Family FamilyOf(NetSlot slot); PrefabRank PrefabOf(NetSlot slot); TypeSetId TypeSetOf(NetSlot slot); ushort RevisionOf(NetSlot slot);
        bool IsSpawnEmitted(NetSlot slot);                               // host (ARCH §3.1)
    }

    // ---------- replication seams (implemented by core-replication) ----------
    public struct SpawnObservation { public EntityKey Entity; public Family Family; public PrefabRank Prefab; public TypeSetId TypeSet; public byte UpdateFrame; public SpawnFlags Flags; }
    [ThreadAffinity(ThreadKind.Main)] public interface IComponentPayloadSource {
        int Count { get; } TypeRank TypeAt(int index);
        void Write(int index, Buffers.ByteWriter writer, out uint rawHash);   // canonical bytes (ARCH §4.3.4)
        bool TryGetEnabled(int index, out bool enabled);
    }
    public enum ObserveSource : byte { Spawn, Edc, Sdc, HotSet, Motion, Coalesced, Repair, Commit,
                                       Prime,       // host session start: baseline only, no dirty bits, no records (ARCH §4.4.8)
                                       CoCapture,   // reverse buffer re-captured with its forward reference: forced into this epoch's P0 TX (ARCH §4.2.6)
                                       PathResult } // segment path state captured on the pathfinder's PathUpdated event: P0, never coalesced (ARCH §4.4.2 step 3a)
    public enum ObserveResult : byte { Unchanged, Changed, New }
    public enum ChannelScope : byte { SharedP0, PerPeerP1, PerPeerP3 }
    [ThreadAffinity(ThreadKind.Main)] public interface IHostWorldReader {      // implemented by game-host-capture
        bool TrySerialize(NetSlot slot, TypeRank type, Buffers.ByteWriter writer, out uint rawHash);
        bool TryDescribe(NetSlot slot, out SpawnObservation spawn, out IComponentPayloadSource payloads);
    }
    [ThreadAffinity(ThreadKind.Any)] public interface IPeerOutput {             // implemented by core-transport's PeerOutbox
        PeerId Peer { get; } ConnectionStats Stats { get; }
        SendResult Send(Lane lane, Delivery delivery, byte[] buffer, int offset, int count);   // reliable: queued, never dropped (Ok or Closed)
        bool CanAcceptReliable(int bytes);                                   // R window has room (the scheduler asks before building value sections)
        long QueuedBytes { get; } double OldestQueuedSeconds { get; }
    }
    public struct JoinTicket { public PeerId Peer; public EpochId SnapshotEpoch; public uint SnapshotTick; public uint HostBuildOrderNext; }
    public struct HostEngineStats { public int Slots; public long HashShadowBytes; public long ValueShadowBytes; public long EpochLogBytes; public int EpochsPerSecond; public int P0BytesPerSecond;
                                    public int QueuedCommands; public int PendingSpawns; public int EpochPartsLastSecond; }
    // Threading contract (ARCH §4.5.5): [CaptureWindow] members may run only between BeginCapture and EndCapture (debug builds assert).
    // Every other mutating member may be called at any time on the main thread; outside the window it is appended to the engine
    // command queue and executed at the next BeginCapture, after the previous frame's workers were joined.
    [ThreadAffinity(ThreadKind.Main)] public interface IHostReplicationEngine {
        EpochId CurrentEpoch { get; } ISlotTable Slots { get; } bool InCaptureWindow { get; } bool AcceptingPeers { get; }
        void BeginCapture(uint hostTick, double nowSeconds);                 // joins workers, drains the command queue, opens the window
        void EndCapture();                                                  // closes the epoch (splits it into parts if needed), hands the frame token to workers
        [CaptureWindow] NetSlot Bind(EntityKey entity, Family family, PrefabRank prefab);              // spawnEmitted = false
        [CaptureWindow] void BindExisting(NetSlot slot, byte generation, EntityKey entity, Family family, PrefabRank prefab);   // after NetIdSystem load
        void BeginSessionStart();                                           // ARCH §4.4.8; AcceptingPeers = false
        [CaptureWindow] void Prime(NetSlot slot, in SpawnObservation spawn, IComponentPayloadSource payloads);   // baselines only; spawnEmitted = true
        void CompleteSessionStart();                                        // AcceptingPeers = true
        [CaptureWindow] int CopyPendingSpawns(List<NetSlot> output);        // bound slots whose spawnEmitted bit is false
        [CaptureWindow] void ObserveSpawn(NetSlot slot, in SpawnObservation spawn, IComponentPayloadSource payloads);   // sets spawnEmitted
        [CaptureWindow] void ObserveDespawn(NetSlot slot);
        bool IsRawUnchanged(NetSlot slot, TypeRank type, uint rawHash);
        [CaptureWindow] ObserveResult ObserveValue(NetSlot slot, TypeRank type, byte[] canonical, int offset, int count, uint rawHash, ObserveSource source);
        [CaptureWindow] void ObserveAuthored(NetSlot slot, TypeRank type, uint projectionHash);         // EDC/commit of a type with an authorable mask (ARCH §3.8)
        [CaptureWindow] void ObserveEnabled(NetSlot slot, TypeRank type, bool enabled);
        [CaptureWindow] void ObserveSignature(NetSlot slot, TypeSetId newSet, IComponentPayloadSource addedPayloads);
        [CaptureWindow] void ObserveTouch(NetSlot slot, TouchFlags flags);
        [CaptureWindow] void ObserveKeyed(in DerivedKey key, TypeRank type, byte[] canonical, int offset, int count, bool playerEdit);
        [CaptureWindow] void ObserveKeyedDelete(in DerivedKey key);
        [CaptureWindow] void BumpRevision(NetSlot slot);                    // commits of proposals
        void EmitChannelRecord(SectionType section, ChannelScope scope, PeerId peerOrHost, byte[] payload, int offset, int count);
        void EmitResult(in ResultRecord result, IReadOnlyList<BindRecord> binds);   // Origin inside the records; shared TX section
        void SetInspectionSample(PeerId peer, uint sampleTick, IReadOnlyList<NetSlot> closure);
        void AddPeer(PeerId peer, IPeerOutput output); void RemovePeer(PeerId peer, bool keepGrace);
        void OnAck(PeerId peer, EpochId epoch);
        void MarkSnapshotFrame(uint hostBuildOrderNext); JoinTicket BeginJoiner(PeerId peer);   // the joiner's first message carries the TSET keyframe
        bool TryResume(PeerId peer, EpochId lastApplied);                   // short-drop resume (ARCH §7.8)
        event Action<PeerId> PeerResumed;                                   // game-motion and channels reset per-peer state
        void MarkDirty(PeerId peer, NetSlot slot, ulong typeMask, Priority priority);          // repair level 2
        void ResendStructural(PeerId peer, NetSlot slot); void ReportNotPresent(PeerId peer, NetSlot slot);
        void StartRebaseline(PeerId peer, ReplicationClass cls);            // repair level 3 (bulk), decided by the host responder
        HostEngineStats GetStats();
    }
    public struct ApplyBudget { public double MaxMilliseconds; public int MaxEpochs; public bool CatchUp; }
    public struct ClientEngineStats { public EpochId LastApplied; public int BacklogEpochs; public long BacklogBytes; public int PendingRecords; public int HeldRecords;
                                      public int PartialEpochs; public long PartialEpochBytes; public int TypeSets; }
    public interface IHoldFilter { bool IsHeld(NetSlot slot, TypeRank type); }   // implemented by core-intents ProposalTracker
    public interface ILocalEditView { bool IsPartOfLocalEdit(NetSlot slot); bool IsCoveredByPendingProposal(NetSlot slot, TypeRank type); }   // route guard (ARCH §4.11.4)
    public interface IClientReplicationEngine {
        [ThreadAffinity(ThreadKind.Network)] void OnEpochMessage(byte[] buffer, int offset, int count);   // assembles epoch parts; TSET loads the registry
        [ThreadAffinity(ThreadKind.Main)] EpochId LastAppliedEpoch { get; }
        [ThreadAffinity(ThreadKind.Main)] PeerId LocalPeer { get; set; }                                // RESULT/BIND of other origins are skipped
        [ThreadAffinity(ThreadKind.Main)] bool TryTakePlan(in ApplyBudget budget, out ApplyPlan plan);
        [ThreadAffinity(ThreadKind.Main)] ApplyProgram Plan(ApplyPlan plan, IApplyWorldView view);      // core-replication ApplyPlanner
        [ThreadAffinity(ThreadKind.Main)] void SetPayloadFacts(IPayloadFacts facts);                    // game-ecs-schema; used by the planner's path-hold rule
        [ThreadAffinity(ThreadKind.Main)] void CompletePlan(ApplyPlan plan, in ApplyResult result);
        [ThreadAffinity(ThreadKind.Main)] void BindSlot(NetSlot slot, byte generation, EntityKey entity, Family family, PrefabRank prefab);
        [ThreadAffinity(ThreadKind.Main)] void UnbindSlot(NetSlot slot);
        [ThreadAffinity(ThreadKind.Main)] void ReportPending(NetSlot slot, TypeRank type, byte[] canonical, int offset, int count, NetSlot missing);
        [ThreadAffinity(ThreadKind.Main)] bool IsPending(NetSlot slot, TypeRank type);                  // parked, held, awaiting re-application or re-resolution (ARCH §8.3)
        [ThreadAffinity(ThreadKind.Main)] void SetHoldFilter(IHoldFilter filter);
        // received-value cache and re-resolution (ARCH §3.7, §4.7.5)
        [ThreadAffinity(ThreadKind.Main)] void RecordReceived(NetSlot slot, TypeRank type, byte[] canonical, int offset, int count, IReadOnlyList<NetSlot> derivedOwnersReferenced);
        [ThreadAffinity(ThreadKind.Main)] bool TryCopyReceived(NetSlot slot, TypeRank type, Buffers.ByteWriter output);   // route guard restores (ARCH §4.11.4)
        [ThreadAffinity(ThreadKind.Main)] void OnDerivedOwnerRegenerated(NetSlot owner);                // from the derived-key resolver's owner invalidation
        [ThreadAffinity(ThreadKind.Main)] bool TryBuildReresolutionPlan(ApplyPlan plan);                // value ops from cached bytes of the dependents; run in ClientEndOfFrameSystem
        [ThreadAffinity(ThreadKind.Main)] void StartCatchUp(EpochId snapshotEpoch);
        [ThreadAffinity(ThreadKind.Main)] bool IsFaulted { get; } event Action<FaultCategory, string> Faulted;   // unknown type set, oversized epoch, plan failure
        [ThreadAffinity(ThreadKind.Main)] void Reset();                                                 // before a snapshot resync: registry, backlog, pending
        [ThreadAffinity(ThreadKind.Main)] ISlotTable Slots { get; }
        [ThreadAffinity(ThreadKind.Main)] ClientEngineStats GetStats();
    }

    // ---------- scheduling seams (implemented by core-scheduling) ----------
    public enum MotionQuality : byte { Full, FarHalf, FarQuarter, Reduced }
    public struct PeerLinkState { public ConnectionStats Stats; public int UserCapBytesPerSecond; public int HostShareBytesPerSecond; public long OutboxQueuedBytes; public bool ReliableWindowOpen; }
    public interface IPeerDirtyView {
        bool AnyDirty { get; } int NextDirtySlot(int fromSlotInclusive);    // -1 when none
        ulong GetMask(NetSlot slot); double DirtyAgeSeconds(NetSlot slot); bool BucketHasDirty(int bucket);
    }
    public interface IRecordEmitter { int EmitValues(PeerId peer, NetSlot slot, ulong typeMask, Priority priority); int EmitChannelBacklog(PeerId peer, Priority priority, int maxBytes); }
    public interface IAttentionView {
        bool IsInView(PeerId peer, NetSlot slot); bool IsInClosure(PeerId peer, NetSlot slot); bool IsInMotionInterest(PeerId peer, NetSlot slot);
        AttentionTopic Topics(PeerId peer); bool TypeMatchesTopics(TypeRank type, AttentionTopic topics);
    }
    public interface IMotionBudgetSink { void SetMotionBudget(PeerId peer, int bytesThisFrame, MotionQuality quality); }
    public struct MotionInterestRegion {                                    // ARCH §4.8.3 item 1
        public float CenterX, CenterZ, Radius, LeaveRadius;                 // metres; LeaveRadius = 1.25 × Radius
        public float[] FrustumXZ;                                           // optional footprint polygon (x0, z0, x1, z1, ...)
        public bool Contains(float x, float z, bool currentlyInside);       // hysteresis
    }
    [ThreadAffinity(ThreadKind.Worker)] public interface IPeerScheduler {
        void BeginFrame(double deltaSeconds, int peerCount);
        int RunPeer(PeerId peer, in PeerLinkState link, int p0BytesThisFrame, IPeerDirtyView dirty, IAttentionView attention,
                    IRecordEmitter emitter, IMotionBudgetSink motion);      // returns bytes scheduled
        bool IsP0Overloaded(PeerId peer, out double backlogSeconds);       // automatic speed cap input (ARCH §4.8.5)
    }

    // ---------- integrity seams (implemented by core-integrity) ----------
    public interface IBucketSink { void OnMemberChanged(ReplicationClass cls, NetSlot slot, ulong oldMember, ulong newMember); }
    public interface IHashLedger : IBucketSink {                            // records every RECEIVED value: applied, parked or held (ARCH §8.3)
        void Record(NetSlot slot, TypeRank type, uint hash); bool TryGet(NetSlot slot, TypeRank type, out uint hash);
        void RemoveSlot(NetSlot slot); ulong BucketSum(ReplicationClass cls, int bucket); int BucketCount(ReplicationClass cls, int bucket);
    }
    public interface IBeaconSource { int AppendBeacons(PeerId peer, IPeerDirtyView dirty, List<HashBeacon> output, int maxBeacons); void EnableClass(PeerId peer, ReplicationClass cls, bool highRate); }
    public interface IHashShadowView {                                      // implemented by core-replication's HashShadow (host)
        bool TryGetCanonical(NetSlot slot, TypeRank type, out uint hash);
        void CollectBucket(ReplicationClass cls, int bucket, List<NetSlot> slots, List<uint> combinedHashes);   // for bucket details (ARCH §8.5 level 1)
        ReplicationClass ClassOf(NetSlot slot);
    }

    // ---------- session seams (implemented by game modules, consumed by core-session) ----------
    public struct HandshakeDigests { public int ProtocolVersion; public ulong ModBuild; public string GameVersion; public string GameFullVersion; public ulong FormatTags;
                                     public ulong Catalog; public ulong Prefabs; public ulong Mods; public ulong Content; public ulong ModSettings; public ulong DataFiles; public string Locale; }
    public sealed class ModEntry { public string Name; public string Version; public string PlatformId; public string DllHash; public bool IsUiModule; public bool RegistersTypesOrSystemsOrPrefabs; }
    public sealed class SettingEntry { public string Mod; public string Key; public string Value; public bool SimulationRelevant; public bool Pushable; }
    public sealed class DataFileDigestEntry { public string File; public ulong Digest; public bool Overridden; }
    public sealed class HandshakeManifest {
        public HandshakeDigests Digests; public List<ModEntry> Mods; public List<string> Content; public List<string> FormatTagNames;
        public List<string> PrefabIds;            // filled only on a prefab digest mismatch
        public List<SettingEntry> Settings;
        public List<DataFileDigestEntry> DataFiles;   // filled only on a data-file digest mismatch (ARCH §4.2.7)
    }
    public interface IGameInfoProvider { HandshakeDigests GetDigests(); HandshakeManifest BuildManifest(bool includePrefabList); }
    public sealed class SnapshotManifest {
        public uint SnapshotId; public long Size; public ulong DigestLo, DigestHi; public int ChunkSize; public EpochId Epoch; public uint FrameIndex;
        public uint HostBuildOrderNext;                                      // ARCH §7.4
        public string GameModeName; public string[] RenderPrefabIds; public string[] ReferencedContent; public Dictionary<Family, int> FamilyCounts;
    }
    public sealed class SnapshotReady { public SnapshotManifest Manifest; public string FilePath; }
    public interface ISnapshotProvider { [ThreadAffinity(ThreadKind.Main)] void RequestSnapshot(Action<SnapshotReady> onReady, Action<string> onFailed); void Release(uint snapshotId); }
    public sealed class LoadOutcome { public bool Success; public string FailureKey; public string FailureDetail; public int Placeholders; public float SlotCoverage; public IReadOnlyList<string> CensusDifferences; }
    public interface IWorldLoader {
        bool IsLoading { get; }
        [ThreadAffinity(ThreadKind.Main)] Stream OpenBlobTarget(SnapshotManifest manifest);   // the transient SaveGameData write stream (core-session's BlobReceiver writes into it)
        [ThreadAffinity(ThreadKind.Main)] void BeginLoad(SnapshotManifest manifest, IReadOnlyList<SettingEntry> pushedSettings, Action<LoadOutcome> onDone);   // loads the opened target
    }

    // ---------- data files (ARCH §4.2.7, §6.3, §10.3, §4.10.1) ----------
    public enum GateAction : byte { Off, OffAndFeed, Keep, OffDuringSessionLoad, ToolScoped }
    public sealed class GateEntry { public string TypeName; public string Phase; public bool Critical; public GateAction Action; public string Reason; }
    public sealed class ToolScopedGateEntry { public string ToolTypeName; public IReadOnlyList<string> Systems; public double LingerSeconds; public string Guard; }   // ARCH §6.3, §4.11.4
    public sealed class GateTable { public int Version; public IReadOnlyList<GateEntry> Entries; public IReadOnlyList<GateEntry> SessionLoadEntries;   // ARCH §6.3
                                    public IReadOnlyList<ToolScopedGateEntry> ToolScopedEntries;
                                    public static GateTable Parse(Json.JsonValue json); }
    public sealed class CosmeticList { public IReadOnlyList<string> SystemTypeNames; public static CosmeticList Parse(Json.JsonValue json); }
    public sealed class TypePolicyEntry { public string TypeName; public Family? Family;               // Family: entry applies to that family only (ARCH §4.2.7)
                                          public ReplicationClass? Class; public CodecKind? Codec; public TypeFlags Flags; public TouchFlags ApplyTags; public AttentionTopic Topics;
                                          public string MergePolicy; public string AuthorableMask; public string StandInResolver; }   // names of hand-written game-layer functions
                                                                                                    // (merge or deferred fields, ARCH §4.2.5; presence-safe stand-ins, §4.11.3)
    public static class DataFileDigest { public static ulong Compute(Json.JsonValue effectiveContent); }   // canonical JSON → xxHash64 (ARCH §4.2.7)
    public sealed class LocaleTable { public string LocaleId; public IReadOnlyDictionary<string, string> Entries; public static LocaleTable Parse(string localeId, Json.JsonValue json); }
    public sealed class PolicyTableModel { public int Version; public IReadOnlyList<TypePolicyEntry> Types; public IReadOnlyDictionary<Family, ReplicationClass> FamilyDefaults; public static PolicyTableModel Parse(Json.JsonValue json); }
    public enum ModClass : byte { WorldAffecting, LocalOnly, Content }
    public sealed class AdapterModel {
        public string Mod; public string Assembly; public string Versions; public ModClass Class;
        public IReadOnlyList<string> ClientOff, ClientKeep, Cosmetic, JoinDisable, LocalTypes, PlayerMutableTypes, DirectWriteTools, HostOnlyActions, SimSettings, ResendAfterJoinTypes;
        public IReadOnlyList<ToolScopedGateEntry> ToolScoped;                                          // mod tools that need local derivation for their previews
        public IReadOnlyList<string> LocalIconPrefabs;                                                 // kept by the icon authority filter (ARCH §4.10.4)
        public bool PushSettings; public IReadOnlyList<SystemStateEntryModel> SystemStateSync; public string Notes;
        public static IReadOnlyList<AdapterModel> ParseList(Json.JsonValue json);
    }
    public enum SystemStateCodecKind : byte { GameSerializable, DeserializeOnly, Reflection, Custom }
    public sealed class SystemStateEntryModel { public string Id; public uint StableId { get; }   // HashMix.Fold32(XxHash64(UTF-8 Id)); the wire identity of the entry (ARCH §4.10.1)
                                                public string SystemTypeName; public SystemStateCodecKind Codec; public double MaxHz; public bool RequiresRoundTrip; public string ReaderHandleMember;
                                                public static IReadOnlyList<SystemStateEntryModel> ParseList(Json.JsonValue json); }   // rejects duplicate StableIds
    public sealed class ConfigValidationException : Exception { public ConfigValidationException(string file, string path, string message); }
}

namespace SkylineTogether.Core.Buffers
{
    public sealed class ByteWriter {
        public ByteWriter(int initialCapacity = 256);
        public byte[] Buffer { get; } public int Length { get; } public void Reset();
        public void WriteByte(byte v); public void WriteUInt16(ushort v); public void WriteUInt32(uint v); public void WriteUInt64(ulong v);
        public void WriteInt32(int v); public void WriteInt64(long v); public void WriteSingle(float v); public void WriteDouble(double v);
        public void WriteVarUInt32(uint v); public void WriteVarUInt64(ulong v); public void WriteVarInt32(int v); public void WriteVarInt64(long v);   // zig-zag for signed
        public void WriteBytes(byte[] src, int offset, int count); public void WriteString(string s, int maxUtf8Bytes);
        public int Reserve(int count); public void PatchUInt32(int position, uint value); public byte[] ToArray();
    }
    public sealed class ByteReader {                       // class: the cursor is shared by all holders (R01 §2.2)
        public ByteReader(); public ByteReader(byte[] buffer, int offset, int count); public void Reset(byte[] buffer, int offset, int count);
        public int Position { get; } public int Remaining { get; } public bool IsAtEnd { get; }
        public byte ReadByte(); public ushort ReadUInt16(); public uint ReadUInt32(); public ulong ReadUInt64(); public int ReadInt32(); public long ReadInt64();
        public float ReadSingle(); public double ReadDouble(); public uint ReadVarUInt32(); public ulong ReadVarUInt64(); public int ReadVarInt32(); public long ReadVarInt64();
        public void ReadBytes(byte[] dst, int offset, int count); public ArraySegment<byte> ReadSegment(int count); public string ReadString(int maxUtf8Bytes); public void Skip(int count);
    }                                                      // every overrun throws DecodeException
    [ThreadAffinity(ThreadKind.Any)] public static class BufferPool { public static byte[] Rent(int minimumLength); public static void Return(byte[] buffer); }
}

namespace SkylineTogether.Core.Hashing
{
    public static class XxHash64 { public static ulong Hash(byte[] data, int offset, int count, ulong seed = 0); public static unsafe ulong Hash(byte* data, int length, ulong seed = 0); }
    public sealed class XxHash64Stream { public XxHash64Stream(ulong seed = 0); public void Append(byte[] data, int offset, int count); public ulong Finish(); public void Reset(); }
    public static class HashMix { public static ulong Mix64(ulong x); public static ulong Combine(ulong a, ulong b); public static uint Fold32(ulong x); }
    public struct Digest128 { public ulong Lo, Hi; public static Digest128 Compute(Stream stream); public static Digest128 Compute(byte[] data, int offset, int count); }
}

namespace SkylineTogether.Core.Json
{
    public enum JsonKind : byte { Null, Bool, Number, String, Array, Object }
    public abstract class JsonValue { public abstract JsonKind Kind { get; } public virtual JsonValue this[string key] { get; } public virtual JsonValue this[int index] { get; }
        public virtual int Count { get; } public virtual IEnumerable<string> Keys { get; } public string AsString(string fallback = null); public double AsNumber(double fallback = 0);
        public bool AsBool(bool fallback = false); public bool Has(string key); }
    public static class MiniJson { public static JsonValue Parse(string text); }                        // throws ConfigValidationException with line/column
    public sealed class JsonWriter { public JsonWriter(TextWriter output, bool indent); public void BeginObject(); public void EndObject(); public void BeginArray(); public void EndArray();
        public void Name(string name); public void Value(string v); public void Value(double v); public void Value(long v); public void Value(bool v); public void Null(); }
}

namespace SkylineTogether.Core.Testing
{
    public sealed class NullLog : ILog { public static readonly NullLog Instance; }
    public sealed class ListLog : ILog { public IReadOnlyList<string> Lines { get; } public int Count(LogLevel atLeast); }
    public sealed class ManualClock : IClock { public void Advance(double seconds); public void Set(double seconds); }
    public sealed class StopwatchClock : IClock { }
    public sealed class DeterministicRandom { public DeterministicRandom(ulong seed); public uint NextUInt(); public int Next(int maxExclusive); public double NextDouble(); }
}
```

- **Game APIs**: none (pure .NET).
- **Tests**: value-type semantics and bounds of every id; `NetSlot.Bucket`; `DerivedKey.Quantize` rounding; `ByteWriter`/`ByteReader` round trips for every primitive and varint edge (0, 127, 128, max, negative zig-zag); overruns throw `DecodeException`; `BufferPool` reuse and thread safety; xxHash64 against published test vectors; `Digest128` stability; `MiniJson` parse/print of the schemas of every data file (incl. the session-load and tool-scoped sections of `gates.json`, family-scoped policy entries with stand-in resolver names, adapters' tool-scoped entries, and `locale.en-US.json`), errors with line/column; every config `Parse` with valid and invalid samples; `DataFileDigest` is independent of key order and whitespace and changes with any value; `SystemStateEntryModel` rejects duplicate stable ids; `ApplyPlan.Clear`, `HasStructural` and `ApplyProgram.Clear`; every locale key referenced by the generated key list exists in `locale.en-US.json`.
- **Done when**: all seams compile, tests pass, and every other module's engineer has signed off the contracts they consume.

### 7.2 core-protocol

- **Milestone** M0 · **Assembly** `SkylineTogether.Core` · **Folder** `src/SkylineTogether.Core/Protocol` · **Depends on** core-contracts
- **Responsibility**: the wire format of ARCH §4.6 and Appendix C: envelope, compression wrapper, fragmentation, epoch sections, epoch parts (split on the host, assembled on the client), record writer and reader (incl. origins on results and binds, type-set keyframes), `NetRef`/derived-key codec, zone-overlay RLE codec, control-message codecs, size limits and validation, and the managed LZ4 fallback compressor.

**Public contracts**

```csharp
namespace SkylineTogether.Core.Protocol
{
    public static class ProtocolVersion { public const int Current = 1; }
    public static class Limits {
        public const int MaxMessageBytes = 512 * 1024; public const int FragmentThreshold = 480 * 1024;
        public const int MaxUncompressedBytes = 8 * 1024 * 1024;                     // one message / one epoch part (ARCH §4.6.5)
        public const long MaxEpochTotalBytes = 256L * 1024 * 1024;                    // all parts of one epoch; above → resync, not a protocol error
        public const int MaxBulkChunkBytes = 256 * 1024; public const int MaxDatagramBytes = 1200; public const int MaxChatChars = 500; public const int MaxNameChars = 64;
        public const int MaxProposalBytes = 4 * 1024 * 1024;                          // one proposal part
        public const long MaxProposalTotalBytes = 64L * 1024 * 1024; public const int MaxProposalEntities = 200_000;
        public const int MaxRepairSlots = 512; public const int CompressThreshold = 256; public const int MaxPartialMessagesPerPeer = 4;
    }
    [Flags] public enum EnvelopeFlags : byte { None = 0, Compressed = 1, Fragment = 2, HasUncompressedSize = 4 }
    public struct Envelope { public MessageKind Kind; public EnvelopeFlags Flags; public byte[] Body; public int BodyOffset; public int BodyCount; }
    [ThreadAffinity(ThreadKind.Any)] public sealed class EnvelopeCodec {
        public EnvelopeCodec(ICompressor compressor);
        public void Encode(MessageKind kind, byte[] body, int offset, int count, ByteWriter output);
        public Envelope Decode(byte[] buffer, int offset, int count, ByteWriter scratch);     // throws DecodeException; enforces Limits
    }
    public sealed class Fragmenter { public void Split(MessageKind kind, byte[] body, int offset, int count, ByteWriter scratch, Action<byte[], int, int> emit); }
    public sealed class Reassembler { public bool Accept(Envelope fragment, out byte[] message, out int length); public void Reset(); }
    public sealed class ManagedLz4Compressor : ICompressor { }                                  // pure C# LZ4 block format (fallback, tests)

    [Flags] public enum EpochFlags : byte { None = 0, SnapshotBoundary = 1, CatchUp = 2, ChannelKeyframe = 4, MultiPart = 8 }
    public struct EpochHeader { public EpochId Epoch; public uint HostTick; public EpochFlags Flags; public int SectionCount; public int PartIndex; public int PartCount; }
    public sealed class EpochWriter {
        public void Begin(EpochId epoch, uint hostTick, EpochFlags flags, ByteWriter output, int partIndex = 0, int partCount = 1);
        public void AppendSection(SectionType type, byte[] body, int offset, int count);
        public int End();                                                                     // returns total length
    }
    public sealed class EpochReader {
        public EpochHeader Open(byte[] buffer, int offset, int count);
        public bool NextSection(out SectionType type, out int bodyOffset, out int bodyCount);
    }
    public sealed class EpochPartSplitter {                                                    // host: never emits a body above Limits.MaxUncompressedBytes
        public void Split(EpochId epoch, uint hostTick, EpochFlags flags, byte[] txRecords, int txOffset, int txCount, IReadOnlyList<ArraySegment<byte>> otherSections,
                          Action<byte[], int, int> emitPart);                                  // splits TX at record boundaries; header sections in part 0, per-peer sections in the last
    }
    public enum PartResult : byte { Incomplete, Complete, Oversized, Invalid }
    public sealed class EpochPartAssembler {                                                   // client; spills above 32 MB to a temp file
        public EpochPartAssembler(string spillDirectory, long spillThresholdBytes = 32L << 20);
        public PartResult Accept(in EpochHeader header, byte[] buffer, int offset, int count, out Stream completeEpoch);   // Oversized → resync (ARCH §12.2)
        public int PartialEpochs { get; } public long PartialBytes { get; } public void Reset();
    }
    public static class NetRefCodec { public static void Write(ByteWriter w, in NetRef value); public static NetRef Read(ByteReader r); }
    public sealed class RecordWriter {
        public RecordWriter(ByteWriter target);
        public void TypeSetDef(TypeSetId id, IReadOnlyList<TypeRank> types);
        public void BeginSpawn(in SpawnHeader header); public void Component(TypeRank type, byte[] payload, int offset, int count); public void EndSpawn(ulong enabledBits);
        public void Despawn(in DespawnRecord record);
        public void BeginStruct(in StructHeader header); public void EndStruct();               // added components via Component(...)
        public void Value(in ValueHeader header, byte[] payload, int offset, int count);
        public void Touch(in TouchRecord record); public void Bind(in BindRecord record);
        public void Pose(NetSlot slot, byte[] payload, int offset, int count);
        public void Keyed(in DerivedKey key, TypeRank type, byte[] payload, int offset, int count); public void KeyedDelete(in DerivedKey key);
        public void Zone(in DerivedKey blockKey, ushort revision, byte[] rle, int offset, int count);
        public void Icon(in IconRecord record); public void Name(in NetRef target, string text);
        public void SysState(uint entryStableId, byte[] blob, int offset, int count); public void Result(in ResultRecord record); public void Hash(in HashBeacon beacon);
        public void Event(EventRecordKind kind, byte[] payload, int offset, int count); public void TerrainStamp(byte[] payload, int offset, int count);
        public void TypeSetKeyframe(ITypeSetRegistry registry);                                  // TSET section
        public int RecordCount { get; }
    }
    public sealed class RecordReader {
        public void Reset(byte[] buffer, int offset, int count);
        public bool Next(out RecordKind kind);
        public TypeSetDefinition ReadTypeSetDef();
        public SpawnHeader ReadSpawnHeader(); public bool NextComponent(out TypeRank type, out int payloadOffset, out int payloadCount); public ulong ReadEnabledBits();
        public DespawnRecord ReadDespawn(); public StructHeader ReadStructHeader();
        public ValueHeader ReadValue(out int payloadOffset, out int payloadCount);
        public TouchRecord ReadTouch(); public BindRecord ReadBind(); public NetSlot ReadPose(out int payloadOffset, out int payloadCount);
        public DerivedKey ReadKeyed(out TypeRank type, out int payloadOffset, out int payloadCount, out bool isDelete);
        public DerivedKey ReadZone(out ushort revision, out int rleOffset, out int rleCount);
        public IconRecord ReadIcon(); public NetRef ReadName(out string text); public uint ReadSysState(out int blobOffset, out int blobCount);
        public ResultRecord ReadResult(); public HashBeacon ReadHash(); public EventRecordKind ReadEvent(out int payloadOffset, out int payloadCount);
        public void ReadTerrainStamp(out int payloadOffset, out int payloadCount);
        public void ReadTypeSetKeyframe(ITypeSetRegistry into);                                  // throws DecodeException on a contradiction
    }
    public static class ZoneOverlayCodec { public static void Encode(ushort[] zoneRanks, int cellCount, ByteWriter output); public static int Decode(ByteReader input, ushort[] zoneRanks); }

    // control messages (each: static Write(ByteWriter, T) / static T Read(ByteReader))
    public sealed class HelloMessage { public HandshakeDigests Digests; public ulong SteamId; public string DisplayName; public byte[] ClientNonce;
                                       public byte CompressionMask; }                                       // wave 1: bit per CompressionKind the client can decode
    public static class CompressionNegotiation {                                                // wave 1 (ARCH §4.6.5)
        public const CompressionKind Handshake = CompressionKind.ManagedLz4;                    // codec of Hello/Welcome/Refuse and pre-Welcome control messages
        public const byte AlwaysSupported, DefinedBits; public static byte Bit(CompressionKind kind); public static byte LocalMask(bool nativeZstd, bool nativeLz4);
        public static bool Supports(byte mask, CompressionKind kind); public static CompressionKind Choose(byte hostMask, byte clientMask);
        public static CompressionKind CompressorKindFor(CompressionKind session, byte localMask);
    }
    public sealed class WelcomeMessage { public uint SessionId; public PeerId AssignedPeer; public SessionRole Role; public Permission Permissions; public byte[] SessionSettings; public byte[] HostNonce;
                                         public uint[] EnabledSysEntries;                                    // ARCH §4.10.1
                                         public CompressionKind Compression; }                               // wave 1: the negotiated session codec
    public sealed class RefuseMessage { public RefuseReason Reason; public string Detail; public byte[] MismatchReport; }
    public enum RefuseReason : byte { Mismatch, Full, Banned, BadPassword, NotInvited, HostShuttingDown, ProtocolVersion, HostPreparing }
    public sealed class ManifestDetailMessage { public HandshakeManifest Manifest; }
    public sealed class PasswordChallengeMessage { public byte[] HostNonce; public byte[] Salt; public uint SessionId; }
    public sealed class PasswordResponseMessage { public byte[] ClientNonce; public byte[] Mac; }
    public enum ControlKind : byte { PasswordChallenge, PasswordResponse, ManifestRequest, ManifestDetail, SessionSettings, Players, Roles, HostBusy, HostReady,
                                     SessionEnd, SessionReset, Kick, Ban, SpeedRequest, SpeedChanged, ResyncRequest, ResyncStart, Toast, SnapshotManifest,
                                     ReadyForSnapshot, LoadDone, Live, ResumeRequest, ResumeResult, ProposalReceived, RepairThrottled, Disconnecting,
                                     LineRowsRequest, LineRows }                                   // "/st lines compare": host rows at one sample tick (ARCH §4.11.6)
    public sealed class ControlMessage { public ControlKind Kind; public byte[] Payload; }
    public sealed class ChatMessage { public PeerId From; public string Text; public bool IsSystem; }
    public sealed class ViewReport { public float PivotX, PivotZ, Zoom, Yaw; public float CamX, CamY, CamZ; public byte Infoview; public uint InfomodeMask;
                                     public NetSlot Selected, Followed; public ushort PanelFlags; public NetSlot PanelTarget; }
    public sealed class PresenceMessage { public PeerId From; public float CursorX, CursorY, CursorZ; public byte ToolKind; public PrefabRank Prefab; public uint Color;
                                          public float[] CurveControlPoints; public float[] Footprints; public float BrushRadius; }
    public sealed class ClockSample { public uint HostFrame; public float FrameTime; public float SmoothSpeed; public byte SessionSpeed; public bool Paused; public double HostTimeSeconds; }
    public sealed class AckMessage { public EpochId LastApplied; public uint LastMotionControlSeq; }      // the second field confirms interest-slot releases (ARCH §4.9.3)
    public sealed class ProposalReceivedMessage { public uint Seq; }                                       // control payload, host → originator on receipt
    public sealed class PingMessage { public uint Sequence; public double SentSeconds; public bool IsReply; }
    public sealed class HashDetailMessage { public ReplicationClass Class; public int Bucket; public EpochId AtEpoch; public NetSlot[] Slots; public uint[] Hashes; }
    public sealed class RepairRequestMessage { public ReplicationClass Class; public NetSlot[] Slots; public RepairLevel LevelHint; }   // the host decides (ARCH §8.5)
    public enum RepairLevel : byte { Leak, Entity, Bucket, Rebaseline, Snapshot }
}
```

- **Wave 1 integration notes** (additive; no frozen signature changed):
  - Envelope: a compressed body is `varint uncompressedSize | u8 compressionKind | data` (ARCH §4.6.1), pinned by the `EnvelopeCodec` golden vectors. The receiver decodes by the kind each envelope names (LZ4 blocks always, through the managed fallback).
  - Handshake codec rule (ARCH §4.6.5): see `CompressionNegotiation.Handshake` above and the core-session note in §7.4.
  - Public extras other modules use: `RecordWriter.PrefabState` / `RecordReader.ReadPrefabState`, `EpochStreamReader` (reads a spilled multi-part epoch from a `Stream`), `SnapshotManifestCodec`, `HelloMessage.PeekProtocolVersion`, a whole-body `Decode(byte[], int, int)` on every message, allocation-free `ReadInto` on `ViewReport`, `ClockSample`, `AckMessage`, `PingMessage` and `PresenceMessage`, `ControlMessage.IsKnownKind`, `Limits.MaxBodyBytes(kind)` / `Limits.IsKnownKind`, and `EpochPartAssembler : IDisposable`.
  - Record exactness (ARCH §4.6.3): ICON writes prefabRank+1 and flags the optional target/location in bits 6/7 of the op byte; ZONE, POSE, PFB and TSETKEY carry a length-prefixed payload; RESULT's status byte has bit 0 = rejected, bit 1 = conflict slot present.
  - Failure semantics: after a `DecodeException` from any `RecordReader.Read*` or `Next`, the reader refuses further reads until `Reset` (it never resynchronises inside a section; ARCH §14.3); the only exception is an exception from the registry inside `ReadTypeSetKeyframe`, whose record is already consumed. `EpochStreamReader` likewise refuses further reads after a failure until `Open`. `ZoneOverlayCodec.Decode` leaves partial output on failure, so decode into a scratch array.
  - `EpochPartSplitter` does not refuse epochs above `Limits.MaxEpochTotalBytes`; the host (core-replication) caps or splits such transactions across epochs before calling `Split`.
- **Game APIs**: none. Message cap 512 KiB from R06 §4.2 (`Constants.cs:253`).
- **Tests**: golden byte vectors for every record and message (results and binds with origin, TSET keyframe); round trips; fuzzing of `EnvelopeCodec.Decode`, `RecordReader` and every message reader (only `DecodeException` may escape); limits enforced (oversized declared sizes rejected before decompression); fragment/reassemble with loss and reordering of fragments rejected cleanly; epoch parts: a 100 MB synthetic transaction is split into parts ≤ 8 MiB at record boundaries and reassembled byte-identical, out-of-order and duplicate parts handled, an epoch above 256 MiB reports `Oversized`; `NetRefCodec` for every kind including all derived keys (subkinds 1-7, owned-route elements included); `ICON` records whose owner and target are derived keys; zone RLE with random cell patterns; managed LZ4 round trips and compatibility with the reference block format.
- **Done when**: wire format frozen as `ProtocolVersion 1` and covered by golden vectors.

### 7.3 core-transport

- **Milestone** M1 · **Assembly** `SkylineTogether.Core` · **Folder** `src/SkylineTogether.Core/Transport` · **Depends on** core-contracts, core-protocol
- **Responsibility**: the per-peer reliable outbox (ARCH §9.3), test and fallback transports and the blob transfer protocol (ARCH §7.5, §9.4): in-process loopback, the network simulator, chunked windowed resumable transfers, and (M9) the LAN UDP transport.

**Public contracts**

```csharp
namespace SkylineTogether.Core.Transport
{
    public sealed class LoopbackTransport : ITransport { public static (LoopbackTransport host, LoopbackTransport client) CreatePair(); }
    public sealed class NetSimOptions { public double LossRate; public double DuplicateRate; public double ReorderRate; public double LatencyMs; public double JitterMs;
                                        public int BandwidthBytesPerSecond; public int Seed; }
    public sealed class NetSimTransport : ITransport { public NetSimTransport(ITransport inner, NetSimOptions options, IClock clock); public NetSimOptions Options { get; set; }
                                                       public double BusyRate { get; set; } }   // simulates Steam LimitExceeded for outbox tests
    public sealed class OutboxConfig {
        public int SendBufferBytes = 16 << 20;              // configured on the Steam connection (ARCH §7.5)
        public int ReliableHeadroomBytes = 1 << 20;         // R may fill the buffer up to SendBufferBytes − headroom
        public int BulkWindowMinBytes = 2 << 20, BulkWindowMaxBytes = 8 << 20;   // B only with an empty R queue
        public long DisconnectQueuedBytes = 64L << 20; public double DisconnectQueuedSeconds = 60;
    }
    public sealed class PeerOutbox : IPeerOutput {
        public PeerOutbox(IConnection connection, PeerId peer, OutboxConfig config, IClock clock, ILog log);
        // IPeerOutput.Send: unreliable → straight to the connection (dropped if refused); reliable → queued per lane, never dropped
        [ThreadAffinity(ThreadKind.Network)] public void Pump();                  // hands queued messages to Steam inside the windows; Busy keeps the head
        public bool OverLimit { get; }                                           // queued bytes or age above the disconnect thresholds (ARCH §4.8.5)
        public long SentReliableMessages { get; } public long BusyRetries { get; }
        // + CanAcceptReliable, QueuedBytes, OldestQueuedSeconds, Stats
    }
    public sealed class BlobManifestInfo { public uint BlobId; public long Size; public int ChunkSize; public Digest128 Digest; public string Purpose; }
    public sealed class BlobSender {
        public BlobSender(PeerOutbox output, ILog log, int minWindowBytes = 2 << 20, int maxWindowBytes = 8 << 20);   // chunks go through the bulk lane window
        public BlobManifestInfo Start(uint blobId, Stream source, string purpose);              // source must be seekable
        [ThreadAffinity(ThreadKind.Network)] public void Pump();                                // keeps PendingReliable within the window
        public void OnHave(byte[] bitmap, int offset, int count);                              // resume after reconnect
        public bool IsComplete { get; } public long BytesSent { get; } public void Cancel();
    }
    public sealed class BlobReceiver {
        public BlobReceiver(BlobManifestInfo manifest, Stream target, ILog log);               // target: preallocated file or asset write stream
        [ThreadAffinity(ThreadKind.Network)] public bool OnChunk(byte[] buffer, int offset, int count);   // verifies per-chunk xxHash64; false on corruption
        public byte[] BuildHaveBitmap(); public bool IsComplete { get; } public bool VerifyDigest(); public long BytesReceived { get; } public double EtaSeconds(double bytesPerSecond);
    }
    public static class BlobCodec { public static void WriteManifest(ByteWriter w, BlobManifestInfo m); public static BlobManifestInfo ReadManifest(ByteReader r);
                                    public static void WriteChunkHeader(ByteWriter w, uint blobId, int index, int length, ulong hash); }
    public sealed class UdpTransport : ITransport { public UdpTransport(string password, ILog log); }      // M9: reliability, fragmentation, HMAC per packet
}
```

- **Game APIs**: none (`System.Net.Sockets` for `UdpTransport`, available in the game's Mono, R06 §6).
- **Tests**: loopback ordering per lane; NetSim statistics (loss, duplication, reordering, bandwidth cap) within tolerance; **outbox**: a fake connection answering `Busy` for random stretches loses no reliable message and keeps per-lane order, bulk never starves R (R latency bounded while a blob transfers), `CanAcceptReliable` turns false at the window, `OverLimit` trips at the thresholds; blob transfer of 300 MB-equivalent synthetic streams under 5 % loss and reordering; resume after simulated disconnect transfers only missing chunks; corrupted chunk detected; digest mismatch detected; memory stays bounded (no full-blob buffers).
- **Done when**: the E2E harness can move a snapshot-sized blob through NetSim with resume while epochs keep flowing on lane R.

### 7.4 core-session

- **Milestone** M1 · **Assembly** `SkylineTogether.Core` · **Folder** `src/SkylineTogether.Core/Session` · **Depends on** core-contracts, core-protocol, core-transport
- **Responsibility**: connection and session state for host and clients (ARCH §7, §9.7, §14): admission (lobby membership, invite, ban, direct-IP password challenge; `HostPreparing` refusal until host session start completed), handshake and the compatibility report (incl. data-file digests and the enabled system-state entries), players, roles and permissions, chat, session settings, speed requests and policy, heartbeats and timeouts, `HostBusy`/`HostReady`, the proposal receipt echo, the join coordinator (snapshot cache, concurrent transfer limit, joiner queue), resync and session reset, short-drop resume bookkeeping, the lobby directory model, and the message router other engines register with.

**Public contracts**

```csharp
namespace SkylineTogether.Core.Session
{
    public enum LobbyPrivacy : byte { FriendsOnly, Private, Public }
    public enum HostSourceKind : byte { LobbyOnly, Save, CurrentCity }                         // ARCH §7.11
    public readonly struct HostSource { public readonly HostSourceKind Kind; public readonly string SaveId;
                                        public static HostSource LobbyOnly(); public static HostSource Save(string saveId); public static HostSource CurrentCity(); }
    public sealed class LobbyInfo { public ulong LobbyId; public ulong OwnerSteamId; public string City; public int Population; public int Players; public int MaxPlayers;   // moved from game-steam-transport
                                    public int ProtocolVersion; public ulong GameDigest, ModsDigest, ContentDigest; public bool PasswordRequired; public int PingEstimateMs; public bool Compatible; }
    public enum SpeedPolicy : byte { HostOnly, HostAndCoHosts, Anyone, MajorityVote }
    public enum WaterQuality : byte { Off, Low, Medium, High }
    public sealed class SessionSettings {
        public string SessionName; public LobbyPrivacy Privacy; public int MaxPlayers = 8; public SessionRole DefaultRole = SessionRole.Builder;
        public SpeedPolicy SpeedPolicy = SpeedPolicy.HostAndCoHosts; public bool AutoSpeedCap = true; public bool PauseWhenHostInMenu;
        public int HostUploadCapBytesPerSecond = 1_500_000; public int PerPeerCapBytesPerSecond = 300_000; public bool AllowLocalOnlyModDifferences;
        public bool AuthoritativeWait; public WaterQuality WaterQuality = WaterQuality.Low; public bool ResumePoints; public List<SettingEntry> PushedModSettings;
        public void Write(ByteWriter w); public static SessionSettings Read(ByteReader r); public SessionSettings Clone();
    }
    public static class RoleDefaults { public static Permission For(SessionRole role); }
    public sealed class PlayerInfo { public PeerId Peer; public ulong SteamId; public string Name; public uint Color; public SessionRole Role; public Permission Permissions;
                                     public SessionPhase Phase; public int PingMs; public float LinkQuality; public string CurrentTool; }
    public sealed class ChatLine { public double Time; public PeerId From; public string Name; public string Text; public bool IsSystem; }
    public enum MismatchCategory : byte { Protocol, ModBuild, GameVersion, FormatTags, TypeCatalog, DataFiles, Mods, UiModules, Content, Prefabs, RenderPrefabs, ModSettings, Locale }
    public sealed class MismatchEntry { public MismatchCategory Category; public string Item; public string HostValue; public string ClientValue; public string FixHintKey; public bool Blocking; }
    public sealed class MismatchReport { public IReadOnlyList<MismatchEntry> Entries; public bool IsBlocking { get; } public string ToText(); public void Write(ByteWriter w); public static MismatchReport Read(ByteReader r); }
    public sealed class CompatibilityOptions { public bool AllowLocalOnlyModDifferences; public IReadOnlyCollection<string> LocalOnlyAllowList; }
    public static class CompatibilityChecker {
        public static IReadOnlyList<MismatchCategory> CompareDigests(in HandshakeDigests host, in HandshakeDigests client);
        public static MismatchReport Compare(HandshakeManifest host, HandshakeManifest client, CompatibilityOptions options);
    }
    public static class Pbkdf2 { public static byte[] HmacSha256(byte[] password, byte[] salt, int iterations, int length); }   // own implementation over HMACSHA256 (netstandard2.0)
    public static class PasswordChallenge {
        public static byte[] NewNonce();                                                      // 16 bytes, RandomNumberGenerator
        public static byte[] DeriveKey(string password, byte[] salt, int iterations = 10000);   // Pbkdf2.HmacSha256(UTF-8 password, salt, iterations, 32) (ARCH §14.2)
        public static byte[] Respond(byte[] key, byte[] hostNonce, byte[] clientNonce, uint sessionId);
        public static bool Verify(byte[] key, byte[] hostNonce, byte[] clientNonce, uint sessionId, byte[] mac);   // constant-time compare
    }
    public enum JoinStage : byte { Handshake, WaitingForSnapshot, Downloading, Loading, CatchingUp, Verifying, Done, Failed }
    public struct JoinProgress { public JoinStage Stage; public long BytesDone; public long BytesTotal; public double EtaSeconds; public int EpochsBehind; public float Fraction; }
    public interface ISessionEvents {                                                        // implemented by game-ui
        void OnPhaseChanged(SessionPhase phase, string detailKey); void OnPlayersChanged(IReadOnlyList<PlayerInfo> players);
        void OnChat(ChatLine line); void OnMismatch(MismatchReport report); void OnJoinProgress(in JoinProgress progress);
        void OnToast(string localeKey, string argument); void OnSpeedChanged(int speed, bool paused, PeerId changedBy);
        void OnSessionStartProgress(float fraction);                                         // "Preparing session n %" (ARCH §4.4.8)
    }
    public interface IMessageHandler { void Handle(PeerId from, MessageKind kind, byte[] buffer, int offset, int count); }
    public interface IMessageRouter {
        void RegisterMainThread(MessageKind kind, IMessageHandler handler);                  // queued, dispatched in SessionController.Update
        void RegisterNetworkThread(MessageKind kind, IMessageHandler handler);               // called on the network thread (motion, epochs)
    }
    public interface IJoinHooks {                                                            // implemented by the composition root (game-bootstrap)
        JoinTicket BeginJoiner(PeerId peer);                                                 // host, in the snapshot frame
        void OnPeerLive(PeerId peer); void OnPeerLeft(PeerId peer, bool keepGrace);
        bool TryResume(PeerId peer, EpochId lastApplied);                                    // host
        void OnSnapshotLoaded(SnapshotManifest manifest);                                    // client: engine catch-up from manifest.Epoch
        bool IsCaughtUp { get; } float CatchUpFraction { get; }                              // client
        void OnSessionEnded(string reasonKey);
    }
    public sealed class SessionEnvironment { public ILog Log; public IClock Clock; public ICompressor Compressor; public IGameInfoProvider GameInfo;
        public ISnapshotProvider Snapshots; public IWorldLoader Loader; public ISessionEvents Events; public IJoinHooks JoinHooks; public string TempDirectory; }
    public sealed class SessionController : IMessageRouter, IDisposable {
        public SessionController(SessionEnvironment environment);
        public SessionPhase Phase { get; } public bool IsHost { get; } public bool IsClient { get; } public PeerId LocalPeer { get; } public uint SessionId { get; }
        public IReadOnlyList<PlayerInfo> Players { get; } public SessionSettings Settings { get; } public int SessionSpeed { get; } public bool SessionPaused { get; }
        public void StartHosting(SessionSettings settings, ITransport primary, ITransport directIp, string password);   // called by SessionRuntime after host session start
        public bool AcceptingPeers { get; set; }                                               // false → Refuse{HostPreparing}
        public uint[] EnabledSysEntries { get; set; }                                          // announced in Welcome (ARCH §4.10.1)
        [ThreadAffinity(ThreadKind.Any)] public void SendProposalReceived(PeerId peer, uint seq);   // echo on receipt (ARCH §5.4.2)
        public void SetRole(PeerId peer, SessionRole role); public void SetPermission(PeerId peer, Permission flag, bool value);
        public void Kick(PeerId peer, string reasonKey); public void Ban(PeerId peer); public void UpdateSettings(SessionSettings settings);
        public void AnnounceHostBusy(string reasonKey, double expectedSeconds); public void AnnounceHostReady();
        public void SetSessionSpeed(int speed, bool paused, PeerId requestedBy); public void ResetSession(string reasonKey); public void EndSession(string reasonKey);
        public void Join(ITransport transport, Endpoint endpoint, string password);
        public void Leave(string reasonKey); public void RequestResync();
        public void SendChat(string text); public void RequestSpeed(int speed, bool paused);
        public bool HasPermission(PeerId peer, Permission permission);
        public IPeerOutput GetOutput(PeerId peer);
        public void RegisterMainThread(MessageKind kind, IMessageHandler handler); public void RegisterNetworkThread(MessageKind kind, IMessageHandler handler);
        [ThreadAffinity(ThreadKind.Main)] public void Update();                                // state machines, timeouts, main-thread dispatch
        [ThreadAffinity(ThreadKind.Network)] public void PollNetwork();
        public event Action<PeerId, EpochId> PeerAck;
        public void Dispose();
    }
    public sealed class JoinCoordinator {                                                    // host; public for tests
        public JoinCoordinator(ISnapshotProvider provider, IClock clock, int maxConcurrentTransfers = 2, double cacheSeconds = 120, long maxLogBytesSinceSnapshot = 64L << 20);
        public void Enqueue(PeerId peer); public void Cancel(PeerId peer); public void Tick(Func<long> logBytesSinceSnapshot);
        public bool TryGetSnapshotFor(PeerId peer, out SnapshotReady snapshot); public int ActiveTransfers { get; }
    }
    public sealed class SpeedVote { public SpeedVote(SpeedPolicy policy); public bool Submit(PeerId peer, SessionRole role, int speed, bool paused, int livePlayers, out int resultSpeed, out bool resultPaused); }
}
```

- **Wave 1 note (from core-protocol, ARCH §4.6.5)**: encode `Hello`, `Welcome`, `Refuse` and the pre-Welcome control messages with an `EnvelopeCodec` on `ManagedLz4Compressor` (`CompressionNegotiation.Handshake`). Decode the whole connection with one codec built on the best local compressor (`CompressorKindFor(LocalMask(...))` semantics); never swap the decoder. Switch only the encoding codec, to `CompressorKindFor(welcome.Compression, localMask)`, once `Welcome` is sent (host) or processed (client). Always pass a spill directory to `EpochPartAssembler` (without one a near-256 MiB epoch is held in memory and copied once).
- **Game APIs**: none directly (Steam identity arrives through `ITransport`/`PeerIdentity`).
- **Tests**: host/client state machines over `LoopbackTransport` and `NetSimTransport` (happy path, refusal on each mismatch category incl. data files, `HostPreparing` while session start runs, bad password, ban, full session, timeouts, disconnect mid-transfer, reconnect within grace, resync, session reset, host end); `CompatibilityChecker` with crafted manifests (local-only relaxation only for the allow-list and only without types/systems/prefabs); `Pbkdf2.HmacSha256` against the RFC 7914 §11 vectors on both target frameworks; `PasswordChallenge` vectors and constant-time verify; `ProposalReceived` echo sent on receipt; `JoinCoordinator` cache reuse within 120 s and limit of two transfers; `SpeedVote` for each policy; rate limits on chat.
- **Done when**: two Core sessions connect, chat, exchange a mismatch report, transfer a snapshot blob and reach `CatchingUp` in the E2E harness.

### 7.5 core-identity

- **Milestone** M2 (build-order re-basing in M4) · **Assembly** `SkylineTogether.Core` · **Folder** `src/SkylineTogether.Core/Identity` · **Depends on** core-contracts
- **Responsibility**: network identity (ARCH §3): slot allocation with deferred reuse, the side tables (incl. `spawnEmitted` and prefab per slot), revisions, prefab ranks, class signatures and digest, the type catalog builder (ranks and fingerprint), the session-global type-set registry with keyframes, the family rule table (ARCH §3.2), derived-key construction and comparison, and the host's `BuildOrder` allocator and re-basing.

**Public contracts**

```csharp
namespace SkylineTogether.Core.Identity
{
    public sealed class SlotTable : ISlotTable {
        public SlotTable(int initialEntityCapacity = 1 << 20);
        public void Bind(NetSlot slot, byte generation, EntityKey entity, Family family, PrefabRank prefab, TypeSetId typeSet, bool spawnEmitted);
        public void Unbind(NetSlot slot);
        public void SetTypeSet(NetSlot slot, TypeSetId typeSet); public void SetSpawnEmitted(NetSlot slot, bool value);
        public ushort BumpRevision(NetSlot slot); public void SetRevision(NetSlot slot, ushort revision);
        public void Clear(); public IEnumerable<NetSlot> EnumerateBound(); public int CopyUnspawned(List<NetSlot> output);
        // + ISlotTable members
    }
    public sealed class SlotAllocator {
        public SlotAllocator(int initialCapacity = 1 << 16);
        public NetSlot Allocate(out byte generation);                       // lowest free slot first
        public void Release(NetSlot slot, EpochId despawnEpoch);            // deferred
        public void OnMinAckedEpoch(EpochId minAckedOverPeers); public void OnLogTail(EpochId tail);   // frees eligible slots (ARCH §3.1)
        public void Restore(int highWaterMark, IEnumerable<KeyValuePair<NetSlot, byte>> bound);         // after NetIdSystem load
        public int HighWaterMark { get; } public int PendingReleaseCount { get; }
    }
    public struct PrefabIdentity { public string TypeName; public string Name; public string Hash; public int EntityIndex; public string SourceHint; public string Key { get; } }   // Key = "type|name|hash"
    public sealed class PrefabIndex : IPrefabIndex {
        public static PrefabIndex Build(IReadOnlyList<PrefabIdentity> prefabs, IReadOnlyList<ulong> classSignatures);   // ordinal sort by Key; duplicates detected and included in the digest
        public IReadOnlyList<PrefabIdentity> Sorted { get; } public IReadOnlyList<string> Duplicates { get; }
        public bool TryGetRank(string key, out PrefabRank rank);
        public static void Diff(IReadOnlyList<string> hostKeys, IReadOnlyList<string> clientKeys, out IReadOnlyList<string> missing, out IReadOnlyList<string> extra);
    }
    public struct TypeDescriptorInput { public ulong StableTypeHash; public string FullName; public string AssemblyName; public TypeKind Kind; public bool IsBuffer;
                                        public bool IsEnableable; public bool SerializeAsEnabled; public int SizeInChunk; public int ElementSize; public int EntityOffsetCount; }
    public sealed class TypeCatalogBuilder {
        public TypeCatalogBuilder(PolicyTableModel policies);
        public void Add(in TypeDescriptorInput input);
        public ITypeCatalog Build();                                        // ranks by StableTypeHash; fingerprint over (hash, sizes, kind, codec, policy version)
    }
    public sealed class TypeSetRegistry : ITypeSetRegistry { public TypeSetRegistry(ITypeCatalog catalog); }   // session-global; keyframe write/read (ARCH §3.6)

    // ---------- family rules (ARCH §3.2): the whole rule table, pure; the game layer only builds the marker mask ----------
    [Flags] public enum FamilyMarker : ulong {
        None = 0, Temp = 1UL << 0, LivePath = 1UL << 1, NullPrefab = 1UL << 2, AdapterLocal = 1UL << 3, PrefabData = 1UL << 4, Lane = 1UL << 5, ZoneBlock = 1UL << 6,
        Icon = 1UL << 7, Object = 1UL << 8, Secondary = 1UL << 9, Owner = 1UL << 10, ServiceUpgrade = 1UL << 11, Building = 1UL << 12, FlowGraph = 1UL << 13,
        Route = 1UL << 14, GateBypassRoutePrefab = 1UL << 15, NetNode = 1UL << 16, NetEdge = 1UL << 17, Aggregate = 1UL << 18, Vehicle = 1UL << 19, Creature = 1UL << 20,
        Area = 1UL << 21, Waypoint = 1UL << 22, Segment = 1UL << 23, Citizen = 1UL << 24, Household = 1UL << 25, HouseholdPet = 1UL << 26, CompanyData = 1UL << 27,
        TransientAgent = 1UL << 28, EventEntity = 1UL << 29, JournalOrChirpOrLifePath = 1UL << 30, CityStatistic = 1UL << 31, Singleton = 1UL << 32,
        WaterSource = 1UL << 33, OnlyModSerializable = 1UL << 34 }
    public struct FamilyFacts { public bool InOwnerSubObjectBuffer; public bool OwnerRouteTracked; }
    public static class FamilyRules {
        public static Family Classify(FamilyMarker markers, in FamilyFacts facts, out int ruleNumber);   // first match wins, rule numbers as in ARCH §3.2
        public static bool NeedsPerEntityFacts(FamilyMarker markers);                                   // only the owned-object and waypoint/segment rules
        public static ReplicationClass DefaultClass(Family family);
    }
    public static class DerivedKeys {
        public static DerivedKey Lane(NetSlot owner, PrefabRank lanePrefab, int masterSlaveFlags, ulong start, ulong middle, ulong end, bool nodeLane);
        public static DerivedKey SubObject(NetSlot owner, PrefabRank prefab, int alignedIndex, float localX, float localY, float localZ);
        public static DerivedKey Secondary(NetSlot owner, PrefabRank prefab, float x, float y, float z);
        public static DerivedKey Block(NetSlot edge, int side, float positionX, float positionZ, int sizeX, int sizeY);
        public static DerivedKey OwnedRoute(NetSlot owner, PrefabRank prefab);
        public static DerivedKey OwnedRouteElement(NetSlot owner, PrefabRank routePrefab, bool segment, int index);   // gate-bypass waypoints/segments (ARCH §3.7)
        public static DerivedKey FlowEdge(NetSlot startOwner, NetSlot endOwner, int role);
        public static ulong ReencodePathNode(ulong pathNodeKey, Func<int, NetSlot> ownerIndexToSlot);   // owner index in the high 32 bits → slot (R13 §2.5)
        public static bool GeometricMatch(in DerivedKey a, in DerivedKey b, int toleranceQ, bool allowReversed);
    }
    public struct BuildOrderRange { public uint Start; public uint End; }
    public sealed class BuildOrderAllocator {                               // host (ARCH §5.4.3)
        public void Observe(uint start, uint end);                         // from primed and captured BuildOrder values; monotonic
        public uint Next { get; }                                          // also the snapshot manifest's HostBuildOrderNext (ARCH §7.4)
        public void Rebase(IList<BuildOrderRange> ranges);                 // in place: preserves relative order and widths, advances Next
    }
}
```

- **Wave 1 note**: `SlotTable.Bind` never moves an entity-index mapping to an older entity version than the current occupant (Unity versions only increase per index); such a stale bind records only slot to entity, is counted in `StaleBindCount` (reset by `Clear`) and is unbound by the caller's `EntityManager.Exists` check. `TypeSetRegistry.ReadKeyframe` counts and logs every rejected keyframe, truncated ones included, and leaves the registry unchanged.
- **Game APIs**: none; inputs come from game-ecs-schema (`PrefabSystem.TryGetPrefab` + `GetPrefabID`, `TypeManager` type info; R04 §1.3, R08 §5).
- **Tests**: slot reuse only after min-ack and log tail; generation increments; `Restore` round trip; side-table version checks reject stale entities; `spawnEmitted` and prefab per slot; prefab sort, digest stability across insertion order, duplicate detection, diff, class signatures by rank; catalog fingerprint changes when any input or the policy version changes; type-set dedup and ordinals; type-set keyframe round trip, `Define` of an identical known id succeeds and of a contradicting one fails, unknown id → `UnknownTypeSetException`; `FamilyRules` rule by rule (one test per row of ARCH §3.2, including first-match precedence); derived-key quantisation and geometric matching (reversed node lanes); owned-route element keys are unique per (owner, prefab, kind, index); `PathNode` re-encoding; build-order re-basing preserves order and widths and never overlaps observed ranges.
- **Done when**: game-ecs-schema and game-join can bind a loaded city and produce matching digests on two machines.

### 7.6 core-replication

- **Milestone** M3 (S/G path, capture window, priming, apply planner, received-value cache and re-resolution; guard planning in M4; V/A/M paths, value shadow, dirty sets and `ScanScheduler` in M5a; icon command filter in M5b; inspection in M5c; echo suppression M4/M9) · **Assembly** `SkylineTogether.Core` · **Folder** `src/SkylineTogether.Core/Replication` · **Depends on** core-contracts, core-protocol, core-identity
- **Responsibility**: the engine of ARCH §4 without ECS: the capture window and the engine command queue (§4.5.5); host session start (binding and priming, §4.4.8); host observations → hash shadow (S/G at emission), value shadow, per-peer dirty sets, S/G coalescing, revisions of authored changes, `spawnEmitted` bookkeeping, reverse-buffer co-capture forcing, epochs and epoch parts, the shared P0 encoding (results and binds with origin, type-set definitions), per-peer message assembly through `IPeerScheduler` and the peer outboxes, the epoch log, joiner tracking (type-set keyframe first) and short-drop resume; the scan scheduler for SDC (§4.4.3); client epoch intake (parts, TSET), epoch merging into `ApplyPlan`s (superseded values dropped), the **apply planner** that turns plans into `ApplyProgram`s (upsert rules, origin filter, holds, parks, tags, synthesised events incl. route `PathUpdated`), pending references, held payloads, the **received-value cache** with the derived-reference index and re-resolution plans (ARCH §3.7), route-guard planning (§4.11.4), the client **icon command filter** rules (§4.10.4), inspection staging and acknowledgement; plus the FakeWorld end-to-end harness in the test project.

**Public contracts**

```csharp
namespace SkylineTogether.Core.Replication
{
    public sealed class ReplicationConfig {
        public double SValueCoalesceSeconds = 0.1; public double GValueCoalesceSeconds = 0.25; public long EpochLogBytes = 128L << 20; public double EpochLogSeconds = 600;
        public string EpochLogSpillDirectory; public double ShortDropGraceSeconds = 300; public int PendingEpochTtl = 600; public double ALowPendingSeconds = 120;
        public bool EchoSuppression; public double JoinerP3Share = 0.5; public int MaxRepairSlotsPerMessage = 512;
        public int MaxPartBytes = Protocol.Limits.MaxUncompressedBytes; public double SessionStartBudgetMs = 8;
    }
    public sealed class PolicyTable {
        public static PolicyTable FromModel(PolicyTableModel model, ITypeCatalog catalog);
        public ReplicationClass ClassOf(Family family, TypeRank type);
        public bool IsPlayerAuthorable(TypeRank type); public bool IsPresenceAuthorable(TypeRank type); public bool HasAuthorableMask(TypeRank type);
        public bool IsHashAsSet(TypeRank type); public bool IsExcluded(TypeRank type); public bool IsOnDemand(TypeRank type); public bool IsNoReapply(TypeRank type);
        public bool IsSource(Family family, TypeRank type);                                // S rather than V on structure families (family-scoped entries win)
        public bool IsCacheReceived(TypeRank type); public bool IsGuarded(TypeRank type); public bool HasDeferredFields(TypeRank type);
        public string StandInResolverOf(Family family, TypeRank type);                   // presence-safe references (ARCH §4.11.3); null if none
        public TouchFlags ApplyTagsOf(TypeRank type);
        public IReadOnlyList<TypeRank> ReverseBuffersOf(TypeRank forwardReferenceType);   // co-capture table (ARCH §4.2.6), incl. Passenger, RouteVehicle, ConnectedRoute, GuestVehicle
        public IReadOnlyList<TypeRank> PathStateTypes { get; }                           // PathTargets, PathInformation, PathElement on the Segment family (ARCH §4.4.2 step 3a)
    }
    [ThreadAffinity(ThreadKind.Main)] public sealed class EngineCommandQueue {           // single-threaded; executed at BeginCapture after WaitForFrameWork (ARCH §4.5.5)
        public void Enqueue(Action<HostReplicationEngine> command); public int Count { get; } public int Drain(HostReplicationEngine engine);
    }
    public sealed class HashShadow : IHashShadowView, IDisposable {
        public HashShadow(ITypeCatalog catalog, IBucketSink buckets, Func<NetSlot, ReplicationClass> classOf);
        public bool TryGet(NetSlot slot, TypeRank type, out uint raw, out uint canonical);
        public bool SetCanonical(NetSlot slot, TypeRank type, uint raw, uint canonical);   // true if canonical changed; updates bucket sums
        public void SetRaw(NetSlot slot, TypeRank type, uint raw); public void SetExistence(NetSlot slot, ulong member); public void RemoveSlot(NetSlot slot);
        public long MemoryBytes { get; }
    }
    public sealed class ValueShadow : IDisposable {
        public ValueShadow(int initialArenaBytes = 16 << 20);
        public void Set(NetSlot slot, TypeRank type, byte[] canonical, int offset, int count);
        public bool TryCopy(NetSlot slot, TypeRank type, ByteWriter output);             // worker threads, read window only (ARCH §4.5.5)
        public void RemoveSlot(NetSlot slot); public void Compact(double maxMilliseconds); public long MemoryBytes { get; }
    }
    public sealed class PeerDirtySet : IPeerDirtyView {
        public PeerDirtySet(ITypeSetRegistry typeSets, ISlotTable slots);
        public void Mark(NetSlot slot, TypeRank type, double now); public void MarkAll(NetSlot slot, double now);
        public void Clear(NetSlot slot, ulong bits); public void ClearSlot(NetSlot slot); public long MemoryBytes { get; }
    }
    public sealed class EpochLog : IDisposable {
        public EpochLog(long maxBytes, double maxSeconds, string spillDirectory);
        public void Append(EpochId epoch, double time, byte[] p0Section, int offset, int count);
        public bool TryCopy(EpochId epoch, ByteWriter output); public EpochId Head { get; } public EpochId Tail { get; } public long BytesSince(EpochId epoch);
    }
    public sealed class HostReplicationEngine : IHostReplicationEngine, IRecordEmitter, IDisposable {
        public HostReplicationEngine(ReplicationConfig config, ITypeCatalog catalog, ITypeSetRegistry typeSets, IPrefabIndex prefabs, PolicyTable policies,
                                     IHostWorldReader worldReader, IPeerScheduler scheduler, IAttentionView attention, IBucketSink buckets, IBeaconSource beacons,
                                     Identity.BuildOrderAllocator buildOrders, ICompressor compressor, IClock clock, ILog log);
        public SlotTable SlotTable { get; } public SlotAllocator Allocator { get; } public HashShadow Hashes { get; } public ValueShadow Values { get; } public EpochLog Log { get; }
        public EngineCommandQueue Commands { get; }
        public IPeerDirtyView GetDirtyView(PeerId peer);
        [ThreadAffinity(ThreadKind.Worker)] public void RunFrameWork(double deltaSeconds);   // P0 encode once (split into parts), per-peer scheduling into the outboxes, after EndCapture
        public void WaitForFrameWork();                                                      // called by BeginCapture (joins workers)
        public event Action<ReplicationClass, EpochId> ScanCycleCompleted;                   // fed by the game scanner through NotifyScanCycle
        public void NotifyScanCycle(ReplicationClass cls, EpochId startedAtEpoch);
        public float SessionStartProgress { get; }
        // + IHostReplicationEngine, IRecordEmitter
    }
    public sealed class ApplyPlanMerger {
        public ApplyPlanMerger(ITypeSetRegistry typeSets, PeerId localPeer);
        [ThreadAffinity(ThreadKind.Worker)] public void Add(in Protocol.EpochHeader header, Stream completeEpoch);   // only complete epochs (parts assembled upstream)
        public bool TryBuild(in ApplyBudget budget, ApplyPlan plan);                         // structure in order, values coalesced latest per (slot, type),
                                                                                             // values superseded by a later DESPAWN or type-removing STRUCT dropped
        public int PendingEpochs { get; } public long PendingBytes { get; }
        public event Action<FaultCategory, string> Faulted;                                  // unknown type-set id, contradicting definition
    }
    // ---------- apply planning (ARCH §4.7.2-4.7.3): pure; executed by the FakeWorld and by game-client-apply ----------
    public sealed class ApplyPlanner {
        public ApplyPlanner(ITypeSetRegistry typeSets, PolicyTable policies, PeerId localPeer);
        public void Plan(ApplyPlan plan, IApplyWorldView view, IHoldFilter holds, IPayloadFacts facts, double nowSeconds, ApplyProgram output);
        // rules: dead bindings → Unbind + re-plan as unbound; SPAWN on a bound slot reconciles only if generation, family and prefab match,
        // otherwise MarkDeleted + Unbind + CreateGroup; BIND only for Origin == localPeer and only onto a prediction of the same family/prefab;
        // RESULT of other origins skipped; held pairs → Hold; values whose references are unbound → Park; tags per PolicyTable;
        // Finalize per created group last; SynthesizeEvent(RentersUpdated) only after the property's Renter value op in the same program;
        // SynthesizeEvent(PathUpdated) once per segment after the last of its path-state value ops (ARCH §4.11.3);
        // for 5 s after a BIND, uncomputed host path state (empty PathElement, zero distance) of a bound prediction → Hold (ARCH §4.11.4)
    }
    // ---------- received-value cache, re-resolution, route guard (ARCH §3.7, §4.7.5, §4.11.4): pure, client side ----------
    public sealed class DerivedRefIndex {                                                    // derived owner slot → dependent (slot, type) pairs
        public void Set(NetSlot slot, TypeRank type, IReadOnlyList<NetSlot> derivedOwners); public void RemoveSlot(NetSlot slot);
        public int CollectDependents(NetSlot derivedOwner, List<(NetSlot slot, TypeRank type)> output);
    }
    public sealed class ReceivedValueCache : IDisposable {                                   // unmanaged arena, soft cap (ARCH App. D)
        public ReceivedValueCache(long softCapBytes = 32L << 20);
        public void Record(NetSlot slot, TypeRank type, byte[] canonical, int offset, int count, bool guardedType, IReadOnlyList<NetSlot> derivedOwners);
        public bool TryCopy(NetSlot slot, TypeRank type, ByteWriter output); public bool TryGetHash(NetSlot slot, TypeRank type, out uint hash);
        public void RemoveSlot(NetSlot slot); public DerivedRefIndex Index { get; }
        public void MarkRegenerated(NetSlot derivedOwner);                                   // queues the dependents
        public bool TryBuildReresolutionPlan(ApplyPlan plan);                                // ValueOps from cached bytes; dependents leave the queue
        public bool IsAwaitingReresolution(NetSlot slot, TypeRank type);                     // pending pair (ARCH §8.3)
        public long MemoryBytes { get; }
    }
    public static class GuardPlanner {                                                       // which guarded pairs the route guard restores (ILocalEditView: core-contracts)
        public static int Plan(IReadOnlyList<(NetSlot slot, TypeRank type, uint liveHash)> changed, ReceivedValueCache cache, ILocalEditView localEdit,
                               List<(NetSlot slot, TypeRank type)> restore);                 // restore = changed ∧ cached ∧ hash differs ∧ ¬local edit ∧ ¬pending proposal
    }
    // ---------- client icon command filter (ARCH §4.10.4): pure decision over the facts of one command ----------
    [Flags] public enum IconCommandFacts : byte { None = 0, Add = 1, Remove = 2, Update = 4, Temp = 8, All = 16, OwnerDeletedOrGone = 32, TargetDeletedOrGone = 64 }
    public enum IconLayerKind : byte { Default, Marker, Transaction, Other }
    public static class IconCommandFilter {
        public static bool Keep(IconCommandFacts facts, IconLayerKind layer, bool fromReplicationQueue);   // the table of ARCH §4.10.4
    }
    // ---------- scan scheduling for SDC (ARCH §4.4.3): pure bookkeeping over abstract chunk records ----------
    public struct ChunkInfo { public ulong SequenceNumber; public int OrderVersion; public int EntityCount; public int ArchetypeId; }
    public sealed class ScanSchedulerConfig { public double[] PeriodSeconds; public double BudgetMsFast = 1.0, BudgetMsSlow = 2.0; public double FastFpsThreshold = 50, SlowFpsThreshold = 30; }
    public sealed class ScanScheduler {
        public ScanScheduler(ScanSchedulerConfig config, IClock clock);
        public void UpdateChunkList(ReplicationClass cls, int queryOrderVersion, IReadOnlyList<ChunkInfo> chunks);   // when the query's order version changed
        public int NextChunks(ReplicationClass cls, double frameDtSeconds, List<ulong> output);                     // which chunks to visit this frame (cursor, cadence)
        public bool ColumnChanged(ulong chunkSequence, TypeRank type, uint changeVersion);                          // equality test against the record
        public void CommitColumn(ulong chunkSequence, TypeRank type, uint changeVersion);
        public void RecordBaseline(ulong chunkSequence, TypeRank type, uint changeVersion);                         // host session start (ARCH §4.4.8)
        public void ReportCost(double milliseconds, int chunksVisited);                                              // adapts periods to the budget
        public double CycleProgress(ReplicationClass cls); public double MeasuredStalenessSeconds(ReplicationClass cls);
        public event Action<ReplicationClass> CycleCompleted;
    }
    public sealed class PendingRefs {
        public void Park(NetSlot slot, TypeRank type, byte[] canonical, int offset, int count, NetSlot missing, EpochId now, bool lowPriorityTarget);
        public void OnBound(NetSlot slot, ApplyPlan into);                                   // re-queues resolvable records as ValueOps
        public IReadOnlyList<PendingReference> Expire(EpochId now, double nowSeconds);       // → repair requests (ARCH §4.7.5)
        public int Count { get; }
    }
    public struct PendingReference { public NetSlot Slot; public TypeRank Type; public NetSlot Missing; public bool LowPriorityTarget; }
    public sealed class ClientReplicationEngine : IClientReplicationEngine, IDisposable {
        public ClientReplicationEngine(ReplicationConfig config, ITypeCatalog catalog, ITypeSetRegistry typeSets, PolicyTable policies, IHashLedger ledger,
                                       ICompressor compressor, IClock clock, ILog log, string spillDirectory);
        public SlotTable SlotTable { get; } public ReceivedValueCache Received { get; }
        public event Action<EpochId> Acknowledge;                                            // the composition root sends Ack messages
        // + IClientReplicationEngine (parts through Protocol.EpochPartAssembler; TSET → registry; Plan → ApplyPlanner;
        //   RecordReceived / TryCopyReceived / OnDerivedOwnerRegenerated / TryBuildReresolutionPlan → ReceivedValueCache)
    }
    public static class ClassDigest { public static ulong Compute(IHashLedger ledger, ReplicationClass cls, int bucketCount); }   // M3 debug command
}
// tests/SkylineTogether.Core.Tests/Fakes (shipped only in the test project)
//   public sealed class FakeWorld : IWorldApplier, IHostWorldReader, IApplyWorldView { public FakeEntity Spawn(...); public void Mutate(...); public void Despawn(...);
//                                                                                     public void DestroyLocally(...); public ulong Fingerprint(); }   // executes ApplyPrograms
//   public sealed class E2EHarness { public E2EHarness(int clients, NetSimOptions sim, int worldEntities = 10_000); public void Step(double seconds); public bool Converged();
//                                    public long P0BytesSince(EpochId epoch); public int SpawnsForPreexistingSlots { get; } }
```

- **Wave 1 notes (from core-protocol)**: the host feeds `HashDetailMessage` only slots of the requested bucket, at most 1024; the host caps or splits transactions so one epoch stays within `Limits.MaxEpochTotalBytes` before `EpochPartSplitter.Split`; the client reads single-part epochs directly with `EpochReader` (`EpochPartAssembler` copies them) and abandons a section after any `DecodeException` from `RecordReader` (see §7.2).
- **Game APIs**: none (the game side implements `IHostWorldReader` and `IWorldApplier`).
- **Tests**: hash/value shadow semantics and memory accounting; S/G shadow updated at emission (a beacon after the TX always matches, also while values are pending); dirty-set marking for every peer, clearing on outbox acceptance, ages and bucket summaries; S coalescing at ≤ 10 Hz and G at ≤ 4 Hz; revisions: a simulation-only (SDC) change never bumps, an EDC change of the authorable projection does; epoch ordering (type sets → results/binds → despawns → spawns → structs → values → touches → keyed → channels); P0 encoded once for N peers; a transaction above the part cap is emitted as parts and applied atomically; per-peer messages never contain a value before the slot's spawn; despawn clears dirty bits; slot reuse only after acks; `spawnEmitted`: entities bound by a commit, the snapshot bind pass or discovery are spawned exactly once, primed entities never; co-capture puts reverse buffers into the same TX as the forward-reference change or despawn; epoch log trimming and spill; joiner dirty set + TSET keyframe + log replay (join after type sets were defined); short-drop resume re-marks sent pairs and raises `PeerResumed`; **capture window**: mutations outside it are queued and run at the next `BeginCapture`; concurrency stress (workers running while randomised main-thread calls arrive) shows no lost dirty bit and no torn read; `ApplyPlanMerger` coalescing preserves structural order and drops superseded values; unknown type-set id → `Faulted`; **ApplyPlanner**: upsert with matching identity reconciles, with mismatching generation/family/prefab replaces; dead binding re-planned; RESULT/BIND of other origins skipped (two clients with identical seq and LocalId); holds and parks; `RentersUpdated` ordering; one `PathUpdated` per segment after its last path-state op; uncomputed host path state of a just-bound prediction is held and the first computed one applied; idempotency (duplicate epochs, spawn upsert, unknown despawn); pending references resolve and expire; **received-value cache**: records with derived owners land in the index, `MarkRegenerated` yields exactly the dependents once, re-resolution plans contain the cached bytes, pairs awaiting re-resolution report pending, the soft cap evicts non-guarded pairs first; **GuardPlanner**: restores only changed, cached, differing pairs outside the local edit and outside pending proposals; **IconCommandFilter**: every row of ARCH §4.10.4 (temp, marker, transaction, update, removes for deleted owners or targets kept; other adds and removes dropped; the replication queue always kept); path-result observations go into the current epoch's P0 TX uncoalesced; `ScanScheduler` (synthetic version streams: equality bookkeeping, wrap-around, cadence and budget adaptation, baseline from session start); **E2E with FakeWorld over NetSim** (loss 5 %, reorder, duplication, 64 KB/s cap): bucket sums converge within bounded time after mutations stop; bytes per second within budget; no unbounded queue; **session start**: a 1 M-entity world is primed without records and a later join emits no `SPAWN` for pre-existing slots; a committed entity of client A appears on client B.
- **Done when**: the E2E harness converges for structure (M3) and for all classes (M5a).

### 7.7 core-scheduling

- **Milestone** M5a (budgets, P0 + rolling P3, staleness promotion, speed cap), M5c (attention model, P1 ordering), M6 (degradation ladder, motion budgets) · **Assembly** `SkylineTogether.Core` · **Folder** `src/SkylineTogether.Core/Scheduling` · **Depends on** core-contracts, core-protocol
- **Responsibility**: budgets and attention (ARCH §4.8): token buckets fed by link statistics with AIMD, the host total upload cap with redistribution, the per-peer P0-P3 scheduler with staleness promotion (value sections only while the peer's outbox window is open; outbox bytes count as P0 backlog), the attention model (view reports, in-view sets, closures, infoview topics, motion interest), the degradation ladder and the automatic speed cap.

**Public contracts**

```csharp
namespace SkylineTogether.Core.Scheduling
{
    public sealed class SchedulerConfig {
        public double P1Share = 0.40, P2Share = 0.45, P3Share = 0.15, MinP1ShareAfterP0 = 0.30; public int FloorBytesPerSecond = 48_000;
        public double QueueHighMs = 150, QueueLowMs = 30, DecreaseFactor = 0.8, IncreasePerSecond = 0.05, SendRateFraction = 0.85;
        public double[] PromotionQuantaSeconds = { 2, 4, 8, 16, 32 }; public double P0OverloadSeconds = 30, P0BacklogSeconds = 2;
        public float MinMotionRadius = 300, MaxMotionRadius = 2500, MotionRadiusPerZoom = 1.0f, LeaveFactor = 1.25f;
    }
    public sealed class TokenBucket { public TokenBucket(int bytesPerSecond, int burstBytes); public int RateBytesPerSecond { get; set; } public int Take(double deltaSeconds); public void Consume(int bytes); public int Available { get; } }
    public sealed class LinkEstimator { public LinkEstimator(SchedulerConfig config); public int Update(in ConnectionStats stats, int userCapBytesPerSecond, bool backlogged, double deltaSeconds); public int CurrentBudget { get; } }
    public sealed class HostUploadBudget { public HostUploadBudget(int totalBytesPerSecond); public int TotalBytesPerSecond { get; set; } public void SetDemand(PeerId peer, int wantedBytesPerSecond); public void RemovePeer(PeerId peer); public int ShareOf(PeerId peer); public void Recompute(); }
    public sealed class TopicMap { public static TopicMap CreateDefault(ITypeCatalog catalog); public void Map(string typeFullName, AttentionTopic topics); public AttentionTopic TopicsOf(TypeRank type); public static AttentionTopic FromInfoview(byte infoviewId, uint infomodeMask);
                                   public static AttentionTopic FromPanels(ushort panelFlags); }   // transportation overview → Transport (ARCH §4.8.3)
    public sealed class AttentionSets : IAttentionView {
        public AttentionSets(TopicMap topics, SchedulerConfig config);
        public void OnViewReport(PeerId peer, Protocol.ViewReport report, double now);
        public void SetInViewSlots(PeerId peer, IReadOnlyList<NetSlot> slots);          // game quad-tree query, 1 Hz (ARCH §4.8.3)
        public void SetClosure(PeerId peer, IReadOnlyList<NetSlot> closure);
        public MotionInterestRegion GetMotionInterest(PeerId peer); public Protocol.ViewReport LastView(PeerId peer);
        public IReadOnlyCollection<PeerId> Peers { get; } public void RemovePeer(PeerId peer);
        // + IAttentionView
    }
    public struct PeerSchedulerStats { public int BudgetBytesPerSecond; public int P0BytesPerSecond, P1BytesPerSecond, P2BytesPerSecond, P3BytesPerSecond; public double StalenessP50Seconds, StalenessP95Seconds; public MotionQuality Motion; public float MotionRadiusScale; }
    public sealed class PeerScheduler : IPeerScheduler {
        public PeerScheduler(SchedulerConfig config, HostUploadBudget hostBudget, IClock clock);
        public PeerSchedulerStats GetStats(PeerId peer); public void RemovePeer(PeerId peer);
    }
    public sealed class DegradationController { public DegradationController(SchedulerConfig config); public MotionQuality Evaluate(PeerId peer, int p2Budget, int p2Demand, out float radiusScale); }
    public sealed class AutoSpeedCap { public AutoSpeedCap(double overloadSeconds = 30, double recoverySeconds = 60); public int Evaluate(int requestedSpeed, IReadOnlyList<bool> peerOverloaded, double now, out string overloadedPeerNote); }
}
```

- **Game APIs**: none (link statistics arrive as `ConnectionStats`; in-view slots from game-host-capture).
- **Tests**: token bucket accuracy; AIMD reacts to queue time; host total split and redistribution (unused shares flow to others); P0 never throttled but accounted; no P1/P3 section is scheduled while `ReliableWindowOpen` is false, and their dirty bits stay set; outbox bytes trigger `IsP0Overloaded`; shares respected over time (deficit round robin); staleness promotion bounds delivery time under a saturated P1; attention: motion radius from zoom, hysteresis, topic mapping from infoview ids and from panel flags (the transportation overview raises Transport for every line); degradation order exactly as ARCH §4.8.5; auto speed cap steps down and recovers.
- **Done when**: the E2E harness with three peers at different caps shows per-peer budgets respected and bounded staleness.

### 7.8 core-motion

- **Milestone** M6 (the `ClockModel` is needed in M2) · **Assembly** `SkylineTogether.Core` · **Folder** `src/SkylineTogether.Core/Motion` · **Depends on** core-contracts, core-protocol
- **Responsibility**: motion maths and codecs of ARCH §4.9 and the clock model of §6.1: Q-A baselines and Q-B keyframe/delta records that decode on their own (reference keyframe tick, assignment generation), interest slots with generations and acknowledged reuse, datagram building per peer and tick, client staging rings, the F1 slot planner, extrapolation and pinning, the union-of-interest grid, per-peer reset on resume, and host-clock estimation with the clock lag `D`.

**Public contracts**

```csharp
namespace SkylineTogether.Core.Motion
{
    public struct MotionFrame { public float X, Y, Z, VX, VY, VZ, QX, QY, QZ, QW; public uint Flags; public byte State, Activity; public ushort StateTimer; public bool Synthetic; }
    public interface IMotionReferences { bool TryGetReference(ushort interestSlot, byte assignGeneration, uint referenceTick, out MotionFrame frame); }   // non-synthetic only
    public struct QbHeader { public ushort InterestSlot; public bool Keyframe; public byte RefAge; public byte AssignGeneration; }   // refAge in 16-tick steps (1..7), generation 4 bits
    public static class MotionCodec {
        public const float PositionQuantum = 1f / 64f; public const int KeyframeEvery = 4;           // every 4th sample of a mover (64 ticks)
        public static void WriteQBKeyframe(ByteWriter w, ushort interestSlot, byte assignGeneration, in MotionFrame frame, bool exactVelocity);
        public static void WriteQBDelta(ByteWriter w, ushort interestSlot, byte assignGeneration, byte refAge, in MotionFrame frame, in MotionFrame reference, bool exactVelocity);
        public static bool TryReadQB(ByteReader r, uint tick, IMotionReferences references, out QbHeader header, out MotionFrame frame);   // false: reference missing → drop
        public static void WriteQA(ByteWriter w, ushort interestSlot, byte assignGeneration, NetSlot slot, byte updateFrame, MotionFrame[] fourSlots, uint[] fourTicks);
        public static void ReadQA(ByteReader r, out ushort interestSlot, out byte assignGeneration, out NetSlot slot, out byte updateFrame, MotionFrame[] fourSlots, uint[] fourTicks);
    }
    public sealed class InterestSlotTable {                                               // host, per peer (ARCH §4.9.3)
        public InterestSlotTable(int capacity = 65535);
        public bool TryAssign(NetSlot slot, uint motionControlSeq, out ushort interestSlot, out byte generation);   // never reuses an unacknowledged release
        public void Release(NetSlot slot, uint motionControlSeq);                         // pending until the client acknowledges motionControlSeq
        public void OnClientAck(uint lastAppliedMotionControlSeq);                        // frees acknowledged releases
        public void ReleaseAll(uint motionControlSeq);                                    // resume (ARCH §7.8)
        public bool TryGetSlot(ushort interestSlot, out NetSlot slot, out byte generation); public bool TryGetInterestSlot(NetSlot slot, out ushort interestSlot, out byte generation);
        public int Count { get; } public int PendingReleases { get; }
    }
    public sealed class MotionHostEncoder : IMotionBudgetSink {
        public MotionHostEncoder(ILog log);
        public void AddPeer(PeerId peer); public void RemovePeer(PeerId peer);
        public void ResetPeer(PeerId peer);                                                // resume: ReleaseAll, then re-enter the current interest with fresh baselines
        public void EnterInterest(PeerId peer, NetSlot slot, byte updateFrame, MotionFrame[] fourSlots, uint[] fourTicks);
        public void ExitInterest(PeerId peer, NetSlot slot);                               // sends release + pin
        public void AddFrame(PeerId peer, uint tick, NetSlot slot, in MotionFrame frame, bool nearField, bool exactVelocity);   // chooses keyframe or delta
        public void OnAck(PeerId peer, uint lastAppliedMotionControlSeq);
        [ThreadAffinity(ThreadKind.Worker)] public void Flush(PeerId peer, IPeerOutput output);   // ≤ 1200-byte datagrams per tick; MotionControl on the reliable lane
        public bool IsInInterest(PeerId peer, NetSlot slot);
        // + IMotionBudgetSink
    }
    public sealed class StagingRing { public const int Capacity = 8; public byte UpdateFrame { get; set; } public byte AssignGeneration { get; set; }
                                      public void Insert(uint tick, in MotionFrame frame);   // frame.Synthetic marks extrapolated entries (never references)
                                      public bool TryGet(uint tick, out MotionFrame frame); public bool TryGetLatest(out uint tick, out MotionFrame frame); public void Clear(); }
    public sealed class MotionClientStager : IMotionReferences {
        [ThreadAffinity(ThreadKind.Network)] public void OnMotionDatagram(byte[] buffer, int offset, int count);   // drops records with a wrong generation or a missing reference
        [ThreadAffinity(ThreadKind.Main)] public void OnMotionControl(byte[] buffer, int offset, int count);       // assign (generation, valid-from tick), release, release-all
        [ThreadAffinity(ThreadKind.Main)] public void Drain();
        public uint LastAppliedMotionControlSeq { get; }                                  // reported in Ack
        public bool TryGetRing(NetSlot slot, out StagingRing ring); public IReadOnlyCollection<NetSlot> InInterest { get; }
        public bool TryTakePin(out NetSlot slot);                                         // movers that just left interest
        public void ResetAll();                                                           // resume
        public long DroppedNoReference { get; } public long DroppedWrongGeneration { get; }
    }
    public static class SlotPlanner {
        public static uint FirstFrameTick(uint renderTick, byte updateFrame);              // F1 = u + 16·((R − u − 32) >> 4) (R15 §2.2)
        public static int SlotOf(uint tick);                                                // (tick >> 4) & 3
        public static void Plan(uint renderTick, byte updateFrame, out uint f1, out uint f2, out uint f3);   // f2 = f1 + 16, f3 = f1 + 32
    }
    public static class Extrapolator { public const int MaxTicks = 32; public static MotionFrame Next(in MotionFrame last); public static MotionFrame Pin(in MotionFrame last); }
    public sealed class ClockModel {
        public ClockModel(IClock clock);
        public void OnSample(Protocol.ClockSample sample, double receivedLocalSeconds, double rttSeconds);
        public void OnEpochTick(uint hostTick, double receivedLocalSeconds);
        public double EstimatedHostFrame(double nowLocalSeconds);
        public int UpdateLag(float renderLeadTicks, double sessionSpeed, double deltaSeconds);   // D = clamp(⌈L + max(0, lead) − 16 + 4⌉, 0, 160), slew ±1 tick/s
        public int CurrentLag { get; } public double OneWaySeconds { get; } public double JitterP95Seconds { get; } public bool HostBusy { get; set; }
    }
    public sealed class InterestGrid { public InterestGrid(float cellSize = 128f, float worldSize = 14336f); public void Clear(); public void Add(in MotionInterestRegion region); public bool Contains(float x, float z); }
}
```

- **Game APIs**: none; the formulas come from R15 §2.2-2.6 and `RenderingSystem.PrepareRendering` (`RenderingSystem.cs:174-221` [V]).
- **Tests**: Q-B quantisation error ≤ 1/128 m and correct escape to absolute; keyframe cadence; a delta decodes only with its reference keyframe; **loss and reordering**: a recorded mover stream through `NetSimTransport` with 5 % loss and reordering keeps the decoded-position error bounded (no drift, every decoded frame within 1/128 m of the host's); records of a released or reassigned interest slot are dropped by generation; an interest slot is reused only after the acknowledgement; `ResetPeer`/`ResetAll` re-enter every mover with fresh baselines; Q-A round trip; datagram size ≤ 1200 bytes with the implicit-tick rule (only `UpdateFrame == T & 15`); staging ring ordering under reordering and duplicates; `SlotPlanner` against hand-computed tables from R15 §2.2-2.3 for all 16 phases; extrapolation and pin; clock model under synthetic jitter traces (convergence, slew limit, lag formula at speeds 1/2/4 and render lead −15..+15); interest grid containment.
- **Done when**: a synthetic replay of host frames through NetSim at speed 4 with 120 ms latency yields no slot written outside the safe window and no backward step.

### 7.9 core-intents

- **Milestone** M4 · **Assembly** `SkylineTogether.Core` · **Folder** `src/SkylineTogether.Core/Intents` · **Depends on** core-contracts, core-protocol
- **Responsibility**: the client action path model (ARCH §5): proposal format and codec (parts for large change sets), local ids, the client proposal tracker (pending proposals, held payloads, timeouts measured from the receipt echo and suspended during `HostBusy`, tombstones, rollback plans), the host's pure validation rules (incl. prefab-class and geometry consistency), permissions per intent, and rate limiting.

**Public contracts**

```csharp
namespace SkylineTogether.Core.Intents
{
    public enum ProposalKind : byte { ToolChangeSet, LocalEdit, TerrainStroke, Semantic }
    public enum IntentKind : ushort { SetPolicy = 1, SetTaxRates, SetServiceBudget, SetServiceFee, ChangeLoan, PurchaseDevNode, PurchaseMapTiles, Delete, Rename,
                                      SetCityName, SetRouteColor, SetMeshColor, SetHistorical, SetVehicleModels, SetServiceDistricts, EditPalette, Follow, Unfollow,
                                      ReplaceObjectPrefab }                               // rebuild / upgrade in place (ARCH §5.3, §5.4.3)
    public struct ComponentPayload { public TypeRank Type; public byte[] Bytes; public bool HasEnabledBit; public bool Enabled; }
    public sealed class ToolHeader { public ushort ToolKind; public string ToolTypeName; public PrefabRank Prefab; public byte Mode; public long CostSum; }
    public sealed class ProposalCreate { public LocalId Local; public Family Family; public PrefabRank Prefab; public TypeSetId TypeSet; public List<ComponentPayload> Components; public ulong EnabledBits; }
    public sealed class ProposalUpdate { public NetSlot Slot; public ushort BaseRevision; public List<ComponentPayload> Components; }
    public sealed class ProposalDelete { public NetSlot Slot; public ushort BaseRevision; }
    public sealed class ZoneEdit { public DerivedKey Block; public ushort BaseRevision; public ushort[] ZoneRanks; }
    public sealed class TerrainStamp { public byte TerraformingType; public float PosX, PosY, PosZ, TargetX, TargetY, TargetZ, StartX, StartY, StartZ, Angle, Size, Strength, Opacity;
                                       public PrefabRank ToolPrefab;                      // Brush.m_Tool (tool strength lookup)
                                       public PrefabRank BrushPrefab;                     // brush texture
                                       public float AreaMinX, AreaMinZ, AreaMaxX, AreaMaxZ;
                                       public float ClampedDeltaTime; }                   // originator's clamp(unscaledDeltaTime, 0.05, 1) (ARCH §4.10.9)
    public sealed class SemanticIntent { public IntentKind Kind; public NetSlot Target; public ushort BaseRevision; public PrefabRank Prefab; public int IntValue; public float FloatValue; public bool BoolValue;
                                         public string Text; public int[] IntArray; public byte[] Blob; }
    public sealed class Proposal {
        public uint Seq; public ProposalKind Kind; public EpochId BaseEpoch; public ToolHeader Tool;
        public List<ProposalCreate> Creates; public List<ProposalUpdate> Updates; public List<ProposalDelete> Deletes; public List<TouchRecord> Touches;
        public List<ZoneEdit> Zones; public List<TerrainStamp> Stamps; public List<SemanticIntent> Intents;
        public int EntityCount { get; }
        public void Write(ByteWriter w); public static Proposal Read(ByteReader r);       // one part; enforces Limits.MaxProposalBytes
        public void WriteParts(ByteWriter scratch, Action<byte[], int, int> emitPart);    // parts ≤ Limits.MaxProposalBytes (ARCH §5.4.1 rule 1)
    }
    public sealed class ProposalAssembler {                                                // host, per peer; enforces MaxProposalTotalBytes and MaxProposalEntities
        public bool Accept(byte[] buffer, int offset, int count, out Proposal complete); public void Reset();
    }
    public sealed class RollbackPlan { public uint Seq; public List<LocalId> PredictedCreates; public List<NetSlot> TouchedOriginals; public List<SemanticIntent> ManagedRestores; public List<PrefabRank> PredictedUnlocks; }
    public enum ResultOutcome : byte { Accepted, Rejected, LateAcceptedBindsIgnored, LateRejectedIgnored, Unknown }
    public sealed class ProposalTracker : IHoldFilter {
        public ProposalTracker(IClock clock, double timeoutSeconds = 10, double connectionTimeoutSeconds = 30, double tombstoneSeconds = 300);
        public uint NextSeq(); public LocalId NewLocalId();
        public void Track(Proposal proposal, RollbackPlan rollback, IEnumerable<(NetSlot slot, TypeRank type)> coveredPairs);
        public void OnReceived(uint seq);                                                  // host echo: the 10 s timer starts now (ARCH §5.5.2)
        public void SetHostBusy(bool busy);                                                // timers suspended while true
        public ResultOutcome OnResult(in ResultRecord result, IReadOnlyList<BindRecord> binds, out RollbackPlan rollbackToRun, out bool applyBinds);   // tombstoned seq → binds ignored
        public IReadOnlyList<RollbackPlan> ExpireTimedOut();                               // leaves tombstones
        public bool TryResolveLocal(LocalId local, out NetSlot slot); public int PendingCount { get; } public int TombstoneCount { get; }
        // + IHoldFilter
    }
    public sealed class ValidationFacts {                                                  // supplied by the game layer per proposal
        public Func<NetSlot, bool> IsAlive; public Func<NetSlot, ushort> RevisionOf; public Func<DerivedKey, ushort> BlockRevisionOf; public Func<PrefabRank, bool> IsUnlocked;
        public bool UnlockAll; public long Money; public bool UnlimitedMoney; public Func<float, float, bool> IsInsideOwnedTiles; public bool AllowOutsideTiles;
        public Func<TypeRank, bool> IsPlayerAuthorable; public Func<TypeRank, bool> IsPresenceAuthorable;
        public Func<Family, ProposalKind, bool> FamilyAllowed; public Func<ProposalCreate, long> EstimateCost; public float CostTolerance = 0.25f;
        public Func<NetSlot, Family> FamilyOf; public Func<NetSlot, PrefabRank> PrefabOf; public Func<PrefabRank, ulong> PrefabClassOf;   // ARCH §5.4.1 rule 5
        public Func<PrefabRank, PrefabRank, bool> AdapterAllowsSwap; public TypeRank PrefabRefType;
        public Func<Proposal, (bool ok, NetSlot offending)> CheckGeometry;                 // game layer: curve end points vs node positions (rule 6)
        public Func<TypeRank, byte[], (bool finite, bool inBounds)> CheckPayload;
    }
    public struct ValidationResult { public bool Accepted; public RejectReason Reason; public NetSlot ConflictSlot; public string Detail; }
    public static class ProposalValidator { public static ValidationResult Validate(Proposal proposal, Permission permissions, bool senderLive, ValidationFacts facts); }
    public static class IntentPermissions { public static Permission Required(ProposalKind kind, IntentKind intent, bool hasDeletes, bool hasTerrain, bool hasZones); }
    public sealed class RateLimiter { public RateLimiter(double perSecond, double burst); public bool TryConsume(double nowSeconds, double cost = 1); }
}
```

- **Game APIs**: none.
- **Tests**: proposal codec round trips and size limits; parts: a 150k-entity change set is split and reassembled, limits enforced on the total; tracker: hold/release semantics, newest value after result, the timer starts at `OnReceived` and pauses during `SetHostBusy(true)`, timeouts produce rollback plans and tombstones, a late acceptance for a tombstoned seq returns `LateAcceptedBindsIgnored` with `applyBinds = false`, local-id binding; validator: every rule of ARCH §5.4.1 in isolation (permission, liveness, stale revision, dead reference, non-authorable type, presence-only authorable types, `PrefabRef` swap across classes or families rejected and within a class accepted, adapter-allowed swaps, geometry callback, locked prefab, money, tiles, non-finite floats, entity limit), whole-proposal atomic rejection; permissions mapping per intent (incl. `ReplaceObjectPrefab`); rate limiter.
- **Done when**: the E2E harness runs proposals with injected conflicts and the tracker rolls back exactly the predicted entities.

### 7.10 core-integrity

- **Milestone** M7 (bucket sums, ledger and the line-state report used from M3 for the class digest and `/st lines compare`) · **Assembly** `SkylineTogether.Core` · **Folder** `src/SkylineTogether.Core/Integrity` · **Depends on** core-contracts, core-protocol
- **Responsibility**: convergence proof and repair (ARCH §8): member hashing with normalisation (including multisets), incremental bucket sums, the client hash ledger (received values), the beacon planner, the client repair coordinator (two-beacon confirmation, ladder on confirmed differences only, tolerance list, pending pairs, coalesced and rate-limited requests), the host repair responder (host-decided escalation, per-peer caps, throttling instead of kicks), the leak tracker, and the UI-level line-state report and its comparison (ARCH §4.11.6).

**Public contracts**

```csharp
namespace SkylineTogether.Core.Integrity
{
    public static class MemberHasher {
        public static uint HashCanonical(TypeRank type, byte[] canonical, int offset, int count);
        public static uint HashEntityRefMultiset(TypeRank type, byte[] canonical, int offset, int count);   // EntityRefList codec layout: varint n, n NetRefs
        public static ulong Member(NetSlot slot, TypeRank type, uint hash);
        public static ulong ExistenceMember(NetSlot slot, byte generation, TypeSetId typeSet, Family family);
    }
    public sealed class BucketSums : IBucketSink { public ulong Sum(ReplicationClass cls, int bucket); public int Count(ReplicationClass cls, int bucket); public int BucketCount(ReplicationClass cls); public void Clear(); }
    public sealed class HashLedger : IHashLedger { public HashLedger(Func<NetSlot, ReplicationClass> classOf); public long MemoryBytes { get; } public void Clear(); }
    public sealed class BeaconPlanner : IBeaconSource { public BeaconPlanner(BucketSums sums, IClock clock, int defaultPerSecond = 32, int highRatePerSecond = 1024); public bool IsEnabled(PeerId peer, ReplicationClass cls); public void RemovePeer(PeerId peer); }
    public enum RepairAction : byte { None, AwaitConfirmation, RequestDetail, RequestEntities, RequestRebaseline, RequestSnapshot, Tolerate }
    public struct RepairStats { public int BucketMismatches, ConfirmedMismatches, EntityRepairs, Rebaselines, Tolerated, Leaks, Resyncs, RequestsCoalesced, RequestsThrottled; }
    public interface IPendingPairs { bool IsPending(NetSlot slot, TypeRank type); }       // parked, held, awaiting re-application, covered by a proposal (ARCH §8.3)
    public sealed class RepairCoordinator {                                                // client
        public RepairCoordinator(IHashLedger ledger, IPendingPairs pending, IClock clock, ILog log, double confirmSeconds = 0.25, int maxRequestsPerSecond = 5, int maxSlotsPerRequest = 256);
        public RepairAction OnBeacon(in HashBeacon beacon);                                // level 1 only after two mismatches ≥ confirmSeconds apart
        public RepairAction OnDetail(Protocol.HashDetailMessage detail, out NetSlot[] extras);   // only confirmed per-slot differences count toward §8.5 thresholds
        public void OnLeak(NetSlot slot, TypeRank type, double now);                       // ignored for pending and tolerated pairs
        public void OnRepairApplied(NetSlot slot, TypeRank type, bool stillMismatching, double now);
        public bool IsTolerated(NetSlot slot, TypeRank type, double now);
        public int TakeRequests(double now, List<Protocol.RepairRequestMessage> output);   // coalesced per 100 ms and class; rate-limited
        public bool WantsRebaseline(ReplicationClass cls, out string reason); public bool WantsResync(out string reason);   // hints; the host decides
        public RepairStats GetStats();
    }
    public enum RepairDecision : byte { Served, Throttled, Rebaseline, OfferSnapshot, DisconnectRepairLoop }
    public sealed class HostRepairPolicy { public int MaxRequestsPerSecond = 10; public int MaxSlotsPerRequest = 512; public int MaxConcurrentRebaselines = 1;
                                           public int MaxRebaselinesPer10Min = 3; public int MaxSnapshotResyncsPer30Min = 3; }
    public sealed class HostRepairResponder {                                              // host; keeps the ladder state per peer
        public HostRepairResponder(IHostReplicationEngine engine, IHashShadowView shadow, HostRepairPolicy policy, IClock clock, ILog log);
        public Protocol.HashDetailMessage BuildDetail(ReplicationClass cls, int bucket, EpochId atEpoch);
        public RepairDecision OnRepairRequest(PeerId peer, Protocol.RepairRequestMessage request);   // → MarkDirty / ResendStructural / ReportNotPresent / StartRebaseline; never kicks for rate
        public void OnSnapshotResync(PeerId peer); public void RemovePeer(PeerId peer);
    }
    public sealed class LeakTracker { public void Count(TypeRank type, Family family); public IReadOnlyList<KeyValuePair<TypeRank, int>> Top(int n); public void Reset(); }
    // ---------- UI-level line digest (ARCH §4.11.6): rows are computed in game by LineStateProbe, compared here ----------
    public sealed class LineStateRow {
        public NetSlot Line; public uint SampleTick; public int Stops; public int Vehicles; public int Cargo; public int Capacity; public int LengthDecimetres;
        public byte Schedule; public bool Active; public uint RouteFlags; public ushort TransportLineFlags;
        public ulong BrokenSegmentMask;                                   // bit i = segment i drawn broken (first 64; overflow hashed into IconHash)
        public ulong LineIconHash; public ulong WaypointIconHash;          // sorted non-Marker icon prefab ranks of the line and of its waypoints
        public bool IconsCompared;                                        // false before the icon channel exists (M3)
        public void Write(Buffers.ByteWriter w); public static LineStateRow Read(Buffers.ByteReader r); public ulong Hash();
    }
    public sealed class LineStateDifference { public NetSlot Line; public string Field; public string HostValue; public string ClientValue; }
    public static class LineStateReport {
        public static ulong ListHash(IReadOnlyList<LineStateRow> rows);
        public static IReadOnlyList<LineStateDifference> Compare(IReadOnlyList<LineStateRow> host, IReadOnlyList<LineStateRow> client);   // also missing and extra lines
    }
}
```

- **Game APIs**: none.
- **Tests**: bucket sums are order-independent and O(1) on change (property test with random add/replace/remove sequences); multiset hashing ignores element order; ledger and host sums agree after random replication through the E2E harness, also with parked and held values (ledger records received values); beacons skipped for dirty buckets; a single mismatching beacon only yields `AwaitConfirmation`; repair ladder transitions exactly per ARCH §8.5 thresholds, counting only confirmed differences; tolerance masks after two failed repairs and escalates above 1 %; leak repair only after two frames and never for pending pairs; requests coalesced and limited to 5/s × 256 slots; responder: right engine calls, throttling above 10/s without disconnect, at most one concurrent and three rebaselines per 10 minutes, excess → `OfferSnapshot`, > 3 snapshot resyncs in 30 minutes → `DisconnectRepairLoop`; line-state report: row round trips, list hash independent of row order, `Compare` names the line and field for every single-field change, missing and extra lines, icon fields ignored when `IconsCompared` is false on either side.
- **Done when**: the E2E harness repairs injected client divergence (value flips, missing entities, extra entities) without snapshot reloads.

### 7.11 core-fields

- **Milestone** M8 (warm-join delta in M9) · **Assembly** `SkylineTogether.Core` · **Folder** `src/SkylineTogether.Core/Fields` · **Depends on** core-contracts
- **Responsibility**: codecs and bookkeeping for grid data (ARCH §4.10.8-4.10.10, §7.10): tile grids, heightmap tile codec and hashes, water tile quantisation, cell-map version rings with XOR deltas, stroke settle detection, the brush-strength frame-time compensation, and the block delta for warm joins.

**Public contracts**

```csharp
namespace SkylineTogether.Core.Fields
{
    public readonly struct TileId : IEquatable<TileId> { public readonly ushort X, Y; public TileId(ushort x, ushort y); }
    public sealed class TileGrid { public TileGrid(int width, int height, int tileSize); public int TilesX { get; } public int TilesY { get; }
                                   public IEnumerable<TileId> TilesIn(int minX, int minY, int maxX, int maxY); public void GetRect(TileId tile, out int x, out int y, out int w, out int h); }
    public static class HeightTileCodec { public static void Encode(ushort[] texels, int count, ByteWriter output, ICompressor compressor); public static void Decode(ByteReader input, ushort[] texels, int count, ICompressor compressor); public static ulong Hash(ushort[] texels, int count); }
    public enum WaterTileLevel : byte { DepthPollution, DepthPollutionVelocity }
    public static class WaterTileCodec { public static void Encode(float[] rgba, int texelCount, WaterTileLevel level, ByteWriter output, ICompressor compressor);
                                         public static WaterTileLevel Decode(ByteReader input, float[] rgba, int texelCount, ICompressor compressor); }
    public static class XorRle { public static void EncodeDelta(byte[] previous, byte[] current, int count, ByteWriter output); public static void ApplyDelta(ByteReader input, byte[] target, int count); }
    public sealed class CellMapVersionRing {
        public CellMapVersionRing(int mapCount, int maxVersions = 4);
        public int Publish(int mapIndex, byte[] data, int count);                           // returns the new version
        public bool WriteDelta(int mapIndex, int fromVersion, ByteWriter output, out bool keyframe);
        public int CurrentVersion(int mapIndex);
    }
    public sealed class StrokeSettleDetector { public StrokeSettleDetector(double settleSeconds = 0.2); public void OnStamp(int minX, int minY, int maxX, int maxY, double now);
                                               public bool TryTakeSettled(double now, out int minX, out int minY, out int maxX, out int maxY); }
    public static class BrushStrengthCompensation {                                        // ARCH §4.10.9; mirrors TerrainSystem.ApplyToTerrain's mapping
        public const float MinDelta = 0.05f, MaxDelta = 1f, LinearThreshold = 0.002f, LowExponent = 0.055f;
        public static float Map(float strength);                                           // f(s)
        public static float Unmap(float mapped);                                           // f⁻¹
        public static float Compensate(float strength, float originDeltaTime, float localDeltaTime);   // s′ with clamp(dt_local)·f(s′) = clamp(dt₀)·f(s)
    }
    public static class BlockDelta {                                                       // M9 warm join
        public static IReadOnlyList<Digest128> HashBlocks(Stream stream, int blockSize = 1 << 20);
        public static void BuildDelta(Stream source, IReadOnlyList<Digest128> receiverBlocks, int blockSize, Stream delta);
        public static void ApplyDelta(Stream basis, Stream delta, Stream output);
        public static void ApplyDelta(Stream basis, Stream delta, Stream output, int expectedBlockSize, int expectedBlockCount);   // wave 1; -1 skips a check
        public const long MaxOutputBytes = 2L << 30;                                       // ARCH §14.5 snapshot cap
    }
}
```

- **Wave 1 notes**: the client applies a warm-join delta with the five-argument `ApplyDelta(basis, delta, output, sentBlockSize, sentBlockList.Count)`, which rejects a delta built for another block list before reading the basis; discard the output on `DecodeException`. `CellMapVersionRing.SetExpectedLength(mapIndex, length)` must be called by game-state-channels for every map on the client (otherwise any size up to 16 MiB is accepted). Documented deviations: `WaterTileCodec` sends depth as signed 32-bit millimetres (sea depth exceeds 65.535 m); `BlockDelta` uses content-defined blocks averaging `blockSize`.
- **Game APIs**: none.
- **Tests**: tile enumeration at borders; height codec round trip and hash stability; water quantisation error bounds (depth 1 mm, pollution 1/255); XOR delta round trips; version ring keyframe fallback after four versions; settle detector timing; brush compensation: `Unmap(Map(s)) = s` over the whole range (both sides of 0.002, negative strengths), equal frame times leave the strength unchanged, 15 fps vs 60 fps produce equal height deltas in a reference implementation of the game's formula; block delta reconstructs files with insertions and modifications.
- **Done when**: game-fields can encode, send and apply tiles in the record/replay harness.

### 7.12 game-bootstrap

- **Milestone** M0 (`SessionRuntime` in M1, host sources in M2) · **Assembly** `SkylineTogether` · **Folder** `src/SkylineTogether/Bootstrap` · **Depends on** M0: core-contracts, core-protocol; the composition root is extended per milestone with core-session (M1), core-codec, core-identity, core-follower (M2), core-replication, core-integrity (M3), core-intents (M4), core-scheduling (M5a), core-motion (M6), core-fields (M8)
- **Responsibility**: the `IMod` entry point; the single registration list (ARCH Appendix A); `ModRuntime` (paths, services, settings, logging); `SkylineSystemBase` and `SafeRun` with the `SafeModeController` (ARCH §12); the probe registry (Appendix B); the self-test runner (§13.4); the diagnostics dump (§13.6) and debug commands; the native ZSTD/LZ4 compressor adapter; lifecycle fan-out; the manifest provider and the data-file digest contributor; the locale table (`LocaleSource`, ARCH §11.7); the settings page with the fallback session controls (ARCH §11.6); the cross-module hook interfaces (§6.1, including the tool-scoped gate state and the local-edit state used by the route guard); and the composition root `SessionRuntime`, which creates the Core engines and wires them to the game modules.

**Public contracts**

```csharp
namespace SkylineTogether.Bootstrap
{
    public sealed class SkylineTogetherMod : IMod { public void OnLoad(UpdateSystem updateSystem); public void OnDispose(); }   // top-level, no field initialisers (R05 §1.3)
    public static class Registration { public static void RegisterAll(UpdateSystem updateSystem); }                            // ARCH Appendix A.3, exact order
    public sealed class ModPaths { public string ModFolder; public string DataOverrideFolder; public string UserDataFolder; public string ModsDataFolder; public string TempFolder; public string LogsFolder; public string DumpsFolder; }
    public sealed class ServiceRegistry { public void Register<T>(T instance) where T : class; public T Get<T>() where T : class; public bool TryGet<T>(out T instance) where T : class; }
    public static class ModRuntime {
        public static bool IsInitialized { get; } public static ILog Log { get; } public static IClock Clock { get; } public static ModPaths Paths { get; }
        public static SkylineTogetherSettings Settings { get; } public static ServiceRegistry Services { get; } public static ProbeRegistry Probes { get; }
        public static SafeModeController SafeMode { get; } public static SelfTestRunner SelfTests { get; } public static SessionRuntime Session { get; }
        public static string LoadEmbeddedData(string fileName);                              // Data/<file> override first, then embedded resource
    }
    [Flags] public enum RoleMask : byte { None = 0, Idle = 1, Host = 2, Client = 4, Any = 7 }
    public abstract class SkylineSystemBase : GameSystemBase {
        protected abstract RoleMask ActiveIn { get; }
        protected virtual FaultCategory FaultCategory => FaultCategory.Capture;
        protected abstract void OnSafeUpdate();
        protected sealed override void OnUpdate();                                          // role check + try/catch → SafeMode
        protected bool IsHost { get; } protected bool IsClient { get; }
    }
    public static class SafeRun { public static bool Invoke(Action action, FaultCategory category, string context); }   // for systems that must derive from other bases
    public sealed class SafeModeController {
        public event Action<FaultCategory, string> Faulted;
        public void Report(FaultCategory category, Exception exception, string context);
        public int Count(FaultCategory category); public void Quarantine(TypeRank type, string reason); public bool IsQuarantined(TypeRank type);
    }
    public enum ProbeStatus : byte { Unresolved, Ok, Fallback, Failed }
    public sealed class ProbeInfo { public string Id; public bool Critical; public ProbeStatus Status; public string Detail; public string Fallback; }
    public sealed class FieldAccessor<TOwner, TValue> { public bool IsAvailable { get; } public TValue Get(TOwner owner); public void Set(TOwner owner, TValue value); }
    public sealed class ProbeRegistry {
        public FieldAccessor<TOwner, TValue> Field<TOwner, TValue>(string probeId, string fieldName, bool critical, string fallback);
        public Action<TOwner, TValue> PropertySetter<TOwner, TValue>(string probeId, string propertyName, bool critical, string fallback);
        public Delegate Method(string probeId, Type owner, string methodName, Type[] parameters, Type delegateType, bool critical, string fallback);
        public void Record(string probeId, bool critical, ProbeStatus status, string detail, string fallback);
        public IReadOnlyList<ProbeInfo> All { get; } public bool AnyCriticalFailed { get; } public ProbeInfo Get(string probeId);
    }
    public interface ISelfTest { string Id { get; } bool Critical { get; } bool RequiresCity { get; } SelfTestResult Run(); }
    public struct SelfTestResult { public bool Passed; public string Detail; public double Milliseconds; }
    public sealed class SelfTestReport { public IReadOnlyList<(string Id, bool Critical, SelfTestResult Result)> Results; public bool CriticalFailure { get; } }
    public sealed class SelfTestRunner { public void Register(ISelfTest test); public SelfTestReport Run(bool cityLoaded); public SelfTestReport LastReport { get; } }
    public sealed class FileLog : ILog { public FileLog(string path, LogLevel minimum); public IReadOnlyList<string> Tail(int lines); public LogLevel Minimum { get; set; } }
    public sealed class SkylineTogetherSettings : ModSetting {                               // ARCH §11.6; AutomaticSettings attributes on each property
        public SkylineTogetherSettings(IMod mod);
        public string DisplayName { get; set; } public int PlayerColor { get; set; } public int DefaultPort { get; set; }
        public int PerPeerUploadCapKBps { get; set; } public int HostUploadCapKBps { get; set; } public int MotionQuality { get; set; } public int WaterQuality { get; set; }
        public bool ShowNetworkPanel { get; set; } public bool VerboseLog { get; set; } public bool WriteAuditor { get; set; } public bool DiscoveryMode { get; set; }
        public bool DeveloperMode { get; set; } public float FakePacketLoss { get; set; } public int FakeLagMs { get; set; }
        public bool WriteDiagnosticsDump { set; }                                            // button
        public ProxyBinding ChatKey { get; set; } public ProxyBinding PingKey { get; set; }
        // fallback session controls (group "Session"; work without the .mjs module, ARCH §11.6)
        public string SelectedSave { get; set; }                                             // [SettingsUIDropdown] over the save list (game-join SaveList)
        public bool HostSelectedSave { set; } public bool HostThisCity { set; }              // buttons
        public string JoinAddress { get; set; } public string JoinPassword { get; set; }     // [SettingsUITextInput]
        public bool Join { set; } public bool Leave { set; } public bool ShowSessionStatus { set; }   // buttons; status in a message dialog
        public override void SetDefaults();
    }
    public sealed class LocaleSource : IDictionarySource {                                   // R05 §6; Data/locale.<id>.json, override folder first, English fallback
        public LocaleSource(string localeId, LocaleTable table, LocaleTable englishFallback);
        public static string Resolve(string key, string argument = null);                    // through LocalizationManager.activeDictionary (for bindings)
    }
    public sealed class DataFileDigests : IManifestContributor { }                           // effective content of every data file (ARCH §4.2.7)
    public static class DiagnosticsDump { public static void RegisterSection(string name, Action<JsonWriter> write); public static string Write(string reason); }
    public sealed class DiagnosticsSystem : SkylineSystemBase { public static void RegisterCommand(string name, Action<string[]> handler); public void StartMeasurement(double seconds); }
    public sealed class NativeCompressor : ICompressor { public static bool TryCreate(CompressionKind kind, out NativeCompressor compressor, out string failure); }   // probes P-ZSTD, P-LZ4
    public sealed class SessionLifecycleSystem : SkylineSystemBase {
        public static event Action<Purpose, GameMode> GamePreload; public static event Action<Context> GameLoaded; public static event Action<Purpose, GameMode> GameLoadingComplete;
    }
    public interface IManifestContributor { void Contribute(ref HandshakeDigests digests, HandshakeManifest manifest, bool includeDetails); }
    public sealed class GameInfoProvider : IGameInfoProvider { public void AddContributor(IManifestContributor contributor); }
    public sealed class SessionRuntime : IJoinHooks {
        public SessionController Controller { get; } public IHostReplicationEngine HostEngine { get; } public IClientReplicationEngine ClientEngine { get; }
        public bool IsHost { get; } public bool IsClient { get; } public bool IsLive { get; } public PeerId LocalPeer { get; }
        public void StartHosting(SessionSettings settings, string password, HostSource source);   // runs the registered IHostFlow (ARCH §7.11), then session start, then the lobby
        public float SessionStartProgress { get; }
        public void JoinLobby(ulong lobbyId, string password); public void JoinDirect(string host, int port, string password); public void Leave(string reasonKey);
        public event Action<RoleMask> RoleChanged;
        // + IJoinHooks (BeginJoiner, OnPeerLive, OnPeerLeft, TryResume, OnSnapshotLoaded, IsCaughtUp, CatchUpFraction, OnSessionEnded)
    }
}
namespace SkylineTogether.Bootstrap.Hooks                                                     // cross-module seams of the game assembly (§6.1)
{
    public sealed class FrameContext { public EntityManager EntityManager; public World World; public uint ClientTick; public double Now; }
    public sealed class SaveEntry { public string Id; public string DisplayName; public string CityName; public int Population; public string LastModified; public string GameMode; public bool PrerequisitesMet; }
    public interface IEndOfFrameHook { void OnEndOfFrame(FrameContext context); }            // called by game-client-apply's ClientEndOfFrameSystem
    public interface IGateRegistry { void AddGateEntries(IEnumerable<GateEntry> entries); void AddCosmetic(IEnumerable<string> systemTypeNames); void AddSessionLoadEntries(IEnumerable<string> systemTypeNames);
                                     void AddToolScoped(IEnumerable<ToolScopedGateEntry> entries); }   // game-follower
    public interface IIconAuthority { void AllowPrefabs(IEnumerable<string> notificationPrefabNames); }   // game-state-channels (adapters' localIconPrefabs)
    public interface ILocalEditRegistry { void AddDirectWriteTools(IEnumerable<string> toolTypeNames); void AddAuthorableTypes(IEnumerable<string> typeNames); void AddHostOnlyActions(IEnumerable<string> actions); }   // game-client-actions
    public interface ILoadOverrides { void AddJoinDisable(IEnumerable<string> systemTypeNames); void SetPushedSettings(IReadOnlyList<SettingEntry> settings); }   // game-join
    public interface ITypeRuleRegistry { void AddLocalTypes(IEnumerable<string> typeNames); void AddResendAfterJoinTypes(IEnumerable<string> typeNames); }   // game-ecs-schema
    public interface ISaveListSource { IReadOnlyList<SaveEntry> Refresh(); }                 // game-join; consumed by game-ui and the settings dropdown
    public interface IToolScopedGateState {                                                    // game-follower; consumed by game-client-apply's RouteGuardSystem (ARCH §4.11.4)
        bool AnyWindowOpenOrLingering { get; } bool IsWindowOpen(string guardName);
        void ReportGuardHealth(string guardName, bool healthy);                               // a failed guard self-test keeps its windows closed
    }
    public interface ILocalEditState : Core.ILocalEditView { }                                // game-client-actions (stage-B original set, predictions, pending proposals)
    public interface IHostFlow {                                                              // game-join (ARCH §7.11, §4.4.8)
        void Begin(HostSource source, Action<bool, string> onCityReady);                      // adjustments, load (Save) or checks (CurrentCity)
        void RunSessionStart(Action<float> progress, Action<bool, string> onDone);            // catalog, binding, priming (time-sliced)
    }
}
```

`SessionRuntime` consumes only Core seams (`IHostWorldReader`, `IWorldApplier`, `IWorldLoader`, `ISnapshotProvider`, `IGameInfoProvider`, ...) and the hook interfaces above, all looked up in the `ServiceRegistry`; every game module registers its implementations there when its systems are created. The composition root therefore needs no compile-time reference to a later game module, and the module-dependency check of `ApiCheck` holds for game-bootstrap as for every other module.

- **Game APIs and research**: `IMod`, `ModManager.TryGetExecutableAsset` (mod folder; `Assembly.Location` is empty, R05 §1.3); `UpdateSystem.UpdateAt/UpdateBefore/UpdateAfter` incl. two-type forms (`UpdateSystem.cs:158-181` [V], R05 §2); `GameSystemBase` lifecycle hooks (R05 §3.2); `ModSetting`, `AutomaticSettings`, `AssetDatabase.global.LoadSettings`, `ApplyAndSave` (unique class name, R05 §5); `LocalizationManager.AddSource` + `IDictionarySource` (R05 §6); `Colossal.Compression.CompressionUtils` / `NativeCompression` (R06 §5.4); `GameManager.instance` (`gameMode`, `userInterface`); `UpdateSystem` exception isolation (`UpdateSystem.cs:196-214` [V]).
- **Tests**: Core: locale-key coverage and `DataFileDigest` (core-contracts); game-side compile + `ApiCheck`; in game: the self-test runner lists every registered probe and self-test; a forced exception in a test system is caught, counted and shown; the dump zip contains every registered section; settings persist and reload; every settings label comes from the locale table; the fallback session controls host and join (M1/M2 scripts).
- **Done when**: M0 exit criteria (§5) pass.

### 7.13 game-steam-transport

- **Milestone** M1 · **Assembly** `SkylineTogether` · **Folder** `src/SkylineTogether/Platform/Steam` · **Depends on** core-contracts, core-transport, core-session (`SessionController`, lobby types), game-bootstrap
- **Responsibility**: Steam transports and discovery (ARCH §9): P2P and direct-IP connections through `ISteamNetworkingSockets` (`Send` reports `k_EResultLimitExceeded` as `SendResult.Busy`; every connection is wrapped in a core-transport `PeerOutbox`, whose `Pump` runs on the network thread; the connection's `SendBufferSize` is set to the outbox configuration), lanes through custom P/Invoke with the second-connection fallback, the socket-pair self-test, the network thread, lobbies, invites, rich presence and cold-start joins.

**Public contracts**

```csharp
namespace SkylineTogether.Platform.Steam
{
    public static class SteamNative {                                                        // flat exports of steam_api64 (probe P-LANES, R06 §5.6)
        public static bool TryInitialize(out string failure);
        public static bool IsAvailable { get; }
        public static bool ConfigureLanes(uint connection, int[] priorities, ushort[] weights);
        public static int SendMessages(uint connection, Lane lane, byte[][] payloads, int[] lengths, int count, int sendFlags, long[] results);
    }
    public sealed class SteamSocketsTransport : ITransport { public SteamSocketsTransport(ILog log, bool useLanes); public static bool IsSteamAvailable { get; } public bool LanesActive { get; } }
    public sealed class SteamIpTransport : ITransport { public SteamIpTransport(ILog log, int port, bool useLanes); }
    public sealed class SteamSocketPairTransport : ITransport { public static (SteamSocketPairTransport first, SteamSocketPairTransport second) Create(ILog log, bool useLanes); }
    public sealed class NetworkThread : IDisposable { public NetworkThread(SessionController session, ILog log, int sleepMilliseconds = 1); public void Start(); public double LoopMilliseconds { get; } public void Dispose(); }   // also pumps the outboxes
    public sealed class SteamLobbyService : IDisposable {                                     // LobbyInfo now lives in core-session
        public SteamLobbyService(ILog log);
        public void CreateLobby(Core.Session.LobbyPrivacy privacy, int maxMembers, Action<ulong> onCreated, Action<string> onFailed);
        public void SetLobbyData(ulong lobby, IReadOnlyDictionary<string, string> data);
        public void RequestFriendLobbies(Action<IReadOnlyList<Core.Session.LobbyInfo>> onList);
        public void JoinLobby(ulong lobby, Action<ulong> onJoinedOwner, Action<string> onFailed);
        public void LeaveLobby(); public void OpenInviteOverlay(ulong lobby); public bool IsMember(ulong lobby, ulong steamId);
        public event Action<ulong> JoinRequested;                                           // GameLobbyJoinRequested_t, GameRichPresenceJoinRequested_t, +connect_lobby
    }
    public sealed class RichPresenceService { public void SetConnect(string connect); public void SetGroup(string groupId, int size); public void Clear(); }
    public static class CommandLineJoin { public static bool TryGetLobby(out ulong lobbyId); }
    public sealed class SteamLaneSelfTest : ISelfTest { }                                   // socket pair: lanes, ordering, unreliable delivery, stats
}
```

- **Game APIs and research**: the game's `com.rlabrecque.steamworks.net.dll` (never ship another, R06 §3.2): `SteamNetworkingSockets.CreateListenSocketP2P`, `ConnectP2P`, `CreateListenSocketIP`, `ConnectByIPAddress`, `CreatePollGroup`, `SetConnectionPollGroup`, `ReceiveMessagesOnPollGroup`, `GetConnectionRealTimeStatus`, `SendMessageToConnection`, `CloseConnection`, `CreateSocketPair`; `SteamNetworkingUtils.InitRelayNetworkAccess`, `GetRelayNetworkStatus`, `SetConfigValue`/`GetConfigValue` (fake loss/lag, send rates); `SteamMatchmaking` lobbies; `SteamFriends.ActivateGameOverlayInviteDialog`, `SetRichPresence`; `Callback<T>` held in fields (R06 §3.1); flat exports `SteamAPI_ISteamNetworkingSockets_ConfigureConnectionLanes` and `SteamAPI_ISteamNetworkingSockets_SendMessages` (`NativeMethods.cs:2048, 2075` [V], wrappers broken, R06 §5.6) and `SteamAPI_ISteamNetworkingUtils_AllocateMessage` (`NativeMethods.cs:2164` [V]); interface pointers through `SteamAPI_SteamNetworkingSockets_SteamAPI_v012` (present in `steam_api64`, R06 §5.6) or, as Steamworks.NET itself does, `SteamInternal_FindOrCreateUserInterface(user, "SteamNetworkingSockets012" / "SteamNetworkingUtils004")` (`CSteamAPIContext.cs:229-239`, `Constants.cs:45-47` [V]); message cap and send flags (`Constants.cs:253-271`, R06 §4.2); callbacks pumped by `GameManager.Update` (R06 §2.3).
- **Tests**: Core-side: outbox behaviour against `NetSimTransport` with `BusyRate` (core-transport); in game: `SteamLaneSelfTest` on a socket pair, including a burst that overfills the send buffer (every reliable message arrives once, in order); M1 two-machine script (relay and direct IP, lanes or fallback logged, 200 MB blob throughput logged while chat stays responsive).
- **Done when**: M1 exit criteria pass on two real Steam accounts.

### 7.14 game-ui

- **Milestone** M0 (menu entry, HUD stub, settings), M1 (host/join panels with `LobbyOnly` hosting, lobby list, chat, players, mismatch), M2 (save list, host from a save, "Host this city", session-start progress), M4 (binding interposer, presence) · **Assembly** `SkylineTogether` (+ `ui/SkylineTogether.mjs`) · **Folder** `src/SkylineTogether/UI`, `ui/` · **Depends on** M0: core-contracts, core-protocol, game-bootstrap; M1: core-session
- **Responsibility**: everything the player sees (ARCH §11): the C# binding system (strings resolved from locale keys), the hand-written UI module and its generated binding-name list, toasts, the dialog queue, the binding interposer for vanilla controls (§5.7), the presence overlay and name tags (§11.4), and the network panel feed.

**Public contracts**

```csharp
namespace SkylineTogether.UI
{
    public sealed class SessionUISystem : UISystemBase, ISessionEvents {                     // UIUpdate; wraps OnUpdate in SafeRun.Invoke
        public const string Group = "skylineTogether";
        // every binding and trigger name is a public const string here; the build writes them to ui/bindings.generated.json (§2.6)
        // value bindings: session, players, chat, netStats, lobbies, saves, joinProgress, mismatch, sessionSettings, nameTags
        // trigger bindings: openCoop, host(saveId, settingsJson), hostCurrentCity(settingsJson), stopHosting, join, joinDirect, leave, sendChat, setRole,
        //                   setPermission, kick, ban, setSessionSetting, requestSpeed, refreshLobbies, refreshSaves, subscribeMissingMods, copyDiagnostics,
        //                   writeDump, requestResync, ping
        public void PushNetStats(in NetStatsSnapshot stats); public void PushNameTags(IReadOnlyList<NameTag> tags); public void PushLobbies(IReadOnlyList<Core.Session.LobbyInfo> lobbies);
        public void PushSaves(IReadOnlyList<Bootstrap.Hooks.SaveEntry> saves);              // from the registered ISaveListSource (game-join)
        // + ISessionEvents (incl. OnSessionStartProgress)
    }
    public struct NetStatsSnapshot { public float InKBps, OutKBps, BudgetKBps; public int PingMs; public int EpochsBehind; public float ApplyMs, CaptureMs; public float StalenessP50, StalenessP95;
                                     public int Repairs; public int PendingIntents; public string[] TopLeaks; public string RelayStatus; public string SyncLabelKey; public SessionPhase Phase; }
    public struct NameTag { public string Name; public uint Color; public float ScreenX, ScreenY; public bool Visible; }
    public static class NotificationBridge { public static void Push(string id, string titleKey, string textKey, int? progress = null, bool failed = false); public static void Pop(string id, float delaySeconds = 0); }
    public static class DialogQueue { public static void Confirm(string titleKey, string messageKey, Action<bool> onResult); public static void Message(string titleKey, string messageKey, Action onClosed = null); }
    public sealed class BindingInterposer : IDisposable {                                   // probe P-BINDINGS
        public BindingInterposer(ILog log, ProbeRegistry probes);
        public bool TryInterpose(string group, string name, IBinding replacement, out string failure);
        public void InterposeSessionControls(SessionRuntime runtime, bool isHost);          // time.setSimulationPaused/Speed, menu save/quicksave/load/continue/new/editor/exit
        public void RestoreAll(); public bool IsActive { get; }
    }
    public sealed class PresenceOverlaySystem : SkylineSystemBase {                          // Rendering, before OverlayRenderSystem
        public void OnPresence(PeerId peer, Core.Protocol.PresenceMessage presence, uint color); public void FlashRejected(IReadOnlyList<Entity> entities); public void RemovePeer(PeerId peer);
    }
    public sealed class PresenceSampler { public Core.Protocol.PresenceMessage Sample(); }       // local tool state at 10 Hz
}
```

`ui/SkylineTogether.mjs` contract: header comment exactly as ARCH §11.2; `register(moduleRegistry)` appends `CoopMenuEntry` to `"Menu"`, the HUD strip to `"GameTopRight"`, and panels/overlays to `"Game"`; it reads only the binding names of `ui/bindings.generated.json` through `window["cs2/api"]` (`bindValue`, `useValue`, `trigger`); it renders nothing when the globals are missing; it passes `node --check` and `tools/UiSmoke` (§2.6).

- **Game APIs and research**: `UISystemBase`, `ValueBinding`, `GetterValueBinding`, `RawValueBinding`, `TriggerBinding`, `CallBinding`, `EventBinding` (R05 §7.1-7.2); UI module header and `ui-mods` host (`UIModuleAsset.cs:44-107`, R05 §7.3); community globals `window.React`, `window["cs2/api"]` (third-party `webpack.config.js:31-43`, R05 §7.3); `NotificationSystem.Push/Pop` (R05 §8.1); `AppBindings.ShowConfirmationDialog`/`ShowMessageDialog` single callback slot (R05 §8.2); `GameManager.instance.userInterface.bindings` and `IBindingRegistry.RemoveBinding` (`IBindingRegistry.cs:7` [V]); `TimeUISystem` bindings `time.setSimulationPaused`/`setSimulationSpeed` (`TimeUISystem.cs:94-95` [V]); `OverlayRenderSystem.GetBuffer`, `Buffer.DrawCurve/DrawCircle/DrawLine`, `AddBufferWriter` (`OverlayRenderSystem.cs:133-251, 603, 636` [V]); `ToolSystem.activeTool` (presence); `CameraUpdateSystem` (name tags, R15 §7.1); text focus masks hotkeys (R05 §7.4).
- **Tests**: build: `node --check` and `tools/UiSmoke` with sample values for every session state (Node optional); Core: locale-key coverage; in game: M0 (menu entry, HUD stub, settings), M1 (host/join panels, lobby list, chat, mismatch dialog), M2 (save list, host from a save, "Host this city", "Preparing session" progress), M4 (speed buttons send requests and fall back when the probe fails, save offers "copy", exit asks to leave, cursors and ghosts visible, rejected predictions flash).
- **Done when**: every surface of ARCH §11.1 is reachable and the UI degrades gracefully when the module or a probe fails (the settings-page session controls still host and join).

### 7.15 game-ecs-schema

- **Milestone** M2 · **Assembly** `SkylineTogether` · **Folder** `src/SkylineTogether/Ecs` · **Depends on** core-contracts, core-codec, core-identity, core-replication (`PolicyTable`), game-bootstrap
- **Responsibility**: everything that bridges ECS and canonical bytes (ARCH §3, §4.2-4.3), as thin forwarders to Core where logic is involved: the type catalog built from the game's serializer library with per-type codecs (`MakeGenericType`; decode-then-commit helpers), `NetWriter`/`NetReader` forwarding every member to core-codec, the owned full-tag context, the entity map (slots, prefab ranks, derived keys, bind on reference), the family classifier (marker mask → core-identity `FamilyRules`), prefab ranks and class signatures, zone-type translation, archetype resolution (staging archetypes without `Created`/`Updated`), chunk access with the resolution contract (ARCH §3.3), merge policies, deferred-field policies and authorable masks of mixed types, presence-safe stand-in resolvers, the derived-owner report that feeds the received-value cache, the codec self-test, the census and the M2 `MeasurementProbe` (incl. `TagCounterSystem`), the load-parity census, and the replica fingerprint.

**Public contracts**

```csharp
namespace SkylineTogether.Ecs
{
    public sealed class TypeCatalogService {
        public static TypeCatalogService Build(World world, PolicyTableModel policies, ILog log);   // walks SerializerSystem.componentLibrary (R01 §4.1)
        public ITypeCatalog Catalog { get; } public ITypeSetRegistry TypeSets { get; } public Core.Replication.PolicyTable Policies { get; }
        public ITypeCodec Codec(TypeRank type); public bool TryGetRank(ComponentType componentType, out TypeRank rank); public ComponentType ComponentTypeOf(TypeRank rank);
        public TypeSetId SignatureOf(EntityArchetype archetype);                             // replicated signature (ARCH §3.6), cached per archetype
    }
    public interface ITypeCodec {
        TypeRank Rank { get; } CodecKind Kind { get; } int SizeInChunk { get; } bool IsBuffer { get; }
        unsafe void Write(void* component, ref NetWriter writer); unsafe uint RawHash(void* component);
        unsafe void DecodeThenCommit(void* component, ref NetReader reader, int recordLength);  // copy → deserialise into the copy → verify length → copy back (ARCH §4.7.4)
        void WriteBuffer(ref UnsafeUntypedBufferAccessor accessor, int index, ref NetWriter writer);
        void DecodeBufferThenCommit(ref UnsafeUntypedBufferAccessor accessor, int index, ref NetReader reader, int recordLength);   // scratch array, then resize + copy
        uint RawHashBuffer(ref UnsafeUntypedBufferAccessor accessor, int index);
        bool RoundTrip(EntityManager entityManager, Entity sample, out string detail);       // no ECS writes (ARCH §4.3.6)
    }
    public sealed class NetWriteState { public Core.Codec.CanonicalWriter Canonical; public Core.Codec.IEntityRefWriter Entities; public Context Context; }
    public struct NetWriter : IWriter { public NetWriter(NetWriteState state); }               // every member forwards one line to CanonicalWriter (ARCH §4.3.1)
    public sealed class NetReadState { public Core.Codec.CanonicalReader Canonical; public Core.Codec.IEntityRefReader Entities; public Context Context; }
    public struct NetReader : IReader { public NetReader(NetReadState state); }               // context = owned full-tag LoadGame context; reader bounded to the record
    public sealed class EntityRefBridge : Core.Codec.IEntityRefWriter, Core.Codec.IEntityRefReader {   // Entity ↔ NetRef via EntityMap
        public EntityRefBridge(EntityMap map, StandInResolvers standIns);
        public void BeginRecord(NetSlot slot, TypeRank type, Family family, string standInResolverOrNull);   // presence-safe types: unresolved keys → stand-in (ARCH §4.11.3)
        public IReadOnlyList<NetSlot> DerivedOwnersReferenced { get; }                     // owners of the derived keys decoded in this record (received-value cache, ARCH §3.7)
    }
    public interface IStandInResolver { Entity StandIn(Entity recordEntity, TypeRank type, int referenceOrdinal); }
    public sealed class StandInResolvers { public void Register(string name, IStandInResolver resolver); public bool TryGet(string name, out IStandInResolver resolver); }
    // built-in: "SegmentPathEndpoints" → the RouteLane end lane of waypoint i (origin) / start lane of waypoint i+1 (destination), else the waypoint
    public sealed class PathStateFacts : IPayloadFacts { public PathStateFacts(TypeCatalogService types); }   // decodes PathElement count / PathInformation distance and end points
    public static class MergePolicies { public static bool TryGet(TypeRank type, out IMergePolicy policy); }   // Chirp (liked)
    public interface IMergePolicy { unsafe void Mask(void* value); unsafe void Merge(void* local, void* incoming); }
    public interface IDeferredFieldSink { unsafe void OnDeferred(TypeRank type, Entity entity, void* decodedValue); }
    public static class DeferredFieldPolicies { public static bool TryGet(TypeRank type, out IDeferredFieldPolicy policy); }   // Building (m_RoadEdge, m_CurvePosition) (ARCH §4.2.5)
    public interface IDeferredFieldPolicy { unsafe void KeepLocal(void* decoded, void* current); }   // decode-then-commit: sink gets the decoded value, the column keeps the local fields
    public static class AuthorableMasks { public static bool TryGet(TypeRank type, out IAuthorableMask mask); }   // Road (flags without LightsOff), Building (historical flag)
    public interface IAuthorableMask { unsafe uint ProjectionHash(void* value); unsafe void MergeAuthorable(void* live, void* proposed); }   // ARCH §3.8, §4.2.5
    public static class ContextFactory { public static bool TryCreateFullTagReadContext(out Context context, out string detail); public static Context CreateWriteContext(); }   // probe P-CTX
    public sealed class PrefabRankMap : IPrefabIndex {
        public static PrefabRankMap Build(PrefabSystem prefabSystem, EntityManager entityManager, ILog log);   // also computes ClassSignature per rank (presence of prefab-data types)
        public Entity PrefabEntity(PrefabRank rank); public PrefabRank Rank(Entity prefabEntity); public IReadOnlyList<string> SortedKeys { get; } public IReadOnlyList<string> Duplicates { get; }
    }
    public sealed class ZoneTypeMap { public static ZoneTypeMap Build(EntityManager entityManager, PrefabRankMap prefabs); public PrefabRank ToRank(ushort zoneTypeIndex); public ushort ToIndex(PrefabRank rank); }
    public sealed class DerivedKeyResolvers {
        public DerivedKeyResolvers(EntityManager entityManager, ISlotTable slots, PrefabRankMap prefabs);
        public bool TryKeyOf(Entity derived, out DerivedKey key);                            // incl. owned-route elements (gate-bypass waypoints/segments)
        public bool TryResolve(in DerivedKey key, out Entity entity);                        // exact, then geometric fallback (ARCH §3.7)
        public void InvalidateOwner(NetSlot owner);
        public event Action<NetSlot> OwnerRegenerated;                                       // owner Updated/Deleted on a client → re-resolution (ARCH §3.7)
    }
    public sealed class EntityMap {
        public EntityMap(EntityManager entityManager, ISlotTable slots, PrefabRankMap prefabs, DerivedKeyResolvers derived, Func<Entity, NetSlot> bindOnReferenceOrNull);
        public NetRef Encode(Entity entity, bool ignoreVersion);                             // slot, prefab rank, derived key, bind on reference (inside the capture window), or Local
        public Entity Decode(in NetRef value, out bool missing, out NetSlot missingSlot);    // checks EntityManager.Exists for slot targets (ARCH §3.3)
        public Entity ToEntity(NetSlot slot); public bool TryGetSlot(Entity entity, out NetSlot slot);
        public int LocalReferenceCount { get; }                                              // diagnostics: refs written as Local
    }
    public sealed class FamilyClassifier : Bootstrap.Hooks.ITypeRuleRegistry {
        public FamilyClassifier(EntityManager entityManager);
        public Family Classify(ArchetypeChunk chunk);                                        // builds the FamilyMarker mask once per archetype → Core FamilyRules
        public Family ClassifyEntity(Entity entity, Family archetypeFamily);                 // per-entity facts only where FamilyRules.NeedsPerEntityFacts
        public ReplicationClass DefaultClass(Family family); public IReadOnlyDictionary<int, long> RuleCounts { get; }
    }
    public sealed class ArchetypeResolver {
        public ArchetypeResolver(EntityManager entityManager, TypeCatalogService types, PrefabRankMap prefabs);
        public EntityArchetype ResolveStaging(Family family, PrefabRank prefab, TypeSetId typeSet, SpawnFlags flags);   // prefab-baked + missing types, minus Created/Updated (ARCH §3.6)
        public ComponentTypeSet FinalizeTypes(SpawnFlags flags);                             // Created + Updated (+ Applied for movers), added after the transaction succeeded
    }
    public static unsafe class ChunkAccess {                                                 // the resolution contract (ARCH §3.3); the only way the applier reaches a chunk
        public static bool TryLocate(EntityManager entityManager, Entity entity, out ArchetypeChunk chunk, out int indexInChunk);   // Exists first, then GetStorageInfo
        public static bool TryColumnRO(ArchetypeChunk chunk, ref DynamicComponentTypeHandle handle, int typeSize, out void* column);   // false if the chunk lacks the type
        public static bool TryColumnRW(ArchetypeChunk chunk, ref DynamicComponentTypeHandle handle, int typeSize, out void* column);   // stamps the change version once
    }
    public sealed class CodecSelfTest : ISelfTest { }
    public sealed class FullTagContextSelfTest : ISelfTest { }
    public sealed class CensusReport { public Dictionary<Family, long> Entities; public Dictionary<Family, long> Chunks; public int Archetypes; public Dictionary<string, double> CodecMicroseconds;
                                       public Dictionary<Family, double> CanonicalBytesPerEntity; }
    public sealed class Census { public CensusReport Measure(EntityManager entityManager, FamilyClassifier classifier, TypeCatalogService types); }
    public sealed class TagCounterSystem : SkylineSystemBase { public IReadOnlyDictionary<(Family, string), long> CountsPerSecond { get; } }   // before PrepareCleanUpSystem; measuring only
    public sealed class MeasurementProbe {                                                   // M2 stand-ins (ARCH §13.7)
        public MeasurementProbe(EntityManager entityManager, Census census, TagCounterSystem tags, TypeCatalogService types, FamilyClassifier classifier);
        public void Start(double seconds); public void Tick(double now, float cameraX, float cameraZ);   // chunk change-version sampler (4/20/60 s), mover distance rings at 1 Hz
        public bool Running { get; } public void WriteReport(JsonWriter output);
    }
    public sealed class LoadParityCensus {                                                   // ARCH §7.6: per type count + multiset hash of canonical bytes of bound entities
        public static IReadOnlyDictionary<TypeRank, (long Count, ulong Hash)> Compute(EntityManager entityManager, TypeCatalogService types, EntityMap map);
        public static IReadOnlyList<string> Compare(IReadOnlyDictionary<TypeRank, (long Count, ulong Hash)> host, IReadOnlyDictionary<TypeRank, (long Count, ulong Hash)> client, ITypeCatalog catalog);
    }
    public static class ReplicaFingerprint { public static ulong Compute(EntityManager entityManager, TypeCatalogService types, FamilyClassifier classifier, EntityMap map, double budgetMs, ref object cursor); }   // M2 spectator check
}
```

- **Game APIs and research**: `SerializerSystem.componentLibrary`/`ComponentSerializerLibrary` (R01 §4.1; `SerializerSystem.cs:41`); `ISerializable`, `IWriter`, `IReader` (`ISerializable.cs:5-7` [V], R01 §2); `Context` constructor and `ContextFormat` (`Context.cs:21-37`, `ContextFormat.cs:9-35` [V]); `FormatTags`, `Version.current`; `TypeManager.GetTypeInfo`, `GetEntityOffsets`, `GetTypeIndexFromStableTypeHash` (R08 §5); `EntityManager.GetDynamicComponentTypeHandle` (`EntityManager.cs:2447` [V]), `GetStorageInfo` (`:1386` [V]); `ArchetypeChunk.SequenceNumber`, `GetChangeVersion`, `GetOrderVersion`, `GetDynamicComponentDataArrayReinterpret`, `GetUntypedBufferAccessor`, `GetEnableableBits`, `IsComponentEnabled`, `SetComponentEnabled` (`ArchetypeChunk.cs:38-835` [V]); `PrefabSystem.TryGetPrefab`, `PrefabBase.GetPrefabID`, `PrefabSystem.GetEntity` (R04 §1.3); prefab archetype data (`ObjectData`, `NetData`, `MovingObjectData`, `TrainObjectData`, `AreaData`, `RouteData`, `AggregateNetData`, `ArchetypeData`; R04 §1.1, R15 §4.1); `ZoneSystem` zone indices (R04 §4.5); `SubObjectSystem` ownership criterion (`SubObjectSystem.cs:1172-1195`); `LaneSystem` lane keys (R13 §2); `BlockSystem` block geometry (R13 §4.3); segment path types and waypoint lanes for the `SegmentPathEndpoints` stand-in (`Game.Pathfind/PathInformation.cs`, `Game.Routes/RouteLane.cs`, `RouteWaypoint`/`RouteSegment` [V]); gate-bypass route ownership (`Game.Buildings/InitializeSystem.cs:154-168`) for owned-route element keys.
- **Tests**: Core: the logic behind these forwarders is tested in core-codec (golden vectors for every `IWriter`/`IReader` member), core-identity (`FamilyRules`) and core-replication (apply planning); in game: `FullTagContextSelfTest` (every `FormatTags` bit set, size guard), `CodecSelfTest` on a loaded 100k city (100 % identical bytes, time per family), a decode-then-commit test that feeds a truncated payload and verifies the live component is unchanged; a `PathInformation` record whose lane keys do not resolve decodes to the stand-ins (never to null); a `Building` record leaves `m_RoadEdge`/`m_CurvePosition` local and reaches the deferred-field sink; census report, classification counts per rule, `MeasurementProbe` report, load-parity census equal between host and joiner, replica fingerprint stable for 10 minutes on a follower client.
- **Done when**: M2 self-tests are green on a large save.

### 7.16 game-join

- **Milestone** M2 (host flow, session start with binding, session-load set; shadow priming in M3; resume points in M7, warm join in M9) · **Assembly** `SkylineTogether` · **Folder** `src/SkylineTogether/Join` · **Depends on** core-contracts, core-session, core-identity, game-bootstrap, game-ecs-schema, game-follower (`HostSessionAdjustments` in the host flow, `FollowerMode` around client loads)
- **Responsibility**: starting a session and persisting slots (ARCH §3.4, §4.4.8, §7): the host flow from a save or the running city with the save list (§7.11), host session start (catalog, binding, priming through `IHostReplicationEngine`, time-sliced), `NetIdSystem`, the host snapshot service and bind pass (with `hostBuildOrderNext`), the client loader with load overrides, the session-load set, the pre-load sweep and the build-order counter restore, post-load validation (incl. the load-parity census), "save a copy", resume points and (M9) warm join.

**Public contracts**

```csharp
namespace SkylineTogether.Join
{
    public sealed class NetIdSystem : GameSystemBase, IDefaultSerializable, IPostDeserialize {
        public static NetIdSystem Instance { get; }
        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter;               // formatVersion, lineage, high-water, (Entity, NetId) pairs through the save's entity table
        public void Deserialize<TReader>(TReader reader) where TReader : IReader;             // containers only, never EntityManager (R12 §10)
        public void SetDefaults(Context context); public void PostDeserialize(Context context);
        public IReadOnlyList<KeyValuePair<Entity, NetId>> LoadedBindings { get; } public int LoadedHighWaterMark { get; } public uint LineageId { get; }
        public Func<IEnumerable<KeyValuePair<Entity, NetId>>> BindingsProvider { set; }       // host: current bindings for Serialize
    }
    public sealed class SaveList : Bootstrap.Hooks.ISaveListSource { public bool TryGet(string saveId, out SaveInfo info); }   // Metadata<SaveInfo> assets, vanilla filter (ARCH §7.11)
    public sealed class HostFlow : Bootstrap.Hooks.IHostFlow {                                // ARCH §7.11
        public HostFlow(SaveList saves, HostSessionStart sessionStart, ILog log);
        // Begin: LobbyOnly → ready at once; Save → HostSessionAdjustments.Apply, vanilla load preparation without ApplyOptions, GameManager.Load on "SaveLoadGame";
        //        CurrentCity → refused while TutorialSystem.tutorialIntroUnfinished, else adjustments and pause
        // + IHostFlow
    }
    public sealed class HostSessionStart {                                                    // ARCH §4.4.8; time-sliced, session paused
        public HostSessionStart(IHostReplicationEngine engine, TypeCatalogService types, FamilyClassifier classifier, EntityMap entities, ILog log);
        public void Begin(); public bool Step(double budgetMs);                               // BindExisting from NetIdSystem, bind sweep, prime (M3); true when complete
        public float Progress { get; } public int BoundEntities { get; } public double ElapsedSeconds { get; }
    }
    public sealed class SnapshotService : ISnapshotProvider { public SnapshotService(SessionRuntime runtime, ILog log, ModPaths paths); public bool SnapshotPendingThisFrame { get; } public double LastStallSeconds { get; } public long LastSizeBytes { get; } }
    public sealed class SnapshotBindSystem : SkylineSystemBase { }                           // before SaveGameSystem, snapshot frames only; binds only entities new since session start (ARCH §4.4.6)
    public sealed class LoadOverrides : Bootstrap.Hooks.ILoadOverrides {
        public void Apply(string hostModeName, string mapName, IReadOnlyList<SettingEntry> pushedSettings, IReadOnlyList<string> joinDisableSystems);   // incl. the session-load set (ARCH §6.3, §7.4)
        public void OnGameLoaded(uint hostBuildOrderNext);                                    // GenerateEdgesSystem.GetBuildOrder().value = hostBuildOrderNext
        public void Restore(); public bool Active { get; }
    }
    public sealed class PreLoadSweepSystem : SkylineSystemBase { public bool Armed { get; set; } public int LastSweptCount { get; } }   // after ClearSystem (Deserialize)
    public sealed class PostLoadValidator { public LoadOutcome Validate(SnapshotManifest manifest, EntityManager entityManager, IReadOnlyList<KeyValuePair<Entity, NetId>> bindings); }
    public sealed class SnapshotLoader : IWorldLoader { public SnapshotLoader(LoadOverrides overrides, PostLoadValidator validator, ILog log, ModPaths paths); }   // OpenBlobTarget + BeginLoad
    public sealed class SaveCopyService { public void RequestHostCopy(Action<string> onSaved, Action<string> onFailed); }
    public sealed class ResumePointService { public void TickHost(double now); public void StoreReceived(string blobPath, SnapshotManifest manifest); public IReadOnlyList<string> AvailableResumePoints { get; } }
}
```

- **Wave 1 note (from core-fields)**: apply warm-join deltas with `BlockDelta.ApplyDelta(basis, delta, output, sentBlockSize, sentBlockList.Count)` and discard the output on `DecodeException`.
- **Game APIs and research**: `SaveGameSystem.stream/context/RunOnce/referencedContent` (`SaveGameSystem.cs:22-84` [V]); `TaskManager.EnqueueTask("SaveLoadGame", ...)` (R05 §4.1); `AssetDatabase.GetTransient` (`AssetDatabase.cs:301` [V]) + `AddAsset<SaveGameData>` + `GetWriteStream` (R12 §4.3); `GameManager.Load(GameMode, Purpose, IAssetData)` (`GameManager.cs:1162` [V]); save enumeration `AssetDatabase.global.GetAssets(default(SearchFilter<SaveGameMetadata>))`, `SaveInfo` (`MenuHelpers.cs:69-83`, `MenuUISystem.cs:563-576`, `Game.Assets/SaveInfo.cs` [V]) and the vanilla load preparation (`MenuUISystem.cs:789-805` [V]); `TutorialSystem.tutorialIntroUnfinished` (`TutorialSystem.cs:233` [V]); `CityConfigurationSystem.overrideLoadedOptions/overrideThemeName`, `GameModeSystem.overrideMode`, `MapMetadataSystem.mapName/prefabReferences` (R12 §4.2); `SharedSettings.instance.gameplay.pausedAfterLoading`/`showTutorials` (R12 §5.3); `PlatformManager.achievementsEnabled`; the session-load set `ResetBuildOrderSystem`, `CitizenSystem`, `RandomLocalizationSystem` (`SystemOrder.cs:850-857`, `ResetBuildOrderSystem.cs:62-148`, `CitizenSystem.cs:38-53, 95-111` [V]) and `GenerateEdgesSystem.GetBuildOrder()` (`GenerateEdgesSystem.cs:2215` [V]); `IDefaultSerializable`, `IPostDeserialize`, `PostDeserialize<T>` registration, `SerializerSystem.SetDirty`, `ObsoleteSystemSerializer` skip (R12 §1.4, §6.3); `ClearSystem` (`SystemOrder.cs:796` [V]); `GameManager.ArePrerequisitesMet` (`GameManager.cs:1847-1861`); placeholders via negative `PrefabData.m_Index` (R12 §5.4); the Serialize stall (`WaterSystem.cs:1042-1063`, R12 §6.1).
- **Tests**: Core: snapshot manifest serialisation (in core-contracts/protocol tests); session start against the FakeWorld (core-replication); in game: host flow from a save and "Host this city" (refused with an active tutorial intro), session-start duration logged on 20/100/300 MB cities, host save round trip with the block (all bound, clean load without the mod), snapshot cost logged, public load path with overrides and the session-load set (no first-frame tick, correct game mode, build-order counter equals `hostBuildOrderNext`), load-parity census equal on host and joiner, post-load validator fails cleanly on a city with missing content, two-machine M2 script.
- **Done when**: M2 exit criteria pass.

### 7.17 game-follower

- **Milestone** M2 (tool-scoped gates M4) · **Assembly** `SkylineTogether` · **Folder** `src/SkylineTogether/Follower` · **Depends on** core-contracts, core-follower, core-motion (`ClockModel`), game-bootstrap
- **Responsibility**: follower mode on clients and speed authority on the host (ARCH §6): the gate controller (data-driven, critical entries, the session-load set, the line and status-producer gates of ARCH §4.11, simulation-phase disabling, re-assertion, producer/consumer self-check through core-follower's `GatePlanner`, unclassified report, restore; implements `IGateRegistry`), **tool-scoped gates** (route-tool preview windows through core-follower's `PreviewWindow`, ARCH §6.3, §4.11.4; implements `IToolScopedGateState`), the **load hand-over** (core-follower `LoadHandover` executed in `ClientClockSystem`; probe `P-SIMLOADING`; also restores the host's `pausedAfterLoading`), the client clock driver with the cosmetic runner (never executing `GameSimulation`; `CosmeticSchedule`; the cosmetic list without `StreetLightSystem`), speed authority and mirroring, the `GroundHeightSystem` drain, tutorial and autosave suppression, `runInBackground` while hosting, and the switch into and out of follower mode around loads.

**Public contracts**

```csharp
namespace SkylineTogether.Follower
{
    public sealed class GateController : Bootstrap.Hooks.IGateRegistry {
        public GateController(World world, UpdateSystem updateSystem, GateTable table, CosmeticList cosmetic, ProbeRegistry probes, ILog log);
        public Core.Follower.GateReport Resolve();                                           // names → systems, then Core GatePlanner checks
        public void Apply(); public void Reassert(); public void Restore();                 // ARCH §6.3
        public void ApplySessionLoadSet(); public void RestoreSessionLoadSet();             // from the session load's OnGamePreload until OnGameLoadingComplete
        public IReadOnlyList<ComponentSystemBase> CosmeticSystems { get; }
        public void RemoveCosmetic(string systemTypeName, string reason);                   // used by game-integrity's cosmetic write audit (M3)
        // + IGateRegistry (adapter entries, cosmetic additions, session-load additions, tool-scoped additions forwarded to ToolScopedGates)
    }
    public sealed class CosmeticRunner { public CosmeticRunner(IReadOnlyList<ComponentSystemBase> systems, Core.Follower.CosmeticSchedule schedule); public void Tick(uint frameIndex, int iteration); }
    public sealed class ToolScopedGates : Bootstrap.Hooks.IToolScopedGateState {             // ARCH §6.3, §4.11.4; ticked by ClientClockSystem (LateUpdate),
        public ToolScopedGates(World world, IReadOnlyList<ToolScopedGateEntry> entries, ILog log);   // so a window opened in frame N is effective from frame N+1
        public void Tick(double now, string activeToolTypeName, bool appliedThisFrame, bool clientLive);   // appliedThisFrame = activeTool.applyMode == Apply;
                                                                                             // Core PreviewWindow per entry; flips Enabled at the edges
        public void CloseAll();                                                              // leaving, resync, Faulted
        // + IToolScopedGateState
    }
    public sealed class ClientClockSystem : SkylineSystemBase {                               // LateUpdate, before SimulationSystem; executes the load hand-over first
        public Core.Motion.ClockModel Model { get; } public int CurrentLag { get; } public bool UsingFallback { get; }
        public Core.Follower.HandoverState HandoverState { get; }
        public void BeginHandover(uint snapshotFrameIndex);                                 // called from OnGameLoadingComplete of a session load
        public void FollowEpochTick(uint hostTick); public void SetCatchUp(bool catchingUp); public void HardSet(uint frameIndex);
    }
    public sealed class SimulationLoadingProbe {                                              // probe P-SIMLOADING (ARCH App. B)
        public SimulationLoadingProbe(ProbeRegistry probes, World world);
        public bool IsAvailable { get; } public bool TryReadIsLoading(out bool isLoading);
        public bool TryForceLoaded(float selectedSpeed);                                     // fallback: m_IsLoading = false, m_SelectedSpeed = value
    }
    public sealed class SpeedAuthoritySystem : SkylineSystemBase { public void SetSessionSpeed(int speed, bool paused); public bool PauseWhenHostInMenu { get; set; } public event Action<int, bool> HostLocalSpeedChange; }
    public sealed class SpeedMirrorSystem : SkylineSystemBase { public event Action<int, bool> LocalSpeedChangeDetected; }   // before PreRenderSystem (ARCH §6.2)
    public sealed class GroundHeightDrain { public GroundHeightDrain(ProbeRegistry probes, World world); public bool IsAvailable { get; } public void Drain(); }   // probe P-GROUNDHEIGHT
    public sealed class FollowerMode {
        public FollowerMode(GateController gates, ClientClockSystem clock, GroundHeightDrain drain, ILog log);
        public void PrepareForLoad();                                                        // re-enable SimulationSystem, force pausedAfterLoading, tutorials off, session-load set armed
        public void Enter(uint snapshotFrameIndex);                                         // OnGameLoadingComplete: re-assert gates, restore the session-load set, BeginHandover
                                                                                            // (SimulationSystem stays enabled until the hand-over disables it, ARCH §6.1)
        public void Exit();                                                                 // restore everything
        public bool Active { get; } public bool HandoverComplete { get; }
    }
    public static class HostSessionAdjustments { public static void Apply(); public static void Restore(); public static void RestorePausedAfterLoadingAfterFirstUpdate(); }   // tutorials off, pausedAfterLoading, runInBackground
}
```

- **Wave 1 notes (from core-follower)**:
  - Executing `HandoverActions.EnableSimulation`: also write `SimulationSystem.selectedSpeed = 0` through the public setter (in addition to keeping `pausedAfterLoading` forced); while the loading flag is set the write is ignored and harmless.
  - `HandoverInputs.SpeedReadBackOk`: read `selectedSpeed` back immediately after writing it, in the same LateUpdate. If the follower clock started paused (it wrote 0), report true only if P-SIMLOADING reads `m_IsLoading` false; without the probe, only if a `LoadHandover.SentinelSpeed` write reads back (then write 0 again).
  - `GateController`: pass the adapters' `ToolScopedGateEntry` items (`IGateRegistry.AddToolScoped`) to `GatePlanner.Plan` inside a merged `GateTable` (`table.ToolScopedEntries` + adapter entries), not as `GateAction.ToolScoped` rows, so their tools resolve and the producer/consumer check knows which tools apply.
  - `CosmeticSchedule.Add` throws `ArgumentOutOfRangeException` for an invalid interval/offset (as `UpdateSystem` does): catch it per system, log and skip that system.
- **Game APIs and research**: `UpdateSystem.m_Systems` (`UpdateSystem.cs:15-48, 78` [V], probe `P-SYSTEMS`); `SystemBase.Enabled` (R03 §7); `SimulationSystem.frameIndex/frameTime/smoothSpeed` private setters, `selectedSpeed`, `Deserialize` fallback (`SimulationSystem.cs:68-87, 140-146` [V], probe `P-CLOCK`); the loading flag `m_IsLoading`, which gates the `selectedSpeed` setter and is cleared only by `SimulationSystem.OnUpdate` once `GameManager.isGameLoading` is false (`SimulationSystem.cs:72-85, 155-162, 202-215` [V], probe `P-SIMLOADING`); `GameManager.state`/`State.WorldReady`, set only after `onGameLoadingComplete` (`GameManager.cs:264-274, 292, 1136-1137` [V]); `UpdateSystem.Update(SystemUpdatePhase.PreSimulation/PostSimulation)` (`UpdateSystem.cs:186-219` [V]); `GameSystemBase.GetUpdateInterval/GetUpdateOffset/ResetDependency` (`GameSystemBase.cs:131-141` [V]); `RenderingSystem.frameIndex` for the render lead and `PreRenderSystem` ordering (`RenderingSystem.cs:173-221`, `PreRenderSystem.cs:20-23` [V]); `TimeUISystem` paused barrier (`TimeUISystem.cs:93` [V], probe `P-BARRIER`); `GroundHeightSystem.m_ReadyUpdates` (probe `P-GROUNDHEIGHT`); `AutoSaveSystem` (R12 §6.2); `SharedSettings.gameplay.showTutorials` (`TutorialSystem.cs:166` [V]); `Application.runInBackground`; the cosmetic list and gate table sources (R11 §3, §8; ARCH Appendix E).
- **Tests**: Core: `ClockModel` (core-motion), `LoadHandover`, `GatePlanner`, `PreviewWindow`, `CosmeticSchedule` (core-follower); in game: gate report green on the targeted mod set (incl. `RoutePathSystem`, `RoutePathReadySystem`, `WaypointConnectionSystem`, `ConnectionWarningSystem` off on clients); the route-tool preview window opens with the route tool and closes 2 s after (M4), and stays closed when the guard reports unhealthy; the hand-over self-test after every session load (`SimulationSystem` disabled after `WorldReady` only once `m_IsLoading` is false, `selectedSpeed` reads back, render tick advances, movers interpolate); from M3 the cosmetic write audit (game-integrity) is clean; follower client for 10 minutes without input (replica fingerprint unchanged, memory flat, no errors); day/night, weather and snow advance; speed changes mirrored; alt-tab of the host does not stall; probe-fallback paths (simulated by disabling `P-CLOCK` and `P-SIMLOADING` in developer mode) still run.
- **Done when**: M2 exit criteria pass (tool-scoped gates: the M4 route-tool criteria).

### 7.18 game-host-capture

- **Milestone** M3 (EDC, capture window, path-result trigger, ingress hook, discovery, liveness, co-capture, K capture of station stops and gate-bypass routes), M5a (SDC through `ScanScheduler`, hot set), M5c (attention queries), M6 (mover hooks, `Passenger`/`RouteVehicle` co-capture) · **Assembly** `SkylineTogether` · **Folder** `src/SkylineTogether/Host` · **Depends on** core-contracts, core-replication, game-bootstrap, game-ecs-schema; M5c: core-scheduling
- **Responsibility**: everything the host reads from ECS (ARCH §4.4): the tag sweep before `PrepareCleanUpSystem` (incl. the `Unlock` flag and the forward references of deleted agents), the capture flush after `EndFrameBarrier` as the capture window (command-queue drain, EDC, the **path-result trigger** for route segments (ARCH §4.4.2 step 3a), pending spawns by `spawnEmitted`, S/G coalescing driver, authorable projections, reverse-buffer co-capture incl. `Passenger`, `RouteVehicle`, `ConnectedRoute`, `GuestVehicle` and keyed owners, keyed derived entities incl. station stops and gate-bypass routes, the ingress hook for commits and repairs), the chunk scanner executing the Core `ScanScheduler` (SDC), the hot set, discovery and liveness sweeps, the host world reader for repairs and coalesced flushes, attention queries (in-view static set, closures, signalised junctions), and the extension points for channels and the motion sampler.

**Public contracts**

```csharp
namespace SkylineTogether.Host
{
    [Flags] public enum TagBits : byte { Created = 1, Updated = 2, Deleted = 4, Applied = 8, BatchesUpdated = 16, EffectsUpdated = 32, PathfindUpdated = 64 }
    public sealed class SweepBuffer {
        public struct Entry { public EntityKey Entity; public TagBits Tags; public Family Family; }
        public struct ForwardRef { public NetSlot Member; public TypeRank ForwardType; public EntityKey Owner; }    // owners of reverse buffers of deleted agents (ARCH §4.2.6)
        public IReadOnlyList<Entry> Entries { get; } public IReadOnlyList<NetSlot> DeletedSlots { get; } public IReadOnlyList<EntityKey> ZoneBlocks { get; }
        public IReadOnlyList<ForwardRef> DeletedForwardRefs { get; } public bool UnlockEventsSeen { get; }        // ARCH §4.4.1
        public void Clear();
    }
    public sealed class TagSweepSystem : SkylineSystemBase { public SweepBuffer Current { get; } }                // before PrepareCleanUpSystem
    public sealed class HostCaptureContext {
        public EntityManager EntityManager; public IHostReplicationEngine Engine; public TypeCatalogService Types; public EntityMap Entities; public FamilyClassifier Classifier;
        public NetWriteState WriteState; public uint HostTick; public double Now; public double BudgetMs;
        public void Serialize(Entity entity, TypeRank type, ByteWriter output, out uint rawHash);
        public IComponentPayloadSource PayloadsOf(Entity entity, TypeSetId typeSet);
        public void CoCaptureReverseBuffers(Entity member, TypeRank forwardType, Entity oldOwnerOrNull);   // re-serialises owners' reverse buffers as ObserveSource.CoCapture
        public bool TryDecodeShadowForwardRef(NetSlot member, TypeRank forwardType, out Entity oldOwner);  // old owner from the value shadow bytes through the codec
    }
    public interface IHostChannel { string Name { get; } void Capture(HostCaptureContext context); }                // game-state-channels, game-fields
    public interface IMotionSampler { void Sample(HostCaptureContext context, uint fromTickExclusive, uint toTickInclusive); }   // game-motion
    public interface IIngressHook { void CommitInsideWindow(HostCaptureContext context); }                        // game-client-actions' HostIngress (ARCH §4.4.2 step 9)
    public sealed class PathResultTrigger {                                                   // ARCH §4.4.2 step 3a: Event+PathUpdated owners → segment path state
        public void Capture(HostCaptureContext context);                                     // bound segments: ObserveValue(..., ObserveSource.PathResult); gate-bypass segments: ObserveKeyed
        public int LastCaptured { get; }
    }
    public struct CaptureStats { public double SweepMs, EdcMs, SdcMs, MotionMs, ChannelsMs, IngressMs; public int Spawns, Despawns, Values, Touches, Discovered, Vanished, CoCaptured, CommandsDrained, PathResults; }
    public sealed class HostCaptureFlushSystem : SkylineSystemBase, IHostWorldReader {                              // after EndFrameBarrier; the capture window
        public void RegisterChannel(IHostChannel channel); public void RegisterMotionSampler(IMotionSampler sampler); public void RegisterIngressHook(IIngressHook hook);
        public void AddToHotSet(NetSlot slot, double untilSeconds); public CaptureStats LastStats { get; }
        public bool PrefabScanRequested { get; }                                             // set for two flushes after the sweep saw Unlock events (ARCH §4.10.3)
        public event Action<ReplicationClass> ScanCycleCompleted;
        // + IHostWorldReader
    }
    public sealed class ChunkScanner {                                                        // executes Core ScanScheduler decisions (M5a)
        public ChunkScanner(Core.Replication.ScanScheduler scheduler, int maxHotSet = 2048);
        public void RunSlice(HostCaptureContext context, double budgetMs);                   // chunk enumeration, version reads, RO columns via ChunkAccess
        public void RecordBaselines(HostCaptureContext context, double budgetMs);            // host session start (ARCH §4.4.8)
    }
    public sealed class AttentionQueries {                                                                          // M5c
        public AttentionQueries(Core.Scheduling.AttentionSets attention);
        public void Update(HostCaptureContext context);                    // in-view static set via quad trees (1 Hz), closures, in-view signalised junctions → hot set
        public IReadOnlyList<NetSlot> InViewJunctions(PeerId peer);
    }
}
```

- **Game APIs and research**: `EntityQuery` with `Any`/`None` and `ToArchetypeChunkArray`, `CompleteDependency` (R08 §1, §12); lifecycle tags and their cleanup (`PrepareCleanUpSystem.cs:21-54`, `CleanUpSystem.cs:24-56`, R02 §5); placement before `PrepareCleanUpSystem` and after `EndFrameBarrier` (`SystemOrder.cs:49-50` [V], `EndFrameBarrier.cs:34-43` [V], `UpdateSystem.cs:382-441` [V]); chunk change and order versions (`ArchetypeChunk.cs:153-244` [V], R08 §3); `EntityManager.Exists`, `GetStorageInfo` (`EntityManager.cs:1386` [V]); quad trees `Game.Objects.SearchSystem.GetStaticSearchTree`, `Game.Net.SearchSystem.GetNetSearchTree` with public iteration (P-B §4.7.4); info-panel cadence (`SelectedInfoUISystem.cs:181, 383`); `TrafficLights` junctions (§4.9.9); lifetime of `Event` entities such as `Unlock` (`PrepareCleanUpSystem.cs:21-28`, `MilestoneSystem.cs:96-102`, `UnlockSystem.cs:206-213` [V]); silent reverse-buffer edits by `HouseholdAndCitizenRemoveSystem` (`:100-117` [V]); `SelectedInfoUISystem.RequestUpdate` on the host at sample ticks (`:343` [V]); path results: `Event+PathUpdated` created through `EndFrameBarrier` by `PathfindResultSystem` (`PathfindResultSystem.cs:246-254` [V]) for requests with event data (`PathfindQueueSystem.cs:511-625` [V]), written silently by `PathfindJobs.ProcessResultsJob` (`PathfindJobs.cs:1148-1290` [V]); runtime reverse-buffer writers of `Passenger`, `RouteVehicle`, `ConnectedRoute`, `GuestVehicle` (ARCH §4.2.4); station stops as sub-objects (`SubObjectSystem.cs:2549` [V]) and `TransportStopSystem` (`:83-106` [V]).
- **Tests**: Core: the observation API, capture window, `spawnEmitted`, co-capture and revisions in core-replication's FakeWorld tests, and the scan bookkeeping in core-replication's `ScanScheduler` tests (the game scanner only executes its decisions); in game: shadow-client self-test (M5a) shows zero hash mismatches on a live city; capture timings within budget in the measurement report; record/replay (M3) reproduces host edits on one machine; a citizen death removes it from its household on the client in the same transaction; a new line's segment paths reach the client in the epoch after the host's path result (M3, `PathResults` counter); a re-routed waypoint updates both stops' `ConnectedRoute` in one transaction (M3/M4); boarding passengers and dispatched line vehicles update `Passenger` and `RouteVehicle` in the same transaction (M6); discovery finds Better Bulldozer record entities; liveness sweep despawns directly destroyed entities; a host milestone unlock reaches the client's toolbar within a frame (M5b).
- **Done when**: M3 (structure) and M5a (values) exit criteria pass from the host side.

### 7.19 game-client-apply

- **Milestone** M3 (structure, apply programs, re-application, re-resolution, liveness, street lights, road-choice reconcile, route path events and `RouteReadyBridgeSystem`), M4 (`RouteGuardSystem`), M5b (event synthesis for progression), M5c (inspection), M6 (traffic-light derivation) · **Assembly** `SkylineTogether` · **Folder** `src/SkylineTogether/Client` · **Depends on** core-contracts, core-replication, game-bootstrap, game-ecs-schema
- **Responsibility**: the single client apply point and the shared applier (ARCH §4.7): `ClientApplySystem` (gate check hook, stage-A hook, plan intake, `ApplyPlanner` call, channels, inspection, freeze while `Faulted`), `WorldApplier` (executor of `ApplyProgram`s: staging creation and finalisation, upserts and replacements, decode-then-commit writes through `ChunkAccess`, presence-safe stand-ins, deferred fields handed to their sink, received values recorded for the cache, pending references, tags, net closure, street-light derivation, synthesised events incl. route `PathUpdated`; also used by the host to commit proposals and by rollbacks), `ClientEndOfFrameSystem` (end-of-frame re-application of spawn payloads, re-resolution of references into regenerated derived children, liveness unbind, end-of-frame hooks), aggregate reconciliation, **road-choice reconciliation** (ARCH §4.11.5), **`RouteReadyBridgeSystem`** and the **route guard** (ARCH §4.11.3-4.11.4), traffic-light derivation, zone overlays and keyed state, event synthesis, time-aligned inspection, the UI-update apply slot for names, and the in-game apply conformance test.

**Public contracts**

```csharp
namespace SkylineTogether.Client
{
    public sealed class ClientApplyContext { public EntityManager EntityManager; public IClientReplicationEngine Engine; public TypeCatalogService Types; public EntityMap Entities;
                                             public ApplyPlan Plan; public uint ClientTick; public double Now; }
    public interface IClientChannel { SectionType Section { get; } void Apply(ClientApplyContext context, in ChannelOp op); }     // game-state-channels, game-fields
    public interface IPreApplyCapture { void CaptureBeforeApply(ClientApplyContext context); }                                   // stage A (game-client-actions)
    public interface IApplyObserver { void OnApplied(ClientApplyContext context, in ApplyResult result); }                       // integrity ledger, motion spawn hooks
    public struct ApplyStats { public double Milliseconds; public int Spawns, Despawns, Structs, Values, Touches, Parked, Failed; }
    public sealed class ClientApplySystem : SkylineSystemBase {                                // after EndFrameBarrier (client role)
        public void RegisterChannel(IClientChannel channel); public void RegisterPreApplyCapture(IPreApplyCapture capture); public void RegisterObserver(IApplyObserver observer);
        public ApplyStats LastStats { get; } public bool WasWrittenThisFrame(int entityIndex); public bool Frozen { get; }   // Faulted → no apply until the resync (ARCH §12.2)
    }
    public sealed class TagRules { public TagRules(ITypeCatalog catalog, Core.Replication.PolicyTable policies); public TouchFlags ForValueChange(TypeRank type, Family family); public bool TriggersNetClosure(TypeRank type, Family family); }
    public sealed class ClosureTagger { public void TagEdge(Entity edge); public void TagNode(Entity node); public void Flush(EntityManager entityManager); }   // ARCH §4.7.7
    public sealed class WorldApplier : IWorldApplier, IApplyWorldView {
        public WorldApplier(EntityManager entityManager, TypeCatalogService types, EntityMap entities, ArchetypeResolver archetypes, TagRules tags,
                            IClientReplicationEngine engineOrNullOnHost, ISlotTable slots, StreetLightDerive streetLights, ReapplyStash reapply,
                            IDeferredFieldSink deferredFields, EventSynthesizer events, ILog log);   // engine: RecordReceived after each applied value (client)
        public event Action<NetSlot, Entity> EntityCreated; public event Action<NetSlot, Entity> EntityDeleted;
        public bool WasWritten(int entityIndex); public void BeginFrame();
        // + IWorldApplier.Execute(ApplyProgram): every op through ChunkAccess (Exists, Has); CreateGroup from staging archetypes; Finalize adds Created/Updated(/Applied);
        //   on an exception: MarkDeleted for the program's creations, ApplyResult.Faulted (ARCH §4.7.2-4.7.4)
        // + IApplyWorldView over the client's side tables and liveness
    }
    public sealed class ReapplyStash {                                                        // ARCH §4.7.8: replaces the kept-initializer stash
        public void Keep(NetSlot slot, Entity entity, ApplyPlan plan, int firstComponent, int componentCount);   // spawn/upsert payloads of this frame
        public void MarkRehash(NetSlot slot);                                                // STRUCT/TOUCH without full payload, upgrade owners
        public bool IsPending(NetSlot slot, TypeRank type);
        public void ReapplyAndVerify(FrameContext context, IHashLedger ledger, Action<NetSlot, TypeRank> immediateLeak);   // run by ClientEndOfFrameSystem
    }
    public sealed class StreetLightDerive { public void NoteLightFlagsChanged(Entity owner); public void Apply(EntityManager entityManager); }   // ARCH §6.4
    public sealed class ClientEndOfFrameSystem : SkylineSystemBase {                          // before PrepareCleanUpSystem (moved here from game-client-actions)
        public void RegisterHook(Bootstrap.Hooks.IEndOfFrameHook hook);                     // order: re-application, re-resolution (TryBuildReresolutionPlan → WorldApplier),
                                                                                            // liveness unbind, hooks (stage C2/D, rolling verification)
        public int UnboundThisFrame { get; } public int ReresolvedThisFrame { get; }
    }
    public sealed class RouteReadyBridgeSystem : SkylineSystemBase { }                        // Mod1, after the (disabled) RoutePathReadySystem: ready positions from PathUpdated
                                                                                            // events and after loads; no icons, no HiddenRoute, no Efficiency (ARCH §4.11.3)
    public sealed class RoadConnectionReconcileSystem : SkylineSystemBase, IDeferredFieldSink {   // Mod4B, after RoadConnectionSystem (ARCH §4.11.5)
        public int ReconciledLastFrame { get; }                                             // moves ConnectedBuilding entries, writes the host's fields,
    }                                                                                       // emits RoadConnectionUpdated via ModificationBarrier4B; no Updated, no icon
    public sealed class RouteGuardSystem : SkylineSystemBase {                                // MainLoop after PathfindResultSystem; only while a preview window is open or lingering
        public int RestoredLastFrame { get; } public bool SelfTestPassed { get; }           // Core GuardPlanner over guarded route-family chunks; restore = decode-then-commit
        // reports its health to IToolScopedGateState; uses ILocalEditState (absent before M4 → every tracked entity is protected)
    }
    public sealed class RouteGuardSelfTest : ISelfTest { }                                    // ARCH §13.4 (n): scratch line, changed Connected/PathInformation restored
    public sealed class ApplyConformanceTest : ISelfTest { }                                  // fixed programs on scratch entities vs the FakeWorld's expected results
    public sealed class AggregateReconcileSystem : SkylineSystemBase { public void Expect(NetSlot aggregate, IReadOnlyList<NetSlot> orderedEdges); }   // after AggregateSystem (Mod2B)
    public sealed class TrafficLightDeriveSystem : SkylineSystemBase { public void NoteChanged(Entity node); }    // after TrafficLightInitializationSystem (Mod4B), ARCH §4.9.9
    public sealed class ZoneOverlayApplier { public void Enqueue(in DerivedKey block, ushort revision, ushort[] zoneRanks, double now); public void ApplyReady(ClientApplyContext context); public int Deferred { get; } }
    public sealed class KeyedStateApplier { public void Enqueue(in KeyedOp op, ApplyPlan plan, double now); public void ApplyReady(ClientApplyContext context); }
    public sealed class EventSynthesizer {
        public void Unlock(Entity prefab); public void MilestoneReached(int fromLevel, int toLevel); public void RentersUpdated(Entity property);
        public void ColorUpdated(Entity route); public void SubObjectsUpdated(Entity owner);
        public void PathUpdated(Entity segment);                                            // positions of the segment's two waypoints (ARCH §4.11.3)
        public void Flush(EntityManager entityManager);
    }
    public sealed class InspectionApplier { public void Enqueue(in InspectionBatch batch, ApplyPlan plan); public void ApplyDue(ClientApplyContext context, uint clientTick); }   // + SelectedInfoUISystem.RequestUpdate
    public sealed class ClientUiApplySystem : SkylineSystemBase { public void EnqueueName(in NetRef target, string name); public void EnqueueCityName(string name); }   // UIUpdate
}
```

- **Wave 1 note (from core-codec)**: open the reader with `CanonicalReader.Open(buffer, offset, length)` and check `IsRecordConsumed` after `Deserialize` (it also catches a block left open); the ARCH §4.7.4 sample is updated accordingly.
- **Game APIs and research**: `EntityManager.CreateEntity(archetype, n, allocator)` (`EntityManager.cs:2053` [V]), `SetSharedComponent(NativeArray<Entity>, T)` (`:1190` [V]), `AddComponent`/`RemoveComponent(NativeArray<Entity>, ComponentTypeSet)` (`:1544-1593` [V]), `GetStorageInfo` (`:1386` [V]); RW dynamic handles and `GetDynamicComponentDataArrayReinterpret` (`ArchetypeChunk.cs:730` [V]), `UnsafeUntypedBufferAccessor.ResizeUninitialized` (R08 §7), `SetComponentEnabled` (`ArchetypeChunk.cs:441` [V]); tag semantics (R11 §6.3, R15 §4.4); `ToolFeedbackSystem` applied-query (`ToolFeedbackSystem.cs:1302-1318` [V]) and LOD fading (`BatchInstanceSystem.cs:379, 425` [V]) behind the no-`Applied` rule; `AggregateSystem`/`Aggregated` (R13 §7.1); `TrafficLightInitializationSystem` (`SystemOrder.cs:182` [V]) and `TrafficLightSystem.UpdateLaneSignal/UpdateTrafficLightState/UpdateMoveableBridge` (`TrafficLightSystem.cs:441-452, 782, 878, 977` [V]); event archetypes `Event+Unlock`, `Event+MilestoneReachedEvent` (R14 §1.5, §2.4), `Event+RentersUpdated` (R13 §3.1), `Event+ColorUpdated` (R10 §3.5), `Event+SubObjectsUpdated` (R15 §2.6); `ApplyZonesSystem` cell writes (R13 §4.2); `SelectedInfoUISystem.RequestUpdate` (`SelectedInfoUISystem.cs:343` [V]); `NameSystem.SetCustomName` (`NameSystem.cs:214` [V]); `Game.Net.InitializeSystem`, `WaterSourceInitializeSystem`, `Creatures.InitializeSystem` overwrites (R11 §6.4, R15 §4.3) and the double-applying `BatteryInitializeSystem`/`CityServiceWorkplaceInitializeSystem` (`BatteryInitializeSystem.cs:56-90`, `CityServiceWorkplaceInitializeSystem.cs:103-120` [V]) behind the end-of-frame re-application; `EntityManager.Exists` vs `GetStorageInfo` (`EntityComponentStore.cs:660-672, 692-708` [V]) and `ArchetypeChunk.Has(ref DynamicComponentTypeHandle)` (`ArchetypeChunk.cs:581` [V]) behind `ChunkAccess`; `StreetLightSystem.UpdateStreetLightState` (`StreetLightSystem.cs:375-410` [V]) for `StreetLightDerive`; route presentation fed by `Event+PathUpdated` (`SegmentCurveSystem.cs:1453-1473`, `Game.Routes/SearchSystem.cs:77-80`, `Game.Rendering/RouteBufferSystem.cs:799-932` [V]) and the non-status branches of `RoutePathReadySystem` reproduced by `RouteReadyBridgeSystem` (`RoutePathReadySystem.cs:84-130, 385` [V]); the replace step of `RoadConnectionSystem` mirrored by the reconcile (`RoadConnectionSystem.cs:596-645`, `ConnectedBuilding`, `RoadConnectionUpdated`, `ModificationBarrier4B` [V]); `PathfindResultSystem` position (`SystemOrder.cs:65` [V]) for `RouteGuardSystem`.
- **Tests**: Core: the apply planning is core-replication's `ApplyPlanner`, run in every FakeWorld scenario; in game: `ApplyConformanceTest` (the executor yields the FakeWorld's results for upsert, replacement, dead binding, park, hold, finalise and failure programs); a forced exception in the middle of a program leaves no entity with `Created` and freezes the client; record/replay (M3) reproduces host structure exactly (class digest equal, `LineStateProbe` rows equal, no duplicate sub-nets after upgrade deletion, zone overlays applied after block regeneration, aggregates match, street lights switch at dusk with the host); a station upgrade on the host re-resolves the waypoints' `Connected` on the client (`ReresolvedThisFrame` > 0, line still connected); line curves are drawn after a synthesised `PathUpdated`; the road-choice reconcile count stays at 0 or names the buildings it corrected; spawned buildings of every family canonically equal after the creation frame; `RouteGuardSelfTest` green and host lines unchanged during a client's line edit (M4); apply stays within the 3 ms budget in the measurement report; traffic lights animate in M6; inspection values equal across two screens in M5c.
- **Done when**: M3 exit criteria pass on the client side (route guard: M4; inspection: M5c; traffic lights: M6).

### 7.20 game-state-channels

- **Milestone** M5b (flow status in M5c, radio in M9) · **Assembly** `SkylineTogether` · **Folder** `src/SkylineTogether/Channels` · **Depends on** core-contracts, core-replication (`IconCommandFilter`), core-fields, game-bootstrap, game-ecs-schema, game-host-capture, game-client-apply; M5c: core-scheduling (attention topics of the flow-status channel)
- **Responsibility**: the non-entity channels of ARCH §4.10.1-4.10.8 and §4.11.5, each with a host capture side (`IHostChannel`) and a client apply side (`IClientChannel`): system state (entries identified by stable string hashes; enabled set announced in `Welcome`; unknown or disabled entries skipped), budget arrays, milestone UI fields, prefab state with synthesised progression events (scan triggered by the sweep's `Unlock` flag), icons (owners and targets as `NetRef`s, derived keys included) with the client **icon authority filter** (ARCH §4.10.4), names, events (XP toasts, milestone hints, journal), cell maps, statistics scalars, the **flow-status channel** for the electricity and water infoviews (ARCH §4.11.5), and (M9) radio.

**Public contracts**

```csharp
namespace SkylineTogether.Channels
{
    public sealed class SystemStateChannel : IHostChannel, IClientChannel {
        public SystemStateChannel(IReadOnlyList<SystemStateEntryModel> entries, World world, ProbeRegistry probes, ILog log);
        public IReadOnlyList<string> EnabledEntries { get; } public IReadOnlyList<string> DisabledEntries { get; }
        public uint[] EnabledStableIds { get; }                                              // host: announced in Welcome
        public void ApplyHostEnabledSet(uint[] hostEnabled);                                 // client: entries the host enables but this client does not → "Degraded: <panel>"
        public bool RoundTripSelfTest(string entryId, out string detail);                  // serialise → deserialise into the live system → re-serialise (ARCH §4.10.1)
    }
    public sealed class BudgetArraysChannel : IHostChannel, IClientChannel { public BudgetArraysChannel(World world, ProbeRegistry probes, ILog log); public bool UsingFeed { get; } }   // P-BUDGET
    public sealed class MilestoneFieldsFeed { public MilestoneFieldsFeed(World world, ProbeRegistry probes); public bool IsAvailable { get; } public void Write(int lastRequired, int nextRequired, float progress, int nextMilestone); }   // P-MILESTONE
    public sealed class PrefabStateChannel : IHostChannel, IClientChannel { public PrefabStateChannel(PrefabRankMap prefabs, Client.EventSynthesizer events, ILog log); public ulong UnlockBitsetHash { get; } }
    public sealed class IconChannel : IHostChannel, IClientChannel { public IconChannel(World world, EntityMap entities, PrefabRankMap prefabs, ILog log); public ulong OwnerIconSetHash(in NetRef owner);
                                                                     public IReadOnlyList<int> ReplicationQueueIndices { get; } }   // indices in IconCommandSystem.m_Queues created this frame
    public sealed class IconAuthorityFilterSystem : SkylineSystemBase, Bootstrap.Hooks.IIconAuthority {   // clients; ModificationEnd, directly before IconCommandSystem (ARCH §4.10.4)
        public IconAuthorityFilterSystem(IconChannel icons, ProbeRegistry probes, ILog log);   // probe P-ICONQUEUES (m_Queues, m_Dependencies)
        public bool Active { get; }                                                          // false → fallback (pure producers gated, digest repair)
        public void AllowPrefabs(IEnumerable<string> notificationPrefabNames);              // adapters' localIconPrefabs
        public IReadOnlyDictionary<string, long> DroppedByPrefab { get; }                   // diagnostics: dropped commands per notification prefab
    }
    public sealed class IconAuthoritySelfTest : ISelfTest { }                                // ARCH §13.4 (m)
    public sealed class FlowStatusChannel : IHostChannel, IClientChannel {                   // M5c; ARCH §4.11.5; per peer, only with the Electricity/Water topic
        public FlowStatusChannel(World world, EntityMap entities, Core.Scheduling.AttentionSets attention, ILog log);   // flow keys (ARCH §3.7); keyframe on activation, deltas ≤ 1 Hz
        public ulong KeyHash(in DerivedKey flowEdgeKey);
    }
    public sealed class NameChannel : IHostChannel, IClientChannel { public NameChannel(World world, EntityMap entities, Client.ClientUiApplySystem uiApply, ILog log); }
    public sealed class EventChannel : IHostChannel, IClientChannel { public EventChannel(World world, ProbeRegistry probes, Client.EventSynthesizer events, ILog log); }
    public sealed class XpMessageTapSystem : SkylineSystemBase { }                                                  // host, after XPSystem (ModificationEnd), probe P-XP
    public sealed class JournalCompanion { public JournalCompanion(World world, ProbeRegistry probes); public bool IsAvailable { get; } public void Refresh(); }   // P-JOURNAL
    public sealed class CellMapChannel : IHostChannel, IClientChannel { public CellMapChannel(World world, Core.Fields.CellMapVersionRing ring, ILog log); public IReadOnlyList<string> Maps { get; } }
    public sealed class RadioChannel : IHostChannel, IClientChannel { }                                             // M9, P-RADIO
    public sealed class ChannelSelfTests : ISelfTest { }                                                            // round trips of every enabled system-state entry
}
```

- **Wave 1 note (from core-fields)**: call `CellMapVersionRing.SetExpectedLength` for every cell map on the client before applying deltas.
- **Game APIs and research**: `TaxSystem.Serialize/Deserialize`, `Readers` (R11 §5.2-5.3), demand systems, `TradeSystem`, `CityStatisticsSystem` (`Deserialize` only), `Count*` systems (R11 §5.2, R14 §10); `CityServiceBudgetSystem` arrays (`CityServiceBudgetSystem.cs:759-806`, [V-R] P-B); `MilestoneSystem` cached fields (`MilestoneSystem.cs:17-23, 41-51`); prefab `Locked` enabled bits, `PlacedSignatureBuildingData`, `UnlockRequirementData` (R14 §2-3); `IconCommandSystem.CreateCommandBuffer` (`IconCommandSystem.cs:727` [V]) and `IconCommandBuffer` add/remove (`IconCommandBuffer.cs:60-125`, R14 §4); the private queue list and writer handle of `IconCommandSystem` and its playback (`IconCommandSystem.cs:698-770` [V], probe `P-ICONQUEUES`), `IconCommandBuffer.Command` flags and layers (`IconCommandBuffer.cs:10-40` [V]), the local producers it filters (`RoadConnectionSystem.cs:611-620`, `BuildingPoliciesSystem.cs:83-90` [V]) and the ones it keeps (`ValidationSystem.cs:1518-1522`, `MarkerCreateSystem.cs:415-489`, `ToolFeedbackSystem.cs:936`, `IconDeletedSystem.cs:17-137` [V]); flow edges `ElectricityFlowEdge`/`WaterPipeEdge` and consumer flags (`Game.Simulation/ElectricityFlowEdge.cs`, `ElectricityFlowEdgeFlags.cs`, `WaterPipeEdge.cs`, `Game.Buildings/ElectricityConsumerFlags.cs`, `WaterConsumerFlags.cs` [V]); `NameSystem.Serialize` (`NameSystem.cs:549` [V]); `XPSystem` message queue and `MilestoneUISystem.AddMessage` (`MilestoneUISystem.cs:359` [V], R14 §1.4); `EventJournalSystem` (R14 §6.1); `CellMapSystem<T>.GetMap`/`AddWriter` (R07 §8.1); `ClimateSystem` weather state (R03 §6.7); `WaterSystem` parameters (R07 §6.2).
- **Tests**: Core: version ring and XOR deltas (core-fields), stable entry ids (core-contracts), `IconCommandFilter` rules (core-replication); in game: `ChannelSelfTests` and `IconAuthoritySelfTest` green; a client with a disabled entry skips its records and shows "Degraded"; M5b script (demand bars, budget panel, statistics, milestone popup, toolbar unlocks within a frame of the host's, icons, chirps and journal identical; ST-LINES warnings and status indicators identical; the dropped-command counters name the road-access and turned-off notification prefabs and nothing unexpected); M5c: electricity and water infoview colours identical (flow-status keys hashed equal); relock path verified with a debug command.
- **Done when**: M5b exit criteria pass (flow-status channel: M5c).

### 7.21 game-client-actions

- **Milestone** M4 · **Assembly** `SkylineTogether` · **Folder** `src/SkylineTogether/Actions` · **Depends on** core-contracts, core-intents, core-identity, game-bootstrap, game-ecs-schema, game-client-apply, game-host-capture
- **Responsibility**: the client action path and its host side (ARCH §5): capture stages A, B (original set incl. owned sub-elements, terrain stamps with frame time), C (candidates: `Applied` ∪ changed original-set members ∪ updated blocks of recorded originals ∪ entities created in the apply frame), C2 and D, proposal building and sending (parts for large change sets), the no-predict gate, held payloads, the receipt echo, timeouts with tombstones and rollback on the client; validation (incl. prefab class and geometry), commit through the shared applier inside the capture window (ingress hook), semantic executors (incl. `ReplaceObjectPrefab`) and the host-only side effects of every vanilla apply system on the host (incl. re-linked waypoints); the local-edit state the route guard needs (ARCH §4.11.4).

**Public contracts**

```csharp
namespace SkylineTogether.Actions
{
    public struct OriginalRecord { public NetSlot Slot; public ushort Revision; public uint TempFlags; public TypeSetId Signature; public bool IsSubElement; }
    public sealed class ToolActionHeader { public ushort ToolKind; public string ToolTypeName; public PrefabRank Prefab; public byte Mode; public long CostSum;
                                           public List<OriginalRecord> OriginalSet; }    // Temp originals + their owned SubNet/SubArea/SubRoute/non-derived SubObject entries
    public sealed class ToolCaptureApplySystem : SkylineSystemBase {                           // first in ApplyTool; client: stage B; host: terrain stamps
        public ToolActionHeader LastHeader { get; } public event Action<IReadOnlyList<Core.Intents.TerrainStamp>, bool /*isHost*/> StampsCaptured;   // stamps carry ClampedDeltaTime
    }
    public sealed class ToolCaptureResultSystem : SkylineSystemBase { public int LastCandidates { get; } }   // first in Modification1: stage C (ARCH §5.2)
    public sealed class EndOfFrameCapture : Bootstrap.Hooks.IEndOfFrameHook { }                // stage C2 and D, registered with game-client-apply's ClientEndOfFrameSystem
    public sealed class UiEffectCapture : Client.IPreApplyCapture { public UiEffectCapture(ProposalSender sender, Core.Intents.ProposalTracker tracker, EntityMap entities, World world, ILog log); }   // stage A
    public sealed class LocalEditCapture : Bootstrap.Hooks.ILocalEditRegistry { public bool WindowOpen { get; } }
    public sealed class ProposalSender { public ProposalSender(SessionRuntime runtime, Core.Intents.ProposalTracker tracker, ILog log);
                                         public void Send(Core.Intents.Proposal proposal, Core.Intents.RollbackPlan rollback, IEnumerable<(NetSlot, TypeRank)> covered); }   // parts when large
    public sealed class ProposalResultHandler { public void OnReceivedEcho(uint seq); public void OnHostBusy(bool busy); public void OnResults(ApplyPlan plan); }   // tracker glue, tombstones
    public sealed class RollbackExecutor { public RollbackExecutor(Client.WorldApplier applier, EntityMap entities, SessionRuntime runtime); public void Execute(Core.Intents.RollbackPlan plan, RejectReason reason); }
    public sealed class NoPredictGateSystem : SkylineSystemBase { public bool GateActive { get; set; } }      // before ToolOutputSystem, probe P-APPLYMODE
    public sealed class HostIngress : Host.IIngressHook {                                      // not a system: called inside the capture window (ARCH §4.4.2 step 9, §5.4.2)
        [ThreadAffinity(ThreadKind.Network)] public void OnProposalPart(PeerId from, byte[] buffer, int offset, int count);   // assembles parts, echoes ProposalReceived at once
        public Core.Intents.ValidationFacts BuildFacts(Core.Intents.Proposal proposal);      // incl. PrefabClassOf, CheckGeometry, presence-only authorable types
        // + IIngressHook.CommitInsideWindow: validate → WorldApplier (MergeAuthorable for masked types) → HostSideEffects → BumpRevision → EmitResult (Origin)
    }
    public sealed class HostCommandSystem : SkylineSystemBase { public void Enqueue(PeerId from, uint proposalSeq, Core.Intents.SemanticIntent intent); }   // UIUpdate
    public sealed class SemanticExecutors { public RejectReason Execute(Core.Intents.SemanticIntent intent, PeerId from); }   // incl. ReplaceObjectPrefab
    public sealed class HostSideEffects {                                                      // ARCH §5.4.3, one method per vanilla apply-only effect
        public void ChargeMoney(long cost); public void SetRecent(Entity entity, int cost);   // vanilla refund arithmetic
        public void QueueObjectCreatedTrigger(Entity entity); public void QueueBicycleRoadBanTrigger();
        public void RebaseBuildOrders(IList<(Entity edge, uint start, uint end)> edges);      // uses Identity.BuildOrderAllocator
        public void EmitPathTargetMoved(Entity original, float3 oldPosition, float3 newPosition);   // moved Building/TransportStop, moved route Waypoint
        public void MarkMovedMover(Entity mover);                                            // current-lane Obsolete flag, Moving reset
        public void FixParkingLocation(Entity parkedVehicle);                                // parked cars/trains in a committed relocation
        public void ReplaceObjectPrefab(Entity original, PrefabRank newPrefab);              // upgrade/rebuild path: Destroyed/Damaged, RescueTarget, Abandoned,
                                                                                             // PropertyToBeOnMarket, mesh colours, RemoveOldSubItems, Updated
        public void DetachKeptSubNets(Entity deletedObject, IReadOnlyList<Entity> keptSubNets);   // Owner removed + Updated
        public void RelinkWaypoint(Entity waypoint, Entity oldStopOrNull, Entity newStopOrNull);   // ConnectedRoute bookkeeping of ApplyRoutesSystem (ARCH §5.4.3)
    }
    public sealed class LocalEditState : Bootstrap.Hooks.ILocalEditState { }                   // stage-B original set (+ owned sub-elements), predicted creations,
                                                                                             // pairs covered by pending proposals; read by RouteGuardSystem
}
```

- **Game APIs and research**: `ToolSystem.activeTool`/`applyMode`, `ToolBaseSystem.applyMode` protected setter (`ToolBaseSystem.cs:182`, probe `P-APPLYMODE`), `ToolOutputSystem`, the ApplyTool phase and capture points IP2/IP3 (R10 §7.2); `Temp`, `TempFlags`, `Temp.m_Cost` and the cost formula (`ToolApplySystem.cs:42-74`); `Brush` stamps (R07 §2.2); `PoliciesUISystem.SetPolicy` (`PoliciesUISystem.cs:186` [V]) and `Event+Modify` (`:237-242`); `TaxSystem.GetTaxRates/SetTaxRate/SetResidentialTaxRate` (`TaxSystem.cs:363, 482, 684` [V]); `CityServiceBudgetSystem.SetServiceBudget` (`:1084` [V]); `ServiceFeeSystem.SetFee` (`:508` [V]); `LoanSystem.ChangeLoan` (`Game.Tools/LoanSystem.cs:130` [V]); `DevTreeSystem.Purchase(Entity)` (`Game.City/DevTreeSystem.cs:157` [V]); `MapTilePurchaseSystem.GetAvailableTiles`/`UnlockTile` (`:255, :346` [V]); `PlayerMoney.Subtract` (`Game.City/PlayerMoney.cs:38` [V]); `LifePathEventSystem.FollowCitizen/UnfollowCitizen` (R10 F1); `TerrainSystem.ApplyBrush` (`TerrainSystem.cs:3968`); `ParkedVehiclesSystem` query (`ParkedVehiclesSystem.cs:1683-1700`); `Recent` (`Game.Tools/Recent.cs` [V]); `TriggerSystem.CreateActionBuffer` (`TriggerSystem.cs:405` [V]); `BuildOrder` counter derivation (`GenerateEdgesSystem.cs:1925-1941` [V]); `OriginalDeletedSystem` (R10 §7.3); `SafeCommandBufferSystem` window (R10 §0.4); `ApplyZonesSystem` (`ApplyZonesSystem.cs:104-140`); tags of the vanilla apply systems (`ApplyNetSystem.cs:901-903`, `ApplyObjectsSystem.cs:595`, `ApplyAreasSystem.cs:175`, `ApplyRoutesSystem.cs:317`, `ApplyZonesSystem.cs:106` [V]) and their apply-only effects (`ApplyObjectsSystem.cs:363-505, 561-663, 923`, `ApplyRoutesSystem.cs:21-72, 322-346`, `ApplyNetSystem.cs:629-687` [V]); `RouteToolSystem` (the tool whose preview window game-follower opens).
- **Tests**: Core: core-intents validator, tracker (receipt echo, `HostBusy`, tombstones) and proposal parts; in game: one test per apply system with the leak tracker at 0 (objects: relocate, upgrade, rebuild; areas: node edit; routes: waypoint move; zoning); a relocated service building with vehicles en route makes them re-path on the host; a destroyed building rebuilt from a client loses `Destroyed` on every peer; a client re-routes a line from one stop to another and both stops' line lists are right on every peer; a client draws a line inside the preview window (vanilla preview, `PathfindFailed` validation) and the host's other lines stay unchanged on the client (guard counters); M4 script (every tool and every UI action from a client; conflicts; rejection toasts; rollback leaves no trace; a proposal during a host snapshot is not rolled back; Move It and Tree Controller brush from a client; write auditor clean during normal play).
- **Done when**: M4 exit criteria pass.

### 7.22 game-motion

- **Milestone** M6 · **Assembly** `SkylineTogether` · **Folder** `src/SkylineTogether/Motion` · **Depends on** core-contracts, core-motion, core-scheduling, game-bootstrap, game-ecs-schema, game-host-capture, game-client-apply, game-follower
- **Responsibility**: moving objects in game (ARCH §4.9): the host motion sampler (pointer reads, union-of-interest prefilter, discrete-state change detection that marks every peer dirty, keyframe/delta choice, interest entry/exit with assignment generations and acknowledged reuse), interest management per peer and the reset on short-drop resume (`IHostReplicationEngine.PeerResumed`), the client JIT slot writer with extrapolation, pinning, pose refreshes, `Transform`/`Moving` coherence, effect and prop side effects, train bogie frames, and `LaneObject` maintenance.

**Public contracts**

```csharp
namespace SkylineTogether.Motion
{
    public sealed class InterestManager { public InterestManager(Core.Scheduling.AttentionSets attention, Core.Motion.InterestGrid unionGrid); public void Update(double now); public bool InUnion(float x, float z); }
    public sealed class MotionSampler : Host.IMotionSampler {                                 // host, inside the capture flush
        public MotionSampler(Core.Motion.MotionHostEncoder encoder, InterestManager interest, Core.Scheduling.AttentionSets attention, ILog log);
        public double LastMilliseconds { get; } public int LastFramesSampled { get; } public int LastKeyframes { get; }
        public void OnPeerResumed(PeerId peer);                                              // MotionHostEncoder.ResetPeer + re-entry of the current interest (ARCH §7.8)
    }
    public sealed class MotionWriterSystem : SkylineSystemBase {                              // PreCulling, before PreCullingSystem
        public Core.Motion.MotionClientStager Stager { get; } public int Extrapolated { get; } public int Pinned { get; }
        public void OnMoverSpawned(NetSlot slot, Entity entity); public void OnMoverDespawned(NetSlot slot);
        public void ApplyPose(NetSlot slot, Entity entity, byte[] payload, int offset, int count);   // POSE records (re-pin)
    }
    public sealed class MoverLaneSync { public MoverLaneSync(EntityManager entityManager); public void OnLaneChanged(Entity mover, Entity oldLane, Entity newLane, float2 curvePosition); }
    public sealed class MotionSelfTest : ISelfTest { }                                         // slot planner sanity against live UpdateFrame/render ticks
}
```

- **Game APIs and research**: `EntityQuery.SetSharedComponentFilter(new UpdateFrame(T & 15))` (P-C §3.7.4) as movers slice (`CarMoveSystem.cs:420-423`, R15 §7.2); `TransformFrame` slots and validity (R15 §2.1-2.3); `RenderingSystem.frameIndex/frameTime` and the `PreRenderSystem` order (`PreRenderSystem.cs:20-23` [V], `RenderingSystem.cs:173-221` [V]); reader maths (`ObjectInterpolateSystem.cs:4332-4360`, R15 §2.2); `Transform` slack in culling (`PreCullingSystem.cs:1152-1171`); `EffectsUpdated` rules (R15 §3.5); `Event+SubObjectsUpdated` and activity props (R15 §2.6); `TrainBogieFrame`/`TrainCurrentLane` (R15 §3.2); `NetUtils.AddLaneObject/UpdateLaneObject/RemoveLaneObject` (`Game.Net/NetUtils.cs:439, 452, 494` [V]); `Vehicles.ReferencesSystem` lane guard (`Game.Vehicles/ReferencesSystem.cs:61-69` [V]); `UpdateGroupSystem` frame reset on created movers (R15 §3.1).
- **Tests**: Core: core-motion maths, codecs under loss and reordering, generations and acknowledged reuse; in game: `MotionSelfTest`; M6 script at speeds 1 and 4 with Steam fake lag 100-150 ms and fake loss 5 % (no backward snaps, no drifting movers, lights, sirens, boarding, follow camera, click a car, pan away and back: pinned then refreshed), dropped-record counters plausible, motion milliseconds within budget in the measurement report; M7: movers keep moving after a short-drop resume.
- **Done when**: M6 exit criteria pass.

### 7.23 game-integrity

- **Milestone** M3 (record/replay, class digest command, `LineStateProbe` and `/st lines`, load-parity comparison, cosmetic write audit), M5a (shadow client), M7 (everything else) · **Assembly** `SkylineTogether` · **Folder** `src/SkylineTogether/Integrity` · **Depends on** core-contracts, core-session (`SessionController`), core-integrity, core-replication, game-bootstrap, game-ecs-schema, game-follower, game-host-capture, game-client-apply, game-join; M7: core-intents
- **Responsibility**: integrity in game (ARCH §8, §13.3-13.5): host beacon enabling after full scan cycles, client ledger maintenance (received values) and rolling live verification that skips pending pairs, repair request/response wiring with host-decided escalation and domain rebaselines over bulk, the write auditor (incl. the cosmetic write audit, ARCH §6.1), discovery mode, the shadow client, record/replay (incl. the load-parity census and line rows), the UI-level line digest (`LineStateProbe`, ARCH §4.11.6), and replica-diff dumps.

**Public contracts**

```csharp
namespace SkylineTogether.Integrity
{
    public sealed class IntegrityHost {
        public IntegrityHost(IHostReplicationEngine engine, Core.Integrity.BucketSums sums, Core.Integrity.BeaconPlanner beacons, Core.Integrity.HostRepairResponder responder, SessionController session);
        public void OnScanCycleCompleted(ReplicationClass cls, EpochId startedAfter); public void OnPeerJoined(PeerId peer, EpochId snapshotEpoch);
        public void OnRepairRequest(PeerId peer, Core.Protocol.RepairRequestMessage request);   // responder decides; Throttled → ControlKind.RepairThrottled, never a kick
    }
    public sealed class PendingPairs : Core.Integrity.IPendingPairs {                          // parked and awaiting re-resolution (engine), held (tracker), re-application (ReapplyStash), proposals
        public PendingPairs(IClientReplicationEngine engine, Client.ReapplyStash reapply, IHoldFilter holdsOrNull);
    }
    public sealed class IntegrityClient : Client.IApplyObserver, Bootstrap.Hooks.IEndOfFrameHook {
        public IntegrityClient(Core.Integrity.HashLedger ledger, Core.Integrity.RepairCoordinator coordinator, SessionController session, PendingPairs pending);
        public Core.Integrity.RepairStats Stats { get; }
    }
    public sealed class RollingVerifier { public RollingVerifier(TypeCatalogService types, EntityMap entities, Core.Integrity.HashLedger ledger, Core.Integrity.IPendingPairs pending);
                                          public void RunSlice(Bootstrap.Hooks.FrameContext context, double budgetMs); }   // skips pending and tolerated pairs
    public sealed class WriteAuditor { public bool Enabled { get; set; } public void RecordAfterApply(EntityManager entityManager); public void CheckAfterModification(World world); public IReadOnlyList<string> Fighters { get; }
                                       public IReadOnlyList<string> AuditCosmetic(World world, Follower.GateController gates); }   // runs each cosmetic system once; offenders removed (ARCH §6.1)
    public sealed class LoadParityCheck { public IReadOnlyList<string> Compare(byte[] hostCensus, World world); }   // ARCH §7.6; host census travels with the manifest in diagnostics mode
    public sealed class DiscoveryMode { public bool Enabled { get; set; } public IReadOnlyList<string> Findings { get; } }
    public sealed class ShadowClient { public bool Enabled { get; set; } public int Mismatches { get; } public IReadOnlyList<string> MismatchSamples { get; } }   // host self-test (ARCH §13.5)
    public sealed class RecordReplay { public void StartRecording(string folder); public void StopRecording(); public void StartReplay(string folder); public bool Replaying { get; } public string Verdict { get; } }
    public static class ReplicaDiffDump { public static string Dump(NetSlot ownerOrBucketStart, int count); }
    public sealed class ClassDigestCommand { }                                                 // "/st digest S|G|V|A|M" debug command (M3)
    public sealed class LineStateProbe {                                                       // ARCH §4.11.6; uses the vanilla UI functions themselves
        public LineStateProbe(World world, PrefabSystem prefabs, EntityMap entities, bool iconsCompared);
        public IReadOnlyList<Core.Integrity.LineStateRow> Compute(uint sampleTick);          // TransportUIUtils.BuildTransportLine, broken flags from segment PathInformation,
                                                                                             // non-Marker icon prefabs of the line and of its waypoints, TransportLine/Route flags
    }
    public sealed class LineStateCommand { }                                                   // "/st lines" (own rows) and "/st lines compare" (host rows at one tick → differences)
    public sealed class LineStateSelfTest : ISelfTest { }                                      // ARCH §13.4 (o)
}
```

- **Game APIs and research**: chunk change versions and `ComponentSystemBase.LastSystemVersion` for naming writers (R08 §3); `EntityManager.Exists`; the client load path of game-join for replay (R12 §4); single-instance limitation (R06 §1); the line UI's own functions and data (`TransportUIUtils.cs:41-140`, `LineVisualizerSection.cs:580-585`, `NotificationsSection.cs:111-121, 354-363` [V]) for `LineStateProbe`.
- **Tests**: Core: core-integrity (incl. `LineStateReport`); in game: M3 record/replay verdict "equal" (class digests and line rows) and load-parity comparison clean; ST-LINES steps 1-2 with `/st lines compare` clean on two PCs (M3), icon fields included from M5b; cosmetic write audit clean on the default list and catching a deliberately added `StreetLightSystem` entry; M7 script (debug commands that flip a value, delete an entity, add an extra entity on a client: all repaired without reload; parked references do not cause repair requests; cable pull resume; forced structural exception → freeze and resync); shadow client shows zero mismatches; write auditor names a deliberately ungated test system.
- **Done when**: M7 exit criteria pass.

### 7.24 game-fields

- **Milestone** M8 · **Assembly** `SkylineTogether` · **Folder** `src/SkylineTogether/Fields` · **Depends on** core-contracts, core-session (`WaterQuality`), core-fields, core-intents, game-bootstrap, game-client-apply, game-host-capture, game-client-actions
- **Responsibility**: GPU-backed fields (ARCH §4.10.9-4.10.10): terrain stamps (host capture, client stroke execution through the public `ApplyBrush` with the brush strength pre-scaled by core-fields' `BrushStrengthCompensation` from the stamp's `ClampedDeltaTime`, forwarding for smooth visuals with the same compensation on every replaying client), settle detection, async readback of dirty heightmap tiles, tile application on clients, tile-hash audits, the splatmap, water parameters and water correction tiles.

**Public contracts**

```csharp
namespace SkylineTogether.Fields
{
    public sealed class TerrainChannel : IHostChannel, IClientChannel {
        public TerrainChannel(World world, Core.Fields.StrokeSettleDetector settle, ILog log);
        public void OnHostStamps(IReadOnlyList<Core.Intents.TerrainStamp> stamps); public void ExecuteClientStroke(PeerId from, IReadOnlyList<Core.Intents.TerrainStamp> stamps);
        public void StartAudit(); public int PendingReadbacks { get; }
    }
    public sealed class SplatmapChannel : IHostChannel, IClientChannel { }
    public sealed class WaterChannel : IHostChannel, IClientChannel { public WaterChannel(World world, ProbeRegistry probes, ILog log); public Core.Session.WaterQuality Quality { get; set; } public bool WritePathAvailable { get; } }   // P-WATER
    public sealed class FieldSelfTests : ISelfTest { }                                        // readback → encode → decode → CopyTexture round trip on a scratch texture;
                                                                                              // one compensated stamp at a forced 15 fps and 60 fps gives the same height delta
}
```

- **Game APIs and research**: `TerrainSystem.heightmap`, `OnAreaChanged`, `UpdateMinMax`, `ApplyBrush` (`TerrainSystem.cs:3968`) and its frame-time scaling and strength mapping (`TerrainSystem.cs:3766-3775, 3968-3979` [V]), `Brush.m_Tool`/`m_Opacity` (`Game.Tools/Brush.cs` [V]), `WaterSystem.TerrainWillChangeFromBrush` (R07 §5.2); `TerrainMaterialSystem.ApplyBrush` (`:815`, R07 §5.3); `WaterSystem.WaterTexture` and public parameters (R07 §6.2, §6.7); `AsyncGPUReadback`, `Graphics.CopyTexture`, `Texture2D` (UnityEngine.CoreModule); the 1-LSB GPU caveat for hashes (R07 §5.2).
- **Tests**: Core: core-fields codecs; in game: `FieldSelfTests`; M8 script (terraform from host and from client; tile hashes equal after settle; water parameters equal; water visually close with quality Low and Medium).
- **Done when**: M8 terrain and water criteria pass.

### 7.25 game-compat

- **Milestone** M1 (manifest provider, adapter loading; research task R-MODS), M8 (all adapters, settings sync, audits) · **Assembly** `SkylineTogether` · **Folder** `src/SkylineTogether/Compat` · **Depends on** core-contracts, core-session, game-bootstrap (adapter targets through the hook interfaces `IGateRegistry`, `ILocalEditRegistry`, `ILoadOverrides`, `ITypeRuleRegistry`, `IIconAuthority`)
- **Responsibility**: mod parity and adapters (ARCH §10): the mod manifest for the handshake (names, versions, PDX ids, DLL hashes, local-only eligibility), adapter loading (embedded + override) and application to gates, tool-scoped gates, cosmetic list, join-time mutators, policies, local types, direct-write tools, authorable types, host-only actions and local icon prefabs through the hook interfaces (implementations that a milestone does not have yet are skipped and applied when they register); simulation-relevant settings sync (collect, compare, push, restore); the Harmony audit; the "subscribe to missing mods" action; and the R-MODS deliverable: the analysis of the installed Traffic and Road Speed Adjuster DLLs (tool systems and their phases relative to capture stages B/C/C2, serializable types with `Entity` fields, systems for `clientOff`/`clientKeep`, settings) turned into their adapters (ARCH §10.4).

**Public contracts**

```csharp
namespace SkylineTogether.Compat
{
    public sealed class ModManifestProvider : IManifestContributor { public ModManifestProvider(ILog log); public IReadOnlyList<ModEntry> Enumerate(); }
    public sealed class AdapterRegistry {
        public static AdapterRegistry Load(ModPaths paths, ILog log);                          // adapters.json (embedded) + Data/adapters.json override
        public IReadOnlyList<AdapterModel> All { get; } public IReadOnlyList<AdapterModel> Present(IReadOnlyList<ModEntry> loadedMods);
        public IReadOnlyList<string> Problems { get; }                                         // unresolved type names (warnings)
    }
    public sealed class AdapterApplier {
        public AdapterApplier(ServiceRegistry services, ILog log);
        public void Apply(IReadOnlyList<AdapterModel> present, PolicyTableModel policies);    // pushes entries into every registered hook; re-applies when a hook registers later
        public IReadOnlyList<string> HostOnlyActions { get; } public IReadOnlyList<string> ResendAfterJoinTypes { get; } public IReadOnlyList<string> SkippedTargets { get; }
    }
    public sealed class SessionSettingsSync : IManifestContributor {                            // probe P-MODSETTINGS
        public SessionSettingsSync(ProbeRegistry probes, ILog log);
        public IReadOnlyList<SettingEntry> Collect(IReadOnlyList<AdapterModel> present);
        public void Push(IReadOnlyList<SettingEntry> hostValues); public void Restore();
    }
    public sealed class HarmonyAudit { public IReadOnlyList<string> FindPatchesOnCriticalSystems(); }   // diagnostics only, reflection, no Harmony shipped
    public sealed class MissingModSubscriber { public void Subscribe(IReadOnlyList<string> platformIds, Action<bool, string> onDone); }   // explicit user action (R06 §8)
}
```

- **Game APIs and research**: `ModManager` enumeration, `ListModsEnabled`, `ExecutableAsset` version/platform id/path (R05 §1.5); `ModSetting.instances` (internal static, probe `P-MODSETTINGS`) and mod settings properties by name (R09 §5.8); `PdxSdkPlatform.SubscribeAll` (R06 §8); type lookup by name across loaded assemblies (no compile-time references, R05 §1.3); the user's mods (R09 §2, ARCH §10.4).
- **Tests**: Core: adapter JSON parsing (core-contracts config tests) with every shipped adapter; build: `ApiCheck` resolves every adapter type and system name against the installed mod DLLs; in game: M1 (manifest and data-file digests equal on matching setups, readable report on a deliberate mismatch, R-MODS write-up), M3 (record/replay with Traffic installed), M8 per-mod checklist (each listed mod used from host and client; Better Bulldozer records, Anarchy overrides, Tree Controller brush, Plop tags, Zoning Toolkit blocks, Water Features sources, 529 Tiles and Historical Start load parity, Traffic lane connections and priorities proposed from a client, Road Speed Adjuster speed edits proposed from a client).
- **Done when**: M8 mod checklist passes.

### 7.26 core-codec

- **Milestone** M2 · **Assembly** `SkylineTogether.Core` · **Folder** `src/SkylineTogether.Core/Codec` · **Depends on** core-contracts
- **Responsibility**: every canonical byte (ARCH §4.3.1, §4.3.4): the encoding of every member of the game's `IWriter`/`IReader` in Core types, in the stock little-endian layout, with `Begin`/`End` length blocks and `NetRef` entity references through a small seam; bounded readers. The game's `NetWriter`/`NetReader` (game-ecs-schema) forward one line per member, so the byte layout is defined and unit-tested here, not in game code.

**Public contracts**

```csharp
namespace SkylineTogether.Core.Codec
{
    public interface IEntityRefWriter { NetRef EncodeEntity(int index, int version, bool ignoreVersion); }      // game: EntityMap via EntityRefBridge
    public interface IEntityRefReader { (int index, int version) DecodeEntity(in NetRef value, out bool missing, out NetSlot missingSlot); }
    public struct BlockToken { public int Position; }
    public sealed class CanonicalWriter {                                                      // stock layout; one method per IWriter member family
        public CanonicalWriter(Buffers.ByteWriter target, IEntityRefWriter entities);
        public BlockToken Begin(); public bool End(BlockToken block); public bool End(BlockToken block, out int size);   // 4-byte length prefix
        public void WriteBool(bool v); public void WriteByte(byte v); public void WriteSByte(sbyte v); public void WriteShort(short v); public void WriteUShort(ushort v);
        public void WriteInt(int v); public void WriteUInt(uint v); public void WriteLong(long v); public void WriteULong(ulong v); public void WriteChar(char v);
        public void WriteFloat(float v); public void WriteDouble(double v); public void WriteString(string v);
        public void WriteFloat2(float x, float y); public void WriteFloat3(float x, float y, float z); public void WriteFloat4(float x, float y, float z, float w);
        public void WriteInt2(int x, int y); public void WriteInt3(int x, int y, int z); public void WriteInt4(int x, int y, int z, int w); public void WriteUInt4(uint x, uint y, uint z, uint w);
        public void WriteBool2(bool x, bool y); public void WriteBool3(bool x, bool y, bool z); public void WriteBool4(bool x, bool y, bool z, bool w);
        public void WriteQuaternion(float x, float y, float z, float w); public void WriteBezier4x3(float[] twelveFloats);
        public void WriteColor(float r, float g, float b, float a); public void WriteColor32(byte r, byte g, byte b, byte a); public void WriteHash128(uint a, uint b, uint c, uint d);
        public void WriteEntity(int index, int version); public void WriteEntityIgnoreVersion(int index);           // → NetRef
        public void WriteIntArray(int[] v, int count); public void WriteUShortArray(ushort[] v, int count); public void WriteByteArray(byte[] v, int count, int stride = 1);
        public void WriteFloat2Array(float[] xy, int count); public void WriteFloat4Array(float[] xyzw, int count); public void WriteInt2Array(int[] xy, int count);
        public void WriteEntityArray(int[] indices, int[] versions, int count); public void WriteCount(int count);   // arrays of ISerializable: count, then elements
    }
    public sealed class CanonicalReader {                                                      // mirror of CanonicalWriter; every overrun throws DecodeException
        public CanonicalReader(IEntityRefReader entities);
        public void Open(byte[] buffer, int offset, int length);                               // bounded to exactly one record
        public int Position { get; } public int Remaining { get; } public bool IsAtEnd { get; }
        public BlockToken Begin(); public bool End(BlockToken block);                          // End skips unread bytes of the block, like the stock reader
        // Read* counterparts of every Write* above, e.g. public float ReadFloat(); public void ReadFloat3(out float x, out float y, out float z); public bool ReadEntity(out int index, out int version);
        public bool HadMissingReference { get; } public NetSlot FirstMissing { get; }
    }
}
```

- **Wave 1 integration notes** (additive members; no frozen signature changed): writer `Retarget(ByteWriter)`, `WriteNetRef`, `Target`, `Entities`, `OpenBlockCount`, `ClearOpenBlocks`, constants `BlockHeaderBytes` and `Bezier4x3Floats`; reader `Begin(out int size)`, `Skip`, `Close`, `ReadNetRef`, `ReadEntityIgnoreVersion`, `ReadCount(int minElementBytes)`, `Length`, `RecordRemaining`, `IsRecordConsumed`, `OpenBlockCount`, `MissingReferenceCount`, `ReferenceCount`, `DerivedReferenceCount`. Blocks must close innermost first. A failed `ReadEntity` (including a seam exception) leaves the cursor and counters unchanged; `ReadEntityArray` is not atomic, so abandon the record on failure. Strided byte arrays use Core's own `ByteShuffle` transposition (canonical bytes are only read by `CanonicalReader`). Tests run on `net8.0` only in this build environment.
- **Game APIs**: none (pure .NET). The member list mirrors `Colossal.Serialization.Entities/IWriter.cs` and `IReader.cs` [V]; the stock layout of each member is taken from the game's own binary writer and reader (R01 §2).
- **Tests**: golden byte vectors for every member (including edge values: NaN payloads preserved, empty and non-ASCII strings, nested `Begin`/`End` blocks, skipped blocks); writer/reader round trips; a reader opened on a record never reads past it; `NetRef` encoding through fake entity seams; property test: random sequences of members round-trip; the test suite runs on `net8.0` and `net472`.
- **Done when**: game-ecs-schema's forwarders compile against it and the in-game `CodecSelfTest` is green.

### 7.27 core-follower

- **Milestone** M2 (`PreviewWindow` M4) · **Assembly** `SkylineTogether.Core` · **Folder** `src/SkylineTogether.Core/Follower` · **Depends on** core-contracts
- **Responsibility**: the decisions behind follower mode, as pure state machines and planners (ARCH §6): the gate planner (table + adapter entries + resolved systems → apply list, critical misses, producer/consumer violations, unclassified systems, the session-load set, the tool-scoped entries), the **preview window** state machine of the tool-scoped gates (ARCH §6.3, §4.11.4), the **load hand-over** state machine of ARCH §6.1, and the cosmetic schedule (interval and offset arithmetic of `UpdateSystem`). game-follower only resolves names, reads game state and flips `Enabled`.

**Public contracts**

```csharp
namespace SkylineTogether.Core.Follower
{
    public sealed class ResolvedSystem { public string TypeName; public string Phase; public bool Resolved; public bool CurrentlyEnabled; }
    public sealed class GateReport { public IReadOnlyList<string> Unresolved; public IReadOnlyList<string> CriticalUnresolved; public IReadOnlyList<string> ProducerConsumerViolations;
                                     public IReadOnlyList<string> Unclassified; public bool Ok { get; } }
    public sealed class GatePlan { public IReadOnlyList<string> Disable; public IReadOnlyList<string> SessionLoadDisable; public IReadOnlyList<string> Keep; public GateReport Report; }
    public static class GatePlanner {
        public static GatePlan Plan(GateTable table, IEnumerable<GateEntry> adapterEntries, IReadOnlyList<ResolvedSystem> allSystems, IReadOnlyList<string> keepList);   // ARCH §6.3 rules
    }
    public enum PreviewWindowState : byte { Closed, Open, Lingering }
    public sealed class PreviewWindow {                                                        // tool-scoped gates (ARCH §6.3, §4.11.4)
        public PreviewWindow(ToolScopedGateEntry entry);
        public bool Step(double now, string activeToolTypeName, bool appliedThisFrame, bool clientLive, bool guardHealthy);   // true = systems enabled
        public PreviewWindowState State { get; } public double LastActiveSeconds { get; }
    }
    public enum HandoverState : byte { Idle, Loading, AwaitWorldReady, AwaitFirstSimulationUpdate, Verify, Retry, Complete, Failed }
    public struct HandoverInputs { public bool GameLoading; public bool WorldReady; public bool? SimulationIsLoading; public bool SpeedReadBackOk; public int FrameNumber; }
    [Flags] public enum HandoverActions : byte { None = 0, DisableSimulation = 1, EnableSimulation = 2, RestorePausedAfterLoading = 4, StartFollowerClock = 8,
                                                 ForceLoadedByProbe = 16, WriteSentinelSpeed = 32, Fault = 64 }
    public sealed class LoadHandover {                                                         // ARCH §6.1; also used by the host (restore only)
        public LoadHandover(bool isClient, int reflectionFallbackAfterFrames = 10, int maxRetryFrames = 60);
        public void OnGameLoadingComplete();                                                   // → AwaitWorldReady (SimulationSystem stays enabled)
        public HandoverActions Step(in HandoverInputs inputs);                                 // called each LateUpdate before SimulationSystem
        public HandoverState State { get; } public string FailureDetail { get; }
    }
    public sealed class CosmeticSchedule {                                                     // interval/offset semantics of UpdateSystem (ARCH §6.1)
        public void Add(string systemTypeName, int interval, int offset);                     // offset −1 → hash of the type name, as UpdateSystem does
        public bool ShouldRun(string systemTypeName, uint frameIndex);
    }
}
```

- **Wave 1 integration notes** (additive members): `GatePlan.ToolScoped`, `GateReport.Conflicts` / `Describe()`, `PreviewWindowSet` (cached read-only views, allocation-free stepping), `LoadHandover.Reset()`, `SentinelSpeed` and diagnostics (`HoldsSimulationDisabled`, `RestoreOwed`, `FramesWaited`, `Attempts`, `UsedReflectionFallback`, `UsedSentinel`, `Log`), `CosmeticSchedule.ShouldResetDependency` / `Remove` / `Contains` / `TryGetSchedule` / `AutoOffset`. `GatePlanner.Plan` expects adapters' tool-scoped entries merged into `table.ToolScopedEntries`. Executor obligations for `EnableSimulation` and `SpeedReadBackOk` are listed in §7.17.
- **Game APIs**: none (pure .NET). The hand-over transitions encode `SimulationSystem.cs:72-85, 155-162, 202-215` and `GameManager.cs:1136-1137` [V]; the schedule encodes `UpdateSystem.cs:240-248` and `GameSystemBase.cs:131-136` [V].
- **Tests**: gate planner: critical misses, producer/consumer pairs of ARCH §6.3, unclassified listing, adapter entries, session-load and tool-scoped entries separate from permanent ones (a tool-scoped system is never in the permanent keep list); preview window: opens on the tool, lingers exactly `lingerSeconds` after deactivation or the last apply, re-opens on reactivation, never opens while the guard is unhealthy or the client is not live; load hand-over: the happy path (`AwaitWorldReady` → first simulation update → disable, restore, start clock on the first frame after `WorldReady` with the flag clear; a delayed clear waits), the host variant (restore only), the probe fallback (flag still set after 10 frames → `ForceLoadedByProbe`), the probe-less sentinel path, the retry limit → `Fault`; cosmetic schedule against hand-computed frame tables.
- **Done when**: game-follower executes the plans in game and the M2 hand-over self-test is green.

---

## 8. User test scripts (in game)

Each script names the build, the setup, the steps and the evidence. Always finish with "Write diagnostics dump" on every PC involved.

| M | Setup | Steps | Expected | Send back |
|---|---|---|---|---|
| M0 | one PC | install (copy the build folder), start the game, open Options → Skyline Together, press "Run self-test", load any city, look at the top right; check that the "Session" group exists on the settings page | "Co-op" in the main menu; HUD stub; self-test report with probe list; settings texts readable (locale table); no red errors | dump zip, screenshot of the self-test |
| M1 | two PCs, same mods | PC A: Co-op → Host test lobby (`LobbyOnly`, friends only). PC B: accept the Steam invite. Chat both ways. Repeat with direct IP + password, once through the settings-page "Join by address". Then disable one mod on PC B and try again; then give PC B a `Data\policies.json` override and try again. Finally run "transfer test" (200 MB) and pull PC B's network cable for 20 s mid-transfer while chatting | connection both ways; chat (also during the transfer); ping shown; clear mismatch reports naming the mod and the data file; transfer resumes; outbox counters show no lost message | both dumps; R-MODS write-up from the developer |
| M2 | two PCs, a 100k city on PC A | PC A: Co-op → Host → pick the city's save; wait for "Preparing session"; PC B joins; watch 10 minutes without touching anything; PC A changes speed 1 → 3 → pause; PC B presses "Resync" once; then PC A leaves, loads the city normally and uses "Host this city"; run "measure" on PC A for 10 minutes | B shows A's city with moving traffic (hand-over self-test green); time/day/weather in step; B's replica fingerprint unchanged; load-parity census reports no difference; speed mirrored; resync works; host banner during snapshot | both dumps (A includes the measurement report and the session-start duration) |
| M3 | as M2 | first on one PC: "Record" while building for 5 minutes (including the ST-LINES line scenario below), then "Replay" → verdict; repeat with Traffic installed. Then two PCs: A builds roads, zones, a service building, upgrades it, bulldozes part, draws a district, a bus line, renames a street, pastes a large Move It selection; wait for dusk. **ST-LINES 1-2**: A builds two train stations and a track with a gap, then a train line through both; both open the line panel, the line diagram and the transportation overview; A closes the gap; A upgrades one station and moves a bus stop of the bus line; both run `/st lines compare` after each step | replay verdicts "equal" (digests and line rows); B identical including intersections, zoning, the bus line and the pasted objects; street lights switch together; `/st digest S` equal; the train line shows the same stops, length and broken segment on both screens, turns connected on both at the same moment, and stays connected after the upgrade; `/st lines compare` reports no difference | both dumps + replay verdicts + `/st lines` output |
| M4 | as M2 | B builds roads, zones, buildings, upgrades, relocates a service building whose vehicles are on the way, rebuilds a destroyed building, moves district nodes and bus stops, bulldozes; B changes taxes, budget, a fee, takes a loan, toggles a policy, buys a dev node and a map tile, renames; A and B edit the same road at the same time; B edits while A saves; B uses Move It; both press speed buttons. **ST-LINES 3**: B draws a new bus line, moves one of its waypoints, and re-routes A's line from one stop to a neighbouring stop | B's actions instant and visible on A (nothing reverted silently); vehicles re-path to the relocated building; conflicts show a toast and roll back; the edit during A's save is kept; speed requests honoured; cursors visible; B's line preview follows the roads and validates like vanilla; both see B's line within a round trip of applying; A's other lines never change on B during the edit; both stops list the re-routed line correctly; `/st lines compare` clean | both dumps |
| M5a | one PC first, then as M2 with the city running 30 minutes | PC A: shadow-client self-test. Then two PCs: pick three buildings, a household and a company; compare their panels after a few minutes (values converge, not time-aligned yet); watch the network panel | shadow client 0 mismatches; values converge; budget within limits; no repair storm | both dumps |
| M5b | as M5a | watch money, demand, budget panel, statistics; reach a milestone; buy a dev node on A; wait for a fire or crime; read chirps and the journal. **ST-LINES (icons) and 5**: repeat the M3 gap scenario and compare the line warnings; A deletes the depot of a line; A bulldozes the road in front of a building, cuts its power line and water pipe, and leaves a dead-end road; B restores the road | money, demand, statistics, budget identical; milestone popup and toolbar unlocks at the same moment; icons, fires, crime, abandonment, chirps consistent; "path not found" and "not enough vehicles" warnings, road-access, electricity, water and dead-end warnings identical on both screens and gone on both after the fix; self-test (m) green; dropped-command counters name only the road-access and turned-off notification prefabs | both dumps |
| M5c | as M5a | both open the same building and the same citizen panel (also on the host); compare numbers; both open happiness, traffic, electricity and water infoviews; both open the same line panel and the transportation overview | identical simulation numbers in the panels at every periodic refresh; matching infoviews in view, including the electricity and water network colours; identical line numbers while the panel is open | both dumps |
| M6 | as M2 | both look at the same busy junction at speed 1 and 4; enable Steam fake lag 120 ms and loss 5 % on B; follow a bus; click a car; pan far away and back; watch an ambulance. **ST-LINES 4**: both open the same bus line and the transportation overview while buses run | identical traffic, smooth, no snap-backs or drifting cars; lights and sirens; selection works; vehicle count, usage and waiting passengers identical in the line panel and the line list; `/st lines compare` clean | both dumps |
| M7 | three PCs if possible | B and C join one after another; debug commands on B flip a value, delete an entity, add an extra one; pull B's cable for 30 s; trigger the forced-exception debug command on C | repairs without reload; B resumes without reload and its traffic keeps moving; C freezes, resyncs and continues | all dumps |
| M8 | two PCs with the full targeted mod list | terraform on both sides (once with PC B limited to 15 fps); Water Features sources; then the per-mod checklist of §7.25 | terrain equal (tile hashes) without visible snapping; water close; every mod usable from both sides | both dumps + checklist |
| M9 | 4+ players over relay, 100k city | play 2-4 hours normally, including joins and leaves | no unplanned resync; network panel within budget; host overhead < 10 % | all dumps |
