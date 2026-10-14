using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Integrity;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Motion;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// The decoder contract (<see cref="DecoderContract"/>) for every decoder the game layer runs on bytes from another
    /// player: the sync batch and its records, global payloads, actions, unlocks, terrain, water, flow, sim values,
    /// statistics, acks, session payloads, blob framing, tool previews, mismatch reports and join assets. Each is fed
    /// mutations of valid encodings: only <see cref="DecodeException"/> may escape, and allocation stays proportional
    /// to the input. Plus a seeded round-trip property over random sync batches.
    /// </summary>
    public sealed class SyncDecoderContractTests
    {
        private const int Iterations = 600;

        private static byte[] Bytes(ByteWriter w) => w.ToArray();

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

        private static void Fuzz(string name, byte[][] seeds, ulong seed, Action<byte[]> decode, long perByte = 256, long fixedBytes = 256 * 1024)
        {
            DecoderContract.Fuzz(name, seeds, Iterations, seed, decode, perByte, fixedBytes);
        }

        [Test(TimeoutSeconds = 120)]
        public void SyncBatchesObeyTheDecoderContract()
        {
            byte[] rich = Batch(w =>
            {
                w.BeginEntity(SyncRecordKind.Spawn, 77, Family.NetEdge, true);
                w.AddComponent(0x1122334455667788UL, SyncComponentKind.Data, new byte[] { 1, 2, 3 }, 0, 3);
                w.AddComponent(0xAAUL, SyncComponentKind.Presence, null, 0, 0);
                w.AddComponent(0xBBUL, SyncComponentKind.Buffer, new byte[] { 9, 8, 7, 6 }, 1, 2);
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
            byte[] integrity = Batch(w =>
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
                w.PrefabSource(3, new AssetSource { Kind = AssetSourceKind.Package, Name = "Pack", PlatformId = "100454" }); // w8/mods
            });
            byte[] movers = Batch(w =>
            {
                w.Mover(SyncRecordKind.MoverSpawn, 40, new MoverRecord { Kind = MoverKind.Riding, PrefabNetId = 12, Layout = new uint[] { 1, 2 }, Route = 3, HasHumanFlags = true, Vehicle = 9, X = 1, Y = 2, Z = 3 });
                w.MoverDespawn(41);
                w.MoverAgents(40, new MoverAgentLinks { Agent = 1, Keeper = 2, Owner = 3 });
                w.Epoch(17);
                w.ProposalHeader(5, 16, ProposalFlags.None);
                w.ProposalResult(5, RejectReason.None);
                w.ReplayHashes(1, 2, new[] { new EntityHash(3, 4) });
                w.ReplayMembers(3, Family.Object, 99, new[] { new ComponentHash(1, 1, ComponentHashState.Presence) });
                w.ReplayCheckpoint(1, ReplayCheckpointPhase.Begin, 100, new byte[] { 1, 2, 3 }, 0, 3);
            });
            int rejected = DecoderContract.Fuzz("sync batch", new[] { rich, integrity, movers }, 4000, 900, b => SyncBatch.Read(b, 0, b.Length));
            Assert.True(rejected > 400, "mutations are rejected now and then (" + rejected + ")");
        }

        [Test(TimeoutSeconds = 120)]
        public void ChannelPayloadsObeyTheDecoderContract()
        {
            var ints = new List<KeyValuePair<uint, int>>();
            var floats = new List<KeyValuePair<uint, float>>();
            var strings = new List<KeyValuePair<uint, string>>();
            var policies = new List<PolicyChange>();
            Fuzz("global ints", new[] { GlobalPayload.WriteInts(GlobalPayloadMode.Full, new[] { new KeyValuePair<uint, int>(1, -5), new KeyValuePair<uint, int>(9, int.MaxValue) }) },
                 910, b => GlobalPayload.ReadInts(b, 0, b.Length, ints));
            Fuzz("global floats", new[] { GlobalPayload.WriteFloats(GlobalPayloadMode.Delta, new[] { new KeyValuePair<uint, float>(2, 0.5f) }) },
                 911, b => GlobalPayload.ReadFloats(b, 0, b.Length, floats));
            Fuzz("global strings", new[] { GlobalPayload.WriteStrings(GlobalPayloadMode.Full, new[] { new KeyValuePair<uint, string>(3, "Springfield"), new KeyValuePair<uint, string>(4, "") }) },
                 912, b => GlobalPayload.ReadStrings(b, 0, b.Length, strings));
            Fuzz("global policies", new[] { GlobalPayload.WritePolicies(GlobalPayloadMode.Full, new[] { new PolicyChange { Target = 1, Policy = 2, Flags = 1, Adjustment = 0.25f } }) },
                 913, b => GlobalPayload.ReadPolicies(b, 0, b.Length, policies));
            Fuzz("actions", new[]
            {
                ActionPayload.Write(new List<ActionIntent>
                {
                    new ActionIntent { Kind = ActionKind.DevTreePurchase, Target = 7 },
                    new ActionIntent { Kind = ActionKind.PurchaseMapTiles, Ids = new uint[] { 3, 900000 }, Amount = 125000 },
                    new ActionIntent { Kind = ActionKind.SetMeshColor, Target = 11, Values = new float[12] },
                    new ActionIntent { Kind = ActionKind.DeleteSubElements, Target = 2, Keys = new[] { new SubElementKey { Kind = 1, OwnerNetId = 5, PrefabNetId = 6, Index = -1, X = -100, Y = 3, Z = 1 << 20 } } },
                })
            }, 914, b => ActionPayload.Read(b, 0, b.Length));
            Fuzz("unlocks", new[] { UnlockSet.Write(new List<uint> { 1, 2, 50, 70000, uint.MaxValue }) }, 915, b => UnlockSet.Read(b, 0, b.Length));

            var w = new ByteWriter();
            AgentRefreshMessage.Write(w, new uint[] { 1, 0, 300000, 16_777_215 });
            Fuzz("agent refresh", new[] { Bytes(w) }, 916, b => AgentRefreshMessage.Read(b, 0, b.Length));
            w = new ByteWriter();
            ApplyAckMessage.Write(w, 123_456_789_012);
            Fuzz("apply ack", new[] { Bytes(w) }, 917, b => ApplyAckMessage.Read(b, 0, b.Length));

            w = new ByteWriter();
            int at = FlowStatusMessage.Begin(w, FlowNetwork.Water, FlowStatusMessage.FlagKeyframe);
            FlowStatusMessage.WriteEntry(w, FlowNetwork.Water, new FlowKey(new FlowNodeKey(0, FlowNodeRole.Source), new FlowNodeKey(300000, FlowNodeRole.Producer)),
                                         new FlowValue { Flow = -12, Capacity = 1073741823, SewageFlow = 40, SewageCapacity = 600, Pollution = 0.25f, Flags = 5 });
            FlowStatusMessage.WriteEntry(w, FlowNetwork.Water, new FlowKey(new FlowNodeKey(16_000_000, FlowNodeRole.Net), new FlowNodeKey(3, FlowNodeRole.Net)), default);
            FlowStatusMessage.End(w, at, 2);
            var flows = new List<KeyValuePair<FlowKey, FlowValue>>();
            Fuzz("flow status", new[] { Bytes(w) }, 918, b => FlowStatusMessage.Read(b, 0, b.Length, flows, out _));

            w = new ByteWriter();
            int count = SimValueMessage.BeginValues(w);
            int e1 = SimValueMessage.BeginEntity(w, 77, Family.Object);
            SimValueMessage.AddComponent(w, 0x1122334455667788UL, SyncComponentKind.Data, new byte[] { 0, 1, 2, 3 }, 1, 3);
            SimValueMessage.EndEntity(w, e1, 1);
            SimValueMessage.End(w, count, 1);
            byte[] values = Bytes(w);
            w = new ByteWriter();
            count = SimValueMessage.BeginSample(w, 99, 5);
            SimValueMessage.End(w, count, 0);
            Fuzz("sim values", new[] { values, Bytes(w) }, 919, b => SimValueMessage.Read(b, 0, b.Length, out _, out _, out _));

            w = new ByteWriter();
            StatisticsMessage.Write(w, 33, 0xABCDEF01u, new List<StatisticSeries>
            {
                new StatisticSeries { Type = 3, Parameter = 0, Values = new[] { 1.5, -2.0, double.MaxValue }, Totals = new[] { 10.0, 20.0, 30.0 } },
                new StatisticSeries { Type = 17, Parameter = -4 },
            });
            Fuzz("statistics", new[] { Bytes(w) }, 920, b => StatisticsMessage.Read(b, 0, b.Length, out _, out _));

            w = new ByteWriter();
            LineStateReport.WriteRows(w, new List<LineStateRow> { new LineStateRow { Line = new NetSlot(5), Stops = 3, LengthDecimetres = 1234, Active = true } });
            Fuzz("line rows", new[] { Bytes(w) }, 921, b => LineStateReport.ReadRows(new ByteReader(b, 0, b.Length)));

            w = new ByteWriter();
            MotionClientStager.WriteAck(w, 0xFFFFFFF0u);
            Fuzz("motion ack", new[] { Bytes(w) }, 922, b => MotionClientStager.TryReadAck(b, 0, b.Length, out _));
        }

        [Test(TimeoutSeconds = 120)]
        public void TerrainAndWaterMessagesObeyTheDecoderContract()
        {
            var grid = new TileGridInfo(4096, 4096, 64);
            var w = new ByteWriter();
            TerrainMessage.WriteStamps(w, new PeerId(3), new[] { Stamp(1), Stamp(2) });
            byte[] stamps = Bytes(w);
            w = new ByteWriter();
            TerrainMessage.WriteManifest(w, grid, new[] { new KeyValuePair<TileId, ulong>(new TileId(1, 2), 77UL) });
            byte[] manifest = Bytes(w);
            w = new ByteWriter();
            TerrainMessage.WriteTileRequest(w, grid, new[] { new TileId(1, 2), new TileId(3, 4) });
            byte[] request = Bytes(w);
            w = new ByteWriter();
            var texels = new ushort[64 * 64];
            for (int i = 0; i < texels.Length; i++) texels[i] = (ushort)(8191 + (i % 64) * 3 + i / 640);
            int at = TerrainMessage.BeginTiles(w, grid);
            TerrainMessage.AddTile(w, new TileId(0, 0), texels, texels.Length);
            TerrainMessage.EndTiles(w, at, 1);
            byte[] tiles = Bytes(w);

            var stampList = new List<TerrainStamp>();
            var hashList = new List<KeyValuePair<TileId, ulong>>();
            var tileList = new List<TileId>();
            var output = new ushort[64 * 64];
            Fuzz("terrain", new[] { stamps, manifest, request, tiles }, 930, b =>
            {
                var r = new ByteReader(b, 0, b.Length);
                switch (TerrainMessage.ReadHeader(r))
                {
                    case TerrainMessageType.Stamps:
                        TerrainMessage.ReadStamps(r, stampList);
                        break;
                    case TerrainMessageType.Manifest:
                        TerrainMessage.ReadManifest(r, hashList);
                        break;
                    case TerrainMessageType.TileRequest:
                        TerrainMessage.ReadTileRequest(r, tileList);
                        break;
                    case TerrainMessageType.Tiles:
                    {
                        int n = TerrainMessage.ReadTilesHeader(r, out TileGridInfo info);
                        if (info.TileSize != 64) return; // the game sizes its buffers from its own grid and drops others
                        TileGrid g = TerrainMessage.ToGrid(info);
                        for (int i = 0; i < n; i++) TerrainMessage.ReadTile(r, g, output, out _, out _);
                        break;
                    }
                }
            });

            var waterInfo = new TileGridInfo(2048, 2048, 256);
            const int waterTexels = 256 * 256;
            var rgba = new float[waterTexels * 4];
            for (int i = 0; i < waterTexels; i++) rgba[i * 4] = i % 7 == 0 ? 0f : 12.5f;
            w = new ByteWriter(1 << 16);
            at = WaterMessage.BeginTiles(w, 511.7f, waterInfo);
            WaterMessage.AddTile(w, new TileId(7, 3), rgba, waterTexels, WaterTileLevel.DepthPollutionVelocity);
            WaterMessage.EndTiles(w, at, 1);
            byte[] water = Bytes(w);
            var waterOut = new float[waterTexels * 4];
            Fuzz("water", new[] { water }, 931, b =>
            {
                var r = new ByteReader(b, 0, b.Length);
                int n = WaterMessage.ReadHeader(r, out _, out TileGridInfo info);
                if (info.TileSize != 256) return;
                TileGrid g = TerrainMessage.ToGrid(info);
                for (int i = 0; i < n; i++) WaterMessage.ReadTile(r, g, waterOut, out _);
            });
        }

        [Test(TimeoutSeconds = 120)]
        public void SessionPayloadsAndFramingObeyTheDecoderContract()
        {
            var w = new ByteWriter();
            SessionPayloads.WritePlayers(w, new List<PlayerInfo>
            {
                new PlayerInfo { Id = PeerId.Host, Name = "Host", SteamId = 1, Role = SessionRole.Host, State = PlayerState.Live },
                new PlayerInfo { Id = new PeerId(2), Name = "Ann", SteamId = 2, Role = SessionRole.Viewer, State = PlayerState.Downloading, PingMs = 40, Progress = 0.5f },
            });
            byte[] players = Bytes(w);
            Fuzz("players", new[] { players }, 940, b => SessionPayloads.ReadPlayers(new ByteReader(b, 0, b.Length)));
            w = new ByteWriter();
            SessionPayloads.WriteSessionInfo(w, new SessionInfo { SessionName = "City", HostName = "Host" });
            Fuzz("session info", new[] { Bytes(w) }, 941, b => SessionPayloads.ReadSessionInfo(new ByteReader(b, 0, b.Length)));
            w = new ByteWriter();
            SessionPayloads.WriteSpeed(w, new SpeedState { Speed = 3, Paused = false, Held = true });
            Fuzz("speed", new[] { Bytes(w) }, 942, b => SessionPayloads.ReadSpeed(new ByteReader(b, 0, b.Length)));
            w = new ByteWriter();
            SessionPayloads.WriteLoadProgress(w, JoinPhase.Loading, 0.25f);
            Fuzz("load progress", new[] { Bytes(w) }, 943, b => SessionPayloads.ReadLoadProgress(new ByteReader(b, 0, b.Length), out _));
            w = new ByteWriter();
            SessionPayloads.WriteText(w, "The host ended the session");
            Fuzz("text", new[] { Bytes(w) }, 944, b => SessionPayloads.ReadText(new ByteReader(b, 0, b.Length)));

            byte[] report = MismatchReport.Encode(MismatchReport.CompareMods(
                new List<ModEntry> { new ModEntry { Name = "Move It", Version = "1.0", PlatformId = "100" } },
                new List<ModEntry> { new ModEntry { Name = "Traffic", Version = "2.0", PlatformId = "200" } }));
            // the mismatch report is the one decoder that tolerates garbage (a refusal must always be shown)
            Fuzz("mismatch report", new[] { report }, 945, b => MismatchReport.Decode(b));

            w = new ByteWriter();
            BlobCodec.WriteManifest(w, new BlobManifestInfo { BlobId = 3, Size = 1_000_000, ChunkSize = BlobCodec.DefaultChunkBytes, Digest = new Core.Hashing.Digest128(1, 2), Purpose = "snapshot" });
            Fuzz("blob manifest", new[] { Bytes(w) }, 946, b => BlobCodec.DecodeManifest(b, 0, b.Length));
            w = new ByteWriter();
            BlobCodec.WriteHave(w, 3, new byte[] { 0xFF, 0x0F }, 0, 2);
            Fuzz("blob have", new[] { Bytes(w) }, 947, b => BlobCodec.DecodeHave(b, 0, b.Length, out _));
            w = new ByteWriter();
            BlobCodec.WriteChunkHeader(w, 3, 7, 100, 0xABCDUL);
            Fuzz("blob chunk header", new[] { Bytes(w) }, 948, b => BlobCodec.ReadChunkHeader(new ByteReader(b, 0, b.Length), out _, out _, out _, out _));

            var preview = new ToolPreview();
            preview.AddCurve(ToolPreviewFlags.None, new PreviewPoint(0, 0, 0), new PreviewPoint(10, 0, 0), new PreviewPoint(20, 1, 5), new PreviewPoint(30, 2, 10), 8f);
            preview.AddCircle(ToolPreviewFlags.None, new PreviewPoint(100, 5, 100), 40f);
            preview.BeginPolyline(ToolPreviewFlags.Closed, 3f);
            for (int i = 0; i < 5; i++) preview.AddPolylinePoint(new PreviewPoint(i * 3, 0, i * i));
            preview.EndPolyline();
            preview.AddZoneBlock(ToolPreviewFlags.None, new PreviewPoint(50, 0, 50), 0.5f, 4, 6, ToolPreview.ZoneMask(24));
            w = new ByteWriter();
            ToolPreviewCodec.Encode(preview, w);
            var into = new ToolPreview();
            Fuzz("tool preview", new[] { Bytes(w) }, 949, b => ToolPreviewCodec.Decode(b, 0, b.Length, into));

            byte[] assets = JoinAssetBundle.Write(new List<JoinAssetEntry>
            {
                new JoinAssetEntry { Kind = (JoinAssetKind)1, TypeName = "Game.Prefabs.BuildingPrefab", Name = "Custom", Guid = "abc", Data = new byte[] { 1, 2, 3 } },
            });
            Fuzz("join assets", new[] { Slice(assets, JoinAssetBundle.HeaderSize) }, 950, b => JoinAssetBundle.ReadBody(b, 0, b.Length));
        }

        private static byte[] Slice(byte[] data, int from)
        {
            var b = new byte[data.Length - from];
            Buffer.BlockCopy(data, from, b, 0, b.Length);
            return b;
        }

        // ------------------------------------------------------------------ round trip

        /// <summary>
        /// Random batches of every record kind the writer offers: decoding and re-encoding the records yields the same
        /// bytes (so no field is lost or altered on the way), and the record count and kinds survive.
        /// </summary>
        [Test(TimeoutSeconds = 120)]
        public void RandomSyncBatchesRoundTripExactly()
        {
            Prop.ForAll("sync batch round trip", 960, 300, g =>
            {
                byte[] original = Batch(w =>
                {
                    int records = g.Size(40);
                    for (int i = 0; i < records; i++) WriteRandomRecord(g, w);
                });
                List<SyncRecord> decoded = SyncBatch.Read(original, 0, original.Length);
                byte[] again = Batch(w =>
                {
                    foreach (SyncRecord rec in decoded) Rewrite(w, rec);
                });
                Assert.SequenceEqual(original, again, "re-encoded batch");
            });
        }

        private static void WriteRandomRecord(Gen g, SyncBatch.Writer w)
        {
            switch (g.Int(23))
            {
                case 0:
                {
                    w.BeginEntity(g.Pick(SyncRecordKind.Spawn, SyncRecordKind.Upsert, SyncRecordKind.Values), g.UIntEdgy(), g.Enum<Family>(), g.Bool());
                    int n = g.Size(6);
                    for (int c = 0; c < n; c++)
                    {
                        SyncComponentKind kind = g.Pick(SyncComponentKind.Data, SyncComponentKind.Buffer, SyncComponentKind.Presence);
                        byte[] data = g.Bytes(40);
                        w.AddComponent(g.ULong(), kind, data, 0, kind == SyncComponentKind.Presence ? 0 : data.Length);
                    }
                    w.EndEntity();
                    break;
                }
                case 1: w.Despawn(g.UIntEdgy()); break;
                case 2:
                {
                    byte[] data = g.Bytes(64);
                    w.Prefab(g.UIntEdgy(), g.String(120), data, 0, data.Length);
                    break;
                }
                case 3:
                {
                    var ids = new List<uint>();
                    int n = g.Size(4096);
                    uint v = g.UIntEdgy();
                    for (int i = 0; i < n; i++)
                    {
                        if (g.OneIn(8)) v = g.UIntEdgy();
                        ids.Add(v);
                    }
                    w.ZoneCells(new ZoneBlockKey { OwnerNetId = g.UIntEdgy(), X = (int)g.UInt(), Z = (int)g.UInt(), DirX = g.Int(-1024, 1025), DirZ = g.Int(-1024, 1025), SizeX = g.Int(0, 11), SizeY = g.Int(0, 7) }, ids);
                    break;
                }
                case 4: w.IdRange(g.UIntEdgy(), g.UIntEdgy()); break;
                case 5: w.Cost(g.Long()); break;
                case 6: w.Clock(g.UInt()); break;
                case 7: w.Request(g.Enum<SyncRequest>()); break;
                case 8:
                {
                    byte[] data = g.Bytes(100);
                    w.Global(g.UInt(), data, 0, data.Length);
                    break;
                }
                case 9: w.BucketDigest(g.UIntEdgy(), g.UIntEdgy(), g.ULong()); break;
                case 10: w.EntityHashes(g.UIntEdgy(), g.List(50, x => new EntityHash(x.UIntEdgy(), x.ULong()))); break;
                case 11: w.RequestIds(g.Enum<SyncRequest>(), g.List(100, x => x.UIntEdgy())); break;
                case 12: w.EntityExplain(g.UIntEdgy(), g.List(20, x => new ComponentHash(x.ULong(), x.ULong(), x.Enum<ComponentHashState>()))); break;
                case 13: w.IconSet(RandomRef(g), g.List(10, x => new SyncIcon { Prefab = x.UIntEdgy(), Target = RandomRef(x), Priority = (byte)x.Int(256), Flags = (byte)x.Int(256), X = x.Float(), Y = x.Float(), Z = x.Float() })); break;
                case 14: w.IconControl(g.Enum<IconControlMode>(), g.UIntEdgy(), g.UIntEdgy(), g.ULong()); break;
                case 15:
                {
                    byte[] data = g.Bytes(80);
                    w.LineRows(data, 0, data.Length);
                    break;
                }
                case 16: w.MoverDespawn(g.UIntEdgy()); break;
                case 17: w.MoverAgents(g.UIntEdgy(), new MoverAgentLinks { Agent = g.UIntEdgy(), Keeper = g.UIntEdgy(), Owner = g.UIntEdgy() }); break;
                case 18: w.EnabledBits(g.UIntEdgy(), g.List(20, x => new SyncEnabledBit(x.ULong(), x.Bool()))); break;
                case 19: w.ModSettings(g.List(5, x => new SettingEntry { Mod = x.String(30), Key = x.String(30), Value = x.String(200) })); break;
                case 20: w.Epoch(g.UIntEdgy()); break;
                case 21: w.PrefabSource(g.UIntEdgy(), new AssetSource { Kind = g.Enum<AssetSourceKind>(), Name = g.String(120), PlatformId = g.String(12) }); break; // w8/mods
                default:
                    if (g.Bool()) w.ProposalHeader(g.UIntEdgy(), g.UIntEdgy(), g.Enum<ProposalFlags>());
                    else w.ProposalResult(g.UIntEdgy(), g.Enum<RejectReason>());
                    break;
            }
        }

        private static SyncRef RandomRef(Gen g)
        {
            switch (g.Int(4))
            {
                case 0: return default;
                case 1: return SyncRef.ForNetId(g.UIntEdgy());
                default:
                    return new SyncRef { Kind = g.Pick(SyncRef.LaneKey, SyncRef.SubObjectKey), Id = g.UIntEdgy(), Prefab = g.UIntEdgy(), Index = (int)g.UInt(), X = (int)g.UInt(), Y = (int)g.UInt(), Z = (int)g.UInt() };
            }
        }

        private static void Rewrite(SyncBatch.Writer w, SyncRecord rec)
        {
            switch (rec.Kind)
            {
                case SyncRecordKind.Spawn:
                case SyncRecordKind.Upsert:
                case SyncRecordKind.Values:
                    w.BeginEntity(rec.Kind, rec.NetId, rec.Family, rec.CompleteSet);
                    foreach (SyncComponent c in rec.Components) w.AddComponent(c.TypeHash, c.Kind, c.Data, c.Offset, c.Count);
                    w.EndEntity();
                    break;
                case SyncRecordKind.Despawn: w.Despawn(rec.NetId); break;
                case SyncRecordKind.Prefab: w.Prefab(rec.NetId, rec.PrefabName, rec.PrefabData, 0, rec.PrefabData.Length); break;
                case SyncRecordKind.ZoneCells: w.ZoneCells(rec.Block, rec.ZoneIds); break;
                case SyncRecordKind.IdRange: w.IdRange(rec.RangeStart, rec.RangeCount); break;
                case SyncRecordKind.Cost: w.Cost(rec.Amount); break;
                case SyncRecordKind.Clock: w.Clock(rec.Frame); break;
                case SyncRecordKind.Request: w.Request(rec.Request); break;
                case SyncRecordKind.Global: w.Global(rec.Channel, rec.Payload, 0, rec.Payload.Length); break;
                case SyncRecordKind.BucketDigest: w.BucketDigest(rec.Bucket, rec.Count, rec.Hash); break;
                case SyncRecordKind.EntityHashes: w.EntityHashes(rec.Bucket, rec.Hashes); break;
                case SyncRecordKind.RequestIds: w.RequestIds(rec.Request, rec.Ids); break;
                case SyncRecordKind.EntityExplain: w.EntityExplain(rec.NetId, rec.ComponentHashes); break;
                case SyncRecordKind.IconSet: w.IconSet(rec.Owner, rec.Icons); break;
                case SyncRecordKind.IconControl: w.IconControl(rec.IconMode, rec.Count, rec.Count2, rec.Hash); break;
                case SyncRecordKind.LineRows: w.LineRows(rec.Payload, 0, rec.Payload.Length); break;
                case SyncRecordKind.MoverDespawn: w.MoverDespawn(rec.NetId); break;
                case SyncRecordKind.MoverAgents: w.MoverAgents(rec.NetId, rec.AgentLinks); break;
                case SyncRecordKind.EnabledBits: w.EnabledBits(rec.NetId, rec.EnabledBits); break;
                case SyncRecordKind.ModSettings: w.ModSettings(rec.Settings); break;
                case SyncRecordKind.PrefabSource: w.PrefabSource(rec.NetId, rec.Source); break;
                case SyncRecordKind.Epoch: w.Epoch(rec.EpochNumber); break;
                case SyncRecordKind.ProposalHeader: w.ProposalHeader(rec.Seq, rec.BaseEpoch, rec.ProposalFlags); break;
                case SyncRecordKind.ProposalResult: w.ProposalResult(rec.Seq, rec.Reason); break;
                default: Assert.Fail("unexpected record kind " + rec.Kind); break;
            }
        }
    }
}
