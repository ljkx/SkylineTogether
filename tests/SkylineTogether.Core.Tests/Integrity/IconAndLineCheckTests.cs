using System.Collections.Generic;
using SkylineTogether.Core.Integrity;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Integrity
{
    public sealed class IconAndLineCheckTests
    {
        [Test]
        public void IconMemberIgnoresLocationFlagsButNotDescribingFlags()
        {
            ulong owner = SyncRef.ForNetId(5).NormalizedHash();
            ulong target = default(SyncRef).NormalizedHash();
            ulong a = IconSetDigest.Member(owner, 7, target, 100, 0x00);
            Assert.Equal(a, IconSetDigest.Member(owner, 7, target, 100, 0x24), "TargetLocation and CustomLocation are presentation");
            Assert.NotEqual(a, IconSetDigest.Member(owner, 7, target, 100, 0x10), "SecondaryLocation names another icon");
            Assert.NotEqual(a, IconSetDigest.Member(owner, 7, target, 50, 0x00));
            Assert.NotEqual(a, IconSetDigest.Member(owner, 8, target, 100, 0x00));
            Assert.NotEqual(a, IconSetDigest.Member(SyncRef.ForNetId(6).NormalizedHash(), 7, target, 100, 0x00));
        }

        [Test]
        public void IconDigestDoesNotDependOnOrder()
        {
            var x = new IconSetDigest.Accumulator();
            var y = new IconSetDigest.Accumulator();
            x.AddOwner();
            x.AddIcon(11);
            x.AddIcon(22);
            y.AddOwner();
            y.AddIcon(22);
            y.AddIcon(11);
            Assert.True(x.Matches(y.Owners, y.Icons, y.Sum));
        }

        [Test]
        public void ExactSetHashSeesEveryFieldButNotTheOrder()
        {
            var a = new SyncIcon { Prefab = 1, Priority = 100, X = 1f };
            var b = new SyncIcon { Prefab = 2, Priority = 100, Target = SyncRef.ForNetId(4) };
            ulong h = IconSetDigest.ExactSetHash(new[] { a, b });
            Assert.Equal(h, IconSetDigest.ExactSetHash(new[] { b, a }));
            var moved = a;
            moved.X = 2f;
            Assert.NotEqual(h, IconSetDigest.ExactSetHash(new[] { moved, b }));
            Assert.Equal(0UL, IconSetDigest.ExactSetHash(new SyncIcon[0]));
            Assert.NotEqual(0UL, IconSetDigest.ExactSetHash(new[] { a }));
        }

        private static LineStateDifference D(uint line, string field, string host, string client)
        {
            return new LineStateDifference { Line = new NetSlot(line), Field = field, HostValue = host, ClientValue = client };
        }

        [Test]
        public void LineIsResentOnlyWhenTheSameDifferenceIsSeenTwice()
        {
            var t = new LineCheckTracker();
            var o = new LineCheckTracker.Outcome();
            t.Observe(new[] { D(9, "Stops", "4", "3") }, 0, o);
            Assert.Equal(0, o.Resend.Count);
            Assert.Equal(1, o.Differing);
            t.Observe(new[] { D(9, "Stops", "4", "2") }, 10, o);
            Assert.Equal(0, o.Resend.Count, "the difference changed: still in flux");
            t.Observe(new[] { D(9, "Stops", "4", "2") }, 20, o);
            Assert.SequenceEqual(new uint[] { 9 }, o.Resend);
            Assert.Equal(1, o.Log.Count);
            Assert.Contains("Stops host 4 / here 2", o.Log[0]);
            t.Observe(new LineStateDifference[0], 30, o);
            Assert.Equal(1, o.Fixed);
        }

        [Test]
        public void StubbornLineIsToleratedAfterTwoResends()
        {
            var t = new LineCheckTracker();
            var o = new LineCheckTracker.Outcome();
            var diff = new[] { D(9, "BrokenSegmentMask", "none", "2"), D(9, "TransportLineFlags", "0x2", "0x0") };
            int resends = 0;
            bool tolerated = false;
            for (int i = 0; i < 10 && !tolerated; i++)
            {
                t.Observe(diff, i * 10, o);
                resends += o.Resend.Count;
                foreach (string line in o.Log)
                {
                    if (line.Contains("still differs")) tolerated = true;
                }
            }
            Assert.True(tolerated);
            Assert.Equal(2, resends);
            t.Observe(diff, 100, o);
            Assert.Equal(0, o.Resend.Count);
            Assert.Equal(1, o.Tolerated);
        }

        [Test]
        public void ResendsPerCheckAreCapped()
        {
            var t = new LineCheckTracker { MaxResendsPerCheck = 2 };
            var o = new LineCheckTracker.Outcome();
            var diffs = new List<LineStateDifference>();
            for (uint i = 1; i <= 5; i++) diffs.Add(D(i, "Stops", "1", "0"));
            t.Observe(diffs, 0, o);
            t.Observe(diffs, 10, o);
            Assert.Equal(2, o.Resend.Count);
            t.Observe(diffs, 20, o);
            Assert.Equal(2, o.Resend.Count, "the next check re-sends the next lines");
        }
    }
}
