# 06 — Networking and Platform Services

Research report for **Skyline Together** (from-scratch, host-authoritative co-op mod for Cities: Skylines II).

All decompiled paths below are relative to `<audit>\decomp`.
Game-install paths are relative to `C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II`.

**Evidence legend**

| Tag | Meaning |
|---|---|
| **[V]** | Verified: read in decompiled code, or checked in the shipped binaries (file listing, version resource, export/string table). |
| **[I]** | Inference: follows from verified facts, but the runtime behaviour was not observed. |
| **[X]** | External knowledge: Steamworks / GameNetworkingSockets documentation or general platform knowledge. Not verifiable from the files here; check it at runtime. |

---

## 0. Executive summary

1. **Steam is always initialised before any mod loads on the Steam build. [V]** `GameManager.Initialize()` awaits `InitializePlatformManager()` (`Game/Game.SceneFlow/GameManager.cs:593`) and only later calls `InitializeModManager()` (`:624`). If any platform integration fails to initialise, the game calls `QuitGame()` (`:1678-1682`). `SteamworksPlatform.Initialize()` returns false when `SteamAPI.Init()` fails or the AppID does not match (`Colossal.PSI.Steamworks/Colossal.PSI.Steamworks/SteamworksPlatform.cs:526-536`). So, on this build, a running game implies an initialised SteamAPI.
2. **App ID 949230. [V]** It is hard-coded in `Game/Game.PSI/PlatformSupport.cs:10-12`. Every Steam networking, lobby and rich-presence feature the mod uses runs under the CS2 app ID.
3. **`SteamAPI.RunCallbacks()` runs once per frame on the main thread. [V]** The call chain is `GameManager.Update()` → `UpdatePlatforms()` → `PlatformManager.Update()` → `SteamworksPlatform.Update()` → `SteamAPI.RunCallbacks()` (`GameManager.cs:724,1689-1692`; `Colossal.PSI.Common/Colossal.PSI.Common/PlatformManager.cs:690-707`; `SteamworksPlatform.cs:577-589`). It runs **after** the ECS MainLoop and Cleanup phases of the same frame. It keeps running when the world is not updating and during shutdown, because it sits outside the `shouldUpdateManager` guard.
4. **A mod can safely create its own `Callback<T>` and `CallResult<T>`. [V]** Steamworks.NET's `CallbackDispatcher` keeps a *list* of subscribers per callback ID (`com.rlabrecque.steamworks.net/Steamworks/CallbackDispatcher.cs:10,56-69`), copies the list before dispatch (`:190-192`), and catches exceptions per handler (`Callback.cs:90-98`). The game itself creates a transient `Callback<AvatarImageLoaded_t>` (`SteamworksPlatform.cs:861`).
   - Rule: **reference the game's `com.rlabrecque.steamworks.net.dll`. Never ship your own copy, and never ship Facepunch.Steamworks.** The dispatcher runs in Steam's exclusive *manual dispatch* mode (`CallbackDispatcher.cs:35`). `steam_api64.dll` contains the string *"SteamAPI_ManualDispatch_Init() Cannot be used, standard dispatch has already been selected."* [V].
5. **The game uses no Steam networking, lobbies or join requests at all. [V]** Grep across `Game`, `Colossal.PSI.*`, `Colossal.Core` and `Colossal.IO.AssetDatabase` finds none. The mod therefore owns these interfaces, but must call `SteamNetworkingUtils.InitRelayNetworkAccess()` itself.
6. **Recommended transport: Steam Networking Sockets (`ISteamNetworkingSockets` v012).** Use `CreateListenSocketP2P` / `ConnectP2P` by SteamID with one poll group on the host. It gives reliable-ordered and unreliable messages, Steam Datagram Relay (NAT traversal and IP hiding), and end-to-end encryption, all from one API. Steamworks.NET 20.2.0 / SDK 1.57 / `steam_api64` 08.02.21.95 [V].
   - Large world snapshots need **application-level chunking**: the maximum message size is 512 KiB (`Constants.cs:253`).
   - To stop the bulk transfer from blocking commands, use either **lanes** or a **second connection on another virtual port**. The lanes binding is broken for more than one lane (see §5.6). Lanes need your own P/Invoke; the second-connection option works with the existing binding.
7. **Snapshots:** the game's save serializer already ZSTD-compresses entity buffers (`Game/Game.Serialization/SerializerSystem.cs:102`, `BufferFormat.CompressedZStd`). Re-compressing a save stream gains little [I].
   - If you need compression, the game ships native LZ4/ZSTD in `Colossal.AssetPipeline.Native.NativeCompression`, wrapped by `Colossal.IO.AssetDatabase/Colossal.Compression/CompressionUtils.cs` [V].
   - For integrity hashes, `System.IO.Hashing.dll` 9.0 (XxHash3/XxHash128/Crc32) ships in Managed [V].
8. **`System.Net.Sockets` is fully available.** Mono 4.6.57 BCL, `netstandard` 2.1 [V]. The game itself uses `System.Net.Dns` and `System.Net.NetworkInformation.Ping` (`Colossal.PSI.Common/Colossal.PSI.Common/Connectivity.cs:73-113`). That makes a raw-UDP or TCP fallback technically possible, but it has no relay and no built-in encryption.
9. **ECS must be touched only from the main thread.** Unity.Entities' thread checks are compiled out in the player (`[Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]` on `EntityDataAccess.AssertMainThread`, `Unity.Entities/Unity.Entities/EntityDataAccess.cs:331-339`), so a violation **corrupts silently instead of throwing** [V].
   - `MainThreadDispatcher.RunOnMainThread` is backed by an unordered `ConcurrentDictionary` (`Colossal.Core/Colossal.Core/MainThreadDispatcher.cs:16,32-42,85-95`). It is unsuitable for ordered message delivery. Use your own `ConcurrentQueue`.
10. **Non-Steam stores:** `Unity.Microsoft.GDK.dll` (with XblMultiplayer, XGameInvite, social types) and the GDK native thunks ship in this Steam install [V]. This `Game.dll` registers only Steam and Discord PSIs (`GameManager.cs:1676-1677`). An Xbox/Game Pass build has no Steam relay, so a cross-store solution needs a third-party relay (for example Epic Online Services P2P) or direct connections [I/X].

---

## 1. Runtime environment (verified from the install)

| Item | Value | Evidence |
|---|---|---|
| Unity player | 2022.3.71 (`UnityPlayer.dll` FileVersion `2022.3.71.13221651`) | version resource [V] |
| Scripting runtime | Mono (`MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll`), BCL file version 4.6.57.0 | `Managed/mscorlib.dll`, `System.dll` [V] |
| netstandard facade | 2.1.0.0 | `Managed/netstandard.dll` [V] |
| Steamworks.NET | 20.2.0, SDK 1.57, steam_api DLL 08.02.21.95 | `com.rlabrecque.steamworks.net/Steamworks/Version.cs:5-13`; also logged by `SteamworksPlatform.LogVersion` (`SteamworksPlatform.cs:629-634`) [V] |
| Native Steam API | `Cities2_Data/Plugins/x86_64/steam_api64.dll`, FileVersion 08.02.21.95, size 298,856 bytes (matches `Version.SteamAPI64DLLSize`) | [V] |
| Steamworks.NET assembly | `Managed/com.rlabrecque.steamworks.net.dll`, AssemblyVersion 0.0.0.0 | `Properties/AssemblyInfo.cs` [V] |
| Discord | `Managed/DiscordSDK.dll` (Discord Game SDK C# wrapper), `Colossal.PSI.Discord.dll`, native `discord_game_sdk.dll` | [V] |
| PDX SDK | `PDX.SDK.dll`, `PDX.ModsUI.dll`, `Colossal.PSI.PdxSdk.dll` | [V] |
| Xbox GDK | `Unity.Microsoft.GDK.dll`, `XblPCSandbox.dll`, native `Microsoft.Xbox.Services.GDK.C.Thunks.dll`, `XGameRuntime.Thunks.dll`, `libHttpClient.GDK.dll`, `XCurl.dll` | [V] |
| `boot.config` | `single-instance=` (Unity single-instance mode), `gc-max-time-slice=3` (incremental GC) | `Cities2_Data/boot.config` [V] |
| Launcher | Paradox Launcher, `Launcher/launcher-settings.json` (`"distPlatform": "steam"`, `"exePath": "../Cities2.exe"`, `"version": "1.6.2f1"`) | [V] |

Consequences:

- **`single-instance=` [V]: you cannot run two copies of the game on one Windows session [I].** Plan development testing around this:
  - Use two PCs, or a VM with a second Steam account.
  - Or use an in-process loopback transport built on `SteamNetworkingSockets.CreateSocketPair` (§5.1).
- **Incremental GC with a 3 ms slice [V]:** the network path must avoid per-message allocation. Use pooled `byte[]` / `NativeArray` buffers and avoid LINQ and boxing [I].

---

## 2. How the game initialises Steam and pumps callbacks

### 2.1 Construction and registration [V]

- `Game/Game.PSI/PlatformSupport.cs:10-16`
  ```csharp
  private const uint kSteamAppId = 949230u;
  public static readonly Func<IPlatformServiceIntegration> kCreateSteamPlatform = () => new SteamworksPlatform(949230u);
  private const long kDiscordClientId = 1125009418476605441L;
  public static readonly Func<IPlatformServiceIntegration> kCreateDiscordRichPresence = () => new DiscordRichPresence(1125009418476605441L);
  ```
- `Game/Game.SceneFlow/GameManager.cs:1671-1687` `InitializePlatformManager()`:
  - registers rich-presence keys (`#StatusInMainMenu` etc., `:1673-1675`);
  - `RegisterPSI(kCreateSteamPlatform)` (`:1676`) and `RegisterPSI(kCreateDiscordRichPresence)` (`:1677`);
  - then `PlatformManager.instance.Initialize(token)`; on failure it logs and calls `QuitGame()` (`:1678-1682`).
- `GameManager.Initialize()` order (`GameManager.cs:577-672`):
  - `InitializePlatformManager()` at `:593`;
  - `RegisterPdxSdk()` at `:613` (PDX SDK is registered **later** as a separate PSI);
  - `InitializeModManager()` at `:624` → `ModManager.Initialize(updateSystem)` → `IMod.OnLoad(UpdateSystem)` (`Game/Game.Modding/IMod.cs`, `Game/Game.Modding/ModManager.cs:124,153-157`).
  - **Conclusion [V]:** at `IMod.OnLoad`, `SteamAPI` is initialised and the dispatcher is live.
- `PlatformManager.RegisterPSI<T>(Func<T>, CancellationToken)` (`PlatformManager.cs:566-608`) pushes the PSI into typed `ConcurrentStack`s. The first registrant of each capability becomes "principal" (`:422-434`). A PSI can be fetched with `PlatformManager.instance.GetPSI<T>(string name)` (`:376-386`). The names are `"Steamworks"` (`SteamworksPlatform.cs:93`), `"PdxSdk"` (`Colossal.PSI.PdxSdk/Colossal.PSI.PdxSdk/PdxSdkPlatform.cs:418`) and `"Discord"` (used at `Game/Game.PSI/RichPresenceUpdateSystem.cs:74`).
- `PlatformManager.Initialize` (`PlatformManager.cs:630-657`):
  - starts `Connectivity.StartConnectivityCheck(3000, …)`, which DNS-resolves and pings `api.paradox-interactive.com` every 3 s on a thread-pool task (`Connectivity.cs:13,33-71`);
  - then calls `psi.Initialize(token)` for every PSI.

### 2.2 `SteamworksPlatform.Initialize()` [V] (`SteamworksPlatform.cs:499-560`)

1. `Packsize.Test()` and `DllCheck.Test()` run as warnings only (`:506-513`).
2. `SteamAPI.RestartAppIfNecessary((AppId_t)949230)`. If true, it returns false and the game quits (`:516-519`). A `DllNotFoundException` is caught (`:521-525`).
3. `m_IsInitialized = SteamAPI.Init()` (`:526`). `SteamAPI.Init()` calls `NativeMethods.SteamAPI_Init()`, then `CSteamAPIContext.Init()`, then `CallbackDispatcher.Initialize()` (`com.rlabrecque.steamworks.net/Steamworks/SteamAPI.cs:5-18`). `CallbackDispatcher.Initialize` calls **`SteamAPI_ManualDispatch_Init()`** (`CallbackDispatcher.cs:29-40`).
4. AppID sanity check: `SteamUtils.GetAppID().m_AppId == 949230` (`:532-536`).
5. The game's own callbacks are `GameOverlayActivated_t`, `PersonaStateChange_t`, `UserStatsReceived_t`, `UserStatsStored_t` and `UserAchievementStored_t` (`:537-539`, fields `:57-61,79-81`). `FloatingGamepadTextInputDismissed_t` is created on demand (`:956`).
6. `SteamClient.SetWarningMessageHook(...)` → log (`:542-546`).

The game uses **only** `SteamAPI.Init()`, the client API. It never uses the GameServer API (`GameServer.Init`) and never calls `SteamNetworkingUtils.InitRelayNetworkAccess()` [V].

### 2.3 Per-frame pump [V]

`GameManager.Update()` (`GameManager.cs:712-725`):

```csharp
if (shouldUpdateManager && !m_Cts.IsCancellationRequested) {
    TestScenarioSystem.instance.Update(); InputManager.instance.Update(); ...
    UpdateWorld();      // m_UpdateSystem.Update(SystemUpdatePhase.MainLoop)  (:2401-2408)
    UpdateUI();
    PostUpdateWorld();  // SystemUpdatePhase.Cleanup                          (:2410-2416)
}
MainThreadDispatcher.UpdateUpdaters();   // :723
UpdatePlatforms();                       // :724 -> PlatformManager.instance.Update()
```

`LateUpdate()` → `LateUpdateWorld()` runs the `LateUpdate` and `DebugGizmos` phases (`:727-733`, `:2418-2426`).

`PlatformManager.Update()` (`PlatformManager.cs:690-707`):

1. Drains `m_MainThreadActionQueue`.
2. Calls `psi.Update()` for every PSI.

`SteamworksPlatform.Update()` (`SteamworksPlatform.cs:577-589`):

1. Optionally calls `SteamUserStats.StoreStats()`.
2. Calls **`SteamAPI.RunCallbacks()`** → `CallbackDispatcher.RunFrame(false)` (`SteamAPI.cs:40-43`).

**Implications**

- Steam callbacks, including any `Callback<T>` the mod registers, fire on the **main thread**, once per rendered frame, after the ECS MainLoop and Cleanup phases and before `LateUpdate` [V].
- `UpdatePlatforms()` sits outside the `shouldUpdateManager` guard. Callbacks are therefore pumped during boot, loading and shutdown as well [V].
- Callback latency is therefore at most one frame. In-game, `Application.targetFrameRate = -1` (`GameManager.cs:1112-1121`); the main menu uses 120. At 20–30 FPS in a large city, that is 33–50 ms [I].
- **Message receipt does not depend on `RunCallbacks`.** `ReceiveMessagesOnPollGroup` / `ReceiveMessagesOnConnection` are polled directly. Only connection-state changes (`SteamNetConnectionStatusChangedCallback_t`), lobby events and similar arrive via the pump [X].
- Steam Networking Sockets runs its own service thread for acks, keepalives, retransmission and the relay [X]. Connections should survive main-thread hitches shorter than `TimeoutConnected`. That value is configurable through `k_ESteamNetworkingConfig_TimeoutConnected = 25` (`ESteamNetworkingConfigValue.cs:7`) and defaults to about 10 s per the SDK docs [X].

### 2.4 Game state gates [V]

- `shouldUpdateManager => m_State >= State.UIReady` (`GameManager.cs:294`).
- `shouldUpdateWorld => m_State >= State.WorldReady` (`:296`).
- `enum State { Booting, Terminated, UIReady, BootingPrefabs, WorldDisposed, WorldReady, Quitting, Loading }` (`:124-134`).

`Loading` (7) is greater than `WorldReady` (5), so ECS phases keep running during `Load()` (`m_State = State.Loading` at `:1063`, back to `WorldReady` at `:1137`). An ECS-system-based network pump therefore keeps ticking during loads, but only as often as the load lets frames through [I].

### 2.5 Shutdown order [V] (`GameManager.TerminateGame`, `GameManager.cs:769-…`)

1. `m_Cts.Cancel()` (`:801`). World and UI updates stop; `UpdatePlatforms()` keeps running.
2. `m_ModManager?.Dispose()` (`:802`) → `IMod.OnDispose()`.
3. `DestroyWorld()`.
4. `DisposePlatforms()` with a 10 s timeout (`:805`). This reaches `SteamworksPlatform.Dispose` (`SteamworksPlatform.cs:591-607`) → `SteamAPI.Shutdown()` → `CallbackDispatcher.Shutdown()` → `UnregisterAll()` (`CallbackDispatcher.cs:116-146`).

**So in `IMod.OnDispose` Steam is still alive**, and the mod can:

- call `CloseConnection(..., bEnableLinger: true)`;
- call `LeaveLobby`;
- clear its own rich-presence keys.

`PlatformManager.Dispose` also calls `ClearRichPresence()` (`PlatformManager.cs:709-…`, `SteamworksPlatform.cs:829-836`).

---

## 3. Steamworks.NET callback system: what a mod may do

### 3.1 Mechanics [V]

- **Manual dispatch.** `CallbackDispatcher.RunFrame(bool isGameServer)` (`CallbackDispatcher.cs:148-210`):
  1. Calls `SteamAPI_ManualDispatch_RunFrame(pipe)`, then loops `SteamAPI_ManualDispatch_GetNextCallback`.
  2. ID 703 (`SteamAPICallCompleted_t`) is routed to the `CallResult` registered for that `SteamAPICall_t` (`:162-182`).
  3. Every other ID is dispatched to a **copied list** of all `Callback` objects registered for that ID (`:185-198`).
  4. `SteamAPI_ManualDispatch_FreeLastCallback` always runs in `finally` (`:207`).
  5. If the dispatcher is not initialised, it throws `InvalidOperationException("Callback dispatcher is not initialized.")` (`:152`).
- **Multiple subscribers per ID** are supported: a `Dictionary<int, List<Callback>>` (`:10`, `Register` `:56-69`). Mod handlers coexist with the game's handlers. For example, both can listen to `PersonaStateChange_t`.
- **Thread-safety:** registration and unregistration take `lock (m_sync)` (`:60,88`), so creating a `Callback<T>` from a worker thread is safe. **Dispatch always happens on the thread calling `RunCallbacks`, which is the main thread** [V].
- **Exceptions** inside a handler are caught and sent to `Debug.LogException` (`Callback.cs:90-98`, `CallbackDispatcher.cs:24-27`). A faulty mod handler cannot break the game's pump.
- **GC hazard:** `Callback<T>` and `CallResult<T>` have finalizers that unregister or cancel (`Callback.cs:46-49`, `CallResult.cs:36-39`). **Keep strong references** in fields for the session's lifetime. Never write `Callback<T>.Create(...)` without storing the result.
- `CallResult<T>.Set(SteamAPICall_t, APIDispatchDelegate)` (`CallResult.cs:51-70`). The handler receives `(T param, bool bIOFailure)`.

### 3.2 Rules for Skyline Together

| Do | Don't |
|---|---|
| Reference `Managed/com.rlabrecque.steamworks.net.dll` with `Private=false` (do not copy it into the mod folder). | Ship any Steamworks wrapper: Steamworks.NET copy, Facepunch.Steamworks, or Heathen. A second `CallbackDispatcher` would be uninitialised (`RunFrame` throws), and a second native dispatch mode is refused by `steam_api64`. |
| Check `PlatformManager.instance.GetPSI<SteamworksPlatform>("Steamworks")?.isInitialized == true` before any Steam call. Otherwise `InteropHelp.TestIfAvailableClient()` throws `InvalidOperationException("Steamworks is not initialized.")` (`InteropHelp.cs:103-110`). | Call `SteamAPI.Init()`, `SteamAPI.Shutdown()` or `SteamAPI.RestartAppIfNecessary()` yourself. |
| Store every `Callback<T>` and `CallResult<T>` in a field and `Dispose()` it in `OnDispose`. | Call `SteamAPI.RunCallbacks()` from a worker thread. It would race with the game's main-thread pump on the same pipe [I]. |
| Poll `ReceiveMessagesOn*` yourself, on the main thread or on a dedicated network thread (§7). | Rely on callback timing for latency-critical data. |

Calling `SteamAPI.RunCallbacks()` an *extra* time from the main thread would technically work [I]. It would also run the game's own handlers at an unexpected point in the frame, and the mod does not need it.

---

## 4. Steam services relevant to co-op: concrete API (Steamworks.NET 20.2.0)

All signatures below are copied from the decompiled wrapper [V]. Paths are under `com.rlabrecque.steamworks.net/Steamworks/`.

### 4.1 `SteamNetworkingSockets` (`SteamNetworkingSockets.cs`, interface `SteamNetworkingSockets012`, `Constants.cs:45`)

```csharp
HSteamListenSocket CreateListenSocketP2P(int nLocalVirtualPort, int nOptions, SteamNetworkingConfigValue_t[] pOptions);   // :20
HSteamNetConnection ConnectP2P(ref SteamNetworkingIdentity identityRemote, int nRemoteVirtualPort, int nOptions, SteamNetworkingConfigValue_t[] pOptions); // :26
HSteamListenSocket CreateListenSocketIP(ref SteamNetworkingIPAddr localAddress, int nOptions, SteamNetworkingConfigValue_t[] pOptions); // :8
HSteamNetConnection ConnectByIPAddress(ref SteamNetworkingIPAddr address, int nOptions, SteamNetworkingConfigValue_t[] pOptions);       // :14
EResult AcceptConnection(HSteamNetConnection hConn);                                                       // :32
bool CloseConnection(HSteamNetConnection hPeer, int nReason, string pszDebug, bool bEnableLinger);         // :38
bool CloseListenSocket(HSteamListenSocket hSocket);                                                        // :45
bool SetConnectionUserData(HSteamNetConnection hPeer, long nUserData);  long GetConnectionUserData(...);   // :51,57
void SetConnectionName(HSteamNetConnection hPeer, string pszName);                                         // :63
EResult SendMessageToConnection(HSteamNetConnection hConn, IntPtr pData, uint cbData, int nSendFlags, out long pOutMessageNumber); // :80
void SendMessages(int nMessages, SteamNetworkingMessage_t[] pMessages, long[] pOutMessageNumberOrResult);   // :86  (see 5.6 - binding suspect)
EResult FlushMessagesOnConnection(HSteamNetConnection hConn);                                              // :92
int ReceiveMessagesOnConnection(HSteamNetConnection hConn, IntPtr[] ppOutMessages, int nMaxMessages);      // :98
bool GetConnectionInfo(HSteamNetConnection hConn, out SteamNetConnectionInfo_t pInfo);                    // :104
EResult GetConnectionRealTimeStatus(HSteamNetConnection hConn, ref SteamNetConnectionRealTimeStatus_t pStatus, int nLanes, ref SteamNetConnectionRealTimeLaneStatus_t pLanes); // :110
int GetDetailedConnectionStatus(HSteamNetConnection hConn, out string pszBuf, int cbBuf);                 // :116
bool CreateSocketPair(out HSteamNetConnection c1, out HSteamNetConnection c2, bool bUseNetworkLoopback, ref SteamNetworkingIdentity id1, ref SteamNetworkingIdentity id2); // :132
EResult ConfigureConnectionLanes(HSteamNetConnection hConn, int nNumLanes, out int pLanePriorities, out ushort pLaneWeights); // :138 (BROKEN for >1 lane, see 5.6)
bool GetIdentity(out SteamNetworkingIdentity pIdentity);                                                   // :144
ESteamNetworkingAvailability InitAuthentication(); / GetAuthenticationStatus(out SteamNetAuthenticationStatus_t); // :150,156
HSteamNetPollGroup CreatePollGroup(); bool DestroyPollGroup(HSteamNetPollGroup);                           // :162,168
bool SetConnectionPollGroup(HSteamNetConnection hConn, HSteamNetPollGroup hPollGroup);                    // :174
int ReceiveMessagesOnPollGroup(HSteamNetPollGroup hPollGroup, IntPtr[] ppOutMessages, int nMaxMessages);  // :180
void RunCallbacks();                                                                                       // :264
// FakeIP: BeginAsyncRequestFakeIP / GetFakeIP / CreateListenSocketP2PFakeIP / CreateFakeUDPPort             // :270-298
```

Receive pattern. Messages are native objects and **must be released** [V] (`SteamNetworkingMessage_t.cs:42-50`):

```csharp
int n = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(pollGroup, ptrs, ptrs.Length);
for (int i = 0; i < n; i++) {
    var msg = SteamNetworkingMessage_t.FromIntPtr(ptrs[i]);   // Marshal.PtrToStructure: allocates a struct copy
    // msg.m_pData (IntPtr), msg.m_cbSize, msg.m_conn, msg.m_identityPeer, msg.m_nConnUserData,
    // msg.m_nMessageNumber, msg.m_nChannel, msg.m_nFlags, msg.m_idxLane    (struct fields :9-35)
    // copy/parse payload here (unsafe read from m_pData), then:
    SteamNetworkingMessage_t.Release(ptrs[i]);
}
```

`FromIntPtr` uses reflection-based `Marshal.PtrToStructure`, which allocates [V]. A hot path should read the fields at fixed offsets with `unsafe` code, or declare a blittable mirror struct [I].

Connection events arrive as `SteamNetConnectionStatusChangedCallback_t` (ID 1221; fields `m_hConn`, `m_info`, `m_eOldState`) via `Callback<T>` [V]. The states are `None=0, Connecting=1, FindingRoute=2, Connected=3, ClosedByPeer=4, ProblemDetectedLocally=5` plus the internal negative states (`ESteamNetworkingConnectionState.cs:3-15`) [V].

Stats come from `SteamNetConnectionRealTimeStatus_t` [V]:

- `m_nPing`, `m_flConnectionQualityLocal`, `m_flConnectionQualityRemote`
- `m_flOutBytesPerSec`, `m_flInBytesPerSec`, `m_nSendRateBytesPerSecond`
- `m_cbPendingUnreliable`, `m_cbPendingReliable`, `m_cbSentUnackedReliable`, `m_usecQueueTime`

Use them for snapshot back-pressure and for the in-game network HUD.

### 4.2 Send flags and limits (`Constants.cs`) [V]

| Constant | Value | Line |
|---|---|---|
| `k_cbMaxSteamNetworkingSocketsMessageSizeSend` | 524288 (512 KiB) | 253 |
| `k_nSteamNetworkingSend_Unreliable` | 0 | 255 |
| `k_nSteamNetworkingSend_NoNagle` | 1 | 257 |
| `k_nSteamNetworkingSend_NoDelay` | 4 | 261 |
| `k_nSteamNetworkingSend_UnreliableNoDelay` | 5 | 263 |
| `k_nSteamNetworkingSend_Reliable` | 8 | 265 |
| `k_nSteamNetworkingSend_ReliableNoNagle` | 9 | 267 |
| `k_nSteamNetworkingSend_UseCurrentThread` | 16 | 269 |
| `k_nSteamNetworkingSend_AutoRestartBrokenSession` | 32 (Messages API) | 271 |
| ICE enable flags (`Disable/Relay/Private/Public/All`) | 0/1/2/4/MaxValue | 279-289 |

Relevant `ESteamNetworkingConfigValue` entries (`ESteamNetworkingConfigValue.cs`) [V]:

- `TimeoutInitial=24`, `TimeoutConnected=25`
- `SendBufferSize=9`, `SendRateMin=10`, `SendRateMax=11`, `NagleTime=12`
- `IP_AllowWithoutAuth=23`, `MTU_PacketSize=32`, `Unencrypted=34`
- `FakePacketLoss_*` (2,3), `FakePacketLag_*` (4,5), `FakePacketReorder_*` (6-8), `FakePacketDup_*` (26-28): built-in network-condition simulation, very useful for sync testing
- `Callback_ConnectionStatusChanged=201`
- `P2P_Transport_ICE_Enable=104`
- `SDRClient_*` (19-22, 29-31, 36)
- `LogLevel_*` (13-18)

You set these with:

```csharp
SteamNetworkingUtils.SetConfigValue(ESteamNetworkingConfigValue, ESteamNetworkingConfigScope, IntPtr scopeObj, ESteamNetworkingConfigDataType, IntPtr pArg)   // SteamNetworkingUtils.cs:120
```

or per connection through the `SteamNetworkingConfigValue_t[] pOptions` argument of `ConnectP2P` / `CreateListenSocketP2P`. That struct has an explicit-layout union `m_val` (`SteamNetworkingConfigValue_t.cs:7-32`) [V].

`steam_api64.dll` also exports `SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32` and `SteamAPI_ISteamNetworkingUtils_SetConnectionConfigValueInt32`. Steamworks.NET does not wrap them, but a mod can `DllImport` them directly [V: export strings present].

### 4.3 `SteamNetworkingUtils` (`SteamNetworkingUtils.cs`) [V]

```csharp
void InitRelayNetworkAccess();                                                   // :14  -> call once in IMod.OnLoad
ESteamNetworkingAvailability GetRelayNetworkStatus(out SteamRelayNetworkStatus_t pDetails); // :20
float GetLocalPingLocation(out SteamNetworkPingLocation_t result);              // :26
int EstimatePingTimeBetweenTwoLocations(ref SteamNetworkPingLocation_t a, ref SteamNetworkPingLocation_t b); // :32
void ConvertPingLocationToString(ref SteamNetworkPingLocation_t, out string, int); bool ParsePingLocationString(string, out SteamNetworkPingLocation_t); // :44,53
SteamNetworkingMicroseconds GetLocalTimestamp();                                // :90  (monotonic us clock, good for net-time)
void SetDebugOutputFunction(ESteamNetworkingSocketsDebugOutputType, FSteamNetworkingSocketsDebugOutput); // :96
IntPtr AllocateMessage(int cbAllocateBuffer);                                   // :8
```

`SteamRelayNetworkStatus_t` (callback 1281) carries `m_eAvail`, `m_eAvailNetworkConfig`, `m_eAvailAnyRelay` and `m_debugMsg` [V]. Show it in the UI as "Relay network: ready / degraded".

A ping-location string can be stored in lobby data so that joiners can estimate their ping to the host before joining [I/X].

### 4.4 `SteamNetworkingMessages` (`SteamNetworkingMessages.cs`, `SteamNetworkingMessages002`) [V]

```csharp
EResult SendMessageToUser(ref SteamNetworkingIdentity identityRemote, IntPtr pubData, uint cubData, int nSendFlags, int nRemoteChannel); // :7
int ReceiveMessagesOnChannel(int nLocalChannel, IntPtr[] ppOutMessages, int nMaxMessages);   // :13
bool AcceptSessionWithUser(ref SteamNetworkingIdentity identityRemote);                      // :19
bool CloseSessionWithUser(ref SteamNetworkingIdentity); bool CloseChannelWithUser(ref SteamNetworkingIdentity, int); // :25,31
ESteamNetworkingConnectionState GetSessionConnectionInfo(ref SteamNetworkingIdentity, out SteamNetConnectionInfo_t, out SteamNetConnectionRealTimeStatus_t); // :37
```

Callbacks: `SteamNetworkingMessagesSessionRequest_t` (1251, `m_identityRemote`) and `SteamNetworkingMessagesSessionFailed_t` (1252) [V].

The Messages API is connectionless sugar over the same transport [X]. It is good for small out-of-band messages, for example a pre-join "ping/version probe". Use Sockets for the session itself: connection state, poll groups and lanes.

The legacy `SteamNetworking` (`ISteamNetworking`, `SendP2PPacket`; `SteamNetworking.cs`, interface `SteamNetworking006`) is also present. It is deprecated by Valve and should not be used [X].

### 4.5 `SteamNetworkingIdentity` (`SteamNetworkingIdentity.cs`) [V]

```csharp
void SetSteamID(CSteamID)   // :35
CSteamID GetSteamID()       // :40
void SetSteamID64(ulong)    // :45
void SetLocalHost()         // :116
void SetIPAddr(SteamNetworkingIPAddr)
bool IsInvalid()
```

It also supports generic string and byte identities.

### 4.6 `SteamMatchmaking`: lobbies (`SteamMatchmaking.cs`) [V]

```csharp
SteamAPICall_t CreateLobby(ELobbyType eLobbyType, int cMaxMembers);           // :90  -> CallResult<LobbyCreated_t>
SteamAPICall_t JoinLobby(CSteamID steamIDLobby);                               // :96  -> CallResult<LobbyEnter_t>
void LeaveLobby(CSteamID);                                                     // :102
bool InviteUserToLobby(CSteamID steamIDLobby, CSteamID steamIDInvitee);       // :108
int GetNumLobbyMembers(CSteamID); CSteamID GetLobbyMemberByIndex(CSteamID, int); // :114,120
string GetLobbyData(CSteamID, string); bool SetLobbyData(CSteamID, string, string); bool DeleteLobbyData(...); // :126,133,160
void SetLobbyMemberData(CSteamID, string, string); string GetLobbyMemberData(CSteamID, CSteamID, string);      // :174,167
bool SendLobbyChatMsg(CSteamID, byte[], int); int GetLobbyChatEntry(...);      // :182,188
bool RequestLobbyData(CSteamID);                                               // :194
bool SetLobbyMemberLimit(CSteamID, int); bool SetLobbyType(CSteamID, ELobbyType); bool SetLobbyJoinable(CSteamID, bool); // :212,224,230
CSteamID GetLobbyOwner(CSteamID); bool SetLobbyOwner(CSteamID, CSteamID);      // :236,242
SteamAPICall_t RequestLobbyList();  + AddRequestLobbyList{String,Numerical,NearValue,FilterSlotsAvailable,Distance,ResultCount}Filter(...) // :32-78
CSteamID GetLobbyByIndex(int);                                                 // :84
```

- `ELobbyType`: `Private, FriendsOnly, Public, Invisible, PrivateUnique` (`ELobbyType.cs`) [V].
- Callbacks [V]:
  - `LobbyCreated_t` (513: `m_eResult`, `m_ulSteamIDLobby`)
  - `LobbyEnter_t` (504: `m_ulSteamIDLobby`, `m_bLocked`, `m_EChatRoomEnterResponse`)
  - `LobbyChatUpdate_t` (506: member join/leave)
  - `LobbyDataUpdate_t` (505)
  - `LobbyChatMsg_t`, `LobbyInvite_t`, `LobbyKicked_t`, `LobbyMatchList_t`, `LobbyGameCreated_t`
- `k_nMaxLobbyKeyLength = 255` (`Constants.cs:311`) [V].

Because lobbies are scoped to app 949230 [V for the app ID], **all CS2 players share the same lobby namespace** [I]. Tag Skyline Together lobbies with a key such as `st.proto = "SkylineTogether/<protocolVersion>"`, and filter with `AddRequestLobbyListStringFilter("st.proto", ..., k_ELobbyComparisonEqual)`.

### 4.7 `SteamFriends`: rich presence, invites, overlay (`SteamFriends.cs`) [V]

```csharp
bool SetRichPresence(string pchKey, string pchValue);                 // :270
void ClearRichPresence();                                             // :278
string GetFriendRichPresence(CSteamID, string); void RequestFriendRichPresence(CSteamID); // :284,303
bool InviteUserToGame(CSteamID steamIDFriend, string pchConnectString); // :309
void ActivateGameOverlayInviteDialog(CSteamID steamIDLobby);          // :210
void ActivateGameOverlayInviteDialogConnectString(string pchConnectString); // :477
void ActivateGameOverlayToUser(string pchDialog, CSteamID steamID);   // :184
bool GetFriendGamePlayed(CSteamID, out FriendGameInfo_t);             // :57
int GetFriendCount(EFriendFlags); CSteamID GetFriendByIndex(int, EFriendFlags); string GetFriendPersonaName(CSteamID); // :27,33,51
int GetSmall/Medium/LargeFriendAvatar(CSteamID); bool RequestUserInformation(CSteamID, bool); // :216-234
```

Join and invite callbacks [V]:

| Callback | ID | Fields | Fires when |
|---|---|---|---|
| `GameLobbyJoinRequested_t` | 333 | `m_steamIDLobby`, `m_steamIDFriend` | the user accepts a lobby invite, or clicks "Join game" on a friend in a lobby, *while the game is running* |
| `GameRichPresenceJoinRequested_t` | 337 | `m_steamIDFriend`, `m_rgchConnect` (256-byte UTF-8) | "Join game" via the rich-presence `connect` key, or `InviteUserToGame` |
| `FriendRichPresenceUpdate_t` | 336 | | a friend's rich presence changes |
| `NewUrlLaunchParameters_t` | 1014 | | URL launch parameters change |

Related constants [V]: `k_cchMaxRichPresenceKeys = 30`, key length 64, value length 256 (`Constants.cs:81-85`).

Cold start (game not running) [X]: Steam starts the game with `+connect_lobby <lobbyId>` or with the `connect` string on the command line. The mod can read it with `Environment.GetCommandLineArgs()`, or with `SteamApps.GetLaunchCommandLine(out string, int)` (`SteamApps.cs:178`) / `SteamApps.GetLaunchQueryParam(string)` (`SteamApps.cs:146`).

The game's own option parser is a Mono.Options `OptionSet` (`GameManager.cs:352-449`) and records the args at `:449`. Unknown options are only logged, so `+connect_lobby` will not break it [I]. **Open question:** whether Steam's launch of CS2 passes through the Paradox Launcher (`Launcher/launcher-settings.json`, `exeArgs: []`) and whether the launcher forwards extra args. Test this.

### 4.8 The game's own use of rich presence: conflict analysis [V]

- In-game, `RichPresenceUpdateSystem.OnUpdate` runs every 5 s (`kUpdateRate = 5`, `Game/Game.PSI/RichPresenceUpdateSystem.cs:24,141-174`). It writes these Steam keys: `cityname`, `stat`, `population`, `money`, `health`, `happiness`, `milestone`, and `steam_display` (via `SetRichPresence(GetActionKey())` → `SteamFriends.SetRichPresence("steam_display", key)`; `RichPresenceUpdateSystem.cs:306-325`, `SteamworksPlatform.cs:813-827`).
- On every load, `PlatformManager.instance.SetRichPresence(gameMode.ToRichPresence())` also sets `steam_display` (`GameManager.cs:1129`, `PlatformManager.cs:1040-1057`). The handler is skipped when `!hasConnectivity`, that is, when the Paradox API is unreachable (`PlatformManager.cs:154-165,1044`).
- `ClearRichPresence()` is called only on dispose (§2.5).

Consequences:

- **The mod cannot own `steam_display`.** The game overwrites it every 5 s. It is also localised against CO's Steamworks rich-presence token file, so the mod cannot add tokens such as "#Coop" [X].
- The mod **can** safely set `connect` (enables "Join Game" in the friends list) and `steam_player_group` / `steam_player_group_size` (groups party members in the friends list) [X]. These keys are never touched by the game [V].
- That makes about 11 keys in total, under the 30-key limit [V].
- The mod should clear its own keys (`SetRichPresence(key, null or "")`) when the session ends [X].

---

## 5. Recommended transport stack

### 5.1 Layering

```
Game/Sim layer (main thread)     Command apply, snapshot load, ECS mutation        <- §7: main thread only
   ^ ordered inbound queue (ConcurrentQueue<NetMessage>) / outbound queue
Session layer                    Handshake, versions, mod/DLC parity, roles, tick/ack, resync
Channel layer                    Control (reliable-ordered) | State (unreliable) | Bulk (chunked reliable)
Transport abstraction  ITransport
   +- SteamSocketsTransport      ConnectP2P / CreateListenSocketP2P, poll group, SDR relay, encryption  (PRIMARY)
   +- LoopbackTransport          SteamNetworkingSockets.CreateSocketPair(..., bUseNetworkLoopback:true) - tests in 1 process
   +- (optional) DirectTransport ConnectByIPAddress/CreateListenSocketIP (same Steam API, LAN/port-forward), or non-Steam lib
```

### 5.2 Primary: Steam Networking Sockets P2P

- **Host:**
  1. `InitRelayNetworkAccess()` in `OnLoad`.
  2. `CreateListenSocketP2P(virtualPort: 0, ...)` and `CreatePollGroup()`.
  3. On `Connecting`: validate the remote SteamID against the lobby member list, then `AcceptConnection` + `SetConnectionPollGroup`. Otherwise `CloseConnection(..., reason, "not in lobby", false)`.
  4. Store the player slot in `SetConnectionUserData`.
- **Client:** `identity.SetSteamID(lobbyOwner)`, then `ConnectP2P(ref identity, 0, n, opts)`.
- **NAT traversal:** P2P connections by SteamID go through Steam Datagram Relay or ICE automatically. Users never forward ports, and relayed traffic hides IPs [X]. The ICE policy is controlled by `P2P_Transport_ICE_Enable` (104) [V]. Read the effective default at runtime with `GetConfigValue` rather than assuming it [X].
- **Encryption and authentication:** Steam Networking Sockets connections are authenticated by Steam-issued certificates and encrypted end-to-end by default (AES-GCM, Curve25519 key exchange) [X]. The existence of an opt-*out* config `k_ESteamNetworkingConfig_Unencrypted = 34` [V] is consistent with this. **Never set it.** Peer identity (`m_identityPeer`) is trustworthy; use it as the authoritative player ID.
- **Ordering:** reliable messages on one connection (and one lane) arrive in order. Unreliable messages may be dropped or reordered [X].

### 5.3 Channel design

| Channel | Use | Flags | Notes |
|---|---|---|---|
| Control | handshake, player commands (tool actions, policies, budget, speed), host decisions and acks, chat, resync orders | `k_nSteamNetworkingSend_ReliableNoNagle` (9) | Ordered. Use NoNagle for responsiveness; batch per frame manually. |
| State | cursors/camera ghosts, tool previews, sim-time heartbeat, hash beacons | `k_nSteamNetworkingSend_UnreliableNoDelay` (5) or `Unreliable` (0) | Latest-wins, sequence-numbered. Each message under one MTU (~1200 B) to avoid fragmentation loss [X]. |
| Bulk | world snapshot on join or desync recovery, large blobs (custom assets) | `Reliable` (8), **separate lane or separate connection** | Chunked (§5.4). Must not head-of-line-block Control. |

Head-of-line blocking [X]: without lanes, all reliable messages on a connection form one ordered stream. Queueing 300 MB of snapshot would delay every command behind it. There are two fixes:

- **A. Lanes:** `ConfigureConnectionLanes(hConn, 2, priorities[]={0,1}, weights[]={1,1})`. The binding is broken (§5.6); call the flat export yourself.
- **B. A second connection** on virtual port 1, used only for Bulk. It works with the existing binding. It costs a second handshake and has separate congestion control, so the two compete less gracefully.

**Recommendation:** implement A via your own P/Invoke and keep B as a fallback.

### 5.4 Large blob transfer (20–300 MB world snapshots)

Verified constraints and helpers:

- Maximum message size is 512 KiB (`Constants.cs:253`).
- Saves are already ZSTD-compressed per buffer (`SerializerSystem.cs:102`, `BufferFormat` enum at `Colossal.Core/Colossal.Serialization.Entities/BufferFormat.cs:3-8`).
- Native compressors are available:
  - `CompressionUtils.Compress/Decompress(CompressionFormat, NativeSlice<byte>, …)` as Burst jobs (`Colossal.IO.AssetDatabase/Colossal.Compression/CompressionUtils.cs:51-77`);
  - `CompressZstdWithMarker(byte[] arr, int count, int compressionLevel)` / `DecompressZstdWithMarker(byte[])` (`:79-129`);
  - backend `Colossal.AssetPipeline.Native.NativeCompression.CompressData/DecompressData/CompressCalcBound` (binary `Managed/Colossal.AssetPipeline.Native.dll`, names from the string table).
- `System.IO.Hashing` 9.0: `XxHash3`, `XxHash64`, `XxHash128`, `Crc32`, `Crc64` [V].

Protocol (recommended):

1. **Manifest** (Control lane): `snapshotId`, `totalBytes`, `chunkSize` (256 KiB — half the cap, leaving header room), `chunkCount`, `XxHash128` of the whole blob, the host sim tick, and the compression flag.
2. **Chunks** (Bulk lane, `Reliable`): `[snapshotId u32][index u32][len u32][XxHash64 u64][payload]`.
3. **Back-pressure:** the sender keeps `m_cbPendingReliable` (from `GetConnectionRealTimeStatus`) below a window, for example 2–4 MiB, and tops up once per frame or from the network thread. This avoids `k_EResultLimitExceeded` from an overfull send buffer [X], keeps memory bounded, and lets Control traffic interleave.
4. **Receiver:** writes chunks straight to a temp file (`FileStream`, preallocated with `SetLength`) to avoid holding 300 MB in managed memory (the GC concern from §1). It verifies the per-chunk hash, then the whole-blob hash, then hands the file to the save-load path. Loading itself is covered in other research documents [I].
5. **Resume/retry:** because chunks are indexed, a reconnect can request "missing ranges" [I].
6. **Catch-up:** during transfer the host keeps simulating and buffers the post-snapshot command log. The client replays it after load (design in the sync documents).

Throughput tuning [X]:

- Raise the Bulk connection's `SendBufferSize` (9) to, for example, 8–16 MiB, and its `SendRateMax` (11).
- Bandwidth estimation decides the actual rate, and relay throughput is not guaranteed.
- Expose an estimate in the UI from `m_flOutBytesPerSec`.

Rough math [I]: 300 MB at 5 MB/s is about 60 s; at 1 MB/s it is about 5 min. Reducing the snapshot (excluding derivable or cached data) matters more than transport tuning.

### 5.5 Threading model for the transport

`ISteamNetworkingSockets` calls are thread-safe and guarded by an internal lock [X]. Recommended model:

- **Network thread** (dedicated `Thread`, `IsBackground = true`, ~1–2 ms sleep or `SpinWait`):
  - `ReceiveMessagesOnPollGroup` → parse into pooled buffers → `ConcurrentQueue` per channel;
  - drains the outbound queue → `SendMessageToConnection`;
  - pumps the Bulk window.
  
  This decouples network latency and ack processing from a 20 FPS main thread. Use `k_nSteamNetworkingSend_UseCurrentThread` (16) only when the send must happen synchronously [X].
- **Main thread:** consumes inbound queues at a fixed point in the frame and applies messages to ECS. A mod `GameSystemBase` early in `SystemUpdatePhase.MainLoop`, or a `Modification` phase, is suitable (system ordering is covered in other docs). Steam *callbacks* (connection status, lobby) still arrive on the main thread via the game's pump [V], so route them into the same session state machine.
- Never touch `EntityManager`, `World` or game managed singletons from the network thread (§7).

### 5.6 Steamworks.NET binding defects to work around

1. **`ConfigureConnectionLanes`** [V]: the wrapper is declared `(HSteamNetConnection hConn, int nNumLanes, out int pLanePriorities, out ushort pLaneWeights)` (`SteamNetworkingSockets.cs:138-142`, P/Invoke `NativeMethods.cs:2075-2076`). The native function reads `nNumLanes` elements from arrays, but `out int` passes a pointer to a single int. With `nNumLanes > 1` the native side reads past the variable, which is undefined behaviour [I].

   Fix: declare your own import. The flat accessor exports `SteamAPI_SteamNetworkingSockets_SteamAPI_v012` and `SteamAPI_ISteamNetworkingSockets_ConfigureConnectionLanes` are present in `steam_api64.dll` [V]. `NativeMethods` and `CSteamAPIContext` are `internal` (`NativeMethods.cs:8`, `CSteamAPIContext.cs:5`) [V], so the interface pointer must come from the flat accessor:
   ```csharp
   [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
   static extern IntPtr SteamAPI_SteamNetworkingSockets_SteamAPI_v012();
   [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
   static extern EResult SteamAPI_ISteamNetworkingSockets_ConfigureConnectionLanes(
       IntPtr self, HSteamNetConnection hConn, int nNumLanes, int[] pLanePriorities, ushort[] pLaneWeights);
   ```
   `steam_api64` is already loaded in the process, so the mod's `DllImport` resolves to the same module [I].

2. **`SendMessages`** [V]: the wrapper marshals `SteamNetworkingMessage_t[]` *by value* (`NativeMethods.cs:2048-2049`). The native API takes `SteamNetworkingMessage_t* const*`, an array of pointers from `AllocateMessage` [X]. Treat it as unusable. Use `SendMessageToConnection` (it copies the buffer, which is fine), or your own import with `IntPtr[]`.

3. **`GetConnectionRealTimeStatus`** requires a `ref SteamNetConnectionRealTimeLaneStatus_t` even when `nLanes == 0` [V]. Pass a dummy local.

4. **`SteamNetworkingMessage_t.FromIntPtr`** allocates through reflection marshaling [V]. Read fields with `unsafe` code in hot paths.

### 5.7 Fallbacks and alternatives (not primary)

- **Direct IP using the same Steam API:** `CreateListenSocketIP` / `ConnectByIPAddress` [V signatures]. This still requires Steam running. It covers LAN or port-forwarded play when the relay is impaired. Unauthenticated IP connections need `IP_AllowWithoutAuth` (23) [V enum]. It remains encrypted, but unauthenticated [X].
- **Raw `System.Net.Sockets`** [V availability; see §6]: possible for a non-Steam build. You must implement reliability, fragmentation, congestion control, encryption and NAT punching yourself. Options:
  - Bundle a managed library such as LiteNetLib (MIT, UDP reliable/unreliable channels, NAT punch via an introducer server; no encryption) [X].
  - Bundle Valve's open-source GameNetworkingSockets native DLL, which has the same API shape and encryption but no Steam relay [X].
- **Epic Online Services P2P** [X]: free, cross-store relay and NAT traversal, with device-ID login so no Epic account is needed. It requires registering a Skyline Together product (client ID and secret embedded in the mod) and shipping the EOS SDK native DLL. It is the most practical path if Game Pass and Steam players must play together. This is a product decision; none of it is in the game files.
- **Discord Game SDK:** `DiscordSDK.dll` contains `Discord.LobbyManager`, `Discord.NetworkManager`, `ActivityManager`, `ActivitySecrets` and `ActivityJoinHandler` types [V, string table]. However:
  - the game's PSI only uses `ActivityManager.UpdateActivity` [V, `Colossal.PSI.Discord.dll` strings];
  - it runs under CO's client ID 1125009418476605441 (`PlatformSupport.cs:14-16`) [V];
  - Discord deprecated the Game SDK lobby and networking features [X].

  **Do not use it for transport.** At most, Discord rich presence could show "Co-op" via the game's existing `DiscordRichPresence` PSI. The game's `RichPresenceUpdateSystem` overwrites it every 5 s in-game (`:303,323`) [V].

---

## 6. .NET networking in the Mono runtime (verified by binary inspection)

| Capability | Evidence | Status |
|---|---|---|
| `System.Net.Sockets` (`Socket`, `UdpClient`, `TcpClient`, `TcpListener`, `SocketAsyncEventArgs`, `NetworkStream`, `IPEndPoint`, `ReceiveFromAsync`, `SendToAsync`) | type-name strings in `Managed/System.dll` (4.6.57) | **available** [V] |
| DNS and ICMP ping | used by the game: `Dns.GetHostEntryAsync`, `Ping.SendPingAsync` (`Connectivity.cs:79,101`) | **works at runtime** [V] |
| `SslStream` (TLS) | `System.dll` string; `UnityEngine.TLSModule.dll` present (Unity TLS provider) | available [V presence; runtime behaviour I] |
| `System.Net.Http` (`HttpClient`) | `Managed/System.Net.Http.dll` 4.6.57; the game also uses `UnityWebRequest` (`Game/Game/UnityWebRequestExtensionMethods.cs`) | available [V] |
| `HMACSHA256`, `SHA256`, `RandomNumberGenerator`, `Aes` | `mscorlib.dll` / `System.Core.dll` (`AesManaged`, `AesCryptoServiceProvider`) strings | available [V presence] |
| `AesGcm`, `ECDiffieHellman(Cng)` | names present in `mscorlib.dll` / `System.Core.dll` | **uncertain**: Mono often throws `PlatformNotSupportedException` for these [I/X]. Not needed when using Steam transport. |
| `BrotliStream` | type in `System.IO.Compression.dll`, which binds a native `CompressionNative` library; `MonoPosixHelper.dll` contains zlib strings but no Brotli symbols | **likely broken** [I]. Use the game's native ZSTD/LZ4 or `DeflateStream`. |
| `DeflateStream` / `GZipStream` | `System.IO.Compression.dll`; zlib inside `MonoPosixHelper.dll` | available [V presence] |
| `System.IO.Hashing` 9.0 (XxHash3/64/128, Crc32/64) | `Managed/System.IO.Hashing.dll` FileVersion 9.0.24.52809 | available [V] |
| `System.Memory` (`Span<T>`, `MemoryMarshal`) | `Managed/System.Memory.dll` 4.0.99.0, netstandard 2.1 | available [V] |
| Game-native LZ4/ZSTD | `CompressionUtils` (§5.4) | available [V] |

---

## 7. Threading constraints (ECS and game)

- **ECS main-thread rule.** `EntityDataAccess.AssertMainThread()` and the structural-change checks are `[Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]` / `[Conditional("UNITY_DOTS_DEBUG")]` (`Unity.Entities/Unity.Entities/EntityDataAccess.cs:316-339`). In the shipped player these calls are **compiled away**, so cross-thread `EntityManager` use will not throw. It races with jobs and the main thread and corrupts chunks [V for the attribute; consequence I]. Every ECS write must be applied on the main thread inside a system update, honouring job dependencies (`CompleteDependency` and similar; see the ECS research doc).
- **`MainThreadDispatcher` (`Colossal.Core/Colossal.Core/MainThreadDispatcher.cs`)** [V]:
  - `isMainThread` compares against the thread ID captured in `Initialize()`, which `GameManager.Awake` calls (`GameManager.cs:537`).
  - `RunOnMainThread(Action)` runs inline on the main thread. Otherwise it calls `RegisterUpdater`, which stores the action in a `ConcurrentDictionary<Guid, Func<bool>>` (`:16,32-42,106-116`). Updaters run in `UpdateUpdaters()` (`:85-95`), called at `GameManager.cs:723`.
  - **The dictionary gives no ordering, and every registration writes a debug log line** (`:112`). Do not use it per network message.
  - `WaitXFrames` mutates a plain `List` (`:18,44-49`) and is **not thread-safe**. Call it only from the main thread.
- **`PlatformManager` main-thread queue** (`m_MainThreadActionQueue`, `PlatformManager.cs:66,690-700`) is private. The mod should keep its own `ConcurrentQueue<T>`, drained by its own system.
- **Steam callbacks** arrive on the main thread (§2.3), so lobby and connection-state logic can touch game state directly from the handler. ECS writes should still be deferred to the mod's system update so they happen at a deterministic point in the frame [I].
- **Async/await caution:** the game uses `ConfigureAwait(false)` in platform code (`GameManager.cs:1676-1678`). Any mod `await` without a synchronization context may resume on the thread pool [I]. Re-check `MainThreadDispatcher.isMainThread` before touching game state.

---

## 8. Other platform services useful for "perfectly synced" sessions

- **Mod parity via PDX SDK** [V]. Get the instance with `PlatformManager.instance.GetPSI<PdxSdkPlatform>("PdxSdk")`.
  - `Task<HashSet<Colossal.PSI.Common.Mod>> GetModsInActivePlayset()` (`PdxSdkPlatform.cs:1614-1657`) returns id, displayName, version and userModVersion.
  - `Task<RequestReport> SubscribeAll(List<string> modIds)` (`:1753-1768`) enables the mods in the active playset and bulk-subscribes.
  - Also `EnableModInActivePlayset(string modId, bool enable)` (`:1676`) and `GetModDetails(string modId, string version)` (`:1705`).
  
  This enables a join flow: "host sends mod manifest → client diff → one-click subscribe → restart". Restart is required for code mods [I].
- **DLC parity** [V]: `PlatformManager.instance.IsDlcOwned(DlcId)` (`PlatformManager.cs:867`). On Steam it maps to `SteamApps.BIsDlcInstalled` (`SteamworksPlatform.cs:641-649`). Include DLC IDs in the handshake.
- **User identity** [V]: `SteamUser.GetSteamID()` (used by `SteamworksPlatform.userSpecificPath`, `:145-155`), `SteamFriends.GetPersonaName()` (`:127-137`), and avatars via `SteamworksPlatform.GetAvatar(AvatarSize)` (`:843-904`, returns RGBA flipped and premultiplied).
- **Connectivity signal** [V]: `PlatformManager.instance.hasConnectivity` / `onConnectivityStatusChanged` (`PlatformManager.cs:154-165,216`). It reflects only Paradox API reachability, **not** Steam relay health. Use `GetRelayNetworkStatus` for that.

---

## 9. Non-Steam stores (Xbox app / Microsoft Store / PC Game Pass)

- **Verified:**
  - This install ships the GDK managed wrapper `Unity.Microsoft.GDK.dll`. Its string table includes `XblMultiplayerSessionHandle`, `XblMultiplayerManagerMember`, `XblMultiplayerActivityInfo`, `XGameInviteRegistrationToken`, `XblSocialManager*` and `XUserHandle`. The native thunks are in `Plugins/x86_64`.
  - This `Game.dll` unconditionally registers the Steam and Discord PSIs (`GameManager.cs:1676-1677`) and contains no GDK PSI (grep found no `XGameRuntime`/GDK references in `Game`).
  - The Game Pass build therefore uses a different `Game.dll` or PSI set that is not available here.
- **Inferred / external:**
  - **No Steam relay:** `steam_api` will not initialise there, so `SteamworksPlatform` would fail and `InteropHelp.TestIfAvailableClient` would throw. The mod must detect this and select another transport.
  - **Xbox Live multiplayer (MPSD, invites, activities)** depends on the title's Partner Center service configuration: session templates and multiplayer privileges. CS2 is single-player, so those are probably not configured. A mod cannot add them. **Do not plan on XblMultiplayer** [X].
  - **Viable cross-store options:**
    1. EOS P2P (relay plus NAT traversal; requires your own EOS product);
    2. a self-hosted relay or introducer, with LiteNetLib or GameNetworkingSockets on top;
    3. direct IP with port-forwarding as a power-user option.
    
    Identity on Game Pass could come from an Xbox gamertag via the GDK. That is unverified and needs the Game Pass build to check.
  - Whether code mods load at all on the Game Pass build needs confirmation. The PDX mods pipeline is shared, but this was not verifiable here.

---

## 10. Recommendations

1. **Transport:** `SteamSocketsTransport` behind an `ITransport` interface. One P2P listen socket and poll group on the host; one connection per client with 2 lanes (Control + Bulk) via your own `ConfigureConnectionLanes` P/Invoke; unreliable state on lane 0 with the Unreliable flags.
2. **Lobby:** `CreateLobby(k_ELobbyTypeFriendsOnly | Private | Public, maxMembers)`. Lobby data:
   - `st.proto` (protocol version)
   - `st.game` (game version string)
   - `st.mods` (hash of the mod manifest)
   - `st.ping` (`ConvertPingLocationToString` output)
   - `st.city`
   
   Set rich presence `connect` = `+connect_lobby <id>` and `steam_player_group` = lobby ID. Invites go through `ActivateGameOverlayInviteDialog(lobby)`. Handle `GameLobbyJoinRequested_t` and `GameRichPresenceJoinRequested_t`, plus command-line parsing at `OnLoad` for cold starts.
3. **Session handshake (Control):** protocol version → game version → DLC set → mod manifest (PDX IDs and versions, plus local mod hashes) → accept/deny with a reason, plus an auto-fix offer (`SubscribeAll`).
4. **Snapshot:** chunked Bulk transfer with a manifest, back-pressure on `m_cbPendingReliable`, `XxHash128` integrity, streaming to a temp file, resume support, and host-side command buffering for catch-up.
5. **Threads:** a network I/O thread, a main-thread apply system, Steam callbacks on the main thread, and no ECS access off the main thread.
6. **Testing:** `CreateSocketPair` loopback in-process, plus `FakePacketLoss/Lag/Reorder/Dup` config values for chaos tests. Two machines or accounts are needed for true P2P because of `single-instance=`.
7. **Telemetry HUD:** ping, quality, in/out bytes per second and queue time from `GetConnectionRealTimeStatus`, plus relay status.

---

## 11. Risks

- The binding defects (lanes, `SendMessages`) need custom P/Invoke. A future game update could ship a different Steamworks.NET or SDK version. Re-check the flat export names (`…_v012`) on every game patch [V for the current names].
- The game's 5 s rich-presence overwrite owns `steam_display`, so the mod cannot show custom friends-list status text [V/X].
- Main-thread pump frequency is tied to FPS. Callback-driven logic (connection state, lobby) sees up to one frame of extra latency; message polling should be independent of it [V/I].
- Large cities make snapshots tens to hundreds of MB, and relay throughput is unknown and variable [X]. The join time could be minutes.
- ECS thread checks are compiled out, so threading bugs corrupt silently [V].
- There is no Steam on Game Pass: cross-store play needs a third-party relay plus your own identity and credentials [I/X].
- The Paradox Launcher may swallow Steam's `+connect_lobby` args on cold start [open].
- Unity single-instance blocks local two-client testing [V].
- `Connectivity`/`hasConnectivity` gate the game's rich presence. If the Paradox API is unreachable, the game skips `SetRichPresence`, but the mod's own direct `SteamFriends.SetRichPresence` calls are not gated [V].

## 12. Open questions (need runtime checks)

1. Does Steam "Join Game" from the friends list (cold start) reach `Cities2.exe` with `+connect_lobby`, or does the Paradox Launcher drop it?
2. What are the default `SendRateMin`, `SendRateMax`, `SendBufferSize` and `P2P_Transport_ICE_Enable` values in this `steam_api64` build? Query them with `GetConfigValue`.
3. What sustained SDR relay throughput can we expect between typical peers, and does ICE direct connectivity succeed for most friends?
4. Does `Application.runInBackground` stay on when the host alt-tabs? No code sets it (grep empty), so it depends on player settings. If the host's frames stall, the simulation stalls for everyone.
5. Do `AesGcm` and `ECDiffieHellmanCng` work under this Mono build? This only matters for a non-Steam fallback.
6. What does the Game Pass build look like: is there a GDK PSI, do code mods load, and can the mod access GDK identity?
7. Does the save loader accept a file from a temp path (`AsyncReadDescriptor`) without registering it as a user save? This belongs to the serialization research doc.
