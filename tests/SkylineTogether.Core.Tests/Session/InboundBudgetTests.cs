using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// The receive side's guards against decompression floods: kind allowlists per direction and per peer state
    /// (checked on the envelope's first byte, before anything is decompressed or reassembled), the handshake's total
    /// decompression budget, the per-peer budget per frame with deferral, and the eviction of abandoned partial messages.
    /// </summary>
    public sealed class InboundBudgetTests
    {
        private static byte[] Compressed(MessageKind kind, int size, byte marker = 0)
        {
            var body = new byte[size];
            body[0] = marker;
            var w = new ByteWriter(1024);
            new EnvelopeCodec(new ManagedLz4Compressor()).Encode(kind, body, 0, body.Length, w);
            byte[] wire = w.ToArray();
            Assert.Equal((byte)(EnvelopeFlags.Compressed | EnvelopeFlags.HasUncompressedSize), wire[1], "the test message is compressed");
            return wire;
        }

        private static PeerChannel Channel()
        {
            var (a, b) = LoopbackTransport.CreatePair();
            a.Listen(new ListenOptions());
            IConnection c = b.Connect(new Endpoint());
            return new PeerChannel(c, PeerId.Host, new ManualClock(), new ListLog());
        }

        [Test]
        public void AnUnadmittedLimitRejectsHostOnlyKindsBeforeDecompressing()
        {
            PeerChannel ch = Channel();
            ch.InboundLimit = k => Limits.IsHandshakeKind(k) ? Limits.MaxBodyBytes(k) : 0;
            ch.RejectUnexpectedKinds = true;
            byte[] bomb = Compressed(MessageKind.Epoch, 1024 * 1024);
            Assert.Throws<DecodeException>(() => ch.TryDecode(bomb, 0, bomb.Length, out _));
            Assert.Equal(0L, ch.DecompressedBytes, "nothing was decompressed");

            // a hello fits; a hello that declares more than its limit is refused on the declared size
            byte[] hello = Compressed(MessageKind.Hello, 4096);
            Assert.True(ch.TryDecode(hello, 0, hello.Length, out Envelope e));
            Assert.Equal(MessageKind.Hello, e.Kind);
            Assert.Equal(4096L, ch.DecompressedBytes);
            ch.MaxBodyBytes = 1024;
            Assert.Throws<DecodeException>(() => ch.TryDecode(hello, 0, hello.Length, out _), "the channel's own lower limit applies too");
        }

        [Test]
        public void AnAdmittedPeersHostOnlyKindsAreDroppedUnread()
        {
            PeerChannel ch = Channel();
            ch.InboundLimit = Limits.MaxClientBodyBytes;
            byte[] bomb = Compressed(MessageKind.Statistics, 8 * 1024 * 1024);
            Assert.False(ch.TryDecode(bomb, 0, bomb.Length, out _));
            Assert.Equal(1L, ch.DroppedKinds);
            Assert.Equal(0L, ch.DecompressedBytes);
            // a control message of a client is capped well below the kind's 8 MiB
            byte[] control = Compressed(MessageKind.Control, 2 * 1024 * 1024);
            Assert.Throws<DecodeException>(() => ch.TryDecode(control, 0, control.Length, out _));
        }

        [Test]
        public void EveryKindIsSentByAtLeastOneSideAndTheTablesMatchTheRouter()
        {
            foreach (MessageKind k in Enum.GetValues(typeof(MessageKind)))
            {
                bool any = Limits.MaxClientBodyBytes(k) > 0 || Limits.MaxHostBodyBytes(k) > 0;
                Assert.Equal(Limits.IsDeliverable(k) || k == MessageKind.Hello || k == MessageKind.Welcome || k == MessageKind.Refuse
                             || k == MessageKind.Control || k == MessageKind.Ping || k == MessageKind.Chat || k == MessageKind.Presence
                             || k == MessageKind.BlobChunk || k == MessageKind.BlobHave, any, k + " is accepted by one side exactly when it is a live kind");
                Assert.True(Limits.MaxClientBodyBytes(k) <= Limits.MaxBodyBytes(k), k.ToString());
            }
            Assert.Equal(0, Limits.MaxClientBodyBytes(MessageKind.Epoch), "only the host sends epochs");
            Assert.Equal(0, Limits.MaxHostBodyBytes(MessageKind.Proposal), "only clients send proposals");
            Assert.Equal(0, Limits.MaxClientBodyBytes((MessageKind)200), "unknown kinds");
        }

        [Test]
        public void AStrangerSendingHostOnlyKindsIsDisconnectedWithoutDecompressing()
        {
            var rig = new SessionRig();
            var raw = new RawPeer(rig, "Stranger", 7301);
            raw.SendRaw(Compressed(MessageKind.Epoch, 1024 * 1024));
            for (int i = 0; i < 5; i++)
            {
                rig.Clock.Advance(0.05);
                rig.Host.Tick();
                raw.Poll();
            }
            Assert.True(raw.Disconnected, "the connection was closed");
            Assert.Contains("not accepted", string.Join("\n", rig.Log.Lines));
        }

        [Test]
        public void TheHandshakeHasATotalDecompressionBudget()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", Password = "pw", UnadmittedDecodeBudgetBytes = 256 * 1024 });
            var raw = new RawPeer(rig, "Flooder", 7302);
            raw.Hello(rig.HostInfo);
            // control messages a peer may send before admission, each well under its limit, until the budget is spent
            var control = new ByteWriter(1 << 16);
            ControlMessage.Write(control, ControlKind.ReadyForSnapshot, new byte[60 * 1024], 0, 60 * 1024);
            var w = new ByteWriter(1024);
            new EnvelopeCodec(new ManagedLz4Compressor()).Encode(MessageKind.Control, control.Buffer, 0, control.Length, w);
            byte[] wire = w.ToArray();
            for (int i = 0; i < 8; i++) raw.SendRaw(wire);
            for (int i = 0; i < 5; i++)
            {
                rig.Clock.Advance(0.05);
                rig.Host.Tick();
                raw.Poll();
            }
            Assert.True(raw.Disconnected);
            Assert.Contains("bytes of handshake data; disconnecting", string.Join("\n", rig.Log.Lines));
        }

        [Test]
        public void APeersDecompressionBudgetPerFrameDefersTheRestInOrder()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", PeerDecodeBudgetBytesPerFrame = 8 * 1024 * 1024 });
            var (client, _, _) = rig.AddClient("Anna");
            rig.Run(400);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            var order = new List<byte>();
            rig.Host.AppMessage += (peer, e) =>
            {
                if (e.Kind == MessageKind.Proposal) order.Add(e.Body[e.BodyOffset]);
            };
            var body = new byte[3 * 1024 * 1024];
            for (byte i = 0; i < 7; i++)
            {
                body[0] = i;
                Assert.True(client.SendToHost(MessageKind.Proposal, body, 0, body.Length));
            }
            client.Tick(); // hands everything to the transport
            rig.Host.Tick();
            Assert.Equal(3, order.Count, "3 MiB, 6 MiB, 9 MiB: the third spends the frame's 8 MiB, the rest waits");
            rig.Host.Tick();
            Assert.Equal(6, order.Count);
            rig.Host.Tick();
            Assert.Equal(7, order.Count);
            for (int i = 0; i < 7; i++) Assert.Equal((byte)i, order[i], "in the order sent");
            Assert.Equal(ClientState.Live, client.State, "an honest burst is only delayed");
        }

        [Test]
        public void APeerThatOutrunsItsBudgetForLongIsDisconnected()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", PeerDecodeBudgetBytesPerFrame = 1024 * 1024, MaxDeferredBytesPerPeer = 64 * 1024 });
            var (client, _, _) = rig.AddClient("Mallory");
            rig.Run(400);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            var body = new byte[4 * 1024 * 1024];
            for (int i = 0; i < 400; i++) client.SendToHost(MessageKind.Proposal, body, 0, body.Length);
            client.Tick();
            rig.Host.Tick();
            Assert.Equal(0, rig.Host.LivePeerCount, "dropped instead of queueing without bound");
            Assert.Contains("more data than the host can process", string.Join("\n", rig.Log.Lines));
        }

        [Test]
        public void AbandonedPartialMessagesAreEvictedWhenIdle()
        {
            var r = new Reassembler();
            var codec = new EnvelopeCodec(null);
            var fragmenter = new Fragmenter();
            var body = new byte[1024 * 1024];
            new Random(5).NextBytes(body);
            var encoded = new ByteWriter(1 << 20);
            codec.Encode(MessageKind.Proposal, body, 0, body.Length, encoded);
            var fragments = new List<byte[]>();
            var scratch = new ByteWriter(1 << 20);
            fragmenter.Split(MessageKind.Proposal, encoded.Buffer, 0, encoded.Length, scratch, (b, o, c) =>
            {
                var copy = new byte[c];
                Buffer.BlockCopy(b, o, copy, 0, c);
                fragments.Add(copy);
            });
            Assert.True(fragments.Count >= 2);
            var decodeScratch = new ByteWriter();
            Envelope first = codec.Decode(fragments[0], 0, fragments[0].Length, decodeScratch);
            Assert.False(r.Accept(first, out _, out _, int.MaxValue, 10.0));
            Assert.Equal(1, r.PartialMessages);
            Assert.Equal(0, r.EvictIdle(100.0, 120), "not idle long enough");
            Assert.Equal(1, r.EvictIdle(200.0, 120));
            Assert.Equal(0, r.PartialMessages);
            Assert.Equal(0L, r.PartialBytes);
            Envelope second = codec.Decode(fragments[1], 0, fragments[1].Length, decodeScratch);
            Assert.Throws<DecodeException>(() => r.Accept(second, out _, out _, int.MaxValue, 201.0), "the rest of an evicted message is a protocol error");
        }

        [Test]
        public void TheClientDropsKindsAHostNeverSends()
        {
            var rig = new SessionRig();
            var (client, _, _) = rig.AddClient("Anna");
            rig.Run(400);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            int proposals = 0;
            client.AppMessage += e =>
            {
                if (e.Kind == MessageKind.Proposal) proposals++;
            };
            var body = new byte[1000];
            rig.Host.Broadcast(MessageKind.Proposal, body, 0, body.Length);
            rig.Run(10);
            Assert.Equal(0, proposals);
            Assert.Equal(ClientState.Live, client.State);
        }
    }
}
