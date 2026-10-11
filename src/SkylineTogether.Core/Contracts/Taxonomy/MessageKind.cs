namespace SkylineTogether.Core
{
    /// <summary>
    /// Envelope message kinds (ARCH §4.6.1, Appendix C). The values are the first byte of every message and must
    /// never change; 0 is not a valid kind.
    /// </summary>
    public enum MessageKind : byte
    {
        Hello = 1,
        Welcome,
        Refuse,
        Control,
        Epoch,
        Ack,
        Proposal,
        Motion,
        MotionControl,
        Clock,
        View,
        Presence,
        HashDetail,
        RepairRequest,
        BlobManifest,
        BlobChunk,
        BlobHave,
        Ping,
        Chat
    }
}
