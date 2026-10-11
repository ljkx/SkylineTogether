using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using SkylineTogether.Core.Json;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Shared schema checks of the data-file parsers. Every error names the file, the JSON path and the source position
    /// of the offending value. Conventions of all data files: member names are camelCase and case-sensitive; members
    /// whose names start with <c>_</c> are comments and ignored; every other unknown member is an error (typos must
    /// not silently disable an entry); enum names match case-insensitively but never as numbers.
    /// </summary>
    internal sealed class ConfigReader
    {
        public const int MaxNameLength = 512;

        private readonly string _file;

        public ConfigReader(string file)
        {
            _file = file;
        }

        public string File => _file;

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

        public static string Index(string path, int index) => path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";

        public void RequireObject(JsonValue node, string path)
        {
            if (node == null || node.Kind != JsonKind.Object) throw Error(node, path, "expected an object, found " + KindName(node));
        }

        public void RequireArray(JsonValue node, string path)
        {
            if (node == null || node.Kind != JsonKind.Array) throw Error(node, path, "expected an array, found " + KindName(node));
        }

        /// <summary>Rejects members that are neither in <paramref name="allowed"/> nor comments (leading underscore).</summary>
        public void CheckKeys(JsonValue obj, string path, params string[] allowed)
        {
            foreach (string key in obj.Keys)
            {
                if (key.Length > 0 && key[0] == '_') continue;
                if (Array.IndexOf(allowed, key) >= 0) continue;
                throw Error(obj[key], Child(path, key), "unknown member \"" + key + "\" (expected one of: " + string.Join(", ", allowed) + ")");
            }
        }

        public static bool IsComment(string key) => key.Length > 0 && key[0] == '_';

        public string GetString(JsonValue obj, string path, string key, bool required, string fallback = null)
        {
            if (!obj.Has(key))
            {
                if (required) throw Error(obj, Child(path, key), "required member \"" + key + "\" is missing");
                return fallback;
            }
            JsonValue value = obj[key];
            if (value.Kind == JsonKind.Null && !required) return fallback;
            if (value.Kind != JsonKind.String) throw Error(value, Child(path, key), "expected a string, found " + KindName(value));
            string s = value.AsString();
            CheckText(value, Child(path, key), s);
            return s;
        }

        /// <summary>A non-empty name: no control characters, no leading or trailing whitespace, at most 512 characters.</summary>
        public string GetName(JsonValue obj, string path, string key, bool required)
        {
            string s = GetString(obj, path, key, required);
            if (s == null) return null;
            CheckName(obj[key], Child(path, key), s);
            return s;
        }

        /// <summary>A .NET type name (namespace-qualified, no whitespace).</summary>
        public string GetTypeName(JsonValue obj, string path, string key, bool required)
        {
            string s = GetString(obj, path, key, required);
            if (s == null) return null;
            CheckTypeName(obj[key], Child(path, key), s);
            return s;
        }

        public bool GetBool(JsonValue obj, string path, string key, bool fallback)
        {
            if (!obj.Has(key)) return fallback;
            JsonValue value = obj[key];
            if (value.Kind != JsonKind.Bool) throw Error(value, Child(path, key), "expected true or false, found " + KindName(value));
            return value.AsBool();
        }

        /// <summary>A boolean member; null when absent.</summary>
        public bool? GetOptionalBool(JsonValue obj, string path, string key)
        {
            if (!obj.Has(key)) return null;
            return GetBool(obj, path, key, false);
        }

        public double GetNumber(JsonValue obj, string path, string key, bool required, double fallback, double min, double max)
        {
            if (!obj.Has(key))
            {
                if (required) throw Error(obj, Child(path, key), "required member \"" + key + "\" is missing");
                return fallback;
            }
            JsonValue value = obj[key];
            if (value.Kind != JsonKind.Number) throw Error(value, Child(path, key), "expected a number, found " + KindName(value));
            double d = value.AsNumber();
            if (d < min || d > max)
            {
                throw Error(value, Child(path, key), "value " + d.ToString("R", CultureInfo.InvariantCulture) + " is outside ["
                            + min.ToString("R", CultureInfo.InvariantCulture) + ", " + max.ToString("R", CultureInfo.InvariantCulture) + "]");
            }
            return d;
        }

        public int GetInt(JsonValue obj, string path, string key, bool required, int fallback, int min, int max)
        {
            double d = GetNumber(obj, path, key, required, fallback, min, max);
            if (Math.Floor(d) != d) throw Error(obj[key], Child(path, key), "expected an integer");
            return (int)d;
        }

        public TEnum GetEnum<TEnum>(JsonValue obj, string path, string key, bool required, TEnum fallback) where TEnum : struct, Enum
        {
            TEnum? value = GetOptionalEnum<TEnum>(obj, path, key, required);
            return value ?? fallback;
        }

        /// <summary>An enum member given by name; null when absent (and not required).</summary>
        public TEnum? GetOptionalEnum<TEnum>(JsonValue obj, string path, string key, bool required) where TEnum : struct, Enum
        {
            string text = GetString(obj, path, key, required);
            if (text == null) return null;
            return ParseEnum<TEnum>(obj[key], Child(path, key), text);
        }

        public TEnum ParseEnum<TEnum>(JsonValue node, string path, string text) where TEnum : struct, Enum
        {
            if (TryParseEnumName(text, out TEnum value)) return value;
            throw Error(node, path, "unknown " + typeof(TEnum).Name + " \"" + text + "\" (expected one of: "
                                    + string.Join(", ", Enum.GetNames(typeof(TEnum))) + ")");
        }

        /// <summary>A flags value given as an array of member names, OR-ed together; <c>default</c> when absent.</summary>
        public TEnum GetFlags<TEnum>(JsonValue obj, string path, string key) where TEnum : struct, Enum
        {
            if (!obj.Has(key)) return default;
            JsonValue array = obj[key];
            string arrayPath = Child(path, key);
            RequireArray(array, arrayPath);
            ulong bits = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < array.Count; i++)
            {
                JsonValue item = array[i];
                string itemPath = Index(arrayPath, i);
                if (item.Kind != JsonKind.String) throw Error(item, itemPath, "expected a " + typeof(TEnum).Name + " name, found " + KindName(item));
                string name = item.AsString();
                if (!seen.Add(name)) throw Error(item, itemPath, "duplicate " + typeof(TEnum).Name + " \"" + name + "\"");
                TEnum flag = ParseEnum<TEnum>(item, itemPath, name);
                bits |= Convert.ToUInt64(flag, CultureInfo.InvariantCulture);
            }
            return (TEnum)Enum.ToObject(typeof(TEnum), bits);
        }

        /// <summary>
        /// An optional array of strings (empty when absent), without duplicates; with <paramref name="typeNames"/> each
        /// element must be a type name, otherwise a name.
        /// </summary>
        public IReadOnlyList<string> GetStringList(JsonValue obj, string path, string key, bool typeNames, bool required = false, bool allowEmpty = true)
        {
            string arrayPath = Child(path, key);
            if (!obj.Has(key))
            {
                if (required) throw Error(obj, arrayPath, "required member \"" + key + "\" is missing");
                return Empty<string>();
            }
            return ReadStringArray(obj[key], arrayPath, typeNames, allowEmpty);
        }

        /// <summary>The strings of an array node (see <see cref="GetStringList"/>).</summary>
        public IReadOnlyList<string> ReadStringArray(JsonValue array, string arrayPath, bool typeNames, bool allowEmpty)
        {
            RequireArray(array, arrayPath);
            if (!allowEmpty && array.Count == 0) throw Error(array, arrayPath, "the list must not be empty");
            var list = new List<string>(array.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < array.Count; i++)
            {
                JsonValue item = array[i];
                string itemPath = Index(arrayPath, i);
                if (item.Kind != JsonKind.String) throw Error(item, itemPath, "expected a string, found " + KindName(item));
                string s = item.AsString();
                if (typeNames) CheckTypeName(item, itemPath, s);
                else CheckName(item, itemPath, s);
                if (!seen.Add(s)) throw Error(item, itemPath, "duplicate entry \"" + s + "\"");
                list.Add(s);
            }
            return list.AsReadOnly();
        }

        /// <summary>The array member <paramref name="key"/>, or null when absent (and not required).</summary>
        public JsonValue GetArray(JsonValue obj, string path, string key, bool required)
        {
            if (!obj.Has(key))
            {
                if (required) throw Error(obj, Child(path, key), "required member \"" + key + "\" is missing");
                return null;
            }
            JsonValue array = obj[key];
            RequireArray(array, Child(path, key));
            return array;
        }

        public void CheckTypeName(JsonValue node, string path, string s)
        {
            CheckText(node, path, s);
            if (s.Length == 0) throw Error(node, path, "a type name must not be empty");
            if (s.Length > MaxNameLength) throw Error(node, path, "type name longer than " + MaxNameLength.ToString(CultureInfo.InvariantCulture) + " characters");
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) throw Error(node, path, "type name \"" + s + "\" contains whitespace");
            }
            if (s[0] == '.' || s[s.Length - 1] == '.' || s.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                throw Error(node, path, "malformed type name \"" + s + "\"");
            }
        }

        public void CheckName(JsonValue node, string path, string s)
        {
            CheckText(node, path, s);
            if (s.Length == 0) throw Error(node, path, "a name must not be empty");
            if (s.Length > MaxNameLength) throw Error(node, path, "name longer than " + MaxNameLength.ToString(CultureInfo.InvariantCulture) + " characters");
            if (char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[s.Length - 1])) throw Error(node, path, "name \"" + s + "\" has leading or trailing whitespace");
        }

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

        private void CheckText(JsonValue node, string path, string s)
        {
            foreach (char c in s)
            {
                if (c < 0x20 || c == 0x7F) throw Error(node, path, "control character in \"" + Printable(s) + "\"");
            }
        }

        public static IReadOnlyList<T> Empty<T>() => new ReadOnlyCollection<T>(Array.Empty<T>());

        internal static bool TryParseEnumName<TEnum>(string text, out TEnum value) where TEnum : struct, Enum
        {
            value = default;
            if (string.IsNullOrEmpty(text)) return false;
            foreach (string name in Enum.GetNames(typeof(TEnum)))
            {
                if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                {
                    value = (TEnum)Enum.Parse(typeof(TEnum), name);
                    return true;
                }
            }
            return false;
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

        private static string Printable(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(c < 0x20 || c == 0x7F ? '?' : c);
            return sb.ToString();
        }
    }
}
