using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Testing
{
    /// <summary>
    /// A thread-safe in-memory log for tests. Each entry becomes one line <c>"Level [category] message"</c>, plus
    /// <c>" | ExceptionType: message"</c> when an exception was given. Entries below the minimum level are ignored.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class ListLog : ILog
    {
        private readonly object _lock = new object();
        private readonly List<string> _lines = new List<string>();
        private readonly List<LogLevel> _levels = new List<LogLevel>();
        private readonly LogLevel _minimum;

        public ListLog() : this(LogLevel.Trace)
        {
        }

        public ListLog(LogLevel minimum)
        {
            _minimum = minimum;
        }

        /// <summary>A snapshot of the lines written so far.</summary>
        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lock)
                {
                    return _lines.ToArray();
                }
            }
        }

        public bool IsEnabled(LogLevel level) => level >= _minimum;

        public void Write(LogLevel level, string category, string message, Exception exception = null)
        {
            if (level < _minimum) return;
            string line = level + " [" + (category ?? string.Empty) + "] " + (message ?? string.Empty);
            if (exception != null) line += " | " + exception.GetType().Name + ": " + exception.Message;
            lock (_lock)
            {
                _lines.Add(line);
                _levels.Add(level);
            }
        }

        /// <summary>Number of entries at <paramref name="atLeast"/> or above.</summary>
        public int Count(LogLevel atLeast)
        {
            lock (_lock)
            {
                int count = 0;
                foreach (LogLevel level in _levels)
                {
                    if (level >= atLeast) count++;
                }
                return count;
            }
        }

        /// <summary>True when some line contains <paramref name="text"/> (ordinal).</summary>
        public bool Contains(string text)
        {
            lock (_lock)
            {
                foreach (string line in _lines)
                {
                    if (line.IndexOf(text, StringComparison.Ordinal) >= 0) return true;
                }
                return false;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _lines.Clear();
                _levels.Clear();
            }
        }
    }
}
