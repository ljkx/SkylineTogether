using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>The client's bound on net ids from its host.</summary>
    public sealed class NetIdCeilingTests
    {
        [Test]
        public void BeforeAnyAnnouncementTheMinimumIsAccepted()
        {
            var c = new NetIdCeiling();
            Assert.Equal(NetIdCeiling.Minimum, c.Allowed);
            Assert.True(c.Allows(NetIdCeiling.Minimum, out _));
            Assert.False(c.Allows(NetIdCeiling.Minimum + 1, out bool first));
            Assert.True(first, "the first refusal is reported for the log");
            Assert.False(c.Allows(NetIdAllocator.MaxId, out first));
            Assert.False(first);
            Assert.Equal(2L, c.Refused);
            Assert.Equal(NetIdAllocator.MaxId, c.HighestRefused);
        }

        [Test]
        public void TheAnnouncedFrontierPlusSlackIsAccepted()
        {
            var c = new NetIdCeiling();
            c.Announce(5_000_000);
            Assert.Equal(5_000_000u + NetIdCeiling.Slack, c.Allowed);
            Assert.True(c.Allows(5_000_000 + NetIdCeiling.Slack, out _));
            Assert.False(c.Allows(5_000_001 + NetIdCeiling.Slack, out _));
        }

        [Test]
        public void TheCeilingNeverFallsAndNeverExceedsTheIdSpace()
        {
            var c = new NetIdCeiling();
            c.Announce(3_000_000);
            c.Announce(100);
            Assert.Equal(3_000_000u, c.Announced);
            c.Announce(uint.MaxValue);
            Assert.Equal(NetIdAllocator.MaxId, c.Allowed);
            c.Reset();
            Assert.Equal(0u, c.Announced);
            Assert.Equal(0L, c.Refused);
        }

        [Test]
        public void AGrantAnnouncesItsEnd()
        {
            var c = new NetIdCeiling();
            c.AnnounceRange(4_000_000, 4096);
            Assert.Equal(4_004_096u, c.Announced);
            c.AnnounceRange(uint.MaxValue - 5, 100);
            Assert.Equal(NetIdAllocator.MaxId, c.Announced, "overflow-safe");
        }

        [Test]
        public void IdZeroIsNeverAccepted()
        {
            Assert.False(new NetIdCeiling().Allows(0, out _));
        }

        [Test]
        public void TablesGrowGeometricallyAndOnlyAsFarAsNeeded()
        {
            Assert.Equal(1024, NetIdCeiling.GrowTo(1024, 10, 1 << 24), "already large enough");
            Assert.Equal(2048, NetIdCeiling.GrowTo(1024, 1024, 1 << 24), "doubling");
            Assert.Equal(5001, NetIdCeiling.GrowTo(1024, 5000, 1 << 24), "a jump beyond the double: exactly what is needed");
            Assert.Equal(1 << 24, NetIdCeiling.GrowTo(12_000_000, 12_000_000, 1 << 24), "the double is capped at the id space");
            Assert.Equal(2, NetIdCeiling.GrowTo(0, 1, 100));
        }

        [Test]
        public void IdCeilingRecordsRoundTripAndAreHostOnly()
        {
            var w = new SyncBatch.Writer();
            w.IdCeiling(123456);
            System.ArraySegment<byte> bytes = w.Finish();
            List<SyncRecord> records = SyncBatch.Read(bytes.Array, bytes.Offset, bytes.Count);
            Assert.Equal(1, records.Count);
            Assert.Equal(SyncRecordKind.IdCeiling, records[0].Kind);
            Assert.Equal(123456u, records[0].RangeStart);
            Assert.False(SyncBatch.AcceptedFromClient(SyncRecordKind.IdCeiling));
        }
    }
}
