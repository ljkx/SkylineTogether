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

        private static GameObject _driver;

        public void OnLoad(UpdateSystem updateSystem)
        {
            var log = new GameLog(Name);
            Log = log;
            log.Info(LogCategory.Session, "Skyline Together " + typeof(Mod).Assembly.GetName().Version + " loading");
            try
            {
                Settings = new SkylineTogetherSettings(this);
                Settings.RegisterInOptionsUI();
                GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
                AssetDatabase.global.LoadSettings(Name, Settings, new SkylineTogetherSettings(this));

                try
                {
                    SteamNetworkingUtils.InitRelayNetworkAccess();
                }
                catch (Exception ex)
                {
                    log.Warn(LogCategory.Net, "Steam relay network unavailable (is this the Steam version of the game?)", ex);
                }

                Sessions = new SessionManager(Settings, log);
                Sync = new SyncController(Sessions, log);
                Presence = new PresenceController(Sessions, log); // presence branch

                // identity: saved into every save (join snapshots included) by the game's own system serializer
                updateSystem.UpdateAt<NetIdSystem>(SystemUpdatePhase.MainLoop);
                updateSystem.UpdateAfter<PostDeserialize<NetIdSystem>>(SystemUpdatePhase.Deserialize);
                World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<SerializerSystem>()?.SetDirty();

                // replication: apply right after EndFrameBarrier, capture right before PrepareCleanUpSystem
                updateSystem.UpdateAfter<SyncApplySystem, EndFrameBarrier>(SystemUpdatePhase.MainLoop);
                updateSystem.UpdateBefore<SyncCaptureSystem, PrepareCleanUpSystem>(SystemUpdatePhase.MainLoop);
                updateSystem.UpdateBefore<SyncToolApplySystem>(SystemUpdatePhase.ApplyTool);
                updateSystem.UpdateBefore<SyncBindSystem, SaveGameSystem>(SystemUpdatePhase.MainLoop);
                // feat/integrity: client icons are host data; filter local producers right before IconCommandSystem
                updateSystem.UpdateBefore<IconAuthorityFilterSystem, Game.Notifications.IconCommandSystem>(SystemUpdatePhase.ModificationEnd);

                // speed authority right before the simulation consumes the speed (R05 §8)
                updateSystem.UpdateBefore<SpeedAuthoritySystem, SimulationSystem>(SystemUpdatePhase.LateUpdate);
                updateSystem.UpdateAt<SessionUISystem>(SystemUpdatePhase.UIUpdate);

                // presence branch: remote cursors drawn into the overlay buffer right before it renders (ARCH K15)
                updateSystem.UpdateBefore<PresenceSystem, Game.Rendering.OverlayRenderSystem>(SystemUpdatePhase.Rendering);

                if (_driver == null)
                {
                    _driver = new GameObject("SkylineTogether.SessionDriver");
                    UnityEngine.Object.DontDestroyOnLoad(_driver);
                    _driver.AddComponent<SessionDriver>();
                }
                log.Info(LogCategory.Session, "loaded");
            }
            catch (Exception ex)
            {
                log.Error(LogCategory.Session, "loading failed", ex);
                throw;
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
            Presence = null;
            Settings?.UnregisterInOptionsUI();
            Settings = null;
            if (_driver != null) UnityEngine.Object.Destroy(_driver);
            _driver = null;
        }
    }
}
