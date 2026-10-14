using System;
using System.Collections.Generic;
using System.IO;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Protocol.ProtocolTestUtil;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>
    /// Fuzzing of every decoder of the wire format (ARCHITECTURE.md#code-rules): valid encodings are mutated (bit
    /// flips, byte replacement, boundary values, truncation, insertion, duplication, random garbage) and decoded; only
    /// <see cref="DecodeException"/> may escape. The input window always ends at the end of its array, so a read past
    /// the window shows up as an <see cref="IndexOutOfRangeException"/> instead of passing silently.
    /// </summary>
    public sealed class ProtocolFuzzTests
    {
        private static readonly byte[] Boundary = { 0x00, 0x01, 0x7F, 0x80, 0x81, 0xFE, 0xFF };

        /// <summary>Multiplies every iteration count (the whole suite stays within a few seconds).</summary>
        private const int Scale = 3;

        /// <summary>A mutated copy of <paramref name="seed"/> placed at the end of a padded array.</summary>
        private static byte[] Mutate(byte[] seed, DeterministicRandom random, out int offset, out int count)
        {
            var data = new List<byte>(seed);
            int rounds = 1 + random.Next(4);
            for (int r = 0; r < rounds; r++)
            {
                switch (random.Next(8))
                {
                    case 0:                                                         // flip one bit
                        if (data.Count > 0)
                        {
                            int i = random.Next(data.Count);
                            data[i] = (byte)(data[i] ^ (1 << random.Next(8)));
                        }
                        break;
                    case 1:                                                         // random byte
                        if (data.Count > 0) data[random.Next(data.Count)] = (byte)random.Next(256);
                        break;
                    case 2:                                                         // boundary value
                        if (data.Count > 0) data[random.Next(data.Count)] = Boundary[random.Next(Boundary.Length)];
                        break;
                    case 3:                                                         // truncate
                        if (data.Count > 0)
                        {
                            int keep = random.Next(data.Count);
                            data.RemoveRange(keep, data.Count - keep);
                        }
                        break;
                    case 4:                                                         // insert bytes
                    {
                        int at = random.Next(data.Count + 1);
                        int n = 1 + random.Next(4);
                        for (int k = 0; k < n; k++) data.Insert(at, (byte)random.Next(256));
                        break;
                    }
                    case 5:                                                         // remove a byte
                        if (data.Count > 0) data.RemoveAt(random.Next(data.Count));
                        break;
                    case 6:                                                         // duplicate a slice
                        if (data.Count > 1)
                        {
                            int from = random.Next(data.Count);
                            int length = 1 + random.Next(Math.Min(16, data.Count - from));
                            data.InsertRange(random.Next(data.Count + 1), data.GetRange(from, length));
                        }
                        break;
                    default:                                                        // garbage tail
                        for (int k = random.Next(8); k > 0; k--) data.Add((byte)random.Next(256));
                        break;
                }
            }
            if (random.Next(50) == 0)
            {
                data.Clear();                                                       // pure garbage
                for (int k = random.Next(64); k > 0; k--) data.Add((byte)random.Next(256));
            }
            int pad = random.Next(5);
            var buffer = new byte[pad + data.Count];
            for (int i = 0; i < pad; i++) buffer[i] = 0xCC;
            data.CopyTo(buffer, pad);
            offset = pad;
            count = data.Count;
            return buffer;
        }

        private static void Fuzz(string name, IList<byte[]> seeds, int iterations, ulong seed, Action<byte[], int, int> decode, bool validSeeds = true)
        {
            var random = new DeterministicRandom(seed);
            if (validSeeds)
            {
                foreach (byte[] valid in seeds) decode(valid, 0, valid.Length);   // the mutations start from valid encodings
            }
            iterations *= Scale;
            int rejected = 0;
            for (int i = 0; i < iterations; i++)
            {
                byte[] input = Mutate(seeds[random.Next(seeds.Count)], random, out int offset, out int count);
                try
                {
                    decode(input, offset, count);
                }
                catch (DecodeException)
                {
                    rejected++;
                }
                catch (Exception e)
                {
                    Assert.Fail(name + ", iteration " + i + ": " + e.GetType().Name + ": " + e.Message + Environment.NewLine
                                + "     input " + ToHex(input, offset, Math.Min(count, 200)) + Environment.NewLine + e.StackTrace);
                }
            }
            Assert.True(rejected > iterations / 20, name + ": the mutations should be rejected now and then (" + rejected + " of " + iterations + ")");
        }

        private static byte[] Encode<T>(Action<ByteWriter, T> write, T message)
        {
            var w = new ByteWriter();
            write(w, message);
            return w.ToArray();
        }

        [Test(TimeoutSeconds = 300)]
        public void EnvelopeDecodeAndReassembly()
        {
            var codec = new EnvelopeCodec(new ManagedLz4Compressor());
            var random = new DeterministicRandom(100);
            var seeds = new List<byte[]>();
            for (int i = 0; i < 40; i++)
            {
                var kind = (MessageKind)random.Next(1, 20);
                int size = random.Next(4) == 0 ? random.Next(256, 3000) : random.Next(0, 300);
                var body = new byte[Math.Min(size, Limits.MaxBodyBytes(kind))];
                for (int k = 0; k < body.Length; k++) body[k] = (byte)(random.Next(3) == 0 ? random.Next(256) : k % 7);
                var output = new ByteWriter();
                codec.Encode(kind, body, 0, body.Length, output);
                seeds.Add(output.ToArray());
            }
            // fragments (kind, Fragment flag, id, index, count, total length 499712, chunk): each a valid envelope, but
            // together far from their declared length, so the reassembler rejects the pair (hence validSeeds: false)
            seeds.Add(Hex("05 02 07 00 02 80c01e aabbccdd"));
            seeds.Add(Hex("05 02 07 01 02 80c01e 05 00 1122"));

            var scratch = new ByteWriter();
            var reassembler = new Reassembler();
            Fuzz("EnvelopeCodec.Decode", seeds, 20000, 101, (buffer, offset, count) =>
            {
                Envelope envelope = codec.Decode(buffer, offset, count, scratch);
                if ((envelope.Flags & EnvelopeFlags.Fragment) == 0) return;
                try
                {
                    if (reassembler.Accept(envelope, out byte[] message, out int length)) codec.Decode(message, 0, length, scratch);
                }
                finally
                {
                    if (reassembler.PartialMessages >= 3) reassembler.Reset();
                }
            }, validSeeds: false);
        }

        [Test]
        public void ProtocolVersionPeek()
        {
            var hello = Encode<HelloMessage>(HelloMessage.Write, new HelloMessage { Digests = MessageGoldenTests.SampleDigests() });
            Fuzz("HelloMessage.PeekProtocolVersion", new[] { hello, Hex("ac02") }, 5000, 304, (buffer, offset, count) => HelloMessage.PeekProtocolVersion(buffer, offset, count));
        }

        [Test(TimeoutSeconds = 300)]
        public void EveryMessageReader()
        {
            var random = new DeterministicRandom(400);
            HandshakeDigests digests = MessageGoldenTests.SampleDigests();
            var hello = new List<byte[]>
            {
                Encode<HelloMessage>(HelloMessage.Write, new HelloMessage { Digests = digests, SteamId = 7, DisplayName = "Ann", ClientNonce = new byte[16], CompressionMask = 7 }),
                Encode<HelloMessage>(HelloMessage.Write, new HelloMessage { Digests = default(HandshakeDigests) })
            };
            Fuzz("Hello", hello, 5000, 401, (b, o, c) => HelloMessage.Decode(b, o, c));

            var welcome = new List<byte[]>
            {
                Encode<WelcomeMessage>(WelcomeMessage.Write, new WelcomeMessage { SessionId = 9, AssignedPeer = new PeerId(3), Role = SessionRole.Viewer, Permissions = Permission.All,
                                                                                  SessionSettings = new byte[40], HostNonce = new byte[16], EnabledSysEntries = new uint[] { 1, 2, 3 }, Compression = CompressionKind.Lz4 }),
                Encode<WelcomeMessage>(WelcomeMessage.Write, new WelcomeMessage())
            };
            Fuzz("Welcome", welcome, 5000, 402, (b, o, c) => WelcomeMessage.Decode(b, o, c));

            var refuse = new List<byte[]>
            {
                Encode<RefuseMessage>(RefuseMessage.Write, new RefuseMessage { Reason = RefuseReason.BadPassword, Detail = "wrong password", MismatchReport = new byte[] { 1, 2, 3 } }),
                Encode<RefuseMessage>(RefuseMessage.Write, new RefuseMessage { Detail = "a" + (char)0x202E + "b" })
            };
            Fuzz("Refuse", refuse, 5000, 403, (b, o, c) => RefuseMessage.Decode(b, o, c));

            var manifest = new HandshakeManifest
            {
                Digests = digests,
                Mods = new List<ModEntry> { new ModEntry { Name = "Traffic", Version = "1", PlatformId = "9", DllHash = "ff", IsUiModule = true } },
                Content = new List<string> { "DLC" }, FormatTagNames = new List<string> { "tag" }, PrefabIds = new List<string> { "Prefab:A", "Prefab:B" },
                Settings = new List<SettingEntry> { new SettingEntry { Mod = "m", Key = "k", Value = "v", Pushable = true } },
                DataFiles = new List<DataFileDigestEntry> { new DataFileDigestEntry { File = "gates.json", Digest = 5 } }
            };
            var manifests = new List<byte[]>
            {
                Encode<ManifestDetailMessage>(ManifestDetailMessage.Write, new ManifestDetailMessage { Manifest = manifest }),
                Encode<ManifestDetailMessage>(ManifestDetailMessage.Write, new ManifestDetailMessage { Manifest = new HandshakeManifest() })
            };
            Fuzz("ManifestDetail", manifests, 8000, 404, (b, o, c) => ManifestDetailMessage.Decode(b, o, c));

            Fuzz("PasswordChallenge", new[] { Encode<PasswordChallengeMessage>(PasswordChallengeMessage.Write, new PasswordChallengeMessage { HostNonce = new byte[16], Salt = new byte[16], SessionId = 3 }) },
                 3000, 405, (b, o, c) => PasswordChallengeMessage.Decode(b, o, c));
            Fuzz("PasswordResponse", new[] { Encode<PasswordResponseMessage>(PasswordResponseMessage.Write, new PasswordResponseMessage { ClientNonce = new byte[16], Mac = new byte[32] }) },
                 3000, 406, (b, o, c) => PasswordResponseMessage.Decode(b, o, c));
            Fuzz("Control", new[] { Encode<ControlMessage>(ControlMessage.Write, new ControlMessage { Kind = ControlKind.Toast, Payload = new byte[] { 1, 2, 3 } }), Hex("18 01 2a") },
                 3000, 407, (b, o, c) => ControlMessage.Decode(b, o, c));
            Fuzz("Chat", new[] { Encode<ChatMessage>(ChatMessage.Write, new ChatMessage { From = new PeerId(1), Text = "hello there", IsSystem = false }) },
                 3000, 409, (b, o, c) => ChatMessage.Decode(b, o, c));
            Fuzz("View", new[] { Encode<ViewReport>(ViewReport.Write, new ViewReport { Zoom = 50, Selected = new NetSlot(1000), PanelTarget = new NetSlot(3) }) },
                 3000, 410, (b, o, c) => ViewReport.Decode(b, o, c));
            Fuzz("Presence", new[]
            {
                Encode<PresenceMessage>(PresenceMessage.Write, new PresenceMessage { From = new PeerId(2), Preview = new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, PreviewLength = 16, BrushRadius = 3 }),
                Encode<PresenceMessage>(PresenceMessage.Write, new PresenceMessage())
            }, 3000, 411, (b, o, c) => PresenceMessage.Decode(b, o, c));
            Fuzz("Ping", new[] { Encode<PingMessage>(PingMessage.Write, new PingMessage { Sequence = 5, SentSeconds = 1.25, IsReply = true }) },
                 2000, 414, (b, o, c) => PingMessage.Decode(b, o, c));
            Fuzz("SnapshotManifest", new[]
            {
                Encode<SnapshotManifest>(SnapshotManifestCodec.Write, new SnapshotManifest
                {
                    SnapshotId = 1, Size = 123456, ChunkSize = 65536, Epoch = new EpochId(4), GameModeName = "Normal", RenderPrefabIds = new[] { "A", "B" },
                    ReferencedContent = new[] { "C" }, FamilyCounts = new Dictionary<Family, int> { { Family.Object, 5 }, { Family.Agent, 1000 } }
                })
            }, 5000, 417, (b, o, c) => SnapshotManifestCodec.Decode(b, o, c));
        }
    }
}
