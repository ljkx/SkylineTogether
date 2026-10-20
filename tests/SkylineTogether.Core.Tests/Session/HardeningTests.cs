using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// The host's defences from the adversarial network review: identity and duplicate resolution after the password,
    /// the order of the handshake's checks, remembered roles, join watchdogs and cooldowns, bans of direct connections.
    /// </summary>
    public sealed class HardeningTests
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

        private static RawPeer Handshake(SessionRig rig, RawPeer peer, FakeInfo info = null)
        {
            peer.Hello(info ?? rig.HostInfo);
            peer.Manifest(info ?? rig.HostInfo);
            Pump(rig, new[] { peer });
            return peer;
        }

        // ------------------------------------------------------------------------------------- duplicates (finding 2)

        [Test]
        public void AConnectionWithALivePlayersIdentityButNoPasswordLeavesThemAlone()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "secret" });
            var (victim, _, _) = rig.AddClient("Victim", "secret", steamId: 1001);
            rig.Run(400);
            Assert.Equal(ClientState.Live, victim.State, victim.EndReason);

            // the transport reports the victim's Steam ID for the attacker (a direct connection whose identity Steam did
            // not verify, before the transport fix): it is challenged, refused, and the victim keeps its connection
            var attackerTransport = rig.HostTransport.CreateClient(new PeerIdentity(1001, "203.0.113.9:5555", "Victim", true));
            var attacker = new SessionClient(attackerTransport, new FakeInfo(), new FakeLoader(), new ClientOptions { PlayerName = "x", SteamId = 1001, Password = "" }, rig.Clock, rig.Log);
            attacker.Connect(new Endpoint());
            rig.Clients.Add((attacker, new FakeLoader(), new FakeInfo()));
            rig.Run(200);
            Assert.Equal(ClientState.Ended, attacker.State);
            Assert.Equal(ClientState.Live, victim.State, victim.EndReason);
            Assert.Equal(1, rig.Host.LivePeerCount);

            // a wrong password does not touch the victim either
            var wrong = Handshake(rig, new RawPeer(rig, new PeerIdentity(1001, "203.0.113.9:5556", "Victim", true), 1001));
            wrong.AnswerChallenge("guess");
            Pump(rig, new[] { wrong });
            Assert.Equal(RefuseReason.BadPassword, wrong.Refusal().Reason);
            Assert.Equal(ClientState.Live, victim.State, victim.EndReason);
        }

        [Test]
        public void TheSamePlayerWithThePasswordStillReplacesItsStaleConnection()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "secret" });
            var (first, _, _) = rig.AddClient("Anna", "secret", steamId: 1002);
            rig.Run(400);
            Assert.Equal(ClientState.Live, first.State, first.EndReason);
            var again = Handshake(rig, new RawPeer(rig, "Anna", 1002));
            Assert.Equal(1, rig.Host.LivePeerCount, "nothing happens before the password");
            again.AnswerChallenge("secret");
            Pump(rig, new[] { again });
            Assert.True(again.Got(MessageKind.Welcome), "admitted");
            rig.Run(30);
            Assert.Equal(ClientState.Ended, first.State, "the stale connection was replaced");
        }

        // ------------------------------------------------------------------------------------ check order (finding 6)

        [Test]
        public void ABannedPlayerOfAnotherVersionLearnsOnlyThatItIsBanned()
        {
            var rig = new SessionRig();
            rig.Host.Options.Bans.Add(7401, null, "Banned", 0);
            var peer = new RawPeer(rig, "Banned", 7401);
            peer.Hello(rig.HostInfo, protocol: (ushort)(ProtocolVersion.Current + 1));
            Pump(rig, new[] { peer });
            RefuseMessage r = peer.Refusal();
            Assert.Equal(RefuseReason.Banned, r.Reason);
            Assert.DoesNotContain(ProtocolVersion.Current.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Detail ?? "");
            Assert.Null(MismatchReport.Decode(r.MismatchReport)?.HostProtocol);

            var friends = new HashSet<ulong>();
            var policy = new SessionRig(new HostOptions { HostName = "Hosty", JoinPolicy = JoinPolicy.FriendsAndInvited, IsFriendOrInvited = friends.Contains });
            var stranger = new RawPeer(policy, "Stranger", 7402);
            stranger.Hello(policy.HostInfo, protocol: (ushort)(ProtocolVersion.Current + 1));
            Pump(policy, new[] { stranger });
            Assert.Equal(RefuseReason.NotInvited, stranger.Refusal().Reason);
        }

        // ---------------------------------------------------------------------------------- roles (finding 3)

        private static SessionClient Join(SessionRig rig, string name, ulong steamId, string password = "")
        {
            var (c, _, _) = rig.AddClient(name, password, steamId: steamId);
            rig.Run(400);
            Assert.Equal(ClientState.Live, c.State, c.EndReason);
            return c;
        }

        [Test]
        public void ARejoiningPlayerKeepsTheRoleTheHostGaveThem()
        {
            var rig = new SessionRig();
            SessionClient griefer = Join(rig, "Griefer", 1101);
            SessionClient helper = Join(rig, "Helper", 1102);
            Assert.True(rig.Host.SetRole(griefer.LocalPeer, SessionRole.Viewer));
            Assert.True(rig.Host.SetRole(helper.LocalPeer, SessionRole.CoHost));
            rig.Run(20);
            griefer.Leave();
            helper.Leave();
            rig.Run(20);

            SessionClient again = Join(rig, "Griefer", 1101);
            Assert.Equal(SessionRole.Viewer, rig.Host.GetRole(again.LocalPeer), "a demoted player cannot become a builder again by rejoining");
            Assert.Equal(SessionRole.Viewer, again.LocalRole, "the welcome carries the remembered role");
            SessionClient helperAgain = Join(rig, "Helper", 1102);
            Assert.Equal(SessionRole.CoHost, rig.Host.GetRole(helperAgain.LocalPeer));
            SessionClient fresh = Join(rig, "Fresh", 1103);
            Assert.Equal(SessionRole.Builder, rig.Host.GetRole(fresh.LocalPeer), "the default is unchanged");
        }

        [Test]
        public void NewPlayersCanJoinAsViewersWhileKnownOnesKeepTheirRole()
        {
            var rig = new SessionRig();
            SessionClient anna = Join(rig, "Anna", 1201);
            Assert.True(rig.Host.SetRole(anna.LocalPeer, SessionRole.Builder));
            anna.Leave();
            rig.Run(20);
            rig.Host.Options.NewPlayersJoinAsViewer = true; // read live
            SessionClient stranger = Join(rig, "Stranger", 1202);
            Assert.Equal(SessionRole.Viewer, rig.Host.GetRole(stranger.LocalPeer));
            Assert.Equal(SessionRole.Viewer, stranger.LocalRole);
            SessionClient annaAgain = Join(rig, "Anna", 1201);
            Assert.Equal(SessionRole.Builder, rig.Host.GetRole(annaAgain.LocalPeer), "a player the host made a builder stays one");
            Assert.True(rig.Host.SetRole(stranger.LocalPeer, SessionRole.Builder));
            Assert.Equal(SessionRole.Builder, rig.Host.GetRole(stranger.LocalPeer));

            // the in-process test recorder always builds
            var local = Handshake(rig, new RawPeer(rig, new PeerIdentity(0, "recorder", "Recorder", false, local: true), 0));
            WelcomeMessage w = null;
            foreach ((MessageKind kind, byte[] body) in local.Received)
            {
                if (kind == MessageKind.Welcome) w = WelcomeMessage.Decode(body, 0, body.Length);
            }
            Assert.NotNull(w);
            Assert.Equal(SessionRole.Builder, w.Role);
        }

        [Test]
        public void ADirectPlayersRoleIsRememberedByAddress()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });
            var a = Handshake(rig, new RawPeer(rig, new PeerIdentity(0, "[2001:db8:1:2::10]:5000", "Ipv6", true), 0));
            a.AnswerChallenge("pw");
            Pump(rig, new[] { a });
            Assert.True(a.Got(MessageKind.Welcome));
            var list = new List<PeerId>();
            rig.Host.GetAdmittedPeers(list);
            Assert.True(rig.Host.SetRole(list[0], SessionRole.Viewer));
            rig.Host.Kick(list[0], "bye");
            // another address of the same /64 network
            var b = Handshake(rig, new RawPeer(rig, new PeerIdentity(0, "[2001:db8:1:2::99]:5001", "Ipv6", true), 0));
            b.AnswerChallenge("pw");
            Pump(rig, new[] { b });
            list.Clear();
            rig.Host.GetAdmittedPeers(list);
            Assert.Equal(SessionRole.Viewer, rig.Host.GetRole(list[0]));
            Assert.Equal("2001:db8:1:2::/64", BanList.AddressKey("[2001:DB8:1:2:abcd::1]:27015"));
            Assert.Equal("203.0.113.7", BanList.AddressKey("203.0.113.7:27015"));
            Assert.Equal("203.0.113.7", BanList.AddressKey("[::ffff:203.0.113.7]:1"));
            Assert.Null(BanList.AddressKey(" "));
        }

        // ------------------------------------------------------------------------- join watchdogs (finding 4)

        [Test]
        public void AJoinerWhoseLoadNeverProgressesNoLongerHoldsTheSimulationForHalfAnHour()
        {
            var rig = new SessionRig();
            var (client, loader, _) = rig.AddClient("Griefer", tweakLoader: l => l.Defer = true);
            rig.Run(300);
            Assert.Equal(ClientState.Loading, client.State, client.EndReason);
            Assert.True(rig.Host.HoldSimulation);
            // the joiner keeps answering and reporting the same load progress
            for (int i = 0; i < 2 * 400 && rig.Host.HoldSimulation; i++)
            {
                rig.Clock.Advance(0.5);
                rig.Host.Tick();
                client.Tick();
            }
            Assert.False(rig.Host.HoldSimulation, "released by the load watchdog");
            Assert.True(rig.Clock.Seconds - 100 < 420, "after about five minutes without progress, not thirty minutes");
            Assert.Contains("Loading the city made no progress", string.Join("\n", rig.Log.Lines));
        }

        [Test]
        public void AJoinerWhoseLoadProgressesIsNotDropped()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", MaxLoadSeconds = 1200 });
            var (client, loader, _) = rig.AddClient("Slow", tweakLoader: l => l.Defer = true);
            rig.Run(300);
            Assert.Equal(ClientState.Loading, client.State, client.EndReason);
            for (int i = 0; i < 2 * 900; i++)
            {
                client.ReportLoadProgress(i / 2000f);
                rig.Clock.Advance(0.5);
                rig.Host.Tick();
                client.Tick();
            }
            Assert.Equal(ClientState.Loading, client.State, "a slow load that progresses may take its time");
            loader.Finish();
            rig.Run(30);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
        }

        [Test]
        public void AJoinerThatNeverFinishesTheDownloadIsDropped()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", DownloadStallSeconds = 60 });
            var raw = Handshake(rig, new RawPeer(rig, "Lurker", 7501));
            Assert.True(raw.Got(MessageKind.Welcome));
            var progress = new ByteWriter();
            SessionPayloads.WriteLoadProgress(progress, JoinPhase.Downloading, 0.5f);
            for (int i = 0; i < 2 * 120 && rig.Host.HoldSimulation; i++)
            {
                if (i % 4 == 0) raw.Control(ControlKind.LoadProgress, progress.ToArray()); // heard, but never loading
                rig.Clock.Advance(0.5);
                rig.Host.Tick();
                raw.Poll();
            }
            Assert.False(rig.Host.HoldSimulation);
            Assert.Contains("The city download made no progress", string.Join("\n", rig.Log.Lines));
        }

        [Test]
        public void JoinsThatDoNotFinishMakeTheSamePlayerWait()
        {
            var rig = new SessionRig();
            ulong id = 7601;
            SessionClient Attempt(bool fail)
            {
                var (c, _, _) = rig.AddClient("Flaky", steamId: id, tweakLoader: l => l.Fail = fail);
                rig.Run(300);
                return c;
            }
            Assert.Equal(ClientEndKind.LoadFailed, Attempt(true).EndKind, "the first failed join");
            Assert.Equal(ClientEndKind.LoadFailed, Attempt(true).EndKind, "the second is admitted at once (the first failure is free)");
            int requests = rig.Snapshots.Requests;
            SessionClient third = Attempt(false);
            Assert.Equal(ClientEndKind.RefusedTemporarily, third.EndKind, "then the player waits");
            Assert.Contains("You can join again in", third.EndReason);
            Assert.True(SessionClient.IsRetryable(third.EndKind), "an automatic rejoin tries again later");
            Assert.Equal(requests, rig.Snapshots.Requests, "no snapshot was taken for the refused attempt");
            rig.RunHostOnly(31);
            SessionClient fourth = Attempt(false);
            Assert.Equal(ClientState.Live, fourth.State, fourth.EndReason);
            // a completed join wipes the slate: another failure is free again
            fourth.Leave();
            rig.Run(10);
            Assert.Equal(ClientEndKind.LoadFailed, Attempt(true).EndKind);
            Assert.Equal(ClientEndKind.LoadFailed, Attempt(true).EndKind);
            // other players are not affected
            var (other, _, _) = rig.AddClient("Other", steamId: 7602);
            rig.Run(300);
            Assert.Equal(ClientState.Live, other.State, other.EndReason);
        }

        [Test]
        public void AJoinerThatKeepsAskingForTheCityAgainIsDropped()
        {
            var rig = new SessionRig(snapshotBytes: 3_000_000);
            var raw = Handshake(rig, new RawPeer(rig, "Leech", 7701));
            SnapshotManifest manifest = null;
            for (int i = 0; i < 200 && manifest == null; i++)
            {
                Pump(rig, new[] { raw }, 1);
                foreach ((ControlKind kind, byte[] payload) in raw.Controls)
                {
                    if (kind == ControlKind.SnapshotManifest) manifest = SnapshotManifestCodec.Decode(payload, 0, payload.Length);
                }
            }
            Assert.NotNull(manifest);
            int chunks = (int)((manifest.Size + manifest.ChunkSize - 1) / manifest.ChunkSize);
            var empty = new byte[(chunks + 7) / 8];
            var have = new ByteWriter();
            BlobCodec.WriteHave(have, manifest.SnapshotId, empty, 0, empty.Length);
            for (int i = 0; i < 400 && !raw.Disconnected; i++)
            {
                raw.Send(MessageKind.BlobHave, have.ToArray());
                rig.Clock.Advance(0.3);
                rig.Host.Tick();
                raw.Poll();
            }
            Assert.True(raw.Disconnected, "the host stopped sending the city over and over");
            Assert.Contains("of the city again", string.Join("\n", rig.Log.Lines));
        }

        // ------------------------------------------------------------- handshake slots and player limit (10, 18)

        [Test]
        public void TheTransportLimitsHandshakesSeparatelyFromPlayers()
        {
            var listen = new ListenOptions { MaxConnections = 4, MaxPendingConnections = 8, MaxPendingPerRemote = 2 };
            Assert.Null(listen.Refusal(0, 0, 0));
            Assert.Null(listen.Refusal(3, 7, 1), "players and handshakes below their limits");
            Assert.Equal("server busy", listen.Refusal(0, 8, 0), "strangers holding handshakes do not take the players' slots");
            Assert.Equal("server busy", listen.Refusal(0, 2, 2), "two handshakes per remote");
            Assert.Equal("server full", listen.Refusal(4, 0, 0));
        }

        [Test]
        public void AConnectionWithoutAHelloIsDroppedSoon()
        {
            var rig = new SessionRig();
            var silent = new RawPeer(rig, "Silent", 7801);
            for (int i = 0; i < 8 * 2; i++)
            {
                rig.Clock.Advance(0.5);
                rig.Host.Tick();
                silent.Poll();
            }
            Assert.False(silent.Disconnected, "eight seconds are fine");
            for (int i = 0; i < 6 * 2; i++)
            {
                rig.Clock.Advance(0.5);
                rig.Host.Tick();
                silent.Poll();
            }
            Assert.True(silent.Disconnected, "no hello within ten seconds");
            // a joiner that sends its hello at once is not affected
            var (anna, _, _) = rig.AddClient("Anna");
            rig.Run(400);
            Assert.Equal(ClientState.Live, anna.State, anna.EndReason);
        }

        [Test]
        public void TheTransportHearsOfAdmissionsAndOfARaisedPlayerLimit()
        {
            var listen = new ListenOptions { MaxConnections = 2 };
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", MaxPlayers = 2, Listen = listen });
            var raw = new RawPeer(rig, "Anna", 7901);
            Pump(rig, new[] { raw });
            var connection = (IAdmissionAware)rig.HostTransport.GetConnections()[0];
            Assert.False(((LoopbackConnection)connection).Admitted, "a handshake gets no player resources");
            Handshake(rig, raw);
            Assert.True(raw.Got(MessageKind.Welcome));
            Assert.True(((LoopbackConnection)connection).Admitted);

            // the host raises the limit while hosting: the session and the transport's cap follow
            var full = Handshake(rig, new RawPeer(rig, "Ben", 7902));
            Assert.Equal(RefuseReason.Full, full.Refusal().Reason);
            rig.Host.SetMaxPlayers(4);
            Assert.Equal(4, listen.MaxConnections);
            Assert.Equal(4, rig.Host.Options.MaxPlayers);
            var ben = Handshake(rig, new RawPeer(rig, "Ben", 7903));
            Assert.True(ben.Got(MessageKind.Welcome));
            rig.Host.SetMaxPlayers(99);
            Assert.Equal(16, rig.Host.Options.MaxPlayers, "clamped");
        }

        // ------------------------------------------------------------------------------- log flood (finding 5)

        [Test]
        public void RefusedModerationRequestsAreLoggedSparingly()
        {
            var rig = new SessionRig();
            SessionClient builder = Join(rig, "Builder", 8001);
            SessionClient other = Join(rig, "Other", 8002);
            for (int i = 0; i < 500; i++) builder.RequestKick(other.LocalPeer);
            rig.Run(30);
            Assert.Equal(ClientState.Live, other.State, "a builder kicks nobody");
            int lines = 0;
            foreach (string l in rig.Log.Lines)
            {
                if (l.Contains("may not Kick")) lines++;
            }
            Assert.True(lines >= 3 && lines <= 10, "the first few and then every 100th, not 500 lines (" + lines + ")");
        }

        // ------------------------------------------------------------ bans and lockouts by address (14, 16)

        [Test]
        public void BanningADirectSteamPlayerAlsoBansTheAddress()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });
            var anna = Handshake(rig, new RawPeer(rig, new PeerIdentity(8101, "203.0.113.20:4000", "Anna", true), 8101));
            anna.AnswerChallenge("pw");
            Pump(rig, new[] { anna });
            var list = new List<PeerId>();
            rig.Host.GetAdmittedPeers(list);
            Assert.True(rig.Host.Ban(list[0], "bye"));
            Assert.Equal(2, rig.Host.Options.Bans.Count, "the Steam ID and the address");
            // she comes back over direct IP without a Steam identity
            var back = Handshake(rig, new RawPeer(rig, new PeerIdentity(0, "203.0.113.20:4001", "Anna", true), 0));
            Assert.Equal(RefuseReason.Banned, back.Refusal().Reason);

            // a Steam P2P player is banned by the Steam ID only (a relay address means nothing)
            var ben = Handshake(rig, new RawPeer(rig, "Ben", 8102));
            ben.AnswerChallenge("pw");
            Pump(rig, new[] { ben });
            list.Clear();
            rig.Host.GetAdmittedPeers(list);
            int before = rig.Host.Options.Bans.Count;
            Assert.True(rig.Host.Ban(list[0], null));
            Assert.Equal(before + 1, rig.Host.Options.Bans.Count);
        }

        [Test]
        public void IPv6BansAndLockoutsCoverTheWhole64()
        {
            var bans = new BanList();
            Assert.True(bans.Add(0, "[2001:db8:aa:bb::1]:27015", "Eve", 1));
            Assert.True(bans.IsBanned(0, "[2001:db8:aa:bb:ffff::42]:1"), "another address of the same /64");
            Assert.False(bans.IsBanned(0, "[2001:db8:aa:bc::1]:1"), "the next /64 is not banned");
            Assert.False(bans.Add(0, "[2001:db8:aa:bb::99]:2", "Eve", 2), "already banned");
            BanList back = BanList.Parse("ip\t2001:db8:aa:bb::5\t0\tOld exact ban");
            Assert.True(back.IsBanned(0, "[2001:db8:aa:bb::6]:7"), "a ban persisted for one address now covers its /64");
            Assert.True(bans.IsBanned(0, "198.51.100.1:1") == false);

            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "right" });
            for (int i = 0; i < 4; i++)
            {
                var guess = Handshake(rig, new RawPeer(rig, new PeerIdentity(0, "[2001:db8:1:2::" + (i + 1) + "]:5000", "Eve", true), 0));
                guess.AnswerChallenge("wrong" + i);
                Pump(rig, new[] { guess });
            }
            var rotated = Handshake(rig, new RawPeer(rig, new PeerIdentity(0, "[2001:db8:1:2::77]:5000", "Eve", true), 0));
            Assert.Equal(RefuseReason.BadPassword, rotated.Refusal()?.Reason ?? (RefuseReason)255, "a new address in the same /64 is still locked out");
            Assert.Null(rotated.Challenge());
        }

        [Test]
        public void PingsAreNotAnsweredBeforeAdmission()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });
            var peer = new RawPeer(rig, "Pinger", 7403);
            var w = new ByteWriter();
            PingMessage.Write(w, new PingMessage { Sequence = 1, SentSeconds = 1 });
            peer.Send(MessageKind.Ping, w);
            Pump(rig, new[] { peer });
            Assert.False(peer.Got(MessageKind.Ping), "no reply before the hello");
            peer.Hello(rig.HostInfo);
            peer.Send(MessageKind.Ping, w);
            Pump(rig, new[] { peer });
            Assert.NotNull(peer.Challenge());
            Assert.False(peer.Got(MessageKind.Ping), "no reply before admission");
        }
    }
}
