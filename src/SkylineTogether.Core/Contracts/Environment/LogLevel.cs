namespace SkylineTogether.Core
{
    /// <summary>Log severities, ascending (ARCH §13.1).</summary>
    public enum LogLevel : byte
    {
        /// <summary>Per-entity detail; only behind <c>IsEnabled(Trace)</c> checks, never on hot paths otherwise.</summary>
        Trace,
        Debug,
        Info,
        Warn,
        Error,
        /// <summary>The session cannot continue (faults, critical probe failures).</summary>
        Critical
    }
}
