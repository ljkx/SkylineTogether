using System;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Declares on which thread a type or member may be used (ARCHITECTURE.md#processes-and-threads). Every public member
    /// that is not main-thread-only carries this attribute; members without it are main-thread-only. Documentation
    /// only: implementations that mutate engine state additionally assert their phase in debug builds.
    /// </summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = false, Inherited = true)]
    public sealed class ThreadAffinityAttribute : Attribute
    {
        public ThreadAffinityAttribute(ThreadKind kind)
        {
            Kind = kind;
        }

        /// <summary>The thread the annotated type or member may be used on.</summary>
        public ThreadKind Kind { get; }
    }
}
