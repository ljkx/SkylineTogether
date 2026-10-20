using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>Placeholders are filled in one pass (R2 misc): a value is never searched for placeholders again.</summary>
    public sealed class TextFormatTests
    {
        [Test]
        public void APlayerNamedLikeAPlaceholderStaysAsTyped()
        {
            string text = NetText.Format("{NAME} was removed: {REASON}", new[] { "NAME", "{REASON}", "REASON", "spam" });
            Assert.Equal("{REASON} was removed: spam", text);
        }

        [Test]
        public void UnknownAndUnclosedPlaceholdersAreKept()
        {
            Assert.Equal("a {X} b 1 {", NetText.Format("a {X} b {N} {", new[] { "N", "1" }));
            Assert.Equal("{}", NetText.Format("{}", new[] { "N", "1" }));
            Assert.Equal("x", NetText.Format("x", null));
            Assert.Equal("1 1", NetText.Format("{N} {N}", new[] { "N", "1" }));
            Assert.Equal("", NetText.Format("{N}", new[] { "N", null }));
        }
    }
}
