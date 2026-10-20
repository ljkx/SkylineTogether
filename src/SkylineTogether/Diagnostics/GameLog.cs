using System;
using System.Collections.Generic;
using System.Globalization;
using Colossal.Logging;
using SkylineTogether.Core;
using CoreLevel = SkylineTogether.Core.LogLevel;

namespace SkylineTogether.Diagnostics
{
    /// <summary>
    /// Core's logging seam written to the game's log folder (<c>Logs/SkylineTogether.log</c>). Keeps the most recent
    /// lines in memory for the diagnostics dump (ARCHITECTURE.md#logs). Every line, and the text of an exception, goes
    /// through <see cref="Core.Diagnostics.PathRedactor"/>: paths in the user's folder are written as %USERPROFILE%, so
    /// the file does not contain the Windows user name.
    /// </summary>
    public sealed class GameLog : Core.ILog
    {
        private const int RecentCapacity = 400;

        private readonly Colossal.Logging.ILog _log;
        private readonly Queue<string> _recent = new Queue<string>(RecentCapacity);
        private readonly object _recentLock = new object();
        // bounds the file whatever peers send: a rate limit and a size cap per run (Core.Diagnostics.LogFloodGuard)
        private readonly Core.Diagnostics.LogFloodGuard _flood = new Core.Diagnostics.LogFloodGuard();
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        public GameLog(string name)
        {
            _log = LogManager.GetLogger(name).SetShowsErrorsInUI(false);
        }

        public CoreLevel Minimum { get; set; } = CoreLevel.Info;

        /// <summary>The log file's path, or null when the game's logger has none.</summary>
        public string LogPath
        {
            get
            {
                try
                {
                    return _log.logPath;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public bool IsEnabled(CoreLevel level) => level >= Minimum;

        /// <summary>The most recent lines (oldest first), at most a few hundred.</summary>
        public string[] RecentLines()
        {
            lock (_recentLock) return _recent.ToArray();
        }

        public void Write(CoreLevel level, string category, string message, Exception exception = null)
        {
            if (level < Minimum) return;
            string text = Core.Diagnostics.PathRedactor.Redact("[" + category + "] " + message);
            Remember(level, text, exception);
            // the exception is written as redacted text (an IOException's message names the file it failed on)
            if (exception != null) text += Environment.NewLine + Core.Diagnostics.PathRedactor.Redact(exception.ToString());
            if (!_flood.Allow(level, text.Length, Clock.Elapsed.TotalSeconds, out string notice)) return;
            if (notice != null) _log.Warn("[" + LogCategory.Session + "] " + notice);
            switch (level)
            {
                case CoreLevel.Trace:
                case CoreLevel.Debug:
                    _log.Debug(text);
                    break;
                case CoreLevel.Info:
                    _log.Info(text);
                    break;
                case CoreLevel.Warn:
                    _log.Warn(text);
                    break;
                default:
                    _log.Error(text);
                    break;
            }
        }

        private void Remember(CoreLevel level, string text, Exception exception)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + level.ToString().ToUpperInvariant() + " " + text
                          + (exception != null ? " | " + exception.GetType().Name + ": " + Core.Diagnostics.PathRedactor.Redact(exception.Message) : "");
            lock (_recentLock)
            {
                if (_recent.Count >= RecentCapacity) _recent.Dequeue();
                _recent.Enqueue(line);
            }
        }
    }
}
