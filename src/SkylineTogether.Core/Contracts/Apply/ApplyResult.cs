namespace SkylineTogether.Core
{
    /// <summary>
    /// Outcome of <see cref="IWorldApplier.Execute"/>. On an exception the executor marks every entity the program
    /// created <c>Deleted</c> and reports <see cref="Faulted"/> with the category and a detail (ARCH §4.7.2, §12.2).
    /// </summary>
    public struct ApplyResult
    {
        /// <summary>Operations applied.</summary>
        public int Applied;
        /// <summary>Values parked for a missing reference.</summary>
        public int Parked;
        /// <summary>Operations that failed without faulting the program (for example a resolution check that failed).</summary>
        public int Failed;
        /// <summary>Bound entities replaced because generation, family or prefab differed (ARCH §4.7.3).</summary>
        public int Replaced;
        public bool Faulted;
        public FaultCategory FaultCategory;
        public string FaultDetail;

        /// <summary>A faulted result (convenience for executors).</summary>
        public static ApplyResult Fault(FaultCategory category, string detail)
        {
            return new ApplyResult { Faulted = true, FaultCategory = category, FaultDetail = detail };
        }
    }
}
