using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>
    /// Random message sequences through the whole framing pipeline the session uses (<c>PeerChannel</c>): envelope
    /// encoding with or without compression, fragmentation above the transport cap, then on the other side envelope
    /// decoding, reassembly and decoding again. Every message must come out once, in order, with its kind and its exact
    /// body, every transport message must respect the cap, and nothing may stay half-assembled.
    /// </summary>
    public sealed class FramingPipelineTests
    {
        private static readonly MessageKind[] Kinds =
        {
            MessageKind.Hello, MessageKind.Welcome, MessageKind.Control, MessageKind.Epoch, MessageKind.Chat, MessageKind.Presence,
            MessageKind.Ping, MessageKind.BlobChunk, MessageKind.BlobHave, MessageKind.Terrain, MessageKind.SimValues, MessageKind.Proposal,
        };

        private static byte[] Body(Gen g, MessageKind kind)
        {
            int max = Limits.MaxBodyBytes(kind);
            int size;
            switch (g.Int(6))
            {
                case 0: size = 0; break;
                case 1: size = g.Int(1, 64); break;
                case 2: size = Limits.CompressThreshold + g.Int(-2, 3); break;
                case 3: size = g.Int(1000, 20_000); break;
                case 4: size = Limits.FragmentThreshold + g.Int(-40, 40); break;
                default: size = g.OneIn(3) ? g.Int(500_000, 1_400_000) : g.Int(20_000, 100_000); break;
            }
            size = Math.Max(0, Math.Min(size, max));
            var body = new byte[size];
            bool random = g.OneIn(2);
            for (int i = 0; i < size; i++) body[i] = random ? (byte)g.Int(256) : (byte)(i / 64 % 5);
            return body;
        }

        [Test(TimeoutSeconds = 120)]
        public void RandomMessageSequencesSurviveTheFramingPipeline()
        {
            Prop.ForAll("framing pipeline", 2000, 40, g =>
            {
                bool compress = g.Bool();
                // the receiver may have negotiated nothing: it still decodes LZ4 with its built-in decoder
                var sender = new EnvelopeCodec(compress ? new ManagedLz4Compressor() : null);
                var receiver = new EnvelopeCodec(g.Bool() ? new ManagedLz4Compressor() : null);
                var fragmenter = new Fragmenter();
                var reassembler = new Reassembler();
                var sent = new List<(MessageKind Kind, byte[] Body)>();
                var wire = new List<byte[]>();
                var encoded = new ByteWriter();
                var fragment = new ByteWriter();
                int count = g.Int(1, 25);
                for (int i = 0; i < count; i++)
                {
                    MessageKind kind = g.Pick(Kinds);
                    byte[] body = Body(g, kind);
                    sent.Add((kind, body));
                    encoded.Reset();
                    sender.Encode(kind, body, 0, body.Length, encoded);
                    fragmenter.Split(kind, encoded.Buffer, 0, encoded.Length, fragment, (b, o, c) =>
                    {
                        var packet = new byte[c];
                        Buffer.BlockCopy(b, o, packet, 0, c);
                        wire.Add(packet);
                    });
                }
                var received = new List<(MessageKind Kind, byte[] Body)>();
                var scratch = new ByteWriter();
                var scratch2 = new ByteWriter();
                foreach (byte[] packet in wire)
                {
                    Assert.True(packet.Length <= Limits.MaxMessageBytes, "a transport message of " + packet.Length + " bytes");
                    Envelope e = receiver.Decode(packet, 0, packet.Length, scratch);
                    if ((e.Flags & EnvelopeFlags.Fragment) != 0)
                    {
                        Assert.True(packet.Length <= Limits.FragmentThreshold, "fragments stay at the threshold");
                        if (!reassembler.Accept(e, out byte[] message, out int length)) continue;
                        e = receiver.Decode(message, 0, length, scratch2);
                    }
                    var body = new byte[e.BodyCount];
                    Buffer.BlockCopy(e.Body, e.BodyOffset, body, 0, e.BodyCount);
                    received.Add((e.Kind, body));
                }
                Assert.Equal(sent.Count, received.Count, "messages");
                for (int i = 0; i < sent.Count; i++)
                {
                    Assert.Equal(sent[i].Kind, received[i].Kind, "kind of message " + i);
                    Assert.Equal(sent[i].Body.Length, received[i].Body.Length, "length of message " + i);
                    for (int k = 0; k < sent[i].Body.Length; k++)
                    {
                        if (sent[i].Body[k] != received[i].Body[k]) Assert.Fail("message " + i + " differs at byte " + k);
                    }
                }
                Assert.Equal(0, reassembler.PartialMessages, "nothing left half-assembled");
                Assert.Equal(0L, reassembler.PartialBytes);
            });
        }
    }
}
