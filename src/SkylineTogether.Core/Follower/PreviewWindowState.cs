namespace SkylineTogether.Core.Follower
{
    /// <summary>State of a tool-scoped gate's preview window (<see cref="PreviewWindow"/>, ARCH §6.3, §4.11.4).</summary>
    public enum PreviewWindowState : byte
    {
        /// <summary>The scoped systems are disabled.</summary>
        Closed,

        /// <summary>The tool is active (and the client is live and the guard healthy): the scoped systems run.</summary>
        Open,

        /// <summary>The tool was deactivated or last applied less than <c>lingerSeconds</c> ago: the systems still run so
        /// that path results for temp and predicted entities can land.</summary>
        Lingering
    }
}
