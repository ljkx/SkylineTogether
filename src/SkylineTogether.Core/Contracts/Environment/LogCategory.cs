namespace SkylineTogether.Core
{
    /// <summary>
    /// Log category names (ARCHITECTURE.md#logs). Using the constants keeps the categories of the log file,
    /// the rate limiter and the diagnostics dump consistent across modules.
    /// </summary>
    public static class LogCategory
    {
        public const string Net = "net";
        public const string Session = "session";
        public const string Capture = "capture";
        public const string Apply = "apply";
        public const string Motion = "motion";
        public const string Join = "join";
        public const string Gate = "gate";
        public const string Probe = "probe";
        public const string Ui = "ui";
        /// <summary>Data files and their parsing (gates, cosmetic, policies, adapters, sysstate, locale).</summary>
        public const string Config = "config";
        public const string Protocol = "protocol";
        public const string Replication = "replication";
        public const string Integrity = "integrity";
        public const string Intents = "intents";
        public const string Scheduling = "scheduling";
        public const string Fields = "fields";
        public const string Compat = "compat";
        public const string Diagnostics = "diagnostics";
    }
}
