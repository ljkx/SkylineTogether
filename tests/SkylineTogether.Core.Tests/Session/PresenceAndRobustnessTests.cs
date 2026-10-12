using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Session
{
    public sealed class PresenceAndRobustnessTests
    {
        private static PresenceMessage Cursor(float x, PresenceTool tool = PresenceTool.Net, int prefab = 12)
        {
            return new PresenceMessage
            {
                From = new PeerId(77), // a lie the host must overwrite
                CursorX = x,
                CursorY = 2,
                CursorZ = -5,
                ToolKind = PresenceToolKind.Pack(tool, true),
                Prefab = prefab >= 0 ? new PrefabRank(prefab) : PrefabRank.None,
                Color = 0x112233FF,
                BrushRadius = 0,
            };
        }

        private static void RunUntil(SessionRig rig, System.Func<bool> done, int maxFrames = 2000)
        {
            for (int i = 0; i < maxFrames && !done(); i++) rig.Run(1);
        }

        private static void RunSeconds(SessionRig rig, double seconds, double step = 1.0)
        {
            for (double t = 0; t < seconds; t += step)
            {
                rig.Clock.Advance(step);
                rig.Host.Tick();
                foreach (var c in rig.Clients) c.Client.Tick();
            }
        }

        // ================================================================== presence relay

        [Test]
        public void PresenceIsRelayedToTheOtherPlayersWithTheSendersRealId()
        {
            var rig = new SessionRig();
            var (a, _, _) = rig.AddClient("Anna");
            var (b, _, _) = rig.AddClient("Ben");
            rig.Run(400);
            var hostGot = new List<PresenceMessage>();
            var aGot = new List<PresenceMessage>();
            var bGot = new List<PresenceMessage>();
            rig.Host.PresenceReceived += hostGot.Add;
            a.PresenceReceived += aGot.Add;
            b.PresenceReceived += bGot.Add;

            Assert.True(a.SendPresence(Cursor(10)));
            rig.Run(3);

            Assert.Equal(1, hostGot.Count);
            Assert.Equal(a.LocalPeer, hostGot[0].From, "the host stamps the sender");
            Assert.Equal(1, bGot.Count, "relayed to the other client");
            Assert.Equal(a.LocalPeer, bGot[0].From);
            Assert.Equal(10f, bGot[0].CursorX);
            Assert.Equal(-5f, bGot[0].CursorZ);
            Assert.Equal(12, bGot[0].Prefab.Value);
            Assert.Equal(PresenceTool.Net, PresenceToolKind.Tool(bGot[0].ToolKind));
            Assert.True(PresenceToolKind.CursorVisible(bGot[0].ToolKind));
            Assert.Equal(0x112233FFu, bGot[0].Color);
            Assert.Equal(0, aGot.Count, "nobody gets their own presence back");
        }

        [Test]
        public void HostPresenceReachesEveryLiveClient()
        {
            var rig = new SessionRig();
            var (a, _, _) = rig.AddClient("Anna");
            var (b, _, _) = rig.AddClient("Ben");
            rig.Run(400);
            var aGot = new List<PresenceMessage>();
            var bGot = new List<PresenceMessage>();
            a.PresenceReceived += aGot.Add;
            b.PresenceReceived += bGot.Add;
            rig.Host.SendPresence(Cursor(3, PresenceTool.Zone, -1));
            rig.Run(2);
            Assert.Equal(1, aGot.Count);
            Assert.Equal(1, bGot.Count);
            Assert.True(aGot[0].From.IsHost);
            Assert.True(aGot[0].Prefab.IsNone);
            Assert.Equal(PresenceTool.Zone, PresenceToolKind.Tool(bGot[0].ToolKind));
        }

        [Test]
        public void PresenceNeverReachesOrLeavesAJoinerAndIsNotBuffered()
        {
            var rig = new SessionRig();
            var (live, _, _) = rig.AddClient("Anna");
            rig.Run(400);
            var (joiner, loader, _) = rig.AddClient("Ben", tweakLoader: l => l.Defer = true);
            var joinerGot = new List<PresenceMessage>();
            var appMessages = 0;
            joiner.PresenceReceived += joinerGot.Add;
            joiner.AppMessage += e => appMessages++;
            RunUntil(rig, () => joiner.State == ClientState.Loading);
            Assert.Equal(ClientState.Loading, joiner.State, joiner.EndReason);
            Assert.False(joiner.SendPresence(Cursor(1)), "a joiner sends no presence");
            rig.Host.SendPresence(Cursor(2));
            live.SendPresence(Cursor(3));
            rig.Run(5);
            loader.Finish();
            rig.Run(10);
            Assert.Equal(ClientState.Live, joiner.State);
            Assert.Equal(0, joinerGot.Count, "presence is cosmetic: dropped while joining, never replayed");
            Assert.Equal(0, appMessages, "presence never reaches the replication layer");
            rig.Host.SendPresence(Cursor(4));
            rig.Run(2);
            Assert.Equal(1, joinerGot.Count);
        }

        [Test]
        public void PresenceFloodsAreDroppedByTheHost()
        {
            var rig = new SessionRig();
            var (a, _, _) = rig.AddClient("Anna");
            rig.Run(400);
            int got = 0;
            rig.Host.PresenceReceived += m => got++;
            for (int i = 0; i < 6; i++) a.SendPresence(Cursor(i));
            rig.Run(1);
            Assert.Equal(1, got, "six updates in one host frame: one relayed");
            rig.Run(10);
            a.SendPresence(Cursor(9));
            rig.Run(1);
            Assert.Equal(2, got);
        }

        // ================================================================== presence table and throttle

        [Test]
        public void PresenceTableKeepsTheLatestStateExpiresAndDropsPlayersWhoLeft()
        {
            var table = new PresenceTable { StaleSeconds = 5 };
            PresenceMessage m = Cursor(1);
            m.From = new PeerId(2);
            table.Update(m, 10, PeerId.Host);
            int v = table.Version;
            m.CursorX = 50;
            table.Update(m, 11, PeerId.Host);
            Assert.Equal(1, table.Entries.Count);
            Assert.Equal(50f, table.Entries[0].X);
            Assert.Equal(v, table.Version, "a cursor move is not a version change");
            m.ToolKind = PresenceToolKind.Pack(PresenceTool.Bulldoze, false);
            table.Update(m, 12, PeerId.Host);
            Assert.True(table.Version > v, "a tool change is");
            Assert.False(table.Entries[0].CursorVisible);
            Assert.Equal(PresenceTool.Bulldoze, table.Entries[0].Tool);

            PresenceMessage own = Cursor(1);
            own.From = new PeerId(3);
            table.Update(own, 12, new PeerId(3));
            Assert.Equal(1, table.Entries.Count, "the local player's own presence is not stored");

            PresenceMessage other = Cursor(1);
            other.From = new PeerId(1);
            table.Update(other, 12, PeerId.Host);
            Assert.Equal(1, table.Entries[0].Peer.Value, "sorted by peer");
            table.RetainOnly(new List<PeerId> { PeerId.Host, new PeerId(2) });
            Assert.Equal(1, table.Entries.Count);
            Assert.Equal(2, table.Entries[0].Peer.Value);

            table.Prune(16.9);
            Assert.Equal(1, table.Entries.Count);
            table.Prune(17.1);
            Assert.Equal(0, table.Entries.Count, "expired");
            table.Update(m, 20, PeerId.Host);
            Assert.True(table.Remove(new PeerId(2)));
            Assert.False(table.Remove(new PeerId(2)));
        }

        [Test]
        public void PresenceThrottleSendsAtTenHertzWhileMovingAndOncePerSecondWhenIdle()
        {
            var t = new PresenceThrottle();
            int sent = 0;
            for (int i = 0; i < 100; i++)
            {
                double now = i / 60.0;
                if (t.ShouldSend(now, i * 2f, 0, 0, 2, 5, 0)) sent++;
            }
            Assert.InRange(sent, 15, 18, "about 10 Hz over 1.65 s of movement");
            sent = 0;
            for (int i = 0; i < 300; i++)
            {
                if (t.ShouldSend(2 + i / 60.0, 1000f, 0, 0, 2, 5, 0)) sent++;
            }
            Assert.InRange(sent, 5, 6, "about 1 Hz over 5 s idle (the first send carries the jump)");
            Assert.True(t.ShouldSend(10, 1000f, 0, 0, 3, 5, 0) || t.ShouldSend(10.2, 1000f, 0, 0, 3, 5, 0), "a tool change is sent at once");
        }

        [Test]
        public void ToolKindPacksToolAndCursorVisibility()
        {
            byte b = PresenceToolKind.Pack(PresenceTool.Terrain, false);
            Assert.Equal(PresenceTool.Terrain, PresenceToolKind.Tool(b));
            Assert.False(PresenceToolKind.CursorVisible(b));
            Assert.True(PresenceToolKind.CursorVisible(PresenceToolKind.Pack(PresenceTool.Select, true)));
            Assert.Equal(PresenceTool.Other, PresenceToolKind.Tool(0x7E), "unknown tools from a newer peer read as other");
            Assert.Equal("building", PresenceToolKind.Verb(PresenceTool.Net));
            Assert.Null(PresenceToolKind.Verb(PresenceTool.Select));
        }

        // ================================================================== join progress and watchdogs

        [Test]
        public void LoadProgressPayloadCarriesThePhase()
        {
            var w = new ByteWriter(16);
            SessionPayloads.WriteLoadProgress(w, JoinPhase.Downloading, 1.7f);
            var r = new ByteReader(w.Buffer, 0, w.Length);
            Assert.Equal(JoinPhase.Downloading, SessionPayloads.ReadLoadProgress(r, out float f));
            Assert.Equal(1f, f, "clamped");
            w.Reset();
            SessionPayloads.WriteLoadProgress(w, JoinPhase.Loading, 0.25f);
            r = new ByteReader(w.Buffer, 0, w.Length);
            Assert.Equal(JoinPhase.Loading, SessionPayloads.ReadLoadProgress(r, out f));
            Assert.Equal(0.25f, f);
        }

        [Test]
        public void HostShowsDownloadingUntilTheJoinerReallyLoads()
        {
            var rig = new SessionRig(snapshotBytes: 8_000_000);
            var (client, loader, _) = rig.AddClient("Anna", tweakLoader: l => l.Defer = true);
            RunUntil(rig, () => client.State == ClientState.Downloading);
            Assert.Equal(ClientState.Downloading, client.State, client.EndReason);
            // the joiner reports its download progress while the host is busy elsewhere
            rig.RunClientOnly(client, 1.0, 1 / 60.0);
            rig.Host.Tick();
            PlayerInfo p = rig.Host.GetPlayers()[1];
            Assert.Equal(PlayerState.Downloading, p.State, "a progress report during the download must not switch to loading");
            Assert.InRange(p.Progress, 0.0, 0.999);
            RunUntil(rig, () => client.State == ClientState.Loading);
            client.ReportLoadProgress(0.4f);
            rig.Run(60);
            p = rig.Host.GetPlayers()[1];
            Assert.Equal(PlayerState.Loading, p.State);
            Assert.Near(0.4, p.Progress, 0.001);
            loader.Finish();
            rig.Run(10);
            Assert.Equal(ClientState.Live, client.State);
        }

        [Test]
        public void AJoinerThatDropsMidDownloadIsCleanedUp()
        {
            var rig = new SessionRig(snapshotBytes: 8_000_000);
            var (client, _, _) = rig.AddClient("Anna");
            var left = new List<PeerId>();
            var chat = new List<string>();
            rig.Host.PeerLeft += left.Add;
            rig.Host.ChatReceived += e => chat.Add(e.Text);
            RunUntil(rig, () => client.State == ClientState.Downloading);
            Assert.Equal(ClientState.Downloading, client.State, client.EndReason);
            Assert.True(rig.Host.HoldSimulation);
            client.Dispose(); // the joiner's game crashed
            rig.Clients.Clear();
            rig.Run(5);
            Assert.Equal(1, rig.Host.GetPlayers().Count);
            Assert.False(rig.Host.HoldSimulation, "the host resumes");
            Assert.Equal(1, left.Count);
            Assert.Equal(1, rig.Snapshots.Released.Count, "the snapshot file is released");
            Assert.Contains("lost the connection while joining", string.Join("\n", chat));
            Assert.Equal(0, rig.Host.GetNetStats().Connections);
        }

        [Test]
        public void AHungJoinerIsDroppedAfterTheStallTimeoutButNotBefore()
        {
            var rig = new SessionRig();
            var (client, _, _) = rig.AddClient("Anna", tweakLoader: l => l.Defer = true);
            RunUntil(rig, () => client.State == ClientState.Loading);
            Assert.Equal(ClientState.Loading, client.State, client.EndReason);
            var chat = new List<string>();
            rig.Host.ChatReceived += e => chat.Add(e.Text);
            rig.RunHostOnly(120); // the joiner's main thread stalls in the city load
            Assert.Equal(2, rig.Host.GetPlayers().Count, "a long load is not a failure");
            Assert.True(rig.Host.HoldSimulation);
            rig.RunHostOnly(70);
            Assert.Equal(1, rig.Host.GetPlayers().Count);
            Assert.False(rig.Host.HoldSimulation, "the host is not held forever");
            Assert.Contains("stopped responding while joining", string.Join("\n", chat));
        }

        [Test]
        public void ALoadingJoinerIsNotDroppedAsTooSlowWhileItsGameDoesNotRead()
        {
            var rig = new SessionRig();
            var (client, loader, _) = rig.AddClient("Anna", tweakLoader: l => l.Defer = true);
            RunUntil(rig, () => client.State == ClientState.Loading);
            rig.HostTransport.SendBufferBytes = 4096; // the joiner's game reads nothing while it deserializes
            var big = new byte[20_000];
            for (int i = 0; i < 50; i++) rig.Host.Broadcast(MessageKind.Epoch, big, 0, big.Length);
            rig.RunHostOnly(90);
            Assert.Equal(2, rig.Host.GetPlayers().Count, "queued data older than 60 s does not drop a joiner that is loading");
            rig.HostTransport.SendBufferBytes = LoopbackTransport.DefaultSendBufferBytes;
            loader.Finish();
            rig.Run(200);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
        }

        [Test]
        public void KeepaliveTimeoutsAreRaisedWhileJoiningAndRestoredWhenLive()
        {
            var rig = new SessionRig();
            var (client, loader, _) = rig.AddClient("Anna", tweakLoader: l => l.Defer = true);
            RunUntil(rig, () => client.State == ClientState.Loading);
            var hostSide = (IConnectionTimeouts)rig.HostTransport.GetConnections()[0];
            var clientSide = (IConnectionTimeouts)rig.ClientTransports[0].GetConnections()[0];
            Assert.Equal(ConnectionTimeouts.JoiningMilliseconds, hostSide.ConnectedTimeoutMilliseconds);
            Assert.Equal(ConnectionTimeouts.JoiningMilliseconds, clientSide.ConnectedTimeoutMilliseconds);
            loader.Finish();
            rig.Run(10);
            Assert.Equal(ClientState.Live, client.State);
            Assert.Equal(ConnectionTimeouts.NormalMilliseconds, hostSide.ConnectedTimeoutMilliseconds);
            Assert.Equal(ConnectionTimeouts.NormalMilliseconds, clientSide.ConnectedTimeoutMilliseconds);
        }

        [Test]
        public void AJoinerGivesUpWhenTheHostNeverSendsTheCity()
        {
            var rig = new SessionRig();
            rig.Snapshots.Hold = true;
            var (client, _, _) = rig.AddClient("Anna");
            rig.Run(100);
            Assert.Equal(ClientState.WaitingForSnapshot, client.State, client.EndReason);
            RunSeconds(rig, 200);
            Assert.Equal(ClientState.WaitingForSnapshot, client.State, "the host may take minutes to save a big city");
            RunSeconds(rig, 110);
            Assert.Equal(ClientState.Ended, client.State);
            Assert.Contains("did not send the city", client.EndReason);
            rig.Run(5);
            Assert.Equal(1, rig.Host.GetPlayers().Count, "the host forgot the joiner");
        }

        [Test]
        public void AJoinerGivesUpWhenTheDownloadStalls()
        {
            var rig = new SessionRig(snapshotBytes: 8_000_000);
            var (client, _, _) = rig.AddClient("Anna");
            RunUntil(rig, () => client.State == ClientState.Downloading);
            Assert.Equal(ClientState.Downloading, client.State, client.EndReason);
            rig.RunClientOnly(client, 100);
            Assert.Equal(ClientState.Downloading, client.State);
            rig.RunClientOnly(client, 30);
            Assert.Equal(ClientState.Ended, client.State);
            Assert.Contains("stalled", client.EndReason);
        }

        [Test]
        public void AJoinerGivesUpWhenTheHostNeverConfirmsTheLoad()
        {
            var rig = new SessionRig();
            var (client, loader, _) = rig.AddClient("Anna", tweakLoader: l => l.Defer = true);
            RunUntil(rig, () => client.State == ClientState.Loading);
            rig.RunClientOnly(client, 60); // a long load: no limit while the game loads
            Assert.Equal(ClientState.Loading, client.State);
            loader.Finish();
            rig.RunClientOnly(client, 100);
            Assert.Equal(ClientState.Loading, client.State);
            rig.RunClientOnly(client, 30);
            Assert.Equal(ClientState.Ended, client.State);
            Assert.Contains("did not confirm", client.EndReason);
        }

        // ================================================================== duplicate connections

        [Test]
        public void ASecondConnectionOfTheSamePlayerReplacesTheStaleOne()
        {
            var rig = new SessionRig();
            var (old, _, _) = rig.AddClient("Anna", steamId: 5555);
            rig.Run(400);
            Assert.Equal(ClientState.Live, old.State, old.EndReason);
            var (fresh, _, _) = rig.AddClient("Anna", steamId: 5555);
            rig.Run(400);
            Assert.Equal(ClientState.Live, fresh.State, fresh.EndReason);
            Assert.Equal(ClientState.Ended, old.State);
            Assert.Equal(2, rig.Host.GetPlayers().Count, "host and the new connection");
        }

        [Test]
        public void ASecondAttemptWhileTheFirstStillLoadsIsRefusedClearly()
        {
            var rig = new SessionRig();
            var (first, loader, _) = rig.AddClient("Anna", steamId: 5555, tweakLoader: l => l.Defer = true);
            RunUntil(rig, () => first.State == ClientState.Loading);
            var (second, _, _) = rig.AddClient("Anna", steamId: 5555);
            rig.Run(60);
            Assert.Equal(ClientState.Ended, second.State);
            Assert.Contains("still joining", second.EndReason);
            Assert.Equal(ClientState.Loading, first.State, "the first attempt continues");
            loader.Finish();
            rig.Run(10);
            Assert.Equal(ClientState.Live, first.State);
        }

        [Test]
        public void HostNetStatsCountAdmittedPeers()
        {
            var rig = new SessionRig();
            rig.AddClient("Anna");
            rig.AddClient("Ben");
            rig.Run(400);
            SessionNetStats n = rig.Host.GetNetStats();
            Assert.Equal(2, n.Connections);
            Assert.True(n.PingMs >= 0, "pings were measured");
            Assert.Equal(2, rig.Host.DescribePeers().Count);
        }
    }
}
