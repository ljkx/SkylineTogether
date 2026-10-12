using System;
using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    public sealed class GlobalStateTests
    {
        [Test]
        public void GlobalRecordRoundTripsBesideOtherRecords()
        {
            var w = new SyncBatch.Writer();
            w.Despawn(4);
            byte[] payload = { 9, 8, 7, 6, 5 };
            w.Global(GlobalChannel.Tax, payload, 1, 3);
            w.Global(0xFFFFFFFFu, Array.Empty<byte>(), 0, 0);
            w.Clock(77);
            ArraySegment<byte> bytes = w.Finish();
            var copy = new byte[bytes.Count + 3];
            Buffer.BlockCopy(bytes.Array, 0, copy, 3, bytes.Count);

            List<SyncRecord> recs = SyncBatch.Read(copy, 3, bytes.Count);
            Assert.Equal(4, recs.Count);
            Assert.Equal(SyncRecordKind.Global, recs[1].Kind);
            Assert.Equal(GlobalChannel.Tax, recs[1].Channel);
            Assert.SequenceEqual(new byte[] { 8, 7, 6 }, recs[1].Payload);
            Assert.Equal(0xFFFFFFFFu, recs[2].Channel);
            Assert.Equal(0, recs[2].Payload.Length);
            Assert.Equal(77u, recs[3].Frame);
        }

        [Test]
        public void GlobalRecordWithOverlongPayloadIsADecodeException()
        {
            var w = new SyncBatch.Writer();
            w.Global(1, new byte[] { 1, 2, 3, 4 }, 0, 4);
            ArraySegment<byte> b = w.Finish();
            byte[] bad = new byte[b.Count];
            Buffer.BlockCopy(b.Array, 0, bad, 0, b.Count);
            bad[10] = 100; // declared payload length (after version, count, kind, channel)
            Assert.Throws<DecodeException>(() => SyncBatch.Read(bad, 0, bad.Length));
            Assert.Throws<DecodeException>(() => SyncBatch.Read(bad, 0, bad.Length - 2));
        }

        [Test]
        public void GlobalBatchIsRecognisedByItsFirstRecordKind()
        {
            // GlobalStateSync.Apply relies on this layout: [version][u32 record count][first record kind]...
            var w = new SyncBatch.Writer();
            w.Global(GlobalChannel.Name, new byte[] { 1, 0 }, 0, 2);
            ArraySegment<byte> b = w.Finish();
            Assert.Equal((byte)SyncRecordKind.Global, b.Array[b.Offset + 5]);
            var entities = new SyncBatch.Writer();
            entities.Despawn(3);
            ArraySegment<byte> e = entities.Finish();
            Assert.NotEqual((byte)SyncRecordKind.Global, e.Array[e.Offset + 5]);
        }

        [Test]
        public void ChannelIdsAreStableAndDistinct()
        {
            Assert.Equal(GlobalChannel.Id("Tax"), GlobalChannel.Tax);
            Assert.Equal(2166136261u, GlobalChannel.Id(""));
            var ids = new HashSet<uint> { GlobalChannel.Tax, GlobalChannel.Budget, GlobalChannel.Fee, GlobalChannel.Loan, GlobalChannel.Policy, GlobalChannel.Name, GlobalChannel.CityName, GlobalChannel.Id("Demand.Residential"), GlobalChannel.Id("Demand.Commercial"), GlobalChannel.Id("Demand.Industrial"), GlobalChannel.Id("Count.Household"), GlobalChannel.Id("Count.Workplaces") };
            Assert.Equal(12, ids.Count);
        }

        [Test]
        public void IntPayloadRoundTrips()
        {
            var entries = new List<KeyValuePair<uint, int>> { new KeyValuePair<uint, int>(0, 10), new KeyValuePair<uint, int>(91, -7), new KeyValuePair<uint, int>(uint.MaxValue, int.MinValue) };
            byte[] data = GlobalPayload.WriteInts(GlobalPayloadMode.Delta, entries);
            var back = new List<KeyValuePair<uint, int>>();
            Assert.Equal(GlobalPayloadMode.Delta, GlobalPayload.ReadInts(data, 0, data.Length, back));
            Assert.SequenceEqual(entries, back);
        }

        [Test]
        public void FloatStringAndPolicyPayloadsRoundTrip()
        {
            var fees = new List<KeyValuePair<uint, float>> { new KeyValuePair<uint, float>(3, 0.25f), new KeyValuePair<uint, float>(7, 300f) };
            byte[] f = GlobalPayload.WriteFloats(GlobalPayloadMode.Full, fees);
            var feesBack = new List<KeyValuePair<uint, float>>();
            Assert.Equal(GlobalPayloadMode.Full, GlobalPayload.ReadFloats(f, 0, f.Length, feesBack));
            Assert.SequenceEqual(fees, feesBack);

            var names = new List<KeyValuePair<uint, string>> { new KeyValuePair<uint, string>(12, "Main Street"), new KeyValuePair<uint, string>(13, ""), new KeyValuePair<uint, string>(14, "Ünïcødé ✓") };
            byte[] n = GlobalPayload.WriteStrings(GlobalPayloadMode.Delta, names);
            var namesBack = new List<KeyValuePair<uint, string>>();
            GlobalPayload.ReadStrings(n, 0, n.Length, namesBack);
            Assert.SequenceEqual(names, namesBack);

            var policies = new List<PolicyChange> { new PolicyChange { Target = 5, Policy = 900, Flags = 1, Adjustment = 0.5f }, new PolicyChange { Target = 6, Policy = 901 } };
            byte[] p = GlobalPayload.WritePolicies(GlobalPayloadMode.Delta, policies);
            var policiesBack = new List<PolicyChange>();
            GlobalPayload.ReadPolicies(p, 0, p.Length, policiesBack);
            Assert.SequenceEqual(policies, policiesBack);
        }

        [Test]
        public void OverlongStringsAreClippedToTheLimit()
        {
            string longName = new string('x', 5000);
            byte[] n = GlobalPayload.WriteStrings(GlobalPayloadMode.Full, new List<KeyValuePair<uint, string>> { new KeyValuePair<uint, string>(1, longName) });
            var back = new List<KeyValuePair<uint, string>>();
            GlobalPayload.ReadStrings(n, 0, n.Length, back);
            Assert.True(back[0].Value.Length > 0 && back[0].Value.Length <= GlobalPayload.MaxStringBytes);
            Assert.True(longName.StartsWith(back[0].Value, StringComparison.Ordinal));
        }

        [Test]
        public void MalformedPayloadsAreDecodeExceptions()
        {
            var sink = new List<KeyValuePair<uint, int>>();
            Assert.Throws<DecodeException>(() => GlobalPayload.ReadInts(new byte[] { 9, 0 }, 0, 2, sink));         // bad mode
            Assert.Throws<DecodeException>(() => GlobalPayload.ReadInts(new byte[] { 1, 50, 1 }, 0, 3, sink));     // count beyond data
            Assert.Throws<DecodeException>(() => GlobalPayload.ReadInts(new byte[] { 1, 0, 0 }, 0, 3, sink));      // trailing byte
            Assert.Throws<DecodeException>(() => GlobalPayload.ReadInts(new byte[] { 1, 1, 0 }, 0, 3, sink));      // truncated entry
            Assert.Throws<DecodeException>(() => GlobalPayload.ReadInts(Array.Empty<byte>(), 0, 0, sink));         // empty
        }

        [Test]
        public void DiffReportsChangedNewAndRemovedKeys()
        {
            var baseline = new Dictionary<uint, float> { { 1, 1f }, { 2, 2f }, { 3, 3f } };
            var current = new Dictionary<uint, float> { { 1, 1f }, { 2, 2.5f }, { 4, 4f } };
            var changed = new List<KeyValuePair<uint, float>>();
            var removed = new List<uint>();
            Assert.True(KeyedDiff.Diff(baseline, current, changed, removed));
            changed.Sort((a, b) => a.Key.CompareTo(b.Key));
            Assert.SequenceEqual(new[] { new KeyValuePair<uint, float>(2, 2.5f), new KeyValuePair<uint, float>(4, 4f) }, changed);
            Assert.SequenceEqual(new uint[] { 3 }, removed);

            changed.Clear();
            Assert.False(KeyedDiff.Diff(current, new Dictionary<uint, float>(current), changed, null));
            Assert.Equal(0, changed.Count);
        }

        [Test]
        public void HoldDefersTheNewestPayloadUntilItExpires()
        {
            var hold = new ChannelHold(1.0);
            var released = new List<KeyValuePair<uint, byte[]>>();
            Assert.False(hold.TryDefer(7, new byte[] { 1 }, 0.0), "not held: apply immediately");

            hold.Hold(7, 10.0);
            Assert.True(hold.IsHeld(7, 10.5));
            Assert.False(hold.IsHeld(8, 10.5));
            Assert.True(hold.TryDefer(7, new byte[] { 1 }, 10.2));
            Assert.True(hold.TryDefer(7, new byte[] { 2 }, 10.4));
            hold.TakeReleased(10.9, released);
            Assert.Equal(0, released.Count);

            hold.Hold(7, 10.8); // another local edit extends the hold
            hold.TakeReleased(11.5, released);
            Assert.Equal(0, released.Count);
            hold.TakeReleased(11.8, released);
            Assert.Equal(1, released.Count);
            Assert.Equal(7u, released[0].Key);
            Assert.SequenceEqual(new byte[] { 2 }, released[0].Value);
            Assert.False(hold.IsHeld(7, 11.8));

            released.Clear();
            hold.TakeReleased(20.0, released);
            Assert.Equal(0, released.Count, "a released payload is handed out once");
        }

        [Test]
        public void HoldWithoutDeferredPayloadReleasesNothing()
        {
            var hold = new ChannelHold(0.5);
            hold.Hold(1, 0);
            var released = new List<KeyValuePair<uint, byte[]>>();
            hold.TakeReleased(1, released);
            Assert.Equal(0, released.Count);
            Assert.False(hold.TryDefer(1, new byte[] { 3 }, 1), "expired holds no longer defer");
        }

        [Test]
        public void SendGateSendsChangesDirtyChannelsAndKeyframes()
        {
            var gate = new ChannelSendGate(30.0);
            Assert.True(gate.ShouldSend(1, 100, 0.0), "first state always goes out");
            Assert.False(gate.ShouldSend(1, 100, 1.0), "unchanged");
            Assert.True(gate.ShouldSend(1, 101, 2.0), "changed");
            Assert.False(gate.ShouldSend(1, 101, 3.0));

            gate.MarkDirty(1);
            Assert.True(gate.AnyDirty);
            Assert.True(gate.ShouldSend(1, 101, 4.0), "dirty (a proposal was applied)");
            Assert.False(gate.ShouldSend(1, 101, 5.0), "dirty is served once");

            Assert.True(gate.ShouldSend(1, 101, 34.5), "keyframe");
            Assert.False(gate.ShouldSend(1, 101, 35.0));

            Assert.True(gate.ShouldSend(2, 5, 35.0));
            gate.MarkAllDirty();
            Assert.True(gate.ShouldSend(1, 101, 36.0));
            Assert.True(gate.ShouldSend(2, 5, 36.0));
            gate.EndPass();
            Assert.False(gate.AnyDirty);
            Assert.False(gate.ShouldSend(2, 5, 37.0));

            gate.Clear();
            Assert.True(gate.ShouldSend(2, 5, 38.0), "cleared: everything is new again");
        }
    }
}
