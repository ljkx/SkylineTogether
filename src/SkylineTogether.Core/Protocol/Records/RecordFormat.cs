namespace SkylineTogether.Core.Protocol
{
    /// <summary>Bit layouts and value ranges of the record formats, shared by <see cref="RecordWriter"/> and <see cref="RecordReader"/>.</summary>
    internal static class RecordFormat
    {
        internal const SpawnFlags KnownSpawnFlags = SpawnFlags.Mover | SpawnFlags.HasUpdateFrame | SpawnFlags.Repair;
        internal const TouchFlags KnownTouchFlags = TouchFlags.Updated | TouchFlags.BatchesUpdated | TouchFlags.PathfindUpdated | TouchFlags.EffectsUpdated;
        internal const ValueHeaderFlags KnownValueFlags = ValueHeaderFlags.HasEnabledBit | ValueHeaderFlags.Enabled | ValueHeaderFlags.PresenceOnly;

        /// <summary>DESPAWN flags byte: bit 0 = delete the first-class children as the host did.</summary>
        internal const byte DespawnDeleteChildren = 1;

        /// <summary>ICON op byte: bits 0-1 = <see cref="IconOp"/>, bit 6 = target present, bit 7 = location present.</summary>
        internal const byte IconOpMask = 0x03;
        internal const byte IconHasTarget = 0x40;
        internal const byte IconHasLocation = 0x80;

        /// <summary>RESULT status byte: bit 0 = rejected (reason ≠ None), bit 1 = conflict slot present.</summary>
        internal const byte ResultRejected = 1;
        internal const byte ResultHasConflict = 2;

        internal const byte MaxRecordKind = (byte)RecordKind.TypeSetKeyframe;
        internal const byte MaxReplicationClass = (byte)ReplicationClass.OnDemand;
        internal const byte MaxEventKind = (byte)EventRecordKind.SpeedChanged;

        /// <summary>UTF-8 bytes a NAME may carry: <see cref="Limits.MaxNameChars"/> UTF-16 units of at most 3 bytes.</summary>
        internal const int MaxNameUtf8Bytes = Limits.MaxNameChars * ProtocolText.MaxUtf8BytesPerChar;

        internal static bool IsDefinedKind(byte kind) => kind >= (byte)RecordKind.TypeSetDef && kind <= MaxRecordKind;
    }
}
