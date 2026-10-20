using System;

namespace SkylineTogether.Core.Tests.Framework
{
    /// <summary>Checks the test framework itself (assertion helpers and failure messages).</summary>
    public sealed class FrameworkSelfTests
    {
        [Test]
        public void EqualPassesForEqualValues()
        {
            Assert.Equal(42, 42);
            Assert.Equal("a", "a");
            Assert.SequenceEqual(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 });
        }

        [Test]
        public void EqualFailureNamesBothValues()
        {
            var e = Assert.Throws<AssertionException>(() => Assert.Equal(1UL, 2UL));
            Assert.Contains("expected 1", e.Message);
            Assert.Contains("actual 2", e.Message);
        }

        [Test]
        public void SequenceFailureNamesTheIndex()
        {
            var e = Assert.Throws<AssertionException>(() => Assert.SequenceEqual(new[] { 1, 2, 3 }, new[] { 1, 5, 3 }));
            Assert.Contains("index 1", e.Message);
        }

        [Test]
        public void ThrowsAcceptsSubclassesAndRejectsSilence()
        {
            Assert.Throws<ArgumentException>(() => throw new ArgumentOutOfRangeException("x"));
            Assert.Throws<AssertionException>(() => Assert.Throws<InvalidOperationException>(() => { }));
        }

        /// <summary>
        /// Made to fail, and run only with <c>--canary</c>: CI runs it and requires exit code 1, which proves end to end
        /// (process exit code included) that a failing test fails the build.
        /// </summary>
        [Test(Canary = true)]
        public void TheCanaryFails() => Assert.Fail("the canary fails on purpose (--canary)");

        [Test]
        public void SkipThrowsSkipException()
        {
            Assert.Throws<SkipException>(() => Assert.Skip("reason"));
        }
    }
}
