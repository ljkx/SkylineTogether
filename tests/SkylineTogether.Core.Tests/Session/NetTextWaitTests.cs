using System.Collections.Generic;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// Waits in the host's texts (lockouts, join cooldowns) are worded by each receiver in its own language: seconds
    /// under two minutes, then minutes, then hours; kick and ban reasons read as one sentence.
    /// </summary>
    public sealed class NetTextWaitTests
    {
        private static readonly Dictionary<string, string> German = new Dictionary<string, string>
        {
            [NetText.RefuseCooldown] = "Du kannst in {WAIT} erneut beitreten.",
            [NetText.TimeSeconds] = "{N} s",
            [NetText.TimeMinutes] = "etwa {N} Minuten",
            [NetText.TimeHours] = "etwa {N} Stunden",
        };

        [Test]
        public void AWaitIsWordedInSecondsMinutesOrHours()
        {
            Assert.Equal("40 s", NetText.Wait("40", null));
            Assert.Equal("119 s", NetText.Wait("119", null));
            Assert.Equal("about 2 minutes", NetText.Wait("120", null));
            Assert.Equal("about 10 minutes", NetText.Wait("600", null));
            Assert.Equal("about 30 minutes", NetText.Wait("1800", null));
            Assert.Equal("about 2 hours", NetText.Wait("7200", null));
            Assert.Equal("about 4 hours", NetText.Wait("14400", null));
            Assert.Equal("etwa 10 Minuten", NetText.Wait("600", k => German.TryGetValue(k, out string v) ? v : null));
        }

        [Test]
        public void TheHostSendsSecondsAndEachReceiverWordsTheWait()
        {
            LocalText t = NetText.Of(NetText.RefuseCooldown, "SECONDS", "14400");
            Assert.SequenceEqual(new[] { "SECONDS", "14400" }, t.Args, "only the seconds go over the network");
            Assert.Equal("Your last attempts to join this session didn't finish. You can join again in about 4 hours.", t.Text);
            Assert.Equal("Du kannst in etwa 4 Stunden erneut beitreten.", t.Render(k => German.TryGetValue(k, out string v) ? v : null));
            Assert.Equal("Too many wrong passwords. Try again in 40 s.", NetText.Of(NetText.RefuseLockout, "SECONDS", "40").Text);
        }

        [Test]
        public void ARemovalReasonReadsAsOneSentence()
        {
            Assert.True(NetText.IsRemovalPhrase(NetText.KickByHost));
            Assert.True(NetText.IsRemovalPhrase(NetText.KickByCoHost));
            Assert.True(NetText.IsRemovalPhrase(NetText.BanByHost));
            Assert.False(NetText.IsRemovalPhrase(null), "a plain reason is a sentence of its own");
            Assert.Equal("You were removed by Anna.", NetText.Format(NetText.English[NetText.ClientKicked], new[] { "REASON", NetText.Of(NetText.KickByCoHost, "NAME", "Anna").Text }));
            Assert.Equal("You were removed from the session and banned by the host.", NetText.Format(NetText.English[NetText.ClientBanned], new[] { "REASON", NetText.Of(NetText.BanByHost).Text }));
        }

        [Test]
        public void AFeatureIsNamedInTheReceiversLanguage()
        {
            LocalText t = NetText.Of(NetText.EndHostError, "FEATURE", "city sync", NetText.FeatureIdArg, "replication");
            Assert.Equal("The host's game hit an error (city sync), so the session ended. The host can start it again.", t.Text);
            var german = new Dictionary<string, string>
            {
                [NetText.EndHostError] = "Fehler ({FEATURE}).",
                ["st.feature.replication"] = "Stadtabgleich",
            };
            Assert.Equal("Fehler (Stadtabgleich).", t.Render(k => german.TryGetValue(k, out string v) ? v : null));
            Assert.Equal("Fehler (city sync).", t.Render(k => k == NetText.EndHostError ? "Fehler ({FEATURE})." : null), "no name here: the host's");
            LocalText odd = NetText.Of(NetText.EndHostError, "FEATURE", "x", NetText.FeatureIdArg, "../ui");
            Assert.Equal("Fehler (x).", odd.Render(k => k == NetText.EndHostError ? "Fehler ({FEATURE})." : "INJECTED"), "only plain ids are looked up");
        }

        [Test]
        public void AJoiningGamesOwnReasonCarriesAKey()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty" });
            var (anna, _, _) = rig.AddClient("Anna");
            rig.HostTransport.Dispose(); // nobody answers
            rig.Run(120);
            Assert.Equal(ClientState.Ended, anna.State);
            Assert.NotNull(anna.EndText, anna.EndReason);
            Assert.True(anna.EndText.Key.StartsWith(NetText.ClientKeyPrefix, System.StringComparison.Ordinal), anna.EndText.Key);
            Assert.Equal(anna.EndText.Text, anna.EndReason, "the English reason is the key's English text");
        }

        [Test]
        public void AKickedPlayerIsToldOnceWhy()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty" });
            var (ben, _, _) = rig.AddClient("Ben");
            var (cy, _, _) = rig.AddClient("Cy");
            rig.Run(400);
            rig.Host.Kick(ben.LocalPeer, (string)null);
            rig.Host.Kick(cy.LocalPeer, NetText.Of(NetText.KickInvalidChanges));
            rig.Run(30);
            Assert.Equal(ClientState.Ended, ben.State);
            Assert.Equal("You were removed by the host.", ben.EndReason);
            Assert.Equal("You were removed from the session: Your game sent too many changes the host couldn't accept.", cy.EndReason, "a reason of its own follows a colon");
        }
    }
}
