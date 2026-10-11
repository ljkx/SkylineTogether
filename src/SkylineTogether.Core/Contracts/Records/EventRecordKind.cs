namespace SkylineTogether.Core
{
    /// <summary>Kinds of EVT records (ARCH §4.6.2, §4.10.3, §4.10.6); the values travel on the wire.</summary>
    public enum EventRecordKind : byte
    {
        XpToast,
        MilestoneHint,
        JournalChanged,
        RadioTag,
        HostBusy,
        SpeedChanged
    }
}
