using System;
using System.Globalization;

namespace SkylineTogether.Core.Transport
{
    /// <summary>Limits and steps of a <see cref="SendRateController"/> (loadspeed).</summary>
    public sealed class SendRateOptions
    {
        /// <summary>The rate a connection starts with. Steam's own default is a fixed 128-256 KiB/s (no estimation).</summary>
        public int InitialBytesPerSecond = 1 << 20;

        /// <summary>The rate never drops below this.</summary>
        public int MinBytesPerSecond = 256 << 10;

        /// <summary>The rate never rises above this.</summary>
        public int MaxBytesPerSecond = 48 << 20;

        /// <summary>Without a remote delivery estimate (<see cref="ConnectionStats.RemoteQuality"/> unknown) increases stop here.</summary>
        public int BlindMaxBytesPerSecond = 8 << 20;

        /// <summary>Seconds between two decisions (Steam's statistics are smoothed over about that long).</summary>
        public double IntervalSeconds = 0.5;

        /// <summary>Multiplicative increase per decision while the link is busy and clean.</summary>
        public double IncreaseFactor = 1.5;

        /// <summary>Multiplicative decrease when the link shows loss or queueing delay.</summary>
        public double DecreaseFactor = 0.7;

        /// <summary>Remote delivery at or above this counts as clean (an increase is allowed).</summary>
        public float GoodQuality = 0.97f;

        /// <summary>Remote delivery below this is loss caused by sending too fast (the rate backs off).</summary>
        public float BadQuality = 0.93f;

        /// <summary>The link is busy (the rate limits it) while the transport holds at least this many seconds of data at the current rate.</summary>
        public double BusySeconds = 0.1;

        /// <summary>No increase this long after a decrease (the statistics lag behind the change).</summary>
        public double HoldSeconds = 2.0;

        /// <summary>A ping above the baseline × this factor and above baseline + <see cref="DelayAllowanceMs"/> is queueing delay.</summary>
        public double DelayFactor = 2.0;

        /// <summary>See <see cref="DelayFactor"/>.</summary>
        public int DelayAllowanceMs = 60;

        /// <summary>The ping baseline is the lowest ping of the last window of this many seconds (a route change can raise it).</summary>
        public double BaselineWindowSeconds = 30.0;

        public SendRateOptions Clone() => (SendRateOptions)MemberwiseClone();
    }

    /// <summary>
    /// loadspeed: chooses a connection's send rate (Steam <c>k_ESteamNetworkingConfig_SendRateMin/Max</c>, which the
    /// game layer sets to the same value: Steam does not estimate the bandwidth itself and otherwise sends at its fixed
    /// default of 128-256 KiB/s, so a 100 MB city took minutes over a relay). AIMD on the link's own signals, decided
    /// every <see cref="SendRateOptions.IntervalSeconds"/>:
    /// <list type="bullet">
    /// <item>back off (× 0.7) when the remote end reports loss (<see cref="ConnectionStats.RemoteQuality"/> below
    /// 0.93) or the ping rises well above its baseline (queueing in a router: the uplink is full);</item>
    /// <item>speed up (× 1.5) only while the rate limits the link (the transport holds at least 0.1 s of data) and
    /// delivery is clean, never within 2 s of a back-off, and only to 8 MiB/s while the remote quality is unknown;</item>
    /// <item>otherwise keep the rate (an idle or demand-limited link never ramps up blindly).</item>
    /// </list>
    /// Pure (the caller passes the time and the statistics); one instance per connection; not thread-safe.
    /// </summary>
    public sealed class SendRateController
    {
        private readonly SendRateOptions _options;
        private double _nextDecision = double.NegativeInfinity;
        private double _holdUntil = double.NegativeInfinity;
        private int _baselinePing = int.MaxValue;
        private int _windowMinPing = int.MaxValue;
        private double _windowStart = double.NaN;

        public SendRateController(SendRateOptions options = null)
        {
            _options = (options ?? new SendRateOptions()).Clone();
            if (_options.MinBytesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MinBytesPerSecond must be positive");
            if (_options.MaxBytesPerSecond < _options.MinBytesPerSecond) throw new ArgumentOutOfRangeException(nameof(options), "MaxBytesPerSecond must not be below MinBytesPerSecond");
            if (!(_options.IncreaseFactor > 1) || !(_options.DecreaseFactor > 0 && _options.DecreaseFactor < 1)) throw new ArgumentOutOfRangeException(nameof(options), "the factors must increase and decrease");
            Rate = Clamp(_options.InitialBytesPerSecond, _options.MaxBytesPerSecond);
        }

        /// <summary>The send rate in bytes per second the connection should use now.</summary>
        public int Rate { get; private set; }

        /// <summary>The lowest recent ping (the uncongested round trip), 0 while unknown.</summary>
        public int BaselinePingMs => _baselinePing == int.MaxValue ? 0 : _baselinePing;

        public int Increases { get; private set; }

        public int Decreases { get; private set; }

        /// <summary>Why the rate last changed (diagnostics).</summary>
        public string LastChange { get; private set; } = "initial";

        /// <summary>The highest rate reached so far.</summary>
        public int PeakRate { get; private set; }

        /// <summary>
        /// Takes the connection's current statistics; returns true when <see cref="Rate"/> changed (the caller applies it
        /// to the transport). Calls between decisions only track the ping baseline.
        /// </summary>
        public bool Update(double now, in ConnectionStats stats)
        {
            if (PeakRate < Rate) PeakRate = Rate;
            TrackPing(now, stats.PingMs);
            if (now < _nextDecision) return false;
            _nextDecision = now + _options.IntervalSeconds;

            float quality = stats.RemoteQuality;
            bool qualityKnown = quality > 0f;
            bool lossy = qualityKnown && quality < _options.BadQuality;
            bool delayed = IsDelayed(stats.PingMs);
            int old = Rate;
            if (lossy || delayed)
            {
                Rate = Clamp((int)(Rate * _options.DecreaseFactor), _options.MaxBytesPerSecond);
                _holdUntil = now + _options.HoldSeconds;
                if (Rate == old) return false;
                Decreases++;
                LastChange = (lossy ? "loss (remote quality " + quality.ToString("0.000", CultureInfo.InvariantCulture) + ")" : "queueing delay (ping " + stats.PingMs + " ms, baseline " + BaselinePingMs + " ms)");
                return true;
            }
            long pending = Math.Max(0L, stats.PendingReliableBytes) + Math.Max(0L, stats.PendingUnreliableBytes);
            bool busy = pending >= Rate * _options.BusySeconds;
            bool clean = qualityKnown ? quality >= _options.GoodQuality : true;
            int ceiling = qualityKnown ? _options.MaxBytesPerSecond : Math.Min(_options.MaxBytesPerSecond, _options.BlindMaxBytesPerSecond);
            if (!busy || !clean || now < _holdUntil || Rate >= ceiling) return false;
            Rate = Clamp((int)Math.Min(int.MaxValue, Rate * _options.IncreaseFactor), ceiling);
            if (Rate == old) return false;
            Increases++;
            if (PeakRate < Rate) PeakRate = Rate;
            LastChange = "busy and clean";
            return true;
        }

        public override string ToString()
        {
            var ci = CultureInfo.InvariantCulture;
            return "send rate " + (Rate / 1048576.0).ToString("0.00", ci) + " MiB/s (peak " + (PeakRate / 1048576.0).ToString("0.00", ci) + ", "
                   + Increases.ToString(ci) + " up, " + Decreases.ToString(ci) + " down, last: " + LastChange + ", ping baseline " + BaselinePingMs.ToString(ci) + " ms)";
        }

        private bool IsDelayed(int ping)
        {
            if (ping <= 0 || _baselinePing == int.MaxValue) return false;
            double limit = Math.Max(_baselinePing * _options.DelayFactor, _baselinePing + _options.DelayAllowanceMs);
            return ping > limit;
        }

        private void TrackPing(double now, int ping)
        {
            if (ping <= 0) return;
            if (double.IsNaN(_windowStart)) _windowStart = now;
            if (ping < _baselinePing) _baselinePing = ping;
            if (ping < _windowMinPing) _windowMinPing = ping;
            if (now - _windowStart >= _options.BaselineWindowSeconds)
            {
                // a new window: the baseline follows a route whose round trip went up
                _baselinePing = _windowMinPing;
                _windowMinPing = ping;
                _windowStart = now;
            }
        }

        private int Clamp(int rate, int ceiling)
        {
            if (rate < _options.MinBytesPerSecond) return _options.MinBytesPerSecond;
            return rate > ceiling ? Math.Max(ceiling, _options.MinBytesPerSecond) : rate;
        }
    }
}
