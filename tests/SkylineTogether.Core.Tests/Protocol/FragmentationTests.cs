using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Protocol.ProtocolTestUtil;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>Fragmenter and Reassembler (ARCH §9.3): fit the 512 KiB cap, restore exactly, reject gaps cleanly.</summary>
    public sealed class FragmentationTests
    {
        private static readonly EnvelopeCodec Codec = new EnvelopeCodec(null);

        /// <summary>An uncompressed envelope of <paramref name="bodySize"/> pseudo-random bytes.</summary>
        private static byte[] Envelope(MessageKind kind, int bodySize, ulong seed)
        {
            var random = new DeterministicRandom(seed);
            var body = new byte[bodySize];
            random.NextBytes(body, 0, bodySize);
            var output = new ByteWriter();
            Codec.Encode(kind, body, 0, bodySize, output);
            return output.ToArray();
        }

        private static List<byte[]> Split(Fragmenter fragmenter, MessageKind kind, byte[] message)
        {
            var fragments = new List<byte[]>();
            fragmenter.Split(kind, message, 0, message.Length, new ByteWriter(), (b, o, c) => fragments.Add(Slice(b, o, c)));
            return fragments;
        }

        private static Envelope Fragment(byte[] wire) => Codec.Decode(wire, 0, wire.Length, new ByteWriter());

        [Test]
        public void SmallMessagesAreEmittedUnchanged()
        {
            byte[] message = Envelope(MessageKind.Epoch, 1000, 1);
            List<byte[]> fragments = Split(new Fragmenter(), MessageKind.Epoch, message);
            Assert.Equal(1, fragments.Count);
            Assert.SequenceEqual(message, fragments[0]);

            byte[] exactly = Envelope(MessageKind.Epoch, Limits.FragmentThreshold - 2, 2);
            Assert.Equal(1, Split(new Fragmenter(), MessageKind.Epoch, exactly).Count, "at the threshold: not fragmented");
        }

        [Test]
        public void LargeMessagesAreSplitAndRestoredExactly()
        {
            foreach (int size in new[] { Limits.FragmentThreshold - 1, Limits.FragmentThreshold * 3, Limits.MaxUncompressedBytes })
            {
                byte[] message = Envelope(MessageKind.Epoch, size, (ulong)size);
                List<byte[]> fragments = Split(new Fragmenter(), MessageKind.Epoch, message);
                Assert.True(fragments.Count >= 2);
                var reassembler = new Reassembler();
                for (int i = 0; i < fragments.Count; i++)
                {
                    Assert.True(fragments[i].Length <= Limits.FragmentThreshold, "every fragment fits below the threshold");
                    Assert.True(fragments[i].Length <= Limits.MaxMessageBytes);
                    bool done = reassembler.Accept(Fragment(fragments[i]), out byte[] restored, out int length);
                    Assert.Equal(i == fragments.Count - 1, done);
                    if (!done) continue;
                    Assert.Equal(message.Length, length);
                    Assert.SequenceEqual(message, Slice(restored, 0, length));
                    Envelope inner = Codec.Decode(restored, 0, length, new ByteWriter());
                    Assert.Equal(MessageKind.Epoch, inner.Kind);
                    Assert.Equal(size, inner.BodyCount);
                }
                Assert.Equal(0, reassembler.PartialMessages);
                Assert.Equal(0L, reassembler.PartialBytes);
            }
        }

        [Test]
        public void FragmentLayoutIsPinned()
        {
            byte[] message = Envelope(MessageKind.Control, Limits.FragmentThreshold, 3);   // 491522 bytes encoded
            List<byte[]> fragments = Split(new Fragmenter(), MessageKind.Control, message);
            Assert.Equal(2, fragments.Count);
            // kind Control | flags Fragment | id 1 | index 0 | count 2 | total 491522 (varint 82 80 1e)
            Assert.SequenceEqual(Hex("04 02 01 00 02 82801e"), Slice(fragments[0], 0, 8));
            Assert.SequenceEqual(Hex("04 02 01 01 02 82801e"), Slice(fragments[1], 0, 8));
            Assert.Equal(Fragmenter.ChunkBytes + 8, fragments[0].Length);
        }

        [Test]
        public void InterleavedMessagesAreKeptApart()
        {
            var fragmenter = new Fragmenter();
            byte[] a = Envelope(MessageKind.Epoch, 1_200_000, 10);
            byte[] b = Envelope(MessageKind.Control, 700_000, 11);
            List<byte[]> fa = Split(fragmenter, MessageKind.Epoch, a);
            List<byte[]> fb = Split(fragmenter, MessageKind.Control, b);
            var reassembler = new Reassembler();
            var completed = new List<byte[]>();
            int ia = 0, ib = 0;
            while (ia < fa.Count || ib < fb.Count)
            {
                byte[] next = (ib >= fb.Count || (ia < fa.Count && ia <= ib)) ? fa[ia++] : fb[ib++];
                if (reassembler.Accept(Fragment(next), out byte[] message, out int length)) completed.Add(Slice(message, 0, length));
            }
            Assert.Equal(2, completed.Count);
            Assert.SequenceEqual(b, completed[0]);
            Assert.SequenceEqual(a, completed[1]);
        }

        [Test]
        public void LostReorderedOrDuplicatedFragmentsAreRejectedCleanly()
        {
            byte[] message = Envelope(MessageKind.Epoch, 2_000_000, 20);
            List<byte[]> fragments = Split(new Fragmenter(), MessageKind.Epoch, message);
            Assert.True(fragments.Count >= 4);

            var lost = new Reassembler();
            lost.Accept(Fragment(fragments[0]), out _, out _);
            Assert.Throws<DecodeException>(() => lost.Accept(Fragment(fragments[2]), out _, out _), "fragment 1 lost");
            Assert.Equal(0, lost.PartialMessages, "the broken message is dropped");

            var reordered = new Reassembler();
            Assert.Throws<DecodeException>(() => reordered.Accept(Fragment(fragments[1]), out _, out _), "starts with fragment 1");

            var duplicated = new Reassembler();
            duplicated.Accept(Fragment(fragments[0]), out _, out _);
            duplicated.Accept(Fragment(fragments[1]), out _, out _);
            Assert.Throws<DecodeException>(() => duplicated.Accept(Fragment(fragments[1]), out _, out _), "fragment 1 twice");
            Assert.Equal(0L, duplicated.PartialBytes);

            // after a rejection the same reassembler still restores a fresh message
            var reused = new Reassembler();
            Assert.Throws<DecodeException>(() => reused.Accept(Fragment(fragments[3]), out _, out _));
            byte[] restored = null;
            int restoredLength = 0;
            foreach (byte[] f in fragments)
            {
                if (reused.Accept(Fragment(f), out byte[] m, out int l))
                {
                    restored = m;
                    restoredLength = l;
                }
            }
            Assert.SequenceEqual(message, Slice(restored, 0, restoredLength));
        }

        [Test]
        public void InconsistentOrMalformedFragmentsAreRejected()
        {
            var reassembler = new Reassembler();
            // count 1, count 65, index beyond count, total below the threshold, total above the kind limit, empty chunk
            Assert.Throws<DecodeException>(() => reassembler.Accept(Fragment(Hex("05 02 01 00 01 82801e aa")), out _, out _));
            Assert.Throws<DecodeException>(() => reassembler.Accept(Fragment(Hex("05 02 01 00 41 82801e aa")), out _, out _));
            Assert.Throws<DecodeException>(() => reassembler.Accept(Fragment(Hex("05 02 01 02 02 82801e aa")), out _, out _));
            Assert.Throws<DecodeException>(() => reassembler.Accept(Fragment(Hex("05 02 01 00 02 05 aa")), out _, out _));
            Assert.Throws<DecodeException>(() => reassembler.Accept(Fragment(Hex("12 02 01 00 02 82801e aa")), out _, out _), "a 491522-byte Ping");
            Assert.Throws<DecodeException>(() => reassembler.Accept(Fragment(Hex("05 02 01 00 02 82801e")), out _, out _));
            Assert.Throws<DecodeException>(() => reassembler.Accept(Fragment(Hex("05 02 01 00")), out _, out _), "truncated header");

            // the second fragment changes the declared total
            reassembler.Accept(Fragment(Hex("05 02 07 00 02 82801e 05 00")), out _, out _);
            Assert.Throws<DecodeException>(() => reassembler.Accept(Fragment(Hex("05 02 07 01 02 83801e 00")), out _, out _));
            // fragments that carry fewer bytes than declared
            reassembler.Accept(Fragment(Hex("05 02 08 00 02 82801e 05 00")), out _, out _);
            Assert.Throws<DecodeException>(() => reassembler.Accept(Fragment(Hex("05 02 08 01 02 82801e 00")), out _, out _));
            Assert.Equal(0, reassembler.PartialMessages);
        }

        [Test]
        public void ReassembledBytesMustBeAnUnfragmentedEnvelopeOfTheSameKind()
        {
            // two fragments whose payload starts with kind Control instead of Epoch
            var reassembler = new Reassembler();
            var first = new ByteWriter();
            first.WriteByte(5);
            first.WriteByte(2);
            first.WriteVarUInt32(1);
            first.WriteVarUInt32(0);
            first.WriteVarUInt32(2);
            first.WriteVarUInt32(Limits.FragmentThreshold + 2);
            first.WriteByte(4);                       // inner kind Control
            first.WriteByte(0);
            first.WriteBytes(new byte[Limits.FragmentThreshold - 10], 0, Limits.FragmentThreshold - 10);
            reassembler.Accept(Fragment(first.ToArray()), out _, out _);
            var second = new ByteWriter();
            second.WriteByte(5);
            second.WriteByte(2);
            second.WriteVarUInt32(1);
            second.WriteVarUInt32(1);
            second.WriteVarUInt32(2);
            second.WriteVarUInt32(Limits.FragmentThreshold + 2);
            second.WriteBytes(new byte[10], 0, 10);
            Assert.Throws<DecodeException>(() => reassembler.Accept(Fragment(second.ToArray()), out _, out _));
        }

        [Test]
        public void PartialMessagesPerPeerAreCapped()
        {
            var reassembler = new Reassembler();
            for (uint id = 1; id <= Limits.MaxPartialMessagesPerPeer; id++)
            {
                var w = new ByteWriter();
                w.WriteByte(5);
                w.WriteByte(2);
                w.WriteVarUInt32(id);
                w.WriteVarUInt32(0);
                w.WriteVarUInt32(2);
                w.WriteVarUInt32(Limits.FragmentThreshold + 2);
                w.WriteByte(5);
                Assert.False(reassembler.Accept(Fragment(w.ToArray()), out _, out _));
            }
            Assert.Equal(Limits.MaxPartialMessagesPerPeer, reassembler.PartialMessages);
            Assert.Throws<DecodeException>(() => reassembler.Accept(Fragment(Hex("05 02 63 00 02 82801e 05")), out _, out _));
            reassembler.Reset();
            Assert.Equal(0, reassembler.PartialMessages);
            Assert.Equal(0L, reassembler.PartialBytes);
        }

        [Test]
        public void SplitRejectsInputThatIsNotAnEnvelopeOfTheKind()
        {
            var fragmenter = new Fragmenter();
            byte[] message = Envelope(MessageKind.Epoch, 600_000, 30);
            Assert.Throws<ArgumentException>(() => fragmenter.Split(MessageKind.Control, message, 0, message.Length, new ByteWriter(), (b, o, c) => { }));
            byte[] fragmentLike = (byte[])message.Clone();
            fragmentLike[1] = 2;
            Assert.Throws<ArgumentException>(() => fragmenter.Split(MessageKind.Epoch, fragmentLike, 0, fragmentLike.Length, new ByteWriter(), (b, o, c) => { }));
            byte[] tooLarge = new byte[Limits.MaxBodyBytes(MessageKind.Ack) + Limits.EnvelopeOverheadBytes + Limits.FragmentThreshold];
            tooLarge[0] = (byte)MessageKind.Ack;
            Assert.Throws<ArgumentException>(() => fragmenter.Split(MessageKind.Ack, tooLarge, 0, tooLarge.Length, new ByteWriter(), (b, o, c) => { }));
        }

        [Test]
        public void AcceptRejectsNonFragments()
        {
            Assert.Throws<ArgumentException>(() => new Reassembler().Accept(new Envelope { Kind = MessageKind.Epoch, Body = new byte[1], BodyCount = 1 }, out _, out _));
        }
    }
}
