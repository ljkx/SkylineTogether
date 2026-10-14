using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Protocol.ProtocolTestUtil;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>
    /// Message codecs: random round trips, the whole-body check of <c>Decode</c>, free-text rules (truncate when written,
    /// reject above the limit and strip control characters when read), limits and enum validation on both sides.
    /// </summary>
    public sealed class MessageRoundTripTests
    {
        private static byte[] Encode<T>(Action<ByteWriter, T> write, T message)
        {
            var w = new ByteWriter();
            write(w, message);
            return w.ToArray();
        }

        private static byte[] RandomBytes(DeterministicRandom random, int max)
        {
            var bytes = new byte[random.Next(max + 1)];
            random.NextBytes(bytes, 0, bytes.Length);
            return bytes;
        }

        private static string RandomText(DeterministicRandom random, int maxChars)
        {
            const string alphabet = "abcXYZ 019-_.éßŝ中文";
            var chars = new char[random.Next(maxChars + 1)];
            for (int i = 0; i < chars.Length; i++) chars[i] = alphabet[random.Next(alphabet.Length)];
            return new string(chars);
        }

        private static float RandomFloat(DeterministicRandom random) => (float)((random.NextDouble() - 0.5) * 1e6);

        private static byte[] Prefix(byte[] data, int length)
        {
            var copy = new byte[length];
            if (length > 0) Array.Copy(data, copy, length);
            return copy;
        }

        private static NetSlot RandomSlot(DeterministicRandom random, bool allowNone)
        {
            uint value = random.Next(4) == 0 ? (uint)random.Next(1, 200) : random.NextUInt() & NetSlot.MaxValue;
            if (value == 0 && !allowNone) value = 1;
            return new NetSlot(value);
        }

        private static void AssertTrailingByteRejected<T>(byte[] encoded, Func<byte[], int, int, T> decode)
        {
            var longer = new byte[encoded.Length + 1];
            Array.Copy(encoded, longer, encoded.Length);
            Assert.Throws<DecodeException>(() => decode(longer, 0, longer.Length), "Decode rejects a trailing byte");
        }

        private static void AssertEveryTruncationRejected<T>(byte[] encoded, Func<byte[], int, int, T> decode)
        {
            for (int cut = 0; cut < encoded.Length; cut++)
            {
                Assert.Throws<DecodeException>(() => decode(encoded, 0, cut), "truncated to " + cut + " of " + encoded.Length + " bytes");
            }
        }

        [Test]
        public void HelloRoundTripsAndValidates()
        {
            var random = new DeterministicRandom(1);
            for (int round = 0; round < 200; round++)
            {
                var m = new HelloMessage
                {
                    Digests = new HandshakeDigests
                    {
                        ProtocolVersion = random.Next(0, int.MaxValue), ModBuild = random.NextULong(), GameVersion = RandomText(random, 30),
                        GameFullVersion = RandomText(random, 60), FormatTags = random.NextULong(), Catalog = random.NextULong(),
                        Prefabs = random.NextULong(), Mods = random.NextULong(), Content = random.NextULong(), ModSettings = random.NextULong(),
                        DataFiles = random.NextULong(), Locale = RandomText(random, 10)
                    },
                    SteamId = random.NextULong(), DisplayName = RandomText(random, 64), ClientNonce = RandomBytes(random, 64),
                    CompressionMask = (byte)random.Next(16)
                };
                byte[] bytes = Encode<HelloMessage>(HelloMessage.Write, m);
                HelloMessage read = HelloMessage.Decode(bytes, 0, bytes.Length);
                MessageGoldenTests.AssertDigests(m.Digests, read.Digests);
                Assert.Equal(m.SteamId, read.SteamId);
                Assert.Equal(m.DisplayName, read.DisplayName);
                Assert.SequenceEqual(m.ClientNonce, read.ClientNonce);
                Assert.Equal(m.CompressionMask, read.CompressionMask);
                Assert.Equal(m.Digests.ProtocolVersion, HelloMessage.PeekProtocolVersion(bytes, 0, bytes.Length));
                if (round == 0)
                {
                    AssertTrailingByteRejected(bytes, HelloMessage.Decode);
                    AssertEveryTruncationRejected(bytes, HelloMessage.Decode);
                }
            }

            // free text: truncated to 64 characters when written, control characters stripped when read
            var hello = new HelloMessage { Digests = MessageGoldenTests.SampleDigests(), DisplayName = new string('n', 100) + (char)0x202E + "evil" };
            byte[] encoded = Encode<HelloMessage>(HelloMessage.Write, hello);
            Assert.Equal(new string('n', 64), HelloMessage.Decode(encoded, 0, encoded.Length).DisplayName);
            hello.DisplayName = "A" + (char)0x07 + "n" + (char)0x202E + "n\r\n";
            encoded = Encode<HelloMessage>(HelloMessage.Write, hello);
            Assert.Equal("Ann", HelloMessage.Decode(encoded, 0, encoded.Length).DisplayName);
            hello.DisplayName = null;
            encoded = Encode<HelloMessage>(HelloMessage.Write, hello);
            Assert.Equal("", HelloMessage.Decode(encoded, 0, encoded.Length).DisplayName);

            // undefined compression bits, oversized nonce
            hello.CompressionMask = 0x10;
            Assert.Throws<ArgumentException>(() => Encode<HelloMessage>(HelloMessage.Write, hello));
            hello.CompressionMask = 0;
            hello.ClientNonce = new byte[HelloMessage.MaxNonceBytes + 1];
            Assert.Throws<ArgumentException>(() => Encode<HelloMessage>(HelloMessage.Write, hello));
            hello.ClientNonce = null;
            encoded = Encode<HelloMessage>(HelloMessage.Write, hello);
            encoded[encoded.Length - 1] = 0x10;
            Assert.Throws<DecodeException>(() => HelloMessage.Decode(encoded, 0, encoded.Length), "undefined mask bit");

            // a display name above 64 characters (only a hostile peer writes one) is rejected
            var w = new ByteWriter();
            HandshakeCodec.WriteDigests(w, MessageGoldenTests.SampleDigests());
            w.WriteUInt64(1);
            w.WriteString(new string('x', 65), 1000);
            w.WriteVarUInt32(0);
            w.WriteByte(0);
            byte[] hostile = w.ToArray();
            Assert.Throws<DecodeException>(() => HelloMessage.Decode(hostile, 0, hostile.Length));

            Assert.Throws<DecodeException>(() => HelloMessage.PeekProtocolVersion(new byte[0], 0, 0));
            Assert.Throws<DecodeException>(() => HelloMessage.PeekProtocolVersion(Hex("ffffffff7f"), 0, 5), "varint above 32 bits");
            Assert.Throws<DecodeException>(() => HelloMessage.PeekProtocolVersion(Hex("ffffffff0f"), 0, 5), "version above int.MaxValue");
            Assert.Equal(300, HelloMessage.PeekProtocolVersion(Hex("ac02 ffff"), 0, 4), "only the first varint is read");
            Assert.Throws<ArgumentOutOfRangeException>(() => HandshakeCodec.WriteDigests(new ByteWriter(), new HandshakeDigests { ProtocolVersion = -1 }));
        }

        [Test]
        public void WelcomeRoundTripsAndValidates()
        {
            var random = new DeterministicRandom(2);
            for (int round = 0; round < 200; round++)
            {
                var entries = new uint[random.Next(30)];
                for (int i = 0; i < entries.Length; i++) entries[i] = random.NextUInt();
                var m = new WelcomeMessage
                {
                    SessionId = random.NextUInt(), AssignedPeer = new PeerId((byte)random.Next(256)), Role = (SessionRole)random.Next(4),
                    Permissions = (Permission)random.Next(4096), SessionSettings = RandomBytes(random, 3000), HostNonce = RandomBytes(random, 64),
                    EnabledSysEntries = entries, Compression = (CompressionKind)random.Next(4)
                };
                byte[] bytes = Encode<WelcomeMessage>(WelcomeMessage.Write, m);
                WelcomeMessage read = WelcomeMessage.Decode(bytes, 0, bytes.Length);
                Assert.Equal(m.SessionId, read.SessionId);
                Assert.Equal(m.AssignedPeer, read.AssignedPeer);
                Assert.Equal(m.Role, read.Role);
                Assert.Equal(m.Permissions, read.Permissions);
                Assert.SequenceEqual(m.SessionSettings, read.SessionSettings);
                Assert.SequenceEqual(m.HostNonce, read.HostNonce);
                Assert.SequenceEqual(m.EnabledSysEntries, read.EnabledSysEntries);
                Assert.Equal(m.Compression, read.Compression);
                if (round == 0)
                {
                    AssertTrailingByteRejected(bytes, WelcomeMessage.Decode);
                    AssertEveryTruncationRejected(bytes, WelcomeMessage.Decode);
                }
            }

            var welcome = new WelcomeMessage();
            byte[] empty = Encode<WelcomeMessage>(WelcomeMessage.Write, welcome);
            AssertBytes("00000000 00 00 00 00 00 00 00", empty, "defaults: host role, no permissions, uncompressed");
            Assert.Equal(0, WelcomeMessage.Decode(empty, 0, empty.Length).EnabledSysEntries.Length);

            Assert.Throws<ArgumentException>(() => Encode<WelcomeMessage>(WelcomeMessage.Write, new WelcomeMessage { Role = (SessionRole)4 }));
            Assert.Throws<ArgumentException>(() => Encode<WelcomeMessage>(WelcomeMessage.Write, new WelcomeMessage { Permissions = (Permission)4096 }));
            Assert.Throws<ArgumentException>(() => Encode<WelcomeMessage>(WelcomeMessage.Write, new WelcomeMessage { Compression = (CompressionKind)4 }));
            Assert.Throws<ArgumentException>(() => Encode<WelcomeMessage>(WelcomeMessage.Write, new WelcomeMessage { EnabledSysEntries = new uint[WelcomeMessage.MaxSysEntries + 1] }));
            Assert.Throws<ArgumentException>(() => Encode<WelcomeMessage>(WelcomeMessage.Write, new WelcomeMessage { SessionSettings = new byte[WelcomeMessage.MaxSessionSettingsBytes + 1] }));

            void Rejected(string hex, string why)
            {
                byte[] bytes = Hex(hex);
                Assert.Throws<DecodeException>(() => WelcomeMessage.Decode(bytes, 0, bytes.Length), why);
            }
            Rejected("00000000 00 04 00 00 00 00 00", "undefined role");
            Rejected("00000000 00 00 8020 00 00 00 00", "undefined permission bit 4096");
            Rejected("00000000 00 00 00 00 00 00 04", "undefined compression");
            Rejected("00000000 00 00 00 00 00 8120 00", "4097 sys entries");
            Rejected("00000000 00 00 00 00 00 03 01000000 00", "entries overrun the message");
            Rejected("00000000 00 00 00 00 41 00 00", "65-byte host nonce");
        }

        [Test]
        public void RefuseRoundTripsAndAcceptsUnknownReasons()
        {
            var random = new DeterministicRandom(3);
            for (int round = 0; round < 100; round++)
            {
                var m = new RefuseMessage { Reason = (RefuseReason)random.Next(256), Detail = RandomText(random, 1024), MismatchReport = RandomBytes(random, 5000) };
                byte[] bytes = Encode<RefuseMessage>(RefuseMessage.Write, m);
                RefuseMessage read = RefuseMessage.Decode(bytes, 0, bytes.Length);
                Assert.Equal(m.Reason, read.Reason, "unknown reasons are accepted for a readable refusal across versions");
                Assert.Equal(m.Detail, read.Detail);
                Assert.SequenceEqual(m.MismatchReport, read.MismatchReport);
                if (round == 0)
                {
                    AssertTrailingByteRejected(bytes, RefuseMessage.Decode);
                    AssertEveryTruncationRejected(bytes, RefuseMessage.Decode);
                }
            }
            var refuse = new RefuseMessage { Detail = new string('d', 2000), MismatchReport = null };
            byte[] encoded = Encode<RefuseMessage>(RefuseMessage.Write, refuse);
            RefuseMessage back = RefuseMessage.Decode(encoded, 0, encoded.Length);
            Assert.Equal(RefuseMessage.MaxDetailChars, back.Detail.Length, "detail truncated when written");
            Assert.Equal(0, back.MismatchReport.Length);
            Assert.Throws<ArgumentException>(() => Encode<RefuseMessage>(RefuseMessage.Write, new RefuseMessage { MismatchReport = new byte[RefuseMessage.MaxMismatchReportBytes + 1] }));
        }

        private static HandshakeManifest RandomManifest(DeterministicRandom random)
        {
            var m = new HandshakeManifest { Digests = MessageGoldenTests.SampleDigests() };
            m.Mods = random.Next(5) == 0 ? null : new List<ModEntry>();
            int mods = random.Next(10);
            for (int i = 0; m.Mods != null && i < mods; i++)
            {
                m.Mods.Add(new ModEntry
                {
                    Name = RandomText(random, 40), Version = RandomText(random, 10), PlatformId = RandomText(random, 10), DllHash = RandomText(random, 64),
                    IsUiModule = random.NextBool(), RegistersTypesOrSystemsOrPrefabs = random.NextBool()
                });
            }
            m.Content = random.Next(5) == 0 ? null : new List<string>();
            for (int i = 0, n = random.Next(10); m.Content != null && i < n; i++) m.Content.Add(RandomText(random, 30));
            m.FormatTagNames = random.Next(5) == 0 ? null : new List<string>();
            for (int i = 0, n = random.Next(10); m.FormatTagNames != null && i < n; i++) m.FormatTagNames.Add(RandomText(random, 30));
            m.PrefabIds = random.Next(2) == 0 ? null : new List<string>();
            for (int i = 0, n = random.Next(50); m.PrefabIds != null && i < n; i++) m.PrefabIds.Add(RandomText(random, 80));
            m.Settings = random.Next(5) == 0 ? null : new List<SettingEntry>();
            for (int i = 0, n = random.Next(10); m.Settings != null && i < n; i++)
            {
                m.Settings.Add(new SettingEntry { Mod = RandomText(random, 20), Key = RandomText(random, 20), Value = RandomText(random, 100), SimulationRelevant = random.NextBool(), Pushable = random.NextBool() });
            }
            m.DataFiles = random.Next(3) == 0 ? null : new List<DataFileDigestEntry>();
            for (int i = 0, n = random.Next(6); m.DataFiles != null && i < n; i++)
            {
                m.DataFiles.Add(new DataFileDigestEntry { File = RandomText(random, 20), Digest = random.NextULong(), Overridden = random.NextBool() });
            }
            return m;
        }

        private static void AssertManifests(HandshakeManifest expected, HandshakeManifest actual)
        {
            MessageGoldenTests.AssertDigests(expected.Digests, actual.Digests);
            Assert.Equal(expected.Mods == null, actual.Mods == null, "mods null-ness");
            for (int i = 0; expected.Mods != null && i < expected.Mods.Count; i++)
            {
                Assert.Equal(expected.Mods[i].Name, actual.Mods[i].Name);
                Assert.Equal(expected.Mods[i].Version, actual.Mods[i].Version);
                Assert.Equal(expected.Mods[i].PlatformId, actual.Mods[i].PlatformId);
                Assert.Equal(expected.Mods[i].DllHash, actual.Mods[i].DllHash);
                Assert.Equal(expected.Mods[i].IsUiModule, actual.Mods[i].IsUiModule);
                Assert.Equal(expected.Mods[i].RegistersTypesOrSystemsOrPrefabs, actual.Mods[i].RegistersTypesOrSystemsOrPrefabs);
            }
            Assert.Equal(expected.Content == null, actual.Content == null, "content null-ness");
            if (expected.Content != null) Assert.SequenceEqual(expected.Content, actual.Content);
            Assert.Equal(expected.FormatTagNames == null, actual.FormatTagNames == null, "format tags null-ness");
            if (expected.FormatTagNames != null) Assert.SequenceEqual(expected.FormatTagNames, actual.FormatTagNames);
            Assert.Equal(expected.PrefabIds == null, actual.PrefabIds == null, "prefab ids null-ness");
            if (expected.PrefabIds != null) Assert.SequenceEqual(expected.PrefabIds, actual.PrefabIds);
            Assert.Equal(expected.Settings == null, actual.Settings == null, "settings null-ness");
            for (int i = 0; expected.Settings != null && i < expected.Settings.Count; i++)
            {
                Assert.Equal(expected.Settings[i].Mod, actual.Settings[i].Mod);
                Assert.Equal(expected.Settings[i].Key, actual.Settings[i].Key);
                Assert.Equal(expected.Settings[i].Value, actual.Settings[i].Value);
                Assert.Equal(expected.Settings[i].SimulationRelevant, actual.Settings[i].SimulationRelevant);
                Assert.Equal(expected.Settings[i].Pushable, actual.Settings[i].Pushable);
            }
            Assert.Equal(expected.DataFiles == null, actual.DataFiles == null, "data files null-ness");
            for (int i = 0; expected.DataFiles != null && i < expected.DataFiles.Count; i++)
            {
                Assert.Equal(expected.DataFiles[i].File, actual.DataFiles[i].File);
                Assert.Equal(expected.DataFiles[i].Digest, actual.DataFiles[i].Digest);
                Assert.Equal(expected.DataFiles[i].Overridden, actual.DataFiles[i].Overridden);
            }
        }

        [Test]
        public void ManifestDetailRoundTripsAndStaysWithinTheControlLimit()
        {
            var random = new DeterministicRandom(4);
            for (int round = 0; round < 200; round++)
            {
                HandshakeManifest manifest = RandomManifest(random);
                byte[] bytes = Encode<ManifestDetailMessage>(ManifestDetailMessage.Write, new ManifestDetailMessage { Manifest = manifest });
                AssertManifests(manifest, ManifestDetailMessage.Decode(bytes, 0, bytes.Length).Manifest);
                if (round == 1)
                {
                    AssertTrailingByteRejected(bytes, ManifestDetailMessage.Decode);
                    AssertEveryTruncationRejected(bytes, ManifestDetailMessage.Decode);
                }
            }

            // a prefab list above its byte budget is sent as null (the report then says "list unavailable")
            var big = new HandshakeManifest { Digests = MessageGoldenTests.SampleDigests(), PrefabIds = new List<string>() };
            string id = new string('p', 1000);
            for (int i = 0; i < 7000; i++) big.PrefabIds.Add(id);                    // ~7 MB > 6 MiB
            byte[] encoded = Encode<ManifestDetailMessage>(ManifestDetailMessage.Write, new ManifestDetailMessage { Manifest = big });
            Assert.Null(ManifestDetailMessage.Decode(encoded, 0, encoded.Length).Manifest.PrefabIds);

            // a payload above the control limit without its prefab list is refused, and nothing is left in the writer
            var huge = new HandshakeManifest { Digests = MessageGoldenTests.SampleDigests(), Settings = new List<SettingEntry>(), PrefabIds = new List<string> { "kept?" } };
            string value = new string('v', ManifestDetailMessage.MaxSettingValueBytes);
            for (int i = 0; i < 600; i++) huge.Settings.Add(new SettingEntry { Mod = "m", Key = "k" + i, Value = value });   // ~9.8 MB
            var w = new ByteWriter();
            w.WriteByte(0x42);
            Assert.Throws<ArgumentException>(() => ManifestDetailMessage.Write(w, new ManifestDetailMessage { Manifest = huge }));
            Assert.Equal(1, w.Length, "the writer is rolled back");

            // the prefab list is dropped first when it alone pushes the payload over the limit
            huge.Settings.RemoveRange(450, 150);                                      // ~7.4 MB of settings
            huge.PrefabIds = new List<string>();
            for (int i = 0; i < 1500; i++) huge.PrefabIds.Add(id);                    // ~1.5 MB: within its own budget
            encoded = Encode<ManifestDetailMessage>(ManifestDetailMessage.Write, new ManifestDetailMessage { Manifest = huge });
            Assert.True(encoded.Length <= ManifestDetailMessage.MaxEncodedBytes);
            HandshakeManifest back = ManifestDetailMessage.Decode(encoded, 0, encoded.Length).Manifest;
            Assert.Null(back.PrefabIds);
            Assert.Equal(450, back.Settings.Count);

            // argument errors leave the writer unchanged as well
            w.Reset();
            Assert.Throws<ArgumentException>(() => ManifestDetailMessage.Write(w, new ManifestDetailMessage { Manifest = new HandshakeManifest { Mods = new List<ModEntry> { null } } }));
            Assert.Equal(0, w.Length);
            Assert.Throws<ArgumentException>(() => ManifestDetailMessage.Write(w, new ManifestDetailMessage()));
            Assert.Throws<ArgumentException>(() => ManifestDetailMessage.Write(w, new ManifestDetailMessage
            {
                Manifest = new HandshakeManifest { Mods = new List<ModEntry> { new ModEntry { Name = new string('n', ManifestDetailMessage.MaxModNameBytes + 1) } } }
            }));
            Assert.Equal(0, w.Length);
        }

        [Test]
        public void ManifestDetailRejectsUnknownFlagBits()
        {
            var manifest = new HandshakeManifest
            {
                Digests = MessageGoldenTests.SampleDigests(), Mods = new List<ModEntry> { new ModEntry { Name = "a" } },
                Content = null, FormatTagNames = null, Settings = null, DataFiles = null
            };
            byte[] bytes = Encode<ManifestDetailMessage>(ManifestDetailMessage.Write, new ManifestDetailMessage { Manifest = manifest });
            // the mod's flags byte sits right after its four strings: "a", "", "", ""
            int flags = Array.LastIndexOf(bytes, (byte)0x61) + 4;
            Assert.Equal((byte)0, bytes[flags]);
            bytes[flags] = 0x08;
            Assert.Throws<DecodeException>(() => ManifestDetailMessage.Decode(bytes, 0, bytes.Length));
        }

        [Test]
        public void SmallControlPayloadsRoundTrip()
        {
            var random = new DeterministicRandom(5);
            for (int round = 0; round < 100; round++)
            {
                var challenge = new PasswordChallengeMessage { HostNonce = RandomBytes(random, 64), Salt = RandomBytes(random, 64), SessionId = random.NextUInt() };
                byte[] c = Encode<PasswordChallengeMessage>(PasswordChallengeMessage.Write, challenge);
                PasswordChallengeMessage cr = PasswordChallengeMessage.Decode(c, 0, c.Length);
                Assert.SequenceEqual(challenge.HostNonce, cr.HostNonce);
                Assert.SequenceEqual(challenge.Salt, cr.Salt);
                Assert.Equal(challenge.SessionId, cr.SessionId);

                var response = new PasswordResponseMessage { ClientNonce = RandomBytes(random, 64), Mac = RandomBytes(random, 64) };
                byte[] p = Encode<PasswordResponseMessage>(PasswordResponseMessage.Write, response);
                PasswordResponseMessage pr = PasswordResponseMessage.Decode(p, 0, p.Length);
                Assert.SequenceEqual(response.ClientNonce, pr.ClientNonce);
                Assert.SequenceEqual(response.Mac, pr.Mac);

                var control = new ControlMessage { Kind = (ControlKind)random.Next(256), Payload = RandomBytes(random, 2000) };
                byte[] k = Encode<ControlMessage>(ControlMessage.Write, control);
                ControlMessage kr = ControlMessage.Decode(k, 0, k.Length);
                Assert.Equal(control.Kind, kr.Kind, "unknown kinds are read for the router to drop");
                Assert.Equal((int)control.Kind <= (int)ControlKind.LoadProgress, ControlMessage.IsKnownKind(kr.Kind));
                Assert.SequenceEqual(control.Payload, kr.Payload);
                if (round == 0)
                {
                    AssertTrailingByteRejected(c, PasswordChallengeMessage.Decode);
                    AssertEveryTruncationRejected(c, PasswordChallengeMessage.Decode);
                    AssertTrailingByteRejected(p, PasswordResponseMessage.Decode);
                    AssertTrailingByteRejected(k, ControlMessage.Decode);
                    AssertEveryTruncationRejected(k, ControlMessage.Decode);
                }
            }
            Assert.Throws<ArgumentException>(() => Encode<PasswordChallengeMessage>(PasswordChallengeMessage.Write, new PasswordChallengeMessage { Salt = new byte[65] }));
            Assert.Throws<ArgumentException>(() => Encode<PasswordResponseMessage>(PasswordResponseMessage.Write, new PasswordResponseMessage { Mac = new byte[65] }));
            Assert.Throws<DecodeException>(() => PasswordResponseMessage.Decode(Hex("41"), 0, 1), "65-byte nonce declared");
            Assert.Throws<ArgumentException>(() => ControlMessage.Write(new ByteWriter(), ControlKind.Toast, new byte[16], 0, ControlMessage.MaxPayloadBytes + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => ControlMessage.Write(new ByteWriter(), ControlKind.Toast, new byte[16], 8, 9));
            Assert.Throws<DecodeException>(() => ControlMessage.Decode(Hex("05 f8ffff03"), 0, 5), "payload length above the limit");
        }

        [Test]
        public void ChatTextIsLimitedAndSanitised()
        {
            var random = new DeterministicRandom(6);
            for (int round = 0; round < 100; round++)
            {
                var m = new ChatMessage { From = new PeerId((byte)random.Next(256)), Text = RandomText(random, 500), IsSystem = random.NextBool() };
                byte[] bytes = Encode<ChatMessage>(ChatMessage.Write, m);
                ChatMessage read = ChatMessage.Decode(bytes, 0, bytes.Length);
                Assert.Equal(m.From, read.From);
                Assert.Equal(m.Text, read.Text);
                Assert.Equal(m.IsSystem, read.IsSystem);
                if (round == 0) AssertEveryTruncationRejected(bytes, ChatMessage.Decode);
            }
            byte[] longText = Encode<ChatMessage>(ChatMessage.Write, new ChatMessage { Text = new string('c', 700) });
            Assert.Equal(Limits.MaxChatChars, ChatMessage.Decode(longText, 0, longText.Length).Text.Length, "truncated when written");
            byte[] control = Encode<ChatMessage>(ChatMessage.Write, new ChatMessage { Text = "line1\nline2" + (char)0 + (char)0x2028 + (char)0x2066 + "x" });
            Assert.Equal("line1line2x", ChatMessage.Decode(control, 0, control.Length).Text);

            var w = new ByteWriter();
            w.WriteByte(1);
            w.WriteString(new string('c', 501), 2000);
            w.WriteByte(0);
            byte[] hostile = w.ToArray();
            Assert.Throws<DecodeException>(() => ChatMessage.Decode(hostile, 0, hostile.Length), "501 characters");
            Assert.Throws<DecodeException>(() => ChatMessage.Decode(Hex("01 00 02"), 0, 3), "boolean byte 2");
        }

        [Test]
        public void ViewPresencePingRoundTrip()
        {
            var random = new DeterministicRandom(7);
            var viewInto = new ViewReport();
            var pingInto = new PingMessage();
            for (int round = 0; round < 200; round++)
            {
                var view = new ViewReport
                {
                    PivotX = RandomFloat(random), PivotZ = RandomFloat(random), Zoom = RandomFloat(random), Yaw = RandomFloat(random),
                    CamX = RandomFloat(random), CamY = RandomFloat(random), CamZ = RandomFloat(random), Infoview = (byte)random.Next(256),
                    InfomodeMask = random.NextUInt(), Selected = RandomSlot(random, true), Followed = RandomSlot(random, true),
                    PanelFlags = (ushort)random.Next(65536), PanelTarget = RandomSlot(random, true)
                };
                byte[] v = Encode<ViewReport>(ViewReport.Write, view);
                Assert.True(v.Length <= Limits.MaxBodyBytes(MessageKind.View));
                ViewReport.ReadInto(Reader(v), viewInto);
                Assert.Equal(view.PivotX, viewInto.PivotX);
                Assert.Equal(view.Zoom, viewInto.Zoom);
                Assert.Equal(view.CamZ, viewInto.CamZ);
                Assert.Equal(view.Infoview, viewInto.Infoview);
                Assert.Equal(view.InfomodeMask, viewInto.InfomodeMask);
                Assert.Equal(view.Selected, viewInto.Selected);
                Assert.Equal(view.Followed, viewInto.Followed);
                Assert.Equal(view.PanelFlags, viewInto.PanelFlags);
                Assert.Equal(view.PanelTarget, viewInto.PanelTarget);

                var preview = new byte[random.Next(PresenceMessage.MaxPreviewBytes + 1)];
                for (int i = 0; i < preview.Length; i++) preview[i] = (byte)random.Next(256);
                var presence = new PresenceMessage
                {
                    From = new PeerId((byte)random.Next(256)), CursorX = RandomFloat(random), CursorY = RandomFloat(random), CursorZ = RandomFloat(random),
                    ToolKind = (byte)random.Next(256), Prefab = new PrefabRank(random.Next(-1, 100000)), Color = random.NextUInt(),
                    Preview = preview, PreviewLength = preview.Length, BrushRadius = RandomFloat(random)
                };
                byte[] p = Encode<PresenceMessage>(PresenceMessage.Write, presence);
                Assert.True(p.Length <= Limits.MaxBodyBytes(MessageKind.Presence));
                PresenceMessage pr = PresenceMessage.Decode(p, 0, p.Length);
                Assert.Equal(presence.From, pr.From);
                Assert.Equal(presence.CursorY, pr.CursorY);
                Assert.Equal(presence.ToolKind, pr.ToolKind);
                Assert.Equal(presence.Prefab, pr.Prefab);
                Assert.Equal(presence.Color, pr.Color);
                Assert.Equal(preview.Length, pr.PreviewLength);
                Assert.SequenceEqual(preview, Prefix(pr.Preview, pr.PreviewLength));
                Assert.Equal(presence.BrushRadius, pr.BrushRadius);
                Assert.True(p.Length + 2 <= Limits.MaxDatagramBytes, "a presence message is one datagram even uncompressed");

                // ReadInto reuses a preview array that is large enough
                var presenceInto = new PresenceMessage { Preview = new byte[PresenceMessage.MaxPreviewBytes] };
                byte[] reused = presenceInto.Preview;
                PresenceMessage.ReadInto(Reader(p), presenceInto);
                Assert.Same(reused, presenceInto.Preview, "a large enough array is reused");
                Assert.SequenceEqual(preview, Prefix(presenceInto.Preview, presenceInto.PreviewLength));
                Assert.Equal(presence.From, presenceInto.From);
                Assert.Equal(presence.CursorX, presenceInto.CursorX);
                Assert.Equal(presence.CursorZ, presenceInto.CursorZ);
                Assert.Equal(presence.Prefab, presenceInto.Prefab);
                Assert.Equal(presence.BrushRadius, presenceInto.BrushRadius);

                var ping = new PingMessage { Sequence = random.NextUInt(), SentSeconds = random.NextDouble() * 1e5, IsReply = random.NextBool() };
                byte[] g = Encode<PingMessage>(PingMessage.Write, ping);
                PingMessage.ReadInto(Reader(g), pingInto);
                Assert.Equal(ping.Sequence, pingInto.Sequence);
                Assert.Equal(ping.SentSeconds, pingInto.SentSeconds);
                Assert.Equal(ping.IsReply, pingInto.IsReply);

                if (round == 0)
                {
                    AssertTrailingByteRejected(v, ViewReport.Decode);
                    AssertEveryTruncationRejected(v, ViewReport.Decode);
                    AssertTrailingByteRejected(p, PresenceMessage.Decode);
                    AssertEveryTruncationRejected(p, PresenceMessage.Decode);
                    AssertTrailingByteRejected(g, PingMessage.Decode);
                    AssertEveryTruncationRejected(g, PingMessage.Decode);
                }
            }
        }

        [Test]
        public void NonFiniteFloatsAreWrittenAsZeroAndRejectedWhenRead()
        {
            byte[] view = Encode<ViewReport>(ViewReport.Write, new ViewReport { PivotX = float.NaN, Zoom = float.PositiveInfinity, CamY = float.NegativeInfinity, Yaw = 7f });
            ViewReport v = ViewReport.Decode(view, 0, view.Length);
            Assert.Equal(0f, v.PivotX);
            Assert.Equal(0f, v.Zoom);
            Assert.Equal(0f, v.CamY);
            Assert.Equal(7f, v.Yaw);
            view[0] = 0x00;
            view[1] = 0x00;
            view[2] = 0xC0;
            view[3] = 0x7F;                                                         // NaN in PivotX
            Assert.Throws<DecodeException>(() => ViewReport.Decode(view, 0, view.Length));

            byte[] presence = Encode<PresenceMessage>(PresenceMessage.Write, new PresenceMessage { CursorX = float.NaN, BrushRadius = 1f });
            PresenceMessage p = PresenceMessage.Decode(presence, 0, presence.Length);
            Assert.Equal(0f, p.CursorX);
            Assert.Equal(1f, p.BrushRadius);

            byte[] ping = Encode<PingMessage>(PingMessage.Write, new PingMessage { SentSeconds = double.NegativeInfinity });
            Assert.Equal(0.0, PingMessage.Decode(ping, 0, ping.Length).SentSeconds);
        }

        [Test]
        public void PresenceAndViewLimitsAreEnforced()
        {
            Assert.Throws<ArgumentException>(() => Encode<PresenceMessage>(PresenceMessage.Write, new PresenceMessage { Preview = new byte[PresenceMessage.MaxPreviewBytes + 1], PreviewLength = PresenceMessage.MaxPreviewBytes + 1 }));
            Assert.Throws<ArgumentException>(() => Encode<PresenceMessage>(PresenceMessage.Write, new PresenceMessage { Preview = new byte[4], PreviewLength = 5 }));

            // 1101 preview bytes declared
            var w = new ByteWriter();
            w.WriteByte(0);
            for (int i = 0; i < 3; i++) w.WriteSingle(0);
            w.WriteByte(0);
            w.WriteVarUInt32(0);
            w.WriteUInt32(0);
            w.WriteSingle(0);
            w.WriteVarUInt32(PresenceMessage.MaxPreviewBytes + 1);
            for (int i = 0; i <= PresenceMessage.MaxPreviewBytes; i++) w.WriteByte(0);
            byte[] hostile = w.ToArray();
            Assert.Throws<DecodeException>(() => PresenceMessage.Decode(hostile, 0, hostile.Length));

            // a slot above 24 bits in a view report
            byte[] view = Encode<ViewReport>(ViewReport.Write, new ViewReport());
            var tampered = new ByteWriter();
            tampered.WriteBytes(view, 0, 33);                                       // the seven floats, infoview and infomode mask
            tampered.WriteVarUInt32(NetSlot.MaxValue + 1);
            tampered.WriteBytes(view, 34, view.Length - 34);
            byte[] bad = tampered.ToArray();
            Assert.Throws<DecodeException>(() => ViewReport.Decode(bad, 0, bad.Length));
        }

        [Test]
        public void HashDetailAndRepairRequestRoundTripAndValidate()
        {
            var random = new DeterministicRandom(8);
            for (int round = 0; round < 200; round++)
            {
                int bucket = random.Next(Limits.MaxBucket + 1);
                int count = random.Next(round == 0 ? Limits.MaxHashDetailSlots + 1 : 40);
                var slots = new List<NetSlot>();
                var hashes = new List<uint>();
                for (int i = 0; i < count; i++)
                {
                    uint slot = ((uint)bucket << NetSlot.BucketShift) | (uint)random.Next(1024);
                    if (slot == 0) continue;
                    slots.Add(new NetSlot(slot));
                    hashes.Add(random.NextUInt());
                }
                var detail = new HashDetailMessage { Class = (ReplicationClass)random.Next(11), Bucket = bucket, AtEpoch = new EpochId(random.NextUInt()), Slots = slots.ToArray(), Hashes = hashes.ToArray() };
                byte[] d = Encode<HashDetailMessage>(HashDetailMessage.Write, detail);
                Assert.True(d.Length <= Limits.MaxBodyBytes(MessageKind.HashDetail));
                HashDetailMessage dr = HashDetailMessage.Decode(d, 0, d.Length);
                Assert.Equal(detail.Class, dr.Class);
                Assert.Equal(detail.Bucket, dr.Bucket);
                Assert.Equal(detail.AtEpoch, dr.AtEpoch);
                Assert.SequenceEqual(detail.Slots, dr.Slots);
                Assert.SequenceEqual(detail.Hashes, dr.Hashes);

                var repairSlots = new NetSlot[random.Next(round == 0 ? Limits.MaxRepairSlots + 1 : 30)];
                for (int i = 0; i < repairSlots.Length; i++) repairSlots[i] = RandomSlot(random, false);
                var repair = new RepairRequestMessage { Class = (ReplicationClass)random.Next(11), Slots = repairSlots, LevelHint = (RepairLevel)random.Next(5) };
                byte[] r = Encode<RepairRequestMessage>(RepairRequestMessage.Write, repair);
                Assert.True(r.Length <= Limits.MaxBodyBytes(MessageKind.RepairRequest));
                RepairRequestMessage rr = RepairRequestMessage.Decode(r, 0, r.Length);
                Assert.Equal(repair.Class, rr.Class);
                Assert.SequenceEqual(repair.Slots, rr.Slots);
                Assert.Equal(repair.LevelHint, rr.LevelHint);
                if (round == 1)
                {
                    AssertTrailingByteRejected(d, HashDetailMessage.Decode);
                    AssertEveryTruncationRejected(d, HashDetailMessage.Decode);
                    AssertTrailingByteRejected(r, RepairRequestMessage.Decode);
                    AssertEveryTruncationRejected(r, RepairRequestMessage.Decode);
                }
            }

            Assert.Throws<ArgumentException>(() => Encode<HashDetailMessage>(HashDetailMessage.Write, new HashDetailMessage { Slots = new NetSlot[1], Hashes = new uint[0] }), "parallel arrays differ");
            Assert.Throws<ArgumentException>(() => Encode<HashDetailMessage>(HashDetailMessage.Write, new HashDetailMessage { Slots = new[] { NetSlot.None }, Hashes = new uint[1] }), "slot 0");
            Assert.Throws<ArgumentException>(() => Encode<HashDetailMessage>(HashDetailMessage.Write, new HashDetailMessage { Bucket = 1, Slots = new[] { new NetSlot(5) }, Hashes = new uint[1] }), "slot outside the bucket");
            Assert.Throws<ArgumentException>(() => Encode<HashDetailMessage>(HashDetailMessage.Write, new HashDetailMessage { Class = (ReplicationClass)11 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => Encode<HashDetailMessage>(HashDetailMessage.Write, new HashDetailMessage { Bucket = Limits.MaxBucket + 1 }));
            Assert.Throws<ArgumentException>(() => Encode<RepairRequestMessage>(RepairRequestMessage.Write, new RepairRequestMessage { Slots = new NetSlot[Limits.MaxRepairSlots + 1] }));
            Assert.Throws<ArgumentException>(() => Encode<RepairRequestMessage>(RepairRequestMessage.Write, new RepairRequestMessage { LevelHint = (RepairLevel)5 }));
            Assert.Throws<ArgumentException>(() => Encode<RepairRequestMessage>(RepairRequestMessage.Write, new RepairRequestMessage { Slots = new[] { NetSlot.None } }));

            Assert.Throws<DecodeException>(() => HashDetailMessage.Decode(Hex("04 01 00 01 05 00000000"), 0, 9), "slot 5 in bucket 1");
            Assert.Throws<DecodeException>(() => HashDetailMessage.Decode(Hex("0b 00 00 00"), 0, 4), "class 11");
            Assert.Throws<DecodeException>(() => HashDetailMessage.Decode(Hex("04 8080 01 00 00"), 0, 6), "bucket 16384");
            Assert.Throws<DecodeException>(() => HashDetailMessage.Decode(Hex("04 00 00 8108"), 0, 5), "1025 slots");
            Assert.Throws<DecodeException>(() => RepairRequestMessage.Decode(Hex("02 01 00 02"), 0, 4), "slot 0");
            Assert.Throws<DecodeException>(() => RepairRequestMessage.Decode(Hex("02 00 05"), 0, 3), "level 5");
            Assert.Throws<DecodeException>(() => RepairRequestMessage.Decode(Hex("02 8104 00"), 0, 4), "513 slots");
            Assert.Throws<DecodeException>(() => RepairRequestMessage.Decode(Hex("02 01 80808008 00"), 0, 7), "slot above 24 bits");
        }

        [Test]
        public void SnapshotManifestRoundTripsAndValidates()
        {
            var random = new DeterministicRandom(9);
            for (int round = 0; round < 200; round++)
            {
                var counts = random.Next(4) == 0 ? null : new Dictionary<Family, int>();
                for (int f = 0; counts != null && f <= (int)Family.Generic; f++)
                {
                    if (random.NextBool()) counts[(Family)f] = random.Next(0, int.MaxValue);
                }
                var renders = new string[random.Next(4)];
                for (int i = 0; i < renders.Length; i++) renders[i] = RandomText(random, 60);
                var m = new SnapshotManifest
                {
                    SnapshotId = random.NextUInt(), Size = (long)(random.NextDouble() * SnapshotManifestCodec.MaxSnapshotBytes), DigestLo = random.NextULong(),
                    DigestHi = random.NextULong(), ChunkSize = random.Next(1, Limits.MaxBulkChunkBytes + 1), Epoch = new EpochId(random.NextUInt()),
                    FrameIndex = random.NextUInt(), HostBuildOrderNext = random.NextUInt(), GameModeName = RandomText(random, 30),
                    RenderPrefabIds = random.Next(4) == 0 ? null : renders, ReferencedContent = random.NextBool() ? null : renders, FamilyCounts = counts
                };
                byte[] bytes = Encode<SnapshotManifest>(SnapshotManifestCodec.Write, m);
                SnapshotManifest read = SnapshotManifestCodec.Decode(bytes, 0, bytes.Length);
                Assert.Equal(m.SnapshotId, read.SnapshotId);
                Assert.Equal(m.Size, read.Size);
                Assert.Equal(m.DigestLo, read.DigestLo);
                Assert.Equal(m.DigestHi, read.DigestHi);
                Assert.Equal(m.ChunkSize, read.ChunkSize);
                Assert.Equal(m.Epoch, read.Epoch);
                Assert.Equal(m.FrameIndex, read.FrameIndex);
                Assert.Equal(m.HostBuildOrderNext, read.HostBuildOrderNext);
                Assert.Equal(m.GameModeName, read.GameModeName);
                Assert.Equal(m.RenderPrefabIds == null, read.RenderPrefabIds == null);
                if (m.RenderPrefabIds != null) Assert.SequenceEqual(m.RenderPrefabIds, read.RenderPrefabIds);
                Assert.Equal(m.ReferencedContent == null, read.ReferencedContent == null);
                Assert.Equal(m.FamilyCounts == null, read.FamilyCounts == null);
                if (m.FamilyCounts != null)
                {
                    Assert.Equal(m.FamilyCounts.Count, read.FamilyCounts.Count);
                    foreach (KeyValuePair<Family, int> pair in m.FamilyCounts) Assert.Equal(pair.Value, read.FamilyCounts[pair.Key]);
                }
                if (round == 0)
                {
                    AssertTrailingByteRejected(bytes, SnapshotManifestCodec.Decode);
                    AssertEveryTruncationRejected(bytes, SnapshotManifestCodec.Decode);
                }
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => Encode<SnapshotManifest>(SnapshotManifestCodec.Write, new SnapshotManifest { ChunkSize = 1, Size = SnapshotManifestCodec.MaxSnapshotBytes + 1 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => Encode<SnapshotManifest>(SnapshotManifestCodec.Write, new SnapshotManifest { ChunkSize = 0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => Encode<SnapshotManifest>(SnapshotManifestCodec.Write, new SnapshotManifest { ChunkSize = Limits.MaxBulkChunkBytes + 1 }));
            Assert.Throws<ArgumentException>(() => Encode<SnapshotManifest>(SnapshotManifestCodec.Write, new SnapshotManifest { ChunkSize = 1, FamilyCounts = new Dictionary<Family, int> { { (Family)26, 1 } } }));
            Assert.Throws<ArgumentException>(() => Encode<SnapshotManifest>(SnapshotManifestCodec.Write, new SnapshotManifest { ChunkSize = 1, FamilyCounts = new Dictionary<Family, int> { { Family.Object, -1 } } }));

            void Rejected(string hex, string why)
            {
                byte[] bytes = Hex(hex);
                Assert.Throws<DecodeException>(() => SnapshotManifestCodec.Decode(bytes, 0, bytes.Length), why);
            }
            // u32 id | size | digest lo | digest hi | chunk 1 | epoch | frame | build order | "" | null | null | family counts...
            const string head = "00000000 00 0000000000000000 0000000000000000 01 00 00000000 00000000 00 00 00 ";
            Rejected(head + "03 0d 01 0a 01", "families out of order");
            Rejected(head + "02 1a 01", "family 26");
            Rejected(head + "03 0a 01 0a 01", "duplicate family");
            Rejected(head + "02 0a 8080808008", "family count above int.MaxValue");
            Rejected("00000000 8180808008 0000000000000000 0000000000000000 01 00 00000000 00000000 00 00 00 00", "size above 2 GiB");
            Rejected("00000000 00 0000000000000000 0000000000000000 00 00 00000000 00000000 00 00 00 00", "chunk size 0");
            Rejected("00000000 00 0000000000000000 0000000000000000 818010 00 00000000 00000000 00 00 00 00", "chunk size 256 KiB + 1");
            byte[] empty = Hex(head + "00");
            SnapshotManifest none = SnapshotManifestCodec.Decode(empty, 0, empty.Length);
            Assert.Null(none.FamilyCounts);
            Assert.Null(none.RenderPrefabIds);
            Assert.Equal("", none.GameModeName);
        }

        [Test]
        public void CompressionIsNegotiatedFromBothMasks()
        {
            byte zstdOnly = CompressionNegotiation.LocalMask(true, false);
            byte lz4Only = CompressionNegotiation.LocalMask(false, true);
            byte both = CompressionNegotiation.LocalMask(true, true);
            byte neither = CompressionNegotiation.LocalMask(false, false);
            Assert.Equal(CompressionNegotiation.AlwaysSupported, neither);
            Assert.Equal(CompressionKind.Zstd, CompressionNegotiation.Choose(both, zstdOnly));
            Assert.Equal(CompressionKind.Lz4, CompressionNegotiation.Choose(both, neither), "native LZ4 on the host, managed decoding on the client");
            Assert.Equal(CompressionKind.Lz4, CompressionNegotiation.Choose(lz4Only, zstdOnly));
            Assert.Equal(CompressionKind.ManagedLz4, CompressionNegotiation.Choose(zstdOnly, neither));
            Assert.Equal(CompressionKind.ManagedLz4, CompressionNegotiation.Choose(neither, 0), "a peer that announced nothing");

            Assert.Equal(CompressionKind.ManagedLz4, CompressionNegotiation.CompressorKindFor(CompressionKind.Lz4, neither));
            Assert.Equal(CompressionKind.Lz4, CompressionNegotiation.CompressorKindFor(CompressionKind.Lz4, lz4Only));
            Assert.Equal(CompressionKind.Zstd, CompressionNegotiation.CompressorKindFor(CompressionKind.Zstd, zstdOnly));
            Assert.Equal(CompressionKind.ManagedLz4, CompressionNegotiation.CompressorKindFor(CompressionKind.Zstd, neither));
            Assert.Equal(CompressionKind.None, CompressionNegotiation.CompressorKindFor(CompressionKind.None, both));

            Assert.True(CompressionNegotiation.Supports(0, CompressionKind.ManagedLz4));
            Assert.True(CompressionNegotiation.Supports(0, CompressionKind.Lz4), "LZ4 blocks always decode with the built-in decoder");
            Assert.False(CompressionNegotiation.Supports(neither, CompressionKind.Zstd));
            Assert.True(CompressionNegotiation.Supports(zstdOnly, CompressionKind.Zstd));
            Assert.False(CompressionNegotiation.Supports(0xFF, (CompressionKind)9));
            Assert.Equal((byte)4, CompressionNegotiation.Bit(CompressionKind.Zstd));
            Assert.Throws<ArgumentOutOfRangeException>(() => CompressionNegotiation.Bit((CompressionKind)4));

            // whatever is negotiated, both sides can decode what the other sends
            var codecs = new Dictionary<CompressionKind, ICompressor>
            {
                { CompressionKind.ManagedLz4, new ManagedLz4Compressor() },
                { CompressionKind.Lz4, new SpyCompressor(new ManagedLz4Compressor(), CompressionKind.Lz4) },
                { CompressionKind.Zstd, new SpyCompressor(new ManagedLz4Compressor(), CompressionKind.Zstd) }
            };
            var body = new byte[4000];
            for (int i = 0; i < body.Length; i++) body[i] = (byte)(i % 17);
            foreach (byte host in new[] { neither, zstdOnly, lz4Only, both })
            {
                foreach (byte client in new[] { neither, zstdOnly, lz4Only, both })
                {
                    CompressionKind session = CompressionNegotiation.Choose(host, client);
                    foreach ((byte sender, byte receiver) in new[] { (host, client), (client, host) })
                    {
                        CompressionKind sendKind = CompressionNegotiation.CompressorKindFor(session, sender);
                        CompressionKind receiveKind = CompressionNegotiation.CompressorKindFor(session, receiver);
                        var encoder = new EnvelopeCodec(sendKind == CompressionKind.None ? null : codecs[sendKind]);
                        var decoder = new EnvelopeCodec(receiveKind == CompressionKind.None ? null : codecs[receiveKind]);
                        Assert.True(CompressionNegotiation.Supports(receiver, sendKind), session + ": the receiver decodes " + sendKind);
                        var output = new ByteWriter();
                        encoder.Encode(MessageKind.Epoch, body, 0, body.Length, output);
                        Envelope envelope = decoder.Decode(output.Buffer, 0, output.Length, new ByteWriter());
                        Assert.SequenceEqual(body, Slice(envelope.Body, envelope.BodyOffset, envelope.BodyCount));
                    }
                }
            }
        }

        [Test]
        public void HandshakeMessagesDecodeOnEveryPeerBeforeNegotiation()
        {
            // Hello/Welcome/Refuse are encoded with the handshake codec; a receiver decodes them with whatever codec it
            // built from its own probes (the best local compressor), before it knows the session codec
            Assert.Equal(CompressionKind.ManagedLz4, CompressionNegotiation.Handshake);
            var codecs = new Dictionary<CompressionKind, ICompressor>
            {
                { CompressionKind.ManagedLz4, new ManagedLz4Compressor() },
                { CompressionKind.Lz4, new SpyCompressor(new ManagedLz4Compressor(), CompressionKind.Lz4) },
                { CompressionKind.Zstd, new SpyCompressor(new ManagedLz4Compressor(), CompressionKind.Zstd) }
            };
            var encoder = new EnvelopeCodec(codecs[CompressionNegotiation.Handshake]);
            var refuse = new RefuseMessage { Reason = RefuseReason.Mismatch, Detail = "mods differ", MismatchReport = new byte[3000] };
            byte[] body = Encode<RefuseMessage>(RefuseMessage.Write, refuse);
            var output = new ByteWriter();
            encoder.Encode(MessageKind.Refuse, body, 0, body.Length, output);
            Assert.Equal((byte)(EnvelopeFlags.Compressed | EnvelopeFlags.HasUncompressedSize), output.Buffer[1], "the report is compressed");

            foreach (CompressionKind local in new[] { CompressionKind.None, CompressionKind.ManagedLz4, CompressionKind.Lz4, CompressionKind.Zstd })
            {
                var decoder = new EnvelopeCodec(local == CompressionKind.None ? null : codecs[local]);
                Envelope envelope = decoder.Decode(output.Buffer, 0, output.Length, new ByteWriter());
                Assert.Equal(MessageKind.Refuse, envelope.Kind);
                RefuseMessage read = RefuseMessage.Decode(envelope.Body, envelope.BodyOffset, envelope.BodyCount);
                Assert.Equal(RefuseReason.Mismatch, read.Reason, "decoded by a peer with " + local);
                Assert.Equal(3000, read.MismatchReport.Length);
            }
        }
    }
}
