using System;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Structured logging seam (ARCHITECTURE.md#logs, #code-rules). Implementations are thread-safe and
    /// rate-limit repeated messages; the game layer writes to the mod's log file (game-bootstrap <c>FileLog</c>).
    /// Callers check <see cref="IsEnabled"/> before building expensive messages (always for <see cref="LogLevel.Trace"/>).
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public interface ILog
    {
        bool IsEnabled(LogLevel level);

        /// <param name="level">Severity.</param>
        /// <param name="category">A short category such as <c>net</c>, <c>session</c>, <c>apply</c> (see <see cref="LogCategory"/>).</param>
        /// <param name="message">The message (no trailing newline).</param>
        /// <param name="exception">Optional exception; implementations log its type, message and stack trace.</param>
        void Write(LogLevel level, string category, string message, Exception exception = null);
    }
}
