using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Json
{
    // The concrete node types behind JsonValue. Internal: callers see only JsonValue and JsonKind.

    internal sealed class JsonNull : JsonValue
    {
        /// <summary>Returned for missing members and out-of-range elements (never part of a parsed tree).</summary>
        internal static readonly JsonNull Missing = new JsonNull();

        public override JsonKind Kind => JsonKind.Null;
    }

    internal sealed class JsonBool : JsonValue
    {
        internal readonly bool Value;

        internal JsonBool(bool value)
        {
            Value = value;
        }

        public override JsonKind Kind => JsonKind.Bool;
    }

    internal sealed class JsonNumber : JsonValue
    {
        internal readonly double Value;

        /// <summary>The number exactly as written (canonical digests and printing use it, never the double).</summary>
        internal readonly string Lexeme;

        internal JsonNumber(double value, string lexeme)
        {
            Value = value;
            Lexeme = lexeme;
        }

        public override JsonKind Kind => JsonKind.Number;

        internal static string FormatLexeme(double value)
        {
            // integers below 2^53 print exactly and identically on every runtime
            if (Math.Abs(value) < 9007199254740992.0 && Math.Floor(value) == value)
            {
                return ((long)value).ToString(CultureInfo.InvariantCulture);
            }
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    internal sealed class JsonString : JsonValue
    {
        internal readonly string Value;

        internal JsonString(string value)
        {
            Value = value;
        }

        public override JsonKind Kind => JsonKind.String;
    }

    internal sealed class JsonArray : JsonValue
    {
        internal readonly List<JsonValue> Items = new List<JsonValue>();

        public override JsonKind Kind => JsonKind.Array;

        public override int Count => Items.Count;

        public override JsonValue this[int index] => index >= 0 && index < Items.Count ? Items[index] : JsonNull.Missing;
    }

    internal sealed class JsonObject : JsonValue
    {
        private readonly List<string> _order = new List<string>();
        private readonly Dictionary<string, JsonValue> _members = new Dictionary<string, JsonValue>(StringComparer.Ordinal);

        public override JsonKind Kind => JsonKind.Object;

        public override int Count => _order.Count;

        public override IEnumerable<string> Keys => _order;

        public override JsonValue this[string key]
        {
            get
            {
                if (key != null && _members.TryGetValue(key, out JsonValue value)) return value;
                return JsonNull.Missing;
            }
        }

        internal bool ContainsKey(string key) => _members.ContainsKey(key);

        internal bool TryAdd(string key, JsonValue value)
        {
            if (_members.ContainsKey(key)) return false;
            _members.Add(key, value);
            _order.Add(key);
            return true;
        }
    }
}
