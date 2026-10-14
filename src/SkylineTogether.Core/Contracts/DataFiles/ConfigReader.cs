using System.Globalization;
using System.Text;
using SkylineTogether.Core.Json;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Schema checks of the data-file parser (<see cref="LocaleTable"/>). Every error names the file, the JSON path and
    /// the source position of the offending value. Members whose names start with <c>_</c> are comments and ignored.
    /// </summary>
    internal sealed class ConfigReader
    {
        private readonly string _file;

        public ConfigReader(string file)
        {
            _file = file;
        }

        public ConfigValidationException Error(JsonValue node, string path, string message)
        {
            return new ConfigValidationException(_file, path, node?.Line ?? 0, node?.Column ?? 0, message);
        }

        public static string Child(string path, string key)
        {
            bool identifier = key.Length > 0;
            foreach (char c in key)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_')) identifier = false;
            }
            return identifier ? path + "." + key : path + "[\"" + key.Replace("\"", "\\\"") + "\"]";
        }

        public void RequireObject(JsonValue node, string path)
        {
            if (node == null || node.Kind != JsonKind.Object) throw Error(node, path, "expected an object, found " + KindName(node));
        }

        public static bool IsComment(string key) => key.Length > 0 && key[0] == '_';

        /// <summary>Restricts identifiers (locale ids, system-state ids, member names) to letters, digits and the given punctuation.</summary>
        public void CheckIdentifier(JsonValue node, string path, string s, string punctuation, int maxLength)
        {
            if (string.IsNullOrEmpty(s)) throw Error(node, path, "an identifier must not be empty");
            if (s.Length > maxLength) throw Error(node, path, "identifier longer than " + maxLength.ToString(CultureInfo.InvariantCulture) + " characters");
            foreach (char c in s)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || punctuation.IndexOf(c) >= 0;
                if (!ok) throw Error(node, path, "identifier \"" + s + "\" may only contain letters, digits and " + Describe(punctuation));
            }
        }

        public static string KindName(JsonValue node)
        {
            if (node == null) return "nothing";
            switch (node.Kind)
            {
                case JsonKind.Null: return "null";
                case JsonKind.Bool: return "a boolean";
                case JsonKind.Number: return "a number";
                case JsonKind.String: return "a string";
                case JsonKind.Array: return "an array";
                default: return "an object";
            }
        }

        private static string Describe(string punctuation)
        {
            var sb = new StringBuilder();
            foreach (char c in punctuation)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append('\'').Append(c).Append('\'');
            }
            return sb.ToString();
        }
    }
}
