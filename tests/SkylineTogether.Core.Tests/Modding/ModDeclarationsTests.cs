using System.Linq;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Modding
{
    /// <summary>Parsing of the integration API's declarations (docs/MOD-INTEGRATION.md): forgiving, never throwing.</summary>
    public sealed class ModDeclarationsTests
    {
        [Test]
        public void RolesParseInTheirWrittenForms()
        {
            Assert.True(ModDeclarations.TryParseRole("host-only", out DeclarationRole r));
            Assert.Equal(DeclarationRole.HostOnly, r);
            Assert.True(ModDeclarations.TryParseRole(" Host_Only ", out r));
            Assert.Equal(DeclarationRole.HostOnly, r);
            Assert.True(ModDeclarations.TryParseRole("never replicate", out r));
            Assert.Equal(DeclarationRole.NeverReplicate, r);
            Assert.True(ModDeclarations.TryParseRole("PIPELINE", out r));
            Assert.Equal(DeclarationRole.Pipeline, r);
            Assert.False(ModDeclarations.TryParseRole("hostonly", out _));
            Assert.False(ModDeclarations.TryParseRole("", out _));
            Assert.False(ModDeclarations.TryParseRole(null, out _));
            foreach (DeclarationRole role in System.Enum.GetValues(typeof(DeclarationRole)))
            {
                Assert.True(ModDeclarations.TryParseRole(ModDeclarations.RoleName(role), out DeclarationRole back), role.ToString());
                Assert.Equal(role, back);
            }
        }

        [Test]
        public void LinesSplitAtTheFirstColon()
        {
            var d = new ModDeclarations();
            d.AddLine("host-only: MyMod.Systems.GrowthSystem", "Declarations[0]");
            d.AddLine("host-setting:MyMod.Setting.Rate", "Declarations[1]");
            d.AddLine("client-local", "Declarations[2]");
            d.AddLine("client-local: ignored target", "Declarations[3]");
            Assert.Equal(0, d.Problems.Count, string.Join(" | ", d.Problems));
            Assert.Equal(3, d.Items.Count, "the second client-local is the same declaration");
            Assert.Equal("MyMod.Systems.GrowthSystem", d.Items[0].Target);
            Assert.Equal("MyMod.Setting.Rate", d.Items[1].Target);
            Assert.Equal("", d.Items[2].Target);
            Assert.Equal("host-only: MyMod.Systems.GrowthSystem", d.Items[0].ToString());
            Assert.True(d.Has(DeclarationRole.ClientLocal));
            Assert.Equal(1, d.With(DeclarationRole.HostSetting).Count());
        }

        [Test]
        public void BadDeclarationsBecomeProblemsNotExceptions()
        {
            var d = new ModDeclarations();
            Assert.DoesNotThrow(() =>
            {
                d.AddLine("", "Declarations[0]");
                d.AddLine(null, "Declarations[1]");
                d.AddLine("teleport: MyMod.X", "Declarations[2]");
                d.AddLine("host-only", "Declarations[3]");
                d.AddLine("host-only:   ", "Declarations[4]");
                d.Add("replicate", new string('x', ModDeclarations.MaxTargetChars + 1), "attribute on X");
                d.Add(null, "MyMod.X", "attribute on Y");
            });
            Assert.Equal(0, d.Items.Count);
            Assert.Equal(7, d.Problems.Count, string.Join(" | ", d.Problems));
            Assert.Contains("unknown role \"teleport\"", d.Problems[2]);
            Assert.Contains("needs a target", d.Problems[3]);
            Assert.Contains("longer than", d.Problems[5]);
        }

        [Test]
        public void ARunawayListIsCut()
        {
            var d = new ModDeclarations();
            for (int i = 0; i < ModDeclarations.MaxDeclarations + 10; i++) d.Add("replicate", "MyMod.T" + i, "attribute");
            Assert.Equal(ModDeclarations.MaxDeclarations, d.Items.Count);
            Assert.Equal(1, d.Problems.Count);
        }

        [Test]
        public void ModLevelRolesTakeNoTarget()
        {
            Assert.True(ModDeclarations.IsModLevel(DeclarationRole.ClientLocal));
            Assert.True(ModDeclarations.IsModLevel(DeclarationRole.World));
            Assert.False(ModDeclarations.IsModLevel(DeclarationRole.HostOnly));
            Assert.True(new ModDeclarations().IsEmpty);
            Assert.False(new ModDeclarations { HasIntegrationClass = true }.IsEmpty);
        }
    }
}
