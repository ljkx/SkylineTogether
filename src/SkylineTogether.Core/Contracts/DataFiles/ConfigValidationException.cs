using System;
using System.Globalization;

namespace SkylineTogether.Core
{
    /// <summary>
    /// A data file (gates, cosmetic list, policies, adapters, system state, locale) is malformed or violates its schema.
    /// Names the file, the JSON path of the offending value (for example <c>$.entries[3].action</c>) and, when known,
    /// its line and column. Critical tables that fail to parse stop hosting and joining with this message (ARCH §6.3).
    /// </summary>
    public sealed class ConfigValidationException : Exception
    {
        public ConfigValidationException(string file, string path, string message)
            : this(file, path, 0, 0, message)
        {
        }

        /// <param name="file">The data file (null when unknown).</param>
        /// <param name="path">JSON path of the offending value (null for syntax errors).</param>
        /// <param name="line">1-based line, 0 when unknown.</param>
        /// <param name="column">1-based column, 0 when unknown.</param>
        /// <param name="message">What is wrong.</param>
        public ConfigValidationException(string file, string path, int line, int column, string message)
            : base(Format(file, path, line, column, message))
        {
            File = file;
            Path = path;
            Line = line;
            Column = column;
            Problem = message;
        }

        /// <summary>The data file, or null.</summary>
        public string File { get; }

        /// <summary>JSON path of the offending value, or null.</summary>
        public string Path { get; }

        /// <summary>1-based line, 0 when unknown.</summary>
        public int Line { get; }

        /// <summary>1-based column, 0 when unknown.</summary>
        public int Column { get; }

        /// <summary>The problem without file and position.</summary>
        public string Problem { get; }

        private static string Format(string file, string path, int line, int column, string message)
        {
            string where = string.IsNullOrEmpty(file) ? "<json>" : file;
            if (!string.IsNullOrEmpty(path)) where += ": " + path;
            if (line > 0)
            {
                where += (string.IsNullOrEmpty(path) ? ": " : " (") + "line " + line.ToString(CultureInfo.InvariantCulture)
                         + ", column " + column.ToString(CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(path) ? string.Empty : ")");
            }
            return where + ": " + message;
        }
    }
}
