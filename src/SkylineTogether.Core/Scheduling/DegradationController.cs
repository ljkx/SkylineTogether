using System;
using System.Globalization;
using System.Threading;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// The degradation ladder of docs/history/ARCHITECTURE-original.md §4.8.5, driven per peer by motion pressure: when motion (P2) wants more than its
    /// budget for <see cref="SchedulerConfig.DegradeAfterSeconds"/>, the peer moves one <see cref="DegradationStep"/> down
    /// the ladder (far field at 1/2, then 1/4 rate; then the motion radius shrinks by
    /// <see cref="SchedulerConfig.RadiusShrinkFactor"/> every <see cref="SchedulerConfig.RadiusStepSeconds"/> towards the
    /// minimum radius; then the P3 share shrinks to 5 %; then water tiles pause; then cell maps coalesce). With headroom
    /// (demand below <see cref="SchedulerConfig.RecoverBudgetRatio"/> × budget) for
    /// <see cref="SchedulerConfig.RecoverAfterSeconds"/> it climbs back in reverse order, the radius growing back
    /// gradually. Between the two thresholds the step holds (hysteresis), so the ladder does not flap.
    /// <para>
    /// <c>p2Demand</c> is what the motion encoder wanted to send at the quality currently in effect and <c>p2Budget</c>
    /// what it was granted, in the same unit (the scheduler passes smoothed bytes per second). Time comes from the clock
    /// given to the constructor; without one each <see cref="Evaluate"/> counts as 1/60 s (one frame). Evaluations for
    /// different peers may run concurrently; for one peer they must not.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Worker)]
    public sealed class DegradationController
    {
        private sealed class State
        {
            public int Step;
            public float Scale = 1f;
            public double OverSince = double.NaN;
            public double UnderSince = double.NaN;
            public double LastChange = double.NegativeInfinity;
            public double LastRadiusAdjust = double.NegativeInfinity;
            public long Evaluations;
        }

        private readonly SchedulerSettings _s;
        private readonly IClock _clock;
        private readonly State[] _states = new State[256];

        public DegradationController(SchedulerConfig config) : this(config, null)
        {
        }

        /// <param name="config">Ladder tunables.</param>
        /// <param name="clock">Time source for the hysteresis; null = one frame (1/60 s) per evaluation.</param>
        public DegradationController(SchedulerConfig config, IClock clock)
        {
            _s = SchedulerSettings.From(config);
            _clock = clock;
        }

        internal DegradationController(SchedulerSettings settings, IClock clock)
        {
            _s = settings ?? SchedulerSettings.From(null);
            _clock = clock;
        }

        /// <summary>Optional log for step changes (category <see cref="LogCategory.Scheduling"/>).</summary>
        public ILog Log { get; set; }

        /// <summary>The motion quality of a ladder step.</summary>
        public static MotionQuality QualityOf(DegradationStep step)
        {
            switch (step)
            {
                case DegradationStep.None: return MotionQuality.Full;
                case DegradationStep.FarHalf: return MotionQuality.FarHalf;
                case DegradationStep.FarQuarter: return MotionQuality.FarQuarter;
                default: return MotionQuality.Reduced;
            }
        }

        /// <summary>
        /// Evaluates one peer: returns the motion quality to use and, in <paramref name="radiusScale"/>, the factor
        /// (minimum radius / maximum radius ... 1) to apply to the motion radius (the attention sets keep the
        /// result at or above the minimum radius).
        /// </summary>
        public MotionQuality Evaluate(PeerId peer, int p2Budget, int p2Demand, out float radiusScale)
        {
            State st = GetOrCreate(peer);
            double now = _clock != null ? _clock.Seconds : ++st.Evaluations / 60.0;
            double budget = Math.Max(0, p2Budget);
            double demand = Math.Max(0, p2Demand);
            bool over = demand > 0 && demand > budget * _s.OverBudgetRatio;
            bool under = demand <= budget * _s.RecoverBudgetRatio;
            int before = st.Step;
            float scaleBefore = st.Scale;

            if (over)
            {
                st.UnderSince = double.NaN;
                if (double.IsNaN(st.OverSince)) st.OverSince = now;
                Degrade(st, now);
            }
            else if (under)
            {
                st.OverSince = double.NaN;
                if (double.IsNaN(st.UnderSince)) st.UnderSince = now;
                Recover(st, now);
            }
            else
            {
                st.OverSince = double.NaN;
                st.UnderSince = double.NaN;
            }

            if (st.Step != before) LogStep(peer, before, st.Step, budget, demand);
            else if (Log != null && st.Scale != scaleBefore && Log.IsEnabled(LogLevel.Debug))
            {
                Log.Write(LogLevel.Debug, LogCategory.Scheduling, peer + " motion radius scale " + scaleBefore.ToString("0.###", CultureInfo.InvariantCulture)
                                                                  + " -> " + st.Scale.ToString("0.###", CultureInfo.InvariantCulture));
            }
            radiusScale = st.Scale;
            return QualityOf((DegradationStep)st.Step);
        }

        /// <summary>The peer's current ladder step (any thread).</summary>
        public DegradationStep StepOf(PeerId peer)
        {
            State st = Volatile.Read(ref _states[peer.Value]);
            return st == null ? DegradationStep.None : (DegradationStep)Volatile.Read(ref st.Step);
        }

        /// <summary>The peer's current motion radius scale (1 when not degraded).</summary>
        public float RadiusScaleOf(PeerId peer)
        {
            State st = Volatile.Read(ref _states[peer.Value]);
            return st == null ? 1f : st.Scale;
        }

        /// <summary>Forgets a peer (it starts at <see cref="DegradationStep.None"/> again).</summary>
        public void RemovePeer(PeerId peer)
        {
            Volatile.Write(ref _states[peer.Value], null);
        }

        private void Degrade(State st, double now)
        {
            double overFor = now - st.OverSince;
            double sinceChange = now - st.LastChange;
            int max = (int)DegradationStep.CoalesceCellMaps;
            if (st.Step == (int)DegradationStep.ShrinkRadius && st.Scale > _s.MinRadiusScale)
            {
                if (now - st.LastRadiusAdjust >= _s.RadiusStepSeconds)
                {
                    st.Scale = Math.Max(_s.MinRadiusScale, st.Scale * _s.RadiusShrinkFactor);
                    st.LastRadiusAdjust = now;
                    st.LastChange = now;
                }
                return;
            }
            if (st.Step < max && overFor >= _s.DegradeAfterSeconds && sinceChange >= _s.DegradeAfterSeconds)
            {
                Volatile.Write(ref st.Step, st.Step + 1);
                st.LastChange = now;
                st.OverSince = now;
                if (st.Step == (int)DegradationStep.ShrinkRadius)
                {
                    st.Scale = Math.Max(_s.MinRadiusScale, st.Scale * _s.RadiusShrinkFactor);
                    st.LastRadiusAdjust = now;
                }
            }
        }

        private void Recover(State st, double now)
        {
            double underFor = now - st.UnderSince;
            double sinceChange = now - st.LastChange;
            if (underFor < _s.RecoverAfterSeconds) return;
            if (st.Step == (int)DegradationStep.ShrinkRadius && st.Scale < 1f)
            {
                if (now - st.LastRadiusAdjust >= _s.RadiusStepSeconds)
                {
                    st.Scale = Math.Min(1f, st.Scale * _s.RadiusGrowFactor);
                    st.LastRadiusAdjust = now;
                    st.LastChange = now;
                }
                return;
            }
            if (st.Step > 0 && sinceChange >= _s.RecoverAfterSeconds)
            {
                Volatile.Write(ref st.Step, st.Step - 1);
                st.LastChange = now;
                st.UnderSince = now;
                if (st.Step < (int)DegradationStep.ShrinkRadius) st.Scale = 1f;
            }
        }

        private State GetOrCreate(PeerId peer)
        {
            State st = Volatile.Read(ref _states[peer.Value]);
            if (st != null) return st;
            var created = new State();
            return Interlocked.CompareExchange(ref _states[peer.Value], created, null) ?? created;
        }

        private void LogStep(PeerId peer, int before, int after, double budget, double demand)
        {
            ILog log = Log;
            if (log == null || !log.IsEnabled(LogLevel.Info)) return;
            log.Write(LogLevel.Info, LogCategory.Scheduling, peer + " degradation " + (DegradationStep)before + " -> " + (DegradationStep)after
                                                            + " (motion wants " + Kb(demand) + " KB/s, budget " + Kb(budget) + " KB/s)");
        }

        private static string Kb(double bytesPerSecond) => (bytesPerSecond / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);
    }
}
