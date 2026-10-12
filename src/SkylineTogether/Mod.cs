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

        /// <summary>Remote presence (cursors, tools, name tags; presence branch).</summary>
        public static PresenceController Presence { get; private set; }

        /// <summary>Movers branch: the client's follower clock.</summary>
        public static FollowerClock Clock { get; private set; }

        /// <summary>Movers branch: vehicle and creature replication.</summary>
        public static MoverSync Movers { get; private set; }

        private static GameObject _driver;

        public void OnLoad(UpdateSystem updateSystem)
        {
            // The first launch must never crash the game or break single player: every step is isolated, nothing is
            // rethrown, and what failed is in the log, in the self-test report and in the diagnostics dump.
            var log = new GameLog(Name);
            Log = log;
            log.Info(LogCategory.Session, "Skyline Together " + typeof(Mod).Assembly.GetName().Version + " loading");
            Step(log, "settings", () =>
            {
                Settings = new SkylineTogetherSettings(this);
                Settings.RegisterInOptionsUI();
                GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
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
            // the self-test (ARCH D6): types, private members, Steam; features whose probe fails stay off
            Step(log, "self-test", () => Probes.RunStartup(log));

            bool core = Step(log, "session and replication", () =>
            {
                if (Settings == null) throw new InvalidOperationException("the settings did not load");
                Sessions = new SessionManager(Settings, log);
                Sync = new SyncController(Sessions, log);
                Presence = new PresenceController(Sessions, log); // presence branch
                // movers branch: follower clock (time runs, gameplay simulation off) and mover replication
                Clock = new FollowerClock(Sessions, log);
                Movers = new MoverSync(Sessions, log);
            });
            if (!core)
            {
                Features.Session.SetProbeResult(false, "the session layer did not start (see the log)");
                Features.Registry.Touch();
            }

            // identity: saved into every save (join snapshots included) by the game's own system serializer
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
                // feat/integrity: client icons are host data; filter local producers right before IconCommandSystem
                updateSystem.UpdateBefore<IconAuthorityFilterSystem, Game.Notifications.IconCommandSystem>(SystemUpdatePhase.ModificationEnd);
            });
            Step(log, "mover systems", () =>
            {
                // movers branch: host capture before SaveGameSystem, client apply after the structure apply, client
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
            Step(log, "presence system", () =>
            {
                // presence branch: remote cursors drawn into the overlay buffer right before it renders (ARCH K15)
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
            try
            {
                Sessions?.Dispose();
            }
            catch (Exception ex)
            {
                Log?.Warn(LogCategory.Session, "leaving the session on exit failed", ex);
            }
            Sessions = null;
            // review fix: the codec's persistent native buffers and serialization contexts were never freed
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
