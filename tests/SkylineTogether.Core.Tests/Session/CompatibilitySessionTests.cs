using System.Collections.Generic;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// The compatibility check in the session (ARCHITECTURE.md#handshake-and-admission): after the password, DLC the
    /// joiner lacks refuses with a report, asset packs and mod settings are told in a join notice, and "require the same
    /// assets" turns asset differences into a refusal.
    /// </summary>
    public sealed class CompatibilitySessionTests
    {
        [Test]
        public void AJoinerWithoutTheHostsDlcIsRefusedWithTheList()
        {
            var rig = new SessionRig();
            var (client, _, _) = rig.AddClient("Anna", tweak: i => i.Content.Clear());
            rig.Run(100);
            Assert.Equal(ClientState.Ended, client.State);
            Assert.Equal(ClientEndKind.Refused, client.EndKind);
            Assert.Equal(RefuseReason.Mismatch, client.RefusedFor);
            Assert.NotNull(client.EndReport);
            Assert.Equal("content", client.EndReport.Kind);
            Assert.Equal("San Francisco Set", client.EndReport.ContentMissing[0].Name);
            Assert.Contains("San Francisco Set", client.EndReason);
        }

        [Test]
        public void NothingIsComparedBeforeThePassword()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "hunter2" });
            var (wrong, _, _) = rig.AddClient("Anna", "nope", tweak: i => i.Content.Clear());
            rig.Run(100);
            Assert.Equal(RefuseReason.BadPassword, wrong.RefusedFor, wrong.EndReason);
            Assert.Null(wrong.EndReport, "a wrong password learns nothing about the host's content");
            var (right, _, _) = rig.AddClient("Ben", "hunter2", tweak: i => i.Content.Clear());
            rig.Run(100);
            Assert.Equal("content", right.EndReport?.Kind, right.EndReason);
        }

        [Test]
        public void DifferentAssetPacksJoinAndAreTold()
        {
            var rig = new SessionRig();
            var hostSide = new List<string>();
            rig.Host.JoinerDiffers += (peer, name, report) => hostSide.Add(name + ": " + report.AssetsMissing.Count);
            var (client, _, _) = rig.AddClient("Anna", tweak: i =>
            {
                i.AssetPacks.Clear();
                i.AssetPacks.Add(new ContentEntry { Id = "90001", Name = "Fences", Version = "1.0", Digest = 3, Prefabs = 4 });
            });
            MismatchReport received = null;
            client.JoinNoticeReceived += r => received = r;
            rig.Run(400);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.NotNull(received);
            Assert.Same(received, client.JoinNotice);
            Assert.Equal("notice", received.Kind);
            Assert.Equal("Bus Stops", received.AssetsMissing[0].Name);
            Assert.Equal("75426", received.AssetsMissing[0].PlatformId);
            Assert.Equal("Fences", received.AssetsExtra[0].Name);
            Assert.SequenceEqual(new[] { "Anna: 1" }, hostSide);
        }

        [Test]
        public void RequireSameAssetsRefusesTheSameJoiner()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", RequireSameAssets = true });
            var (client, _, _) = rig.AddClient("Anna", tweak: i => i.AssetPacks.Clear());
            rig.Run(100);
            Assert.Equal(ClientState.Ended, client.State);
            Assert.Equal("assets", client.EndReport?.Kind, client.EndReason);
            Assert.Equal("Bus Stops", client.EndReport.AssetsMissing[0].Name);
            Assert.Contains("requires the same asset packs", client.EndReason);
        }

        [Test]
        public void ModSettingsAreToldAndTheJoinGoesOn()
        {
            var rig = new SessionRig();
            var (client, _, _) = rig.AddClient("Anna", tweak: i => i.Settings[0] = new SettingEntry { Mod = "Traffic", Key = "AllowUTurns", Value = "False" });
            rig.Run(400);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.Equal(1, client.JoinNotice.Settings.Count);
            Assert.Equal("True", client.JoinNotice.Settings[0].HostValue);
            Assert.Equal("False", client.JoinNotice.Settings[0].ClientValue);
        }

        [Test]
        public void AnIdenticalJoinerGetsNoNotice()
        {
            var rig = new SessionRig();
            bool told = false;
            rig.Host.JoinerDiffers += (peer, name, report) => told = true;
            var (client, _, _) = rig.AddClient("Anna");
            rig.Run(400);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.Null(client.JoinNotice);
            Assert.False(told);
        }

        [Test]
        public void ModsTheHostAllowsAreTold()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", RequireSameMods = false });
            var (client, _, _) = rig.AddClient("Anna", tweak: i => i.Mods.Clear());
            rig.Run(400);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.Equal("Move It", client.JoinNotice.Missing[0].Name);
        }

        [Test]
        public void AnAssetListTooLongToSendIsToldAsUnknown()
        {
            var rig = new SessionRig();
            var (client, _, _) = rig.AddClient("Anna", tweak: i =>
            {
                i.AssetPacks.Add(new ContentEntry { Id = "1", Name = "One more" });
                i.WithholdAssetPacks = true;
            });
            rig.Run(400);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.True(client.JoinNotice.AssetsUnknown);
        }
    }
}
