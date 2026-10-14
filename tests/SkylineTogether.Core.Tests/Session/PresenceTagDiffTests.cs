using System.Collections.Generic;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>Name tags are pushed to the UI only when they change noticeably.</summary>
    public sealed class PresenceTagDiffTests
    {
        private static PresenceTag Tag(byte peer, float x, float y, bool on = true, string activity = "") =>
            new PresenceTag { Peer = peer, Name = "P" + peer, Color = "#fff", ScreenX = x, ScreenY = y, OnScreen = on, Activity = activity };

        [Test]
        public void SmallMovesAreNotPushed()
        {
            var shown = new List<PresenceTag> { Tag(1, 0.5f, 0.5f) };
            Assert.False(PresenceTagDiff.Differs(shown, new List<PresenceTag> { Tag(1, 0.5005f, 0.4995f) }));
            Assert.True(PresenceTagDiff.Differs(shown, new List<PresenceTag> { Tag(1, 0.503f, 0.5f) }));
            Assert.True(PresenceTagDiff.Differs(shown, new List<PresenceTag> { Tag(1, 0.5f, 0.497f) }));
        }

        [Test]
        public void TextVisibilityAndMembershipArePushed()
        {
            var shown = new List<PresenceTag> { Tag(1, 0.5f, 0.5f) };
            Assert.True(PresenceTagDiff.Differs(shown, new List<PresenceTag> { Tag(1, 0.5f, 0.5f, activity: "building Small Road") }));
            Assert.True(PresenceTagDiff.Differs(shown, new List<PresenceTag> { Tag(1, 0.5f, 0.5f, on: false) }));
            Assert.True(PresenceTagDiff.Differs(shown, new List<PresenceTag>()));
            Assert.True(PresenceTagDiff.Differs(shown, new List<PresenceTag> { Tag(2, 0.5f, 0.5f) }));
            Assert.True(PresenceTagDiff.Differs(null, shown));
            Assert.False(PresenceTagDiff.Differs(null, new List<PresenceTag>()));
        }

        [Test]
        public void HiddenTagsIgnoreTheirPosition()
        {
            var shown = new List<PresenceTag> { Tag(1, 0.1f, 0.1f, on: false) };
            Assert.False(PresenceTagDiff.Differs(shown, new List<PresenceTag> { Tag(1, 0.9f, 0.9f, on: false) }));
        }
    }
}
