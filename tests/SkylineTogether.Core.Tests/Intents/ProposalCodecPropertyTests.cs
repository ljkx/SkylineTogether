using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Intents;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Intents
{
    /// <summary>
    /// Seeded properties of the proposal wire format (<see cref="ProposalFormat"/>), the largest decoder that runs on
    /// client input: random proposals of every item kind encode canonically (decode, re-encode: the same bytes), split
    /// into parts that respect the part size and reassemble to the same items, well-formed proposals above a cap come
    /// back as flagged shells instead of exceptions, and mutated parts obey the decoder contract.
    /// </summary>
    public sealed class ProposalCodecPropertyTests
    {
        private static PrefabRank Prefab(Gen g) => g.OneIn(3) ? PrefabRank.None : new PrefabRank(g.Pick(0, 1, 127, 128, 70_000, int.MaxValue - 1));

        private static NetSlot Slot(Gen g, bool allowNone = false)
        {
            uint v = g.Pick(1u, 2u, 127u, 128u, 1024u, NetSlot.MaxValue, (uint)g.Int(1, 1 << 20));
            return allowNone && g.OneIn(4) ? new NetSlot(0) : new NetSlot(v);
        }

        private static List<ComponentPayload> Components(Gen g, int max)
        {
            return g.List(max, x =>
            {
                var type = new TypeRank((ushort)x.Int(0, 65536));
                switch (x.Int(4))
                {
                    case 0: return ComponentPayload.Value(type, x.Bytes(48));
                    case 1: return ComponentPayload.WithEnabledBit(type, x.Bytes(16), x.Bool());
                    case 2: return ComponentPayload.Presence(type);
                    default: return ComponentPayload.Removal(type);
                }
            });
        }

        private static Proposal RandomProposal(Gen g, int scale = 6)
        {
            var p = new Proposal
            {
                Seq = Math.Max(1u, g.UIntEdgy()),
                Kind = g.Enum<ProposalKind>(),
                BaseEpoch = new EpochId(g.UIntEdgy()),
                Tool = g.OneIn(3) ? null : new ToolHeader
                {
                    ToolKind = (ushort)g.Int(0, 65536),
                    ToolTypeName = g.OneIn(4) ? null : g.String(40),
                    Prefab = Prefab(g),
                    Mode = (byte)g.Int(256),
                    CostSum = g.Long(),
                }
            };
            p.Creates = g.List(scale, x => new ProposalCreate
            {
                Local = new LocalId(Math.Max(1u, x.UIntEdgy())),
                Family = x.Enum<Family>(),
                Prefab = Prefab(x),
                TypeSet = new TypeSetId(x.Int(0, int.MaxValue)),
                EnabledBits = x.ULong(),
                Components = Components(x, 5),
            });
            p.Updates = g.List(scale, x => new ProposalUpdate { Slot = Slot(x), BaseRevision = (ushort)x.Int(0, 65536), Components = Components(x, 5) });
            p.Deletes = g.List(scale, x => new ProposalDelete { Slot = Slot(x), BaseRevision = (ushort)x.Int(0, 65536) });
            p.Touches = g.List(scale, x => new TouchRecord { Slot = Slot(x), Flags = (TouchFlags)x.Int(0, 16) });
            p.Zones = g.List(scale / 2, x =>
            {
                var ranks = new ushort[x.Size(ProposalFormat.MaxZoneCellsPerEdit)];
                ushort v = (ushort)x.Int(0, 65536);
                for (int i = 0; i < ranks.Length; i++)
                {
                    if (x.OneIn(16)) v = (ushort)x.Int(0, 65536);
                    ranks[i] = v;
                }
                var key = new DerivedKey((DerivedKeyKind)x.Int(1, 8), Slot(x, true), Prefab(x), (int)x.UInt(), (int)x.UInt(), (int)x.UInt(), (int)x.UInt(), x.ULong(), x.ULong(), x.ULong());
                return new ZoneEdit { Block = key, BaseRevision = (ushort)x.Int(0, 65536), ZoneRanks = x.OneIn(8) ? null : ranks };
            });
            p.Stamps = g.List(scale / 2, x => new TerrainStamp
            {
                TerraformingType = (byte)x.Int(0, TerrainStamp.MaxTerraformingType + 1),
                PosX = x.Float(true), PosY = x.Float(), PosZ = x.Float(), TargetX = x.Float(), TargetY = x.Float(), TargetZ = x.Float(),
                StartX = x.Float(), StartY = x.Float(), StartZ = x.Float(), Angle = x.Float(), Size = x.Float(), Strength = x.Float(true), Opacity = x.Float(),
                ToolPrefab = Prefab(x), BrushPrefab = Prefab(x),
                AreaMinX = x.Float(), AreaMinZ = x.Float(), AreaMaxX = x.Float(), AreaMaxZ = x.Float(), ClampedDeltaTime = x.Float(),
            });
            p.Intents = g.List(scale, x => new SemanticIntent
            {
                Kind = x.Pick(IntentKind.SetPolicy, IntentKind.Rename, IntentKind.SetCityName, IntentKind.ReplaceObjectPrefab, IntentKind.SetTaxRates, IntentKind.SetMeshColor),
                Target = Slot(x, true),
                BaseRevision = (ushort)x.Int(0, 65536),
                Prefab = Prefab(x),
                IntValue = (int)x.UInt(),
                FloatValue = x.Float(true),
                BoolValue = x.Bool(),
                Text = x.OneIn(3) ? null : x.String(ProposalFormat.MaxTextChars + 10, hostile: true),
                IntArray = x.OneIn(2) ? null : x.List(40, y => (int)y.UInt()).ToArray(),
                Blob = x.OneIn(2) ? null : x.Bytes(200),
            });
            return p;
        }

        private static byte[] Encode(Proposal p)
        {
            var w = new ByteWriter();
            p.Write(w);
            return w.ToArray();
        }

        [Test(TimeoutSeconds = 120)]
        public void RandomProposalsEncodeCanonically()
        {
            Prop.ForAll("proposal round trip", 1700, 250, g =>
            {
                Proposal p = RandomProposal(g);
                byte[] bytes = Encode(p);
                Proposal back = Proposal.Decode(bytes, 0, bytes.Length);
                Assert.Null(back.LimitViolation);
                Assert.Equal(p.Seq, back.Seq);
                Assert.Equal(p.Kind, back.Kind);
                Assert.Equal(p.EntityCount, back.EntityCount);
                Assert.Equal(p.Touches.Count, back.Touches.Count);
                Assert.Equal(p.Stamps.Count, back.Stamps.Count);
                Assert.Equal(p.Intents.Count, back.Intents.Count);
                bool textChanged = false;
                for (int i = 0; i < p.Intents.Count; i++)
                {
                    string sent = p.Intents[i].Text, text = back.Intents[i].Text;
                    Assert.Equal(sent == null, text == null, "null text stays null");
                    if (text == null) continue;
                    // free text is cut on the sender (never inside a surrogate pair) and sanitized by the receiver
                    Assert.True(text.Length <= ProposalFormat.MaxTextChars, "intent text is cut to the limit");
                    Assert.Equal(ProtocolText.Sanitize(text), text, "decoded text is sanitized");
                    string expectedText = ProtocolText.Sanitize(System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(ProtocolText.Truncate(sent, ProposalFormat.MaxTextChars))));
                    Assert.Equal(expectedText, text, "decoded text");
                    if (text != sent) textChanged = true;
                }
                // the decoded proposal re-encodes to the very same bytes (no field is lost, altered or ambiguous), up to
                // the text the receiver sanitized; from then on the encoding is a fixed point
                byte[] again = Encode(back);
                if (!textChanged) Assert.SequenceEqual(bytes, again, "re-encoded proposal");
                byte[] third = Encode(Proposal.Decode(again, 0, again.Length));
                Assert.SequenceEqual(again, third, "encoding a decoded proposal is a fixed point");
                var pairs = new List<(NetSlot, TypeRank)>();
                int covered = back.CollectCoveredPairs(pairs);
                int expected = 0;
                foreach (ProposalUpdate u in p.Updates) expected += u.Components.Count;
                Assert.Equal(expected, covered);
                Assert.True(back.EstimateEncodedBytes() >= bytes.Length, "the estimate is an upper bound (" + back.EstimateEncodedBytes() + " < " + bytes.Length + ")");
            });
        }

        [Test(TimeoutSeconds = 120)]
        public void PartsRespectTheirSizeAndReassembleToTheSameItems()
        {
            Prop.ForAll("proposal parts", 1710, 120, g =>
            {
                int partBytes = g.Pick(512, 1024, 4096);
                var limits = new ProposalLimits(partBytes, 1 << 24, 100_000, 100_000, 4096, 1024, 1 << 20, 1 << 20);
                Proposal p = RandomProposal(g, 30);
                var parts = new List<byte[]>();
                try
                {
                    p.WriteParts(new ByteWriter(), (b, o, c) =>
                    {
                        var part = new byte[c];
                        Buffer.BlockCopy(b, o, part, 0, c);
                        parts.Add(part);
                    }, limits);
                }
                catch (ArgumentException e) when (e.Message.Contains("does not fit into a part"))
                {
                    Assert.Equal(0, parts.Count, "nothing is emitted when an item cannot be split");
                    return;
                }
                Assert.True(parts.Count >= 1);
                var state = new ProposalDecodeState(limits, new Proposal());
                for (int i = 0; i < parts.Count; i++)
                {
                    Assert.True(parts[i].Length <= partBytes, "part " + i + " has " + parts[i].Length + " bytes");
                    var r = new ByteReader(parts[i], 0, parts[i].Length);
                    ProposalCodec.ReadPartHeader(r, out byte flags, out uint seq, out int index);
                    Assert.Equal(p.Seq, seq);
                    Assert.Equal(i, index);
                    Assert.Equal(i < parts.Count - 1, (flags & ProposalCodec.FlagMoreParts) != 0, "the more-parts flag marks all but the last part");
                    if (i == 0)
                    {
                        state.Target.Seq = seq;
                        ProposalCodec.ReadProposalHeader(r, state.Target);
                    }
                    state.Bytes += parts[i].Length;
                    ProposalCodec.ReadItems(r, state);
                }
                Assert.Null(state.Violation);
                if (parts.Count == 1) Assert.SequenceEqual(Encode(p), parts[0], "a single part is byte-identical to Write");
                // the reassembled items, written as one part with roomy limits, equal the original written the same way
                var roomy = new ProposalLimits(1 << 24, 1 << 25, 100_000, 100_000, 4096, 1024, 1 << 20, 1 << 20);
                var a = new ByteWriter();
                var b2 = new ByteWriter();
                byte[] single = Encode(p);
                Proposal.Decode(single, 0, single.Length).Write(a, roomy);   // (decoded once: texts sanitized like the parts')
                state.Target.Seq = p.Seq;
                state.Target.Write(b2, roomy);
                Assert.SequenceEqual(a.ToArray(), b2.ToArray(), "reassembled proposal");
            });
        }

        [Test]
        public void AWellFormedProposalAboveACapIsAFlaggedShell()
        {
            var roomy = new ProposalLimits(1 << 20, 1 << 21, 100_000, 100_000, 4096, 1024, 1 << 20, 1 << 20);
            var p = new Proposal { Seq = 7, Kind = ProposalKind.ToolChangeSet, Tool = new ToolHeader { ToolKind = 3, ToolTypeName = "NetTool" } };
            for (int i = 0; i < 20; i++) p.Deletes.Add(new ProposalDelete { Slot = new NetSlot((uint)(i + 1)), BaseRevision = 1 });
            for (int i = 0; i < 9; i++) p.Touches.Add(new TouchRecord { Slot = new NetSlot((uint)(i + 1)), Flags = TouchFlags.Updated });
            var w = new ByteWriter();
            p.Write(w, roomy);
            byte[] bytes = w.ToArray();

            var tight = new ProposalLimits(1 << 20, 1 << 21, 10, 100_000, 4096, 1024, 1 << 20, 1 << 20);
            Proposal shell = Proposal.Read(new ByteReader(bytes, 0, bytes.Length), tight);
            Assert.NotNull(shell.LimitViolation, "flagged");
            Assert.Contains("10 entities", shell.LimitViolation);
            Assert.Equal(0, shell.EntityCount, "a shell carries no items");
            Assert.Equal(7u, shell.Seq);
            Assert.Equal("NetTool", shell.Tool.ToolTypeName, "the header survives for the RESULT record");

            var fewTouches = new ProposalLimits(1 << 20, 1 << 21, 100_000, 5, 4096, 1024, 1 << 20, 1 << 20);
            Assert.Contains("touches", Proposal.Read(new ByteReader(bytes, 0, bytes.Length), fewTouches).LimitViolation);
            Assert.True(p.IsWithinLimits(out string none), "the default limits hold this proposal");
            Assert.Null(none);
            Assert.Throws<ArgumentException>(() => p.Write(new ByteWriter(), tight), "the sender refuses to encode above a cap");
            Assert.Throws<ArgumentException>(() => new Proposal().Write(new ByteWriter()), "seq 0 is not encodable");
        }

        [Test(TimeoutSeconds = 120)]
        public void MutatedProposalsObeyTheDecoderContract()
        {
            var seeds = new List<byte[]>();
            var gen = new Gen(1720);
            for (int i = 0; i < 12; i++) seeds.Add(Encode(RandomProposal(gen, 4)));
            int rejected = DecoderContract.Fuzz("proposal", seeds, 3000, 1721, b => Proposal.Decode(b, 0, b.Length));
            Assert.True(rejected > 300, "mutations are rejected now and then (" + rejected + ")");
        }
    }
}
