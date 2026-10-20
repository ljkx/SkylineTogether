using System.Collections.Generic;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Diagnostics
{
    /// <summary>What the mod writes to its log and diagnostics keeps the Windows user name and full addresses out.</summary>
    public sealed class LogPrivacyTests
    {
        private static PathRedactor Redactor() => new PathRedactor(new[]
        {
            new KeyValuePair<string, string>(@"C:\Users\User", PathRedactor.UserProfileToken),
        }, "User");

        [Test]
        public void APathInTheProfileIsWrittenWithTheVariable()
        {
            PathRedactor r = Redactor();
            Assert.Equal(@"diagnostics written to %USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\x.txt",
                r.Apply(@"diagnostics written to C:\Users\User\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\x.txt"));
        }

        [Test]
        public void UnitySlashesAndOtherLetterCaseCountToo()
        {
            PathRedactor r = Redactor();
            Assert.Equal("test recording started in %USERPROFILE%/AppData/LocalLow/Colossal Order/Cities Skylines II/ModsData",
                r.Apply("test recording started in c:/users/user/AppData/LocalLow/Colossal Order/Cities Skylines II/ModsData"));
            Assert.Equal("\"%USERPROFILE%\"", r.Apply("\"C:\\Users\\User\""), "the profile folder itself, quoted");
        }

        [Test]
        public void EveryPathInALineIsReplaced()
        {
            PathRedactor r = Redactor();
            Assert.Equal(@"could not delete %USERPROFILE%\a: Access to the path '%USERPROFILE%\a\b' is denied.",
                r.Apply(@"could not delete C:\Users\User\a: Access to the path 'C:\Users\User\a\b' is denied."));
        }

        [Test]
        public void TheUserNameAsAFolderElsewhereIsReplaced()
        {
            PathRedactor r = Redactor();
            Assert.Equal(@"loaded from D:\Games\%USERNAME%\Mods\SkylineTogether", r.Apply(@"loaded from D:\Games\User\Mods\SkylineTogether"));
        }

        [Test]
        public void ALongerFolderNameOrAWordIsNotAMatch()
        {
            PathRedactor r = Redactor();
            Assert.Equal(@"C:\Users\Username\x", r.Apply(@"C:\Users\Username\x"), "another user's folder that starts with the name");
            Assert.Equal(@"D:\Userland\x", r.Apply(@"D:\Userland\x"));
            Assert.Equal("User joined the session", r.Apply("User joined the session"), "a player name is not a path segment");
        }

        [Test]
        public void TextWithoutTheNameIsReturnedAsItIs()
        {
            PathRedactor r = Redactor();
            string line = "[Session] connected: peer 2 (steam connection 7)";
            Assert.Same(line, r.Apply(line));
            Assert.Equal(null, r.Apply(null));
            Assert.Equal("", r.Apply(""));
        }

        [Test]
        public void TheLongestRootWins()
        {
            // AppData redirected to a share outside the profile
            var r = new PathRedactor(new[]
            {
                new KeyValuePair<string, string>(@"C:\Users\User", PathRedactor.UserProfileToken),
                new KeyValuePair<string, string>(@"\\server\profiles\User\AppData\Roaming", "%APPDATA%"),
            }, "User");
            Assert.Equal(@"%APPDATA%\x and %USERPROFILE%\y", r.Apply(@"\\server\profiles\User\AppData\Roaming\x and C:\Users\User\y"));
        }

        [Test]
        public void AProfileFolderNamedDifferentlyFromTheUserIsStillCovered()
        {
            // the profile folder "you" of the Windows user "Me" (like folder names name_000 or name.DOMAIN)
            var r = new PathRedactor(new[] { new KeyValuePair<string, string>(@"C:\Users\you", PathRedactor.UserProfileToken) }, "Me");
            Assert.Equal(@"%USERPROFILE%\x", r.Apply(@"C:\Users\you\x"));
            Assert.Equal(@"E:\%USERNAME%\x", r.Apply(@"E:\you\x"), "the profile folder's name is redacted like the user name");
            Assert.Equal(@"E:\%USERNAME%\x", r.Apply(@"E:\Me\x"), "and the user name itself");
        }

        [Test]
        public void ADriveRootOrAOneLetterNameIsNeverARoot()
        {
            var r = new PathRedactor(new[] { new KeyValuePair<string, string>(@"C:\", PathRedactor.UserProfileToken) }, "A");
            Assert.True(r.IsEmpty, "nothing that would redact every path");
            Assert.Equal(@"C:\Program Files\A\x", r.Apply(@"C:\Program Files\A\x"));
        }

        [Test]
        public void TheRedactorForThisMachineIsUsable()
        {
            // whatever this machine's profile is, a path inside it does not keep the profile folder
            string profile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile)) throw new SkipException("no profile folder on this machine");
            string redacted = PathRedactor.Redact(System.IO.Path.Combine(profile, "file.txt"));
            Assert.DoesNotContain(profile, redacted);
            Assert.Contains(PathRedactor.UserProfileToken, redacted);
        }

        [Test]
        public void AnIpv4AddressKeepsItsFirstThreeParts()
        {
            Assert.Equal("203.0.113.x", AddressMask.Host("203.0.113.7"));
            Assert.Equal("203.0.113.x:27015", AddressMask.EndpointText(new Endpoint { Host = "203.0.113.7", Port = 27015 }));
        }

        [Test]
        public void AnIpv6AddressKeepsItsFirstTwoGroups()
        {
            Assert.Equal("2001:db8:x:x:x:x:x:x", AddressMask.Host("2001:db8::1"));
            Assert.Equal("[2001:db8:x:x:x:x:x:x]", AddressMask.Host("[2001:db8::1]"));
            Assert.Equal("::ffff:198.51.100.x", AddressMask.Host("::ffff:198.51.100.20"));
        }

        [Test]
        public void AHostNameKeepsItsFirstLetterAndDomain()
        {
            Assert.Equal("m***.ddns.example", AddressMask.Host("myhost.ddns.example"));
            Assert.Equal("m***", AddressMask.Host("myhost"));
            Assert.Equal("", AddressMask.Host(null));
        }

        [Test]
        public void ASteamEndpointIsUnchanged()
        {
            var steam = new Endpoint { SteamId = 76561190000000000, VirtualPort = 0 };
            Assert.Equal(steam.ToString(), AddressMask.EndpointText(steam));
            Assert.Equal("(none)", AddressMask.EndpointText(null));
        }

        [Test]
        public void AddressesInAFreeTextAreMaskedButVersionsAreNot()
        {
            Assert.Equal("#3 UDP 203.0.113.x:27015", AddressMask.InText("#3 UDP 203.0.113.7:27015"));
            Assert.Equal("#3 UDP [2001:db8:x:x:x:x:x:x]:27015", AddressMask.InText("#3 UDP [2001:db8::5]:27015"));
            Assert.Equal("game 1.6.2.1.3 and 1.6.2", AddressMask.InText("game 1.6.2.1.3 and 1.6.2"), "five parts and three parts are versions");
            Assert.Equal("999.1.1.1", AddressMask.InText("999.1.1.1"), "not an address");
            Assert.Equal("#9 P2P steamid:76561190000000000", AddressMask.InText("#9 P2P steamid:76561190000000000"));
        }
    }
}
