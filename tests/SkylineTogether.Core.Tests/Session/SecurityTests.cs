using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// Authentication, password limits, bans, the join policy, co-host moderation, refusal reasons and the
    /// build id, against scripted raw peers (<see cref="RawPeer"/>) and real clients on the loopback transport.
    /// </summary>
    public sealed class SecurityTests
    {
        private static readonly PeerIdentity DirectEve = new PeerIdentity(0, "203.0.113.5:40001", "Eve", true);

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

        private static RawPeer Handshake(SessionRig rig, RawPeer peer, FakeInfo info = null)
        {
            peer.Hello(info ?? rig.HostInfo);
            peer.Manifest(info ?? rig.HostInfo);
            Pump(rig, new[] { peer });
            return peer;
        }

        private static RefuseReason ReasonOf(RawPeer p) => p.Refusal()?.Reason ?? (RefuseReason)255;

        // ------------------------------------------------------------------------------------- direct IP and identity

        [Test]
        public void ADirectConnectionWithoutAHostPasswordIsRefusedBeforeAnyDetail()
        {
            var rig = new SessionRig();
            var eve = Handshake(rig, new RawPeer(rig, DirectEve, 0));
            Assert.Equal(RefuseReason.NotInvited, ReasonOf(eve), "a direct joiner needs the password, and there is none");
            Assert.Null(eve.Challenge(), "no challenge without a password");
            Assert.True(MismatchReport.Decode(eve.Refusal().MismatchReport).CarriesNoDetails, "no compatibility report");
            Assert.False(eve.Got(MessageKind.Welcome));
            Assert.True(eve.Disconnected);

            // an unauthenticated connection (no Steam ID) that did not come in on the direct socket is treated the same
            var anon = Handshake(rig, new RawPeer(rig, new PeerIdentity(0, "198.51.100.7:5000", "Anon"), 0));
            Assert.Equal(RefuseReason.NotInvited, ReasonOf(anon));

            // an in-process connection (the solo test recorder) is trusted like the host and joins without one
            var local = Handshake(rig, new RawPeer(rig, new PeerIdentity(0, "recorder", "Recorder", false, local: true), 0));
            Assert.True(local.Got(MessageKind.Welcome), "the local recorder is admitted");

            // a Steam-authenticated P2P peer joins without a password, as before
            var steam = Handshake(rig, new RawPeer(rig, "Steam", 7101));
            Assert.True(steam.Got(MessageKind.Welcome));
            Assert.Null(steam.Challenge());
        }

        [Test]
        public void ADirectClientJoinsWithTheRightPasswordEndToEnd()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "tram line 7" });
            var info = new FakeInfo();
            var loader = new FakeLoader();
            var transport = rig.HostTransport.CreateClient(DirectEve);
            var client = new SessionClient(transport, info, loader, new ClientOptions { PlayerName = "Eve", Password = "tram line 7" }, rig.Clock, rig.Log);
            client.Connect(new Endpoint());
            rig.Clients.Add((client, loader, info));
            rig.Run(400);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);

            var wrongTransport = rig.HostTransport.CreateClient(new PeerIdentity(0, "203.0.113.6:40002", "Mallory", true));
            var wrong = new SessionClient(wrongTransport, new FakeInfo(), new FakeLoader(), new ClientOptions { PlayerName = "Mallory", Password = "tram line 8" }, rig.Clock, rig.Log);
            wrong.Connect(new Endpoint());
            rig.Clients.Add((wrong, new FakeLoader(), new FakeInfo()));
            rig.Run(100);
            Assert.Equal(ClientState.Ended, wrong.State);
            Assert.Equal(RefuseReason.BadPassword, wrong.RefusedFor);
        }

        [Test]
        public void TheHellosSteamIdClaimIsNeverAnIdentity()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });
            var (anna, _, _) = rig.AddClient("Anna", "pw", steamId: 8001);
            rig.Run(400);
            Assert.Equal(ClientState.Live, anna.State, anna.EndReason);

            // a direct connection claims Anna's Steam ID: previously the host believed it and dropped Anna's
            // connection as "stale"; now the claim is ignored and the stranger only gets a password challenge
            var imposter = Handshake(rig, new RawPeer(rig, new PeerIdentity(0, "203.0.113.66:1000", "Anna", true), 8001));
            Assert.NotNull(imposter.Challenge());
            rig.Run(30);
            Assert.Equal(ClientState.Live, anna.State, "Anna keeps her connection");
            Assert.Equal(1, rig.Host.LivePeerCount);
            Assert.Contains("the claim is ignored", string.Join("\n", rig.Log.Lines));
        }

        // ------------------------------------------------------------------------------------------- password first

        [Test]
        public void ThePasswordIsCheckedBeforeAnyMismatchIsRevealed()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "secret" });
            var other = new FakeInfo { ModBuild = 0xABCDEF012345, GameVersion = "9.9.9" };
            other.Mods.Add(new ModEntry { Name = "Secret Mod", Version = "1", PlatformId = "999" });

            var stranger = Handshake(rig, new RawPeer(rig, "Stranger", 7201), other);
            Assert.NotNull(stranger.Challenge(), "challenged first");
            Assert.Null(stranger.Refusal(), "nothing about versions or mods before the password");
            stranger.AnswerChallenge("guess");
            Pump(rig, new[] { stranger });
            RefuseMessage r = stranger.Refusal();
            Assert.Equal(RefuseReason.BadPassword, r.Reason);
            Assert.True(MismatchReport.Decode(r.MismatchReport).CarriesNoDetails);
            Assert.DoesNotContain("9.9.9", r.Detail ?? "");
            Assert.DoesNotContain("1.2.3", r.Detail ?? "");

            var friend = Handshake(rig, new RawPeer(rig, "Friend", 7202), other);
            friend.AnswerChallenge("secret");
            Pump(rig, new[] { friend });
            RefuseMessage m = friend.Refusal();
            Assert.Equal(RefuseReason.Mismatch, m.Reason, "after the password the detail follows");
            MismatchReport report = MismatchReport.Decode(m.MismatchReport);
            Assert.Equal("build", report.Kind);
            Assert.Equal(BuildId.Format(rig.HostInfo.ModBuild), report.HostBuild);
            Assert.Equal("abcdef012345", report.ClientBuild);
            Assert.Contains("host build " + BuildId.Format(rig.HostInfo.ModBuild) + ", yours abcdef012345", m.Detail);
        }

        [Test]
        public void AProtocolMismatchBeforeThePasswordCarriesNoVersions()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "secret" });
            var peer = new RawPeer(rig, "Old", 7301);
            peer.Hello(rig.HostInfo, protocol: (ushort)(ProtocolVersion.Current - 1));
            Pump(rig, new[] { peer });
            RefuseMessage r = peer.Refusal();
            Assert.Equal(RefuseReason.ProtocolVersion, r.Reason);
            Assert.True(MismatchReport.Decode(r.MismatchReport).CarriesNoDetails);
            Assert.DoesNotContain(ProtocolVersion.Current.ToString(), r.Detail);
        }

        [Test]
        public void AChallengeIsAnsweredOnceAndNeedsARealNonce()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });
            var peer = Handshake(rig, new RawPeer(rig, "Short", 7401));
            PasswordChallengeMessage ch = peer.Challenge();
            var shortNonce = new byte[2];
            var body = new ByteWriter();
            PasswordResponseMessage.Write(body, new PasswordResponseMessage { ClientNonce = shortNonce, Mac = SessionCrypto.ClientProof(SessionCrypto.DeriveKey("pw", ch.Salt), ch.SessionId, ch.HostNonce, shortNonce, ch.Salt, 0) });
            peer.Control(ControlKind.PasswordResponse, body.ToArray());
            Pump(rig, new[] { peer });
            Assert.Equal(RefuseReason.BadPassword, ReasonOf(peer), "a two-byte nonce is refused even with the right password");

            var twice = Handshake(rig, new RawPeer(rig, "Twice", 7402));
            twice.AnswerChallenge("pw");
            twice.AnswerChallenge("wrong"); // the challenge is spent: the second answer is ignored
            Pump(rig, new[] { twice });
            Assert.True(twice.Got(MessageKind.Welcome));
            Assert.Null(twice.Refusal());
        }

        // ------------------------------------------------------------------------------------------- attempt limits

        [Test]
        public void WrongPasswordsLockTheSteamIdOutWithBackoff()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "right", MaxPlayers = 8 });
            for (int i = 0; i < 3; i++)
            {
                var p = Handshake(rig, new RawPeer(rig, "Guesser", 7501));
                Assert.NotNull(p.Challenge(), "free attempt " + (i + 1));
                p.AnswerChallenge("wrong" + i);
                Pump(rig, new[] { p });
                Assert.Equal(RefuseReason.BadPassword, ReasonOf(p));
                Assert.DoesNotContain("Too many", p.Refusal().Detail);
            }
            var fourth = Handshake(rig, new RawPeer(rig, "Guesser", 7501));
            fourth.AnswerChallenge("wrong3");
            Pump(rig, new[] { fourth });
            Assert.Contains("Too many wrong passwords", fourth.Refusal().Detail, "the fourth failure locks out");

            var locked = Handshake(rig, new RawPeer(rig, "Guesser", 7501));
            Assert.Null(locked.Challenge(), "while locked out no challenge is issued");
            Assert.Equal(RefuseReason.BadPassword, ReasonOf(locked));
            Assert.Contains("Try again in 5 s", locked.Refusal().Detail);

            // another player is not affected
            var other = Handshake(rig, new RawPeer(rig, "Other", 7502));
            Assert.NotNull(other.Challenge());

            // after the lockout one more wrong answer doubles it
            rig.Clock.Advance(6);
            var again = Handshake(rig, new RawPeer(rig, "Guesser", 7501));
            Assert.NotNull(again.Challenge(), "the lockout ended");
            again.AnswerChallenge("wrong4");
            Pump(rig, new[] { again });
            Assert.Contains("Try again in 10 s", again.Refusal().Detail);

            // the right password clears the record
            rig.Clock.Advance(11);
            var right = Handshake(rig, new RawPeer(rig, "Guesser", 7501));
            right.AnswerChallenge("right");
            Pump(rig, new[] { right });
            Assert.True(right.Got(MessageKind.Welcome));
            Assert.Equal(0, rig.Host.PasswordAttempts.Tracked, "a right answer clears the record");
        }

        [Test]
        public void DirectConnectionsFromOneAddressShareTheirLockout()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "right" });
            for (int i = 0; i < 4; i++)
            {
                var p = Handshake(rig, new RawPeer(rig, new PeerIdentity(0, "203.0.113.20:" + (50000 + i), "Bot" + i, true), 0));
                Assert.NotNull(p.Challenge(), "attempt " + i);
                p.AnswerChallenge("nope" + i);
                Pump(rig, new[] { p });
            }
            var next = Handshake(rig, new RawPeer(rig, new PeerIdentity(0, "203.0.113.20:59999", "Bot9", true), 0));
            Assert.Null(next.Challenge(), "a new port of the same address is still locked out");
            Assert.Contains("Too many wrong passwords", next.Refusal().Detail);
            var elsewhere = Handshake(rig, new RawPeer(rig, new PeerIdentity(0, "203.0.113.21:50000", "Fine", true), 0));
            Assert.NotNull(elsewhere.Challenge(), "another address is not");
        }

        [Test]
        public void OpenChallengesPerAddressAreBounded()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "right", MaxPendingChallengesPerKey = 2 });
            var peers = new List<RawPeer>();
            for (int i = 0; i < 3; i++) peers.Add(new RawPeer(rig, new PeerIdentity(0, "203.0.113.30:" + (40000 + i), "Flood" + i, true), 0));
            foreach (RawPeer p in peers) p.Hello(rig.HostInfo);
            Pump(rig, peers);
            Assert.Equal(2, peers.FindAll(p => p.Challenge() != null).Count, "two open challenges at most");
            Assert.Equal(RefuseReason.BadPassword, ReasonOf(peers[2]));
        }

        [Test]
        public void TheLimiterDoublesUpToItsMaximumAndForgets()
        {
            var l = new PasswordAttemptLimiter { FreeFailures = 1, BaseLockoutSeconds = 2, MaxLockoutSeconds = 9, ForgetSeconds = 100 };
            string[] keys = { "steam:1", "ip:10.0.0.1" };
            Assert.Equal(0.0, l.OnFailure(keys, 0), "the free failure");
            Assert.False(l.IsLocked(keys, 0, out _));
            Assert.Equal(2.0, l.OnFailure(keys, 1));
            Assert.True(l.IsLocked(new[] { "ip:10.0.0.1" }, 2, out double wait));
            Assert.Equal(1.0, wait);
            Assert.Equal(4.0, l.OnFailure(keys, 3));
            Assert.Equal(8.0, l.OnFailure(keys, 7));
            Assert.Equal(9.0, l.OnFailure(keys, 15), "capped");
            Assert.False(l.IsLocked(keys, 15 + 9.5, out _));
            Assert.Equal(9.0, l.OnFailure(keys, 30), "still remembered");
            Assert.Equal(0.0, l.OnFailure(keys, 200), "forgotten after 100 s without failures: free again");
            l.OnSuccess(keys);
            Assert.Equal(0, l.Tracked);

            var bounded = new PasswordAttemptLimiter { MaxEntries = 8 };
            for (int i = 0; i < 100; i++) bounded.OnFailure(new[] { "ip:" + i }, i);
            Assert.True(bounded.Tracked <= 8, "bounded memory: " + bounded.Tracked);
        }

        // -------------------------------------------------------------------------------------------------- crypto

        [Test]
        public void ThePasswordKeyIsPbkdf2Sha256()
        {
            // RFC 7914 §11: PBKDF2-HMAC-SHA256("passwd", "salt", 1)
            byte[] one = SessionCrypto.Pbkdf2Sha256(Encoding.UTF8.GetBytes("passwd"), Encoding.UTF8.GetBytes("salt"), 1);
            Assert.Equal("55ac046e56e3089fec1691c22544b605f94185216dde0465e68b9d57c20dacbc", Hex(one));
            byte[] salt = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
            byte[] mine = SessionCrypto.DeriveKey("Grün & 7", salt);
            byte[] reference;
            using (var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes("Grün & 7"), salt, SessionCrypto.Pbkdf2Iterations, HashAlgorithmName.SHA256)) reference = kdf.GetBytes(32);
            Assert.Equal(Hex(reference), Hex(mine), "matches the framework's PBKDF2 with the same rounds");
            Assert.Equal(200000, SessionCrypto.Pbkdf2Iterations, "raised from 10 000 with protocol 6");
            byte[] hn = { 9, 9, 9 }, cn = { 8, 8, 8, 8, 8, 8, 8, 8 };
            string proof = Hex(SessionCrypto.ClientProof(mine, 77, hn, cn, salt, 5));
            Assert.Equal(proof, Hex(SessionCrypto.ClientProof(mine, 77, hn, cn, salt, 5)), "deterministic");
            Assert.NotEqual(proof, Hex(SessionCrypto.ClientProof(mine, 78, hn, cn, salt, 5)), "bound to the session id");
            Assert.NotEqual(proof, Hex(SessionCrypto.ClientProof(mine, 77, hn, cn, salt, 6)), "bound to the host's Steam ID");
            Assert.NotEqual(proof, Hex(SessionCrypto.ClientProof(mine, 77, new byte[] { 9, 9 }, new byte[] { 9, 8, 8, 8, 8, 8, 8, 8, 8 }, salt, 5)), "nonces are length-prefixed");
            Assert.NotEqual(proof, Hex(SessionCrypto.HostProof(mine, 77, hn, cn, salt, 5)), "the host's proof is not the client's reflected");
            Assert.True(SessionCrypto.FixedTimeEquals(new byte[] { 1, 2 }, new byte[] { 1, 2 }));
            Assert.False(SessionCrypto.FixedTimeEquals(new byte[] { 1, 2 }, new byte[] { 1, 3 }));
            Assert.False(SessionCrypto.FixedTimeEquals(new byte[] { 1 }, new byte[] { 1, 2 }));
        }

        private static string Hex(byte[] b)
        {
            var sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        // ---------------------------------------------------------------------------------------------------- bans

        [Test]
        public void ABannedPlayerIsToldAndRefusedUntilUnbanned()
        {
            var rig = new SessionRig();
            int changes = 0;
            rig.Host.BansChanged += () => changes++;
            var (anna, _, _) = rig.AddClient("Anna", steamId: 9001);
            var (ben, _, _) = rig.AddClient("Ben", steamId: 9002);
            rig.Run(400);
            Assert.Equal(ClientState.Live, anna.State, anna.EndReason);
            var chats = new List<ChatEntry>();
            rig.Host.ChatReceived += chats.Add;
            Assert.False(rig.Host.Ban(PeerId.Host, "no"), "the host cannot ban itself");
            Assert.True(rig.Host.Ban(anna.LocalPeer, "griefing"));
            rig.Run(30);
            Assert.Equal(ClientEndKind.Banned, anna.EndKind);
            Assert.Contains("griefing", anna.EndReason);
            Assert.Contains("banned", anna.EndReason);
            Assert.Equal(1, changes);
            Assert.Equal(1, rig.Host.Options.Bans.Count);
            Assert.Equal("steam:9001", rig.Host.Options.Bans.Entries[0].Key);
            Assert.Equal("Anna", rig.Host.Options.Bans.Entries[0].Name);
            Assert.True(chats.Exists(c => c.IsSystem && c.Text == "Anna was banned"), "the others are told");
            Assert.Equal(ClientState.Live, ben.State);

            var (again, _, _) = rig.AddClient("Anna", steamId: 9001);
            rig.Run(60);
            Assert.Equal(ClientEndKind.Refused, again.EndKind);
            Assert.Equal(RefuseReason.Banned, again.RefusedFor);

            Assert.True(rig.Host.Unban("steam:9001"));
            Assert.False(rig.Host.Unban("steam:9001"));
            Assert.Equal(2, changes);
            var (back, _, _) = rig.AddClient("Anna", steamId: 9001);
            rig.Run(400);
            Assert.Equal(ClientState.Live, back.State, back.EndReason);
        }

        [Test]
        public void APlayerWithoutSteamIdentityIsBannedByAddress()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });
            var transport = rig.HostTransport.CreateClient(DirectEve);
            var eve = new SessionClient(transport, new FakeInfo(), new FakeLoader(), new ClientOptions { PlayerName = "Eve", Password = "pw" }, rig.Clock, rig.Log);
            eve.Connect(new Endpoint());
            rig.Clients.Add((eve, new FakeLoader(), new FakeInfo()));
            rig.Run(400);
            Assert.Equal(ClientState.Live, eve.State, eve.EndReason);
            Assert.True(rig.Host.Ban(eve.LocalPeer, null));
            rig.Run(20);
            Assert.Equal(ClientEndKind.Banned, eve.EndKind);
            Assert.Equal("ip:203.0.113.5", rig.Host.Options.Bans.Entries[0].Key);

            var sameAddress = Handshake(rig, new RawPeer(rig, new PeerIdentity(0, "203.0.113.5:40999", "Eve2", true), 0));
            Assert.Equal(RefuseReason.Banned, ReasonOf(sameAddress), "a new port does not help");
            Assert.Null(sameAddress.Challenge(), "refused before the password");
        }

        [Test]
        public void AnAddedBanDropsAMatchingConnection()
        {
            var rig = new SessionRig();
            var (anna, _, _) = rig.AddClient("Anna", steamId: 9101);
            rig.Run(400);
            rig.Host.Options.Bans.Add(9101, null, "Anna", 0);
            rig.Host.EnforceBans();
            rig.Run(20);
            Assert.Equal(ClientEndKind.Banned, anna.EndKind);
        }

        [Test]
        public void TheBanListRoundTripsAndNormalizesAddresses()
        {
            Assert.Equal("203.0.113.7", BanList.AddressHost("203.0.113.7:27015"));
            Assert.Equal("2001:db8::1", BanList.AddressHost("[2001:DB8::1]:27015"));
            Assert.Equal("2001:db8::1", BanList.AddressHost("2001:db8::1"));
            Assert.Equal("203.0.113.7", BanList.AddressHost("[::ffff:203.0.113.7]:27015"));
            Assert.Null(BanList.AddressHost("  "));
            var list = new BanList();
            Assert.True(list.Add(76561190000000001UL, "203.0.113.1:1", "Tab\tName\nX", 1700000000));
            Assert.False(list.Add(76561190000000001UL, null, "again", 0), "no duplicates");
            Assert.True(list.Add(0, "203.0.113.9:4000", "", 5));
            Assert.False(list.Add(0, null, "nobody", 0), "nothing to ban");
            Assert.True(list.IsBanned(76561190000000001UL, null));
            Assert.True(list.IsBanned(0, "203.0.113.9:1"));
            Assert.False(list.IsBanned(0, "203.0.113.1:1"), "a Steam ban is not an address ban");
            Assert.False(list.IsBanned(42, null));
            BanList back = BanList.Parse(list.Serialize() + "\ngarbage\nsteam\tnot-a-number\t0\tx\nip\t\t0\t\n");
            Assert.Equal(2, back.Count);
            Assert.Equal("steam:76561190000000001", back.Entries[0].Key);
            Assert.Equal("Tab Name X", back.Entries[0].Name);
            Assert.Equal(1700000000L, back.Entries[0].BannedAt);
            Assert.Equal("ip:203.0.113.9", back.Entries[1].Key);
            Assert.Equal(0, back.Version, "a parsed list is unchanged");
            Assert.True(back.Remove("IP:203.0.113.9"));
            Assert.Equal(1, back.Version);
            Assert.Equal(0, BanList.Parse(null).Count);
        }

        // ---------------------------------------------------------------------------------------------- join policy

        [Test]
        public void FriendsOnlyRefusesStrangersAsNotInvited()
        {
            var friends = new HashSet<ulong> { 9201 };
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw", JoinPolicy = JoinPolicy.FriendsAndInvited, IsFriendOrInvited = friends.Contains });
            var stranger = Handshake(rig, new RawPeer(rig, "Stranger", 9202));
            Assert.Equal(RefuseReason.NotInvited, ReasonOf(stranger));
            Assert.Null(stranger.Challenge(), "not even challenged");
            var friend = Handshake(rig, new RawPeer(rig, "Friend", 9201));
            Assert.NotNull(friend.Challenge(), "a friend still needs the password");
            friend.AnswerChallenge("pw");
            Pump(rig, new[] { friend });
            Assert.True(friend.Got(MessageKind.Welcome));
            // a direct joiner without Steam identity is not judged by the friends list (the password protects it)
            var direct = Handshake(rig, new RawPeer(rig, DirectEve, 0));
            Assert.NotNull(direct.Challenge());

            // the policy is read live; a throwing check fails closed
            rig.Host.Options.IsFriendOrInvited = id => throw new InvalidOperationException("Steam is gone");
            var unknown = Handshake(rig, new RawPeer(rig, "Unknown", 9203));
            Assert.Equal(RefuseReason.NotInvited, ReasonOf(unknown));
            rig.Host.Options.JoinPolicy = JoinPolicy.Open;
            var open = Handshake(rig, new RawPeer(rig, "Open", 9204));
            Assert.NotNull(open.Challenge());
        }

        // ------------------------------------------------------------------------------------------ shutting down

        [Test]
        public void StoppingTellsJoinersTheHostIsShuttingDown()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw" });
            var (anna, _, _) = rig.AddClient("Anna", "pw", steamId: 9301);
            rig.Run(400);
            var joiner = Handshake(rig, new RawPeer(rig, "Joiner", 9302));
            Assert.NotNull(joiner.Challenge());
            rig.Host.Stop("The host left the city.");
            for (int i = 0; i < 10; i++)
            {
                anna.Tick();
                joiner.Poll();
            }
            Assert.Equal(RefuseReason.HostShuttingDown, ReasonOf(joiner));
            Assert.Equal("The host left the city.", joiner.Refusal().Detail);
            Assert.Equal(ClientEndKind.SessionEnded, anna.EndKind);
        }

        // ------------------------------------------------------------------------------------------------ co-hosts

        [Test]
        public void ModerationRulesFollowTheRoles()
        {
            SessionRole H = SessionRole.Host, C = SessionRole.CoHost, B = SessionRole.Builder, V = SessionRole.Viewer;
            foreach (SessionRole target in new[] { C, B, V })
            {
                Assert.True(Moderation.CanKick(H, target), "host kicks " + target);
                Assert.True(Moderation.CanBan(H, target), "host bans " + target);
            }
            Assert.False(Moderation.CanKick(H, H));
            Assert.True(Moderation.CanKick(C, B));
            Assert.True(Moderation.CanKick(C, V));
            Assert.False(Moderation.CanKick(C, C), "co-hosts do not remove each other");
            Assert.False(Moderation.CanKick(C, H));
            Assert.False(Moderation.CanBan(C, B), "only the host bans");
            Assert.False(Moderation.CanKick(B, V));
            Assert.False(Moderation.CanKick(V, B));
            Assert.True(Moderation.CanSetRole(H, B, C), "the host appoints co-hosts");
            Assert.True(Moderation.CanSetRole(H, C, B));
            Assert.False(Moderation.CanSetRole(H, B, H), "nobody becomes host");
            Assert.True(Moderation.CanSetRole(C, B, V));
            Assert.True(Moderation.CanSetRole(C, V, B));
            Assert.False(Moderation.CanSetRole(C, B, C), "co-hosts cannot appoint co-hosts");
            Assert.False(Moderation.CanSetRole(C, C, B), "or demote one");
            Assert.False(Moderation.CanSetRole(B, V, B));
            Assert.False(Moderation.CanSetRole(H, B, (SessionRole)77));
            Assert.True(Moderation.CanManageSession(H));
            Assert.False(Moderation.CanManageSession(C));
        }

        [Test]
        public void ACoHostModeratesBuildersAndViewersOnly()
        {
            var rig = new SessionRig();
            var (co, _, _) = rig.AddClient("Co", steamId: 9401);
            var (ben, _, _) = rig.AddClient("Ben", steamId: 9402);
            var (vic, _, _) = rig.AddClient("Vic", steamId: 9403);
            var (co2, _, _) = rig.AddClient("Co2", steamId: 9404);
            rig.Run(500);
            foreach (SessionClient c in new[] { co, ben, vic, co2 }) Assert.Equal(ClientState.Live, c.State, c.EndReason);
            Assert.True(rig.Host.SetRole(co.LocalPeer, SessionRole.CoHost));
            Assert.True(rig.Host.SetRole(co2.LocalPeer, SessionRole.CoHost));
            rig.Run(30);
            Assert.Equal(SessionRole.CoHost, co.LocalRole, "the player list told the co-host");

            // what a co-host may not do is ignored
            Assert.True(co.RequestKick(PeerId.Host));
            co.RequestKick(co2.LocalPeer);
            co.RequestRole(ben.LocalPeer, SessionRole.CoHost);
            co.RequestRole(co2.LocalPeer, SessionRole.Viewer);
            co.RequestKick(co.LocalPeer);
            ben.RequestKick(vic.LocalPeer); // a builder moderates nobody
            rig.Run(30);
            Assert.Equal(ClientState.Live, co2.State);
            Assert.Equal(ClientState.Live, vic.State);
            Assert.Equal(SessionRole.Builder, rig.Host.GetRole(ben.LocalPeer));
            Assert.Equal(SessionRole.CoHost, rig.Host.GetRole(co2.LocalPeer));
            Assert.Equal(ClientState.Live, co.State);

            // what it may do happens
            co.RequestRole(ben.LocalPeer, SessionRole.Viewer);
            co.RequestKick(vic.LocalPeer);
            rig.Run(30);
            Assert.Equal(SessionRole.Viewer, rig.Host.GetRole(ben.LocalPeer));
            Assert.Equal(ClientEndKind.Kicked, vic.EndKind);
            Assert.Contains("Removed by Co", vic.EndReason);

            // demoted, the former co-host loses the rights
            Assert.True(rig.Host.SetRole(co.LocalPeer, SessionRole.Builder));
            rig.Run(30);
            co.RequestKick(ben.LocalPeer);
            rig.Run(30);
            Assert.Equal(ClientState.Live, ben.State);
        }

        // ---------------------------------------------------------------------------------- removed after loading

        [Test]
        public void APlayerRemovedAfterLoadingSeesTheRemovalNotAnEnd()
        {
            var rig = new SessionRig();
            var (anna, _, _) = rig.AddClient("Anna", steamId: 9501);
            var (ben, _, _) = rig.AddClient("Ben", steamId: 9502);
            rig.Run(400);
            rig.Host.Kick(anna.LocalPeer, "Removed by the host");
            Assert.True(rig.Host.Ban(ben.LocalPeer, "Banned by the host"));
            rig.Run(20);
            Assert.True(anna.EnteredCity && ben.EnteredCity);
            var annaRecovery = new RejoinController();
            Assert.True(annaRecovery.OnClientEnded(anna.EndKind, anna.EndReason, anna.EnteredCity, 0));
            Assert.Equal(RejoinPhase.NeedsChoice, annaRecovery.Phase);
            Assert.Equal(ClientEndKind.Kicked, annaRecovery.StartKind, "the panel shows \"removed\", not \"ended\"");
            Assert.False(annaRecovery.CanReconnect);
            var benRecovery = new RejoinController();
            Assert.True(benRecovery.OnClientEnded(ben.EndKind, ben.EndReason, ben.EnteredCity, 0));
            Assert.Equal(ClientEndKind.Banned, benRecovery.StartKind);
            Assert.False(benRecovery.CanReconnect);

            // a ban during a rejoin attempt of a recovering player also ends in the choice
            var r = new RejoinController();
            r.OnClientEnded(ClientEndKind.ConnectionLost, "lost", true, 0);
            Assert.True(r.Tick(10));
            r.OnClientEnded(ClientEndKind.Banned, "banned", true, 11);
            Assert.Equal(RejoinPhase.NeedsChoice, r.Phase);
            Assert.Equal(ClientEndKind.Banned, r.StartKind);
        }

        [Test]
        public void AResyncRejoinsAtOnce()
        {
            Assert.True(SessionClient.IsRetryable(ClientEndKind.Resync));
            Assert.False(SessionClient.IsRetryable(ClientEndKind.Banned));
            var r = new RejoinController();
            Assert.True(r.OnClientEnded(ClientEndKind.Resync, "fresh copy", true, 50));
            Assert.Equal(RejoinPhase.Waiting, r.Phase);
            Assert.True(r.Tick(50), "no backoff for a resync the player asked for");

            var rig = new SessionRig();
            var (anna, _, _) = rig.AddClient("Anna", steamId: 9601);
            rig.Run(400);
            anna.LeaveForResync(null);
            rig.Run(20);
            Assert.Equal(ClientEndKind.Resync, anna.EndKind);
            Assert.Equal(0, rig.Host.LivePeerCount, "the host saw a polite leave");
        }

        // ------------------------------------------------------------------------------------------------ build id

        [Test]
        public void TheBuildIdIsTheInstallersSha256Prefix()
        {
            // SHA-256("abc") = ba7816bf8f01cfea...; the installer prints the first 12 hex digits
            Assert.Equal("ba7816bf8f01", BuildId.Format(BuildId.OfBytes(Encoding.ASCII.GetBytes("abc"))));
            Assert.True(BuildId.TryParse("2f076aad4234", out ulong v));
            Assert.Equal("2f076aad4234", BuildId.Format(v));
            Assert.False(BuildId.TryParse("2f076aad423", out _));
            Assert.False(BuildId.TryParse("zz076aad4234", out _));
            Assert.Equal("000000000000", BuildId.Format(0));
            Assert.Equal("ffffffffffff", BuildId.Format(ulong.MaxValue), "only 48 bits");
            var report = new MismatchReport { Kind = "build", HostBuild = "2f076aad4234", ClientBuild = "0123456789ab" };
            MismatchReport back = MismatchReport.Decode(MismatchReport.Encode(report));
            Assert.Equal("2f076aad4234", back.HostBuild);
            Assert.Equal("0123456789ab", back.ClientBuild);
        }
    }
}
