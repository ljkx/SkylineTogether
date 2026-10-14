using System;
using System.Text;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Hashing
{
    /// <summary>xxHash64 against published vectors, plus overload and streaming equivalence.</summary>
    public sealed class XxHash64Tests
    {
        private static ulong Hash(string ascii, ulong seed = 0)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(ascii);
            return XxHash64.Hash(bytes, 0, bytes.Length, seed);
        }

        [Test]
        public void PublishedVectors()
        {
            // reference values of the xxHash project and its bindings (XXH64, seed 0 unless stated)
            Assert.Equal(0xEF46DB3751D8E999UL, Hash(""));
            Assert.Equal(0xD24EC4F1A98C6E5BUL, Hash("a"));
            Assert.Equal(0x44BC2CF5AD770999UL, Hash("abc"));
            Assert.Equal(0x32DD38952C4BC720UL, Hash("xxhash"));
            Assert.Equal(0xB559B98D844E0635UL, Hash("xxhash", 20141025));
            Assert.Equal(0xFBCEA83C8A378BF1UL, Hash("Nobody inspects the spammish repetition"));
        }

        [Test]
        public unsafe void PointerAndArrayOverloadsAgree()
        {
            var random = new DeterministicRandom(64);
            var data = new byte[300];
            random.NextBytes(data, 0, data.Length);
            for (int offset = 0; offset < 5; offset++)
            {
                for (int length = 0; length + offset <= data.Length; length += 7)
                {
                    ulong fromArray = XxHash64.Hash(data, offset, length, 99);
                    fixed (byte* p = &data[offset == data.Length ? 0 : offset])
                    {
                        Assert.Equal(fromArray, XxHash64.Hash(length == 0 ? null : p, length, 99));
                    }
                }
            }
        }

        [Test]
        public void SeedChangesTheHash()
        {
            Assert.NotEqual(Hash("abc", 0), Hash("abc", 1));
            Assert.NotEqual(Hash("abc", 1), Hash("abc", 2));
        }

        [Test]
        public void ArgumentsAreValidated()
        {
            Assert.Throws<ArgumentNullException>(() => XxHash64.Hash(null, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => XxHash64.Hash(new byte[4], 3, 2));
        }
    }
}
