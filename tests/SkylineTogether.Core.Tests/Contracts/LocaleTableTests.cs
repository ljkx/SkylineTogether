using System;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Contracts.DataFileSamples;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>Locale tables (ARCH §11.7).</summary>
    public sealed class LocaleTableTests
    {
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
