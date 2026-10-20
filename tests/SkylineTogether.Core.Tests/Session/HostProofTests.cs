using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// A fake host that skips the password challenge (R2 N2): on a direct connection (whose real host always challenges)
    /// or for a session known to have a password, the client ends the join before its manifest leaves, and nothing the
    /// impostor sends before the authentication (welcome, city, player list) is read.
    /// </summary>
    public sealed class HostProofTests
    {
        /// <summary>The loopback transport, reporting another kind (a direct connection in these tests).</summary>
        private sealed class KindTransport : ITransport
        {
            private readonly ITransport _inner;

            public KindTransport(ITransport inner, TransportKind kind)
            {
                _inner = inner;
                Kind = kind;
            }

            public TransportKind Kind { get; }

            public void Listen(ListenOptions options) => _inner.Listen(options);

            public IConnection Connect(Endpoint endpoint) => _inner.Connect(endpoint);

            public void Poll(ITransportEventSink sink) => _inner.Poll(sink);

            public void Dispose() => _inner.Dispose();
        }

        /// <summary>What the impostor does after the hello.</summary>
        private enum Script
        {
            /// <summary>"Authenticated" with an empty proof, no challenge.</summary>
            SkipChallenge,
            /// <summary>Straight to the welcome, the city and a player list.</summary>
            StraightToTheCity,
        }

        private sealed class Impostor : ITransportEventSink
        {
            private readonly Script _script;
            private PeerChannel _ch;
            private readonly ByteWriter _w = new ByteWriter();
            private bool _hello;
            public bool GotManifest;

            public Impostor(Script script)
            {
                _script = script;
            }

            public void OnConnected(IConnection connection) => _ch = new PeerChannel(connection, PeerId.Host, new Core.Testing.ManualClock(), new Core.Testing.ListLog());

            public void OnDisconnected(IConnection connection, int reason, string text)
            {
            }

            public void OnMessage(IConnection connection, Lane lane, byte[] buffer, int offset, int count)
            {
                if (!_ch.TryDecode(buffer, offset, count, out Envelope e)) return;
                if (e.Kind == MessageKind.Hello) _hello = true;
                if (e.Kind == MessageKind.Control && ControlMessage.Decode(e.Body, e.BodyOffset, e.BodyCount).Kind == ControlKind.ManifestDetail) GotManifest = true;
            }

            public void Act()
            {
                if (_ch == null || !_hello) return;
                _hello = false;
                var payload = new ByteWriter();
                if (_script == Script.SkipChallenge)
                {
                    payload.WriteVarUInt32(0);
                    Control(ControlKind.Authenticated, payload);
                }
                else
                {
                    _w.Reset();
                    WelcomeMessage.Write(_w, new WelcomeMessage { AssignedPeer = new PeerId(2), Role = SessionRole.Builder, SessionSettings = new byte[0], HostNonce = new byte[16] });
                    _ch.Send(MessageKind.Welcome, _w);
                    SnapshotManifestCodec.Write(payload, new SnapshotManifest { SnapshotId = 1, Size = 1000, ChunkSize = 1000, GameModeName = "" });
                    Control(ControlKind.SnapshotManifest, payload);
                    payload.Reset();
                    SessionPayloads.WritePlayers(payload, new[] { new PlayerInfo { Id = new PeerId(1), Name = "Evil", State = PlayerState.Live } });
                    Control(ControlKind.Players, payload);
                }
                _ch.Pump();
            }

            private void Control(ControlKind kind, ByteWriter payload)
            {
                _w.Reset();
                ControlMessage.Write(_w, kind, payload.Buffer, 0, payload.Length);
                _ch.Send(MessageKind.Control, _w);
            }
        }

        private static (SessionClient Client, Impostor Host) Join(Script script, TransportKind kind, string password, bool expectPassword = false, int frames = 40)
        {
            var (hostNet, clientNet) = LoopbackTransport.CreatePair();
            hostNet.Listen(new ListenOptions());
            var clock = new Core.Testing.ManualClock(1);
            var log = new Core.Testing.ListLog();
            var client = new SessionClient(new KindTransport(clientNet, kind), new FakeInfo(), new FakeLoader(),
                new ClientOptions { PlayerName = "Anna", Password = password, ExpectPassword = expectPassword }, clock, log);
            var impostor = new Impostor(script);
            client.Connect(new Endpoint());
            for (int i = 0; i < frames && client.State != ClientState.Ended; i++)
            {
                clock.Advance(0.05);
                hostNet.Poll(impostor);
                impostor.Act();
                client.Tick();
            }
            return (client, impostor);
        }

        [Test]
        public void ADirectHostThatSkipsTheChallengeIsLeftBeforeTheManifest()
        {
            foreach (string password in new[] { "real", "" })
            {
                var (client, impostor) = Join(Script.SkipChallenge, TransportKind.SteamIp, password);
                Assert.Equal(ClientState.Ended, client.State, "password '" + password + "'");
                Assert.Equal(ClientEndKind.HostNotProven, client.EndKind, "its own screen: check the host and the password");
                Assert.Contains("couldn't prove that it knows the session password", client.EndReason);
                Assert.False(impostor.GotManifest, "the mod list never reached the impostor");
            }
        }

        [Test]
        public void ASessionKnownToHaveAPasswordMustChallengeOverSteamToo()
        {
            var (client, impostor) = Join(Script.SkipChallenge, TransportKind.SteamP2P, "real", expectPassword: true);
            Assert.Equal(ClientEndKind.HostNotProven, client.EndKind, client.EndReason);
            Assert.False(impostor.GotManifest);
        }

        [Test]
        public void ASteamHostWithoutAPasswordStillAdmitsAJoinerWithAKeptPassword()
        {
            // the dialled Steam account is the host (Steam authenticates it); a password left in the join settings must
            // not stop joins of a session without one
            var (client, impostor) = Join(Script.SkipChallenge, TransportKind.SteamP2P, "left over");
            Assert.Equal(ClientState.Handshaking, client.State, client.EndReason);
            Assert.True(impostor.GotManifest, "authenticated: the compatibility check goes on");
        }

        [Test]
        public void NothingBeforeTheAuthenticationIsRead()
        {
            foreach (TransportKind kind in new[] { TransportKind.SteamIp, TransportKind.SteamP2P })
            {
                var (client, impostor) = Join(Script.StraightToTheCity, kind, "real");
                Assert.Equal(ClientState.Handshaking, client.State, kind + ": no welcome, no download");
                Assert.Equal(0, client.Players.Count, kind + ": no player list");
                Assert.False(impostor.GotManifest);
            }
        }

        [Test]
        public void TheRealHandshakeStillWorksDirectAndOverSteam()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });
            var (client, _, _) = rig.AddClient("Anna", "pw");
            rig.Run(300);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            var open = new SessionRig();
            var (ben, _, _) = open.AddClient("Ben");
            open.Run(300);
            Assert.Equal(ClientState.Live, ben.State, ben.EndReason);
        }
    }
}
