using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>w9/perf: the inspection sample's header is read without decoding its entities.</summary>
    public sealed class SimValueSampleHeaderTests
    {
        private static byte[] Sample(uint tick, int entities)
        {
            var w = new ByteWriter();
            int count = SimValueMessage.BeginSample(w, tick, 42);
            for (int i = 0; i < entities; i++)
            {
                int e = SimValueMessage.BeginEntity(w, (uint)(10 + i), Family.Object);
                SimValueMessage.AddComponent(w, 0x55UL, SyncComponentKind.Data, new byte[] { 1, 2 }, 0, 2);
                SimValueMessage.EndEntity(w, e, 1);
            }
            SimValueMessage.End(w, count, entities);
            return w.ToArray();
        }

        [Test]
        public void TheHeaderGivesTickAndEntityCount()
        {
            byte[] data = Sample(123456, 3);
            SimValueMessage.ReadSampleHeader(data, 0, data.Length, out uint tick, out uint entities);
            Assert.Equal(123456u, tick);
            Assert.Equal(3u, entities);
            SimValueMessage.Read(data, 0, data.Length, out byte type, out uint fullTick, out _);
            Assert.Equal(SimValueMessage.TypeSample, type);
            Assert.Equal(tick, fullTick);
        }

        [Test]
        public void AnEmptySampleHasNoEntities()
        {
            byte[] data = Sample(5, 0);
            SimValueMessage.ReadSampleHeader(data, 0, data.Length, out _, out uint entities);
            Assert.Equal(0u, entities);
        }

        [Test]
        public void MalformedHeadersAreRefused()
        {
            byte[] data = Sample(5, 2);
            Assert.Throws<DecodeException>(() => SimValueMessage.ReadSampleHeader(data, 0, 3, out _, out _), "truncated");
            var w = new ByteWriter();
            SimValueMessage.End(w, SimValueMessage.BeginValues(w), 0);
            byte[] notSample = w.ToArray();
            Assert.Throws<DecodeException>(() => SimValueMessage.ReadSampleHeader(notSample, 0, notSample.Length, out _, out _), "a values message");
            data[0] = 99;
            Assert.Throws<DecodeException>(() => SimValueMessage.ReadSampleHeader(data, 0, data.Length, out _, out _), "version");
        }
    }
}
