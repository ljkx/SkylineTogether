using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Integrity;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Motion;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>
    /// The wire as a whole, pinned to <see cref="ProtocolVersion.Current"/>:
    /// <list type="bullet">
    /// <item>the number of every message, control and sync record kind (literals; a renumbered or new kind fails);</item>
    /// <item>for every message and control kind, the test that pins its layout (a golden vector, or a sample of the
    /// corpus below), so a new kind cannot ship without one;</item>
    /// <item>a corpus: one canonical sample of every payload codec without a hand-written vector (sync batches and their
    /// records, global payloads, terrain, water, cell maps, motion, sim values, session payloads, tool previews, ...),
    /// each pinned by its XXH64. Any layout change fails the corpus; the fix is a protocol version bump and new
    /// hashes in the same diff (docs/dev/releasing.md#protocol-version-policy).</item>
    /// </list>
    /// </summary>
    public sealed class WireCorpusTests
    {
        /// <summary>The protocol version the tables below describe.</summary>
        private const int PinnedProtocolVersion = 8;

        private const string Reserved = "reserved";
        private const string Corpus = "corpus:";

        // name | number | where the layout is pinned ("Class.Method" of a test, "corpus:<sample>", or reserved)
        private static readonly (string Name, int Number, string PinnedBy)[] MessageKinds =
        {
            ("Hello", 1, "MessageGoldenTests.Hello"),
            ("Welcome", 2, "MessageGoldenTests.Welcome"),
            ("Refuse", 3, "MessageGoldenTests.Refuse"),
            ("Control", 4, "WireCorpusTests.ControlEnvelope"),
            ("Epoch", 5, Corpus + "sync batch: entities"),
            ("Ack", 6, "MessageGoldenTests.AckAndPing"),
            ("Proposal", 7, Corpus + "sync batch: movers and proposals"),
            ("Motion", 8, Corpus + "motion: keyframe"),
            ("MotionControl", 9, Corpus + "motion: assign"),
            ("Clock", 10, Reserved),
            ("View", 11, "MessageGoldenTests.View"),
            ("Presence", 12, "MessageGoldenTests.Presence"),
            ("HashDetail", 13, Reserved),
            ("RepairRequest", 14, Reserved),
            ("BlobManifest", 15, Reserved),
            ("BlobChunk", 16, "WireGoldenTests.BlobChunkAndHave"),
            ("BlobHave", 17, "WireGoldenTests.BlobChunkAndHave"),
            ("Ping", 18, "MessageGoldenTests.AckAndPing"),
            ("Chat", 19, "MessageGoldenTests.Chat"),
            ("Terrain", 20, Corpus + "terrain: stamps"),
            ("Water", 21, Corpus + "water: tiles"),
            ("ApplyAck", 60, "WireGoldenTests.ApplyAck"),
            ("SimValues", 65, Corpus + "sim values"),
            ("CellMaps", 66, Corpus + "cell maps: patch"),
            ("Statistics", 67, "WireGoldenTests.StatisticsSeries"),
            ("AgentValues", 90, Corpus + "sim values"),
            ("AgentRefresh", 91, "WireGoldenTests.AgentRefresh"),
            ("FlowStatus", 100, "WireGoldenTests.FlowStatusElectricity"),
            ("ModChannel", 110, Corpus + "mod channel"),
        };

        private static readonly (string Name, int Number, string PinnedBy)[] ControlKinds =
        {
            ("PasswordChallenge", 0, "MessageGoldenTests.PasswordChallengeAndResponse"),
            ("PasswordResponse", 1, "MessageGoldenTests.PasswordChallengeAndResponse"),
            ("ManifestRequest", 2, Reserved),
            ("ManifestDetail", 3, "MessageGoldenTests.ManifestDetail"),
            ("SessionSettings", 4, Reserved),
            ("Players", 5, Corpus + "session: players"),
            ("Roles", 6, Reserved),
            ("HostBusy", 7, Reserved),
            ("HostReady", 8, Reserved),
            ("SessionEnd", 9, "WireGoldenTests.LocalTextWithAKeyAndAnArgument"),
            ("SessionReset", 10, Reserved),
            ("Kick", 11, "WireGoldenTests.LocalTextWithAKeyAndAnArgument"),
            ("Ban", 12, "WireGoldenTests.LocalTextWithAKeyAndAnArgument"),
            ("SpeedRequest", 13, Corpus + "session: speed"),
            ("SpeedChanged", 14, Corpus + "session: speed"),
            ("ResyncRequest", 15, Reserved),
            ("ResyncStart", 16, Reserved),
            ("Toast", 17, Corpus + "session: text"),
            ("SnapshotManifest", 18, "MessageGoldenTests.SnapshotManifest"),
            ("ReadyForSnapshot", 19, "WireCorpusTests.ControlEnvelope"),
            ("LoadDone", 20, Corpus + "session: load done"),
            ("Live", 21, Corpus + "session: speed"),
            ("ResumeRequest", 22, Reserved),
            ("ResumeResult", 23, Reserved),
            ("ProposalReceived", 24, Reserved),
            ("RepairThrottled", 25, Reserved),
            ("Disconnecting", 26, "WireCorpusTests.ControlEnvelope"),
            ("LineRowsRequest", 27, Reserved),
            ("LineRows", 28, Reserved),
            ("LoadProgress", 29, Corpus + "session: load progress"),
            ("PlayerAction", 30, Corpus + "session: player action"),
            ("JoinNotice", 31, Corpus + "session: mismatch report"),
            ("Authenticated", 32, "WireCorpusTests.ControlEnvelope"),
            ("Dropped", 33, "WireGoldenTests.LocalTextWithAKeyAndAnArgument"),
        };

        private static readonly (string Name, int Number)[] SyncRecordKinds =
        {
            ("Spawn", 1), ("Upsert", 2), ("Despawn", 3), ("Values", 4), ("Prefab", 5), ("ZoneCells", 6), ("IdRange", 7),
            ("Cost", 8), ("Clock", 9), ("Request", 10), ("Global", 11), ("IdCeiling", 12),
            ("MoverSpawn", 20), ("MoverState", 21), ("MoverDespawn", 22), ("MoverRoster", 23),
            ("BucketDigest", 40), ("EntityHashes", 41), ("RequestIds", 42), ("EntityExplain", 43), ("IconSet", 44),
            ("IconControl", 45), ("LineRows", 46),
            ("Epoch", 80), ("ProposalHeader", 81), ("ProposalResult", 82),
            ("EnabledBits", 110), ("ModSettings", 111), ("PrefabSource", 112), ("MoverAgents", 120),
            ("ReplayHashes", 160), ("ReplayMembers", 161), ("ReplayCheckpoint", 162),
        };

        // XXH64 of each corpus sample at PinnedProtocolVersion
        private static readonly Dictionary<string, ulong> PinnedHashes = new Dictionary<string, ulong>(StringComparer.Ordinal)
        {
            { "sync batch: entities", 0x12CF5952F2AC3DC3UL },
            { "sync batch: integrity", 0x7DFA3BB1AB8E7C45UL },
            { "sync batch: movers and proposals", 0x35E5AC8B48183397UL },
            { "global: ints", 0xD6C7088F4B398856UL },
            { "global: floats", 0x95F0AA886B4264D8UL },
            { "global: strings", 0xF98F8AF3C29C0FC8UL },
            { "global: policies", 0x95B13FAE3A51D3AEUL },
            { "global: actions", 0xD02C0019D425AD75UL },
            { "global: unlocks", 0xDE16549935EBC724UL },
            { "sim values", 0x281A611720B64A4EUL },
            { "sim values: sample", 0xEAE59BA42032CEEFUL },
            { "statistics: mixed", 0xE55C62F2723B068DUL },
            { "line rows", 0x09EE3189BA4ED21DUL },
            { "motion: ack", 0x924D44DCDB499EFEUL },
            { "motion: keyframe", 0x6B917735B0D20D77UL },
            { "motion: delta", 0xB265D90DA423B6C3UL },
            { "motion: assign", 0x3729B5C8E88DDB0EUL },
            { "terrain: stamps", 0xEDEE6AC5EA3A6D64UL },
            { "terrain: manifest", 0xEA4120D3ECFFC331UL },
            { "terrain: tile request", 0x5D2206DC5093C649UL },
            { "terrain: tiles", 0x159AD1286C486F07UL },
            { "water: tiles", 0xB56B433E5F1866B3UL },
            { "cell maps: patch", 0x99992C8986C2F9B1UL },
            { "cell maps: digest", 0x31B8CE9AA14B5E06UL },
            { "cell maps: resync", 0x970D649B5C36363FUL },
            { "session: players", 0xE54859814E720CF6UL },
            { "session: info", 0x56C746BD7436676DUL },
            { "session: speed", 0x483B889B4D44261AUL },
            { "session: load progress", 0x04A111D47CCF5A1BUL },
            { "session: load done", 0xDE20B55C67B51612UL },
            { "session: player action", 0x743E13EE0C4EE5A5UL },
            { "session: text", 0x66C7997DB762F8B6UL },
            { "session: mismatch report", 0x264BE7CB16DFF90AUL },
            { "mod channel", 0x9758CA4E2D7D1959UL },
            { "tool preview", 0x4D2925077A271EE6UL },
            { "join assets", 0xF1FEE1960FF1D361UL },
            { "blob: chunk header", 0x6BF6800306FD92EBUL },
        };

        private static void CheckNumbers<TEnum>(IEnumerable<(string Name, int Number)> table) where TEnum : struct, Enum
        {
            var expected = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var (name, number) in table) expected.Add(name, number);
            var actual = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (TEnum value in Enum.GetValues<TEnum>()) actual.Add(value.ToString(), Convert.ToInt32(value, CultureInfo.InvariantCulture));
            var problems = new List<string>();
            foreach (var pair in actual)
            {
                if (!expected.TryGetValue(pair.Key, out int number)) problems.Add(typeof(TEnum).Name + "." + pair.Key + " = " + pair.Value + " is new: add it here with a golden vector or corpus sample, and bump the protocol version");
                else if (number != pair.Value) problems.Add(typeof(TEnum).Name + "." + pair.Key + " is " + pair.Value + ", pinned " + number);
            }
            foreach (var pair in expected)
            {
                if (!actual.ContainsKey(pair.Key)) problems.Add(typeof(TEnum).Name + "." + pair.Key + " was removed: numbers stay taken (mark it reserved instead)");
            }
            if (problems.Count > 0) Assert.Fail(string.Join("\n", problems));
        }

        private static IEnumerable<(string, int)> Names((string Name, int Number, string PinnedBy)[] table)
        {
            foreach (var row in table) yield return (row.Name, row.Number);
        }

        [Test]
        public void KindNumbersArePinned()
        {
            Assert.Equal(PinnedProtocolVersion, ProtocolVersion.Current, "the tables of this class describe protocol version " + PinnedProtocolVersion);
            CheckNumbers<MessageKind>(Names(MessageKinds));
            CheckNumbers<ControlKind>(Names(ControlKinds));
            CheckNumbers<SyncRecordKind>(SyncRecordKinds);
        }

        [Test]
        public void EveryKindNamesWhereItsLayoutIsPinned()
        {
            Dictionary<string, Func<byte[]>> corpus = BuildCorpus();
            var problems = new List<string>();
            foreach (var (name, _, pinnedBy) in MessageKinds)
            {
                var kind = Enum.Parse<MessageKind>(name);
                Check("MessageKind." + name, pinnedBy, Limits.IsReservedKind(kind), corpus, problems);
            }
            foreach (var (name, _, pinnedBy) in ControlKinds)
            {
                var kind = Enum.Parse<ControlKind>(name);
                Check("ControlKind." + name, pinnedBy, ControlMessage.IsReservedKind(kind), corpus, problems);
            }
            if (problems.Count > 0) Assert.Fail(string.Join("\n", problems));
        }

        private static void Check(string what, string pinnedBy, bool reserved, Dictionary<string, Func<byte[]>> corpus, List<string> problems)
        {
            if (pinnedBy == Reserved)
            {
                if (!reserved) problems.Add(what + " is pinned as reserved but is in use");
                return;
            }
            if (reserved) problems.Add(what + " is reserved: pin it as reserved");
            if (pinnedBy.StartsWith(Corpus, StringComparison.Ordinal))
            {
                if (!corpus.ContainsKey(pinnedBy.Substring(Corpus.Length))) problems.Add(what + ": no corpus sample \"" + pinnedBy.Substring(Corpus.Length) + "\"");
                return;
            }
            int dot = pinnedBy.LastIndexOf('.');
            Type type = typeof(WireCorpusTests).Assembly.GetType("SkylineTogether.Core.Tests.Protocol." + pinnedBy.Substring(0, dot));
            MethodInfo method = type?.GetMethod(pinnedBy.Substring(dot + 1), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (method == null || method.GetCustomAttribute<TestAttribute>() == null) problems.Add(what + ": no test " + pinnedBy);
        }

        [Test]
        public void ControlEnvelope()
        {
            // u8 control kind | varint payload length | payload; the payloads of these kinds are plain byte fields:
            // Authenticated = varint proof length | proof, ReadyForSnapshot and Disconnecting = empty
            var w = new ByteWriter();
            ControlMessage.Write(w, ControlKind.Toast, new byte[] { 0xAB }, 0, 1);
            ProtocolTestUtil.AssertBytes("11 01 ab", w);
            w.Reset();
            ControlMessage.Write(w, ControlKind.ReadyForSnapshot, null, 0, 0);
            ProtocolTestUtil.AssertBytes("13 00", w);
            w.Reset();
            ControlMessage.Write(w, ControlKind.Disconnecting, null, 0, 0);
            ProtocolTestUtil.AssertBytes("1a 00", w);
            w.Reset();
            ControlMessage.Write(w, ControlKind.Authenticated, new byte[] { 2, 0xC1, 0xC2 }, 0, 3);
            ProtocolTestUtil.AssertBytes("20 03 02c1c2", w);
            ControlMessage read = ControlMessage.Read(ProtocolTestUtil.Reader(ProtocolTestUtil.Hex("20 03 02c1c2")));
            Assert.Equal(ControlKind.Authenticated, read.Kind);
            Assert.SequenceEqual(new byte[] { 2, 0xC1, 0xC2 }, read.Payload);
        }

        [Test]
        public void TheCorpusIsPinned()
        {
            Assert.Equal(PinnedProtocolVersion, ProtocolVersion.Current, "update the corpus hashes together with the protocol version");
            Dictionary<string, Func<byte[]>> corpus = BuildCorpus();
            var problems = new List<string>();
            var table = new StringBuilder();
            foreach (var pair in corpus)
            {
                byte[] bytes = pair.Value();
                ulong hash = XxHash64.Hash(bytes, 0, bytes.Length);
                table.Append("            { \"").Append(pair.Key).Append("\", 0x").Append(hash.ToString("X16", CultureInfo.InvariantCulture)).Append("UL },\n");
                if (!PinnedHashes.TryGetValue(pair.Key, out ulong pinned)) problems.Add("\"" + pair.Key + "\" has no pinned hash");
                else if (pinned != hash) problems.Add("\"" + pair.Key + "\" changed (" + bytes.Length + " bytes): a wire change");
            }
            foreach (string name in PinnedHashes.Keys)
            {
                if (!corpus.ContainsKey(name)) problems.Add("\"" + name + "\" is pinned but no longer in the corpus");
            }
            if (problems.Count > 0)
            {
                Assert.Fail(string.Join("\n", problems) + "\nIf the change is intended, bump ProtocolVersion.Current and PinnedProtocolVersion and pin:\n" + table);
            }
        }

        // ------------------------------------------------------------------ the corpus

        private static byte[] Bytes(Action<ByteWriter> write)
        {
            var w = new ByteWriter();
            write(w);
            return w.ToArray();
        }

        private static byte[] Batch(Action<SyncBatch.Writer> fill)
        {
            var w = new SyncBatch.Writer();
            fill(w);
            ArraySegment<byte> s = w.Finish();
            var copy = new byte[s.Count];
            Buffer.BlockCopy(s.Array, s.Offset, copy, 0, s.Count);
            return copy;
        }

        private static TerrainStamp Stamp(int i) => new TerrainStamp
        {
            ToolPrefab = 1000u + (uint)i, BrushPrefab = 7, PositionX = -1234.5f + i, PositionY = 512.25f, PositionZ = 77.125f,
            TargetX = 1, TargetY = 2, TargetZ = 3, StartX = -4, StartY = -5, StartZ = -6, Angle = 0.75f, Size = 150f,
            Strength = -0.0015f, Opacity = 0.25f, DeltaTime = 0.05f
        };

        private static MotionFrame Frame(int i) => new MotionFrame
        {
            X = 100.5f + i, Y = 20.25f, Z = -300.75f - i, VX = 1.5f, VY = 0f, VZ = -2.25f, QX = 0f, QY = 0.38268343f, QZ = 0f, QW = 0.9238795f,
            Flags = 3, State = 2, Activity = 1, StateTimer = 40,
        };

        private static Dictionary<string, Func<byte[]>> BuildCorpus()
        {
            var c = new Dictionary<string, Func<byte[]>>(StringComparer.Ordinal);
            c["sync batch: entities"] = () => Batch(w =>
            {
                w.BeginEntity(SyncRecordKind.Spawn, 77, Family.NetEdge, true);
                w.AddComponent(0x1122334455667788UL, SyncComponentKind.Data, new byte[] { 1, 2, 3 }, 0, 3);
                w.AddComponent(0xAAUL, SyncComponentKind.Presence, null, 0, 0);
                w.AddComponent(0xBBUL, SyncComponentKind.Buffer, new byte[] { 9, 8, 7, 6 }, 1, 2);
                w.EndEntity();
                w.BeginEntity(SyncRecordKind.Upsert, 78, Family.Object, false);
                w.EndEntity();
                w.Despawn(12);
                w.Prefab(3, "Hospital01", new byte[] { 5, 6, 7 }, 0, 3);
                w.ZoneCells(new ZoneBlockKey { OwnerNetId = 9, X = -100, Z = 64, DirX = 1024, DirZ = 0, SizeX = 4, SizeY = 6 }, new uint[] { 0, 0, 0, 7, 7, 8 });
                w.IdRange(1000, 4096);
                w.Cost(-12345);
                w.Clock(123456);
                w.Request(SyncRequest.ResendRoutes);
                w.Global(GlobalChannel.Tax, new byte[] { 1, 1, 0 }, 0, 3);
            });
            c["sync batch: integrity"] = () => Batch(w =>
            {
                w.BucketDigest(3, 1000, 0xFEEDUL);
                w.EntityHashes(3, new[] { new EntityHash(5, 1), new EntityHash(70000, 2) });
                w.RequestIds(SyncRequest.ResendRoutes, new uint[] { 1, 2, 300 });
                w.EntityExplain(5, new[] { new ComponentHash(1, 2, ComponentHashState.Value) });
                w.IconSet(SyncRef.ForNetId(9), new[] { new SyncIcon { Prefab = 4, Target = new SyncRef { Kind = SyncRef.LaneKey, Id = 2, Prefab = 3, Index = -1, X = 5, Y = 6, Z = 7 }, Priority = 2, X = 1f } });
                w.IconControl(IconControlMode.Digest, 4, 5, 0x1234UL);
                w.LineRows(new byte[] { 1, 0 }, 0, 2);
                w.EnabledBits(8, new[] { new SyncEnabledBit(0x55UL, true) });
                w.ModSettings(new[] { new SettingEntry { Mod = "m", Key = "k", Value = "v" } });
                w.PrefabSource(3, new AssetSource { Kind = AssetSourceKind.Package, Name = "Pack", PlatformId = "100454" });
            });
            c["sync batch: movers and proposals"] = () => Batch(w =>
            {
                w.Mover(SyncRecordKind.MoverSpawn, 40, new MoverRecord { Kind = MoverKind.Riding, PrefabNetId = 12, Layout = new uint[] { 1, 2 }, Route = 3, HasHumanFlags = true, Vehicle = 9, X = 1, Y = 2, Z = 3 });
                w.MoverDespawn(41);
                w.MoverAgents(40, new MoverAgentLinks { Agent = 1, Keeper = 2, Owner = 3 });
                w.MoverRoster(30, 2048, new uint[] { 30, 5, 40, 1, 1000, 300 });
                w.Epoch(17);
                w.ProposalHeader(5, 16, ProposalFlags.None);
                w.ProposalResult(5, RejectReason.None);
                w.ReplayHashes(1, 2, new[] { new EntityHash(3, 4) });
                w.ReplayMembers(3, Family.Object, 99, new[] { new ComponentHash(1, 1, ComponentHashState.Presence) });
                w.ReplayCheckpoint(1, ReplayCheckpointPhase.Begin, 100, new byte[] { 1, 2, 3 }, 0, 3);
            });
            c["global: ints"] = () => GlobalPayload.WriteInts(GlobalPayloadMode.Full, new[] { new KeyValuePair<uint, int>(1, -5), new KeyValuePair<uint, int>(9, int.MaxValue) });
            c["global: floats"] = () => GlobalPayload.WriteFloats(GlobalPayloadMode.Delta, new[] { new KeyValuePair<uint, float>(2, 0.5f) });
            c["global: strings"] = () => GlobalPayload.WriteStrings(GlobalPayloadMode.Full, new[] { new KeyValuePair<uint, string>(3, "Springfield"), new KeyValuePair<uint, string>(4, "") });
            c["global: policies"] = () => GlobalPayload.WritePolicies(GlobalPayloadMode.Full, new[] { new PolicyChange { Target = 1, Policy = 2, Flags = 1, Adjustment = 0.25f } });
            c["global: actions"] = () => ActionPayload.Write(new List<ActionIntent>
            {
                new ActionIntent { Kind = ActionKind.DevTreePurchase, Target = 7 },
                new ActionIntent { Kind = ActionKind.PurchaseMapTiles, Ids = new uint[] { 3, 900000 }, Amount = 125000 },
                new ActionIntent { Kind = ActionKind.SetMeshColor, Target = 11, Values = new float[12] },
                new ActionIntent { Kind = ActionKind.DeleteSubElements, Target = 2, Keys = new[] { new SubElementKey { Kind = 1, OwnerNetId = 5, PrefabNetId = 6, Index = -1, X = -100, Y = 3, Z = 1 << 20 } } },
            });
            c["global: unlocks"] = () => UnlockSet.Write(new List<uint> { 1, 2, 50, 70000, uint.MaxValue });
            c["sim values"] = () => Bytes(w =>
            {
                int count = SimValueMessage.BeginValues(w);
                int e1 = SimValueMessage.BeginEntity(w, 77, Family.Object);
                SimValueMessage.AddComponent(w, 0x1122334455667788UL, SyncComponentKind.Data, new byte[] { 0, 1, 2, 3 }, 1, 3);
                SimValueMessage.EndEntity(w, e1, 1);
                SimValueMessage.End(w, count, 1);
            });
            c["sim values: sample"] = () => Bytes(w =>
            {
                int count = SimValueMessage.BeginSample(w, 99, 5);
                SimValueMessage.End(w, count, 0);
            });
            c["statistics: mixed"] = () => Bytes(w => StatisticsMessage.WriteMixed(w, 33, 0xABCDEF01u, new List<StatisticSeries>
            {
                new StatisticSeries { Append = false, Type = 3, Parameter = 0, TotalLength = 3, Values = new[] { 1.5, -2.0, double.MaxValue }, Totals = new[] { 10.0, 20.0, 30.0 } },
                new StatisticSeries { Append = true, Type = 17, Parameter = -4, TotalLength = 40, Values = new[] { 0.25 }, Totals = new[] { 0.5 } },
            }));
            c["line rows"] = () => Bytes(w => LineStateReport.WriteRows(w, new List<LineStateRow> { new LineStateRow { Line = new NetSlot(5), Stops = 3, LengthDecimetres = 1234, Active = true } }));
            c["motion: ack"] = () => Bytes(w => MotionClientStager.WriteAck(w, 0xFFFFFFF0u));
            c["motion: keyframe"] = () => Bytes(w => MotionCodec.WriteQBKeyframe(w, 4, 2, Frame(0), true));
            c["motion: delta"] = () => Bytes(w => MotionCodec.WriteQBDelta(w, 4, 2, 3, Frame(1), Frame(0), false));
            c["motion: assign"] = () => Bytes(w => MotionCodec.WriteQA(w, 600, 1, new NetSlot(42), 3, new[] { Frame(0), Frame(1), Frame(2), Frame(3) }, new uint[] { 19, 35, 51, 67 }));
            var grid = new TileGridInfo(4096, 4096, 64);
            c["terrain: stamps"] = () => Bytes(w => TerrainMessage.WriteStamps(w, new PeerId(3), new[] { Stamp(1), Stamp(2) }));
            c["terrain: manifest"] = () => Bytes(w => TerrainMessage.WriteManifest(w, grid, new[] { new KeyValuePair<TileId, ulong>(new TileId(1, 2), 77UL) }));
            c["terrain: tile request"] = () => Bytes(w => TerrainMessage.WriteTileRequest(w, grid, new[] { new TileId(1, 2), new TileId(3, 4) }));
            c["terrain: tiles"] = () => Bytes(w =>
            {
                var texels = new ushort[64 * 64];
                for (int i = 0; i < texels.Length; i++) texels[i] = (ushort)(8191 + (i % 64) * 3 + i / 640);
                int at = TerrainMessage.BeginTiles(w, grid);
                TerrainMessage.AddTile(w, new TileId(0, 0), texels, texels.Length);
                TerrainMessage.EndTiles(w, at, 1);
            });
            c["water: tiles"] = () => Bytes(w =>
            {
                var waterInfo = new TileGridInfo(2048, 2048, 256);
                const int waterTexels = 256 * 256;
                var rgba = new float[waterTexels * 4];
                for (int i = 0; i < waterTexels; i++) rgba[i * 4] = i % 7 == 0 ? 0f : 12.5f;
                int at = WaterMessage.BeginTiles(w, 511.7f, waterInfo);
                WaterMessage.AddTile(w, new TileId(7, 3), rgba, waterTexels, WaterTileLevel.DepthPollutionVelocity);
                WaterMessage.EndTiles(w, at, 1);
            });
            c["cell maps: patch"] = () => Bytes(w =>
            {
                var layout = new CellMapLayout(16, 16, 6, 16);
                var map = new byte[layout.MapBytes];
                for (int i = 0; i < map.Length; i++) map[i] = (byte)(i * 7);
                CellMapMessage.WritePatch(w, 9, layout, 2, 2, 3, 3, map, new byte[64 * 64 * 6]);
            });
            c["cell maps: digest"] = () => Bytes(w => CellMapMessage.WriteDigest(w, 9, new CellMapLayout(16, 16, 6, 16), 0x0123456789ABCDEFUL));
            c["cell maps: resync"] = () => Bytes(w => CellMapMessage.WriteResync(w, 9, new CellMapLayout(16, 16, 6, 16)));
            c["session: players"] = () => Bytes(w => SessionPayloads.WritePlayers(w, new List<PlayerInfo>
            {
                new PlayerInfo { Id = PeerId.Host, Name = "Host", SteamId = 1, Role = SessionRole.Host, State = PlayerState.Live },
                new PlayerInfo { Id = new PeerId(2), Name = "Ann", SteamId = 2, Role = SessionRole.Viewer, State = PlayerState.Downloading, PingMs = 40, Progress = 0.5f },
            }));
            c["session: info"] = () => Bytes(w => SessionPayloads.WriteSessionInfo(w, new SessionInfo { SessionName = "City", HostName = "Host" }));
            c["session: speed"] = () => Bytes(w => SessionPayloads.WriteSpeed(w, new SpeedState { Speed = 3, Paused = false, Held = true }));
            c["session: load progress"] = () => Bytes(w => SessionPayloads.WriteLoadProgress(w, JoinPhase.Loading, 0.25f));
            c["session: load done"] = () => Bytes(w =>
            {
                w.WriteBool(false);
                SessionPayloads.WriteText(w, "missing asset pack");
            });
            c["session: player action"] = () => Bytes(w => SessionPayloads.WritePlayerAction(w, PlayerActionKind.SetRole, new PeerId(2), SessionRole.Viewer));
            c["session: text"] = () => Bytes(w => SessionPayloads.WriteText(w, "The host ended the session"));
            c["session: mismatch report"] = () => MismatchReport.Encode(MismatchReport.CompareMods(
                new List<ModEntry> { new ModEntry { Name = "Move It", Version = "1.0", PlatformId = "100" } },
                new List<ModEntry> { new ModEntry { Name = "Traffic", Version = "2.0", PlatformId = "200" } }));
            c["mod channel"] = () => Bytes(w => ModChannelMessage.Write(w, new ModChannelMessage { Channel = "RoadBuilder", From = new PeerId(2), Target = ModChannelTarget.Player, TargetPeer = new PeerId(3), Payload = new byte[] { 1, 2, 3, 0, 255 } }));
            c["tool preview"] = () => Bytes(w =>
            {
                var preview = new ToolPreview();
                preview.AddCurve(ToolPreviewFlags.None, new PreviewPoint(0, 0, 0), new PreviewPoint(10, 0, 0), new PreviewPoint(20, 1, 5), new PreviewPoint(30, 2, 10), 8f);
                preview.AddCircle(ToolPreviewFlags.None, new PreviewPoint(100, 5, 100), 40f);
                preview.BeginPolyline(ToolPreviewFlags.Closed, 3f);
                for (int i = 0; i < 5; i++) preview.AddPolylinePoint(new PreviewPoint(i * 3, 0, i * i));
                preview.EndPolyline();
                preview.AddZoneBlock(ToolPreviewFlags.None, new PreviewPoint(50, 0, 50), 0.5f, 4, 6, ToolPreview.ZoneMask(24));
                ToolPreviewCodec.Encode(preview, w);
            });
            c["join assets"] = () => JoinAssetBundle.Write(new List<JoinAssetEntry>
            {
                new JoinAssetEntry { Kind = (JoinAssetKind)1, TypeName = "Game.Prefabs.BuildingPrefab", Name = "Custom", Guid = "abc", Data = new byte[] { 1, 2, 3 } },
            });
            c["blob: chunk header"] = () => Bytes(w => BlobCodec.WriteChunkHeader(w, 3, 7, 100, 0xABCDUL));
            return c;
        }
    }
}
