using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Protocol.ProtocolTestUtil;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>
    /// The decoder contract (<see cref="DecoderContract"/>) for the framing layer and every protocol message: malformed
    /// input is a <see cref="DecodeException"/> and nothing else, and no input makes a decoder allocate far beyond its own
    /// size (a 20-byte message from an unauthenticated peer must not cost the host megabytes). Plus properties of
    /// fragmentation under random faults.
    /// </summary>
    public sealed class DecoderContractTests
    {
        private static byte[] Encode<T>(Action<ByteWriter, T> write, T message)
        {
            var w = new ByteWriter();
            write(w, message);
            return w.ToArray();
        }

        private static byte[] EncodeEnvelope(MessageKind kind, byte[] body, bool compress)
        {
            var codec = new EnvelopeCodec(compress ? new ManagedLz4Compressor() : null);
            var w = new ByteWriter();
            codec.Encode(kind, body, 0, body.Length, w);
            return w.ToArray();
        }

        // ------------------------------------------------------------------ envelopes

        [Test]
        public void ACompressedEnvelopeCannotDeclareMoreThanLz4CanProduce()
        {
            // kind Control, flags Compressed|UncompressedSize, declared size 8 MiB, codec ManagedLz4, three data bytes
            var w = new ByteWriter();
            w.WriteByte((byte)MessageKind.Control);
            w.WriteByte(EncodeEnvelope(MessageKind.Control, new byte[4096], compress: true)[1]);
            w.WriteVarUInt32((uint)Limits.MaxUncompressedBytes);
            w.WriteByte((byte)CompressionKind.ManagedLz4);
            w.WriteBytes(new byte[] { 0x1F, 0x00, 0x01 }, 0, 3);
            byte[] bomb = w.ToArray();
            var codec = new EnvelopeCodec(new ManagedLz4Compressor());
            Assert.False(DecoderContract.Check("8 MiB from 3 bytes", bomb, b => codec.Decode(b, 0, b.Length, new ByteWriter(16)), 1100, 64 * 1024),
                         "an LZ4 block cannot expand 3 bytes to 8 MiB");
        }

        [Test]
        public void HighlyCompressibleBodiesStillDecode()
        {
            // the expansion bound must not reject real LZ4 output: zeros compress at close to LZ4's maximum ratio
            foreach (int size in new[] { 300, 4096, 1 << 20, Limits.MaxUncompressedBytes })
            {
                var body = new byte[size];
                for (int i = 0; i < size; i += 4099) body[i] = (byte)i;
                byte[] envelope = EncodeEnvelope(MessageKind.Epoch, body, compress: true);
                Assert.True(envelope.Length < size / 50 + 64, "the body compressed well (" + envelope.Length + " bytes for " + size + ")");
                Envelope e = new EnvelopeCodec(new ManagedLz4Compressor()).Decode(envelope, 0, envelope.Length, new ByteWriter());
                Assert.Equal(size, e.BodyCount);
                Assert.SequenceEqual(body, Slice(e.Body, e.BodyOffset, e.BodyCount));
            }
        }

        [Test]
        public void EnvelopeDecodingAllocatesInProportionToItsInput()
        {
            var random = new DeterministicRandom(810);
            var seeds = new List<byte[]>();
            MessageKind[] kinds = { MessageKind.Control, MessageKind.Epoch, MessageKind.Hello, MessageKind.Chat, MessageKind.Presence, MessageKind.BlobHave };
            for (int i = 0; i < 24; i++)
            {
                MessageKind kind = kinds[i % kinds.Length];
                int size = Math.Min(random.Next(0, 6000), Limits.MaxBodyBytes(kind));
                var body = new byte[size];
                for (int k = 0; k < size; k++) body[k] = (byte)(random.Next(4) == 0 ? random.Next(256) : k % 5);
                seeds.Add(EncodeEnvelope(kind, body, compress: i % 3 != 0));
            }
            var codec = new EnvelopeCodec(new ManagedLz4Compressor());
            // LZ4 expands at most ~255:1, and the decode target is allocated once at the declared size
            int rejected = DecoderContract.Fuzz("envelope", seeds, 3000, 811, b => codec.Decode(b, 0, b.Length, new ByteWriter(16)), 1100, 64 * 1024);
            Assert.True(rejected > 300, "mutations are rejected now and then (" + rejected + ")");
        }

        // ------------------------------------------------------------------ fragments

        private static Envelope AsFragment(byte[] wire) => new EnvelopeCodec(null).Decode(wire, 0, wire.Length, new ByteWriter());

        private static byte[] FirstFragment(uint id, uint count, uint total, int chunkBytes, MessageKind kind = MessageKind.Control)
        {
            var w = new ByteWriter();
            w.WriteByte((byte)kind);
            w.WriteByte((byte)EnvelopeFlags.Fragment);
            w.WriteVarUInt32(id);
            w.WriteVarUInt32(0);
            w.WriteVarUInt32(count);
            w.WriteVarUInt32(total);
            w.WriteBytes(new byte[chunkBytes], 0, chunkBytes);
            return w.ToArray();
        }

        [Test]
        public void AFirstFragmentDeclaringMegabytesAllocatesOnlyWhatArrived()
        {
            // before w8/tests the reassembler allocated the declared total (up to 8 MiB) on the first fragment: four
            // 12-byte packets from a peer that was not even admitted cost the host 32 MiB
            var reassembler = new Reassembler();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (uint id = 1; id <= Limits.MaxPartialMessagesPerPeer; id++)
            {
                byte[] wire = FirstFragment(id, Limits.MaxFragmentsPerMessage, (uint)(Limits.MaxUncompressedBytes + 8), 1);
                Assert.False(reassembler.Accept(AsFragment(wire), out _, out _));
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(Limits.MaxPartialMessagesPerPeer, reassembler.PartialMessages);
            Assert.True(allocated < 64 * 1024, "four tiny fragments allocated " + allocated + " bytes");
            Assert.Equal((long)Limits.MaxPartialMessagesPerPeer * (Limits.MaxUncompressedBytes + 8), reassembler.PartialBytes, "PartialBytes still counts the declared lengths");
        }

        private static List<byte[]> Split(Fragmenter fragmenter, byte[] envelope)
        {
            var list = new List<byte[]>();
            fragmenter.Split((MessageKind)envelope[0], envelope, 0, envelope.Length, new ByteWriter(), (b, o, c) => list.Add(Slice(b, o, c)));
            return list;
        }

        private enum Fault { None, Drop, Duplicate, Swap, BadCount }

        [Test(TimeoutSeconds = 120)]
        public void FragmentStreamsWithRandomFaultsRestoreExactlyOrFailCleanly()
        {
            // up to four messages interleaved (fragments of one message stay in order), some with a fault: a message
            // completes exactly when it has no fault (or only a duplicated last fragment), every completed message is
            // byte-identical to what was sent, and only DecodeException ever escapes
            var bodies = new byte[3][];
            for (int i = 0; i < bodies.Length; i++)
            {
                bodies[i] = new byte[Limits.FragmentThreshold * (i + 1) + 1000 * i];
                new DeterministicRandom((ulong)(900 + i)).NextBytes(bodies[i], 0, bodies[i].Length);
            }
            Prop.ForAll("fragment streams", 820, 40, g =>
            {
                var fragmenter = new Fragmenter();
                int messages = g.Int(1, Limits.MaxPartialMessagesPerPeer + 1);
                var originals = new List<byte[]>();
                var streams = new List<List<byte[]>>();
                var faults = new List<Fault>();
                var expectComplete = new List<bool>();
                for (int m = 0; m < messages; m++)
                {
                    byte[] body = g.Pick(bodies);
                    byte[] envelope = EncodeEnvelope(m % 2 == 0 ? MessageKind.Epoch : MessageKind.Control, body, compress: false);
                    List<byte[]> fragments = Split(fragmenter, envelope);
                    Fault fault = g.OneIn(2) ? Fault.None : g.Enum<Fault>();
                    int k = g.Int(fragments.Count);
                    bool complete = true;
                    switch (fault)
                    {
                        case Fault.Drop:
                            fragments.RemoveAt(k);
                            complete = false;
                            break;
                        case Fault.Duplicate:
                            fragments.Insert(k + 1, fragments[k]);
                            complete = k == fragments.Count - 2;
                            break;
                        case Fault.Swap:
                            if (k == fragments.Count - 1) k--;
                            byte[] t = fragments[k];
                            fragments[k] = fragments[k + 1];
                            fragments[k + 1] = t;
                            complete = false;
                            break;
                        case Fault.BadCount:
                        {
                            // fragment k claims another fragment count than its message
                            byte[] f = (byte[])fragments[k].Clone();
                            int at = 2;
                            while ((f[at] & 0x80) != 0) at++;   // message id
                            at++;
                            while ((f[at] & 0x80) != 0) at++;   // index
                            at++;
                            f[at] = (byte)(f[at] == 2 ? 3 : 2);
                            fragments[k] = f;
                            complete = false;
                            break;
                        }
                    }
                    originals.Add(envelope);
                    streams.Add(fragments);
                    faults.Add(fault);
                    expectComplete.Add(complete);
                }

                var reassembler = new Reassembler();
                var next = new int[messages];
                var completed = new List<byte[]>();
                int remaining = 0;
                foreach (List<byte[]> s in streams) remaining += s.Count;
                while (remaining > 0)
                {
                    int m = g.Int(messages);
                    if (next[m] >= streams[m].Count) continue;
                    byte[] wire = streams[m][next[m]++];
                    remaining--;
                    try
                    {
                        if (reassembler.Accept(AsFragment(wire), out byte[] message, out int length)) completed.Add(Slice(message, 0, length));
                    }
                    catch (DecodeException)
                    {
                        // the faulty message is dropped; the others go on
                    }
                    Assert.True(reassembler.PartialMessages <= Limits.MaxPartialMessagesPerPeer);
                    Assert.True(reassembler.PartialBytes >= 0);
                }
                int expected = 0;
                for (int m = 0; m < messages; m++)
                {
                    if (expectComplete[m]) expected++;
                }
                Assert.Equal(expected, completed.Count, "completed messages (faults " + string.Join(",", faults) + ")");
                var unmatched = new List<int>();
                for (int m = 0; m < messages; m++)
                {
                    if (expectComplete[m]) unmatched.Add(m);
                }
                foreach (byte[] c in completed)
                {
                    // two messages may carry the same bytes: match each completion to a distinct intact message
                    int match = unmatched.FindIndex(m => Equal(originals[m], c));
                    Assert.True(match >= 0, "a completed message is one that was sent intact");
                    unmatched.RemoveAt(match);
                }
                bool anyFault = faults.Exists(f => f != Fault.None);
                if (!anyFault) Assert.Equal(0, reassembler.PartialMessages, "nothing stays behind after intact streams");
            });
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        [Test]
        public void RandomFragmentHeadersOnlyEverRaiseDecodeExceptions()
        {
            // a peer that sends nothing but fragments with random headers: the reassembler stays within its caps and
            // allocates in proportion to the bytes it was sent
            var reassembler = new Reassembler();
            long sent = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            Prop.ForAll("random fragments", 830, 4000, g =>
            {
                var w = new ByteWriter();
                w.WriteByte((byte)g.Pick(MessageKind.Control, MessageKind.Epoch, MessageKind.Hello, MessageKind.Ack, (MessageKind)77));
                w.WriteByte((byte)EnvelopeFlags.Fragment);
                w.WriteVarUInt32((uint)g.Int(6));
                w.WriteVarUInt32((uint)g.Pick(0, 0, 0, 1, 2, g.Int(70)));
                w.WriteVarUInt32((uint)g.Pick(0, 1, 2, 3, 64, 65, g.Int(100)));
                w.WriteVarUInt32(g.Pick((uint)Limits.FragmentThreshold, (uint)Limits.FragmentThreshold + 1, (uint)Limits.MaxUncompressedBytes + 16,
                                        (uint)Limits.MaxUncompressedBytes + 17, g.UIntEdgy()));
                byte[] chunk = g.Bytes(64);
                w.WriteBytes(chunk, 0, chunk.Length);
                byte[] wire = w.ToArray();
                sent += wire.Length;
                try
                {
                    reassembler.Accept(AsFragment(wire), out _, out _);
                }
                catch (DecodeException)
                {
                }
                Assert.True(reassembler.PartialMessages <= Limits.MaxPartialMessagesPerPeer);
            });
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            // the property runner itself allocates per case (generator, writer, envelope); the reassembly buffers must
            // not dominate: 4 partial messages × 8 MiB would be 32 MiB
            Assert.True(allocated < 4000L * 4096 + 64 * sent, "allocated " + allocated + " bytes for " + sent + " bytes of fragments");
        }

        // ------------------------------------------------------------------ messages

        [Test(TimeoutSeconds = 120)]
        public void EveryProtocolMessageDecoderStaysWithinItsAllocationBudget()
        {
            HandshakeDigests digests = MessageGoldenTests.SampleDigests();
            var manifest = new HandshakeManifest
            {
                Digests = digests,
                Mods = new List<ModEntry> { new ModEntry { Name = "Traffic", Version = "1", PlatformId = "9", DllHash = "ff", IsUiModule = true } },
                Content = new List<string> { "DLC" }, FormatTagNames = new List<string> { "tag" }, PrefabIds = new List<string> { "Prefab:A", "Prefab:B" },
                Settings = new List<SettingEntry> { new SettingEntry { Mod = "m", Key = "k", Value = "v", Pushable = true } },
                DataFiles = new List<DataFileDigestEntry> { new DataFileDigestEntry { File = "gates.json", Digest = 5 } }
            };
            var table = new List<(string Name, byte[][] Seeds, Action<byte[]> Decode)>
            {
                ("Hello", new[]
                {
                    Encode<HelloMessage>(HelloMessage.Write, new HelloMessage { Digests = digests, SteamId = 7, DisplayName = "Ann", ClientNonce = new byte[16], CompressionMask = 7 }),
                    Encode<HelloMessage>(HelloMessage.Write, new HelloMessage { Digests = default(HandshakeDigests) })
                }, b => HelloMessage.Decode(b, 0, b.Length)),
                ("Welcome", new[]
                {
                    Encode<WelcomeMessage>(WelcomeMessage.Write, new WelcomeMessage { SessionId = 9, AssignedPeer = new PeerId(3), Role = SessionRole.Viewer, Permissions = Permission.All,
                                                                                      SessionSettings = new byte[40], HostNonce = new byte[16], EnabledSysEntries = new uint[] { 1, 2, 3 }, Compression = CompressionKind.Lz4 })
                }, b => WelcomeMessage.Decode(b, 0, b.Length)),
                ("Refuse", new[] { Encode<RefuseMessage>(RefuseMessage.Write, new RefuseMessage { Reason = RefuseReason.BadPassword, Detail = "wrong password", MismatchReport = new byte[] { 1, 2, 3 } }) },
                 b => RefuseMessage.Decode(b, 0, b.Length)),
                ("ManifestDetail", new[]
                {
                    Encode<ManifestDetailMessage>(ManifestDetailMessage.Write, new ManifestDetailMessage { Manifest = manifest }),
                    Encode<ManifestDetailMessage>(ManifestDetailMessage.Write, new ManifestDetailMessage { Manifest = new HandshakeManifest() })
                }, b => ManifestDetailMessage.Decode(b, 0, b.Length)),
                ("PasswordChallenge", new[] { Encode<PasswordChallengeMessage>(PasswordChallengeMessage.Write, new PasswordChallengeMessage { HostNonce = new byte[16], Salt = new byte[16], SessionId = 3 }) },
                 b => PasswordChallengeMessage.Decode(b, 0, b.Length)),
                ("PasswordResponse", new[] { Encode<PasswordResponseMessage>(PasswordResponseMessage.Write, new PasswordResponseMessage { ClientNonce = new byte[16], Mac = new byte[32] }) },
                 b => PasswordResponseMessage.Decode(b, 0, b.Length)),
                ("Control", new[] { Encode<ControlMessage>(ControlMessage.Write, new ControlMessage { Kind = ControlKind.Toast, Payload = new byte[] { 1, 2, 3 } }) },
                 b => ControlMessage.Decode(b, 0, b.Length)),
                ("Chat", new[] { Encode<ChatMessage>(ChatMessage.Write, new ChatMessage { From = new PeerId(1), Text = "hello there", IsSystem = false }) },
                 b => ChatMessage.Decode(b, 0, b.Length)),
                ("View", new[] { Encode<ViewReport>(ViewReport.Write, new ViewReport { Zoom = 50, Selected = new NetSlot(1000), PanelTarget = new NetSlot(3) }) },
                 b => ViewReport.Decode(b, 0, b.Length)),
                ("Presence", new[]
                {
                    Encode<PresenceMessage>(PresenceMessage.Write, new PresenceMessage { From = new PeerId(2), Preview = new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, PreviewLength = 16, BrushRadius = 3 }),
                    Encode<PresenceMessage>(PresenceMessage.Write, new PresenceMessage())
                }, b => PresenceMessage.Decode(b, 0, b.Length)),
                ("Clock", new[] { Encode<ClockSample>(ClockSample.Write, new ClockSample { HostFrame = 99, FrameTime = 0.5f, SmoothSpeed = 1, Paused = true, HostTimeSeconds = 12.5 }) },
                 b => ClockSample.Decode(b, 0, b.Length)),
                ("Ack", new[] { Hex("ac02 05") }, b => AckMessage.Decode(b, 0, b.Length)),
                ("Ping", new[] { Encode<PingMessage>(PingMessage.Write, new PingMessage { Sequence = 5, SentSeconds = 1.25, IsReply = true }) },
                 b => PingMessage.Decode(b, 0, b.Length)),
                ("HashDetail", new[]
                {
                    Encode<HashDetailMessage>(HashDetailMessage.Write, new HashDetailMessage
                    {
                        Class = ReplicationClass.A, Bucket = 2, AtEpoch = new EpochId(77),
                        Slots = new[] { new NetSlot(2048), new NetSlot(2049), new NetSlot(3000) }, Hashes = new uint[] { 1, 2, 3 }
                    })
                }, b => HashDetailMessage.Decode(b, 0, b.Length)),
                ("RepairRequest", new[]
                {
                    Encode<RepairRequestMessage>(RepairRequestMessage.Write, new RepairRequestMessage { Class = ReplicationClass.S, Slots = new[] { new NetSlot(1), new NetSlot(70000) }, LevelHint = RepairLevel.Entity })
                }, b => RepairRequestMessage.Decode(b, 0, b.Length)),
                ("SnapshotManifest", new[]
                {
                    Encode<SnapshotManifest>(SnapshotManifestCodec.Write, new SnapshotManifest
                    {
                        SnapshotId = 1, Size = 123456, ChunkSize = 65536, Epoch = new EpochId(4), GameModeName = "Normal", RenderPrefabIds = new[] { "A", "B" },
                        ReferencedContent = new[] { "C" }, FamilyCounts = new Dictionary<Family, int> { { Family.Object, 5 }, { Family.Agent, 1000 } }
                    }),
                    // w8/mods: with the trailing asset sources
                    Encode<SnapshotManifest>(SnapshotManifestCodec.Write, new SnapshotManifest
                    {
                        SnapshotId = 2, Size = 10, ChunkSize = 1024, GameModeName = "Normal", FamilyCounts = null,
                        AssetSources = new List<AssetSourceUse> { new AssetSourceUse { Source = new AssetSource { Kind = AssetSourceKind.Package, Name = "Pack", PlatformId = "100454" }, Prefabs = 3, Example = "Pagoda" } }
                    })
                }, b => SnapshotManifestCodec.Decode(b, 0, b.Length)),
                ("ProposalReceived", new[] { Hex("e807") }, b => ProposalReceivedMessage.Decode(b, 0, b.Length)),
                // w8/mods
                ("ModChannel", new[] { Encode<ModChannelMessage>(ModChannelMessage.Write, new ModChannelMessage { Channel = "MyMod", From = new PeerId(2), Target = ModChannelTarget.Player, TargetPeer = new PeerId(3), Payload = new byte[] { 1, 2, 3 } }) },
                 b => ModChannelMessage.Decode(b, 0, b.Length)),
            };
            ulong seed = 840;
            foreach ((string name, byte[][] seeds, Action<byte[]> decode) in table)
            {
                DecoderContract.Fuzz(name, seeds, 700, seed++, decode);
            }
        }
    }
}
