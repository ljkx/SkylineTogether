namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// Default permissions per session role (ARCHITECTURE.md#roles-and-moderation). Host and co-hosts may do everything; builders everything that
    /// changes the city; viewers only chat (their client also runs the no-predict gate, so their tools never apply).
    /// </summary>
    public static class RolePermissions
    {
        public static Permission Of(SessionRole role)
        {
            switch (role)
            {
                case SessionRole.Host:
                case SessionRole.CoHost:
                case SessionRole.Builder:
                    return Permission.All;
                default:
                    return Permission.Chat;
            }
        }

        /// <summary>True when <paramref name="role"/> holds every flag of <paramref name="needed"/>.</summary>
        public static bool Allows(SessionRole role, Permission needed) => (Of(role) & needed) == needed;

        /// <summary>The panel's role toggle: viewer ↔ builder (host and co-host are not toggled).</summary>
        public static SessionRole Toggle(SessionRole role)
        {
            switch (role)
            {
                case SessionRole.Viewer: return SessionRole.Builder;
                case SessionRole.Builder: return SessionRole.Viewer;
                default: return role;
            }
        }

        /// <summary>True for roles whose client predicts its own tool applies.</summary>
        public static bool Predicts(SessionRole role) => role != SessionRole.Viewer;
    }
}
