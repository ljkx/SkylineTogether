# 0001: Build without NuGet

| | |
|---|---|
| Status | Accepted, 2026-10-11 (milestone M0, solution scaffold) |
| Changes | The original implementation plan ([history](../history/IMPLEMENTATION-PLAN.md)) §2.3 (Core project), §2.5 (tests), §2.7 (build script); the architecture is unchanged |

## Context

The plan assumes a `netstandard2.0` Core project, a test project with xUnit that targets `net8.0;net472` through the
`Microsoft.NETFramework.ReferenceAssemblies` package, and a PowerShell build script. In the environment the project is
built in, **no NuGet download is allowed**: neither reference-assembly packages nor test frameworks can be restored.
The only inputs are the .NET SDK (8.0) and the game's own `Cities2_Data\Managed` folder.

## Decision

1. **Core** (`src/SkylineTogether.Core`) targets `net472` and compiles against the game's own runtime assemblies,
   exactly like the game project: `NoStdLib`, `DisableImplicitFrameworkReferences`,
   `AutomaticallyUseReferenceAssemblyPackages=false`, `FrameworkPathOverride=$(GameManagedPath)`. It references
   **only** `mscorlib`, `System`, `System.Core`, `netstandard`, `System.Memory`,
   `System.Runtime.CompilerServices.Unsafe` and `System.IO.Compression` from the Managed folder (all with
   `Private=false`), and no Unity, Game or Colossal assembly: Core stays pure .NET ([module map](../ARCHITECTURE.md#module-map)).
2. **Core code uses only APIs that exist in netstandard2.0**, so the same sources also compile on .NET 8. (The game's
   `netstandard.dll` is a 2.1 facade, so the compiler would accept 2.1 APIs; the rule is enforced by review. Typical
   traps: `Math.Clamp`, `HashCode`, `BitConverter.SingleToInt32Bits`, `Dictionary.TryAdd`, span overloads of
   `Encoding`/`Stream`, `string.Contains(char)`.)
3. **Tests** (`tests/SkylineTogether.Core.Tests`) are a `net8.0` console application without any package. It compiles
   the Core sources directly (`<Compile Include="..\..\src\SkylineTogether.Core\**\*.cs" />`, so internal members are
   testable without `InternalsVisibleTo`) plus its own small test framework (`Framework/`: `[Test]` methods discovered
   by reflection, `Assert` helpers, name filters as command-line arguments, exit code = number of failures, capped at
   125). Tests live in `tests/SkylineTogether.Core.Tests/<Area>/*Tests.cs`. The hash implementations are
   additionally cross-checked against Microsoft's `System.IO.Hashing.dll`, which the game ships in its Managed folder;
   it is a conditional plain file reference, not a package, and those checks are compiled only when the file exists.
4. **Build script**: `tools/build.sh` (bash; Git Bash on Windows) runs the Release build, `tools/ApiCheck` and the UI
   checks when they exist, the tests, then optionally deploys (`--deploy`) and packages (`--package`).
5. **Language and toolchain limits** (unchanged hard constraints, restated because they shape every module): C# 10;
   no Entities source generators or IL post-processing (no `SystemAPI`, `IJobEntity`, `ISystem` code generation, no
   custom `IJobChunk` scheduling, no Burst requirement); game systems are `GameSystemBase` subclasses that iterate
   chunks on the main thread and complete the game's job dependencies before touching component data (ECS safety
   checks are compiled out of the game build). Every game type or member used is verified against the decompiled
   code; reflection is used only where the design lists a probe, always with a fallback and a log line.
6. **Agents build and test only through a snapshot checker** that copies the repository into a private temporary
   folder, so parallel work never shares `obj`/`bin` folders; nobody runs `dotnet build` inside the repository while
   others work on it.

## Consequences

- There is no `net472` test run. The .NET Framework/Mono API surface is covered at compile time instead: Core is
  compiled against the game's own `mscorlib`, and the game project compiles against Core. `tools/ApiCheck` (when it
  exists) adds the metadata check that every referenced member resolves in the game's assemblies.
- xUnit features (theories, fixtures, parallel runs) are not available; tests are plain methods. Data-driven tests
  loop over their cases and name the failing case in the assertion message.
- The Core assembly is built for `net472` (the game's profile) instead of `netstandard2.0`; it is loaded by the game's
  Mono exactly like the game assembly and depends on nothing the game does not ship.
- Builds need the game installed (or `GameDir`/`GameManagedPath` pointing at a copy of its Managed folder).

## Verification

With .NET SDK 8.0.425 and the current game build: the Core and game projects and the test project build with
`TreatWarningsAsErrors=true` (Debug and Release), and the test runner discovers and runs the tests.

## Amendment (2026-10-14): building Core without the game

For continuous integration and for contributors who only work on Core, `SkylineTogether.Core.csproj` no longer fails
when the game is missing. When `$(GameManagedPath)\mscorlib.dll` does not exist, Core targets `netstandard2.1` and
compiles against the reference assemblies that ship inside the .NET SDK (`packs/NETStandard.Library.Ref`), so restore
still downloads nothing. netstandard2.1 is the API level of the game's own `netstandard.dll` facade, so this compile
check accepts the same APIs as the real build; the netstandard2.0 rule of point 2 is still enforced by review. The
output of that fallback is never shipped. With the game present the project is built exactly as described above.
The game project itself still needs the game. See [docs/dev/building.md](../dev/building.md).
