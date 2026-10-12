using Game;
using Game.Simulation;
using SkylineTogether.Core.Session;
using SkylineTogether.Session;

namespace SkylineTogether.Systems
{
    /// <summary>
    /// One game speed for everyone. The host's speed is the session's; while a player joins, the host's simulation is
    /// held (paused) and resumes afterwards. On clients the host's speed is enforced every frame, and a speed change
    /// the player makes becomes a request to the host, which applies it for everyone.
    /// </summary>
    public sealed class SpeedAuthoritySystem : GameSystemBase
    {
        private SimulationSystem _simulation;
        private bool _holding;
        private float _speedBeforeHold;
        private float _lastNonZero = 1f;
        private float _enforced = -1f;

        protected override void OnCreate()
        {
            base.OnCreate();
            _simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
        }

        protected override void OnUpdate()
        {
            SessionManager sessions = Mod.Sessions;
            if (sessions != null && sessions.Host != null)
            {
                UpdateHost(sessions);
                return;
            }
            if (_holding) Release();
            if (sessions != null && sessions.Client != null && sessions.Client.State == ClientState.Live) UpdateClient(sessions.Client);
            else _enforced = -1f;
        }

        private void UpdateHost(SessionManager sessions)
        {
            if (sessions.TakeSpeedRequest(out SpeedState request))
            {
                float speed = request.Paused ? 0f : MultiplierToSpeed(request.Speed);
                if (_holding) _speedBeforeHold = speed;
                else _simulation.selectedSpeed = speed;
            }
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

        private void Release()
        {
            _holding = false;
            _simulation.selectedSpeed = _speedBeforeHold;
        }

        private void UpdateClient(SessionClient client)
        {
            SpeedState s = client.Speed;
            float wanted = s.Paused || s.Held ? 0f : MultiplierToSpeed(s.Speed);
            float current = _simulation.selectedSpeed;
            if (_enforced >= 0f && current != _enforced && !s.Held)
            {
                // the player changed the speed locally: ask the host and keep the host's speed until it answers
                client.RequestSpeed(new SpeedState { Speed = current > 0f ? SpeedToMultiplier(current) : s.Speed, Paused = current <= 0f });
            }
            if (current != wanted) _simulation.selectedSpeed = wanted;
            _enforced = wanted;
        }

        private static int SpeedToMultiplier(float speed) => speed >= 3.5f ? 4 : speed >= 1.5f ? 2 : 1;

        private static float MultiplierToSpeed(int multiplier) => multiplier >= 4 ? 4f : multiplier >= 2 ? 2f : 1f;
    }
}
