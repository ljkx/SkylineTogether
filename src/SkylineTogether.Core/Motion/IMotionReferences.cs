namespace SkylineTogether.Core.Motion
{
    /// <summary>
    /// Reference frames for Q-B delta records (ARCH §4.9.3 client rule): a delta is decoded only when the receiver holds
    /// a non-synthetic frame of the same interest slot and assignment generation for the delta's reference tick
    /// (<c>T − 16·refAge</c>). References are the Q-A baseline frames of an interest entry and the keyframes received
    /// since. Implemented by <c>MotionClientStager</c>.
    /// </summary>
    public interface IMotionReferences
    {
        /// <summary>True with the frame exactly as it was decoded; false when no such reference is held (non-synthetic only).</summary>
        bool TryGetReference(ushort interestSlot, byte assignGeneration, uint referenceTick, out MotionFrame frame);
    }
}
