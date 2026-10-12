using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>w4/flow: flow keys, significance, the per-peer channel and the wire form of the flow-status channel (ARCH §4.11.5).</summary>
    public sealed class FlowStatusTests
    {
        private static FlowKey Key(uint a, FlowNodeRole ra, uint b, FlowNodeRole rb) => new FlowKey(new FlowNodeKey(a, ra), new FlowNodeKey(b, rb));

        private static FlowKey NetKey(uint a, uint b) => Key(a, FlowNodeRole.Net, b, FlowNodeRole.Net);

        [Test]
        public void KeysNeedABoundOwnerExceptForTheSuperNodes()
        {
            Assert.True(NetKey(1, 2).IsValid);
            Assert.False(NetKey(0, 2).IsValid, "unbound owner");
            Assert.False(Key(1, FlowNodeRole.None, 2, FlowNodeRole.Net).IsValid);
            Assert.True(Key(5, FlowNodeRole.Consumer, 0, FlowNodeRole.Sink).IsValid);
            Assert.True(Key(0, FlowNodeRole.Source, 5, FlowNodeRole.Producer).IsValid);
            Assert.False(Key(3, FlowNodeRole.Sink, 5, FlowNodeRole.Net).IsValid, "a super node has no owner");
            Assert.False(Key(3, (FlowNodeRole)12, 5, FlowNodeRole.Net).IsValid);
        }

        [Test]
        public void DirectionAndRoleArePartOfTheKey()
        {
            Assert.NotEqual(NetKey(1, 2), NetKey(2, 1));
            Assert.NotEqual(Key(1, FlowNodeRole.Valve, 2, FlowNodeRole.Net), NetKey(1, 2));
            Assert.Equal(NetKey(7, 9), NetKey(7, 9));
            Assert.Equal(NetKey(7, 9).GetHashCode(), NetKey(7, 9).GetHashCode());
        }

        [Test]
        public void SmallFlowFluctuationsAreNotSignificant()
        {
            var a = new FlowValue { Flow = 6400, Capacity = 10000, Flags = 1 };
            var b = a;
            b.Flow = 6450; // < 1/64
            Assert.False(FlowValue.Significant(a, b));
            b.Flow = 6600;
            Assert.True(FlowValue.Significant(a, b));
            b = a;
            b.Flow = 0;
            Assert.True(FlowValue.Significant(a, b), "a flow that stops");
            b.Flow = -6400;
            Assert.True(FlowValue.Significant(a, b), "a flow that reverses");
            b = a;
            b.Flags = 1 | 4;
            Assert.True(FlowValue.Significant(a, b), "bottleneck flag");
            b = a;
            b.Capacity = 9999;
            Assert.True(FlowValue.Significant(a, b), "capacity");
            var small = new FlowValue { Flow = 3 };
            Assert.True(FlowValue.Significant(small, new FlowValue { Flow = 5 }), "tiny flows move by at least one unit");
            Assert.False(FlowValue.Significant(small, new FlowValue { Flow = 4 }));
        }

        [Test]
        public void WaterValuesCompareSewageAndPollution()
        {
            var a = new FlowValue { Flow = 100, Capacity = 500, SewageFlow = 200, SewageCapacity = 500, Pollution = 0.5f };
            var b = a;
            b.SewageFlow = 260;
            Assert.True(FlowValue.Significant(a, b));
            b = a;
            b.Pollution = 0.501f;
            Assert.False(FlowValue.Significant(a, b));
            b.Pollution = 0.6f;
            Assert.True(FlowValue.Significant(a, b));
            b.Pollution = 0f;
            Assert.True(FlowValue.Significant(a, b), "pollution cleared");
        }

        [Test]
        public void DuplicateKeysAreAmbiguousAndDropped()
        {
            var t = new FlowStatusTable();
            Assert.True(t.Add(NetKey(1, 2), new FlowValue { Flow = 1 }));
            Assert.False(t.Add(NetKey(1, 2), new FlowValue { Flow = 2 }));
            Assert.False(t.Add(NetKey(1, 2), new FlowValue { Flow = 3 }), "stays ambiguous");
            Assert.False(t.TryGet(NetKey(1, 2), out _));
            Assert.True(t.IsAmbiguous(NetKey(1, 2)));
            Assert.False(t.Add(NetKey(0, 2), default), "invalid keys are dropped");
            Assert.Equal(0, t.Count);
            t.Clear();
            Assert.True(t.Add(NetKey(1, 2), default));
        }

        [Test]
        public void ActivationIsAKeyframeAndInViewEdgesComeFirst()
        {
            var t = new FlowStatusTable();
            for (uint i = 1; i <= 6; i++) t.Add(NetKey(i, i + 100), new FlowValue { Flow = (int)i * 1000, Capacity = 10000 });
            var ch = new FlowStatusChannel();
            var pending = new List<FlowKey>();
            ch.Plan(t, null, pending);
            Assert.Equal(0, pending.Count, "inactive channels send nothing");
            Assert.True(ch.SetActive(true));
            Assert.False(ch.SetActive(true));
            Assert.True(ch.KeyframePending);
            ch.Plan(t, k => k.StartOwner == 4 || k.StartOwner == 5, pending);
            Assert.Equal(6, pending.Count);
            Assert.True(pending[0].StartOwner == 4 || pending[0].StartOwner == 5);
            Assert.True(pending[1].StartOwner == 4 || pending[1].StartOwner == 5);
            // only part of it fits the budget
            for (int i = 0; i < 3; i++)
            {
                t.TryGet(pending[i], out FlowValue v);
                ch.MarkSent(pending[i], v);
            }
            ch.Plan(t, null, pending);
            Assert.Equal(3, pending.Count, "the rest follows in the next pass");
            foreach (FlowKey k in pending)
            {
                t.TryGet(k, out FlowValue v);
                ch.MarkSent(k, v);
            }
            ch.Plan(t, null, pending);
            Assert.Equal(0, pending.Count);
            Assert.False(ch.KeyframePending);
        }

        [Test]
        public void DeltasCarryOnlySignificantChangesAndForgetGoneEdges()
        {
            var t = new FlowStatusTable();
            t.Add(NetKey(1, 2), new FlowValue { Flow = 5000, Capacity = 10000 });
            t.Add(NetKey(2, 3), new FlowValue { Flow = 5000, Capacity = 10000 });
            var ch = new FlowStatusChannel();
            ch.SetActive(true);
            var pending = new List<FlowKey>();
            ch.Plan(t, null, pending);
            foreach (FlowKey k in pending)
            {
                t.TryGet(k, out FlowValue v);
                ch.MarkSent(k, v);
            }
            var next = new FlowStatusTable();
            next.Add(NetKey(1, 2), new FlowValue { Flow = 5010, Capacity = 10000 }); // noise
            next.Add(NetKey(2, 3), new FlowValue { Flow = 9000, Capacity = 10000, Flags = 4 }); // bottleneck
            ch.Plan(next, null, pending);
            Assert.Equal(1, pending.Count);
            Assert.Equal(NetKey(2, 3), pending[0]);
            var third = new FlowStatusTable();
            third.Add(NetKey(2, 3), new FlowValue { Flow = 5000, Capacity = 10000 });
            ch.Plan(third, null, pending);
            Assert.False(ch.TryGetSent(NetKey(1, 2), out _), "a removed edge is forgotten");
            Assert.Equal(1, ch.SentCount);
            // deactivation forgets everything: the next activation is a keyframe again
            ch.SetActive(false);
            Assert.Equal(0, ch.SentCount);
            Assert.True(ch.SetActive(true));
            ch.Plan(third, null, pending);
            Assert.Equal(1, pending.Count);
        }

        [Test]
        public void MessagesRoundTrip()
        {
            var w = new ByteWriter();
            int at = FlowStatusMessage.Begin(w, FlowNetwork.Water, FlowStatusMessage.FlagKeyframe);
            var water = new FlowValue { Flow = -12, Capacity = 1073741823, SewageFlow = 40, SewageCapacity = 600, Pollution = 0.25f, Flags = 5 };
            FlowStatusMessage.WriteEntry(w, FlowNetwork.Water, Key(0, FlowNodeRole.Source, 300000, FlowNodeRole.Producer), water);
            FlowStatusMessage.WriteEntry(w, FlowNetwork.Water, NetKey(16_000_000, 3), default);
            FlowStatusMessage.End(w, at, 2);
            var list = new List<KeyValuePair<FlowKey, FlowValue>>();
            FlowNetwork network = FlowStatusMessage.Read(w.Buffer, 0, w.Length, list, out byte flags);
            Assert.Equal(FlowNetwork.Water, network);
            Assert.Equal(FlowStatusMessage.FlagKeyframe, flags);
            Assert.Equal(2, list.Count);
            Assert.Equal(Key(0, FlowNodeRole.Source, 300000, FlowNodeRole.Producer), list[0].Key);
            Assert.Equal(water, list[0].Value);
            Assert.Equal(NetKey(16_000_000, 3), list[1].Key);

            w.Reset();
            at = FlowStatusMessage.Begin(w, FlowNetwork.Electricity, 0);
            var power = new FlowValue { Flow = 77, Capacity = 900, Flags = 3, SewageFlow = 9, Pollution = 1f };
            FlowStatusMessage.WriteEntry(w, FlowNetwork.Electricity, Key(4, FlowNodeRole.Consumer, 0, FlowNodeRole.Sink), power);
            FlowStatusMessage.End(w, at, 1);
            Assert.Equal(FlowNetwork.Electricity, FlowStatusMessage.Read(w.Buffer, 0, w.Length, list, out _));
            Assert.Equal(new FlowValue { Flow = 77, Capacity = 900, Flags = 3 }, list[0].Value, "electricity carries no water fields");
            Assert.True(w.Length - 7 <= FlowStatusMessage.MaxEntryBytes);
        }

        [Test]
        public void MalformedMessagesThrow()
        {
            var list = new List<KeyValuePair<FlowKey, FlowValue>>();
            Assert.Throws<DecodeException>(() => FlowStatusMessage.Read(new byte[] { 2, 1, 0, 0, 0, 0, 0 }, 0, 7, list, out _));
            Assert.Throws<DecodeException>(() => FlowStatusMessage.Read(new byte[] { 1, 3, 0, 0, 0, 0, 0 }, 0, 7, list, out _));
            Assert.Throws<DecodeException>(() => FlowStatusMessage.Read(new byte[] { 1, 1, 0, 9, 0, 0, 0 }, 0, 7, list, out _));
            // an invalid key (owner 0 with role Net)
            Assert.Throws<DecodeException>(() => FlowStatusMessage.Read(new byte[] { 1, 1, 0, 1, 0, 0, 0, 0, 2, 0x11, 0, 0, 0 }, 0, 13, list, out _));
            // trailing bytes
            var w = new ByteWriter();
            int at = FlowStatusMessage.Begin(w, FlowNetwork.Electricity, 0);
            FlowStatusMessage.WriteEntry(w, FlowNetwork.Electricity, NetKey(1, 2), default);
            FlowStatusMessage.End(w, at, 1);
            w.WriteByte(0);
            Assert.Throws<DecodeException>(() => FlowStatusMessage.Read(w.Buffer, 0, w.Length, list, out _));
            Assert.Throws<ArgumentException>(() => FlowStatusMessage.WriteEntry(new ByteWriter(), FlowNetwork.Electricity, NetKey(0, 2), default));
        }
    }
}
