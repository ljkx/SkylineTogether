namespace SkylineTogether.Core
{
    /// <summary>A POSE record in a plan: a quantised host <c>Transform</c> that re-pins an out-of-interest mover (ARCH §4.9.5).</summary>
    public struct PoseOp
    {
        public NetSlot Slot;
        public PayloadRef Payload;
    }
}
