using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SkylineTogether.Core.Json
{
    /// <summary>
    /// The canonical text of a JSON value, the input of data-file digests (ARCH §4.2.7): object members sorted by
    /// ordinal name, members whose names start with <c>_</c> (comments) dropped, no whitespace, strings with only the
    /// mandatory escapes (quote, backslash, control characters as lowercase <c>\u00xx</c>), and numbers in one normal
    /// decimal form computed from the literal text (so <c>1</c>, <c>1.0</c> and <c>1e0</c> are equal, and no runtime's
    /// double formatting is involved). Equal canonical text means equal effective content.
    /// </summary>
    internal static class CanonicalJson
    {
        public static string ToCanonicalString(JsonValue value)
        {
            var sb = new StringBuilder(256);
            Append(sb, value, 0);
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, JsonValue value, int depth)
        {
            if (depth > MiniJson.MaxDepth) throw new InvalidOperationException("JSON value nested too deeply");
            switch (value)
            {
                case JsonObject obj:
                {
                    var keys = new List<string>();
                    foreach (string key in obj.Keys)
                    {
                        if (key.Length > 0 && key[0] == '_') continue;
                        keys.Add(key);
                    }
                    keys.Sort(string.CompareOrdinal);
                    sb.Append('{');
                    for (int i = 0; i < keys.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        AppendString(sb, keys[i]);
                        sb.Append(':');
                        Append(sb, obj[keys[i]], depth + 1);
                    }
                    sb.Append('}');
                    break;
                }
                case JsonArray array:
                    sb.Append('[');
                    for (int i = 0; i < array.Items.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        Append(sb, array.Items[i], depth + 1);
                    }
                    sb.Append(']');
                    break;
                case JsonString s:
                    AppendString(sb, s.Value);
                    break;
                case JsonNumber n:
                    sb.Append(NormalizeNumber(n.Lexeme));
                    break;
                case JsonBool b:
                    sb.Append(b.Value ? "true" : "false");
                    break;
                default:
                    sb.Append("null");
                    break;
            }
        }

        private static void AppendString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20) sb.Append("\\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            sb.Append('"');
        }

        /// <summary>
        /// The normal decimal form of a JSON number literal (exact, from the digits): leading and trailing zeros removed,
        /// "-0" becomes "0", integers up to 21 digits plain, fractions with a decimal point when the point lies within
        /// 21 digits or at most 6 places after it, otherwise "d.ddde+x"/"d.ddde-x" (the ECMAScript Number-to-string layout).
        /// </summary>
        internal static string NormalizeNumber(string lexeme)
        {
            int i = 0;
            bool negative = false;
            if (i < lexeme.Length && lexeme[i] == '-')
            {
                negative = true;
                i++;
            }
            var digits = new StringBuilder(lexeme.Length);
            while (i < lexeme.Length && IsDigit(lexeme[i])) digits.Append(lexeme[i++]);
            long exponent = 0;
            if (i < lexeme.Length && lexeme[i] == '.')
            {
                i++;
                while (i < lexeme.Length && IsDigit(lexeme[i]))
                {
                    digits.Append(lexeme[i++]);
                    exponent--;
                }
            }
            if (i < lexeme.Length && (lexeme[i] == 'e' || lexeme[i] == 'E'))
            {
                i++;
                bool exponentNegative = false;
                if (i < lexeme.Length && (lexeme[i] == '+' || lexeme[i] == '-'))
                {
                    exponentNegative = lexeme[i] == '-';
                    i++;
                }
                long e = 0;
                while (i < lexeme.Length && IsDigit(lexeme[i]))
                {
                    if (e < 1000000000L) e = e * 10 + (lexeme[i] - '0');
                    i++;
                }
                exponent += exponentNegative ? -e : e;
            }

            // value = digits × 10^exponent; strip leading zeros, then move trailing zeros into the exponent
            int first = 0;
            while (first < digits.Length && digits[first] == '0') first++;
            if (first == digits.Length) return "0";
            int last = digits.Length - 1;
            while (digits[last] == '0')
            {
                last--;
                exponent++;
            }
            string d = digits.ToString(first, last - first + 1);
            long point = d.Length + exponent;   // position of the decimal point relative to the first digit

            string body;
            if (exponent >= 0 && point <= 21)
            {
                body = d + new string('0', (int)exponent);
            }
            else if (point > 0 && point <= 21)
            {
                body = d.Substring(0, (int)point) + "." + d.Substring((int)point);
            }
            else if (point > -6 && point <= 0)
            {
                body = "0." + new string('0', (int)-point) + d;
            }
            else
            {
                long e = point - 1;
                body = d.Substring(0, 1) + (d.Length > 1 ? "." + d.Substring(1) : string.Empty)
                       + (e >= 0 ? "e+" : "e-") + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
            }
            return negative ? "-" + body : body;
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';
    }
}
