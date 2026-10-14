using System;
using System.Collections.Generic;
using System.Linq;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// A deterministic multi-peer simulation: a host and up to <see cref="MaxClients"/> clients over
    /// <see cref="NetSimTransport"/> (latency, jitter, loss, reordering and duplication on the unreliable lane), driven
    /// for thousands of frames by a seeded random script of joins, wrong passwords, leaves, aborts, cut links, kicks,
    /// role changes, chat (with hostile text), presence and speed changes. Invariants are checked every frame; after
    /// the script stops and the network settles, every peer must agree on the session state.
    /// </summary>
    internal sealed class SessionSim
    {
        public const int MaxClients = 6;
        public const double Frame = 1 / 60.0;

        public readonly ManualClock Clock = new ManualClock(500);
        public readonly ListLog Log = new ListLog(LogLevel.Info);
        public readonly FakeInfo Info = new FakeInfo();
        public readonly FakeSnapshots Snapshots = new FakeSnapshots(150_000);
        public readonly LoopbackTransport HostLoop;
        public readonly SessionHost Host;
        public readonly Gen G;
        public readonly List<(PeerId From, string Text, bool IsSystem)> HostChat = new List<(PeerId, string, bool)>();
        public readonly List<Member> Members = new List<Member>();
        public readonly List<string> Violations = new List<string>();
        public readonly string Password;
        private readonly NetSimOptions _net;
        private int _netSeed = 100;
        private int _nextSteam;
        public int Frames;

        public sealed class Member
        {
            public string Name;
            public ulong SteamId;
            public SessionClient Client;
            public LoopbackTransport Loop;
            public FakeLoader Loader;
            public bool WrongPassword;
            public bool EverWelcomed;
            public readonly List<(PeerId From, string Text, bool IsSystem)> Chat = new List<(PeerId, string, bool)>();
            public readonly PresenceTable Presence = new PresenceTable { StaleSeconds = 1e9 };
            public float Cursor;
            public int Connections;
            public readonly List<ClientEndKind> Ends = new List<ClientEndKind>();

            public bool Live => Client != null && Client.State == ClientState.Live;
        }

        public SessionSim(ulong seed, NetSimOptions net, string password)
        {
            G = new Gen(seed);
            _net = net;
            Password = password;
            HostLoop = LoopbackTransport.CreatePair().host;
            var hostNet = new NetSimTransport(HostLoop, Clone(net), Clock);
            Host = new SessionHost(hostNet, Info, Snapshots, new HostOptions { HostName = "Host", MaxPlayers = MaxClients + 1, Password = password, HandshakeTimeoutSeconds = 10 }, Clock, Log);
            Host.ChatReceived += e => HostChat.Add((e.From, e.Text, e.IsSystem));
            // the game layer applies a permitted speed request at once
            Host.SpeedRequested += (peer, s) => Host.Speed = new SpeedState { Speed = s.Speed, Paused = s.Paused };
            Host.Start();
        }

        private NetSimOptions Clone(NetSimOptions o)
        {
            NetSimOptions c = o.Clone();
            c.Seed = _netSeed++;
            return c;
        }

        public Member AddMember()
        {
            var m = new Member { Name = "P" + Members.Count, SteamId = 76561190000001000UL + (ulong)_nextSteam++ };
            Members.Add(m);
            Connect(m);
            return m;
        }

        public void Connect(Member m)
        {
            m.Connections++;
            m.EverWelcomed = false;
            m.Chat.Clear();
            m.Presence.Clear();
            m.WrongPassword = Password.Length > 0 && G.OneIn(5);
            m.Loop = HostLoop.CreateClient(new PeerIdentity(m.SteamId, "loop:" + m.Name + "#" + m.Connections, m.Name));
            var net = new NetSimTransport(m.Loop, Clone(_net), Clock);
            m.Loader = new FakeLoader { Defer = G.OneIn(3) };
            var client = new SessionClient(net, Info, m.Loader, new ClientOptions { PlayerName = m.Name, SteamId = m.SteamId, Password = m.WrongPassword ? Password + "?" : Password }, Clock, Log);
            client.StateChanged += () =>
            {
                if (client.State >= ClientState.WaitingForSnapshot && client.State != ClientState.Ended) m.EverWelcomed = true;
            };
            client.ChatReceived += e => m.Chat.Add((e.From, e.Text, e.IsSystem));
            client.PresenceReceived += p => m.Presence.Update(p, Clock.Seconds, client.LocalPeer);
            m.Client = client;
            client.Connect(new Endpoint());
        }

        /// <summary>One frame: the random script acts (when <paramref name="script"/>), then every peer ticks.</summary>
        public void Step(bool script)
        {
            Frames++;
            Clock.Advance(Frame);
            if (script) Act();
            Host.Tick();
            foreach (Member m in Members)
            {
                if (m.Client == null) continue;
                if (m.Client.State == ClientState.Loading && m.Loader.PendingDone != null && G.OneIn(40)) m.Loader.Finish();
                m.Client.Tick();
                if (m.Live && (ulong)Frames % 6 == m.SteamId % 6) m.Client.SendPresence(new PresenceMessage { CursorX = m.Cursor, CursorY = m.Connections, CursorZ = 1 });
                if (m.Client.State == ClientState.Ended) Retire(m);
            }
            CheckInvariants();
        }

        private void Retire(Member m)
        {
            SessionClient c = m.Client;
            m.Ends.Add(c.EndKind);
            if (c.EndKind == ClientEndKind.Refused && m.EverWelcomed)
            {
                Violations.Add(m.Name + " was refused after it had been admitted (" + c.EndReason + ")");
            }
            if (m.WrongPassword && m.EverWelcomed) Violations.Add(m.Name + " was admitted with a wrong password");
            CheckChatSlice(m, "ended connection " + m.Connections);
            c.Dispose();
            m.Client = null;
        }

        private void Act()
        {
            int roll = G.Int(1000);
            var live = Members.Where(x => x.Live).ToList();
            if (roll < 6 && Members.Count < MaxClients)
            {
                AddMember();
            }
            else if (roll < 14)
            {
                // someone whose session ended comes back
                Member gone = Members.FirstOrDefault(x => x.Client == null);
                if (gone != null) Connect(gone);
            }
            else if (roll < 17 && live.Count > 0)
            {
                G.Element(live).Client.Leave();
            }
            else if (roll < 19 && live.Count > 0)
            {
                G.Element(live).Client.Abort("simulated fault");
            }
            else if (roll < 22)
            {
                // a link fails (both ends notice, as after a transport timeout); also hits joiners mid-download
                var connected = Members.Where(x => x.Client != null).ToList();
                if (connected.Count > 0)
                {
                    Member m = G.Element(connected);
                    foreach (IConnection c in m.Loop.GetConnections()) m.Loop.Sever(c, notifyThisSide: true, notifyOtherSide: true);
                }
            }
            else if (roll < 24 && live.Count > 0)
            {
                Host.Kick(G.Element(live).Client.LocalPeer, "simulated kick");
            }
            else if (roll < 34)
            {
                var ids = new List<PeerId>();
                Host.GetAdmittedPeers(ids);
                if (ids.Count > 0) Host.SetRole(G.Element(ids), G.Pick(SessionRole.Builder, SessionRole.CoHost, SessionRole.Viewer));
            }
            else if (roll < 60 && live.Count > 0)
            {
                G.Element(live).Client.SendChat(G.String(60, hostile: true));
            }
            else if (roll < 66)
            {
                Host.SendChat("host says " + Frames);
            }
            else if (roll < 70)
            {
                Host.Speed = new SpeedState { Speed = G.Int(1, 4), Paused = G.OneIn(3) };
            }
            else if (roll < 74 && live.Count > 0)
            {
                G.Element(live).Client.RequestSpeed(new SpeedState { Speed = G.Int(1, 4), Paused = G.OneIn(4) });
            }
            else if (roll < 200 && live.Count > 0)
            {
                Member mover = G.Element(live);
                mover.Cursor = G.Int(1, 100000);
            }
        }

        private void CheckInvariants()
        {
            var admitted = new List<PeerId>();
            Host.GetAdmittedPeers(admitted);
            if (admitted.Count != admitted.Distinct().Count()) Violations.Add("frame " + Frames + ": duplicate admitted peer ids " + string.Join(",", admitted));
            if (admitted.Contains(PeerId.Host)) Violations.Add("frame " + Frames + ": a peer was admitted with the host's id");
            if (admitted.Count > MaxClients) Violations.Add("frame " + Frames + ": " + admitted.Count + " admitted peers, more than the session holds");
            IReadOnlyList<PlayerInfo> players = Host.GetPlayers();
            if (players.Select(p => p.Id).Distinct().Count() != players.Count) Violations.Add("frame " + Frames + ": duplicate ids in the player list");
        }

        /// <summary>
        /// A connection's chat log is a contiguous slice of the host's: the reliable ordered lane delivers every line
        /// distributed while it was admitted, in the host's order, with the same text.
        /// </summary>
        public void CheckChatSlice(Member m, string what)
        {
            if (m.Chat.Count == 0) return;
            int start = HostChat.FindIndex(e => e.Equals(m.Chat[0]));
            while (start >= 0)
            {
                bool ok = start + m.Chat.Count <= HostChat.Count;
                for (int i = 0; ok && i < m.Chat.Count; i++) ok = HostChat[start + i].Equals(m.Chat[i]);
                if (ok) return;
                start = HostChat.FindIndex(start + 1, e => e.Equals(m.Chat[0]));
            }
            Violations.Add(m.Name + " (" + what + "): its " + m.Chat.Count + " chat lines are not a contiguous slice of the host's " + HostChat.Count
                           + "; first \"" + m.Chat[0].Text + "\"");
        }

        public string Describe()
        {
            return string.Join("; ", Members.Select(m => m.Name + "=" + (m.Client?.State.ToString() ?? "gone") + " conns=" + m.Connections + " ends=[" + string.Join(",", m.Ends) + "]"))
                   + "; host live=" + Host.LivePeerCount + " hold=" + Host.HoldSimulation;
        }
    }

    public sealed class SessionSimulationTests
    {
        private static NetSimOptions Wan() => new NetSimOptions { LatencyMs = 40, JitterMs = 20, LossRate = 0.03, ReorderRate = 0.1, DuplicateRate = 0.03, BandwidthBytesPerSecond = 4 << 20 };

        private static void RunAndCheck(ulong seed, string password, int frames)
        {
            var sim = new SessionSim(seed, Wan(), password);
            for (int i = 0; i < 3; i++) sim.AddMember();
            for (int f = 0; f < frames; f++) sim.Step(script: true);

            // settle: no more script; joiners finish loading, everyone sends a final presence a few times
            foreach (SessionSim.Member m in sim.Members)
            {
                if (m.Loader?.PendingDone != null) m.Loader.Defer = false;
            }
            for (int f = 0; f < 60 * 25; f++)
            {
                foreach (SessionSim.Member m in sim.Members)
                {
                    if (m.Client != null && m.Client.State == ClientState.Loading && m.Loader.PendingDone != null) m.Loader.Finish();
                }
                sim.Step(script: false);
            }
            string state = sim.Describe();
            Assert.True(sim.Violations.Count == 0, "invariants: " + string.Join(" | ", sim.Violations.Take(5)) + " — " + state);

            List<SessionSim.Member> live = sim.Members.Where(m => m.Live).ToList();
            foreach (SessionSim.Member m in sim.Members)
            {
                if (!(m.Client == null || m.Live))
                {
                    Console.WriteLine("DEBUG " + m.Name + " downloaded " + m.Client.DownloadedBytes + "/" + m.Client.DownloadTotalBytes + " local " + m.Client.LocalPeer);
                    foreach (string l in sim.Host.DescribePeers()) Console.WriteLine("DEBUG host: " + l);
                    foreach (string l in sim.Log.Lines.Skip(Math.Max(0, sim.Log.Lines.Count - 30))) Console.WriteLine("DEBUG log: " + l);
                }
                Assert.True(m.Client == null || m.Live, m.Name + " settled in " + m.Client?.State + " — " + state);
            }
            // membership: the host and every live client agree on who is in the session, with which role and state
            IReadOnlyList<PlayerInfo> hostPlayers = sim.Host.GetPlayers();
            Assert.Equal(live.Count + 1, hostPlayers.Count, "host player list — " + state);
            Assert.Equal(live.Count, sim.Host.LivePeerCount);
            Assert.False(sim.Host.HoldSimulation, "nobody is joining any more");
            var ids = new HashSet<PeerId>();
            foreach (SessionSim.Member m in live)
            {
                Assert.True(ids.Add(m.Client.LocalPeer), "live clients have distinct ids");
                Assert.Equal(sim.Host.GetRole(m.Client.LocalPeer), m.Client.LocalRole, m.Name + " knows its role");
                Assert.Equal(sim.Host.Speed, m.Client.Speed, m.Name + " runs at the host's speed");
                string expected = string.Join(" ", hostPlayers.Select(p => p.Id + ":" + p.Name + ":" + p.Role + ":" + p.State));
                string actual = string.Join(" ", m.Client.Players.Select(p => p.Id + ":" + p.Name + ":" + p.Role + ":" + p.State));
                Assert.Equal(expected, actual, m.Name + "'s player list");
                sim.CheckChatSlice(m, "live connection");
                // the chat log ends with the host's: nothing distributed while it was admitted is missing at the end
                if (m.Chat.Count > 0) Assert.Equal(sim.HostChat[sim.HostChat.Count - 1], m.Chat[m.Chat.Count - 1], m.Name + " saw the last chat line");
                // presence converged: the last cursor of every other live player
                foreach (SessionSim.Member other in live)
                {
                    if (other == m) continue;
                    PresenceTable.Entry e = m.Presence.Find(other.Client.LocalPeer);
                    Assert.NotNull(e, m.Name + " sees " + other.Name);
                    Assert.Equal(other.Cursor, e.X, m.Name + " sees " + other.Name + "'s last cursor");
                }
            }
            Assert.True(sim.Violations.Count == 0, "chat: " + string.Join(" | ", sim.Violations.Take(5)));
            Assert.Equal(sim.Snapshots.Requests, sim.Snapshots.Released.Count, "every snapshot file was released");
            Assert.Equal(0, sim.Log.Count(LogLevel.Error), "errors: " + string.Join(" / ", sim.Log.Lines.Where(l => l.Contains("ERROR")).Take(3)));
            int ended = sim.Members.Sum(m => m.Ends.Count);
            Assert.True(ended > 0 && sim.HostChat.Count > 20, "the script exercised the session (" + ended + " ends, " + sim.HostChat.Count + " chat lines)");
        }

        [Test(TimeoutSeconds = 120)]
        public void PeersConvergeAfterARandomSessionOverALossyNetwork()
        {
            RunAndCheck(seed: 1001, password: "", frames: 60 * 90);
        }

        [Test(TimeoutSeconds = 120)]
        public void PeersConvergeWithAPasswordAndWrongGuesses()
        {
            RunAndCheck(seed: 2002, password: "s3cret", frames: 60 * 90);
        }

        [Test(TimeoutSeconds = 180)]
        public void SeedSweepOfShortRandomSessions()
        {
            for (ulong seed = 3000; seed < 3006; seed++) RunAndCheck(seed, seed % 2 == 0 ? "" : "pw", frames: 60 * 30);
        }
    }
}
