using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// A hand-driven peer on the host's loopback network: it sends exactly the messages a test scripts (in any order,
    /// malformed, early, twice) and records what the host answers. Used to pin the host's handshake state machine
    /// against clients that do not behave like <see cref="SessionClient"/>.
    /// </summary>
    internal sealed class RawPeer : ITransportEventSink
    {
        public readonly LoopbackTransport Transport;
        public readonly List<(MessageKind Kind, byte[] Body)> Received = new List<(MessageKind, byte[])>();
        public readonly List<(ControlKind Kind, byte[] Payload)> Controls = new List<(ControlKind, byte[])>();
        public PeerChannel Channel;
        public bool Disconnected;
        private readonly ByteWriter _w = new ByteWriter();

        public RawPeer(SessionRig rig, string name, ulong steamId)
            : this(rig, new PeerIdentity(steamId, "loop:" + name, name), steamId)
        {
        }

        /// <summary>
        /// A peer with the given transport identity (e.g. a direct connection without Steam identity),
        /// whose hello claims <paramref name="claimedSteamId"/>.
        /// </summary>
        public RawPeer(SessionRig rig, PeerIdentity identity, ulong claimedSteamId)
        {
            Transport = rig.HostTransport.CreateClient(identity);
            Name = identity.DisplayName ?? "raw";
            SteamId = claimedSteamId;
            IConnection c = Transport.Connect(new Endpoint());
            Channel = new PeerChannel(c, PeerId.Host, rig.Clock, rig.Log);
        }

        public string Name { get; }

        public ulong SteamId { get; }

        public byte[] ClientNonce = SessionCrypto.RandomBytes(16);

        public void Hello(FakeInfo info, ushort? protocol = null, byte joinFlags = 0)
        {
            HandshakeDigests d = info.GetDigests();
            if (protocol.HasValue) d.ProtocolVersion = protocol.Value;
            _w.Reset();
            HelloMessage.Write(_w, new HelloMessage
            {
                Digests = d, SteamId = SteamId, DisplayName = Name, ClientNonce = ClientNonce, CompressionMask = CompressionNegotiation.AlwaysSupported, JoinFlags = joinFlags
            });
            Send(MessageKind.Hello, _w);
        }

        public void Manifest(FakeInfo info)
        {
            var payload = new ByteWriter();
            ManifestDetailMessage.Write(payload, new ManifestDetailMessage { Manifest = info.BuildManifest() });
            Control(ControlKind.ManifestDetail, payload.ToArray());
        }

        public void Control(ControlKind kind, byte[] payload)
        {
            _w.Reset();
            ControlMessage.Write(_w, kind, payload ?? Array.Empty<byte>(), 0, payload?.Length ?? 0);
            Send(MessageKind.Control, _w);
        }

        public void Send(MessageKind kind, ByteWriter body)
        {
            Channel.Send(kind, body);
            Channel.Pump();
        }

        public void Send(MessageKind kind, byte[] body)
        {
            Channel.Send(kind, body, 0, body.Length);
            Channel.Pump();
        }

        /// <summary>Sends bytes as one transport message, bypassing the envelope codec.</summary>
        public void SendRaw(byte[] wire) => Channel.Connection.Send(Lane.Reliable, Delivery.ReliableOrdered, wire, 0, wire.Length);

        public void Poll()
        {
            Channel.Pump();
            Transport.Poll(this);
        }

        public bool Got(MessageKind kind) => Received.Exists(m => m.Kind == kind);

        public bool GotControl(ControlKind kind) => Controls.Exists(c => c.Kind == kind);

        public RefuseMessage Refusal()
        {
            foreach ((MessageKind kind, byte[] body) in Received)
            {
                if (kind == MessageKind.Refuse) return RefuseMessage.Decode(body, 0, body.Length);
            }
            return null;
        }

        public PasswordChallengeMessage Challenge()
        {
            foreach ((ControlKind kind, byte[] payload) in Controls)
            {
                if (kind == ControlKind.PasswordChallenge) return PasswordChallengeMessage.Decode(payload, 0, payload.Length);
            }
            return null;
        }

        public void AnswerChallenge(string password)
        {
            PasswordChallengeMessage ch = Challenge();
            Assert.NotNull(ch, Name + " was challenged");
            var body = new ByteWriter();
            PasswordResponseMessage.Write(body, new PasswordResponseMessage { ClientNonce = ClientNonce, Mac = SessionCrypto.PasswordMac(password, ch.HostNonce, ClientNonce, ch.Salt, ch.SessionId) });
            Control(ControlKind.PasswordResponse, body.ToArray());
        }

        void ITransportEventSink.OnConnected(IConnection connection)
        {
        }

        void ITransportEventSink.OnDisconnected(IConnection connection, int reason, string text) => Disconnected = true;

        void ITransportEventSink.OnMessage(IConnection connection, Lane lane, byte[] buffer, int offset, int count)
        {
            if (!Channel.TryDecode(buffer, offset, count, out Envelope e)) return;
            var body = new byte[e.BodyCount];
            Buffer.BlockCopy(e.Body, e.BodyOffset, body, 0, e.BodyCount);
            Received.Add((e.Kind, body));
            if (e.Kind == MessageKind.Control)
            {
                ControlMessage c = ControlMessage.Decode(body, 0, body.Length);
                Controls.Add((c.Kind, c.Payload ?? Array.Empty<byte>()));
            }
        }
    }

    /// <summary>
    /// A client transport that can hold back everything that arrives on the reliable lane while other lanes flow, as
    /// when a lost packet of the reliable lane is retransmitted (Steam orders each lane on its own).
    /// </summary>
    internal sealed class LaneHoldTransport : ITransport, ITransportEventSink
    {
        private readonly ITransport _inner;
        private readonly List<(IConnection C, byte[] Data)> _held = new List<(IConnection, byte[])>();
        private ITransportEventSink _sink;

        public LaneHoldTransport(ITransport inner)
        {
            _inner = inner;
        }

        public bool HoldReliable;

        public int Held => _held.Count;

        public TransportKind Kind => _inner.Kind;

        public void Listen(ListenOptions options) => _inner.Listen(options);

        public IConnection Connect(Endpoint endpoint) => _inner.Connect(endpoint);

        public void Poll(ITransportEventSink sink)
        {
            _sink = sink;
            if (!HoldReliable && _held.Count > 0)
            {
                var release = new List<(IConnection C, byte[] Data)>(_held);
                _held.Clear();
                foreach ((IConnection c, byte[] data) in release) sink.OnMessage(c, Lane.Reliable, data, 0, data.Length);
            }
            _inner.Poll(this);
        }

        public void Dispose() => _inner.Dispose();

        void ITransportEventSink.OnConnected(IConnection connection) => _sink.OnConnected(connection);

        void ITransportEventSink.OnDisconnected(IConnection connection, int reason, string text) => _sink.OnDisconnected(connection, reason, text);

        void ITransportEventSink.OnMessage(IConnection connection, Lane lane, byte[] buffer, int offset, int count)
        {
            if (HoldReliable && lane == Lane.Reliable)
            {
                var copy = new byte[count];
                Buffer.BlockCopy(buffer, offset, copy, 0, count);
                _held.Add((connection, copy));
                return;
            }
            _sink.OnMessage(connection, lane, buffer, offset, count);
        }
    }

    /// <summary>
    /// The host's session state machine against scripted peers and real clients: handshake orderings, early and
    /// duplicate messages, refusals, passwords, capacity, roles and permissions, kicks and rejoins, timeouts.
    /// </summary>
    public sealed class SessionStateMachineTests
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

        private static List<PeerId> Admitted(SessionHost host)
        {
            var list = new List<PeerId>();
            host.GetAdmittedPeers(list);
            return list;
        }

        [Test]
        public void TheManifestMayArriveBeforeTheHello()
        {
            var rig = new SessionRig();
            var peer = new RawPeer(rig, "Early", 501);
            peer.Manifest(rig.HostInfo);
            Pump(rig, new[] { peer });
            Assert.False(peer.Got(MessageKind.Welcome), "not admitted on the manifest alone");
            peer.Hello(rig.HostInfo);
            Pump(rig, new[] { peer });
            Assert.True(peer.Got(MessageKind.Welcome), "admitted once both arrived");
            Assert.Equal(1, Admitted(rig.Host).Count);
        }

        [Test]
        public void ARepeatedHelloOrManifestChangesNothing()
        {
            var rig = new SessionRig();
            var peer = new RawPeer(rig, "Twice", 502);
            peer.Hello(rig.HostInfo);
            peer.Manifest(rig.HostInfo);
            Pump(rig, new[] { peer });
            peer.Hello(rig.HostInfo);
            peer.Manifest(rig.HostInfo);
            Pump(rig, new[] { peer });
            Assert.Equal(1, peer.Received.FindAll(m => m.Kind == MessageKind.Welcome).Count, "one welcome");
            Assert.Equal(1, Admitted(rig.Host).Count);
            Assert.Equal(1, rig.Snapshots.Requests, "one snapshot");
        }

        [Test]
        public void MessagesFromAPeerThatIsNotLiveAreIgnored()
        {
            var rig = new SessionRig();
            var chats = new List<ChatEntry>();
            var app = new List<MessageKind>();
            int live = 0, speeds = 0, presence = 0;
            rig.Host.ChatReceived += chats.Add;
            rig.Host.AppMessage += (p, e) => app.Add(e.Kind);
            rig.Host.PeerLive += p => live++;
            rig.Host.SpeedRequested += (p, s) => speeds++;
            rig.Host.PresenceReceived += m => presence++;
            var peer = new RawPeer(rig, "Pushy", 503);

            void Spam()
            {
                var w = new ByteWriter();
                ChatMessage.Write(w, new ChatMessage { From = new PeerId(9), Text = "hi" });
                peer.Send(MessageKind.Chat, w);
                peer.Send(MessageKind.Epoch, new byte[] { 1, 2, 3, 4 });
                var speed = new ByteWriter();
                SessionPayloads.WriteSpeed(speed, new SpeedState { Speed = 3 });
                peer.Control(ControlKind.SpeedRequest, speed.ToArray());
                var done = new ByteWriter();
                done.WriteBool(true);
                SessionPayloads.WriteText(done, "");
                peer.Control(ControlKind.LoadDone, done.ToArray());
                w.Reset();
                PresenceMessage.Write(w, new PresenceMessage { CursorX = 1 });
                peer.Send(MessageKind.Presence, w);
            }

            // before the handshake
            Spam();
            Pump(rig, new[] { peer });
            Assert.Equal(0, chats.Count, "no chat from an unadmitted peer");
            Assert.Equal(0, live);

            // admitted, but the snapshot is still being taken: LoadDone must not make it live
            rig.Snapshots.Hold = true;
            peer.Hello(rig.HostInfo);
            peer.Manifest(rig.HostInfo);
            Pump(rig, new[] { peer });
            Assert.True(peer.Got(MessageKind.Welcome));
            chats.Clear();
            Spam();
            Pump(rig, new[] { peer });
            Assert.Equal(0, live, "LoadDone before the download is ignored");
            Assert.Equal(0, app.Count, "no replication messages from a joiner");
            Assert.Equal(0, speeds, "no speed requests from a joiner");
            Assert.Equal(0, presence, "no presence from a joiner");
            Assert.Equal(1, chats.Count, "an admitted joiner may chat");
            Assert.Equal(new PeerId(1), chats[0].From, "the host stamps the sender, whatever the message claims");
            Assert.True(rig.Host.HoldSimulation);
        }

        [Test]
        public void AnOtherProtocolVersionIsRefusedWithAReport()
        {
            var rig = new SessionRig();
            var peer = new RawPeer(rig, "Old", 504);
            peer.Hello(rig.HostInfo, protocol: (ushort)(ProtocolVersion.Current - 1));
            peer.Manifest(rig.HostInfo);
            Pump(rig, new[] { peer });
            RefuseMessage r = peer.Refusal();
            Assert.NotNull(r);
            Assert.Equal(RefuseReason.ProtocolVersion, r.Reason);
            MismatchReport report = MismatchReport.Decode(r.MismatchReport);
            Assert.NotNull(report);
            Assert.Equal("protocol", report.Kind);
            Assert.True(peer.Disconnected, "the host closes the connection after refusing");
            Assert.Equal(0, Admitted(rig.Host).Count);
        }

        [Test]
        public void GarbageFromOnePeerDisconnectsOnlyThatPeer()
        {
            var rig = new SessionRig();
            var (good, _, _) = rig.AddClient("Good");
            rig.Run(300);
            Assert.Equal(ClientState.Live, good.State, good.EndReason);
            var bad = new RawPeer(rig, "Bad", 505);
            bad.SendRaw(new byte[] { (byte)MessageKind.Hello, 0x40, 1, 2, 3 });   // unknown envelope flags
            Pump(rig, new[] { bad }, 20);
            Assert.True(bad.Disconnected, "a malformed envelope ends that connection");
            Assert.Equal(ClientState.Live, good.State, "the others are not affected");
            Assert.Contains("malformed", string.Join("\n", rig.Log.Lines));

            // an admitted peer that later sends garbage is removed and the others are told
            var bad2 = new RawPeer(rig, "Bad2", 506);
            bad2.Hello(rig.HostInfo);
            bad2.Manifest(rig.HostInfo);
            Pump(rig, new[] { bad2 }, 20);
            Assert.True(bad2.Got(MessageKind.Welcome));
            bad2.Send(MessageKind.Ping, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });  // overlong varint
            Pump(rig, new[] { bad2 }, 40);
            Assert.True(bad2.Disconnected);
            Assert.Equal(1, Admitted(rig.Host).Count, "only the good client remains");
            Assert.False(rig.Host.HoldSimulation, "the removed joiner no longer holds the simulation");
        }

        [Test]
        public void PasswordResponsesAreOnlyAcceptedWhenChallengedAndCorrect()
        {
            var rig = new SessionRig(new HostOptions { Password = "open sesame" });
            var early = new RawPeer(rig, "Early", 507);
            // a response before any challenge is ignored (and does not admit)
            var body = new ByteWriter();
            PasswordResponseMessage.Write(body, new PasswordResponseMessage { ClientNonce = early.ClientNonce, Mac = new byte[32] });
            early.Control(ControlKind.PasswordResponse, body.ToArray());
            early.Hello(rig.HostInfo);
            early.Manifest(rig.HostInfo);
            Pump(rig, new[] { early });
            Assert.False(early.Got(MessageKind.Welcome));
            Assert.Null(early.Refusal(), "the early response was ignored, not judged");
            Assert.NotNull(early.Challenge(), "the challenge follows hello + manifest");
            early.AnswerChallenge("open sesame");
            Pump(rig, new[] { early });
            Assert.True(early.Got(MessageKind.Welcome), "the right answer admits");

            var wrong = new RawPeer(rig, "Wrong", 508);
            wrong.Hello(rig.HostInfo);
            wrong.Manifest(rig.HostInfo);
            Pump(rig, new[] { wrong });
            wrong.AnswerChallenge("open sesame!");
            Pump(rig, new[] { wrong });
            Assert.Equal(RefuseReason.BadPassword, wrong.Refusal()?.Reason ?? (RefuseReason)255);

            // a correct MAC for another peer's challenge (replay) does not work: each challenge has its own nonce
            var replay = new RawPeer(rig, "Replay", 509);
            replay.Hello(rig.HostInfo);
            replay.Manifest(rig.HostInfo);
            Pump(rig, new[] { replay });
            PasswordChallengeMessage first = early.Challenge();
            var replayed = new ByteWriter();
            PasswordResponseMessage.Write(replayed, new PasswordResponseMessage { ClientNonce = early.ClientNonce, Mac = SessionCrypto.PasswordMac("open sesame", first.HostNonce, early.ClientNonce, first.Salt, first.SessionId) });
            replay.Control(ControlKind.PasswordResponse, replayed.ToArray());
            Pump(rig, new[] { replay });
            Assert.Equal(RefuseReason.BadPassword, replay.Refusal()?.Reason ?? (RefuseReason)255, "a replayed response is refused");
            Assert.Equal(1, Admitted(rig.Host).Count);
        }

        [Test]
        public void ConcurrentPasswordHandshakesCannotOverfillTheSession()
        {
            // MaxPlayers counts the host: two clients fit. Three clients that all pass hello + manifest before any
            // of them answered the password challenge must not all be admitted (previously the capacity check
            // ran only before the challenge, so every pending handshake was admitted).
            var rig = new SessionRig(new HostOptions { Password = "pw", MaxPlayers = 3 });
            var peers = new List<RawPeer>();
            for (int i = 0; i < 3; i++) peers.Add(new RawPeer(rig, "P" + i, (ulong)(600 + i)));
            foreach (RawPeer p in peers)
            {
                p.Hello(rig.HostInfo);
                p.Manifest(rig.HostInfo);
            }
            Pump(rig, peers);
            foreach (RawPeer p in peers) Assert.NotNull(p.Challenge(), p.Name + " challenged");
            foreach (RawPeer p in peers) p.AnswerChallenge("pw");
            Pump(rig, peers);
            Assert.Equal(2, Admitted(rig.Host).Count, "the session holds the host and two players");
            Assert.Equal(2, peers.FindAll(p => p.Got(MessageKind.Welcome)).Count);
            RawPeer refused = peers.Find(p => !p.Got(MessageKind.Welcome));
            Assert.Equal(RefuseReason.Full, refused.Refusal()?.Reason ?? (RefuseReason)255, "the third is told the session is full");
        }

        [Test]
        public void ASilentConnectionIsDroppedAfterTheHandshakeTimeout()
        {
            var rig = new SessionRig(new HostOptions { HandshakeTimeoutSeconds = 5 });
            var silent = new RawPeer(rig, "Silent", 510);
            rig.Clock.Advance(0.1);
            rig.Host.Tick();
            silent.Poll();
            Assert.False(silent.Disconnected);
            for (int i = 0; i < 40; i++)
            {
                rig.Clock.Advance(0.1);
                rig.Host.Tick();
                silent.Poll();
            }
            Assert.False(silent.Disconnected, "4 s: still waiting");
            for (int i = 0; i < 20; i++)
            {
                rig.Clock.Advance(0.1);
                rig.Host.Tick();
                silent.Poll();
            }
            Assert.True(silent.Disconnected, "dropped after 5 s");
        }

        [Test]
        public void RolesDecidePermissionsAndTravelWithThePlayerList()
        {
            var rig = new SessionRig();
            var (anna, _, _) = rig.AddClient("Anna");
            var (ben, _, _) = rig.AddClient("Ben");
            rig.Run(400);
            Assert.Equal(ClientState.Live, anna.State, anna.EndReason);
            Assert.Equal(ClientState.Live, ben.State, ben.EndReason);
            var speedRequests = new List<PeerId>();
            var presence = new List<PresenceMessage>();
            rig.Host.SpeedRequested += (p, s) => speedRequests.Add(p);
            rig.Host.PresenceReceived += presence.Add;
            var roleEvents = new List<(PeerId, SessionRole)>();
            rig.Host.RoleChanged += (p, r) => roleEvents.Add((p, r));

            Assert.False(rig.Host.SetRole(PeerId.Host, SessionRole.Viewer), "the host's own role is fixed");
            Assert.False(rig.Host.SetRole(anna.LocalPeer, SessionRole.Host), "nobody else becomes host");
            Assert.False(rig.Host.SetRole(new PeerId(77), SessionRole.Viewer), "unknown peer");
            Assert.True(rig.Host.SetRole(anna.LocalPeer, SessionRole.Viewer));
            Assert.True(rig.Host.SetRole(anna.LocalPeer, SessionRole.Viewer), "setting the same role again is fine");
            Assert.Equal(1, roleEvents.Count, "but raises no second event");
            rig.Run(30);
            Assert.Equal(SessionRole.Viewer, anna.LocalRole, "the player list told Anna");
            Assert.Equal(SessionRole.Viewer, FindRole(ben, anna.LocalPeer), "and every other player");
            Assert.Equal(SessionRole.Viewer, rig.Host.GetRole(anna.LocalPeer));
            Assert.Equal(SessionRole.Host, rig.Host.GetRole(PeerId.Host));
            Assert.Equal(SessionRole.Viewer, rig.Host.GetRole(new PeerId(99)), "unknown peers have no rights");

            anna.RequestSpeed(new SpeedState { Speed = 3 });
            ben.RequestSpeed(new SpeedState { Speed = 2 });
            var preview = new byte[16];
            preview[0] = 1;
            anna.SendPresence(new PresenceMessage { CursorX = 5, Preview = preview, PreviewLength = 16 });
            rig.Run(10);
            Assert.SequenceEqual(new[] { ben.LocalPeer }, speedRequests, "a viewer cannot change the speed");
            PresenceMessage fromAnna = presence.Find(m => m.From == anna.LocalPeer);
            Assert.NotNull(fromAnna);
            Assert.Equal(0, fromAnna.PreviewLength, "a viewer shows no tool preview");

            // back to builder: the speed request goes through again
            Assert.True(rig.Host.SetRole(anna.LocalPeer, SessionRole.Builder));
            rig.Run(30);
            Assert.Equal(SessionRole.Builder, anna.LocalRole);
            anna.RequestSpeed(new SpeedState { Speed = 1 });
            rig.Run(10);
            Assert.Contains(anna.LocalPeer, speedRequests);
        }

        [Test]
        public void RolePermissionsAreNestedAndViewersOnlyChat()
        {
            SessionRole[] order = { SessionRole.Host, SessionRole.CoHost, SessionRole.Builder, SessionRole.Viewer };
            for (int i = 0; i + 1 < order.Length; i++)
            {
                Assert.True(RolePermissions.Allows(order[i], RolePermissions.Of(order[i + 1])), order[i] + " may do everything " + order[i + 1] + " may");
            }
            Permission all = Permission.None;
            foreach (Permission p in Enum.GetValues(typeof(Permission)))
            {
                if (p == Permission.All || p == Permission.None) continue;
                all |= p;
                Assert.True(RolePermissions.Allows(SessionRole.Builder, p), "builders: " + p);
                Assert.Equal(p == Permission.Chat, RolePermissions.Allows(SessionRole.Viewer, p), "viewers: " + p);
            }
            Assert.Equal(Permission.All, all, "All is exactly the union of the flags");
            Assert.True(RolePermissions.Allows(SessionRole.Viewer, Permission.None));
            Assert.False(RolePermissions.Allows(SessionRole.Viewer, Permission.Chat | Permission.Build));
            Assert.False(RolePermissions.Allows((SessionRole)200, Permission.Build), "an unknown role is a viewer");
            foreach (SessionRole r in order)
            {
                Assert.Equal(r, RolePermissions.Toggle(RolePermissions.Toggle(r)), "toggling twice is the identity: " + r);
                Assert.Equal(r != SessionRole.Viewer, RolePermissions.Predicts(r));
            }
            Assert.Equal(SessionRole.Builder, RolePermissions.Toggle(SessionRole.Viewer));
            Assert.Equal(SessionRole.CoHost, RolePermissions.Toggle(SessionRole.CoHost), "co-hosts are not toggled");
        }

        private static SessionRole FindRole(SessionClient client, PeerId id)
        {
            foreach (PlayerInfo p in client.Players)
            {
                if (p.Id == id) return p.Role;
            }
            return (SessionRole)255;
        }

        [Test]
        public void AKickedPlayerIsToldWhyAndMayJoinAgain()
        {
            var rig = new SessionRig();
            var (anna, _, _) = rig.AddClient("Anna", steamId: 7001);
            var (ben, _, _) = rig.AddClient("Ben", steamId: 7002);
            rig.Run(400);
            PeerId annaId = anna.LocalPeer;
            var left = new List<PeerId>();
            rig.Host.PeerLeft += left.Add;
            rig.Host.Kick(annaId, "too many roundabouts");
            rig.Host.Kick(annaId, "again");               // a second kick of the same id finds nobody
            rig.Host.Kick(PeerId.Host, "the host");       // never kicks the host
            rig.Run(30);
            Assert.Equal(ClientState.Ended, anna.State);
            Assert.Equal(ClientEndKind.Kicked, anna.EndKind);
            Assert.Contains("too many roundabouts", anna.EndReason);
            Assert.SequenceEqual(new[] { annaId }, left);
            Assert.Equal(2, ben.Players.Count, "host and Ben");
            Assert.Equal(ClientState.Live, ben.State);

            var (again, _, _) = rig.AddClient("Anna", steamId: 7001);
            rig.Run(400);
            Assert.Equal(ClientState.Live, again.State, again.EndReason);
            Assert.Equal(annaId, again.LocalPeer, "the lowest free id is handed out again");
            Assert.Equal(3, ben.Players.Count);
        }

        [Test]
        public void PeerIdsAreUniqueAndTheLowestFreeOneIsReused()
        {
            var rig = new SessionRig(new HostOptions { MaxPlayers = 16 });
            var clients = new List<SessionClient>();
            for (int i = 0; i < 5; i++) clients.Add(rig.AddClient("P" + i).Client);
            rig.Run(600);
            var ids = new HashSet<byte>();
            foreach (SessionClient c in clients)
            {
                Assert.Equal(ClientState.Live, c.State, c.EndReason);
                Assert.True(ids.Add(c.LocalPeer.Value), "duplicate id " + c.LocalPeer);
                Assert.False(c.LocalPeer.IsHost);
            }
            clients[1].Leave();
            clients[3].Leave();
            rig.Run(30);
            var late1 = rig.AddClient("Late1").Client;
            rig.Run(400);
            var late2 = rig.AddClient("Late2").Client;
            rig.Run(400);
            Assert.Equal(clients[1].LocalPeer, late1.LocalPeer);
            Assert.Equal(clients[3].LocalPeer, late2.LocalPeer);
        }

        [Test]
        public void SnapshotChunksThatOvertakeTheManifestAreNotLost()
        {
            // the snapshot fits into one chunk (bulk lane); the manifest (reliable lane) is held back until the chunk
            // has arrived. Previously the client dropped the chunk (no receiver yet) and, since no later chunk
            // revealed the gap, waited for the 120 s download watchdog.
            var rig = new SessionRig(snapshotBytes: 100_000);
            rig.Snapshots.Hold = true;
            var info = new FakeInfo();
            var loader = new FakeLoader();
            var lanes = new LaneHoldTransport(rig.HostTransport.CreateClient(new PeerIdentity(4242, "loop:slow", "Slow")));
            var client = new SessionClient(lanes, info, loader, new ClientOptions { PlayerName = "Slow", SteamId = 4242 }, rig.Clock, rig.Log);
            client.Connect(new Endpoint());
            rig.Clients.Add((client, loader, info));
            rig.Run(30);
            Assert.Equal(ClientState.WaitingForSnapshot, client.State, client.EndReason);

            lanes.HoldReliable = true;
            rig.Snapshots.DeliverPending();
            rig.Run(30);
            Assert.True(lanes.Held > 0, "the manifest is held back");
            Assert.Equal(ClientState.WaitingForSnapshot, client.State, "no manifest yet");
            lanes.HoldReliable = false;
            rig.Run(60);
            Assert.Equal(ClientState.Live, client.State, "joined within a second, not after the watchdog: " + client.EndReason);
            Assert.SequenceEqual(rig.Snapshots.Data, loader.Target.ToArray(), "the early chunk is the city");
        }

        [Test]
        public void TheSameSteamUserReplacesItsStaleConnection()
        {
            var rig = new SessionRig();
            var (first, _, _) = rig.AddClient("Anna", steamId: 8001);
            rig.Run(400);
            Assert.Equal(ClientState.Live, first.State);
            // the old game is frozen (never ticks again); the player starts a new game and joins
            rig.Clients.RemoveAt(0);
            var (second, _, _) = rig.AddClient("Anna", steamId: 8001);
            rig.Run(400);
            Assert.Equal(ClientState.Live, second.State, second.EndReason);
            Assert.Equal(1, rig.Host.LivePeerCount, "the stale connection was replaced, not kept");
            Assert.Equal(2, second.Players.Count);
        }
    }
}
