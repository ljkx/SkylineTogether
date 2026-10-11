using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SkylineTogether.Core.Tests.Framework
{
    /// <summary>
    /// Assertion helpers. Every failing assertion throws <see cref="AssertionException"/> with a message that shows the
    /// expected and the actual value (integers also in hex, byte sequences as hex, strings quoted and escaped).
    /// </summary>
    public static class Assert
    {
        public static void True(bool condition, string message = null)
        {
            if (!condition) Fail(message ?? "expected true, got false");
        }

        public static void False(bool condition, string message = null)
        {
            if (condition) Fail(message ?? "expected false, got true");
        }

        public static void Equal<T>(T expected, T actual, string message = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                Fail(Prefix(message) + "expected " + Format(expected) + ", actual " + Format(actual));
            }
        }

        public static void NotEqual<T>(T notExpected, T actual, string message = null)
        {
            if (EqualityComparer<T>.Default.Equals(notExpected, actual))
            {
                Fail(Prefix(message) + "did not expect " + Format(actual));
            }
        }

        public static void Same(object expected, object actual, string message = null)
        {
            if (!ReferenceEquals(expected, actual)) Fail(Prefix(message) + "expected the same instance");
        }

        public static void NotSame(object notExpected, object actual, string message = null)
        {
            if (ReferenceEquals(notExpected, actual)) Fail(Prefix(message) + "expected a different instance");
        }

        public static void Null(object value, string message = null)
        {
            if (value != null) Fail(Prefix(message) + "expected null, actual " + Format(value));
        }

        public static void NotNull(object value, string message = null)
        {
            if (value == null) Fail(Prefix(message) + "expected a non-null value");
        }

        /// <summary>Element-wise equality; reports the first differing index and both lengths.</summary>
        public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message = null)
        {
            if (expected == null || actual == null)
            {
                if (expected == null && actual == null) return;
                Fail(Prefix(message) + "expected " + Format(expected) + ", actual " + Format(actual));
            }
            var e = new List<T>(expected);
            var a = new List<T>(actual);
            int n = Math.Min(e.Count, a.Count);
            var comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < n; i++)
            {
                if (!comparer.Equals(e[i], a[i]))
                {
                    Fail(Prefix(message) + "sequences differ at index " + i + ": expected " + Format(e[i]) + ", actual " + Format(a[i])
                         + " (lengths " + e.Count + " and " + a.Count + ")" + Environment.NewLine
                         + "     expected " + Format(e) + Environment.NewLine + "     actual   " + Format(a));
                }
            }
            if (e.Count != a.Count)
            {
                Fail(Prefix(message) + "sequence lengths differ: expected " + e.Count + ", actual " + a.Count + Environment.NewLine
                     + "     expected " + Format(e) + Environment.NewLine + "     actual   " + Format(a));
            }
        }

        /// <summary>Asserts that <paramref name="action"/> throws <typeparamref name="TException"/> or a subclass of it.</summary>
        public static TException Throws<TException>(Action action, string message = null) where TException : Exception
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            try
            {
                action();
            }
            catch (TException expected)
            {
                return expected;
            }
            catch (Exception other)
            {
                Fail(Prefix(message) + "expected " + typeof(TException).Name + ", got " + other.GetType().Name + ": " + other.Message);
            }
            Fail(Prefix(message) + "expected " + typeof(TException).Name + ", but nothing was thrown");
            return null;
        }

        public static void DoesNotThrow(Action action, string message = null)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            try
            {
                action();
            }
            catch (Exception e)
            {
                Fail(Prefix(message) + "unexpected " + e.GetType().Name + ": " + e.Message);
            }
        }

        public static void Contains(string expectedSubstring, string actual, string message = null)
        {
            if (actual == null || actual.IndexOf(expectedSubstring, StringComparison.Ordinal) < 0)
            {
                Fail(Prefix(message) + "expected " + Format(actual) + " to contain " + Format(expectedSubstring));
            }
        }

        public static void DoesNotContain(string unexpectedSubstring, string actual, string message = null)
        {
            if (actual != null && actual.IndexOf(unexpectedSubstring, StringComparison.Ordinal) >= 0)
            {
                Fail(Prefix(message) + "expected " + Format(actual) + " not to contain " + Format(unexpectedSubstring));
            }
        }

        public static void Contains<T>(T expected, IEnumerable<T> collection, string message = null)
        {
            foreach (T item in collection)
            {
                if (EqualityComparer<T>.Default.Equals(item, expected)) return;
            }
            Fail(Prefix(message) + "expected " + Format(collection) + " to contain " + Format(expected));
        }

        public static void InRange(double actual, double low, double high, string message = null)
        {
            if (!(actual >= low && actual <= high))
            {
                Fail(Prefix(message) + "expected a value in [" + Format(low) + ", " + Format(high) + "], actual " + Format(actual));
            }
        }

        public static void Near(double expected, double actual, double tolerance, string message = null)
        {
            if (!(Math.Abs(expected - actual) <= tolerance))
            {
                Fail(Prefix(message) + "expected " + Format(expected) + " ± " + Format(tolerance) + ", actual " + Format(actual));
            }
        }

        public static void Fail(string message)
        {
            throw new AssertionException(message ?? "assertion failed");
        }

        /// <summary>Stops the test and reports it as skipped (for example when an optional input file is absent).</summary>
        public static void Skip(string reason)
        {
            throw new SkipException(reason ?? "skipped");
        }

        private static string Prefix(string message) => string.IsNullOrEmpty(message) ? string.Empty : message + ": ";

        /// <summary>Formats a value for assertion messages.</summary>
        public static string Format(object value)
        {
            switch (value)
            {
                case null: return "null";
                case string s: return Quote(s);
                case char c: return Quote(c.ToString());
                case bool b: return b ? "true" : "false";
                case byte[] bytes: return "[" + Hex(bytes, 0, bytes.Length) + "] (" + bytes.Length + " bytes)";
                case ArraySegment<byte> segment: return "[" + Hex(segment.Array, segment.Offset, segment.Count) + "] (" + segment.Count + " bytes)";
                case ulong u: return u.ToString(CultureInfo.InvariantCulture) + " (0x" + u.ToString("X16", CultureInfo.InvariantCulture) + ")";
                case long l: return l.ToString(CultureInfo.InvariantCulture) + " (0x" + l.ToString("X16", CultureInfo.InvariantCulture) + ")";
                case uint ui: return ui.ToString(CultureInfo.InvariantCulture) + " (0x" + ui.ToString("X8", CultureInfo.InvariantCulture) + ")";
                case int i: return i.ToString(CultureInfo.InvariantCulture) + " (0x" + i.ToString("X8", CultureInfo.InvariantCulture) + ")";
                case float f: return f.ToString("R", CultureInfo.InvariantCulture) + "f";
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case IFormattable formattable: return formattable.ToString(null, CultureInfo.InvariantCulture);
                case IEnumerable enumerable:
                {
                    var sb = new StringBuilder("{");
                    int count = 0;
                    foreach (object item in enumerable)
                    {
                        if (count > 0) sb.Append(", ");
                        if (count == 32) { sb.Append("..."); break; }
                        sb.Append(Format(item));
                        count++;
                    }
                    return sb.Append('}').ToString();
                }
                default: return value.ToString();
            }
        }

        private static string Hex(byte[] data, int offset, int count)
        {
            if (data == null) return "null";
            var sb = new StringBuilder(Math.Min(count, 64) * 3);
            int shown = Math.Min(count, 64);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[offset + i].ToString("x2", CultureInfo.InvariantCulture));
            }
            if (count > shown) sb.Append(" ...");
            return sb.ToString();
        }

        private static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2).Append('"');
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20 || (c >= 0xD800 && c <= 0xDFFF)) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
