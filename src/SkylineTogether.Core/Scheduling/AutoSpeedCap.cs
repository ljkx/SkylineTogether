using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// The automatic speed cap (docs/history/ARCHITECTURE-original.md §4.8.5, session option "automatic speed cap", default on): when a peer's link cannot
    /// sustain P0 at the current speed, the host lowers the session speed one step, and again every
    /// <c>overloadSeconds</c> while some peer stays overloaded; once no peer has been overloaded for
    /// <c>recoverySeconds</c> ("until that peer has had 60 s of headroom") the cap rises one step, and again after each
    /// further period of headroom, until it no longer limits the requested speed.
    /// <para>
    /// Input: one flag per live peer, normally the peer scheduler's "P0 overloaded" flag, which already requires the
    /// backlog to stay above 2 s for 30 s. The first step down therefore happens as soon as a flag is set, and
    /// <c>overloadSeconds</c> is the time each step is given to take effect before the next one. Speeds are the session's
    /// speed steps (1 = slowest); the cap never goes below <see cref="MinimumSpeed"/> and never pauses the game; a
    /// requested speed at or below the minimum (or 0, paused) passes through unchanged. Main thread (session controller).
    /// </para>
    /// </summary>
    public sealed class AutoSpeedCap
    {
        private readonly double _overloadSeconds;
        private readonly double _recoverySeconds;
        private readonly List<int> _overloaded = new List<int>();
        private int _cap;                       // 0 = not capping
        private double _lastStepDown = double.NegativeInfinity;
        private double _headroomSince = double.NaN;
        private int _lastEffective = -1;

        /// <param name="overloadSeconds">Time a step down is given before the next one (default 30 s).</param>
        /// <param name="recoverySeconds">Headroom needed before each step up (default 60 s).</param>
        public AutoSpeedCap(double overloadSeconds = 30, double recoverySeconds = 60)
        {
            _overloadSeconds = IsValid(overloadSeconds) ? overloadSeconds : 30;
            _recoverySeconds = IsValid(recoverySeconds) ? recoverySeconds : 60;
        }

        /// <summary>The cap never goes below this speed step (default 1).</summary>
        public int MinimumSpeed { get; set; } = 1;

        /// <summary>True while the cap limits (or would limit) the requested speed.</summary>
        public bool IsCapping => _cap > 0;

        /// <summary>The current cap (0 when not capping).</summary>
        public int CapSpeed => _cap;

        /// <summary>Indices (into the last <c>peerOverloaded</c> list) of the peers that were overloaded at the last evaluation.</summary>
        public IReadOnlyList<int> OverloadedIndices => _overloaded;

        /// <summary>Optional log for cap changes (category <see cref="LogCategory.Scheduling"/>).</summary>
        public ILog Log { get; set; }

        /// <summary>
        /// Returns the speed the session should run at: <paramref name="requestedSpeed"/>, limited by the cap.
        /// <paramref name="overloadedPeerNote"/> is a diagnostic note (English, for the log and the dump; the HUD builds
        /// its own localised text from <see cref="OverloadedIndices"/>), or null when no peer is overloaded and no cap applies.
        /// </summary>
        /// <param name="requestedSpeed">The speed the players asked for (session speed step; 0 = paused).</param>
        /// <param name="peerOverloaded">One flag per live peer (null = none).</param>
        /// <param name="now">Monotonic time in seconds.</param>
        /// <param name="overloadedPeerNote">The note described above.</param>
        public int Evaluate(int requestedSpeed, IReadOnlyList<bool> peerOverloaded, double now, out string overloadedPeerNote)
        {
            _overloaded.Clear();
            if (peerOverloaded != null)
            {
                for (int i = 0; i < peerOverloaded.Count; i++)
                {
                    if (peerOverloaded[i]) _overloaded.Add(i);
                }
            }
            int minimum = Math.Max(1, MinimumSpeed);
            bool anyOverloaded = _overloaded.Count > 0;
            if (double.IsNaN(now) || double.IsInfinity(now)) now = _lastStepDown > double.NegativeInfinity ? _lastStepDown : 0;
            int cappedBefore = _cap;

            if (requestedSpeed <= 0)
            {
                // paused: nothing to cap, and the speed the session returns to is unknown, so the cap and its timers wait
                overloadedPeerNote = BuildNote(requestedSpeed, requestedSpeed);
                return requestedSpeed;
            }

            if (anyOverloaded)
            {
                _headroomSince = double.NaN;
                if (requestedSpeed > minimum)
                {
                    if (_cap == 0)
                    {
                        _cap = Math.Max(minimum, requestedSpeed - 1);
                        _lastStepDown = now;
                    }
                    else if (now - _lastStepDown >= _overloadSeconds || now < _lastStepDown)
                    {
                        int current = Math.Min(_cap, requestedSpeed);
                        _cap = Math.Max(minimum, current - 1);
                        _lastStepDown = now;
                    }
                }
            }
            else if (_cap > 0)
            {
                if (double.IsNaN(_headroomSince) || now < _headroomSince) _headroomSince = now;
                if (now - _headroomSince >= _recoverySeconds)
                {
                    _cap++;
                    _headroomSince = now;
                    if (_cap >= requestedSpeed) _cap = 0;     // no longer limiting
                }
            }

            int effective = _cap > 0 && requestedSpeed > _cap ? _cap : requestedSpeed;

            overloadedPeerNote = BuildNote(requestedSpeed, effective);
            if (_cap != cappedBefore || (effective != _lastEffective && _lastEffective >= 0)) LogChange(requestedSpeed, effective, overloadedPeerNote);
            _lastEffective = effective;
            return effective;
        }

        /// <summary>Lifts the cap and forgets the timers (session reset).</summary>
        public void Reset()
        {
            _cap = 0;
            _lastStepDown = double.NegativeInfinity;
            _headroomSince = double.NaN;
            _overloaded.Clear();
            _lastEffective = -1;
        }

        private string BuildNote(int requested, int effective)
        {
            if (_overloaded.Count == 0 && _cap == 0) return null;
            var sb = new StringBuilder();
            if (_overloaded.Count > 0)
            {
                sb.Append("peer index ");
                for (int i = 0; i < _overloaded.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(_overloaded[i].ToString(CultureInfo.InvariantCulture));
                }
                sb.Append(_overloaded.Count == 1 ? " cannot" : " cannot all").Append(" keep up at speed ").Append(requested.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append("no peer overloaded");
            }
            if (_cap > 0) sb.Append("; session speed capped at ").Append(effective.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private void LogChange(int requested, int effective, string note)
        {
            ILog log = Log;
            if (log == null || !log.IsEnabled(LogLevel.Info)) return;
            log.Write(LogLevel.Info, LogCategory.Scheduling, "automatic speed cap: requested " + requested.ToString(CultureInfo.InvariantCulture)
                                                            + ", effective " + effective.ToString(CultureInfo.InvariantCulture)
                                                            + (note != null ? " (" + note + ")" : string.Empty));
        }

        private static bool IsValid(double seconds) => !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds >= 0;
    }
}
