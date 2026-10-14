using System;
using System.IO;
using SkylineTogether.Core.Json;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Contracts.DataFileSamples;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>
    /// The locale schema survives parse, print (compact and indented) and re-parse: the printed text parses with the
    /// same model parser and prints the same (IMPLEMENTATION-PLAN §7.1 tests: MiniJson parse/print of every schema).
    /// </summary>
    public sealed class DataFileRoundTripTests
    {
        private static void RoundTrip(string sample, Action<JsonValue> modelParser)
        {
            JsonValue original = J(sample);
            modelParser(original);
            foreach (bool indent in new[] { false, true })
            {
                var text = new StringWriter();
                original.WriteTo(new JsonWriter(text, indent));
                JsonValue reparsed = MiniJson.Parse(text.ToString());
                modelParser(reparsed);
                Assert.Equal(original.ToString(), reparsed.ToString());
            }
        }

        [Test]
        public void LocaleSchema() => RoundTrip(Locale, json => LocaleTable.Parse("en-US", json));
    }
}
