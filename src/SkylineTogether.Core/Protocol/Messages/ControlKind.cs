namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Kinds of <see cref="ControlMessage"/> (ARCH Appendix C); the values travel on the wire and never change. Payload
    /// codecs: <see cref="PasswordChallenge"/> → <see cref="PasswordChallengeMessage"/>, <see cref="PasswordResponse"/>
    /// → <see cref="PasswordResponseMessage"/>, <see cref="ManifestDetail"/> → <see cref="ManifestDetailMessage"/>,
    /// <see cref="SnapshotManifest"/> →
    /// <see cref="SnapshotManifestCodec"/>, <see cref="LineRows"/> → core-integrity's <c>LineStateRow</c>; the session
    /// payloads (settings, players, roles, speed, toasts, resume, ...) are defined by core-session.
    /// </summary>
    public enum ControlKind : byte
    {
        PasswordChallenge,
        PasswordResponse,
        ManifestRequest,
        ManifestDetail,
        SessionSettings,
        Players,
        Roles,
        HostBusy,
        HostReady,
        /// <summary>Host → client: the session ended for everyone (payload: text reason).</summary>
        SessionEnd,
        SessionReset,
        /// <summary>Host → client: the player was removed (payload: text reason); they may join again.</summary>
        Kick,
        /// <summary>w8/security: host → client: the player was removed and banned (payload: text reason).</summary>
        Ban,
        SpeedRequest,
        SpeedChanged,
        ResyncRequest,
        ResyncStart,
        Toast,
        SnapshotManifest,
        ReadyForSnapshot,
        LoadDone,
        Live,
        ResumeRequest,
        ResumeResult,
        /// <summary>Host → originator on receipt of a proposal (ARCH §5.4.2) (reserved; not sent by the shipped build).</summary>
        ProposalReceived,
        RepairThrottled,
        Disconnecting,
        /// <summary><c>/st lines compare</c>: request of the host's line rows (ARCH §4.11.6).</summary>
        LineRowsRequest,
        /// <summary>The host's line rows at one sample tick.</summary>
        LineRows,
        /// <summary>Client → host: join progress while downloading and loading (payload: f32 fraction of the phase 0..1, u8 phase 0 = download, 1 = load).</summary>
        LoadProgress,
        /// <summary>
        /// w8/security: client → host: a moderation request of a co-host (payload: u8 action 0 = kick, 1 = set role |
        /// u8 target peer | u8 role); the host checks the sender's role (<c>Session.Moderation</c>) and ignores the rest.
        /// </summary>
        PlayerAction
    }
}
