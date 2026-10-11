using System;
using System.IO;
using SkylineTogether.Core.Json;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Json
{
    /// <summary>JsonWriter output, escaping and structural validation.</summary>
    public sealed class JsonWriterTests
    {
        private static string Write(bool indent, Action<JsonWriter> body)
        {
            var text = new StringWriter();
            var writer = new JsonWriter(text, indent);
            body(writer);
            Assert.True(writer.IsComplete, "the document is complete");
            return text.ToString();
        }

        private static void Sample(JsonWriter w)
        {
            w.BeginObject();
            w.Name("session");
            w.Value("Anna's city");
            w.Name("players");
            w.Value(3L);
            w.Name("tags");
            w.BeginArray();
            w.Value(true);
            w.Null();
            w.Value(0.25);
            w.EndArray();
            w.Name("empty");
            w.BeginObject();
            w.EndObject();
            w.Name("none");
            w.BeginArray();
            w.EndArray();
            w.EndObject();
        }

        [Test]
        public void CompactOutput()
        {
            Assert.Equal("{\"session\":\"Anna's city\",\"players\":3,\"tags\":[true,null,0.25],\"empty\":{},\"none\":[]}", Write(false, Sample));
        }

        [Test]
        public void IndentedOutput()
        {
            string expected = "{\n  \"session\": \"Anna's city\",\n  \"players\": 3,\n  \"tags\": [\n    true,\n    null,\n    0.25\n  ],\n  \"empty\": {},\n  \"none\": []\n}";
            Assert.Equal(expected, Write(true, Sample));
        }

        [Test]
        public void StringsAreEscaped()
        {
            string input = "q\" b\\ \n\r\t\b\f" + (char)1 + (char)0x1F + " " + (char)0x2028 + (char)0x2029 + " " + (char)0xD800 + "x" + (char)0xDC00
                           + " " + (char)0xD83C + (char)0xDFD9 + " " + (char)0xE9;
            string output = Write(false, w => w.Value(input));
            string expected = "\"q\\\" b\\\\ \\n\\r\\t\\b\\f\\u0001\\u001f \\u2028\\u2029 \\ud800x\\udc00 " + (char)0xD83C + (char)0xDFD9 + " " + (char)0xE9 + "\"";
            Assert.Equal(expected, output);
            Assert.Equal(input.Replace(((char)0xD800).ToString(), "?").Replace(((char)0xDC00).ToString(), "?"),
                         MiniJson.Parse(output.Replace("\\ud800", "?").Replace("\\udc00", "?")).AsString(), "the escapes parse back");
        }

        [Test]
        public void NumbersAndSpecialDoubles()
        {
            Assert.Equal("[3,-7,0.5,1E-05,\"NaN\",\"Infinity\",\"-Infinity\",9223372036854775807]", Write(false, w =>
            {
                w.BeginArray();
                w.Value(3.0);
                w.Value(-7.0);
                w.Value(0.5);
                w.Value(0.00001);
                w.Value(double.NaN);
                w.Value(double.PositiveInfinity);
                w.Value(double.NegativeInfinity);
                w.Value(long.MaxValue);
                w.EndArray();
            }));
            Assert.Equal("null", Write(false, w => w.Value((string)null)));
        }

        [Test]
        public void MisuseThrows()
        {
            JsonWriter Fresh() => new JsonWriter(new StringWriter(), false);
            Assert.Throws<InvalidOperationException>(() => Fresh().Name("x"));
            Assert.Throws<InvalidOperationException>(() => { var w = Fresh(); w.BeginArray(); w.Name("x"); });
            Assert.Throws<InvalidOperationException>(() => { var w = Fresh(); w.BeginObject(); w.Value(1L); });
            Assert.Throws<InvalidOperationException>(() => { var w = Fresh(); w.BeginObject(); w.Name("a"); w.Name("b"); });
            Assert.Throws<InvalidOperationException>(() => { var w = Fresh(); w.BeginObject(); w.Name("a"); w.EndObject(); });
            Assert.Throws<InvalidOperationException>(() => { var w = Fresh(); w.BeginObject(); w.EndArray(); });
            Assert.Throws<InvalidOperationException>(() => Fresh().EndObject());
            Assert.Throws<InvalidOperationException>(() => { var w = Fresh(); w.Value(1L); w.Value(2L); });
            Assert.Throws<ArgumentNullException>(() => { var w = Fresh(); w.BeginObject(); w.Name(null); });
            Assert.Throws<ArgumentNullException>(() => new JsonWriter(null, false));
            var incomplete = Fresh();
            incomplete.BeginArray();
            Assert.False(incomplete.IsComplete);
        }
    }
}
