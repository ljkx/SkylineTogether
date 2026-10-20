using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.Simulation;
using Game.UI.InGame;
using SkylineTogether.Core;
using SkylineTogether.Core.Follower;
using SkylineTogether.Core.Session;
using SkylineTogether.Session;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// The follower clock of a client (ARCHITECTURE.md#follower-clock): the client's simulation time runs with the
    /// host's speed so rendering interpolation, time of day, weather and the moving objects are alive, while no gameplay
    /// system of <see cref="SystemUpdatePhase.GameSimulation"/> runs. Every system registered in that phase is found
    /// through <c>UpdateSystem</c>'s private registration lists (<c>m_Systems</c> and the before/after map
    /// <c>m_RefMap</c>, <c>Game/UpdateSystem.cs:78-88, 274-292</c>) and disabled, except a cosmetic allow-list that writes
    /// no replicated state (R11 §3, §8.2). A system added by a patch or another mod is therefore off as well (fail closed).
    /// <para>
    /// Vanilla <c>SimulationSystem</c> keeps running (its loading flag and speed setter stay healthy) at the mirrored host
    /// speed; disabled systems simply skip their update. Drift is corrected towards the host frame estimated from the
    /// <c>Clock</c> record and the tick of every motion datagram: small errors by skipping ticks or holding one frame
    /// (through <c>SimulationSystem.m_Timer</c>, <c>SimulationSystem.cs:228-238</c>), large ones by setting
    /// <c>frameIndex</c> (private setter, <c>:68</c>). A speed the player picks becomes a request to the host.
    /// </para>
    /// <para>
    /// Fail safe: when a probe fails (registration lists, frame setter) the clock stays inactive and the client keeps the
    /// earlier paused behaviour (<see cref="Systems.SpeedAuthoritySystem"/>). Everything is restored when the session ends.
    /// Main thread only.
    /// </para>
    /// </summary>
    public sealed class FollowerClock
    {
        /// <summary>GameSimulation systems that stay on: pure functions of the clock and replicated state (R11 §3).</summary>
        private static readonly HashSet<string> Cosmetic = new HashSet<string>(StringComparer.Ordinal)
        {
            "Game.Simulation.TimeSystem",
            "Game.Simulation.ClimateSystem",
            "Game.Simulation.SnowSystem",
            "Game.Simulation.WindSimulationSystem",
            "Game.Simulation.WindSystem",
            "Game.Simulation.ZoneAmbienceSystem",
            "Game.Simulation.SpawnableAmbienceSystem",
            "Game.Simulation.WetnessSystem",
            "Game.Simulation.DirtynessSystem",
            "Game.Effects.EffectFlagSystem",
            // the budget UI reads its Enabled flag; it only derives the subsidy line from replicated values (ARCHITECTURE.md#follower-clock)
            "Game.Simulation.GameModeGovernmentSubsidiesSystem",
            // the economy panels' trade, wealth, worker and tourist figures; a pure read of the replicated city
            // statistics (BudgetSystem.UpdateData), without it they stay at the join snapshot
            "Game.Simulation.BudgetSystem",
        };

        /// <summary>The cosmetic allow-list's names (probed at startup, <see cref="Diagnostics.Probes"/>).</summary>
        public static IEnumerable<string> CosmeticSystemNames => Cosmetic;

        private const int ReassertFrames = 120;
        private const double RescanSeconds = 10.0;

        private readonly ILog _log;
        // the host's samples stop while it stalls (autosave, join snapshot): hold instead of running ahead of it
        private readonly FollowerClockModel _model = new FollowerClockModel { MaxExtrapolationSeconds = 1.0 };
        private readonly Dictionary<ComponentSystemBase, bool> _disabled = new Dictionary<ComponentSystemBase, bool>();
        private Action<SimulationSystem, uint> _setFrame;
        private FieldInfo _timer;
        private object _barrierBinding;
        private PropertyInfo _barrierObservers;
        private bool _pendingActivate;
        private bool _probeFailed;
        private int _frames;
        private double _lastScan;
        private bool _hasWritten;
        private float _lastWritten;
        private double _lastRate = -1;

        public FollowerClock(SessionManager sessions, ILog log)
        {
            _log = log;
            Instance = this;
            sessions.ClientStarted += client =>
            {
                _probeFailed = false;
                client.BecameLive += () => _pendingActivate = true;
            };
            sessions.SessionEnded += Deactivate;
        }

        /// <summary>The instance created by <see cref="Mod"/> (null before the mod loaded).</summary>
        public static FollowerClock Instance { get; private set; }

        /// <summary>True while this client follows the host's clock with gameplay simulation off.</summary>
        public bool Active { get; private set; }

        /// <summary>Systems currently held off by the clock.</summary>
        public int DisabledCount => _disabled.Count;

        private static double Now => UnityEngine.Time.realtimeSinceStartupAsDouble;

        // ------------------------------------------------------------------------------------------------ samples

        /// <summary>A <c>Clock</c> record from the host. True when the running follower clock consumed it (the caller
        /// must then not set <c>frameIndex</c> itself).</summary>
        public bool OnHostClock(uint frame)
        {
            if (!Active) return false;
            _model.AddSample(frame, Now);
            return true;
        }

        /// <summary>The host tick of a motion datagram (a fresher clock sample than the 4 Hz record).</summary>
        public void OnHostTick(uint tick)
        {
            if (Active) _model.AddSample(tick, Now);
        }

        // ------------------------------------------------------------------------------------------------ per frame

        /// <summary>
        /// LateUpdate, right before <c>SimulationSystem</c> (called by <see cref="Systems.SpeedAuthoritySystem"/>):
        /// activates after the client went live, mirrors the host's speed, turns player speed changes into requests
        /// and corrects drift. Returns false while the clock is not active (the caller keeps the paused behaviour).
        /// </summary>
        public bool Tick(SimulationSystem simulation, SessionClient client)
        {
            if (_pendingActivate)
            {
                _pendingActivate = false;
                Activate(simulation);
            }
            if (!Active || client == null) return false;
            try
            {
                if (++_frames % ReassertFrames == 0) Reassert(Now - _lastScan >= RescanSeconds);
                SpeedState host = client.Speed;
                bool paused = host.Paused || host.Held;
                float hostSpeed = paused ? 0f : MultiplierToSpeed(host.Speed);
                ForwardPlayerChoice(simulation, client, host);
                simulation.selectedSpeed = hostSpeed;
                _lastWritten = simulation.selectedSpeed;
                _hasWritten = true;
                Correct(simulation, hostSpeed, paused, host.Speed);
            }
            catch (Exception ex)
            {
                // one failure is enough: a clock that misbehaves would fight the simulation every frame
                Diagnostics.Guard.Report(Diagnostics.Features.Clock, "follower clock (falling back to the paused client)", ex);
                Deactivate();
                _probeFailed = true;
                return false;
            }
            return true;
        }

        private void ForwardPlayerChoice(SimulationSystem simulation, SessionClient client, SpeedState host)
        {
            float current = simulation.selectedSpeed;
            if (!_hasWritten || current == _lastWritten || !UnityEngine.Application.isFocused || BarrierActive()) return;
            if (current > 0f)
            {
                int wanted = SpeedToMultiplier(current);
                if (host.Paused || wanted != host.Speed) client.RequestSpeed(new SpeedState { Speed = wanted, Paused = false });
            }
            else if (!host.Paused && _barrierObservers != null)
            {
                client.RequestSpeed(new SpeedState { Speed = host.Speed, Paused = true });
            }
        }

        private void Correct(SimulationSystem simulation, float hostSpeed, bool paused, int multiplier)
        {
            double now = Now;
            double rate = hostSpeed * 60.0;
            if (rate != _lastRate)
            {
                _model.SetRate(rate, now);
                _lastRate = rate;
            }
            if (!_model.HasEstimate) return;
            // the motion lag (ARCHITECTURE.md#movers): a few ticks of cushion so frames are staged before they are rendered
            uint lag = paused ? 0u : (uint)(4 * Math.Max(1, multiplier));
            uint target = unchecked((uint)(long)Math.Floor(_model.Estimate(now)) - lag);
            ClockCorrection c = FollowerClockModel.Decide(simulation.frameIndex, target, paused);
            if (c.Jump)
            {
                if (!paused) _log.Debug(LogCategory.Probe, "follower clock jumps from " + simulation.frameIndex + " to " + target);
                _setFrame(simulation, target);
            }
            else if (c.SkipTicks > 0)
            {
                _setFrame(simulation, unchecked(simulation.frameIndex + (uint)c.SkipTicks));
            }
            else if (c.Hold && _timer != null)
            {
                // SimulationSystem adds deltaTime × speed to m_Timer; starting from its negation yields no tick this frame
                _timer.SetValue(simulation, -(UnityEngine.Time.deltaTime * simulation.selectedSpeed));
            }
        }

        // ------------------------------------------------------------------------------------------------ gating

        private void Activate(SimulationSystem simulation)
        {
            if (Active || _probeFailed) return;
            if (!Diagnostics.Features.Clock.IsAvailable)
            {
                _probeFailed = true;
                _log.Warn(LogCategory.Probe, "follower clock unavailable (" + Diagnostics.Features.Clock.UnavailableReason + "); the client stays paused and follows the host's frame");
                return;
            }
            try
            {
                _setFrame = Diagnostics.Probes.SetFrameIndex ?? throw new MissingMemberException("SimulationSystem.frameIndex setter");
                _timer = Diagnostics.Probes.SimulationTimer;
                ProbeBarrier(simulation.World);
                List<ComponentSystemBase> systems = Diagnostics.Probes.GameSimulationSystems(simulation.World);
                if (systems == null) throw new MissingMemberException("UpdateSystem registration lists");
                _disabled.Clear();
                Disable(systems);
                _lastScan = Now;
                // the model outlives sessions; a stale estimate from the previous session could keep this
                // client ahead of the host (and of the motion stream) for minutes
                _model.Reset();
                _model.SetRate(0, Now);
                _model.AddSample(simulation.frameIndex, Now);
                _lastRate = 0;
                _hasWritten = false;
                Active = true;
                _log.Info(LogCategory.Gate, "follower clock active: " + _disabled.Count + " GameSimulation systems off, " + Cosmetic.Count + " cosmetic kept");
            }
            catch (Exception ex)
            {
                _probeFailed = true;
                RestoreSystems();
                _log.Warn(LogCategory.Probe, "follower clock unavailable (" + ex.Message + "); the client stays paused and follows the host's frame", ex);
            }
        }

        /// <summary>Restores every system and leaves the simulation paused (session ended or fallback).</summary>
        public void Deactivate()
        {
            _pendingActivate = false;
            bool was = Active;
            Active = false;
            RestoreSystems();
            if (was)
            {
                try
                {
                    World world = World.DefaultGameObjectInjectionWorld;
                    SimulationSystem simulation = world?.GetExistingSystemManaged<SimulationSystem>();
                    if (simulation != null) simulation.selectedSpeed = 0f;
                }
                catch (Exception)
                {
                    // the world may be gone
                }
                _log.Info(LogCategory.Gate, "follower clock stopped; GameSimulation systems restored");
            }
        }

        private void RestoreSystems()
        {
            foreach (KeyValuePair<ComponentSystemBase, bool> pair in _disabled)
            {
                try
                {
                    SystemSwitch.Restore(pair.Key, pair.Value);
                }
                catch (Exception)
                {
                    // the world may be gone
                }
            }
            _disabled.Clear();
        }

        private void Disable(List<ComponentSystemBase> systems)
        {
            foreach (ComponentSystemBase s in systems)
            {
                // mod compatibility: adapters keep mod systems that write no replicated state (Compat.ModAdapter.ClientSimulationSystems)
                if (s == null || Cosmetic.Contains(s.GetType().FullName ?? "") || Compat.ModCompat.IsClientSimulationAllowed(s.GetType())) continue;
                if (!_disabled.ContainsKey(s)) _disabled[s] = SystemSwitch.TurnOff(s); // true original even if the gate got there first
                else s.Enabled = false;
            }
        }

        private void Reassert(bool rescan)
        {
            if (rescan)
            {
                _lastScan = Now;
                World world = World.DefaultGameObjectInjectionWorld;
                List<ComponentSystemBase> systems = world != null ? Diagnostics.Probes.GameSimulationSystems(world) : null;
                if (systems != null)
                {
                    int before = _disabled.Count;
                    Disable(systems);
                    if (_disabled.Count != before) _log.Info(LogCategory.Gate, "follower clock: " + (_disabled.Count - before) + " newly registered GameSimulation systems off");
                }
            }
            foreach (ComponentSystemBase s in _disabled.Keys)
            {
                if (s.Enabled) s.Enabled = false;
            }
        }

        private void ProbeBarrier(World world)
        {
            _barrierBinding = null;
            _barrierObservers = null;
            try
            {
                TimeUISystem time = world.GetExistingSystemManaged<TimeUISystem>();
                object binding = time != null ? Diagnostics.Probes.PausedBarrierBinding?.GetValue(time) : null;
                PropertyInfo observers = binding != null ? Diagnostics.Probes.PausedBarrierObservers : null; // P-BARRIER
                if (observers != null)
                {
                    _barrierBinding = binding;
                    _barrierObservers = observers;
                }
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Probe, "pause barrier probe failed: " + ex.Message);
            }
            if (_barrierObservers == null) _log.Info(LogCategory.Probe, "pause barrier not found: a local pause is not forwarded to the host (speed changes are)");
        }

        /// <summary>The pause menu (or another UI barrier) forces speed 0 locally; that is not a player request.</summary>
        private bool BarrierActive()
        {
            if (_barrierObservers == null) return false;
            try
            {
                return (int)_barrierObservers.GetValue(_barrierBinding) > 0;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static int SpeedToMultiplier(float speed) => speed >= 3.5f ? 4 : speed >= 1.5f ? 2 : 1;

        private static float MultiplierToSpeed(int multiplier) => multiplier >= 4 ? 4f : multiplier >= 2 ? 2f : 1f;
    }
}
