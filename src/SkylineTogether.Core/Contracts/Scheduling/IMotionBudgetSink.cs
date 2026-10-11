namespace SkylineTogether.Core
{
    /// <summary>Receives the motion budget the scheduler grants a peer each frame (core-motion <c>MotionHostEncoder</c>).</summary>
    public interface IMotionBudgetSink
    {
        void SetMotionBudget(PeerId peer, int bytesThisFrame, MotionQuality quality);
    }
}
