using System;
using Colossal.IO.AssetDatabase;
using Game;
using Game.Common;
using Game.Modding;
using Game.SceneFlow;
using Game.Serialization;
using Game.Simulation;
using Unity.Entities;
using SkylineTogether.Core;
using SkylineTogether.Diagnostics;
using SkylineTogether.Presence;
using SkylineTogether.Session;
using SkylineTogether.Settings;
using SkylineTogether.Sync;
using SkylineTogether.Systems;
using SkylineTogether.UI;
using Steamworks;
using UnityEngine;
using ILog = SkylineTogether.Core.ILog;

namespace SkylineTogether
{
    /// <summary>
    /// Entry point. The game creates this class without running its constructor (R05 §1), so everything is set up in
    /// <see cref="OnLoad"/> and kept in static fields.
    /// </summary>
    public sealed class Mod : IMod
    {
        public const string Name = "SkylineTogether";

        public static ILog Log { get; private set; }

        public static SkylineTogetherSettings Settings { get; private set; }

        public static SessionManager Sessions { get; private set; }

        public static SyncController Sync { get; private set; }

        /// <summary>Remote presence (cursors, tools, name tags).</summary>
        public static PresenceController Presence { get; private set; }

        /// <summary>The client's follower clock.</summary>
        public static FollowerClock Clock { get; private set; }

        /// <summary>Vehicle and creature replication.</summary>
        public static MoverSync Movers { get; private set; }

        /// <summary>Steam friends: lobby, rich presence, the invite dialog, direct invites, the friends list, invite joins.</summary>
        public static SteamInvites Invites { get; private set; }

        /// <summary>Solo testing: "Record test session" and "Replay &amp; verify" (ARCHITECTURE.md#solo-test-record-and-replay).</summary>
        public static SkylineTogether.Replay.ReplayController Replay { get; private set; }

        /// <summary>Join, leave and progress toasts.</summary>
        public static SessionToasts Toasts { get; private set; }

        private static GameObject _driver;

        public void OnLoad(UpdateSystem updateSystem)
        {
            // The first launch must never crash the game or break single player: every step is isolated, nothing is
            // rethrown, and what failed is in the log, in the self-test report and in the diagnostics dump.
            var log = new GameLog(Name);
            Log = log;
            log.Info(LogCategory.Session, "Skyline Together " + typeof(Mod).Assembly.GetName().Version + " loading");
            try
            {
                log.Info(LogCategory.Session, "build " + Session.GameInfoProvider.BuildText + " (the installer shows the same id; players must match it)");
            }
            catch (Exception ex)
            {
                log.Warn(LogCategory.Session, "could not compute the build id", ex);
            }
            // where this copy was loaded from (local Mods folder or Paradox Mods) and a warning when it is installed twice
            Step(log, "install check", () => InstallInfo.CheckAtStartup(log));
            Step(log, "settings", () =>
            {
                Settings = new SkylineTogetherSettings(this);
                Settings.RegisterInOptionsUI();
                LocaleSource.RegisterAll(Settings); // every embedded Data/locale.<id>.json, English as the fallback
                AssetDatabase.global.LoadSettings(Name, Settings, new SkylineTogetherSettings(this));
            });
            Step(log, "Steam relay network", () =>
            {
                try
                {
                    SteamNetworkingUtils.InitRelayNetworkAccess();
                }
                catch (Exception ex)
                {
                    log.Warn(LogCategory.Net, "Steam relay network unavailable (is this the Steam version of the game?)", ex);
                }
            });
            // the self-test (ARCHITECTURE.md#feature-health-and-guards): types, private members, Steam; features whose probe fails stay off
            Step(log, "self-test", () => Probes.RunStartup(log));
            // P-REFS: every type and member reference of the mod resolved once on a background thread, so a game update that
            // removed a member the mod uses switches that feature off now instead of throwing mid-session (reported through
            // Probes.Tick)
            Step(log, "reference check", () => ReferenceProbe.Start(log));

            bool core = Step(log, "session and replication", () =>
            {
                if (Settings == null) throw new InvalidOperationException("the settings did not load");
                Sessions = new SessionManager(Settings, log);
                Sync = new SyncController(Sessions, log);
                Presence = new PresenceController(Sessions);
                // follower clock (time runs, gameplay simulation off) and mover replication
                Clock = new FollowerClock(Sessions, log);
                Movers = new MoverSync(Sessions, log);
                Toasts = new SessionToasts(Sessions);
                Invites = new SteamInvites(Sessions, Settings, log);
                Replay = new SkylineTogether.Replay.ReplayController(Sessions, log); // solo testing
                Compat.ModIntegration.Attach(Sessions, log); // session events and the channel for other mods
            });
            if (!core)
            {
                Features.Session.SetProbeResult(false, "the session layer did not start (see the log)");
                Features.Registry.Touch();
            }

            // identity: the bindings travel in the join snapshot (the game's own system serializer; other saves carry an empty block)
            Step(log, "net id system", () =>
            {
                updateSystem.UpdateAt<NetIdSystem>(SystemUpdatePhase.MainLoop);
                updateSystem.UpdateAfter<PostDeserialize<NetIdSystem>>(SystemUpdatePhase.Deserialize);
                World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<SerializerSystem>()?.SetDirty();
            });

            // every system below returns at once without a session (single player costs nothing)
            Step(log, "replication systems", () =>
            {
                // replication: apply right after EndFrameBarrier, capture right before PrepareCleanUpSystem
                updateSystem.UpdateAfter<SyncApplySystem, EndFrameBarrier>(SystemUpdatePhase.MainLoop);
                updateSystem.UpdateBefore<SyncCaptureSystem, PrepareCleanUpSystem>(SystemUpdatePhase.MainLoop);
                updateSystem.UpdateBefore<SyncToolApplySystem>(SystemUpdatePhase.ApplyTool);
                updateSystem.UpdateBefore<SyncBindSystem, SaveGameSystem>(SystemUpdatePhase.MainLoop);
                // viewers' tools never apply (no-predict gate, right before ToolOutputSystem)
                updateSystem.UpdateBefore<ViewerGateSystem, Game.Tools.ToolOutputSystem>(SystemUpdatePhase.ToolUpdate);
                // client icons are host data; filter local producers right before IconCommandSystem
                updateSystem.UpdateBefore<IconAuthorityFilterSystem, Game.Notifications.IconCommandSystem>(SystemUpdatePhase.ModificationEnd);
                // clients: local chirps dropped right before CreateChirpSystem consumes its queue (it stays enabled)
                updateSystem.UpdateBefore<ChirpDrainSystem, Game.Triggers.CreateChirpSystem>(SystemUpdatePhase.ModificationEnd);
                // clients: the reference clean-up of local deletions (the game's removal systems are off on clients)
                updateSystem.UpdateAt<RemovalCascadeSystem>(SystemUpdatePhase.ModificationEnd);
                // no created entity reaches the game's update-group system without its marker
                updateSystem.UpdateBefore<UpdateFrameGuardSystem, Game.Simulation.UpdateGroupSystem>(SystemUpdatePhase.Modification5);
                // host and clients: street lists, road connections and node and edge lists never name a destroyed entity when the game indexes
                // them (checked right before, cleared at the end of the Modification phases; ARCHITECTURE.md#derived-net-links)
                updateSystem.UpdateBefore<NetReferenceGuardSystem, Game.Net.ReferencesSystem>(SystemUpdatePhase.Modification2B);
                updateSystem.UpdateBefore<AggregateLinkGuardSystem, Game.Net.AggregateSystem>(SystemUpdatePhase.Modification2B);
                updateSystem.UpdateBefore<RoadConnectionGuardSystem, Game.Buildings.RoadConnectionSystem>(SystemUpdatePhase.Modification4B);
                updateSystem.UpdateBefore<AggregateMeshGuardSystem, Game.Rendering.AggregateMeshSystem>(SystemUpdatePhase.PreCulling);
                updateSystem.UpdateAfter<DerivedLinkCleanupSystem, Game.Common.ModificationEndBarrier>(SystemUpdatePhase.ModificationEnd);
            });
            Step(log, "mover systems", () =>
            {
                // host capture before SaveGameSystem, client apply after the structure apply, client
                // motion writer after PrepareRendering and before PreCullingSystem reads the frames
                updateSystem.UpdateBefore<MoverCaptureSystem, SaveGameSystem>(SystemUpdatePhase.MainLoop);
                updateSystem.UpdateAfter<MoverApplySystem, SyncApplySystem>(SystemUpdatePhase.MainLoop);
                updateSystem.UpdateBefore<MoverWriteSystem, Game.Rendering.PreCullingSystem>(SystemUpdatePhase.PreCulling);
            });
            Step(log, "speed authority", () =>
            {
                // speed authority right before the simulation consumes the speed (R05 §8)
                updateSystem.UpdateBefore<SpeedAuthoritySystem, SimulationSystem>(SystemUpdatePhase.LateUpdate);
            });
            Step(log, "panel bindings", () => updateSystem.UpdateAt<SessionUISystem>(SystemUpdatePhase.UIUpdate));
            Step(log, "solo testing bindings", () => updateSystem.UpdateAt<ReplayUISystem>(SystemUpdatePhase.UIUpdate));
            Step(log, "invite bindings", () => updateSystem.UpdateAt<InviteUISystem>(SystemUpdatePhase.UIUpdate)); // friends list, overlay state
            Step(log, "presence system", () =>
            {
                // remote cursors drawn into the overlay buffer right before it renders (ARCHITECTURE.md#presence-and-tool-previews)
                updateSystem.UpdateBefore<PresenceSystem, Game.Rendering.OverlayRenderSystem>(SystemUpdatePhase.Rendering);
            });
            Step(log, "session driver", () =>
            {
                if (_driver == null)
                {
                    _driver = new GameObject("SkylineTogether.SessionDriver");
                    UnityEngine.Object.DontDestroyOnLoad(_driver);
                    _driver.AddComponent<SessionDriver>();
                }
            });
            log.Info(LogCategory.Session, "loaded; " + Probes.Summary);
        }

        /// <summary>Runs one part of <see cref="OnLoad"/>; a failure is logged and the next part still runs.</summary>
        private static bool Step(ILog log, string what, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                log.Error(LogCategory.Session, "loading failed: " + what, ex);
                return false;
            }
        }

        public void OnDispose()
        {
            Log?.Info(LogCategory.Session, "disposing");
            ReferenceProbe.Stop();
            Replay?.Shutdown(); // solo testing: keep the recording, write the verdict
            try
            {
                Sessions?.Dispose();
            }
            catch (Exception ex)
            {
                Log?.Warn(LogCategory.Session, "leaving the session on exit failed", ex);
            }
            Sessions = null;
            try
            {
                Invites?.Dispose();
            }
            catch (Exception ex)
            {
                Log?.Warn(LogCategory.Session, "leaving the Steam lobby or clearing the rich presence failed", ex);
            }
            Invites = null;
            Toasts = null;
            // the codec's persistent native buffers and serialization contexts were never freed
            try
            {
                Sync?.Dispose();
            }
            catch (Exception ex)
            {
                Log?.Warn(LogCategory.Session, "disposing replication failed", ex);
            }
            Presence = null;
            Settings?.UnregisterInOptionsUI();
            Settings = null;
            if (_driver != null) UnityEngine.Object.Destroy(_driver);
            _driver = null;
        }
    }
}
