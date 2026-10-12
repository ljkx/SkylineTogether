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
    /// lines in memory for the diagnostics dump (ARCH §13.1).
    /// </summary>
    public sealed class GameLog : Core.ILog
    {
        private const int RecentCapacity = 400;

        private readonly Colossal.Logging.ILog _log;
        private readonly Queue<string> _recent = new Queue<string>(RecentCapacity);
        private readonly object _recentLock = new object();

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
            string text = "[" + category + "] " + message;
            Remember(level, text, exception);
            switch (level)
            {
                case CoreLevel.Trace:
                case CoreLevel.Debug:
                    if (exception != null) _log.Debug(exception, text);
                    else _log.Debug(text);
                    break;
                case CoreLevel.Info:
                    if (exception != null) _log.Info(exception, text);
                    else _log.Info(text);
                    break;
                case CoreLevel.Warn:
                    if (exception != null) _log.Warn(exception, text);
                    else _log.Warn(text);
                    break;
                default:
                    if (exception != null) _log.Error(exception, text);
                    else _log.Error(text);
                    break;
            }
        }

        private void Remember(CoreLevel level, string text, Exception exception)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + level.ToString().ToUpperInvariant() + " " + text
                          + (exception != null ? " | " + exception.GetType().Name + ": " + exception.Message : "");
            lock (_recentLock)
            {
                if (_recent.Count >= RecentCapacity) _recent.Dequeue();
                _recent.Enqueue(line);
            }
        }
    }
}
