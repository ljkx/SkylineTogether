namespace SkylineTogether.Core
{
    /// <summary>Host engine statistics for the network panel and the diagnostics dump.</summary>
    public struct HostEngineStats
    {
        public int Slots;
        public long HashShadowBytes;
        public long ValueShadowBytes;
        public long EpochLogBytes;
        public int EpochsPerSecond;
        public int P0BytesPerSecond;
        /// <summary>Engine calls waiting for the next capture window.</summary>
        public int QueuedCommands;
        /// <summary>Bound slots whose SPAWN was not emitted yet.</summary>
        public int PendingSpawns;
        public int EpochPartsLastSecond;
    }
}
