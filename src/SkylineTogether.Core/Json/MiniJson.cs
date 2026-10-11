using System.Globalization;
using System.Text;

namespace SkylineTogether.Core.Json
{
    /// <summary>
    /// A small strict JSON parser (RFC 8259) for the data files (gates, cosmetic list, policies, adapters, system state,
    /// locale tables). Strict on purpose, because the files are hand-edited and every peer must read them identically:
    /// no comments, no trailing commas, no duplicate member names, no unpaired surrogates, no raw control characters in
    /// strings, numbers only in JSON syntax and within the double range, nesting at most <see cref="MaxDepth"/> levels.
    /// A leading byte-order mark is ignored. Errors throw <see cref="ConfigValidationException"/> with line and column
    /// (1-based; columns count UTF-16 code units). Comments belong into members whose names start with <c>_</c>, which
    /// every data-file parser ignores.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class MiniJson
    {
        /// <summary>Maximum nesting depth of arrays and objects.</summary>
        public const int MaxDepth = 64;

        /// <summary>Largest absolute decimal exponent accepted in a number literal.</summary>
        internal const int MaxExponentDigitsValue = 100000;

        private const char ByteOrderMark = (char)0xFEFF;

        public static JsonValue Parse(string text) => Parse(text, null);

        /// <summary>Parses <paramref name="text"/>; <paramref name="fileName"/> only labels error messages.</summary>
        public static JsonValue Parse(string text, string fileName)
        {
            var parser = new Parser(text ?? string.Empty, fileName);
            return parser.ParseDocument();
        }

        private sealed class Parser
        {
            private readonly string _text;
            private readonly string _file;
            private int _pos;
            private int _line = 1;
            private int _lineStart;

            public Parser(string text, string file)
            {
                _text = text;
                _file = file;
            }

            public JsonValue ParseDocument()
            {
                if (_text.Length > 0 && _text[0] == ByteOrderMark) _pos = 1;
                SkipWhitespace();
                if (_pos >= _text.Length) throw Error("empty JSON document");
                JsonValue root = ParseValue(0);
                SkipWhitespace();
                if (_pos < _text.Length) throw Error("unexpected content after the JSON value");
                return root;
            }

            private JsonValue ParseValue(int depth)
            {
                if (_pos >= _text.Length) throw Error("unexpected end of input, expected a value");
                int line = _line, column = _pos - _lineStart + 1;
                JsonValue value;
                char c = _text[_pos];
                switch (c)
                {
                    case '{':
                        value = ParseObject(depth);
                        break;
                    case '[':
                        value = ParseArray(depth);
                        break;
                    case '"':
                        value = new JsonString(ParseString());
                        break;
                    case 't':
                        ExpectLiteral("true");
                        value = new JsonBool(true);
                        break;
                    case 'f':
                        ExpectLiteral("false");
                        value = new JsonBool(false);
                        break;
                    case 'n':
                        ExpectLiteral("null");
                        value = new JsonNull();
                        break;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                        {
                            value = ParseNumber();
                            break;
                        }
                        throw Error("unexpected character " + Describe(c) + ", expected a value");
                }
                value.Line = line;
                value.Column = column;
                return value;
            }

            private JsonValue ParseObject(int depth)
            {
                if (depth >= MaxDepth) throw Error("nesting deeper than " + MaxDepth.ToString(CultureInfo.InvariantCulture) + " levels");
                var obj = new JsonObject();
                _pos++; // {
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _pos++;
                    return obj;
                }
                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"') throw Error("expected a member name in quotes");
                    int nameLine = _line, nameColumn = _pos - _lineStart + 1;
                    string name = ParseString();
                    SkipWhitespace();
                    if (Peek() != ':') throw Error("expected ':' after the member name");
                    _pos++;
                    SkipWhitespace();
                    JsonValue value = ParseValue(depth + 1);
                    if (!obj.TryAdd(name, value))
                    {
                        throw new ConfigValidationException(_file, null, nameLine, nameColumn, "duplicate member name \"" + name + "\"");
                    }
                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',')
                    {
                        _pos++;
                        continue;
                    }
                    if (c == '}')
                    {
                        _pos++;
                        return obj;
                    }
                    throw Error("expected ',' or '}' in the object");
                }
            }

            private JsonValue ParseArray(int depth)
            {
                if (depth >= MaxDepth) throw Error("nesting deeper than " + MaxDepth.ToString(CultureInfo.InvariantCulture) + " levels");
                var array = new JsonArray();
                _pos++; // [
                SkipWhitespace();
                if (Peek() == ']')
                {
                    _pos++;
                    return array;
                }
                while (true)
                {
                    SkipWhitespace();
                    array.Items.Add(ParseValue(depth + 1));
                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',')
                    {
                        _pos++;
                        continue;
                    }
                    if (c == ']')
                    {
                        _pos++;
                        return array;
                    }
                    throw Error("expected ',' or ']' in the array");
                }
            }

            private string ParseString()
            {
                _pos++; // opening quote
                StringBuilder sb = null;
                int runStart = _pos;
                while (true)
                {
                    if (_pos >= _text.Length) throw Error("unterminated string");
                    char c = _text[_pos];
                    if (c == '"')
                    {
                        string result = sb == null
                            ? _text.Substring(runStart, _pos - runStart)
                            : sb.Append(_text, runStart, _pos - runStart).ToString();
                        _pos++;
                        return result;
                    }
                    if (c < 0x20) throw Error("control character " + Describe(c) + " in a string (escape it)");
                    if (c == '\\')
                    {
                        if (sb == null) sb = new StringBuilder();
                        sb.Append(_text, runStart, _pos - runStart);
                        _pos++;
                        AppendEscape(sb);
                        runStart = _pos;
                        continue;
                    }
                    if (char.IsHighSurrogate(c))
                    {
                        if (_pos + 1 >= _text.Length || !char.IsLowSurrogate(_text[_pos + 1])) throw Error("unpaired surrogate in a string");
                        _pos += 2;
                        continue;
                    }
                    if (char.IsLowSurrogate(c)) throw Error("unpaired surrogate in a string");
                    _pos++;
                }
            }

            private void AppendEscape(StringBuilder sb)
            {
                if (_pos >= _text.Length) throw Error("unterminated escape sequence");
                char e = _text[_pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); return;
                    case '\\': sb.Append('\\'); return;
                    case '/': sb.Append('/'); return;
                    case 'b': sb.Append('\b'); return;
                    case 'f': sb.Append('\f'); return;
                    case 'n': sb.Append('\n'); return;
                    case 'r': sb.Append('\r'); return;
                    case 't': sb.Append('\t'); return;
                    case 'u':
                    {
                        char unit = ReadHex4();
                        if (char.IsHighSurrogate(unit))
                        {
                            // a high surrogate must be followed by an escaped low surrogate
                            if (_pos + 1 < _text.Length && _text[_pos] == '\\' && _text[_pos + 1] == 'u')
                            {
                                _pos += 2;
                                char low = ReadHex4();
                                if (!char.IsLowSurrogate(low)) throw Error("unpaired surrogate escape in a string");
                                sb.Append(unit).Append(low);
                                return;
                            }
                            throw Error("unpaired surrogate escape in a string");
                        }
                        if (char.IsLowSurrogate(unit)) throw Error("unpaired surrogate escape in a string");
                        sb.Append(unit);
                        return;
                    }
                    default:
                        _pos--;
                        throw Error("invalid escape sequence \\" + Describe(e));
                }
            }

            private char ReadHex4()
            {
                if (_pos + 4 > _text.Length) throw Error("incomplete \\u escape");
                int value = 0;
                for (int i = 0; i < 4; i++)
                {
                    char h = _text[_pos];
                    int digit = h >= '0' && h <= '9' ? h - '0'
                              : h >= 'a' && h <= 'f' ? h - 'a' + 10
                              : h >= 'A' && h <= 'F' ? h - 'A' + 10
                              : -1;
                    if (digit < 0) throw Error("invalid hex digit " + Describe(h) + " in a \\u escape");
                    value = (value << 4) | digit;
                    _pos++;
                }
                return (char)value;
            }

            private JsonValue ParseNumber()
            {
                int start = _pos;
                if (Peek() == '-') _pos++;
                if (Peek() == '0')
                {
                    _pos++;
                    if (IsDigit(Peek())) throw Error("leading zeros are not allowed in numbers");
                }
                else if (IsDigit(Peek()))
                {
                    while (IsDigit(Peek())) _pos++;
                }
                else
                {
                    throw Error("expected a digit");
                }
                if (Peek() == '.')
                {
                    _pos++;
                    if (!IsDigit(Peek())) throw Error("expected a digit after the decimal point");
                    while (IsDigit(Peek())) _pos++;
                }
                if (Peek() == 'e' || Peek() == 'E')
                {
                    _pos++;
                    if (Peek() == '+' || Peek() == '-') _pos++;
                    if (!IsDigit(Peek())) throw Error("expected a digit in the exponent");
                    long exponent = 0;
                    while (IsDigit(Peek()))
                    {
                        if (exponent <= MaxExponentDigitsValue) exponent = exponent * 10 + (_text[_pos] - '0');
                        _pos++;
                    }
                    if (exponent > MaxExponentDigitsValue) throw Error("number exponent out of range");
                }
                string lexeme = _text.Substring(start, _pos - start);
                if (!double.TryParse(lexeme, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    || double.IsInfinity(value) || double.IsNaN(value))
                {
                    _pos = start;
                    throw Error("number out of range: " + lexeme);
                }
                return new JsonNumber(value, lexeme);
            }

            private void ExpectLiteral(string literal)
            {
                if (string.CompareOrdinal(_text, _pos, literal, 0, literal.Length) != 0) throw Error("invalid literal, expected " + literal);
                _pos += literal.Length;
            }

            private void SkipWhitespace()
            {
                while (_pos < _text.Length)
                {
                    char c = _text[_pos];
                    if (c == ' ' || c == '\t')
                    {
                        _pos++;
                    }
                    else if (c == '\n')
                    {
                        _pos++;
                        _line++;
                        _lineStart = _pos;
                    }
                    else if (c == '\r')
                    {
                        _pos++;
                        if (_pos < _text.Length && _text[_pos] == '\n') _pos++;
                        _line++;
                        _lineStart = _pos;
                    }
                    else
                    {
                        return;
                    }
                }
            }

            private char Peek() => _pos < _text.Length ? _text[_pos] : '\0';

            private static bool IsDigit(char c) => c >= '0' && c <= '9';

            private static string Describe(char c)
            {
                if (c == '\0') return "end of input";
                if (c < 0x20 || c > 0x7E) return "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture);
                return "'" + c + "'";
            }

            private ConfigValidationException Error(string message)
            {
                return new ConfigValidationException(_file, null, _line, _pos - _lineStart + 1, message);
            }
        }
    }
}
