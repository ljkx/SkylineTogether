using System;

namespace SkylineTogether.Core.Tests.Framework
{
    /// <summary>Thrown by <see cref="Assert.Skip"/>; the runner reports the test as skipped with the given reason.</summary>
    public sealed class SkipException : Exception
    {
        public SkipException(string reason) : base(reason) { }
    }
}
