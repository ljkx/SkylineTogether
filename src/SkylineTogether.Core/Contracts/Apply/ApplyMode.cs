namespace SkylineTogether.Core
{
    /// <summary>Why the shared applier executes a program (docs/history/ARCHITECTURE-original.md §4.7, §5.4.2, §5.5.2).</summary>
    public enum ApplyMode : byte
    {
        /// <summary>A client applying host epochs.</summary>
        ClientReplication,
        /// <summary>The host committing a proposal inside the capture window.</summary>
        HostCommit,
        /// <summary>Repair records (entity re-sends, rebaselines).</summary>
        Repair,
        /// <summary>A client undoing a rejected or timed-out prediction.</summary>
        Rollback
    }
}
