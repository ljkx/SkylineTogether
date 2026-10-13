using System;
using System.Collections.Generic;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// A host and several players over <see cref="NetSimTransport"/> (latency, jitter, loss, reordering and duplication
    /// on the unreliable lane, bandwidth caps), driven frame by frame like the game drives them: the host broadcasts a
    /// numbered replication stream, players send proposals and presence, connections are cut and players rejoin through
    /// <see cref="RejoinController"/> with the normal join path. Every player checks that its replication stream is
    /// gapless and in order from its admission on.
    /// </summary>
    internal sealed class SoakRig
    {
        public const double Frame = 1 / 60.0;

        /// <summary>loadspeed: the simulated frame time (a slow game ticks the session less often).</summary>
        public double FrameSeconds = Frame;

        public readonly ManualClock Clock = new ManualClock(1000);
        public readonly ListLog Log = new ListLog(LogLevel.Info);
        public readonly FakeInfo Info = new FakeInfo();
        public readonly FakeSnapshots Snapshots;
        public readonly LoopbackTransport HostLoop;
        public readonly NetSimTransport HostNet;
        public readonly SessionHost Host;
        public readonly List<Player> Players = new List<Player>();
        public readonly DeterministicRandom Random;

        /// <summary>Impairments of what each player sends (cloned per connection; seeds differ).</summary>
        public NetSimOptions ClientOptions = new NetSimOptions();

        /// <summary>Host replication bytes per frame (random content, so compression cannot shrink it).</summary>
        public int BroadcastBytesPerFrame;

        /// <summary>Every Nth frame one host message is much larger (bursts, fragmentation).</summary>
        public int BurstEvery;
        public int BurstBytes = 200_000;

        /// <summary>The host's game is gone (its transport disposed); new connection attempts reach nobody.</summary>
        public bool HostGone;

        public uint BroadcastSeq;
        public long BroadcastTotalBytes;
        public readonly Dictionary<byte, uint> ProposalSeqByPeer = new Dictionary<byte, uint>();
        public int ProposalOrderErrors;
        public int ProposalsReceived;
        private readonly byte[] _payload = new byte[1 << 20];
        private int _seedCounter = 11;

        public sealed class Player
        {
            public string Name;
            public ulong SteamId;
            public SessionClient Client;
            public FakeLoader Loader;
            public LoopbackTransport Loop;
            public NetSimTransport Net;
            public readonly RejoinController Rejoin = new RejoinController(new RejoinOptions { BackoffSeconds = new double[] { 1, 2, 4 }, MaxAttempts = 8 });
            public bool HaveSeq;
            public uint LastSeq;
            public int Gaps;
            public long Received;
            public int LiveCount;
            public double LiveAt;
            public int Attempts;
            public readonly List<string> Ends = new List<string>();
            public uint ProposalSeq;
            public bool SendProposals;
            public bool SendPresence;
            public bool Gone;
            public bool LoaderDefer;

            public bool IsLive => Client != null && Client.State == ClientState.Live;
        }

        public SoakRig(NetSimOptions hostOptions, int snapshotBytes = 400_000, int seed = 5)
        {
            Random = new DeterministicRandom((ulong)seed);
            Random.NextBytes(_payload, 0, _payload.Length);
            Snapshots = new FakeSnapshots(snapshotBytes);
            HostLoop = LoopbackTransport.CreatePair().host;
            HostNet = new NetSimTransport(HostLoop, hostOptions, Clock);
            Host = new SessionHost(HostNet, Info, Snapshots, new HostOptions { HostName = "Host", MaxPlayers = 16 }, Clock, Log);
            Host.AppMessage += (peer, e) =>
            {
                if (e.Kind != MessageKind.Proposal || e.BodyCount < 4) return;
                uint seq = BitConverter.ToUInt32(e.Body, e.BodyOffset);
                ProposalsReceived++;
                if (ProposalSeqByPeer.TryGetValue(peer.Value, out uint last) && seq != last + 1) ProposalOrderErrors++;
                ProposalSeqByPeer[peer.Value] = seq;
            };
            Host.PeerJoining += peer => ProposalSeqByPeer.Remove(peer.Value);
            Host.Start();
        }

        public Player AddPlayer(string name, bool proposals = true, bool presence = true)
        {
            var p = new Player { Name = name, SteamId = 76561190000000000UL + (ulong)Players.Count, SendProposals = proposals, SendPresence = presence };
            Players.Add(p);
            Connect(p);
            return p;
        }

        public void Connect(Player p)
        {
            p.Attempts++;
            p.HaveSeq = false;
            p.ProposalSeq = 0;
            // a vanished host: the address no longer answers (a pair whose host end never listens)
            p.Loop = HostGone ? LoopbackTransport.CreatePair().client : HostLoop.CreateClient(new PeerIdentity(p.SteamId, "loop:" + p.Name + p.Attempts, p.Name));
            NetSimOptions o = ClientOptions.Clone();
            o.Seed = _seedCounter++;
            p.Net = new NetSimTransport(p.Loop, o, Clock);
            p.Loader = new FakeLoader { Defer = p.LoaderDefer };
            var client = new SessionClient(p.Net, Info, p.Loader, new ClientOptions { PlayerName = p.Name, SteamId = p.SteamId }, Clock, Log);
            client.AppMessage += e =>
            {
                if (e.Kind != MessageKind.Epoch || e.BodyCount < 4) return;
                uint seq = BitConverter.ToUInt32(e.Body, e.BodyOffset);
                if (p.HaveSeq && seq != p.LastSeq + 1) p.Gaps++;
                p.HaveSeq = true;
                p.LastSeq = seq;
                p.Received++;
            };
            client.BecameLive += () =>
            {
                p.LiveCount++;
                p.LiveAt = Clock.Seconds;
                p.Rejoin.OnLive();
            };
            p.Client = client;
            client.Connect(new Endpoint());
        }

        /// <summary>The network link of the player fails (both ends notice, as after a transport timeout).</summary>
        public void Cut(Player p, bool hostNotices = true)
        {
            IConnection[] connections = p.Loop.GetConnections();
            foreach (IConnection c in connections) p.Loop.Sever(c, notifyThisSide: true, notifyOtherSide: hostNotices);
        }

        public void Run(double seconds)
        {
            int frames = (int)Math.Round(seconds / FrameSeconds);
            for (int i = 0; i < frames; i++) Step();
        }

        public bool RunUntil(Func<bool> done, double maxSeconds)
        {
            int frames = (int)Math.Round(maxSeconds / FrameSeconds);
            for (int i = 0; i < frames; i++)
            {
                if (done()) return true;
                Step();
            }
            return done();
        }

        private int _frame;

        public void Step()
        {
            _frame++;
            Clock.Advance(FrameSeconds);
            double now = Clock.Seconds;
            int bytes = BroadcastBytesPerFrame;
            if (BurstEvery > 0 && _frame % BurstEvery == 0) bytes = Math.Max(bytes, BurstBytes);
            if (bytes >= 4 && !HostGone)
            {
                BroadcastSeq++;
                int offset = Random.Next(0, _payload.Length - bytes - 4);
                byte[] body = new byte[bytes];
                Buffer.BlockCopy(_payload, offset, body, 0, bytes);
                BitConverter.GetBytes(BroadcastSeq).CopyTo(body, 0);
                Host.Broadcast(MessageKind.Epoch, body, 0, body.Length);
                BroadcastTotalBytes += bytes;
            }
            if (!HostGone) Host.Tick();
            foreach (Player p in Players)
            {
                if (p.Gone) continue;
                if (p.Client != null)
                {
                    if (p.Client.State == ClientState.Loading && p.Loader.PendingDone != null && p.Loader.Defer && Random.Next(0, 30) == 0) p.Loader.Finish();
                    p.Client.Tick();
                    if (p.IsLive)
                    {
                        if (p.SendProposals && _frame % 6 == 0)
                        {
                            byte[] proposal = new byte[64 + Random.Next(0, 4000)];
                            BitConverter.GetBytes(++p.ProposalSeq).CopyTo(proposal, 0);
                            p.Client.SendToHost(MessageKind.Proposal, proposal, 0, proposal.Length);
                        }
                        if (p.SendPresence && _frame % 6 == 0)
                        {
                            p.Client.SendPresence(new PresenceMessage { CursorX = _frame, CursorY = 1, CursorZ = 2, CurveControlPoints = new float[0], Footprints = new float[0] });
                        }
                    }
                    if (p.Client.State == ClientState.Ended)
                    {
                        SessionClient ended = p.Client;
                        p.Ends.Add(ended.EndKind + ": " + ended.EndReason);
                        p.Client = null;
                        ended.Dispose();
                        p.Rejoin.OnClientEnded(ended.EndKind, ended.EndReason, ended.EnteredCity, now);
                    }
                }
                if (p.Client == null && p.Rejoin.Tick(now)) Connect(p);
            }
        }

        public int LiveCount()
        {
            int n = 0;
            foreach (Player p in Players) if (p.IsLive) n++;
            return n;
        }

        public string Describe()
        {
            var parts = new List<string>();
            foreach (Player p in Players)
            {
                parts.Add(p.Name + "=" + (p.Client != null ? p.Client.State.ToString() : "none") + " rejoin=" + p.Rejoin.Phase + " attempts=" + p.Attempts + " gaps=" + p.Gaps
                          + " ends=[" + string.Join(" | ", p.Ends) + "]");
            }
            parts.Add("host live peers=" + Host.LivePeerCount + " hold=" + Host.HoldSimulation);
            return string.Join("; ", parts);
        }

        public void AssertNoErrors()
        {
            Assert.Equal(0, Log.Count(LogLevel.Error), "errors logged: " + string.Join(" / ", ErrorLines()));
        }

        private IEnumerable<string> ErrorLines()
        {
            foreach (string l in Log.Lines)
            {
                if (l.Contains("ERROR")) yield return l;
            }
        }
    }

    public sealed class SoakTests
    {
        private static NetSimOptions Wan(int seed, int bandwidth = 0) => new NetSimOptions
        {
            LatencyMs = 60,
            JitterMs = 30,
            LossRate = 0.02,
            ReorderRate = 0.1,
            DuplicateRate = 0.02,
            BandwidthBytesPerSecond = bandwidth,
            Seed = seed,
        };

        [Test(TimeoutSeconds = 120)]
        public void ThreePlayersJoinOverALossyLinkAndReceiveAGaplessStream()
        {
            var rig = new SoakRig(Wan(1, 4 << 20), snapshotBytes: 2_000_000) { ClientOptions = Wan(2, 1 << 20), BroadcastBytesPerFrame = 2_000, BurstEvery = 120 };
            rig.AddPlayer("Anna");
            rig.AddPlayer("Ben");
            rig.AddPlayer("Cleo");
            Assert.True(rig.RunUntil(() => rig.LiveCount() == 3, 120), rig.Describe());
            rig.Run(30);
            Assert.Equal(3, rig.LiveCount(), rig.Describe());
            foreach (SoakRig.Player p in rig.Players)
            {
                Assert.Equal(0, p.Gaps, p.Name + " saw gaps");
                Assert.True(p.Received > 1000, p.Name + " received " + p.Received);
                Assert.True(rig.BroadcastSeq - p.LastSeq < 120, p.Name + " lags " + (rig.BroadcastSeq - p.LastSeq) + " messages behind");
            }
            Assert.Equal(0, rig.ProposalOrderErrors);
            Assert.True(rig.ProposalsReceived > 300, "proposals " + rig.ProposalsReceived);
            Assert.True(rig.Snapshots.Requests <= 3, "joiners that arrive together share snapshots: " + rig.Snapshots.Requests);
            Assert.Equal(3, rig.Host.LivePeerCount);
            Assert.False(rig.Host.HoldSimulation);
            rig.AssertNoErrors();
        }

        [Test(TimeoutSeconds = 120)]
        public void AJoinDuringHeavyBroadcastCompletesWithinTheBandwidth()
        {
            // 600 KB/s of replication while a 6 MB city is downloaded over a 2 MB/s link with loss
            var rig = new SoakRig(Wan(3, 2 << 20), snapshotBytes: 6_000_000) { ClientOptions = Wan(4, 1 << 20), BroadcastBytesPerFrame = 10_000 };
            SoakRig.Player first = rig.AddPlayer("Anna");
            Assert.True(rig.RunUntil(() => first.IsLive, 60), rig.Describe());
            rig.Run(5);
            SoakRig.Player late = rig.AddPlayer("Ben");
            Assert.True(rig.RunUntil(() => late.IsLive, 120), rig.Describe());
            rig.Run(20);
            Assert.True(first.IsLive && late.IsLive, rig.Describe());
            Assert.Equal(0, first.Gaps);
            Assert.Equal(0, late.Gaps, "the joiner's buffered backlog is complete and in order");
            Assert.True(rig.BroadcastSeq - late.LastSeq < 200, "the joiner caught up: " + (rig.BroadcastSeq - late.LastSeq) + " behind");
            rig.AssertNoErrors();
        }

        [Test(TimeoutSeconds = 120)]
        public void ALargeSnapshotArrivesIntactOverASlowLossyLink()
        {
            var rig = new SoakRig(new NetSimOptions { LatencyMs = 120, JitterMs = 40, LossRate = 0.05, BandwidthBytesPerSecond = 1 << 20, Seed = 9 }, snapshotBytes: 24_000_000)
            {
                ClientOptions = Wan(10),
                BroadcastBytesPerFrame = 500,
            };
            double start = rig.Clock.Seconds;
            SoakRig.Player p = rig.AddPlayer("Anna", proposals: false);
            Assert.True(rig.RunUntil(() => p.IsLive, 110), rig.Describe());
            Assert.True(p.LiveAt - start > 22, "the 1 MB/s cap applies: live after " + (p.LiveAt - start) + " s");
            Assert.SequenceEqual(rig.Snapshots.Data, p.Loader.Target.ToArray(), "the city arrived byte for byte");
            Assert.Equal(0, p.Gaps);
            rig.AssertNoErrors();
        }

        [Test(TimeoutSeconds = 180)]
        public void PlayersRejoinWithAFreshSnapshotAfterRepeatedConnectionLoss()
        {
            var rig = new SoakRig(Wan(21, 4 << 20), snapshotBytes: 1_000_000) { ClientOptions = Wan(22, 1 << 20), BroadcastBytesPerFrame = 3_000 };
            SoakRig.Player a = rig.AddPlayer("Anna");
            SoakRig.Player b = rig.AddPlayer("Ben");
            Assert.True(rig.RunUntil(() => rig.LiveCount() == 2, 60), rig.Describe());
            for (int cycle = 0; cycle < 5; cycle++)
            {
                rig.Run(3);
                // alternate: both ends notice at once, or only the player does (the host learns of it from the rejoin)
                rig.Cut(cycle % 2 == 0 ? a : b, hostNotices: cycle % 4 == 0);
                rig.Run(0.5);
                SoakRig.Player cut = cycle % 2 == 0 ? a : b;
                Assert.True(cut.Rejoin.Recovering || cut.IsLive, "recovery started: " + rig.Describe());
                Assert.True(rig.RunUntil(() => rig.LiveCount() == 2, 90), "cycle " + cycle + ": " + rig.Describe());
            }
            rig.Run(10);
            Assert.Equal(2, rig.LiveCount(), rig.Describe());
            Assert.Equal(2, rig.Host.LivePeerCount, "no stale peers on the host: " + rig.Describe());
            Assert.True(a.LiveCount >= 3 && b.LiveCount >= 3, rig.Describe());
            foreach (SoakRig.Player p in rig.Players)
            {
                Assert.Equal(RejoinPhase.Idle, p.Rejoin.Phase);
                Assert.Equal(0, p.Gaps, p.Name + ": every session's stream is gapless");
                foreach (string end in p.Ends) Assert.True(end.StartsWith("ConnectionLost") || end.StartsWith("Timeout") || end.StartsWith("RefusedTemporarily") || end.StartsWith("Unreachable"), p.Name + ": unexpected end " + end);
            }
            Assert.True(rig.Snapshots.Requests >= 6, "each rejoin took a fresh snapshot: " + rig.Snapshots.Requests);
            rig.Run(2);
            Assert.Equal(rig.Snapshots.Requests, rig.Snapshots.Released.Count, "every snapshot file was released");
            rig.AssertNoErrors();
        }

        [Test(TimeoutSeconds = 120)]
        public void ALinkCutDuringTheDownloadRejoinsAndTheHostResumesTheSimulation()
        {
            var rig = new SoakRig(new NetSimOptions { LatencyMs = 40, BandwidthBytesPerSecond = 1 << 20, Seed = 31 }, snapshotBytes: 4_000_000) { BroadcastBytesPerFrame = 1_000 };
            SoakRig.Player p = rig.AddPlayer("Anna");
            Assert.True(rig.RunUntil(() => p.Client != null && p.Client.State == ClientState.Downloading && p.Client.JoinProgress > 0.1, 30), rig.Describe());
            Assert.True(rig.Host.HoldSimulation);
            rig.Cut(p, hostNotices: true);
            rig.Run(1);
            Assert.False(rig.Host.HoldSimulation, "the host's simulation is no longer held for a joiner that is gone");
            // a first join that fails before the city loads is not a recovery case: the player is still where it was
            Assert.Equal(RejoinPhase.Idle, p.Rejoin.Phase, rig.Describe());
            Assert.Null(p.Client);
            rig.Connect(p);
            Assert.True(rig.RunUntil(() => p.IsLive, 60), rig.Describe());
            rig.AssertNoErrors();
        }

        [Test(TimeoutSeconds = 120)]
        public void ALinkCutWhileLoadingTheCityRecoversThroughRejoin()
        {
            var rig = new SoakRig(new NetSimOptions { LatencyMs = 40, Seed = 41 }, snapshotBytes: 500_000) { BroadcastBytesPerFrame = 800 };
            SoakRig.Player p = rig.AddPlayer("Anna");
            p.Loader.Defer = true;
            Assert.True(rig.RunUntil(() => p.Client != null && p.Client.State == ClientState.Loading, 30), rig.Describe());
            rig.Cut(p, hostNotices: false); // the host still believes the joiner is loading
            rig.Run(0.5);
            Assert.True(p.Rejoin.Recovering, "the player is in the host's (half loaded) city: recovery starts. " + rig.Describe());
            // the rejoin is refused while the host still counts the old attempt as joining, then succeeds once the host
            // drops it (15 s without a word) or notices the cut
            Assert.True(rig.RunUntil(() => p.IsLive, 120), rig.Describe());
            Assert.Equal(1, rig.Host.LivePeerCount, rig.Describe());
            Assert.False(rig.Host.HoldSimulation);
            rig.AssertNoErrors();
        }

        [Test(TimeoutSeconds = 120)]
        public void WhenTheHostEndsTheSessionPlayersAreAskedInsteadOfRetrying()
        {
            var rig = new SoakRig(new NetSimOptions { LatencyMs = 30, Seed = 51 }) { BroadcastBytesPerFrame = 500 };
            SoakRig.Player a = rig.AddPlayer("Anna");
            SoakRig.Player b = rig.AddPlayer("Ben");
            Assert.True(rig.RunUntil(() => rig.LiveCount() == 2, 30), rig.Describe());
            rig.Host.Stop("The host left the city.");
            rig.Run(3);
            foreach (SoakRig.Player p in rig.Players)
            {
                Assert.Null(p.Client);
                Assert.Equal(RejoinPhase.NeedsChoice, p.Rejoin.Phase, rig.Describe());
                Assert.False(p.Rejoin.CanReconnect);
                Assert.Equal(ClientEndKind.SessionEnded, p.Rejoin.StartKind);
                Assert.Contains("left the city", p.Rejoin.Reason);
                Assert.Equal(1, p.Attempts, "no automatic attempt against an ended session");
            }
        }

        [Test(TimeoutSeconds = 120)]
        public void AHostThatVanishesExhaustsTheRetriesAndThenAsks()
        {
            var rig = new SoakRig(new NetSimOptions { LatencyMs = 30, Seed = 61 }) { BroadcastBytesPerFrame = 500 };
            SoakRig.Player p = rig.AddPlayer("Anna");
            Assert.True(rig.RunUntil(() => p.IsLive, 30), rig.Describe());
            // the host's game crashes: its connections time out on the player's side and it never listens again
            rig.Cut(p, hostNotices: false);
            rig.HostGone = true;
            rig.HostNet.Dispose();
            rig.Run(60);
            Assert.Equal(RejoinPhase.NeedsChoice, p.Rejoin.Phase, rig.Describe());
            Assert.True(p.Rejoin.CanReconnect, "the player may still try again by hand");
            Assert.Equal(1 + p.Rejoin.MaxAttempts, p.Attempts, rig.Describe());
            p.Rejoin.ReconnectNow(rig.Clock.Seconds);
            rig.Run(0.1);
            Assert.Equal(2 + p.Rejoin.MaxAttempts, p.Attempts, "a manual reconnect starts one attempt at once");
        }

        [Test(TimeoutSeconds = 120)]
        public void SixJoinersAtOnceWithOneCutMidDownload()
        {
            var rig = new SoakRig(Wan(81, 8 << 20), snapshotBytes: 3_000_000) { ClientOptions = Wan(82, 1 << 20), BroadcastBytesPerFrame = 2_500, BurstEvery = 90 };
            for (int i = 0; i < 6; i++) rig.AddPlayer("P" + i);
            SoakRig.Player victim = rig.Players[3];
            Assert.True(rig.RunUntil(() => victim.Client != null && victim.Client.State == ClientState.Downloading && victim.Client.JoinProgress > 0.2, 60), rig.Describe());
            rig.Cut(victim, hostNotices: true);
            Assert.True(rig.RunUntil(() => rig.LiveCount() == 5, 120), rig.Describe());
            Assert.Null(victim.Client);
            rig.Connect(victim);
            Assert.True(rig.RunUntil(() => rig.LiveCount() == 6, 120), rig.Describe());
            rig.Run(10);
            Assert.Equal(6, rig.Host.LivePeerCount);
            Assert.False(rig.Host.HoldSimulation);
            foreach (SoakRig.Player p in rig.Players) Assert.Equal(0, p.Gaps, p.Name);
            Assert.Equal(0, rig.ProposalOrderErrors);
            rig.Run(2);
            Assert.Equal(rig.Snapshots.Requests, rig.Snapshots.Released.Count, "every snapshot file was released");
            rig.AssertNoErrors();
        }

        [Test(TimeoutSeconds = 120)]
        public void TheStreamStaysGaplessWhenTheTransportKeepsRefusingSends()
        {
            var rig = new SoakRig(Wan(91), snapshotBytes: 800_000) { ClientOptions = Wan(92), BroadcastBytesPerFrame = 4_000, BurstEvery = 60 };
            rig.HostNet.BusyRate = 0.3; // Steam's k_EResultLimitExceeded, at random
            rig.AddPlayer("Anna");
            rig.AddPlayer("Ben");
            Assert.True(rig.RunUntil(() => rig.LiveCount() == 2, 90), rig.Describe());
            foreach (SoakRig.Player p in rig.Players) p.Net.BusyRate = 0.3;
            rig.Run(30);
            Assert.Equal(2, rig.LiveCount(), rig.Describe());
            foreach (SoakRig.Player p in rig.Players)
            {
                Assert.Equal(0, p.Gaps, p.Name);
                Assert.True(rig.BroadcastSeq - p.LastSeq < 200, p.Name + " lags " + (rig.BroadcastSeq - p.LastSeq));
            }
            Assert.Equal(0, rig.ProposalOrderErrors);
            Assert.True(rig.HostNet.Counters.BusyRateRefusals > 100);
            rig.AssertNoErrors();
        }

        [Test(TimeoutSeconds = 120)]
        public void JoinChurnLeavesNoStalePeersHeldSimulationOrSnapshotFiles()
        {
            var rig = new SoakRig(Wan(101, 4 << 20), snapshotBytes: 600_000) { ClientOptions = Wan(102, 1 << 20), BroadcastBytesPerFrame = 1_500 };
            SoakRig.Player stay = rig.AddPlayer("Stay");
            Assert.True(rig.RunUntil(() => stay.IsLive, 60), rig.Describe());
            SoakRig.Player churn = rig.AddPlayer("Churn");
            var r = new DeterministicRandom(7);
            for (int i = 0; i < 12; i++)
            {
                // leave or get cut at a random point of the join (or after it), then come back
                rig.Run(0.2 + r.NextDouble() * 4);
                if (churn.Client != null)
                {
                    if (i % 3 == 0) rig.Cut(churn, hostNotices: true);
                    else churn.Client.Leave();
                }
                rig.Run(0.5);
                if (churn.Client == null && !churn.Rejoin.Recovering) rig.Connect(churn);
                else if (churn.Rejoin.Recovering) churn.Rejoin.ReconnectNow(rig.Clock.Seconds);
            }
            Assert.True(rig.RunUntil(() => churn.IsLive && stay.IsLive, 90), rig.Describe());
            rig.Run(5);
            Assert.Equal(2, rig.Host.LivePeerCount, rig.Describe());
            Assert.False(rig.Host.HoldSimulation, "no joiner holds the host's simulation any more");
            Assert.Equal(0, stay.Gaps);
            Assert.Equal(0, churn.Gaps);
            Assert.True(stay.LiveCount == 1, "the player who stayed was never disturbed");
            rig.Run(2);
            Assert.Equal(rig.Snapshots.Requests, rig.Snapshots.Released.Count, "every snapshot file was released");
            rig.AssertNoErrors();
        }

        [Test(TimeoutSeconds = 120)]
        public void AJoinerThatVanishesWhileTheHostSavesReleasesTheSnapshot()
        {
            var rig = new SoakRig(new NetSimOptions { LatencyMs = 30, Seed = 111 }) { BroadcastBytesPerFrame = 500 };
            rig.Snapshots.Hold = true; // the host is still saving the city
            SoakRig.Player p = rig.AddPlayer("Anna");
            Assert.True(rig.RunUntil(() => p.Client != null && p.Client.State == ClientState.WaitingForSnapshot, 30), rig.Describe());
            Assert.True(rig.Host.HoldSimulation);
            rig.Cut(p, hostNotices: true);
            rig.Run(1);
            Assert.False(rig.Host.HoldSimulation);
            Assert.Equal(1, rig.Snapshots.Requests);
            // the save finishes later: nobody waits for it, so it is released at once
            rig.Snapshots.Hold = false;
            rig.Snapshots.DeliverPending();
            rig.Run(1);
            Assert.Equal(rig.Snapshots.Requests, rig.Snapshots.Released.Count, "the late snapshot was released");
            rig.Connect(p);
            Assert.True(rig.RunUntil(() => p.IsLive, 60), rig.Describe());
            rig.AssertNoErrors();
        }

        [Test(TimeoutSeconds = 300)]
        public void SeedSweepOfCutsAndRejoinsOnAHarshLink()
        {
            var failures = new List<string>();
            for (int seed = 200; seed < 216; seed++)
            {
                var harsh = new NetSimOptions { LatencyMs = 90, JitterMs = 60, LossRate = 0.08, ReorderRate = 0.2, DuplicateRate = 0.05, BandwidthBytesPerSecond = 3 << 20, Seed = seed };
                var rig = new SoakRig(harsh, snapshotBytes: 900_000, seed: seed) { ClientOptions = harsh.Clone(), BroadcastBytesPerFrame = 1_200 + seed * 10, BurstEvery = 97 };
                rig.ClientOptions.Seed = seed + 1000;
                rig.ClientOptions.BandwidthBytesPerSecond = 512 << 10;
                SoakRig.Player a = rig.AddPlayer("A");
                SoakRig.Player b = rig.AddPlayer("B");
                SoakRig.Player c = rig.AddPlayer("C");
                var r = new DeterministicRandom((ulong)seed);
                bool ok = rig.RunUntil(() => rig.LiveCount() == 3, 120);
                for (int i = 0; ok && i < 4; i++)
                {
                    rig.Run(r.NextDouble() * 6);
                    SoakRig.Player victim = rig.Players[r.Next(0, 3)];
                    rig.Cut(victim, hostNotices: r.NextBool());
                    rig.Run(0.5); // the cut is noticed at the next polls
                    ok = rig.RunUntil(() => rig.LiveCount() == 3, 150);
                }
                rig.Run(3);
                string problem = null;
                if (!ok || rig.LiveCount() != 3) problem = "not all live";
                else if (rig.Host.LivePeerCount != 3) problem = "host has " + rig.Host.LivePeerCount + " live peers";
                else if (rig.Host.HoldSimulation) problem = "host still holds the simulation";
                else if (a.Gaps + b.Gaps + c.Gaps != 0) problem = "gaps";
                else if (rig.ProposalOrderErrors != 0) problem = "proposal order";
                else if (rig.Log.Count(LogLevel.Error) != 0) problem = "errors logged";
                if (problem != null) failures.Add("seed " + seed + ": " + problem + " / " + rig.Describe());
            }
            Assert.Equal(0, failures.Count, string.Join(Environment.NewLine, failures));
        }

        [Test(TimeoutSeconds = 120)]
        public void PresenceSurvivesLossReorderingAndDuplication()
        {
            var opts = new NetSimOptions { LatencyMs = 50, JitterMs = 50, LossRate = 0.2, ReorderRate = 0.3, DuplicateRate = 0.2, Seed = 71 };
            var rig = new SoakRig(opts) { ClientOptions = opts.Clone(), BroadcastBytesPerFrame = 200 };
            int relayed = 0;
            rig.Host.PresenceReceived += m => relayed++;
            rig.AddPlayer("Anna", proposals: false);
            rig.AddPlayer("Ben", proposals: false);
            Assert.True(rig.RunUntil(() => rig.LiveCount() == 2, 60), rig.Describe());
            rig.Run(20);
            Assert.True(relayed > 100, "presence reached the host despite the loss: " + relayed);
            Assert.True(rig.HostNet.Counters.UnreliableLost > 0 || rig.Players[0].Net.Counters.UnreliableLost > 0, "the link did lose unreliable messages");
            Assert.Equal(2, rig.LiveCount());
            rig.AssertNoErrors();
        }
    }
}
