using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>What the joiner's UI learns about a join: why it ended (kind, refusal, mod lists) and download sizes.</summary>
    public sealed class JoinFeedbackTests
    {
        [Test]
        public void ModRefusalCarriesTheCompleteListsForTheUi()
        {
            var rig = new SessionRig();
            var (client, _, _) = rig.AddClient("Anna", tweak: i =>
            {
                i.Mods.Clear();
                i.Mods.Add(new ModEntry { Name = "Traffic", Version = "2.0", PlatformId = "200" });
                i.Mods.Add(new ModEntry { Name = "Move It", Version = "0.9", PlatformId = "100" });
            });
            rig.HostInfo.Mods.Add(new ModEntry { Name = "Anarchy", Version = "3.1", PlatformId = "300" });
            rig.Run(100);
            Assert.Equal(ClientState.Ended, client.State);
            Assert.Equal(ClientEndKind.Refused, client.EndKind);
            Assert.Equal(RefuseReason.Mismatch, client.RefusedFor);
            MismatchReport r = client.EndReport;
            Assert.NotNull(r, "the refusal carries a report");
            Assert.Equal("mods", r.Kind);
            Assert.Equal(1, r.Missing.Count);
            Assert.Equal("Anarchy", r.Missing[0].Name);
            Assert.Equal("300", r.Missing[0].PlatformId);
            Assert.Equal(1, r.Extra.Count);
            Assert.Equal("Traffic", r.Extra[0].Name);
            Assert.Equal(1, r.Versions.Count);
            Assert.Equal("1.0", r.Versions[0].HostVersion);
            Assert.Equal("0.9", r.Versions[0].ClientVersion);
            Assert.Contains("Missing (enable these): Anarchy 3.1", client.EndReason);
        }

        [Test]
        public void GameVersionAndBuildRefusalsSayWhichKind()
        {
            var rig = new SessionRig();
            var (a, _, _) = rig.AddClient("A", tweak: i => i.ModBuild = 43);
            var (b, _, _) = rig.AddClient("B", tweak: i => i.GameVersion = "9.9.9");
            rig.Run(100);
            Assert.Equal("build", a.EndReport.Kind);
            Assert.Equal("game", b.EndReport.Kind);
            Assert.Equal("1.2.3", b.EndReport.HostGameVersion);
            Assert.Equal("9.9.9", b.EndReport.ClientGameVersion);
        }

        [Test]
        public void PasswordProblemsAreDistinguished()
        {
            var rig = new SessionRig(new HostOptions { Password = "hunter2" });
            var (wrong, _, _) = rig.AddClient("Bad", "nope");
            var (none, _, _) = rig.AddClient("None");
            rig.Run(300);
            Assert.Equal(ClientEndKind.Refused, wrong.EndKind);
            Assert.Equal(RefuseReason.BadPassword, wrong.RefusedFor);
            Assert.Null(wrong.EndReport);
            Assert.Equal(ClientEndKind.PasswordRequired, none.EndKind);
        }

        [Test]
        public void LeaveKickAndHostEndHaveTheirKinds()
        {
            var rig = new SessionRig();
            var (a, _, _) = rig.AddClient("Anna");
            var (b, _, _) = rig.AddClient("Ben");
            var (c, _, _) = rig.AddClient("Cleo");
            rig.Run(500);
            a.Leave();
            rig.Host.Kick(b.LocalPeer, "be nice");
            rig.Run(30);
            Assert.Equal(ClientEndKind.Left, a.EndKind);
            Assert.Equal(ClientEndKind.Kicked, b.EndKind);
            rig.Host.Stop("Host closed the city");
            for (int i = 0; i < 10; i++) c.Tick();
            Assert.Equal(ClientEndKind.SessionEnded, c.EndKind);
        }

        [Test]
        public void DownloadSizeIsKnownToTheJoiner()
        {
            var rig = new SessionRig(snapshotBytes: 700_000);
            var (client, _, _) = rig.AddClient("Anna");
            Assert.Equal(0L, client.DownloadTotalBytes);
            rig.Run(400);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.Equal(700_000L, client.DownloadTotalBytes);
        }

        [Test]
        public void ReportSurvivesEncodingWithAwkwardNames()
        {
            var r = new MismatchReport { Kind = "mods" };
            r.Missing.Add(new ModEntry { Name = "Tab\there", Version = "1\n2", PlatformId = "7" });
            r.Versions.Add(new ModVersionDifference { Name = "X", HostVersion = "2", ClientVersion = "1" });
            MismatchReport back = MismatchReport.Decode(MismatchReport.Encode(r));
            Assert.Equal(1, back.Missing.Count);
            Assert.Equal("Tab here", back.Missing[0].Name);
            Assert.Equal("1 2", back.Missing[0].Version);
            Assert.Equal("7", back.Missing[0].PlatformId);
            Assert.Equal("1", back.Versions[0].ClientVersion);
            Assert.Null(MismatchReport.Decode(null));
            Assert.Null(MismatchReport.Decode(new byte[0]));
            Assert.DoesNotThrow(() => MismatchReport.Decode(new byte[] { 0xff, 0xfe, 0x09, 0x0a, 0x00 }));
        }

        [Test]
        public void InviteLinksOnlyCarryASteamId()
        {
            string link = InviteLink.Format(76561198000000001UL);
            Assert.Equal("+skylinetogether_join 76561198000000001", link);
            Assert.True(InviteLink.TryParse(link, out ulong id));
            Assert.Equal(76561198000000001UL, id);
            Assert.True(InviteLink.TryParseArgs(new[] { "Cities2.exe", "-uiDeveloperMode", "+skylinetogether_join", "76561198000000002" }, out id));
            Assert.Equal(76561198000000002UL, id);
            Assert.False(InviteLink.TryParse("+skylinetogether_join 203.0.113.7:27015", out _));
            Assert.False(InviteLink.TryParse("+connect 76561198000000001", out _));
            Assert.False(InviteLink.TryParse(null, out _));
        }
    }
}
