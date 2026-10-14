using System;
using System.Collections.Generic;
using System.IO;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Replay;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>
    /// Seeded properties of the byte-level codecs: the managed LZ4 block codec (round trips at every size class, and a
    /// decoder that never writes outside its destination window, even on corrupt input), random typed sequences
    /// through <see cref="ByteWriter"/> and <see cref="ByteReader"/>, and the replay recording reader on damaged files.
    /// </summary>
    public sealed class CodecPropertyTests
    {
        private static byte[] Compressible(Gen g, int size)
        {
            var data = new byte[size];
            int style = g.Int(4);
            for (int i = 0; i < size; i++)
            {
                switch (style)
                {
                    case 0: data[i] = (byte)g.Int(256); break;                         // incompressible
                    case 1: data[i] = (byte)(i % 7); break;                           // short period
                    case 2: data[i] = (byte)(g.OneIn(10) ? g.Int(256) : 0); break;    // sparse
                    default: data[i] = (byte)(i / 300 % 3 == 0 ? g.Int(4) : i % 251); break;
                }
            }
            return data;
        }

        [Test(TimeoutSeconds = 120)]
        public void Lz4RoundTripsEverySizeClassAndStaysInsideItsWindow()
        {
            var lz4 = new ManagedLz4Compressor();
            Prop.ForAll("lz4 round trip", 1800, 300, g =>
            {
                int size = g.Pick(g.Int(0, 20), g.Int(0, 300), g.Int(60_000, 70_000), g.Size(200_000));
                byte[] data = Compressible(g, size);
                if (size == 0) return;
                var compressed = new byte[lz4.MaxCompressedSize(size) + 8];
                int n = lz4.Compress(data, 0, size, compressed, 4);
                Assert.True(n > 0 && n <= lz4.MaxCompressedSize(size), "compressed size " + n);
                // the destination window sits between guard bytes that must survive
                int pad = g.Int(1, 9);
                var dst = new byte[size + 2 * pad];
                for (int i = 0; i < dst.Length; i++) dst[i] = 0xA5;
                int produced = lz4.Decompress(compressed, 4, n, dst, pad, size);
                Assert.Equal(size, produced);
                for (int i = 0; i < pad; i++)
                {
                    Assert.Equal((byte)0xA5, dst[i], "guard before");
                    Assert.Equal((byte)0xA5, dst[pad + size + i], "guard after");
                }
                for (int i = 0; i < size; i++)
                {
                    if (dst[pad + i] != data[i]) Assert.Fail("byte " + i + " differs");
                }
            });
        }

        [Test(TimeoutSeconds = 120)]
        public void CorruptLz4BlocksNeverWriteOutsideTheirWindow()
        {
            var lz4 = new ManagedLz4Compressor();
            Prop.ForAll("lz4 corrupt", 1810, 2000, g =>
            {
                int size = g.Pick(g.Int(1, 40), g.Int(1, 2000));
                byte[] data = Compressible(g, size);
                var compressed = new byte[lz4.MaxCompressedSize(size)];
                int n = lz4.Compress(data, 0, size, compressed, 0);
                var block = new byte[n];
                Buffer.BlockCopy(compressed, 0, block, 0, n);
                byte[] bad = g.Mutate(block);
                if (bad.Length == 0) return;
                int expected = g.OneIn(3) ? g.Int(0, size * 2 + 1) : size;   // also a wrong declared size
                const int Pad = 16;
                var dst = new byte[expected + 2 * Pad];
                for (int i = 0; i < dst.Length; i++) dst[i] = 0x5A;
                try
                {
                    int produced = lz4.Decompress(bad, 0, bad.Length, dst, Pad, expected);
                    Assert.True(produced >= 0 && produced <= expected, "produced " + produced + " of " + expected);
                }
                catch (DecodeException)
                {
                }
                for (int i = 0; i < Pad; i++)
                {
                    Assert.Equal((byte)0x5A, dst[i], "guard before at " + i);
                    Assert.Equal((byte)0x5A, dst[Pad + expected + i], "guard after at " + i);
                }
            });
        }

        // ------------------------------------------------------------------ byte writer / reader

        private enum Op { Byte, Bool, U16, U32, U64, I32, Single, Double, VarU32, VarU64, VarI32, VarI64, Str, Bytes }

        [Test(TimeoutSeconds = 120)]
        public void RandomTypedSequencesRoundTripThroughWriterAndReader()
        {
            Prop.ForAll("writer/reader", 1820, 400, g =>
            {
                var ops = new List<(Op Op, object Value)>();
                var w = new ByteWriter(g.Pick(0, 1, 16, 256));
                int count = g.Size(60);
                for (int i = 0; i < count; i++)
                {
                    Op op = g.Enum<Op>();
                    object v;
                    switch (op)
                    {
                        case Op.Byte: v = (byte)g.Int(256); w.WriteByte((byte)v); break;
                        case Op.Bool: v = g.Bool(); w.WriteBool((bool)v); break;
                        case Op.U16: v = (ushort)g.Int(65536); w.WriteUInt16((ushort)v); break;
                        case Op.U32: v = g.UIntEdgy(); w.WriteUInt32((uint)v); break;
                        case Op.U64: v = g.ULong(); w.WriteUInt64((ulong)v); break;
                        case Op.I32: v = (int)g.UIntEdgy(); w.WriteInt32((int)v); break;
                        case Op.Single: v = g.Float(true); w.WriteSingle((float)v); break;
                        case Op.Double: v = BitConverter.Int64BitsToDouble(g.Long()); w.WriteDouble((double)v); break;
                        case Op.VarU32: v = g.UIntEdgy(); w.WriteVarUInt32((uint)v); break;
                        case Op.VarU64: v = g.OneIn(2) ? (ulong)g.UIntEdgy() : g.ULong(); w.WriteVarUInt64((ulong)v); break;
                        case Op.VarI32: v = (int)g.UIntEdgy(); w.WriteVarInt32((int)v); break;
                        case Op.VarI64: v = g.Long(); w.WriteVarInt64((long)v); break;
                        case Op.Str: v = g.String(80); w.WriteString((string)v, 1024); break;
                        default: v = g.Bytes(50); w.WriteVarUInt32((uint)((byte[])v).Length); w.WriteBytes((byte[])v, 0, ((byte[])v).Length); break;
                    }
                    ops.Add((op, v));
                }
                byte[] bytes = w.ToArray();
                Assert.Equal(w.Length, bytes.Length);
                int pad = g.Int(0, 5);
                var framed = new byte[bytes.Length + pad + 3];
                Buffer.BlockCopy(bytes, 0, framed, pad, bytes.Length);
                var r = new ByteReader(framed, pad, bytes.Length);
                foreach ((Op op, object v) in ops)
                {
                    switch (op)
                    {
                        case Op.Byte: Assert.Equal((byte)v, r.ReadByte()); break;
                        case Op.Bool: Assert.Equal((bool)v, r.ReadBool()); break;
                        case Op.U16: Assert.Equal((ushort)v, r.ReadUInt16()); break;
                        case Op.U32: Assert.Equal((uint)v, r.ReadUInt32()); break;
                        case Op.U64: Assert.Equal((ulong)v, r.ReadUInt64()); break;
                        case Op.I32: Assert.Equal((int)v, r.ReadInt32()); break;
                        case Op.Single: Assert.Equal(BitConverter.SingleToInt32Bits((float)v), BitConverter.SingleToInt32Bits(r.ReadSingle()), "float bits"); break;
                        case Op.Double: Assert.Equal(BitConverter.DoubleToInt64Bits((double)v), BitConverter.DoubleToInt64Bits(r.ReadDouble()), "double bits"); break;
                        case Op.VarU32: Assert.Equal((uint)v, r.ReadVarUInt32()); break;
                        case Op.VarU64: Assert.Equal((ulong)v, r.ReadVarUInt64()); break;
                        case Op.VarI32: Assert.Equal((int)v, r.ReadVarInt32()); break;
                        case Op.VarI64: Assert.Equal((long)v, r.ReadVarInt64()); break;
                        case Op.Str: Assert.Equal((string)v, r.ReadString(1024)); break;
                        default:
                        {
                            var expected = (byte[])v;
                            Assert.Equal((uint)expected.Length, r.ReadVarUInt32());
                            var got = new byte[expected.Length];
                            r.ReadBytes(got, 0, got.Length);
                            Assert.SequenceEqual(expected, got);
                            break;
                        }
                    }
                }
                Assert.True(r.IsAtEnd, "everything consumed");
                Assert.Throws<DecodeException>(() => r.ReadByte(), "the window ends where the writer ended, not at the array's end");
            });
        }

        // ------------------------------------------------------------------ recordings

        private sealed class KeptStream : MemoryStream
        {
            protected override void Dispose(bool disposing)
            {
            }
        }

        [Test(TimeoutSeconds = 120)]
        public void DamagedRecordingsEndCleanly()
        {
            var stream = new KeptStream();
            var w = new RecordingWriter(stream, new RecordingHeader { ModBuild = 1, GameVersion = "1", SessionName = "s", HostName = "h", CityName = "c", RecorderName = "r" });
            var g0 = new Gen(1830);
            w.Connected(0, 1);
            for (int i = 0; i < 12; i++)
            {
                byte[] body = g0.Bytes(300);
                w.Message(0.1 * i, (uint)(2 + i), g0.Pick(Lane.Reliable, Lane.Bulk, Lane.Unreliable), body, 0, body.Length);
            }
            w.Barrier(2, 20, RecordingBarrier.LoadDone);
            w.Note(2.5, 21, "note");
            w.Disconnected(3, 22, 1, "bye");
            w.End(3.5, 23, "end");
            w.Dispose();
            byte[] file = stream.ToArray();

            int rejected = DecoderContract.Fuzz("recording", new[] { file }, 1500, 1831, b =>
            {
                var r = new RecordingReader(new MemoryStream(b));       // a bad header: DecodeException
                int entries = 0;
                while (r.TryRead(out RecordingEntry e))                 // a bad entry: the stream ends (truncated or corrupt)
                {
                    Assert.NotNull(e);
                    if (++entries > 100) Assert.Fail("more entries than were written");
                }
                Assert.False(r.Truncated && r.Corrupt, "one reason at a time");
            }, bytesPerInputByte: 256, fixedBytes: 1 << 20);
            Assert.True(rejected > 0, "header damage is refused");
        }
    }
}
