using System;
using System.Collections.Generic;
using System.IO;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Session
{
    internal sealed class FakeInfo : IGameInfoProvider
    {
        public ulong ModBuild = 42;
        public string GameVersion = "1.2.3";
        public List<ModEntry> Mods = new List<ModEntry> { new ModEntry { Name = "Move It", Version = "1.0", PlatformId = "100" } };

        public HandshakeDigests GetDigests() => new HandshakeDigests { ProtocolVersion = ProtocolVersion.Current, ModBuild = ModBuild, GameVersion = GameVersion, GameFullVersion = GameVersion, Locale = "en-US" };

        public HandshakeManifest BuildManifest(bool includePrefabList) => new HandshakeManifest { Digests = GetDigests(), Mods = new List<ModEntry>(Mods) };
    }

    internal sealed class FakeSnapshots : ISnapshotProvider
    {
        public byte[] Data;
        public int Requests;
        public readonly List<uint> Released = new List<uint>();
        private readonly string _dir;
        private uint _next = 1;

        public FakeSnapshots(int size)
        {
            Data = new byte[size];
            new DeterministicRandom(7).NextBytes(Data, 0, size);
            _dir = Path.Combine(Path.GetTempPath(), "st-session-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        /// <summary>When set, snapshots are not delivered (the host keeps "saving the city").</summary>
        public bool Hold;

        private readonly List<Action<SnapshotReady>> _pending = new List<Action<SnapshotReady>>();

        /// <summary>Completes the snapshots requested while <see cref="Hold"/> was set.</summary>
        public void DeliverPending()
        {
            var pending = new List<Action<SnapshotReady>>(_pending);
            _pending.Clear();
            foreach (Action<SnapshotReady> onReady in pending) Deliver(onReady);
        }

        public void RequestSnapshot(Action<SnapshotReady> onReady, Action<string> onFailed)
        {
            Requests++;
            if (Hold)
            {
                _pending.Add(onReady);
                return;
            }
            Deliver(onReady);
        }

        private void Deliver(Action<SnapshotReady> onReady)
        {
            uint id = _next++;
            string path = Path.Combine(_dir, id + ".save");
            File.WriteAllBytes(path, Data);
            var ready = new SnapshotReady { FilePath = path, Manifest = new SnapshotManifest { SnapshotId = id, ChunkSize = BlobCodec.DefaultChunkBytes, GameModeName = "Normal" } };
            onReady(ready);
        }

        public void Release(uint snapshotId) => Released.Add(snapshotId);
    }

    internal sealed class FakeLoader : IWorldLoader
    {
        public MemoryStream Target;
        public Action<LoadOutcome> PendingDone;
        public bool Fail;
        public bool Defer;
        public bool IsLoading => PendingDone != null;

        public Stream OpenBlobTarget(SnapshotManifest manifest)
        {
            Target = new KeepOpenStream();
            return Target;
        }

        public void BeginLoad(SnapshotManifest manifest, IReadOnlyList<SettingEntry> pushedSettings, Action<LoadOutcome> onDone)
        {
            var outcome = new LoadOutcome { Success = !Fail, FailureDetail = Fail ? "missing asset pack" : null };
            if (Defer) PendingDone = onDone;
            else onDone(outcome);
        }

        public void Finish()
        {
            Action<LoadOutcome> d = PendingDone;
            PendingDone = null;
            d(new LoadOutcome { Success = !Fail });
        }

        private sealed class KeepOpenStream : MemoryStream
        {
            protected override void Dispose(bool disposing)
            {
                // keep the bytes readable for the assertions
            }
        }
    }

    internal sealed class SessionRig
    {
        public readonly ManualClock Clock = new ManualClock(100);
        public readonly ListLog Log = new ListLog(LogLevel.Debug);
        public readonly FakeInfo HostInfo = new FakeInfo();
        public readonly FakeSnapshots Snapshots;
        public readonly SessionHost Host;
        public readonly LoopbackTransport HostTransport;
        public readonly List<(SessionClient Client, FakeLoader Loader, FakeInfo Info)> Clients = new List<(SessionClient, FakeLoader, FakeInfo)>();

        public SessionRig(HostOptions options = null, int snapshotBytes = 300_000)
        {
            Snapshots = new FakeSnapshots(snapshotBytes);
            var (host, _) = LoopbackTransport.CreatePair();
            HostTransport = host;
            Host = new SessionHost(host, HostInfo, Snapshots, options ?? new HostOptions { HostName = "Hosty" }, Clock, Log);
            Host.Start();
        }

        /// <summary>The transport of each client, in the order of <see cref="Clients"/>.</summary>
        public readonly List<LoopbackTransport> ClientTransports = new List<LoopbackTransport>();

        public (SessionClient Client, FakeLoader Loader, FakeInfo Info) AddClient(string name, string password = "", Action<FakeInfo> tweak = null, Action<FakeLoader> tweakLoader = null, ulong steamId = 0)
        {
            var info = new FakeInfo();
            tweak?.Invoke(info);
            var loader = new FakeLoader();
            tweakLoader?.Invoke(loader);
            ulong id = steamId != 0 ? steamId : (ulong)(1000 + Clients.Count);
            LoopbackTransport t = HostTransport.CreateClient(new PeerIdentity(id, "loop:" + name, name));
            var client = new SessionClient(t, info, loader, new ClientOptions { PlayerName = name, Password = password, SteamId = id }, Clock, Log);
            client.Connect(new Endpoint());
            Clients.Add((client, loader, info));
            ClientTransports.Add(t);
            return (client, loader, info);
        }

        /// <summary>Advances time and ticks only the host (the clients' games are stalled or gone).</summary>
        public void RunHostOnly(double seconds, double step = 0.5)
        {
            for (double t = 0; t < seconds; t += step)
            {
                Clock.Advance(step);
                Host.Tick();
            }
        }

        /// <summary>Advances time and ticks only the given client (the host stopped answering).</summary>
        public void RunClientOnly(SessionClient client, double seconds, double step = 0.5)
        {
            for (double t = 0; t < seconds; t += step)
            {
                Clock.Advance(step);
                client.Tick();
            }
        }

        public void Run(int frames = 200)
        {
            for (int i = 0; i < frames; i++)
            {
                Clock.Advance(1 / 60.0);
                Host.Tick();
                foreach (var c in Clients) c.Client.Tick();
            }
        }
    }

    public sealed class SessionTests
    {
        [Test]
        public void ClientJoinsDownloadsTheCityAndGoesLive()
        {
            var rig = new SessionRig(snapshotBytes: 1_300_000);
            var (client, loader, _) = rig.AddClient("Anna");
            bool live = false;
            client.BecameLive += () => live = true;
            rig.Run(400);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.True(live);
            Assert.SequenceEqual(rig.Snapshots.Data, loader.Target.ToArray(), "the loaded bytes are the host's snapshot");
            Assert.Equal(1, rig.Snapshots.Requests);
            Assert.Equal(2, client.Players.Count, "host and client in the list");
            Assert.Equal("Hosty", client.Players[0].Name);
            Assert.Equal(PlayerState.Live, client.Players[1].State);
            Assert.False(rig.Host.HoldSimulation, "nobody is joining any more");
            Assert.Equal(1, rig.Snapshots.Released.Count, "the snapshot file is released after the transfer");
            Assert.Equal(1, rig.Host.LivePeerCount);
        }

        [Test]
        public void HostHoldsTheSimulationWhileAPlayerJoins()
        {
            var rig = new SessionRig();
            var (client, loader, _) = rig.AddClient("Anna", tweakLoader: l => l.Defer = true);
            rig.Run(200);
            Assert.Equal(ClientState.Loading, client.State, client.EndReason);
            Assert.True(rig.Host.HoldSimulation);
            loader.Finish();
            rig.Run(30);
            Assert.Equal(ClientState.Live, client.State);
            Assert.False(rig.Host.HoldSimulation);
        }

        [Test]
        public void PasswordIsCheckedWithoutSendingIt()
        {
            var rig = new SessionRig(new HostOptions { Password = "hunter2" });
            var (wrong, _, _) = rig.AddClient("Bad", "nope");
            var (none, _, _) = rig.AddClient("None");
            var (right, _, _) = rig.AddClient("Good", "hunter2");
            rig.Run(300);
            Assert.Equal(ClientState.Ended, wrong.State);
            Assert.Contains("Wrong password", wrong.EndReason);
            Assert.Equal(ClientState.Ended, none.State);
            Assert.Contains("password", none.EndReason);
            Assert.Equal(ClientState.Live, right.State, right.EndReason);
            Assert.DoesNotContain("hunter2", string.Join("\n", rig.Log.Lines));
        }

        [Test]
        public void DifferentModsAreRefusedWithAReadableList()
        {
            var rig = new SessionRig();
            var (client, _, _) = rig.AddClient("Anna", tweak: i =>
            {
                i.Mods.Clear();
                i.Mods.Add(new ModEntry { Name = "Traffic", Version = "2.0", PlatformId = "200" });
            });
            rig.Run(100);
            Assert.Equal(ClientState.Ended, client.State);
            Assert.Contains("Missing (enable these): Move It 1.0", client.EndReason);
            Assert.Contains("Not on host (disable these): Traffic 2.0", client.EndReason);
        }

        [Test]
        public void DifferentModsAreAllowedWhenTheHostSaysSo()
        {
            var rig = new SessionRig(new HostOptions { RequireSameMods = false });
            var (client, _, _) = rig.AddClient("Anna", tweak: i => i.Mods.Clear());
            rig.Run(300);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
        }

        [Test]
        public void DifferentModBuildOrGameVersionIsRefused()
        {
            var rig = new SessionRig();
            var (a, _, _) = rig.AddClient("A", tweak: i => i.ModBuild = 43);
            var (b, _, _) = rig.AddClient("B", tweak: i => i.GameVersion = "9.9.9");
            rig.Run(100);
            Assert.Contains("Skyline Together builds differ", a.EndReason);
            Assert.Contains("Game versions differ", b.EndReason);
        }

        [Test]
        public void ChatIsRelayedToEveryone()
        {
            var rig = new SessionRig();
            var (a, _, _) = rig.AddClient("Anna");
            var (b, _, _) = rig.AddClient("Ben");
            rig.Run(400);
            var hostSeen = new List<string>();
            var bSeen = new List<string>();
            rig.Host.ChatReceived += e => hostSeen.Add(e.Name + ": " + e.Text);
            b.ChatReceived += e => { if (!e.IsSystem) bSeen.Add(e.Name + ": " + e.Text); };
            a.SendChat("hello there");
            rig.Host.SendChat("welcome");
            rig.Run(10);
            Assert.Contains("Anna: hello there", hostSeen);
            Assert.Contains("Anna: hello there", bSeen);
            Assert.Contains("Hosty: welcome", bSeen);
        }

        [Test]
        public void ReplicationMessagesSentDuringTheJoinAreDeliveredInOrderAfterLive()
        {
            var rig = new SessionRig();
            var (client, loader, _) = rig.AddClient("Anna", tweakLoader: l => l.Defer = true);
            var got = new List<byte>();
            client.AppMessage += e => got.Add(e.Body[e.BodyOffset]);
            rig.Run(5);
            for (byte i = 0; i < 20; i++) rig.Host.Broadcast(MessageKind.Epoch, new[] { i, (byte)7 }, 0, 2);
            rig.Run(200);
            Assert.Equal(0, got.Count, "nothing is applied while the city loads");
            loader.Finish();
            rig.Run(10);
            Assert.Equal(20, got.Count);
            for (int i = 0; i < 20; i++) Assert.Equal((byte)i, got[i]);
            rig.Host.Broadcast(MessageKind.Epoch, new byte[] { 99 }, 0, 1);
            rig.Run(2);
            Assert.Equal((byte)99, got[20], "live messages flow directly");
        }

        [Test]
        public void LargeReplicationMessagesAreFragmented()
        {
            var rig = new SessionRig();
            var (client, _, _) = rig.AddClient("Anna");
            rig.Run(300);
            var big = new byte[2_000_000];
            new DeterministicRandom(3).NextBytes(big, 0, big.Length);
            byte[] received = null;
            client.AppMessage += e =>
            {
                received = new byte[e.BodyCount];
                Buffer.BlockCopy(e.Body, e.BodyOffset, received, 0, e.BodyCount);
            };
            rig.Host.Broadcast(MessageKind.Epoch, big, 0, big.Length);
            rig.Run(20);
            Assert.NotNull(received);
            Assert.SequenceEqual(big, received);
        }

        [Test]
        public void FailedLoadRemovesThePlayer()
        {
            var rig = new SessionRig();
            var (client, _, _) = rig.AddClient("Anna", tweakLoader: l => l.Fail = true);
            rig.Run(300);
            Assert.Equal(ClientState.Ended, client.State);
            Assert.Contains("missing asset pack", client.EndReason);
            Assert.Equal(1, rig.Host.GetPlayers().Count);
            Assert.False(rig.Host.HoldSimulation);
        }

        [Test]
        public void SpeedChangesReachLiveClients()
        {
            var rig = new SessionRig();
            var (client, _, _) = rig.AddClient("Anna");
            rig.Run(300);
            rig.Host.Speed = new SpeedState { Speed = 3, Paused = false };
            rig.Run(3);
            Assert.Equal(3, client.Speed.Speed);
            Assert.False(client.Speed.Paused);
        }

        [Test]
        public void ClientSpeedRequestsReachTheHost()
        {
            var rig = new SessionRig();
            var (client, _, _) = rig.AddClient("Anna");
            rig.Run(300);
            SpeedState? asked = null;
            rig.Host.SpeedRequested += (peer, s) => asked = s;
            client.RequestSpeed(new SpeedState { Speed = 2, Paused = false });
            rig.Run(3);
            Assert.True(asked.HasValue);
            Assert.Equal(2, asked.Value.Speed);
        }

        [Test]
        public void LeavingAndKickingUpdateThePlayerList()
        {
            var rig = new SessionRig();
            var (a, _, _) = rig.AddClient("Anna");
            var (b, _, _) = rig.AddClient("Ben");
            rig.Run(400);
            Assert.Equal(3, rig.Host.GetPlayers().Count);
            a.Leave();
            rig.Run(30);
            Assert.Equal(2, rig.Host.GetPlayers().Count);
            Assert.Equal(2, b.Players.Count);
            rig.Host.Kick(b.LocalPeer, "be nice");
            rig.Run(30);
            Assert.Equal(ClientState.Ended, b.State);
            Assert.Contains("be nice", b.EndReason);
            Assert.Equal(1, rig.Host.GetPlayers().Count);
        }

        [Test]
        public void HostStopEndsEveryClient()
        {
            var rig = new SessionRig();
            var (a, _, _) = rig.AddClient("Anna");
            rig.Run(300);
            rig.Host.Stop("Host closed the city");
            for (int i = 0; i < 10; i++) a.Tick();
            Assert.Equal(ClientState.Ended, a.State);
            Assert.Contains("Host closed the city", a.EndReason);
        }

        [Test]
        public void SecondJoinerGetsAFreshSnapshotWhileTheFirstStillDownloads()
        {
            var rig = new SessionRig(snapshotBytes: 5_000_000);
            var (a, la, _) = rig.AddClient("Anna");
            rig.Run(3);
            var (b, lb, _) = rig.AddClient("Ben");
            rig.Run(800);
            Assert.Equal(ClientState.Live, a.State, a.EndReason);
            Assert.Equal(ClientState.Live, b.State, b.EndReason);
            Assert.Equal(2, rig.Snapshots.Requests);
            Assert.Equal(2, rig.Snapshots.Released.Count);
            Assert.SequenceEqual(rig.Snapshots.Data, lb.Target.ToArray());
        }

        [Test]
        public void ModComparisonMatchesByPlatformIdAndVersion()
        {
            var host = new List<ModEntry> { new ModEntry { Name = "A", Version = "1", PlatformId = "1" }, new ModEntry { Name = "B", Version = "1" } };
            var same = new List<ModEntry> { new ModEntry { Name = "A renamed", Version = "1", PlatformId = "1" }, new ModEntry { Name = "b", Version = "1" } };
            Assert.Null(SessionPayloads.CompareMods(host, same));
            var older = new List<ModEntry> { new ModEntry { Name = "A", Version = "0.9", PlatformId = "1" }, new ModEntry { Name = "B", Version = "1" } };
            Assert.Contains("Different version: A (host 1, you 0.9)", SessionPayloads.CompareMods(host, older));
        }
    }
}
