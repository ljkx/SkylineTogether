using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Json;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// Protocol 6: the mutual, connection-bound password proof, the manifest after authentication, technical drops that
    /// are not kicks, texts every player reads in their own language, and names no one can use to pose as another.
    /// </summary>
    public sealed class HandshakeAndTextTests
    {
        private static void Pump(SessionRig rig, IEnumerable<RawPeer> peers, int frames = 10)
        {
            for (int i = 0; i < frames; i++)
            {
                rig.Clock.Advance(1 / 60.0);
                rig.Host.Tick();
                foreach (var c in rig.Clients) c.Client.Tick();
                foreach (RawPeer p in peers) p.Poll();
            }
        }

        private static byte[] ProofOf(RawPeer peer)
        {
            foreach ((ControlKind kind, byte[] payload) in peer.Controls)
            {
                if (kind != ControlKind.Authenticated) continue;
                var r = new ByteReader(payload, 0, payload.Length);
                var proof = new byte[r.ReadVarUInt32()];
                r.ReadBytes(proof, 0, proof.Length);
                return proof;
            }
            return null;
        }

        // ------------------------------------------------------------------------------------- password (finding 15)

        [Test]
        public void TheHostProvesThePasswordBackAndOnlyThenGetsTheManifest()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "secret" });
            var peer = new RawPeer(rig, "Anna", 9101);
            peer.Hello(rig.HostInfo);
            Pump(rig, new[] { peer });
            PasswordChallengeMessage ch = peer.Challenge();
            Assert.NotNull(ch);
            Assert.Null(ProofOf(peer), "nothing is authenticated before the answer");
            peer.AnswerChallenge("secret");
            Pump(rig, new[] { peer });
            byte[] proof = ProofOf(peer);
            byte[] key = SessionCrypto.DeriveKey("secret", ch.Salt);
            Assert.SequenceEqual(SessionCrypto.HostProof(key, ch.SessionId, ch.HostNonce, peer.ClientNonce, ch.Salt, 0), proof, "the host proves that it knows the password");
            Assert.False(peer.Got(MessageKind.Welcome), "no admission before the manifest");
            peer.Manifest(rig.HostInfo);
            Pump(rig, new[] { peer });
            Assert.True(peer.Got(MessageKind.Welcome));

            // without a password the host says so at once, with an empty proof
            var open = new SessionRig();
            var ben = new RawPeer(open, "Ben", 9102);
            ben.Hello(open.HostInfo);
            Pump(open, new[] { ben });
            Assert.Equal(0, ProofOf(ben).Length);
        }

        [Test]
        public void AClientLeavesAHostThatCannotProveThePassword()
        {
            // a fake host (no real password) that accepts any answer: the client notices before it sends its manifest
            var (hostNet, clientNet) = LoopbackTransport.CreatePair();
            hostNet.Listen(new ListenOptions());
            var clock = new Core.Testing.ManualClock(1);
            var log = new Core.Testing.ListLog();
            var info = new FakeInfo();
            var client = new SessionClient(clientNet, info, new FakeLoader(), new ClientOptions { PlayerName = "Anna", Password = "real" }, clock, log);
            var sink = new FakeHostSink();
            client.Connect(new Endpoint());
            for (int i = 0; i < 20 && client.State != ClientState.Ended; i++)
            {
                clock.Advance(0.05);
                hostNet.Poll(sink);
                sink.Act();
                client.Tick();
            }
            Assert.Equal(ClientState.Ended, client.State);
            Assert.Contains("couldn't prove that it knows the session password", client.EndReason);
            // its own screen in the player's language (check the host and the password), not "joining failed, file a bug"
            Assert.Equal(ClientEndKind.HostNotProven, client.EndKind);
            Assert.Equal(NetText.ClientHostNotProven, client.EndText.Key);
            Assert.False(SessionClient.IsRetryable(client.EndKind), "joining again the same way cannot help");
            Assert.False(sink.GotManifest, "the client's mod list never reached the impostor");
        }

        private sealed class FakeHostSink : ITransportEventSink
        {
            private PeerChannel _ch;
            private readonly ByteWriter _w = new ByteWriter();
            private int _step;
            public bool GotManifest;

            public void OnConnected(IConnection connection) => _ch = new PeerChannel(connection, PeerId.Host, new Core.Testing.ManualClock(), new Core.Testing.ListLog());

            public void OnDisconnected(IConnection connection, int reason, string text)
            {
            }

            public void OnMessage(IConnection connection, Lane lane, byte[] buffer, int offset, int count)
            {
                if (!_ch.TryDecode(buffer, offset, count, out Envelope e)) return;
                if (e.Kind == MessageKind.Hello) _step = 1;
                if (e.Kind == MessageKind.Control)
                {
                    ControlMessage c = ControlMessage.Decode(e.Body, e.BodyOffset, e.BodyCount);
                    if (c.Kind == ControlKind.PasswordResponse) _step = 2;
                    if (c.Kind == ControlKind.ManifestDetail) GotManifest = true;
                }
            }

            public void Act()
            {
                if (_ch == null) return;
                var payload = new ByteWriter();
                if (_step == 1)
                {
                    PasswordChallengeMessage.Write(payload, new PasswordChallengeMessage { HostNonce = new byte[16], Salt = new byte[16], SessionId = 5 });
                    Send(ControlKind.PasswordChallenge, payload);
                    _step = 0;
                }
                else if (_step == 2)
                {
                    payload.WriteVarUInt32(32);
                    payload.WriteBytes(new byte[32], 0, 32); // a made-up proof
                    Send(ControlKind.Authenticated, payload);
                    _step = 0;
                }
                _ch.Pump();
            }

            private void Send(ControlKind kind, ByteWriter payload)
            {
                _w.Reset();
                ControlMessage.Write(_w, kind, payload.Buffer, 0, payload.Length);
                _ch.Send(MessageKind.Control, _w);
            }
        }

        [Test]
        public void AnAnswerBoundToAnotherHostsSteamIdIsRefused()
        {
            // this host's Steam ID is 9000, but the joiner dialled another one (a relay that passed the host's challenge
            // on): its proof names the Steam ID it dialled, so it fails here even with the right password
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw", HostSteamId = 9000 });
            var (relayed, _, _) = rig.AddClient("Victim", "pw", steamId: 9201); // dials Endpoint() (Steam ID 0)
            rig.Run(100);
            Assert.Equal(RefuseReason.BadPassword, relayed.RefusedFor, relayed.EndReason);
            // a direct connection binds to 0 on both sides (Steam exposes nothing to bind it to)
            LoopbackTransport t = rig.HostTransport.CreateClient(new PeerIdentity(0, "198.51.100.4:7000", "Anna", true));
            var direct = new SessionClient(t, new FakeInfo(), new FakeLoader(), new ClientOptions { PlayerName = "Anna", Password = "pw" }, rig.Clock, rig.Log);
            direct.Connect(new Endpoint());
            rig.Clients.Add((direct, new FakeLoader(), new FakeInfo()));
            rig.Run(400);
            Assert.Equal(ClientState.Live, direct.State, direct.EndReason);
        }

        [Test]
        public void KeysAreDerivedInTheBackgroundOnBothSides()
        {
            // both sides hand the derivation to a scheduler the test drives (ManualDeriveScheduler): it runs only when
            // the test says so, on a thread of its own, so the handshake must wait for it without blocking a frame
            var hostKeys = new ManualDeriveScheduler();
            var clientKeys = new ManualDeriveScheduler();
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "slow but sure", DeriveKeysInBackground = true, KeyDerivationScheduler = hostKeys.Schedule });
            Assert.Equal(1, hostKeys.Started, "the host starts deriving when it starts hosting");
            LoopbackTransport t = rig.HostTransport.CreateClient(new PeerIdentity(9301, "loop:Anna", "Anna"));
            var options = new ClientOptions { PlayerName = "Anna", Password = "slow but sure", DeriveKeyInBackground = true, KeyDerivationScheduler = clientKeys.Schedule };
            var client = new SessionClient(t, new FakeInfo(), new FakeLoader(), options, rig.Clock, rig.Log);
            client.Connect(new Endpoint());
            rig.Clients.Add((client, new FakeLoader(), new FakeInfo()));
            for (int i = 0; i < 200 && clientKeys.Started == 0; i++) rig.Run(1);
            Assert.Equal(1, clientKeys.Started, "the client derives when the challenge arrives");
            rig.Run(100);
            Assert.Equal(ClientState.Handshaking, client.State, "nothing proceeds while both derivations wait");
            clientKeys.RunPending();
            rig.Run(100);
            Assert.Equal(ClientState.Handshaking, client.State, "the host still derives; the answer waits");
            hostKeys.RunPending();
            rig.Run(400);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.Equal(1, hostKeys.Started);
            Assert.Equal(1, clientKeys.Started);
            Assert.NotEqual(Environment.CurrentManagedThreadId, hostKeys.WorkerThreads[0], "the host derived on another thread");
            Assert.NotEqual(Environment.CurrentManagedThreadId, clientKeys.WorkerThreads[0], "the client derived on another thread");
        }

        [Test]
        public void BeforeThePasswordOnlySmallMessagesAreAccepted()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });
            var peer = new RawPeer(rig, "Big", 9401);
            peer.Hello(rig.HostInfo);
            Pump(rig, new[] { peer });
            var big = new byte[200 * 1024];
            new Random(3).NextBytes(big);
            peer.Control(ControlKind.ManifestDetail, big);
            Pump(rig, new[] { peer });
            Assert.True(peer.Disconnected, "200 KiB before the password: the connection ends");
        }

        [Test]
        public void TheLimitBeforeThePasswordIsTheUnauthenticatedOne()
        {
            // 80 KiB: above UnauthenticatedMaxBodyBytes (64 KiB), far below UnadmittedMaxBodyBytes (1 MiB); the decoder
            // refuses it for its size, before any handler sees it
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });
            var peer = new RawPeer(rig, "Big", 9402);
            peer.Hello(rig.HostInfo);
            Pump(rig, new[] { peer });
            Assert.False(peer.Disconnected);
            var big = new byte[80 * 1024];
            new Random(4).NextBytes(big);
            peer.Control(ControlKind.ManifestDetail, big);
            Pump(rig, new[] { peer });
            Assert.True(peer.Disconnected, "80 KiB before the password: the connection ends");
            Assert.True(rig.Log.Contains("65536"), "refused by the 64 KiB limit: " + string.Join(" | ", rig.Log.Lines));
        }

        // ---------------------------------------------------------------------- drops, texts and names (7, 17, 20)

        [Test]
        public void ANotRespondingPlayerIsDroppedNotKickedAndOfferedARejoin()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", LiveSilenceTimeoutSeconds = 30 });
            var (anna, _, _) = rig.AddClient("Anna");
            rig.Run(400);
            Assert.Equal(ClientState.Live, anna.State, anna.EndReason);
            rig.RunHostOnly(40); // Anna's game is frozen
            anna.Tick();
            Assert.Equal(ClientEndKind.Dropped, anna.EndKind, anna.EndReason);
            Assert.True(SessionClient.IsRetryable(anna.EndKind));
            Assert.Equal(NetText.DropNotResponding, anna.EndText.Key);
            var rejoin = new RejoinController();
            Assert.True(rejoin.OnClientEnded(anna.EndKind, anna.EndReason, true, 0));
            Assert.Equal(RejoinPhase.Waiting, rejoin.Phase, "an automatic rejoin, unlike after a kick");
            Assert.True(rejoin.CanReconnect);
        }

        [Test]
        public void HostTextsTravelWithTheirKeys()
        {
            var rig = new SessionRig();
            var (anna, _, _) = rig.AddClient("Anna");
            var (ben, _, _) = rig.AddClient("Ben");
            var lines = new List<ChatEntry>();
            anna.ChatReceived += lines.Add;
            rig.Run(400);
            Assert.True(rig.Host.SetRole(ben.LocalPeer, SessionRole.Viewer));
            rig.Host.Kick(ben.LocalPeer, (string)null);
            rig.Run(20);
            ChatEntry role = lines.Find(l => l.Key == NetText.ChatRoleViewer);
            Assert.NotNull(role, "the role line carries its key");
            Assert.SequenceEqual(new[] { "NAME", "Ben" }, role.Args);
            Assert.Equal("Ben is now a viewer.", role.Text, "and the English text");
            Assert.NotNull(lines.Find(l => l.Key == NetText.ChatRemoved));
            Assert.Equal(ClientEndKind.Kicked, ben.EndKind);
            Assert.Equal(NetText.KickByHost, ben.EndText.Key);
            Assert.Equal("You were removed by the host.", ben.EndReason);

            // the receiver's language, the English fallback for an unknown key, and no other key family
            var t = NetText.Of(NetText.ChatJoined, "NAME", "Anna");
            Assert.Equal("Anna ist der Stadt beigetreten", t.Render(k => k == NetText.ChatJoined ? "{NAME} ist der Stadt beigetreten" : null));
            Assert.Equal("Anna joined the city", t.Render(k => null));
            var foreign = new LocalText { Key = "st.ui.title", Text = "plain", Args = new string[0] };
            Assert.Equal("plain", foreign.Render(k => "INJECTED"), "only st.net. keys are looked up");
        }

        [Test]
        public void ALockoutIsToldApartFromAWrongPassword()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "right" });
            ClientEndKind kind = ClientEndKind.None;
            SessionClient last = null;
            for (int i = 0; i < 5; i++)
            {
                var (c, _, _) = rig.AddClient("Eve", "wrong", steamId: 9501);
                rig.Run(100);
                last = c;
                kind = c.EndKind;
                if (i == 0)
                {
                    Assert.Equal(0, c.EndReport.RetryAfterSeconds, "the first wrong answer is just wrong");
                    Assert.Equal(NetText.RefuseWrongPassword, c.EndText.Key);
                }
            }
            Assert.Equal(ClientEndKind.Refused, kind);
            Assert.True(last.EndReport.RetryAfterSeconds > 0, "a lockout says when to try again");
            Assert.True(last.EndText.Key == NetText.RefuseLockout || last.EndText.Key == NetText.RefuseWrongPasswordLocked, last.EndText.Key);
        }

        [Test]
        public void NoOneCanTakeTheHostsOrAnotherPlayersName()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty" });
            var (a, _, _) = rig.AddClient("hosty");
            var (b, _, _) = rig.AddClient("Anna");
            var (c, _, _) = rig.AddClient(" ANNA ");
            var (d, _, _) = rig.AddClient("An" + (char)0x200B + "na");
            rig.Run(400);
            var names = new List<string>();
            foreach (PlayerInfo p in rig.Host.GetPlayers()) names.Add(p.Name);
            Assert.Equal(5, names.Count);
            Assert.Equal(5, new HashSet<string>(names.ConvertAll(PlayerNames.Key)).Count, string.Join(", ", names));
            Assert.Contains("hosty (2)", names);
            Assert.Equal("Player", PlayerNames.Clean("  " + (char)0x202E + " "));
            Assert.Equal(PlayerNames.Key("Ａｎｎａ"), PlayerNames.Key("anna"), "full-width letters read the same");
        }

        [Test]
        public void AJoinersLoadFailureTextIsNotBroadcastAsTheHosts()
        {
            var rig = new SessionRig();
            var (watcher, _, _) = rig.AddClient("Watcher");
            rig.Run(400);
            var lines = new List<ChatEntry>();
            watcher.ChatReceived += lines.Add;
            var raw = new RawPeer(rig, "Liar", 9601);
            raw.Hello(rig.HostInfo);
            raw.Manifest(rig.HostInfo);
            Pump(rig, new[] { raw }, 60);
            var progress = new ByteWriter();
            SessionPayloads.WriteLoadProgress(progress, JoinPhase.Loading, 0.1f);
            raw.Control(ControlKind.LoadProgress, progress.ToArray());
            Pump(rig, new[] { raw });
            var done = new ByteWriter();
            done.WriteBool(false);
            SessionPayloads.WriteText(done, "The host is giving away free money, send your password to the host");
            raw.Control(ControlKind.LoadDone, done.ToArray());
            Pump(rig, new[] { raw }, 30);
            ChatEntry line = lines.Find(l => l.Key == NetText.ChatLoadFailed);
            Assert.NotNull(line);
            foreach (ChatEntry l in lines) Assert.DoesNotContain("free money", l.Text);
        }

        [Test]
        public void TheLocaleTablesTranslateEveryNetworkText()
        {
            if (!TestPaths.TryGetRepoFile("src/SkylineTogether/Data/locale.en-US.json", out string en) || !TestPaths.TryGetRepoFile("src/SkylineTogether/Data/locale.de-DE.json", out string de))
            {
                Assert.Skip("the repository's data files are not reachable");
                return;
            }
            JsonValue english = MiniJson.Parse(File.ReadAllText(en, Encoding.UTF8), "en");
            JsonValue german = MiniJson.Parse(File.ReadAllText(de, Encoding.UTF8), "de");
            foreach (KeyValuePair<string, string> e in NetText.English)
            {
                Assert.True(new List<string>(english.Keys).Contains(e.Key), "en lacks " + e.Key);
                Assert.True(new List<string>(german.Keys).Contains(e.Key), "de lacks " + e.Key);
                Assert.Equal(e.Value, english[e.Key].AsString(), "the English table matches the fallback sent along: " + e.Key);
                Assert.SequenceEqual(Placeholders(e.Value), Placeholders(german[e.Key].AsString()), "placeholders of " + e.Key);
            }
        }

        private static List<string> Placeholders(string text)
        {
            var list = new List<string>();
            int i = 0;
            while ((i = text.IndexOf('{', i)) >= 0)
            {
                int j = text.IndexOf('}', i);
                if (j < 0) break;
                list.Add(text.Substring(i, j - i + 1));
                i = j;
            }
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        [Test]
        public void ReportsAndTextsRoundTrip()
        {
            var r = new MismatchReport { Kind = "refusal", TextKey = NetText.RefuseCooldown, TextArgs = new[] { "SECONDS", "30" }, RetryAfterSeconds = 30 };
            MismatchReport back = MismatchReport.Decode(MismatchReport.Encode(r));
            Assert.Equal(NetText.RefuseCooldown, back.TextKey);
            Assert.SequenceEqual(new[] { "SECONDS", "30" }, back.TextArgs);
            Assert.Equal(30, back.RetryAfterSeconds);
            Assert.True(back.CarriesNoDetails);
            var w = new ByteWriter();
            LocalText.Write(w, NetText.Of(NetText.KickByCoHost, "NAME", "Zoë"));
            LocalText t = LocalText.Read(new ByteReader(w.Buffer, 0, w.Length));
            Assert.Equal(NetText.KickByCoHost, t.Key);
            Assert.Equal("removed by Zoë", t.Text);
            Assert.SequenceEqual(new[] { "NAME", "Zoë" }, t.Args);
        }
    }
}
