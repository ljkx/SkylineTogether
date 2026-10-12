using System;
using Colossal.IO.AssetDatabase;
using Game;
using Game.Modding;
using Game.SceneFlow;
using SkylineTogether.Core;
using SkylineTogether.Diagnostics;
using SkylineTogether.Session;
using SkylineTogether.Settings;
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
                updateSystem.UpdateAt<SpeedAuthoritySystem>(SystemUpdatePhase.MainLoop);
                updateSystem.UpdateAt<SessionUISystem>(SystemUpdatePhase.UIUpdate);

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
            Settings?.UnregisterInOptionsUI();
            Settings = null;
            if (_driver != null) UnityEngine.Object.Destroy(_driver);
            _driver = null;
        }
    }
}
