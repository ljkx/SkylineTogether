using System;
using System.Text;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Json;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Contracts.DataFileSamples;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>Data-file digests over canonical JSON (ARCH §4.2.7).</summary>
    public sealed class DataFileDigestTests
    {
        private static ulong Digest(string singleQuoted) => DataFileDigest.Compute(J(singleQuoted));

        [Test]
        public void CanonicalFormSortsMembersAndDropsWhitespaceAndComments()
        {
            JsonValue v = J("{ 'b': [ 1, 2.50, 'x' ], '_comment': 'ignored', 'a': { 'd': true, 'c': null, '_x': 1 } }");
            Assert.Equal("{\"a\":{\"c\":null,\"d\":true},\"b\":[1,2.5,\"x\"]}", DataFileDigest.ToCanonicalJson(v));
        }

        [Test]
        public void DigestIsXxHash64OfTheCanonicalUtf8()
        {
            JsonValue v = J(Gates);
            byte[] utf8 = Encoding.UTF8.GetBytes(DataFileDigest.ToCanonicalJson(v));
            Assert.Equal(XxHash64.Hash(utf8, 0, utf8.Length), DataFileDigest.Compute(v));
        }

        [Test]
        public void DigestIgnoresMemberOrderWhitespaceCommentsAndNumberSpelling()
        {
            ulong baseline = Digest("{ 'version': 1, 'entries': [ { 'type': 'A.B', 'critical': true } ], 'linger': 2 }");
            Assert.Equal(baseline, Digest("{'linger':2,'entries':[{'critical':true,'type':'A.B'}],'version':1}"));
            Assert.Equal(baseline, Digest("{\n\t'version' : 1 ,\r\n 'entries' : [ { 'type' : 'A.B' , 'critical' : true } ] , 'linger' : 2.0 }"));
            Assert.Equal(baseline, Digest("{ '_about': 'comments do not count', 'version': 1e0, 'entries': [ { 'type': 'A.B', 'critical': true, '_why': 'x' } ], 'linger': 20e-1 }"));
            Assert.Equal(baseline, DataFileDigest.Compute(MiniJson.Parse(((char)0xFEFF) + "{\"version\":1,\"entries\":[{\"type\":\"A.B\",\"critical\":true}],\"linger\":2}")));
        }

        [Test]
        public void DigestChangesWithAnyEffectiveValue()
        {
            string original = "{ 'version': 1, 'entries': [ { 'type': 'A.B', 'critical': true }, { 'type': 'C.D' } ], 'linger': 2, 'guard': null }";
            ulong baseline = Digest(original);
            string[] variants =
            {
                "{ 'version': 2, 'entries': [ { 'type': 'A.B', 'critical': true }, { 'type': 'C.D' } ], 'linger': 2, 'guard': null }",
                "{ 'version': 1, 'entries': [ { 'type': 'A.C', 'critical': true }, { 'type': 'C.D' } ], 'linger': 2, 'guard': null }",
                "{ 'version': 1, 'entries': [ { 'type': 'A.B', 'critical': false }, { 'type': 'C.D' } ], 'linger': 2, 'guard': null }",
                "{ 'version': 1, 'entries': [ { 'type': 'C.D' }, { 'type': 'A.B', 'critical': true } ], 'linger': 2, 'guard': null }",
                "{ 'version': 1, 'entries': [ { 'type': 'A.B', 'critical': true }, { 'type': 'C.D' } ], 'linger': 2.5, 'guard': null }",
                "{ 'version': 1, 'entries': [ { 'type': 'A.B', 'critical': true }, { 'type': 'C.D' } ], 'linger': 2, 'guard': 'G' }",
                "{ 'version': 1, 'entries': [ { 'type': 'A.B', 'critical': true }, { 'type': 'C.D' } ], 'linger': 2 }",
                "{ 'version': 1, 'entries': [ { 'type': 'A.B', 'critical': true }, { 'type': 'C.D' } ], 'linger': 2, 'guard': null, 'extra': 0 }",
                "{ 'version': 1, 'entries': [ { 'type': 'A.B', 'critical': true }, { 'type': 'C.D' } ], 'linger': '2', 'guard': null }",
                "{ 'version': 1, 'entries': [ { 'type': 'A.B', 'critical': true }, { 'type': 'C.D' }, {} ], 'linger': 2, 'guard': null }",
                "{ 'version': 1, 'entries': [ { 'type': 'A.B ', 'critical': true }, { 'type': 'C.D' } ], 'linger': 2, 'guard': null }",
                "{ 'version': 1, 'entries': [ { 'Type': 'A.B', 'critical': true }, { 'type': 'C.D' } ], 'linger': 2, 'guard': null }",
                "{ 'version': 1, 'entries': [ { 'type': 'A.B', 'critical': true }, { 'type': 'C.D' } ], 'linger': -2, 'guard': null }",
            };
            foreach (string variant in variants)
            {
                Assert.NotEqual(baseline, Digest(variant), "digest should change: " + variant);
            }
        }

        [Test]
        public void NumberNormalisationIsExact()
        {
            string[,] cases =
            {
                { "0", "0" }, { "-0", "0" }, { "0.000", "0" }, { "0e5", "0" }, { "1", "1" }, { "1.0", "1" }, { "1e0", "1" }, { "10e-1", "1" },
                { "0.1e1", "1" }, { "100", "100" }, { "1e2", "100" }, { "-12.50", "-12.5" }, { "-2.5E+3", "-2500" }, { "123.4500", "123.45" },
                { "0.5", "0.5" }, { "0.000001", "0.000001" }, { "0.0000001", "1e-7" }, { "1.5e-7", "1.5e-7" },
                { "1e20", "100000000000000000000" }, { "1e21", "1e+21" }, { "12e20", "1.2e+21" },
                { "12345678901234567890123", "1.2345678901234567890123e+22" }, { "0.30000000000000004", "0.30000000000000004" },
                { "9007199254740993", "9007199254740993" }, { "1e-400", "1e-400" }, { "-1.25e-10", "-1.25e-10" }
            };
            for (int i = 0; i < cases.GetLength(0); i++)
            {
                Assert.Equal(cases[i, 1], CanonicalJson.NormalizeNumber(cases[i, 0]), "normal form of " + cases[i, 0]);
            }
        }

        [Test]
        public void LargeIntegersKeepEveryDigit()
        {
            // 2^53 + 1 is not representable as a double; the digest must still tell it apart from 2^53
            Assert.NotEqual(Digest("{ 'n': 9007199254740992 }"), Digest("{ 'n': 9007199254740993 }"));
        }

        [Test]
        public void StringsUseOnlyMandatoryEscapes()
        {
            JsonValue v = MiniJson.Parse("{\"s\":\"q\\\" b\\\\ \\/ \\n" + (char)0xE9 + "\"}");
            Assert.Equal("{\"s\":\"q\\\" b\\\\ / \\u000a" + (char)0xE9 + "\"}", DataFileDigest.ToCanonicalJson(v));
        }

        [Test]
        public void CodeCreatedValuesDigestLikeParsedOnes()
        {
            JsonValue built = JsonValue.CreateObject(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, JsonValue>("version", JsonValue.CreateNumber(1L)),
                new System.Collections.Generic.KeyValuePair<string, JsonValue>("ratio", JsonValue.CreateNumber(0.25)),
            });
            Assert.Equal(Digest("{ 'ratio': 0.250, 'version': 1.0 }"), DataFileDigest.Compute(built));
        }

        [Test]
        public void NullInputThrows()
        {
            Assert.Throws<ArgumentNullException>(() => DataFileDigest.Compute(null));
            Assert.Throws<ArgumentNullException>(() => DataFileDigest.ToCanonicalJson(null));
        }
    }
}
