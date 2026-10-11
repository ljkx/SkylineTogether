namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Kinds of <see cref="ControlMessage"/> (ARCH Appendix C); the values travel on the wire and never change. Payload
    /// codecs: <see cref="PasswordChallenge"/> → <see cref="PasswordChallengeMessage"/>, <see cref="PasswordResponse"/>
    /// → <see cref="PasswordResponseMessage"/>, <see cref="ManifestDetail"/> → <see cref="ManifestDetailMessage"/>,
    /// <see cref="ProposalReceived"/> → <see cref="ProposalReceivedMessage"/>, <see cref="SnapshotManifest"/> →
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
        SessionEnd,
        SessionReset,
        Kick,
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
        /// <summary>Host → originator on receipt of a proposal (ARCH §5.4.2); payload <see cref="ProposalReceivedMessage"/>.</summary>
        ProposalReceived,
        RepairThrottled,
        Disconnecting,
        /// <summary><c>/st lines compare</c>: request of the host's line rows (ARCH §4.11.6).</summary>
        LineRowsRequest,
        /// <summary>The host's line rows at one sample tick.</summary>
        LineRows
    }
}
