namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Bits of <see cref="HelloMessage.JoinFlags"/>: what the joiner asks of the host beyond the handshake digests
    /// (ARCHITECTURE.md#handshake-and-admission). Readers reject bits outside <see cref="DefinedBits"/>; a new bit is a wire
    /// change (docs/dev/releasing.md).
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class HelloJoinFlags
    {
        /// <summary>
        /// "Join anyway": the player saw that its code mods or asset packs differ from the host's and chose to join. A host
        /// that lets players join with different mods (<c>HostOptions.AllowModMismatchOverride</c>) then admits instead of
        /// refusing for that difference; it changes nothing else (build, game version, DLC, password, bans, join policy and
        /// capacity are checked as always), and a host that does not allow it ignores the bit.
        /// </summary>
        public const byte AcceptModMismatch = 0x01;

        /// <summary>Every defined bit; readers reject others.</summary>
        public const byte DefinedBits = AcceptModMismatch;
    }
}
