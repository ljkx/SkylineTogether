using System;

namespace SkylineTogether.Core.Tests.Framework
{
    /// <summary>Thrown by <see cref="Assert"/> when an expectation does not hold; the runner reports the test as failed.</summary>
    public sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message) { }
    }
}
