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
    /// Fuzzing of every decoder of the wire format (IMPLEMENTATION-PLAN §4, §7.2): valid encodings are mutated (bit
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

        [Test(TimeoutSeconds = 300)]
        public void RecordReader()
        {
            var random = new DeterministicRandom(200);
            var seeds = new List<byte[]>();
            for (int i = 0; i < 60; i++)
            {
                var target = new ByteWriter();
                var writer = new RecordWriter(target);
                int records = random.Next(1, 8);
                for (int r = 0; r < records; r++) RecordRoundTripTests.WriteRandomRecord(writer, random);
                seeds.Add(target.ToArray());
            }
            Fuzz("RecordReader (read)", seeds, 30000, 201, (buffer, offset, count) =>
                RecordReaderValidationTests.ReadAll(buffer, offset, count, new FakeTypeSetRegistry()));
            var skipper = new RecordReader();
            Fuzz("RecordReader (skip)", seeds, 30000, 202, (buffer, offset, count) =>
            {
                skipper.Reset(buffer, offset, count);
                while (skipper.Next(out _)) { }
            });
        }

        [Test(TimeoutSeconds = 300)]
        public void NetRefsZoneOverlaysAndProtocolVersionPeek()
        {
            var random = new DeterministicRandom(300);
            var refs = new List<byte[]>();
            for (int i = 0; i < 50; i++)
            {
                var w = new ByteWriter();
                NetRefCodec.Write(w, NetRefCodecTests.RandomRef(random));
                refs.Add(w.ToArray());
            }
            Fuzz("NetRefCodec.Read", refs, 20000, 301, (buffer, offset, count) => NetRefCodec.Read(new ByteReader(buffer, offset, count)));
            var keys = new List<byte[]>();
            var keyWriter = new ByteWriter();
            NetRefCodec.WriteKey(keyWriter, NetRefCodecTests.LaneKey());
            keys.Add(keyWriter.ToArray());
            keyWriter.Reset();
            NetRefCodec.WriteKey(keyWriter, NetRefCodecTests.SubObjectKey());
            keys.Add(keyWriter.ToArray());
            Fuzz("NetRefCodec.ReadKey", keys, 10000, 302, (buffer, offset, count) => NetRefCodec.ReadKey(new ByteReader(buffer, offset, count)));

            var zones = new List<byte[]>();
            for (int i = 0; i < 30; i++)
            {
                var cells = new ushort[random.Next(0, 61)];
                for (int c = 0; c < cells.Length; c++) cells[c] = (ushort)(random.Next(3) == 0 ? random.Next(65536) : random.Next(3));
                var w = new ByteWriter();
                ZoneOverlayCodec.Encode(cells, cells.Length, w);
                zones.Add(w.ToArray());
            }
            var target = new ushort[64];
            Fuzz("ZoneOverlayCodec.Decode", zones, 20000, 303, (buffer, offset, count) => ZoneOverlayCodec.Decode(new ByteReader(buffer, offset, count), target));

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
            Fuzz("ProposalReceived", new[] { Hex("e807"), Hex("00") }, 2000, 408, (b, o, c) => ProposalReceivedMessage.Decode(b, o, c));
            Fuzz("Chat", new[] { Encode<ChatMessage>(ChatMessage.Write, new ChatMessage { From = new PeerId(1), Text = "hello there", IsSystem = false }) },
                 3000, 409, (b, o, c) => ChatMessage.Decode(b, o, c));
            Fuzz("View", new[] { Encode<ViewReport>(ViewReport.Write, new ViewReport { Zoom = 50, Selected = new NetSlot(1000), PanelTarget = new NetSlot(3) }) },
                 3000, 410, (b, o, c) => ViewReport.Decode(b, o, c));
            Fuzz("Presence", new[]
            {
                Encode<PresenceMessage>(PresenceMessage.Write, new PresenceMessage { From = new PeerId(2), CurveControlPoints = new float[12], Footprints = new float[8], BrushRadius = 3 }),
                Encode<PresenceMessage>(PresenceMessage.Write, new PresenceMessage())
            }, 3000, 411, (b, o, c) => PresenceMessage.Decode(b, o, c));
            Fuzz("Clock", new[] { Encode<ClockSample>(ClockSample.Write, new ClockSample { HostFrame = 99, FrameTime = 0.5f, SmoothSpeed = 1, Paused = true, HostTimeSeconds = 12.5 }) },
                 3000, 412, (b, o, c) => ClockSample.Decode(b, o, c));
            Fuzz("Ack", new[] { Hex("ac02 05"), Hex("00 00") }, 2000, 413, (b, o, c) => AckMessage.Decode(b, o, c));
            Fuzz("Ping", new[] { Encode<PingMessage>(PingMessage.Write, new PingMessage { Sequence = 5, SentSeconds = 1.25, IsReply = true }) },
                 2000, 414, (b, o, c) => PingMessage.Decode(b, o, c));
            Fuzz("HashDetail", new[]
            {
                Encode<HashDetailMessage>(HashDetailMessage.Write, new HashDetailMessage
                {
                    Class = ReplicationClass.A, Bucket = 2, AtEpoch = new EpochId(77),
                    Slots = new[] { new NetSlot(2048), new NetSlot(2049), new NetSlot(3000) }, Hashes = new uint[] { 1, 2, 3 }
                })
            }, 5000, 415, (b, o, c) => HashDetailMessage.Decode(b, o, c));
            Fuzz("RepairRequest", new[]
            {
                Encode<RepairRequestMessage>(RepairRequestMessage.Write, new RepairRequestMessage { Class = ReplicationClass.S, Slots = new[] { new NetSlot(1), new NetSlot(70000) }, LevelHint = RepairLevel.Entity })
            }, 5000, 416, (b, o, c) => RepairRequestMessage.Decode(b, o, c));
            Fuzz("SnapshotManifest", new[]
            {
                Encode<SnapshotManifest>(SnapshotManifestCodec.Write, new SnapshotManifest
                {
                    SnapshotId = 1, Size = 123456, ChunkSize = 65536, Epoch = new EpochId(4), GameModeName = "Normal", RenderPrefabIds = new[] { "A", "B" },
                    ReferencedContent = new[] { "C" }, FamilyCounts = new Dictionary<Family, int> { { Family.Object, 5 }, { Family.Agent, 1000 } }
                })
            }, 5000, 417, (b, o, c) => SnapshotManifestCodec.Decode(b, o, c));
        }

        [Test(TimeoutSeconds = 300)]
        public void EpochReadersAndThePartAssembler()
        {
            var random = new DeterministicRandom(500);
            var seeds = new List<byte[]>();
            for (int i = 0; i < 30; i++)
            {
                var sections = new List<EpochFramingTests.Section>();
                for (int s = random.Next(0, 5); s > 0; s--)
                {
                    var body = new byte[random.Next(0, 40)];
                    random.NextBytes(body, 0, body.Length);
                    sections.Add(new EpochFramingTests.Section { Type = (SectionType)random.Next(1, 14), Body = body });
                }
                bool multi = random.NextBool();
                int partCount = multi ? random.Next(2, 4) : 1;
                seeds.Add(EpochFramingTests.WriteEpoch(new EpochId((uint)random.Next(1, 4)), 7, (EpochFlags)random.Next(8), sections,
                                                       multi ? random.Next(partCount) : 0, partCount));
            }

            var reader = new EpochReader();
            Fuzz("EpochReader", seeds, 20000, 501, (buffer, offset, count) =>
            {
                reader.Open(buffer, offset, count);
                while (reader.NextSection(out _, out _, out _)) { }
            });

            var streamReader = new EpochStreamReader();
            var body8 = new byte[Limits.MaxUncompressedBytes];
            var choice = new DeterministicRandom(502);
            Fuzz("EpochStreamReader", seeds, 20000, 503, (buffer, offset, count) =>
            {
                using (var stream = new MemoryStream(buffer, offset, count, false))
                {
                    streamReader.Open(stream);
                    while (streamReader.NextSection(out _, out int length))
                    {
                        if (choice.NextBool()) streamReader.ReadBody(body8, 0);
                    }
                }
            });

            // the assembler gets every message that passes EpochReader.Open and must never throw, whatever it is
            var assembler = new EpochPartAssembler(null, 32L << 20, null, 64 * 1024);
            var openReader = new EpochReader();
            var mutations = new DeterministicRandom(504);
            var results = new int[4];
            for (int i = 0; i < 30000 * Scale; i++)
            {
                byte[] input = Mutate(seeds[mutations.Next(seeds.Count)], mutations, out int offset, out int count);
                EpochHeader header;
                try
                {
                    header = openReader.Open(input, offset, count);
                }
                catch (DecodeException)
                {
                    continue;
                }
                if (mutations.Next(10) == 0) header.PartIndex = mutations.Next(4);    // a header that may not describe the bytes
                try
                {
                    PartResult result = assembler.Accept(in header, input, offset, count, out Stream complete);
                    results[(int)result]++;
                    if (result == PartResult.Complete)
                    {
                        Assert.NotNull(complete);
                        using (complete)
                        {
                            var check = new EpochStreamReader();
                            check.Open(complete);
                            while (check.NextSection(out _, out _)) { }
                        }
                    }
                    else
                    {
                        Assert.Null(complete);
                    }
                }
                catch (Exception e)
                {
                    Assert.Fail("EpochPartAssembler.Accept threw " + e.GetType().Name + ": " + e.Message + " for " + ToHex(input, offset, count) + Environment.NewLine + e.StackTrace);
                }
                if (mutations.Next(200) == 0) assembler.Reset();
            }
            Assert.True(results[(int)PartResult.Complete] > 0 && results[(int)PartResult.Incomplete] > 0, "both outcomes occur");
        }
    }
}
