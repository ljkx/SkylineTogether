using System;
using Game;
using Game.Simulation;
using SkylineTogether.Core;
using SkylineTogether.Core.Session;
using SkylineTogether.Session;

namespace SkylineTogether.Systems
{
    /// <summary>
    /// One game speed for everyone. The host's speed is the session's; while a player joins, the host's simulation is
    /// held (paused) and resumes afterwards. Clients never simulate (they follow the host's clock and results); a speed
    /// the client player selects becomes a request to the host. Runs in LateUpdate right before the simulation.
    /// </summary>
    public sealed class SpeedAuthoritySystem : GameSystemBase
    {
        private SimulationSystem _simulation;
        private bool _holding;
        private float _speedBeforeHold;
        private float _lastNonZero = 1f;
        // gameplay branch: TimeUISystem forces speed 0 while the window has no focus or a pause menu is open
        // (TimeUISystem.cs:115-137, R10 T2); with players in the session the host's menu must not pause everyone
        private Game.UI.InGame.TimeUISystem _timeUi;
        private System.Reflection.FieldInfo _forcedPause;
        private System.Reflection.FieldInfo _speedBeforePause;
        private bool _pauseRequested;

        protected override void OnCreate()
        {
            base.OnCreate();
            _simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            _timeUi = World.GetExistingSystemManaged<Game.UI.InGame.TimeUISystem>();
            const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            _forcedPause = typeof(Game.UI.InGame.TimeUISystem).GetField("m_UnpausedBeforeForcedPause", Private);
            _speedBeforePause = typeof(Game.UI.InGame.TimeUISystem).GetField("m_SpeedBeforePause", Private);
            if (_forcedPause == null || _speedBeforePause == null) Mod.Log?.Warn(SkylineTogether.Core.LogCategory.Session, "TimeUISystem fields not found: the host's pause menu pauses the session");
        }

        /// <summary>
        /// A join hold must not survive the city it was made in. The host leaving the city mid-join stops
        /// the session, but the next LateUpdate (in the newly loaded city) released the hold and restored the old city's
        /// speed, so the new city started running against "paused after loading".
        /// </summary>
        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            _holding = false;
            _pauseRequested = false;
        }

        protected override void OnUpdate()
        {
            SessionManager sessions = Mod.Sessions;
            if (sessions == null) return;
            if (sessions.Host == null && sessions.Client == null && !_holding && !sessions.HoldCity) return; // single player
            if (!Diagnostics.Features.Speed.IsAvailable) return;
            try
            {
                if (sessions.Host != null)
                {
                    UpdateHost(sessions);
                    return;
                }
                if (_holding) Release();
                if (sessions.Client != null && sessions.Client.State == ClientState.Live)
                {
                    UpdateClient(sessions.Client);
                }
                else if (sessions.HoldCity)
                {
                    // recovery: the host's city without a session stays paused until the player decides or it rejoins
                    if (_simulation.selectedSpeed != 0f) _simulation.selectedSpeed = 0f;
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Speed, "speed authority", ex);
            }
        }

        private void UpdateHost(SessionManager sessions)
        {
            if (sessions.TakeSpeedRequest(out SpeedState request))
            {
                float speed = request.Paused ? 0f : MultiplierToSpeed(request.Speed);
                _pauseRequested = request.Paused; // gameplay branch: a player's pause is never undone below
                if (_holding) _speedBeforeHold = speed;
                else _simulation.selectedSpeed = speed;
            }
            if (_simulation.selectedSpeed > 0f) _pauseRequested = false;
            if (sessions.Host.LivePeerCount > 0 && !_holding && !_pauseRequested) KeepRunningThroughForcedPause();
            bool hold = sessions.Host.HoldSimulation;
            if (hold && !_holding)
            {
                _holding = true;
                _speedBeforeHold = _simulation.selectedSpeed;
                _simulation.selectedSpeed = 0f;
            }
            else if (hold && _simulation.selectedSpeed > 0f)
            {
                // the host pressed play while a player joins: remember it, stay paused until the joiner is in
                _speedBeforeHold = _simulation.selectedSpeed;
                _simulation.selectedSpeed = 0f;
            }
            else if (!hold && _holding)
            {
                Release();
            }
            float intended = _holding ? _speedBeforeHold : _simulation.selectedSpeed;
            if (intended > 0f) _lastNonZero = intended;
            sessions.Host.Speed = new SpeedState { Speed = SpeedToMultiplier(_lastNonZero), Paused = intended <= 0f, Held = _holding };
        }

        /// <summary>
        /// gameplay branch: undoes TimeUISystem's forced pause (focus loss, pause menu) on a host with live players, but
        /// never a pause the host chose (the field is only set when the game ran before the forced pause).
        /// </summary>
        private void KeepRunningThroughForcedPause()
        {
            try
            {
                if (_timeUi == null) _timeUi = World.GetExistingSystemManaged<Game.UI.InGame.TimeUISystem>();
                if (_timeUi == null || _forcedPause == null || _speedBeforePause == null) return;
                if (_simulation.selectedSpeed > 0f || !(bool)_forcedPause.GetValue(_timeUi)) return;
                float before = (float)_speedBeforePause.GetValue(_timeUi);
                _simulation.selectedSpeed = before > 0f ? before : _lastNonZero;
            }
            catch (System.Exception ex)
            {
                _forcedPause = null;
                Mod.Log?.Warn(SkylineTogether.Core.LogCategory.Session, "keeping the session running through the pause menu failed", ex);
            }
        }

        private void Release()
        {
            _holding = false;
            _simulation.selectedSpeed = _speedBeforeHold;
        }

        /// <summary>
        /// Clients follow: their gameplay simulation never runs (the host's results arrive as data). With the follower
        /// clock their time runs at the host's speed; without it the local speed stays 0. A speed the player selects is
        /// a request to the host, which applies it for everyone.
        /// </summary>
        private void UpdateClient(SessionClient client)
        {
            // movers branch: the follower clock (Sync/FollowerClock.cs) mirrors the host's speed with every gameplay
            // GameSimulation system off and forwards player speed changes as requests; without it, stay paused below
            if (Sync.FollowerClock.Instance != null && Sync.FollowerClock.Instance.Tick(_simulation, client)) return;
            float current = _simulation.selectedSpeed;
            if (current > 0f)
            {
                if (UnityEngine.Application.isFocused) client.RequestSpeed(new SpeedState { Speed = SpeedToMultiplier(current), Paused = false });
                _simulation.selectedSpeed = 0f;
            }
        }

        private static int SpeedToMultiplier(float speed) => speed >= 3.5f ? 4 : speed >= 1.5f ? 2 : 1;

        private static float MultiplierToSpeed(int multiplier) => multiplier >= 4 ? 4f : multiplier >= 2 ? 2f : 1f;
    }
}
