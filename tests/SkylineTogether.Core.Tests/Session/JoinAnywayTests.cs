using System.Collections.Generic;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// "Join anyway" with different mods (ARCHITECTURE.md#handshake-and-admission): a host that lets players join with
    /// different mods marks a mod or asset pack refusal overridable, and a hello with
    /// <see cref="HelloJoinFlags.AcceptModMismatch"/> is then admitted with a join notice. The flag changes nothing else:
    /// with the option off it is ignored, and it never passes DLC, build, game or protocol version, password, bans, join
    /// policy or capacity.
    /// </summary>
    public sealed class JoinAnywayTests
    {
        private static void Pump(SessionRig rig, IEnumerable<RawPeer> peers, int frames = 20)
        {
            for (int i = 0; i < frames; i++)
            {
                rig.Clock.Advance(1 / 60.0);
                rig.Host.Tick();
                foreach (var c in rig.Clients) c.Client.Tick();
                foreach (RawPeer p in peers) p.Poll();
            }
        }

        /// <summary>A joiner whose code mods differ from the host's (it lacks Move It and runs Traffic).</summary>
        private static FakeInfo OtherMods()
        {
            var info = new FakeInfo();
            info.Mods.Clear();
            info.Mods.Add(new ModEntry { Name = "Traffic", Version = "2.0", PlatformId = "200" });
            return info;
        }

        private static MismatchReport ReportOf(RawPeer peer)
        {
            RefuseMessage r = peer.Refusal();
            return r == null ? null : MismatchReport.Decode(r.MismatchReport);
        }

        private static MismatchReport NoticeOf(RawPeer peer)
        {
            foreach ((ControlKind kind, byte[] payload) in peer.Controls)
            {
                if (kind == ControlKind.JoinNotice) return MismatchReport.Decode(payload);
            }
            return null;
        }

        private static int AdmittedCount(SessionHost host)
        {
            var list = new List<PeerId>();
            host.GetAdmittedPeers(list);
            return list.Count;
        }

        [Test]
        public void ARefusalForModsIsOverridableAndJoinAnywayIsAdmittedWithANotice()
        {
            var rig = new SessionRig();
            var hostSide = new List<MismatchReport>();
            rig.Host.JoinerDiffers += (peer, name, report) => hostSide.Add(report);
            FakeInfo info = OtherMods();

            // first attempt: refused, with the lists and the overridable mark
            var first = new RawPeer(rig, "Anna", 701);
            first.Hello(info);
            first.Manifest(info);
            Pump(rig, new[] { first });
            RefuseMessage refusal = first.Refusal();
            Assert.NotNull(refusal, "refused");
            Assert.Equal(RefuseReason.Mismatch, refusal.Reason);
            Assert.Contains("join anyway", refusal.Detail);
            MismatchReport report = MismatchReport.Decode(refusal.MismatchReport);
            Assert.Equal("mods", report.Kind);
            Assert.True(report.Overridable, "the host lets players join with different mods");
            Assert.Equal("Move It", report.Missing[0].Name);
            Assert.Equal("Traffic", report.Extra[0].Name);
            Assert.Equal(0, AdmittedCount(rig.Host));

            // the player chose "join anyway": the same hello with the flag
            var second = new RawPeer(rig, "Anna", 701);
            second.Hello(info, joinFlags: HelloJoinFlags.AcceptModMismatch);
            second.Manifest(info);
            Pump(rig, new[] { second });
            Assert.Null(second.Refusal(), "not refused again");
            Assert.True(second.Got(MessageKind.Welcome), "admitted");
            Assert.Equal(1, AdmittedCount(rig.Host));
            MismatchReport notice = NoticeOf(second);
            Assert.NotNull(notice, "told what differs");
            Assert.Equal("notice", notice.Kind);
            Assert.True(notice.JoinedAnyway);
            Assert.False(notice.Overridable);
            Assert.Equal("Move It", notice.Missing[0].Name);
            Assert.Equal("Traffic", notice.Extra[0].Name);

            // the host is told, logs the accepted mismatch and lists it in the diagnostics
            Assert.Equal(1, hostSide.Count);
            Assert.True(hostSide[0].JoinedAnyway);
            Assert.Contains("Anna joins anyway with different mods", string.Join("\n", rig.Log.Lines));
            Assert.Contains("Missing (enable these): Move It 1.0", string.Join("\n", rig.Log.Lines));
            Assert.Contains("joinedAnywayWithDifferentMods=", string.Join("\n", rig.Host.DescribePeers()));
        }

        [Test]
        public void AClientThatJoinsAnywayGoesLive()
        {
            var rig = new SessionRig();
            var (refused, _, _) = rig.AddClient("Anna", tweak: i => i.Mods.Clear());
            rig.Run(100);
            Assert.Equal(ClientEndKind.Refused, refused.EndKind);
            Assert.True(refused.EndReport.Overridable, refused.EndReason);

            var (client, _, _) = rig.AddClient("Anna", tweak: i => i.Mods.Clear(), joinAnyway: true);
            rig.Run(400);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.NotNull(client.JoinNotice);
            Assert.True(client.JoinNotice.JoinedAnyway);
            Assert.Equal("Move It", client.JoinNotice.Missing[0].Name);
        }

        [Test]
        public void JoinAnywayPassesAssetPacksTooAndOneChoiceCoversBoth()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", RequireSameAssets = true });
            var (assetsOnly, _, _) = rig.AddClient("Anna", tweak: i => i.AssetPacks.Clear());
            var (both, _, _) = rig.AddClient("Ben", tweak: i => { i.AssetPacks.Clear(); i.Mods.Clear(); });
            rig.Run(100);
            Assert.Equal("assets", assetsOnly.EndReport?.Kind, assetsOnly.EndReason);
            Assert.True(assetsOnly.EndReport.Overridable);
            Assert.Equal("mods", both.EndReport?.Kind, "code mods are reported first");
            Assert.True(both.EndReport.Overridable);

            var (anna, _, _) = rig.AddClient("Anna", tweak: i => i.AssetPacks.Clear(), joinAnyway: true);
            var (ben, _, _) = rig.AddClient("Ben", tweak: i => { i.AssetPacks.Clear(); i.Mods.Clear(); }, joinAnyway: true);
            rig.Run(600);
            Assert.Equal(ClientState.Live, anna.State, anna.EndReason);
            Assert.True(anna.JoinNotice.JoinedAnyway);
            Assert.Equal("Bus Stops", anna.JoinNotice.AssetsMissing[0].Name);
            Assert.Equal(ClientState.Live, ben.State, ben.EndReason);
            Assert.Equal("Move It", ben.JoinNotice.Missing[0].Name);
            Assert.Equal("Bus Stops", ben.JoinNotice.AssetsMissing[0].Name, "the asset packs it was not shown are in the notice");
        }

        [Test]
        public void WithTheOptionOffTheRefusalIsFinalAndTheFlagIsIgnored()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", AllowModMismatchOverride = false });
            FakeInfo info = OtherMods();
            var plain = new RawPeer(rig, "Anna", 711);
            plain.Hello(info);
            plain.Manifest(info);
            var insisting = new RawPeer(rig, "Ben", 712);
            insisting.Hello(info, joinFlags: HelloJoinFlags.AcceptModMismatch);
            insisting.Manifest(info);
            Pump(rig, new[] { plain, insisting });
            foreach (RawPeer p in new[] { plain, insisting })
            {
                MismatchReport report = ReportOf(p);
                Assert.NotNull(report, p.Name + " refused");
                Assert.Equal("mods", report.Kind);
                Assert.False(report.Overridable, "the host requires the same mods");
                Assert.DoesNotContain("join anyway", p.Refusal().Detail);
                Assert.False(p.Got(MessageKind.Welcome));
            }
            Assert.Equal(0, AdmittedCount(rig.Host));
            Assert.Contains("Ben asked to join anyway with different mods, but this host does not let players join with different mods", string.Join("\n", rig.Log.Lines));

            // read live: turned on, the same request is admitted
            rig.Host.Options.AllowModMismatchOverride = true;
            var again = new RawPeer(rig, "Ben", 712);
            again.Hello(info, joinFlags: HelloJoinFlags.AcceptModMismatch);
            again.Manifest(info);
            Pump(rig, new[] { again });
            Assert.True(again.Got(MessageKind.Welcome));
        }

        [Test]
        public void TheFlagWithoutDifferencesOrWithoutARequirementChangesNothing()
        {
            // a remembered choice on a later join whose mods now match: no notice at all
            var rig = new SessionRig();
            var (same, _, _) = rig.AddClient("Anna", joinAnyway: true);
            // mods the host does not require: told as before, not as "joined anyway"
            var relaxed = new SessionRig(new HostOptions { HostName = "Hosty", RequireSameMods = false });
            var (told, _, _) = relaxed.AddClient("Ben", tweak: i => i.Mods.Clear(), joinAnyway: true);
            rig.Run(400);
            relaxed.Run(400);
            Assert.Equal(ClientState.Live, same.State, same.EndReason);
            Assert.Null(same.JoinNotice);
            Assert.Equal(ClientState.Live, told.State, told.EndReason);
            Assert.False(told.JoinNotice.JoinedAnyway);
            Assert.Equal("Move It", told.JoinNotice.Missing[0].Name);
        }

        [Test]
        public void JoinAnywayNeverPassesMissingDlcBuildOrGameVersion()
        {
            var rig = new SessionRig();
            var dlc = new RawPeer(rig, "Dlc", 721);
            FakeInfo noDlc = OtherMods();
            noDlc.Content.Clear();
            dlc.Hello(noDlc, joinFlags: HelloJoinFlags.AcceptModMismatch);
            dlc.Manifest(noDlc);

            var build = new RawPeer(rig, "Build", 722);
            FakeInfo otherBuild = OtherMods();
            otherBuild.ModBuild = 43;
            build.Hello(otherBuild, joinFlags: HelloJoinFlags.AcceptModMismatch);
            build.Manifest(otherBuild);

            var game = new RawPeer(rig, "Game", 723);
            FakeInfo otherGame = OtherMods();
            otherGame.GameVersion = "9.9.9";
            game.Hello(otherGame, joinFlags: HelloJoinFlags.AcceptModMismatch);
            game.Manifest(otherGame);

            Pump(rig, new[] { dlc, build, game });
            Assert.Equal("content", ReportOf(dlc)?.Kind, "DLC the joiner lacks is checked before its mods");
            Assert.False(ReportOf(dlc).Overridable);
            Assert.Equal("build", ReportOf(build)?.Kind);
            Assert.False(ReportOf(build).Overridable);
            Assert.Equal("game", ReportOf(game)?.Kind);
            Assert.False(ReportOf(game).Overridable);
            Assert.Equal(0, AdmittedCount(rig.Host));
        }

        [Test]
        public void JoinAnywayNeverPassesTheProtocolVersion()
        {
            var rig = new SessionRig();
            var peer = new RawPeer(rig, "Old", 724);
            FakeInfo info = OtherMods();
            peer.Hello(info, protocol: (ushort)(ProtocolVersion.Current + 1), joinFlags: HelloJoinFlags.AcceptModMismatch);
            peer.Manifest(info);
            Pump(rig, new[] { peer });
            Assert.Equal(RefuseReason.ProtocolVersion, peer.Refusal()?.Reason);
            Assert.Equal(0, AdmittedCount(rig.Host));
        }

        [Test]
        public void JoinAnywayNeverPassesThePasswordAndRevealsNothingBeforeIt()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "hunter2" });
            FakeInfo info = OtherMods();
            var peer = new RawPeer(rig, "Anna", 731);
            peer.Hello(info, joinFlags: HelloJoinFlags.AcceptModMismatch);
            peer.Manifest(info);
            Pump(rig, new[] { peer });
            Assert.NotNull(peer.Challenge(), "challenged first");
            Assert.False(peer.Got(MessageKind.Welcome));
            peer.AnswerChallenge("wrong");
            Pump(rig, new[] { peer });
            RefuseMessage r = peer.Refusal();
            Assert.Equal(RefuseReason.BadPassword, r?.Reason);
            Assert.True(MismatchReport.Decode(r.MismatchReport)?.CarriesNoDetails ?? true, "nothing about the host's mods before the password");
            Assert.Equal(0, AdmittedCount(rig.Host));

            // with the right password the flag counts again
            var (client, _, _) = rig.AddClient("Ben", "hunter2", tweak: i => i.Mods.Clear(), joinAnyway: true);
            rig.Run(400);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
        }

        [Test]
        public void JoinAnywayNeverPassesBansOrTheJoinPolicy()
        {
            var rig = new SessionRig();
            rig.Host.Options.Bans.Add(741, null, "Anna", 0);
            FakeInfo info = OtherMods();
            var banned = new RawPeer(rig, "Anna", 741);
            banned.Hello(info, joinFlags: HelloJoinFlags.AcceptModMismatch);
            banned.Manifest(info);
            Pump(rig, new[] { banned });
            Assert.Equal(RefuseReason.Banned, banned.Refusal()?.Reason);

            var friendsOnly = new SessionRig(new HostOptions { HostName = "Hosty", JoinPolicy = JoinPolicy.FriendsAndInvited, IsFriendOrInvited = id => false });
            var stranger = new RawPeer(friendsOnly, "Eve", 742);
            stranger.Hello(info, joinFlags: HelloJoinFlags.AcceptModMismatch);
            stranger.Manifest(info);
            Pump(friendsOnly, new[] { stranger });
            Assert.Equal(RefuseReason.NotInvited, stranger.Refusal()?.Reason);
            Assert.Equal(0, AdmittedCount(rig.Host));
            Assert.Equal(0, AdmittedCount(friendsOnly.Host));
        }

        [Test]
        public void JoinAnywayNeverPassesCapacity()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", MaxPlayers = 2 });
            var first = new RawPeer(rig, "Anna", 751);
            first.Hello(rig.HostInfo);
            first.Manifest(rig.HostInfo);
            Pump(rig, new[] { first });
            Assert.True(first.Got(MessageKind.Welcome));

            FakeInfo info = OtherMods();
            var second = new RawPeer(rig, "Ben", 752);
            second.Hello(info, joinFlags: HelloJoinFlags.AcceptModMismatch);
            second.Manifest(info);
            Pump(rig, new[] { first, second });
            Assert.Equal(RefuseReason.Full, second.Refusal()?.Reason);
            Assert.Equal(1, AdmittedCount(rig.Host));
        }
    }
}
