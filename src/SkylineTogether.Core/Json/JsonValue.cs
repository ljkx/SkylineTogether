using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SkylineTogether.Core.Json
{
    /// <summary>
    /// An immutable JSON value produced by <see cref="MiniJson.Parse(string)"/> (or the <c>Create*</c> factories).
    /// <para>
    /// The indexers never throw: a missing member, an index out of range or a lookup on the wrong kind returns a value
    /// of kind <see cref="JsonKind.Null"/>, so chained lookups are safe and <see cref="AsString"/>, <see cref="AsNumber"/>
    /// and <see cref="AsBool"/> fall back. Use <see cref="Has"/> to tell a missing member from an explicit null.
    /// Object members keep their document order in <see cref="Keys"/>. Thread-safe for reading.
    /// </para>
    /// </summary>
    public abstract class JsonValue
    {
        // 1-based source position of the value's first character; 0 for values created in code
        internal int Line;
        internal int Column;

        // only the node types of this assembly derive from JsonValue
        internal JsonValue()
        {
        }

        public abstract JsonKind Kind { get; }

        /// <summary>The member <paramref name="key"/> of an object; a null-kind value when absent or not an object.</summary>
        public virtual JsonValue this[string key] => JsonNull.Missing;

        /// <summary>The element <paramref name="index"/> of an array; a null-kind value when out of range or not an array.</summary>
        public virtual JsonValue this[int index] => JsonNull.Missing;

        /// <summary>Number of elements (array) or members (object); 0 for other kinds.</summary>
        public virtual int Count => 0;

        /// <summary>Member names of an object in document order; empty for other kinds.</summary>
        public virtual IEnumerable<string> Keys => Array.Empty<string>();

        /// <summary>The string of a string value, otherwise <paramref name="fallback"/>.</summary>
        public string AsString(string fallback = null) => this is JsonString s ? s.Value : fallback;

        /// <summary>The number of a number value, otherwise <paramref name="fallback"/>.</summary>
        public double AsNumber(double fallback = 0) => this is JsonNumber n ? n.Value : fallback;

        /// <summary>The boolean of a bool value, otherwise <paramref name="fallback"/>.</summary>
        public bool AsBool(bool fallback = false) => this is JsonBool b ? b.Value : fallback;

        /// <summary>True when this is an object with a member named <paramref name="key"/> (even if its value is null).</summary>
        public bool Has(string key) => this is JsonObject o && key != null && o.ContainsKey(key);

        /// <summary>Writes this value with <paramref name="writer"/> (members in document order).</summary>
        public void WriteTo(JsonWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            WriteValue(writer, this, 0);
        }

        /// <summary>Compact JSON text of this value.</summary>
        public override string ToString()
        {
            var text = new StringWriter(CultureInfo.InvariantCulture);
            WriteTo(new JsonWriter(text, false));
            return text.ToString();
        }

        public static JsonValue CreateNull() => new JsonNull();

        public static JsonValue CreateBool(bool value) => new JsonBool(value);

        public static JsonValue CreateString(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return new JsonString(value);
        }

        /// <summary>A number; NaN and infinities cannot be represented in JSON and throw.</summary>
        public static JsonValue CreateNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("JSON numbers are finite", nameof(value));
            return new JsonNumber(value, JsonNumber.FormatLexeme(value));
        }

        public static JsonValue CreateNumber(long value) => new JsonNumber(value, value.ToString(CultureInfo.InvariantCulture));

        public static JsonValue CreateArray(IEnumerable<JsonValue> items)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            var array = new JsonArray();
            foreach (JsonValue item in items)
            {
                array.Items.Add(item ?? new JsonNull());
            }
            return array;
        }

        /// <summary>An object with members in the given order; duplicate names throw.</summary>
        public static JsonValue CreateObject(IEnumerable<KeyValuePair<string, JsonValue>> members)
        {
            if (members == null) throw new ArgumentNullException(nameof(members));
            var obj = new JsonObject();
            foreach (KeyValuePair<string, JsonValue> member in members)
            {
                if (member.Key == null) throw new ArgumentException("member names cannot be null", nameof(members));
                if (!obj.TryAdd(member.Key, member.Value ?? new JsonNull()))
                {
                    throw new ArgumentException("duplicate member name \"" + member.Key + "\"", nameof(members));
                }
            }
            return obj;
        }

        private static void WriteValue(JsonWriter writer, JsonValue value, int depth)
        {
            if (depth > MiniJson.MaxDepth) throw new InvalidOperationException("JSON value nested too deeply");
            switch (value)
            {
                case JsonObject obj:
                    writer.BeginObject();
                    foreach (string key in obj.Keys)
                    {
                        writer.Name(key);
                        WriteValue(writer, obj[key], depth + 1);
                    }
                    writer.EndObject();
                    break;
                case JsonArray array:
                    writer.BeginArray();
                    for (int i = 0; i < array.Items.Count; i++)
                    {
                        WriteValue(writer, array.Items[i], depth + 1);
                    }
                    writer.EndArray();
                    break;
                case JsonString s:
                    writer.Value(s.Value);
                    break;
                case JsonNumber n:
                    writer.RawNumber(n.Lexeme);
                    break;
                case JsonBool b:
                    writer.Value(b.Value);
                    break;
                default:
                    writer.Null();
                    break;
            }
        }
    }
}
