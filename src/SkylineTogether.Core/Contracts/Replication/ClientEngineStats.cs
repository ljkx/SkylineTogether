namespace SkylineTogether.Core
{
    /// <summary>Client engine statistics for the network panel and the diagnostics dump.</summary>
    public struct ClientEngineStats
    {
        public EpochId LastApplied;
        public int BacklogEpochs;
        public long BacklogBytes;
        /// <summary>Values parked for missing references.</summary>
        public int PendingRecords;
        /// <summary>Values held for pending proposals.</summary>
        public int HeldRecords;
        /// <summary>Epochs whose parts are still being assembled.</summary>
        public int PartialEpochs;
        public long PartialEpochBytes;
        /// <summary>Entries in the type-set registry.</summary>
        public int TypeSets;
    }
}
