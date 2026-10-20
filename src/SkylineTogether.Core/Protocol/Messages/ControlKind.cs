namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Kinds of <see cref="ControlMessage"/> (ARCHITECTURE.md#envelope-and-message-kinds); the values travel on the wire and never change. Payload
    /// codecs: <see cref="PasswordChallenge"/> → <see cref="PasswordChallengeMessage"/>, <see cref="PasswordResponse"/>
    /// → <see cref="PasswordResponseMessage"/>, <see cref="ManifestDetail"/> → <see cref="ManifestDetailMessage"/>,
    /// <see cref="SnapshotManifest"/> → <see cref="SnapshotManifestCodec"/>; the session payloads (players, speed,
    /// toasts, load progress, ...) are defined by <c>Session.SessionPayloads</c>.
    /// <para>
    /// Values marked "reserved" (ARCHITECTURE.md#reserved-wire-values) come from the original design and are never sent;
    /// their numbers stay taken, and both session ends ignore them (<see cref="ControlMessage.IsReservedKind"/>), as they do
    /// unknown values.
    /// </para>
    /// </summary>
    public enum ControlKind : byte
    {
        PasswordChallenge,
        PasswordResponse,
        /// <summary>Reserved (2): never sent; ignored on receipt.</summary>
        ManifestRequest,
        ManifestDetail,
        /// <summary>Reserved (4): never sent; ignored (the session settings travel in the welcome).</summary>
        SessionSettings,
        Players,
        /// <summary>Reserved (6): never sent; ignored (roles travel in the player list).</summary>
        Roles,
        /// <summary>Reserved (7): never sent; ignored.</summary>
        HostBusy,
        /// <summary>Reserved (8): never sent; ignored.</summary>
        HostReady,
        /// <summary>Host → client: the session ended for everyone (payload: text reason).</summary>
        SessionEnd,
        /// <summary>Reserved (10): never sent; ignored.</summary>
        SessionReset,
        /// <summary>Host → client: the player was removed (payload: text reason); they may join again.</summary>
        Kick,
        /// <summary>Host → client: the player was removed and banned (payload: text reason).</summary>
        Ban,
        SpeedRequest,
        SpeedChanged,
        /// <summary>Reserved (15): never sent; ignored (a resync is a rejoin).</summary>
        ResyncRequest,
        /// <summary>Reserved (16): never sent; ignored.</summary>
        ResyncStart,
        Toast,
        SnapshotManifest,
        ReadyForSnapshot,
        LoadDone,
        Live,
        /// <summary>Reserved (22): never sent; ignored.</summary>
        ResumeRequest,
        /// <summary>Reserved (23): never sent; ignored.</summary>
        ResumeResult,
        /// <summary>Reserved (24): never sent; ignored.</summary>
        ProposalReceived,
        /// <summary>Reserved (25): never sent; ignored.</summary>
        RepairThrottled,
        Disconnecting,
        /// <summary>Reserved (27): never sent; ignored (the line check travels as batch records).</summary>
        LineRowsRequest,
        /// <summary>Reserved (28): never sent; ignored.</summary>
        LineRows,
        /// <summary>Client → host: join progress while downloading and loading (payload: f32 fraction of the phase 0..1, u8 phase 0 = download, 1 = load).</summary>
        LoadProgress,
        /// <summary>
        /// Client → host: a moderation request of a co-host (payload: u8 action 0 = kick, 1 = set role |
        /// u8 target peer | u8 role); the host checks the sender's role (<c>Session.Moderation</c>) and ignores the rest.
        /// </summary>
        PlayerAction,
        /// <summary>
        /// Host → client, right after the welcome: what differs between the host and the admitted joiner without blocking
        /// the join (asset packs, extra DLC, mods the host allows, mod settings the host's values replace); payload: a
        /// <c>Session.MismatchReport</c> of kind "notice" (ARCHITECTURE.md#handshake-and-admission).
        /// </summary>
        JoinNotice,
        /// <summary>
        /// Host → client: the password was right, or none was needed (payload: bytes hostProof, ≤ 64; empty when no
        /// password was asked). The host's proof that it knows the password too
        /// (<c>Session.SessionCrypto.HostProof</c>); the client verifies it and only then sends its manifest.
        /// </summary>
        Authenticated,
        /// <summary>
        /// Host → client: the player was disconnected for a technical reason (not responding, too slow, a join that stopped
        /// making progress, messages that keep failing); unlike a kick, joining again is expected (payload:
        /// <c>Session.LocalText</c>).
        /// </summary>
        Dropped
    }
}
