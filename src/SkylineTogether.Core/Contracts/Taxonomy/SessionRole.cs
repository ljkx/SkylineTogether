namespace SkylineTogether.Core
{
    /// <summary>Player roles (ARCH §5.6); each has default permissions the host can override per player.</summary>
    public enum SessionRole : byte
    {
        Host,
        CoHost,
        Builder,
        /// <summary>No local applies (the no-predict gate, ARCH §5.3).</summary>
        Viewer
    }
}
