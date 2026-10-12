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

        /// <summary>Steam friends: rich presence "connect", the invite dialog, invite joins.</summary>
        public static SteamInvites Invites { get; private set; }

        /// <summary>Join, leave and progress toasts.</summary>
        public static SessionToasts Toasts { get; private set; }

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
                LocaleSource.RegisterAll(Settings); // every embedded Data/locale.<id>.json, English as the fallback
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
                // movers branch: follower clock (time runs, gameplay simulation off) and mover replication
                Clock = new FollowerClock(Sessions, log);
                Movers = new MoverSync(Sessions, log);
                Toasts = new SessionToasts(Sessions);
                Invites = new SteamInvites(Sessions, Settings, log);

                // identity: saved into every save (join snapshots included) by the game's own system serializer
                updateSystem.UpdateAt<NetIdSystem>(SystemUpdatePhase.MainLoop);
                updateSystem.UpdateAfter<PostDeserialize<NetIdSystem>>(SystemUpdatePhase.Deserialize);
                World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<SerializerSystem>()?.SetDirty();

                // replication: apply right after EndFrameBarrier, capture right before PrepareCleanUpSystem
                updateSystem.UpdateAfter<SyncApplySystem, EndFrameBarrier>(SystemUpdatePhase.MainLoop);
                updateSystem.UpdateBefore<SyncCaptureSystem, PrepareCleanUpSystem>(SystemUpdatePhase.MainLoop);
                updateSystem.UpdateBefore<SyncToolApplySystem>(SystemUpdatePhase.ApplyTool);
                updateSystem.UpdateBefore<SyncBindSystem, SaveGameSystem>(SystemUpdatePhase.MainLoop);
                // gameplay branch: viewers' tools never apply (no-predict gate, right before ToolOutputSystem)
                updateSystem.UpdateBefore<ViewerGateSystem, Game.Tools.ToolOutputSystem>(SystemUpdatePhase.ToolUpdate);
                // feat/integrity: client icons are host data; filter local producers right before IconCommandSystem
                updateSystem.UpdateBefore<IconAuthorityFilterSystem, Game.Notifications.IconCommandSystem>(SystemUpdatePhase.ModificationEnd);

                // movers branch: host capture before SaveGameSystem, client apply after the structure apply, client
                // motion writer after PrepareRendering and before PreCullingSystem reads the frames
                updateSystem.UpdateBefore<MoverCaptureSystem, SaveGameSystem>(SystemUpdatePhase.MainLoop);
                updateSystem.UpdateAfter<MoverApplySystem, SyncApplySystem>(SystemUpdatePhase.MainLoop);
                updateSystem.UpdateBefore<MoverWriteSystem, Game.Rendering.PreCullingSystem>(SystemUpdatePhase.PreCulling);

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
            try
            {
                Invites?.Dispose();
            }
            catch (Exception ex)
            {
                Log?.Warn(LogCategory.Session, "clearing the Steam rich presence failed", ex);
            }
            Invites = null;
            Toasts = null;
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
