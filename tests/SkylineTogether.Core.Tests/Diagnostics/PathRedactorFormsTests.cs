using System.Collections.Generic;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Diagnostics
{
    /// <summary>
    /// The other spellings of the profile folder that reach logs (<see cref="PathRedactor"/>): its 8.3 short form
    /// (<c>D:\Profiles\ALEXAN~1</c>, common in TEMP and so in the snapshot folders' IO exceptions) and a <c>file://</c> URL
    /// with percent-encoded characters; and what must stay unchanged.
    /// </summary>
    public sealed class PathRedactorFormsTests
    {
        private static PathRedactor Profile(string profile, string userName)
        {
            return new PathRedactor(new[] { new KeyValuePair<string, string>(profile, PathRedactor.UserProfileToken) }, userName);
        }

        [Test]
        public void TheShortFormOfTheProfileFolderIsRedacted()
        {
            PathRedactor r = Profile(@"D:\Profiles\Alexandra Smith", "Alexandra Smith");
            Assert.Equal(@"Could not find file '%USERPROFILE%\AppData\Local\Temp\SkylineTogether\join\city.cok'",
                         r.Apply(@"Could not find file 'D:\Profiles\ALEXAN~1\AppData\Local\Temp\SkylineTogether\join\city.cok'"));
            Assert.Equal("%USERPROFILE%/AppData", r.Apply("d:/profiles/alexan~2/AppData"), "any number, any case, either separator");
            Assert.Equal(@"%USERPROFILE%", r.Apply(@"D:\Profiles\ALEXAN~12"));
            Assert.Equal(@"D:\Profiles\ALEXAN~1x\AppData", r.Apply(@"D:\Profiles\ALEXAN~1x\AppData"), "a longer name is another folder");
            Assert.Equal(@"D:\Profiles\ALEXAN~0\AppData", r.Apply(@"D:\Profiles\ALEXAN~0\AppData"), "Windows numbers from 1");
            Assert.Equal(@"E:\Profiles\ALEXAN~1\AppData", r.Apply(@"E:\Profiles\ALEXAN~1\AppData"), "another parent folder");
            Assert.Equal(@"D:\Profiles\OTHERS~1\AppData", r.Apply(@"D:\Profiles\OTHERS~1\AppData"), "another user's folder");
        }

        [Test]
        public void TheShortNamePatternFollowsWindows()
        {
            Assert.True(PathRedactor.ShortNameOf("Alexandra Smith", out string b, out string e));
            Assert.Equal("ALEXAN", b);
            Assert.Equal("", e);
            Assert.True(PathRedactor.ShortNameOf("anna.smith", out b, out e), "an extension longer than three letters");
            Assert.Equal("ANNA", b);
            Assert.Equal("SMI", e);
            Assert.True(PathRedactor.ShortNameOf("Max Mustermann", out b, out _));
            Assert.Equal("MAXMUS", b, "spaces are left out");
            Assert.True(PathRedactor.ShortNameOf("a+b,c;d=e[f]g", out b, out _));
            Assert.Equal("A_B_C_", b);
            Assert.False(PathRedactor.ShortNameOf("alex", out _, out _), "a valid 8.3 name has no short form");
            Assert.False(PathRedactor.ShortNameOf("ALEXSM~1", out _, out _));
            Assert.False(PathRedactor.ShortNameOf("abc.def", out _, out _));
            Assert.False(PathRedactor.ShortNameOf("J\u00f6rg M\u00fcller", out _, out _), "not predictable outside ASCII (the resolved form covers it)");

            PathRedactor r = Profile(@"D:\Profiles\anna.smith", null);
            Assert.Equal(@"%USERPROFILE%\x", r.Apply(@"D:\Profiles\ANNA~1.SMI\x"));
            Assert.Equal(@"D:\Profiles\ANNA~1\x", r.Apply(@"D:\Profiles\ANNA~1\x"), "without its extension it is another name");
        }

        [Test]
        public void TheResolvedShortFormIsARootToo()
        {
            // a hashed short name (after four folders with the same first letters) is only known by asking Windows
            var r = new PathRedactor(new[]
            {
                new KeyValuePair<string, string>(@"D:\Profiles\Alexandra Smith", PathRedactor.UserProfileToken),
                new KeyValuePair<string, string>(@"D:\Profiles\LE1A2B~1", PathRedactor.UserProfileToken),
            }, "Alexandra Smith");
            Assert.Equal(@"%USERPROFILE%\AppData\Local\Temp\st", r.Apply(@"D:\Profiles\LE1A2B~1\AppData\Local\Temp\st"));
            Assert.Equal(@"%USERPROFILE%\AppData", r.Apply(@"D:\Profiles\Alexandra Smith\AppData"));
        }

        [Test]
        public void AFileUrlWithPercentEncodedCharactersIsRedacted()
        {
            PathRedactor r = Profile(@"D:\Profiles\Alexandra Smith", "Alexandra Smith");
            Assert.Equal("file:///%USERPROFILE%/AppData/LocalLow/x.png", r.Apply("file:///D:/Profiles/Alexandra%20Smith/AppData/LocalLow/x.png"));
            Assert.Equal("file:///%USERPROFILE%/AppData", r.Apply("file:///d:/profiles/alexando%20smith/AppData"), "lower-case hex and letters");
            Assert.Equal("see /srv/%USERNAME%/x", r.Apply("see /srv/Alexandra%20Smith/x"), "the user name as a segment");
            PathRedactor j = Profile("D:\\Profiles\\J\u00f6rg M\u00fcller", "J\u00f6rg M\u00fcller");
            Assert.Equal("file:///%USERPROFILE%/AppData", j.Apply("file:///D:/Profiles/J%C3%B6rg%20M%C3%BCller/AppData"), "UTF-8 bytes, percent-encoded");
            Assert.Equal("file:///D:/Profiles/Alexandra%2Smith/x", r.Apply("file:///D:/Profiles/Alexandra%2Smith/x"), "a broken escape is no match");
        }

        [Test]
        public void TheNameInsideAWordAndARootInsideALongerTokenStay()
        {
            PathRedactor r = Profile(@"D:\Profiles\Al", "Al");
            Assert.Equal(@"Global\x and C:\Games\Alpha", r.Apply(@"Global\x and C:\Games\Alpha"), "the name inside a word");
            Assert.Equal(@"D:\%USERNAME%\x", r.Apply(@"D:\Al\x"), "the name as a whole segment");
            Assert.Equal(@"XD:\Profiles\%USERNAME%\x", r.Apply(@"XD:\Profiles\Al\x"), "the root inside a longer token is no root (only its name segment goes)");
            Assert.Equal(@"%USERPROFILE%\x", r.Apply(@"D:\Profiles\Al\x"));
        }

        [Test]
        public void RootsThatWouldRedactEveryPathAreIgnored()
        {
            var r = new PathRedactor(new[]
            {
                new KeyValuePair<string, string>("/x", PathRedactor.UserProfileToken),
                new KeyValuePair<string, string>(@"C:\", PathRedactor.UserProfileToken),
                new KeyValuePair<string, string>("/home", PathRedactor.UserProfileToken),
                new KeyValuePair<string, string>("Users", PathRedactor.UserProfileToken),
            }, null);
            Assert.True(r.IsEmpty, "no folder below a drive or share");
            Assert.Equal(@"/x/y C:\a\b /srv/b Users\b", r.Apply(@"/x/y C:\a\b /srv/b Users\b"));
            Assert.Equal(@"%USERPROFILE%\b", Profile(@"C:\a", null).Apply(@"C:\a\b"), "a folder below a drive is enough");
        }
    }
}
