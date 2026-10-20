# Integrating your mod with Skyline Together

Skyline Together is a host-authoritative co-op mod: the host runs the simulation, clients follow it and send their
edits to the host as proposals. Most mods work in a session without doing anything (see
[`design/MOD-COMPATIBILITY.md`](design/MOD-COMPATIBILITY.md) for what happens automatically). This page is for mod
authors who want to tell Skyline Together what their mod does, or react to sessions and exchange data between players.

**No reference to Skyline Together is needed.** Everything is matched by name and read by reflection: you copy a
small attribute class and/or a static class into your mod. When Skyline Together is not installed, nothing happens and
nothing breaks. Invalid declarations never fail; they are listed in the log and in the diagnostics dump (Skyline
Together panel, "Write diagnostics"), under "Mod compatibility".

API version: **1**.

## 1. What Skyline Together does without you

| Your mod has ... | In a session ... |
|---|---|
| component types that implement the game's `ISerializable` / `IEmptySerializable` | replicate with their entity (entity references as network ids); changes made without an `Updated` tag are found by a watcher; enabled bits replicate |
| component types the game does not save | stay local to each machine (never sent) |
| systems in `GameSimulation` | run on the host only, like every vanilla simulation system |
| systems in `PreSimulation`, `PostSimulation`, `LoadSimulation` | run on the host only (they run with the simulation step) |
| systems in `Modification1` ... `ModificationEnd`, tool, UI or rendering phases | run on every machine (derivation from replicated data, like vanilla) |
| tools (`ToolBaseSystem`) | a client's changes while your tool is active are proposed to the host ("direct-write"), with an edit hold so the host's echo does not snap a dragged object back |
| prefabs created at runtime | bind across machines by prefab id, or by type and name when the name is deterministic; players without them get a message naming your mod |
| settings | stay per player |

Join check: a mod that can change the shared world must be on every machine in the same version. Skyline Together
decides this from a scan of your assembly (saved components, saved systems, prefab types, runtime prefabs, tools,
systems in world phases, Harmony patches outside UI code). A mod with none of these never blocks a join.

## 2. Declarations

Copy this attribute into your mod (any namespace, any accessibility). It is read as metadata, never constructed:

```csharp
using System;

[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property, AllowMultiple = true)]
internal sealed class SkylineTogetherAttribute : Attribute
{
    public SkylineTogetherAttribute(string role) { }
    public SkylineTogetherAttribute(string role, string target) { }
}
```

Put it on your types, on properties of your `ModSetting` class, or on the assembly (then with a target, a full type
name; nested types use `+`):

```csharp
[assembly: SkylineTogether("world")]
[assembly: SkylineTogether("join-off", "MyMod.Systems.ApplyStartingBonusSystem")]

namespace MyMod
{
    [SkylineTogether("tool-local")]          // entities carrying it are tool previews: never sent
    public struct BrushPreview : IComponentData { public Entity m_Target; }

    [SkylineTogether("never-replicate")]     // saved by the game, but a per-machine cache: never sent
    public struct PathCache : IComponentData, ISerializable { /* ... */ }

    [SkylineTogether("host-only")]           // decides shared state: the host's result replicates
    public partial class GrowthSystem : GameSystemBase { /* ... */ }

    [SkylineTogether("everywhere")]          // a GameSimulation system that only feeds local visuals
    public partial class SeasonalColorSystem : GameSystemBase { /* ... */ }

    [SkylineTogether("pipeline")]            // applies through CreationDefinition/Temp like vanilla tools
    public partial class MyPlacementTool : ToolBaseSystem { /* ... */ }

    public class Setting : ModSetting
    {
        [SkylineTogether("host-setting")]    // the host's value applies to everyone in a session
        public float GrowthRate { get; set; }
    }
}
```

| Role | On | Effect |
|---|---|---|
| `replicate` | component | Documents (and checks) that it replicates. Only types the game saves can replicate; on any other type this is reported as a problem. |
| `never-replicate` | component | Never sent, although the game saves it; the entity still replicates. |
| `tool-local` | component | An entity carrying it never replicates (tool, overlay or preview entities). |
| `host-only` | system | Off on clients for the whole session. |
| `everywhere` | system | Kept on clients, even in `GameSimulation`. Only for systems that write no replicated state. |
| `join-off` | system | Off on a client from the start of its join: load-time code that changes the city from the player's own settings (the host's city arrives already processed). |
| `direct-write` | tool | The default for every tool: changes made while it is active are proposed as direct writes. |
| `pipeline` | tool | The tool changes the world only through the game's Temp/apply pipeline; its applies are captured like vanilla ones. |
| `host-setting` | setting property | The host's value is used by everyone during a session; each player's own value comes back afterwards. bool, int, long, float, double, enum and string properties with a public getter and setter. |
| `client-local` | assembly | Your mod only affects its own player (UI, tooltips, overlays); it never blocks a join. Takes precedence over the scan, with one exception: if your mod saves component types or system state with the city, a player without it cannot load the host's city (the game crashes), so a joiner that lacks it is always refused, whatever you declare ([mods that store data in the city](design/MOD-COMPATIBILITY.md#10-mods-that-store-data-in-the-city)). |
| `world` | assembly | Your mod must be on every machine in the same version. |
| `viewer-message` | assembly, with a target: a message kind `0` ... `255` (decimal or `0x` hex), or `*` | Viewers may send mod channel messages whose first payload byte is that kind (`*`: every message). Without it a viewer only receives (see [4](#4-the-mod-channel)). |

Roles are case-insensitive; `host_only` and `Host Only` also work. Contradicting roles on one target (`host-only` and
`everywhere`, `replicate` and `never-replicate`, ...) cancel each other: neither applies and the conflict is reported.

## 3. The integration class

For session events, the channel, or to declare things as text, add a static class named exactly
`SkylineTogetherIntegration` (any namespace). Every member is optional:

```csharp
using System;

internal static class SkylineTogetherIntegration
{
    // the API version this class is written for (a const, read without running your code)
    public const int ApiVersion = 1;

    // the same roles as the attribute, as "role: target" lines (handy for types you cannot annotate)
    public static readonly string[] Declarations =
    {
        "host-only: MyMod.Systems.GrowthSystem",
        "host-setting: MyMod.Setting.GrowthRate",
        "viewer-message: 1",                       // viewers may send messages starting with byte 1 (see section 4)
    };

    // set by Skyline Together (they stay null when it is not installed: always use ?.Invoke)
    public static Func<bool> IsInSession;          // hosting, or joined and following the host
    public static Func<bool> IsHost;
    public static Func<bool> IsClient;
    public static Func<int> LocalPlayer;           // 0 = host, 1 ... 15 = clients (lowest free id first; freed ids are reused), -1 outside a session
    public static Func<byte[], int, bool> Send;    // payload, target: -1 = everyone, 0 = host, n = player n

    // called by Skyline Together on the main thread
    public static void OnSessionStarted(bool isHost) { }    // hosting started, or this client is live in the city
    public static void OnSessionEnded() { }
    public static void OnMessage(int fromPlayer, byte[] payload) { }
}
```

The delegate fields must be `static`, not `readonly`, and of exactly these types; callbacks must be `static` with
exactly these parameters (one overload each). A field or method with another shape is reported and skipped.

Typical use:

```csharp
bool inSession = SkylineTogetherIntegration.IsInSession?.Invoke() == true;
bool isClient = SkylineTogetherIntegration.IsClient?.Invoke() == true;
if (isClient)
{
    // e.g. skip a one-off "apply to all buildings" button on clients and ask the host instead
    SkylineTogetherIntegration.Send?.Invoke(new byte[] { 1 }, 0);
}
```

## 4. The mod channel

`Send(payload, target)` sends opaque bytes to your own mod on other machines. Your channel is your assembly name, so
only your mod receives what you send; a machine without your mod drops it silently.

- Clients only talk to the host; the host relays. A client's message to everyone (`-1`) reaches the host and every
  other client; `0` reaches the host; `n` reaches player `n` through the host. Nothing comes back to the sender.
- `fromPlayer` in `OnMessage` is set by the host, so a client cannot pretend to be someone else.
- Roles: the host, co-hosts and builders may send. Viewers (the read-only role) receive everything but may send only
  the message kinds your mod declares with `viewer-message` (the kind is the first byte of the payload; `*` allows
  every message). A viewer's `Send` of another kind returns `false`, and the host drops such a message from a viewer
  whatever the client did. The host's copy of your mod decides, so declare the same kinds in every version you ship.
  Typical use: a viewer may *ask* the host for something (`viewer-message: 1` for a "request state" message), while
  the messages that change things stay with builders.
- At most **16 KiB** per message, and **30 messages / 64 KiB per second** per sender (with one second of burst).
  `Send` returns `false` (and sends nothing) when not in a session or over a limit, and on the host also for an unknown
  player (a client cannot check the target; the host drops a message to an unknown player). The host drops a client's
  messages beyond the budget.
- Delivery is reliable and in order per sender. Messages are not stored: a player who joins later does not receive
  earlier ones (send your state in `OnSessionStarted` on the client's request, for example).
- The host is the authority: when a client asks for a change, apply it on the host and let replication (saved
  components) or a host broadcast carry the result.

## 5. Robustness rules

- Nothing you declare or do can throw into the game or into a session. Unreadable declarations, unknown roles, wrong
  targets and contradictions are reported, then ignored.
- A callback that throws is logged. After 3 failures your message callback is switched off for the rest of the session;
  the session start and end callbacks are always called.
- Declarations are read once, when the game has loaded every mod. Host and clients read the same declarations from the
  same version of your mod, so they decide the same.
- A newer `ApiVersion` than Skyline Together knows is reported; the parts it knows still apply.
- Prefabs you create at runtime should get the same name on every machine for the same configuration; then they bind
  across machines even when their asset hash differs.
