using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Modding
{
    /// <summary>Role permissions on the mod channel: viewers receive, and send only kinds the mod declared viewer-allowed.</summary>
    public sealed class ModChannelPermissionsTests
    {
        [Test]
        public void ViewerMessageKindsAreDeclaredAsKindsOrStar()
        {
            var d = new ModDeclarations();
            d.AddLine("viewer-message: 3", "Declarations[0]");
            d.AddLine("viewer-message: 0x10", "Declarations[1]");
            d.Add("Viewer Message", " 200 ", "assembly attribute");
            d.AddLine("viewer-message: 256", "Declarations[2]");
            d.AddLine("viewer-message", "Declarations[3]");
            d.AddLine("viewer-message: MyMod.Messages", "Declarations[4]");
            Assert.Equal(3, d.Items.Count, "three valid kinds");
            Assert.Equal("16", d.Items[1].Target, "hex kinds are kept in decimal");
            Assert.Equal(3, d.Problems.Count, d.Problems.Count > 0 ? d.Problems[0] : "");
            Assert.Contains("message kind", d.Problems[0]);
            ViewerMessageKinds kinds = ViewerMessageKinds.From(d);
            Assert.True(kinds.Allows(3));
            Assert.True(kinds.Allows(16));
            Assert.True(kinds.Allows(200));
            Assert.False(kinds.Allows(4));
            Assert.False(kinds.All);
            Assert.True(kinds.Any);
        }

        [Test]
        public void BuildersSendAnythingAndViewersOnlyTheirKinds()
        {
            var d = new ModDeclarations();
            d.AddLine("viewer-message: 7", "Declarations[0]");
            ViewerMessageKinds kinds = ViewerMessageKinds.From(d);
            byte[] request = { 7, 1, 2 };
            byte[] change = { 9, 1, 2 };
            foreach (SessionRole role in new[] { SessionRole.Host, SessionRole.CoHost, SessionRole.Builder })
            {
                Assert.True(ModChannelPermissions.MaySend(role, change, ViewerMessageKinds.None), role + " sends without declarations");
            }
            Assert.True(ModChannelPermissions.MaySend(SessionRole.Viewer, request, kinds), "a declared kind");
            Assert.False(ModChannelPermissions.MaySend(SessionRole.Viewer, change, kinds), "an undeclared kind");
            Assert.False(ModChannelPermissions.MaySend(SessionRole.Viewer, new byte[0], kinds), "an empty message has no kind");
            Assert.False(ModChannelPermissions.MaySend(SessionRole.Viewer, request, ViewerMessageKinds.None), "a mod that declares nothing");
            Assert.False(ModChannelPermissions.MaySend(SessionRole.Viewer, request, null));
        }

        [Test]
        public void StarLetsViewersSendEveryMessage()
        {
            var d = new ModDeclarations();
            d.AddLine("viewer-message: *", "Declarations[0]");
            ViewerMessageKinds kinds = ViewerMessageKinds.From(d);
            Assert.True(kinds.All);
            Assert.True(ModChannelPermissions.MaySend(SessionRole.Viewer, new byte[0], kinds));
            Assert.True(ModChannelPermissions.MaySend(SessionRole.Viewer, new byte[] { 255 }, kinds));
            Assert.False(ViewerMessageKinds.From(new ModDeclarations()).Any, "no declaration, no kind");
        }
    }
}
