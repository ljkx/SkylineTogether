using System.Collections.Generic;
using System.Linq;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Modding
{
    /// <summary>
    /// The IL token walk behind the runtime-prefab and pipeline detection, and the version check that turns a built-in
    /// adapter off when its mod changed (docs/design/MOD-COMPATIBILITY.md §5-6).
    /// </summary>
    public sealed class IlScannerAndAdapterCheckTests
    {
        private static List<IlToken> Scan(byte[] il, out bool ok)
        {
            var tokens = new List<IlToken>();
            ok = IlScanner.Scan(il, tokens);
            return tokens;
        }

        [Test]
        public void CallsFieldsTypesAndTokensAreFound()
        {
            byte[] il =
            {
                0x02,                         // ldarg.0
                0x7B, 0x01, 0x00, 0x00, 0x04, // ldfld 0x04000001
                0x28, 0x02, 0x00, 0x00, 0x0A, // call 0x0A000002
                0x8C, 0x03, 0x00, 0x00, 0x02, // box 0x02000003
                0xD0, 0x04, 0x00, 0x00, 0x1B, // ldtoken 0x1B000004
                0xFE, 0x06, 0x05, 0x00, 0x00, 0x06, // ldftn 0x06000005
                0x73, 0x06, 0x00, 0x00, 0x0A, // newobj 0x0A000006
                0x2A,                         // ret
            };
            List<IlToken> tokens = Scan(il, out bool ok);
            Assert.True(ok);
            Assert.SequenceEqual(new[] { IlOperand.Field, IlOperand.Method, IlOperand.Type, IlOperand.Token, IlOperand.Method, IlOperand.Method }, tokens.Select(t => t.Kind));
            Assert.SequenceEqual(new[] { 0x04000001, 0x0A000002, 0x02000003, 0x1B000004, 0x06000005, 0x0A000006 }, tokens.Select(t => t.Token));
        }

        [Test]
        public void OperandBytesAreNeverReadAsOpcodes()
        {
            byte[] il =
            {
                0x21, 0x28, 0x28, 0x28, 0x28, 0x28, 0x28, 0x28, 0x28, // ldc.i8 with call-like bytes
                0x20, 0x6F, 0x6F, 0x6F, 0x6F,                         // ldc.i4
                0x72, 0x01, 0x00, 0x00, 0x70,                         // ldstr (not a member token)
                0x45, 0x02, 0x00, 0x00, 0x00, 0x28, 0, 0, 0, 0x28, 0, 0, 0, // switch with 2 targets
                0xFE, 0x0C, 0x28, 0x00,                               // ldloc (2-byte index)
                0x2B, 0x28,                                           // br.s
                0x2A,
            };
            List<IlToken> tokens = Scan(il, out bool ok);
            Assert.True(ok);
            Assert.Equal(0, tokens.Count);
        }

        [Test]
        public void MalformedBodiesStopTheWalk()
        {
            Scan(new byte[] { 0x28, 0x01, 0x00 }, out bool ok);
            Assert.False(ok, "truncated operand");
            List<IlToken> tokens = Scan(new byte[] { 0x28, 0x01, 0x00, 0x00, 0x0A, 0x24 }, out ok);
            Assert.False(ok, "0x24 is no opcode");
            Assert.Equal(1, tokens.Count, "what was read before is kept");
            Scan(new byte[] { 0xFE }, out ok);
            Assert.False(ok);
            Scan(new byte[] { 0xFE, 0x08 }, out ok);
            Assert.False(ok);
            Scan(new byte[] { 0x45, 0xFF, 0xFF, 0xFF, 0x7F }, out ok);
            Assert.False(ok, "a switch larger than the body");
            Assert.True(IlScanner.Scan(null, null));
            Assert.True(IlScanner.Scan(new byte[] { 0x00, 0x2A }, null));
        }

        [Test]
        public void AHealthyAdapterStaysOn()
        {
            var check = new AdapterCheck();
            check.Expect("type", "Traffic.Components.DataTemp", true);
            check.Expect("system", "Traffic.Tools.PriorityToolSystem", true);
            Assert.True(check.Healthy);
            Assert.Equal(2, check.Checked);
            Assert.Null(check.Describe("Traffic", "1.0"));
            Assert.True(new AdapterCheck().Healthy, "an adapter that names nothing");
        }

        [Test]
        public void AnyMissingNameDisablesTheAdapterWithOneLine()
        {
            var check = new AdapterCheck();
            check.Expect("type", "Mod.A", true);
            check.Expect("type", "Mod.B", false);
            check.Expect("system", "Mod.C", false);
            check.Expect("setting", "Mod.Settings.D", false);
            check.Expect("tool", "Mod.E", false);
            Assert.False(check.Healthy);
            Assert.SequenceEqual(new[] { "type Mod.B", "system Mod.C", "setting Mod.Settings.D", "tool Mod.E" }, check.Missing);
            string line = check.Describe("Some Mod", "2.1.0");
            Assert.Equal("Some Mod 2.1.0: adapter disabled, 4 of 5 names not found in this version (type Mod.B, system Mod.C, setting Mod.Settings.D and 1 more); the generic analysis applies instead", line);
            Assert.DoesNotContain("\n", line);
        }
    }
}
