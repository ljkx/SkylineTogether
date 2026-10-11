namespace SkylineTogether.Core
{
    /// <summary>A value of one (slot, type) pair in a plan (the latest one when epochs were merged).</summary>
    public struct ValueOp
    {
        public NetSlot Slot;
        public ComponentOp Component;
    }
}
