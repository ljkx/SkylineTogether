using System;

namespace SkylineTogether.Core.Tests.Framework
{
    /// <summary>
    /// Marks a test method. The method takes no parameters and returns <c>void</c>; it may be public or private,
    /// static or instance. For an instance method the runner creates a fresh object of the declaring class (through its
    /// parameterless constructor) for every test, so tests never share instance state.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class TestAttribute : Attribute
    {
        /// <summary>Wall-clock limit for the test; a test that runs longer is reported as failed.</summary>
        public int TimeoutSeconds { get; set; } = 60;

        /// <summary>When not null, the test is not run and is reported as skipped with this reason.</summary>
        public string Skip { get; set; }

        /// <summary>
        /// A canary: a test made to fail, run only with <c>--canary</c> (and then alone). CI runs the canaries and requires
        /// the run to fail, which proves that a failing test fails the build.
        /// </summary>
        public bool Canary { get; set; }
    }
}
