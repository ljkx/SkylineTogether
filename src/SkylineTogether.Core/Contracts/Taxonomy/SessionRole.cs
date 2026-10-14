namespace SkylineTogether.Core
{
    /// <summary>Player roles (ARCHITECTURE.md#roles-and-moderation); permissions follow from the role (<see cref="Session.RolePermissions"/>).</summary>
    public enum SessionRole : byte
    {
        Host,
        CoHost,
        Builder,
        /// <summary>No local applies (the no-predict gate, ARCHITECTURE.md#roles-and-moderation).</summary>
        Viewer
    }
}
