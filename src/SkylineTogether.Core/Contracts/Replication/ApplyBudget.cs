namespace SkylineTogether.Core
{
    /// <summary>
    /// How much apply work a client takes per frame (ARCH §4.7.10): 3 ms live; 6 ms or 200 epochs during catch-up.
    /// A transaction is never split across frames, even when it arrived as several epoch parts.
    /// </summary>
    public struct ApplyBudget
    {
        public double MaxMilliseconds;
        public int MaxEpochs;
        public bool CatchUp;
    }
}
