using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Protocol.ProtocolTestUtil;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>
    /// Golden byte vectors of every message and control payload codec (ARCHITECTURE.md#envelope-and-message-kinds): with the record vectors they
    /// freeze the layouts of the current ProtocolVersion. Each vector is written by hand from the documented layout (comments name the fields),
    /// checked against the writer and decoded back.
    /// </summary>
    public sealed class MessageGoldenTests
    {
        private static byte[] Write<T>(Action<ByteWriter, T> write, T message)
        {
            var w = new ByteWriter();
            write(w, message);
            return w.ToArray();
        }

        internal static HandshakeDigests SampleDigests() => new HandshakeDigests
        {
            ProtocolVersion = 4, ModBuild = 0x0102030405060708UL, GameVersion = "1.2", GameFullVersion = "1.2.3f1",
            Mods = 0x44, Content = 0x55, AssetPacks = 0x66, ModSettings = 0x77, Locale = "en-US"
        };

        // varint 4 | u64 modBuild | "1.2" | "1.2.3f1" | u64 mods, content, assetPacks, modSettings | "en-US"
        private const string DigestsHex =
            "04 | 0807060504030201 | 03 312e32 | 07 312e322e336631 | 4400000000000000 5500000000000000 6600000000000000 7700000000000000 | 05 656e2d5553";

        internal static void AssertDigests(HandshakeDigests expected, HandshakeDigests actual)
        {
            Assert.Equal(expected.ProtocolVersion, actual.ProtocolVersion);
            Assert.Equal(expected.ModBuild, actual.ModBuild);
            Assert.Equal(expected.GameVersion ?? "", actual.GameVersion);
            Assert.Equal(expected.GameFullVersion ?? "", actual.GameFullVersion);
            Assert.Equal(expected.Mods, actual.Mods);
            Assert.Equal(expected.Content, actual.Content);
            Assert.Equal(expected.AssetPacks, actual.AssetPacks);
            Assert.Equal(expected.ModSettings, actual.ModSettings);
            Assert.Equal(expected.Locale ?? "", actual.Locale);
        }

        [Test]
        public void Hello()
        {
            // digests | u64 steamId | "Ann" | nonce aa bb | compression mask {None, ManagedLz4, Zstd} | join flags {AcceptModMismatch}
            string hex = DigestsHex + " | 0100000001001001 | 03 416e6e | 02 aabb | 07 | 01";
            var m = new HelloMessage
            {
                Digests = SampleDigests(), SteamId = 0x0110000100000001UL, DisplayName = "Ann", ClientNonce = new byte[] { 0xAA, 0xBB },
                CompressionMask = CompressionNegotiation.LocalMask(true, false), JoinFlags = HelloJoinFlags.AcceptModMismatch
            };
            AssertBytes(hex, Write<HelloMessage>(HelloMessage.Write, m));
            byte[] bytes = Hex(hex);
            HelloMessage read = HelloMessage.Decode(bytes, 0, bytes.Length);
            AssertDigests(m.Digests, read.Digests);
            Assert.Equal(m.SteamId, read.SteamId);
            Assert.Equal("Ann", read.DisplayName);
            Assert.SequenceEqual(m.ClientNonce, read.ClientNonce);
            Assert.Equal((byte)7, read.CompressionMask);
            Assert.Equal(HelloJoinFlags.AcceptModMismatch, read.JoinFlags);
            Assert.Equal(4, HelloMessage.PeekProtocolVersion(bytes, 0, bytes.Length));
            Assert.Equal((byte)0x01, HelloJoinFlags.AcceptModMismatch, "the bit is pinned on the wire");
        }

        [Test]
        public void Welcome()
        {
            // u32 sessionId | peer 2 | role Builder | permissions Build|Demolish|Chat (2051) | settings 01 02 03 | no host nonce
            // | 2 entries | 0x01020304 | 0xffffffff | compression Zstd
            const string hex = "efbeadde | 02 | 02 | 8310 | 03 010203 | 00 | 02 04030201 ffffffff | 02";
            var m = new WelcomeMessage
            {
                SessionId = 0xDEADBEEF, AssignedPeer = new PeerId(2), Role = SessionRole.Builder,
                Permissions = Permission.Build | Permission.Demolish | Permission.Chat, SessionSettings = new byte[] { 1, 2, 3 },
                HostNonce = null, EnabledSysEntries = new uint[] { 0x01020304, 0xFFFFFFFF }, Compression = CompressionKind.Zstd
            };
            AssertBytes(hex, Write<WelcomeMessage>(WelcomeMessage.Write, m));
            byte[] bytes = Hex(hex);
            WelcomeMessage read = WelcomeMessage.Decode(bytes, 0, bytes.Length);
            Assert.Equal(m.SessionId, read.SessionId);
            Assert.Equal(m.AssignedPeer, read.AssignedPeer);
            Assert.Equal(SessionRole.Builder, read.Role);
            Assert.Equal(m.Permissions, read.Permissions);
            Assert.SequenceEqual(m.SessionSettings, read.SessionSettings);
            Assert.Equal(0, read.HostNonce.Length, "null is read back as empty");
            Assert.SequenceEqual(m.EnabledSysEntries, read.EnabledSysEntries);
            Assert.Equal(CompressionKind.Zstd, read.Compression);
        }

        [Test]
        public void Refuse()
        {
            // reason Mismatch | "Mods differ" | report 09
            const string hex = "00 | 0b 4d6f647320646966666572 | 01 09";
            var m = new RefuseMessage { Reason = RefuseReason.Mismatch, Detail = "Mods differ", MismatchReport = new byte[] { 9 } };
            AssertBytes(hex, Write<RefuseMessage>(RefuseMessage.Write, m));
            byte[] bytes = Hex(hex);
            RefuseMessage read = RefuseMessage.Decode(bytes, 0, bytes.Length);
            Assert.Equal(RefuseReason.Mismatch, read.Reason);
            Assert.Equal("Mods differ", read.Detail);
            Assert.SequenceEqual(new byte[] { 9 }, read.MismatchReport);

            // the enum values are pinned on the wire
            Assert.Equal(7, (int)RefuseReason.HostPreparing);
            Assert.Equal(6, (int)RefuseReason.ProtocolVersion);
        }

        [Test]
        public void ManifestDetail()
        {
            // digests | mods: 1 entry ("Traffic", "1.0", "123", "ab", flags RegistersTypesOrSystemsOrPrefabs)
            // | content: 1 entry (id "1", "DLC1", version "", digest 0, prefabs 0)
            // | assetPacks: 1 entry (id "75426", "Bus", "1.2", digest, prefabs 300) | settings: 1 entry ("M", "K", "V", flags 3)
            string hex = DigestsHex
                         + " | 02 07 54726166666963 03 312e30 03 313233 02 6162 02"
                         + " | 02 01 31 04 444c4331 00 0000000000000000 00"
                         + " | 02 05 3735343236 03 427573 03 312e32 0807060504030201 ac02"
                         + " | 02 01 4d 01 4b 01 56 03";
            var manifest = new HandshakeManifest
            {
                Digests = SampleDigests(),
                Mods = new List<ModEntry> { new ModEntry { Name = "Traffic", Version = "1.0", PlatformId = "123", DllHash = "ab", RegistersTypesOrSystemsOrPrefabs = true } },
                Content = new List<ContentEntry> { new ContentEntry { Id = "1", Name = "DLC1" } },
                AssetPacks = new List<ContentEntry> { new ContentEntry { Id = "75426", Name = "Bus", Version = "1.2", Digest = 0x0102030405060708UL, Prefabs = 300 } },
                Settings = new List<SettingEntry> { new SettingEntry { Mod = "M", Key = "K", Value = "V", SimulationRelevant = true, Pushable = true } },
            };
            AssertBytes(hex, Write<ManifestDetailMessage>(ManifestDetailMessage.Write, new ManifestDetailMessage { Manifest = manifest }));
            byte[] bytes = Hex(hex);
            HandshakeManifest read = ManifestDetailMessage.Decode(bytes, 0, bytes.Length).Manifest;
            AssertDigests(manifest.Digests, read.Digests);
            Assert.Equal(1, read.Mods.Count);
            Assert.Equal("Traffic", read.Mods[0].Name);
            Assert.Equal("1.0", read.Mods[0].Version);
            Assert.Equal("123", read.Mods[0].PlatformId);
            Assert.Equal("ab", read.Mods[0].DllHash);
            Assert.False(read.Mods[0].IsUiModule);
            Assert.True(read.Mods[0].RegistersTypesOrSystemsOrPrefabs);
            Assert.Equal("1", read.Content[0].Id);
            Assert.Equal("DLC1", read.Content[0].Name);
            Assert.Equal("", read.Content[0].Version);
            Assert.Equal("75426", read.AssetPacks[0].Id);
            Assert.Equal("Bus", read.AssetPacks[0].Name);
            Assert.Equal("1.2", read.AssetPacks[0].Version);
            Assert.Equal(0x0102030405060708UL, read.AssetPacks[0].Digest);
            Assert.Equal(300, read.AssetPacks[0].Prefabs);
            Assert.Equal("M", read.Settings[0].Mod);
            Assert.Equal("K", read.Settings[0].Key);
            Assert.Equal("V", read.Settings[0].Value);
            Assert.True(read.Settings[0].SimulationRelevant);
            Assert.True(read.Settings[0].Pushable);

            // null lists ("not provided") survive
            byte[] empty = Write<ManifestDetailMessage>(ManifestDetailMessage.Write, new ManifestDetailMessage
            {
                Manifest = new HandshakeManifest { Digests = SampleDigests(), Mods = null, Content = null, AssetPacks = null, Settings = null }
            });
            AssertBytes(DigestsHex + " | 00 | 00 | 00 | 00", empty);
            HandshakeManifest none = ManifestDetailMessage.Decode(empty, 0, empty.Length).Manifest;
            Assert.Null(none.Mods);
            Assert.Null(none.Content);
            Assert.Null(none.AssetPacks);
            Assert.Null(none.Settings);
        }

        [Test]
        public void PasswordChallengeAndResponse()
        {
            // 16-byte host nonce | salt aa bb cc dd | u32 sessionId
            const string challengeHex = "10 000102030405060708090a0b0c0d0e0f | 04 aabbccdd | 04030201";
            var nonce = new byte[16];
            for (int i = 0; i < 16; i++) nonce[i] = (byte)i;
            var challenge = new PasswordChallengeMessage { HostNonce = nonce, Salt = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, SessionId = 0x01020304 };
            AssertBytes(challengeHex, Write<PasswordChallengeMessage>(PasswordChallengeMessage.Write, challenge));
            byte[] bytes = Hex(challengeHex);
            PasswordChallengeMessage readChallenge = PasswordChallengeMessage.Decode(bytes, 0, bytes.Length);
            Assert.SequenceEqual(nonce, readChallenge.HostNonce);
            Assert.SequenceEqual(challenge.Salt, readChallenge.Salt);
            Assert.Equal(0x01020304u, readChallenge.SessionId);

            // client nonce 11 22 | mac 33 44 55
            const string responseHex = "02 1122 | 03 334455";
            var response = new PasswordResponseMessage { ClientNonce = new byte[] { 0x11, 0x22 }, Mac = new byte[] { 0x33, 0x44, 0x55 } };
            AssertBytes(responseHex, Write<PasswordResponseMessage>(PasswordResponseMessage.Write, response));
            bytes = Hex(responseHex);
            PasswordResponseMessage readResponse = PasswordResponseMessage.Decode(bytes, 0, bytes.Length);
            Assert.SequenceEqual(response.ClientNonce, readResponse.ClientNonce);
            Assert.SequenceEqual(response.Mac, readResponse.Mac);
        }

        [Test]
        public void Chat()
        {
            // from 3 | "hi" | system
            const string hex = "03 | 02 6869 | 01";
            AssertBytes(hex, Write<ChatMessage>(ChatMessage.Write, new ChatMessage { From = new PeerId(3), Text = "hi", IsSystem = true }));
            byte[] bytes = Hex(hex);
            ChatMessage read = ChatMessage.Decode(bytes, 0, bytes.Length);
            Assert.Equal(new PeerId(3), read.From);
            Assert.Equal("hi", read.Text);
            Assert.True(read.IsSystem);
        }

        [Test]
        public void View()
        {
            // f32 pivot 1, -2 | zoom 100 | yaw 0.5 | camera 0, 10, -1 | infoview 5 | infomodes 0x101 | selected 300 | followed none
            // | panel flags 0x0102 | panel target 1
            const string hex = "0000803f 000000c0 0000c842 0000003f | 00000000 00002041 000080bf | 05 | 01010000 | ac02 | 00 | 0201 | 01";
            var m = new ViewReport
            {
                PivotX = 1f, PivotZ = -2f, Zoom = 100f, Yaw = 0.5f, CamX = 0f, CamY = 10f, CamZ = -1f, Infoview = 5, InfomodeMask = 0x101,
                Selected = new NetSlot(300), Followed = NetSlot.None, PanelFlags = 0x0102, PanelTarget = new NetSlot(1)
            };
            AssertBytes(hex, Write<ViewReport>(ViewReport.Write, m));
            byte[] bytes = Hex(hex);
            ViewReport read = ViewReport.Decode(bytes, 0, bytes.Length);
            Assert.Equal(1f, read.PivotX);
            Assert.Equal(-2f, read.PivotZ);
            Assert.Equal(100f, read.Zoom);
            Assert.Equal(0.5f, read.Yaw);
            Assert.Equal(0f, read.CamX);
            Assert.Equal(10f, read.CamY);
            Assert.Equal(-1f, read.CamZ);
            Assert.Equal((byte)5, read.Infoview);
            Assert.Equal(0x101u, read.InfomodeMask);
            Assert.Equal(new NetSlot(300), read.Selected);
            Assert.Equal(NetSlot.None, read.Followed);
            Assert.Equal((ushort)0x0102, read.PanelFlags);
            Assert.Equal(new NetSlot(1), read.PanelTarget);
        }

        [Test]
        public void Presence()
        {
            // from 1 | cursor 1, 2, 3 | tool 4 | prefab 9+1 | color 0xff00ff00 | brush 8 | 3 preview bytes (opaque)
            const string hex = "01 | 0000803f 00000040 00004040 | 04 | 0a | 00ff00ff | 00000041 | 03 aabbcc";
            var m = new PresenceMessage
            {
                From = new PeerId(1), CursorX = 1f, CursorY = 2f, CursorZ = 3f, ToolKind = 4, Prefab = new PrefabRank(9), Color = 0xFF00FF00,
                Preview = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, PreviewLength = 3, BrushRadius = 8f
            };
            AssertBytes(hex, Write<PresenceMessage>(PresenceMessage.Write, m));
            byte[] bytes = Hex(hex);
            PresenceMessage read = PresenceMessage.Decode(bytes, 0, bytes.Length);
            Assert.Equal(new PeerId(1), read.From);
            Assert.Equal(3f, read.CursorZ);
            Assert.Equal((byte)4, read.ToolKind);
            Assert.Equal(new PrefabRank(9), read.Prefab);
            Assert.Equal(0xFF00FF00u, read.Color);
            Assert.Equal(3, read.PreviewLength);
            Assert.SequenceEqual(new byte[] { 0xAA, 0xBB, 0xCC }, read.Preview);
            Assert.Equal(8f, read.BrushRadius);

            // no prefab is written as 0, no preview as an empty one
            m.Prefab = PrefabRank.None;
            m.Preview = null;
            AssertBytes("01 | 0000803f 00000040 00004040 | 04 | 00 | 00ff00ff | 00000041 | 00", Write<PresenceMessage>(PresenceMessage.Write, m));
        }

        [Test]
        public void AckAndPing()
        {
            // varint sequence 7 | f64 sent 2.0 | reply
            const string pingHex = "07 | 0000000000000040 | 01";
            AssertBytes(pingHex, Write<PingMessage>(PingMessage.Write, new PingMessage { Sequence = 7, SentSeconds = 2.0, IsReply = true }));
            byte[] ping = Hex(pingHex);
            PingMessage readPing = PingMessage.Decode(ping, 0, ping.Length);
            Assert.Equal(7u, readPing.Sequence);
            Assert.Equal(2.0, readPing.SentSeconds);
            Assert.True(readPing.IsReply);
        }

        [Test]
        public void SnapshotManifest()
        {
            // u32 id 9 | varint64 size 300 | digest lo 1 | digest hi 2 | varint chunk 256 KiB | varint epoch 5 | u32 frame 256
            // | u32 hostBuildOrderNext 7 | "Normal" | renderPrefabIds ["A"] | referencedContent null
            // | familyCounts: 2 entries (NetEdge 2, Object 300)
            const string hex = "09000000 | ac02 | 0100000000000000 | 0200000000000000 | 808010 | 05 | 00010000 | 07000000"
                               + " | 06 4e6f726d616c | 02 01 41 | 00 | 03 0a 02 0d ac02";
            var m = new SnapshotManifest
            {
                SnapshotId = 9, Size = 300, DigestLo = 1, DigestHi = 2, ChunkSize = 256 * 1024, Epoch = new EpochId(5), FrameIndex = 256,
                HostBuildOrderNext = 7, GameModeName = "Normal", RenderPrefabIds = new[] { "A" }, ReferencedContent = null,
                FamilyCounts = new Dictionary<Family, int> { { Family.Object, 300 }, { Family.NetEdge, 2 } }
            };
            AssertBytes(hex, Write<SnapshotManifest>(SnapshotManifestCodec.Write, m));
            byte[] bytes = Hex(hex);
            SnapshotManifest read = SnapshotManifestCodec.Decode(bytes, 0, bytes.Length);
            Assert.Equal(9u, read.SnapshotId);
            Assert.Equal(300L, read.Size);
            Assert.Equal(1UL, read.DigestLo);
            Assert.Equal(2UL, read.DigestHi);
            Assert.Equal(256 * 1024, read.ChunkSize);
            Assert.Equal(new EpochId(5), read.Epoch);
            Assert.Equal(256u, read.FrameIndex);
            Assert.Equal(7u, read.HostBuildOrderNext);
            Assert.Equal("Normal", read.GameModeName);
            Assert.SequenceEqual(new[] { "A" }, read.RenderPrefabIds);
            Assert.Null(read.ReferencedContent);
            Assert.Equal(2, read.FamilyCounts.Count);
            Assert.Equal(2, read.FamilyCounts[Family.NetEdge]);
            Assert.Equal(300, read.FamilyCounts[Family.Object]);
        }
    }
}
