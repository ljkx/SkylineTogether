using System;
using System.Text;
using SkylineTogether.Core.Json;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Json
{
    /// <summary>The strict JSON parser: grammar, escapes, limits and error positions.</summary>
    public sealed class MiniJsonTests
    {
        /// <summary>A JSON \uXXXX escape, built from pieces so no tool rewrites it in the source.</summary>
        private static string U(string hex4) => "\\" + "u" + hex4;

        /// <summary>JSON written with single quotes for readability.</summary>
        private static JsonValue J(string singleQuoted) => MiniJson.Parse(singleQuoted.Replace('\'', '"'));

        private static ConfigValidationException Fails(string text)
        {
            return Assert.Throws<ConfigValidationException>(() => MiniJson.Parse(text, "test.json"), "expected a parse error for " + Assert.Format(text));
        }

        [Test]
        public void ParsesScalars()
        {
            Assert.Equal(JsonKind.Null, MiniJson.Parse("null").Kind);
            Assert.True(MiniJson.Parse("true").AsBool());
            Assert.False(MiniJson.Parse("false").AsBool(true));
            Assert.Equal(0.0, MiniJson.Parse("0").AsNumber(1));
            Assert.Equal(-12.5, MiniJson.Parse("-12.5").AsNumber());
            Assert.Equal(0.0125, MiniJson.Parse("12.5e-3").AsNumber());
            Assert.Equal(1e10, MiniJson.Parse("1E10").AsNumber());
            Assert.Equal(-2500.0, MiniJson.Parse("-2.5e+3").AsNumber());
            Assert.Equal("text", MiniJson.Parse("\"text\"").AsString());
            Assert.Equal(string.Empty, MiniJson.Parse("\"\"").AsString());
            Assert.Equal(JsonKind.Number, MiniJson.Parse(" \t\r\n 7 \n").Kind);
        }

        [Test]
        public void ParsesNestedStructuresInDocumentOrder()
        {
            JsonValue v = J("{ 'b': [1, 2, {'x': null}], 'a': {'c': true}, 'z': 'last' }");
            Assert.Equal(JsonKind.Object, v.Kind);
            Assert.Equal(3, v.Count);
            Assert.SequenceEqual(new[] { "b", "a", "z" }, v.Keys);
            Assert.Equal(3, v["b"].Count);
            Assert.Equal(2.0, v["b"][1].AsNumber());
            Assert.True(v["b"][2].Has("x"));
            Assert.Equal(JsonKind.Null, v["b"][2]["x"].Kind);
            Assert.True(v["a"]["c"].AsBool());
            Assert.Equal("last", v["z"].AsString());
            Assert.Equal(0, J("{}").Count);
            Assert.Equal(0, J("[]").Count);
        }

        [Test]
        public void DecodesEveryEscape()
        {
            string text = "\"q\\\" b\\\\ s\\/ \\b\\f\\n\\r\\t " + U("0041") + U("00e9") + U("6771") + "\"";
            string expected = "q\" b\\ s/ \b\f\n\r\t A" + (char)0xE9 + (char)0x6771;
            Assert.Equal(expected, MiniJson.Parse(text).AsString());
        }

        [Test]
        public void SurrogatePairsMustBeComplete()
        {
            string pair = MiniJson.Parse("\"" + U("d83c") + U("dfd9") + "\"").AsString();
            Assert.Equal(2, pair.Length);
            Assert.True(char.IsHighSurrogate(pair[0]) && char.IsLowSurrogate(pair[1]));
            string raw = "\"" + (char)0xD83C + (char)0xDFD9 + "\"";
            Assert.Equal(2, MiniJson.Parse(raw).AsString().Length);
            Assert.Contains("surrogate", Fails("\"" + U("d800") + "\"").Message);
            Assert.Contains("surrogate", Fails("\"" + U("dc00") + "\"").Message);
            Assert.Contains("surrogate", Fails("\"" + U("d800") + U("0041") + "\"").Message);
            Assert.Contains("surrogate", Fails("\"" + (char)0xD800 + "x\"").Message);
            Assert.Contains("surrogate", Fails("\"" + (char)0xDC00 + "\"").Message);
        }

        [Test]
        public void RejectsMalformedStrings()
        {
            Assert.Contains("unterminated string", Fails("\"abc").Message);
            Assert.Contains("control character", Fails("\"a\tb\"").Message);
            Assert.Contains("invalid escape", Fails("\"\\x\"").Message);
            Assert.Contains("hex digit", Fails("\"" + U("00g0") + "\"").Message);
            Assert.Contains("incomplete", Fails("\"\\u12").Message);
            Assert.Contains("unterminated escape", Fails("\"\\").Message);
        }

        [Test]
        public void RejectsMalformedNumbers()
        {
            foreach (string bad in new[] { "01", "-", "1.", ".5", "+1", "1e", "1e+", "--1", "0x10", "1.2.3", "NaN", "Infinity", "-Infinity", "1e400", "-1e400" })
            {
                Fails(bad);
            }
            Assert.Contains("out of range", Fails("1e400").Message);
            Assert.Contains("exponent out of range", Fails("1e100001").Message);
            Assert.Equal(0.0, MiniJson.Parse("1e-400").AsNumber(1), "underflow to zero is a valid double");
        }

        [Test]
        public void RejectsStructuralErrors()
        {
            Assert.Contains("empty JSON document", Fails("").Message);
            Assert.Contains("empty JSON document", Fails("   \n ").Message);
            Assert.Contains("after the JSON value", Fails("{} x").Message);
            Assert.Contains("after the JSON value", Fails("1 2").Message);
            Fails("[1,2,]");
            Fails("{\"a\":1,}");
            Fails("{\"a\" 1}");
            Fails("{a:1}");
            Fails("[1 2]");
            Fails("{\"a\":1");
            Fails("[");
            Fails("tru");
            Fails("nulls");
            Fails("// comment\n{}");
        }

        [Test]
        public void RejectsDuplicateMemberNames()
        {
            ConfigValidationException e = Fails("{\"a\":1,\"a\":2}");
            Assert.Contains("duplicate member name \"a\"", e.Message);
            Assert.Equal(1, e.Line);
            Assert.Equal(8, e.Column);
        }

        [Test]
        public void ErrorsCarryFileLineAndColumn()
        {
            ConfigValidationException e = Fails("{\n  \"a\": 1,\n  \"b\": tru\n}");
            Assert.Equal("test.json", e.File);
            Assert.Equal(3, e.Line);
            Assert.Equal(8, e.Column);
            Assert.Contains("test.json: line 3, column 8", e.Message);

            ConfigValidationException crlf = Fails("{\r\n\"a\": x}");
            Assert.Equal(2, crlf.Line);
            Assert.Equal(6, crlf.Column);

            ConfigValidationException oldMac = Fails("[1,\r2,\rx]");
            Assert.Equal(3, oldMac.Line);
            Assert.Equal(1, oldMac.Column);

            ConfigValidationException trailing = Fails("[1,2,]");
            Assert.Equal(1, trailing.Line);
            Assert.Equal(6, trailing.Column);

            Assert.Contains("<json>", Assert.Throws<ConfigValidationException>(() => MiniJson.Parse("{")).Message);
        }

        [Test]
        public void NestingDepthIsLimited()
        {
            string ok = new string('[', MiniJson.MaxDepth) + new string(']', MiniJson.MaxDepth);
            Assert.Equal(JsonKind.Array, MiniJson.Parse(ok).Kind);
            string tooDeep = new string('[', MiniJson.MaxDepth + 1) + new string(']', MiniJson.MaxDepth + 1);
            Assert.Contains("nesting deeper", Fails(tooDeep).Message);
            var objects = new StringBuilder();
            for (int i = 0; i <= MiniJson.MaxDepth; i++) objects.Append("{\"a\":");
            objects.Append("1").Append('}', MiniJson.MaxDepth + 1);
            Assert.Contains("nesting deeper", Fails(objects.ToString()).Message);
        }

        [Test]
        public void ByteOrderMarkIsIgnored()
        {
            Assert.Equal(JsonKind.Object, MiniJson.Parse((char)0xFEFF + "{}").Kind);
            Fails("{}" + (char)0xFEFF);
        }

        [Test]
        public void NullTextIsAnEmptyDocument()
        {
            Assert.Contains("empty JSON document", Assert.Throws<ConfigValidationException>(() => MiniJson.Parse(null)).Message);
        }

        [Test]
        public void LargeDocumentsParse()
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < 20000; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(i).Append(",\"name\":\"n").Append(i).Append("\"}");
            }
            sb.Append(']');
            JsonValue v = MiniJson.Parse(sb.ToString());
            Assert.Equal(20000, v.Count);
            Assert.Equal("n19999", v[19999]["name"].AsString());
        }
    }
}
