namespace SkylineTogether.Core
{
    /// <summary>
    /// The shared applier (ARCH §4.7): executes <see cref="ApplyProgram"/>s for client replication, host commits,
    /// repairs and rollbacks. Implemented by the game's <c>WorldApplier</c> and by the FakeWorld in tests.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public interface IWorldApplier
    {
        IApplyWorldView View { get; }

        /// <summary>
        /// Executes the program op by op. On an exception: <c>MarkDeleted</c> for every entity the program created
        /// (they never received <c>Created</c>), then a result with <see cref="ApplyResult.Faulted"/> set (ARCH §12.2).
        /// </summary>
        ApplyResult Execute(ApplyProgram program, ApplyMode mode);
    }
}
