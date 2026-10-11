namespace SkylineTogether.Core
{
    /// <summary>
    /// One inspection-closure sample (ARCH §4.7.9): its values are <see cref="ApplyPlan.InspectionValues"/>[
    /// <see cref="FirstValue"/> ... <see cref="FirstValue"/> + <see cref="ValueCount"/> - 1], applied when the client
    /// clock reaches <see cref="SampleTick"/>.
    /// </summary>
    public struct InspectionBatch
    {
        public uint SampleTick;
        public int FirstValue;
        public int ValueCount;
    }
}
