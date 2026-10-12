using System;
using Colossal.Logging;
using SkylineTogether.Core;
using CoreLevel = SkylineTogether.Core.LogLevel;

namespace SkylineTogether.Diagnostics
{
    /// <summary>Core's logging seam written to the game's log folder (<c>Logs/SkylineTogether.log</c>).</summary>
    public sealed class GameLog : Core.ILog
    {
        private readonly Colossal.Logging.ILog _log;

        public GameLog(string name)
        {
            _log = LogManager.GetLogger(name).SetShowsErrorsInUI(false);
        }

        public CoreLevel Minimum { get; set; } = CoreLevel.Info;

        public bool IsEnabled(CoreLevel level) => level >= Minimum;

        public void Write(CoreLevel level, string category, string message, Exception exception = null)
        {
            if (level < Minimum) return;
            string text = "[" + category + "] " + message;
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
    }
}
