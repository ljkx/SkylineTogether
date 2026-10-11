using System;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Convenience writers over <see cref="ILog"/>. Each checks <see cref="ILog.IsEnabled"/> first and tolerates a null
    /// log, so call sites stay one line. For trace messages that are expensive to build, check
    /// <c>log.IsEnabled(LogLevel.Trace)</c> before formatting.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class LogExtensions
    {
        public static void Trace(this ILog log, string category, string message) => Emit(log, LogLevel.Trace, category, message, null);

        public static void Debug(this ILog log, string category, string message) => Emit(log, LogLevel.Debug, category, message, null);

        public static void Info(this ILog log, string category, string message) => Emit(log, LogLevel.Info, category, message, null);

        public static void Warn(this ILog log, string category, string message, Exception exception = null) => Emit(log, LogLevel.Warn, category, message, exception);

        public static void Error(this ILog log, string category, string message, Exception exception = null) => Emit(log, LogLevel.Error, category, message, exception);

        public static void Critical(this ILog log, string category, string message, Exception exception = null) => Emit(log, LogLevel.Critical, category, message, exception);

        private static void Emit(ILog log, LogLevel level, string category, string message, Exception exception)
        {
            if (log == null || !log.IsEnabled(level)) return;
            log.Write(level, category, message, exception);
        }
    }
}
