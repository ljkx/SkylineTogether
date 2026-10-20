namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Version of the wire format (ARCHITECTURE.md#code-rules): every change of an envelope, epoch, record or
    /// message layout increments it, and peers with different versions refuse each other in the handshake
    /// (<see cref="RefuseReason.ProtocolVersion"/>), before the password and before any build or mod detail. The policy
    /// (one increment per change that reaches main) is in docs/dev/releasing.md. The golden byte vectors of the Core
    /// tests (<c>Protocol/*GoldenTests.cs</c>) pin the layouts.
    /// <para>
    /// Two layouts are kept stable across versions so that peers of different versions can still refuse each other
    /// readably: the leading <c>varint protocolVersion</c> of <see cref="HelloMessage"/> (see
    /// <see cref="HelloMessage.PeekProtocolVersion"/>) and the whole <see cref="RefuseMessage"/>, both inside the
    /// two-byte envelope header of <see cref="EnvelopeCodec"/>.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class ProtocolVersion
    {
        /// <summary>
        /// The version this build speaks. 1 was the first layout; 2 covers everything added since under one increment (among
        /// others the id ceiling record, the cell-map messages and the mod channel message kind); 3: the handshake digests
        /// and <c>ManifestDetail</c> of the compatibility check (DLC, asset pack and mod setting lists instead of the
        /// never-filled format-tag, type-catalog and data-file fields) and <see cref="ControlKind.JoinNotice"/>; 4: the join flags
        /// byte at the end of <see cref="HelloMessage"/> ("join anyway" with different mods, <see cref="HelloJoinFlags"/>);
        /// 5: the mover roster record (<see cref="Sync.SyncRecordKind.MoverRoster"/>); 6: the hardened handshake (the
        /// password proof bound to the connection and answered by the host, <see cref="ControlKind.Authenticated"/>, the
        /// manifest sent only after it, 200 000 PBKDF2 rounds), <see cref="ControlKind.Dropped"/>, and localizable texts
        /// (<c>Session.LocalText</c> in kick, ban, end and drop payloads, keys in chat lines, a "text" record in refusal reports);
        /// 7: the id generation byte in entity records (<see cref="Sync.SyncRecord.Generation"/>) and the painted-cell mask
        /// of resource paint patches.
        /// </summary>
        public const int Current = 7;
    }
}
