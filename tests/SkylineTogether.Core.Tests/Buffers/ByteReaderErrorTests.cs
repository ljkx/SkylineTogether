using System;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Buffers
{
    /// <summary>Every overrun and every invalid encoding throws DecodeException and nothing else (IMPLEMENTATION-PLAN §4).</summary>
    public sealed class ByteReaderErrorTests
    {
        private static ByteReader Reader(params byte[] bytes) => new ByteReader(bytes, 0, bytes.Length);

        [Test]
        public void OverrunsThrowDecodeException()
        {
            Assert.Throws<DecodeException>(() => Reader().ReadByte());
            Assert.Throws<DecodeException>(() => Reader().PeekByte());
            Assert.Throws<DecodeException>(() => Reader(1).ReadUInt16());
            Assert.Throws<DecodeException>(() => Reader(1, 2, 3).ReadUInt32());
            Assert.Throws<DecodeException>(() => Reader(1, 2, 3).ReadInt32());
            Assert.Throws<DecodeException>(() => Reader(1, 2, 3, 4, 5, 6, 7).ReadUInt64());
            Assert.Throws<DecodeException>(() => Reader(1, 2, 3, 4, 5, 6, 7).ReadInt64());
            Assert.Throws<DecodeException>(() => Reader(1, 2, 3).ReadSingle());
            Assert.Throws<DecodeException>(() => Reader(1, 2, 3, 4, 5, 6, 7).ReadDouble());
            Assert.Throws<DecodeException>(() => Reader().ReadBool());
            Assert.Throws<DecodeException>(() => Reader(0x80).ReadVarUInt32(), "continuation bit at the end");
            Assert.Throws<DecodeException>(() => Reader(0x80, 0x80).ReadVarUInt64());
            Assert.Throws<DecodeException>(() => Reader(0x81).ReadVarInt32());
            Assert.Throws<DecodeException>(() => Reader(0x81).ReadVarInt64());
            Assert.Throws<DecodeException>(() => Reader(1, 2).ReadBytes(new byte[3], 0, 3));
            Assert.Throws<DecodeException>(() => Reader(1, 2).ReadSegment(3));
            Assert.Throws<DecodeException>(() => Reader(1, 2).Skip(3));
            Assert.Throws<DecodeException>(() => Reader(5, (byte)'a').ReadString(100), "declared length beyond the window");
        }

        [Test]
        public void NegativeCountsAreDecodeErrors()
        {
            Assert.Throws<DecodeException>(() => Reader(1, 2).ReadBytes(new byte[3], 0, -1));
            Assert.Throws<DecodeException>(() => Reader(1, 2).ReadSegment(-1));
            Assert.Throws<DecodeException>(() => Reader(1, 2).Skip(-5));
        }

        [Test]
        public void DestinationErrorsAreArgumentErrors()
        {
            Assert.Throws<ArgumentNullException>(() => Reader(1, 2).ReadBytes(null, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Reader(1, 2).ReadBytes(new byte[1], 0, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => Reader(1, 2).ReadBytes(new byte[4], -1, 1));
        }

        [Test]
        public void VarintsBeyondTheirWidthAreRejected()
        {
            Assert.Throws<DecodeException>(() => Reader(0xFF, 0xFF, 0xFF, 0xFF, 0x10).ReadVarUInt32(), "33rd bit set");
            Assert.Throws<DecodeException>(() => Reader(0xFF, 0xFF, 0xFF, 0xFF, 0x8F, 0x01).ReadVarUInt32(), "sixth byte");
            Assert.Throws<DecodeException>(() => Reader(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02).ReadVarUInt64(), "65th bit set");
            Assert.Throws<DecodeException>(() => Reader(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x81, 0x00).ReadVarUInt64(), "eleventh byte");
            Assert.Equal(uint.MaxValue, Reader(0xFF, 0xFF, 0xFF, 0xFF, 0x0F).ReadVarUInt32());
            Assert.Equal(ulong.MaxValue, Reader(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01).ReadVarUInt64());
        }

        [Test]
        public void OverlongVarintsAreRejected()
        {
            // the same bytes are hashed, so only the canonical (shortest) encoding is accepted
            Assert.Throws<DecodeException>(() => Reader(0x80, 0x00).ReadVarUInt32());
            Assert.Throws<DecodeException>(() => Reader(0x81, 0x80, 0x00).ReadVarUInt32());
            Assert.Throws<DecodeException>(() => Reader(0xFF, 0x00).ReadVarUInt64());
            Assert.Throws<DecodeException>(() => Reader(0x80, 0x00).ReadVarInt32());
            Assert.Equal(0u, Reader(0x00).ReadVarUInt32());
            Assert.Equal(1u, Reader(0x01).ReadVarUInt32());
        }

        [Test]
        public void BooleansMustBeZeroOrOne()
        {
            Assert.False(Reader(0).ReadBool());
            Assert.True(Reader(1).ReadBool());
            Assert.Throws<DecodeException>(() => Reader(2).ReadBool());
            Assert.Throws<DecodeException>(() => Reader(0xFF).ReadBool());
        }

        [Test]
        public void StringLimitIsCheckedBeforeDecoding()
        {
            var w = new ByteWriter();
            w.WriteVarUInt32(int.MaxValue);   // a hostile length with no data behind it
            DecodeException e = Assert.Throws<DecodeException>(() => new ByteReader(w.Buffer, 0, w.Length).ReadString(64));
            Assert.Contains("exceeds the limit", e.Message);
            Assert.Throws<DecodeException>(() => new ByteReader(w.Buffer, 0, w.Length).ReadString(int.MaxValue));
        }

        [Test]
        public void ErrorsNamePositions()
        {
            ByteReader r = Reader(1, 2, 3);
            r.ReadUInt16();
            DecodeException e = Assert.Throws<DecodeException>(() => r.ReadUInt32());
            Assert.Contains("position 2", e.Message);
            Assert.Contains("1 remaining", e.Message);
        }

        [Test]
        public void FuzzedReadsThrowOnlyDecodeException()
        {
            // random byte strings fed to random reads: anything but DecodeException is a bug
            var random = new DeterministicRandom(20261011);
            var data = new byte[64];
            for (int round = 0; round < 5000; round++)
            {
                int length = random.Next(data.Length + 1);
                random.NextBytes(data, 0, length);
                var r = new ByteReader(data, 0, length);
                try
                {
                    for (int step = 0; step < 16 && !r.IsAtEnd; step++)
                    {
                        switch (random.Next(9))
                        {
                            case 0: r.ReadVarUInt32(); break;
                            case 1: r.ReadVarUInt64(); break;
                            case 2: r.ReadVarInt32(); break;
                            case 3: r.ReadString(16); break;
                            case 4: r.ReadUInt32(); break;
                            case 5: r.ReadBool(); break;
                            case 6: r.ReadSegment(random.Next(8)); break;
                            case 7: r.ReadDouble(); break;
                            default: r.Skip(random.Next(4)); break;
                        }
                    }
                }
                catch (DecodeException)
                {
                    // expected for malformed input
                }
            }
        }
    }
}
