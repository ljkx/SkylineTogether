using System;
using System.Collections.Generic;
using SkylineTogether.Core.Codec;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Codec.CodecTestUtil;

namespace SkylineTogether.Core.Tests.Codec
{
    /// <summary>
    /// A random sequence of canonical members ("script") for the property and fuzz tests: it can be written with a
    /// <see cref="CanonicalWriter"/>, read back and verified value by value, or replayed against arbitrary bytes.
    /// </summary>
    internal sealed class CodecScript
    {
        internal enum Kind
        {
            Bool, Byte, SByte, Short, UShort, Int, UInt, Long, ULong, Char, Float, Double, String,
            Float2, Float3, Float4, Int2, Int3, Int4, UInt4, Bool2, Bool3, Bool4, Quaternion, Bezier, Color, Color32, Hash128,
            Entity, EntityIgnoreVersion, NetRef, EntityArray,
            IntArray, UShortArray, ByteArray, Float2Array, Float4Array, Int2Array, CountedBytes, Skip,
            Block, SkippedBlock,
        }

        internal sealed class Member
        {
            public Kind Kind;
            public ulong A, B, C, D;
            public string Text;
            public int[] Ints;
            public ushort[] UShorts;
            public byte[] Bytes;
            public float[] Floats;
            public int Count;
            public int Stride;
            public NetRef Ref;
            public (int Index, int Version)[] Entities;
            public List<Member> Children;
            public int WrittenSize;   // blocks: payload size, filled in while writing
        }

        private static readonly Kind[] Leaves = BuildLeaves();

        /// <summary>The entities the fake seams know: bound slots, prefabs, derived keys (index → expected handle).</summary>
        internal static readonly (int Index, int Version, NetRef Ref)[] KnownEntities =
        {
            (10, 1, NetRef.ForSlot(new NetSlot(1))),
            (11, 7, NetRef.ForSlot(new NetSlot(200))),
            (12, 2, NetRef.ForSlot(new NetSlot(NetSlot.MaxValue))),
            (13, 1, NetRef.ForPrefab(new PrefabRank(0))),
            (14, 3, NetRef.ForPrefab(new PrefabRank(123456))),
            (15, 1, NetRef.ForDerived(new DerivedKey(DerivedKeyKind.Lane, new NetSlot(1), new PrefabRank(5), -1, 640, 0, -12800, 0x100000002UL, 7, 0))),
            (16, 9, NetRef.ForDerived(new DerivedKey(DerivedKeyKind.Block, new NetSlot(200), PrefabRank.None, 1, 1, 2, 3, 0, 0, 0))),
            (17, 1, NetRef.ForProvisional(new LocalId(42))),
        };

        public readonly List<Member> Members = new List<Member>();

        internal static FakeEntityRefs Seams()
        {
            var fake = new FakeEntityRefs();
            foreach ((int index, int version, NetRef reference) in KnownEntities) fake.Bind(index, version, reference);
            return fake;
        }

        internal static CodecScript Random(DeterministicRandom random, int maxMembers, int maxDepth)
        {
            var script = new CodecScript();
            int n = random.Next(maxMembers + 1);
            for (int i = 0; i < n; i++) script.Members.Add(RandomMember(random, maxDepth));
            return script;
        }

        // ---------------------------------------------------------------- write

        internal void Write(CanonicalWriter w)
        {
            foreach (Member m in Members) Write(w, m);
        }

        private static void Write(CanonicalWriter w, Member m)
        {
            switch (m.Kind)
            {
                case Kind.Bool: w.WriteBool(m.A != 0); break;
                case Kind.Byte: w.WriteByte((byte)m.A); break;
                case Kind.SByte: w.WriteSByte(unchecked((sbyte)m.A)); break;
                case Kind.Short: w.WriteShort(unchecked((short)m.A)); break;
                case Kind.UShort: w.WriteUShort((ushort)m.A); break;
                case Kind.Int: w.WriteInt(unchecked((int)m.A)); break;
                case Kind.UInt: w.WriteUInt((uint)m.A); break;
                case Kind.Long: w.WriteLong(unchecked((long)m.A)); break;
                case Kind.ULong: w.WriteULong(m.A); break;
                case Kind.Char: w.WriteChar((char)m.A); break;
                case Kind.Float: w.WriteFloat(FromBits((uint)m.A)); break;
                case Kind.Double: w.WriteDouble(FromBits(m.A)); break;
                case Kind.String: w.WriteString(m.Text); break;
                case Kind.Float2: w.WriteFloat2(F(m.A), F(m.B)); break;
                case Kind.Float3: w.WriteFloat3(F(m.A), F(m.B), F(m.C)); break;
                case Kind.Float4: w.WriteFloat4(F(m.A), F(m.B), F(m.C), F(m.D)); break;
                case Kind.Int2: w.WriteInt2(I(m.A), I(m.B)); break;
                case Kind.Int3: w.WriteInt3(I(m.A), I(m.B), I(m.C)); break;
                case Kind.Int4: w.WriteInt4(I(m.A), I(m.B), I(m.C), I(m.D)); break;
                case Kind.UInt4: w.WriteUInt4((uint)m.A, (uint)m.B, (uint)m.C, (uint)m.D); break;
                case Kind.Bool2: w.WriteBool2((m.A & 1) != 0, (m.A & 2) != 0); break;
                case Kind.Bool3: w.WriteBool3((m.A & 1) != 0, (m.A & 2) != 0, (m.A & 4) != 0); break;
                case Kind.Bool4: w.WriteBool4((m.A & 1) != 0, (m.A & 2) != 0, (m.A & 4) != 0, (m.A & 8) != 0); break;
                case Kind.Quaternion: w.WriteQuaternion(F(m.A), F(m.B), F(m.C), F(m.D)); break;
                case Kind.Bezier: w.WriteBezier4x3(m.Floats); break;
                case Kind.Color: w.WriteColor(F(m.A), F(m.B), F(m.C), F(m.D)); break;
                case Kind.Color32: w.WriteColor32((byte)m.A, (byte)m.B, (byte)m.C, (byte)m.D); break;
                case Kind.Hash128: w.WriteHash128((uint)m.A, (uint)m.B, (uint)m.C, (uint)m.D); break;
                case Kind.Entity: w.WriteEntity(m.Entities[0].Index, m.Entities[0].Version); break;
                case Kind.EntityIgnoreVersion: w.WriteEntityIgnoreVersion(m.Entities[0].Index); break;
                case Kind.NetRef: w.WriteNetRef(m.Ref); break;
                case Kind.EntityArray:
                {
                    var indices = new int[m.Count];
                    var versions = new int[m.Count];
                    for (int i = 0; i < m.Count; i++) (indices[i], versions[i]) = m.Entities[i];
                    w.WriteEntityArray(indices, versions, m.Count);
                    break;
                }
                case Kind.IntArray: w.WriteIntArray(m.Ints, m.Count); break;
                case Kind.UShortArray: w.WriteUShortArray(m.UShorts, m.Count); break;
                case Kind.ByteArray: w.WriteByteArray(m.Bytes, m.Count, m.Stride); break;
                case Kind.Float2Array: w.WriteFloat2Array(m.Floats, m.Count); break;
                case Kind.Float4Array: w.WriteFloat4Array(m.Floats, m.Count); break;
                case Kind.Int2Array: w.WriteInt2Array(m.Ints, m.Count); break;
                case Kind.CountedBytes:
                    w.WriteCount(m.Count);
                    for (int i = 0; i < m.Count; i++) w.WriteByte(m.Bytes[i]);
                    break;
                case Kind.Skip: w.WriteByteArray(m.Bytes, m.Count); break;
                case Kind.Block:
                case Kind.SkippedBlock:
                {
                    BlockToken block = w.Begin();
                    int before = w.Target.Length;
                    foreach (Member child in m.Children) Write(w, child);
                    m.WrittenSize = w.Target.Length - before;
                    bool nonEmpty = w.End(block, out int size);
                    Assert.Equal(m.WrittenSize, size, "block size");
                    Assert.Equal(size != 0, nonEmpty);
                    break;
                }
                default: throw new InvalidOperationException("unknown member " + m.Kind);
            }
        }

        // ---------------------------------------------------------------- read and verify

        internal void ReadAndVerify(CanonicalReader r)
        {
            for (int i = 0; i < Members.Count; i++) Verify(r, Members[i], "member " + i);
        }

        private static void Verify(CanonicalReader r, Member m, string where)
        {
            where = where + " (" + m.Kind + ")";
            switch (m.Kind)
            {
                case Kind.Bool: Assert.Equal(m.A != 0, r.ReadBool(), where); break;
                case Kind.Byte: Assert.Equal((byte)m.A, r.ReadByte(), where); break;
                case Kind.SByte: Assert.Equal(unchecked((sbyte)m.A), r.ReadSByte(), where); break;
                case Kind.Short: Assert.Equal(unchecked((short)m.A), r.ReadShort(), where); break;
                case Kind.UShort: Assert.Equal((ushort)m.A, r.ReadUShort(), where); break;
                case Kind.Int: Assert.Equal(unchecked((int)m.A), r.ReadInt(), where); break;
                case Kind.UInt: Assert.Equal((uint)m.A, r.ReadUInt(), where); break;
                case Kind.Long: Assert.Equal(unchecked((long)m.A), r.ReadLong(), where); break;
                case Kind.ULong: Assert.Equal(m.A, r.ReadULong(), where); break;
                case Kind.Char: Assert.Equal((char)m.A, r.ReadChar(), where); break;
                case Kind.Float: Assert.Equal((uint)m.A, Bits(r.ReadFloat()), where); break;
                case Kind.Double: Assert.Equal(m.A, Bits(r.ReadDouble()), where); break;
                case Kind.String: Assert.Equal(m.Text ?? string.Empty, r.ReadString(), where); break;
                case Kind.Float2:
                {
                    r.ReadFloat2(out float x, out float y);
                    AssertBits(where, m, x, y);
                    break;
                }
                case Kind.Float3:
                {
                    r.ReadFloat3(out float x, out float y, out float z);
                    AssertBits(where, m, x, y, z);
                    break;
                }
                case Kind.Float4:
                {
                    r.ReadFloat4(out float x, out float y, out float z, out float v);
                    AssertBits(where, m, x, y, z, v);
                    break;
                }
                case Kind.Quaternion:
                {
                    r.ReadQuaternion(out float x, out float y, out float z, out float v);
                    AssertBits(where, m, x, y, z, v);
                    break;
                }
                case Kind.Color:
                {
                    r.ReadColor(out float x, out float y, out float z, out float v);
                    AssertBits(where, m, x, y, z, v);
                    break;
                }
                case Kind.Int2:
                {
                    r.ReadInt2(out int x, out int y);
                    Assert.Equal(I(m.A), x, where); Assert.Equal(I(m.B), y, where);
                    break;
                }
                case Kind.Int3:
                {
                    r.ReadInt3(out int x, out int y, out int z);
                    Assert.Equal(I(m.A), x, where); Assert.Equal(I(m.B), y, where); Assert.Equal(I(m.C), z, where);
                    break;
                }
                case Kind.Int4:
                {
                    r.ReadInt4(out int x, out int y, out int z, out int v);
                    Assert.Equal(I(m.A), x, where); Assert.Equal(I(m.B), y, where); Assert.Equal(I(m.C), z, where); Assert.Equal(I(m.D), v, where);
                    break;
                }
                case Kind.UInt4:
                case Kind.Hash128:
                {
                    uint x, y, z, v;
                    if (m.Kind == Kind.UInt4) r.ReadUInt4(out x, out y, out z, out v);
                    else r.ReadHash128(out x, out y, out z, out v);
                    Assert.Equal((uint)m.A, x, where); Assert.Equal((uint)m.B, y, where); Assert.Equal((uint)m.C, z, where); Assert.Equal((uint)m.D, v, where);
                    break;
                }
                case Kind.Bool2:
                {
                    r.ReadBool2(out bool x, out bool y);
                    Assert.Equal((int)(m.A & 3), (x ? 1 : 0) | (y ? 2 : 0), where);
                    break;
                }
                case Kind.Bool3:
                {
                    r.ReadBool3(out bool x, out bool y, out bool z);
                    Assert.Equal((int)(m.A & 7), (x ? 1 : 0) | (y ? 2 : 0) | (z ? 4 : 0), where);
                    break;
                }
                case Kind.Bool4:
                {
                    r.ReadBool4(out bool x, out bool y, out bool z, out bool v);
                    Assert.Equal((int)(m.A & 15), (x ? 1 : 0) | (y ? 2 : 0) | (z ? 4 : 0) | (v ? 8 : 0), where);
                    break;
                }
                case Kind.Bezier:
                {
                    var back = new float[12];
                    r.ReadBezier4x3(back);
                    for (int i = 0; i < 12; i++) Assert.Equal(Bits(m.Floats[i]), Bits(back[i]), where + " float " + i);
                    break;
                }
                case Kind.Color32:
                {
                    r.ReadColor32(out byte x, out byte y, out byte z, out byte v);
                    Assert.Equal((byte)m.A, x, where); Assert.Equal((byte)m.B, y, where); Assert.Equal((byte)m.C, z, where); Assert.Equal((byte)m.D, v, where);
                    break;
                }
                case Kind.Entity:
                {
                    Assert.True(r.ReadEntity(out int index, out int version), where + ": resolves");
                    (int Index, int Version) expected = Expected(m.Entities[0]);
                    Assert.Equal(expected.Index, index, where);
                    Assert.Equal(expected.Version, version, where);
                    break;
                }
                case Kind.EntityIgnoreVersion:
                {
                    Assert.True(r.ReadEntityIgnoreVersion(out int index), where + ": resolves");
                    Assert.Equal(m.Entities[0].Index, index, where);
                    break;
                }
                case Kind.NetRef: Assert.Equal(m.Ref, r.ReadNetRef(), where); break;
                case Kind.EntityArray:
                {
                    var indices = new int[m.Count];
                    var versions = new int[m.Count];
                    Assert.True(r.ReadEntityArray(indices, versions, m.Count), where + ": resolves");
                    for (int i = 0; i < m.Count; i++)
                    {
                        (int Index, int Version) expected = Expected(m.Entities[i]);
                        Assert.Equal(expected.Index, indices[i], where + " element " + i);
                        Assert.Equal(expected.Version, versions[i], where + " element " + i);
                    }
                    break;
                }
                case Kind.IntArray:
                {
                    var back = new int[m.Count];
                    r.ReadIntArray(back, m.Count);
                    for (int i = 0; i < m.Count; i++) Assert.Equal(m.Ints[i], back[i], where + " element " + i);
                    break;
                }
                case Kind.Int2Array:
                {
                    var back = new int[2 * m.Count];
                    r.ReadInt2Array(back, m.Count);
                    for (int i = 0; i < 2 * m.Count; i++) Assert.Equal(m.Ints[i], back[i], where + " element " + i);
                    break;
                }
                case Kind.UShortArray:
                {
                    var back = new ushort[m.Count];
                    r.ReadUShortArray(back, m.Count);
                    for (int i = 0; i < m.Count; i++) Assert.Equal(m.UShorts[i], back[i], where + " element " + i);
                    break;
                }
                case Kind.ByteArray:
                {
                    var back = new byte[m.Count];
                    r.ReadByteArray(back, m.Count, m.Stride);
                    for (int i = 0; i < m.Count; i++) Assert.Equal(m.Bytes[i], back[i], where + " element " + i);
                    break;
                }
                case Kind.Float2Array:
                case Kind.Float4Array:
                {
                    int per = m.Kind == Kind.Float2Array ? 2 : 4;
                    var back = new float[per * m.Count];
                    if (per == 2) r.ReadFloat2Array(back, m.Count);
                    else r.ReadFloat4Array(back, m.Count);
                    for (int i = 0; i < back.Length; i++) Assert.Equal(Bits(m.Floats[i]), Bits(back[i]), where + " element " + i);
                    break;
                }
                case Kind.CountedBytes:
                {
                    int count = r.ReadCount();
                    Assert.Equal(m.Count, count, where);
                    for (int i = 0; i < count; i++) Assert.Equal(m.Bytes[i], r.ReadByte(), where + " element " + i);
                    break;
                }
                case Kind.Skip:
                    r.Skip(m.Count);
                    break;
                case Kind.Block:
                {
                    BlockToken block = r.Begin(out int size);
                    Assert.Equal(m.WrittenSize, size, where + ": block size");
                    for (int i = 0; i < m.Children.Count; i++) Verify(r, m.Children[i], where + "/" + i);
                    Assert.True(r.IsAtEnd, where + ": the children consume the block");
                    Assert.True(r.End(block), where + ": consumed");
                    break;
                }
                case Kind.SkippedBlock:
                {
                    BlockToken block = r.Begin(out int size);
                    Assert.Equal(m.WrittenSize, size, where + ": block size");
                    Assert.Equal(size == 0, r.End(block), where + ": End reports whether anything was skipped");
                    break;
                }
                default: throw new InvalidOperationException("unknown member " + m.Kind);
            }
        }

        // ---------------------------------------------------------------- replay without verification (fuzzing)

        /// <summary>Runs the read calls of the script against whatever bytes the reader holds, ignoring the values.</summary>
        internal void Replay(CanonicalReader r)
        {
            foreach (Member m in Members) Replay(r, m);
        }

        private static void Replay(CanonicalReader r, Member m)
        {
            switch (m.Kind)
            {
                case Kind.Bool: r.ReadBool(); break;
                case Kind.Byte: r.ReadByte(); break;
                case Kind.SByte: r.ReadSByte(); break;
                case Kind.Short: r.ReadShort(); break;
                case Kind.UShort: r.ReadUShort(); break;
                case Kind.Int: r.ReadInt(); break;
                case Kind.UInt: r.ReadUInt(); break;
                case Kind.Long: r.ReadLong(); break;
                case Kind.ULong: r.ReadULong(); break;
                case Kind.Char: r.ReadChar(); break;
                case Kind.Float: r.ReadFloat(); break;
                case Kind.Double: r.ReadDouble(); break;
                case Kind.String: r.ReadString(); break;
                case Kind.Float2: r.ReadFloat2(out _, out _); break;
                case Kind.Float3: r.ReadFloat3(out _, out _, out _); break;
                case Kind.Float4: r.ReadFloat4(out _, out _, out _, out _); break;
                case Kind.Int2: r.ReadInt2(out _, out _); break;
                case Kind.Int3: r.ReadInt3(out _, out _, out _); break;
                case Kind.Int4: r.ReadInt4(out _, out _, out _, out _); break;
                case Kind.UInt4: r.ReadUInt4(out _, out _, out _, out _); break;
                case Kind.Bool2: r.ReadBool2(out _, out _); break;
                case Kind.Bool3: r.ReadBool3(out _, out _, out _); break;
                case Kind.Bool4: r.ReadBool4(out _, out _, out _, out _); break;
                case Kind.Quaternion: r.ReadQuaternion(out _, out _, out _, out _); break;
                case Kind.Bezier: r.ReadBezier4x3(new float[12]); break;
                case Kind.Color: r.ReadColor(out _, out _, out _, out _); break;
                case Kind.Color32: r.ReadColor32(out _, out _, out _, out _); break;
                case Kind.Hash128: r.ReadHash128(out _, out _, out _, out _); break;
                case Kind.Entity:
                case Kind.EntityIgnoreVersion: r.ReadEntity(out _, out _); break;
                case Kind.NetRef: r.ReadNetRef(); break;
                case Kind.EntityArray: r.ReadEntityArray(new int[m.Count], new int[m.Count], m.Count); break;
                case Kind.IntArray: r.ReadIntArray(new int[m.Count], m.Count); break;
                case Kind.Int2Array: r.ReadInt2Array(new int[2 * m.Count], m.Count); break;
                case Kind.UShortArray: r.ReadUShortArray(new ushort[m.Count], m.Count); break;
                case Kind.ByteArray: r.ReadByteArray(new byte[m.Count], m.Count, m.Stride); break;
                case Kind.Float2Array: r.ReadFloat2Array(new float[2 * m.Count], m.Count); break;
                case Kind.Float4Array: r.ReadFloat4Array(new float[4 * m.Count], m.Count); break;
                case Kind.CountedBytes:
                {
                    int count = r.ReadCount();
                    r.ReadByteArray(new byte[count], count);
                    break;
                }
                case Kind.Skip: r.Skip(m.Count); break;
                case Kind.Block:
                {
                    BlockToken block = r.Begin();
                    foreach (Member child in m.Children) Replay(r, child);
                    r.End(block);
                    break;
                }
                case Kind.SkippedBlock: r.End(r.Begin()); break;
                default: throw new InvalidOperationException("unknown member " + m.Kind);
            }
        }

        // ---------------------------------------------------------------- generation

        private static Kind[] BuildLeaves()
        {
            var leaves = new List<Kind>();
            foreach (Kind kind in (Kind[])Enum.GetValues(typeof(Kind)))
            {
                if (kind != Kind.Block && kind != Kind.SkippedBlock) leaves.Add(kind);
            }
            return leaves.ToArray();
        }

        private static Member RandomMember(DeterministicRandom random, int depth)
        {
            if (depth > 0 && random.Next(12) == 0)
            {
                var block = new Member { Kind = random.NextBool() ? Kind.Block : Kind.SkippedBlock, Children = new List<Member>() };
                int n = random.Next(6);
                for (int i = 0; i < n; i++) block.Children.Add(RandomMember(random, depth - 1));
                return block;
            }
            Kind kind = Leaves[random.Next(Leaves.Length)];
            var m = new Member { Kind = kind, A = Value(random), B = Value(random), C = Value(random), D = Value(random) };
            switch (kind)
            {
                case Kind.Float:
                case Kind.Float2:
                case Kind.Float3:
                case Kind.Float4:
                case Kind.Quaternion:
                case Kind.Color:
                    m.A = FloatBits(random); m.B = FloatBits(random); m.C = FloatBits(random); m.D = FloatBits(random);
                    break;
                case Kind.Double:
                    m.A = DoubleBits(random);
                    break;
                case Kind.Char:
                    m.A = RandomChar(random);
                    break;
                case Kind.String:
                    m.Text = RandomString(random);
                    break;
                case Kind.Bezier:
                    m.Floats = new float[12];
                    for (int i = 0; i < 12; i++) m.Floats[i] = FromBits((uint)FloatBits(random));
                    break;
                case Kind.Entity:
                case Kind.EntityIgnoreVersion:
                    m.Entities = new[] { RandomEntity(random, kind == Kind.EntityIgnoreVersion) };
                    break;
                case Kind.NetRef:
                    m.Ref = NetRefEncodingTests.RandomNetRef(random);
                    break;
                case Kind.EntityArray:
                    m.Count = random.Next(6);
                    m.Entities = new (int, int)[m.Count];
                    for (int i = 0; i < m.Count; i++) m.Entities[i] = RandomEntity(random, false);
                    break;
                case Kind.IntArray:
                    m.Count = random.Next(9);
                    m.Ints = RandomInts(random, m.Count + random.Next(3));
                    break;
                case Kind.Int2Array:
                    m.Count = random.Next(5);
                    m.Ints = RandomInts(random, 2 * m.Count + random.Next(3));
                    break;
                case Kind.UShortArray:
                    m.Count = random.Next(9);
                    m.UShorts = new ushort[m.Count + random.Next(3)];
                    for (int i = 0; i < m.UShorts.Length; i++) m.UShorts[i] = (ushort)random.Next(65536);
                    break;
                case Kind.ByteArray:
                case Kind.CountedBytes:
                case Kind.Skip:
                    m.Count = random.Next(kind == Kind.ByteArray ? 40 : 9);
                    m.Stride = kind == Kind.ByteArray ? random.Next(0, 9) : 1;
                    m.Bytes = new byte[m.Count + random.Next(3)];
                    random.NextBytes(m.Bytes, 0, m.Bytes.Length);
                    break;
                case Kind.Float2Array:
                case Kind.Float4Array:
                {
                    m.Count = random.Next(5);
                    int per = kind == Kind.Float2Array ? 2 : 4;
                    m.Floats = new float[per * m.Count + random.Next(3)];
                    for (int i = 0; i < m.Floats.Length; i++) m.Floats[i] = FromBits((uint)FloatBits(random));
                    break;
                }
            }
            return m;
        }

        private static (int, int) RandomEntity(DeterministicRandom random, bool boundOnly)
        {
            if (!boundOnly && random.Next(5) == 0) return (0, 0);
            var e = KnownEntities[random.Next(KnownEntities.Length)];
            return (e.Index, e.Version);
        }

        private static (int Index, int Version) Expected((int Index, int Version) written) => written;

        private static ulong Value(DeterministicRandom random)
        {
            switch (random.Next(6))
            {
                case 0: return 0;
                case 1: return ulong.MaxValue;
                case 2: return (ulong)random.Next(256);
                case 3: return 0x8000000000000000UL + (uint)random.Next(4);
                case 4: return 0x80000000UL;
                default: return random.NextULong();
            }
        }

        private static ulong FloatBits(DeterministicRandom random)
        {
            uint[] specials = { 0x00000000, 0x80000000, 0x7F800000, 0xFF800000, 0x7FC00000, 0x7FC00001, 0x7F800001, 0xFFFFFFFF, 0x00000001, 0x3F800000, 0x7F7FFFFF };
            return random.Next(3) == 0 ? specials[random.Next(specials.Length)] : random.NextUInt();
        }

        private static ulong DoubleBits(DeterministicRandom random)
        {
            ulong[] specials = { 0, 0x8000000000000000UL, 0x7FF0000000000000UL, 0x7FF8000000000123UL, 0x7FF0000000000001UL, 1, ulong.MaxValue };
            return random.Next(3) == 0 ? specials[random.Next(specials.Length)] : random.NextULong();
        }

        private static ulong RandomChar(DeterministicRandom random)
        {
            int[] specials = { 0, 0x7F, 0x80, 0x7FF, 0x800, 0xD800, 0xDBFF, 0xDC00, 0xDFFF, 0xFFFE, 0xFFFF, 'A' };
            return (ulong)(random.Next(3) == 0 ? specials[random.Next(specials.Length)] : random.Next(65536));
        }

        private static string RandomString(DeterministicRandom random)
        {
            switch (random.Next(5))
            {
                case 0: return null;
                case 1: return string.Empty;
                default:
                {
                    var chars = new char[random.Next(40)];
                    for (int i = 0; i < chars.Length; i++) chars[i] = (char)RandomChar(random);
                    return new string(chars);
                }
            }
        }

        private static int[] RandomInts(DeterministicRandom random, int length)
        {
            var values = new int[length];
            for (int i = 0; i < length; i++) values[i] = unchecked((int)Value(random));
            return values;
        }

        private static float F(ulong bits) => FromBits((uint)bits);

        private static int I(ulong bits) => unchecked((int)bits);

        private static void AssertBits(string where, Member m, params float[] actual)
        {
            ulong[] expected = { m.A, m.B, m.C, m.D };
            for (int i = 0; i < actual.Length; i++) Assert.Equal((uint)expected[i], Bits(actual[i]), where + " component " + i);
        }
    }
}
