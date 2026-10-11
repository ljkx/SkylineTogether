namespace SkylineTogether.Core
{
    /// <summary>The body of one channel section (SYS, PFB, ICON, EVT, NAME, MAP, ...) for the game layer's channel appliers.</summary>
    public struct ChannelOp
    {
        public SectionType Section;
        public PayloadRef Payload;
    }
}
