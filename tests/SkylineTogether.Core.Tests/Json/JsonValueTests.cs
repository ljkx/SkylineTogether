using System;
using System.Collections.Generic;
using SkylineTogether.Core.Json;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Json
{
    /// <summary>JsonValue accessors, safe navigation, factories and printing.</summary>
    public sealed class JsonValueTests
    {
        private static JsonValue J(string singleQuoted) => MiniJson.Parse(singleQuoted.Replace('\'', '"'));

        [Test]
        public void MissingMembersAndElementsAreNullKindAndNeverThrow()
        {
            JsonValue v = J("{ 'a': { 'b': [10, 20] }, 'n': null }");
            Assert.Equal(JsonKind.Null, v["missing"].Kind);
            Assert.Equal(JsonKind.Null, v["missing"]["deeper"][3].Kind);
            Assert.Equal(JsonKind.Null, v["a"]["b"][2].Kind);
            Assert.Equal(JsonKind.Null, v["a"]["b"][-1].Kind);
            Assert.Equal(JsonKind.Null, v["a"][0].Kind, "an index into an object");
            Assert.Equal(JsonKind.Null, v["a"]["b"]["key"].Kind, "a key into an array");
            Assert.Equal(JsonKind.Null, v[(string)null].Kind);
            Assert.Equal("fallback", v["missing"].AsString("fallback"));
            Assert.Equal(5.0, v["missing"].AsNumber(5));
            Assert.True(v["missing"].AsBool(true));
            Assert.Equal(0, v["missing"].Count);
            Assert.SequenceEqual(new string[0], v["missing"].Keys);
        }

        [Test]
        public void HasDistinguishesExplicitNullFromMissing()
        {
            JsonValue v = J("{ 'n': null }");
            Assert.True(v.Has("n"));
            Assert.False(v.Has("x"));
            Assert.False(v.Has(null));
            Assert.Equal(JsonKind.Null, v["n"].Kind);
            Assert.False(J("[1]").Has("0"));
        }

        [Test]
        public void TypedAccessorsReturnFallbacksForOtherKinds()
        {
            JsonValue v = J("{ 's': 'text', 'n': 2.5, 'b': true }");
            Assert.Equal("text", v["s"].AsString());
            Assert.Null(v["n"].AsString());
            Assert.Equal("x", v["b"].AsString("x"));
            Assert.Equal(2.5, v["n"].AsNumber());
            Assert.Equal(-1.0, v["s"].AsNumber(-1));
            Assert.True(v["b"].AsBool());
            Assert.False(v["s"].AsBool());
            Assert.Equal(3, v.Count);
            Assert.Equal(0, v["s"].Count);
        }

        [Test]
        public void ToStringPrintsCompactJsonKeepingNumberLiterals()
        {
            JsonValue v = J("{ 'b': [1, 2.50, -0, 1e3], 'a': { 's': 'q\\'uote' }, 't': true, 'z': null }".Replace("\\'", "\\\""));
            Assert.Equal("{\"b\":[1,2.50,-0,1e3],\"a\":{\"s\":\"q\\\"uote\"},\"t\":true,\"z\":null}", v.ToString());
        }

        [Test]
        public void PrintedJsonParsesBackToTheSameContent()
        {
            JsonValue v = J("{ 'list': [ { 'x': 1 }, [], {}, 'str', false ], 'nested': { 'deep': { 'deeper': [ 0.125 ] } } }");
            JsonValue back = MiniJson.Parse(v.ToString());
            Assert.Equal(v.ToString(), back.ToString());
            Assert.Equal(DataFileDigest.Compute(v), DataFileDigest.Compute(back));
        }

        [Test]
        public void FactoriesBuildValues()
        {
            JsonValue obj = JsonValue.CreateObject(new[]
            {
                new KeyValuePair<string, JsonValue>("name", JsonValue.CreateString("Anarchy")),
                new KeyValuePair<string, JsonValue>("count", JsonValue.CreateNumber(3L)),
                new KeyValuePair<string, JsonValue>("ratio", JsonValue.CreateNumber(0.5)),
                new KeyValuePair<string, JsonValue>("big", JsonValue.CreateNumber(1e300)),
                new KeyValuePair<string, JsonValue>("whole", JsonValue.CreateNumber(42.0)),
                new KeyValuePair<string, JsonValue>("on", JsonValue.CreateBool(true)),
                new KeyValuePair<string, JsonValue>("none", JsonValue.CreateNull()),
                new KeyValuePair<string, JsonValue>("items", JsonValue.CreateArray(new[] { JsonValue.CreateNumber(1L), null })),
            });
            Assert.Equal("{\"name\":\"Anarchy\",\"count\":3,\"ratio\":0.5,\"big\":1E+300,\"whole\":42,\"on\":true,\"none\":null,\"items\":[1,null]}", obj.ToString());
            Assert.Equal(3.0, obj["count"].AsNumber());
            Assert.Throws<ArgumentException>(() => JsonValue.CreateNumber(double.NaN));
            Assert.Throws<ArgumentException>(() => JsonValue.CreateNumber(double.PositiveInfinity));
            Assert.Throws<ArgumentNullException>(() => JsonValue.CreateString(null));
            Assert.Throws<ArgumentException>(() => JsonValue.CreateObject(new[]
            {
                new KeyValuePair<string, JsonValue>("a", JsonValue.CreateNull()),
                new KeyValuePair<string, JsonValue>("a", JsonValue.CreateNull())
            }));
            Assert.Throws<ArgumentException>(() => JsonValue.CreateObject(new[] { new KeyValuePair<string, JsonValue>(null, JsonValue.CreateNull()) }));
        }

        [Test]
        public void WriteToUsesTheGivenWriter()
        {
            var text = new System.IO.StringWriter();
            J("{ 'a': [1, 2] }").WriteTo(new JsonWriter(text, true));
            Assert.Equal("{\n  \"a\": [\n    1,\n    2\n  ]\n}", text.ToString());
            Assert.Throws<ArgumentNullException>(() => J("1").WriteTo(null));
        }
    }
}
