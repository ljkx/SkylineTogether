using System.Collections.Generic;

namespace SkylineTogether.Core.Modding
{
    /// <summary>What an IL token operand refers to.</summary>
    public enum IlOperand : byte
    {
        /// <summary>A method (<c>call</c>, <c>callvirt</c>, <c>newobj</c>, <c>ldftn</c>, <c>ldvirtftn</c>, <c>jmp</c>).</summary>
        Method = 1,
        /// <summary>A field (<c>ldfld</c>, <c>stfld</c>, <c>ldsfld</c>, ...).</summary>
        Field = 2,
        /// <summary>A type (<c>box</c>, <c>initobj</c>, <c>newarr</c>, <c>constrained.</c>, ...).</summary>
        Type = 3,
        /// <summary>Any member (<c>ldtoken</c>).</summary>
        Token = 4,
    }

    /// <summary>One metadata token read from a method body.</summary>
    public struct IlToken
    {
        public IlOperand Kind;
        public int Token;

        public IlToken(IlOperand kind, int token)
        {
            Kind = kind;
            Token = token;
        }
    }

    /// <summary>
    /// Reads the metadata tokens of a method body's IL (ECMA-335 Partition III opcodes and operand sizes) without running
    /// anything: the mod scan resolves them to find what a mod calls (<c>PrefabSystem.AddPrefab</c>: runtime prefabs) and
    /// which types its tools use (<c>CreationDefinition</c>: the Temp pipeline). The walk follows the opcode lengths
    /// exactly, so operand bytes are never read as opcodes; an unknown opcode stops it (false), keeping what was read.
    /// Pure and thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class IlScanner
    {
        // operand sizes of the one-byte opcodes; -1 = no such opcode, -2 = switch (variable)
        private static readonly sbyte[] OneByte = BuildOneByte();

        // operand sizes of the 0xFE-prefixed opcodes (second byte 0x00 ... 0x1E); -1 = no such opcode
        private static readonly sbyte[] TwoByte =
        {
            0, 0, 0, 0, 0, 0, 4, 4, -1, 2, 2, 2, 2, 2, 2, 0, // FE00 arglist ... FE0F localloc
            -1, 0, 1, 0, 0, 4, 4, 0, 0, 1, 0, -1, 4, 0, 0,    // FE10 ... FE1E readonly.
        };

        private static sbyte[] BuildOneByte()
        {
            var size = new sbyte[256];
            for (int i = 0; i < size.Length; i++) size[i] = -1;
            void Set(int from, int to, sbyte n)
            {
                for (int i = from; i <= to; i++) size[i] = n;
            }
            Set(0x00, 0x0D, 0);  // nop, break, ldarg.0-3, ldloc.0-3, stloc.0-3
            Set(0x0E, 0x13, 1);  // ldarg.s ... stloc.s
            Set(0x14, 0x1E, 0);  // ldnull, ldc.i4.m1 ... ldc.i4.8
            Set(0x1F, 0x1F, 1);  // ldc.i4.s
            Set(0x20, 0x20, 4);  // ldc.i4
            Set(0x21, 0x21, 8);  // ldc.i8
            Set(0x22, 0x22, 4);  // ldc.r4
            Set(0x23, 0x23, 8);  // ldc.r8
            Set(0x25, 0x26, 0);  // dup, pop
            Set(0x27, 0x29, 4);  // jmp, call, calli
            Set(0x2A, 0x2A, 0);  // ret
            Set(0x2B, 0x37, 1);  // short branches
            Set(0x38, 0x44, 4);  // branches
            size[0x45] = -2;     // switch
            Set(0x46, 0x6E, 0);  // ldind, stind, arithmetic, conv
            Set(0x6F, 0x75, 4);  // callvirt, cpobj, ldobj, ldstr, newobj, castclass, isinst
            Set(0x76, 0x76, 0);  // conv.r.un
            Set(0x79, 0x79, 4);  // unbox
            Set(0x7A, 0x7A, 0);  // throw
            Set(0x7B, 0x81, 4);  // ldfld ... stsfld, stobj
            Set(0x82, 0x8B, 0);  // conv.ovf.*.un
            Set(0x8C, 0x8D, 4);  // box, newarr
            Set(0x8E, 0x8E, 0);  // ldlen
            Set(0x8F, 0x8F, 4);  // ldelema
            Set(0x90, 0xA2, 0);  // ldelem.*, stelem.*
            Set(0xA3, 0xA5, 4);  // ldelem, stelem, unbox.any
            Set(0xB3, 0xBA, 0);  // conv.ovf.*
            Set(0xC2, 0xC2, 4);  // refanyval
            Set(0xC3, 0xC3, 0);  // ckfinite
            Set(0xC6, 0xC6, 4);  // mkrefany
            Set(0xD0, 0xD0, 4);  // ldtoken
            Set(0xD1, 0xDC, 0);  // conv.u2 ... endfinally
            Set(0xDD, 0xDD, 4);  // leave
            Set(0xDE, 0xDE, 1);  // leave.s
            Set(0xDF, 0xE0, 0);  // stind.i, conv.u
            return size;
        }

        private static IlOperand KindOf(int opcode)
        {
            switch (opcode)
            {
                case 0x27: case 0x28: case 0x6F: case 0x73: case 0xFE06: case 0xFE07:
                    return IlOperand.Method;
                case 0x7B: case 0x7C: case 0x7D: case 0x7E: case 0x7F: case 0x80:
                    return IlOperand.Field;
                case 0x70: case 0x71: case 0x74: case 0x75: case 0x79: case 0x81: case 0x8C: case 0x8D: case 0x8F:
                case 0xA3: case 0xA4: case 0xA5: case 0xC2: case 0xC6: case 0xFE15: case 0xFE16: case 0xFE1C:
                    return IlOperand.Type;
                case 0xD0:
                    return IlOperand.Token;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Appends the member, field, type and <c>ldtoken</c> tokens of <paramref name="il"/> to <paramref name="output"/>.
        /// False when the body is malformed or uses an opcode this table does not know (the tokens before it are kept).
        /// </summary>
        public static bool Scan(byte[] il, List<IlToken> output)
        {
            if (il == null) return true;
            int pos = 0;
            while (pos < il.Length)
            {
                int opcode = il[pos++];
                int operand;
                if (opcode == 0xFE)
                {
                    if (pos >= il.Length) return false;
                    int second = il[pos++];
                    if (second >= TwoByte.Length || TwoByte[second] < 0) return false;
                    opcode = 0xFE00 | second;
                    operand = TwoByte[second];
                }
                else
                {
                    operand = OneByte[opcode];
                    if (operand == -1) return false;
                    if (operand == -2)
                    {
                        if (pos + 4 > il.Length) return false;
                        uint targets = (uint)(il[pos] | il[pos + 1] << 8 | il[pos + 2] << 16 | il[pos + 3] << 24);
                        if (targets > (uint)(il.Length - pos - 4) / 4) return false;
                        operand = 4 + (int)targets * 4;
                    }
                }
                if (pos + operand > il.Length) return false;
                IlOperand kind = KindOf(opcode);
                if (kind != 0 && operand == 4)
                {
                    output?.Add(new IlToken(kind, il[pos] | il[pos + 1] << 8 | il[pos + 2] << 16 | il[pos + 3] << 24));
                }
                pos += operand;
            }
            return true;
        }
    }
}
