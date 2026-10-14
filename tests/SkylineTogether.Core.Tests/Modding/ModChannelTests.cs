using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Modding
{
    /// <summary>The mod channel (docs/MOD-INTEGRATION.md): framing, size limits, host relay routing and the send budget.</summary>
    public sealed class ModChannelTests
    {
        private static byte[] Encode(ModChannelMessage m)
        {
            var w = new ByteWriter();
            ModChannelMessage.Write(w, m);
            return w.ToArray();
        }

        private static readonly PeerId P1 = new PeerId(1), P2 = new PeerId(2), P3 = new PeerId(3);

        [Test]
        public void MessagesRoundTrip()
        {
            var m = new ModChannelMessage { Channel = "RoadBuilder", From = P2, Target = ModChannelTarget.Player, TargetPeer = P3, Payload = new byte[] { 1, 2, 3, 0, 255 } };
            byte[] bytes = Encode(m);
            ModChannelMessage back = ModChannelMessage.Decode(bytes, 0, bytes.Length);
            Assert.Equal("RoadBuilder", back.Channel);
            Assert.Equal(P2, back.From);
            Assert.Equal(ModChannelTarget.Player, back.Target);
            Assert.Equal(P3, back.TargetPeer);
            Assert.SequenceEqual(m.Payload, back.Payload);

            m.Payload = null; // an empty message is valid (a signal)
            bytes = Encode(m);
            Assert.Equal(0, ModChannelMessage.Decode(bytes, 0, bytes.Length).Payload.Length);
            Assert.Equal(ModChannelMessage.Version, bytes[0]);
        }

        [Test]
        public void LimitsHoldBothWays()
        {
            var full = new ModChannelMessage { Channel = new string('c', ModChannelMessage.MaxChannelBytes), Payload = new byte[ModChannelMessage.MaxPayloadBytes] };
            byte[] bytes = Encode(full);
            Assert.True(bytes.Length <= ModChannelMessage.MaxEncodedBytes);
            Assert.True(Limits.MaxBodyBytes(MessageKind.ModChannel) >= bytes.Length);
            Assert.True(Limits.IsKnownKind(MessageKind.ModChannel));

            Assert.Throws<ArgumentException>(() => Encode(new ModChannelMessage { Channel = "x", Payload = new byte[ModChannelMessage.MaxPayloadBytes + 1] }));
            Assert.Throws<ArgumentException>(() => Encode(new ModChannelMessage { Channel = new string('c', ModChannelMessage.MaxChannelBytes + 1) }));
            Assert.Throws<ArgumentException>(() => Encode(new ModChannelMessage { Channel = "" }));
            Assert.Throws<ArgumentException>(() => Encode(new ModChannelMessage { Channel = "x", Target = (ModChannelTarget)9 }));
            Assert.Throws<ArgumentNullException>(() => Encode(null));
        }

        [Test]
        public void HostileBytesAreRejected()
        {
            byte[] good = Encode(new ModChannelMessage { Channel = "M", Payload = new byte[] { 7 } });
            // truncated anywhere
            for (int n = 0; n < good.Length; n++) Assert.Throws<DecodeException>(() => ModChannelMessage.Decode(good, 0, n), "length " + n);
            // trailing bytes
            var longer = new byte[good.Length + 1];
            Buffer.BlockCopy(good, 0, longer, 0, good.Length);
            Assert.Throws<DecodeException>(() => ModChannelMessage.Decode(longer, 0, longer.Length));
            // other version, unknown target, empty channel
            byte[] version = (byte[])good.Clone();
            version[0] = 2;
            Assert.Throws<DecodeException>(() => ModChannelMessage.Decode(version, 0, version.Length));
            byte[] target = (byte[])good.Clone();
            target[1 + 1 + 1 + 1] = 3; // version, length, 'M', from, target
            Assert.Throws<DecodeException>(() => ModChannelMessage.Decode(target, 0, target.Length));
            var empty = new byte[] { 1, 0, 0, 0, 0, 0 };
            Assert.Throws<DecodeException>(() => ModChannelMessage.Decode(empty, 0, empty.Length));
            // a payload length above the limit fails before allocating
            var w = new ByteWriter();
            w.WriteByte(1);
            w.WriteString("M", 128);
            w.WriteByte(0);
            w.WriteByte(0);
            w.WriteByte(0);
            w.WriteVarUInt32(ModChannelMessage.MaxPayloadBytes + 1);
            byte[] huge = w.ToArray();
            Assert.Throws<DecodeException>(() => ModChannelMessage.Decode(huge, 0, huge.Length));
        }

        [Test]
        public void TheHostRelaysAClientsMessages()
        {
            var live = new List<PeerId> { P1, P2, P3 };
            ModChannelRoute r = ModChannelRouter.Route(P1, ModChannelTarget.Everyone, default, live);
            Assert.True(r.DeliverToHost);
            Assert.SequenceEqual(new[] { P2, P3 }, r.Forward);

            r = ModChannelRouter.Route(P1, ModChannelTarget.Host, default, live);
            Assert.True(r.DeliverToHost);
            Assert.Equal(0, r.Forward.Count);

            r = ModChannelRouter.Route(P1, ModChannelTarget.Player, P3, live);
            Assert.False(r.DeliverToHost);
            Assert.SequenceEqual(new[] { P3 }, r.Forward);

            r = ModChannelRouter.Route(P1, ModChannelTarget.Player, PeerId.Host, live);
            Assert.True(r.DeliverToHost, "player 0 is the host");

            r = ModChannelRouter.Route(P1, ModChannelTarget.Player, P1, live);
            Assert.False(r.DeliverToHost);
            Assert.Equal(0, r.Forward.Count, "never back to the sender");

            r = ModChannelRouter.Route(P1, ModChannelTarget.Player, new PeerId(9), live);
            Assert.Equal(0, r.Forward.Count, "a player that is not live is skipped");
        }

        [Test]
        public void TheHostsOwnMessagesGoToTheClients()
        {
            var live = new List<PeerId> { P1, P2, PeerId.Host, P2 };
            ModChannelRoute r = ModChannelRouter.Route(PeerId.Host, ModChannelTarget.Everyone, default, live);
            Assert.False(r.DeliverToHost);
            Assert.SequenceEqual(new[] { P1, P2 }, r.Forward);
            r = ModChannelRouter.Route(PeerId.Host, ModChannelTarget.Host, default, live);
            Assert.False(r.DeliverToHost);
            Assert.Equal(0, r.Forward.Count);
            r = ModChannelRouter.Route(PeerId.Host, ModChannelTarget.Player, P2, null);
            Assert.Equal(0, r.Forward.Count);
        }

        [Test]
        public void TheBudgetAllowsABurstThenRefills()
        {
            var b = new ModChannelBudget(1000, 5);
            for (int i = 0; i < 5; i++) Assert.True(b.TrySpend(100, 10.0), "message " + i);
            Assert.False(b.TrySpend(100, 10.0), "6th message in the same second");
            Assert.Equal(1L, b.Refused);
            Assert.True(b.TrySpend(100, 10.3), "1.5 messages refilled");
            Assert.False(b.TrySpend(100, 10.3), "half a message left");
            Assert.True(b.TrySpend(600, 11.3));
            Assert.False(b.TrySpend(-1, 20.0));
            // time going backwards never refills
            var c = new ModChannelBudget(100, 100);
            Assert.True(c.TrySpend(100, 5.0));
            Assert.False(c.TrySpend(1, 4.0));
        }
    }
}
