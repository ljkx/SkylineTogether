using System;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Codec;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Codec.CodecTestUtil;

namespace SkylineTogether.Core.Tests.Codec
{
    /// <summary>
    /// Property tests (IMPLEMENTATION-PLAN §7.26): random sequences of every member, nested and skipped blocks included,
    /// round-trip value by value; and the bytes are canonical (writing the decoded values again gives the same bytes).
    /// </summary>
    public sealed class CanonicalPropertyTests
    {
        [Test]
        public void RandomMemberSequencesRoundTrip()
        {
            var random = new DeterministicRandom(0xC0DEC);
            for (int iteration = 0; iteration < 3000; iteration++)
            {
                CodecScript script = CodecScript.Random(random, 30, 3);
                FakeEntityRefs seams = CodecScript.Seams();
                byte[] bytes;
                try
                {
                    bytes = Write(script.Write, seams);
                }
                catch (Exception e)
                {
                    throw new AssertionException("iteration " + iteration + ": writing failed: " + e);
                }
                CanonicalReader reader = Reader(bytes, seams);
                try
                {
                    script.ReadAndVerify(reader);
                }
                catch (AssertionException e)
                {
                    throw new AssertionException("iteration " + iteration + " (" + bytes.Length + " bytes): " + e.Message);
                }
                Assert.True(reader.IsRecordConsumed, "iteration " + iteration + ": the whole record is consumed");
                Assert.False(reader.HadMissingReference, "iteration " + iteration + ": every reference resolves");
            }
        }

        [Test]
        public void ScriptsAreWrittenIdenticallyEveryTime()
        {
            // determinism: the same values give the same bytes, independent of the target's prior content or capacity
            var random = new DeterministicRandom(99);
            for (int iteration = 0; iteration < 300; iteration++)
            {
                CodecScript script = CodecScript.Random(random, 20, 2);
                byte[] first = Write(script.Write, CodecScript.Seams());
                var target = new ByteWriter(1);
                target.WriteBytes(new byte[] { 9, 9, 9 }, 0, 3);
                var writer = new CanonicalWriter(target, CodecScript.Seams());
                script.Write(writer);
                byte[] second = target.ToArray();
                Assert.Equal(first.Length + 3, second.Length, "iteration " + iteration);
                for (int i = 0; i < first.Length; i++)
                {
                    if (first[i] != second[i + 3]) Assert.Fail("iteration " + iteration + ": byte " + i + " differs");
                }
            }
        }

        [Test]
        public void ReaderAndWriterInstancesAreReusable()
        {
            var random = new DeterministicRandom(7);
            var target = new ByteWriter(8);
            FakeEntityRefs seams = CodecScript.Seams();
            var writer = new CanonicalWriter(target, seams);
            var reader = new CanonicalReader(seams);
            for (int iteration = 0; iteration < 200; iteration++)
            {
                CodecScript script = CodecScript.Random(random, 15, 2);
                target.Reset();
                script.Write(writer);
                Assert.Equal(0, writer.OpenBlockCount);
                byte[] copy = target.ToArray();
                reader.Open(copy, 0, copy.Length);
                script.ReadAndVerify(reader);
                Assert.True(reader.IsRecordConsumed, "iteration " + iteration);
            }
        }

        [Test]
        public void RecordsInsideALargerBufferDecodeIndependently()
        {
            // the applier opens one record after another inside an epoch buffer
            var random = new DeterministicRandom(31);
            var scripts = new CodecScript[20];
            var offsets = new int[20];
            var lengths = new int[20];
            var target = new ByteWriter();
            FakeEntityRefs seams = CodecScript.Seams();
            var writer = new CanonicalWriter(target, seams);
            for (int i = 0; i < scripts.Length; i++)
            {
                scripts[i] = CodecScript.Random(random, 10, 2);
                offsets[i] = target.Length;
                scripts[i].Write(writer);
                lengths[i] = target.Length - offsets[i];
            }
            byte[] epoch = target.ToArray();
            var reader = new CanonicalReader(seams);
            for (int i = scripts.Length - 1; i >= 0; i--)
            {
                reader.Open(epoch, offsets[i], lengths[i]);
                scripts[i].ReadAndVerify(reader);
                Assert.True(reader.IsRecordConsumed, "record " + i);
            }
        }
    }
}
