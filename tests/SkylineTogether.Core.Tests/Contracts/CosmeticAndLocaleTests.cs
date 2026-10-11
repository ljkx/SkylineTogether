using System;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Contracts.DataFileSamples;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>cosmetic.json (ARCH §6.1) and locale tables (ARCH §11.7).</summary>
    public sealed class CosmeticAndLocaleTests
    {
        [Test]
        public void ParsesTheCosmeticSample()
        {
            CosmeticList list = CosmeticList.Parse(J(Cosmetic));
            Assert.Equal(9, list.SystemTypeNames.Count);
            Assert.Equal("Game.Simulation.TimeSystem", list.SystemTypeNames[0]);
            Assert.Equal("Game.Simulation.DirtynessSystem", list.SystemTypeNames[8]);
        }

        [Test]
        public void CosmeticListAcceptsABareArray()
        {
            CosmeticList list = CosmeticList.Parse(J("[ 'Game.Simulation.TimeSystem', 'Game.Simulation.ClimateSystem' ]"));
            Assert.SequenceEqual(new[] { "Game.Simulation.TimeSystem", "Game.Simulation.ClimateSystem" }, list.SystemTypeNames);
            Assert.Equal(0, CosmeticList.Parse(J("[]")).SystemTypeNames.Count);
        }

        [Test]
        public void CosmeticListRejectsInvalidContent()
        {
            ConfigValidationException street = Assert.Throws<ConfigValidationException>(() =>
                CosmeticList.Parse(J("{ 'systems': [ 'Game.Simulation.TimeSystem', 'Game.Simulation.StreetLightSystem' ] }")));
            Assert.Contains("never be cosmetic", street.Message);
            Assert.Equal("$.systems[1]", street.Path);
            Assert.Equal("$[0]", Assert.Throws<ConfigValidationException>(() => CosmeticList.Parse(J("[ 'Game.Simulation.StreetLightSystem' ]"))).Path);
            Assert.Contains("duplicate entry", Assert.Throws<ConfigValidationException>(() => CosmeticList.Parse(J("[ 'A.B', 'A.B' ]"))).Message);
            Assert.Contains("required member \"systems\"", Assert.Throws<ConfigValidationException>(() => CosmeticList.Parse(J("{ 'version': 1 }"))).Message);
            Assert.Contains("expected a string", Assert.Throws<ConfigValidationException>(() => CosmeticList.Parse(J("[ 1 ]"))).Message);
            Assert.Contains("unknown member", Assert.Throws<ConfigValidationException>(() => CosmeticList.Parse(J("{ 'systems': [], 'extra': 1 }"))).Message);
            Assert.Contains("expected an object", Assert.Throws<ConfigValidationException>(() => CosmeticList.Parse(J("'text'"))).Message);
        }

        [Test]
        public void ParsesTheLocaleSample()
        {
            LocaleTable table = LocaleTable.Parse("en-US", J(Locale));
            Assert.Equal("en-US", table.LocaleId);
            Assert.Equal(4, table.Entries.Count, "the comment member is skipped");
            Assert.Equal("Co-op", table.Entries["st.menu.coop"]);
            Assert.Equal("Conflicts with the edit of {0}", table.Entries["st.toast.rejected.conflict"]);
            Assert.Equal(string.Empty, table.Entries["st.empty"]);
            Assert.False(table.Entries.ContainsKey("_comment"));
            Assert.Equal("locale.en-US.json", LocaleTable.FileNameOf("en-US"));
        }

        [Test]
        public void MissingKeysListsAbsentKeysOnce()
        {
            LocaleTable table = LocaleTable.Parse("en-US", J(Locale));
            Assert.SequenceEqual(new[] { "st.nope", "st.other" },
                                 table.MissingKeys(new[] { "st.menu.coop", "st.nope", null, "st.other", "st.nope", "st.hud.sync.synced" }));
            Assert.Equal(0, table.MissingKeys(new string[0]).Count);
            Assert.Throws<ArgumentNullException>(() => table.MissingKeys(null));
        }

        [Test]
        public void LocaleTablesRejectInvalidContent()
        {
            ConfigValidationException Bad(string localeId, string singleQuoted)
            {
                return Assert.Throws<ConfigValidationException>(() => LocaleTable.Parse(localeId, J(singleQuoted)));
            }

            Assert.Contains("expected the text as a string", Bad("en-US", "{ 'a': { 'nested': 'x' } }").Message);
            Assert.Contains("expected the text as a string", Bad("en-US", "{ 'a': 1 }").Message);
            Assert.Contains("must not be empty", Bad("en-US", "{ '': 'x' }").Message);
            Assert.Contains("whitespace", Bad("en-US", "{ ' st.menu': 'x' }").Message);
            Assert.Contains("expected an object", Bad("en-US", "[ 'x' ]").Message);
            Assert.Contains("identifier", Bad("en US", "{}").Message);
            Assert.Contains("must not be empty", Bad(null, "{}").Message);
            Assert.Equal("locale.de-DE.json", Bad("de-DE", "{ 'a': true }").File);
        }
    }
}
