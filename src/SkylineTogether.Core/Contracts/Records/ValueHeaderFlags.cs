using System;

namespace SkylineTogether.Core
{
    /// <summary>The header byte of a VALUE record (ARCH §4.6.3); the values travel on the wire.</summary>
    [Flags]
    public enum ValueHeaderFlags : byte
    {
        None = 0,
        /// <summary>The record carries the type's enabled bit.</summary>
        HasEnabledBit = 1,
        /// <summary>The enabled bit's value (meaningful only with <see cref="HasEnabledBit"/>).</summary>
        Enabled = 2,
        /// <summary>Presence only, no payload bytes (tags and presence-replicated types).</summary>
        PresenceOnly = 4
    }
}
