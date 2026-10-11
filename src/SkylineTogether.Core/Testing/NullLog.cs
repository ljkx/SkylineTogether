using System;

namespace SkylineTogether.Core.Testing
{
    /// <summary>A log that discards everything (<see cref="IsEnabled"/> is always false).</summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class NullLog : ILog
    {
        public static readonly NullLog Instance = new NullLog();

        private NullLog()
        {
        }

        public bool IsEnabled(LogLevel level) => false;

        public void Write(LogLevel level, string category, string message, Exception exception = null)
        {
        }
    }
}
