using System;
using SkylineTogether.Core.Codec;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Codec.CodecTestUtil;

namespace SkylineTogether.Core.Tests.Codec
{
    /// <summary>
    /// Decoder fuzzing (IMPLEMENTATION-PLAN §4): whatever bytes a peer sends, reading them with any sequence of members
    /// either succeeds or throws <see cref="DecodeException"/>, never anything else, and never reads outside the record.
    /// </summary>
    public sealed class CanonicalFuzzTests
    {
        private static void ReplayExpectingOnlyDecodeErrors(CodecScript script, byte[] buffer, int offset, int length, string where)
        {
            var reader = new CanonicalReader(CodecScript.Seams());
            reader.Open(buffer, offset, length);
            try
            {
                script.Replay(reader);
            }
            catch (DecodeException)
            {
                // expected for malformed input
            }
            catch (Exception e)
            {
                throw new AssertionException(where + ": " + e.GetType().Name + " escaped the decoder: " + e.Message + Environment.NewLine + e.StackTrace);
            }
            if (reader.Position < 0 || reader.Position > length) Assert.Fail(where + ": the cursor left the record (" + reader.Position + ")");
        }

        [Test(TimeoutSeconds = 120)]
        public void RandomBytesRaiseOnlyDecodeExceptions()
        {
            var random = new DeterministicRandom(0xF022);
            for (int iteration = 0; iteration < 20000; iteration++)
            {
                CodecScript script = CodecScript.Random(random, 12, 2);
                int length = random.Next(64);
                // the record ends flush with the array, so a read past its end would raise IndexOutOfRangeException
                // (reported as an escaped exception) instead of silently consuming foreign bytes
                var buffer = new byte[length + 8];
                random.NextBytes(buffer, 0, buffer.Length);
                if (random.Next(4) == 0)
                {
                    // bias towards plausible small sizes and counts
                    for (int i = 8; i + 4 <= 8 + length; i += 4) buffer[i + 1] = buffer[i + 2] = buffer[i + 3] = 0;
                }
                ReplayExpectingOnlyDecodeErrors(script, buffer, 8, length, "iteration " + iteration);
            }
        }

        [Test(TimeoutSeconds = 120)]
        public void MutatedEncodingsRaiseOnlyDecodeExceptions()
        {
            var random = new DeterministicRandom(0xBADC0DE);
            for (int iteration = 0; iteration < 6000; iteration++)
            {
                CodecScript script = CodecScript.Random(random, 15, 3);
                byte[] valid = Write(script.Write, CodecScript.Seams());
                for (int mutation = 0; mutation < 4; mutation++)
                {
                    byte[] mutated = Mutate(random, valid);
                    ReplayExpectingOnlyDecodeErrors(script, mutated, 0, mutated.Length, "iteration " + iteration + " mutation " + mutation);
                }
            }
        }

        [Test]
        public void EveryTruncationOfAValidRecordIsADecodeError()
        {
            var random = new DeterministicRandom(4242);
            for (int iteration = 0; iteration < 300; iteration++)
            {
                CodecScript script = CodecScript.Random(random, 12, 2);
                byte[] valid = Write(script.Write, CodecScript.Seams());
                for (int length = 0; length < valid.Length; length++)
                {
                    var reader = new CanonicalReader(CodecScript.Seams());
                    reader.Open(valid, 0, length);
                    bool threw = false;
                    try
                    {
                        script.Replay(reader);
                    }
                    catch (DecodeException)
                    {
                        threw = true;
                    }
                    catch (Exception e)
                    {
                        Assert.Fail("iteration " + iteration + ", length " + length + ": " + e.GetType().Name + ": " + e.Message);
                    }
                    // the script consumes exactly the bytes it wrote (skipped blocks still check their declared size),
                    // so every proper prefix of the record must fail
                    Assert.True(threw, "iteration " + iteration + ": a record truncated to " + length + " of " + valid.Length + " bytes decoded");
                }
            }
        }

        private static byte[] Mutate(DeterministicRandom random, byte[] valid)
        {
            switch (random.Next(5))
            {
                case 0:
                {
                    // flip a few bits
                    byte[] copy = (byte[])valid.Clone();
                    if (copy.Length == 0) return new byte[] { (byte)random.Next(256) };
                    int flips = 1 + random.Next(3);
                    for (int i = 0; i < flips; i++) copy[random.Next(copy.Length)] ^= (byte)(1 << random.Next(8));
                    return copy;
                }
                case 1:
                {
                    // replace a byte with an extreme value
                    byte[] copy = (byte[])valid.Clone();
                    if (copy.Length == 0) return new byte[] { 0xFF };
                    byte[] extremes = { 0x00, 0x7F, 0x80, 0xFF, 0xC0, 0xE0, 0xF0 };
                    copy[random.Next(copy.Length)] = extremes[random.Next(extremes.Length)];
                    return copy;
                }
                case 2:
                {
                    // truncate
                    int length = valid.Length == 0 ? 0 : random.Next(valid.Length);
                    var copy = new byte[length];
                    Array.Copy(valid, copy, length);
                    return copy;
                }
                case 3:
                {
                    // insert random bytes
                    int at = random.Next(valid.Length + 1);
                    int extra = 1 + random.Next(4);
                    var copy = new byte[valid.Length + extra];
                    Array.Copy(valid, 0, copy, 0, at);
                    random.NextBytes(copy, at, extra);
                    Array.Copy(valid, at, copy, at + extra, valid.Length - at);
                    return copy;
                }
                default:
                {
                    // overwrite a 4-byte window with a huge or negative length
                    byte[] copy = (byte[])valid.Clone();
                    if (copy.Length < 4) return copy;
                    int at = random.Next(copy.Length - 3);
                    uint value = random.NextBool() ? 0xFFFFFFFFu : 0x7FFFFFF0u + (uint)random.Next(16);
                    copy[at] = (byte)value;
                    copy[at + 1] = (byte)(value >> 8);
                    copy[at + 2] = (byte)(value >> 16);
                    copy[at + 3] = (byte)(value >> 24);
                    return copy;
                }
            }
        }
    }
}
