namespace SkylineTogether.Core
{
    /// <summary>What the client gate controller does with a system (ARCH §6.3, Appendix E).</summary>
    public enum GateAction : byte
    {
        /// <summary>Disabled on clients for the whole session.</summary>
        Off,
        /// <summary>Disabled on clients; its output is fed from the host instead (budget arrays, milestone fields).</summary>
        OffAndFeed,
        /// <summary>Kept on (the keep list; informational and for the unclassified-systems report).</summary>
        Keep,
        /// <summary>Disabled only during a session load (the session-load set: Deserialize-phase rewriters, ARCH §7.4).</summary>
        OffDuringSessionLoad,
        /// <summary>Off except inside a tool's preview window (tool-scoped gates, ARCH §4.11.4).</summary>
        ToolScoped
    }
}
